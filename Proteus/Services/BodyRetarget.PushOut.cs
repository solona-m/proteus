using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// The target bodies: where the cloth has to end up outside of.
    /// <para/>
    /// Nearest-point queries stay per slot, as in <see cref="SourceBody"/>. The inside/outside test does NOT: a body
    /// mod's chest model is cut off at the waist, and a generalised winding number over that alone would read a point
    /// in the middle of the torso as barely inside, because the waist opening is enormous relative to the shape. Chest
    /// and legs TOGETHER are very nearly a closed body — only the neck, wrists and ankles are still open, and those
    /// holes are small — so the winding number is built once over all of the slots at once.
    /// </summary>
    internal sealed class TargetBody
    {
        private readonly BodySurface[] surfaces;
        private readonly BodyBridge.BodyWinding? winding;

        private TargetBody(BodySurface[] surfaces, BodyBridge.BodyWinding? winding)
        {
            this.surfaces = surfaces;
            this.winding = winding;
        }

        public static TargetBody Build(IReadOnlyList<SlotPair> pairs)
        {
            var surfaces = new BodySurface[pairs.Count];
            for (int i = 0; i < pairs.Count; i++)
                surfaces[i] = new BodySurface(pairs[i].Target, BodySurface.CellFor(MeanEdgeOf(pairs[i].Target)));

            // Every slot's skin triangles concatenated into one vertex numbering, then welded, so the winding number
            // sees one body rather than several open shells.
            var pos = new List<Vec3>();
            var tris = new List<int>();
            foreach (var pair in pairs)
            {
                var model = pair.Target;
                int vc = model.Positions.Length / 3;
                int baseVertex = pos.Count;
                for (int i = 0; i < vc; i++)
                    pos.Add(new Vec3(model.Positions[i * 3], model.Positions[i * 3 + 1], model.Positions[i * 3 + 2]));

                foreach (var part in model.Parts)
                {
                    if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                    foreach (int v in part.Triangles)
                        tris.Add(v >= 0 && v < vc ? baseVertex + v : -1);
                }
            }

            if (tris.Count == 0) return new TargetBody(surfaces, null);

            var all = pos.ToArray();
            var nodeOf = MeshMath.WeldByPosition(all, out int nodeCount);
            var nodePos = new Vec3[nodeCount];
            for (int i = 0; i < all.Length; i++) nodePos[nodeOf[i]] = all[i];

            var clean = new List<int>(tris.Count);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                if (tris[t] < 0 || tris[t + 1] < 0 || tris[t + 2] < 0) continue;
                clean.Add(tris[t]);
                clean.Add(tris[t + 1]);
                clean.Add(tris[t + 2]);
            }

            return new TargetBody(surfaces, new BodyBridge.BodyWinding(nodePos, clean.ToArray(), nodeOf));
        }

        /// <summary>The nearest point on any target body within <paramref name="maxDistance"/>.</summary>
        public bool Nearest(Vector3 p, float maxDistance, out BodySurface.Hit hit)
        {
            hit = default;
            bool found = false;
            float best = maxDistance;
            foreach (var surface in surfaces)
            {
                if (!surface.Nearest(p, best, out var candidate)) continue;
                best = candidate.Distance;
                hit = candidate;
                found = true;
            }
            return found;
        }

        public bool Inside(Vector3 p) => winding?.Inside(ToVec(p)) == true;
    }

    /// <summary>
    /// Push cloth that ended up inside the target body back out of it.
    /// </summary>
    /// <param name="nodes">
    /// The nodes to push. Called with the CLOTH nodes only, minus the ones the transfer snapped — see
    /// <see cref="Sets"/>. Both exclusions are set membership and never a distance test, deliberately: a snapped node
    /// sits exactly ON the target surface, where the winding number is 0.5 and <c>Inside</c> is a coin flip, and the
    /// garment's own body mesh is SUPPOSED to coincide with the body. A cloth vertex 0.01 mm inside and a skin vertex
    /// exactly on the surface are the same number with opposite right answers, so no epsilon can separate them.
    /// </param>
    /// <returns>How many nodes moved.</returns>
    private static int PushOut(Sets sets, IReadOnlyList<int> nodes, TargetBody target,
                               Vec3[] nodeDelta, out float worst)
    {
        worst = 0f;

        var need = new float[sets.NodeCount];
        var dir = new Vec3[sets.NodeCount];
        var hasDir = new bool[sets.NodeCount];
        bool any = false;

        foreach (int n in nodes)
        {
            var p = ToVector(new Vec3(sets.NodeAt[n].X + nodeDelta[n].X,
                                      sets.NodeAt[n].Y + nodeDelta[n].Y,
                                      sets.NodeAt[n].Z + nodeDelta[n].Z));

            if (!target.Nearest(p, PushProbeRange, out var hit)) continue;

            dir[n] = ToVec(hit.Normal);
            hasDir[n] = true;

            if (!target.Inside(p)) continue;

            // Along the BODY's normal at the landing, not along (p - landing): for a point inside the body that
            // difference aims further in, and near the surface it is numerically meaningless as well.
            var wanted = hit.Point + hit.Normal * Clearance;
            float d = Vector3.Dot(wanted - p, hit.Normal);
            if (d <= 0f) continue;

            need[n] = d;
            any = true;
        }

        if (!any) return 0;

        // Spread the requirement outward with a slope limit, so the push does not step where it stops. Without this
        // the boundary between a pushed node and its unpushed neighbour is a crease of exactly the push distance.
        float step = MathF.Max(sets.MeanEdge, 1e-6f) * PushSlope;
        for (int round = 0; round < PushSpreadRounds; round++)
        {
            bool changed = false;
            foreach (int n in nodes)
            {
                float most = need[n];
                foreach (int m in sets.Adj[n])
                {
                    float from = need[m] - step;
                    if (from > most) most = from;
                }
                if (most <= need[n] + 1e-9f) continue;
                need[n] = most;
                changed = true;
            }
            if (!changed) break;
        }

        int pushed = 0;
        foreach (int n in nodes)
        {
            if (need[n] <= 0f) continue;
            var d = hasDir[n] ? dir[n] : sets.NodeNormal[n];
            if (d.X == 0f && d.Y == 0f && d.Z == 0f) continue;

            nodeDelta[n] = new Vec3(nodeDelta[n].X + d.X * need[n],
                                    nodeDelta[n].Y + d.Y * need[n],
                                    nodeDelta[n].Z + d.Z * need[n]);
            if (need[n] > worst) worst = need[n];
            pushed++;
        }

        return pushed;
    }
}
