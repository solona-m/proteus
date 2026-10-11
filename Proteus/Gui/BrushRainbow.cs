using System;

namespace Proteus.Gui;

/// <summary>
/// The colour a brush's reach is shown in, by how strongly the brush moves a point: red at full strength in the middle,
/// through yellow, green and blue to violet at the rim. One scale for the model view and the character, so the two
/// read alike.
/// </summary>
internal static class BrushRainbow
{
    /// <summary>The hue at the rim, in degrees; the middle is 0 (red). Stops short of magenta, which reads as red again.</summary>
    private const float RimHue = 270f;

    /// <summary>The colour for a falloff weight <paramref name="w"/>, 0 (rim) to 1 (middle), as 0..255 channels.</summary>
    public static (int R, int G, int B) At(float w)
    {
        float h = (1f - Math.Clamp(w, 0f, 1f)) * RimHue / 60f;
        int sector = Math.Min((int)h, 4);
        float f = h - sector, q = 1f - f;
        var (r, g, b) = sector switch
        {
            0 => (1f, f, 0f),   // red → yellow
            1 => (q, 1f, 0f),   // yellow → green
            2 => (0f, 1f, f),   // green → cyan
            3 => (0f, q, 1f),   // cyan → blue
            _ => (f, 0f, 1f),   // blue → violet
        };
        return ((int)(r * 255f + 0.5f), (int)(g * 255f + 0.5f), (int)(b * 255f + 0.5f));
    }

    /// <summary>
    /// How far in from the rim the colour fades up from clear, as a falloff weight: a soft edge rather than a hard
    /// violet band where the brush stops.
    /// </summary>
    private const float FadeIn = 0.25f;

    /// <summary>How much of a view's full opacity the colour gets at weight <paramref name="w"/>: none at the rim, all of
    /// it from <see cref="FadeIn"/> inward. Both views use it, so their edges match.</summary>
    public static float Fade(float w) => Math.Clamp(w / FadeIn, 0f, 1f);

    /// <summary>The same colour packed for an ImGui draw list (ABGR), at <paramref name="alpha"/> 0..1.</summary>
    public static uint Abgr(float w, float alpha)
    {
        var (r, g, b) = At(w);
        uint a = (uint)(Math.Clamp(alpha, 0f, 1f) * 255f + 0.5f);
        return (a << 24) | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
    }
}
