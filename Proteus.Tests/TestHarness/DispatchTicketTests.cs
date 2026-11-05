using System;
using System.Threading;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

[Collection(HarnessTimingCollection.Name)]
public class DispatchTicketTests
{
    [Fact]
    public async Task TimeoutBeforeStart_WorkNeverRuns()
    {
        // [AC-qa-harness-4] Abandoned work never runs, even once the framework resumes.
        using var d = new FakeFrameworkDispatcher();
        d.Stall();
        int ran = 0;
        await Assert.ThrowsAsync<FrameworkTimeoutException>(() => d.RunAsync(() => Interlocked.Increment(ref ran), TimeSpan.FromMilliseconds(100)));
        d.Resume();
        await d.RunAsync(() => 0, TimeSpan.FromSeconds(5));   // drains the queue behind it
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task StartedBeforeTimeout_WaiterGetsResult()
    {
        // [EDGE-qa-harness-20] Work that started in time is waited for and returns its result.
        // Started late (600 ms into a 2 s start window), it runs past the window's end (to ~2.2 s) but within the
        // extra window running work gets. Wide margins: a loaded machine can start the work late (#49).
        using var d = new FakeFrameworkDispatcher();
        d.Stall();
        var task = d.RunAsync(() => { Thread.Sleep(1600); return 42; }, TimeSpan.FromSeconds(2));
        await Task.Delay(600);
        d.Resume();
        Assert.Equal(42, await task);
    }

    [Fact]
    public async Task RunsOnTheFrameworkThread()
    {
        using var d = new FakeFrameworkDispatcher();
        Assert.Equal(d.ThreadId, await d.RunAsync(() => Environment.CurrentManagedThreadId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RunUnabandonable_NeverAbandoned_SkipsOnlyWhenDisposing()
    {
        // [EDGE-qa-harness-21] However long the stall, it runs on resume; it skips only when told to by then.
        using var d = new FakeFrameworkDispatcher();
        d.Stall();
        int ran = 0;
        var task = d.RunUnabandonable(() => ran++, () => false);
        await Task.Delay(300);
        Assert.False(task.IsCompleted);
        d.Resume();
        Assert.True(await task);
        Assert.Equal(1, ran);

        bool disposing = false;
        d.Stall();
        var skipped = d.RunUnabandonable(() => ran++, () => disposing);
        disposing = true;
        d.Resume();
        Assert.False(await skipped);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Ticket_FirstTransitionWins()
    {
        var a = new DispatchTicket();
        Assert.True(a.TryStart());
        Assert.False(a.TryAbandon());
        var b = new DispatchTicket();
        Assert.True(b.TryAbandon());
        Assert.False(b.TryStart());
    }
}
