using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// Shared by every import that turns a glow mask into a second skin: splitting the mask into painted regions,
/// the index texture that addresses them, and the surface tuning. Everything is stated in INTENSITY (one byte
/// per pixel, 0 dark, 255 fully lit); each caller converts its own format (AL stores it inverted in alpha).
/// </summary>
internal static class GlowShell
{
    // ── the surface ──────────────────────────────────────────────────────────

    /// <summary>
    /// The surface UNDER the glow: black. characterscroll has no base texture, so this row colour is the whole
    /// surface and is scene-lit; any grey reads as charcoal.
    /// </summary>
    public const string SurfaceColour = "#000000";

    /// <summary>The colour-table row a shell with no <c>_id</c> art samples: SecondSkinService fabricates an
    /// index of (255, 255, 0), which is row pair 16, sub-row A.</summary>
    public const int Row = 16;

    /// <summary>
    /// The row emissive: 300%, tuned in game. On characterscroll it scales the scroll map's brightness, and a
    /// glow sheet is mostly black with thin lines, so it needs far more than saturated maps. Only correct with
    /// a true-black <see cref="SurfaceColour"/>.
    /// </summary>
    public const float Emissive = 3.0f;

    // ── the regions ──────────────────────────────────────────────────────────

    /// <summary>
    /// How many separately-addressable regions (colour-table rows) one imported sheet may be split into, so
    /// each painted plateau gets its own glow, colour and light response. Rows above these stay free for the user.
    /// </summary>
    public const int MaxRegions = 8;

    /// <summary>How much of the glowing area a plateau must hold to earn a row of its own. Below this it is
    /// an antialiased edge or a compression artefact, not a region anyone painted.</summary>
    public const float MinRegionFraction = 0.02f;

    /// <summary>How far apart two plateaus must be (in the 0–255 intensity range) to count as different
    /// regions. Anything closer is the same fill read through lossy compression.</summary>
    public const int Separation = 16;

    /// <summary>
    /// At or below this a pixel does not glow and takes no part in the split; above zero to tolerate lossy
    /// compression.
    /// </summary>
    public const int Dark = 5;

    /// <summary>
    /// The distinct plateaus (histogram spikes) in a glow mask, brightest first. Repeatedly takes the most
    /// populated value and claims everything within <see cref="Separation"/>; values under
    /// <paramref name="minFraction"/> of the glowing area get no row. Empty when nothing glows.
    /// </summary>
    public static List<int> Bands(
        byte[] intensity, int maxBands = MaxRegions, float minFraction = MinRegionFraction)
    {
        var counts = new int[256];
        long total = 0;
        foreach (var v in intensity)
        {
            if (v <= Dark) continue;   // no glow here at all
            counts[v]++;
            total++;
        }
        if (total == 0) return [];

        var bands = new List<int>();
        for (int n = 0; n < maxBands; n++)
        {
            int best = -1, bestCount = 0;
            for (int v = 0; v < counts.Length; v++)
                if (counts[v] > bestCount) { bestCount = counts[v]; best = v; }

            if (best < 0 || bestCount / (float)total < minFraction) break;
            bands.Add(best);
            for (int v = Math.Max(0, best - Separation); v <= Math.Min(255, best + Separation); v++)
                counts[v] = 0;
        }

        bands.Sort((a, b) => b.CompareTo(a));   // brightest first
        return bands;
    }

    /// <summary>
    /// An RGBA index texture sending each glowing pixel to its plateau's row. Red is
    /// <c>(row − 1) × 17</c> as <see cref="ContentIndexTexture.RowOf"/> decodes, green 255 is sub-row A.
    /// Non-glowing pixels get the first row (their coverage is zero anyway).
    /// </summary>
    public static byte[] Index(byte[] intensity, IReadOnlyList<int> bands)
    {
        var id = new byte[intensity.Length * 4];
        for (int p = 0; p < intensity.Length; p++)
        {
            int lit = intensity[p];
            int band = 0;
            if (lit > Dark)
            {
                int bestGap = int.MaxValue;
                for (int b = 0; b < bands.Count; b++)
                {
                    int gap = Math.Abs(lit - bands[b]);
                    if (gap < bestGap) { bestGap = gap; band = b; }
                }
            }
            int i = p * 4;
            id[i]     = (byte)(band * 17);   // band 0 → row 1, band 1 → row 2, …
            id[i + 1] = 255;                 // sub-row A
            id[i + 2] = 0;
            id[i + 3] = 255;
        }
        return id;
    }
}
