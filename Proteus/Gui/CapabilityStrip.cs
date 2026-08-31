using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;                       // FontAwesomeIcon.ToIconString, ColorHelpers.WithAlpha
using Proteus.Localization;

namespace Proteus.Gui;

/// <summary>
/// The band's second line: four icon-and-label pairs naming what the plugin actually does. Replaces a
/// caption that read "overlay compositor", which was true at launch and now describes about a quarter of
/// the plugin — it said nothing about wearing mods off-slot, adding toggles to someone else's mod, or
/// binding the whole look to a Glamourer design.
/// <para/>
/// Four labels do not fit at every window size (see <see cref="Draw"/>), so the row degrades to icons alone
/// and lets the caller show the hovered one's label instead. That is the whole reason this reports a result
/// rather than just painting.
/// </summary>
/// <remarks>
/// Everything here goes through the DRAW LIST, never through ImGui's layout. The status window is
/// AlwaysAutoResize on every tab but Toggles, so an item submitted at this width would feed straight back
/// into the window's fit — which is the same trap <see cref="BrandHeader"/> documents at length and the
/// reason the band contributes zero width. This strip rides the band, so it draws on every tab and keeps the
/// rule regardless of which mode the window is in.
/// </remarks>
internal static class CapabilityStrip
{
    /// <summary>
    /// The labels are <c>Func</c>s and not strings because <c>Strings.Reload()</c> rebuilds every holder on
    /// a language change. A captured value would pin the row to whatever language the game booted in.
    /// </summary>
    private static readonly (FontAwesomeIcon Icon, Func<string> Label)[] Items =
    {
        (FontAwesomeIcon.LayerGroup, () => Strings.Band.CapOverlay),
        (FontAwesomeIcon.Tshirt,     () => Strings.Band.CapWear),
        (FontAwesomeIcon.ToggleOn,   () => Strings.Band.CapToggle),
        (FontAwesomeIcon.Link,       () => Strings.Band.CapBind),
    };

    // Scratch, reused every frame. The band draws on the UI thread and nowhere else, so static buffers are
    // safe and keep a per-frame path off the allocator — the same reason ProteusStyle.Ellipsize measures
    // spans instead of slicing. Declared after Items because static initialisers run in declaration order.
    private static readonly string[] Labels = new string[Items.Length];
    private static readonly float[]  LabelW = new float[Items.Length];
    private static readonly float[]  Xs     = new float[Items.Length];

    /// <summary>What <see cref="Draw"/> did, so the caller can lay the rest of the line out against it.</summary>
    /// <param name="Collapsed">True when the labels did not fit and only icons were drawn. The caller owes
    /// the user a readout in that case — the icons alone are not self-explanatory.</param>
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

        // Measured in two separate font scopes, and it has to be that way: the icons come from Dalamud's
        // fixed-width FontAwesome atlas and the labels from whatever body font the user has configured.
        // Measuring a label inside the icon scope would size it in glyphs that font does not carry.
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

        // Plain IsWindowHovered, deliberately unflagged: it reports false while another window sits over
        // this point, so a file dialog parked on the band does not light the icons underneath it.
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

        var draw = ImGui.GetWindowDrawList();
        var dim  = ImGui.GetColorU32(ProteusStyle.Accent.WithAlpha(0.55f));
        var lit  = ImGui.GetColorU32(ProteusStyle.Accent);

        // Icons in one font scope, labels in the other, rather than pushing and popping per item. Each text
        // is centred on the row's own height because the two faces need not share a line height.
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var y = at.Y + ((rowH - iconH) * 0.5f);
            for (var i = 0; i < Items.Length; i++)
                draw.AddText(new Vector2(Xs[i], y), i == hovered ? lit : dim, Items[i].Icon.ToIconString());
        }

        if (!collapsed)
        {
            // Structure colour read at the call site so the labels follow the user's Dalamud style, per the
            // rule ProteusStyle's header comment sets out. Only the hovered one takes the brand colour.
            var text = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            var y    = at.Y + ((rowH - labelH) * 0.5f);
            for (var i = 0; i < Items.Length; i++)
                draw.AddText(new Vector2(Xs[i] + iconW + iconGap, y), i == hovered ? lit : text, Labels[i]);
        }

        return new Result(collapsed, right, hovered >= 0 ? Labels[hovered] : null);
    }
}
