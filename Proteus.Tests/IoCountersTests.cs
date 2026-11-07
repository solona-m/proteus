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
        // Counts reset after each line: the second holds only what came after the first, and the count that wrote it
        // opens the next window.
        Assert.Contains("presence stats=400", lines[1]);
        Assert.DoesNotContain("boot probes", lines[1]);
        now += 60_000;
        c.MaybeSummarise();
        Assert.Equal(3, lines.Count);
        Assert.Contains("boot probes=1", lines[2]);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-14")]
    [Trait("Plan", "EDGE-idle-disk-io-47")]
    public void Count_SummarisesBeforeAdding()
    {
        // [AC-idle-disk-io-14] [EDGE-idle-disk-io-47]
        var c = Make();
        c.Count(IoCounters.DiscoveryCalls);
        c.Count(IoCounters.PresenceStats, 50);
        now += 61_000;
        c.Count(IoCounters.DiscoveryCalls);
        c.Count(IoCounters.MetadataStats, 2);
        Assert.Equal(["[Proteus] idle I/O, last 61 s: discovery calls=1, presence stats=50"], lines);
        now += 60_000;
        c.MaybeSummarise();
        Assert.Equal("[Proteus] idle I/O, last 60 s: discovery calls=1, metadata stats=2", lines[1]);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-47")]
    public void EmptyCheck_RestartsWindow()
    {
        // [EDGE-idle-disk-io-47]
        var c = Make();
        now += 200_000;           // a long quiet stretch
        c.Count(IoCounters.BootProbes);
        Assert.Empty(lines);      // nothing to summarise: the window restarted at this count
        now += 59_000;
        c.MaybeSummarise();
        Assert.Empty(lines);
        now += 1_000;
        c.MaybeSummarise();
        Assert.Equal(["[Proteus] idle I/O, last 60 s: boot probes=1"], lines);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-13")]
    [Trait("Plan", "EDGE-idle-disk-io-46")]
    public void Snapshot_CopiesWithoutReset()
    {
        // [AC-idle-disk-io-13] [EDGE-idle-disk-io-46]
        var c = Make();
        c.Count(IoCounters.Redraws, 3);
        c.Count(IoCounters.RedrawTreeReads, 3);
        now += 20_000;
        var (age, counts) = c.Snapshot();
        Assert.Equal(20_000, age);
        Assert.Equal(3, counts[IoCounters.Redraws]);
        Assert.Equal(3, counts[IoCounters.RedrawTreeReads]);
        now += 41_000;
        c.MaybeSummarise();
        Assert.Equal(["[Proteus] idle I/O, last 61 s: redraws=3, redraw tree reads=3"], lines);
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
        c.Count(IoCounters.MetadataReads);
        now += 60_000;
        c.MaybeSummarise();
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
