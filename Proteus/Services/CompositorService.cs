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
    // Which gear model the second skin sources each slot's shell from (part -> .mdl game path), captured
    // on the framework thread from the draw object's loaded models so the background build can read it
    // without an IPC. Refreshed wherever the material snapshot is.
    private volatile IReadOnlyDictionary<string, string>? _equippedPartModels;
    // Which ring/bracelet the second skin appends its shell into (slot rir|ril|wrs -> .mdl game path),
    // captured the same way as _equippedPartModels. Empty when no accessory is worn, in which case the
    // shell falls back to replacing the invisible Emperor's New Ring.
    private volatile IReadOnlyDictionary<string, string>? _equippedAccessoryModels;
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
        this.secondSkin = new SecondSkinService(penumbra, textureLoader, discovery, uvRemap, log);

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
    }

    public void Dispose()
    {
        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.ModAdded          -= OnModAdded;
        penumbra.ModDeleted        -= OnModDeleted;
        penumbra.PenumbraReady     -= OnPenumbraReady;
        penumbra.PlayerCollectionChanged -= OnPlayerCollectionChanged;
        penumbra.LocalPlayerRedrawn              -= OnLocalPlayerRedrawn;
        glamourer.LocalPlayerStateChanged        -= OnGlamourerStateChanged;
        glamourer.LocalPlayerCustomizationChanged -= OnGlamourerCustomizationChanged;

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
            _equippedPartModels = equipped;
            _equippedAccessoryModels = accessories;
            var sig = EquipSignature(equipped, accessories);
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

    // Sums size + mtime over a mod's own default_mod.json/group_*.json (same top-level enumeration
    // SidecarDiscoveryService.ReadMaskGroupOptionOrder uses for group_*.json) — cheap invalidation
    // key so a mod update is detected without a plugin restart.
    private static long ComputeModFingerprint(string modRoot)
    {
        long fp = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (!string.Equals(name, "default_mod.json", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                    continue;
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
                var name = Path.GetFileName(file);
                if (!string.Equals(name, "default_mod.json", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                    continue;
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

        Task.Run(() =>
        {
            try
            {
                WriteManagedModJson(new Dictionary<string, string>());
                penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                ReloadAndRedraw();   // character reverts to un-composited

                if (collId.HasValue)
                    penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, false);

                log.Debug("[Proteus] disabled: output cleared, redrawn, Penumbra mod off");
            }
            catch (Exception ex) { log.Error(ex, "[Proteus] failed to disable cleanly"); }
        });
    }

    // ── Trigger ──────────────────────────────────────────────────────────────

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
                }
            }
            catch (OperationCanceledException) { return; }

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
        @"chara/accessory/(a\d+)/model/c\d+a\d+_(rir|ril|wrs)\.mdl",
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

    // Stable string of the equipped part + accessory models, for cheap change detection on redraw. A ring
    // swap must rebuild the shell (it changes the host), so the accessory map is folded in too.
    private static string EquipSignature(
        IReadOnlyDictionary<string, string>? models, IReadOnlyDictionary<string, string>? accessories = null)
        => string.Join("|",
            (models ?? new Dictionary<string, string>()).Concat(accessories ?? new Dictionary<string, string>())
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

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

                foreach (var overlay in overlays)
                {
                    allOverlays.Add((entry, overlay));
                    if (overlay.Descriptor.Layer == OverlayLayer.Gear)
                    {
                        gearOverlays.Add((entry, overlay));
                        continue;
                    }

                    foreach (var mtrlPath in overlay.Descriptor.MaterialGamePaths)
                    {
                        if (string.IsNullOrEmpty(mtrlPath)) continue;
                        if (!byMaterial.TryGetValue(mtrlPath, out var list))
                            byMaterial[mtrlPath] = list = new();
                        list.Add((entry, overlay));
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

            // Within a mod, a HIGHER-priority group (lower group_NNN number) must composite LAST so it
            // lands on top. Across mods the existing Penumbra-priority order is preserved.
            foreach (var list in byMaterial.Values)
            {
                var sorted = list
                    .OrderBy(p => p.Entry.Priority)
                    .ThenByDescending(p => p.Overlay.GroupOrder)
                    .ToList();
                list.Clear();
                list.AddRange(sorted);
            }

            // The mod's highest-priority group. Only it is granted a mask's forced-opacity term; see
            // ApplyCoverageMask. Mods with no groups (top-level Overlays) all sit at int.MaxValue, so
            // every overlay qualifies and the old behaviour stands.
            var topGroupByMod = byMaterial.Values.SelectMany(l => l)
                .GroupBy(p => p.Entry.ModDirectory, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Min(p => p.Overlay.GroupOrder), StringComparer.OrdinalIgnoreCase);

            bool MaskAdds(OverlayEntry e, ResolvedOverlay o)
                => !topGroupByMod.TryGetValue(e.ModDirectory, out var top) || o.GroupOrder <= top;

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
                            if (wArr == null)
                            {
                                wArr = new byte[n];
                                tArr = new byte[n];
                                for (int pi = 0; pi < n; pi++)
                                {
                                    int o = pi * 4;
                                    int a = m[o + 3];
                                    int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8; // luminance
                                    wArr[pi] = (byte)(255 - a);       // (1-a)
                                    tArr[pi] = (byte)(g * a / 255);   // gray*a
                                }
                            }
                            else
                            {
                                for (int pi = 0; pi < n; pi++)
                                {
                                    int o = pi * 4;
                                    int a = m[o + 3];
                                    int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;
                                    int inv = 255 - a;
                                    // T' = T*(1-a) + gray*a ;  W' = W*(1-a)
                                    tArr![pi] = (byte)(tArr[pi] * inv / 255 + g * a / 255);
                                    wArr[pi]  = (byte)(wArr[pi] * inv / 255);
                                }
                            }
                        }
                        return wArr == null ? ((byte[] W, byte[] T)?)null : (wArr, tArr!);
                    });
                }

                if (ct.IsCancellationRequested) return;

                var mtrlDisk = penumbra.ResolvePlayer(mtrlGamePath);
                var texPaths = (mtrlDisk != null && File.Exists(mtrlDisk))
                    ? textureLoader.ResolveMtrlTextures(mtrlDisk)
                    : textureLoader.ResolveMtrlTexturesFromGame(mtrlGamePath);

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

                foreach (var (entry, resolved) in pairs)
                {
                    if (ct.IsCancellationRequested) return;

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
                            for (int si = 0; si < normalOv.Length; si += 4)
                            {
                                synth[si] = synth[si + 1] = synth[si + 2] = 255;
                                synth[si + 3] = normalOv[si + 2]; // blue → opacity
                            }
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
                            for (int di = 0; di < dropMask.Length; di++)
                            {
                                if (!dropMask[di]) continue;
                                int o = di * 4;
                                covSrc![o] = covSrc[o + 1] = covSrc[o + 2] = covSrc[o + 3] = 0;
                                if (diffuseOv != null && diffuseOv.Length == covSrc.Length)
                                    diffuseOv[o] = diffuseOv[o + 1] = diffuseOv[o + 2] = diffuseOv[o + 3] = 0;
                            }
                    }

                    // ── Phase A: diffuse composite ────────────────────────────
                    if (desc.Diffuse != null && diffuseOv != null && baseD is { Length: > 0 })
                    {
                        if (desc.Index != null)
                        {
                            var idxPath = Path.Combine(entry.SidecarRoot, desc.Index);
                            var idD = LoadIndexMerged(idxPath, wD, hD, srcBodyType, entry.ModDirectory);
                            if (idD != null) ApplyIndexedOverlay(baseD, diffuseOv, idD, rows, false, wD, hD);
                            else             ApplyFlatOverlay(baseD, diffuseOv, row16A, wD, hD);
                        }
                        else ApplyFlatOverlay(baseD, diffuseOv, row16A, wD, hD);
                    }
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

                        foreach (var (maskPath, normalPath, _) in assets)
                        {
                            if (normalPath == null) continue;
                            var maskPng  = RemapIfNeeded(LoadPng(maskPath, wN, hN), wN, hN, maskSrcBodyType, maskPath);
                            var normalOv = RemapIfNeeded(LoadPng(normalPath, wN, hN), wN, hN, maskSrcBodyType, normalPath);
                            if (maskPng != null && normalOv != null)
                                ReplaceNormal(baseN, normalOv, wN, hN, maskPng);   // the mask IS the surface
                        }

                        var msk = CombinedMaskAt(modDir, wN, hN, maskSrcBodyType);
                        if (msk != null)
                        {
                            var full = new byte[wN * hN * 4];
                            for (int fi = 3; fi < full.Length; fi += 4) full[fi] = 255;
                            var weight = ApplyCoverageMask(full, msk.Value.W, msk.Value.T);
                            for (int i = 0; i < baseN.Length; i += 4)
                            {
                                float t = weight[i + 3] / 255f;
                                baseN[i]     = (byte)(preRelief[i]     * (1f - t) + baseN[i]     * t);
                                baseN[i + 1] = (byte)(preRelief[i + 1] * (1f - t) + baseN[i + 1] * t);
                            }
                        }
                    }
                }

                var baseName = SanitizeName(mtrlGamePath) + "_" + runId;
                var channels = new System.Text.StringBuilder();

                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_d.tex");
                    var relPath = "textures/" + baseName + "_d.tex";
                    if (textureLoader.WriteTex(baseD, wD, hD, outPath))
                    { redirects[texPaths.Diffuse] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" diffuse"); }
                }
                if (baseN is { Length: > 0 } && texPaths.Normal != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_n.tex");
                    var relPath = "textures/" + baseName + "_n.tex";
                    if (textureLoader.WriteTex(baseN, wN, hN, outPath))
                    { redirects[texPaths.Normal] = relPath; Interlocked.Increment(ref texturesPatched); channels.Append(" normal"); }
                }
                if (baseM is { Length: > 0 } && texPaths.Mask != null)
                {
                    var outPath = Path.Combine(texturesDir, baseName + "_m.tex");
                    var relPath = "textures/" + baseName + "_m.tex";
                    if (textureLoader.WriteTex(baseM, wM, hM, outPath))
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

            // ── Second skin: one gear shell per Layer:Gear overlay ────────────
            // Built from the body model the character is CURRENTLY drawing (resolved live through
            // Penumbra) — a shell cut from any other body shape shows the body through it.
            List<object>? manipulations = null;
            _needFullRedraw = false;
            if (gearOverlays.Count > 0)
            {
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
                        log.Information("[Proteus] second skin: equipped part models [{0}], accessories [{1}] ({2})",
                            string.Join(", ", equippedModels.Select(kv => $"{kv.Key}={kv.Value}")),
                            string.Join(", ", equippedAccessories.Select(kv => $"{kv.Key}={kv.Value}")),
                            _equippedPartModels == null ? "cache null" : "cached");

                        // gen2 (vanilla) shells are opt-in per mod, same as the skin-layer gen2 sibling.
                        var shells = secondSkin.Build(charCode, gearOverlays, managedModDir, bodyType,
                            discovery.EffectsLibraryPath(), allOverlays, equippedModels, equippedAccessories,
                            modDir => config.SiblingModeFor(modDir) == SiblingSynthesisMode.AllBodies);
                        if (shells != null)
                        {
                            foreach (var (gamePath, relPath) in shells.Redirects)
                                redirects[gamePath] = relPath;
                            manipulations = shells.Manipulations;

                            // Only when the shell's .mdl/.mtrl actually changed — an in-place reload
                            // refreshes textures but cannot see a new model or material, so those runs
                            // need a real redraw. Colour-only or texture-only runs don't.
                            _needFullRedraw = shells.ShellChanged;
                            if (_needFullRedraw)
                                log.Debug("[Proteus] second skin changed — forcing a full redraw");
                        }
                    }
                    catch (Exception ex) { log.Error(ex, "[Proteus] second skin build failed"); }
            }

            WriteManagedModJson(redirects, manipulations);
            ReloadAndRedrawWhenReady(redirects, runId);

            LastResult = new CompositorResult
            {
                Success = true,
                TexturesPatched = texturesPatched,
                OverlayModsUsed = entries.Count,
            };
            ResultChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] Recomposite failed");
            LastResult = new CompositorResult { Success = false, ErrorMessage = ex.Message };
            ResultChanged?.Invoke();
        }
    }

    // ── Managed mod helpers ──────────────────────────────────────────────────

    private void EnsureManagedModExists()
    {
        if (Directory.Exists(managedModDir)) return;

        Directory.CreateDirectory(managedModDir);
        Directory.CreateDirectory(Path.Combine(managedModDir, "textures"));

        File.WriteAllText(
            Path.Combine(managedModDir, "meta.json"),
            """{"FileVersion":3,"Name":"Proteus","Author":"Proteus","Description":"Managed by the Proteus overlay compositor plugin.","Version":"","Website":"","ModTags":[]}""");

        WriteManagedModJson(new Dictionary<string, string>());

        var ec = penumbra.AddModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Information("[Proteus] AddMod({0}) -> {1}", managedModDir, ec);

        var collId = penumbra.GetPlayerCollectionId();
        if (collId.HasValue)
        {
            penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
            penumbra.SetModPriority(collId.Value, SidecarDiscoveryService.ManagedModDir, config.ManagedModPriority);
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
    /// Write the managed mod's default_mod.json.
    /// <paramref name="manipulations"/> carries metadata edits — a second-skin shell needs an EQDP entry
    /// so the accessory it rides on loads the character's own race/gender model rather than the default.
    /// </summary>
    private void WriteManagedModJson(IDictionary<string, string> redirects, IReadOnlyList<object>? manipulations = null)
    {
        // Penumbra default_mod.json: { "Files": { "gamePath": "relPath", ... }, "Swaps": {}, "Manipulations": [] }
        var files = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, relPath) in redirects)
            files[gamePath] = relPath;

        var obj = new
        {
            Files = files,
            Swaps = new { },
            Manipulations = (IReadOnlyList<object>)(manipulations ?? Array.Empty<object>()),
        };
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        var target = Path.Combine(managedModDir, "default_mod.json");
        var tmp    = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, json);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, target, overwrite: true); break; }
            catch (Exception) when (i < 5) { Thread.Sleep(50 << i); } // 50 100 200 400 800ms
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
                return;
            }
        }

        Interlocked.Exchange(ref _lastOwnRedrawTick, Environment.TickCount64);
        penumbra.RedrawPlayer();
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

                if (bodyTypeChanged || charCodeChanged)
                {
                    log.Debug("[Proteus] Post-settle correction: bodyType={0}→{1} charCode={2}→{3}",
                        _lastCompositedBodyType ?? "none", newBodyTypeKey ?? "none",
                        _lastCompositedCharCodes ?? "none", newCharCodeKey ?? "none");
                    // Publish the settled snapshot so the corrective recomposite uses it directly
                    // (dirty stays false → TriggerRecomposite won't re-fetch a possibly-still-settling one).
                    _activeMtrlSnapshot = snapshot;
                    _activeMtrlSnapshotDirty = false;
                    config.CachedActiveMaterialPaths = snapshot.ToList();
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
    internal static void ApplyFlatOverlay(byte[] baseTex, byte[] ov, ColorTableSubRow row, int w, int h)
    {
        float cr = row.DiffuseR, cg = row.DiffuseG, cb = row.DiffuseB;
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            float a = ov[i + 3] / 255f;
            if (a <= 0f) continue;
            float ia = 1f - a;
            baseTex[i]     = (byte)(ov[i]     / 255f * cr * a * 255f + baseTex[i]     * ia);
            baseTex[i + 1] = (byte)(ov[i + 1] / 255f * cg * a * 255f + baseTex[i + 1] * ia);
            baseTex[i + 2] = (byte)(ov[i + 2] / 255f * cb * a * 255f + baseTex[i + 2] * ia);
        }
    }

    // Write emissive intensity to the normal map alpha where the overlay is opaque.
    internal static void ApplyFlatEmissive(byte[] baseN, byte[] ov, ColorTableSubRow row, int w, int h)
    {
        if (row.Emissive <= 0.001f) return;
        byte intensity = (byte)(row.Emissive * 255f);
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
            if (ov[i + 3] > 0)
                baseN[i + 3] = Math.Max(baseN[i + 3], intensity);
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
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            if (cov[i + 3] == 0) continue; // outside this overlay's coverage
            int pairIdx = idx[i] / 17;
            if (!rows.TryGetValue(pairIdx, out var pair)) continue;
            float blendA = idx[i + 1] / 255f;
            float em = pair.B.Emissive + (pair.A.Emissive - pair.B.Emissive) * blendA;
            if (em > 0.001f)
                baseN[i + 3] = Math.Max(baseN[i + 3], (byte)(em * 255f));
        }
    }

    // Per-pixel color and emissive driven by index texture.
    // isNormal = false: tint+composite diffuse; isNormal = true: write emissive to normal alpha.
    internal static void ApplyIndexedOverlay(
        byte[] baseTex, byte[] ov, byte[] idx,
        Dictionary<int, ColorTableRowOverride> rows,
        bool isNormal, int w, int h)
    {
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
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
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            float a = src[i + 3] / 255f;
            if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
            if (a <= 0f) continue;

            dst[i]     = (byte)Math.Clamp(dst[i]     + (src[i]     - dst[i])     * a, 0, 255);
            dst[i + 1] = (byte)Math.Clamp(dst[i + 1] + (src[i + 1] - dst[i + 1]) * a, 0, 255);
        }
    }

    internal static void CompoundNormal(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
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
    }

    // Standard alpha-over: dst = src * src.a + dst * (1 - src.a). Dst alpha unchanged.
    // mask: if provided, effective alpha = min(src alpha, mask alpha) — used so a diffuse overlay
    // silhouette gates the normal composite (invisible diffuse pixels stay at base normal).
    internal static void AlphaComposite(byte[] dst, byte[] src, int w, int h, byte[]? mask = null)
    {
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
        {
            float a = src[i + 3] / 255f;
            if (mask != null) a = Math.Min(a, mask[i + 3] / 255f);
            if (a <= 0f) continue;
            float ia = 1f - a;
            dst[i]     = (byte)(src[i]     * a + dst[i]     * ia);
            dst[i + 1] = (byte)(src[i + 1] * a + dst[i + 1] * ia);
            dst[i + 2] = (byte)(src[i + 2] * a + dst[i + 2] * ia);
        }
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
        int len = w * h * 4;
        for (int i = 0; i < len; i += 4)
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
        for (int i = 0; i < dst.Length; i += 4)
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
        return dst;
    }

    internal static byte[] ScaleOverlayAlpha(byte[] src, int opacity)
    {
        var dst = (byte[])src.Clone();
        for (int i = 3; i < dst.Length; i += 4)
        {
            int a = dst[i];
            if (opacity < 0)
                dst[i] = (byte)(a * (100 + opacity) / 100);
            else if (a > 0)
                dst[i] = (byte)Math.Min(255, a + (255 - a) * opacity / 100);
        }
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
        for (int pi = 0; pi < n; pi++)
        {
            int baseA = dst[pi * 4 + 3];
            if (baseA == 0) continue;                        // no base coverage → mask has no say (stays 0)
            int v = baseA * w[pi] / 255 + (additive ? t[pi] : 0);
            dst[pi * 4 + 3] = (byte)(v > 255 ? 255 : v);
        }
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
