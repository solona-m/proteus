using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;                       // ColorHelpers.WithAlpha
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Proteus.Gui;

/// <summary>
/// The window's identity band: the logo gradient, the wordmark, and whatever status the user needs to see from every tab.
/// </summary>
internal static class BrandHeader
{
    /// <summary>Unscaled band height: the Jupiter wordmark plus the capability row under it.</summary>
    private const float Height = 60f;

    /// <summary>The band art (Resources/header.png, 512x128).</summary>
    /// <remarks>
    /// The wrap is valid for THIS FRAME ONLY: never dispose or cache it. Null while not yet resident; the art layer is skipped.
    /// </remarks>
    private static IDalamudTextureWrap? Art() =>
        Plugin.TextureProvider
              .GetFromManifestResource(typeof(Plugin).Assembly, "Proteus.Resources.header.png")
              .GetWrapOrDefault();

    /// <summary>
    /// Paint the band and return the screen rect it occupies, so the caller can lay content into it.
    /// </summary>
    /// <remarks>
    /// Contributes ZERO width to the layout: under AlwaysAutoResize a band sized from GetContentRegionAvail() would
    /// widen the window every frame. Only the height is reserved.
    /// </remarks>
    /// <param name="minWindowWidth">The window's guaranteed minimum WIDTH, already scaled; converted to content space below.</param>
    public static (Vector2 Min, Vector2 Max) Draw(float minWindowWidth)
    {
        var top = ImGui.GetCursorScreenPos();
        var h   = ProteusStyle.S(Height);

        // Content-region width, floored at the minimum window's content width (two window paddings narrower) so the
        // first frame's unsettled region cannot make the band too narrow.
        var floor = minWindowWidth - (ImGui.GetStyle().WindowPadding.X * 2f);
        var w = Math.Max((ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) - top.X, floor);

        var min  = top;
        var max  = top + new Vector2(w, h);
        var draw = ImGui.GetWindowDrawList();
        var r    = ProteusStyle.S(5f);

        // 1. Acrylic. Prepend, so the callback fires before every draw command in this window. Main viewport only: the
        //    blur samples the main render target. Clamped to the clip rect by hand: ImGui's clipping does not apply to a callback.
        if (ImGui.GetWindowViewport().ID == ImGui.GetMainViewport().ID)
        {
            var blurMin = Vector2.Max(min, draw.GetClipRectMin());
            var blurMax = Vector2.Min(max, draw.GetClipRectMax());
            // Skip a sliver: at a couple of pixels the corner rounding is wider than the rect.
            if (blurMax.X - blurMin.X > ProteusStyle.S(4f) && blurMax.Y - blurMin.Y > ProteusStyle.S(4f))
                ImGuiHelpers.PrependBlurBehind(
                    draw, blurMin, blurMax,
                    blurStrength: 6f,                                  // clamped to 8 by Dalamud
                    rounding: r,
                    tintColor: ProteusStyle.Accent.WithAlpha(0.10f),    // A is blend strength, not opacity
                    luminosityColor: new Vector4(0f, 0f, 0f, 0.25f),    // pull dark so the wordmark reads
                    noiseOpacity: 0.04f);
        }

        // 2. The art, at partial strength so it is a backdrop and not the subject.
        if (Art() is { } art)
            draw.AddImageRounded(art.Handle, min, max, Vector2.Zero, Vector2.One,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.5f)), r);

        // 3. Fade toward the window background from the left, so the wordmark sits on a readable surface. No opaque
        //    base fill: it would cover the blur. Corner order is upper-left, upper-right, lower-right, lower-left.
        var bg = ImGui.GetColorU32(ImGuiCol.WindowBg);
        draw.AddRectFilledMultiColor(min, max, bg, 0u, 0u, bg);
        draw.AddRect(min, max, ImGui.GetColorU32(ProteusStyle.Accent.WithAlpha(0.35f)), r);

        return (min, max);
    }

    /// <summary>Reserve the band's height without contributing any width to the auto-resize fit.</summary>
    public static void Reserve(Vector2 min)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(0f, ProteusStyle.S(Height)));
    }
}
