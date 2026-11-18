using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public partial class CompositorService
{
    // ── Invisible auto-glasses (opt-in) ──────────────────────────────────────────
    // Ownership is remembered, not inferred: the carrier is a real item players wear by choice, so the injected glasses
    // are ours only when we recorded equipping them (Configuration.InjectedGlasses) and that item is still worn.

    // Don't re-equip the invisible pair within this window: a recomposite before the model loads would loop inject → recomposite.
    private const int GlassesInjectCooldownMs = 5000;
    private long _lastGlassesInjectTick;

    // Same guard for the Emperor's-ring injection, kept per slot: taking one host's carrier off must never delay
    // equipping the next host's (a host move rir->ril).
    private const int RingInjectCooldownMs = 5000;
    private readonly CarrierCooldowns _carrierCooldowns = new();

    // Consecutive asks per ring slot (CarrierDecisions.AskCap), counted per host-decision epoch and reset whenever the
    // slot is seen drawn. A user's log showed an unbounded ask loop, one every 5.25 s.
    private readonly CarrierAskCounter _carrierAsks = new();
    private long _hostDecisionEpoch;
    private readonly ConcurrentDictionary<string, byte> _carrierGaveUpLogged = new(StringComparer.OrdinalIgnoreCase);

    // When Proteus last took its carrier off a slot: a walk still showing it inside CarrierDecisions.RemovalGraceMs is
    // that removal in flight, not a piece the player put on (the log's false "no record" 0.9 s after a removal).
    private readonly ConcurrentDictionary<string, long> _carrierRemovedAt = new(StringComparer.OrdinalIgnoreCase);

    // When _equippedAccessoryModels was last assigned: a walk older than the slot's last ask + settle is stale evidence.
    private long _accessoryWalkTick;

    /// <summary>Stamp the accessory walk; called wherever <see cref="_equippedAccessoryModels"/> is assigned.</summary>
    private void NoteAccessoryWalk() => Interlocked.Exchange(ref _accessoryWalkTick, Environment.TickCount64);

    /// <summary>The newest ring-carrier ask on any slot (for the drawn check's "equipped just now" deferral).</summary>
    private long LastRingAskTick
        => InvisibleRing.CarrierSlots.Max(c => _carrierAsks.LastAskTick(c.Slot));

    // What we equipped, remembered rather than inferred, so teardown works before the first successful walk; never
    // acted on when the walk says another item is there. Persisted (Configuration.InjectedRingSlot), since the ring
    // is an ordinary item and ownership must survive a reload. Comma-joined so the single-slot field carries a set.
    private IReadOnlyList<string> _injectedCarrierSlots
    {
        get => config.InjectedRingSlot is { Length: > 0 } s
            ? s.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : [];
        set
        {
            var joined = value.Count == 0
                ? null
                : string.Join(",", value.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
            if (config.InjectedRingSlot == joined) return;
            config.InjectedRingSlot = joined;
            config.Save();
        }
    }

    // The record is read, changed and written back; reconciles run on several threads (the composite, the redraw hook,
    // the retry timer), so the two changes are serialised: an interleaving could drop a slot and strand its carrier.
    private readonly object _carrierRecordLock = new();

    private void MarkCarrierInjected(string slot)
    {
        lock (_carrierRecordLock)
        {
            if (_injectedCarrierSlots.Contains(slot, StringComparer.Ordinal)) return;
            _injectedCarrierSlots = [.. _injectedCarrierSlots, slot];
        }
    }

    private void MarkCarrierRemoved(string slot)
    {
        lock (_carrierRecordLock)
        {
            if (!_injectedCarrierSlots.Contains(slot, StringComparer.Ordinal)) return;
            _injectedCarrierSlots = _injectedCarrierSlots.Where(s => s != slot).ToList();
        }
    }

    // Persisted like the ring slots: removing the pair is only safe when we put it there, and that must survive a reload.
    private bool _injectedGlasses
    {
        get => config.InjectedGlasses;
        set
        {
            if (config.InjectedGlasses == value) return;
            config.InjectedGlasses = value;
            // Before the save, so a reader that captured the old generation sees the bump however the write races.
            Interlocked.Increment(ref _glassesRecordGeneration);
            config.Save();
        }
    }

    // Bumped on every change to the record above. ReconcileInvisibleGlasses can run on two threads at once — the
    // redraw hook checks _compositesInFlight when it SCHEDULES its Task.Run and never again — and deciding the
    // record's fate means blocking on a Glamourer read first. Capturing this across that read catches a record that
    // moved underneath the decision, including away and back, so a stale reading cannot clear a record for a pair
    // that is on the character right now.
    private int _glassesRecordGeneration;

    // Our carrier item worn with no record of equipping it; reported once per session.
    private int _unclaimedGlassesLogged;

    // The first "held our record" reading is worth an Information line; the rest are per-redraw noise.
    private int _glassesHoldLogged;

    // Ring slots holding an Emperor's ring we have no record of equipping; reported once per slot per session.
    private readonly ConcurrentDictionary<string, byte> _unclaimedRingSlots = new(StringComparer.OrdinalIgnoreCase);

    // The last composite's hosting decision, so the redraw hook can re-run the reconciles without one: carriers are
    // equipped with ApplyFlag.Once and any game redraw reverts them.
    private volatile bool _lastGearWanted;
    private volatile bool _lastShellBuilt;
    private volatile bool _lastShellOnFacewear;
    // An array, never mutated after assignment; volatile publishes the reference.
    private volatile string[] _lastShellCarrierSlots = [];

    /// <summary>Remember this composite's hosting decision for the cheap reconciles above.</summary>
    private void RememberHostDecision(bool gearWanted, bool shellBuilt, bool onFacewear,
        IReadOnlyList<string> carrierSlots)
    {
        _lastGearWanted         = gearWanted;
        _lastShellBuilt         = shellBuilt;
        _lastShellOnFacewear    = onFacewear;
        _lastShellCarrierSlots  = [.. carrierSlots];
        // A new decision is a new epoch: every slot's consecutive-ask count starts again.
        Interlocked.Increment(ref _hostDecisionEpoch);
        _carrierGaveUpLogged.Clear();
    }

    /// <summary>One pending carrier retry, coalesced to the earliest deadline any slot asked for.</summary>
    private readonly CoalescingRetry _carrierRetry = new();

    /// <summary>A retry that fires while a composite runs is re-armed this much later, not dropped.</summary>
    private const int CarrierRetryRearmMs = 1000;

    /// <summary>
    /// Re-run the carrier reconciles once <paramref name="cooldownRemainingMs"/> has elapsed (for
    /// <paramref name="slot"/>: a ring slot, or "glasses"). Coalesced to the earliest deadline asked for; goes through
    /// the same guards; does not touch any cooldown.
    /// </summary>
    private void ScheduleCarrierRetry(int cooldownRemainingMs, string slot = "glasses")
    {
        // A margin past the window: TickCount64 and the timer are different clocks.
        int delay = Math.Clamp(cooldownRemainingMs, 0, RingInjectCooldownMs + CarrierDecisions.SettleMs) + 250;
        if (!_carrierRetry.Request(slot, Environment.TickCount64 + delay, out var generation)) return;
        _ = Task.Delay(delay).ContinueWith(_ => OnCarrierRetry(generation));
    }

    private void OnCarrierRetry(int generation)
    {
        if (!_carrierRetry.Fire(generation, out var slots)) return;   // superseded by an earlier deadline
        try
        {
            if (_disposed || !config.PluginEnabled || !_secondSkinActive) return;
            // A composite in flight runs its own reconcile first, with fresher arguments; come back after it, re-armed.
            if (Volatile.Read(ref _compositesInFlight) > 0)
            {
                foreach (var slot in slots) ScheduleCarrierRetry(CarrierRetryRearmMs - 250, slot);
                return;
            }
            // Only the carriers that asked: a ring's retry must not re-ask a drawn pair of glasses (QA: an extra
            // Glamourer write 6.8 s after every boot composite).
            if (slots.Contains("glasses"))
                ReconcileInvisibleGlasses(_lastGearWanted, _lastShellBuilt, _lastShellOnFacewear,
                                          alreadyHosted: true);
            if (slots.Any(s => !string.Equals(s, "glasses", StringComparison.OrdinalIgnoreCase)))
                ReconcileEmperorRing(_lastGearWanted, _lastShellBuilt, _lastShellCarrierSlots);
        }
        catch (Exception ex) { log.Debug("[Proteus] carrier retry failed: {0}", ex.Message); }
    }

    /// <summary>The equipment set number from a head "_met" model path, e.g. "…/e5524/model/…_met.mdl" → 5524.</summary>
    private static int? ParseMetSet(string metGamePath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(metGamePath, @"/e(\d+)/");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : (int?)null;
    }

    /// <summary>Every head "_met" set currently loaded (a helmet and glasses can both be worn).</summary>
    private IEnumerable<int> CurrentMetSets()
        => (_equippedMetModels ?? []).Select(ParseMetSet).Where(s => s != null).Select(s => s!.Value);

    private bool AnyMetWorn() => (_equippedMetModels?.Count ?? 0) > 0;

    /// <summary>Has a draw-object walk ever populated the "_met" list? Until it has, <see cref="AnyMetWorn"/>
    /// reads "unknown" as "nothing worn" (see <see cref="AccessorySnapshotKnown"/>).</summary>
    private bool MetSnapshotKnown => _equippedMetModels != null;

    private bool IsOurGlassesWorn(int ourSet) => CurrentMetSets().Contains(ourSet);

    /// <summary>
    /// Whether the pair on the player's face is ours to act on: we recorded equipping it, and our carrier's model is
    /// among the ones being drawn. Any thread, and no IPC.
    /// <para/>
    /// Deliberately NOT gated on the row Glamourer reports. That reading's job is to REVOKE the record — see
    /// <see cref="GlassesRecord"/>, which runs first in every reconcile and clears it the moment another item answers
    /// — and it is not sound as a precondition in its own right: mid-redraw it reports no pair while ours is still
    /// drawn, and requiring it to match there left Proteus holding a record it could not act on, with the carrier
    /// showing its real frames and no way to take it off.
    /// </summary>
    private bool OurGlassesAreOn(InvisibleGlasses.Identity g) => _injectedGlasses && IsOurGlassesWorn(g.ModelSet);

    /// <summary>The Glasses row Glamourer says is worn (0 = none), or null when its state could not be read.</summary>
    private ulong? WornGlassesRow()
    {
        try
        {
            var bonusId = Plugin.Framework.RunOnFrameworkThread(
                () => glamourer.GetObjectState(0)?["Bonus"]?["Glasses"]?["BonusId"]?.ToObject<ulong?>())
                .GetAwaiter().GetResult();
            // Glamourer packs a bonus item as (type << 48) | row id.
            return bonusId is { } id ? id & 0x0000_FFFF_FFFF_FFFFUL : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Debug("[Proteus] invisible glasses: Glamourer state read failed ({0})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Act on what Glamourer's state says about the pair we recorded equipping. Every outcome is logged: losing this
    /// record silently is what strands the carrier on a player's face, and the only way that came to light was a user
    /// sending in a log with no trace of it in them.
    /// </summary>
    private void JudgeGlassesRecord(InvisibleGlasses.Identity g)
    {
        // Captured before the read, checked after: see _glassesRecordGeneration. This narrows the window from the
        // whole Glamourer round-trip — tens of milliseconds, going by the "carriers" figure in the refresh timeline —
        // to the few instructions below. It does not close it; doing that would mean holding a lock across a
        // dispatch to the framework thread, which is how this deadlocks.
        var generation = Volatile.Read(ref _glassesRecordGeneration);
        var worn = WornGlassesRow();
        var drawn = IsOurGlassesWorn(g.ModelSet);
        var decision = GlassesRecord.Evaluate(recorded: true, worn, g.ItemId, drawn);

        if (decision is not GlassesRecord.Decision.Keep
            && Volatile.Read(ref _glassesRecordGeneration) != generation)
        {
            log.Debug("[Proteus] invisible glasses: the record changed while we read Glamourer's state — "
                    + "dropping this reading rather than acting on it");
            return;
        }

        switch (decision)
        {
            case GlassesRecord.Decision.Keep:
                break;

            case GlassesRecord.Decision.Hold:
                var why = worn is null
                    ? "Glamourer's state could not be read"
                    : "Glamourer reports no pair while its model is still drawn (a redraw in flight)";
                // The first one at Information. This branch suppresses an action, and a suppression nobody can see is
                // exactly what cost a user's log to diagnose; after that it is per-redraw noise.
                if (Interlocked.Exchange(ref _glassesHoldLogged, 1) == 0)
                    log.Information("[Proteus] invisible glasses: keeping our record of e{0:D4} — {1}", g.ModelSet, why);
                else
                    log.Debug("[Proteus] invisible glasses: keeping our record of e{0:D4} — {1}", g.ModelSet, why);
                break;

            case GlassesRecord.Decision.Forget:
                _injectedGlasses = false;
                log.Information("[Proteus] invisible glasses: forgetting our pair (e{0:D4}) — Glamourer reports {1}",
                    g.ModelSet, worn == 0 ? "no glasses worn" : $"item #{worn}");
                break;

            // Matched with the reading it needs: the decision implies one, and binding it here keeps that a fact of
            // the code rather than of GlassesRecord's current shape.
            case GlassesRecord.Decision.ForgetAndRelease when worn is { } theirs:
                _injectedGlasses = false;
                // A pair of the player's now draws our carrier's model, which the shell may still be redirected onto. The
                // redraw that got us here already took its equip signature (which skipped this set while it was ours), so
                // nothing else will recomposite: do it now, so the shell moves off their pair.
                log.Information("[Proteus] invisible glasses: item #{0} replaced our pair on the same model (e{1:D4}) — "
                              + "forgetting it and recompositing to move the shell off it", theirs, g.ModelSet);
                TriggerRecomposite("invisible-glasses-released");
                break;
        }
    }

    /// <summary>
    /// Keep the invisible-glasses injection in line with the current composite: equip our pair when a shell is wanted
    /// and no facewear is worn; remove only ours, and only when the feature is off or nothing is hosted (not on a
    /// transient failed build). Writes only when the slot's occupant must change. Framework-thread game write.
    /// </summary>
    /// <param name="gearWanted">Gear overlays are active, so a shell is supposed to exist.</param>
    /// <param name="shellBuilt">A shell actually built this composite.</param>
    /// <param name="hostedOnFacewear">That shell was published onto a "_met" path, so the pair is its host.</param>
    /// <param name="alreadyHosted">
    /// The shell is already hosted on the pair about to be equipped, so no follow-up recomposite is needed.
    /// </param>
    private void ReconcileInvisibleGlasses(bool gearWanted, bool shellBuilt, bool hostedOnFacewear,
        bool alreadyHosted = false)
    {
        if (InvisibleGlasses.Resolve(Plugin.DataManager, log) is not { } g) return;
        bool want = config.AutoInvisibleGlasses && shellBuilt && hostedOnFacewear;

        // Glamourer says another pair (or none) is worn: ours is gone, so forget it, or a pair the player puts on later
        // would be taken for it. Guarded on holding a record at all, since the state read is IPC and this runs on every
        // redraw. What the reading means is decided in GlassesRecord — see there for why a wrong clear is permanent.
        if (_injectedGlasses)
            JudgeGlassesRecord(g);

        if (want)
        {
            // No adopting a pair already on the face: the carrier is a real item, and a pair we did not equip is not ours
            // to take off later. Nor does the shell host on it: ShellPhase hides the carrier set from the chooser unless the
            // record says we equipped the worn pair, so a player's own carrier item keeps its real model.
            var sinceInject = unchecked(Environment.TickCount64 - _lastGlassesInjectTick);
            if (MetSnapshotKnown && !AnyMetWorn() && sinceInject <= GlassesInjectCooldownMs)
                // Refused only by the cooldown: schedule a retry, since fast callers would otherwise leave the shell hostless.
                ScheduleCarrierRetry((int)(GlassesInjectCooldownMs - sinceInject), "glasses");

            if (MetSnapshotKnown && !AnyMetWorn() && sinceInject > GlassesInjectCooldownMs)
            {
                // Charge the cooldown on the attempt, not the success, or a locked slot retries on every redraw.
                var equipped = SetGlassesOnFramework(g.ItemId);
                _lastGlassesInjectTick = Environment.TickCount64;
                if (!equipped)
                    return;

                _injectedGlasses = true;
                if (alreadyHosted)
                {
                    // The shell is already redirected onto this pair, so its redraw lands on it; no recomposite.
                    log.Information("[Proteus] invisible glasses: equipped item #{0} (model e{1:D4}) — shell already hosted on it",
                        g.ItemId, g.ModelSet);
                }
                else
                {
                    log.Information("[Proteus] invisible glasses: equipped item #{0} (model e{1:D4}) — recompositing to host on it",
                        g.ItemId, g.ModelSet);
                    TriggerRecomposite("invisible-glasses-equipped", delayMs: 600);
                }
            }
        }
        else if ((!config.AutoInvisibleGlasses || !gearWanted || (shellBuilt && !hostedOnFacewear))
                 && IsOurGlassesWorn(g.ModelSet))
        {
            // Remove when the feature is off, nothing is hosted, or the shell moved to another host (the carrier would render
            // its real frames). Not on a merely failed build, which is transient.
            //
            // Our carrier's model is drawn with no record of us equipping it: the player's own pair, so leave it and say so
            // once. Confirmed against Glamourer's row, since other rows share the model set and naming the wrong item sends
            // them looking for something they are not wearing. The flag is taken BEFORE the read, not after a match: this
            // branch is reached on every redraw for as long as they wear it, and a read gated on matching would repeat the
            // IPC forever against a pair that never matches. The cost is that a variant worn at this moment and swapped for
            // ours later goes unremarked — an advisory line, against an unbounded per-redraw round-trip.
            if (!_injectedGlasses)
            {
                if (Interlocked.Exchange(ref _unclaimedGlassesLogged, 1) == 0
                    && WornGlassesRow() == g.ItemId)
                    log.Information("[Proteus] invisible glasses: item #{0} (e{1:D4}) is worn but Proteus has no record "
                                  + "of equipping it — leaving it alone. If you did not put it on yourself (an older "
                                  + "build could equip it and forget), take it off manually.", g.ItemId, g.ModelSet);
                return;
            }

            bool hostMoved = shellBuilt && !hostedOnFacewear;
            if (SetGlassesOnFramework(0))
            {
                // Charge the cooldown only for the hosts-elsewhere removal, which can oscillate; the others cannot.
                if (hostMoved) _lastGlassesInjectTick = Environment.TickCount64;
                _injectedGlasses = false;
                log.Information("[Proteus] invisible glasses: removed our injected glasses (e{0:D4}){1}",
                    g.ModelSet, hostMoved ? " — the shell hosts elsewhere now" : "");
            }
        }
    }

    private bool SetGlassesOnFramework(ulong itemId)
    {
        int claim = ExpectIndirectRedraw(itemId == 0 ? "glasses carrier off" : "glasses carrier on");
        try
        {
            bool ok = CarrierWrites.Guarded(OnFramework, () => GoneAway("invisible glasses"),
                                            () => glamourer.SetGlasses(itemId));
            if (!ok) WithdrawIndirectRedraw(claim);
            return ok;
        }
        catch (Exception ex)
        {
            WithdrawIndirectRedraw(claim);
            log.Warning(ex, "[Proteus] invisible glasses: SetGlasses({0}) failed", itemId);
            return false;
        }
    }

    /// <summary>The model the game draws in a ring slot ("rir"/"ril"), or null when empty. Only meaningful once
    /// <see cref="AccessorySnapshotKnown"/>.</summary>
    private string? RingModel(string slot)
        => _equippedAccessoryModels != null && _equippedAccessoryModels.TryGetValue(slot, out var p) ? p : null;

    /// <summary>Has a draw-object walk ever populated the accessory map? Until it has, an absent slot means
    /// "unknown", not "empty"; never confuse the two before writing to the player's equipment.</summary>
    private bool AccessorySnapshotKnown => _equippedAccessoryModels != null;

    /// <summary>
    /// The item ids of the hosts Proteus put on the player, or null for each we did not; design matching retires
    /// them (<see cref="DesignBindingService.StripCarriers"/>). Keyed on what we actually injected, not the feature
    /// toggle, since players wear these items by choice.
    /// </summary>
    public ulong? InjectedGlassesItemId
        => _injectedGlasses ? InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ItemId : null;

    /// <inheritdoc cref="InjectedGlassesItemId"/>
    /// <remarks>A list: each carrier slot is a different item row, so one id could not cover them all.</remarks>
    public IReadOnlyList<ulong> InjectedCarrierItemIds
        => _injectedCarrierSlots.Where(OurCarrierStillWorn)
            .Select(s => InvisibleRing.ResolveFor(Plugin.DataManager, log, s)?.ItemId)
            .Where(id => id != null).Select(id => id!.Value).ToList();

    /// <summary>
    /// The Glamourer slot names ("RFinger", "Wrists", …) we are currently borrowing for a carrier. Design matching
    /// ignores the player's choice for a borrowed slot (<see cref="DesignBindingService.StripCarriers"/>).
    /// Config-backed, so it answers correctly at boot.
    /// </summary>
    public IReadOnlyList<string> InjectedCarrierSlots
        => _injectedCarrierSlots.Where(OurCarrierStillWorn)
            .Select(s => Array.Find(InvisibleRing.CarrierSlots, c => c.Slot == s).EqdpSlot)
            .Where(s => s != null).ToList();

    /// <summary>
    /// Is the carrier we recorded equipping in this slot still on the player? The record can outlive the piece,
    /// which would retire the slot from design matching forever, so check the live slot and trust the record only
    /// while the walk has nothing to say.
    /// </summary>
    private bool OurCarrierStillWorn(string slot)
        => InvisibleRing.ResolveFor(Plugin.DataManager, log, slot) is { } r
        && (IsOurRingWorn(slot, r.ModelSet) || !AccessorySnapshotKnown);

    /// <summary>Does this ring slot hold the Emperor's ring we equipped?</summary>
    private bool IsOurRingWorn(string slot, int modelSet)
        => RingModel(slot) is { } p && p.Contains($"a{modelSet:D4}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keep the invisible-carrier injections in line with the current composite, mirroring
    /// <see cref="ReconcileInvisibleGlasses"/>. A carrier renders only while worn, so equip it into the slot
    /// ChooseHosts established is free. Idempotent; framework-thread game write.
    /// </summary>
    /// <param name="gearWanted">Gear overlays are active, so a shell is supposed to exist.</param>
    /// <param name="shellBuilt">A shell actually built this composite.</param>
    /// <param name="carrierSlots">The accessory slots the shell published a carrier onto; empty for none.</param>
    /// <param name="selfScheduleRetry">True only from a publishing composite's ReconcileAndVerify, where the drawn
    /// check follows: a successful ask then schedules its own checked retry.</param>
    private void ReconcileEmperorRing(bool gearWanted, bool shellBuilt, IReadOnlyList<string>? carrierSlots,
                                      bool selfScheduleRetry = false)
    {
        // One pass per carrier slot, then one removal sweep. Each slot resolves its own item id (same model set, different items).
        foreach (var slot in carrierSlots ?? [])
            ReconcileOneCarrier(slot, selfScheduleRetry);

        SweepUnusedCarriers(gearWanted, shellBuilt, carrierSlots ?? []);
    }

    /// <summary>
    /// Equip the invisible piece for one slot the shell published a carrier onto. Idempotent. Asks only on fresh
    /// evidence that the slot is undrawn, at most <see cref="CarrierDecisions.AskCap"/> times in a row.
    /// </summary>
    private void ReconcileOneCarrier(string ringSlot, bool selfScheduleRetry = false)
    {
        if (InvisibleRing.ResolveFor(Plugin.DataManager, log, ringSlot) is not { } r) return;
        long epoch = Interlocked.Read(ref _hostDecisionEpoch);

        for (int pass = 0; pass < 2; pass++)
        {
            long now = Environment.TickCount64;
            long lastAsk = _carrierAsks.LastAskTick(ringSlot);
            // Players wear these by choice: an invisible piece of that set already in the slot is not claimed.
            bool walkShowsOurs = IsOurRingWorn(ringSlot, r.ModelSet);
            bool walkFresh = Interlocked.Read(ref _accessoryWalkTick) > lastAsk + CarrierDecisions.SettleMs;
            var d = CarrierDecisions.Equip(walkShowsOurs, walkFresh, ShellConfirmedSince(lastAsk),
                                           AccessorySnapshotKnown, RingModel(ringSlot) != null && !walkShowsOurs,
                                           _carrierCooldowns.LeftMs(ringSlot, now, RingInjectCooldownMs),
                                           _carrierAsks.Count(ringSlot, epoch));
            switch (d.Action)
            {
                case CarrierDecisions.EquipAction.None:
                    if (d.Drawn) { _carrierAsks.Reset(ringSlot); _carrierGaveUpLogged.TryRemove(ringSlot, out _); }
                    else if (!AccessorySnapshotKnown)
                        log.Debug("[Proteus] invisible carrier: no accessory snapshot yet — not equipping into {0} until "
                                + "a walk confirms it is empty", ringSlot);
                    return;
                case CarrierDecisions.EquipAction.GaveUp:
                    if (_carrierGaveUpLogged.TryAdd(ringSlot, 0))
                        log.Information("[Proteus] invisible carrier: asked twice for {0} but the game is not drawing it — "
                                      + "not asking again until it is drawn or the next composite", ringSlot);
                    return;
                case CarrierDecisions.EquipAction.RewalkFirst:
                    // The held walk predates the last ask's load: look again (a framework-thread resource walk) once.
                    if (pass == 0 && RefreshEquippedModels()) continue;
                    return;
                case CarrierDecisions.EquipAction.RetryAfter:
                    ScheduleCarrierRetry(d.RetryMs, ringSlot);
                    return;
            }

            // Equip. Counted atomically first: two reconciles can race on one slot.
            if (!_carrierAsks.TryBeginAsk(ringSlot, epoch, CarrierDecisions.AskCap, now)) return;
            // Charge the cooldown on the attempt, not the success (see the glasses).
            _carrierCooldowns.Charge(ringSlot, now);
            if (SetAccessoryOnFramework(r.ItemId, ringSlot))
            {
                MarkCarrierInjected(ringSlot);
                _carrierRemovedAt.TryRemove(ringSlot, out _);
                // No follow-up recomposite: the shell is already published at the path this piece loads.
                log.Information("[Proteus] invisible carrier: equipped item #{0} (model a{1:D4}) in {2} — shell already hosted on it",
                    r.ItemId, r.ModelSet, ringSlot);
                // The checked retry, which no longer rides on the echo composite or the racing redraw hook.
                if (selfScheduleRetry) ScheduleCarrierRetry(RingInjectCooldownMs + CarrierDecisions.SettleMs, ringSlot);
            }
            else _carrierAsks.Undo(ringSlot);   // never reached the player (gone, locked, IPC failed): not an ask
            return;
        }
    }

    /// <summary>
    /// The drawn check confirmed the current shell after <paramref name="lastAskTick"/> and after the last observed
    /// redraw. A confirmation from before a game redraw (a zone change reverts carriers) no longer counts.
    /// </summary>
    private bool ShellConfirmedSince(long lastAskTick)
    {
        var probe = _shellDrawnCheck;
        if (probe == null || _shellConfirmedDrawnKey is not { } key) return false;
        if (!string.Equals(key, ShellProbeKey(probe), StringComparison.Ordinal)) return false;
        return CarrierDecisions.ConfirmationCounts(Interlocked.Read(ref _shellConfirmedDrawnAtTick), lastAskTick,
                                                   Interlocked.Read(ref _lastObservedRedrawTick));
    }

    /// <summary>
    /// Take back every carrier we equipped that this composite no longer hosts on. A whole-build decision, and it
    /// must sweep slots the current build never mentioned.
    /// </summary>
    private void SweepUnusedCarriers(bool gearWanted, bool shellBuilt, IReadOnlyList<string> inUse)
        => SweepCarriers(gearWanted, shellBuilt, inUse, localWalk: false);

    /// <summary>
    /// Take off the ring carriers a composite stops hosting on BEFORE its manifest withdraws their redirects (called in
    /// CommitLocators after the supersede check). The removal used to run after the reload and redraw, so the game
    /// reloaded a still-worn carrier whose redirects were gone (a SqPack load of the ring model). Local walks only.
    /// </summary>
    private void ReleaseVacatedCarriers(bool hostWanted, bool shellBuilt, IReadOnlyList<string> shellCarrierSlots)
        => SweepCarriers(hostWanted, shellBuilt, shellCarrierSlots, localWalk: true);

    private void SweepCarriers(bool gearWanted, bool shellBuilt, IReadOnlyList<string> inUse, bool localWalk)
    {
        // Remove our piece when nothing is hosted or the shell built and went elsewhere; not on a merely failed build.
        if (!CarrierDecisions.MayRelease(gearWanted, shellBuilt)) return;
        bool hostMoved = gearWanted && shellBuilt;

        foreach (var (slot, eqdpSlot, _) in InvisibleRing.CarrierSlots)
        {
            if (inUse.Contains(slot, StringComparer.Ordinal)) continue;   // still hosting on it — leave it on
            if (InvisibleRing.ResolveFor(Plugin.DataManager, log, slot) is not { } r) continue;
            bool walkShowsOurs = IsOurRingWorn(slot, r.ModelSet);
            bool recorded = _injectedCarrierSlots.Contains(slot, StringComparer.Ordinal);
            // The walk shows the slot without our piece: a removal of ours has landed.
            if (!walkShowsOurs && AccessorySnapshotKnown) _carrierRemovedAt.TryRemove(slot, out _);
            if (!walkShowsOurs && !recorded) continue;

            // Only for a recorded slot the walk cannot see: what Glamourer holds there, and a valid walk taken after.
            ulong? held = null;
            bool? after = null;
            if (recorded && !walkShowsOurs)
            {
                held = GlamourerSlotItem(eqdpSlot);
                if (held is { } h && GlamourerItemIds.IsEmpty(h)) after = PostReadWalkShowsOurs(slot, r.ModelSet);
            }
            long? sinceRemoval = _carrierRemovedAt.TryGetValue(slot, out var at) ? Environment.TickCount64 - at : null;

            switch (CarrierDecisions.Sweep(false, walkShowsOurs, recorded, held, r.ItemId, after, sinceRemoval))
            {
                case CarrierDecisions.SweepAction.Unclaimed:
                    // Worn but not recorded as ours: could be the player's own, so do nothing and say so once.
                    if (_unclaimedRingSlots.TryAdd(slot, 0))
                        log.Information("[Proteus] invisible carrier: a{0:D4} is worn in {1} but Proteus has no "
                                      + "record of equipping it — leaving it alone. If you did not put it "
                                      + "there yourself (an older build could equip one and forget), take "
                                      + "it off manually.", r.ModelSet, slot);
                    break;
                case CarrierDecisions.SweepAction.ForgetRecord:
                    MarkCarrierRemoved(slot);
                    log.Information("[Proteus] invisible carrier: {0} no longer holds our piece — record dropped", slot);
                    break;
                case CarrierDecisions.SweepAction.Remove:
                    if (SetAccessoryOnFramework(0, slot))
                    {
                        // Only this slot's cooldown: the next host's equip is not held back.
                        if (hostMoved) _carrierCooldowns.Charge(slot, Environment.TickCount64);
                        _carrierRemovedAt[slot] = Environment.TickCount64;
                        MarkCarrierRemoved(slot);
                        log.Information(localWalk
                            ? "[Proteus] invisible carrier: removed our injected piece (a{0:D4}) from {1} before withdrawing its host"
                            : "[Proteus] invisible carrier: removed our injected piece (a{0:D4}) from {1}", r.ModelSet, slot);
                    }
                    break;
            }
        }
    }

    /// <summary>Glamourer's item in an equipment slot ("RFinger"/"LFinger"), or null when its state cannot be read.</summary>
    private ulong? GlamourerSlotItem(string eqdpSlot)
    {
        try
        {
            return Plugin.Framework.RunOnFrameworkThread(
                () => glamourer.GetObjectState(0)?["Equipment"]?[eqdpSlot]?["ItemId"]?.ToObject<ulong?>())
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Debug("[Proteus] invisible carrier: Glamourer state read failed ({0})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// A walk taken after the Glamourer read, kept local: it publishes nothing to the shared walk maps and raises no
    /// hair event (it can run mid-composite). Null when it is not a real walk (null, empty, or no body-slot model —
    /// the rule ApplyEquippedModels uses), so a mid-redraw teardown never drops a record.
    /// </summary>
    private bool? PostReadWalkShowsOurs(string slot, int modelSet)
    {
        try
        {
            var models = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerModelPaths).GetAwaiter().GetResult();
            if (!CarrierDecisions.IsValidWalk(models, m => DrawnModelPaths.HasBodySlotModel((HashSet<string>)m))) return null;
            var accessories = DrawnModelPaths.EquippedAccessoryModelsFromModels(models!);
            return accessories.TryGetValue(slot, out var p) && p.Contains($"a{modelSet:D4}", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Debug("[Proteus] invisible carrier: walk after the Glamourer read failed ({0})", ex.GetType().Name);
            return null;
        }
    }

    private bool SetAccessoryOnFramework(ulong itemId, string slot)
    {
        int claim = ExpectIndirectRedraw($"{slot} carrier {(itemId == 0 ? "off" : "on")}");
        try
        {
            bool ok = CarrierWrites.Guarded(OnFramework, () => GoneAway("invisible carrier"),
                                            () => glamourer.SetAccessory(itemId, slot));
            if (!ok) WithdrawIndirectRedraw(claim);
            return ok;
        }
        catch (Exception ex)
        {
            WithdrawIndirectRedraw(claim);
            log.Warning(ex, "[Proteus] invisible carrier: SetItem({0},{1}) failed", slot, itemId);
            return false;
        }
    }

    /// <summary>The framework thread, as <see cref="CarrierWrites.Guarded"/> takes it.</summary>
    private static bool OnFramework(Func<bool> write)
        => Plugin.Framework.RunOnFrameworkThread(write).GetAwaiter().GetResult();

    /// <summary>
    /// This instance has been torn down, so it may not put anything on the player.
    /// <para/>
    /// Asked INSIDE the framework-thread write, not before it — see <see cref="CarrierWrites.Guarded"/>, which is
    /// where that ordering lives and is tested. Asked at the call site there is a window between deciding and
    /// writing, and it is wide: deciding to equip a carrier means a Glamourer read first.
    /// <para/>
    /// It matters because a composite outlives the instance that started it: it is a Task and nothing waits for it.
    /// Measured on a reload, where one ran 9.5 seconds and equipped the facewear carrier 2.2 seconds AFTER its
    /// instance was disposed. The item went on the player with the record that owns it in a config that died with
    /// that instance, so the live one had no record of equipping it, would not take it off, and left the player
    /// wearing an eyepatch nothing claimed. Refusing the write leaves the equipping to the instance that is still
    /// there to remember it.
    /// </summary>
    /// <param name="what">Which carrier is being refused, for the log line.</param>
    private bool GoneAway(string what)
    {
        if (!_disposed) return false;
        // Once per kind: a torn-down composite can reach this for every carrier slot it was going to write, and
        // which kinds were refused is the whole content of the line.
        if (_goneAwayLogged.TryAdd(what, 0))
            log.Information("[Proteus] {0}: not touching the player's gear — this Proteus was unloaded while a "
                          + "composite was still running, and the one that replaced it owns the carriers now.", what);
        return true;
    }

    private readonly ConcurrentDictionary<string, byte> _goneAwayLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Remove our injected ring immediately (plugin disable/unload). Falls back to what we remember equipping only
    /// where the walk is silent; an item the walk names in that slot is the player's.
    /// </summary>
    public void RemoveInjectedRing()
    {
        foreach (var (slot, eqdpSlot, _) in InvisibleRing.CarrierSlots)
        {
            if (InvisibleRing.ResolveFor(Plugin.DataManager, log, slot) is not { } r) continue;
            // Teardown takes back only what we equipped; the model is not ownership.
            bool recorded = _injectedCarrierSlots.Contains(slot, StringComparer.Ordinal);
            bool ours = recorded && (IsOurRingWorn(slot, r.ModelSet) || !AccessorySnapshotKnown);
            // The walk can predate the carrier being drawn (a walk taken just before the ask): then Glamourer decides,
            // as in the sweep. QA saw a recorded rir carrier survive an unload that took the glasses off.
            if (recorded && !ours && GlamourerSlotItem(eqdpSlot) == r.ItemId) ours = true;
            if (ours && SetAccessoryOnFramework(0, slot))
            {
                _carrierRemovedAt[slot] = Environment.TickCount64;
                MarkCarrierRemoved(slot);
            }
        }
    }

    /// <summary>Remove our injected glasses immediately (plugin disable/unload), if we recorded equipping the worn pair
    /// (see <see cref="RemoveInjectedRing"/>). Best-effort, framework thread.</summary>
    public void RemoveInjectedGlasses()
    {
        if (InvisibleGlasses.Resolve(Plugin.DataManager, log) is not { } g) return;
        // Teardown takes back only what we equipped; the item is not ownership. An unknown walk is not evidence that
        // the pair is gone, so the record alone carries it there.
        bool ours = OurGlassesAreOn(g) || (_injectedGlasses && !MetSnapshotKnown);
        if (ours && SetGlassesOnFramework(0))
            _injectedGlasses = false;
    }

    // Stable string of the equipped part + accessory + head("_met") + bare-body ("bare:") models, for change
    // detection on redraw: each can change the shell's host or source. Our own carriers are excluded: they are
    // hosts, never sources (tracked by _lastShellHostPaths), and their reverts would defeat the unchanged-inputs gate.
    private string EquipSignature(
        IReadOnlyDictionary<string, string>? models, IReadOnlyDictionary<string, string>? accessories = null,
        IReadOnlyList<string>? metModels = null, IReadOnlyDictionary<string, string>? bareBody = null,
        // The character's own face/hair/tail/ear models: a shell can be cut from them, so a change must invalidate it.
        IReadOnlyList<string>? humanParts = null)
    {
        // Only while we equipped the pair: the carrier's model file is shared by every variant of its set, so a player's
        // own pair of that set must count as a change, or the shell stays redirected onto it and it renders invisible.
        var glassesSet = _injectedGlasses ? InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ModelSet : null;
        bool IsOurCarrier(string modelPath)
            => modelPath.Contains($"a{InvisibleRing.EmperorSetId:D4}", StringComparison.OrdinalIgnoreCase)
            || (glassesSet is int gs && modelPath.Contains($"e{gs:D4}", StringComparison.OrdinalIgnoreCase));

        return string.Join("|",
            (models ?? new Dictionary<string, string>()).Concat(accessories ?? new Dictionary<string, string>())
            .Where(kv => !IsOurCarrier(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .Concat((metModels ?? []).Where(p => !IsOurCarrier(p)).Select(p => $"met={p}"))
            .Concat((bareBody ?? new Dictionary<string, string>())
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"bare:{kv.Key}={kv.Value}"))
            .Concat((humanParts ?? []).Select(p => $"human={p}")));
    }

    private static string? BodyTypeKey(HashSet<string> snapshot)
    {
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in snapshot)
        {
            var bt = UVRemapService.InferBodyType(m);
            if (bt != null) types.Add(bt);
        }
        return types.Count > 0 ? string.Join(",", types) : null;
    }

    /// <summary>The character codes (e.g. "c1401") of the body materials in a snapshot.</summary>
    private static HashSet<string> CharCodeSet(HashSet<string> snapshot)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in snapshot)
        {
            if (UVRemapService.InferBodyType(m) == null) continue;
            var code = ExtractHumanCharCode(m);
            if (code != null) codes.Add(code);
        }
        return codes;
    }

    /// <summary>
    /// The character codes of the wearer's HEAD — face, eyes, hair, tail and ears. The counterpart of
    /// <see cref="CharCodeSet"/>, which answers for the body, and deliberately not a superset of it.
    /// <para/>
    /// The two are NOT the same code. Elezen, Miqo'te, Roegadyn and Lalafell females all wear the Midlander
    /// female body (c0201) under their own face (c0601, c0801, c1001, c1201), and the males the Midlander male
    /// one. Measured against <see cref="CharCodeSet"/>, a Miqo'te's own face material reads as another race's
    /// and is thrown away — which is how a face pack for the character being worn came out inert. Pooling the
    /// two is just as wrong in the other direction: a Midlander face pack would then match her body's code and
    /// pass for hers.
    /// <para/>
    /// Models as well as materials: a face's material can be missing from a materials snapshot the model walk
    /// still names, and the model is the sturdier signal of the two.
    /// </summary>
    internal static HashSet<string> HeadCodeSet(IEnumerable<string>? materials, IEnumerable<string>? models)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { materials, models })
        {
            if (source == null) continue;
            foreach (var p in source)
                if (ShellSurface.KeyFor(p) is { IsBody: false } && ExtractHumanCharCode(p) is { } code)
                    codes.Add(code);
        }
        return codes;
    }

    /// <summary>
    /// <paramref name="codes"/> as one comparable key, or null when there are none. Every "which races is the
    /// character drawn as" check must use this, or WaitForRaceToSettle and SchedulePostRedrawBodyTypeCheck disagree.
    /// </summary>
    private static string? CharCodeKey(HashSet<string> codes)
        => codes.Count > 0 ? string.Join(",", codes.OrderBy(x => x)) : null;

    /// <summary>
    /// True when <paramref name="candidate"/> is a strict, non-empty subset of <paramref name="baseline"/> (both
    /// <see cref="BodyTypeKey"/> values). A pure shrink is a half-loaded walk, not a body swap; a genuine removal
    /// arrives through OnModSettingChanged instead.
    /// </summary>
    private static bool IsStrictSubsetKey(string? candidate, string? baseline)
    {
        if (candidate == null || baseline == null) return false;
        var have = candidate.Split(',');
        var had = new HashSet<string>(baseline.Split(','), StringComparer.OrdinalIgnoreCase);
        if (have.Length >= had.Count) return false;                       // not strictly smaller
        foreach (var t in have) if (!had.Contains(t)) return false;       // brings something new → real
        return true;
    }
}
