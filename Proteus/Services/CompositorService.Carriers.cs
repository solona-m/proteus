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

    // Same guard for the Emperor's-ring injection.
    private const int RingInjectCooldownMs = 5000;
    private long _lastRingInjectTick;

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

    private void MarkCarrierInjected(string slot)
    {
        if (_injectedCarrierSlots.Contains(slot, StringComparer.Ordinal)) return;
        _injectedCarrierSlots = [.. _injectedCarrierSlots, slot];
    }

    private void MarkCarrierRemoved(string slot)
    {
        if (!_injectedCarrierSlots.Contains(slot, StringComparer.Ordinal)) return;
        _injectedCarrierSlots = _injectedCarrierSlots.Where(s => s != slot).ToList();
    }

    // Persisted like the ring slots: removing the pair is only safe when we put it there, and that must survive a reload.
    private bool _injectedGlasses
    {
        get => config.InjectedGlasses;
        set
        {
            if (config.InjectedGlasses == value) return;
            config.InjectedGlasses = value;
            config.Save();
        }
    }

    // Our carrier item worn with no record of equipping it; reported once per session.
    private int _unclaimedGlassesLogged;

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
    }

    /// <summary>At most one outstanding <see cref="ScheduleCarrierRetry"/>; extra requests coalesce onto it.</summary>
    private int _carrierRetryPending;

    /// <summary>
    /// Re-run the carrier reconciles once <paramref name="cooldownRemainingMs"/> has elapsed, when one wanted to
    /// equip and was refused only by its inject cooldown. Goes through the same guards; does not touch the cooldown.
    /// </summary>
    private void ScheduleCarrierRetry(int cooldownRemainingMs)
    {
        if (Interlocked.Exchange(ref _carrierRetryPending, 1) == 1) return;

        // A margin past the window: TickCount64 and the timer are different clocks.
        _ = Task.Delay(Math.Clamp(cooldownRemainingMs, 0, GlassesInjectCooldownMs) + 250)
            .ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _carrierRetryPending, 0);
                try
                {
                    if (_disposed || !config.PluginEnabled || !_secondSkinActive) return;
                    // A composite in flight runs its own reconcile with fresher arguments than these.
                    if (Volatile.Read(ref _compositesInFlight) > 0) return;
                    ReconcileInvisibleGlasses(_lastGearWanted, _lastShellBuilt, _lastShellOnFacewear,
                                              alreadyHosted: true);
                    ReconcileEmperorRing(_lastGearWanted, _lastShellBuilt, _lastShellCarrierSlots);
                }
                catch (Exception ex) { log.Debug("[Proteus] carrier retry failed: {0}", ex.Message); }
            });
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
    /// Whether the pair on the player's face is our carrier item, not merely a pair drawing its model (other variants
    /// of the carrier's model set are real pairs). Checks Glamourer's state; falls back to remembering we equipped it. Any thread.
    /// </summary>
    private bool IsOurGlassesItemWorn(InvisibleGlasses.Identity g)
    {
        if (!IsOurGlassesWorn(g.ModelSet)) return false;
        return WornGlassesRow() is { } row ? row == g.ItemId : _injectedGlasses;
    }

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
        // would be taken for it. Only on a positive reading; an unreadable state keeps the record.
        if (_injectedGlasses && WornGlassesRow() is { } worn && worn != g.ItemId)
        {
            _injectedGlasses = false;
            // A pair of the player's now draws our carrier's model, which the shell may still be redirected onto. The redraw
            // that got us here already took its equip signature (which skipped this set while it was ours), so nothing else
            // will recomposite: do it now, so the shell moves off their pair. Not on a plain revert (none worn): the want
            // branch re-equips below without one.
            if (worn != 0 && IsOurGlassesWorn(g.ModelSet))
            {
                log.Information("[Proteus] invisible glasses: item #{0} replaced our pair on the same model (e{1:D4}) — "
                              + "recompositing to move the shell off it", worn, g.ModelSet);
                TriggerRecomposite("invisible-glasses-released");
            }
        }

        if (want)
        {
            // No adopting a pair already on the face: the carrier is a real item, and a pair we did not equip is not ours
            // to take off later. Nor does the shell host on it: ShellPhase hides the carrier set from the chooser unless the
            // record says we equipped the worn pair, so a player's own carrier item keeps its real model.
            var sinceInject = unchecked(Environment.TickCount64 - _lastGlassesInjectTick);
            if (MetSnapshotKnown && !AnyMetWorn() && sinceInject <= GlassesInjectCooldownMs)
                // Refused only by the cooldown: schedule a retry, since fast callers would otherwise leave the shell hostless.
                ScheduleCarrierRetry((int)(GlassesInjectCooldownMs - sinceInject));

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
                 && IsOurGlassesItemWorn(g))
        {
            // Remove when the feature is off, nothing is hosted, or the shell moved to another host (the carrier would render
            // its real frames). Not on a merely failed build, which is transient.
            //
            // Worn but not recorded as ours: the player's own pair of the same item, so leave it and say so once.
            if (!_injectedGlasses)
            {
                if (Interlocked.Exchange(ref _unclaimedGlassesLogged, 1) == 0)
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
        try { return Plugin.Framework.RunOnFrameworkThread(() => glamourer.SetGlasses(itemId)).GetAwaiter().GetResult(); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] invisible glasses: SetGlasses({0}) failed", itemId); return false; }
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
    private void ReconcileEmperorRing(bool gearWanted, bool shellBuilt, IReadOnlyList<string>? carrierSlots)
    {
        // One pass per carrier slot, then one removal sweep. Each slot resolves its own item id (same model set, different items).
        foreach (var slot in carrierSlots ?? [])
            ReconcileOneCarrier(slot);

        SweepUnusedCarriers(gearWanted, shellBuilt, carrierSlots ?? []);
    }

    /// <summary>
    /// Equip the invisible piece for one slot the shell published a carrier onto. Idempotent.
    /// </summary>
    private void ReconcileOneCarrier(string ringSlot)
    {
        if (InvisibleRing.ResolveFor(Plugin.DataManager, log, ringSlot) is not { } r) return;

        // An invisible piece of that set is already in the slot: nothing to equip. Do not claim ownership: players wear
        // these by choice, and the sweep would later take their glamour off.
        if (IsOurRingWorn(ringSlot, r.ModelSet)) return;

        // Never write to a slot we have not seen to be empty (before the first walk the map is null).
        if (!AccessorySnapshotKnown)
        {
            log.Debug("[Proteus] invisible carrier: no accessory snapshot yet — not equipping into {0} until "
                    + "a walk confirms it is empty", ringSlot);
            return;
        }

        // Occupied by the player's own piece: the walk and the build disagree, so leave their jewellery be.
        if (RingModel(ringSlot) != null) return;

        // Same cooldown as the glasses, with a scheduled retry behind it.
        var sinceRing = unchecked(Environment.TickCount64 - _lastRingInjectTick);
        if (sinceRing <= RingInjectCooldownMs)
        {
            ScheduleCarrierRetry((int)(RingInjectCooldownMs - sinceRing));
            return;
        }

        // Charge the cooldown on the attempt, not the success (see the glasses).
        var equipped = SetAccessoryOnFramework(r.ItemId, ringSlot);
        _lastRingInjectTick = Environment.TickCount64;
        if (equipped)
        {
            MarkCarrierInjected(ringSlot);
            // No follow-up recomposite: the shell is already published at the path this piece loads.
            log.Information("[Proteus] invisible carrier: equipped item #{0} (model a{1:D4}) in {2} — shell already hosted on it",
                r.ItemId, r.ModelSet, ringSlot);
        }
    }

    /// <summary>
    /// Take back every carrier we equipped that this composite no longer hosts on. A whole-build decision, and it
    /// must sweep slots the current build never mentioned.
    /// </summary>
    private void SweepUnusedCarriers(bool gearWanted, bool shellBuilt, IReadOnlyList<string> inUse)
    {
        // Remove our piece when nothing is hosted or the shell built and went elsewhere; not on a merely failed build.
        // Only the "shell moved" removal charges the cooldown (see the glasses).
        bool hostMoved = gearWanted && shellBuilt;
        if (gearWanted && !shellBuilt) return;

        foreach (var (slot, _, _) in InvisibleRing.CarrierSlots)
        {
            // Still hosting on it — leave it on.
            if (inUse.Contains(slot, StringComparer.Ordinal)) continue;
            if (InvisibleRing.ResolveFor(Plugin.DataManager, log, slot) is not { } r) continue;
            if (!IsOurRingWorn(slot, r.ModelSet)) continue;

            // Worn but not recorded as ours: could be the player's own, so do nothing and say so once.
            if (!_injectedCarrierSlots.Contains(slot, StringComparer.Ordinal))
            {
                if (_unclaimedRingSlots.TryAdd(slot, 0))
                    log.Information("[Proteus] invisible carrier: a{0:D4} is worn in {1} but Proteus has no "
                                  + "record of equipping it — leaving it alone. If you did not put it "
                                  + "there yourself (an older build could equip one and forget), take "
                                  + "it off manually.", r.ModelSet, slot);
                continue;
            }

            if (SetAccessoryOnFramework(0, slot))
            {
                if (hostMoved) _lastRingInjectTick = Environment.TickCount64;
                MarkCarrierRemoved(slot);
                log.Information("[Proteus] invisible carrier: removed our injected piece (a{0:D4}) from {1}",
                    r.ModelSet, slot);
            }
        }
    }

    private bool SetAccessoryOnFramework(ulong itemId, string slot)
    {
        try { return Plugin.Framework.RunOnFrameworkThread(() => glamourer.SetAccessory(itemId, slot)).GetAwaiter().GetResult(); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] invisible carrier: SetItem({0},{1}) failed", slot, itemId); return false; }
    }

    /// <summary>
    /// Remove our injected ring immediately (plugin disable/unload). Falls back to what we remember equipping only
    /// where the walk is silent; an item the walk names in that slot is the player's.
    /// </summary>
    public void RemoveInjectedRing()
    {
        foreach (var (slot, _, _) in InvisibleRing.CarrierSlots)
        {
            if (InvisibleRing.ResolveFor(Plugin.DataManager, log, slot) is not { } r) continue;
            // Teardown takes back only what we equipped; the model is not ownership.
            bool ours = _injectedCarrierSlots.Contains(slot, StringComparer.Ordinal)
                     && (IsOurRingWorn(slot, r.ModelSet) || !AccessorySnapshotKnown);
            if (ours && SetAccessoryOnFramework(0, slot))
                MarkCarrierRemoved(slot);
        }
    }

    /// <summary>Remove our injected glasses immediately (plugin disable/unload), if we recorded equipping the worn pair
    /// (see <see cref="RemoveInjectedRing"/>). Best-effort, framework thread.</summary>
    public void RemoveInjectedGlasses()
    {
        if (InvisibleGlasses.Resolve(Plugin.DataManager, log) is not { } g) return;
        // Teardown takes back only what we equipped; the item is not ownership.
        bool ours = _injectedGlasses && (IsOurGlassesItemWorn(g) || !MetSnapshotKnown);
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
