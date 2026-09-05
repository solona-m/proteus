using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;                       // ColorHelpers.WithAlpha
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Proteus.Gui;

/// <summary>
/// The window's identity band: the logo gradient, the wordmark, and whatever status the user needs to see
/// from every tab. Replaces a status area that rendered nothing at all in the normal case, so the window
/// used to open onto a bare tab bar.
/// </summary>
internal static class BrandHeader
{
    /// <summary>
    /// Unscaled band height. Tall enough for the Jupiter wordmark plus the capability row under it — 46
    /// before that row existed, when the second line was a one-phrase caption set in the body font.
    /// </summary>
    private const float Height = 60f;

    /// <summary>
    /// The band art, cropped offline from the middle of the square logo where the yellow→red sweep is
    /// widest (Resources/header.png, 512x128).
    /// </summary>
    /// <remarks>
    /// ISharedImmediateTexture hands back a wrap that is valid for THIS FRAME ONLY — Dalamud owns the
    /// lifetime and the eviction, so this must never be disposed and never cached across frames. Null
    /// while it is not yet resident, in which case the band simply skips its art layer that frame.
    /// </remarks>
    private static IDalamudTextureWrap? Art() =>
        Plugin.TextureProvider
              .GetFromManifestResource(typeof(Plugin).Assembly, "Proteus.Resources.header.png")
              .GetWrapOrDefault();

    /// <summary>
    /// Paint the band and return the screen rect it occupies, so the caller can lay content into it.
    /// Reserves vertical space only — see the remarks.
    /// </summary>
    /// <remarks>
    /// The band deliberately contributes ZERO width to the layout. The status window is AlwaysAutoResize on
    /// every tab but Toggles, so sizing the band from GetContentRegionAvail() would be a feedback loop: a
    /// wider band makes wider content, which makes a wider window, which makes a wider band, until
    /// MaximumSize clamps it. Instead the width is measured from the settled content region and only the
    /// height is reserved. The band draws on every tab, so the rule holds everywhere even though the loop it
    /// avoids can only close on the auto-fitting ones.
    /// </remarks>
    /// <param name="minWindowWidth">
    /// The window's own guaranteed minimum WIDTH, already scaled. Converted to a content-space floor
    /// below — passing it through unconverted would make the band wider than the region it lives in.
    /// </param>
    public static (Vector2 Min, Vector2 Max) Draw(float minWindowWidth)
    {
        var top = ImGui.GetCursorScreenPos();
        var h   = ProteusStyle.S(Height);

        // Content-region width rather than window width, so the band stays inside WindowPadding and can
        // never collide with the window's own rounded frame.
        //
        // Floored so that on the first frame — before the window has a settled size, when the content
        // region can report almost anything — the band is not narrower than the window will ever be, which
        // would put the right-aligned button in the middle of it. The floor must be in the SAME space as
        // the measurement: a window of minWindowWidth encloses a content region two window paddings
        // narrower, so comparing against the raw window width overhangs the region by exactly that much
        // whenever the window sits at its minimum size.
        var floor = minWindowWidth - (ImGui.GetStyle().WindowPadding.X * 2f);
        var w = Math.Max((ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) - top.X, floor);

        var min  = top;
        var max  = top + new Vector2(w, h);
        var draw = ImGui.GetWindowDrawList();
        var r    = ProteusStyle.S(5f);

        // 1. Acrylic. Prepend, not Add: the callback is inserted at command index 0 so it fires before
        //    every draw command in this window, which is what preserves z-order against whatever is
        //    behind it. Guarded on the main viewport because the blur samples the main render target — a
        //    window the user has torn off into its own platform window has no access to it, and asking
        //    anyway would blur nothing while still costing a GPU pass.
        //
        //    Clamped to the window's clip rect because the blur is a render callback operating on a
        //    SCREEN-SPACE rect: unlike every draw-list primitive below it, ImGui's clipping does not apply
        //    to it. Once the window scrolled, the band's art clipped away correctly while the glass carried
        //    on painting at its original coordinates — a pane of frost floating above the title bar.
        if (ImGui.GetWindowViewport().ID == ImGui.GetMainViewport().ID)
        {
            var blurMin = Vector2.Max(min, draw.GetClipRectMin());
            var blurMax = Vector2.Min(max, draw.GetClipRectMax());
            // Skip a sliver as well as nothing at all: at a couple of pixels the corner rounding is wider
            // than the rect it is rounding, which reads as a torn edge rather than as a thin band.
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

        // 3. Fade toward the window background from the left, so the wordmark and pills sit on a readable
        //    surface instead of on the loudest part of the gradient. Deliberately NOT an opaque base fill
        //    underneath the art — that would cover the blur from step 1 and waste the whole GPU pass.
        //    Corner order is upper-left, upper-right, lower-right, lower-left; AddRectFilledMultiColor has
        //    no rounded variant, but the faded edge is WindowBg against WindowBg so the square corners it
        //    leaves are not visible.
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
