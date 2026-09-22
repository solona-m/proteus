using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Proteus.Gui;

/// <summary>
/// Draw-list primitives for the hand-drawn chrome: fading rules, soft glows, gradient tints, corner brackets, a hover
/// sheen and cover-cropped images. None of them submit an ImGui item, so they never touch layout or auto-fit.
/// </summary>
/// <remarks>Colours go through <c>GetColorU32(Vector4)</c>, so a surrounding Alpha push fades them like any widget.</remarks>
internal static class ProteusDraw
{
    private static uint U32(Vector4 c) => ImGui.GetColorU32(c);

    /// <summary>A horizontal rule that is solid in the middle and fades to nothing at both ends.</summary>
    public static void FadeRule(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 col)
    {
        var mid   = MathF.Round((min.X + max.X) * 0.5f);
        var solid = U32(col);
        var clear = U32(col.WithAlpha(0f));
        // Corner order is upper-left, upper-right, lower-right, lower-left.
        dl.AddRectFilledMultiColor(min, new Vector2(mid, max.Y), clear, solid, solid, clear);
        dl.AddRectFilledMultiColor(new Vector2(mid, min.Y), max, solid, clear, clear, solid);
    }

    /// <summary>A rule that is solid at the left and fades out to the right.</summary>
    public static void FadeRuleRight(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 col)
    {
        var solid = U32(col);
        var clear = U32(col.WithAlpha(0f));
        dl.AddRectFilledMultiColor(min, max, solid, clear, clear, solid);
    }

    /// <summary>
    /// A blurred disc: <paramref name="layers"/> concentric circles, each carrying an equal share of the alpha, so the
    /// centre reaches <c>col.W</c> and the edge falls off smoothly. No shader needed.
    /// </summary>
    public static void SoftGlow(ImDrawListPtr dl, Vector2 centre, float radius, Vector4 col, int layers = 14)
    {
        if (radius <= 0f || col.W <= 0f) return;
        var share = U32(col.WithAlpha(col.W / layers));
        for (var i = 0; i < layers; i++)
        {
            var r = radius * (1f - (i / (float)layers));
            dl.AddCircleFilled(centre, r, share, 48);
        }
    }

    /// <summary>A blurred ellipse, for glows behind wide shapes such as a line of text.</summary>
    public static void SoftGlowEllipse(ImDrawListPtr dl, Vector2 centre, Vector2 radius, Vector4 col, int layers = 12)
    {
        if (radius.X <= 0f || radius.Y <= 0f || col.W <= 0f) return;
        var share = U32(col.WithAlpha(col.W / layers));
        for (var i = 0; i < layers; i++)
            EllipseFilled(dl, centre, radius * (1f - (i / (float)layers)), share);
    }

    /// <summary>A filled axis-aligned ellipse; the binding has no AddEllipseFilled, so it is built as a convex path.</summary>
    private static void EllipseFilled(ImDrawListPtr dl, Vector2 centre, Vector2 radius, uint col, int segments = 40)
    {
        for (var i = 0; i < segments; i++)
        {
            var a = i * MathF.Tau / segments;
            dl.PathLineTo(centre + new Vector2(MathF.Cos(a) * radius.X, MathF.Sin(a) * radius.Y));
        }
        dl.PathFillConvex(col);
    }

    /// <summary>A soft rectangular halo just outside a rounded rect: stacked outlines fading outward.</summary>
    public static void RectHalo(ImDrawListPtr dl, Vector2 min, Vector2 max, float rounding, float spread, Vector4 col, int layers = 6)
    {
        if (col.W <= 0f || spread <= 0f) return;
        for (var i = 1; i <= layers; i++)
        {
            var t   = i / (float)layers;
            var pad = spread * t;
            var a   = col.W * (1f - t) * (1f - t);
            dl.AddRect(min - new Vector2(pad), max + new Vector2(pad), U32(col.WithAlpha(a)),
                rounding + pad, ImDrawFlags.None, MathF.Max(1f, spread / layers));
        }
    }

    /// <summary>A vertical gradient from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    public static void GradientV(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 top, Vector4 bottom)
    {
        var t = U32(top);
        var b = U32(bottom);
        dl.AddRectFilledMultiColor(min, max, t, t, b, b);
    }

    /// <summary>L-shaped brackets at all four corners of a rect, <paramref name="len"/> long on each arm.</summary>
    public static void CornerBrackets(ImDrawListPtr dl, Vector2 min, Vector2 max, float len, float thick, Vector4 col)
    {
        var c = U32(col);
        // Each arm is a filled rect rather than a line, so it stays crisp at fractional thickness.
        void Arm(Vector2 a, Vector2 b) => dl.AddRectFilled(Vector2.Min(a, b), Vector2.Max(a, b), c);

        Arm(min, new Vector2(min.X + len, min.Y + thick));
        Arm(min, new Vector2(min.X + thick, min.Y + len));

        Arm(new Vector2(max.X - len, min.Y), new Vector2(max.X, min.Y + thick));
        Arm(new Vector2(max.X - thick, min.Y), new Vector2(max.X, min.Y + len));

        Arm(new Vector2(min.X, max.Y - thick), new Vector2(min.X + len, max.Y));
        Arm(new Vector2(min.X, max.Y - len), new Vector2(min.X + thick, max.Y));

        Arm(new Vector2(max.X - len, max.Y - thick), max);
        Arm(new Vector2(max.X - thick, max.Y - len), max);
    }

    /// <summary>A small diamond, used as a pip at the head of a rule.</summary>
    public static void Diamond(ImDrawListPtr dl, Vector2 centre, float r, Vector4 col)
        => dl.AddQuadFilled(
            centre + new Vector2(0f, -r), centre + new Vector2(r, 0f),
            centre + new Vector2(0f, r), centre + new Vector2(-r, 0f), U32(col));

    /// <summary>
    /// A bright band swept left to right across a rect at <paramref name="progress"/> (0..1). Clipped to the rect.
    /// Draws nothing for a negative progress (see <see cref="UiAnim.Sheen"/>).
    /// </summary>
    public static void HoverSheen(ImDrawListPtr dl, Vector2 min, Vector2 max, float progress, float strength = 0.16f)
    {
        if (progress < 0f) return;
        var w    = max.X - min.X;
        var band = MathF.Max(w * 0.35f, 8f);
        var x0   = min.X - band + ((w + band) * progress);
        var mid  = x0 + (band * 0.5f);

        var clear  = U32(new Vector4(1f, 1f, 1f, 0f));
        var bright = U32(new Vector4(1f, 1f, 1f, strength * (1f - (progress * 0.6f))));

        dl.PushClipRect(min, max, true);
        dl.AddRectFilledMultiColor(new Vector2(x0, min.Y), new Vector2(mid, max.Y), clear, bright, bright, clear);
        dl.AddRectFilledMultiColor(new Vector2(mid, min.Y), new Vector2(x0 + band, max.Y), bright, clear, clear, bright);
        dl.PopClipRect();
    }
}
