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
/// The plugin's palette and shared widgets. STRUCTURE colours are never named here (read <c>ImGui.GetColorU32</c>
/// at the call site); SEMANTIC status (<see cref="Ok"/>/<see cref="Warn"/>/<see cref="Bad"/>) forwards to the mutable
/// <see cref="ImGuiColors"/> via properties so it follows the theme; BRAND (<see cref="Accent"/>) is fixed.
/// </summary>
public static class ProteusStyle
{
    /// <summary>Set by <c>Plugin</c> at construction, cleared on dispose. Null before load and after
    /// unload, so every use is null-conditional.</summary>
    public static ProteusFonts? Fonts { get; set; }

    /// <summary>
    /// Whether the display face (Jupiter, Latin only) can set the language currently in effect; otherwise headings
    /// fall back to the default font. Maintained by <c>LocSetup</c> on every language change.
    /// </summary>
    public static bool DisplayFontUsable { get; set; } = true;

    // ---- brand -------------------------------------------------------------------------------------

    /// <summary>
    /// #F27712, sampled from the logo art. Deliberately NOT the #F4C430 gold, which already means "Animated glow".
    /// </summary>
    public static readonly Vector4 Accent = ProteusTheme.Accent;

    // Value is already 0.95, so lighter means desaturated toward white; ColorHelpers keeps the logo's hue.
    public static readonly Vector4 AccentHover  = Accent.Desaturate(0.20f);
    public static readonly Vector4 AccentActive = Accent.Darken(0.18f);

    // Fills sit behind text, so they are alpha'd down. WithAlpha, not ColorHelpers.Fade: Fade SUBTRACTS from alpha.
    public static readonly Vector4 AccentFill       = Accent.WithAlpha(0.45f);
    public static readonly Vector4 AccentFillHover  = AccentHover.WithAlpha(0.60f);
    public static readonly Vector4 AccentFillActive = AccentActive.WithAlpha(0.75f);

    /// <summary>Row tints and pill backgrounds — present, but never competing with text.</summary>
    public static readonly Vector4 AccentSoft = Accent.WithAlpha(0.18f);

    /// <summary>"A Glamourer design owns this." Blue, a different axis from the warm brand accent.</summary>
    public static readonly Vector4 Binding = new(0.45f, 0.75f, 1f, 1f);

    // ---- semantic ----------------------------------------------------------------------------------

    public static Vector4 Ok => ImGuiColors.HealerGreen;

    /// <summary>"This worked, but read it."</summary>
    public static Vector4 Warn => ImGuiColors.DalamudOrange;

    /// <summary>Salmon rather than ImGuiColors.DalamudRed, whose pure red is less legible on a dark background.</summary>
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
    /// A heading in the game's Jupiter face over an accent rule that runs past it and fades out to the right, headed
    /// by a small diamond pip and lit by a faint glow.
    /// </summary>
    /// <param name="text">The heading.</param>
    /// <param name="display">False for text the user supplied (a mod name), which stays on the default font.</param>
    public static void SectionHeader(string text, bool display = true)
    {
        using (var font = display && DisplayFontUsable ? Fonts?.PushHeader() : null)
            ImGui.TextUnformatted(text);

        // Measured from the item rect, so the rule tracks whatever font actually rendered.
        var min  = ImGui.GetItemRectMin();
        var max  = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var y    = MathF.Round(max.Y + S(2.5f));

        // Past the text by a fixed reach, never past the content region: the rule contributes no width to auto-fit.
        var edge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var end  = MathF.Min(max.X + S(60f), edge);
        var pip  = S(2.5f);
        var from = min.X + (pip * 2f) + S(2f);

        ProteusDraw.SoftGlowEllipse(draw, new Vector2(from + ((end - from) * 0.25f), y),
            new Vector2((end - from) * 0.45f, S(4f)), Accent.WithAlpha(0.18f), layers: 6);
        ProteusDraw.Diamond(draw, new Vector2(min.X + pip, y + S(0.75f)), pip, Accent);
        ProteusDraw.FadeRuleRight(draw, new Vector2(from, y), new Vector2(end, y + S(1.5f)), Accent);

        ImGui.Dummy(new Vector2(0f, S(5f)));
    }

    /// <summary>
    /// A minor heading inside a panel: the label in the brand accent, then a hairline rule to the right edge of the
    /// content region (the CELL inside a table). Fades with a surrounding Alpha push, applied exactly once.
    /// </summary>
    /// <param name="suffix">An optional dimmed qualifier drawn after the label; the rule starts after it.</param>
    public static void SubHeader(string text, string? suffix = null)
    {
        // Spacing rather than a Dummy: a Dummy would be picked up by the GetItemRect* calls below.
        ImGui.Spacing();

        // Pushed as a Vector4, NOT GetColorU32: ImGui applies style.Alpha when it resolves the colour, so resolving here doubles it.
        using (ImRaii.PushColor(ImGuiCol.Text, Accent.WithAlpha(0.85f)))
            ImGui.TextUnformatted(text);

        if (suffix != null)
        {
            ImGui.SameLine(0f, S(4f));
            ImGui.TextDisabled(suffix);
        }

        // Measured off the item rect (the suffix's, when there is one), so the rule tracks the rendered font.
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        // The cursor is back at the start of the next line, so cursor.x + avail.x is the cell's right edge.
        var x0    = max.X + S(6f);
        var x1    = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var y     = MathF.Round((min.Y + max.Y) * 0.5f);   // whole pixel, or a 1px rule straddles two rows
        var thick = MathF.Max(1f, S(1f));

        // No room left: a three-pixel stub is worse than nothing.
        if (x1 > x0 + S(8f))
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(x0, y), new Vector2(x1, y + thick),
                ImGui.GetColorU32(Accent.WithAlpha(0.28f)));

        ImGui.Spacing();
    }

    private static Vector2 PillPad => S(5f, 1f);

    /// <summary>The total size <see cref="Pill"/> occupies, label plus padding.</summary>
    public static Vector2 PillSize(string text) => ImGui.CalcTextSize(text) + (PillPad * 2f);

    /// <summary>The widest label <see cref="Pill"/> can carry within <paramref name="pillWidth"/>.</summary>
    public static float PillTextBudget(float pillWidth) => pillWidth - (PillPad.X * 2f);

    /// <summary>
    /// <paramref name="text"/> shortened with an ellipsis until it fits <paramref name="maxWidth"/>. Returns the input
    /// unchanged when it fits, and empty when not even the ellipsis fits, so the result is never wider than asked.
    /// </summary>
    public static string Ellipsize(string text, float maxWidth)
    {
        if (text.Length == 0 || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "…";
        var ellipsisWidth = ImGui.CalcTextSize(ellipsis).X;
        if (maxWidth <= ellipsisWidth)
            return string.Empty;

        // Binary search measuring SPANS rather than substrings, so a search allocates nothing per frame.
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
    /// A rounded status badge. Advances the cursor by its full size, so SameLine and IsItemHovered behave normally.
    /// </summary>
    /// <remarks>
    /// Do NOT right-align one against a WidthStretch table column: the real item makes an AlwaysAutoResize window creep wider.
    /// </remarks>
    /// <param name="baselineOffset">How far ImGui pushes text down on this line (<c>DC.CurrLineTextBaseOffset</c>);
    /// the lozenge is drawn straight to the draw list and needs it. The caller measures it, see
    /// <c>ColorTableEditor.DrawRenderingAsBadge</c>.</param>
    public static void Pill(string text, Vector4 colour, float baselineOffset = 0f)
    {
        var pad  = PillPad;
        var pill = PillSize(text);

        var draw   = ImGui.GetWindowDrawList();
        var screen = ImGui.GetCursorScreenPos() + new Vector2(0f, baselineOffset);
        var round  = S(3f);
        draw.AddRectFilled(screen, screen + pill, ImGui.GetColorU32(colour.WithAlpha(0.16f)), round);
        draw.AddRect(screen, screen + pill, ImGui.GetColorU32(colour.WithAlpha(0.55f)), round);

        // Submit the label inset, then rewind and reserve the badge, so the LAST item is the pill. The label is not
        // offset (ImGui applies baselineOffset to it); the reservation is.
        var local = ImGui.GetCursorPos();
        ImGui.SetCursorPos(local + pad);
        ImGui.TextColored(colour, text);
        ImGui.SetCursorPos(local + new Vector2(0f, baselineOffset));
        ImGui.Dummy(pill);
    }

    /// <summary>
    /// <c>BeginTabBar</c> in the display face.
    /// </summary>
    /// <remarks>
    /// The font covers only the Begin call: the bar is sized from the font at Begin time, while tab content must stay
    /// on the default font. EndTabBar needs no push only while at least one tab item is submitted unconditionally.
    /// Returns the concrete ref struct, which cannot be widened to <c>IDisposable</c>.
    /// </remarks>
    public static ImRaii.TabBarDisposable HeaderTabBar(string id)
    {
        // Gated on the same latch as HeaderTabItem: the bar and its labels must use one font's metrics.
        using (DisplayFontUsable ? Fonts?.PushHeader() : null)
            return ImRaii.TabBar(id);
    }

    /// <summary><c>BeginTabItem</c> in the display face; the font is popped before the scope is returned.</summary>
    /// <param name="label">The visible text. Localized, so it changes with the UI language.</param>
    /// <param name="id">A stable ASCII token, never translated: <c>###</c> makes it the ImGui id, so a language change
    /// keeps the selection.</param>
    public static ImRaii.TabItemDisposable HeaderTabItem(
        string label, string id, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
    {
        ImRaii.TabItemDisposable tab;
        using (DisplayFontUsable ? Fonts?.PushHeader() : null)
            tab = ImRaii.TabItem($"{label}###{id}", flags);

        var selected = false;
        if (tab) selected = true;
        TabUnderline(selected);
        return tab;
    }

    /// <summary>
    /// An accent underline beneath the tab just submitted: full width under the selected tab, growing in from the
    /// centre under a hovered one. Read from the last item, which after BeginTabItem is the tab itself.
    /// </summary>
    private static void TabUnderline(bool selected)
    {
        var hovered = ImGui.IsItemHovered();
        var target  = selected ? 1f : hovered ? 0.55f : 0f;
        var e       = UiAnim.Ease(ImGuiP.GetItemID(), target, 14f, initial: target);
        if (e < 0.01f) return;

        var min   = ImGui.GetItemRectMin();
        var max   = ImGui.GetItemRectMax();
        var half  = (max.X - min.X) * 0.5f * e;
        var mid   = (min.X + max.X) * 0.5f;
        var thick = MathF.Max(1f, S(2f));
        var draw  = ImGui.GetWindowDrawList();

        ProteusDraw.SoftGlowEllipse(draw, new Vector2(mid, max.Y - thick), new Vector2(half, S(5f)),
            Accent.WithAlpha(0.22f * e), layers: 6);
        ProteusDraw.FadeRule(draw, new Vector2(mid - half, max.Y - thick), new Vector2(mid + half, max.Y),
            Accent.WithAlpha(0.35f + (0.65f * e)));
    }

    /// <summary>
    /// The brand tint for a tab bar. Safe to wrap the whole BeginTabBar/EndTabBar scope, unlike the font push:
    /// <c>ImGuiCol.Tab*</c> is only read inside BeginTabBar/BeginTabItem. Faint fills: the eased underline from
    /// <see cref="HeaderTabItem"/> carries the selection.
    /// </summary>
    public static ImRaii.ColorDisposable TabAccent() =>
        ImRaii.PushColor(ImGuiCol.Tab,          ImGui.GetColorU32(Accent.WithAlpha(0.06f)))
              .Push(ImGuiCol.TabHovered,        ImGui.GetColorU32(Accent.WithAlpha(0.20f)))
              .Push(ImGuiCol.TabActive,         ImGui.GetColorU32(Accent.WithAlpha(0.28f)))
              // Unfocused = the window doesn't have focus; pulled back so it does not compete with the focused one.
              .Push(ImGuiCol.TabUnfocused,       ImGui.GetColorU32(Accent.WithAlpha(0.03f)))
              .Push(ImGuiCol.TabUnfocusedActive, ImGui.GetColorU32(Accent.WithAlpha(0.14f)));

    /// <summary>The "this button is the current selection" idiom.</summary>
    public static ImRaii.ColorDisposable Selected(bool on) =>
        ImRaii.PushColor(ImGuiCol.Button,        ImGui.GetColorU32(AccentFill),       on)
              .Push(ImGuiCol.ButtonHovered,      ImGui.GetColorU32(AccentFillHover),  on)
              .Push(ImGuiCol.ButtonActive,       ImGui.GetColorU32(AccentFillActive), on);

    /// <summary>
    /// Dress the item just submitted as a prominent button: an accent border that eases in on hover and a sheen
    /// that sweeps across once as the hover begins. Call straight after the button.
    /// </summary>
    public static void Decorate()
    {
        var id      = ImGuiP.GetItemID();
        var hovered = ImGui.IsItemHovered();
        var min     = ImGui.GetItemRectMin();
        var max     = ImGui.GetItemRectMax();
        var round   = ImGui.GetStyle().FrameRounding;
        var draw    = ImGui.GetWindowDrawList();

        var e = UiAnim.Ease(id, hovered);
        if (e > 0.01f)
        {
            ProteusDraw.RectHalo(draw, min, max, round, S(4f), Accent.WithAlpha(0.30f * e), layers: 4);
            draw.AddRect(min, max, ImGui.GetColorU32(Accent.WithAlpha(0.75f * e)), round, ImDrawFlags.None,
                MathF.Max(1f, S(1f)));
        }
        ProteusDraw.HoverSheen(draw, min, max, UiAnim.Sheen(id, hovered));
    }

    /// <summary>A button with <see cref="Decorate"/>'s hover border and sheen; an optional FontAwesome icon leads the label.</summary>
    public static bool FancyButton(string label, FontAwesomeIcon? icon = null)
    {
        var clicked = icon is { } i
            ? Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(i, label)
            : ImGui.Button(label);
        Decorate();
        return clicked;
    }

    /// <summary>One sortable table-column header.</summary>
    /// <param name="label">The visible text. Localized.</param>
    /// <param name="id">A stable ASCII token, never translated, so the ImGui id does not move with the UI language.</param>
    public static void SortableHeader<T>(
        string label, string id, T column, ref T current, ref bool desc, bool defaultDesc)
        where T : struct, Enum
    {
        ImGui.TableNextColumn();
        var active = EqualityComparer<T>.Default.Equals(current, column);
        var arrow  = active ? (desc ? " ▼" : " ▲") : string.Empty;

        // "###id" keeps the ImGui id stable across a direction flip.
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(Accent), active))
            ImGui.TableHeader($"{label}{arrow}###{id}");

        if (!ImGui.IsItemClicked())
            return;

        if (active)
            desc = !desc;
        else
            (current, desc) = (column, defaultDesc);
    }

    /// <summary>
    /// Dimmed explanatory text that wraps to the window: ImGui.TextDisabled does not wrap, so every full-sentence
    /// notice goes through this.
    /// </summary>
    public static void DisabledWrapped(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.TextWrapped(text);
    }

    /// <summary><see cref="DisabledWrapped"/> in <see cref="Warn"/> amber, for why something expected did not happen.</summary>
    public static void WarnWrapped(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Warn))
            ImGui.TextWrapped(text);
    }

    /// <summary>
    /// Tooltip for an item that may be disabled: asks with AllowWhenDisabled, since a disabled item reports no hover.
    /// A null reason draws nothing.
    /// </summary>
    public static void ReasonTooltip(string? reason)
    {
        if (reason != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(reason);
    }

    /// <summary>
    /// A raised panel with an accent bar down its left edge, painted around a group of widgets. Its border warms to
    /// the accent while the mouse is over it.
    /// </summary>
    /// <remarks>
    /// A group, not a child: a zero-size BeginChild does not auto-size. The fill must sit UNDER the widgets, but the
    /// group's size is only known once it closes, so the fill is drawn up front from the size the same card measured
    /// last frame (cards are told apart by their order within the window). The first frame draws no fill; a resize
    /// lags one frame, which nobody can see.
    /// </remarks>
    public static CardScope Card(Vector4? barColour = null) => new(barColour);

    /// <summary>Last frame's frame rect per card, as (offset from the group's start, size).</summary>
    private static readonly Dictionary<uint, (Vector2 Offset, Vector2 Size)> CardRects = new();
    private static int cardFrame;
    private static int cardSeq;

    public readonly struct CardScope : IDisposable
    {
        private readonly Vector4 bar;
        private readonly float inset;
        private readonly uint key;
        private readonly Vector2 start;
        /// <summary>This frame's eased hover, 0 before the card has been measured once.</summary>
        private readonly float hover;

        internal CardScope(Vector4? barColour)
        {
            bar   = barColour ?? Accent;
            inset = S(8f);
            hover = 0f;

            var frame = ImGui.GetFrameCount();
            if (frame != cardFrame)
            {
                cardFrame = frame;
                cardSeq   = 0;
                // Cards that stopped drawing (a tab closed) fall out here rather than accumulating.
                if (CardRects.Count > 256) CardRects.Clear();
            }
            key   = unchecked(ImGui.GetID("##proteusCard") + (uint)(cardSeq++ * 0x9E3779B1u));
            start = ImGui.GetCursorScreenPos();

            if (CardRects.TryGetValue(key, out var r))
            {
                var min  = start + r.Offset;
                var max  = min + r.Size;
                var hot  = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                        && ImGui.IsMouseHoveringRect(min, max, false);
                var e    = hover = UiAnim.Ease(key, hot);
                var fill = Vector4.Lerp(ProteusTheme.CardFill, ProteusTheme.CardFillHover, e);
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), S(4f));
                // Light falling from above: a faint sheen across the top third.
                ProteusDraw.GradientV(draw, min + new Vector2(S(2f), S(1f)), new Vector2(max.X - S(1f), min.Y + ((max.Y - min.Y) * 0.33f)),
                    new Vector4(1f, 1f, 1f, 0.025f + (0.015f * e)), new Vector4(1f, 1f, 1f, 0f));
            }

            ImGui.BeginGroup();
            ImGui.Indent(inset);
        }

        public void Dispose()
        {
            ImGui.Unindent(inset);
            ImGui.EndGroup();

            var min = ImGui.GetItemRectMin() - new Vector2(inset, S(3f));
            var max = ImGui.GetItemRectMax() + new Vector2(S(6f), S(3f));

            // Clamp the right edge to the content region, or a full-width item pushes the border outside the window.
            var edge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            if (max.X > edge)
                max.X = edge;

            CardRects[key] = (min - start, max - min);

            var e     = hover;
            var draw  = ImGui.GetWindowDrawList();
            var round = S(4f);
            var line  = Vector4.Lerp(ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Border)), bar.WithAlpha(0.65f), e);
            draw.AddRect(min, max, ImGui.GetColorU32(line), round);

            // The bar fades from full strength at the top to a third at the bottom.
            ProteusDraw.GradientV(draw, min + new Vector2(0f, S(1f)), new Vector2(min.X + S(2.5f), max.Y - S(1f)),
                bar, bar.WithAlpha(0.35f));
            if (e > 0.01f)
                ProteusDraw.SoftGlowEllipse(draw, new Vector2(min.X + S(1f), (min.Y + max.Y) * 0.5f),
                    new Vector2(S(6f), (max.Y - min.Y) * 0.5f), bar.WithAlpha(0.25f * e), layers: 5);
        }
    }
}
