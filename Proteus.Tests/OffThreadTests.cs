using System;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A refit hops to the framework thread for a moment and must come back to a worker. testing-569 and 570 awaited the
/// hop with <c>ConfigureAwaitOptions.ForceYielding</c>, which yields only for a task already complete — a hop completes a
/// frame later, so every refit after its first hop ran inside the game's frame. Here a dedicated thread stands in for the
/// framework thread.
/// </summary>
public class OffThreadTests
{
    /// <summary>A task completed later, on a thread of its own, as Dalamud completes a framework hop.</summary>
    private static (Task<int> Task, Func<int> ThreadId) CompletedLaterElsewhere(Action<TaskCompletionSource<int>> finish)
    {
        var source = new TaskCompletionSource<int>();
        int id = 0;
        var thread = new Thread(() =>
        {
            id = Environment.CurrentManagedThreadId;
            Thread.Sleep(100);
            finish(source);
        }) { IsBackground = true };
        thread.Start();
        return (source.Task, () => id);
    }

    [Fact]
    public async Task The_caller_carries_on_off_the_thread_that_completed_the_hop()
    {
        // Inside Task.Run: no synchronization context, as on the refit's worker — xUnit's own would carry the test back
        // to its thread whatever the helper did.
        var (result, after, completer) = await Task.Run(async () =>
        {
            var (task, completer) = CompletedLaterElsewhere(s => s.SetResult(7));
            int result = await AutoRefitWatcher.OffThread(task);
            return (result, Environment.CurrentManagedThreadId, completer);
        });

        Assert.Equal(7, result);
        Assert.NotEqual(completer(), after);
    }

    [Fact]
    public async Task ForceYielding_alone_does_not_leave_that_thread()
    {
        // The defect itself, so that this test fails should the runtime ever change and make the helper unnecessary.
        var (after, completer) = await Task.Run(async () =>
        {
            var (task, completer) = CompletedLaterElsewhere(s => s.SetResult(7));
            await task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            return (Environment.CurrentManagedThreadId, completer);
        });

        Assert.Equal(completer(), after);
    }

    [Fact]
    public async Task A_failed_hop_throws_its_own_exception()
    {
        var (task, _) = CompletedLaterElsewhere(s => s.SetException(new OperationCanceledException("auto refit disposed")));

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => AutoRefitWatcher.OffThread(task));
        Assert.Equal("auto refit disposed", thrown.Message);
    }
}
