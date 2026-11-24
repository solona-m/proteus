using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.CarrierDecisions;

namespace Proteus.Tests;

/// <summary>
/// The invisible-glasses carrier (Gold Eyepatch, Right) has a visible model of its own, so it is worn only while the
/// shell is published on it and Penumbra serves that redirect, and comes off whenever the shell is not on it.
/// </summary>
public sealed class GlassesCarrierDecisionTests
{
    private static GlassesDecision Decide(bool featureOn = true, bool gearWanted = true, bool shellBuilt = true,
                                          bool published = true, bool writeFailed = false, bool recorded = true,
                                          bool drawn = true, bool metKnown = true, bool anyMetWorn = false,
                                          long cooldownLeft = 0, long? sinceRemoval = null, bool resting = false,
                                          bool live = true, int livePolls = 0, Action? onLiveCheck = null)
        => Glasses(featureOn, gearWanted, shellBuilt, published, writeFailed, recorded, drawn, metKnown, anyMetWorn,
                   cooldownLeft, sinceRemoval, resting, () => { onLiveCheck?.Invoke(); return live; }, livePolls);

    [Fact]
    public void FailedBuildRemovesOurCarrier() // [AC-face-glasses-priority-4] [EDGE-face-glasses-priority-13]
    {
        var d = Decide(shellBuilt: false, published: false);

        Assert.Equal(GlassesAction.Remove, d.Action);
        Assert.True(d.ChargeCooldown);
        Assert.True(d.FailureRemoval);
    }

    [Fact]
    public void NoCharacterCodeIsAFailedBuild() // [AC-face-glasses-priority-4] [EDGE-face-glasses-priority-14]
    {
        // The shell phase skipped (no character code yet): no shell, nothing published, overlays still wanted.
        var d = Decide(shellBuilt: false, published: false, writeFailed: false);

        Assert.Equal(GlassesAction.Remove, d.Action);
        Assert.True(d.FailureRemoval);
    }

    [Fact]
    public void ShellMovedElsewhereRemovesWithoutCountingAFailure() // [AC-face-glasses-priority-4] [EDGE-face-glasses-priority-15]
    {
        var d = Decide(shellBuilt: true, published: false);

        Assert.Equal(GlassesAction.Remove, d.Action);
        Assert.True(d.ChargeCooldown);
        Assert.False(d.FailureRemoval);
    }

    [Fact]
    public void MetWriteFailureSingleHostRemoves() // [AC-face-glasses-priority-4] [EDGE-face-glasses-priority-21]
    {
        var d = Decide(shellBuilt: false, published: false, writeFailed: true);

        Assert.Equal(GlassesAction.Remove, d.Action);
        Assert.True(d.FailureRemoval);
    }

    [Fact]
    public void MetWriteFailureMultiHostRemoves() // [AC-face-glasses-priority-4] [EDGE-face-glasses-priority-21]
    {
        // A ring host published; the facewear host's write failed, so its redirect is gone.
        var d = Decide(shellBuilt: true, published: false, writeFailed: true);

        Assert.Equal(GlassesAction.Remove, d.Action);
        Assert.True(d.ChargeCooldown);
        Assert.True(d.FailureRemoval);
    }

    [Fact]
    public void MetWriteFailureAutoRedrawOffRemovesAndWaitsForATrigger() // [EDGE-face-glasses-priority-29]
    {
        // Auto redraw is not an input: the carrier comes off the same way, and no retry is asked for.
        var d = Decide(shellBuilt: true, published: false, writeFailed: true);

        Assert.Equal(GlassesAction.Remove, d.Action);
        Assert.Equal(0, d.RetryMs);
    }

    public static IEnumerable<object[]> EveryBuildInput()
    {
        foreach (var feature in new[] { true, false })
        foreach (var wanted in new[] { true, false })
        foreach (var built in new[] { true, false })
        foreach (var published in new[] { true, false })
        foreach (var failed in new[] { true, false })
            yield return [feature, wanted, built, published, failed];
    }

    [Theory]
    [MemberData(nameof(EveryBuildInput))]
    public void PlayersOwnPairIsNeverRemoved(bool feature, bool wanted, bool built, bool published, bool failed) // [AC-face-glasses-priority-6] [EDGE-face-glasses-priority-16]
    {
        var d = Decide(featureOn: feature, gearWanted: wanted, shellBuilt: built, published: published,
                       writeFailed: failed, recorded: false, drawn: true, anyMetWorn: true);

        Assert.NotEqual(GlassesAction.Remove, d.Action);
        Assert.NotEqual(GlassesAction.Equip, d.Action);
    }

    [Fact]
    public void HostedAgainWithinCooldownRetriesThenEquips() // [AC-face-glasses-priority-7] [EDGE-face-glasses-priority-18]
    {
        var waiting = Decide(recorded: false, drawn: false, cooldownLeft: 3000);
        Assert.Equal(GlassesAction.RetryAfter, waiting.Action);
        Assert.Equal(3000, waiting.RetryMs);
        Assert.False(waiting.LivePoll);

        Assert.Equal(GlassesAction.Equip, Decide(recorded: false, drawn: false, cooldownLeft: 0).Action);
    }

    [Fact]
    public void FailSucceedLoopIsBounded() // [AC-face-glasses-priority-7] [EDGE-face-glasses-priority-23]
    {
        // A build that fails every other second for two minutes, with the 5 s inject cooldown applied as the compositor does.
        const int cooldown = 5000;
        var churn = new GlassesChurn();
        bool ours = false;
        long lastInject = long.MinValue / 2;
        var writes = new List<long>();
        bool rested = false;

        for (long t = 0; t <= 120_000; t += 1000)
        {
            bool fail = (t / 1000) % 2 == 1;
            long since = t - lastInject;
            long left = since >= 0 && since <= cooldown ? cooldown - since : 0;
            var d = Decide(shellBuilt: !fail, published: !fail, recorded: ours, drawn: ours, anyMetWorn: ours,
                           cooldownLeft: left, resting: churn.Resting(t));
            switch (d.Action)
            {
                case GlassesAction.Equip:
                    ours = true; lastInject = t; writes.Add(t);
                    break;
                case GlassesAction.Remove:
                    ours = false; writes.Add(t);
                    if (d.ChargeCooldown) lastInject = t;
                    if (d.FailureRemoval) churn.NoteFailureRemoval(t);
                    break;
                case GlassesAction.Rest:
                    rested = true;
                    break;
            }
        }

        Assert.True(rested);
        foreach (var start in writes)
            Assert.True(writes.Count(w => w >= start && w < start + GlassesChurn.WindowMs) <= 4,
                        $"more than 4 Glamourer writes in the 60 s from {start} ms: {string.Join(",", writes)}");
    }

    [Fact]
    public void RestEndsAfterWindowOrConfirmedDraw() // [AC-face-glasses-priority-7] [EDGE-face-glasses-priority-23]
    {
        var churn = new GlassesChurn();
        Assert.False(churn.NoteFailureRemoval(1_000));
        Assert.True(churn.NoteFailureRemoval(2_000));                     // the cap: rest begins
        Assert.True(churn.Resting(30_000));
        Assert.False(churn.Resting(2_000 + GlassesChurn.WindowMs));       // the window has passed

        var confirmed = new GlassesChurn();
        confirmed.NoteFailureRemoval(1_000);
        confirmed.NoteFailureRemoval(2_000);
        confirmed.NoteConfirmedOnFacewear();
        Assert.False(confirmed.Resting(3_000));
        Assert.False(confirmed.NoteFailureRemoval(4_000));                // the count started again

        // A removal while already resting neither starts a second rest nor extends it.
        var extending = new GlassesChurn();
        extending.NoteFailureRemoval(1_000);
        Assert.True(extending.NoteFailureRemoval(2_000));
        long endsAt = extending.RestEndsAt;
        Assert.False(extending.NoteFailureRemoval(10_000));
        Assert.Equal(endsAt, extending.RestEndsAt);

        Assert.Equal(GlassesAction.Rest, Decide(recorded: false, drawn: false, resting: true).Action);
    }

    [Fact]
    public void RedirectGapUsesTheExactWalkedPath() // [AC-face-glasses-priority-8] [EDGE-face-glasses-priority-25]
    {
        const string walked = "chara/equipment/e5521/model/c0801e5521_met.mdl";
        var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/equipment/e5521/model/c0101e5521_met.mdl"] = "models/secondskin_0.mdl",
        };
        string? Lookup(string k) => manifest.TryGetValue(k, out var v) ? v : null;

        Assert.True(GlassesRedirectGap(walked, Lookup));                  // only another race code's key
        manifest[walked] = "models/secondskin_0.mdl";
        Assert.False(GlassesRedirectGap(walked, Lookup));                 // the exact key
        Assert.False(GlassesRedirectGap(null, _ => throw new InvalidOperationException("no lookup without a path")));
        Assert.True(GlassesRedirectGap(walked, _ => null));               // missing

        // Once per path and manifest: a new publish re-arms the Warning, repeated redraws on one manifest don't.
        string? last = null;
        Assert.True(SecondSkinService.LogLineChanged(ref last, GlassesGapLogKey(walked, 7)));
        Assert.False(SecondSkinService.LogLineChanged(ref last, GlassesGapLogKey(walked, 7)));
        Assert.True(SecondSkinService.LogLineChanged(ref last, GlassesGapLogKey(walked, 8)));
    }

    [Fact]
    public void EquipWaitsForTheRedirectToBeLive() // [AC-face-glasses-priority-9] [EDGE-face-glasses-priority-24]
    {
        var poll = Decide(recorded: false, drawn: false, live: false, livePolls: 0);
        Assert.Equal(GlassesAction.RetryAfter, poll.Action);
        Assert.Equal(GlassesLivePollMs, poll.RetryMs);
        Assert.True(poll.LivePoll);

        Assert.Equal(GlassesAction.StopPolling, Decide(recorded: false, drawn: false, live: false, livePolls: GlassesLivePollCap).Action);
        Assert.Equal(GlassesAction.Equip, Decide(recorded: false, drawn: false, live: true, livePolls: 0).Action);
        // A later reconcile after the polls stopped still equips once the redirect is live.
        Assert.Equal(GlassesAction.Equip, Decide(recorded: false, drawn: false, live: true, livePolls: GlassesLivePollCap).Action);
    }

    [Fact]
    public void LiveCheckRunsOnlyOnTheWouldEquipPath() // [AC-face-glasses-priority-9]
    {
        int checks = 0;
        Decide(anyMetWorn: true, onLiveCheck: () => checks++);                        // a pair worn
        Decide(recorded: false, drawn: false, cooldownLeft: 100, onLiveCheck: () => checks++);
        Decide(recorded: false, drawn: false, resting: true, onLiveCheck: () => checks++);
        Decide(shellBuilt: false, published: false, onLiveCheck: () => checks++);     // a removal
        Assert.Equal(0, checks);

        Decide(recorded: false, drawn: false, onLiveCheck: () => checks++);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void RedirectLiveComparesCanonicalPaths() // [AC-face-glasses-priority-9] [EDGE-face-glasses-priority-30]
    {
        Assert.True(GlassesRedirectLive(@"E:\Penumbra\Proteus\models\secondskin_0.mdl", @"e:\penumbra\proteus\MODELS\secondskin_0.mdl"));
        Assert.False(GlassesRedirectLive(@"E:\Penumbra\Proteus\models\secondskin_0.mdl", @"E:\Penumbra\SomeGlassesMod\e5521_met.mdl"));
        Assert.False(GlassesRedirectLive(null, @"E:\x.mdl"));
        Assert.False(GlassesRedirectLive(@"E:\x.mdl", null));
    }

    [Fact]
    public void StaleWalkAfterOurRemovalIsInFlight() // [EDGE-face-glasses-priority-17]
    {
        Assert.Equal(GlassesAction.RemovalInFlight,
                     Decide(shellBuilt: false, published: false, recorded: false, drawn: true, sinceRemoval: 800).Action);
        Assert.Equal(GlassesAction.Unclaimed,
                     Decide(shellBuilt: false, published: false, recorded: false, drawn: true, sinceRemoval: 6000).Action);
        Assert.Equal(GlassesAction.Unclaimed,
                     Decide(shellBuilt: false, published: false, recorded: false, drawn: true, sinceRemoval: null).Action);
    }

    [Fact]
    public void HostedSteadyStateIsNone() // [EDGE-face-glasses-priority-20]
        => Assert.Equal(GlassesAction.None, Decide(recorded: true, drawn: true, anyMetWorn: true).Action);

    [Fact]
    public void FeatureOffOrNothingWantedRemovesWithoutCooldownOrFailure() // [EDGE-face-glasses-priority-22]
    {
        foreach (var d in new[] { Decide(featureOn: false), Decide(gearWanted: false, shellBuilt: false, published: false) })
        {
            Assert.Equal(GlassesAction.Remove, d.Action);
            Assert.False(d.ChargeCooldown);
            Assert.False(d.FailureRemoval);
        }
    }

    [Fact]
    public void HostsToNameDropsOurRemovedGlasses() // [AC-face-glasses-priority-4] [EDGE-face-glasses-priority-21]
    {
        IReadOnlyList<string> failed =
        [
            "chara/equipment/e5521/model/c0201e5521_met.mdl",
            "chara/accessory/a0053/model/c0201a0053_rir.mdl",
        ];

        Assert.Equal(["chara/accessory/a0053/model/c0201a0053_rir.mdl"],
                     ShellNotDrawnNotice.HostsToName(failed, 5521, ourGlassesOn: false));
        Assert.Equal(failed, ShellNotDrawnNotice.HostsToName(failed, 5521, ourGlassesOn: true));
        Assert.Equal(failed, ShellNotDrawnNotice.HostsToName(failed, null, ourGlassesOn: false));
    }
}
