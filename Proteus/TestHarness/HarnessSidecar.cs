using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>One mod's sidecar put back: restored / unchanged / changed / failed, and what became of a defaults snapshot
/// the harness's save created (deleted / kept / none).</summary>
internal sealed record SidecarUndo(string Mod, string Result, string? Code, string Defaults);

/// <summary>A ledger an earlier session left without putting it back (a crash, or a put-back that left the file).</summary>
/// <param name="Backup">The backup folder, relative to the plugin's config folder (never absolute: it holds the profile name).</param>
internal sealed record LeftoverSidecar(string Session, string Mod, string Backup, bool FileMatchesHarnessWrite);

/// <summary>
/// The colour-row edits the harness saved into Proteus mods' sidecars (<c>Proteus/metadata.json</c>) this session, and how
/// to put them back (ADR 0002, 2026-11-07 amendment).
/// <list type="bullet">
/// <item>Before a mod's first edit its <c>metadata.json</c> is backed up, and the ledger written, under
/// <c>test-harness/backups/&lt;session&gt;/sidecar/&lt;mod&gt;/</c>: on disk before the save.</item>
/// <item>A mod with an edit the user has not saved yet is refused: the harness never saves over, discards or races it.</item>
/// <item>Put-back restores a file only while it is still what the harness wrote; otherwise it reports <c>changed</c> and
/// keeps the backup. A defaults snapshot is deleted only when the harness's save created it, it is unchanged, and the file
/// was restored.</item>
/// <item>One lock covers an edit (stopping check, backup, save, hash) and each restore, so a put-back waits for an edit in
/// flight and an edit that finds the harness stopping saves nothing.</item>
/// </list>
/// Any thread: the edit runs on a worker, put-back on the framework thread or wherever the harness stops.
/// </summary>
internal sealed class HarnessSidecar(string backupsRoot, string session, IPluginLog? log)
{
    internal const string Pending = "pending", Done = "done", Left = "left";
    private const string RecordFile = "record.json", BackupFile = "metadata.json";

    private sealed class Entry
    {
        public required string Mod { get; init; }
        public required string MetaPath { get; init; }
        public required string Dir { get; init; }
        public required string BackupSha { get; init; }
        public required bool DefaultsExisted { get; init; }
        public string? LastWrittenSha { get; set; }
        public int Edits { get; set; }
        public DateTime AtUtc { get; set; }
        public string State { get; set; } = Pending;
    }

    /// <summary>The ledger on disk.</summary>
    private sealed record Record(string Session, string Mod, string MetaPath, string BackupSha, bool DefaultsExisted,
                                 string? LastWrittenSha, int Edits, string State, DateTime AtUtc);

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool stopping;

    /// <summary>From now on an edit saves nothing (503); put-back still runs. Takes the lock, so an edit in flight finishes first.</summary>
    public void BeginStop()
    {
        stopping = true;
        lock (gate) { }
    }

    public bool Empty { get { lock (gate) return entries.Count == 0; } }

    /// <summary>Whether the calling thread holds the lock: no framework hop may happen while it does (tests assert it).</summary>
    internal bool LockHeldByCaller => System.Threading.Monitor.IsEntered(gate);

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string? ShaOf(string path) => File.Exists(path) ? Sha(File.ReadAllBytes(path)) : null;

    private string DirFor(string mod) => Path.Combine(backupsRoot, session, "sidecar", mod);

    /// <summary>The defaults snapshot <see cref="SidecarDiscoveryService.SaveMetadata"/> may create beside the file.</summary>
    internal static string DefaultsPath(string metaPath) => Path.Combine(Path.GetDirectoryName(metaPath)!, "metadata.default.json");

    /// <summary>
    /// Save one edit of <paramref name="mod"/>'s sidecar at <paramref name="metaPath"/> through <paramref name="save"/>,
    /// backing the file up first on the mod's first edit this session. Worker thread.
    /// </summary>
    /// <param name="hasStaged">Whether the user holds an unsaved edit of this mod (asked under the lock).</param>
    /// <param name="save">Applies the change and saves it (SaveMetadata); false when the save failed.</param>
    public ChangeResult Edit(string mod, string metaPath, Func<bool> hasStaged, Func<bool> save, Func<object?, object> answer)
    {
        lock (gate)
        {
            if (stopping) return ChangeResult.Fail(503, "harness-stopping", "the test harness is stopping");
            if (hasStaged())
                return ChangeResult.Fail(409, "user-edit-held",
                                         $"{mod} has a colour edit that is not saved yet; the harness never saves over it");
            if (!File.Exists(metaPath)) return ChangeResult.Fail(404, "not-a-proteus-mod", $"{mod} has no Proteus sidecar");

            if (!entries.TryGetValue(mod, out var entry))
            {
                var bytes = File.ReadAllBytes(metaPath);
                var dir = DirFor(mod);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, BackupFile), bytes);
                entry = new Entry
                {
                    Mod = mod, MetaPath = metaPath, Dir = dir, BackupSha = Sha(bytes),
                    DefaultsExisted = File.Exists(DefaultsPath(metaPath)), AtUtc = DateTime.UtcNow,
                };
                WriteRecord(entry);   // before the save: a crash after it still leaves a ledger to find
                entries[mod] = entry;
            }

            if (!save())
            {
                log?.Warning("[Proteus] test harness: the sidecar save of {0} failed; its backup is kept", mod);
                return ChangeResult.Fail(500, "save-failed", $"the sidecar save of {mod} failed; the backup is kept");
            }
            entry.LastWrittenSha = ShaOf(metaPath);
            entry.Edits++;
            entry.AtUtc = DateTime.UtcNow;
            WriteRecord(entry);
            log?.Information("[Proteus] test harness: saved a colour edit into {0}'s sidecar (edit {1})", mod, entry.Edits);
            return ChangeResult.Ok(answer(Relative(entry.Dir)));
        }
    }

    /// <summary>
    /// Put every recorded sidecar back, each under the lock. <paramref name="hasStaged"/> and <paramref name="forget"/>
    /// must be safe on any thread. Only failures stay recorded.
    /// </summary>
    public List<SidecarUndo> PutBack(Func<string, bool> hasStaged, Action<string> forget)
    {
        var results = new List<SidecarUndo>();
        lock (gate)
        {
            foreach (var entry in entries.Values.ToList())
            {
                SidecarUndo undo;
                try { undo = Undo(entry, hasStaged); }
                catch (Exception ex)
                {
                    log?.Warning(ex, "[Proteus] test harness: putting back {0}'s sidecar failed; backup at {1}", entry.Mod,
                                 Relative(entry.Dir));
                    undo = new SidecarUndo(entry.Mod, "failed", "internal", "none");
                }
                try { forget(entry.Mod); } catch (Exception ex) { log?.Debug("[Proteus] test harness: forget failed: {0}", ex.Message); }
                if (undo.Result != "failed")
                {
                    entry.State = undo.Result is "restored" or "unchanged" ? Done : Left;
                    try { WriteRecord(entry); }
                    catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: could not mark {0}'s ledger", entry.Mod); }
                    entries.Remove(entry.Mod);
                }
                results.Add(undo);
            }
        }
        return results;
    }

    private SidecarUndo Undo(Entry entry, Func<string, bool> hasStaged)
    {
        // The user is editing it now: their flush must win.
        if (hasStaged(entry.Mod)) return new SidecarUndo(entry.Mod, "changed", "user-edit-held", DefaultsLeft(entry));
        var now = ShaOf(entry.MetaPath);
        if (now == entry.BackupSha) return new SidecarUndo(entry.Mod, "unchanged", null, DefaultsAfter(entry, restored: false));
        if (now == null || now != entry.LastWrittenSha)
            return new SidecarUndo(entry.Mod, "changed", now == null ? "gone" : null, DefaultsLeft(entry));

        PenumbraModMeta.AtomicWrite(entry.MetaPath, File.ReadAllBytes(Path.Combine(entry.Dir, BackupFile)), maxRetries: 2);
        log?.Information("[Proteus] test harness: put {0}'s sidecar back", entry.Mod);
        return new SidecarUndo(entry.Mod, "restored", null, DefaultsAfter(entry, restored: true));
    }

    /// <summary>A defaults snapshot the harness's save created: deleted only when the file was restored and it is unchanged.</summary>
    private string DefaultsAfter(Entry entry, bool restored)
    {
        var path = DefaultsPath(entry.MetaPath);
        if (entry.DefaultsExisted || !File.Exists(path)) return "none";
        if (restored && ShaOf(path) == entry.BackupSha)
        {
            File.Delete(path);
            return "deleted";
        }
        return "kept";
    }

    private static string DefaultsLeft(Entry entry)
        => !entry.DefaultsExisted && File.Exists(DefaultsPath(entry.MetaPath)) ? "kept" : "none";

    private void WriteRecord(Entry e)
    {
        var record = new Record(session, e.Mod, e.MetaPath, e.BackupSha, e.DefaultsExisted, e.LastWrittenSha, e.Edits, e.State, e.AtUtc);
        PenumbraModMeta.AtomicWrite(Path.Combine(e.Dir, RecordFile), JsonSerializer.Serialize(record), maxRetries: 2);
    }

    /// <summary>A backup folder relative to the plugin's config folder: never the absolute path, which holds the profile name.</summary>
    private string Relative(string dir) => RelativeTo(backupsRoot, dir);

    private static string RelativeTo(string backupsRoot, string dir)
        => Path.Combine("test-harness", "backups", Path.GetRelativePath(backupsRoot, dir)).Replace('\\', '/');

    /// <summary>What <c>/state.harnessChanges.sidecar</c> shows.</summary>
    public object View()
    {
        lock (gate)
            return entries.Values.Select(e => new { mod = e.Mod, backup = Relative(e.Dir), edits = e.Edits, atUtc = e.AtUtc }).ToList();
    }

    /// <summary>The mods still recorded, for the warning when put-back left them.</summary>
    public IEnumerable<string> Leftovers()
    {
        lock (gate) return entries.Values.Select(e => $"{e.Mod}'s sidecar (backup {Relative(e.Dir)})").ToList();
    }

    /// <summary>
    /// Ledgers of other sessions that are not <see cref="Done"/>: a crash, or a put-back that left a changed file. Restores
    /// nothing. Never throws.
    /// </summary>
    internal static List<LeftoverSidecar> ScanLeftovers(string backupsRoot, string currentSession, IPluginLog? log = null)
    {
        var found = new List<LeftoverSidecar>();
        try
        {
            if (!Directory.Exists(backupsRoot)) return found;
            foreach (var sessionDir in Directory.EnumerateDirectories(backupsRoot))
            {
                if (string.Equals(Path.GetFileName(sessionDir), currentSession, StringComparison.OrdinalIgnoreCase)) continue;
                var sidecars = Path.Combine(sessionDir, "sidecar");
                if (!Directory.Exists(sidecars)) continue;
                foreach (var modDir in Directory.EnumerateDirectories(sidecars))
                {
                    try
                    {
                        var path = Path.Combine(modDir, RecordFile);
                        if (!File.Exists(path)) continue;
                        var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(path));
                        if (r == null || r.State == Done) continue;
                        bool matches = r.LastWrittenSha != null && ShaOf(r.MetaPath) == r.LastWrittenSha;
                        found.Add(new LeftoverSidecar(r.Session, r.Mod, RelativeTo(backupsRoot, modDir), matches));
                    }
                    catch (Exception ex) { log?.Debug("[Proteus] test harness: unreadable sidecar ledger in {0}: {1}", Path.GetFileName(modDir), ex.Message); }
                }
            }
        }
        catch (Exception ex) { log?.Debug("[Proteus] test harness: scanning for leftover sidecar ledgers failed: {0}", ex.Message); }
        return found;
    }
}
