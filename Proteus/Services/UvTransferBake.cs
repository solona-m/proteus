using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Region = Proteus.Services.UvBakeBones.Region;

namespace Proteus.Services;

/// <summary>
/// One body's skin as a transfer-map bake reads it: every LOD0 skin triangle of its parts, with each triangle's uvs
/// wrapped into the 0..1 tile by its centroid (Bibo+ files its legs a tile below), each vertex's body region, and the
/// open rim of the welded surface.
/// </summary>
internal sealed class BakeBody
{
    /// <summary>One model's LOD0 skin, as <see cref="SecondSkinWriter.TryReadLod0Geometry(byte[], out float[], out float[], out int[], out (string, float)[][], out float[], bool, bool, Func{string, bool})"/> gives it.</summary>
    internal sealed record Part(float[] Positions, float[] Uvs, int[] Triangles, (string Bone, float W)[][] Weights,
                                float[] Normals, int Option = 0);

    public readonly Vector3[] P;
    public readonly Vector3[] N;
    public readonly int[] Tri;
    /// <summary>Per triangle corner, its uv wrapped into the 0..1 tile by the triangle's centroid.</summary>
    public readonly Vector2[] Uv;
    public readonly Region[] VertexRegion;
    public readonly Region[] TriRegion;
    /// <summary>Per triangle, which of the inputs it came from (a legs option, say).</summary>
    public readonly int[] TriOption;
    public readonly Vector3[] FaceNormal;
    /// <summary>The open rim after welding by position: segments of edges only one triangle uses.</summary>
    public readonly RimIndex Rim;
    public readonly (string Bone, float W)[][] Weights;

    public int TriangleCount => Tri.Length / 3;

    public BakeBody(IReadOnlyList<Part> parts)
    {
        var p = new List<Vector3>();
        var n = new List<Vector3>();
        var tri = new List<int>();
        var opt = new List<int>();
        var uvv = new List<Vector2>();
        var w = new List<(string, float)[]>();
        foreach (var part in parts)
        {
            int baseV = p.Count;
            int count = part.Positions.Length / 3;
            for (int v = 0; v < count; v++)
            {
                p.Add(new Vector3(part.Positions[v * 3], part.Positions[v * 3 + 1], part.Positions[v * 3 + 2]));
                var nv = part.Normals.Length >= (v + 1) * 3
                    ? new Vector3(part.Normals[v * 3], part.Normals[v * 3 + 1], part.Normals[v * 3 + 2]) : Vector3.Zero;
                n.Add(nv.LengthSquared() > 1e-12f ? Vector3.Normalize(nv) : Vector3.Zero);
                uvv.Add(new Vector2(part.Uvs[v * 2], part.Uvs[v * 2 + 1]));
                w.Add(v < part.Weights.Length ? part.Weights[v] : []);
            }
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                tri.Add(baseV + part.Triangles[t]);
                tri.Add(baseV + part.Triangles[t + 1]);
                tri.Add(baseV + part.Triangles[t + 2]);
                opt.Add(part.Option);
            }
        }

        P = p.ToArray();
        N = n.ToArray();
        Tri = tri.ToArray();
        TriOption = opt.ToArray();
        Weights = w.ToArray();

        int tc = TriangleCount;
        Uv = new Vector2[tc * 3];
        FaceNormal = new Vector3[tc];
        for (int t = 0; t < tc; t++)
        {
            Vector2 a = uvv[Tri[t * 3]], b = uvv[Tri[t * 3 + 1]], c = uvv[Tri[t * 3 + 2]];
            var shift = new Vector2(MathF.Floor((a.X + b.X + c.X) / 3f), MathF.Floor((a.Y + b.Y + c.Y) / 3f));
            Uv[t * 3] = a - shift;
            Uv[t * 3 + 1] = b - shift;
            Uv[t * 3 + 2] = c - shift;
            var fn = Vector3.Cross(P[Tri[t * 3 + 1]] - P[Tri[t * 3]], P[Tri[t * 3 + 2]] - P[Tri[t * 3]]);
            FaceNormal[t] = fn.LengthSquared() > 1e-20f ? Vector3.Normalize(fn) : Vector3.Zero;
        }

        VertexRegion = UvBakeBones.VertexRegions(Weights, Tri);
        TriRegion = new Region[tc];
        for (int t = 0; t < tc; t++)
        {
            Region a = VertexRegion[Tri[t * 3]], b = VertexRegion[Tri[t * 3 + 1]], c = VertexRegion[Tri[t * 3 + 2]];
            TriRegion[t] = a == b || a == c ? a : b == c ? b : a;
        }

        // Covered: a step off the edge, away from its own triangle and along the surface, still finds skin there.
        var cover = new TriangleBvh(this);
        Rim = RimIndex.Build(P, Tri, (a, b, t) =>
        {
            var mid = (a + b) * 0.5f;
            var c = (P[Tri[t * 3]] + P[Tri[t * 3 + 1]] + P[Tri[t * 3 + 2]]) / 3f;
            var edge = b - a;
            var outward = mid - c;
            if (edge.LengthSquared() > 1e-20f) outward -= edge * (Vector3.Dot(outward, edge) / edge.LengthSquared());
            if (outward.LengthSquared() < 1e-20f) return false;
            var q = mid + Vector3.Normalize(outward) * CoverStep;
            return cover.Closest(q, CoverReach, o => o != t, out _, out _, out _, out _, out _);
        });
        Smooth = SmoothNormals(SmoothPasses);
    }

    /// <summary>How far off an open edge, along the surface, the bake looks for skin lapped over it.</summary>
    internal const float CoverStep = 0.005f;

    /// <summary>How near that point skin must be for the edge to be an overlap, not a hole.</summary>
    internal const float CoverReach = 0.003f;

    /// <summary>
    /// How many times each vertex's normal is averaged with its neighbours' for the projection: about the width of
    /// the cleavage, so a flat chest's texels are cast along the chest's own facing, not the breast's turned flank.
    /// </summary>
    internal const int SmoothPasses = 20;

    /// <summary>Per vertex, the normal averaged over the welded surface around it (<see cref="SmoothPasses"/> rings).</summary>
    public readonly Vector3[] Smooth;

    private Vector3[] SmoothNormals(int passes)
    {
        // Welded first: a uv seam splits a vertex in two, and averaging must not stop at it.
        var id = RimIndex.WeldIds(P);
        var neighbours = new HashSet<int>[P.Length];
        for (int t = 0; t + 2 < Tri.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = id[Tri[t + k]], b = id[Tri[t + (k + 1) % 3]];
                if (a == b) continue;
                (neighbours[a] ??= []).Add(b);
                (neighbours[b] ??= []).Add(a);
            }
        var cur = new Vector3[P.Length];
        for (int v = 0; v < P.Length; v++) cur[id[v]] += N[v];
        var next = new Vector3[P.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int v = 0; v < P.Length; v++)
            {
                if (id[v] != v) continue;
                var sum = cur[v];
                if (neighbours[v] != null) foreach (int u in neighbours[v]) sum += cur[u];
                next[v] = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : cur[v];
            }
            (cur, next) = (next, cur);
        }
        var smooth = new Vector3[P.Length];
        for (int v = 0; v < P.Length; v++)
            smooth[v] = cur[id[v]].LengthSquared() > 1e-12f ? Vector3.Normalize(cur[id[v]]) : N[v];
        return smooth;
    }

    /// <summary>The smoothed normal at barycentrics (u, v, w) of triangle <paramref name="t"/>.</summary>
    public Vector3 SmoothAt(int t, float u, float v, float w)
    {
        var n = Smooth[Tri[t * 3]] * u + Smooth[Tri[t * 3 + 1]] * v + Smooth[Tri[t * 3 + 2]] * w;
        return n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : FaceNormal[t];
    }

    /// <summary>The surface point, interpolated normal and uv of triangle <paramref name="t"/> at barycentrics (u, v, w).</summary>
    public (Vector3 Pos, Vector3 Normal, Vector2 Uv) At(int t, float u, float v, float w)
    {
        int a = Tri[t * 3], b = Tri[t * 3 + 1], c = Tri[t * 3 + 2];
        var pos = P[a] * u + P[b] * v + P[c] * w;
        var nrm = N[a] * u + N[b] * v + N[c] * w;
        nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : FaceNormal[t];
        var uv = Uv[t * 3] * u + Uv[t * 3 + 1] * v + Uv[t * 3 + 2] * w;
        return (pos, nrm, uv);
    }

    /// <summary>The region at barycentrics (u, v, w): the region of the corner weighted most.</summary>
    public Region RegionAt(int t, float u, float v, float w)
        => VertexRegion[Tri[t * 3 + (u >= v && u >= w ? 0 : v >= w ? 1 : 2)]];
}

/// <summary>
/// The open rim of a surface — edges only one triangle uses once its vertices are welded by position — bucketed so
/// "how far is this point from the rim" is cheap.
/// </summary>
internal sealed class RimIndex
{
    private const float Weld = 0.0001f;   // 0.1 mm: one point of the surface split only by a uv seam
    private const float Cell = 0.02f;
    private readonly Dictionary<(int, int, int), List<(Vector3 A, Vector3 B)>> cells = [];

    public int SegmentCount { get; private set; }

    /// <summary>Per vertex, the first vertex at the same position (to 0.1 mm): one point of the surface, however split.</summary>
    public static int[] WeldIds(Vector3[] p)
    {
        var id = new int[p.Length];
        var byKey = new Dictionary<(long, long, long), int>();
        for (int v = 0; v < p.Length; v++)
        {
            var key = ((long)MathF.Round(p[v].X / Weld), (long)MathF.Round(p[v].Y / Weld), (long)MathF.Round(p[v].Z / Weld));
            if (!byKey.TryGetValue(key, out int w)) byKey[key] = w = v;
            id[v] = w;
        }
        return id;
    }

    /// <summary>
    /// The holes in a surface: the edges only one triangle uses once vertices are welded by position, less those
    /// <paramref name="covered"/> says another piece of skin lies over. A body is built of overlapping pieces — the
    /// chest runs down over the legs at the waist, the thigh is two meshes lapped at mid-thigh — and a piece's edge
    /// there is no hole; only an edge nothing covers (a cut-out under the undies, the neck) is.
    /// </summary>
    /// <param name="covered">Given an edge's ends and the triangle that has the edge, whether other skin is there.</param>
    public static RimIndex Build(Vector3[] p, int[] tri, Func<Vector3, Vector3, int, bool>? covered = null)
    {
        var id = WeldIds(p);
        var uses = new Dictionary<(int, int), (int Count, int Tri)>();
        for (int t = 0; t + 2 < tri.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = id[tri[t + k]], b = id[tri[t + (k + 1) % 3]];
                if (a == b) continue;
                var e = a < b ? (a, b) : (b, a);
                var (count, first) = uses.GetValueOrDefault(e, (0, t / 3));
                uses[e] = (count + 1, first);
            }
        var open = new List<(int A, int B, int Tri)>();
        foreach (var (e, (count, t)) in uses)
            if (count == 1) open.Add((e.Item1, e.Item2, t));
        open.Sort();

        var rim = new RimIndex();
        foreach (var (a, b, t) in open)
            if (covered == null || !covered(p[a], p[b], t)) rim.Add(p[a], p[b]);
        return rim;
    }

    private readonly List<(Vector3 A, Vector3 B)> segments = [];

    /// <summary>The rim's edges.</summary>
    public IReadOnlyList<(Vector3 A, Vector3 B)> Segments => segments;

    private void Add(Vector3 a, Vector3 b)
    {
        SegmentCount++;
        segments.Add((a, b));
        var lo = Vector3.Min(a, b);
        var hi = Vector3.Max(a, b);
        for (int x = (int)MathF.Floor(lo.X / Cell); x <= (int)MathF.Floor(hi.X / Cell); x++)
            for (int y = (int)MathF.Floor(lo.Y / Cell); y <= (int)MathF.Floor(hi.Y / Cell); y++)
                for (int z = (int)MathF.Floor(lo.Z / Cell); z <= (int)MathF.Floor(hi.Z / Cell); z++)
                {
                    if (!cells.TryGetValue((x, y, z), out var list)) cells[(x, y, z)] = list = [];
                    list.Add((a, b));
                }
    }

    /// <summary>Whether <paramref name="q"/> lies within <paramref name="reach"/> (at most one cell) of the rim.</summary>
    public bool Near(Vector3 q, float reach)
    {
        int cx = (int)MathF.Floor(q.X / Cell), cy = (int)MathF.Floor(q.Y / Cell), cz = (int)MathF.Floor(q.Z / Cell);
        float r2 = reach * reach;
        for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
                for (int z = cz - 1; z <= cz + 1; z++)
                {
                    if (!cells.TryGetValue((x, y, z), out var list)) continue;
                    foreach (var (a, b) in list)
                    {
                        var ab = b - a;
                        float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(q - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
                        if ((a + ab * t - q).LengthSquared() <= r2) return true;
                    }
                }
        return false;
    }
}

/// <summary>A bounding-volume hierarchy over one body's triangles: ray and closest-point queries, in a fixed build order.</summary>
internal sealed class TriangleBvh
{
    private readonly struct Node(Vector3 lo, Vector3 hi, int left, int right, int start, int count)
    {
        public readonly Vector3 Lo = lo, Hi = hi;
        public readonly int Left = left, Right = right, Start = start, Count = count;
    }

    private readonly BakeBody body;
    private readonly int[] order;
    private readonly List<Node> nodes = [];

    public TriangleBvh(BakeBody body)
    {
        this.body = body;
        order = new int[body.TriangleCount];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        var centroid = new Vector3[order.Length];
        for (int t = 0; t < order.Length; t++)
            centroid[t] = (body.P[body.Tri[t * 3]] + body.P[body.Tri[t * 3 + 1]] + body.P[body.Tri[t * 3 + 2]]) / 3f;
        if (order.Length > 0) Build(0, order.Length, centroid);
    }

    private int Build(int start, int count, Vector3[] centroid)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++)
            for (int k = 0; k < 3; k++)
            {
                var p = body.P[body.Tri[order[i] * 3 + k]];
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
        int index = nodes.Count;
        nodes.Add(default);
        if (count <= 4)
        {
            nodes[index] = new Node(lo, hi, -1, -1, start, count);
            return index;
        }
        var ext = hi - lo;
        int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
        // Stable: ties broken by triangle index, so the tree, and every answer from it, is the same each build.
        Array.Sort(order, start, count, Comparer<int>.Create((a, b) =>
        {
            int c = Axis(centroid[a], axis).CompareTo(Axis(centroid[b], axis));
            return c != 0 ? c : a.CompareTo(b);
        }));
        int half = count / 2;
        int left = Build(start, half, centroid);
        int right = Build(start + half, count - half, centroid);
        nodes[index] = new Node(lo, hi, left, right, start, count);
        return index;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>
    /// The hit nearest <paramref name="origin"/> along the line through it in <paramref name="dir"/>, either way, within
    /// <paramref name="reach"/>, among triangles <paramref name="accept"/> allows. Barycentrics of the hit.
    /// </summary>
    public bool Ray(Vector3 origin, Vector3 dir, float reach, Func<int, bool> accept,
                    out int tri, out float u, out float v, out float w, out float dist)
    {
        tri = -1; u = v = w = 0f; dist = reach;
        if (nodes.Count == 0) return false;
        var stack = new Stack<int>();
        stack.Push(0);
        var a0 = origin - dir * reach;
        var a1 = origin + dir * reach;
        var segLo = Vector3.Min(a0, a1);
        var segHi = Vector3.Max(a0, a1);
        while (stack.Count > 0)
        {
            var node = nodes[stack.Pop()];
            if (node.Hi.X < segLo.X || node.Lo.X > segHi.X || node.Hi.Y < segLo.Y || node.Lo.Y > segHi.Y
                || node.Hi.Z < segLo.Z || node.Lo.Z > segHi.Z) continue;
            if (node.Left >= 0) { stack.Push(node.Right); stack.Push(node.Left); continue; }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int t = order[i];
                if (!accept(t)) continue;
                var p0 = body.P[body.Tri[t * 3]];
                var e1 = body.P[body.Tri[t * 3 + 1]] - p0;
                var e2 = body.P[body.Tri[t * 3 + 2]] - p0;
                var pv = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, pv);
                if (MathF.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var tv = origin - p0;
                float bu = Vector3.Dot(tv, pv) * inv;
                if (bu < 0f || bu > 1f) continue;
                var qv = Vector3.Cross(tv, e1);
                float bv = Vector3.Dot(dir, qv) * inv;
                if (bv < 0f || bu + bv > 1f) continue;
                float s = Vector3.Dot(e2, qv) * inv;
                float d = MathF.Abs(s);
                if (d > dist || (d == dist && t > tri && tri >= 0)) continue;
                dist = d; tri = t; u = 1f - bu - bv; v = bu; w = bv;
            }
        }
        return tri >= 0;
    }

    /// <summary>The closest point to <paramref name="q"/> within <paramref name="reach"/>, among triangles <paramref name="accept"/> allows.</summary>
    public bool Closest(Vector3 q, float reach, Func<int, bool> accept,
                        out int tri, out float u, out float v, out float w, out float dist)
    {
        tri = -1; u = v = w = 0f;
        float best2 = reach * reach;
        if (nodes.Count == 0) { dist = reach; return false; }
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = nodes[stack.Pop()];
            var c = Vector3.Clamp(q, node.Lo, node.Hi);
            if ((c - q).LengthSquared() > best2) continue;
            if (node.Left >= 0) { stack.Push(node.Right); stack.Push(node.Left); continue; }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int t = order[i];
                if (!accept(t)) continue;
                var (cp, bu, bv, bw) = ClosestOnTriangle(q, body.P[body.Tri[t * 3]], body.P[body.Tri[t * 3 + 1]],
                                                         body.P[body.Tri[t * 3 + 2]]);
                float d2 = (cp - q).LengthSquared();
                if (d2 > best2 || (d2 == best2 && tri >= 0 && t > tri)) continue;
                best2 = d2; tri = t; u = bu; v = bv; w = bw;
            }
        }
        dist = MathF.Sqrt(best2);
        return tri >= 0;
    }

    /// <summary>The point of triangle abc closest to <paramref name="p"/>, with its barycentrics (Ericson, 5.1.5).</summary>
    internal static (Vector3 Point, float U, float V, float W) ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return (a, 1, 0, 0);
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return (b, 0, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            return (a + ab * t, 1 - t, t, 0);
        }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return (c, 0, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            return (a + ac * t, 1 - t, 0, t);
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return (b + (c - b) * t, 0, 1 - t, t);
        }
        float denom = 1f / (va + vb + vc);
        float v = vb * denom, w = vc * denom;
        return (a + ab * v + ac * w, 1 - v - w, v, w);
    }
}

/// <summary>
/// A uv transfer map baked between two bodies whose skin shares no layout: for every texel of the DESTINATION body's
/// sheet, where on the SOURCE body's sheet the same point of the body is. The map is indexed by a destination pixel and
/// stores a source coordinate — the convention of <see cref="UVRemapService"/>'s <c>{source}_to_{destination}</c> files.
/// <para/>
/// Each destination texel is rasterised to its point on the destination's surface; that point is projected along its
/// normal onto the source's surface, among triangles of the same or a neighbouring body region whose normal agrees, and
/// the source uv there is written. Where the projection finds nothing, the closest such point within reach stands in.
/// <para/>
/// Landings at the source's open rim are kept. The nude bodies the maps are baked from have no holes in their skin, and
/// what rims they have are the ends of the body (neck, waist, wrists, ankles) and the joins between lapped pieces — a
/// rule refusing them dropped skin that was there and folded what fell back elsewhere (measured, 2026-10-20).
/// </summary>
internal static class UvTransferBake
{
    internal const float Reach = 0.08f;          // 8 cm: bodies bent to one race differ by less, limb for limb
    internal const float RimReach = 0.01f;
    internal const float NormalAgree = 0.3f;

    internal sealed class Stats
    {
        public long OwnedPixels, ValidPixels, RayHits, ClosestFallbacks, RegionAdjacentHits, Unreached;
        public long OverlapPixels;
        /// <summary>The sheet half filled by mirror because no texel of it was reached, or null.</summary>
        public string? MirrorFilled;
        public Dictionary<(int, int), long> OverlapByOption = [];
        public double DestCoverage => OwnedPixels == 0 ? 0 : (double)ValidPixels / OwnedPixels;
    }

    internal sealed record Result(ushort[] R, ushort[] G, bool[] Valid, int Size, Stats Stats);

    /// <summary>Which destination triangle owns each pixel, and where in it — the first triangle in order to reach a pixel.</summary>
    internal sealed record Raster(int[] Owner, float[] BaryU, float[] BaryV, int Size, long Overlap,
                                  Dictionary<(int, int), long> OverlapByOption);

    /// <summary>
    /// Every pixel any triangle of <paramref name="body"/> overlaps (its centre within half a pixel's diagonal of the
    /// triangle), owned by the first triangle in order to reach it, with that triangle's barycentrics clamped inside it.
    /// </summary>
    internal static Raster Rasterize(BakeBody body, int size)
    {
        var owner = new int[size * size];
        Array.Fill(owner, -1);
        var bu = new float[size * size];
        var bv = new float[size * size];
        long overlap = 0;
        var byOption = new Dictionary<(int, int), long>();
        const float Tol = 0.70711f;
        float scale = size - 1;
        for (int t = 0; t < body.TriangleCount; t++)
        {
            var a = body.Uv[t * 3] * scale;
            var b = body.Uv[t * 3 + 1] * scale;
            var c = body.Uv[t * 3 + 2] * scale;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) - Tol));
            int x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)) + Tol));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - Tol));
            int y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) + Tol));
            var a3 = new Vector3(a, 0); var b3 = new Vector3(b, 0); var c3 = new Vector3(c, 0);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var (cp, u, v, w) = TriangleBvh.ClosestOnTriangle(new Vector3(x, y, 0), a3, b3, c3);
                    if ((cp.X - x) * (cp.X - x) + (cp.Y - y) * (cp.Y - y) > Tol * Tol) continue;
                    int i = y * size + x;
                    if (owner[i] >= 0)
                    {
                        if (owner[i] == t) continue;
                        int oa = body.TriOption[owner[i]], ob = body.TriOption[t];
                        // Options share most of their skin texel for texel, and that is no conflict; one texel
                        // drawn at two places of the body (a crotch modelled differently) is.
                        if (oa != ob && (body.At(owner[i], bu[i], bv[i], 1 - bu[i] - bv[i]).Pos
                                         - body.At(t, u, v, w).Pos).LengthSquared() > 0.01f * 0.01f)
                        {
                            overlap++;
                            var key = oa < ob ? (oa, ob) : (ob, oa);
                            byOption[key] = byOption.GetValueOrDefault(key) + 1;
                        }
                        continue;
                    }
                    owner[i] = t; bu[i] = u; bv[i] = v;
                }
        }
        return new Raster(owner, bu, bv, size, overlap, byOption);
    }

    /// <summary>Where on <paramref name="src"/> the destination point at (<paramref name="p"/>, <paramref name="n"/>) of region <paramref name="region"/> lands.</summary>
    internal readonly record struct Landing(bool Found, int Tri, float U, float V, float W, bool ByRay, bool Adjacent);

    internal static Landing Land(TriangleBvh bvh, BakeBody src, Vector3 p, Vector3 n, Region region)
    {
        bool Accept(int t) => UvBakeBones.Adjacent(region, src.TriRegion[t]) && Vector3.Dot(src.FaceNormal[t], n) > NormalAgree;
        bool Facing(int t) => UvBakeBones.Adjacent(region, src.TriRegion[t]) && Vector3.Dot(src.FaceNormal[t], n) > 0f;
        if (n != Vector3.Zero && bvh.Ray(p, n, Reach, Accept, out int t1, out float u1, out float v1, out float w1, out _))
            return new Landing(true, t1, u1, v1, w1, true, src.TriRegion[t1] != region);
        if (bvh.Closest(p, Reach, Facing, out int t2, out float u2, out float v2, out float w2, out _))
            return new Landing(true, t2, u2, v2, w2, false, src.TriRegion[t2] != region);
        return default;
    }

    /// <summary>The map from <paramref name="src"/>'s sheet to <paramref name="dest"/>'s: indexed by a destination pixel.</summary>
    internal static Result Bake(BakeBody dest, BakeBody src, int size, Raster? destRaster = null)
    {
        var raster = destRaster ?? Rasterize(dest, size);
        var bvh = new TriangleBvh(src);
        var r = new ushort[size * size];
        var g = new ushort[size * size];
        var valid = new bool[size * size];
        var stats = new Stats { OverlapPixels = raster.Overlap, OverlapByOption = raster.OverlapByOption };

        Parallel.For(0, size, () => new long[5], (y, _, local) =>
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                int t = raster.Owner[i];
                if (t < 0) continue;
                local[0]++;
                float u = raster.BaryU[i], v = raster.BaryV[i], w = 1f - u - v;
                var pos = dest.At(t, u, v, w).Pos;
                var nrm = dest.SmoothAt(t, u, v, w);
                var region = dest.RegionAt(t, u, v, w);
                var land = Land(bvh, src, pos, nrm, region);
                if (!land.Found) { local[4]++; continue; }
                var uv = src.At(land.Tri, land.U, land.V, land.W).Uv;
                if (land.ByRay) local[1]++; else local[2]++;
                if (land.Adjacent) local[3]++;
                r[i] = Quantize(uv.X);
                g[i] = Quantize(uv.Y);
                valid[i] = true;
            }
            return local;
        }, local =>
        {
            Interlocked.Add(ref stats.OwnedPixels, local[0]);
            Interlocked.Add(ref stats.RayHits, local[1]);
            Interlocked.Add(ref stats.ClosestFallbacks, local[2]);
            Interlocked.Add(ref stats.RegionAdjacentHits, local[3]);
            Interlocked.Add(ref stats.Unreached, local[4]);
        });
        stats.ValidPixels = stats.RayHits + stats.ClosestFallbacks;
        return new Result(r, g, valid, size, stats);
    }

    internal static ushort Quantize(float c) => (ushort)MathF.Round(Math.Clamp(c, 0f, 1f) * 65535f);
}
