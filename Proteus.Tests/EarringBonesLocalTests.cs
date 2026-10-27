using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Proteus.Services;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// TBSE rigs its legs to the earring nodes <c>n_ear_*</c>; men's pants refitted for women are saved without them
/// (earring-bones). Step 0 measures, on the real files, where those weights are and what the skeletons and Bibo+ make of
/// them — the numbers the target bone is picked from.
/// </summary>
public class EarringBonesLocalTests(ITestOutputHelper o)
{
    private const string TwillTbseX = LocalData.Mods + ":-Twill Trousers- [YAB, TBSE, TBSE-X]/male- legs style/tbse-x/chara/equipment/e6269/model/c0101e6269_dwn.mdl";
    private const string TbseXSfwLegs = "p1/legs (smallclothes legs)/sfw/chara/equipment/e0000/model/c0101e0000_dwn.mdl";

    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    /// <summary>Men's legs found naming earring nodes (2026-10-22, every c0101 _dwn under the mods folder but TBSE's).</summary>
    private const string MaleBottom = LocalData.Mods + ":Ruffles - by Solona/male bottom/chara/equipment/e6010/model/c0101e6010_dwn.mdl";
    private const string Towels = LocalData.Mods + ":his and hers bathtowels - by solona (1)/default group/chara/equipment/e6019/model/c0101e6019_dwn.mdl";
    private static readonly string[] Garments = [Chaps, Pantalettes, MaleBottom, Towels];

    private static bool IsEar(string bone) => bone.StartsWith("n_ear_", StringComparison.Ordinal);

    [LocalDataFact(LocalData.GameData, CrossSexRefitLocalTests.TwillTbse, TwillTbseX, CrossSexRefitLocalTests.Tbse,
                   CrossSexRefitLocalTests.TbseX, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.IvcsMen,
                   LocalData.TbseUvMaps, Chaps, Pantalettes, MaleBottom, Towels,
                   Skip = "Step 0 diagnostic (asserts nothing, ~1 min): remove Skip to print the table")]
    public void Earring_nodes_measured()
    {
        var bibo = ModelSkinReader.Read(File.ReadAllBytes(Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Bibo),
                                                                       UvBakeInputs.BiboLegs[0].Rel)), null, null)!;
        o.WriteLine("== skeletons: parent chain of each earring node ==");
        var skeletons = new (string Name, byte[]? Bytes)[]
        {
            ("game c0101", GameData.Value.GetFile(OtherRaceWearer.BaseSkeletonPath(101))?.Data),
            ("IVCS c0101", File.ReadAllBytes(LocalData.Path(CrossSexRefitLocalTests.IvcsMen))),
            ("game c0201", GameData.Value.GetFile(OtherRaceWearer.BaseSkeletonPath(201))?.Data),
        };
        foreach (var (name, bytes) in skeletons)
        {
            var parents = HavokSkeleton.Read(bytes!)!.ParentMap();
            foreach (string ear in parents.Keys.Where(IsEar).Order(StringComparer.Ordinal))
            {
                var chain = new List<string>();
                for (string? at = parents[ear]; at != null && chain.Count < 12; at = parents.GetValueOrDefault(at)) chain.Add(at);
                o.WriteLine($"{name}: {ear} → {string.Join(" → ", chain)}");
            }
        }

        var inputs = new List<(string Name, byte[] Bytes)>
        {
            ("Twill TBSE garment", File.ReadAllBytes(LocalData.Path(CrossSexRefitLocalTests.TwillTbse))),
            ("Twill TBSE-X garment", File.ReadAllBytes(LocalData.Path(TwillTbseX))),
            ("TBSE-X legs sfw", File.ReadAllBytes(Path.Combine(LocalData.Path(CrossSexRefitLocalTests.TbseX), TbseXSfwLegs))),
        };
        foreach (var (name, rel) in UvBakeInputs.TbseLegs)
            inputs.Add(($"TBSE legs {name}", File.ReadAllBytes(Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Tbse), rel))));
        foreach (var (name, bytes) in inputs) Measure(name, bytes, bibo);

        // What today's refit saves: each garment made on TBSE's SFW legs, onto Bibo+.
        foreach (string garmentPath in Garments.Select(LocalData.Path).Prepend(LocalData.Path(CrossSexRefitLocalTests.TwillTbse)))
        {
            const string name = "sfw";
            string rel = UvBakeInputs.TbseLegs[0].Rel;
            string gamePath = garmentPath.Replace('\\', '/')[garmentPath.Replace('\\', '/').IndexOf("chara/", StringComparison.Ordinal)..];
            if (!garmentPath.Contains("Twill", StringComparison.Ordinal)) Measure(Path.GetFileName(garmentPath) + " garment", File.ReadAllBytes(garmentPath), bibo);
            var madeFor = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Tbse));
            var onto = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Bibo));
            var r = CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs(File.ReadAllBytes(garmentPath), gamePath, 201, madeFor,
                CrossSexRefitLocalTests.IsFile(madeFor, rel), onto, CrossSexRefitLocalTests.IsFile(onto, UvBakeInputs.BiboLegs[0].Rel),
                CrossSexRefitLocalTests.Wearer(201, gamePath),
                new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps)),
                NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>()));
            o.WriteLine($"== today's save, {Path.GetFileName(garmentPath)} made on TBSE {name}: refusal '{r.Refusal}' / '{r.Sizes.SingleOrDefault()?.Refusal}'");
            if (r.Sizes.SingleOrDefault()?.Saved?.Model is { } saved) Measure($"saved, made on TBSE {name}", saved, bibo);
        }
    }

    private const string Chaps = LocalData.Mods + ":Rogue - by Solona/anemos chaps/chara/equipment/e0488/model/c0101e0488_dwn.mdl";
    private const string Pantalettes = LocalData.Mods + ":Ruffles - by Solona/gryphonskin pantalettes/chara/equipment/e6010/model/c0101e6010_dwn.mdl";

    private CrossSexRefitJob.Inputs Inputs(string garmentNeed, string madeForNeed, string madeForRel, Dalamud.Plugin.Services.IPluginLog log)
    {
        string garmentPath = LocalData.Path(garmentNeed);
        string p = garmentPath.Replace('\\', '/');
        string gamePath = p[p.IndexOf("chara/", StringComparison.OrdinalIgnoreCase)..].ToLowerInvariant();
        var madeFor = BodySizeCatalog.Read(LocalData.Path(madeForNeed));
        var onto = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Bibo));
        return new CrossSexRefitJob.Inputs(File.ReadAllBytes(garmentPath), gamePath, 201, madeFor,
            CrossSexRefitLocalTests.IsFile(madeFor, madeForRel), onto, CrossSexRefitLocalTests.IsFile(onto, UvBakeInputs.BiboLegs[0].Rel),
            CrossSexRefitLocalTests.Wearer(201, gamePath),
            new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps)), log);
    }

    private static bool Logged(Dalamud.Plugin.Services.IPluginLog log, string template)
        => log.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Information" && c.GetArguments()[0] as string == template);

    /// <summary>
    /// TBSE-rigged men's pants made women's on Bibo+: saved naming no earring node, every vertex whole, the renames said
    /// in the log and the report, the bend as it always was, and the refit's gates still held.
    /// </summary>
    [LocalDataTheory(LocalData.GameData, Chaps, Pantalettes, CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.Bibo,
                     LocalData.TbseUvMaps)]
    [InlineData(Chaps)]
    [InlineData(Pantalettes)]
    [Trait("Plan", "AC-earring-bones-1")]
    [Trait("Plan", "AC-earring-bones-2")]
    [Trait("Plan", "AC-earring-bones-3")]
    [Trait("Plan", "AC-earring-bones-4")]
    public void Tbse_rigged_pants_refit_for_women_lose_their_earrings(string garment) // [AC-earring-bones-1] [AC-earring-bones-2] [AC-earring-bones-3] [AC-earring-bones-4]
    {
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var inputs = Inputs(garment, CrossSexRefitLocalTests.Tbse, UvBakeInputs.TbseLegs[0].Rel, log);
        var r = CrossSexRefitJob.Run(inputs);
        Assert.True(r.Refusal.Length == 0, r.Refusal);
        var s = Assert.Single(r.Sizes);
        Assert.True(s.Refusal.Length == 0, s.Refusal);
        o.WriteLine(CrossSexRefitLocalTests.Describe(Path.GetFileName(inputs.GamePath), r, s));
        Assert.True(s.SaveAllowed);

        Assert.Equal(["n_ear_b_l", "n_ear_b_r"], (r.Earrings ?? []).Select(m => m.From));
        Assert.All(r.Earrings!, m => Assert.Equal("j_kosi", m.To));
        Assert.Empty(r.Moved ?? []);

        var saved = ModelSkinReader.Read(s.Saved!.Model, null, null)!;
        Assert.DoesNotContain(saved.BoneNames, IsEar);
        for (int v = 0; v < saved.VertexCount; v++)
        {
            float sum = 0f;
            for (int k = 0; k < SkinnedMesh.MaxInfluences; k++) sum += saved.BoneWeights[v * SkinnedMesh.MaxInfluences + k];
            Assert.True(MathF.Abs(sum - 1f) <= 1f / 255f + 1e-4f, $"vertex {v} weighs {sum}");
        }

        // The made-for body was renamed the same way.
        Assert.DoesNotContain(OtherRaceRefit.Weighted(ModelSkinReader.Read(File.ReadAllBytes(r.SourcePath!), null, null)!), IsEar);
        Assert.Contains("n_ear_b_l", BakedBodyCatalog.RiggedBefore(r.SourcePath!)!);

        Assert.True(Logged(log, "[Proteus] retarget: earring bones moved for c{0:D4}: {1}"), "the renames were not logged");
        var report = CrossSexRefitReport.Report(r, garment, inputs.GamePath, 101, 201, false, new System.Text.Json.Nodes.JsonArray());
        Assert.Equal(2, report["movedBones"]!.AsArray().Count(m => (string?)m!["how"] == "earring"));

        // The bend is the one the garment always had: the bake bent the earring nodes as the hip already.
        var unrenamed = ModelSkinReader.Read(r.Route.Into(inputs.Garment, r.Wearer!, out string why)!, null, null)!.Positions;
        Assert.True(why.Length == 0, why);
        var baked = ModelSkinReader.Read(r.Baked!, null, null)!.Positions;
        Assert.Equal(unrenamed.Length, baked.Length);
        for (int v = 0; v < baked.Length; v++)
            Assert.True(Vector3.Distance(unrenamed[v], baked[v]) <= 1e-6f, $"vertex {v} bent differently");

        // Across the sexes the folds are said, not gated.
        RefitGate.Assert(o, r, s, 0, RefitGate.Gates.Inside | RefitGate.Gates.Regions | RefitGate.Gates.Standoff);
    }

    /// <summary>
    /// What earring dropping must not touch, byte for byte against the model the job saved before it (recorded on the
    /// unchanged code, 2026-10-22, the same twice in one process): men's legs made on TBSE with no earring node of their
    /// own, and a men's top. They also change with the game's data and the mods' versions (TBSE, Bibo+, Twill,
    /// Lumberjack): after a patch or an update, re-record on the commit before this change, never on this one. Re-record
    /// otherwise only for a change meant to alter these refits, and say which.
    /// </summary>
    [LocalDataTheory(LocalData.GameData, CrossSexRefitLocalTests.TwillTbse, CrossSexRefitLocalTests.LumberjackShirt,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.Bibo, LocalData.TbseUvMaps)]
    [InlineData("twill / tbse sfw", CrossSexRefitLocalTests.TwillTbse,
                "55D970FCC3520DFB505C32652B810DDDF38CAA7594522505320FD04917FE2CE4")]
    [InlineData("lumberjack / tbse chest", CrossSexRefitLocalTests.LumberjackShirt,
                "9370663CE7EC6B3CF44F77C4DE2AF2AA52E82B0865A54D7040C014A875EAC921")]
    [Trait("Plan", "AC-earring-bones-5")]
    [Trait("Plan", "EDGE-earring-bones-3")]
    [Trait("Plan", "EDGE-earring-bones-9")]
    public void Outputs_without_earrings_are_byte_identical(string what, string garment, string sha) // [AC-earring-bones-5] [EDGE-earring-bones-3] [EDGE-earring-bones-9]
    {
        bool legs = garment.EndsWith("_dwn.mdl", StringComparison.Ordinal);
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var inputs = Inputs(garment, CrossSexRefitLocalTests.Tbse, legs ? UvBakeInputs.TbseLegs[0].Rel : UvBakeInputs.TbseChest, log);
        if (!legs)
        {
            var onto = BodySizeCatalog.Read(LocalData.Path(CrossSexRefitLocalTests.Bibo));
            inputs = inputs with { TargetPick = CrossSexRefitLocalTests.IsFile(onto, UvBakeInputs.BiboChest) };
        }
        // Before 1382's seam weights, as these were recorded (measured at 1382: 2 and 6,730 vertices on 7 and 931 seams took
        // one weighting, the most any moved 0.015).
        var r = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeamWeights: true), () => CrossSexRefitJob.Run(inputs));
        Assert.Empty(r.Earrings ?? []);
        string got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Assert.Single(r.Sizes).Saved!.Model));
        o.WriteLine($"{what}: {got}");
        Assert.Equal(sha, got);
    }

    /// <summary>Per earring node: how many vertices it weights, how much, where (height, side), the bones beside it,
    /// and what the nearest Bibo+ legs vertex is weighted to.</summary>
    private void Measure(string name, byte[] bytes, SkinnedMesh bibo)
    {
        var skin = ModelSkinReader.Read(bytes, null, null)!;
        var ears = skin.BoneNames.Where(IsEar).Distinct().Order(StringComparer.Ordinal).ToList();
        o.WriteLine($"== {name}: {skin.VertexCount} vertices; earring entries in the bone table: [{string.Join(", ", ears)}]");
        foreach (string ear in ears)
        {
            int n = 0, only = 0;
            double sum = 0, max = 0, wy = 0, wx = 0, neg = 0, pos = 0, minY = double.MaxValue, maxY = double.MinValue;
            var beside = new Dictionary<string, double>(StringComparer.Ordinal);
            var biboOn = new Dictionary<string, double>(StringComparer.Ordinal);
            double farthest = 0;
            for (int v = 0; v < skin.VertexCount; v++)
            {
                float w = 0f;
                for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
                {
                    int at = v * SkinnedMesh.MaxInfluences + k;
                    if (skin.BoneWeights[at] > 0f && skin.BoneNames[skin.BoneIndices[at]] == ear) w += skin.BoneWeights[at];
                }
                if (w <= 0f) continue;
                n++;
                if (w >= 0.999f) only++;
                var p = skin.Positions[v];
                sum += w; max = Math.Max(max, w); wy += w * p.Y; wx += w * p.X;
                if (p.X < 0) neg += w; else pos += w;
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
                {
                    int at = v * SkinnedMesh.MaxInfluences + k;
                    string b = skin.BoneNames[skin.BoneIndices[at]];
                    if (skin.BoneWeights[at] > 0f && !IsEar(b)) beside[b] = beside.GetValueOrDefault(b) + w * skin.BoneWeights[at];
                }
                int near = Nearest(bibo, p, out float d);
                farthest = Math.Max(farthest, d);
                for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
                {
                    int at = near * SkinnedMesh.MaxInfluences + k;
                    if (bibo.BoneWeights[at] > 0f)
                    {
                        string b = bibo.BoneNames[bibo.BoneIndices[at]];
                        biboOn[b] = biboOn.GetValueOrDefault(b) + w * bibo.BoneWeights[at];
                    }
                }
            }
            if (n == 0) { o.WriteLine($"  {ear}: in the table, weights no vertex"); continue; }
            o.WriteLine($"  {ear}: {n} vertices ({only} wholly), weight sum {sum:F1}, max {max:F3}; centroid y {wy / sum:F3} x {wx / sum:F3}; "
                      + $"y {minY:F3}..{maxY:F3}; weight on x<0 {neg / sum:P0}, x>=0 {pos / sum:P0}");
            o.WriteLine($"    beside it: {Shares(beside)}");
            o.WriteLine($"    Bibo+ type A nearest vertex (≤ {farthest * 1000:F0} mm away) weighted: {Shares(biboOn)}");
        }
    }

    private static string Shares(Dictionary<string, double> by)
    {
        double total = by.Values.Sum();
        return string.Join(", ", by.OrderByDescending(p => p.Value).Take(6).Select(p => $"{p.Key} {p.Value / total:P0}"));
    }

    private static int Nearest(SkinnedMesh mesh, Vector3 p, out float distance)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float d = Vector3.DistanceSquared(mesh.Positions[v], p);
            if (d < bestD) { bestD = d; best = v; }
        }
        distance = MathF.Sqrt(bestD);
        return best;
    }
}
