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
        f.LandmarksMm["underBustL"] = 31f;      // the crease: bounded by the plain map's 30.2 rounded up, 31
        f.PlainLandmarksMm["underBustL"] = 30.2f;
        f.LandmarksMm["underBustR"] = 16f;      // no plain figure: the torso's 15 mm
        f.LandmarksMm["underBustOldL"] = 99f;   // the old point, reported only
        f.LandmarksMm["nippleL"] = 40f;         // nipples by how far across the skin, not how far apart
        f.LandmarksTangentMm["nippleL"] = 2f;
        f.LandmarksMm["nippleR"] = 2f;
        f.LandmarksTangentMm["nippleR"] = 3.5f;
        var miss = UvTransferQuality.Check("m", f);
        Assert.Contains(miss, m => m.Contains("coverage 50"));
        Assert.Contains(miss, m => m.Contains("folds 50"));
        Assert.Contains(miss, m => m.Contains("round-trip gross"));
        Assert.Contains(miss, m => m.Contains("sternum coverage"));
        Assert.Contains(miss, m => m.Contains("landmark sternum"));
        Assert.DoesNotContain(miss, m => m.Contains("underBustL"));
        Assert.Contains(miss, m => m.Contains("landmark underBustR off by 16.0 mm > 15 mm"));
        Assert.DoesNotContain(miss, m => m.Contains("underBustOld"));
        Assert.DoesNotContain(miss, m => m.Contains("nippleL"));
        Assert.Contains(miss, m => m.Contains("nippleR off by 3.5 mm across the skin"));
    }

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-4")]
    public void Gate_names_chest_distortion_and_changes_outside_reach() // [AC-uvmap-nipples-4] [EDGE-uvmap-nipples-27]
    {
        var f = new UvTransferQuality.Figures { DestCoverage = 1 };
        f.Stretch = new UvTransferQuality.StretchFigures { AreaP99 = 2.4, PlainAreaP99 = 2.0, AnisoP99 = 2.6, PlainAnisoP99 = 2.0, ChangedOutside = 0 };
        var miss = UvTransferQuality.Check("m", f);   // area 1.2 × the plain map's passes; anisotropy 1.3 × does not
        Assert.DoesNotContain(miss, m => m.Contains("area p99"));
        Assert.Contains(miss, m => m.Contains("anisotropy p99 2.60"));
        f.Stretch.ChangedOutside = 3;
        Assert.Contains(UvTransferQuality.Check("m", f), m => m.Contains("changed 3 texels outside its reach"));
    }

    /// <summary>
    /// A front-facing torso panel, 36 cm wide from 1.00 m to 1.45 m high, every 5 mm: its lower half weighted to the
    /// middle spine and its upper half to the upper spine (both Torso), its depth <paramref name="z"/>(x, y).
    /// </summary>
    internal static BakeBody Chest(Func<float, float, float> z, float step = 0.005f)
    {
        int nx = (int)MathF.Round(0.36f / step), ny = (int)MathF.Round(0.45f / step);
        var pos = new List<float>();
        var uv = new List<float>();
        var nrm = new List<float>();
        var w = new List<(string, float)[]>();
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                float x = -0.18f + i * step, y = 1.0f + j * step;
                pos.AddRange([x, y, z(x, y)]);
                uv.AddRange([(float)i / nx, (float)j / ny]);
                float dzdx = (z(x + 1e-3f, y) - z(x - 1e-3f, y)) / 2e-3f, dzdy = (z(x, y + 1e-3f) - z(x, y - 1e-3f)) / 2e-3f;
                var n = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-dzdx, -dzdy, 1));
                nrm.AddRange([n.X, n.Y, n.Z]);
                w.Add([(y < 1.225f ? "j_sebo_b" : "j_sebo_c", 1f)]);
            }
        var tri = new List<int>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                tri.AddRange([a, b, c, b, d, c]);
            }
        return new BakeBody([new BakeBody.Part(pos.ToArray(), uv.ToArray(), tri.ToArray(), w.ToArray(), nrm.ToArray())]);
    }

    private static float Bump(float x, float y, float cx, float cy, float height, float sigma)
        => height * MathF.Exp(-((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (2 * sigma * sigma));

    /// <summary>A breast on each side, a nipple at each apex: the half-ellipsoid ends at a fold 4 cm under the apex.</summary>
    private static float Breasts(float x, float y, float apexY = 1.24f, float nippleDy = 0f)
    {
        float z = 0;
        foreach (float cx in new[] { 0.08f, -0.08f })
        {
            float r2 = MathF.Pow((x - cx) / 0.055f, 2) + MathF.Pow((y - apexY) / 0.04f, 2);
            if (r2 < 1) z = MathF.Max(z, 0.04f * MathF.Sqrt(1 - r2));
            z += Bump(x, y, cx, apexY + nippleDy, 0.004f, 0.006f);
        }
        return z;
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-1")]
    public void Nipple_finder_finds_bump_apex_and_none_without_a_bump() // [EDGE-uvmap-nipples-1]
    {
        var (found, failure) = UvBakeNipples.Find(Chest((x, y) => Breasts(x, y)));
        Assert.True(found != null, failure);
        Assert.InRange(found!.Value.L.Pos.X, 0.075f, 0.085f);
        Assert.InRange(found.Value.L.Pos.Y, 1.235f, 1.245f);
        Assert.InRange(found.Value.R.Pos.X, -0.085f, -0.075f);

        var (none, why) = UvBakeNipples.Find(Chest((_, _) => 0f));
        Assert.Null(none);
        Assert.Contains("no nipple", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-2")]
    public void Asymmetric_nipples_are_refused() // [EDGE-uvmap-nipples-2]
    {
        // The right nipple 2 cm higher than the left.
        var body = Chest((x, y) => Bump(x, y, 0.08f, 1.24f, 0.004f, 0.006f) + Bump(x, y, -0.08f, 1.26f, 0.004f, 0.006f));
        var (found, why) = UvBakeNipples.Find(body);
        Assert.Null(found);
        Assert.Contains("asymmetric", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-35")]
    public void Crease_finds_the_fold_under_a_breast_and_refuses_a_flat_chest() // [EDGE-uvmap-nipples-35]
    {
        var breasts = Chest((x, y) => Breasts(x, y));
        var nipple = UvBakeNipples.Find(breasts).Found!.Value.L;
        var (crease, failure) = UvBakeNipples.Crease(breasts, nipple);
        Assert.True(crease != null, failure);
        Assert.InRange(crease!.Value.Pos.Y, 1.19f, 1.21f);   // the fold, 4 cm under the apex
        Assert.True(crease.Value.SlopeChange >= UvBakeNipples.MinSlopeChange);

        // A flat chest with only a nipple: nothing turns.
        var flat = Chest((x, y) => Bump(x, y, 0.08f, 1.24f, 0.004f, 0.006f) + Bump(x, y, -0.08f, 1.24f, 0.004f, 0.006f));
        var (none, why) = UvBakeNipples.Crease(flat, UvBakeNipples.Find(flat).Found!.Value.L);
        Assert.Null(none);
        Assert.Contains("no under-bust crease", why);
    }

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-4")]
    public void Field_solver_and_conditioning() // [AC-uvmap-nipples-4] [EDGE-uvmap-nipples-43]
    {
        double[,] a = { { 4, 1, 0 }, { 1, 3, 1 }, { 0, 1, 2 } };
        double[,] b = { { 1, 0 }, { 2, 1 }, { 3, -1 } };
        var x = ChestField.Solve(a, b)!;
        for (int r = 0; r < 3; r++)
            for (int k = 0; k < 2; k++)
            {
                double s = 0;
                for (int c = 0; c < 3; c++) s += a[r, c] * x[c, k];
                Assert.Equal(b[r, k], s, 9);
            }
        Assert.Null(ChestField.Solve(new double[,] { { 1, 2 }, { 2, 4 } }, new double[,] { { 1 }, { 2 } }));

        Assert.Equal(1.0, ChestField.ConditionNumber(new double[,] { { 2, 0 }, { 0, 2 } }), 9);
        Assert.Equal(100.0, ChestField.ConditionNumber(new double[,] { { 100, 0 }, { 0, 1 } }), 6);
        // Two near-coincident bumps: the system the ridge is there for.
        double p = ChestField.Phi(0.00001);
        Assert.True(ChestField.ConditionNumber(new double[,] { { 1, p }, { p, 1 } }) > ChestField.MaxCondition);
        Assert.Equal(0.0, ChestField.Phi(1.0));
        Assert.Equal(1.0, ChestField.Phi(0.0));
    }

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-9")]
    public void Inverse_redraws_only_what_the_forward_map_reaches() // [AC-uvmap-nipples-9]
    {
        // A forward map that shifts the sheet a quarter right: its inverse shifts it back, where it reaches.
        const int size = 64;
        var r = new ushort[size * size];
        var g = new ushort[size * size];
        var ok = new bool[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                if (x >= size * 3 / 4) continue;
                r[i] = UvTransferBake.Quantize((x + size / 4f) / (size - 1));
                g[i] = UvTransferBake.Quantize((float)y / (size - 1));
                ok[i] = true;
            }
        var forward = new UvTransferBake.Result(r, g, ok, size, new UvTransferBake.Stats());
        var back = new UvTransferBake.Result(new ushort[size * size], new ushort[size * size], new bool[size * size], size, new UvTransferBake.Stats());
        var owner = Enumerable.Repeat(0, size * size).ToArray();
        var raster = new UvTransferBake.Raster(owner, new float[size * size], new float[size * size], size, 0, []);
        // Only the forward texels in the top half reach.
        var (inv, drawn) = UvTransferBake.InvertInto(forward, i => i / size < size / 2, back, raster);
        int count = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                if (!drawn[i]) { Assert.False(inv.Valid[i]); continue; }
                count++;
                Assert.True(y < size / 2);
                Assert.InRange(inv.R[i] / 65535f * (size - 1) - (x - size / 4f), -0.01f, 0.01f);
                Assert.InRange(inv.G[i] / 65535f * (size - 1) - y, -0.01f, 0.01f);
            }
        Assert.True(count > size * size / 4);
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
