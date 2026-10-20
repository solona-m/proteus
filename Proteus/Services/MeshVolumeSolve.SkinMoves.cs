using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// "Move skin too": the brushes and the Move tools moving the garment's own body skin, and the cloth lying on it riding
/// along (<see cref="SkinFollow"/>). Off, none of this runs and the solve is exactly the one that never moves skin.
/// <para/>
/// Every rule here is gated on <see cref="opSkin"/>, which an operation captures once when it begins — the first dab of
/// a stroke, <see cref="BeginMove"/>, <see cref="BeginGrab"/> — and never on whether a binding happens to be attached:
/// turning the checkbox off after using it must give back today's brush exactly.
/// </summary>
internal sealed partial class MeshVolumeSolve
{
    /// <summary>
    /// Whether the next operation may move skin. Read when an operation begins, so changing it mid-stroke or mid-drag
    /// changes the next one. Has no effect until a binding is attached (<see cref="AttachFollow"/>), and none on the
    /// bridge or wind brushes, which never move skin.
    /// </summary>
    public bool MoveSkin { get; set; }

    /// <summary>The operation under way moves skin: <see cref="MoveSkin"/> was on and a binding was ready when it began.</summary>
    private bool opSkin;

    private SkinFollow? follow;

    /// <summary>Per node, how much of a brush the skin there may take — see <see cref="SkinFollow.Seams"/>.</summary>
    private float[]? seamWeight;

    /// <summary>Per skin node, the displacement its riders have already been carried to.</summary>
    private Vec3[]? skinCarried;

    /// <summary>
    /// Skin has been moved and a binding is attached: from then on cloth riding it is kept above it where it now is,
    /// whatever the operation. Refreshed by every settle. False while skin never moved, so the brush is today's.
    /// </summary>
    private bool skinMoved;

    /// <summary>The model this solve was built over, which a binding reads.</summary>
    private readonly ModelParts sourceModel;

    /// <summary>A binding is attached, so skin may move.</summary>
    public bool FollowReady => follow != null;

    /// <summary>What <see cref="SkinFollow.Bind"/> needs, safe to hand to a worker: nothing in it ever changes.</summary>
    internal SkinFollow.Inputs FollowInputs => new(sourceModel, nodeOf, nodeCount, skin, MeanEdge, this);

    /// <summary>
    /// Hand over the binding a worker built. One built for another solve — the model was reopened meanwhile — is
    /// ignored. Safe at any time: an operation already under way keeps what it began with (<see cref="opSkin"/>).
    /// </summary>
    /// <returns>Whether it was taken.</returns>
    public bool AttachFollow(SkinFollow binding)
    {
        if (!ReferenceEquals(binding.Owner, this)) return false;
        follow = binding;
        return true;
    }

    /// <summary>Any skin node carries a displacement.</summary>
    public bool SkinEdited
    {
        get
        {
            for (int n = 0; n < nodeCount; n++)
                if (skin[n] && nodeDelta[n] != default) return true;
            return false;
        }
    }

    /// <summary>How much of a brush the skin at vertex <paramref name="vertex"/> may take — see <see cref="SkinFollow.Seams"/>.</summary>
    internal float SeamWeightAt(int vertex) => Seams()[nodeOf[vertex]];

    private float[] Seams() => seamWeight ??= SkinFollow.Seams(nodeCount, skin, adj, nodeTris, nodeAt);

    /// <summary>
    /// An operation begins: capture whether it moves skin. The bridge and the wind brush pass false.
    /// </summary>
    private void BeginOp(bool skinMay = true)
    {
        opSkin = skinMay && MoveSkin && follow != null;
        if (!opSkin) return;
        Seams();
        // Riders are carried by how far their skin moves from here on.
        skinCarried ??= new Vec3[nodeCount];
        for (int n = 0; n < nodeCount; n++)
            if (skin[n]) skinCarried[n] = nodeDelta[n];
    }

    /// <summary>Node <paramref name="n"/> may not move in the operation under way: locked, or skin when it moves none.</summary>
    private bool Held(int n) => locked[n] || (skin[n] && !opSkin);

    /// <summary><see cref="Held"/> for a question asked outside an operation — the move falloff the view previews.</summary>
    private bool Held(int n, bool skinMoves) => locked[n] || (skin[n] && !skinMoves);

    /// <summary>Cloth that rides the skin in the operation under way.</summary>
    private bool IsRider(int n) => opSkin && !skin[n] && follow!.BindWeight(n) > 0f;

    /// <summary>
    /// How much of its skin's movement rider <paramref name="n"/> takes in this operation: its bind weight times the
    /// share of its corners that may move here — not locked, and not held by an open edge. Cloth over skin that stays
    /// is brushed as cloth.
    /// </summary>
    private float RiderShare(int n)
    {
        if (!IsRider(n)) return 0f;
        float share = 0f;
        foreach (var (s, w) in follow!.CornersOf(n))
            if (!locked[s]) share += w * seamWeight![s];
        return follow.BindWeight(n) * share;
    }

    /// <summary>
    /// What a brush's weight at node <paramref name="n"/> is scaled by: 1 outside a skin-moving operation; the open-edge
    /// fade on skin; and on a rider whatever share it does not take from its skin, which brings the rest.
    /// </summary>
    private float OpScale(int n)
    {
        if (!opSkin) return 1f;
        if (skin[n]) return seamWeight![n];
        return 1f - RiderShare(n);
    }

    /// <summary>
    /// Carry every rider of these skin nodes by however far the skin moved since they were last carried, recording each
    /// rider in <paramref name="into"/> before its first change so undo takes it back with the skin.
    /// </summary>
    private void CarryRiders(IEnumerable<int> skinNodes, Dictionary<int, Was> into)
    {
        if (!opSkin) return;
        foreach (int s in skinNodes)
        {
            if (!skin[s]) continue;
            var now = nodeDelta[s];
            var was = skinCarried![s];
            if (now == was) continue;
            var d = new Vec3(now.X - was.X, now.Y - was.Y, now.Z - was.Z);
            skinCarried[s] = now;
            foreach (var (r, w) in follow!.RidersOf(s))
            {
                if (locked[r]) continue;
                into.TryAdd(r, Snapshot(r));
                nodeDelta[r] = new Vec3(nodeDelta[r].X + d.X * w, nodeDelta[r].Y + d.Y * w, nodeDelta[r].Z + d.Z * w);
                // The rider's normals turn with it, as far as it follows the skin.
                nodeWeight[r] = MathF.Max(nodeWeight[r], follow.BindWeight(r) * nodeWeight[s]);
            }
        }
    }

    /// <summary>
    /// Settle a stroke that moved skin, in the order that keeps the cloth on the body: the skin and loose cloth are
    /// unfolded first, the riders then take the skin's FINAL change (carried before, they would float where the
    /// unfold took a fold of skin back), are unfolded themselves against where the stroke found them, and the guard
    /// runs last (<see cref="KeepRidersAboveSkin"/>).
    /// </summary>
    private void SettleSkinStroke(Dictionary<int, Was> start, bool allowCollapse)
    {
        var core = new Dictionary<int, Was>();
        foreach (var (n, was) in start)
            if (!IsRider(n)) core[n] = was;
        if (core.Count > 0) UnfoldStroke(core, allowCollapse);

        CarryRiders(core.Keys, start);

        var ride = new Dictionary<int, Was>();
        foreach (var (n, was) in start)
            if (IsRider(n)) ride[n] = was;
        if (ride.Count > 0) UnfoldStroke(ride, allowCollapse);

        KeepRidersAboveSkin(start);
    }

    /// <summary>
    /// The point of the skin rider <paramref name="n"/> is bound to, and the skin's way out there: as authored, or with
    /// <paramref name="now"/> where the skin now stands, its authored normal turned as far as the triangle has.
    /// </summary>
    private (Vec3 Point, Vec3 Normal) SkinUnder(int n, bool now)
    {
        var corners = follow!.CornersOf(n);
        float px = 0f, py = 0f, pz = 0f, nx = 0f, ny = 0f, nz = 0f;
        foreach (var (s, w) in corners)
        {
            var at = now ? Here(s) : nodeAt[s];
            px += at.X * w; py += at.Y * w; pz += at.Z * w;
            nx += nodeNormal[s].X * w; ny += nodeNormal[s].Y * w; nz += nodeNormal[s].Z * w;
        }
        var normal = Unit(new Vec3(nx, ny, nz));
        if (now && corners.Count == 3)
        {
            var authored = MeshMath.TriNormal(nodeAt[corners[0].Skin], nodeAt[corners[1].Skin], nodeAt[corners[2].Skin]);
            var current = MeshMath.TriNormal(Here(corners[0].Skin), Here(corners[1].Skin), Here(corners[2].Skin));
            normal = Unit(LinearNormals.Turn(authored, current, normal));
        }
        return (new Vec3(px, py, pz), normal);
    }

    /// <summary>
    /// A rider's floor during a skin-moving relax or smooth: <see cref="SkinFloorGap"/> above the skin it is bound to
    /// as that skin now stands, or its authored clearance where the author had it closer. Unbound cloth keeps the
    /// authored skin's floor — see <see cref="KeepAboveSkin"/>.
    /// </summary>
    private bool KeepAboveBoundSkin(int n, Vec3 delta, out Vec3 kept)
    {
        kept = delta;
        if (follow == null || skin[n] || follow.BindWeight(n) <= 0f) return false;
        var (q, nrm) = SkinUnder(n, now: true);
        if (nrm == default) return false;
        var p = new Vec3(nodeAt[n].X + delta.X, nodeAt[n].Y + delta.Y, nodeAt[n].Z + delta.Z);
        float h = (p.X - q.X) * nrm.X + (p.Y - q.Y) * nrm.Y + (p.Z - q.Z) * nrm.Z;
        float floor = MathF.Min(SkinFloorGap, follow.AuthoredHeight(n));
        if (h < floor)
        {
            float lift = floor - h;
            kept = new Vec3(delta.X + nrm.X * lift, delta.Y + nrm.Y * lift, delta.Z + nrm.Z * lift);
        }
        return true;
    }

    /// <summary>
    /// The guard at the end of a skin-moving operation, over the cloth that rode or was brushed in it only — never the
    /// whole model, whose clips are the author's. Two checks, as a game shows a poke two ways:
    /// <list type="bullet">
    /// <item>a rider below the skin it is bound to (<see cref="KeepAboveBoundSkin"/>) is lifted to its floor;</item>
    /// <item>moved skin standing further outside a rider cloth face than the author had it — between the cloth's
    /// vertices, where no vertex check sees it (<see cref="SkinThroughCloth"/>) — lifts that face's riders by the
    /// excess, each by its share of the nearest point.</item>
    /// </list>
    /// </summary>
    /// <param name="start">The operation's undo record: only riders in it move, so undo stays exact.</param>
    private void KeepRidersAboveSkin(Dictionary<int, Was> start)
    {
        if (!opSkin) return;
        var riders = new List<int>();
        var movedSkin = new List<int>();
        foreach (var (n, was) in start)
        {
            if (skin[n]) { if (nodeDelta[n] != was.Delta) movedSkin.Add(n); }
            else if (!locked[n] && follow!.BindWeight(n) > 0f) riders.Add(n);
        }
        if (riders.Count == 0 || movedSkin.Count == 0) return;

        foreach (int r in riders)
            if (KeepAboveBoundSkin(r, nodeDelta[r], out var kept)) nodeDelta[r] = kept;

        for (int pass = 0; pass < FacePasses; pass++)
            if (LiftFacesOverSkin(start, riders, movedSkin) == 0) break;
    }

    /// <summary>The face check gives up after this many passes; what is left is logged, not chased.</summary>
    private const int FacePasses = 2;

    /// <summary>Faces still showing skin after the last <see cref="KeepRidersAboveSkin"/>, for the log.</summary>
    public int FacesStillOutside { get; private set; }

    /// <summary>One pass of the face check in <see cref="KeepRidersAboveSkin"/>.</summary>
    /// <returns>How many faces it lifted.</returns>
    private int LiftFacesOverSkin(Dictionary<int, Was> start, List<int> riders, List<int> movedSkin)
    {
        const float reach = SkinThroughCloth.Reach, tolerance = SkinThroughCloth.Tolerance;
        var isRider = new HashSet<int>(riders);

        // Cloth faces with a rider corner, bucketed by space.
        (int, int, int) CellOf(Vec3 p)
            => ((int)MathF.Floor(p.X / reach), (int)MathF.Floor(p.Y / reach), (int)MathF.Floor(p.Z / reach));
        var faces = new List<int>();
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int t = 0; t + 2 < nodeTris.Length; t += 3)
        {
            int a = nodeTris[t], b = nodeTris[t + 1], c = nodeTris[t + 2];
            if (skin[a] || skin[b] || skin[c]) continue;
            if (!isRider.Contains(a) && !isRider.Contains(b) && !isRider.Contains(c)) continue;
            int f = faces.Count;
            faces.Add(t);
            var pa = Here(a); var pb = Here(b); var pc = Here(c);
            var lo = CellOf(new Vec3(MathF.Min(pa.X, MathF.Min(pb.X, pc.X)) - reach, MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y)) - reach,
                                     MathF.Min(pa.Z, MathF.Min(pb.Z, pc.Z)) - reach));
            var hi = CellOf(new Vec3(MathF.Max(pa.X, MathF.Max(pb.X, pc.X)) + reach, MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y)) + reach,
                                     MathF.Max(pa.Z, MathF.Max(pb.Z, pc.Z)) + reach));
            for (int x = lo.Item1; x <= hi.Item1; x++)
            for (int y = lo.Item2; y <= hi.Item2; y++)
            for (int z = lo.Item3; z <= hi.Item3; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = [];
                list.Add(f);
            }
        }
        if (faces.Count == 0) return 0;

        var hem = HemEdges();
        int lifted = 0, outside = 0;
        foreach (int s in movedSkin)
        {
            var sp = ToVector(Here(s));
            if (!grid.TryGetValue(CellOf(Here(s)), out var near)) continue;

            int best = -1;
            float bestD = reach;
            Vector3 bestQ = default;
            float bu = 0f, bv = 0f, bw = 0f;
            foreach (int f in near)
            {
                int t = faces[f];
                var q = BrushTransfer.ClosestOnTriangle(sp, ToVector(Here(nodeTris[t])), ToVector(Here(nodeTris[t + 1])),
                                                        ToVector(Here(nodeTris[t + 2])), out float u, out float v, out float w);
                float d = Vector3.Distance(sp, q);
                if (d >= bestD) continue;
                bestD = d; best = f; bestQ = q; bu = u; bv = v; bw = w;
            }
            if (best < 0) continue;

            int ft = faces[best];
            int ca = nodeTris[ft], cb = nodeTris[ft + 1], cc = nodeTris[ft + 2];
            // Past a hem is not through anything — as SkinThroughCloth judges it.
            const float onEdge = 0.02f;
            if ((bu < onEdge && hem.Contains(EdgeKey(cb, cc))) || (bv < onEdge && hem.Contains(EdgeKey(cc, ca)))
                || (bw < onEdge && hem.Contains(EdgeKey(ca, cb))))
                continue;

            var face = FaceOut(Here(ca), Here(cb), Here(cc), ca, cb, cc);
            if (face == Vector3.Zero) continue;
            float by = Vector3.Dot(sp - bestQ, face);

            // The author's own: how far outside the same face this skin point stood as authored.
            var aq = BrushTransfer.ClosestOnTriangle(ToVector(nodeAt[s]), ToVector(nodeAt[ca]), ToVector(nodeAt[cb]),
                                                     ToVector(nodeAt[cc]), out _, out _, out _);
            var authoredFace = FaceOut(nodeAt[ca], nodeAt[cb], nodeAt[cc], ca, cb, cc);
            float authored = Vector3.Distance(ToVector(nodeAt[s]), aq) <= reach && authoredFace != Vector3.Zero
                ? Vector3.Dot(ToVector(nodeAt[s]) - aq, authoredFace)
                : 0f;

            float excess = by - MathF.Max(tolerance, authored);
            if (excess <= 0f) continue;

            // Only riders this operation recorded may move. Lifted by λ·share each, the face's point there rises
            // λ·Σshare², so that is what the excess is divided by.
            var corners = new[] { (ca, bu), (cb, bv), (cc, bw) };
            float sq = 0f;
            foreach (var (c, share) in corners)
                if (isRider.Contains(c) && start.ContainsKey(c) && share > 0f) sq += share * share;
            bool any = false;
            foreach (var (c, share) in corners)
            {
                if (!isRider.Contains(c) || !start.ContainsKey(c) || share <= 0f || sq < 1e-4f) continue;
                // Never more than a few times the excess: a corner with a sliver of the share would be thrown off.
                float lift = MathF.Min(excess * share / sq, 3f * excess);
                nodeDelta[c] = new Vec3(nodeDelta[c].X + face.X * lift, nodeDelta[c].Y + face.Y * lift,
                                        nodeDelta[c].Z + face.Z * lift);
                any = true;
            }
            if (any) lifted++;
            else outside++;
        }
        FacesStillOutside = outside;
        return lifted;
    }

    /// <summary>A face's way out: its own normal, turned to agree with its corners' authored normals.</summary>
    private Vector3 FaceOut(Vec3 a, Vec3 b, Vec3 c, int na, int nb, int nc)
    {
        var f = Vector3.Cross(ToVector(b) - ToVector(a), ToVector(c) - ToVector(a));
        if (f.LengthSquared() < 1e-14f) return Vector3.Zero;
        f = Vector3.Normalize(f);
        var sum = ToVector(nodeNormal[na]) + ToVector(nodeNormal[nb]) + ToVector(nodeNormal[nc]);
        return Vector3.Dot(f, sum) < 0f ? -f : f;
    }

    private HashSet<(int, int)>? hemEdges;

    /// <summary>Edges only one cloth face uses: a hem.</summary>
    private HashSet<(int, int)> HemEdges()
    {
        if (hemEdges != null) return hemEdges;
        var uses = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < nodeTris.Length; t += 3)
        {
            int a = nodeTris[t], b = nodeTris[t + 1], c = nodeTris[t + 2];
            if (skin[a] || skin[b] || skin[c]) continue;
            foreach (var key in new[] { EdgeKey(a, b), EdgeKey(b, c), EdgeKey(c, a) })
                uses[key] = uses.GetValueOrDefault(key) + 1;
        }
        hemEdges = [];
        foreach (var (key, count) in uses)
            if (count < 2) hemEdges.Add(key);
        return hemEdges;
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static Vector3 ToVector(Vec3 v) => new(v.X, v.Y, v.Z);

    // ── grab ────────────────────────────────────────────────────────────────

    /// <summary>Riders of the skin a skin-moving grab holds; empty otherwise.</summary>
    private readonly List<int> grabRiders = [];

    /// <summary>Gather the riders of every skin node the grab holds, recorded for undo — see <see cref="BeginGrab"/>.</summary>
    private void CollectGrabRiders()
    {
        grabRiders.Clear();
        if (!opSkin) return;
        var seen = new HashSet<int>();
        foreach (var g in grabTerms)
        {
            if (!skin[g.Node]) continue;
            foreach (var (r, _) in follow!.RidersOf(g.Node))
            {
                if (locked[r] || !seen.Add(r)) continue;
                grabRiders.Add(r);
                stroke!.TryAdd(r, Snapshot(r));
                float b = follow.BindWeight(r);
                if (b > grabWeights.GetValueOrDefault(r)) grabWeights[r] = b;
            }
        }
    }

    /// <summary>
    /// After <see cref="GrabTo"/> placed every node it holds: each rider takes its skin's displacement since the grab
    /// began on top of its own share. Absolute, from the undo record, so the drag cannot creep.
    /// </summary>
    private void CarryGrabRiders(HashSet<int> placed)
    {
        foreach (int r in grabRiders)
        {
            var was = stroke![r];
            var own = placed.Contains(r) ? nodeDelta[r] : was.Delta;
            var ownMoved = placed.Contains(r) ? nodeMoved[r] : was.Moved;
            float cx = 0f, cy = 0f, cz = 0f;
            foreach (var (s, share) in follow!.CornersOf(r))
            {
                if (!stroke.TryGetValue(s, out var sw)) continue;
                float w = follow.BindWeight(r) * share;
                cx += (nodeDelta[s].X - sw.Delta.X) * w;
                cy += (nodeDelta[s].Y - sw.Delta.Y) * w;
                cz += (nodeDelta[s].Z - sw.Delta.Z) * w;
            }
            nodeDelta[r] = new Vec3(own.X + cx, own.Y + cy, own.Z + cz);
            nodeMoved[r] = new Vec3(ownMoved.X + cx, ownMoved.Y + cy, ownMoved.Z + cz);
        }
    }

    // ── move ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Add the riders of the skin a move carries, each as strongly as its skin is carried (the most of its own weight
    /// and that): then every node takes the same offset or matrix, and the cloth goes with the body.
    /// </summary>
    private void AddMoveRiders(Dictionary<int, float> into)
    {
        var carried = new Dictionary<int, float>();
        foreach (var (s, ws) in into)
        {
            if (!skin[s]) continue;
            foreach (var (r, w) in follow!.RidersOf(s))
            {
                if (locked[r]) continue;
                carried[r] = carried.GetValueOrDefault(r) + w * ws;
            }
        }
        foreach (var (r, w) in carried)
            if (w > into.GetValueOrDefault(r)) into[r] = MathF.Min(w, 1f);
    }
}
