using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// The invisible-carrier decisions, apart from the game and IPC so they can be tested (the pattern of
/// <c>GlassesRecord</c>). A user's log showed "asked: ril carrier on" every 5.25 s without end: each ask was taken on a
/// stale walk, raised a Glamourer equip, and that recomposite scheduled the next ask.
/// </summary>
internal static class CarrierDecisions
{
    /// <summary>At most this many asks in a row for a slot that stays undrawn.</summary>
    internal const int AskCap = 2;

    /// <summary>A walk taken this long after the slot's last ask counts as fresh evidence of what the game drew.</summary>
    internal const int SettleMs = 1500;

    /// <summary>A walk still showing our piece this soon after we took it off is our removal in flight, not a stranger's.</summary>
    internal const int RemovalGraceMs = 5000;

    internal enum EquipAction { None, Equip, RetryAfter, RewalkFirst, GaveUp }

    internal readonly record struct EquipDecision(EquipAction Action, int RetryMs = 0)
    {
        /// <summary>The slot is drawn: its consecutive-ask count resets.</summary>
        public bool Drawn { get; init; }
    }

    /// <summary>Should Proteus ask Glamourer for its carrier in this slot now?</summary>
    /// <param name="walkShowsOurs">The held walk shows our model in the slot.</param>
    /// <param name="walkFresh">That walk was taken at least <see cref="SettleMs"/> after the slot's last ask.</param>
    /// <param name="probeConfirmed">The drawn check confirmed the current shell after the slot's last ask and the last redraw.</param>
    /// <param name="snapshotKnown">A walk has happened at all.</param>
    /// <param name="slotOccupiedByOther">The walk shows another piece in the slot (the player's).</param>
    /// <param name="cooldownLeftMs">This slot's inject cooldown still to run.</param>
    /// <param name="consecutiveAsks">Asks since the slot was last seen drawn, this publish epoch.</param>
    public static EquipDecision Equip(bool walkShowsOurs, bool walkFresh, bool probeConfirmed, bool snapshotKnown,
                                      bool slotOccupiedByOther, long cooldownLeftMs, int consecutiveAsks)
    {
        if (probeConfirmed || walkShowsOurs) return new EquipDecision(EquipAction.None) { Drawn = true };
        if (!snapshotKnown || slotOccupiedByOther) return new EquipDecision(EquipAction.None);
        if (consecutiveAsks >= AskCap) return new EquipDecision(EquipAction.GaveUp);
        // The cooldown first: it outlasts the settle time, so a re-walk only ever happens at retry time and always
        // yields a fresh walk (a re-walk is a framework-thread resource-tree read, hundreds of ms on a user's machine).
        if (cooldownLeftMs > 0) return new EquipDecision(EquipAction.RetryAfter, (int)Math.Min(cooldownLeftMs, int.MaxValue));
        // A retry on a walk taken before the last ask could have loaded: look again before asking again.
        if (consecutiveAsks >= 1 && !walkFresh) return new EquipDecision(EquipAction.RewalkFirst);
        return new EquipDecision(EquipAction.Equip);
    }

    /// <summary>
    /// A drawn-check confirmation counts only when it is newer than the slot's last ask AND the last observed redraw:
    /// a game redraw (a zone change) reverts carriers, and the confirmation would otherwise outlive it.
    /// </summary>
    public static bool ConfirmationCounts(long confirmedAtTick, long lastAskTick, long lastRedrawTick)
        => confirmedAtTick > Math.Max(lastAskTick, lastRedrawTick);

    internal enum SweepAction { Leave, Remove, ForgetRecord, RemovalInFlight, Unclaimed }

    /// <summary>A whole-build removal is allowed: nothing hosted, or the shell built and went elsewhere; never a failed build.</summary>
    public static bool MayRelease(bool gearWanted, bool shellBuilt) => !(gearWanted && !shellBuilt);

    /// <summary>What to do with a carrier slot this composite does not host on.</summary>
    /// <param name="inUse">The build still hosts on it.</param>
    /// <param name="walkShowsOurs">The held walk shows our model there.</param>
    /// <param name="recorded">Proteus recorded equipping it.</param>
    /// <param name="glamourerSlot">Glamourer's item in the slot; null when unread or unreadable.</param>
    /// <param name="ourItemId">Our carrier item for the slot.</param>
    /// <param name="walkAfterReadShowsOurs">A valid walk taken after that read; null when there is none.</param>
    /// <param name="msSinceOurRemoval">Since Proteus last took its carrier off this slot; null when it has not.</param>
    public static SweepAction Sweep(bool inUse, bool walkShowsOurs, bool recorded, ulong? glamourerSlot, ulong ourItemId,
                                    bool? walkAfterReadShowsOurs, long? msSinceOurRemoval)
    {
        if (inUse) return SweepAction.Leave;
        if (recorded)
        {
            if (walkShowsOurs) return SweepAction.Remove;
            if (glamourerSlot is not { } held) return SweepAction.Leave;          // unread or unreadable: keep the record
            if (held == ourItemId) return SweepAction.Remove;                     // undrawn but on: take it off
            if (GlamourerItemIds.IsEmpty(held))
                return walkAfterReadShowsOurs switch
                {
                    false => SweepAction.ForgetRecord,                            // gone, and a later walk agrees
                    true  => SweepAction.Remove,
                    null  => SweepAction.Leave,                                   // a mid-redraw reading: look again
                };
            return SweepAction.ForgetRecord;                                      // the player put their own on
        }
        if (!walkShowsOurs) return SweepAction.Leave;
        if (msSinceOurRemoval is { } since && since >= 0 && since <= RemovalGraceMs) return SweepAction.RemovalInFlight;
        return SweepAction.Unclaimed;
    }

    /// <summary>
    /// A post-read walk counts only if it is a real walk: the same rule <c>ApplyEquippedModels</c> uses — null, empty, or
    /// with no body-slot model is a mid-redraw teardown and says nothing.
    /// </summary>
    public static bool IsValidWalk(IReadOnlyCollection<string>? models, Func<IReadOnlyCollection<string>, bool> hasBodySlotModel)
        => models is { Count: > 0 } m && hasBodySlotModel(m);
}

/// <summary>Consecutive asks per carrier slot, counted atomically: two reconciles can run at once.</summary>
internal sealed class CarrierAskCounter
{
    private sealed class Entry { public long Epoch; public int Count; public long LastAskTick; }
    private readonly ConcurrentDictionary<string, Entry> slots = new(StringComparer.OrdinalIgnoreCase);

    private Entry For(string slot) => slots.GetOrAdd(slot, _ => new Entry());

    /// <summary>Count an ask if the cap allows it; a new epoch starts the count again. Atomic per slot.</summary>
    public bool TryBeginAsk(string slot, long epoch, int cap, long nowTick)
    {
        var e = For(slot);
        lock (e)
        {
            if (e.Epoch != epoch) { e.Epoch = epoch; e.Count = 0; }
            if (e.Count >= cap) return false;
            e.Count++;
            e.LastAskTick = nowTick;
            return true;
        }
    }

    public int Count(string slot, long epoch)
    {
        var e = For(slot);
        lock (e) return e.Epoch == epoch ? e.Count : 0;
    }

    /// <summary>The tick of the slot's last ask; 0 when never asked.</summary>
    public long LastAskTick(string slot)
    {
        var e = For(slot);
        lock (e) return e.LastAskTick;
    }

    /// <summary>The ask never reached the player (the write failed): it does not count against the cap.</summary>
    public void Undo(string slot)
    {
        var e = For(slot);
        lock (e) if (e.Count > 0) e.Count--;
    }

    /// <summary>The slot was seen drawn.</summary>
    public void Reset(string slot)
    {
        var e = For(slot);
        lock (e) e.Count = 0;
    }
}

/// <summary>An inject cooldown per carrier slot, so taking one host's carrier off never delays the next host's.</summary>
internal sealed class CarrierCooldowns
{
    private readonly ConcurrentDictionary<string, long> ticks = new(StringComparer.OrdinalIgnoreCase);

    public void Charge(string slot, long nowTick) => ticks[slot] = nowTick;

    public long LeftMs(string slot, long nowTick, int windowMs)
    {
        if (!ticks.TryGetValue(slot, out var at)) return 0;
        var since = unchecked(nowTick - at);
        return since < 0 || since > windowMs ? 0 : windowMs - since;
    }
}

/// <summary>
/// One pending retry for all carriers, coalesced to the EARLIEST deadline asked for (per-slot cooldowns end at
/// different times). An earlier request re-arms; a later one is absorbed; a superseded timer fires as a no-op.
/// </summary>
internal sealed class CoalescingRetry
{
    private readonly object sync = new();
    private long deadline = long.MaxValue;
    private int generation;
    private readonly HashSet<string> slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ask for a retry at <paramref name="atTick"/>. True (with the generation to arm) when a timer must be armed.</summary>
    public bool Request(string slot, long atTick, out int armGeneration)
    {
        lock (sync)
        {
            slots.Add(slot);
            if (atTick >= deadline) { armGeneration = generation; return false; }
            deadline = atTick;
            armGeneration = ++generation;
            return true;
        }
    }

    /// <summary>A timer fired. True when it is the current one; the pending state is cleared then.</summary>
    public bool Fire(int armGeneration, out IReadOnlyCollection<string> firedSlots)
    {
        lock (sync)
        {
            if (armGeneration != generation || deadline == long.MaxValue) { firedSlots = []; return false; }
            firedSlots = [.. slots];
            slots.Clear();
            deadline = long.MaxValue;
            return true;
        }
    }

    public bool PendingFor(string slot)
    {
        lock (sync) return deadline != long.MaxValue && slots.Contains(slot);
    }

    public long Deadline { get { lock (sync) return deadline; } }
}

/// <summary>
/// The drawn check's own re-check for a shell whose carrier host is not drawn, now that no echo composite re-checks it.
/// Timed from the slot's last ask; the deferral is decided before any warning, so a retry still owed or still loading
/// never reads as a failure.
/// </summary>
internal static class ShellRecheck
{
    internal enum Verdict { Done, Schedule, Warn, Info }

    /// <summary>How long until the slot's last ask has had its cooldown and settle time.</summary>
    public static long DelayMs(long nowTick, long lastAskTick, int cooldownMs, int settleMs)
        => Math.Max(0, lastAskTick + cooldownMs + settleMs - nowTick);

    /// <summary>
    /// When to run the next re-check after a deferral. A pending retry asks only after it fires AND, on a stale walk,
    /// re-walks the character (hundreds of ms on a slow machine), so its ask lands after the deadline: wait a full
    /// cooldown + settle past the deadline plus one more settle of margin. A retry owed but not pending: at least a
    /// full cooldown + settle from now. Otherwise from the slot's last ask.
    /// </summary>
    public static long NextDelayMs(long nowTick, long lastAskTick, long retryDeadlineTick, bool retryOwedNotPending,
                                   int cooldownMs, int settleMs)
    {
        long delay = DelayMs(nowTick, lastAskTick, cooldownMs, settleMs);
        if (retryDeadlineTick != long.MaxValue)
            delay = Math.Max(delay, retryDeadlineTick - nowTick + cooldownMs + settleMs + settleMs);
        if (retryOwedNotPending) delay = Math.Max(delay, cooldownMs + settleMs);
        return delay;
    }

    /// <param name="recheck">1 for the first re-check, 2 for the second (the last).</param>
    public static Verdict Next(int recheck, long msSinceSlotLastAsk, bool retryPendingForSlot, bool asksBelowCap,
                               bool hostGone, int cooldownMs, int settleMs)
    {
        if (!hostGone) return Verdict.Done;
        bool defer = msSinceSlotLastAsk < cooldownMs + settleMs || retryPendingForSlot || asksBelowCap;
        if (defer) return recheck <= 1 ? Verdict.Schedule : Verdict.Info;
        return Verdict.Warn;
    }
}
