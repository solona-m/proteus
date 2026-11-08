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
                                    BindingPutBack? Binding = null)
{
    /// <summary>The Studio session put-back ended (or left recorded), if there was one.</summary>
    public IReadOnlyList<StudioPutBack> Studio { get; init; } = [];

    /// <summary>The test images the matcap seed route wrote: deleted, changed (left) or gone.</summary>
    public IReadOnlyList<UndoEntry> MatcapImages { get; init; } = [];

    /// <summary>The library mods a matcap job changed the setting of (put back through the binding session's ledger).</summary>
    public IReadOnlyList<string> MatcapMods { get; init; } = [];

    /// <summary>The sidecars the sidecar-edit route saved into, put back.</summary>
    public IReadOnlyList<SidecarUndo> Sidecar { get; init; } = [];

    /// <summary>The auto-refit override: cleared or none, and the mods auto refit wrote into while it stood (not undone).</summary>
    public (string Result, IReadOnlyList<string> RefitsWhileOverridden) AutoRefit { get; init; } = ("none", []);

    /// <summary>The output locks, move fault, shell fault and auto-redraw override (issue sweep D).</summary>
    public OutputPutBack? Output { get; init; }
}

/// <summary>What put-back did about the shell output routes: each held file, the move fault, the shell fault (cleared and
/// recomposited) and the auto-redraw override.</summary>
internal sealed record OutputPutBack(IReadOnlyList<UndoEntry> Locks, string MoveFault, string Fault, string AutoRedraw)
{
    public static readonly OutputPutBack Nothing = new([], "none", "none", "none");
}

/// <summary>The harness's session-only auto-redraw switch: what it set, and the effective state before the first set.</summary>
internal sealed record AutoRedrawOverrideRecord(bool Enabled, bool BeforeEffective, DateTime AtUtc);

/// <summary>The harness's session-only auto-refit switch: what it set, and the effective state before the first set.</summary>
internal sealed record AutoRefitOverrideRecord(bool Enabled, bool BeforeEffective, DateTime AtUtc);

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

    /// <summary>Test images the matcap seed route wrote (path → hash): deleted at put-back while unchanged.</summary>
    private readonly Dictionary<string, string> seededImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Library mods a matcap job wrote a setting of this session.</summary>
    private readonly HashSet<string> matcapMods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record a test image the seed route wrote, to delete at put-back.</summary>
    public void NoteSeededImage(string path, byte[] bytes) => seededImages[path] = HairPortPlan.Sha8(bytes);

    /// <summary>Record that a matcap job changed a library's setting (the Before is in the binding session's ledger).</summary>
    public void NoteMatcapMod(string modDir) => matcapMods.Add(modDir);

    private List<UndoEntry> PutBackSeededImages()
    {
        var results = new List<UndoEntry>();
        foreach (var (path, sha) in seededImages.ToList())
        {
            string name = System.IO.Path.GetFileName(path);
            UndoEntry entry;
            try
            {
                if (!System.IO.File.Exists(path)) entry = new UndoEntry(name, "gone");
                else if (HairPortPlan.Sha8(System.IO.File.ReadAllBytes(path)) != sha) entry = new UndoEntry(name, "changed");
                else { System.IO.File.Delete(path); entry = new UndoEntry(name, "deleted"); }
            }
            catch (Exception ex)
            {
                log?.Warning(ex, "[Proteus] test harness: putting back the seeded image {0} failed", name);
                entry = new UndoEntry(name, "failed", "internal");
            }
            if (entry.Result != "failed") seededImages.Remove(path);
            results.Add(entry);
        }
        return results;
    }
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

    /// <summary>From now on equips, holds and sidecar edits are refused (503 harness-stopping); put-back still runs. A
    /// sidecar edit in flight finishes first (it holds no framework hop, so this never deadlocks).</summary>
    public void BeginStop()
    {
        stopping = true;
        Sidecar.BeginStop();
    }

    // ── sidecar edits and the auto-refit override (idle disk I/O routes) ────

    /// <summary>Made with the session, never lazily: a stop and the first edit on two threads must see the same ledger.</summary>
    private readonly HarnessSidecar sidecar =
        new(recordRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proteus-harness-records"), session, log);

    /// <summary>The sidecar-edit route's backups and ledger.</summary>
    public HarnessSidecar Sidecar => sidecar;

    /// <summary>Earlier sessions' sidecar ledgers left without a put-back, found at start (<c>/state.leftoverSidecar</c>).</summary>
    public IReadOnlyList<LeftoverSidecar> LeftoverSidecars { get; set; } = [];

    private AutoRefitOverrideRecord? autoRefitOverride;

    // ── shell output failures (issue sweep D) ──

    /// <summary>The output-lock route's handles and move fault.</summary>
    public HarnessOutputLocks OutputLocks { get; } = new(log);

    private bool shellFaultArmed;
    private AutoRedrawOverrideRecord? autoRedrawOverride;

    /// <summary>Record that the shell-fault route armed the fault (cleared at put-back, with a recomposite).</summary>
    public void NoteShellFault(bool armed) => shellFaultArmed = armed;

    /// <summary>Record the session override the auto-redraw route set; the first set keeps its Before.</summary>
    public AutoRedrawOverrideRecord NoteAutoRedrawOverride(bool enabled, bool beforeEffective)
        => autoRedrawOverride = new AutoRedrawOverrideRecord(enabled, autoRedrawOverride?.BeforeEffective ?? beforeEffective, DateTime.UtcNow);

    /// <summary>
    /// Releases the output locks, disarms the move fault, clears the shell fault and the auto-redraw override — memory and
    /// handles only, no framework hop, so the off-thread stop runs it too. Whether a recomposite is owed (the fault left a
    /// texture out of the manifest) is in the result's <see cref="OutputPutBack.Fault"/> == "cleared".
    /// </summary>
    internal OutputPutBack PutBackOutput()
    {
        try
        {
            var (released, _, moveFault) = OutputLocks.ReleaseAll("put back");
            string fault = "none";
            if (shellFaultArmed || game.ClearShellFault())
            {
                game.ClearShellFault();
                fault = "cleared";
            }
            shellFaultArmed = false;
            string redraw = "none";
            if (autoRedrawOverride != null)
            {
                game.SetAutoRedrawOverride(null);
                autoRedrawOverride = null;
                redraw = "cleared";
            }
            return new OutputPutBack(released.Select(r => new UndoEntry(r.File, r.Result)).ToList(), moveFault, fault, redraw);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] test harness: putting back the output routes failed");
            return OutputPutBack.Nothing;
        }
    }

    /// <summary>Record the session override the auto-refit route set; the first set keeps its Before.</summary>
    public AutoRefitOverrideRecord NoteAutoRefitOverride(bool enabled, bool beforeEffective)
        => autoRefitOverride = new AutoRefitOverrideRecord(enabled, autoRefitOverride?.BeforeEffective ?? beforeEffective, DateTime.UtcNow);

    /// <summary>Put the sidecars back and clear the override, from any thread (the off-thread stop): file work, the cache's
    /// forget and an in-memory switch, no framework hop. The recomposite is the caller's.</summary>
    internal (List<SidecarUndo> Sidecar, (string, IReadOnlyList<string>) AutoRefit) PutBackOffThreadParts()
    {
        List<SidecarUndo> sidecars;
        try { sidecars = sidecar.PutBack(game.HasStaged, game.ForgetSidecar); }
        catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: putting back the sidecars failed"); sidecars = []; }

        (string, IReadOnlyList<string>) refit = ("none", []);
        try
        {
            if (autoRefitOverride != null)
            {
                game.SetAutoRefitOverride(null);
                refit = ("cleared", game.TakeRefitsWhileOverridden());
                autoRefitOverride = null;
                if (refit.Item2.Count > 0)
                    log?.Information("[Proteus] test harness: auto refit wrote into {0} while its override stood (not undone by put-back)",
                                     string.Join(", ", refit.Item2));
            }
        }
        catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: clearing the auto-refit override failed"); }
        return (sidecars, refit);
    }

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
        // Only a hold in the player's own collection changes what the character draws; a redraw for another collection's
        // would itself wake every watcher and hide what QA is checking (that they ignore other collections).
        bool redraw = game.PlayerCollection() == collection;
        if (redraw) game.RequestRedraw();
        log?.Information("[Proteus] test harness: holding {0} enabled={1} priority={2}", modDir, newEnabled, newPriority);
        return ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["mod"] = modDir, ["collection"] = collection, ["enabled"] = newEnabled, ["priority"] = newPriority,
            ["before"] = new { enabled = before.Enabled, priority = before.Priority, temporary = before.Temporary,
                               configured = before.Configured },
            ["redrawRequested"] = redraw,
        });
    }

    // ── put back ────────────────────────────────────────────────────────────

    /// <summary>
    /// Undo every hold, then every equip, then sweep leftover harness holds, then end the Studio session, then the binding
    /// session, each in its own try/catch. Never refused. Only failures stay recorded (and a Studio session something
    /// else touched).
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
            // Every collection Penumbra lists, so a hold an earlier session left in another collection goes too; the
            // recorded ones plus the player's when the list cannot be read.
            if (game.Collections() is { } all) foreach (var (id, _) in all) collections.Add(id);
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

        // The Studio after holds and gear (the order agreed with design binding's put-back, which comes after it); its own
        // try/catch, so a throwing step above never stops it.
        var studioResults = new List<StudioPutBack>();
        if (studio is { } session)
        {
            StudioPutBack entry;
            try
            {
                entry = game.Studio is { } port
                    ? port.EndSession(session, refreshGame: true)
                    : new StudioPutBack(session.ModDir, "failed", "none", "studio-unavailable", true);
            }
            catch (Exception ex)
            {
                log?.Warning(ex, "[Proteus] test harness: putting back the Studio session on {0} failed", session.ModDir);
                entry = new StudioPutBack(session.ModDir, "failed", "none", "internal", true);
            }
            if (!entry.Keep) studio = null;
            studioResults.Add(entry);
        }

        // After the Studio and before the binding session: the sidecars the harness saved, and the auto-refit override.
        var (sidecars, autoRefit) = PutBackOffThreadParts();
        // The shell output routes: a cleared shell fault left a texture out of the manifest, so the composite puts it back.
        var output = PutBackOutput();
        if (output.Fault == "cleared")
        {
            try { game.Recomposite("test harness: shell fault cleared"); }
            catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: the recomposite after clearing the shell fault failed"); }
        }
        if (sidecars.Any(s => s.Result == "restored"))
        {
            try { game.Recomposite("test harness: sidecar put back"); }
            catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: the recomposite after the sidecar put-back failed"); }
        }

        if (redraw) game.RequestRedraw();
        log?.Information("[Proteus] test harness: put back {0} slot(s), {1} hold(s); sweep {2}; studio {3}",
                         gearResults.Count, holdResults.Count, sweep,
                         studioResults.Count == 0 ? "none" : string.Join(", ", studioResults.Select(s => $"{s.Files}/{s.Panel}{(s.Code is { } c ? $" ({c})" : "")}")));

        // Last: the Glamourer state goes back over what gear put-back restored (its originals are post-apply states).
        BindingPutBack? binding = null;
        try { binding = bindings?.PutBack(skipHalfRecordedBinding); }
        catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: putting back the binding session failed"); }

        // The matcap seed route's images: files of the harness's own, deleted while still what it wrote.
        List<UndoEntry> images;
        try { images = PutBackSeededImages(); }
        catch (Exception ex) { log?.Warning(ex, "[Proteus] test harness: putting back the matcap images failed"); images = []; }
        var mods = matcapMods.ToList();
        matcapMods.Clear();
        return new PutBackReport(gearResults, holdResults, sweep, redraw || binding?.Restored == true, binding)
               { Studio = studioResults, MatcapImages = images, MatcapMods = mods, Sidecar = sidecars, AutoRefit = autoRefit,
                 Output = output };
    }

    // ── the Studio ──────────────────────────────────────────────────────────

    private StudioSession? studio;

    /// <summary>
    /// A Studio session is recorded: from the first <c>studio-open</c> until put-back ends it. While it is, design
    /// binding's apply refuses (put-back-first). A put-back that finds the session touched leaves it recorded.
    /// </summary>
    public bool HasStudioSession => studio != null;

    internal StudioSession? StudioSessionNow => studio;

    /// <summary>The session as <c>/studio</c> and <c>/state</c> show it; null with none.</summary>
    internal object? StudioSessionView() => studio is not { } s ? null : new
    {
        modDir = s.ModDir, file = s.File, windowOpened = s.WindowOpened, editVersion = s.EditVersion, snapshotTaken = true,
        filesSaved = s.Files.Select(f => new { file = f.Key, sha256 = f.Value }).ToList(),
    };

    /// <summary>Open a mod's model in the Studio once the gate and the folder check passed. One session at a time.</summary>
    public ChangeResult StudioOpen(string modDir, string model, bool show)
    {
        if (game.Studio is not { } port) return ChangeResult.Fail(503, "studio-unavailable", "the Studio is not available");
        if (studio != null)
            return ChangeResult.Fail(409, "put-back-first", $"a Studio session on {studio.ModDir} is recorded; put back first");
        var o = port.Open(modDir, model, show);
        if (o.Refused is { } r) return ChangeResult.Fail(r.Status, r.Code, r.Message);
        studio = new StudioSession(modDir, o.File!, o.Snapshot!, o.WindowOpened) { EditVersion = o.EditVersion };
        log?.Information("[Proteus] test harness: opened {0} in the Studio ({1})", studio.File, modDir);
        return ChangeResult.Ok(WithSession(o.State));
    }

    /// <summary>Set the Studio's controls for the session's model.</summary>
    public ChangeResult StudioSet(StudioSetRequest request)
    {
        if (SessionRefusal() is { } refused) return refused;
        var o = game.Studio!.Set(request, studio!);
        if (o.Refused is { } r) return ChangeResult.Fail(r.Status, r.Code, r.Message);
        studio!.EditVersion = o.EditVersion;
        return ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["applied"] = new
            {
                tool = request.Tool, mode = request.Mode, view = request.View, mirror = request.Mirror, adjacent = request.Adjacent, falloffMm = request.FalloffMm,
                moveSkin = request.MoveSkin, unlockAll = request.UnlockAll ? true : (bool?)null,
                part = request.Part,
            },
            ["locks"] = o.Locks.Select(l => new { label = l.Label, result = l.Result }).ToList(),
            ["chosen"] = o.Chosen,
            ["state"] = WithSession(o.State),
        });
    }

    /// <summary>One drag (or undo) of the session's model, saved; what it saved is recorded for put-back.</summary>
    public ChangeResult StudioDrag(StudioDragRequest request)
    {
        if (SessionRefusal() is { } refused) return refused;
        var o = game.Studio!.Drag(request, studio!);
        // Only a drag that went ahead is recorded: a refusal's counters are not the harness's.
        if (o.Refused is { } r) return ChangeResult.Fail(r.Status, r.Code, r.Message);
        studio!.Record(o.Files, o.EditVersion);
        return ChangeResult.Ok(new Dictionary<string, object?>
        {
            ["kind"] = o.Kind, ["moved"] = o.Moved, ["nodes"] = o.Nodes, ["worstMm"] = o.WorstMm,
            ["pivot"] = o.Pivot is { } p ? new[] { p.X, p.Y, p.Z } : null, ["saved"] = o.Saved,
            ["status"] = o.Status is { } text ? new { text = Scrub(text), isError = o.StatusIsError } : null,
            ["state"] = WithSession(o.State),
        });
    }

    /// <summary>The Matcaps tool's click preview on the session's model: a Penumbra temporary mod, never a mod write.</summary>
    public ChangeResult StudioMatcapPreview(StudioMatcapPreviewRequest request)
    {
        if (SessionRefusal() is { } refused) return refused;
        var o = game.Studio!.MatcapPreview(request, studio!);
        if (o.Refused is { } r) return ChangeResult.Fail(r.Status, r.Code, r.Message);
        return ChangeResult.Ok(new Dictionary<string, object?> { ["preview"] = o.Preview, ["state"] = WithSession(o.State) });
    }

    /// <summary>The state body with the session added.</summary>
    internal Dictionary<string, object?> WithSession(IReadOnlyDictionary<string, object?>? state)
    {
        var body = state != null ? new Dictionary<string, object?>(state) : new Dictionary<string, object?>();
        if (body.TryGetValue("status", out var line) && line is StudioStatusLine s0)
            body["status"] = new { text = Scrub(s0.text), isError = s0.isError };
        body["harnessOpened"] = studio is { } s && game.Studio is { } port && port.IsOpen(s.ModDir, s.File);
        body["session"] = StudioSessionView();
        return body;
    }

    private ChangeResult? SessionRefusal()
    {
        if (game.Studio is not { } port) return ChangeResult.Fail(503, "studio-unavailable", "the Studio is not available");
        if (studio is not { } s) return ChangeResult.Fail(409, "no-studio-session", "open a model with /actions/studio-open first");
        if (!port.IsOpen(s.ModDir, s.File))
            return ChangeResult.Fail(409, "studio-changed", "the Studio no longer has the model the harness opened");
        return null;
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
        studio = StudioSessionView(),
        binding = bindings?.View(),
        applySettling,
        sidecar = sidecar.View(),
        autoRefitOverride = autoRefitOverride is { } o ? new { enabled = o.Enabled, before = o.BeforeEffective, atUtc = o.AtUtc } : null,
        output = new
        {
            locksActive = OutputLocks.Active,
            shellFault = shellFaultArmed,
            autoRedrawOverride = autoRedrawOverride is { } r ? new { enabled = r.Enabled, before = r.BeforeEffective, atUtc = r.AtUtc } : null,
        },
    };

    /// <summary>The slots and mods still recorded, for the warning when put-back cannot run.</summary>
    public string Leftovers()
        => string.Join(", ", gear.Keys.Concat(holds.Values.Select(h => h.ModDir)).Concat(bindings?.Leftovers() ?? [])
                                 .Concat(studio is { } s
                                     ? [$"Studio edits in {s.ModDir} ({(s.Files.Count == 0 ? "nothing saved" : string.Join(", ", s.Files.Keys))}): press Undo saved changes"]
                                     : [])
                                 .Concat(sidecar.Leftovers())
                                 .Concat(autoRefitOverride != null ? ["the auto-refit override"] : [])
                                 .Concat(OutputLocks.Active ? ["output locks"] : [])
                                 .Concat(shellFaultArmed ? ["the shell fault"] : [])
                                 .Concat(autoRedrawOverride != null ? ["the auto-redraw override"] : []));

    internal bool Empty => gear.Count == 0 && holds.Count == 0 && bindings?.HasSession != true && studio == null
                        && sidecar.Empty && autoRefitOverride == null
                        && !OutputLocks.Active && !shellFaultArmed && autoRedrawOverride == null;
}
