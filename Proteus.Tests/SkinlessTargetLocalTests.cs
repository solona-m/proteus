using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Neolithe-made garments refitted onto Muse+'s SFW legs, whose hips are modelled boxers with the skin cut away under
/// them: the pair is accepted, the boxers come in with the skin, and the rest of the refit reads them as the body.
/// Muse+ is read straight from its pack: it sets no IMC entry for its legs (asserted), so the game draws every part of
/// the option, as here.
/// </summary>
public class SkinlessTargetLocalTests(ITestOutputHelper output)
{
    private const string Neolithe = SkinlessTargetMeasureLocalTests.Neolithe;
    private const string Laurel = LocalData.Mods + ":[Jaque] Laurel Attire [July 2025]";
    private const string Bottoms = "valentione emissary/bottom/chara/equipment/e6144/model/c0201e6144_dwn.mdl";
    private const string Top = "valentione emissary/top - gameplay/chara/equipment/e6144/model/c0201e6144_top.mdl";
    private const string MuseBoxers = "SFW Textures/Install/chara/equipment/e0000/material/v0001/mt_c0201e0000_dwn_b.mtrl";
    private const string MuseChest = "Smallclothes Chest/Defined - Bare/chara/equipment/e0000/model/c0201e0000_top.mdl";

    private static readonly string[] OtherMuseLegs =
        ["Defined - Gen A", "Defined - Gen B", "Defined - Gen C", "Defined - Intersex", "Defined - Transmasc",
         "Defined - Erect", "Defined - Flaccid", "Defined - Barbie"];

    /// <summary>Muse's materials as its mod publishes them: the SFW textures option's smallclothes material.</summary>
    private static byte[]? MuseMaterial(string leaf)
    {
        try { return LocalData.PackEntry(LocalData.MusePack, $"SFW Textures/Install/chara/equipment/e0000/material/v0001/{leaf}"); }
        catch (FileNotFoundException) { return null; }
    }

    private static byte[] MuseLegs(string option) => LocalData.PackEntry(LocalData.MusePack, SkinlessTargetMeasureLocalTests.MuseLegs(option));

    private static string NeolithePath(string rel) => BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Neolithe), rel);

    private static BodyRetarget.SlotPair Pair(string slot, string sourcePath, byte[] target)
    {
        var refusal = BodyRetarget.BuildPair(slot, File.ReadAllBytes(sourcePath), target, slot, false, null, null, null,
                                             out var pair, MuseMaterial);
        Assert.Null(refusal);
        return pair;
    }

    public static TheoryData<string, string> SfwPairs() => SkinlessTargetMeasureLocalTests.Pairs();

    [LocalDataTheory(Neolithe, LocalData.MusePack)]
    [MemberData(nameof(SfwPairs))]
    [Trait("Plan", "AC-skinless-target-1")]
    public void Neolithe_SFW_legs_pair_with_every_Muse_SFW_option(string neolithe, string muse)
    {
        var pair = Pair("_dwn", NeolithePath(neolithe), MuseLegs(muse));
        Assert.True(pair.Worn);
        Assert.Contains("onto its smallclothes", pair.Correspondence.Describe());
    }

    [LocalDataTheory(Neolithe, LocalData.MusePack)]
    [MemberData(nameof(SfwPairs))]
    [Trait("Plan", "AC-skinless-target-2")]
    public void Muse_SFW_excused_points_land_on_the_worn_body(string neolithe, string muse)
    {
        var (built, refusal, m, corr) = SkinlessTargetMeasureLocalTests.Measure(neolithe, muse);
        Assert.True(built, refusal);
        var target = ModelPartReader.Read(MuseLegs(muse))!;
        var worn = BodySurface.OfBodyWorn(target, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(target)));
        var source = corr!.Source;
        int far = 0, crossed = 0, checkedPoints = 0;
        for (int v = 0; v < source.Positions.Length / 3; v++)
        {
            if (corr.Field[v] is not { } d) continue;
            var p = new Vector3(source.Positions[v * 3], source.Positions[v * 3 + 1], source.Positions[v * 3 + 2]);
            if (p.Y < 0.80f || p.Y > 1.05f) continue;
            checkedPoints++;
            var q = p + d;
            if (!worn.Nearest(q, 0.001f, out _)) far++;
            if (MathF.Abs(p.X) > UvAtlasCorrespondence.SideBand && MathF.Sign(q.X) != MathF.Sign(p.X)) crossed++;
        }
        output.WriteLine($"{neolithe} -> {muse}: {checkedPoints} hip points, {far} over 1 mm off the worn body, " +
                         $"{crossed} crossed sides; excused {m.Excused}, unprojected {m.Unprojected}, on skin {m.OnSkin}, " +
                         $"pressed flat {m.FoldedInIslands}");
        Assert.Equal(0, far);
        Assert.Equal(0, crossed);
        Assert.True(m.Unprojected <= 0.01f * m.Excused, $"{m.Unprojected} of {m.Excused} left at the carry");
        Assert.True(m.OnSkin <= 0.1f * m.Excused, $"{m.OnSkin} of {m.Excused} landed on skin");
        Assert.Equal(0, m.FoldedElsewhere);
    }

    /// <summary>The figures each other Muse+ legs option gave before this change, from NEOBELLY's SFW Small.</summary>
    [LocalDataFact(Neolithe, LocalData.MusePack)]
    [Trait("Plan", "AC-skinless-target-6")]
    public void Other_Muse_legs_options_keep_their_numbers()
    {
        var expected = new Dictionary<string, string>
        {
            ["Defined - Gen A"] = "100%", ["Defined - Gen B"] = "100%", ["Defined - Gen C"] = "100%",
            ["Defined - Intersex"] = "100%", ["Defined - Transmasc"] = "100%",
            ["Defined - Erect"] = "86%", ["Defined - Flaccid"] = "86%", ["Defined - Barbie"] = "83%",
        };
        var sourceBytes = File.ReadAllBytes(NeolithePath(SkinlessTargetMeasureLocalTests.NeolitheSfwSmall[1]));
        foreach (var option in OtherMuseLegs)
        {
            var targetBytes = MuseLegs(option);
            Assert.True(BodyCorrespondence.TryBuild(ModelPartReader.Read(sourceBytes)!, SkinlessTargetMeasureLocalTests.Uv(sourceBytes),
                                                    ModelPartReader.Read(targetBytes)!, SkinlessTargetMeasureLocalTests.Uv(targetBytes),
                                                    "legs", out var built, out string refusal), refusal);
            Assert.False(built!.TargetWorn, option);
            if (built is UvAtlasCorrespondence atlas)
            {
                output.WriteLine($"{option}: {atlas.Measured.VertexCoverage:P1}, excused {atlas.Measured.Excused}");
                Assert.Equal(0, atlas.Measured.Excused);
                Assert.Equal(expected[option], atlas.Measured.VertexCoverage.ToString("P0", System.Globalization.CultureInfo.InvariantCulture).Replace(" ", ""));
            }
            Assert.DoesNotContain("smallclothes", built.Describe());
        }
    }

    [LocalDataFact(LocalData.MusePack)]
    [Trait("Plan", "AC-skinless-target-1")]
    public void Muse_pack_sets_no_IMC_for_its_legs()
    {
        using var zip = ZipFile.OpenRead(LocalData.Path(LocalData.MusePack));
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(entry.Open());
            string json = reader.ReadToEnd();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var m in Manipulations(doc.RootElement))
                Assert.False(m.TryGetProperty("Type", out var t) && t.GetString() == "Imc"
                             && m.GetRawText().Contains("\"PrimaryId\":0,", StringComparison.Ordinal),
                             $"{entry.FullName} sets an IMC entry for e0000");
        }

        static IEnumerable<System.Text.Json.JsonElement> Manipulations(System.Text.Json.JsonElement e)
        {
            if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "Manipulations" && p.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var m in p.Value.EnumerateArray()) yield return m;
                    foreach (var inner in Manipulations(p.Value)) yield return inner;
                }
            }
            else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in e.EnumerateArray())
                    foreach (var inner in Manipulations(item)) yield return inner;
        }
    }

    [LocalDataFact(Laurel, Neolithe, LocalData.MusePack)]
    [Trait("Plan", "EDGE-skinless-target-33")]
    public void Materials_survive_the_refit_s_finishing_passes()
    {
        var plan = RefitBottoms("Defined - SFW Flat", auto: false, out _);
        Assert.NotNull(plan.Materials);
        string temp = Path.Combine(Path.GetTempPath(), "proteus-skinless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string target = Path.Combine(temp, "c0201e0000_dwn.mdl");
            File.WriteAllBytes(target, MuseLegs("Defined - SFW Flat"));
            var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            string source = NeolithePath(SkinlessTargetMeasureLocalTests.NeolitheSfwSmall[0]);
            Assert.Same(plan.Materials, RefitCore.WithBodySkin(plan, target, log).Materials);
            Assert.Same(plan.Materials, RefitCore.MatchSkinToBody(plan, source, target, null, false, log).Materials);
            Assert.Same(plan.Materials, RefitCore.Finish(plan, default, null, target, log).Saved!.Materials);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [LocalDataFact(Laurel, Neolithe, LocalData.MusePack)]
    [Trait("Plan", "EDGE-skinless-target-22")]
    public void Without_the_underwear_material_the_bottoms_keep_their_own_skin()
    {
        // The body mod's material cannot be read: the legs keep the garment's own skin, whole, and nothing is published.
        var bodies = new[] { Neolithe }
            .Select(b => (Dir: Path.GetFileName(LocalData.Path(b)), Catalog: BodySizeCatalog.Read(LocalData.Path(b)))).ToList();
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Laurel), Bottoms));
        var garment = ModelPartReader.Read(bytes)!;
        var from = RefitCore.DetectSource(garment, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal),
                                          "_dwn", "0201", bodies, null, _ => true)!;
        Assert.Null(BodyRetarget.BuildPair("_dwn", File.ReadAllBytes(bodies[0].Catalog.PathOf(from.Option)),
                                           MuseLegs("Defined - SFW Flat"), "_dwn", false, null, null, null, out var pair));
        var plan = BodyRetarget.Plan(garment, bytes, [pair], "_dwn", held: new HashSet<int>(), replaceSkin: true,
                                     acrossBodies: true, cutHidden: true);
        output.WriteLine($"swap {plan.Report.Swap}; fallback {plan.Report.SmallclothesFallback}");
        Assert.Equal(1, plan.Report.SmallclothesFallback);
        Assert.Null(plan.Materials);
        Assert.Equal(0, plan.Report.Swap?.Removed ?? 0);
        int skinBefore = garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).Sum(p => p.TriangleCount);
        int skinAfter = ModelPartReader.Read(plan.Model)!.Parts
                                       .Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).Sum(p => p.TriangleCount);
        Assert.Equal(skinBefore, skinAfter);
    }

    private BodyRetarget.Planned RefitBottoms(string museLegs, bool auto, out ModelParts garment)
    {
        var bodies = new[] { Neolithe }
            .Select(b => (Dir: Path.GetFileName(LocalData.Path(b)), Catalog: BodySizeCatalog.Read(LocalData.Path(b)))).ToList();
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Laurel), Bottoms));
        garment = ModelPartReader.Read(bytes)!;
        var from = RefitCore.DetectSource(garment, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal),
                                          "_dwn", "0201", bodies, null, _ => true)!;
        var pair = Pair("_dwn", bodies[0].Catalog.PathOf(from.Option), MuseLegs(museLegs));
        var g = garment;
        return auto
            ? RefitCore.PlanRefit(g, bytes, [pair], "_dwn", across: true, gameGear: false, out _)
            : BodyRetarget.Plan(g, bytes, [pair], "_dwn", held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
                                cutHidden: true);
    }

    [LocalDataTheory(Laurel, Neolithe, LocalData.MusePack)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Plan", "AC-skinless-target-8")]
    public void Laurel_bottoms_onto_Muse_SFW_Flat_draw_Muse_s_boxers(bool auto)
    {
        var plan = RefitBottoms("Defined - SFW Flat", auto, out _);
        var swap = plan.Report.Swap;
        output.WriteLine($"{(auto ? "auto" : "studio")}: swap {swap}; fallback {plan.Report.SmallclothesFallback}; " +
                         $"materials {string.Join(", ", plan.Materials?.Select(m => m.Name) ?? [])}");
        Assert.Equal(0, plan.Report.SmallclothesFallback);
        Assert.NotNull(swap);
        Assert.True(swap!.Value.Smallclothes > 0, "no boxers brought in");
        var material = Assert.Single(plan.Materials!);
        Assert.Equal(LocalData.PackEntry(LocalData.MusePack, MuseBoxers), material.Bytes);
        Assert.Contains("/" + material.Name, SecondSkinWriter.Parse(plan.Model).MatNames);
        Assert.True(SecondSkinWriter.Parse(plan.Model).MatNames.Count <= SecondSkinWriter.MaxMaterials);

        // The boxers mesh is drawn, and not over its whole: the author's cut carries onto it.
        var drawn = ModelPartReader.Read(plan.Model)!;
        int boxerTris = drawn.Parts.Where(p => p.Island < 0 && p.Material == "/" + material.Name).Sum(p => p.TriangleCount);
        output.WriteLine($"boxers drawn: {boxerTris} triangles of {swap.Value.Smallclothes}");
        Assert.InRange(boxerTris, 1, swap.Value.Smallclothes);

        // The boxers as written are a valid surface: no triangle without area, none out of range.
        int vc = drawn.Positions.Length / 3, degenerate = 0;
        foreach (var part in drawn.Parts.Where(p => p.Island < 0 && p.Material == "/" + material.Name))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                Assert.True(a < vc && b < vc && c < vc);
                Vector3 P(int v) => new(drawn.Positions[v * 3], drawn.Positions[v * 3 + 1], drawn.Positions[v * 3 + 2]);
                if (Vector3.Cross(P(b) - P(a), P(c) - P(a)).LengthSquared() < 1e-16f) degenerate++;
            }
        Assert.Equal(0, degenerate);

        // No more folded than the same garment onto a Muse option with skin there.
        var genA = RefitBottoms("Defined - Gen A", auto, out _);
        output.WriteLine($"folded: {plan.Report.Folded} onto SFW Flat, {genA.Report.Folded} onto Gen A");
        Assert.True(plan.Report.Folded <= genA.Report.Folded, $"{plan.Report.Folded} folded, {genA.Report.Folded} onto Gen A");
        Assert.Null(genA.Materials);
    }

    [LocalDataFact(Laurel, Neolithe, LocalData.MusePack)]
    [Trait("Plan", "AC-skinless-target-9")]
    public void Hip_cloth_takes_the_worn_body_s_weights()
    {
        var plan = RefitBottoms("Defined - SFW Flat", auto: false, out _);
        var target = ModelPartReader.Read(MuseLegs("Defined - SFW Flat"))!;
        var targetSkin = ModelSkinReader.Read(MuseLegs("Defined - SFW Flat"), null, null)!;
        var worn = BodySurface.OfBodyWorn(target, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(target)));
        var drawn = ModelPartReader.Read(plan.Model)!;
        var skin = ModelSkinReader.Read(plan.Model, null, null)!;

        int hip = 0, agree = 0, close = 0;
        foreach (int v in drawn.Parts.Where(p => p.Island < 0 && p.Material.Contains("e6144", StringComparison.Ordinal))
                                     .SelectMany(p => p.Triangles).Distinct())
        {
            var p = new Vector3(drawn.Positions[v * 3], drawn.Positions[v * 3 + 1], drawn.Positions[v * 3 + 2]);
            if (p.Y < 0.80f || p.Y > 1.05f || !worn.Nearest(p, 0.02f, out var hit)) continue;
            hip++;
            var body = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (corner, w) in new[] { (hit.A, hit.U), (hit.B, hit.V), (hit.C, hit.W) })
                foreach (var (bone, x) in Weights(targetSkin, corner)) body[bone] = body.GetValueOrDefault(bone) + x * w;
            string bodyTop = body.MaxBy(kv => kv.Value).Key;
            var cloth = Weights(skin, v).GroupBy(w => w.Bone).ToDictionary(g => g.Key, g => g.Sum(w => w.Weight));
            string clothTop = cloth.MaxBy(kv => kv.Value).Key;
            if (bodyTop == clothTop) agree++;
            float l1 = body.Keys.Union(cloth.Keys).Sum(b => MathF.Abs(body.GetValueOrDefault(b) - cloth.GetValueOrDefault(b)));
            if (l1 <= 0.1f) close++;
        }
        output.WriteLine($"hip cloth points: {hip}; their heaviest bone the worn body's there: {agree} ({(float)agree / hip:P1}); " +
                         $"within 0.1 L1 of the worn body's weights: {close} ({(float)close / hip:P1})");
        // Across body mods the cloth keeps its author's weighting and takes the change between the two bodies, so it never
        // equals the boxers' weights outright. Measured 2026-10-20: 92.1% share the boxers' heaviest bone there, 77.5% sit
        // within 0.1 (L1) of their weights.
        Assert.True(hip > 100, $"only {hip} hip cloth points");
        Assert.True(agree >= 0.9f * hip, $"{agree} of {hip}");
        Assert.True(close >= 0.75f * hip, $"{close} of {hip} within 0.1 L1");
    }

    private static IEnumerable<(string Bone, float Weight)> Weights(XivLiveMesh.SkinnedMesh skin, int v)
    {
        for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
        {
            float w = skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
            int b = skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
            if (w > 0f && b < skin.BoneNames.Length) yield return (skin.BoneNames[b], w);
        }
    }

    /// <summary>
    /// Laurel's top tucks its hem into the body as its author made it: over the hips, 274 cloth points sit more than
    /// 1 mm inside Neolithe's legs, the deepest 25.9 mm; refitted onto Muse "Defined - Gen A" (skin there), 266 and
    /// 26.6 mm. Measured 2026-10-20. The push-out undoes the clips a refit makes and leaves an author's tuck, so over
    /// Muse's boxers the hem may sit no deeper in than that.
    /// </summary>
    private const int AuthoredTucked = 274;

    private const float AuthoredDeepest = 0.0259f;

    /// <summary>What the refit may add to the author's own tuck's depth (2 mm).</summary>
    private const float TuckSlack = 0.002f;

    [LocalDataFact(Laurel, Neolithe, LocalData.MusePack)]
    [Trait("Plan", "AC-skinless-target-9")]
    public void A_long_top_s_hem_stays_outside_Muse_s_boxers()
    {
        var bodies = new[] { Neolithe }
            .Select(b => (Dir: Path.GetFileName(LocalData.Path(b)), Catalog: BodySizeCatalog.Read(LocalData.Path(b)))).ToList();
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Laurel), Top));
        var garment = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var chestFrom = RefitCore.DetectSource(garment, bones, "_top", "0201", bodies, null, _ => true)!;
        var chest = Pair("_top", bodies[0].Catalog.PathOf(chestFrom.Option), LocalData.PackEntry(LocalData.MusePack, MuseChest));
        var legs = Pair("_dwn", NeolithePath(SkinlessTargetMeasureLocalTests.NeolitheSfwSmall[0]), MuseLegs("Defined - SFW Flat"));

        var plan = BodyRetarget.Plan(garment, bytes, [chest, legs], "_top", held: new HashSet<int>(), replaceSkin: true,
                                     acrossBodies: true, cutHidden: true);
        var target = legs.Target;
        var worn = BodySurface.OfBodyWorn(target, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(target)));
        var drawn = ModelPartReader.Read(plan.Model)!;
        int hem = 0, inside = 0;
        float deepest = 0f;
        foreach (int v in drawn.Parts.Where(p => p.Island < 0 && p.Material.Contains("e6144", StringComparison.Ordinal))
                                     .SelectMany(p => p.Triangles).Distinct())
        {
            var p = new Vector3(drawn.Positions[v * 3], drawn.Positions[v * 3 + 1], drawn.Positions[v * 3 + 2]);
            if (p.Y < 0.80f || p.Y > 1.05f || !worn.Nearest(p, 0.05f, out var hit)) continue;
            hem++;
            float depth = -Vector3.Dot(p - hit.Point, hit.Normal);
            if (depth > 0.001f) inside++;
            deepest = MathF.Max(deepest, depth);
        }
        output.WriteLine($"top's cloth over the hips: {hem} points; more than 1 mm inside Muse's boxers or skin: {inside}; " +
                         $"deepest {deepest * 1000f:F2} mm");
        Assert.True(hem > 0, "the top does not reach the hips");
        Assert.True(inside <= AuthoredTucked, $"{inside} points inside, the author tucked {AuthoredTucked}");
        Assert.True(deepest <= AuthoredDeepest + TuckSlack, $"{deepest * 1000f:F2} mm deep");
    }
}
