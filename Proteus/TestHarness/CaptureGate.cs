using System;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>Another screenshot is still being taken.</summary>
internal sealed class CaptureBusyException() : Exception("a screenshot is already being taken");

/// <summary>The screenshot did not complete in time (game minimised, rendering paused).</summary>
internal sealed class CaptureTimeoutException() : Exception("the screenshot did not complete in time");

/// <summary>
/// One screenshot at a time, each bounded: a capture that never completes (the game minimised or not rendering)
/// times out, frees the gate for the next one, and has its late result disposed when it finally arrives.
/// </summary>
internal sealed class CaptureGate(TimeSpan timeout)
{
    private int busy;

    public TimeSpan Timeout { get; } = timeout;

    /// <param name="capture">Takes the frame; honours the token.</param>
    /// <param name="disposeLate">Disposes a result that arrives after the timeout.</param>
    public async Task<T> Run<T>(Func<CancellationToken, Task<T>> capture, Action<T>? disposeLate = null)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new CaptureBusyException();
        using var cts = new CancellationTokenSource();
        try
        {
            var task = capture(cts.Token);
            var first = await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false);
            if (first != task)
            {
                cts.Cancel();
                _ = task.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) disposeLate?.Invoke(t.Result);
                    else _ = t.Exception;
                }, TaskScheduler.Default);
                throw new CaptureTimeoutException();
            }
            return await task.ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }
}
