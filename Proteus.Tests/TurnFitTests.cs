using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// The turn a kept piece is moved whole by (<see cref="TurnFit"/>, <see cref="BodyRetarget.TurnOf"/>): the polar answer
/// where it is a rotation, Horn's quaternion fit where it is not — a flat card's, most often.
/// </summary>
public class TurnFitTests
{
    /// <summary>A 15.8 mm square card's four corners, as Virtu Dancer's cards are, tilted by <paramref name="tilt"/> and
    /// standing at body height.</summary>
    internal static Vec3[] Card(Quaternion tilt, float half = 0.0079f)
    {
        var centre = new Vector3(0.05f, 0.2f, 0.21f);
        return Enumerable.Range(0, 4).Select(i =>
        {
            var corner = new Vector3(i % 2 == 0 ? -half : half, i < 2 ? -half : half, 0f);
            return V(centre + Vector3.Transform(corner, tilt));
        }).ToArray();
    }

    private static Vec3 V(Vector3 p) => new(p.X, p.Y, p.Z);
    private static Vector3 P(Vec3 p) => new(p.X, p.Y, p.Z);
    private static Vector3 Centre(IReadOnlyList<Vec3> ps) => ps.Aggregate(Vector3.Zero, (s, p) => s + P(p)) / ps.Count;

    private static Vector3 Apply(float[] r, Vector3 q)
        => new(r[0] * q.X + r[1] * q.Y + r[2] * q.Z, r[3] * q.X + r[4] * q.Y + r[5] * q.Z, r[6] * q.X + r[7] * q.Y + r[8] * q.Z);

    private static Quaternion RandomTurn(Random rnd)
    {
        var axis = Vector3.Normalize(new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f,
                                                 (float)rnd.NextDouble() - 0.5f));
        return Quaternion.CreateFromAxisAngle(axis, (float)rnd.NextDouble() * 3f);
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-flat-card-2")]
    [Trait("Plan", "EDGE-keep-shape-flat-card-1")]
    public void Horn_turns_every_tilted_flat_card_where_BestRotation_diverges()
    {
        var rnd = new Random(1365);
        int diverged = 0;
        for (int t = 0; t < 200; t++)
        {
            var from = Card(RandomTurn(rnd));
            var turn = RandomTurn(rnd);
            var cFrom = Centre(from);
            var shift = new Vector3(0.01f, -0.02f, 0.005f);
            // The card turned and shifted, then bent a little — at most half a millimetre, as a refit bends it.
            var to = from.Select((p, i) => V(cFrom + shift + Vector3.Transform(P(p) - cFrom, turn)
                                             + new Vector3(0.0003f * (i % 2), 0.0002f * (i / 2), -0.0001f * i))).ToArray();
            var cTo = Centre(to);

            if (!TurnFit.IsTurn(SecondSkinWriter.BestRotation(from, to, V(cFrom), V(cTo)))) diverged++;
            Assert.True(TurnFit.IsTurn(TurnFit.HornTurn(from, to, cFrom, cTo)), $"tilt {t}: Horn gave no rotation");

            // Unbent, the card's own turn comes back corner for corner.
            var exact = from.Select(p => V(cFrom + shift + Vector3.Transform(P(p) - cFrom, turn))).ToArray();
            var r = TurnFit.HornTurn(from, exact, cFrom, Centre(exact));
            for (int i = 0; i < 4; i++)
            {
                var got = Centre(exact) + Apply(r, P(from[i]) - cFrom);
                Assert.True(Vector3.Distance(got, P(exact[i])) < 1e-5f,
                            $"tilt {t}: corner {i} {Vector3.Distance(got, P(exact[i])) * 1000:F4} mm off");
            }
        }
        // The fixture is the failure: without it this proves nothing (152 of 200 when measured).
        Assert.True(diverged >= 100, $"BestRotation diverged on only {diverged} of 200 cards");
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-4")]
    public void A_mirrored_set_is_turned_not_reflected()
    {
        Vec3[] from = [new(0.01f, 0, 0), new(0, 0.02f, 0), new(0, 0, 0.03f), new(-0.01f, -0.01f, -0.01f)];
        var to = from.Select(p => new Vec3(-p.X, p.Y, p.Z)).ToArray();   // a mirror: no rotation carries one onto the other
        var cFrom = Centre(from);
        var cTo = Centre(to);

        Assert.False(TurnFit.IsTurn(SecondSkinWriter.BestRotation(from, to, V(cFrom), V(cTo))));
        var r = BodyRetarget.TurnOf(from, to, cFrom, cTo, out bool fellBack);
        Assert.True(fellBack);
        Assert.True(TurnFit.IsTurn(r));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-5")]
    public void Non_finite_points_give_identity()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity })
        {
            Vec3[] from = [new(0, 0, 0), new(0.01f, 0, 0), new(0, 0.01f, bad)];
            Vec3[] to = [new(0, 0, 0), new(0, 0.01f, 0), new(-0.01f, 0, 0)];
            var r = TurnFit.HornTurn(from, to, Vector3.Zero, Vector3.Zero);
            Assert.All(r, x => Assert.True(float.IsFinite(x)));
            Assert.Equal([1f, 0, 0, 0, 1f, 0, 0, 0, 1f], r);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-6")]
    public void A_collinear_set_maps_its_line()
    {
        var dir = Vector3.Normalize(new Vector3(1, 2, 0.5f));
        var target = Vector3.Normalize(new Vector3(-0.3f, 0.4f, 1f));
        var from = Enumerable.Range(0, 5).Select(i => V(dir * (i * 0.004f))).ToArray();
        var to = Enumerable.Range(0, 5).Select(i => V(new Vector3(0.1f, 0, 0) + target * (i * 0.004f))).ToArray();

        var r = TurnFit.HornTurn(from, to, Centre(from), Centre(to));

        Assert.True(TurnFit.IsTurn(r));
        Assert.True(Vector3.Dot(Apply(r, dir), target) > 1f - 1e-5f, "the line was not turned onto its target");
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-7")]
    public void No_spread_gives_identity()
    {
        var from = Card(Quaternion.Identity);
        var to = Enumerable.Repeat(new Vec3(0.3f, 0.3f, 0.3f), 4).ToArray();

        Assert.Equal([1f, 0, 0, 0, 1f, 0, 0, 0, 1f], TurnFit.HornTurn(from, to, Centre(from), Centre(to)));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-shape-flat-card-8")]
    public void IsTurn_tolerance_is_inclusive_at_1e_4()
    {
        var m = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, -2, 0.7f)), 37f * MathF.PI / 180f);
        float[] Rot(Matrix4x4 x) => [x.M11, x.M21, x.M31, x.M12, x.M22, x.M32, x.M13, x.M23, x.M33];
        // Stretched along its first column by 1 + e/2: (RᵀR)₀₀ = (1 + e/2)² ≈ 1 + e.
        float[] Off(float e)
        {
            var r = Rot(m);
            float k = 1f + e / 2f;
            r[0] *= k; r[3] *= k; r[6] *= k;
            return r;
        }

        Assert.True(TurnFit.IsTurn([1, 0, 0, 0, 1, 0, 0, 0, 1]));
        Assert.True(TurnFit.IsTurn(Rot(m)));
        Assert.True(TurnFit.IsTurn(Off(5e-5f)));
        Assert.False(TurnFit.IsTurn(Off(2e-4f)));
        Assert.False(TurnFit.IsTurn([1, 0, 0, 0, 1, 0, 0, 0, -1]));
        Assert.False(TurnFit.IsTurn([2, 0, 0, 0, 2, 0, 0, 0, 2]));
        Assert.False(TurnFit.IsTurn([float.NaN, 0, 0, 0, 1, 0, 0, 0, 1]));
        Assert.False(TurnFit.IsTurn([1, 0, 0, 0, float.PositiveInfinity, 0, 0, 0, 1]));
    }

    [Fact]
    [Trait("Plan", "AC-keep-shape-flat-card-3")]
    [Trait("Plan", "EDGE-keep-shape-flat-card-13")]
    public void TurnOf_keeps_BestRotation_when_it_converged()
    {
        // A solid piece — points spread in all three directions — turned and bent a little: the polar answer converges.
        Vec3[] from = [new(0, 0, 0), new(0.02f, 0, 0), new(0, 0.015f, 0), new(0, 0, 0.01f), new(0.01f, 0.01f, 0.01f)];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.2f, 1, 0.3f)), 0.6f);
        var to = from.Select((p, i) => V(Vector3.Transform(P(p), turn) + new Vector3(0.0001f * i, 0, 0))).ToArray();
        var cFrom = Centre(from);
        var cTo = Centre(to);

        var best = SecondSkinWriter.BestRotation(from, to, V(cFrom), V(cTo));
        Assert.True(TurnFit.IsTurn(best));
        var r = BodyRetarget.TurnOf(from, to, cFrom, cTo, out bool fellBack);
        Assert.False(fellBack);
        Assert.Equal(best, r);

        // And the old rule on the diverging card: BestRotation's answer, whatever it is.
        var rnd = new Random(1365);
        Vec3[] card, bent;
        float[] broken;
        do
        {
            card = Card(RandomTurn(rnd));
            var c = Centre(card);
            var t = RandomTurn(rnd);
            bent = card.Select((p, i) => V(c + Vector3.Transform(P(p) - c, t) + new Vector3(0.0003f * (i % 2), 0.0002f * (i / 2), 0))).ToArray();
            broken = SecondSkinWriter.BestRotation(card, bent, V(Centre(card)), V(Centre(bent)));
        } while (TurnFit.IsTurn(broken));
        var old = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoTurnFallback: true),
                                          () => BodyRetarget.TurnOf(card, bent, Centre(card), Centre(bent), out _));
        Assert.Equal(broken, old);
    }
}
