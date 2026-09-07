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
              float lobe = 0.10f, float centre = 0.10f, float spread = 0.06f)
    {
        var pos = new List<SecondSkinWriter.Vec3>();
        var nrm = new List<SecondSkinWriter.Vec3>();
        var bust = new List<float>();

        // Two Gaussian lobes at x = ±centre. Chosen over a trig shape because its geometry is obvious by
        // inspection: each lobe peaks at its own centre, the two overlap only faintly at x = 0, and that
        // shortfall IS the valley the span has to cross.
        float Lobe(float x, float c) => lobe * MathF.Exp(-((x - c) * (x - c)) / (spread * spread));

        for (int row = 0; row < rows; row++)
            for (int c = 0; c < columns; c++)
            {
                float t = (float)c / (columns - 1);            // 0..1 across the chest
                float x = -halfWidth + 2f * halfWidth * t;
                float y = height * row / (rows - 1);
                pos.Add(new SecondSkinWriter.Vec3(x, y, Lobe(x, -centre) + Lobe(x, centre)));
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
        var wide = new bool[pos.Length];
        var narrow = new bool[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            float ax = MathF.Abs(pos[i].X);
            wide[i] = ax < 0.18f;
            narrow[i] = ax < 0.13f;
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
