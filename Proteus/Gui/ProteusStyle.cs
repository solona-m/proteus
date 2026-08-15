using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Proteus.Gui;

/// <summary>
/// The plugin's palette and shared widgets. Three kinds of colour live here, and the distinction is the
/// whole point of the file:
/// <list type="bullet">
/// <item>STRUCTURE (borders, frames, row backgrounds, disabled text) is never named here — read it from
/// <c>ImGui.GetColorU32(ImGuiCol.X)</c> at the call site so it follows whatever Dalamud style the user
/// picked.</item>
/// <item>SEMANTIC status is <see cref="Ok"/>/<see cref="Warn"/>/<see cref="Bad"/>, forwarding to
/// <see cref="ImGuiColors"/>. Those are mutable statics that DalamudColors.Apply() rewrites per entry
/// when a style defines them, so they follow the theme too — which is why they are properties and not
/// readonly fields. Caching one in a field would freeze it at whatever the style was on first use.</item>
/// <item>BRAND is <see cref="Accent"/> and its derived states — ours, fixed, theme-independent.</item>
/// </list>
/// </summary>
public static class ProteusStyle
{
    /// <summary>Set by <c>Plugin</c> at construction, cleared on dispose. Null before load and after
    /// unload, so every use is null-conditional.</summary>
    public static ProteusFonts? Fonts { get; set; }

    // ---- brand -------------------------------------------------------------------------------------

    /// <summary>
    /// Sampled from the real logo art (images/icon.png): #F27712, the centre pixel where the yellow→red
    /// sweep crosses, and within a hair of the mid-band average (#DF7717). Deliberately NOT the #F4C430
    /// gold at ColorTableEditor.DrawRenderingAsBadge — that gold already means "Animated glow", and a
    /// brand colour that reads as a state is worse than having no brand colour.
    /// </summary>
    public static readonly Vector4 Accent = new(0.949f, 0.467f, 0.071f, 1f);

    // Value is already 0.95, so Lighten() has no headroom to brighten into: desaturating toward white is
    // what reads as "lighter" here. Both go through ColorHelpers so the hue stays exactly the logo's.
    public static readonly Vector4 AccentHover  = Accent.Desaturate(0.20f);
    public static readonly Vector4 AccentActive = Accent.Darken(0.18f);

    // Fills sit BEHIND text, so they are alpha'd down to let the theme's own frame colour through —
    // full-strength orange behind label text is illegible under a light Dalamud style. Note this uses
    // WithAlpha and not ColorHelpers.Fade: Fade SUBTRACTS from alpha (hsv.A -= amount) rather than
    // setting it, so Fade(c, 0.55f) would give 0.45 alpha, not 0.55.
    public static readonly Vector4 AccentFill       = Accent.WithAlpha(0.45f);
    public static readonly Vector4 AccentFillHover  = AccentHover.WithAlpha(0.60f);
    public static readonly Vector4 AccentFillActive = AccentActive.WithAlpha(0.75f);

    /// <summary>Row tints and pill backgrounds — present, but never competing with text.</summary>
    public static readonly Vector4 AccentSoft = Accent.WithAlpha(0.18f);

    /// <summary>
    /// "A Glamourer design owns this." A different axis from the brand, so it stays blue and stays
    /// maximally distant from the warm accent — resist the urge to make everything orange.
    /// </summary>
    public static readonly Vector4 Binding = new(0.45f, 0.75f, 1f, 1f);

    // ---- semantic ----------------------------------------------------------------------------------

    public static Vector4 Ok => ImGuiColors.HealerGreen;

    /// <summary>"This worked, but read it." Unifies the (1,0.8,0.2) and (1,0.6,0.2) ambers that were
    /// spelled differently in different tabs.</summary>
    public static Vector4 Warn => ImGuiColors.DalamudOrange;

    /// <summary>
    /// Unifies the three separate spellings of "red" that were in use — (1,0.4,0.4), (1,0.35,0.35) and
    /// (1,0.3,0.3). Kept as the existing salmon rather than ImGuiColors.DalamudRed, which is pure
    /// (1,0,0) and markedly less legible on a dark background.
    /// </summary>
    public static readonly Vector4 Bad = new(1f, 0.4f, 0.4f, 1f);

    // ---- scale -------------------------------------------------------------------------------------

    /// <summary>
    /// Scale a hardcoded pixel size by the user's global UI scale. Window.SizeConstraints is scaled by
    /// Dalamud's window host already and must NOT go through this; everything drawn inside Draw() must.
    /// </summary>
    public static float S(float v) => v * ImGuiHelpers.GlobalScale;

    public static Vector2 S(float x, float y) => ImGuiHelpers.ScaledVector2(x, y);

    // ---- widgets -----------------------------------------------------------------------------------

    /// <summary>
    /// A heading in the game's Jupiter face with a short accent rule beneath it that fades out to the
    /// right, so it reads as an underline rather than as a full-width Separator.
    /// </summary>
    /// <param name="text">The heading.</param>
    /// <param name="display">False for text the user supplied (a mod name). Jupiter is a display face
    /// with narrow coverage, so anything that might carry CJK or accents stays on the default font.</param>
    public static void SectionHeader(string text, bool display = true)
    {
        using (var font = display ? Fonts?.PushHeader() : null)
            ImGui.TextUnformatted(text);

        // Measured from the item rect rather than from a predicted text size, so the rule tracks whatever
        // font actually rendered. That is what makes the font atlas arriving a frame or two late a
        // non-event: the heading grows and the rule grows with it.
        var min  = ImGui.GetItemRectMin();
        var max  = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var y    = max.Y + S(2f);

        uint solid = ImGui.GetColorU32(Accent);
        uint clear = ImGui.GetColorU32(Accent.WithAlpha(0f));
        // Corner order is upper-left, upper-right, lower-right, lower-left.
        draw.AddRectFilledMultiColor(
            new Vector2(min.X, y), new Vector2(max.X, y + S(1.5f)), solid, clear, clear, solid);

        ImGui.Dummy(new Vector2(0f, S(5f)));
    }

    private static Vector2 PillPad => S(5f, 1f);

    /// <summary>The total size <see cref="Pill"/> occupies, label plus padding.</summary>
    public static Vector2 PillSize(string text) => ImGui.CalcTextSize(text) + (PillPad * 2f);

    /// <summary>The widest label <see cref="Pill"/> can carry within <paramref name="pillWidth"/>.</summary>
    public static float PillTextBudget(float pillWidth) => pillWidth - (PillPad.X * 2f);

    /// <summary>
    /// <paramref name="text"/> shortened with an ellipsis until it fits <paramref name="maxWidth"/>.
    /// Returns the input unchanged when it already fits, so a caller can compare the result against what
    /// it passed in to decide whether the full text still needs to be reachable in a tooltip. Returns an
    /// empty string when not even the ellipsis fits, so the result is NEVER wider than asked for.
    /// </summary>
    public static string Ellipsize(string text, float maxWidth)
    {
        if (text.Length == 0 || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "…";
        var ellipsisWidth = ImGui.CalcTextSize(ellipsis).X;
        if (maxWidth <= ellipsisWidth)
            return string.Empty;

        // Binary search the longest prefix that fits, measuring SPANS rather than substrings: this runs on
        // every frame the band is visible, and slicing the string would allocate once per search step.
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text.AsSpan(0, mid)).X + ellipsisWidth <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }

        // Never cut between a surrogate pair — half a code point renders as a replacement glyph.
        if (lo > 0 && char.IsHighSurrogate(text[lo - 1]))
            lo--;

        return string.Concat(text.AsSpan(0, lo), ellipsis);
    }

    /// <summary>
    /// A rounded status badge. Generalises the "Rendering as:" badge that was the codebase's only
    /// designed component. Advances the cursor by the pill's full size, so SameLine and IsItemHovered
    /// behave as they would for any ordinary item.
    /// </summary>
    /// <remarks>
    /// Do NOT position one of these against the right edge of a WidthStretch table column. It submits a
    /// real item, so the column's required width becomes its own current width plus cell padding, the
    /// table asks for more room every frame, and an AlwaysAutoResize window creeps wider until it hits its
    /// maximum size. Draw straight to the draw list if the alignment has to depend on column geometry.
    /// </remarks>
    public static void Pill(string text, Vector4 colour)
    {
        var pad  = PillPad;
        var pill = PillSize(text);

        var draw   = ImGui.GetWindowDrawList();
        var screen = ImGui.GetCursorScreenPos();
        var round  = S(3f);
        draw.AddRectFilled(screen, screen + pill, ImGui.GetColorU32(colour.WithAlpha(0.16f)), round);
        draw.AddRect(screen, screen + pill, ImGui.GetColorU32(colour.WithAlpha(0.55f)), round);

        // Submit the label inset into the badge, then rewind and reserve the badge itself, so the LAST
        // item is the pill and not the text. Same rewind idiom as the right-aligned header button.
        var local = ImGui.GetCursorPos();
        ImGui.SetCursorPos(local + pad);
        ImGui.TextColored(colour, text);
        ImGui.SetCursorPos(local);
        ImGui.Dummy(pill);
    }

    /// <summary>The "this button is the current selection" idiom, previously copy-pasted four times.</summary>
    public static ImRaii.ColorDisposable Selected(bool on) =>
        ImRaii.PushColor(ImGuiCol.Button,        ImGui.GetColorU32(AccentFill),       on)
              .Push(ImGuiCol.ButtonHovered,      ImGui.GetColorU32(AccentFillHover),  on)
              .Push(ImGuiCol.ButtonActive,       ImGui.GetColorU32(AccentFillActive), on);

    /// <summary>
    /// One sortable table-column header. Replaces the two near-identical local functions in the Mods and
    /// Bindings tabs, which differed only by enum.
    /// </summary>
    public static void SortableHeader<T>(string label, T column, ref T current, ref bool desc, bool defaultDesc)
        where T : struct, Enum
    {
        ImGui.TableNextColumn();
        var active = EqualityComparer<T>.Default.Equals(current, column);
        var arrow  = active ? (desc ? " ▼" : " ▲") : string.Empty;

        // "###label" keeps the ImGui id stable across a direction flip — previously the arrow was part of
        // the id, so the header silently became a different item every time it was clicked.
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(Accent), active))
            ImGui.TableHeader($"{label}{arrow}###{label}");

        if (!ImGui.IsItemClicked())
            return;

        if (active)
            desc = !desc;
        else
            (current, desc) = (column, defaultDesc);
    }

    /// <summary>
    /// Tooltip for an item that may be disabled. A disabled item reports no hover under the default
    /// flags, so the "why is this off" explanation is unreachable at exactly the moment it is wanted —
    /// this always asks with AllowWhenDisabled. A null reason draws nothing.
    /// </summary>
    public static void ReasonTooltip(string? reason)
    {
        if (reason != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(reason);
    }

    /// <summary>
    /// A bordered panel with an accent bar down its left edge, painted around a group of widgets.
    /// </summary>
    /// <remarks>
    /// Deliberately a group and not a child region: the status window is AlwaysAutoResize, and a
    /// BeginChild with zero size does not auto-size to its content — it collapses to a fixed box and
    /// clips. Deliberately border-only with no fill, too: the frame is painted after the group closes,
    /// so a fill would cover the widgets, and the obvious fix (an ImDrawListSplitter) cannot be nested
    /// around an ImGui table, which splits the window draw list itself.
    /// </remarks>
    public static CardScope Card(Vector4? barColour = null) => new(barColour);

    public readonly struct CardScope : IDisposable
    {
        private readonly Vector4 bar;
        private readonly float inset;

        internal CardScope(Vector4? barColour)
        {
            bar   = barColour ?? Accent;
            inset = S(8f);
            ImGui.BeginGroup();
            ImGui.Indent(inset);
        }

        public void Dispose()
        {
            ImGui.Unindent(inset);
            ImGui.EndGroup();

            var min = ImGui.GetItemRectMin() - new Vector2(inset, S(3f));
            var max = ImGui.GetItemRectMax() + new Vector2(S(6f), S(3f));

            // Clamp the right edge to the content region. A card whose widest item already spans the full
            // width — the collapsing headers in Diagnostics do — would otherwise push its border outside
            // the window, where it is clipped away or drawn under the scrollbar once the tab is tall
            // enough to have one.
            var edge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            if (max.X > edge)
                max.X = edge;

            var draw = ImGui.GetWindowDrawList();
            draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border), S(3f));
            draw.AddRectFilled(min, new Vector2(min.X + S(2f), max.Y), ImGui.GetColorU32(bar), S(1f));
        }
    }
}
