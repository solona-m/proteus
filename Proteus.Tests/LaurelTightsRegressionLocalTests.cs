using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Laurel Attire's bottoms (Jaque, e6144): culottes over dark tights whose legs and stocking are a copy of the Neolithe
/// legs' skin, 0.1 mm off it. Refitted onto Rue+ Yiggle "Watermelon Crushers - A", the user saw Rue's thigh streak through
/// the tights from the lace band to the knee — the transfer snapped the tights onto the Neolithe vertices, and Rue's skin,
/// tessellated otherwise, bulged through between them. Measured against the author's ORIGINAL file, the source body
/// detected as the automatic refit detects it, on both refit paths: the automatic refit (the whole new skin) and the
/// Studio's Body size (hidden skin cut).
/// </summary>
public class LaurelTightsRegressionLocalTests(ITestOutputHelper output)
{
    private const string Laurel = LocalData.Mods + ":[Jaque] Laurel Attire [July 2025]";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Bottoms = "valentione emissary/bottom/chara/equipment/e6144/model/c0201e6144_dwn.mdl";
    private const string WatermelonA =
        "files/legs - smallclothes/yiggle - watermelon crushers - a/chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    /// <summary>Below the lace band, which the author's own file already lets skin through (design, not the refit).</summary>
    private const float LaceBand = 0.70f;

    private static readonly BodyRetarget.Tuning Before = new(NoSnapPush: true);

    /// <param name="Final">Each garment node's move at the solve's last stage, by node: compared node for node, since the
    /// written models' vertex numbering differs wherever the hidden skin cut differs.</param>
    private sealed record Refitted(int Pushed, int Folded, int Inside, IReadOnlyList<SkinThroughCloth.Point> Through,
                                   SecondSkinWriter.Vec3[] Final, int[] Tights);

    /// <summary>Skin left through the tights below the lace band, as measured 2026-10-14: 7 points behind the knee
    /// (y 0.24-0.26 m, under the boots), the deepest 0.47 mm. No more of them, and none deeper.</summary>
    private const int MostLeft = 7;
    private const float DeepestLeft = 0.0005f;

    /// <summary>
    /// How far the rule may move a tights node: 3.9 mm, measured 2026-10-14, at the knee (y 0.54 m), where Rue's knee
    /// stands proud of the Neolithe one the tights were copied from; p99 2.7 mm. Their gap off the new skin goes from
    /// 1.0 mm (median) / 2.6 (p95) to 1.4 / 3.0 — clear of the skin, not ballooned off it.
    /// </summary>
    private const float MostLift = 0.0045f;

    /// <summary>
    /// The folds it may add: one, measured 2026-10-14 — a sliver 0.95 mm wide and 17 mm long in the tights at the lace
    /// band (y 0.74 m), which the face clear lifted 2.95 mm by one corner and the layer order then tipped over by 0.06 mm.
    /// On the dark tights, under the lace; accepted against 509 points of thigh through them.
    /// </summary>
    private const int MostNewFolds = 1;

    private static (ModelParts Garment, byte[] Bytes, BodyRetarget.SlotPair Pair, string Target) Setup()
    {
        var bodies = new[] { Neolithe, Rue }
            .Select(b => (Dir: Path.GetFileName(LocalData.Path(b)), Catalog: BodySizeCatalog.Read(LocalData.Path(b)))).ToList();
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Laurel), Bottoms));
        var garment = ModelPartReader.Read(bytes)!;
        var from = RefitCore.DetectSource(garment, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal),
                                          "_dwn", "0201", bodies, null, _ => true)!;
        Assert.Equal(Path.GetFileName(LocalData.Path(Neolithe)), from.Dir, StringComparer.OrdinalIgnoreCase);
        string source = bodies.First(b => string.Equals(b.Dir, from.Dir, StringComparison.OrdinalIgnoreCase)).Catalog.PathOf(from.Option);
        string target = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Rue), WatermelonA);
        Assert.Null(BodyRetarget.BuildPair("_dwn", source, target, "_dwn", false, null, null, null, out var pair));
        Assert.False(pair.Correspondence.SameMesh, "the bodies must be two meshes, or the rule never runs");
        return (garment, bytes, pair, target);
    }

    private static Refitted Measure(ModelParts garment, BodyRetarget.SlotPair pair, string target, BodyRetarget.Planned plan,
                                    SecondSkinWriter.Vec3[] final)
    {
        var sets = BodyRetarget.Sets.From(garment, new HashSet<int>(),
                                          BodyRetarget.PartedSkinMeshes(garment, [pair], "_dwn", true, null));
        var drawn = ModelPartReader.Read(plan.Model)!;
        var (_, outside) = SkinThroughCloth.Find(drawn);
        var tights = garment.Parts.Where(p => p.Island < 0 && p.Material.Contains("_dwn_c", StringComparison.Ordinal))
                                  .SelectMany(p => p.Triangles).Select(v => sets.NodeOf[v]).Distinct().ToArray();
        return new Refitted(plan.Report.Pushed, BodyRetarget.FoldedTriangles(sets, final), BodySizeMatch.ClothAgainst(drawn, target)!.Value.Inside,
                            outside.Where(o => o.At.Y < LaceBand).ToList(), final, tights);
    }

    /// <summary>The furthest the rule moved any tights node: between the two refits' last stages.</summary>
    private static float WorstLift(Refitted before, Refitted now)
        => now.Tights.Max(n => MathF.Sqrt(MathF.Pow(now.Final[n].X - before.Final[n].X, 2)
                                          + MathF.Pow(now.Final[n].Y - before.Final[n].Y, 2)
                                          + MathF.Pow(now.Final[n].Z - before.Final[n].Z, 2)));

    private Refitted Auto(BodyRetarget.Tuning tuning)
    {
        var (garment, bytes, pair, target) = Setup();
        SecondSkinWriter.Vec3[]? final = null;
        var plan = BodyRetarget.WithTuning(tuning, () => RefitCore.PlanRefit(
            garment, bytes, [pair], "_dwn", across: true, gameGear: false, out _,
            trace: (s, d) => { if (s == "layer-order") final = d; }));
        Assert.NotNull(final);
        return Measure(garment, pair, target, plan, final!);
    }

    private Refitted Studio(BodyRetarget.Tuning tuning)
    {
        var (garment, bytes, pair, target) = Setup();
        SecondSkinWriter.Vec3[]? final = null;
        var plan = BodyRetarget.WithTuning(tuning, () => BodyRetarget.Plan(
            garment, bytes, [pair], "_dwn", held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
            cutHidden: true, trace: (s, d) => { if (s == "layer-order") final = d; }));
        Assert.NotNull(final);
        return Measure(garment, pair, target, plan, final!);
    }

    private void NoThighThrough(string what, Refitted before, Refitted now)
    {
        float lift = WorstLift(before, now);
        float deepest = now.Through.Count > 0 ? now.Through.Max(o => o.By) : 0f;
        output.WriteLine($"{what}: skin through the tights below the lace band {before.Through.Count} -> {now.Through.Count} "
                       + $"(deepest {deepest * 1000f:F2} mm); folded {before.Folded} -> {now.Folded}; "
                       + $"inside {before.Inside} -> {now.Inside}; tights moved by the rule at most {lift * 1000f:F2} mm");
        Assert.True(before.Through.Count > 100, $"the defect as reported: {before.Through.Count} points before");
        // The snapped tights join the push: more nodes pushed, by a hair each where they were already clear.
        Assert.True(now.Pushed > before.Pushed, $"{now.Pushed} pushed, {before.Pushed} before");
        Assert.True(now.Through.Count <= MostLeft, $"{now.Through.Count} points still through");
        Assert.True(deepest <= DeepestLeft, $"skin still {deepest * 1000f:F2} mm through");
        Assert.True(now.Folded <= before.Folded + MostNewFolds, $"{now.Folded} folded, {before.Folded} before");
        Assert.True(now.Inside <= before.Inside, $"{now.Inside} inside, {before.Inside} before");
        Assert.True(lift <= MostLift, $"tights moved up to {lift * 1000f:F2} mm by the rule");
    }

    [LocalDataFact(Laurel, Neolithe, Rue)]
    [Trait("Plan", "AC-snap-push-3")]
    [Trait("Plan", "EDGE-snap-push-5")]
    [Trait("Plan", "EDGE-snap-push-10")]
    public void Laurel_tights_onto_Rue_WC_A_show_no_thigh_on_the_automatic_refit()
        => NoThighThrough("automatic refit", Auto(Before), Auto(new BodyRetarget.Tuning()));

    /// <summary>Within Neolithe, from the Neobelly legs the bottoms were made on to the plain ones: a family change the
    /// automatic refit makes, through the atlas wherever the two are not one mesh. No more folds, no more cloth inside.</summary>
    [LocalDataFact(Laurel, Neolithe)]
    [Trait("Plan", "AC-snap-push-5")]
    public void Laurel_onto_Neolithe_plain_legs_is_no_worse()
    {
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Laurel), Bottoms));
        var garment = ModelPartReader.Read(bytes)!;
        string source = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Neolithe), "neobelly legs - smallclothes/gen c small.mdl");
        string target = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Neolithe), "default legs - smallclothes/gen c small.mdl");
        Assert.Null(BodyRetarget.BuildPair("_dwn", source, target, "_dwn", false, null, null, null, out var pair));
        Assert.False(pair.Correspondence.SameMesh, "the bodies must be two meshes, or the rule never runs");
        Refitted Run(BodyRetarget.Tuning tuning)
        {
            SecondSkinWriter.Vec3[]? final = null;
            var plan = BodyRetarget.WithTuning(tuning, () => RefitCore.PlanRefit(
                garment, bytes, [pair], "_dwn", across: true, gameGear: false, out _,
                trace: (s, d) => { if (s == "layer-order") final = d; }));
            Assert.NotNull(final);
            return Measure(garment, pair, target, plan, final!);
        }
        var before = Run(Before);
        var now = Run(new BodyRetarget.Tuning());
        output.WriteLine($"Neobelly -> plain legs ({pair.Correspondence.GetType().Name}): folded {before.Folded} -> {now.Folded}; "
                       + $"inside {before.Inside} -> {now.Inside}; through {before.Through.Count} -> {now.Through.Count}");
        Assert.True(now.Folded <= before.Folded, $"{now.Folded} folded, {before.Folded} before");
        // Accepted (2026-10-14): 174 -> 175 cloth points inside the plain legs, against skin through the tights 630 -> 20.
        Assert.True(now.Inside <= before.Inside + 1, $"{now.Inside} inside, {before.Inside} before");
        Assert.True(now.Through.Count <= before.Through.Count, $"{now.Through.Count} through, {before.Through.Count} before");
    }

    [LocalDataFact(Laurel, Neolithe, Rue)]
    [Trait("Plan", "AC-snap-push-3")]
    [Trait("Plan", "EDGE-snap-push-10")]
    public void Laurel_tights_onto_Rue_WC_A_show_no_thigh_in_the_Studio()
        => NoThighThrough("Studio", Studio(Before), Studio(new BodyRetarget.Tuning()));
}
