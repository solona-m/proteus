using System;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.Services;

/// <summary>A count of background checks or timers still to finish (Copy Logs waits on it; see LogExportService).</summary>
internal sealed class InFlightCounter
{
    private int n;
    public int Count => Volatile.Read(ref n);
    internal void Enter() => Interlocked.Increment(ref n);
    internal void Leave() => Interlocked.Decrement(ref n);
}

/// <summary>
/// Runs background work under an <see cref="InFlightCounter"/>, releasing it on every way out — superseded, disposed,
/// thrown — so a leaked count never leaves Copy Logs waiting its full cap for the rest of the session (#91).
/// </summary>
internal static class InFlight
{
    /// <summary>Counts <paramref name="body"/> while it runs.</summary>
    internal static async Task RunAsync(InFlightCounter c, Func<Task> body)
    {
        c.Enter();
        try { await body().ConfigureAwait(false); }
        finally { c.Leave(); }
    }

    /// <summary>
    /// Counts from now (scheduling) until done: on the pool, awaits <paramref name="delay"/>, then runs
    /// <paramref name="body"/> only if <paramref name="proceed"/> still says so. Exceptions are handed to
    /// <paramref name="onError"/> (never thrown into the pool).
    /// </summary>
    internal static Task ScheduleAsync(InFlightCounter c, Func<Task> delay, Func<bool> proceed, Func<Task> body,
                                       Action<Exception>? onError = null)
    {
        c.Enter();
        return Task.Run(async () =>
        {
            try
            {
                await delay().ConfigureAwait(false);
                if (proceed()) await body().ConfigureAwait(false);
            }
            catch (Exception ex) { onError?.Invoke(ex); }
            finally { c.Leave(); }
        });
    }
}
