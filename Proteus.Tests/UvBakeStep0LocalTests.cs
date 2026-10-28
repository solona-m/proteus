using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Step 0 of the bibo↔TBSE bake: the facts about the real Bibo+ and TBSE meshes the bake rests on, measured before the
/// maps are trusted. Each prints what it measured.
/// </summary>
public class UvBakeStep0LocalTests(ITestOutputHelper output)
{
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Tbse = LocalData.Mods + ":The Body SE";

    private static (PbdFile Pbd, Func<string, string?> ParentOf, HashSet<string> Bones) Game()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var pbd = new PbdFile(data.GetFile(OtherRaceWearer.PbdPath)!.Data);
        var skel = HavokSkeleton.Read(data.GetFile(OtherRaceWearer.BaseSkeletonPath(101))!.Data)!;
        var parents = skel.ParentMap();
        return (pbd, b => parents.GetValueOrDefault(b), skel.Names.ToHashSet(StringComparer.Ordinal));
    }

    private static Dictionary<string, byte[]> BiboFiles() => UvBakeInputs.Read(LocalData.Path(Bibo), UvBakeInputs.BiboFiles());
    private static Dictionary<string, byte[]> TbseFiles() => UvBakeInputs.Read(LocalData.Path(Tbse), UvBakeInputs.TbseFiles());

    /// <summary>Of the triangles off the midline, how many sit on the sheet half of their own side (+X → right half).</summary>
    private static (double PlusRight, double PlusLeft, int Counted) SideHalf(BakeBody body)
    {
        int right = 0, left = 0, counted = 0;
        for (int t = 0; t < body.TriangleCount; t++)
        {
            var c = (body.P[body.Tri[t * 3]] + body.P[body.Tri[t * 3 + 1]] + body.P[body.Tri[t * 3 + 2]]) / 3f;
            if (MathF.Abs(c.X) < 0.01f) continue;
            float u = (body.Uv[t * 3].X + body.Uv[t * 3 + 1].X + body.Uv[t * 3 + 2].X) / 3f;
            counted++;
            bool plus = c.X > 0, rightHalf = u >= 0.5f;
            if (plus == rightHalf) right++; else left++;
        }
        return ((double)right / counted, (double)left / counted, counted);
    }

    private static int Covered(BakeBody body, int size)
        => UvTransferBake.Rasterize(body, size).Owner.Count(o => o >= 0);

    /// <summary>Both nude bodies put each side on its own half of the sheet (+X on the right), which the bake assumes.</summary>
    [LocalDataFact(Bibo, Tbse)]
    [Trait("Plan", "EDGE-tbse-uvmap-27")]
    public void Each_side_on_its_own_half() // [EDGE-tbse-uvmap-27]
    {
        var bibo = BiboFiles();
        var tbse = TbseFiles();
        foreach (var bytes in new[] { bibo[UvBakeInputs.BiboChest], bibo[UvBakeInputs.BiboLegs[0].Rel], bibo[UvBakeInputs.BiboHands],
                                      bibo[UvBakeInputs.BiboFeet], tbse[UvBakeInputs.TbseChest], tbse[UvBakeInputs.TbseLegs[0].Rel],
                                      tbse[UvBakeInputs.TbseHands], tbse[UvBakeInputs.TbseFeet] })
            Assert.True(SideHalf(new BakeBody([UvBakeInputs.Part(bytes)!])).PlusRight >= 0.99);
    }

    [LocalDataFact(Tbse)]
    [Trait("Plan", "EDGE-tbse-uvmap-21")]
    public void Tbse_c0301_chest_uv_matches_c0101() // [EDGE-tbse-uvmap-21]
    {
        var tbse = TbseFiles();
        double iou = UvTransferBakeJob.Iou(new BakeBody([UvBakeInputs.Part(tbse[UvBakeInputs.TbseHighlanderChest])!]),
                                           new BakeBody([UvBakeInputs.Part(tbse[UvBakeInputs.TbseChest])!]), 1024);
        output.WriteLine($"c0301 vs c0101 chest uv IoU {iou:F4}");
        Assert.True(iou >= 0.99, $"IoU {iou:F4}: c0301 would need leaving out of the men's layout group");
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "EDGE-tbse-uvmap-22")]
    [Trait("Speed", "Slow")]
    public void Bone_report_has_no_unresolved_vertices() // [EDGE-tbse-uvmap-22]
    {
        var (_, _, bones) = Game();
        foreach (var bytes in BiboFiles().Values.Concat(TbseFiles().Values))
            if (UvBakeInputs.Part(bytes) is { } part)
                Assert.DoesNotContain(UvBakeBones.Report(part.Weights, bones.Contains),
                                      r => r.How == UvBakeBones.Resolution.Absent && r.Vertices > 0);
    }

    [LocalDataFact(Tbse)]
    [Trait("Plan", "EDGE-tbse-uvmap-24")]
    public void Genital_overlap_reported_per_option_pair() // [EDGE-tbse-uvmap-24]
    {
        var tbse = TbseFiles();
        var legs = UvBakeInputs.TbseLegs.Select((l, i) => UvBakeInputs.Part(tbse[l.Rel], i)!).ToList();
        var raster = UvTransferBake.Rasterize(new BakeBody(legs), 1024);
        foreach (var ((a, b), n) in raster.OverlapByOption)
            output.WriteLine($"{UvBakeInputs.TbseLegs[a].Name} | {UvBakeInputs.TbseLegs[b].Name}: {n} texels drawn at two places");
        // The options model the crotch differently over shared texels: the conflicts exist, and every one is attributed.
        Assert.NotEmpty(raster.OverlapByOption);
        Assert.Equal(raster.Overlap, raster.OverlapByOption.Values.Sum());
    }

    [LocalDataFact(LocalData.TbseUvMaps, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-tbse-uvmap-29")]
    [Trait("Speed", "Slow")]
    public void Compressed_load_within_2x_raw_v1() // [EDGE-tbse-uvmap-29]
    {
        string dir = System.IO.Path.Combine(LocalData.Path(LocalData.TbseUvMaps), "uvmaps");
        UvTransferBaselineLocalTests.ReadMap(System.IO.Path.Combine(dir, "bibo_to_tbse_transfer.tif"));   // warm up
        UvTransferBaselineLocalTests.ReadMap(System.IO.Path.Combine(dir, "bibo_to_gen3_transfer.tif"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UvTransferBaselineLocalTests.ReadMap(System.IO.Path.Combine(dir, "bibo_to_tbse_transfer.tif"));
        long compressed = sw.ElapsedMilliseconds;
        sw.Restart();
        UvTransferBaselineLocalTests.ReadMap(System.IO.Path.Combine(dir, "bibo_to_gen3_transfer.tif"));
        long raw = sw.ElapsedMilliseconds;
        output.WriteLine($"compressed {compressed} ms, raw v1 {raw} ms");
        // 3×, not the plan's 2×: accepted by the user (2026-10-20) at 2.5× measured — a half-second load once a session,
        // only on a man who needs the map, for a download a tenth the size.
        Assert.True(compressed <= 3 * Math.Max(raw, 1), $"compressed {compressed} ms > 3 × raw {raw} ms");
    }

    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    [Trait("Speed", "Slow")]
    public void Measure_bodies() // [AC-tbse-uvmap-2]
    {
        var (pbd, parentOf, bones) = Game();
        var bibo = BiboFiles();
        var tbse = TbseFiles();

        foreach (var (name, files) in new[] { ("Bibo+", bibo), ("TBSE", tbse) })
            foreach (var (rel, bytes) in files)
            {
                var part = UvBakeInputs.Part(bytes);
                if (part == null) { output.WriteLine($"{name} {rel}: NO SKIN"); continue; }
                var body = new BakeBody([part]);
                var lo = body.P.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var hi = body.P.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                var uvLo = body.Uv.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
                var uvHi = body.Uv.Aggregate(new Vector2(float.MinValue), Vector2.Max);
                var (pr, pl, n) = SideHalf(body);
                output.WriteLine($"{name} {rel}: {body.P.Length} v, {body.TriangleCount} t, pos {lo}..{hi}, uv {uvLo}..{uvHi}, "
                                 + $"+X→right {pr:P1} +X→left {pl:P1} of {n}, rim {body.Rim.SegmentCount}, cover@1024 {Covered(body, 1024)}");
                foreach (var g in body.Rim.Segments.GroupBy(s => (MathF.Round((s.A.Y + s.B.Y) * 50f) / 100f,
                                                                  MathF.Sign((s.A.X + s.B.X) * 0.5f))))
                    output.WriteLine($"    rim y≈{g.Key.Item1:F2} side {g.Key.Item2}: {g.Count()} edges, "
                                     + $"x {g.Min(s => s.A.X):F3}..{g.Max(s => s.A.X):F3} z {g.Min(s => s.A.Z):F3}..{g.Max(s => s.A.Z):F3}");
                foreach (var (bone, count, how) in UvBakeBones.Report(part.Weights, bones.Contains))
                    if (how != UvBakeBones.Resolution.Own)
                        output.WriteLine($"    bone {bone}: {count} v, {how}");
            }
    }
}
