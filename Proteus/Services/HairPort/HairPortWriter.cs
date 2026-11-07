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
    /// 0 for none. With <paramref name="SkeletonFrom"/>, the slot the carried skeleton is published at.</param>
    /// <param name="SkeletonFrom">The source race's skeleton the port carries, among <paramref name="Materials"/>, to
    /// slot <paramref name="Est"/> of the target race (see <see cref="HairPortSkeleton.CarryAt"/>); 0 for none.</param>
    /// <param name="Stripped">Not a port made now: one from before, saved again without the skeleton it carried,
    /// because it could not be made again and that skeleton changed another hairstyle. Only its redirects change: its
    /// model file is not written (<paramref name="Model"/> is unused), its hat fit and the user's edits are kept, and
    /// its record keeps how and when it was made. Skipped when the port is gone by the time it is saved.</param>
    /// <param name="StrippedFrom">For a strip, the port's entry and carried skeleton it was planned from: it applies only
    /// while the port still has them.</param>
    /// <param name="Pushed">Points the bend sank into the target's skull that were pushed back out, for the log; never
    /// saved.</param>
    /// <param name="PushWorst">The furthest any was pushed, in metres, for the log.</param>
    /// <param name="NoScalp">No scalp could be read to push against (a face model without a skin mesh), for the log.</param>
    public sealed record PortWrite(HairPortPlan.Owner Into, string SourceGamePath, string SourceFile, string SourceSha8,
                                   string TargetGamePath, byte[] Model, IReadOnlyList<MaterialWrite> Materials,
                                   long Millis = 0, int Est = 0, int SkeletonFrom = 0, bool Stripped = false,
                                   (int Est, int SkeletonFrom) StrippedFrom = default, int Pushed = 0, float PushWorst = 0f,
                                   bool NoScalp = false);

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

    /// <param name="Saved">The writes saved — those a save skipped left out (a strip of a port no longer the one it was
    /// planned from).</param>
    /// <param name="Also">For an Undo, the ports of build 1430's shared sets it took along beyond what was asked for
    /// (target game paths; <see cref="Join.Folder"/>), which its message names.</param>
    public sealed record Outcome(bool Ok, string Message, int Count, IReadOnlyList<PortWrite>? Saved = null,
                                 IReadOnlyList<string>? Also = null)
    {
        public IReadOnlyList<PortWrite> SavedWrites => Saved ?? [];
    }

    /// <summary>Whether a strip still applies to the port the record holds now: the same carry — entry and skeleton —
    /// made from the same model, with every redirect the strip keeps; not one undone, or remade by another look, since
    /// the plan.</summary>
    private static bool StillStrippable(PortWrite w, HairPortRecord.Entry? was)
        => was is { SkeletonFrom: > 0 }
           && (was.Est, was.SkeletonFrom) == w.StrippedFrom
           && string.Equals(was.SourceSha8, w.SourceSha8, StringComparison.OrdinalIgnoreCase)
           && w.Materials.All(m => was.Materials.TryGetValue(m.GamePath, out var rel)
                                   && HairPortRecord.Rel(rel).Equals(HairPortRecord.Rel(m.AuthorFile ?? ""), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Publish <paramref name="writes"/>: the files first, then the manifest, one option at a time, then the record. On a
    /// failure the options already written are taken back out before the files they named are deleted, so a failed port
    /// leaves nothing behind — least of all a redirect to a file that is gone.
    /// </summary>
    /// <param name="auto">Made on equip, not asked for in Studio.</param>
    public static Outcome Save(string modRoot, IReadOnlyList<PortWrite> writes, bool auto)
    {
        if (writes.Count == 0) return new Outcome(true, "", 0, []);
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
                // A strip of a port whose option is gone: the option took the port with it, nothing is left to strip.
                if (owner == null && w.Stripped) continue;
                if (owner == null) return new Outcome(false, string.Format(ProteusLoc.Localize("HairPort.Refused.NoOption.Fmt",
                    "the option \"{0}\" is no longer in the mod"), Describe(w.Into)), 0);
                if (owner.IsCombining) return new Outcome(false, Combining(owner.Group), 0);

                var was = record.Ports.FirstOrDefault(e => HairPortPlan.Same(e.Owner, owner)
                    && e.TargetGamePath.Equals(w.TargetGamePath, StringComparison.OrdinalIgnoreCase));
                // A strip of a port undone (or taken out by hand) since the plan: nothing is left to strip — and writing
                // it would bring the port back. Of one remade since: it would put the old port's redirects over the new.
                if (w.Stripped && !StillStrippable(w, was)) continue;
                if (was != null)
                {
                    // A port the user has since edited is theirs now: remaking it would throw that edit away. A strip
                    // writes no model, so the edit stays.
                    if (!w.Stripped && EditedBy(modRoot, was.File) is { } later)
                        return new Outcome(false, ModelBackupOrder.BlockedMessage(later), 0);
                }
                else if (!HairPortPlan.CanPortInto(owners, owner, w.TargetGamePath))
                    return new Outcome(false, Covered(w.TargetGamePath), 0);
                // A carried skeleton is what the port moves with: without it, no port at all — refused before anything
                // is written, so the port from before stays as it was.
                if (w.Materials.FirstOrDefault(m => CarriesSkeleton(w, m.GamePath) && !MayPublish(owners, owner, record, m.GamePath))
                    is { } taken)
                    return new Outcome(false, Covered(taken.GamePath), 0);
                // So is a material of the port's own name: the game ships no file there, so a model naming it with no
                // redirect where it is on draws invisible. Borrowing the default data's — always on with any option — is
                // the one way to go without our own.
                if (!w.Stripped && w.Materials.FirstOrDefault(m => HairMaterialPath.IsOwnName(m.GamePath)
                                                                   && !MayPublish(owners, owner, record, m.GamePath)
                                                                   && !PublishedByDefaults(owners, owner, m.GamePath))
                    is { } unpublishable)
                    return new Outcome(false, Covered(unpublishable.GamePath), 0);
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
            // What a strip publishes again was there before the save: a failed save leaves it, or the port would be gone
            // for good — a race refused is not ported again.
            var republished = new Dictionary<HairPortPlan.Owner, HashSet<string>>();
            var entries = new List<HairPortRecord.Entry>();
            bool dropped = false;
            try
            {
                foreach (var (w, owner, file, was) in plan)
                {
                    // A strip changes redirects only: the model on disk is the port as it was — hat fit and edits and
                    // all, whose backups and records stay.
                    bool stripped = w.Stripped;
                    // A hat fit of the old port has nothing to restore once the port is remade.
                    if (was != null && !stripped && HatCompatService.Forget(modRoot, file) is { } why)
                        throw new IOException(why);
                    if (!stripped) WriteFile(modRoot, file, w.Model, created);

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
                        if (!MayPublish(owners, owner, record, m.GamePath)) continue;

                        string rel = m.AuthorFile ?? HairPortPlan.PortFile(owner, m.GamePath);
                        if (m.AuthorFile == null) WriteFile(modRoot, rel, m.Bytes ?? [], created);
                        add[m.GamePath] = rel;
                        materials[m.GamePath] = rel;
                    }
                    if (stripped)
                    {
                        var again = republished.TryGetValue(owner, out var r) ? r : republished[owner] = new(StringComparer.OrdinalIgnoreCase);
                        again.Add(w.TargetGamePath);
                        again.UnionWith(materials.Keys);
                    }
                    entries.Add(new HairPortRecord.Entry
                    {
                        Group = owner.Group, Option = owner.Option, SourceGamePath = w.SourceGamePath,
                        SourceFile = HairPortRecord.Rel(w.SourceFile), SourceSha8 = w.SourceSha8,
                        TargetGamePath = w.TargetGamePath, File = file, Materials = materials,
                        // A strip is the same port, made when and how it was.
                        Auto = stripped ? was!.Auto : auto,
                        PortVersion = stripped ? was!.PortVersion : HairPortBaker.Version, Est = w.Est, SkeletonFrom = w.SkeletonFrom,
                    });
                }

                // A remade port that no longer needs a redirect it added before (a skeleton it carried, a material) takes
                // it out — while it still names the file it was given, and no other port of the option still uses it —
                // or nothing would ever take it out: the record forgets it. Before the merges, so a merge that fails
                // later leaves none of these behind untracked.
                foreach (var (_, owner, _, was) in plan.Where(p => p.Was != null))
                {
                    var drop = Unneeded(was!, owner, redirects[owner], record, plan.Select(p => p.Was), owners, entries);
                    if (drop.Count == 0) continue;
                    PenumbraModMeta.RemoveOptionRedirects(modRoot, owner.Group, owner.Option, drop);
                    dropped = true;
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
                {
                    var again = republished.GetValueOrDefault(owner);
                    var undo = add.Where(p => again?.Contains(p.Key) != true)
                                  .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                    try { PenumbraModMeta.RemoveOptionRedirects(modRoot, owner.Group, owner.Option, undo, AnyEst(carriedBy.GetValueOrDefault(owner) ?? [])); }
                    catch (Exception) { kept.UnionWith(add.Values); }
                }
                // Only files that were not there before. A remade port's file was overwritten in place: where its option was
                // taken back out above it is unpublished, and the record drops it on the next read, so the automatic path
                // ports it again; where its option was never written, the manifest still names it.
                foreach (var file in created.Where(f => !kept.Contains(f)))
                    try { File.Delete(Native(modRoot, file)); } catch { /* left for the next sweep */ }
                return new Outcome(false, ex.Message, 0);
            }

            foreach (var (e, w) in entries.Zip(plan.Select(p => p.Write)))
            {
                record.Ports.RemoveAll(p => HairPortPlan.Same(p.Owner, e.Owner)
                    && p.TargetGamePath.Equals(e.TargetGamePath, StringComparison.OrdinalIgnoreCase));
                record.Ports.Add(e);
                // A port made now ends an undo of its target; a strip is no port made, and leaves the undo remembered.
                if (!w.Stripped) record.Declined.RemoveAll(d => d == HairPortPlan.DeclineKey(e.TargetGamePath));
            }
            HairPortRecord.Write(modRoot, record);
            if (dropped) DeleteUnnamed(modRoot);
            return new Outcome(true, "", entries.Count, plan.Select(p => p.Write).ToList());
        }
    }

    /// <summary>
    /// The redirects a remade port's entry from before added that it no longer publishes (<paramref name="add"/> is
    /// everything its option is given now) and that no other port of the option still uses — the ports remade in the
    /// same save, <paramref name="remade"/>, aside, since what they use is in <paramref name="add"/>. A material of a
    /// port's own name stays while any port left — of any place, those this save writes (<paramref name="written"/>)
    /// included — relies on it there (<see cref="ReliesOn"/>): an option's port may draw with the default data's.
    /// </summary>
    internal static Dictionary<string, string> Unneeded(HairPortRecord.Entry was, HairPortPlan.Owner owner,
                                                        IReadOnlyDictionary<string, string> add, HairPortRecord record,
                                                        IEnumerable<HairPortRecord.Entry?> remade,
                                                        IReadOnlyList<HairPortPlan.Owner>? owners = null,
                                                        IEnumerable<HairPortRecord.Entry>? written = null)
    {
        var replaced = remade.ToHashSet();
        var left = record.Ports.Where(p => !replaced.Contains(p)).Concat(written ?? []).ToList();
        return was.Materials
                  .Where(m => !add.ContainsKey(m.Key)
                              && (HairMaterialPath.IsOwnName(m.Key)
                                  ? !left.Any(p => ReliesOn(p, owner, m.Key, owners ?? []))
                                  : !left.Any(p => HairPortPlan.Same(p.Owner, owner) && p.Materials.ContainsKey(m.Key))))
                  .ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="entry"/>'s model may name the own-named material <paramref name="gamePath"/>: a port
    /// across the sexes to the race the name was made for, of a hairstyle whose material folder holds it. Read from the
    /// record alone, so it may say yes for a port that names none — it only ever keeps a redirect longer.
    /// </summary>
    internal static bool NeedsOwn(HairPortRecord.Entry entry, string gamePath)
    {
        if (!HairMaterialPath.IsOwnName(gamePath)) return false;
        if (HairPortPlan.Parse(entry.TargetGamePath) is not { } t || HairPortPlan.Parse(entry.SourceGamePath) is not { } s)
            return false;
        if (HairMaterialPath.OwnRace(gamePath) != t.Race || !CrossSexRefit.CrossesSex(s.Race, t.Race)) return false;
        string folder = $"chara/human/c{HairMaterialPath.Race(t.Race, t.Id):D4}/obj/hair/h{t.Id:D4}/material/";
        return gamePath.Replace('\\', '/').StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> draws with <paramref name="owner"/>'s redirect of the own-named material
    /// <paramref name="gamePath"/>: its own place's, or the default data's when its own place does not publish it.
    /// </summary>
    internal static bool ReliesOn(HairPortRecord.Entry entry, HairPortPlan.Owner owner, string gamePath,
                                  IReadOnlyList<HairPortPlan.Owner> owners)
    {
        if (!NeedsOwn(entry, gamePath)) return false;
        if (HairPortPlan.Same(entry.Owner, owner)) return true;
        if (!owner.IsDefault) return false;
        var own = owners.FirstOrDefault(o => HairPortPlan.Same(o, entry.Owner));
        return own == null || HairPortPlan.FileFor(own, gamePath) == null;
    }

    /// <summary>Whether the default data publishes <paramref name="gamePath"/> for a port of <paramref name="owner"/>
    /// to draw with — always on with any option, so a port there need not publish its own.</summary>
    internal static bool PublishedByDefaults(IReadOnlyList<HairPortPlan.Owner> owners, HairPortPlan.Owner owner, string gamePath)
        => !owner.IsDefault && owners.FirstOrDefault(o => o.IsDefault) is { } defaults
           && HairPortPlan.FileFor(defaults, gamePath) != null;

    /// <summary>
    /// Whether a redirect of <paramref name="gamePath"/> may be published from <paramref name="owner"/>: the path is
    /// ours there already — a port file, or what the record says a port of the option added (a redirect to the author's
    /// own file lives outside Hair Ports/ and is still ours to carry and later take out) — or free (see
    /// <see cref="HairPortPlan.CanPortInto"/>). The author's own redirect always wins.
    /// </summary>
    internal static bool MayPublish(IReadOnlyList<HairPortPlan.Owner> owners, HairPortPlan.Owner owner, HairPortRecord record,
                                    string gamePath)
    {
        if (HairPortPlan.FileFor(owner, gamePath) is not { } existing) return HairPortPlan.CanPortInto(owners, owner, gamePath);
        return HairPortPlan.IsPortFile(existing)
            || record.Ports.Any(p => HairPortPlan.Same(p.Owner, owner) && p.Materials.TryGetValue(gamePath, out var mine)
                                     && HairPortRecord.Rel(mine).Equals(HairPortRecord.Rel(existing), StringComparison.OrdinalIgnoreCase));
    }

    internal static string Covered(string gamePath)
        => string.Format(ProteusLoc.Localize("HairPort.Refused.Covered.Fmt", "the mod already publishes {0} there"), gamePath);

    /// <summary>Whether <paramref name="gamePath"/> is the skeleton, or its physics, <paramref name="w"/> carries for its
    /// race's hairstyle (<see cref="PortWrite.SkeletonFrom"/>) — at the slot its entry points to.</summary>
    private static bool CarriesSkeleton(PortWrite w, string gamePath)
    {
        if (w.SkeletonFrom <= 0 || HairPortPlan.Parse(w.TargetGamePath) is not { } t) return false;
        int slot = w.Est > 0 ? w.Est : t.Id;
        return gamePath.Equals(HairPortPlan.SkeletonPath(t.Race, slot), StringComparison.OrdinalIgnoreCase)
            || gamePath.Equals(HairPortPlan.PhysicsPath(t.Race, slot), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Take out every port <paramref name="which"/> picks — and every port sharing a material with one of them, since the
    /// races of a shared material go together (<see cref="HairPortBuilder.Sharers"/>): one taken out alone would draw its
    /// own game hairstyle with the material the others still redirect — and every port of build 1430's shared sets holding
    /// a game-named redirect in a taken race's own folder (<see cref="Join.Folder"/>), which the result names. Its redirects
    /// go only while they still name Proteus's file; a material of a port's own name only once no port left relies on it,
    /// in that place or the default data (<see cref="ReliesOn"/>); then every file under <see cref="HairPortPlan.Folder"/>
    /// nothing names any more. A set with a port the user edited since — the brush, part switches or a matcap of its own —
    /// is left whole, the other sets still go, and the result says why. Nothing is changed while the manifest cannot be
    /// read: unknown is not gone.
    /// </summary>
    /// <param name="decline">Remember the undo, so the automatic path does not port the same hairstyle there again — for
    /// what was asked for and what shares its material, never for an old port taken along by its folder.</param>
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
                return new Outcome(false, ProteusLoc.Localize("HairPort.Undo.Nothing", "There are no hair ports here to undo."), 0);

            // Set by set, each all or nothing: a set with one port the user edited since stays whole, or the port kept
            // would lose the material the others take out with them. The other sets still go.
            string blocked = "";
            var undo = new List<HairPortRecord.Entry>();
            var how = new Dictionary<HairPortRecord.Entry, Join>();
            var left = new List<HairPortRecord.Entry>();
            foreach (var e in picked)
            {
                // Its set was already taken, or already left whole.
                if (undo.Contains(e) || left.Contains(e)) continue;
                var set = SharerSet(record, [e]);
                if (set.Select(p => EditedBy(modRoot, p.Entry.File)).FirstOrDefault(n => n != null) is { } later)
                {
                    blocked = ModelBackupOrder.BlockedMessage(later);
                    left.AddRange(set.Select(p => p.Entry));
                    continue;
                }
                // A port's join kind can depend on which pick reached it first: an old port's key-sharers reached through a
                // remade pick's folder clause are Folder (not declined, listed as "also"); reached from that old port as a
                // pick of its own first, they are Key. Harmless — Folder members are build 1430's cross-sex ports, which the
                // automatic path never makes again — and accepted as such (review 2026-11-05).
                foreach (var (p, j) in set)
                {
                    // What another pick took by its own key keeps that: only what nothing asked for is "also".
                    if (how.TryGetValue(p, out var had)) { if (j < had) how[p] = j; continue; }
                    undo.Add(p);
                    how[p] = j;
                }
            }
            foreach (var e in picked.Where(how.ContainsKey)) how[e] = Join.Picked;
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
                        RemoveRedirects(modRoot, e.Group, e.Option,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [e.TargetGamePath] = e.File },
                            e.Est > 0 ? IsEst(e.TargetGamePath, e.Est) : null);
                    }
                    catch (Exception ex) { blocked = ex.Message; continue; }
                gone.Add(e);
            }

            foreach (var e in gone)
            {
                record.Ports.Remove(e);
                // Only what was asked for, and what goes with it by its own material: a port of build 1430 taken along
                // because it held a redirect in the undone race's folder was never undone by the user.
                if (decline && how[e] != Join.Folder && !record.Declined.Contains(HairPortPlan.DeclineKey(e.TargetGamePath)))
                    record.Declined.Add(HairPortPlan.DeclineKey(e.TargetGamePath));
                // Only once the port is out: a hat fit of it has nothing left to restore.
                if (HatCompatService.Forget(modRoot, e.File) is { } why) blocked = why;
            }

            foreach (var e in gone.Where(e => e.Materials.Count > 0 && owners.Any(o => HairPortPlan.Same(o, e.Owner))))
            {
                // A material another port of the same option still uses stays published — including one that could not
                // be taken out above, which is still in the record. One of a port's own name goes by its own rule, below.
                var drop = e.Materials.Where(m => !HairMaterialPath.IsOwnName(m.Key)
                                                  && !record.Ports.Any(p => HairPortPlan.Same(p.Owner, e.Owner)
                                                                            && p.Materials.ContainsKey(m.Key)))
                                      .ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase);
                try { RemoveRedirects(modRoot, e.Group, e.Option, drop); }
                catch (Exception ex) { blocked = ex.Message; }
            }
            if (DropOwnMaterials(modRoot, owners, record, gone) is { } failed) blocked = failed;
            int removed = gone.Count;

            DeleteUnnamed(modRoot);
            HairPortRecord.Write(modRoot, record);
            var also = gone.Where(e => how[e] == Join.Folder).Select(e => e.TargetGamePath).ToList();
            string message = also.Count == 0 ? blocked : JoinText(blocked, AlsoMessage(also));
            return removed == 0 ? new Outcome(false, blocked, 0) : new Outcome(true, message, removed, Also: also);
        }
    }

    /// <summary>
    /// Take each own-named material (<see cref="HairMaterialPath.IsOwnName"/>) a port in <paramref name="gone"/> may have
    /// drawn with out of every place where no port left relies on it (<see cref="ReliesOn"/>) — the place the record says
    /// published it, and any other, since the <c>_portNNNN</c> marker is Proteus's alone. Kept wherever a port left still
    /// draws with it. The error of a place that could not be written, or null.
    /// </summary>
    private static string? DropOwnMaterials(string modRoot, IReadOnlyList<HairPortPlan.Owner> owners, HairPortRecord record,
                                            IReadOnlyList<HairPortRecord.Entry> gone)
    {
        string? failed = null;
        foreach (var owner in owners)
        {
            var drop = owner.Files
                .Where(f => HairMaterialPath.IsOwnName(f.Key) && gone.Any(e => NeedsOwn(e, f.Key))
                            && !record.Ports.Any(p => ReliesOn(p, owner, f.Key, owners)))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
            if (drop.Count == 0) continue;
            try { RemoveRedirects(modRoot, owner.Group, owner.Option, drop); }
            catch (Exception ex) { failed = ex.Message; }
        }
        return failed;
    }

    private static string JoinText(string a, string b) => a.Length == 0 ? b : $"{a} {b}";

    /// <summary>What an Undo says of the ports of build 1430 it took along (target game paths).</summary>
    internal static string AlsoMessage(IEnumerable<string> targets)
        => string.Format(ProteusLoc.Localize("HairPort.Undo.Also.Fmt",
               "Also took out the ports for {0}, which used a material redirect their own hairstyle reads."),
           string.Join(", ", targets.Select(t => HairPortPlan.Parse(t) is { } p ? $"c{p.Race:D4}" : t).Distinct()));

    /// <summary>How a port joined an Undo's set: asked for; sharing a material redirect with what was asked for (and so
    /// on); or holding a game-named redirect in the folder a member's race reads for its own hairstyle — a port of build
    /// 1430's shared set, which would otherwise leave that race's own hairstyle drawn with a port's material.</summary>
    internal enum Join { Picked, Key, Folder }

    /// <summary>
    /// <paramref name="picked"/>, and every port that shares a material redirect with one of them in the same place of
    /// the mod, over and over until nothing more joins (<see cref="Join.Key"/>); then every port of the same place holding
    /// a game-named redirect in the folder a member's race reads for its hairstyle (<see cref="HairMaterialPath.ReadBy"/>),
    /// and what shares with those, over and over (<see cref="Join.Folder"/>). A port that joins both ways is Key.
    /// </summary>
    internal static List<(HairPortRecord.Entry Entry, Join How)> SharerSet(HairPortRecord record,
                                                                           IEnumerable<HairPortRecord.Entry> picked)
    {
        var set = picked.Select(p => (Entry: p, How: Join.Picked)).ToList();
        Grow(Join.Key, SharesKey);
        Grow(Join.Folder, (e, p) => SharesKey(e, p) || InReadFolder(e, p));
        return set;

        void Grow(Join how, Func<HairPortRecord.Entry, HairPortRecord.Entry, bool> joins)
        {
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var p in record.Ports)
                {
                    if (set.Any(s => s.Entry == p)) continue;
                    if (!set.Any(s => HairPortPlan.Same(s.Entry.Owner, p.Owner) && joins(s.Entry, p))) continue;
                    set.Add((p, how));
                    grew = true;
                }
            }
        }

        static bool SharesKey(HairPortRecord.Entry e, HairPortRecord.Entry p) => e.Materials.Keys.Any(p.Materials.ContainsKey);

        static bool InReadFolder(HairPortRecord.Entry e, HairPortRecord.Entry p)
            => HairPortPlan.Parse(e.TargetGamePath) is { } t
               && p.Materials.Keys.Any(k => HairMaterialPath.ReadBy(t.Race, t.Id, k));
    }

    /// <summary>A test seam: when set, called before each redirect removal an Undo makes, with the option and what goes;
    /// it may throw to stand for a manifest that cannot be written. Per thread. Never set outside tests.</summary>
    [ThreadStatic] internal static Action<string, IReadOnlyDictionary<string, string>>? BeforeRemove;

    private static void RemoveRedirects(string modRoot, string group, string option, IReadOnlyDictionary<string, string> remove,
                                        Func<JsonElement, bool>? dropManipulation = null)
    {
        if (remove.Count == 0 && dropManipulation == null) return;
        BeforeRemove?.Invoke(option, remove);
        PenumbraModMeta.RemoveOptionRedirects(modRoot, group, option, remove, dropManipulation);
    }

    /// <summary>A test seam: called before each option's redirects are merged, with how many are already in. Per
    /// thread, since tests run in parallel. Never set outside tests.</summary>
    [ThreadStatic] internal static Action<int>? BeforeMerge;

    /// <summary>
    /// The feature that edited a port in place since it was written — part switches, the brush or a matcap, which each
    /// keep a backup — or null. A matcap counts only while the port still draws one of its own (an unreadable matcap
    /// record counts: never overwritten by mistake); a retired matcap backup never does. Hat fitting is not one: it is
    /// redone on whatever the port becomes. An edited port is neither remade nor undone until that edit is undone.
    /// </summary>
    internal static string? EditedBy(string modRoot, string rel)
    {
        foreach (var (subdir, name) in new[]
                 {
                     (MeshToggleService.BackupSubdir, ProteusLoc.Localize("MeshVolume.Feature.Toggles", "part switches")),
                     (MeshVolumeService.BackupSubdir, ProteusLoc.Localize("MeshVolume.Feature.Brush", "the brush")),
                 })
            if (File.Exists(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, subdir, NativeRel(rel))))
                return name;
        if (File.Exists(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, Matcap.MatcapWriter.BackupSubdir, NativeRel(rel)))
            && Matcap.MatcapWriter.NamesOwnMatcap(modRoot, rel.Replace('\\', '/')))
            return ProteusLoc.Localize("MeshVolume.Feature.Matcaps", "matcaps");
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
        => owner.IsDefault ? ProteusLoc.Localize("HairPort.DefaultData", "the mod's default files") : $"{owner.Group} / {owner.Option}";

    internal static string Combining(string group)
        => string.Format(ProteusLoc.Localize("HairPort.Refused.Combining.Fmt",
            "the hairstyle is in \"{0}\", a combining group, which Proteus does not edit"), group);

    internal static string Unreadable
        => ProteusLoc.Localize("HairPort.Refused.Unreadable", "the mod's manifest could not be read");
}
