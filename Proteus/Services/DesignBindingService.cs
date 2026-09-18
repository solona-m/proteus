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
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

// ── Persisted model (design_bindings.json) ──────────────────────────────────

public class DesignBindingStore
{
    /// <summary>Version 2 adds <see cref="DesignBinding.CharacterMods"/>; a version-1 store loads unchanged.</summary>
    public int Version { get; set; } = 2;
    public Dictionary<Guid, DesignBinding> Bindings { get; set; } = new();
}

public class DesignBinding
{
    public Guid DesignId { get; set; }
    public string? DesignName { get; set; }
    public DateTime CapturedUtc { get; set; }
    public List<ProteusModBinding> Mods { get; set; } = new();

    /// <summary>
    /// The Penumbra settings of every mod on the character when the design was saved, read underneath any
    /// temporary settings. Empty ⇒ the binding restores Proteus mods only.
    /// </summary>
    public List<PenumbraModSetting> CharacterMods { get; set; } = new();

    /// <summary>Whether this binding carries a whole-character snapshot rather than Proteus mods alone.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCharacterSnapshot => CharacterMods.Count > 0;
}

/// <summary>One mod's permanent Penumbra settings, as a design binding recorded them.</summary>
public class PenumbraModSetting
{
    public string ModDirectory { get; set; } = string.Empty;
    public string? ModName { get; set; }
    public bool Enabled { get; set; }
    public int Priority { get; set; }

    /// <summary>Penumbra option group → selected option names.</summary>
    public Dictionary<string, List<string>> Options { get; set; } = new();
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

    /// <summary>Effective gear-layer settings at capture time; applied as an in-memory override like Colors.</summary>
    public OverlayGearOverride Gear { get; set; } = new();

    /// <summary>The mod-wide overlay stack order top-first at capture time (<see cref="Configuration.ModStackEntry"/>
    /// keys), applied as an in-memory override. Empty ⇒ the composite keeps the global order.</summary>
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
    // A design must apply at least this many equipment slots to be a heuristic candidate; gearless designs never match.
    private const int MinGearSlots = 3;
    private const int RestoreSuppressMs = 2000;

    // Widest gap (ms) between the foreign Reapply and the Gearset finalization for the pair to read as one
    // automation apply. See IsInferredAutomationApply.
    private const int AutomationPairWindowMs = 2000;

    // How long (ms) we keep expecting the Gearset that our own redraw causes. A one-shot expectation, not a
    // blackout; see CompositorService.ConsumeOwnRedrawEcho.
    private const int OwnRedrawEchoMs = 10000;

    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly SidecarDiscoveryService discovery;
    private readonly CompositorService compositor;
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    // Encoder is write-only: design names are often non-ASCII, and escaping them makes the store unreadable.
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Encoder = ProteusJson.Encoder };

    private readonly string storePath;
    private readonly object gate = new();
    private DesignBindingStore store = new();

    // The live colour / gear / stack overrides this design publishes. Shared implementation with PresetService.
    private readonly OverlayOverrideBag overrides;

    /// <summary>
    /// Raised when a design takes over the look, so pinned per-mod presets drop (saved presets are untouched).
    /// An event rather than a call because PresetService already depends on this service.
    /// </summary>
    public event Action? PresetsSuperseded;

    // All of the below are touched only on the framework thread (watcher callbacks marshal first).
    private Guid? activeDesignId;
    // The design that was active at logout, until the next login resolves. Its overrides stay published meanwhile,
    // so re-adopting it keeps unsaved edits and pinned presets; anything else must replace or clear them.
    private Guid? suspendedDesignId;
    private long suppressUntilTick;
    private readonly Dictionary<Guid, JObject?> designCache = new();

    // Boot restore poll cadence and give-up; the deadline clock starts only once the local player exists.
    private const int BootRestorePollMs    = 250;
    private const int BootRestoreTimeoutMs = 20000;

    // How long a mismatch is treated as "the state hasn't settled yet" rather than the answer: our own Dispose
    // removes injected accessories asynchronously. Also the worst-case extra delay on the unbound path.
    private const int BootSettleMs = 3000;

    private long bootDeadlineTick;          // 0 until the local player first exists
    private long bootSettleUntilTick;
    private long lastBootRestorePollTick;
    private bool bootStep1Reported;         // the deterministic step-1 reasons are logged once, not per poll
    private bool bootAtLogin;               // the poll waited for a player, so this is a login, not a reload
    private bool bootAwaitingDeparture;     // armed at logout: the leaving character must go before the poll may resolve
    private long bootDepartureDeadlineTick; // when that wait gives up, in case the logout never completes
    private int  bootRestoreDone = 1;       // 1 = resolved or never armed; the ctor and each logout set 0 when arming

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

        overrides = new OverlayOverrideBag(
            compositor.SetActiveColorOverride, compositor.SetActiveGearOverride, compositor.SetActiveStackOverride);

        storePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "design_bindings.json");
        Load();

        glamourer.LocalPlayerStateFinalized += OnGlamourerStateFinalized;
        glamourer.LocalPlayerStateChangedAny += OnGlamourerStateChangedAny;
        penumbra.ModSettingChanged += OnPenumbraModSettingChanged;
        penumbra.LocalPlayerRedrawn += OnLocalPlayerRedrawn;

        Plugin.ClientState.Logout += OnLogout;

        // Pick up the binding the character is already wearing: Glamourer fires no apply signal on a plugin
        // reload, nor for the automation that dresses the character at login. Armed here and again at each
        // logout (RearmForNextLogin).
        if (ShouldArmBootRestore())
        {
            Volatile.Write(ref bootRestoreDone, 0);
            framework.Update += OnBootRestoreTick;
            log.Information("[Proteus] design-binding: boot restore armed (last active {0}, {1} binding(s)); boot composite held.",
                config.LastActiveDesignId?.ToString() ?? "(none)", store.Bindings.Count);
        }
        else
        {
            log.Debug("[Proteus] design-binding: no boot restore (enabled={0}, glamourer={1}, lastActive={2}, bindings={3}).",
                config.DesignBindingEnabled, glamourer.IsAvailable,
                config.LastActiveDesignId?.ToString() ?? "(none)", store.Bindings.Count);
        }
    }

    public void Dispose()
    {
        Plugin.ClientState.Logout -= OnLogout;
        glamourer.LocalPlayerStateFinalized -= OnGlamourerStateFinalized;
        glamourer.LocalPlayerStateChangedAny -= OnGlamourerStateChangedAny;
        penumbra.ModSettingChanged -= OnPenumbraModSettingChanged;
        penumbra.LocalPlayerRedrawn -= OnLocalPlayerRedrawn;
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
        // Via ClearOverrides so the gear and stack overrides are un-published too.
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
    /// Called when a design's {guid}.json is deleted. Drops the binding even when DesignBindingEnabled is
    /// off, since bindings for vanished designs would pollute ambiguous-match resolution.
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

            // Only a design the character is actually wearing may be captured: Glamourer rewrites a design's
            // file on every edit. Judged exactly as an apply is, so the apply path can recognise it again.
            lock (gate) designCache.Remove(designId);   // the file just changed; never judge a stale copy
            var design = GetDesignCached(designId);
            var state  = glamourer.GetObjectState(0);
            if (design == null || state == null)
            {
                log.Debug("[Proteus] Skipping design capture for {0}: {1} unreadable, so it can't be checked against the character.",
                    designId, design == null ? "design" : "Glamourer state");
                return;
            }
            string? mismatch = null;
            if (!StateMatches(StripCarriers(design, LiveCarriers()), state, out _, r => mismatch ??= r))
            {
                log.Debug("[Proteus] Skipping design capture for {0}: the character isn't wearing it ({1}).",
                    designId, mismatch ?? "no reason reported");
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

            var characterMods = ReadCharacterMods(collId.Value);

            lock (gate)
            {
                // Unreadable (not drawn): keep the snapshot it had rather than wiping it.
                binding.CharacterMods = characterMods
                    ?? (store.Bindings.TryGetValue(designId, out var previous) ? previous.CharacterMods : new());
                store.Bindings[designId] = binding;
                Save();
            }

            // The design was just saved from the live state, so mark it active now. Penumbra settings aren't
            // re-pushed, hence no echo to suppress.
            AdoptOverrides(binding, designId, suppressEcho: false);
            compositor.TriggerRecomposite($"design-capture:{designId}");
            UsageStats.Count(UsageFeature.DesignBindCapture);

            log.Information("[Proteus] Captured Proteus state for design {0} ({1} mods).", name ?? designId.ToString(), mods.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to capture design binding for {0}", designId);
        }
    }

    // Capture the effective colors for every option, so the binding is self-contained.
    private OverlayColorOverride CaptureColors(OverlayEntry e)
    {
        // Ask the compositor rather than this service's bag: a preset applied on top is what the player sees.
        var active = compositor.EffectiveColorOverrideFor(e.ModDirectory);

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

        // Imported content packs keep their rows on ContentOption, keyed the same way as OverlayOption.
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

        // And per material, which is the level the colour panel edits a pack at: capturing only the options
        // would record a look the composite never shows (ContentSettingLevels).
        result.Materials = CaptureMaterials(
            e.Metadata.ContentMaterials, active?.Materials, m => CloneRows(m.ColorTableRows),
            r => CloneRows(r)!);

        return result;
    }

    /// <summary>
    /// One content pack's per-material settings for a binding: the live override's entry where there is one, else
    /// the mod's own, over the union of both key sets. Cloned on both paths, so previewing an edit never moves
    /// what is stored. Null when neither side has anything.
    /// </summary>
    private static Dictionary<string, T>? CaptureMaterials<T>(
        Dictionary<string, ContentMaterialSettings>? stored,
        IReadOnlyDictionary<string, T>? active,
        Func<ContentMaterialSettings, T?> fromStored,
        Func<T, T> clone) where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        if (stored != null)
            foreach (var (rel, settings) in stored)
                if (fromStored(settings) is { } value) result[rel] = value;

        if (active != null)
            foreach (var (rel, value) in active) result[rel] = clone(value);

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Snapshot one mod's live state: Penumbra enable/priority/options plus the effective colours, gear and
    /// stack order (unsaved edits and pinned presets included). Presets capture through this same call.
    /// </summary>
    public ProteusModBinding CaptureMod(OverlayEntry e, Guid collId)
    {
        var settings = penumbra.GetModSettings(collId, e.ModDirectory);
        var options  = settings?.Options is { } o
            ? o.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value))
            : new Dictionary<string, List<string>>();

        return new ProteusModBinding
        {
            ModDirectory = e.ModDirectory,
            Enabled      = e.Enabled,
            Priority     = e.Priority,
            Options      = options,
            Colors       = CaptureColors(e),
            Gear         = CaptureGear(e),
            StackOrder   = CaptureStackOrder(e.ModDirectory),
        };
    }

    // Snapshot every discovered Proteus mod's current live state into fresh binding entries.
    private List<ProteusModBinding> BuildModBindings(Guid collId)
        => discovery.DiscoverAll().Select(e => CaptureMod(e, collId)).ToList();

    // The mod-wide stack order to record: the live override if any, else the global config order, else empty.
    private List<string> CaptureStackOrder(string modDir)
    {
        if (compositor.EffectiveStackOverrideFor(modDir) is { } live)
            return new List<string>(live);
        return config.OverlayModStackOrder.TryGetValue(modDir, out var cfg)
            ? new List<string>(cfg)
            : new List<string>();
    }

    // Live color override keyed by mod dir, cloned so live preview never mutates the persisted binding.
    private static Dictionary<string, OverlayColorOverride> CloneOverrides(IEnumerable<ProteusModBinding> mods)
        => mods.ToDictionary(m => m.ModDirectory, m => CloneOverride(m.Colors), StringComparer.OrdinalIgnoreCase);

    private static OverlayColorOverride CloneOverride(OverlayColorOverride o)
        => JsonSerializer.Deserialize<OverlayColorOverride>(JsonSerializer.Serialize(o)) ?? new();

    // Live stack override keyed by mod dir, cloned; only mods that captured an order contribute.
    private static Dictionary<string, List<string>> CloneStack(IEnumerable<ProteusModBinding> mods)
        => mods.Where(m => m.StackOrder.Count > 0)
               .ToDictionary(m => m.ModDirectory, m => new List<string>(m.StackOrder), StringComparer.OrdinalIgnoreCase);

    // ── Restore / clear (framework thread) ──────────────────────────────────────

    /// <summary>
    /// Publish a binding's colour / gear / stack overrides as active and mark its design active. Does not
    /// write Penumbra, disable unbound mods or recomposite. Framework thread.
    /// </summary>
    /// <param name="suppressEcho">Arm the <see cref="RestoreSuppressMs"/> window that ignores the finalization our
    /// own Penumbra writes provoke. False for paths that write none.</param>
    private void AdoptOverrides(DesignBinding b, Guid designId, bool suppressEcho)
    {
        lock (gate)
        {
            if (suppressEcho) suppressUntilTick = Environment.TickCount64 + RestoreSuppressMs;
            activeDesignId = designId;
        }

        PersistActiveDesignId(designId);

        // A design supersedes any per-mod preset pinned on the previous look.
        PresetsSuperseded?.Invoke();

        // Clone so live colour edits preview without mutating the stored binding.
        overrides.Adopt(CloneOverrides(b.Mods), CloneGear(b.Mods), CloneStack(b.Mods));
    }

    /// <summary>
    /// The unbound sidecar mods that also ship their own Penumbra content; <see cref="Restore"/> leaves these
    /// enabled, since disabling them takes content the binding cannot put back. With
    /// <paramref name="stripImported"/>, imported packs (<see cref="ProteusMetadata.HasContent"/>) are not held.
    /// </summary>
    private static HashSet<string> UnboundContentMods(DesignBinding b, IReadOnlyList<OverlayEntry> discovered, bool stripImported)
        => UnboundContentMods(b.Mods.Select(m => m.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase), discovered, stripImported);

    private static HashSet<string> UnboundContentMods(HashSet<string> bound, IReadOnlyList<OverlayEntry> discovered, bool stripImported)
    {
        var held  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in discovered)
        {
            if (bound.Contains(e.ModDirectory)) continue;
            if (stripImported && e.Metadata.HasContent) continue;
            // SidecarRoot is the Proteus/ subfolder; trim first, or GetDirectoryName returns the sidecar folder
            // itself and every unbound mod would be held.
            var modRoot = Path.GetDirectoryName(
                e.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (modRoot != null && PenumbraModMeta.PublishesGameContent(modRoot))
                held.Add(e.ModDirectory);
        }

        return held;
    }

    /// <summary>
    /// Drop the active overrides and design and un-publish them; does not recomposite. Returns whether
    /// anything was active. Framework thread.
    /// </summary>
    private bool ClearOverrides()
    {
        bool hadDesign;
        lock (gate)
        {
            // A design suspended across a logout still has its overrides published, so it counts.
            hadDesign         = activeDesignId != null || suspendedDesignId != null;
            activeDesignId    = null;
            suspendedDesignId = null;
        }

        // Run even when nothing was active: the persisted pointer can be stale, and a revert mid-boot must
        // supersede the boot restore.
        PersistActiveDesignId(null);
        if (BootRestoreArmed) FinishBootRestore("superseded by an explicit clear");
        // Nothing is being restored any more, so a temporary setting landing now must be left alone.
        DisarmTemporaryGuard();
        // Supersede a character restore's post-load sweep and release the mods it held.
        characterWorkGeneration++;
        var released = ReleaseHeldMods();

        // Held mods let go change the look as much as a cleared override does.
        return overrides.Clear() | hadDesign | released;
    }

    /// <summary>
    /// Remember the active design so a plugin reload can pick it back up (Glamourer raises no apply signal
    /// for an already-applied design). No-ops when unchanged.
    /// </summary>
    private void PersistActiveDesignId(Guid? id)
    {
        if (config.LastActiveDesignId == id) return;
        config.LastActiveDesignId = id;
        compositor.SaveConfig();
        log.Debug("[Proteus] design-binding: persisted active design = {0}", id?.ToString() ?? "(none)");
    }

    /// <param name="stripImported">Also switch off imported Proteus packs the binding didn't capture — see
    /// <see cref="UnboundContentMods(HashSet{string}, IReadOnlyList{OverlayEntry}, bool)"/>. Set on the same
    /// applies that unequip the slots a design leaves unset.</param>
    public void Restore(Guid designId, bool stripImported = false)
    {
        DesignBinding? b;
        lock (gate) store.Bindings.TryGetValue(designId, out b);
        if (b == null) return;
        UsageStats.Count(UsageFeature.DesignBindRestore);

        if (b.HasCharacterSnapshot && config.DesignBindingRestoresCharacterMods) RestoreCharacter(b, designId, stripImported);
        else                                                                   RestoreProteusOnly(b, designId, stripImported);
    }

    /// <summary>
    /// The Proteus-only restore: for bindings without a character snapshot, or while
    /// <see cref="Configuration.DesignBindingRestoresCharacterMods"/> is off.
    /// </summary>
    private void RestoreProteusOnly(DesignBinding b, Guid designId, bool stripImported)
    {
        // Supersedes any character restore.
        characterWorkGeneration++;
        ReleaseHeldMods();

        var allMods = discovery.DiscoverAll();
        var present = allMods
            .Select(e => e.ModDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boundDirs = b.Mods
            .Select(m => m.ModDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collId = penumbra.GetPlayerCollectionId();

        AdoptOverrides(b, designId, suppressEcho: true);

        // Exempt from the disable sweep below only; they still composite. See UnboundContentMods.
        var held = UnboundContentMods(b, allMods, stripImported);
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

            // Proteus mods outside this binding are switched off, except those holding their own Penumbra content.
            foreach (var e in allMods)
            {
                if (boundDirs.Contains(e.ModDirectory) || held.Contains(e.ModDirectory)) continue;
                penumbra.SetModEnabled(collId.Value, e.ModDirectory, false);
            }
        }

        compositor.TriggerRecomposite($"design-restore:{designId}");
        log.Information("[Proteus] Restored Proteus state for design {0}.", b.DesignName ?? designId.ToString());
    }

    // ── Character snapshot (capture) ────────────────────────────────────────────

    // Bumped by everything that supersedes a character restore, so its post-load sweep stands down.
    // Framework thread only.
    private int characterWorkGeneration;

    /// <summary>
    /// The settings of every mod on the character right now, or null when unreadable (the caller then keeps
    /// the snapshot it had).
    /// </summary>
    private List<PenumbraModSetting>? ReadCharacterMods(Guid collId)
    {
        var modsRoot  = penumbra.GetModDirectory();
        var resources = penumbra.GetActivePlayerResourceMap();
        var permanent = penumbra.GetCollectionModSettings(collId, ignoreTemporary: true);
        var names     = penumbra.GetAllMods();
        if (modsRoot == null || resources == null || permanent == null || names == null)
        {
            log.Information("[Proteus] design-binding: character snapshot skipped — Penumbra state unreadable (root={0}, tree={1}, settings={2}).",
                modsRoot != null, resources != null, permanent != null);
            return null;
        }

        var proteusDirs = discovery.DiscoverAll().Select(e => e.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mods = SelectCharacterMods(modsRoot, resources.Keys, permanent, heldTemporary, names, proteusDirs);
        log.Information("[Proteus] design-binding: captured {0} mod(s) on the character.", mods.Count);
        return mods;
    }

    /// <summary>
    /// Which mods make up the character: every mod a drawn file resolves into, plus every Proteus mod (drawn
    /// through the managed mod, so the tree never names them). A <paramref name="held"/> mod records the
    /// design's un-raised state; any other records its PERMANENT setting, beneath Glamourer's temporary ones.
    /// </summary>
    internal static List<PenumbraModSetting> SelectCharacterMods(
        string modsRoot,
        IEnumerable<string> drawnFiles,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> permanent,
        IReadOnlyDictionary<string, PenumbraModSetting> held,
        IReadOnlyDictionary<string, string> installed,
        IReadOnlySet<string> proteusDirs)
    {
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in drawnFiles)
            if (OwningMod(modsRoot, file) is { } owner && installed.ContainsKey(owner))
                chosen.Add(owner);

        foreach (var dir in proteusDirs)
            if (installed.ContainsKey(dir))
                chosen.Add(dir);

        chosen.Remove(SidecarDiscoveryService.ManagedModDir);

        return chosen
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(dir =>
            {
                var name = installed.TryGetValue(dir, out var n) ? n : null;
                if (held.TryGetValue(dir, out var h))
                    return new PenumbraModSetting
                    {
                        ModDirectory = dir, ModName = name, Enabled = h.Enabled, Priority = h.Priority,
                        Options = h.Options.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
                    };

                permanent.TryGetValue(dir, out var s);
                return new PenumbraModSetting
                {
                    ModDirectory = dir,
                    ModName      = name,
                    Enabled      = s.Options != null && s.Enabled,
                    Priority     = s.Options != null ? s.Priority : 0,
                    Options      = s.Options?.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value))
                                   ?? new Dictionary<string, List<string>>(),
                };
            })
            .ToList();
    }

    /// <summary>The mod directory a resolved file lives in, or null when it is not under the mods folder.</summary>
    internal static string? OwningMod(string modsRoot, string resolvedPath)
    {
        var root = modsRoot.Replace('\\', '/').TrimEnd('/') + "/";
        var file = resolvedPath.Replace('\\', '/');
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var rest  = file.AsSpan(root.Length);
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash].ToString() : null;
    }

    // ── Character snapshot: holding mods temporarily ────────────────────────────
    //
    // A restore holds non-Proteus mods in the design's state with a Penumbra temporary setting locked with
    // Proteus's key, and releases them all when the look ends, so the collection is left as the player had it.
    // Proteus mods keep permanent writes, or Proteus's own option edits would appear to do nothing.
    // The lock is positive: others can't replace it, and readers must pass TemporaryKey to see it. Penumbra
    // forgets all of them on restart; the boot restore holds them again.

    internal const int    TemporaryKey    = 0x50524F54;   // "PROT"
    internal const string TemporarySource = "Proteus";

    // What each held mod is held as, with its recorded (un-raised) priority; a save reads this back. Framework thread only.
    private readonly Dictionary<string, PenumbraModSetting> heldTemporary = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Let go of every mod Proteus is holding in the player's collection; returns whether anything was released.
    /// Removes by key, since after a plugin reload the in-memory list is empty while Penumbra still holds them.
    /// </summary>
    private bool ReleaseHeldMods()
    {
        if (penumbra.GetPlayerCollectionId() is not { } collId) return false;
        var had = heldTemporary.Count > 0;
        heldTemporary.Clear();
        // No suppression scope: NothingChanged raises no events, and a real release should recomposite.
        var ec = penumbra.RemoveAllTemporaryModSettings(collId, TemporaryKey);
        if (ec == PenumbraApiEc.Success)
            log.Information("[Proteus] design-binding: released the mods Proteus was holding for the previous look.");
        return had || ec == PenumbraApiEc.Success;
    }

    /// <summary>
    /// "Restore every mod on the character" was switched. Off: release everything held. On: restore the
    /// active design's whole look now.
    /// </summary>
    public void OnRestoreCharacterModsToggled(bool on)
    {
        characterWorkGeneration++;
        if (!on)
        {
            if (ReleaseHeldMods()) compositor.TriggerRecomposite("design-binding-release");
            return;
        }
        if (ActiveDesignId is { } active) Restore(active);
    }

    // ── Character snapshot (restore) ────────────────────────────────────────────

    /// <summary>One mod held in its recorded state by a temporary setting.</summary>
    internal readonly record struct TemporaryHold(PenumbraModSetting Recorded, int Priority);

    /// <summary>Every Penumbra write the first step of a character restore makes, in the order it makes them.</summary>
    internal sealed record RestorePlan(
        List<string> ClearTemporary,
        List<string> Disable,
        List<(string ModDirectory, string Group, List<string> Options)> SetOptions,
        List<(string ModDirectory, int Priority)> SetPriority,
        List<(string ModDirectory, bool Enabled)> SetEnabled,
        List<TemporaryHold> Hold,
        List<string> Missing,
        int PriorityOffset)
    {
        internal int WriteCount => ClearTemporary.Count + Disable.Count + SetOptions.Count + SetPriority.Count
                                 + SetEnabled.Count + Hold.Count;
    }

    /// <summary>
    /// Work out the first step of a character restore without touching anything. <paramref name="effective"/> must
    /// be read after Proteus's own holds were released.
    /// <list type="bullet">
    /// <item>Bound non-Proteus mods are held in their recorded state; enabled ones are raised together above every
    /// other enabled mod (<see cref="ComputePriorityOffset"/>).</item>
    /// <item>Bound Proteus mods get their recorded state written permanently, where it differs.</item>
    /// <item>Unbound Proteus mods are switched off, except <paramref name="heldProteus"/>; Glamourer's temporary
    /// settings are cleared from every Proteus mod written, or they would mask the write.</item>
    /// <item>Bound mods no longer installed are reported, not written.</item>
    /// </list>
    /// Unbound non-Proteus mods are left to the post-load sweep (<see cref="StrayDrawnMods"/>).
    /// </summary>
    internal static RestorePlan PlanRestore(
        IReadOnlyList<PenumbraModSetting> bound,
        IReadOnlySet<string> installed,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> permanent,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> effective,
        IReadOnlySet<string> proteusDirs,
        IReadOnlySet<string> heldProteus)
    {
        var boundDirs = bound.Select(m => m.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present   = bound.Where(m => installed.Contains(m.ModDirectory)).ToList();
        var missing   = bound.Where(m => !installed.Contains(m.ModDirectory)).Select(m => m.ModDirectory).ToList();
        var proteus   = present.Where(m => proteusDirs.Contains(m.ModDirectory)).ToList();
        var others    = present.Where(m => !proteusDirs.Contains(m.ModDirectory)).ToList();

        var disable = proteusDirs
            .Where(dir => !boundDirs.Contains(dir) && !heldProteus.Contains(dir) && (IsOn(permanent, dir) || IsOn(effective, dir)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var disabling = disable.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var clearTemporary = proteus.Select(m => m.ModDirectory).Concat(disable)
            .Where(dir => effective.TryGetValue(dir, out var s) && s.Temporary)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A disable needs a permanent write only if the permanent setting is on.
        var disableWrites = disable.Where(dir => IsOn(permanent, dir)).ToList();

        // ── Held mods, raised above everything else that stays on — but never to or past the Proteus output mod ──
        //
        // A held mod outranking the managed mod would override Proteus's own output, so its priority is a ceiling.
        int? ceiling = effective.TryGetValue(SidecarDiscoveryService.ManagedModDir, out var managed) && managed.Enabled
            ? managed.Priority : null;

        var rivals = effective
            .Where(kv => kv.Value.Enabled && !boundDirs.Contains(kv.Key) && !disabling.Contains(kv.Key)
                      && !string.Equals(kv.Key, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)
                      && (ceiling == null || kv.Value.Priority < ceiling))
            .Select(kv => kv.Value.Priority)
            .ToList();
        var offset = ComputePriorityOffset(others.Where(m => m.Enabled).Select(m => m.Priority).ToList(), rivals);

        // A mod the design keeps off draws nothing, so its priority isn't raised.
        var hold = others
            .Select(m => new TemporaryHold(m, m.Enabled ? HeldPriority(m.Priority, offset, ceiling) : m.Priority))
            .ToList();

        // ── Proteus mods, written permanently, diffed against the collection ──
        var setOptions  = new List<(string, string, List<string>)>();
        var setPriority = new List<(string, int)>();
        var setEnabled  = new List<(string, bool)>();
        foreach (var m in proteus)
        {
            permanent.TryGetValue(m.ModDirectory, out var cur);
            var curKnown = cur.Options != null;

            foreach (var (group, sel) in m.Options)
                if (!curKnown || !cur.Options!.TryGetValue(group, out var curSel) || !SameSelection(curSel, sel))
                    setOptions.Add((m.ModDirectory, group, sel));

            if (!curKnown || cur.Priority != m.Priority)
                setPriority.Add((m.ModDirectory, m.Priority));

            if ((curKnown && cur.Enabled) != m.Enabled)
                setEnabled.Add((m.ModDirectory, m.Enabled));
        }

        return new RestorePlan(clearTemporary, disableWrites, setOptions, setPriority, setEnabled, hold, missing, offset);
    }

    private static bool IsOn(IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> settings, string dir)
        => settings.TryGetValue(dir, out var s) && s.Enabled;

    private static bool SameSelection(List<string> a, List<string> b)
        => a.Count == b.Count && a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    /// <summary>
    /// How far every enabled held mod's priority must rise so the lowest sits strictly above the highest other
    /// enabled mod (at equal priority Penumbra's order is unstable). One offset for all keeps their relative order.
    /// </summary>
    /// <summary>
    /// A held mod's priority: recorded plus the shared offset, kept strictly below the Proteus output mod (the
    /// design's top mods may then tie each other there).
    /// </summary>
    internal static int HeldPriority(int recorded, int offset, int? ceiling)
    {
        var raised = (long)recorded + offset;
        if (ceiling is { } c) raised = Math.Min(raised, (long)c - 1);
        return (int)Math.Clamp(raised, int.MinValue, int.MaxValue);
    }

    internal static int ComputePriorityOffset(IReadOnlyList<int> bound, IReadOnlyList<int> rivals)
    {
        if (bound.Count == 0 || rivals.Count == 0) return 0;
        var offset = (long)rivals.Max() + 1 - bound.Min();
        return (int)Math.Clamp(offset, 0, int.MaxValue / 2);
    }

    /// <summary>
    /// Restore a whole-character binding in two steps: now, release the previous holds, hold this design's mods,
    /// write its Proteus mods and sweep unbound ones inside one suppression scope; then, after the redraw, the
    /// post-load sweep holds off anything still drawn from a mod the design lacks.
    /// </summary>
    /// <param name="writeProteusMods">False when the boot restore re-adopts the remembered design: re-hold what
    /// Penumbra forgot, but don't re-impose permanent settings the player may have changed.</param>
    private void RestoreCharacter(DesignBinding b, Guid designId, bool stripImported, bool writeProteusMods = true)
    {
        if (writeProteusMods) AdoptOverrides(b, designId, suppressEcho: true);
        characterWorkGeneration++;

        var collId = penumbra.GetPlayerCollectionId();
        var names  = penumbra.GetAllMods();
        if (collId == null || names == null)
        {
            log.Warning("[Proteus] design-restore: Penumbra state unreadable — overrides applied, mod settings left as they are.");
            compositor.TriggerRecomposite($"design-restore:{designId}");
            return;
        }

        var discovered  = discovery.DiscoverAll();
        var proteusDirs = discovered.Select(e => e.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boundDirs   = b.CharacterMods.Select(m => m.ModDirectory)
                              .Concat(b.Mods.Select(m => m.ModDirectory))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RestorePlan? plan = null;
        var held = 0;
        using (compositor.SuppressModSettingEvents())
        {
            heldTemporary.Clear();
            penumbra.RemoveAllTemporaryModSettings(collId.Value, TemporaryKey);

            var permanent = penumbra.GetCollectionModSettings(collId.Value, ignoreTemporary: true);
            var effective = penumbra.GetCollectionModSettings(collId.Value, ignoreTemporary: false);
            if (permanent != null && effective != null)
            {
                plan = PlanRestore(b.CharacterMods, names.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    permanent, effective, proteusDirs, UnboundContentMods(boundDirs, discovered, stripImported));

                if (writeProteusMods)
                {
                    foreach (var dir in plan.ClearTemporary)
                        penumbra.ClearTemporaryModSettings(collId.Value, dir);
                    foreach (var dir in plan.Disable)
                        penumbra.SetModEnabled(collId.Value, dir, false);
                    foreach (var (dir, group, options) in plan.SetOptions)
                        penumbra.SetModOption(collId.Value, dir, group, options);
                    foreach (var (dir, priority) in plan.SetPriority)
                        penumbra.SetModPriority(collId.Value, dir, priority);
                    foreach (var (dir, enabled) in plan.SetEnabled)
                        penumbra.SetModEnabled(collId.Value, dir, enabled);
                }

                foreach (var (recorded, priority) in plan.Hold)
                {
                    var ec = penumbra.SetTemporaryModSettings(collId.Value, recorded.ModDirectory, recorded.Enabled,
                        priority, recorded.Options, TemporarySource, TemporaryKey);
                    if (ec == PenumbraApiEc.Success)
                    {
                        heldTemporary[recorded.ModDirectory] = recorded;
                        held++;
                    }
                    else
                    {
                        // TemporarySettingDisallowed: another plugin holds this mod under its own lock.
                        log.Warning("[Proteus] design-restore: couldn't hold {0} ({1}) — it shows as the collection has it.",
                            recorded.ModDirectory, ec);
                    }
                }
            }
        }

        if (plan == null)
        {
            log.Warning("[Proteus] design-restore: mod settings unreadable — overrides applied, mods left as they are.");
            compositor.TriggerRecomposite($"design-restore:{designId}");
            return;
        }

        if (writeProteusMods)
            ArmTemporaryGuard(collId.Value,
                plan.ClearTemporary.Concat(b.CharacterMods.Select(m => m.ModDirectory).Where(proteusDirs.Contains)).Concat(plan.Disable));
        ArmPostLoadSweep(designId, collId.Value, boundDirs, proteusDirs);

        compositor.TriggerRecomposite($"design-restore:{designId}");
        log.Information(
            "[Proteus] design-restore: {0} — holding {1} mod(s) (offset +{2}); Proteus mods: {3} temporary cleared, {4} switched off, {5} option group(s), {6} priorit(ies), {7} enable change(s){8}.",
            b.DesignName ?? designId.ToString(), held, plan.PriorityOffset, plan.ClearTemporary.Count, plan.Disable.Count,
            plan.SetOptions.Count, plan.SetPriority.Count, plan.SetEnabled.Count,
            plan.Missing.Count > 0 ? $"; not installed: {string.Join(", ", plan.Missing)}" : "");
    }

    // ── Post-load sweep ─────────────────────────────────────────────────────────
    //
    // The second step of a character restore. A mod the design lacks can still show where the design has nothing
    // to outrank it, so for a while after a restore each redraw is followed by a look at the resource tree, and
    // any drawn mod the design doesn't have is held off temporarily.

    private const int PostLoadSweepWindowMs = 20000;
    private const int PostLoadSweepSettleMs = 1500;

    private long postLoadSweepUntil;
    private Guid postLoadSweepDesign;
    private Guid postLoadSweepCollection;
    private int postLoadSweepGeneration;
    private int postLoadSweepToken;
    private HashSet<string> postLoadSweepBound = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> postLoadSweepProteus = new(StringComparer.OrdinalIgnoreCase);

    private void ArmPostLoadSweep(Guid designId, Guid collId, HashSet<string> bound, HashSet<string> proteusDirs)
    {
        postLoadSweepDesign     = designId;
        postLoadSweepCollection = collId;
        postLoadSweepGeneration = characterWorkGeneration;
        postLoadSweepBound      = bound;
        postLoadSweepProteus    = proteusDirs;
        postLoadSweepUntil      = Environment.TickCount64 + PostLoadSweepWindowMs;
        // One sweep is queued regardless: neither the recomposite nor Penumbra is guaranteed to redraw.
        QueuePostLoadSweep();
    }

    private void OnLocalPlayerRedrawn()
    {
        if (Environment.TickCount64 < postLoadSweepUntil) QueuePostLoadSweep();
    }

    /// <summary>Sweep once the draw object has been quiet for <see cref="PostLoadSweepSettleMs"/>; a burst of
    /// redraws produces one sweep.</summary>
    private void QueuePostLoadSweep()
    {
        var token = ++postLoadSweepToken;
        framework.RunOnTick(() =>
        {
            if (token != postLoadSweepToken) return;
            RunPostLoadSweep();
        }, TimeSpan.FromMilliseconds(PostLoadSweepSettleMs));
    }

    private void RunPostLoadSweep()
    {
        if (Environment.TickCount64 >= postLoadSweepUntil) return;
        if (postLoadSweepGeneration != characterWorkGeneration || ActiveDesignId != postLoadSweepDesign) return;

        var modsRoot = penumbra.GetModDirectory();
        var tree     = penumbra.GetActivePlayerResourceMap();
        if (modsRoot == null || tree == null) return;

        var strays = StrayDrawnMods(modsRoot, tree, postLoadSweepBound, postLoadSweepProteus);
        if (strays.Count == 0) return;

        var collId = postLoadSweepCollection;
        var off    = new List<string>(strays.Count);
        using (compositor.SuppressModSettingEvents())
        {
            foreach (var dir in strays)
            {
                var ec = penumbra.SetTemporaryModSettings(collId, dir, enabled: false, priority: 0,
                    new Dictionary<string, List<string>>(), TemporarySource, TemporaryKey);
                if (ec != PenumbraApiEc.Success)
                {
                    log.Warning("[Proteus] design-restore: couldn't hold {0} off ({1}).", dir, ec);
                    continue;
                }
                heldTemporary[dir] = new PenumbraModSetting { ModDirectory = dir, Enabled = false };
                off.Add(dir);
            }
        }
        if (off.Count == 0) return;

        compositor.TriggerRecomposite($"design-restore-sweep:{postLoadSweepDesign}");
        log.Information("[Proteus] design-restore: holding off mod(s) the design doesn't have that were still drawn: {0}",
            string.Join(", ", off));
    }

    /// <summary>
    /// The mods a character draws from that a design doesn't have: every mods-folder owner of a drawn file, minus
    /// bound mods, the managed output mod and Proteus mods. Weapon-only files are ignored, since the weapon
    /// follows the job, not the design.
    /// </summary>
    internal static List<string> StrayDrawnMods(
        string modsRoot, IReadOnlyDictionary<string, HashSet<string>> tree,
        IReadOnlySet<string> bound, IReadOnlySet<string> proteusDirs)
    {
        var strays = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, gamePaths) in tree)
        {
            if (gamePaths.Count > 0 && gamePaths.All(p => p.StartsWith("chara/weapon/", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (OwningMod(modsRoot, file) is not { } owner) continue;
            if (bound.Contains(owner) || proteusDirs.Contains(owner)) continue;
            if (string.Equals(owner, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)) continue;
            strays.Add(owner);
        }
        return strays.ToList();
    }

    // ── Temporary-setting guard ─────────────────────────────────────────────────
    //
    // Glamourer re-asserts a design's associated mods as temporary settings, including after our own redraw.
    // For a short while after a restore those are cleared again, a bounded number of times so two plugins
    // can never fight forever.

    private const int TemporaryGuardMs       = 15000;
    private const int TemporaryGuardMaxClear = 3;

    private Guid temporaryGuardCollection;
    private long temporaryGuardUntil;
    private HashSet<string> temporaryGuardMods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> temporaryGuardClears = new(StringComparer.OrdinalIgnoreCase);

    private void ArmTemporaryGuard(Guid collId, IEnumerable<string> mods)
    {
        temporaryGuardCollection = collId;
        temporaryGuardMods       = mods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        temporaryGuardClears.Clear();
        Volatile.Write(ref temporaryGuardUntil, Environment.TickCount64 + TemporaryGuardMs);
    }

    private void OnPenumbraModSettingChanged(ModSettingChange change, Guid collId, string modDir, bool inherited)
    {
        if (change != ModSettingChange.TemporarySetting) return;
        if (Environment.TickCount64 >= Volatile.Read(ref temporaryGuardUntil)) return;

        // Next tick, never inline: editing the collection inside Penumbra's event dispatch would re-enter it.
        framework.RunOnTick(() =>
        {
            if (Environment.TickCount64 >= temporaryGuardUntil) return;
            if (collId != temporaryGuardCollection || !temporaryGuardMods.Contains(modDir)) return;

            var n = temporaryGuardClears.GetValueOrDefault(modDir);
            if (n >= TemporaryGuardMaxClear) return;

            // NothingChanged raises no further event, so this never feeds itself.
            var ec = penumbra.ClearTemporaryModSettings(collId, modDir);
            if (ec == PenumbraApiEc.Success)
            {
                temporaryGuardClears[modDir] = n + 1;
                log.Debug("[Proteus] design-restore: cleared a re-asserted temporary setting on {0} ({1}/{2}).",
                    modDir, n + 1, TemporaryGuardMaxClear);
            }
        }, delayTicks: 1);
    }

    private void DisarmTemporaryGuard() => Volatile.Write(ref temporaryGuardUntil, 0);

    /// <summary>Drop the active color override (revert to metadata colors) and recomposite.</summary>
    public void ClearColorOverride()
    {
        ClearOverrides();
        compositor.TriggerRecomposite("design-override-clear");
    }

    /// <summary>
    /// The applied design has no binding: drop the overrides so everything falls back to each mod's metadata.
    /// Deliberately does not disable Proteus mods (see <see cref="DisableAllProteusMods"/>).
    /// </summary>
    private void HandleUnboundDesign()
    {
        // Ambient (fires on every zone-in): ClearOverrides returns false on a re-assert that changed nothing.
        if (ClearOverrides())
            compositor.TriggerRecomposite("design-binding-unbound", force: false);
    }

    /// <summary>
    /// Disable every discovered Proteus overlay mod. UNUSED: the gear-match heuristic can't tell an unknown
    /// design from a missed match, and a false negative turns off the user's whole setup. Restore the call
    /// once an applied design reports its GUID, with an idempotency guard against repeated apply signals.
    /// </summary>
    private void DisableAllProteusMods()
    {
        // Pull the injected hosts first, or they show as real accessories once the mods are off.
        compositor.RemoveInjectedGlasses();
        compositor.RemoveInjectedRing();

        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null) return;
        foreach (var e in discovery.DiscoverAll())
            penumbra.SetModEnabled(collId.Value, e.ModDirectory, false);
    }

    // ── Live override editing (UI, framework thread) ────────────────────────────
    //
    // Thin delegations to the shared OverlayOverrideBag, which presets use too.

    /// <summary>True when a binding is active and supplies colors for this mod.</summary>
    public bool IsOverrideActiveFor(string modDir) => overrides.Governs(modDir);

    /// <inheritdoc cref="OverlayOverrideBag.PeekMaskRows"/>
    public List<ColorTableRowPreset>? PeekMaskRows(string modDir) => overrides.PeekMaskRows(modDir);

    /// <summary>Install the mask rows as this binding's live override (preview only; the stored binding is
    /// untouched until "Update binding"). False when no binding is active.</summary>
    public bool SetMaskRows(string modDir, List<ColorTableRowPreset> rows)
        => overrides.SetMaskRows(modDir, rows);

    /// <inheritdoc cref="OverlayOverrideBag.PeekRows"/>
    public List<ColorTableRowPreset>? PeekOverrideRows(string modDir, string? group, string? option)
        => overrides.PeekRows(modDir, group, option);

    /// <summary>Install rows as this binding's live override (preview only). False when no binding is active.</summary>
    public bool SetOverrideRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows)
        => overrides.SetRows(modDir, group, option, rows);

    /// <inheritdoc cref="OverlayOverrideBag.PeekContentMaterialRows"/>
    public List<ColorTableRowPreset>? PeekContentMaterialRows(string modDir, string materialRel)
        => overrides.PeekContentMaterialRows(modDir, materialRel);

    /// <inheritdoc cref="OverlayOverrideBag.SetContentMaterialRows"/>
    public bool SetContentMaterialRows(string modDir, string materialRel, List<ColorTableRowPreset> rows)
        => overrides.SetContentMaterialRows(modDir, materialRel, rows);

    /// <summary>
    /// The active binding's mod-wide tab order (<see cref="Configuration.ModStackEntry"/> keys, top-first), or null
    /// when no binding overrides it and the global stack config applies.
    /// </summary>
    public IReadOnlyList<string>? ActiveStackOrderFor(string modDir) => overrides.StackOrderFor(modDir);

    /// <summary>
    /// Record a mod-wide restack into the active binding's live stack override rather than the global config.
    /// False when no binding is active.
    /// </summary>
    public bool SetEditableStackOrder(string modDir, IEnumerable<(string Group, string Option)> topFirst)
        => overrides.SetStackOrder(modDir, topFirst);

    /// <inheritdoc cref="OverlayOverrideBag.GetEditableGear"/>
    public GearSettingsPreset? GetEditableGearOverride(
        string modDir, string? group, string? option, OverlayDescriptor seed)
        => overrides.GetEditableGear(modDir, group, option, seed);

    /// <inheritdoc cref="OverlayOverrideBag.GetEditableContentGear"/>
    public GearSettingsPreset? GetEditableContentGearOverride(
        string modDir, string? group, string? option, GearSettingsPreset seed)
        => overrides.GetEditableContentGear(modDir, group, option, seed);

    /// <inheritdoc cref="OverlayOverrideBag.PeekContentGear"/>
    public GearSettingsPreset? PeekContentGearOverride(string modDir, string? group, string? option)
        => overrides.PeekContentGear(modDir, group, option);

    /// <inheritdoc cref="OverlayOverrideBag.GetEditableContentMaterialGear"/>
    public GearSettingsPreset? GetEditableContentMaterialGearOverride(
        string modDir, string materialRel, GearSettingsPreset seed)
        => overrides.GetEditableContentMaterialGear(modDir, materialRel, seed);

    /// <inheritdoc cref="OverlayOverrideBag.PeekContentMaterialGear"/>
    public GearSettingsPreset? PeekContentMaterialGearOverride(string modDir, string materialRel)
        => overrides.PeekContentMaterialGear(modDir, materialRel);

    /// <inheritdoc cref="OverlayOverrideBag.PeekGear"/>
    /// <remarks>Overlays only; a content pack's glow reads <see cref="PeekContentGearOverride"/>
    /// (see <see cref="OverlayGearOverride.Content"/>).</remarks>
    public GearSettingsPreset? PeekGearOverride(string modDir, string group, string option)
        => overrides.PeekGear(modDir, group, option);

    /// <inheritdoc cref="OverlayOverrideBag.GetEditableMaskGear"/>
    public GearSettingsPreset? GetEditableMaskGearOverride(string modDir, OverlayDescriptor seed)
        => overrides.GetEditableMaskGear(modDir, seed);

    /// <summary>
    /// Drop one option's colour + gear override from the active design, both live and persisted, so "Reset to
    /// defaults" sticks on a bound mod. False when no design is active or nothing was stored.
    /// </summary>
    public bool ClearOptionOverride(string modDir, string? group, string? option)
    {
        Guid id;
        lock (gate)
        {
            if (activeDesignId is not { } active) return false;
            id = active;
        }

        // Outside `gate`: the bag has its own lock, and the two must never nest.
        bool touched = overrides.ClearOption(modDir, group, option);

        lock (gate)
        {
            // Persisted binding, so the design stops re-applying it.
            if (store.Bindings.TryGetValue(id, out var b))
            {
                var mod = b.Mods.FirstOrDefault(m =>
                    string.Equals(m.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
                if (mod != null)
                {
                    touched |= OverlayOverrideBag.ClearScope(mod.Colors.Options, group, option,
                        () => { bool had = mod.Colors.Top != null; mod.Colors.Top = null; return had; });
                    touched |= OverlayOverrideBag.ClearScope(mod.Gear.Options, group, option,
                        () => OverlayOverrideBag.ClearTopGear(mod.Gear));
                }
            }

            // Serialise under the lock, write outside it: the store reaches tens of MB and the caller is the UI thread.
            if (touched) SaveDeferred();
        }

        if (!touched) return false;

        log.Information("[Proteus] cleared binding override for {0} [{1}/{2}] from the active design",
            modDir, group ?? "(top)", option ?? "(top)");
        return true;
    }


    /// <summary>
    /// Re-snapshot the live Proteus state, unsaved editor tweaks included, into the active binding and persist it;
    /// the only path that folds UI edits into a binding. False if no binding is active. Framework thread.
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

        var characterMods = ReadCharacterMods(collId.Value);

        lock (gate)
        {
            if (activeDesignId != id) return false; // active binding changed underfoot
            store.Bindings[id] = new DesignBinding
            {
                DesignId      = id,
                DesignName    = name,
                CapturedUtc   = DateTime.UtcNow,
                Mods          = mods,
                CharacterMods = characterMods
                    ?? (store.Bindings.TryGetValue(id, out var previous) ? previous.CharacterMods : new()),
            };
            Save();
        }

        // All three, gear included: the capture reads the effective gear (possibly a preset's), so the bag must
        // be replaced or it keeps the design's older copy.
        overrides.Adopt(CloneOverrides(mods), CloneGear(mods), CloneStack(mods));

        // The binding now holds the effective state outright, presets included, so the pins drop.
        PresetsSuperseded?.Invoke();

        compositor.TriggerRecomposite($"design-binding-update:{id}");
        log.Information("[Proteus] Updated binding for design {0} from current state.", name ?? id.ToString());
        return true;
    }

    // ── Heuristic apply detection (framework thread) ────────────────────────────

    /// <summary>
    /// Whether a finished Glamourer operation should re-evaluate which design is applied. Uses
    /// <see cref="StateFinalizationType"/> because <see cref="StateChangeType"/> can't tell a gearset change from a
    /// revert, and fires once per operation. Plain <c>Reapply</c> is excluded (it is our own echo, commands and
    /// auto-redraw), as are <c>Gearset</c> (see <see cref="IsInferredAutomationApply"/>) and every <c>Revert*</c>.
    /// </summary>
    internal static bool IsApplySignal(StateFinalizationType type)
        => type is StateFinalizationType.DesignApplied
                or StateFinalizationType.ReapplyAutomation;

    /// <summary>
    /// The player reverted to their game state, so the active override must be dropped. <c>RevertAutomation</c> is
    /// excluded: a Reapply restoring the correct design always follows it.
    /// </summary>
    internal static bool IsRevertSignal(StateFinalizationType type)
        => type is StateFinalizationType.Revert
                or StateFinalizationType.RevertCustomize
                or StateFinalizationType.RevertEquipment
                or StateFinalizationType.RevertAdvanced;

    /// <summary>
    /// Whether a <c>Gearset</c> finalization is really automation having just applied a design, which raises no
    /// apply signal of its own. The signature is a Reapply we did not cause followed by a Gearset within
    /// <see cref="AutomationPairWindowMs"/>; <paramref name="isOwnRedrawEcho"/> excludes our own redraw's tail
    /// (<see cref="CompositorService.ConsumeOwnRedrawEcho"/>). <paramref name="msSinceForeignReapply"/> is
    /// <see cref="long.MaxValue"/> when there has never been one. Callers must treat it as restore-only
    /// (see <see cref="EvaluateAppliedDesign"/>).
    /// </summary>
    internal static bool IsInferredAutomationApply(StateFinalizationType type,
                                                   long msSinceForeignReapply, bool isOwnRedrawEcho)
        => type is StateFinalizationType.Gearset
        && !isOwnRedrawEcho
        && msSinceForeignReapply < AutomationPairWindowMs;

    // ── The state from before the current apply ─────────────────────────────────
    //
    // Refreshed a tick after anything changes the character, so when a finalization arrives it still holds the
    // state from before that operation. Read by AppliedInsteadOfActive. Framework thread only; null until the first refresh.

    private JObject? preApplyState;
    private int preApplyRefreshQueued;

    private void OnGlamourerStateChangedAny(StateChangeType _) => QueuePreApplyRefresh();

    private void QueuePreApplyRefresh()
    {
        if (Interlocked.Exchange(ref preApplyRefreshQueued, 1) == 1) return;
        framework.RunOnTick(() =>
        {
            Volatile.Write(ref preApplyRefreshQueued, 0);
            preApplyState = glamourer.GetObjectState(0);
        }, delayTicks: 1);
    }

    private void OnGlamourerStateFinalized(StateFinalizationType type)
    {
        // Read before anything below changes the character, and refreshed after whatever this operation did.
        var preApply = preApplyState;
        try { HandleStateFinalized(type, preApply); }
        finally { QueuePreApplyRefresh(); }
    }

    private void HandleStateFinalized(StateFinalizationType type, JObject? preApply)
    {
        // A revert leaves no design applied: drop the override so colours fall back to metadata.
        if (IsRevertSignal(type))
        {
            if (Environment.TickCount64 >= suppressUntilTick) HandleUnboundDesign();
            return;
        }

        // Consume the own-redraw expectation for every Gearset, before any guard, so a dropped Gearset can't
        // leave it armed to swallow a genuine change later.
        var ownRedrawEcho = type is StateFinalizationType.Gearset
                         && compositor.ConsumeOwnRedrawEcho(OwnRedrawEchoMs);

        if (Environment.TickCount64 < suppressUntilTick) return; // our own restore echo

        // Feature disabled → never restore, and drop any override left active so off means fully off.
        if (!config.DesignBindingEnabled)
        {
            if (IsApplySignal(type) && activeDesignId != null) ClearColorOverride();
            return;
        }

        // Automation-applied designs must be inferred, and an inferred signal may restore but never clear:
        // a wrong clear silently drops the player's colours.
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

        // Only an explicit design apply may strip slots: automation merges several designs.
        EvaluateAppliedDesign(allowUnbind: !inferred,
            unequipUnset: type is StateFinalizationType.DesignApplied && config.DesignBindingUnequipUnsetSlots,
            preApply: type is StateFinalizationType.DesignApplied ? preApply : null);
    }

    // ── Unequip what the design leaves unset ────────────────────────────────────

    /// <summary>The gear slots a design can leave unset, by their Glamourer design names. Weapons are not
    /// here: a character always holds its job's weapon, and "Nothing" is not a valid main hand.</summary>
    internal static readonly string[] UnequippableSlots =
        ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    internal const string GlassesSlot = "Glasses";

    // Glamourer's "Nothing" item for an equipment slot is uint.MaxValue - 128 - slot index (ItemManager.NothingId);
    // matched as a window so the slot-index mapping isn't copied here.
    private const ulong NothingIdHigh = uint.MaxValue - 128UL;
    private const ulong NothingIdLow  = NothingIdHigh - 31UL;

    /// <summary>
    /// The slots to unequip so the character wears only what <paramref name="design"/> puts on it: every filled slot
    /// the design does not apply and Proteus isn't borrowing. Takes the design unstripped; carrier slots are judged
    /// by the live <paramref name="carriers"/>.
    /// </summary>
    internal static List<string> UnsetSlots(JObject design, JObject state, Carriers carriers)
    {
        var result = new List<string>();
        var dEquip = design["Equipment"] as JObject;
        var sEquip = state["Equipment"] as JObject;

        if (sEquip != null)
            foreach (var slot in UnequippableSlots)
            {
                if (dEquip?[slot] is JObject d && d["Apply"]?.ToObject<bool>() == true) continue;
                if (sEquip[slot] is not JObject s || s["ItemId"] is not { } idToken) continue;
                var id = idToken.ToObject<ulong>();
                if (id == 0 || id is >= NothingIdLow and <= NothingIdHigh) continue;       // already empty
                if (carriers.OwnedSlots?.Contains(slot, StringComparer.Ordinal) == true) continue;
                if (carriers.AccessoryItems?.Contains(id) == true) continue;                 // our carrier item
                result.Add(slot);
            }

        if (state["Bonus"]?[GlassesSlot] is JObject sGlasses
            && !(design["Bonus"]?[GlassesSlot] is JObject dGlasses && dGlasses["Apply"]?.ToObject<bool>() == true)
            && sGlasses["BonusId"] is { } bonusToken)
        {
            var row = bonusToken.ToObject<ulong>() & BonusIdRowMask;
            var ours = carriers.GlassesSlotOwned || (carriers.GlassesRow is { } g && row == g);
            if (row != 0 && !ours)
                result.Add(GlassesSlot);
        }

        return result;
    }

    /// <summary>
    /// Switch off the imported Proteus packs a binding didn't capture, for a re-apply of the already-active design
    /// (the counterpart of <see cref="Restore"/>'s <c>stripImported</c>).
    /// </summary>
    private void DisableUnboundImportedPacks(Guid designId)
    {
        DesignBinding? b;
        lock (gate) store.Bindings.TryGetValue(designId, out b);
        if (b == null || penumbra.GetPlayerCollectionId() is not { } collId) return;

        var bound = b.CharacterMods.Select(m => m.ModDirectory)
            .Concat(b.Mods.Select(m => m.ModDirectory))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packs = discovery.DiscoverAll()
            .Where(e => e.Enabled && e.Metadata.HasContent && !bound.Contains(e.ModDirectory))
            .Select(e => e.ModDirectory)
            .ToList();
        if (packs.Count == 0) return;

        lock (gate) suppressUntilTick = Environment.TickCount64 + RestoreSuppressMs;
        using (compositor.SuppressModSettingEvents())
        {
            foreach (var dir in packs)
            {
                // A temporary setting would keep it on over the permanent write; NothingChanged when there is none.
                penumbra.ClearTemporaryModSettings(collId, dir);
                penumbra.SetModEnabled(collId, dir, false);
            }
        }

        compositor.TriggerRecomposite($"design-unbound-packs:{designId}");
        log.Information("[Proteus] design-binding: switched off imported pack(s) the design didn't capture: {0}",
            string.Join(", ", packs));
    }

    /// <summary>
    /// Unequip what the applied design leaves unset. Deferred a tick: editing Glamourer's state inside its own
    /// finalization event would re-enter the apply.
    /// </summary>
    private void UnequipUnsetSlots(Guid designId, Carriers carriers)
    {
        if (GetDesignCached(designId) is not { } design) return;

        framework.RunOnTick(() =>
        {
            if (glamourer.GetObjectState(0) is not { } state) return;
            var slots = UnsetSlots(design, state, carriers);
            if (slots.Count == 0) return;

            var done = new List<string>(slots.Count);
            foreach (var slot in slots)
            {
                var ok = slot == GlassesSlot ? glamourer.SetGlasses(0) : glamourer.UnequipSlot(slot);
                if (ok) done.Add(slot);
            }
            log.Information("[Proteus] design-binding: unequipped slot(s) the design leaves unset: {0}{1}",
                string.Join(", ", done),
                done.Count < slots.Count ? $" (failed: {string.Join(", ", slots.Except(done))})" : "");
        }, delayTicks: 1);
    }

    /// <summary>An elapsed-ms reading for the log, printing "never" for the long.MaxValue sentinel.</summary>
    private static string Elapsed(long ms)
        => ms == long.MaxValue ? "never" : $"{ms}ms ago";

    /// <summary>
    /// Match the player's current state against every binding and restore the best one.
    /// <paramref name="allowUnbind"/> is false for an inferred signal, where a failed match clears nothing.
    /// </summary>
    /// <param name="unequipUnset">Also clear the gear slots the matched design leaves unset. Explicit applies only.</param>
    /// <param name="preApply">The state from before this apply, for explicit applies only — see
    /// <see cref="AppliedInsteadOfActive"/>.</param>
    private void EvaluateAppliedDesign(bool allowUnbind, bool unequipUnset = false, JObject? preApply = null)
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

        // Judge each design on the player's own choices: retire the slots Proteus is borrowing, or nothing would match.
        var carriers = LiveCarriers();
        var pick = MatchBinding(state, carriers, preApply);

        if (pick == null)
        {
            if (!allowUnbind)
            {
                // Inferred signal: no match is an ordinary outcome, so touch nothing.
                log.Information("[Proteus] design-binding: no binding matched the inferred automation apply — leaving overrides as they are.");
                return;
            }

            // No binding matched: overrides are dropped. Reasons go to Debug for a capped set of candidates.
            log.Warning("[Proteus] design-binding: NO binding matched the applied state — dropping colour/gear overrides (dyes revert to metadata white).");
            ReportNoMatch(state, carriers);
            HandleUnboundDesign();
            return;
        }

        // Before the already-applied return: re-applying the same design should still take hand-added pieces off.
        if (unequipUnset) UnequipUnsetSlots(pick.Value, carriers);

        if (activeDesignId == pick)
        {
            // Already applied, so no restore runs, but packs switched on since still have to come off.
            if (unequipUnset) DisableUnboundImportedPacks(pick.Value);
            return;
        }
        Restore(pick.Value, stripImported: unequipUnset);
    }

    /// <summary>
    /// The binding that best matches the player's live state, or null. Ambiguous matches prefer the most specific
    /// design (most applied fields), then the most recently captured. Shared by the live apply path and the boot
    /// restore so both resolve a look identically.
    /// </summary>
    /// <param name="carriers">What Proteus has on the player, retired from each design before comparing — see
    /// <see cref="StripCarriers"/> and <see cref="BestMatches"/>.</param>
    /// <param name="preApply">The state from just before this apply; null at boot. See <see cref="AppliedInsteadOfActive"/>.</param>
    private Guid? MatchBinding(JObject state, Carriers carriers, JObject? preApply = null)
    {
        Guid[] candidateIds;
        lock (gate) candidateIds = store.Bindings.Keys.ToArray();

        var candidates = new List<(Guid Id, JObject Design)>(candidateIds.Length);
        foreach (var id in candidateIds)
            if (GetDesignCached(id) is { } design)
                candidates.Add((id, design));

        var top = BestMatches(candidates, state, carriers);
        if (top.Count == 0) return null;

        Guid pick;
        lock (gate) pick = top.Count == 1 ? top[0] : PickMostRecent(top, store.Bindings);

        if (preApply != null && ActiveDesignId == pick)
        {
            var instead = AppliedInsteadOfActive(pick, candidates, preApply, state, carriers);
            if (instead.Count > 0)
            {
                Guid other;
                lock (gate) other = instead.Count == 1 ? instead[0] : PickMostRecent(instead, store.Bindings);
                log.Information("[Proteus] design-binding: {0} matches only because its extra fields were left over from it — "
                              + "taking {1}, which sets a subset of it and explains everything that changed.", pick, other);
                return other;
            }
        }
        return pick;
    }

    /// <summary>
    /// The designs really applied when the best match is the design already active: a design leaves unset fields
    /// at their previous values, so the active superset can outrank what was applied. A candidate qualifies when
    /// it matches, sets a strict subset of the active design's fields, and none of the extra fields changed during
    /// this apply. Returns those at the highest specificity; empty when none.
    /// </summary>
    internal static List<Guid> AppliedInsteadOfActive(
        Guid active, IReadOnlyList<(Guid Id, JObject Design)> candidates, JObject preApply, JObject state, Carriers carriers)
    {
        var activeDesign = candidates.FirstOrDefault(c => c.Id == active).Design;
        if (activeDesign == null) return [];
        var activeFields = AppliedFieldKeys(StripCarriers(activeDesign, carriers));

        var found = new List<(Guid Id, int Count)>();
        foreach (var (id, design) in candidates)
        {
            if (id == active) continue;
            var stripped = StripCarriers(design, carriers);
            if (!StateMatches(stripped, state, out _)) continue;

            var fields = AppliedFieldKeys(stripped);
            if (!fields.IsProperSubsetOf(activeFields)) continue;

            var extraChanged = activeFields.Except(fields).Any(k => FieldChanged(preApply, state, k));
            if (!extraChanged) found.Add((id, fields.Count));
        }

        if (found.Count == 0) return [];
        var best = found.Max(f => f.Count);
        return found.Where(f => f.Count == best).Select(f => f.Id).ToList();
    }

    /// <summary>
    /// The fields a design applies, as <c>Container/Name/Part</c> keys, under the same rules
    /// <see cref="StateMatches"/> compares.
    /// </summary>
    internal static HashSet<string> AppliedFieldKeys(JObject design)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (design["Equipment"] is JObject equip)
            foreach (var p in equip.Properties())
            {
                if (NonMatchedSlots.Contains(p.Name) || p.Value is not JObject slot) continue;
                if (slot["ItemId"] != null && slot["Apply"]?.ToObject<bool>() == true) keys.Add($"Equipment/{p.Name}/Item");
                if (slot["ApplyStain"]?.ToObject<bool>() == true)                        keys.Add($"Equipment/{p.Name}/Stain");
            }

        if (design["Bonus"] is JObject bonus)
            foreach (var p in bonus.Properties())
                if (p.Value is JObject b && b["Apply"]?.ToObject<bool>() == true)
                    keys.Add($"Bonus/{p.Name}/Item");

        if (design["Customize"] is JObject cust)
            foreach (var p in cust.Properties())
                if (p.Name != "Wetness" && p.Value is JObject c && c["Apply"]?.ToObject<bool>() == true && c["Value"] != null)
                    keys.Add($"Customize/{p.Name}/Value");

        if (design["Parameters"] is JObject pars)
            foreach (var p in pars.Properties())
                if (p.Value is JObject c && c["Apply"]?.ToObject<bool>() == true)
                    keys.Add($"Parameters/{p.Name}/Value");

        return keys;
    }

    /// <summary>Whether the field an <see cref="AppliedFieldKeys"/> key names differs between two states.</summary>
    internal static bool FieldChanged(JObject before, JObject after, string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 3) return true;
        var a = before[parts[0]]?[parts[1]] as JObject;
        var b = after[parts[0]]?[parts[1]] as JObject;
        if (a == null || b == null) return a != b;

        return parts[0] switch
        {
            "Equipment" when parts[2] == "Item"  => !JToken.DeepEquals(a["ItemId"], b["ItemId"]),
            "Equipment"                          => !JToken.DeepEquals(a["Stain"], b["Stain"]) || !JToken.DeepEquals(a["Stain2"], b["Stain2"]),
            "Bonus"                              => !JToken.DeepEquals(a["BonusId"], b["BonusId"]),
            "Customize"                          => !JToken.DeepEquals(a["Value"], b["Value"]),
            _                                    => !ParameterEquals(a, b),
        };
    }

    /// <summary>
    /// The candidates that match best (a list: the caller breaks ties on recency); empty when none. Two passes:
    /// STRICT retires only slots a design captured from us by carrier item id; LOOSE also retires every slot we are
    /// borrowing, and runs only when strict finds nothing, since it drops a real criterion and a wrong pick makes
    /// <see cref="Restore"/> write Penumbra settings that outlive the apply.
    /// </summary>
    internal static List<Guid> BestMatches(
        IReadOnlyList<(Guid Id, JObject Design)> candidates, JObject state, Carriers carriers)
    {
        var top = Pass(carriers.ItemsOnly);
        // Skip the second pass when no slots are borrowed: it would be the same comparison.
        if (top.Count == 0 && carriers.RetiresSlots)
            top = Pass(carriers);
        return top;

        List<Guid> Pass(Carriers c)
        {
            var matches = new List<(Guid id, int specificity)>();
            foreach (var (id, design) in candidates)
                if (StateMatches(StripCarriers(design, c), state, out var spec))
                    matches.Add((id, spec));

            if (matches.Count == 0) return [];
            var best = matches.Max(m => m.specificity);
            return matches.Where(m => m.specificity == best).Select(m => m.id).ToList();
        }
    }

    // How many bindings a failed match explains itself against, at Debug.
    private const int NoMatchReportCandidates = 4;

    /// <summary>
    /// Log at Debug why the likeliest candidates were rejected: the previously active design, then the most recently
    /// captured bindings. Uses the same comparison as <see cref="MatchBinding"/>.
    /// </summary>
    private void ReportNoMatch(JObject state, Carriers carriers)
    {
        List<Guid> candidates;
        lock (gate)
        {
            candidates = store.Bindings.OrderByDescending(kv => kv.Value.CapturedUtc)
                              .Select(kv => kv.Key).Take(NoMatchReportCandidates).ToList();
            if (config.LastActiveDesignId is { } last && store.Bindings.ContainsKey(last))
            {
                candidates.Remove(last);
                candidates.Insert(0, last);
            }
        }

        foreach (var id in candidates.Take(NoMatchReportCandidates))
        {
            string? name;
            lock (gate) name = store.Bindings.TryGetValue(id, out var b) ? b.DesignName : null;
            name ??= id.ToString();

            if (GetDesignCached(id) is not { } design)
            {
                log.Debug("[Proteus] design-binding: {0} rejected — gone from Glamourer.", name);
                continue;
            }

            string? why = null;
            StateMatches(StripCarriers(design, carriers), state, out _, r => why ??= r);
            log.Debug("[Proteus] design-binding: {0} rejected — {1}", name, why ?? "(no reason reported)");
        }
    }

    // ── Boot restore (framework thread) ─────────────────────────────────────────

    /// <summary>Whether there is anything for a boot restore to find, and a Glamourer to verify it against. Not
    /// only when a design was active: the last session may have ended on a revert.</summary>
    private bool ShouldArmBootRestore()
    {
        if (!config.DesignBindingEnabled || !glamourer.IsAvailable) return false;
        lock (gate) return config.LastActiveDesignId != null || store.Bindings.Count > 0;
    }

    // The Logout event, not a missing local player: that also happens on every zone change.
    private void OnLogout(int type, int code) => framework.RunOnFrameworkThread(RearmForNextLogin);

    /// <summary>
    /// Arm the boot restore again for the next login, which may be another character or the same one dressed by an
    /// automation: either way Glamourer signals nothing. The active design is suspended rather than cleared (see
    /// <see cref="suspendedDesignId"/>) and stays persisted, so the login verifies it as step 1 like any boot.
    /// </summary>
    private void RearmForNextLogin()
    {
        if (!ShouldArmBootRestore()) return;

        lock (gate)
        {
            if (activeDesignId != null) suspendedDesignId = activeDesignId;
            activeDesignId = null;
        }

        // Work queued for the character that just left.
        DisarmTemporaryGuard();
        characterWorkGeneration++;

        bootDeadlineTick  = 0;
        bootStep1Reported = false;
        bootAtLogin       = true;
        bootAwaitingDeparture     = true;
        bootDepartureDeadlineTick = Environment.TickCount64 + BootRestoreTimeoutMs;
        compositor.BootCompositeHold = true;
        if (Interlocked.Exchange(ref bootRestoreDone, 0) == 1)
            framework.Update += OnBootRestoreTick;
        log.Information("[Proteus] design-binding: logged out — boot restore armed for the next login; boot composite held.");
    }

    /// <summary>
    /// Make the remembered design active. After a relogin its overrides are still published, so it is only marked
    /// active again; on a fresh boot they are adopted from the stored binding.
    /// </summary>
    private void AdoptRemembered(DesignBinding b, Guid designId)
    {
        bool stillPublished;
        lock (gate)
        {
            stillPublished    = suspendedDesignId == designId;
            suspendedDesignId = null;
            if (stillPublished) activeDesignId = designId;
        }
        if (!stillPublished) AdoptOverrides(b, designId, suppressEcho: false);
    }

    /// <summary>
    /// Waits for the local player and a readable Glamourer and Penumbra, then resolves the boot restore, once per
    /// arming. A poll because a plugin reload while logged in fires no login event.
    /// </summary>
    private void OnBootRestoreTick(IFramework fw)
    {
        if (Volatile.Read(ref bootRestoreDone) == 1) return;

        var now = Environment.TickCount64;
        if (unchecked(now - lastBootRestorePollTick) < BootRestorePollMs) return;
        lastBootRestorePollTick = now;

        // No player, nothing to verify against. The deadline is not started here: the plugin can sit at the title screen.
        if ((Plugin.ObjectTable.LocalPlayer?.Address ?? 0) == 0)
        {
            bootAtLogin = true;
            // The departure a re-arm waits for; and whoever arrives next gets a deadline and settle window of their own.
            bootAwaitingDeparture = false;
            bootDeadlineTick      = 0;
            return;
        }

        // Logout fires while the leaving character is still drawn, still wearing the suspended design: judging it
        // would resolve the poll before the login it was armed for. Bounded, since a logout can fail and the hold
        // would otherwise never release.
        if (bootAwaitingDeparture)
        {
            if (now < bootDepartureDeadlineTick) return;
            bootAwaitingDeparture = false;
            log.Information("[Proteus] design-binding: the character never left after the logout signal — resolving against it.");
        }

        if (bootDeadlineTick == 0)
        {
            bootDeadlineTick    = now + BootRestoreTimeoutMs;
            bootSettleUntilTick = now + BootSettleMs;
        }

        // A real Glamourer apply beat us to it, and wins outright over a reconstruction.
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

        // Penumbra can answer later than Glamourer when everything loads mid-session, and a restore that can't
        // read it marks the design active with nothing written, which no later re-apply repairs.
        if (penumbra.GetPlayerCollectionId() == null || penumbra.GetModDirectory() == null)
        {
            if (now >= bootDeadlineTick)
                FinishBootRestore($"Penumbra unreadable after {BootRestoreTimeoutMs / 1000}s — abstaining");
            return;
        }

        // The state is readable well before it is settled, so an early mismatch means "not yet": retry until the
        // settle window lapses.
        var final = now >= bootSettleUntilTick;
        TryBootRestore(state, final);
    }

    /// <summary>
    /// Pick the binding the character is already wearing. The remembered design is adopted with no Penumbra
    /// writes: never clears an override, disables a mod or re-asserts settings, which would undo changes made
    /// while Proteus was unloaded. Any other match is a look Proteus never restored, so it is restored in full
    /// where <see cref="BootMatchMayRestore"/> allows.
    /// </summary>
    /// <param name="final">False while the settle window is open: a non-match is retried rather than resolved.</param>
    private void TryBootRestore(JObject state, bool final)
    {
        // Only the design is stripped, never the state: StateMatches reads a state slot only where the design carries one.
        var carriers = BootCarriers();

        // 1. The design we were on when we unloaded, VERIFIED against the live state.
        if (config.LastActiveDesignId is { } lastId)
        {
            DesignBinding? b;
            lock (gate) store.Bindings.TryGetValue(lastId, out b);

            // Missing binding and deleted design are deterministic, so they fall through to step 2; reported
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
                design = StripCarriers(design, carriers);
                string? why = null;
                if (BootIdStillApplies(design, state, r => why ??= r))
                {
                    AdoptRemembered(b, lastId);
                    FinishBootRestore($"adopted last active design {b.DesignName ?? lastId.ToString()} ({b.Mods.Count} mods)");
                    Rehold(b, lastId);
                    return;
                }

                // The mismatch reason is the whole diagnosis here, so it is logged.
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

        // 2. The same match the live apply path runs (the store is not per-character). Its restore writes Penumbra
        // settings, so it waits for the settled state even when step 1 fell through at once.
        if (!final) return;
        if (MatchBinding(state, carriers) is not { } pick)
        {
            FinishBootRestore("no binding matched the character — leaving overrides unset");
            return;
        }

        DesignBinding? picked;
        lock (gate) store.Bindings.TryGetValue(pick, out picked);
        if (picked == null) { FinishBootRestore("matched binding vanished underfoot"); return; }

        if (!BootMatchMayRestore(bootAtLogin, config.DesignBindingFollowsAutomation))
        {
            FinishBootRestore($"{picked.DesignName ?? pick.ToString()} matched at login, but following Glamourer's "
                            + "automation is off — leaving Proteus as it is");
            return;
        }

        // Never the remembered design (step 1 just rejected it): an automation applied this look at login, or the
        // design changed while Proteus was unloaded. The Proteus mods still carry the previous look's Penumbra
        // settings, so adopting alone would put this look's colours on that look's mods. Restore in full, as the
        // live apply path would have. Before FinishBootRestore, which releases the holds when nothing is active.
        Restore(pick);
        FinishBootRestore($"restored matched design {picked.DesignName ?? pick.ToString()}");
    }

    /// <summary>
    /// Whether a boot match other than the remembered design may be restored, which writes Penumbra settings. At a
    /// login that look is the automation's (or the character's own gear), so it answers to
    /// <see cref="Configuration.DesignBindingFollowsAutomation"/> as the live path's inferred apply does. On a plugin
    /// reload it is a design applied while Proteus was unloaded, and is always restored.
    /// </summary>
    internal static bool BootMatchMayRestore(bool atLogin, bool followsAutomation)
        => !atLogin || followsAutomation;

    /// <summary>
    /// Hold the adopted design's mods again, since Penumbra forgets temporary settings on restart. Holds and sweep
    /// only; the Proteus mods' permanent settings are left as the player has them.
    /// </summary>
    private void Rehold(DesignBinding b, Guid designId)
    {
        if (config.DesignBindingRestoresCharacterMods && b.HasCharacterSnapshot)
            RestoreCharacter(b, designId, stripImported: false, writeProteusMods: false);
    }

    /// <summary>Log a step-1 fall-through reason the first time only.</summary>
    private void ReportStep1Once(string reason)
    {
        if (bootStep1Reported) return;
        bootStep1Reported = true;
        log.Information("[Proteus] boot restore: {0} — trying every binding.", reason);
    }

    /// <summary>Whether the remembered design still exists and still matches the live state. The design must
    /// already have had its carrier slots retired — see <see cref="StripCarriers"/>.</summary>
    /// <param name="onMismatch">Reports the first field that differed, for the boot log.</param>
    internal static bool BootIdStillApplies(JObject? design, JObject state,
                                            Action<string>? onMismatch = null)
    {
        if (design == null) return false;
        return StateMatches(design, state, out _, onMismatch);
    }

    /// <summary>One-shot: unhook the poll and release the boot composite. Every exit path routes through here.</summary>
    private void FinishBootRestore(string outcome)
    {
        if (Interlocked.Exchange(ref bootRestoreDone, 1) == 1) return;
        framework.Update -= OnBootRestoreTick;
        compositor.BootCompositeHold = false;
        log.Information("[Proteus] design-binding boot restore: {0} — boot composite released.", outcome);

        bool adopted, stale;
        lock (gate)
        {
            adopted = activeDesignId != null;
            stale   = !adopted && suspendedDesignId != null;
            suspendedDesignId = null;
        }
        if (adopted) return;

        // Nothing adopted: release whatever a previous instance was holding (by key, so nothing else is touched).
        ReleaseHeldMods();
        // A relogin that adopted nothing: the look from before the logout is still published, and this character
        // isn't wearing it. The boot composite released above picks the change up.
        if (stale) overrides.Clear();
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

    // Weapon slots are excluded from the match: the drawn weapon changes with job, independently of the outfit.
    private static readonly HashSet<string> NonMatchedSlots =
        new(StringComparer.OrdinalIgnoreCase) { "MainHand", "OffHand" };

    // A design matches when every field it applies equals the player's state (equipment, dyes, bonus items,
    // customize, parameters); weapons and wetness are excluded. `specificity` counts matched applied fields.
    /// <remarks>
    /// The caller must first retire Proteus's carriers from the design — see <see cref="StripCarriers"/>.
    /// </remarks>
    internal static bool StateMatches(JObject design, JObject state, out int specificity)
        => StateMatches(design, state, out specificity, null);

    // onMismatch: when non-null, called with the first failing field.
    internal static bool StateMatches(JObject design, JObject state, out int specificity, Action<string>? onMismatch)
    {
        specificity = 0;
        // A plain string, not Func<string>: capturing the foreach variable would allocate a display class every iteration.
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

        // Customize, per-index objects only (the base64 "Array" form contributes nothing). Wetness is situational.
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

    // Glamourer packs a bonus item as (type << 48) | row id, so the sheet row lives in the low 48 bits.
    private const ulong BonusIdRowMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>The Glamourer equipment-slot names a carrier can ride, derived from
    /// <see cref="InvisibleRing.CarrierSlots"/> so the two never drift.</summary>
    private static readonly string[] CarrierSlotNames =
        InvisibleRing.CarrierSlots.Select(c => c.EqdpSlot).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// What Proteus currently has on the player, in the two forms design matching needs.
    /// </summary>
    /// <param name="GlassesRow">The Glasses SHEET ROW of the invisible facewear host (not the packed
    /// BonusId a design stores — <see cref="StripCarriers"/> masks before comparing).</param>
    /// <param name="AccessoryItems">Item ids of the invisible accessories that can host a shell (separate items
    /// sharing a model set).</param>
    /// <param name="OwnedSlots">Glamourer slot names we are currently borrowing. Empty where ownership is unknowable,
    /// falling back to id-matching alone.</param>
    /// <param name="GlassesSlotOwned">Whether the Glasses slot is one we are currently borrowing.</param>
    internal readonly record struct Carriers(
        ulong? GlassesRow,
        IReadOnlyList<ulong>? AccessoryItems,
        IReadOnlyList<string>? OwnedSlots = null,
        bool GlassesSlotOwned = false)
    {
        /// <summary>Whether this retires whole slots beyond the carrier items; when not, the two passes in
        /// <see cref="BestMatches"/> are the same comparison.</summary>
        internal bool RetiresSlots => GlassesSlotOwned || OwnedSlots is { Count: > 0 };

        /// <summary>This, with slot retirement dropped: the strict pass's view.</summary>
        internal Carriers ItemsOnly => new(GlassesRow, AccessoryItems);
    }

    /// <summary>
    /// Retire the slots Proteus owns from a design, so it is never judged on a slot the player didn't choose: by
    /// item (the design captured a carrier) or by slot (we have since borrowed the slot). Returns the input
    /// unchanged when there is nothing to retire. DESIGNS ONLY — stripping a state turns a carried slot into
    /// "state has no item".
    /// </summary>
    internal static JObject StripCarriers(JObject design, Carriers carriers)
    {
        List<(string Container, string Slot)>? drop = null;

        if (design["Bonus"] is JObject bonus)
            foreach (var p in bonus.Properties())
            {
                var ours = carriers.GlassesRow is { } g && p.Value is JObject s && s["BonusId"] is { } id
                        && (id.ToObject<ulong>() & BonusIdRowMask) == g;
                if (ours || (carriers.GlassesSlotOwned && p.Name == "Glasses"))
                    (drop ??= []).Add(("Bonus", p.Name));
            }

        if (design["Equipment"] is JObject equip)
            foreach (var accessorySlot in CarrierSlotNames)
            {
                if (equip[accessorySlot] is not JObject s) continue;
                var ours = carriers.AccessoryItems is { Count: > 0 } items && s["ItemId"] is { } iid
                        && items.Contains(iid.ToObject<ulong>());
                if (ours || carriers.OwnedSlots?.Contains(accessorySlot, StringComparer.Ordinal) == true)
                    (drop ??= []).Add(("Equipment", accessorySlot));
            }

        if (drop == null) return design;

        var copy = (JObject)design.DeepClone();
        foreach (var (container, slot) in drop)
            (copy[container] as JObject)?.Remove(slot);
        return copy;
    }

    /// <summary>What Proteus has on the player right now, for the live apply path, straight from the compositor
    /// (feature toggles lag the recomposite, and blanking by id would erase the player's own invisible ring).</summary>
    private Carriers LiveCarriers()
    {
        // Non-null only while we have a pair on, so it doubles as the ownership flag (adopted pairs included).
        var glasses = compositor.InjectedGlassesItemId;
        return new Carriers(glasses, compositor.InjectedCarrierItemIds,
            compositor.InjectedCarrierSlots, GlassesSlotOwned: glasses != null);
    }

    /// <summary>The carriers to retire at boot.</summary>
    // Slots come from the persisted config; item ids from the game sheets, since the glasses flag is in-memory only.
    // An id only retires a slot naming that exact item, so this can loosen a match but never fabricate one.
    private Carriers BootCarriers()
        => new(InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ItemId,
               InvisibleRing.CarrierSlots
                   .Select(c => InvisibleRing.ResolveFor(Plugin.DataManager, log, c.Slot)?.ItemId)
                   .Where(id => id != null).Select(id => id!.Value).Distinct().ToList(),
               compositor.InjectedCarrierSlots);

    // Numeric colour fields compared with a small tolerance; a field on the design but missing from the state mismatches.
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
    /// Serialize now (the caller holds <c>gate</c>) but write off the calling thread, since the file reaches tens of
    /// MB. The newest snapshot wins; superseded flushes find nothing and skip.
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
    /// Snapshot a mod's effective gear-layer settings for every option, so the binding is self-contained. Mirrors CaptureColors.
    /// </summary>
    private OverlayGearOverride CaptureGear(OverlayEntry e)
    {
        var active = compositor.EffectiveGearOverrideFor(e.ModDirectory);

        var result = new OverlayGearOverride();

        var top = (e.Metadata.Overlays ?? []).FirstOrDefault();
        if (active?.Top != null) result.Top = CloneGearPreset(active.Top);
        else if (top != null) result.Top = GearSettingsPreset.From(top);

        // The Masks tab's own gear settings, captured separately like the mask colours.
        if (active?.Mask != null) result.Mask = CloneGearPreset(active.Mask);
        else if (e.Metadata.MaskDescriptor is { } md) result.Mask = GearSettingsPreset.From(md);

        // An imported pack's unconditional pieces, in their own slot, never Top: see OverlayGearOverride.Content.
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

        // The glow the panel edits per material, captured like the colours beside it.
        result.Materials = CaptureMaterials(
            e.Metadata.ContentMaterials, active?.Materials,
            m => m.Glow == null ? null : CloneGearPreset(m.Glow), CloneGearPreset);

        return result;
    }

    private static GearSettingsPreset CloneGearPreset(GearSettingsPreset p)
        => JsonSerializer.Deserialize<GearSettingsPreset>(JsonSerializer.Serialize(p)) ?? new();

    private static List<ColorTableRowPreset>? CloneRows(List<ColorTableRowPreset>? rows)
        => rows == null ? null : JsonSerializer.Deserialize<List<ColorTableRowPreset>>(JsonSerializer.Serialize(rows));

    /// <summary>
    /// Deep copy of a row list for editor preview. Per-element <see cref="ColorTableRowPreset.Clone"/> rather than
    /// <see cref="CloneRows"/>'s JSON round-trip, because this runs every frame.
    /// </summary>
    public static List<ColorTableRowPreset> CopyRows(List<ColorTableRowPreset>? rows)
    {
        var copy = new List<ColorTableRowPreset>(rows?.Count ?? 0);
        if (rows != null) foreach (var r in rows) copy.Add(r.Clone());
        return copy;
    }
}
