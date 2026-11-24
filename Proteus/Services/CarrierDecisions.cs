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

    // ── The invisible-glasses carrier ────────────────────────────────────────────────────────────────
    // Unlike the Emperor's New pieces, the facewear carrier (Gold Eyepatch, Right) has a visible model of its own: worn
    // without our redirect it shows its real gold frames. So it is worn only while the shell is published on it AND
    // Penumbra serves that redirect, and comes off whenever the shell is not on it, failed builds included.

    /// <summary>Between polls for our facewear redirect to go live before an equip.</summary>
    internal const int GlassesLivePollMs = 500;

    /// <summary>Polls per host decision before giving up on the live check (a later reconcile may still equip).</summary>
    internal const int GlassesLivePollCap = 6;

    internal enum GlassesAction { None, Equip, RetryAfter, Remove, Unclaimed, RemovalInFlight, Rest, StopPolling }

    /// <param name="RetryMs">For <see cref="GlassesAction.RetryAfter"/>: when to look again.</param>
    /// <param name="ChargeCooldown">A removal that can oscillate with an equip: charge the inject cooldown.</param>
    /// <param name="FailureRemoval">Taken off because the build failed (counted toward <see cref="GlassesChurn"/>).</param>
    /// <param name="LivePoll">A retry waiting for the redirect to go live (counted toward <see cref="GlassesLivePollCap"/>).</param>
    internal readonly record struct GlassesDecision(GlassesAction Action, int RetryMs = 0, bool ChargeCooldown = false,
                                                    bool FailureRemoval = false, bool LivePoll = false);

    /// <summary>What to do with the invisible-glasses carrier after a composite (or on a redraw or retry).</summary>
    /// <param name="featureOn">The invisible-glasses option is on.</param>
    /// <param name="gearWanted">Gear overlays or content want a host.</param>
    /// <param name="shellBuilt">A shell was built this composite.</param>
    /// <param name="publishedOnFacewear">Its model was PUBLISHED on a facewear "_met" path (a failed write does not count).</param>
    /// <param name="facewearWriteFailed">The facewear host's model could not be written (#91).</param>
    /// <param name="recorded">Proteus recorded equipping the carrier.</param>
    /// <param name="ourModelDrawn">The walk shows the carrier's model set drawn.</param>
    /// <param name="metKnown">A walk has populated the facewear list at all.</param>
    /// <param name="anyMetWorn">Some facewear is drawn (ours or another).</param>
    /// <param name="cooldownLeftMs">The inject cooldown still to run.</param>
    /// <param name="msSinceOurRemoval">Since Proteus last took the carrier off; null when it has not.</param>
    /// <param name="resting">Too many failure removals lately (<see cref="GlassesChurn"/>): no equip.</param>
    /// <param name="redirectLive">Penumbra resolves the carrier's model to our file. Called only on the would-equip path.</param>
    /// <param name="livePolls">Live polls already made this host decision.</param>
    public static GlassesDecision Glasses(bool featureOn, bool gearWanted, bool shellBuilt, bool publishedOnFacewear,
                                          bool facewearWriteFailed, bool recorded, bool ourModelDrawn, bool metKnown,
                                          bool anyMetWorn, long cooldownLeftMs, long? msSinceOurRemoval, bool resting,
                                          Func<bool> redirectLive, int livePolls)
    {
        if (featureOn && shellBuilt && publishedOnFacewear)
        {
            if (!metKnown || anyMetWorn) return new GlassesDecision(GlassesAction.None);
            if (resting) return new GlassesDecision(GlassesAction.Rest);
            if (cooldownLeftMs > 0)
                return new GlassesDecision(GlassesAction.RetryAfter, (int)Math.Min(cooldownLeftMs, int.MaxValue));
            // Equip only into a live redirect: equipped early, the game loads the carrier's own model (the gold frames)
            // and, with auto redraw off, nothing redraws it afterwards. Stopping the polls never stops a later equip.
            if (redirectLive()) return new GlassesDecision(GlassesAction.Equip);
            return livePolls < GlassesLivePollCap
                ? new GlassesDecision(GlassesAction.RetryAfter, GlassesLivePollMs, LivePoll: true)
                : new GlassesDecision(GlassesAction.StopPolling);
        }

        if (!ourModelDrawn) return new GlassesDecision(GlassesAction.None);
        if (!recorded)
            return msSinceOurRemoval is { } since && since >= 0 && since <= RemovalGraceMs
                ? new GlassesDecision(GlassesAction.RemovalInFlight)
                : new GlassesDecision(GlassesAction.Unclaimed);
        bool wanted = featureOn && gearWanted;
        return new GlassesDecision(GlassesAction.Remove, ChargeCooldown: wanted,
                                   FailureRemoval: wanted && (!shellBuilt || facewearWriteFailed));
    }

    /// <summary>
    /// Our recorded pair is drawn at <paramref name="ourWalkedMetPath"/> but the newest published manifest has no redirect
    /// for that exact path, so the game draws the carrier's own model.
    /// </summary>
    public static bool GlassesRedirectGap(string? ourWalkedMetPath, Func<string, string?> publishedTarget)
        => ourWalkedMetPath != null && publishedTarget(ourWalkedMetPath) == null;

    /// <summary>The gap Warning's dedup key: once per path and published manifest.</summary>
    public static string GlassesGapLogKey(string path, long publishSeq) => $"{path}|{publishSeq}";

    /// <summary>
    /// A walk taken while the character reloads can miss our pair that is still on. With our record standing and the
    /// shell NOT on the facewear slot, the walk not showing ours is not evidence: Glamourer decides.
    /// </summary>
    public static bool GlassesWalkMayMissOurs(bool recorded, bool hostedOnFacewearNow, bool walkShowsOurs)
        => recorded && !hostedOnFacewearNow && !walkShowsOurs;

    /// <summary>Glamourer reports our own carrier row while our record stands (re-checked after the read).</summary>
    public static bool GlassesConfirmedOurs(bool recordedAfterRead, ulong? wornRow, ulong ourItemId)
        => recordedAfterRead && wornRow is { } worn && worn == ourItemId;

    /// <summary>How long after our own removal a walk showing our set may be that removal's stale reading.</summary>
    internal const int GlassesRewalkWindowMs = 300_000;

    /// <summary>
    /// Re-walk before believing a walk that shows our carrier's set with no record: Proteus took its own pair off
    /// within <see cref="GlassesRewalkWindowMs"/> and has not re-walked for that removal yet. Once per removal, so a
    /// player who really wears a pair on that set costs one extra walk, not one per composite.
    /// </summary>
    /// <param name="removedAtTick">When Proteus last took its pair off (0 = never).</param>
    /// <param name="rewalkedForTick">The removal stamp a re-walk was already made for.</param>
    public static bool GlassesRewalkDue(bool recorded, bool walkShowsOurs, long removedAtTick, long rewalkedForTick, long nowTick)
    {
        if (recorded || !walkShowsOurs || removedAtTick == 0 || removedAtTick == rewalkedForTick) return false;
        long since = unchecked(nowTick - removedAtTick);
        return since >= 0 && since < GlassesRewalkWindowMs;
    }

    /// <summary>Penumbra resolves the carrier's model to our file (both canonicalised by the caller).</summary>
    public static bool GlassesRedirectLive(string? expectedCanonical, string? resolvedCanonical)
        => expectedCanonical != null && resolvedCanonical != null
           && string.Equals(expectedCanonical, resolvedCanonical, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Failure removals of the invisible-glasses carrier. A build that alternates between failing and succeeding would take
/// the pair off and put it back every few seconds; after <see cref="Cap"/> failure removals within <see cref="WindowMs"/>
/// the carrier RESTS (stays off, and the shell is offered other hosts) until the window has passed since the last one
/// or a facewear shell is confirmed drawn. The resting state is always "carrier off", never a visible eyepatch.
/// </summary>
internal sealed class GlassesChurn
{
    internal const int Cap = 2;
    internal const int WindowMs = 60_000;

    private readonly object sync = new();
    private readonly Queue<long> removals = new();
    private long restUntil = long.MinValue;

    /// <summary>Count a failure removal. True when this one starts a rest.</summary>
    public bool NoteFailureRemoval(long nowTick)
    {
        lock (sync)
        {
            while (removals.Count > 0 && nowTick - removals.Peek() >= WindowMs) removals.Dequeue();
            removals.Enqueue(nowTick);
            if (removals.Count < Cap || nowTick < restUntil) return false;
            removals.Clear();               // the rest answers these; the next rest needs Cap new failures
            restUntil = nowTick + WindowMs;
            return true;
        }
    }

    /// <summary>
    /// A facewear shell was confirmed drawn: an active rest ends. The failure removals stay counted for their window,
    /// since a build that alternates failing and succeeding confirms a draw between every two failures (QA: the
    /// count was cleared each time and rest never started, 7 carrier writes in 60 s).
    /// </summary>
    public void NoteConfirmedOnFacewear()
    {
        lock (sync) restUntil = long.MinValue;
    }

    public bool Resting(long nowTick)
    {
        lock (sync) return nowTick < restUntil;
    }

    public long RestEndsAt
    {
        get { lock (sync) return restUntil; }
    }
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
