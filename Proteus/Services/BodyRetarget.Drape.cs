using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>How far across the midline the crotch field reaches, at a Midlander man's scale (6 cm) — see
    /// <see cref="DrapeCrotch"/>.</summary>
    internal const float DrapeHalfWidth = 0.06f;

    /// <summary>How far below the crotch the field reaches (5 cm).</summary>
    internal const float DrapeBelow = 0.05f;

    /// <summary>How far above the crotch the field reaches (14 cm): the front of a thong over the bulge and the back of it
    /// up the cleft.</summary>
    internal const float DrapeAbove = 0.14f;

    /// <summary>The most the drape moves any node from where it found it (25 mm).</summary>
    internal const float DrapeMaxMove = 0.025f;

    /// <summary>Rounds of the drape at most.</summary>
    internal const int DrapeRounds = 32;
    internal static int DrapeDiagRings = 0;
    internal static bool DrapeDiagRelax = false;

    /// <summary>The step the drape's exit takes along the cloth's normal before it bisects (2 mm), and where the bisection
    /// stops (0.1 mm).</summary>
    internal const float DrapeExitStep = 0.002f, DrapeExitTolerance = 0.0001f;

    /// <summary>The smallest triangle the drape counts as folded (1 mm², as <see cref="CrossSexRefit.ClothFolded(byte[],
    /// Planned, Action{string}?)"/>).</summary>
    private const float DrapeMinArea = 1e-6f;

    /// <summary>What <see cref="DrapeCrotch"/> did.</summary>
    /// <param name="Patch">Nodes it was allowed to move: the folded faces' corners and the rings about them in the
    /// field. 0 when it did not run.</param>
    /// <param name="FoldsBefore">Folded cloth faces touching the patch when it began.</param>
    /// <param name="FoldsAfter">The same, in the round it kept.</param>
    /// <param name="WorstMove">The furthest it moved a node.</param>
    /// <param name="Moved">Each node it moved, for the report's net worst push.</param>
    internal sealed record DrapeOutcome(int Patch, int FoldsBefore, int FoldsAfter, float WorstMove, bool[]? Moved)
    {
        internal static readonly DrapeOutcome None = new(0, 0, 0, 0f, null);
    }

    /// <summary>The crotch field about <paramref name="a"/>: what <see cref="DrapeCrotch"/> may move.</summary>
    internal static bool InCrotchField(CrotchAnchor a, Vector3 p)
        => MathF.Abs(p.X - a.MidX) <= DrapeHalfWidth * a.Scale
           && p.Y >= a.Y - DrapeBelow * a.Scale && p.Y <= a.Y + DrapeAbove * a.Scale;

    /// <summary>The crotch of the bodies <paramref name="pairs"/> refit onto, or null when they have none — a top's chest,
    /// shoes, gloves.</summary>
    internal static CrotchAnchor? CrotchOfTargets(IReadOnlyList<SlotPair> pairs)
    {
        var bodies = new List<(ModelParts, XivLiveMesh.SkinnedMesh)>();
        foreach (var p in pairs)
            if (p.TargetModel != null && ModelSkinReader.Read(p.TargetModel, null, null) is { } skin
                && skin.VertexCount * 3 == p.Target.Positions.Length)
                bodies.Add((p.Target, skin));
        return bodies.Count == 0 ? null : FindCrotch(bodies);
    }

    /// <summary>
    /// Cloth turned over in the crotch, mended: a women's garment fitted to a man's body whose genitals face every way.
    /// The push-out moves each buried point along the normal of the skin it is buried in, and over the shaft, the scrotum
    /// and the thighs beside them those normals part, so neighbouring points are pushed across each other and the faces
    /// between them turn over — back-face culled in game, a hole in the cloth. Unfold cannot take them back: its bury
    /// guard forbids relaxing into that same skin. Measured on Stormblessed's Neo-Ishgardian bottoms fitted to TBSE's
    /// Gen A legs: 75 cloth faces turned over, every one of them in this field (CrossSexCrotchLocalTests).
    /// <para/>
    /// Only the folded patches move — their corners and <see cref="SettleRings"/> rings of cloth about them in the field;
    /// skin, held, kept-shape and snapped skin-copy nodes never. Each round relaxes the patch halfway toward its
    /// neighbours, then takes every point still inside the body out of it along the CLOTH's own normal, smoothed over two
    /// rings — neighbouring cloth normals agree where the skin's part — stepping until it clears every surface of the
    /// drawn body (<see cref="TargetBody.Deepest"/>), at most <see cref="DrapeMaxMove"/> from where the drape found it.
    /// The normal is a push direction only: no normal is written. A round counts only when every point is finite and no
    /// point ends deeper in the body than it began; the best counting round is kept, fewest folds then least movement.
    /// </summary>
    internal static DrapeOutcome DrapeCrotch(Sets sets, Vec3[] nodeDelta, TargetBody drawn, CrotchAnchor anchor,
                                             bool[]? stay, bool[] snapped)
    {
        var movable = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes)
            movable[n] = (stay == null || !stay[n]) && !snapped[n];
        var cloth = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) cloth[n] = true;

        var signedOff = SignedOff(drawn);
        Vector3 At(Vec3[] d, int n) => ToVector(sets.NodeAt[n]) + ToVector(d[n]);

        // The cloth triangles, by node, each corner cloth: what is counted and what the normals are read from.
        var tris = new List<(int A, int B, int C)>();
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
            if (va < 0 || vb < 0 || vc < 0
                || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) continue;
            int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
            if (a == b || b == c || c == a || !cloth[a] || !cloth[b] || !cloth[c]) continue;
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[b]) - ToVector(sets.NodeAt[a]),
                                   ToVector(sets.NodeAt[c]) - ToVector(sets.NodeAt[a]));
            if (n0.Length() * 0.5f < DrapeMinArea) continue;
            tris.Add((a, b, c));
        }
        bool Turned(Vec3[] d, (int A, int B, int C) t)
        {
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[t.B]) - ToVector(sets.NodeAt[t.A]),
                                   ToVector(sets.NodeAt[t.C]) - ToVector(sets.NodeAt[t.A]));
            var pa = At(d, t.A);
            return Vector3.Dot(n0, Vector3.Cross(At(d, t.B) - pa, At(d, t.C) - pa)) < 0f;
        }
        Vector3 Middle(Vec3[] d, (int A, int B, int C) t) => (At(d, t.A) + At(d, t.B) + At(d, t.C)) / 3f;

        // The patch: corners of the turned faces in the field, and the rings of cloth about them still in it.
        var inPatch = new bool[sets.NodeCount];
        var frontier = new List<int>();
        foreach (var t in tris)
        {
            if (!Turned(nodeDelta, t) || !InCrotchField(anchor, Middle(nodeDelta, t))) continue;
            foreach (int n in new[] { t.A, t.B, t.C })
                if (movable[n] && !inPatch[n]) { inPatch[n] = true; frontier.Add(n); }
        }
        if (frontier.Count == 0) return DrapeOutcome.None;
        for (int ring = 0; ring < (Tuned.NoDrape ? 0 : DrapeDiagRings); ring++)
        {
            var next = new List<int>();
            foreach (int n in frontier)
                foreach (int m in sets.Adj[n])
                    if (movable[m] && !inPatch[m] && InCrotchField(anchor, At(nodeDelta, m))) { inPatch[m] = true; next.Add(m); }
            frontier = next;
        }
        var patch = Enumerable.Range(0, sets.NodeCount).Where(n => inPatch[n]).ToArray();
        var touching = tris.Where(t => inPatch[t.A] || inPatch[t.B] || inPatch[t.C]).ToList();
        int Count(Vec3[] d) => touching.Count(t => Turned(d, t));

        var start = (Vec3[])nodeDelta.Clone();
        var entry = new float[sets.NodeCount];
        foreach (int n in patch) entry[n] = signedOff(At(start, n));
        int before = Count(start);

        var trisOf = new List<int>[sets.NodeCount];
        for (int i = 0; i < touching.Count; i++)
            foreach (int n in new[] { touching[i].A, touching[i].B, touching[i].C })
                (trisOf[n] ??= []).Add(i);

        var d = (Vec3[])start.Clone();
        var best = (Delta: start, Folds: before, Move: 0f);
        float step = MathF.Max(sets.MeanEdge, 1e-6f) * PushSlope;
        for (int round = 0; round < DrapeRounds && best.Folds > 0; round++)
        {
            // Relax: halfway toward the neighbours' mean movement.
            if (DrapeDiagRelax)
            {
            var relaxed = (Vec3[])d.Clone();
            foreach (int n in patch)
            {
                var sum = Vector3.Zero;
                int k = 0;
                foreach (int m in sets.Adj[n])
                    if (cloth[m]) { sum += ToVector(d[m]); k++; }
                if (k == 0) continue;
                var mid = (ToVector(d[n]) + sum / k) * 0.5f;
                relaxed[n] = new Vec3(mid.X, mid.Y, mid.Z);
            }
            d = relaxed;
            }

            // The cloth's normal at each patch node: from the faces about it still facing the way they were drawn — a
            // face turned over faces into the body, and its normal would push the cloth further in — smoothed over two
            // rings of cloth, then turned to face away from the skin the point is deepest in.
            var normal = new Vector3[sets.NodeCount];
            foreach (var t in tris)
            {
                if (Turned(d, t)) continue;
                var pa = At(d, t.A);
                var face = Vector3.Cross(At(d, t.B) - pa, At(d, t.C) - pa);
                normal[t.A] += face;
                normal[t.B] += face;
                normal[t.C] += face;
            }
            for (int ring = 0; ring < 2; ring++)
            {
                var smooth = (Vector3[])normal.Clone();
                foreach (int n in patch)
                    foreach (int m in sets.Adj[n])
                        if (cloth[m]) smooth[n] += normal[m];
                normal = smooth;
            }
            foreach (int n in patch)
            {
                if (!drawn.Deepest(At(d, n), PushProbeRange, out var hit)) continue;
                if (normal[n].LengthSquared() <= 1e-20f) normal[n] = hit.Normal;
                else if (Vector3.Dot(normal[n], hit.Normal) < 0f) normal[n] = -normal[n];
            }

            // Out along it, past every surface of the body; the push spread so it does not step where it stops.
            var push = new float[sets.NodeCount];
            foreach (int n in patch)
            {
                var p = At(d, n);
                if (signedOff(p) >= Clearance || normal[n].LengthSquared() <= 1e-20f) continue;
                push[n] = ExitAlong(signedOff, p, Vector3.Normalize(normal[n]), Clearance, DrapeMaxMove);
            }
            for (int spread = 0; spread < PushSpreadRounds; spread++)
            {
                bool changed = false;
                foreach (int n in patch)
                    foreach (int m in sets.Adj[n])
                        if (inPatch[m] && push[n] - step > push[m]) { push[m] = push[n] - step; changed = true; }
                if (!changed) break;
            }
            foreach (int n in patch)
            {
                if (push[n] <= 0f || normal[n].LengthSquared() <= 1e-20f) continue;
                var moved = ToVector(d[n]) + Vector3.Normalize(normal[n]) * push[n];
                // Never further than the cap from where the drape found it.
                var from = ToVector(start[n]);
                if (Vector3.Distance(moved, from) > DrapeMaxMove)
                    moved = from + Vector3.Normalize(moved - from) * DrapeMaxMove;
                d[n] = new Vec3(moved.X, moved.Y, moved.Z);
            }

            // Counted only when finite and nothing ends deeper than it began.
            bool counts = true;
            float worst = 0f;
            foreach (int n in patch)
            {
                var v = ToVector(d[n]);
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)
                    || signedOff(At(d, n)) < MathF.Min(entry[n], 0f) - UnfoldBuryTolerance) { counts = false; break; }
                worst = MathF.Max(worst, Vector3.Distance(v, ToVector(start[n])));
            }
            int folds = Count(d);
            PushTrace.Value?.Invoke("drape-round", [round, folds, counts ? 1f : 0f, worst, before, patch.Length]);
            if (!counts) continue;
            if (folds < best.Folds || (folds == best.Folds && worst < best.Move))
                best = ((Vec3[])d.Clone(), folds, worst);
        }

        Array.Copy(best.Delta, nodeDelta, nodeDelta.Length);
        var movedNodes = new bool[sets.NodeCount];
        foreach (int n in patch)
            movedNodes[n] = ToVector(nodeDelta[n]) != ToVector(start[n]);
        return new DrapeOutcome(patch.Length, before, best.Folds, best.Move, movedNodes);
    }

    /// <summary>
    /// How far along <paramref name="dir"/> from <paramref name="p"/> the point clears the drawn body by
    /// <paramref name="clearance"/> — <paramref name="signedOff"/> reads every surface of it, so the first skin passed is
    /// not taken for the exit. Steps <see cref="DrapeExitStep"/> at a time, then bisects to
    /// <see cref="DrapeExitTolerance"/>; <paramref name="max"/> when it does not clear by then.
    /// </summary>
    internal static float ExitAlong(Func<Vector3, float> signedOff, Vector3 p, Vector3 dir, float clearance, float max)
    {
        if (signedOff(p) >= clearance) return 0f;
        float lo = 0f, hi = -1f;
        for (float t = DrapeExitStep; t <= max + 1e-6f; t += DrapeExitStep)
        {
            if (signedOff(p + dir * t) >= clearance) { hi = t; break; }
            lo = t;
        }
        if (hi < 0f) return max;
        while (hi - lo > DrapeExitTolerance)
        {
            float mid = (lo + hi) * 0.5f;
            if (signedOff(p + dir * mid) >= clearance) hi = mid;
            else lo = mid;
        }
        return hi;
    }

    /// <summary>How much longer or shorter than authored a cloth edge may be and still count as coherent when the region
    /// grows (3 times, a third) — see <see cref="CrotchRegion"/>.</summary>
    internal const float RegionStretch = 3f;

    /// <summary>Coherent rings the region grows past before it stops — see <see cref="CrotchRegion"/>.</summary>
    internal const int RegionCoherentRings = 2;

    /// <summary>
    /// The cloth whose movement the crotch re-solve replaces: the movable corners of folded cloth triangles in the field,
    /// grown ring by ring while the ring's triangles still hold a fold or an edge stretched past
    /// <see cref="RegionStretch"/> or crushed under its inverse, until <see cref="RegionCoherentRings"/> coherent rings
    /// have been taken, and never past the field. Empty when the field holds no fold.
    /// </summary>
    /// <param name="movable">Nodes the re-solve may move: cloth, not held, not kept whole, not an authored lining.</param>
    /// <param name="reachedEdge">Whether it stopped at the field's edge with its boundary still incoherent.</param>
    internal static bool[] CrotchRegion(Sets sets, Vec3[] nodeDelta, CrotchAnchor anchor, bool[] movable,
                                        out bool reachedEdge)
    {
        reachedEdge = false;
        var cloth = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) cloth[n] = true;
        Vector3 At(int n) => ToVector(sets.NodeAt[n]) + ToVector(nodeDelta[n]);
        var trisOf = new List<(int A, int B, int C)>[sets.NodeCount];
        var tris = new List<(int A, int B, int C)>();
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
            if (va < 0 || vb < 0 || vc < 0
                || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) continue;
            int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
            if (a == b || b == c || c == a || !cloth[a] || !cloth[b] || !cloth[c]) continue;
            if (Vector3.Cross(ToVector(sets.NodeAt[b]) - ToVector(sets.NodeAt[a]),
                              ToVector(sets.NodeAt[c]) - ToVector(sets.NodeAt[a])).Length() * 0.5f < DrapeMinArea) continue;
            var tri = (a, b, c);
            tris.Add(tri);
            foreach (int n in new[] { a, b, c }) (trisOf[n] ??= []).Add(tri);
        }
        bool Turned((int A, int B, int C) t)
        {
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[t.B]) - ToVector(sets.NodeAt[t.A]),
                                   ToVector(sets.NodeAt[t.C]) - ToVector(sets.NodeAt[t.A]));
            var pa = At(t.A);
            return Vector3.Dot(n0, Vector3.Cross(At(t.B) - pa, At(t.C) - pa)) < 0f;
        }
        bool EdgeOff(int a, int b)
        {
            float was = Vector3.Distance(ToVector(sets.NodeAt[a]), ToVector(sets.NodeAt[b]));
            if (was < 1e-5f) return false;
            float r = Vector3.Distance(At(a), At(b)) / was;
            return r > RegionStretch || r < 1f / RegionStretch;
        }
        bool Incoherent((int A, int B, int C) t) => Turned(t) || EdgeOff(t.A, t.B) || EdgeOff(t.B, t.C) || EdgeOff(t.C, t.A);

        var region = new bool[sets.NodeCount];
        var frontier = new List<int>();
        foreach (var t in tris)
        {
            if (!Turned(t) || !InCrotchField(anchor, (At(t.A) + At(t.B) + At(t.C)) / 3f)) continue;
            foreach (int n in new[] { t.A, t.B, t.C })
                if (movable[n] && !region[n]) { region[n] = true; frontier.Add(n); }
        }
        int coherent = 0;
        while (frontier.Count > 0 && coherent < RegionCoherentRings)
        {
            var next = new List<int>();
            bool incoherent = false;
            foreach (int n in frontier)
                foreach (int m in sets.Adj[n])
                {
                    if (region[m] || !movable[m]) continue;
                    if (!InCrotchField(anchor, At(m)))
                    {
                        if (trisOf[n]?.Any(Incoherent) == true) reachedEdge = true;
                        continue;
                    }
                    region[m] = true;
                    next.Add(m);
                }
            foreach (int n in next)
                if (trisOf[n]?.Any(Incoherent) == true) { incoherent = true; break; }
            coherent = incoherent ? 0 : coherent + 1;
            frontier = next;
        }
        return region;
    }

    /// <summary>
    /// The region's movement replaced by the smoothest movement its fixed surroundings allow: the Laplace equation over
    /// <paramref name="region"/> (uniform weights over each node's neighbours), every other node's movement fixed, solved
    /// per component by conjugate gradient. A component with no fixed neighbour has nothing to fill from and is left as it
    /// was. The authored shape, carried by a smooth offset, turns no face over while the offset's gradient stays small.
    /// </summary>
    /// <param name="converged">Whether every component's solve converged to <paramref name="tolerance"/>.</param>
    /// <param name="unanchored">Nodes in components with no fixed neighbour, left as they were.</param>
    internal static void HarmonicFill(Sets sets, Vec3[] nodeDelta, bool[] region, out bool converged, out int unanchored,
                                      float tolerance = 1e-6f, int maxIterations = 2000)
    {
        converged = true;
        unanchored = 0;
        var index = new int[sets.NodeCount];
        Array.Fill(index, -1);
        var seen = new bool[sets.NodeCount];
        for (int start = 0; start < sets.NodeCount; start++)
        {
            if (!region[start] || seen[start]) continue;
            // One component of the region, and whether anything fixed borders it.
            var comp = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            bool anchored = false;
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                comp.Add(n);
                foreach (int m in sets.Adj[n])
                {
                    if (!region[m]) { anchored = true; continue; }
                    if (!seen[m]) { seen[m] = true; stack.Push(m); }
                }
            }
            if (!anchored)
            {
                unanchored += comp.Count;
                continue;
            }
            for (int i = 0; i < comp.Count; i++) index[comp[i]] = i;
            // A x = b, A the degree on the diagonal less the neighbours inside, b the fixed neighbours' movement; per axis.
            int count = comp.Count;
            for (int axis = 0; axis < 3; axis++)
            {
                float Get(Vec3 v) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
                var x = new double[count];
                var b = new double[count];
                for (int i = 0; i < count; i++)
                {
                    x[i] = Get(nodeDelta[comp[i]]);
                    foreach (int m in sets.Adj[comp[i]])
                        if (!region[m]) b[i] += Get(nodeDelta[m]);
                }
                void Apply(double[] v, double[] into)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int n = comp[i];
                        double sum = sets.Adj[n].Count * v[i];
                        foreach (int m in sets.Adj[n])
                            if (region[m]) sum -= v[index[m]];
                        into[i] = sum;
                    }
                }
                var r = new double[count];
                var ax = new double[count];
                Apply(x, ax);
                for (int i = 0; i < count; i++) r[i] = b[i] - ax[i];
                var d = (double[])r.Clone();
                var ad = new double[count];
                double rr = r.Sum(v => v * v);
                bool done = Math.Sqrt(rr) <= tolerance;
                for (int it = 0; it < maxIterations && !done; it++)
                {
                    Apply(d, ad);
                    double dad = 0;
                    for (int i = 0; i < count; i++) dad += d[i] * ad[i];
                    if (dad <= 0) break;
                    double alpha = rr / dad;
                    for (int i = 0; i < count; i++) { x[i] += alpha * d[i]; r[i] -= alpha * ad[i]; }
                    double next = r.Sum(v => v * v);
                    if (Math.Sqrt(next) <= tolerance) { done = true; break; }
                    double beta = next / rr;
                    rr = next;
                    for (int i = 0; i < count; i++) d[i] = r[i] + beta * d[i];
                }
                if (!done) converged = false;
                for (int i = 0; i < count; i++)
                {
                    var v = nodeDelta[comp[i]];
                    float f = (float)x[i];
                    nodeDelta[comp[i]] = axis == 0 ? new Vec3(f, v.Y, v.Z) : axis == 1 ? new Vec3(v.X, f, v.Z) : new Vec3(v.X, v.Y, f);
                }
            }
            foreach (int n in comp) index[n] = -1;
        }
    }

    /// <summary>
    /// The region's movement made as smooth as its fixed surroundings allow to second order: the sum over the region and
    /// the ring about it of the squared umbrella Laplacian of the movement, least — so the fixed cloth sets both the
    /// movement and its slope at the region's edge, and the offset bends rather than kinks there. Solved per component by
    /// conjugate gradient on the normal equations. A component with no fixed neighbour is left as it was.
    /// </summary>
    internal static void BiharmonicFill(Sets sets, Vec3[] nodeDelta, bool[] region, out bool converged, out int unanchored,
                                        float tolerance = 1e-7f, int maxIterations = 4000)
    {
        converged = true;
        unanchored = 0;
        var index = new int[sets.NodeCount];
        Array.Fill(index, -1);
        var seen = new bool[sets.NodeCount];
        for (int start = 0; start < sets.NodeCount; start++)
        {
            if (!region[start] || seen[start]) continue;
            var comp = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            bool anchored = false;
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                comp.Add(n);
                foreach (int m in sets.Adj[n])
                {
                    if (!region[m]) { anchored = true; continue; }
                    if (!seen[m]) { seen[m] = true; stack.Push(m); }
                }
            }
            if (!anchored)
            {
                unanchored += comp.Count;
                continue;
            }
            for (int i = 0; i < comp.Count; i++) index[comp[i]] = i;
            // The rows: the component and every node beside it, whose Laplacian the component's movement reaches.
            var rows = new List<int>(comp);
            var inRows = new HashSet<int>(comp);
            foreach (int n in comp)
                foreach (int m in sets.Adj[n])
                    if (inRows.Add(m)) rows.Add(m);
            int count = comp.Count;
            for (int axis = 0; axis < 3; axis++)
            {
                float Get(Vec3 v) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
                // M x: each row's Laplacian of the movement, the fixed part included; Mt r: its transpose on the unknowns.
                double[] Lap(double[] x, bool withFixed)
                {
                    var l = new double[rows.Count];
                    for (int k = 0; k < rows.Count; k++)
                    {
                        int n = rows[k];
                        double self = index[n] >= 0 ? x[index[n]] : withFixed ? Get(nodeDelta[n]) : 0;
                        double sum = sets.Adj[n].Count * self;
                        foreach (int m in sets.Adj[n])
                            sum -= index[m] >= 0 ? x[index[m]] : withFixed ? Get(nodeDelta[m]) : 0;
                        l[k] = sum;
                    }
                    return l;
                }
                double[] LapT(double[] l)
                {
                    var g = new double[count];
                    for (int k = 0; k < rows.Count; k++)
                    {
                        int n = rows[k];
                        if (index[n] >= 0) g[index[n]] += sets.Adj[n].Count * l[k];
                        foreach (int m in sets.Adj[n])
                            if (index[m] >= 0) g[index[m]] -= l[k];
                    }
                    return g;
                }
                var x = new double[count];
                for (int i = 0; i < count; i++) x[i] = Get(nodeDelta[comp[i]]);
                // Minimise |Lap(x) + c|², c the fixed part: (Mt M) x = −Mt c.
                var c = Lap(new double[count], true);
                var b = LapT(c);
                for (int i = 0; i < count; i++) b[i] = -b[i];
                double[] A(double[] v) => LapT(Lap(v, false));
                var ax = A(x);
                var r = new double[count];
                for (int i = 0; i < count; i++) r[i] = b[i] - ax[i];
                var d = (double[])r.Clone();
                double rr = r.Sum(v => v * v);
                bool done = Math.Sqrt(rr) <= tolerance;
                for (int it = 0; it < maxIterations && !done; it++)
                {
                    var ad = A(d);
                    double dad = 0;
                    for (int i = 0; i < count; i++) dad += d[i] * ad[i];
                    if (dad <= 0) break;
                    double alpha = rr / dad;
                    for (int i = 0; i < count; i++) { x[i] += alpha * d[i]; r[i] -= alpha * ad[i]; }
                    double next = r.Sum(v => v * v);
                    if (Math.Sqrt(next) <= tolerance) { done = true; break; }
                    double beta = next / rr;
                    rr = next;
                    for (int i = 0; i < count; i++) d[i] = r[i] + beta * d[i];
                }
                if (!done) converged = false;
                for (int i = 0; i < count; i++)
                {
                    var v = nodeDelta[comp[i]];
                    float f = (float)x[i];
                    nodeDelta[comp[i]] = axis == 0 ? new Vec3(f, v.Y, v.Z) : axis == 1 ? new Vec3(v.X, f, v.Z) : new Vec3(v.X, v.Y, f);
                }
            }
            foreach (int n in comp) index[n] = -1;
        }
    }
}
