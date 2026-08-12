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

    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly SidecarDiscoveryService discovery;
    private readonly CompositorService compositor;
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

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
    }

    public void Dispose()
    {
        glamourer.LocalPlayerStateFinalized -= OnGlamourerStateFinalized;
    }

    // ── UI / accessors ─────────────────────────────────────────────────────────

    public Guid? ActiveDesignId { get { lock (gate) return activeDesignId; } }

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
            if (wasActive) { activeDesignId = null; activeOverride = null; activeGearOverride = null; activeStackOverride = null; }
            Save();
        }
        if (wasActive)
        {
            compositor.SetActiveColorOverride(null);
            compositor.TriggerRecomposite($"design-binding-remove:{id}");
        }
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

            Dictionary<string, OverlayColorOverride> newOverride;
            lock (gate)
            {
                store.Bindings[designId] = new DesignBinding
                {
                    DesignId    = designId,
                    DesignName  = name,
                    CapturedUtc = DateTime.UtcNow,
                    Mods        = mods,
                };
                designCache.Remove(designId); // design content changed → drop cached gear

                // The design was just saved from the current live state, so it's already "applied" in
                // spirit — mark it active immediately so the UI shows the binding as bound (blue) without
                // waiting for a separate Glamourer apply. Penumbra settings aren't re-pushed since they're
                // already what we just captured from. The live override is cloned so subsequent edits
                // preview without mutating the stored binding (see UpdateActiveBindingFromCurrentState).
                newOverride = CloneOverrides(mods);
                activeDesignId       = designId;
                activeOverride       = newOverride;
                activeGearOverride   = CloneGear(mods);
                activeStackOverride  = CloneStack(mods);
                Save();
            }
            compositor.SetActiveColorOverride(newOverride);
            compositor.SetActiveGearOverride(activeGearOverride);
            compositor.SetActiveStackOverride(activeStackOverride);
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

        lock (gate)
        {
            suppressUntilTick   = Environment.TickCount64 + RestoreSuppressMs;
            activeDesignId      = designId;
            // Clone so live color edits preview without mutating the stored binding (they only fold
            // in via UpdateActiveBindingFromCurrentState).
            activeOverride      = CloneOverrides(b.Mods);
            activeGearOverride  = CloneGear(b.Mods);
            activeStackOverride = CloneStack(b.Mods);
        }
        compositor.SetActiveColorOverride(activeOverride);
        compositor.SetActiveGearOverride(activeGearOverride);
        compositor.SetActiveStackOverride(activeStackOverride);

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
            foreach (var e in allMods)
            {
                if (!boundDirs.Contains(e.ModDirectory))
                    penumbra.SetModEnabled(collId.Value, e.ModDirectory, false);
            }
        }

        compositor.TriggerRecomposite($"design-restore:{designId}");
        log.Information("[Proteus] Restored Proteus state for design {0}.", b.DesignName ?? designId.ToString());
    }

    /// <summary>Drop the active color override (revert to metadata colors) and recomposite.</summary>
    public void ClearColorOverride()
    {
        lock (gate) { activeDesignId = null; activeOverride = null; activeGearOverride = null; activeStackOverride = null; }
        compositor.SetActiveColorOverride(null);
        compositor.SetActiveGearOverride(null);
        compositor.SetActiveStackOverride(null);
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
        bool changed = false;
        lock (gate)
        {
            if (activeDesignId != null) { activeDesignId = null; activeOverride = null; activeGearOverride = null; activeStackOverride = null; changed = true; }
        }
        if (changed)
        {
            compositor.SetActiveColorOverride(null);
            compositor.SetActiveGearOverride(null);
            compositor.SetActiveStackOverride(null);
            // Ambient: reached from Glamourer's state-finalized signal, which fires on every zone-in. The
            // overrides just cleared are part of the composite fingerprint, so a real unbinding still
            // composites — this only lets a re-assert that changed nothing stop early.
            compositor.TriggerRecomposite("design-binding-unbound", force: false);
        }
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
                touched |= ClearScope(gear.Options, group, option, () => { bool had = gear.Top != null; gear.Top = null; return had; });

            // Persisted binding, so the design stops re-applying it.
            if (store.Bindings.TryGetValue(id, out var b))
            {
                var mod = b.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
                if (mod != null)
                {
                    touched |= ClearScope(mod.Colors.Options, group, option,
                        () => { bool had = mod.Colors.Top != null; mod.Colors.Top = null; return had; });
                    touched |= ClearScope(mod.Gear.Options, group, option,
                        () => { bool had = mod.Gear.Top != null; mod.Gear.Top = null; return had; });
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

    private void OnGlamourerStateFinalized(StateFinalizationType type)
    {
        // A revert leaves no design applied: drop the override so colours fall back to metadata. This is
        // NOT the unbound-design case (nothing failed to match), it just has the same effect.
        if (IsRevertSignal(type))
        {
            if (Environment.TickCount64 >= suppressUntilTick) HandleUnboundDesign();
            return;
        }

        if (!IsApplySignal(type)) return;
        if (Environment.TickCount64 < suppressUntilTick) return; // our own restore echo

        // Feature disabled → never restore. Also drop any override left active from before the
        // toggle was turned off, so "off" means colors fall back to metadata (off == fully off).
        if (!config.DesignBindingEnabled)
        {
            if (activeDesignId != null) ClearColorOverride();
            return;
        }

        Guid[] candidateIds;
        lock (gate) candidateIds = store.Bindings.Keys.ToArray();
        if (candidateIds.Length == 0) { HandleUnboundDesign(); return; } // nothing ever bound

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
            compositor.InjectedGlassesItemId, compositor.InjectedRingItemId);

        var matches = new List<(Guid id, int specificity)>();
        foreach (var id in candidateIds)
        {
            var design = GetDesignCached(id);
            if (design != null && StateMatches(design, state, out var spec))
                matches.Add((id, spec));
        }

        if (matches.Count == 0)
        {
            // No binding matched, so overrides get dropped and gear dyes revert to metadata. This is the
            // apply-match false-negative that silently clears a correctly-dyed design on a redraw, so log
            // the FIRST field that rejected each candidate — the usual culprit and hard to spot otherwise.
            foreach (var id in candidateIds)
            {
                var d = GetDesignCached(id);
                if (d == null) continue;
                string name;
                lock (gate) name = (store.Bindings.TryGetValue(id, out var b) ? b.DesignName : null) ?? id.ToString()[..8];
                StateMatches(d, state, out _, why =>
                    log.Warning("[Proteus] design match REJECTED '{0}': {1}", name, why));
            }
            log.Warning("[Proteus] design-binding: NO binding matched the applied state — dropping colour/gear overrides (dyes revert to metadata white).");
            HandleUnboundDesign();
            return;
        }

        // For ambiguous matches (variations of the same outfit share a gear set), prefer the
        // most *specific* match — the design that constrains the most applied fields (e.g. one
        // that also matches the applied dye beats a gear-only design). Break remaining ties by
        // the most recently captured binding, which avoids stale older overrides sticking around.
        Guid pick;
        if (matches.Count == 1)
        {
            pick = matches[0].id;
        }
        else
        {
            var best = matches.Max(m => m.specificity);
            var top  = matches.Where(m => m.specificity == best).Select(m => m.id).ToList();
            if (top.Count == 1)
                pick = top[0];
            else
                lock (gate) pick = PickMostRecent(top, store.Bindings);
        }

        if (activeDesignId == pick) return; // already applied
        Restore(pick);
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
        ulong? syntheticRingId = null)
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

        // Same for the ring we equip to host the shell in cut space — either hand, since it goes to
        // whichever slot was free. Nothing zero-ish about the id: a design that saved an empty ring stores
        // ItemId 0, so that is what "not the player's choice" has to look like here — otherwise wearing
        // our ring makes every such design mismatch.
        if (syntheticRingId is { } ring)
        {
            foreach (var finger in new[] { "RFinger", "LFinger" })
            {
                if ((copy ?? state)["Equipment"] is not JObject equip
                    || equip[finger] is not JObject slot
                    || slot["ItemId"] is not { } rid || rid.ToObject<ulong>() != ring)
                    continue;
                copy ??= (JObject)state.DeepClone();
                if (((JObject?)copy["Equipment"])?[finger] is JObject target)
                    target["ItemId"] = 0;
            }
        }

        return copy ?? state;
    }

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
