using System;

namespace Proteus.Services;

/// <summary>
/// When the auto refit's body mod index is refreshed. Reading it means listing every mod folder (and, the first time,
/// parsing every manifest), so while auto refit is off a mod event only marks it stale, unless the Settings body list
/// is on screen. It is refreshed when someone needs it: the list is shown with nothing built, a button asks, or auto
/// refit is turned on. A refit itself refreshes inline when it finds none. Thread-safe.
/// </summary>
internal sealed class BodyIndexSchedule
{
    /// <summary>How recently the body list must have been drawn to count as on screen.</summary>
    internal const long ShownWindowMs = 1_000;
    /// <summary>How often the body list may ask for a refresh on its own: a refresh that keeps failing must not loop
    /// at frame rate.</summary>
    internal const long ShownAskThrottleMs = 3_000;

    internal enum Change { Scheduled, MarkStale }

    private readonly object gate = new();
    private long dueAt;                         // 0: nothing scheduled
    private long shownAt = long.MinValue / 2;
    private long lastShownAsk = long.MinValue / 2;
    private bool stale;

    /// <summary>Mods changed while nobody needed the index, and it has not been read since.</summary>
    public bool Stale { get { lock (gate) return stale; } }

    /// <summary>A mod changed. Scheduled after <paramref name="debounceMs"/> when auto refit is on or the list is on
    /// screen; otherwise the caller drops the index (<see cref="Change.MarkStale"/>).</summary>
    public Change Changed(long now, int debounceMs, bool enabled)
    {
        lock (gate)
        {
            if (enabled || now - shownAt <= ShownWindowMs)
            {
                dueAt = Math.Max(1, now + Math.Max(debounceMs, 1));
                return Change.Scheduled;
            }
            stale = true;
            return Change.MarkStale;
        }
    }

    /// <summary>The body list was drawn this frame. True when it has nothing to show and nothing is reading it, at most
    /// once every <see cref="ShownAskThrottleMs"/>: a refresh is then due now.</summary>
    public bool Shown(long now, bool built, bool refreshing)
    {
        lock (gate)
        {
            shownAt = now;
            if (built || refreshing || now - lastShownAsk < ShownAskThrottleMs) return false;
            lastShownAsk = now;
            dueAt = Math.Max(1, now);
            return true;
        }
    }

    /// <summary>Someone asked for the bodies now.</summary>
    public void Asked(long now)
    {
        lock (gate) dueAt = Math.Max(1, now);
    }

    /// <summary>Auto refit was turned on: refresh now if mods changed while it was off, or nothing was ever built.</summary>
    public bool Enabled(long now, bool built)
    {
        lock (gate)
        {
            if (!stale && built) return false;
            dueAt = Math.Max(1, now);
            return true;
        }
    }

    /// <summary>Whether a refresh is due now; taking it clears the schedule.</summary>
    public bool TakeDue(long now)
    {
        lock (gate)
        {
            if (dueAt == 0 || now < dueAt) return false;
            dueAt = 0;
            stale = false;
            return true;
        }
    }
}
