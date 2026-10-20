using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// Another piece of the same outfit, which the garment being refitted was authored over: the bra a jacket worn as
    /// gloves covers. Read, never written.
    /// </summary>
    /// <param name="Authored">The piece as its author made it, on the body the garment was made on.</param>
    /// <param name="Refitted">The piece as it is drawn now, on the body the garment is refitted to: its own refit, or the
    /// author's file when that is already on this body.</param>
    internal sealed record Outfit(ModelParts Authored, ModelParts Refitted);

    /// <summary>Cells of the body's surface the outfit's relation is read in (5 mm): per cell, the other piece's highest
    /// cloth above the skin.</summary>
    internal const float OutfitCell = 0.005f;

    /// <summary>The gap <see cref="KeepOutfitOrder"/> keeps between the garment and the piece beneath it, at most;
    /// less where the author left less. Fixed from the M1b table (see the remarks there).</summary>
    internal const float OutfitOrderGapDefault = 1e-3f;

    /// <summary>How far a lift fades out across the body around the cloth that needed it (15 mm), so it is one swell over
    /// the piece beneath rather than a step at each cell.</summary>
    internal const float OutfitFadeDefault = 0.015f;

    /// <summary>Around each point that needed it, the lift is whole this far out (2 mm) before it starts to fade.</summary>
    private const float OutfitPlateau = 0.002f;

    /// <summary>Rounds of raising a fold's low corners and fading their lift onto their neighbours (3).</summary>
    private const int OutfitRaiseRounds = 3;

    /// <summary>
    /// Keep the garment outside the other piece of its outfit wherever its author had it outside: a jacket worn as
    /// gloves over the bra of the same option.
    /// <para/>
    /// Each piece is refitted against the body alone, and two files cannot see each other. Geometric's bra, laid 0.4-0.7
    /// mm off Bibo+'s skin, came out 2.4-4.1 mm off Rue's where the push-out cleared Rue's rounder breast out of its large
    /// faces, while its jacket kept its own gap to within 0.4 mm: the bra showed through the jacket in 7, 15 and 14 cells
    /// on Rue S, M and L; with this pass, in none, and no triangle turned over. The relation is read body-relatively —
    /// each piece's height above the nearest point of the body pairs' skin — because a jacket's lining faces in and a ray
    /// from it says nothing (98% "through" as shipped).
    /// <para/>
    /// The garment is lifted, though it is the piece seen: the other piece is a file already written, and only the piece
    /// in hand is this refit's. <see cref="KeepLayerOrder"/> records that lifting the cover was measured worse within one
    /// model; across two files there is no other piece to move. The lift is a smooth field over the body — whole around
    /// the cloth that needed it, faded over <paramref name="fade"/> — so lining and shell, a few millimetres apart, take
    /// the same lift, and scattered cells make one swell rather than dimples. The garment's own layers are then put back
    /// in order by lifting their covers (<see cref="KeepLayerOrder"/> with no room to pull back), never by pulling a
    /// lining back under the other piece.
    /// </summary>
    /// <param name="pairs">The refit's body pairs. Pieces are measured on their skin only — never a
    /// <see cref="SlotPair.ShapeOnly"/> pair's, whose body the garment replaces (the hands of a jacket worn as gloves).</param>
    /// <param name="layered">Per node, the garment's own cloth (see <see cref="GarmentCloth"/>).</param>
    /// <param name="covered">The garment's own covered points (see <see cref="CoveredPoints"/>), kept in order after the lift.</param>
    /// <param name="residual">The largest shortfall left after the lift, in metres: what the fold guard did not let it do.</param>
    /// <returns>How many nodes were lifted.</returns>
    internal static int KeepOutfitOrder(Sets sets, IReadOnlyList<SlotPair> pairs, Outfit outfit, Vec3[] nodeDelta,
                                        bool[]? stay, bool[] layered, IReadOnlyList<Covered> covered,
                                        out float residual, float gap = OutfitOrderGapDefault,
                                        float fade = OutfitFadeDefault)
    {
        residual = 0f;
        var source = new List<BodySurface>();
        var target = new List<BodySurface>();
        foreach (var pair in pairs)
        {
            if (pair.ShapeOnly) continue;
            var s = pair.SourceSurface();
            var t = pair.TargetSurface();
            if (s.IsEmpty || t.IsEmpty) continue;
            source.Add(s);
            target.Add(t);
        }
        if (source.Count == 0) return 0;

        var below = OutfitHeights(outfit.Authored, source);
        var now = OutfitHeights(outfit.Refitted, target);
        if (below.Count == 0 || now.Count == 0) return 0;

        var movable = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) movable[n] = layered[n] && (stay == null || !stay[n]) && Finite(nodeDelta[n]);

        var needs = OutfitNeeds(sets, nodeDelta, movable, source, target, below, now, gap);
        if (needs.Count == 0) return 0;

        // Where each node stands over the skin now: the field is read there, and lifts along the skin's normal there.
        var under = new BodySurface.Hit?[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++)
            if (movable[n] && NearestOf(target, ToVector(sets.NodeAt[n]) + ToVector(nodeDelta[n]), out var hit))
                under[n] = hit;

        // The field: whole within OutfitPlateau of each point that needed it, faded to nothing over `fade` beyond.
        var lifted = new bool[sets.NodeCount];
        List<(int Node, Vector3 By)> Field()
        {
            var moves = new List<(int Node, Vector3 By)>();
            for (int n = 0; n < sets.NodeCount; n++)
            {
                if (under[n] is not { } hit) continue;
                float lift = 0f;
                foreach (var (at, need) in needs)
                {
                    if (need <= lift) continue;
                    float d = Vector3.Distance(hit.Point, at);
                    float w = d <= OutfitPlateau ? 1f : 1f - MeshMath.Smoothstep((d - OutfitPlateau) / fade);
                    lift = MathF.Max(lift, need * w);
                }
                if (lift <= 0f) continue;
                moves.Add((n, hit.Normal * lift));
                lifted[n] = true;
            }
            return moves;
        }

        // A triangle the lift would turn over has its low corners raised to its high ones — the far corner of a sliver
        // standing edge-on at the garment's edge — and a corner so raised asks the field for its lift in turn, so its
        // neighbours rise with it rather than leaving a spike. Whatever would still turn over is held back by the fold
        // guard, as the push-out's moves are.
        var moves = Field();
        if (moves.Count == 0) return 0;
        for (int round = 0; round < OutfitRaiseRounds; round++)
        {
            var raised = RaiseLowCorners(sets, nodeDelta, moves, movable, lifted);
            if (raised.Count == 0) break;
            foreach (var (n, lift) in raised)
                if (under[n] is { } hit) needs.Add((hit.Point, lift));
            moves = Field();
        }
        PullIn(sets, nodeDelta, moves);

        // The garment's own layers back in order where the lift moved them, by lifting the cover only (room 0 pulls
        // nothing back, which would take a lining back under the other piece) — through the fold guard too.
        var near = new List<Covered>();
        foreach (var c in covered)
            if (lifted[c.Node] || lifted[c.A] || lifted[c.B] || lifted[c.C]) near.Add(c);
        if (near.Count > 0)
        {
            var ordered = (Vec3[])nodeDelta.Clone();
            KeepLayerOrder(sets, near, ordered, stay, layered, drawn: null, liftOnly: true);
            var orderMoves = new List<(int Node, Vector3 By)>();
            for (int n = 0; n < sets.NodeCount; n++)
            {
                var by = ToVector(ordered[n]) - ToVector(nodeDelta[n]);
                if (by != Vector3.Zero) orderMoves.Add((n, by));
            }
            PullIn(sets, nodeDelta, orderMoves);
        }

        // What is left, at the garment's own points: the middles of its edges ask for the field, but between two points
        // lifted differently they sit lower than either, and are no cloth of their own.
        foreach (var (_, need) in OutfitNeeds(sets, nodeDelta, movable, source, target, below, now, gap, midpoints: false))
            residual = MathF.Max(residual, need);
        int count = 0;
        foreach (bool l in lifted) if (l) count++;
        return count;
    }

    /// <summary>
    /// Where the lift would turn a triangle over, its lower corners are raised to its highest, rather than its higher
    /// ones held back: a sliver standing edge-on to the skin at the garment's edge flips when its near corners rise and
    /// its far one does not. Rounds until none would, or <see cref="PushUnfoldPasses"/>; what is left is for the fold
    /// guard.
    /// </summary>
    /// <returns>The corners raised, and the lift each was raised to.</returns>
    private static List<(int Node, float Lift)> RaiseLowCorners(Sets sets, Vec3[] nodeDelta,
                                                                List<(int Node, Vector3 By)> moves, bool[] movable,
                                                                bool[] lifted)
    {
        var lift = new float[sets.NodeCount];
        var dir = new Vector3[sets.NodeCount];
        foreach (var (n, by) in moves)
        {
            lift[n] = by.Length();
            dir[n] = lift[n] > 0f ? by / lift[n] : Vector3.Zero;
        }
        Vector3 Trial(int n) => Placed(sets, nodeDelta, n) + dir[n] * lift[n];
        var up = new HashSet<int>();
        for (int pass = 0; pass < PushUnfoldPasses; pass++)
        {
            bool raised = false;
            for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
            {
                int a = sets.NodeOf[sets.Tris[t]], b = sets.NodeOf[sets.Tris[t + 1]], c = sets.NodeOf[sets.Tris[t + 2]];
                if (a == b || b == c || c == a || (lift[a] <= 0f && lift[b] <= 0f && lift[c] <= 0f)) continue;
                var was = ToVector(sets.NodeAt[a]);
                var n0 = Vector3.Cross(ToVector(sets.NodeAt[b]) - was, ToVector(sets.NodeAt[c]) - was);
                if (n0.LengthSquared() <= 1e-24f) continue;
                var before = Placed(sets, nodeDelta, a);
                if (Vector3.Dot(n0, Vector3.Cross(Placed(sets, nodeDelta, b) - before, Placed(sets, nodeDelta, c) - before)) <= 0f)
                    continue;   // folded before the lift: not this pass's doing
                var now = Trial(a);
                if (Vector3.Dot(n0, Vector3.Cross(Trial(b) - now, Trial(c) - now)) > 0f) continue;
                float top = MathF.Max(lift[a], MathF.Max(lift[b], lift[c]));
                int from = lift[a] == top ? a : lift[b] == top ? b : c;
                foreach (int m in new[] { a, b, c })
                {
                    if (!movable[m] || lift[m] >= top) continue;
                    lift[m] = top;
                    if (dir[m] == Vector3.Zero) dir[m] = dir[from];
                    lifted[m] = true;
                    raised = true;
                    up.Add(m);
                }
            }
            if (!raised) break;
        }
        moves.Clear();
        for (int n = 0; n < sets.NodeCount; n++)
            if (lift[n] > 0f) moves.Add((n, dir[n] * lift[n]));
        var result = new List<(int Node, float Lift)>(up.Count);
        foreach (int n in up) result.Add((n, lift[n]));
        return result;
    }

    private static bool Finite(Vec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>The nearest point on any of <paramref name="surfaces"/> within <see cref="PushProbeRange"/>.</summary>
    private static bool NearestOf(List<BodySurface> surfaces, Vector3 p, out BodySurface.Hit hit)
    {
        hit = default;
        bool found = false;
        float within = PushProbeRange;
        foreach (var s in surfaces)
        {
            if (!s.Nearest(p, within, out var h)) continue;
            hit = h;
            within = h.Distance;
            found = true;
        }
        return found;
    }

    private static (int, int, int) OutfitCellOf(Vector3 p)
        => ((int)MathF.Floor(p.X / OutfitCell), (int)MathF.Floor(p.Y / OutfitCell), (int)MathF.Floor(p.Z / OutfitCell));

    /// <summary>
    /// Per cell of the body's surface, the highest the piece's cloth stands above the skin: its vertices and the middles
    /// of its faces, so a face wider than a cell still counts where it spans. Only the piece's own cloth — not the body
    /// skin its refit carries, nor the body mod's piercings and pubic hair (<see cref="BodyMaterial"/>).
    /// </summary>
    internal static Dictionary<(int, int, int), float> OutfitHeights(ModelParts piece, List<BodySurface> surfaces)
    {
        var cells = new Dictionary<(int, int, int), float>();
        void Add(Vector3 p)
        {
            if (!NearestOf(surfaces, p, out var hit)) return;
            float h = Vector3.Dot(p - hit.Point, hit.Normal);
            var key = OutfitCellOf(hit.Point);
            if (!cells.TryGetValue(key, out float had) || h > had) cells[key] = h;
        }
        Vector3 At(int v) => new(piece.Positions[v * 3], piece.Positions[v * 3 + 1], piece.Positions[v * 3 + 2]);
        foreach (var part in piece.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material) || BodyMaterial.IsMatch(part.Material))
                continue;
            var tris = part.Triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var a = At(tris[t]);
                var b = At(tris[t + 1]);
                var c = At(tris[t + 2]);
                Add(a);
                Add(b);
                Add(c);
                Add((a + b + c) / 3f);
            }
        }
        return cells;
    }

    /// <summary>
    /// Where the garment is short of the piece beneath it: per sample — each movable node and the middle of each of its
    /// edges — the author's margin over that piece (<c>m0</c>: the garment's height above the old body less the piece's
    /// highest in that cell), against the margin now (<c>m1</c>, on the new body against the piece as refitted). A
    /// sample short of <c>min(m0, gap)</c> asks for the difference, at the point of the skin under it. A sample the
    /// author had touching the piece or under it asks nothing (<see cref="LayerOrderTouching"/>), and nor does one with
    /// no piece in its cell on either body.
    /// </summary>
    private static List<(Vector3 At, float Need)> OutfitNeeds(Sets sets, Vec3[] nodeDelta, bool[] movable,
                                                             List<BodySurface> source, List<BodySurface> target,
                                                             Dictionary<(int, int, int), float> below,
                                                             Dictionary<(int, int, int), float> now, float gap,
                                                             bool midpoints = true)
    {
        var found = new List<(Vector3 At, float Need)>();
        void Sample(Vector3 authored, Vector3 placed)
        {
            if (!NearestOf(source, authored, out var was)) return;
            if (!below.TryGetValue(OutfitCellOf(was.Point), out float hSrc)) return;
            float m0 = Vector3.Dot(authored - was.Point, was.Normal) - hSrc;
            if (m0 <= LayerOrderTouching) return;
            if (!NearestOf(target, placed, out var hit)) return;
            if (!now.TryGetValue(OutfitCellOf(hit.Point), out float hTgt)) return;
            float m1 = Vector3.Dot(placed - hit.Point, hit.Normal) - hTgt;
            float need = MathF.Min(m0, gap) - m1;
            if (need > 0f) found.Add((hit.Point, need));
        }
        for (int n = 0; n < sets.NodeCount; n++)
        {
            if (!movable[n]) continue;
            var a = ToVector(sets.NodeAt[n]);
            var placed = a + ToVector(nodeDelta[n]);
            Sample(a, placed);
            if (!midpoints) continue;
            foreach (int m in sets.Adj[n])
            {
                if (m <= n || !movable[m]) continue;
                var b = ToVector(sets.NodeAt[m]);
                Sample((a + b) * 0.5f, (placed + b + ToVector(nodeDelta[m])) * 0.5f);
            }
        }

        // One per 2 mm is enough: the field takes the largest within its plateau anyway.
        found.Sort((x, y) => y.Need.CompareTo(x.Need));
        var kept = new List<(Vector3 At, float Need)>();
        var taken = new Dictionary<(int, int, int), List<Vector3>>();
        foreach (var (at, need) in found)
        {
            var key = ((int)MathF.Floor(at.X / OutfitPlateau), (int)MathF.Floor(at.Y / OutfitPlateau),
                       (int)MathF.Floor(at.Z / OutfitPlateau));
            bool near = false;
            for (int dx = -1; dx <= 1 && !near; dx++)
            for (int dy = -1; dy <= 1 && !near; dy++)
            for (int dz = -1; dz <= 1 && !near; dz++)
                if (taken.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list))
                    foreach (var q in list)
                        if (Vector3.DistanceSquared(q, at) <= OutfitPlateau * OutfitPlateau) { near = true; break; }
            if (near) continue;
            if (!taken.TryGetValue(key, out var bucket)) taken[key] = bucket = [];
            bucket.Add(at);
            kept.Add((at, need));
        }
        return kept;
    }

    /// <summary>Triangles <paramref name="nodeDelta"/> turns over against the mesh as authored (see <see cref="Folded"/>).</summary>
    internal static int FoldedTriangles(Sets sets, Vec3[] nodeDelta) => Folded(sets, nodeDelta, new bool[sets.NodeCount]);
}
