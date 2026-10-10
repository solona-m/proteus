using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>How far in front of a cloth point another piece may lie, as authored, and still count as covering it
    /// (10 mm) — see <see cref="KeepLayerOrder"/>.</summary>
    internal const float LayerOrderReach = 0.01f;

    /// <summary>
    /// A piece nearer than this in front of a point (0.2 mm) is touching it, not covering it: where two pieces meet at a
    /// seam, or cross by design, neither is behind.
    /// </summary>
    private const float LayerOrderTouching = 2e-4f;

    /// <summary>The gap <see cref="KeepLayerOrder"/> keeps between a point and the piece covering it, at most (0.5 mm);
    /// less where the author left less.</summary>
    internal const float LayerOrderGap = 5e-4f;

    /// <summary>Rounds of smoothing <see cref="KeepLayerOrder"/> gives the pull, so it fades out rather than stepping.</summary>
    private const int LayerOrderSmoothRounds = 3;

    /// <summary>A cloth point as authored, and the face of another piece in front of it: where on that face, how far.</summary>
    /// <param name="Facing">+1 or -1: which way round the face's winding normal pointed away from the point, as authored.</param>
    internal readonly record struct Covered(int Node, int A, int B, int C, float U, float V, float Gap, float Facing);

    /// <summary>
    /// Every cloth point another piece of the garment covers as authored: the nearest face of ANOTHER piece along the
    /// point's own normal, within <see cref="LayerOrderReach"/>. Another piece is one the cloth only reaches the long way
    /// round (<see cref="LayerKnitDetour"/>, as <see cref="KnitLayers"/> tells them): a bra under a top, a lining under its
    /// shell — not the far side of a fold of the same sheet, nor a piece welded to it at a seam.
    /// </summary>
    /// <param name="layered">Per node, whether it is the garment's own cloth (see <see cref="GarmentCloth"/>); points
    /// and faces of anything else are left out.</param>
    /// <param name="oneWay">Per node, whether its welded vertices face one way (see <see cref="FacesOneWay"/>); a node
    /// that does not is no covered point, its normal being no direction at all. Null takes every node's.</param>
    internal static List<Covered> CoveredPoints(Sets sets, bool[] layered, bool[]? oneWay = null)
    {
        var isSkin = new bool[sets.NodeCount];
        foreach (int n in sets.SkinNodes) isSkin[n] = true;
        for (int n = 0; n < sets.NodeCount; n++)
            if (!layered[n]) isSkin[n] = true;

        // Cloth faces, by node, bucketed by every cell their bounds touch.
        var faces = new List<(int A, int B, int C)>(sets.Tris.Length / 3);
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / LayerOrderReach), (int)MathF.Floor(p.Y / LayerOrderReach),
                                            (int)MathF.Floor(p.Z / LayerOrderReach));
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int a = sets.NodeOf[sets.Tris[t]], b = sets.NodeOf[sets.Tris[t + 1]], c = sets.NodeOf[sets.Tris[t + 2]];
            if (a == b || b == c || a == c || isSkin[a] || isSkin[b] || isSkin[c]) continue;
            int f = faces.Count;
            faces.Add((a, b, c));
            var pa = ToVector(sets.NodeAt[a]);
            var pb = ToVector(sets.NodeAt[b]);
            var pc = ToVector(sets.NodeAt[c]);
            var (x0, y0, z0) = Cell(Vector3.Min(pa, Vector3.Min(pb, pc)));
            var (x1, y1, z1) = Cell(Vector3.Max(pa, Vector3.Max(pb, pc)));
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) grid[(x, y, z)] = bucket = [];
                bucket.Add(f);
            }
        }

        var found = new List<Covered>();
        var seen = new HashSet<int>();
        var way = new Dictionary<int, float>();
        var queue = new PriorityQueue<int, float>();
        foreach (int n in sets.ClothNodes)
        {
            if (isSkin[n] || (oneWay != null && !oneWay[n])) continue;
            var o = ToVector(sets.NodeAt[n]);
            var d = ToVector(sets.NodeNormal[n]);
            if (d.LengthSquared() < 1e-12f) continue;
            d = Vector3.Normalize(d);

            // The faces the ray could meet, from the cells along it.
            seen.Clear();
            int best = -1;
            float bestT = LayerOrderReach, bestU = 0f, bestV = 0f;
            // A quarter of a cell apart, so a ray crossing a cell's corner still looks in it.
            for (int step = 0; step <= 4; step++)
            {
                if (!grid.TryGetValue(Cell(o + d * (LayerOrderReach * step / 4f)), out var bucket)) continue;
                foreach (int f in bucket)
                {
                    if (!seen.Add(f)) continue;
                    var (a, b, c) = faces[f];
                    if (a == n || b == n || c == n) continue;
                    if (!RayHits(o, d, ToVector(sets.NodeAt[a]), ToVector(sets.NodeAt[b]), ToVector(sets.NodeAt[c]),
                                 out float t, out float u, out float v)) continue;
                    if (t <= LayerOrderTouching || t >= bestT) continue;
                    (best, bestT, bestU, bestV) = (f, t, u, v);
                }
            }
            if (best < 0) continue;
            var (fa, fb, fc) = faces[best];

            // A cover faces the way the point does. A point facing IN — a lining's inside, the back of a double-sided
            // sheet — meets the layer beneath it, and kept "behind" that it would push the visible layer outward.
            // A double-sided cover's corners face both ways and say nothing, and are not asked.
            var coverFaces = Vector3.Zero;
            foreach (int corner in new[] { fa, fb, fc })
                if (oneWay == null || oneWay[corner]) coverFaces += ToVector(sets.NodeNormal[corner]);
            if (coverFaces != Vector3.Zero && Vector3.Dot(coverFaces, d) <= 0f) continue;

            // Another piece, not this one folded back: none of the face's corners is reached along the cloth in less
            // than the detour a separate piece takes.
            float limit = MathF.Max(bestT, Sets.SeamWeld) * LayerKnitDetour + sets.MeanEdge;
            way.Clear();
            queue.Clear();
            way[n] = 0f;
            queue.Enqueue(n, 0f);
            bool same = false;
            while (queue.TryDequeue(out int at, out float dist))
            {
                if (dist > way[at]) continue;
                if (at == fa || at == fb || at == fc) { same = true; break; }
                foreach (int m in sets.Adj[at])
                {
                    float to = dist + Vector3.Distance(ToVector(sets.NodeAt[at]), ToVector(sets.NodeAt[m]));
                    if (to > limit || (way.TryGetValue(m, out float had) && had <= to)) continue;
                    way[m] = to;
                    queue.Enqueue(m, to);
                }
            }
            if (same) continue;

            var normal = Vector3.Cross(ToVector(sets.NodeAt[fb]) - ToVector(sets.NodeAt[fa]),
                                       ToVector(sets.NodeAt[fc]) - ToVector(sets.NodeAt[fa]));
            float facing = Vector3.Dot(normal, d) >= 0f ? 1f : -1f;
            found.Add(new Covered(n, fa, fb, fc, bestU, bestV, bestT, facing));
        }
        return found;
    }

    /// <summary>
    /// Keep every covered cloth point behind the piece that covered it as authored: a bra under a top, a lining under its
    /// shell. Where the refit has brought one through the other, the COVERED point goes back behind, to the author's
    /// gap or <see cref="LayerOrderGap"/>, whichever is less — as far as the skin under it allows, and only what it
    /// cannot give is made up by lifting the piece in front, which is the one seen.
    /// <para/>
    /// Each pass before this moves points by their own measure — the transfer by the body under each, the push-out and
    /// the lifts by the skin — and two pieces a few millimetres apart can each be given a different answer. Reported on
    /// "[Dogg] Lumme - Ribbon Galore", a tank top over a bra, Neolithe Almond XS to Rue+ Yiggle Medium: 40 of the 1360
    /// bra points under the top came out in front of it, up to 2.9 mm, the bra showing through the top across the
    /// front of the breast. The underbust lift carries the cloth in front of what it lifts, but by that cloth's
    /// corners, and the top's faces are larger than the bra's: the bra came up through the middle of a face whose
    /// corners the lift never reached.
    /// <para/>
    /// The covered point moves rather than the cover because it cannot be seen: pulled back toward the skin it is hidden
    /// by the very piece it came through. Moving the piece in front instead is a lump on the visible layer (the layer
    /// guard in <see cref="ClearFaces"/> measured that as worse everywhere). Last of all the passes, so nothing moves
    /// either piece after it.
    /// </summary>
    /// <returns>How many points it moved.</returns>
    /// <param name="layered">Per node, whether the pull may reach it (see <see cref="GarmentCloth"/>).</param>
    /// <param name="drawn">The skin drawn under the garment, or null when the refit did not push out: a covered point is
    /// pulled back no further than it stands off this skin, and the cover is lifted by the rest. Hidden only while its
    /// cover is drawn, and Proteus's own part switches can hide the cover: a bra sunk into the breast showed the skin
    /// through it the moment the top was switched off.</param>
    internal static int KeepLayerOrder(Sets sets, IReadOnlyList<Covered> covered, Vec3[] nodeDelta, bool[]? stay,
                                       bool[] layered, TargetBody? drawn = null)
    {
        if (covered.Count == 0) return 0;
        var movable = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) movable[n] = layered[n] && (stay == null || !stay[n]);

        // Again until nothing moves: each round measures every pair as it stands, and with three layers pulling the
        // middle one behind the outer can take it behind the inner.
        int moved = 0;
        for (int round = 0; round < LayerOrderRounds; round++)
        {
            int now = KeepLayerOrderOnce(sets, covered, nodeDelta, movable, drawn);
            if (now == 0) break;
            moved += now;
        }
        return moved;
    }

    /// <summary>Rounds <see cref="KeepLayerOrder"/> measures and moves again, at most (3).</summary>
    private const int LayerOrderRounds = 3;

    private static int KeepLayerOrderOnce(Sets sets, IReadOnlyList<Covered> covered, Vec3[] nodeDelta, bool[] movable,
                                          TargetBody? drawn)
    {
        Vector3 Now(int n) => ToVector(sets.NodeAt[n]) + ToVector(nodeDelta[n]);
        bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        // What each covered point is short of its gap, and the cover face it is short against.
        var need = new float[sets.NodeCount];
        var dir = new Vector3[sets.NodeCount];
        var against = new int[sets.NodeCount];
        Array.Fill(against, -1);
        for (int i = 0; i < covered.Count; i++)
        {
            var c = covered[i];
            if (!movable[c.Node]) continue;
            var a = Now(c.A);
            var b = Now(c.B);
            var cc = Now(c.C);
            var p = Now(c.Node);
            // A point with no finite answer is put back where it was by Solve; it measures nothing and spreads nothing.
            if (!Finite(a) || !Finite(b) || !Finite(cc) || !Finite(p)) continue;
            var normal = Vector3.Cross(b - a, cc - a);
            if (normal.LengthSquared() < 1e-16f) continue;
            normal = Vector3.Normalize(normal) * c.Facing;   // away from the covered point, as authored
            var at = a * (1f - c.U - c.V) + b * c.U + cc * c.V;
            float short_ = MathF.Min(c.Gap, LayerOrderGap) - Vector3.Dot(at - p, normal);
            if (short_ <= 0f || short_ <= need[c.Node]) continue;
            need[c.Node] = short_;
            dir[c.Node] = -normal;
            against[c.Node] = i;
        }

        // How far each point may go back before it reaches the skin.
        var room = new float[sets.NodeCount];
        Array.Fill(room, float.PositiveInfinity);
        if (drawn != null)
            for (int n = 0; n < sets.NodeCount; n++)
            {
                if (!movable[n]) continue;
                var p = Now(n);
                if (Finite(p) && drawn.Deepest(p, PushProbeRange, out var hit))
                    room[n] = MathF.Max(0f, Vector3.Dot(p - hit.Point, hit.Normal));
            }

        // Back behind the cover, faded onto the neighbours, no further than the skin allows.
        var pull = Spread(sets, need, dir, movable);
        for (int n = 0; n < sets.NodeCount; n++) pull[n] = MathF.Min(pull[n], room[n]);

        // The rest is the cover's to make up: its face lifted off the point by what the point could not give.
        var lift = new float[sets.NodeCount];
        var way = new Vector3[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++)
        {
            if (against[n] < 0) continue;
            float rest = need[n] - pull[n];
            if (rest <= 0f) continue;
            var c = covered[against[n]];
            foreach (int m in new[] { c.A, c.B, c.C })
            {
                if (!movable[m] || rest <= lift[m]) continue;
                lift[m] = rest;
                way[m] = -dir[n];
            }
        }
        var raise = Spread(sets, lift, way, movable);

        int moved = 0;
        for (int n = 0; n < sets.NodeCount; n++)
        {
            var d = Vector3.Zero;
            if (pull[n] > 0f && dir[n] != default) d += dir[n] * pull[n];
            if (raise[n] > 0f && way[n] != default) d += way[n] * raise[n];
            if (d == Vector3.Zero || !movable[n]) continue;
            nodeDelta[n] = new Vec3(nodeDelta[n].X + d.X, nodeDelta[n].Y + d.Y, nodeDelta[n].Z + d.Z);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// <paramref name="amount"/> faded onto the neighbours over <see cref="LayerOrderSmoothRounds"/>, so a piece is not
    /// stepped where its moved points end; never below what a point itself asked for. A neighbour that asked for nothing
    /// takes the direction of the neighbour it takes the most from (written into <paramref name="dir"/>).
    /// </summary>
    private static float[] Spread(Sets sets, float[] amount, Vector3[] dir, bool[] movable)
    {
        var spread = (float[])amount.Clone();
        for (int round = 0; round < LayerOrderSmoothRounds; round++)
        {
            var next = (float[])spread.Clone();
            for (int n = 0; n < sets.NodeCount; n++)
            {
                if (!movable[n] || sets.Adj[n].Count == 0) continue;
                float sum = 0f, most = 0f;
                int from = -1;
                foreach (int m in sets.Adj[n])
                {
                    sum += spread[m];
                    if (spread[m] > most) { most = spread[m]; from = m; }
                }
                if (most <= 0f) continue;
                next[n] = MathF.Max(amount[n], 0.5f * spread[n] + 0.5f * sum / sets.Adj[n].Count);
                if (dir[n] == default && from >= 0) dir[n] = dir[from];
            }
            spread = next;
        }
        return spread;
    }

    /// <summary>
    /// Per node, whether it is the garment's own cloth, which <see cref="KeepLayerOrder"/> keeps in order: not the body's
    /// skin, and not what a body mod draws on it — pubic hair, piercings, nails, any <c>mt_c####b####_</c> material. A
    /// body mod ships those as ALTERNATIVES, one switched on at a time, stacked on the same skin: Neolithe's two pubic
    /// hair meshes lie within a millimetre of each other, and keeping one behind the other sank whichever was drawn up to
    /// 1.3 mm into the skin.
    /// </summary>
    internal static bool[] GarmentCloth(ModelParts garment, Sets sets)
    {
        var cloth = new bool[sets.NodeCount];
        var body = new bool[sets.NodeCount];
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0) continue;
            bool isBody = BodyMaterial.IsMatch(part.Material);
            foreach (int v in part.Triangles)
            {
                if (v < 0 || v >= sets.NodeOf.Length) continue;
                if (isBody) body[sets.NodeOf[v]] = true;
                else cloth[sets.NodeOf[v]] = true;
            }
        }
        for (int n = 0; n < sets.NodeCount; n++) cloth[n] &= !body[n];
        return cloth;
    }

    /// <summary>
    /// Per node, whether the vertices welded into it face one way: their normals' average is at least half a unit long.
    /// The front and back of a double-sided sheet weld into one node whose summed normal is next to nothing, and the
    /// direction <see cref="Sets.NodeNormal"/> makes of that is noise.
    /// </summary>
    internal static bool[] FacesOneWay(ModelParts garment, Sets sets)
    {
        var sum = new Vector3[sets.NodeCount];
        var count = new int[sets.NodeCount];
        int vc = Math.Min(sets.NodeOf.Length, garment.Normals.Length / 3);
        for (int v = 0; v < vc; v++)
        {
            var nv = new Vector3(garment.Normals[v * 3], garment.Normals[v * 3 + 1], garment.Normals[v * 3 + 2]);
            if (nv.LengthSquared() < 1e-12f) continue;
            sum[sets.NodeOf[v]] += Vector3.Normalize(nv);
            count[sets.NodeOf[v]]++;
        }
        var oneWay = new bool[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++)
            oneWay[n] = count[n] > 0 && sum[n].Length() >= 0.5f * count[n];
        return oneWay;
    }

    private static readonly System.Text.RegularExpressions.Regex BodyMaterial = new(@"(^|/)mt_c\d{4}b\d{4}_",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Möller–Trumbore: where the ray from <paramref name="o"/> along <paramref name="d"/> meets triangle
    /// abc, with the hit's barycentric weights on b and c.</summary>
    private static bool RayHits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
    {
        t = u = v = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-14f) return false;
        float inv = 1f / det;
        var tv = o - a;
        u = Vector3.Dot(tv, pv) * inv;
        if (u < 0f || u > 1f) return false;
        var qv = Vector3.Cross(tv, e1);
        v = Vector3.Dot(d, qv) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, qv) * inv;
        return t > 0f;
    }
}
