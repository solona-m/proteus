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
/// The refits the thick-shell rules (<see cref="BodyRetarget.Tuning.NoWallPair"/>, <see cref="BodyRetarget.Tuning.NoWallCover"/>)
/// and the rim lift's fold guard (<see cref="BodyRetarget.Tuning.NoRimTurnGuard"/>) must not make worse: one size of an
/// author's garment onto another body the author made a size for, with the size set's parameters, against the author's
/// own size — and the same garment across body mods, against its own folds and cloth inside.
/// </summary>
public class SkirtHipsRegressionLocalTests(ITestOutputHelper output)
{
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    private const string OldThing = LocalData.Mods + ":This Old Thing - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Sirius = LocalData.Mods + ":Sirius - by Solona";
    private const string Race = "0201";

    private static readonly BodyRetarget.Tuning Old = new(NoWallPair: true, NoWallCover: true, NoRimTurnGuard: true,
                                                          NoSnapPush: true);

    private sealed record Refitted(ModelParts Model, int Folded, int Inside);

    /// <summary><paramref name="fromRel"/> refitted onto the body option the author's <paramref name="toRel"/> was made
    /// on, as the size set refits it (the skin taken, its default Keep shape pieces).</summary>
    private static (Refitted Now, Refitted Before, ModelParts Author, int AuthorInside) Refit(string mod, string fromRel, string toRel, string slot,
                                                                         string[] bodyNeeds)
    {
        var bodies = bodyNeeds.Select(b => (Dir: Path.GetFileName(LocalData.Path(b)), Catalog: BodySizeCatalog.Read(LocalData.Path(b))))
                              .ToList();
        AutoRefitDecisions.Pick Detect(byte[] b, ModelParts m)
            => RefitCore.DetectSource(m, new HashSet<string>(SecondSkinWriter.Parse(b).BoneNames, StringComparer.Ordinal), slot,
                                      Race, bodies, null, _ => true)!;
        BodySizeCatalog CatalogOf(string dir) => bodies.First(x => string.Equals(x.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;

        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), fromRel));
        var garment = ModelPartReader.Read(bytes)!;
        var authorBytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), toRel));
        var author = ModelPartReader.Read(authorBytes)!;
        var from = Detect(bytes, garment);
        var to = Detect(authorBytes, author);
        Assert.False(AutoRefitDecisions.SameFile(from.Dir, from.Option, to.Dir, to.Option), $"{fromRel} and {toRel} share a body");
        Assert.Null(BodyRetarget.BuildPair(slot, CatalogOf(from.Dir).PathOf(from.Option), CatalogOf(to.Dir).PathOf(to.Option), slot,
                                           false, null, null, null, out var pair));
        bool across = !string.Equals(from.Dir, to.Dir, StringComparison.OrdinalIgnoreCase);
        var pieces = RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment), out _);
        string target = CatalogOf(to.Dir).PathOf(to.Option);

        // Folds counted on the solve's last stage: the report's count is taken at the fold relax, before the passes the
        // rules change.
        var sets = BodyRetarget.Sets.From(garment, new HashSet<int>(),
                                          BodyRetarget.PartedSkinMeshes(garment, [pair], slot, true, null));
        Refitted Run(BodyRetarget.Tuning t)
        {
            SecondSkinWriter.Vec3[]? final = null;
            var plan = BodyRetarget.WithTuning(t, () => BodyRetarget.Plan(garment, bytes, [pair], slot, held: new HashSet<int>(),
                                                                           replaceSkin: true, acrossBodies: across, clearBody: false,
                                                                           cutHidden: true, keepShape: pieces,
                                                                           trace: (s, d) => { if (s == "layer-order") final = d; }));
            Assert.NotNull(final);
            var model = ModelPartReader.Read(plan.Model)!;
            return new Refitted(model, BodyRetarget.FoldedTriangles(sets, final!), BodySizeMatch.ClothAgainst(model, target)!.Value.Inside);
        }
        return (Run(new BodyRetarget.Tuning()), Run(Old), author, BodySizeMatch.ClothAgainst(author, target)!.Value.Inside);
    }

    /// <summary>The mean distance from each cloth point of the author's own size to the nearest cloth point of the refit.</summary>
    private static float FromAuthor(ModelParts author, ModelParts refit)
    {
        static List<Vector3> Cloth(ModelParts m)
        {
            var seen = new HashSet<int>();
            var points = new List<Vector3>();
            foreach (var part in m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
                foreach (int v in part.Triangles)
                    if (seen.Add(v)) points.Add(new Vector3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]));
            return points;
        }
        const float cell = 0.01f;
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        var grid = new Dictionary<(int, int, int), List<Vector3>>();
        foreach (var p in Cloth(refit))
        {
            if (!grid.TryGetValue(Key(p), out var list)) grid[Key(p)] = list = [];
            list.Add(p);
        }
        double sum = 0;
        var mine = Cloth(author);
        foreach (var p in mine)
        {
            var (x, y, z) = Key(p);
            float best = 0.05f;
            for (int r = 0; r <= 4 && best > r * cell; r++)
                for (int i = -r; i <= r; i++)
                for (int j = -r; j <= r; j++)
                for (int k = -r; k <= r; k++)
                {
                    if (Math.Max(Math.Abs(i), Math.Max(Math.Abs(j), Math.Abs(k))) != r) continue;
                    if (!grid.TryGetValue((x + i, y + j, z + k), out var list)) continue;
                    foreach (var q in list) best = MathF.Min(best, Vector3.Distance(p, q));
                }
            sum += best;
        }
        return (float)(sum / mine.Count);
    }

    private void NoWorse(string what, (Refitted Now, Refitted Before, ModelParts Author, int AuthorInside) r, bool againstAuthor,
                         float slack = 0f)
    {
        float now = FromAuthor(r.Author, r.Now.Model), before = FromAuthor(r.Author, r.Before.Model);
        output.WriteLine($"{what}: from the author's own {before * 1000f:F3} -> {now * 1000f:F3} mm; folded {r.Before.Folded} -> "
                       + $"{r.Now.Folded}; inside {r.Before.Inside} -> {r.Now.Inside} (the author's own {r.AuthorInside})");
        if (againstAuthor) Assert.True(now <= before + 1e-5f + slack, $"{now * 1000f:F3} mm from the author's own, {before * 1000f:F3} before");
        Assert.True(r.Now.Folded <= r.Before.Folded, $"{r.Now.Folded} folded, {r.Before.Folded} before");
        // Against the author's own size, cloth the author put inside the body is theirs (Geometric's Large: 364 points
        // inside Bibo+ Large): no further from the author's count. Across body mods, no more than before.
        if (againstAuthor)
            Assert.True(Math.Abs(r.Now.Inside - r.AuthorInside) <= Math.Abs(r.Before.Inside - r.AuthorInside),
                        $"{r.Now.Inside} inside, {r.Before.Inside} before, the author's own {r.AuthorInside}");
        else
            Assert.True(r.Now.Inside <= r.Before.Inside, $"{r.Now.Inside} inside, {r.Before.Inside} before");
    }

    [LocalDataFact(Geometric, Bibo)]
    [Trait("Plan", "AC-skirt-hips-4")]
    public void Geometric_Small_to_Large_within_Bibo_is_no_further_from_the_authors_Large()
        => NoWorse("Geometric skirt Small -> Large",
                   Refit(Geometric, "leg size/small/chara/equipment/e6010/model/c0201e6010_dwn.mdl",
                         "leg size/large/chara/equipment/e6010/model/c0201e6010_dwn.mdl", "_dwn", [Bibo]), againstAuthor: true);

    [LocalDataFact(OldThing, Rue)]
    [Trait("Plan", "AC-skirt-hips-4")]
    public void This_Old_Thing_between_its_own_sizes_keeps_the_authors_fit()
        => NoWorse("This Old Thing Rue M -> Rue L",
                   Refit(OldThing, "size/rue m/chara/equipment/e6255/model/c0201e6255_top.mdl",
                         "size/rue l/chara/equipment/e6255/model/c0201e6255_top.mdl", "_top", [Rue]), againstAuthor: true);

    /// <summary>"Sirius", a corset over a blouse — a lined garment, where a wall paired wrongly would show — from its plain
    /// L onto the sizes its author made for other bodies.</summary>
    public static TheoryData<string> SiriusTargets => new() { "Rue L", "YAB Buff L" };

    [LocalDataTheory(Sirius, Neolithe, Rue, Yab)]
    [MemberData(nameof(SiriusTargets))]
    [Trait("Plan", "AC-skirt-hips-4")]
    [Trait("Plan", "AC-snap-push-5")]
    public void Sirius_onto_other_bodies_is_no_worse(string size)
        => NoWorse($"Sirius L -> {size}",
                   Refit(Sirius, "size/Sirius/L/chara/equipment/e0154/model/c0201e0154_top.mdl",
                         $"size/Sirius/{size}/chara/equipment/e0154/model/c0201e0154_top.mdl", "_top", [Neolithe, Rue, Yab]),
                   // Accepted (build 1261): onto YAB Buff L the corset's two walls moved together end 0.024 mm (of 2.4)
                   // further from the author's own; folds 80 -> 60.
                   againstAuthor: true, slack: size == "YAB Buff L" ? 0.00003f : 0f);

    /// <summary>The other way: from Rue+ onto Neolithe, where the snapped cloth (<see cref="BodyRetarget.Tuning.NoSnapPush"/>)
    /// meets the creases of a body tessellated otherwise.</summary>
    [LocalDataFact(OldThing, Rue, Neolithe)]
    [Trait("Plan", "AC-snap-push-5")]
    public void This_Old_Thing_Rue_L_onto_Neolithe_L_keeps_the_authors_fit()
        => NoWorse("This Old Thing Rue L -> Neolithe L",
                   Refit(OldThing, "size/rue l/chara/equipment/e6255/model/c0201e6255_top.mdl",
                         "size/neolithe l/chara/equipment/e6255/model/c0201e6255_top.mdl", "_top", [Rue, Neolithe]), againstAuthor: true);

    [LocalDataFact(OldThing, Rue, Neolithe)]
    [Trait("Plan", "AC-skirt-hips-4")]
    [Trait("Plan", "AC-snap-push-5")]
    public void This_Old_Thing_L_onto_Rue_L_is_no_worse()
        => NoWorse("This Old Thing Neolithe L -> Rue L",
                   Refit(OldThing, "size/neolithe l/chara/equipment/e6255/model/c0201e6255_top.mdl",
                         "size/rue l/chara/equipment/e6255/model/c0201e6255_top.mdl", "_top", [Rue, Neolithe]), againstAuthor: false);
}
