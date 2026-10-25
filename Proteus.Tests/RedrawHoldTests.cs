using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;
using Deferred = Proteus.Services.RedrawHold.Deferred;

namespace Proteus.Tests;

/// <summary>
/// The hold an auto refit batch puts on the composites and redraws it causes itself, so an outfit change redraws once
/// at the end of the batch instead of once per slot.
/// </summary>
public class RedrawHoldTests
{
    private long now = 100_000;
    private RedrawHold Hold() => new(() => now);
    private const string Batch = "auto-refit#1";

    private static Deferred Trigger(string reason, bool force = true, bool skinAuth = false)
        => new(reason, force, skinAuth, AutoRedrawExempt: false);

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-1")]
    [Trait("Plan", "EDGE-fewer-redraws-9")]
    public void Triggers_held_by_a_batch_come_back_as_one_merged_release()
    {
        var hold = Hold();
        Assert.True(hold.Hold(Batch, 30_000, 120_000));
        Assert.True(hold.TryDefer(Trigger("ModSettingChanged", force: true, skinAuth: true)));
        Assert.True(hold.TryDefer(Trigger("ModSettingChanged", force: false)));
        Assert.True(hold.TryDefer(Trigger("refit-mod-created", force: true, skinAuth: false)));

        Assert.Null(hold.Poll());
        hold.Release(Batch, 1500);
        now += 1499;
        Assert.Null(hold.Poll());
        now += 1;

        var r = hold.Poll()!;
        Assert.Equal(["ModSettingChanged", "refit-mod-created"], r.Reasons);
        Assert.True(r.AnyDeferred);
        Assert.True(r.Force);
        Assert.False(r.SkinAuth);          // every forced one must be authoritative for the merge to be
        Assert.False(r.Forced);
        Assert.Null(hold.Poll());          // handed out once
        Assert.False(hold.TryDefer(Trigger("after")));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-1")]
    [Trait("Plan", "EDGE-fewer-redraws-8")]
    public void A_held_trigger_latches_its_force_and_never_runs()
    {
        var hold = Hold();
        hold.Hold(Batch, 30_000, 120_000);
        int latched = 0;

        var a = RedrawHold.Admit(force: true, autoRedraw: true, autoRedrawExempt: false, deferWhileHeld: true,
                                 () => latched++, () => hold.TryDefer(Trigger("x")));
        Assert.Equal(RedrawHold.Admission.Deferred, a);
        Assert.Equal(1, latched);

        // Not opted in: runs now, held or not.
        Assert.Equal(RedrawHold.Admission.Run, RedrawHold.Admit(true, true, false, deferWhileHeld: false,
            () => latched++, () => hold.TryDefer(Trigger("y"))));
        // Opted in with nothing held: runs.
        var idle = Hold();
        Assert.Equal(RedrawHold.Admission.Run, RedrawHold.Admit(false, true, false, true,
            () => latched++, () => idle.TryDefer(Trigger("z"))));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-8")]
    [Trait("Plan", "EDGE-fewer-redraws-15")]
    public void With_auto_redraw_off_an_ambient_trigger_stops_before_the_hold()
    {
        var hold = Hold();
        hold.Hold(Batch, 30_000, 120_000);
        bool asked = false;

        Assert.Equal(RedrawHold.Admission.AutoRedrawOff, RedrawHold.Admit(force: false, autoRedraw: false,
            autoRedrawExempt: false, deferWhileHeld: true, () => { }, () => asked = true));
        Assert.False(asked);
        // A forced one is held as with auto redraw on; the owed refit redraw ignores the setting (OwedRedrawDue has no input for it).
        Assert.Equal(RedrawHold.Admission.Deferred, RedrawHold.Admit(true, false, false, true, () => { },
            () => hold.TryDefer(Trigger("forced"))));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-1")]
    [Trait("Plan", "AC-fewer-redraws-2")]
    public void A_model_redraw_absorbed_while_held_is_owed_at_release_and_redrawn_first()
    {
        var hold = Hold();
        Assert.False(hold.TryAbsorbModelRedraw("not held"));

        hold.Hold(Batch, 30_000, 120_000);
        Assert.True(hold.TryAbsorbModelRedraw("auto refit: _top switched on"));
        Assert.True(hold.TryAbsorbModelRedraw("auto refit: _dwn switched on"));
        hold.TryDefer(Trigger("ModSettingChanged"));
        hold.Release(Batch, 0);

        var r = hold.Poll()!;
        Assert.Equal(2, r.ModelRedrawCauses.Count);
        var plan = RedrawHold.PlanRelease(r, disposed: false, canComposite: true);
        Assert.True(plan.RedrawFirst);
        Assert.True(plan.Composite);
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-4")]
    [Trait("Plan", "EDGE-fewer-redraws-7")]
    [Trait("Plan", "EDGE-fewer-redraws-14")]
    public void The_release_plan()
    {
        var hold = Hold();
        hold.Hold(Batch, 30_000, 120_000);
        hold.TryAbsorbModelRedraw("refit");
        hold.TryDefer(Trigger("t"));
        hold.Release(Batch, 0);
        var r = hold.Poll()!;

        Assert.Equal(default, RedrawHold.PlanRelease(r, disposed: true, canComposite: true));
        var cannot = RedrawHold.PlanRelease(r, disposed: false, canComposite: false);
        Assert.True(cannot.RedrawFirst);       // Proteus off or Penumbra gone: the refits are still drawn
        Assert.False(cannot.Composite);

        var none = RedrawHold.PlanRelease(r with { ModelRedrawCauses = [] }, false, true);
        Assert.False(none.RedrawFirst);
        Assert.True(none.Composite);
        Assert.False(RedrawHold.PlanRelease(r with { AnyDeferred = false }, false, true).Composite);
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-2")]
    [Trait("Plan", "EDGE-fewer-redraws-11")]
    [Trait("Plan", "EDGE-fewer-redraws-12")]
    [Trait("Plan", "EDGE-fewer-redraws-13")]
    public void An_owed_redraw_is_asked_again_only_when_nothing_else_will_pay_it_off()
    {
        Assert.True(RedrawHold.OwedRedrawDue(owed: true, held: false, compositeBusy: false, 2000, asks: 1, playerDrawn: true));
        Assert.False(RedrawHold.OwedRedrawDue(false, false, false, 2000, 1, true));   // seen: cleared
        Assert.False(RedrawHold.OwedRedrawDue(true, true, false, 2000, 1, true));     // held again
        Assert.False(RedrawHold.OwedRedrawDue(true, false, true, 2000, 1, true));     // a publish will pay it
        Assert.False(RedrawHold.OwedRedrawDue(true, false, false, 1999, 1, true));    // too soon
        Assert.False(RedrawHold.OwedRedrawDue(true, false, false, 2000, 3, true));    // given up
        Assert.False(RedrawHold.OwedRedrawDue(true, false, false, 2000, 1, false));   // loading screen
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-2")]
    [Trait("Plan", "AC-auto-redraw-off-empty-4")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-6")]
    public void An_owed_redraw_makes_a_publish_a_full_redraw()
    {
        Assert.False(RedrawHold.FullRedrawFor(needFullRedraw: false, useInPlace: true, owed: false));
        Assert.True(RedrawHold.FullRedrawFor(false, true, owed: true));
        Assert.True(RedrawHold.FullRedrawFor(true, true, false));
        Assert.True(RedrawHold.FullRedrawFor(false, useInPlace: false, false));
    }

    [Fact]
    [Trait("Plan", "AC-auto-redraw-off-empty-1")]
    [Trait("Plan", "AC-auto-redraw-off-empty-2")]
    [Trait("Plan", "AC-auto-redraw-off-empty-3")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-7")]
    public void Auto_redraw_gates_a_self_initiated_reload_but_not_a_user_requested_one()
    {
        Assert.False(RedrawHold.AutoRedrawAllows(autoRedraw: false, userRequested: false));
        Assert.True(RedrawHold.AutoRedrawAllows(false, userRequested: true));   // switching Proteus off
        Assert.True(RedrawHold.AutoRedrawAllows(autoRedraw: true, false));
        Assert.True(RedrawHold.AutoRedrawAllows(true, true));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-4")]
    [Trait("Plan", "EDGE-fewer-redraws-1")]
    [Trait("Plan", "EDGE-fewer-redraws-2")]
    public void A_lapsed_lease_or_a_cap_lets_go_forced_and_composites_unheld()
    {
        var hold = Hold();
        hold.Hold(Batch, 30_000, 120_000);
        hold.TryDefer(Trigger("t"));
        now += 30_000;
        var lapsed = hold.Poll()!;
        Assert.True(lapsed.Keys.Single().TimedOut);
        Assert.True(lapsed.Forced);
        Assert.True(RedrawHold.PlanRelease(lapsed, false, true).Composite);

        // The next job may take it again, under the same cap from its first take.
        Assert.True(hold.Hold(Batch, 30_000, 120_000));
        now += 89_999;
        Assert.True(hold.Hold(Batch, 30_000, 120_000));
        now += 1;
        Assert.False(hold.Hold(Batch, 30_000, 120_000));
        var capped = hold.Poll()!;
        Assert.True(capped.Keys.Single().Capped);
        Assert.False(hold.Hold(Batch, 30_000, 120_000));   // refused until the batch lets it go
        hold.Release(Batch, 0);
        Assert.True(hold.Hold(Batch, 30_000, 120_000));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-4")]
    public void A_hold_kept_alive_by_one_batch_after_another_still_ends_at_the_global_cap()
    {
        var hold = Hold();
        hold.Hold("auto-refit#1", 30_000, 120_000);
        for (int i = 2; i < 10; i++)
        {
            now += 20_000;
            hold.Release($"auto-refit#{i - 1}", 1500);   // the next batch takes its key inside the grace
            hold.Hold($"auto-refit#{i}", 30_000, 120_000);
            if (hold.Poll() is { } r) { Assert.True(now - 100_000 >= RedrawHold.GlobalCapMs); Assert.True(r.Forced); return; }
        }
        Assert.Fail("the hold outlived the global cap");
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-4")]
    [Trait("Plan", "EDGE-fewer-redraws-4")]
    [Trait("Plan", "EDGE-fewer-redraws-5")]
    [Trait("Plan", "EDGE-fewer-redraws-6")]
    public void Release_is_per_key_idempotent_and_never_pushes_the_deadline_out()
    {
        var hold = Hold();
        hold.Hold("auto-refit#1", 30_000, 120_000);
        hold.Hold("auto-refit#2", 30_000, 120_000);
        Assert.True(hold.Release("auto-refit#1", 0));
        Assert.True(hold.Release("auto-refit#1", 5000));   // later, longer grace: the deadline stays now
        Assert.False(hold.Release("auto-refit#9", 0));
        Assert.Null(hold.Poll());                          // #2 still holds
        hold.Release("auto-refit#2", 0);
        var r = hold.Poll()!;
        Assert.Equal(2, r.Keys.Count);
        Assert.False(r.Forced);
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-4")]
    public void The_frame_poll_is_due_at_the_nearest_deadline_lease_cap_or_global_cap()
    {
        var hold = Hold();
        Assert.Equal(long.MaxValue, hold.NextDueMs);
        hold.Hold("a", 30_000, 120_000);
        Assert.Equal(now + 30_000, hold.NextDueMs);
        hold.Hold("b", 30_000, 10_000);
        Assert.Equal(now + 10_000, hold.NextDueMs);    // b's cap
        hold.Release("a", 1500);
        Assert.Equal(now + 1500, hold.NextDueMs);
        hold.Release("a", 0);
        hold.Release("b", 0);
        Assert.True(hold.NextDueMs <= now);
        Assert.NotNull(hold.Poll());
        Assert.Equal(long.MaxValue, hold.NextDueMs);
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-4")]
    [Trait("Plan", "EDGE-fewer-redraws-1")]
    public void A_key_that_lapses_beside_a_live_one_is_reported_forced_at_the_end()
    {
        var hold = Hold();
        hold.Hold("a", 1000, 120_000);
        hold.Hold("b", 30_000, 120_000);
        now += 1000;
        Assert.Null(hold.Poll());      // b still holds
        hold.Release("b", 0);
        var r = hold.Poll()!;
        Assert.True(r.Keys.Single(k => k.Key == "a").TimedOut);
        Assert.True(r.Forced);
    }

    [Fact]
    [Trait("Plan", "EDGE-fewer-redraws-2")]
    public void A_lapsed_key_taken_again_past_its_cap_is_refused()
    {
        var hold = Hold();
        hold.Hold(Batch, 1000, 5000);
        now += 1000;
        Assert.NotNull(hold.Poll());   // lapsed
        now += 4000;
        Assert.False(hold.Hold(Batch, 1000, 5000));
        Assert.False(hold.IsHeld);
    }

    [Fact]
    [Trait("Plan", "EDGE-fewer-redraws-21")]
    public void An_event_on_a_mod_counts_as_the_batchs_own_for_two_seconds_after_its_mark()
    {
        var marks = new OwnWriteMarks(() => now, CompositorService.OwnRefitWriteMs);
        Assert.False(marks.IsMarked("Chibi"));
        marks.Mark("Chibi");
        now += CompositorService.OwnRefitWriteMs - 1;
        Assert.True(marks.IsMarked("chibi"));     // a player's change in the window is held too (accepted)
        Assert.False(marks.IsMarked("Other"));
        now += 1;
        Assert.False(marks.IsMarked("Chibi"));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-5")]
    [Trait("Plan", "EDGE-fewer-redraws-16")]
    [Trait("Plan", "EDGE-fewer-redraws-17")]
    public void Only_the_releases_own_composite_skips_the_material_reload_once_while_fresh()
    {
        var hold = Hold();
        Assert.False(hold.ConsumeReleaseToken(0));
        int first = hold.IssueReleaseToken();
        int second = hold.IssueReleaseToken();
        Assert.False(hold.ConsumeReleaseToken(first));     // superseded
        Assert.True(hold.ConsumeReleaseToken(second));
        Assert.False(hold.ConsumeReleaseToken(second));    // once

        int third = hold.IssueReleaseToken();
        now += RedrawHold.ReleaseTokenMs;
        Assert.False(hold.ConsumeReleaseToken(third));     // stale

        Assert.True(RedrawHold.PreambleReload(wasDirty: true, autoRedraw: true, bodyKeyUnchanged: true, releaseTokenValid: false));
        Assert.False(RedrawHold.PreambleReload(true, true, true, releaseTokenValid: true));
        Assert.False(RedrawHold.PreambleReload(false, true, true, false));
        Assert.False(RedrawHold.PreambleReload(true, false, true, false));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-5")]
    public void Materials_made_stale_while_held_are_owed_at_release()
    {
        var hold = Hold();
        Assert.False(hold.TryOweMaterials());
        hold.Hold(Batch, 30_000, 120_000);
        Assert.True(hold.TryOweMaterials());
        hold.Release(Batch, 0);
        Assert.True(hold.Poll()!.MaterialsOwed);
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-7")]
    [Trait("Plan", "EDGE-fewer-redraws-18")]
    public void A_draining_batch_waits_for_its_own_evaluations_but_not_past_the_bound()
    {
        // 1279: the last switch at 44.717; its classify and scan took ~0.1 s, the moved-base recheck fires 1.2 s later.
        long lastSwitch = 0, recheckLands = 1200 + 100;
        for (long waited = 0; waited < recheckLands; waited += 50)
            Assert.False(RedrawHold.ReleaseAfterWait(evaluationsPending: true, waited, capMs: 3000));
        Assert.True(RedrawHold.ReleaseAfterWait(evaluationsPending: false, recheckLands - lastSwitch, 3000));
        Assert.True(RedrawHold.ReleaseAfterWait(evaluationsPending: true, 3000, 3000));
    }

    [Fact]
    [Trait("Plan", "EDGE-fewer-redraws-23")]
    public void Triggers_offered_from_many_threads_are_each_handed_back_once()
    {
        var hold = Hold();
        hold.Hold(Batch, 30_000, 120_000);
        var deferred = new ConcurrentBag<string>();
        Parallel.For(0, 500, i =>
        {
            if (hold.TryDefer(Trigger($"t{i}"))) deferred.Add($"t{i}");
            hold.Hold(Batch, 30_000, 120_000);
        });
        hold.Release(Batch, 0);
        var r = hold.Poll()!;
        Assert.Equal(deferred.OrderBy(x => x), r.Reasons.OrderBy(x => x));
        Assert.Equal(500, r.Reasons.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-fewer-redraws-23")]
    public void A_trigger_racing_the_release_is_either_handed_back_or_told_to_run_now()
    {
        for (int round = 0; round < 50; round++)
        {
            var hold = Hold();
            hold.Hold(Batch, 30_000, 120_000);
            var deferred = new ConcurrentBag<string>();
            var ranNow = new ConcurrentBag<string>();
            var handedBack = new ConcurrentBag<string>();
            Parallel.Invoke(
                () => Parallel.For(0, 200, i =>
                {
                    if (hold.TryDefer(Trigger($"t{i}"))) deferred.Add($"t{i}"); else ranNow.Add($"t{i}");
                }),
                () =>
                {
                    hold.Release(Batch, 0);
                    for (int i = 0; i < 50; i++)
                        if (hold.Poll() is { } r) foreach (var reason in r.Reasons) handedBack.Add(reason);
                });
            if (hold.Poll() is { } last) foreach (var reason in last.Reasons) handedBack.Add(reason);

            Assert.Equal(deferred.OrderBy(x => x), handedBack.OrderBy(x => x));
            Assert.Equal(200, deferred.Count + ranNow.Count);
        }
    }
}
