using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;

internal static partial class MeshMath
{
    /// <summary>A triangle's un-normalised normal: the cross product of two of its edges, whose direction
    /// says which way it faces and whose length is twice its area.</summary>
    internal static Vec3 TriNormal(Vec3 a, Vec3 b, Vec3 c)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        return new Vec3(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    /// <summary>Hermite smoothstep, clamped.</summary>
    internal static float Smoothstep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>One [1,2,1] pass over a band series, ends held.</summary>
    internal static void Smooth1D(float[] a, int count)
    {
        if (count < 3) return;
        var src = new float[count];
        Array.Copy(a, src, count);
        for (int i = 1; i < count - 1; i++)
            a[i] = (src[i - 1] + 2f * src[i] + src[i + 1]) * 0.25f;
    }

    /// <summary>
    /// The region's principal direction ACROSS <paramref name="ax"/> — on a chest, the line from one
    /// breast to the other. Derived from the geometry in hand rather than taken as model-space X, because
    /// nothing else in this pass assumes a model-space convention and a caller on another surface would
    /// get a plainly wrong answer from a constant.
    /// </summary>
    internal static Vec3? PrincipalAcross(Vec3[] pos, float[] weight, int count, Vec3 ax)
    {
        var mid = default(Vec3);
        float n = 0;
        for (int i = 0; i < count; i++)
        {
            if (weight[i] <= 0f) continue;
            mid = new Vec3(mid.X + pos[i].X, mid.Y + pos[i].Y, mid.Z + pos[i].Z);
            n++;
        }
        if (n < 2f) return null;
        mid = new Vec3(mid.X / n, mid.Y / n, mid.Z / n);

        Basis(ax, out var bu, out var bv);
        float suu = 0f, svv = 0f, suv = 0f;
        for (int i = 0; i < count; i++)
        {
            if (weight[i] <= 0f) continue;
            var d = new Vec3(pos[i].X - mid.X, pos[i].Y - mid.Y, pos[i].Z - mid.Z);
            float a = d.X * bu.X + d.Y * bu.Y + d.Z * bu.Z;
            float b = d.X * bv.X + d.Y * bv.Y + d.Z * bv.Z;
            suu += a * a; svv += b * b; suv += a * b;
        }
        // Leading eigenvector of the 2x2 covariance, in closed form.
        float theta = 0.5f * MathF.Atan2(2f * suv, suu - svv);
        float cu = MathF.Cos(theta), cv = MathF.Sin(theta);
        return Normalize(new Vec3(bu.X * cu + bv.X * cv, bu.Y * cu + bv.Y * cv, bu.Z * cu + bv.Z * cv));
    }

    /// <summary>Weighted mean of a per-node vector, unnormalized.</summary>
    internal static Vec3 WeightedMean(Vec3[] v, float[] w, int count)
    {
        var acc = default(Vec3);
        for (int n = 0; n < count; n++)
        {
            if (w[n] <= 0f) continue;
            acc = new Vec3(acc.X + v[n].X * w[n], acc.Y + v[n].Y * w[n], acc.Z + v[n].Z * w[n]);
        }
        return acc;
    }

    /// <summary>Longest cycle in a rim adjacency map, walked in connectivity order.</summary>
    internal static List<int> LongestLoop(Dictionary<int, List<int>> rim)
    {
        var best = new List<int>();
        var seen = new HashSet<int>();
        foreach (int s in rim.Keys)
        {
            if (!seen.Add(s)) continue;
            var loop = new List<int> { s };
            int cur = s, prev = -1;
            while (true)
            {
                int next = -1;
                foreach (int k in rim[cur])
                    if (k != prev && !seen.Contains(k)) { next = k; break; }
                if (next < 0) break;
                seen.Add(next);
                loop.Add(next);
                prev = cur;
                cur = next;
            }
            if (loop.Count > best.Count) best = loop;
        }
        return best;
    }

    /// <summary>
    /// Rotate a rim loop to start near angle zero and run counter-clockwise, WITHOUT reordering it —
    /// its walk order is the only thing that keeps the stitch from crossing itself.
    /// </summary>
    internal static void OrientLoop(List<int> loop, Vec3[] pos, Func<Vec3, (float X, float Y)> flatten)
    {
        int n = loop.Count;
        var ang = new float[n];
        for (int i = 0; i < n; i++)
        {
            var f = flatten(pos[loop[i]]);
            ang[i] = MathF.Atan2(f.Y, f.X);
        }

        float turn = 0;
        for (int i = 0; i < n; i++)
        {
            float d = ang[(i + 1) % n] - ang[i];
            while (d > MathF.PI) d -= MathF.Tau;
            while (d < -MathF.PI) d += MathF.Tau;
            turn += d;
        }
        if (turn < 0) { loop.Reverse(); Array.Reverse(ang); }

        int startAt = 0;
        for (int i = 1; i < n; i++)
            if (MathF.Abs(ang[i]) < MathF.Abs(ang[startAt])) startAt = i;
        if (startAt == 0) return;

        var rotated = new List<int>(n);
        for (int i = 0; i < n; i++) rotated.Add(loop[(startAt + i) % n]);
        loop.Clear();
        loop.AddRange(rotated);
    }

    /// <summary>Closest point to <paramref name="p"/> on a triangle, including its edges and corners.</summary>
    internal static float Dist(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Three body vertices' skinning combined at a barycentric coordinate, keyed by bone NAME, reduced to
    /// the four slots a vertex has and renormalised. Names because the result is destined for a different
    /// mesh with a different bone table — an index would mean something else there.
    /// </summary>
    /// <param name="max">
    /// Influences to keep. Eight where the destination format holds eight (see BlendCount) — trimming to
    /// four and hoping is how a body's own skinning gets quietly coarsened on the way through.
    /// </param>
    internal static (string Bone, float W)[] BlendWeights(
        (string Bone, float W)[] a, float wa, (string Bone, float W)[] b, float wb,
        (string Bone, float W)[] c, float wc, int max = 8)
    {
        var acc = new Dictionary<string, float>(8);
        void Add((string Bone, float W)[] src, float k)
        {
            if (k <= 0f) return;                       // a clamped barycentric can go slightly negative
            foreach (var (bone, w) in src) acc[bone] = acc.GetValueOrDefault(bone) + w * k;
        }
        Add(a, wa); Add(b, wb); Add(c, wc);
        if (acc.Count == 0) return [];

        var top = acc.OrderByDescending(kv => kv.Value).Take(max).ToArray();
        float sum = top.Sum(kv => kv.Value);
        if (sum <= 0f) return [];
        return top.Select(kv => (kv.Key, kv.Value / sum)).ToArray();
    }

    /// <summary>Area of a triangle — what it actually covers, as opposed to how thin it is.</summary>
    internal static float TriArea(Vec3 a, Vec3 b, Vec3 c)
    {
        float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return 0.5f * MathF.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    internal static Vec3 ClosestOnSegment(Vec3 p, Vec3 a, Vec3 b, out float t)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        float len2 = dx * dx + dy * dy + dz * dz;
        if (len2 < 1e-20f) { t = 0f; return a; }
        t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / len2;
        t = MathF.Max(0f, MathF.Min(1f, t));
        return new Vec3(a.X + dx * t, a.Y + dy * t, a.Z + dz * t);
    }

    internal static Vec3 NormalizeOr(Vec3 v, Vec3 fallback)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-6f ? new Vec3(v.X / len, v.Y / len, v.Z / len) : fallback;
    }

    internal static Vec3 ClosestOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        static float Dot(Vec3 u, Vec3 v) => u.X * v.X + u.Y * v.Y + u.Z * v.Z;
        static Vec3 Sub(Vec3 u, Vec3 v) => new(u.X - v.X, u.Y - v.Y, u.Z - v.Z);
        static Vec3 Add(Vec3 u, Vec3 v, float s) => new(u.X + v.X * s, u.Y + v.Y * s, u.Z + v.Z * s);

        Vec3 ab = Sub(b, a), ac = Sub(c, a), ap = Sub(p, a);
        float d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        Vec3 bp = Sub(p, b);
        float d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        float vc2 = d1 * d4 - d3 * d2;
        if (vc2 <= 0 && d1 >= 0 && d3 <= 0) return Add(a, ab, d1 / (d1 - d3));

        Vec3 cp = Sub(p, c);
        float d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Add(a, ac, d2 / (d2 - d6));

        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return Add(b, Sub(c, b), (d4 - d3) / (d4 - d3 + (d5 - d6)));

        float den = 1f / (va + vb + vc2);
        return Add(Add(a, ab, vb * den), ac, vc2 * den);
    }

    /// <summary>Nearest not-yet-claimed node to a target point, or -1 when the region is exhausted.</summary>
    internal static int NearestFree(List<int> pool, Vec3[] pos, HashSet<int> taken, Vec3 p)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (int n in pool)
        {
            if (taken.Contains(n)) continue;
            float dx = pos[n].X - p.X, dy = pos[n].Y - p.Y, dz = pos[n].Z - p.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    /// <summary>Distance from an interior point to the hull boundary along a unit direction.</summary>
    internal static float HullRadius((float X, float Y)[] hull, (float X, float Y) c, float dx, float dy)
    {
        float best = 0;
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            float ex = b.X - a.X, ey = b.Y - a.Y;
            float den = dx * ey - dy * ex;
            if (MathF.Abs(den) < 1e-12f) continue;
            float t = ((a.X - c.X) * ey - (a.Y - c.Y) * ex) / den;
            float u = ((a.X - c.X) * dy - (a.Y - c.Y) * dx) / den;
            if (t > 0 && u >= -1e-6f && u <= 1 + 1e-6f) best = MathF.Max(best, t);
        }
        return best;
    }

    /// <summary>
    /// Group coincident vertices into shared nodes, returning each vertex's node index. A body mesh
    /// splits vertices at UV seams and hard edges; the cap's displacement and its normals must both be
    /// decided per NODE, or two copies of the same point drift apart and the surface cracks open along
    /// the seam. One function so that grouping is structurally identical in both passes.
    /// </summary>
    internal static int[] WeldByPosition(Vec3[] pos, out int nodeCount)
    {
        var nodeOf = new int[pos.Length];
        var byPos = new Dictionary<(int, int, int), int>(pos.Length);
        nodeCount = 0;
        for (int i = 0; i < pos.Length; i++)
        {
            var key = ((int)MathF.Round(pos[i].X * 1e5f), (int)MathF.Round(pos[i].Y * 1e5f), (int)MathF.Round(pos[i].Z * 1e5f));
            if (!byPos.TryGetValue(key, out int n)) byPos[key] = n = nodeCount++;
            nodeOf[i] = n;
        }
        return nodeOf;
    }

    /// <summary>Mean length of the edges touching the given nodes — the mesh's own resolution.</summary>
    internal static float MeanEdgeLength(Vec3[] pos, List<int>[] adj, List<int> nodes)
    {
        float total = 0;
        int count = 0;
        foreach (int n in nodes)
        {
            if (adj[n] == null) continue;
            foreach (int k in adj[n])
            {
                float dx = pos[k].X - pos[n].X, dy = pos[k].Y - pos[n].Y, dz = pos[k].Z - pos[n].Z;
                total += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                count++;
            }
        }
        return count > 0 ? total / count : 0f;
    }

    /// <summary>Axis of the given nodes' longest bounding-box side, or null when they occupy no space.</summary>
    internal static Vec3? LongestExtent(Vec3[] pos, List<int> nodes)
    {
        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue;
        foreach (int n in nodes)
        {
            lox = MathF.Min(lox, pos[n].X); hix = MathF.Max(hix, pos[n].X);
            loy = MathF.Min(loy, pos[n].Y); hiy = MathF.Max(hiy, pos[n].Y);
            loz = MathF.Min(loz, pos[n].Z); hiz = MathF.Max(hiz, pos[n].Z);
        }
        float ex = hix - lox, ey = hiy - loy, ez = hiz - loz;
        if (ex <= 1e-6f && ey <= 1e-6f && ez <= 1e-6f) return null;
        return ex >= ey && ex >= ez ? new Vec3(1, 0, 0)
             : ey >= ez             ? new Vec3(0, 1, 0)
                                    : new Vec3(0, 0, 1);
    }

    /// <summary>Unit vector, or null when the input is too short to have a direction.</summary>
    internal static Vec3? Normalize(Vec3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-6f ? new Vec3(v.X / len, v.Y / len, v.Z / len) : null;
    }

    internal static float Len(Vec3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    /// <summary>
    /// Least-squares fit of h = a + bu + cv + du² + euv + fv² to scattered (u,v,h), by Gaussian
    /// elimination with partial pivoting on the 6x6 normal equations. Null if it is underdetermined or
    /// singular — a caller must have a fallback, since a coverage-clipped region can supply neither
    /// enough points nor enough spread.
    /// <para/>
    /// Six terms rather than a radial h = h0 + kr²: a nipple does not sit on the apex of the breast, so
    /// the surface under it SLOPES, and a rotationally symmetric fit averages that slope away and returns
    /// a dome tilted out of the surface it is meant to continue. The linear pair carry the slope and the
    /// three quadratic terms carry anisotropic curvature.
    /// </summary>
    internal static double[]? FitQuadric(List<(double U, double V, double H)> pts)
    {
        if (pts.Count < 12) return null;
        var m = new double[6, 7];
        Span<double> t = stackalloc double[6];
        foreach (var (u, v, h) in pts)
        {
            // WEIGHTED toward the near edge of the ring, by 1/r². Unweighted, the far side of the ring
            // dominates simply by being further out, and the two axes are not equivalent out there: a ring
            // wide enough to be stable reaches the collarbone above and the underbust below long before it
            // runs out of breast sideways. So the vertical curvature collapses while the horizontal barely
            // moves, and the dome comes out as a cylinder — measured on a real body as 3.86 across against
            // 1.68 up, from a breast that was very nearly isotropic (5.45 / 5.45). That is a surface which
            // reads round from one angle and flat from another.
            double r2 = u * u + v * v;
            double wt = r2 > 1e-12 ? 1.0 / r2 : 1e12;
            t[0] = 1; t[1] = u; t[2] = v; t[3] = u * u; t[4] = u * v; t[5] = v * v;
            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 6; j++) m[i, j] += wt * t[i] * t[j];
                m[i, 6] += wt * t[i] * h;
            }
        }
        for (int c = 0; c < 6; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 6; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (Math.Abs(m[piv, c]) < 1e-18) return null;
            if (piv != c) for (int j = 0; j <= 6; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            for (int r = 0; r < 6; r++)
            {
                if (r == c) continue;
                double f = m[r, c] / m[c, c];
                for (int j = c; j <= 6; j++) m[r, j] -= f * m[c, j];
            }
        }
        var x = new double[6];
        for (int i = 0; i < 6; i++) x[i] = m[i, 6] / m[i, i];
        return x;
    }

    /// <summary>Any two unit vectors spanning the plane perpendicular to <paramref name="n"/>.</summary>
    internal static void Basis(Vec3 n, out Vec3 u, out Vec3 v)
    {
        var seed = MathF.Abs(n.X) < 0.9f ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        u = Normalize(new Vec3(
            seed.Y * n.Z - seed.Z * n.Y,
            seed.Z * n.X - seed.X * n.Z,
            seed.X * n.Y - seed.Y * n.X))!.Value;
        v = new Vec3(n.Y * u.Z - n.Z * u.Y, n.Z * u.X - n.X * u.Z, n.X * u.Y - n.Y * u.X);
    }

    /// <summary>Convex hull of a 2D point set, counter-clockwise (Andrew's monotone chain).</summary>
    internal static (float X, float Y)[] ConvexHull((float X, float Y)[] pts)
    {
        if (pts.Length < 3) return pts;
        var p = (( float X, float Y)[])pts.Clone();
        Array.Sort(p, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new (float X, float Y)[p.Length * 2];
        int k = 0;
        foreach (var q in p)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], q) <= 0) k--;
            hull[k++] = q;
        }
        int lower = k + 1;
        for (int i = p.Length - 2; i >= 0; i--)
        {
            var q = p[i];
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], q) <= 0) k--;
            hull[k++] = q;
        }
        return hull[..Math.Max(k - 1, 0)];
    }

    /// <summary>Nearest point to <paramref name="q"/> on the hull's boundary.</summary>
    private static (float X, float Y) ClosestOnHull((float X, float Y)[] hull, (float X, float Y) q)
    {
        var best = hull[0];
        float bestD = float.MaxValue;
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            float ex = b.X - a.X, ey = b.Y - a.Y;
            float len2 = ex * ex + ey * ey;
            float t = len2 > 1e-20f ? ((q.X - a.X) * ex + (q.Y - a.Y) * ey) / len2 : 0f;
            t = Math.Clamp(t, 0f, 1f);
            float px = a.X + ex * t, py = a.Y + ey * t;
            float d = (px - q.X) * (px - q.X) + (py - q.Y) * (py - q.Y);
            if (d < bestD) { bestD = d; best = (px, py); }
        }
        return best;
    }
}
