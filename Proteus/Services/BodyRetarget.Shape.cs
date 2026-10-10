using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// Knobs a diagnostic can turn to compare refits, each defaulting to what a refit really uses.
    /// </summary>
    /// <param name="NoFollow">Switch <see cref="FollowLaidSkin"/> off.</param>
    /// <param name="NoOwnSkinWeights">Take the weight change against the old body mod everywhere, never the
    /// garment's own skin.</param>
    /// <param name="NoGiveUp">Leave folds the relax could not clear, rather than giving up their corners' movement.</param>
    /// <param name="NoSettle">Switch <see cref="Settle"/> off: leave whatever the push-out could not clear.</param>
    /// <param name="NoFaceSettle">Settle vertices only, leaving skin the push gave up on through a face's middle.</param>
    /// <param name="NoLayerGuard">Let <see cref="ClearFaces"/> push one layer of cloth through another.</param>
    /// <param name="NoWeightSmooth">Leave each cloth vertex's new weights as its own lookup gave them
    /// (see <see cref="SmoothWeights"/>).</param>
    /// <param name="NoUnderbustLift">Leave cloth resting on the underside of the breast where it lands
    /// (see <see cref="UnderbustClearance"/>).</param>
    /// <param name="NoRelax">Leave the cloth's movement as uneven as the transfer left it (see <see cref="Relax"/>).</param>
    /// <param name="NoSkinHug">Let cloth lying on the body keep the garment's own bones (see
    /// <see cref="FollowBodyNearSkin"/>).</param>
    /// <param name="NoLayerKnit">Let each sheet of cloth take its own move from the bodies, however near another sheet
    /// it lies (see <see cref="KnitLayers"/>).</param>
    /// <param name="NoLayerOrder">Leave a covered piece wherever the passes put it, in front of its cover or not (see
    /// <see cref="KeepLayerOrder"/>).</param>
    internal sealed record Tuning(bool NoFollow = false, bool NoOwnSkinWeights = false,
                                  float OwnSkinReach = OwnSkinReachDefault, float CopyReach = CopyReachDefault,
                                  float CopyTolerance = CopyToleranceDefault, bool NoGiveUp = false,
                                  bool NoSettle = false, bool NoFaceSettle = false, bool NoLayerGuard = false,
                                  bool NoWeightSmooth = false, bool NoUnderbustLift = false, bool NoRelax = false,
                                  bool NoSkinHug = false, bool NoLayerKnit = false, bool NoRimLift = false,
                                  bool NoLayerOrder = false);

    private static readonly Tuning DefaultTuning = new();

    /// <summary>Per logical call, not per process: two tests refitting at once on the test runner's threads each see
    /// only their own, and a task a refit starts sees its caller's.</summary>
    private static readonly System.Threading.AsyncLocal<Tuning?> tuning = new();

    /// <summary>The knobs in force for this refit.</summary>
    internal static Tuning Tuned => tuning.Value ?? DefaultTuning;

    /// <summary>Run <paramref name="refit"/> with <paramref name="with"/> in force, and only it.</summary>
    internal static T WithTuning<T>(Tuning with, Func<T> refit)
    {
        var was = tuning.Value;
        tuning.Value = with;
        try { return refit(); }
        finally { tuning.Value = was; }
    }

    /// <summary>Cloth this close to the garment's own skin follows the skin's correction completely (10 mm).</summary>
    internal const float FollowFull = 0.01f;

    /// <summary>Cloth this far from the garment's own skin keeps the transfer's answer (30 mm); between the two the
    /// correction fades out smoothly.</summary>
    internal const float FollowReach = 0.03f;

    /// <summary>
    /// With the garment's skin being swapped for the body's, carry the cloth out with it wherever the author had pressed
    /// that skin in.
    /// <para/>
    /// The transfer carries cloth along with the SOURCE body, so it keeps its offset from that body. An author who
    /// reshapes the skin under a garment — a belt cinching the waist, an underwire lifting the breast — builds the cloth
    /// against the reshaped skin, not against the source body: "pop" (YAB Medium) sits 0.4 mm into its own waist and
    /// 3.4 mm into YAB's, and its bodice 0.9 mm into its own breasts and 10.5 mm into YAB's. The swap throws the reshaped
    /// skin away and draws the body's own full shape in its place, so the cloth came out buried by the difference.
    /// <para/>
    /// Cloth the author put OUTSIDE its skin is put back at the gap it had from that skin, measured against the new
    /// bodies it will be drawn over — out or in, never nearer than <see cref="Clearance"/>. Moving it only outward, by
    /// <see cref="LaySkin"/>'s correction, left cloth standing off wherever the author's skin stood proud of the source
    /// body: "Victoria's Secret Sauce", bikini bottoms whose own skin under the front panel is about 4 mm fuller than
    /// Bibo+ Small, had the panel 1.5-2 mm off that skin and every refit kept it 6-7 mm off the new one. Pulling cloth in
    /// WITHOUT a limit tightened it until it clipped (the reverted <c>HugBody</c>, measured in game); aimed at the author's
    /// own gap it cannot go nearer than the author had it. Inward moves are fold-guarded (<see cref="PullIn"/>).
    /// <para/>
    /// Cloth the author tucked under its skin keeps the old rule: let out by the skin's correction, never pulled in.
    /// </summary>
    /// <param name="carried">Each node's delta before the skin was laid; the skin nodes' change since is the correction.</param>
    /// <returns>How many cloth nodes were moved.</returns>
    private static int FollowLaidSkin(ModelParts garment, Sets sets, Vec3[] carried, Vec3[] nodeDelta, bool[] snapped,
                                      IReadOnlyList<SlotPair> pairs)
    {
        var own = new BodySurface(garment, BodySurface.CellFor(MeanEdgeOf(garment)));
        if (own.IsEmpty) return 0;
        var drawn = new List<BodySurface>(pairs.Count);
        foreach (var pair in pairs)
        {
            var surface = new BodySurface(pair.Target, BodySurface.CellFor(MeanEdgeOf(pair.Target)));
            if (!surface.IsEmpty) drawn.Add(surface);
        }

        Vector3 Correction(int vertex)
        {
            int n = sets.NodeOf[vertex];
            return ToVector(nodeDelta[n]) - ToVector(carried[n]);
        }

        var add = new List<(int Node, Vector3 By)>();
        var pull = new List<(int Node, Vector3 By)>();
        foreach (int n in sets.ClothNodes)
        {
            if (snapped[n]) continue;
            var p = ToVector(sets.NodeAt[n]);
            if (!own.Nearest(p, FollowReach, out var hit)) continue;
            var c = Correction(hit.A) * hit.U + Correction(hit.B) * hit.V + Correction(hit.C) * hit.W;
            float along = Vector3.Dot(c, hit.Normal);
            float w = 1f - MeshMath.Smoothstep((hit.Distance - FollowFull) / (FollowReach - FollowFull));
            if (w <= 0f) continue;

            float authoredGap = Vector3.Dot(p - hit.Point, hit.Normal);
            if (authoredGap <= 0f)
            {
                // Tucked under its skin by the author: only ever let out, by the skin's own move, as before.
                if (along > 0f) add.Add((n, hit.Normal * (along * w)));
                continue;
            }

            // Outside its skin: put back at the author's own gap from that skin, laid where it now is — out or in, and
            // never nearer than Clearance. Aimed at the gap rather than moved by the skin's correction, which is not the
            // author's offset alone: under the lower edge of "Victoria's Secret Sauce"'s low-poly front panel it read
            // 2.5-3.6 mm outward, and following it left the panel 4-5 mm off the new skin where its author had 1.5-2.
            // The gap now, against the skin that will be DRAWN — the new bodies themselves — not against the author's
            // skin as laid: where that skin is too far off the source body it is laid only part way (LayFull..LayReach),
            // and the panel's lower edge measured 7-8 mm inside it while 4-5 mm outside the body actually drawn.
            var at = p + ToVector(nodeDelta[n]);
            BodySurface.Hit drawnHit = default;
            bool onDrawn = false;
            float within = FollowReach;
            foreach (var surface in drawn)
            {
                if (!surface.Nearest(at, within, out var h)) continue;
                drawnHit = h;
                within = h.Distance;
                onDrawn = true;
            }
            if (!onDrawn) continue;
            float gapNow = Vector3.Dot(at - drawnHit.Point, drawnHit.Normal);
            float move = (MathF.Max(authoredGap, Clearance) - gapNow) * w;
            if (move > 0f) add.Add((n, drawnHit.Normal * move));
            else if (move < 0f) pull.Add((n, drawnHit.Normal * move));
        }
        foreach (var (n, by) in add) nodeDelta[n] = ToVec(ToVector(nodeDelta[n]) + by);
        return add.Count + PullIn(sets, nodeDelta, pull);
    }

    /// <summary>
    /// Apply <see cref="FollowLaidSkin"/>'s inward moves, backed off wherever one would turn a triangle over: halved on
    /// every corner of such a triangle for <c>PushUnfoldPasses</c> passes, then given up outright, as the push-out's own
    /// guard does. Neighbours pulled in by different amounts step past each other; measured on "Victoria's Secret Sauce"
    /// without this, YAB+ Skull Crushers went from 8 turned-over triangles to 33.
    /// </summary>
    /// <returns>How many nodes moved.</returns>
    private static int PullIn(Sets sets, Vec3[] nodeDelta, List<(int Node, Vector3 By)> pull)
    {
        if (pull.Count == 0) return 0;
        var by = new Vector3[sets.NodeCount];
        var scale = new float[sets.NodeCount];
        foreach (var (n, v) in pull) { by[n] = v; scale[n] = 1f; }

        Vector3 Trial(int n) => Placed(sets, nodeDelta, n) + by[n] * scale[n];
        for (int pass = 0; ; pass++)
        {
            bool giveUp = pass >= PushUnfoldPasses;
            int folded = 0;
            for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
            {
                int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
                if (va < 0 || vb < 0 || vc < 0
                    || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) continue;
                int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
                if (a == b || b == c || c == a) continue;
                if (scale[a] <= 0f && scale[b] <= 0f && scale[c] <= 0f) continue;

                var was = ToVector(sets.NodeAt[a]);
                var n0 = Vector3.Cross(ToVector(sets.NodeAt[b]) - was, ToVector(sets.NodeAt[c]) - was);
                if (n0.LengthSquared() <= 1e-24f) continue;
                var now = Trial(a);
                if (Vector3.Dot(n0, Vector3.Cross(Trial(b) - now, Trial(c) - now)) > 0f) continue;

                // Turned over already, before this pull: not its doing, and backing off cannot help.
                var before = Placed(sets, nodeDelta, a);
                if (Vector3.Dot(n0, Vector3.Cross(Placed(sets, nodeDelta, b) - before, Placed(sets, nodeDelta, c) - before)) <= 0f)
                    continue;

                float keep = giveUp ? 0f : 0.5f;
                scale[a] *= keep;
                scale[b] *= keep;
                scale[c] *= keep;
                folded++;
            }
            if (folded == 0) break;
        }

        int moved = 0;
        foreach (var (n, _) in pull)
        {
            if (scale[n] <= 0f) continue;
            nodeDelta[n] = ToVec(ToVector(nodeDelta[n]) + by[n] * scale[n]);
            moved++;
        }
        return moved;
    }

    /// <summary>The share of a piece's edges that may be open and still count as a closed solid (5%): the underwire of
    /// "pop" leaves its two ends open, 40 edges of 1,460.</summary>
    internal const float HardOpenShare = 0.05f;

    /// <summary>
    /// The largest a piece may be and still be taken for hard without asking (13 cm): a ring, a band round the arm, a
    /// buckle. Past this a closed piece is more likely thickened cloth that has to drape. Measured: the largest hard
    /// pieces seen are 12.2-12.4 cm ("pop"'s arm bands, a hoodie's drawstrings, a skirt's side straps); the smallest
    /// closed cloth is 13.9 cm (the upper tiers of "Ruffles", solidified ruffles round the hips). Neither how a piece is
    /// rigged nor how far it sits off the skin tells the two apart — the bands blend three bones, the ruffles follow one.
    /// </summary>
    internal const float HardMaxSize = 0.13f;

    /// <summary>
    /// The pieces of a garment that look hard — metal, not cloth — and so should keep their shape through a refit
    /// unless the user says otherwise: a separate piece (an island the reader split off its submesh), closed or all but
    /// closed, and no bigger than <see cref="HardMaxSize"/>.
    /// <para/>
    /// Cloth is a sheet with hems, so its open edges run all round it; a ring, a band, a chain link or a buckle is
    /// modelled as a solid. Material says nothing: "pop" draws its arm bands with the shirt's own material. Measured on
    /// "pop": its four arm bands, 176 chain links and underwire pieces are closed or 3% open; its cloth is not split
    /// into pieces at all, and its belt is one whole submesh 21 cm across.
    /// </summary>
    /// <returns>The labels of the pieces that look hard.</returns>
    internal static List<string> HardPieces(ModelParts garment)
    {
        var labels = new List<string>();
        foreach (var part in garment.Parts)
        {
            if (part.Island < 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            if (IsHard(garment, part)) labels.Add(part.Label);
        }
        return labels;
    }

    private static bool IsHard(ModelParts garment, ModelPart part)
    {
        var verts = new List<int>();
        var local = new Dictionary<int, int>();
        foreach (int v in part.Triangles)
            if (v >= 0 && v * 3 + 2 < garment.Positions.Length && local.TryAdd(v, verts.Count)) verts.Add(v);
        if (verts.Count < 4) return false;

        var at = new Vec3[verts.Count];
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (int i = 0; i < verts.Count; i++)
        {
            int v = verts[i];
            var p = new Vector3(garment.Positions[v * 3], garment.Positions[v * 3 + 1], garment.Positions[v * 3 + 2]);
            at[i] = ToVec(p);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        var size = hi - lo;
        if (MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > HardMaxSize) return false;

        // Open edges once welded by position: a uv seam splits vertices without opening the surface.
        var node = MeshMath.WeldByPosition(at, out _);
        var uses = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                if (!local.TryGetValue(part.Triangles[t + k], out int ia)
                    || !local.TryGetValue(part.Triangles[t + (k + 1) % 3], out int ib)) continue;
                int a = node[ia], b = node[ib];
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        if (uses.Count == 0) return false;
        int open = 0;
        foreach (int c in uses.Values)
            if (c == 1) open++;
        return open <= HardOpenShare * uses.Count;
    }

    /// <summary>
    /// Move each hard piece as ONE piece: the turn, shift and uniform scale that best carry its points where the refit
    /// sent them, in place of the refit's point-by-point answer.
    /// <para/>
    /// Each point of a garment follows the body under it on its own, which is right for cloth and wrong for metal: the
    /// underwire ring of "pop" follows the underbust of a smaller body point by point and comes out 18% shorter than it
    /// is wide, bent by up to 8 mm; its chain links came out anywhere from 0.88 to 1.5 times their size.
    /// </summary>
    /// <param name="pieces">The vertices of each piece to keep, one set per piece.</param>
    /// <param name="scales">Each piece's scale, by index. NaN on the way in means work it out, and it is written back;
    /// a number is used as it is. The push-out moves points by millimetres, which across a 3 mm chain link reads as a
    /// scale of 0.8 or 1.3; so the scale is taken once, from the transfer's smooth answer, and kept after the push.</param>
    /// <returns>How many pieces were moved whole.</returns>
    private static int KeepShape(Sets sets, IReadOnlyList<IReadOnlyCollection<int>> pieces, Vec3[] nodeDelta,
                                 float[] scales)
    {
        var mayMove = new bool[sets.NodeCount];
        foreach (int n in sets.AllNodes) mayMove[n] = true;

        int kept = 0;
        var seen = new HashSet<int>();
        for (int k = 0; k < pieces.Count; k++)
        {
            var piece = pieces[k];
            seen.Clear();
            var nodes = new List<int>();
            foreach (int v in piece)
            {
                if (v < 0 || v >= sets.NodeOf.Length) continue;
                int n = sets.NodeOf[v];
                if (mayMove[n] && seen.Add(n)) nodes.Add(n);
            }
            if (nodes.Count < 3) continue;

            var from = new List<Vec3>(nodes.Count);
            var to = new List<Vec3>(nodes.Count);
            Vector3 cFrom = Vector3.Zero, cTo = Vector3.Zero;
            foreach (int n in nodes)
            {
                var a = ToVector(sets.NodeAt[n]);
                var b = a + ToVector(nodeDelta[n]);
                from.Add(ToVec(a));
                to.Add(ToVec(b));
                cFrom += a;
                cTo += b;
            }
            cFrom /= nodes.Count;
            cTo /= nodes.Count;

            var r = SecondSkinWriter.BestRotation(from, to, ToVec(cFrom), ToVec(cTo));
            Vector3 Turn(Vector3 q) => new(r[0] * q.X + r[1] * q.Y + r[2] * q.Z,
                                           r[3] * q.X + r[4] * q.Y + r[5] * q.Z,
                                           r[6] * q.X + r[7] * q.Y + r[8] * q.Z);
            float det = r[0] * (r[4] * r[8] - r[5] * r[7]) - r[1] * (r[3] * r[8] - r[5] * r[6])
                      + r[2] * (r[3] * r[7] - r[4] * r[6]);
            if (det < 0.5f) continue;   // a reflection, or no answer: a piece too flat to say how it turned

            // The uniform scale that best explains the spread of the refit's answer about the turned piece.
            float s = scales[k];
            if (float.IsNaN(s))
            {
                double num = 0, den = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    var a = Turn(ToVector(from[i]) - cFrom);
                    var b = ToVector(to[i]) - cTo;
                    num += Vector3.Dot(a, b);
                    den += a.LengthSquared();
                }
                scales[k] = s = den > 1e-18 ? (float)(num / den) : 1f;
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                var a = ToVector(from[i]);
                var placed = cTo + Turn(a - cFrom) * s;
                nodeDelta[nodes[i]] = ToVec(placed - a);
            }
            kept++;
        }
        return kept;
    }
}
