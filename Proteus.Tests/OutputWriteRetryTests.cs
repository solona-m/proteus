using Proteus.Services;
using Xunit;
using static Proteus.Services.OutputWriteRetry;

namespace Proteus.Tests;

/// <summary>The retry latch for output writes that could not be put in place (#91), and when its timer fires.</summary>
public class OutputWriteRetryTests
{
    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-22")]
    public void Zero_failures_is_clean()
    {
        // [EDGE-issue-sweep-d-22] No output write failed (a missing pack source never counts): clean, nothing scheduled.
        var latch = new OutputWriteRetry();
        var d = latch.OnPublished("fp", 0, 0, 0, willSchedule: true);
        Assert.Equal(Kind.Clean, d.Kind);
        Assert.Equal(0, latch.ScheduledInLast60s(0));
        Assert.Equal(("fp", false, true), Record("fp", d, skinFailed: false));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-5")]
    public void Shell_only_failure_keeps_skin_and_blocks_skip()
    {
        // [AC-issue-sweep-d-5] A dropped shell layer: the fingerprint stays (skin reuse), but the gate cannot skip.
        var d = new OutputWriteRetry().OnPublished("fp", 1, 0, 0, willSchedule: true);
        Assert.Equal(Kind.Retry, d.Kind);
        var (fp, incomplete, keepSkin) = Record("fp", d, skinFailed: false);
        Assert.Equal(("fp", true, true), (fp, incomplete, keepSkin));
        Assert.False(CompositorService.SkipUnchanged(false, false, incomplete, fp, "fp", () => false));
        Assert.True(CompositorService.SkipUnchanged(false, false, false, fp, "fp", () => false));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-25")]
    public void Clean_and_giveup_clear_outputIncomplete()
    {
        // [EDGE-issue-sweep-d-25]
        Assert.False(Record("fp", new Decision(Kind.Clean, 0, 0, false), false).OutputIncomplete);
        Assert.False(Record("fp", new Decision(Kind.GiveUp, 0, 4, true), false).OutputIncomplete);
        Assert.Equal("fp", Record("fp", new Decision(Kind.GiveUp, 0, 4, true), false).Fingerprint);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-8")]
    public void Skin_failure_nulls_fingerprint_and_drops_skin()
    {
        // [EDGE-issue-sweep-d-8] A skin texture that never landed: no skip and no skin reuse.
        var d = new OutputWriteRetry().OnPublished("fp", 0, 1, 0, willSchedule: true);
        Assert.Equal((null, false, false), Record("fp", d, skinFailed: true));
        // A give-up records the inputs (no loop) but still does not reuse a skin publish that never landed.
        Assert.Equal(("fp", false, false), Record("fp", new Decision(Kind.GiveUp, 0, 4, false), skinFailed: true));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-6")]
    public void Three_retries_then_give_up_once_per_fingerprint()
    {
        // [AC-issue-sweep-d-6] 1.5 s, 4 s, 10 s, then give up — said out loud once.
        var latch = new OutputWriteRetry();
        long t = 0;
        var a = latch.OnPublished("fp", 1, 0, t, true);
        var b = latch.OnPublished("fp", 1, 0, t += 1600, true);
        var c = latch.OnPublished("fp", 1, 0, t += 4100, true);
        Assert.Equal((Kind.Retry, 1500, 1), (a.Kind, a.DelayMs, a.Attempt));
        Assert.Equal((Kind.Retry, 4000, 2), (b.Kind, b.DelayMs, b.Attempt));
        Assert.Equal((Kind.Retry, 10000, 3), (c.Kind, c.DelayMs, c.Attempt));
        var g1 = latch.OnPublished("fp", 1, 0, t += 70_000, true);
        var g2 = latch.OnPublished("fp", 1, 0, t += 70_000, true);
        Assert.Equal((Kind.GiveUp, true), (g1.Kind, g1.Warn));
        Assert.Equal((Kind.GiveUp, false), (g2.Kind, g2.Warn));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-23")]
    public void Alternating_fingerprints_hit_the_global_cap()
    {
        // [EDGE-issue-sweep-d-23] A, B, A, B within a minute: three timers, then give up; a minute later, retries again.
        var latch = new OutputWriteRetry();
        Assert.Equal(Kind.Retry, latch.OnPublished("A", 1, 0, 0, true).Kind);
        Assert.Equal(Kind.Retry, latch.OnPublished("B", 1, 0, 100, true).Kind);
        Assert.Equal(Kind.Retry, latch.OnPublished("A", 1, 0, 200, true).Kind);
        Assert.Equal(Kind.GiveUp, latch.OnPublished("B", 1, 0, 300, true).Kind);
        Assert.Equal(3, latch.ScheduledInLast60s(300));
        Assert.Equal(Kind.Retry, latch.OnPublished("A", 1, 0, 60_300, true).Kind);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-44")]
    public void Unscheduled_retries_take_no_global_slot()
    {
        // [EDGE-issue-sweep-d-44] With auto redraw off no timer is scheduled, so no slot is used up.
        var latch = new OutputWriteRetry();
        latch.OnPublished("A", 1, 0, 0, willSchedule: false);
        latch.OnPublished("B", 1, 0, 10, willSchedule: false);
        latch.OnPublished("C", 1, 0, 20, willSchedule: false);
        Assert.Equal(0, latch.ScheduledInLast60s(30));
        Assert.Equal(Kind.Retry, latch.OnPublished("D", 1, 0, 40, willSchedule: true).Kind);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-9")]
    public void A_new_fingerprint_gets_a_fresh_per_fingerprint_budget()
    {
        // [EDGE-issue-sweep-d-9]
        var latch = new OutputWriteRetry();
        latch.OnPublished("A", 1, 0, 0, false);
        latch.OnPublished("A", 1, 0, 0, false);
        latch.OnPublished("A", 1, 0, 0, false);
        Assert.Equal(Kind.GiveUp, latch.OnPublished("A", 1, 0, 0, false).Kind);
        var d = latch.OnPublished("B", 1, 0, 0, false);
        Assert.Equal((Kind.Retry, 1), (d.Kind, d.Attempt));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-11")]
    public void Every_failing_publish_counts_whatever_triggered_it()
    {
        // [EDGE-issue-sweep-d-11] Our own redraw's echo composites count the same as the retry timer's: the caps end it.
        var latch = new OutputWriteRetry();
        for (int i = 0; i < 3; i++) Assert.Equal(Kind.Retry, latch.OnPublished("fp", 2, 0, i * 10_000L, true).Kind);
        Assert.Equal(Kind.GiveUp, latch.OnPublished("fp", 2, 0, 90_000, true).Kind);
        // A clean publish resets it.
        Assert.Equal(Kind.Clean, latch.OnPublished("fp", 0, 0, 91_000, true).Kind);
        Assert.Equal(Kind.Retry, latch.OnPublished("fp", 1, 0, 200_000, true).Kind);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-24")]
    public void Fire_waits_while_pending_drops_after_a_publish_triggers_otherwise()
    {
        // [EDGE-issue-sweep-d-24]
        Assert.Equal(Fire.Step.Wait, Fire.Decide(pending: true, held: false, 5, 5, true, false, 0));
        Assert.Equal(Fire.Step.Drop, Fire.Decide(pending: false, held: false, 6, 5, true, false, 0));
        Assert.Equal(Fire.Step.Drop, Fire.Decide(pending: true, held: false, 6, 5, true, false, 0));
        Assert.Equal(Fire.Step.Trigger, Fire.Decide(pending: false, held: false, 5, 5, true, false, 0));
        Assert.Equal(Fire.Step.Drop, Fire.Decide(pending: false, held: false, 5, 5, true, disposed: true, 0));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-39")]
    public void Fire_waits_while_held_and_drops_after_30s()
    {
        // [EDGE-issue-sweep-d-39] An auto refit batch holds redraws: wait, and give the batch up to 30 s.
        Assert.Equal(Fire.Step.Wait, Fire.Decide(false, held: true, 1, 1, true, false, 29_750));
        Assert.Equal(Fire.Step.Drop, Fire.Decide(false, held: true, 1, 1, true, false, 30_000));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-12")]
    public void Fire_drops_with_auto_redraw_off()
    {
        // [EDGE-issue-sweep-d-12]
        Assert.Equal(Fire.Step.Drop, Fire.Decide(false, false, 1, 1, autoRedraw: false, false, 0));
    }
}
