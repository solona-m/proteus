using System;
using System.Collections.Generic;

namespace Proteus;

/// <summary>
/// Reads left/right structure off a mesh: which side of the character each vertex is on, and whether the
/// mesh's UV layout is MIRRORED — both sides sampling the same texels.
/// <para/>
/// A mirrored layout is what makes asymmetric art impossible on a surface: one sheet describes both sides,
/// so a mark painted on one side appears on both and there is nowhere to put a mark that belongs to only
/// one. Vanilla is like this and Bibo+/gen3 are not, which is the whole reason art ported to vanilla loses
/// a side. Measured on the vanilla c0201 e0000 parts, 89-97% of mirror-partner vertex pairs share a UV
/// (<c>u' = u</c>) and essentially none are reflections of one another; on a Bibo+ body the numbers are the
/// other way round. The vanilla FACE reads the same way as the body (89%), so the fold is not a body-only
/// property — which is why this asks the geometry rather than consulting a table of body types.
/// <para/>
/// Deliberately not a body-type lookup: a mod is free to ship a body that calls itself vanilla and unwraps
/// it differently, and the model in hand is the only thing that actually knows.
/// </summary>
public static class SurfaceMirror
{
    /// <summary>
    /// How close to x = 0 counts as "on the midline", where a vertex's own position can't say which side it
    /// belongs to. Model units; a body is roughly 2 units tall.
    /// </summary>
    public const float Midline = 1e-4f;

    /// <summary>
    /// Which side of the body each vertex belongs to: <c>+1</c> for +X, <c>-1</c> for -X, <c>0</c> for one
    /// that can't be placed.
    /// <para/>
    /// Decided per TRIANGLE and inherited by its vertices, not read off each vertex's own X. The midline is
    /// where a mirrored layout puts its UV seam, so it carries a band of vertices sitting at x ~ 0 whose own
    /// coordinate is pure noise — 54 of them on the vanilla e0000 top, 359 on the face. Their side comes
    /// from the triangles that use them, and getting one wrong is not a subtle error: under un-mirroring the
    /// two halves of the sheet are a long way apart, so a single misplaced vertex stretches its triangle
    /// across the texture.
    /// <para/>
    /// <paramref name="conflicts"/> counts vertices claimed by triangles on BOTH sides — those get 0 and
    /// keep their authored UV, because there is no one answer for them. <paramref name="straddling"/> counts
    /// triangles with vertices on both sides, which a mirrored layout should not have (its UV seam forces
    /// the split): measured zero on every vanilla e0000 part and on the face, and 3 conflicted vertices on
    /// the face out of 5990. Both are reported rather than assumed away.
    /// </summary>
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
    /// Whether this mesh's UV layout is mirrored — a vertex and the vertex at its reflection across x = 0
    /// share a UV rather than being reflections in UV too.
    /// <para/>
    /// Sampled, not exhaustive: enough pairs to be decisive and cheap enough to run per body part per
    /// composite. Returns false when too few mirror partners were found to judge (an asymmetric mesh, or one
    /// that isn't laterally symmetric at all), which is the safe answer — it leaves the caller on the
    /// behaviour it already had.
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

        // Decisive in practice: vanilla scores 89-97% "same" against ~0% "reflected", Bibo+ the reverse.
        return paired >= 64 && same > paired / 2 && same > reflected;
    }
}
