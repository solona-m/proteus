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
[Trait("Speed", "Slow")]
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
        var (bibo, tbse) = Chests();
        System.Numerics.Vector2 At(BakeBody body)
        {
            var (t, bary) = UvTransferQuality.Landmarks(body)["sternum"];
            return body.At(t, bary.X, bary.Y, bary.Z).Uv;
        }
        return (At(bibo), At(tbse));
    }

    /// <summary>The two chests the maps were baked from: Bibo+'s, and TBSE's bent to a woman's proportions.</summary>
    private static (BakeBody Bibo, BakeBody Tbse) Chests()
    {
        var inputs = UvTransferBakeLocalTests.Inputs();
        var data = new XivLiveMesh.PbdFile(inputs.Pbd);
        var skel = XivLiveMesh.HavokSkeleton.Read(inputs.Skeleton)!.ParentMap();
        string? Parent(string b) => skel.TryGetValue(b, out var p) ? p : b == "j_kosi" ? null : "j_kosi";
        var bibo = new BakeBody([UvBakeInputs.Part(inputs.Bibo[UvBakeInputs.BiboChest])!]);
        var tbse = new BakeBody([UvBakeInputs.Part(UvBakeInputs.Bend(inputs.Tbse[UvBakeInputs.TbseChest], data, Parent))!]);
        return (bibo, tbse);
    }

    /// <summary>Each chest's nipples (left, then right), found the way the bake finds them.</summary>
    private static (UvBakeNipples.Nipple L, UvBakeNipples.Nipple R) NipplesOf(BakeBody body)
    {
        var (found, failure) = UvBakeNipples.Find(body);
        Assert.True(found != null, failure);
        return found!.Value;
    }

    /// <summary>
    /// A 6 px dot painted at <paramref name="uv"/> on a 4096² sheet in layout <paramref name="src"/>, carried into
    /// <paramref name="dst"/> the way the compositor carries art (<see cref="UVRemapService.ConvertSheet"/>), and found
    /// there: the alpha-weighted centroid, as a uv of the destination sheet. Null when none of it arrives.
    /// </summary>
    private static System.Numerics.Vector2? OverlayDot(UVRemapService uv, System.Numerics.Vector2 at, string src, string dst)
    {
        const int S = 4096, Radius = 6;
        var rgba = new byte[S * S * 4];
        int cx = (int)MathF.Round(at.X * (S - 1)), cy = (int)MathF.Round(at.Y * (S - 1));
        for (int y = cy - Radius; y <= cy + Radius; y++)
            for (int x = cx - Radius; x <= cx + Radius; x++)
            {
                if (x < 0 || y < 0 || x >= S || y >= S || (x - cx) * (x - cx) + (y - cy) * (y - cy) > Radius * Radius) continue;
                int i = (y * S + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = rgba[i + 3] = 255;
            }
        var sheet = uv.ConvertSheet(rgba, S, S, src, dst);
        Assert.NotNull(sheet);
        var (img, w, h) = sheet!.Value;
        double sx = 0, sy = 0, sa = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int a = img[(y * w + x) * 4 + 3];
                if (a == 0) continue;
                sx += x * a; sy += y * a; sa += a;
            }
        return sa == 0 ? null : new System.Numerics.Vector2((float)(sx / sa / (w - 1)), (float)(sy / sa / (h - 1)));
    }

    /// <summary>The point of <paramref name="body"/>'s surface its sheet draws at <paramref name="uv"/>, or null off its skin.</summary>
    private static System.Numerics.Vector3? PointAt(BakeBody body, UvTransferBake.Raster raster, System.Numerics.Vector2 uv)
    {
        int x = (int)MathF.Round(Math.Clamp(uv.X, 0, 1) * (raster.Size - 1));
        int y = (int)MathF.Round(Math.Clamp(uv.Y, 0, 1) * (raster.Size - 1));
        for (int r = 0; r <= 4; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= raster.Size || yy >= raster.Size) continue;
                    int i = yy * raster.Size + xx, t = raster.Owner[i];
                    if (t < 0) continue;
                    return body.At(t, raster.BaryU[i], raster.BaryV[i], 1 - raster.BaryU[i] - raster.BaryV[i]).Pos;
                }
        return null;
    }

    private UVRemapService Maps() => new(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), LocalData.Path(LocalData.UvMaps));

    /// <summary>
    /// Where a dot on one chest's nipples lands on the other's, per side: how far from that chest's own nipple on its
    /// sheet (px at 4096) and how far above it on the body (mm; negative is below). <paramref name="landedUv"/> gives
    /// the landing uv on <paramref name="to"/>'s sheet for a uv on <paramref name="from"/>'s.
    /// </summary>
    private List<(string Side, float Px, float DyMm)> NippleLandings(BakeBody from, BakeBody to,
                                                                    Func<System.Numerics.Vector2, int, System.Numerics.Vector2?> landedUv)
    {
        var mine = NipplesOf(from);
        var theirs = NipplesOf(to);
        var raster = UvTransferBake.Rasterize(to, 4096);
        var result = new List<(string, float, float)>();
        foreach (var (side, a, b, k) in new[] { ("L", mine.L, theirs.L, 0), ("R", mine.R, theirs.R, 1) })
        {
            var got = landedUv(a.Uv, k);
            Assert.True(got != null, $"nipple {side}: nothing arrived");
            float px = (got!.Value - b.Uv).Length() * 4096f;
            float dy = PointAt(to, raster, got.Value) is { } p ? (p.Y - b.Pos.Y) * 1000f : float.NaN;
            output.WriteLine($"nipple {side}: {a.Uv} -> {got.Value}, own nipple {b.Uv} ({b.Pos}): {px:F1} px, {dy:+0.0;-0.0} mm up");
            result.Add((side, px, dy));
        }
        return result;
    }

    /// <summary>
    /// Overlay map <c>bibo_to_tbse</c>: a dot on Bibo+'s nipple, in a woman's overlay carried onto a man in TBSE's layout,
    /// lands on TBSE's nipple.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse, LocalData.UvMaps)]
    [Trait("Plan", "AC-uvmap-nipples-1")]
    public void OverlayMap_nipple_dot_lands_on_tbse_nipple() // [AC-uvmap-nipples-1]
    {
        var uv = Maps();
        var (bibo, tbse) = Chests();
        foreach (var (side, px, _) in NippleLandings(bibo, tbse, (at, _) => OverlayDot(uv, at, "bibo", UVRemapService.TbseSpace)))
            Assert.True(px <= UvTransferQuality.Gate.NippleDotPx, $"nipple {side} {px:F1} px");
    }

    /// <summary>Both chests have an under-bust crease below each nipple, mirrored; printed with the old under-bust point.</summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "EDGE-uvmap-nipples-35")]
    public void Creases_found_on_bibo_and_bent_tbse() // [EDGE-uvmap-nipples-35] [EDGE-uvmap-nipples-38]
    {
        var (bibo, tbse) = Chests();
        foreach (var (name, body) in new[] { ("Bibo+", bibo), ("TBSE", tbse) })
        {
            var (l, r) = NipplesOf(body);
            var (cl, fl) = UvBakeNipples.Crease(body, l);
            var (cr, fr) = UvBakeNipples.Crease(body, r);
            Assert.True(cl != null, $"{name} L: {fl}");
            Assert.True(cr != null, $"{name} R: {fr}");
            var old = UvTransferQuality.Landmarks(body)["underBustL"];
            output.WriteLine($"{name}: nipple y {l.Pos.Y:F4}; crease L {cl!.Value.Pos} (turn {cl.Value.SlopeChange:F2}), "
                             + $"R {cr!.Value.Pos} (turn {cr.Value.SlopeChange:F2}); old under-bust {body.At(old.Tri, old.Bary.X, old.Bary.Y, old.Bary.Z).Pos}");
            var mirror = new System.Numerics.Vector3(-cr.Value.Pos.X, cr.Value.Pos.Y, cr.Value.Pos.Z);
            Assert.InRange(System.Numerics.Vector3.Distance(cl.Value.Pos, mirror), 0f, UvBakeNipples.Symmetry);
        }
    }

    /// <summary>
    /// Each map's chest field, built on the real chests: it lands nipples and creases where they must go, and passes
    /// every construction check — no fold, pins held, nothing off the torso moved, no overshoot.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "AC-uvmap-nipples-4")]
    public void Chest_fields_pass_construction_checks() // [AC-uvmap-nipples-4] [EDGE-uvmap-nipples-3] [EDGE-uvmap-nipples-4] [EDGE-uvmap-nipples-39]
    {
        var (bibo, tbse) = Chests();
        var misses = new List<string>();
        foreach (var (map, dest, src) in new[] { (UvTransferBakeJob.BiboToTbse, tbse, bibo), (UvTransferBakeJob.TbseToBibo, bibo, tbse) })
        {
            var (field, failure) = ChestField.Build(dest, src, new TriangleBvh(src));
            Assert.True(field != null, $"{map}: {failure}");
            foreach (var s in field!.Sites)
                output.WriteLine($"{map} {s.Name}{(s.Pin ? " (pin)" : "")} at {s.Centre}: {s.Offset * 1000f} mm");
            var c = field.Measure(dest);
            output.WriteLine($"{map}: yLow {field.YLow:F4} yClav {field.YClav:F4} cond {field.Condition:E2}{(field.Ridged ? " ridged" : "")} "
                             + $"iterations {field.Iterations} residual {string.Join(", ", field.ResidualMm.Select(r => $"{r.Key} {r.Value:F2}"))}");
            output.WriteLine($"{map}: min J {c.MinJacobian:F3} ({c.FoldedTriangles} under), pin {c.MaxPinMm:F3} mm, off-torso {c.MaxOffTorsoMm:F2} mm, "
                             + $"max {c.MaxMm:F1} mm, reverse {c.MaxReverseMm:F2} mm at {c.ReverseAt}, still {c.MaxStillMm:F2} mm");
            misses.AddRange(field.Check(c).Select(m => $"{map}: {m}"));
        }
        Assert.True(misses.Count == 0, string.Join("; ", misses));
    }

    private const string BiboSkin = Bibo + "/Midlander, Elezen, Miqo - SKIN DETAIL/Detailed/chara/bibo_mid_base.tex";
    private const string TbseSkin = Tbse + "/p1/step 2- midlander, elezen, miqo'te/type 0/chara/human/c0101/obj/body/b0001/texture/c0101b0001_b_d.tex";

    /// <summary>
    /// The nipple the finder takes from each mesh is where that body's own skin texture paints its areola: within
    /// 8 texture px (16 at 4096) of the centroid of the areola's dark pixels, each side in its own half of the sheet.
    /// Both diffuses are 2048² and draw the whole sheet (TBSE's <c>_b_d</c> is the doubled one), so a uv is uv × 2048.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse, BiboSkin, TbseSkin)]
    [Trait("Plan", "EDGE-uvmap-nipples-23")]
    public void Nipple_uv_matches_areola_in_skin_diffuse() // [EDGE-uvmap-nipples-23]
    {
        var loader = new TextureLoader(Substitute.For<Dalamud.Plugin.Services.IDataManager>(), Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
        var (bibo, tbse) = Chests();
        foreach (var (name, body, skin) in new[] { ("Bibo+", bibo, BiboSkin), ("TBSE", tbse, TbseSkin) })
        {
            var tex = loader.LoadTexAsRgba(LocalData.Path(skin));
            Assert.NotNull(tex);
            var (rgba, w, h) = tex!.Value;
            Assert.Equal(2048, w);
            Assert.Equal(2048, h);
            var (l, r) = NipplesOf(body);
            Assert.True(l.Uv.X >= 0.5f && r.Uv.X < 0.5f, $"{name}: the left nipple belongs on the right half");
            foreach (var (side, n) in new[] { ("L", l), ("R", r) })
            {
                const int Window = 40;   // about 2 cm each way
                int cx = (int)MathF.Round(n.Uv.X * w), cy = (int)MathF.Round(n.Uv.Y * h);
                var lum = new List<(int X, int Y, float L)>();
                for (int y = cy - Window; y <= cy + Window; y++)
                    for (int x = cx - Window; x <= cx + Window; x++)
                    {
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        int i = (y * w + x) * 4;
                        lum.Add((x, y, 0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2]));
                    }
                var sorted = lum.Select(p => p.L).OrderBy(v => v).ToList();
                float median = sorted[sorted.Count / 2], dark = sorted[0] + (median - sorted[0]) * 0.5f;
                double sx = 0, sy = 0, sn = 0;
                foreach (var (x, y, lv) in lum)
                    if (lv < dark) { sx += x; sy += y; sn++; }
                Assert.True(sn > 0, $"{name} {side}: no areola");
                float off = MathF.Sqrt(MathF.Pow((float)(sx / sn) - n.Uv.X * w, 2) + MathF.Pow((float)(sy / sn) - n.Uv.Y * h, 2));
                output.WriteLine($"{name} {side}: nipple at ({n.Uv.X * w:F1}, {n.Uv.Y * h:F1}), areola at ({sx / sn:F1}, {sy / sn:F1}) "
                                 + $"of {sn} px: {off:F1} texture px");
                Assert.InRange(off, 0f, 8f);
            }
        }
    }

    /// <summary>
    /// Overlay map <c>bibo_to_tbse</c>, the chest from collarbone to under-bust at the nipple's x, and the sternum: where
    /// a dot on Bibo+ lands on TBSE, against TBSE's own point there (mm up; negative is below). The nipple row compares
    /// with TBSE's nipple; the others with TBSE's point at the same height measured between pelvis and neck.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse, LocalData.UvMaps)]
    [Trait("Plan", "AC-uvmap-nipples-1")]
    public void OverlayMap_chest_profile_bibo_to_tbse() // [AC-uvmap-nipples-1] [EDGE-uvmap-nipples-24]
    {
        var uv = Maps();
        var (bibo, tbse) = Chests();
        var rows = ChestProfile(bibo, tbse, at => OverlayDot(uv, at, "bibo", UVRemapService.TbseSpace));
        foreach (var (row, dy) in rows)
            if (row.StartsWith("nipple")) Assert.True(MathF.Abs(dy) <= UvTransferQuality.Gate.NippleDotPx / 4.2f, $"{row} {dy:F1} mm");
    }

    /// <summary>
    /// The chest profile of one map: per row, a point of <paramref name="from"/> carried by <paramref name="landedUv"/>
    /// onto <paramref name="to"/>'s sheet and found on its surface, less <paramref name="to"/>'s own point for that row.
    /// </summary>
    private List<(string Row, float DyMm)> ChestProfile(BakeBody from, BakeBody to, Func<System.Numerics.Vector2, System.Numerics.Vector2?> landedUv)
    {
        var raster = UvTransferBake.Rasterize(to, 4096);
        var fa = UvTransferQuality.Frame.Of(from);
        var ta = UvTransferQuality.Frame.Of(to);
        var na = NipplesOf(from);
        var nb = NipplesOf(to);
        var rows = new List<(string, float)>();
        void Row(string name, (System.Numerics.Vector3 Pos, System.Numerics.Vector2 Uv)? a, System.Numerics.Vector3? want)
        {
            if (a == null || want == null) { output.WriteLine($"{name}: no point"); return; }
            var got = landedUv(a.Value.Uv);
            var p = got == null ? null : PointAt(to, raster, got.Value);
            if (p == null) { output.WriteLine($"{name}: nothing landed"); return; }
            float dy = (p.Value.Y - want.Value.Y) * 1000f;
            output.WriteLine($"{name}: from {a.Value.Pos} lands at {p.Value}, want {want.Value}: {dy:+0.0;-0.0} mm up");
            rows.Add((name, dy));
        }
        float Height(UvTransferQuality.Frame f, float y) => (y - f.PelvisY) / f.Span;
        float Y(UvTransferQuality.Frame f, float h) => f.PelvisY + h * f.Span;
        foreach (var (side, sx, a, b) in new[] { ("L", 1f, na.L, nb.L), ("R", -1f, na.R, nb.R) })
        {
            Row("nipple" + side, (a.Pos, a.Uv), b.Pos);
            foreach (var (row, bone) in new[] { ("clavicle", "j_sako_" + side.ToLowerInvariant()), ("underBust", "j_sebo_b") })
            {
                if (BoneY(from, bone) is not { } y) { output.WriteLine($"{row}{side}: no {bone}"); continue; }
                var src = FrontPoint(from, a.Pos.X, y);
                var dst = FrontPoint(to, b.Pos.X, Y(ta, Height(fa, y)));
                Row(row + side, src, dst?.Pos);
            }
        }
        if (BoneY(from, "j_sebo_c") is { } sy)
            Row("sternum", FrontPoint(from, 0f, sy), FrontPoint(to, 0f, Y(ta, Height(fa, sy)))?.Pos);
        return rows;
    }

    /// <summary>The height of the skin <paramref name="bone"/> carries most of, or null.</summary>
    private static float? BoneY(BakeBody body, string bone)
    {
        float sum = 0;
        int n = 0;
        for (int v = 0; v < body.P.Length; v++)
        {
            string? top = null;
            float best = 0f;
            foreach (var (b, w) in body.Weights[v]) if (w > best) { best = w; top = b; }
            if (top == bone) { sum += body.P[v].Y; n++; }
        }
        return n == 0 ? null : sum / n;
    }

    /// <summary>The front of <paramref name="body"/> at (<paramref name="x"/>, <paramref name="y"/>): the foremost vertex within 1 cm.</summary>
    private static (System.Numerics.Vector3 Pos, System.Numerics.Vector2 Uv)? FrontPoint(BakeBody body, float x, float y)
    {
        (System.Numerics.Vector3, System.Numerics.Vector2)? best = null;
        float bestZ = float.MinValue;
        for (int t = 0; t < body.TriangleCount; t++)
            for (int k = 0; k < 3; k++)
            {
                var p = body.P[body.Tri[t * 3 + k]];
                if (MathF.Abs(p.X - x) > 0.01f || MathF.Abs(p.Y - y) > 0.01f || p.Z <= bestZ) continue;
                bestZ = p.Z;
                best = (p, body.Uv[t * 3 + k]);
            }
        return best;
    }

    /// <summary>Art map back, <c>tbse_to_bibo</c>: a dot on TBSE's nipple carried to bibo lands on Bibo+'s nipple.</summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse, LocalData.UvMaps)]
    [Trait("Plan", "AC-uvmap-nipples-3")]
    public void ArtBackMap_nipple_dot_on_tbse_lands_on_bibo_nipple() // [AC-uvmap-nipples-3]
    {
        var uv = Maps();
        var (bibo, tbse) = Chests();
        foreach (var (side, px, _) in NippleLandings(tbse, bibo, (at, _) => OverlayDot(uv, at, UVRemapService.TbseSpace, "bibo")))
            Assert.True(px <= UvTransferQuality.Gate.NippleDotPx, $"nipple {side} {px:F1} px");
    }

    /// <summary>
    /// Refit map <c>tbse_to_bibo</c> (read by <see cref="UVRemapService.UvConverter"/> bibo→tbse, the body match of a
    /// refit across the sexes): Bibo+'s nipple is matched to TBSE's.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse, LocalData.UvMaps)]
    [Trait("Plan", "AC-uvmap-nipples-9")]
    public void RefitMap_nipple_point_lands_on_tbse_nipple() // [AC-uvmap-nipples-9]
    {
        var conv = Maps().UvConverter("bibo", UVRemapService.TbseSpace)!;
        var (bibo, tbse) = Chests();
        foreach (var (side, px, _) in NippleLandings(bibo, tbse,
                     (at, k) => conv(at.X, at.Y, k) is { } c ? new System.Numerics.Vector2(c.U, c.V) : null))
            Assert.True(px <= UvTransferQuality.Gate.NippleDotPx, $"nipple {side} {px:F1} px");
    }
}
