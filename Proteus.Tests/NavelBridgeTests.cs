using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using static Proteus.Services.SecondSkinWriter;

namespace Proteus.Tests;

/// <summary>
/// The navel fill on synthetic bellies: a gently curved front with a pit, groove or bowl pressed in where each test
/// wants it. <see cref="NavelBridgeLocalTests"/> runs the same passes on real bodies.
/// </summary>
public class NavelBridgeTests
{
    private const float Spacing = 0.00125f;
    private const float NavelY = 1.05f;

    /// <summary>
    /// A belly: x across ±75 mm, y from 0.95 to 1.15, curving back round a 15 cm radius sideways and a 30 cm one up
    /// and down, with <paramref name="dent"/> pressed in (how deep at x, y) and <paramref name="warp"/> moving points
    /// across it. Two triangles a grid square, wound to face +Z. <paramref name="seam"/> splits the mesh down x = 0
    /// into two halves whose vertices along the seam are copies, as a UV seam leaves them.
    /// </summary>
    internal static (Vec3[] Pos, Vec3[] Nrm, ushort[] Tris) Belly(Func<float, float, float>? dent = null,
        Func<Vec3, Vec3>? warp = null, float scale = 1f, bool seam = false, Func<Vec3, bool>? hole = null)
    {
        float half = 0.075f * scale, lo = NavelY - 0.1f * scale, hi = NavelY + 0.1f * scale, step = Spacing * scale;
        int nx = (int)MathF.Round(2 * half / step) + 1, ny = (int)MathF.Round((hi - lo) / step) + 1;
        int mid = nx / 2;
        var pos = new List<Vec3>();
        var at = new Dictionary<(int, int, int), ushort>();
        ushort V(int i, int j, int side)
        {
            // With a seam, the column at x = 0 is held twice: once by each half.
            int key = seam && i == mid ? side : 0;
            if (at.TryGetValue((i, j, key), out var v)) return v;
            float x = -half + i * step, y = lo + j * step;
            float r = 0.15f * scale, rv = 0.3f * scale;
            float z = 0.1f * scale + (MathF.Sqrt(r * r - x * x) - r) - (y - NavelY) * (y - NavelY) / (2 * rv);
            if (dent != null) z -= dent(x, y);
            var p = new Vec3(x, y, z);
            if (warp != null) p = warp(p);
            at[(i, j, key)] = v = (ushort)pos.Count;
            pos.Add(p);
            return v;
        }
        var tris = new List<ushort>();
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                int side = i < mid ? 0 : 1;
                ushort a = V(i, j, side), b = V(i + 1, j, side), c = V(i, j + 1, side), d = V(i + 1, j + 1, side);
                foreach (var (p, q, r) in new[] { (a, b, d), (a, d, c) })
                {
                    if (hole != null)
                    {
                        var ctr = new Vec3((pos[p].X + pos[q].X + pos[r].X) / 3, (pos[p].Y + pos[q].Y + pos[r].Y) / 3,
                                           (pos[p].Z + pos[q].Z + pos[r].Z) / 3);
                        if (hole(ctr)) continue;
                    }
                    tris.Add(p); tris.Add(q); tris.Add(r);
                }
            }
        var positions = pos.ToArray();
        return (positions, Normals(positions, tris), tris.ToArray());
    }

    /// <summary>Area-weighted vertex normals.</summary>
    private static Vec3[] Normals(Vec3[] pos, List<ushort> tris)
    {
        var n = new Vec3[pos.Length];
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            Vec3 a = pos[tris[t]], b = pos[tris[t + 1]], c = pos[tris[t + 2]];
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            var f = new Vec3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
            foreach (int k in new[] { tris[t], tris[t + 1], tris[t + 2] })
                n[k] = new Vec3(n[k].X + f.X, n[k].Y + f.Y, n[k].Z + f.Z);
        }
        for (int i = 0; i < n.Length; i++)
        {
            float l = MathF.Sqrt(n[i].X * n[i].X + n[i].Y * n[i].Y + n[i].Z * n[i].Z);
            n[i] = l > 0 ? new Vec3(n[i].X / l, n[i].Y / l, n[i].Z / l) : new Vec3(0, 0, 1);
        }
        return n;
    }

    /// <summary>A rounded pit <paramref name="depth"/> deep and about 2.35 <paramref name="sigma"/> across at half depth.</summary>
    private static Func<float, float, float> Pit(float depth = 0.005f, float sigma = 0.003f, float x0 = 0f, float y0 = NavelY)
        => (x, y) => depth * MathF.Exp(-((x - x0) * (x - x0) + (y - y0) * (y - y0)) / (2 * sigma * sigma));

    private static BodyBridge.NavelMesh Part((Vec3[] Pos, Vec3[] Nrm, ushort[] Tris) b, float[]? bust = null, float[]? thigh = null)
        => new(b.Pos, b.Nrm, Array.ConvertAll(b.Tris, t => (int)t), bust ?? new float[b.Pos.Length], thigh ?? new float[b.Pos.Length]);

    private static BodyBridge.NavelSite? Locate((Vec3[] Pos, Vec3[] Nrm, ushort[] Tris) b, List<string>? log = null,
                                                float[]? bust = null, float[]? thigh = null)
        => BodyBridge.LocateNavel([Part(b, bust, thigh)], log == null ? null : log.Add, out _);

    private static (BustBridgePlanView? Plan, BodyBridge.NavelFillReport Report) Fill(
        (Vec3[] Pos, Vec3[] Nrm, ushort[] Tris) b, BodyBridge.NavelSite site, bool[]? covered = null, bool[]? pin = null,
        float strength = 1f, List<string>? log = null)
    {
        var plan = BodyBridge.NavelFill(b.Pos, b.Nrm, b.Tris, site, covered, pin, strength, log == null ? null : log.Add,
                                        out var report);
        return (plan == null ? null : new BustBridgePlanView(plan, b.Pos), report);
    }

    /// <summary>A plan's per-vertex result positions, and which vertices it moved.</summary>
    private sealed class BustBridgePlanView(BodyBridge.BustBridgePlan plan, Vec3[] pos)
    {
        public BodyBridge.BustBridgePlan Plan => plan;
        public Vec3 Final(int i) => new(pos[i].X + plan.Delta[i].X, pos[i].Y + plan.Delta[i].Y, pos[i].Z + plan.Delta[i].Z);
        public bool Moved(int i) => Len(plan.Delta[i]) > 1e-7f;
    }

    private static float Len(Vec3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    private static float Dist(Vec3 a, Vec3 b) => Len(new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z));

    // ── finding and filling ───────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-navel-bridge-4")]
    public void Fills_a_rounded_navel_flat()
    {
        var b = Belly(Pit());
        var site = Locate(b);
        Assert.NotNull(site);
        Assert.True(MathF.Abs(site!.Centre.X) < 0.002f && MathF.Abs(site.Centre.Y - NavelY) < 0.002f, site.ToString());

        var (plan, report) = Fill(b, site);
        Assert.NotNull(plan);
        Assert.Equal(BodyBridge.NavelOutcome.Filled, report.Outcome);
        Assert.True(report.DepthBefore > 0.004f, $"read {report.DepthBefore * 1000:0.##}mm deep");
        Assert.True(report.DepthAfter <= 0.0005f, $"{report.DepthAfter * 1000:0.##}mm left");
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-5")]
    public void Nothing_outside_the_pit_moves()
    {
        var b = Belly(Pit());
        var site = Locate(b)!;
        var (plan, _) = Fill(b, site);
        for (int i = 0; i < b.Pos.Length; i++)
            if (plan!.Moved(i))
                Assert.True(Dist(b.Pos[i], site.Centre) <= site.DiscRadius, $"vertex {i} moved, {Dist(b.Pos[i], site.Centre) * 1000:0}mm out");
        // Well clear of the pit the belly is exactly as it was.
        for (int i = 0; i < b.Pos.Length; i++)
            if (Dist(b.Pos[i], site.Centre) > 0.02f) Assert.False(plan!.Moved(i));
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-5")]
    public void The_filled_navel_faces_out_and_stays_outside()
    {
        var b = Belly(Pit());
        var (plan, report) = Fill(b, Locate(b)!);
        Assert.Equal(0, report.InwardTriangles);
        Assert.Equal(0, report.InsideNodes);
        for (int t = 0; t < b.Tris.Length; t += 3)
        {
            Vec3 p = plan!.Final(b.Tris[t]), q = plan.Final(b.Tris[t + 1]), r = plan.Final(b.Tris[t + 2]);
            float nz = (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
            Assert.True(nz > 0f, $"triangle {t / 3} turned over");
        }
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-5")]
    public void The_rim_is_reshaded_but_not_moved()
    {
        var b = Belly(Pit());
        var (view, _) = Fill(b, Locate(b)!);
        var plan = view!.Plan;
        var moved = new HashSet<int>();
        for (int i = 0; i < b.Pos.Length; i++) if (view.Moved(i)) moved.Add(plan.NodeOf[i]);
        int rim = 0;
        for (int i = 0; i < b.Pos.Length; i++)
        {
            int n = plan.NodeOf[i];
            if (plan.NodeWeight[n] > 0f && !moved.Contains(n)) rim++;
            // Everything that moved is reshaded, and kept off the extra clearance a bridge gets.
            if (moved.Contains(n)) { Assert.True(plan.NodeWeight[n] > 0f); Assert.True(plan.NoExtraClearance![n]); }
        }
        Assert.True(rim > 0, "no rim reshaded");
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-7")]
    [Trait("Plan", "EDGE-navel-bridge-1")]
    public void A_flat_belly_has_no_navel()
    {
        var b = Belly();
        Assert.Null(BodyBridge.LocateNavel([Part(b)], null, out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Theory]
    [Trait("Plan", "EDGE-navel-bridge-2")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_groove_is_not_a_navel(bool upAndDown)
    {
        // The whole length of the belly, and one that ends: 40 mm long and 3 mm deep.
        var through = Belly((x, y) => 0.003f * MathF.Exp(-(upAndDown ? x * x : (y - NavelY) * (y - NavelY)) / (2 * 0.002f * 0.002f)));
        Assert.Null(Locate(through));
        var ending = Belly((x, y) =>
        {
            float along = upAndDown ? y - NavelY : x, across = upAndDown ? x : y - NavelY;
            float end = MathF.Max(0f, MathF.Abs(along) - 0.02f);
            return 0.003f * MathF.Exp(-(across * across + end * end) / (2 * 0.002f * 0.002f));
        });
        Assert.Null(Locate(ending));
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-3")]
    public void The_deepest_pit_wins()
    {
        var shallow = Pit(0.0015f, 0.002f, 0.025f, NavelY + 0.04f);
        var b = Belly((x, y) => Pit()(x, y) + shallow(x, y));
        var site = Locate(b)!;
        Assert.True(MathF.Abs(site.Centre.Y - NavelY) < 0.002f, site.ToString());
        var (plan, _) = Fill(b, site);
        for (int i = 0; i < b.Pos.Length; i++)
            if (Dist(b.Pos[i], new Vec3(0.025f, NavelY + 0.04f, b.Pos[i].Z)) < 0.003f) Assert.False(plan!.Moved(i));
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-4")]
    public void A_wide_bowl_is_not_a_navel()
        => Assert.Null(Locate(Belly(Pit(0.004f, 0.015f))));

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-5")]
    public void A_pit_off_the_midline_is_ignored()
        => Assert.Null(Locate(Belly(Pit(x0: 0.06f))));

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-6")]
    public void A_pit_on_breast_or_thigh_skin_or_above_the_breasts_is_ignored()
    {
        var b = Belly(Pit());
        float[] Near(float w) => [.. b.Pos.Select(p => Dist(p, new Vec3(0, NavelY, p.Z)) < 0.01f ? w : 0f)];
        Assert.Null(Locate(b, bust: Near(0.5f)));
        Assert.Null(Locate(b, thigh: Near(0.5f)));
        // Breast skin below the pit: everything above the breasts' lowest point is the chest.
        Assert.Null(Locate(b, bust: [.. b.Pos.Select(p => p.Y < 0.97f && MathF.Abs(p.X) > 0.06f ? 0.5f : 0f)]));
        // A stray weight under the floor is not breast skin.
        Assert.NotNull(Locate(b, bust: Near(0.05f)));
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-7")]
    public void An_overhanging_navel_fills_without_folding()
    {
        // The pit's deep part pushed out wider than its mouth: seen down the axis its walls fold under the lip.
        const float sigma = 0.003f;
        var b = Belly(Pit(sigma: sigma), warp: p =>
        {
            float dx = p.X, dy = p.Y - NavelY, r = MathF.Sqrt(dx * dx + dy * dy);
            if (r < 1e-6f) return p;
            float u = r / sigma, grow = 3f * sigma * u * MathF.Exp(-u * u);
            return new Vec3(p.X + dx / r * grow, p.Y + dy / r * grow, p.Z);
        });
        var site = Locate(b);
        Assert.NotNull(site);
        var (plan, report) = Fill(b, site!);
        Assert.Equal(BodyBridge.NavelOutcome.Filled, report.Outcome);
        Assert.NotNull(plan);
        Assert.Equal(0, report.InwardTriangles);
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-8")]
    public void A_fill_that_folds_or_goes_inside_is_counted()
    {
        // Two triangles facing +Z; the second turned over by moving one corner across.
        Vec3[] before = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)];
        ushort[] tris = [0, 1, 2, 1, 3, 2];
        int[] nodeOf = [0, 1, 2, 3];
        var after = (Vec3[])before.Clone();
        after[3] = new Vec3(-1, -1, 0);
        var axis = new Vec3(0, 0, 1);
        Assert.Equal(0, BodyBridge.NavelFolded(before, before, tris, nodeOf, new HashSet<int> { 3 }, _ => true, axis, 1e-9f));
        Assert.Equal(1, BodyBridge.NavelFolded(before, after, tris, nodeOf, new HashSet<int> { 3 }, _ => true, axis, 1e-9f));

        // A closed box, and a corner moved to its middle.
        var (bp, bt) = Box();
        var bAfter = (Vec3[])bp.Clone();
        bAfter[0] = new Vec3(0.5f, 0.5f, 0.5f);
        var boxNodes = Enumerable.Range(0, bp.Length).ToArray();
        Assert.Equal(1, BodyBridge.NavelInside(bp, bAfter, bt, boxNodes, [0]));
        bAfter[0] = new Vec3(-0.5f, -0.5f, -0.5f);
        Assert.Equal(0, BodyBridge.NavelInside(bp, bAfter, bt, boxNodes, [0]));
    }

    /// <summary>A closed unit cube, wound outward.</summary>
    private static (Vec3[] Pos, ushort[] Tris) Box()
    {
        Vec3[] p = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)];
        ushort[] t = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3];
        return (p, t);
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-7")]
    [Trait("Plan", "EDGE-navel-bridge-9")]
    public void A_hem_through_the_navel_declines()
    {
        var b = Belly(Pit());
        var site = Locate(b)!;
        var covered = b.Pos.Select(p => p.X < 0.001f).ToArray();   // cut away down the right of the navel
        var log = new List<string>();
        var (plan, report) = Fill(b, site, covered: covered, log: log);
        Assert.Null(plan);
        Assert.Equal(BodyBridge.NavelOutcome.Declined, report.Outcome);
        Assert.Contains("edge", report.Reason);
        Assert.Contains(log, l => l.StartsWith("navel fill:"));
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-7")]
    [Trait("Plan", "EDGE-navel-bridge-10")]
    public void A_join_or_a_hole_at_the_navel_declines()
    {
        var b = Belly(Pit());
        var site = Locate(b)!;
        var pin = b.Pos.Select(p => MathF.Abs(p.Y - NavelY) < 0.0007f).ToArray();
        var (plan, report) = Fill(b, site, pin: pin);
        Assert.Null(plan);
        Assert.Contains("join", report.Reason);

        var holed = Belly(Pit(), hole: c => Dist(c, new Vec3(0, NavelY, c.Z)) < 0.001f);
        var (plan2, report2) = Fill(holed, site);
        Assert.Null(plan2);
        Assert.Equal(BodyBridge.NavelOutcome.Declined, report2.Outcome);
        Assert.Contains("open", report2.Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-11")]
    public void A_site_on_another_mesh_is_silent()
    {
        var b = Belly(Pit());
        var far = Locate(b)! with { Centre = new Vec3(0.3f, 0.3f, 0.3f) };
        var log = new List<string>();
        var (plan, report) = Fill(b, far, log: log);
        Assert.Null(plan);
        Assert.Equal(BodyBridge.NavelOutcome.NotOnThisMesh, report.Outcome);
        Assert.Empty(log);
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-12")]
    public void A_uv_seam_through_the_navel_moves_both_copies_together()
    {
        var b = Belly(Pit(), seam: true);
        var (view, report) = Fill(b, Locate(b)!);
        Assert.Equal(BodyBridge.NavelOutcome.Filled, report.Outcome);
        var byPlace = new Dictionary<(int, int, int), Vec3>();
        int shared = 0;
        for (int i = 0; i < b.Pos.Length; i++)
        {
            var key = ((int)MathF.Round(b.Pos[i].X * 1e5f), (int)MathF.Round(b.Pos[i].Y * 1e5f), (int)MathF.Round(b.Pos[i].Z * 1e5f));
            var d = view!.Plan.Delta[i];
            if (byPlace.TryGetValue(key, out var other)) { Assert.Equal(other, d); if (Len(d) > 0) shared++; }
            else byPlace[key] = d;
        }
        Assert.True(shared > 0, "no seam copy moved");
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-13")]
    public void Half_strength_fills_half_way_and_stays_valid()
    {
        var b = Belly(Pit());
        var site = Locate(b)!;
        var (_, report) = Fill(b, site, strength: 0.5f);
        Assert.Equal(BodyBridge.NavelOutcome.Filled, report.Outcome);
        Assert.Equal(0, report.InwardTriangles);
        float share = report.DepthAfter / report.DepthBefore;
        Assert.InRange(share, 0.3f, 0.7f);
        Assert.Null(Fill(b, site, strength: 0f).Plan);
    }

    [Theory]
    [Trait("Plan", "EDGE-navel-bridge-14")]
    [InlineData(0.5f)]
    [InlineData(1.4f)]
    public void Found_and_filled_at_every_body_scale(float scale)
    {
        var b = Belly((x, y) => Pit(0.005f * scale, 0.003f * scale, 0f, NavelY)(x, y), scale: scale);
        var site = Locate(b);
        Assert.NotNull(site);
        Assert.Equal(BodyBridge.NavelOutcome.Filled, Fill(b, site!).Report.Outcome);
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-17")]
    public void The_navel_is_left_out_where_another_span_moved_it()
    {
        int n = 3;
        BodyBridge.BustBridgePlan Plan(params int[] moved)
        {
            var d = new Vec3[n];
            foreach (int i in moved) d[i] = new Vec3(0, 0, 0.001f);
            return new BodyBridge.BustBridgePlan
            {
                Delta = d, NodeOf = [0, 1, 2], NodeWeight = new float[n], NodeNormal = new Vec3[n],
            };
        }
        var log = new List<string>();
        var others = Plan(0);
        Assert.Same(others, BodyBridge.MergeNavel(others, Plan(0, 1), log.Add));
        Assert.Contains(log, l => l.StartsWith("navel fill:"));
        var merged = BodyBridge.MergeNavel(others, Plan(2), log.Add)!;
        Assert.Equal(0.001f, merged.Delta[0].Z);
        Assert.Equal(0.001f, merged.Delta[2].Z);
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-18")]
    public void The_fill_finds_the_pit_again_where_a_shape_moved_it()
    {
        var b = Belly(Pit());
        var site = Locate(b)!;
        var shifted = Belly(Pit(x0: 0.003f));
        var (_, report) = Fill(shifted, site);
        Assert.Equal(BodyBridge.NavelOutcome.Filled, report.Outcome);
        Assert.True(report.DepthAfter <= 0.0005f, $"{report.DepthAfter * 1000:0.##}mm left");
    }

    /// <summary>The navel's nodes take none of the extra push a bridge gets, and pass none on; others are untouched.</summary>
    [Fact]
    [Trait("Plan", "AC-navel-bridge-10")]
    public void The_navel_gets_no_extra_clearance()
    {
        // A strip of four nodes: 0 and 1 moved by a millimetre, 1 the navel's.
        ushort[] tris = [0, 1, 2, 1, 3, 2];
        BodyBridge.BustBridgePlan Plan(bool[]? exempt) => new()
        {
            Delta = [new Vec3(0, 0, 0.001f), new Vec3(0, 0, 0.001f), default, default],
            NodeOf = [0, 1, 2, 3], NodeWeight = [1, 1, 0, 0], NodeNormal = new Vec3[4], NoExtraClearance = exempt,
        };
        var all = SecondSkinWriter.BridgedClearance(Plan(null), tris);
        Assert.All(all, e => Assert.True(e > 0f));
        var navel = SecondSkinWriter.BridgedClearance(Plan([false, true, false, false]), tris);
        Assert.Equal(all[0], navel[0]);
        Assert.Equal(0f, navel[1]);
        // Node 3 touches only the navel's node and node 2, which gets its spread from node 0.
        Assert.True(navel[3] < all[3]);
    }

    // ── the tick and the writer ───────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-navel-bridge-1")]
    public void The_navel_tick_sits_between_the_nipple_and_the_buttocks()
    {
        var ids = GeometryToggles.All.Select(t => t.Id).ToList();
        Assert.Equal(["bustbridge", "smoothnipples", "navelbridge", "cleftbridge", "smoothfold"], ids);
        var t = GeometryToggles.All.Single(x => x.Id == "navelbridge");
        var cs = new Proteus.Localization.ColorsStrings();
        Assert.Equal(cs.NavelBridge, t.Label(cs));
        Assert.Equal(cs.NavelBridgeTip, t.Tip(cs));
        Assert.Equal("navel-bridge", t.Reason);
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-2")]
    public void Ticking_saves_on_and_clears_the_strength()
    {
        var t = GeometryToggles.All.Single(x => x.Id == "navelbridge");
        var md = new ProteusMetadata { NavelBridgeStrength = 0.4f };
        t.Set(md, true);
        Assert.True(md.NavelBridge);
        Assert.Null(md.NavelBridgeStrength);
        var json = JsonSerializer.Serialize(md, ProteusJson.MetadataWrite);
        Assert.Contains("\"NavelBridge\": true", json);
        Assert.DoesNotContain("NavelBridgeStrength", json);
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-2")]
    [Trait("Plan", "EDGE-navel-bridge-19")]
    public void Unticking_removes_the_key_and_absent_is_off()
    {
        var t = GeometryToggles.All.Single(x => x.Id == "navelbridge");
        var md = new ProteusMetadata { NavelBridge = true };
        t.Set(md, false);
        Assert.Null(md.NavelBridge);
        Assert.DoesNotContain("NavelBridge", JsonSerializer.Serialize(md, ProteusJson.MetadataWrite));

        var empty = JsonSerializer.Deserialize<ProteusMetadata>("{}", ProteusJson.MetadataRead)!;
        Assert.False(t.IsOn(empty));
        Assert.False(RenderModeInference.WantsGeometry(empty));
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-2")]
    public void The_sidecar_round_trips()
    {
        var md = JsonSerializer.Deserialize<ProteusMetadata>("{\"navelbridge\": true, \"NavelBridgeStrength\": 0.25}",
                                                             ProteusJson.MetadataRead)!;
        Assert.True(md.NavelBridge);
        Assert.Equal(0.25f, md.NavelBridgeStrength);
        Assert.Equal(0.25f, SecondSkinService.GeometryStrength(md.NavelBridge, md.NavelBridgeStrength));
    }

    [Fact]
    [Trait("Plan", "AC-navel-bridge-3")]
    public void The_navel_tick_alone_promotes_to_a_shell()
    {
        var md = new ProteusMetadata();
        GeometryToggles.All.Single(x => x.Id == "navelbridge").Set(md, true);
        Assert.True(RenderModeInference.WantsGeometry(md));
    }

    private const string Skin = "/mt_c0201b0001_a.mtrl";

    [Fact]
    [Trait("Plan", "AC-navel-bridge-7")]
    public void A_build_with_the_tick_runs_the_navel_pass_and_says_so()
    {
        var model = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 8)));
        var on = new List<string>();
        SecondSkinWriter.Build([model], [new SecondSkinLayer { MaterialName = Skin, NavelBridgeStrength = 1f }],
                               null, false, out _, diag: on.Add);
        Assert.Contains(on, l => l.StartsWith("navel fill: no navel found"));

        var off = new List<string>();
        SecondSkinWriter.Build([model], [new SecondSkinLayer { MaterialName = Skin }], null, false, out _, diag: off.Add);
        Assert.DoesNotContain(off, l => l.Contains("navel"));
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-20")]
    public void Skin_alone_is_searched()
    {
        var skinOnly = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 4)));
        var withPiercing = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 4)),
            new SyntheticModel.Mesh(SyntheticModel.PackPieceVanillaMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 6)));
        Assert.True(TryReadLod0Geometry(skinOnly, out var a, out _, out _, out _, out _, keepMaterial: IsBodySkinMaterial));
        Assert.True(TryReadLod0Geometry(withPiercing, out var b, out _, out _, out _, out _, keepMaterial: IsBodySkinMaterial));
        Assert.Equal(a.Length, b.Length);
    }
}
