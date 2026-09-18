using System;
using System.Collections.Generic;

namespace Proteus;

/// <summary>Reads an <c>_id</c> (index) texture for which colour-table cells the material actually samples.</summary>
public static class ContentIndexTexture
{
    /// <summary>
    /// Rows (1-based) the texture selects, and the sub-row column when every sampled texel agrees on one. A null
    /// <paramref name="SubRow"/> means both columns are used; an empty <paramref name="Rows"/> means read but selects nothing.
    /// </summary>
    public readonly record struct Scan(HashSet<int> Rows, string? SubRow);

    /// <summary>
    /// The row pair a pack's red value names, 1–16. ROUNDED, not truncated: pair <c>n</c> sits at <c>n * 17</c> and
    /// lossy art lands between.
    /// </summary>
    public static int RowOf(byte red) => Math.Clamp((red + 8) / 17 + 1, 1, 16);

    /// <summary>
    /// The row pair a red value names on an overlay's OWN <c>_id</c>, 1–16. TRUNCATED, unlike <see cref="RowOf"/>: it
    /// must match <c>OverlayBlend.ApplyIndexedOverlay</c>'s <c>idx[i] / 17</c> binning.
    /// </summary>
    public static int OverlayRowOf(byte red) => red / 17 + 1;

    public static Scan Read(byte[] rgba)
    {
        var rows = new HashSet<int>();
        bool anyA = false, anyB = false;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            rows.Add(RowOf(rgba[i]));
            if (rgba[i + 1] > 127) anyA = true; else anyB = true;
        }
        return new Scan(rows, anyA == anyB ? null : anyA ? "A" : "B");
    }

    /// <summary>
    /// <see cref="Read"/> for an overlay's own <c>_id</c>: <see cref="OverlayRowOf"/> binning, a row needs a real share
    /// of the art to count, and the UV island mask replaces the transparency skip.
    /// </summary>
    /// <param name="island">UV island coverage at its own resolution, sampled nearest-neighbour. Null
    /// counts every texel.</param>
    public static Scan ReadOverlay(
        byte[] rgba, int width, int height, bool[]? island = null, int islandW = 0, int islandH = 0)
    {
        var rows = new HashSet<int>();
        if (width <= 0 || height <= 0 || rgba.Length < (long)width * height * 4) return new(rows, null);
        if (islandW <= 0 || islandH <= 0) island = null;

        bool anyA = false, anyB = false;
        var counts = new int[17];
        int total = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (island != null)
                {
                    int mx = x * islandW / width, my = y * islandH / height;
                    int mi = my * islandW + mx;
                    if (mi >= island.Length || !island[mi]) continue;   // outside the islands
                }

                int p = (y * width + x) * 4;
                counts[OverlayRowOf(rgba[p])]++;
                // Green blends sub-row A at 255 against B at 0.
                if (rgba[p + 1] > 127) anyA = true; else anyB = true;
                total++;
            }
        }

        if (total == 0) return new(rows, null);

        int threshold = Math.Max(64, total / 1000);   // 0.1% of the island area
        for (int row = 1; row <= 16; row++)
            if (counts[row] >= threshold)
                rows.Add(row);

        // A texture using BOTH columns narrows to neither: that is a gradient, not a mistake.
        return new(rows, anyA == anyB ? null : anyA ? "A" : "B");
    }
}
