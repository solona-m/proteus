using System;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>Raised to a request or job when the framework did not start its work in time; the work never runs.</summary>
internal sealed class FrameworkTimeoutException() : Exception("the game's framework did not run the work in time");

/// <summary>
/// One piece of framework work's fate. It moves Pending → Running when the framework starts it, or Pending →
/// Abandoned when the waiter gives up first; whichever happens first wins, so abandoned work never runs later
/// (a refit whose request timed out must not start a job behind QA's back).
/// </summary>
internal sealed class DispatchTicket
{
    private const int Pending = 0, Running = 1, Abandoned = 2;
    private int state;

    /// <summary>Called by the framework side as it starts: false means the waiter gave up and the work must not run.</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref state, Running, Pending) == Pending;

    /// <summary>Called by the waiter on timeout: false means the work already started (and will finish).</summary>
    public bool TryAbandon() => Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending;

    public bool Started => Volatile.Read(ref state) == Running;
}

/// <summary>Puts work on the game's framework thread — the only thread game state, Penumbra IPC and the compositor's
/// framework-side calls may be touched from.</summary>
internal interface IFrameworkDispatcher
{
    /// <summary>
    /// Post <paramref name="work"/> to the framework. Not started within <paramref name="startTimeout"/>: abandoned,
    /// never runs, and the returned task throws <see cref="FrameworkTimeoutException"/>. Started in time: waited for (up
    /// to <paramref name="startTimeout"/> again; work that overruns that is reported as a timeout but has run).
    /// For requests, and job steps that write nothing.
    /// </summary>
    Task<T> RunAsync<T>(Func<T> work, TimeSpan startTimeout, CancellationToken ct = default);

    /// <summary>
    /// Post <paramref name="work"/> and never abandon it: it runs whenever the framework gets to it, unless
    /// <paramref name="skip"/> says so by then (the harness disposing), in which case it does not run and the task
    /// completes with false. For the after-change following a write, which must not be dropped.
    /// </summary>
    Task<bool> RunUnabandonable(Action work, Func<bool> skip);

    /// <summary>Whether the caller is on the framework thread now (so framework work may run inline).</summary>
    bool OnFrameworkThread { get; }
}

/// <summary>The dispatcher on Dalamud's framework.</summary>
internal sealed class DalamudFrameworkDispatcher(Dalamud.Plugin.Services.IFramework framework) : IFrameworkDispatcher
{
    public bool OnFrameworkThread => framework.IsInFrameworkUpdateThread;

    public async Task<T> RunAsync<T>(Func<T> work, TimeSpan startTimeout, CancellationToken ct = default)
    {
        var ticket = new DispatchTicket();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = OffFramework(framework.RunOnFrameworkThread(() =>
        {
            if (!ticket.TryStart()) return default!;
            started.TrySetResult();
            return work();
        }));
        return await DispatchWait.Await(ticket, started.Task, run, startTimeout, ct).ConfigureAwait(false);
    }

    public Task<bool> RunUnabandonable(Action work, Func<bool> skip)
        => OffFramework(framework.RunOnFrameworkThread(() =>
        {
            if (skip()) return false;
            work();
            return true;
        }));

    /// <summary>
    /// The same outcome, completed on the thread pool: whatever awaits it (JSON shaping, the socket write, a job's
    /// next step) must never continue inline on the framework thread, however Dalamud completes its task.
    /// </summary>
    private static Task<T> OffFramework<T>(Task<T> onFramework)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        onFramework.ContinueWith(t =>
        {
            if (t.IsCanceled) result.TrySetCanceled();
            else if (t.IsFaulted) result.TrySetException(t.Exception!.InnerExceptions);
            else result.TrySetResult(t.Result);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return result.Task;
    }
}

/// <summary>The waiting half of <see cref="IFrameworkDispatcher.RunAsync"/>, shared with the test dispatcher.</summary>
internal static class DispatchWait
{
    internal static async Task<T> Await<T>(DispatchTicket ticket, Task started, Task<T> run, TimeSpan startTimeout,
                                           CancellationToken ct)
    {
        var first = await Task.WhenAny(started, run, Task.Delay(startTimeout, ct)).ConfigureAwait(false);
        if (first != started && first != run)
        {
            if (ticket.TryAbandon())
            {
                // Observe the posted task so its (default) result or a framework shutdown never surfaces unobserved.
                _ = run.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
                throw new FrameworkTimeoutException();
            }
            // It started just as we gave up: it will finish; wait for it like any other started work.
        }
        var done = await Task.WhenAny(run, Task.Delay(startTimeout, ct)).ConfigureAwait(false);
        if (done != run)
        {
            _ = run.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
            throw new FrameworkTimeoutException();
        }
        return await run.ConfigureAwait(false);
    }
}
