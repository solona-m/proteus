using System;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>When the live brush's preview is shared with sync pairs, and when locked parts come back so it can be.</summary>
public class LivePreviewPolicyTests
{
    private static readonly DateTime Saved = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static SavedEdit Edit() => new("Old Thing", "chara/top.mdl", @"C:\mods\Old Thing\chara\top.mdl", 1234, Saved);

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-1")]
    public void A_settled_edit_whose_save_is_on_disk_is_shared()
        => Assert.True(LivePreviewPolicy.ShouldShare(editing: false, savePending: false, hidesParts: false, savedMatches: true));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-2")]
    [Trait("Plan", "EDGE-brush-pair-sync-1")]
    public void Nothing_is_shared_mid_stroke_drag_or_grab()
        => Assert.False(LivePreviewPolicy.ShouldShare(editing: true, savePending: false, hidesParts: false, savedMatches: true));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-2")]
    [Trait("Plan", "EDGE-brush-pair-sync-2")]
    public void Nothing_is_shared_while_the_save_is_still_pending()
        => Assert.False(LivePreviewPolicy.ShouldShare(editing: false, savePending: true, hidesParts: false, savedMatches: true));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-3")]
    public void The_state_right_after_an_immediate_save_is_shared()
    {
        // A Move release, Undo or Start over saves at once: nothing open, nothing pending, the record just written.
        var save = Edit();
        bool matches = save.Matches("Old Thing", "chara/top.mdl", 1234, Saved);
        Assert.True(LivePreviewPolicy.ShouldShare(false, false, false, matches));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-4")]
    public void A_preview_hiding_locked_parts_is_never_shared()
        => Assert.False(LivePreviewPolicy.ShouldShare(editing: false, savePending: false, hidesParts: true, savedMatches: true));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-17")]
    public void Revealed_parts_hide_again_when_the_brush_comes_back_onto_the_garment()
        => Assert.True(LivePreviewPolicy.ShouldHideAgain(editing: false, hovering: true));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-18")]
    public void Revealed_parts_hide_again_when_an_edit_starts()
        => Assert.True(LivePreviewPolicy.ShouldHideAgain(editing: true, hovering: false));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-7")]
    public void Locked_parts_come_back_after_the_idle_time_and_not_before()
    {
        Assert.True(LivePreviewPolicy.ShouldRevealLocked(true, false, false, false, true, 3000));
        Assert.False(LivePreviewPolicy.ShouldRevealLocked(true, false, false, false, true, 2999));
        Assert.True(LivePreviewPolicy.LockRevealIdleMs > 1500, "must outlast the autosave");
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-7")]
    [Trait("Plan", "EDGE-brush-pair-sync-5")]
    public void Once_the_hiding_ends_the_saved_model_is_shared()
        => Assert.True(LivePreviewPolicy.ShouldShare(editing: false, savePending: false, hidesParts: false, savedMatches: true));

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-19")]
    public void Changing_the_locks_alone_does_not_end_a_reveal()
        => Assert.False(LivePreviewPolicy.ShouldHideAgain(editing: false, hovering: false));

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-20")]
    public void No_reveal_while_a_save_is_pending_or_the_file_is_not_the_saved_one()
    {
        Assert.False(LivePreviewPolicy.ShouldRevealLocked(true, false, false, savePending: true, savedMatches: true, 10_000));
        Assert.False(LivePreviewPolicy.ShouldRevealLocked(true, false, false, savePending: false, savedMatches: false, 10_000));
        Assert.False(LivePreviewPolicy.ShouldRevealLocked(true, editing: true, false, false, true, 10_000));
        Assert.False(LivePreviewPolicy.ShouldRevealLocked(true, false, hovering: true, false, true, 10_000));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-21")]
    public void Tools_that_hide_nothing_never_reveal()
        => Assert.False(LivePreviewPolicy.ShouldRevealLocked(locksWouldHide: false, false, false, false, true, 10_000));

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-3")]
    [Trait("Plan", "EDGE-brush-pair-sync-24")]
    [Trait("Plan", "EDGE-brush-pair-sync-25")]
    public void A_save_record_matches_only_its_own_mod_and_model()
    {
        var save = Edit();
        Assert.True(save.Matches("old thing", @"chara\top.mdl", 1234, Saved));
        Assert.False(save.Matches("Other Mod", "chara/top.mdl", 1234, Saved));
        Assert.False(save.Matches("Old Thing", "chara/dwn.mdl", 1234, Saved));
        Assert.False(save.Matches(null, null, 1234, Saved));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-3")]
    [Trait("Plan", "EDGE-brush-pair-sync-24")]
    public void With_no_save_record_nothing_is_shared()
    {
        SavedEdit? none = null;
        bool matches = none?.Matches("Old Thing", "chara/top.mdl", 1234, Saved) == true;
        Assert.False(LivePreviewPolicy.ShouldShare(false, false, false, matches));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-26")]
    public void A_file_changed_on_disk_since_the_save_no_longer_matches()
    {
        var save = Edit();
        Assert.False(save.Matches("Old Thing", "chara/top.mdl", 1235, Saved));
        Assert.False(save.Matches("Old Thing", "chara/top.mdl", 1234, Saved.AddSeconds(1)));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-8")]
    [Trait("Plan", "EDGE-brush-pair-sync-16")]
    public void Game_gear_or_an_unreadable_file_stays_private()
        => Assert.False(LivePreviewPolicy.ShouldShare(editing: false, savePending: false, hidesParts: false, savedMatches: false));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-8")]
    [Trait("Plan", "EDGE-brush-pair-sync-22")]
    [Trait("Plan", "EDGE-brush-pair-sync-23")]
    public void An_owed_share_is_retried_only_once_what_it_waited_on_changes()
    {
        Assert.False(LivePreviewPolicy.OwedShareDue(owed: true, parkedOn: "a|no-meta", current: "a|no-meta"));
        Assert.True(LivePreviewPolicy.OwedShareDue(owed: true, parkedOn: "a|no-meta", current: "a|meta"));
        Assert.True(LivePreviewPolicy.OwedShareDue(owed: true, parkedOn: "a|meta", current: "b|meta"));
        Assert.False(LivePreviewPolicy.OwedShareDue(owed: false, parkedOn: "a|no-meta", current: "a|meta"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-3")]
    public void The_stamp_names_one_exact_save()
    {
        var a = Edit();
        Assert.Equal(a.Stamp, Edit().Stamp);
        Assert.NotEqual(a.Stamp, (a with { WriteUtc = Saved.AddTicks(1) }).Stamp);
    }
}
