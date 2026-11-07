using System;

namespace Proteus.Services;

/// <summary>What one boot probe found (<see cref="BootBackoff.Classify"/>).</summary>
internal enum BootProbeOutcome
{
    /// <summary>At least one Proteus mod is enabled: composite, then stop probing until the next arm.</summary>
    Composite,
    /// <summary>A complete answer with nothing enabled: look again later, less and less often.</summary>
    Idle,
    /// <summary>Penumbra could not answer yet: look again soon, then back off as for <see cref="Idle"/>.</summary>
    Retry,
}

/// <summary>
/// When the compositor's boot probe looks for Proteus mods to composite. It is armed whenever the character goes away
/// (logout, zone change) and latches once it has composited. With nothing to composite it used to look every 500 ms for
/// the whole session; now it backs off to 5 s, 30 s, then once a minute, and never stops, so a collection Penumbra
/// reports late is still found. An incomplete answer is retried every 500 ms for the first 10 s of an arm, then joins
/// the same ladder. Thread-safe: reported from the probe's worker, asked from the framework thread, reset from
/// Penumbra's events.
/// </summary>
internal sealed class BootBackoff
{
    internal static readonly long[] Steps = [5_000, 30_000, 60_000];
    internal const long RetryMs = 500;
    internal const long FastRetryWindowMs = 10_000;

    private readonly object gate = new();
    private long armedAt;
    private int step;
    private long nextDue;
    private bool latched;
    private long armGen;           // moved by every Rearm
    private long compositeGen = -1; // the arm a Composite was reported in, which Latch latches

    public BootBackoff(long now) => armedAt = now;

    public static BootProbeOutcome Classify(int enabled, bool complete)
        => enabled > 0 ? BootProbeOutcome.Composite
         : complete    ? BootProbeOutcome.Idle
         :               BootProbeOutcome.Retry;

    /// <summary>The character went away: probe again from the start, composite included.</summary>
    public void Rearm(long now)
    {
        lock (gate)
        {
            latched = false;
            armGen++;
            armedAt = now;
            step    = 0;
            nextDue = 0;
        }
    }

    /// <summary>The collection or Penumbra changed: probe again now and restart the ladder, but a latch stands.</summary>
    public void ResetBackoff(long now)
    {
        lock (gate)
        {
            armedAt = now;
            step    = 0;
            nextDue = 0;
        }
    }

    public bool Latched { get { lock (gate) return latched; } }

    /// <summary>Whether a probe should run now.</summary>
    public bool Due(long now)
    {
        lock (gate) return !latched && now >= nextDue;
    }

    /// <summary>
    /// Record a probe's outcome and set the next one. True means composite now, then call <see cref="Latch"/>: the latch
    /// comes after the trigger, so a trigger that throws leaves the probe armed.
    /// </summary>
    public bool Report(BootProbeOutcome outcome, long now)
    {
        lock (gate)
        {
            if (latched) return false;
            switch (outcome)
            {
                case BootProbeOutcome.Composite:
                    nextDue = now + RetryMs;
                    compositeGen = armGen;
                    return true;
                case BootProbeOutcome.Retry when now - armedAt < FastRetryWindowMs:
                    nextDue = now + RetryMs;
                    return false;
                default:
                    nextDue = now + Steps[Math.Min(step, Steps.Length - 1)];
                    if (step < Steps.Length) step++;
                    return false;
            }
        }
    }

    /// <summary>The composite was triggered: no more probes until the next <see cref="Rearm"/>. A re-arm since the
    /// <see cref="BootProbeOutcome.Composite"/> was reported wins: the new arm gets its own composite.</summary>
    public void Latch()
    {
        lock (gate)
            if (compositeGen == armGen) latched = true;
    }

    /// <summary>The latch, the ladder step and the time to the next probe, read together (the harness's <c>/state</c>).</summary>
    public (bool Latched, int Step, long NextDueInMs) Snapshot(long now)
    {
        lock (gate) return (latched, step, latched ? 0 : Math.Max(0, nextDue - now));
    }

    /// <summary>Milliseconds from <paramref name="now"/> to the next probe, for the log.</summary>
    public long DelayFrom(long now)
    {
        lock (gate) return Math.Max(0, nextDue - now);
    }
}
