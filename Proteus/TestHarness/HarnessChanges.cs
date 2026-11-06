using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>One slot the harness equipped: what it found there first, and what it put on (read back when it could).</summary>
/// <param name="Requested">The model asked for (mode 2); null for an item id (mode 1).</param>
/// <param name="Verified">Whether <paramref name="Applied"/> was read back after the equip; false when the read-back failed.</param>
internal sealed record GearRecord(string Slot, Worn Original, Worn Applied, (int Set, int Variant)? Requested, bool Verified,
                                  DateTime AtUtc);

/// <summary>A mod's effective state before the harness first held it this session.</summary>
internal sealed record HoldBefore(bool Enabled, int Priority, bool Temporary, bool Configured);

internal sealed record HoldRecord(Guid Collection, string ModDir, bool Enabled, int Priority, HoldBefore Before, DateTime AtUtc);

/// <summary>One undone change: its slot or mod, and restored / cleared / changed / gone / failed (with a code).</summary>
internal sealed record UndoEntry(string Name, string Result, string? Code = null);

internal sealed record PutBackReport(IReadOnlyList<UndoEntry> Gear, IReadOnlyList<UndoEntry> Holds, string Sweep, bool RedrawRequested,
                                    BindingPutBack? Binding = null);

/// <summary>A change route's answer: 200 with a body, or an error status, code and message.</summary>
internal sealed record ChangeResult(int Status, string? Code, string? Message, object? Body)
{
    public static ChangeResult Ok(object body) => new(200, null, null, body);
    public static ChangeResult Fail(int status, string code, string message) => new(status, code, message, null);
}

/// <summary>
/// What the harness equipped and held this session, and how to put it all back (ADR 0002: harness writes are undoable).
/// Equips go through Glamourer (Once, key 0, no lock); holds are Penumbra temporary settings under the harness's own key
/// and source, never saved to the collection. Put-back restores each slot's first original and removes each hold, but
/// never overwrites a slot or removes a hold something else changed since: those are reported <c>changed</c>.
/// <para/>
/// Framework thread only, except <see cref="Stopping"/>, which the host sets from wherever <c>Stop</c> runs.
/// </summary>
internal sealed class HarnessChanges(IHarnessGame game, SessionNames names, IPluginLog? log, string? recordRoot = null,
                                     string session = "", int build = 0)
{
    /// <summary>The harness's temporary-setting key: negative (it locks nobody out) and distinct from design binding's.</summary>
    internal const int HoldKey = -0x50544853;
    internal const string HoldSource = "Proteus test harness";

    private readonly Dictionary<string, GearRecord> gear = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid, string), HoldRecord> holds = new();
    private volatile bool stopping;
    private volatile bool applySettling;
    private HarnessBindings? bindings;

    /// <summary>Design applies, live binding edits and permanent Proteus-mod settings (see <see cref="HarnessBindings"/>).</summary>
    public HarnessBindings Bindings => bindings ??= new HarnessBindings(game, Scrub, log, recordRoot, session, build);

    /// <summary>
    /// A design apply is between its first and final hop, waiting for its deferred work to settle: every change route and
    /// put-back answer 409 busy meanwhile, so nothing lands between the apply and its After readings. Set and cleared by the
    /// glamourer-apply route (cleared in a finally, from whichever thread it ends on).
    /// </summary>
    public bool ApplySettling { get => applySettling; set => applySettling = value; }

    /// <summary>Whether harness equips are recorded (a design apply must come first).</summary>
    public bool EquipsRecorded => gear.Count > 0;

    public bool Stopping => stopping;

    /// <summary>From now on equips and holds are refused (503 harness-stopping); put-back still runs.</summary>
    public void BeginStop() => stopping = true;

    internal string Scrub(string source) => LogAnonymizer.Scrub(names.Scrub(source));

    /// <summary>The gate equips and holds share, in the contract's order; null when they may go ahead.</summary>
    public ChangeResult? Gate(bool jobBusy, bool autoRefitBusy)
    {
        if (stopping) return ChangeResult.Fail(503, "harness-stopping", "the test harness is stopping");
        if (jobBusy) return ChangeResult.Fail(409, "busy", "a harness job is running");
        if (applySettling) return ChangeResult.Fail(409, "busy", "a design apply is still settling; wait for its answer");
        if (autoRefitBusy) return ChangeResult.Fail(409, "auto-refit-busy", "auto refit is working; wait for /state autoRefit.busy == false");
        return null;
    }

    // ── equip ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Equip <paramref name="sentId"/> in <paramref name="slot"/> (a canonical slot name) once <see cref="Gate"/> passed,
    /// recording the slot's first original and what is worn after (read back in the same tick).
    /// </summary>
    public ChangeResult Equip(string slot, ulong sentId, (int Set, int Variant)? requested)
    {
        if (!game.PlayerPresent()) return ChangeResult.Fail(409, "not-logged-in", "there is no character to equip");
        if (game.GearNow() is not { } before)
            return ChangeResult.Fail(503, "glamourer-unavailable", "Glamourer's state cannot be read");
        if (game.CarrierSlots().Contains(slot, StringComparer.OrdinalIgnoreCase))
            return ChangeResult.Fail(409, "carrier-slot", $"Proteus has an invisible carrier in {slot}; it is never replaced");

        // A slot the state leaves out is unknown, not Nothing: putting back item 0 would unequip the real piece.
        if (!before.TryGetValue(slot, out var prev))
            return ChangeResult.Fail(503, "glamourer-unavailable", $"Glamourer's state has no readable {slot} slot");
        var ec = game.SetGear(slot, sentId, 0, 0);
        if (ec == "ItemInvalid") return ChangeResult.Fail(400, "item-invalid", $"Glamourer cannot put {sentId} in {slot}");
        if (ec != null) return ChangeResult.Fail(409, "glamourer-refused", $"Glamourer refused the equip: {ec}");

        Worn? readBack = game.GearNow() is { } after && after.TryGetValue(slot, out var worn) ? worn : null;
        bool verified = readBack != null;
        var applied = readBack ?? new Worn(sentId, 0, 0);
        var original = gear.TryGetValue(slot, out var existing) ? existing.Original : prev;
        gear[slot] = new GearRecord(slot, original, applied, requested, verified, DateTime.UtcNow);
        log?.Information("[Proteus] test harness: equipped {0}: sent {1}, worn {2}, was {3}", slot, sentId, applied.Item, prev.Item);

        var decoded = requested ?? HarnessGear.TryDecode(applied.Item);
        return ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["slot"] = slot, ["sent"] = sentId, ["item"] = applied.Item, ["set"] = decoded?.Set, ["variant"] = decoded?.Variant,
            ["dyes"] = new int[] { applied.Dye1, applied.Dye2 },
            ["previous"] = WornJson(prev), ["original"] = WornJson(original),
            ["readBack"] = verified ? null : false,
        });
    }

    /// <summary>A worn slot as JSON. Dyes as numbers: a byte[] would serialise as base64.</summary>
    private static object WornJson(Worn w) => new { item = w.Item, dyes = new int[] { w.Dye1, w.Dye2 } };

    // ── hold ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hold a mod in a collection with a temporary setting, its options kept. Checks in one order: the temporary-setting
    /// query, someone else's hold, the effective-settings read, the write. A failed read never falls back to "off".
    /// </summary>
    public ChangeResult Hold(Guid collection, string modDir, bool? enabled, int? priority)
    {
        if (game.QueryHold(collection, modDir) is not { } q)
            return ChangeResult.Fail(503, "penumbra-unavailable", "Penumbra could not say whether the mod is held");
        if (q.Present && (q.Locked || q.Source != HoldSource))
        {
            var who = Scrub(q.Source);
            log?.Information("[Proteus] test harness: {0} is held by {1}; not held", modDir, who);
            return ChangeResult.Fail(409, "held-elsewhere", $"{modDir} is held by {who}");
        }

        PenumbraBridge.SettingsRead read;
        try { read = game.ReadSettings(collection, modDir); }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] test harness: reading {0}'s settings failed", modDir);
            read = new PenumbraBridge.SettingsRead(PenumbraBridge.SettingsReadKind.Failed, null);
        }
        if (read.Kind == PenumbraBridge.SettingsReadKind.Failed)
            return ChangeResult.Fail(503, "penumbra-unavailable", "Penumbra could not read the mod's settings; nothing was held");
        var s = read.Settings;
        bool baseEnabled = s?.Enabled ?? false;
        int basePriority = s?.Priority ?? 0;
        var options = s?.Options ?? new Dictionary<string, List<string>>();

        bool newEnabled = enabled ?? baseEnabled;
        int newPriority = priority ?? basePriority;
        if (game.SetHold(collection, modDir, newEnabled, newPriority, options, HoldSource, HoldKey) is { } ec)
            return ChangeResult.Fail(409, "penumbra-refused", $"Penumbra refused the hold: {ec}");

        var key = (collection, modDir);
        var before = holds.TryGetValue(key, out var old) ? old.Before
                   : new HoldBefore(baseEnabled, basePriority, q.Present || s?.Temporary == true, s != null);
        holds[key] = new HoldRecord(collection, modDir, newEnabled, newPriority, before, DateTime.UtcNow);
        game.RequestRedraw();
        log?.Information("[Proteus] test harness: holding {0} enabled={1} priority={2}", modDir, newEnabled, newPriority);
        return ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["mod"] = modDir, ["collection"] = collection, ["enabled"] = newEnabled, ["priority"] = newPriority,
            ["before"] = new { enabled = before.Enabled, priority = before.Priority, temporary = before.Temporary,
                               configured = before.Configured },
            ["redrawRequested"] = true,
        });
    }

    // ── put back ────────────────────────────────────────────────────────────

    /// <summary>
    /// Undo every hold, then every equip, then sweep leftover harness holds, then the binding session. Never refused. Only
    /// failures stay recorded.
    /// </summary>
    public PutBackReport PutBack() => PutBack(skipHalfRecordedBinding: false);

    /// <inheritdoc cref="PutBack()"/>
    /// <param name="skipHalfRecordedBinding">An apply is still settling (off/unload mid-apply): leave the binding session as
    /// recorded and log it rather than undo half-recorded state.</param>
    public PutBackReport PutBack(bool skipHalfRecordedBinding)
    {
        var holdResults = new List<UndoEntry>();
        var gearResults = new List<UndoEntry>();
        bool redraw = false;
        var collections = new HashSet<Guid>();

        foreach (var (key, rec) in holds.ToList())
        {
            collections.Add(rec.Collection);
            UndoEntry entry;
            try { entry = UndoHold(rec); }
            catch (Exception ex)
            {
                log?.Warning(ex, "[Proteus] test harness: putting back {0} failed", rec.ModDir);
                entry = new UndoEntry(rec.ModDir, "failed", "internal");
            }
            if (entry.Result != "failed") holds.Remove(key);
            if (entry.Result == "cleared") redraw = true;
            holdResults.Add(entry);
        }

        foreach (var slot in HarnessGear.Slots)
        {
            if (!gear.TryGetValue(slot, out var rec)) continue;
            UndoEntry entry;
            try { entry = UndoGear(rec); }
            catch (Exception ex)
            {
                log?.Warning(ex, "[Proteus] test harness: putting back {0} failed", slot);
                entry = new UndoEntry(slot, "failed", "internal");
            }
            if (entry.Result != "failed") gear.Remove(slot);
            gearResults.Add(entry);
        }

        string sweep = "skipped";
        try
        {
            if (game.PlayerCollection() is { } current) collections.Add(current);
            if (collections.Count > 0)
            {
                sweep = "ok";
                foreach (var c in collections)
                {
                    var ec = game.SweepHolds(c, HoldKey);
                    if (ec == "Success") redraw = true;
                    else if (ec != "NothingChanged") sweep = "failed";
                }
            }
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] test harness: the hold sweep failed");
            sweep = "failed";
        }

        if (redraw) game.RequestRedraw();
        log?.Information("[Proteus] test harness: put back {0} slot(s), {1} hold(s); sweep {2}",
                         gearResults.Count, holdResults.Count, sweep);

        // Last: the Glamourer state goes back over what gear put-back restored (its originals are post-apply states).
        BindingPutBack? binding = null;
        try { binding = bindings?.PutBack(skipHalfRecordedBinding); }
        catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: putting back the binding session failed"); }
        return new PutBackReport(gearResults, holdResults, sweep, redraw || binding?.Restored == true, binding);
    }

    private UndoEntry UndoHold(HoldRecord rec)
    {
        if (game.QueryHold(rec.Collection, rec.ModDir) is not { } q) return new UndoEntry(rec.ModDir, "failed", "penumbra-unavailable");
        if (!q.Present) return new UndoEntry(rec.ModDir, "gone");
        if (q.Locked || q.Source != HoldSource) return new UndoEntry(rec.ModDir, "changed");
        return game.ClearHold(rec.Collection, rec.ModDir) switch
        {
            null or "NothingChanged" => new UndoEntry(rec.ModDir, "cleared"),
            "ModMissing" or "CollectionMissing" => new UndoEntry(rec.ModDir, "gone"),
            var ec => new UndoEntry(rec.ModDir, "failed", ec),
        };
    }

    private UndoEntry UndoGear(GearRecord rec)
    {
        if (game.GearNow() is not { } now) return new UndoEntry(rec.Slot, "failed", "glamourer-unavailable");
        if (!now.TryGetValue(rec.Slot, out var worn)) return new UndoEntry(rec.Slot, "failed", "glamourer-unavailable");
        if (!StillOurs(rec, worn)) return new UndoEntry(rec.Slot, "changed");
        return game.SetGear(rec.Slot, rec.Original.Item, rec.Original.Dye1, rec.Original.Dye2) is { } ec
            ? new UndoEntry(rec.Slot, "failed", ec)
            : new UndoEntry(rec.Slot, "restored");
    }

    /// <summary>
    /// Whether the slot still holds what the harness put on: exactly <see cref="GearRecord.Applied"/>; or, unverified,
    /// anything but a Proteus carrier; or the requested model in any spelling with the same dyes.
    /// </summary>
    private bool StillOurs(GearRecord rec, Worn worn)
    {
        if (worn == rec.Applied) return true;
        if (!rec.Verified) return !game.CarrierSlots().Contains(rec.Slot, StringComparer.OrdinalIgnoreCase);
        return rec.Requested is { } req && HarnessGear.TryDecode(worn.Item) == req
            && worn.Dye1 == rec.Applied.Dye1 && worn.Dye2 == rec.Applied.Dye2;
    }

    // ── views ───────────────────────────────────────────────────────────────

    /// <summary>What <c>/state.harnessChanges</c> shows.</summary>
    public object View() => new
    {
        stopping,
        gear = HarnessGear.Slots.Where(gear.ContainsKey).Select(s => gear[s]).Select(g => new
        {
            slot = g.Slot, original = WornJson(g.Original), applied = WornJson(g.Applied),
            requested = g.Requested is { } r ? new { set = r.Set, variant = r.Variant } : null,
            verified = g.Verified, atUtc = g.AtUtc,
        }).ToList(),
        holds = holds.Values.Select(h => new
        {
            mod = h.ModDir, collection = h.Collection, enabled = h.Enabled, priority = h.Priority,
            before = new { enabled = h.Before.Enabled, priority = h.Before.Priority, temporary = h.Before.Temporary,
                           configured = h.Before.Configured },
            atUtc = h.AtUtc,
        }).ToList(),
        binding = bindings?.View(),
        applySettling,
    };

    /// <summary>The slots and mods still recorded, for the warning when put-back cannot run.</summary>
    public string Leftovers()
        => string.Join(", ", gear.Keys.Concat(holds.Values.Select(h => h.ModDir)).Concat(bindings?.Leftovers() ?? []));

    internal bool Empty => gear.Count == 0 && holds.Count == 0 && bindings?.HasSession != true;
}
