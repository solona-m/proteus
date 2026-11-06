using System.Collections.Generic;
using System.Linq;
using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Which row buttons the picker draws, when the "Show all rows" tick appears, and where the selection lands.</summary>
public class RowPickerViewTests
{
    private static readonly int[] All = Enumerable.Range(1, 16).ToArray();

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-8")]
    public void Visible_lists_only_used_rows_ascending()
        => Assert.Equal([3, 10, 12], RowPickerView.Visible(new HashSet<int> { 12, 3, 10 }, false));

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-8")]
    public void Show_all_lists_all_sixteen()
        => Assert.Equal(All, RowPickerView.Visible(new HashSet<int> { 3 }, true));

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-21")]
    public void Unknown_or_empty_shows_all_sixteen()
    {
        Assert.Equal(All, RowPickerView.Visible(null, false));
        Assert.Equal(All, RowPickerView.Visible(new HashSet<int>(), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-25")]
    public void Out_of_range_values_are_ignored_and_fall_back()
    {
        Assert.Equal([5], RowPickerView.Visible(new HashSet<int> { 0, 17, 5 }, false));
        Assert.Equal(All, RowPickerView.Visible(new HashSet<int> { 0, 17 }, false));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-9")]
    [Trait("Plan", "EDGE-row-hover-highlight-22")]
    public void Toggle_is_relevant_only_when_something_would_be_hidden()
    {
        Assert.False(RowPickerView.ToggleRelevant(null));
        Assert.False(RowPickerView.ToggleRelevant(new HashSet<int>()));
        Assert.False(RowPickerView.ToggleRelevant(new HashSet<int>(All)));
        Assert.True(RowPickerView.ToggleRelevant(new HashSet<int> { 16 }));
        Assert.True(RowPickerView.ToggleRelevant(new HashSet<int> { 3, 10 }));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-23")]
    public void Only_the_fallback_row_with_the_toggle()
    {
        var used = new HashSet<int> { GlowShell.Row };
        Assert.Equal([16], RowPickerView.Visible(used, false));
        Assert.True(RowPickerView.ToggleRelevant(used));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-26")]
    public void Unset_selection_lands_on_the_first_visible_row()
    {
        var used = new HashSet<int> { 3, 10 };
        Assert.Equal(3, RowPickerView.Land(0, [3, 10], false, used.Contains));
        Assert.Equal(3, RowPickerView.Land(0, All, true, used.Contains));
        Assert.Equal(1, RowPickerView.Land(0, All, true, _ => false));   // nothing in use: row 1, as before
        Assert.Equal(3, RowPickerView.Land(17, [3, 10], false, used.Contains));
    }

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-10")]
    [Trait("Plan", "EDGE-row-hover-highlight-27")]
    [Trait("Plan", "EDGE-row-hover-highlight-28")]
    public void Hidden_selection_lands_on_the_first_visible_row()
        => Assert.Equal(3, RowPickerView.Land(5, [3, 10], false, new HashSet<int> { 3, 10 }.Contains));

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-10")]
    [Trait("Plan", "EDGE-row-hover-highlight-27")]
    public void Show_all_never_moves_a_deliberate_selection()
        => Assert.Equal(5, RowPickerView.Land(5, All, true, new HashSet<int> { 3, 10 }.Contains));

    [Fact]
    [Trait("Plan", "AC-row-hover-highlight-10")]
    public void A_visible_selection_is_kept()
        => Assert.Equal(10, RowPickerView.Land(10, [3, 10], false, new HashSet<int> { 3, 10 }.Contains));

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-24")]
    public void An_approximate_compressed_scan_hides_like_any_scan_and_Show_all_reveals_the_miss()
    {
        // An imported pack's BC-compressed index is read approximately: say it really uses 2, 5 and 9, but the snapped
        // read reported only 2 and 9. The picker trusts the read like any scan; Show all is the way to the missed row.
        var approximate = new HashSet<int> { 2, 9 };
        Assert.Equal([2, 9], RowPickerView.Visible(approximate, false));
        Assert.True(RowPickerView.ToggleRelevant(approximate));
        Assert.Contains(5, RowPickerView.Visible(approximate, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-hover-highlight-39")]
    public void A_row_missing_from_the_scan_is_hidden_until_Show_all()
    {
        // A small detail row under ReadOverlay's coverage threshold never reaches usedRows (ContentPieceSelectionTests
        // pins that drop); the picker then hides it, and Show all brings it back.
        var used = new HashSet<int> { 3 };
        Assert.DoesNotContain(7, RowPickerView.Visible(used, false));
        Assert.Contains(7, RowPickerView.Visible(used, true));
    }
}
