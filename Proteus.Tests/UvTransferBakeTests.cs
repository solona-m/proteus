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
    [Trait("Plan", "EDGE-uvmap-nipples-5")]
    public void Unreachable_texels_invalid_and_fallbacks_counted() // [EDGE-tbse-uvmap-25] [EDGE-uvmap-nipples-5]
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
    [Trait("Plan", "EDGE-uvmap-nipples-35")]
    public void Crease_finds_a_mild_pec_border_and_refuses_a_near_flat_one() // [EDGE-uvmap-nipples-35]
    {
        // A pec leaning out 0.5 per metre of height from a border 4 cm under the nipple to 2 cm over it, flat below and
        // above: the border is found.
        float Pec(float x, float y, float lean) => lean * Math.Clamp(y - 1.2f, 0f, 0.06f)
                                                   + Bump(x, y, 0.08f, 1.24f, 0.004f, 0.006f) + Bump(x, y, -0.08f, 1.24f, 0.004f, 0.006f);
        var mild = Chest((x, y) => Pec(x, y, 0.5f));
        var (crease, failure) = UvBakeNipples.Crease(mild, UvBakeNipples.Find(mild).Found!.Value.L);
        Assert.True(crease != null, failure);
        Assert.InRange(crease!.Value.Pos.Y, 1.19f, 1.205f);

        // Leaning 0.2: under the 0.3 a crease must turn by.
        var faint = Chest((x, y) => Pec(x, y, 0.2f));
        var (none, why) = UvBakeNipples.Crease(faint, UvBakeNipples.Find(faint).Found!.Value.L);
        Assert.Null(none);
        Assert.Contains("no under-bust crease", why);
    }

    /// <summary>
    /// The front half of a torso, 1.00–1.45 m high: an elliptical cross-section 34 cm wide and 12 cm deep, so it has
    /// flanks that face sideways, its depth raised by <paramref name="bulge"/>(x, y). Bones by height: the middle spine
    /// below 1.225 m, the upper spine above, the clavicles over 1.38 m. Normals from the faces.
    /// </summary>
    internal static BakeBody Torso(Func<float, float, float> bulge)
    {
        const int Around = 100, Up = 90;
        const float A = 0.17f, B = 0.12f;
        var pos = new List<System.Numerics.Vector3>();
        var uv = new List<float>();
        var w = new List<(string, float)[]>();
        for (int j = 0; j <= Up; j++)
            for (int i = 0; i <= Around; i++)
            {
                float th = (-100f + 200f * i / Around) * MathF.PI / 180f;
                float y = 1.0f + 0.45f * j / Up;
                float x = A * MathF.Sin(th), z = B * MathF.Cos(th);   // the front 12 cm forward, as a body's chest is
                float out_ = bulge(x, y);
                var n = System.Numerics.Vector3.Normalize(new(MathF.Sin(th) / A, 0, MathF.Cos(th) / B));
                pos.Add(new System.Numerics.Vector3(x, y, z) + n * out_);
                uv.AddRange([(float)i / Around, (float)j / Up]);
                string bone = y > 1.38f ? (x >= 0 ? "j_sako_l" : "j_sako_r") : y < 1.225f ? "j_sebo_b" : "j_sebo_c";
                w.Add([(bone, 1f)]);
            }
        var tri = new List<int>();
        for (int j = 0; j < Up; j++)
            for (int i = 0; i < Around; i++)
            {
                int a = j * (Around + 1) + i, b = a + 1, c = a + Around + 1, d = c + 1;
                tri.AddRange([a, b, c, b, d, c]);   // facing out: around × up
            }
        var nrm = new System.Numerics.Vector3[pos.Count];
        for (int t = 0; t < tri.Count; t += 3)
        {
            var f = System.Numerics.Vector3.Cross(pos[tri[t + 1]] - pos[tri[t]], pos[tri[t + 2]] - pos[tri[t]]);
            for (int k = 0; k < 3; k++) nrm[tri[t + k]] += f;
        }
        var flat = new List<float>();
        var nflat = new List<float>();
        for (int v = 0; v < pos.Count; v++)
        {
            flat.AddRange([pos[v].X, pos[v].Y, pos[v].Z]);
            var n = System.Numerics.Vector3.Normalize(nrm[v]);
            nflat.AddRange([n.X, n.Y, n.Z]);
        }
        return new BakeBody([new BakeBody.Part(flat.ToArray(), uv.ToArray(), tri.ToArray(), w.ToArray(), nflat.ToArray())]);
    }

    /// <summary>A man's chest: a pec leaning out above a border 3 cm under a nipple at 1.26 m.</summary>
    private static float MansChest(float x, float y)
        => 0.4f * Math.Clamp(y - 1.23f, 0f, 0.05f)
           + Bump(x, y, 0.08f, 1.26f, 0.003f, 0.006f) + Bump(x, y, -0.08f, 1.26f, 0.003f, 0.006f);

    /// <summary>A woman's: breasts standing 4 cm forward, their nipples 3 cm lower, a fold 4 cm under them.</summary>
    private static float WomansChest(float x, float y) => Breasts(x, y, apexY: 1.23f);

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-1")]
    [Trait("Plan", "AC-uvmap-nipples-4")]
    public void Field_lands_a_mans_nipples_on_a_womans_and_holds_its_pins() // [AC-uvmap-nipples-1] [AC-uvmap-nipples-4]
    {
        var man = Torso(MansChest);
        var woman = Torso(WomansChest);
        var womanBvh = new TriangleBvh(woman);
        var (field, failure) = ChestField.Build(man, woman, womanBvh);
        Assert.True(field != null, failure);
        var c = field!.Measure(man);
        Assert.Empty(field.Check(c));
        Assert.All(field.ResidualMm.Values, r => Assert.InRange(r, 0f, UvTransferQuality.Gate.FieldResidualMm));
        Assert.InRange(c.MaxPinMm, 0f, UvTransferQuality.Gate.FieldPinMm);

        // Each nipple, moved by the field and cast, lands on hers; unmoved, it does not.
        var (mine, _) = UvBakeNipples.Find(man);
        var (hers, _) = UvBakeNipples.Find(woman);
        var n = mine!.Value.L;
        var normal = man.SmoothAt(n.Tri, n.Bary.X, n.Bary.Y, n.Bary.Z);
        var region = man.RegionAt(n.Tri, n.Bary.X, n.Bary.Y, n.Bary.Z);
        System.Numerics.Vector3 Landed(UvTransferBake.Landing l) => woman.At(l.Tri, l.U, l.V, l.W).Pos;
        var moved = Landed(field.Land(womanBvh, woman, n.Pos, normal, region));
        var plain = Landed(UvTransferBake.Land(womanBvh, woman, n.Pos, normal, region));
        float Across(System.Numerics.Vector3 d) => (d - normal * System.Numerics.Vector3.Dot(d, normal)).Length();
        Assert.InRange(Across(moved - hers!.Value.L.Pos) * 1000f, 0f, UvTransferQuality.Gate.FieldResidualMm);
        Assert.True(Across(plain - hers.Value.L.Pos) > 0.01f, "unmoved, the nipple should land well off hers");

        // Out of its reach the field moves nothing, and the landing is the plain one.
        var far = new System.Numerics.Vector3(0f, 1.02f, 0f);
        Assert.False(field.InSupport(far));
        Assert.Equal(far, field.Apply(far));
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-45")]
    public void A_field_cast_from_a_breast_onto_a_flat_chest_is_refused() // [EDGE-uvmap-nipples-45]
    {
        var man = Torso(MansChest);
        var (field, why) = ChestField.Build(Torso(WomansChest), man, new TriangleBvh(man));
        Assert.Null(field);
        Assert.Contains("cast the other way and invert", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-3")]
    [Trait("Plan", "EDGE-uvmap-nipples-4")]
    [Trait("Plan", "EDGE-uvmap-nipples-39")]
    [Trait("Plan", "EDGE-uvmap-nipples-40")]
    [Trait("Plan", "EDGE-uvmap-nipples-41")]
    [Trait("Plan", "EDGE-uvmap-nipples-42")]
    [Trait("Plan", "EDGE-uvmap-nipples-44")]
    public void Every_field_check_names_its_miss() // [EDGE-uvmap-nipples-3] [EDGE-uvmap-nipples-4] [EDGE-uvmap-nipples-39] [EDGE-uvmap-nipples-40] [EDGE-uvmap-nipples-41] [EDGE-uvmap-nipples-42] [EDGE-uvmap-nipples-44]
    {
        var man = Torso(MansChest);
        var woman = Torso(WomansChest);
        var field = ChestField.Build(man, woman, new TriangleBvh(woman)).Field!;
        var bad = new ChestField.Checks
        {
            MinJacobian = 0.1f, FoldedTriangles = 3, MaxPinMm = 0.5f, MaxOffTorsoMm = 4f, MaxMm = 1000f,
            MaxReverseMm = 2f, MaxStillMm = 3f,
        };
        var miss = field.Check(bad);
        foreach (var said in new[] { "folds: 3 triangles", "moves a pin", "arm or neck", "overshoots", "the wrong way", "sternum or clavicle row" })
            Assert.Contains(miss, m => m.Contains(said));
        // A ridge cannot hold two pins a hair apart that want different things: what it solves moves one of them.
        double p = ChestField.Phi(0.00001);
        double[,] phi = { { 1 + ChestField.Ridge, p }, { p, 1 + ChestField.Ridge } };
        var w = ChestField.Solve(phi, new double[,] { { 0.03 }, { 0 } })!;
        double atSecond = p * w[0, 0] + w[1, 0];
        Assert.True(Math.Abs(atSecond) * 1000 > UvTransferQuality.Gate.FieldPinMm, $"the second pin moves {atSecond * 1000:F3} mm");
    }

    /// <summary>A size² map storing <paramref name="at"/>(x, y) as a uv, every texel valid.</summary>
    private static UvTransferBake.Result Map(int size, Func<int, int, (float U, float V)> at)
    {
        var r = new ushort[size * size];
        var g = new ushort[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (u, v) = at(x, y);
                r[y * size + x] = UvTransferBake.Quantize(u);
                g[y * size + x] = UvTransferBake.Quantize(v);
            }
        return new UvTransferBake.Result(r, g, Enumerable.Repeat(true, size * size).ToArray(), size, new UvTransferBake.Stats());
    }

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-4")]
    [Trait("Plan", "EDGE-uvmap-nipples-27")]
    public void Stretch_compares_each_map_with_itself_and_finds_a_tear_at_the_edge() // [AC-uvmap-nipples-4] [EDGE-uvmap-nipples-27]
    {
        const int size = 64;
        var raster = new UvTransferBake.Raster(new int[size * size], new float[size * size], new float[size * size], size, 0, []);
        var plain = Map(size, (x, y) => (x / 63f * 0.5f, y / 63f * 0.5f));
        bool Reach(int i) => i % size is >= 16 and < 48 && i / size is >= 16 and < 48;

        // Squeezed twice over sideways inside the reach, joined at its edges: distortion, no tear, nothing changed outside.
        float Squeeze(int x) => x < 16 ? x : x < 48 ? 16 + (x - 16) * 0.5f : x - 16f;
        var squeezed = Map(size, (x, y) => Reach(y * size + x) ? (Squeeze(x) / 63f * 0.5f, y / 63f * 0.5f) : (x / 63f * 0.5f, y / 63f * 0.5f));
        var s = UvTransferQuality.WarpStretch(squeezed, plain, raster, Reach);
        Assert.True(s.AreaP99 > s.PlainAreaP99, $"area {s.AreaP99} against {s.PlainAreaP99}");
        Assert.True(s.AnisoP99 > 1.5, $"anisotropy {s.AnisoP99}");
        Assert.Equal(0, s.ChangedOutside);
        Assert.True(s.EdgePairs > 0);

        // Shifted a sixth of the sheet inside the reach: neighbours across its edge land far apart — a tear, named.
        var torn = Map(size, (x, y) => Reach(y * size + x) ? (x / 63f * 0.5f + 1f / 6, y / 63f * 0.5f) : (x / 63f * 0.5f, y / 63f * 0.5f));
        var t = UvTransferQuality.WarpStretch(torn, plain, raster, Reach);
        Assert.True(t.EdgeJumpMax > 5 * Math.Max(t.PlainEdgeJumpMax, 1));
        Assert.Contains(UvTransferQuality.Check("m", new UvTransferQuality.Figures { DestCoverage = 1, Stretch = t }), m => m.Contains("tears at its edge"));

        // Changed outside the reach: counted.
        var spill = Map(size, (x, y) => (x / 63f * 0.5f + (x < 8 ? 0.01f : 0f), y / 63f * 0.5f));
        Assert.True(UvTransferQuality.WarpStretch(spill, plain, raster, Reach).ChangedOutside > 0);
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-46")]
    public void Inverse_skips_cells_a_seam_splits() // [EDGE-uvmap-nipples-46]
    {
        // The forward map jumps half the sheet between columns 31 and 32: the cells across that seam draw nothing.
        const int size = 64;
        var forward = Map(size, (x, y) => (x < 32 ? x / 63f * 0.4f : 0.6f + x / 63f * 0.3f, y / 63f));
        var back = new UvTransferBake.Result(new ushort[size * size], new ushort[size * size], new bool[size * size], size, new UvTransferBake.Stats());
        var (_, drawn) = UvTransferBake.InvertInto(forward, _ => true, back, _ => true);
        // Between where column 31 lands (u 0.197) and column 32 (u 0.752) nothing is drawn.
        for (int y = 1; y < size - 1; y++)
            for (int x = 14; x < 47; x++)
                Assert.False(drawn[y * size + x], $"({x},{y}) drawn across the seam");
    }

    [Fact]
    [Trait("Plan", "EDGE-uvmap-nipples-47")]
    public void No_lower_row_is_refused() // [EDGE-uvmap-nipples-47]
    {
        // Under a crease on a panel cast onto the same panel, the landing has caught up at once: the row is 4 cm down.
        var dest = Chest((_, _) => 0f);
        var bvh = new TriangleBvh(dest);
        var crease = new System.Numerics.Vector3(0.08f, 1.2f, 0f);
        Assert.Equal(1.2f - ChestField.LowerRow, ChestField.LowerRowUnder(dest, bvh, dest, bvh, crease, crease)!.Value, 3);

        // Onto a panel out of reach, nothing lands: no row.
        var far = Chest((_, _) => 1f);
        Assert.Null(ChestField.LowerRowUnder(dest, bvh, far, new TriangleBvh(far), crease, crease));
    }

    [Fact]
    [Trait("Plan", "AC-uvmap-nipples-4")]
    [Trait("Plan", "EDGE-uvmap-nipples-43")]
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
    [Trait("Plan", "EDGE-uvmap-nipples-46")]
    public void Inverse_redraws_only_what_the_forward_map_reaches() // [AC-uvmap-nipples-9] [EDGE-uvmap-nipples-46]
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
        // Only the forward texels in the top half reach; nothing is drawn into the back map's last column (no skin there).
        var (inv, drawn) = UvTransferBake.InvertInto(forward, i => i / size < size / 2, back, q => q % size != size - 1);
        int count = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                if (!drawn[i]) { Assert.False(inv.Valid[i]); continue; }
                count++;
                Assert.True(y < size / 2);
                Assert.NotEqual(size - 1, x);
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
