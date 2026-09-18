using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

using static Proteus.Services.InertModDiagnosis;

using static Proteus.Services.DrawnModelPaths;

using static Proteus.Services.IslandBlur;

using static Proteus.Services.OverlayBlend;

public partial class CompositorService : IDisposable
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
    /// How many overlays ahead of the blend to decode in the background. Deeper is not better: prefetch only pays
    /// when it completes before the blend reaches it. Must stay matched to TextureLoader's cache budget.
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

    // Debounce slot for the recomposite trigger: cancels the pending run and issues the new one's token.
    private readonly DebounceGate recompositeGate = new();
    private readonly SecondSkinService secondSkin;
    private readonly UvSeamMapService seamMaps;
    private readonly FaceUvDoublingService faceUv;

    private long _lastOwnRedrawTick = 0; // TickCount64 when we last called RedrawPlayer()
    private long _lastOwnReapplyTick = 0; // TickCount64 when we last called Glamourer ReapplyState()

    // Armed when we ask the game to rebuild the player's draw object, cleared by the first Gearset
    // finalization that follows. 0 = nothing outstanding. See ConsumeOwnRedrawEcho.
    private long _pendingRedrawEchoTick = 0;

    /// <summary>
    /// Whether the Gearset finalization now arriving is the echo of a redraw Proteus asked for; consumes that
    /// expectation either way, so it accounts for at most one Gearset. <paramref name="withinMs"/> bounds how
    /// long to wait for an echo that may never arrive. Not armed by the in-place reload, which produces no Gearset.
    /// </summary>
    public bool ConsumeOwnRedrawEcho(int withinMs)
    {
        var stamp = Interlocked.Exchange(ref _pendingRedrawEchoTick, 0);   // consume, expired or not
        if (stamp == 0) return false;
        var since = unchecked(Environment.TickCount64 - stamp);
        return since >= 0 && since < withinMs;
    }

    /// <summary>Record that we caused the redraw the game is about to perform (Glamourer echo suppression and
    /// the one-shot Gearset expectation).</summary>
    private void StampOwnRedraw()
    {
        var now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastOwnRedrawTick, now);
        Interlocked.Exchange(ref _pendingRedrawEchoTick, now);
    }

    /// <summary>
    /// Withdraw the expectation <see cref="StampOwnRedraw"/> armed, for a redraw that never reached the game;
    /// otherwise it would swallow the player's next real gearset change.
    /// </summary>
    private void CancelOwnRedrawEcho()
        => Interlocked.Exchange(ref _pendingRedrawEchoTick, 0);

    // Non-persistent per-mod color override pushed by the design-binding system. When set, the
    // compositor uses these colors in place of each mod's metadata.json colors for the run; null
    // means "use metadata as authored". Reference assignment is atomic; read on the recomposite task.
    private volatile IReadOnlyDictionary<string, OverlayColorOverride>? _colorOverride;
    // Snapshot of the player's active material game paths, captured on the main thread at trigger
    // time so the background recomposite can filter without touching main-thread-only IPCs.
    private volatile HashSet<string>? _activeMtrlSnapshot;
    // Set when a body mod or the player collection changes — anything that could make _activeMtrlSnapshot
    // wrong without a redraw. The resource-tree walk is only paid for when this is set or the snapshot is cold.
    private volatile bool _activeMtrlSnapshotDirty;
    // Every game path the last composite read as a base (PrimeUpstreamCache's return value): a mod that
    // redirects none of these cannot change our output. Not _upstreamByGamePath, which is emptied on
    // every settings change. Set and hash share one immutable record so they can never disagree.
    // Hash is content-derived because verdicts keyed on it persist across sessions. Signature hashes the
    // material paths the composite targets; paths are dropped when it changes. See RecordCompositeBaseKeys.
    private sealed record BaseKeySet(HashSet<string> Paths, int Hash, int Signature);
    private volatile BaseKeySet? _compositeBaseKeys;

    /// <summary>
    /// Which file each base path resolved to at the end of the last composite; a copy of
    /// <see cref="_upstreamByGamePath"/> that <see cref="InvalidateUpstreamCache"/> does not clear, so
    /// <see cref="ModSettingMovedABase"/> can ask whether a settings change moved anything we read.
    /// </summary>
    private volatile IReadOnlyDictionary<string, string>? _lastBaseUpstreams;

    /// <summary>
    /// Mods with a deferred <see cref="ModSettingMovedABase"/> re-check in flight, so a user dragging a
    /// slider or clicking through a group does not queue one per event.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _pendingMoveRecheck = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long after a settings change the deferred re-check re-asks whether a base moved. Penumbra recomputes
    /// the collection cache asynchronously, so the immediate answer can still be the pre-change winner.
    /// </summary>
    private const int MovedBaseRecheckMs = 1200;
    // Signature of the equipped gear models the second skin cuts its shells from (feet/legs/hands/body).
    // Equipping fires no mod or design event, so this redraw-time diff is the only signal. Null until the first redraw.
    private string? _lastEquipSignature;
    // Per gear-overlay shell material file names (ss_{letter}.mtrl), keyed by (mod, group, option), from
    // the last shell build. Built on the composite thread, read on the UI thread; immutable once assigned.
    private volatile Dictionary<(string ModDir, string? Group, string? Option), List<string>> _shellMaterials = new();

    /// <summary>The shell material file names (ss_{letter}.mtrl) for a gear overlay, or null if none were built.</summary>
    public IReadOnlyList<string>? GetShellMaterials(string modDir, string? group, string? option)
        => _shellMaterials.TryGetValue((modDir, group, option), out var leaves) ? leaves : null;

    // Shell material leaf → what its rows want from the scene light, from the last shell build. Same
    // publish contract as _shellMaterials, so the framework thread reads it every frame with no lock.
    private volatile Dictionary<string, ShellLightProfile> _shellLight = new();

    /// <summary>
    /// The light response of a shell material, or null when it asks for none (the common, fast path).
    /// </summary>
    public ShellLightProfile? GetShellLight(string materialLeaf)
        => _shellLight.TryGetValue(materialLeaf, out var p) ? p : null;

    /// <summary>Whether any live shell asks for a light response; lets the per-frame applier skip its pass.</summary>
    public bool AnyShellLight => _shellLight.Count > 0;

    // Mod directory → the content materials of that mod backing a drawn mesh in the last composite, as
    // paths relative to the mod root. Same publish contract as _shellMaterials: assembled on the composite
    // thread, swapped in as one reference, never mutated afterwards.
    private volatile Dictionary<string, HashSet<string>> _contentMaterials = new();

    /// <summary>
    /// The content materials of <paramref name="modDir"/> that the last composite found backing a drawn
    /// mesh, or null when none; callers must treat null as "no information". Includes unhosted materials.
    /// </summary>
    public IReadOnlySet<string>? GetLiveContentMaterials(string modDir)
        => _contentMaterials.TryGetValue(modDir, out var mats) ? mats : null;

    // Mod directory → the content MODEL files of that mod that went into the last composite's shell, relative
    // to the mod root with forward slashes. Published on the same terms as _contentMaterials beside it.
    private volatile Dictionary<string, HashSet<string>> _contentModels = new();

    /// <summary>
    /// The imported models of <paramref name="modDir"/> the character wears through our shell, or null when none
    /// (or not yet composited). Our grafted geometry never appears in the character's loaded-file walk.
    /// </summary>
    public IReadOnlySet<string>? GetLiveContentModels(string modDir)
        => _contentModels.TryGetValue(modDir, out var models) ? models : null;

    /// <summary>
    /// Why none of <paramref name="modDir"/>'s content pieces can be worn by this character, or null when
    /// they can. Recorded by the shell builder even on runs that host nothing.
    /// </summary>
    public string? GetUnwearableContentReason(string modDir)
        => secondSkin.UnwearableContent.TryGetValue(modDir, out var why) ? why : null;

    /// <summary>
    /// Why <paramref name="modDir"/> is enabled yet contributed nothing to the last composite, or null when it
    /// contributed something. The tier above <see cref="GetUnwearableContentReason"/>.
    /// </summary>
    public InertReason? GetInertReason(string modDir)
        => _inertMods.TryGetValue(modDir, out var r) ? r : null;

    /// <summary>
    /// What the last shell build published, for the drawn check after the redraw (<see cref="SchedulePostRedrawShellCheck"/>).
    /// <paramref name="Materials"/> is the test (only drawn meshes load them); <paramref name="Models"/> is the
    /// anchor that separates "our mesh didn't load" from "the redraw hasn't finished".
    /// </summary>
    private sealed record ShellDrawnProbe(IReadOnlyList<string> Materials, IReadOnlyList<string> Models);

    /// <summary>
    /// The last shell build's probe, or null when no shell was built. Same publish contract as
    /// <see cref="_shellMaterials"/>: assembled on the composite thread, swapped in as one reference.
    /// </summary>
    private volatile ShellDrawnProbe? _shellDrawnCheck;

    /// <summary>
    /// The material set the drawn check last reported on the character, joined; paths are logged only when it
    /// changes. Null after a failure or rebuild. Touched only from the drawn check's own task.
    /// </summary>
    private string? _lastDrawnMaterials;

    /// <summary>
    /// <see cref="ShellProbeKey"/> of the last shell the drawn check positively confirmed on the character, or null.
    /// Bounds the gear phase's "forced composite redraws anyway" net to shells whose state is unknown. A content
    /// key, not a flag, so it cannot race the async check. Only conclusive success sets it; only conclusive failure clears it.
    /// </summary>
    private volatile string? _shellConfirmedDrawnKey;

    /// <summary>The shell the "redraw to see it" chat notice last went out for.</summary>
    private volatile string? _redrawWithheldNoticeKey;

    /// <summary>Content identity of a shell probe — the published material and model paths. Two probes
    /// with the same key describe the same shell on the character, however many times it was rebuilt.</summary>
    private static string ShellProbeKey(ShellDrawnProbe p)
        => string.Join('\n', p.Materials) + '\u0000' + string.Join('\n', p.Models);

    /// <summary>
    /// Drop all three shell locators together, for tear-down paths that never reach the gear phase (plugin
    /// disabled, "no enabled mods" early return). The composite does not call this on entry.
    /// </summary>
    private void ClearShellLocators()
    {
        _shellMaterials   = new();
        _contentMaterials = new(StringComparer.OrdinalIgnoreCase);
        _contentModels    = new(StringComparer.OrdinalIgnoreCase);
        _shellLight       = new(StringComparer.OrdinalIgnoreCase);
        _shellDrawnCheck  = null;
        // The shell is gone, so the next one to be drawn is news even if it lands on the same materials.
        _lastDrawnMaterials = null;
    }

    // Per skin-overlay glow recipe (composited-diffuse pixels per colour-table row), keyed by (mod, group,
    // option), from the last composite. Same publish contract as _shellMaterials.
    private volatile Dictionary<(string ModDir, string? Group, string? Option), List<Proteus.Interop.SkinGlowTarget>> _skinGlowTargets = new();

    /// <summary>Glow recipes for a skin overlay (one per composited body material), or null if none.</summary>
    public IReadOnlyList<Proteus.Interop.SkinGlowTarget>? GetSkinGlowTargets(string modDir, string? group, string? option)
        => _skinGlowTargets.TryGetValue((modDir, group, option), out var t) ? t : null;
    // Which gear model the second skin sources each slot's shell from (part -> .mdl game path), captured
    // on the framework thread from the draw object's loaded models so the background build can read it
    // without an IPC. Refreshed wherever the material snapshot is.
    private volatile IReadOnlyDictionary<string, string>? _equippedPartModels;
    // The bare-body e0000 models drawn in the slots gear does NOT cover (part -> .mdl game path), captured
    // in the same walk. Names the race the game resolved each bare slot to, which is not always the
    // character's own. See BareBodyModelsFromModels.
    private volatile IReadOnlyDictionary<string, string>? _bareBodyModels;
    // The character's real race code ("0801"), from the same walk. Distinct from charCode, which is the
    // shared BODY code. See DrawnRaceCodeFromModels.
    private volatile string? _drawnRaceCode;
    // Who _drawnRaceCode was read off. The cache is sticky, so without an owner the previous character's race
    // would survive a character switch and feed a wrong hostRace into SecondSkinService.Build.
    private volatile string? _drawnRaceOwner;
    // Serializes the _drawnRaceCode/_drawnRaceOwner pair, written from the framework thread and the composite.
    // Readers read _drawnRaceCode unlocked: they only ever see one whole value.
    private readonly object _drawnRaceLock = new();
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
    // The character's own face/hair/tail/ear models, sorted (see HumanPartModelsFromModels). Captured by the
    // same walk as the maps above. Currently observed only — logged, and folded into the equip signature so
    // a face or hairstyle change triggers a recomposite the way an equipment change does.
    private volatile IReadOnlyList<string>? _humanPartModels;
    // modDir -> (ships an obj/body/ material, fingerprint it was computed at). Fingerprint = summed size+mtime of
    // the mod's manifests. AffectsComposite (provides a path this composite reads) gates a recomposite; IsSurfaceMod
    // drives cache invalidation. BaseKeysHash records the base set AffectsComposite was computed against.
    private readonly ConcurrentDictionary<string,
        (bool IsSurfaceMod, bool AffectsComposite, int BaseKeysHash, long Fingerprint)> _bodyModCache =
        new(StringComparer.OrdinalIgnoreCase);
    // Serializes the config.KnownBodyMods mutations + config.Save() done off-thread by ClassifySurfaceMod
    // and OnModDeleted, so a save never serializes the dictionary while another thread mutates it.
    private readonly object _bodyModConfigLock = new();

    // Mods already reacted to while disabled; lets OnModSettingChanged skip further changes on them.
    // Absence is the safe default ("don't skip"). Only OnModSettingChanged adds, on the framework thread.
    private readonly ConcurrentDictionary<string, byte> _knownDisabled =
        new(StringComparer.OrdinalIgnoreCase);

    // Depth of SuppressModSettingEvents scopes. Framework thread only, like the handler it gates.
    private int _modSettingEventSuppression;

    /// <summary>
    /// Swallow Penumbra's ModSettingChanged events for the life of the returned scope, and settle up once when
    /// it closes. Covers only writes made on the framework thread; the caller still owns the recomposite.
    /// </summary>
    public IDisposable SuppressModSettingEvents()
    {
        _modSettingEventSuppression++;
        return new ModSettingSuppression(this);
    }

    private sealed class ModSettingSuppression(CompositorService owner) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (--owner._modSettingEventSuppression > 0) return;
            owner._knownDisabled.Clear();
            owner.InvalidateUpstreamCache("mod-setting batch");
        }
    }

    /// <summary>
    /// Save the plugin config under <see cref="_bodyModConfigLock"/>, which the off-thread body-mod classifier also
    /// holds; a bare Save() can serialize <see cref="Configuration.KnownBodyMods"/> mid-mutation.
    /// </summary>
    public void SaveConfig() { lock (_bodyModConfigLock) config.Save(); }

    // Mods already told (this session) that their skin Glow now renders as a cloth layer.
    // Concurrent because two composites can overlap and both walk the promotion loop.
    private readonly ConcurrentDictionary<string, byte> _glowPromotedMods = new(StringComparer.OrdinalIgnoreCase);
    // Same, for mods whose skin overlay WANTED a shell but has no body surface to put one on (a face
    // overlay). Separate set so the two notices don't suppress each other on a mod that does both.
    private readonly ConcurrentDictionary<string, byte> _noShellMods = new(StringComparer.OrdinalIgnoreCase);
    // Overlay/material pairs already reported (this session) as painting nothing, keyed by reason too so a
    // different cause still gets said; a composite runs on every gear change, so this stops log spam.
    private readonly ConcurrentDictionary<string, byte> _erasureReported = new(StringComparer.Ordinal);
    // Mods already reported (this session) as contributing nothing, keyed by the whole reason so a new
    // reason for the same mod is still reported.
    private readonly ConcurrentDictionary<string, byte> _inertReported = new(StringComparer.Ordinal);
    // Why each enabled mod contributed nothing to the last composite (see ExplainInertMods). Published as
    // one whole-dictionary swap so the UI never reads a half-built map.
    private volatile IReadOnlyDictionary<string, InertReason> _inertMods =
        new Dictionary<string, InertReason>(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>A "displayed race|snapshot races" pair, and the tick after which the observation goes stale.</summary>
    private sealed record UnsettledRace(string Pair, long ExpiresAtTick);

    /// <summary>
    /// The pair <see cref="WaitForRaceToSettle"/> waited out without the snapshot catching up, so the wait can
    /// be skipped next time. Expires, since a slow load looks identical to a permanent Glamourer override.
    /// </summary>
    private volatile UnsettledRace? _unsettledRace;
    private const int UnsettledRaceMemoMs = 60_000;

    public CompositorResult? LastResult { get; private set; }
    public List<OverlayEntry> LastDiscovered { get; private set; } = [];
    public event Action? ResultChanged;

    // Guards for EnsureDiscovered's background probe: one at a time, and not more often than this.
    private const int DiscoverProbeIntervalMs = 2000;
    private int _discoverProbeRunning;
    private long _lastDiscoverProbeTick;

    // Boot composite: no event reliably fires once both Penumbra's mod list is readable and the local player's
    // draw object exists, so a framework poll waits for both and fires once per login (re-armed on logout).
    private int _bootComposited;      // 1 once the boot composite has fired this login; reset on logout
    private int _bootProbeRunning;    // one off-thread discovery check at a time
    private long _lastBootPollTick;

    /// <summary>
    /// Withholds the boot composite while <c>DesignBindingService</c> decides whether a bound design is still
    /// worn, so the first composite already sees its overrides. Defaults to true because that service is
    /// constructed after this one; every release path in DesignBindingService.FinishBootRestore is unconditional.
    /// Set again at each logout (DesignBindingService.RearmForNextLogin), for the next login's composite.
    /// </summary>
    public volatile bool BootCompositeHold = true;
    private volatile bool _disposed;  // set in Dispose so an in-flight probe task bails

    /// <summary>
    /// Populate <see cref="LastDiscovered"/> for the UI without compositing: discovery only, no write, reload
    /// or redraw. Safe to call every frame; no-ops once populated, while a probe runs, or within the retry interval.
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
        // ResolveUpstream, not a bare resolver: the append host may be modded, so SecondSkinService must read the
        // player's file even where our own redirect masks the path. Only invoked during a composite, after managedModDir is set.
        this.secondSkin = new SecondSkinService(penumbra, textureLoader, discovery, uvRemap, config, log,
                                                ResolveUpstream, SettledUpstream);
        this.seamMaps  = new UvSeamMapService(log);
        this.faceUv    = new FaceUvDoublingService(log, textureLoader, uvRemap);

        // Seeded before the first composite: the manifest on disk already masks last session's append
        // hosts, so PrimeUpstreamCache needs to know which they are before any shell has been rebuilt.
        if (config.AppendHostModelPaths is { Count: > 0 } appendHosts)
            _appendHostModelPaths = new HashSet<string>(appendHosts, StringComparer.OrdinalIgnoreCase);

        modsRoot      = penumbra.GetModDirectory() ?? string.Empty;
        managedModDir = Path.Combine(modsRoot, SidecarDiscoveryService.ManagedModDir);

        // Before the classifications, so a restored verdict is checked against the base set it was
        // computed against rather than being retired wholesale on the first classify of the session.
        if (config.CachedCompositeBaseKeys is { Count: > 0 } baseKeys)
            _compositeBaseKeys = new BaseKeySet(
                new HashSet<string>(baseKeys, StringComparer.OrdinalIgnoreCase),
                ComputeBaseKeysHash(baseKeys),
                config.CachedCompositeBaseSignature);

        foreach (var (modDir, entry) in config.KnownBodyMods)
            _bodyModCache[modDir] =
                (entry.IsBodyMod, entry.AffectsComposite, entry.BaseKeysHash, entry.Fingerprint);

        // Seed from the last session's snapshot instead of a Penumbra walk at boot; trusted until proven stale.
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
        glamourer.LocalPlayerEquipmentChanged  += OnGlamourerEquipmentChanged;
        Plugin.Framework.Update += OnBootPoll;

        // The decode cache is only trimmed when over budget, so release it on an idle timer (30s cadence, any thread).
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

    /// <summary>
    /// How long the decode cache may sit untouched before it is dropped; editing is bursty at a scale of minutes.
    /// </summary>
    private static readonly TimeSpan DecodeCacheIdleRelease = TimeSpan.FromMinutes(5);
    private readonly Timer idleCacheTimer;

    // No "settle redraw" after edits: sync plugins pick up our output from loaded resources, and the in-place
    // reload loads them. A redraw per edit is costly and must not be reintroduced without evidence.

    /// <summary>Push the configured decode-cache budget onto the loader (called by the Settings slider).
    /// Takes effect at once: the loader trims on assignment.</summary>
    public void ApplyDecodeCacheBudget()
        => textureLoader.DecodeCacheBudgetBytes = Math.Max(256, config.DecodeCacheBudgetMb) * 1024L * 1024;

    // Framework-thread poll that fires the boot composite once the player and Penumbra are both ready.
    // Only cheap checks run on the framework thread; discovery runs off-thread, throttled and single-flighted.
    private void OnBootPoll(IFramework fw)
    {
        // Re-arm across logout: with no draw object there's nothing to composite, and clearing the flag
        // lets the next login run its own boot composite (character swaps don't otherwise reach here).
        if ((Plugin.ObjectTable.LocalPlayer?.Address ?? 0) == 0)
        {
            Volatile.Write(ref _bootComposited, 0);
            return;
        }
        // After the logout re-arm and before the latch: the design-binding boot restore owes us its overrides first.
        if (BootCompositeHold) return;
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
                // Ambient: the boot poll re-arms whenever the local player goes away, so every zone
                // transition queues one of these on arrival.
                TriggerRecomposite("boot-ready", force: false);
                // Only latch AFTER the trigger, so a throw above leaves the poll armed to retry.
                Volatile.Write(ref _bootComposited, 1);
            }
            catch (Exception ex) { log.Debug("[Proteus] boot composite probe failed: {0}", ex.Message); }
            finally { Interlocked.Exchange(ref _bootProbeRunning, 0); }
        });
    }

    public void Dispose()
    {
        // Pull our injected host items off the player before teardown (Glamourer is disposed after us), or a reload
        // leaves a phantom bonus item and ring.
        RemoveInjectedGlasses();
        RemoveInjectedRing();

        _disposed = true;   // an in-flight boot-probe task bails instead of touching torn-down bridges

        idleCacheTimer.Dispose();

        penumbra.ModSettingChanged -= OnModSettingChanged;
        penumbra.ModAdded          -= OnModAdded;
        penumbra.ModDeleted        -= OnModDeleted;
        penumbra.PenumbraReady     -= OnPenumbraReady;
        penumbra.PlayerCollectionChanged -= OnPlayerCollectionChanged;
        penumbra.LocalPlayerRedrawn              -= OnLocalPlayerRedrawn;
        glamourer.LocalPlayerStateChanged        -= OnGlamourerStateChanged;
        glamourer.LocalPlayerCustomizationChanged -= OnGlamourerCustomizationChanged;
        glamourer.LocalPlayerEquipmentChanged   -= OnGlamourerEquipmentChanged;
        Plugin.Framework.Update -= OnBootPoll;

        recompositeGate.Stop();
    }

    // ── Core compositor ──────────────────────────────────────────────────────

    /// <summary>
    /// How many <see cref="Recomposite"/> bodies are executing right now. Cancellation is cooperative and the last
    /// <c>ct</c> check is far before the writes, so superseded runs overlap and still publish; the pruner must know.
    /// </summary>
    private int _compositesInFlight;

    /// <summary>
    /// Which composite is the newest one running, stamped beside the in-flight increment. The gate token cannot
    /// answer this (cancelled at trigger time, even for triggers that never composite); the highest-epoch started
    /// run never bails, so exactly one run reaches the manifest. Only needed below the last <c>ct</c> check.
    /// </summary>
    private long _recompositeEpoch;

    /// <summary>
    /// True once a composite that started after this one exists. Read only below the last <c>ct</c> check;
    /// see <see cref="_recompositeEpoch"/>.
    /// </summary>
    private bool Superseded(long epoch) => Volatile.Read(ref _recompositeEpoch) != epoch;

    // Neither bool is defaulted: they are one decision, and a default would let a caller opt into skin reuse by omission.
    private void Recomposite(CancellationToken ct, bool force, bool skinFingerprintAuthoritative,
                             RefreshPreamble preamble)
    {
        // Stamped here, not at trigger time: a trigger that never composites must not orphan a run that did work.
        var epoch = Interlocked.Increment(ref _recompositeEpoch);
        Interlocked.Increment(ref _compositesInFlight);
        var timeline = ClaimRefresh();
        // "burst" spans the first trigger to the one this run answers.
        timeline.MarkAt("burst", preamble.Triggered);
        timeline.MarkAt("debounce", preamble.Debounced);
        timeline.MarkAt("race-settle", preamble.RaceSettled);
        timeline.MarkAt("draw-settle", preamble.DrawSettled);
        timeline.MarkAt("snapshot", preamble.SnapshotReady);
        timeline.Mark("queue");
        try
        {
            RecompositeBody(ct, epoch, force, skinFingerprintAuthoritative, timeline);
        }
        finally
        {
            Interlocked.Decrement(ref _compositesInFlight);
            if (!timeline.Completed) ReturnRefresh(timeline);
        }
    }

    private void RecompositeBody(CancellationToken ct, long epoch, bool force,
                                 bool skinFingerprintAuthoritative, RefreshTimeline timeline)
    {
        new CompositeRun(this, ct, epoch, force, skinFingerprintAuthoritative, timeline).Run();
    }

    // int.MaxValue (unset stack index) prints as "-" so the log is readable.
    private static string FmtIdx(int i) => i == int.MaxValue ? "-" : i.ToString();

    // Order-independent signature of the enabled body shapes, for change detection; empty when none.
    /// <remarks>
    /// Shape sets only, not model stems: Proteus's own relaxed bodies rename the stem, which would read as a shape change.
    /// Which model is drawn is hashed elsewhere in the fingerprint.
    /// </remarks>
    private static string BodyShapeSignature(IReadOnlyDictionary<string, HashSet<string>>? shapes)
    {
        if (shapes == null || shapes.Count == 0) return "";
        return string.Join("|", shapes.Values
            .Select(v => string.Join(",", v.OrderBy(x => x, StringComparer.Ordinal)))
            .OrderBy(x => x, StringComparer.Ordinal));
    }
}
