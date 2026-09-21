using System;
using Dalamud.Bindings.ImGui;
using Proteus.Localization;

namespace Proteus.Gui;

/// <summary>
/// The search box at the top of a dropdown, and the rule for what it matches. One place, so every dropdown in the
/// Studio searches the same way.
/// </summary>
internal static class ComboSearch
{
    /// <summary>
    /// Draw the search box. Call it first thing inside an open dropdown. It empties and takes the keyboard each time the
    /// list opens, so typing straight after the click narrows it — Neolithe's chest list is 114 options long, and
    /// scrolling it is not a way to find "NSFW Almond L".
    /// </summary>
    public static void Box(string id, ref string text)
    {
        if (ImGui.IsWindowAppearing())
        {
            text = "";
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search" + id, Strings.Export.FilterHint, ref text, 64);
    }

    /// <summary>
    /// Whether every word typed starts a word of <paramref name="haystack"/>, in any order and any case — so
    /// "almond nsfw l" finds "NEOBELLY ALMOND · NSFW Almond L".
    /// <para/>
    /// Word starts rather than anywhere, and a single letter only as a whole word, because sizes are single letters:
    /// matched anywhere, "L" is in every "Almond" and "S" in every "SFW", and the search would narrow nothing.
    /// </summary>
    public static bool Matches(string filter, string haystack)
    {
        var words = haystack.Split(NotWord, StringSplitOptions.RemoveEmptyEntries);
        foreach (string term in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool found = false;
            foreach (string word in words)
            {
                found = term.Length == 1
                            ? string.Equals(word, term, StringComparison.OrdinalIgnoreCase)
                            : word.StartsWith(term, StringComparison.OrdinalIgnoreCase);
                if (found) break;
            }
            if (!found) return false;
        }
        return true;
    }

    private static readonly char[] NotWord =
        [' ', '·', '—', '/', '\\', '-', '_', ':', '(', ')', '[', ']', ',', '.', '\'', '"', '♡'];
}
