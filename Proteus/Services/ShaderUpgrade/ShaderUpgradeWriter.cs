using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Proteus.Services.ShaderUpgradePlan;

namespace Proteus.Services;

/// <summary>
/// The gear shader upgrade's writes into a worn mod, and their undo. SENSITIVE ("Writes into users' Penumbra mods"):
/// <list type="bullet">
/// <item>each material is backed up once, inside the mod (<c>Proteus/shaderupgrade-backup/</c>), before its first
/// change — with <c>File.Copy</c>, which keeps the source's write time that <see cref="ModelBackupOrder"/> reads;</item>
/// <item>a record (<c>Proteus/shaderupgrade.json</c>) names every file, mask and redirect, written BEFORE the mask,
/// the redirects and the material, so a crash at any later step leaves something undo can clean;</item>
/// <item>Form B's derived mask is a new file under <c>Shader Upgrade/</c>, published by redirects added inside the
/// options (or default data) that already ship the material — no group is ever added, moved or reordered;</item>
/// <item>v3 folders and Proteus sidecar packs are never written;</item>
/// <item>undo restores only a file still as Proteus left it: an author's update is kept, never overwritten by an old
/// backup, whether met by undo or by the next upgrade. Where that update can't be told from an edit of our output (an
/// index/mask upgrade names no mask of ours), undo archives the original in
/// <c>Proteus/shaderupgrade-backup-superseded/</c> instead of deleting it; a file it can't read is left alone.</item>
/// </list>
/// Writes run under <see cref="ModManifestLock.Gate"/> then <see cref="MeshVolumeService.WriteLock"/> (the brush never
/// takes the Gate, so the two cannot deadlock).
/// </summary>
internal static class ShaderUpgradeWriter
{
    public const string RecordFile = "shaderupgrade.json";
    public const string BackupSubdir = "shaderupgrade-backup";

    /// <summary>Where undo archives an original it can no longer restore (see <see cref="RetireBackup"/>).</summary>
    public const string SupersededSubdir = "shaderupgrade-backup-superseded";

    /// <summary>Bumped when what an upgrade writes changes for the same input.</summary>
    public const int Version = 1;

    // ── the record ──────────────────────────────────────────────────────────

    internal sealed class Record
    {
        public List<FileEntry> Files { get; set; } = [];
        public List<string> Declined { get; set; } = [];
    }

    internal sealed class FileEntry
    {
        public string Rel { get; set; } = "";
        public string Form { get; set; } = "index-mask";
        public string SourceSha8 { get; set; } = "";
        public string UpgradedSha8 { get; set; } = "";
        public MaskEntry? Mask { get; set; }
        public int Version { get; set; }
        public bool Pending { get; set; }
    }

    internal sealed class MaskEntry
    {
        public string Rel { get; set; } = "";
        public string GamePath { get; set; } = "";
        public List<OwnerRef> Owners { get; set; } = [];
    }

    internal sealed class OwnerRef
    {
        public string Group { get; set; } = "";
        public string Option { get; set; } = "";
    }

    private static string ProteusDir(string modRoot) => Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
    private static string RecordPath(string modRoot) => Path.Combine(ProteusDir(modRoot), RecordFile);
    private static string BackupPath(string modRoot, string rel) => Path.Combine(ProteusDir(modRoot), BackupSubdir, Native(rel));
    private static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);
    private static string FileOf(string modRoot, string rel) => Path.Combine(modRoot, Native(rel));

    internal static Record? ReadRecord(string modRoot)
    {
        try
        {
            var at = RecordPath(modRoot);
            if (!File.Exists(at)) return null;
            var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(at));
            if (r == null) return null;
            foreach (var f in r.Files) f.Rel = MeshVolumeService.Rel(f.Rel);
            r.Declined = r.Declined.Select(MeshVolumeService.Rel).ToList();
            return r;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void WriteRecord(string modRoot, Record r)
    {
        var at = RecordPath(modRoot);
        if (r.Files.Count == 0 && r.Declined.Count == 0)
        {
            if (File.Exists(at)) File.Delete(at);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        PenumbraModMeta.AtomicWrite(at, JsonSerializer.Serialize(r, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }

    private static FileEntry? EntryOf(Record r, string rel)
        => r.Files.FirstOrDefault(f => string.Equals(f.Rel, MeshVolumeService.Rel(rel), StringComparison.OrdinalIgnoreCase));

    public static bool IsDeclined(string modRoot, string rel)
        => ReadRecord(modRoot)?.Declined.Contains(MeshVolumeService.Rel(rel), StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether Proteus's upgrade of this file is recorded and the file is still Proteus's output: as it left it, or
    /// edited since (see <see cref="EditedAfterUpgrade"/>).
    /// </summary>
    public static bool IsUpgraded(string modRoot, string rel)
    {
        if (ReadRecord(modRoot) is not { } r || EntryOf(r, rel) is not { } e) return false;
        try
        {
            var bytes = File.ReadAllBytes(FileOf(modRoot, e.Rel));
            return Sha8(bytes) == e.UpgradedSha8 || EditedAfterUpgrade(bytes, e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The file is not the bytes Proteus wrote, yet still names the mask Proteus derived for it: someone edited the
    /// upgraded material (a material editor, a colour change). It is built on our output, so it is neither the author's
    /// update (which cannot know our mask's name) nor something the backup may overwrite. An index/mask upgrade names
    /// no mask of ours and cannot be told from an author's Dawntrail update, so it is never "edited".
    /// </summary>
    internal static bool EditedAfterUpgrade(byte[]? current, FileEntry e)
        => current is { Length: > 0 } && e.Mask is { } m && Sha8(current) != e.UpgradedSha8
           && TexturePaths(current) is { } paths
           && paths.Any(p => string.Equals(p, m.GamePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The refusal an undo returns for a file <see cref="EditedAfterUpgrade"/>.</summary>
    internal static string EditedMessage(string rel)
        => string.Format(ProteusLoc.Localize("Settings.GearShaders.Edited.Fmt",
            "{0} was edited after its shader upgrade, so Undo leaves it as it is. The author's original is kept in "
          + "Proteus/shaderupgrade-backup/{0}: copy it back over the file to undo."), rel);

    // ── apply ───────────────────────────────────────────────────────────────

    internal sealed record Outcome(List<string> Written, List<(string Rel, LeftReason Reason)> Left, bool Rewalk)
    {
        public bool ChangedAny => Written.Count > 0;
    }

    /// <summary>
    /// Test seam: a stop injected after a named step (pending, mask, redirects, material). Per thread, so tests running
    /// in parallel never see each other's.
    /// </summary>
    [ThreadStatic] internal static Action<string>? AfterStep;

    /// <summary>
    /// Upgrade a mod's materials. The masks are encoded first, OUTSIDE the locks (the brush saves under the same write lock
    /// on the framework thread, and an encode can take a while). Then per file, under the locks: re-check the file is
    /// still the one planned; forget an entry an author's update has left behind; back up; record (pending); place the
    /// mask and add its redirects; write the material; record (done). <paramref name="mayWrite"/> is asked before every
    /// file.
    /// </summary>
    /// <param name="writeTex"><see cref="TextureLoader.WriteTex(byte[], int, int, string, TexEncoding)"/>.</param>
    public static Outcome Apply(string modRoot, IReadOnlyList<MaterialWrite> writes, Func<bool> mayWrite,
                                Func<byte[], int, int, string, TexEncoding, bool> writeTex, TexEncoding encoding)
    {
        var written = new List<string>();
        var left = new List<(string, LeftReason)>();
        bool rewalk = false;
        if (!Directory.Exists(modRoot))
            return new Outcome(written, writes.Select(w => (w.Rel, LeftReason.WriteFailed)).ToList(), true);
        lock (ModManifestLock.Gate)
        {
            if (PenumbraModMeta.IsLegacyFolder(modRoot) || HairPortPlan.ReadOwners(modRoot) == null)
                return new Outcome(written, writes.Select(w => (w.Rel, LeftReason.LegacyMod)).ToList(), false);
            if (IsProteusPack(modRoot))
                return new Outcome(written, writes.Select(w => (w.Rel, LeftReason.ProteusPack)).ToList(), false);
        }

        // Masks this batch names: an author-update forget must not delete one a write here is about to reuse.
        var keep = writes.Where(w => w.Mask != null).Select(w => MeshVolumeService.Rel(w.Mask!.Rel))
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prepared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // mask rel → encoded temp file
        try
        {
            foreach (var w in writes)
            {
                if (!mayWrite()) break;
                string? tmp = null;
                if (w.Mask is { Rgba: not null } m && !prepared.ContainsKey(m.Rel))
                {
                    try
                    {
                        tmp = PrepareMask(modRoot, m, encoding, writeTex);
                        if (tmp != null) prepared[m.Rel] = tmp;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WriteRefused)
                    {
                        left.Add((w.Rel, LeftReason.WriteFailed));
                        continue;
                    }
                }

                lock (ModManifestLock.Gate)
                lock (MeshVolumeService.WriteLock)
                {
                    if (!mayWrite()) break;
                    WriteOne(modRoot, w, prepared, keep, written, left, ref rewalk);
                }
            }
        }
        finally
        {
            foreach (var tmp in prepared.Values) TryDelete(tmp);
            // A folder made for masks that never landed (the unit was skipped) is not left in the author's mod.
            var dir = Path.Combine(modRoot, MaskFolder);
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (IOException) { /* harmless */ }
        }
        return new Outcome(written, left, rewalk);
    }

    private static void WriteOne(string modRoot, MaterialWrite w, Dictionary<string, string> prepared, ISet<string> keep,
                                 List<string> written, List<(string, LeftReason)> left, ref bool rewalk)
    {
        var record = ReadRecord(modRoot) ?? new Record();
        if (record.Declined.Contains(w.Rel, StringComparer.OrdinalIgnoreCase)) { left.Add((w.Rel, LeftReason.Declined)); return; }

        var path = FileOf(modRoot, w.Rel);
        byte[] current;
        try { current = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left.Add((w.Rel, LeftReason.WriteFailed));
            rewalk = true;
            return;
        }
        var sha = Sha8(current);
        if (sha != w.SourceSha8) { rewalk = true; return; }   // changed since planning: look again

        try
        {
            // An entry whose file matches neither hash is an author's update: never restore over it. Its backup must go
            // (a failure aborts the unit), or the old original would stand in for the author's new file.
            if (EntryOf(record, w.Rel) is { } stale && sha != stale.SourceSha8 && sha != stale.UpgradedSha8)
            {
                // Still naming our mask (our output with its shader set back by hand): not the author's update. Its
                // backup is the only original left, so the unit is left alone.
                if (EditedAfterUpgrade(current, stale)) { left.Add((w.Rel, LeftReason.WriteFailed)); return; }
                Forget(modRoot, record, stale, keep, strictBackup: true);
                WriteRecord(modRoot, record);
            }

            var entry = EntryOf(record, w.Rel);
            // No entry: any backup on disk is an orphan (a write that failed before its record), never this file's original.
            Backup(modRoot, w.Rel, replace: entry == null);
            if (entry == null) record.Files.Add(entry = new FileEntry { Rel = w.Rel });
            else if (entry.Mask is { } oldMask && (w.Mask == null
                     || !string.Equals(oldMask.Rel, w.Mask.Rel, StringComparison.OrdinalIgnoreCase)))
            {
                // A pending entry reused after a crash, now planned with another mask: take the old one out first.
                RemoveMaskRedirects(modRoot, oldMask);
                entry.Mask = null;
                DeleteUnnamedMask(modRoot, oldMask.Rel, record, keep);
            }
            entry.Form = w.Form == LegacyForm.Diffuse ? "diffuse" : "index-mask";
            entry.SourceSha8 = w.SourceSha8;
            entry.UpgradedSha8 = Sha8(w.Bytes);
            entry.Version = Version;
            entry.Pending = true;
            entry.Mask = w.Mask is { } m
                ? new MaskEntry
                {
                    Rel = m.Rel, GamePath = m.GamePath,
                    Owners = m.Owners.Select(o => new OwnerRef { Group = o.Group, Option = o.Option }).ToList(),
                }
                : null;
            WriteRecord(modRoot, record);
            AfterStep?.Invoke("pending");

            if (w.Mask is { } mask)
            {
                PlaceMask(modRoot, mask, prepared.TryGetValue(mask.Rel, out var tmp) ? tmp : null);
                AfterStep?.Invoke("mask");
                foreach (var (group, option) in mask.Owners)
                    PenumbraModMeta.MergeOptionRedirects(modRoot, group, option,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [mask.GamePath] = Native(mask.Rel) });
                AfterStep?.Invoke("redirects");
            }

            PenumbraModMeta.AtomicWrite(path, w.Bytes);
            AfterStep?.Invoke("material");
            entry.Pending = false;
            WriteRecord(modRoot, record);
            written.Add(w.Rel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                         or ArgumentException or JsonException or WriteRefused)
        {
            // The material write is atomic, so the file is as it was, or (a failure after it) our output; take out what
            // this unit added only when the file is readable and NOT our output. A file that can't be read (locked,
            // missing, empty) is an unknown state: the entry and its backup are kept, and the next walk looks again. An
            // already-upgraded file is never planned again, so its entry may stay Pending — harmless: undo works from
            // the hashes.
            var r2 = ReadRecord(modRoot) ?? record;
            if (EntryOf(r2, w.Rel) is { } e)
            {
                if (SafeRead(path) is not { Length: > 0 } now) rewalk = true;   // unreadable, missing or empty: unknown
                else if (Sha8(now) != e.UpgradedSha8)
                {
                    Forget(modRoot, r2, e, keep: null, strictBackup: false);
                    TryWriteRecord(modRoot, r2);
                }
            }
            left.Add((w.Rel, LeftReason.WriteFailed));
            // A mask the plan meant to reuse was gone: the next walk derives it again.
            if (ex is MaskMissing) rewalk = true;
        }
    }

    /// <summary>
    /// Copy the author's file aside, ONCE: a second pass must not overwrite the pristine copy. With
    /// <paramref name="replace"/> (no record entry claims a backup), one already there is an orphan and is replaced.
    /// <c>File.Copy</c> keeps the source's write time, which <see cref="ModelBackupOrder"/> reads.
    /// </summary>
    private static void Backup(string modRoot, string rel, bool replace)
    {
        var backup = BackupPath(modRoot, rel);
        if (File.Exists(backup))
        {
            if (!replace) return;
            File.Delete(backup);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(FileOf(modRoot, rel), backup);
    }

    /// <summary>A write refused by the mask gate.</summary>
    internal class WriteRefused(string message) : Exception(message);

    /// <summary>A mask the plan meant to reuse is no longer in the mod.</summary>
    internal sealed class MaskMissing(string message) : WriteRefused(message);

    private static string MaskPath(string modRoot, MaskWrite mask)
    {
        var path = FileOf(modRoot, mask.Rel);
        var dir = Path.GetFullPath(Path.Combine(modRoot, MaskFolder));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), dir, StringComparison.OrdinalIgnoreCase))
            throw new WriteRefused($"\"{mask.Rel}\" is outside {MaskFolder}/");
        return path;
    }

    /// <summary>
    /// Encode a mask to a temporary file beside its final name and check its header — no lock held. Null when a complete
    /// file of that name (same content address) is already there.
    /// </summary>
    internal static string? PrepareMask(string modRoot, MaskWrite mask, TexEncoding encoding,
                                        Func<byte[], int, int, string, TexEncoding, bool> writeTex)
    {
        var path = MaskPath(modRoot, mask);
        if (TextureLoader.IsCompleteTex(path) || mask.Rgba == null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Path.GetRandomFileName() + ".part";
        try
        {
            if (!writeTex(mask.Rgba, mask.Width, mask.Height, tmp, encoding))
                throw new WriteRefused($"{mask.Rel} could not be encoded");
            var header = new byte[16];
            long length;
            using (var fs = File.OpenRead(tmp))
            {
                length = fs.Length;
                if (fs.Read(header, 0, header.Length) != header.Length) throw new WriteRefused($"{mask.Rel} is short");
            }
            if (!IsSoundMaskHeader(header, length, mask.Width, mask.Height) || !TextureLoader.IsCompleteTex(tmp))
                throw new WriteRefused($"{mask.Rel} failed its header check");
            return tmp;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Move a prepared mask to its final name (under the locks). A complete file already there is reused.</summary>
    private static string PlaceMask(string modRoot, MaskWrite mask, string? prepared)
    {
        var path = MaskPath(modRoot, mask);
        if (TextureLoader.IsCompleteTex(path)) return path;
        if (prepared == null || !File.Exists(prepared)) throw new MaskMissing($"{mask.Rel} is missing and has no pixels to write");
        try { File.Move(prepared, path, overwrite: false); }
        catch (IOException) when (TextureLoader.IsCompleteTex(path)) { /* the same mask, placed meanwhile */ }
        return path;
    }

    /// <summary>Prepare and place in one step (no lock): for tests and single writes.</summary>
    internal static string WriteMask(string modRoot, MaskWrite mask, TexEncoding encoding,
                                     Func<byte[], int, int, string, TexEncoding, bool> writeTex)
    {
        string? tmp = null;
        try
        {
            tmp = PrepareMask(modRoot, mask, encoding, writeTex);
            return PlaceMask(modRoot, mask, tmp);
        }
        finally { if (tmp != null) TryDelete(tmp); }
    }

    // ── undo ────────────────────────────────────────────────────────────────

    internal enum Undone { Nothing, Restored, Forgotten, Refused, Failed }

    /// <summary>
    /// Put one file back the way its author shipped it. Refused while a later feature (the brush) changed it after us,
    /// and for a file edited after the upgrade (its edits and our backup, mask and redirects are all kept; it is still
    /// declined when <paramref name="decline"/> is set).
    /// The backup is restored only over a file still as Proteus left it; an author's update is kept. Either way our
    /// mask redirects (only while they still name our mask) and unnamed masks go, the entry is dropped, and the file
    /// is declined when <paramref name="decline"/> is set. A kept file whose entry has no mask (so an edit can't be
    /// told from the author's update) has its original archived, not deleted; <paramref name="message"/> then names
    /// the archive. A file that exists but can't be read fails the undo and forgets nothing.
    /// </summary>
    public static Undone RevertOrForget(string modRoot, string rel, bool decline, out string message)
    {
        message = "";
        rel = MeshVolumeService.Rel(rel);
        lock (ModManifestLock.Gate)
        lock (MeshVolumeService.WriteLock)
        {
            var record = ReadRecord(modRoot);
            if (record == null || EntryOf(record, rel) is not { } entry)
            {
                if (decline && record != null && !record.Declined.Contains(rel, StringComparer.OrdinalIgnoreCase))
                {
                    record.Declined.Add(rel);
                    TryWriteRecord(modRoot, record);
                }
                return Undone.Nothing;
            }
            if (ModelBackupOrder.LaterFeature(modRoot, BackupSubdir, rel) is { } later)
            {
                message = ModelBackupOrder.BlockedMessage(later);
                return Undone.Refused;
            }

            var path = FileOf(modRoot, rel);
            var current = SafeRead(path);
            if (current is null or { Length: 0 } && File.Exists(path))
            {
                // There but unreadable (in use), or empty (a save still being written): its hash says nothing, and an
                // unknown file must not be taken for the author's update (that would drop the only original). Nothing
                // is forgotten; the next undo tries again.
                message = $"{rel} could not be read or is empty; undo left it as it is — try again";
                return Undone.Failed;
            }
            if (EditedAfterUpgrade(current, entry))
            {
                // Declined all the same: the original copied back by hand (as the message says) is not upgraded again.
                if (decline && !record.Declined.Contains(rel, StringComparer.OrdinalIgnoreCase))
                {
                    record.Declined.Add(rel);
                    TryWriteRecord(modRoot, record);
                }
                message = EditedMessage(rel);
                return Undone.Refused;
            }
            var sha = Sha8(current ?? []);
            Undone result;
            try
            {
                if (sha == entry.UpgradedSha8)
                {
                    var backup = BackupPath(modRoot, rel);
                    if (!File.Exists(backup)) { message = rel; return Undone.Failed; }
                    PenumbraModMeta.AtomicWrite(path, File.ReadAllBytes(backup));
                    result = Undone.Restored;
                }
                else result = sha == entry.SourceSha8 ? Undone.Nothing : Undone.Forgotten;

                // With no mask of ours, an edit after the upgrade can't be told from the author's update
                // (EditedAfterUpgrade needs the mask): the file is kept and the entry forgotten all the same, but the
                // author's original is archived, never deleted.
                var retire = result == Undone.Forgotten && entry.Mask is null;
                var archived = Forget(modRoot, record, entry, retire: retire);
                if (archived != null)
                    message = $"kept the author's original of {rel} in {SidecarDiscoveryService.SidecarSubdir}/{SupersededSubdir}/{archived}";
                if (decline && !record.Declined.Contains(rel, StringComparer.OrdinalIgnoreCase)) record.Declined.Add(rel);
                WriteRecord(modRoot, record);
                CleanFolders(modRoot, record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                             or ArgumentException or JsonException)
            {
                message = ex.Message;
                return Undone.Failed;
            }
            return result;
        }
    }

    /// <summary>
    /// Drop an entry: its backup first, then its mask redirects (only while they still name our mask), then the mask
    /// file if nothing names it any more (nor <paramref name="keep"/>). The caller writes the record.
    /// </summary>
    /// <param name="strictBackup">Throw when the backup cannot be deleted (a stale original must never survive).</param>
    /// <param name="retire">Move the backup to <see cref="SupersededSubdir"/> instead of deleting it (see
    /// <see cref="RetireBackup"/>).</param>
    /// <returns>The archived file's path under <see cref="SupersededSubdir"/>, when one was archived.</returns>
    internal static string? Forget(string modRoot, Record record, FileEntry entry, ISet<string>? keep = null,
                                   bool strictBackup = false, bool retire = false)
    {
        var backup = BackupPath(modRoot, entry.Rel);
        string? archived = null;
        if (retire) archived = RetireBackup(modRoot, entry.Rel);
        else if (strictBackup) { if (File.Exists(backup)) File.Delete(backup); }
        else TryDelete(backup);
        record.Files.Remove(entry);
        if (entry.Mask is { } m)
        {
            RemoveMaskRedirects(modRoot, m);
            DeleteUnnamedMask(modRoot, m.Rel, record, keep);
        }
        return archived;
    }

    /// <summary>
    /// Move a backup out of the restore path into <c>Proteus/shaderupgrade-backup-superseded/&lt;rel&gt;</c> (a
    /// timestamped name when that one is taken). Nothing reads it back: it is the author's original kept for the user,
    /// outside every feature's backup folder (<see cref="ModelBackupOrder"/> lists those by name). A missing backup is
    /// already retired (an earlier attempt moved it, then its record write failed): no-op, null. A failed move of an
    /// existing backup throws, so the caller forgets nothing.
    /// </summary>
    internal static string? RetireBackup(string modRoot, string rel)
    {
        var backup = BackupPath(modRoot, rel);
        if (!File.Exists(backup)) return null;
        var native = Native(MeshVolumeService.Rel(rel));
        var root = Path.Combine(ProteusDir(modRoot), SupersededSubdir);
        var target = Path.Combine(root, native);
        if (File.Exists(target) || Directory.Exists(target))
        {
            var stamped = Path.Combine(Path.GetDirectoryName(native) ?? "",
                Path.GetFileNameWithoutExtension(native) + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(native));
            target = Path.Combine(root, stamped);
            for (int n = 2; File.Exists(target) || Directory.Exists(target); n++)
                target = Path.Combine(root, Path.ChangeExtension(stamped, null) + "-" + n + Path.GetExtension(native));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(backup, target, overwrite: false);
        return Path.GetRelativePath(root, target).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void RemoveMaskRedirects(string modRoot, MaskEntry m)
    {
        foreach (var o in m.Owners)
        {
            try
            {
                PenumbraModMeta.RemoveOptionRedirects(modRoot, o.Group, o.Option,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [m.GamePath] = Native(m.Rel) });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                                             or KeyNotFoundException)
            {
                /* the option is gone: nothing of ours left in it */
            }
        }
    }

    /// <summary>Delete a mask under <c>Shader Upgrade/</c> that no redirect, no other entry and nothing in <paramref name="keep"/> names.</summary>
    private static void DeleteUnnamedMask(string modRoot, string maskRel, Record record, ISet<string>? keep = null)
    {
        if (keep?.Contains(MeshVolumeService.Rel(maskRel)) == true) return;
        if (record.Files.Any(f => f.Mask != null && string.Equals(f.Mask.Rel, maskRel, StringComparison.OrdinalIgnoreCase)))
            return;
        var named = PenumbraModMeta.ReadAllRedirects(modRoot)
                                   .Any(r => string.Equals(MeshVolumeService.Rel(r.File), MeshVolumeService.Rel(maskRel),
                                                           StringComparison.OrdinalIgnoreCase));
        if (named) return;
        TryDelete(FileOf(modRoot, maskRel));
        var dir = Path.Combine(modRoot, MaskFolder);
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (IOException) { /* harmless */ }
    }

    private static void CleanFolders(string modRoot, Record record)
    {
        if (record.Files.Count > 0) return;
        var dir = Path.Combine(ProteusDir(modRoot), BackupSubdir);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* harmless */ }
    }

    /// <summary>"Upgrade what I'm wearing now": the named files are no longer declined.</summary>
    public static void ClearDeclines(string modRoot, IEnumerable<string> rels)
    {
        lock (MeshVolumeService.WriteLock)
        {
            if (ReadRecord(modRoot) is not { } r) return;
            var drop = rels.Select(MeshVolumeService.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (r.Declined.RemoveAll(drop.Contains) > 0) TryWriteRecord(modRoot, r);
        }
    }

    private static void TryWriteRecord(string modRoot, Record r)
    {
        try { WriteRecord(modRoot, r); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next write tries again */ }
    }

    private static byte[]? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* left for the next undo */ }
    }
}
