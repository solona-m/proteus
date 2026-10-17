using System;
using System.Diagnostics;
using System.Threading;
using Dalamud.Plugin.Services;

namespace Proteus;

/// <summary>
/// When and where <see cref="Configuration.Save"/> writes. The write serialises every collection on the config, so it
/// runs only on the framework thread — where the GUI and the auto refit change those collections in place — and inside
/// <see cref="Configuration.Gate"/>, which the compositor's workers hold while they change theirs. A save asked for on
/// any other thread is posted to the framework, once per window however many arrive, and never waited for: a worker
/// that holds the gate and waits on the framework would deadlock with a framework save waiting on the gate.
/// <para/>
/// A save never throws at its caller. A player's auto refit reported "Couldn't refit" for a piece it had already
/// switched on, because the save in the same step threw. A failed write stays pending and goes out with the next save,
/// or at unload; it is not retried by itself, which on a lasting disk error would log every frame.
/// </summary>
internal sealed class ConfigSaveScheduler
{
    /// <summary>A write, or a wait for the gate, slower than this is logged, the two apart.</summary>
    internal const long SlowWriteMs = 8;

    /// <summary>A lasting failure, or a lasting slow write, logs once per this long; the saves between log at Debug.</summary>
    private const long ErrorEveryMs = 60_000;

    private readonly object gate;
    private readonly Action write;
    private readonly Func<bool> onFrameworkThread;
    private readonly Action<Action> post;
    private readonly IPluginLog log;

    private bool pending;            // under gate
    private int posted;              // Interlocked: a callback is queued and has not started
    private volatile bool closed;
    private long lastErrorAt = long.MinValue;
    private long lastSlowAt = long.MinValue;

    public ConfigSaveScheduler(object gate, Action write, Func<bool> onFrameworkThread, Action<Action> post,
                               IPluginLog log)
    {
        this.gate = gate;
        this.write = write;
        this.onFrameworkThread = onFrameworkThread;
        this.post = post;
        this.log = log;
    }

    /// <summary>Something is changed and not yet on disk (a deferred save, or a write that failed).</summary>
    public bool Pending { get { lock (gate) return pending; } }

    public void Save(string caller = "")
    {
        if (closed)
        {
            log.Debug("[Proteus] settings save from {0} after unload — dropped", caller);
            return;
        }
        if (onFrameworkThread())
        {
            Write(caller, onlyIfPending: false);
            return;
        }

        lock (gate) pending = true;
        if (Interlocked.CompareExchange(ref posted, 1, 0) != 0) return;
        log.Debug("[Proteus] settings save from {0} deferred to the framework thread", caller);
        try { post(OnTick); }
        catch (Exception ex)
        {
            Volatile.Write(ref posted, 0);
            log.Warning(ex, "[Proteus] settings save from {0} could not be handed to the framework thread; kept for "
                          + "the next save", caller);
        }
    }

    /// <summary>At unload: nothing posted runs any more, and what is pending is written now.</summary>
    public void FlushAndClose()
    {
        // Closed first, so nothing can be queued between the flush and the close.
        closed = true;
        Write("unload", onlyIfPending: true);
    }

    private void OnTick()
    {
        // Cleared before the write, so a save arriving during it posts again rather than being lost.
        Volatile.Write(ref posted, 0);
        if (closed) return;
        Write("deferred", onlyIfPending: true);
    }

    private void Write(string caller, bool onlyIfPending)
    {
        var clock = Stopwatch.StartNew();
        long waitMs, writeMs;
        Exception? failure = null;
        lock (gate)
        {
            waitMs = clock.ElapsedMilliseconds;
            if (onlyIfPending && !pending) return;
            pending = false;
            try { write(); }
            catch (Exception ex)
            {
                failure = ex;
                pending = true;
            }
            writeMs = clock.ElapsedMilliseconds - waitMs;
        }

        // Logged outside the gate: a worker waiting on it must not wait on the log as well.
        if (failure != null)
        {
            long now = Environment.TickCount64;
            if (lastErrorAt == long.MinValue || now - lastErrorAt >= ErrorEveryMs)
            {
                lastErrorAt = now;
                log.Error(failure, "[Proteus] settings save failed ({0}); kept for the next save", caller);
            }
            else log.Debug("[Proteus] settings save failed again ({0}): {1}", caller, failure.Message);
        }
        if (writeMs > SlowWriteMs || waitMs > SlowWriteMs)
        {
            long now = Environment.TickCount64;
            if (lastSlowAt == long.MinValue || now - lastSlowAt >= ErrorEveryMs)
            {
                lastSlowAt = now;
                log.Warning("[Proteus] settings save took {0} ms ({1}; waited {2} ms for the gate)",
                            writeMs, caller, waitMs);
            }
            else log.Debug("[Proteus] settings save took {0} ms ({1}; waited {2} ms for the gate)", writeMs, caller, waitMs);
        }
    }
}
