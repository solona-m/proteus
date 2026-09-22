using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// The skin that will actually be DRAWN under the garment once it is worn — what the cloth must end up outside of.
    /// <para/>
    /// That is not the target body. A worn garment REPLACES its own slot's body model: put on a top and the game draws
    /// the top instead of the body's chest, so the chest body is never on screen while the top is. What is drawn in its
    /// place is the garment's own embedded body mesh, retargeted along with everything else — plus the bodies of the
    /// OTHER slots, which still render beside it, like the legs a top's hem hangs over.
    /// <para/>
    /// Pushing against the garment's own slot body was measured and is actively harmful. "This Old Thing" compresses
    /// the chest under its top, so its cloth legitimately sits inside the Neolithe body; testing against that body
    /// pushed 825 nodes out by up to 8 mm and made the refit 50% worse against the author's own hand-fitted size.
    /// <para/>
    /// The inside test is a signed distance along the interpolated normal at the nearest point rather than a winding
    /// number. The garment's own body mesh is an open patch — whatever part of the body the garment happens to show —
    /// and a winding number over an open patch is meaningless. Within <see cref="PushProbeRange"/>, which is all this
    /// pass looks at, the local test is also simply correct.
    /// </summary>
    internal sealed class TargetBody
    {
        private readonly BodySurface[] surfaces;

        private TargetBody(BodySurface[] surfaces) => this.surfaces = surfaces;

        /// <param name="garmentSlot">The slot the garment is worn in, whose body is therefore not drawn. Null to keep
        /// every slot's body, for a caller that cannot say.</param>
        /// <param name="garmentSkin">The garment whose body-skin parts are indexed — as authored for the BEFORE surface,
        /// with the transfer applied for the AFTER one; null when the garment carries no body mesh of its own.</param>
        /// <param name="before">Build the skin drawn under the garment as it was AUTHORED — the source bodies — rather
        /// than as it will be after the refit.</param>
        public static TargetBody Build(IReadOnlyList<SlotPair> pairs, string? garmentSlot, ModelParts? garmentSkin,
                                       bool before)
        {
            var surfaces = new List<BodySurface>();
            foreach (var pair in pairs)
            {
                if (garmentSlot != null && string.Equals(pair.Slot, garmentSlot, StringComparison.Ordinal)) continue;
                var model = before ? pair.Correspondence.Source : pair.Target;
                var body = new BodySurface(model, BodySurface.CellFor(MeanEdgeOf(model)));
                if (!body.IsEmpty) surfaces.Add(body);
            }

            if (garmentSkin != null)
            {
                var own = new BodySurface(garmentSkin, BodySurface.CellFor(MeanEdgeOf(garmentSkin)));
                if (!own.IsEmpty) surfaces.Add(own);
            }

            return new TargetBody(surfaces.ToArray());
        }

        /// <summary>The nearest drawn skin within <paramref name="maxDistance"/>.</summary>
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
    }

    /// <summary>
    /// The garment as it stands after the transfer: same parts and normals, moved positions. What
    /// <see cref="TargetBody"/> indexes the garment's own body mesh from.
    /// <para/>
    /// The normals are the author's, not recomputed. A size change turns the skin by a few degrees at most, and the
    /// push-out only uses the normal to tell inside from outside and to pick a direction, neither of which a few degrees
    /// changes.
    /// </summary>
    private static ModelParts Moved(ModelParts garment, Sets sets, Vec3[] nodeDelta)
    {
        var pos = (float[])garment.Positions.Clone();
        int vc = pos.Length / 3;
        for (int i = 0; i < vc; i++)
        {
            var d = nodeDelta[sets.NodeOf[i]];
            pos[i * 3] += d.X;
            pos[i * 3 + 1] += d.Y;
            pos[i * 3 + 2] += d.Z;
        }

        return new ModelParts
        {
            Positions = pos,
            Normals = garment.Normals,
            MeshSpans = garment.MeshSpans,
            Parts = garment.Parts,
            AttributeNames = garment.AttributeNames,
            Min = garment.Min,
            Max = garment.Max,
            ShatteredSubmeshes = garment.ShatteredSubmeshes,
        };
    }

    /// <summary>
    /// Push cloth the refit drove INTO the drawn skin back out of it — and nothing else.
    /// </summary>
    /// <param name="candidates">
    /// The CLOTH nodes, minus the ones the transfer snapped — see <see cref="Sets"/>. Both exclusions are set
    /// membership and never a distance test, deliberately: a snapped node sits exactly ON a body surface, where inside
    /// and outside are a coin flip, and the garment's own body mesh is SUPPOSED to coincide with the body. A cloth
    /// vertex 0.01 mm inside and a skin vertex exactly on the surface are the same number with opposite right answers,
    /// so no epsilon can separate them.
    /// </param>
    /// <param name="before">The skin drawn under the garment as AUTHORED.</param>
    /// <param name="after">The same skin after the refit.</param>
    /// <remarks>
    /// A third exclusion is made here, and it is the one that decides whether this pass helps at all: cloth the author
    /// put INSIDE the drawn skin is left exactly where the refit carried it. Garment authors routinely leave the whole
    /// body mesh under the fabric, where it is hidden, so cloth sitting behind a skin surface is very often authored
    /// rather than a clip. Measured on "This Old Thing": every one of the 536 nodes an unconditional push-out moved was
    /// already inside its own body mesh as shipped, by 4 mm on average and up to 19 mm, and pushing them made the refit
    /// 30% worse against the author's own hand-fitted size. So the rule is to undo only clips the refit CREATED —
    /// authored outside, now inside — and by the least that clears them.
    /// </remarks>
    /// <returns>How many nodes moved.</returns>
    private static int PushOut(Sets sets, IReadOnlyList<int> candidates, TargetBody before, TargetBody after,
                               Vec3[] nodeDelta, out float worst)
    {
        worst = 0f;

        // The authored-inside nodes out of the set entirely, before anything else looks at it: neither a source of
        // push nor a receiver of the spread below, which would otherwise drag hidden cloth out at the boundary.
        var nodes = new List<int>(candidates.Count);
        var authored = new float[sets.NodeCount];
        foreach (int n in candidates)
        {
            var p0 = ToVector(sets.NodeAt[n]);

            // Reached much further than the push itself: a point DEEP inside the body is authored-inside just as much
            // as one just under the surface, and at the short range the two were indistinguishable from cloth far
            // outside. A heeled shoe's foot sits well inside where the body's flat foot is drawn, and reading it as
            // "outside" had the push-out balloon the stocking by up to 30 mm.
            if (before.Nearest(p0, AuthoredProbeRange, out var h0))
            {
                float s0 = Vector3.Dot(p0 - h0.Point, h0.Normal);
                if (s0 < 0f) continue;
                authored[n] = s0;
            }
            else
            {
                authored[n] = float.MaxValue;   // nowhere near skin as authored, so certainly not tucked under it
            }
            nodes.Add(n);
        }

        var need = new float[sets.NodeCount];
        var dir = new Vec3[sets.NodeCount];
        var hasDir = new bool[sets.NodeCount];
        bool any = false;

        foreach (int n in nodes)
        {
            var p = ToVector(new Vec3(sets.NodeAt[n].X + nodeDelta[n].X,
                                      sets.NodeAt[n].Y + nodeDelta[n].Y,
                                      sets.NodeAt[n].Z + nodeDelta[n].Z));

            if (!after.Nearest(p, PushProbeRange, out var hit)) continue;

            dir[n] = ToVec(hit.Normal);
            hasDir[n] = true;

            float s1 = Vector3.Dot(p - hit.Point, hit.Normal);
            if (s1 >= 0f) continue;

            // Back to the clearance, or to the author's own standoff where that was less: un-clipping, not re-fitting.
            // Along the SKIN's normal at the landing, not along (p - landing): for a point inside, that difference aims
            // further in, and near the surface it is numerically meaningless as well.
            float d = MathF.Min(authored[n], Clearance) - s1;
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
