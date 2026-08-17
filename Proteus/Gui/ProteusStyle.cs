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

    /// <summary>
    /// Whether the display face (Jupiter) can set the language currently in effect. Maintained by
    /// <c>LocSetup</c> on every language change.
    /// <para/>
    /// Jupiter carries Latin only — <c>GameFontFamily.Jupiter</c> is documented as "Contains Latin
    /// characters", and the game uses it for job names, nothing longer. Under Japanese, Chinese, Korean or
    /// Russian every glyph it is asked for is missing, so the tab bar and the section headings — the two
    /// places that push it — would render as boxes. Falling back to the default font there is not a
    /// downgrade worth avoiding: Dalamud has already stocked that atlas with the right glyph ranges for
    /// whatever language it is set to, which is exactly the coverage Jupiter lacks.
    /// </summary>
    public static bool DisplayFontUsable { get; set; } = true;

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
        using (var font = display && DisplayFontUsable ? Fonts?.PushHeader() : null)
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

    /// <summary>
    /// <c>BeginTabBar</c> in the display face, so a tab strip reads as part of the same design language
    /// as <see cref="SectionHeader"/> rather than as stock ImGui.
    /// </summary>
    /// <remarks>
    /// The font is pushed for the <c>Begin</c> CALL and popped before the scope is handed back — which is
    /// the whole point of the helper existing. The returned scope spans the tab's CONTENT, and content
    /// must stay on the default font: Jupiter is a display face with narrow coverage (see
    /// <see cref="ProteusFonts"/>), so a mod name with an accent or any CJK inside a tab would render as
    /// boxes. <c>using (…) return x;</c> disposes after the return expression is evaluated, so the push
    /// covers exactly the one call and nothing else.
    /// <para/>
    /// <c>BeginTabBar</c> needs the push as much as the items do, and it is the load-bearing one: ImGui
    /// sizes the bar rect as <c>g.FontSize + FramePadding.y * 2</c> at Begin time, and that single height
    /// then fixes the separator's y, the origin of every tab shape, the space reserved in the window, and
    /// where content resumes. Sized for the default font while the labels drew in Jupiter, the tabs would
    /// hang below their own bar and overdraw the separator.
    /// <para/>
    /// <c>EndTabBar</c> does NOT need it. The bar's deferred width layout runs at the top of the FIRST
    /// <c>BeginTabItem</c> of the frame, not at the end — the End path is a fallback for when no tab item
    /// was submitted at all. That fallback is unreachable only because the call site submits all six items
    /// unconditionally; if any of them ever becomes conditional, either keep one unconditional or wrap the
    /// End as well. That first-item layout also re-measures every tab from the names stored last frame,
    /// which is why the widths self-correct one frame after the font atlas finishes building.
    /// <para/>
    /// Returns the concrete <c>ImRaii.TabBarDisposable</c> rather than an interface deliberately: it is a
    /// ref struct, so widening it to <c>IDisposable</c> is not merely wasteful but illegal — and it would
    /// throw away the implicit bool conversion the <c>if (tabs)</c> at the call site depends on.
    /// </remarks>
    public static ImRaii.TabBarDisposable HeaderTabBar(string id)
    {
        // Gated on the same latch as HeaderTabItem, and it has to be: the bar sizes itself from the font
        // active during BeginTabBar. Pushing Jupiter here while the items fell back to the default face
        // would lay the bar out to one font's metrics and draw the labels in another's.
        using (DisplayFontUsable ? Fonts?.PushHeader() : null)
            return ImRaii.TabBar(id);
    }

    /// <summary><c>BeginTabItem</c> in the display face. See <see cref="HeaderTabBar"/> for why the font
    /// is popped before the scope is returned.</summary>
    /// <remarks>Every item needs the push, not just the first: ImGui measures each label with
    /// <c>CalcTextSize</c> and draws its glyphs inside <c>TabItemEx</c>. The first call additionally runs
    /// the whole bar's layout, which wrapping each call covers at no extra cost.</remarks>
    /// <param name="label">The visible text. Localized, so it changes with the UI language.</param>
    /// <param name="id">A stable ASCII token, never translated. ImGui derives a widget's identity from its
    /// label, so without this the six tabs would become six DIFFERENT tabs the moment the language changed
    /// — losing the selection — and two labels that happened to translate alike would collide into one
    /// item. <c>###</c> replaces the identity outright rather than appending to it, so only this token
    /// counts.</param>
    public static ImRaii.TabItemDisposable HeaderTabItem(
        string label, string id, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
    {
        using (DisplayFontUsable ? Fonts?.PushHeader() : null)
            return ImRaii.TabItem($"{label}###{id}", flags);
    }

    /// <summary>
    /// The brand tint for a tab bar. Unlike a font push this is safe to wrap the whole
    /// BeginTabBar/EndTabBar scope: <c>ImGuiCol.Tab*</c> is read in exactly two places, both inside
    /// <c>BeginTabBar</c>/<c>BeginTabItem</c>, so it cannot leak into tab content — whereas <c>g.Font</c>
    /// is read by every text draw there is. Do not "tidy" the two into one helper; that asymmetry is the
    /// whole reason they are scoped differently. One push per frame rather than one per item because
    /// <c>ImRaii.ColorDisposable</c> is a class, not a struct.
    /// <para/>
    /// Tinting <see cref="ImGuiCol.TabActive"/> also recolours the rule ImGui draws under the whole bar,
    /// which lands close to the accent rule <see cref="SectionHeader"/> hand-draws — for free, and at full
    /// window width. Uses the alpha'd <see cref="AccentFill"/> family rather than the solid
    /// <see cref="Accent"/> for the same reason <see cref="Selected"/> does: these sit behind label text,
    /// and full-strength orange behind text is illegible under a light Dalamud style.
    /// </summary>
    public static ImRaii.ColorDisposable TabAccent() =>
        ImRaii.PushColor(ImGuiCol.Tab,          ImGui.GetColorU32(AccentSoft))
              .Push(ImGuiCol.TabHovered,        ImGui.GetColorU32(AccentFillHover))
              .Push(ImGuiCol.TabActive,         ImGui.GetColorU32(AccentFill))
              // Unfocused = the window doesn't have focus. Same hierarchy, pulled back so an inactive
              // window's tabs don't compete with the focused one's.
              .Push(ImGuiCol.TabUnfocused,       ImGui.GetColorU32(AccentSoft.WithAlpha(0.10f)))
              .Push(ImGuiCol.TabUnfocusedActive, ImGui.GetColorU32(AccentFill.WithAlpha(0.25f)));

    /// <summary>The "this button is the current selection" idiom, previously copy-pasted four times.</summary>
    public static ImRaii.ColorDisposable Selected(bool on) =>
        ImRaii.PushColor(ImGuiCol.Button,        ImGui.GetColorU32(AccentFill),       on)
              .Push(ImGuiCol.ButtonHovered,      ImGui.GetColorU32(AccentFillHover),  on)
              .Push(ImGuiCol.ButtonActive,       ImGui.GetColorU32(AccentFillActive), on);

    /// <summary>
    /// One sortable table-column header. Replaces the two near-identical local functions in the Mods and
    /// Bindings tabs, which differed only by enum.
    /// </summary>
    /// <param name="label">The visible text. Localized.</param>
    /// <param name="id">A stable ASCII token, never translated. This used to be <paramref name="label"/>
    /// itself, which was fine while the label was a hardcoded English constant; once it is translated the
    /// id would move with the UI language and the header would again "silently become a different item",
    /// which is the exact bug the <c>###</c> was added to fix.</param>
    public static void SortableHeader<T>(
        string label, string id, T column, ref T current, ref bool desc, bool defaultDesc)
        where T : struct, Enum
    {
        ImGui.TableNextColumn();
        var active = EqualityComparer<T>.Default.Equals(current, column);
        var arrow  = active ? (desc ? " ▼" : " ▲") : string.Empty;

        // "###id" keeps the ImGui id stable across a direction flip — previously the arrow was part of
        // the id, so the header silently became a different item every time it was clicked.
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
    /// Dimmed explanatory text that wraps to the window instead of running off the right edge.
    /// <para/>
    /// <c>ImGui.TextDisabled</c> draws a single unwrapped line and lets the window clip whatever does not
    /// fit, which is silent and looks fine right up until the text gets longer — so every full-sentence
    /// notice goes through this rather than through TextDisabled directly. Localization is what made that
    /// stop being hypothetical: German and Russian run about a third longer than the English these lines
    /// were laid out against.
    /// </summary>
    public static void DisabledWrapped(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.TextWrapped(text);
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
