using System;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Background checks and timers release their count on every way out (#91: Copy Logs waits on it).</summary>
public class InFlightTests
{
    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-43")]
    public async Task A_superseded_schedule_leaves_the_count_at_zero()
    {
        // [EDGE-issue-sweep-d-43]
        var c = new InFlightCounter();
        bool ran = false;
        var t = InFlight.ScheduleAsync(c, () => Task.CompletedTask, () => false, () => { ran = true; return Task.CompletedTask; });
        await t;
        Assert.False(ran);
        Assert.Equal(0, c.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-43")]
    public async Task A_body_that_throws_leaves_the_count_at_zero()
    {
        // [EDGE-issue-sweep-d-43]
        var c = new InFlightCounter();
        Exception? seen = null;
        await InFlight.ScheduleAsync(c, () => Task.CompletedTask, () => true,
            () => throw new InvalidOperationException("boom"), ex => seen = ex);
        Assert.IsType<InvalidOperationException>(seen);
        Assert.Equal(0, c.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InFlight.RunAsync(c, () => throw new InvalidOperationException()));
        Assert.Equal(0, c.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-43")]
    public async Task An_early_return_leaves_the_count_at_zero()
    {
        // [EDGE-issue-sweep-d-43]
        var c = new InFlightCounter();
        await InFlight.RunAsync(c, () => Task.CompletedTask);
        Assert.Equal(0, c.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-43")]
    public async Task The_count_stands_from_scheduling_until_done()
    {
        // [EDGE-issue-sweep-d-43]
        var c = new InFlightCounter();
        var gate = new TaskCompletionSource();
        var t = InFlight.ScheduleAsync(c, () => gate.Task, () => true, () => Task.CompletedTask);
        Assert.Equal(1, c.Count);
        gate.SetResult();
        await t;
        Assert.Equal(0, c.Count);
    }
}
