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
    /// <param name="Writes">The ports to save.</param>
    /// <param name="Refused">Races that get no port, with why.</param>
    /// <param name="Current">Races already ported, up to date.</param>
    /// <param name="Unreadable">Refused because a file of the mod — its manifest, its port record, the model — could
    /// not be read (held open elsewhere, mid-update): no verdict on the model, the next look may read it.</param>
    public sealed record Plan(IReadOnlyList<HairPortWriter.PortWrite> Writes, IReadOnlyDictionary<ushort, string> Refused,
                              IReadOnlySet<ushort> Current, bool Unreadable = false);

    /// <summary>
    /// The ports of the mod's hairstyle at <paramref name="sourceGamePath"/> for <paramref name="targets"/>.
    /// </summary>
    /// <param name="readGame">Reads a file from the game's own data; null when the game has no such file.</param>
    /// <param name="withSharers">Also port to every race that would read the same material as a target (see
    /// <see cref="Sharers"/>) — what the automatic path does, so redirecting a shared material never dresses other
    /// races' own hairstyles in it; a race whose port the user undid is refused, and with it every race it shares with.
    /// Off — a port asked for by hand — a target whose sharers are not all ported is refused.</param>
    /// <param name="progress">Called with (done, total) after each race.</param>
    public static Plan Build(string modRoot, string sourceGamePath, IEnumerable<ushort> targets, PbdFile pbd,
                             Func<string, byte[]?> readGame, bool withSharers, CancellationToken ct = default,
                             Action<int, int>? progress = null)
    {
        var writes = new List<HairPortWriter.PortWrite>();
        var refused = new Dictionary<ushort, string>();
        var current = new HashSet<ushort>();
        var wanted = targets.Distinct().ToList();

        if (HairPortPlan.Parse(sourceGamePath) is not { } parsed)
            return new Plan(writes, wanted.ToDictionary(t => t, _ => Loc.Localize("HairPort.Refused.NotHair",
                "it is not a hairstyle model")), current);
        var (from, id) = parsed;
        if (PenumbraModMeta.IsLegacyFolder(modRoot))
            return new Plan(writes, wanted.ToDictionary(t => t, _ => new PenumbraModMeta.LegacyFolderException(Path.GetFileName(modRoot)).Message), current);
        if (HairPortPlan.ReadOwners(modRoot) is not { } owners)
            return new Plan(writes, wanted.ToDictionary(t => t, _ => HairPortWriter.Unreadable), current, Unreadable: true);

        // Each place that publishes the hairstyle from the author's own file.
        var serving = owners.Where(o => HairPortPlan.FileFor(o, sourceGamePath) is { } rel && !HairPortPlan.IsPortFile(rel))
                            .ToList();
        if (serving.FirstOrDefault(o => o.IsCombining) is { } combining)
            return new Plan(writes, wanted.ToDictionary(t => t, _ => HairPortWriter.Combining(combining.Group)), current);
        if (serving.Count == 0)
            return new Plan(writes, wanted.ToDictionary(t => t, _ => Loc.Localize("HairPort.Refused.NotInMod",
                "the mod does not publish that hairstyle")), current);

        var authored = HairPortPlan.AuthoredRaces(owners, id).ToHashSet();
        var record = HairPortRecord.Read(modRoot);
        if (record.Unreadable)
            return new Plan(writes, wanted.ToDictionary(t => t, _ => HairPortRecord.UnreadableMessage), current, Unreadable: true);
        var sources = serving.Select(o => HairPortPlan.FileFor(o, sourceGamePath)!).Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(rel => rel, rel => ReadMod(modRoot, rel), StringComparer.OrdinalIgnoreCase);
        if (sources.Values.Any(b => b == null))
            return new Plan(writes, wanted.ToDictionary(t => t, _ => Loc.Localize("Parts.Retarget.Bake.Unreadable",
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
        int done = 0;
        foreach (ushort to in wanted)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(done++, wanted.Count);
            if (to == from || to / 100 % 2 != from / 100 % 2 || !HairPortPlan.Playable(to))
            {
                refused[to] = Loc.Localize("HairPort.Refused.Gender", "hairstyles are only ported between races of one sex");
                continue;
            }
            if (authored.Contains(to)) continue;   // the author made one: never overwritten
            string target = HairPortPlan.HairPath(to, id);
            // An undone port is not made again by itself — not even as another race's sharer. Asked for by hand, it is.
            if (withSharers && record.Declined.Contains(HairPortPlan.DeclineKey(target)))
            {
                refused[to] = Loc.Localize("HairPort.Refused.Declined", "its port was undone, so it is left alone");
                continue;
            }
            if (readGame(target) is not { } vanilla)
            {
                refused[to] = string.Format(Loc.Localize("HairPort.Refused.NoStyle.Fmt",
                    "c{0:D4} cannot choose hairstyle {1}"), to, id);
                continue;
            }
            // A material only other, unported races would also read is not ours to redirect.
            var stranded = Sharers(from, to, id, names, canWear).Where(s => !wanted.Contains(s) && !authored.Contains(s)
                                                                   && !Ported(record, HairPortPlan.HairPath(s, id))).ToList();
            if (stranded.Count > 0)
            {
                refused[to] = string.Format(Loc.Localize("HairPort.Refused.SharedMaterial.Fmt",
                    "its material is the one {0} read for their own hairstyle {1} — port to them too"),
                    string.Join(", ", stranded.Select(s => $"c{s:D4}")), id);
                continue;
            }

            var defaults = owners.FirstOrDefault(o => o.IsDefault);
            var targetHead = FirstFile(HairPortPlan.HeadPaths(to), readGame);

            var made = new Dictionary<string, HairPortBaker.Result>(StringComparer.OrdinalIgnoreCase);
            bool anyCurrent = false;
            string? why = null;
            foreach (var owner in serving)
            {
                string rel = HairPortPlan.FileFor(owner, sourceGamePath)!;
                var bytes = sources[rel]!;
                var was = record.Ports.FirstOrDefault(e => HairPortPlan.Same(e.Owner, owner)
                    && e.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase));
                var (check, est) = SkeletonFor(owner, defaults, was, from, to, id, DefaultsPortEst(record.Ports, target));
                if (was != null && !IsOutOfDate(was, bytes, owners, record.Ports)) { anyCurrent = true; continue; }
                if (was == null && !HairPortPlan.CanPortInto(owners, owner, target)) continue;

                var bones = TargetBones(readGame, vanilla, to, id, check);
                // The target race's skeleton lacks bones the hairstyle moves with: it carries the source race's own,
                // unless the author gave the target race a skeleton of their own (then that one is the answer).
                IReadOnlyList<HairPortWriter.MaterialWrite> skeletonFiles = [];
                int skeletonFrom = 0;
                if ((est > 0 || check == 0) && MissingBones(bytes, bones).Count > 0
                    && CarrySkeleton(modRoot, owners, owner, record, bytes, from, to, id, est > 0 ? est : id, readGame)
                        is { } carried)
                {
                    (skeletonFiles, bones, skeletonFrom) = carried;
                    est = 0;
                }

                // One bend per distinct file and skeleton checked against, however many options publish it.
                string key = $"{rel}|{check}|{skeletonFrom}";
                if (!made.TryGetValue(key, out var result))
                {
                    result = HairPortBaker.Prepare(bytes, from, to, id, pbd, bones, sourceHead, targetHead, out var r)!;
                    if (result == null) { why = r; break; }
                    made[key] = result;
                }

                var materials = Materials(modRoot, owners, owner, bytes, from, to, id, readGame, out var missing);
                if (missing != null) { why = missing; break; }
                writes.Add(new HairPortWriter.PortWrite(owner, sourceGamePath, rel, HairPortPlan.Sha8(bytes), target,
                                                        result.Model, [.. materials, .. skeletonFiles], result.Millis, est,
                                                        skeletonFrom));
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
                    refused[s] = string.Format(Loc.Localize("HairPort.Refused.SharerRefused.Fmt",
                        "it shares its material with c{0:D4}, which could not be ported: {1}"), to, refused[to]);
                }

        return new Plan(writes, refused, current);
    }

    /// <summary>
    /// Which extra skeleton a port to <paramref name="to"/> moves with, and which it must carry. Physics hair borrows
    /// another hairstyle's skeleton through an <c>Est</c> entry — the source race's, from the same place or the default
    /// data. The target race has that skeleton of its own; the port carries the entry for it. Unless the author gave
    /// the target race an entry of their own in a place always on with the port (the same place, or the default
    /// data): then that skeleton is the one the port is checked against, and nothing is carried. The record tells an
    /// entry this port added before — remade, it is carried again — from the author's.
    /// </summary>
    /// <returns>The skeleton to check the bones against (0: the hairstyle's own), and the entry to carry (0: none).</returns>
    /// <param name="defaultsOurs">The skeleton a port of ours in the default data carries for the same target (0 for
    /// none): with the hairstyle served from both, that entry is ours too, never the author's.</param>
    internal static (int Check, int Carry) SkeletonFor(HairPortPlan.Owner owner, HairPortPlan.Owner? defaults,
                                                       HairPortRecord.Entry? was, ushort from, ushort to, int id,
                                                       int defaultsOurs = 0)
    {
        int? source = HairPortPlan.HairEst(owner, from, id) ?? (defaults != null ? HairPortPlan.HairEst(defaults, from, id) : null);

        // The author's entry for the target race in the port's own place: any there that is not ours from before,
        // wherever it sits among them.
        int? target = HairPortPlan.HairEsts(owner, to, id).Where(e => !(was is { Est: > 0 } && was.Est == e))
                                  .Cast<int?>().FirstOrDefault();
        if (target == null && defaults != null && !owner.IsDefault)
            target = HairPortPlan.HairEsts(defaults, to, id).Where(e => !(defaultsOurs > 0 && e == defaultsOurs))
                                 .Cast<int?>().FirstOrDefault();

        if (target is { } author) return (author, 0);
        return source is > 0 and var s ? (s, s) : (0, 0);
    }

    /// <summary>
    /// Whether a port must be remade: its source model changed, it was made by an older bend, or the skeleton it should
    /// carry is no longer the one it carries — authors change a physics borrow in the manifest, not the model. The one
    /// test the builder, the automatic path and Studio share, so none of them calls a port current the others would
    /// remake.
    /// </summary>
    /// <param name="owners">The mod's places as read now; null when the manifest could not be read, which judges by
    /// the model alone.</param>
    /// <param name="ports">Every port the mod's record holds, to tell a skeleton entry of ours in the default data from
    /// the author's.</param>
    internal static bool IsOutOfDate(HairPortRecord.Entry entry, byte[] source, IReadOnlyList<HairPortPlan.Owner>? owners,
                                     IReadOnlyList<HairPortRecord.Entry> ports)
    {
        if (HairPortPlan.IsStale(entry, source)) return true;
        if (owners?.FirstOrDefault(o => HairPortPlan.Same(o, entry.Owner)) is not { } owner) return false;
        if (HairPortPlan.Parse(entry.SourceGamePath) is not { } src || HairPortPlan.Parse(entry.TargetGamePath) is not { } dst)
            return false;
        var (check, carry) = SkeletonFor(owner, owners.FirstOrDefault(o => o.IsDefault), entry, src.Race, dst.Race, dst.Id,
                                         DefaultsPortEst(ports, entry.TargetGamePath));
        // A port carrying the source race's skeleton stays current while the source still moves with that one, and
        // the author has given the target race none of their own.
        if (entry.SkeletonFrom > 0) return check != carry || (check > 0 ? check : dst.Id) != entry.SkeletonFrom;
        return carry != entry.Est;
    }

    /// <summary>The hairstyle-skeleton bones a model moves with that <paramref name="have"/> lacks.</summary>
    internal static List<string> MissingBones(byte[] model, IReadOnlySet<string> have)
        => SecondSkinWriter.Parse(model).BoneNames
                           .Where(b => b.StartsWith("j_ex_", StringComparison.Ordinal) && !have.Contains(b)).ToList();

    /// <summary>
    /// What a port carries when the target race's skeleton lacks bones the hairstyle moves with: the source race's own
    /// skeleton <paramref name="skeleton"/> — what the source borrowed, or the hairstyle's — and its physics, published
    /// at the target race's paths for the hairstyle itself. Only this hairstyle on this race moves with it, and only
    /// while the port's place is on, so no other hairstyle the race wears changes; no <c>Est</c> entry is needed, as a
    /// race the game ships a skeleton of the hairstyle for loads it from there. The author's file is named when the mod
    /// publishes one, otherwise the game's is copied. Null when it would not do: the game ships the target race no
    /// skeleton for the hairstyle (none would be loaded), the source's skeleton lacks the bones too, the mod already
    /// publishes the target's skeleton or physics, or the target race has physics for the hairstyle the source has
    /// none to replace.
    /// </summary>
    internal static (IReadOnlyList<HairPortWriter.MaterialWrite> Files, HashSet<string> Bones, int From)? CarrySkeleton(
        string modRoot, IReadOnlyList<HairPortPlan.Owner> owners, HairPortPlan.Owner owner, HairPortRecord record,
        byte[] model, ushort from, ushort to, int id, int skeleton, Func<string, byte[]?> readGame)
    {
        // Only a path the game loads: a race with no skeleton of its own for the hairstyle loads none there.
        if (readGame(HairPortPlan.SkeletonPath(to, id)) == null) return null;
        var files = new List<HairPortWriter.MaterialWrite>();
        var (skl, sklBytes) = Carry(modRoot, owners, owner, HairPortPlan.SkeletonPath(from, skeleton),
                                    HairPortPlan.SkeletonPath(to, id), readGame);
        var bones = HairPortPlan.SkeletonBones(sklBytes);
        if (skl == null || MissingBones(model, bones).Count > 0) return null;
        files.Add(skl);

        string physics = HairPortPlan.PhysicsPath(to, id);
        var (phy, _) = Carry(modRoot, owners, owner, HairPortPlan.PhysicsPath(from, skeleton), physics, readGame);
        // The target's own physics would swing bones the carried skeleton does not have.
        if (phy == null && (readGame(physics) != null || owners.Any(o => HairPortPlan.FileFor(o, physics) != null)))
            return null;
        if (phy != null) files.Add(phy);

        if (files.Any(f => !HairPortWriter.MayPublish(owners, owner, record, f.GamePath))) return null;
        return (files, bones, skeleton);
    }

    /// <summary>
    /// A file the source race reads at <paramref name="source"/>, to publish at <paramref name="target"/>: the author's
    /// own when the same place (or the default data) publishes it, otherwise a copy of another option's or the game's.
    /// Null when there is none; the bytes as read, null for the author's own when it could not be read.
    /// </summary>
    private static (HairPortWriter.MaterialWrite? Write, byte[]? Bytes) Carry(string modRoot,
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
    /// The extra bones the target race's skeleton has for the hairstyle: those its own model of it names, those its own
    /// skeleton file for it carries, and — moving with a borrowed skeleton — that one's.
    /// </summary>
    internal static HashSet<string> TargetBones(Func<string, byte[]?> readGame, byte[]? targetModel, ushort to, int id,
                                                int skeleton)
    {
        var bones = targetModel == null ? new HashSet<string>(StringComparer.Ordinal)
            : SecondSkinWriter.Parse(targetModel).BoneNames.Where(b => b.StartsWith("j_ex_", StringComparison.Ordinal)).ToHashSet();
        bones.UnionWith(HairPortPlan.SkeletonBones(readGame(HairPortPlan.SkeletonPath(to, id))));
        if (skeleton > 0 && skeleton != id)
            bones.UnionWith(HairPortPlan.SkeletonBones(readGame(HairPortPlan.SkeletonPath(to, skeleton))));
        return bones;
    }

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
            var target = HairMaterialPath.GamePath(to, id, HairMaterialPath.RenameRace(name, to));
            if (target == null || string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
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
            var t = HairMaterialPath.GamePath(to, id, HairMaterialPath.RenameRace(name, to));
            if (s == null || t == null || string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) continue;

            if (Carry(modRoot, owners, owner, s, t, readGame).Write is not { } write)
            {
                missing = string.Format(Loc.Localize("HairPort.Refused.NoMaterial.Fmt",
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

    private static byte[]? ReadMod(string modRoot, string rel)
    {
        try { return File.ReadAllBytes(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))); }
        catch (Exception) { return null; }
    }
}
