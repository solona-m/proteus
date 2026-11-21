using System;

namespace Proteus.Services;

/// <summary>
/// How much of the configured decode-cache budget the machine can afford right now. Pure, so every rule is testable.
/// <para/>
/// What the cache could use without taking memory from anyone else is what is free (the lesser of physical memory and
/// commit) plus what the cache already holds plus memory its process holds but isn't using: arrays it evicted that
/// no gen2 has collected yet, and space the GC kept committed after its last collection (less what the cache has
/// put into it since). The cache takes half of that, never more than configured and never less than
/// min(configured, <see cref="FloorBytes"/>), so a composite's working set still fits.
/// </summary>
internal static class DecodeCacheMemoryPolicy
{
    /// <summary>The lowest the free-memory cap goes (it never goes below what the user configured, if that's lower).
    /// 1 GiB, not 2: a 16 GB machine's default is 2048 MB, and a 2 GiB floor would never cap it.</summary>
    public const long FloorBytes = 1024L << 20;

    /// <summary>The smallest move the cap makes between its ends, so free-memory jitter never trims.</summary>
    public const long QuantumBytes = 256L << 20;

    /// <summary>The budget the machine can afford, before hysteresis. Null <paramref name="freeBytes"/> (unknown) is
    /// the configured budget.</summary>
    public static long Target(long configured, long? freeBytes, long cacheHeld, long evictedSinceGen2,
                              long gcCommittedUnused, long addedSinceGcSnapshot, long floorBytes = FloorBytes)
    {
        configured = Math.Max(0, configured);
        if (freeBytes is not { } free) return configured;
        long unused = Math.Max(0, Math.Max(0, gcCommittedUnused) - Math.Max(0, addedSinceGcSnapshot));
        long controllable = Add(Add(Add(Math.Max(0, free), Math.Max(0, cacheHeld)), Math.Max(0, evictedSinceGen2)), unused);
        long lo = Math.Min(configured, Math.Max(0, floorBytes));
        return Math.Clamp(controllable / 2, lo, configured);
    }

    /// <summary>
    /// The next effective budget from the current one. A configured change (and the first refresh) takes the target
    /// at once; a target at either end is always reached; between the ends the budget moves only by a whole quantum.
    /// </summary>
    public static long Next(long current, long target, long configured, bool configuredChanged,
                            long floorBytes = FloorBytes, long quantumBytes = QuantumBytes)
    {
        configured = Math.Max(0, configured);
        long lo = Math.Min(configured, Math.Max(0, floorBytes));
        long t = Math.Clamp(target, lo, configured);
        if (configuredChanged) return t;
        if (t == configured || t == lo) return t;
        long cur = Math.Clamp(current, lo, configured);
        long gap = t > cur ? t - cur : cur - t;
        return gap >= quantumBytes ? t : cur;
    }

    private static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>Where the decode cache reads memory and GC state from; the live OS by default, fakes in tests.</summary>
internal sealed record DecodeCacheMemorySeams(Func<PhysicalMemoryStatus?> ReadMemory, Func<GcSnapshot> ReadGc,
                                              long FloorBytes, long QuantumBytes)
{
    public static readonly DecodeCacheMemorySeams Live =
        new(PhysicalMemory.Read, GcState.Read, DecodeCacheMemoryPolicy.FloorBytes, DecodeCacheMemoryPolicy.QuantumBytes);
}
