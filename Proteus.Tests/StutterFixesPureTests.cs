using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using NSubstitute;
using Proteus.Interop;
using Proteus.Services;
using Xunit;
using static Proteus.Services.CarrierDecisions;

namespace Proteus.Tests;

/// <summary>
/// The invisible-carrier decisions (stutter fixes). A user's log showed "asked: ril carrier on" every 5.25 s without
/// end, spurious re-asks on stale walks, and a false "no record of equipping it" 0.9 s after Proteus's own removal.
/// </summary>
public class CarrierDecisionsTests
{
    private static EquipDecision Ask(bool walkShowsOurs = false, bool walkFresh = true, bool probeConfirmed = false,
                                     bool snapshotKnown = true, bool occupied = false, long cooldownLeft = 0, int asks = 0)
        => Equip(walkShowsOurs, walkFresh, probeConfirmed, snapshotKnown, occupied, cooldownLeft, asks);

    // [AC-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    public void ConsecutiveAsksCappedAtTwo()
    {
        Assert.Equal(EquipAction.Equip, Ask(asks: 0).Action);
        Assert.Equal(EquipAction.Equip, Ask(asks: 1, walkFresh: true).Action);
        Assert.Equal(EquipAction.GaveUp, Ask(asks: 2).Action);
    }

    // [AC-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    public void ProbeConfirmedOrFreshWalkMeansNoAsk()
    {
        var a = Ask(probeConfirmed: true, asks: 1);
        var b = Ask(walkShowsOurs: true, asks: 1);
        Assert.Equal(EquipAction.None, a.Action);
        Assert.True(a.Drawn);
        Assert.Equal(EquipAction.None, b.Action);
        Assert.True(b.Drawn);
    }

    // [EDGE-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-1")]
    public void ConfirmedProbeBeatsAStaleWalk()
        => Assert.Equal(EquipAction.None, Ask(probeConfirmed: true, walkFresh: false, asks: 1).Action);

    // [AC-stutter-fixes-1] [EDGE-stutter-fixes-2]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    [Trait("Plan", "EDGE-stutter-fixes-2")]
    public void StaleWalkDuringTheCooldownWaitsRatherThanRewalking()
    {
        // A reconcile inside the cooldown (the redraw hook, a skipped composite) never pays for a framework-thread
        // re-walk: the re-walk waits for retry time, when the settle window has passed and the walk comes back fresh.
        var d = Ask(walkFresh: false, asks: 1, cooldownLeft: 1200);
        Assert.Equal(EquipAction.RetryAfter, d.Action);
        Assert.Equal(1200, d.RetryMs);
    }

    // [EDGE-stutter-fixes-2]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-2")]
    public void StaleWalkAtRetryAsksForARewalk()
        => Assert.Equal(EquipAction.RewalkFirst, Ask(walkFresh: false, asks: 1).Action);

    // [EDGE-stutter-fixes-6]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-6")]
    public void UnknownSnapshotOrOccupiedSlotNeverAsks()
    {
        Assert.Equal(EquipAction.None, Ask(snapshotKnown: false).Action);
        Assert.False(Ask(snapshotKnown: false).Drawn);
        Assert.Equal(EquipAction.None, Ask(occupied: true).Action);
        Assert.False(Ask(occupied: true).Drawn);
    }

    // [AC-stutter-fixes-6] [EDGE-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-6")]
    [Trait("Plan", "EDGE-stutter-fixes-4")]
    public void RevertedAfterADrawnSightingAsksAgain()
    {
        // Seen drawn: the caller resets the count. A zone change then reverts it: an empty fresh walk asks again.
        Assert.True(Ask(walkShowsOurs: true, asks: 1).Drawn);
        Assert.Equal(EquipAction.Equip, Ask(asks: 0, probeConfirmed: false).Action);
    }

    // [AC-stutter-fixes-1] [EDGE-stutter-fixes-39]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    [Trait("Plan", "EDGE-stutter-fixes-39")]
    public void ConfirmationOlderThanTheLastRedrawDoesNotCount()
    {
        Assert.True(ConfirmationCounts(confirmedAtTick: 300, lastAskTick: 100, lastRedrawTick: 200));
        Assert.False(ConfirmationCounts(confirmedAtTick: 300, lastAskTick: 100, lastRedrawTick: 400));   // zone change after it
        Assert.False(ConfirmationCounts(confirmedAtTick: 300, lastAskTick: 500, lastRedrawTick: 0));     // asked again after it
        Assert.False(ConfirmationCounts(confirmedAtTick: 300, lastAskTick: 300, lastRedrawTick: 0));     // equal: not newer
        // Not counted, a fresh empty walk decides: ask.
        Assert.Equal(EquipAction.Equip, Ask(probeConfirmed: false, walkFresh: true, asks: 0).Action);
    }

    // [AC-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    public void CooldownSchedulesARetry()
    {
        var d = Ask(cooldownLeft: 1200);
        Assert.Equal(EquipAction.RetryAfter, d.Action);
        Assert.Equal(1200, d.RetryMs);
    }

    private const ulong Ours = 9295;

    // [AC-stutter-fixes-3]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-3")]
    public void RecordedCarrierWalkShowsIsRemoved()
        => Assert.Equal(SweepAction.Remove, Sweep(false, true, true, null, Ours, null, null));

    // [AC-stutter-fixes-3]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-3")]
    public void RecordedUnseenCarrierHeldByGlamourerIsRemoved()
        => Assert.Equal(SweepAction.Remove, Sweep(false, false, true, Ours, Ours, null, null));

    // [AC-stutter-fixes-5]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-5")]
    public void OurModelStillDrawnRightAfterOurRemovalIsInFlight()
        => Assert.Equal(SweepAction.RemovalInFlight, Sweep(false, true, false, null, Ours, null, 900));

    // [AC-stutter-fixes-5]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-5")]
    public void UnrecordedCarrierWithNoRecentRemovalIsUnclaimed()
        => Assert.Equal(SweepAction.Unclaimed, Sweep(false, true, false, null, Ours, null, null));

    // [EDGE-stutter-fixes-11]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-11")]
    public void AfterTheGraceWindowItIsUnclaimed()
        => Assert.Equal(SweepAction.Unclaimed, Sweep(false, true, false, null, Ours, null, RemovalGraceMs + 1));

    // [EDGE-stutter-fixes-12]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-12")]
    public void InsideTheWindowAPlayerPieceReadsInFlight()
        => Assert.Equal(SweepAction.RemovalInFlight, Sweep(false, true, false, null, Ours, null, 100));

    // [EDGE-stutter-fixes-13]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-13")]
    public void GlamourerEmptyWithoutAPostReadWalkLeaves()
        => Assert.Equal(SweepAction.Leave, Sweep(false, false, true, 4294967155UL, Ours, null, null));

    // [EDGE-stutter-fixes-14]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-14")]
    public void GlamourerEmptyAndPostReadWalkClearForgets()
    {
        Assert.Equal(SweepAction.ForgetRecord, Sweep(false, false, true, 4294967155UL, Ours, false, null));
        Assert.Equal(SweepAction.ForgetRecord, Sweep(false, false, true, 0UL, Ours, false, null));
    }

    // [EDGE-stutter-fixes-15]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-15")]
    public void PostReadWalkShowingOursRemoves()
        => Assert.Equal(SweepAction.Remove, Sweep(false, false, true, 0UL, Ours, true, null));

    // [EDGE-stutter-fixes-16]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-16")]
    public void UnreadableGlamourerKeepsTheRecord()
        => Assert.Equal(SweepAction.Leave, Sweep(false, false, true, null, Ours, null, null));

    // [EDGE-stutter-fixes-17]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-17")]
    public void AnotherItemForgetsWithoutWriting()
        => Assert.Equal(SweepAction.ForgetRecord, Sweep(false, false, true, 1234UL, Ours, null, null));

    // [EDGE-stutter-fixes-18]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-18")]
    public void FailedBuildRemovesNothing()
    {
        Assert.False(MayRelease(gearWanted: true, shellBuilt: false));
        Assert.True(MayRelease(gearWanted: true, shellBuilt: true));
        Assert.True(MayRelease(gearWanted: false, shellBuilt: false));
    }

    // [AC-stutter-fixes-3] [EDGE-stutter-fixes-46]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-3")]
    [Trait("Plan", "EDGE-stutter-fixes-46")]
    public void InvalidPostReadWalkLeaves()
    {
        Func<IReadOnlyCollection<string>, bool> body = m => m.Any(p => p.Contains("_top"));
        Assert.False(IsValidWalk(null, body));
        Assert.False(IsValidWalk(new HashSet<string>(), body));
        Assert.False(IsValidWalk(new HashSet<string> { "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl" }, body));
        Assert.True(IsValidWalk(new HashSet<string> { "chara/equipment/e0000/model/c1401e0000_top.mdl" }, body));
        // An invalid walk means "no post-read walk": Leave, never ForgetRecord.
        Assert.Equal(SweepAction.Leave, Sweep(false, false, true, 0UL, Ours, null, null));
    }
}

public class CarrierAskCounterTests
{
    // [AC-stutter-fixes-6] [EDGE-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-6")]
    [Trait("Plan", "EDGE-stutter-fixes-4")]
    public void DrawnSightingResetsTheCount()
    {
        var c = new CarrierAskCounter();
        Assert.True(c.TryBeginAsk("ril", 1, AskCap, 100));
        Assert.True(c.TryBeginAsk("ril", 1, AskCap, 200));
        Assert.False(c.TryBeginAsk("ril", 1, AskCap, 300));
        c.Reset("ril");
        Assert.True(c.TryBeginAsk("ril", 1, AskCap, 400));
        Assert.Equal(400, c.LastAskTick("ril"));
    }

    // [AC-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    public void AFailedWriteDoesNotCountAgainstTheCap()
    {
        var c = new CarrierAskCounter();
        Assert.True(c.TryBeginAsk("ril", 1, AskCap, 1));
        c.Undo("ril");                                   // the write never reached the player
        Assert.Equal(0, c.Count("ril", 1));
        Assert.True(c.TryBeginAsk("ril", 1, AskCap, 2));
        Assert.True(c.TryBeginAsk("ril", 1, AskCap, 3));
    }

    // [EDGE-stutter-fixes-5]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-5")]
    public void NewEpochResetsTheCount()
    {
        var c = new CarrierAskCounter();
        c.TryBeginAsk("rir", 1, AskCap, 1);
        c.TryBeginAsk("rir", 1, AskCap, 2);
        Assert.False(c.TryBeginAsk("rir", 1, AskCap, 3));
        Assert.Equal(0, c.Count("rir", 2));
        Assert.True(c.TryBeginAsk("rir", 2, AskCap, 4));
    }

    // [EDGE-stutter-fixes-3]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-3")]
    public void ConcurrentAsksNeverExceedTheCap()
    {
        var c = new CarrierAskCounter();
        int granted = 0;
        Parallel.For(0, 64, i => { if (c.TryBeginAsk("ril", 7, AskCap, i)) Interlocked.Increment(ref granted); });
        Assert.Equal(AskCap, granted);
    }
}

public class CarrierCooldownsTests
{
    // [AC-stutter-fixes-3] [EDGE-stutter-fixes-7]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-3")]
    [Trait("Plan", "EDGE-stutter-fixes-7")]
    public void RemovingOneSlotDoesNotDelayAnother()
    {
        var c = new CarrierCooldowns();
        c.Charge("rir", 1000);   // the host-moved removal of rir
        Assert.Equal(0, c.LeftMs("ril", 1001, 5000));
        Assert.Equal(4999, c.LeftMs("rir", 1001, 5000));
    }

    // [EDGE-stutter-fixes-8]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-8")]
    public void EachSlotHasItsOwnWindow()
    {
        var c = new CarrierCooldowns();
        c.Charge("rir", 0);
        c.Charge("ril", 3000);
        Assert.Equal(0, c.LeftMs("rir", 6000, 5000));
        Assert.Equal(2000, c.LeftMs("ril", 6000, 5000));
    }
}

public class CoalescingRetryTests
{
    // [EDGE-stutter-fixes-41]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-41")]
    public void EarlierRequestReArmsLaterIsAbsorbed()
    {
        var r = new CoalescingRetry();
        Assert.True(r.Request("rir", 5000, out var g1));
        Assert.True(r.Request("ril", 1000, out var g2));   // earlier: re-arm
        Assert.False(r.Request("rir", 9000, out _));        // later: absorbed
        Assert.NotEqual(g1, g2);
        Assert.True(r.PendingFor("rir") && r.PendingFor("ril"));
        Assert.Equal(1000, r.Deadline);
    }

    // [EDGE-stutter-fixes-41]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-41")]
    public void SupersededTimerIsANoOp()
    {
        var r = new CoalescingRetry();
        r.Request("rir", 5000, out var g1);
        r.Request("ril", 1000, out var g2);
        Assert.True(r.Fire(g2, out var slots));
        Assert.Equal(2, slots.Count);
        Assert.False(r.Fire(g1, out _));
        Assert.False(r.PendingFor("rir"));
    }

    // [AC-stutter-fixes-6] [EDGE-stutter-fixes-48]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-6")]
    [Trait("Plan", "EDGE-stutter-fixes-48")]
    public void FireDuringACompositeReArms()
    {
        var r = new CoalescingRetry();
        r.Request("ril", 1000, out var g);
        Assert.True(r.Fire(g, out var slots));
        // A composite is in flight: the caller re-requests each slot at now + rearm instead of dropping it.
        foreach (var s in slots) Assert.True(r.Request(s, 2000, out g));
        Assert.True(r.PendingFor("ril"));
        Assert.True(r.Fire(g, out var again));
        Assert.Contains("ril", again);
    }
}

public class ShellRecheckTests
{
    private const int Cd = 5000, Settle = SettleMs;

    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void DelayRunsFromTheSlotsLastAsk()
    {
        Assert.Equal(6500, ShellRecheck.DelayMs(1000, 1000, Cd, Settle));
        Assert.Equal(0, ShellRecheck.DelayMs(99_999, 1000, Cd, Settle));
    }

    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void WarnOnlyAtTheCapWithTheHostStillGone()
    {
        Assert.Equal(ShellRecheck.Verdict.Warn, ShellRecheck.Next(1, 7000, false, false, true, Cd, Settle));
        Assert.Equal(ShellRecheck.Verdict.Done, ShellRecheck.Next(1, 7000, false, false, false, Cd, Settle));
    }

    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void WarnsOnlyAtTheCapAfterSettle()
        => Assert.Equal(ShellRecheck.Verdict.Warn, ShellRecheck.Next(2, Cd + Settle, false, false, true, Cd, Settle));

    // [EDGE-stutter-fixes-20]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-20")]
    public void DeferralInTheFirstRecheckSchedulesASecond()
        => Assert.Equal(ShellRecheck.Verdict.Schedule, ShellRecheck.Next(1, 300, false, false, true, Cd, Settle));

    // [EDGE-stutter-fixes-21]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-21")]
    public void SecondRecheckStillDeferredLogsInfo()
        => Assert.Equal(ShellRecheck.Verdict.Info, ShellRecheck.Next(2, 300, false, false, true, Cd, Settle));

    // [EDGE-stutter-fixes-22]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-22")]
    public void BelowTheCapNeverWarns()
    {
        Assert.NotEqual(ShellRecheck.Verdict.Warn, ShellRecheck.Next(1, 99_999, false, true, true, Cd, Settle));
        Assert.NotEqual(ShellRecheck.Verdict.Warn, ShellRecheck.Next(2, 99_999, false, true, true, Cd, Settle));
    }

    // [AC-stutter-fixes-4] [EDGE-stutter-fixes-42]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    [Trait("Plan", "EDGE-stutter-fixes-42")]
    public void RetryAt5250RecheckAt6500Defers()
    {
        // The retry went out at +5250, so at +6500 only 1250 ms have passed since the slot's last ask.
        Assert.Equal(ShellRecheck.Verdict.Schedule, ShellRecheck.Next(1, 6500 - 5250, false, false, true, Cd, Settle));
    }

    /// <summary>A retry whose ask lands 700 ms after its deadline (a slow re-walk) still gets its Warning.</summary>
    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void ASlowRetryAskStillReachesTheWarning()
    {
        long deadline = 10_000, firstRecheck = 9_900, ask2 = deadline + 700;
        long delay = ShellRecheck.NextDelayMs(firstRecheck, lastAskTick: 3_250, retryDeadlineTick: deadline,
                                              retryOwedNotPending: false, Cd, Settle);
        long secondRecheck = firstRecheck + delay + 250 + 2_400;   // timer margin + the drawn check's poll window
        Assert.Equal(ShellRecheck.Verdict.Warn,
                     ShellRecheck.Next(2, secondRecheck - ask2, false, false, true, Cd, Settle));
    }

    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void AnOwedRetryWaitsAFullCooldown()
        => Assert.Equal(Cd + Settle, ShellRecheck.NextDelayMs(50_000, 1_000, long.MaxValue, true, Cd, Settle));

    // [AC-stutter-fixes-4] [EDGE-stutter-fixes-45]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    [Trait("Plan", "EDGE-stutter-fixes-45")]
    public void RecheckCoincidingWithTheRetryDefers()
        => Assert.Equal(ShellRecheck.Verdict.Schedule, ShellRecheck.Next(1, Cd + Settle, true, false, true, Cd, Settle));

    // [AC-stutter-fixes-4] [EDGE-stutter-fixes-45]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    [Trait("Plan", "EDGE-stutter-fixes-45")]
    public void BelowTheCapAlwaysDefers()
        => Assert.Equal(ShellRecheck.Verdict.Schedule, ShellRecheck.Next(1, 99_999, false, true, true, Cd, Settle));
}

public class GlamourerItemIdsTests
{
    // [EDGE-stutter-fixes-14]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-14")]
    public void NothingIdsAndZeroAreEmpty()
    {
        Assert.True(GlamourerItemIds.IsEmpty(0));
        Assert.True(GlamourerItemIds.IsEmpty(4294967155UL));                 // 无, from the user's log
        Assert.True(GlamourerItemIds.IsEmpty(uint.MaxValue - 128UL - 10));   // RFinger
        Assert.True(GlamourerItemIds.IsEmpty(uint.MaxValue - 128UL - 11));   // LFinger
        Assert.False(GlamourerItemIds.IsEmpty(9295));
    }
}

public class GlamourerSignalsTests
{
    // [AC-stutter-fixes-2]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-2")]
    public void OwnCarrierWriteEchoDoesNotRaiseEquipmentChange()
    {
        Assert.False(GlamourerSignals.RaisesEquipmentChange(StateChangeType.Equip, false, true));
        Assert.False(GlamourerSignals.RaisesEquipmentChange(StateChangeType.BonusItem, false, true));
        Assert.False(GlamourerSignals.RaisesEquipmentChange(StateChangeType.Equip, true, false));   // reapply echo, as before
    }

    // [EDGE-stutter-fixes-9]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-9")]
    public void OnlyTheOwnWriteFlagSuppresses()
    {
        Assert.True(GlamourerSignals.RaisesEquipmentChange(StateChangeType.Equip, false, false));
        Assert.False(GlamourerSignals.RaisesEquipmentChange(StateChangeType.Customize, false, false));
    }

    // [EDGE-stutter-fixes-10]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-10")]
    public void NoOwnWriteRaisesAsBefore()
        => Assert.True(GlamourerSignals.RaisesEquipmentChange(StateChangeType.BonusItem, false, false));
}

public class PhaseResidualTests
{
    // [AC-stutter-fixes-10] [EDGE-stutter-fixes-36]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-10")]
    [Trait("Plan", "EDGE-stutter-fixes-36")]
    public void NeverNegativeAndFlagsOverlap()
    {
        Assert.Equal((0L, true), PhaseResidual.Blend(507, 2482));
        Assert.Equal((200L, false), PhaseResidual.Blend(300, 100));
    }
}

public class GcPauseProbeTests
{
    // [AC-stutter-fixes-7]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-7")]
    public void ReportsThePauseDeltaOverTheSpan()
    {
        var t = TimeSpan.FromMilliseconds(10);
        var probe = new GcPauseProbe(() => t);
        t = TimeSpan.FromMilliseconds(35);
        Assert.Equal(25, probe.ElapsedPauseMs);
    }
}

public class RefreshTimelineGcTests
{
    // [AC-stutter-fixes-10]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-10")]
    public void DescribeAppendsGcPause()
    {
        var t = TimeSpan.Zero;
        var tl = new RefreshTimeline(PhaseCounter.Begin(), 1, "glamourer-equip", () => t);
        Assert.DoesNotContain("gc pause", tl.Describe());
        t = TimeSpan.FromMilliseconds(42);
        Assert.Contains("gc pause 42", tl.Describe());
    }
}

public class StutterIdleTests
{
    private static bool Idle(string[] reasons, bool forced = false, bool emptyDrawn = true, bool carrier = false,
                             bool withdrawing = false, bool anyEnabled = false)
        => RedrawHold.IdleNothingToDo(reasons, forced, emptyDrawn, carrier, withdrawing, () => anyEnabled);

    // [AC-stutter-fixes-9]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-9")]
    public void IdleNothingToDo()
    {
        Assert.True(Idle(["glamourer-equip"]));
        Assert.True(Idle(["glamourer-equip", "equipment-change"]));
        Assert.False(Idle(["glamourer-equip"], anyEnabled: true));
        Assert.False(Idle(["glamourer-equip"], emptyDrawn: false));
        Assert.False(Idle([]));
        // anyEnabled (a Penumbra read) asked last: not at all when something else already says no.
        bool asked = false;
        RedrawHold.IdleNothingToDo(["glamourer-equip"], true, true, false, false, () => asked = true);
        Assert.False(asked);
    }

    // [EDGE-stutter-fixes-31]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-31")]
    public void IdleNothingToDoFalseWhenAForceIsLatched() => Assert.False(Idle(["glamourer-equip"], forced: true));

    // [EDGE-stutter-fixes-32]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-32")]
    public void TemporarySettingThenGlamourerEquipFromIdleIsNotSkipped()
        => Assert.False(Idle(["ModSettingChanged", "glamourer-equip"]));

    // [EDGE-stutter-fixes-33]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-33")]
    public void FullReasonListIsNotSkipped()
        => Assert.False(Idle(["glamourer-equip", "equipment-change", "glamourer-equip", "equipment-change"]));

    // [EDGE-stutter-fixes-34]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-34")]
    public void IdleNothingToDoFalseWhileACarrierIsRecordedOrWithdrawing()
    {
        Assert.False(Idle(["glamourer-equip"], carrier: true));
        Assert.False(Idle(["glamourer-equip"], withdrawing: true));
    }

    // [EDGE-stutter-fixes-35]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-35")]
    public void CustomizationAndDesignReasonsAreNeverSkipped()
    {
        Assert.False(Idle(["glamourer-customize"]));
        Assert.False(Idle(["glamourer-design"]));
        Assert.False(Idle(["body-mod"]));
    }
}

public class MissingOverlayImageTests
{
    private static (TextureLoader Loader, IPluginLog Log) Make()
    {
        var log = Substitute.For<IPluginLog>();
        return (new TextureLoader(Substitute.For<IDataManager>(), log), log);
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "proteus-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // [AC-stutter-fixes-10]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-10")]
    public void MissingFileWarnsOncePerPathWithoutStack()
    {
        var (loader, log) = Make();
        var dir = TempDir();
        var mixed = dir.Replace('\\', '/') + "/Style/mask.png";
        Assert.Null(loader.LoadPngAsRgba(mixed, 4, 4));
        Assert.Null(loader.LoadPngAsRgba(mixed, 4, 4));
        log.Received(1).Warning(Arg.Any<string>(), Arg.Is<object[]>(a => a.Length == 1
            && ((string)a[0]).Contains("\\Style\\mask.png") && !((string)a[0]).Contains('/')));
        log.DidNotReceive().Error(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());
        log.DidNotReceive().Warning(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());
    }

    // [EDGE-stutter-fixes-37]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-37")]
    public void AFileThatAppearsLaterLoads()
    {
        var (loader, _) = Make();
        var dir = TempDir();
        var path = Path.Combine(dir, "late.png");
        Assert.Null(loader.LoadPngAsRgba(path, 1, 1));
        // A 1x1 RGBA PNG.
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        Assert.NotNull(loader.LoadPngAsRgba(path, 1, 1));
    }

    // [EDGE-stutter-fixes-37]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-37")]
    public void ACorruptFileStillLogsAnError()
    {
        var (loader, log) = Make();
        var path = Path.Combine(TempDir(), "corrupt.png");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        Assert.Null(loader.LoadPngAsRgba(path, 1, 1));
        log.Received(1).Error(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());
    }
}
