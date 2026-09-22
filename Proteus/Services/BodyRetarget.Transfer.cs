using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// The source bodies, as one surface to ask questions of: where the nearest body point is, how far it moved, and
    /// whether a garment vertex is sitting exactly on a body vertex.
    /// <para/>
    /// Several slots (chest, legs) are kept as several indexes and queried together, taking the nearest answer of all
    /// of them. That is the same thing as one index over their union — nearest over a union is the least of the
    /// nearests — without having to splice two models into one. A dress crossing the waist therefore sees no
    /// discontinuity where the chest model's geometry ends.
    /// </summary>
    internal sealed class SourceBody
    {
        /// <param name="Field">The correspondence's own answer per body vertex: what the garment's SKIN lands on,
        /// which has to be exact.</param>
        /// <param name="Eased">The same, smoothed over the body's surface: what CLOTH follows. Cloth samples the field
        /// wherever it happens to hang, and away from the body the nearest point is not a stable thing to ask for, so a
        /// step in the field tears the cloth across it. Skin is on the body and wants the exact answer.</param>
        private readonly (BodySurface Surface, IReadOnlyList<Vector3?> Field, IReadOnlyList<Vector3?> Eased)[] slots;
        private readonly Dictionary<(int, int, int), List<(Vector3 At, Vector3 Delta)>> snap = [];

        private SourceBody((BodySurface, IReadOnlyList<Vector3?>, IReadOnlyList<Vector3?>)[] slots) => this.slots = slots;

        public static SourceBody Build(IReadOnlyList<SlotPair> pairs)
        {
            var built = new (BodySurface, IReadOnlyList<Vector3?>, IReadOnlyList<Vector3?>)[pairs.Count];
            for (int i = 0; i < pairs.Count; i++)
            {
                var src = pairs[i].Correspondence.Source;
                var surface = new BodySurface(src, BodySurface.CellFor(MeanEdgeOf(src)));
                var whole = Whole(src, pairs[i].Correspondence.Field);
                built[i] = (surface, whole, Eased(src, whole));
            }

            var body = new SourceBody(built);

            // Built in slot order, then ascending vertex, so a tie between two body vertices in one bucket always
            // resolves the same way and a golden hash of the output is stable.
            foreach (var (surface, field, _) in built)
            {
                var src = surface;
                foreach (int v in src.SkinVertices)
                {
                    if (field[v] is not { } d) continue;
                    var at = src.PositionOf(v);
                    var key = MeshMath.PositionKey(ToVec(at), SnapPerMetre);
                    if (!body.snap.TryGetValue(key, out var bucket)) body.snap[key] = bucket = [];
                    bucket.Add((at, d));
                }
            }

            return body;
        }

        /// <summary>
        /// The field with its holes filled in from around them — the value at every vertex the correspondence DID place
        /// is kept exactly, so a field with no holes comes back as it was.
        /// <para/>
        /// A correspondence by texture coordinate leaves the odd vertex unplaced, and a triangle touching one is no use:
        /// the caller skips it and the point falls to another body, or to nothing. Either way the field steps where the
        /// hole is, and cloth tears across the step — measured on a stocking at the ankle, a 0.3 mm edge stretched to
        /// 7.1 mm. A hole is small by definition, so the average of its placed neighbours is a good answer, and holes
        /// that touch only holes are filled in later rounds from the ones that have been.
        /// </summary>
        internal static IReadOnlyList<Vector3?> Whole(ModelParts body, IReadOnlyList<Vector3?> field)
        {
            int missing = 0;
            for (int v = 0; v < field.Count; v++)
                if (field[v] == null) missing++;

            var adj = Neighbours(body, field.Count);

            var filled = new Vector3?[field.Count];
            for (int v = 0; v < field.Count; v++) filled[v] = field[v];
            if (missing > 0) Fill(filled, adj, missing);
            Despike(filled, adj);
            return filled;
        }

        /// <summary>
        /// The field eased over the body's surface — every vertex moved part way toward its neighbours' average,
        /// <see cref="SmoothRounds"/> times at <see cref="SmoothRate"/>. What CLOTH follows; skin keeps the exact
        /// field, since it is meant to land on the body rather than near it.
        /// <para/>
        /// A correspondence by texture coordinate is exact where the two bodies agree and noisy where the atlas is cut.
        /// Cloth three centimetres off the body asks for the nearest point, and around a heel that question has two
        /// answers a centimetre apart; between them the field steps, and the cloth tears across the step. Easing costs
        /// nothing real, because the shape change between two bodies is smooth, and it measurably improves the fit
        /// against an author's own hand-made size.
        /// </summary>
        internal static IReadOnlyList<Vector3?> Eased(ModelParts body, IReadOnlyList<Vector3?> field)
        {
            var adj = Neighbours(body, field.Count);
            var eased = new Vector3?[field.Count];
            for (int v = 0; v < field.Count; v++) eased[v] = field[v];

            for (int round = 0; round < SmoothRounds; round++)
            {
                var next = (Vector3?[])eased.Clone();
                for (int v = 0; v < eased.Length; v++)
                {
                    if (eased[v] is not { } d || adj[v] == null) continue;
                    var sum = Vector3.Zero;
                    int n = 0;
                    foreach (int m in adj[v])
                        if (eased[m] is { } other) { sum += other; n++; }
                    if (n == 0) continue;
                    next[v] = Vector3.Lerp(d, sum / n, SmoothRate);
                }
                Array.Copy(next, eased, eased.Length);
            }
            return eased;
        }

        /// <summary>
        /// Replace a vertex's displacement where it disagrees with every neighbour by more than <see cref="DespikeGap"/>.
        /// <para/>
        /// Two bodies' shapes differ smoothly, so the field between them is smooth too — except where the correspondence
        /// itself went wrong, which it can at the body's own texture seams: a vertex lands on the far side of a cut and
        /// takes a displacement metres from its neighbours'. Cloth sampled either side of that vertex is torn apart by
        /// it, and on a stocking it showed as a spike through the shoe. The neighbours' average is the answer the
        /// surface itself gives.
        /// </summary>
        private static void Despike(Vector3?[] field, List<int>[] adj)
        {
            var fixedUp = new List<(int V, Vector3 D)>();
            for (int v = 0; v < field.Length; v++)
            {
                if (field[v] is not { } d || adj[v] == null) continue;
                var sum = Vector3.Zero;
                int n = 0;
                foreach (int m in adj[v])
                    if (field[m] is { } other) { sum += other; n++; }
                if (n == 0) continue;
                var average = sum / n;
                float off = Vector3.Distance(d, average);
                if (off <= DespikeGap) continue;

                // Only where the neighbours AGREE with each other: a field is allowed to change quickly, and on a
                // body that changes shape a lot it does. What marks a mistake is one vertex disagreeing with a
                // neighbourhood that does not disagree with itself.
                float spread = 0f;
                foreach (int m in adj[v])
                    if (field[m] is { } other) spread += Vector3.Distance(other, average);
                spread /= n;
                if (off <= DespikeOutlier * spread) continue;

                fixedUp.Add((v, average));
            }
            foreach (var (v, d) in fixedUp) field[v] = d;
        }

        /// <summary>Which body vertices share a skin triangle: the only places a displacement may be carried between.</summary>
        private static List<int>[] Neighbours(ModelParts body, int count)
        {
            var adj = new List<int>[count];
            foreach (var part in body.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = part.Triangles[t + k], b = part.Triangles[t + (k + 1) % 3];
                        if (a < 0 || b < 0 || a >= count || b >= count) continue;
                        (adj[a] ??= []).Add(b);
                        (adj[b] ??= []).Add(a);
                    }
            }
            return adj;
        }

        /// <summary>Holes filled from their edges, ring by ring — see <see cref="Whole"/>.</summary>
        private static void Fill(Vector3?[] filled, List<int>[] adj, int missing)
        {
            for (int round = 0; round < HoleRounds && missing > 0; round++)
            {
                var next = (Vector3?[])filled.Clone();
                bool changed = false;
                for (int v = 0; v < filled.Length; v++)
                {
                    if (filled[v] != null || adj[v] == null) continue;
                    var sum = Vector3.Zero;
                    int n = 0;
                    foreach (int m in adj[v])
                        if (filled[m] is { } d) { sum += d; n++; }
                    if (n == 0) continue;
                    next[v] = sum / n;
                    missing--;
                    changed = true;
                }
                Array.Copy(next, filled, filled.Length);
                if (!changed) break;
            }
        }

        /// <summary>
        /// The displacement of the body vertex this point sits exactly on, if there is one.
        /// <para/>
        /// Most gear models are built by editing a copy of the body, so a large share of a garment's own body mesh IS
        /// the body's vertices. Taking those verbatim rather than through an interpolation is what makes bare skin come
        /// out seam-perfect instead of merely close.
        /// </summary>
        public bool TrySnap(Vector3 p, out Vector3 delta)
        {
            delta = default;
            var (kx, ky, kz) = MeshMath.PositionKey(ToVec(p), SnapPerMetre);
            float best = SnapEps * SnapEps;
            bool found = false;

            // The 27 neighbours, not just the home bucket: two points 0.01 mm apart can still straddle a boundary.
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!snap.TryGetValue((kx + dx, ky + dy, kz + dz), out var bucket)) continue;
                foreach (var (at, d) in bucket)
                {
                    float d2 = Vector3.DistanceSquared(p, at);
                    if (d2 >= best) continue;
                    best = d2;
                    delta = d;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Where the source body's nearest point to <paramref name="p"/> ends up on the target body — the point itself,
        /// not <paramref name="p"/> carried along with it. What laying a garment's skin onto the new body needs: the
        /// author's offset from the body is exactly what it drops.
        /// </summary>
        /// <param name="distance">How far <paramref name="p"/> was from the source body.</param>
        public bool TryLand(Vector3 p, float maxDistance, out Vector3 landing, out float distance)
        {
            landing = default;
            distance = 0f;
            bool found = false;
            float best = maxDistance;
            foreach (var (surface, field, _) in slots)
            {
                if (!surface.Nearest(p, best, out var hit)) continue;
                var sum = Vector3.Zero;
                float weight = 0f;
                int present = 0;
                if (field[hit.A] is { } fa) { sum += fa * hit.U; weight += hit.U; present++; }
                if (field[hit.B] is { } fb) { sum += fb * hit.V; weight += hit.V; present++; }
                if (field[hit.C] is { } fc) { sum += fc * hit.W; weight += hit.W; present++; }
                if (weight < 0.5f) continue;

                best = hit.Distance;
                distance = hit.Distance;
                landing = hit.Point + (present == 3 ? sum : sum / weight);
                found = true;
            }
            return found;
        }

        /// <summary>
        /// The displacement of the nearest point on any source body, interpolated across the triangle — and, where two
        /// bodies are both near, blended between them.
        /// <para/>
        /// Taking the nearest body outright puts a step in the field wherever two of them meet. The bodies overlap at
        /// the ankle, and a stocking that runs from foot to thigh crosses that overlap: neighbouring points took the
        /// foot's answer and the leg's, which differ, and the cloth between them tore — a 0.3 mm edge stretched to
        /// 7.1 mm, which reads in game as a spike through the shoe. Within <see cref="SlotBlend"/> of the nearest, a
        /// body's answer fades in rather than replacing it.
        /// </summary>
        public bool TryNearest(Vector3 p, float maxDistance, out Vector3 delta, out float distance)
        {
            delta = default;
            distance = 0f;
            bool found = false;
            float best = maxDistance;
            var blend = Vector3.Zero;
            float blendWeight = 0f;
            near.Clear();

            foreach (var (surface, _, field) in slots)
            {
                if (!surface.Nearest(p, maxDistance, out var hit)) continue;

                // Over the corners that have a landing, renormalised. A correspondence by texture coordinate leaves the
                // odd vertex unplaced — a seam, an island the other body cuts differently — and dropping every triangle
                // touching one would leave holes in the field around it. Most of the triangle's weight has to be
                // present, though, or the answer is one corner's guess stretched over the whole face.
                var sum = Vector3.Zero;
                float weight = 0f;
                int present = 0;
                if (field[hit.A] is { } fa) { sum += fa * hit.U; weight += hit.U; present++; }
                if (field[hit.B] is { } fb) { sum += fb * hit.V; weight += hit.V; present++; }
                if (field[hit.C] is { } fc) { sum += fc * hit.W; weight += hit.W; present++; }
                if (weight < 0.5f) continue;

                // All three present: the plain barycentric sum, undivided, so a complete field gives bit-for-bit the
                // answer it always did (u + v + w is 0.99999994 as often as it is 1).
                var here = present == 3 ? sum : sum / weight;
                if (hit.Distance < best)
                {
                    best = hit.Distance;
                    distance = hit.Distance;
                }
                near.Add((hit.Distance, here));
                found = true;
            }
            if (!found) return false;

            // Weighted toward the nearest: it alone at the surface it owns, the two blended where they overlap. One
            // body in reach is its own answer, bit for bit, so a single-slot refit is untouched.
            foreach (var (d, value) in near)
            {
                float w = SlotBlend <= 0f ? (d <= best ? 1f : 0f) : Math.Clamp(1f - (d - best) / SlotBlend, 0f, 1f);
                if (w <= 0f) continue;
                blend += value * w;
                blendWeight += w;
            }
            delta = blendWeight > 0f ? blend / blendWeight : delta;
            return true;
        }

        /// <summary>Scratch for <see cref="TryNearest"/>: each slot's answer and how far its body was.</summary>
        private readonly List<(float Distance, Vector3 Delta)> near = [];
    }

    /// <summary>
    /// Full inside <see cref="NearBand"/>, smoothly to nothing by <see cref="FarBand"/>.
    /// <para/>
    /// Neither a hard cutoff nor unbounded extrapolation. A cutoff at any radius puts a step in the field exactly where
    /// it is non-zero on one side and zero on the other, and on a long skirt that step runs as a crease all the way
    /// round. Extrapolating instead is wrong the other way: a hem 40 cm below the hip is not attached to the hip, and
    /// translating it by the hip's full displacement moves the silhouette and changes a flared hem's diameter for no
    /// reason. Smoothstep is C1 at both ends, so neither the position nor its slope has a step — the same argument
    /// <c>SecondSkinWriter.FootPushAt</c> makes for its own taper.
    /// </summary>
    internal static float Reach(float distance)
        => distance <= NearBand ? 1f
         : distance >= FarBand ? 0f
         : 1f - MeshMath.Smoothstep((distance - NearBand) / (FarBand - NearBand));

    /// <summary>
    /// Carry the body's displacement onto the garment.
    /// </summary>
    /// <param name="nodes">The nodes to move. Called with every node, embedded skin included — see <see cref="Sets"/>.</param>
    private static void Transfer(Sets sets, IReadOnlyList<int> nodes, SourceBody source,
                                 Vec3[] nodeDelta, bool[] snapped, out int transferred, out int missed)
    {
        transferred = 0;
        missed = 0;

        foreach (int n in nodes)
        {
            var p = ToVector(sets.NodeAt[n]);

            if (source.TrySnap(p, out var exact))
            {
                nodeDelta[n] = ToVec(exact);
                snapped[n] = true;
                transferred++;
                continue;
            }

            if (!source.TryNearest(p, FarBand, out var delta, out float distance))
            {
                missed++;
                continue;
            }

            nodeDelta[n] = ToVec(delta * Reach(distance));
            transferred++;
        }
    }

    private static float MeanEdgeOf(ModelParts model)
    {
        double total = 0;
        int count = 0;
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                total += Edge(model, a, b) + Edge(model, b, c) + Edge(model, c, a);
                count += 3;
            }
        }
        return count > 0 ? (float)(total / count) : 0.01f;
    }

    private static double Edge(ModelParts m, int a, int b)
    {
        int vc = m.Positions.Length / 3;
        if (a < 0 || b < 0 || a >= vc || b >= vc) return 0;
        double dx = m.Positions[a * 3] - m.Positions[b * 3];
        double dy = m.Positions[a * 3 + 1] - m.Positions[b * 3 + 1];
        double dz = m.Positions[a * 3 + 2] - m.Positions[b * 3 + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
