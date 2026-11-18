// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Numerics;

namespace XivLiveMesh;

/// <summary>Where a ray met a posed mesh.</summary>
/// <param name="Triangle">Index of the triangle hit (its corners are entries 3·Triangle.. of the triangle list).</param>
/// <param name="U">Barycentric weight of the triangle's second corner.</param>
/// <param name="V">Barycentric weight of its third corner; the first corner's is 1 − U − V.</param>
/// <param name="Distance">Along the ray, in world units.</param>
/// <param name="World">The point hit, in the world.</param>
/// <param name="Normal">The triangle's unit normal in the world, turned to face the ray's origin.</param>
public readonly record struct LiveMeshHit(int Triangle, float U, float V, float Distance, Vector3 World, Vector3 Normal)
{
    /// <summary>Interpolate a per-vertex value across the hit triangle.</summary>
    public Vector3 Interpolate(Vector3 a, Vector3 b, Vector3 c) => a * (1f - U - V) + b * U + c * V;

    /// <summary>Interpolate texture coordinates across the hit triangle.</summary>
    public Vector2 Interpolate(Vector2 a, Vector2 b, Vector2 c) => a * (1f - U - V) + b * U + c * V;
}

/// <summary>
/// Casts a ray against a posed mesh: the nearest triangle, from either side. Cloth is thin and seen from inside
/// as often as outside, so back faces count.
/// </summary>
public static class LiveMeshPicker
{
    /// <summary>The nearest hit, or null. <paramref name="accept"/> can refuse triangles — refused ones do not
    /// block the ray either.</summary>
    public static LiveMeshHit? Raycast(ReadOnlySpan<Vector3> world, ReadOnlySpan<int> triangles, Vector3 origin,
                                       Vector3 direction, Func<int, bool>? accept = null)
    {
        const float Epsilon = 1e-9f;
        float best = float.MaxValue;
        int bestTri = -1;
        float bestU = 0f, bestV = 0f;

        int count = triangles.Length / 3;
        for (int t = 0; t < count; t++)
        {
            int o = t * 3;
            int ia = triangles[o], ib = triangles[o + 1], ic = triangles[o + 2];
            if ((uint)ia >= (uint)world.Length || (uint)ib >= (uint)world.Length || (uint)ic >= (uint)world.Length)
                continue;

            // Möller–Trumbore.
            var a = world[ia];
            var e1 = world[ib] - a;
            var e2 = world[ic] - a;
            var p = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < Epsilon) continue;
            float inv = 1f / det;
            var s = origin - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) continue;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(direction, q) * inv;
            if (v < 0f || u + v > 1f) continue;
            float dist = Vector3.Dot(e2, q) * inv;
            if (dist <= 0f || dist >= best) continue;
            if (accept != null && !accept(t)) continue;

            best = dist;
            bestTri = t;
            bestU = u;
            bestV = v;
        }
        if (bestTri < 0) return null;

        int bo = bestTri * 3;
        var wa = world[triangles[bo]];
        var n = Vector3.Cross(world[triangles[bo + 1]] - wa, world[triangles[bo + 2]] - wa);
        float len = n.Length();
        n = len > 1e-12f ? n / len : -direction;
        if (Vector3.Dot(n, direction) > 0f) n = -n;
        return new LiveMeshHit(bestTri, bestU, bestV, best, origin + direction * best, n);
    }
}
