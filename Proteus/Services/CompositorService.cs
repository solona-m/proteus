using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

public class CompositorResult
{
    public bool Success { get; init; }
    public int TexturesPatched { get; init; }
    public int OverlayModsUsed { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class CompositorService : IDisposable
{
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly SidecarDiscoveryService discovery;
    private readonly TextureLoader textureLoader;
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly UVRemapService uvRemap;

    // Body-UV material suffixes (shared stem) and their UV body type, used by sibling
    // synthesis. _b/_a are body-UV only under /obj/body/, which InferBodyType enforces.
    /// <summary>
    /// How many overlays ahead of the blend to decode in the background.
    ///
    /// Depth 6 was measured and was WORSE than 3 (background work fell 867 ms -> 559 ms, composite rose
    /// 3964 ms -> 4195 ms): spreading the same thread pool over more files makes each one finish later,
    /// so the blend arrives mid-decode and blocks instead of hitting a warm cache. Prefetching only pays
    /// when it COMPLETES before the consumer gets there — deeper is not better. Must also stay matched to
    /// TextureLoader's cache budget: a file evicted before use is decoded twice.
    /// </summary>
    private const int PrefetchDepth = 3;

    private static readonly (string Suffix, string BodyType)[] BodySuffixes =
    {
        ("_bibo.mtrl", "bibo"),
        ("_b.mtrl",    "gen3"),
        ("_eve.mtrl",  "gen3"),
        ("_a.mtrl",    "gen2"),
    };

    private string modsRoot;
    private string managedModDir;

    private CancellationTokenSource? currentCts;
    private readonly SecondSkinService secondSkin;
    private readonly UvSeamMapService seamMaps;

    private readonly object triggerLock = new();
    private long _lastOwnRedrawTick = 0; // TickCount64 when we last called RedrawPlayer()
    private long _lastOwnReapplyTick = 0; // TickCount64 when we last called Glamourer ReapplyState()

    // Non-persistent per-mod color override pushed by the design-binding system. When set, the
    // compositor uses these colors in place of each mod's metadata.json colors for the run; null
    // means "use metadata as authored". Reference assignment is atomic; read on the recomposite task.
    private volatile IReadOnlyDictionary<string, OverlayColorOverride>? _colorOverride;
    // Snapshot of the player's active material game paths, captured on the main thread at trigger
    // time so the background recomposite can filter without touching main-thread-only IPCs.
    private volatile HashSet<string>? _activeMtrlSnapshot;
    // Set when a body mod (one whose files include an obj/body/ material) changes, or the player
    // collection changes — anything that could make _activeMtrlSnapshot wrong without a redraw.
    // GetActivePlayerMaterialPaths (a full Penumbra resource-tree walk on the framework thread) is
    // only paid for when this is set or the snapshot is cold; everything else reuses the cache.
    private volatile bool _activeMtrlSnapshotDirty;
    // Signature of the equipped gear models the second skin cuts its shells from (feet/legs/hands/body).
    // The shell now depends on WHICH gear is worn — a heel poses the foot, a top reshapes the chest — so
    // a redraw that changes the equipped set must rebuild it. Equipping through the game fires no mod or
    // design event, so this redraw-time diff is the only signal for it. Null until the first redraw sets
    // the baseline (the initial composite already covers load).
    private string? _lastEquipSignature;
    // Per gear-overlay shell material file names (ss_{letter}.mtrl), keyed by (mod, group, option), from
    // the last shell build — lets the colorset editor's "glow" button target the right live materials.
    // Built on the background composite thread, read on the UI thread: volatile publishes the swapped-in
    // reference (the map is immutable once assigned).
    private volatile Dictionary<(string ModDir, string? Group, string? Option), List<string>> _shellMaterials = new();

    /// <summary>The shell material file names (ss_{letter}.mtrl) for a gear overlay, or null if none were built.</summary>
    public IReadOnlyList<string>? GetShellMaterials(string modDir, string? group, string? option)
        => _shellMaterials.TryGetValue((modDir, group, option), out var leaves) ? leaves : null;

    // Per skin-overlay "glow" recipe (which composited-diffuse pixels map to each colour-table row),
    // keyed by (mod, group, option), from the last composite. Lets the colorset editor's glow button
    // light up a row's region on the live body diffuse via a texture rebind (no recomposite). Same
    // publish contract as _shellMaterials: built on the composite thread, swapped in as one reference.
    private volatile Dictionary<(string ModDir, string? Group, string? Option), List<Proteus.Interop.SkinGlowTarget>> _skinGlowTargets = new();

    /// <summary>Glow recipes for a skin overlay (one per composited body material), or null if none.</summary>
    public IReadOnlyList<Proteus.Interop.SkinGlowTarget>? GetSkinGlowTargets(string modDir, string? group, string? option)
        => _skinGlowTargets.TryGetValue((modDir, group, option), out var t) ? t : null;
    // Which gear model the second skin sources each slot's shell from (part -> .mdl game path), captured
    // on the framework thread from the draw object's loaded models so the background build can read it
    // without an IPC. Refreshed wherever the material snapshot is.
    private volatile IReadOnlyDictionary<string, string>? _equippedPartModels;
    // Enabled shape keys per drawn body model (normalized filename -> shape names), captured on the
    // framework thread each redraw. Used to bake body morphs (e.g. "Remove Hip Dips") into the second-skin
    // shell so it follows the body instead of diverging. See BodyShapeReader.
    private volatile IReadOnlyDictionary<string, HashSet<string>>? _bodyShapeSnapshot;
    // Signature of the enabled body shapes at the last composite, so a change forces a full redraw
    // (an in-place reload can't pick up the rebaked geometry). Null until the first composite. Volatile:
    // written by the composite task, read by the post-settle task (matching its _lastComposited* siblings).
    private volatile string? _lastCompositedBodyShapeSig;
    // Which ring/bracelet the second skin appends its shell into (slot rir|ril|wrs -> .mdl game path),
    // captured the same way as _equippedPartModels. Empty when no accessory is worn, in which case the
    // shell falls back to replacing the invisible Emperor's New Ring.
    private volatile IReadOnlyDictionary<string, string>? _equippedAccessoryModels;
    // Every loaded FACEWEAR/glasses "_met" model, sorted. Head equipment (helmets/hats) shares the "_met"
    // path but is a different slot and is filtered OUT (see EquippedMetModelsFromModels) — only facewear can
    // host the shell. A list because real glasses + our injected pair are both facewear and could coexist.
    private volatile IReadOnlyList<string>? _equippedMetModels;
    // modDir -> (does this mod ship an obj/body/ material file, fingerprint it was computed at).
    // Fingerprint = summed size+mtime over the mod's own default_mod.json/group_*.json, so a mod
    // update is detected without needing a plugin restart. Seeded from config.KnownBodyMods.
    // ConcurrentDictionary because IsBodyMod runs on a background thread (its manifest file I/O +
    // config.Save must not touch the framework thread) while OnModDeleted may read/remove entries.
    private readonly ConcurrentDictionary<string, (bool IsBodyMod, long Fingerprint)> _bodyModCache =
        new(StringComparer.OrdinalIgnoreCase);
    // Serializes the config.KnownBodyMods mutations + config.Save() done off-thread by IsBodyMod
    // and OnModDeleted, so a save never serializes the dictionary while another thread mutates it.
    private readonly object _bodyModConfigLock = new();
    // Body type and char codes that the last completed Recomposite() actually composited for.
    // Used by the post-redraw check to detect switches and trigger a corrective composite.
    private volatile string? _lastCompositedBodyType;
    private volatile string? _lastCompositedCharCodes;
    // Set to 1 when a Glamourer customization change (race/body) is pending a recomposite.
    // Cleared by OnLocalPlayerRedrawn (preferred — snapshot is fresh) or a 2s timeout fallback.
    private int _pendingCustomizationRecomposite = 0;
    // Glamourer's currently-displayed char code (e.g. "c1801"), updated on the framework thread.
    // Null when Glamourer isn't available or hasn't overridden the race.
    private volatile string? _glamourerCharCode;

    public CompositorResult? LastResult { get; private set; }
    public List<OverlayEntry> LastDiscovered { get; private set; } = [];
    public event Action? ResultChanged;

    // Guards for EnsureDiscovered's background probe: one at a time, and not more often than this.
    private const int DiscoverProbeIntervalMs = 2000;
    private int _discoverProbeRunning;
    private long _lastDiscoverProbeTick;

    // Boot composite. At game start no event reliably fires a composite once BOTH Penumbra's mod list is
    // readable AND the local player's draw object exists: PenumbraReady can beat the player, the first
    // redraw can beat discovery (or never fire if the character was drawn before we loaded), the glamourer
    // design-apply gets echo-suppressed, and the discovery probe only runs while the status window is open.
    // This framework poll waits for both preconditions and fires once per login (re-armed when the draw
    // object goes away, so logging into another character composites again).
    private int _bootComposited;      // 1 once the boot composite has fired this login; reset on logout
    private int _bootProbeRunning;    // one off-thread discovery check at a time
    private long _lastBootPollTick;
    private volatile bool _disposed;  // set in Dispose so an in-flight probe task bails

    /// <summary>
    /// Populate <see cref="LastDiscovered"/> for the UI WITHOUT compositing. That list is otherwise only
    /// filled deep inside a full recomposite, and at game boot none runs (Penumbra isn't up yet when the
    /// plugin is constructed), so the mod list would sit empty until the user pressed Refresh — which
    /// forces a whole composite just to see a list. This does the cheap discovery half only: no managed-mod
    /// write, no Penumbra reload, no redraw. Safe to call every frame — it no-ops once the list is
    /// populated, while a probe is in flight, or within the retry interval, and runs off the UI thread.
    /// </summary>
    public void EnsureDiscovered()
    {
        if (LastDiscovered.Count > 0 || !config.PluginEnabled || !penumbra.IsAvailable) return;
        if (unchecked(Environment.TickCount64 - _lastDiscoverProbeTick) < DiscoverProbeIntervalMs) return;
        if (Interlocked.Exchange(ref _discoverProbeRunning, 1) == 1) return;

        _lastDiscoverProbeTick = Environment.TickCount64;
        Task.Run(() =>
        {
            try
            {
                // Discovery returns empty while Penumbra's mod list isn't readable yet (early boot); leave
                // LastDiscovered alone in that case so the retry interval picks it up a moment later.
                var found = discovery.DiscoverAll();
                if (found.Count > 0 && LastDiscovered.Count == 0)
                {
                    LastDiscovered = found;
                    log.Debug("[Proteus] mod list populated by discovery probe ({0} mod(s)) — no composite run", found.Count);
                }
            }
            catch (Exception ex) { log.Debug("[Proteus] discovery probe failed: {0}", ex.Message); }
            finally { Interlocked.Exchange(ref _discoverProbeRunning, 0); }
        });
    }

    public CompositorService(
        PenumbraBridge penumbra,
        GlamourerBridge glamourer,
        SidecarDiscoveryService discovery,
        TextureLoader textureLoader,
        Configuration config,
        IPluginLog log,
        UVRemapService uvRemap)
    {
        this.penumbra  = penumbra;
        this.glamourer = glamourer;
        this.discovery = discovery;
        this.textureLoader = textureLoader;
        this.config = config;
        this.log = log;
        this.uvRemap = uvRemap;
        this.secondSkin = new SecondSkinService(penumbra, textureLoader, discovery, uvRemap, config, log);
        this.seamMaps  = new UvSeamMapService(log);

        modsRoot      = penumbra.GetModDirectory() ?? string.Empty;
        managedModDir = Path.Combine(modsRoot, SidecarDiscoveryService.ManagedModDir);

        foreach (var (modDir, entry) in config.KnownBodyMods)
            _bodyModCache[modDir] = (entry.IsBodyMod, entry.Fingerprint);

        // Seed from the last session's snapshot instead of forcing an expensive Penumbra walk at
        // boot. Trusted until a body mod change or a real redraw proves it stale.
        if (config.CachedActiveMaterialPaths is { Count: > 0 } cached)
            _activeMtrlSnapshot = new HashSet<string>(cached, StringComparer.OrdinalIgnoreCase);
        else
            _activeMtrlSnapshotDirty = true;

        penumbra.ModSettingChanged += OnModSettingChanged;
        penumbra.ModAdded          += OnModAdded;
        penumbra.ModDeleted        += OnModDeleted;
        penumbra.PenumbraReady     += OnPenumbraReady;
        penumbra.PlayerCollectionChanged += OnPlayerCollectionChanged;
        penumbra.LocalPlayerRedrawn            += OnLocalPlayerRedrawn;
        glamourer.LocalPlayerStateChanged      += OnGlamourerStateChanged;
        glamourer.LocalPlayerCustomizationChanged += OnGlamourerCustomizationChanged;
        Plugin.Framework.Update += OnBootPoll;

        // The decode cache is only ever trimmed when it EXCEEDS its budget, so left alone it holds the full
        // budget for the plugin's lifetime — a couple of GB sitting there long after the last composite.
        // Poll on a timer rather than the framework tick: this is a 30s cadence and has no business running
        // per frame, and clearing a ConcurrentDictionary needs no particular thread.
        idleCacheTimer = new Timer(_ =>
        {
            try
            {
                int dropped = textureLoader.ReleaseIfIdle(DecodeCacheIdleRelease);
                if (dropped > 0)
                    log.Debug("[Proteus] decode cache: released {0} entries after {1:F0}s idle",
                              dropped, DecodeCacheIdleRelease.TotalSeconds);
            }
            catch (Exception ex) { log.Warning(ex, "[Proteus] decode cache idle release failed"); }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>How long the decode cache may sit untouched before it is dropped. Long enough to cover a
    /// burst of recomposites while editing colours, short enough that idling in a city doesn't hold GBs.</summary>
    private static readonly TimeSpan DecodeCacheIdleRelease = TimeSpan.FromSeconds(60);
    private readonly Timer idleCacheTimer;

    // ── settle redraw for sync plugins ───────────────────────────────────────
    // Mare and its forks (PSync/MareSempiterne, Snowcloak, …) build a peer's file list from the resources
    // the character has ACTUALLY resolved, and their between-redraw channel deliberately drops our file
    // types: TransientResourceManager's allow-list only handles tex/mdl/mtrl while a manual transient
    // recording is running. Our entire output is those three, written AFTER the draw — so an in-place
    // reload leaves paired users seeing none of it (no fabric, vanilla skin) until something else forces
    // a redraw. That's why "redraw + refresh" clears it by hand.
    //
    // Fire ONE real redraw once edits settle: the in-place reload still lands immediately, so dragging a
    // colour slider stays flicker-free, and the redraw that follows the last edit is what the peer picks
    // up. Rescheduled on every composite, so a burst collapses to a single redraw at the end.
    private Timer? syncRedrawTimer;
    private static readonly TimeSpan SyncRedrawSettle = TimeSpan.FromSeconds(3);

    // Re-probed rather than cached forever: a sync plugin can be installed or enabled mid-session.
    private long syncProbeTick;
    private bool syncPluginLoaded;

    /// <summary>Push the configured decode-cache budget onto the loader. The Settings slider calls this —
    /// the UI has no reference to the loader. Takes effect at once: the loader trims on assignment, so
    /// lowering the budget releases the excess immediately rather than at the next composite.</summary>
    public void ApplyDecodeCacheBudget()
        => textureLoader.DecodeCacheBudgetBytes = Math.Max(256, config.DecodeCacheBudgetMb) * 1024L * 1024;

    // Framework-thread poll that fires the boot composite once the player and Penumbra are both ready.
    // Only the cheap checks (player address, Penumbra availability) run on the framework thread; the
    // potentially-slow discovery scan runs off-thread, throttled and single-flighted, so no frame hitches.
    private void OnBootPoll(IFramework fw)
    {
        // Re-arm across logout: with no draw object there's nothing to composite, and clearing the flag
        // lets the next login run its own boot composite (character swaps don't otherwise reach here).
        if ((Plugin.ObjectTable.LocalPlayer?.Address ?? 0) == 0)
        {
            Volatile.Write(ref _bootComposited, 0);
            return;
        }
        if (Volatile.Read(ref _bootComposited) == 1) return;        // already composited this login
        if (!config.PluginEnabled || !penumbra.IsAvailable) return; // wait for Penumbra IPC

        var now = Environment.TickCount64;
        if (unchecked(now - _lastBootPollTick) < 500) return;
        _lastBootPollTick = now;
        if (Interlocked.Exchange(ref _bootProbeRunning, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                if (_disposed) return;
                // Penumbra's mod list can still be unreadable for a moment after the player draws;
                // an empty result just means "not yet", so leave the flag clear and try again next tick.
                if (discovery.DiscoverEnabled().Count == 0) return;
                log.Debug("[Proteus] boot composite: player + discovery ready");
                TriggerRecomposite("boot-ready");
                // Only latch AFTER the trigger, so a throw above leaves the poll armed to retry.
                Volatile.Write(ref _bootComposited, 1);
            }
            catch (Exception ex) { log.Debug("[Proteus] boot composite probe failed: {0}", ex.Message); }
            finally { Interlocked.Exchange(ref _bootProbeRunning, 0); }
        });
    }

    public void Dispose()
    {
        // Pull our injected invisible glasses off the player before we tear down (Glamourer is disposed
        // after us in Plugin.Dispose, so this still succeeds). Otherwise a plugin reload leaves a phantom
        // bonus item until the next design/reset.
        RemoveInjectedGlasses();

        _disposed = true;   // an in-flight boot-probe task bails instead of touching torn-down bridges

        idleCacheTimer.Dispose();
        syncRedrawTimer?.Dispose();

        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.ModAdded          -= OnModAdded;
        penumbra.ModDeleted        -= OnModDeleted;
        penumbra.PenumbraReady     -= OnPenumbraReady;
        penumbra.PlayerCollectionChanged -= OnPlayerCollectionChanged;
        penumbra.LocalPlayerRedrawn              -= OnLocalPlayerRedrawn;
        glamourer.LocalPlayerStateChanged        -= OnGlamourerStateChanged;
        glamourer.LocalPlayerCustomizationChanged -= OnGlamourerCustomizationChanged;
        Plugin.Framework.Update -= OnBootPoll;

        currentCts?.Cancel();
        currentCts?.Dispose();
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnModSettingChanged(ModSettingChange change, Guid collId, string modDir, bool inherited)
    {
        if (string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return;
        var playerColl = penumbra.GetPlayerCollectionId();
        if (playerColl == null || collId != playerColl.Value)
            return;

        var sidecar = HasSidecar(modDir);

        // For enable/disable events on our own overlay mods, re-check whether the active mod set
        // actually changed. Glamourer re-applies designs after each redraw, calling Penumbra to
        // enable already-enabled mod associations — Penumbra fires EnableState regardless of whether
        // the value changed. Without this guard: RedrawPlayer() → Glamourer re-apply → this → loop.
        if (sidecar && change is ModSettingChange.EnableState or ModSettingChange.MultiEnableState)
        {
            var current = discovery.DiscoverAll();
            if (current.Count == 0) return;
            if (DiscoveredSetsEqual(current, LastDiscovered)) return;
            LastDiscovered = current;
        }
        else if (change == ModSettingChange.TemporarySetting)
        {
            // Glamourer re-applies temporary mod settings (option groups) after every character
            // redraw caused by RedrawPlayer(). The echo fires ~90ms after our redraw call.
            // Suppress triggers within 1500ms of our own redraw — human design-switching takes
            // at least a few seconds after seeing the result, so false suppression is negligible.
            // Applies to overlay and body mods alike (both can be design-associated).
            var msSince = unchecked(Environment.TickCount64 - Interlocked.Read(ref _lastOwnRedrawTick));
            if (msSince >= 0 && msSince < 1500) return;
        }

        if (sidecar)
        {
            // The mod's files may have changed underneath a cached decode (a reinstall/edit that kept the
            // same timestamp or byte length); drop this mod's cached textures so the composite re-reads them.
            textureLoader.EvictMod(modDir);
            TriggerRecomposite($"ModSettingChanged:{change}:{modDir}");
            return;
        }

        // Not one of our overlay mods: the only other thing we react to is a body mod (ships an
        // obj/body/ material), whose change can leave the cached snapshot wrong without a redraw.
        // Its detection does manifest file I/O + a config.Save, so run it off the framework thread.
        EvaluateBodyModOffThread(modDir, $"ModSettingChanged:{change}:{modDir}");
    }

    private void OnModAdded(string modDir)
    {
        // A (re)install almost always rewrites the mod's files — evict any stale cached decodes for it.
        textureLoader.EvictMod(modDir);
        if (HasSidecar(modDir))
        {
            TriggerRecomposite($"ModAdded:{modDir}");
            return;
        }
        // A (re)installed body mod may have changed its files without changing its directory name —
        // IsBodyMod's fingerprint check handles that. Off-thread, same as OnModSettingChanged.
        EvaluateBodyModOffThread(modDir, $"ModAdded:{modDir}");
    }

    private void OnModDeleted(string modDir)
    {
        // Files are already gone by the time this fires, so use the last-known classification
        // rather than rescanning; then drop it, there's nothing left to invalidate against.
        var wasBodyMod = _bodyModCache.TryGetValue(modDir, out var cached) && cached.IsBodyMod;
        if (wasBodyMod) _activeMtrlSnapshotDirty = true;

        // Drop the cached classification off the framework thread — config.Save is a disk write.
        Task.Run(() =>
        {
            _bodyModCache.TryRemove(modDir, out _);
            lock (_bodyModConfigLock)
                if (config.KnownBodyMods.Remove(modDir)) config.Save();
        });

        if (LastDiscovered.All(e => !string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase))
            && !wasBodyMod)
            return;
        TriggerRecomposite($"ModDeleted:{modDir}");
    }

    // IsBodyMod does manifest file I/O + a config.Save on a cache miss, both of which must stay off
    // the framework-thread event handlers. Evaluate it on a background thread; if the mod turns out
    // to ship body materials, mark the snapshot dirty and trigger a (debounced) recomposite.
    private void EvaluateBodyModOffThread(string modDir, string reason)
    {
        Task.Run(() =>
        {
            try
            {
                if (!IsBodyMod(modDir)) return;
                textureLoader.EvictMod(modDir);   // body textures may have changed under a cached decode
                _activeMtrlSnapshotDirty = true;
                TriggerRecomposite(reason);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Proteus] Body-mod evaluation failed for {0}", modDir);
            }
        });
    }

    private void OnPenumbraReady()
    {
        modsRoot      = penumbra.GetModDirectory() ?? string.Empty;
        managedModDir = Path.Combine(modsRoot, SidecarDiscoveryService.ManagedModDir);
        // Now that the mod directory is resolvable, make sure the bundled starter effects are present.
        discovery.SeedDefaultEffects();
        if (!config.PluginEnabled) return;
        // Only trigger if discovery already sees mods. PenumbraReady can fire before Penumbra's
        // mod settings are readable; if discovery returns empty we'd wipe the existing output.
        // Leave previous-session files intact — ModSettingChanged/ModAdded will fire the first
        // real composite once settings are available.
        if (discovery.DiscoverEnabled().Count > 0)
            TriggerRecomposite("PenumbraReady");
    }

    private void OnPlayerCollectionChanged()
    {
        // The collection assigned to the player changed — the enabled mod set, priorities and
        // option selections are all collection-scoped, so the whole composite must be recomputed.
        // Rare event; not worth trying to scan every mod in the new collection, just force one walk.
        _activeMtrlSnapshotDirty = true;
        if (!config.PluginEnabled) return;
        TriggerRecomposite("collection-changed");
    }

    // Called on the framework thread by PenumbraBridge whenever the local player's draw object is
    // redrawn. Most material changes happen this way, so this is the cheap, common-case refresh —
    // it's piggybacking on work Penumbra/the game already did, not an extra query. Only write
    // non-null: GameObjectRedrawn can fire mid-redraw while the draw object is being destroyed, at
    // which point GetActivePlayerMaterialPaths returns null. Writing null would clear a valid cached
    // snapshot and trigger the all-races bug.
    private void OnLocalPlayerRedrawn()
    {
        var snapshot = penumbra.GetActivePlayerMaterialPaths();
        bool equipChanged = false;
        if (snapshot != null)
        {
            _activeMtrlSnapshot = snapshot;
            _activeMtrlSnapshotDirty = false;
            // Update the in-memory field only — this runs on the framework thread on every redraw
            // (equipment changes, zoning, etc.), and a disk write here would reintroduce the same
            // class of framework-thread cost this whole change exists to avoid. The value still gets
            // persisted the next time TriggerRecomposite's own fetch (below) calls config.Save().
            config.CachedActiveMaterialPaths = snapshot.ToList();

            // Did the gear the second skin cuts its shells from change? Equipping/removing an item
            // fires no mod-setting or design event, so this diff is what makes the shell follow it.
            var modelPaths = penumbra.GetActivePlayerModelPaths();
            var equipped = EquippedPartModelsFromModels(modelPaths);
            var accessories = EquippedAccessoryModelsFromModels(modelPaths);
            var metModels = EquippedMetModelsFromModels(modelPaths, InvisibleGlasses.FacewearModelSets(Plugin.DataManager));
            _equippedPartModels = equipped;
            _equippedAccessoryModels = accessories;
            _equippedMetModels = metModels;
            var sig = EquipSignature(equipped, accessories, metModels);
            equipChanged = _lastEquipSignature != null && !string.Equals(_lastEquipSignature, sig, StringComparison.Ordinal);
            _lastEquipSignature = sig;
        }

        RefreshGlamourerCharCode();

        if (equipChanged && config.PluginEnabled)
            TriggerRecomposite("equipment-change");

        if (Interlocked.Exchange(ref _pendingCustomizationRecomposite, 0) == 1)
            TriggerRecomposite("glamourer-customization");
    }

    private void OnGlamourerCustomizationChanged()
    {
        if (!config.PluginEnabled) return;
        // Read the Glamourer-displayed char code now (framework thread), before it changes again.
        RefreshGlamourerCharCode();
        // Don't recomposite immediately — the snapshot still has the old race because
        // GameObjectRedrawn hasn't fired yet. Set the pending flag; OnLocalPlayerRedrawn
        // will fire the recomposite once the snapshot is fresh.
        Interlocked.Exchange(ref _pendingCustomizationRecomposite, 1);
        // Fallback: if GameObjectRedrawn never fires (e.g. redraw suppressed), trigger anyway.
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            if (Interlocked.Exchange(ref _pendingCustomizationRecomposite, 0) == 1)
                TriggerRecomposite("glamourer-customization-timeout");
        });
    }

    // Must be called on the framework thread. Caches the char code Glamourer is currently
    // displaying so Recomposite (background thread) can filter without an IPC call.
    private void RefreshGlamourerCharCode()
    {
        try
        {
            var state = glamourer.GetObjectState(0);
            var cust  = state?["Customize"];
            if (cust == null) { _glamourerCharCode = null; return; }
            var race  = cust["Race"]?["Value"]?.ToObject<byte>() ?? 0;
            var tribe = cust["Clan"]?["Value"]?.ToObject<byte>() ?? 0;
            var sex   = cust["Gender"]?["Value"]?.ToObject<byte>() ?? 0;
            _glamourerCharCode = BodyCodeFromCustomize(race, tribe, sex);
        }
        catch { _glamourerCharCode = null; }
    }

    private void OnGlamourerStateChanged()
    {
        // Suppress the echo from our own ReapplyState call: ReapplyPlayerState fires Glamourer's
        // StateChanged(Reapply), which lands here. Ignore events within a short window of our call.
        var msSinceReapply = unchecked(Environment.TickCount64 - Interlocked.Read(ref _lastOwnReapplyTick));
        if (msSinceReapply >= 0 && msSinceReapply < 250) return;

        // (Invisible-glasses re-assert needs no bookkeeping here: a design that reverts our ApplyFlag.Once
        // glasses just empties the slot, and the recomposite this triggers re-injects. Ownership is derived
        // from the worn set, not a flag — see ReconcileInvisibleGlasses.)

        // Glamourer applied a design / reset / reapplied state on the local player.
        // Diff the discovered set against the last known set — only recomposite if something changed.
        var current = discovery.DiscoverAll();

        // Empty results mean Penumbra's IPC is temporarily unavailable (e.g. mid-reload).
        // Treat empty as "no information" rather than "no mods" to avoid overwriting a good
        // LastDiscovered with [] and re-triggering on the next Reapply event.
        if (current.Count == 0) return;

        if (DiscoveredSetsEqual(current, LastDiscovered)) return;

        // Update LastDiscovered eagerly before triggering. Without this, repeated glamourer events
        // cancel the in-flight recomposite before it reaches the `LastDiscovered = allEntries` line,
        // keeping the sets permanently unequal and looping.
        LastDiscovered = current;
        TriggerRecomposite("glamourer-design");
    }

    // Extracts the human character code (e.g. "c0101") from a path like
    // "chara/human/c0101/obj/body/...". Returns null for non-human paths.
    private static string? ExtractHumanCharCode(string path)
    {
        const string prefix = "chara/human/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        int slash = path.IndexOf('/', prefix.Length);
        return slash > prefix.Length ? path[prefix.Length..slash] : null;
    }

    // Set-based comparison — order-independent. Discovery sorts by priority but the sort is not
    // stable, so two mods with equal priority can swap positions between calls. Compares enabled
    // state too so toggling a mod on/off in Penumbra triggers a recomposite.
    private static bool DiscoveredSetsEqual(List<OverlayEntry> a, List<OverlayEntry> b)
    {
        if (a.Count != b.Count) return false;
        var lookup = new Dictionary<string, (int Priority, bool Enabled)>(a.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in a) lookup[e.ModDirectory] = (e.Priority, e.Enabled);
        foreach (var e in b)
            if (!lookup.TryGetValue(e.ModDirectory, out var v) || v.Priority != e.Priority || v.Enabled != e.Enabled)
                return false;
        return true;
    }

    private bool HasSidecar(string modDir)
    {
        var metaPath = Path.Combine(modsRoot, modDir, "Proteus", "metadata.json");
        return File.Exists(metaPath);
    }

    // ── Body-mod detection ──────────────────────────────────────────────────────
    // Whether a mod ships an obj/body/ material redirect — the only kind of change that can make
    // _activeMtrlSnapshot wrong (see the sibling-synthesis staleness this fixes: a body-shape mod
    // like AB Body applies its option as an in-place Penumbra file redirect on an already-loaded
    // resource, with no redraw, so the old "only refresh on redraw" assumption went stale forever).
    // Detected from the mod's own manifest files on disk — no Penumbra IPC needed, so it can run on
    // any thread. It's driven off the framework thread (EvaluateBodyModOffThread) because the
    // manifest reads + config.Save on a cache miss shouldn't block the game's main thread.

    private static readonly Regex BodyMaterialPattern = new(
        // BodySuffixes entries look like "_bibo.mtrl" / "_b.mtrl" — strip the leading "_" and
        // trailing ".mtrl" to get the alternation core ("bibo", "b", "eve", "a").
        @"obj[/\\]body[/\\][^""]*?_(" + string.Join('|', BodySuffixes
            .Select(s => Regex.Escape(s.Suffix[1..^".mtrl".Length]))) + @")\.mtrl",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A mod's own redirect manifest(s). Penumbra v4 keeps everything in meta.json; the legacy
    // default_mod.json/group_*.json pair is still accepted for folders an older Penumbra wrote.
    private static bool IsModManifestFile(string fileName)
        => string.Equals(fileName, PenumbraModMeta.MetaFile, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, PenumbraModMeta.LegacyDefaultMod, StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("group_", StringComparison.OrdinalIgnoreCase);

    // Sums size + mtime over a mod's manifest files — cheap invalidation key so a mod update is
    // detected without a plugin restart. Adding meta.json to the set invalidates every cached
    // KnownBodyMods entry once; that's a one-time rescan, and self-healing.
    private static long ComputeModFingerprint(string modRoot)
    {
        long fp = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!IsModManifestFile(Path.GetFileName(file))) continue;
                var info = new FileInfo(file);
                fp = unchecked(fp * 31 + info.Length + info.LastWriteTimeUtc.Ticks);
            }
        }
        catch { /* modRoot missing/unreadable */ }
        return fp;
    }

    private static bool ScanModForBodyMaterials(string modRoot)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!IsModManifestFile(Path.GetFileName(file))) continue;
                if (BodyMaterialPattern.IsMatch(File.ReadAllText(file))) return true;
            }
        }
        catch { /* modRoot missing/unreadable */ }
        return false;
    }

    private bool IsBodyMod(string modDir)
    {
        // Proteus's own sidecar/overlay mods legitimately reference body materials too — that's
        // their redirect target, not a body-shape change — and they're already fully handled via
        // the HasSidecar-gated recompose path regardless of this flag. Exclude them here, or every
        // mask/color toggle on the user's own overlay mods would re-trigger the expensive walk this
        // whole mechanism exists to avoid.
        if (HasSidecar(modDir)) return false;

        var modRoot = Path.Combine(modsRoot, modDir);
        var fingerprint = ComputeModFingerprint(modRoot);
        if (_bodyModCache.TryGetValue(modDir, out var cached) && cached.Fingerprint == fingerprint)
            return cached.IsBodyMod;

        var isBody = ScanModForBodyMaterials(modRoot);
        _bodyModCache[modDir] = (isBody, fingerprint);
        lock (_bodyModConfigLock)
        {
            config.KnownBodyMods[modDir] = new BodyModCacheEntry { IsBodyMod = isBody, Fingerprint = fingerprint };
            config.Save();
        }
        return isBody;
    }

    // ── Color override (design bindings) ───────────────────────────────────────

    /// <summary>
    /// Push a non-persistent per-mod color override (from a restored design binding) to be used at
    /// composite time in place of metadata.json colors. Pass null to clear. Does not itself trigger
    /// a recomposite — the caller decides when to recomposite.
    /// </summary>
    /// <summary>
    /// Gear-layer settings pushed by a design binding — layer, shader, effect, scroll speed and tiling.
    /// Applied at composite time onto a COPY of the descriptor, so metadata.json is never mutated
    /// (same contract as the colour override).
    /// </summary>
    private volatile IReadOnlyDictionary<string, OverlayGearOverride>? _gearOverride;

    public void SetActiveGearOverride(IReadOnlyDictionary<string, OverlayGearOverride>? overrideByMod)
        => _gearOverride = overrideByMod;

    public void SetActiveColorOverride(IReadOnlyDictionary<string, OverlayColorOverride>? overrideByMod)
        => _colorOverride = overrideByMod;

    /// <summary>
    /// Mod-wide overlay tab/stack order pushed by a design binding: modDir → option keys
    /// (<see cref="Configuration.ModStackEntry"/>) top-first. Applied at composite time in place of
    /// <see cref="Configuration.ModStackIndexOf"/>, so the global stack-order config is never mutated
    /// (same contract as the colour/gear overrides). Null ⇒ fall back to the config order.
    /// </summary>
    private volatile IReadOnlyDictionary<string, List<string>>? _stackOverride;

    public void SetActiveStackOverride(IReadOnlyDictionary<string, List<string>>? overrideByMod)
        => _stackOverride = overrideByMod;

    /// <summary>
    /// Apply the plugin's enabled state, both visually and in Penumbra.
    ///
    /// Turning off in the wrong order leaves the character still wearing the last composite: the mod has
    /// to stop contributing files and the character has to be redrawn BEFORE the mod entry is switched
    /// off, or the redraw would just re-apply what was already there.
    ///
    /// Off: clear the managed mod's redirects -> redraw (character now shows no Proteus) -> disable the
    /// mod in Penumbra.  On: enable the mod, then recomposite (which reloads and redraws).
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        var collId = penumbra.GetPlayerCollectionId();

        if (enabled)
        {
            EnsureManagedModExists();
            if (collId.HasValue)
                penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
            TriggerRecomposite("enabled");
            return;
        }

        // Disabling stops all hosting, so pull our injected invisible glasses off the player's Glamourer
        // state (else they'd linger as a phantom bonus item).
        RemoveInjectedGlasses();

        // If gear shells were active, a hosted accessory's model is redirected to our merged model. An
        // in-place reload won't reload that .mdl, so the shell would linger on the accessory after the
        // redirect clears — force a FULL redraw to reload the accessory's original model.
        bool restoreAccessory = _secondSkinActive;

        Task.Run(() =>
        {
            try
            {
                WriteManagedModJson(new Dictionary<string, string>());
                penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                if (restoreAccessory) _needFullRedraw = true;
                ReloadAndRedraw();   // character reverts to un-composited
                _secondSkinActive = false;

                if (collId.HasValue)
                    penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, false);

                log.Debug("[Proteus] disabled: output cleared, redrawn ({0}), Penumbra mod off",
                    restoreAccessory ? "full — accessory restored" : "in-place");
            }
            catch (Exception ex) { log.Error(ex, "[Proteus] failed to disable cleanly"); }
        });
    }

    // ── Trigger ──────────────────────────────────────────────────────────────

    /// <summary>Colorset "glow" highlighter, cleared on recomposite (the shell may rebuild with a new letter).</summary>
    public Proteus.Interop.ColorTableHighlighter? Highlighter { get; set; }

    /// <summary>
    /// Manual escape hatch: drop every cached decoded texture, then recomposite immediately. For the rare
    /// case where a texture edit isn't reflected because the file kept the same timestamp and byte length —
    /// something the decode cache's key can't see. Returns the number of cache entries dropped.
    /// </summary>
    public int ClearTextureCacheAndRecomposite()
    {
        int dropped = textureLoader.ClearCache();
        log.Information("[Proteus] Texture cache cleared manually ({0} entries) — recompositing.", dropped);
        TriggerRecomposite("clear-texture-cache", 0);
        return dropped;
    }

    /// <summary>
    /// Schedule a recomposite on a background thread.
    /// Any in-flight recomposite is cancelled first (debounce), so the timer restarts from the last
    /// call. <paramref name="delayMs"/> sets how long to wait: the default 200ms coalesces bursts like
    /// mask toggles, while colour-table edits pass a longer window so a run of slider drags recomposites
    /// only once, 5s after the user stops.
    /// </summary>
    public void TriggerRecomposite(string reason, int delayMs = 200)
    {
        if (!config.PluginEnabled || !penumbra.IsAvailable) return;
        Highlighter?.Clear();

        CancellationTokenSource cts;
        lock (triggerLock)
        {
            currentCts?.Cancel();
            currentCts?.Dispose();
            cts = currentCts = new CancellationTokenSource();
        }

        log.Debug("[Proteus] Recomposite triggered: {0} (delay {1}ms)", reason, delayMs);
        var token = cts.Token;
        Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Refresh the equipped gear models the second skin sources its shells from, EVERY composite.
            // Unlike the material snapshot this can't be gated on cold/dirty: equipping an item fires no
            // mod/collection event, and a composite can be triggered while no redraw has repopulated the
            // cache (e.g. right after load, when the first walk hit a not-yet-ready player and returned
            // null). The walk is the same cheap ~2-8ms framework call. Keep the last value on a transient
            // null so a mid-reload blank draw object doesn't wipe a good set.
            try
            {
                var equipped = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerModelPaths).GetAwaiter().GetResult();
                if (equipped != null)
                {
                    _equippedPartModels = EquippedPartModelsFromModels(equipped);
                    _equippedAccessoryModels = EquippedAccessoryModelsFromModels(equipped);
                    _equippedMetModels = EquippedMetModelsFromModels(equipped, InvisibleGlasses.FacewearModelSets(Plugin.DataManager));
                }
            }
            catch (OperationCanceledException) { return; }

            // Which shape keys the game has enabled on each drawn body model (e.g. "Remove Hip Dips").
            // Read here, EVERY composite, for the same reason the equipped-models walk above is: a mod
            // toggle changes the enabled shapes but fires no redraw when Proteus uses the in-place reload,
            // so the redraw hook is unreliable. Must run on the framework thread (walks the draw object);
            // GetResult() (not await) for the same reason documented below the material-snapshot walk.
            // Stage 1: log it so toggling a body option shows the shape name it controls.
            try
            {
                var shapes = Plugin.Framework.RunOnFrameworkThread(
                    () => Interop.BodyShapeReader.ReadEnabledShapes(Plugin.ObjectTable.LocalPlayer?.Address ?? 0))
                    .GetAwaiter().GetResult();
                _bodyShapeSnapshot = shapes;
                if (shapes.Count > 0)
                    foreach (var (path, names) in shapes)
                        log.Debug("[Proteus] body shapes enabled: {0} -> [{1}]",
                            SanitizeName(path), string.Join(", ", names));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.Warning("[Proteus] body-shape read failed: {0}", ex.Message); }

            // OnLocalPlayerRedrawn only fires when the draw object is recreated, but some mods (e.g.
            // body replacers that redirect an always-loaded "smallclothes" resource in place) change
            // which materials are active WITHOUT a redraw. GetActivePlayerMaterialPaths must run on
            // the framework thread (it walks the draw object); it's cheap (~2-8ms), but we still only
            // pay for it when the cache is cold or IsBodyMod flagged it dirty (see OnModSettingChanged/
            // OnModAdded/OnModDeleted/OnPlayerCollectionChanged), not on every mask/color toggle.
            if (_activeMtrlSnapshot == null || _activeMtrlSnapshotDirty)
            {
                bool wasDirty = _activeMtrlSnapshotDirty;
                log.Debug("[Proteus] Refreshing active-material snapshot (cold={0}, dirty={1})",
                    _activeMtrlSnapshot == null, wasDirty);

                // GetResult(), NOT await: RunOnFrameworkThread's task COMPLETES on the framework
                // thread, and an await continuation would run INLINE on that completing thread
                // (ConfigureAwait(false) doesn't prevent this — it only suppresses returning to a
                // captured context). That would hop the rest of this lambda — including Recomposite,
                // whose Parallel.ForEach uses its calling thread as a worker — onto the framework
                // thread, freezing the game for the whole multi-second composite. GetResult() instead
                // blocks THIS background pool thread for the ~2ms IPC and stays on it, mirroring the
                // ReapplyPlayerState call later in Recomposite.
                HashSet<string>? fresh;
                try { fresh = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }

                // A body mod changed, but the new body's materials don't load until the character is
                // reloaded — and that reload is normally Proteus's OWN post-composite reapply. So a
                // plain composite here keys off the stale (pre-load) snapshot and needs a second
                // corrective composite once the reapply lands the new materials. If the body-type set
                // still matches what we last composited (the change hasn't landed), force the reload
                // up front and wait for the body types to actually change, so we composite ONCE with
                // the settled state. SchedulePostRedrawBodyTypeCheck stays as a backstop for a load
                // slower than this window.
                if (wasDirty && fresh != null
                    && string.Equals(BodyTypeKey(fresh), _lastCompositedBodyType, StringComparison.OrdinalIgnoreCase))
                {
                    var beforeKey = _lastCompositedBodyType;
                    RefreshPlayerTextures(); // reload the character so the new body's materials load
                    for (int i = 0; i < 12; i++) // up to ~3s, then compose anyway (backstop covers misses)
                    {
                        try { await Task.Delay(250, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        HashSet<string>? next;
                        try { next = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { return; }
                        if (next == null) break;
                        fresh = next;
                        if (!string.Equals(BodyTypeKey(next), beforeKey, StringComparison.OrdinalIgnoreCase))
                            break; // the reload landed the new body materials → settled
                    }
                }

                if (token.IsCancellationRequested) return;
                if (fresh != null)
                {
                    _activeMtrlSnapshot = fresh;
                    _activeMtrlSnapshotDirty = false;
                    config.CachedActiveMaterialPaths = fresh.ToList();
                    config.Save();
                }
            }
            Recomposite(token);
        });
    }

    // Comma-joined sorted set of the body types present in a snapshot (e.g. "bibo,gen2,gen3"),
    // matching the format of _lastCompositedBodyType. Used to detect when a body-mod change has
    // actually landed in the loaded materials.
    /// <summary>Deep copy, so a binding's gear override never mutates the mod's own metadata objects.</summary>
    private static OverlayDescriptor CloneDescriptor(OverlayDescriptor d)
        => JsonSerializer.Deserialize<OverlayDescriptor>(JsonSerializer.Serialize(d))!;

    // The second skin cuts each slot's shell from the gear MODEL the character is drawing there (whose
    // exposed skin is posed to fit the gear) instead of the flat bare body. Detect those models straight
    // from the loaded .mdl resources — e.g. chara/equipment/e6039/model/c0201e6039_sho.mdl → sho. Read
    // the model, NOT the material: the model loads reliably even when a gear piece's materials fail
    // (some mods ship a material that FailedSubResource), so a material scan would miss it. Bare slots
    // (e0000) are omitted, so the shell falls back to the bare-body default there.
    private static readonly System.Text.RegularExpressions.Regex EquipModelRe = new(
        @"chara/equipment/(e\d+)/model/c\d+e\d+_(top|dwn|glv|sho)\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // "_met" is BOTH the head-equipment slot and the Dawntrail facewear/glasses bonus item — the game
    // renders glasses through this same head-equipment path (Penumbra: BonusItemFlag.Glasses ToSuffix
    // "met", model index 16). Because a helmet and glasses can be worn together, several "_met" models can
    // be loaded at once, so these are collected as a SORTED LIST rather than squeezed into the by-suffix
    // part map (where one would clobber the other in hash order, flipping the shell's host between
    // composites and forcing a rebuild + full redraw each time).
    private static readonly System.Text.RegularExpressions.Regex MetModelRe = new(
        @"chara/equipment/(e\d+)/model/c\d+e\d+_met\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<string> EquippedMetModelsFromModels(HashSet<string>? modelPaths, HashSet<int>? facewearSets)
    {
        var met = new List<string>();
        if (modelPaths == null) return met;
        foreach (var p in modelPaths)
        {
            var match = MetModelRe.Match(p);
            if (!match.Success) continue;
            if (string.Equals(match.Groups[1].Value, "e0000", StringComparison.OrdinalIgnoreCase)) continue;
            // Head equipment (helmets/hats) and facewear glasses share the "_met" path but are different
            // slots. Only facewear can host the shell — a head item (e.g. the Emperor's New Hat) must be
            // ignored so the shell rides the facewear slot (real glasses or our synthesized invisible pair)
            // instead. facewearSets == null (sheet not readable yet) means don't filter, so we never drop
            // a real host by mistake before the game data is up.
            if (facewearSets != null
                && int.TryParse(match.Groups[1].Value.AsSpan(1), out var set)
                && !facewearSets.Contains(set))
                continue;
            met.Add(match.Value);
        }
        met.Sort(StringComparer.OrdinalIgnoreCase);   // stable order — the host must not flip run to run
        return met;
    }

    private static Dictionary<string, string> EquippedPartModelsFromModels(HashSet<string>? modelPaths)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (modelPaths == null) return models;
        foreach (var p in modelPaths)
        {
            var match = EquipModelRe.Match(p);
            if (!match.Success) continue;
            if (string.Equals(match.Groups[1].Value, "e0000", StringComparison.OrdinalIgnoreCase)) continue;
            models[match.Groups[2].Value.ToLowerInvariant()] = match.Value;
        }
        return models;
    }

    // The second skin appends its shell into a ring/bracelet the player already wears (so the accessory
    // stays visible), keyed by slot — chara/accessory/a0114/model/c0201a0114_rir.mdl → rir. Detect them
    // the same way as the equipment models above.
    private static readonly System.Text.RegularExpressions.Regex AccessoryModelRe = new(
        @"chara/accessory/(a\d+)/model/c\d+a\d+_(rir|ril|wrs|nek)\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static Dictionary<string, string> EquippedAccessoryModelsFromModels(HashSet<string>? modelPaths)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (modelPaths == null) return models;
        foreach (var p in modelPaths)
        {
            var match = AccessoryModelRe.Match(p);
            if (!match.Success) continue;
            models[match.Groups[2].Value.ToLowerInvariant()] = match.Value;
        }
        return models;
    }

    // ── Invisible auto-glasses (opt-in) ──────────────────────────────────────────
    // Ownership is derived from state, not a flag: the injected glasses are "ours" exactly when the head
    // "_met" slot currently holds our chosen invisible item's model set (InvisibleGlasses.Resolve). That
    // needs no re-assert bookkeeping — a design that reverts our ApplyFlag.Once glasses simply leaves the
    // slot empty, and the next recomposite re-injects.

    // Don't re-equip the invisible pair within this window: the recomposite we trigger right after an inject
    // may run before the game has loaded the model, leaving "met" still empty — without the guard that would
    // inject → recomposite → inject → … forever.
    private const int GlassesInjectCooldownMs = 5000;
    private long _lastGlassesInjectTick;

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

    private bool IsOurGlassesWorn(int ourSet) => CurrentMetSets().Contains(ourSet);

    /// <summary>
    /// Keep the invisible-glasses injection in line with the current composite. When the feature is on and a
    /// shell is being built but NO glasses/helmet are worn, have Glamourer equip our invisible pair so the
    /// shell can ride the facewear slot (captured as "_met" and hosted like any worn glasses). Pull OUR
    /// glasses back off (identified by set, so the player's own are never touched) only when the feature is
    /// off or there is genuinely nothing to host — NOT when a composite merely failed to produce a shell,
    /// which is transient and would otherwise unequip/re-equip on every retry. Idempotent: we only write to
    /// Glamourer when the slot's occupant must change. Framework-thread game write.
    /// </summary>
    /// <param name="gearWanted">Gear overlays are active, so a shell is supposed to exist.</param>
    /// <param name="shellBuilt">A shell actually built this composite.</param>
    /// <param name="alreadyHosted">
    /// The shell just built is already hosted on the pair we are about to equip, so the redirect is live
    /// and the equip's own redraw shows the finished result — no follow-up recomposite needed.
    /// </param>
    private void ReconcileInvisibleGlasses(bool gearWanted, bool shellBuilt, bool alreadyHosted = false)
    {
        if (InvisibleGlasses.Resolve(Plugin.DataManager, log) is not { } g) return;
        bool want = config.AutoInvisibleGlasses && shellBuilt;

        if (want)
        {
            // Inject only when the facewear slot is empty; if any glasses are already worn (ours or the
            // player's own), leave them — the host appends onto whatever is there.
            //
            // The composite that injects has ALREADY picked its host (a ring — the slot was empty when it
            // chose), so the carrier glasses would render un-redirected, and visible, until something else
            // happened to recomposite. Trigger one now so the shell re-hosts onto the glasses and REPLACEs
            // their model. The tick guard stops a re-inject if that composite runs before the game has
            // loaded/captured the model (met still null), which would otherwise ping-pong.
            if (!AnyMetWorn()
                && unchecked(Environment.TickCount64 - _lastGlassesInjectTick) > GlassesInjectCooldownMs
                && SetGlassesOnFramework(g.ItemId))
            {
                _lastGlassesInjectTick = Environment.TickCount64;
                if (alreadyHosted)
                {
                    // The composite that just finished already built and redirected the shell onto this
                    // pair, so the equip's redraw lands on it directly — recompositing would only redo
                    // identical work and force a second redraw.
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
        else if ((!config.AutoInvisibleGlasses || !gearWanted) && IsOurGlassesWorn(g.ModelSet))
        {
            // Feature off, or there is genuinely nothing to host (no gear overlays at all) — take OUR pair
            // back off. Deliberately NOT keyed on shellBuilt: a composite that merely failed to produce a
            // shell is transient, and unequipping there would flip the glasses off and straight back on.
            if (SetGlassesOnFramework(0))
                log.Information("[Proteus] invisible glasses: removed our injected glasses (e{0:D4})", g.ModelSet);
        }
    }

    private bool SetGlassesOnFramework(ulong itemId)
    {
        try { return Plugin.Framework.RunOnFrameworkThread(() => glamourer.SetGlasses(itemId)).GetAwaiter().GetResult(); }
        catch (Exception ex) { log.Warning("[Proteus] invisible glasses: SetGlasses({0}) failed: {1}", itemId, ex.Message); return false; }
    }

    /// <summary>Remove our injected glasses immediately (plugin disable/unload), if the worn pair is ours.
    /// Identified by set so the player's own glasses are never touched. Best-effort, framework thread.</summary>
    public void RemoveInjectedGlasses()
    {
        if (InvisibleGlasses.Resolve(Plugin.DataManager, log) is { } g && IsOurGlassesWorn(g.ModelSet))
            SetGlassesOnFramework(0);
    }

    // Stable string of the equipped part + accessory + head("_met") models, for cheap change detection on
    // redraw. A ring swap must rebuild the shell (it changes the host), so the accessory map is folded in
    // too — as is the "_met" list, since putting on/removing glasses or a helmet also changes the host.
    private static string EquipSignature(
        IReadOnlyDictionary<string, string>? models, IReadOnlyDictionary<string, string>? accessories = null,
        IReadOnlyList<string>? metModels = null)
        => string.Join("|",
            (models ?? new Dictionary<string, string>()).Concat(accessories ?? new Dictionary<string, string>())
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .Concat((metModels ?? []).Select(p => $"met={p}")));

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

    // ── Core compositor ──────────────────────────────────────────────────────

    private void Recomposite(CancellationToken ct)
    {
        try
        {
            log.Debug("[Proteus] Recomposite START");

            // Phase instrumentation: the counters below are reset here and reported in a single
            // summary line after the composite loop, so a slow run can be attributed to a stage
            // (decode / remap / blend / swizzle / write) instead of guessed at.
            var tRunStart = PhaseCounter.Begin();
            textureLoader.ResetStats();
            uvRemap.RemapStats.Reset();

            EnsureManagedModExists();

            // Delete previously written files BEFORE clearing redirects so that
            // File.Exists checks fail even if the Penumbra IPC reload is asynchronous.
            // This prevents us from loading our own stale output as the base texture.
            // Second-skin files (ss_*, models/) are deliberately NOT deleted: they're compared against
            // the new build to decide whether the shell actually changed, and a changed shell is what
            // forces a full redraw. Wiping them first would make every run look like a change.
            // They're never read back as a compositing source, so a stale one is harmless.
            var texturesDirEarly  = Path.Combine(managedModDir, "textures");
            var materialsDirEarly = Path.Combine(managedModDir, "materials");
            if (Directory.Exists(texturesDirEarly))
                foreach (var f in Directory.GetFiles(texturesDirEarly, "*.tex"))
                    if (!Path.GetFileName(f).StartsWith("ss_", StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(f); } catch { }
            if (Directory.Exists(materialsDirEarly))
                foreach (var f in Directory.GetFiles(materialsDirEarly, "*.mtrl"))
                    if (!Path.GetFileName(f).StartsWith("ss_", StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(f); } catch { }

            // Clear redirects and reload. Penumbra's IPC reload may process asynchronously
            // on the game main thread, so sleep briefly to let it take effect before any
            // ResolvePlayer calls that determine which mod's file is the upstream source.
            WriteManagedModJson(new Dictionary<string, string>());
            penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
            Thread.Sleep(80);

            // Discover ALL sidecar mods (incl. disabled) so the UI can list and re-enable them;
            // composite only the ones enabled in Penumbra, in priority order.
            var allEntries = discovery.DiscoverAll();
            if (ct.IsCancellationRequested) return;

            LastDiscovered = allEntries;

            var entries = allEntries.Where(e => e.Enabled).OrderBy(e => e.Priority).ToList();
            CheckManagedModHealth(entries);

            if (entries.Count == 0)
            {
                WriteManagedModJson(new Dictionary<string, string>());

                // A shell hosted on an accessory redirected that accessory's .mdl; an in-place reload won't
                // reload it, so dropping to zero enabled mods must force a FULL redraw or the shell lingers
                // on the accessory (same reasoning as the plugin-disable path). Clear the host tracking too,
                // so the next shell build compares against an empty set. This early return skips the normal
                // reset/drop-detection at the end of the method, hence doing it explicitly here.
                if (_secondSkinActive) _needFullRedraw = true;
                _secondSkinActive = false;
                _lastShellHostPaths = new(StringComparer.OrdinalIgnoreCase);

                // Nothing is referenced now, so remove every ss_*/model/material orphan too (the up-front
                // cleanup keeps ss_ files; with all mods off, none should survive).
                PruneManagedOutput(new Dictionary<string, string>());

                // Nothing is hosted, and the line above just deleted the redirect that renders our
                // invisible-glasses carrier as the shell. Leaving it equipped would put a REAL pair of
                // glasses on the player's face that they never chose, so take it off before the redraw.
                // This early return skips the reconcile at the end of the method, hence the explicit call.
                ReconcileInvisibleGlasses(gearWanted: false, shellBuilt: false);

                ReloadAndRedraw();
                LastResult = new CompositorResult { Success = true, TexturesPatched = 0, OverlayModsUsed = 0 };
                ResultChanged?.Invoke();
                return;
            }

            // Flatten: (entry, resolvedOverlay) pairs, grouped by material game path
            var byMaterial = new Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>>(
                StringComparer.OrdinalIgnoreCase);

            var colorOverride = _colorOverride; // snapshot the volatile reference for this run
            var gearOverride  = _gearOverride;

            // The mod's shared "Masks" colorset, with the active design binding's override applied when it
            // has one — so mask colours are captured/restored per-design like the overlay colorsets are
            // (see OverlayColorOverride.Mask). Falls back to the live metadata mask rows otherwise.
            List<ColorTableRowPreset>? MaskRowsFor(OverlayEntry e)
            {
                if (colorOverride != null && colorOverride.TryGetValue(e.ModDirectory, out var ov)
                    && ov.Mask is { Count: > 0 } m)
                    return m;
                return e.Metadata.MaskColorTableRows;
            }

            // The Masks tab's effective render-mode descriptor: the metadata MaskDescriptor with the active
            // binding's mask gear override applied (so a design captures/restores the mask's Cloth/Glow mode),
            // or null when the mask is plain Skin (bakes into the diffuse). Mirrors MaskRowsFor.
            OverlayDescriptor? MaskDescriptorFor(OverlayEntry e)
            {
                var baseDesc = e.Metadata.MaskDescriptor;
                var ovr = gearOverride != null && gearOverride.TryGetValue(e.ModDirectory, out var g)
                    ? g.Mask : null;
                if (baseDesc == null && ovr == null) return null;
                var desc = baseDesc != null ? CloneDescriptor(baseDesc) : new OverlayDescriptor();
                ovr?.ApplyTo(desc);
                return desc;
            }

            // Mod-wide stack position of an overlay (0 = top), from the active design binding's stack
            // override when it has one, else the global config order. Mirrors the mask/colour overrides so
            // a design captures/restores its tab arrangement without mutating the global stack config.
            var stackOverride = _stackOverride; // snapshot the volatile reference for this run
            int ModStackIndexFor(string modDir, string group, string option)
                => stackOverride != null && stackOverride.TryGetValue(modDir, out var order)
                    ? Configuration.ModStackIndexIn(order, group, option)
                    : config.ModStackIndexOf(modDir, group, option);

            // Gear overlays don't composite into a skin material — each becomes its own second-skin
            // shell with its own material and shader. Collect them separately.
            var gearOverlays = new List<(OverlayEntry Entry, ResolvedOverlay Overlay)>();
            // Every active overlay of every mod, both layers — the second-skin builder ranks groups
            // across layers (a skin group can outrank a gear group), so it needs the full picture.
            var allOverlays = new List<(OverlayEntry Entry, ResolvedOverlay Overlay)>();

            foreach (var entry in entries)
            {
                var overlays = discovery.ResolveActiveOverlays(entry);

                // A restored design binding overrides metadata colors in-memory (metadata.json is
                // never written). Replace each overlay's rows with the binding's, falling back to
                // the live metadata colors when the binding has none for that overlay.
                if (colorOverride != null && colorOverride.TryGetValue(entry.ModDirectory, out var ovr))
                    overlays = overlays
                        .Select(o => o with { ColorTableRows = ovr.Resolve(o.OptionGroup, o.Option) ?? o.ColorTableRows })
                        .ToList();

                // Same for the gear settings, onto a COPY of the descriptor — the binding must not write
                // through to metadata.json.
                if (gearOverride != null && gearOverride.TryGetValue(entry.ModDirectory, out var gOvr))
                    overlays = overlays
                        .Select(o =>
                        {
                            var gs = gOvr.Resolve(o.OptionGroup, o.Option);
                            if (gs == null) return o;
                            var copy = CloneDescriptor(o.Descriptor);
                            gs.ApplyTo(copy);
                            return o with { Descriptor = copy };
                        })
                        .ToList();

                // Stack rank of an overlay, top-first, matching the tab strip / composite sort keys
                // (ModStackIndexOf, GroupOrder, StackIndexOf). Lower tuple = higher in the stack.
                var modDir = entry.ModDirectory;
                (int, int, int) Rank(ResolvedOverlay o) => (
                    ModStackIndexFor(modDir, o.OptionGroup ?? "", o.Option ?? ""),
                    o.GroupOrder,
                    config.StackIndexOf(modDir, o.OptionGroup ?? "", o.Option ?? ""));

                // The deepest (lowest) gear overlay in this mod. A skin overlay stacked ABOVE it can't render
                // over the shell as skin, so — when it's on auto (not pinned) — promote it to a gear shell
                // (character.shpk). Reverts on its own once dragged back below all gear (rank recomputed each
                // composite). Pinned skin stays skin.
                (int, int, int)? lowestGear = null;
                foreach (var o in overlays)
                    if (o.Descriptor.Layer == OverlayLayer.Gear)
                    {
                        var r = Rank(o);
                        if (lowestGear == null || r.CompareTo(lowestGear.Value) > 0) lowestGear = r;
                    }

                foreach (var overlay in overlays)
                {
                    var ov = overlay;
                    if (overlay.Descriptor.Layer == OverlayLayer.Skin
                        && !overlay.Descriptor.ManualShaderLock
                        && lowestGear.HasValue && Rank(overlay).CompareTo(lowestGear.Value) < 0)
                    {
                        var promoted = CloneDescriptor(overlay.Descriptor);
                        promoted.Layer = OverlayLayer.Gear;   // ShaderPackage → character.shpk
                        ov = overlay with { Descriptor = promoted };
                    }

                    allOverlays.Add((entry, ov));
                    if (ov.Descriptor.Layer == OverlayLayer.Gear)
                    {
                        gearOverlays.Add((entry, ov));
                        continue;
                    }

                    foreach (var mtrlPath in ov.Descriptor.MaterialGamePaths)
                    {
                        if (string.IsNullOrEmpty(mtrlPath)) continue;
                        if (!byMaterial.TryGetValue(mtrlPath, out var list))
                            byMaterial[mtrlPath] = list = new();
                        list.Add((entry, ov));
                    }
                }
            }

            // Drop materials the player doesn't currently have loaded.
            // Equipment/accessory materials: filtered by exact path (authoritative).
            // Body-type materials: the snapshot is captured before a body-type switch takes effect,
            // so the new body's paths won't appear in the exact set yet. Strategy:
            //   - Collect ALL body types present in the snapshot (there may be several: the character
            //     can simultaneously have a bibo top body, vanilla leg body, etc.).
            //   - If a body material's type IS in the snapshot but its exact path is not → filter
            //     (the type is active for a different race/body code, not this one).
            //   - If a body material's type is NOT in the snapshot at all → keep (mid-switch: the
            //     new body type hasn't appeared yet; post-redraw check will clean up if needed).
            var activeMtrl = _activeMtrlSnapshot;
            {
                var activeBodyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (activeMtrl != null)
                    foreach (var m in activeMtrl)
                    {
                        var bt = UVRemapService.InferBodyType(m);
                        if (bt != null) activeBodyTypes.Add(bt);
                    }

                _lastCompositedBodyType = activeBodyTypes.Count > 0
                    ? string.Join(",", activeBodyTypes.OrderBy(x => x))
                    : null;

                if (activeMtrl != null)
                {
                    // Collect the active character codes (e.g. "c0101") from body materials in the
                    // snapshot. Used below to filter wrong-race materials in the mid-switch branch,
                    // and stored for the post-redraw race-change check.
                    var activeCharCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in activeMtrl)
                        if (UVRemapService.InferBodyType(m) != null)
                        {
                            var code = ExtractHumanCharCode(m);
                            if (code != null) activeCharCodes.Add(code);
                        }
                    _lastCompositedCharCodes = activeCharCodes.Count > 0
                        ? string.Join(",", activeCharCodes.OrderBy(x => x))
                        : null;

                    // Glamourer may be displaying a different race than the draw object uses
                    // (GetGameObjectResourcePaths returns the actual race, not the visual override).
                    // If Glamourer's char code differs from the snapshot, use it as the sole
                    // effective char code so overlays authored for the displayed race are kept.
                    string? glamCode = _glamourerCharCode;
                    bool glamOverride = glamCode != null && !activeCharCodes.Contains(glamCode);
                    var effectiveCharCodes = glamOverride
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { glamCode! }
                        : activeCharCodes;
                    if (glamOverride)
                        log.Debug("[Proteus] Glamourer race override: snapshot={0} → displayed={1}",
                            _lastCompositedCharCodes ?? "none", glamCode!);

                    foreach (var key in byMaterial.Keys.Where(k => !activeMtrl.Contains(k)).ToList())
                    {
                        var keyBodyType = UVRemapService.InferBodyType(key);
                        if (keyBodyType != null)
                        {
                            var keyCharCode = ExtractHumanCharCode(key);
                            if (activeBodyTypes.Contains(keyBodyType))
                            {
                                // Body type is active. Keep if the char code matches the effective
                                // race (handles Glamourer display override — actual snapshot has a
                                // different char code, but the overlay is authored for glamCode).
                                if (keyCharCode != null && effectiveCharCodes.Count > 0
                                    && effectiveCharCodes.Contains(keyCharCode))
                                    continue; // keep
                                log.Debug("[Proteus] Skipping body material (active types={0}): {1}",
                                    _lastCompositedBodyType ?? "none", key);
                                byMaterial.Remove(key);
                            }
                            else
                            {
                                // Body type absent — could be mid-switch to a new body type.
                                // Only keep the mid-switch heuristic for the effective race.
                                if (keyCharCode != null && effectiveCharCodes.Count > 0
                                    && !effectiveCharCodes.Contains(keyCharCode))
                                {
                                    log.Debug("[Proteus] Skipping body material (wrong race): {0}", key);
                                    byMaterial.Remove(key);
                                }
                                // else: same race, body type absent → keep (mid body-type switch)
                            }
                        }
                        else
                        {
                            log.Debug("[Proteus] Skipping non-equipped material: {0}", key);
                            byMaterial.Remove(key);
                        }
                    }
                }
            }

            // Sibling synthesis: a mod's overlay entries are automatically applied to the other
            // body-type materials the character currently has loaded, when those materials have no
            // direct overlay entries. This handles the common case — overlays are authored for one
            // UV space (e.g. bibo), but the character equips a different body (gen3, Eve, vanilla).
            // No metadata.json change required; UV remap fires automatically from the descriptor's
            // source body type. The cross-UV bake (bibo↔gen3/Eve) runs for any mode except Off;
            // vanilla (gen2) is opt-in per mod (All bodies only). gen2 is never a source (vanilla
            // is a terminal target and has no outbound transfer maps).
            if (activeMtrl != null)
            {
                var siblings = new Dictionary<string, List<(OverlayEntry, ResolvedOverlay)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (srcPath, pairs) in byMaterial)
                {
                    var srcType = UVRemapService.InferBodyType(srcPath);
                    if (srcType is null or "gen2") continue;

                    var srcSuffix = BodySuffixes.First(s => srcPath.EndsWith(s.Suffix, StringComparison.OrdinalIgnoreCase)).Suffix;
                    var stem = srcPath[..^srcSuffix.Length];

                    foreach (var (suffix, bodyType) in BodySuffixes)
                    {
                        if (suffix == srcSuffix) continue;
                        var dstPath = stem + suffix;
                        // A sibling body-type material is a synthesis target only if it's actually
                        // loaded on the character (its suffix appears in the active-material snapshot).
                        // The snapshot must be settled for this to be correct — see the post-composite
                        // re-verification in SchedulePostRedrawBodyTypeCheck.
                        if (!activeMtrl.Contains(dstPath))
                            continue;

                        bool vanilla = bodyType == "gen2";
                        var dstPairs = pairs.Where(p => vanilla
                            ? config.SiblingModeFor(p.Entry.ModDirectory) == SiblingSynthesisMode.AllBodies
                            : config.SiblingModeFor(p.Entry.ModDirectory) != SiblingSynthesisMode.Off).ToList();
                        if (dstPairs.Count == 0) continue;

                        // Name the mod/option(s) driving this sibling — and their sibling mode — so any
                        // "why is it baking to <body> when I have <X> equipped/nothing equipped?" can be
                        // traced to the exact mod whose mode to change. The destination body type is
                        // always tagged (gen3/eve/gen2), so a gen3 item pulling in a gen3 sibling is as
                        // legible as the vanilla case (vanilla only fires for mods set to All bodies).
                        var contributors = string.Join(", ", dstPairs
                            .Select(p => p.Overlay.Option != null
                                ? $"\"{p.Entry.ModName}\"/{p.Overlay.OptionGroup}:{p.Overlay.Option} [{config.SiblingModeFor(p.Entry.ModDirectory)}]"
                                : $"\"{p.Entry.ModName}\" [{config.SiblingModeFor(p.Entry.ModDirectory)}]")
                            .Distinct());
                        log.Debug("[Proteus] Sibling synthesis ({0}): {1} → {2} (from {3})",
                            vanilla ? "gen2/vanilla" : bodyType, srcPath, dstPath, contributors);
                        if (siblings.TryGetValue(dstPath, out var existSiblings))
                            existSiblings.AddRange(dstPairs);
                        else
                            siblings[dstPath] = dstPairs;
                    }
                }
                foreach (var (path, pairs) in siblings)
                {
                    if (byMaterial.TryGetValue(path, out var existing))
                        existing.AddRange(pairs);
                    else
                        byMaterial[path] = pairs;
                }
            }

            if (ct.IsCancellationRequested) return;

            var texturesDir  = texturesDirEarly;
            var materialsDir = materialsDirEarly;
            Directory.CreateDirectory(texturesDir);

            var redirects = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int texturesPatched = 0;
            // Accumulated across the parallel per-material loop; published to _skinGlowTargets after it.
            var skinGlow = new ConcurrentDictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>();

            // Unique suffix for all output files in this composite run. FFXIV caches textures
            // by their resolved path; using the same filename across runs means the game never
            // reloads the file even after the content changes. A new suffix each run guarantees
            // Penumbra sees a genuinely different redirect path → forces a cache miss.
            var runId = Guid.NewGuid().ToString("N")[..8];

            // Per-mod transparency masks (the "Masks" convention): a Penumbra multi-select group
            // named "Masks" whose selected options each load a grayscale PNG from Proteus/Masks/.
            // These reduce the coverage of every overlay in the same mod. Resolve the active mask
            // files per mod once (Penumbra IPC), then combine + cache the keep-map per (mod, size)
            // for the duration of this run.
            var maskPathsByMod = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var masks = discovery.ResolveActiveMasks(entry);
                if (masks.Count > 0) maskPathsByMod[entry.ModDirectory] = masks;
            }
            var combinedMaskCache = new ConcurrentDictionary<(string mod, int w, int h, string bodyType), (byte[] W, byte[] T)?>();

            // Masks whose exported layer also produced a companion relief normal and/or color-row
            // index (see proteus_packager.py's Masks-group export) — resolved once per mod, same as
            // maskPathsByMod above. Only mods with at least one such companion appear here.
            var maskAssetsByMod = new Dictionary<string, List<(string MaskPath, string? NormalPath, string? IndexPath)>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var assets = discovery.ResolveActiveMaskAssets(entry)
                    .Where(a => a.NormalPath != null || a.IndexPath != null).ToList();
                if (assets.Count > 0) maskAssetsByMod[entry.ModDirectory] = assets;
            }

            // ── Body materials for a gear-only / masks-only look ─────────────
            // byMaterial is built from SKIN-layer overlays alone — a Gear-layer overlay is diverted into
            // gearOverlays before it can add a material. So with no skin overlay the body material never
            // enters the set, the composite loop never visits it, and the AO/Skindenting block inside that
            // loop never runs: straps rendered, skin beneath them flat.
            //
            // But AO and the indent are cast BY the garment ONTO the skin, so they belong on the body
            // material whether or not any mod paints there. Add the character's own body materials with an
            // EMPTY overlay list; the AO pass reads gearOverlays/maskPathsByMod directly, not `pairs`, so
            // an empty list costs nothing and changes nothing for materials already present.
            //
            // Two gates. The effect must be on globally AND opted in for at least one mod that actually
            // contributes gear or masks — the same test the loop itself applies, so the two can't disagree.
            // And a body that is NOT the caster's own is a sibling, so it obeys that mod's sibling-synthesis
            // setting: without that, a mod set to Off still got its shadow baked onto the vanilla body the
            // user had excluded, and paid two ~16 MB writes to do it.
            if ((config.AmbientOcclusionStrength > 0f || config.AmbientOcclusionNormalDepth > 0f)
                && activeMtrl != null)
            {
                // Each caster with the body type(s) it is authored for, so a SIBLING body can be judged
                // against that mod's sibling-synthesis setting below.
                var casters = entries
                    .Where(e => (gearOverlays.Any(g => string.Equals(g.Entry.ModDirectory, e.ModDirectory, StringComparison.OrdinalIgnoreCase))
                                 || maskPathsByMod.ContainsKey(e.ModDirectory))
                                && config.AmbientOcclusionEnabledFor(e.ModDirectory, e.Metadata?.AmbientOcclusion))
                    .Select(e => (
                        Mod: e.ModDirectory,
                        Types: allOverlays
                            .Where(o => string.Equals(o.Entry.ModDirectory, e.ModDirectory, StringComparison.OrdinalIgnoreCase))
                            .Select(o => o.Overlay.Descriptor.SourceBodyType
                                         ?? o.Overlay.Descriptor.MaterialGamePaths
                                              .Select(UVRemapService.InferBodyType).FirstOrDefault(t => t != null))
                            .Where(t => t != null)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)))
                    .ToList();

                if (casters.Count > 0)
                {
                    int added = 0;
                    foreach (var m in activeMtrl)
                    {
                        // BOTH predicates: InferBodyType alone also matches chara/weapon/…/obj/body/…,
                        // and ExtractHumanCharCode is what excludes it. Never overwrite a real list.
                        if (ExtractHumanCharCode(m) == null) continue;
                        var dstType = UVRemapService.InferBodyType(m);
                        if (dstType == null || byMaterial.ContainsKey(m)) continue;

                        // A caster's OWN body always qualifies. Any OTHER loaded body is a sibling, and
                        // touching it is the user's call — the same gate sibling synthesis applies above, so
                        // a mod set to Off doesn't get its shadow baked onto a body the user excluded.
                        bool vanilla = string.Equals(dstType, "gen2", StringComparison.OrdinalIgnoreCase);
                        if (!casters.Any(c => c.Types.Contains(dstType)
                                || (vanilla ? config.SiblingModeFor(c.Mod) == SiblingSynthesisMode.AllBodies
                                            : config.SiblingModeFor(c.Mod) != SiblingSynthesisMode.Off)))
                            continue;

                        byMaterial[m] = new();
                        added++;
                    }
                    if (added > 0)
                        log.Debug("[Proteus] AO: added {0} body material(s) with no skin overlay so the "
                                + "shadow/indent from gear or masks has somewhere to land", added);
                }
            }

            // The mod's single shared "Masks" colorset (the one Masks tab). When present, its active masks
            // are coloured by THESE rows via their combined _id in a top diffuse layer — instead of merging
            // each mask's _id into the overlays beneath (that merge is skipped for the mod; see
            // LoadIndexMerged). Absent ⇒ legacy behaviour (mask _id merges into each overlay's own colorset).
            var maskRowsByMod = new Dictionary<string, Dictionary<int, ColorTableRowOverride>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (MaskRowsFor(entry) is { Count: > 0 } mr)
                    maskRowsByMod[entry.ModDirectory] = BuildRowDict(mr);

            // Mods that will get a dedicated top mask SHELL: any mod with GEAR shells + mask _id/relief assets.
            // A mask colorset is OPTIONAL — with one the shell uses it, without one it inherits the fabric's
            // colours (see the shell synthesis). A mask mod with ONLY skin layers stays on the skin instead
            // (not here). For a shell mod the mask lives entirely on the shell (coverage, colour, relief), so
            // the skin diffuse/relief passes and LoadIndexMerged below MUST skip it — otherwise the same mask
            // lands on BOTH the body and the shell (two stacked surfaces = a doubled-height bump).
            var maskShellMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in gearOverlays.Select(g => g.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                if (maskAssetsByMod.TryGetValue(mod, out var mA)
                    && mA.Any(a => a.IndexPath != null || a.NormalPath != null))
                    maskShellMods.Add(mod);

            // Also promote an ALL-SKIN mod whose Masks tab was given its own Cloth/Glow mode
            // (MaskDescriptor.Layer == Gear): the mask becomes a dedicated shell even with no other gear
            // overlay to ride over. The synthesis below seeds it without a sibling gear overlay.
            foreach (var entry in entries)
                if (maskAssetsByMod.TryGetValue(entry.ModDirectory, out var mA2)
                    && mA2.Any(a => a.IndexPath != null || a.NormalPath != null)
                    && MaskDescriptorFor(entry)?.Layer == OverlayLayer.Gear)
                    maskShellMods.Add(entry.ModDirectory);

            // Composite order = list order; LAST lands on top. Across mods, Penumbra priority is preserved.
            //
            // Within a mod the user's tab strip decides, via the mod-wide stack (index 0 = top, so sort
            // descending; unlisted = int.MaxValue → bottom). That has to outrank GroupOrder: the per-group
            // stack below can only order options against others in the SAME group, so with Fabric and
            // Patterns both active the group number always won and one group sat on top no matter how the
            // tabs were arranged.
            //
            // GroupOrder and the old per-group stack remain as tiebreaks, so a mod nobody has restacked
            // still composites exactly as before, and per-group orders saved earlier keep working.
            //
            // Masks are NOT in this list — they come from maskPathsByMod / MaskAdds and still apply on top.
            foreach (var list in byMaterial.Values)
            {
                var sorted = list
                    .OrderBy(p => p.Entry.Priority)
                    .ThenByDescending(p => ModStackIndexFor(p.Entry.ModDirectory, p.Overlay.OptionGroup ?? "", p.Overlay.Option ?? ""))
                    .ThenByDescending(p => p.Overlay.GroupOrder)
                    .ThenByDescending(p => config.StackIndexOf(p.Entry.ModDirectory, p.Overlay.OptionGroup ?? "", p.Overlay.Option ?? ""))
                    .ToList();
                list.Clear();
                list.AddRange(sorted);

                // Bottom-to-top composite order for this material, with the sort keys. Debug-level: kept
                // for diagnosing "wrong overlay on top" (mod= is the per-mod tab-stack index, grp= the
                // Penumbra group ordinal), without spamming a normal log.
                if (sorted.Count > 1)
                {
                    var parts = sorted.Select(p =>
                    {
                        var g = p.Overlay.OptionGroup ?? "";
                        var o = p.Overlay.Option ?? "";
                        var mi = FmtIdx(ModStackIndexFor(p.Entry.ModDirectory, g, o));
                        return $"{g}/{o}[mod={mi},grp={p.Overlay.GroupOrder}]";
                    });
                    log.Debug("[Proteus] skin stack (bottom->top): {0}", string.Join("  ->  ", parts));
                }
            }

            // A mask OCCLUDES everything beneath it. In a mask's territory every overlay group — including the
            // mod's highest-priority one — is erased to skin (coverage = cov·W, and W=0 where the mask is
            // opaque), so only the mask's own colorset renders there. Lowering the mask's opacity then fades
            // it toward BARE SKIN, never revealing the layers underneath. (Masks no longer ADD coverage to a
            // sheer top group — a mask is a garment in its own right, not a coverage patch for another layer.)
            static bool MaskAdds(OverlayEntry e, ResolvedOverlay o) => false;

            // Decode a file on a background thread purely to warm the cache; callers never await it.
            // Marking the thread keeps its time out of the critical-path decode figure (see DecodeWaitStats).
            void WarmBg(string path, int w, int h)
                => Task.Run(() =>
                {
                    TextureLoader.BackgroundPrefetch = true;
                    try { textureLoader.LoadPngAsRgba(path, w, h); }
                    catch { }
                    finally { TextureLoader.BackgroundPrefetch = false; }
                }, ct);

            // ── Cold-start prefetch ──────────────────────────────────────────
            // The blend loop cannot prefetch its own first overlays: overlay sizes follow the base
            // texture, and that isn't decoded until the loop is already running. Measured, that left the
            // first two overlays' diffuse/normal/index decoding serially on the composite thread —
            // ~1.3 s, the largest remaining item once blend was parallelised.
            //
            // We do know the size in advance: LoadBaseTexture upscales anything smaller to
            // BaseTargetSize, so that is what the loop will ask for unless the base is larger than 4K.
            // Guessing wrong costs a wasted decode, never a wrong pixel — the loop still requests
            // whatever size it actually needs, and a mismatched entry is simply never read.
            const int coldSize = TextureLoader.BaseTargetSize;
            foreach (var list in byMaterial.Values)
                foreach (var (cEntry, cOverlay) in list.Take(PrefetchDepth))
                {
                    var cd = cOverlay.Descriptor;
                    if (cd.Diffuse != null) WarmBg(Path.Combine(cEntry.SidecarRoot, cd.Diffuse), coldSize, coldSize);
                    if (cd.Normal  != null) WarmBg(Path.Combine(cEntry.SidecarRoot, cd.Normal),  coldSize, coldSize);
                    if (cd.Index   != null) WarmBg(Path.Combine(cEntry.SidecarRoot, cd.Index),   coldSize, coldSize);
                }

            var tSetupEnd = PhaseCounter.Begin();

            Parallel.ForEach(byMaterial, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, kvp =>
            {
                var (mtrlGamePath, pairs) = kvp;

                // TextureLoader caches decoded PNGs across runs (keyed by path + mtime),
                // and its Lazy wrapper dedups concurrent requests for the same file.
                byte[]? LoadPng(string path, int w, int h) => textureLoader.LoadPngAsRgba(path, w, h);

                var dstBodyType = UVRemapService.InferBodyType(mtrlGamePath);
                byte[]? RemapIfNeeded(byte[]? png, int w, int h, string? srcType, string? overlayPath = null)
                {
                    if (png == null || srcType == null || dstBodyType == null) return png;
                    if (string.Equals(srcType, dstBodyType, StringComparison.OrdinalIgnoreCase)) return png;
                    // Any source → gen2 (vanilla): vanilla UV is the right half of bibo UV space.
                    // Convert to bibo first (via transfer map if needed), crop right half, resize.
                    if (string.Equals(dstBodyType, "gen2", StringComparison.OrdinalIgnoreCase))
                    {
                        if (overlayPath == null) return png;
                        var native = textureLoader.LoadPngAsRgba(overlayPath, 4096, 4096);
                        if (native == null) return png;
                        byte[] biboSpace;
                        if (string.Equals(srcType, "bibo", StringComparison.OrdinalIgnoreCase))
                        {
                            biboSpace = native;
                        }
                        else
                        {
                            var converted = uvRemap.Remap(native, 4096, 4096, srcType, "bibo");
                            if (ReferenceEquals(converted, native)) return png; // map not found — skip
                            biboSpace = converted;
                        }
                        var rightHalf = UVRemapService.CropRightHalf(biboSpace, 4096, 4096);
                        return UVRemapService.ResizeBilinear(rightHalf, 2048, 4096, w, h);
                    }
                    // Transfer-map paths operate at 4096×4096. If the overlay was loaded at a
                    // smaller size (e.g. base texture is 2048), reload at full res, remap, resize.
                    if (w != 4096 || h != 4096)
                    {
                        if (overlayPath == null) return png;
                        var native4k = textureLoader.LoadPngAsRgba(overlayPath, 4096, 4096);
                        if (native4k == null) return png;
                        var remapped4k = uvRemap.Remap(native4k, 4096, 4096, srcType, dstBodyType);
                        if (ReferenceEquals(remapped4k, native4k)) return png;
                        return UVRemapService.ResizeBilinear(remapped4k, 4096, 4096, w, h);
                    }
                    return uvRemap.Remap(png, w, h, srcType, dstBodyType);
                }

                // Loads an overlay's Index texture, then replaces its per-pixel row selection
                // (R = row, G = subrow blend) with any active "Masks" option's own Index
                // companion wherever that mask has coverage — a hard swap (not a blend: R
                // encodes a row *id*, so interpolating it between two arbitrary rows would
                // select a meaningless third row) at alpha ≥ 50%. This makes a mask's Index
                // "win" over whatever the mod's other overlay(s) would otherwise select there,
                // using the exact same ColorTableRows the overlay's own Index already resolves
                // against — no separate per-mask colorset needed.
                byte[]? LoadIndexMerged(string idxPath, int w, int h, string? srcType, string modDir)
                {
                    var idx = RemapIfNeeded(LoadPng(idxPath, w, h), w, h, srcType, idxPath);
                    if (idx == null || !maskAssetsByMod.TryGetValue(modDir, out var assets)) return idx;

                    // This mod's masks are handled elsewhere, NOT merged into the overlay index here (merging
                    // AND painting/shelling would double them): a mask colorset ⇒ the separate top diffuse pass
                    // below; a mask SHELL mod ⇒ the shell owns the mask entirely.
                    if (maskRowsByMod.ContainsKey(modDir) || maskShellMods.Contains(modDir)) return idx;

                    // LoadPngAsRgba shares its cached array with read-only callers (see TextureLoader's
                    // mutation contract) — clone before writing into it, or a mask toggled off later
                    // still shows the swapped rows because the cache itself was corrupted.
                    idx = (byte[])idx.Clone();
                    foreach (var (maskPath, _, maskIndexPath) in assets)
                    {
                        if (maskIndexPath == null) continue;
                        var maskPng = RemapIfNeeded(LoadPng(maskPath, w, h), w, h, srcType, maskPath);
                        var maskIdx = RemapIfNeeded(LoadPng(maskIndexPath, w, h), w, h, srcType, maskIndexPath);
                        if (maskPng == null || maskIdx == null) continue;
                        for (int i = 0; i < idx.Length; i += 4)
                        {
                            if (maskPng[i + 3] < 128) continue;
                            idx[i]     = maskIdx[i];
                            idx[i + 1] = maskIdx[i + 1];
                        }
                    }
                    return idx;
                }

                // Combined coverage-mask for a mod's active masks at a given size, cached per run.
                // A mask SETS coverage opacity explicitly within its alpha region: its grayscale RGB
                // is the target opacity and its alpha is how strongly to apply it (white alpha = fully
                // set, black = no effect): cov' = lerp(cov, gray, a) = cov*(1-a) + gray*a. This is
                // additive — a white patch can force full opacity over a sheer area (but only where
                // the overlay already has some coverage; see ApplyCoverageMask's base-alpha gate).
                // Stored as W = Π(1-aᵢ) (how much original coverage survives) and T (accumulated
                // gray*a target); the apply step is cov' = cov*W + T, gated by base alpha > 0.
                // `paths` is ordered highest-priority-first and applied in reverse so the top-of-list
                // mask wins where masks overlap. Returns null when none active. W/T are bytes (0–255).
                (byte[] W, byte[] T)? CombinedMaskAt(string modDir, int w, int h, string? srcBodyType = null)
                {
                    if (!maskPathsByMod.TryGetValue(modDir, out var paths) || paths.Count == 0) return null;
                    var bodyKey = $"{srcBodyType ?? ""}→{dstBodyType ?? ""}";
                    return combinedMaskCache.GetOrAdd((modDir, w, h, bodyKey), _ =>
                    {
                        int n = w * h;
                        byte[]? wArr = null, tArr = null;
                        for (int pidx = paths.Count - 1; pidx >= 0; pidx--)
                        {
                            var m = textureLoader.LoadPngAsRgba(paths[pidx], w, h);
                            if (m != null) m = RemapIfNeeded(m, w, h, srcBodyType, paths[pidx]);
                            if (m == null) continue;
                            // Masks accumulate in order (outer loop), but within one mask every pixel is
                            // independent — so the inner pass parallelises exactly like the kernels below.
                            var src = m;
                            if (wArr == null)
                            {
                                var wNew = new byte[n];
                                var tNew = new byte[n];
                                ParallelPixels(0, n, 1, (fromPi, toPi) =>
                                {
                                    for (int pi = fromPi; pi < toPi; pi++)
                                    {
                                        int o = pi * 4;
                                        int a = src[o + 3];
                                        int g = (src[o] * 77 + src[o + 1] * 150 + src[o + 2] * 29) >> 8; // luminance
                                        wNew[pi] = (byte)(255 - a);       // (1-a)
                                        tNew[pi] = (byte)(g * a / 255);   // gray*a
                                    }
                                });
                                wArr = wNew;
                                tArr = tNew;
                            }
                            else
                            {
                                var wCur = wArr;
                                var tCur = tArr!;
                                ParallelPixels(0, n, 1, (fromPi, toPi) =>
                                {
                                    for (int pi = fromPi; pi < toPi; pi++)
                                    {
                                        int o = pi * 4;
                                        int a = src[o + 3];
                                        int g = (src[o] * 77 + src[o + 1] * 150 + src[o + 2] * 29) >> 8;
                                        int inv = 255 - a;
                                        // T' = T*(1-a) + gray*a ;  W' = W*(1-a)
                                        tCur[pi] = (byte)(tCur[pi] * inv / 255 + g * a / 255);
                                        wCur[pi] = (byte)(wCur[pi] * inv / 255);
                                    }
                                });
                            }
                        }
                        return wArr == null ? ((byte[] W, byte[] T)?)null : (wArr, tArr!);
                    });
                }

                // Garment silhouette for a mod at (w,h) in body UV: the union of its overlays' diffuse alpha
                // (GEAR shells AND skin-painted garments), remapped to the body UV. Lets a non-masked garment
                // — a cloth bralette (its shell's shape) OR a skin-painted one (e.g. "Ala Mhigan", whose straps
                // are just its diffuse) — cast an AO shadow / indent the same way a masked strap does. Diffuse
                // alpha is self-protecting: a garment opaque across the whole UV yields strap≈1 everywhere, so
                // halo = blur·(1−strap) → 0 (no false shadow); only real edges cast. Returns null if the mod
                // has no overlay with a diffuse. (allOverlays holds skin + gear; mask shells aren't in it.)
                byte[]? GarmentSilhouette(string modDir, int w, int h)
                {
                    byte[]? sil = null;
                    foreach (var (gEntry, gOverlay) in allOverlays)
                    {
                        if (!string.Equals(gEntry.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)) continue;
                        var gd = gOverlay.Descriptor;
                        if (gd.IsMaskShell || gd.Diffuse == null) continue;   // mask shells trace their mask, not this
                        // A SKIN overlay is baked into ONE material, so only trace it for the material being
                        // composited now — otherwise a face overlay (face UV) would remap into the body
                        // silhouette as garbage.
                        if (gd.Layer == OverlayLayer.Skin
                            && !gd.MaterialGamePaths.Contains(mtrlGamePath, StringComparer.OrdinalIgnoreCase))
                            continue;
                        // A GEAR shell isn't bound to a material, but its art still lives in BODY UV (the
                        // shell is cut from the body mesh) — so it describes a garment only on a body-UV
                        // material. Without this it traced onto the face/hair material too, sampling body
                        // coverage in face UV and scribbling AO shadows + Skindenting grooves over the face.
                        if (gd.Layer == OverlayLayer.Gear && !IsBodyUvMaterial(mtrlGamePath))
                            continue;

                        var gSrc = gd.SourceBodyType;
                        if (gSrc == null)
                        {
                            if (gd.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)))
                                gSrc = "bibo";
                            else if (gd.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3"))
                                gSrc = "gen3";
                        }
                        var dp = Path.Combine(gEntry.SidecarRoot, gd.Diffuse);
                        var img = RemapIfNeeded(LoadPng(dp, w, h), w, h, gSrc, dp);
                        if (img == null) continue;

                        sil ??= new byte[w * h];
                        var s = sil; var src = img;
                        ParallelPixels(0, w * h, 1, (from, to) =>
                        {
                            for (int p = from; p < to; p++)
                                if (src[p * 4 + 3] > s[p]) s[p] = src[p * 4 + 3];   // union of diffuse alpha
                        });
                    }
                    return sil;
                }

                if (ct.IsCancellationRequested) return;

                var mtrlDisk = penumbra.ResolvePlayer(mtrlGamePath);
                // RAW parse. The disk/game split this used to do by hand lives inside ResolveMtrlTexturesRaw
                // now, and more importantly it skips Lumina's typed MtrlFile — which misreads some Dawntrail
                // layouts. A modded material (older TexTools layout) read fine while the stock game file came
                // back empty, so an overlay targeting a VANILLA material hit the "no textures" bail below and
                // silently never composited.
                var texPaths = textureLoader.ResolveMtrlTexturesRaw(mtrlDisk, mtrlGamePath);

                if (texPaths.Diffuse == null && texPaths.Normal == null && texPaths.Mask == null)
                {
                    log.Warning("[Proteus] No textures found for material: {0}", mtrlGamePath);
                    return;
                }

                // If any entry in this material's stack uses emissive, the normal alpha must
                // start at 0 so that only overlay-covered pixels receive emissive intensity.
                // (BC5-decoded normals have alpha=255 everywhere; without this reset, the
                // entire material would glow when the emissive shader key is active.)
                bool anyEmissive = pairs.Any(p =>
                    p.Overlay.ColorTableRows?.Any(r =>
                        r.SubRowA?.Emissive > 0.001f || r.SubRowB?.Emissive > 0.001f) == true);

                byte[]? baseD = null, baseN = null, baseM = null;
                int wD = 0, hD = 0, wN = 0, hN = 0, wM = 0, hM = 0;

                // Captured per mod as the loop below runs, for the Masks-driven relief pass
                // afterwards (masks are mod-level, not tied to one overlay descriptor).
                var lastSrcBodyTypeByMod = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                // ── Higher-priority group claims ──────────────────────────────
                // A mod's groups are ranked by Penumbra's own numbering (group_002 beats group_003), and a
                // higher group wins wherever it is VISIBLE: a lower one is faded by the higher one's alpha.
                // Coverage drives every channel — CovAt gates the normal/mask/emissive phases — so fading
                // the alpha is also what stops a lower group's normal COMPOUNDING through an opaque higher
                // one (CompoundNormal is additive), which is the bug that flattened the leather cup.
                var claimCache = new Dictionary<(string Mod, int Group, int W, int H), byte[]?>();

                string? SrcTypeOf(OverlayDescriptor d)
                {
                    if (d.SourceBodyType != null) return d.SourceBodyType;
                    if (d.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)))
                        return "bibo";
                    if (d.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3"))
                        return "gen3";
                    return null;
                }

                // One overlay's effective coverage — art alpha, then the Masks group, then opacity.
                // Same rules CovAt applies, just for a different descriptor.
                byte[]? CoverageOf(OverlayEntry e, ResolvedOverlay o, int tw, int th)
                {
                    var d = o.Descriptor;
                    var srcT = SrcTypeOf(d);
                    byte[]? cov = null;

                    if (d.Diffuse != null)
                    {
                        var p = Path.Combine(e.SidecarRoot, d.Diffuse);
                        cov = RemapIfNeeded(LoadPng(p, tw, th), tw, th, srcT, p);
                    }
                    else if (d.Normal != null)
                    {
                        var p = Path.Combine(e.SidecarRoot, d.Normal);
                        var n = RemapIfNeeded(LoadPng(p, tw, th), tw, th, srcT, p);
                        if (n != null)
                        {
                            cov = new byte[n.Length];
                            for (int i = 0; i < n.Length; i += 4) cov[i + 3] = n[i + 2];   // blue → opacity
                        }
                    }
                    else if (d.Mask != null)
                    {
                        var p = Path.Combine(e.SidecarRoot, d.Mask);
                        cov = RemapIfNeeded(LoadPng(p, tw, th), tw, th, srcT, p);
                    }
                    if (cov == null) return null;

                    var msk = CombinedMaskAt(e.ModDirectory, tw, th, srcT);
                    if (msk != null) cov = ApplyCoverageMask(cov, msk.Value.W, msk.Value.T, MaskAdds(e, o));

                    var r = BuildRowDict(o.ColorTableRows);
                    if (d.Index != null && r.Values.Any(x => x.A.Opacity != 0 || x.B.Opacity != 0))
                    {
                        var ip = Path.Combine(e.SidecarRoot, d.Index);
                        var idx = LoadIndexMerged(ip, tw, th, srcT, e.ModDirectory);
                        if (idx != null) cov = ApplyIndexedOpacity(cov, idx, r);
                    }
                    else if (d.Index == null)
                    {
                        r.TryGetValue(15, out var r16);
                        int op = r16?.A.Opacity ?? 0;
                        if (op != 0) cov = ScaleOverlayAlpha(cov, op);
                    }
                    return cov;
                }

                // Union alpha of every same-mod overlay in a higher-priority group.
                byte[]? ClaimAt(string modDir, int groupOrder, int tw, int th)
                {
                    var key = (modDir, groupOrder, tw, th);
                    if (claimCache.TryGetValue(key, out var hit)) return hit;

                    byte[]? acc = null;
                    foreach (var (e, o) in pairs)
                    {
                        if (o.GroupOrder >= groupOrder) continue;
                        if (!string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)) continue;

                        var cov = CoverageOf(e, o, tw, th);
                        if (cov == null) continue;

                        acc ??= new byte[tw * th];
                        for (int i = 0, a = 3; i < acc.Length && a < cov.Length; i++, a += 4)
                            acc[i] = (byte)(acc[i] + (255 - acc[i]) * cov[a] / 255);   // alpha-over union
                    }
                    claimCache[key] = acc;
                    return acc;
                }

                // Fade a coverage buffer by what the higher groups already claim.
                byte[]? Suppress(byte[]? cov, OverlayEntry e, ResolvedOverlay o, int tw, int th)
                {
                    if (cov == null) return null;
                    var claim = ClaimAt(e.ModDirectory, o.GroupOrder, tw, th);
                    if (claim == null) return cov;

                    var dst = (byte[])cov.Clone();
                    for (int i = 0, a = 3; i < claim.Length && a < dst.Length; i++, a += 4)
                        dst[a] = (byte)(dst[a] * (255 - claim[i]) / 255);
                    return dst;
                }

                // Per-overlay glow row-maps for the live "glow" button, captured from the diffuse phase
                // below (reusing the merged index + the actual composite coverage) and finalized with the
                // diffuse disk path after it is written. Downsampled to bound memory (full 4K would be 16 MB
                // each); the highlighter nearest-samples it back up.
                const int glowMapCap = 1024;
                var glowMaps = new List<(string ModDir, string? Group, string? Option, byte[] Map, int W, int H)>();

                // ── Decode prefetch ───────────────────────────────────────────
                // Overlay decode dominates a recomposite (measured: 5.1 s of a 7.1 s composite, ~96 ms
                // per 4K file), and the loop below consumes each overlay's art strictly in turn, so those
                // decodes run one at a time on one thread. Warm the next few overlays on background
                // threads while the current one blends: the decode cache is thread-safe and its Lazy
                // wrapper dedups concurrent requests for the same file, so the loop simply finds the work
                // already done instead of waiting for it. The depth is bounded rather than prefetching
                // the whole material because each 4K entry is 64 MB — see DecodeCacheBudgetBytes, which
                // is sized to hold this window plus the base textures.

                // Overlay sizes follow the base textures, so this no-ops until the first base is loaded
                // (pair 0 establishes wD/hD and wN/hN); the pipeline is full from pair 1 onward.
                void PrefetchAhead(int fromIndex)
                {
                    for (int k = fromIndex; k < Math.Min(fromIndex + PrefetchDepth, pairs.Count); k++)
                    {
                        var pd = pairs[k].Overlay.Descriptor;
                        var root = pairs[k].Entry.SidecarRoot;
                        if (pd.Diffuse != null && wD > 0) WarmBg(Path.Combine(root, pd.Diffuse), wD, hD);
                        if (pd.Normal  != null && wN > 0) WarmBg(Path.Combine(root, pd.Normal),  wN, hN);
                        // The colour-row index map — measured at ~125 ms each and decoded once per overlay.
                        if (pd.Index   != null && wD > 0) WarmBg(Path.Combine(root, pd.Index),   wD, hD);

                        // The Masks group is read per mod by CombinedMaskAt, at the diffuse size.
                        // Several overlays usually share one mod's masks, so the cache dedups these.
                        var mod = pairs[k].Entry.ModDirectory;
                        if (wD > 0 && maskPathsByMod.TryGetValue(mod, out var mPaths))
                            foreach (var mp in mPaths) WarmBg(mp, wD, hD);

                        // ...and each mask's companion relief-normal / colour-index, read in the later
                        // passes of the same overlay. Warming the mask but not these left ~640 ms of the
                        // measured decode-wait on the composite thread.
                        if (wD > 0 && maskAssetsByMod.TryGetValue(mod, out var mAssets))
                            foreach (var a in mAssets)
                            {
                                if (a.NormalPath != null) WarmBg(a.NormalPath, wD, hD);
                                if (a.IndexPath  != null) WarmBg(a.IndexPath,  wD, hD);
                            }
                    }
                }

                int pairIndex = -1;
                foreach (var (entry, resolved) in pairs)
                {
                    if (ct.IsCancellationRequested) return;

                    PrefetchAhead(++pairIndex + 1);

                    var desc        = resolved.Descriptor;
                    var srcBodyType = desc.SourceBodyType;
                    // Infer the source UV space from the overlay's material paths when no explicit
                    // SourceBodyType is set — covers overlays authored before the SourceBodyType
                    // field existed and sibling-synthesised entries. gen3 covers both _b (body) and
                    // _eve (Eve, same UV as gen3); needed so cross-UV bakes from those sources remap.
                    if (srcBodyType == null)
                    {
                        if (desc.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)))
                            srcBodyType = "bibo";
                        else if (desc.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3"))
                            srcBodyType = "gen3";
                    }
                    var rows   = BuildRowDict(resolved.ColorTableRows);
                    rows.TryGetValue(15, out var row16);
                    var row16A = row16?.A ?? new ColorTableSubRow();

                    lastSrcBodyTypeByMod[entry.ModDirectory] = srcBodyType;

                    byte[]? diffuseOv = null;
                    byte[]? normalOv  = null;

                    // Coverage mask: the alpha of the diffuse overlay defines WHERE this overlay
                    // applies. When there is no diffuse overlay (normal-only), the mask is
                    // synthesized from the normal map's blue channel. Every compositing channel
                    // — diffuse, normal, emissive, mask texture — is gated by this same mask.
                    byte[]? covSrc = null;  // coverage source at (covW × covH)
                    int covW = 0, covH = 0;

                    // ── Step 1: load diffuse overlay (establishes coverage) ───
                    if (desc.Diffuse != null && texPaths.Diffuse != null)
                    {
                        if (baseD == null)
                        {
                            var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                            var loaded = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                            if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                            baseD ??= Array.Empty<byte>();
                        }
                        if (baseD.Length > 0)
                        {
                            var diffPath = Path.Combine(entry.SidecarRoot, desc.Diffuse);
                            diffuseOv = RemapIfNeeded(LoadPng(diffPath, wD, hD), wD, hD, srcBodyType, diffPath);
                            if (diffuseOv != null)
                            {
                                // All opacity (indexed and flat) is applied AFTER the Masks-group mask
                                // in the block below, so the user's transparency slider always scales
                                // the mask result rather than the mask overriding the slider.
                                covSrc = diffuseOv; covW = wD; covH = hD;
                            }
                        }
                    }

                    // ── Step 2: load normal overlay; synthesize coverage if needed ──
                    if (desc.Normal != null && texPaths.Normal != null)
                    {
                        if (baseN == null)
                        {
                            baseN = LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
                            if (anyEmissive && baseN.Length > 0)
                                for (int ai = 3; ai < baseN.Length; ai += 4) baseN[ai] = 0;
                        }
                        if (baseN.Length > 0)
                        {
                            var normPath = Path.Combine(entry.SidecarRoot, desc.Normal);
                            normalOv = RemapIfNeeded(LoadPng(normPath, wN, hN), wN, hN, srcBodyType, normPath);
                        }

                        if (normalOv != null && covSrc == null)
                        {
                            // No diffuse overlay — synthesize coverage from normal blue channel.
                            var synth = new byte[normalOv.Length];
                            var nOv = normalOv;
                            ParallelPixels(0, nOv.Length, 4, (fromSi, toSi) =>
                            {
                                for (int si = fromSi; si < toSi; si += 4)
                                {
                                    synth[si] = synth[si + 1] = synth[si + 2] = 255;
                                    synth[si + 3] = nOv[si + 2]; // blue → opacity
                                }
                            });
                            // Opacity (indexed and flat) deferred — CovAt applies it after the Masks-group mask.
                            diffuseOv = synth;
                            covSrc = synth; covW = wN; covH = hN;
                        }
                    }

                    // ── Step 3: mask-only overlay — coverage from the mask's own alpha ──
                    // No diffuse or normal to define the silhouette, so the mask overlay PNG's own
                    // alpha is the coverage. Without this, a mask-only overlay (e.g. a wetness multi
                    // map) has no covSrc and is skipped entirely below.
                    if (covSrc == null && desc.Mask != null && texPaths.Mask != null)
                    {
                        if (baseM == null)
                        {
                            var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(texPaths.Mask), texPaths.Mask);
                            if (loaded.HasValue) { baseM = loaded.Value.rgba; wM = loaded.Value.width; hM = loaded.Value.height; }
                            baseM ??= Array.Empty<byte>();
                        }
                        if (baseM.Length > 0)
                        {
                            var maskPath3 = Path.Combine(entry.SidecarRoot, desc.Mask);
                            var maskOv = RemapIfNeeded(LoadPng(maskPath3, wM, hM), wM, hM, srcBodyType, maskPath3);
                            if (maskOv != null)
                            {
                                // Flat opacity deferred — CovAt applies it after the Masks-group mask.
                                covSrc = maskOv; covW = wM; covH = hM;
                            }
                        }
                    }
                    else if (covSrc == null && desc.Mask != null && texPaths.Mask == null)
                    {
                        log.Warning("[Proteus] Mask-only overlay but material has no mask texture: {0}", mtrlGamePath);
                    }

                    if (covSrc == null) continue; // no coverage — nothing to composite

                    // ── Per-mod transparency masks + opacity ─────────────────
                    // diffuseOv is consumed directly by Phase A, so apply mask and opacity here.
                    // covSrc stays raw — CovAt applies the same sequence on every path so it stays
                    // consistent. Synth/mask-only coverage isn't used directly; those gate through CovAt.
                    // Order: Masks-group mask first, then opacity — so the user's transparency slider
                    // always scales the mask result rather than the mask overriding the slider.
                    if (desc.Diffuse != null && diffuseOv != null)
                    {
                        var msk = CombinedMaskAt(entry.ModDirectory, covW, covH, srcBodyType);
                        if (msk != null)
                            diffuseOv = ApplyCoverageMask(diffuseOv, msk.Value.W, msk.Value.T, MaskAdds(entry, resolved));
                        if (desc.Index != null && rows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                        {
                            var idxPath = Path.Combine(entry.SidecarRoot, desc.Index);
                            var idD = LoadIndexMerged(idxPath, covW, covH, srcBodyType, entry.ModDirectory);
                            if (idD != null) diffuseOv = ApplyIndexedOpacity(diffuseOv, idD, rows);
                        }
                        else if (desc.Index == null && row16A.Opacity != 0)
                            diffuseOv = ScaleOverlayAlpha(diffuseOv, row16A.Opacity);
                    }

                    // Phase A reads diffuseOv directly, so it needs the same higher-group fade CovAt
                    // applies to every other channel. Suppress() clones, so covSrc stays raw for CovAt.
                    diffuseOv = Suppress(diffuseOv, entry, resolved, covW, covH);

                    // Returns coverage at (tw × th): mask first, then opacity (indexed or flat).
                    // covSrc is raw — no opacity pre-baked — so the Masks-group always shapes
                    // coverage before the user's transparency slider scales the result.
                    byte[]? CovAt(int tw, int th)
                    {
                        byte[]? cov;
                        if (tw == covW && th == covH)
                        {
                            cov = covSrc; // raw seed
                        }
                        else if (desc.Diffuse != null)
                        {
                            // Reload the overlay at the requested size and remap into the
                            // destination UV space — same as the covSrc seed (line ~767) and the
                            // index load below. Without the remap, coverage comes back in the
                            // SOURCE (e.g. bibo) UV space and lands misaligned on a converted base
                            // (gen3/vanilla), producing a fringe/seam at UV-island boundaries.
                            var diffPath = Path.Combine(entry.SidecarRoot, desc.Diffuse);
                            cov = RemapIfNeeded(LoadPng(diffPath, tw, th), tw, th, srcBodyType, diffPath);
                        }
                        else
                        {
                            cov = textureLoader.ScaleRgba(covSrc!, covW, covH, tw, th);
                        }
                        // Mask first.
                        if (cov != null)
                        {
                            var mask = CombinedMaskAt(entry.ModDirectory, tw, th, srcBodyType);
                            if (mask != null)
                                cov = ApplyCoverageMask(cov, mask.Value.W, mask.Value.T, MaskAdds(entry, resolved));
                        }
                        // Opacity after mask (TextureLoader cache deduplicates the index-texture load).
                        if (cov != null && desc.Index != null && rows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                        {
                            var idxPath = Path.Combine(entry.SidecarRoot, desc.Index);
                            var idxCov = LoadIndexMerged(idxPath, tw, th, srcBodyType, entry.ModDirectory);
                            if (idxCov != null) cov = ApplyIndexedOpacity(cov, idxCov, rows);
                        }
                        else if (cov != null && desc.Index == null && row16A.Opacity != 0)
                            cov = ScaleOverlayAlpha(cov, row16A.Opacity);

                        // Finally, fade by what a higher-priority group in this mod already claims.
                        cov = Suppress(cov, entry, resolved, tw, th);
                        return cov;
                    }

                    // ── UV-seam bleed removal ─────────────────────────────────
                    // ONLY for coverage that was actually cross-UV converted (sibling synthesis /
                    // bibo↔gen3 bake). A native (same-UV) overlay is rendered verbatim and must be
                    // left untouched — the seam artefacts only exist because of the remap.
                    // Decide on the final composited coverage (post Masks-group + opacity) and drop
                    // the chosen pixels from covSrc (drives every channel via CovAt) and diffuseOv
                    // (Phase A), keeping all channels consistent.
                    // Skip gen2: vanilla UV is a right-half crop of bibo space, not a transfer-map
                    // stitch, so it has no UV-island seams to clean — and no *_to_gen2 map exists
                    // (asking for one just logs a spurious "transfer map not found").
                    if (srcBodyType != null && dstBodyType != null
                        && !string.Equals(srcBodyType, dstBodyType, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(dstBodyType, "gen2", StringComparison.OrdinalIgnoreCase))
                    {
                        var decision = CovAt(covW, covH);
                        var dropMask = decision != null
                            ? uvRemap.ComputeSeamDropMask(decision, covW, covH, srcBodyType, dstBodyType)
                            : null;
                        if (dropMask != null)
                        {
                            var cov = covSrc!;
                            var dif = diffuseOv != null && diffuseOv.Length == cov.Length ? diffuseOv : null;
                            var drop = dropMask;
                            ParallelPixels(0, drop.Length, 1, (fromDi, toDi) =>
                            {
                                for (int di = fromDi; di < toDi; di++)
                                {
                                    if (!drop[di]) continue;
                                    int o = di * 4;
                                    cov[o] = cov[o + 1] = cov[o + 2] = cov[o + 3] = 0;
                                    if (dif != null)
                                        dif[o] = dif[o + 1] = dif[o + 2] = dif[o + 3] = 0;
                                }
                            });
                        }
                    }

                    // ── Phase A: diffuse composite ────────────────────────────
                    if (desc.Diffuse != null && diffuseOv != null && baseD is { Length: > 0 })
                    {
                        if (desc.Index != null)
                        {
                            var idxPath = Path.Combine(entry.SidecarRoot, desc.Index);
                            var idD = LoadIndexMerged(idxPath, wD, hD, srcBodyType, entry.ModDirectory);
                            if (idD != null)
                            {
                                ApplyIndexedOverlay(baseD, diffuseOv, idD, rows, false, wD, hD);

                                // Glow recipe: which pixels resolve to each row (red/17 = pair, green≥128 =
                                // sub-row A), gated by the SAME coverage the composite used (diffuseOv alpha),
                                // downsampled. One byte/pixel: 0 = no glow, else 0x80 | (A?0x40) | pairIdx.
                                int gw = Math.Min(wD, glowMapCap), gh = Math.Min(hD, glowMapCap);
                                var gmap = new byte[gw * gh];
                                for (int my = 0; my < gh; my++)
                                {
                                    int sy = gh == hD ? my : (int)((long)my * hD / gh);
                                    for (int mx = 0; mx < gw; mx++)
                                    {
                                        int sx = gw == wD ? mx : (int)((long)mx * wD / gw);
                                        int si = (sy * wD + sx) * 4;
                                        if (diffuseOv[si + 3] == 0) continue;   // outside this overlay's coverage
                                        gmap[my * gw + mx] = (byte)(0x80 | (idD[si + 1] >= 128 ? 0x40 : 0) | ((idD[si] / 17) & 0x0F));
                                    }
                                }
                                glowMaps.Add((entry.ModDirectory, resolved.OptionGroup, resolved.Option, gmap, gw, gh));
                            }
                            else ApplyFlatOverlay(baseD, diffuseOv, row16A, wD, hD);
                        }
                        else ApplyFlatOverlay(baseD, diffuseOv, row16A, wD, hD);
                    }
                    // Normal-only (and mask-only) overlays no longer synthesize a diffuse tint: the author
                    // wants just the normal (and any mask) applied, leaving the skin diffuse untouched. The
                    // Row-16-colour synthesis below is disabled per request — kept for reference.
                    /*
                    else if (desc.Diffuse == null && normalOv != null && texPaths.Diffuse != null && desc.GenerateDiffuse)
                    {
                        // Normal-only overlay: apply synthesized tint (Row 16 color) to the diffuse
                        // channel. Skipped when GenerateDiffuse is false — the author wants the normal
                        // (and any mask) applied without altering the skin diffuse.
                        if (baseD == null)
                        {
                            var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                            var loaded   = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                            if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                            baseD ??= Array.Empty<byte>();
                        }
                        if (baseD.Length > 0)
                        {
                            var tint = CovAt(wD, hD);
                            if (tint != null) ApplyFlatOverlay(baseD, tint, row16A, wD, hD);
                        }
                    }
                    */

                    // ── Phase B: normal composite ─────────────────────────────
                    if (normalOv != null && baseN is { Length: > 0 })
                        CompoundNormal(baseN, normalOv, wN, hN, CovAt(wN, hN));

                    // ── Phase B2: suppress skin-color influence under the overlay ──
                    // skin.shpk reads the normal map's BLUE channel as "skin color influence":
                    // white = the character's skin tone is multiplied onto the diffuse. Overlays
                    // are baked into the skin diffuse, so without this the shader re-tints every
                    // overlay pixel by skin tone (darker skin → darker overlay, even when opaque).
                    // Fade the influence out by diffuse coverage so opaque fabric renders at its
                    // authored colour while sheer gaps keep skin tone. Diffuse overlays only —
                    // normal-only overlays add relief, not colour, so they keep skin tinting.
                    // Strength = the user's global setting × this overlay's optional SkinToneMask
                    // (author override; null = full). 0 disables it entirely (no skin masking, and
                    // no normal rewrite for diffuse-only overlays).
                    float skinMask = config.SkinColorSuppression * (desc.SkinToneMask ?? 1f);
                    if (desc.Diffuse != null && texPaths.Normal != null && skinMask > 0f)
                    {
                        if (baseN == null)
                        {
                            baseN = LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
                            if (anyEmissive && baseN.Length > 0)
                                for (int ai = 3; ai < baseN.Length; ai += 4) baseN[ai] = 0;
                        }
                        if (baseN.Length > 0)
                        {
                            var scMask = CovAt(wN, hN);
                            if (scMask != null)
                            {
                                // Weight the suppression by the composited overlay colour so dark
                                // dyes keep skin tone (and stay matte) while bright dyes get fully
                                // de-tinted. baseD holds that colour; scale it to the normal's size.
                                byte[]? diffAtN = baseD is { Length: > 0 }
                                    ? (wD == wN && hD == hN ? baseD : textureLoader.ScaleRgba(baseD, wD, hD, wN, hN))
                                    : null;
                                SuppressSkinColorInfluence(baseN, scMask, diffAtN, wN, hN, skinMask);
                            }
                        }
                    }

                    // ── Phase C: emissive → normal alpha ──────────────────────
                    // skin.shpk: normal alpha = per-pixel emissive intensity (key 0x380CAED0).
                    bool thisOverlayHasEmissive = rows.Values.Any(r => r.A.Emissive > 0.001f || r.B.Emissive > 0.001f);
                    if (thisOverlayHasEmissive)
                    {
                        if (texPaths.Normal == null)
                        {
                            log.Warning("[Proteus] Emissive set but material has no normal texture: {0}", mtrlGamePath);
                        }
                        else
                        {
                            if (baseN == null)
                            {
                                baseN = LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
                                if (anyEmissive && baseN.Length > 0)
                                    for (int ai = 3; ai < baseN.Length; ai += 4) baseN[ai] = 0;
                            }
                            if (baseN.Length > 0)
                            {
                                if (desc.Index != null)
                                {
                                    // Index texture maps each pixel to a color table row.
                                    // Write configured emissive for that row to normal alpha.
                                    // Pixels outside the overlay have R=0 → unmapped → stay at 0.
                                    var idxPath = Path.Combine(entry.SidecarRoot, desc.Index);
                                    var idN = LoadIndexMerged(idxPath, wN, hN, srcBodyType, entry.ModDirectory);
                                    var emMask = CovAt(wN, hN);
                                    if (idN != null && emMask != null) ApplyIndexedEmissive(baseN, idN, emMask, rows, wN, hN);
                                }
                                else
                                {
                                    var emMask = CovAt(wN, hN);
                                    if (emMask != null) ApplyFlatEmissive(baseN, emMask, row16A, wN, hN);
                                    else log.Warning("[Proteus] No emissive mask for: {0}", texPaths.Normal);
                                }
                            }
                        }
                    }

                    // ── Phase D: mask texture composite ───────────────────────
                    if (desc.Mask != null && texPaths.Mask != null)
                    {
                        if (baseM == null)
                        {
                            var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(texPaths.Mask), texPaths.Mask);
                            if (loaded.HasValue) { baseM = loaded.Value.rgba; wM = loaded.Value.width; hM = loaded.Value.height; }
                            baseM ??= Array.Empty<byte>();
                        }
                        if (baseM.Length > 0)
                        {
                            var maskPathD = Path.Combine(entry.SidecarRoot, desc.Mask);
                            var ov = RemapIfNeeded(LoadPng(maskPathD, wM, hM), wM, hM, srcBodyType, maskPathD);
                            if (ov != null) AlphaComposite(baseM, ov, wM, hM, CovAt(wM, hM));
                        }
                    }
                }

                // ── Masks-driven relief ────────────────────────────────────────
                // Runs once per mod, after every overlay in its stack has composited, for any
                // active "Masks" option whose export also produced a companion relief normal
                // (see proteus_packager.py). The companion Index texture (if any) is handled
                // earlier via LoadIndexMerged, inline with the overlay's own Index usage.
                //
                // Order: compute the base normal (overlay stack) PLUS every active mask's own
                // relief first, then fold in the combined Masks-group coverage (the same
                // priority-ordered reduction CombinedMaskAt already applies to the regular
                // overlay's coverage) as a final show/hide pass. Gating each mask's relief only
                // by its own alpha (as before) let mask A's bump bleed into territory a
                // DIFFERENT active mask B is meant to erase — B's own shape never got a say.
                foreach (var modDir in pairs.Select(p => p.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (maskShellMods.Contains(modDir)) continue;   // relief lives on the mask shell instead
                    if (!maskAssetsByMod.TryGetValue(modDir, out var assets)) continue;
                    if (texPaths.Normal == null || !assets.Any(a => a.NormalPath != null)) continue;
                    lastSrcBodyTypeByMod.TryGetValue(modDir, out var maskSrcBodyType);

                    if (baseN == null)
                    {
                        baseN = LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
                        if (anyEmissive && baseN.Length > 0)
                            for (int ai = 3; ai < baseN.Length; ai += 4) baseN[ai] = 0;
                    }
                    if (baseN.Length > 0)
                    {
                        // Snapshot before any mask relief — the combined masks-group coverage
                        // below decides, per pixel, how much to blend back toward this.
                        var preRelief = (byte[])baseN.Clone();

                        // Fold each active mask's trim relief into the body normal, top-first with a claim so a
                        // higher mask's trim wins over (and suppresses) a lower one's where they run together,
                        // while plain fill leaves the fabric normal to show. See CombineMaskReliefs.
                        var reliefMasks = new List<(byte[] Relief, byte[] Coverage)>();
                        foreach (var (maskPath, normalPath, _) in assets)   // top-first (highest priority first)
                        {
                            if (normalPath == null) continue;
                            var maskPng  = RemapIfNeeded(LoadPng(maskPath, wN, hN), wN, hN, maskSrcBodyType, maskPath);
                            var normalOv = RemapIfNeeded(LoadPng(normalPath, wN, hN), wN, hN, maskSrcBodyType, normalPath);
                            if (maskPng != null && normalOv != null)
                                reliefMasks.Add((normalOv, maskPng));
                        }
                        CombineMaskReliefs(baseN, wN, hN, reliefMasks);

                        var msk = CombinedMaskAt(modDir, wN, hN, maskSrcBodyType);
                        if (msk != null)
                        {
                            var full = new byte[wN * hN * 4];
                            ParallelPixels(3, full.Length, 4, (fromFi, toFi) =>
                            {
                                for (int fi = fromFi; fi < toFi; fi += 4) full[fi] = 255;
                            });
                            var weight = ApplyCoverageMask(full, msk.Value.W, msk.Value.T);
                            var bn = baseN;
                            var pre = preRelief;
                            ParallelPixels(0, bn.Length, 4, (fromI, toI) =>
                            {
                                for (int i = fromI; i < toI; i += 4)
                                {
                                    float t = weight[i + 3] / 255f;
                                    bn[i]     = (byte)(pre[i]     * (1f - t) + bn[i]     * t);
                                    bn[i + 1] = (byte)(pre[i + 1] * (1f - t) + bn[i + 1] * t);
                                }
                            });
                        }
                    }
                }

                // ── Masks own-colorset diffuse ─────────────────────────────────
                // A mod whose single "Masks" tab has a colorset (maskRowsByMod) colours its active masks
                // from THAT shared table, composited on top of the overlay diffuse. The mask _id is NOT merged
                // into the overlays (skipped in LoadIndexMerged), so this is the only place it gets its colour.
                //
                // The active masks combine TOP-TERRITORY-WINS (matching CombinedMaskAt, which carves the other
                // layers the same way): at each pixel the topmost mask that has territory (alpha) there decides
                // both the coverage (its grayscale) and the colour row (its _id). So a mask that is BLACK in
                // its territory forces coverage 0 — a hole that reveals skin — even where a LOWER mask is white.
                foreach (var modDir in pairs.Select(p => p.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (maskShellMods.Contains(modDir)) continue;   // mask lives on the shell, not the skin diffuse
                    if (!maskRowsByMod.TryGetValue(modDir, out var maskRows)) continue;
                    if (!maskAssetsByMod.TryGetValue(modDir, out var assets) || texPaths.Diffuse == null) continue;
                    if (!assets.Any(a => a.IndexPath != null)) continue;
                    lastSrcBodyTypeByMod.TryGetValue(modDir, out var maskSrcBodyType);

                    if (baseD == null)
                    {
                        var diffDisk = penumbra.ResolvePlayer(texPaths.Diffuse);
                        var loaded = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                        if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                        baseD ??= Array.Empty<byte>();
                    }
                    if (baseD.Length == 0) continue;

                    // Combine the masks into ONE coverage (paint alpha) + ONE _id, top-territory-wins. Bottom
                    // masks first so the top one (assets are highest-priority-first) lands last and overrides.
                    int n = wD * hD;
                    var cov = new byte[n];        // paint alpha = winning mask's grayscale in its territory
                    var cid = new byte[n * 4];    // winning mask's _id (red = row pair, green = A/B blend)
                    bool anyMask = false;
                    for (int mi = assets.Count - 1; mi >= 0; mi--)
                    {
                        var (maskPath, _, maskIndexPath) = assets[mi];
                        if (maskIndexPath == null) continue;
                        var maskPng = RemapIfNeeded(LoadPng(maskPath, wD, hD), wD, hD, maskSrcBodyType, maskPath);
                        var maskIdx = RemapIfNeeded(LoadPng(maskIndexPath, wD, hD), wD, hD, maskSrcBodyType, maskIndexPath);
                        if (maskPng == null || maskIdx == null) continue;
                        anyMask = true;
                        ParallelPixels(0, n, 1, (from, to) =>
                        {
                            for (int p = from; p < to; p++)
                            {
                                int o = p * 4;
                                int a = maskPng[o + 3];
                                if (a == 0) continue;                                       // outside this mask
                                int g = (maskPng[o] * 77 + maskPng[o + 1] * 150 + maskPng[o + 2] * 29) >> 8;
                                // Territory alpha-over: the top mask REPLACES lower coverage in its territory,
                                // so a=255,g=0 (black) drives cov to 0 — a hole — erasing a lower mask's white.
                                cov[p] = (byte)(cov[p] * (255 - a) / 255 + g * a / 255);
                                if (a >= 128) { cid[o] = maskIdx[o]; cid[o + 1] = maskIdx[o + 1]; }
                            }
                        });
                    }
                    if (!anyMask) continue;

                    // Paint once from the combined coverage + _id (white "art", alpha = coverage).
                    var art = new byte[n * 4];
                    ParallelPixels(0, art.Length, 4, (from, to) =>
                    {
                        for (int i = from; i < to; i += 4)
                        {
                            art[i] = art[i + 1] = art[i + 2] = 255;
                            art[i + 3] = cov[i >> 2];
                        }
                    });
                    ApplyIndexedOverlay(baseD, art, cid, maskRows, false, wD, hD);

                    // Glow row-map from the same combined coverage + _id. 0 = no glow (hole/outside), else
                    // 0x80 | (A?0x40) | pairIdx — same format as overlays.
                    int gw = Math.Min(wD, glowMapCap), gh = Math.Min(hD, glowMapCap);
                    var gmap = new byte[gw * gh];
                    bool anyGlow = false;
                    for (int my = 0; my < gh; my++)
                    {
                        int sy = gh == hD ? my : (int)((long)my * hD / gh);
                        for (int mx = 0; mx < gw; mx++)
                        {
                            int sx = gw == wD ? mx : (int)((long)mx * wD / gw);
                            int sp = sy * wD + sx;
                            if (cov[sp] == 0) continue;   // hole/outside = no glow
                            int so = sp * 4;
                            gmap[my * gw + mx] = (byte)(0x80 | (cid[so + 1] >= 128 ? 0x40 : 0) | ((cid[so] / 17) & 0x0F));
                            anyGlow = true;
                        }
                    }
                    if (anyGlow)
                        glowMaps.Add((modDir, SidecarDiscoveryService.MaskGroupName, "Masks", gmap, gw, gh));
                }

                // ── Ambient occlusion: soft contact-shadow on skin around strap / garment edges ──
                // Each mod spreads its silhouette into the surrounding skin and darkens the diffuse just
                // outside the edge, so straps/garments read with depth. The silhouette is the mod's mask
                // when it has one, otherwise the garment's own gear coverage (so non-masked straps like a
                // bralette cast a shadow too). The shadow is on skin either way — including straps promoted
                // to gear shells. Multiplies into the shared baseD, so overlapping mods each add a shadow.
                float aoStrength = config.AmbientOcclusionStrength;
                float aoNormal   = config.AmbientOcclusionNormalDepth;
                if (aoStrength > 0f || aoNormal > 0f)
                {
                    float aoSoftness = config.AmbientOcclusionSoftness;
                    // Every mod that contributes a mask, a gear garment, OR a skin-painted garment to this
                    // body, in composite order (bottom→top).
                    var aoModsList = pairs.Select(p => p.Entry.ModDirectory)
                        .Concat(gearOverlays.Select(g => g.Entry.ModDirectory))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    // What each pack itself declares about AO. Absent means it never asked, and the answer
                    // is no — see ProteusMetadata.AmbientOcclusion. Collected from the same two sources
                    // aoModsList is built from, so every mod in that list has an entry here.
                    var aoDeclaredBy = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in pairs.Select(p => p.Entry).Concat(gearOverlays.Select(g => g.Entry)))
                        aoDeclaredBy.TryAdd(e.ModDirectory, e.Metadata?.AmbientOcclusion);

                    // Gear shells and Masks-group coverage are authored in BODY UV — a shell is literally cut
                    // from the body mesh — so they only describe a garment when the material being composited
                    // shares that UV space. See GarmentSilhouette, which gates gear the same way.
                    bool bodyUvMaterial = IsBodyUvMaterial(mtrlGamePath);

                    // The UV islands at the diffuse resolution: a 0/255 plane to tell body from padding, plus
                    // the per-island labelling BlurCoverageWithinIslands needs to keep each blur window on
                    // one island. Null when this body has no transfer map to derive them from (gen2), in
                    // which case the silhouettes are blurred plainly, exactly as before.
                    byte[]? insidePlane = null;
                    int[]? islandLabels = null, islandOwner = null;
                    int islandCount = 0;
                    // Reused by every mod's AO pass on THIS material (see IslandBlurCache). Per material,
                    // because materials composite in parallel while the mod loop below is sequential.
                    var islandBlurCache = new IslandBlurCache();
                    if (dstBodyType != null && baseD is { Length: > 0 })
                    {
                        var isl = uvRemap.IslandMask(dstBodyType, out int islW, out int islH);
                        if (isl != null && islW > 0 && islH > 0)
                        {
                            insidePlane = new byte[wD * hD];
                            var ip = insidePlane;
                            Parallel.For(0, hD, y =>
                            {
                                int srow = (int)((long)y * islH / hD) * islW;
                                int row = y * wD;
                                for (int x = 0; x < wD; x++)
                                {
                                    int si = srow + (int)((long)x * islW / wD);
                                    ip[row + x] = (byte)(si < isl.Length && isl[si] ? 255 : 0);
                                }
                            });
                            (islandLabels, islandOwner, islandCount) = IslandLabelsFor(dstBodyType, insidePlane, wD, hD);
                        }
                    }

                    // The mesh that owns this UV layout. Its seams are the only statement of which island
                    // edge continues into which other — the texture can't say, and the transfer maps
                    // describe a different body's layout, not this one's topology. Loaded once per
                    // composite; the seam map itself is cached by model content inside the service.
                    // IDENTITY ONLY here — no file is opened. The seam map is keyed on it, so a cache hit
                    // (the normal case: the body doesn't change between composites) never pays the 4.6 MB
                    // read that used to cost ~1.1s per composite just to compute a content hash.
                    List<UvSeamMapService.SeamModel>? bodyMdls = null;
                    if (islandLabels != null)
                    {
                        if (BodyModelPathsFor(mtrlGamePath) is { } bodyMdlPaths)
                        {
                            bodyMdls = new List<UvSeamMapService.SeamModel>(bodyMdlPaths.Length);
                            foreach (var mp in bodyMdlPaths)
                            {
                                var gamePath = mp;
                                var disk = penumbra.ResolvePlayer(gamePath);
                                // A modded part is a real file, so size+mtime settles whether it changed.
                                // A vanilla one comes from sqpack and can't change without a game patch,
                                // so the game path alone is a stable identity for it.
                                string id = gamePath;
                                try
                                {
                                    if (!string.IsNullOrEmpty(disk))
                                    {
                                        var fi = new FileInfo(disk);
                                        if (fi.Exists) id = $"{disk}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                                    }
                                }
                                catch { /* unreadable — fall back to the game path */ }
                                bodyMdls.Add(new UvSeamMapService.SeamModel(
                                    id, () => textureLoader.LoadRawFile(disk, gamePath)));
                            }
                            // Which files the seam map will be built from, WITHOUT opening any of them.
                            // Worth keeping: "did the seam map even see my body?" took several rounds to
                            // answer once, and on a cache hit nothing else is logged at all.
                            log.Debug("[Proteus] seam map: {0} body part(s) for {1} — {2}",
                                      bodyMdls.Count, mtrlGamePath,
                                      string.Join(", ", bodyMdls.Select(m =>
                                          m.Id.Contains('|') ? Path.GetFileName(m.Id[..m.Id.IndexOf('|')]) : "(game)")));
                        }
                        else
                        {
                            log.Debug("[Proteus] seam map: {0} is not a human body material — no seam data",
                                      mtrlGamePath);
                        }
                    }

                    // NOTE — the "seam / crease along a UV border" hunt, and what it actually turned out
                    // to be, so none of it is retried. TWO real causes, both now fixed:
                    //  1. TryReadLod0Geometry was reading the WHOLE model. A body model also carries the
                    //     undies, nails and pubes meshes, which are authored in GEAR UV space — their
                    //     triangles landed at unrelated places in the body atlas, welding separate islands
                    //     together and inventing seam correspondences between surfaces that never touch.
                    //     Filtering to skin materials took the seam build 5.8s -> 258ms and the mapped
                    //     texels 12M -> 1.67M.
                    //  2. ApplyNormalIndent took a CENTRAL difference across the island border, so one
                    //     sample landed in padding whose value comes from a different computation. The
                    //     one-texel step read as a slope ~4x the interior (2.93 against 0.74 measured over
                    //     the whole body) and got carved into a crease tracing every island outline.
                    // Rejected along the way, all against this same artefact: fading AO/indent out near
                    // borders (the ridges sit ~90% inside an island, so the fade is ~1 where they are);
                    // clipping the silhouette to IslandMask (kills the seam but substitutes an equal step
                    // the other way, reported as "AO near mask edges is gone"); scaling the blur radius to
                    // the trim's feature size (removes the seam but loses the effect — a filament mask
                    // wants radius ~4 where the look wants ~12).
                    // SEPARATELY, and not a compositor bug: the mask art itself can carry one-row cliffs
                    // (0 -> 255 across 2px where that file's own edges ramp over ~32px). The blur turns
                    // those into ~24px bands. They are distinguishable — a 200-texel axis-aligned run in a
                    // single row is not a real strap edge — but nothing here filters them today.

                    // What a mod casts onto THIS material. ONE definition, used both to decide whether any
                    // base texture needs loading at all and by the loop below — they qualified separately
                    // before, and a change to either would have silently desynced them.
                    //
                    // Every source is scoped to the material being composited. Masks and gear by UV space
                    // (above); SKIN by the material it was painted for — that one is the subtle case, because
                    // a mod's skin overlays live on the body while the mod itself reaches this loop through
                    // ANY gear shell it owns. Unscoped, a bodysuit's body-painted Pattern kept the pass alive
                    // while compositing the face, which loaded and republished the face's diffuse and normal
                    // (re-encoded, unmodified) even though nothing could possibly draw there.
                    (bool Masks, bool Gear, bool Skin) AoSources(string modDir) => (
                        bodyUvMaterial && maskPathsByMod.ContainsKey(modDir),
                        bodyUvMaterial && gearOverlays.Any(g => string.Equals(g.Entry.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)),
                        // Skin-layer overlays with a diffuse (a painted garment) also cast AO/indent. Flat
                        // full-coverage overlays are self-gating (no edges → no effect); the per-mod AO
                        // checkbox, or the pack's own AmbientOcclusion declaration, opts anything unwanted
                        // (tattoos, skin details) back out — and since AO is opt-IN they are off by default.
                        allOverlays.Any(o => string.Equals(o.Entry.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)
                            && o.Overlay.Descriptor.Layer == OverlayLayer.Skin
                            && o.Overlay.Descriptor.Diffuse != null
                            && o.Overlay.Descriptor.MaterialGamePaths.Contains(mtrlGamePath, StringComparer.OrdinalIgnoreCase)));

                    // Qualify every mod ONCE, keeping the only flag the loop still needs (masks pick the
                    // silhouette: a masked garment traces its trim, everything else its own coverage). The
                    // Skin term walks allOverlays, so evaluating this per mod per use — the load gate, the
                    // loop's continue, and the mask flag — swept it three times for nothing.
                    var aoQualified = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var modDir in aoModsList)
                    {
                        // Opt-IN: the user's explicit choice, else what the pack declared, else off.
                        aoDeclaredBy.TryGetValue(modDir, out bool? declared);
                        if (!config.AmbientOcclusionEnabledFor(modDir, declared)) continue;
                        var (m, g, s) = AoSources(modDir);
                        if (m || g || s) aoQualified[modDir] = m;
                    }

                    log.Debug("[Proteus] AO on {0}: strength={1:F3} normal={2:F4} softness={3:F4} — {4}/{5} mod(s) qualified [{6}]",
                              mtrlGamePath, aoStrength, aoNormal, aoSoftness,
                              aoQualified.Count, aoModsList.Count, string.Join(", ", aoQualified.Keys));

                    // The base body diffuse — needed for the shadow AND for the shared "covered above" mask's
                    // dimensions, so load it up front even when only the normal indent is enabled.
                    //
                    // Only when some mod actually qualifies, though. Loading it unconditionally left baseD
                    // non-empty for every material this pass touched, and the writer at the end of the
                    // composite republishes (and re-encodes) any non-empty buffer — so a face material whose
                    // mod supplies nothing but a mask still got a Proteus-written, BC7-recompressed _d.
                    if ((baseD == null || baseD.Length == 0) && texPaths.Diffuse != null && aoQualified.Count > 0)
                    {
                        var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(texPaths.Diffuse), texPaths.Diffuse);
                        if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                    }

                    // Union of the opaque coverage of every garment ABOVE the one being processed: a lower
                    // garment's contact shadow / indent is suppressed where a higher garment already covers the
                    // skin. Process TOP→BOTTOM — gate each garment by what's accumulated, then add its own
                    // coverage. Uses the FULL garment coverage (GarmentSilhouette), even for a masked garment
                    // whose AO silhouette is only its trim, so the whole fabric occludes what's under it.
                    byte[]? coveredAbove = baseD is { Length: > 0 } ? new byte[wD * hD] : null;

                    // Tracked across the WHOLE sweep, not per mod: whether this pass was the one to load the
                    // skin normal, and whether any garment actually indented it. LoadBaseTexture hands back a
                    // buffer upscaled to 4K (~64 MB), so loading and dropping it per mod would churn that
                    // repeatedly — and dropping a FAILED load (Array.Empty) would re-attempt the resolve, and
                    // re-log its warning, once per qualifying mod. Load at most once, hand back at most once.
                    bool aoLoadedNormal = false, aoIndentedNormal = false;

                    for (int mi = aoModsList.Count - 1; mi >= 0; mi--)
                    {
                        var modDir = aoModsList[mi];
                        if (!aoQualified.TryGetValue(modDir, out bool hasMasks)) continue;
                        lastSrcBodyTypeByMod.TryGetValue(modDir, out var aoSrcBodyType);

                        // This garment's coverage at the diffuse res — occludes LOWER garments' effects. Carve
                        // it by the mod's OWN masks: a masked garment does NOT cover its cutout holes, so a
                        // garment below (visible through the hole) must still get its shadow/indent there.
                        // Carved coverage = fabric·(mask keep) + trim, so cutouts → 0, fabric/trim → opaque.
                        byte[]? garmentCov = baseD is { Length: > 0 } ? GarmentSilhouette(modDir, wD, hD) : null;
                        if (hasMasks && baseD is { Length: > 0 } && CombinedMaskAt(modDir, wD, hD, aoSrcBodyType) is { } gm)
                        {
                            var mW = gm.W; var mT = gm.T;
                            if (garmentCov == null) garmentCov = (byte[])mT.Clone();   // mask-only: its trim is the coverage
                            else
                            {
                                var gc = garmentCov;
                                ParallelPixels(0, wD * hD, 1, (from, to) =>
                                {
                                    for (int p = from; p < to; p++)
                                    {
                                        int v = gc[p] * mW[p] / 255 + mT[p];
                                        gc[p] = (byte)(v > 255 ? 255 : v);
                                    }
                                });
                            }
                        }

                        // The AO silhouette (mask if the mod has one, else the garment coverage). Computed at
                        // the diffuse res; reused for the normal indent when the normal shares that size.
                        // radiusD is remembered so the indent normalises against the radius the blur it reads
                        // was ACTUALLY built with — the feature-scaled one, not the raw softness setting.
                        byte[]? strapD = null, blurredD = null;
                        int radiusD = 0;

                        // ── Diffuse: soft contact shadow on the skin just outside the edge ──
                        if (aoStrength > 0f && baseD is { Length: > 0 })
                        {
                            strapD = hasMasks ? (CombinedMaskAt(modDir, wD, hD, aoSrcBodyType)?.T ?? garmentCov) : garmentCov;
                            if (strapD != null)
                            {
                                radiusD = Math.Max(1, (int)(wD * aoSoftness));
                                // Keep every blur window on one UV island, so the halo comes from the mask
                                // and not from padding or from the island across the gutter. The silhouette
                                // itself is left exactly as authored — it is also the gate, and on-model
                                // texels are the same either way. See BlurCoverageWithinIslands.
                                blurredD = insidePlane != null && islandLabels != null && islandOwner != null
                                    ? BlurCoverageWithinIslands(strapD, islandLabels, islandOwner, islandCount, insidePlane,
                                                                bodyMdls == null ? null : seamMaps.SeamSource(bodyMdls, wD, hD, SeamReach(radiusD)), wD, hD, radiusD, islandBlurCache)
                                    : BlurCoverage(strapD, wD, hD, radiusD);
                                ApplyAmbientOcclusion(baseD, strapD, blurredD, wD, hD, aoStrength, coveredAbove);
                            }
                        }

                        // ── Normal: indent the skin at the edge so the strap looks pressed in ──
                        if (aoNormal > 0f && texPaths.Normal != null)
                        {
                            // Building the silhouette needs the normal's dimensions, which only the load
                            // reports — so the load can't be deferred past the decision to use it. Instead
                            // note that WE loaded it (see aoLoadedNormal above) and hand it back after the
                            // sweep if nothing indented it: any non-empty baseN is republished, and
                            // re-encoded, at the end of the composite, so a speculative load on its own
                            // rewrites an untouched normal.
                            if (baseN == null)
                            {
                                aoLoadedNormal = true;
                                baseN = LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
                                if (anyEmissive && baseN.Length > 0)
                                    for (int ai = 3; ai < baseN.Length; ai += 4) baseN[ai] = 0;
                            }
                            if (baseN.Length > 0)
                            {
                                byte[]? strapN, blurredN;
                                // The radius the indent's gradient must be normalised against — it sets the
                                // width of the coverage ramp, and so the per-pixel slope the indent reads.
                                int radiusN;
                                if (strapD != null && blurredD != null && wN == wD && hN == hD)
                                {
                                    strapN = strapD; blurredN = blurredD;   // reuse the diffuse-resolution buffers
                                    radiusN = radiusD;                      // the radius that blur was built with
                                }
                                else
                                {
                                    strapN = hasMasks ? (CombinedMaskAt(modDir, wN, hN, aoSrcBodyType)?.T ?? GarmentSilhouette(modDir, wN, hN))
                                                      : GarmentSilhouette(modDir, wN, hN);
                                    radiusN = Math.Max(1, (int)(wN * aoSoftness));
                                    // Island-restricted only when the normal shares the diffuse's size —
                                    // insidePlane and the labels are built at wD/hD, the same guard
                                    // coveredAbove uses. Otherwise a plain blur, as before.
                                    blurredN = strapN == null ? null
                                        : insidePlane != null && islandLabels != null && islandOwner != null && wN == wD && hN == hD
                                            ? BlurCoverageWithinIslands(strapN, islandLabels, islandOwner, islandCount, insidePlane,
                                                                        bodyMdls == null ? null : seamMaps.SeamSource(bodyMdls, wN, hN, SeamReach(radiusN)), wN, hN, radiusN, islandBlurCache)
                                            : BlurCoverage(strapN, wN, hN, radiusN);
                                }
                                // Gate by covered-above only when the normal shares the diffuse res it was built
                                // at (the common case — skin diffuse and normal are usually equal); else ungated.
                                if (strapN != null && blurredN != null)
                                {
                                    ApplyNormalIndent(baseN, blurredN, strapN, wN, hN, aoNormal,
                                        wN == wD && hN == hD ? coveredAbove : null, radiusN,
                                        wN == wD && hN == hD ? insidePlane : null);
                                    aoIndentedNormal = true;
                                }
                            }
                        }

                        // Add this garment's coverage so LOWER garments' effects are suppressed where it covers.
                        if (garmentCov != null && coveredAbove != null)
                        {
                            var acc = coveredAbove; var src = garmentCov;
                            ParallelPixels(0, wD * hD, 1, (from, to) =>
                            { for (int p = from; p < to; p++) if (src[p] > acc[p]) acc[p] = src[p]; });
                        }
                    }

                    // This pass loaded the normal and no garment ended up indenting it — hand it back so the
                    // writer doesn't republish (and re-encode) an untouched texture. Only a buffer with real
                    // content: a failed load left Array.Empty behind, which must STAY as the memo that the
                    // load was already tried and failed.
                    if (aoLoadedNormal && !aoIndentedNormal && baseN is { Length: > 0 }) baseN = null;
                }

                var baseName = SanitizeName(mtrlGamePath) + "_" + runId;
                var channels = new System.Text.StringBuilder();

                // Compression (opt-in): BC7 for every skin channel — the skin normal uses its B/A channels
                // too, so BC5 (2-channel) would corrupt it. Off ⇒ uncompressed, byte-identical to before.
                bool compress = config.EnableCompression;

                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_d.tex");
                    var relPath = "textures/" + baseName + "_d.tex";
                    if (textureLoader.WriteTex(baseD, wD, hD, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    {
                        redirects[texPaths.Diffuse] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" diffuse");

                        // Publish the glow recipes captured during the diffuse phase, now that the on-disk
                        // path (what the live texture resource reports) is known.
                        foreach (var gm in glowMaps)
                        {
                            var list = skinGlow.GetOrAdd((gm.ModDir, gm.Group, gm.Option),
                                _ => new List<Proteus.Interop.SkinGlowTarget>());
                            lock (list) list.Add(new Proteus.Interop.SkinGlowTarget(outPath, gm.Map, gm.W, gm.H));
                        }
                    }
                }
                if (baseN is { Length: > 0 } && texPaths.Normal != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_n.tex");
                    var relPath = "textures/" + baseName + "_n.tex";
                    if (textureLoader.WriteTex(baseN, wN, hN, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    { redirects[texPaths.Normal] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" normal"); }
                }
                if (baseM is { Length: > 0 } && texPaths.Mask != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_m.tex");
                    var relPath = "textures/" + baseName + "_m.tex";
                    if (textureLoader.WriteTex(baseM, wM, hM, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    { redirects[texPaths.Mask] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" mask"); }
                }

                if (channels.Length > 0)
                    log.Debug("[Proteus] Composited {0}:{1}", mtrlGamePath, channels);

                // Patch .mtrl with emissive shader key + color table if any row has Emissive > 0
                bool needsEmissive = pairs.Any(p =>
                    p.Overlay.ColorTableRows?.Any(r =>
                        r.SubRowA?.Emissive > 0.001f || r.SubRowB?.Emissive > 0.001f) == true);

                if (needsEmissive)
                {
                    var raw = textureLoader.LoadRawMtrl(mtrlDisk, mtrlGamePath);
                    if (raw == null)
                    {
                        log.Warning("[Proteus] Could not load raw mtrl for emissive patch: {0}", mtrlGamePath);
                    }
                    else
                    {
                        var combinedRows = new Dictionary<int, ColorTableRowOverride>();
                        foreach (var (_, ov2) in pairs)
                        {
                            var dict = BuildRowDict(ov2.ColorTableRows);
                            foreach (var (pairIdx, row) in dict)
                                if (!combinedRows.ContainsKey(pairIdx))
                                    combinedRows[pairIdx] = row;
                        }

                        // Switch the skin-type shader key to the glow variant; this is what enables
                        // emissive on skin.shpk (body skin-type is 0x2BDB45F1). Values below mirror
                        // the canonical LooseTextureCompiler skin_glow.mtrl.
                        raw = TextureLoader.EnsureShaderKey(raw, 0x380CAED0u, 0x72E697CDu);
                        raw = TextureLoader.PatchColorTableEmissive(raw, combinedRows);

                        // The body sets 0x2E60B071 to [200,200]; under the glow skin-type that value
                        // makes the material render so that seams between separate body models (e.g.
                        // a split torso/legs) become visible. skin_glow.mtrl uses [100,100].
                        raw = TextureLoader.PatchConstantValues(raw, 0x2E60B071u, 100f, 100f).data;

                        // Emissive color constant: the per-pixel glow is this color masked by the
                        // normal-map alpha, so it must be non-zero. White makes the glow take the
                        // configured per-row color. Add the constant if the material lacks it.
                        var (rawEmConst, emConstPatched) = TextureLoader.PatchEmissiveColorConstant(raw, 1f, 1f, 1f);
                        raw = emConstPatched ? rawEmConst : TextureLoader.EnsureEmissiveColorConstant(raw, 1f, 1f, 1f);

                        Directory.CreateDirectory(materialsDir);
                        var outPath = Path.Combine(materialsDir, baseName + ".mtrl");
                        var relPath = "materials/" + baseName + ".mtrl";
                        if (textureLoader.WriteMtrl(raw, outPath))
                            redirects[mtrlGamePath] = relPath;
                    }
                }

            });

            LogPhaseBreakdown(tRunStart, tSetupEnd, byMaterial.Count);

            // Publish the glow recipes gathered above (empty dict if no indexed skin overlays).
            _skinGlowTargets = new Dictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>(skinGlow);

            // ── Second skin: one gear shell per Layer:Gear overlay ────────────
            // Built from the body model the character is CURRENTLY drawing (resolved live through
            // Penumbra) — a shell cut from any other body shape shows the body through it.
            List<object>? manipulations = null;
            _needFullRedraw = false;
            _secondSkinActive = false;
            _shellMaterials = new();   // repopulated below only if a shell actually builds — else stays empty
            bool shellBuilt = false;   // a gear shell was produced this composite (drives glasses reconcile)
            // The shell was built for invisible glasses we have not equipped YET (ChooseHost's pending
            // branch). The injection below then needs no follow-up recomposite — the redirect is already
            // in place, so the equip's own redraw lands straight on the finished shell.
            bool glassesPreHosted = false;
            var tGear = PhaseCounter.Begin();
            if (gearOverlays.Count > 0)
            {
                // Same stacking rules as the skin composite: Penumbra priority, then group order, then the
                // user's per-group stack order (top-first). SecondSkinService assigns shell letters in this
                // order, so a higher-listed gear fabric layers over a lower one.
                gearOverlays = gearOverlays
                    .OrderBy(p => p.Entry.Priority)
                    .ThenByDescending(p => ModStackIndexFor(p.Entry.ModDirectory, p.Overlay.OptionGroup ?? "", p.Overlay.Option ?? ""))
                    .ThenByDescending(p => p.Overlay.GroupOrder)
                    .ThenByDescending(p => config.StackIndexOf(p.Entry.ModDirectory, p.Overlay.OptionGroup ?? "", p.Overlay.Option ?? ""))
                    .ToList();

                // ── Top mask shell ────────────────────────────────────────────
                // Each mask-shell mod (computed above) gets a dedicated mask shell, appended AFTER the sort so
                // it takes the highest letter = renders on top of all its other shells. Coloured by
                // MaskColorTableRows; SecondSkinService sources its coverage, _id and relief from the mod's
                // active masks (IsMaskShell), and skips the ordinary mask merge on the mod's OTHER shells
                // (maskShellMods) so the mask isn't coloured twice.
                foreach (var mod in maskShellMods)
                {
                    // Seed from a sibling gear overlay when the mod has one, else ANY of the mod's overlays
                    // (an all-skin mod whose mask was promoted to Cloth/Glow on its own). Only SourceBodyType
                    // and GroupOrder are read off the seed; everything else is overridden below.
                    var seed = gearOverlays.FirstOrDefault(g => g.Entry.ModDirectory == mod);
                    if (seed.Entry == null) seed = allOverlays.FirstOrDefault(g => g.Entry.ModDirectory == mod);
                    if (seed.Entry == null) continue;   // no overlay to source a body type from

                    // The mask's own render mode: Cloth (character.shpk) by default, or the shader/scroll it
                    // was given (Glow ⇒ characterscroll.shpk). Null descriptor ⇒ plain Cloth, as before.
                    var md = MaskDescriptorFor(seed.Entry);
                    var maskDesc = new OverlayDescriptor
                    {
                        Layer          = OverlayLayer.Gear,
                        IsMaskShell    = true,
                        SourceBodyType = seed.Overlay.Descriptor.SourceBodyType,
                        Shader         = md?.Shader,
                        Scroll         = md?.Scroll,
                        ScrollSpeedX   = md?.ScrollSpeedX,
                        ScrollSpeedY   = md?.ScrollSpeedY,
                        ScrollTilingX  = md?.ScrollTilingX,
                        ScrollTilingY  = md?.ScrollTilingY,
                    };
                    var maskResolved = seed.Overlay with
                    {
                        Descriptor     = maskDesc,
                        // Its own Masks colorset if set, else inherit the fabric/overlay colorset the legacy
                        // merge would have used — so a mask with no colours of its own still shows (the fabric's).
                        ColorTableRows = MaskRowsFor(seed.Entry) ?? seed.Overlay.ColorTableRows,
                        OptionGroup    = SidecarDiscoveryService.MaskGroupName,
                        Option         = "Masks",
                    };
                    gearOverlays.Add((seed.Entry, maskResolved));
                }

                // ── Gear-shell prefetch ──────────────────────────────────────
                // This phase is decode-bound, not mesh-bound: its own decodes measured 803 ms of a 1050 ms
                // phase, every one on the calling thread. Fire them all now and let the serial code below
                // consume them as they land — roughly 15 files at ~60 ms collapse to a couple of hundred ms.
                //
                // Warming these back at composite start does NOT work: the skin composite decodes over a
                // gigabyte of 4K art in between and LRU-evicts every one of them before this phase starts.
                // Issue them here, next to their consumer, where nothing can evict them first.
                foreach (var (gEntry, gOverlay) in gearOverlays)
                {
                    var gd = gOverlay.Descriptor;
                    const int gs = SecondSkinService.TexSize;
                    if (gd.Diffuse != null) WarmBg(Path.Combine(gEntry.SidecarRoot, gd.Diffuse), gs, gs);
                    if (gd.Normal  != null) WarmBg(Path.Combine(gEntry.SidecarRoot, gd.Normal),  gs, gs);
                    if (gd.Index   != null) WarmBg(Path.Combine(gEntry.SidecarRoot, gd.Index),   gs, gs);
                    if (maskPathsByMod.TryGetValue(gEntry.ModDirectory, out var gMasks))
                        foreach (var mp in gMasks) WarmBg(mp, gs, gs);
                    if (maskAssetsByMod.TryGetValue(gEntry.ModDirectory, out var gAssets))
                        foreach (var a in gAssets)
                        {
                            if (a.NormalPath != null) WarmBg(a.NormalPath, gs, gs);
                            if (a.IndexPath  != null) WarmBg(a.IndexPath,  gs, gs);
                        }
                }

                var charCode = (_glamourerCharCode ?? _lastCompositedCharCodes?.Split(',').FirstOrDefault())
                    ?.TrimStart('c', 'C');
                if (string.IsNullOrEmpty(charCode))
                    log.Warning("[Proteus] {0} gear overlay(s) skipped: no character code yet", gearOverlays.Count);
                else
                    try
                    {
                        // The shell inherits the BODY's UVs, so overlays authored for another body's UV
                        // layout must be remapped into the body's space — not the accessory material's.
                        // Use THIS character's body material: _lastCompositedBodyType can list several
                        // types (bibo hands + gen3 body, say) and taking whichever sorts first is wrong.
                        var bodyType = activeMtrl?
                            .Where(m => m.Contains($"/c{charCode}/obj/body/", StringComparison.OrdinalIgnoreCase))
                            .Select(UVRemapService.InferBodyType)
                            .FirstOrDefault(t => t != null)
                            ?? _lastCompositedBodyType?.Split(',').FirstOrDefault();

                        // Gear poses the skin it exposes in its OWN model (a heel tiptoes the foot, a
                        // skimpy top reshapes the chest). Cut each equipped slot's shell from the model
                        // the character is actually drawing there, not the flat bare body. Captured on
                        // the framework thread at redraw/trigger time (draw-object model resources);
                        // never call the draw-object IPC from this background thread.
                        var equippedModels = _equippedPartModels
                            ?? new Dictionary<string, string>();
                        var equippedAccessories = _equippedAccessoryModels
                            ?? new Dictionary<string, string>();
                        var metModels = _equippedMetModels ?? [];
                        log.Information("[Proteus] second skin: equipped part models [{0}], accessories [{1}], head/met [{2}] ({3})",
                            string.Join(", ", equippedModels.Select(kv => $"{kv.Key}={kv.Value}")),
                            string.Join(", ", equippedAccessories.Select(kv => $"{kv.Key}={kv.Value}")),
                            string.Join(", ", metModels),
                            _equippedPartModels == null ? "cache null" : "cached");

                        // Our injected invisible-glasses set (when the feature is on) so the shell REPLACES it
                        // — hiding the carrier item's frames — rather than appending. See ChooseHost.
                        int? invisibleGlassesSet = config.AutoInvisibleGlasses
                            ? InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ModelSet : null;

                        // Snapshot the volatile shape set ONCE: the shell is baked from it and its change
                        // signature is derived from it, so both must see the same value even if a concurrent
                        // post-settle read swaps _bodyShapeSnapshot mid-build.
                        var bodyShapes = _bodyShapeSnapshot;

                        // gen2 (vanilla) shells are opt-in per mod, same as the skin-layer gen2 sibling.
                        var shells = secondSkin.Build(charCode, gearOverlays, managedModDir, bodyType,
                            discovery.EffectsLibraryPath(), equippedModels, equippedAccessories,
                            modDir => config.SiblingModeFor(modDir) == SiblingSynthesisMode.AllBodies,
                            invisibleGlassesSet, metModels, bodyShapes, maskShellMods);
                        if (shells != null)
                        {
                            shellBuilt = true;
                            // Mirrors ChooseHost's pending-injection branch: feature on and the "_met"
                            // slot empty means the shell was built for glasses we are about to equip.
                            glassesPreHosted = invisibleGlassesSet is int && metModels.Count == 0;
                            foreach (var (gamePath, relPath) in shells.Redirects)
                                redirects[gamePath] = relPath;
                            manipulations = shells.Manipulations;
                            _secondSkinActive = true;   // an accessory model was redirected — disable must full-redraw

                            // Only new GEOMETRY forces the heavy path. A colorset edit rewrites just the
                            // .mtrl, and treating that like a new model cost a character redraw — and its
                            // flicker — on every colour change. Verified in-game: the in-place reload DOES
                            // apply a gear colorset change, so materials don't need the redraw.
                            // If a gear colour edit ever stops showing until something else redraws the
                            // character, this is the line to put back to shells.ShellChanged.
                            // A change in the body's ENABLED SHAPE KEYS (e.g. toggling "Remove Hip Dips")
                            // rebakes the shell's geometry, but can land on a byte-identical-length build or
                            // arrive a beat late, so ModelChanged alone let it slip through as an in-place
                            // reload — the morph didn't show until a manual refresh. Treat a shape-set change
                            // as a redraw trigger in its own right.
                            var shapeSig = BodyShapeSignature(bodyShapes);
                            bool shapesChanged = !string.Equals(shapeSig, _lastCompositedBodyShapeSig, StringComparison.Ordinal);
                            _lastCompositedBodyShapeSig = shapeSig;

                            // A spill host being added or (crucially) dropped as the layer count changes needs
                            // a full redraw so the vacated accessory reloads its real model — the in-place
                            // reload never re-fetches an accessory .mdl. ModelChanged catches added/changed
                            // hosts; this catches a host that simply vanished from the set.
                            var hostPaths = new HashSet<string>(shells.HostModelPaths, StringComparer.OrdinalIgnoreCase);
                            bool hostsChanged = !hostPaths.SetEquals(_lastShellHostPaths);
                            _lastShellHostPaths = hostPaths;

                            _needFullRedraw = shells.ModelChanged || shapesChanged || hostsChanged;
                            if (shells.ModelChanged)
                                log.Debug("[Proteus] second skin model changed — forcing a full redraw");
                            else if (shapesChanged)
                                log.Debug("[Proteus] second skin body shapes changed — forcing a full redraw");
                            else if (hostsChanged)
                                log.Debug("[Proteus] second skin host set changed — forcing a full redraw");
                            else if (shells.ShellChanged)
                                log.Debug("[Proteus] second skin material/textures changed — in-place reload");
                            _shellMaterials = shells.ShellMaterials;
                        }
                    }
                    catch (Exception ex) { log.Error(ex, "[Proteus] second skin build failed"); }
            }

            // No shell built this composite (no gear, or build failed) but hosts were redirected last time —
            // they've been dropped, so force a full redraw to reload the vacated accessories' real models.
            if (!shellBuilt && _lastShellHostPaths.Count > 0)
            {
                _needFullRedraw = true;
                _lastShellHostPaths = new(StringComparer.OrdinalIgnoreCase);
                log.Debug("[Proteus] second skin removed — forcing a full redraw to restore host accessories");
            }

            // Record the enabled-shape signature on EVERY composite — the gear phase above only sets it when
            // a shell actually builds. Without this, a skin-only composite (no gear shell) on a character WITH
            // shape keys leaves _lastCompositedBodyShapeSig stale, so SchedulePostRedrawBodyTypeCheck sees a
            // permanent mismatch and recomposites forever. Same snapshot the composite ran against.
            _lastCompositedBodyShapeSig = BodyShapeSignature(_bodyShapeSnapshot);

            // Runs entirely after the composite, so it adds to the user-visible delay one-for-one.
            if (gearOverlays.Count > 0)
                log.Information("[Proteus] recomposite phases: second skin {0:F0}ms ({1} gear layer(s))",
                    PhaseCounter.MsSince(tGear), gearOverlays.Count);

            WriteManagedModJson(redirects, manipulations);
            PruneManagedOutput(redirects);   // drop ss_*/model/material orphans from disabled/shrunk mods
            ReloadAndRedrawWhenReady(redirects, runId);

            // Reconcile the invisible-glasses injection AFTER the redirect mod is live, so when the equip's
            // redraw loads the glasses model it resolves straight to the shell (no visible frames). Passes
            // whether a shell was built this composite; the reconcile reads the current "met" state itself.
            ReconcileInvisibleGlasses(gearOverlays.Count > 0, shellBuilt, glassesPreHosted);

            LastResult = new CompositorResult
            {
                Success = true,
                TexturesPatched = texturesPatched,
                OverlayModsUsed = entries.Count,
            };
            ResultChanged?.Invoke();

            // The number the user actually waits through: wall clock from START to everything done — the
            // texture composite, the second-skin build, the managed-mod write, and the Penumbra reload plus
            // its redraw-readiness wait. Each phase line above covers one stage and none of them sum to
            // this, so without it the real cost has to be reconstructed from log timestamps.
            log.Information("[Proteus] recomposite DONE — {0:F0}ms total", PhaseCounter.MsSince(tRunStart));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex))
        {
            // The plugin is being torn down with this composite still in flight. Every managed thing it
            // touches from here is on a dying AssemblyLoadContext — the first not-yet-JITted method that
            // needs an assembly reference throws "AssemblyLoadContext is unloading or was already
            // unloaded" (StbImageSharp, on the PNG decode path). Nothing is wrong and nothing is
            // recoverable: the instance is going away and a fresh one will composite on load. Preloading
            // the assembly up front did NOT prevent this — the unload drops the ALC's resolved-assembly
            // cache, so code still running re-resolves and fails regardless. Log it as the shutdown race
            // it is instead of a red stack trace that reads like a crash.
            log.Debug("[Proteus] recomposite abandoned — plugin unloading ({0})", ex.GetBaseException().Message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Recomposite failed");
            LastResult = new CompositorResult { Success = false, ErrorMessage = ex.Message };
            ResultChanged?.Invoke();
        }
    }

    /// <summary>
    /// One summary line attributing a recomposite to its stages. "blend" is the composite loop minus
    /// the stages measured inside it, i.e. the per-pixel work itself; "materials" is how many items the
    /// loop had to spread over its 4 workers, which is what decides whether that loop is parallel at all.
    /// Logged at Information so a user's normal log shows it without a debug build.
    /// </summary>
    // int.MaxValue (unset stack index) prints as "-" so the log is readable.
    private static string FmtIdx(int i) => i == int.MaxValue ? "-" : i.ToString();

    // Order-independent signature of the enabled body shapes (model stem → shape names), for change
    // detection. Empty string when none, so toggling the last shape off also registers as a change.
    private static string BodyShapeSignature(IReadOnlyDictionary<string, HashSet<string>>? shapes)
    {
        if (shapes == null || shapes.Count == 0) return "";
        return string.Join("|", shapes
            .Select(kv => $"{kv.Key}:{string.Join(",", kv.Value.OrderBy(x => x, StringComparer.Ordinal))}")
            .OrderBy(x => x, StringComparer.Ordinal));
    }

    private void LogPhaseBreakdown(long runStart, long setupEnd, int materialCount)
    {
        var totalMs     = PhaseCounter.MsSince(runStart);
        var compositeMs = PhaseCounter.MsSince(setupEnd);
        var setupMs     = totalMs - compositeMs;

        var decode   = textureLoader.DecodeStats;
        var hits     = textureLoader.DecodeHitStats;
        var wait     = textureLoader.DecodeWaitStats;
        var prefetch = textureLoader.PrefetchWaitStats;
        var swizzle  = textureLoader.SwizzleStats;
        var write    = textureLoader.WriteStats;
        var remap    = uvRemap.RemapStats;

        // Blend is what's left of the composite once the stages measured inside it are removed. Only
        // the COMPOSITE thread's stages may be subtracted — decode now also runs on prefetch threads,
        // and subtracting concurrent work from wall time would understate blend by however much
        // overlapped. Caveat: with several materials in flight these foreground counters sum across
        // workers, so the split is only exact for the single-material case (the usual one).
        var blendMs = compositeMs - (wait.Ms + remap.Ms + swizzle.Ms + write.Ms);

        // Cache state alongside the miss count: a repeat composite that still misses means the budget is
        // under the working set, and these two numbers say by how much.
        var (cacheEntries, cacheBytes) = textureLoader.CacheState();

        log.Information(
            "[Proteus] recomposite phases: setup {0:F0}ms | decode-wait {1:F0}ms ({2} miss, {3} hit) | " +
            "prefetch {4:F0}ms bg (decode work {5:F0}ms) | remap {6:F0}ms ({7}) | blend {8:F0}ms | swizzle {9:F0}ms | " +
            "write {10:F0}ms ({11} files, {12:F0} MB) | composite {13:F0}ms | total {14:F0}ms | {15} material(s) | " +
            "cache {16} entries, {17:F0} MB, {18} evicted (budget {19:F0} MB)",
            setupMs, wait.Ms, decode.Calls, hits.Calls,
            prefetch.Ms, decode.Ms, remap.Ms, remap.Calls,
            blendMs, swizzle.Ms, write.Ms, write.Calls, write.Bytes / (1024.0 * 1024.0),
            compositeMs, totalMs, materialCount,
            cacheEntries, cacheBytes / (1024.0 * 1024.0), textureLoader.Evictions,
            textureLoader.DecodeCacheBudgetBytes / (1024.0 * 1024.0));
    }

    // ── Managed mod helpers ──────────────────────────────────────────────────

    private void EnsureManagedModExists()
    {
        // Keyed on the manifest, not the directory: without meta.json Penumbra doesn't register the mod
        // at all, so a folder that survived while its manifest didn't is dead weight that would never
        // repair itself. Rewriting it is safe — every caller recomposites straight afterwards, which
        // restores the redirects this clears.
        var metaPath = Path.Combine(managedModDir, PenumbraModMeta.MetaFile);
        if (File.Exists(metaPath)) return;
        var repairing = Directory.Exists(managedModDir);

        Directory.CreateDirectory(managedModDir);
        Directory.CreateDirectory(Path.Combine(managedModDir, "textures"));

        var verb = repairing ? "Repaired" : "Created";
        if (repairing)
            log.Warning("[Proteus] Managed mod at \"{0}\" was missing its {1} — recreating it",
                managedModDir, PenumbraModMeta.MetaFile);

        File.WriteAllText(
            metaPath,
            PenumbraModMeta.NewMetaJson(
                SidecarDiscoveryService.ManagedModDir, "Proteus",
                "Managed by the Proteus overlay compositor plugin."));

        WriteManagedModJson(new Dictionary<string, string>());

        var ec = penumbra.AddModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Information("[Proteus] AddMod({0}) -> {1}", managedModDir, ec);

        // Log which collection the new mod was enabled in, and where it landed. Both are the first
        // things to check when composited textures don't show up: the mod has to be enabled in the
        // collection the player is actually using, and the folder has to be under Penumbra's root.
        var coll = penumbra.GetPlayerCollection();
        if (coll.HasValue)
        {
            var (collId, collName) = coll.Value;
            penumbra.SetModEnabled(collId, SidecarDiscoveryService.ManagedModDir, true);
            penumbra.SetModPriority(collId, SidecarDiscoveryService.ManagedModDir, config.ManagedModPriority);
            log.Information("[Proteus] {0} managed mod at \"{1}\", enabled in collection \"{2}\" ({3}) at priority {4}",
                verb, managedModDir, collName, collId, config.ManagedModPriority);
        }
        else
        {
            log.Warning("[Proteus] {0} managed mod at \"{1}\", but the player's collection could not be "
                      + "determined — it has not been enabled anywhere. Overlays will not apply until it is.",
                verb, managedModDir);
        }
    }

    private void CheckManagedModHealth(List<OverlayEntry> overlayEntries)
    {
        var collId = penumbra.GetPlayerCollectionId();
        if (!collId.HasValue) return;

        var settings = penumbra.GetModSettings(collId.Value, SidecarDiscoveryService.ManagedModDir);
        if (settings == null)
        {
            log.Warning("[Proteus] Managed mod not found in player collection — re-adding");
            penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
            return;
        }

        if (!settings.Value.Enabled)
        {
            log.Warning("[Proteus] Managed mod is disabled in player collection — enabling");
            penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
        }

        if (overlayEntries.Count > 0)
        {
            int managedPriority = settings.Value.Priority;
            int maxOverlayPriority = overlayEntries.Max(e => e.Priority);
            if (maxOverlayPriority >= managedPriority)
                log.Warning("[Proteus] Managed mod priority ({0}) is not higher than overlay mod priority ({1}) — composited textures may be overridden",
                    managedPriority, maxOverlayPriority);
        }
    }

    /// <summary>
    /// Write the managed mod's redirects, in whichever layout the installed Penumbra reads — meta.json's
    /// <c>DefaultData</c> from FileVersion 4 on, a separate default_mod.json before that.
    /// <paramref name="manipulations"/> carries metadata edits — a second-skin shell needs an EQDP entry
    /// so the accessory it rides on loads the character's own race/gender model rather than the default.
    /// </summary>
    private void WriteManagedModJson(IDictionary<string, string> redirects, IReadOnlyList<object>? manipulations = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, relPath) in redirects)
            files[gamePath] = relPath;

        PenumbraModMeta.WriteRedirects(
            managedModDir, SidecarDiscoveryService.ManagedModDir, files, swaps: null, manipulations: manipulations);
    }

    /// <summary>
    /// Delete any file under textures/ materials/ models/ that the just-written manifest doesn't reference —
    /// orphans left by a now-disabled mod, a dropped spill host, or a shell that shed a layer. The skin
    /// textures are already cleared up-front (they're runId-named), but the ss_*/model/material files are
    /// deliberately kept across a run for the change-detection skip, so nothing else ever removes their
    /// orphans. Pass the FINAL redirect map (its rel-path values are what to keep). Safe to delete a file
    /// still tracked in SecondSkinService's hash cache: the write path re-checks File.Exists and rewrites.
    /// </summary>
    private void PruneManagedOutput(IDictionary<string, string> redirects)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in redirects.Values)
            keep.Add(rel.Replace('\\', '/'));   // Rel() emits backslashes, the skin path forward slashes

        foreach (var sub in new[] { "textures", "materials", "models" })
        {
            var dir = Path.Combine(managedModDir, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir))
                if (!keep.Contains(sub + "/" + Path.GetFileName(f)))
                    try { File.Delete(f); } catch { }
        }
    }

    private void ReloadAndRedraw(bool redraw = true)
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        if (redraw && !config.DisableAutoRedraw)
        {
            // Give Penumbra's async reload time to process before the refresh re-requests textures.
            Thread.Sleep(300);
            RefreshPlayerTextures();
        }
    }

    // Force the game to reload the (just-recomposited) player textures. Prefers Glamourer's in-place
    // equipment reload (ReapplyState) to avoid the full despawn/respawn flicker; falls back to a
    // Penumbra full redraw when in-place reload is disabled or Glamourer can't service it.
    /// <summary>
    /// True when the last run wrote a second-skin shell. Glamourer's in-place reload only re-requests
    /// TEXTURES — it does not rebuild the draw object — so a changed .mdl or .mtrl (the shell's geometry,
    /// and the material flags that carry transparency) is simply never picked up. Those runs need a real
    /// redraw, or the character keeps rendering the previous shell until the mod is toggled off and on.
    /// </summary>
    private volatile bool _needFullRedraw;

    /// <summary>
    /// True when the last composite produced second-skin gear shells — i.e. an accessory's model was
    /// redirected to our merged (host + shell) model. Reverting that needs a FULL redraw: clearing the
    /// redirect alone leaves the game rendering the merged model, because an in-place reload never reloads
    /// an accessory's .mdl. Used to decide whether disabling must force a full redraw.
    /// </summary>
    private volatile bool _secondSkinActive;

    /// <summary>The shell host model paths redirected last composite. When the set changes — a spill host was
    /// added or (crucially) dropped as the layer count fell — the vacated accessory must reload its real model,
    /// which only a full redraw does. Compared each composite to force one; an in-place reload can't do it.</summary>
    private HashSet<string> _lastShellHostPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Restore any accessory whose model the second skin replaced back to its original geometry, by
    /// forcing a FULL player redraw so the game reloads the accessory's own .mdl. When the managed mod's
    /// redirects have been cleared (disable, or nothing composited) this reverts the accessory to vanilla;
    /// otherwise it re-renders the current composite from scratch, clearing any shell an in-place reload
    /// left stuck on the accessory. Runs off the framework thread; safe to call anytime.
    /// </summary>
    public void RestoreChangedAccessory()
    {
        Task.Run(() =>
        {
            try
            {
                penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                Thread.Sleep(300);
                Interlocked.Exchange(ref _lastOwnRedrawTick, Environment.TickCount64);
                Plugin.Framework.RunOnFrameworkThread(penumbra.RedrawPlayer).GetAwaiter().GetResult();
                log.Information("[Proteus] restored changed accessory via full redraw");
            }
            catch (Exception ex) { log.Error(ex, "[Proteus] restore changed accessory failed"); }
        });
    }

    private void RefreshPlayerTextures()
    {
        if (config.UseInPlaceReload && !_needFullRedraw)
        {
            Interlocked.Exchange(ref _lastOwnReapplyTick, Environment.TickCount64);
            // ReapplyState mutates game objects (loads weapons, flags slots) synchronously on the
            // calling thread, so it MUST run on the framework thread — calling it from the background
            // recomposite thread causes a native access violation. (Penumbra's RedrawObject, by
            // contrast, queues internally and is safe to call from any thread.)
            bool reapplied;
            try
            {
                reapplied = Plugin.Framework.RunOnFrameworkThread(glamourer.ReapplyPlayerState)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Warning("[Proteus] In-place reload failed on framework thread: {0}", ex.Message);
                reapplied = false;
            }

            if (reapplied)
            {
                log.Debug("[Proteus] Refreshed textures via Glamourer in-place reload.");
                ScheduleSyncSettleRedraw();
                return;
            }
        }

        // A real redraw already re-resolves everything, which is exactly what a peer's snapshot needs —
        // so drop any settle redraw still pending from an earlier in-place reload.
        syncRedrawTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref _lastOwnRedrawTick, Environment.TickCount64);
        penumbra.RedrawPlayer();
    }

    /// <summary>
    /// Arm (or push back) the post-settle redraw that lets sync plugins see this composite. See the
    /// syncRedrawTimer field for why an in-place reload alone is invisible to them. No-op unless a sync
    /// plugin is loaded, so nobody else pays a redraw for a feature they aren't using.
    /// </summary>
    private void ScheduleSyncSettleRedraw()
    {
        if (!config.SyncSettleRedraw || _disposed || !SyncPluginLoaded()) return;

        syncRedrawTimer ??= new Timer(_ =>
        {
            if (_disposed) return;
            try
            {
                // Same bookkeeping as every other redraw we initiate: the tick lets our own
                // redrawn/reapply events be recognised as echoes instead of user activity.
                Interlocked.Exchange(ref _lastOwnRedrawTick, Environment.TickCount64);
                penumbra.RedrawPlayer();
                log.Debug("[Proteus] sync settle: full redraw so paired clients resolve this composite");
            }
            catch (Exception ex) { log.Warning("[Proteus] sync settle redraw failed: {0}", ex.Message); }
        }, null, Timeout.Infinite, Timeout.Infinite);

        syncRedrawTimer.Change(SyncRedrawSettle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Whether any Mare-family sync plugin is loaded. Matched on internal name rather than an exact list
    /// because the forks multiply (MareSynchronos, MareSempiterne/PSync, Snowcloak, …); a false positive
    /// costs one extra redraw a few seconds after editing stops, which is far cheaper than a fork nobody
    /// listed leaving its users with the invisible-character bug. Re-probed every 30s so a plugin enabled
    /// mid-session is picked up.
    /// </summary>
    /// <summary>
    /// True when <paramref name="ex"/> (or anything it wraps) is the AssemblyLoadContext-unloading
    /// failure a background composite hits while the plugin is being torn down. Matched on the message
    /// because the CLR surfaces it as a plain InvalidOperationException inside a FileLoadException, with
    /// no distinguishing type to catch.
    /// </summary>
    private static bool IsLoadContextUnloading(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (IsLoadContextUnloading(inner)) return true;
            if (e.Message.Contains("AssemblyLoadContext is unloading", StringComparison.OrdinalIgnoreCase)
             || e.Message.Contains("was already unloaded", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool SyncPluginLoaded()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref syncProbeTick) < 30_000) return syncPluginLoaded;
        Interlocked.Exchange(ref syncProbeTick, now);

        try
        {
            var hit = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.IsLoaded
                && (p.InternalName.Contains("mare", StringComparison.OrdinalIgnoreCase)
                 || p.InternalName.Contains("sync", StringComparison.OrdinalIgnoreCase)));
            if (hit != null && !syncPluginLoaded)
                log.Information("[Proteus] sync plugin \"{0}\" detected — a full redraw will follow each "
                              + "settled composite so paired clients resolve our textures and shell",
                              hit.InternalName);
            syncPluginLoaded = hit != null;
        }
        catch { /* InstalledPlugins can throw during teardown; keep the last answer */ }

        return syncPluginLoaded;
    }

    // Reload the managed mod, then redraw — but instead of sleeping a fixed, conservative
    // interval before the redraw, poll until Penumbra has actually processed the new
    // redirects. Penumbra applies a ReloadMod asynchronously on its framework handler; the
    // redraw re-requests textures through ResolvePlayer, so redrawing before the reload lands
    // loads stale files. Because the managed mod is highest priority, ResolvePlayer returns
    // our own output, and this run's unique runId in the filename confirms the *new* output is
    // live (not a prior run's). Typical readiness is well under the old 300 ms; the cap keeps a
    // miss from hanging the redraw.
    private void ReloadAndRedrawWhenReady(IDictionary<string, string> redirects, string runId)
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        if (config.DisableAutoRedraw) return;

        // A game path we just redirected to a .tex output — used as the readiness probe.
        var probe = redirects.FirstOrDefault(
            kv => kv.Value.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).Key;

        if (probe != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400)
            {
                var resolved = penumbra.ResolvePlayer(probe);
                if (resolved != null && resolved.Contains(runId, StringComparison.OrdinalIgnoreCase))
                    break;
                Thread.Sleep(15);
            }
            log.Debug("[Proteus] reload ready after {0}ms", sw.ElapsedMilliseconds);
        }
        else
        {
            Thread.Sleep(300); // no texture probe (mtrl-only redirects) — fall back to fixed wait
        }

        RefreshPlayerTextures();
        SchedulePostRedrawBodyTypeCheck();
    }

    // After a composite, verify it used the settled body state. The snapshot at trigger time can
    // reflect PRE-settle state: a body-mod toggle changes which materials are loaded (bibo vs gen3
    // etc.), but that update lands slightly later — sometimes via a redraw, sometimes as an in-place
    // resource reload with NO redraw event at all. So the just-finished composite may have keyed off
    // a stale body-type set (which manifested as sibling synthesis being one toggle behind). This
    // re-fetches the LIVE snapshot (not the cached _activeMtrlSnapshot, which only updates on a redraw
    // event) after a settle delay, and re-composites once if the settled body-type set differs from
    // what was composited. It converges: the re-composite records the settled body type as
    // _lastCompositedBodyType, so the check that follows it finds no change and stops.
    private void SchedulePostRedrawBodyTypeCheck()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600).ConfigureAwait(false);

                // GetResult (not await) so the continuation stays on this background pool thread and
                // never hops onto the framework thread — see TriggerRecomposite for the full rationale.
                HashSet<string>? snapshot;
                try { snapshot = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }
                if (snapshot == null) return;

                var newBodyTypes  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var newCharCodes  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in snapshot)
                {
                    var bt = UVRemapService.InferBodyType(m);
                    if (bt != null)
                    {
                        newBodyTypes.Add(bt);
                        var code = ExtractHumanCharCode(m);
                        if (code != null) newCharCodes.Add(code);
                    }
                }
                var newBodyTypeKey  = newBodyTypes.Count > 0 ? string.Join(",", newBodyTypes.OrderBy(x => x))  : null;
                var newCharCodeKey  = newCharCodes.Count > 0 ? string.Join(",", newCharCodes.OrderBy(x => x))  : null;

                bool bodyTypeChanged = newBodyTypeKey != null &&
                    !string.Equals(newBodyTypeKey, _lastCompositedBodyType, StringComparison.OrdinalIgnoreCase);
                bool charCodeChanged = newCharCodeKey != null &&
                    !string.Equals(newCharCodeKey, _lastCompositedCharCodes, StringComparison.OrdinalIgnoreCase);

                // Enabled body shapes settle AFTER the composite too: toggling "Remove Hip Dips" fires a
                // recomposite that can read the shape state before the game applies it, so the first shell
                // bakes the OLD shape and the morph shows only after a manual refresh. Re-read the settled
                // shapes here and correct, exactly as for body-type/char-code above.
                IReadOnlyDictionary<string, HashSet<string>>? settledShapes = null;
                try
                {
                    settledShapes = Plugin.Framework.RunOnFrameworkThread(
                        () => Interop.BodyShapeReader.ReadEnabledShapes(Plugin.ObjectTable.LocalPlayer?.Address ?? 0))
                        .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { return; }
                bool shapesChanged = !string.Equals(
                    BodyShapeSignature(settledShapes), _lastCompositedBodyShapeSig, StringComparison.Ordinal);

                if (bodyTypeChanged || charCodeChanged || shapesChanged)
                {
                    log.Debug("[Proteus] Post-settle correction: bodyType={0}→{1} charCode={2}→{3} shapesChanged={4}",
                        _lastCompositedBodyType ?? "none", newBodyTypeKey ?? "none",
                        _lastCompositedCharCodes ?? "none", newCharCodeKey ?? "none", shapesChanged);
                    // Publish the settled snapshots so the corrective recomposite uses them directly
                    // (dirty stays false → TriggerRecomposite won't re-fetch a possibly-still-settling one).
                    _activeMtrlSnapshot = snapshot;
                    _activeMtrlSnapshotDirty = false;
                    config.CachedActiveMaterialPaths = snapshot.ToList();
                    if (settledShapes != null) _bodyShapeSnapshot = settledShapes;
                    TriggerRecomposite("post-settle-correction");
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        });
    }

    // ── Compositing ──────────────────────────────────────────────────────────

    // Load the base normal texture, guarding against our own managed mod output
    // (feedback loop: Penumbra may still resolve our path after a reload if the IPC
    // is processed asynchronously, or if path separators differ).
    // Falls back to game SqPack if the resolved path points into managedModDir.
    // After loading, resets alpha to 0 if >50% of pixels are 255 — a reliable
    // fingerprint of our own stale all-255 output (natural base normals avg ~5).
    private byte[] LoadBaseNormal(string gamePath, ref int w, ref int h)
    {
        var diskPath = penumbra.ResolvePlayer(gamePath);
        if (diskPath != null)
        {
            // Normalize separators before comparing so forward/back-slash mismatches don't bypass the guard.
            var diskFull    = Path.GetFullPath(diskPath);
            var managedFull = Path.GetFullPath(managedModDir);
            if (diskFull.StartsWith(managedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(diskFull, managedFull, StringComparison.OrdinalIgnoreCase))
            {
                log.Warning("[Proteus] ResolvePlayer returned our own managed file for {0} — falling back to game data", gamePath);
                diskPath = null;
            }
        }

        var loaded = textureLoader.LoadBaseTexture(diskPath, gamePath);
        if (!loaded.HasValue) return Array.Empty<byte>();

        var rgba = loaded.Value.rgba;
        w = loaded.Value.width;
        h = loaded.Value.height;
        return rgba;
    }

    // Tint + alpha composite using a flat sub-row color (no index texture).
    // When DiffuseR/G/B = 1, this is a standard alpha-over composite.
    /// <summary>
    /// Split a per-pixel loop across cores. <paramref name="body"/> is handed a [from, to) sub-range and
    /// iterates it with the same <paramref name="step"/> the serial loop used.
    ///
    /// Every kernel below writes only to the pixel it is given and reads only that same index from its
    /// inputs — no carried state, no cross-pixel reads — so partitioning cannot change the result and
    /// the output stays byte-identical to the serial loop. Any future kernel that does NOT hold to that
    /// (a running total, a neighbour sample) must not use this.
    ///
    /// Small buffers stay serial: partitioning costs more than it saves below roughly a 256×256 image.
    /// </summary>
    internal static void ParallelPixels(int start, int end, int step, Action<int, int> body)
    {
        const int MinParallelPixels = 256 * 256;
        int span = end - start;
        if (span <= 0) return;
        if (span / step < MinParallelPixels || Environment.ProcessorCount < 2)
        {
            body(start, end);
            return;
        }

        int workers = Math.Min(Environment.ProcessorCount, 16);
        // Round each chunk up to a whole number of steps so no worker can start mid-pixel.
        int chunk = ((span + workers - 1) / workers + step - 1) / step * step;
        Parallel.For(0, workers, k =>
        {
            int from = start + k * chunk;
            if (from >= end) return;
            body(from, Math.Min(from + chunk, end));
        });
    }

    internal static void ApplyFlatOverlay(byte[] baseTex, byte[] ov, ColorTableSubRow row, int w, int h)
    {
        float cr = row.DiffuseR, cg = row.DiffuseG, cb = row.DiffuseB;
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = ov[i + 3] / 255f;
                if (a <= 0f) continue;
                float ia = 1f - a;
                baseTex[i]     = (byte)(ov[i]     / 255f * cr * a * 255f + baseTex[i]     * ia);
                baseTex[i + 1] = (byte)(ov[i + 1] / 255f * cg * a * 255f + baseTex[i + 1] * ia);
                baseTex[i + 2] = (byte)(ov[i + 2] / 255f * cb * a * 255f + baseTex[i + 2] * ia);
            }
        });
    }

    // Write emissive intensity to the normal map alpha where the overlay is opaque.
    internal static void ApplyFlatEmissive(byte[] baseN, byte[] ov, ColorTableSubRow row, int w, int h)
    {
        if (row.Emissive <= 0.001f) return;
        byte intensity = (byte)(row.Emissive * 255f);
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
                if (ov[i + 3] > 0)
                    baseN[i + 3] = Math.Max(baseN[i + 3], intensity);
        });
    }

    // Write emissive intensity to normal alpha driven by index texture row mapping.
    // cov gates which pixels belong to this overlay (diffuse alpha > 0 = inside overlay).
    // For covered pixels, pairIdx (idx R/17) selects the row; only rows in `rows` with
    // emissive > 0 write a value. All other pixels remain at 0 (set by the anyEmissive reset).
    internal static void ApplyIndexedEmissive(
        byte[] baseN, byte[] idx, byte[] cov,
        Dictionary<int, ColorTableRowOverride> rows,
        int w, int h)
    {
        // rows is only read here, never mutated, so concurrent TryGetValue is safe.
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                if (cov[i + 3] == 0) continue; // outside this overlay's coverage
                int pairIdx = idx[i] / 17;
                if (!rows.TryGetValue(pairIdx, out var pair)) continue;
                float blendA = idx[i + 1] / 255f;
                float em = pair.B.Emissive + (pair.A.Emissive - pair.B.Emissive) * blendA;
                if (em > 0.001f)
                    baseN[i + 3] = Math.Max(baseN[i + 3], (byte)(em * 255f));
            }
        });
    }

    // Per-pixel color and emissive driven by index texture.
    // isNormal = false: tint+composite diffuse; isNormal = true: write emissive to normal alpha.
    internal static void ApplyIndexedOverlay(
        byte[] baseTex, byte[] ov, byte[] idx,
        Dictionary<int, ColorTableRowOverride> rows,
        bool isNormal, int w, int h)
    {
        // rows is only read here, never mutated, so concurrent TryGetValue is safe.
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float ovA = ov[i + 3] / 255f;
                if (ovA <= 0f) continue;

                int   pairIdx = idx[i]     / 17;        // red → pair 0–15
                float blendA  = idx[i + 1] / 255f;      // green → lerp B→A (1 = full A, 0 = full B)

                if (!rows.TryGetValue(pairIdx, out var pair)) pair = new ColorTableRowOverride();

                float dr = pair.B.DiffuseR + (pair.A.DiffuseR - pair.B.DiffuseR) * blendA;
                float dg = pair.B.DiffuseG + (pair.A.DiffuseG - pair.B.DiffuseG) * blendA;
                float db = pair.B.DiffuseB + (pair.A.DiffuseB - pair.B.DiffuseB) * blendA;
                float em = pair.B.Emissive  + (pair.A.Emissive  - pair.B.Emissive)  * blendA;

                if (!isNormal)
                {
                    float ia = 1f - ovA;
                    baseTex[i]     = (byte)(ov[i]     / 255f * dr * ovA * 255f + baseTex[i]     * ia);
                    baseTex[i + 1] = (byte)(ov[i + 1] / 255f * dg * ovA * 255f + baseTex[i + 1] * ia);
                    baseTex[i + 2] = (byte)(ov[i + 2] / 255f * db * ovA * 255f + baseTex[i + 2] * ia);
                }
                else
                {
                    baseTex[i + 3] = Math.Max(baseTex[i + 3], (byte)(em * 255f));
                }
            }
        });
    }

    // Partial-derivative linear add for normal maps: XY (tangent/bitangent) components are decoded
    // to signed [-1,1] space, the overlay's contribution (scaled by alpha) is added to the base,
    // then re-encoded. Blue (skin color influence) and Alpha (emissive) are left untouched — each
    // has its own dedicated pass. This compounds detail: overlay bumps stack on top of base bumps
    // rather than lerping them away.
    /// <summary>
    /// Overwrite the base normal with <paramref name="src"/> where it applies, instead of adding to it.
    ///
    /// A Masks option's relief normal is the surface there — a strap, a seam, a weave — not a bump to
    /// pile on top of whatever the overlay already had. Compounding them layers two reliefs and reads
    /// as noise, so the mask's own normal replaces the base, faded in by its alpha.
    /// </summary>
    internal static void ReplaceNormal(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
                if (a <= 0f) continue;

                dst[i]     = (byte)Math.Clamp(dst[i]     + (src[i]     - dst[i])     * a, 0, 255);
                dst[i + 1] = (byte)Math.Clamp(dst[i + 1] + (src[i + 1] - dst[i + 1]) * a, 0, 255);
            }
        });
    }

    // ── Mask trim convention ──────────────────────────────────────────────────
    // A Proteus mask replaces the surface beneath it (its normal, and its colour) only on its TRIM — the
    // authored seams/edges — while its plain FILL leaves the fabric/skin below to show through. A texel is
    // the trim if EITHER of two authored signals holds:
    //   • its relief normal deviates from flat (a real bump — the raised lip), or
    //   • its coverage texel is near-white (the trim is painted bright over a mid-grey garment body).
    // Neither alone covers the whole band (a flat trim centre has no bump; relief is often drawn past the
    // coverage edge), so both are used. Shape/brightness are read from the coverage LUMINANCE — the mask
    // PNGs' alpha channel is not a usable shape signal (it is inverted/unused on these exports).
    internal const int MaskReliefDeadzone = 8;    // |R-128|+|G-128| below this = flat (no bump)
    internal const int MaskTrimLuma       = 190;  // coverage luminance at/above this = bright trim band

    /// <summary>True if texel <paramref name="o"/> is a mask's trim (see the convention above). RGBA arrays.</summary>
    internal static bool IsMaskTrim(byte[] relief, byte[] coverage, int o)
    {
        if (Math.Abs(relief[o] - 128) + Math.Abs(relief[o + 1] - 128) >= MaskReliefDeadzone) return true;
        int luma = (coverage[o] * 77 + coverage[o + 1] * 150 + coverage[o + 2] * 29) >> 8;
        return luma >= MaskTrimLuma;
    }

    /// <summary>
    /// Fold several masks' relief into <paramref name="baseNormal"/> (RGBA), TOP-FIRST with a per-texel claim:
    /// the higher mask's TRIM owns the texel (writes its R/G, blue left alone), and a lower mask can never draw
    /// its trim where a higher one already claimed — so overlapping/parallel trims resolve to just the top
    /// one instead of stacking into a doubled ridge. A mask's plain fill does NOT claim, so a lower mask's
    /// trim still shows through a higher mask's body (e.g. a neckline strap over a leotard). Shared by the skin
    /// body normal and the gear mask-shell so the two can't drift.
    /// </summary>
    internal static void CombineMaskReliefs(byte[] baseNormal, int w, int h,
        IReadOnlyList<(byte[] Relief, byte[] Coverage)> masksTopFirst)
    {
        if (masksTopFirst.Count == 0) return;
        var claimed = new bool[w * h];
        foreach (var (relief, coverage) in masksTopFirst)
        {
            if (relief == null || coverage == null) continue;
            ParallelPixels(0, w * h, 1, (from, to) =>
            {
                for (int p = from; p < to; p++)
                {
                    if (claimed[p]) continue;               // a higher mask's trim already owns this texel
                    int o = p * 4;
                    if (!IsMaskTrim(relief, coverage, o)) continue;
                    baseNormal[o]     = relief[o];          // R/G only — blue is unused for masks
                    baseNormal[o + 1] = relief[o + 1];
                    claimed[p] = true;
                }
            });
        }
    }

    internal static void CompoundNormal(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
                if (a <= 0f) continue;

                float bx = dst[i]     / 127.5f - 1f;
                float by = dst[i + 1] / 127.5f - 1f;
                float ox = src[i]     / 127.5f - 1f;
                float oy = src[i + 1] / 127.5f - 1f;

                dst[i]     = (byte)Math.Clamp((bx + ox * a + 1f) * 127.5f, 0, 255);
                dst[i + 1] = (byte)Math.Clamp((by + oy * a + 1f) * 127.5f, 0, 255);
            }
        });
    }

    // Standard alpha-over: dst = src * src.a + dst * (1 - src.a). Dst alpha unchanged.
    // mask: if provided, effective alpha = min(src alpha, mask alpha) — used so a diffuse overlay
    // silhouette gates the normal composite (invisible diffuse pixels stay at base normal).
    internal static void AlphaComposite(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
                if (a <= 0f) continue;
                float ia = 1f - a;
                dst[i]     = (byte)(src[i]     * a + dst[i]     * ia);
                dst[i + 1] = (byte)(src[i + 1] * a + dst[i + 1] * ia);
                dst[i + 2] = (byte)(src[i + 2] * a + dst[i + 2] * ia);
            }
        });
    }

    // Fade the normal map's BLUE channel (skin.shpk "skin color influence") toward black under the
    // overlay so the shader stops re-tinting opaque overlay pixels by skin tone. The amount is
    // weighted by the composited overlay colour's luminance: bright pixels (where skin tint is most
    // visible — white-over-dark-skin reads beige) get full suppression, dark pixels get little or
    // none (skin tint is invisible on dark colour anyway, and leaving the channel intact avoids the
    // specular/subsurface shift that reads as extra shine). cov.alpha is the overlay opacity (sheer
    // gaps keep skin tone); `diffuse` is the composited diffuse at the normal's resolution, null →
    // luminance treated as 1 (coverage-only). `strength` is the global user multiplier.
    internal static void SuppressSkinColorInfluence(byte[] baseN, byte[] cov, byte[]? diffuse, int w, int h, float strength = 1f)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = cov[i + 3] / 255f * strength;
                if (a <= 0f) continue;
                if (diffuse != null)
                {
                    float lum = (0.299f * diffuse[i] + 0.587f * diffuse[i + 1] + 0.114f * diffuse[i + 2]) / 255f;
                    a *= lum;
                    if (a <= 0f) continue;
                }
                baseN[i + 2] = (byte)(baseN[i + 2] * (1f - a));
            }
        });
    }

    /// <summary>
    /// Separable box blur of a single-channel (1 byte/pixel) plane. Two box passes per iteration
    /// (horizontal then vertical) approximate a smooth Gaussian falloff — used to spread a strap's
    /// coverage into the surrounding skin for the ambient-occlusion halo. Unlike the ParallelPixels
    /// kernels this reads neighbours, so it parallelises over independent rows/columns instead.
    /// </summary>
    internal static byte[] BlurCoverage(byte[] src, int w, int h, int radius, int iterations = 2)
    {
        if (radius < 1 || w <= 0 || h <= 0 || src.Length < w * h) return (byte[])src.Clone();
        var a = (byte[])src.Clone();
        var b = new byte[a.Length];
        for (int it = 0; it < iterations; it++)
        {
            BoxBlurH(a, b, w, h, radius);   // a -> b (rows)
            BoxBlurV(b, a, w, h, radius);   // b -> a (columns)
        }
        return a;
    }

    // Horizontal running-sum box blur, one row per worker (rows are independent, so this is safe to
    // parallelise even though it reads neighbours — which ParallelPixels forbids).
    private static void BoxBlurH(byte[] src, byte[] dst, int w, int h, int radius)
    {
        int window = radius * 2 + 1;
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            int sum = 0;
            // Seed the window at x = 0: clamp samples off the left edge to column 0.
            for (int k = -radius; k <= radius; k++)
                sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = (byte)(sum / window);
                int add = Math.Clamp(x + radius + 1, 0, w - 1);
                int sub = Math.Clamp(x - radius, 0, w - 1);
                sum += src[row + add] - src[row + sub];
            }
        });
    }

    // Vertical running-sum box blur, one column per worker.
    private static void BoxBlurV(byte[] src, byte[] dst, int w, int h, int radius)
    {
        int window = radius * 2 + 1;
        Parallel.For(0, w, x =>
        {
            int sum = 0;
            for (int k = -radius; k <= radius; k++)
                sum += src[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (byte)(sum / window);
                int add = Math.Clamp(y + radius + 1, 0, h - 1);
                int sub = Math.Clamp(y - radius, 0, h - 1);
                sum += src[add * w + x] - src[sub * w + x];
            }
        });
    }

    /// <summary>
    /// Bake a soft contact-shadow onto the skin diffuse hugging the OUTSIDE edge of each strap.
    /// <paramref name="strap"/> is the sharp coverage (≈255 under the strap, 0 on skin); <paramref name="blurred"/>
    /// is that coverage spread by <see cref="BlurCoverage"/>. The halo = blurred·(1−strap) keeps only the
    /// spread that lands OUTSIDE the strap, so the interior isn't darkened and the shadow fades with
    /// distance. RGB is multiplied down by (1 − strength·halo); alpha is untouched. Per-pixel, so it
    /// satisfies the ParallelPixels contract (the neighbour work happened in the blur).
    /// </summary>
    // coveredAbove (optional, single-channel w*h): where a HIGHER-stacked garment is opaque the shadow is
    // suppressed — a lower garment's contact shadow can't fall on skin that another layer covers.
    internal static void ApplyAmbientOcclusion(byte[] baseD, byte[] strap, byte[] blurred, int w, int h, float strength,
        byte[]? coveredAbove = null)
    {
        if (strength <= 0f) return;
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                float s = strap[p] / 255f;
                float halo = (blurred[p] / 255f) * (1f - s);
                if (coveredAbove != null) halo *= 1f - coveredAbove[p] / 255f;   // hidden under a higher layer
                if (halo <= 0f) continue;
                float k = 1f - strength * halo;
                if (k >= 1f) continue;
                if (k < 0f) k = 0f;
                int o = p * 4;
                baseD[o]     = (byte)(baseD[o]     * k);
                baseD[o + 1] = (byte)(baseD[o + 1] * k);
                baseD[o + 2] = (byte)(baseD[o + 2] * k);
            }
        });
    }

    /// <summary>
    /// Perturb the skin normal at strap / garment edges so the skin reads as pressed IN under the strap.
    /// The tilt is the gradient of the blurred coverage (edge-concentrated: zero on flat interior/skin),
    /// gated to the skin OUTSIDE the strap — the same band the AO shadow darkens — so the two effects line
    /// up. The surface leans toward increasing coverage (toward the strap), i.e. the skin slopes down into
    /// the strap, giving a concave groove.
    ///
    /// FFXIV uses OpenGL-style tangent normals: the green channel is +Y pointing "up", whereas texture rows
    /// increase downward — so the green offset negates the row-space gradient (gy). If the vertical edges of
    /// a strap ever look inverted (bulging out instead of pressed in) this single sign is what to flip.
    /// Writes R/G only (X/Y); blue (skin-color influence) and alpha (emissive) are left untouched, exactly
    /// like <see cref="CompoundNormal"/>. Reads neighbours, so it parallelises over independent rows.
    ///
    /// Two things keep the tilt physical, and both exist because their absence showed up as a thin BLOWN-OUT
    /// WHITE rim tracing every garment edge:
    /// <list type="number">
    /// <item>The tilted (x, y) is clamped by VECTOR LENGTH, not per component. A tangent normal implies
    /// z = sqrt(1 − x² − y²); once x² + y² exceeds 1 that is imaginary, the shader's z collapses to 0, and the
    /// surface becomes a mirror-edge slab that blows out to white. Clamping x and y separately (as the byte
    /// conversion alone does) never catches this — it only bites on diagonal and curved edges, where BOTH
    /// gradients are large at once, which is exactly where the rim appeared.</item>
    /// <item>The gradient is normalised by the blur <paramref name="radius"/>. It is a per-PIXEL difference
    /// across a ramp whose width is set by that radius, so its magnitude scales as 1/radius: the very same
    /// depth setting was a gentle slope on a 4K skin map and a saturated wall on a 1K one.</item>
    /// </list>
    /// </summary>
    /// <param name="radius">
    /// The blur radius used to build <paramref name="blurred"/>. Defaults to <see cref="IndentRefRadius"/>,
    /// which leaves the tilt exactly as it was at the resolution the depth default was tuned against.
    /// </param>
    internal static void ApplyNormalIndent(byte[] baseN, byte[] blurred, byte[] strap, int w, int h, float strength,
        byte[]? coveredAbove = null, int radius = IndentRefRadius, byte[]? inside = null)
    {
        if (strength <= 0f) return;
        if (inside != null && inside.Length < w * h) inside = null;
        // Depth was tuned on a 2048-wide skin map at the 0.003 default softness ⇒ radius 6. Scaling by
        // radius/6 makes the setting mean the same slope everywhere, and a no-op at that reference.
        float gScale = radius > 0 ? radius / (float)IndentRefRadius : 1f;
        Parallel.For(0, h, y =>
        {
            int row     = y * w;
            int rowUp   = (y > 0 ? y - 1 : 0) * w;
            int rowDown = (y < h - 1 ? y + 1 : h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                float edge = 1f - strap[row + x] / 255f;   // skin side of the edge only
                if (coveredAbove != null) edge *= 1f - coveredAbove[row + x] / 255f;   // hidden under a higher layer
                if (edge <= 0f) continue;
                int xm = x > 0 ? x - 1 : 0;
                int xp = x < w - 1 ? x + 1 : w - 1;

                // Never differentiate ACROSS a UV-island border. Padding is not surface — its value comes
                // from a different computation than the island's own blur, so a central difference that
                // straddles the border sees a one-texel step and reports a gradient ~3x the interior
                // (measured: 6.9 against 2.2). ApplyNormalIndent then carves that into a crease following
                // the island outline, which is the faint shadow cutoff along a seam. Dropping to a one-sided
                // difference there keeps the real slope and loses the phantom step.
                //
                // The scale-up is relative to the span this texel WOULD have had, not to a fixed 2 — at the
                // texture's own outer row/column the difference was already one-sided before this change,
                // and normalising against 2 there would silently double the indent on a path this fix isn't
                // about. Comparing against baseSpan makes the correction apply only where the island mask
                // actually shortened the difference.
                int rUp = rowUp, rDn = rowDown;                 // per-texel copies: the row bases must not move
                int baseSpanX = xp - xm, baseSpanY = (rDn - rUp) / w;
                int spanX = baseSpanX, spanY = baseSpanY;
                if (inside != null)
                {
                    if (inside[row + xm] == 0) xm = x;
                    if (inside[row + xp] == 0) xp = x;
                    if (inside[rUp + x] == 0) rUp = row;
                    if (inside[rDn + x] == 0) rDn = row;
                    spanX = xp - xm;
                    spanY = (rDn - rUp) / w;
                }
                float gx = spanX == 0 ? 0f : (blurred[row + xp] - blurred[row + xm]) / 255f * ((float)baseSpanX / spanX);
                float gy = spanY == 0 ? 0f : (blurred[rDn + x] - blurred[rUp + x]) / 255f * ((float)baseSpanY / spanY);
                if (gx == 0f && gy == 0f) continue;
                int i = (row + x) * 4;
                float bx = baseN[i]     / 127.5f - 1f;
                float by = baseN[i + 1] / 127.5f - 1f;
                bx += strength * gx * edge * gScale;        // lean X toward the strap
                by -= strength * gy * edge * gScale;        // green = +Y up (OpenGL); rows go down → negate

                // Keep (x, y) inside the unit disc so the implied z stays real. Scaling both by the same
                // factor preserves the tilt DIRECTION (the groove still points into the strap) and only
                // caps how steep it gets.
                float len2 = bx * bx + by * by;
                if (len2 > MaxIndentTilt * MaxIndentTilt)
                {
                    float k = MaxIndentTilt / MathF.Sqrt(len2);
                    bx *= k;
                    by *= k;
                }

                baseN[i]     = (byte)Math.Clamp((bx + 1f) * 127.5f, 0, 255);
                baseN[i + 1] = (byte)Math.Clamp((by + 1f) * 127.5f, 0, 255);
            }
        });
    }

    /// <summary>
    /// Whether a material is painted in the BODY's UV space — the layout gear shells (cut from the body
    /// mesh) and Masks-group coverage art are authored in. Anything else (face, hair, tail, and equipment
    /// meshes with their own layout) would sample that art as arbitrary shapes.
    ///
    /// <see cref="UVRemapService.InferBodyType"/> is the primary test: it recognises the body-UV suffixes
    /// (<c>_bibo</c>, <c>_eve</c>) wherever they live, which matters because body mods do route body-UV skin
    /// through <c>chara/equipment/</c> slots. The <c>/obj/body/</c> fallback then catches body materials
    /// whose suffix it can't classify — a mod's custom <c>_nails</c> / <c>_piercings</c> naming, say.
    /// </summary>
    internal static bool IsBodyUvMaterial(string mtrlGamePath)
        => UVRemapService.InferBodyType(mtrlGamePath) != null
        || mtrlGamePath.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replace an AO silhouette's values OUTSIDE the UV islands with a smooth extension of the values just
    /// inside, so the blur that follows sees no step at an island border.
    /// <para/>
    /// Why: texture space between islands is padding, and art tools dilate into it — but they fill it with
    /// whatever suits the DIFFUSE, not with something consistent with a coverage mask. Measured on a real
    /// mask, the padding is 90% covered at a mean of 40 (and ~127 across the torso/leg gutter) while the body
    /// either side is 94% EMPTY. <see cref="BlurCoverage"/> then drags that plateau inward and
    /// <see cref="ApplyNormalIndent"/> carves its gradient into a crease that follows the UV seam across bare
    /// skin — which is what it looks like in game.
    /// <para/>
    /// Two things were tried first and both were wrong, for the same reason — they treat the symptom at the
    /// border instead of the padding behind it:
    /// <list type="bullet">
    /// <item>ZEROING the padding. Kills the seam, but substitutes an equal step in the other direction, so AO
    /// weakens within a blur-reach of every island border — and on a body UV that's most of the skin. Reported
    /// as "AO near mask edges is gone".</item>
    /// <item>FADING the effect out near borders. Only halves the artefact: the padding still bleeds inward,
    /// and the fade is already back to ~1 by the time the bled gradient peaks.</item>
    /// </list>
    /// Extending instead means the padding AGREES with the island edge, so there is no discontinuity to blur
    /// and nothing near a border is weakened.
    /// <para/>
    /// The extension is a normalised (masked) blur: average the in-island values over the window and divide
    /// by how much of the window was in-island. Where too little of the window is in-island to average
    /// meaningfully — deep padding, far from any island — it falls to 0, which is harmless because the blur
    /// can't reach the island from there. Valid texels are copied through EXACTLY; only padding is rewritten.
    /// </summary>
    internal static byte[] ExtendIntoPadding(byte[] plane, byte[] inside, int w, int h, int radius,
                                             IslandBlurCache? cache = null)
    {
        if (plane.Length < w * h || inside.Length < w * h || w <= 0 || h <= 0) return plane;

        // Masked sums: numerator over in-island values, denominator over in-island weight. The denominator
        // is the blurred island mask — no dependence on `plane` — so it is reused across this material's mods.
        var masked = new byte[w * h];
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++) masked[p] = inside[p] != 0 ? plane[p] : (byte)0;
        });
        var num = BlurCoverage(masked, w, h, radius);
        var den = cache != null
            ? cache.PaddingDenominator(inside, radius, () => BlurCoverage(inside, w, h, radius))
            : BlurCoverage(inside, w, h, radius);

        // Below this share of the window being in-island the average is noise, not an extension.
        const int MinWeight = 8;   // ~3% of a full window
        var outp = (byte[])plane.Clone();
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                if (inside[p] != 0) continue;                       // on the body — leave exactly as authored
                outp[p] = den[p] >= MinWeight
                    ? (byte)Math.Clamp(num[p] * 255 / den[p], 0, 255)
                    : (byte)0;
            }
        });
        return outp;
    }

    /// <summary>
    /// The models that own a skin material's UV layout — the meshes whose seams say which island edge
    /// continues into which. A skin material names its race, and the body is DRAWN as the four bare-body
    /// equipment parts, so "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl"
    /// becomes the c0201e0000 top / dwn / glv / sho set.
    /// <para/>
    /// NOT chara/human/…/c0201b0001.mdl, which is what the material path looks like it points at: body
    /// replacers (Bibo+, gen3) ship their mesh as the e0000 equipment parts and leave that model vanilla,
    /// so reading it would hand back a different body's topology and UVs entirely. The seams must come from
    /// the mesh actually being drawn — the same set <see cref="SecondSkinService"/> shells.
    /// <para/>
    /// Null for anything that isn't a human body material; gear and face have their own layouts.
    /// </summary>
    internal static string[]? BodyModelPathsFor(string mtrlGamePath)
    {
        if (string.IsNullOrEmpty(mtrlGamePath)) return null;
        var m = BodyMaterialPath.Match(mtrlGamePath.Replace('\\', '/'));
        if (!m.Success) return null;
        string code = m.Groups[1].Value;
        return [$"chara/equipment/e0000/model/c{code}e0000_top.mdl",
                $"chara/equipment/e0000/model/c{code}e0000_dwn.mdl",
                $"chara/equipment/e0000/model/c{code}e0000_glv.mdl",
                $"chara/equipment/e0000/model/c{code}e0000_sho.mdl"];
    }

    private static readonly Regex BodyMaterialPath =
        new(@"^chara/human/c(\d{4})/obj/body/b(\d{4})/material/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Connected-component labels of a UV-island mask: 0 for padding, 1..<paramref name="count"/>
    /// for islands. Two-pass union-find over 4-connectivity — one linear scan, then a root resolve, which
    /// matters because this runs on a 4096² plane.</summary>
    internal static int[] LabelIslands(byte[] inside, int w, int h, out int count)
    {
        var labels = new int[w * h];
        // parent[i] is the provisional label i's union-find parent. Unions always point the higher index at
        // the lower one, so a root is exactly an i with parent[i] == i, and roots are found in ascending
        // order — which is what lets the remap below resolve in a single forward pass.
        var parent = new List<int> { 0 };
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x;
                if (inside[p] == 0) continue;
                int west  = x > 0 ? labels[p - 1] : 0;
                int north = y > 0 ? labels[p - w] : 0;
                if (west == 0 && north == 0)
                {
                    parent.Add(parent.Count);
                    labels[p] = parent.Count - 1;
                }
                else if (west != 0 && north != 0)
                {
                    labels[p] = Math.Min(west, north);
                    int a = Find(west), b = Find(north);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
                else labels[p] = west != 0 ? west : north;
            }
        }

        var remap = new int[parent.Count];
        count = 0;
        for (int i = 1; i < parent.Count; i++)
        {
            int r = Find(i);
            remap[i] = r == i ? ++count : remap[r];   // r < i, so remap[r] is already resolved
        }
        ParallelPixels(0, labels.Length, 1, (from, to) =>
        {
            for (int p = from; p < to; p++) if (labels[p] != 0) labels[p] = remap[Find(labels[p])];
        });
        return labels;
    }

    /// <summary>How far past an island border the seam map must reach to cover the blur's window.
    /// <see cref="BlurCoverage"/> is SEPARABLE — two box passes of the given radius per axis — so its window
    /// is a SQUARE reaching 2*radius in x and in y independently, and therefore 2*radius*sqrt(2) diagonally.
    /// Filling only a disc of 2*radius leaves the window's corners unmapped, and they fall back to
    /// extrapolation: measured, that cost 19.94 mean cross-seam mismatch against 17.49 once the corners are
    /// covered. Beyond this there is nothing left to gain (48 measured at 17.52).</summary>
    internal static int SeamReach(int radius) => (int)Math.Ceiling(2 * radius * 1.4143);

    /// <summary>Below this share of the blur window being on the SAME island, the renormalised average is
    /// noise rather than a mean, and the texel keeps its authored value instead.</summary>
    private const int MinIslandWeight = 8;   // ~3% of a full window

    /// <summary>For every padding texel, the label of the CLOSEST island — which island's dilation that bit
    /// of gutter is standing in for. Multi-source breadth-first expansion from the island borders outward, so
    /// each texel is reached first along its shortest path. 0 where nothing is reachable.</summary>
    internal static int[] NearestIslandOwner(int[] labels, int w, int h)
    {
        var owner = new int[w * h];
        var frontier = new List<int>();

        // Seed: padding texels touching an island. Seeding from the islands themselves would enqueue
        // millions of interior texels that can never own anything.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x;
                if (labels[p] != 0 || owner[p] != 0) continue;
                int L = 0;
                if (x > 0 && labels[p - 1] != 0) L = labels[p - 1];
                else if (x < w - 1 && labels[p + 1] != 0) L = labels[p + 1];
                else if (y > 0 && labels[p - w] != 0) L = labels[p - w];
                else if (y < h - 1 && labels[p + w] != 0) L = labels[p + w];
                if (L == 0) continue;
                owner[p] = L;
                frontier.Add(p);
            }
        }

        var next = new List<int>();
        while (frontier.Count > 0)
        {
            next.Clear();
            foreach (int p in frontier)
            {
                int L = owner[p], x = p % w, y = p / w;
                if (x > 0)     Push(p - 1);
                if (x < w - 1) Push(p + 1);
                if (y > 0)     Push(p - w);
                if (y < h - 1) Push(p + w);

                void Push(int q)
                {
                    if (labels[q] != 0 || owner[q] != 0) return;
                    owner[q] = L;
                    next.Add(q);
                }
            }
            (frontier, next) = (next, frontier);
        }
        return owner;
    }

    /// <summary>
    /// The labelling depends only on the island mask and the plane size, never on the mask being composited,
    /// so it is computed once and reused — it is a full serial pass over a 4096² plane.
    /// <para/>
    /// Instance-scoped and capped, not static: each entry is two <c>int[w*h]</c> (134 MB at 4096²), so a
    /// static dictionary would pin every body type the session ever drew for the plugin's whole lifetime.
    /// Keyed on a checksum of the mask itself rather than the body type alone, so a transfer map replaced on
    /// disk can't be served a stale labelling.
    /// </summary>
    private readonly List<((string BodyType, int W, int H, long Sum) Key, (int[] Labels, int[] Owner, int Count) Value)>
        islandLabelCache = new();
    private const int MaxCachedLabelings = 2;
    private readonly object islandLabelLock = new();

    internal (int[] Labels, int[] Owner, int Count) IslandLabelsFor(string bodyType, byte[] inside, int w, int h)
    {
        // Cheap but content-sensitive: the mask is 0/255, so a strided sum distinguishes any real change.
        long sum = 0;
        for (int p = 0; p < w * h; p += 97) sum += inside[p];
        var key = (bodyType, w, h, sum);

        lock (islandLabelLock)
        {
            for (int i = 0; i < islandLabelCache.Count; i++)
                if (islandLabelCache[i].Key == key) return islandLabelCache[i].Value;
        }

        // Built outside the lock — it is a serial pass over the whole plane, and holding the lock would
        // stall an unrelated material behind it. A concurrent duplicate build wastes work, never misleads.
        var labels = LabelIslands(inside, w, h, out int count);
        var built = (labels, NearestIslandOwner(labels, w, h), count);

        lock (islandLabelLock)
        {
            for (int i = 0; i < islandLabelCache.Count; i++)
                if (islandLabelCache[i].Key == key) return islandLabelCache[i].Value;
            if (islandLabelCache.Count >= MaxCachedLabelings) islandLabelCache.RemoveAt(0);
            islandLabelCache.Add((key, built));
        }
        return built;
    }

    /// <summary>
    /// Blur an AO silhouette without ever sampling across a UV-island border: each texel averages only
    /// texels of its OWN island, renormalised by how much of the window that was. This is what makes the
    /// AO come from the mask rather than from the UV layout.
    /// <para/>
    /// Why not a plain blur: the texture space between islands is padding, and art tools dilate into it with
    /// whatever suits the DIFFUSE, not something consistent with a coverage mask. Measured on a real mask,
    /// the padding is 90% covered at a mean of 40 (~127 across the torso/leg gutter) while the body either
    /// side is 94% EMPTY. A plain blur drags that plateau inward and <see cref="ApplyNormalIndent"/> carves
    /// its gradient into a crease that follows the UV seam across bare skin.
    /// <para/>
    /// Why not merely extend the island's own values into the padding first — which is what this replaced —
    /// is that the gutters are NARROW. Measured on the bibo layout (the destination Valid of
    /// gen3_to_bibo_transfer, which is what <see cref="UVRemapService.IslandMask"/> returns for it): 19
    /// components, with gaps from 6px, against a blur whose window reaches 2*radius per axis. So however
    /// well the padding is repaired, the blur still samples straight across a gutter into the NEIGHBOURING
    /// island, which in 3D is an unrelated part of the body. Where a strap crosses the torso/leg seam that
    /// mixes leg coverage into the torso and back, distorting the AO of a real mask edge. Restricting the
    /// window to one island removes both failures at once.
    /// <para/>
    /// Recorded so they aren't retried — all rejected against this same artefact: ZEROING the padding
    /// (substitutes an equal step the other way, so AO weakens within a blur-reach of every border, reported
    /// as "AO near mask edges is gone"); FADING the effect out near borders (only halves it — the fade is
    /// back to ~1 by the time the bled gradient peaks); CLIPPING the silhouette to the island mask (the same
    /// step problem); SCALING the radius to the trim's feature size (removes the seam but loses the effect —
    /// a filament mask wants radius ~4 where the look wants ~12).
    /// <para/>
    /// An island may however look at ITS OWN padding — the gutter texels nearer to it than to any other
    /// island, filled with its own edge values continued outward. That matters, because a strap crossing a
    /// real 3D seam is one strap on the body but two pieces in UV: restricting strictly to the island drops
    /// the AO beside it to 81% of what it should be, on exactly the texels where cloth meets a seam.
    /// Continuing the island's own edge outward stands in for the part that carried on across the seam and
    /// restores it to 99%, while still never reading the unrelated island across the gutter.
    /// <para/>
    /// More than a blur-reach inside an island the window is entirely same-island, so this is identical to a
    /// plain blur there — verified against the previous pipeline at max difference 0.00 over the interior.
    /// Cost is bounded: the work is per island over its own bounding box, and a body layout has ~10.
    /// </summary>
    /// <summary>
    /// Scratch for <see cref="BlurCoverageWithinIslands"/>: the parts of its work that do NOT depend on the
    /// silhouette being blurred, so they can be computed once and reused for every mod on a material.
    /// <para/>
    /// Per island the method runs four blurs; two of them — the island-mask blur and the gutter-mask blur —
    /// are functions of the UV layout and the radius alone. Every branch that decides gutter membership is
    /// likewise plane-independent (island test, seam target on-island test, and the min-weight test against
    /// the island-mask blur); only the VALUES written into the numerator depend on the plane. Four mods
    /// qualified for AO on one body measured ~27 full-map-equivalent 4096² blurs; caching these halves it.
    /// <para/>
    /// Deliberately NOT shared between materials. Materials composite in parallel
    /// (<c>MaxDegreeOfParallelism = 4</c>) while the mod loop inside one material is sequential, so a
    /// per-material instance is both the exact scope of the reuse and free of any locking.
    /// </summary>
    internal sealed class IslandBlurCache
    {
        private int[]? labels, owner, seam;
        private byte[]? inside;
        private int radius = -1;
        private int count = -1;
        private bool ready;

        internal int[]? Bx0, By0, Bx1, By1;   // island bounding boxes — a function of labels alone
        internal byte[]?[]? Bd, Cd;           // per-island plane-independent blurs
        private byte[]? padDen;               // ExtendIntoPadding's denominator — a function of inside alone

        /// <summary>
        /// True when everything held was derived from exactly these inputs AND is fully populated. Reference
        /// equality, not content: the caller hands back the same cached arrays each time, so this is exact
        /// and cheap, and it misses naturally when the body or the softness (radius) changes.
        /// <para/>
        /// The <see cref="ready"/> flag is the point. The fill runs in stages — <see cref="Reset"/> allocates
        /// Bd/Cd, then the bounding boxes are stored, then the per-island loop fills the blurs — so ANY field
        /// used as a proxy for "populated" is true partway through, and a reader that trusted it would meet a
        /// null Bd entry. Only the producer knows when it has finished, so only the producer sets this, via
        /// <see cref="MarkReady"/> after the last island. <paramref name="count"/> is in the key because it
        /// sizes Bd/Cd; a larger one against the same labels would index past them.
        /// </summary>
        internal bool Matches(int[] labels, int[] owner, byte[] inside, int[]? seam, int radius, int count)
            => ready && this.count == count
            && ReferenceEquals(this.labels, labels) && ReferenceEquals(this.owner, owner)
            && ReferenceEquals(this.inside, inside) && ReferenceEquals(this.seam, seam)
            && this.radius == radius;

        internal void Reset(int[] labels, int[] owner, byte[] inside, int[]? seam, int radius, int count)
        {
            ready = false;
            this.labels = labels; this.owner = owner; this.inside = inside; this.seam = seam;
            this.radius = radius; this.count = count;
            Bx0 = By0 = Bx1 = By1 = null;
            Bd = new byte[count + 1][];
            Cd = new byte[count + 1][];
            padDen = null;
        }

        /// <summary>Publish the entry: every island's blurs are now stored. Anything that throws before this
        /// leaves the cache unusable rather than half-filled, so the next call rebuilds instead of reading a
        /// null.</summary>
        internal void MarkReady() => ready = true;

        /// <summary>
        /// <see cref="ExtendIntoPadding"/>'s blurred island mask, built on first use. Self-validating: a
        /// cache built for a different mask or radius is ignored rather than trusted, because the caller
        /// there can't otherwise tell — the denominator only divides, so a stale one yields a plausible
        /// wrong answer instead of a crash.
        /// </summary>
        internal byte[] PaddingDenominator(byte[] inside, int radius, Func<byte[]> build)
        {
            bool mine = ReferenceEquals(this.inside, inside) && this.radius == radius;
            if (mine && padDen != null) return padDen;
            var den = build();
            if (mine) padDen = den;
            return den;
        }
    }

    internal static byte[] BlurCoverageWithinIslands(byte[] plane, int[] labels, int[] owner, int islandCount,
                                                     byte[] inside, int[]? seamSource, int w, int h, int radius,
                                                     IslandBlurCache? cache = null)
    {
        if (w <= 0 || h <= 0 || islandCount <= 0 ||
            plane.Length < w * h || labels.Length < w * h || owner.Length < w * h || inside.Length < w * h)
            return BlurCoverage(plane, w, h, radius);
        if (seamSource != null && seamSource.Length < w * h) seamSource = null;

        int n = islandCount + 1;
        // Everything below that doesn't depend on `plane` is reused across the mods on this material.
        bool cached = cache != null && cache.Matches(labels, owner, inside, seamSource, radius, islandCount);
        if (cache != null && !cached) cache.Reset(labels, owner, inside, seamSource, radius, islandCount);

        // Per-island bounding boxes, so the cost is the sum of island areas rather than islands × map.
        // A function of `labels` alone, and a full-map scan, so it rides the cache too.
        int[] bx0, by0, bx1, by1;
        if (cached)
        {
            bx0 = cache!.Bx0!; by0 = cache.By0!; bx1 = cache.Bx1!; by1 = cache.By1!;
        }
        else
        {
            bx0 = new int[n]; by0 = new int[n]; bx1 = new int[n]; by1 = new int[n];
            for (int i = 0; i < n; i++) { bx0[i] = int.MaxValue; by0[i] = int.MaxValue; bx1[i] = -1; by1[i] = -1; }
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int L = labels[row + x];
                    if (L <= 0 || L >= n) continue;
                    if (x < bx0[L]) bx0[L] = x;
                    if (x > bx1[L]) bx1[L] = x;
                    if (y < by0[L]) by0[L] = y;
                    if (y > by1[L]) by1[L] = y;
                }
            }
            if (cache != null) { cache.Bx0 = bx0; cache.By0 = by0; cache.Bx1 = bx1; cache.By1 = by1; }
        }

        var outp = new byte[w * h];
        for (int L = 1; L < n; L++)
        {
            if (bx1[L] < 0) continue;
            // The window has to be able to hang off the island by its own reach — twice, since the first
            // pass builds the values the second one then reads — or edge texels would be renormalised
            // against a window the crop had already truncated.
            int pad = 3 * radius;
            int ax0 = Math.Max(0, bx0[L] - pad), ax1 = Math.Min(w - 1, bx1[L] + pad);
            int ay0 = Math.Max(0, by0[L] - pad), ay1 = Math.Min(h - 1, by1[L] + pad);
            int cw = ax1 - ax0 + 1, ch = ay1 - ay0 + 1;

            // Pass 1 — the island alone, to learn what its edge values are. The numerator carries the plane
            // and must be rebuilt per mod; the denominator is the island mask, so it is cached.
            var num = new byte[cw * ch];
            byte[]? den = cached ? null : new byte[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                int src = (ay0 + y) * w + ax0, dst = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    if (labels[src + x] != L) continue;
                    num[dst + x] = plane[src + x];
                    if (den != null) den[dst + x] = 255;
                }
            }
            var bn = BlurCoverage(num, cw, ch, radius);
            var bd = cached ? cache!.Bd![L]! : BlurCoverage(den!, cw, ch, radius);
            if (!cached && cache != null) cache.Bd![L] = bd;

            // Pass 2 — the island plus the gutter it owns, that gutter carrying pass 1's continuation of the
            // island's own edge. This is what keeps a strap's AO intact where the strap crosses a UV seam.
            var plane2 = new byte[cw * ch];
            byte[]? mask2 = cached ? null : new byte[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                int src = (ay0 + y) * w + ax0, dst = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    int lp = labels[src + x];
                    if (lp == L)
                    {
                        plane2[dst + x] = plane[src + x];
                        if (mask2 != null) mask2[dst + x] = 255;
                    }
                    else if (lp == 0 && owner[src + x] == L)
                    {
                        // Best case: the mesh says which texel the surface actually continues into across
                        // the seam, so the gutter carries the REAL neighbouring coverage and the halo
                        // crosses correctly. Where there's no seam data (an open boundary, a body we
                        // couldn't read) fall back to continuing this island's own edge outward.
                        //
                        // The target must itself be ON an island. A seam edge near an island's corner can
                        // map outward into the NEIGHBOUR's padding, and reading that would feed the art's
                        // dilated ink straight back into the blur — the exact artefact this whole path
                        // exists to keep out. Measured: ~13% of mapped gutter texels land off-island.
                        int q = seamSource != null ? seamSource[src + x] : -1;
                        if (q >= 0 && labels[q] != 0)
                        {
                            plane2[dst + x] = plane[q];
                            if (mask2 != null) mask2[dst + x] = 255;
                            continue;
                        }
                        int d0 = bd[dst + x];
                        if (d0 < MinIslandWeight) continue;      // too far out to have a meaningful value
                        plane2[dst + x] = (byte)Math.Clamp(bn[dst + x] * 255 / d0, 0, 255);
                        if (mask2 != null) mask2[dst + x] = 255;
                    }
                }
            }
            var cn = BlurCoverage(plane2, cw, ch, radius);
            var cd = cached ? cache!.Cd![L]! : BlurCoverage(mask2!, cw, ch, radius);
            if (!cached && cache != null) cache.Cd![L] = cd;
            for (int y = 0; y < ch; y++)
            {
                int src = (ay0 + y) * w + ax0, dst = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    if (labels[src + x] != L) continue;
                    int d = cd[dst + x];
                    outp[src + x] = d >= MinIslandWeight
                        ? (byte)Math.Clamp(cn[dst + x] * 255 / d, 0, 255)
                        : plane[src + x];
                }
            }
        }

        // Every island's blurs are stored, so the entry is now safe for the next mod to read. Set here and
        // not earlier: anything that throws above must leave the cache unusable, not half-filled.
        cache?.MarkReady();

        // The padding still has to carry values that agree with the island edge: the sampler's bilinear tap
        // reaches about a texel past the border, so leaving it at 0 would draw a thin light fringe along
        // every island outline — the very thing this is here to avoid.
        return ExtendIntoPadding(outp, inside, w, h, radius, cache);
    }

    /// <summary>Blur radius the Skindenting depth default was tuned against (2048-wide skin map × the 0.003
    /// default softness). <see cref="ApplyNormalIndent"/> normalises its gradient against this.</summary>
    private const int IndentRefRadius = 6;

    /// <summary>Steepest tangent-space tilt <see cref="ApplyNormalIndent"/> will produce, as the length of
    /// (x, y). Below 1 by a real margin: at exactly 1 the implied z is 0 (a wall seen edge-on, which is what
    /// blows out to white), so 0.9 leaves z ≈ 0.44 — a deep groove that still shades like a surface.</summary>
    private const float MaxIndentTilt = 0.9f;

    /// <summary>
    /// Repair an index texture's RED channel — the colour-table row selector, read as <c>red / 17 + 1</c> —
    /// so it only ever names a row that actually HAS a preset.
    ///
    /// Row selection is discrete; the art is not. An exported _id is antialiased, so every edge texel ramps
    /// through the full range (255 → 238 → … → 0) and on the way names rows nobody configured. The skin layer
    /// gets away with it — an unconfigured row is simply skipped — but a gear shell hands this texture
    /// straight to the shader, which resolves it against the TEMPLATE's colorset and paints the template's
    /// colours as a one-texel fringe tracing every edge (the "white border" symptom).
    ///
    /// The repair is SPATIAL, not a nearest-row-number snap: an ambiguous texel takes the row of a nearby
    /// texel that is already valid, so an edge inherits the row of the region it belongs to. Snapping by row
    /// NUMBER instead would send the antialiased skirt of a row-16 region to row 15 (the numerically closest
    /// configured row) and just trade a white fringe for a row-15 one.
    ///
    /// Note what this deliberately leaves alone:
    /// <list type="bullet">
    /// <item>Texels already naming a configured row — including the whole blend band between two adjacent
    /// configured rows (with 15 and 16 set, every value from 239 to 254 already reads as row 15), so
    /// boundaries between real rows keep their current appearance.</item>
    /// <item>GREEN, the A/B sub-row weight. That one is a genuine continuous blend.</item>
    /// <item>Coverage/alpha. Edges stay antialiased — only the row they name is corrected.</item>
    /// </list>
    /// Texels the spread never reaches are left EXACTLY as they are. That is deliberate: assigning them the
    /// numerically nearest configured row instead would repaint the whole background (with rows 15 and 16
    /// set, every `red = 0` texel becomes row 15), and the shell's transparency gate lives in a SEPARATE
    /// texture — `norm`'s blue — which bleeds outward at edges through block compression and plain bilinear
    /// filtering. Under that bleed a repainted background paints its row just OUTSIDE the garment: the inner
    /// white fringe would simply become an outer coloured one. <paramref name="dilate"/> is sized to cover
    /// the bleed band instead, so anything the gate can actually reveal already carries the right row, and
    /// everything past it stays unmapped and is never drawn.
    /// </summary>
    /// <param name="index">RGBA index texture, modified in place.</param>
    /// <param name="definedRows">1-based rows that have presets. Empty ⇒ no-op (nothing to snap to).</param>
    /// <param name="dilate">How many texels a valid row may spread outward. It has to cover the antialiased
    /// band (1–2 texels) PLUS however far the separate transparency gate can bleed past the edge — a BC7
    /// block is 4 texels, and mip sampling widens that — so the budget is deliberately larger than the band
    /// alone. Past this the row is left unmapped; more only costs time.</param>
    internal static void SnapIndexRowsToDefined(byte[] index, int w, int h,
        IReadOnlyCollection<int> definedRows, int dilate = 8)
    {
        if (index.Length < w * h * 4 || definedRows.Count == 0 || w <= 0 || h <= 0) return;

        var isDefined = new bool[17];
        foreach (var r in definedRows)
            if (r >= 1 && r <= 16) isDefined[r] = true;

        int n = w * h;
        var valid = new bool[n];
        ParallelPixels(0, n, 1, (from, to) =>
        { for (int p = from; p < to; p++) valid[p] = isDefined[index[p * 4] / 17 + 1]; });

        // One scratch buffer reused by every pass rather than a fresh clone per pass: `valid` is w×h bytes
        // (4 MB on a 2048² map), and cloning it each pass churned ~16 MB per texture per composite — on a
        // path that reruns for every shell on every colour edit.
        var snapshotValid = new bool[n];

        // Spread valid rows outward one texel per pass. Each pass reads the PREVIOUS pass's validity
        // snapshot, so the result doesn't depend on which texel a worker reaches first.
        for (int it = 0; it < dilate; it++)
        {
            Array.Copy(valid, snapshotValid, n);
            int filled = 0;
            Parallel.For(0, h, () => 0, (y, _, local) =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int p = row + x;
                    if (snapshotValid[p]) continue;
                    // 4-neighbourhood; first valid neighbour wins. Diagonals add nothing here — the band is
                    // contiguous, so an extra pass reaches anything a diagonal would.
                    int src = -1;
                    if (x > 0     && snapshotValid[p - 1]) src = p - 1;
                    else if (x < w - 1 && snapshotValid[p + 1]) src = p + 1;
                    else if (y > 0     && snapshotValid[p - w]) src = p - w;
                    else if (y < h - 1 && snapshotValid[p + w]) src = p + w;
                    if (src < 0) continue;
                    index[p * 4]     = index[src * 4];       // row selector
                    index[p * 4 + 1] = index[src * 4 + 1];   // and its sub-row weight, so the pair stays coherent
                    valid[p] = true;
                    local++;
                }
                return local;
            }, local => Interlocked.Add(ref filled, local));
            if (filled == 0) break;   // nothing left adjacent to a valid row
        }
    }

    internal static Dictionary<int, ColorTableRowOverride> BuildRowDict(List<ColorTableRowPreset>? presets)
    {
        var dict = new Dictionary<int, ColorTableRowOverride>();
        if (presets == null) return dict;
        foreach (var p in presets)
        {
            var row = new ColorTableRowOverride();
            if (p.SubRowA is { } a)
            {
                if (a.Diffuse != null) (row.A.DiffuseR, row.A.DiffuseG, row.A.DiffuseB) = ParseHex(a.Diffuse);
                row.A.Emissive = a.Emissive;
                row.A.Opacity  = a.Opacity;
            }
            if (p.SubRowB is { } b)
            {
                if (b.Diffuse != null) (row.B.DiffuseR, row.B.DiffuseG, row.B.DiffuseB) = ParseHex(b.Diffuse);
                row.B.Emissive = b.Emissive;
                row.B.Opacity  = b.Opacity;
            }
            dict[p.Row - 1] = row; // 1-based JSON → 0-based internal
        }
        return dict;
    }

    internal static (float r, float g, float b) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        int v = Convert.ToInt32(hex, 16);
        return ((v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    internal static string? BodyCodeFromCustomize(byte race, byte tribe, byte sex)
    {
        bool f = sex == 1;
        if (race == 1) return (tribe == 2, f) switch // Hyur: tribe 2 = Highlander
        {
            (false, false) => "c0101",
            (false, true)  => "c0201",
            (true,  false) => "c0301",
            _              => "c0401",
        };
        return race switch
        {
            2 or 3 or 4 or 5 => f ? "c0201" : "c0101", // Elezen/Lalafell/Miqo'te/Roegadyn share mid bodies
            6 => f ? "c1401" : "c1301", // Au Ra
            7 => f ? "c1601" : "c1501", // Hrothgar
            8 => f ? "c1801" : "c1701", // Viera
            _ => null,
        };
    }

    // Apply per-pixel opacity from the index texture, blending sub-row A/B values just
    // like diffuse color and emissive. Returns a new array; src and pngCache are not mutated.
    internal static byte[] ApplyIndexedOpacity(byte[] src, byte[] idx, Dictionary<int, ColorTableRowOverride> rows)
    {
        var dst = (byte[])src.Clone();
        // rows is only read here, never mutated, so concurrent TryGetValue is safe.
        ParallelPixels(0, dst.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = dst[i + 3] / 255f;
                if (a <= 0f) continue;
                int pairIdx = idx[i] / 17;
                if (!rows.TryGetValue(pairIdx, out var pair)) continue;
                float blendA = idx[i + 1] / 255f;
                float op = pair.B.Opacity + (pair.A.Opacity - pair.B.Opacity) * blendA;
                if (op == 0f) continue;
                float newA = op < 0f
                    ? a * (100f + op) / 100f
                    : a + (1f - a) * op / 100f;
                dst[i + 3] = (byte)(newA * 255f + 0.5f);
            }
        });
        return dst;
    }

    internal static byte[] ScaleOverlayAlpha(byte[] src, int opacity)
    {
        var dst = (byte[])src.Clone();
        ParallelPixels(3, dst.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                int a = dst[i];
                if (opacity < 0)
                    dst[i] = (byte)(a * (100 + opacity) / 100);
                else if (a > 0)
                    dst[i] = (byte)Math.Min(255, a + (255 - a) * opacity / 100);
            }
        });
        return dst;
    }


    // Apply a per-mod "Masks" map to a coverage RGBA buffer. A mask SETS the overlay's opacity
    // explicitly within its alpha region: cov' = cov*W + T, where W (how much the overlay's own
    // coverage survives, = Π(1-aᵢ)) and T (the mask's target opacity contribution, = Σ gray·a) come
    // from CombinedMaskAt. This is additive WHERE THE OVERLAY IS ALREADY VISIBLE — a white-RGB/white-
    // alpha patch (W=0, T=255) forces full opacity over a sheer area, so masks can ADD coverage, not
    // only remove it. But the additive term is gated by the base coverage: where the overlay's own
    // alpha is 0 (outside the garment — e.g. above where a stocking ends, or the holes of a fishnet)
    // the pixel stays fully transparent, so a mask can never paint opacity onto bare skin.
    // Returns the input unchanged when the map is null; otherwise returns a clone, since the
    // coverage may be a shared, cached PNG array that must not be mutated.
    // `additive` withholds the T term. A mask is a garment element in its own right (it carries its
    // own relief normal and color row), and it must overwrite whatever else the mod draws underneath —
    // so only the mod's HIGHEST-priority group is granted the forced opacity. Lower groups see W alone,
    // which is 0 wherever the mask is opaque, erasing them from the mask's territory instead of letting
    // them paint over it.
    internal static byte[] ApplyCoverageMask(byte[] coverageRgba, byte[]? w, byte[]? t, bool additive = true)
    {
        if (w == null || t == null) return coverageRgba;
        var dst = (byte[])coverageRgba.Clone();
        int n = Math.Min(w.Length, dst.Length / 4);
        ParallelPixels(0, n, 1, (from, to) =>
        {
            for (int pi = from; pi < to; pi++)
            {
                int baseA = dst[pi * 4 + 3];
                if (baseA == 0) continue;                    // no base coverage → mask has no say (stays 0)
                int v = baseA * w[pi] / 255 + (additive ? t[pi] : 0);
                dst[pi * 4 + 3] = (byte)(v > 255 ? 255 : v);
            }
        });
        return dst;
    }

    internal static string SanitizeName(string gamePath)
    {
        var name = Path.GetFileNameWithoutExtension(gamePath);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }
}
