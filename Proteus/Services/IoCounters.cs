using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// How often the background paths touched the drive or ran, summarised into one Debug line at most once a minute, and
/// only when something was counted. Gives before/after numbers for a slow-drive report without a profiler. Thread-safe.
/// </summary>
public sealed class IoCounters
{
    public const string DiscoveryCalls      = "discovery calls";
    public const string PresenceStats       = SidecarCache.PresenceStatsKey;
    public const string MetadataReads       = SidecarCache.MetadataReadsKey;
    public const string BootProbes          = "boot probes";
    public const string GearVariantsRuns    = "gear variants runs";
    public const string GearShadersRuns     = "gear shaders runs";
    public const string BodyIndexRefreshes  = "body index refreshes";
    public const string BodyIndexDeferred   = "body index deferred";

    /// <summary>The order keys are written in; any other key follows, alphabetically.</summary>
    private static readonly string[] Order =
    [
        DiscoveryCalls, PresenceStats, MetadataReads, BootProbes,
        GearVariantsRuns, GearShadersRuns, BodyIndexRefreshes, BodyIndexDeferred,
    ];

    private readonly Func<long> nowMs;
    private readonly Action<string> sink;
    private readonly long intervalMs;
    private readonly ConcurrentDictionary<string, long> counts = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private long lastSummaryAt;

    public IoCounters(Action<string> sink, Func<long>? nowMs = null, long intervalMs = 60_000)
    {
        this.sink       = sink;
        this.nowMs      = nowMs ?? (() => Environment.TickCount64);
        this.intervalMs = intervalMs;
        lastSummaryAt   = this.nowMs();
    }

    public void Count(string key, int n = 1)
    {
        if (n <= 0) return;
        counts.AddOrUpdate(key, n, (_, v) => v + n);
        MaybeSummarise();
    }

    /// <summary>Write the summary when a minute has passed since the last one and something was counted. Cheap to call
    /// often: the time is checked before the lock is taken.</summary>
    public void MaybeSummarise()
    {
        long now = nowMs();
        if (now - Interlocked.Read(ref lastSummaryAt) < intervalMs) return;

        string line;
        lock (gate)
        {
            long since = now - lastSummaryAt;
            if (since < intervalMs) return;

            var taken = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var key in counts.Keys.ToList())
                if (counts.TryRemove(key, out var v) && v > 0) taken[key] = v;
            // Nothing counted: no line, and the next count may write one at once.
            if (taken.Count == 0) return;

            var parts = Order.Where(taken.ContainsKey).Select(k => $"{k}={taken[k]}")
                .Concat(taken.Keys.Except(Order).OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={taken[k]}"));
            line = $"[Proteus] idle I/O, last {since / 1000} s: {string.Join(", ", parts)}";
            Interlocked.Exchange(ref lastSummaryAt, now);
        }
        sink(line);
    }
}
