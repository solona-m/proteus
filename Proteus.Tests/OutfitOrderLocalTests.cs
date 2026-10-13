using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// An outfit's pieces kept in their author's order across a refit (see <see cref="BodyRetarget.KeepOutfitOrder"/>),
/// against the installed mods: Geometric's cropped jacket — its hands model — over its bra, onto Rue's chests; Scarlet's
/// jacket on a bracelet slot over its top; Heartthrob's shoes over its pants. Measured on the refit's own models, inside
/// the pass, body-relatively: per 5 mm cell of the target chest, the bra's highest cloth against the jacket's lowest.
/// </summary>
[Collection(GameBodyCollection.Name)]
public class OutfitOrderLocalTests(ITestOutputHelper output)
{
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string IvcsBibo = LocalData.Mods + ":[IVCS] Bibo+";
    private const string FixPubes = LocalData.Mods + ":fix pubes";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Scarlet = LocalData.Mods + ":Scarlet - by Solona";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Heartthrob = LocalData.Mods + ":Heartthrob - by Solona";
    private const string Race = "0201";
    private const string TopRel = "chest size/small/chara/equipment/e6010/model/c0201e6010_top.mdl";
    private const string GloveRel = "chest size/small/chara/equipment/e0068/model/c0201e0068_glv.mdl";

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    private static (byte[] Bytes, ModelParts Parts, IReadOnlySet<string> Bones) Load(string root, string rel)
    {
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, rel));
        return (bytes, ModelPartReader.Read(bytes)!, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal));
    }

    private static (int, int, int) Cell(Vector3 p)
        => ((int)MathF.Floor(p.X / BodyRetarget.OutfitCell), (int)MathF.Floor(p.Y / BodyRetarget.OutfitCell),
            (int)MathF.Floor(p.Z / BodyRetarget.OutfitCell));

    /// <summary>Per cell of <paramref name="surface"/>, the lowest the piece's cloth stands above it.</summary>
    private static Dictionary<(int, int, int), float> Lowest(ModelParts piece, BodySurface surface)
    {
        var cells = new Dictionary<(int, int, int), float>();
        foreach (var part in piece.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                var p = new Vector3(piece.Positions[v * 3], piece.Positions[v * 3 + 1], piece.Positions[v * 3 + 2]);
                if (!surface.Nearest(p, BodyRetarget.PushProbeRange, out var hit)) continue;
                float h = Vector3.Dot(p - hit.Point, hit.Normal);
                var key = Cell(hit.Point);
                if (!cells.TryGetValue(key, out float had) || h < had) cells[key] = h;
            }
        }
        return cells;
    }

    /// <summary>Cells where the piece beneath stands more than 0.5 mm in front of the piece over it.</summary>
    private static int Through(ModelParts beneath, ModelParts over, BodySurface surface, out float worst)
    {
        var high = BodyRetarget.OutfitHeights(beneath, [surface]);
        var low = Lowest(over, surface);
        worst = 0f;
        int count = 0;
        foreach (var (key, h) in high)
            if (low.TryGetValue(key, out float l) && h > l + 5e-4f)
            {
                count++;
                worst = MathF.Max(worst, h - l);
            }
        return count;
    }

    /// <summary>The largest difference between neighbours' lifts, and the largest an edge grew, from one model to the
    /// other — the same mesh, moved twice.</summary>
    private static (float Neighbours, float Stretch) Lump(ModelParts before, ModelParts after)
    {
        Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        float neighbours = 0f, stretch = 0f;
        foreach (var part in after.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            var t = part.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = t[i + e], b = t[i + (e + 1) % 3];
                    float liftA = Vector3.Distance(At(after, a), At(before, a));
                    float liftB = Vector3.Distance(At(after, b), At(before, b));
                    neighbours = MathF.Max(neighbours, MathF.Abs(liftA - liftB));
                    stretch = MathF.Max(stretch, Vector3.Distance(At(after, a), At(after, b))
                                                 - Vector3.Distance(At(before, a), At(before, b)));
                }
        }
        return (neighbours, stretch);
    }

    [LocalDataFact(Geometric, Bibo, IvcsBibo, FixPubes, Rue)]
    [Trait("Plan", "AC-outfit-fit-1")]
    [Trait("Plan", "AC-outfit-fit-2")]
    public void Geometrics_jacket_stays_outside_the_bras_refit_on_Rue()
    {
        string root = LocalData.Path(Geometric);
        var bodies = new[] { Body(Bibo), Body(IvcsBibo), Body(FixPubes), Body(Rue) }.ToList();
        var rue = bodies[3];
        BodySizeCatalog? CatalogOf(string dir)
            => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;

        var (topBytes, top, topBones) = Load(root, TopRel);
        var source = RefitCore.DetectSource(top, topBones, "_top", Race, bodies, rue.Dir, _ => true)!;
        var sourceCat = CatalogOf(source.Dir)!;
        var (gloveBytes, glove, gloveBones) = Load(root, GloveRel);
        var reading = KeepEnds.Read("_glv", glove, gloveBytes)!;
        var found = KeepEnds.Find(reading, glove, gloveBones, Race,
                                  (view, slot) => RefitCore.DetectSource(view, gloveBones, slot, Race, bodies, rue.Dir, _ => true),
                                  CatalogOf, sibling: source);
        Assert.Equal(KeepEnds.Route.Sibling, found.Route);
        var keepShapeTop = RefitCore.ShapePieces(top, RefitCore.DefaultKeepShape(top));

        // As measured when the pass was calibrated (M1b): Rue S, M and L at most 0, 3 and 2 cells through — the rest
        // is the jacket's hem, which its author laid on the skin over a bra band tucked under it, and which cannot rise
        // the 5-7 mm the band rose without folding.
        var atMost = new Dictionary<string, int> { ["Small"] = 0, ["Medium"] = 3, ["Large"] = 2 };
        foreach (string size in new[] { "Small", "Medium", "Large" })
        {
            var chest = rue.Catalog.For("_top", Race).First(o => o.Label == "None · " + size);
            string chestPath = rue.Catalog.PathOf(chest);
            Assert.Null(BodyRetarget.BuildPair("_top", sourceCat.PathOf(source.Option), chestPath, "_top", false, null,
                                               null, null, out var pair));
            var bra = ModelPartReader.Read(BodyRetarget.Plan(top, topBytes, [pair], "_top", held: new HashSet<int>(),
                                                             replaceSkin: true, acrossBodies: true, cutHidden: false,
                                                             keepShape: keepShapeTop).Model)!;

            BodyOption? TargetFor(string slot)
                => slot == "_top" ? chest : AutoRefitDecisions.Companion(rue.Catalog.For(slot, Race), chest, null);
            var refit = Assert.IsType<KeepEnds.Refit>(KeepEnds.Decide(found, reading, gloveBones, Race, rue.Dir, CatalogOf,
                                                                      TargetFor));
            Assert.Null(KeepEnds.BuildPairs(reading, sourceCat.PathOf(refit.Source.Option), chestPath,
                                            refit.EndFrom == null ? null : sourceCat.PathOf(refit.EndFrom),
                                            refit.EndTo == null ? null : rue.Catalog.PathOf(refit.EndTo),
                                            false, _ => (null, null), s => s, null, null, out var limb, out var end));
            var outfit = new BodyRetarget.Outfit(top, bra);
            var plain = KeepEnds.Plan(glove, gloveBytes, reading, limb, end, across: true);
            var kept = KeepEnds.Plan(glove, gloveBytes, reading, limb, end, across: true, outfit: outfit);
            var off = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoOutfitOrder: true),
                                              () => KeepEnds.Plan(glove, gloveBytes, reading, limb, end, across: true,
                                                                  outfit: outfit));

            var surface = new BodySurface(pair.Target, BodySurface.CellFor(0.01f));
            var before = ModelPartReader.Read(plain.Model)!;
            var after = ModelPartReader.Read(kept.Model)!;
            int was = Through(bra, before, surface, out float wasWorst);
            int now = Through(bra, after, surface, out float worst);
            var (neighbours, stretch) = Lump(before, after);
            output.WriteLine($"Rue {size}: {was} cells through (worst {wasWorst * 1000f:F1} mm) -> {now} ({worst * 1000f:F1} mm); "
                           + $"{kept.Report.Layered} lifted, {kept.Report.LayerResidual * 1000f:F1} mm short; folds "
                           + $"{plain.Report.Folded} -> {kept.Report.Folded}; neighbours {neighbours * 1000f:F2} mm apart, "
                           + $"an edge grew {stretch * 1000f:F2} mm");

            Assert.Equal(plain.Model, off.Model);
            Assert.True(kept.Report.Layered > 0, "the jacket should have been lifted");
            Assert.True(now <= atMost[size], $"Rue {size}: {now} cells through");
            Assert.True(kept.Report.Folded <= plain.Report.Folded, $"{kept.Report.Folded} folded, {plain.Report.Folded} before");
            // The lift, faded over 15 mm on a jacket whose edges are ~9.5 mm: never more than 0.75 of an edge between
            // neighbours, as measured (M1b), and normals left as the author made them.
            Assert.True(neighbours <= 0.75f * 0.0095f, $"neighbours {neighbours * 1000f:F2} mm apart");
            Assert.Equal(before.Normals, after.Normals);
        }
    }

    [LocalDataFact(Scarlet, Neolithe)]
    [Trait("Plan", "AC-outfit-fit-3")]
    public void Scarlets_bracelet_jacket_stays_outside_its_tops_refit()
    {
        const string xsJacket = "top size/neolithe xs/chara/accessory/a0095/model/c0201a0095_wrs.mdl";
        string root = LocalData.Path(Scarlet);
        var neo = Body(Neolithe);
        var chest = neo.Catalog.For("_top", Race).FirstOrDefault(o => o.Label.EndsWith("SFW M", StringComparison.Ordinal))
                    ?? neo.Catalog.For("_top", Race)[0];
        var top = Assert.Single(SiblingPiece.Find(PenumbraModMeta.ReadAllRedirects(root), BodyRetargetWriter.ReadRecord(root),
                                                  xsJacket, "_top", Race, output.WriteLine));
        var (topBytes, topParts, topBones) = Load(root, top.File);
        var source = RefitCore.DetectSource(topParts, topBones, "_top", Race, [neo], neo.Dir, _ => true)!;
        Assert.Null(BodyRetarget.BuildPair("_top", neo.Catalog.PathOf(source.Option), neo.Catalog.PathOf(chest), "_top",
                                           false, null, null, null, out var pair));
        var topRefit = ModelPartReader.Read(BodyRetarget.Plan(topParts, topBytes, [pair], "_top",
                                                              held: new HashSet<int>()).Model)!;
        var (bytes, jacket, _) = Load(root, xsJacket);

        var plain = RefitCore.PlanAccessoryRefit(jacket, bytes, [pair], "_top", across: false, out _);
        var kept = RefitCore.PlanAccessoryRefit(jacket, bytes, [pair], "_top", across: false, out _,
                                                outfit: new BodyRetarget.Outfit(topParts, topRefit));

        var surface = new BodySurface(pair.Target, BodySurface.CellFor(0.01f));
        int was = Through(topRefit, ModelPartReader.Read(plain.Model)!, surface, out float wasWorst);
        int now = Through(topRefit, ModelPartReader.Read(kept.Model)!, surface, out float worst);
        output.WriteLine($"{chest.Label}: {was} cells through (worst {wasWorst * 1000f:F1} mm) -> {now} "
                       + $"({worst * 1000f:F1} mm); {kept.Report.Layered} lifted; folds {plain.Report.Folded} -> "
                       + $"{kept.Report.Folded}");
        Assert.True(now <= was, $"{now} cells through, {was} before");
        Assert.True(kept.Report.Folded <= plain.Report.Folded);
        if (kept.Report.Layered == 0) Assert.Equal(plain.Model, kept.Model);
    }

    [LocalDataFact(Heartthrob, Bibo, Neolithe)]
    [Trait("Plan", "AC-outfit-fit-2")]
    public void Heartthrobs_shoe_over_its_pants_moves_only_where_the_pants_came_through()
    {
        // A shoe and the legs piece of the same option, refitted Bibo+ onto Neolithe: where nothing came through,
        // nothing moves — byte for byte.
        const string shoeRel = "bottom size/medium/chara/equipment/e6070/model/c0201e6070_sho.mdl";
        string root = LocalData.Path(Heartthrob);
        var bodies = new[] { Body(Bibo), Body(Neolithe) }.ToList();
        var neo = bodies[1];
        BodySizeCatalog? CatalogOf(string dir)
            => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;
        var pants = Assert.Single(SiblingPiece.Find(PenumbraModMeta.ReadAllRedirects(root),
                                                    BodyRetargetWriter.ReadRecord(root), shoeRel, "_dwn", Race,
                                                    output.WriteLine));
        var (pantsBytes, pantsParts, pantsBones) = Load(root, pants.File);
        var (bytes, shoe, bones) = Load(root, shoeRel);
        var reading = KeepEnds.Read("_sho", shoe, bytes)!;
        var found = KeepEnds.Find(reading, shoe, bones, Race,
                                  (view, slot) => RefitCore.DetectSource(view, bones, slot, Race, bodies, bodies[0].Dir, _ => true),
                                  CatalogOf);
        var legs = neo.Catalog.For("_dwn", Race).FirstOrDefault(o => o.Label.EndsWith("SFW M", StringComparison.Ordinal))
                   ?? neo.Catalog.For("_dwn", Race)[0];
        var refit = Assert.IsType<KeepEnds.Refit>(KeepEnds.Decide(found, reading, bones, Race, neo.Dir, CatalogOf,
            slot => slot == "_dwn" ? legs : AutoRefitDecisions.Companion(neo.Catalog.For(slot, Race), legs, null)));
        var sourceCat = CatalogOf(refit.Source.Dir)!;
        Assert.Null(KeepEnds.BuildPairs(reading, sourceCat.PathOf(refit.Source.Option), neo.Catalog.PathOf(refit.Target),
                                        refit.EndFrom == null ? null : sourceCat.PathOf(refit.EndFrom),
                                        refit.EndTo == null ? null : neo.Catalog.PathOf(refit.EndTo),
                                        false, _ => (null, null), s => s, null, null, out var limb, out var end));
        var pantsRefit = ModelPartReader.Read(BodyRetarget.Plan(pantsParts, pantsBytes, [limb with { ShapeOnly = false }],
                                                                "_dwn", held: new HashSet<int>(), replaceSkin: true,
                                                                acrossBodies: true).Model)!;

        var plain = KeepEnds.Plan(shoe, bytes, reading, limb, end, across: true);
        var kept = KeepEnds.Plan(shoe, bytes, reading, limb, end, across: true,
                                 outfit: new BodyRetarget.Outfit(pantsParts, pantsRefit));
        output.WriteLine($"{kept.Report.Layered} lifted, {kept.Report.LayerResidual * 1000f:F1} mm short; folds "
                       + $"{plain.Report.Folded} -> {kept.Report.Folded}");
        Assert.True(kept.Report.Folded <= plain.Report.Folded);
        if (kept.Report.Layered == 0) Assert.Equal(plain.Model, kept.Model);
        else Assert.NotEqual(plain.Model, kept.Model);
    }
}
