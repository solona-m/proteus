using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CheapLoc;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// Works out every port one hairstyle of a mod needs for a set of races, ready for <see cref="HairPortWriter.Save"/>:
/// one per place in the mod that publishes the hairstyle, so each of the author's options gets its own. Reads the mod
/// and the game, writes nothing. Shared by the automatic path and Studio, so the two make the same port.
/// </summary>
internal static class HairPortBuilder
{
    /// <param name="Writes">The ports to save — among them, for a race refused, any old port of it saved again without
    /// the skeleton it wrongly carried (<see cref="HairPortWriter.PortWrite.Stripped"/>).</param>
    /// <param name="Refused">Races that get no port, with why.</param>
    /// <param name="Current">Races already ported, up to date.</param>
    /// <param name="Unreadable">Refused because a file of the mod — its manifest, its port record, the model, its own
    /// skeleton — or the game's table of hairstyle skeletons could not be read (held open elsewhere, mid-update): no
    /// verdict on the model, the next look may read it.</param>
    /// <param name="EditBlocked">Races refused because the user edited their port since (the brush, part switches or a
    /// matcap of its own), and the races sharing such a race's material, refused with it: no verdict on the model either —
    /// reverting the edit lets the next look port them.</param>
    /// <param name="Warnings">Races ported all the same with something to look at in game — a port that does not sit on
    /// the race's head, or a bend that turned part of the hairstyle inside out
    /// (<see cref="HairPortBaker.Result.Warning"/>) — with what.</param>
    /// <param name="Replaced">Races whose write replaces a port another hairstyle of the mod made in the same place
    /// (#71), with that hairstyle's game path.</param>
    public sealed record Plan(IReadOnlyList<HairPortWriter.PortWrite> Writes, IReadOnlyDictionary<ushort, string> Refused,
                              IReadOnlySet<ushort> Current, bool Unreadable = false, IReadOnlySet<ushort>? EditBlocked = null,
                              IReadOnlyDictionary<ushort, string>? Warnings = null,
                              IReadOnlyDictionary<ushort, string>? Replaced = null)
    {
        /// <summary>Whether a race's refusal is the model's own verdict, which the same model would get again.</summary>
        public bool IsVerdict(ushort race) => !Unreadable && EditBlocked?.Contains(race) != true;
    }

    /// <summary>
    /// The ports of the mod's hairstyle at <paramref name="sourceGamePath"/> for <paramref name="targets"/>, each moving
    /// with the skeleton its race loads for the hairstyle — given one that has the bones where it does not
    /// (<see cref="HairPortSkeleton"/>).
    /// </summary>
    /// <param name="readGame">Reads a file from the game's own data; null when the game has no such file.</param>
    /// <param name="withSharers">Also port to every race that would read the same material as a target (see
    /// <see cref="Sharers"/>) — what the automatic path does, so redirecting a shared material never dresses other
    /// races' own hairstyles in it; a race whose port the user undid is refused, and with it every race it shares with.
    /// Off — a port asked for by hand — a target whose sharers are not all ported is refused. On, it is also the
    /// automatic path's mark for races of the other sex: none gets a new port, and one the Studio made is remade only in
    /// the places that already have it (<see cref="HasPrior"/>); by hand, the other sex may be ported to.</param>
    /// <param name="progress">Called with (done, total) after each race.</param>
    public static Plan Build(string modRoot, string sourceGamePath, IEnumerable<ushort> targets, PbdFile pbd,
                             Func<string, byte[]?> readGame, bool withSharers, CancellationToken ct = default,
                             Action<int, int>? progress = null)
    {
        var writes = new List<HairPortWriter.PortWrite>();
        var refused = new Dictionary<ushort, string>();
        var current = new HashSet<ushort>();
        var warnings = new Dictionary<ushort, string>();
        var replaced = new Dictionary<ushort, string>();
        var wanted = targets.Distinct().ToList();
        Dictionary<ushort, string> All(string why) => wanted.ToDictionary(t => t, _ => why);

        if (HairPortPlan.Parse(sourceGamePath) is not { } parsed)
            return new Plan(writes, All(ProteusLoc.Localize("HairPort.Refused.NotHair",
                "it is not a hairstyle model")), current);
        var (from, id) = parsed;
        if (PenumbraModMeta.IsLegacyFolder(modRoot))
            return new Plan(writes, All(new PenumbraModMeta.LegacyFolderException(Path.GetFileName(modRoot)).Message), current);
        if (HairPortPlan.ReadOwners(modRoot) is not { } owners)
            return new Plan(writes, All(HairPortWriter.Unreadable), current, Unreadable: true);

        var sv = Serving(modRoot, owners, sourceGamePath);
        if (sv.Refusal != null) return new Plan(writes, All(sv.Refusal), current, Unreadable: sv.Unreadable);
        var serving = sv.Owners;
        var sourceOf = sv.SourceOf;
        if (serving.Count == 0)
            return new Plan(writes, All(ProteusLoc.Localize("HairPort.Refused.NotInMod",
                "the mod does not publish that hairstyle")), current);

        var authored = HairPortPlan.AuthoredRaces(owners, id).ToHashSet();
        var record = HairPortRecord.Read(modRoot);
        if (record.Unreadable)
            return new Plan(writes, All(HairPortRecord.UnreadableMessage), current, Unreadable: true);
        var sources = serving.Select(o => sourceOf[o]).Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(rel => rel, rel => ReadMod(modRoot, rel), StringComparer.OrdinalIgnoreCase);
        if (sources.Values.Any(b => b == null))
            return new Plan(writes, All(ProteusLoc.Localize("Parts.Retarget.Bake.Unreadable",
                "the model could not be read")), current, Unreadable: true);

        // Which races a target's materials drag along: every race reading the same material file.
        var names = sources.Values.SelectMany(b => SecondSkinWriter.Parse(b!).MatNames).Distinct().ToList();
        var wears = new Dictionary<ushort, bool>();
        bool canWear(ushort race) => wears.TryGetValue(race, out var w) ? w
                                   : wears[race] = readGame(HairPortPlan.HairPath(race, id)) != null;
        if (withSharers)
            foreach (var t in wanted.ToList())
                foreach (var s in Sharers(from, t, id, names, canWear))
                    if (!wanted.Contains(s) && !authored.Contains(s)) wanted.Add(s);

        var sourceHead = FirstFile(HairPortPlan.HeadPaths(from), readGame);
        // Read once, and only when a port needs to know which skeleton a race loads.
        var table = HairSkeletonTable.Lazy(readGame);
        bool noVerdict = false;
        var editBlocked = new HashSet<ushort>();
        int done = 0;
        foreach (ushort to in wanted)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(done++, wanted.Count);
            if (to == from || !HairPortPlan.Playable(to))
            {
                refused[to] = ProteusLoc.Localize("HairPort.Refused.NotTarget", "it is the hairstyle's own race, or not a playable one");
                continue;
            }
            if (authored.Contains(to)) continue;   // the author made one: never overwritten
            string target = HairPortPlan.HairPath(to, id);
            // An undone port is not made again by itself — not even as another race's sharer. Asked for by hand, it is.
            if (withSharers && record.Declined.Contains(HairPortPlan.DeclineKey(target)))
            {
                refused[to] = ProteusLoc.Localize("HairPort.Refused.Declined", "its port was undone, so it is left alone");
                continue;
            }
            // Across the sexes: the author's own model for the target's sex is the look they meant for it; and the
            // automatic path only keeps a port the Studio made current, in the places that have it — it never makes one.
            bool cross = CrossSexRefit.CrossesSex(from, to);
            if (cross && authored.FirstOrDefault(r => !CrossSexRefit.CrossesSex(r, to)) is var mine and > 0)
            {
                refused[to] = string.Format(withSharers
                    ? ProteusLoc.Localize("HairPort.Refused.OtherSexAuthoredAuto.Fmt",
                        "the mod now has its own hairstyle {1} for c{0:D4}; the port made in the Studio is left as it is")
                    : ProteusLoc.Localize("HairPort.Refused.OtherSexAuthored.Fmt",
                        "the mod has its own hairstyle {1} for c{0:D4}; port that one instead"), mine, id);
                continue;
            }
            if (cross && withSharers && !serving.Any(o => HasPrior(record, o, target, sourceGamePath)))
            {
                refused[to] = OtherSexAuto;
                continue;
            }
            if (readGame(target) == null)
            {
                refused[to] = string.Format(ProteusLoc.Localize("HairPort.Refused.NoStyle.Fmt",
                    "c{0:D4} cannot choose hairstyle {1}"), to, id);
                continue;
            }
            // A material only other, unported races would also read is not ours to redirect.
            var stranded = Sharers(from, to, id, names, canWear).Where(s => !wanted.Contains(s) && !authored.Contains(s)
                                                                   && !Ported(record, HairPortPlan.HairPath(s, id))).ToList();
            if (stranded.Count > 0)
            {
                // Advice to port the sharers too is no use when the race itself would be refused for its bones (#72).
                refused[to] = BonesFirst(modRoot, owners, serving, sourceOf, sources, record, from, to, id, cross, withSharers,
                                         sourceGamePath, readGame, table)
                              ?? string.Format(ProteusLoc.Localize("HairPort.Refused.SharedMaterial.Fmt",
                                  "its material is the one {0} read for their own hairstyle {1} — port to them too"),
                                  string.Join(", ", stranded.Select(s => $"c{s:D4}")), id);
                continue;
            }

            var targetHead = FirstFile(HairPortPlan.HeadPaths(to), readGame);

            var made = new Dictionary<string, HairPortBaker.Result>(StringComparer.OrdinalIgnoreCase);
            bool anyCurrent = false;
            string? why = null;
            // The slot a carried skeleton of this target went to: every place's port of it uses the same one.
            int slot = 0;
            foreach (var owner in serving)
            {
                string rel = sourceOf[owner];
                var bytes = sources[rel]!;
                var was = record.Ports.FirstOrDefault(e => HairPortPlan.Same(e.Owner, owner) && SamePath(e.TargetGamePath, target));
                // By hand, another hairstyle's port of the target in this place is replaced, whatever its bytes (#71):
                // its bend followed another race's chain, and the user asked for this one.
                bool otherSource = was != null && !SamePath(was.SourceGamePath, sourceGamePath);
                if (was != null && !(otherSource && !withSharers)
                    && !IsOutOfDate(modRoot, was, bytes, owners, record.Ports, readGame, table)) { anyCurrent = true; continue; }
                // A remake by the automatic path goes only where the Studio put a port across the sexes before.
                if (withSharers && cross && !HasPrior(record, owner, target, sourceGamePath)) continue;
                if (was == null && !HairPortPlan.CanPortInto(owners, owner, target)) continue;
                // The author gave the target race a skeleton of their own: their chain for their own style of that sex,
                // which a port from the other sex cannot be checked against, nor bring its own beside.
                if (cross && AuthorTargetEst(owner, owners.FirstOrDefault(o => o.IsDefault), was, to, id,
                                             DefaultsPortEst(record.Ports, target)) != null)
                {
                    why = string.Format(ProteusLoc.Localize("HairPort.Refused.OtherSexSkeleton.Fmt",
                        "the mod gives c{0:D4} a skeleton of its own for hairstyle {1}, so a port from the other sex cannot bring its own"),
                        to, id);
                    break;
                }

                var place = new HairPortSkeleton.Place(modRoot, owners, owner, was, record.Ports, from, to, id, readGame, table);
                // A port the user edited since cannot be remade: refused for its race alone — the save would refuse the
                // whole lot — and one whose skeleton changes another hairstyle is stripped of it, below, the edit kept.
                if (was != null && HairPortWriter.EditedBy(modRoot, was.File) is { } edited)
                {
                    why = ModelBackupOrder.BlockedMessage(edited);
                    editBlocked.Add(to);
                    break;
                }
                var prep = PrepareOwner(place, record, bytes, from, to, id, slot);
                var choice = prep.Choice;
                if (choice.Route == HairPortSkeleton.Route.Unknown) { why = choice.Why ?? NoSkeletonTable; noVerdict = true; break; }
                IReadOnlySet<string> bones = prep.Bones;
                int est = prep.Est, skeletonFrom = prep.SkeletonFrom;
                IReadOnlyList<HairPortWriter.MaterialWrite> skeletonFiles = prep.SkeletonFiles;
                slot = prep.Slot;
                if (prep.BonesWhy is { } lacking) { why = lacking; break; }

                // One bend per distinct file and skeleton checked against, however many options publish it.
                string key = $"{rel}|{est}|{choice.Check}|{skeletonFrom}";
                if (!made.TryGetValue(key, out var result))
                {
                    result = HairPortBaker.Prepare(bytes, from, to, id, pbd, bones, sourceHead, targetHead, out var r)!;
                    if (result == null) { why = r; break; }
                    made[key] = result;
                }

                var materials = Materials(modRoot, owners, owner, bytes, from, to, id, readGame, out var missing);
                if (missing != null) { why = missing; break; }
                // A material of the port's own name is a file the game does not ship: published nowhere the port is on,
                // the hair draws invisible. Refused for its race instead.
                if (OwnMaterialUnpublishable(owners, owner, record, result.Model, to, id, materials) is { } covered)
                {
                    why = HairPortWriter.Covered(covered);
                    break;
                }
                writes.Add(new HairPortWriter.PortWrite(owner, sourceGamePath, rel, HairPortPlan.Sha8(bytes), target,
                                                        result.Model, [.. materials, .. skeletonFiles], result.Millis, est,
                                                        skeletonFrom, Pushed: result.Pushed, PushWorst: result.PushWorst,
                                                        NoScalp: result.NoScalp));
                if (otherSource) replaced[to] = was!.SourceGamePath;
                if (result.Warning is { } warning) warnings[to] = warning;
            }
            if (why != null)
            {
                writes.RemoveAll(w => w.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase));
                refused[to] = why;
            }
            else if (anyCurrent && !writes.Any(w => w.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase)))
                current.Add(to);
        }
        progress?.Invoke(wanted.Count, wanted.Count);

        // Sharers go together or not at all: one refused leaves the material it needed unredirected for the rest.
        if (withSharers)
            foreach (var to in wanted.Where(refused.ContainsKey).ToList())
                foreach (var s in Sharers(from, to, id, names, canWear).Where(s => !refused.ContainsKey(s)))
                {
                    writes.RemoveAll(w => w.TargetGamePath.Equals(HairPortPlan.HairPath(s, id), StringComparison.OrdinalIgnoreCase));
                    // Refused for another's edit, it is no verdict on the model either.
                    if (editBlocked.Contains(to)) editBlocked.Add(s);
                    refused[s] = string.Format(ProteusLoc.Localize("HairPort.Refused.SharerRefused.Fmt",
                        "it shares its material with c{0:D4}, which could not be ported: {1}"), to, refused[to]);
                }

        // Every race refused, however: a port of it from before that carries a skeleton another hairstyle of the race
        // loads stops doing that.
        foreach (var to in refused.Keys)
            writes.AddRange(Strips(modRoot, owners, record, from, to, id, readGame, table));

        foreach (var to in refused.Keys) warnings.Remove(to);
        foreach (var to in refused.Keys) replaced.Remove(to);
        return new Plan(writes, refused, current, Unreadable: noVerdict, EditBlocked: editBlocked, Warnings: warnings,
                        Replaced: replaced);
    }

    /// <summary>
    /// The places that publish the hairstyle at <paramref name="sourceGamePath"/> from the author's own file — or from an
    /// earless copy of it, which is ported from the author's file it was made from (a Miqo'te's ears come out of a port to
    /// a race without them anyway, and the port then never goes out of date for the earless setting being switched) — with
    /// the file each is ported from. Shared by <see cref="Build"/> and the Studio's list (#71), which must agree on which
    /// places a port of another hairstyle sits in.
    /// </summary>
    /// <returns>A <see cref="ServingResult.Refusal"/> for the whole hairstyle: a combining group publishes it (its owners
    /// kept), the earless record is unreadable (owners kept, <see cref="ServingResult.Unreadable"/>), or every earless
    /// copy's author file is gone (no owners).</returns>
    internal static ServingResult Serving(string modRoot, IReadOnlyList<HairPortPlan.Owner> owners, string sourceGamePath)
    {
        var serving = owners.Where(o => HairPortPlan.FileFor(o, sourceGamePath) is { } rel && !HairPortPlan.IsPortFile(rel))
                            .ToList();
        var sourceOf = new Dictionary<HairPortPlan.Owner, string>(ReferenceEqualityComparer.Instance);
        if (serving.FirstOrDefault(o => o.IsCombining) is { } combining)
            return new ServingResult(serving, sourceOf, HairPortWriter.Combining(combining.Group), false);
        if (serving.Any(o => EarlessHairPlan.IsEarlessFile(HairPortPlan.FileFor(o, sourceGamePath)!)))
        {
            var earless = EarlessHairRecord.Read(modRoot);
            if (earless.Unreadable)
                return new ServingResult(serving, sourceOf, EarlessHairRecord.UnreadableMessage, true);
            var exists = EarlessHairPlan.Exists(modRoot);
            string? missing = null;
            foreach (var o in serving)
            {
                var rel = HairPortPlan.FileFor(o, sourceGamePath)!;
                if (EarlessHairPlan.AuthorFileFor(earless, o, sourceGamePath, rel, exists) is { } author && exists(author))
                    sourceOf[o] = author;
                else missing = EarlessHairBuilder.AuthorMissing(EarlessHairPlan.AuthorOfCopy(rel));
            }
            // A copy whose author's file is gone has nothing to port from: that place gets no port — a verdict, since the
            // same mod would get it again.
            serving.RemoveAll(o => !sourceOf.ContainsKey(o));
            if (serving.Count == 0 && missing != null)
                return new ServingResult(serving, sourceOf, missing, false);
        }
        else
            foreach (var o in serving) sourceOf[o] = HairPortPlan.FileFor(o, sourceGamePath)!;
        return new ServingResult(serving, sourceOf, null, false);
    }

    /// <param name="Owners">The places serving the hairstyle.</param>
    /// <param name="SourceOf">The file each is ported from.</param>
    /// <param name="Refusal">Why the hairstyle gets no port at all, or null.</param>
    /// <param name="Unreadable">The refusal is no verdict (a file could not be read).</param>
    internal sealed record ServingResult(List<HairPortPlan.Owner> Owners, Dictionary<HairPortPlan.Owner, string> SourceOf,
                                         string? Refusal, bool Unreadable);

    /// <summary>
    /// One place's skeleton for a port, and whether it has the bones (#72): the choice, the carry to a slot when one is
    /// made, and <see cref="OwnerPrep.BonesWhy"/> when the bones the model moves with are missing all the same. Mutates
    /// nothing — the caller keeps the slot. Shared by <see cref="Build"/>'s loop and <see cref="BonesFirst"/>.
    /// </summary>
    /// <param name="slot">The slot a carried skeleton of this target went to for an earlier place: preferred again.</param>
    internal static OwnerPrep PrepareOwner(HairPortSkeleton.Place place, HairPortRecord record, byte[] bytes, ushort from,
                                           ushort to, int id, int slot)
    {
        var choice = HairPortSkeleton.Choose(place, bytes);
        if (choice.Route == HairPortSkeleton.Route.Unknown) return new OwnerPrep(choice, choice.Bones, 0, 0, [], slot, null);
        IReadOnlySet<string> bones = choice.Bones;
        int est = choice.Route == HairPortSkeleton.Route.Entry ? choice.Check : 0, skeletonFrom = 0;
        IReadOnlyList<HairPortWriter.MaterialWrite> files = [];
        bool carried = false;
        // What the race loads lacks bones the hairstyle moves with, and no skeleton of its own has them: the source
        // race's goes to a slot of the race's that only this hairstyle loads, with an entry pointing there.
        if (HairPortSkeleton.Carry(place, record, bytes, choice, slot) is { } c)
        {
            (files, bones, slot) = c;
            (est, skeletonFrom, carried) = (slot, choice.From, true);
        }
        return new OwnerPrep(choice, bones, est, skeletonFrom, files, slot, BonesRefusal(from, to, id, choice, carried, bones, bytes));
    }

    /// <param name="BonesWhy">Why the port is refused for its bones, or null when it has them all.</param>
    internal sealed record OwnerPrep(HairPortSkeleton.Choice Choice, IReadOnlySet<string> Bones, int Est, int SkeletonFrom,
                                     IReadOnlyList<HairPortWriter.MaterialWrite> SkeletonFiles, int Slot, string? BonesWhy);

    /// <summary>
    /// Why a port moving with <paramref name="model"/>'s hairstyle bones is refused for lacking some of them, or null when
    /// <paramref name="bones"/> has every one. Within a sex, the target race does not have them (the bend's own wording).
    /// Across the sexes only the source race's skeleton can come along, so the refusal names that one (#72): it lacks the
    /// bones itself, or it has them but could not be carried to the target.
    /// </summary>
    internal static string? BonesRefusal(ushort from, ushort to, int id, HairPortSkeleton.Choice choice, bool carried,
                                         IReadOnlySet<string> bones, byte[] model)
    {
        var missing = HairPortSkeleton.Missing(HairPortSkeleton.ExtraBones(model), bones);
        if (missing.Count == 0) return null;
        string list = string.Join(", ", missing);
        if (!CrossSexRefit.CrossesSex(from, to))
            return string.Format(ProteusLoc.Localize("HairPort.Refused.Bones.Fmt",
                "it moves with bones c{0:D4} does not have for this hairstyle ({1})"), to, list);
        return choice.Route == HairPortSkeleton.Route.Carry && !carried
            ? string.Format(ProteusLoc.Localize("HairPort.Refused.BonesCarry.Fmt",
                "it moves with bones only c{0:D4}'s skeleton for hairstyle {2} has ({1}), and that skeleton could not be brought to c{3:D4}"),
                from, list, id, to)
            : string.Format(ProteusLoc.Localize("HairPort.Refused.BonesSourceSkeleton.Fmt",
                "it moves with bones c{0:D4}'s own skeleton for hairstyle {2} does not have ({1}), and only that skeleton can come along to the other sex"),
                from, list, id);
    }

    /// <summary>
    /// For a target the shared-material check would send back with "port to them too": its own bones refusal, when a
    /// place the port would really go to lacks them — advice to port more races cannot help then (#72). Applies the
    /// loop's skips first (a place a port can't go into, the automatic path's places with no prior port across the
    /// sexes), and uses only the bones verdict of <see cref="PrepareOwner"/>.
    /// </summary>
    private static string? BonesFirst(string modRoot, IReadOnlyList<HairPortPlan.Owner> owners, List<HairPortPlan.Owner> serving,
                                      Dictionary<HairPortPlan.Owner, string> sourceOf, Dictionary<string, byte[]?> sources,
                                      HairPortRecord record, ushort from, ushort to, int id, bool cross, bool withSharers,
                                      string sourceGamePath, Func<string, byte[]?> readGame, Func<HairSkeletonTable?> table)
    {
        string target = HairPortPlan.HairPath(to, id);
        foreach (var owner in serving)
        {
            var was = record.Ports.FirstOrDefault(e => HairPortPlan.Same(e.Owner, owner) && SamePath(e.TargetGamePath, target));
            if (withSharers && cross && !HasPrior(record, owner, target, sourceGamePath)) continue;
            if (was == null && !HairPortPlan.CanPortInto(owners, owner, target)) continue;
            var place = new HairPortSkeleton.Place(modRoot, owners, owner, was, record.Ports, from, to, id, readGame, table);
            if (PrepareOwner(place, record, sources[sourceOf[owner]]!, from, to, id, 0).BonesWhy is { } why) return why;
        }
        return null;
    }

    /// <summary>
    /// The first own-named material (<see cref="HairMaterialPath.IsOwnName"/>) the port's model names that would be
    /// published nowhere it is on — not redirected among <paramref name="materials"/>, or not publishable from
    /// <paramref name="owner"/> while the default data does not publish it either — or null when every one will be.
    /// </summary>
    internal static string? OwnMaterialUnpublishable(IReadOnlyList<HairPortPlan.Owner> owners, HairPortPlan.Owner owner,
                                                     HairPortRecord record, byte[] model, ushort to, int id,
                                                     IReadOnlyList<HairPortWriter.MaterialWrite> materials)
    {
        foreach (var name in SecondSkinWriter.Parse(model).MatNames.Distinct())
        {
            if (!HairMaterialPath.IsOwnName(name) || HairMaterialPath.GamePath(to, id, name) is not { } path) continue;
            if (!materials.Any(m => m.GamePath.Equals(path, StringComparison.OrdinalIgnoreCase))) return path;
            if (!HairPortWriter.MayPublish(owners, owner, record, path) && !HairPortWriter.PublishedByDefaults(owners, owner, path))
                return path;
        }
        return null;
    }

    /// <summary>Why the automatic path makes no port across the sexes.</summary>
    internal static string OtherSexAuto => ProteusLoc.Localize("HairPort.Refused.OtherSexAuto",
        "it is of the other sex, which is only ported when asked for in the Studio");

    /// <summary>Whether the record holds a port of <paramref name="target"/> from <paramref name="source"/> in
    /// <paramref name="owner"/> — paths compared without regard to case or slashes, as everywhere else here.</summary>
    internal static bool HasPrior(HairPortRecord record, HairPortPlan.Owner owner, string target, string source)
        => record.Ports.Any(e => HairPortPlan.Same(e.Owner, owner) && SamePath(e.TargetGamePath, target)
                                 && SamePath(e.SourceGamePath, source));

    private static bool SamePath(string a, string b)
        => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a port that needs the game's table of hairstyle skeletons is refused without it.</summary>
    internal static string NoSkeletonTable
        => ProteusLoc.Localize("HairPort.Refused.NoSkeletonTable", "the game's table of hairstyle skeletons could not be read");

    /// <summary>
    /// For a race that could not be ported again: each port of it that carries a skeleton to a slot another hairstyle of
    /// the race loads (<see cref="HairPortSkeleton.Harmful"/>), saved again without that skeleton, its physics and the
    /// entry pointing there — the model, its materials and its hat fit kept — so it stops changing that hairstyle.
    /// </summary>
    private static IEnumerable<HairPortWriter.PortWrite> Strips(string modRoot, IReadOnlyList<HairPortPlan.Owner> owners,
        HairPortRecord record, ushort from, ushort to, int id, Func<string, byte[]?> readGame, Func<HairSkeletonTable?> table)
    {
        string target = HairPortPlan.HairPath(to, id);
        var defaults = owners.FirstOrDefault(o => o.IsDefault);
        foreach (var e in record.Ports.Where(e => e.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            if (owners.FirstOrDefault(o => HairPortPlan.Same(o, e.Owner)) is not { } owner) continue;
            // Each port judged from its own source race: a build from one source may refuse a race another source's
            // port of it sits in.
            ushort src = HairPortPlan.Parse(e.SourceGamePath)?.Race ?? from;
            var place = new HairPortSkeleton.Place(modRoot, owners, owner, e, record.Ports, src, to, id, readGame, table);
            // Across the sexes, a carry beside a skeleton entry the author has since given the race is taken out too:
            // two entries for one race in one place leave Penumbra's pick undefined.
            bool authorsSkeleton = CrossSexRefit.CrossesSex(src, to) && e.SkeletonFrom > 0
                && AuthorTargetEst(owner, defaults, e, to, id, DefaultsPortEst(record.Ports, target)) != null;
            if (!HairPortSkeleton.Harmful(place, e) && !authorsSkeleton) continue;
            int slot = e.Est > 0 ? e.Est : id;
            var skeleton = new[] { HairPortPlan.SkeletonPath(to, slot), HairPortPlan.PhysicsPath(to, slot) };
            var kept = e.Materials.Where(m => !skeleton.Contains(m.Key, StringComparer.OrdinalIgnoreCase))
                                  .Select(m => new HairPortWriter.MaterialWrite(m.Key, m.Value, null)).ToList();
            yield return new HairPortWriter.PortWrite(owner, e.SourceGamePath, e.SourceFile, e.SourceSha8, e.TargetGamePath,
                                                      [], kept, Stripped: true, StrippedFrom: (e.Est, e.SkeletonFrom));
        }
    }

    /// <summary>
    /// Which extra skeleton a port to <paramref name="to"/> moves with, and which it must carry. Physics hair borrows
    /// another hairstyle's skeleton through an <c>Est</c> entry — the source race's, from the same place or the default
    /// data. The target race has that skeleton of its own; the port carries the entry for it. Unless the author gave
    /// the target race an entry of their own in a place always on with the port (the same place, or the default
    /// data): then that skeleton is the one the port is checked against, and nothing is carried. The record tells an
    /// entry this port added before — remade, it is carried again — from the author's. The mod's entries only: what the
    /// game's table gives a race without them, and so what a port really moves with, is
    /// <see cref="HairPortSkeleton.Choose"/>'s — which also takes the source's entry from the mod's other groups
    /// (<see cref="HairPortPlan.SourceEst"/>); this reads the port's place and the default data alone.
    /// </summary>
    /// <returns>The skeleton to check the bones against (0: none the mod names), and the entry to carry (0: none).</returns>
    /// <param name="defaultsOurs">The skeleton a port of ours in the default data carries for the same target (0 for
    /// none): with the hairstyle served from both, that entry is ours too, never the author's.</param>
    internal static (int Check, int Carry) SkeletonFor(HairPortPlan.Owner owner, HairPortPlan.Owner? defaults,
                                                       HairPortRecord.Entry? was, ushort from, ushort to, int id,
                                                       int defaultsOurs = 0)
    {
        int? source = HairPortPlan.SourceEst(owner, defaults, from, id);
        if (AuthorTargetEst(owner, defaults, was, to, id, defaultsOurs) is { } author) return (author, 0);
        return source is > 0 and var s ? (s, s) : (0, 0);
    }

    /// <summary>
    /// The author's entry for the target race: in the port's own place any there that is not ours from
    /// <paramref name="was"/>, wherever it sits among them; else, for an option's port, the default data's unless it is
    /// the one our default-data port carries. Null for none.
    /// </summary>
    internal static int? AuthorTargetEst(HairPortPlan.Owner owner, HairPortPlan.Owner? defaults, HairPortRecord.Entry? was,
                                         ushort to, int id, int defaultsOurs)
    {
        int? target = HairPortPlan.HairEsts(owner, to, id).Where(e => !(was is { Est: > 0 } && was.Est == e))
                                  .Cast<int?>().FirstOrDefault();
        if (target == null && defaults != null && !owner.IsDefault)
            target = HairPortPlan.HairEsts(defaults, to, id).Where(e => !(defaultsOurs > 0 && e == defaultsOurs))
                                 .Cast<int?>().FirstOrDefault();
        return target;
    }

    /// <summary>
    /// Whether a port must be remade: its source model changed, it was made by an older bend, or the skeleton it moves
    /// with is no longer the one it should (<see cref="HairPortSkeleton.Current"/>) — authors change a physics borrow in
    /// the manifest, not the model, and ports made before the game's table of hairstyle skeletons was read may carry
    /// theirs where the race does not load it. The one test the builder, the automatic path and Studio share, so none of
    /// them calls a port current the others would remake.
    /// </summary>
    /// <param name="owners">The mod's places as read now; null when the manifest could not be read, which judges by
    /// the model alone.</param>
    /// <param name="ports">Every port the mod's record holds, to tell a skeleton entry of ours in the default data from
    /// the author's.</param>
    /// <param name="modRoot">The mod, whose own skeleton files an entry may point at.</param>
    /// <param name="readGame">Reads the game's own files: its skeletons, and its table of them.</param>
    internal static bool IsOutOfDate(string modRoot, HairPortRecord.Entry entry, byte[] source,
                                     IReadOnlyList<HairPortPlan.Owner>? owners, IReadOnlyList<HairPortRecord.Entry> ports,
                                     Func<string, byte[]?> readGame)
        => IsOutOfDate(modRoot, entry, source, owners, ports, readGame, HairSkeletonTable.Lazy(readGame));

    /// <inheritdoc cref="IsOutOfDate(string, HairPortRecord.Entry, byte[], IReadOnlyList{HairPortPlan.Owner}?, IReadOnlyList{HairPortRecord.Entry}, Func{string, byte[]?})"/>
    /// <param name="table">The game's table, read at most once for every port a caller judges.</param>
    internal static bool IsOutOfDate(string modRoot, HairPortRecord.Entry entry, byte[] source,
                                     IReadOnlyList<HairPortPlan.Owner>? owners, IReadOnlyList<HairPortRecord.Entry> ports,
                                     Func<string, byte[]?> readGame, Func<HairSkeletonTable?> table)
    {
        if (CrossSexVerdict(modRoot, entry, owners, ports, readGame, table) is { } settled) return settled;
        if (HairPortPlan.IsStale(entry, source)) return true;
        if (owners?.FirstOrDefault(o => HairPortPlan.Same(o, entry.Owner)) is not { } owner) return false;
        if (HairPortPlan.Parse(entry.SourceGamePath) is not { } src || HairPortPlan.Parse(entry.TargetGamePath) is not { } dst)
            return false;
        var place = new HairPortSkeleton.Place(modRoot, owners, owner, entry, ports, src.Race, dst.Race, dst.Id, readGame, table);
        return !HairPortSkeleton.Current(place, entry, source);
    }

    /// <summary>
    /// A port across the sexes the mod has since given its own answer to, which is left as it is whatever its source
    /// does — judged before the source, so an author's update never brings it back as a refusal on every look. The
    /// author gave the target race a skeleton entry of their own: out of date only while the port still carries one,
    /// so the build strips it once (<see cref="Strips"/>). The mod has its own model for the target's sex: out of date
    /// only while its carried skeleton harms another hairstyle of the race (<see cref="HairPortSkeleton.Harmful"/>),
    /// the same once-only strip. Null for every other port, which the usual tests judge.
    /// </summary>
    private static bool? CrossSexVerdict(string modRoot, HairPortRecord.Entry entry, IReadOnlyList<HairPortPlan.Owner>? owners,
                                         IReadOnlyList<HairPortRecord.Entry> ports, Func<string, byte[]?> readGame,
                                         Func<HairSkeletonTable?> table)
    {
        if (owners?.FirstOrDefault(o => HairPortPlan.Same(o, entry.Owner)) is not { } owner) return null;
        if (HairPortPlan.Parse(entry.SourceGamePath) is not { } src || HairPortPlan.Parse(entry.TargetGamePath) is not { } dst
            || !CrossSexRefit.CrossesSex(src.Race, dst.Race)) return null;
        if (AuthorTargetEst(owner, owners.FirstOrDefault(o => o.IsDefault), entry, dst.Race, dst.Id,
                            DefaultsPortEst(ports, entry.TargetGamePath)) != null)
            return entry.SkeletonFrom > 0;
        if (HairPortPlan.AuthoredRaces(owners, dst.Id).Any(r => !CrossSexRefit.CrossesSex(r, dst.Race)))
            return HairPortSkeleton.Harmful(new HairPortSkeleton.Place(modRoot, owners, owner, entry, ports, src.Race,
                                                                       dst.Race, dst.Id, readGame, table), entry);
        return null;
    }

    /// <summary>
    /// A file the source race reads at <paramref name="source"/>, to publish at <paramref name="target"/>: the author's
    /// own when the same place (or the default data) publishes it, otherwise a copy of another option's or the game's.
    /// Null when there is none; the bytes as read, null for the author's own when it could not be read.
    /// </summary>
    internal static (HairPortWriter.MaterialWrite? Write, byte[]? Bytes) Carry(string modRoot,
        IReadOnlyList<HairPortPlan.Owner> owners, HairPortPlan.Owner owner, string source, string target,
        Func<string, byte[]?> readGame)
    {
        var defaults = owners.FirstOrDefault(o => o.IsDefault);
        var author = HairPortPlan.FileFor(owner, source) ?? (defaults != null ? HairPortPlan.FileFor(defaults, source) : null);
        if (author != null) return (new HairPortWriter.MaterialWrite(target, author, null), ReadMod(modRoot, author));

        var elsewhere = owners.Select(o => HairPortPlan.FileFor(o, source)).FirstOrDefault(r => r != null);
        var bytes = elsewhere != null ? ReadMod(modRoot, elsewhere) : readGame(source);
        return bytes == null ? (null, null) : (new HairPortWriter.MaterialWrite(target, null, bytes), bytes);
    }

    /// <summary>The skeleton the default data's port of ours carries for a target, or 0.</summary>
    internal static int DefaultsPortEst(IReadOnlyList<HairPortRecord.Entry> ports, string target)
        => ports.FirstOrDefault(p => p.Group.Length == 0
                                     && p.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase))?.Est ?? 0;

    /// <summary>
    /// The other races that read the material file a port to <paramref name="to"/> would have to redirect: none when
    /// the port reads the same file the source does. Miqo'te hairstyles 101–115 are the case — everyone else of that sex
    /// reads one Midlander material for them.
    /// </summary>
    /// <param name="canWear">Whether the game gives a race this hairstyle at all; a race that cannot choose it never
    /// reads its material. Null counts every race.</param>
    public static IEnumerable<ushort> Sharers(ushort from, ushort to, int id, IReadOnlyList<string> names,
                                              Func<ushort, bool>? canWear = null)
    {
        var shared = new SortedSet<ushort>();
        foreach (var name in names)
        {
            var source = HairMaterialPath.GamePath(from, id, name);
            var target = HairMaterialPath.GamePath(to, id, HairMaterialPath.PortName(name, from, to, id));
            if (target == null || string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
            // The target's sex: across the sexes, the races that read the target's material are its own sex's.
            foreach (var other in HairPortPlan.SameGenderRaces(to).Where(r => r != from && canWear?.Invoke(r) != false))
                if (string.Equals(HairMaterialPath.GamePath(other, id, HairMaterialPath.RenameRace(name, other)), target,
                                  StringComparison.OrdinalIgnoreCase))
                    shared.Add(other);
        }
        return shared;
    }

    /// <summary>
    /// The material redirects a port published from <paramref name="owner"/> needs: for each material whose file differs
    /// between the races, the target's path pointing at what the source race reads there — the author's file when the
    /// same place (or the default data) publishes it, otherwise a copy of what it resolves to.
    /// </summary>
    private static List<HairPortWriter.MaterialWrite> Materials(string modRoot, IReadOnlyList<HairPortPlan.Owner> owners,
        HairPortPlan.Owner owner, byte[] source, ushort from, ushort to, int id, Func<string, byte[]?> readGame,
        out string? missing)
    {
        missing = null;
        var result = new List<HairPortWriter.MaterialWrite>();
        foreach (var name in SecondSkinWriter.Parse(source).MatNames.Distinct())
        {
            var s = HairMaterialPath.GamePath(from, id, name);
            var t = HairMaterialPath.GamePath(to, id, HairMaterialPath.PortName(name, from, to, id));
            if (s == null || t == null || string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) continue;

            if (Carry(modRoot, owners, owner, s, t, readGame).Write is not { } write)
            {
                missing = string.Format(ProteusLoc.Localize("HairPort.Refused.NoMaterial.Fmt",
                    "its material {0} could not be found"), s);
                return result;
            }
            result.Add(write);
        }
        return result;
    }

    private static bool Ported(HairPortRecord record, string target)
        => record.Ports.Any(e => e.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase));

    private static byte[]? FirstFile(IEnumerable<string> paths, Func<string, byte[]?> read)
        => paths.Select(read).FirstOrDefault(b => b != null);

    internal static byte[]? ReadMod(string modRoot, string rel)
    {
        try { return File.ReadAllBytes(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))); }
        catch (Exception) { return null; }
    }
}
