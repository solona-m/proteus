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
[Collection(Proteus.Tests.TestHarness.HarnessTimingCollection.Name)]
public class OffThreadTests
{
    /// <summary>
    /// A task completed later, on a thread of its own, as Dalamud completes a framework hop. The thread stays alive until
    /// the test has compared ids (dispose the handle): .NET hands a finished thread's managed id to the next thread made,
    /// so under load a pool thread could carry the completer's id and the comparison would fail for nothing. It completes
    /// only after the caller says <c>go</c> (just before it awaits), then 100 ms later: a caller that started late can
    /// never find the task already complete, which is how this flaked under load (#49). The wait is bounded.
    /// </summary>
    private static (Task<int> Task, Func<int> ThreadId, IDisposable Alive, Action Go) CompletedLaterElsewhere(
        Action<TaskCompletionSource<int>> finish)
    {
        var source = new TaskCompletionSource<int>();
        var release = new ManualResetEventSlim();
        var go = new ManualResetEventSlim();
        int id = 0;
        var thread = new Thread(() =>
        {
            id = Environment.CurrentManagedThreadId;
            go.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(100);
            finish(source);
            release.Wait(TimeSpan.FromSeconds(30));
        }) { IsBackground = true };
        thread.Start();
        return (source.Task, () => id, new Releaser(release), go.Set);
    }

    private sealed class Releaser(ManualResetEventSlim release) : IDisposable
    {
        public void Dispose() => release.Set();
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-4")]
    public async Task The_caller_carries_on_off_the_thread_that_completed_the_hop()
    {
        // Inside Task.Run: no synchronization context, as on the refit's worker — xUnit's own would carry the test back
        // to its thread whatever the helper did.
        var (result, after, completer, alive) = await Task.Run(async () =>
        {
            var (task, completer, alive, go) = CompletedLaterElsewhere(s => s.SetResult(7));
            go();
            int result = await AutoRefitWatcher.OffThread(task);
            return (result, Environment.CurrentManagedThreadId, completer, alive);
        });

        using (alive)
        {
            Assert.Equal(7, result);
            Assert.NotEqual(completer(), after);
        }
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-a-4")]
    public async Task ForceYielding_alone_does_not_leave_that_thread()
    {
        // The defect itself, so that this test fails should the runtime ever change and make the helper unnecessary.
        var (after, completer, alive) = await Task.Run(async () =>
        {
            var (task, completer, alive, go) = CompletedLaterElsewhere(s => s.SetResult(7));
            go();
            await task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            return (Environment.CurrentManagedThreadId, completer, alive);
        });

        using (alive) Assert.Equal(completer(), after);
    }

    [Fact]
    public async Task A_failed_hop_throws_its_own_exception()
    {
        var (task, _, alive, go) = CompletedLaterElsewhere(s => s.SetException(new OperationCanceledException("auto refit disposed")));
        using var keep = alive;
        go();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => AutoRefitWatcher.OffThread(task));
        Assert.Equal("auto refit disposed", thrown.Message);
    }
}
