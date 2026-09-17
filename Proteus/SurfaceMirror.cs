using System;
using System.Collections.Generic;

namespace Proteus;

/// <summary>
/// Reads left/right structure off a mesh: which side each vertex is on, and whether the UV layout is MIRRORED
/// (both sides sampling the same texels, so asymmetric art is impossible). Asks the geometry rather than a
/// body-type table, since only the model in hand knows how it is unwrapped.
/// </summary>
public static class SurfaceMirror
{
    /// <summary>
    /// How close to x = 0 counts as "on the midline", where a vertex's own position can't say which side it
    /// belongs to. Model units; a body is roughly 2 units tall.
    /// </summary>
    public const float Midline = 1e-4f;

    /// <summary>
    /// Which side of the body each vertex belongs to: <c>+1</c> for +X, <c>-1</c> for -X, <c>0</c> for one that
    /// can't be placed. Decided per TRIANGLE and inherited by its vertices, since midline vertices' own X is noise
    /// and one misplaced vertex stretches its triangle across the sheet.
    /// </summary>
    /// <param name="conflicts">Vertices claimed by triangles on BOTH sides; they get 0.</param>
    /// <param name="straddling">Triangles with vertices on both sides, which a mirrored layout should not have.</param>
    /// <param name="x">Model-space X of each vertex, one entry per vertex.</param>
    /// <param name="triangles">Flat triangle list indexing <paramref name="x"/>.</param>
    public static sbyte[] AssignSides(float[] x, IReadOnlyList<ushort> triangles,
        out int conflicts, out int straddling)
    {
        int conflicted = 0, straddled = 0;
        int n = x.Length;
        var sides = new sbyte[n];
        var disputed = new bool[n];
        for (int t = 0; t + 2 < triangles.Count; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if (a >= n || b >= n || c >= n) continue;
            float xa = x[a], xb = x[b], xc = x[c];
            bool anyPos = xa > Midline || xb > Midline || xc > Midline;
            bool anyNeg = xa < -Midline || xb < -Midline || xc < -Midline;
            if (anyPos && anyNeg) { straddled++; continue; }
            // A triangle wholly on the midline (every vertex within the band) can't place itself either;
            // leaving it unassigned lets a neighbouring triangle claim its vertices instead.
            if (!anyPos && !anyNeg) continue;
            sbyte side = anyPos ? (sbyte)1 : (sbyte)-1;
            for (int k = 0; k < 3; k++)
            {
                int v = triangles[t + k];
                if (disputed[v]) continue;                  // already known to have no single answer
                if (sides[v] == 0) { sides[v] = side; continue; }
                if (sides[v] == side) continue;
                sides[v] = 0;
                disputed[v] = true;
                conflicted++;
            }
        }
        conflicts = conflicted;
        straddling = straddled;
        return sides;
    }

    /// <summary>
    /// Whether this mesh's UV layout is mirrored: a vertex and its reflection across x = 0 share a UV. Sampled.
    /// Returns false when too few mirror partners were found to judge, leaving the caller's behaviour unchanged.
    /// </summary>
    public static bool LooksMirrored(float[] positions, float[] uvs, int samples = 3000)
    {
        int n = positions.Length / 3;
        if (n < 64 || uvs.Length < n * 2) return false;

        // The tile shift the writer applies per mesh, so this compares the same coordinates it will.
        float minU = float.MaxValue, minV = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (uvs[i * 2] < minU) minU = uvs[i * 2];
            if (uvs[i * 2 + 1] < minV) minV = uvs[i * 2 + 1];
        }
        float uOff = MathF.Floor(minU), vOff = MathF.Floor(minV);

        const float Cell = 0.01f;
        (int, int, int) Key(float x, float y, float z)
            => ((int)MathF.Floor(x / Cell), (int)MathF.Floor(y / Cell), (int)MathF.Floor(z / Cell));

        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var k = Key(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
            l.Add(i);
        }

        int paired = 0, same = 0, reflected = 0;
        int step = Math.Max(1, n / Math.Max(1, samples));
        for (int i = 0; i < n; i += step)
        {
            float x = positions[i * 3], y = positions[i * 3 + 1], z = positions[i * 3 + 2];
            if (MathF.Abs(x) < 0.01f) continue;   // too near the midline to have a distinct partner
            var kk = Key(-x, y, z);
            int best = -1;
            float bestD = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((kk.Item1 + dx, kk.Item2 + dy, kk.Item3 + dz), out var l)) continue;
                        foreach (var j in l)
                        {
                            float ex = positions[j * 3] + x, ey = positions[j * 3 + 1] - y, ez = positions[j * 3 + 2] - z;
                            float d = ex * ex + ey * ey + ez * ez;
                            if (d < bestD) { bestD = d; best = j; }
                        }
                    }
            if (best < 0 || bestD > 1e-6f) continue;
            paired++;
            float ui = uvs[i * 2] - uOff, uj = uvs[best * 2] - uOff;
            float dv = MathF.Abs((uvs[best * 2 + 1] - vOff) - (uvs[i * 2 + 1] - vOff));
            if (MathF.Abs(uj - ui) + dv < 0.01f) same++;
            else if (MathF.Abs(uj - (1f - ui)) + dv < 0.01f) reflected++;
        }

        // Vanilla scores mostly "same" and almost no "reflected"; an unmirrored layout the reverse.
        return paired >= 64 && same > paired / 2 && same > reflected;
    }
}
