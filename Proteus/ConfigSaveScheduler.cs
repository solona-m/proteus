using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Proteus;

/// <summary>
/// When and where <see cref="Configuration.Save"/> writes. A save has two halves:
/// <list type="bullet">
/// <item><b>serialise</b> — reads every collection on the config, so it runs only on the framework thread (where the GUI
/// and the auto refit change those collections in place) and inside <see cref="Configuration.Gate"/>, which the
/// compositor's workers hold while they change theirs;</item>
/// <item><b>persist</b> — the disk write of that text, on one background writer (Dalamud's reliable storage), so the
/// frame never waits on the disk. A user's log showed 61 saves on the framework thread at 12–104 ms each.</item>
/// </list>
/// A save asked for on any other thread is posted to the framework, once per window however many arrive, and never
/// waited for: a worker that holds the gate and waits on the framework would deadlock with a framework save waiting on
/// the gate.
/// <para/>
/// The writer: one task at a time, the latest payload wins, every await <c>ConfigureAwait(false)</c> (it never posts to
/// the framework thread), its bookkeeping under <see cref="writerLock"/> and never under the gate. A write is issued only
/// while not closed, under the same lock that closes, so no write can start after <see cref="FlushAndClose"/>.
/// <para/>
/// A save never throws at its caller. A player's auto refit reported "Couldn't refit" for a piece it had already
/// switched on, because the save in the same step threw. A failed write stays pending and goes out with the next save,
/// or at unload; it is not retried by itself, which on a lasting disk error would log every frame.
/// </summary>
internal sealed class ConfigSaveScheduler
{
    /// <summary>A serialise, a disk write, or a wait for the gate, slower than this is logged, each apart.</summary>
    internal const long SlowWriteMs = 8;

    /// <summary>A lasting failure, or a lasting slow write, logs once per this long; the saves between log at Debug.</summary>
    private const long ErrorEveryMs = 60_000;

    private readonly object gate;
    private readonly Func<string?> serialize;               // framework thread, inside the gate; null = already written
    private readonly Func<string, Task>? persistAsync;      // background writer; null = legacy single-phase save
    private readonly Action persistSync;                    // unload: the full synchronous save, written last
    private readonly Func<bool> onFrameworkThread;
    private readonly Action<Action> post;
    private readonly IPluginLog log;

    private bool pending;            // under gate
    private int posted;              // Interlocked: a callback is queued and has not started
    private volatile bool closed;
    private long lastErrorAt = long.MinValue;
    private long lastSlowAt = long.MinValue;

    // The background writer. All of it under writerLock; the writer never takes the gate.
    private readonly object writerLock = new();
    private string? queuedJson;      // the newest payload not yet issued
    private Task? inFlight;          // the newest issued writer task (assigned when it is issued)
    private bool writerRunning;
    private bool writerClosed;
    private bool persistFailed;

    /// <summary>The legacy single-phase save (serialise and write in one call, on the framework thread).</summary>
    public ConfigSaveScheduler(object gate, Action write, Func<bool> onFrameworkThread, Action<Action> post,
                               IPluginLog log)
        : this(gate, () => { write(); return null; }, null, write, onFrameworkThread, post, log) { }

    /// <summary>The two-phase save: <paramref name="serialize"/> on the framework thread inside the gate, then
    /// <paramref name="persistAsync"/> on the background writer; <paramref name="persistSync"/> at unload.</summary>
    public ConfigSaveScheduler(object gate, Func<string?> serialize, Func<string, Task>? persistAsync,
                               Action persistSync, Func<bool> onFrameworkThread, Action<Action> post, IPluginLog log)
    {
        this.gate = gate;
        this.serialize = serialize;
        this.persistAsync = persistAsync;
        this.persistSync = persistSync;
        this.onFrameworkThread = onFrameworkThread;
        this.post = post;
        this.log = log;
    }

    /// <summary>Something is changed and not yet on disk (a deferred save, a queued write, or a write that failed).</summary>
    public bool Pending
    {
        get
        {
            lock (writerLock) if (persistFailed || queuedJson != null || writerRunning) return true;
            lock (gate) return pending;
        }
    }

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

    /// <summary>
    /// At unload: nothing posted runs any more, no background write starts, the one in flight is waited for (with no
    /// lock held — it needs neither the gate nor the framework thread), and then the full synchronous save runs under
    /// the gate, unconditionally, so the final state is written last.
    /// </summary>
    public void FlushAndClose()
    {
        // Closed first, so nothing can be queued between the flush and the close.
        closed = true;
        Task? wait;
        lock (writerLock)
        {
            writerClosed = true;
            queuedJson = null;
            wait = inFlight;
        }
        if (wait != null)
        {
            try { wait.GetAwaiter().GetResult(); }
            catch { /* its failure is logged by the writer; the synchronous save below covers it */ }
        }

        var clock = Stopwatch.StartNew();
        Exception? failure = null;
        lock (gate)
        {
            // Two-phase: unconditional, so the final state lands after any background write. The legacy single-phase
            // save has no background writer and keeps writing only what is pending.
            if (persistAsync == null && !pending) return;
            pending = false;
            try { persistSync(); }
            catch (Exception ex) { failure = ex; pending = true; }
        }
        if (failure != null) log.Error(failure, "[Proteus] settings save failed (unload)");
        else if (clock.ElapsedMilliseconds > SlowWriteMs)
            log.Debug("[Proteus] settings save took {0} ms (unload, synchronous)", clock.ElapsedMilliseconds);
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
        long waitMs, serializeMs;
        Exception? failure = null;
        string? json = null;
        lock (gate)
        {
            waitMs = clock.ElapsedMilliseconds;
            bool retryPersist;
            lock (writerLock) retryPersist = persistFailed;
            if (onlyIfPending && !pending && !retryPersist) return;
            pending = false;
            try { json = serialize(); }
            catch (Exception ex)
            {
                failure = ex;
                pending = true;
            }
            serializeMs = clock.ElapsedMilliseconds - waitMs;
        }

        if (json != null && persistAsync != null) Enqueue(json, caller);

        // Logged outside the gate: a worker waiting on it must not wait on the log as well.
        if (failure != null) LogFailure(failure, caller);
        if (serializeMs > SlowWriteMs || waitMs > SlowWriteMs)
            LogSlow(persistAsync != null ? "serialise" : "save", serializeMs, caller, waitMs);
    }

    /// <summary>Hands a payload to the background writer; starts it if idle. Nothing is issued once closed.</summary>
    private void Enqueue(string json, string caller)
    {
        lock (writerLock)
        {
            if (writerClosed) return;    // unload's synchronous save writes the final state
            queuedJson = json;
            if (writerRunning) return;   // the running writer picks up the newest payload
            writerRunning = true;
            // Assigned at issue time, under the lock, so FlushAndClose always waits on the newest write.
            inFlight = Task.Run(() => WriterLoop(caller));
        }
    }

    private async Task WriterLoop(string caller)
    {
        while (true)
        {
            string? json;
            lock (writerLock)
            {
                json = queuedJson;
                queuedJson = null;
                if (json == null || writerClosed)
                {
                    writerRunning = false;
                    return;
                }
            }

            var clock = Stopwatch.StartNew();
            try
            {
                await persistAsync!(json).ConfigureAwait(false);
                lock (writerLock) persistFailed = false;
            }
            catch (Exception ex)
            {
                lock (writerLock) persistFailed = true;
                LogFailure(ex, caller + ", background write");
            }
            // Off the framework thread it cannot hitch a frame: logged, but at Debug (QA: Warning noise in stutter logs).
            if (clock.ElapsedMilliseconds > SlowWriteMs)
                log.Debug("[Proteus] settings {0} took {1} ms ({2}; waited {3} ms for the gate)", "write",
                          clock.ElapsedMilliseconds, caller + ", off the framework thread", 0L);
        }
    }

    private void LogFailure(Exception failure, string caller)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref lastErrorAt);
        if (last == long.MinValue || now - last >= ErrorEveryMs)
        {
            Interlocked.Exchange(ref lastErrorAt, now);
            log.Error(failure, "[Proteus] settings save failed ({0}); kept for the next save", caller);
        }
        else log.Debug("[Proteus] settings save failed again ({0}): {1}", caller, failure.Message);
    }

    /// <summary>"settings serialise/write/save took N ms" — the halves are timed and logged apart.</summary>
    private void LogSlow(string what, long ms, string caller, long waitMs)
    {
        const string format = "[Proteus] settings {0} took {1} ms ({2}; waited {3} ms for the gate)";
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref lastSlowAt);
        if (last == long.MinValue || now - last >= ErrorEveryMs)
        {
            Interlocked.Exchange(ref lastSlowAt, now);
            log.Warning(format, what, ms, caller, waitMs);
        }
        else log.Debug(format, what, ms, caller, waitMs);
    }
}
