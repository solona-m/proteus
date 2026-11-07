using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Copy Logs waits for the retry and drawn-check lines that follow its refresh, up to a cap (#91).</summary>
public class LogExportSettleTests
{
    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-16")]
    [Trait("Plan", "EDGE-issue-sweep-d-37")]
    public async Task Nothing_pending_waits_only_the_base()
    {
        // [AC-issue-sweep-d-16] [EDGE-issue-sweep-d-37]
        var waits = new List<TimeSpan>();
        var extra = await LogExportService.SettleAsync(() => false, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20),
            t => { waits.Add(t); return Task.CompletedTask; });
        Assert.Equal(TimeSpan.Zero, extra);
        Assert.Equal([TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-16")]
    [Trait("Plan", "EDGE-issue-sweep-d-37")]
    public async Task Pending_waits_until_it_clears_and_never_past_the_cap()
    {
        // [AC-issue-sweep-d-16] [EDGE-issue-sweep-d-37]
        int polls = 0;
        var extra = await LogExportService.SettleAsync(() => ++polls <= 4, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20),
            _ => Task.CompletedTask);
        Assert.Equal(TimeSpan.FromSeconds(2), extra);

        var waits = new List<TimeSpan>();
        var capped = await LogExportService.SettleAsync(() => true, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20),
            t => { waits.Add(t); return Task.CompletedTask; });
        Assert.Equal(TimeSpan.FromSeconds(20), capped);
        Assert.Equal(TimeSpan.FromSeconds(23), waits.Aggregate(TimeSpan.Zero, (a, b) => a + b));
    }
}
