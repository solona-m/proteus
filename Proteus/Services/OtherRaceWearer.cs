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
    internal sealed record Inputs(
        ushort Race, ushort Drawn, string Slot, int Set,
        byte[]? ModdedBase, string? ModdedFrom, byte[]? GameBase, byte[]? Pbd,
        IReadOnlyList<EstTable.ModEntry> ModEst, IReadOnlyDictionary<(int Set, ushort Race), int>? GameEst,
        IReadOnlyDictionary<string, byte[]> EstSkeletons);

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
        byte[]? modded = null;
        string? moddedFrom = resolveCollection(basePath);
        if (moddedFrom != null && System.IO.Path.IsPathRooted(moddedFrom) && System.IO.File.Exists(moddedFrom))
        {
            try { modded = System.IO.File.ReadAllBytes(moddedFrom); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                warn?.Invoke(moddedFrom, ex);
            }
        }
        else moddedFrom = null;

        var gameEst = EstTable.GamePath(slot) is { } table ? EstTable.Parse(readGame(table)) : null;
        var entries = modEst.Where(e => e.Set == set).Select(e => e.Entry).ToList();
        if (gameEst != null)
            foreach (ushort r in new[] { race, drawn })
                if (gameEst.TryGetValue((set, r), out int value)) entries.Add(value);
        var skeletons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (int entry in entries.Distinct())
            if (EstTable.SkeletonPath(race, slot, entry) is { } path && (modFile(path) ?? readGame(path)) is { } bytes)
                skeletons[path] = bytes;

        return new Inputs(race, drawn, slot, set, modded, moddedFrom, readGame(basePath), readGame(PbdPath), modEst,
                          gameEst, skeletons);
    }

    /// <param name="Wearer">Who to bake for; null when the bake is refused.</param>
    /// <param name="FellBack">The collection's skeleton could not be read, and the game's was used.</param>
    /// <param name="Refusal">Why there is no wearer, worded for the user.</param>
    internal sealed record Made(RacialModelBake.Wearer? Wearer, EstPlan Est, bool FellBack, string Refusal,
                                IReadOnlyList<string> Unbendable);

    /// <summary>
    /// The wearer for a garment: the base skeleton (the collection's, else the game's), joined by whatever extra skeleton
    /// the race loads for the item, and the game's deformer. Refused — never baked half-way — when a bone the garment
    /// leans on would not move: no deformer entry for it or any ancestor the skeletons give it.
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
        var est = OtherRaceRefit.PlanEst(inputs.Race, inputs.Drawn, inputs.Slot, inputs.Set,
                                         OtherRaceRefit.Weighted(garment), parents.Keys.ToHashSet(StringComparer.Ordinal),
                                         inputs.ModEst, inputs.GameEst,
                                         path => inputs.EstSkeletons.TryGetValue(path, out var bytes)
                                                     ? HavokSkeleton.Read(bytes) : null);
        // An extra skeleton's bones hang off the base one's, as the game attaches it; the base keeps its own parents.
        if (est.Skeleton != null)
            foreach (var (bone, parent) in est.Skeleton.ParentMap())
                parents.TryAdd(bone, parent);

        var wearer = new RacialModelBake.Wearer(inputs.Race, parents, pbd);
        // Bones the race's skeletons do not have are refused whether or not the deformer happens to list them: the bake
        // might bend them, but the game would load no bone to draw them with, and no entry would be saved to give it one.
        var unbendable = est.Outcome == EstOutcome.Missing
            ? (est.Missing ?? []).ToList()
            : OtherRaceRefit.Unbendable(garment, pbd, wearer.ParentOf, inputs.Drawn, inputs.Race);
        if (unbendable.Count > 0)
            return new Made(null, est, fellBack, string.Format(Loc.Localize("Parts.Retarget.Race.Unbendable.Fmt",
                "{0} would not move with {1}'s skeleton: {2}"), unbendable.Count == 1
                    ? Loc.Localize("Parts.Retarget.Race.OneBone", "a bone the garment leans on")
                    : string.Format(Loc.Localize("Parts.Retarget.Race.ManyBones.Fmt", "{0} bones the garment leans on"),
                                    unbendable.Count),
                ModelRace.Describe($"{inputs.Race:D4}"), string.Join(", ", unbendable.Take(12))), unbendable);
        return new Made(wearer, est, fellBack, "", []);
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
