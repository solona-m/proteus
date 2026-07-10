using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private Guid? activeDesignId;
    private bool unboundModsDisabled;
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

        glamourer.LocalPlayerStateChangedTyped += OnGlamourerStateChangedTyped;
    }

    public void Dispose()
    {
        glamourer.LocalPlayerStateChangedTyped -= OnGlamourerStateChangedTyped;
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
            if (wasActive) { activeDesignId = null; activeOverride = null; }
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

            var mods = new List<ProteusModBinding>();
            foreach (var e in discovery.DiscoverAll())
            {
                var settings = penumbra.GetModSettings(collId.Value, e.ModDirectory);
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
                });
            }

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
                // already what we just captured from.
                newOverride = mods.ToDictionary(m => m.ModDirectory, m => m.Colors, StringComparer.OrdinalIgnoreCase);
                activeDesignId       = designId;
                activeOverride       = newOverride;
                unboundModsDisabled  = false;
                Save();
            }
            compositor.SetActiveColorOverride(newOverride);
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
            Top = CloneRows(active?.Top ?? e.Metadata.ColorTableRows),
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
            activeOverride      = b.Mods.ToDictionary(m => m.ModDirectory, m => m.Colors, StringComparer.OrdinalIgnoreCase);
            unboundModsDisabled = false;
        }
        compositor.SetActiveColorOverride(activeOverride);

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
        lock (gate) { activeDesignId = null; activeOverride = null; }
        compositor.SetActiveColorOverride(null);
        compositor.TriggerRecomposite("design-override-clear");
    }

    /// <summary>
    /// The currently applied design has no captured binding (never bound, or no gear match): revert
    /// colors to metadata and disable every discovered Proteus overlay mod, so an unrecognized look
    /// never keeps a previous design's overlays composited onto it. Idempotent via
    /// <see cref="unboundModsDisabled"/> so repeated apply signals while sitting on the same unbound
    /// design don't re-issue Penumbra calls every event.
    /// </summary>
    private void HandleUnboundDesign()
    {
        bool changed = false;
        lock (gate)
        {
            if (activeDesignId != null) { activeDesignId = null; activeOverride = null; changed = true; }
        }
        if (changed) compositor.SetActiveColorOverride(null);

        if (!unboundModsDisabled)
        {
            DisableAllProteusMods();
            unboundModsDisabled = true;
            changed = true;
        }

        if (changed) compositor.TriggerRecomposite("design-binding-unbound");
    }

    private void DisableAllProteusMods()
    {
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
    /// The mutable rows list the editor should bind to when an override is active for this mod,
    /// or null if none. group/option=null targets the top-level rows; otherwise the option's rows.
    /// Seeds (clones) from seedRows (the mod's metadata rows for the same scope) when the override
    /// has nothing stored yet, so editing starts from what was on screen.
    /// </summary>
    public List<ColorTableRowPreset>? GetEditableOverrideRows(
        string modDir, string? group, string? option, List<ColorTableRowPreset>? seedRows)
    {
        lock (gate)
        {
            if (activeOverride == null || !activeOverride.TryGetValue(modDir, out var ovr))
                return null;
            if (group != null && option != null)
            {
                ovr.Options ??= new();
                if (!ovr.Options.TryGetValue(group, out var inner))
                    ovr.Options[group] = inner = new();
                if (!inner.TryGetValue(option, out var rows))
                    inner[option] = rows = CloneRows(seedRows) ?? new();
                return rows;
            }
            return ovr.Top ??= CloneRows(seedRows) ?? new();
        }
    }

    /// <summary>
    /// Persist + re-push the live override after the editor mutated a list from
    /// GetEditableOverrideRows. No-op if no binding active. Caller triggers the recomposite.
    /// </summary>
    public void CommitActiveOverrideEdit()
    {
        Dictionary<string, OverlayColorOverride>? snapshot;
        lock (gate)
        {
            if (activeDesignId == null || activeOverride == null) return;
            snapshot = activeOverride;
            Save();
        }
        compositor.SetActiveColorOverride(snapshot);
    }

    /// <summary>
    /// Fold a manual enable/priority change (made in the Proteus UI) into the active binding so the
    /// edit becomes the binding's new truth, instead of being reverted by the next restore. No-op if
    /// no binding is active or the mod isn't part of it. Returns true if the binding was updated.
    /// </summary>
    public bool UpdateActiveBindingMod(string modDir, bool? enabled = null, int? priority = null)
    {
        lock (gate)
        {
            if (activeDesignId == null) return false;
            if (!store.Bindings.TryGetValue(activeDesignId.Value, out var b)) return false;
            if (!ApplyManualModEdit(b, modDir, enabled, priority)) return false;
            Save();
            return true;
        }
    }

    // Apply an enable/priority edit to a binding's stored mod entry. Returns true iff something
    // actually changed (so the caller can skip persisting). Pure — unit-tested without Dalamud.
    internal static bool ApplyManualModEdit(DesignBinding b, string modDir, bool? enabled, int? priority)
    {
        var mod = b.Mods.FirstOrDefault(m =>
            string.Equals(m.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
        if (mod == null) return false;

        bool changed = false;
        if (enabled.HasValue  && mod.Enabled  != enabled.Value)  { mod.Enabled  = enabled.Value;  changed = true; }
        if (priority.HasValue && mod.Priority != priority.Value) { mod.Priority = priority.Value; changed = true; }
        return changed;
    }

    // ── Heuristic apply detection (framework thread) ────────────────────────────

    // Glamourer automation never delivers Design over IPC for the local player: a Fixed-source
    // ApplyDesign carries no actors, so the heuristic only ever sees Reapply (automation apply and
    // revert) — and Reset (manual revert). Mirror GlamourerBridge.OnStateChanged's state-wide set.
    internal static bool IsApplySignal(StateChangeType type)
        => type is StateChangeType.Design or StateChangeType.Reapply or StateChangeType.Reset;

    private void OnGlamourerStateChangedTyped(StateChangeType type)
    {
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

        var matches = new List<(Guid id, int specificity)>();
        foreach (var id in candidateIds)
        {
            var design = GetDesignCached(id);
            if (design != null && StateMatches(design, state, out var spec))
                matches.Add((id, spec));
        }

        if (matches.Count == 0)
        {
            // An unbound/unrecognized design was applied.
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
    internal static bool StateMatches(JObject design, JObject state, out int specificity)
    {
        specificity = 0;
        if (design["Equipment"] is not JObject dEquip || state["Equipment"] is not JObject sEquip)
            return false;

        int gearSlots = 0;
        foreach (var prop in dEquip.Properties())
        {
            if (NonMatchedSlots.Contains(prop.Name)) continue;      // weapons vary situationally
            if (prop.Value is not JObject dSlot) continue;
            var sSlot = sEquip[prop.Name] as JObject;

            // Equipment item id. Meta entries (Hat/Visor/Weapon/VieraEars) have no ItemId → skipped.
            if (dSlot["ItemId"] is { } dItem && dSlot["Apply"]?.ToObject<bool>() == true)
            {
                if (sSlot?["ItemId"] is not { } sItem) return false;                 // state lacks the slot
                if (dItem.ToObject<ulong>() != sItem.ToObject<ulong>()) return false; // different item
                gearSlots++;
                specificity++;
            }

            // Dye/stain, compared independently of the item (a re-dye is a different look).
            if (dSlot["ApplyStain"]?.ToObject<bool>() == true)
            {
                if (sSlot == null) return false;
                if (!IdEquals(dSlot["Stain"],  sSlot["Stain"]))  return false;
                if (!IdEquals(dSlot["Stain2"], sSlot["Stain2"])) return false;
                specificity++;
            }
        }

        if (gearSlots < MinGearSlots) return false; // appearance-only designs never match (safe abstain)

        // Bonus items (glasses / facewear).
        if (design["Bonus"] is JObject dBonus && state["Bonus"] is JObject sBonus)
        {
            foreach (var prop in dBonus.Properties())
            {
                if (prop.Value is not JObject dItem) continue;
                if (dItem["Apply"]?.ToObject<bool>() != true) continue;
                if (sBonus[prop.Name] is not JObject sItem) return false;
                if (!IdEquals(dItem["BonusId"], sItem["BonusId"])) return false;
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
                if (sCust[prop.Name] is not JObject sEntry || sEntry["Value"] is not { } sVal) return false;
                if (dVal.ToObject<long>() != sVal.ToObject<long>()) return false;
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
                if (sParams[prop.Name] is not JObject sEntry) return false;
                if (!ParameterEquals(dEntry, sEntry)) return false;
                specificity++;
            }
        }

        return true;
    }

    private static bool IdEquals(JToken? a, JToken? b)
        => a != null && b != null && a.ToObject<ulong>() == b.ToObject<ulong>();

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

    private static List<ColorTableRowPreset>? CloneRows(List<ColorTableRowPreset>? rows)
        => rows == null ? null : JsonSerializer.Deserialize<List<ColorTableRowPreset>>(JsonSerializer.Serialize(rows));
}
