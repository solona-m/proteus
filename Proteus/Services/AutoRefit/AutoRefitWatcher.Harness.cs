#if PROTEUS_TEST_HARNESS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// The test harness's session-only switch for auto refit, and its view of the body index (ADR 0002). The switch lives in
/// memory only: it is never written to <see cref="Configuration"/>, so no settings save can keep it, and it goes with the
/// watcher at unload. Compiled out of every CI build. Any thread.
/// </summary>
public sealed partial class AutoRefitWatcher
{
    /// <summary>−1 none, 0 off, 1 on.</summary>
    private volatile int harnessOverride = -1;

    /// <summary>Mods a refit wrote into while the override stood: writes put-back cannot undo (auto refit's Undo can).</summary>
    private readonly ConcurrentDictionary<string, byte> harnessRefits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The session override: null for none (the user's setting decides).</summary>
    internal bool? HarnessOverride
    {
        get => harnessOverride switch { 1 => true, 0 => false, _ => null };
        set => harnessOverride = value switch { true => 1, false => 0, null => -1 };
    }

    partial void HarnessEnabled(ref bool? on)
    {
        int v = harnessOverride;
        if (v >= 0) on = v == 1;
    }

    partial void HarnessNoteRefit(string modDir)
    {
        if (harnessOverride >= 0) harnessRefits.TryAdd(modDir, 0);
    }

    /// <summary>The mods refitted while the override stood, in name order; the list is emptied.</summary>
    internal IReadOnlyList<string> HarnessTakeRefitsWhileOverridden()
    {
        var mods = harnessRefits.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var m in mods) harnessRefits.TryRemove(m, out _);
        return mods;
    }

    /// <summary>The body index: built, stale (mods changed while nobody needed it), being read, and its generation.</summary>
    internal (bool Built, bool Stale, bool Refreshing, long Generation) HarnessBodyIndexState()
        => (Index.Current != null, indexSchedule.Stale, Volatile.Read(ref indexing) != 0, Index.Generation);
}
#endif
