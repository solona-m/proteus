using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;

using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    // ── bust bridge ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The breast bones under both names: vanilla, Bibo+ and gen3 bodies rig <c>j_mune_l/r</c>; IVCS bodies rig
    /// <c>iv_c_mune_l/r</c> and carry no <c>j_mune</c> at all.
    /// </summary>
    internal static readonly string[] BustBones = ["j_mune_l", "j_mune_r", "iv_c_mune_l", "iv_c_mune_r"];

    private static bool IsBustBone(string bone)
    {
        foreach (var b in BustBones)
            if (bone.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// How close to the midpoint between a band's two apexes the surface must come for the band to count as a cleft
    /// rather than a gap, as a fraction of the apexes' separation. See <c>ChordTarget</c>.
    /// </summary>
    private const float ChordMidlineGap = 0.25f;

    /// <summary>
    /// Two solves over one mesh as a single plan: the writer applies one plan per mesh. Valid because both weld with
    /// <see cref="WeldByPosition"/> (same node numbering) and each only raises along its own axis, so deltas add.
    /// </summary>
    internal static BustBridgePlan? MergePlans(BustBridgePlan? a, BustBridgePlan? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        if (a.Delta.Length != b.Delta.Length || a.NodeWeight.Length != b.NodeWeight.Length) return a;

        var delta = new Vec3[a.Delta.Length];
        for (int i = 0; i < delta.Length; i++)
            delta[i] = new Vec3(a.Delta[i].X + b.Delta[i].X,
                                a.Delta[i].Y + b.Delta[i].Y,
                                a.Delta[i].Z + b.Delta[i].Z);

        // Reshading is gated on this, so a node either pass touched has to report non-zero or it keeps a
        // normal describing the surface it no longer has.
        var weight = new float[a.NodeWeight.Length];
        for (int n = 0; n < weight.Length; n++) weight[n] = MathF.Max(a.NodeWeight[n], b.NodeWeight[n]);

        // A node either pass pinned stays pinned.
        bool[]? pinned = null;
        foreach (var src in new[] { a.NodePinned, b.NodePinned })
            if (src != null)
                for (int n = 0; n < src.Length && n < weight.Length; n++)
                    if (src[n]) (pinned ??= new bool[weight.Length])[n] = true;

        // A normal either pass knows: the heavier one where both do.
        Vec3[]? over = null;
        float[]? overW = null;
        foreach (var src in new[] { a, b })
        {
            if (src.NormalOverride is not { } o || src.NormalOverrideWeight is not { } ow) continue;
            over ??= new Vec3[weight.Length];
            overW ??= new float[weight.Length];
            for (int n = 0; n < ow.Length && n < overW.Length; n++)
                if (ow[n] > overW[n]) { overW[n] = ow[n]; over[n] = o[n]; }
        }

        return new BustBridgePlan
        {
            Delta = delta, NodeOf = a.NodeOf, NodeWeight = weight, NodeNormal = a.NodeNormal, NodePinned = pinned,
            NormalOverride = over, NormalOverrideWeight = overW,
        };
    }

    /// <summary>
    /// Zero the seed wherever the surface does not face backwards: <see cref="HipBone"/> covers front and back at the
    /// same height, and without this the cleft pass would span the crotch too. A local test of each vertex's normal
    /// against model +Z (forward on every body), not a dividing plane, because the surface curves round the hip.
    /// </summary>
    internal static void GateToBackFacing(float[] w, Vec3[] nrm, Vec3[] pos)
    {
        // The band around edge-on is faded, not cut: a step in the region ramp is a step in the displacement, which the
        // slope limit can only soften to a kink. A deep cleft's walls face each other rather than backwards, so a wall is
        // let through by facing the midline, faded in over CleftWallDepth behind z = 0 so the inner thighs at the crotch
        // stay the crotch fold's.
        const float Edge = 0.15f;
        for (int i = 0; i < w.Length && i < nrm.Length; i++)
        {
            if (w[i] <= 0f) continue;
            float back = Smoothstep(Math.Clamp((-nrm[i].Z - Edge) / Edge, 0f, 1f));
            if (back < 1f && i < pos.Length && nrm[i].Z <= 0f)
            {
                float inward = -MathF.Sign(pos[i].X) * nrm[i].X;
                float wall = Smoothstep(Math.Clamp((inward - Edge) / Edge, 0f, 1f))
                           * Smoothstep(Math.Clamp(-pos[i].Z / CleftWallDepth, 0f, 1f));
                back = MathF.Max(back, wall);
            }
            w[i] *= back;
        }
    }

    /// <summary>
    /// How far behind z = 0 a midline-facing wall must sit before <see cref="GateToBackFacing"/> takes it in full;
    /// fading in over that distance keeps the inner thighs out and the whole cleft in.
    /// </summary>
    private const float CleftWallDepth = 0.02f;

    /// <summary>How many bands past the last one with its own cleft a borrowed chord survives, fading to nothing across them.</summary>
    private const float ChordBorrowBands = 3f;

    /// <summary>
    /// Passes of Jacobi smoothing over a caller-supplied region ramp; a diffusion reaches about the square root of
    /// its pass count in rings, so sixteen reaches four.
    /// </summary>
    private const int RampSmoothPasses = 16;

    /// <summary>Fewest welded nodes the region needs before a bridge is attempted; fewer describe no cleavage.</summary>
    private const int MinBustBridgeNodes = 24;

    /// <summary>
    /// Movement below which a node counts as untouched, in model units (a tenth of <see cref="LayerSeparation"/>);
    /// decides which nodes are reported as moved and which keep their original normal bytes.
    /// </summary>
    internal const float BustBridgeEpsilon = 1e-6f;

    /// <summary>
    /// How much longer than the shortest crossing a path between the two bust lobes may be and still count as between
    /// them, in edges. More than a step or two: the sternum is narrower at the apexes than near the collarbone.
    /// </summary>
    private const int BustGapSlack = 8;

    /// <summary>How far down a node's normal must face (-Y) to count as a breast's underside when the region grows past
    /// the bones; about 12° below level.</summary>
    private const float BustLowerPoleFacing = 0.2f;

    /// <summary>How far a span node may stand above both its neighbours across the chest before it counts as a
    /// ridge — half a millimetre, under what shading shows, over the float noise of a flat chord.</summary>
    private const float BridgeRidgeSlack = 0.0005f;

    /// <summary>How many rings out from a garment's cut edge the tuned chest slope limit still applies.</summary>
    private const int BustHemRings = 3;

    /// <summary>Diagnostics only: when set, every span solve logs each node it matches, stage by stage.</summary>
    internal static Func<Vec3, bool>? TraceSpanNode;

    /// <summary>The smallest chord lift counted in the per-stage share report — 2mm, below which nothing reads.</summary>
    private const float BridgeShareFloor = 0.002f;

    /// <summary>How far below the breast bones' lowest node the underside may grow, as a share of the seeded lobes'
    /// height. A backstop; the crease normally stops it first.</summary>
    private const float BustLowerPoleReach = 0.5f;

    /// <summary>Ceiling on how far the gap search walks from a lobe; <see cref="BustGapSlack"/> does the real selecting.</summary>
    private const int BustGapMaxSteps = 64;

    /// <summary>
    /// Rings of the region's outer edge the effect ramps over, in edges. One only: <see cref="BustMaxSlope"/> does the
    /// fade in the mesh's own units, and this just takes the hard 0-to-1 step off the outermost vertices.
    /// </summary>
    private const int BustFadeSteps = 1;

    /// <summary>
    /// Steepest the bridge's displacement may change between two vertices, as a ratio to the distance between them
    /// (about 39° of tilt). Bounds the fade in mesh terms; above 0.8 the span stops improving and only buys folds.
    /// Re-measure whenever the axis or the fade changes.
    /// </summary>
    private const float BustMaxSlope = 0.8f;

    /// <summary>
    /// How far short of the skin a lift that would have ended inside the body is stopped. See
    /// <see cref="KeepLiftsOutside"/>.
    /// </summary>
    private const float BridgeInsideMargin = 0.001f;

    /// <summary>
    /// Cuts back every lift that would end inside the body (the chord assumes a height field along the axis; touching
    /// breasts break that). Judged at the lift's end only, by winding number (<see cref="BodyWinding"/>), never by
    /// crossing count: a node sits on the surface, so its start side cannot be read. An inside end is walked back in
    /// <see cref="BridgeInsideStep"/> steps to the furthest outside point, less <see cref="BridgeInsideMargin"/>.
    /// </summary>
    /// <returns>How many lifts were cut.</returns>
    /// <param name="checkedAt">Per node, the lift last found outside; a node whose lift still equals it is skipped.</param>
    private static int KeepLiftsOutside(float[] scale, Vec3[] start, Vec3 ax, BodyWinding body, float[] checkedAt,
                                        int nodeCount)
    {
        int cut = 0;
        for (int n = 0; n < nodeCount; n++)
        {
            float lift = scale[n];
            if (lift <= BridgeInsideMargin || lift == checkedAt[n]) continue;
            var s0 = start[n];
            Vec3 At(float d) => new(s0.X + ax.X * d, s0.Y + ax.Y * d, s0.Z + ax.Z * d);
            if (!body.Inside(At(lift))) { checkedAt[n] = lift; continue; }

            float keep = 0f;
            for (float d = lift - BridgeInsideStep; d > 0f; d -= BridgeInsideStep)
            {
                if (body.Inside(At(d))) continue;
                // The margin is taken back toward the start and can land inside again where the open air is thinner than it;
                // a cached value is never re-tested, so the margin is kept only when that point is outside too.
                float withMargin = MathF.Max(0f, d - BridgeInsideMargin);
                keep = withMargin > 0f && body.Inside(At(withMargin)) ? d : withMargin;
                break;
            }
            scale[n] = keep;
            checkedAt[n] = keep;
            cut++;
        }
        return cut;
    }

    /// <summary>
    /// The same guarantee for the span's steep edges: an edge steeper than <paramref name="gentle"/> can cut under a
    /// breast's curve while both ends sit outside. Sampled along the edge; where a sample is inside, the higher end is
    /// walked down until the edge clears or is no steeper than <paramref name="gentle"/>.
    /// </summary>
    private static int KeepEdgesOutside(float[] scale, Vec3[] start, Vec3 ax, BodyWinding body, List<int>[] adj,
                                        int nodeCount, float gentle)
    {
        int cut = 0;
        for (int n = 0; n < nodeCount; n++)
        {
            if (scale[n] <= BridgeInsideMargin) continue;
            foreach (int k in adj[n])
            {
                float dx = start[k].X - start[n].X, dy = start[k].Y - start[n].Y, dz = start[k].Z - start[n].Z;
                float floor = scale[k] + gentle * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (scale[n] <= floor) continue;
                bool lowered = false;
                while (scale[n] > floor && EdgeInside(n, k)) { scale[n] = MathF.Max(floor, scale[n] - BridgeInsideStep); lowered = true; }
                if (lowered) cut++;
            }
        }
        return cut;

        bool EdgeInside(int a, int b)
        {
            var pa = new Vec3(start[a].X + ax.X * scale[a], start[a].Y + ax.Y * scale[a], start[a].Z + ax.Z * scale[a]);
            var pb = new Vec3(start[b].X + ax.X * scale[b], start[b].Y + ax.Y * scale[b], start[b].Z + ax.Z * scale[b]);
            for (int s = 1; s < EdgeInsideSamples; s++)
            {
                float t = s / (float)EdgeInsideSamples;
                if (body.Inside(new Vec3(pa.X + (pb.X - pa.X) * t, pa.Y + (pb.Y - pa.Y) * t, pa.Z + (pb.Z - pa.Z) * t)))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The bridge down from each breast to the ribs: a per-node 3-D move in the plane of the chest's outward axis and
    /// its vertical, laying the skin tucked under a breast onto the line from the breast's lowest front point to where
    /// the ribs meet it. Null when nothing moves. Built per thin slice from the convex hull of the slice's front
    /// profile (over the slice and its two neighbours so adjacent slices agree), using only hull edges that bridge a
    /// real gap, start on the breast and run below the slice's most-forward point.
    /// </summary>
    private static Vec3[]? UnderBustSling(Vec3[] start, Vec3[] nNorm, float[] h0, float[] lat, float[] ver,
                                          bool[] seed, bool[] cut, List<int>[] adj, int nodeCount, Vec3 ax,
                                          Vec3 lateral, Vec3 vertical, BodyWinding body, Action<string>? log,
                                          out float[] weight, out float[] between)
    {
        return new SlingSolution(start, nNorm, h0, lat, ver, seed, cut, adj, nodeCount, ax, lateral, vertical, body, log).Run(out weight, out between);
    }

    /// <summary>How far below the breasts' lowest seeded node the sling's slices reach, as a share of the breasts' height.</summary>
    private const float SlingReachDown = 0.8f;

    /// <summary>Jacobi passes smoothing the sling's displacement among the nodes it moved.</summary>
    private const int SlingSmoothPasses = 10;

    /// <summary>Most rounds of drawing back sling edges that pass through the body.</summary>
    private const int SlingEdgeRounds = 12;

    /// <summary>Sling weight at which a node counts as part of a sling when finding the skin between the two.</summary>
    private const float SlingSolid = 0.5f;

    /// <summary>How far below the breast the sling's line may run before it has to be back on the skin, as a share of
    /// the breasts' height. The line ends on the skin at this depth unless the ribs come forward to meet it sooner.</summary>
    private const float SlingLength = 0.6f;

    /// <summary>How far the sling's curve dips in from the straight line at its middle, as a share of its length.</summary>
    private const float SlingSag = 0.08f;

    /// <summary>Shortest hull edge, in mesh edges, that counts as bridging a gap rather than tracing the surface.</summary>
    private const float SlingMinSpan = 2f;

    /// <summary>Smallest move worth making — below this the node already lies on the line.</summary>
    private const float SlingMinMove = 0.0005f;

    /// <summary>Largest move allowed; past it something other than a crease is between the node and the line.</summary>
    private const float SlingMaxMove = 0.045f;

    /// <summary>Share of the bust's width over which the sling fades out at each outer side.</summary>
    private const float SlingSideFade = 0.12f;

    /// <summary>How far past the breasts' own width the slices reach, as a share of it: the crease runs on round the
    /// outer corner a little past the breast bones.</summary>
    private const float SlingSideExtend = 0.15f;

    /// <summary>The torso's half-width at the bust, as a multiple of the breasts' own reach from the midline — sets
    /// the angle a slice turns to.</summary>
    private const float SlingTorsoWidth = 1.25f;

    /// <summary>Slice angle, in radians, inside which the sling moves along the chest's forward axis.</summary>
    private const float SlingFrontAngle = 0.45f;

    /// <summary>Slice angle past which the sling moves along the slice's true outward direction; blended between.</summary>
    private const float SlingRadialAngle = 0.85f;

    /// <summary>How much further out than the breast in the same slice a point may stand and still be body, not arm.</summary>
    private const float SlingArmMargin = 0.004f;

    /// <summary>Segments a steep span edge is split into for <see cref="KeepEdgesOutside"/>.</summary>
    private const int EdgeInsideSamples = 4;

    /// <summary>Most rounds of cut-then-slope-limit before the span is taken as settled. See the call site.</summary>
    private const int BridgeInsideRounds = 8;

    /// <summary>Step a lift that ended inside the body is walked back by, looking for open air.</summary>
    private const float BridgeInsideStep = 0.001f;

    /// <summary>
    /// Inside-or-outside for a point against a body mesh that is not closed, by generalized winding number: the solid
    /// angle every triangle subtends at the point, over 4π. Near 1 inside, near 0 outside, from any direction. Over the
    /// whole mesh: a breast's front alone reads about a half; the torso closing the volume behind is what makes the
    /// inside read as inside. Sign-agnostic, so it does not depend on which way the mesh winds.
    /// </summary>
    /// <remarks>
    /// Exact only for triangles in the cells around the point; every farther cell is one oriented patch (area-weighted
    /// normal at area-weighted centre, dipole term A·d/|d|³). The answer only has to tell 0 from 1.
    /// </remarks>
    internal sealed class BodyWinding
    {
        private const float CellSize = 0.02f;
        private const int NearCells = 1;

        private readonly Vec3[] pos;
        private readonly int[] tri;   // node triples
        private readonly Dictionary<(int, int, int), List<int>> cellTris = new();
        private readonly List<((int, int, int) Key, Vec3 Area, Vec3 Centre)> cells = new();

        public BodyWinding(Vec3[] nodePos, int[] tris, int[] nodeOf)
        {
            pos = nodePos;
            var t3 = new List<int>(tris.Length);
            var area = new Dictionary<(int, int, int), (double X, double Y, double Z)>();
            var centre = new Dictionary<(int, int, int), (double X, double Y, double Z, double W)>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= nodeOf.Length || tris[t + 1] >= nodeOf.Length || tris[t + 2] >= nodeOf.Length) continue;
                int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
                if (a == b || b == c || a == c) continue;
                int id = t3.Count / 3;
                t3.Add(a); t3.Add(b); t3.Add(c);

                Vec3 pa = pos[a], pb = pos[b], pc = pos[c];
                var ctr = new Vec3((pa.X + pb.X + pc.X) / 3f, (pa.Y + pb.Y + pc.Y) / 3f, (pa.Z + pb.Z + pc.Z) / 3f);
                var key = Key(ctr);
                (cellTris.TryGetValue(key, out var l) ? l : cellTris[key] = []).Add(id);

                // Half the cross product: the triangle's area along its normal, wound the way the exact formula
                // below is.
                double ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
                double vx = pc.X - pa.X, vy = pc.Y - pa.Y, vz = pc.Z - pa.Z;
                double nx = 0.5 * (uy * vz - uz * vy), ny = 0.5 * (uz * vx - ux * vz), nz = 0.5 * (ux * vy - uy * vx);
                var s = area.GetValueOrDefault(key);
                area[key] = (s.X + nx, s.Y + ny, s.Z + nz);
                double w = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                var cs = centre.GetValueOrDefault(key);
                centre[key] = (cs.X + ctr.X * w, cs.Y + ctr.Y * w, cs.Z + ctr.Z * w, cs.W + w);
            }
            tri = t3.ToArray();
            foreach (var (key, s) in area)
            {
                var cs = centre[key];
                var at = cs.W > 0 ? new Vec3((float)(cs.X / cs.W), (float)(cs.Y / cs.W), (float)(cs.Z / cs.W))
                                  : new Vec3((key.Item1 + 0.5f) * CellSize, (key.Item2 + 0.5f) * CellSize,
                                             (key.Item3 + 0.5f) * CellSize);
                cells.Add((key, new Vec3((float)s.X, (float)s.Y, (float)s.Z), at));
            }
        }

        private static (int, int, int) Key(Vec3 q) =>
            ((int)MathF.Floor(q.X / CellSize), (int)MathF.Floor(q.Y / CellSize), (int)MathF.Floor(q.Z / CellSize));

        public float Winding(Vec3 p)
        {
            var k = Key(p);
            double sum = 0;
            foreach (var (key, a, c) in cells)
            {
                bool near = Math.Abs(key.Item1 - k.Item1) <= NearCells && Math.Abs(key.Item2 - k.Item2) <= NearCells
                         && Math.Abs(key.Item3 - k.Item3) <= NearCells;
                if (near)
                {
                    foreach (int id in cellTris[key]) sum += Exact(id, p);
                    continue;
                }
                double dx = c.X - p.X, dy = c.Y - p.Y, dz = c.Z - p.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                sum += (a.X * dx + a.Y * dy + a.Z * dz) / (d2 * Math.Sqrt(d2));
            }
            return (float)Math.Abs(sum / (4.0 * Math.PI));
        }

        /// <summary>The solid angle triangle <paramref name="id"/> subtends at <paramref name="p"/>.</summary>
        private double Exact(int id, Vec3 p)
        {
            Vec3 a = pos[tri[id * 3]], b = pos[tri[id * 3 + 1]], c = pos[tri[id * 3 + 2]];
            double ax = a.X - p.X, ay = a.Y - p.Y, az = a.Z - p.Z;
            double bx = b.X - p.X, by = b.Y - p.Y, bz = b.Z - p.Z;
            double cx = c.X - p.X, cy = c.Y - p.Y, cz = c.Z - p.Z;
            double la = Math.Sqrt(ax * ax + ay * ay + az * az);
            double lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            double lc = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            // Van Oosterom–Strackee: tan(Ω/2) = a·(b×c) / (|a||b||c| + (a·b)|c| + (a·c)|b| + (b·c)|a|)
            double det = ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx);
            double den = la * lb * lc + (ax * bx + ay * by + az * bz) * lc
                       + (ax * cx + ay * cy + az * cz) * lb + (bx * cx + by * cy + bz * cz) * la;
            // For a far triangle det ≈ 2 A·d and den ≈ 4|d|³, so this tends to the A·d/|d|³ used for far cells.
            return 2.0 * Math.Atan2(det, den);
        }

        public bool Inside(Vec3 p) => Winding(p) > 0.5f;
    }

    /// <summary>How many times the slope limit is swept before giving up; one pass propagates one edge, and it stops early once settled.</summary>
    private const int BustSlopePasses = 200;

    /// <summary>
    /// What the bust bridge decided: the fields <see cref="RelaxedNormals"/> needs. The pass moves vertices and never
    /// changes topology, so a vertex keeps its UV coordinate.
    /// </summary>
    internal sealed class BustBridgePlan
    {
        /// <summary>Per-vertex displacement, indexed like the mesh's vertices.</summary>
        public required Vec3[] Delta { get; init; }

        /// <summary>Vertex index -> welded node index.</summary>
        public required int[] NodeOf { get; init; }

        /// <summary>Per-node region weight, 0 where the bridge left it alone.</summary>
        public required float[] NodeWeight { get; init; }

        /// <summary>Per-node normalized average of the members' source normals.</summary>
        public required Vec3[] NodeNormal { get; init; }

        /// <summary>
        /// Nodes on a ring shared with another part, which nothing may move — not the span, and not the extra
        /// clearance <see cref="BridgedClearance"/> spreads from moved neighbours. Null when there are none.
        /// </summary>
        public bool[]? NodePinned { get; init; }

        /// <summary>
        /// Per-node normal the pass already knows the moved surface has, blended over the recomputed one by
        /// <see cref="NormalOverrideWeight"/>. Null when every normal is recomputed from the faces.
        /// </summary>
        public Vec3[]? NormalOverride { get; init; }

        /// <summary>Per-node weight of <see cref="NormalOverride"/>, 0..1.</summary>
        public float[]? NormalOverrideWeight { get; init; }
    }

    /// <summary>
    /// The recomputed normals with a plan's <see cref="BustBridgePlan.NormalOverride"/> blended over them, keeping each
    /// vertex's own facing as <see cref="RelaxedNormals"/> does. Returns <paramref name="normals"/> itself when there is none.
    /// </summary>
    internal static Vec3[] ApplyNormalOverride(BustBridgePlan plan, Vec3[] normals, Vec3[] baseNrm)
    {
        if (plan.NormalOverride is not { } over || plan.NormalOverrideWeight is not { } ow) return normals;
        var outN = (Vec3[])normals.Clone();
        for (int i = 0; i < outN.Length && i < plan.NodeOf.Length; i++)
        {
            int n = plan.NodeOf[i];
            if (n >= ow.Length || ow[n] <= 0f) continue;
            var o = over[n];
            // A back-facing copy of the surface — its own normal against the node's — keeps facing back.
            var src = n < plan.NodeNormal.Length ? plan.NodeNormal[n] : default;
            if (i < baseNrm.Length && src.X * baseNrm[i].X + src.Y * baseNrm[i].Y + src.Z * baseNrm[i].Z < 0f)
                o = new Vec3(-o.X, -o.Y, -o.Z);
            float w = ow[n];
            outN[i] = Normalize(new Vec3(outN[i].X + (o.X - outN[i].X) * w,
                                         outN[i].Y + (o.Y - outN[i].Y) * w,
                                         outN[i].Z + (o.Z - outN[i].Z) * w)) ?? outN[i];
        }
        return outN;
    }

    /// <summary>
    /// Bust bridge: per-vertex displacement that relaxes cloth across the cleavage, so a garment spans between the
    /// breasts instead of sinking into the valley. <paramref name="bust"/> is the bust bones' influence per vertex,
    /// already multiplied by this layer's coverage, so only cloth moves. The surface is a height field along the
    /// chest's outward axis and every node is lifted to the chord between the two apexes in its own row
    /// (<see cref="ChordTarget"/>): a node is only ever lifted, so it cannot enter the body where the chest is a height
    /// field, and the apexes are the chord's endpoints, so the breasts keep their shape. Under an overhang that is not
    /// a guarantee; <see cref="BridgedSkinClearance"/> gives those vertices their clearance back. Vertices are welded
    /// by position first, because a UV seam runs down the sternum. Returns null when the region is too small.
    /// </summary>
    /// <param name="bust">Per-vertex region weight in 0..1, already gated on coverage.</param>
    /// <param name="ramp">
    /// Optional per-vertex 0..1 multiplied into the finished region weight. <paramref name="bust"/> is read as a
    /// boolean seed and <see cref="BustRegionWeights"/> builds a one-ring ramp, which is right for a coverage edge and
    /// wrong for a boundary drawn through open skin, where the displacement must die over centimetres.
    /// </param>
    /// <param name="relaxSeed">
    /// Optional per-vertex 0..1 region for the finishing 3-D relax, separate from the height field's own: a height
    /// field has no leverage on surface that faces across its axis, and a Laplacian does not care which way it faces.
    /// </param>
    internal static BustBridgePlan? BustBridgeSolve(
        Vec3[] pos, Vec3[] nrm, ushort[] tris, float[] bust, float strength,
        Action<string>? log = null, bool[]? covered = null, float smoothStrength = 0f,
        bool fillGap = true, Vec3? outward = null, bool envelope = false,
        float[]? ramp = null, float[]? relaxSeed = null, bool pinBoundary = false, float minDepthShare = 0f,
        bool[]? joinPin = null, float rampFull = 1f, float maxSlope = BustMaxSlope, float openSlope = 0f)
        => BustBridgeSolve(pos, nrm, Array.ConvertAll(tris, t => (int)t), bust, strength, log, covered,
                           smoothStrength, fillGap, outward, envelope, ramp, relaxSeed, pinBoundary, minDepthShare,
                           joinPin, rampFull, maxSlope, openSlope);

    /// <inheritdoc cref="BustBridgeSolve(Vec3[], Vec3[], ushort[], float[], float, Action{string}, bool[])"/>
    /// <remarks>Int indices: a caller working on the whole body concatenates every skin mesh and runs past a ushort.</remarks>
    /// <param name="pinBoundary">
    /// Hold every vertex on an open boundary (the rim of a hole) where it is. For the body passes only: on a body an
    /// open edge is where another part or an authored socket meets. A shell must not pass this — its hem is open edge
    /// along the whole perimeter, and is already held by <paramref name="covered"/>.
    /// </param>
    /// <param name="minDepthShare">
    /// When above zero, skip the solve unless the chord reaches at least this share of the apex gap — see
    /// <see cref="CleftMinDepthShare"/>. Zero for the chest, whose shallow spans are real.
    /// </param>
    /// <param name="joinPin">
    /// Per vertex, whether it sits on a ring this part shares with another part (see <see cref="Source.JoinRing"/>).
    /// Held in place like <paramref name="pinBoundary"/>'s rim, with the span fading to them over
    /// <see cref="JoinPinFade"/> rings. Safe on a shell: it pins only the join, never the hem.
    /// </param>
    internal static BustBridgePlan? BustBridgeSolve(
        Vec3[] pos, Vec3[] nrm, int[] tris, float[] bust, float strength,
        Action<string>? log = null, bool[]? covered = null, float smoothStrength = 0f,
        bool fillGap = true, Vec3? outward = null, bool envelope = false,
        float[]? ramp = null, float[]? relaxSeed = null, bool pinBoundary = false, float minDepthShare = 0f,
        bool[]? joinPin = null, float rampFull = 1f, float maxSlope = BustMaxSlope, float openSlope = 0f)
    {
        return new BridgeSolution(pos, nrm, tris, bust, strength, log, covered, smoothStrength, fillGap, outward, envelope, ramp, relaxSeed, pinBoundary, minDepthShare, joinPin, rampFull, maxSlope, openSlope).Run();
    }

    /// <summary>
    /// Which vertices this layer paints, or null when it paints everything. Sampled nearest per vertex, not through
    /// <see cref="AnyVisible"/>: the outermost ring of cloth vertices comes out uncovered and becomes the pinned
    /// boundary the relax solves against.
    /// </summary>
    internal static bool[]? CoveredVertices((float U, float V)[] uv, SecondSkinLayer layer, int count)
    {
        var mask = layer.Coverage;
        int w = layer.CoverageWidth, h = layer.CoverageHeight;
        if (mask == null || w <= 0 || h <= 0 || mask.Length < w * h) return null;

        var outC = new bool[count];
        int n = Math.Min(count, uv.Length);
        for (int i = 0; i < n; i++)
        {
            int x = ((int)MathF.Floor(uv[i].U * w) % w + w) % w;
            int y = ((int)MathF.Floor(uv[i].V * h) % h + h) % h;
            outC[i] = mask[y * w + x] >= CoverageFloor;
        }
        return outC;
    }

    /// <summary>
    /// The region the bridge may move: the bust plus the gap between its two lobes, faded to zero at its outer edge.
    /// 1 inside, 0 outside. The gap fill is essential: the sternum carries no bust weight, and without it the one
    /// place a bridge exists to span cannot move. The gap is a geodesic ellipse — a node is between the lobes when
    /// its combined graph distance to both is within <see cref="BustGapSlack"/> of the shortest crossing — which
    /// distinguishes "between them" from "near both", unlike a dilation that would swallow the belly. The gap
    /// ignores <paramref name="excluded"/>: between the cups the hem is the top of the span itself. The bone weight
    /// finds the bust and is discarded as a ramp (it is tiny at the midline); the ramp is graph distance to the
    /// region's edge.
    /// </summary>
    /// <param name="fillGap">
    /// Whether the region grows into the gap between its two largest lobes. True for the bust, where the two breast
    /// bones miss the sternum. False for the gluteal cleft: one midline hip bone covers both cheeks and the floor, so
    /// the seed arrives connected and the fill would bridge to an arbitrary scrap.
    /// </param>
    private static float[] BustRegionWeights(bool[] seed, bool[] excluded, List<int>[] adj, int nodeCount,
                                             Action<string>? log, bool fillGap = true)
    {
        // The lobes: connected components of the seed.
        var comp = new int[nodeCount];
        Array.Fill(comp, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (!seed[n] || comp[n] >= 0) continue;
            int id = sizes.Count, size = 0;
            comp[n] = id;
            stack.Push(n);
            while (stack.Count > 0)
            {
                int q = stack.Pop();
                size++;
                if (adj[q] == null) continue;
                foreach (int k in adj[q])
                    if (seed[k] && comp[k] < 0) { comp[k] = id; stack.Push(k); }
            }
            sizes.Add(size);
        }

        var inRegion = (bool[])seed.Clone();
        int gapAdded = 0;

        // Two lobes or more: fill between the two LARGEST. Anything smaller is a stray scrap of weighting,
        // not a breast, and bridging to one would drag the region somewhere arbitrary.
        var largest = Enumerable.Range(0, sizes.Count).OrderByDescending(i => sizes[i]).Take(2).ToList();
        if (!fillGap)
        {
            log?.Invoke($"bust bridge: {sizes.Count} lobe(s) "
                      + $"[{string.Join(", ", sizes.OrderByDescending(s => s).Take(4))}] — "
                      + "seed used as the region, no gap to fill");
        }
        else if (largest.Count == 2)
        {
            var dA = GapDistance(comp, largest[0], seed, adj, nodeCount);
            var dB = GapDistance(comp, largest[1], seed, adj, nodeCount);

            int best = int.MaxValue;
            for (int n = 0; n < nodeCount; n++)
            {
                if (seed[n] || dA[n] < 0 || dB[n] < 0) continue;
                int sum = dA[n] + dB[n];
                if (sum < best) best = sum;
            }
            if (best != int.MaxValue)
                for (int n = 0; n < nodeCount; n++)
                {
                    if (seed[n] || dA[n] < 0 || dB[n] < 0) continue;
                    if (dA[n] + dB[n] > best + BustGapSlack) continue;
                    inRegion[n] = true;
                    gapAdded++;
                }
            log?.Invoke($"bust bridge: {sizes.Count} lobe(s) "
                      + $"[{string.Join(", ", sizes.OrderByDescending(s => s).Take(4))}], "
                      + $"{gapAdded} node(s) added across the gap (shortest crossing {best} edges)");
        }
        else
        {
            log?.Invoke($"bust bridge: {sizes.Count} lobe(s) — no gap to fill");
        }

        // The ramp: graph distance inward from the region's edge, so the bridge fades into the untouched
        // shell instead of ending in a crease.
        var depth = new int[nodeCount];
        Array.Fill(depth, -1);
        var queue = new Queue<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (inRegion[n] || adj[n] == null) continue;
            foreach (int k in adj[n])
                if (inRegion[k] && depth[k] < 0) { depth[k] = 1; queue.Enqueue(k); }
        }
        while (queue.Count > 0)
        {
            int q = queue.Dequeue();
            if (depth[q] >= BustFadeSteps + 1 || adj[q] == null) continue;
            foreach (int k in adj[q])
                if (inRegion[k] && depth[k] < 0) { depth[k] = depth[q] + 1; queue.Enqueue(k); }
        }

        var w = new float[nodeCount];
        for (int n = 0; n < nodeCount; n++)
        {
            if (!inRegion[n]) continue;
            // depth < 0 means the ramp never reached it — deep inside, or a region with no outside at all
            // (a mesh entirely covered by the bust, which no body has). Full weight either way.
            w[n] = depth[n] < 0 ? 1f : MathF.Min(1f, depth[n] / (float)(BustFadeSteps + 1));
        }
        return w;
    }

    /// <summary>
    /// Breadth-first edge distance from one component of <paramref name="seed"/> to every node OUTSIDE the
    /// seed. -1 for anything unreached. Seeded from the component's own nodes at distance 0 and never
    /// travelling back through the seed, so the numbers describe the gap and not a walk over the bust.
    /// </summary>
    private static int[] GapDistance(int[] comp, int id, bool[] seed, List<int>[] adj, int nodeCount)
    {
        var d = new int[nodeCount];
        Array.Fill(d, -1);
        var queue = new Queue<int>();
        for (int n = 0; n < nodeCount; n++)
        {
            if (comp[n] != id || adj[n] == null) continue;
            foreach (int k in adj[n])
                if (!seed[k] && d[k] < 0) { d[k] = 1; queue.Enqueue(k); }
        }
        while (queue.Count > 0)
        {
            int q = queue.Dequeue();
            if (d[q] >= BustGapMaxSteps || adj[q] == null) continue;
            foreach (int k in adj[q])
                if (!seed[k] && d[k] < 0) { d[k] = d[q] + 1; queue.Enqueue(k); }
        }
        return d;
    }

    /// <summary>
    /// How far a bridged shell stands off the skin, as a body-UV map: 0 where the cloth lies on the body, 255 where it
    /// has lifted <paramref name="fullAt"/> or more. Null when this body has no bust bones, nothing is covered, or the
    /// bridge would not fire. The skin bake runs before the shell and must not dent the skin under a spanned
    /// cleavage, so the same <see cref="BustBridgeSolve"/> the shell uses is run here over the body models.
    /// </summary>
    /// <param name="fullAt">Lift at which suppression is total, in model units.</param>
    internal static byte[]? BustStandoffMap(IReadOnlyList<byte[]> bodies, byte[]? coverage, int covW, int covH,
                                           float strength, int size, float fullAt, Action<string>? log = null)
    {
        if (bodies.Count == 0 || size <= 0 || strength <= 0f || fullAt <= 0f) return null;

        byte[]? map = null;
        foreach (var mdl in bodies)
        {
            if (mdl is not { Length: > 0 }) continue;
            if (!TryReadLod0Geometry(mdl, out var fPos, out var fUv, out var fTri, out var fW, out var fNrm))
                continue;
            int vc = fPos.Length / 3;
            if (vc == 0 || fTri.Length < 3) continue;

            var bust = new float[vc];
            bool anyBust = false;
            for (int i = 0; i < vc; i++)
            {
                float acc = 0f;
                foreach (var (bone, bw) in fW[i])
                    if (IsBustBone(bone))
                        acc += bw;
                if (acc <= 0f) continue;
                bust[i] = MathF.Min(1f, acc);
                anyBust = true;
            }
            if (!anyBust) continue;

            // Sampled with wrap, like every other body-UV read here: a body's UVs need not live in the [0,1] tile.
            bool[]? covered = null;
            if (coverage != null && covW > 0 && covH > 0 && coverage.Length >= covW * covH)
            {
                covered = new bool[vc];
                for (int i = 0; i < vc; i++)
                {
                    int x = ((int)MathF.Floor(fUv[i * 2] * covW) % covW + covW) % covW;
                    int y = ((int)MathF.Floor(fUv[i * 2 + 1] * covH) % covH + covH) % covH;
                    covered[i] = coverage[y * covW + x] >= CoverageFloor;
                }
            }

            var p3 = new Vec3[vc];
            var n3 = new Vec3[vc];
            for (int i = 0; i < vc; i++)
            {
                p3[i] = new Vec3(fPos[i * 3], fPos[i * 3 + 1], fPos[i * 3 + 2]);
                n3[i] = new Vec3(fNrm[i * 3], fNrm[i * 3 + 1], fNrm[i * 3 + 2]);
            }

            var plan = BustBridgeSolve(p3, n3, fTri, bust, strength, log, covered);
            if (plan == null) continue;

            var lift = new float[vc];
            for (int i = 0; i < vc; i++)
            {
                var d = plan.Delta[i];
                lift[i] = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            }

            map ??= new byte[size * size];
            // Interpolated across the triangle, not the max of its corners: the corner max makes the map's boundary follow
            // the body's triangle edges rather than the contour of the lift. Sub-texel triangles keep the conservative
            // footprint fill, or a mesh finer than the map leaves a dotted mask (see CapFootprintMask).
            for (int t = 0; t + 2 < fTri.Length; t += 3)
            {
                int a = fTri[t], b = fTri[t + 1], c = fTri[t + 2];
                if (a >= vc || b >= vc || c >= vc) continue;
                if (MathF.Max(lift[a], MathF.Max(lift[b], lift[c])) <= BustBridgeEpsilon) continue;

                float ax = fUv[a * 2] * size, ay = fUv[a * 2 + 1] * size;
                float bx = fUv[b * 2] * size, by = fUv[b * 2 + 1] * size;
                float cx = fUv[c * 2] * size, cy = fUv[c * 2 + 1] * size;
                int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)));
                int x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
                int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)));
                int y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 18) continue;   // straddles a UV seam

                float det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                bool tiny = x1 - x0 <= 2 && y1 - y0 <= 2;
                byte flat = (byte)Math.Clamp(
                    MathF.Round(MathF.Max(lift[a], MathF.Max(lift[b], lift[c])) / fullAt * 255f), 0f, 255f);

                for (int y = y0; y <= y1; y++)
                {
                    int wy = (y % size + size) % size;
                    for (int x = x0; x <= x1; x++)
                    {
                        byte v;
                        if (tiny || MathF.Abs(det) < 1e-9f) v = flat;
                        else
                        {
                            float px = x + 0.5f, py = y + 0.5f;
                            float l0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / det;
                            float l1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / det;
                            float l2 = 1f - l0 - l1;
                            // A small negative margin keeps neighbouring triangles from leaving a seam of
                            // untouched texels between them; the values agree along a shared edge anyway.
                            if (l0 < -0.02f || l1 < -0.02f || l2 < -0.02f) continue;
                            float m = l0 * lift[a] + l1 * lift[b] + l2 * lift[c];
                            v = (byte)Math.Clamp(MathF.Round(m / fullAt * 255f), 0f, 255f);
                        }
                        if (v == 0) continue;
                        int p = wy * size + (x % size + size) % size;
                        if (map[p] < v) map[p] = v;
                    }
                }
            }
        }
        return map;
    }

    /// <summary>
    /// The height each node should reach: the chord between the furthest-forward point of each breast, row by row
    /// across the chest, never below where the node already is. A construction, not an iteration: a cleavage is a
    /// saddle, so an isotropic relax sees its curvatures cancel, and the row's answer is known in closed form. Taken
    /// directly, the span is exactly straight, the apexes are its endpoints and cannot move, and no node is placed
    /// below where it started. Rows are bands of <paramref name="ver"/> about one mesh edge tall, and a node reads
    /// the chord interpolated between the two nearest band centres. Outside the two apexes nothing moves.
    /// </summary>
    private static float[] ChordTarget(float[] h0, float[] lat, float[] ver, float[] w, int count,
                                       List<int>[] adj, Vec3[] pos, Action<string>? log)
    {
        return new ChordSolution(h0, lat, ver, w, count, adj, pos, log).Run();
    }

    /// <summary>
    /// Rings out from the rim of a hole over which the relax fades back in; the rim itself is held exactly. About
    /// the scale of the hole itself, which is what a fade needs to be invisible.
    /// </summary>
    private const int RimPinFade = 3;

    /// <summary>
    /// Rings over which a span fades to zero at a join with another part. Wider than <see cref="RimPinFade"/> because
    /// it tapers the span, which can be centimetres; the slope limit would otherwise do the tapering alone, as a crease.
    /// </summary>
    private const int JoinPinFade = 6;

    /// <summary>
    /// Rounds of the finishing Laplacian. Longer than the nipple's: under the crotch it is the only operator acting,
    /// so it has to take out the fold's own relief and not merely vertex noise. Past convergence the pass stops buying
    /// smoothness and starts spending shape.
    /// </summary>
    private const int FoldRelaxPasses = 60;

    /// <summary>How far each relax pass moves a node toward its neighbours' centroid. Under-relaxed: at 1 a Jacobi
    /// step oscillates on the checkerboard mode instead of converging.</summary>
    private const float FoldRelaxLambda = 0.5f;

    /// <summary>
    /// How far a node must move before its normal is fully recomputed rather than kept, as a fraction of the distance
    /// to its neighbours. Half an edge is a different surface; a hundredth of one still fits the artist's normal.
    /// </summary>
    private const float NormalReshadeSpan = 0.5f;

    /// <summary>
    /// The most two neighbouring nodes' free 3-D displacements may differ, per unit of the edge between them. A
    /// smoothness setting, not a safety one — <see cref="UnfoldTriangles"/> owns safety by checking. A relax works by
    /// making neighbouring displacements differ, so too tight a limit fights the operator.
    /// </summary>
    private const float FoldRelaxMaxSlope = 1.0f;

    /// <summary>
    /// The distance between the region's two apexes — the node standing furthest out along
    /// <paramref name="ax"/> on each side of its lateral midpoint — measured across the axis. The same apexes
    /// <see cref="ChordReport"/> reports. Zero when the region has no node on one side.
    /// </summary>
    private static float ApexGap(Vec3[] start, float[] h0, float[] w, int count, Vec3 ax, Vec3 lateral)
    {
        var mid = default(Vec3);
        float wsum = 0f;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            mid = new Vec3(mid.X + start[n].X, mid.Y + start[n].Y, mid.Z + start[n].Z);
            wsum++;
        }
        if (wsum < 2f) return 0f;
        mid = new Vec3(mid.X / wsum, mid.Y / wsum, mid.Z / wsum);

        int apexL = -1, apexR = -1;
        float bestL = float.MinValue, bestR = float.MinValue;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            float l = (start[n].X - mid.X) * lateral.X + (start[n].Y - mid.Y) * lateral.Y + (start[n].Z - mid.Z) * lateral.Z;
            if (l >= 0f) { if (h0[n] > bestR) { bestR = h0[n]; apexR = n; } }
            else         { if (h0[n] > bestL) { bestL = h0[n]; apexL = n; } }
        }
        if (apexL < 0 || apexR < 0) return 0f;

        var d = new Vec3(start[apexR].X - start[apexL].X, start[apexR].Y - start[apexL].Y, start[apexR].Z - start[apexL].Z);
        float along = d.X * ax.X + d.Y * ax.Y + d.Z * ax.Z;
        var across = new Vec3(d.X - ax.X * along, d.Y - ax.Y * along, d.Z - ax.Z * along);
        return MathF.Sqrt(across.X * across.X + across.Y * across.Y + across.Z * across.Z);
    }

    /// <summary>
    /// How deep the chord has to reach, as a share of the apex gap, before a cleft solve is taken as a real cleft.
    /// The solve runs once per source mesh and the hip bone reaches the bottom of the torso mesh too, where it would
    /// span a shallow ridge at the waist and step against the legs mesh. A width share does not separate the two.
    /// </summary>
    internal const float CleftMinDepthShare = 0.4f;

    private static string ChordReport(Vec3[] start, float[] h0, float[] h, float[] w, int count,
                                      Vec3 ax, Vec3 lateral)
    {
        // Lateral direction: the widest spread of the region, taken across the outward axis. On a chest
        // that is left-to-right, and it is derived the same way the axis is.
        var mid = default(Vec3);
        float wsum = 0f;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            mid = new Vec3(mid.X + start[n].X, mid.Y + start[n].Y, mid.Z + start[n].Z);
            wsum++;
        }
        if (wsum < 2f) return "";
        mid = new Vec3(mid.X / wsum, mid.Y / wsum, mid.Z / wsum);

        int apexL = -1, apexR = -1;
        float bestL = float.MinValue, bestR = float.MinValue;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            var d = new Vec3(start[n].X - mid.X, start[n].Y - mid.Y, start[n].Z - mid.Z);
            float lat = d.X * lateral.X + d.Y * lateral.Y + d.Z * lateral.Z;
            if (lat >= 0f) { if (h0[n] > bestR) { bestR = h0[n]; apexR = n; } }
            else           { if (h0[n] > bestL) { bestL = h0[n]; apexL = n; } }
        }
        if (apexL < 0 || apexR < 0) return "";

        // The dish along the chord, before and after. Which nodes count is decided across the axis only: their depth
        // along it is the thing being measured, so letting it into the test excludes exactly the nodes to look at.
        Vec3 Across(Vec3 p)
        {
            float along = p.X * ax.X + p.Y * ax.Y + p.Z * ax.Z;
            return new Vec3(p.X - ax.X * along, p.Y - ax.Y * along, p.Z - ax.Z * along);
        }

        var pl = start[apexL];
        var pr = start[apexR];
        var seg = Across(new Vec3(pr.X - pl.X, pr.Y - pl.Y, pr.Z - pl.Z));
        float span = seg.X * seg.X + seg.Y * seg.Y + seg.Z * seg.Z;
        if (span <= 1e-12f) return "";
        float band = ChordBand * ChordBand * span;

        // Mean and worst: the worst alone can be held up by a single node at the edge of the band.
        float wasMax = 0f, nowMax = 0f;
        double wasSum = 0, nowSum = 0;
        int sampled = 0;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            var d = Across(new Vec3(start[n].X - pl.X, start[n].Y - pl.Y, start[n].Z - pl.Z));
            float t = (d.X * seg.X + d.Y * seg.Y + d.Z * seg.Z) / span;
            if (t <= 0f || t >= 1f) continue;
            // How far off the apex-to-apex line it sits, across the axis — on a chest, how far above or
            // below the nipple line. Further off than the band and the chord says nothing about it.
            var perp = new Vec3(d.X - seg.X * t, d.Y - seg.Y * t, d.Z - seg.Z * t);
            if (perp.X * perp.X + perp.Y * perp.Y + perp.Z * perp.Z > band) continue;
            float chord = h0[apexL] + (h0[apexR] - h0[apexL]) * t;
            float a = MathF.Max(0f, chord - h0[n]), b = MathF.Max(0f, chord - h[n]);
            wasMax = MathF.Max(wasMax, a); nowMax = MathF.Max(nowMax, b);
            wasSum += a; nowSum += b;
            sampled++;
        }
        // How far apart the two apexes sit as a share of the region's own width across — a real cleavage or
        // cleft keeps its lobes a good fraction of the region apart; a solve on a narrow ridge does not.
        float latMin = float.MaxValue, latMax = float.MinValue;
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            float l = start[n].X * lateral.X + start[n].Y * lateral.Y + start[n].Z * lateral.Z;
            latMin = MathF.Min(latMin, l); latMax = MathF.Max(latMax, l);
        }
        float width = latMax - latMin;
        string where = $", apexes ({pl.X:0.###},{pl.Y:0.###},{pl.Z:0.###})-({pr.X:0.###},{pr.Y:0.###},{pr.Z:0.###})"
                     + $" gap {MathF.Sqrt(span):0.####} of a region {width:0.####} wide"
                     + (width > 0f ? $" (share {MathF.Sqrt(span) / width:0.##})" : "");
        if (sampled == 0) return where + ", nothing on the chord to measure";
        return where + $", dish mean {wasSum / sampled:0.#####} -> {nowSum / sampled:0.#####}"
                     + $", worst {wasMax:0.#####} -> {nowMax:0.#####}, over {sampled} node(s)";
    }

    /// <summary>
    /// How far off the apex-to-apex segment a node may be and still count as part of the cleavage —
    /// measured ACROSS the chest axis, as a fraction of the segment's length. Only used for reporting.
    /// </summary>
    private const float ChordBand = 0.12f;

    /// <summary>
    /// How many times the recomputed normal field is averaged over the welded-node graph before it is written; see
    /// <see cref="RelaxedNormals"/>. Without it the geometry pass shades rougher than the untouched body. Sixteen is
    /// past the knee of the curve without being at the floor, where shading goes waxy. It only ever changes shading;
    /// no vertex moves because of it.
    /// </summary>
    internal const int NormalSmoothPasses = 16;
}
