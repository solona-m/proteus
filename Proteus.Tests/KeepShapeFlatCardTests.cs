using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.KeepShapeReleaseTests;

namespace Proteus.Tests;

/// <summary>
/// A flat card kept whole: the double-sided ornament card the game's gear is full of, 8 vertices on 4 points in one
/// plane. The polar turn fit diverges on it, and before build 1365 the scale fitted to that broken turn threw Virtu
/// Dancer's cards metres off Tre. Over <see cref="KeepShapeReleaseTests"/>' box, whose +Z face grows a dome.
/// </summary>
public class KeepShapeFlatCardTests
{
    private const string Cloth = "/mt_c0201e6255_top_a.mtrl";

    /// <summary>
    /// A tilt that makes the polar fit diverge on <see cref="Card"/> in every arrangement below — over the dome with and
    /// without the push-out, and beside a plate. Whether it diverges turns on float noise,
    /// so a pinned angle is fragile; the first of a fixed run of tilts that does (seed 7: most do) is taken instead, and
    /// the tests assert it still does (<see cref="BodyRetarget.Solved.ShapeFallbacks"/>).
    /// </summary>
    private static readonly Quaternion Tilt = FirstDivergingTilt();

    private static Quaternion FirstDivergingTilt()
    {
        var rnd = new Random(7);
        var plate = Plate(new Vector3(-0.08f, 0f, 0f), 0.03f, 0.001f);
        int np = plate.Positions.Length / 3;
        for (int t = 0; t < 40; t++)
        {
            var axis = Vector3.Normalize(new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f,
                                                     (float)rnd.NextDouble() - 0.5f));
            var tilt = Quaternion.CreateFromAxisAngle(axis, 0.2f + (float)rnd.NextDouble() * 0.6f);
            var card = Card(tilt);
            var all = AllVertices(card);
            if (Solve(card, TallDome, [all]).ShapeFallbacks > 0
                && Solve(card, TallDome, [all], pushOut: false).ShapeFallbacks > 0
                && Solve(Merge(plate, card), TallDome,
                         [Enumerable.Range(0, np).ToArray(), Enumerable.Range(np, all.Length).ToArray()]).ShapeFallbacks > 0)
                return tilt;
        }
        throw new InvalidOperationException("no tilt made the polar fit diverge — the fixture exercises nothing");
    }

    /// <summary>
    /// A flat sheet 6 cm across lying low over the dome's top, as <see cref="KeepShapeReleaseTests"/>' plate does but with
    /// no thickness: a 5 mm grid, both faces at the same points, all in one plane — so the polar fit diverges on it as on a
    /// card, and it has points across the dome for the release to measure (a card's four corners all lie past it). Moved
    /// whole it sinks into the dome that grows under it, so it is let go.
    /// </summary>
    private static ModelParts BigCard(Quaternion tilt)
    {
        const float size = 0.06f, step = 0.005f;
        int n = (int)MathF.Round(size / step), layer = (n + 1) * (n + 1);
        var centre = new Vector3(0f, 0f, KeepShapeReleaseTests.Half + 0.002f);
        var up = Vector3.Transform(Vector3.UnitZ, tilt);
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        for (int side = 0; side < 2; side++)
            for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                var p = centre + Vector3.Transform(new Vector3(-size / 2 + i * step, -size / 2 + j * step, 0f), tilt);
                pos.AddRange([p.X, p.Y, p.Z]);
                var normal = side == 0 ? up : -up;
                nrm.AddRange([normal.X, normal.Y, normal.Z]);
            }
        for (int side = 0; side < 2; side++)
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            int b = side * layer;
            int p00 = b + i * (n + 1) + j, p10 = b + (i + 1) * (n + 1) + j, p01 = p00 + 1, p11 = p10 + 1;
            if (side == 0) tris.AddRange([p00, p10, p11, p00, p11, p01]);
            else tris.AddRange([p00, p11, p10, p00, p01, p11]);
        }
        return Build([.. pos], [.. nrm], [.. tris], Cloth);
    }

    /// <summary>A tilt gentle enough for <see cref="BigCard"/> to lie over the face (under 0.06 rad: its edge drops under 2 mm) that makes the polar fit
    /// diverge on it — the first of a fixed run, as <see cref="Tilt"/>.</summary>
    private static readonly Quaternion BigTilt = FirstDivergingBigTilt();

    private static Quaternion FirstDivergingBigTilt()
    {
        var rnd = new Random(7);
        var noRelease = new BodyRetarget.Tuning(NoRelease: true);
        for (int t = 0; t < 40; t++)
        {
            var axis = Vector3.Normalize(new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f,
                                                     (float)rnd.NextDouble() - 0.5f));
            var tilt = Quaternion.CreateFromAxisAngle(axis, 0.02f + (float)rnd.NextDouble() * 0.04f);
            var big = BigCard(tilt);
            if (BodyRetarget.WithTuning(noRelease, () => Solve(big, TallDome, [AllVertices(big)])).ShapeFallbacks > 0)
                return tilt;
        }
        throw new InvalidOperationException("no gentle tilt made the polar fit diverge — the fixture exercises nothing");
    }

    /// <summary>
    /// A square card <paramref name="size"/> across centred at (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>), turned by <paramref name="tilt"/>: its four corners twice over, one face each way, so the
    /// solve welds it to four points as it does the game's.
    /// </summary>
    private static ModelParts Card(Quaternion tilt, float x = 0.02f, float y = 0f, float z = KeepShapeReleaseTests.Half + 0.005f, float size = 0.0158f)
    {
        var centre = new Vector3(x, y, z);
        var pos = new List<float>();
        var nrm = new List<float>();
        var up = Vector3.Transform(Vector3.UnitZ, tilt);
        for (int side = 0; side < 2; side++)
            for (int i = 0; i < 4; i++)
            {
                var corner = new Vector3(i % 2 == 0 ? -size / 2 : size / 2, i < 2 ? -size / 2 : size / 2, 0f);
                var p = centre + Vector3.Transform(corner, tilt);
                pos.AddRange([p.X, p.Y, p.Z]);
                var n = side == 0 ? up : -up;
                nrm.AddRange([n.X, n.Y, n.Z]);
            }
        int[] tris = [0, 1, 3, 0, 3, 2, 4, 7, 5, 4, 6, 7];
        return Build([.. pos], [.. nrm], tris, Cloth);
    }

    private static float WorstMove(ModelParts garment, BodyRetarget.Solved solved, IEnumerable<int>? only = null)
    {
        float worst = 0f;
        foreach (int v in only ?? AllVertices(garment))
        {
            var d = solved.Edit.DeltaAt(v);
            Assert.True(float.IsFinite(d.X) && float.IsFinite(d.Y) && float.IsFinite(d.Z), $"vertex {v} moved to no number");
            worst = MathF.Max(worst, new Vector3(d.X, d.Y, d.Z).Length());
        }
        return worst;
    }

    /// <summary>The uniform scale a piece moved whole was given: its first and last points' distance, after over before.</summary>
    private static float ScaleOf(ModelParts garment, BodyRetarget.Solved solved, int a, int b)
    {
        Vector3 Before(int v) => new(garment.Positions[v * 3], garment.Positions[v * 3 + 1], garment.Positions[v * 3 + 2]);
        Vector3 After(int v)
        {
            var d = solved.Edit.DeltaAt(v);
            return Before(v) + new Vector3(d.X, d.Y, d.Z);
        }
        return Vector3.Distance(After(a), After(b)) / Vector3.Distance(Before(a), Before(b));
    }

    private static readonly BodyRetarget.Tuning Old = new(NoTurnFallback: true);

    [Fact]
    [Trait("Plan", "AC-keep-shape-flat-card-2")]
    [Trait("Plan", "EDGE-keep-shape-flat-card-2")]
    public void A_tilted_flat_card_is_moved_whole_at_body_scale()
    {
        var card = Card(Tilt);
        var all = AllVertices(card);

        var solved = Solve(card, TallDome, [all]);

        Assert.True(solved.ShapeFallbacks >= 1, "the fixture no longer exercises the fallback turn — pick another tilt");
        float worst = WorstMove(card, solved);
        Assert.True(worst <= 0.030f, $"the card moved {worst * 1000:F1} mm");
        AssertMovedWhole(card, solved, all);
        float scale = ScaleOf(card, solved, 0, 3);
        Assert.InRange(scale, 0.5f, 2f);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-3")]
    public void An_axis_aligned_flat_card_is_unchanged()
    {
        // At a height a float holds exactly (13/64 m), so the four corners' centre is exact and the cross-covariance has
        // an exactly zero row: the polar fit's own identity, as before.
        var card = Card(Quaternion.Identity, z: 0.203125f);
        var all = AllVertices(card);

        var solved = Solve(card, TallDome, [all]);
        var old = BodyRetarget.WithTuning(Old, () => Solve(card, TallDome, [all]));

        Assert.Equal(0, solved.ShapeFallbacks);
        AssertSameDeltas(old, solved, card);
        Assert.True(WorstMove(card, solved) <= 0.030f);
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-flat-card-3")]
    [Trait("Plan", "EDGE-keep-shape-flat-card-9")]
    public void A_solid_piece_beside_a_flat_card_is_as_before()
    {
        var plate = Plate(new Vector3(-0.08f, 0f, 0f), 0.03f, 0.001f);
        var card = Card(Tilt);
        var garment = Merge(plate, card);
        int np = plate.Positions.Length / 3;
        var platePiece = Enumerable.Range(0, np).ToArray();
        var cardPiece = Enumerable.Range(np, card.Positions.Length / 3).ToArray();
        IReadOnlyCollection<int>[] keep = [platePiece, cardPiece];

        var solved = Solve(garment, TallDome, keep);
        var old = BodyRetarget.WithTuning(Old, () => Solve(garment, TallDome, keep));

        Assert.True(solved.ShapeFallbacks >= 1, "the fixture no longer exercises the fallback turn");
        foreach (int v in platePiece)
        {
            var e = old.Edit.DeltaAt(v);
            var a = solved.Edit.DeltaAt(v);
            Assert.True(e.X == a.X && e.Y == a.Y && e.Z == a.Z, $"plate vertex {v} moved otherwise");
        }
        AssertMovedWhole(garment, solved, platePiece);
        AssertMovedWhole(garment, solved, cardPiece);
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-flat-card-3")]
    [Trait("Speed", "Slow")]
    public void Well_posed_pieces_are_bit_identical()
    {
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);
        IReadOnlyCollection<int>[] keep = [AllVertices(plate)];
        var noRelease = new BodyRetarget.Tuning(NoRelease: true);
        var noReleaseOld = new BodyRetarget.Tuning(NoRelease: true, NoTurnFallback: true);

        var low = Solve(plate, LowDome, keep);
        var tall = BodyRetarget.WithTuning(noRelease, () => Solve(plate, TallDome, keep));

        Assert.Equal(0, low.ShapeFallbacks);
        Assert.Equal(0, tall.ShapeFallbacks);
        AssertSameDeltas(BodyRetarget.WithTuning(Old, () => Solve(plate, LowDome, keep)), low, plate);
        AssertSameDeltas(BodyRetarget.WithTuning(noReleaseOld, () => Solve(plate, TallDome, keep)), tall, plate);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-10")]
    public void A_flat_card_without_push_out_is_turned_too()
    {
        var card = Card(Tilt);
        var all = AllVertices(card);

        var solved = Solve(card, TallDome, [all], pushOut: false);

        Assert.True(solved.ShapeFallbacks >= 1, "the fixture no longer exercises the fallback turn");
        Assert.False(Assert.Single(solved.Shapes!).Judged);
        Assert.True(WorstMove(card, solved) <= 0.030f);
        AssertMovedWhole(card, solved, all);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-11")]
    public void A_fallback_turned_piece_is_released_like_any_other()
    {
        var card = BigCard(BigTilt);
        var all = AllVertices(card);

        var solved = Solve(card, TallDome, [all]);
        var whole = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelease: true), () => Solve(card, TallDome, [all]));

        Assert.True(whole.ShapeFallbacks >= 1, "the fixture no longer exercises the fallback turn");
        var outcome = Assert.Single(solved.Shapes!);
        Assert.True(outcome.Released, $"the card sinks {outcome.Sink * 1000:F2} mm moved whole (judged {outcome.Judged}) and should have been let go");
        AssertSameDeltas(Solve(card, TallDome, []), solved, card);
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-flat-card-4")]
    [Trait("Speed", "Slow")]
    public void The_fallback_count_is_reported()
    {
        var card = Card(Tilt);
        var plate = Plate(Vector3.Zero, 0.06f, 0.001f);

        Assert.True(Solve(card, TallDome, [AllVertices(card)]).ShapeFallbacks >= 1);
        Assert.Equal(0, Solve(plate, LowDome, [AllVertices(plate)]).ShapeFallbacks);
        Assert.Equal(0, Solve(card, TallDome, []).ShapeFallbacks);
    }
}
