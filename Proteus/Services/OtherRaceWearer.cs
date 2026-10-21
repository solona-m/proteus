using System;
using System.Collections.Generic;
using System.Linq;
using CheapLoc;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// Who a garment is baked for when the creator picks a race (see <see cref="OtherRaceRefit"/>): that race's own skeleton
/// and the game's own racial deformer, read from files — so the bake is the same whoever is logged in, or nobody.
/// <para/>
/// The skeleton is the one the creator's collection gives the race, so a custom skeleton (IVCS) and its extra bones
/// count; the game's own when that cannot be read. The deformer is always the game's: a creator's posing mod that ships
/// its own would otherwise be baked into a model meant for everyone.
/// </summary>
internal static class OtherRaceWearer
{
    /// <summary>The game path of a race's base skeleton.</summary>
    internal static string BaseSkeletonPath(ushort race) => $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb";

    /// <summary>The game's racial deformer.</summary>
    internal const string PbdPath = "chara/xls/boneDeformer/human.pbd";

    /// <summary>
    /// Everything the bake for a picked race reads, gathered on the framework thread — the collection and the game's data
    /// are asked there — for a worker to make the wearer from.
    /// </summary>
    /// <param name="ModdedBase">The base skeleton the creator's collection resolves the race to, when that is a mod's
    /// file; null when it is the game's own.</param>
    /// <param name="ModdedFrom">That file, for the log.</param>
    /// <param name="ModEst">The Est entries the garment's option and the mod's default data set.</param>
    /// <param name="EstSkeletons">Extra skeletons by game path, from the mod's own files or the game's.</param>
    /// <param name="DrawnSkeletons">The drawn race's skeletons, base first (the collection's, else the game's), then
    /// its extra skeleton for the item: the parents a bone the new race lacks is moved up through
    /// (<see cref="SkeletonRemap"/>). None when they could not be read.</param>
    internal sealed record Inputs(
        ushort Race, ushort Drawn, string Slot, int Set,
        byte[]? ModdedBase, string? ModdedFrom, byte[]? GameBase, byte[]? Pbd,
        IReadOnlyList<EstTable.ModEntry> ModEst, IReadOnlyDictionary<(int Set, ushort Race), int>? GameEst,
        IReadOnlyDictionary<string, byte[]> EstSkeletons, IReadOnlyList<byte[]>? DrawnSkeletons = null);

    /// <summary>
    /// What the bake for <paramref name="race"/> of a garment drawn at <paramref name="drawnPath"/> reads: the race's base
    /// skeleton as the collection gives it and as the game ships it, the game's deformer, and the extra-skeleton entries
    /// that may apply with the skeletons they name — those only from the mod's own files or the game's.
    /// </summary>
    /// <param name="readGame">Reads a game path out of the game's own data, past every mod; null when it has none.</param>
    /// <param name="resolveCollection">The file the creator's collection loads for a game path: a path on disk when a mod
    /// replaces it, anything else when the game's own is used. Asked for the base skeleton alone.</param>
    /// <param name="modFile">A file the garment's own mod publishes at a game path, read; null when it has none.</param>
    /// <param name="modEst">The Est entries the garment's option and the mod's default data set.</param>
    internal static Inputs Gather(ushort race, string drawnPath, Func<string, byte[]?> readGame,
                                  Func<string, string?> resolveCollection, Func<string, byte[]?> modFile,
                                  IReadOnlyList<EstTable.ModEntry> modEst, Action<string, Exception>? warn = null)
    {
        ushort drawn = ModelSkinReader.RaceOf(drawnPath);
        string slot = BodySizeCatalog.SlotOf(drawnPath) ?? "_top";
        int set = PenumbraManipulations.ParseSetId(drawnPath, 'e') ?? 0;

        string basePath = BaseSkeletonPath(race);
        string? moddedFrom = resolveCollection(basePath);
        byte[]? modded = ReadCollectionFile(moddedFrom, warn);
        if (modded == null) moddedFrom = null;

        var gameEst = EstTable.GamePath(slot) is { } table ? EstTable.Parse(readGame(table)) : null;
        var entries = modEst.Where(e => e.Set == set).Select(e => e.Entry).ToList();
        if (gameEst != null)
            foreach (ushort r in new[] { race, drawn })
                if (gameEst.TryGetValue((set, r), out int value)) entries.Add(value);
        var skeletons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (int entry in entries.Distinct())
            if (EstTable.SkeletonPath(race, slot, entry) is { } path && (modFile(path) ?? readGame(path)) is { } bytes)
                skeletons[path] = bytes;

        // The drawn race's own skeletons, for where a bone the new race lacks hangs: the collection's base (a women's
        // custom skeleton is where her garment's own bones come from), else the game's, then her extra skeleton.
        var drawnSkeletons = new List<byte[]>();
        string drawnBase = BaseSkeletonPath(drawn);
        if ((ReadCollectionFile(resolveCollection(drawnBase), warn) ?? readGame(drawnBase)) is { } drawnBytes)
            drawnSkeletons.Add(drawnBytes);
        var drawnEntries = modEst.Where(e => e.Set == set && e.Race == drawn).Select(e => e.Entry).ToList();
        if (gameEst != null && gameEst.TryGetValue((set, drawn), out int drawnEntry)) drawnEntries.Add(drawnEntry);
        foreach (int entry in drawnEntries.Where(e => e > 0).Distinct())
            if (EstTable.SkeletonPath(drawn, slot, entry) is { } path && (modFile(path) ?? readGame(path)) is { } bytes)
                drawnSkeletons.Add(bytes);

        return new Inputs(race, drawn, slot, set, modded, moddedFrom, readGame(basePath), readGame(PbdPath), modEst,
                          gameEst, skeletons, drawnSkeletons);
    }

    /// <summary>A file the collection loads for a game path, when that is a file on disk; null otherwise.</summary>
    private static byte[]? ReadCollectionFile(string? path, Action<string, Exception>? warn)
    {
        if (path == null || !System.IO.Path.IsPathRooted(path) || !System.IO.File.Exists(path)) return null;
        try { return System.IO.File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            warn?.Invoke(path, ex);
            return null;
        }
    }

    /// <param name="Wearer">Who to bake for; null when the bake is refused.</param>
    /// <param name="FellBack">The collection's skeleton could not be read, and the game's was used.</param>
    /// <param name="Refusal">Why there is no wearer, worded for the user.</param>
    /// <param name="Moves">The garment's bones the race lacks, and the bones they are moved onto
    /// (<see cref="SkeletonRemap.Rename"/> before the bake).</param>
    /// <param name="Planner">Plans the same moves for the bodies the garment was made on.</param>
    /// <param name="NeedsSkeleton">Bones the garment leans on, after the moves, that only the collection's custom
    /// skeleton gives the race: anyone without it sees those parts pulled out of place.</param>
    internal sealed record Made(RacialModelBake.Wearer? Wearer, EstPlan Est, bool FellBack, string Refusal,
                                IReadOnlyList<string> Unbendable, IReadOnlyList<SkeletonRemap.Move>? Moves = null,
                                SkeletonRemap.Planner? Planner = null, IReadOnlyList<string>? NeedsSkeleton = null);

    /// <summary>
    /// The wearer for a garment: the base skeleton (the collection's, else the game's), joined by whatever extra skeleton
    /// the race loads for the item, and the game's deformer. A bone the garment leans on that neither skeleton has is
    /// moved onto one they have (<see cref="SkeletonRemap"/>), never refused. Refused — never baked half-way — when a bone
    /// the skeletons do have would not move: no deformer entry for it or any ancestor they give it.
    /// </summary>
    internal static Made Build(Inputs inputs, SkinnedMesh garment)
    {
        var none = EstPlan.NotNeeded;
        if (inputs.Pbd is not { Length: > 0 } pbdBytes)
            return new Made(null, none, false, Loc.Localize("Parts.Retarget.Race.NoDeformer",
                "the game's racial deformer could not be read"), []);
        PbdFile pbd;
        try { pbd = new PbdFile(pbdBytes); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException
                                       or OutOfMemoryException)
        {
            // The reader indexes the bytes as it goes; a file it cannot read fails with one of these. The cause goes
            // with the refusal, which the panel logs.
            return new Made(null, none, false, Loc.Localize("Parts.Retarget.Race.NoDeformer",
                "the game's racial deformer could not be read") + $" ({ex.GetType().Name}: {ex.Message})", []);
        }

        var skeleton = inputs.ModdedBase != null ? HavokSkeleton.Read(inputs.ModdedBase) : null;
        bool fellBack = inputs.ModdedBase != null && skeleton == null;
        skeleton ??= inputs.GameBase != null ? HavokSkeleton.Read(inputs.GameBase) : null;
        if (skeleton == null)
            return new Made(null, none, fellBack, string.Format(Loc.Localize("Parts.Retarget.Race.NoSkeleton.Fmt",
                "{0}'s skeleton could not be read"), ModelRace.Describe($"{inputs.Race:D4}")), []);

        var parents = skeleton.ParentMap();
        var baseBones = parents.Keys.ToHashSet(StringComparer.Ordinal);
        var drawnParentOf = DrawnParents(inputs.DrawnSkeletons);
        HavokSkeleton? EstSkeleton(string path)
            => inputs.EstSkeletons.TryGetValue(path, out var bytes) ? HavokSkeleton.Read(bytes) : null;
        EstPlan PlanFor(IEnumerable<string> bones)
            => OtherRaceRefit.PlanEst(inputs.Race, inputs.Drawn, inputs.Slot, inputs.Set, bones, baseBones,
                                      inputs.ModEst, inputs.GameEst, EstSkeleton);

        var weighted = OtherRaceRefit.Weighted(garment).ToList();
        var est = PlanFor(weighted);
        IReadOnlyList<SkeletonRemap.Move> moves = [];
        if (est.Outcome == EstOutcome.Missing)
        {
            // The game would load no bone to draw these with: each is moved onto one the race's skeletons have — the
            // base's, or one the extra skeleton found for the item brings — and the table planned again for the rest.
            var have = new HashSet<string>(baseBones, StringComparer.Ordinal);
            if (est.Skeleton != null) have.UnionWith(est.Skeleton.Names);
            moves = new SkeletonRemap.Planner(have, drawnParentOf).MovesFor(est.Missing ?? []);
            weighted = SkeletonRemap.Apply(weighted, moves).ToList();
            est = PlanFor(weighted);
        }
        // An extra skeleton's bones hang off the base one's, as the game attaches it; the base keeps its own parents.
        if (est.Outcome != EstOutcome.Missing && est.Skeleton != null)
            foreach (var (bone, parent) in est.Skeleton.ParentMap())
                parents.TryAdd(bone, parent);

        var wearer = new RacialModelBake.Wearer(inputs.Race, parents, pbd);
        // Still missing after the moves cannot happen — every move lands on a bone the skeletons have — but if it did,
        // the bake is refused naming them rather than made with bones the game would not draw.
        var unbendable = est.Outcome == EstOutcome.Missing
            ? (est.Missing ?? []).ToList()
            : OtherRaceRefit.Unbendable(weighted, pbd, wearer.ParentOf, inputs.Drawn, inputs.Race);
        if (unbendable.Count > 0)
            return new Made(null, est, fellBack, string.Format(Loc.Localize("Parts.Retarget.Race.Unbendable.Fmt",
                "{0} would not move with {1}'s skeleton: {2}"), unbendable.Count == 1
                    ? Loc.Localize("Parts.Retarget.Race.OneBone", "a bone the garment leans on")
                    : string.Format(Loc.Localize("Parts.Retarget.Race.ManyBones.Fmt", "{0} bones the garment leans on"),
                                    unbendable.Count),
                ModelRace.Describe($"{inputs.Race:D4}"), string.Join(", ", unbendable.Take(12))), unbendable, moves);
        var planner = new SkeletonRemap.Planner(parents.Keys.ToHashSet(StringComparer.Ordinal), drawnParentOf);
        return new Made(wearer, est, fellBack, "", [], moves, planner,
                        NeedsSkeleton(inputs, fellBack, baseBones, est, weighted));
    }

    /// <summary>
    /// The parents the drawn race's skeletons give a bone: its base first, then its extra skeleton's — null for a bone
    /// none of them has.
    /// </summary>
    internal static Func<string, string?> DrawnParents(IReadOnlyList<byte[]>? skeletons)
    {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var bytes in skeletons ?? [])
            if (HavokSkeleton.Read(bytes) is { } skeleton)
                foreach (var (bone, parent) in skeleton.ParentMap())
                    parents.TryAdd(bone, parent);
        return bone => parents.TryGetValue(bone, out var parent) ? parent : null;
    }

    /// <summary>
    /// The bones the garment leans on, after the moves, that only the collection's custom skeleton gives the race —
    /// neither the game's base skeleton nor the extra skeleton the item loads has them.
    /// </summary>
    private static IReadOnlyList<string> NeedsSkeleton(Inputs inputs, bool fellBack, IReadOnlySet<string> baseBones,
                                                       EstPlan est, IEnumerable<string> weighted)
    {
        if (inputs.ModdedBase == null || fellBack || inputs.GameBase == null) return [];
        if (HavokSkeleton.Read(inputs.GameBase) is not { } game) return [];
        var gameBones = game.Names.ToHashSet(StringComparer.Ordinal);
        return weighted.Where(b => baseBones.Contains(b) && !gameBones.Contains(b)
                                   && !(est.Skeleton?.Names.Contains(b) ?? false))
                       .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The parent differences between two skeletons of one race — the one read from a file and the one the live
    /// character has — for the log: a bone the two give different parents is one the file reader got wrong, or a
    /// skeleton mod the character does not wear.
    /// </summary>
    internal static List<string> Parity(IReadOnlyDictionary<string, string?> file, IReadOnlyDictionary<string, string?> live)
    {
        var differ = new List<string>();
        foreach (var (bone, parent) in live)
            if (file.TryGetValue(bone, out var other) && !string.Equals(parent, other, StringComparison.Ordinal))
                differ.Add($"{bone}: {other ?? "root"} in the file, {parent ?? "root"} live");
        int onlyLive = live.Keys.Count(b => !file.ContainsKey(b));
        int onlyFile = file.Keys.Count(b => !live.ContainsKey(b));
        if (onlyLive > 0) differ.Add($"{onlyLive} bone(s) only live");
        if (onlyFile > 0) differ.Add($"{onlyFile} bone(s) only in the file");
        return differ;
    }
}
