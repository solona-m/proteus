using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Proteus.Interop;

namespace Proteus.Services;

// ── Persisted model (design_bindings.json) ──────────────────────────────────

public class DesignBindingStore
{
    public int Version { get; set; } = 1;
    public Dictionary<Guid, DesignBinding> Bindings { get; set; } = new();
}

public class DesignBinding
{
    public Guid DesignId { get; set; }
    public string? DesignName { get; set; }
    public DateTime CapturedUtc { get; set; }
    public List<ProteusModBinding> Mods { get; set; } = new();
}

/// <summary>Captured state of one Proteus overlay mod at the moment a design was saved.</summary>
public class ProteusModBinding
{
    public string ModDirectory { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Priority { get; set; }

    /// <summary>Penumbra option group → selected option names.</summary>
    public Dictionary<string, List<string>> Options { get; set; } = new();

    /// <summary>Effective colors at capture time (in-memory override on restore; never written to metadata.json).</summary>
    public OverlayColorOverride Colors { get; set; } = new();

    /// <summary>Effective gear-layer settings at capture time — layer, shader, effect, scroll speed and
    /// tiling. Same contract as Colors: applied as an in-memory override, metadata.json is untouched.</summary>
    public OverlayGearOverride Gear { get; set; } = new();

    /// <summary>The mod-wide overlay tab/stack order top-first at capture time
    /// (<see cref="Configuration.ModStackEntry"/> keys). Same contract as Colors/Gear: applied as an
    /// in-memory override at composite time, the global stack-order config is untouched. Empty ⇒ nothing
    /// captured (the mod was never restacked), so the composite keeps the global order.</summary>
    public List<string> StackOrder { get; set; } = new();
}

/// <summary>
/// Binds the current Proteus state to a Glamourer design (keyed by GUID) on save, and restores it
/// on apply. Observer-only: Proteus never applies designs. Restore writes Penumbra enable/priority/
/// options but applies colors as a non-destructive in-memory override (metadata.json is untouched).
/// Apply detection is heuristic — a unique gear match against the player's current state.
/// </summary>
public class DesignBindingService : IDisposable
{
    // A design must apply at least this many equipment slots to be a heuristic candidate; gearless
    // designs never match (safe abstain). "Most designs save everything including gear" so real
    // outfits apply ~10+.
    private const int MinGearSlots = 3;
    private const int RestoreSuppressMs = 2000;

    // Widest gap allowed between the foreign Reapply and the Gearset finalization for the pair to read as
    // one automation apply. Measured in-game at 30-450 ms; the headroom covers a slow equipment load
    // without widening this into "any gear change counts". See IsInferredAutomationApply.
    private const int AutomationPairWindowMs = 2000;

    // How long we keep expecting the Gearset that OUR OWN redraw causes.
    //
    // It bounds a ONE-SHOT expectation, not a blackout — see CompositorService.ConsumeOwnRedrawEcho for why
    // that distinction is what keeps a restore from suppressing the player's next real gearset change. That
    // also makes this generous by choice rather than by necessity: the expectation is armed at the moment
    // the redraw is REQUESTED, which is after the composite has finished, so it never has to outlast a
    // composite — only the game's own redraw-to-equipment-load latency, which is well under a second. The
    // headroom costs nothing while the redraw does arrive, and a redraw that never reaches the game
    // withdraws the expectation immediately instead of waiting this out (CancelOwnRedrawEcho).
    private const int OwnRedrawEchoMs = 10000;

    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly SidecarDiscoveryService discovery;
    private readonly CompositorService compositor;
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    // Encoder is write-only (it has no effect on the read side that shares this): design names are
    // user-authored and often non-ASCII, and escaping them makes the store unreadable. See ProteusJson.
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Encoder = ProteusJson.Encoder };

    private readonly string storePath;
    private readonly object gate = new();
    private DesignBindingStore store = new();

    // All of the below are touched only on the framework thread (watcher callbacks marshal first).
    private Dictionary<string, OverlayColorOverride>? activeOverride;
    private Dictionary<string, OverlayGearOverride>? activeGearOverride;
    private Dictionary<string, List<string>>? activeStackOverride;
    private Guid? activeDesignId;
    private long suppressUntilTick;
    private readonly Dictionary<Guid, JObject?> designCache = new();

    // Boot restore. Cadence and give-up for the poll below; the deadline clock only starts once the
    // local player exists, so the timeout measures "Glamourer isn't answering", not "the user is
    // sitting at the title screen".
    private const int BootRestorePollMs    = 250;
    private const int BootRestoreTimeoutMs = 20000;

    // How long a MISMATCH is treated as "the state hasn't settled yet" rather than as the answer. Our
    // own Dispose pulls the injected glasses/ring on the way out and the game applies those removals
    // asynchronously, so the first readable state after a reload can still be in motion. Kept short
    // because it is also the worst-case extra delay on the genuinely-unbound path, where the boot
    // composite waits this out before running.
    private const int BootSettleMs = 3000;

    private long bootDeadlineTick;          // 0 until the local player first exists
    private long bootSettleUntilTick;
    private long lastBootRestorePollTick;
    private bool bootStep1Reported;         // the deterministic step-1 reasons are logged once, not per poll
    private int  bootRestoreDone = 1;       // 1 = resolved or never armed; the ctor sets 0 when arming

    public DesignBindingService(
        PenumbraBridge penumbra, GlamourerBridge glamourer, SidecarDiscoveryService discovery,
        CompositorService compositor, Configuration config, IDalamudPluginInterface pluginInterface,
        IFramework framework, IPluginLog log)
    {
        this.penumbra   = penumbra;
        this.glamourer  = glamourer;
        this.discovery  = discovery;
        this.compositor = compositor;
        this.config     = config;
        this.framework  = framework;
        this.log        = log;

        storePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "design_bindings.json");
        Load();

        glamourer.LocalPlayerStateFinalized += OnGlamourerStateFinalized;

        // Adopt whatever binding the character is already wearing, once, at load. Glamourer fires no
        // apply signal on a plugin reload — the design is already applied — so without this the
        // overrides stay null and the first composite paints metadata colours over a design the player
        // is still visibly wearing.
        //
        // Armed only when there was something active AND a way to verify it. A null LastActiveDesignId
        // means nothing was active (a revert, or an explicit clear), which must stay cleared. Glamourer
        // being absent is a precondition rather than something the poll discovers: GetObjectState would
        // return null forever and the boot composite would be held for the whole timeout for nothing.
        if (config.DesignBindingEnabled && glamourer.IsAvailable && config.LastActiveDesignId is { } lastId)
        {
            Volatile.Write(ref bootRestoreDone, 0);
            framework.Update += OnBootRestoreTick;
            log.Information("[Proteus] design-binding: boot restore armed (last active {0}); boot composite held.", lastId);
        }
        else
        {
            log.Debug("[Proteus] design-binding: no boot restore (enabled={0}, glamourer={1}, lastActive={2}).",
                config.DesignBindingEnabled, glamourer.IsAvailable,
                config.LastActiveDesignId?.ToString() ?? "(none)");
        }
    }

    public void Dispose()
    {
        glamourer.LocalPlayerStateFinalized -= OnGlamourerStateFinalized;
        Volatile.Write(ref bootRestoreDone, 1);
        framework.Update -= OnBootRestoreTick;   // idempotent; safe when it was never subscribed
    }

    // ── UI / accessors ─────────────────────────────────────────────────────────

    public Guid? ActiveDesignId { get { lock (gate) return activeDesignId; } }

    /// <summary>True while a boot restore is pending, so the first composite must wait for its
    /// overrides. Read once by Plugin's constructor — see CompositorService.BootCompositeHold.</summary>
    public bool BootRestoreArmed => Volatile.Read(ref bootRestoreDone) == 0;

    public IReadOnlyList<DesignBinding> Bindings
    {
        get { lock (gate) return store.Bindings.Values.OrderByDescending(b => b.CapturedUtc).ToList(); }
    }

    public bool HasBinding(Guid id) { lock (gate) return store.Bindings.ContainsKey(id); }

    public void RemoveBinding(Guid id)
    {
        bool wasActive;
        lock (gate)
        {
            if (!store.Bindings.Remove(id)) return;
            designCache.Remove(id);
            wasActive = activeDesignId == id;
            Save();
        }
        // Via ClearOverrides so the GEAR and STACK overrides are un-published too — clearing them in
        // memory while only the colour null reached the compositor left it reading dead dictionaries
        // until the next composite.
        if (wasActive && ClearOverrides())
            compositor.TriggerRecomposite($"design-binding-remove:{id}");
    }

    // ── Capture (called by the design-file watcher; any thread) ─────────────────

    /// <summary>Called when a design's {guid}.json is written. Marshals to the framework thread.</summary>
    public void OnDesignSaved(Guid designId)
    {
        if (!config.DesignBindingEnabled) return;
        framework.RunOnFrameworkThread(() => Capture(designId));
    }

    /// <summary>
    /// Called when a design's {guid}.json is deleted. Drops the binding (which also clears the
    /// cached design JObject and, if the deleted design's override is active, the live override).
    /// Runs even when DesignBindingEnabled is off, because stale bindings for vanished designs
    /// are never useful and would pollute future ambiguous-match resolution.
    /// </summary>
    public void OnDesignDeleted(Guid designId)
    {
        framework.RunOnFrameworkThread(() =>
        {
            string? name;
            lock (gate)
            {
                if (!store.Bindings.TryGetValue(designId, out var b)) return;
                name = b.DesignName;
            }
            RemoveBinding(designId);
            log.Information("[Proteus] Removed binding for deleted Glamourer design {0}.", name ?? designId.ToString());
        });
    }

    private void Capture(Guid designId)
    {
        try
        {
            var collId = penumbra.GetPlayerCollectionId();
            if (collId == null)
            {
                log.Debug("[Proteus] Skipping design capture for {0}: no player collection.", designId);
                return;
            }

            var name = glamourer.GetDesigns().TryGetValue(designId, out var n) ? n : null;
            var mods = BuildModBindings(collId.Value);

            var binding = new DesignBinding
            {
                DesignId    = designId,
                DesignName  = name,
                CapturedUtc = DateTime.UtcNow,
                Mods        = mods,
            };

            lock (gate)
            {
                store.Bindings[designId] = binding;
                designCache.Remove(designId); // design content changed → drop cached gear
                Save();
            }

            // The design was just saved from the current live state, so it's already "applied" in
            // spirit — mark it active immediately so the UI shows the binding as bound (blue) without
            // waiting for a separate Glamourer apply. Penumbra settings aren't re-pushed since they're
            // already what we just captured from (hence no echo to suppress either).
            AdoptOverrides(binding, designId, suppressEcho: false);
            compositor.TriggerRecomposite($"design-capture:{designId}");

            log.Information("[Proteus] Captured Proteus state for design {0} ({1} mods).", name ?? designId.ToString(), mods.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to capture design binding for {0}", designId);
        }
    }

    // Capture the *effective* colors (what the compositor is currently using): the live override for
    // this mod if a design is active, else the mod's metadata. Captures all options so the binding is
    // self-contained; the right one is selected at composite time via OverlayColorOverride.Resolve.
    private OverlayColorOverride CaptureColors(OverlayEntry e)
    {
        OverlayColorOverride? active = null;
        lock (gate) activeOverride?.TryGetValue(e.ModDirectory, out active);

        var result = new OverlayColorOverride
        {
            Top  = CloneRows(active?.Top ?? e.Metadata.ColorTableRows),
            Mask = CloneRows(active?.Mask ?? e.Metadata.MaskColorTableRows),
        };

        if (e.Metadata.OptionGroups is { } groups)
        {
            var opts = new Dictionary<string, Dictionary<string, List<ColorTableRowPreset>>>();
            foreach (var g in groups)
            foreach (var o in g.Options)
            {
                List<ColorTableRowPreset>? rows = null;
                if (active?.Options != null
                    && active.Options.TryGetValue(g.PenumbraGroupName, out var d)
                    && d.TryGetValue(o.Name, out var r))
                    rows = r;
                rows ??= o.ColorTableRows;

                var cloned = CloneRows(rows);
                if (cloned == null) continue;
                if (!opts.TryGetValue(g.PenumbraGroupName, out var inner))
                    opts[g.PenumbraGroupName] = inner = new();
                inner[o.Name] = cloned;
            }
            if (opts.Count > 0) result.Options = opts;
        }

        // Imported content packs colour the material they SHIP, and those rows live on a ContentOption
        // rather than an OverlayOption — a different list, keyed the same way. Without this, binding a
        // design captured every colour a mod had except a content pack's, and restoring it painted the
        // pack's authored colours back over the ones the design was saved with.
        if (e.Metadata.ContentGroups is { } contentGroups)
        {
            var opts = result.Options ?? new Dictionary<string, Dictionary<string, List<ColorTableRowPreset>>>();
            foreach (var g in contentGroups)
            foreach (var o in g.Options)
            {
                List<ColorTableRowPreset>? rows = null;
                if (active?.Options != null
                    && active.Options.TryGetValue(g.PenumbraGroupName, out var d)
                    && d.TryGetValue(o.Name, out var r))
                    rows = r;
                rows ??= o.ColorTableRows;

                var cloned = CloneRows(rows);
                if (cloned == null) continue;
                if (!opts.TryGetValue(g.PenumbraGroupName, out var inner))
                    opts[g.PenumbraGroupName] = inner = new();
                inner[o.Name] = cloned;
            }
            if (opts.Count > 0) result.Options = opts;
        }

        return result;
    }

    // Snapshot every discovered Proteus mod's current live state (Penumbra enable/priority/options +
    // effective colors) into fresh binding entries. Shared by design-save capture and the manual
    // "Update binding" action.
    private List<ProteusModBinding> BuildModBindings(Guid collId)
    {
        var mods = new List<ProteusModBinding>();
        foreach (var e in discovery.DiscoverAll())
        {
            var settings = penumbra.GetModSettings(collId, e.ModDirectory);
            var options  = settings?.Options is { } o
                ? o.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value))
                : new Dictionary<string, List<string>>();

            mods.Add(new ProteusModBinding
            {
                ModDirectory = e.ModDirectory,
                Enabled      = e.Enabled,
                Priority     = e.Priority,
                Options      = options,
                Colors       = CaptureColors(e),
                Gear         = CaptureGear(e),
                StackOrder   = CaptureStackOrder(e.ModDirectory),
            });
        }
        return mods;
    }

    // The mod-wide tab/stack order to record: the live override for this mod if a design is active (so an
    // in-progress restack is captured), else the global config order. Empty when neither has one.
    private List<string> CaptureStackOrder(string modDir)
    {
        lock (gate)
            if (activeStackOverride != null && activeStackOverride.TryGetValue(modDir, out var live))
                return new List<string>(live);
        return config.OverlayModStackOrder.TryGetValue(modDir, out var cfg)
            ? new List<string>(cfg)
            : new List<string>();
    }

    // Build a live color override keyed by mod dir, cloned so editing it (live preview) never mutates
    // the persisted binding those colors came from.
    private static Dictionary<string, OverlayColorOverride> CloneOverrides(IEnumerable<ProteusModBinding> mods)
        => mods.ToDictionary(m => m.ModDirectory, m => CloneOverride(m.Colors), StringComparer.OrdinalIgnoreCase);

    private static OverlayColorOverride CloneOverride(OverlayColorOverride o)
        => JsonSerializer.Deserialize<OverlayColorOverride>(JsonSerializer.Serialize(o)) ?? new();

    // Live stack override keyed by mod dir, cloned so an in-progress restack (live preview) never mutates
    // the persisted binding. Only mods that actually captured an order contribute an entry.
    private static Dictionary<string, List<string>> CloneStack(IEnumerable<ProteusModBinding> mods)
        => mods.Where(m => m.StackOrder.Count > 0)
               .ToDictionary(m => m.ModDirectory, m => new List<string>(m.StackOrder), StringComparer.OrdinalIgnoreCase);

    // ── Restore / clear (framework thread) ──────────────────────────────────────

    /// <summary>
    /// Publish a binding's colour / gear / stack overrides as the ACTIVE ones and mark its design
    /// active. Deliberately does NOT write Penumbra (enable / priority / options), does NOT disable
    /// unbound mods and does NOT recomposite — <see cref="Restore"/> does all three around it, and the
    /// boot restore does none of them. Framework thread.
    /// </summary>
    /// <param name="suppressEcho">
    /// Arm the <see cref="RestoreSuppressMs"/> window that makes <see cref="OnGlamourerStateFinalized"/>
    /// ignore the finalization our own Penumbra writes provoke. True for a real restore, which writes
    /// Penumbra; false for paths that write none — a blackout there would swallow a genuine apply
    /// signal instead.
    /// </param>
    private void AdoptOverrides(DesignBinding b, Guid designId, bool suppressEcho)
    {
        Dictionary<string, OverlayColorOverride> colours;
        Dictionary<string, OverlayGearOverride>  gear;
        Dictionary<string, List<string>>         stack;

        lock (gate)
        {
            if (suppressEcho) suppressUntilTick = Environment.TickCount64 + RestoreSuppressMs;
            activeDesignId      = designId;
            // Clone so live color edits preview without mutating the stored binding (they only fold
            // in via UpdateActiveBindingFromCurrentState).
            activeOverride      = colours = CloneOverrides(b.Mods);
            activeGearOverride  = gear    = CloneGear(b.Mods);
            activeStackOverride = stack   = CloneStack(b.Mods);
        }

        PersistActiveDesignId(designId);
        // The locals, not a re-read of the fields: SetEditableStackOrder publishes a fresh dictionary
        // by design (copy-on-write), so re-reading outside the lock can publish someone else's swap.
        compositor.SetActiveColorOverride(colours);
        compositor.SetActiveGearOverride(gear);
        compositor.SetActiveStackOverride(stack);
    }

    /// <summary>
    /// The discovered sidecar mods this binding never captured that ALSO ship Penumbra content of their
    /// own — the gear they overlay, a body, textures. <see cref="Restore"/> leaves these ENABLED where it
    /// switches every other unbound mod off.
    /// <para/>
    /// A restore switches unbound mods off in Penumbra so a previous look's overlays can't bleed into this
    /// design. For a pure overlay pack that is exactly right and costs nothing else. For one of these it is
    /// not: the author's dress, model and metadata edits go off with the overlays, the binding captured
    /// none of it to put back, and the mod simply stops working — including after a reboot, because the
    /// disable is written into Penumbra's collection.
    /// <para/>
    /// Being left enabled is now the WHOLE of it. These used to be silenced in the composite as well, on
    /// the reasoning that a design should show only what it captured. That was the wrong trade: a mod the
    /// user has switched on is a mod they expect to see, and one that had been imported since the design
    /// was saved went quietly missing with only a tooltip to explain it. Enabled means composited.
    /// </summary>
    private HashSet<string> UnboundContentMods(DesignBinding b, IReadOnlyList<OverlayEntry> discovered)
    {
        var bound = b.Mods.Select(m => m.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var held  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in discovered)
        {
            if (bound.Contains(e.ModDirectory)) continue;
            // SidecarRoot is the Proteus/ subfolder; the manifest lives one level up. Trimmed first,
            // exactly as SidecarDiscoveryService does at its three sibling conversions: a trailing
            // separator would make GetDirectoryName return the sidecar folder itself, and
            // PublishesGameContent answers "has content" for a folder it cannot read — so EVERY unbound
            // mod would be held out and none would ever be disabled again.
            var modRoot = Path.GetDirectoryName(
                e.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (modRoot != null && PenumbraModMeta.PublishesGameContent(modRoot))
                held.Add(e.ModDirectory);
        }

        return held;
    }

    /// <summary>
    /// Drop the active overrides and the active design, and un-publish all three. Does NOT recomposite —
    /// each caller wants its own reason and force flag. Returns whether anything was actually active.
    /// Framework thread.
    /// </summary>
    private bool ClearOverrides()
    {
        bool changed;
        lock (gate)
        {
            changed = activeDesignId != null || activeOverride != null
                   || activeGearOverride != null || activeStackOverride != null;
            activeDesignId      = null;
            activeOverride      = null;
            activeGearOverride  = null;
            activeStackOverride = null;
        }

        // Both of these run even when nothing was active, because both are about the state OUTSIDE this
        // object: the persisted pointer can be non-null while nothing is active (a boot restore that
        // abstained), and a revert arriving mid-boot must supersede the reconstruction rather than let
        // it re-adopt what the player just took off. PersistActiveDesignId no-ops when already null, and
        // FinishBootRestore is a one-shot, so neither costs anything on the ambient zone-in path.
        PersistActiveDesignId(null);
        if (BootRestoreArmed) FinishBootRestore("superseded by an explicit clear");

        if (!changed) return false;

        compositor.SetActiveColorOverride(null);
        compositor.SetActiveGearOverride(null);
        compositor.SetActiveStackOverride(null);
        return true;
    }

    /// <summary>
    /// Remember which design is active so a plugin reload can pick it back up — Glamourer raises no
    /// apply signal for a design that is already applied, so this pointer is the only way back to it.
    /// No-ops when unchanged: <see cref="HandleUnboundDesign"/> runs on every zone-in and must not
    /// rewrite the config each time.
    /// </summary>
    private void PersistActiveDesignId(Guid? id)
    {
        if (config.LastActiveDesignId == id) return;
        config.LastActiveDesignId = id;
        compositor.SaveConfig();
        log.Debug("[Proteus] design-binding: persisted active design = {0}", id?.ToString() ?? "(none)");
    }

    public void Restore(Guid designId)
    {
        DesignBinding? b;
        lock (gate) store.Bindings.TryGetValue(designId, out b);
        if (b == null) return;

        var allMods = discovery.DiscoverAll();
        var present = allMods
            .Select(e => e.ModDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boundDirs = b.Mods
            .Select(m => m.ModDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collId = penumbra.GetPlayerCollectionId();

        AdoptOverrides(b, designId, suppressEcho: true);

        // Exempt from the disable sweep below, and nothing more — they stay enabled AND they still
        // composite. See UnboundContentMods.
        var held = UnboundContentMods(b, allMods);
        if (held.Count > 0)
            log.Debug("[Proteus] design-binding: leaving {0} unbound mod(s) enabled — they ship their own "
                    + "Penumbra content, which a disable would take with it: {1}",
                held.Count, string.Join(", ", held));

        if (collId != null)
        {
            foreach (var m in b.Mods)
            {
                if (!present.Contains(m.ModDirectory)) continue; // mod no longer installed — skip
                penumbra.SetModEnabled(collId.Value, m.ModDirectory, m.Enabled);
                penumbra.SetModPriority(collId.Value, m.ModDirectory, m.Priority);
                foreach (var (group, sel) in m.Options)
                    penumbra.SetModOption(collId.Value, m.ModDirectory, group, sel);
            }

            // Any Proteus mod not part of this binding shouldn't carry over from whatever was
            // active before — e.g. enabled after this design was captured, or left on from a
            // previous unrelated look.
            //
            // Only overlay packs are switched off, though. A mod that also ships its own Penumbra
            // content loses the gear, model and metadata edits along with the overlays, and the binding
            // captured none of that to put back — so it just stops working, and stays broken across a
            // reboot because the disable lives in Penumbra's collection. Those are left alone entirely:
            // enabled, and composing, like any other mod the user has switched on.
            foreach (var e in allMods)
            {
                if (boundDirs.Contains(e.ModDirectory) || held.Contains(e.ModDirectory)) continue;
                penumbra.SetModEnabled(collId.Value, e.ModDirectory, false);
            }
        }

        compositor.TriggerRecomposite($"design-restore:{designId}");
        log.Information("[Proteus] Restored Proteus state for design {0}.", b.DesignName ?? designId.ToString());
    }

    /// <summary>Drop the active color override (revert to metadata colors) and recomposite.</summary>
    public void ClearColorOverride()
    {
        ClearOverrides();
        compositor.TriggerRecomposite("design-override-clear");
    }

    /// <summary>
    /// The currently applied design has no captured binding (never bound, or no gear match): drop the
    /// colour/gear overrides so everything falls back to each mod's own metadata.
    ///
    /// It deliberately does NOT disable the Proteus overlay mods (see <see cref="DisableAllProteusMods"/>).
    /// </summary>
    private void HandleUnboundDesign()
    {
        // Ambient: reached from Glamourer's state-finalized signal, which fires on every zone-in. The
        // overrides just cleared are part of the composite fingerprint, so a real unbinding still
        // composites — this only lets a re-assert that changed nothing stop early. ClearOverrides
        // returning false is that re-assert, and it also keeps the config write off the zone-in path.
        if (ClearOverrides())
            compositor.TriggerRecomposite("design-binding-unbound", force: false);
    }

    /// <summary>
    /// Disable every discovered Proteus overlay mod, so an unrecognised look can't keep a previous design's
    /// overlays composited onto it.
    ///
    /// TEMPORARILY UNUSED — deliberately not called from <see cref="HandleUnboundDesign"/>. Its trigger is
    /// the gear-match heuristic, which cannot reliably tell "the player applied something I don't know"
    /// from "I failed to recognise a design I do know". A single false negative turns off the user's entire
    /// Proteus setup, and that fired twice in one session: once because the invisible-glasses host mutated
    /// the bonus slot the matcher compares, and once on a plain revert. Until an applied design reports its
    /// GUID (upstream request pending), the failure is too costly to act on — an unbound design now just
    /// falls back to metadata colours instead.
    ///
    /// Restore the call once binding is GUID-exact: at that point "no binding for this design" is a fact
    /// rather than a guess. Restoring it also needs its idempotency guard back — a bool set here and
    /// cleared in <see cref="Capture"/>/<see cref="Restore"/>, so repeated apply signals while sitting on
    /// the same unbound design don't re-issue a Penumbra call per event.
    /// </summary>
    private void DisableAllProteusMods()
    {
        // Proteus is standing down, so its injected hosts have nothing left to carry. Pull them now: once
        // the mods are off, the redirect that renders them as the shell goes with them, and anything still
        // equipped shows up as a real pair of glasses on the player's face (or a ring they never chose).
        compositor.RemoveInjectedGlasses();
        compositor.RemoveInjectedRing();

        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null) return;
        foreach (var e in discovery.DiscoverAll())
            penumbra.SetModEnabled(collId.Value, e.ModDirectory, false);
    }

    // ── Live override editing (UI, framework thread) ────────────────────────────

    /// <summary>True when a binding is active and supplies colors for this mod.</summary>
    public bool IsOverrideActiveFor(string modDir)
    {
        lock (gate) return activeOverride != null && activeOverride.ContainsKey(modDir);
    }

    /// <summary>
    /// The mod's stored mask override — the single shared Masks tab's colorset — or null when the binding
    /// has none. NEVER creates one.
    /// <para/>
    /// Creating on read is what made an edit invisible: merely drawing the Masks tab snapshotted the
    /// metadata into the live override, and from then on that snapshot shadowed the metadata for as long as
    /// the design stayed applied — so the editor showed the value you had just typed while the composite
    /// kept using the snapshot. A binding must not change because you looked at it.
    /// </summary>
    public List<ColorTableRowPreset>? PeekMaskRows(string modDir)
    {
        lock (gate)
            return activeOverride != null && activeOverride.TryGetValue(modDir, out var ovr) ? ovr.Mask : null;
    }

    /// <summary>
    /// Install the mask rows as this binding's LIVE override, on an actual edit. Live preview only — the
    /// stored binding on disk is untouched until "Update binding". Returns false when no binding is active,
    /// so the caller persists to the metadata instead.
    /// </summary>
    public bool SetMaskRows(string modDir, List<ColorTableRowPreset> rows)
    {
        lock (gate)
        {
            if (activeOverride == null || !activeOverride.TryGetValue(modDir, out var ovr)) return false;
            ovr.Mask = rows;
            return true;
        }
    }

    /// <summary>The stored colour override for a mod's top-level rows, or one option's, or null when the
    /// binding has none. NEVER creates one — same reason as <see cref="PeekMaskRows"/>.</summary>
    public List<ColorTableRowPreset>? PeekOverrideRows(string modDir, string? group, string? option)
    {
        lock (gate)
        {
            if (activeOverride == null || !activeOverride.TryGetValue(modDir, out var ovr)) return null;
            if (group == null || option == null) return ovr.Top;
            return ovr.Options != null && ovr.Options.TryGetValue(group, out var inner)
                && inner.TryGetValue(option, out var rows) ? rows : null;
        }
    }

    /// <summary>Install rows as this binding's LIVE override, on an actual edit. Preview only — the stored
    /// binding is untouched until "Update binding". False when no binding is active.</summary>
    public bool SetOverrideRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows)
    {
        lock (gate)
        {
            if (activeOverride == null || !activeOverride.TryGetValue(modDir, out var ovr)) return false;
            if (group == null || option == null) { ovr.Top = rows; return true; }
            ovr.Options ??= new();
            if (!ovr.Options.TryGetValue(group, out var inner)) ovr.Options[group] = inner = new();
            inner[option] = rows;
            return true;
        }
    }

    /// <summary>
    /// Record a mod-wide tab restack while a design binding is active: into the live stack override (live
    /// preview, folded into the binding on "Update binding"), NOT the global stack config — mirroring how
    /// colour/gear edits stay on the binding. Returns false when no binding is active, so the caller
    /// persists to the global config instead. Republishes to the compositor on success.
    /// </summary>
    /// <summary>
    /// The active design binding's mod-wide tab order for this mod (<see cref="Configuration.ModStackEntry"/>
    /// keys, top-first), or null when no binding overrides it — so the tab strip orders its buttons by the
    /// same source the composite does (see CompositorService.ModStackIndexFor). Falls back to the global
    /// stack config when this returns null.
    /// </summary>
    public IReadOnlyList<string>? ActiveStackOrderFor(string modDir)
    {
        lock (gate)
            return activeStackOverride != null && activeStackOverride.TryGetValue(modDir, out var o)
                ? new List<string>(o)
                : null;
    }

    public bool SetEditableStackOrder(string modDir, IEnumerable<(string Group, string Option)> topFirst)
    {
        IReadOnlyDictionary<string, List<string>>? published;
        lock (gate)
        {
            if (activeDesignId == null || activeStackOverride == null) return false;
            // Copy-on-write: the compositor reads the published dictionary on its background thread, so
            // adding a key in place would be a structural mutation racing that read. Publish a fresh dict
            // instead (the colour/gear overrides only ever mutate nested lists, never the dict shape).
            var next = new Dictionary<string, List<string>>(activeStackOverride, StringComparer.OrdinalIgnoreCase)
            {
                [modDir] = topFirst.Select(x => Configuration.ModStackEntry(x.Group, x.Option)).ToList(),
            };
            activeStackOverride = next;
            published = next;
        }
        compositor.SetActiveStackOverride(published);
        return true;
    }

    /// <summary>
    /// The mutable gear-settings preset the layer/shader editor should bind to when an override is active
    /// for this mod, or null if none. Mirrors <see cref="PeekOverrideRows"/>: group/option=null
    /// targets the top-level overlay; otherwise the option's. Seeds from the metadata descriptor's own
    /// gear settings when the override has nothing stored yet, so editing starts from what's on screen.
    /// </summary>
    public GearSettingsPreset? GetEditableGearOverride(
        string modDir, string? group, string? option, OverlayDescriptor seed)
    {
        lock (gate)
        {
            if (activeGearOverride == null || !activeGearOverride.TryGetValue(modDir, out var ovr))
                return null;
            if (group != null && option != null)
            {
                ovr.Options ??= new();
                if (!ovr.Options.TryGetValue(group, out var inner))
                    ovr.Options[group] = inner = new();
                if (!inner.TryGetValue(option, out var g))
                    inner[option] = g = GearSettingsPreset.From(seed);
                return g;
            }
            return ovr.Top ??= GearSettingsPreset.From(seed);
        }
    }

    /// <summary>
    /// The same, seeded from a preset rather than a descriptor — for a content pack's glow, which has no
    /// overlay descriptor to snapshot. The seed is CLONED before it is stored, so a binding that starts
    /// from the sidecar's own settings can never write back into them.
    /// <para/>
    /// An unconditional piece lands in <see cref="OverlayGearOverride.Content"/>, not
    /// <see cref="OverlayGearOverride.Top"/>: Top is captured from the mod's first overlay descriptor, so
    /// sharing it would let an overlay's scroll effect reach the pack's meshes and the reverse.
    /// </summary>
    public GearSettingsPreset? GetEditableContentGearOverride(
        string modDir, string? group, string? option, GearSettingsPreset seed)
    {
        lock (gate)
        {
            if (activeGearOverride == null || !activeGearOverride.TryGetValue(modDir, out var ovr))
                return null;
            if (group != null && option != null)
            {
                ovr.Options ??= new();
                if (!ovr.Options.TryGetValue(group, out var inner))
                    ovr.Options[group] = inner = new();
                if (!inner.TryGetValue(option, out var g))
                    inner[option] = g = seed.Clone();
                return g;
            }
            return ovr.Content ??= seed.Clone();
        }
    }

    /// <summary>
    /// Read-only peek at a content material's glow under the active design. Resolves through
    /// <see cref="OverlayGearOverride.ResolveContent"/> — the SAME call the compositor makes — so the
    /// editor and the composite can never disagree about which slot governs.
    /// </summary>
    /// <summary>
    /// Clear the mod-wide gear scopes: the overlays' <see cref="OverlayGearOverride.Top"/> and an imported
    /// pack's <see cref="OverlayGearOverride.Content"/>.
    /// <para/>
    /// Both, because "reset this option" with no option named means the mod-wide settings, and content lives
    /// in its own slot precisely so it does NOT share Top. Clearing only one would leave a glow the reset
    /// claimed to remove.
    /// </summary>
    private static bool ClearTopGear(OverlayGearOverride gear)
    {
        bool had = gear.Top != null || gear.Content != null;
        gear.Top = null;
        gear.Content = null;
        return had;
    }

    public GearSettingsPreset? PeekContentGearOverride(string modDir, string? group, string? option)
    {
        lock (gate)
            return activeGearOverride != null && activeGearOverride.TryGetValue(modDir, out var ovr)
                ? ovr.ResolveContent(group, option)
                : null;
    }

    /// <summary>
    /// Read-only peek at the active design's captured gear settings for one option. Unlike
    /// <see cref="GetEditableGearOverride"/> this creates nothing, so callers can ask about options the user
    /// hasn't opened — needed when surveying every active option's effective layer. Null when no design is
    /// active or that option has nothing captured (i.e. the mod's own descriptor still rules).
    ///
    /// Resolution goes through <see cref="OverlayGearOverride.Resolve"/> — the SAME call the compositor
    /// makes — so the per-option entry and its top-level fallback are honoured identically. Looking only in
    /// <c>Options</c> here would diverge for any active option the binding never captured (one added to the
    /// mod after the design was saved, or one whose descriptors were empty at capture time): the composite
    /// would apply <c>Top</c> while the editor read the raw descriptor.
    /// </summary>
    /// <remarks>Overlays only. A content pack's glow reads <see cref="PeekContentGearOverride"/>, which
    /// resolves against its own slot rather than <c>Top</c> — see <see cref="OverlayGearOverride.Content"/>
    /// for why the two must not share.</remarks>
    public GearSettingsPreset? PeekGearOverride(string modDir, string group, string option)
    {
        lock (gate)
            return activeGearOverride != null && activeGearOverride.TryGetValue(modDir, out var ovr)
                ? ovr.Resolve(group, option)
                : null;
    }

    /// <summary>
    /// The mutable gear-settings preset the Masks tab should bind to when a design is active for this mod, or
    /// null if none. Mirrors <see cref="GetEditableMaskRows"/> for the mod's single shared Masks tab — seeds
    /// from the descriptor's own gear settings when the override has nothing stored yet.
    /// </summary>
    public GearSettingsPreset? GetEditableMaskGearOverride(string modDir, OverlayDescriptor seed)
    {
        lock (gate)
        {
            if (activeGearOverride == null || !activeGearOverride.TryGetValue(modDir, out var ovr))
                return null;
            return ovr.Mask ??= GearSettingsPreset.From(seed);
        }
    }

    /// <summary>
    /// Drop ONE option's colour + gear override from the ACTIVE design's binding: from the live in-memory
    /// copy (so the preview falls back to the mod's own metadata straight away) and from the persisted
    /// binding (so re-applying that design doesn't bring it back). Other designs keep theirs.
    ///
    /// This is what makes the editor's "Reset to defaults" stick on a bound mod — restoring metadata.json
    /// alone is invisible while a binding holds its own captured Layer/Shader/colours for that option and
    /// re-imposes them on every apply. Returns false when no design is active or nothing was stored.
    /// </summary>
    public bool ClearOptionOverride(string modDir, string? group, string? option)
    {
        bool touched = false;
        IReadOnlyDictionary<string, OverlayColorOverride>? colours;
        IReadOnlyDictionary<string, OverlayGearOverride>? gears;

        lock (gate)
        {
            if (activeDesignId is not { } id) return false;

            // Live preview copies.
            if (activeOverride != null && activeOverride.TryGetValue(modDir, out var col))
                touched |= ClearScope(col.Options, group, option, () => { bool had = col.Top != null; col.Top = null; return had; });
            if (activeGearOverride != null && activeGearOverride.TryGetValue(modDir, out var gear))
                touched |= ClearScope(gear.Options, group, option, () => ClearTopGear(gear));

            // Persisted binding, so the design stops re-applying it.
            if (store.Bindings.TryGetValue(id, out var b))
            {
                var mod = b.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
                if (mod != null)
                {
                    touched |= ClearScope(mod.Colors.Options, group, option,
                        () => { bool had = mod.Colors.Top != null; mod.Colors.Top = null; return had; });
                    touched |= ClearScope(mod.Gear.Options, group, option, () => ClearTopGear(mod.Gear));
                }
            }

            // Serialising must happen under the lock (a consistent `store`), but the write must not:
            // this store reaches tens of MB and the caller is an ImGui button on the framework thread.
            if (touched) SaveDeferred();
            colours = activeOverride;
            gears   = activeGearOverride;
        }

        if (!touched) return false;

        // Re-publish the trimmed overrides so the next composite drops this option's override.
        compositor.SetActiveColorOverride(colours);
        compositor.SetActiveGearOverride(gears);
        log.Information("[Proteus] cleared binding override for {0} [{1}/{2}] from the active design",
            modDir, group ?? "(top)", option ?? "(top)");
        return true;
    }

    /// <summary>Remove one group/option entry from an override map (pruning the group when it empties),
    /// or clear the top-level entry when BOTH group and option are null. Returns whether anything was
    /// there. A half-specified scope (one null, one not) is a caller bug: refuse it rather than fall
    /// through to clearing Top, which would wipe the settings every option inherits.</summary>
    private static bool ClearScope<T>(Dictionary<string, Dictionary<string, T>>? options,
        string? group, string? option, Func<bool> clearTop)
    {
        if (group == null && option == null) return clearTop();
        if (group == null || option == null) return false;
        if (options == null || !options.TryGetValue(group, out var inner)) return false;
        if (!inner.Remove(option)) return false;
        if (inner.Count == 0) options.Remove(group);
        return true;
    }

    /// <summary>
    /// Re-snapshot the current live Proteus state (Penumbra enable/priority/options + the live color
    /// override, including unsaved editor tweaks) into the active binding and persist it. This is the
    /// only path that folds manual UI edits into a binding — edits are otherwise live-preview only.
    /// No-op (returns false) if no binding is active. Framework thread.
    /// </summary>
    public bool UpdateActiveBindingFromCurrentState()
    {
        Guid id;
        string? name;
        lock (gate)
        {
            if (activeDesignId == null) return false;
            id   = activeDesignId.Value;
            name = store.Bindings.TryGetValue(id, out var existing) ? existing.DesignName : null;
        }

        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null) return false;

        var mods = BuildModBindings(collId.Value);
        name ??= glamourer.GetDesigns().TryGetValue(id, out var n) ? n : null;

        Dictionary<string, OverlayColorOverride> newOverride;
        lock (gate)
        {
            if (activeDesignId != id) return false; // active binding changed underfoot
            store.Bindings[id] = new DesignBinding
            {
                DesignId    = id,
                DesignName  = name,
                CapturedUtc = DateTime.UtcNow,
                Mods        = mods,
            };
            newOverride         = CloneOverrides(mods);
            activeOverride      = newOverride;
            activeStackOverride = CloneStack(mods);
            Save();
        }
        compositor.SetActiveColorOverride(newOverride);
        compositor.SetActiveStackOverride(activeStackOverride);
        compositor.TriggerRecomposite($"design-binding-update:{id}");
        log.Information("[Proteus] Updated binding for design {0} from current state.", name ?? id.ToString());
        return true;
    }

    // ── Heuristic apply detection (framework thread) ────────────────────────────

    /// <summary>
    /// Whether a finished Glamourer operation should re-evaluate which design is applied.
    ///
    /// Driven by <see cref="StateFinalizationType"/> rather than <see cref="StateChangeType"/> because the
    /// latter can't tell these cases apart — measured in-game, a gearset change and a revert BOTH arrive as
    /// <c>Reapply</c>/<c>Reset</c>, so the heuristic ran on both: a gearset swap restored an unrelated
    /// design, and a revert reached <see cref="HandleUnboundDesign"/> and disabled every Proteus mod a
    /// moment before the follow-up reapply restored the right one. The finalization type separates them
    /// (<c>Gearset</c>, <c>RevertAutomation</c>), and fires ONCE per operation where the change type fires
    /// per field — so this also collapses several redundant passes into one.
    ///
    /// Included, per Glamourer's own emission sites:
    ///  • <c>DesignApplied</c> — a design was applied (StateEditor, gated on <c>settings.IsFinal</c>).
    ///  • <c>ReapplyAutomation</c> — automation applied state, which is how automation-applied DESIGNS
    ///    surface (<c>AutoDesignApplier</c> → <c>ReapplyAutomationState(…, wasReset: false, …)</c>).
    ///
    /// Excluded, and note plain <c>Reapply</c> among them: Glamourer raises that from <c>ReapplyState</c>
    /// only — the IPC call (i.e. OUR own post-composite reapply), <c>/glamour reapply</c>, a UI button and
    /// Penumbra's auto-redraw. No design-application path ends there, so treating it as an apply signal
    /// just fed the heuristic our own echo. Likewise <c>Gearset</c> (gear moved, the design did not) and
    /// every <c>Revert*</c> (handled by <see cref="IsRevertSignal"/> instead).
    ///
    /// <c>Gearset</c> is not quite the dead end that reads as, though — it is the ONLY thing an
    /// automation-applied design produces, so it is admitted separately, in company and restore-only, by
    /// <see cref="IsInferredAutomationApply"/>.
    /// </summary>
    internal static bool IsApplySignal(StateFinalizationType type)
        => type is StateFinalizationType.DesignApplied
                or StateFinalizationType.ReapplyAutomation;

    /// <summary>
    /// The player reverted to their game state, so no design is applied any more and the active override
    /// must be dropped — otherwise the previous design's colours and gear settings stay composited onto a
    /// character that was just reverted to vanilla. (The old StateChangeType.Reset signal did this; the
    /// switch to finalization types lost it until this was added back.)
    ///
    /// <c>RevertAutomation</c> is deliberately NOT here: it is immediately followed by a Reapply that
    /// restores the correct design (observed in-game), so clearing first would only add a wasted
    /// clear-then-restore pair of recomposites.
    /// </summary>
    internal static bool IsRevertSignal(StateFinalizationType type)
        => type is StateFinalizationType.Revert
                or StateFinalizationType.RevertCustomize
                or StateFinalizationType.RevertEquipment
                or StateFinalizationType.RevertAdvanced;

    /// <summary>
    /// Whether a <c>Gearset</c> finalization is really automation having just applied a design.
    ///
    /// Automation applying a design on a gearset or job change raises NO apply signal: Glamourer merges
    /// and applies it with <c>StateSource.Fixed</c>, whose <c>RequiresChange()</c> is false, so the actor
    /// set is empty and the IPC layer — which iterates actors — emits neither <c>Design</c> nor
    /// <c>DesignApplied</c>; and the follow-up call is <c>ReapplyState(isFinal: false)</c> rather than the
    /// <c>ReapplyAutomationState</c> that would have reported <c>ReapplyAutomation</c>. All that reaches us
    /// is the game's own <c>Gearset</c>, from the equipment load finishing.
    ///
    /// So the signature is ordered rather than typed: a Reapply we did not cause, then a Gearset a moment
    /// later (30-450 ms in practice). Bare <c>Gearset</c> is NOT usable on its own — it fires on every
    /// zone-in, redraw and gear swap, design or no design.
    ///
    /// Both conditions earn their place:
    ///  • <paramref name="msSinceForeignReapply"/> is what separates "automation re-asserted state" from
    ///    "the game loaded gear" (<see cref="long.MaxValue"/> when there has never been one). Without it
    ///    this is just bare Gearset.
    ///  • <paramref name="isOwnRedrawEcho"/> excludes OUR OWN tail, and is the one doing the real work: a
    ///    Proteus redraw rebuilds the draw object (→ Gearset) and makes Glamourer reapply state right after
    ///    (→ foreign Reapply, via PenumbraAutoRedraw), which is indistinguishable from the real thing. It
    ///    is a consumed one-shot expectation rather than a time window, so discounting our own redraw
    ///    cannot also discount the player's next gearset change — see
    ///    <see cref="CompositorService.ConsumeOwnRedrawEcho"/>.
    ///
    /// A caller acting on this must treat it as restore-only — see <see cref="EvaluateAppliedDesign"/>.
    /// </summary>
    internal static bool IsInferredAutomationApply(StateFinalizationType type,
                                                   long msSinceForeignReapply, bool isOwnRedrawEcho)
        => type is StateFinalizationType.Gearset
        && !isOwnRedrawEcho
        && msSinceForeignReapply < AutomationPairWindowMs;

    private void OnGlamourerStateFinalized(StateFinalizationType type)
    {
        // A revert leaves no design applied: drop the override so colours fall back to metadata. This is
        // NOT the unbound-design case (nothing failed to match), it just has the same effect.
        if (IsRevertSignal(type))
        {
            if (Environment.TickCount64 >= suppressUntilTick) HandleUnboundDesign();
            return;
        }

        // The one-shot expectation belongs to the redraw, not to any decision made below it, so consume it
        // for every Gearset that arrives. Deferring it past the guards would let a Gearset dropped for an
        // unrelated reason — our own restore echo, the feature being off — leave the expectation armed to
        // swallow a genuine gearset change later instead.
        var ownRedrawEcho = type is StateFinalizationType.Gearset
                         && compositor.ConsumeOwnRedrawEcho(OwnRedrawEchoMs);

        if (Environment.TickCount64 < suppressUntilTick) return; // our own restore echo

        // Feature disabled → never restore. Also drop any override left active from before the toggle was
        // turned off, so "off" means colors fall back to metadata (off == fully off). Checked before the
        // inference below rather than after: an inferred signal has nothing to clear (it is restore-only),
        // so with the feature off it would otherwise announce an apply in the log and then do nothing.
        if (!config.DesignBindingEnabled)
        {
            if (IsApplySignal(type) && activeDesignId != null) ClearColorOverride();
            return;
        }

        // Automation-applied designs (gearset / job change) arrive with no apply signal of their own and
        // have to be inferred. That inference is a guess, so it is allowed to restore a binding and never
        // to clear one: a wrong restore is visible and reversible, a wrong clear silently drops the
        // player's colours back to metadata white.
        var inferred = false;
        if (!IsApplySignal(type))
        {
            if (type is not StateFinalizationType.Gearset) return;
            if (!config.DesignBindingFollowsAutomation) return;

            var sinceReapply = glamourer.MsSinceForeignReapply;
            if (!IsInferredAutomationApply(type, sinceReapply, ownRedrawEcho))
            {
                log.Debug("[Proteus] design-binding: Gearset ignored (foreign reapply {0}, own redraw echo={1}).",
                    Elapsed(sinceReapply), ownRedrawEcho);
                return;
            }

            inferred = true;
            log.Information("[Proteus] glamourer signal: finalized=Gearset + foreign reapply {0} -> inferred automation apply.",
                Elapsed(sinceReapply));
        }

        EvaluateAppliedDesign(allowUnbind: !inferred);
    }

    /// <summary>An elapsed-ms reading for the log, where "never happened" is a sentinel that would
    /// otherwise print as 9223372036854775807 and read like a glitch.</summary>
    private static string Elapsed(long ms)
        => ms == long.MaxValue ? "never" : $"{ms}ms ago";

    /// <summary>
    /// Match the player's current state against every binding and restore the best one.
    ///
    /// <paramref name="allowUnbind"/> is false when the signal that got us here was INFERRED
    /// (<see cref="IsInferredAutomationApply"/>) rather than reported by Glamourer. A failed match then
    /// means "the guess did not pan out", not "the player is wearing something unbound" — and the two are
    /// indistinguishable from here, so nothing is cleared. It also sidesteps the mid-transition read: the
    /// Gearset finalization is the GAME finishing its equipment load, which is not ordered against
    /// Glamourer finishing its design apply, so the state read here can legitimately be half-applied.
    /// </summary>
    private void EvaluateAppliedDesign(bool allowUnbind)
    {
        bool anyBindings;
        lock (gate) anyBindings = store.Bindings.Count > 0;
        if (!anyBindings)                                                // nothing ever bound
        {
            if (allowUnbind) HandleUnboundDesign();
            return;
        }

        var state = glamourer.GetObjectState(0);
        if (state == null) return; // can't read state → abstain

        // Match against the player's OWN choices: strip anything Proteus equipped for them (the invisible
        // glasses and the Emperor's ring hosts) or nothing would ever match and every Proteus mod would be
        // disabled.
        //
        // Asked of the compositor, which knows what it actually injected, rather than derived from the
        // feature toggles. Either way round is a trap: gating on the toggle misses our item during the
        // window between switching it off and the recomposite pulling it, while blanking the id whenever
        // the feature is off would erase the player's OWN Emperor's New Ring — a common invisible-ring
        // glamour — from every comparison. Both mistakes end in a design that matches nothing.
        state = NeutralizeProteusOwnedState(state,
            compositor.InjectedGlassesItemId, compositor.InjectedCarrierItemIds);

        var pick = MatchBinding(state);

        if (pick == null)
        {
            if (!allowUnbind)
            {
                // Inferred signal: no match is an ordinary outcome (the pairing can also come from a gear
                // change nobody bound, or a state read taken before Glamourer finished applying), so it is
                // neither a warning nor a reason to touch anything.
                log.Information("[Proteus] design-binding: no binding matched the inferred automation apply — leaving overrides as they are.");
                return;
            }

            // No binding matched, so overrides get dropped and gear dyes revert to metadata. A per-candidate
            // "REJECTED: <first field that differed>" line used to be emitted here for every binding the
            // player owns; it fired on the ordinary path (most designs are SUPPOSED not to match the state
            // being applied), so it was many warnings per apply describing correct behaviour. The summary
            // below is the part that reports something actually went wrong.
            log.Warning("[Proteus] design-binding: NO binding matched the applied state — dropping colour/gear overrides (dyes revert to metadata white).");
            HandleUnboundDesign();
            return;
        }

        if (activeDesignId == pick) return; // already applied
        Restore(pick.Value);
    }

    /// <summary>
    /// The binding that best matches an already-neutralized player state, or null when none does.
    /// <para/>
    /// For ambiguous matches (variations of the same outfit share a gear set), prefer the most
    /// *specific* match — the design that constrains the most applied fields (e.g. one that also
    /// matches the applied dye beats a gear-only design). Break remaining ties by the most recently
    /// captured binding, which avoids stale older overrides sticking around.
    /// <para/>
    /// Shared by the live apply path and the boot restore so both resolve a look identically.
    /// </summary>
    /// <param name="stripCarriers">
    /// When set, each candidate design has the shell's carrier slots removed before it is compared — see
    /// <see cref="StripCarriers"/>. The caller must have stripped the state with the same ids. Only the
    /// boot restore passes this; the live path compares designs as saved.
    /// </param>
    private Guid? MatchBinding(JObject neutralizedState, (ulong? Glasses, IReadOnlyList<ulong> Accessories)? stripCarriers = null)
    {
        Guid[] candidateIds;
        lock (gate) candidateIds = store.Bindings.Keys.ToArray();

        var matches = new List<(Guid id, int specificity)>();
        foreach (var id in candidateIds)
        {
            var design = GetDesignCached(id);
            if (design == null) continue;
            if (stripCarriers is { } c) design = StripCarriers(design, c.Glasses, c.Accessories);
            if (StateMatches(design, neutralizedState, out var spec))
                matches.Add((id, spec));
        }

        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0].id;

        var best = matches.Max(m => m.specificity);
        var top  = matches.Where(m => m.specificity == best).Select(m => m.id).ToList();
        if (top.Count == 1) return top[0];
        lock (gate) return PickMostRecent(top, store.Bindings);
    }

    // ── Boot restore (framework thread) ─────────────────────────────────────────

    /// <summary>
    /// Waits for the local player and a readable Glamourer state, then resolves the boot restore once.
    /// A poll rather than a login event because the case being fixed is a plugin reload while ALREADY
    /// logged in, where no login event ever fires. Same shape as CompositorService.OnBootPoll, but the
    /// preconditions are cheaper: no Penumbra collection and no discovery, since this writes nothing to
    /// Penumbra.
    /// </summary>
    private void OnBootRestoreTick(IFramework fw)
    {
        if (Volatile.Read(ref bootRestoreDone) == 1) return;

        var now = Environment.TickCount64;
        if (unchecked(now - lastBootRestorePollTick) < BootRestorePollMs) return;
        lastBootRestorePollTick = now;

        // No player, nothing to verify against. The deadline is deliberately NOT started here: the
        // plugin can load at the title screen and sit there, and a clock running there would time out
        // long before there was anything to read.
        if ((Plugin.ObjectTable.LocalPlayer?.Address ?? 0) == 0) return;
        if (bootDeadlineTick == 0)
        {
            bootDeadlineTick    = now + BootRestoreTimeoutMs;
            bootSettleUntilTick = now + BootSettleMs;
        }

        // A real Glamourer apply beat us to it (login automation, or the player applied something while
        // we were waiting). That came from an actual apply signal where ours is a reconstruction, so it
        // wins outright.
        if (ActiveDesignId != null) { FinishBootRestore("a live Glamourer apply got there first"); return; }

        // Toggled off between arming and now: off means off.
        if (!config.DesignBindingEnabled) { FinishBootRestore("design binding disabled"); return; }

        var state = glamourer.GetObjectState(0);
        if (state == null)
        {
            if (now >= bootDeadlineTick)
                FinishBootRestore($"Glamourer state unreadable after {BootRestoreTimeoutMs / 1000}s — abstaining");
            return;                     // not ready yet; try again next interval
        }

        // The state is READABLE well before it is SETTLED. Our own Dispose pulls the injected glasses
        // and ring on the way out, and the game applies those removals asynchronously — so the first
        // read after a reload can still show items that are on their way off (or already off while the
        // rest of the outfit lags). A mismatch that early means "not yet", not "not this design", so
        // keep retrying until the settle window lapses and only then take the answer as final.
        var final = now >= bootSettleUntilTick;
        TryBootRestore(state, final);
    }

    /// <summary>
    /// Adopt the binding the character is ALREADY wearing, with no Penumbra writes of any kind.
    /// <para/>
    /// Restore-only by construction: it can publish overrides and it can leave them unset, but it never
    /// clears one, never disables a mod and never re-asserts enable/priority/options. Those are the
    /// player's live Penumbra settings, which survived the reload perfectly well; re-imposing a
    /// binding's snapshot of them would silently undo anything changed while Proteus was unloaded.
    /// </summary>
    /// <param name="final">
    /// False while the settle window is still open: a non-match is reported and retried rather than
    /// resolved, so a state caught mid-transition doesn't decide the answer. True once the window
    /// lapses, at which point this must resolve one way or the other.
    /// </param>
    private void TryBootRestore(JObject rawState, bool final)
    {
        // The design and the state are treated ASYMMETRICALLY, deliberately. StateMatches only ever
        // reads a state slot the DESIGN carries (both its loops walk the design's properties), so:
        //
        //  • the design gets the carrier REMOVED, below, via StripCarriers — a slot it no longer carries
        //    stops being a criterion at all, which is what rescues a design that captured our facewear;
        //  • the state gets the carrier ZEROED, here, via NeutralizeProteusOwnedState — REMOVING it
        //    there would turn a slot the design does carry from "compares equal" into "state has no
        //    item". That is precisely the mismatch the neutralizer was written to prevent: a design
        //    saved with an empty right ring stores ItemId 0, so our ring has to look like 0, not like
        //    an absent slot. It bites whenever the carrier is still equipped at load — a crash, or a
        //    Dispose removal that has not landed yet.
        //
        // Carrier ids come from the game sheets rather than the compositor's injection flags, which are
        // still false at boot because no composite has run to set them.
        var carriers = BootCarriers();
        var state    = NeutralizeProteusOwnedState(rawState, carriers.Glasses, carriers.Accessories);

        // 1. The design we were on when we unloaded, VERIFIED against the live state.
        if (config.LastActiveDesignId is { } lastId)
        {
            DesignBinding? b;
            lock (gate) store.Bindings.TryGetValue(lastId, out b);

            // The first two outcomes are DETERMINISTIC — a missing binding will not appear and a deleted
            // design will not come back — so they fall straight through to step 2 instead of retrying
            // until the settle window lapses. Only the mismatch below is worth waiting on. Reported
            // once, since the fall-through repeats every poll.
            if (b == null)
            {
                ReportStep1Once($"last active design {lastId} has no binding any more");
            }
            else if (GetDesignCached(lastId) is not { } design)
            {
                ReportStep1Once($"design {b.DesignName ?? lastId.ToString()} is gone from Glamourer");
            }
            else
            {
                design = StripCarriers(design, carriers.Glasses, carriers.Accessories);
                string? why = null;
                if (BootIdStillApplies(design, state, r => why ??= r))
                {
                    AdoptOverrides(b, lastId, suppressEcho: false);
                    FinishBootRestore($"adopted last active design {b.DesignName ?? lastId.ToString()} ({b.Mods.Count} mods)");
                    return;
                }

                // The reason matters here, unlike on the live path: this is the difference between the
                // player's colours coming back and not, and the field named is the whole diagnosis.
                if (!final)
                {
                    log.Debug("[Proteus] boot restore: {0} does not match yet ({1}) — state may still be settling.",
                        b.DesignName ?? lastId.ToString(), why ?? "no reason reported");
                    return;
                }
                log.Information("[Proteus] boot restore: {0} no longer matches the character ({1}) — trying every binding.",
                    b.DesignName ?? lastId.ToString(), why ?? "no reason reported");
            }
        }

        // 2. The same match the live apply path runs. The character may have been changed while we were
        //    unloaded, or this may be a different character entirely (the store is not per-character).
        if (MatchBinding(state, carriers) is not { } pick)
        {
            if (!final) return;
            FinishBootRestore("no binding matched the character — leaving overrides unset");
            return;
        }

        DesignBinding? picked;
        lock (gate) store.Bindings.TryGetValue(pick, out picked);
        if (picked == null) { FinishBootRestore("matched binding vanished underfoot"); return; }

        AdoptOverrides(picked, pick, suppressEcho: false);
        FinishBootRestore($"adopted matched design {picked.DesignName ?? pick.ToString()}");
    }

    /// <summary>Log a step-1 fall-through reason the first time only: the deterministic branches retry
    /// every poll until the window closes, and the reason does not change between them.</summary>
    private void ReportStep1Once(string reason)
    {
        if (bootStep1Reported) return;
        bootStep1Reported = true;
        log.Information("[Proteus] boot restore: {0} — trying every binding.", reason);
    }

    /// <summary>Whether the remembered design can still be adopted: it must still exist in Glamourer
    /// (a deleted one reads as null) and still match the character's neutralized live state.</summary>
    /// <param name="onMismatch">Reports the first field that differed, for the boot log. This is the
    /// one place the reason is worth having: everywhere else a non-match is the ordinary outcome, but
    /// here it is the difference between the player's colours coming back and not.</param>
    internal static bool BootIdStillApplies(JObject? design, JObject neutralizedState,
                                            Action<string>? onMismatch = null)
    {
        if (design == null) return false;
        return StateMatches(design, neutralizedState, out _, onMismatch);
    }

    /// <summary>One-shot: unhook the poll and release the boot composite, whatever the outcome. Every
    /// exit path routes through here — a boot restore that abstains still owes the first composite.</summary>
    private void FinishBootRestore(string outcome)
    {
        if (Interlocked.Exchange(ref bootRestoreDone, 1) == 1) return;
        framework.Update -= OnBootRestoreTick;
        compositor.BootCompositeHold = false;
        log.Information("[Proteus] design-binding boot restore: {0} — boot composite released.", outcome);
    }

    internal static Guid PickMostRecent(IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, DesignBinding> bindings)
        => ids.OrderByDescending(id => bindings.TryGetValue(id, out var b) ? b.CapturedUtc : DateTime.MinValue).First();

    private JObject? GetDesignCached(Guid id)
    {
        lock (gate)
            if (designCache.TryGetValue(id, out var cached))
                return cached;

        var design = glamourer.GetDesign(id);
        lock (gate) designCache[id] = design;
        return design;
    }

    // Weapon slots are excluded from the match: a character's drawn weapon changes with job,
    // gearset, and sheathe state independently of the worn outfit, so requiring it to match would
    // reject a correctly-applied design whenever the equipped weapon differs (the common case).
    private static readonly HashSet<string> NonMatchedSlots =
        new(StringComparer.OrdinalIgnoreCase) { "MainHand", "OffHand" };

    // A design matches the state when every applied field it carries equals the player's current
    // state: equipment items, dyes/stains, bonus items (glasses/facewear), customize values, and
    // advanced parameters. Only fields the design actually applies are compared (Apply / ApplyStain
    // = true); anything else is left to whatever the state already had. Weapons, wetness, and
    // material color tables are excluded (situational, or overlapping Proteus's own color override).
    //
    // `specificity` counts how many applied fields matched: a richer design that also matches the
    // dye/bonus/appearance scores higher than a gear-only design for the same look, so the caller can
    // prefer the most-constrained match. Any mismatch on an applied field rejects the design outright.
    /// <remarks>
    /// Compares the design against the player's OWN choices, so the caller must first strip anything
    /// Proteus wrote on their behalf — see <see cref="NeutralizeProteusOwnedState"/>.
    /// </remarks>
    internal static bool StateMatches(JObject design, JObject state, out int specificity)
        => StateMatches(design, state, out specificity, null);

    // onMismatch: when non-null, called with the reason the design was rejected (the FIRST failing field),
    // for diagnosing why a correctly-applied design fails to match and its overrides get dropped.
    internal static bool StateMatches(JObject design, JObject state, out int specificity, Action<string>? onMismatch)
    {
        specificity = 0;
        // A plain string, deliberately. Its one caller is the boot restore (BootIdStillApplies), which
        // runs once per load against a single design; the live path still passes null, because there
        // the per-candidate "REJECTED" log was noise describing correct behaviour.
        //
        // Taking Func<string> to defer it looks like the obvious saving and is the opposite: the lambdas
        // capture the foreach variable, and a captured per-iteration variable makes Roslyn allocate its
        // display class at the TOP OF EVERY ITERATION, whether or not a Fail is reached. That is one
        // allocation per equipment slot on every call — including the matching design, which reaches no
        // Fail at all and previously allocated nothing — to avoid one interpolated string on the failing
        // branch. Capturing gearSlots also hoists it out of a register and into a heap field for the
        // duration of the loop. Measured in shape rather than in numbers, but the direction is not close.
        bool Fail(string why) { onMismatch?.Invoke(why); return false; }

        if (design["Equipment"] is not JObject dEquip || state["Equipment"] is not JObject sEquip)
            return Fail("no Equipment object on design or state");

        int gearSlots = 0;
        foreach (var prop in dEquip.Properties())
        {
            if (NonMatchedSlots.Contains(prop.Name)) continue;      // weapons vary situationally
            if (prop.Value is not JObject dSlot) continue;
            var sSlot = sEquip[prop.Name] as JObject;

            // Equipment item id. Meta entries (Hat/Visor/Weapon/VieraEars) have no ItemId → skipped.
            if (dSlot["ItemId"] is { } dItem && dSlot["Apply"]?.ToObject<bool>() == true)
            {
                if (sSlot?["ItemId"] is not { } sItem) return Fail($"{prop.Name}: state has no item");
                if (dItem.ToObject<ulong>() != sItem.ToObject<ulong>())
                    return Fail($"{prop.Name}: item {dItem.ToObject<ulong>()} != state {sItem.ToObject<ulong>()}");
                gearSlots++;
                specificity++;
            }

            // Dye/stain, compared independently of the item (a re-dye is a different look).
            if (dSlot["ApplyStain"]?.ToObject<bool>() == true)
            {
                if (sSlot == null) return Fail($"{prop.Name}: state has no slot for stain");
                if (!IdEquals(dSlot["Stain"],  sSlot["Stain"]))  return Fail($"{prop.Name}: Stain {dSlot["Stain"]} != state {sSlot["Stain"]}");
                if (!IdEquals(dSlot["Stain2"], sSlot["Stain2"])) return Fail($"{prop.Name}: Stain2 {dSlot["Stain2"]} != state {sSlot["Stain2"]}");
                specificity++;
            }
        }

        if (gearSlots < MinGearSlots) return Fail($"only {gearSlots} gear slot(s) applied (< {MinGearSlots})");

        // Bonus items (glasses / facewear).
        if (design["Bonus"] is JObject dBonus && state["Bonus"] is JObject sBonus)
        {
            foreach (var prop in dBonus.Properties())
            {
                if (prop.Value is not JObject dItem) continue;
                if (dItem["Apply"]?.ToObject<bool>() != true) continue;
                if (sBonus[prop.Name] is not JObject sItem) return Fail($"Bonus/{prop.Name}: state has no bonus item");
                if (!IdEquals(dItem["BonusId"], sItem["BonusId"])) return Fail($"Bonus/{prop.Name}: BonusId {dItem["BonusId"]} != state {sItem["BonusId"]}");
                specificity++;
            }
        }

        // Customize (skin colour, hair, eyes, face…). Per-index objects only; the base64 "Array"
        // form (non-human models — out of scope for skin overlays) has no per-field objects to
        // compare, so it simply contributes nothing. Wetness is situational and skipped.
        if (design["Customize"] is JObject dCust && state["Customize"] is JObject sCust)
        {
            foreach (var prop in dCust.Properties())
            {
                if (prop.Name == "Wetness") continue;
                if (prop.Value is not JObject dEntry) continue;     // ModelId scalar / Array form → skipped
                if (dEntry["Apply"]?.ToObject<bool>() != true) continue;
                if (dEntry["Value"] is not { } dVal) continue;
                if (sCust[prop.Name] is not JObject sEntry || sEntry["Value"] is not { } sVal) return Fail($"Customize/{prop.Name}: state missing");
                if (dVal.ToObject<long>() != sVal.ToObject<long>()) return Fail($"Customize/{prop.Name}: {dVal} != state {sVal}");
                specificity++;
            }
        }

        // Advanced parameters (RGBA / value / percentage colours).
        if (design["Parameters"] is JObject dParams && state["Parameters"] is JObject sParams)
        {
            foreach (var prop in dParams.Properties())
            {
                if (prop.Value is not JObject dEntry) continue;
                if (dEntry["Apply"]?.ToObject<bool>() != true) continue;
                if (sParams[prop.Name] is not JObject sEntry) return Fail($"Parameters/{prop.Name}: state missing");
                if (!ParameterEquals(dEntry, sEntry)) return Fail($"Parameters/{prop.Name}: value differs");
                specificity++;
            }
        }

        return true;
    }

    private static bool IdEquals(JToken? a, JToken? b)
        => a != null && b != null && a.ToObject<ulong>() == b.ToObject<ulong>();

    /// <summary>
    /// Blank out every part of the player's Glamourer state that PROTEUS wrote, so design matching only
    /// ever sees the player's own choices. Returns a copy; the input is left alone.
    ///
    /// This is the single place that knows what Proteus injects. Without it, anything we equip on the
    /// player's behalf makes their live state differ from every design that saved that field: no design
    /// matches, the apply is treated as unbound, and <see cref="HandleUnboundDesign"/> disables every
    /// Proteus mod. Keep this in step with each new injection rather than teaching the matcher about them
    /// one at a time — the failure mode is losing the user's whole setup.
    /// </summary>
    /// <param name="syntheticGlassesId">
    /// The Glasses-slot item id of the invisible-glasses host, when that feature has one equipped.
    /// </param>
    /// <param name="syntheticRingId">
    /// The item id of the Emperor's-ring host, when that feature has one equipped in the right ring slot.
    /// </param>
    internal static JObject NeutralizeProteusOwnedState(JObject state, ulong? syntheticGlassesId,
        IReadOnlyList<ulong>? syntheticAccessoryIds = null)
    {
        JObject? copy = null;

        if (syntheticGlassesId is { } glasses && state["Bonus"] is JObject bonus)
        {
            foreach (var prop in bonus.Properties())
            {
                if (prop.Value is not JObject slot) continue;
                if (slot["BonusId"] is not { } id || id.ToObject<ulong>() != glasses) continue;

                // Ours — present it as an empty slot so a design that saved "no glasses" still matches.
                copy ??= (JObject)state.DeepClone();
                if (((JObject?)copy["Bonus"])?[prop.Name] is JObject target)
                    target["BonusId"] = 0;
            }
        }

        // Same for every invisible accessory we equip to host a shell — either hand, the bracelet, the
        // necklace, since a carrier goes to whichever slots were free. Nothing zero-ish about the ids: a
        // design that saved an empty ring stores ItemId 0, so that is what "not the player's choice" has to
        // look like here — otherwise wearing our carrier makes every such design mismatch.
        //
        // A LIST of ids, because the pieces share an accessory model set but are separate items; matching on
        // one id would leave the others looking like deliberate jewellery.
        if (syntheticAccessoryIds is { Count: > 0 } carriers)
        {
            foreach (var accessorySlot in new[] { "RFinger", "LFinger", "Wrists", "Neck" })
            {
                if ((copy ?? state)["Equipment"] is not JObject equip
                    || equip[accessorySlot] is not JObject slot
                    || slot["ItemId"] is not { } rid || !carriers.Contains(rid.ToObject<ulong>()))
                    continue;
                copy ??= (JObject)state.DeepClone();
                if (((JObject?)copy["Equipment"])?[accessorySlot] is JObject target)
                    target["ItemId"] = 0;
            }
        }

        return copy ?? state;
    }

    // Glamourer packs a bonus item as (type << 48) | row id, so the sheet row we resolve a carrier by
    // lives in the low 48 bits — e.g. Glasses row 1 reads as 562949953421313.
    private const ulong BonusIdRowMask = 0x0000_FFFF_FFFF_FFFF;

    private static readonly string[] RingSlots = ["RFinger", "LFinger"];

    /// <summary>
    /// Remove the shell's CARRIER slots — the facewear Proteus equips, and the Emperor's ring it may host
    /// on — from a DESIGN, so it is not judged on a slot Proteus owns. Returns the input unchanged when
    /// it carries neither.
    /// <para/>
    /// This exists because <see cref="NeutralizeProteusOwnedState"/> can only fix the state, and the
    /// carrier ends up on the DESIGN too: saving a Glamourer design while the shell is hosted captures
    /// our injected facewear as part of the look. That design then demands a bonus item the player never
    /// chose — and by the time the boot restore verifies it, Dispose has already taken the carrier off,
    /// so the design mismatches on a slot that is entirely our own doing. Observed as
    /// <c>Bonus/Glasses: BonusId 562949953421313 != state 844424946909184</c>.
    /// <para/>
    /// DESIGNS ONLY — never pass a state. StateMatches walks the design's properties and looks the state
    /// up per design-carried slot, so dropping a slot from the design retires it as a criterion (what we
    /// want), while dropping one from the state turns a slot the design DOES carry from "compares equal"
    /// into "state has no item". Use <see cref="NeutralizeProteusOwnedState"/> for the state, which
    /// zeroes rather than removes — a design saved with an empty right ring stores ItemId 0, so that is
    /// what our carrier has to look like there.
    /// <para/>
    /// The caller identifies the carriers from the Glasses/Ring sheets rather than from the compositor's
    /// injection flags, which are still false at boot — no composite has run to set them yet. That is
    /// safe in this direction: the worst case is that a player who genuinely wears the carrier item
    /// stops having it count toward a match, which loosens specificity rather than fabricating one.
    /// </summary>
    internal static JObject StripCarriers(JObject design, ulong? glassesRow, IReadOnlyList<ulong>? accessoryItems)
    {
        List<(string Container, string Slot)>? drop = null;

        if (glassesRow is { } g && design["Bonus"] is JObject bonus)
            foreach (var p in bonus.Properties())
                if (p.Value is JObject s && s["BonusId"] is { } id
                    && (id.ToObject<ulong>() & BonusIdRowMask) == g)
                    (drop ??= []).Add(("Bonus", p.Name));

        // Every accessory slot a carrier can ride, not just the fingers — see NeutralizeProteusOwnedState.
        if (accessoryItems is { Count: > 0 } carriers && design["Equipment"] is JObject equip)
            foreach (var accessorySlot in new[] { "RFinger", "LFinger", "Wrists", "Neck" })
                if (equip[accessorySlot] is JObject s && s["ItemId"] is { } iid
                    && carriers.Contains(iid.ToObject<ulong>()))
                    (drop ??= []).Add(("Equipment", accessorySlot));

        if (drop == null) return design;

        var copy = (JObject)design.DeepClone();
        foreach (var (container, slot) in drop)
            (copy[container] as JObject)?.Remove(slot);
        return copy;
    }

    /// <summary>The carrier ids to strip at boot, straight from the game sheets.</summary>
    // Every accessory slot's invisible piece, not just the ring: at boot we cannot know which slots a
    // carrier was left in, so all of them are neutralized. Harmless for a slot we never used — the id only
    // matches if that exact item is actually worn.
    private (ulong? Glasses, IReadOnlyList<ulong> Accessories) BootCarriers()
        => (InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ItemId,
            InvisibleRing.CarrierSlots
                .Select(c => InvisibleRing.ResolveFor(Plugin.DataManager, log, c.Slot)?.ItemId)
                .Where(id => id != null).Select(id => id!.Value).Distinct().ToList());

    // Compare whichever numeric colour fields the design entry carries against the state entry, with
    // a small tolerance to absorb float round-trip noise. A field present on the design but missing
    // from the state is treated as a mismatch.
    private static readonly string[] ParamFields = ["Value", "Percentage", "Red", "Green", "Blue", "Alpha"];

    private static bool ParameterEquals(JObject d, JObject s)
    {
        foreach (var f in ParamFields)
        {
            if (d[f] is not { } dv) continue;
            if (s[f] is not { } sv) return false;
            if (Math.Abs(dv.ToObject<double>() - sv.ToObject<double>()) > 1e-4) return false;
        }
        return true;
    }

    // ── Persistence ─────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (File.Exists(storePath))
                store = JsonSerializer.Deserialize<DesignBindingStore>(File.ReadAllText(storePath), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to load design bindings; starting empty.");
            store = new();
        }
    }

    // Latest serialized store awaiting a write, and the gate that keeps writes from interleaving.
    private readonly object writeGate = new();
    private string? pendingJson;

    /// <summary>
    /// Serialize now (the caller holds <c>gate</c>, so <c>store</c> is consistent) but write off the
    /// calling thread. The bindings file grows to tens of MB, so a synchronous write from a UI click
    /// stalls the frame — and doing it inside the lock also blocks every concurrent binding operation.
    /// Whichever flush runs first writes the newest snapshot; superseded ones find nothing and skip, so
    /// a stale write can never land on top of a newer one.
    /// </summary>
    private void SaveDeferred()
    {
        try { Interlocked.Exchange(ref pendingJson, JsonSerializer.Serialize(store, JsonOpts)); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] Failed to serialize design bindings."); return; }

        Task.Run(() =>
        {
            lock (writeGate)
            {
                var json = Interlocked.Exchange(ref pendingJson, null);
                if (json == null) return;   // a later flush already wrote a newer snapshot
                try { File.WriteAllText(storePath, json); }
                catch (Exception ex) { log.Warning(ex, "[Proteus] Failed to save design bindings."); }
            }
        });
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(store, JsonOpts));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to save design bindings.");
        }
    }

    private static Dictionary<string, OverlayGearOverride> CloneGear(IEnumerable<ProteusModBinding> mods)
        => mods.ToDictionary(
            m => m.ModDirectory,
            m => JsonSerializer.Deserialize<OverlayGearOverride>(JsonSerializer.Serialize(m.Gear)) ?? new(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot a mod's gear-layer settings for every option, so the binding is self-contained (the
    /// right one is picked at composite time via OverlayGearOverride.Resolve) — mirrors CaptureColors.
    /// </summary>
    // Capture the *effective* gear settings (what the compositor is currently using): the live override
    // for this mod if a design is active — including unsaved layer/shader editor tweaks — else the mod's
    // metadata. Mirrors CaptureColors, so "Update binding" folds gear edits in just like colour edits.
    private OverlayGearOverride CaptureGear(OverlayEntry e)
    {
        OverlayGearOverride? active = null;
        lock (gate) activeGearOverride?.TryGetValue(e.ModDirectory, out active);

        var result = new OverlayGearOverride();

        var top = (e.Metadata.Overlays ?? []).FirstOrDefault();
        if (active?.Top != null) result.Top = CloneGearPreset(active.Top);
        else if (top != null) result.Top = GearSettingsPreset.From(top);

        // The Masks tab's own gear settings (its render mode), captured separately like the mask colours —
        // the synthesized Masks tab isn't a real option group. Live override first, else the metadata.
        if (active?.Mask != null) result.Mask = CloneGearPreset(active.Mask);
        else if (e.Metadata.MaskDescriptor is { } md) result.Mask = GearSettingsPreset.From(md);

        // An imported pack's unconditional pieces, on the same rule — the live override if a design is
        // driving it, else the sidecar's own. Its own slot, never Top: see OverlayGearOverride.Content.
        if (active?.Content != null) result.Content = CloneGearPreset(active.Content);
        else if (e.Metadata.ContentGlow is { } cg) result.Content = CloneGearPreset(cg);

        if (e.Metadata.OptionGroups is { } groups)
        {
            var opts = new Dictionary<string, Dictionary<string, GearSettingsPreset>>();
            foreach (var g in groups)
            foreach (var o in g.Options)
            {
                GearSettingsPreset? preset = null;
                if (active?.Options != null
                    && active.Options.TryGetValue(g.PenumbraGroupName, out var d)
                    && d.TryGetValue(o.Name, out var p))
                    preset = CloneGearPreset(p);
                if (preset == null)
                {
                    var desc = o.Overlays.FirstOrDefault();
                    if (desc == null) continue;
                    preset = GearSettingsPreset.From(desc);
                }
                if (!opts.TryGetValue(g.PenumbraGroupName, out var inner))
                    opts[g.PenumbraGroupName] = inner = new();
                inner[o.Name] = preset;
            }
            if (opts.Count > 0) result.Options = opts;
        }
        return result;
    }

    private static GearSettingsPreset CloneGearPreset(GearSettingsPreset p)
        => JsonSerializer.Deserialize<GearSettingsPreset>(JsonSerializer.Serialize(p)) ?? new();

    private static List<ColorTableRowPreset>? CloneRows(List<ColorTableRowPreset>? rows)
        => rows == null ? null : JsonSerializer.Deserialize<List<ColorTableRowPreset>>(JsonSerializer.Serialize(rows));

    /// <summary>
    /// Deep copy of a row list, for an editor that must preview edits without writing them into either the
    /// metadata or the binding until it decides where they belong.
    /// <para/>
    /// Per-element <see cref="ColorTableRowPreset.Clone"/> rather than the JSON round-trip
    /// <see cref="CloneRows"/> uses: this one runs in the ImGui draw path, once per frame per open tab.
    /// </summary>
    public static List<ColorTableRowPreset> CopyRows(List<ColorTableRowPreset>? rows)
    {
        var copy = new List<ColorTableRowPreset>(rows?.Count ?? 0);
        if (rows != null) foreach (var r in rows) copy.Add(r.Clone());
        return copy;
    }
}
