using System;
using System.Collections.Generic;
using System.Numerics;
using Region = Proteus.Services.UvBakeBones.Region;

namespace Proteus.Services;

/// <summary>
/// Each body's nipples, found on its own mesh: the tip of the modelled bump, the vertex standing furthest out of the
/// skin around it along the smoothed normal. Read in the bake's frame (+Y up, +Z the front, +X the character's left;
/// the man already bent to the woman's proportions). The bake aims its chest warp at them and the gate measures them,
/// so both use this one finder and can never disagree on where a nipple is.
/// </summary>
internal static class UvBakeNipples
{
    /// <summary>One nipple: a corner of a triangle (barycentrics one-hot), where it is, its uv, and how far it stands out.</summary>
    internal readonly record struct Nipple(int Tri, Vector3 Bary, Vector3 Pos, Vector2 Uv, float Height);

    /// <summary>The skin a vertex is measured against: everything within this reach of it (1.5 cm, about an areola).</summary>
    internal const float Reach = 0.015f;

    /// <summary>How far apart, mirrored across the body's middle, the two nipples may be (5 mm).</summary>
    internal const float Symmetry = 0.005f;

    /// <summary>How far a bump must stand out of its skin to be a nipple at all (0.5 mm).</summary>
    internal const float MinHeight = 0.0005f;

    /// <summary>
    /// How squarely the skin must face forward (its smoothed normal's z) to hold a nipple: on a large breast the rim
    /// of its overhang, facing down, stands out of the skin around it as much as a nipple does.
    /// </summary>
    internal const float MinFacing = 0.7f;

    /// <summary>How far behind the foremost skin of its side a nipple may sit (2 cm): a large breast has bumps by the cleavage too.</summary>
    internal const float FrontDepth = 0.02f;

    /// <summary>The chest's search window, sideways: off the sternum, inside the arm.</summary>
    private const float MinX = 0.03f, MaxX = 0.16f;

    /// <summary>Both nipples of <paramref name="body"/>, or why they could not be found.</summary>
    internal static ((Nipple L, Nipple R)? Found, string? Failure) Find(BakeBody body)
    {
        var centroid = new Dictionary<string, (Vector3 Sum, int N)>(StringComparer.Ordinal);
        for (int v = 0; v < body.P.Length; v++)
        {
            string? top = null;
            float best = 0f;
            foreach (var (b, w) in body.Weights[v]) if (w > best) { best = w; top = b; }
            if (top == null) continue;
            var (sum, n) = centroid.GetValueOrDefault(top);
            centroid[top] = (sum + body.P[v], n + 1);
        }
        Vector3? C(string bone) => centroid.TryGetValue(bone, out var c) && c.N > 0 ? c.Sum / c.N : null;
        if (C("j_sebo_b") is not { } mid || C("j_sebo_c") is not { } upper)
            return (null, "no chest: the body carries no j_sebo_b or j_sebo_c skin");
        // Down to well under the middle spine (a woman's breast hangs below it), up to the upper spine.
        float lo = mid.Y - 0.08f, hi = upper.Y;

        // Welded: a uv seam splits a vertex in two, and both halves are one point of the skin.
        var id = RimIndex.WeldIds(body.P);
        var near = new List<int>();
        for (int v = 0; v < body.P.Length; v++)
        {
            if (id[v] != v) continue;
            var p = body.P[v];
            float ax = MathF.Abs(p.X);
            if (p.Y >= lo - Reach && p.Y <= hi + Reach && ax >= MinX - Reach && ax <= MaxX + Reach) near.Add(v);
        }

        bool InWindow(Vector3 p, float sx) => p.X * sx >= MinX && p.X * sx <= MaxX && p.Y >= lo && p.Y <= hi;

        Nipple? Side(float sx)
        {
            // The front of the chest on this side: a nipple is on it, not back by the cleavage or under the breast.
            float front = float.MinValue;
            foreach (int v in near)
                if (InWindow(body.P[v], sx) && body.VertexRegion[v] == Region.Torso) front = MathF.Max(front, body.P[v].Z);
            int bestV = -1;
            float bestH = float.MinValue;
            foreach (int v in near)
            {
                var p = body.P[v];
                if (!InWindow(p, sx) || p.Z < front - FrontDepth) continue;
                if (body.VertexRegion[v] != Region.Torso) continue;
                var n = body.Smooth[v];
                if (n.Z <= MinFacing) continue;
                var sum = Vector3.Zero;
                int count = 0;
                foreach (int u in near)
                    if (u != v && Vector3.DistanceSquared(body.P[u], p) <= Reach * Reach) { sum += body.P[u]; count++; }
                if (count < 6) continue;
                float h = Vector3.Dot(p - sum / count, n);
                if (h > bestH || (h == bestH && v < bestV)) { bestH = h; bestV = v; }
            }
            if (bestV < 0 || bestH < MinHeight) return null;
            for (int t = 0; t < body.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    if (id[body.Tri[t * 3 + k]] != bestV) continue;
                    var bary = k == 0 ? Vector3.UnitX : k == 1 ? Vector3.UnitY : Vector3.UnitZ;
                    var (pos, _, uv) = body.At(t, bary.X, bary.Y, bary.Z);
                    return new Nipple(t, bary, pos, uv, bestH);
                }
            return null;
        }

        var l = Side(1f);
        var r = Side(-1f);
        if (l == null || r == null)
            return (null, $"no nipple on the {(l == null ? "left" : "right")}: no bump standing {MinHeight * 1000:F1} mm out of the chest");
        var mirror = new Vector3(-r.Value.Pos.X, r.Value.Pos.Y, r.Value.Pos.Z);
        float apart = Vector3.Distance(l.Value.Pos, mirror);
        if (apart > Symmetry)
            return (null, $"nipples asymmetric: {apart * 1000:F1} mm apart mirrored > {Symmetry * 1000:F0} mm");
        return ((l.Value, r.Value), null);
    }

    /// <summary>One side's under-bust crease: the front of the body where the breast's underside turns into the chest wall.</summary>
    internal readonly record struct Fold(int Tri, Vector3 Bary, Vector3 Pos, float SlopeChange);

    /// <summary>How sharply the front must turn toward vertical for a crease to be one (dz/dy, smoothed over three bins).</summary>
    internal const float MinSlopeChange = 0.3f;

    private const float Bin = 0.005f;

    /// <summary>
    /// The under-bust crease below <paramref name="nipple"/>: the body's front profile at the nipple's x, every 5 mm from
    /// 1 cm to 12 cm below it (the foremost Torso skin a ray from the front meets there); the crease is where the
    /// profile's slope dz/dy falls most, the underside of the breast giving way to the chest wall, and the point is the
    /// front just below it. Null, with why, when nothing turns by <see cref="MinSlopeChange"/>.
    /// </summary>
    internal static (Fold? Found, string? Failure) Crease(BakeBody body, Nipple nipple, TriangleBvh? bvh = null)
    {
        const int Bins = 23;   // 1 cm to 12 cm below
        bvh ??= new TriangleBvh(body);
        var front = new float[Bins];
        var at = new (int Tri, Vector3 Bary)[Bins];
        for (int b = 0; b < Bins; b++)
        {
            // From well in front, so the nearest hit either way is the foremost skin.
            var origin = new Vector3(nipple.Pos.X, nipple.Pos.Y - 0.01f - b * Bin, 0.5f);
            if (!bvh.Ray(origin, -Vector3.UnitZ, 0.5f, t => body.TriRegion[t] == Region.Torso,
                         out int tri, out float u, out float v, out float w, out _))
                return (null, $"no front skin {10 + b * 5} mm below the nipple at x {nipple.Pos.X * 1000:F0} mm");
            front[b] = body.At(tri, u, v, w).Pos.Z;
            at[b] = (tri, new Vector3(u, v, w));
        }

        // Slope between neighbouring bands going down: how fast the front falls back per metre of height.
        var slope = new float[Bins - 1];
        for (int b = 0; b + 1 < Bins; b++) slope[b] = (front[b] - front[b + 1]) / Bin;
        int bestB = -1;
        float bestChange = float.MinValue;
        for (int b = 1; b + 2 < slope.Length; b++)
        {
            float above = (slope[b - 1] + slope[b]) * 0.5f, under = (slope[b + 1] + slope[b + 2]) * 0.5f;
            float change = above - under;
            if (change > bestChange) { bestChange = change; bestB = b + 1; }
        }
        if (bestB < 0 || bestChange < MinSlopeChange)
            return (null, $"no under-bust crease: the front turns by {Math.Max(bestChange, 0):F2} < {MinSlopeChange:F1}");
        var (crease, bary) = at[bestB];
        return (new Fold(crease, bary, body.At(crease, bary.X, bary.Y, bary.Z).Pos, bestChange), null);
    }
}
