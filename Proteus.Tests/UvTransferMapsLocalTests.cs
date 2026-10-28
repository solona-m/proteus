using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>One bibo↔TBSE bake for every test that reads its report, so the 4096 bake runs once.</summary>
public sealed class TbseBakeFixture
{
    private readonly Lazy<UvTransferBakeJob.Output> output = new(() => UvTransferBakeJob.Run(UvTransferBakeLocalTests.Inputs()));
    internal UvTransferBakeJob.Output Output => output.Value;
}

/// <summary>The committed bibo↔TBSE maps: made from the inputs they say, and as good as the gate demands.</summary>
public class UvTransferMapsLocalTests(TbseBakeFixture bake, ITestOutputHelper output) : IClassFixture<TbseBakeFixture>
{
    private const string Bibo = UvTransferBakeLocalTests.Bibo;
    private const string Tbse = UvTransferBakeLocalTests.Tbse;
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string TbseX = LocalData.Mods + ":The Body SE-X IVCS2 V1_1_0";

    private static string Committed(string name) => Path.Combine(LocalData.Path(LocalData.UvMaps), "uvmaps", name);

    private JsonNode Stats(string map) => bake.Output.Report["stats"]![map]!;

    [LocalDataFact(LocalData.GameData, Bibo, Tbse, LocalData.UvMaps)]
    [Trait("Plan", "AC-tbse-uvmap-1")]
    [Trait("Speed", "Slow")]
    public void Rebake_reproduces_committed_maps() // [AC-tbse-uvmap-1]
    {
        Assert.True(bake.Output.Passed, string.Join("; ", bake.Output.Failures));
        Assert.Equal(File.ReadAllBytes(Committed(UvTransferBakeJob.BiboToTbse)), bake.Output.BiboToTbseBytes);
        Assert.Equal(File.ReadAllBytes(Committed(UvTransferBakeJob.TbseToBibo)), bake.Output.TbseToBiboBytes);
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "AC-tbse-uvmap-1")]
    public void Job_writes_both_maps_and_full_report() // [AC-tbse-uvmap-1]
    {
        var report = bake.Output.Report;
        foreach (var key in new[] { "inputs", "choice", "measure", "stats", "gate", "files", "elapsedMs" })
            Assert.NotNull(report[key]);
        var files = report["files"]!.AsArray();
        Assert.Equal(UvBakeInputs.Sha256(bake.Output.BiboToTbseBytes!), (string)files[0]!["sha256"]!);
        Assert.Equal(UvBakeInputs.Sha256(bake.Output.TbseToBiboBytes!), (string)files[1]!["sha256"]!);
        Assert.Equal(UvBakeInputs.BiboFiles().Count(), report["inputs"]!["bibo"]!.AsArray().Count);
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    public void Committed_maps_cover_named_regions_without_folds() // [AC-tbse-uvmap-2]
    {
        foreach (var map in new[] { UvTransferBakeJob.BiboToTbse, UvTransferBakeJob.TbseToBibo })
            foreach (var region in UvTransferQuality.NamedRegions)
            {
                var r = Stats(map)["regions"]![region]!;
                output.WriteLine($"{map} {region}: coverage {(double)r["coverage"]!:P2} folds {(double)r["folds"]!:P2}");
                Assert.True((double)r["coverage"]! >= UvTransferQuality.Gate.RegionCoverage);
                Assert.True((double)r["folds"]! <= UvTransferQuality.Gate.RegionFolds);
            }
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    [Trait("Speed", "Slow")]
    public void Landmarks_land_on_each_other() // [AC-tbse-uvmap-2]
    {
        foreach (var map in new[] { UvTransferBakeJob.BiboToTbse, UvTransferBakeJob.TbseToBibo })
            foreach (var (name, mm) in Stats(map)["landmarksMm"]!.AsObject())
            {
                Assert.NotNull(mm);
                float limit = UvTransferQuality.Gate.AcceptedLandmarkMm.TryGetValue(name, out var accepted) ? accepted
                            : (name.StartsWith("fingertip") || name.StartsWith("toe") ? UvTransferQuality.Gate.TipLandmark
                                                                                      : UvTransferQuality.Gate.TorsoLandmark) * 1000f;
                Assert.True((double)mm! <= limit, $"{map} {name} off by {mm} mm");
            }
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    public void Global_coverage_and_round_trip_within_limits() // [AC-tbse-uvmap-2]
    {
        foreach (var map in new[] { UvTransferBakeJob.BiboToTbse, UvTransferBakeJob.TbseToBibo })
        {
            var s = Stats(map);
            Assert.True((double)s["destCoverage"]! >= UvTransferQuality.Gate.GlobalCoverage);
            Assert.True((double)s["folds"]! <= UvTransferQuality.Gate.GlobalFolds);
            Assert.True((double)s["roundTripGross"]! <= UvTransferQuality.Gate.RoundTripGross);
            Assert.True((double)s["roundTripModerate"]! <= UvTransferQuality.Gate.RoundTripModerate);
        }
    }

    /// <summary>
    /// Two bodies drawn on one layout — Bibo+ and Rue+ share the bibo sheet, TBSE and TBSE-X the TBSE one — bake to a
    /// map that leaves every texel where it is, near enough: the method finds the same point of a body when there is
    /// one to find.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo, Rue, Tbse, TbseX)]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    [Trait("Speed", "Slow")]
    public void Same_layout_bodies_bake_to_identity() // [AC-tbse-uvmap-2]
    {
        const int size = 2048;   // a 4096 bound halves: p50 ≤ 4 px at 4096 is ≤ 2 px here
        var bibo = UvBakeInputs.Read(LocalData.Path(Bibo), [UvBakeInputs.BiboChest, UvBakeInputs.BiboLegs[0].Rel]);
        var rue = UvBakeInputs.Read(LocalData.Path(Rue), ["files/chest - smallclothes/small/chara/equipment/e0000/model/c0201e0000_top.mdl",
                                                          "files/legs - smallclothes/watermelon crushers - a/chara/equipment/e0000/model/c0201e0000_dwn.mdl"]);
        var tbse = UvBakeInputs.Read(LocalData.Path(Tbse), [UvBakeInputs.TbseChest, UvBakeInputs.TbseLegs[0].Rel]);
        var tbseX = UvBakeInputs.Read(LocalData.Path(TbseX),
            ["p1/torso/m midlander miqo'te au'ra elezen viera/chara/equipment/e0000/model/c0101e0000_top.mdl",
             "p1/legs (smallclothes legs)/sfw/chara/equipment/e0000/model/c0101e0000_dwn.mdl"]);
        BakeBody Body(Dictionary<string, byte[]> files) => new(files.Values.Select(b => UvBakeInputs.Part(b)).OfType<BakeBody.Part>().ToList());

        var misses = new List<string>();
        foreach (var (name, a, b) in new[] { ("Rue+ → Bibo+", Body(rue), Body(bibo)), ("TBSE-X → TBSE", Body(tbseX), Body(tbse)) })
        {
            var raster = UvTransferBake.Rasterize(b, size);
            var map = UvTransferBake.Bake(b, a, size, raster);
            var err = new List<double>();
            var far = new Dictionary<UvBakeBones.Region, int>();
            for (int i = 0; i < map.Valid.Length; i++)
            {
                if (!map.Valid[i]) continue;
                double dx = map.R[i] / 65535.0 * (size - 1) - i % size, dy = map.G[i] / 65535.0 * (size - 1) - i / size;
                double e = Math.Sqrt(dx * dx + dy * dy);
                err.Add(e);
                if (e <= 8) continue;
                int t = raster.Owner[i];
                var r = b.RegionAt(t, raster.BaryU[i], raster.BaryV[i], 1 - raster.BaryU[i] - raster.BaryV[i]);
                far[r] = far.GetValueOrDefault(r) + 1;
            }
            output.WriteLine($"{name}: texels off by more than 8 px, by region: "
                             + string.Join(", ", far.OrderByDescending(f => f.Value).Select(f => $"{f.Key} {f.Value}")));
            err.Sort();
            double p50 = err[err.Count / 2], p95 = err[(int)(err.Count * 0.95)];
            output.WriteLine($"{name}: p50 {p50:F2} px, p95 {p95:F2} px at {size} ({map.Stats.DestCoverage:P2} covered)");
            if (p50 > UvTransferQuality.Gate.IdentityP50 / 2) misses.Add($"{name} p50 {p50:F2} px");
            if (p95 > UvTransferQuality.Gate.IdentityP95 / 2) misses.Add($"{name} p95 {p95:F2} px");
        }
        Assert.True(misses.Count == 0, string.Join("; ", misses));
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse, LocalData.UvMaps)]
    [Trait("Plan", "AC-tbse-uvmap-4")]
    [Trait("Speed", "Slow")]
    public void Sternum_dot_lands_on_male_vanilla_sternum() // [AC-tbse-uvmap-4]
    {
        // A dot at Bibo+'s sternum, carried into men's vanilla space (the right half of TBSE's sheet), lands where
        // TBSE's own sternum is on that half.
        var uv = new UVRemapService(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), LocalData.Path(LocalData.UvMaps));
        var (biboSternum, tbseSternum) = Sternums();
        var conv = uv.UvConverter("bibo", UVRemapService.MaleVanillaSpace, fold: true)!;
        var got = conv(biboSternum.X, biboSternum.Y, 1);
        Assert.NotNull(got);
        float wantU = (tbseSternum.X >= 0.5f ? tbseSternum.X : 1f - tbseSternum.X) * 2f - 1f;
        Assert.InRange(MathF.Abs(got!.Value.U - wantU) * 2048f, 0f, UvTransferQuality.Gate.SternumDotPx);
        Assert.InRange(MathF.Abs(got.Value.V - tbseSternum.Y) * 4096f, 0f, UvTransferQuality.Gate.SternumDotPx);
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse, LocalData.UvMaps)]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    [Trait("Speed", "Slow")]
    public void Sternum_dot_on_bibo_lands_on_tbse_sternum() // [AC-tbse-uvmap-2]
    {
        var uv = new UVRemapService(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), LocalData.Path(LocalData.UvMaps));
        var (biboSternum, tbseSternum) = Sternums();
        var conv = uv.UvConverter("bibo", UVRemapService.TbseSpace)!;
        var got = conv(biboSternum.X, biboSternum.Y, 0);
        Assert.NotNull(got);
        float px = (new System.Numerics.Vector2(got!.Value.U, got.Value.V) - tbseSternum).Length() * 4096f;
        output.WriteLine($"bibo sternum {biboSternum} -> {got.Value}, TBSE sternum {tbseSternum}: {px:F1} px");
        Assert.InRange(px, 0f, UvTransferQuality.Gate.SternumDotPx);
    }

    /// <summary>Each body's sternum landmark, as a uv on its own sheet.</summary>
    private static (System.Numerics.Vector2 Bibo, System.Numerics.Vector2 Tbse) Sternums()
    {
        var inputs = UvTransferBakeLocalTests.Inputs();
        var data = new XivLiveMesh.PbdFile(inputs.Pbd);
        var skel = XivLiveMesh.HavokSkeleton.Read(inputs.Skeleton)!.ParentMap();
        string? Parent(string b) => skel.TryGetValue(b, out var p) ? p : b == "j_kosi" ? null : "j_kosi";
        var bibo = new BakeBody([UvBakeInputs.Part(inputs.Bibo[UvBakeInputs.BiboChest])!]);
        var tbse = new BakeBody([UvBakeInputs.Part(UvBakeInputs.Bend(inputs.Tbse[UvBakeInputs.TbseChest], data, Parent))!]);
        System.Numerics.Vector2 At(BakeBody body)
        {
            var (t, bary) = UvTransferQuality.Landmarks(body)["sternum"];
            return body.At(t, bary.X, bary.Y, bary.Z).Uv;
        }
        return (At(bibo), At(tbse));
    }
}
