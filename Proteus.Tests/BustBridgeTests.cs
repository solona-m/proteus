using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The bust bridge's solver, on synthetic geometry rather than a body model.
/// <para/>
/// A body dump proves the plumbing and nothing else: it cannot say whether the span is straight, only
/// that vertices moved, and "vertices moved" is exactly what a half-converged relax reports too. These
/// build a surface whose right answer is known in closed form — two ridges with a valley between them,
/// where the correct result is the straight chord joining the ridge tops — so each claim the feature
/// makes is checked against a number rather than against a screenshot.
/// </summary>
public class BustBridgeTests
{
    /// <summary>
    /// A two-lobed chest: a Gaussian lobe either side of the midline with a valley between them, extruded
    /// up Y so the whole thing is a wall standing off a flat base in +Z.
    /// <para/>
    /// TALLER THAN IT IS DEEP, and that is load-bearing rather than incidental. The solver derives the
    /// body's vertical from the mesh's widest spread across the span direction, so a fixture two rows deep
    /// (which this was) has its "up" resolve to the lobes' own depth and pushes cloth sideways up the body.
    /// A real torso runs hips to neck and is nowhere near that flat; a fixture that is flatter than any
    /// body tests a case that cannot occur and fails one that matters.
    /// <para/>
    /// The profile is extruded unchanged, so every horizontal band has the same two apexes and the right
    /// answer is the same chord at every height — which keeps each assertion below about one thing.
    /// </summary>
    private static (SecondSkinWriter.Vec3[] Pos, SecondSkinWriter.Vec3[] Nrm, ushort[] Tris, float[] Bust)
        Chest(int columns = 41, int rows = 25, float halfWidth = 0.20f, float height = 0.30f,
              float lobe = 0.10f, float centre = 0.10f, float spread = 0.06f,
              float nipple = 0f, float nippleSpread = 0.012f)
    {
        var pos = new List<SecondSkinWriter.Vec3>();
        var nrm = new List<SecondSkinWriter.Vec3>();
        var bust = new List<float>();

        // Two Gaussian lobes at x = ±centre. Chosen over a trig shape because its geometry is obvious by
        // inspection: each lobe peaks at its own centre, the two overlap only faintly at x = 0, and that
        // shortfall IS the valley the span has to cross.
        float Lobe(float x, float c) => lobe * MathF.Exp(-((x - c) * (x - c)) / (spread * spread));

        // A far narrower bump on each lobe's peak, when asked for: the nipple. Narrow RELATIVE to the
        // lobe on purpose — smoothing has to be able to take this off without dissolving the breast, and
        // a bump of comparable width would not test that distinction at all. Round rather than extruded,
        // unlike the lobes, because a nipple is.
        float Nipple(float x, float y, float c)
            => nipple <= 0f ? 0f
             : nipple * MathF.Exp(-(((x - c) * (x - c) + (y - height * 0.5f) * (y - height * 0.5f))
                                    / (nippleSpread * nippleSpread)));

        for (int row = 0; row < rows; row++)
            for (int c = 0; c < columns; c++)
            {
                float t = (float)c / (columns - 1);            // 0..1 across the chest
                float x = -halfWidth + 2f * halfWidth * t;
                float y = height * row / (rows - 1);
                pos.Add(new SecondSkinWriter.Vec3(x, y,
                    Lobe(x, -centre) + Lobe(x, centre)
                  + Nipple(x, y, -centre) + Nipple(x, y, centre)));
                nrm.Add(new SecondSkinWriter.Vec3(0, 0, 1));   // the whole wall faces +Z
                // The outermost column each side is the untouched shell — the pinned boundary.
                bust.Add(c == 0 || c == columns - 1 ? 0f : 1f);
            }

        var tris = new List<ushort>();
        for (int row = 0; row + 1 < rows; row++)
            for (int c = 0; c + 1 < columns; c++)
            {
                ushort a  = (ushort)(row * columns + c),       b  = (ushort)(row * columns + c + 1);
                ushort a2 = (ushort)((row + 1) * columns + c), b2 = (ushort)((row + 1) * columns + c + 1);
                tris.AddRange(new[] { a, b, b2 });
                tris.AddRange(new[] { a, b2, a2 });
            }

        return (pos.ToArray(), nrm.ToArray(), tris.ToArray(), bust.ToArray());
    }

    private static SecondSkinWriter.Vec3[] Displaced(SecondSkinWriter.Vec3[] pos, SecondSkinWriter.Vec3[] d)
        => pos.Select((p, i) => new SecondSkinWriter.Vec3(p.X + d[i].X, p.Y + d[i].Y, p.Z + d[i].Z)).ToArray();

    /// <summary>
    /// How far each point stands above the mean of a ring around it, averaged over a band at radius
    /// <paramref name="lo"/>..<paramref name="hi"/> from a nipple. Positive bulges, negative dishes.
    /// <para/>
    /// Mid-scale on purpose. Curvature measured at vertex spacing is blind to a broad shallow dish — it
    /// reported the patch as convex while the surface was visibly caving — and a quadric extrapolated
    /// from outside depends on exactly where "outside" starts. A ring wider than the vertex spacing and
    /// narrower than the feature answers the question directly.
    /// </summary>
    private static float RingConvexity(SecondSkinWriter.Vec3[] p, SecondSkinWriter.Vec3[] at,
                                       float cx, float cy, float lo, float hi)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < p.Length; i++)
        {
            float dx = at[i].X - cx, dy = at[i].Y - cy;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            if (r < lo || r >= hi) continue;
            double ring = 0;
            int m = 0;
            for (int k = 0; k < p.Length; k++)
            {
                float ex = at[k].X - at[i].X, ey = at[k].Y - at[i].Y;
                float rr = MathF.Sqrt(ex * ex + ey * ey);
                if (rr < 0.012f || rr > 0.030f) continue;
                ring += p[k].Z;
                m++;
            }
            if (m < 6) continue;
            sum += p[i].Z - ring / m;
            n++;
        }
        return n == 0 ? 0f : (float)(sum / n);
    }

    [Fact]
    public void SmoothingDoesNotLeaveADishAroundTheNipple()
    {
        // THE REGRESSION GUARD for the failure this feature kept coming back to. Relaxing a disc pins it
        // to the disc's rim, and a surface pinned to a rim sits below the convex cap it replaced — so
        // every version that ran the relax alone shaved the nipple and left a shallow bowl behind it,
        // reported first as "a bump inside a recess", then "caves in instead of following the curve",
        // then "still slightly concave, it should still be convex slightly".
        //
        // Measured on a real body the surround ran -1.36/-1.46/-1.19mm dished before the dome floor
        // existed and -0.26/+0.15/-0.00mm after it.
        var (pos, nrm, tris, bust) = Chest(nipple: 0.012f);
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 1f);
        Assert.NotNull(plan);
        var outp = Displaced(pos, plan!.Delta);

        const float centre = 0.10f, midY = 0.15f;
        foreach (float side in new[] { -1f, 1f })
        {
            float cx = side * centre;

            // The tip must come DOWN but stay a bulge — no nipple, still convex.
            float tipBefore = RingConvexity(pos, pos, cx, midY, 0f, 0.012f);
            float tipAfter = RingConvexity(outp, pos, cx, midY, 0f, 0.012f);
            Assert.True(tipAfter < tipBefore,
                $"side {side}: the tip stands {tipAfter:0.#####} proud against {tipBefore:0.#####} — nothing came off");
            Assert.True(tipAfter > -1e-4f,
                $"side {side}: the tip finished {tipAfter:0.#####} — it was pushed INTO the breast, not levelled");

            // ...and the surround must not be left more dished than it started.
            float ringBefore = RingConvexity(pos, pos, cx, midY, 0.012f, 0.030f);
            float ringAfter = RingConvexity(outp, pos, cx, midY, 0.012f, 0.030f);
            Assert.True(ringAfter >= ringBefore - 1e-4f,
                $"side {side}: the surround went from {ringBefore:0.#####} to {ringAfter:0.#####} — "
              + "the pass dug a dish around the nipple instead of filling toward the breast");
        }
    }

    // ── the normal field ────────────────────────────────────────────────────────

    /// <summary>
    /// The same chest, with high-frequency noise laid over it — the small-scale variation a real body
    /// carries and a shell reproduces exactly. Deterministic, so a failure is reproducible.
    /// </summary>
    private static (SecondSkinWriter.Vec3[] Pos, SecondSkinWriter.Vec3[] Nrm, ushort[] Tris, float[] Bust)
        NoisyChest(float amplitude = 0.002f)
    {
        var (pos, nrm, tris, bust) = Chest();
        var rng = new Random(1234);
        for (int i = 0; i < pos.Length; i++)
            pos[i] = new SecondSkinWriter.Vec3(
                pos[i].X, pos[i].Y, pos[i].Z + (float)(rng.NextDouble() * 2 - 1) * amplitude);
        return (pos, nrm, tris, bust);
    }

    /// <summary>
    /// Mean angle in degrees between the normals of vertices sharing an edge — how faceted the shading
    /// is. The measurement the normal pass exists to move.
    /// </summary>
    private static float AdjacentNormalSpread(SecondSkinWriter.Vec3[] n, ushort[] tris)
    {
        double sum = 0;
        int count = 0;
        for (int t = 0; t + 2 < tris.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = tris[t + e], b = tris[t + (e + 1) % 3];
                float dot = n[a].X * n[b].X + n[a].Y * n[b].Y + n[a].Z * n[b].Z;
                sum += Math.Acos(Math.Clamp(dot, -1f, 1f)) * 180 / Math.PI;
                count++;
            }
        return count == 0 ? 0f : (float)(sum / count);
    }

    /// <summary>Raw area-weighted vertex normals — what the pass produces before any smoothing.</summary>
    private static SecondSkinWriter.Vec3[] FaceNormals(SecondSkinWriter.Vec3[] pos, ushort[] tris)
    {
        var acc = new SecondSkinWriter.Vec3[pos.Length];
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            var pa = pos[tris[t]]; var pb = pos[tris[t + 1]]; var pc = pos[tris[t + 2]];
            float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
            float wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
            float cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
            for (int e = 0; e < 3; e++)
            {
                int v = tris[t + e];
                acc[v] = new SecondSkinWriter.Vec3(acc[v].X + cx, acc[v].Y + cy, acc[v].Z + cz);
            }
        }
        return acc.Select(v =>
        {
            float l = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return l > 1e-9f ? new SecondSkinWriter.Vec3(v.X / l, v.Y / l, v.Z / l)
                             : new SecondSkinWriter.Vec3(0, 0, 1);
        }).ToArray();
    }

    [Fact]
    public void SmoothingTheNormalsCutsTheFaceting()
    {
        // The complaint this answers is "it matches the skin perfectly, but it's not a graceful curve":
        // geometry that conforms exactly and still shades lumpy, because shading shows the surface's
        // derivative and nothing had ever smoothed it.
        // The whole surface at full weight and no displacement, which isolates the normal pass from the
        // geometry solve. Deliberately NOT the nipple disc: the pass reads neighbours outside the region
        // unsmoothed on purpose, so that it fades into untouched shell rather than creasing against it,
        // and over a small patch almost every node is a hop or two from that boundary and has noise fed
        // back into it every pass. On a real body the region runs to about a thousand nodes and has an
        // interior, where the same code takes the chest from 6.19deg to 2.66deg.
        var (pos, nrm, tris, _) = NoisyChest();
        int vc = pos.Length;
        var nodeOf = Enumerable.Range(0, vc).ToArray();
        var weight = Enumerable.Repeat(1f, vc).ToArray();
        var zero = new SecondSkinWriter.Vec3[vc];

        var raw = FaceNormals(pos, tris);
        var smoothed = SecondSkinWriter.RelaxedNormals(pos, nrm, zero, nodeOf, weight, nrm, tris);

        // Interior only: a vertex on the mesh's border has fewer faces around it, so its normal is
        // one-sided whatever the field does, and that is a property of the fixture's edge rather than of
        // the pass.
        var interior = Enumerable.Range(0, vc)
            .Where(i => MathF.Abs(pos[i].X) < 0.17f && pos[i].Y > 0.03f && pos[i].Y < 0.27f)
            .ToHashSet();
        var innerTris = new List<ushort>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
            if (interior.Contains(tris[t]) && interior.Contains(tris[t + 1]) && interior.Contains(tris[t + 2]))
                innerTris.AddRange(new[] { tris[t], tris[t + 1], tris[t + 2] });
        Assert.True(innerTris.Count >= 90, $"only {innerTris.Count / 3} interior triangle(s) — too few");

        // Against the NOISELESS chest, which is the floor. Adjacent normals on a curved surface differ
        // because the surface curves, and that difference is the shape — the pass must not chase it to
        // zero. So the measurement is the EXCESS over a clean fixture of the same topology: how much of
        // the roughness that noise added has been taken back off.
        var (clean, _, _, _) = Chest();
        float floor = AdjacentNormalSpread(FaceNormals(clean, tris), innerTris.ToArray());
        float before = AdjacentNormalSpread(raw, innerTris.ToArray());
        float after = AdjacentNormalSpread(smoothed, innerTris.ToArray());

        Assert.True(before > floor + 1f, $"the fixture is not rough enough to test ({before:0.##} vs {floor:0.##}deg)");
        Assert.True(after - floor < (before - floor) * 0.5f,
            $"roughness above a clean surface went {before - floor:0.##}deg -> {after - floor:0.##}deg "
          + $"(floor {floor:0.##}deg, smoothed {after:0.##}deg) — the field is not being smoothed");
    }

    [Fact]
    public void SmoothingTheNormalsMovesNothing()
    {
        // What keeps this step separable from the geometry passes: it may only ever change SHADING, so
        // the nipple's displacement must be bit-for-bit what it was without it.
        var (pos, nrm, tris, bust) = NoisyChest();
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 1f);
        Assert.NotNull(plan);

        var before = pos.ToArray();
        SecondSkinWriter.RelaxedNormals(pos, nrm, plan!.Delta, plan.NodeOf, plan.NodeWeight,
                                        plan.NodeNormal, tris);
        for (int i = 0; i < pos.Length; i++)
        {
            Assert.Equal(before[i].X, pos[i].X);
            Assert.Equal(before[i].Y, pos[i].Y);
            Assert.Equal(before[i].Z, pos[i].Z);
        }
    }

    [Fact]
    public void SmoothingTheNormalsLeavesUntouchedVerticesExactly()
    {
        // An untouched shell has to stay byte-identical, or every composite republishes a model that
        // did not change and the game redraws for nothing.
        var (pos, nrm, tris, bust) = NoisyChest();
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 1f);
        Assert.NotNull(plan);

        var outN = SecondSkinWriter.RelaxedNormals(
            pos, nrm, plan!.Delta, plan.NodeOf, plan.NodeWeight, plan.NodeNormal, tris);

        int untouched = 0;
        for (int i = 0; i < pos.Length; i++)
        {
            if (plan.NodeWeight[plan.NodeOf[i]] > 0f) continue;
            untouched++;
            Assert.Equal(nrm[i].X, outN[i].X);
            Assert.Equal(nrm[i].Y, outN[i].Y);
            Assert.Equal(nrm[i].Z, outN[i].Z);
        }
        Assert.True(untouched > 0, "the fixture has no untouched vertices, so this proves nothing");
    }

    [Fact]
    public void SmoothingTheNormalsAgreesAcrossWeldedCopies()
    {
        // Seam copies must land on identical bytes or the UV seam cracks into a visible line.
        var (pos, nrm, tris, bust) = NoisyChest();
        int n = pos.Length;
        var dPos = pos.Concat(pos).ToArray();
        var dNrm = nrm.Concat(nrm).ToArray();
        var plan = SecondSkinWriter.BustBridgeSolve(
            dPos, dNrm, tris, bust.Concat(bust).ToArray(), 0f, smoothStrength: 1f);
        Assert.NotNull(plan);

        var outN = SecondSkinWriter.RelaxedNormals(
            dPos, dNrm, plan!.Delta, plan.NodeOf, plan.NodeWeight, plan.NodeNormal, tris);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(outN[i].X, outN[i + n].X, 6);
            Assert.Equal(outN[i].Y, outN[i + n].Y, 6);
            Assert.Equal(outN[i].Z, outN[i + n].Z, 6);
        }
    }

    [Fact]
    public void SpansTheValleyAsAStraightChord()
    {
        var (pos, nrm, tris, bust) = Chest();
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f);
        Assert.NotNull(plan);

        var outp = Displaced(pos, plan!.Delta);

        // The two apexes are wherever the SOURCE was highest either side of the midline. They anchor the
        // chord, so they are read off the input, not asserted into place.
        int apexL = -1, apexR = -1;
        for (int i = 0; i < pos.Length; i++)
        {
            if (pos[i].X < 0 && (apexL < 0 || pos[i].Z > pos[apexL].Z)) apexL = i;
            if (pos[i].X > 0 && (apexR < 0 || pos[i].Z > pos[apexR].Z)) apexR = i;
        }
        Assert.True(apexL >= 0 && apexR >= 0);

        // Every vertex between the apexes must sit on the straight line joining them, not in the dish it
        // started in. The tolerance is a two-hundredth of the valley's own depth: tight enough that this
        // is the assertion that failed every relaxation tried before the chord construction — those came
        // in between 20% and 36% of the depth, all of them looking like they had worked.
        float depth = 0f, worst = 0f;
        for (int i = 0; i < pos.Length; i++)
        {
            if (pos[i].X <= pos[apexL].X || pos[i].X >= pos[apexR].X) continue;
            float t = (pos[i].X - pos[apexL].X) / (pos[apexR].X - pos[apexL].X);
            float chord = pos[apexL].Z + (pos[apexR].Z - pos[apexL].Z) * t;
            depth = MathF.Max(depth, chord - pos[i].Z);
            worst = MathF.Max(worst, MathF.Abs(chord - outp[i].Z));
        }
        Assert.True(depth > 0.01f, $"the test surface has no valley to span (depth {depth})");
        Assert.True(worst < depth / 200f,
            $"the span is still dished: {worst:0.#####} off the chord, against a valley {depth:0.#####} deep");
    }

    [Fact]
    public void LeavesTheApexesWhereTheyWere()
    {
        var (pos, nrm, tris, bust) = Chest();
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f);
        Assert.NotNull(plan);

        // The apexes ARE the chord's endpoints, so they cannot move — that is what keeps the breasts their
        // own shape while the cleavage between them fills in. If this ever fails, the pass has become a
        // smooth rather than a span and it will flatten the bust along with the gap.
        for (int i = 0; i < pos.Length; i++)
        {
            bool apex = pos.Where(p => MathF.Sign(p.X) == MathF.Sign(pos[i].X) && pos[i].X != 0)
                           .All(p => p.Z <= pos[i].Z + 1e-6f);
            if (!apex || pos[i].X == 0) continue;
            float moved = MathF.Sqrt(plan!.Delta[i].X * plan.Delta[i].X
                                   + plan.Delta[i].Y * plan.Delta[i].Y
                                   + plan.Delta[i].Z * plan.Delta[i].Z);
            Assert.True(moved < 1e-5f, $"apex at x={pos[i].X:0.###} moved {moved:0.#####}");
        }
    }

    [Fact]
    public void NeverMovesAVertexInward()
    {
        var (pos, nrm, tris, bust) = Chest();
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f);
        Assert.NotNull(plan);

        // The no-clip guarantee, stated as the invariant it actually is: displacement along the outward
        // axis is non-negative everywhere. Nothing else is needed to know the span cannot enter the body,
        // which is why the feature has no separate clearance pass.
        var outp = Displaced(pos, plan!.Delta);
        for (int i = 0; i < pos.Length; i++)
            Assert.True(outp[i].Z >= pos[i].Z - 1e-6f,
                $"vertex {i} moved inward by {pos[i].Z - outp[i].Z:0.#######}");
    }

    [Fact]
    public void WeldedCopiesGetIdenticalDeltas()
    {
        var (pos, nrm, tris, bust) = Chest();

        // Duplicate every vertex at the same position — a UV seam, which a body mesh has down the sternum.
        // Two copies solving independently is how a shell cracks open along the midline, so the solver
        // welds first; this is the assertion that says it still does.
        int n = pos.Length;
        var pos2 = pos.Concat(pos).ToArray();
        var nrm2 = nrm.Concat(nrm).ToArray();
        var bust2 = bust.Concat(bust).ToArray();
        // The duplicates are unreferenced by any triangle, exactly as a seam copy is on ONE side of the
        // seam: they must still move with their originals, purely by sharing a position.
        var plan = SecondSkinWriter.BustBridgeSolve(pos2, nrm2, tris, bust2, 1f);
        Assert.NotNull(plan);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(plan!.Delta[i].X, plan.Delta[i + n].X, 6);
            Assert.Equal(plan.Delta[i].Y, plan.Delta[i + n].Y, 6);
            Assert.Equal(plan.Delta[i].Z, plan.Delta[i + n].Z, 6);
        }
    }

    [Fact]
    public void StrengthScalesHowFarItSpans()
    {
        var (pos, nrm, tris, bust) = Chest();
        var full = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f);
        var half = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0.25f);
        Assert.NotNull(full);
        Assert.NotNull(half);

        float Max(SecondSkinWriter.Vec3[] d) => d.Max(v => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z));
        // Weaker, but still in the same direction — the knob keeps more of the shape underneath rather
        // than switching to some other solve.
        Assert.True(Max(half!.Delta) < Max(full!.Delta));
        Assert.True(Max(half.Delta) > 0f);
    }

    [Fact]
    public void DeclinesWhenNothingIsInTheRegion()
    {
        var (pos, nrm, tris, _) = Chest();
        // Coverage has carved the cloth away between the cups — a low-cut neckline. Nothing to span, and
        // the caller must then write exactly the shell it would have written without this feature.
        Assert.Null(SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, new float[pos.Length], 1f));
        // ...and a strength of zero is off, not a no-op pass that still rewrites normals.
        var (_, _, _, bust) = Chest();
        Assert.Null(SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f));
    }

    [Fact]
    public void PinsTheHemOnTheBodyButNotBetweenTheCups()
    {
        var (pos, nrm, tris, bust) = Chest();

        // A bra: painted over the two cups only. Unpainted in the middle (the cleavage) AND outside them
        // (the flanks, where the garment ends and the body carries on).
        //
        // The two unpainted regions must be treated OPPOSITELY, which is the whole point of this test.
        // Out at the flanks the hem really is anchored to the body and must not lift. Between the cups it
        // is not anchored to anything — it IS the top of the span — and pinning it there held the top of
        // the cleavage diving as deep as it started while the surface below it spanned, which is what a
        // bralette looked like in game.
        var covered = new bool[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            float ax = MathF.Abs(pos[i].X);
            covered[i] = ax > 0.06f && ax < 0.16f;
        }

        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f, covered: covered);
        Assert.NotNull(plan);

        float movedInGap = 0f, movedOnFlank = 0f;
        for (int i = 0; i < pos.Length; i++)
        {
            if (covered[i]) continue;
            var d = plan!.Delta[i];
            float m = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (MathF.Abs(pos[i].X) < 0.06f) movedInGap = MathF.Max(movedInGap, m);
            else movedOnFlank = MathF.Max(movedOnFlank, m);
        }

        Assert.True(movedOnFlank < 1e-6f,
            $"the hem out on the flank lifted by {movedOnFlank:0.#####} — it is on the body and must not");
        Assert.True(movedInGap > 1e-4f,
            "nothing between the cups moved — the cleavage hem is still pinned");
    }

    [Fact]
    public void NeverTearsTheShellAtARaggedCoverageEdge()
    {
        var (pos, nrm, tris, bust) = Chest(columns: 81);

        // A RAGGED coverage boundary — alternate columns unpainted down one side, which is what a 256px
        // coverage map sampled per vertex actually produces along a garment's edge. This is the shape that
        // tore the shell in game: the region ramp was computed before the unpainted vertices were removed,
        // so full-weight vertices ended up next to zeroed ones and the edge came out as a row of spikes.
        var covered = new bool[pos.Length];
        for (int i = 0; i < pos.Length; i++)
            covered[i] = pos[i].X > -0.10f && (pos[i].X > -0.06f || (i % 2 == 0));

        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f, covered: covered);
        if (plan == null) return;

        // No two vertices sharing an edge may differ in displacement by more than the edge between them
        // can absorb. Asserted on the TRIANGLES rather than on the region, because tearing is a property
        // of the drawn surface and the region is exactly the thing that got it wrong.
        float worst = 0f;
        (int A, int B) at = (0, 0);
        for (int t = 0; t + 2 < tris.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = tris[t + e], b = tris[t + (e + 1) % 3];
                var da = plan.Delta[a];
                var db = plan.Delta[b];
                float drop = MathF.Abs(MathF.Sqrt(da.X * da.X + da.Y * da.Y + da.Z * da.Z)
                                     - MathF.Sqrt(db.X * db.X + db.Y * db.Y + db.Z * db.Z));
                float dx = pos[a].X - pos[b].X, dy = pos[a].Y - pos[b].Y, dz = pos[a].Z - pos[b].Z;
                float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len <= 1e-9f) continue;
                if (drop / len > worst) { worst = drop / len; at = (a, b); }
            }

        // The slope limit is 1.5; allow a little over it for welded copies and float noise. The build that
        // tore in game measured around 10 here, so this has a wide margin and still catches that.
        Assert.True(worst < 2.0f,
            $"edge {at.A}-{at.B} changes displacement at {worst:0.###} per unit length — the shell is torn");
    }

    [Fact]
    public void TwoLayersWithDifferentCoverageSpanIdentically()
    {
        var (pos, nrm, tris, bust) = Chest();

        // Two mods on one host, both spanning, with DIFFERENT coverage: a bodysuit that reaches the flanks
        // and a bralette that stops at the cups. Solved per layer they reach different heights across the
        // same cleavage, and wherever the lower one lifts further it comes through the upper — which is
        // what the bralette's lace punching through the bodysuit was.
        //
        // The shells are separated by a push along the normal AFTER this, so the ONLY thing that keeps the
        // stack in order is the two displacements being equal. This asserts that directly, against the two
        // coverages the writer would otherwise have used one each.
        // The two coverages differ in HEIGHT, not width, and that matters. Narrowing to |x| < 0.13 was the
        // original pair and no longer separates them: it leaves a region 0.26 across against 0.30 tall, so
        // the solve now recognises a region taller than it is wide and spans across the body rather than
        // along its own principal direction — correctly, and identically to the wide one. The two used to
        // differ partly because the narrow one was being spanned the WRONG WAY, which is not a difference
        // worth building a test on. Different heights give different bands, so the spans still differ.
        var wide = new bool[pos.Length];
        var narrow = new bool[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            float ax = MathF.Abs(pos[i].X);
            wide[i] = ax < 0.18f;
            narrow[i] = ax < 0.18f && pos[i].Y > 0.06f && pos[i].Y < 0.24f;
        }

        // The union is what the host solves once with — the writer builds it in Build().
        var union = new bool[pos.Length];
        for (int i = 0; i < pos.Length; i++) union[i] = wide[i] || narrow[i];

        var shared = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f, covered: union);
        Assert.NotNull(shared);

        // Per-layer solves must differ — otherwise this test proves nothing about sharing.
        var perWide = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f, covered: wide);
        var perNarrow = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 1f, covered: narrow);
        Assert.NotNull(perWide);
        Assert.NotNull(perNarrow);
        float spread = 0f;
        for (int i = 0; i < pos.Length; i++)
            spread = MathF.Max(spread, MathF.Abs(perWide!.Delta[i].Z - perNarrow!.Delta[i].Z));
        Assert.True(spread > 1e-4f,
            "the two coverages produce the same displacement anyway — this fixture cannot detect crossing");

        // Sharing keeps the stack in order only if the one plan is also not WORSE than either layer's own —
        // fixing a crossing by flattening less is not a fix. Compared across the CLEAVAGE rather than per
        // vertex: the bands, and so the chords, are derived from the region, so a wider region shifts
        // individual vertices by a hair in both directions. What must not move is how far the span gets.
        float Cleavage(SecondSkinWriter.BustBridgePlan p)
        {
            float best = 0f;
            for (int i = 0; i < pos.Length; i++)
                if (MathF.Abs(pos[i].X) < 0.04f) best = MathF.Max(best, p.Delta[i].Z);
            return best;
        }
        float s = Cleavage(shared!), a = Cleavage(perWide!), b = Cleavage(perNarrow!);
        Assert.True(s >= a - 1e-4f && s >= b - 1e-4f,
            $"the shared span ({s:0.#####}) falls short of a per-layer one ({a:0.#####} / {b:0.#####})");
    }

    // ── smoothing the nipple ────────────────────────────────────────────────────

    /// <summary>
    /// How far the surface stands proud of its own surroundings at <paramref name="at"/> — height there
    /// against the mean of a ring around it.
    /// <para/>
    /// PROMINENCE rather than a raw difference against a nippleless twin, because the pass lifts the whole
    /// smoothed patch clear of the body and that lift is larger when there is a nipple to remove. A raw
    /// difference counts that offset as surviving nipple and reports almost no improvement; a prominence
    /// is blind to any uniform offset, which is exactly the confound to remove.
    /// <para/>
    /// The breast's own curvature still shows up in it, so the caller subtracts the same measurement taken
    /// on a chest with no nipple.
    /// </summary>
    private static float Prominence(SecondSkinWriter.Vec3[] h, SecondSkinWriter.Vec3[] at,
                                    float cx, float cy, float inner, float outer)
    {
        float peak = float.MinValue, sum = 0f;
        int n = 0;
        for (int i = 0; i < h.Length; i++)
        {
            float dx = at[i].X - cx, dy = at[i].Y - cy;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            if (r <= inner * 0.5f) peak = MathF.Max(peak, h[i].Z);
            if (r < inner || r > outer) continue;
            sum += h[i].Z;
            n++;
        }
        return n == 0 || peak == float.MinValue ? 0f : peak - sum / n;
    }

    [Fact]
    public void SmoothingTakesTheNippleOut()
    {
        var (pos, nrm, tris, bust) = Chest(nipple: 0.012f);
        var (flat, fNrm, fTris, fBust) = Chest();

        var smoothed = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 1f);
        Assert.NotNull(smoothed);
        var smoothedFlat = SecondSkinWriter.BustBridgeSolve(flat, fNrm, fTris, fBust, 0f, smoothStrength: 1f);

        var outp = Displaced(pos, smoothed!.Delta);
        var outFlat = smoothedFlat == null ? flat : Displaced(flat, smoothedFlat.Delta);

        // The fixture puts each nipple at a known place, so the measurement does not depend on finding it
        // — which is the thing the pass itself deliberately avoids doing.
        const float centre = 0.10f, midY = 0.15f;
        foreach (float side in new[] { -1f, 1f })
        {
            float cx = side * centre;
            float before = Prominence(pos, pos, cx, midY, 0.012f, 0.024f)
                         - Prominence(flat, pos, cx, midY, 0.012f, 0.024f);
            float after = Prominence(outp, pos, cx, midY, 0.012f, 0.024f)
                        - Prominence(outFlat, pos, cx, midY, 0.012f, 0.024f);
            Assert.True(before > 0.004f, $"the fixture has no nipple to remove ({before:0.#####})");
            Assert.True(after < before * 0.35f,
                $"side {side}: the nipple still stands {after:0.#####} proud, against {before:0.#####}");
        }
    }

    [Fact]
    public void SmoothingPushesTheNippleIn()
    {
        // Smoothing LOWERS, and the body is republished so the shell cut from it stays clear. Stated as a
        // test rather than left implicit because the opposite was tried: lifting the patch back out
        // afterwards keeps the shell clear of the body but leaves the nipple exactly where it was, since a
        // uniform lift cannot change a local shape.
        //
        // Counted AT THE TIP, not over the whole region, and that distinction is the feature rather than
        // a convenience. Two of the three passes legitimately push OUT: the dome floor fills the trough a
        // nipple sits in, and the finishing relax redistributes in both directions. Counted over
        // everything this came out 456 in against 519 out while the tip was being shaved correctly, so the
        // wider count measures the other passes rather than this one.
        var (pos, nrm, tris, bust) = Chest(nipple: 0.012f);
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 1f);
        Assert.NotNull(plan);

        const float centre = 0.10f, midY = 0.15f;
        int inward = 0, outward = 0;
        for (int i = 0; i < pos.Length; i++)
        {
            float dy = pos[i].Y - midY;
            float dx = MathF.Min(MathF.Abs(pos[i].X - centre), MathF.Abs(pos[i].X + centre));
            if (MathF.Sqrt(dx * dx + dy * dy) > 0.012f) continue;   // the nipple itself
            if (plan!.Delta[i].Z < -1e-6f) inward++;
            else if (plan.Delta[i].Z > 1e-6f) outward++;
        }
        Assert.True(inward > 0 || outward > 0, "no vertex near either nipple moved at all");
        Assert.True(inward > outward * 2,
            $"at the nipple, {inward} vertices moved in and {outward} out — shaving a bump should push in");
    }

    [Fact]
    public void SmoothingLeavesTheBreastAlone()
    {
        // The guard against this turning into a general-purpose bust flattener, which is what it becomes
        // if the smoothing scale ever grows toward the breast's own.
        //
        // Measured as DEFORMATION, not movement: the pass lifts the whole smoothed patch clear of the body
        // by design, so a breast with nothing on it still travels outward as a unit, and asserting it
        // barely moves would be asserting against the fix rather than against the fault. Subtracting the
        // mean displacement leaves only the part that changed the breast's SHAPE, which is the thing that
        // must stay small.
        const float lobe = 0.10f;
        var (pos, nrm, tris, bust) = Chest();
        var plan = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 1f);
        if (plan == null) return;

        float mean = 0f;
        int n = 0;
        for (int i = 0; i < pos.Length; i++)
            if (bust[i] > 0f) { mean += plan.Delta[i].Z; n++; }
        Assert.True(n > 0);
        mean /= n;

        float deformed = 0f;
        for (int i = 0; i < pos.Length; i++)
            if (bust[i] > 0f) deformed = MathF.Max(deformed, MathF.Abs(plan.Delta[i].Z - mean));
        Assert.True(deformed < lobe * 0.10f,
            $"a breast with no nipple was reshaped by {deformed:0.#####} against a lobe of {lobe:0.##} "
          + $"(it also moved out by {mean:0.#####}, which is the lift and is expected)");
    }

    [Fact]
    public void SmoothingStrengthScalesAndZeroIsOff()
    {
        var (pos, nrm, tris, bust) = Chest(nipple: 0.012f);
        Assert.Null(SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 0f));

        var full = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 1f);
        var quarter = SecondSkinWriter.BustBridgeSolve(pos, nrm, tris, bust, 0f, smoothStrength: 0.25f);
        Assert.NotNull(full);
        Assert.NotNull(quarter);

        float Max(SecondSkinWriter.BustBridgePlan p) => p.Delta.Max(d => MathF.Abs(d.Z));
        Assert.True(Max(quarter!) < Max(full!));
        Assert.True(Max(quarter) > 0f);
    }

    [Fact]
    public void SmoothingWeldedCopiesGetIdenticalDeltas()
    {
        var (pos, nrm, tris, bust) = Chest(nipple: 0.012f);
        int n = pos.Length;
        var plan = SecondSkinWriter.BustBridgeSolve(
            pos.Concat(pos).ToArray(), nrm.Concat(nrm).ToArray(), tris,
            bust.Concat(bust).ToArray(), 0f, smoothStrength: 1f);
        Assert.NotNull(plan);
        for (int i = 0; i < n; i++)
            Assert.Equal(plan!.Delta[i].Z, plan.Delta[i + n].Z, 6);
    }

    [Fact]
    public void DeclinesOnAnAlreadyConvexSurface()
    {
        // One lobe, no valley: a shoulder or an upper arm that happens to carry a little bust weight. The
        // rule has nothing to raise, and the pass must say so rather than rewriting normals for no reason.
        const int columns = 41, rows = 25;
        var pos = new List<SecondSkinWriter.Vec3>();
        var nrm = new List<SecondSkinWriter.Vec3>();
        var bust = new List<float>();
        for (int row = 0; row < rows; row++)
            for (int c = 0; c < columns; c++)
            {
                float t = (float)c / (columns - 1);
                float x = -0.2f + 0.4f * t;
                pos.Add(new SecondSkinWriter.Vec3(x, 0.30f * row / (rows - 1),
                                                  0.1f * MathF.Cos(MathF.PI * (t - 0.5f))));
                nrm.Add(new SecondSkinWriter.Vec3(0, 0, 1));
                bust.Add(c == 0 || c == columns - 1 ? 0f : 1f);
            }
        var tris = new List<ushort>();
        for (int row = 0; row + 1 < rows; row++)
            for (int c = 0; c + 1 < columns; c++)
            {
                ushort a  = (ushort)(row * columns + c),       b  = (ushort)(row * columns + c + 1);
                ushort a2 = (ushort)((row + 1) * columns + c), b2 = (ushort)((row + 1) * columns + c + 1);
                tris.AddRange(new[] { a, b, b2 });
                tris.AddRange(new[] { a, b2, a2 });
            }

        var plan = SecondSkinWriter.BustBridgeSolve(pos.ToArray(), nrm.ToArray(), tris.ToArray(),
                                                    bust.ToArray(), 1f);
        Assert.Null(plan);
    }
}
