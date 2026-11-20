#if PROTEUS_TEST_HARNESS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>The pubes override: −1 none, 0 off, 1 on.</summary>
    private volatile int harnessPubesOverride = -1;

    /// <summary>The session override of <see cref="Configuration.AutoRefitPubes"/>: null for none (the user's setting
    /// decides). Never saved.</summary>
    internal bool? HarnessPubesOverride
    {
        get => harnessPubesOverride switch { 1 => true, 0 => false, _ => null };
        set => harnessPubesOverride = value switch { true => 1, false => 0, null => -1 };
    }

    partial void HarnessPubes(ref bool pubes)
    {
        int v = harnessPubesOverride;
        if (v >= 0) pubes = v == 1;
    }

    partial void HarnessNoteRefit(string modDir)
    {
        if (harnessOverride >= 0 || harnessPubesOverride >= 0) harnessRefits.TryAdd(modDir, 0);
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

    /// <summary>
    /// "Refit what I'm wearing now" for the pieces drawn from one mod alone — the test harness's (see
    /// <c>AutoRefitNowJob</c>): each piece goes through the walk's own checks (<see cref="WalkSlot"/>) as forced, and
    /// nothing worn from any other mod is touched. Never arms <see cref="RunNow"/>'s walk. Framework thread.
    /// </summary>
    /// <returns>Per worn piece of that mod, its slot, game path and outcome ("enqueued" or "not-enqueued:&lt;reason&gt;");
    /// one "-" row naming the reason when nothing of it could be looked at; null when auto refit cannot run now.</returns>
    internal IReadOnlyList<(string Slot, string GamePath, string Outcome)>? RunNowFor(string modRoot)
    {
        var activity = Activity();
        if (disposed || !EffectivelyEnabled || current.Busy || activity.Running || activity.Queued > 0 || activity.Undoing)
            return null;
        var models = penumbra.GetActivePlayerModelPaths();
        if (!DrawnModelPaths.HasBodySlotModel(models)) return [("-", "", "not-enqueued:blank-models")];
        if (penumbra.GetPlayerCollection() is not { } collection) return [("-", "", "not-enqueued:no-collection")];
        if (PreferenceOf(collection.Id) is not { } pref || !pref.HasAnyBody())
            return [("-", "", "not-enqueued:no-preferred-body")];

        var now = ReadWorn(models, penumbra.GetModDirectory(), out var resolvedOf);
        string want = Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        long tick = Environment.TickCount64;
        var outcomes = new List<(string, string, string)>();
        foreach (var (slot, worn) in now)
        {
            if (resolvedOf[slot].Root is not { } root
                || !string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                  want, StringComparison.OrdinalIgnoreCase))
                continue;
            string outcome = WalkSlot(slot, now, resolvedOf, pref, collection.Id, all: true, tick);
            outcomes.Add((slot, worn.GamePath, outcome == "enqueued" ? outcome : "not-enqueued:" + outcome));
        }
        return outcomes;
    }
}
#endif
