using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Women's legs made on a nude body, made men's (cross-sex-area-coverage): their crotch skin has no place on TBSE's SFW
/// legs, which draw underwear there, so they are fitted to TBSE's nude Gen A legs instead — against the real files.
/// </summary>
public class CrossSexNudeLegsLocalTests(ITestOutputHelper o)
{
    internal const string LavaBod = LocalData.Mods + ":LavaBod+ 2.2";
    internal const string StormblessedLegs = LocalData.Mods
        + ":Stormblessed - by Solona/items/neo-ishgardian bottoms of aiming/chara/equipment/e0633/model/c0201e0633_dwn.mdl";

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    private static Vector3 At(ModelParts body, int v)
        => new(body.Positions[v * 3], body.Positions[v * 3 + 1], body.Positions[v * 3 + 2]);

    private static string TbsePath(int i) => Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Tbse), UvBakeInputs.TbseLegs[i].Rel);

    /// <summary>Where a body's skin missed on the men's legs, before and after <see cref="BodyRetarget.SourceBody.Whole"/>.</summary>
    internal sealed record Misses(UvAtlasCorrespondence.Stats Stats, int Skin, int Before, int After, Vector3 Min, Vector3 Max);

    internal static Misses? Measure(string sourcePath, string targetPath, out string? refusal)
    {
        refusal = BodyRetarget.BuildPair("_dwn", File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath), "_dwn", false,
                                         null, null, Maps(), out var pair, out var stats, targetMale: true);
        if (refusal != null || stats is not { } landed) return null;
        var src = pair.Correspondence.Source;
        var field = pair.Correspondence.Field;
        var skin = new BodySurface(src, BodySurface.CellFor(0.01f)).SkinVertices;
        var whole = BodyRetarget.SourceBody.Whole(src, field);
        var missed = skin.Where(v => field[v] == null).ToList();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (int v in missed)
        {
            var p = At(src, v);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Misses(landed, skin.Count, missed.Count, skin.Count(v => whole[v] == null), min, max);
    }

    private static string Describe(Misses m)
        => $"skin {m.Skin}, by vertex {m.Stats.VertexCoverage:P2}, by surface {m.Stats.AreaCoverage:P2}; {m.Before} unplaced, " +
           $"{m.After} still unplaced after Whole; box x {m.Min.X:0.000}..{m.Max.X:0.000} y {m.Min.Y:0.000}..{m.Max.Y:0.000} " +
           $"z {m.Min.Z:0.000}..{m.Max.Z:0.000}";

    /// <summary>Every legs option of a women's body mod, by label and file.</summary>
    private static IEnumerable<(string Label, string Path)> LegsOf(string need, Func<BodyOption, bool>? which = null)
    {
        var cat = BodySizeCatalog.Read(LocalData.Path(need));
        return cat.For("_dwn", "0201").Where(which ?? (_ => true)).Select(b => (b.Label, cat.PathOf(b)));
    }

    /// <summary>Cloth faces each garment turns over fitted to TBSE's Gen A legs, and cloth points the solve misses
    /// (measured 2026-10-24), guarded against growing.</summary>
    private const int KnownStormblessedLegsFolds = 69, KnownStormblessedLegsMissed = 0, KnownSheerLegsFolds = 8;

    private (string Name, Misses? Sfw, Misses? GenA) Both(string name, string path)
    {
        var sfw = Measure(path, TbsePath(0), out var sfwRefused);
        var genA = Measure(path, TbsePath(1), out var genARefused);
        o.WriteLine($"{name} → TBSE sfw: {(sfw is null ? "refused: " + sfwRefused : Describe(sfw))}");
        o.WriteLine($"{name} → TBSE gen a: {(genA is null ? "refused: " + genARefused : Describe(genA))}");
        return (name, sfw, genA);
    }

    [LocalDataFact(LavaBod, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-area-coverage-2")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-2")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-15")]
    [Trait("Speed", "Slow")]
    public void Nude_legs_reach_tbse_gen_a() // [AC-cross-sex-area-coverage-2] [EDGE-cross-sex-area-coverage-2] [EDGE-cross-sex-area-coverage-15]
    {
        var lava = LegsOf(LavaBod).ToList();
        Assert.Contains(lava, l => RefitCore.Normal(l.Path).EndsWith("common/64/c0201e0000_dwn.mdl", StringComparison.Ordinal));
        var nude = lava.Select(l => ("LavaBod+ " + l.Label, l.Path))
            .Concat(UvBakeInputs.BiboLegs.Take(3).Select(l => ("Bibo+ " + l.Name, Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Bibo), l.Rel))));
        foreach (var (name, sfw, genA) in nude.Select(n => Both(n.Item1, n.Item2)).ToList())
        {
            Assert.True(sfw is { } s && !CrossSexRefit.CoverageAllowed(s.Stats.VertexCoverage), $"{name} matches TBSE's SFW legs enough");
            Assert.True(genA is { } g && CrossSexRefit.CoverageAllowed(g.Stats.VertexCoverage), $"{name} matches too little of Gen A");
            Assert.Equal(0, genA!.After);
            Assert.True(CrossSexRefit.TakesNude(sfw!.Stats, genA.Stats), name);
        }
    }

    [LocalDataFact(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-area-coverage-4")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-1")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-16")]
    [Trait("Speed", "Slow")]
    public void Sfw_made_and_rue_legs_keep_tbse_sfw() // [AC-cross-sex-area-coverage-4] [EDGE-cross-sex-area-coverage-1] [EDGE-cross-sex-area-coverage-16]
    {
        var kept = new[] { ("Bibo+ SFW Undies", Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Bibo), UvBakeInputs.BiboLegs[3].Rel)) }
            .Concat(LegsOf(CrossSexRefitLocalTests.Rue, b => b.Rel.Contains("watermelon crushers - a", StringComparison.OrdinalIgnoreCase))
                        .Select(l => ("Rue+ " + l.Label, l.Path)));
        foreach (var (name, sfw, genA) in kept.Select(n => Both(n.Item1, n.Item2)).ToList())
        {
            Assert.True(sfw is { } s && CrossSexRefit.CoverageAllowed(s.Stats.VertexCoverage), $"{name} matches too little of SFW");
            Assert.False(CrossSexRefit.TakesNude(sfw!.Stats, genA?.Stats), name);
        }
    }

    /// <summary>TBSE's SFW legs against its Gen A legs: where their skin differs, and their waist edges.</summary>
    [LocalDataFact(CrossSexRefitLocalTests.Tbse)]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-13")]
    public void Tbse_sfw_and_gen_a_differ_only_at_the_crotch() // [EDGE-cross-sex-area-coverage-13]
    {
        var tbse = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        o.WriteLine("TBSE legs options: " + string.Join(", ", tbse.For("_dwn", "0101").Select(b => $"{b.Label} = {b.Rel}")));
        var sfw = ModelPartReader.Read(File.ReadAllBytes(TbsePath(0)))!;
        var genA = ModelPartReader.Read(File.ReadAllBytes(TbsePath(1)))!;
        var sfwSkin = new BodySurface(sfw, BodySurface.CellFor(0.01f)).SkinVertices;
        var genASurface = new BodySurface(genA, BodySurface.CellFor(0.01f));
        var far = new List<(Vector3 P, float D)>();
        var dists = new List<float>();
        foreach (int v in sfwSkin)
        {
            var p = At(sfw, v);
            float d = genASurface.Nearest(p, 0.05f, out var hit) ? hit.Distance : 0.05f;
            dists.Add(d);
            if (d > 0.0005f) far.Add((p, d));
        }
        dists.Sort();
        o.WriteLine($"sfw skin {sfwSkin.Count}: to gen a max {dists[^1] * 1000f:F3} mm, p99 {dists[(int)(dists.Count * 0.99)] * 1000f:F3} mm; " +
                    $"{far.Count} further than 0.5 mm");
        if (far.Count > 0)
        {
            var min = far.Aggregate(new Vector3(float.MaxValue), (a, f) => Vector3.Min(a, f.P));
            var max = far.Aggregate(new Vector3(float.MinValue), (a, f) => Vector3.Max(a, f.P));
            o.WriteLine($"  differing box x {min.X:0.000}..{max.X:0.000} y {min.Y:0.000}..{max.Y:0.000} z {min.Z:0.000}..{max.Z:0.000}");
        }
        // The waist: the open edge of the legs' skin at its top.
        var ring = OpenEdgeVertices(sfw).Where(v => At(sfw, v).Y > sfwSkin.Max(s => At(sfw, s).Y) - 0.03f).ToList();
        float worst = ring.Select(v => genASurface.Nearest(At(sfw, v), 0.05f, out var h) ? h.Distance : 0.05f)
                          .DefaultIfEmpty(0f).Max();
        o.WriteLine($"waist ring {ring.Count} vertices: furthest from gen a {worst * 1000f:F3} mm");
        // The SFW legs' skin is Gen A's less the crotch: every SFW vertex lies on Gen A, the waist above all, so the men's
        // top meets either alike and the author's size nearest the SFW body is nearest Gen A's too.
        Assert.Empty(far);
        Assert.NotEmpty(ring);
        Assert.True(worst <= 0.0001f, $"the waist is {worst * 1000f:F3} mm off");
    }

    /// <summary>Skin vertices on an edge only one skin triangle uses.</summary>
    internal static HashSet<int> OpenEdgeVertices(ModelParts body)
    {
        var count = new Dictionary<(int, int), int>();
        var weld = new Dictionary<(int, int, int), int>();
        int Key(int v)
        {
            var p = At(body, v);
            var k = MeshMath.PositionKey(BodyRetarget.ToVec(p), BodyRetarget.SnapPerMetre);
            return weld.TryGetValue(k, out int w) ? w : weld[k] = v;
        }
        foreach (var part in body.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int i = 0; i + 2 < part.Triangles.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = Key(part.Triangles[i + e]), b = Key(part.Triangles[i + (e + 1) % 3]);
                    var edge = a < b ? (a, b) : (b, a);
                    count[edge] = count.TryGetValue(edge, out int c) ? c + 1 : 1;
                }
        }
        var open = new HashSet<int>();
        foreach (var (edge, c) in count)
            if (c == 1) { open.Add(edge.Item1); open.Add(edge.Item2); }
        return open;
    }

    /// <summary>The body a women's garment reads as made on, as the size set reads it.</summary>
    internal static (BodySizeCatalog Catalog, BodyOption Option) MadeFor(string garmentNeed, string bodyNeed)
    {
        var bytes = File.ReadAllBytes(LocalData.Path(garmentNeed));
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var cat = BodySizeCatalog.Read(LocalData.Path(bodyNeed));
        var mods = new List<(string, BodySizeCatalog)> { (Path.GetFileName(LocalData.Path(bodyNeed)), cat) };
        var pick = RefitCore.DetectSource(ModelPartReader.Read(bytes)!, bones, "_dwn", "0201", mods, mods[0].Item1, _ => true);
        Assert.NotNull(pick);
        return (cat, pick!.Option);
    }

    private static string GamePathOf(string localPath)
    {
        string p = localPath.Replace('\\', '/');
        return p[p.IndexOf("chara/", StringComparison.OrdinalIgnoreCase)..].ToLowerInvariant();
    }

    internal static CrossSexRefitJob.Inputs JobInputs(string garmentNeed, string bodyNeed, int tbseLegs, IPluginLog log)
    {
        string path = LocalData.Path(garmentNeed);
        string gamePath = GamePathOf(path);
        var (madeFor, option) = MadeFor(garmentNeed, bodyNeed);
        var onto = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        return new CrossSexRefitJob.Inputs(File.ReadAllBytes(path), gamePath, CrossSexRefit.Men, madeFor, CrossSexRefitLocalTests.IsFile(madeFor, option.Rel),
            onto, CrossSexRefitLocalTests.IsFile(onto, UvBakeInputs.TbseLegs[tbseLegs].Rel),
            CrossSexRefitLocalTests.Wearer(CrossSexRefit.Men, gamePath), Maps(), log);
    }

    /// <summary>The same garment refitted between two women's bodies: what the solve misses on its own.</summary>
    private (int Missed, int Folds) SameSex(string garmentNeed, string bodyNeed, string toRel)
    {
        var garment = File.ReadAllBytes(LocalData.Path(garmentNeed));
        var (cat, option) = MadeFor(garmentNeed, bodyNeed);
        string to = Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Bibo), toRel);
        Assert.Null(BodyRetarget.BuildPair("_dwn", cat.PathOf(option), to, "_dwn", false, null, null, Maps(), out var pair));
        var plan = BodyRetarget.Plan(ModelPartReader.Read(garment)!, garment, [pair], "_dwn", replaceSkin: true,
                                     acrossBodies: true, cutHidden: true);
        return (plan.Report.Missed, CrossSexRefit.ClothFolded(garment, plan));
    }

    [LocalDataFact(LocalData.GameData, StormblessedLegs, LavaBod, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse,
                   LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-area-coverage-2")]
    [Trait("Plan", "AC-cross-sex-area-coverage-5")]
    [Trait("Plan", "AC-cross-sex-area-coverage-6")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-10")]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-15")]
    [Trait("Speed", "Slow")]
    public void Stormblessed_legs_through_the_job() // [AC-cross-sex-area-coverage-2] [AC-cross-sex-area-coverage-5] [AC-cross-sex-area-coverage-6] [EDGE-cross-sex-area-coverage-10] [EDGE-cross-sex-area-coverage-15]
    {
        var log = Substitute.For<IPluginLog>();
        var (missed, folds) = SameSex(StormblessedLegs, LavaBod, UvBakeInputs.BiboLegs[0].Rel);
        o.WriteLine($"same-sex onto Bibo+ Type A Small: {missed} missed, {folds} folds");

        // Its SFW legs picked, as Studio and the CLI may: refused as before, saying the share.
        var sfw = CrossSexRefitJob.Run(JobInputs(StormblessedLegs, LavaBod, 0, log)).Sizes.Single();
        Assert.False(sfw.SaveAllowed);
        Assert.True(sfw.Landed!.Value.VertexCoverage < 0.9f);
        Assert.Contains($"{sfw.Landed!.Value.VertexCoverage:P0}", CrossSexRefitReport.Refusal(sfw));

        // The size set's way: the SFW legs asked for, the nude ones fitted.
        var onto = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var genAOption = onto.Options.First(b => RefitCore.Normal(b.Rel) == RefitCore.Normal(UvBakeInputs.TbseLegs[1].Rel));
        var inputs = JobInputs(StormblessedLegs, LavaBod, 0, log) with
        {
            Nude = b => RefitCore.Normal(b.Rel) == RefitCore.Normal(UvBakeInputs.TbseLegs[0].Rel) ? genAOption : null,
        };
        var r = CrossSexRefitJob.Run(inputs);
        var s = r.Sizes.Single();
        Assert.Equal(genAOption.Label, s.Target.Label);
        Assert.Equal(sfw.Target.Label, s.Asked?.Label);
        Assert.True(s.AskedLanded!.Value.VertexCoverage < 0.9f);
        Assert.True(s.SaveAllowed);
        RefitGate.Assert(o, r, s, 0, RefitGate.Gates.None, minCoverage: 0.9f);
        Assert.True(s.Folds <= KnownStormblessedLegsFolds, $"{s.Folds} folds, more than the {KnownStormblessedLegsFolds} known");
        Assert.True(s.Plan!.Report.Missed <= KnownStormblessedLegsMissed, $"{s.Plan.Report.Missed} cloth points missed");
        // The bent made-for body leaves nothing unplaced on Gen A once Whole has filled it in.
        Assert.Equal(0, Measure(r.SourcePath!, TbsePath(1), out var bentRefused)!.After);
        Assert.Null(bentRefused);
        var entry = CrossSexRefitReport.Size(s, "TBSE", "legs.mdl");
        Assert.Null(entry["refusal"]);
        Assert.Equal("legs.mdl", (string?)entry["file"]);
        // Its nude crotch shows, counted against the SFW legs; the SFW legs show none of it, and an unread model is unknown.
        int? shown = CrossSexRefit.NudeSkinShown(s.Saved!.Model, TbsePath(0));
        o.WriteLine($"Gen A: {shown} of its own skin triangles shown");
        Assert.True(shown > 0, $"{shown} nude skin triangles shown");
        Assert.Equal(0, CrossSexRefit.NudeSkinShown(File.ReadAllBytes(TbsePath(0)), TbsePath(0)));
        Assert.Null(CrossSexRefit.NudeSkinShown(null, TbsePath(0)));

        // Gen A picked outright: the very same model and materials.
        var direct = CrossSexRefitJob.Run(JobInputs(StormblessedLegs, LavaBod, 1, log)).Sizes.Single();
        Assert.True(direct.SaveAllowed);
        Assert.Null(direct.Asked);
        var a = ModelPartReader.Read(s.Saved!.Model)!.Positions;
        var b = ModelPartReader.Read(direct.Saved!.Model)!.Positions;
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(MathF.Abs(a[i] - b[i]) <= 1e-5f, $"position {i / 3} differs");
        Assert.Equal((s.Saved.Materials ?? []).Select(m => (m.Name, Convert.ToHexString(m.Bytes))),
                     (direct.Saved.Materials ?? []).Select(m => (m.Name, Convert.ToHexString(m.Bytes))));
    }

    [LocalDataFact(LocalData.GameData, StormblessedLegs, LavaBod, CrossSexRefitLocalTests.Tbse)]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-4")]
    public void A_pair_the_match_refuses_never_tries_the_nude_legs() // [EDGE-cross-sex-area-coverage-4]
    {
        // No body maps: the SFW pair is refused in the match itself, before any share is measured.
        int asked = 0;
        var log = Substitute.For<IPluginLog>();
        var inputs = JobInputs(StormblessedLegs, LavaBod, 0, log) with
        {
            UvRemap = new UVRemapService(Substitute.For<IPluginLog>(), "."),
            Nude = _ => { asked++; return null; },
        };
        var s = CrossSexRefitJob.Run(inputs).Sizes.Single();
        Assert.Equal(0, asked);
        Assert.False(s.SaveAllowed && s.Refusal.Length == 0);
        Assert.Contains("body maps", CrossSexRefitReport.Refusal(s));
        Assert.Null(s.Asked);
    }

    [LocalDataFact(LocalData.GameData, CrossSexRefitLocalTests.SheerLegs, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-cross-sex-area-coverage-3")]
    public void Sheer_legs_read_as_nude_bibo_fit_gen_a() // [AC-cross-sex-area-coverage-3]
    {
        // Its skin lies exactly on Bibo+'s nude Type A (and B, C), 4 mm from SFW Undies: made nude, not SFW.
        var (_, option) = MadeFor(CrossSexRefitLocalTests.SheerLegs, CrossSexRefitLocalTests.Bibo);
        Assert.StartsWith("Type ", option.Label);
        var log = Substitute.For<IPluginLog>();
        var onto = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
        var genA = onto.Options.First(b => RefitCore.Normal(b.Rel) == RefitCore.Normal(UvBakeInputs.TbseLegs[1].Rel));
        var r = CrossSexRefitJob.Run(JobInputs(CrossSexRefitLocalTests.SheerLegs, CrossSexRefitLocalTests.Bibo, 0, log) with
        {
            Nude = _ => genA,
        });
        var s = r.Sizes.Single();
        Assert.Equal(genA.Label, s.Target.Label);
        Assert.True(s.SaveAllowed);
        RefitGate.Assert(o, r, s, 0, RefitGate.Gates.None, minCoverage: 0.9f);
        Assert.True(s.Folds <= KnownSheerLegsFolds, $"{s.Folds} folds, more than the {KnownSheerLegsFolds} known");
        Assert.Equal(0, s.Plan!.Report.Missed);
    }
}
