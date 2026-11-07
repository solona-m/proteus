using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>When the compositor's boot probe looks for Proteus mods to composite.</summary>
public class BootBackoffTests
{
    private const long T0 = 100_000;

    /// <summary>Report <paramref name="o"/> and return the delay to the next probe.</summary>
    private static long Step(BootBackoff b, BootProbeOutcome o, ref long now)
    {
        Assert.True(b.Due(now));
        b.Report(o, now);
        long delay = b.DelayFrom(now);
        Assert.False(b.Due(now + delay - 1));
        now += delay;
        Assert.True(b.Due(now));
        return delay;
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-1")]
    public void Idle_BacksOff_5_30_60_60()
    {
        // [AC-idle-disk-io-1]
        var b = new BootBackoff(T0);
        long now = T0;
        var delays = Enumerable.Range(0, 5).Select(_ => Step(b, BootProbeOutcome.Idle, ref now)).ToList();
        Assert.Equal([5_000L, 30_000, 60_000, 60_000, 60_000], delays);
        Assert.False(b.Latched);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-2")]
    [Trait("Plan", "EDGE-idle-disk-io-5")]
    public void Rearm_ResetsBackoff()
    {
        // [AC-idle-disk-io-2] [EDGE-idle-disk-io-5]
        var b = new BootBackoff(T0);
        long now = T0;
        for (int i = 0; i < 4; i++) Step(b, BootProbeOutcome.Idle, ref now);
        b.Rearm(now);
        Assert.True(b.Due(now));
        Assert.Equal(5_000, Step(b, BootProbeOutcome.Idle, ref now));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-2")]
    [Trait("Plan", "EDGE-idle-disk-io-5")]
    public void Composite_TriggersAndLatches()
    {
        // [AC-idle-disk-io-2] [EDGE-idle-disk-io-5]
        var b = new BootBackoff(T0);
        Assert.True(b.Report(BootProbeOutcome.Composite, T0));
        // Not latched until the caller has triggered: a trigger that throws leaves the probe armed.
        Assert.False(b.Latched);
        Assert.True(b.Due(T0 + BootBackoff.RetryMs));
        b.Latch();
        Assert.True(b.Latched);
        Assert.False(b.Due(T0 + 10_000_000));
        Assert.False(b.Report(BootProbeOutcome.Composite, T0 + 1));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-2")]
    [Trait("Plan", "EDGE-idle-disk-io-6")]
    public void Idle_NeverStops_NextProbeWithin60s()
    {
        // [AC-idle-disk-io-2] [EDGE-idle-disk-io-6]
        var b = new BootBackoff(T0);
        long now = T0;
        for (int i = 0; i < 200; i++)
            Assert.True(Step(b, BootProbeOutcome.Idle, ref now) <= 60_000);
        // The collection was reported late: the next probe finds the mods and composites.
        Assert.True(b.Report(BootProbeOutcome.Composite, now));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-1")]
    [Trait("Plan", "EDGE-idle-disk-io-2")]
    [Trait("Plan", "EDGE-idle-disk-io-34")]
    public void Retry_500msForFirst10s_ThenLadder_NoLatch()
    {
        // [EDGE-idle-disk-io-1] [EDGE-idle-disk-io-2] [EDGE-idle-disk-io-34]
        var b = new BootBackoff(T0);
        long now = T0;
        while (now - T0 < BootBackoff.FastRetryWindowMs)
            Assert.Equal(BootBackoff.RetryMs, Step(b, BootProbeOutcome.Retry, ref now));
        Assert.Equal(5_000, Step(b, BootProbeOutcome.Retry, ref now));
        Assert.Equal(30_000, Step(b, BootProbeOutcome.Retry, ref now));
        Assert.Equal(60_000, Step(b, BootProbeOutcome.Retry, ref now));
        Assert.False(b.Latched);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-34")]
    public void Rearm_RestoresFastRetry()
    {
        // [EDGE-idle-disk-io-34]
        var b = new BootBackoff(T0);
        long now = T0 + 20_000;
        Assert.Equal(5_000, Step(b, BootProbeOutcome.Retry, ref now));
        b.Rearm(now);
        Assert.Equal(BootBackoff.RetryMs, Step(b, BootProbeOutcome.Retry, ref now));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-34")]
    public void ResetBackoff_ResetsStep_KeepsLatch()
    {
        // [EDGE-idle-disk-io-34]
        var b = new BootBackoff(T0);
        long now = T0;
        for (int i = 0; i < 3; i++) Step(b, BootProbeOutcome.Idle, ref now);
        b.ResetBackoff(now);
        Assert.True(b.Due(now));
        Assert.Equal(BootBackoff.RetryMs, Step(b, BootProbeOutcome.Retry, ref now));
        Assert.Equal(5_000, Step(b, BootProbeOutcome.Idle, ref now));

        Assert.True(b.Report(BootProbeOutcome.Composite, now));
        b.Latch();
        b.ResetBackoff(now);
        Assert.True(b.Latched);
        Assert.False(b.Due(now + 1_000_000));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-34")]
    public void Rearm_ClearsLatch()
    {
        // [EDGE-idle-disk-io-34]
        var b = new BootBackoff(T0);
        b.Report(BootProbeOutcome.Composite, T0);
        b.Latch();
        b.Rearm(T0 + 5);
        Assert.False(b.Latched);
        Assert.True(b.Due(T0 + 5));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-4")]
    [Trait("Plan", "EDGE-idle-disk-io-5")]
    public void RearmBetweenCompositeAndLatch_NewArmGetsItsOwnComposite()
    {
        // [EDGE-idle-disk-io-4] [EDGE-idle-disk-io-5]
        // A zone change lands while the last arm's composite is being triggered: that latch must not swallow the new arm.
        var b = new BootBackoff(T0);
        Assert.True(b.Report(BootProbeOutcome.Composite, T0));
        b.Rearm(T0 + 10);
        b.Latch();
        Assert.False(b.Latched);
        Assert.True(b.Due(T0 + 10));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-5")]
    public void ConcurrentCalls_StateConsistent()
    {
        // [EDGE-idle-disk-io-5]
        var b = new BootBackoff(T0);
        Parallel.For(0, 20_000, i =>
        {
            // One clock reading for every thread: delays are measured from the time they were reported at.
            long now = T0 + 1_000;
            switch (i % 6)
            {
                case 0: b.Rearm(now); break;
                case 1: b.ResetBackoff(now); break;
                case 2: b.Report(BootProbeOutcome.Idle, now); break;
                case 3: b.Report(BootProbeOutcome.Retry, now); break;
                case 4: b.Due(now); break;
                default: Assert.InRange(b.DelayFrom(now), 0, 60_000); break;
            }
        });
        Assert.False(b.Latched);
        b.Rearm(T0 + 30_000);
        Assert.True(b.Due(T0 + 30_000));
        Assert.Equal(5_000, (b.Report(BootProbeOutcome.Idle, T0 + 30_000), b.DelayFrom(T0 + 30_000)).Item2);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-9")]
    public void Snapshot_ReportsLatchStepAndDelay()
    {
        // [AC-idle-disk-io-9]
        var b = new BootBackoff(T0);
        b.Report(BootProbeOutcome.Idle, T0);
        b.Report(BootProbeOutcome.Idle, T0 + 5_000);
        Assert.Equal((false, 2, 30_000L), b.Snapshot(T0 + 5_000));
        b.Report(BootProbeOutcome.Composite, T0 + 35_000);
        b.Latch();
        Assert.Equal((true, 2, 0L), b.Snapshot(T0 + 35_000));
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-9")]
    public void Snapshot_AfterRearm_Unlatched_StepZero()
    {
        // [AC-idle-disk-io-9]
        var b = new BootBackoff(T0);
        b.Report(BootProbeOutcome.Idle, T0);
        b.Report(BootProbeOutcome.Composite, T0 + 5_000);
        b.Latch();
        b.Rearm(T0 + 6_000);
        Assert.Equal((false, 0, 0L), b.Snapshot(T0 + 6_000));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-3")]
    public void Classify_SomeEnabledIncomplete_IsComposite()
    {
        // [EDGE-idle-disk-io-3]
        Assert.Equal(BootProbeOutcome.Composite, BootBackoff.Classify(1, complete: false));
        Assert.Equal(BootProbeOutcome.Composite, BootBackoff.Classify(3, complete: true));
        Assert.Equal(BootProbeOutcome.Idle, BootBackoff.Classify(0, complete: true));
        Assert.Equal(BootProbeOutcome.Retry, BootBackoff.Classify(0, complete: false));
    }
}
