using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// Points a mod's redirects of a Miqo'te hairstyle at earless copies, and back at the author's files again.
/// <para/>
/// The author's files are never written or deleted: a copy goes under <see cref="EarlessHairPlan.Folder"/>, and the
/// redirect that named the author's file names the copy — swapped only while it still names what it was swapped from,
/// so an author's update since is never overwritten. No group, option or manipulation is added, moved or changed
/// (Penumbra keeps settings by group position): only the value of a redirect the mod already has.
/// </summary>
internal static class EarlessHairWriter
{
    /// <param name="Restored">For a restore: the game paths pointed back at the author's file.</param>
    /// <param name="Unreadable">The manifest or the record could not be read: no verdict, the next try may read it.</param>
    /// <param name="Warning">The redirects are as asked, but the record or the sweep after them failed — for the log,
    /// not for the player: what they asked for was done.</param>
    public sealed record Outcome(bool Ok, string Message, int Count, IReadOnlyList<string>? Restored = null,
                                 bool Unreadable = false, string Warning = "")
    {
        public IReadOnlyList<string> RestoredPaths => Restored ?? [];
    }

    /// <summary>A test seam: called before each place's redirects are swapped, with how many are already in. Per
    /// thread, since tests run in parallel. Never set outside tests.</summary>
    [ThreadStatic] internal static Action<int>? BeforeSwap;

    /// <summary>A test seam: called once the redirects are in, before the record is written. Per thread. Never set
    /// outside tests.</summary>
    [ThreadStatic] internal static Action? AfterSwaps;

    /// <summary>
    /// Save <paramref name="writes"/>: the copies first, then the manifest one place at a time, then the record. On a
    /// failure the places already swapped are swapped back before the copies they named are deleted, so a failed save
    /// leaves nothing behind — least of all a redirect to a file that is gone.
    /// </summary>
    public static Outcome Save(string modRoot, IReadOnlyList<EarlessHairBuilder.SwapWrite> writes)
    {
        if (writes.Count == 0) return new Outcome(true, "", 0);
        lock (ModManifestLock.Gate)
        {
            if (PenumbraModMeta.IsLegacyFolder(modRoot))
                return new Outcome(false, new PenumbraModMeta.LegacyFolderException(Path.GetFileName(modRoot)).Message, 0);
            if (HairPortPlan.ReadOwners(modRoot) is not { } owners)
                return new Outcome(false, HairPortWriter.Unreadable, 0, Unreadable: true);
            var record = EarlessHairRecord.Read(modRoot);
            if (record.Unreadable) return new Outcome(false, EarlessHairRecord.UnreadableMessage, 0, Unreadable: true);

            var plan = new List<(EarlessHairBuilder.SwapWrite Write, HairPortPlan.Owner Owner)>();
            foreach (var w in writes)
            {
                if (owners.FirstOrDefault(o => HairPortPlan.Same(o, w.Into)) is not { } owner)
                    return new Outcome(false, string.Format(ProteusLoc.Localize("HairPort.Refused.NoOption.Fmt",
                        "the option \"{0}\" is no longer in the mod"), HairPortWriter.Describe(w.Into)), 0);
                if (owner.IsCombining) return new Outcome(false, HairPortWriter.Combining(owner.Group), 0);
                // Planned against a manifest that has changed since: planned again on the next look.
                if (HairPortPlan.FileFor(owner, w.GamePath) is not { } now || !SameRel(now, w.From))
                    return new Outcome(false, Changed, 0);
                // A copy the user edited since is theirs now: making it again would throw that edit away.
                if (w.Model.Length > 0 && File.Exists(Native(modRoot, w.To)) && HairPortWriter.EditedBy(modRoot, w.To) is { } by)
                    return new Outcome(false, ModelBackupOrder.BlockedMessage(by), 0);
                plan.Add((w, owner));
            }

            var created = new List<string>();
            var committed = new List<(HairPortPlan.Owner Owner, Dictionary<string, (string From, string To)> Swaps)>();
            try
            {
                foreach (var w in plan.Select(p => p.Write).Where(w => w.Model.Length > 0)
                                      .DistinctBy(w => HairPortRecord.Rel(w.To), StringComparer.OrdinalIgnoreCase))
                {
                    // A hat fit of the copy written over has nothing left to restore.
                    if (File.Exists(Native(modRoot, w.To)) && HatCompatService.Forget(modRoot, w.To) is { } why)
                        throw new IOException(why);
                    WriteFile(modRoot, w.To, w.Model, created);
                }

                foreach (var place in plan.Where(p => !SameRel(p.Write.From, p.Write.To))
                                          .GroupBy(p => (p.Owner.Group, p.Owner.Option)))
                {
                    var owner = place.First().Owner;
                    var swaps = place.ToDictionary(p => p.Write.GamePath, p => (p.Write.From, p.Write.To),
                                                   StringComparer.OrdinalIgnoreCase);
                    BeforeSwap?.Invoke(committed.Count);
                    int swapped = PenumbraModMeta.SwapOptionRedirects(modRoot, owner.Group, owner.Option, swaps);
                    committed.Add((owner, swaps));
                    // Fewer than planned (a manifest naming one path twice in two spellings): not saved as done.
                    if (swapped != swaps.Count) throw new IOException(Changed);
                }
            }
            catch (Exception ex)
            {
                // The places already swapped name copies about to be deleted: swap them back first. One that cannot be
                // swapped back keeps its copy, so nothing is left pointing at nothing.
                var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (owner, swaps) in committed)
                {
                    var back = swaps.ToDictionary(s => s.Key, s => (s.Value.To, s.Value.From), StringComparer.OrdinalIgnoreCase);
                    try { PenumbraModMeta.SwapOptionRedirects(modRoot, owner.Group, owner.Option, back); }
                    catch (Exception) { kept.UnionWith(swaps.Values.Select(s => HairPortRecord.Rel(s.To))); }
                }
                foreach (var file in created.Where(f => !kept.Contains(HairPortRecord.Rel(f))))
                    try { File.Delete(Native(modRoot, file)); } catch { /* left for the next sweep */ }
                return new Outcome(false, ex.Message, 0);
            }

            foreach (var (w, owner) in plan)
            {
                record.Swaps.RemoveAll(e => HairPortPlan.Same(e.Owner, owner)
                                            && e.GamePath.Equals(w.GamePath, StringComparison.OrdinalIgnoreCase));
                if (!EarlessHairPlan.IsEarlessFile(w.To)) continue;   // pointed back at the author's own, now earless
                record.Swaps.Add(new EarlessHairRecord.Entry
                {
                    Group = owner.Group, Option = owner.Option, GamePath = w.GamePath,
                    AuthorFile = HairPortRecord.Rel(w.AuthorFile), AuthorSha8 = w.AuthorSha8,
                    File = HairPortRecord.Rel(w.To), EarlessVersion = EarlessHair.Version,
                });
            }
            // The swaps are in: a record that cannot be written is recovered from the copies' own paths
            // (EarlessHairPlan.AuthorFileFor), so the save stands — and the caller still reloads.
            try
            {
                AfterSwaps?.Invoke();
                EarlessHairRecord.Write(modRoot, record);
                DeleteUnnamed(modRoot);
            }
            catch (Exception ex) { return new Outcome(true, "", plan.Count, Warning: ex.Message); }
            return new Outcome(true, "", plan.Count);
        }
    }

    /// <summary>
    /// Point every redirect of the mod that names an earless copy — of <paramref name="gamePath"/> only, when given — back
    /// at the author's file: the one the record names, else the one in the copy's own path. Each only while it still
    /// names the copy. A copy the user edited since, or whose author's file is gone, is left as it is, and the result says
    /// why; the rest still go. Then every copy nothing names any more is deleted. Nothing is changed while the manifest
    /// or the record cannot be read: unknown is not gone.
    /// </summary>
    public static Outcome Restore(string modRoot, string? gamePath = null)
    {
        lock (ModManifestLock.Gate)
        {
            if (PenumbraModMeta.IsLegacyFolder(modRoot))
                return new Outcome(false, new PenumbraModMeta.LegacyFolderException(Path.GetFileName(modRoot)).Message, 0);
            if (HairPortPlan.ReadOwners(modRoot) is not { } owners)
                return new Outcome(false, HairPortWriter.Unreadable, 0, Unreadable: true);
            var record = EarlessHairRecord.Read(modRoot);
            if (record.Unreadable) return new Outcome(false, EarlessHairRecord.UnreadableMessage, 0, Unreadable: true);

            var exists = EarlessHairPlan.Exists(modRoot);
            string blocked = "";
            var restored = new List<string>();
            foreach (var owner in owners.Where(o => !o.IsCombining))
                foreach (var (path, rel) in owner.Files.ToList())
                {
                    if (!EarlessHairPlan.IsEarlessFile(rel)) continue;
                    if (gamePath != null && !path.Replace('\\', '/').Equals(gamePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (HairPortWriter.EditedBy(modRoot, rel) is { } by)
                    {
                        blocked = ModelBackupOrder.BlockedMessage(by);
                        continue;
                    }
                    var author = EarlessHairPlan.AuthorFileFor(record, owner, path, rel, exists);
                    if (author == null || !exists(author))
                    {
                        blocked = EarlessHairBuilder.AuthorMissing(author ?? EarlessHairPlan.AuthorOfCopy(rel));
                        continue;
                    }
                    try
                    {
                        BeforeSwap?.Invoke(restored.Count);
                        if (PenumbraModMeta.SwapOptionRedirects(modRoot, owner.Group, owner.Option,
                                new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase) { [path] = (rel, author) }) != 1)
                            continue;   // not swapped after all: nothing put back, nothing counted
                    }
                    catch (Exception ex) { blocked = ex.Message; continue; }
                    record.Swaps.RemoveAll(e => HairPortPlan.Same(e.Owner, owner)
                                                && e.GamePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                    restored.Add(path);
                }

            // What was pointed back is back, whatever happens to the bookkeeping after: counted, so the caller reloads.
            // A record every entry of which was dropped on reading (the author re-pointed them) goes too.
            string warning = "";
            try
            {
                AfterSwaps?.Invoke();
                if (restored.Count > 0 || (record.Swaps.Count == 0 && EarlessHairRecord.Exists(modRoot)))
                    EarlessHairRecord.Write(modRoot, record);
                DeleteUnnamed(modRoot);
            }
            catch (Exception ex) { warning = ex.Message; }
            return new Outcome(blocked.Length == 0, blocked, restored.Count, restored, Warning: warning);
        }
    }

    /// <summary>Delete every file under <see cref="EarlessHairPlan.Folder"/> no redirect of the mod names, its hat fit
    /// with it. Only there: the author's files are never deleted, whatever names them.</summary>
    private static void DeleteUnnamed(string modRoot)
    {
        var dir = Path.Combine(modRoot, EarlessHairPlan.Folder);
        if (!Directory.Exists(dir)) return;
        // Read afresh, and not at all when it cannot be: an unreadable manifest names nothing, which is not the same as
        // naming none of these.
        if (HairPortPlan.ReadOwners(modRoot) is not { } owners) return;
        var named = new HashSet<string>(owners.SelectMany(o => o.Files.Values).Select(HairPortRecord.Rel),
                                        StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = HairPortRecord.Rel(Path.GetRelativePath(modRoot, file));
            if (named.Contains(rel)) continue;
            // A hat fit of a copy that is gone has nothing left to restore; one that cannot be forgotten keeps its copy.
            if (HatCompatService.Forget(modRoot, rel) != null) continue;
            try { File.Delete(file); } catch { /* in use: the next save or restore tries again */ }
        }
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    private static void WriteFile(string modRoot, string rel, byte[] bytes, List<string> created)
    {
        var path = Native(modRoot, rel);
        // Only ever inside the copies' folder: a manifest path that climbs out of it fails the save, which rolls back.
        var folder = Path.GetFullPath(Path.Combine(modRoot, EarlessHairPlan.Folder)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            throw new IOException(string.Format(ProteusLoc.Localize("HairPort.Earless.Refused.Outside.Fmt",
                "{0} is not a path inside the mod's earless folder, so nothing was written"), rel));
        if (!File.Exists(path)) created.Add(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PenumbraModMeta.AtomicWrite(path, bytes);
    }

    private static bool SameRel(string a, string b)
        => HairPortRecord.Rel(a).Equals(HairPortRecord.Rel(b), StringComparison.OrdinalIgnoreCase);

    private static string Native(string modRoot, string rel)
        => Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

    private static string Changed => ProteusLoc.Localize("HairPort.Earless.Refused.Changed",
        "the mod changed while Proteus was working on it, so nothing was written");
}
