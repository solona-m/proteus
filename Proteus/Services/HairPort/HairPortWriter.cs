using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// Writes hair ports into a mod, and takes them out again.
/// <para/>
/// A port is published from the SAME place as the hairstyle it was made from — the author's option, or the default
/// data — as one more redirect beside theirs, never as a group of ours: that is what makes it follow the author's option
/// switches, and why no group is ever added, moved or reordered (Penumbra keeps settings by group position). Undo takes
/// out only the redirects Proteus added, and only while they still name Proteus's file, then the files nothing names.
/// </summary>
internal static class HairPortWriter
{
    /// <summary>A material the port needs published beside it: either the author's own file, or bytes to copy in.</summary>
    /// <param name="AuthorFile">A file of the mod's (mod-relative) the redirect names; null to copy <paramref name="Bytes"/>.</param>
    public sealed record MaterialWrite(string GamePath, string? AuthorFile, byte[]? Bytes);

    /// <summary>One port to publish from one place in the mod.</summary>
    /// <param name="Millis">How long the bend took, for the log.</param>
    /// <param name="Est">The extra skeleton to give the target race beside the port (<see cref="HairPortPlan.HairEst"/>);
    /// 0 for none.</param>
    /// <param name="SkeletonFrom">The source race's skeleton the port carries as the target's own for the hairstyle,
    /// among <paramref name="Materials"/> (see <see cref="HairPortBuilder.CarrySkeleton"/>); 0 for none.</param>
    public sealed record PortWrite(HairPortPlan.Owner Into, string SourceGamePath, string SourceFile, string SourceSha8,
                                   string TargetGamePath, byte[] Model, IReadOnlyList<MaterialWrite> Materials,
                                   long Millis = 0, int Est = 0, int SkeletonFrom = 0);

    /// <summary>The <c>Est</c> manipulation that gives a port's race its extra skeleton.</summary>
    internal static object EstFor(string targetGamePath, int entry)
    {
        var (race, id) = HairPortPlan.Parse(targetGamePath)!.Value;
        return PenumbraManipulations.EstManipulation($"{race:D4}", "Hair", id, entry);
    }

    /// <summary>Whether a manipulation is the <c>Est</c> <see cref="EstFor"/> wrote, however it has been spelled since.</summary>
    internal static Func<JsonElement, bool> IsEst(string targetGamePath, int entry)
    {
        var (race, id) = HairPortPlan.Parse(targetGamePath)!.Value;
        return m => HairPortPlan.HairEstEntry(m, race, id) == entry;
    }

    /// <summary>The <c>Est</c> entries an option was given in this save, as one test.</summary>
    private static Func<JsonElement, bool>? AnyEst(IEnumerable<(string Target, int Est)> carried)
    {
        var tests = carried.Where(c => c.Est > 0).Select(c => IsEst(c.Target, c.Est)).ToList();
        return tests.Count == 0 ? null : m => tests.Any(t => t(m));
    }

    public sealed record Outcome(bool Ok, string Message, int Count);

    /// <summary>
    /// Publish <paramref name="writes"/>: the files first, then the manifest, one option at a time, then the record. On a
    /// failure the options already written are taken back out before the files they named are deleted, so a failed port
    /// leaves nothing behind — least of all a redirect to a file that is gone.
    /// </summary>
    /// <param name="auto">Made on equip, not asked for in Studio.</param>
    public static Outcome Save(string modRoot, IReadOnlyList<PortWrite> writes, bool auto)
    {
        if (writes.Count == 0) return new Outcome(true, "", 0);
        lock (ModManifestLock.Gate)
        {
            if (PenumbraModMeta.IsLegacyFolder(modRoot))
                return new Outcome(false, new PenumbraModMeta.LegacyFolderException(Path.GetFileName(modRoot)).Message, 0);
            if (HairPortPlan.ReadOwners(modRoot) is not { } owners)
                return new Outcome(false, Unreadable, 0);

            var record = HairPortRecord.Read(modRoot);
            if (record.Unreadable) return new Outcome(false, HairPortRecord.UnreadableMessage, 0);
            var plan = new List<(PortWrite Write, HairPortPlan.Owner Owner, string File, HairPortRecord.Entry? Was)>();
            foreach (var w in writes)
            {
                var owner = owners.FirstOrDefault(o => HairPortPlan.Same(o, w.Into));
                if (owner == null) return new Outcome(false, string.Format(Loc.Localize("HairPort.Refused.NoOption.Fmt",
                    "the option \"{0}\" is no longer in the mod"), Describe(w.Into)), 0);
                if (owner.IsCombining) return new Outcome(false, Combining(owner.Group), 0);

                var was = record.Ports.FirstOrDefault(e => HairPortPlan.Same(e.Owner, owner)
                    && e.TargetGamePath.Equals(w.TargetGamePath, StringComparison.OrdinalIgnoreCase));
                if (was != null)
                {
                    // A port the user has since edited is theirs now: remaking it would throw that edit away.
                    if (EditedBy(modRoot, was.File) is { } later)
                        return new Outcome(false, ModelBackupOrder.BlockedMessage(later), 0);
                }
                else if (!HairPortPlan.CanPortInto(owners, owner, w.TargetGamePath))
                    return new Outcome(false, string.Format(Loc.Localize("HairPort.Refused.Covered.Fmt",
                        "the mod already publishes {0} there"), w.TargetGamePath), 0);
                plan.Add((w, owner, was?.File ?? HairPortPlan.PortFile(owner, w.TargetGamePath), was));
            }

            var created = new List<string>();
            var committed = new List<(HairPortPlan.Owner Owner, Dictionary<string, string> Add)>();
            var redirects = new Dictionary<HairPortPlan.Owner, Dictionary<string, string>>();
            var skeletons = new Dictionary<HairPortPlan.Owner, List<object>>();
            var carriedBy = new Dictionary<HairPortPlan.Owner, List<(string Target, int Est)>>();
            // A remade port's skeleton entry from before goes in the same write as the one it carries now — whatever
            // that is, none included — so an option never holds two for one race, and none is left untracked.
            var replaced = new Dictionary<HairPortPlan.Owner, List<(string Target, int Est)>>();
            var entries = new List<HairPortRecord.Entry>();
            try
            {
                foreach (var (w, owner, file, was) in plan)
                {
                    // A hat fit of the old port has nothing to restore once the port is remade.
                    if (was != null && HatCompatService.Forget(modRoot, file) is { } why)
                        throw new IOException(why);
                    WriteFile(modRoot, file, w.Model, created);

                    var add = redirects.TryGetValue(owner, out var a) ? a : redirects[owner] = new(StringComparer.OrdinalIgnoreCase);
                    if (was is { Est: > 0 })
                        (replaced.TryGetValue(owner, out var rl) ? rl : replaced[owner] = []).Add((w.TargetGamePath, was.Est));
                    if (w.Est > 0)
                    {
                        (skeletons.TryGetValue(owner, out var k) ? k : skeletons[owner] = []).Add(EstFor(w.TargetGamePath, w.Est));
                        (carriedBy.TryGetValue(owner, out var c) ? c : carriedBy[owner] = []).Add((w.TargetGamePath, w.Est));
                    }
                    add[w.TargetGamePath] = file;
                    var materials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in w.Materials)
                    {
                        // The author's own material for that path wins — and a redirect of ours from before is replaced.
                        // Ours is what the record says we added: a redirect to the author's own file (a material the
                        // source race reads) lives outside Hair Ports/ and is still ours to carry and later take out.
                        string? existing = HairPortPlan.FileFor(owner, m.GamePath);
                        bool ours = existing != null
                            && (HairPortPlan.IsPortFile(existing)
                                || record.Ports.Any(p => HairPortPlan.Same(p.Owner, owner)
                                                         && p.Materials.TryGetValue(m.GamePath, out var mine)
                                                         && HairPortRecord.Rel(mine).Equals(HairPortRecord.Rel(existing),
                                                                                            StringComparison.OrdinalIgnoreCase)));
                        if (existing != null && !ours) continue;
                        if (existing == null && !HairPortPlan.CanPortInto(owners, owner, m.GamePath)) continue;

                        string rel = m.AuthorFile ?? HairPortPlan.PortFile(owner, m.GamePath);
                        if (m.AuthorFile == null) WriteFile(modRoot, rel, m.Bytes ?? [], created);
                        add[m.GamePath] = rel;
                        materials[m.GamePath] = rel;
                    }
                    entries.Add(new HairPortRecord.Entry
                    {
                        Group = owner.Group, Option = owner.Option, SourceGamePath = w.SourceGamePath,
                        SourceFile = HairPortRecord.Rel(w.SourceFile), SourceSha8 = w.SourceSha8,
                        TargetGamePath = w.TargetGamePath, File = file, Materials = materials, Auto = auto,
                        PortVersion = HairPortBaker.Version, Est = w.Est, SkeletonFrom = w.SkeletonFrom,
                    });
                }

                foreach (var (owner, add) in redirects)
                {
                    BeforeMerge?.Invoke(committed.Count);
                    PenumbraModMeta.MergeOptionRedirects(modRoot, owner.Group, owner.Option, add, skeletons.GetValueOrDefault(owner),
                                                         AnyEst(replaced.GetValueOrDefault(owner) ?? []));
                    committed.Add((owner, add));
                }
            }
            catch (Exception ex)
            {
                // The options already written name files about to be deleted: take those redirects out first. One that
                // cannot be taken out keeps its files, so nothing is left pointing at nothing.
                var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (owner, add) in committed)
                    try { PenumbraModMeta.RemoveOptionRedirects(modRoot, owner.Group, owner.Option, add, AnyEst(carriedBy.GetValueOrDefault(owner) ?? [])); }
                    catch (Exception) { kept.UnionWith(add.Values); }
                // Only files that were not there before. A remade port's file was overwritten in place: where its option was
                // taken back out above it is unpublished, and the record drops it on the next read, so the automatic path
                // ports it again; where its option was never written, the manifest still names it.
                foreach (var file in created.Where(f => !kept.Contains(f)))
                    try { File.Delete(Native(modRoot, file)); } catch { /* left for the next sweep */ }
                return new Outcome(false, ex.Message, 0);
            }

            foreach (var e in entries)
            {
                record.Ports.RemoveAll(p => HairPortPlan.Same(p.Owner, e.Owner)
                    && p.TargetGamePath.Equals(e.TargetGamePath, StringComparison.OrdinalIgnoreCase));
                record.Ports.Add(e);
                record.Declined.RemoveAll(d => d == HairPortPlan.DeclineKey(e.TargetGamePath));
            }
            HairPortRecord.Write(modRoot, record);
            return new Outcome(true, "", entries.Count);
        }
    }

    /// <summary>
    /// Take out every port <paramref name="which"/> picks — and every port sharing a material with one of them, since the
    /// races of a shared material go together (<see cref="HairPortBuilder.Sharers"/>): one taken out alone would draw its
    /// own game hairstyle with the material the others still redirect. Its redirects go only while they still name
    /// Proteus's file; then every file under <see cref="HairPortPlan.Folder"/> nothing names any more. A set with a port
    /// the user edited since with the brush or part switches is left whole, the other sets still go, and the result says
    /// why. Nothing is changed while the manifest cannot be read: unknown is not gone.
    /// </summary>
    /// <param name="decline">Remember the undo, so the automatic path does not port the same hairstyle there again.</param>
    public static Outcome Undo(string modRoot, Func<HairPortRecord.Entry, bool> which, bool decline)
    {
        lock (ModManifestLock.Gate)
        {
            if (PenumbraModMeta.IsLegacyFolder(modRoot))
                return new Outcome(false, new PenumbraModMeta.LegacyFolderException(Path.GetFileName(modRoot)).Message, 0);
            if (HairPortPlan.ReadOwners(modRoot) is not { } owners)
                return new Outcome(false, Unreadable, 0);

            var record = HairPortRecord.Read(modRoot);
            if (record.Unreadable) return new Outcome(false, HairPortRecord.UnreadableMessage, 0);
            var picked = record.Ports.Where(which).ToList();
            if (picked.Count == 0)
                return new Outcome(false, Loc.Localize("HairPort.Undo.Nothing", "There are no hair ports here to undo."), 0);

            // Set by set, each all or nothing: a set with one port the user edited since stays whole, or the port kept
            // would lose the material the others take out with them. The other sets still go.
            string blocked = "";
            var undo = new List<HairPortRecord.Entry>();
            var left = new List<HairPortRecord.Entry>();
            foreach (var e in picked)
            {
                // Its set was already taken, or already left whole.
                if (undo.Contains(e) || left.Contains(e)) continue;
                var set = SharerSet(record, [e]);
                if (set.Select(p => EditedBy(modRoot, p.File)).FirstOrDefault(n => n != null) is { } later)
                {
                    blocked = ModelBackupOrder.BlockedMessage(later);
                    left.AddRange(set);
                    continue;
                }
                undo.AddRange(set.Where(p => !undo.Contains(p)));
            }
            if (undo.Count == 0) return new Outcome(false, blocked, 0);

            // The ports first, then their materials — each kept while any port still in the record uses it, so a port
            // that could not be taken out keeps drawing with what it needs.
            var gone = new List<HairPortRecord.Entry>();
            foreach (var e in undo)
            {
                // An option a readable manifest no longer has took our redirects with it.
                if (owners.Any(o => HairPortPlan.Same(o, e.Owner)))
                    try
                    {
                        PenumbraModMeta.RemoveOptionRedirects(modRoot, e.Group, e.Option,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [e.TargetGamePath] = e.File },
                            e.Est > 0 ? IsEst(e.TargetGamePath, e.Est) : null);
                    }
                    catch (Exception ex) { blocked = ex.Message; continue; }
                gone.Add(e);
            }

            foreach (var e in gone)
            {
                record.Ports.Remove(e);
                if (decline && !record.Declined.Contains(HairPortPlan.DeclineKey(e.TargetGamePath)))
                    record.Declined.Add(HairPortPlan.DeclineKey(e.TargetGamePath));
                // Only once the port is out: a hat fit of it has nothing left to restore.
                if (HatCompatService.Forget(modRoot, e.File) is { } why) blocked = why;
            }

            foreach (var e in gone.Where(e => e.Materials.Count > 0 && owners.Any(o => HairPortPlan.Same(o, e.Owner))))
            {
                // A material another port of the same option still uses stays published — including one that could not
                // be taken out above, which is still in the record.
                var drop = e.Materials.Where(m => !record.Ports.Any(p => HairPortPlan.Same(p.Owner, e.Owner)
                                                                         && p.Materials.ContainsKey(m.Key)))
                                      .ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase);
                try { PenumbraModMeta.RemoveOptionRedirects(modRoot, e.Group, e.Option, drop); }
                catch (Exception ex) { blocked = ex.Message; }
            }
            int removed = gone.Count;

            DeleteUnnamed(modRoot);
            HairPortRecord.Write(modRoot, record);
            return removed == 0 ? new Outcome(false, blocked, 0) : new Outcome(true, blocked, removed);
        }
    }

    /// <summary>
    /// <paramref name="picked"/>, and every port that shares a material redirect with one of them in the same place of
    /// the mod, over and over until nothing more joins.
    /// </summary>
    internal static List<HairPortRecord.Entry> SharerSet(HairPortRecord record, IEnumerable<HairPortRecord.Entry> picked)
    {
        var set = picked.ToList();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var p in record.Ports)
            {
                if (set.Contains(p)) continue;
                if (!set.Any(e => HairPortPlan.Same(e.Owner, p.Owner) && e.Materials.Keys.Any(p.Materials.ContainsKey))) continue;
                set.Add(p);
                grew = true;
            }
        }
        return set;
    }

    /// <summary>A test seam: called before each option's redirects are merged, with how many are already in. Per
    /// thread, since tests run in parallel. Never set outside tests.</summary>
    [ThreadStatic] internal static Action<int>? BeforeMerge;

    /// <summary>
    /// The feature that edited a port in place since it was written — part switches or the brush, which each keep a
    /// backup — or null. Hat fitting is not one: it is redone on whatever the port becomes.
    /// </summary>
    internal static string? EditedBy(string modRoot, string rel)
    {
        foreach (var (subdir, name) in new[]
                 {
                     (MeshToggleService.BackupSubdir, Loc.Localize("MeshVolume.Feature.Toggles", "part switches")),
                     (MeshVolumeService.BackupSubdir, Loc.Localize("MeshVolume.Feature.Brush", "the brush")),
                 })
            if (File.Exists(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, subdir, NativeRel(rel))))
                return name;
        return null;
    }

    /// <summary>Delete every file under <see cref="HairPortPlan.Folder"/> no redirect of the mod names. Only there: the
    /// author's files are never deleted, whatever names them.</summary>
    private static void DeleteUnnamed(string modRoot)
    {
        var dir = Path.Combine(modRoot, HairPortPlan.Folder);
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
            try { File.Delete(file); } catch { /* in use: the next undo tries again */ }
        }
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    private static void WriteFile(string modRoot, string rel, byte[] bytes, List<string> created)
    {
        var path = Native(modRoot, rel);
        if (!File.Exists(path)) created.Add(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PenumbraModMeta.AtomicWrite(path, bytes);
    }

    private static string Native(string modRoot, string rel) => Path.Combine(modRoot, NativeRel(rel));

    private static string NativeRel(string rel) => rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    internal static string Describe(HairPortPlan.Owner owner)
        => owner.IsDefault ? Loc.Localize("HairPort.DefaultData", "the mod's default files") : $"{owner.Group} / {owner.Option}";

    internal static string Combining(string group)
        => string.Format(Loc.Localize("HairPort.Refused.Combining.Fmt",
            "the hairstyle is in \"{0}\", a combining group, which Proteus does not edit"), group);

    internal static string Unreadable
        => Loc.Localize("HairPort.Refused.Unreadable", "the mod's manifest could not be read");
}
