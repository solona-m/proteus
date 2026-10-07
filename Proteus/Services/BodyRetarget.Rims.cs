using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>How far clear of the skin a lifted rim edge ends up (0.5 mm).</summary>
    internal const float RimClear = 0.0005f;

    /// <summary>
    /// The most a rim corner is lifted (6 mm). A rim edge deeper in the body than that is not lying on the skin and
    /// sagging into it; it was put there.
    /// </summary>
    internal const float RimLiftMost = 0.006f;

    /// <summary>How far inside the skin a rim corner may already be and still count as lying on it (0.5 mm).</summary>
    private const float RimOnSkin = 0.0005f;

    /// <summary>
    /// How nearly back to back a fold's two flaps must lie for it to be a doubled hem's rim (0.9: within about 25° of
    /// each other). A hem turned back on itself folds flat; a pleat, a ruffle or a collar's roll opens much wider, and
    /// lifting one of those off the skin flares it.
    /// </summary>
    private const float RimFold = 0.9f;

    /// <summary>How finely a rim face is sampled (6 steps a side).</summary>
    private const int RimSamples = 6;

    /// <summary>
    /// A point of a rim face carried less than this by the face's rim corners (0.5) is the face's inner side, which
    /// lifting the rim cannot clear without flaring it — and the lift a point asks for grows as its share shrinks, so a
    /// lower floor let one point near the far corner lift the whole edge several times its own depth.
    /// </summary>
    private const float RimShareLeast = 0.5f;

    /// <summary>How many times the rim is measured and lifted again (3): a lifted corner moves the next edge's ends.</summary>
    private const int RimRounds = 3;

    /// <summary>
    /// The faces of a cloth mesh that lie on its rim: those with an edge only one face uses, or with an edge that is the
    /// fold of a doubled hem — two faces whose far corners lie nearly back to back across it (<see cref="RimFold"/>).
    /// The Pioneer's Bottoms' shorts turn their hem back, so their bottom edge is used twice.
    /// </summary>
    /// <param name="tris">The cloth's faces, as welded point indices.</param>
    /// <param name="at">Where each point is.</param>
    /// <param name="onRim">Set for every point on a rim edge, when given.</param>
    /// <returns>Indices into <paramref name="tris"/>.</returns>
    internal static HashSet<int> RimFaces(IReadOnlyList<(int A, int B, int C)> tris, Func<int, Vector3> at,
                                          bool[]? onRim = null)
    {
        var flaps = new Dictionary<(int, int), List<int>>();
        foreach (var (a, b, c) in tris)
            foreach (var (u, v, far) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                var key = u < v ? (u, v) : (v, u);
                if (!flaps.TryGetValue(key, out var list)) flaps[key] = list = [];
                list.Add(far);
            }

        bool IsRim((int U, int V) edge, List<int> far)
        {
            if (far.Count == 1) return true;
            if (far.Count != 2) return false;
            var span = at(edge.V) - at(edge.U);
            if (span.LengthSquared() < 1e-14f) return false;
            var along = Vector3.Normalize(span);
            Vector3 Out(int f)
            {
                var d = at(f) - at(edge.U);
                d -= along * Vector3.Dot(d, along);
                return d.LengthSquared() > 1e-14f ? Vector3.Normalize(d) : default;
            }
            return Vector3.Dot(Out(far[0]), Out(far[1])) > RimFold;
        }

        var rimEdges = new HashSet<(int, int)>();
        foreach (var (edge, far) in flaps)
            if (IsRim(edge, far))
            {
                rimEdges.Add(edge);
                if (onRim != null) onRim[edge.Item1] = onRim[edge.Item2] = true;
            }

        var faces = new HashSet<int>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            if (rimEdges.Contains(a < b ? (a, b) : (b, a)) || rimEdges.Contains(b < c ? (b, c) : (c, b))
                || rimEdges.Contains(c < a ? (c, a) : (a, c)))
                faces.Add(i);
        }
        return faces;
    }

    /// <summary>
    /// Lift a cloth rim that lies on the skin off it where its straight edges cut into the new body between corners.
    /// <para/>
    /// The Pioneer's Bottoms' shorts end in a hem laid ON vanilla's skin, corners 30-60 mm apart. The refit keeps each
    /// corner on the skin, as authored; but Neolithe's buttock is rounder, and the edges between them sank up to 5.3 mm
    /// into it. Nothing else lifts them: the push-out moves points that are inside, and every corner is on the skin; the
    /// face pass treats an open edge as a hem, not a clip. With the hem's edge buried, the skin under it can neither run
    /// on under the cloth (it would stand in front of the buried strip) nor stop short of it (a slit through to the
    /// background, seen in game). Lifted until each edge clears the skin, the hem stands a few millimetres off at its
    /// corners — and the skin has somewhere to go.
    /// <para/>
    /// Only faces whose rim edge itself dips into the skin, and only where the rim's corners lie on the skin or outside
    /// it: a rim the author put deeper is left as it is. Hard pieces stay. A lift whose path would take the corner
    /// through other cloth is not made — the face pass's layer guard learned that a push through a lining's shell shows
    /// — and lifts that would turn a face over are backed off as the push-out's own guard backs off (see
    /// <see cref="PullIn"/>), so the fold count the solve reports still stands.
    /// </summary>
    /// <returns>How many rim nodes moved.</returns>
    internal static int LiftRims(Sets sets, TargetBody drawn, Vec3[] nodeDelta, bool[]? stay)
    {
        var isCloth = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) isCloth[n] = true;

        var cloth = new List<(int A, int B, int C)>();
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int a = sets.NodeOf[sets.Tris[t]], b = sets.NodeOf[sets.Tris[t + 1]], c = sets.NodeOf[sets.Tris[t + 2]];
            if (!isCloth[a] || !isCloth[b] || !isCloth[c] || a == b || b == c || c == a) continue;
            cloth.Add((a, b, c));
        }
        var onRim = new bool[sets.NodeCount];
        var rimFaces = new List<(int A, int B, int C)>();
        foreach (int i in RimFaces(cloth, n => ToVector(sets.NodeAt[n]), onRim)) rimFaces.Add(cloth[i]);
        if (rimFaces.Count == 0) return 0;

        // Each cloth node's faces, for the fold and layer guards.
        var facesOf = new Dictionary<int, List<int>>();
        for (int i = 0; i < cloth.Count; i++)
            foreach (int n in new[] { cloth[i].A, cloth[i].B, cloth[i].C })
            {
                if (!onRim[n]) continue;
                if (!facesOf.TryGetValue(n, out var list)) facesOf[n] = list = [];
                list.Add(i);
            }

        Vector3 At(int n) => ToVector(sets.NodeAt[n]) + ToVector(nodeDelta[n]);
        float Off(Vector3 p, out Vector3 normal)
        {
            if (drawn.Deepest(p, PushProbeRange, out var hit))
            {
                normal = hit.Normal;
                return Vector3.Dot(p - hit.Point, hit.Normal);
            }
            normal = default;
            return float.PositiveInfinity;
        }

        var lifted = new HashSet<int>();
        var total = new float[sets.NodeCount];
        for (int round = 0; round < RimRounds; round++)
        {
            var need = new Dictionary<int, (float Lift, Vector3 Dir)>();
            foreach (var (a, b, c) in rimFaces)
            {
                Vector3 pa = At(a), pb = At(b), pc = At(c);
                float oa = Off(pa, out var na), ob = Off(pb, out var nb), oc = Off(pc, out var nc);
                // A rim corner the author put inside the skin is theirs.
                if ((onRim[a] && oa < -RimOnSkin) || (onRim[b] && ob < -RimOnSkin) || (onRim[c] && oc < -RimOnSkin))
                    continue;

                // Sampled over the face. Only the face's rim corners are lifted, so a point is lifted by its share of
                // them: the share it would take to bring it clear is what they each need. And only a face whose rim
                // edge itself dips — the points the rim corners carry whole — is a rim cutting into the skin.
                float want = 0f;
                bool edgeDips = false;
                for (int i = 0; i <= RimSamples; i++)
                    for (int j = 0; i + j <= RimSamples; j++)
                    {
                        float u = (float)i / RimSamples, v = (float)j / RimSamples, w = 1f - u - v;
                        float share = (onRim[a] ? w : 0f) + (onRim[b] ? u : 0f) + (onRim[c] ? v : 0f);
                        if (share < RimShareLeast) continue;
                        float off = Off(pa * w + pb * u + pc * v, out _);
                        if (off >= RimClear) continue;
                        want = MathF.Max(want, (RimClear - off) / share);
                        if (share > 0.999f) edgeDips = true;
                    }
                if (want <= 0f || !edgeDips) continue;
                // Lifted along the skin's own outward normal where the corner lies; a corner the skin is out of reach of
                // is not lying on it, and there is no outward to lift it along.
                if (onRim[a] && na.LengthSquared() > 1e-12f && (stay == null || !stay[a])) Want(a, want, na);
                if (onRim[b] && nb.LengthSquared() > 1e-12f && (stay == null || !stay[b])) Want(b, want, nb);
                if (onRim[c] && nc.LengthSquared() > 1e-12f && (stay == null || !stay[c])) Want(c, want, nc);
            }
            if (need.Count == 0) break;

            var by = new Dictionary<int, Vector3>();
            foreach (var (n, (lift, dir)) in need)
            {
                float room = RimLiftMost - total[n];
                if (room <= 0f) continue;
                var d = Vector3.Normalize(dir) * MathF.Min(lift, room);
                // Never through other cloth on the way out: the hem's own faces do not count.
                if (CrossesCloth(n, At(n), d)) continue;
                by[n] = d;
            }
            if (by.Count == 0) break;

            // Back off whatever turns a face over: halved on every corner of it, then given up.
            var scale = new Dictionary<int, float>();
            foreach (int n in by.Keys) scale[n] = 1f;
            Vector3 Trial(int n) => At(n) + (by.TryGetValue(n, out var d) ? d * scale[n] : Vector3.Zero);
            for (int pass = 0; ; pass++)
            {
                bool giveUp = pass >= PushUnfoldPasses;
                int turned = 0;
                var checkedFaces = new HashSet<int>();
                foreach (int n in by.Keys)
                {
                    if (scale[n] <= 0f || !facesOf.TryGetValue(n, out var faces)) continue;
                    foreach (int f in faces)
                    {
                        if (!checkedFaces.Add(f)) continue;
                        var (a, b, c) = cloth[f];
                        var was = Vector3.Cross(At(b) - At(a), At(c) - At(a));
                        if (was.LengthSquared() <= 1e-24f) continue;
                        if (Vector3.Dot(was, Vector3.Cross(Trial(b) - Trial(a), Trial(c) - Trial(a))) > 0f) continue;
                        float keep = giveUp ? 0f : 0.5f;
                        foreach (int m in new[] { a, b, c })
                            if (scale.ContainsKey(m)) scale[m] *= keep;
                        turned++;
                    }
                }
                if (turned == 0 || giveUp) break;
            }

            foreach (var (n, d) in by)
            {
                if (scale[n] <= 0f) continue;
                var step = d * scale[n];
                nodeDelta[n] = new Vec3(nodeDelta[n].X + step.X, nodeDelta[n].Y + step.Y, nodeDelta[n].Z + step.Z);
                total[n] += step.Length();
                lifted.Add(n);
            }

            void Want(int n, float lift, Vector3 dir)
            {
                if (!need.TryGetValue(n, out var have) || have.Lift < lift) need[n] = (lift, dir);
            }
        }
        return lifted.Count;

        // Whether moving node n from p by d passes through a cloth face that is not one of n's own.
        bool CrossesCloth(int n, Vector3 p, Vector3 d)
        {
            var lo = Vector3.Min(p, p + d);
            var hi = Vector3.Max(p, p + d);
            foreach (var (a, b, c) in cloth)
            {
                if (a == n || b == n || c == n) continue;
                Vector3 pa = At(a), pb = At(b), pc = At(c);
                var tlo = Vector3.Min(pa, Vector3.Min(pb, pc));
                var thi = Vector3.Max(pa, Vector3.Max(pb, pc));
                if (thi.X < lo.X || thi.Y < lo.Y || thi.Z < lo.Z || tlo.X > hi.X || tlo.Y > hi.Y || tlo.Z > hi.Z) continue;
                if (SegmentHits(p, d, pa, pb, pc)) return true;
            }
            return false;
        }
    }
}
