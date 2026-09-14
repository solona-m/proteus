using System;
using System.Collections.Generic;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// Where a reinforced toe ENDS: a straight line across the foot, the way a real reinforced toe is knitted.
/// <para/>
/// The region used to be the cap's UV footprint, and its edge showed two faults in game. It stair-stepped,
/// because the footprint is a 512 map stretched over the sheet; and it wandered — a notch stepping up one
/// side — because it traced the outline of the cap's triangles in the atlas, which is not a line of any kind.
/// A sharper footprint fixes only the first.
/// <para/>
/// So the edge is defined in 3D instead. A plane is fitted through the cap's outer rim, and every point of
/// the foot on the toe side of it is reinforced, fading out over a short band behind it. A plane through a
/// foot meets its surface in a clean line. The signed distance to the plane is affine across each triangle,
/// so it is interpolated per texel at sheet resolution and the contour is exact — no texel grid in the edge.
/// </summary>
internal static class ToeLine
{
    /// <summary>
    /// The cap's OUTER rim: of the boundary loops of the cap mesh, the one with the longest perimeter.
    /// <para/>
    /// Not every boundary vertex, because a cap can have more than one boundary — an opening around a
    /// toenail, say — and those points lie well forward of the rim, where they would tilt the plane and
    /// slant the line.
    /// </summary>
    /// <param name="tri">Triangle vertex indices, three per face.</param>
    /// <param name="pos">Position per vertex.</param>
    /// <returns>Vertex indices of the outer rim; empty when the mesh has no boundary.</returns>
    public static List<int> OuterRim(IReadOnlyList<int> tri, IReadOnlyList<Vec3> pos)
    {
        var use = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < tri.Count; t += 3)
        {
            int a = tri[t], b = tri[t + 1], c = tri[t + 2];
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var e = (Math.Min(x, y), Math.Max(x, y));
                use[e] = use.GetValueOrDefault(e) + 1;
            }
        }

        var adj = new Dictionary<int, List<int>>();
        var boundary = new List<(int A, int B)>();
        foreach (var (e, n) in use)
        {
            if (n != 1) continue;
            boundary.Add(e);
            if (!adj.TryGetValue(e.Item1, out var la)) adj[e.Item1] = la = [];
            if (!adj.TryGetValue(e.Item2, out var lb)) adj[e.Item2] = lb = [];
            la.Add(e.Item2);
            lb.Add(e.Item1);
        }
        if (boundary.Count == 0) return [];

        // Label each boundary component, then measure every component's perimeter off the edge list.
        var label = new Dictionary<int, int>();
        int labels = 0;
        foreach (int start in adj.Keys)
        {
            if (label.ContainsKey(start)) continue;
            var stack = new Stack<int>();
            stack.Push(start);
            label[start] = labels;
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                foreach (int w in adj[v])
                    if (label.TryAdd(w, labels)) stack.Push(w);
            }
            labels++;
        }

        var perimeter = new double[labels];
        foreach (var (a, b) in boundary)
            if (a < pos.Count && b < pos.Count)
                perimeter[label[a]] += Length(Sub(pos[a], pos[b]));

        int best = 0;
        for (int i = 1; i < labels; i++)
            if (perimeter[i] > perimeter[best]) best = i;

        var rim = new List<int>();
        foreach (var (v, l) in label)
            if (l == best) rim.Add(v);
        return rim;
    }

    /// <summary>
    /// The least-squares plane through <paramref name="points"/>, with its normal pointing toward
    /// <paramref name="toward"/> — the toes, given the cap's centroid, since the cap reaches forward of its
    /// own rim. False when the points cannot define a plane.
    /// </summary>
    public static bool FitPlane(IReadOnlyList<Vec3> points, Vec3 toward, out Vec3 origin, out Vec3 normal)
    {
        origin = default;
        normal = default;
        if (points.Count < 3) return false;

        double cx = 0, cy = 0, cz = 0;
        foreach (var p in points) { cx += p.X; cy += p.Y; cz += p.Z; }
        cx /= points.Count; cy /= points.Count; cz /= points.Count;

        var m = new double[3, 3];
        foreach (var p in points)
        {
            double x = p.X - cx, y = p.Y - cy, z = p.Z - cz;
            m[0, 0] += x * x; m[0, 1] += x * y; m[0, 2] += x * z;
            m[1, 1] += y * y; m[1, 2] += y * z;
            m[2, 2] += z * z;
        }
        m[1, 0] = m[0, 1]; m[2, 0] = m[0, 2]; m[2, 1] = m[1, 2];

        // The normal of the best-fit plane is the direction the points spread LEAST along.
        var (nx, ny, nz, smallest, middle, largest) = Eigen(m);
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12 || largest < 1e-18) return false;
        nx /= len; ny /= len; nz /= len;

        // A plane needs spread along TWO directions. Collinear points have it along one only — total spread
        // is no guide, since they can spread a long way along that one — and then any plane containing the
        // line fits equally well, so the normal would be arbitrary.
        if (middle < largest * 1e-6) return false;
        _ = smallest;

        origin = new Vec3((float)cx, (float)cy, (float)cz);
        normal = new Vec3((float)nx, (float)ny, (float)nz);
        if (Dot(Sub(toward, origin), normal) < 0f)
            normal = new Vec3(-normal.X, -normal.Y, -normal.Z);
        return true;
    }

    /// <summary>
    /// The connected pieces of a triangle mesh: a label per vertex (-1 for a vertex no triangle uses) and how
    /// many pieces there are.
    /// <para/>
    /// A cap can arrive as ONE mesh covering both feet. Each foot then has its own rim, and a single plane
    /// fitted to "the" rim took whichever loop was longer: measured in game, one foot got its reinforced toe
    /// and the other only the slivers of it that happened to lie past the first foot's plane. So every piece
    /// is given its own line.
    /// </summary>
    public static (int[] Label, int Count) Components(IReadOnlyList<int> tri, int vertexCount)
    {
        var parent = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        var used = new bool[vertexCount];
        for (int t = 0; t + 2 < tri.Count; t += 3)
        {
            int a = tri[t], b = tri[t + 1], c = tri[t + 2];
            if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
            used[a] = used[b] = used[c] = true;
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
            int rb2 = Find(b), rc = Find(c);
            if (rb2 != rc) parent[rb2] = rc;
        }

        var label = new int[vertexCount];
        var byRoot = new Dictionary<int, int>();
        for (int i = 0; i < vertexCount; i++)
        {
            if (!used[i]) { label[i] = -1; continue; }
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out int l)) byRoot[r] = l = byRoot.Count;
            label[i] = l;
        }
        return (label, byRoot.Count);
    }

    /// <summary>Signed distance from <paramref name="p"/> to a plane; positive on the toe side.</summary>
    public static float Distance(Vec3 p, Vec3 origin, Vec3 normal) => Dot(Sub(p, origin), normal);

    /// <summary>
    /// How strongly a point at signed distance <paramref name="d"/> is reinforced: fully on the toe side,
    /// fading smoothly to nothing over <paramref name="band"/> behind the plane. The line therefore sits
    /// exactly at the rim, and the cap itself is always reinforced in full.
    /// </summary>
    public static float Weight(float d, float band)
    {
        if (d >= 0f) return 1f;
        if (band <= 0f) return 0f;
        float t = Math.Clamp((d + band) / band, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Draw one triangle's reinforcement into a square UV map, keeping the larger value where it overlaps
    /// what is already there.
    /// <para/>
    /// The signed distance is interpolated and the weight taken per texel, not the weight interpolated: the
    /// distance is exact across a flat triangle, so the fade's contour — the visible line — is exact too.
    /// <para/>
    /// Written with WRAP, like the cap footprint the geometry cut reads: a body's UVs need not sit in the
    /// unit tile (a heel's foot model is a whole tile down), and the game samples the sheet with wrap.
    /// </summary>
    public static void Rasterize(byte[] map, int size,
                                 (float U, float V) ua, (float U, float V) ub, (float U, float V) uc,
                                 float da, float db, float dc, float band)
    {
        if (size <= 0 || map.Length < size * size) return;

        // Nothing to draw when all three corners are past the fade.
        if (da < -band && db < -band && dc < -band) return;

        float ax = ua.U * size, ay = ua.V * size;
        float bx = ub.U * size, by = ub.V * size;
        float cx = uc.U * size, cy = uc.V * size;

        int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)));
        int x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
        int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)));
        int y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
        // A triangle spanning a large part of the atlas is one straddling a UV seam, not a real face.
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1L << 20) return;

        int Wrap(int v) => (v % size + size) % size;
        void Put(int x, int y, float d)
        {
            byte w = (byte)(Weight(d, band) * 255f + 0.5f);
            ref byte cell = ref map[Wrap(y) * size + Wrap(x)];
            if (w > cell) cell = w;
        }

        float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        float wide = x1 - x0, tall = y1 - y0;

        // A face smaller than a texel may contain no texel centre at all, and point sampling would leave
        // holes. Cover its whole footprint with the distance at its centroid instead.
        if (MathF.Abs(den) < 1e-12f || (wide <= 1.5f && tall <= 1.5f))
        {
            float mid = (da + db + dc) / 3f;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Put(x, y, mid);
            return;
        }

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                float w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                float w2 = 1f - w0 - w1;
                // A slightly generous test, so neighbouring faces meet without a hairline gap between them.
                if (w0 < -0.02f || w1 < -0.02f || w2 < -0.02f) continue;
                Put(x, y, w0 * da + w1 * db + w2 * dc);
            }
    }

    /// <summary>
    /// Jacobi rotations on a symmetric 3×3 matrix: the eigenvector of its smallest eigenvalue, and all three
    /// eigenvalues in ascending order.
    /// </summary>
    private static (double X, double Y, double Z, double Smallest, double Middle, double Largest) Eigen(double[,] a)
    {
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            if (off < 1e-30) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    double t = theta == 0 ? 1.0
                             : Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    double c = 1.0 / Math.Sqrt(t * t + 1.0), s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }

        int min = 0;
        for (int i = 1; i < 3; i++)
            if (a[i, i] < a[min, min]) min = i;
        var ev = new[] { a[0, 0], a[1, 1], a[2, 2] };
        Array.Sort(ev);
        return (v[0, min], v[1, min], v[2, min], ev[0], ev[1], ev[2]);
    }

    private static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Length(Vec3 a) => Math.Sqrt((double)a.X * a.X + (double)a.Y * a.Y + (double)a.Z * a.Z);
}
