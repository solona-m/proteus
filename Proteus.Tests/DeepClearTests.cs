using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Tests.SkinTuckSheet;

namespace Proteus.Tests;

/// <summary>
/// A game top's cloth cleared out of a far larger new breast from deeper than the usual 8 mm (see
/// <see cref="BodyRetarget.DeepClearDepth"/>), on <see cref="SkinTuckSheet"/>: the top's cloth authored 12 mm inside a
/// flat breast-rigged body, refitted onto the same body grown a ridge (rows 3-8) up to 20 mm high. The ridge's rows 5 and
/// 6 grew 19 mm, past <see cref="BodyRetarget.DeepGrowLeast"/>; rows 4 and 7 grew 12 mm, short of it.
/// </summary>
public class DeepClearTests(ITestOutputHelper output)
{
    private static readonly BodyRetarget.Tuning NoDeep = new(NoDeepClear: true);

    private BodyRetarget.Planned GameTop(byte[] top, byte[] target, bool across = true,
                                         IReadOnlyList<IReadOnlyCollection<int>>? keepShape = null, byte[]? source = null)
    {
        var garment = ModelPartReader.Read(top)!;
        var pair = Grown(source ?? Bust(0f), target);
        var plan = keepShape == null
            ? RefitCore.PlanRefit(garment, top, [pair], "_top", across: across, gameGear: true, out _)
            : BodyRetarget.Plan(garment, top, [pair], "_top", held: new HashSet<int>(), replaceSkin: across,
                                acrossBodies: across, clearBody: true, cutHidden: true, keepShape: keepShape,
                                tuckSlot: "_top", deepClear: true);
        output.WriteLine($"deep cleared {plan.Report.DeepCleared}, up to {plan.Report.DeepWorst * 1000:0.0} mm; pushed "
                         + $"{plan.Report.Pushed}, folded {plan.Report.Folded}");
        return plan;
    }

    /// <summary>Each cloth vertex of the laid garment in the rows given, and how far outside the target body it is
    /// (negative inside), by this test's own nearest point.</summary>
    private static List<(int Vertex, float Off)> ClothOff(ModelParts laid, byte[] target, Func<float, bool> row)
    {
        var body = ModelPartReader.Read(target)!;
        var surface = new BodySurface(body, 0.01f);
        var verts = laid.Parts.Where(p => p.Island < 0 && p.Material == Cloth).SelectMany(p => p.Triangles).Distinct();
        var off = new List<(int, float)>();
        foreach (int v in verts)
        {
            var p = new Vector3(laid.Positions[v * 3], laid.Positions[v * 3 + 1], laid.Positions[v * 3 + 2]);
            if (!row((p.Y - Bottom) / Step) || !surface.Nearest(p, 0.05f, out var hit)) continue;
            off.Add((v, Vector3.Dot(p - hit.Point, hit.Normal)));
        }
        return off;
    }

    private static bool GrownRows(float row) => row is > 4.5f and < 6.5f;

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-7")]
    public void Breast_cloth_buried_past_the_clear_comes_out_where_the_bust_grew()
    {
        var top = CoarseBuriedTop(0.012f);
        var target = Bust(0.02f);
        var before = BodyRetarget.WithTuning(NoDeep, () => GameTop(top, target));
        var buried = ClothOff(Laid(top, before), target, GrownRows);
        Assert.Contains(buried, c => c.Off < -BodyRetarget.ClearDepth);   // the fixture is the case

        var plan = GameTop(top, target);
        Assert.True(plan.Report.DeepCleared > 0);
        var cleared = ClothOff(Laid(top, plan), target, GrownRows);
        Assert.All(cleared, c => Assert.True(c.Off >= 0.0005f, $"cloth {c.Vertex} still {-c.Off * 1000:0.0} mm inside"));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-1")]
    public void Nothing_comes_out_where_the_bust_did_not_grow()
        => Assert.Equal(0, GameTop(CoarseBuriedTop(0.012f), Bust(0.003f)).Report.DeepCleared);

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-2")]
    [Trait("Plan", "EDGE-neckline-large-bust-7")]
    public void No_deep_push_starts_off_the_breast()
    {
        // The same ridge rigged to the spine: no breast, so no deep push — and no breast bones at all, so none can start.
        var spine = Bust(0.02f, breast: _ => false);
        Assert.Equal(0, GameTop(CoarseBuriedTop(0.012f), spine, source: Bust(0f, breast: _ => false)).Report.DeepCleared);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-3")]
    public void Cloth_deeper_than_the_probe_stays()
        => Assert.Equal(0, GameTop(CoarseBuriedTop(0.035f), Bust(0.02f)).Report.DeepCleared);

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-4")]
    [Trait("Plan", "AC-neckline-large-bust-4")]
    public void The_deep_clear_turns_nothing_over_and_stretches_nothing()
    {
        // The ridge's flank turns more than 30° from its top: neighbouring cloth is pushed along skin facing different
        // ways, where the push-out's fold guard has work to do.
        var target = Bust(0.02f);
        var body = ModelPartReader.Read(target)!;
        Vector3 NormalAtRow(int row) => new(body.Normals[(row * Columns + 4) * 3], body.Normals[(row * Columns + 4) * 3 + 1],
                                            body.Normals[(row * Columns + 4) * 3 + 2]);
        Assert.True(Vector3.Dot(NormalAtRow(3), NormalAtRow(7)) < MathF.Cos(MathF.PI / 6), "the ridge is too gentle");

        var top = CoarseBuriedTop(0.012f);
        var before = BodyRetarget.WithTuning(NoDeep, () => GameTop(top, target));
        var plan = GameTop(top, target);
        Assert.True(plan.Report.DeepCleared > 0);
        Assert.True(plan.Report.Folded <= before.Report.Folded, $"folded {plan.Report.Folded} against {before.Report.Folded}");
        Assert.Equal(0, ClothStretch(Laid(top, before), Laid(top, plan)));
        Assert.NotNull(ModelPartReader.Read(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-5")]
    public void Two_buried_layers_come_out_in_order()
    {
        // A shell 6 mm in, and a lining 10 mm in behind it over the ridge — 4 mm apart, past WallPairReach, so the two
        // are layers and not one thick shell's walls. A 35 mm ridge: a lining lands on the ridge's flank, and the 20 mm
        // one's flank grew short of DeepGrowLeast (as the real buried cloth's did not — 36 mm and more).
        var top = CoarseBuriedTop(0.006f, lining: 0.004f);
        var target = Bust(0.035f);
        var plan = GameTop(top, target);
        Assert.True(plan.Report.DeepCleared > 0);
        var laid = Laid(top, plan);
        var off = ClothOff(laid, target, GrownRows);
        Assert.All(off, c => Assert.True(c.Off > 0f, $"cloth {c.Vertex} still {-c.Off * 1000:0.0} mm inside"));

        // The lining: the second cloth mesh. A +Z ray from each of its vertices over the grown rows meets the shell.
        var meshes = laid.Parts.Where(p => p.Island < 0 && p.Material == Cloth).GroupBy(p => p.Mesh).OrderBy(g => g.Key).ToList();
        Assert.Equal(2, meshes.Count);
        var shell = meshes[0].SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                        .Select(t => (Pos(laid, p.Triangles[t * 3]), Pos(laid, p.Triangles[t * 3 + 1]),
                                                                      Pos(laid, p.Triangles[t * 3 + 2])))).ToList();
        foreach (int v in meshes[1].SelectMany(p => p.Triangles).Distinct())
        {
            var p = Pos(laid, v);
            if (!GrownRows((p.Y - Bottom) / Step) || MathF.Abs(p.X) > X(Columns - 1) - 0.001f) continue;
            Assert.True(shell.Any(t => Up(p, t)), $"lining vertex {v} is not behind the shell");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-6")]
    public void A_hard_piece_in_the_deep_region_is_moved_whole()
    {
        // A patch of the cloth over the grown rows kept in shape: moved as one, or let go if moving it whole sinks it.
        var top = CoarseBuriedTop(0.012f);
        var garment = ModelPartReader.Read(top)!;
        var piece = Enumerable.Range(0, garment.Positions.Length / 3)
                              .Where(v => garment.Parts.Any(p => p.Island < 0 && p.Material == Cloth && p.Triangles.Contains(v)))
                              .Where(v => GrownRows((garment.Positions[v * 3 + 1] - Bottom) / Step))
                              .ToList();
        Assert.True(piece.Count >= 2);
        var plan = GameTop(top, Bust(0.02f), keepShape: [piece]);
        var shape = Assert.Single(plan.Report.Shapes!);
        if (shape.Released) return;
        // Whole: every distance within the piece scaled by one factor (a kept piece may grow with the body).
        var laid = Laid(top, plan);
        var pairs = piece.SelectMany(a => piece.Where(b => b > a).Select(b => (a, b))).ToList();
        float scale = pairs.Select(e => Vector3.Distance(Pos(laid, e.a), Pos(laid, e.b))
                                        / Vector3.Distance(Pos(garment, e.a), Pos(garment, e.b))).Order().ElementAt(pairs.Count / 2);
        foreach (var (a, b) in pairs)
        {
            float was = Vector3.Distance(Pos(garment, a), Pos(garment, b)) * scale;
            float now = Vector3.Distance(Pos(laid, a), Pos(laid, b));
            Assert.True(MathF.Abs(now - was) <= 0.0001f, $"piece edge {a}-{b} {was * 1000:0.0} -> {now * 1000:0.0} mm");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-7")]
    public void No_deep_clear_when_the_chests_weights_cannot_be_read()
    {
        // The deep clear asks the new chest's own weights where its breast is; a file they cannot be read from clears
        // nothing deep (and the tuck says why, as (a)'s No_tuck_when_the_bodys_weights_cannot_be_read pins).
        var pair = Grown(Bust(0f), Bust(0.02f));
        Assert.NotNull(BodyRetarget.DeepClearTest([pair], "_top"));
        Assert.Null(BodyRetarget.DeepClearTest([pair with { TargetModel = new byte[64] }], "_top"));
        Assert.Null(BodyRetarget.DeepClearTest([pair], "_dwn"));
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-8")]
    public void No_deep_clear_within_one_body()
    {
        var plan = GameTop(CoarseBuriedTop(0.012f), Bust(0.02f), across: false);
        Assert.Equal(0, plan.Report.DeepCleared);
        Assert.Null(plan.Report.Swap);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-23")]
    public void Cloth_spanning_the_cleavage_is_not_driven_into_it()
    {
        // Two breasts with the cleavage between; the cloth spans it. Out of the breasts, and not into the valley.
        var top = CoarseBuriedTop(0.012f);
        var target = Busts(0.02f);
        var before = BodyRetarget.WithTuning(NoDeep, () => GameTop(top, target));
        var plan = GameTop(top, target);
        Assert.True(plan.Report.Folded <= before.Report.Folded, $"folded {plan.Report.Folded} against {before.Report.Folded}");
        Assert.Equal(0, ClothStretch(Laid(top, before), Laid(top, plan)));

        var valleyBefore = ClothOff(Laid(top, before), target, GrownRows)
                               .Where(c => MathF.Abs(Laid(top, before).Positions[c.Vertex * 3] - X(4)) < 0.001f)
                               .ToDictionary(c => c.Vertex, c => c.Off);
        var after = Laid(top, plan);
        foreach (var (v, off) in ClothOff(after, target, GrownRows))
            if (valleyBefore.TryGetValue(v, out float was))
                Assert.True(off >= was - 0.0005f, $"valley cloth {v} driven in: {was * 1000:0.0} -> {off * 1000:0.0} mm");
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-19")]
    public void The_deep_clear_is_deterministic()
        => Assert.Equal(GameTop(CoarseBuriedTop(0.012f), Bust(0.02f)).Model, GameTop(CoarseBuriedTop(0.012f), Bust(0.02f)).Model);

    private static Vector3 Pos(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>Whether the triangle lies straight up (+Z) from <paramref name="p"/>.</summary>
    private static bool Up(Vector3 p, (Vector3 A, Vector3 B, Vector3 C) t)
    {
        var e1 = t.B - t.A; var e2 = t.C - t.A;
        var h = Vector3.Cross(Vector3.UnitZ, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-12f) return false;
        var s = p - t.A;
        float u = Vector3.Dot(s, h) / det;
        if (u < -1e-4f || u > 1f + 1e-4f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(Vector3.UnitZ, q) / det;
        if (v < -1e-4f || u + v > 1f + 1e-4f) return false;
        return Vector3.Dot(e2, q) / det > 0f;
    }
}
