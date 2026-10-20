using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The transfer-map bake on synthetic bodies: flat sheets of skin whose answers can be worked out by hand.</summary>
public class UvTransferBakeTests
{
    /// <summary>
    /// A flat n×n grid of skin facing +Z at depth <paramref name="z"/>, 0.2 m across, its uv the grid position
    /// (offset by <paramref name="uvShift"/>), every vertex weighted to the pelvis.
    /// </summary>
    internal static BakeBody.Part Sheet(int n = 8, float z = 0f, float scale = 0.2f, float uvShiftX = 0f, float uvShiftY = 0f,
                                        int option = 0, Func<float, float, float>? bulge = null, float uvScale = 1f, float uvX0 = 0f)
    {
        var pos = new List<float>();
        var uv = new List<float>();
        var nrm = new List<float>();
        var w = new List<(string, float)[]>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float u = (float)i / n, v = (float)j / n;
                pos.AddRange([u * scale - scale / 2, v * scale, z + (bulge?.Invoke(u, v) ?? 0f)]);
                uv.AddRange([uvX0 + u * uvScale + uvShiftX, v * uvScale + uvShiftY]);
                nrm.AddRange([0f, 0f, 1f]);
                w.Add([("j_kosi", 1f)]);
            }
        var tri = new List<int>();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                tri.AddRange([a, b, c, b, d, c]);
            }
        return new BakeBody.Part(pos.ToArray(), uv.ToArray(), tri.ToArray(), w.ToArray(), nrm.ToArray(), option);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-1")]
    public void Bake_is_deterministic() // [AC-tbse-uvmap-1]
    {
        var dest = new BakeBody([Sheet(12, bulge: (u, v) => 0.01f * MathF.Sin(u * 6))]);
        var src = new BakeBody([Sheet(9, z: 0.005f, scale: 0.21f)]);
        var a = UvTransferBake.Bake(dest, src, 128);
        var b = UvTransferBake.Bake(dest, src, 128);
        Assert.Equal(UvTransferMapFile.Encode(a.R, a.G, a.Valid, 128), UvTransferMapFile.Encode(b.R, b.G, b.Valid, 128));
        Assert.True(a.Stats.ValidPixels > 0);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    public void Same_body_bakes_to_identity() // [AC-tbse-uvmap-2]
    {
        var body = new BakeBody([Sheet(10)]);
        var map = UvTransferBake.Bake(body, body, 64);
        for (int i = 0; i < map.Valid.Length; i++)
        {
            if (!map.Valid[i]) continue;
            Assert.InRange(map.R[i] / 65535f * 63 - i % 64, -0.75f, 0.75f);
            Assert.InRange(map.G[i] / 65535f * 63 - i / 64, -0.75f, 0.75f);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-23")]
    public void Off_tile_uvs_wrap_per_triangle() // [EDGE-tbse-uvmap-23]
    {
        var src = new BakeBody([Sheet(6, z: 0.002f)]);
        // Sixteenths, so shifting a tile down and back is exact in floating point.
        var inTile = UvTransferBake.Bake(new BakeBody([Sheet(8, uvScale: 0.5f, uvX0: 0.25f)]), src, 64);
        var below = UvTransferBake.Bake(new BakeBody([Sheet(8, uvScale: 0.5f, uvX0: 0.25f, uvShiftY: -1f)]), src, 64);
        Assert.Equal(inTile.Valid, below.Valid);
        Assert.Equal(inTile.R, below.R);
        Assert.Equal(inTile.G, below.G);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-24")]
    public void Overlaps_first_in_default_order_wins_and_counted() // [EDGE-tbse-uvmap-24]
    {
        // Two options drawing the same texels, the second 3 cm in front: the first owns them, and each is counted.
        var dest = new BakeBody([Sheet(4, option: 0), Sheet(4, z: 0.03f, option: 1)]);
        var raster = UvTransferBake.Rasterize(dest, 32);
        int firstOptionTriangles = dest.TriOption.Count(o => o == 0);
        Assert.All(raster.Owner.Where(o => o >= 0), o => Assert.True(o < firstOptionTriangles));
        Assert.True(raster.Overlap > 0);
        Assert.Equal(raster.Overlap, raster.OverlapByOption[(0, 1)]);

        // The same texels at the same place are no conflict.
        Assert.Equal(0, UvTransferBake.Rasterize(new BakeBody([Sheet(4, option: 0), Sheet(4, option: 1)]), 32).Overlap);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-25")]
    public void Unreachable_texels_invalid_and_fallbacks_counted() // [EDGE-tbse-uvmap-25]
    {
        var dest = new BakeBody([Sheet(6)]);
        var far = UvTransferBake.Bake(dest, new BakeBody([Sheet(6, z: UvTransferBake.Reach * 2)]), 32);
        Assert.Equal(0, far.Stats.ValidPixels);
        Assert.Equal(far.Stats.OwnedPixels, far.Stats.Unreached);

        // A source smaller than the destination: its edge texels miss by ray and are taken by the closest point.
        var small = UvTransferBake.Bake(dest, new BakeBody([Sheet(6, scale: 0.15f, z: 0.001f)]), 32);
        Assert.True(small.Stats.ClosestFallbacks > 0);
        Assert.Equal(small.Stats.OwnedPixels, small.Stats.ValidPixels);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-26")]
    public void Landings_at_the_source_rim_are_kept() // [EDGE-tbse-uvmap-26]
    {
        // The source ends where the destination does: its rim is the body's end, and texels landing on it are kept.
        var map = UvTransferBake.Bake(new BakeBody([Sheet(6)]), new BakeBody([Sheet(6, z: 0.001f)]), 32);
        Assert.Equal(map.Stats.OwnedPixels, map.Stats.ValidPixels);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-27")]
    public void Uncovered_half_is_mirror_filled_and_reported() // [EDGE-tbse-uvmap-27]
    {
        const int size = 16;
        var r = new ushort[size * size];
        var g = new ushort[size * size];
        var ok = new bool[size * size];
        for (int y = 0; y < size; y++)
            for (int x = size / 2; x < size; x++)
            {
                int i = y * size + x;
                r[i] = 40000; g[i] = 1234; ok[i] = true;
            }
        var filled = UvTransferBakeJob.MirrorFill(new UvTransferBake.Result(r, g, ok, size, new UvTransferBake.Stats()));
        Assert.Equal("left", filled.Stats.MirrorFilled);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size / 2; x++)
            {
                int i = y * size + x;
                Assert.True(filled.Valid[i]);
                Assert.Equal(65535 - 40000, filled.R[i]);
                Assert.Equal(1234, filled.G[i]);
            }

        // Both halves reached: left alone.
        var both = new UvTransferBake.Result(r, g, Enumerable.Repeat(true, size * size).ToArray(), size, new UvTransferBake.Stats());
        Assert.Same(both, UvTransferBakeJob.MirrorFill(both));
        Assert.Null(both.Stats.MirrorFilled);
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-28")]
    public void Failure_leaves_no_output() // [EDGE-tbse-uvmap-28]
    {
        var inputs = new UvTransferBakeJob.Inputs(new Dictionary<string, byte[]>(), new Dictionary<string, byte[]>(), [0, 0, 0, 0], []);
        var result = UvTransferBakeJob.Run(inputs, 64);
        Assert.False(result.Passed);
        Assert.Null(result.BiboToTbseBytes);
        Assert.Null(result.TbseToBiboBytes);
        Assert.NotNull(result.Report["error"]);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    public void Gate_names_every_miss() // [AC-tbse-uvmap-2]
    {
        var f = new UvTransferQuality.Figures { DestCoverage = 0.5, FoldShare = 0.5, RoundTripGross = 0.5, RoundTripModerate = 0.5 };
        f.Regions["sternum"] = new UvTransferQuality.RegionFigures { Owned = 10, Valid = 1, Measured = 10, Folds = 9 };
        f.LandmarksMm["sternum"] = 99f;
        f.LandmarksMm["underBustL"] = 31f;   // accepted up to 32 mm
        var miss = UvTransferQuality.Check("m", f);
        Assert.Contains(miss, m => m.Contains("coverage 50"));
        Assert.Contains(miss, m => m.Contains("folds 50"));
        Assert.Contains(miss, m => m.Contains("round-trip gross"));
        Assert.Contains(miss, m => m.Contains("sternum coverage"));
        Assert.Contains(miss, m => m.Contains("landmark sternum"));
        Assert.DoesNotContain(miss, m => m.Contains("underBustL"));
    }
}

/// <summary>Body regions and the body mods' own bones.</summary>
public class UvBakeBonesTests
{
    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-22")]
    public void Overrides_apply_before_pbd_lookup() // [EDGE-tbse-uvmap-22]
    {
        Assert.Equal("j_kosi", UvBakeBones.Alias("n_ear_b_l"));
        Assert.Equal("j_kosi", UvBakeBones.Alias("n_root"));
        Assert.Equal("j_kosi", UvBakeBones.Alias("heels_offset_n_a_ac"));
        Assert.Equal("j_asi_a_l", UvBakeBones.Alias("iv_daitai_phys_l"));
        Assert.Equal("j_kosi", UvBakeBones.Alias("iv_kintama_phys_l"));
        Assert.Equal("j_mune_l", UvBakeBones.Alias("j_mune_l"));
        Assert.Equal(UvBakeBones.Region.Genital, UvBakeBones.RegionOf("iv_kintama_phys_l"));
        Assert.Equal(UvBakeBones.Region.ThighR, UvBakeBones.RegionOf("iv_daitai_phys_r"));
        Assert.Equal(UvBakeBones.Region.HandL, UvBakeBones.RegionOf("j_hito_b_l"));
        Assert.Equal(UvBakeBones.Region.Torso, UvBakeBones.RegionOf("j_mune_r"));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-22")]
    public void Non_voting_bones_take_neighbour_region() // [EDGE-tbse-uvmap-22]
    {
        // A strip of four vertices: the first on the thigh, the rest on an earring node, which votes for nothing.
        (string, float)[][] weights = [[("j_asi_a_l", 1f)], [("n_ear_b_l", 1f)], [("n_ear_b_l", 1f)], [("n_root", 1f)]];
        int[] tri = [0, 1, 2, 1, 3, 2];
        var regions = UvBakeBones.VertexRegions(weights, tri);
        Assert.All(regions, r => Assert.Equal(UvBakeBones.Region.ThighL, r));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-22")]
    public void Report_names_how_each_bone_resolves() // [EDGE-tbse-uvmap-22]
    {
        (string, float)[][] weights = [[("j_kosi", 1f)], [("n_ear_b_l", 0.9f), ("j_kosi", 0.1f)], [("x_custom", 1f)]];
        var report = UvBakeBones.Report(weights, b => b == "j_kosi");
        Assert.Contains(report, r => r.Bone == "j_kosi" && r.How == UvBakeBones.Resolution.Own);
        Assert.Contains(report, r => r.Bone == "n_ear_b_l" && r.How == UvBakeBones.Resolution.Override);
        Assert.Contains(report, r => r.Bone == "x_custom" && r.How == UvBakeBones.Resolution.Absent);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    public void Regions_neighbour_only_along_the_body() // [AC-tbse-uvmap-2]
    {
        Assert.True(UvBakeBones.Adjacent(UvBakeBones.Region.ThighL, UvBakeBones.Region.Pelvis));
        Assert.True(UvBakeBones.Adjacent(UvBakeBones.Region.HandL, UvBakeBones.Region.ForearmL));
        Assert.False(UvBakeBones.Adjacent(UvBakeBones.Region.ThighL, UvBakeBones.Region.ThighR));
        Assert.False(UvBakeBones.Adjacent(UvBakeBones.Region.HandL, UvBakeBones.Region.Pelvis));
    }
}
