using System.Threading;

namespace Proteus.Services;

/// <summary>
/// One debounce slot: hands out a cancellation token for a new run and cancels the run it replaced.
/// <para/>
/// Exists because the obvious hand-rolled version is racy, and the race took Proteus down on install.
/// Creating the source under a lock is not enough — the token must be READ under the same lock:
/// <code>
///   lock (gate) { current?.Cancel(); current?.Dispose(); cts = current = new(); }
///   var token = cts.Token;   // another thread already disposed cts → ObjectDisposedException
/// </code>
/// <see cref="CancellationTokenSource.Token"/> throws once the source is disposed, so a second caller
/// arriving in that window killed the first caller's token. Reached from the plugin constructor
/// (<c>TriggerRecomposite("startup")</c>), where an escaping exception means the plugin fails to
/// construct and Dalamud reports the install/update as failed.
/// </summary>
internal sealed class DebounceGate
{
    private readonly object gate = new();
    private CancellationTokenSource? current;
    private bool stopped;

    /// <summary>
    /// Cancels the in-flight run, if any, and returns the token governing the new one. After
    /// <see cref="Stop"/> the returned token is already cancelled, so callers bail through their
    /// normal cancellation path instead of needing a separate "are we torn down" check.
    /// </summary>
    public CancellationToken Next()
    {
        lock (gate)
        {
            if (stopped) return new CancellationToken(canceled: true);
            current?.Cancel();
            // Cancelled but deliberately NOT disposed. The token we return outlives this lock, and the
            // caller hands it to things that register with the source afterwards (Task.Delay(ms, token),
            // Task.Run(.., token)) — registering against a disposed source can throw, which would simply
            // move the crash off .Token and onto Task.Delay. Cancel() has already run and released the
            // registrations that existed; the source is collectable once the last token holder drops it.
            // One undisposed source per debounced trigger costs nothing.
            current = new CancellationTokenSource();
            return current.Token;   // read INSIDE the lock — this is the whole point of the class
        }
    }

    /// <summary>
    /// Cancels the in-flight run and refuses further ones. Idempotent; safe to call from Dispose while
    /// another thread is in <see cref="Next"/>.
    /// </summary>
    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            current?.Cancel();
            current = null;
        }
    }
}
