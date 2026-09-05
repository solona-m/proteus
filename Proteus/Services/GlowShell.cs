using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// What every import that turns a glow mask into a second skin has in common: how the mask splits into the
/// regions its author painted, the index texture that addresses them, and what the resulting surface is
/// tuned to.
/// <para/>
/// Two formats arrive here — an Atramentum Luminis <c>.ttmp2</c> and an emissive-skin <c>.pmp</c> — and they
/// disagree about exactly one thing: where the intensity lives. AL inverts it into a diffuse's ALPHA (0
/// glows, 255 is ordinary skin); an emissive map carries it the right way up. So everything below is stated
/// in terms of INTENSITY — one byte per pixel, 0 dark and 255 fully lit — and each caller hands over its own
/// reading of its own format. That is also why the inversion is no longer buried inside a function taking
/// RGBA: a convention only one of two callers holds is not one to hide.
/// <para/>
/// The tuning constants are here for a blunter reason. The two importers can be handed the SAME artwork —
/// the same tattoo shipped in both formats — and a surface colour or a glow dial that drifted between them
/// would make one import brighter than the other for no reason anyone could name.
/// </summary>
internal static class GlowShell
{
    // ── the surface ──────────────────────────────────────────────────────────

    /// <summary>
    /// The surface UNDER the glow: black.
    /// <para/>
    /// characterscroll declares no base texture at all (see GearMaterialWriter's sampler table), so this row
    /// colour IS the whole unlit surface, and it is lit by the scene like any other. A glow panel is artwork
    /// drawn on black and has to read as black; the near-black grey this started at came out as a visibly
    /// lifted charcoal wherever the light fell on it, against the true black of the areas facing away.
    /// </summary>
    public const string SurfaceColour = "#000000";

    /// <summary>The colour-table row a shell with no <c>_id</c> art samples: SecondSkinService fabricates an
    /// index of (255, 255, 0), which is row pair 16, sub-row A.</summary>
    public const int Row = 16;

    /// <summary>
    /// The row emissive: 300%. Measured in game against the artwork, not derived.
    /// <para/>
    /// On characterscroll this is the multiplier the scroll map's brightness scales with. Much higher than
    /// anything else Proteus writes, and the reason is what the map holds: a glow sheet is mostly BLACK with
    /// thin neon on it, so the average pixel contributes nothing and only the lines have anything to scale.
    /// The values tuned for a piece whose scroll map is saturated edge to edge —
    /// <see cref="ContentGlowRow.DefaultGlow"/> at 25%, <see cref="RenderModeInference.GlowEmissive"/> at
    /// 150% — both leave this art dim.
    /// <para/>
    /// It only reads correctly once <see cref="SurfaceColour"/> is true black. Against the near-black grey
    /// this started at, the same dial lifted the whole panel instead of the lines, which is what made 150%
    /// look blown out earlier: the surface was rising with the glow.
    /// <para/>
    /// Worth being precise about, because the same field means something else one shader over: on plain
    /// character.shpk it is a flat additive tint, and a quarter of WHITE there is a wash that turns black
    /// artwork into white slabs. Both were observed here, on the way to getting the shader right.
    /// </summary>
    public const float Emissive = 3.0f;

    // ── the regions ──────────────────────────────────────────────────────────

    /// <summary>
    /// How many separately-addressable regions one imported sheet may be split into.
    /// <para/>
    /// A glow mask is flat plateaus — full strength here, half strength there — and those plateaus are how
    /// its author made one part of a tattoo behave differently from another. Collapsing them onto one
    /// colour-table cell (which is what a shell with no <c>_id</c> gets) keeps the picture but throws that
    /// structure away: every region then shares one glow dial, one colour, and one light response.
    /// Splitting them out is what makes "this half is dark-only, that half is always there" reachable on an
    /// imported pack at all.
    /// <para/>
    /// Eight rather than sixteen: the rows above these stay free for the user, and no real sheet has been
    /// seen with more than three plateaus.
    /// </summary>
    public const int MaxRegions = 8;

    /// <summary>How much of the glowing area a plateau must hold to earn a row of its own. Below this it is
    /// an antialiased edge or a compression artefact, not a region anyone painted.</summary>
    public const float MinRegionFraction = 0.02f;

    /// <summary>How far apart two plateaus must be (in the 0–255 intensity range) to count as different
    /// regions. Anything closer is the same fill read through lossy compression.</summary>
    public const int Separation = 16;

    /// <summary>
    /// At or below this a pixel does not glow at all and takes no part in the split.
    /// <para/>
    /// Five rather than zero, so a lossily-compressed source still reads its own empty regions as empty.
    /// </summary>
    public const int Dark = 5;

    /// <summary>
    /// The distinct plateaus in a glow mask, brightest first.
    /// <para/>
    /// A plateau is a spike in the intensity histogram, because these masks fill a region with one flat
    /// value and only ramp at its outline. So: take the most populated value, claim everything within
    /// <see cref="Separation"/> of it as the same fill, and repeat. A value holding less than
    /// <paramref name="minFraction"/> of the glowing area is an edge or an artefact and gets no row of its
    /// own — the nearest real plateau absorbs it.
    /// <para/>
    /// Brightest first so row 1 is the main artwork, which is what someone opening Colors wants to find at
    /// the top rather than having to hunt for.
    /// <para/>
    /// Returns an empty list when nothing glows, and a single entry when the sheet is one flat region — the
    /// caller keeps the single-row shape there rather than authoring an index that says nothing.
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
    /// An RGBA index texture sending each glowing pixel to the row of the plateau it belongs to.
    /// <para/>
    /// Red is the row selector in the convention <see cref="ContentIndexTexture.RowOf"/> decodes —
    /// <c>(row − 1) × 17</c>, which round-trips exactly for all sixteen rows — and green is 255 for sub-row
    /// A. Pixels that do not glow are given the first row rather than a "selects nothing" alpha: the shell's
    /// coverage is already zero there so they render either way, and leaving a hole in the selector only
    /// gives the row-repair pass something to argue with.
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
