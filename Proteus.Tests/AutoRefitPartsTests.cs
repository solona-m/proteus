using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.AutoRefitTargetsTests;

namespace Proteus.Tests;

/// <summary>
/// Settings > Body refit with a body per part: the rows the panel draws (<see cref="AutoRefitPanel.Rows"/>), what picking
/// a body does (<see cref="AutoRefitDecisions.ChooseBody"/>) and how the sizes waiting for what is worn are filled
/// (<see cref="AutoRefitDecisions.ApplySnapshot"/>). The panel itself is ImGui, which does not run headless: these are
/// the decisions it is a view of.
/// </summary>
public class AutoRefitPartsTests
{
    private static readonly IReadOnlyList<BodyModIndex.Entry> All = [Bibo, Yab, Nails, Rue, LaRue];

    private static AutoRefitTargets At(AutoRefitPreference pref, string? race = "0201",
                                       Dictionary<string, BodyOption?>? worn = null)
        => new(pref, Find, race, worn);

    private static Dictionary<string, string> ReadIn(AutoRefitPreference pref)
        => AutoRefitDecisions.Slots.ToDictionary(s => s, pref.BodyOf, StringComparer.Ordinal);

    // ── the rows ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-1")]
    [Trait("Plan", "EDGE-per-part-target-body-15")]
    public void Rows_are_chest_legs_hands_feet_each_with_its_own_body_and_size()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0201", canReadWorn: false);
        AutoRefitDecisions.ChooseBody(pref, "_dwn", Yab, "0201", canReadWorn: false);
        var rows = AutoRefitPanel.Rows(pref, Find, "0201");
        Assert.Equal(["_top", "_dwn", "_glv", "_sho"], rows.Select(r => r.Slot));
        Assert.Equal(["Bibo+", "YAB+", "Bibo+", "Bibo+"], rows.Select(r => r.BodyDir));
        Assert.Equal(Yab.Catalog.For("_dwn", "0201"), rows[1].Options);
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-1")]
    public void A_part_lists_only_bodies_that_have_it()
    {
        Assert.Contains(Nails, AutoRefitPanel.BodiesFor("_glv", All));
        Assert.DoesNotContain(Nails, AutoRefitPanel.BodiesFor("_top", All));
        Assert.DoesNotContain(LaRue, AutoRefitPanel.BodiesFor("_sho", All));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-1")]
    public void Changing_one_row_leaves_the_others()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0201", canReadWorn: false);
        var before = JsonConvert.SerializeObject(pref);
        AutoRefitDecisions.ChooseBody(pref, "_glv", Yab, "0201", canReadWorn: false);
        Assert.Equal("YAB+", pref.BodyOf("_glv"));
        Assert.Equal(["Bibo+", "Bibo+", "Bibo+"], new[] { "_top", "_dwn", "_sho" }.Select(pref.BodyOf));
        Assert.Equal("M", pref.Chest!.Name);
        Assert.Equal("M", pref.Legs!.Name);
        pref.SetSize("_dwn", Ref(Bibo, "_dwn", "L"));
        Assert.Equal("M", pref.Chest!.Name);
        Assert.NotEqual(before, JsonConvert.SerializeObject(pref));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-1")]
    public void Run_now_needs_any_part_with_a_body()
    {
        Assert.False(AutoRefitPanel.CanRunNow(true, null, false));
        Assert.False(AutoRefitPanel.CanRunNow(true, new AutoRefitPreference(), false));
        var handsOnly = new AutoRefitPreference();
        handsOnly.SetBody("_glv", Nails.Dir);
        Assert.True(AutoRefitPanel.CanRunNow(true, handsOnly, false));
        Assert.False(AutoRefitPanel.CanRunNow(false, handsOnly, false));
        Assert.False(AutoRefitPanel.CanRunNow(true, handsOnly, true));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-1")]
    public void Forget_clears_every_part()
    {
        var config = new Configuration();
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0201", canReadWorn: false);
        config.AutoRefitByCollection["c"] = pref;
        config.AutoRefitByCollection.Remove("c");   // what the button does
        config.AutoRefitByCollection.TryGetValue("c", out var gone);
        Assert.All(AutoRefitPanel.Rows(gone, Find, "0201"), r => Assert.Equal("", r.BodyDir));
        Assert.False(AutoRefitPanel.CanRunNow(true, gone, false));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-9")]
    public void A_missing_body_marks_only_its_row()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0201", canReadWorn: false);
        pref.SetBody("_dwn", "Gone Body");
        var rows = AutoRefitPanel.Rows(pref, Find, "0201");
        Assert.Equal([false, true, false, false], rows.Select(r => r.Missing));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-5")]
    public void A_body_without_the_players_sex_flags_its_row_and_fills_nothing()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0101", canReadWorn: false);   // Bibo+ has no man's sizes here
        Assert.False(pref.HasAnyBody());
        pref.SetBody("_top", Bibo.Dir);
        var rows = AutoRefitPanel.Rows(pref, Find, "0101");
        Assert.True(rows[0].NoSizesForSex);
        Assert.Empty(rows[0].Options);
    }

    // ── the first body ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-2")]
    public void First_body_fills_every_part_with_the_worn_size_and_matched_ends()
    {
        var pref = new AutoRefitPreference();
        var first = AutoRefitDecisions.ChooseBody(pref, "_dwn", Bibo, "0201", canReadWorn: true);
        Assert.Empty(first);
        Assert.All(AutoRefitDecisions.Slots, s => Assert.Equal("Bibo+", pref.BodyOf(s)));
        Assert.Equal(["_dwn", "_top"], pref.PendingFill.Keys.Order(StringComparer.Ordinal));

        var worn = new Dictionary<string, BodyOption?>
        {
            ["_top"] = Bibo.Catalog.For("_top").First(o => o.Name == "L"),
            ["_dwn"] = Bibo.Catalog.For("_dwn").First(o => o.Name == "L"),
        };
        Assert.True(AutoRefitDecisions.ApplySnapshot(pref, At(pref, worn: worn), ReadIn(pref), out var prefilled));
        Assert.Empty(prefilled);
        Assert.Equal("L", pref.Chest!.Name);
        Assert.Equal("L", pref.Legs!.Name);
        Assert.Null(pref.Hands);
        Assert.Null(pref.Feet);
        Assert.Empty(pref.PendingFill);
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-2")]
    public void Without_a_worn_size_chest_and_legs_take_the_first_for_the_race_and_say_so()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0201", canReadWorn: true);
        Assert.True(AutoRefitDecisions.ApplySnapshot(pref, At(pref), ReadIn(pref), out var prefilled));
        Assert.Equal(["_top", "_dwn"], prefilled);
        Assert.Equal("M", pref.Chest!.Name);
        Assert.Equal("M", pref.Legs!.Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-3")]
    public void Non_active_collection_first_pick_uses_first_sizes_and_notes_it()
    {
        var pref = new AutoRefitPreference();
        var first = AutoRefitDecisions.ChooseBody(pref, "_top", Yab, "0201", canReadWorn: false);
        Assert.Equal(["_top", "_dwn"], first);
        Assert.Equal("S", pref.Chest!.Name);
        Assert.Equal("S", pref.Legs!.Name);
        Assert.Empty(pref.PendingFill);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-4")]
    [Trait("Plan", "AC-auto-refit-part-bodies-2")]
    public void Unknown_race_defers_sizes_until_known()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, race: null, canReadWorn: false);
        Assert.All(AutoRefitDecisions.Slots, s => Assert.Equal("Bibo+", pref.BodyOf(s)));
        Assert.Null(pref.Chest);
        Assert.Equal(2, pref.PendingFill.Count);
        Assert.False(AutoRefitDecisions.ApplySnapshot(pref, At(pref, race: null), null, out _));
        Assert.True(AutoRefitDecisions.ApplySnapshot(pref, At(pref), null, out var prefilled));
        Assert.Equal(2, prefilled.Count);
        Assert.Equal("M", pref.Chest!.Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-2")]
    public void First_pick_of_a_hands_only_body_fills_hands_only()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_glv", Nails, "0201", canReadWorn: false);
        Assert.Equal(["", "", "Nails", ""], AutoRefitDecisions.Slots.Select(pref.BodyOf));
        Assert.Null(pref.Hands);
        Assert.Empty(pref.PendingFill);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-28")]
    public void Json_keeps_the_BodyDir_key_set_to_the_chest_body()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_dwn", Bibo, "0201", canReadWorn: false);
        AutoRefitDecisions.ChooseBody(pref, "_dwn", Yab, "0201", canReadWorn: false);
        Assert.Contains("\"BodyDir\":\"Bibo+\"", JsonConvert.SerializeObject(pref));
    }

    // ── a later body ─────────────────────────────────────────────────────────

    private static AutoRefitPreference SetUp()
    {
        var pref = new AutoRefitPreference();
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0201", canReadWorn: false);
        return pref;
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-3")]
    public void A_later_body_change_takes_the_worn_size_in_the_new_body()
    {
        var pref = SetUp();
        AutoRefitDecisions.ChooseBody(pref, "_dwn", Yab, "0201", canReadWorn: true);
        Assert.Null(pref.Legs);
        Assert.False(pref.PendingFill["_dwn"]);
        var worn = new Dictionary<string, BodyOption?> { ["_dwn"] = Yab.Catalog.For("_dwn").First(o => o.Name == "M") };
        Assert.True(AutoRefitDecisions.ApplySnapshot(pref, At(pref, worn: worn), ReadIn(pref), out var prefilled));
        Assert.Empty(prefilled);
        Assert.Equal("M", pref.Legs!.Name);
        Assert.Equal("M", pref.Chest!.Name);   // the chest kept its own
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-3")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-8")]
    public void Body_change_without_worn_leaves_chest_unchosen_and_hands_matched()
    {
        var pref = SetUp();
        AutoRefitDecisions.ChooseBody(pref, "_top", Yab, "0201", canReadWorn: true);
        Assert.True(AutoRefitDecisions.ApplySnapshot(pref, At(pref), ReadIn(pref), out var prefilled));
        Assert.Empty(prefilled);
        Assert.Null(pref.Chest);                // unchosen: its gear waits
        Assert.Empty(pref.PendingFill);
        Assert.Null(At(pref).TargetFor("_top", "_top"));

        pref.SetSize("_glv", Ref(Bibo, "_glv", "M"));
        AutoRefitDecisions.ChooseBody(pref, "_glv", Yab, "0201", canReadWorn: true);
        Assert.Null(pref.Hands);                // matched again
        Assert.DoesNotContain("_glv", pref.PendingFill.Keys);
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-3")]
    public void A_hands_body_change_stays_matched()
    {
        var pref = SetUp();
        AutoRefitDecisions.ChooseBody(pref, "_glv", Yab, "0201", canReadWorn: true);
        Assert.Null(pref.Hands);
        var row = AutoRefitPanel.Rows(pref, Find, "0201")[2];
        Assert.True(row.Matched);
        Assert.Equal("M", row.Shown!.Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-6")]
    public void Repicking_the_same_body_keeps_sizes()
    {
        var pref = SetUp();
        pref.SetSize("_top", Ref(Bibo, "_top", "L"));
        AutoRefitDecisions.ChooseBody(pref, "_top", Bibo, "0201", canReadWorn: true);
        Assert.Equal("L", pref.Chest!.Name);
        Assert.Empty(pref.PendingFill);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-7")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-1")]
    public void First_edit_of_a_legacy_pref_expands_then_changes_one_part()
    {
        var pref = new AutoRefitPreference { LegacyBodyDir = Bibo.Dir, Chest = Ref(Bibo, "_top", "L") };
        // [EDGE-1] A body but no legs size: shown, not filled.
        var rows = AutoRefitPanel.Rows(pref, Find, "0201");
        Assert.Equal("Bibo+", rows[1].BodyDir);
        Assert.Null(rows[1].Shown);
        Assert.Empty(pref.PendingFill);

        AutoRefitDecisions.ChooseBody(pref, "_dwn", Yab, "0201", canReadWorn: false);
        Assert.Equal(["Bibo+", "YAB+", "Bibo+", "Bibo+"], AutoRefitDecisions.Slots.Select(pref.BodyOf));
        Assert.Equal("L", pref.Chest!.Name);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-9")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-30")]
    public void A_stale_worn_snapshot_changes_nothing()
    {
        var pref = SetUp();
        AutoRefitDecisions.ChooseBody(pref, "_dwn", Yab, "0201", canReadWorn: true);
        var readIn = ReadIn(pref);
        pref.SetBody("_dwn", Bibo.Dir);   // changed again before the reading landed
        var worn = new Dictionary<string, BodyOption?> { ["_dwn"] = Yab.Catalog.For("_dwn")[0] };
        Assert.False(AutoRefitDecisions.ApplySnapshot(pref, At(pref, worn: worn), readIn, out _));
        Assert.Null(pref.Legs);
        // A size picked meanwhile ends the wait.
        pref.SetSize("_dwn", Ref(Bibo, "_dwn", "L"));
        Assert.Empty(pref.PendingFill);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-9")]
    public void ApplySnapshot_with_nothing_pending_changes_nothing()
    {
        var pref = SetUp();
        string before = JsonConvert.SerializeObject(pref);
        Assert.False(AutoRefitDecisions.ApplySnapshot(pref, At(pref), ReadIn(pref), out var prefilled));
        Assert.Empty(prefilled);
        Assert.Equal(before, JsonConvert.SerializeObject(pref));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-30")]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-4")]
    public void WornFrom_matches_from_resolved_paths_alone()
    {
        var l = Bibo.Catalog.For("_top").First(o => o.Name == "L");
        var resolved = new Dictionary<string, string?> { [PathOf("_top")] = Bibo.Catalog.PathOf(l) };
        Assert.Equal(l, AutoRefitTargets.WornFrom(Bibo.Catalog, "_top", "0201", resolved));
        Assert.Null(AutoRefitTargets.WornFrom(Bibo.Catalog, "_dwn", "0201", resolved));
        Assert.Equal([PathOf("_top")], AutoRefitTargets.GamePathsOf(Bibo.Catalog, "_top", "0201"));
    }

    // ── matched hands and feet ───────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-4")]
    public void Matched_label_is_the_size_the_refit_uses()
    {
        var pref = SetUp();
        AutoRefitDecisions.ChooseBody(pref, "_glv", Yab, "0201", canReadWorn: false);
        var row = AutoRefitPanel.Rows(pref, Find, "0201")[2];
        Assert.Equal(At(pref).TargetFor("_glv", "_glv"), row.Shown);
        Assert.Equal(string.Format(Localization.Strings.AutoRefit.MatchedFmt, "M"), AutoRefitPanel.SizeLabel(row));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-refit-part-bodies-36")]
    public void Matched_label_on_another_collection_says_worn_else_first()
    {
        var pref = SetUp();
        pref.SetSize("_top", Ref(Bibo, "_top", "L"));
        AutoRefitDecisions.ChooseBody(pref, "_glv", Yab, "0201", canReadWorn: false);
        var row = AutoRefitPanel.Rows(pref, Find, "0201")[2];
        Assert.NotEqual(AutoRefitTargets.Basis.Anchor, row.Basis);
        Assert.Equal(string.Format(Localization.Strings.AutoRefit.MatchedWornFmt, "Long"), AutoRefitPanel.SizeLabel(row));
    }

    [Fact]
    [Trait("Plan", "AC-auto-refit-part-bodies-4")]
    public void Match_entry_returns_hands_to_matched()
    {
        var pref = SetUp();
        pref.SetSize("_glv", Ref(Bibo, "_glv", "M"));
        Assert.False(AutoRefitPanel.Rows(pref, Find, "0201")[2].Matched);
        pref.SetSize("_glv", null);   // the "Match" entry
        Assert.True(AutoRefitPanel.Rows(pref, Find, "0201")[2].Matched);
    }

    // ── Rue goes only with Rue ───────────────────────────────────────────────

    /// <summary>
    /// Replaces "picking Rue beside another body switches every part": a reporter's chest and legs were forced onto one
    /// body that way. Each part keeps its own pick now; the refit leaves a part across the line out (see
    /// AutoRefitTargetsTests), and Settings says so (RueSides).
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-per-part-target-body-5")]
    public void Picking_rue_for_one_part_changes_only_that_part()
    {
        var pref = SetUp();
        pref.SetSize("_top", Ref(Bibo, "_top", "L"));
        var first = AutoRefitDecisions.ChooseBody(pref, "_dwn", Rue, "0201", canReadWorn: false);
        Assert.Empty(first);
        Assert.Equal([Bibo.Dir, Rue.Dir, Bibo.Dir, Bibo.Dir], AutoRefitDecisions.Slots.Select(pref.BodyOf));
        Assert.Equal("L", pref.Chest!.Name);   // Bibo+'s L kept

        // Another body for the hands, across the line from the legs: only the hands change.
        AutoRefitDecisions.ChooseBody(pref, "_glv", Yab, "0201", canReadWorn: false);
        Assert.Equal([Bibo.Dir, Rue.Dir, Yab.Dir, Bibo.Dir], AutoRefitDecisions.Slots.Select(pref.BodyOf));
        Assert.Equal("L", pref.Chest!.Name);
    }
}
