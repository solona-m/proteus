using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// The chest beneath a legs garment's waist. A high-waisted skirt's waistband sits over the belly, which a body mod
/// draws in its CHEST model; a legs refit pairs the legs alone, so the waist kept the old body's belly and the new one
/// came through it — Geometric's skirt onto Rue "Watermelon Crushers - A": 19 of 87 cloth points inside Rue's chest.
/// With the chest beneath refitted along for its shape, 7 of 90; inside the legs 363 → 311. Rue's chests share their
/// belly, so which of them is used does not change it.
/// </summary>
internal static partial class RefitCore
{
    /// <summary>
    /// How much of the source chest a legs garment's cloth must lie over, in 1 cm cells of its skin, for the chest to go
    /// in (50). Measured: Geometric's high-waisted skirt 150 at Small, 153 at Large; Heartthrob's hip-level pants 10;
    /// Scarlet's 0. Below it a legs refit is made exactly as before.
    /// </summary>
    internal const int OverBellyGate = 50;

    /// <summary>
    /// The key a legs refit's record keeps the chest beneath under, in <see cref="BodyRetargetWriter.Entry.Targets"/>:
    /// not "_top", which says a chest piece of the garment's own went onto that chest — joining options goes by those
    /// (<see cref="JoinOption"/>), and a top refitted later would otherwise join the skirt's option.
    /// </summary>
    internal const string ChestBeneathKey = "beneath:_top";

    /// <summary>
    /// Whether a legs garment's refit asks for the chest beneath at all: a legs garment, whose own skin did not read the
    /// chest (then it goes in whole, as before), with a chest the player chose — never one that only follows the legs.
    /// </summary>
    internal static bool WantsChestBeneath(string primary, IEnumerable<string> otherSlots, BodyOption? chosenChest)
        => primary == "_dwn" && chosenChest != null && !otherSlots.Contains("_top");

    /// <summary>
    /// The pair for the chest beneath, for its shape only — or false, noted: the same file on both sides, or the pair
    /// refused. Either way the legs are refitted as before.
    /// </summary>
    internal static bool ChestBeneathPair(ChestBeneath beneath, string targetDir, BodySizeCatalog targetCat,
                                          BodyOption chestTo, bool male, ushort? sourceMask, ushort? targetMask,
                                          UVRemapService? uvRemap, Action<string> note, out BodyRetarget.SlotPair pair)
    {
        pair = default;
        if (AutoRefitDecisions.SameFile(beneath.Dir, beneath.Chest, targetDir, chestTo))
        {
            note($"the chest beneath is \"{chestTo.Label}\" already; the legs are refitted alone");
            return false;
        }
        if (BodyRetarget.BuildPair("_top", beneath.Catalog.PathOf(beneath.Chest), targetCat.PathOf(chestTo),
                                   AutoRefitWatcher.SlotName("_top"), male, sourceMask, targetMask, uvRemap,
                                   out var built) is { } why)
        {
            note($"the chest beneath left out: {why}");
            return false;
        }
        pair = built with { ShapeOnly = true };
        return true;
    }

    /// <summary>The chest a legs garment's waist lies over, on the body it was made on.</summary>
    /// <param name="Dir">The body mod it is from: the garment's own, or a twin's (see <see cref="ChestBeneathOf"/>).</param>
    /// <param name="Cells">How much of it the cloth lies over (see <see cref="OverBellyGate"/>).</param>
    internal sealed record ChestBeneath(string Dir, BodySizeCatalog Catalog, BodyOption Chest, int Cells);

    /// <summary>
    /// How many 1 cm cells of the chest's skin a garment's cloth lies nearer to than to the legs' skin, within 30 mm —
    /// the waist over the belly. Per point, so a garment's size does not change it.
    /// </summary>
    internal static int WaistOverChest(ModelParts garment, ModelParts legs, ModelParts chest)
    {
        var legsSkin = new BodySurface(legs, BodySurface.CellFor(0.01f));
        var chestSkin = new BodySurface(chest, BodySurface.CellFor(0.01f));
        if (chestSkin.IsEmpty) return 0;
        var cells = new HashSet<(int, int, int)>();
        var seen = new HashSet<(float, float, float)>();
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                var key = (garment.Positions[v * 3], garment.Positions[v * 3 + 1], garment.Positions[v * 3 + 2]);
                if (!seen.Add(key)) continue;
                var p = new Vector3(key.Item1, key.Item2, key.Item3);
                if (!chestSkin.Nearest(p, BodyRetarget.PushProbeRange, out var c)) continue;
                if (legsSkin.Nearest(p, c.Distance, out _)) continue;
                cells.Add(((int)MathF.Floor(c.Point.X / 0.01f), (int)MathF.Floor(c.Point.Y / 0.01f),
                           (int)MathF.Floor(c.Point.Z / 0.01f)));
            }
        }
        return cells.Count;
    }

    /// <summary>
    /// The chest a legs size goes with: one named for it — the same name, or the same heading — else null. Unlike
    /// <see cref="AutoRefitDecisions.Companion"/> it never falls back to the first chest listed: Rue's legs sizes name no
    /// chest, and that would quietly be its smallest.
    /// </summary>
    internal static BodyOption? Beneath(IReadOnlyList<BodyOption> chests, BodyOption legs)
    {
        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        return chests.FirstOrDefault(o => Same(o.Section, legs.Section) && Same(o.Name, legs.Name))
               ?? chests.FirstOrDefault(o => Same(o.Name, legs.Name))
               ?? (legs.Section.Length > 0 ? chests.FirstOrDefault(o => Same(o.Section, legs.Section)) : null);
    }

    /// <summary>Twin legs found before, by the source legs' file and the bodies searched: a search reads every legs size
    /// of every body, which is the slow part.</summary>
    private static readonly ConcurrentDictionary<string, (string Dir, string Rel)?> twins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The chest beneath a legs garment made on <paramref name="legs"/>, when its cloth lies over enough of it
    /// (<see cref="OverBellyGate"/>); null, noted, otherwise. From the garment's own body mod when it has chests; else
    /// from the first of <paramref name="family"/> — in order — with chests whose legs include one with the very same
    /// skin: "fix pubes" is Bibo+'s "Type A - Small" alone, point for point. Which chest: the one named for the legs size,
    /// else the first listed, said.
    /// </summary>
    /// <param name="family">The bodies the garment's reading searched, in the order it searched them.</param>
    internal static ChestBeneath? ChestBeneathOf(ModelParts garment, AutoRefitDecisions.Pick legs, string race,
                                                 IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> family,
                                                 Func<string, BodySizeCatalog?> catalogOf, Action<string>? note = null)
    {
        if (catalogOf(legs.Dir) is not { } own) return null;
        string dir = legs.Dir;
        var catalog = own;
        var legsOption = legs.Option;
        if (own.For("_top", race).Count == 0)
        {
            if (Twin(own.PathOf(legs.Option), legs.Dir, race, family) is not { } twin
                || catalogOf(twin.Dir) is not { } twinCat
                || twinCat.For("_dwn", race).FirstOrDefault(o => Normal(o.Rel) == Normal(twin.Rel)) is not { } twinLegs)
            {
                note?.Invoke($"{legs.Dir} has no chest, and no other body has the same legs and one; its waist is refitted "
                           + "with the legs alone");
                return null;
            }
            note?.Invoke($"{legs.Dir} has no chest; its legs are {twin.Dir}'s \"{twinLegs.Label}\", whose chests are used");
            (dir, catalog, legsOption) = (twin.Dir, twinCat, twinLegs);
        }

        var chests = catalog.For("_top", race);
        var chest = Beneath(chests, legsOption);
        if (chest == null)
        {
            chest = chests[0];
            note?.Invoke($"no chest of {dir} is named for \"{legsOption.Label}\"; \"{chest.Label}\" is taken");
        }
        ModelParts? legsModel, chestModel;
        try
        {
            legsModel = ModelPartReader.Read(File.ReadAllBytes(catalog.PathOf(legsOption)));
            chestModel = ModelPartReader.Read(File.ReadAllBytes(catalog.PathOf(chest)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note?.Invoke($"the chest beneath could not be read: {ex.Message}");
            return null;
        }
        if (legsModel == null || chestModel == null)
        {
            note?.Invoke("the chest beneath could not be read");
            return null;
        }
        int cells = WaistOverChest(garment, legsModel, chestModel);
        if (cells < OverBellyGate) return null;
        note?.Invoke($"its waist lies over {cells} cm² of {dir}'s \"{chest.Label}\": the chest beneath is refitted along");
        return new ChestBeneath(dir, catalog, chest, cells);
    }

    /// <summary>The first body of <paramref name="family"/> with chests whose legs include one with the same skin as
    /// <paramref name="legsPath"/>: as many skin points, each on one of the other's.</summary>
    private static (string Dir, string Rel)? Twin(string legsPath, string ownDir, string race,
                                                  IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> family)
    {
        string key = legsPath + "|" + race + "|" + string.Join(";", family.Select(b => b.Dir));
        if (twins.TryGetValue(key, out var known)) return known;
        (string Dir, string Rel)? found = null;
        try
        {
            var own = SkinKeys(ModelPartReader.Read(File.ReadAllBytes(legsPath)));
            if (own.Count > 0)
                foreach (var (dir, catalog) in family)
                {
                    if (string.Equals(dir, ownDir, StringComparison.OrdinalIgnoreCase) || catalog.For("_top", race).Count == 0)
                        continue;
                    foreach (var option in catalog.For("_dwn", race))
                    {
                        var path = catalog.PathOf(option);
                        if (!File.Exists(path)) continue;
                        var theirs = SkinKeys(ModelPartReader.Read(File.ReadAllBytes(path)));
                        if (theirs.Count != own.Count || !Covers(theirs, own) || !Covers(own, theirs)) continue;
                        found = (dir, option.Rel);
                        break;
                    }
                    if (found != null) break;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return twins[key] = found;
    }

    /// <summary>Whether every point of <paramref name="points"/> has one of <paramref name="set"/> within a tenth of a
    /// millimetre: a point a hair's breadth either side of a step is the same point.</summary>
    private static bool Covers(HashSet<(int, int, int)> set, HashSet<(int, int, int)> points)
    {
        foreach (var (x, y, z) in points)
        {
            if (set.Contains((x, y, z))) continue;
            bool near = false;
            for (int dx = -1; dx <= 1 && !near; dx++)
            for (int dy = -1; dy <= 1 && !near; dy++)
            for (int dz = -1; dz <= 1 && !near; dz++)
                near = set.Contains((x + dx, y + dy, z + dz));
            if (!near) return false;
        }
        return true;
    }

    /// <summary>A model's skin points, to a tenth of a millimetre.</summary>
    private static HashSet<(int, int, int)> SkinKeys(ModelParts? model)
    {
        var keys = new HashSet<(int, int, int)>();
        if (model == null) return keys;
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
                keys.Add(((int)MathF.Round(model.Positions[v * 3] * 1e4f), (int)MathF.Round(model.Positions[v * 3 + 1] * 1e4f),
                          (int)MathF.Round(model.Positions[v * 3 + 2] * 1e4f)));
        }
        return keys;
    }
}
