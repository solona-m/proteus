using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Gui;

/// <summary>Which of the 16 row buttons the picker draws, whether the "Show all rows" tick is worth drawing, and where
/// the selection lands. Pure; <see cref="ColorTableEditor.DrawRows"/> draws from it.</summary>
internal static class RowPickerView
{
    private static readonly IReadOnlyList<int> All = Enumerable.Range(1, 16).ToArray();

    private static List<int> Valid(IReadOnlySet<int>? usedRows)
        => usedRows == null ? [] : usedRows.Where(r => r is >= 1 and <= 16).Distinct().Order().ToList();

    /// <summary>The rows to draw, ascending: the used ones, or all 16 under Show all or when usage is unknown or empty.</summary>
    public static IReadOnlyList<int> Visible(IReadOnlySet<int>? usedRows, bool showAll)
    {
        if (showAll) return All;
        var v = Valid(usedRows);
        return v.Count == 0 ? All : v;
    }

    /// <summary>True when the tick would change something: some rows used and some not.</summary>
    public static bool ToggleRelevant(IReadOnlySet<int>? usedRows)
    {
        int n = Valid(usedRows).Count;
        return n is > 0 and < 16;
    }

    /// <summary>
    /// The selection to use this frame. Nothing chosen yet (0 or out of range) lands on the first visible row — under
    /// Show all, the first used row, or 1. A hidden selection lands on the first visible row. Under Show all a valid
    /// selection never moves, so a dimmed row can be picked on purpose.
    /// </summary>
    public static int Land(int selected, IReadOnlyList<int> visible, bool showAll, Func<int, bool> inUse)
    {
        if (selected is <= 0 or > 16)
        {
            if (!showAll) return visible.Count > 0 ? visible[0] : 1;
            int firstUsed = All.FirstOrDefault(inUse);
            return firstUsed == 0 ? 1 : firstUsed;
        }
        if (!showAll && visible.Count > 0 && !visible.Contains(selected)) return visible[0];
        return selected;
    }
}
