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
    public sealed record Plan(IReadOnlyList<HairPortWriter.PortWrite> Writes, IReadOnlyDictionary<ushort, string> Refused,
                              IReadOnlySet<ushort> Current, bool Unreadable = false);

    /// <summary>
    /// The ports of the mod's hairstyle at <paramref name="sourceGamePath"/> for <paramref name="targets"/>, each moving
    /// with the skeleton its race loads for the hairstyle — given one that has the bones where it does not
    /// (<see cref="HairPortSkeleton"/>).
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
        // Read once, and only when a port needs to know which skeleton a race loads.
        var table = HairSkeletonTable.Lazy(readGame);
        bool tableMissing = false;
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
            if (readGame(target) == null)
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

            var targetHead = FirstFile(HairPortPlan.HeadPaths(to), readGame);

            var made = new Dictionary<string, HairPortBaker.Result>(StringComparer.OrdinalIgnoreCase);
            bool anyCurrent = false;
            string? why = null;
            // The slot a carried skeleton of this target went to: every place's port of it uses the same one.
            int slot = 0;
            foreach (var owner in serving)
            {
                string rel = HairPortPlan.FileFor(owner, sourceGamePath)!;
                var bytes = sources[rel]!;
                var was = record.Ports.FirstOrDefault(e => HairPortPlan.Same(e.Owner, owner)
                    && e.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (was != null && !IsOutOfDate(modRoot, was, bytes, owners, record.Ports, readGame, table)) { anyCurrent = true; continue; }
                if (was == null && !HairPortPlan.CanPortInto(owners, owner, target)) continue;

                var place = new HairPortSkeleton.Place(modRoot, owners, owner, was, record.Ports, from, to, id, readGame, table);
                // A port the user edited since cannot be remade: refused for its race alone — the save would refuse the
                // whole lot — and one whose skeleton changes another hairstyle is stripped of it, below, the edit kept.
                if (was != null && HairPortWriter.EditedBy(modRoot, was.File) is { } edited)
                {
                    why = ModelBackupOrder.BlockedMessage(edited);
                    break;
                }
                var choice = HairPortSkeleton.Choose(place, bytes);
                if (choice.Route == HairPortSkeleton.Route.Unknown) { why = choice.Why ?? NoSkeletonTable; tableMissing = true; break; }
                IReadOnlySet<string> bones = choice.Bones;
                int est = choice.Route == HairPortSkeleton.Route.Entry ? choice.Check : 0, skeletonFrom = 0;
                IReadOnlyList<HairPortWriter.MaterialWrite> skeletonFiles = [];
                // What the race loads lacks bones the hairstyle moves with, and no skeleton of its own has them: the
                // source race's goes to a slot of the race's that only this hairstyle loads, with an entry pointing there.
                if (HairPortSkeleton.Carry(place, record, bytes, choice, slot) is { } carried)
                {
                    (skeletonFiles, bones, slot) = carried;
                    (est, skeletonFrom) = (slot, choice.From);
                }

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

        // Every race refused, however: a port of it from before that carries a skeleton another hairstyle of the race
        // loads stops doing that.
        foreach (var to in refused.Keys)
            writes.AddRange(Strips(modRoot, owners, record, from, to, id, readGame, table));

        return new Plan(writes, refused, current, Unreadable: tableMissing);
    }

    /// <summary>Why a port that needs the game's table of hairstyle skeletons is refused without it.</summary>
    internal static string NoSkeletonTable
        => Loc.Localize("HairPort.Refused.NoSkeletonTable", "the game's table of hairstyle skeletons could not be read");

    /// <summary>
    /// For a race that could not be ported again: each port of it that carries a skeleton to a slot another hairstyle of
    /// the race loads (<see cref="HairPortSkeleton.Harmful"/>), saved again without that skeleton, its physics and the
    /// entry pointing there — the model, its materials and its hat fit kept — so it stops changing that hairstyle.
    /// </summary>
    private static IEnumerable<HairPortWriter.PortWrite> Strips(string modRoot, IReadOnlyList<HairPortPlan.Owner> owners,
        HairPortRecord record, ushort from, ushort to, int id, Func<string, byte[]?> readGame, Func<HairSkeletonTable?> table)
    {
        string target = HairPortPlan.HairPath(to, id);
        foreach (var e in record.Ports.Where(e => e.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            if (owners.FirstOrDefault(o => HairPortPlan.Same(o, e.Owner)) is not { } owner) continue;
            var place = new HairPortSkeleton.Place(modRoot, owners, owner, e, record.Ports, from, to, id, readGame, table);
            if (!HairPortSkeleton.Harmful(place, e)) continue;
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
    /// <see cref="HairPortSkeleton.Choose"/>'s.
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
        if (HairPortPlan.IsStale(entry, source)) return true;
        if (owners?.FirstOrDefault(o => HairPortPlan.Same(o, entry.Owner)) is not { } owner) return false;
        if (HairPortPlan.Parse(entry.SourceGamePath) is not { } src || HairPortPlan.Parse(entry.TargetGamePath) is not { } dst)
            return false;
        var place = new HairPortSkeleton.Place(modRoot, owners, owner, entry, ports, src.Race, dst.Race, dst.Id, readGame, table);
        return !HairPortSkeleton.Current(place, entry, source);
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

    internal static byte[]? ReadMod(string modRoot, string rel)
    {
        try { return File.ReadAllBytes(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar))); }
        catch (Exception) { return null; }
    }
}
