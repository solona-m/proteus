using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
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

    /// <summary>Cloth bound to the skin, in an operation that moves skin.</summary>
    private bool IsRider(int n) => opSkin && !skin[n] && follow!.BindWeight(n) > 0f;

    /// <summary>
    /// Cloth that takes a real share of its skin's movement in this operation (<see cref="RiderShare"/>): it is settled
    /// after the skin rather than smoothed on its own. Cloth over locked skin, or skin an open edge holds, is brushed —
    /// and settled — as cloth, exactly as with the checkbox off.
    /// </summary>
    private bool RidesSkin(int n) => RiderShare(n) > RideMin;

    /// <summary>Below this share of its skin's movement (1%), bound cloth is settled as cloth.</summary>
    private const float RideMin = 0.01f;

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
                // Counted with the Move tool's share, not the brushes': the skin took the cloth there, and the brush cap
                // must not then hold the cloth's own strokes short by however far it rode (nor its reach lag behind it).
                nodeMoved[r] = new Vec3(nodeMoved[r].X + d.X * w, nodeMoved[r].Y + d.Y * w, nodeMoved[r].Z + d.Z * w);
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
            if (!RidesSkin(n)) core[n] = was;
        if (core.Count > 0) UnfoldStroke(core, allowCollapse);

        CarryRiders(core.Keys, start);

        var ride = new Dictionary<int, Was>();
        foreach (var (n, was) in start)
            if (RidesSkin(n)) ride[n] = was;
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
    /// The guard at the end of a skin-moving operation, over what the skin it moved can reach — never the whole model,
    /// whose clips are the author's. Two checks, as a game shows a poke two ways:
    /// <list type="bullet">
    /// <item>a rider of skin this operation moved, below that skin (<see cref="KeepAboveBoundSkin"/>), is lifted to
    /// its floor;</item>
    /// <item>skin — moved, or still skin the moved cloth passes over — standing further outside the cloth face nearest
    /// it than the author had it (a clip deeper than <see cref="DeepClip"/> aside) — between the cloth's vertices, where
    /// no vertex check sees it (<see cref="SkinThroughCloth"/>) — lifts that face's unlocked corners by the excess,
    /// each by its share of the nearest point. "Outside" is along the skin's own way out, so a lining is judged as the
    /// shell over it is.</item>
    /// </list>
    /// </summary>
    /// <param name="start">The operation's undo record: whatever the guard moves is recorded in it first, so undo stays
    /// exact.</param>
    private void KeepRidersAboveSkin(Dictionary<int, Was> start)
    {
        if (!opSkin) return;
        var movedSkin = new List<int>();
        foreach (var (n, was) in start)
            if (skin[n] && nodeDelta[n] != was.Delta) movedSkin.Add(n);
        if (movedSkin.Count == 0) return;

        // Riders of the skin this operation moved — not every bound cloth point it recorded: cloth the user deflated
        // onto still skin is theirs.
        var moved = new HashSet<int>(movedSkin);
        foreach (var (n, _) in start)
        {
            if (skin[n] || locked[n] || follow!.BindWeight(n) <= 0f) continue;
            if (!follow.CornersOf(n).Any(c => moved.Contains(c.Skin))) continue;
            if (KeepAboveBoundSkin(n, nodeDelta[n], out var kept)) nodeDelta[n] = kept;
        }

        // The face check judges the moved skin and any still skin the moved cloth passes over: at an open edge the skin
        // holds while the cloth beside it rides, and the cloth can come to cross it.
        var judged = JudgedSkin(start, movedSkin);
        int pass = 0;
        var moving = new HashSet<int>(movedSkin);
        // What the operation itself moved, fixed before the guard lifts anything: its own lifts must not widen what it
        // may judge into cloth an earlier stroke placed.
        var opMoved = new HashSet<int>();
        foreach (var (n, was) in start)
            if (nodeDelta[n] != was.Delta) opMoved.Add(n);
        while (pass < FacePasses && LiftFacesOverSkin(start, judged, moving, opMoved, lift: true) > 0) pass++;
        // What is left, measured again after the last lift: logged by the caller, not chased.
        SkinStillOutside = LiftFacesOverSkin(start, judged, moving, opMoved, lift: false);
        GuardVersion++;
    }

    /// <summary>The moved skin, and every skin node within <see cref="SkinThroughCloth.Reach"/> of the box around what
    /// the operation moved, skin or cloth.</summary>
    private List<int> JudgedSkin(Dictionary<int, Was> start, List<int> movedSkin)
    {
        const float reach = SkinThroughCloth.Reach;
        var lo = Here(movedSkin[0]);
        var hi = lo;
        foreach (var (n, was) in start)
        {
            if (nodeDelta[n] == was.Delta) continue;
            var p = Here(n);
            lo = new Vec3(MathF.Min(lo.X, p.X), MathF.Min(lo.Y, p.Y), MathF.Min(lo.Z, p.Z));
            hi = new Vec3(MathF.Max(hi.X, p.X), MathF.Max(hi.Y, p.Y), MathF.Max(hi.Z, p.Z));
        }
        var judged = new List<int>(movedSkin);
        var seen = new HashSet<int>(movedSkin);
        for (int n = 0; n < nodeCount; n++)
        {
            if (!skin[n] || seen.Contains(n)) continue;
            var p = Here(n);
            if (p.X >= lo.X - reach && p.Y >= lo.Y - reach && p.Z >= lo.Z - reach
                && p.X <= hi.X + reach && p.Y <= hi.Y + reach && p.Z <= hi.Z + reach)
                judged.Add(n);
        }
        return judged;
    }

    /// <summary>The face check gives up after this many passes; what is left is logged, not chased.</summary>
    private const int FacePasses = 2;

    /// <summary>
    /// Skin points still further outside the cloth than the author had them after the last
    /// <see cref="KeepRidersAboveSkin"/> — the face check's leftovers, for the log. See <see cref="GuardVersion"/>.
    /// </summary>
    public int SkinStillOutside { get; private set; }

    /// <summary>Bumped by every run of the guard, so a caller can tell a new <see cref="SkinStillOutside"/> from an old one.</summary>
    public int GuardVersion { get; private set; }

    /// <summary>
    /// Skin the author had further than this (2 mm) outside a face is cloth left inside the body: covered by the skin
    /// whatever the sculpt does, and never lifted toward it. Less is a slight clip, and its growth is guarded.
    /// </summary>
    private const float DeepClip = 0.002f;

    /// <summary>One pass of the face check in <see cref="KeepRidersAboveSkin"/>.</summary>
    /// <param name="lift">Lift what is through; off, only count it.</param>
    /// <returns>Lifting, how many skin points it lifted cloth over; counting, how many are still through.</returns>
    /// <param name="moving">The skin this operation moved. Still skin is judged only against a face this operation
    /// moved: cloth placed by an earlier stroke is the user's, however it lies.</param>
    /// <param name="opMoved">Every node the operation moved, before the guard lifted any.</param>
    private int LiftFacesOverSkin(Dictionary<int, Was> start, List<int> judged, HashSet<int> moving, HashSet<int> opMoved,
                                  bool lift)
    {
        const float reach = SkinThroughCloth.Reach, tolerance = SkinThroughCloth.Tolerance;

        // Only cloth near the skin judged can be through it.
        var mlo = Here(judged[0]);
        var mhi = mlo;
        foreach (int s in judged)
        {
            var p = Here(s);
            mlo = new Vec3(MathF.Min(mlo.X, p.X), MathF.Min(mlo.Y, p.Y), MathF.Min(mlo.Z, p.Z));
            mhi = new Vec3(MathF.Max(mhi.X, p.X), MathF.Max(mhi.Y, p.Y), MathF.Max(mhi.Z, p.Z));
        }
        bool Near(Vec3 p) => p.X >= mlo.X - reach && p.Y >= mlo.Y - reach && p.Z >= mlo.Z - reach
                             && p.X <= mhi.X + reach && p.Y <= mhi.Y + reach && p.Z <= mhi.Z + reach;

        // Every cloth face there, bucketed by space: a face over moved skin may have no corner that rode it — its skin
        // lay just past the brush — and is through all the same.
        (int, int, int) CellOf(Vec3 p)
            => ((int)MathF.Floor(p.X / reach), (int)MathF.Floor(p.Y / reach), (int)MathF.Floor(p.Z / reach));
        var faces = new List<int>();
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int t = 0; t + 2 < nodeTris.Length; t += 3)
        {
            int a = nodeTris[t], b = nodeTris[t + 1], c = nodeTris[t + 2];
            // Cloth, with at least one corner of its own: a face of a stocking welded to the leg is cloth still.
            if (skin[a] && skin[b] && skin[c]) continue;
            if (!clothFaces[t / 3]) continue;
            var pa = Here(a); var pb = Here(b); var pc = Here(c);
            if (!Near(pa) && !Near(pb) && !Near(pc)) continue;
            int f = faces.Count;
            faces.Add(t);
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
        var lifts = new Dictionary<int, (float Lift, Vector3 Way)>();
        int lifted = 0, outside = 0;
        const float onEdge = 0.02f;
        foreach (int s in judged)
        {
            var sp = ToVector(Here(s));
            if (!grid.TryGetValue(CellOf(Here(s)), out var near)) continue;

            // The face nearest the skin point. Not every face over it: within reach that takes in the far side of a
            // strap or a stocking, and lifting those folded the cloth.
            int best = -1;
            float bestD = reach;
            (int Face, Vector3 Q, float U, float V, float W) hit = default;
            foreach (int f in near)
            {
                int t = faces[f];
                var q = BrushTransfer.ClosestOnTriangle(sp, ToVector(Here(nodeTris[t])), ToVector(Here(nodeTris[t + 1])),
                                                        ToVector(Here(nodeTris[t + 2])), out float u, out float v, out float w);
                float d = Vector3.Distance(sp, q);
                if (d >= bestD) continue;
                bestD = d; best = f; hit = (f, q, u, v, w);
            }
            if (best < 0) continue;

            var (hitFace, hq, bu, bv, bw) = hit;
            int ft = faces[hitFace];
            int ca = nodeTris[ft], cb = nodeTris[ft + 1], cc = nodeTris[ft + 2];
            if (!moving.Contains(s) && !opMoved.Contains(ca) && !opMoved.Contains(cb) && !opMoved.Contains(cc)) continue;
            // Past a hem is not through anything — as SkinThroughCloth judges it.
            if ((bu < onEdge && hem.Contains(EdgeKey(cb, cc))) || (bv < onEdge && hem.Contains(EdgeKey(cc, ca)))
                || (bw < onEdge && hem.Contains(EdgeKey(ca, cb))))
                continue;

            // "Outside" is the skin's own way out, whichever way the face is wound: a lining faces the body, and
            // judged by its own normal every skin point under it is outside it — and lifted that way, it is driven
            // into the body.
            var skinOut = ToVector(nodeNormal[s]);
            var face = OutAlong(Here(ca), Here(cb), Here(cc), skinOut);
            if (face == Vector3.Zero) continue;
            float by = Vector3.Dot(sp - hq, face);

            // The author's own: how far outside the same face this skin point stood as authored.
            var aq = BrushTransfer.ClosestOnTriangle(ToVector(nodeAt[s]), ToVector(nodeAt[ca]), ToVector(nodeAt[cb]),
                                                     ToVector(nodeAt[cc]), out _, out _, out _);
            var authoredFace = OutAlong(nodeAt[ca], nodeAt[cb], nodeAt[cc], skinOut);
            float authored = Vector3.Distance(ToVector(nodeAt[s]), aq) <= reach && authoredFace != Vector3.Zero
                ? Vector3.Dot(ToVector(nodeAt[s]) - aq, authoredFace)
                : 0f;

            // Skin the author had well outside this face covers it: cloth left inside the body. Moving the skin
            // further out shows nothing more, and lifting that cloth toward the skin would drag it through its own
            // surface. A slight clip is the author's to keep, but not to grow.
            if (authored > DeepClip) continue;
            float excess = by - MathF.Max(authored, 0f) - tolerance;
            if (excess <= 0f) continue;
            if (!lift) { outside++; continue; }

            // Unlocked cloth corners only. Lifted by λ·share each, the face's point there rises λ·Σshare², so that
            // is what the excess is divided by.
            var corners = new[] { (ca, bu), (cb, bv), (cc, bw) };
            float sq = 0f;
            foreach (var (c, share) in corners)
                if (!locked[c] && !skin[c] && share > 0f) sq += share * share;
            bool any = false;
            foreach (var (c, share) in corners)
            {
                if (locked[c] || skin[c] || share <= 0f || sq < 1e-4f) continue;
                // Never more than a few times the excess: a corner with a sliver of the share would be thrown off.
                float cornerLift = MathF.Min(excess * share / sq, 2f * excess);
                // The most any one skin point asks of the corner, not the sum: several points under one face each
                // ask for the same lift, and adding them threw tight cloth centimetres off a 3 mm sculpt.
                if (!lifts.TryGetValue(c, out var had) || cornerLift > had.Lift) lifts[c] = (cornerLift, face);
                any = true;
            }
            if (any) lifted++;
        }
        if (!lift) return outside;

        foreach (var (c, (rise, way)) in lifts)
        {
            start.TryAdd(c, Snapshot(c));   // into the undo record before its first change, so undo stays exact
            nodeDelta[c] = new Vec3(nodeDelta[c].X + way.X * rise, nodeDelta[c].Y + way.Y * rise, nodeDelta[c].Z + way.Z * rise);
        }
        return lifted;
    }

    /// <summary>A face's normal, turned to agree with <paramref name="skinOut"/>, the skin's way out under it.</summary>
    private static Vector3 OutAlong(Vec3 a, Vec3 b, Vec3 c, Vector3 skinOut)
    {
        var f = Vector3.Cross(ToVector(b) - ToVector(a), ToVector(c) - ToVector(a));
        if (f.LengthSquared() < 1e-14f) return Vector3.Zero;
        f = Vector3.Normalize(f);
        return Vector3.Dot(f, skinOut) < 0f ? -f : f;
    }

    /// <summary>Per face of <see cref="nodeTris"/>, whether its part is drawn with cloth rather than skin — a face can be
    /// cloth with every corner welded to skin. Filled by the constructor, which makes the faces.</summary>
    private readonly bool[] clothFaces;

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

    /// <summary>
    /// After <see cref="ImportEdit"/> carried a skin move from another size: run the guard over this size's own riders,
    /// bound by <paramref name="binding"/>, as if the whole edit were one operation — this size's cloth and skin are
    /// not the brushed one's, and its cloth can land a hair under its moved skin. Against the author's model, so only
    /// what the edit made worse is touched.
    /// </summary>
    internal void GuardImportedRiders(SkinFollow binding)
    {
        if (!AttachFollow(binding)) return;
        var start = new Dictionary<int, Was>(nodeCount);
        for (int n = 0; n < nodeCount; n++) start[n] = new Was(default, 0f, initialWind[n], default, null);
        opSkin = true;
        Seams();
        try { KeepRidersAboveSkin(start); }
        finally { opSkin = false; }
        Settle(null);
    }

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
