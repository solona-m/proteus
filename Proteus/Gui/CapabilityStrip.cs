using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;                       // FontAwesomeIcon.ToIconString, ColorHelpers.WithAlpha
using Proteus.Localization;

namespace Proteus.Gui;

/// <summary>
/// The band's second line: four icon-and-label pairs naming what the plugin does, each a link to its README
/// section (<see cref="ReadmeLinks"/>). When the labels do not fit it collapses to icons and reports so.
/// </summary>
/// <remarks>
/// Everything goes through the DRAW LIST, never ImGui's layout, so the strip contributes no width to an
/// AlwaysAutoResize window (see <see cref="BrandHeader"/>).
/// </remarks>
internal static class CapabilityStrip
{
    /// <summary>
    /// The labels are <c>Func</c>s because <c>Strings.Reload()</c> rebuilds every holder on a language change.
    /// </summary>
    private static readonly (FontAwesomeIcon Icon, Func<string> Label, ReadmeSection Section)[] Items =
    {
        (FontAwesomeIcon.LayerGroup, () => Strings.Band.CapOverlay, ReadmeSection.ColorEditor),
        (FontAwesomeIcon.Tshirt,     () => Strings.Band.CapWear,    ReadmeSection.Import),
        (FontAwesomeIcon.PaintBrush, () => Strings.Band.CapReshape, ReadmeSection.Studio),
        (FontAwesomeIcon.Link,       () => Strings.Band.CapBind,    ReadmeSection.Bindings),
    };

    // Scratch reused every frame, safe because the band draws only on the UI thread. Declared after Items because
    // static initialisers run in declaration order.
    private static readonly string[] Labels = new string[Items.Length];
    private static readonly float[]  LabelW = new float[Items.Length];
    private static readonly float[]  Xs     = new float[Items.Length];

    /// <summary>What <see cref="Draw"/> did, so the caller can lay the rest of the line out against it.</summary>
    /// <param name="Collapsed">True when the labels did not fit and only icons were drawn; the caller should show a readout.</param>
    /// <param name="Right">Screen X the row ends at, trailing gap excluded.</param>
    /// <param name="Hovered">Label of the item under the mouse, or null.</param>
    internal readonly record struct Result(bool Collapsed, float Right, string? Hovered);

    /// <summary>
    /// Paint the row at <paramref name="at"/>, with labels only if <paramref name="avail"/> can hold them.
    /// </summary>
    /// <param name="at">Top-left, in screen space.</param>
    /// <param name="avail">Width the row may use before it would run under the band's right-aligned button.</param>
    public static Result Draw(Vector2 at, float avail)
    {
        var iconGap  = ProteusStyle.S(6f);    // icon → its own label
        var itemGap  = ProteusStyle.S(16f);   // labelled item → next item
        var tightGap = ProteusStyle.S(10f);   // icon → next icon, once the labels are gone

        // Measured in two separate font scopes: icons come from the FontAwesome atlas, labels from the body font.
        var labelH = 0f;
        for (var i = 0; i < Items.Length; i++)
        {
            Labels[i] = Items[i].Label();
            var sz = ImGui.CalcTextSize(Labels[i]);
            LabelW[i] = sz.X;
            labelH = Math.Max(labelH, sz.Y);
        }

        float iconW, iconH;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            // Fixed-width by construction, so one measurement answers for all four.
            var sz = ImGui.CalcTextSize(Items[0].Icon.ToIconString());
            iconW = sz.X;
            iconH = sz.Y;
        }

        var full = itemGap * (Items.Length - 1);
        for (var i = 0; i < Items.Length; i++)
            full += iconW + iconGap + LabelW[i];

        var collapsed = full > avail;
        var rowH      = Math.Max(iconH, labelH);

        var x = at.X;
        for (var i = 0; i < Items.Length; i++)
        {
            Xs[i] = x;
            x += collapsed ? iconW + tightGap : iconW + iconGap + LabelW[i] + itemGap;
        }
        var right = x - (collapsed ? tightGap : itemGap);

        // Plain IsWindowHovered, unflagged, so a window over the band does not light the icons underneath.
        var hovered = -1;
        if (ImGui.IsWindowHovered())
        {
            for (var i = 0; i < Items.Length; i++)
            {
                var w = collapsed ? iconW : iconW + iconGap + LabelW[i];
                if (ImGui.IsMouseHoveringRect(new Vector2(Xs[i], at.Y), new Vector2(Xs[i] + w, at.Y + rowH)))
                {
                    hovered = i;
                    break;
                }
            }
        }

        // The link, read straight off the mouse: an InvisibleButton would submit an item. Language asked at click time.
        if (hovered >= 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(Strings.Band.CapLinkTip);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var url = ReadmeLinks.Url(Plugin.PluginInterface.UiLanguage, Items[hovered].Section);
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* opening a browser is best-effort */ }
            }
        }

        var draw = ImGui.GetWindowDrawList();
        var dim  = ImGui.GetColorU32(ProteusStyle.Accent.WithAlpha(0.55f));
        var lit  = ImGui.GetColorU32(ProteusStyle.Accent);

        // One font scope each for icons and labels; each text centred on the row since the faces' line heights differ.
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var y = at.Y + ((rowH - iconH) * 0.5f);
            for (var i = 0; i < Items.Length; i++)
                draw.AddText(new Vector2(Xs[i], y), i == hovered ? lit : dim, Items[i].Icon.ToIconString());
        }

        if (!collapsed)
        {
            // Structure colour read at the call site so the labels follow the Dalamud style; only the hovered one is brand.
            var text = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            var y    = at.Y + ((rowH - labelH) * 0.5f);
            for (var i = 0; i < Items.Length; i++)
                draw.AddText(new Vector2(Xs[i] + iconW + iconGap, y), i == hovered ? lit : text, Labels[i]);
        }

        return new Result(collapsed, right, hovered >= 0 ? Labels[hovered] : null);
    }
}
