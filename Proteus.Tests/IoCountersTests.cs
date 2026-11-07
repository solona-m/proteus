using System.Collections.Generic;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The rate-limited idle I/O summary.</summary>
public class IoCountersTests
{
    private long now = 1_000;
    private readonly List<string> lines = [];

    private IoCounters Make() => new(lines.Add, () => now);

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-8")]
    [Trait("Plan", "EDGE-idle-disk-io-30")]
    public void NothingCounted_NoLine()
    {
        // [AC-idle-disk-io-8] [EDGE-idle-disk-io-30]
        var c = Make();
        now += 600_000;
        c.MaybeSummarise();
        Assert.Empty(lines);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-8")]
    [Trait("Plan", "EDGE-idle-disk-io-30")]
    public void Burst_AtMostOneLinePer60s_ThenReset()
    {
        // [AC-idle-disk-io-8] [EDGE-idle-disk-io-30]
        var c = Make();
        for (int i = 0; i < 1000; i++)
        {
            c.Count(IoCounters.PresenceStats);
            now += 100;   // 100 s in all
        }
        Assert.Single(lines);
        Assert.Contains("presence stats=", lines[0]);
        Assert.StartsWith("[Proteus] idle I/O, last 60 s:", lines[0]);

        now += 60_000;
        c.Count(IoCounters.BootProbes);
        Assert.Equal(2, lines.Count);
        // Counts reset after each line: the second holds only what came after the first.
        Assert.DoesNotContain("presence stats=1000", lines[1]);
        Assert.Contains("boot probes=1", lines[1]);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-8")]
    [Trait("Plan", "EDGE-idle-disk-io-30")]
    public void QuietTail_FlushedByMaybeSummarise()
    {
        // [AC-idle-disk-io-8] [EDGE-idle-disk-io-30]
        var c = Make();
        c.Count(IoCounters.DiscoveryCalls, 3);
        c.Count(IoCounters.GearVariantsRuns);
        Assert.Empty(lines);
        now += 61_000;
        c.MaybeSummarise();   // the idle timer
        Assert.Equal(["[Proteus] idle I/O, last 61 s: discovery calls=3, gear variants runs=1"], lines);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-8")]
    public void ManyCallsWithin60s_EmitNothing()
    {
        // [AC-idle-disk-io-8]
        var c = Make();
        now += 60_000;
        c.Count(IoCounters.MetadataReads);
        Assert.Single(lines);
        for (int i = 0; i < 10_000; i++)
        {
            c.Count(IoCounters.MetadataReads);
            c.MaybeSummarise();
        }
        now += 59_999;
        c.MaybeSummarise();
        Assert.Single(lines);
    }
}
