using System.Collections.Generic;
using Proteus.Services;
using Xunit;
using Deferred = Proteus.Services.RedrawHold.Deferred;

namespace Proteus.Tests;

/// <summary>
/// The outfit change measured on build 1279 (2026-10-15 13:41, "Git Gud Proteus": chest, legs and shoes refitted),
/// replayed through the hold at the times the log shows. Property evidence only — what the game does between these
/// events is not in it; the in-game count is AC-9.
/// </summary>
public class RedrawHoldReplayTests
{
    [Fact]
    [Trait("Plan", "AC-fewer-redraws-1")]
    [Trait("Plan", "AC-fewer-redraws-7")]
    [Trait("Plan", "AC-fewer-redraws-9")]
    public void The_measured_batch_comes_out_as_one_redraw_and_one_composite()
    {
        long now = 0;
        var hold = new RedrawHold(() => now);
        const string key = "auto-refit#1";
        var ran = new List<string>();
        void At(double seconds) => now = (long)(seconds * 1000);

        // Offered as TriggerRecomposite does; what is not held runs.
        void Trigger(double at, string reason, bool own)
        {
            At(at);
            var a = RedrawHold.Admit(true, true, false, deferWhileHeld: own, () => { },
                                     () => hold.TryDefer(new Deferred(reason, true, false, false)));
            if (a == RedrawHold.Admission.Run) ran.Add(reason);
        }

        Trigger(17.631, "glamourer-equip", own: false);            // the design restore's: not the batch's
        At(18.412); Assert.True(hold.Hold(key, 30_000, 120_000));  // chest job
        Trigger(18.616, "post-settle-correction", own: false);     // not held (ND-2 = A)
        Trigger(22.631, "ModSettingChanged:Setting", own: true);
        At(22.658); Assert.True(hold.TryAbsorbModelRedraw("auto refit: _top switched on"));
        Trigger(23.821, "ModSettingChanged:TemporarySetting", own: true);   // the 1.2 s moved-base re-check
        At(27.223); hold.Hold(key, 30_000, 120_000);               // legs job
        Trigger(33.663, "ModSettingChanged:TemporarySetting", own: true);
        Trigger(33.682, "ModSettingChanged:Setting", own: true);
        At(33.717); hold.TryAbsorbModelRedraw("auto refit: _dwn switched on");
        At(38.601); hold.Hold(key, 30_000, 120_000);               // shoes job
        Trigger(44.698, "ModSettingChanged:TemporarySetting", own: true);
        Trigger(44.717, "ModSettingChanged:Setting", own: true);
        At(44.742); hold.TryAbsorbModelRedraw("auto refit: _sho switched on");

        // Drain: the last switch's re-check lands 1.2 s on (plus its classify and scan) and is held, because the
        // batch waits for it before letting go.
        At(44.75);
        Assert.False(RedrawHold.ReleaseAfterWait(evaluationsPending: true, waitedMs: 0, capMs: 3000));
        Trigger(46.0, "ModSettingChanged:Setting (re-check)", own: true);
        Assert.True(RedrawHold.ReleaseAfterWait(evaluationsPending: false, waitedMs: 1250, capMs: 3000));
        hold.Release(key, 1500);
        At(47.499); Assert.Null(hold.Poll());
        At(47.5);
        var r = hold.Poll()!;

        Assert.Equal(["glamourer-equip", "post-settle-correction"], ran);   // only what the batch did not cause
        Assert.Equal(3, r.ModelRedrawCauses.Count);
        Assert.False(r.Forced);
        var plan = RedrawHold.PlanRelease(r, disposed: false, canComposite: true);
        Assert.True(plan.RedrawFirst);
        Assert.True(plan.Composite);
        // One redraw and one composite for the whole batch, where 1279 had #5, #6, #10 and reloads #1, #2, #3, #5.
        int redraws = plan.RedrawFirst ? 1 : 0, composites = plan.Composite ? 1 : 0;
        Assert.Equal((1, 1), (redraws, composites));
    }
}
