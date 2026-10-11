using Proteus.Services;
using Xunit;
using Step = Proteus.Services.LivePreviewShareState.RevealStep;

namespace Proteus.Tests;

/// <summary>The Studio's share and lock-reveal bookkeeping, frame by frame.</summary>
public class LivePreviewShareStateTests
{
    private const int Locks = 0x51;   // any non-zero "what the locks hide"

    private static Step Frame(LivePreviewShareState s, long now, bool editing = false, bool hovering = false,
                              bool pending = false, int locks = Locks, bool hiding = true, bool matches = true)
        => s.Tick(now, editing, hovering, pending, locks, hiding, () => matches);

    /// <summary>Settles the state at t=0 with the brush on the garment, so idle time counts from there.</summary>
    private static LivePreviewShareState Hovered()
    {
        var s = new LivePreviewShareState();
        Frame(s, 0, hovering: true);
        return s;
    }

    // ── owed shares ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-8")]
    [Trait("Plan", "EDGE-brush-pair-sync-22")]
    public void A_share_that_went_private_is_retried_once_the_managed_mod_appears()
    {
        var s = new LivePreviewShareState();
        s.SharePushed("save1", "save1|False");

        Assert.False(s.OwedShareDue(10_000, sharedStamp: null, () => "save1|False"));   // nothing changed
        Assert.True(s.OwedShareDue(20_000, sharedStamp: null, () => "save1|True"));     // the mod is there now
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-7")]
    [Trait("Plan", "EDGE-brush-pair-sync-23")]
    public void A_share_that_keeps_failing_is_not_retried_on_the_same_answer()
    {
        var s = new LivePreviewShareState();
        s.SharePushed("save1", "save1|True");
        for (long t = 0; t < 30_000; t += 16)
            Assert.False(s.OwedShareDue(t, sharedStamp: null, () => "save1|True"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-1")]
    public void A_share_that_landed_owes_nothing()
    {
        var s = new LivePreviewShareState();
        s.SharePushed("save1", "save1|True");
        Assert.False(s.OwedShareDue(10_000, sharedStamp: "save1", () => "anything else"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-16")]
    public void Once_something_else_is_shown_no_share_is_owed_however_the_save_changes()
    {
        // A share landed, then the character was redrawn instead (reload in place failed) or a refit preview went up:
        // a later save must not bring back a share over it, once a second.
        var s = new LivePreviewShareState();
        s.SharePushed("save1", "save1|True");
        s.NotSharing();

        Assert.False(s.OwedShareDue(10_000, sharedStamp: null, () => "save2|True"));
        Assert.False(s.OwedShareDue(20_000, sharedStamp: null, () => "|True"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-8")]
    public void Owed_shares_are_looked_at_no_more_than_once_a_second()
    {
        var s = new LivePreviewShareState();
        s.SharePushed("save1", "save1|False");
        int asked = 0;
        for (long t = 0; t < 2_000; t += 16)
            s.OwedShareDue(t, null, () => { asked++; return "save1|False"; });
        Assert.InRange(asked, 1, 2);
    }

    // ── lock reveal ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-7")]
    public void Locked_parts_come_back_after_three_seconds_off_the_garment()
    {
        var s = Hovered();
        Assert.Equal(Step.None, Frame(s, 2_999));
        Assert.Equal(Step.Reveal, Frame(s, 3_000));
        Assert.True(s.Revealed);
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-17")]
    public void Coming_back_onto_the_garment_hides_them_again()
    {
        var s = Hovered();
        Frame(s, 3_000);
        Assert.Equal(Step.Hide, Frame(s, 3_100, hovering: true));
        Assert.False(s.Revealed);
        // And the idle clock starts again from there.
        Assert.Equal(Step.None, Frame(s, 6_000));
        Assert.Equal(Step.Reveal, Frame(s, 6_100));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-18")]
    public void Starting_an_edit_hides_them_again()
    {
        var s = Hovered();
        Frame(s, 3_000);
        Assert.Equal(Step.Hide, Frame(s, 3_100, editing: true));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-7")]
    [Trait("Plan", "EDGE-brush-pair-sync-5")]
    public void Hiding_ended_by_a_reveal_leaves_every_part_drawn_so_the_save_is_shared()
    {
        var s = Hovered();
        Frame(s, 3_000);
        // The panel's WantedHides is 0 while revealed: nothing hidden, so the next push may share.
        Assert.True(s.Revealed);
        Assert.True(LivePreviewPolicy.ShouldShare(false, false, hidesParts: false, savedMatches: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-19")]
    public void Changing_the_locks_while_revealed_keeps_them_revealed()
    {
        var s = Hovered();
        Frame(s, 3_000);
        Assert.Equal(Step.None, Frame(s, 3_100, locks: 0x77));
        Assert.True(s.Revealed);
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    public void Ticking_a_lock_counts_as_activity_so_the_new_hiding_is_not_undone_at_once()
    {
        var s = Hovered();
        // Long idle, then a lock ticked in the side panel: the parts are hidden by it, and stay hidden for a while.
        Assert.Equal(Step.None, Frame(s, 10_000, locks: 0x77, hiding: false));
        Assert.Equal(Step.None, Frame(s, 10_300, locks: 0x77));
        Assert.Equal(Step.None, Frame(s, 12_999, locks: 0x77));
        Assert.Equal(Step.Reveal, Frame(s, 13_000, locks: 0x77));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-20")]
    public void No_reveal_while_a_save_waits_or_the_file_is_not_the_saved_one()
    {
        var s = Hovered();
        Assert.Equal(Step.None, Frame(s, 5_000, matches: false));
        Assert.Equal(Step.None, Frame(s, 6_000, pending: true));
        Assert.False(s.Revealed);
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-21")]
    public void Nothing_to_reveal_when_nothing_is_hidden()
    {
        var s = Hovered();
        Assert.Equal(Step.None, Frame(s, 5_000, locks: 0, hiding: false));
        Assert.Equal(Step.None, Frame(s, 6_000, hiding: false));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-7")]
    public void The_saved_file_is_looked_at_a_few_times_a_second_at_most()
    {
        var s = Hovered();
        int looked = 0;
        for (long t = 3_000; t < 4_000; t += 16)
            s.Tick(t, false, false, false, Locks, true, () => { looked++; return false; });
        Assert.InRange(looked, 1, 4);
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    [Trait("Plan", "EDGE-brush-pair-sync-10")]
    public void Taking_the_preview_down_forgets_the_reveal_and_any_owed_share()
    {
        var s = Hovered();
        Frame(s, 3_000);
        s.SharePushed("save1", "save1|False");
        s.Reset();
        Assert.False(s.Revealed);
        Assert.False(s.OwedShareDue(10_000, null, () => "save1|True"));
    }
}
