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
        private readonly (BodySurface Surface, IReadOnlyList<Vector3?> Field)[] slots;
        private readonly Dictionary<(int, int, int), List<(Vector3 At, Vector3 Delta)>> snap = [];

        private SourceBody((BodySurface, IReadOnlyList<Vector3?>)[] slots) => this.slots = slots;

        public static SourceBody Build(IReadOnlyList<SlotPair> pairs)
        {
            var built = new (BodySurface, IReadOnlyList<Vector3?>)[pairs.Count];
            for (int i = 0; i < pairs.Count; i++)
            {
                var src = pairs[i].Correspondence.Source;
                var surface = new BodySurface(src, BodySurface.CellFor(MeanEdgeOf(src)));
                built[i] = (surface, pairs[i].Correspondence.Field);
            }

            var body = new SourceBody(built);

            // Built in slot order, then ascending vertex, so a tie between two body vertices in one bucket always
            // resolves the same way and a golden hash of the output is stable.
            foreach (var (surface, field) in built)
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
            foreach (var (surface, field) in slots)
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

        /// <summary>The displacement of the nearest point on any source body, interpolated across the triangle.</summary>
        public bool TryNearest(Vector3 p, float maxDistance, out Vector3 delta, out float distance)
        {
            delta = default;
            distance = 0f;
            bool found = false;
            float best = maxDistance;

            foreach (var (surface, field) in slots)
            {
                if (!surface.Nearest(p, best, out var hit)) continue;

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

                best = hit.Distance;
                distance = hit.Distance;
                // All three present: the plain barycentric sum, undivided, so a complete field gives bit-for-bit the
                // answer it always did (u + v + w is 0.99999994 as often as it is 1).
                delta = present == 3 ? sum : sum / weight;
                found = true;
            }

            return found;
        }
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
