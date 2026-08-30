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

    // Debounce slot for the recomposite trigger: cancels the pending run and issues the new one's token.
    private readonly DebounceGate recompositeGate = new();
    private readonly SecondSkinService secondSkin;
    private readonly UvSeamMapService seamMaps;

    private long _lastOwnRedrawTick = 0; // TickCount64 when we last called RedrawPlayer()
    private long _lastOwnReapplyTick = 0; // TickCount64 when we last called Glamourer ReapplyState()

    // Armed when we ask the game to rebuild the player's draw object, cleared by the first Gearset
    // finalization that follows. 0 = nothing outstanding. See ConsumeOwnRedrawEcho.
    private long _pendingRedrawEchoTick = 0;

    /// <summary>
    /// Whether the Gearset finalization now arriving is the echo of a redraw PROTEUS asked for — and
    /// consumes that expectation either way, so it can only ever account for ONE Gearset.
    /// <para/>
    /// A redraw rebuilds the draw object, which the game reports as exactly one bulk equipment load
    /// (<c>Gearset</c>), and Penumbra's mod-setting churn makes Glamourer reapply state right after (a
    /// foreign <c>Reapply</c>) — together, byte-for-byte the pairing that identifies an automation apply
    /// (<see cref="DesignBindingService.IsInferredAutomationApply"/>).
    /// <para/>
    /// One-shot rather than a time window on purpose. A window long enough to cover a slow composite's
    /// redraw also swallows every REAL gearset change in it — and since restoring a binding itself ends in
    /// a composite, that blackout would re-arm after every restore, which is exactly when the player is
    /// most likely to be flipping gearsets. Consuming a single expected echo costs the same protection
    /// and nothing else. <paramref name="withinMs"/> only bounds how long we keep waiting for an echo
    /// that may never arrive (a redraw suppressed by DisableAutoRedraw, say).
    /// <para/>
    /// Deliberately NOT armed by the in-place reload: that path reloads textures through
    /// FlagSlotForUpdate and never touches the game's gearset load, so it produces no Gearset to discount.
    /// </summary>
    public bool ConsumeOwnRedrawEcho(int withinMs)
    {
        var stamp = Interlocked.Exchange(ref _pendingRedrawEchoTick, 0);   // consume, expired or not
        if (stamp == 0) return false;
        var since = unchecked(Environment.TickCount64 - stamp);
        return since >= 0 && since < withinMs;
    }

    /// <summary>Record that WE caused the redraw the game is about to perform: the suppression window
    /// <see cref="OnModSettingChanged"/> uses for Glamourer's temporary-setting echo, and the one-shot
    /// Gearset expectation design binding consumes.</summary>
    private void StampOwnRedraw()
    {
        var now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastOwnRedrawTick, now);
        Interlocked.Exchange(ref _pendingRedrawEchoTick, now);
    }

    /// <summary>
    /// Withdraw the expectation <see cref="StampOwnRedraw"/> armed, for a redraw that never reached the
    /// game (Penumbra absent or the IPC threw). Without this the expectation would survive to swallow the
    /// player's next real gearset change — a redraw that did not happen produces no echo to spend it on.
    /// <para/>
    /// Armed before the call and withdrawn after, rather than armed on success afterwards, so there is no
    /// window in which the echo could arrive before we are ready for it.
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
    // Set when a body mod (one whose files include an obj/body/ material) changes, or the player
    // collection changes — anything that could make _activeMtrlSnapshot wrong without a redraw.
    // GetActivePlayerMaterialPaths (a full Penumbra resource-tree walk on the framework thread) is
    // only paid for when this is set or the snapshot is cold; everything else reuses the cache.
    private volatile bool _activeMtrlSnapshotDirty;
    // Every game path the last composite READ as a base — PrimeUpstreamCache's return value, captured
    // verbatim. This is what makes "would a change to this mod actually change our output?" answerable:
    // a mod that redirects none of these cannot move a single pixel we composite.
    //
    // Deliberately NOT _upstreamByGamePath: that one is a resolution memo, emptied wholesale by
    // InvalidateUpstreamCache on every non-sidecar settings change (i.e. exactly when this has to be
    // readable).
    //
    // The set and its hash live in ONE immutable record, swapped by reference, because they must never
    // disagree. As two separate volatile fields, two composites publishing different sets could
    // interleave their writes (setA, setB, hashB, hashA) and leave the pair permanently mismatched —
    // and a verdict cached under a hash that does not describe the set it was computed from is never
    // retired, because the mod's own fingerprint has not changed.
    //
    // The hash is content-derived rather than a counter: verdicts keyed on it are persisted, so it has
    // to mean the same thing in the next session. It changes only when the contents change, so a cached
    // verdict survives the composites that read the same bases — nearly all of them.
    // Signature = a hash of the material paths the composite TARGETS, i.e. the shape of the run rather
    // than its contents. It is what lets a path be retired: paths accumulate while the shape holds, and
    // are dropped wholesale when it changes. See RecordCompositeBaseKeys.
    private sealed record BaseKeySet(HashSet<string> Paths, int Hash, int Signature);
    private volatile BaseKeySet? _compositeBaseKeys;
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

    // Mod directory → the content materials of that mod backing a drawn mesh in the last composite, as
    // paths relative to the mod root. Same publish contract as _shellMaterials: assembled on the composite
    // thread, swapped in as one reference, never mutated afterwards.
    private volatile Dictionary<string, HashSet<string>> _contentMaterials = new();

    /// <summary>
    /// The content materials of <paramref name="modDir"/> that the last composite found backing a drawn
    /// mesh, or null when it found none — which is also what a mod that has not been composited yet looks
    /// like, so callers must treat null as "no information" rather than "nothing is live".
    /// <para/>
    /// Includes materials whose unit could not be given a host: the piece is real and on screen, it just
    /// spilled past the material budget, and its colours are still the user's to set.
    /// </summary>
    public IReadOnlySet<string>? GetLiveContentMaterials(string modDir)
        => _contentMaterials.TryGetValue(modDir, out var mats) ? mats : null;

    /// <summary>
    /// Why none of <paramref name="modDir"/>'s content pieces can be worn by this character, or null when
    /// they can. Read straight off the shell builder, which records it even on the runs that host nothing —
    /// a pack built for another race, enabled by itself, is exactly that run.
    /// </summary>
    public string? GetUnwearableContentReason(string modDir)
        => secondSkin.UnwearableContent.TryGetValue(modDir, out var why) ? why : null;

    /// <summary>
    /// What the last shell build published, split by kind, for the drawn check after the redraw — see
    /// <see cref="SchedulePostRedrawShellCheck"/>.
    ///
    /// Both halves are needed and they answer different questions. <paramref name="Materials"/> is the test:
    /// an appended material only enters the character's resource tree when the mesh referencing it is
    /// actually drawn. <paramref name="Models"/> is the ANCHOR: the host accessory's model loads whether or
    /// not our version of it won, so its presence is what separates "our mesh didn't load" from "the redraw
    /// hasn't finished yet" — and without that separation the check cannot tell a real failure from a slow
    /// disk.
    /// </summary>
    private sealed record ShellDrawnProbe(IReadOnlyList<string> Materials, IReadOnlyList<string> Models);

    /// <summary>
    /// The last shell build's probe, or null when no shell was built. Same publish contract as
    /// <see cref="_shellMaterials"/>: assembled on the composite thread, swapped in as one reference.
    /// </summary>
    private volatile ShellDrawnProbe? _shellDrawnCheck;

    /// <summary>
    /// Drop all three shell locators together, for the tear-down paths that never reach the gear phase
    /// where they are normally published: the plugin being disabled, and the composite's own "no enabled
    /// mods" early return. They describe a shell on the character, so leaving them standing after the shell
    /// has been dropped tells the editor's Glow locator a surface exists that does not.
    /// <para/>
    /// The composite does NOT call this on its way in — see the gear phase, where they are built into
    /// locals and published in one step precisely so they are never observably empty while a shell stands.
    /// </summary>
    private void ClearShellLocators()
    {
        _shellMaterials   = new();
        _contentMaterials = new(StringComparer.OrdinalIgnoreCase);
        _shellDrawnCheck  = null;
    }

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
    // The bare-body e0000 models drawn in the slots gear does NOT cover (part -> .mdl game path), captured
    // in the same walk. Names the race the game resolved each bare slot to, which is not always the
    // character's own. See BareBodyModelsFromModels.
    private volatile IReadOnlyDictionary<string, string>? _bareBodyModels;
    // The character's real race code ("0801"), from the same walk. Distinct from charCode, which is the
    // shared BODY code. See DrawnRaceCodeFromModels.
    private volatile string? _drawnRaceCode;
    // WHO _drawnRaceCode was read off. The cache is deliberately sticky — a walk that carried no human
    // model keeps the last value rather than reporting "unknown" (see the assignments below) — but sticky
    // with no owner means the PREVIOUS character's race survives a character switch, and nothing ever
    // cleared it: the plugin subscribes to no login/logout event, so only a reload dropped it.
    //
    // That is not a harmless staleness. _drawnRaceCode becomes hostRace in SecondSkinService.Build, and
    // its sibling caches on the same walk (_equippedPartModels/_bareBodyModels) are assigned
    // UNCONDITIONALLY while this one is not — so a walk that resolves a new character's body parts before
    // its chara/human models leaves the two describing different people. hostRace and cutCode then
    // disagree for a reason that is pure bookkeeping, and the carrier branch empties the wearer's own EQDP
    // entry on the strength of it. Binding the value to an owner is what stops it crossing characters;
    // SecondSkinService.CanFallThrough is the second line of defence for the intra-walk case.
    private volatile string? _drawnRaceOwner;
    // Serializes the _drawnRaceCode/_drawnRaceOwner pair. They are two fields but one fact, and the two
    // callers of UpdateDrawnRaceCode are on different threads (the redraw hook on the framework thread,
    // the composite off it, and composites overlap — see _compositesInFlight). Interleaved, the stores
    // tear into a new owner beside an old code, which every later call then treats as matching and KEEPS.
    // That is the original bug with a longer life, so the pair moves under a lock. Readers still read
    // _drawnRaceCode unlocked: they only ever see one whole value or the other, never a mismatch.
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
    // modDir -> (does this mod ship an obj/body/ material file, fingerprint it was computed at).
    // Fingerprint = summed size+mtime over the mod's own default_mod.json/group_*.json, so a mod
    // update is detected without needing a plugin restart. Seeded from config.KnownBodyMods.
    // ConcurrentDictionary because ClassifySurfaceMod runs on a background thread (its manifest file I/O +
    // config.Save must not touch the framework thread) while OnModDeleted may read/remove entries.
    // AffectsComposite is the narrower verdict — the mod provides a path this composite actually reads —
    // and is what gates a recomposite; IsSurfaceMod stays the wide one that drives cache invalidation.
    // BaseKeysHash records which composite base set AffectsComposite was computed against.
    private readonly ConcurrentDictionary<string,
        (bool IsSurfaceMod, bool AffectsComposite, int BaseKeysHash, long Fingerprint)> _bodyModCache =
        new(StringComparer.OrdinalIgnoreCase);
    // Serializes the config.KnownBodyMods mutations + config.Save() done off-thread by ClassifySurfaceMod
    // and OnModDeleted, so a save never serializes the dictionary while another thread mutates it.
    private readonly object _bodyModConfigLock = new();

    // Set (value unused) of mods we have already REACTED to while they were disabled — i.e. a composite
    // or a body-mod evaluation was kicked off with the mod off. Membership is what lets
    // OnModSettingChanged skip further setting changes on a disabled mod; see that handler for why the
    // entry is only added once the handler is past every early return, and why "not a member" is the safe
    // default (it merely means "don't skip"). Only OnModSettingChanged adds, on the framework thread.
    private readonly ConcurrentDictionary<string, byte> _knownDisabled =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Save the plugin config under the same lock the off-thread body-mod classifier uses. Callers on
    /// the framework thread need this too: <c>Save()</c> serializes the WHOLE Configuration, so a bare
    /// call can serialize <see cref="Configuration.KnownBodyMods"/> while ClassifySurfaceMod is mutating it —
    /// the exact race <see cref="_bodyModConfigLock"/> exists for.
    /// </summary>
    public void SaveConfig() { lock (_bodyModConfigLock) config.Save(); }

    // Mods already told (this session) that their skin Glow now renders as a cloth layer. The promotion
    // itself is recomputed every composite, so without this the notice would repeat on every run.
    // Concurrent because two composites genuinely overlap (see _compositesInFlight) and both walk the
    // promotion loop — a plain HashSet resizing under two threads loses entries or corrupts its buckets.
    private readonly ConcurrentDictionary<string, byte> _glowPromotedMods = new(StringComparer.OrdinalIgnoreCase);
    // Same, for mods whose skin overlay WANTED a shell but has no body surface to put one on (a face
    // overlay). Separate set so the two notices don't suppress each other on a mod that does both.
    private readonly ConcurrentDictionary<string, byte> _noShellMods = new(StringComparer.OrdinalIgnoreCase);
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
    /// The pair <see cref="WaitForRaceToSettle"/> waited out without the snapshot ever catching up, so the
    /// wait can be skipped next time. Expires, because the observation is only PROBABLY a permanent Glamourer
    /// display override — a load slow enough to blow the window looks identical, and that must not silently
    /// disable the wait for the rest of the session.
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

    // Boot composite. At game start no event reliably fires a composite once BOTH Penumbra's mod list is
    // readable AND the local player's draw object exists: PenumbraReady can beat the player, the first
    // redraw can beat discovery (or never fire if the character was drawn before we loaded), the glamourer
    // design-apply gets echo-suppressed, and the discovery probe only runs while the status window is open.
    // This framework poll waits for both preconditions and fires once per login (re-armed when the draw
    // object goes away, so logging into another character composites again).
    private int _bootComposited;      // 1 once the boot composite has fired this login; reset on logout
    private int _bootProbeRunning;    // one off-thread discovery check at a time
    private long _lastBootPollTick;

    /// <summary>
    /// Withholds the boot composite while <c>DesignBindingService</c> decides whether the character is
    /// still wearing a bound design. Its colour/gear/stack overrides are part of the composite's inputs
    /// (see BuildCompositeFingerprint), so a composite that runs before they are published paints
    /// metadata colours and then has to be redone — two full pipelines and two redraws per load.
    /// <para/>
    /// Defaults to TRUE, released by Plugin's constructor when no boot restore was armed. It cannot
    /// default to false and be raised by DesignBindingService instead: that service is constructed
    /// AFTER this one, and the plugin constructor does not run on the framework thread, so a tick could
    /// slip between the two and fire the boot composite before the hold was ever taken.
    /// <para/>
    /// Every release path in DesignBindingService.FinishBootRestore is unconditional — adopt, abstain,
    /// disable and timeout all release it — so a composite always happens.
    /// </summary>
    public volatile bool BootCompositeHold = true;
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
        // ResolveUpstream, not a bare resolver: the shell's append host is an item the player picked and
        // may have modded, so SecondSkinService has to read THEIR file as its base even on the composites
        // where our own redirect masks the path. Safe as a method group here even though managedModDir is
        // assigned below — the delegate is only invoked during a composite, long after this returns.
        this.secondSkin = new SecondSkinService(penumbra, textureLoader, discovery, uvRemap, config, log,
                                                ResolveUpstream);
        this.seamMaps  = new UvSeamMapService(log);

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
        glamourer.LocalPlayerEquipmentChanged  += OnGlamourerEquipmentChanged;
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

    /// <summary>
    /// How long the decode cache may sit untouched before it is dropped.
    /// <para/>
    /// Was 60s, which is shorter than the gap between two edits in an ordinary session: a measured run had
    /// released 15 entries eleven seconds before the composite that then had to decode them all again.
    /// Editing is bursty at a scale of minutes, not seconds, and the cache exists for exactly that burst.
    /// Five minutes keeps it across the pauses that are part of editing while still not holding gigabytes
    /// through an evening of raiding.
    /// </summary>
    private static readonly TimeSpan DecodeCacheIdleRelease = TimeSpan.FromMinutes(5);
    private readonly Timer idleCacheTimer;

    // A "settle redraw" used to live here: one full redraw a few seconds after edits stopped, on the
    // theory that Mare-family sync plugins drop tex/mdl/mtrl arriving between redraws and so could never
    // see our output. Removed 2026-08-09 after measuring it. The theory was wrong — those plugins build a
    // peer's file list from currently-LOADED resources, and our in-place reload loads them. Verified end
    // to end with the redraw disabled: a skin edit wrote two textures and PSync independently found two
    // new hashes, compressed, uploaded and pushed them, with no redraw anywhere in the sequence.
    //
    // The real cause of peers seeing nothing was a composite deleting its own output up front, which left
    // live redirects dangling; that is fixed by keeping output on disk until the next composite prunes it.
    // Don't reintroduce a redraw here without evidence: it cost a full character redraw per edit, and
    // those redraws were what kept landing inside composite windows and triggering the very bug.

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
        // Checked after the logout re-arm above (so re-arming still works) and before the latch below
        // (so the release doesn't have to race it): the design-binding boot restore owes us its
        // overrides before the first composite reads them.
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
        // Pull our injected host items off the player before we tear down (Glamourer is disposed after us
        // in Plugin.Dispose, so this still succeeds). Otherwise a plugin reload leaves a phantom bonus item
        // and ring until the next design/reset.
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

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnModSettingChanged(ModSettingChange change, Guid collId, string modDir, bool inherited)
    {
        if (string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return;
        var playerColl = penumbra.GetPlayerCollectionId();
        if (playerColl == null || collId != playerColl.Value)
            return;

        // A disabled mod contributes nothing: no overlay art, and no redirect that could move a base we
        // read. Tweaking its option groups, priority or temporary settings therefore cannot change the
        // composite, so skip the whole handler — including the upstream invalidation below, which is not
        // free (the next composite re-derives every upstream, briefly unpublishing our redirects). Penumbra
        // fires these for disabled mods just as readily as for enabled ones, and Glamourer re-asserts
        // temporary settings for every mod a design touches whether or not the mod is on.
        //
        // The transition itself must still get through: by the time a disable event reaches us the mod
        // already reads as disabled, and THAT one does change the composite. Hence the _knownDisabled gate
        // — skip only mods we have already ACTED on while off (see the write further down), never on a
        // live reading alone. A mod that isn't a member is processed exactly as before.
        //
        // Membership is checked before asking Penumbra anything: this handler runs for every mod in the
        // player's collection, and the set is empty for all the enabled ones, so the common path costs a
        // dictionary lookup rather than an IPC round trip.
        if (_knownDisabled.ContainsKey(modDir))
        {
            var live = penumbra.GetModSettings(playerColl.Value, modDir);
            if (live is { Enabled: false })
            {
                NoteSkippedDisabled(modDir, live.Value.Priority);
                return;
            }
            // Back on, or no longer readable — either way the recorded verdict no longer holds. Drop it
            // and fall through, so this event is treated as the real change it is.
            _knownDisabled.TryRemove(modDir, out _);
        }

        var sidecar = HasSidecar(modDir);

        // Before any of the early returns below. Anything that isn't one of our overlay mods can change
        // which file a base game path resolves to — a body/skin mod toggling, or a new one outranking the
        // old — and that has to be recorded even when we decide not to recomposite. The echo-suppression
        // return further down is NOT sidecar-gated, so leaving this after it let a design-driven body-mod
        // change inside the 1500 ms window keep a stale upstream indefinitely. Sidecar toggles are exempt:
        // they only add overlay art, they can't move a base, and they fire constantly, so flushing on them
        // would empty the cache exactly when the resolve race needs it.
        //
        // Mods already CLASSIFIED as non-body are exempt too. Emptying the cache is no longer free: the next
        // composite has to re-derive every upstream through PrimeUpstreamCache, which briefly unpublishes
        // those redirects — so doing it for a hair or VFX mod that cannot move any base we read means a
        // pointless flicker. Glamourer re-asserts temporary settings for every mod a design touches on each
        // zone-in, so unfiltered this fired constantly. See MayMoveOurBases for why an unknown mod still
        // invalidates, and EvaluateSurfaceModOffThread for the case where a classification turns out stale.
        if (!sidecar && MayMoveOurBases(modDir))
            InvalidateUpstreamCache($"ModSettingChanged:{change}:{modDir}");

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

        // Everything below this line acts. THIS is where a mod earns its place in _knownDisabled, not the
        // gate at the top: the set means "a composite was kicked off with this mod off", and the early
        // returns above — the echo suppression in particular — abandon the handler without producing one.
        // Recording at the top instead let a disable that arrived inside the 1500 ms echo window mark the
        // mod as handled while the composite still carried it, and every later event for it was then
        // skipped, stranding the character on a base a temporarily-disabled body mod had supplied.
        //
        // This is the only place that pays for the extra IPC, and it sits next to a recomposite or an
        // off-thread manifest scan, so the lookup is noise against the work it is gating.
        if (penumbra.GetModSettings(playerColl.Value, modDir) is { Enabled: false })
            _knownDisabled[modDir] = 0;

        // A TemporarySetting is the only change here that Glamourer generates by itself, and it re-asserts
        // them after every redraw — so it is the one change kind that routinely carries no new information.
        // Every other kind is a deliberate act by the user or another plugin and must always composite.
        bool ambient = change == ModSettingChange.TemporarySetting;

        if (sidecar)
        {
            // The mod's files may have changed underneath a cached decode (a reinstall/edit that kept the
            // same timestamp or byte length); drop this mod's cached textures so the composite re-reads them.
            textureLoader.EvictMod(modDir);
            TriggerRecomposite($"ModSettingChanged:{change}:{modDir}", force: !ambient);
            return;
        }

        // Not one of our overlay mods: the only other thing we react to is a body mod (ships an
        // obj/body/ material), whose change can leave the cached snapshot wrong without a redraw.
        // Its detection does manifest file I/O + a config.Save, so run it off the framework thread.
        EvaluateSurfaceModOffThread(modDir, $"ModSettingChanged:{change}:{modDir}", force: !ambient);
    }

    /// <summary>
    /// Fold a skipped disabled mod's current priority and enabled state into <see cref="LastDiscovered"/>.
    ///
    /// Needed because <see cref="DiscoveredSetsEqual"/> compares priority, and that list is otherwise only
    /// refreshed by a completed composite or by the enable-state branch above — both of which the skip
    /// bypasses. Without this, reordering a DISABLED overlay mod left the stale priority in place until the
    /// next Glamourer state change diffed it and fired a full recomposite for a mod contributing nothing,
    /// turning the skip into a deferral rather than an avoidance.
    ///
    /// No-op for anything that isn't one of our overlay mods: only those are ever in the list. Copy-on-write
    /// because background composite threads read <see cref="LastDiscovered"/> and the UI enumerates it; the
    /// re-sort keeps the priority-ascending order <c>Discover</c> establishes. If a composite is in flight it
    /// will overwrite this with its own fresher walk, which is equally correct.
    /// </summary>
    private void NoteSkippedDisabled(string modDir, int priority)
    {
        var snapshot = LastDiscovered;
        var idx = snapshot.FindIndex(e =>
            string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var entry = snapshot[idx];
        if (entry.Priority == priority && !entry.Enabled) return;

        var updated = new List<OverlayEntry>(snapshot);
        updated[idx] = entry with { Priority = priority, Enabled = false };
        updated.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        LastDiscovered = updated;
    }

    /// <summary>
    /// Drop every remembered upstream. Called whenever Penumbra's resolution for the player could have
    /// changed under us — the cache is only ever read when a resolve races our own reload, and a stale
    /// entry there means compositing onto the wrong base with nothing but a Debug line to show for it.
    /// Cheap to rebuild: the next composite's normal resolutions repopulate it.
    /// </summary>
    /// <summary>
    /// Could a settings change on this mod move a file we read as a composite base? Answers the question
    /// WITHOUT touching disk, because it is called from a framework-thread Penumbra event.
    ///
    /// "Unknown" answers TRUE. The asymmetry is the whole point: invalidating unnecessarily costs one
    /// upstream re-derivation, while failing to invalidate when a body mod really did move means compositing
    /// onto the wrong base with nothing in the log to show for it. Only a mod we have already classified as
    /// shipping no body materials is exempt.
    ///
    /// Note this consults the cached VERDICT and ignores the fingerprint <see cref="ClassifySurfaceMod"/> stores
    /// beside it — checking that would mean reading the mod's manifest, which is exactly the I/O this has to
    /// avoid here. A mod that changes from non-body to body under a stale verdict is caught instead by
    /// <see cref="EvaluateSurfaceModOffThread"/>, which re-runs the full fingerprinted classification off-thread
    /// and invalidates there.
    /// </summary>
    /// <remarks>
    /// Tests AffectsComposite and deliberately not IsSurfaceMod. The two answer different questions, and
    /// this one is literally "could it move a base we read" — which is what AffectsComposite measures. A
    /// mod can also provide such a base without its manifest naming a surface tree at all (see
    /// EvaluateSurfaceModOffThread on "Drenched Wet Skin"), so the surface verdict is neither necessary
    /// nor sufficient here. Admitting it as well would re-admit every hair/face/iris mod — exactly the
    /// mods the comment at the call site wants exempt, because emptying the cache costs a re-derivation
    /// that briefly unpublishes our redirects.
    /// </remarks>
    private bool MayMoveOurBases(string modDir)
        => !_bodyModCache.TryGetValue(modDir, out var cached) || cached.AffectsComposite;

    private void InvalidateUpstreamCache(string reason)
    {
        // Deliberately does NOT clear _lastCompositeFingerprint. This fires for every non-sidecar mod,
        // which includes Glamourer re-asserting a body mod's temporary settings on zone-in — the exact
        // event the unchanged-inputs gate exists to skip, so nulling here would blind the gate whenever a
        // body mod happens to be design-associated. The change this invalidation is really guarding against
        // (a different file behind a base path) is caught instead by the upstream identity the fingerprint
        // carries: PrimeUpstreamCache re-resolves these paths before the gate reads them, so a body mod
        // that genuinely moved shows up as a different disk path or mtime and the composite runs.
        // Cleared alongside the memo: with nothing remembered, needPrime re-admits every path on the
        // !ContainsKey clause anyway, so keeping retry marks here would only let the set grow forever.
        // _upstreamSettled must go too — it outranks live resolves, so leaving it set would keep asserting a
        // value derived from the collection we were just told has changed.
        _upstreamUnsettled.Clear();
        _upstreamSettled.Clear();
        if (_upstreamByGamePath.IsEmpty) return;
        _upstreamByGamePath.Clear();
        log.Debug("[Proteus] upstream cache cleared ({0})", reason);
    }

    private void OnModAdded(string modDir)
    {
        // A (re)install almost always rewrites the mod's files — evict any stale cached decodes for it.
        textureLoader.EvictMod(modDir);
        InvalidateUpstreamCache($"ModAdded:{modDir}");
        if (HasSidecar(modDir))
        {
            TriggerRecomposite($"ModAdded:{modDir}");
            return;
        }
        // A (re)installed body mod may have changed its files without changing its directory name —
        // ClassifySurfaceMod's fingerprint check handles that. Off-thread, same as OnModSettingChanged.
        EvaluateSurfaceModOffThread(modDir, $"ModAdded:{modDir}");
    }

    private void OnModDeleted(string modDir)
    {
        // Files are already gone by the time this fires, so use the last-known classification
        // rather than rescanning; then drop it, there's nothing left to invalidate against. Both
        // verdicts are needed and mean different things: the wide one still says whether the material
        // snapshot went stale, while only the narrow one justifies a rebuild (deleting an iris mod
        // changes no base we composite). Unknown mods answer false to both — nothing we ever read.
        var known       = _bodyModCache.TryGetValue(modDir, out var cached);
        var wasSurface  = known && cached.IsSurfaceMod;
        var wasComposed = known && cached.AffectsComposite;
        if (wasSurface || wasComposed) _activeMtrlSnapshotDirty = true;
        InvalidateUpstreamCache($"ModDeleted:{modDir}");
        // The directory can come back (a reinstall keeps the name), and it would come back enabled or not
        // on its own terms — a remembered "was disabled" from the old install must not suppress the first
        // event of the new one.
        _knownDisabled.TryRemove(modDir, out _);

        // Drop the cached classification off the framework thread — config.Save is a disk write.
        Task.Run(() =>
        {
            _bodyModCache.TryRemove(modDir, out _);
            lock (_bodyModConfigLock)
                if (config.KnownBodyMods.Remove(modDir)) config.Save();
        });

        if (LastDiscovered.All(e => !string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase))
            && !wasComposed)
            return;
        TriggerRecomposite($"ModDeleted:{modDir}");
    }

    // ClassifySurfaceMod does manifest file I/O + a config.Save on a cache miss, both of which must stay
    // off the framework-thread event handlers. Evaluate it on a background thread.
    //
    // The two verdicts drive DIFFERENT things, and conflating them is what made every unrelated mod
    // recomposite. "Surface" is deliberately wide (body/face/hair/tail/zear, any file, see
    // BodyMaterialPattern) because the caches below have to be dropped whenever the answer to "is this
    // material on the character" could have moved — an iris mod really does move that. But recompositing
    // is a 5-7s rebuild-and-redraw, and it can only ever change the output if the mod provides one of the
    // paths we actually read: an eye mod's *_iri_d.tex is not a base of any composite that isn't
    // compositing that eye. So the trigger takes the narrow verdict, and the bookkeeping takes the wide one.
    //
    // Neither verdict implies the other, so the two are tested separately rather than nested. A skin mod
    // can feed us WITHOUT looking like a surface mod: "Drenched Wet Skin" redirects Bibo's invented
    // chara/bibo_mid_*.tex paths directly and its manifest contains no obj/body/ literal at all, so the
    // old surface-only gate meant it never triggered a recomposite — measured, not hypothetical.
    private void EvaluateSurfaceModOffThread(string modDir, string reason, bool force = true)
    {
        Task.Run(() =>
        {
            try
            {
                var (surface, affects) = ClassifySurfaceMod(modDir);
                if (!surface && !affects) return;
                textureLoader.EvictMod(modDir);   // body textures may have changed under a cached decode
                // The wide verdict's own job: a face/hair/iris mod moves the answer to "is this material on
                // the character", so the snapshot has to be re-walked even though nothing below will run.
                // Either verdict earns the re-walk, for different reasons. A face/hair/iris mod moves the
                // answer to "is this material on the character" directly. And a mod that feeds us without
                // naming a surface tree still can: an append host is an accessory model, and replacing it
                // changes which materials that model references — so gating this on `surface` alone would
                // recomposite the shell against a material snapshot known to be stale.
                if (surface || affects) _activeMtrlSnapshotDirty = true;

                // The gate. A mod that provides none of our base paths cannot change a pixel of the output,
                // and forcing a composite for it is not merely wasted work: TriggerRecomposite(force)
                // latches _forcePending, which then defeats the unchanged-inputs gate for the NEXT
                // composite too.
                //
                // The upstream invalidation stays BELOW this gate, deliberately. Dropping that cache is not
                // free — the next composite re-derives every upstream through PrimeUpstreamCache, which
                // briefly unpublishes our redirects — and a mod that moves no base we read has nothing to
                // invalidate against. Doing it anyway is the pointless flicker the comment at
                // OnModSettingChanged's MayMoveOurBases call warns about.
                if (!affects)
                {
                    // Information, not Debug: this is the line that explains an absent recomposite, and it
                    // fires at most once per deliberate user action on a mod we chose not to act on. The
                    // base-set size is here so a false negative is diagnosable from the log alone.
                    log.Information("[Proteus] {0}: mod provides none of the {1} base path(s) this composite "
                                  + "reads — no recomposite",
                        reason, _compositeBaseKeys?.Paths.Count ?? 0);
                    return;
                }

                // The authoritative invalidation. MayMoveOurBases had to answer from a cached verdict with no
                // disk access, so it can be working from a stale classification — a mod that only just began
                // providing one of our bases would have been let through. ClassifySurfaceMod above has just
                // re-derived it against a fresh fingerprint, so this is the point where we actually know.
                // Idempotent: when the sync check already invalidated, the cache is empty and this returns
                // immediately.
                InvalidateUpstreamCache(reason);
                // Safe to gate on an ambient re-assert: the upstream identity in the composite fingerprint
                // is what actually detects a body mod that moved, and the EvictMod above guarantees the
                // prime re-reads rather than trusting a cached decode.
                TriggerRecomposite(reason, force: force);
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
        // Same hold as OnBootPoll: at game boot this can fire while the design-binding restore is still
        // waiting for the player, and compositing without its overrides means doing the whole pipeline
        // twice. OnBootPoll picks it up once the hold releases.
        if (BootCompositeHold) return;
        // Only trigger if discovery already sees mods. PenumbraReady can fire before Penumbra's
        // mod settings are readable; if discovery returns empty we'd wipe the existing output.
        // Leave previous-session files intact — ModSettingChanged/ModAdded will fire the first
        // real composite once settings are available.
        if (discovery.DiscoverEnabled().Count > 0)
            TriggerRecomposite("PenumbraReady", force: false);
    }

    private void OnPlayerCollectionChanged()
    {
        // The collection assigned to the player changed — the enabled mod set, priorities and
        // option selections are all collection-scoped, so the whole composite must be recomputed.
        // Rare event; not worth trying to scan every mod in the new collection, just force one walk.
        _activeMtrlSnapshotDirty = true;
        InvalidateUpstreamCache("collection-changed");
        // Enabled state is collection-scoped, so every remembered verdict now describes the wrong
        // collection. Dropping them makes the first setting change for each mod count as a possible
        // transition again, which is what OnModSettingChanged's disabled skip needs to stay honest.
        _knownDisabled.Clear();
        if (!config.PluginEnabled) return;
        TriggerRecomposite("collection-changed", force: false);
    }

    // Called on the framework thread by PenumbraBridge whenever the local player's draw object is
    // redrawn. Most material changes happen this way, so this is the cheap, common-case refresh —
    // it's piggybacking on work Penumbra/the game already did, not an extra query. Only write
    // non-null: GameObjectRedrawn can fire mid-redraw while the draw object is being destroyed, at
    // which point GetActivePlayerMaterialPaths returns null. Writing null would clear a valid cached
    // snapshot and trigger the all-races bug. An EMPTY walk is that same mid-redraw moment wearing a
    // different mask — a drawable character always has materials — and it does the identical damage while
    // passing a null test, so both are rejected here.
    private void OnLocalPlayerRedrawn()
    {
        var snapshot = penumbra.GetActivePlayerMaterialPaths();
        bool equipChanged = false;
        bool modelWalkOk = false;   // a draw object we actually read — see the carrier reconcile below
        if (snapshot is { Count: > 0 })
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
            //
            // Guarded on its OWN null, not the material snapshot's: this is a second walk and can come
            // back null by itself (the draw object being torn down mid-redraw) while materials resolved
            // fine. Every derived map would then be EMPTY — wiping four good caches and reporting a full
            // unequip that never happened, which fires a phantom "equipment-change" recomposite that
            // builds the shell from nothing. Same reasoning as the material snapshot above; keep the last
            // values and claim no change, since TriggerRecomposite re-walks before it composites anyway.
            var modelPaths = penumbra.GetActivePlayerModelPaths();
            // Empty is the same failure as null — see the composite-side walk. Testing only for null here
            // meant the paragraph above described the right hazard and then let it through: an empty set
            // wipes all five maps and reports a full unequip that never happened.
            if (modelPaths is { Count: > 0 })
            {
                var equipped = EquippedPartModelsFromModels(modelPaths);
                var accessories = EquippedAccessoryModelsFromModels(modelPaths);
                var metModels = EquippedMetModelsFromModels(modelPaths, InvisibleGlasses.FacewearModelSets(Plugin.DataManager));
                var bare = BareBodyModelsFromModels(modelPaths);
                var humanParts = HumanPartModelsFromModels(modelPaths);
                _equippedPartModels = equipped;
                _equippedAccessoryModels = accessories;
                _equippedMetModels = metModels;
                _bareBodyModels = bare;
                _humanPartModels = humanParts;
                // Framework thread (this is the redraw hook), so the owner can be read inline.
                UpdateDrawnRaceCode(modelPaths, Plugin.ObjectTable.LocalPlayer?.Name.TextValue);
                var sig = EquipSignature(equipped, accessories, metModels, bare, humanParts);
                equipChanged = _lastEquipSignature != null && !string.Equals(_lastEquipSignature, sig, StringComparison.Ordinal);
                _lastEquipSignature = sig;
                modelWalkOk = true;
            }
        }

        RefreshGlamourerCharCode();

        // Put the carriers back, NOW. They are equipped with ApplyFlag.Once, so this redraw is exactly what
        // reverted them — and with them gone the shell has no host and stops rendering. The reconciles are
        // idempotent and cost an IPC each, so running them on every redraw is far cheaper than the composite
        // that used to be the only thing that re-equipped them.
        //
        // Off-thread: the reconciles block on RunOnFrameworkThread and read Lumina sheets, neither of which
        // belongs on a frame. alreadyHosted, because the shell's redirect is live at the carrier's model path
        // the whole time now — the equip's own redraw lands straight on the finished shell, so chaining a
        // recomposite here would only redo identical work. Skipped while a composite is running, since that
        // composite will run its own reconcile with fresher arguments.
        if (config.PluginEnabled && modelWalkOk && _secondSkinActive
            && Volatile.Read(ref _compositesInFlight) == 0)
        {
            var (gearWanted, shellBuilt, onFacewear, ringSlot) =
                (_lastGearWanted, _lastShellBuilt, _lastShellOnFacewear, _lastShellCarrierSlots);
            Task.Run(() =>
            {
                try
                {
                    ReconcileInvisibleGlasses(gearWanted, shellBuilt, onFacewear, alreadyHosted: true);
                    ReconcileEmperorRing(gearWanted, shellBuilt, ringSlot);
                }
                catch (Exception ex) { log.Debug("[Proteus] carrier reconcile on redraw failed: {0}", ex.Message); }
            });
        }

        if (equipChanged && config.PluginEnabled)
            TriggerRecomposite("equipment-change", force: false);

        if (Interlocked.Exchange(ref _pendingCustomizationRecomposite, 0) == 1)
            TriggerRecomposite("glamourer-customization", force: false);
    }

    /// <summary>
    /// Glamourer moved an equipped item or a pair of glasses.
    /// <para/>
    /// AMBIENT, and that is what makes it affordable. Glamourer raises this per changed slot, so applying
    /// an outfit produces a burst — the 200ms debounce collapses those into one run, and the
    /// unchanged-inputs gate then costs a fingerprint compare when nothing the composite reads actually
    /// moved. With the skin-reuse gate in place a real outfit change rebuilds only the shell.
    /// <para/>
    /// Not narrowed to "only when a shell exists": equipping shoes replaces the bare foot model, which is
    /// one of the meshes the skin's seam map is keyed on, so an equip can legitimately change the SKIN
    /// too. Deciding that here would duplicate the fingerprint's job and get it wrong; let the gate answer.
    /// </summary>
    private void OnGlamourerEquipmentChanged()
    {
        if (!config.PluginEnabled) return;
        TriggerRecomposite("glamourer-equip", force: false);
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
                TriggerRecomposite("glamourer-customization-timeout", force: false);
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
            // No zero defaults. Gender 0 is MALE, so a Gender field that fails to read — a partial state,
            // an actor mid-load, a Glamourer schema change — used to turn a Midlander female into a
            // perfectly plausible "c0101" rather than an error. Nothing downstream can tell that apart
            // from a real male: the probe in SecondSkinService accepts c0101 (its e0000 models exist),
            // the bare parts are rebuilt at c0101, and those paths then win the cutCode vote unanimously.
            // The shell is published male-cut, the game deforms it male->female on load, and it arrives
            // shrunk and low — the reported bug, from one missing JSON field.
            //
            // Unknown must therefore mean unknown. A null char code falls back to _lastCompositedCharCodes,
            // which is read off the drawn body materials and cannot get the gender wrong.
            var race  = cust["Race"]?["Value"]?.ToObject<byte>();
            var tribe = cust["Clan"]?["Value"]?.ToObject<byte>();
            var sex   = cust["Gender"]?["Value"]?.ToObject<byte>();
            if (race == null || tribe == null || sex == null)
            {
                Plugin.Log.Warning("[Proteus] Glamourer customize is incomplete (race={0}, clan={1}, "
                                 + "gender={2}) — treating the char code as unknown rather than guessing",
                    race?.ToString() ?? "missing", tribe?.ToString() ?? "missing", sex?.ToString() ?? "missing");
                _glamourerCharCode = null;
                return;
            }
            _glamourerCharCode = BodyCodeFromCustomize(race.Value, tribe.Value, sex.Value);
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
        TriggerRecomposite("glamourer-design", force: false);
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
    // any thread. It's driven off the framework thread (EvaluateSurfaceModOffThread) because the
    // manifest reads + config.Save on a cache miss shouldn't block the game's main thread.

    // Bumped whenever BodyMaterialPattern below changes what it matches, OR whenever a verdict stored in
    // BodyModCacheEntry gains a meaning old entries cannot carry. It seeds every mod's fingerprint, so
    // raising it invalidates the whole cached classification — including the entries restored from
    // config.KnownBodyMods — and forces one re-scan per mod. Without it, widening the pattern would change
    // nothing for any mod already on disk: their manifests are untouched, so their fingerprints still match
    // and the stale "not a body mod" verdict is returned forever.
    //
    // 3 -> 4 was the SECOND reason, not the first: BodyMaterialPattern is unchanged, but the entry gained
    // AffectsComposite/BaseKeysHash, which deserialise from an older config as (false, 0). A real body mod
    // restored that way would answer "affects nothing" and, in a session that has not yet recorded a base
    // set (hash also 0), match on hash and never be re-derived — silently silenced forever. Do not remove
    // this bump on the grounds that the pattern did not change.
    private const int SurfaceModClassifierVersion = 4;

    private static readonly Regex BodyMaterialPattern = new(
        // Every SKIN surface, not just the body. The snapshot this guards (_activeMtrlSnapshot) is what
        // answers "is this material currently on the character", and a face/hair/tail/ear mod moves those
        // answers exactly as a body mod moves the body's. Matching only obj/body/ meant a mod that redirects
        // face files never marked the snapshot dirty, so it went stale and stayed stale — invisible until
        // something asks whether a given face is drawn.
        //
        // And any file under those trees, not only ".mtrl". Requiring a material was already a narrow test
        // for the body and is a WRONG one for the rest: face and hair mods overwhelmingly ship textures
        // alone (obj/face/f0001/texture/…_base.tex) and never touch a material, so a ".mtrl" requirement
        // classified the most common shape of face mod as "not a surface mod" and skipped the very
        // invalidation this exists for. The cost of the wider match is one extra ~2-8 ms walk when such a
        // mod is toggled, which is the same cost a body mod already pays; the cost of the narrow one is a
        // stale snapshot with nothing in the log.
        //
        // BodySuffixes is no longer consulted here. It describes body-TYPE suffixes (bibo/eve/b/a) for
        // naming a UV space, which has nothing to do with whether a mod touches a surface at all, and
        // leaning on it is what tied this test to materials in the first place.
        @"obj[/\\](body|face|hair|tail|zear)[/\\]",
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
        // Seeded with the classifier version, not 0, so widening what counts as a surface material retires
        // every cached verdict at once (see SurfaceModClassifierVersion).
        long fp = SurfaceModClassifierVersion;
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

    // Every game path a manifest redirects. Penumbra writes these as lowercase forward-slash JSON
    // strings ("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl"), both as
    // the keys of a Files map and inside option groups, so one quoted-string match over the raw text
    // finds them all without having to model v3's and v4's two different layouts.
    private static readonly Regex ManifestGamePathPattern = new(
        // Not anchored to "chara/": a base key only has to be something we READ, and skin mods invent
        // paths outside the usual trees (Bibo's chara/bibo_mid_base.tex is tame; others are not). An
        // over-wide match costs a few extra strings in a set that is only ever tested for overlap —
        // a local file value like "textures/foo.tex" simply never appears in baseKeys — while an
        // over-narrow one silently drops a real base and skips the recomposite that base needed.
        // The backslash exclusion drops the right-hand side of a Files entry — the mod's own relative disk
        // path, JSON-escaped ("textures\\foo.tex") — which is never a game path and so never a base key.
        @"""([^""\\]+\.(?:tex|mtrl|mdl))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// One pass over a mod's manifests answering both questions at once, because they need the same
    /// bytes: does it touch a skin surface at all (<paramref name="Surface"/>), and exactly which game
    /// paths does it provide (<paramref name="Paths"/>).
    /// </summary>
    private static (bool Surface, HashSet<string> Paths) ScanModManifests(string modRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var surface = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!IsModManifestFile(Path.GetFileName(file))) continue;
                var text = File.ReadAllText(file);
                // Only until it is true: the answer cannot be un-set, and a multi-group mod would
                // otherwise pay a full regex pass per manifest for a result already known.
                if (!surface && BodyMaterialPattern.IsMatch(text)) surface = true;
                foreach (Match m in ManifestGamePathPattern.Matches(text))
                    paths.Add(m.Groups[1].Value);
            }
        }
        catch { /* modRoot missing/unreadable */ }
        return (surface, paths);
    }

    /// <summary>
    /// Classify a mod: does it touch a skin surface, and does it provide any of the base paths THIS
    /// composite reads. The second is the recomposite gate — see <see cref="EvaluateSurfaceModOffThread"/>
    /// for why the two must not be the same answer.
    /// </summary>
    private (bool Surface, bool Affects) ClassifySurfaceMod(string modDir)
    {
        // Proteus's own sidecar/overlay mods legitimately reference body materials too — that's
        // their redirect target, not a body-shape change — and they're already fully handled via
        // the HasSidecar-gated recompose path regardless of this flag. Exclude them here, or every
        // mask/color toggle on the user's own overlay mods would re-trigger the expensive walk this
        // whole mechanism exists to avoid.
        if (HasSidecar(modDir)) return (false, false);

        var modRoot = Path.Combine(modsRoot, modDir);
        var fingerprint = ComputeModFingerprint(modRoot);
        // One read of the pair, so the hash always describes the set the verdict is computed from.
        var bases   = _compositeBaseKeys;
        var version = bases?.Hash ?? 0;
        if (_bodyModCache.TryGetValue(modDir, out var cached)
            && cached.Fingerprint == fingerprint && cached.BaseKeysHash == version)
            return (cached.IsSurfaceMod, cached.AffectsComposite);

        var (surface, paths) = ScanModManifests(modRoot);

        // Fail open while we have no idea what we read — before this session's first composite, or if
        // the persisted set was lost. The old behaviour (any surface mod recomposites) is the safe
        // answer there, and it lasts exactly until one composite runs.
        var affects = bases is not { Paths.Count: > 0 } ? surface : paths.Overlaps(bases.Paths);

        _bodyModCache[modDir] = (surface, affects, version, fingerprint);
        lock (_bodyModConfigLock)
        {
            // Only pay the disk write when a VERDICT moved. config.Save serialises the whole
            // Configuration — every KnownBodyMods entry included — and a changed base set retires every
            // cached verdict at once, so Glamourer's zone-in re-assert storm would otherwise reclassify
            // dozens of mods and write the entire config once per mod. BaseKeysHash is deliberately not
            // part of "changed": a hash-only refresh costs one rescan next session if it is lost, which
            // is far cheaper than the writes.
            var stale = !config.KnownBodyMods.TryGetValue(modDir, out var prev)
                     || prev.IsBodyMod != surface
                     || prev.AffectsComposite != affects
                     || prev.Fingerprint != fingerprint;
            config.KnownBodyMods[modDir] = new BodyModCacheEntry
            {
                IsBodyMod        = surface,
                AffectsComposite = affects,
                BaseKeysHash     = version,
                Fingerprint      = fingerprint,
            };
            if (stale) config.Save();
        }
        return (surface, affects);
    }

    /// <summary>
    /// Record the base paths a composite reads, so <see cref="ClassifySurfaceMod"/> can tell a mod that
    /// feeds us from one that merely touches a skin surface.
    ///
    /// ACCUMULATES while the composite SHAPE holds, and is replaced outright when that shape changes.
    /// Both halves matter, for opposite reasons:
    ///
    /// Accumulating, because a run does not reliably see everything. <see cref="ResolveUpstream"/> records
    /// a path only when it resolves to something that is not our own output, so a composite running on a
    /// cold cache (any settings change clears it) legitimately reports FEWER paths than the warm run
    /// before it. Shrinking to that would be silent and lasting: the hash of this set keys every cached
    /// verdict, so a skin mod whose only base was among the dropped paths gets classified "affects
    /// nothing" and CACHED that way, and is then ignored until the set happens to move again.
    ///
    /// Replacing, because otherwise nothing ever retires. A face overlay enabled once would put the face
    /// surfaces in the set permanently, and every face mod would then force a full recomposite for as
    /// long as the config file survives — the exact symptom this whole mechanism exists to remove.
    /// <paramref name="signature"/> hashes the material paths the composite TARGETS, so it changes when
    /// overlays are added, removed or retargeted, and holds across the ordinary composites in between.
    ///
    /// Only an <paramref name="authoritative"/> record may retire paths. The pre-gate call knows just the
    /// published-manifest half, so letting it replace would shrink the set to that floor for the width of
    /// a composite — long enough for a concurrent classification to cache the very false negative the
    /// accumulate rule exists to prevent.
    /// </summary>
    private void RecordCompositeBaseKeys(IEnumerable<string> baseKeys, int signature, bool authoritative)
    {
        // The whole read-modify-write is under the lock the persisted copy is written under: two
        // overlapping composites could otherwise both compute a union against the same old set and race
        // the swap, losing one of their contributions and leaving config.CachedCompositeBaseKeys
        // describing neither.
        lock (_bodyModConfigLock)
        {
            var prev = _compositeBaseKeys;
            var next = new HashSet<string>(baseKeys, StringComparer.OrdinalIgnoreCase);

            // The one moment a path may be dropped: an authoritative record for a shape that is not the
            // one the stored set describes.
            var retire = authoritative && prev is not null && prev.Signature != signature;

            if (prev is not null && !retire)
            {
                next.UnionWith(prev.Paths);
                // A union can only equal the old count when it added nothing, so this is "no new paths".
                if (next.Count == prev.Paths.Count) return;
            }

            // A non-authoritative record leaves the stored signature alone — it has not seen enough of
            // the run to redefine the shape, only enough to add to it.
            var sig = prev is null || retire ? signature : prev.Signature;

            _compositeBaseKeys = new BaseKeySet(next, ComputeBaseKeysHash(next), sig);
            config.CachedCompositeBaseKeys = [.. next];
            config.CachedCompositeBaseSignature = sig;
            config.Save();
            log.Debug("[Proteus] composite base set {0}: {1} path(s), hash {2}, shape {3}",
                retire ? "replaced (composite shape changed)" : "now", next.Count,
                _compositeBaseKeys.Hash, sig);
        }
    }

    // Order-independent and case-insensitive, matching the set's own comparer, and stable across
    // sessions — string.GetHashCode is randomised per process, so it cannot be used here.
    private static int ComputeBaseKeysHash(IEnumerable<string> baseKeys)
    {
        var h = 17;
        var n = 0;
        foreach (var p in baseKeys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal))
        {
            foreach (var c in p)
                h = unchecked(h * 31 + c);
            // Terminator, or the concatenation alone collides: {"ab","c"} would hash as {"a","bc"}.
            h = unchecked(h * 31 + '\n');
            n++;
        }
        h = unchecked(h * 31 + n);
        // Never 0 — that is the "no set recorded" value a restored BodyModCacheEntry from a config
        // written before this field existed also carries, and the two must not compare equal.
        return h == 0 ? 1 : h;
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

        // Either direction changes what is published out from under the gate.
        _lastCompositeFingerprint = null;

        if (enabled)
        {
            // Off the framework thread, like the disable branch below. EnsureManagedModExists does file I/O
            // and can write the manifest, which now serialises on _manifestLock — and a composite priming
            // its upstream cache can hold that for a few hundred milliseconds. Blocking a frame on it would
            // be a visible hitch for no reason: nothing here needs to complete before the checkbox returns.
            Task.Run(() =>
            {
                try
                {
                    EnsureManagedModExists();
                    if (collId.HasValue)
                        penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
                    TriggerRecomposite("enabled");
                }
                catch (Exception ex) { log.Error(ex, "[Proteus] failed to enable cleanly"); }
            });
            return;
        }

        // Disabling stops all hosting, so pull our injected host items off the player's Glamourer state
        // (else they'd linger as a phantom bonus item and ring).
        RemoveInjectedGlasses();
        RemoveInjectedRing();

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
                // Nothing is hosted any more, so the redraw hook must not re-equip a carrier for a shell
                // that no longer exists.
                RememberHostDecision(gearWanted: false, shellBuilt: false, onFacewear: false, carrierSlots: []);
                if (restoreAccessory) _needFullRedraw = true;
                ReloadAndRedraw();   // character reverts to un-composited
                _secondSkinActive = false;
                ClearShellLocators();   // the shell is off the character; nothing left for them to describe

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
        // This exists for the case where a file changed in a way nothing can see; the fingerprint is one of
        // the things that can't see it, so drop it too. (The trigger below is forced anyway — belt and braces
        // for an escape hatch whose whole job is to bypass every optimisation.)
        _lastCompositeFingerprint = null;
        // And re-derive WHICH file each base path resolves to, not just re-decode the one we remembered.
        // Without this the button rebuilds from the same remembered upstream, so the one failure a user is
        // most likely to reach for it over — the composite standing on the wrong skin mod — was the one
        // thing it could not fix.
        InvalidateUpstreamCache("clear-texture-cache");
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
    /// <param name="force">
    /// True (the default) for anything the USER or a plugin explicitly asked for — a setting change, a
    /// design apply, an IPC call, a mod being installed. Such a composite always runs.
    ///
    /// False marks an AMBIENT trigger: something in the world moved and we are checking whether it mattered
    /// (zoning, a redraw, Glamourer re-asserting temporary settings). Those may be skipped when the
    /// composite's inputs are byte-identical to the last published one — see the gate in RecompositeBody.
    /// Defaulting to true means a new call site is safe by construction; opting out is the deliberate act.
    /// </param>
    /// <summary>
    /// Re-walk the draw object and refresh the five equipped-model maps the second skin builds from.
    /// Returns whether the maps are populated afterwards — false only when no walk has EVER succeeded,
    /// which is the state in which every host decision downstream is a guess.
    /// </summary>
    /// <remarks>
    /// Safe to call from a background thread: the draw-object IPC itself runs inside
    /// RunOnFrameworkThread. What must never happen is calling that IPC directly from off-thread.
    /// </remarks>
    private bool RefreshEquippedModels()
    {
        // Who the walk saw is captured INSIDE the framework call, with the paths themselves — this
        // method runs off-thread, so a later object-table read could name a different character
        // than the one these models came from, which is the very confusion being guarded against.
        var walk = Plugin.Framework.RunOnFrameworkThread(() =>
            (Paths: penumbra.GetActivePlayerModelPaths(),
             Owner: Plugin.ObjectTable.LocalPlayer?.Name.TextValue)).GetAwaiter().GetResult();
        return ApplyEquippedModels(walk.Paths, walk.Owner);
    }

    /// <summary>
    /// Publish one model walk into the five caches the second skin builds from. Returns whether the maps
    /// are populated afterwards.
    /// <para/>
    /// Split out from <see cref="RefreshEquippedModels"/> so <see cref="WaitForDrawStateToSettle"/> can
    /// apply the sample it ALREADY fetched rather than walking again. That is not just a saved IPC: the
    /// walk the composite builds from has to be the same reading the settle decision was made on, or the
    /// composite runs against a state nothing ever verified had stopped moving.
    /// </summary>
    private bool ApplyEquippedModels(HashSet<string>? equipped, string? owner)
    {
        // EMPTY counts as failure, not as "wearing nothing". A character that exists always draws
        // models — a face at the very least — so an empty set only ever means the walk caught the
        // draw object mid-teardown. Guarding on null alone let that through, and the damage is not
        // subtle: all five maps are wiped, so the shell is rebuilt from rebuilt-by-default paths.
        // Seen in a post-settle composite — the equipped heel (e6039, 2158v of posed foot) silently
        // became the bare foot model (e0000_sho, 6710v), the host list collapsed from four items to
        // the Emperor-ring fallback, and the invisible glasses were injected — all reported as a
        // perfectly successful build, because from here it looks exactly like a naked character.
        // The redraw hook has always documented this hazard; it just tested the wrong condition.
        if (equipped is { Count: > 0 })
        {
            _equippedPartModels = EquippedPartModelsFromModels(equipped);
            _equippedAccessoryModels = EquippedAccessoryModelsFromModels(equipped);
            _equippedMetModels = EquippedMetModelsFromModels(equipped, InvisibleGlasses.FacewearModelSets(Plugin.DataManager));
            _bareBodyModels = BareBodyModelsFromModels(equipped);
            _humanPartModels = HumanPartModelsFromModels(equipped);
            // Keep the last known race on a walk that carried no human model: it only changes on a
            // race change, which redraws, and "unknown" would send the shell back to charCode.
            // Bounded by the owner check, so "keep" never means "keep someone else's".
            UpdateDrawnRaceCode(equipped, owner);
        }
        return _equippedPartModels != null;
    }

    /// <param name="skinFingerprintAuthoritative">
    /// This trigger's effect on the SKIN composite is fully described by the skin fingerprint, so a forced
    /// run may still reuse the published skin when that fingerprint matches.
    /// <para/>
    /// <paramref name="force"/> exists because the fingerprint deliberately does not hash everything — most
    /// of all the config knobs (see BuildCompositeFingerprint's "standing requirement"). That makes it the
    /// right veto for the full skip gate, but far too broad for the skin-reuse one: EVERY editor interaction
    /// is forced, so skin reuse could never fire on the operation it was written for. A colour edit was
    /// costing a 2.6 s skin re-blend that wrote nothing, because the content hashes came out identical.
    /// <para/>
    /// Set it only where the claim is checkable in BuildCompositeFingerprint — colour-table rows are, at the
    /// `mtrl:`/`content:`/`maskrow:` blocks. It is NOT a licence to skip: reuse still requires the skin
    /// fingerprint to match, so an edit that genuinely moves a skin texel rebuilds regardless.
    /// </param>
    public void TriggerRecomposite(string reason, int delayMs = 200, bool force = true,
        bool skinFingerprintAuthoritative = false)
    {
        if (_disposed || !config.PluginEnabled || !penumbra.IsAvailable) return;
        Highlighter?.Clear();

        // A forced composite that gets cancelled by an ambient one must not be lost. Without this latch the
        // sequence "drag a colour slider (5s debounce), zone before it fires" cancels the user's edit and
        // replaces it with a composite that skips — and the colour silently never applies. The latch is
        // cleared only when a composite actually publishes.
        if (force)
        {
            Interlocked.Exchange(ref _forcePending, 1);
            // The skin half of the latch. Set only by a forced trigger whose skin effect the fingerprint
            // CANNOT see — an AO knob, skin suppression, compression. If such a run is cancelled by a
            // colour edit, the colour edit's composite must still rebuild the skin: it has no way to know
            // what the run it replaced was owed. Without splitting the latch, the single bit this trigger
            // sets for itself would veto its own reuse and the whole opt-in would be dead code.
            if (!skinFingerprintAuthoritative) Interlocked.Exchange(ref _skinForcePending, 1);
        }

        // Cancels whatever composite was pending and gives us the token for this one. The token is read
        // under the gate's own lock — doing it out here, off a source another thread can replace, is what
        // used to throw ObjectDisposedException out of the plugin constructor. See DebounceGate.
        var token = recompositeGate.Next();

        log.Debug("[Proteus] Recomposite triggered: {0} (delay {1}ms)", reason, delayMs);
        Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // FIRST, before anything below reads the draw object. Everything this lambda gathers — equipped
            // models, the drawn race code, enabled shape keys, the material snapshot — has to describe the
            // SAME character, and mid-race-change they don't: the walks would capture the outgoing race
            // while the wait settles the materials to the incoming one, and the composite would be built
            // from the mix. Worse, settling the materials is exactly what stops
            // SchedulePostRedrawBodyTypeCheck noticing the change, so the stale walks would never be
            // corrected the way they used to be.
            //
            // await, not GetResult() — and that is not a violation of the rule below. What that rule guards
            // against is a continuation resuming INLINE on the framework thread and dragging Recomposite
            // onto a frame with it. This task can only complete on a pool thread: its awaits are all
            // Task.Delay, and it blocks on GetResult() for the framework calls exactly as this lambda does.
            if (!await WaitForRaceToSettle(token).ConfigureAwait(false)) return;

            // ...and then for the DRAW OBJECT AS A WHOLE to stop moving. WaitForRaceToSettle above asks
            // "has the race the game is moving to arrived"; this asks the broader, blinder question "has
            // anything still got a foot in the air". A design apply lands its equipment over several frames
            // after the events that triggered us, so without this the composite below cuts its shell from
            // the PREVIOUS outfit and the post-redraw check has to rebuild everything.
            //
            // Its final sample replaces the three separate draw-object reads that used to live here — the
            // equipped-model walk, the body-shape read and the material walk — so this is cheaper than what
            // it displaces, not dearer, and all three now describe the same frame.
            var settled = await WaitForDrawStateToSettle(token).ConfigureAwait(false);
            if (settled is not { } state) return;

            // Publish the equipped gear models the second skin sources its shells from, EVERY composite.
            // Unlike the material snapshot this can't be gated on cold/dirty: equipping an item fires no
            // mod/collection event, and a composite can be triggered while no redraw has repopulated the
            // cache (e.g. right after load, when the first walk hit a not-yet-ready player and returned
            // null). Applied from the SETTLED sample rather than a fresh walk — re-walking here would
            // reopen exactly the gap the loop just closed. Keep the last value on a transient null so a
            // mid-reload blank draw object doesn't wipe a good set.
            ApplyEquippedModels(state.Models, state.Owner);

            // Which shape keys the game has enabled on each drawn body model (e.g. "Remove Hip Dips").
            // Read EVERY composite, for the same reason the equipped-models walk is: a mod toggle changes
            // the enabled shapes but fires no redraw when Proteus uses the in-place reload, so the redraw
            // hook is unreliable.
            //
            // Only published from a USABLE sample. BodyShapeReader returns an EMPTY MAP, not null, when the
            // player isn't drawable — so on the settle loop's loading-screen bail-out this would otherwise
            // publish "no shapes enabled" as though the user had just switched them all off. The shell would
            // then rebuild without their morphs, and the emptied signature would move the composite
            // fingerprint enough to stop the unchanged-inputs gate from skipping the run. Guarding on null
            // alone was not enough, which is the same lesson ApplyEquippedModels documents for its own walk.
            if (state.IsUsable && state.Shapes != null)
            {
                _bodyShapeSnapshot = state.Shapes;
                foreach (var (path, names) in state.Shapes)
                    log.Debug("[Proteus] body shapes enabled: {0} -> [{1}]",
                        SanitizeName(path), string.Join(", ", names));
            }

            // OnLocalPlayerRedrawn only fires when the draw object is recreated, but some mods (e.g.
            // body replacers that redirect an always-loaded "smallclothes" resource in place) change
            // which materials are active WITHOUT a redraw. GetActivePlayerMaterialPaths must run on
            // the framework thread (it walks the draw object); it's cheap (~2-8ms), but we still only
            // pay for it when the cache is cold or ClassifySurfaceMod flagged it dirty (see OnModSettingChanged/
            // OnModAdded/OnModDeleted/OnPlayerCollectionChanged), not on every mask/color toggle.
            if (_activeMtrlSnapshot == null || _activeMtrlSnapshotDirty)
            {
                bool wasDirty = _activeMtrlSnapshotDirty;
                log.Debug("[Proteus] Refreshing active-material snapshot (cold={0}, dirty={1})",
                    _activeMtrlSnapshot == null, wasDirty);

                // The settle loop already walked this, on the same visit that produced the models and
                // shapes above — so there is nothing left to fetch here. (The walks below, inside the
                // body-type settle loop, still use GetResult() rather than await: RunOnFrameworkThread's
                // task COMPLETES on the framework thread, and an await continuation would run INLINE on
                // that completing thread — ConfigureAwait(false) doesn't prevent this, it only suppresses
                // returning to a captured context. That would hop the rest of this lambda, including
                // Recomposite, whose Parallel.ForEach uses its calling thread as a worker, onto the
                // framework thread and freeze the game for the whole multi-second composite.)
                HashSet<string>? fresh = state.Materials;

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
                // Empty is a mid-teardown walk, not a character with no materials — same rule as the model
                // walk above, and the same damage: publishing it as the snapshot makes every material look
                // unloaded, so overlays are dropped as "non-equipped" and the dirty flag is cleared as
                // though the answer were trustworthy. Leaving the previous snapshot in place keeps the flag
                // set, so the next composite retries instead of settling on nothing.
                if (fresh is { Count: > 0 })
                {
                    _activeMtrlSnapshot = fresh;
                    _activeMtrlSnapshotDirty = false;
                    // Under the lock like every other off-thread save: Save() serializes the WHOLE
                    // Configuration, so an unsynchronized one here can throw "collection was modified"
                    // while ClassifySurfaceMod mutates KnownBodyMods on its own thread. Nothing catches that, and
                    // Recomposite is below — so the composite this trigger exists to run would be dropped
                    // outright, with no log line to say it happened.
                    lock (_bodyModConfigLock)
                    {
                        config.CachedActiveMaterialPaths = fresh.ToList();
                        config.Save();
                    }
                }
            }
            Recomposite(token, force, skinFingerprintAuthoritative);
        });
    }

    /// <summary>
    /// A race change costs TWO full composites unless this waits.
    ///
    /// The active-material snapshot can be perfectly CLEAN and still be the old race's: OnLocalPlayerRedrawn
    /// stamps it (and clears the dirty flag) the moment the draw object is recreated, which on a race change
    /// is before the new race's body/face materials have loaded. Nothing downstream can tell that apart from
    /// a settled snapshot, so the composite runs on the old race, publishes, redraws — and then
    /// SchedulePostRedrawBodyTypeCheck sees the char code move and rebuilds the whole thing from scratch.
    /// Two composites, two redraws, several seconds, for one race change.
    ///
    /// Glamourer's displayed char code is read on the framework thread the instant the customize event fires,
    /// so it is the authority on which race is COMING. When the snapshot's body materials don't mention it,
    /// the snapshot demonstrably hasn't settled — so poll for it rather than compositing something we already
    /// know is about to be thrown away. Same shape as the body-type settle loop in the caller, and
    /// SchedulePostRedrawBodyTypeCheck stays the backstop for a load slower than this window.
    ///
    /// Runs FIRST in the caller, ahead of the draw-object walks, so those describe the settled character
    /// too — see the call site for why a half-settled composite would go uncorrected.
    /// </summary>
    /// <returns>False only if the wait was cancelled — the caller must not composite on a dead token.</returns>
    private async Task<bool> WaitForRaceToSettle(CancellationToken token)
    {
        var glamCode = _glamourerCharCode;
        var snapshot = _activeMtrlSnapshot;
        if (glamCode == null || snapshot == null) return true;

        var codes = CharCodeSet(snapshot);
        // No body materials at all means there is nothing to disagree with — not a stale snapshot.
        if (codes.Count == 0 || codes.Contains(glamCode)) { _unsettledRace = null; return true; }

        // Glamourer can display a race the draw object genuinely never adopts, in which case the snapshot
        // never catches up and every composite from here on would pay the full timeout. Take a pair we have
        // already waited out at its word — until the memo expires, since a slow load looks the same.
        var codeKey = CharCodeKey(codes)!;   // non-null: the empty case returned above
        var pair = $"{glamCode}|{codeKey}";
        var memo = _unsettledRace;
        if (memo != null && memo.Pair == pair && Environment.TickCount64 < memo.ExpiresAtTick) return true;

        log.Debug("[Proteus] snapshot is mid-race-change (snapshot={0}, Glamourer displays {1}) — "
                + "waiting for the new race's materials before compositing", codeKey, glamCode);

        // Read BEFORE the wait. Anything that arrives DURING it and marks the snapshot dirty is talking
        // about ITS OWN materials, which this walk knows nothing about — see the publish below.
        bool wasDirty = _activeMtrlSnapshotDirty;

        int consecutiveNulls = 0;
        for (int i = 0; i < 12; i++) // up to ~3s, then composite anyway (the post-settle check covers misses)
        {
            // Teardown gets the same treatment as cancellation, and needs saying out loud because this wait
            // is the one place the lambda can sit for seconds: the plugin can be unloaded mid-poll, and then
            // the framework call raises ObjectDisposedException (or an unloading load context, which the CLR
            // hands over as a bare InvalidOperationException). Neither is an OperationCanceledException, so
            // without these they escape the async lambda and fault a task nobody awaits.
            try { await Task.Delay(250, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return false; }

            HashSet<string>? next;
            try { next = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return false; }

            // A null walk means there is no drawable player right now — and that is AMBIGUOUS, in a way worth
            // being careful about. A race change IS the draw object being destroyed and recreated (see the
            // null-guard in OnLocalPlayerRedrawn), so a null here is the single most likely observation during
            // exactly the window this wait exists for. Bailing on the first one would make the feature a
            // no-op for its primary case. But a loading screen or a cutscene is also null, and staying for
            // the full 3s there is the stall this budget exists to avoid — so tolerate a redraw-sized gap
            // (~1s) and give up only once it looks persistent. No memo either way: a null run learned
            // nothing about whether this pair can settle.
            if (next == null)
            {
                if (++consecutiveNulls >= 4) return true;
                continue;
            }
            consecutiveNulls = 0;

            // Scanned rather than CharCodeSet(next).Contains(...): the question is one bit, and building a
            // whole set per poll to read it back is the loop describing itself badly.
            if (!next.Any(m => UVRemapService.InferBodyType(m) != null
                            && glamCode.Equals(ExtractHumanCharCode(m), StringComparison.OrdinalIgnoreCase)))
                continue;

            // Settled — publish so the caller's composite builds straight from the new race.
            //
            // The token check is the same one the body-type settle loop makes before ITS publish: a trigger
            // that superseded us has already cancelled this token, and writing the snapshot after that point
            // hands the run that replaced us a stale one it will then trust.
            if (token.IsCancellationRequested) return false;
            _activeMtrlSnapshot = next;
            // Only clear dirty if it was clear when we started. If something set it mid-wait, its materials
            // may still be loading — this walk is no evidence they arrived, and clearing the flag would make
            // the next trigger skip the refresh that exists to catch exactly that.
            if (!wasDirty) _activeMtrlSnapshotDirty = false;
            // No lock, unlike the body-type settle loop — which is written above this method but RUNS after
            // it, since this wait goes first in the caller. That one holds _bodyModConfigLock for the
            // config.Save() beside it, which serializes a whole collection while another thread may be
            // mutating it. This is a bare atomic reference swap with no Save, so taking the lock here would
            // only imply a hazard that isn't present.
            config.CachedActiveMaterialPaths = next.ToList();
            _unsettledRace = null;
            log.Debug("[Proteus] race settled to {0} after {1}ms — compositing once", glamCode, (i + 1) * 250);
            return true;
        }

        // The full window elapsed with the race never adopted, so this is most likely a real display
        // override rather than a slow load — but only most likely, hence the expiry on the memo.
        _unsettledRace = new UnsettledRace(pair, Environment.TickCount64 + UnsettledRaceMemoMs);
        log.Debug("[Proteus] race never settled to {0} within 3s — compositing on the snapshot as-is", glamCode);
        return true;
    }

    /// <summary>
    /// One framework-thread reading of every draw-object fact a composite is built from, gathered in a
    /// single visit because the reads must describe the SAME frame — comparing walks that straddle two
    /// frames is diffing noise, which is fatal to a settle loop.
    /// </summary>
    private readonly record struct DrawSample(
        HashSet<string>? Models,     // .mdl game paths  — the second skin's shell sources
        HashSet<string>? Materials,  // .mtrl game paths — body type and char code
        string? Owner,               // captured in the same visit; see ApplyEquippedModels
        IReadOnlyDictionary<string, HashSet<string>>? Shapes)
    {
        /// <summary>Null or empty is a teardown / loading-screen walk, not a character wearing nothing —
        /// the same rule every other consumer of these walks applies.</summary>
        public bool IsUsable => Models is { Count: > 0 } && Materials is { Count: > 0 };
    }

    private const int SettlePollMs        = 100;
    private const int SettleStableSamples = 2;      // two consecutive identical readings
    private const int SettleCapMs         = 1500;
    private const int SettleMaxBlankPolls = 5;      // ~500ms of blank walks → loading screen, stop waiting

    /// <summary>
    /// One framework visit for all three draw-object reads the preamble needs.
    /// <para/>
    /// A body-shape failure degrades to "shapes unknown" rather than taking the sample down with it — it
    /// is the least important of the three, and the caller it replaced already swallowed that exception.
    /// </summary>
    private DrawSample SampleDrawState()
        => Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            var (models, materials) = penumbra.GetActivePlayerResourcePaths();
            IReadOnlyDictionary<string, HashSet<string>>? shapes = null;
            try { shapes = Interop.BodyShapeReader.ReadEnabledShapes(player?.Address ?? 0); }
            catch (Exception ex) { log.Warning("[Proteus] body-shape read failed: {0}", ex.Message); }
            return new DrawSample(models, materials, player?.Name.TextValue, shapes);
        }).GetAwaiter().GetResult();

    /// <summary>
    /// Order-independent signature of everything a composite's SHAPE depends on, built purely from a
    /// sample already in hand — no IPC, so it is safe to compute every poll.
    /// <para/>
    /// <see cref="EquipSignature"/> rather than the raw model set, and that is load-bearing: it is the
    /// same builder the redraw hook diffs and the fingerprint hashes, and it EXCLUDES our own injected
    /// carriers. Without that exclusion an ApplyFlag.Once ring or glasses pair flapping mid-poll would
    /// never let the reading stand still, and every composite would pay the full cap for nothing.
    /// </summary>
    private string DrawStateSignature(in DrawSample s)
        => string.Join('\n',
            EquipSignature(
                EquippedPartModelsFromModels(s.Models!),
                EquippedAccessoryModelsFromModels(s.Models!),
                EquippedMetModelsFromModels(s.Models!, InvisibleGlasses.FacewearModelSets(Plugin.DataManager)),
                BareBodyModelsFromModels(s.Models!),
                HumanPartModelsFromModels(s.Models!)),
            BodyTypeKey(s.Materials!)              ?? "-",
            CharCodeKey(CharCodeSet(s.Materials!)) ?? "-",
            BodyShapeSignature(s.Shapes));

    /// <summary>
    /// A Glamourer DESIGN apply costs TWO full composites unless this waits.
    ///
    /// A design's equipment lands piecemeal over several frames, AFTER the mod-setting events that
    /// triggered us — and nothing announces "the design has finished applying". The 200ms debounce is
    /// nowhere near enough: measured, the first composite cut a second-skin shell against the outfit the
    /// character wore BEFORE the design (top=e0666/dwn=e6058/sho=e6116, 4.5s of work thrown away), then
    /// SchedulePostRedrawBodyTypeCheck rebuilt the lot — including a 9s skin decode+blend that produced
    /// byte-identical textures. ~29s for one design apply.
    ///
    /// So poll the draw object and wait for the reading to stop moving. TWO consecutive identical samples,
    /// not one: a single match lands easily in the gap between two items arriving.
    ///
    /// Cheap by construction. Each poll is ONE framework visit and ONE IPC — fewer than the three reads
    /// this replaces in the caller — so a settled character pays less than it used to, and only a character
    /// genuinely in motion pays the cap.
    ///
    /// Runs AFTER <see cref="WaitForRaceToSettle"/>, which answers a different question with different
    /// evidence: it waits for a SPECIFIC race Glamourer has declared, over 3s, tolerating the ~1s of null
    /// walks a race change necessarily produces. This loop cannot do that job — during a race change the
    /// draw object is destroyed, samples go blank, and stability can never be reached.
    ///
    /// Mutates nothing. That is what makes cancellation free: a superseded run leaves no trace, and the run
    /// that replaced it starts its own settle from scratch.
    /// </summary>
    /// <returns>The settled sample, or null when cancelled or torn down — the caller must not composite
    /// on a dead token.</returns>
    private async Task<DrawSample?> WaitForDrawStateToSettle(CancellationToken token)
    {
        var started = Environment.TickCount64;
        DrawSample sample = default;
        string? prevSig = null, prevOwner = null;
        int agreements = 0, blanks = 0, polls = 0;

        while (true)
        {
            // Teardown gets the same treatment as cancellation, for the reason WaitForRaceToSettle spells
            // out: this loop can sit for over a second, and an unloading load context arrives as a bare
            // InvalidOperationException that would otherwise fault a task nobody awaits.
            try { sample = SampleDrawState(); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return null; }
            if (token.IsCancellationRequested) return null;
            polls++;

            if (!sample.IsUsable)
            {
                // Two blank walks must never AGREE with each other and settle the composite onto nothing.
                agreements = 0;
                prevSig = null;
                // A redraw-sized gap is normal; a loading screen or cutscene is not worth the full cap.
                // Returning the blank sample reproduces today's behaviour exactly — every consumer below
                // already keeps its previous value on an empty walk.
                if (++blanks >= SettleMaxBlankPolls) return sample;
            }
            else
            {
                blanks = 0;
                var sig = DrawStateSignature(sample);
                // Owner too: a walk that landed on a different character is not evidence about this one.
                bool sameOwner = string.Equals(sample.Owner, prevOwner, StringComparison.Ordinal);
                agreements = sameOwner && string.Equals(sig, prevSig, StringComparison.Ordinal) ? agreements + 1 : 1;
                prevSig = sig;
                prevOwner = sample.Owner;
                if (agreements >= SettleStableSamples)
                {
                    // Only worth a line when something actually was in flight — otherwise every colour
                    // slider drag logs a settle that had nothing to settle.
                    if (polls > SettleStableSamples)
                        log.Debug("[Proteus] draw state settled after {0}ms ({1} polls) — compositing once",
                                  Environment.TickCount64 - started, polls);
                    return sample;
                }
            }

            if (Environment.TickCount64 - started >= SettleCapMs)
            {
                log.Debug("[Proteus] draw state still moving after {0}ms — compositing on the latest reading "
                        + "(the post-redraw check remains the backstop)", Environment.TickCount64 - started);
                return sample;
            }

            try { await Task.Delay(SettlePollMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return null; }
        }
    }

    /// <summary>
    /// Set by any forced <see cref="TriggerRecomposite"/>, cleared only once a composite publishes. While
    /// it is set, even an ambient composite runs — the forced work it superseded is still owed.
    /// </summary>
    private int _forcePending;

    /// <summary>
    /// The subset of <see cref="_forcePending"/> that the SKIN reuse gate must honour: a forced trigger
    /// whose effect on the skin the fingerprint cannot see. Cleared with <see cref="_forcePending"/>, at
    /// the same publish.
    /// </summary>
    private int _skinForcePending;

    /// <summary>
    /// The composite inputs behind the manifest that is currently published, or null when that is unknown
    /// (nothing composited yet, the last one failed, or something happened that could have moved the world
    /// under us). An ambient trigger whose inputs hash to this can skip: the composite would rewrite the
    /// same bytes and redraw the character for nothing.
    ///
    /// Written only after a successful publish, so a cancelled or throwing run can never leave a fingerprint
    /// claiming output that was never produced. Two overlapping composites are last-writer-wins; a stale run
    /// finishing second costs one extra composite later, which is not worth a lock.
    /// </summary>
    private volatile string? _lastCompositeFingerprint;

    /// <summary>
    /// The SKIN half of one published composite's inputs, and everything the skin phase produced from
    /// them: the fingerprint that identifies the inputs, and the three outputs a later run has to restore
    /// if it skips the phase.
    /// <para/>
    /// The skin phase does not merely compute pixels. It fills the redirect map, the colour-table editor's
    /// glow targets and the contributions panel — so reusing it means reinstating all three, not just the
    /// textures. Reinstating only the redirects leaves the panel blank and the Glow button inert on a
    /// composite that changed nothing about the skin.
    /// <para/>
    /// ONE immutable record rather than separate fields, and that is the point of the type. Composites
    /// overlap (see <see cref="_compositesInFlight"/>), so independent writes can be observed
    /// half-applied: a reader picking up the NEW fingerprint beside the OLD redirects would carry the
    /// previous skin's textures forward under a fingerprint claiming they are current, and publish a
    /// manifest pointing at the pre-edit skin. The existence check cannot catch that — those files are
    /// still on disk until the next composite prunes them. Grouping them behind one reference makes the
    /// set indivisible: a reader sees the whole publish or none of it.
    /// </summary>
    private sealed record SkinPublish(
        string Fingerprint,
        IReadOnlyDictionary<string, string> Redirects,
        IReadOnlyList<ChannelContribution> Contributions,
        IReadOnlyDictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>> GlowTargets);

    /// <summary>
    /// The last successfully published <see cref="SkinPublish"/>, or null before the first one.
    /// <para/>
    /// This is what lets an outfit change reuse the skin outright. The unchanged-inputs gate above is
    /// all-or-nothing, so swapping a top re-ran the entire skin decode+blend — measured at 2.5s of a 3.6s
    /// composite — to produce byte-identical textures, purely because the equipped-item signature sits in
    /// the same fingerprint as the overlays.
    /// <para/>
    /// Written only on a successful publish, and trusted only when the files it names are still on disk.
    /// A redirect carried forward to a file that no longer exists is the "invisible body" failure:
    /// Penumbra drops the dangling path, Bibo's invented texture paths have nothing behind them, and the
    /// material fails to load. Hence the existence check rather than trust — see
    /// <see cref="SkinOutputStillOnDisk"/>.
    /// </summary>
    private volatile SkinPublish? _lastSkinPublish;

    /// <summary>
    /// Whether every texture a remembered skin publish points at is still on disk AND intact.
    /// <para/>
    /// <see cref="AlreadyWritten"/> rather than File.Exists: it validates the length against the .tex
    /// header's own dimensions, so a write interrupted by a crash or antivirus reads as absent instead of
    /// being re-approved forever under a name that promises content it does not have.
    /// </summary>
    private bool SkinOutputStillOnDisk(IReadOnlyDictionary<string, string> skinRedirects)
    {
        foreach (var rel in skinRedirects.Values)
        {
            var disk = Path.Combine(managedModDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!AlreadyWritten(disk)) return false;
        }
        return true;
    }

    /// <summary>
    /// Everything that decides what this composite will produce, as one comparable string. Built at the
    /// point where the inputs are settled but before any expensive work, so an ambient trigger whose world
    /// hasn't moved can return without paying for a composite.
    ///
    /// What is IN, and why each is not redundant:
    ///   • the overlays in composite order, with their descriptors and colour rows — this one component
    ///     subsumes the enabled mod set, Penumbra priorities, option selections, all three design-binding
    ///     overrides, skin-to-gear promotion, the user's tab-strip stack order, and metadata.json content;
    ///   • the gear overlays, the mask paths/assets/colour rows, and the mask-shell set — a mask lives
    ///     outside the overlay lists and changes coverage, relief and colour;
    ///   • the material set, AFTER filtering and sibling synthesis — which bodies actually get written;
    ///   • the equip signature, body shape, body types, char codes and race — the shell's geometry.
    ///
    /// What is deliberately OUT:
    ///   • the raw active-material snapshot. It contains weapon paths, which change on draw/sheathe and on
    ///     every zone-in, so hashing it would make the fingerprint differ almost every time and the gate
    ///     would never fire. Its two composite-relevant projections (the body-type/char-code keys, and the
    ///     filtered material set) are in.
    ///   • Configuration values. Every knob is reachable only through a FORCED trigger, and _forcePending
    ///     covers the case where an ambient trigger cancels one. THIS IS A STANDING REQUIREMENT: a new
    ///     config knob that affects output must trigger with force: true, or its change will be skipped.
    ///     It must also NOT pass skinFingerprintAuthoritative — that flag is the assertion that this list
    ///     already covers the trigger's effect on the skin, which for a config knob is exactly false.
    /// </summary>
    private string BuildCompositeFingerprint(
        Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial,
        List<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        Dictionary<string, List<string>> maskPathsByMod,
        Dictionary<string, List<(string MaskPath, string? NormalPath, string? IndexPath)>> maskAssetsByMod,
        Dictionary<string, Dictionary<int, ColorTableRowOverride>> maskRowsByMod,
        Dictionary<string, OverlayDescriptor> maskDescByMod,
        HashSet<string> maskShellMods,
        List<string> baseKeys,
        List<(OverlayEntry Entry, ResolvedContent Content)> contentLayers,
        bool skinOnly = false)
    {
        var sb = new System.Text.StringBuilder();

        static void Pair(System.Text.StringBuilder b, OverlayEntry e, ResolvedOverlay o)
            => b.Append(e.ModDirectory).Append('#').Append(e.Priority).Append('#')
               .Append(o.OptionGroup).Append('/').Append(o.Option).Append('#').Append(o.GroupOrder).Append('#')
               .Append(JsonSerializer.Serialize(o.Descriptor)).Append('#')
               .Append(o.ColorTableRows == null ? "-" : JsonSerializer.Serialize(o.ColorTableRows)).Append(';');

        // Material order and, within a material, composite order — both are output-affecting, so iterate
        // sorted by key but NOT sorted within a list: the list order IS the stack.
        foreach (var mtrl in byMaterial.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("mtrl:").Append(mtrl).Append('{');
            foreach (var (e, o) in byMaterial[mtrl]) Pair(sb, e, o);
            sb.Append("}\n");
        }

        sb.Append("gear:");
        foreach (var (e, o) in gearOverlays) Pair(sb, e, o);
        sb.Append('\n');

        // Imported geometry, in the order it will be placed. Without this a change of selection in a
        // content pack — a different piercing, a piece switched off — hashes identically to the
        // composite already published and is skipped, and the character keeps wearing the old one.
        //
        // Shell-only: content pieces are meshes grafted onto the shell and never touch a skin texture.
        if (!skinOnly)
        {
            sb.Append("content:");
            foreach (var (e, c) in contentLayers)
                sb.Append(e.ModDirectory).Append('#').Append(e.Priority).Append('#')
                  .Append(c.OptionGroup).Append('/').Append(c.Option).Append('#').Append(c.GroupOrder).Append('#')
                  .Append(JsonSerializer.Serialize(c.Piece)).Append('#')
                  .Append(c.ColorTableRows == null ? "-" : JsonSerializer.Serialize(c.ColorTableRows)).Append(';');
            sb.Append('\n');
        }

        foreach (var mod in maskPathsByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            sb.Append("mask:").Append(mod).Append('=')
              .Append(string.Join(",", maskPathsByMod[mod])).Append('\n');

        foreach (var mod in maskAssetsByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            sb.Append("maskasset:").Append(mod).Append('=')
              .Append(string.Join(",", maskAssetsByMod[mod].Select(a => $"{a.MaskPath}|{a.NormalPath}|{a.IndexPath}")))
              .Append('\n');

        // Shell-only for a mask-SHELL mod: its Masks colorset paints the shell's own material and nothing
        // else. Every skin consumer of maskRowsByMod already refuses those mods by name — the fallback-rows
        // build, LoadIndexMerged's _id merge, and the skin mask-colour pass all lead with
        // `maskShellMods.Contains(modDir) ⇒ skip` — so the rows provably cannot move a skin texel.
        //
        // Leaving them in the skin fingerprint cost a full skin re-blend on every mask colour tweak, which
        // then wrote nothing because the content hashes came out identical: 2.6 s of the 3.8 s a one-row
        // colour change took, measured. The mods NOT on a shell keep their rows here — there the mask really
        // is painted into the skin diffuse.
        //
        // Safe against the transition in both directions because `maskshell:` below is in the skin
        // fingerprint too: a mod entering or leaving the set changes it, and the skin rebuilds.
        foreach (var mod in maskRowsByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (skinOnly && maskShellMods.Contains(mod)) continue;
            sb.Append("maskrow:").Append(mod).Append('=')
              .Append(JsonSerializer.Serialize(maskRowsByMod[mod].OrderBy(kv => kv.Key)
                          .ToDictionary(kv => kv.Key, kv => kv.Value)))
              .Append('\n');
        }

        // The Masks tab's own render mode — layer, shader, and the scroll effect with its speed and tiling.
        // Nothing else hashed it: `maskrow:` carries the colours and `maskshell:` only set membership, so
        // switching a mask's glow effect (or its scroll speed) produced an IDENTICAL fingerprint and worked
        // solely because mask-mode-change happens to trigger with force: true. That is the "standing
        // requirement" hazard above, one line away from a flag whose whole meaning is "the fingerprint
        // covers this" — so hash it instead of relying on a caller staying forced.
        //
        // Shell-only, on the same argument as `content:` and the mask rows: the descriptor reaches the skin
        // ONLY by deciding maskShellMods membership, which `maskshell:` hashes for both fingerprints. Its
        // shader and scroll describe a surface the skin composite never touches, so folding them into the
        // skin fingerprint would re-blend the body for a glow-effect swap that cannot move a skin texel.
        if (!skinOnly)
            foreach (var mod in maskDescByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                sb.Append("maskdesc:").Append(mod).Append('=')
                  .Append(JsonSerializer.Serialize(maskDescByMod[mod])).Append('\n');

        sb.Append("maskshell:")
          .Append(string.Join(",", maskShellMods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))).Append('\n');

        // Recomputed here, NOT read from _lastEquipSignature: that field is only written by the redraw hook,
        // so it is stale for every trigger that didn't come from a redraw — while TriggerRecomposite's
        // preamble has just refreshed the four maps it is built from.
        //
        // Under skinOnly the GEAR-side half is dropped and only the bare-body models are kept. That is the
        // whole point of the split: swapping a top changes which equipment models are drawn, but not one
        // texel of the skin composite. What must NOT be dropped is the bare-body half — which body parts
        // are drawn depends on what the gear hides, and those are the meshes the seam map is keyed on, so a
        // shoe that replaces the bare foot genuinely does change the skin's UV topology.
        //
        // `gear:` above stays in either way: gear OVERLAYS are Proteus mods, and their coverage drives the
        // skin's ambient-occlusion pass. Only the equipped FFXIV items are gear-side.
        sb.Append("equip:")
          .Append(skinOnly
              ? EquipSignature(null, null, null, _bareBodyModels)
              : EquipSignature(_equippedPartModels, _equippedAccessoryModels, _equippedMetModels, _bareBodyModels))
          .Append('\n');
        sb.Append("shape:").Append(BodyShapeSignature(_bodyShapeSnapshot)).Append('\n');
        sb.Append("bodytype:").Append(_lastCompositedBodyType).Append('\n');
        sb.Append("charcodes:").Append(_lastCompositedCharCodes).Append('\n');
        sb.Append("glamcode:").Append(_glamourerCharCode).Append('\n');
        sb.Append("race:").Append(_drawnRaceCode).Append('\n');

        // WHICH FILE each base path actually resolves to, plus its size and mtime. The overlay entries above
        // describe what we paint; this describes what we paint ON TOP OF. Without it, switching body mods,
        // reinstalling one, or a design selecting a different body option would all hash identically to the
        // composite already published and be skipped — the output would silently keep the old base.
        //
        // baseKeys comes from PrimeUpstreamCache, which has just resolved every one of them. It is a
        // function of this composite's inputs alone — deliberately NOT _upstreamByGamePath's key set, which
        // grows as the blend loop resolves textures and is emptied wholesale on any non-sidecar mod change,
        // so hashing that would give a different key set before and after every zone-in and the gate would
        // alternate between two shapes forever instead of settling.
        foreach (var gamePath in baseKeys)
        {
            sb.Append("base:").Append(gamePath).Append('=')
              .Append(_upstreamByGamePath.TryGetValue(gamePath, out var disk) ? disk : "(game data)");
            if (disk == null) { sb.Append('\n'); continue; }
            try
            {
                var fi = new FileInfo(disk);
                if (fi.Exists) sb.Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks);
            }
            catch { /* unreadable — the path alone still distinguishes a different mod */ }
            sb.Append('\n');
        }

        return sb.ToString();
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

    // The character's own HUMAN part models — face, hair, tail, Viera ears. These arrive in the same walk as
    // everything above (GetActivePlayerModelPaths filters on nothing but the .mdl extension) and have always
    // been thrown away here: they match none of the three regexes above, so the only thing ever read off them
    // was five characters of race code in DrawnRaceCodeFromModels.
    //
    // A face draws SEVERAL models — _fac beside _iri, _etc and a race's extras — so this is a list, not a
    // by-slot map. Which of them a given overlay wants is decided by the material it targets, not by the slot.
    private static readonly System.Text.RegularExpressions.Regex HumanPartModelRe = new(
        @"chara/human/c\d+/obj/(face|hair|tail|zear)/[fhtz]\d+/model/c\d+[fhtz]\d+_\w+\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<string> HumanPartModelsFromModels(HashSet<string>? modelPaths)
    {
        var parts = new List<string>();
        if (modelPaths == null) return parts;
        foreach (var p in modelPaths)
            if (HumanPartModelRe.IsMatch(p)) parts.Add(p);
        parts.Sort(StringComparer.OrdinalIgnoreCase);   // stable order, same reason as the met list
        return parts;
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

    // The exact opposite selection: the BARE-BODY (e0000) part models the character is drawing where no gear
    // covers the slot, e.g. chara/equipment/e0000/model/c0201e0000_top.mdl → top. The shell needs these for
    // the same reason it needs the gear models — cut from what the game actually draws, never from a path
    // guessed at. Guessing is what broke a naked Au Ra female: equipment is keyed to a MODEL race, she draws
    // Midlander c0201 e0000 parts, and with nothing equipped there was no other resolved path to read that
    // off, so the shell asked for c1401e0000_* (shipped by no one) and came out empty.
    // The character's REAL race code, read off any chara/human/… model the draw object has loaded — the
    // face is always one of them. charCode cannot answer this: BodyCodeFromCustomize collapses Elezen,
    // Miqo'te, Roegadyn and Lalafell females onto c0201 because that is the BODY they share, so it says
    // 0201 for a c0801 Miqo'te. The second skin needs the real one to name the race whose metadata entry
    // must be emptied for the shell to fall through into cut space. Any human model will do — they all
    // carry the same code — and only .mdl paths reach here, so a c0201 body MATERIAL can't be mistaken
    // for one.
    /// <summary>
    /// Record the race code a model walk saw, keeping the last known one when the walk carried no human
    /// model — but only while it still belongs to the same character.
    /// <para/>
    /// <paramref name="owner"/> must be read on the FRAMEWORK THREAD, in the same walk that produced
    /// <paramref name="modelPaths"/>; pass it in rather than reading the object table here, because one
    /// caller updates these caches off-thread from a walk it marshalled separately.
    /// <para/>
    /// A changed owner takes the new reading even when it is null. Keeping a value across a switch is the
    /// bug this exists to close: the code is sticky by design, so before this it could only be replaced,
    /// never dropped, and a character swap left the old race in place until the plugin reloaded.
    /// </summary>
    private void UpdateDrawnRaceCode(HashSet<string>? modelPaths, string? owner)
    {
        var code = DrawnRaceCodeFromModels(modelPaths);
        // Read-decide-write over both fields, so it runs under the lock as one step. See _drawnRaceLock.
        lock (_drawnRaceLock)
        {
            // Null owner (draw object gone mid-switch) counts as "not the same character": that is
            // precisely the window where the cached code is least trustworthy, so it is dropped not held.
            if (owner == null || !string.Equals(owner, _drawnRaceOwner, StringComparison.Ordinal))
            {
                if (_drawnRaceCode != null && code == null)
                    Plugin.Log.Information("[Proteus] drawn race code c{0} dropped — read off {1}, now {2}",
                        _drawnRaceCode, _drawnRaceOwner ?? "(nobody)", owner ?? "(nobody)");
                _drawnRaceCode  = code;
                _drawnRaceOwner = owner;
                return;
            }
            _drawnRaceCode = code ?? _drawnRaceCode;
        }
    }

    private static string? DrawnRaceCodeFromModels(HashSet<string>? modelPaths)
    {
        if (modelPaths == null) return null;
        foreach (var p in modelPaths)
            if (ExtractHumanCharCode(p) is { Length: 5 } code && (code[0] == 'c' || code[0] == 'C'))
                return code[1..];
        return null;
    }

    private static Dictionary<string, string> BareBodyModelsFromModels(HashSet<string>? modelPaths)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (modelPaths == null) return models;
        foreach (var p in modelPaths)
        {
            var match = EquipModelRe.Match(p);
            if (!match.Success) continue;
            if (!string.Equals(match.Groups[1].Value, "e0000", StringComparison.OrdinalIgnoreCase)) continue;
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

    // Same guard for the Emperor's-ring injection: the equip's redraw re-runs the walk, and without a
    // cooldown a composite that lands before the ring model is captured would equip it again.
    private const int RingInjectCooldownMs = 5000;
    private long _lastRingInjectTick;

    // What WE equipped, remembered rather than inferred. The walk is the normal source of truth, but it is
    // null until the first successful one, and teardown after a failed walk would then leave our item on
    // the player with the redirect that made it invisible already gone. Only ever acted on when the walk
    // cannot contradict it — if it says another item is in that slot, that is the player's and we keep our
    // hands off. Reset on a successful removal.
    // Ring ownership is PERSISTED (see Configuration.InjectedRingSlot), unlike the glasses flag below. The
    // Emperor's New Ring is an ordinary item people wear by choice, so the model alone cannot say whose it
    // is, and the answer has to survive a plugin reload — in memory only it would be null on the next
    // composite with our ring still on the player, which is precisely when it is needed.
    // Comma-joined so the single-slot config field carries a SET without a schema change — an older config
    // holding "rir" still reads back as exactly that one slot. There can be several now: each free accessory
    // slot is offered as a carrier, and a carrier is the only host that can publish a natively-authored
    // surface undeformed, so a face and a body layer routinely need one each.
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

    private volatile bool _injectedGlasses;

    // Ring slots reported as holding an Emperor's ring we have no record of equipping. Once per slot per
    // session — the reconcile runs on every composite and this is a standing state, not an event.
    private readonly ConcurrentDictionary<string, byte> _unclaimedRingSlots = new(StringComparer.OrdinalIgnoreCase);

    // What the last composite decided about hosting, remembered so a reconcile can be re-run WITHOUT one.
    // The carriers are equipped with ApplyFlag.Once, so any redraw the game does on its own — zoning is the
    // one users notice — reverts them, and the shell loses its host. Re-running the reconciles from the
    // redraw hook puts them back in milliseconds; before this, the only thing that re-equipped them was the
    // tail of a full 5-7s composite, which is what "my gear takes forever to come back" was.
    private volatile bool _lastGearWanted;
    private volatile bool _lastShellBuilt;
    private volatile bool _lastShellOnFacewear;
    // An array, not a List: volatile publishes the swapped-in reference and the contents are never mutated
    // after assignment, which is the same contract the shell-material map uses.
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
    /// Re-run the carrier reconciles once <paramref name="cooldownRemainingMs"/> has elapsed, for the case
    /// where one of them wanted to equip and was refused only by its inject cooldown.
    ///
    /// The cooldowns exist to stop an inject/re-inject ping-pong while the game has not yet loaded and
    /// captured the item's model, and they were sized against the only caller that used to exist: the tail
    /// of a composite, several seconds long, by which point the window had always lapsed. The redraw hook
    /// and the unchanged-inputs skip both run in milliseconds, so without this a zone landing inside the
    /// window leaves the shell with no host and nothing scheduled to put one back.
    ///
    /// Deliberately does not touch the cooldown itself: the retry goes through the same guards, and by the
    /// time it runs a model that WAS merely still loading has been captured, so the equip is skipped for
    /// the right reason instead of repeated.
    /// </summary>
    private void ScheduleCarrierRetry(int cooldownRemainingMs)
    {
        if (Interlocked.Exchange(ref _carrierRetryPending, 1) == 1) return;

        // A margin past the window: TickCount64 and the timer are not the same clock, and firing a
        // millisecond early would be refused again and schedule yet another retry.
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
    /// reads "we don't know" as "nothing worn" — see <see cref="AccessorySnapshotKnown"/> for why that
    /// distinction has to be made before writing to the player's equipment.</summary>
    private bool MetSnapshotKnown => _equippedMetModels != null;

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
    /// <param name="hostedOnFacewear">That shell was published onto a "_met" path, so the pair is its host.</param>
    /// <param name="alreadyHosted">
    /// The shell just built is already hosted on the pair we are about to equip, so the redirect is live
    /// and the equip's own redraw shows the finished result — no follow-up recomposite needed.
    /// </param>
    private void ReconcileInvisibleGlasses(bool gearWanted, bool shellBuilt, bool hostedOnFacewear,
        bool alreadyHosted = false)
    {
        if (InvisibleGlasses.Resolve(Plugin.DataManager, log) is not { } g) return;
        bool want = config.AutoInvisibleGlasses && shellBuilt && hostedOnFacewear;

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
            // MetSnapshotKnown first: before any successful walk the list is null and AnyMetWorn() reads
            // that as "slot empty", which would equip our carrier over a pair the player is actually
            // wearing — the same unknown-is-not-empty trap the ring guards against.
            // The shell is hosting on a pair already on their face, and with the current host policy the
            // only facewear it will host on is a carrier — ours, or an invisible item we may as well treat
            // as ours. Adopt it, so teardown and design matching both know this pair is Proteus's doing
            // even across a plugin reload that lost the flag while the item stayed equipped.
            if (IsOurGlassesWorn(g.ModelSet)) _injectedGlasses = true;

            var sinceInject = unchecked(Environment.TickCount64 - _lastGlassesInjectTick);
            if (MetSnapshotKnown && !AnyMetWorn() && sinceInject <= GlassesInjectCooldownMs)
                // Wanted to equip and was refused only by the cooldown. Something has to come back for it:
                // the callers that reach here now (the redraw hook and the unchanged-inputs skip) run in
                // milliseconds, so a zone landing inside the window would otherwise leave the shell hostless
                // with nothing scheduled to retry. That was safe only while the sole caller was the tail of
                // a multi-second composite, by which point the window had always lapsed.
                ScheduleCarrierRetry((int)(GlassesInjectCooldownMs - sinceInject));

            if (MetSnapshotKnown && !AnyMetWorn()
                && sinceInject > GlassesInjectCooldownMs
                && SetGlassesOnFramework(g.ItemId))
            {
                _lastGlassesInjectTick = Environment.TickCount64;
                _injectedGlasses = true;
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
        else if ((!config.AutoInvisibleGlasses || !gearWanted || (shellBuilt && !hostedOnFacewear))
                 && IsOurGlassesWorn(g.ModelSet))
        {
            // Feature off, nothing to host at all, or a shell built and went to a different host — in that
            // last case our carrier is still on the player's face but no longer redirected to the shell, so
            // its REAL frames would render. Deliberately NOT keyed on a composite that merely failed to
            // produce a shell: that is transient, and unequipping there would flip the glasses off and
            // straight back on.
            bool hostMoved = shellBuilt && !hostedOnFacewear;
            if (SetGlassesOnFramework(0))
            {
                // Charge the cooldown ONLY for the hosts-elsewhere removal. That one can be reached because
                // the carrier failed to load, and then the unequip's redraw empties the "_met" slot, the
                // pending-injection branch hosts on it again, and we re-equip — a flip every composite;
                // charging the cooldown in both directions bounds it to one flip per window. The
                // feature-off and nothing-to-host removals cannot oscillate, and stamping there would put
                // a dead window in front of the obvious "untick, re-tick" that has nothing to retry it.
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

    /// <summary>The model the game draws in a ring slot ("rir"/"ril"), or null when that slot is empty.
    /// Only meaningful once <see cref="AccessorySnapshotKnown"/> — before the first successful walk this
    /// returns null for a slot that is actually full.</summary>
    private string? RingModel(string slot)
        => _equippedAccessoryModels != null && _equippedAccessoryModels.TryGetValue(slot, out var p) ? p : null;

    /// <summary>Has a draw-object walk ever populated the accessory map? Until it has, an absent slot means
    /// "we don't know", not "empty" — and the two must never be confused when the answer decides whether we
    /// write to the player's equipment.</summary>
    private bool AccessorySnapshotKnown => _equippedAccessoryModels != null;

    /// <summary>
    /// The item ids of the hosts PROTEUS put on the player, or null for each we did not. Design matching
    /// blanks these out of the live state so it compares the player's own choices
    /// (<see cref="DesignBindingService.NeutralizeProteusOwnedState"/>).
    /// <para/>
    /// Keyed on what we actually injected, NOT on the feature toggle. The Emperor's New Ring is the
    /// standard invisible-ring glamour and the carrier glasses are an ordinary item — plenty of people
    /// wear either by choice. Blanking one of those would make every design that saved it mismatch, and a
    /// design that matches nothing is treated as unbound, which disables every Proteus mod. The reconciles
    /// adopt an item they find already worn while hosting the shell, so a plugin reload that loses these
    /// flags with our item still equipped re-learns it on the next composite.
    /// </summary>
    public ulong? InjectedGlassesItemId
        => _injectedGlasses ? InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ItemId : null;

    /// <inheritdoc cref="InjectedGlassesItemId"/>
    /// <remarks>A list now: a look can need a carrier in more than one accessory slot, and each slot is a
    /// DIFFERENT item (the Emperor's New Ring, Bracelets and Necklace share a model set, not a row id), so
    /// one id could not blank them all out of the compared state.</remarks>
    public IReadOnlyList<ulong> InjectedCarrierItemIds
        => _injectedCarrierSlots
            .Select(s => InvisibleRing.ResolveFor(Plugin.DataManager, log, s)?.ItemId)
            .Where(id => id != null).Select(id => id!.Value).ToList();

    /// <summary>Does this ring slot hold the Emperor's ring WE equipped?</summary>
    private bool IsOurRingWorn(string slot, int modelSet)
        => RingModel(slot) is { } p && p.Contains($"a{modelSet:D4}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keep the invisible-carrier injections in line with the current composite, mirroring
    /// <see cref="ReconcileInvisibleGlasses"/>. A carrier is the host that makes non-Midlander races FIT, and
    /// the only host that can carry a face/hair/tail layer undeformed — but it renders only while actually
    /// worn, so when the shell was published onto one we equip it here, into the very slot ChooseHosts
    /// already established is free. Idempotent; framework-thread game write.
    /// </summary>
    /// <param name="gearWanted">Gear overlays are active, so a shell is supposed to exist.</param>
    /// <param name="shellBuilt">A shell actually built this composite.</param>
    /// <param name="carrierSlots">The accessory slots the shell published a carrier onto; empty for none.</param>
    private void ReconcileEmperorRing(bool gearWanted, bool shellBuilt, IReadOnlyList<string>? carrierSlots)
    {
        // One pass per slot the shell published a carrier onto, then one removal sweep. Each slot resolves
        // its OWN invisible piece: they share the accessory set but are different items, and Glamourer is
        // told an item id, not a model.
        foreach (var slot in carrierSlots ?? [])
            ReconcileOneCarrier(slot);

        SweepUnusedCarriers(gearWanted, shellBuilt, carrierSlots ?? []);
    }

    /// <summary>
    /// Equip the invisible piece for ONE slot the shell published a carrier onto. The shell renders only
    /// while the piece is actually worn, so this is what makes a carrier host real. Idempotent.
    /// </summary>
    private void ReconcileOneCarrier(string ringSlot)
    {
        if (InvisibleRing.ResolveFor(Plugin.DataManager, log, ringSlot) is not { } r) return;

        // An invisible piece of that set is ALREADY in the slot, so there is nothing to equip — our redirect
        // is what its model loads, and the shell renders on it either way.
        //
        // Crucially this does NOT claim ownership. It used to, on the reasoning that a plugin reload lost the
        // in-memory record while the piece stayed on; ownership is persisted now, so that case is covered
        // without guessing. And the guess was harmful: plenty of people wear the Emperor's New pieces by
        // choice (invisible hands, invisible arms), so adopting one meant that when the shell later moved
        // elsewhere the sweep took THEIR glamour off. Left unclaimed, we simply stop redirecting its model —
        // our mesh disappears from it and the piece stays exactly as they equipped it, which is the right
        // outcome whoever put it there.
        if (IsOurRingWorn(ringSlot, r.ModelSet)) return;

        // Never write to a slot we have not SEEN to be empty. Before the first successful draw-object walk
        // the accessory map is null, which ChooseHosts reads as "nothing worn" — equipping on that would
        // replace a piece the player is actually wearing, and our own removal path would then clear the slot
        // rather than give it back.
        if (!AccessorySnapshotKnown)
        {
            log.Debug("[Proteus] invisible carrier: no accessory snapshot yet — not equipping into {0} until "
                    + "a walk confirms it is empty", ringSlot);
            return;
        }

        // Occupied by the player's own piece: not ours to take. ChooseHosts only picks a free slot, so this
        // means the walk and the build disagree — leave it be rather than overwrite their jewellery.
        if (RingModel(ringSlot) != null) return;

        // Same cooldown reasoning as the glasses: the composite this triggers can run before the game has
        // loaded and captured the model, and without the guard we would equip again and again. And, as there,
        // a retry has to be scheduled behind it — the millisecond-scale callers can all land inside the
        // window and leave the shell hostless with nothing to come back for it.
        var sinceRing = unchecked(Environment.TickCount64 - _lastRingInjectTick);
        if (sinceRing <= RingInjectCooldownMs)
        {
            ScheduleCarrierRetry((int)(RingInjectCooldownMs - sinceRing));
            return;
        }

        if (SetAccessoryOnFramework(r.ItemId, ringSlot))
        {
            _lastRingInjectTick = Environment.TickCount64;
            MarkCarrierInjected(ringSlot);
            // No follow-up recomposite: the shell is ALREADY published at the path this piece loads, so the
            // equip's own redraw lands on the finished shell (the glasses path needs one only when it
            // guessed the host before the item existed).
            log.Information("[Proteus] invisible carrier: equipped item #{0} (model a{1:D4}) in {2} — shell already hosted on it",
                r.ItemId, r.ModelSet, ringSlot);
        }
    }

    /// <summary>
    /// Take back every carrier we equipped that this composite no longer hosts on. Split out from the
    /// per-slot pass because it is a decision about the build AS A WHOLE — a slot is only abandoned once we
    /// know nothing was published onto it — and because it has to sweep slots the current build never
    /// mentioned, which a loop over the build's own slots cannot reach.
    /// </summary>
    private void SweepUnusedCarriers(bool gearWanted, bool shellBuilt, IReadOnlyList<string> inUse)
    {
        // Take OUR piece back off when there is nothing to host at all, or when a shell DID build and went
        // somewhere else (the player equipped a ring of their own that hosts it, say) — otherwise ours would
        // sit in their equipment forever. Deliberately not keyed on a composite that merely failed to produce
        // a shell: that is transient, and unequipping there would flip the piece off and straight back on.
        // Only the "shell moved" removal can be undone by the very next composite, so only that one charges
        // the cooldown (see the glasses). Having no gear at all is a settled state — stamping there would
        // stall an immediate re-tick with nothing to retry it.
        bool hostMoved = gearWanted && shellBuilt;
        if (gearWanted && !shellBuilt) return;

        foreach (var (slot, _, _) in InvisibleRing.CarrierSlots)
        {
            // Still hosting on it — leave it on.
            if (inUse.Contains(slot, StringComparer.Ordinal)) continue;
            if (InvisibleRing.ResolveFor(Plugin.DataManager, log, slot) is not { } r) continue;
            if (!IsOurRingWorn(slot, r.ModelSet)) continue;

            // The piece is worn but no record says we put it there. Two ways to arrive: the player equipped
            // it themselves, or WE did before ownership was persisted (the record used to live only in
            // memory, so a plugin reload lost it). Indistinguishable from here, and the two call for opposite
            // actions — so do nothing and say so once, rather than guess and pull jewellery off someone who
            // chose it. Silence was the old bug's other half: it just sat there with nothing in the log.
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
    /// Remove our injected ring immediately (plugin disable/unload), from whichever hand wears it. Falls
    /// back to what we REMEMBER equipping when the walk can't answer: teardown after a failed walk would
    /// otherwise leave the ring on the player with the redirect that made it invisible already gone. The
    /// memory is only trusted where the walk is silent — if it names another item in that slot, that is
    /// the player's own ring and we leave it alone.
    /// </summary>
    public void RemoveInjectedRing()
    {
        foreach (var (slot, _, _) in InvisibleRing.CarrierSlots)
        {
            if (InvisibleRing.ResolveFor(Plugin.DataManager, log, slot) is not { } r) continue;
            // Teardown takes back only what we equipped, for the same reason the reconcile does — the model
            // is not ownership. The walk-silent clause stays: it is already keyed on our own record.
            bool ours = _injectedCarrierSlots.Contains(slot, StringComparer.Ordinal)
                     && (IsOurRingWorn(slot, r.ModelSet) || !AccessorySnapshotKnown);
            if (ours && SetAccessoryOnFramework(0, slot))
                MarkCarrierRemoved(slot);
        }
    }

    /// <summary>Remove our injected glasses immediately (plugin disable/unload), if the worn pair is ours.
    /// Identified by set so the player's own glasses are never touched, and by what we remember equipping
    /// when the walk has nothing to say (see <see cref="RemoveInjectedRing"/>). Best-effort, framework
    /// thread.</summary>
    public void RemoveInjectedGlasses()
    {
        if (InvisibleGlasses.Resolve(Plugin.DataManager, log) is not { } g) return;
        bool ours = IsOurGlassesWorn(g.ModelSet) || (!MetSnapshotKnown && _injectedGlasses);
        if (ours && SetGlassesOnFramework(0))
            _injectedGlasses = false;
    }

    // Stable string of the equipped part + accessory + head("_met") + bare-body models, for cheap change
    // detection on redraw. A ring swap must rebuild the shell (it changes the host), so the accessory map is
    // folded in too — as is the "_met" list, since putting on/removing glasses or a helmet also changes the
    // host. The bare-body models are in for the same reason as the gear: the shell is cut from them, so a
    // slot that starts resolving to a different race's e0000 model is a different shell. Prefixed "bare:"
    // because those keys are the same four slot names the gear map uses.
    //
    // OUR OWN CARRIERS ARE EXCLUDED — the invisible glasses and the Emperor's ring. They are hosts, never
    // sources: nothing is ever cut from them, and a change of host is tracked separately and correctly by
    // _lastShellHostPaths/hostsChanged. Including them made the signature report a full equipment change
    // every time an ApplyFlag.Once carrier reverted on a redraw, which fired a composite whose only job was
    // to put the carrier back — and, worse, moved the signature so the unchanged-inputs gate could never
    // skip for anyone running a gear shell. The cost is that manually equipping a real Emperor's New Ring
    // no longer fires an equipment-change composite; ChooseHosts already treats that ring as a free slot
    // and the reconcile adopts it, so there is nothing for that composite to have done.
    private string EquipSignature(
        IReadOnlyDictionary<string, string>? models, IReadOnlyDictionary<string, string>? accessories = null,
        IReadOnlyList<string>? metModels = null, IReadOnlyDictionary<string, string>? bareBody = null,
        // The character's own face/hair/tail/ear models. Folded in for the same reason as everything else
        // here: they are geometry a shell can be cut from, so changing face or hairstyle has to invalidate
        // it. Left out, a shell would keep the mesh it cut from the PREVIOUS face and there would be no
        // event anywhere in the plugin that noticed.
        IReadOnlyList<string>? humanParts = null)
    {
        var glassesSet = InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ModelSet;
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
    /// <paramref name="codes"/> as one comparable key, or null when there are none.
    ///
    /// Every site that asks "which races is the character drawn as" must build this the same way or they
    /// will disagree — WaitForRaceToSettle calling a race settled while SchedulePostRedrawBodyTypeCheck
    /// still reads it as changed would leave the two triggering each other indefinitely.
    /// </summary>
    private static string? CharCodeKey(HashSet<string> codes)
        => codes.Count > 0 ? string.Join(",", codes.OrderBy(x => x)) : null;

    /// <summary>
    /// True when <paramref name="candidate"/> is a strict, non-empty SUBSET of <paramref name="baseline"/>
    /// — both comma-joined <see cref="BodyTypeKey"/> values.
    /// <para/>
    /// A real body swap REPLACES a type ("bibo,gen2" → "gen3,gen2") or adds one. It does not simply lose
    /// one with nothing in its place: a pure shrink is a HALF-LOADED walk, caught after the old body
    /// material was dropped and before its replacement arrived. Reading that as a change is what published
    /// "gen2" over a composited "bibo,gen2", which corrupted the fingerprint's bodytype field and let the
    /// corrective composite past the unchanged-inputs gate for the entire nine-second skin phase as well as
    /// the shell it actually needed.
    /// <para/>
    /// The one legitimate shrink — a mod or gear removal genuinely dropping a body type — arrives through
    /// OnModSettingChanged with its own dirty flag and the preamble's own refresh, not through this
    /// backstop. Vetoing it here defers that correction by at most one trigger; accepting it guarantees a
    /// double composite on every design apply.
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

    // ── Core compositor ──────────────────────────────────────────────────────

    /// <summary>
    /// How many <see cref="Recomposite"/> bodies are executing right now. Cancellation here is cooperative
    /// and the LAST <c>ct.IsCancellationRequested</c> check sits some nine hundred lines before the writes,
    /// so a superseded composite does not stop — it keeps writing files and publishes its manifest. Two
    /// runs genuinely overlap in practice (the log shows a 4971 ms and a 3952 ms composite finishing 1.5 s
    /// apart, i.e. ~2.5 s of overlap), and the pruner has to know.
    /// </summary>
    private int _compositesInFlight;

    // Neither bool is defaulted, deliberately: `force` never was, and defaulting only its companion would
    // let a future caller opt into skin reuse by omission. They are one decision, so they are passed together.
    private void Recomposite(CancellationToken ct, bool force, bool skinFingerprintAuthoritative)
    {
        Interlocked.Increment(ref _compositesInFlight);
        try
        {
            RecompositeBody(ct, force, skinFingerprintAuthoritative);
        }
        finally
        {
            Interlocked.Decrement(ref _compositesInFlight);
        }
    }

    private void RecompositeBody(CancellationToken ct, bool force, bool skinFingerprintAuthoritative)
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
            ResetBlendStats();

            EnsureManagedModExists();

            // The previous run's output STAYS ON DISK for the whole composite, and is only pruned once the
            // new manifest is live. Deleting it here — which is what this used to do — left every redirect
            // we own pointing at a file that no longer existed for the ~5s a composite takes. Penumbra
            // validates a redirect's target, so it fell back to the raw game path; for a skin mod's invented
            // paths (Bibo's chara/bibo_mid_*.tex) there is nothing behind that, and the load hard-failed:
            //
            //   WRN Failed to synchronously load resource chara/bibo_mid_base.tex … state: 2:Failure
            //   WRN Failed to … mt_c0201b0001_bibo.mtrl … state: 2:FailedSubResource
            //
            // A material in FailedSubResource does not render, so any redraw landing inside a composite —
            // including the sync-settle redraw we schedule ourselves — turned the body invisible. Worse, a
            // sync plugin snapshotting in that window sees no loaded body textures, so they never reach the
            // peer at all and the invisibility sticks on their side. Content-hashed filenames are what make
            // keeping the old files safe: changed content writes to a DIFFERENT name, so old and new coexist
            // and no live redirect is ever dangling.
            var texturesDirEarly = Path.Combine(managedModDir, "textures");

            // Collect what the last published manifest no longer names. Doing it HERE, a composite late,
            // is deliberate — see PruneSupersededOutput. It also sweeps up anything a cancelled run wrote,
            // which is the only place that ever happens.
            PruneSupersededOutput();

            // NOTHING IS UNPUBLISHED HERE, and nothing may be. This used to clear the manifest and reload,
            // so that a base resolve couldn't come back pointing at our own previous output. The cost of
            // that was every redirect we own — skin textures, shell materials and models, AND the EQDP rows
            // in Manipulations — going dark from here until the republish some five seconds later. The EQDP
            // loss is the worst of it: the host accessory stops loading a model at all, so the shell doesn't
            // degrade, it vanishes.
            //
            // And it was not one composite's worth. Every cancellation check in this method sits AFTER this
            // point and BEFORE the republish, while TriggerRecomposite cancels the in-flight run — so a
            // burst of triggers went: A clears, B cancels A (manifest left empty), B clears, C cancels B…
            // The manifest stayed empty for the whole burst plus the first run that survived to the end.
            // That is what "all my mods disappear when I zone" was: a zone-in fires four independent
            // triggers, and the blackout lasted until the last of them finished.
            //
            // Resolution is handled instead by ResolveUpstream, which every base read already goes through
            // and which refuses to read our own output — plus PrimeUpstreamCache below for the one case
            // ResolveUpstream can't cover on its own (a cold cache with nothing remembered yet).

            // Discover ALL sidecar mods (incl. disabled) so the UI can list and re-enable them;
            // composite only the ones enabled in Penumbra, in priority order.
            var allEntries = discovery.DiscoverAll();
            if (ct.IsCancellationRequested) return;

            LastDiscovered = allEntries;

            // Enabled is the whole test. A design binding used to hold unbound content mods out here as
            // well, so a mod the user had switched on could still paint nothing — which is how a pack
            // imported after the applied design was saved went silently missing. Whether a mod composites
            // is now the same question as whether Penumbra says it is on, and the answer is visible in the
            // one place the user already looks.
            var entries = allEntries
                .Where(e => e.Enabled)
                .OrderBy(e => e.Priority)
                .ToList();
            CheckManagedModHealth(entries);

            if (entries.Count == 0)
            {
                var empty = new Dictionary<string, string>();
                WriteManagedModJson(empty);
                // Nothing is referenced any more. Recorded rather than skipped so the history reflects
                // reality: once this empty manifest ages out of it, everything becomes collectable.
                RecordPublish(empty);

                // A shell hosted on an accessory redirected that accessory's .mdl; an in-place reload won't
                // reload it, so dropping to zero enabled mods must force a FULL redraw or the shell lingers
                // on the accessory (same reasoning as the plugin-disable path). Clear the host tracking too,
                // so the next shell build compares against an empty set. This early return skips the normal
                // reset/drop-detection at the end of the method, hence doing it explicitly here.
                if (_secondSkinActive) _needFullRedraw = true;
                _secondSkinActive = false;
                _lastShellHostPaths = new(StringComparer.OrdinalIgnoreCase);
                // …and the UI-facing locators, for the same reason: this return skips the gear phase that
                // would otherwise publish them, so without this they keep describing the shell that was
                // standing before the last mod was switched off.
                ClearShellLocators();

                // This branch publishes an empty manifest, which no fingerprint describes — and it satisfies
                // whatever forced work was owed, since "no enabled mods" IS the requested result.
                _lastCompositeFingerprint = null;
                Interlocked.Exchange(ref _forcePending, 0);
                Interlocked.Exchange(ref _skinForcePending, 0);
                // Nothing is hosted any more, so the redraw hook must not put a carrier back for a shell
                // that no longer exists.
                RememberHostDecision(gearWanted: false, shellBuilt: false, onFacewear: false, carrierSlots: []);

                // Nothing is hosted, and the empty manifest above dropped the redirect that renders our
                // invisible-glasses carrier as the shell. Leaving it equipped would put a REAL pair of
                // glasses on the player's face that they never chose, so take it off before the redraw.
                // This early return skips the reconcile at the end of the method, hence the explicit call.
                // The ring is invisible either way, but it is still not the player's choice to be wearing.
                ReconcileInvisibleGlasses(gearWanted: false, shellBuilt: false, hostedOnFacewear: false);
                ReconcileEmperorRing(gearWanted: false, shellBuilt: false, carrierSlots: []);

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
            // Imported content packs bring geometry rather than art. They composite into no skin material at
            // all, so they never reach byMaterial — they go straight to the second-skin builder, which
            // appends their meshes into the carrier beside the shells.
            var contentLayers = new List<(OverlayEntry Entry, ResolvedContent Content)>();
            // Every active overlay of every mod, both layers — the second-skin builder ranks groups
            // across layers (a skin group can outrank a gear group), so it needs the full picture.
            var allOverlays = new List<(OverlayEntry Entry, ResolvedOverlay Overlay)>();

            foreach (var entry in entries)
            {
                if (entry.Metadata.HasContent)
                {
                    var content = discovery.ResolveActiveContent(entry);
                    // A restored design binding overrides the pack's colours in memory, exactly as it does
                    // for overlays below — metadata.json is never written.
                    if (colorOverride != null && colorOverride.TryGetValue(entry.ModDirectory, out var cOvr))
                        content = content
                            .Select(c => c with { ColorTableRows = cOvr.Resolve(c.OptionGroup, c.Option) ?? c.ColorTableRows })
                            .ToList();
                    // And its animated glow, resolved the same way. Without this the editor would write a
                    // glow into the binding and the composite would go on publishing the pack's own
                    // material — the change would appear to save and do nothing.
                    if (gearOverride != null && gearOverride.TryGetValue(entry.ModDirectory, out var cGear))
                        content = content
                            .Select(c => c with { Glow = cGear.ResolveContent(c.OptionGroup, c.Option) ?? c.Glow })
                            .ToList();
                    foreach (var c in content) contentLayers.Add((entry, c));
                }

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
                    bool aboveGear = lowestGear.HasValue && Rank(overlay).CompareTo(lowestGear.Value) < 0;
                    // A shell is cut from the body, so only a body-UV overlay has one to move onto (see
                    // CanRenderAsShell). An overlay that paints the face has to stay on its own material
                    // whatever its layer says — including a stored Gear layer, which the editor could write
                    // before it knew better and which is still sitting in mods on disk. Demoting here is what
                    // stops it: the shell builder is not material-aware, so a face overlay reaching it built
                    // a BODY shell and pasted the face art across the whole character.
                    bool canShell = CanRenderAsShell(overlay.Descriptor);
                    if (!canShell && overlay.Descriptor.Layer == OverlayLayer.Gear)
                    {
                        var demoted = CloneDescriptor(overlay.Descriptor);
                        demoted.Layer = OverlayLayer.Skin;   // ShaderPackage → skin.shpk; Scroll goes unread
                        ov = overlay with { Descriptor = demoted };
                        NotifyNoShellSurface(entry, overlay.ColorTableRows, overlay.Descriptor);
                    }
                    // Same surface, the other direction: the auto-promotion is vetoed, so tell the user when
                    // that is what silenced a glow they just set — it looks broken with no word anywhere.
                    else if (!canShell && !overlay.Descriptor.ManualShaderLock
                        && (aboveGear || RenderModeInference.HasCloth(overlay.ColorTableRows ?? [])))
                        NotifyNoShellSurface(entry, overlay.ColorTableRows, overlay.Descriptor);
                    else if (RenderModeInference.ShouldPromoteToGear(overlay.Descriptor.Layer,
                            overlay.Descriptor.ManualShaderLock, overlay.ColorTableRows, aboveGear, canShell))
                    {
                        var promoted = CloneDescriptor(overlay.Descriptor);
                        promoted.Layer = OverlayLayer.Gear;   // ShaderPackage → character.shpk
                        ov = overlay with { Descriptor = promoted };

                        // Only when GLOW is what moved it: an overlay already promoted for sitting above
                        // gear was rendering through a shell before this too, so nothing changed for it
                        // and the notice would be noise.
                        if (!aboveGear) NotifyGlowPromoted(entry, overlay.ColorTableRows);
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
            // Declared out here so the sibling-synthesis pass below can tell "this body type isn't loaded
            // at all" from "the type is loaded, but not the material this sibling would target" — the two
            // read identically in the logs otherwise, and the distinction is the whole answer when a
            // vanilla sibling silently stops being synthesized.
            var activeBodyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            {
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
                    // The active character codes (e.g. "c0101") from body materials in the snapshot. Used
                    // below to filter wrong-race materials in the mid-switch branch, and stored for the
                    // post-redraw race-change check — which compares against CharCodeKey, so this must be
                    // built by it too.
                    var activeCharCodes = CharCodeSet(activeMtrl);
                    _lastCompositedCharCodes = CharCodeKey(activeCharCodes);

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
                            // A material on one of the character's OWN non-body surfaces — face, hair, tail,
                            // ears — gets the same mid-switch tolerance the body branch above gets, and for
                            // the same reason: the snapshot is a walk that can be stale or still settling,
                            // so its silence about a face is not evidence the face isn't there.
                            //
                            // Dropping it outright made the face flap in and out on EVERY trigger. The first
                            // composite ran against a snapshot that reported charCode "none" and body type
                            // "gen2", dropped the face material on that basis, and withdrew its redirects —
                            // then forced a full redraw, so the character reloaded with the unredirected
                            // face. The post-settle composite corrected the snapshot to c1401/"bibo,gen2",
                            // recomposited the face and republished the redirects, but behind an in-place
                            // reload, which does not re-fetch a texture that was withdrawn and restored. Net
                            // result: a face overlay that composites perfectly (byte-identical output to a
                            // run where it rendered) and never reaches the character.
                            //
                            // Equipment and accessory materials keep the strict exact-path rule — for those
                            // the snapshot IS authoritative, which is what the comment at the top says.
                            var keyRace = ExtractHumanCharCode(key);
                            if (keyRace != null
                                && (effectiveCharCodes.Count == 0 || effectiveCharCodes.Contains(keyRace)))
                                continue;   // keep — ours, and the snapshot simply hasn't caught up

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
                        {
                            // Worth a line only when the body type IS loaded somewhere but this material
                            // is not: that is the case that looks like a contradiction in the log ("active
                            // types=bibo,gen2" yet no vanilla sibling), and it means the type came from a
                            // different race or body id. The plain "that body isn't loaded" case is the
                            // normal state for every character and would drown the log.
                            if (activeBodyTypes.Contains(bodyType))
                                log.Debug("[Proteus] No sibling synthesized ({0} is loaded, but not this material): {1}",
                                    bodyType, dstPath);
                            continue;
                        }

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

            var texturesDir = texturesDirEarly;
            Directory.CreateDirectory(texturesDir);

            var redirects = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int texturesPatched = 0;
            // Accumulated across the parallel per-material loop; published to _skinGlowTargets after it.
            var skinGlow = new ConcurrentDictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>();
            // Same, for ChannelContributions — what each material's channels actually received, as opposed to
            // which of them got a redirect published. Touched carries the passes that edit a buffer without
            // being an overlay (AO, skin-tint suppression), so a material those alone reached reads as worked
            // on rather than as an all-zero row.
            //
            // Keyed by material rather than a bag: there are two Add sites (the no-textures bail and the end
            // of the loop), so "one entry per material" is worth making structural instead of relying on the
            // two staying mutually exclusive.
            var contributions = new ConcurrentDictionary<string, ChannelContribution>(StringComparer.OrdinalIgnoreCase);

            // Output filenames carry a CONTENT hash, not a per-run id — see ContentTag for why. Changed
            // bytes still get a new path (the cache miss the old GUID existed for); unchanged bytes keep
            // theirs, so sync plugins stop re-transferring the whole skin set on every composite.

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

            // The Masks tab's effective render mode per mod (layer, shader, scroll effect and its speed and
            // tiling), resolved through the active design binding. Materialised ONCE here rather than
            // re-resolved at each use: it feeds the shell-promotion loop below, the shell synthesis in the
            // gear phase, and the composite fingerprint, and those three must not disagree.
            var maskDescByMod = new Dictionary<string, OverlayDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (MaskDescriptorFor(entry) is { } md)
                    maskDescByMod[entry.ModDirectory] = md;

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
            //
            // Deliberately a RECORDED mode, not one inferred from the mask's colorset the way overlays are
            // (RenderModeInference.ShouldPromoteToGear). Two reasons. Mask glow never worked on skin in the
            // first place — the emissive bake only ever read the OVERLAY rows, never MaskColorTableRows —
            // so there is no existing look to preserve here, only a new one to opt into. And a mask that
            // moves to a shell stops being painted into the skin diffuse/relief (see the maskShellMods
            // skips below), so it lifts off the body onto a pushed-out surface and needs a free accessory
            // to exist at all. Inferring that from a stray Emissive an author left in a colorset would
            // change how the mask renders — or lose it entirely — for a value that used to be inert.
            // The editor still gets there: setting Glow on the Masks tab infers Cloth and persists
            // MaskDescriptor.Layer = Gear (the mask branch of StatusWindow.DrawColorEditor), which is
            // exactly what this reads.
            foreach (var entry in entries)
                if (maskAssetsByMod.TryGetValue(entry.ModDirectory, out var mA2)
                    && mA2.Any(a => a.IndexPath != null || a.NormalPath != null)
                    && maskDescByMod.TryGetValue(entry.ModDirectory, out var md2)
                    && md2.Layer == OverlayLayer.Gear)
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

            // Everything this composite will read as a base is known now, and nothing expensive has started.
            // Make sure each of those paths has a remembered upstream before anything resolves them through
            // our own live redirects. Normally returns immediately; see PrimeUpstreamCache.
            //
            // BEFORE the gate, not after: the gate's fingerprint includes which file each base path actually
            // resolves to, and that is only knowable once this has run. It is also what lets
            // InvalidateUpstreamCache stay a pure cache drop instead of a gate-defeating reset.
            var baseKeys = PrimeUpstreamCache(byMaterial.Keys);
            if (ct.IsCancellationRequested) return;

            // The shape of this composite: which materials it targets, independent of what they resolve
            // to. Retiring a stale base path keys off a CHANGE in this, so it is computed from
            // byMaterial rather than from anything downstream that a cold cache could perturb.
            var baseSignature = ComputeBaseKeysHash(byMaterial.Keys);

            // Contribute the half of the base set that is knowable before the gate — the published
            // manifest's keys plus the overlay material paths — so a fresh install's first mod-settings
            // event has something to test against instead of failing open. Not authoritative: it has not
            // seen what the blend loop resolves, so it may add paths but must never retire any.
            RecordCompositeBaseKeys(baseKeys, baseSignature, authoritative: false);

            // ── Unchanged-inputs gate ────────────────────────────────────────
            // Every input is settled here and nothing expensive has run yet: the decode/blend loop, the
            // shell build and all writes are below. An AMBIENT trigger — one that fires because the world
            // moved rather than because the user asked for something — can stop here when the inputs hash
            // to what is already published.
            //
            // This is what makes zoning cheap. A single zone-in fires four independent triggers (the boot
            // poll re-arms when the local player goes away, the redraw hook, Glamourer re-asserting
            // temporary settings, and a design binding failing to re-match), and every one of them used to
            // run the whole 5-7s pipeline to produce byte-identical output.
            //
            // Placed HERE and not earlier on purpose: option and mask selections are only known after
            // discovery and resolution, and the design-binding overrides are only folded into the overlays
            // at this point. A gate above them would compare a set of inputs that omits the very things a
            // Penumbra temporary setting carries. Nothing persistent is mutated between here and the return
            // below, so a skip leaves the previous composite's shell state intact.
            var fingerprint = BuildCompositeFingerprint(
                byMaterial, gearOverlays, maskPathsByMod, maskAssetsByMod, maskRowsByMod, maskDescByMod,
                maskShellMods, baseKeys, contentLayers);

            // The same inputs minus the ones only the shell reads — see the skin-reuse gate below.
            var skinFingerprint = BuildCompositeFingerprint(
                byMaterial, gearOverlays, maskPathsByMod, maskAssetsByMod, maskRowsByMod, maskDescByMod,
                maskShellMods, baseKeys, contentLayers, skinOnly: true);

            if (!force && config.SkipUnchangedComposites && Volatile.Read(ref _forcePending) == 0
                && _lastCompositeFingerprint != null && fingerprint == _lastCompositeFingerprint)
            {
                // Still reconcile the carriers. They are ApplyFlag.Once, so whatever redraw led here may
                // well have reverted them — and this is the one path that would otherwise leave a shell
                // hosted on an item the player is no longer wearing. Idempotent, so a redundant call when
                // the redraw hook already did it costs nothing.
                ReconcileInvisibleGlasses(_lastGearWanted, _lastShellBuilt, _lastShellOnFacewear,
                                          alreadyHosted: true);
                ReconcileEmperorRing(_lastGearWanted, _lastShellBuilt, _lastShellCarrierSlots);

                // Nothing published, so nothing to record: RecordPublish here would make _publishHistory
                // describe a manifest that was never written, and PruneSupersededOutput would eventually
                // collect files the LIVE manifest still points at. No write, no reload, no redraw — a
                // redraw is precisely the flicker this exists to remove. LastResult keeps showing the last
                // real composite.
                log.Information("[Proteus] recomposite skipped — inputs unchanged ({0:F0}ms)",
                    PhaseCounter.MsSince(tRunStart));
                return;
            }

            // ── Skin reuse ───────────────────────────────────────────────────
            // Something moved, but maybe not anything the SKIN depends on — the overwhelmingly common case
            // being an outfit change, which alters the equipped-item signature and nothing else. The skin
            // fingerprint drops the gear-side equip half and the content pieces (see BuildCompositeFingerprint's
            // skinOnly), so when it matches, the published skin textures are already exactly what this run
            // would recompute. Measured at 2.5s of a 3.6s composite.
            //
            // Gated on _lastCompositeFingerprint being non-null as well, so every site that invalidates the
            // full gate invalidates this one too and there is only one field to remember to clear.
            //
            // Fail-safe by construction: anything unexpected — a missing or truncated output, no remembered
            // publish, a forced run — falls through to the full composite. The cost of being wrong in that
            // direction is a few seconds; the other direction is a character wearing stale skin.
            // Read the whole group through ONE reference so the fingerprint and the redirects it vouches
            // for cannot come from different publishes — see SkinPublish.
            // `force` is the wrong veto here on its own — see TriggerRecomposite's
            // skinFingerprintAuthoritative, which is how a trigger whose skin effect IS hashed opts back in.
            // _skinForcePending is the same distinction applied to a CANCELLED forced run whose work is
            // still owed; the full gate above keeps using the undivided _forcePending.
            var lastSkin = _lastSkinPublish;
            bool skinReused =
                (!force || skinFingerprintAuthoritative)
                && config.SkipUnchangedComposites && Volatile.Read(ref _skinForcePending) == 0
                && _lastCompositeFingerprint != null
                && lastSkin != null && skinFingerprint == lastSkin.Fingerprint
                && lastSkin.Redirects.Count > 0
                && SkinOutputStillOnDisk(lastSkin.Redirects);

            if (skinReused)
            {
                foreach (var kv in lastSkin!.Redirects) redirects[kv.Key] = kv.Value;
                log.Information("[Proteus] skin unchanged — reusing {0} published texture(s), "
                              + "compositing the shell only", lastSkin.Redirects.Count);
            }

            // ── Inherited mask colorsets ─────────────────────────────────────
            // A mod whose masks carry an _id but whose Masks tab has no colorset of its own paints them from
            // the TOPMOST overlay it draws on that material — the same rows the legacy _id merge would have
            // picked, and the same fallback the mask SHELL makes (see the shell synthesis). Keyed per
            // (material, mod) rather than per mod because an overlay only appears on the materials its
            // MaterialGamePath lists, so the topmost one genuinely differs between a mod's body materials.
            //
            // Built HERE, not at the point of use: the paint pass runs inside a 4-way Parallel.ForEach, so
            // both this dictionary and the log-once set have to be finished before that loop starts. Doing
            // the lookup inline meant a plain HashSet was being mutated from four material tasks at once —
            // and a character with two body materials in one mod (Au Ra female b0001 + b0101) hits that on
            // every composite. It also keeps BuildRowDict off the hot path.
            var maskFallbackRows = new Dictionary<string, Dictionary<int, ColorTableRowOverride>>(
                StringComparer.OrdinalIgnoreCase);
            {
                var loggedInherit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (mtrl, list) in byMaterial)
                    // GroupBy preserves source order within a group, and `list` is already in composite
                    // order (bottom→top), so Last() is the overlay the mask sits on — and the mod is
                    // guaranteed present, which an "if not found" guard on a separate lookup would not be.
                    foreach (var modGroup in list.GroupBy(p => p.Entry.ModDirectory, StringComparer.OrdinalIgnoreCase))
                    {
                        var modDir = modGroup.Key;
                        if (maskRowsByMod.ContainsKey(modDir) || maskShellMods.Contains(modDir)) continue;
                        if (!maskAssetsByMod.TryGetValue(modDir, out var mA) || !mA.Any(a => a.IndexPath != null))
                            continue;

                        var top = modGroup.Last();
                        // An empty/absent colorset leaves the dictionary empty, which ApplyIndexedOverlay
                        // reads as neutral white rather than skipping the pixel.
                        var inherited = BuildRowDict(top.Overlay.ColorTableRows);
                        maskFallbackRows[MaskFallbackKey(mtrl, modDir)] = inherited;
                        if (loggedInherit.Add(modDir))
                            log.Debug("[Proteus] Masks on \"{0}\": no Masks colorset — inheriting \"{1}\"'s "
                                    + "{2} row(s) so the mask still paints on skin",
                                modDir, top.Overlay.Option ?? "(default)", inherited.Count);
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
            //
            // Two bounds, both of which this loop used to lack:
            //
            // ISSUE-ONCE WHILE IN FLIGHT. PrefetchAhead re-warms the overlapping part of its window on every
            // pair, and re-lists the mod's whole mask set each time, so the same file was queued several
            // times per composite. Each duplicate still costs a pool slot, a decode-cache lookup and a full
            // TrimCache scan to discover it had nothing to do.
            //
            // The key is RETIRED once the warm completes, deliberately. Suppressing it for the whole
            // composite would mean a file evicted between its prefetch and its use is never warmed again —
            // the repeated warms were wasteful, but they were also self-healing, and dropping that outright
            // would trade one inefficiency for a synchronous decode on the critical path.
            //
            // BOUNDED CONCURRENCY. These ran as unbounded fire-and-forget tasks on the same pool the blend
            // loop's own Parallel.For work uses, so a wide prefetch could starve the very consumer it was
            // meant to be running ahead of — and prefetching only pays when it COMPLETES first (see
            // PrefetchDepth, where measurement already showed deeper being worse for the same reason).
            // Half the cores leaves the blend loop the other half. The wait is awaited, not blocked on, so a
            // queued warm holds no thread while it waits.
            var prefetchIssued = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var prefetchGate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
            void WarmBg(string path, int w, int h)
            {
                var warmKey = $"{path}|{w}x{h}";
                if (!prefetchIssued.TryAdd(warmKey, 0)) return;
                _ = Task.Run(async () =>
                {
                    try { await prefetchGate.WaitAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { prefetchIssued.TryRemove(warmKey, out _); return; }
                    catch (ObjectDisposedException) { prefetchIssued.TryRemove(warmKey, out _); return; }
                    try
                    {
                        // Set AFTER the await: the flag is [ThreadStatic], so it has to be stamped on the
                        // thread that actually runs the decode, not the one that queued the wait.
                        TextureLoader.BackgroundPrefetch = true;
                        textureLoader.LoadPngAsRgba(path, w, h);
                    }
                    catch { }
                    finally
                    {
                        TextureLoader.BackgroundPrefetch = false;
                        prefetchGate.Release();
                        // Retired here, not held for the composite — see ISSUE-ONCE WHILE IN FLIGHT above.
                        prefetchIssued.TryRemove(warmKey, out _);
                    }
                }, ct);
            }

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

            // Drop the previous run's answers BEFORE producing this run's. Every cancellation check below sits
            // between here and the assignment after the loop, so without this a cancelled composite would
            // leave the panel showing a full, plausible set of contributions for a composite that no longer
            // describes the character. Empty reads as "no answer yet", which is the truth.
            _channelContributions = [];

            // Compression (opt-in): BC7 for every skin channel — the skin normal uses its B/A channels
            // too, so BC5 (2-channel) would corrupt it. Off ⇒ uncompressed, byte-identical to before.
            bool compress = config.EnableCompression;

            // Salted with the dimensions and how the bytes will be encoded — both change the written
            // file without changing the RGBA buffer, and a stale path would leave the game on the old
            // format. 0 = uncompressed, 1 = BC7 via the native shim, 2 = BC7 via managed BCnEncoder:
            // the two encoders differ byte-for-byte, so a session that fell back to managed must not
            // silently reuse a native-encoded file under a name that claims to describe its content.
            // Only distinguished when compressing, so a backend flip can't churn uncompressed output.
            //
            // Loop-invariant, so hoisted out of the per-material body — and it has to be, because the base
            // diffuse is now fingerprinted at LOAD time with the same salts the output name uses, and that
            // happens long before the publish block where these used to be declared.
            int encSalt = compress ? (TextureLoader.NativeEncoderAvailable ? 1 : 2) : 0;

            // Skipped wholesale when the skin is being reused — the redirects it would produce are already
            // seeded above, and its other two outputs (contributions, glow targets) are restored below.
            // BRACED: the statement it guards runs for well over a thousand lines, so an unbraced `if`
            // would let anything appended after that closing `});` run unconditionally — on the reuse path
            // that means skin code whose inputs were never computed.
            if (!skinReused)
            {
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
                    //
                    // A mod with NO mask colorset still merges here. That pass now paints those masks too
                    // (inheriting the fabric's rows), but the two cannot double up: the combined mask's W term
                    // has already erased the fabric wherever the mask's territory is opaque, so the merge only
                    // reaches the soft edges — where it keeps giving them the mask's row, as it always has.
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
                    var tSil = PhaseCounter.Begin();
                    try { return GarmentSilhouetteCore(modDir, w, h); }
                    finally { blendSilhouetteStats.Stop(tSil); }
                }

                byte[]? GarmentSilhouetteCore(string modDir, int w, int h)
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

                var mtrlDisk = ResolveUpstream(mtrlGamePath);
                // RAW parse. The disk/game split this used to do by hand lives inside ResolveMtrlTexturesRaw
                // now, and more importantly it skips Lumina's typed MtrlFile — which misreads some Dawntrail
                // layouts. A modded material (older TexTools layout) read fine while the stock game file came
                // back empty, so an overlay targeting a VANILLA material hit the "no textures" bail below and
                // silently never composited.
                var texPaths = textureLoader.ResolveMtrlTexturesRaw(mtrlDisk, mtrlGamePath);

                if (texPaths.Diffuse == null && texPaths.Normal == null && texPaths.Mask == null)
                {
                    log.Warning("[Proteus] No textures found for material: {0}", mtrlGamePath);
                    // Record it before bailing. This is the most broken a material can be, and leaving it out
                    // of the panel meant the rows most worth seeing were the ones that never appeared.
                    contributions[mtrlGamePath] = new ChannelContribution(mtrlGamePath, 0, 0, 0,
                        DiffuseWanted: pairs.Any(p => p.Overlay.Descriptor.Diffuse != null), Touched: false);
                    return;
                }

                byte[]? baseD = null, baseN = null, baseM = null;
                int wD = 0, hD = 0, wN = 0, hN = 0, wM = 0, hM = 0;

                // Whether the base diffuse has been ASKED for yet, which is not the same question as whether
                // baseD is null: a failed load leaves Array.Empty behind, which is non-null. Several sites
                // used to load this buffer with slightly different guards — `baseD == null` in the overlay
                // loop, in the Masks pass and in the disabled tint block, `baseD == null || baseD.Length == 0`
                // in the AO pass, so that one alone re-attempted a load that had already failed. They agree
                // now because they all go through EnsureBaseDiffuse below.
                //
                // Behaviourally this is a wash for the overlay loop — Array.Empty made the old guard skip the
                // reload just as this does — but it is what lets the failure be reported ONCE per material
                // instead of once per overlay, and it removes the triplicated load.
                bool baseDTried = false;

                // The base diffuse's content tag as it stood before anything blended into it — computed with
                // the SAME salts the output filename uses, so it can be compared to the published tag for
                // free at the end. Taken by SnapshotBaseDiffuse at the first edit, not at load.
                //
                // This is what separates the last two ways the reported fault can happen once the diagnostics
                // above come back clean: an overlay that blended and CHANGED the skin, versus one that ran a
                // blend pass which turned out to be a no-op (coverage masked to nothing, opacity at -100, art
                // that is fully transparent). Both report diffuse(1); only the tags tell them apart. One extra
                // hash of the base is the whole cost, and the question is no longer hypothetical.
                string? baseDiffuseTag = null;

                // Whether an overlay actually BLENDED into each buffer, as opposed to the buffer merely
                // existing. The publish block below used to key off "buffer is non-empty", which is true the
                // moment a base texture decodes — so a composite that applied nothing and one that applied
                // everything produced identical logs and identical redirects. Content-hashed output names make
                // that indistinguishable on disk too (unchanged bytes keep their name). These are locals on a
                // single material's task inside the Parallel.ForEach, so a plain bool is the right tool.
                //
                // Deliberately "a blend pass RAN", not "the bytes changed". A fully transparent overlay still
                // sets the flag and still publishes a copy of the base — provably a no-op, since
                // ApplyFlatOverlay skips zero-alpha pixels. Proving the stronger claim means hashing the base
                // before and after, a second full pass over a buffer up to 4096×4096, to catch a fault nobody
                // has reported. The question this answers is "did an overlay reach this channel at all", which
                // is the one that was unanswerable.
                bool diffuseBlended = false, normalBlended = false, maskBlended = false;
                int diffuseContributors = 0, normalContributors = 0, maskContributors = 0;

                // At least one overlay on this material ASKED for a diffuse. Paired with a zero contributor
                // count that is the whole reported fault — the author meant to paint the skin and nothing
                // reached it — and it is the one combination worth colouring red in the UI.
                bool diffuseWanted = false;

                // The "this material has no diffuse sampler" warning is about the MATERIAL, so it says the
                // same thing however many overlays trip it. Ten overlays on one body used to mean ten
                // identical lines, on every equipment change and every zone.
                bool warnedNoDiffuseSampler = false;

                // Load the material's base diffuse at most once, remembering a failure so no later overlay or
                // pass re-attempts it. Returns the buffer, or null when there is nothing to composite onto —
                // a buffer rather than a bool because the compiler cannot carry a captured local's
                // nullability across a local-function call, and `baseD!` at every use is a worse answer.
                byte[]? EnsureBaseDiffuse()
                {
                    if (baseDTried) return baseD is { Length: > 0 } ? baseD : null;
                    baseDTried = true;
                    if (texPaths.Diffuse == null) { baseD = Array.Empty<byte>(); return null; }

                    var diffDisk = ResolveUpstream(texPaths.Diffuse);
                    var loaded = textureLoader.LoadBaseTexture(diffDisk, texPaths.Diffuse);
                    if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                    baseD ??= Array.Empty<byte>();

                    // A skin mod's paths are invented (chara/bibo_mid_base.tex is in no game index), so there
                    // is no SqPack fallback behind a failed upstream resolve: this is the whole diffuse for
                    // the whole material, gone, and it used to go without a word.
                    if (baseD.Length == 0)
                        log.Warning("[Proteus] Base diffuse failed to load for {0}: {1} resolved to {2} — no "
                                  + "diffuse can be composited onto this material",
                            mtrlGamePath, texPaths.Diffuse, diffDisk ?? "(nothing)");

                    return baseD.Length > 0 ? baseD : null;
                }

                // Fingerprint the base immediately BEFORE the first edit, not at load. Called from each of the
                // three sites that mutate the buffer, because "loaded" and "about to be changed" are different
                // events: a material loaded only for the AO pass's dimensions, or one whose every overlay's
                // art failed to decode, would otherwise pay a full FNV pass over as much as 64 MB for a value
                // discarded moments later with the buffer itself.
                void SnapshotBaseDiffuse()
                {
                    if (baseDiffuseTag == null && baseD is { Length: > 0 })
                        baseDiffuseTag = TimedContentTag(baseD, wD, hD, encSalt, OutputFormatVersion);
                }

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

                    // How this overlay is named in the diagnostics below. Both halves are nullable — an
                    // overlay in a mod's default data belongs to no group and no option.
                    var optLabel = $"{resolved.OptionGroup ?? "(default)"}/{resolved.Option ?? "(default)"}";

                    byte[]? diffuseOv = null;
                    byte[]? normalOv  = null;

                    // Coverage mask: the alpha of the diffuse overlay defines WHERE this overlay
                    // applies. When there is no diffuse overlay (normal-only), the mask is
                    // synthesized from the normal map's blue channel. Every compositing channel
                    // — diffuse, normal, emissive, mask texture — is gated by this same mask.
                    byte[]? covSrc = null;  // coverage source at (covW × covH)
                    int covW = 0, covH = 0;

                    // ── Step 1: load diffuse overlay (establishes coverage) ───
                    // The material having no diffuse SAMPLER is a different failure from the overlay art not
                    // loading, and it is the quieter of the two: the guard below is an AND, so a body material
                    // with no diffuse/ColorMap0 sampler skips this block entirely, Step 2 synthesizes coverage
                    // off the normal, and the normal composites perfectly while the diffuse silently cannot.
                    // The all-three-null case warns above; this one had nothing, even though the exactly
                    // analogous mask-only case has warned for ages.
                    if (desc.Diffuse != null) diffuseWanted = true;

                    if (desc.Diffuse != null && texPaths.Diffuse == null && !warnedNoDiffuseSampler)
                    {
                        warnedNoDiffuseSampler = true;
                        log.Warning("[Proteus] Overlay declares a diffuse but the material has no diffuse "
                                  + "sampler, so it cannot be painted onto the skin: {0} (first seen on mod "
                                  + "{1}, {2}) — set the overlay to Cloth to render it on a gear shell instead",
                            mtrlGamePath, entry.ModDirectory, optLabel);
                    }

                    if (desc.Diffuse != null && texPaths.Diffuse != null)
                    {
                        if (EnsureBaseDiffuse() != null)
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
                            else
                            {
                                // RemapIfNeeded only ever returns null when its INPUT is null, so this is
                                // precisely a LoadPng failure. TextureLoader does log it — unprefixed, and
                                // cached, so it appears once per session and vanishes from a filtered log.
                                log.Warning("[Proteus] Overlay diffuse failed to load: {0} (mod {1}, {2}) — "
                                          + "the skin diffuse is left untouched",
                                    diffPath, entry.ModDirectory, optLabel);
                            }
                        }
                    }

                    // ── Step 2: load normal overlay; synthesize coverage if needed ──
                    if (desc.Normal != null && texPaths.Normal != null)
                    {
                        baseN ??= LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
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
                            var loaded = textureLoader.LoadBaseTexture(ResolveUpstream(texPaths.Mask), texPaths.Mask);
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

                    if (covSrc == null)
                    {
                        // Nothing to composite — but "this overlay contributed nothing at all" is a real
                        // outcome, not a non-event, and it used to leave no trace whatsoever. Every art
                        // source either was not declared or failed to load; the specific reason has already
                        // been warned about above if there was one.
                        log.Debug("[Proteus] No coverage for {0} on {1} ({2}) — overlay contributes nothing",
                            entry.ModDirectory, mtrlGamePath, optLabel);
                        continue;
                    }

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
                        SnapshotBaseDiffuse();
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
                        diffuseBlended = true; diffuseContributors++;
                    }
                    else if (desc.Diffuse == null && normalOv != null)
                    {
                        // Deliberate, per the disabled synthesis below — but indistinguishable in the log from
                        // a diffuse that was MEANT to apply and failed, which is exactly the report this came
                        // from ("the normal applies, but they see the base skin diffuse"). Say which it is.
                        log.Debug("[Proteus] Normal-only overlay {0} ({1}) on {2} — skin diffuse left "
                                + "untouched by design",
                            entry.ModDirectory, optLabel, mtrlGamePath);
                    }
                    // Normal-only (and mask-only) overlays no longer synthesize a diffuse tint: the author
                    // wants just the normal (and any mask) applied, leaving the skin diffuse untouched. The
                    // Row-16-colour synthesis below is disabled per request — kept for reference.
                    //
                    // Kept CURRENT rather than frozen, because dead code that no longer compiles against the
                    // live invariants is a trap for whoever revives it: it goes through EnsureBaseDiffuse like
                    // every other load site, and it sets diffuseBlended, without which the writer's gate would
                    // compute the tint correctly and then discard the whole buffer.
                    /*
                    else if (desc.Diffuse == null && normalOv != null && texPaths.Diffuse != null && desc.GenerateDiffuse)
                    {
                        // Normal-only overlay: apply synthesized tint (Row 16 color) to the diffuse
                        // channel. Skipped when GenerateDiffuse is false — the author wants the normal
                        // (and any mask) applied without altering the skin diffuse.
                        if (EnsureBaseDiffuse() is { } tintBaseD)
                        {
                            var tint = CovAt(wD, hD);
                            if (tint != null)
                            {
                                SnapshotBaseDiffuse();
                                ApplyFlatOverlay(tintBaseD, tint, row16A, wD, hD);
                                diffuseBlended = true; diffuseContributors++;
                            }
                        }
                    }
                    */

                    // ── Phase B: normal composite ─────────────────────────────
                    if (normalOv != null && baseN is { Length: > 0 })
                    {
                        // Replace mode is a plain alpha-over, which is exactly what a whole-skin overlay
                        // wants: at full coverage the base is gone rather than added to (CompoundNormal
                        // would apply the same slopes twice — see NormalMode.Replace), and RGB includes the
                        // blue channel, so the author's skin-colour influence survives instead of being
                        // silently inherited from whatever body mod happens to sit underneath.
                        if (desc.NormalMode == NormalMode.Replace)
                            AlphaComposite(baseN, normalOv, wN, hN, CovAt(wN, hN));
                        else
                            CompoundNormal(baseN, normalOv, wN, hN, CovAt(wN, hN));
                        normalBlended = true; normalContributors++;
                    }

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
                        baseN ??= LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
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
                                // A real rewrite of the normal buffer, even for a diffuse-only overlay that
                                // never reached Phase B — so the normal is genuinely ours to publish. Not a
                                // contributor bump: it is the same overlay Phase B already counted.
                                normalBlended = true;
                            }
                        }
                    }

                    // ── Phase D: mask texture composite ───────────────────────
                    if (desc.Mask != null && texPaths.Mask != null)
                    {
                        if (baseM == null)
                        {
                            var loaded = textureLoader.LoadBaseTexture(ResolveUpstream(texPaths.Mask), texPaths.Mask);
                            if (loaded.HasValue) { baseM = loaded.Value.rgba; wM = loaded.Value.width; hM = loaded.Value.height; }
                            baseM ??= Array.Empty<byte>();
                        }
                        if (baseM.Length > 0)
                        {
                            var maskPathD = Path.Combine(entry.SidecarRoot, desc.Mask);
                            var ov = RemapIfNeeded(LoadPng(maskPathD, wM, hM), wM, hM, srcBodyType, maskPathD);
                            if (ov != null)
                            {
                                AlphaComposite(baseM, ov, wM, hM, CovAt(wM, hM));
                                maskBlended = true; maskContributors++;
                            }
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

                    baseN ??= LoadBaseNormal(texPaths.Normal, ref wN, ref hN);
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

                // ── Masks diffuse ──────────────────────────────────────────────
                // A mod's single "Masks" tab colours its active masks from ONE shared table, composited on
                // top of the overlay diffuse. The mask _id selects the row, so this is the only place a mask
                // gets colour on skin.
                //
                // The table is the mod's own Masks colorset (maskRowsByMod) when it has one, else the topmost
                // fabric overlay's rows — the same colours the legacy _id merge would have picked, and the same
                // fallback the mask SHELL already makes (see the shell synthesis). Without that fallback a mask
                // with no colorset of its own painted NOTHING on skin: the legacy merge only recolours pixels
                // the fabric already covers, and a mask's own grayscale has not been additive coverage since
                // masks became garments in their own right (MaskAdds). So a waistband, toe cap or seam — the
                // parts a mask draws rather than erases — vanished on Skin while rendering fine on Cloth, which
                // is the "masks are invisible as a skin layer" report. Painting over the merge is safe: the
                // combined mask's W already erased the fabric everywhere its territory is opaque, so the two
                // never both contribute to the same texel.
                //
                // What the inherited table paints is a FLAT row colour, not the fabric's art — the same as the
                // mod's own Masks colorset would, since `art` is white and only the row tints it. With the
                // packager's default row (white) that means a mask reads white until its Masks tab is given
                // colours. That is the intended trade: the fabric's pattern was never available there anyway
                // (W erased it), so the choice is a flat colour or nothing at all.
                //
                // The active masks combine TOP-TERRITORY-WINS (matching CombinedMaskAt, which carves the other
                // layers the same way): at each pixel the topmost mask that has territory (alpha) there decides
                // both the coverage (its grayscale) and the colour row (its _id). So a mask that is BLACK in
                // its territory forces coverage 0 — a hole that reveals skin — even where a LOWER mask is white.
                foreach (var modDir in pairs.Select(p => p.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (maskShellMods.Contains(modDir)) continue;   // mask lives on the shell, not the skin diffuse
                    if (!maskAssetsByMod.TryGetValue(modDir, out var assets) || texPaths.Diffuse == null) continue;
                    if (!assets.Any(a => a.IndexPath != null)) continue;
                    // Own Masks colorset, else the one inherited from this material's topmost overlay
                    // (precomputed above, single-threaded). Neither ⇒ nothing to colour the mask with.
                    if (!maskRowsByMod.TryGetValue(modDir, out var maskRows)
                     && !maskFallbackRows.TryGetValue(MaskFallbackKey(mtrlGamePath, modDir), out maskRows))
                        continue;
                    lastSrcBodyTypeByMod.TryGetValue(modDir, out var maskSrcBodyType);

                    if (EnsureBaseDiffuse() is not { } maskBaseD) continue;

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

                    // Per-row Opacity from the Masks colorset — coverage shapes the mask, THEN the row's
                    // transparency scales it, the same order the overlay path uses after its own mask pass.
                    // ApplyIndexedOverlay reads colour and emissive from the row but never opacity, so
                    // without this the Masks tab's opacity slider moved nothing on skin, while the identical
                    // row faded a mask SHELL correctly (SecondSkinService's per-row opacity pass). Reordering
                    // the option in the group could not help: order decides which mask owns a texel, not how
                    // transparent the winner is.
                    if (maskRows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                        art = ApplyIndexedOpacity(art, cid, maskRows);

                    SnapshotBaseDiffuse();
                    ApplyIndexedOverlay(maskBaseD, art, cid, maskRows, false, wD, hD);
                    diffuseBlended = true; diffuseContributors++;

                    // Glow row-map from the same PAINTED alpha + _id. 0 = no glow (hole/outside/faded out),
                    // else 0x80 | (A?0x40) | pairIdx — same format as overlays. Reads the post-opacity alpha
                    // rather than raw coverage so a mask a row fades to nothing stops glowing too.
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
                            int so = sp * 4;
                            if (art[so + 3] == 0) continue;   // hole/outside/faded out = no glow
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

                    // Qualify every mod ONCE, keeping the only flag the loop still needs (masks pick the
                    // silhouette: a masked garment traces its trim, everything else its own coverage). The
                    // Skin term walks allOverlays, so evaluating this per mod per use — the load gate, the
                    // loop's continue, and the mask flag — swept it three times for nothing.
                    //
                    // This has to happen BEFORE the island precompute below, because that reads baseD and the
                    // base is loaded off the back of this. It used to sit after, so on a material whose skin
                    // overlays are all normal-only — nothing else having loaded the diffuse yet — the island
                    // labelling was skipped and the AO shadow fell back to a plain blur that bleeds across the
                    // UV gutter. AoSources is a local function, so it is usable above its definition.
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

                    // The base body diffuse — needed for the shadow, for the island precompute below, AND for
                    // the shared "covered above" mask's dimensions, so load it up front even when only the
                    // normal indent is enabled.
                    //
                    // Only when some mod actually qualifies, though. Loading it unconditionally left baseD
                    // non-empty for every material this pass touched, and the writer at the end of the
                    // composite used to republish (and re-encode) any non-empty buffer — so a face material
                    // whose mod supplies nothing but a mask still got a Proteus-written, BC7-recompressed _d.
                    // The diffuseBlended gate at the writer now catches that case generally; this stays as the
                    // cheaper guard, since not loading at all beats loading and discarding.
                    if (aoQualified.Count > 0) EnsureBaseDiffuse();

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
                    var tIslands = PhaseCounter.Begin();
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
                    blendIslandStats.Stop(tIslands);

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

                    var tAo = PhaseCounter.Begin();
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
                                var tBlurD = PhaseCounter.Begin();
                                blurredD = insidePlane != null && islandLabels != null && islandOwner != null
                                    ? BlurCoverageWithinIslands(strapD, islandLabels, islandOwner, islandCount, insidePlane,
                                                                bodyMdls == null ? null : TimedSeamSource(bodyMdls, wD, hD, SeamReach(radiusD)), wD, hD, radiusD, islandBlurCache)
                                    : BlurCoverage(strapD, wD, hD, radiusD);
                                blendBlurStats.Stop(tBlurD);
                                SnapshotBaseDiffuse();
                                ApplyAmbientOcclusion(baseD, strapD, blurredD, wD, hD, aoStrength, coveredAbove);
                                // AO is a real edit to the skin diffuse in its own right — a gear-layer mod
                                // with no skin overlay at all still legitimately owns the buffer through it.
                                diffuseBlended = true;
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
                                    var tBlurN = PhaseCounter.Begin();
                                    blurredN = strapN == null ? null
                                        : insidePlane != null && islandLabels != null && islandOwner != null && wN == wD && hN == hD
                                            ? BlurCoverageWithinIslands(strapN, islandLabels, islandOwner, islandCount, insidePlane,
                                                                        bodyMdls == null ? null : TimedSeamSource(bodyMdls, wN, hN, SeamReach(radiusN)), wN, hN, radiusN, islandBlurCache)
                                            : BlurCoverage(strapN, wN, hN, radiusN);
                                    blendBlurStats.Stop(tBlurN);
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
                    // Includes the seam-map and content-tag time charged separately below, so the phases
                    // line subtracts those out rather than double-counting them.
                    blendAoStats.Stop(tAo);

                    // This pass loaded the normal and no garment ended up indenting it — hand it back so the
                    // writer doesn't republish (and re-encode) an untouched texture. Only a buffer with real
                    // content: a failed load left Array.Empty behind, which must STAY as the memo that the
                    // load was already tried and failed.
                    if (aoLoadedNormal && !aoIndentedNormal && baseN is { Length: > 0 }) baseN = null;
                }

                var baseName = SanitizeName(mtrlGamePath);
                var channels = new System.Text.StringBuilder();
                // WHICH game path each channel was published to. The material's texture paths are read out
                // of the .mtrl, so they are not derivable from anything else in the log — and they are the
                // one link that decides whether the game ever reads our output. Without them, confirming
                // "we composited" says nothing about "the character samples it".
                var published = new List<string>(3);

                // Nothing edited the diffuse — hand the buffer back exactly as the AO pass does for the normal
                // above. Publishing it anyway meant redirecting the skin mod's own texture path at a BC7
                // re-encode of its own pixels: lossy, pointless, and worst of all invisible, because a
                // healthy-looking redirect is what a normal-only overlay and a BROKEN diffuse overlay both
                // produced. Now the diffuse's absence from the redirects line below IS the signal.
                if (!diffuseBlended && baseD is { Length: > 0 })
                {
                    log.Debug("[Proteus] Nothing composited into the diffuse of {0} — leaving the base texture "
                            + "in place rather than republishing it", mtrlGamePath);
                    baseD = null;
                }

                // Edited, but to no effect — and WHICH pass edited it decides whether that is a fault.
                //
                // An OVERLAY that blended and changed nothing is the reported bug: from the outside it is
                // indistinguishable from the overlay not applying at all, and every other line says success.
                //
                // Ambient occlusion changing nothing is not a fault at all. AO qualifies for a material
                // whenever a gear or mask mod could cast onto it, and then legitimately does nothing when the
                // garment covers the whole body — coveredAbove suppresses the shadow everywhere there is
                // gear over the skin. That fired the warning on a perfectly healthy composite, telling the
                // user to "check coverage masks and opacity" for overlays they do not have. It is only ever
                // reachable with zero contributors, since Phase A and the Masks pass both increment.
                string? diffuseTag = null;
                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    diffuseTag = TimedContentTag(baseD, wD, hD, encSalt, OutputFormatVersion);

                    if (baseDiffuseTag != null && diffuseTag == baseDiffuseTag)
                    {
                        if (diffuseContributors > 0)
                            log.Warning("[Proteus] Diffuse of {0} is byte-identical to the base skin after {1} "
                                      + "overlay(s) blended into it — the blend was a no-op, so the body will "
                                      + "render as if nothing applied (check coverage masks and opacity)",
                                mtrlGamePath, diffuseContributors);
                        else
                        {
                            // No contributors means no glow maps either (both sites that add them increment),
                            // so nothing downstream needs the published path. Hand it back like the untouched
                            // case above: publishing a byte-identical BC7 re-encode of the skin mod's own
                            // texture is the pointless lossy round-trip that gate exists to avoid.
                            log.Debug("[Proteus] Diffuse of {0} unchanged — only ambient occlusion reached it "
                                    + "and it had nothing to darken (gear covers the skin, or strength is 0). "
                                    + "Leaving the base texture in place.", mtrlGamePath);
                            baseD = null;
                        }
                    }
                }

                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    var tag = diffuseTag!;
                    var name = baseName + "_" + tag + "_d.tex";
                    var outPath = Path.Combine(texturesDir, name);
                    var relPath = "textures/" + name;
                    if (AlreadyWritten(outPath)
                     || textureLoader.WriteTex(baseD, wD, hD, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    {
                        redirects[texPaths.Diffuse] = relPath; Interlocked.Increment(ref texturesPatched);
                        channels.Append(" diffuse(").Append(diffuseContributors).Append(')');
                        published.Add($"{texPaths.Diffuse} -> {relPath}");

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
                    var name = baseName + "_" + TimedContentTag(baseN, wN, hN, encSalt, OutputFormatVersion) + "_n.tex";
                    var outPath = Path.Combine(texturesDir, name);
                    var relPath = "textures/" + name;
                    if (AlreadyWritten(outPath)
                     || textureLoader.WriteTex(baseN, wN, hN, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    {
                        redirects[texPaths.Normal] = relPath; Interlocked.Increment(ref texturesPatched);
                        channels.Append(" normal(").Append(normalContributors).Append(')');
                        published.Add($"{texPaths.Normal} -> {relPath}");
                    }
                }
                if (baseM is { Length: > 0 } && texPaths.Mask != null)
                {
                    var name = baseName + "_" + TimedContentTag(baseM, wM, hM, encSalt, OutputFormatVersion) + "_m.tex";
                    var outPath = Path.Combine(texturesDir, name);
                    var relPath = "textures/" + name;
                    if (AlreadyWritten(outPath)
                     || textureLoader.WriteTex(baseM, wM, hM, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    {
                        redirects[texPaths.Mask] = relPath; Interlocked.Increment(ref texturesPatched);
                        channels.Append(" mask(").Append(maskContributors).Append(')');
                        published.Add($"{texPaths.Mask} -> {relPath}");
                    }
                }

                contributions[mtrlGamePath] = new ChannelContribution(mtrlGamePath,
                    diffuseContributors, normalContributors, maskContributors,
                    diffuseWanted, diffuseBlended || normalBlended || maskBlended);

                if (channels.Length > 0)
                {
                    log.Debug("[Proteus] Composited {0}:{1}", mtrlGamePath, channels);
                    log.Debug("[Proteus]   redirects: {0}", string.Join(" | ", published));
                }

                // The headline fault this instrumentation exists for, stated in one line rather than left to
                // be inferred from a channel missing off a list. An overlay asked to paint the skin and not
                // one pixel of it reached the diffuse; every specific reason has already been warned about
                // above, so this is the summary that points at them.
                if (diffuseWanted && !diffuseBlended)
                    log.Warning("[Proteus] Nothing reached the diffuse of {0} although an overlay declared one "
                              + "— the body will render its base skin colour (normal: {1}, mask: {2})",
                        mtrlGamePath, normalBlended ? "applied" : "not applied", maskBlended ? "applied" : "not applied");

            });
            }   // end: if (!skinReused)

            // Filtered and sorted here, once per composite, so the status window can render it straight — that
            // panel redraws every frame and has no business doing either.
            //
            // Inert rows are dropped at the source: the AO top-up pass adds the character's own body materials
            // to the composite with no overlays attached, and those would render as "diffuse 0, normal 0,
            // mask 0" — a line that looks like a fault and is nothing of the kind. A row survives if something
            // reached it, or if something meant to.
            //
            // On a skin reuse the loop never ran, so `contributions` and `skinGlow` are empty. Restore what
            // the last real skin composite produced instead of publishing nothing: the panel would go blank
            // and the colour-table editor's Glow button would lose its targets, on a composite that changed
            // nothing about the skin.
            _channelContributions = skinReused
                ? lastSkin!.Contributions
                : contributions.Values
                    .Where(c => c.Touched || c.DiffuseWanted || c.Diffuse + c.Normal + c.Mask > 0)
                    .OrderBy(c => c.Material, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            LogPhaseBreakdown(tRunStart, tSetupEnd, skinReused ? 0 : byMaterial.Count);

            // Publish the glow recipes gathered above (empty dict if no indexed skin overlays).
            _skinGlowTargets = skinReused
                ? new Dictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>(lastSkin!.GlowTargets)
                : new Dictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>>(skinGlow);

            // Everything in `redirects` up to this point is the skin's — the shell adds its own below — so
            // this is the snapshot a later run reuses. Captured now, published only if this run publishes.
            var skinRedirectsThisRun =
                new Dictionary<string, string>(redirects, StringComparer.OrdinalIgnoreCase);

            // ── Second skin: one gear shell per Layer:Gear overlay ────────────
            // Built from the body model the character is CURRENTLY drawing (resolved live through
            // Penumbra) — a shell cut from any other body shape shows the body through it.
            List<object>? manipulations = null;
            _needFullRedraw = false;
            _secondSkinActive = false;
            // The three UI-facing locators are built into LOCALS and published as one step after the gear
            // phase (below), instead of being cleared here and refilled ~1.2 s later. That clear opened a
            // window in which the fields said "no shell was built" while one plainly was: the editor's Glow
            // locator reads GetShellMaterials every frame, saw the empty map mid-composite, and fired a
            // mask-glow-warmup recomposite — a second full composite, ~3.8 s, for a one-row colour tweak.
            // Holding the previous composite's values until this one has an answer closes it; they only ever
            // describe a shell that IS on the character, and the publish below still empties them when no
            // shell built this time.
            Dictionary<(string ModDir, string? Group, string? Option), List<string>>? nextShellMaterials = null;
            Dictionary<string, HashSet<string>>? nextContentMaterials = null;
            ShellDrawnProbe? nextShellDrawnCheck = null;
            bool shellBuilt = false;   // a gear shell was produced this composite (drives glasses reconcile)
            // The shell was built for invisible glasses we have not equipped YET (ChooseHost's pending
            // branch). The injection below then needs no follow-up recomposite — the redirect is already
            // in place, so the equip's own redraw lands straight on the finished shell.
            bool glassesPreHosted = false;
            // Which host the shell was published onto. The glasses are only worth equipping when the shell
            // rides the facewear slot, and the Emperor's ring only renders once it is actually WORN — so
            // each reconcile below is driven by where the shell went, not by the feature toggle alone.
            bool shellOnFacewear = false;
            // The accessory slots the shell published a CARRIER onto (may be several — each free accessory slot
        // is offered, and a natively-authored surface can only ride a carrier).
        List<string> shellCarrierSlots = [];
            var tGear = PhaseCounter.Begin();
            // maskShellMods too, not just gear overlays. A mod whose Masks tab was promoted to Cloth/Glow
            // while all its other layers stayed on Skin has NO gear overlay — that is the whole point of the
            // promotion at maskShellMods' second loop — so gating purely on gearOverlays skipped the mask
            // shell synthesis below and left the mask nowhere: the skin relief and diffuse passes had
            // already skipped it ("mask lives on the shell"), and the shell it was deferred to was never
            // built. The masked region then rendered as plain body skin, which is exactly what it looks
            // like. The synthesis adds to gearOverlays itself, and Build still no-ops on an empty list.
            // …and content packs, which have no overlay of any kind: their whole contribution is geometry.
            if (gearOverlays.Count > 0 || maskShellMods.Count > 0 || contentLayers.Count > 0)
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
                    // Whether the seed is a real GEAR sibling decides if its colorset means anything to a
                    // mask. A fabric's colours describe cloth and are worth inheriting; a SKIN overlay's
                    // describe skin, and inheriting those painted the mask in the body's own tone — which
                    // is the "mask renders the body skin texture" report, on a mod whose Masks tab was set
                    // to Cloth while its fabric stayed on Skin (the all-skin promotion above).
                    bool seededFromGear = seed.Entry != null;
                    if (seed.Entry == null) seed = allOverlays.FirstOrDefault(g => g.Entry.ModDirectory == mod);
                    if (seed.Entry == null) continue;   // no overlay to source a body type from

                    // The mask's own render mode: Cloth (character.shpk) by default, or the shader/scroll it
                    // was given (Glow ⇒ characterscroll.shpk). Null descriptor ⇒ plain Cloth, as before.
                    // The same instance the promotion loop and the fingerprint read — see maskDescByMod.
                    maskDescByMod.TryGetValue(mod, out var md);
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
                        // Its own Masks colorset if set, else inherit the FABRIC's — the colours the legacy
                        // merge would have used, so a mask with no colours of its own still shows. Only from
                        // a gear sibling: with none, null falls through to the neutral-white baseline
                        // (BuildRows' neutralWhenEmpty), which multiplies to the base diffuse instead of
                        // painting the mask a skin tone.
                        //
                        // The SKIN fallback (maskFallbackRows) deliberately does NOT make that distinction and
                        // inherits from a skin overlay too. The hazard here is a shell — a surface pushed off
                        // the body — being painted in body tones; on skin those same rows are exactly what the
                        // legacy _id merge selected for the very pixels the mask covers, so inheriting them
                        // reproduces the old colours rather than importing the wrong ones.
                        ColorTableRows = MaskRowsFor(seed.Entry)
                                      ?? (seededFromGear ? seed.Overlay.ColorTableRows : null),
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
                    log.Warning("[Proteus] {0} gear overlay(s) and {1} content piece(s) skipped: "
                              + "no character code yet", gearOverlays.Count, contentLayers.Count);
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
                        // Last chance to learn what the character is actually wearing. The walk at
                        // trigger time can land before the draw object is ready — right after load, or
                        // mid-redraw — and leave every map null. Seconds have passed since then (the
                        // blend loop ran), so a retry here usually succeeds where that one failed.
                        //
                        // This matters because "null" and "empty" mean opposite things to the host
                        // chooser and it cannot tell them apart on its own: an unknown met slot used to
                        // read as "no hat worn", so Proteus injected invisible glasses over the head
                        // slot and took the hat off. Retry first, and pass the maps through WITHOUT
                        // coalescing null away, so a still-failed walk stays legible as "unknown".
                        // Wrapped, because everything from here to the end of the shell build sits under
                        // one catch-all that reports "second skin build failed" and skips _needFullRedraw.
                        // RefreshEquippedModels blocks on the framework thread, so it throws on teardown
                        // or a cancelled frame queue — and an unhandled throw here would trade a failed
                        // RETRY for a lost SHELL plus the redraw that restores its host accessory. A
                        // failure just leaves the maps null, which the host chooser now reads as
                        // "unknown" and handles by refusing to replace anything.
                        if (_equippedPartModels == null)
                        {
                            var known = false;
                            try { known = RefreshEquippedModels(); }
                            catch (Exception ex)
                            {
                                log.Debug("[Proteus] second skin: equipped-model retry could not run ({0})",
                                    ex.GetType().Name);
                            }
                            if (!known)
                                log.Warning("[Proteus] second skin: no draw-object walk has succeeded yet — "
                                          + "host choice will avoid anything that replaces worn gear");
                        }

                        var equippedModels = _equippedPartModels
                            ?? new Dictionary<string, string>();
                        var equippedAccessories = _equippedAccessoryModels
                            ?? new Dictionary<string, string>();
                        var metModels = _equippedMetModels;
                        // The bare slots come from the same walk. Logged beside the gear because when a shell
                        // comes out empty these are usually what answers "cut from WHAT?" — a slot missing
                        // from BOTH lists is a slot the shell has no geometry for.
                        var bareBodyModels = _bareBodyModels ?? new Dictionary<string, string>();
                        log.Information("[Proteus] second skin: equipped part models [{0}], accessories [{1}], head/met [{2}], bare [{3}] ({4})",
                            string.Join(", ", equippedModels.Select(kv => $"{kv.Key}={kv.Value}")),
                            string.Join(", ", equippedAccessories.Select(kv => $"{kv.Key}={kv.Value}")),
                            metModels == null ? "unknown" : string.Join(", ", metModels),
                            string.Join(", ", bareBodyModels.Select(kv => $"{kv.Key}={kv.Value}")),
                            _equippedPartModels == null ? "cache null" : "cached");
                        // Shells ARE cut from these — SecondSkinService.ResolveHumanSurface filters this
                        // very list by part folder and picks the model declaring the overlay's material.
                        // (It said "observed only, nothing cuts from these yet" until now; that was stale
                        // from birth, since the commit adding the line added the resolver too.)
                        //
                        // Still logged, because the whole multi-surface story rests on this walk actually
                        // reporting them and this is the line that says whether it does: whether a Viera's
                        // ears show up, whether a face really does draw several models, and — for an eye
                        // overlay — whether anything here declares the iris material at all, since a
                        // surface that resolves to nothing is dropped with a warning and no shell.
                        log.Information("[Proteus] second skin: human part models [{0}]",
                            string.Join(", ", _humanPartModels ?? []));

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
                            invisibleGlassesSet, metModels, bodyShapes, maskShellMods, bareBodyModels,
                            _drawnRaceCode, activeMtrl,
                            InvisibleRing.Resolve(Plugin.DataManager, log)?.Variant,
                            InvisibleGlasses.Resolve(Plugin.DataManager, log)?.Variant,
                            _humanPartModels, contentLayers);
                        if (shells != null)
                        {
                            shellBuilt = true;
                            // Where the shell actually landed, read off the paths it published rather than
                            // re-derived: these drive which item we have to equip for it to render at all.
                            shellOnFacewear = shells.HostModelPaths.Any(
                                p => p.EndsWith("_met.mdl", StringComparison.OrdinalIgnoreCase));
                            // …and in WHICH slots, since a carrier only loads from the slot it published for,
                            // and there can be several now (each free accessory is offered as a carrier).
                            shellCarrierSlots = InvisibleRing.CarrierSlots
                                .Where(c => shells.HostModelPaths.Any(p =>
                                    p.Contains($"a{InvisibleRing.EmperorSetId:D4}", StringComparison.OrdinalIgnoreCase)
                                 && p.EndsWith($"_{c.Slot}.mdl", StringComparison.OrdinalIgnoreCase)))
                                .Select(c => c.Slot).ToList();
                            // Mirrors ChooseHost's pending-injection branch, and must keep mirroring it:
                            // feature on and the "_met" slot KNOWN empty means the shell was built for
                            // glasses we are about to equip. Unknown (null) is not empty — claiming a
                            // pre-host we never chose would make the reconcile below adopt glasses that
                            // were never injected, over a hat that was there all along.
                            glassesPreHosted = invisibleGlassesSet is int && metModels is { Count: 0 };
                            // Verified after publishing along with everything else — see VerifyRedirectsLive.
                            // This used to build a narrowed list of just the .mdl/.mtrl keys, on the reasoning
                            // that the textures hang off the materials so listing every ss_*.tex would bury
                            // the signal. That narrowing is gone because the skin textures now need checking
                            // too, and they are exactly the ones a body mod can take from us.
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

                            // A FORCED trigger that produced NO change at all is the user pressing
                            // recomposite and getting byte-identical output. That is precisely the state an
                            // in-place reload cannot repair: it never re-fetches an accessory .mdl, so a
                            // shell whose host model the game has not reloaded since we redirected it stays
                            // invisible, every retry is another no-op, and the manual button they are
                            // reaching for is the one thing guaranteed not to help.
                            //
                            // Gated on "nothing changed" rather than on `force` alone: a colorset edit is
                            // also forced, and treating THAT as a new model is what used to cost a redraw
                            // and its flicker on every colour change (see above). Here there is nothing to
                            // flicker away from — the output is identical either way.
                            bool nothingChanged = !shells.ModelChanged && !shapesChanged && !hostsChanged
                                               && !shells.ShellChanged;
                            _needFullRedraw = shells.ModelChanged || shapesChanged || hostsChanged
                                           || (force && nothingChanged);
                            if (force && nothingChanged)
                                log.Debug("[Proteus] second skin unchanged on a forced composite — redrawing "
                                        + "anyway, since an in-place reload can't reload a host accessory");
                            if (shells.ModelChanged)
                                log.Debug("[Proteus] second skin model changed — forcing a full redraw");
                            else if (shapesChanged)
                                log.Debug("[Proteus] second skin body shapes changed — forcing a full redraw");
                            else if (hostsChanged)
                                log.Debug("[Proteus] second skin host set changed — forcing a full redraw");
                            else if (shells.ShellChanged)
                                log.Debug("[Proteus] second skin material/textures changed — in-place reload");
                            nextShellMaterials = shells.ShellMaterials;
                            nextContentMaterials = shells.ContentMaterials;

                            // Materials to test, models to anchor the test against — see ShellDrawnProbe.
                            nextShellDrawnCheck = new ShellDrawnProbe(
                                [.. shells.Redirects.Keys.Where(k => k.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))],
                                [.. shells.Redirects.Keys.Where(k => k.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))]);

                            // Which of the hosts we APPENDED into, for PrimeUpstreamCache. Persisted, not
                            // just held in memory: the manifest outlives the session and masks these paths
                            // from the first composite after a restart, when nothing has been built yet to
                            // tell an append host from a carrier. Written only on a real change — this runs
                            // on every composite and config.Save() is disk I/O.
                            //
                            // LAST in this block, and under the same lock every other off-thread save takes
                            // (see _bodyModConfigLock). Save() serializes the whole Configuration, so an
                            // unsynchronized one can throw "collection was modified" while ClassifySurfaceMod mutates
                            // KnownBodyMods on its own thread — and thrown from higher up this block it would
                            // be swallowed as "second skin build failed" and skip _needFullRedraw, leaving a
                            // changed shell model to an in-place reload that never re-fetches an accessory
                            // .mdl. Everything load-bearing is already assigned above; this can only lose
                            // the persisted hint, which the next composite rewrites.
                            var appendHosts = new HashSet<string>(shells.AppendHostModelPaths, StringComparer.OrdinalIgnoreCase);
                            if (!appendHosts.SetEquals(_appendHostModelPaths))
                            {
                                _appendHostModelPaths = appendHosts;
                                lock (_bodyModConfigLock)
                                {
                                    config.AppendHostModelPaths = [.. appendHosts];
                                    config.Save();
                                }
                            }
                        }
                    }
                    catch (Exception ex) { log.Error(ex, "[Proteus] second skin build failed"); }
            }

            // Publish the locators in one step, now that the gear phase has an answer. Null ⇒ no shell was
            // built (no gear overlays at all, or the build threw), which is exactly when the editor SHOULD
            // see them empty — so the warm-up it triggers then is the real cold-boot case it was written for.
            //
            // This covers only the paths that REACH here. The two tear-downs that return earlier — the
            // plugin being disabled, and the "no enabled mods" branch above — clear them through
            // ClearShellLocators instead.
            _shellMaterials   = nextShellMaterials ?? new();
            _contentMaterials = nextContentMaterials ?? new(StringComparer.OrdinalIgnoreCase);
            _shellDrawnCheck  = nextShellDrawnCheck;

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
            if (gearOverlays.Count > 0 || contentLayers.Count > 0)
                log.Information("[Proteus] recomposite phases: second skin {0:F0}ms ({1} gear layer(s), "
                              + "{2} content piece(s))",
                    PhaseCounter.MsSince(tGear), gearOverlays.Count, contentLayers.Count);

            WriteManagedModJson(redirects, manipulations);

            // Publish only. Nothing is deleted in this composite: whatever this map supersedes is collected
            // at the top of the NEXT one, by which point the reload below has long since landed. See
            // PruneSupersededOutput for why an immediate prune could not be made safe.
            bool manifestConfirmedLive = ReloadAndRedrawWhenReady(redirects, RecordPublish(redirects));

            // Published: from here the manifest on disk is the output of exactly these inputs, so an ambient
            // trigger that hashes to the same thing has nothing to do. Set only on this path — a cancelled or
            // throwing run returns before it and leaves the previous (or no) fingerprint standing, so the
            // gate can never claim output that was not produced. The forced-work latch is settled here too:
            // whatever the user asked for is now in the published manifest.
            _lastCompositeFingerprint = fingerprint;
            // The skin half moves with it, and only here — a run that returned early or threw leaves the
            // previous set standing, so a remembered redirect always describes a manifest that was actually
            // published. ONE reference swap, so a concurrent reader can never pair this fingerprint with
            // another publish's redirects. Assigned even on a reuse, where it round-trips its own values.
            _lastSkinPublish = new SkinPublish(
                skinFingerprint, skinRedirectsThisRun, _channelContributions, _skinGlowTargets);
            Interlocked.Exchange(ref _forcePending, 0);
            Interlocked.Exchange(ref _skinForcePending, 0);

            // Widen the recorded base set to what this run ACTUALLY resolved. baseKeys is the published
            // manifest's keys plus the overlay material paths, but the blend loop resolves texture bases
            // through ResolveUpstream that we may read without publishing — the live manifest here carries
            // c0201f0001_fac_mask.tex and not the face's other channels, so a skin mod changing only one of
            // those would otherwise be judged "does not affect us" and skip a recomposite it needed.
            // _upstreamByGamePath is exactly "every path resolved as a base", and by this point it holds the
            // whole run. Over-inclusive by an append host's .mdl or two, which errs toward compositing.
            // Authoritative: this has seen the whole run, so it is the record allowed to retire paths
            // left over from a previous composite shape.
            //
            // NOT authoritative when the skin was reused: the blend loop is what resolves those extra
            // bases, and it did not run, so this run has seen only part of the picture. Retiring on it
            // would drop paths a later mod change needs to be judged against, and the mod would read as
            // "does not affect us" — a missed recomposite, which is exactly the failure the widening
            // above exists to prevent. Adding is still safe and still useful.
            RecordCompositeBaseKeys(baseKeys.Concat(_upstreamByGamePath.Keys), baseSignature,
                authoritative: !skinReused);

            // Reconcile the injected host items AFTER the redirect mod is live, so when the equip's redraw
            // loads the model it resolves straight to the shell (no visible frames, no bare ring). Each
            // passes whether the shell actually went to THAT host; the reconciles read the worn state
            // themselves. Glasses only count when the shell rides the facewear slot — on a race whose
            // facewear ships native the shell goes to the ring instead, and equipping a pair we don't host
            // on would just put real frames on the player's face.
            //
            // "Wanted" counts content packs as well as gear overlays, and has to: it is what separates "this
            // composite asked for a host and failed to build one" (transient — leave the carrier on, retry)
            // from "nothing wants a host at all" (settled — take it back off). A content-only look wants one
            // exactly as much as a shell does, and reading it off gearOverlays alone would unequip the
            // carrier its own geometry is riding the moment a build hiccuped.
            bool hostWanted = gearOverlays.Count > 0 || contentLayers.Count > 0;
            RememberHostDecision(hostWanted, shellBuilt, shellOnFacewear,
                                 shellBuilt ? shellCarrierSlots : []);
            ReconcileInvisibleGlasses(hostWanted, shellBuilt, shellOnFacewear, glassesPreHosted);
            ReconcileEmperorRing(hostWanted, shellBuilt, shellBuilt ? shellCarrierSlots : []);

            // Every path, not just the shell's. The shell paths were verified from the start because a worn
            // accessory is obviously contested; the skin textures were not, on the unexamined assumption that
            // a path we redirect is a path we own. It isn't — the body mod invented chara/bibo_mid_*.tex and
            // still publishes it, so we hold those only by priority, and losing one is invisible everywhere
            // else in the log. The shell paths are in this map too, so this subsumes the old narrower check.
            //
            // Below the reconciles on purpose. It is pure diagnostics and can block for up to
            // VerifySettleTimeout waiting on Penumbra, while those two equip and unequip real items and want
            // to run as soon after the publish as possible.
            //
            // Which is also why it takes ct: the reconciles equip and unequip, Glamourer fires state events,
            // and OnGlamourerStateChanged can start a NEW composite that republishes the manifest. Judging
            // our resolves against a superseded expectation would manufacture exactly the false accusation
            // this check is hardened against, so a cancelled token means stand down and say nothing.
            VerifyRedirectsLive(redirects, manifestConfirmedLive, ct);

            // And one step further out than that check can see: winning the path is not the same as the game
            // having drawn what is behind it. Fires its own delayed task — the redraw is still in flight here.
            SchedulePostRedrawShellCheck();

            LastResult = new CompositorResult
            {
                Success = true,
                // On a skin reuse the loop never incremented this, but the textures ARE published — the
                // redirects were carried forward. Report what the manifest actually carries, or the status
                // window says "0 textures" for a character wearing a full composite.
                TexturesPatched = skinReused ? skinRedirectsThisRun.Count : texturesPatched,
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
            // What is published no longer corresponds to any known set of inputs, so the next ambient
            // trigger must actually composite rather than trust a fingerprint from before the failure.
            _lastCompositeFingerprint = null;
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

    // ── Blend sub-phases ──────────────────────────────────────────────────────
    // "blend" in the phases line is a RESIDUAL — composite wall time minus the stages measured inside it
    // — so for a long time it was one number covering every per-pixel pass, the seam map, island
    // labelling, ambient occlusion and the output hashing. Once the decode cache stopped thrashing it
    // became the largest item in the run (3.3s of a 5.1s composite) and there was nothing to attribute
    // it to. These four cover the well-bounded blocks; whatever is left over is the per-overlay kernels.
    //
    // Same caveat as the foreground decode counters: materials composite in parallel, so these SUM across
    // workers and only line up with wall time in the single-material case.
    private readonly PhaseCounter blendIslandStats = new();
    private readonly PhaseCounter blendSeamStats   = new();
    private readonly PhaseCounter blendAoStats     = new();
    private readonly PhaseCounter blendTagStats    = new();

    // Inside AO, which measured at 81% of blend once the coarse counters landed. Two candidates, and
    // guessing between them has a poor track record on this pipeline: the garment silhouette is rebuilt
    // per mod with no cache, and the island-restricted blur does its per-island cropping serially.
    private readonly PhaseCounter blendSilhouetteStats = new();
    private readonly PhaseCounter blendBlurStats       = new();

    private void ResetBlendStats()
    {
        blendIslandStats.Reset();
        blendSeamStats.Reset();
        blendAoStats.Reset();
        blendTagStats.Reset();
        blendSilhouetteStats.Reset();
        blendBlurStats.Reset();
    }

    /// <summary>Time a seam-map lookup. A hit is near-free; a miss is a ~1s build, and the two are
    /// indistinguishable in the log without this.</summary>
    private int[]? TimedSeamSource(IReadOnlyList<UvSeamMapService.SeamModel> models, int w, int h, int reach)
    {
        var t = PhaseCounter.Begin();
        try { return seamMaps.SeamSource(models, w, h, reach); }
        finally { blendSeamStats.Stop(t); }
    }

    /// <summary>Time a <see cref="ContentTag"/> call. Each one FNV-hashes a whole 64 MB output buffer and
    /// there are four per material, so this is real money hiding inside "blend".</summary>
    private string TimedContentTag(byte[] data, params int[] salt)
    {
        var t = PhaseCounter.Begin();
        try { return ContentTag(data, salt); }
        finally { blendTagStats.Stop(t); }
    }

    private void LogPhaseBreakdown(long runStart, long setupEnd, int materialCount)
    {
        var totalMs     = PhaseCounter.MsSince(runStart);
        var compositeMs = PhaseCounter.MsSince(setupEnd);
        var setupMs     = totalMs - compositeMs;

        var decode   = textureLoader.DecodeStats;
        var hits     = textureLoader.DecodeHitStats;
        var blocked  = textureLoader.DecodeBlockedStats;
        var nativeD  = textureLoader.DecodeNativeStats;
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

        // The AO timer brackets the whole per-mod sweep, which contains the seam-map and content-tag work
        // measured separately — so subtract those out of it rather than reporting the same milliseconds
        // under two headings. "rest" is then the per-overlay kernels and everything else unattributed:
        // the overlay blend passes, mask compositing, base clones, and whatever is genuinely left.
        var aoMs   = Math.Max(0, blendAoStats.Ms - (blendSeamStats.Ms + blendTagStats.Ms));
        var restMs = blendMs - (blendIslandStats.Ms + blendSeamStats.Ms + aoMs + blendTagStats.Ms);
        // The two measured pieces inside AO; "apply" is what remains of it (ApplyAmbientOcclusion,
        // ApplyNormalIndent, the coveredAbove merge, and the mask combine).
        var aoApplyMs = Math.Max(0, aoMs - (blendSilhouetteStats.Ms + blendBlurStats.Ms));

        log.Information(
            "[Proteus] recomposite phases: setup {0:F0}ms | decode-wait {1:F0}ms ({2} miss, {3} hit, {4} blocked) | " +
            "prefetch {5:F0}ms bg (decode work {6:F0}ms, {7} native of {8}) | remap {9:F0}ms ({10}) | " +
            "blend {11:F0}ms (islands {12:F0} | seam {13:F0}/{14} | ao {15:F0} [sil {16:F0}/{17} + blur {18:F0}/{19} " +
            "+ apply {20:F0}] | tag {21:F0}/{22} | rest {23:F0}) | " +
            "swizzle {24:F0}ms | write {25:F0}ms ({26} files, {27:F0} MB) | composite {28:F0}ms | total {29:F0}ms | " +
            "{30} material(s) | cache {31} entries, {32:F0} MB, {33} evicted (budget {34:F0} MB)",
            setupMs, wait.Ms, decode.Calls, hits.Calls, blocked.Calls,
            prefetch.Ms, decode.Ms, nativeD.Calls, decode.Calls, remap.Ms, remap.Calls,
            blendMs, blendIslandStats.Ms, blendSeamStats.Ms, blendSeamStats.Calls, aoMs,
            blendSilhouetteStats.Ms, blendSilhouetteStats.Calls, blendBlurStats.Ms, blendBlurStats.Calls,
            aoApplyMs, blendTagStats.Ms, blendTagStats.Calls, restMs,
            swizzle.Ms, write.Ms, write.Calls, write.Bytes / (1024.0 * 1024.0),
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
        if (repairing)
            log.Warning("[Proteus] Managed mod at \"{0}\" was missing its {1} — recreating it",
                managedModDir, PenumbraModMeta.MetaFile);

        WriteManagedModFiles();
        RegisterManagedMod(repairing ? "Repaired" : "Created");
    }

    /// <summary>
    /// The managed mod's folder and a FRESH <see cref="PenumbraModMeta.MetaFile"/> — everything
    /// Penumbra needs to list the mod, and nothing else.
    /// <para/>
    /// DESTRUCTIVE, and only legitimate when there is no readable manifest to lose. From
    /// <see cref="PenumbraModMeta.SingleFileVersion"/> on, the manifest is also where the redirects
    /// live (as <c>DefaultData</c>), and it carries the <c>Identifier</c> Penumbra keys the mod by —
    /// a fresh one has neither, so writing this over a manifest that parses unpublishes everything we
    /// own and orphans the mod's settings. Callers must gate on
    /// <see cref="PenumbraModMeta.HasReadableManifest"/>; <see cref="WriteManagedModJson"/> is the
    /// non-destructive way to touch a live folder, preserving every key it doesn't own.
    /// </summary>
    private void WriteManagedModMeta()
    {
        Directory.CreateDirectory(managedModDir);
        Directory.CreateDirectory(Path.Combine(managedModDir, "textures"));

        // Through AtomicWrite like every other manifest write, NOT File.WriteAllText: that truncates the
        // target in place and refills it, so a crash mid-write leaves a half or zero-filled meta.json —
        // and a manifest Penumbra can't parse means it drops the mod entirely, which is the failure this
        // whole repair path exists to recover from. No reason to be the one who causes it.
        PenumbraModMeta.AtomicWrite(
            Path.Combine(managedModDir, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(
                SidecarDiscoveryService.ManagedModDir, "Proteus",
                "Managed by the Proteus overlay compositor plugin."));
    }

    /// <summary>
    /// The manifest plus an empty redirect set, for a mod being created from nothing. Only safe from
    /// <see cref="EnsureManagedModExists"/>, which runs BEFORE a composite's no-unpublish region and
    /// is always followed by a republish.
    /// </summary>
    private void WriteManagedModFiles()
    {
        WriteManagedModMeta();
        WriteManagedModJson(new Dictionary<string, string>());
    }

    /// <summary>
    /// Whether Penumbra currently LISTS the managed mod. Null when the query itself failed (Penumbra
    /// down, IPC error) — that says nothing about registration, so a caller must not read it as
    /// "missing" and start churning AddMod. Deliberately not called on the healthy path: the whole
    /// mod list is marshalled across IPC, and <see cref="CheckManagedModHealth"/> already has a
    /// one-mod symptom check that says when it is worth asking.
    /// </summary>
    private bool? IsListedByPenumbra()
    {
        var mods = penumbra.GetAllMods();
        if (mods == null) return null;
        return mods.Keys.Any(d => string.Equals(d, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the "Penumbra took the mod but still won't list it" repair has actually rewritten the
    /// manifest this session. One attempt is the useful one: if a freshly written manifest doesn't get
    /// the mod loaded, repeating it every composite won't either.
    /// <para/>
    /// Set ONLY where a rewrite really happens. A pass that inspects the manifest and decides against
    /// rewriting must not consume it: the two are hours apart in the case that matters — an early
    /// false alarm (Penumbra polled mid-rediscovery, manifest perfectly fine) would otherwise disarm
    /// the repair for the rest of the session, so a manifest corrupted later by a crash could never be
    /// fixed and the mod would stay unlistable until the plugin is reloaded.
    /// </summary>
    private bool _manifestRewriteAttempted;

    /// <summary>
    /// Whether the "unlisted, but the manifest reads fine" notice has been logged this session. Its own
    /// flag rather than <see cref="_manifestRewriteAttempted"/>, so silencing a repeat of the message
    /// cannot also silence the repair. The condition itself stays visible either way —
    /// <see cref="CheckManagedModHealth"/> reports it every composite for as long as it lasts.
    /// </summary>
    private bool _manifestIntactNoticeLogged;

    /// <summary>
    /// The priority to assert whenever we (re)establish the managed mod's settings. Never below what
    /// <see cref="TryRaisePriorityAbove"/> has already achieved this session: writing the config
    /// baseline over a completed raise hands the contested paths straight back to whatever outranked
    /// us, and <see cref="_highestPriorityRaiseAttempted"/> would then refuse to raise again — it
    /// latches on the attempt and only clears once every redirect is live, which by then it isn't.
    /// The mod would sit at the baseline for the rest of the session while the log blamed a
    /// temporary collection for a change we undid ourselves.
    /// </summary>
    private int WantedManagedModPriority
        => Math.Max(config.ManagedModPriority, _highestPriorityRaiseAttempted);

    /// <summary>
    /// AddMod the managed directory, then enable it at our priority in the player's collection.
    /// The error codes are logged rather than dropped: if AddMod fails (folder outside Penumbra's
    /// root, say) nothing downstream can work, and silence there is what made this hard to diagnose.
    /// </summary>
    private void RegisterManagedMod(string verb)
    {
        var ec = penumbra.AddModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Information("[Proteus] AddMod({0}) -> {1}", managedModDir, ec);

        if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
        {
            log.Warning("[Proteus] Penumbra refused to add the managed mod ({0}). It is usually because "
                      + "\"{1}\" is not inside Penumbra's mod root — overlays cannot apply until it is.",
                ec, managedModDir);
        }
        else if (!_manifestRewriteAttempted && IsListedByPenumbra() == false)
        {
            // Success from AddMod means the CALL worked, not that the mod LOADED — Penumbra's own
            // contract: "success does only imply a successful call, not a successful mod load"
            // (IPenumbraApiMods.AddMod). The way that happens is a manifest Penumbra can't read: it
            // takes the request and drops the folder. EnsureManagedModExists would never notice,
            // because meta.json exists on disk — it just can't be parsed. Without this rewrite the
            // repair has nothing left to try and every composite re-runs the same failing AddMod.
            //
            // The rewrite latch is tested FIRST so that once the one useful attempt is spent we don't
            // marshal the whole mod list on every later call to learn something we can no longer act on.
            // The condition itself keeps being reported either way — CheckManagedModHealth logs it on
            // every composite for as long as it lasts.
            //
            // Rewriting is only justified when the manifest is what Penumbra choked on. A manifest that
            // PARSES is not: from FileVersion 4 the redirects live in it as DefaultData, so replacing it
            // with a fresh one unpublishes every path we own — and this runs mid-composite from the
            // health check, inside the region where a cancellation would strand them unpublished (see
            // the "NOTHING IS UNPUBLISHED HERE" block in Recomposite). It would also drop the Identifier
            // Penumbra keys the mod by, orphaning its settings. So: repair the unreadable case, and for
            // the readable one say what we know and stop, rather than break the mod to prove a theory.
            if (PenumbraModMeta.HasReadableManifest(managedModDir))
            {
                // Deliberately does NOT set _manifestRewriteAttempted — see that field. This costs a
                // mod-list query per composite for as long as the state lasts, which is the right trade:
                // overlays are already not applying, and keeping the repair armed matters more.
                if (!_manifestIntactNoticeLogged)
                {
                    _manifestIntactNoticeLogged = true;
                    log.Warning("[Proteus] Penumbra accepted the managed mod but does not list it, and its "
                              + "\"{0}\" reads fine — so the manifest is not what is stopping the load. Check "
                              + "that \"{1}\" is inside Penumbra's mod root, then press Rediscover Mods in "
                              + "Penumbra's settings.", PenumbraModMeta.MetaFile, managedModDir);
                }
            }
            else
            {
                _manifestRewriteAttempted = true;
                log.Warning("[Proteus] Penumbra accepted the managed mod but does not list it — its \"{0}\" "
                          + "is unreadable. Rewriting it and retrying once.", PenumbraModMeta.MetaFile);

                // Safe here precisely because nothing readable is being overwritten: an unparseable
                // manifest publishes nothing, so there is no live redirect set to lose.
                WriteManagedModMeta();
                ec = penumbra.AddModDirectory(SidecarDiscoveryService.ManagedModDir);
                log.Information("[Proteus] AddMod retry -> {0}", ec);

                if (IsListedByPenumbra() == false)
                    log.Warning("[Proteus] Penumbra still does not list the managed mod after its \"{0}\" was "
                              + "rewritten — overlays cannot apply. Check that \"{1}\" is inside Penumbra's mod "
                              + "root, then press Rediscover Mods in Penumbra's settings.",
                        PenumbraModMeta.MetaFile, managedModDir);
            }
        }

        // Log which collection the new mod was enabled in, and where it landed. Both are the first
        // things to check when composited textures don't show up: the mod has to be enabled in the
        // collection the player is actually using, and the folder has to be under Penumbra's root.
        var coll = penumbra.GetPlayerCollection();
        if (!coll.HasValue)
        {
            log.Warning("[Proteus] {0} managed mod at \"{1}\", but the player's collection could not be "
                      + "determined — it has not been enabled anywhere. Overlays will not apply until it is.",
                verb, managedModDir);
            return;
        }

        var (collId, collName) = coll.Value;
        var priority = WantedManagedModPriority;

        // Enabling is where an unregistered mod actually shows up as a failure: TrySetMod answers
        // ModMissing and changes nothing. Reporting "enabled in collection …" without reading that
        // back is the same false claim the old health check made — and on this path it would be the
        // ONLY line printed per composite, so a log reader would rule registration out and go looking
        // somewhere else entirely.
        var enabled = penumbra.SetModEnabled(collId, SidecarDiscoveryService.ManagedModDir, true);
        if (enabled is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
        {
            log.Warning("[Proteus] {0} managed mod at \"{1}\", but Penumbra would not enable it in "
                      + "collection \"{2}\" ({3}) — overlays will not apply.",
                verb, managedModDir, collName, enabled);
            return;
        }

        penumbra.SetModPriority(collId, SidecarDiscoveryService.ManagedModDir, priority);
        log.Information("[Proteus] {0} managed mod at \"{1}\", enabled in collection \"{2}\" ({3}) at priority {4}",
            verb, managedModDir, collName, collId, priority);
    }

    private void CheckManagedModHealth(List<OverlayEntry> overlayEntries)
    {
        var collId = penumbra.GetPlayerCollectionId();
        if (!collId.HasValue) return;

        var settings = penumbra.GetModSettings(collId.Value, SidecarDiscoveryService.ManagedModDir);
        if (settings == null)
        {
            // Two different failures land here and they need different repairs. Penumbra may not have
            // the mod AT ALL, in which case there is nothing to enable — TrySetMod answers ModMissing
            // and the state is unchanged; or it has the mod with no settings yet in this collection,
            // where enabling is the whole fix. This branch used to assume the second and only ever
            // call SetModEnabled, so the first case looped: "re-adding" every composite, never adding.
            //
            // This is also the ONLY place that asks Penumbra for its mod list, and it is the right one:
            // null settings is the cheap one-mod symptom of an unregistered mod, so the expensive
            // whole-list query is paid for exactly when something is already wrong, never per composite.
            log.Warning("[Proteus] Managed mod has no settings in the player collection — repairing");
            if (IsListedByPenumbra() == false)
            {
                log.Warning("[Proteus] Penumbra does not list the managed mod at \"{0}\" — registering it",
                    managedModDir);
                RegisterManagedMod("Re-registered");   // enables and sets priority itself
                return;
            }

            var ec = penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
            if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                log.Warning("[Proteus] Managed mod could not be enabled in the player collection ({0}) "
                          + "— composited textures will not apply", ec);
            else
                penumbra.SetModPriority(collId.Value, SidecarDiscoveryService.ManagedModDir, WantedManagedModPriority);
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

        lock (_manifestLock)
            PenumbraModMeta.WriteRedirects(
                managedModDir, SidecarDiscoveryService.ManagedModDir, files, swaps: null, manipulations: manipulations);
    }

    /// <summary>
    /// Serialises every write to the managed mod's manifest, and — the reason it exists — lets
    /// <see cref="PrimeUpstreamCache"/> hold a read-modify-write across its whole narrow-and-restore span.
    ///
    /// Composites genuinely overlap (see <see cref="_compositesInFlight"/>), so without this a prime could
    /// read the live manifest, another composite could publish, and the prime's restore would then write the
    /// pre-publish copy back — silently discarding a published manifest while the fingerprint recorded it as
    /// live, which the unchanged-inputs gate would then refuse to correct.
    ///
    /// Monitor is reentrant per thread, so the prime's own nested WriteManagedModJson calls are free.
    /// </summary>
    private readonly object _manifestLock = new();

    /// <summary>
    /// The last disk path Penumbra resolved each game path to that WASN'T our own output — i.e. the real
    /// upstream mod file a composite reads as its base. Recorded on every clean resolution and used only
    /// when a resolution comes back pointing at the managed mod, which happens in the window between
    /// clearing the manifest and Penumbra finishing the reload.
    ///
    /// Falling back to the game's SqPack there (what the old guard did) is wrong for exactly the textures
    /// that matter: a skin mod's paths are invented — chara/bibo_mid_base.tex is in no game index — so
    /// "fall back to vanilla" means "fail to load". Remembering the upstream keeps the composite reading
    /// the same base it would have read after the reload landed.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _upstreamByGamePath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this run's output is already sitting at <paramref name="outPath"/>, so the write can be
    /// skipped. Safe because the filename carries a <see cref="ContentTag"/> over the exact bytes plus
    /// <see cref="OutputFormatVersion"/>: a matching name means matching content, so the file already
    /// there IS what we were about to write. Skipping saves the encode (64 MB of BC7 per skin channel)
    /// and, just as importantly, leaves LastWriteTime alone — sync plugins invalidate their file cache on
    /// any modtime change (PSync: FileCacheManager.ValidateFileCacheEntity), so rewriting identical bytes
    /// would force a re-hash of the whole skin set on every slider drag.
    /// <para/>
    /// The name is only a promise about content, though, so the file is also checked for COMPLETENESS.
    /// <c>TextureLoader.WriteWithRetry</c> publishes through a .tmp and an atomic move, so our own
    /// writing cannot leave a torn file here — but anything that damages one AFTERWARDS (antivirus, a
    /// half-restored backup, a failing disk, a sync tool mid-copy) is permanent without this: a bare
    /// existence test re-approves the damaged file on every composite from then on, the composite reports
    /// success, the redirect goes live, and the game quietly fails to load the texture. Rewriting costs
    /// one encode; not rewriting costs the user a broken skin until they delete the file by hand.
    /// </summary>
    private bool AlreadyWritten(string outPath)
    {
        bool complete;
        try
        {
            // ONE stat for the whole check: FileInfo caches Exists/Length from its first access, and the
            // IsCompleteTex overload below reuses that snapshot instead of re-stat'ing the path.
            var fi = new FileInfo(outPath);
            if (!fi.Exists) return false;      // absent is the normal first-run case, not damage — no warning
            complete = TextureLoader.IsCompleteTex(fi);
        }
        catch { return false; }   // unreadable — rewrite rather than trust it

        if (complete) return true;

        log.Warning("[Proteus] output present but incomplete — rewriting: {0}", outPath);
        return false;
    }

    /// <summary>
    /// True when <paramref name="diskPath"/> is a file this plugin wrote into the managed mod. Compared on
    /// canonical full paths so a forward/back-slash or relative-segment mismatch can't slip past.
    ///
    /// Undecidable input answers TRUE. The two ways to be wrong are not symmetric: rejecting a genuine
    /// upstream costs one uncomposited channel for one run, self-correcting on the next composite, while
    /// accepting our own output as a base composites the previous composite again — cumulative, silent,
    /// and only noticed once the skin has visibly drifted. So the fallbacks below bias toward "ours".
    /// </summary>
    private bool IsOwnOutput(string? diskPath)
    {
        var diskFull    = TryCanonicalise(diskPath);
        var managedFull = TryCanonicalise(managedModDir);

        if (diskFull != null && managedFull != null)
            return IsUnderRoot(diskFull, managedFull);

        // Something wouldn't canonicalise. A literal separator-normalised compare still catches the
        // ordinary case; only if even that is unevaluable do we fall back to the pessimistic answer.
        var d = diskPath?.Replace('/', Path.DirectorySeparatorChar);
        var m = managedModDir?.Replace('/', Path.DirectorySeparatorChar);
        if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(m))
            return IsUnderRoot(d, m);

        log.Warning("[Proteus] could not tell whether \"{0}\" is our own output — treating it as ours "
                  + "rather than risk compositing onto a previous composite", diskPath ?? "(null)");
        return true;
    }

    private static bool IsUnderRoot(string path, string root)
        => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                           StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar),
                         root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string? TryCanonicalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); } catch { return null; }
    }

    /// <summary>
    /// How long <see cref="VerifyRedirectsLive"/> may spend waiting for the published manifest to go live
    /// before giving up and calling the result undetermined. Usually unspent: the reload has normally landed
    /// by the time it runs, so the first pass finds a match and never sleeps.
    ///
    /// Much shorter than <see cref="PrimeLiveTimeout"/>, and deliberately so: the prime's answer decides which
    /// skin the composite is BUILT on, so it is worth blocking for. This one only decides what a log line
    /// says, runs after the redraw and the host-item reconciles, and reports "undetermined" rather than
    /// guessing — so a slow rebuild should cost the user a vague message, not a stall.
    /// </summary>
    private static readonly TimeSpan VerifySettleTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// After publishing, ask Penumbra what it ACTUALLY resolves each path we redirect to, and say so.
    ///
    /// Publishing a path is not the same as WINNING it. A shell hosted on a worn accessory claims a path the
    /// player's own mods can claim too — "[neo] Mine" redirecting chara/accessory/a0016/model/c0201a0016_nek.mdl
    /// is the ordinary case, not an exotic one — and a composited skin texture claims a path the BODY MOD
    /// itself owns, which is not an edge case at all: chara/bibo_mid_base.tex belongs to Bibo+, and Proteus
    /// only sits on top of it by out-prioritising it. Penumbra settles both by mod priority, and if we lose,
    /// NOTHING else we log notices: the composite blends, the manifest publishes, the reload succeeds, the
    /// redraw fires, and the character renders the other mod's texture. Every line reads healthy.
    ///
    /// It is also per PATH, which is why the failure presents so oddly — the normal can be ours while the
    /// diffuse is not, and the body then shows an overlay's relief over untouched skin colour.
    ///
    /// It converts that silent loss into the one message that names the winning file — which is also the fix,
    /// since the answer is to raise the managed mod's priority above whatever owns the path.
    ///
    /// ANCHORED ON THE EXPECTED TARGET, which is what makes the answer trustworthy. This runs straight after
    /// <c>ReloadModDirectory</c>, which Penumbra processes asynchronously, and the caller does not reliably
    /// wait first: under DisableAutoRedraw nothing waits at all, and WaitForManifestLive returns immediately
    /// whenever no redirect changed. Sampling until the answer merely STOPS MOVING is not enough — a reload
    /// still sitting in Penumbra's queue gives a perfectly stable PRE-reload answer, so "settled" and "never
    /// started" read identically, and a newly published path would be reported as lost to the very mod it was
    /// published over.
    ///
    /// So compare against the file the manifest names instead. A path resolving to exactly what we just
    /// published is proof, needing no settling at all, and one such path proves the reload landed.
    ///
    /// That is necessary but NOT sufficient, because the rebuild is progressive rather than atomic — see
    /// <see cref="SettleUpstreams"/>, where a contested path was observed resolving to three different mods
    /// on three loads with identical settings. A neighbour reading correctly does not mean this path has been
    /// reconsidered yet. So a path that does NOT match must also repeat its answer before it is called lost,
    /// which is the other half and the reason both mechanisms are here.
    ///
    /// <paramref name="manifestConfirmedLive"/> is the second source of liveness, and it is what makes the
    /// worst case reportable instead of merely slow. A local match cannot exist when we are outranked on
    /// EVERY path, so on its own this check could never tell that apart from a reload still in Penumbra's
    /// queue: it re-read the same settled answer for the whole budget and then shrugged. The caller may
    /// already have proven liveness by probing a changed path, and when it has, "nothing resolves to us"
    /// means exactly what it looks like.
    ///
    /// What stays unproven is reported as UNDETERMINED and never as a loss, and the message says so without
    /// naming a cause it hasn't established — this signal is meant to be actionable (it names the mod to
    /// raise priority over), so a confident wrong answer would point at an innocent mod.
    /// </summary>
    private void VerifyRedirectsLive(IDictionary<string, string> redirects, bool manifestConfirmedLive,
                                     CancellationToken ct)
    {
        if (redirects.Count == 0 || ct.IsCancellationRequested) return;

        // What each path SHOULD resolve to, canonicalised once. Same construction WaitForManifestLive uses.
        var expected = new Dictionary<string, string>(redirects.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, rel) in redirects)
            if (TryCanonicalise(Path.Combine(managedModDir, rel)) is { } full)
                expected[gamePath] = full;
        if (expected.Count == 0) return;

        // The raw resolve per path from the final round, so the warnings below quote what Penumbra actually
        // said rather than a canonicalised form of it. An unredirected resolve echoes the game path straight
        // back, which canonicalises against the working directory into something meaningless — fine for the
        // comparison (it cannot equal our output) but not for a message.
        var raw  = new Dictionary<string, string?>(expected.Count, StringComparer.OrdinalIgnoreCase);
        var prev = new Dictionary<string, string?>(expected.Count, StringComparer.OrdinalIgnoreCase);
        var matched  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unstable = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            matched.Clear();
            unstable.Clear();
            foreach (var (gamePath, full) in expected)
            {
                var disk = penumbra.ResolvePlayer(gamePath);
                if (TryCanonicalise(disk) is { } got
                    && string.Equals(got, full, StringComparison.OrdinalIgnoreCase))
                {
                    // Already the end state we are waiting for; no repeat read can improve on it.
                    matched.Add(gamePath);
                }
                else if (!prev.TryGetValue(gamePath, out var was)
                      || !string.Equals(was, disk, StringComparison.OrdinalIgnoreCase))
                {
                    // A path that is NOT ours has to say so twice. Mid-rebuild it reports whichever
                    // contributor has been applied so far — see SettleUpstreams, where the same path was
                    // observed resolving to three different mods on three loads with identical settings.
                    unstable.Add(gamePath);
                }
                prev[gamePath] = disk;
                raw[gamePath]  = disk;
            }

            // Two things must hold before a verdict is worth anything. The manifest has to be live, and each
            // non-matching path has to have stopped moving — a match alone cannot give the second, because
            // the rebuild applies progressively and a path can still show an intermediate winner while its
            // neighbour already reads correctly.
            //
            // Liveness comes from either source: the caller may already have PROVEN it by probing a changed
            // path, and failing that a path resolving to our file proves it here. Both are needed, because
            // each covers the other's blind spot — the caller has nothing to probe when no redirect changed,
            // and the local check finds nothing when we are outranked everywhere. Without the caller's
            // answer, that second case is indistinguishable from a reload still sitting in Penumbra's queue,
            // and used to burn the entire budget re-reading a question already settled.
            //
            // When everything matches — the normal case — unstable is empty on the first pass and this costs
            // one IPC per redirect and no sleep at all.
            bool live = manifestConfirmedLive || matched.Count > 0;
            if ((live && unstable.Count == 0) || sw.Elapsed >= VerifySettleTimeout) break;
            if (ct.IsCancellationRequested) return;
            Thread.Sleep((int)PrimeSettleInterval.TotalMilliseconds);
        }

        if (!manifestConfirmedLive && matched.Count == 0)
        {
            // Deliberately does NOT claim which. Two different faults produce this exact observation, and
            // saying "the reload had not landed" — as this used to — sends someone chasing Penumbra timing
            // when the real answer may be that they are outranked on every path they publish.
            log.Warning("[Proteus] redirect check UNDETERMINED after {0}ms — not one of {1} published path(s) "
                      + "resolves to the file we wrote for it. EITHER the reload has not landed yet, OR we "
                      + "are outranked on every path we publish; this check cannot tell them apart. A "
                      + "\"manifest live after Nms\" line above means the reload DID land, so the cause is "
                      + "priority.",
                sw.ElapsedMilliseconds, expected.Count);
            return;
        }

        int lost = 0;
        var selfServed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unclaimed = new List<string>();
        foreach (var gamePath in expected.Keys)
        {
            if (matched.Contains(gamePath))
            {
                // Re-arm the chat notice: if this path is ever taken again — the user reinstalls the mod, or
                // a different one outranks us — that is news, and should be said again.
                _reportedRedirectLosses.TryRemove(gamePath, out _);
                continue;
            }
            if (unstable.Contains(gamePath)) continue;

            var disk = raw[gamePath];

            // OUR OWN FOLDER IS NOT A CONFLICT, and must never reach the owner set below. `owners` feeds
            // TryRaisePriorityAbove and ManagedModDir is literally "Proteus", so a self-resolve had us read
            // our own priority, add one, and write it back to ourselves — then force a recomposite that
            // computed the same thing one higher. The attempted-target latch cannot stop that: each raise
            // moves `highest` up with it, so the target is strictly increasing and always novel. Observed
            // climbing 1000 -> 1001 -> … at roughly one composite per second, telling the user in chat each
            // time that "Proteus" was overriding Proteus, and persisting the climbed value to the config.
            //
            // What it actually means is that Penumbra is still serving an EARLIER manifest's file for this
            // path — same mod, different name. No priority can order one Proteus file above another, so
            // there is nothing for the user to fix and nothing worth a chat line; the next reload settles it.
            if (string.Equals(ModFolderOf(disk), SidecarDiscoveryService.ManagedModDir,
                              StringComparison.OrdinalIgnoreCase))
            {
                selfServed.Add(gamePath);
                log.Warning("[Proteus] redirect not yet current: {0} resolves to {1} — our own mod, but not "
                          + "the file this manifest names, so Penumbra is still serving an earlier publish "
                          + "for it. Not a conflict; no priority change can affect it.", gamePath, disk!);
                continue;
            }

            lost++;

            // TWO DIFFERENT FAULTS, and conflating them produced nonsense. Penumbra echoes the game path
            // straight back when NOTHING provides it — our entry simply isn't in the winning collection —
            // and a null answer means the same. Treating that as "another mod wins" printed the path as its
            // own culprit: «"chara/accessory/a0053/texture/ss_0_id.tex" overrides ss_0_id.tex», advising the
            // user to outrank a mod that does not exist. Nobody is winning these; they are not published to
            // the collection at all, which is a different problem with a different fix.
            bool nobodyProvides = disk == null
                               || string.Equals(disk, gamePath, StringComparison.OrdinalIgnoreCase);

            if (nobodyProvides)
            {
                unclaimed.Add(gamePath);
                log.Warning("[Proteus] redirect NOT live: {0} resolves to nothing — our entry is not in the "
                          + "winning collection at all, so this is not another mod outranking us. The managed "
                          + "mod is disabled, or the path was withdrawn between publish and check.", gamePath);
                continue;
            }

            log.Warning("[Proteus] redirect NOT live: {0} resolves to {1} — another mod wins this path, so "
                      + "what we composited for it cannot render. Raise the Proteus managed mod's priority "
                      + "in Penumbra above the mod that owns it.", gamePath, disk!);

            if (ModFolderOf(disk) is { } owner) owners.Add(owner);
            else NotifyRedirectLost(gamePath, disk);   // a real file, but not under the mod root
        }

        // ONE line, not one per path. The shell alone publishes a model, a material and up to five textures,
        // and the previous per-path notice turned a single fault into a seven-message wall of red.
        if (unclaimed.Count > 0 && _reportedRedirectLosses.TryAdd("\0unclaimed", "1"))
        {
            var msg = string.Format(Loc.Localize("Chat.UnclaimedRedirects.Fmt",
                "[Proteus] {0} of the files Proteus publishes aren't reaching the game — they resolve to "
                + "nothing at all, which usually means the \"Proteus\" mod is disabled in your current "
                + "Penumbra collection. Check it is enabled there."), unclaimed.Count);
            _ = Plugin.Framework.RunOnFrameworkThread(
                () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 17).Build()));
        }
        if (unclaimed.Count == 0) _reportedRedirectLosses.TryRemove("\0unclaimed", out _);

        // Raising is the fix, so prefer doing it over describing it. Only when we know WHICH mod to outrank:
        // a path resolving to nothing, or to a file outside the mod directory, has no priority to beat.
        if (owners.Count > 0 && !TryRaisePriorityAbove(owners))
            foreach (var gamePath in expected.Keys)
                if (!matched.Contains(gamePath) && !unstable.Contains(gamePath)
                    && !selfServed.Contains(gamePath))
                    NotifyRedirectLost(gamePath, raw[gamePath]);

        if (unstable.Count > 0)
            log.Warning("[Proteus] redirect check UNDETERMINED for {0} path(s) after {1}ms — they do not "
                      + "resolve to our output but the answer was still changing, so this is NOT evidence of "
                      + "a loss: {2}", unstable.Count, sw.ElapsedMilliseconds, string.Join(", ", unstable));

        if (selfServed.Count > 0)
            log.Warning("[Proteus] {0} path(s) resolve to an earlier publish of our own mod rather than the "
                      + "file this manifest names: {1}", selfServed.Count, string.Join(", ", selfServed));

        if (lost == 0 && unstable.Count == 0 && selfServed.Count == 0)
        {
            // Winning everything re-arms the raise latch, exactly as it re-arms the per-path chat notice
            // above. Without this, a mod installed later in the session that takes a path back could compute
            // a target we happen to have tried before and be waved through as "already attempted".
            _highestPriorityRaiseAttempted = int.MinValue;
            log.Debug("[Proteus] all {0} redirect(s) live — every path we publish resolves to our output",
                expected.Count);
        }
    }

    /// <summary>
    /// Resolve <paramref name="gamePath"/> to the mod file a composite should read as its BASE, never to
    /// our own previous output. Every call site that loads a base texture or material goes through this
    /// rather than <c>penumbra.ResolvePlayer</c> directly. Returns null only when there is no known
    /// upstream, which lets the loader fall through to game data as before.
    /// </summary>
    private string? ResolveUpstream(string gamePath)
    {
        var disk = penumbra.ResolvePlayer(gamePath);

        if (disk != null && !IsOwnOutput(disk))
        {
            // A live answer is normally the freshest truth — but not while Penumbra is rebuilding the
            // collection, when a contested path transiently resolves to whichever contributor happens to be
            // applied so far. That window is open RIGHT HERE: PrimeUpstreamCache's finally block republishes
            // the full manifest and reloads immediately before the blend loop starts calling this, so the
            // first resolves after a prime land mid-rebuild. Overwriting here would undo the settle the prime
            // just did and put the composite back on the wrong skin mod by a different route.
            //
            // So a settled value wins over a live one for the rest of the composite. It is only ever set by
            // SettleUpstreams, which has already waited for the answer to stop moving, and any real change to
            // the collection clears it via InvalidateUpstreamCache.
            if (_upstreamSettled.ContainsKey(gamePath)
                && _upstreamByGamePath.TryGetValue(gamePath, out var settled)
                && !string.Equals(settled, disk, StringComparison.OrdinalIgnoreCase)
                && File.Exists(settled))
            {
                log.Debug("[Proteus] resolve for {0} returned {1}, disagreeing with the settled upstream {2} "
                        + "— keeping the settled one", gamePath, disk, settled);
                return settled;
            }

            _upstreamByGamePath[gamePath] = disk;
            return disk;
        }

        if (_upstreamByGamePath.TryGetValue(gamePath, out var prev) && File.Exists(prev))
        {
            if (disk != null)
                log.Debug("[Proteus] resolve for {0} still pointed at our output — using upstream {1}",
                          gamePath, prev);
            return prev;
        }

        if (disk != null)
            log.Warning("[Proteus] ResolvePlayer returned our own managed file for {0} and no upstream is "
                      + "known — falling back to game data", gamePath);
        return null;
    }

    /// <summary>
    /// How long the prime may spend waiting for Penumbra's resolution to SETTLE before giving up on a path.
    ///
    /// Was 500ms when the loop only waited for the narrowed manifest to land. Settling needs longer because
    /// it is waiting on a different event: not "our redirect is gone" (which happens almost immediately) but
    /// "Penumbra has finished recomputing the collection and the winner has stopped moving". Two seconds is
    /// far more than a rebuild takes, and it is only ever paid on a cold cache — the warm path returns at
    /// the needPrime.Count == 0 check without touching this.
    ///
    /// Only paths that have already unmasked can spend this budget; see <see cref="PrimeNarrowTimeout"/> for
    /// the shorter one that governs getting there.
    /// </summary>
    private static readonly TimeSpan PrimeLiveTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long the sample must stay unchanged before we believe it. See <see cref="SettleUpstreams"/>.</summary>
    private static readonly TimeSpan PrimeSettleInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long a path gets to shed OUR OWN redirect after the reload before we give up on it entirely.
    ///
    /// Separate from <see cref="PrimeLiveTimeout"/> because the two are waiting on different events with
    /// different costs. Unmasking is bounded by Penumbra processing one ReloadMod and is normally tens of
    /// milliseconds; settling is bounded by a whole collection rebuild. Sharing one budget meant a path
    /// Penumbra never unmasks at all — the reload dropped, the key not in the collection — sat in the loop
    /// for the full settle budget, holding the narrowed manifest live the entire time for an answer that was
    /// never going to arrive. The narrow window is the expensive thing here: while it is open the base
    /// textures are unredirected, so the character renders un-composited.
    /// </summary>
    private static readonly TimeSpan PrimeNarrowTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// One body material's outcome on the last composite: how many overlays actually BLENDED into each
    /// channel, whether any overlay asked for a diffuse, and whether a non-overlay pass (AO, skin-tint
    /// suppression) edited a buffer.
    /// <para/>
    /// Two combinations carry the meaning. <c>DiffuseWanted &amp;&amp; Diffuse == 0</c> is the exact state behind
    /// "the normal applies but I still see the base skin". <c>Touched</c> with no contributors at all is a
    /// material only AO or skin-tint suppression reached — real work, but no overlay art, so it must not be
    /// rendered as a bare row of zeros.
    /// <para/>
    /// The inert combination — nothing reached it and nothing meant to — is filtered out by
    /// <see cref="ChannelContributions"/> and never reaches a caller, so don't branch on it.
    /// </summary>
    public readonly record struct ChannelContribution(
        string Material, int Diffuse, int Normal, int Mask, bool DiffuseWanted, bool Touched);

    /// <summary>
    /// What the last composite actually did to each body material's channels, ready to render: already
    /// filtered and sorted, because the status window redraws this every frame.
    ///
    /// The mirror image of <see cref="BaseUpstreams"/>: that says which file we read, this says what we did
    /// to it. Both exist for the same reason — a composite that applied nothing is visually and textually
    /// identical to one that applied everything, because the "Composited …" line only ever proved a buffer
    /// existed and a redirect was published.
    ///
    /// NOT one row per composited material: inert materials are dropped, so this count is lower than
    /// <c>byMaterial.Count</c> and than the <c>N material(s)</c> figure in the phase-breakdown line. Empty
    /// while a composite is in flight, and left empty by one that cancels or throws, so a stale set is never
    /// shown as if it described the character on screen.
    /// </summary>
    public IReadOnlyList<ChannelContribution> ChannelContributions() => _channelContributions;

    private volatile IReadOnlyList<ChannelContribution> _channelContributions = [];

    /// <summary>
    /// What each base game path currently resolves to, as (path, mod folder, settled) — the files the
    /// composite is standing ON. Surfaced in the status window because the composite gives no visual clue
    /// which skin mod it read: an overlay painted onto the wrong body looks like a correct composite of a
    /// skin the user didn't choose, and the only other evidence is a debug log line.
    ///
    /// Model/material shell paths are filtered out; they are written, not read as a base.
    /// </summary>
    public IReadOnlyList<(string GamePath, string Source, bool Settled)> BaseUpstreams()
        => _upstreamByGamePath
            .Where(kv => kv.Key.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, ModFolderOf(kv.Value) ?? kv.Value, !_upstreamUnsettled.ContainsKey(kv.Key)))
            .ToList();

    /// <summary>
    /// The mod FOLDER a file on disk belongs to — the answer to "which mod is this?", and the name the user
    /// can actually search for in Penumbra's list. The rest of the path is noise in a one-line row or a chat
    /// message. Null when the file isn't under the mod directory at all, which the caller should render as
    /// the full path rather than hiding.
    /// </summary>
    private string? ModFolderOf(string? diskPath)
    {
        var root = modsRoot;
        if (string.IsNullOrEmpty(diskPath) || string.IsNullOrEmpty(root)
            || !diskPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        var rel = diskPath[root.Length..].TrimStart('/', '\\');
        var cut = rel.IndexOfAny(['/', '\\']);
        var folder = cut > 0 ? rel[..cut] : rel;
        return folder.Length > 0 ? folder : null;
    }

    /// <summary>
    /// Base paths whose last prime never settled, so whatever is in <see cref="_upstreamByGamePath"/> for
    /// them (if anything) was read while Penumbra was still recomputing and must not be trusted as final.
    ///
    /// Exists because the memo is otherwise permanent: <see cref="ResolveUpstream"/> returns a remembered
    /// value for as long as the file exists, and the needPrime filter skips any path already remembered, so
    /// one bad read would never be re-asked for the rest of the session. Membership here forces the next
    /// composite to prime the path again.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _upstreamUnsettled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Base paths whose entry in <see cref="_upstreamByGamePath"/> came from <see cref="SettleUpstreams"/>,
    /// i.e. from a read that was verified to have stopped moving.
    ///
    /// <see cref="ResolveUpstream"/> treats these as authoritative and will not overwrite them with a live
    /// answer, because the resolves that immediately follow a prime land while Penumbra is rebuilding the
    /// collection from the restored manifest — the same mid-rebuild window the settle exists to see past.
    /// Cleared wholesale by <see cref="InvalidateUpstreamCache"/>, so a genuine collection change still wins.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _upstreamSettled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve <paramref name="paths"/> to their real upstreams while our own redirects are narrowed away,
    /// waiting until the answer STOPS CHANGING rather than accepting the first non-Proteus value.
    ///
    /// The distinction is the whole point of this method. <c>ReloadMod</c> makes Penumbra recompute the
    /// collection cache asynchronously, and while that is in flight a contested path resolves to whichever
    /// contributor has been applied so far — not to the highest-priority one. The old loop broke out on the
    /// first answer that wasn't ours, which is satisfied by any contributor, so a path claimed by several
    /// mods came back effectively at random: observed resolving to the correct mod, to the mod one priority
    /// step below it, and to one four steps below it on three consecutive plugin loads with identical
    /// settings, the FASTEST prime giving the WORST answer.
    ///
    /// So: require two consecutive identical reads a quiet interval apart. A mid-rebuild read disagrees with
    /// the next one and is discarded; a settled read agrees and is kept.
    ///
    /// Settling is tracked PER PATH, not for the batch as a whole. Paths converge at different moments, and
    /// one that never unmasks — Penumbra not processing that key, an undecidable path that IsOwnOutput
    /// pessimistically calls ours — must not cost its neighbours the answers they already reached, which is
    /// what an all-or-nothing return does on every composite for as long as the condition lasts.
    /// </summary>
    /// <returns>The settled upstreams. Paths that never settled are absent — deliberately, so the caller
    /// can leave them unmemoised and retry rather than bake in a value read mid-rebuild.</returns>
    private Dictionary<string, string> SettleUpstreams(IReadOnlyList<string> paths, System.Diagnostics.Stopwatch sw)
    {
        // Per path, the previous answer of an unbroken streak of unmasked reads. Absent means there is no
        // streak — we haven't seen an answer yet, or the last read still showed our own redirect. A null
        // VALUE is different from absent: it means "answered, and the answer is that nobody provides this",
        // which is a legitimate thing for two consecutive reads to agree on.
        var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var settled  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending  = new List<string>(paths);
        var unmasked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string>? stuck = null;
        int samples  = 0;

        // Two failure modes, reported separately because they mean different things: a path Penumbra never
        // unmasked says the reload didn't reach it, while a path that kept changing says the collection was
        // still rebuilding. Both end the same way — unresolved, retried next composite.
        void WarnUnresolved(List<string>? neverUnmasked, List<string>? stillMoving)
        {
            if (neverUnmasked is { Count: > 0 })
                log.Warning("[Proteus] upstream prime: {0} path(s) never shed our own redirect within {1}ms — "
                          + "Penumbra did not apply the narrowed manifest to them; leaving them unresolved so "
                          + "the next composite retries: {2}",
                    neverUnmasked.Count, (int)PrimeNarrowTimeout.TotalMilliseconds, string.Join(", ", neverUnmasked));
            if (stillMoving is { Count: > 0 })
                log.Warning("[Proteus] upstream did NOT settle for {0} path(s) within {1}ms — the resolved "
                          + "winner was still changing; leaving them unresolved so the next composite "
                          + "retries: {2}",
                    stillMoving.Count, sw.ElapsedMilliseconds, string.Join(", ", stillMoving));
        }

        while (true)
        {
            samples++;
            bool narrowExpired = sw.Elapsed >= PrimeNarrowTimeout;

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                var d = penumbra.ResolvePlayer(p);

                // Still masked by our own redirect, or no answer at all: this read tells us nothing, so break
                // the streak. Without the reset a read taken before the narrow landed could pair with one
                // taken after it and "settle" on the empty answer the two happen to share — concluding
                // "nobody provides this" when the truth is only that we hadn't looked yet.
                if (d == null || IsOwnOutput(d))
                {
                    previous.Remove(p);

                    // Out of narrow budget without this path EVER having unmasked. Waiting the full settle
                    // budget for it would keep the narrow open for an answer that isn't coming; drop it and
                    // let the paths that did unmask finish. When every path is stuck this empties pending on
                    // the spot, so the manifest is restored at the narrow deadline rather than the settle one.
                    if (narrowExpired && !unmasked.Contains(p))
                    {
                        (stuck ??= []).Add(p);
                        pending.RemoveAt(i);
                    }
                    continue;
                }

                unmasked.Add(p);

                // An unredirected path echoes the game path straight back. That is "nobody else provides
                // this", not a disk file — memoising it would put a non-existent path in the upstream cache.
                var value = string.Equals(d, p, StringComparison.OrdinalIgnoreCase) ? null : d;

                if (previous.TryGetValue(p, out var prev)
                    && string.Equals(prev, value, StringComparison.OrdinalIgnoreCase))
                {
                    if (value != null) settled[p] = value;
                    pending.RemoveAt(i);
                    continue;
                }

                previous[p] = value;
            }

            if (pending.Count == 0)
            {
                WarnUnresolved(stuck, null);
                log.Debug("[Proteus] upstream settled: {0} of {1} path(s) resolved after {2} sample(s) in {3}ms",
                    settled.Count, paths.Count, samples, sw.ElapsedMilliseconds);
                return settled;
            }

            if (sw.Elapsed >= PrimeLiveTimeout)
            {
                // Only what is still moving is dropped. An unsettled answer is not a weaker answer, it is a
                // wrong one, and memoising it is what strands a whole session on the wrong skin — but that is
                // a reason to discard THAT path, not the ones that already stood still.
                WarnUnresolved(stuck, pending);
                return settled;
            }

            Thread.Sleep((int)PrimeSettleInterval.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Make sure every game path we currently redirect and are about to READ has a known upstream, so the
    /// composite reads the user's body mod as its base rather than our own last output.
    ///
    /// This is the narrow remnant of the manifest clear that used to sit at the top of a composite. The
    /// difference is the scope. That cleared EVERYTHING for the whole run; this drops only the handful of
    /// paths whose upstream we don't already remember, keeps the shell and its EQDP rows published
    /// throughout, and puts the full manifest back within a few hundred milliseconds.
    ///
    /// It is needed at all because <see cref="ResolveUpstream"/> can only fall back to a remembered value,
    /// and <see cref="_upstreamByGamePath"/> is in-memory: empty at session start, and emptied by
    /// <see cref="InvalidateUpstreamCache"/> whenever a non-sidecar mod's settings change — which Glamourer
    /// does on zone-in when it re-asserts a body mod's temporary settings. With a cold cache and a live
    /// manifest, every path we redirect resolves to our own output, ResolveUpstream returns null, and the
    /// composite silently bakes onto vanilla (or, for a skin mod's invented paths, onto nothing at all).
    ///
    /// Normally a no-op: after the first composite of a session every path is remembered, so this returns
    /// at the <c>needPrime.Count == 0</c> check without touching Penumbra. That is the zone-in path.
    /// </summary>
    /// <param name="materialPaths">The material game paths this composite will read as bases.</param>
    /// <returns>
    /// Every base path considered, sorted — the live manifest's readable keys plus <paramref name="materialPaths"/>.
    /// This is the set the composite fingerprint reports upstream identity for, and it is returned rather than
    /// recomputed there so the fingerprint depends only on THIS composite's inputs. Deriving it from
    /// <see cref="_upstreamByGamePath"/> instead would make it depend on whatever earlier composites happened
    /// to resolve — that dictionary grows during the blend loop and is emptied wholesale by
    /// <see cref="InvalidateUpstreamCache"/>, so the key set would change shape after every zone-in and the
    /// gate would never settle.
    /// </returns>
    private List<string> PrimeUpstreamCache(IEnumerable<string> materialPaths)
    {
        // Only paths we might read as a BASE matter here. Shell files under chara/equipment|accessory are
        // write-only from the composite's point of view, and excluding them stops a new shell texture name
        // from forcing a prime every time the shell changes.
        //
        // The one exception is an APPEND host's model: we read that back as the base we merge into, so it
        // has a real upstream (the player's own necklace/ring mod) that has to survive our redirect. It
        // used to be excluded too, on the grounds that SecondSkinService's IsInsideOutputRoot check
        // "guarded" it — but that check only refuses our output, it does not recover what was behind it,
        // so with a cold cache and a live manifest the merge was rebuilt from VANILLA and the player's
        // modded necklace lost its appearance (taking the appended shell with it).
        //
        // CARRIER hosts are deliberately NOT admitted, even though they are .mdl under the same trees.
        // Their model is replaced outright and never read, so priming one learns nothing — while the
        // narrow below would drop its redirect for the width of the prime, and since the EQDP rows are
        // carried across, the game would load the real (invisible) Emperor ring and the shell would blink
        // out. That is why this tests the append set rather than the extension.
        var appendHosts = _appendHostModelPaths;
        bool IsReadableBase(string p)
            => (!p.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
             && !p.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase))
            || appendHosts.Contains(p);

        List<string> baseKeys;

        // The lock covers the read-modify-write and NOTHING ELSE. A concurrent composite publishing between
        // the read and the restore would otherwise have its manifest silently overwritten with the
        // pre-publish copy (see _manifestLock) — but the trailing resolve loop below touches no manifest, and
        // on a cold cache it is dozens of Penumbra IPC calls, so holding the lock across it would block a
        // concurrent publish for no reason and keep the older manifest live longer than necessary.
        lock (_manifestLock)
        {
            // What is actually published right now — not what we are about to publish. Null means unreadable,
            // which is "unknown", not "empty": guessing empty would skip a prime that is genuinely needed.
            var live = PenumbraModMeta.TryReadDefaultData(managedModDir);

            var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in materialPaths) keys.Add(p);
            if (live is { } l)
                foreach (var p in l.Files.Keys)
                    if (IsReadableBase(p)) keys.Add(p);
            baseKeys = [.. keys];

            // A path only needs the narrow-and-restore dance if OUR OWN manifest currently masks it. Anything
            // else already resolves to its real upstream, so a plain ResolveUpstream call is enough.
            //
            // _upstreamUnsettled re-admits paths we already "know", because for those the memo holds a value
            // read while Penumbra was mid-rebuild. Without this clause the needPrime filter would see them as
            // known and never ask again, which is exactly how one racy read used to survive a whole session.
            var needPrime = live is { } lv
                ? baseKeys.Where(p => lv.Files.ContainsKey(p)
                                   && (!_upstreamByGamePath.ContainsKey(p) || _upstreamUnsettled.ContainsKey(p)))
                          .ToList()
                : [];

            if (needPrime.Count > 0)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var files = live!.Value.Files;
                var manips = live.Value.Manipulations;
                var narrowed = new Dictionary<string, string>(files, StringComparer.OrdinalIgnoreCase);
                foreach (var p in needPrime) narrowed.Remove(p);

                try
                {
                    // Manipulations are carried across verbatim: dropping them here is what used to un-load
                    // the shell's host accessory entirely, and none of them affect texture resolution.
                    WriteManagedModJson(narrowed, manips);
                    penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);

                    // Wait for the answer to stop moving, not merely for our redirect to disappear — see
                    // SettleUpstreams for why the difference decides which skin mod the composite lands on.
                    var settled = SettleUpstreams(needPrime, sw);

                    int missed = 0;
                    foreach (var p in needPrime)
                    {
                        if (settled.TryGetValue(p, out var disk))
                        {
                            // Log the transition, not just the value: a genuine skin change and a racy read
                            // produce identical output, and this line is what tells them apart afterwards.
                            if (_upstreamByGamePath.TryGetValue(p, out var was)
                                && !string.Equals(was, disk, StringComparison.OrdinalIgnoreCase))
                                log.Information("[Proteus] base upstream CHANGED: {0}\n    was {1}\n    now {2}",
                                    p, was, disk);
                            else
                                log.Information("[Proteus] base upstream: {0} <- {1}", p, disk);

                            _upstreamByGamePath[p] = disk;
                            _upstreamSettled[p] = 0;
                            _upstreamUnsettled.TryRemove(p, out _);
                        }
                        else
                        {
                            // Unsettled, or genuinely provided by nobody. Either way don't memoise a guess:
                            // mark it for retry. Drop any earlier settled mark too — whatever the memo still
                            // holds for this path was not confirmed by THIS prime, so it must not go on
                            // outranking live resolves.
                            missed++;
                            _upstreamUnsettled[p] = 0;
                            _upstreamSettled.TryRemove(p, out _);
                        }
                    }

                    log.Information("[Proteus] upstream prime: {0} path(s) in {1}ms{2}",
                        needPrime.Count, sw.ElapsedMilliseconds,
                        missed > 0 ? $" — {missed} still unresolved (will composite on game data, retrying next composite)" : "");
                }
                finally
                {
                    // Always, even if the prime threw: leaving the narrowed manifest live is exactly the
                    // blackout this whole change exists to remove.
                    WriteManagedModJson(files, manips);
                    penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                }
            }
        }

        // Outside the lock: this writes no manifest. Whatever is still unresolved isn't masked by us, so it
        // resolves straight to its real upstream. Done here rather than lazily in the blend loop so every key
        // the fingerprint reports has its value BEFORE the gate reads it — otherwise the first composite
        // after a cache drop would report "unknown" for paths the next one reports properly, and differ from
        // it for no real reason.
        foreach (var p in baseKeys)
            if (!_upstreamByGamePath.ContainsKey(p))
                ResolveUpstream(p);

        return baseKeys;
    }

    /// <summary>
    /// The last few redirect maps we published, newest last. Penumbra may still be serving ANY of them, so
    /// <see cref="PruneSupersededOutput"/> keeps anything named by any remembered map rather than assuming
    /// the newest one is live. Output survives a couple of composites longer, which costs disk and nothing
    /// else, and no live redirect can ever be left dangling.
    ///
    /// Two independent reasons, both of which outlive any improvement to the readiness probe:
    ///
    /// The reload is ASYNCHRONOUS. <see cref="WaitForManifestLive"/> does confirm the new manifest landed
    /// (measured at 16-32 ms in practice), but it is skipped entirely under DisableAutoRedraw and can hit
    /// its timeout, so "confirmed" is not something every path can rely on.
    ///
    /// And composites OVERLAP. Cancellation is cooperative and its last check sits far above the writes,
    /// so a superseded run keeps going and publishes after a newer one already has — at which point the
    /// newest map is not even the live one. No probe can fix that; it is not a timing question.
    ///
    /// So do not collapse this to a single map on the grounds that the probe works. It does now; that was
    /// never what this defends against. Getting it wrong means a live redirect pointing at a deleted file,
    /// which for a skin mod's invented paths hard-fails the load and renders the body invisible.
    ///
    /// Empty on startup on purpose: Penumbra is still serving the manifest the LAST SESSION left on disk,
    /// and this session has no idea what that named. Pruning against an empty history would delete the
    /// very files that manifest points at, so the pruner no-ops until we have published something.
    ///
    /// Guarded by <see cref="_publishHistoryLock"/> — written by whichever composite task publishes, read
    /// by whichever one prunes, and those are not the same thread.
    /// </summary>
    private readonly Queue<IDictionary<string, string>> _publishHistory = new();
    private readonly object _publishHistoryLock = new();

    /// <summary>How many past manifests to treat as possibly-live. Two would cover the ordinary
    /// publish-then-reload lag; three leaves room for a reload that Penumbra queues behind another.</summary>
    private const int PublishHistoryDepth = 3;

    /// <summary>
    /// Remember a manifest we just published as possibly-live, retiring the oldest, and return the one it
    /// replaces (null on the first publish of a session). The caller needs that to tell which redirects
    /// actually changed — the only ones worth probing for readiness.
    /// </summary>
    private IDictionary<string, string>? RecordPublish(IDictionary<string, string> redirects)
    {
        // Snapshot: the caller's map is a live ConcurrentDictionary this reference would otherwise outlive.
        var snapshot = new Dictionary<string, string>(redirects, StringComparer.OrdinalIgnoreCase);
        lock (_publishHistoryLock)
        {
            var previous = _publishHistory.Count > 0 ? _publishHistory.Last() : null;
            _publishHistory.Enqueue(snapshot);
            while (_publishHistory.Count > PublishHistoryDepth) _publishHistory.Dequeue();
            return previous;
        }
    }

    /// <summary>
    /// Delete output files that nothing can still be pointing at, run at the START of a composite rather
    /// than the end of the previous one. What goes: the previous run's copy of a texture whose content has
    /// since changed, files from a mod that got disabled, a dropped spill host, a shell that shed a layer,
    /// and — the case an end-of-run prune could never reach — whatever a CANCELLED composite wrote before
    /// it bailed, since every cancellation path returns long before the publish step.
    ///
    /// Two things make it safe, and both replace guarantees the old end-of-run prune could not actually
    /// give. It keeps anything named by ANY of the last <see cref="PublishHistoryDepth"/> manifests, so it
    /// does not matter which one Penumbra is currently serving — deleting right after publishing meant
    /// racing an asynchronous reload whose readiness poll times out routinely and is skipped entirely
    /// under DisableAutoRedraw. And it stands down while another composite is in flight, because that
    /// composite has already passed its last cancellation check and will write files and publish them:
    /// deleting its output mid-run would hand it a manifest full of dangling redirects.
    ///
    /// Either mistake produces the same failure — a live redirect pointing at a file that is gone, which
    /// for a skin mod's invented paths hard-fails the load and takes the whole body material with it
    /// (2:Failure → 2:FailedSubResource → invisible body). Both fallbacks defer collection by a run, which
    /// costs disk and nothing else.
    ///
    /// Safe to delete a file still tracked in SecondSkinService's hash cache: the write path re-checks
    /// File.Exists and rewrites.
    /// </summary>
    private void PruneSupersededOutput()
    {
        // >1 because this composite has already counted itself in.
        var others = Volatile.Read(ref _compositesInFlight) - 1;
        if (others > 0)
        {
            log.Debug("[Proteus] prune deferred — {0} other composite(s) still running and writing", others);
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_publishHistoryLock)
        {
            if (_publishHistory.Count == 0) return;   // see the field: last session's manifest is still live
            foreach (var map in _publishHistory)
                foreach (var rel in map.Values)
                    keep.Add(rel.Replace('\\', '/'));   // Rel() emits backslashes, the skin path forward slashes
        }

        PruneManagedOutput(keep);
    }

    /// <summary>
    /// Delete any file under textures/ materials/ models/ whose <c>sub/name</c> is not in
    /// <paramref name="keep"/>. Callers build that set from every manifest that could still be live —
    /// never from the one about to be published.
    /// </summary>
    private void PruneManagedOutput(HashSet<string> keep)
    {
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
    /// The subset of <see cref="_lastShellHostPaths"/> we APPENDED into — the player's own worn item, whose
    /// model is read back as the base of the merge. These are the only published .mdl paths with an upstream
    /// worth recovering, so they are the only ones <see cref="PrimeUpstreamCache"/> may unpublish to go and
    /// look for it. Seeded from config at construction because the manifest outlives the session: on the
    /// first composite after a restart it already masks these paths, and nothing has been built yet to say
    /// which they are.
    /// </summary>
    private volatile HashSet<string> _appendHostModelPaths = new(StringComparer.OrdinalIgnoreCase);

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
                StampOwnRedraw();
                if (!Plugin.Framework.RunOnFrameworkThread(penumbra.RedrawPlayer).GetAwaiter().GetResult())
                    CancelOwnRedrawEcho();
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
                return;
            }
        }

        StampOwnRedraw();
        if (!penumbra.RedrawPlayer())
            CancelOwnRedrawEcho();
    }

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

    /// <summary>How long to keep polling for the reload to land before giving up and redrawing anyway.
    /// Generous because the old 400 ms cap was never actually reached by a match (see WaitForManifestLive)
    /// — until the logs show real latencies, a cap tight enough to expire is a cap that hides them.</summary>
    private static readonly TimeSpan ManifestLiveTimeout = TimeSpan.FromMilliseconds(1500);

    // Reload the managed mod, then redraw — but instead of sleeping a fixed, conservative interval before
    // the redraw, poll until Penumbra has actually processed the new redirects. Penumbra applies a
    // ReloadMod asynchronously on its framework handler; the redraw re-requests textures through
    // ResolvePlayer, so redrawing before the reload lands renders the previous composite.
    /// <returns>
    /// Whether the published manifest was OBSERVED to be live — a probe resolving to the file we just wrote,
    /// or a withdrawn path confirmed gone. False means unproven, not disproven: nothing changed so there was
    /// nothing to probe, the wait timed out, or DisableAutoRedraw skipped it. <see cref="VerifyRedirectsLive"/>
    /// needs the distinction, because "no path resolves to us" means we are outranked everywhere if the
    /// manifest is known live, and means nothing at all if it isn't.
    /// </returns>
    private bool ReloadAndRedrawWhenReady(IDictionary<string, string> redirects,
                                          IDictionary<string, string>? previous)
    {
        var ec = penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
        log.Debug("[Proteus] ReloadMod -> {0}", ec);
        if (config.DisableAutoRedraw) return false;

        bool live = WaitForManifestLive(redirects, previous);

        RefreshPlayerTextures();
        SchedulePostRedrawBodyTypeCheck();
        return live;
    }

    /// <summary>
    /// Block until Penumbra resolves a path this composite CHANGED to the file we just wrote for it.
    /// <paramref name="previous"/> is the manifest being replaced.
    ///
    /// Both halves of that sentence are load-bearing, and both were wrong before:
    ///
    /// It must compare FULL PATHS. Comparing filenames looks equivalent but isn't, because a shell
    /// texture's output name is the same as the name in its game path — "chara/equipment/e5501/texture/
    /// ss_0_base.tex" maps to "textures/ss_0_base.tex". An unredirected resolve returns the game path,
    /// whose filename already equals what we expect, so the probe passed instantly and we redrew before
    /// the reload had landed. (The runId check this replaced had the mirror-image flaw: shell filenames
    /// never contain a runId, so whenever the probe happened to pick one it could never match and the
    /// loop ran to its cap. That is the "reload ready after 409ms" on every composite in the logs — not a
    /// slow reload, a probe that was structurally incapable of succeeding.)
    ///
    /// And it must probe a CHANGED entry. Output names are content-hashed, so an unchanged redirect maps
    /// to the file the previous manifest already pointed at; matching on one proves nothing about which
    /// manifest is live. When nothing changed there is genuinely nothing to wait for.
    /// </summary>
    /// <returns>True only when a probe actually confirmed the manifest is live. "Nothing to wait for" and a
    /// timeout both return false — unproven, which is not the same as disproven.</returns>
    private bool WaitForManifestLive(IDictionary<string, string> redirects,
                                     IDictionary<string, string>? previous)
    {
        string? probe = null, expectedFull = null;
        foreach (var (gamePath, rel) in redirects)
        {
            if (previous != null && previous.TryGetValue(gamePath, out var was)
                && string.Equals(was, rel, StringComparison.OrdinalIgnoreCase))
                continue;                         // unchanged — proves nothing
            var full = TryCanonicalise(Path.Combine(managedModDir, rel));
            if (full == null) continue;
            probe = gamePath; expectedFull = full; break;
        }

        // A REMOVED key is a change too, and scanning only the new manifest cannot see one. That used to be
        // theoretical — every composite added or rewrote something — but withholding the diffuse redirect for
        // a material no overlay painted makes removal-only composites routine, and this function's answer for
        // them was "nothing to wait for": it redrew immediately and could render the composite it had just
        // withdrawn. Here the wait inverts — hold until the path STOPS resolving to the file we dropped.
        string? goneProbe = null, goneFull = null;
        if (probe == null && previous != null)
        {
            foreach (var (gamePath, rel) in previous)
            {
                if (redirects.ContainsKey(gamePath)) continue;
                var full = TryCanonicalise(Path.Combine(managedModDir, rel));
                if (full == null) continue;
                goneProbe = gamePath; goneFull = full; break;
            }
        }

        if (probe == null && goneProbe == null)
        {
            log.Debug("[Proteus] no redirect changed this composite — nothing to wait for");
            return false;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < ManifestLiveTimeout)
        {
            if (probe != null)
            {
                var resolved = TryCanonicalise(penumbra.ResolvePlayer(probe));
                if (resolved != null && string.Equals(resolved, expectedFull, StringComparison.OrdinalIgnoreCase))
                {
                    log.Debug("[Proteus] manifest live after {0}ms", sw.ElapsedMilliseconds);
                    return true;
                }
            }
            else
            {
                // Anything other than the withdrawn file means the removal landed — including a null answer,
                // which is the legitimate "nobody provides this now" for a skin mod's invented path.
                var resolved = TryCanonicalise(penumbra.ResolvePlayer(goneProbe!));
                if (resolved == null || !string.Equals(resolved, goneFull, StringComparison.OrdinalIgnoreCase))
                {
                    log.Debug("[Proteus] withdrawn redirect cleared after {0}ms ({1})",
                        sw.ElapsedMilliseconds, goneProbe!);
                    return true;
                }
            }
            Thread.Sleep(15);
        }

        // Distinct from the success line on purpose: a timeout means the redraw below may render the
        // PREVIOUS composite, and that should be legible in the log rather than buried in a shared message.
        // One of the two probes is non-null — the early return above is the only path where both are.
        log.Warning("[Proteus] manifest not live after {0}ms (probe {1}) — redrawing anyway",
                    sw.ElapsedMilliseconds, probe ?? goneProbe!);
        return false;
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
    /// <summary>
    /// After the redraw, ask the CHARACTER whether the second skin is actually being drawn.
    ///
    /// The last link in a chain this session has been walking outwards one step at a time. The composite
    /// proving it blended something says nothing about the redirect being published; the redirect being
    /// published says nothing about Penumbra serving it (VerifyRedirectsLive); and Penumbra serving it says
    /// nothing about the game having LOADED it. A gear shell can pass every one of those and still not
    /// appear, because it is appended into a worn accessory and only renders if the game draws that host.
    ///
    /// Materials are the probe, not models: the host accessory's model loads whether or not our version won,
    /// but the appended material only enters the resource tree when the mesh referencing it is actually
    /// drawn. Missing means the suit is not on the character, whatever the rest of the log says.
    ///
    /// Sampled twice, because a full redraw is not instant and a single early read would call a healthy
    /// shell missing — the same discipline every other check here settled on.
    /// </summary>
    private void SchedulePostRedrawShellCheck()
    {
        var expected = _shellDrawnCheck;
        if (expected == null || expected.Materials.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                List<string> missing = [];
                bool hostEverDrawn = false;
                HashSet<string>? hostMaterials = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Task.Delay(800).ConfigureAwait(false);
                    if (_disposed) return;

                    // Superseded by a newer build — that composite runs its own check.
                    if (!ReferenceEquals(_shellDrawnCheck, expected)) return;

                    HashSet<string>? materials, models;
                    try
                    {
                        materials = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerMaterialPaths).GetAwaiter().GetResult();
                        models    = Plugin.Framework.RunOnFrameworkThread(penumbra.GetActivePlayerModelPaths).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) { return; }
                    if (materials == null || models == null) return;   // not in game / IPC down — no answer

                    // THE ANCHOR. The composite that scheduled this usually forced a full redraw, and a
                    // despawn-and-reload of the character plus a multi-megabyte accessory model is not
                    // reliably done within the sampling window. Until the host model is back in the draw
                    // object, a missing material means "not loaded YET", and warning on it would tell someone
                    // to re-equip an accessory that was about to work. Only once the host is drawn does its
                    // absence mean our mesh did not load.
                    if (!expected.Models.Any(models.Contains)) continue;
                    hostEverDrawn = true;

                    missing = expected.Materials.Where(p => !materials.Contains(p)).ToList();
                    if (missing.Count == 0)
                    {
                        log.Debug("[Proteus] second skin is drawn: all {0} shell material(s) are on the character",
                            expected.Materials.Count);
                        return;
                    }
                    hostMaterials = materials;
                }

                if (!hostEverDrawn)
                {
                    log.Debug("[Proteus] second skin drawn check inconclusive — the host accessory was not back "
                            + "in the draw object within the sampling window, so whether our mesh loaded is "
                            + "unknown (not a failure)");
                    return;
                }

                // A carrier we equipped DURING this composite is still settling: the equip fires its own
                // redraw, and the model can be back in the draw object before its materials are. The host
                // being drawn is therefore not enough to call the material missing here, and the next
                // composite re-runs this check with everything landed. Without the guard, the one case that
                // needs no host at all — nothing equipped, so the shell rides the Emperor's ring we just put
                // on — reports failure every time on nothing more than timing.
                var sinceCarrier = unchecked(Environment.TickCount64
                    - Math.Max(_lastRingInjectTick, _lastGlassesInjectTick));
                if (sinceCarrier >= 0 && sinceCarrier < RingInjectCooldownMs)
                {
                    log.Debug("[Proteus] second skin drawn check deferred — a carrier was equipped {0}ms ago "
                            + "and is still loading; the next composite re-checks", sinceCarrier);
                    return;
                }

                // What the host DID load, so the next question is answerable from this line alone. The
                // material is referenced variant-relatively, so the usual reason a published path is never
                // requested is that we published it under the wrong v#### — and that is only visible by
                // comparing against the variant the host's own materials came in under. Listing them turns
                // "it isn't drawn" into "it isn't drawn, and here is the folder the game was actually
                // reading from".
                var hostDir = missing
                    .Select(p => p[..(p.LastIndexOf('/') + 1)])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var siblings = hostMaterials == null ? [] : hostMaterials
                    .Where(m => hostDir.Any(d => m.StartsWith(
                        d[..(d.IndexOf("/material/", StringComparison.OrdinalIgnoreCase) + 10)],
                        StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                log.Warning("[Proteus] second skin built and published but is NOT being drawn — {0} of {1} "
                          + "shell material(s) never appeared on the character: {2}. The host accessory it "
                          + "was appended into is drawn, but not our copy of it. The host's OWN materials in "
                          + "the draw object are: {3}",
                    missing.Count, expected.Materials.Count, string.Join(", ", missing),
                    siblings.Count == 0 ? "(none — the host loads no material of its own)" : string.Join(", ", siblings));

                var msg = Loc.Localize("Chat.ShellNotDrawing",
                    "[Proteus] Your second skin was built but the game isn't drawing it. The accessory "
                    + "it rides on is equipped, yet the character isn't loading Proteus' version. Try "
                    + "unequipping and re-equipping that accessory, or pick a different one.");
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 17).Build()));
            }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { }
            catch (Exception ex) { log.Error(ex, "[Proteus] post-redraw shell check failed"); }
        });
    }

    /// <summary>
    /// The body-side facts this backstop judges, as one comparable key.
    /// <para/>
    /// Deliberately NARROWER than <see cref="DrawStateSignature"/>: it must not react to an equipment
    /// change, which the redraw hook's own equip diff already owns.
    /// </summary>
    private static string PostSettleKey(in DrawSample s)
        => string.Join('\n',
            BodyTypeKey(s.Materials!)              ?? "-",
            CharCodeKey(CharCodeSet(s.Materials!)) ?? "-",
            BodyShapeSignature(s.Shapes));

    /// <summary>Set while a post-redraw body-type check is in flight. Every publishing composite schedules
    /// one, and composites genuinely overlap, so without this two can run at once and both fire a
    /// correction off two different half-settled readings.</summary>
    private int _postSettleCheckRunning;

    private const int PostSettleInitialMs = 600;
    private const int PostSettlePollMs    = 200;
    private const int PostSettleMaxPolls  = 6;   // ~1.6s all told

    private void SchedulePostRedrawBodyTypeCheck()
    {
        if (Interlocked.Exchange(ref _postSettleCheckRunning, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PostSettleInitialMs).ConfigureAwait(false);

                // CONFIRM BEFORE DECIDING. A single read here is what made this check fire on nothing: at
                // 600ms the bibo body material had been dropped and not yet reloaded, so the walk yielded
                // bodyType "gen2" (a strict subset of the composited "bibo,gen2") and NO char code, which
                // read as a body-type change and drove a full corrective composite. The shell check beside
                // this one already learned the lesson — "a single early read would call a healthy shell
                // missing" — this one never adopted it.
                //
                // So sample until two consecutive readings agree. One IPC per sample (SampleDrawState),
                // replacing the two separate framework calls this used to make.
                string? prevKey = null;
                DrawSample latest = default;
                bool confirmed = false;
                for (int i = 0; i < PostSettleMaxPolls; i++)
                {
                    DrawSample s;
                    // GetResult (not await) inside SampleDrawState so the continuation stays on this
                    // background pool thread — see TriggerRecomposite for the full rationale.
                    try { s = SampleDrawState(); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { return; }

                    // Empty as well as null: this runs precisely when the draw object may still be coming
                    // up. An empty walk yields no body type and no char code, which reads as "everything
                    // changed" and would drive a corrective composite off nothing. Blanks also must never
                    // agree with each other, or two of them would "confirm" a character that isn't there.
                    if (s.Materials is { Count: > 0 })
                    {
                        latest = s;
                        var key = PostSettleKey(s);
                        if (string.Equals(key, prevKey, StringComparison.Ordinal)) { confirmed = true; break; }
                        prevKey = key;
                    }
                    else prevKey = null;

                    // Not after the last sample — there is nothing left to wait for, and sleeping there
                    // added a fifth of a second to every capped-out correction for nothing.
                    if (i < PostSettleMaxPolls - 1)
                        await Task.Delay(PostSettlePollMs).ConfigureAwait(false);
                }
                if (prevKey == null) return;   // never got a usable reading at all

                // Same builders WaitForRaceToSettle and Recomposite use — see CharCodeKey for why sharing
                // them is load-bearing rather than tidiness.
                var snapshot       = latest.Materials!;
                var newBodyTypeKey = BodyTypeKey(snapshot);
                var newCharCodeKey = CharCodeKey(CharCodeSet(snapshot));

                // BodyTypeKey and CharCodeSet read the SAME materials — CharCodeSet skips anything
                // InferBodyType rejects — so a non-null body type standing beside a NULL char code is not a
                // state a character can be in. It is a mid-reload walk, and nothing derived from THE
                // MATERIALS is worth acting on. This is the "charCode c0201 → none" that drove the
                // corrective composite.
                //
                // A veto, not an early return: the body-shape branch below reads the draw object's shape
                // keys, not the material list, so an incoherent MATERIAL walk says nothing about it. Bailing
                // outright dropped a genuine "Remove Hip Dips" toggle whenever the 600ms read happened to
                // land mid-reload — and since this backstop runs once per publishing composite, dropped
                // means gone, with the morph not appearing until something else triggers a composite.
                bool materialsIncoherent = newBodyTypeKey != null && newCharCodeKey == null;
                if (materialsIncoherent)
                    log.Debug("[Proteus] post-settle materials are incoherent (bodyType={0}, charCode=none) — "
                            + "judging shapes only", newBodyTypeKey ?? "none");

                bool bodyTypeShrank  = IsStrictSubsetKey(newBodyTypeKey, _lastCompositedBodyType);
                bool bodyTypeChanged = !materialsIncoherent && newBodyTypeKey != null && !bodyTypeShrank &&
                    !string.Equals(newBodyTypeKey, _lastCompositedBodyType, StringComparison.OrdinalIgnoreCase);
                bool charCodeChanged = !materialsIncoherent && newCharCodeKey != null &&
                    !string.Equals(newCharCodeKey, _lastCompositedCharCodes, StringComparison.OrdinalIgnoreCase);

                // Enabled body shapes settle AFTER the composite too: toggling "Remove Hip Dips" fires a
                // recomposite that can read the shape state before the game applies it, so the first shell
                // bakes the OLD shape and the morph shows only after a manual refresh. Independent of the
                // material vetoes above — a shape signature has no subset or null pathology, and it is read
                // from a different place. Only a shape read that FAILED (null) is untrustworthy: an empty
                // map is a real answer meaning "nothing enabled".
                var settledShapes = latest.Shapes;
                bool shapesChanged = settledShapes != null && !string.Equals(
                    BodyShapeSignature(settledShapes), _lastCompositedBodyShapeSig, StringComparison.Ordinal);

                if (bodyTypeChanged || charCodeChanged || shapesChanged)
                {
                    log.Debug("[Proteus] Post-settle correction: bodyType={0}→{1} charCode={2}→{3} "
                            + "shapesChanged={4} confirmed={5} incoherent={6}",
                        _lastCompositedBodyType ?? "none", newBodyTypeKey ?? "none",
                        _lastCompositedCharCodes ?? "none", newCharCodeKey ?? "none",
                        shapesChanged, confirmed, materialsIncoherent);

                    // Publishing is per-source, because the two can disagree about how trustworthy they are:
                    // the shapes may be settled while the material walk is mid-reload, which is precisely the
                    // case that used to bail out entirely.
                    if (confirmed && !materialsIncoherent)
                    {
                        // Two agreeing readings — publish, so the corrective recomposite uses them directly
                        // (dirty stays false → TriggerRecomposite won't re-fetch a still-settling one).
                        _activeMtrlSnapshot = snapshot;
                        _activeMtrlSnapshotDirty = false;
                        config.CachedActiveMaterialPaths = snapshot.ToList();
                    }
                    else
                    {
                        // Either the state was still moving when we capped out, or the material walk is
                        // self-contradictory. Still worth a corrective composite — the backstop's job is
                        // "something is off, go look" — but publishing an unverified read is what corrupted
                        // _lastCompositedBodyType and defeated the unchanged-inputs gate. Say that something
                        // is wrong without claiming to know what: mark it dirty and let the preamble's own
                        // settle establish the truth.
                        _activeMtrlSnapshotDirty = true;
                    }
                    if (confirmed && settledShapes != null) _bodyShapeSnapshot = settledShapes;
                    TriggerRecomposite("post-settle-correction", force: false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) when (_disposed || IsLoadContextUnloading(ex)) { }
            finally { Volatile.Write(ref _postSettleCheckRunning, 0); }
        });
    }

    // ── Compositing ──────────────────────────────────────────────────────────

    // Load the base normal texture. ResolveUpstream keeps this off our own managed output (a feedback
    // loop: Penumbra may still resolve our path while its reload is in flight) and, unlike the plain
    // managedModDir guard this used to do inline, falls back to the last known UPSTREAM rather than to
    // game data — which for a skin mod's invented paths does not exist.
    // After loading, resets alpha to 0 if >50% of pixels are 255 — a reliable
    // fingerprint of our own stale all-255 output (natural base normals avg ~5).
    private byte[] LoadBaseNormal(string gamePath, ref int w, ref int h)
    {
        var loaded = textureLoader.LoadBaseTexture(ResolveUpstream(gamePath), gamePath);
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

    // Per-pixel color and emissive driven by index texture.
    // isNormal = false: tint+composite diffuse; isNormal = true: write emissive to normal alpha.
    internal static void ApplyIndexedOverlay(
        byte[] baseTex, byte[] ov, byte[] idx,
        Dictionary<int, ColorTableRowOverride> rows,
        bool isNormal, int w, int h)
    {
        // The row pair is `red / 17`, so there are only ever SIXTEEN distinct answers — resolved once here
        // into flat arrays instead of per texel. The loop below runs w*h times (16.7M at 4K) six times per
        // material, and it used to do a dictionary lookup on each, plus `new ColorTableRowOverride()` on
        // every miss: a heap allocation per pixel, on the most common path for any index texture whose
        // rows aren't all configured. Same shape of defect as the gear layer's per-texel FirstOrDefault.
        const int Pairs = 16;
        float[] aR = new float[Pairs], aG = new float[Pairs], aB = new float[Pairs], aE = new float[Pairs];
        float[] bR = new float[Pairs], bG = new float[Pairs], bB = new float[Pairs], bE = new float[Pairs];
        for (int p = 0; p < Pairs; p++)
        {
            // An absent row keeps the default-constructed values, exactly as the old per-pixel
            // `new ColorTableRowOverride()` fallback did.
            var pair = rows.TryGetValue(p, out var r) ? r : new ColorTableRowOverride();
            aR[p] = pair.A.DiffuseR; aG[p] = pair.A.DiffuseG; aB[p] = pair.A.DiffuseB; aE[p] = pair.A.Emissive;
            bR[p] = pair.B.DiffuseR; bG[p] = pair.B.DiffuseG; bB[p] = pair.B.DiffuseB; bE[p] = pair.B.Emissive;
        }

        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float ovA = ov[i + 3] / 255f;
                if (ovA <= 0f) continue;

                int   pairIdx = idx[i]     / 17;        // red → pair 0–15
                float blendA  = idx[i + 1] / 255f;      // green → lerp B→A (1 = full A, 0 = full B)

                float dr = bR[pairIdx] + (aR[pairIdx] - bR[pairIdx]) * blendA;
                float dg = bG[pairIdx] + (aG[pairIdx] - bG[pairIdx]) * blendA;
                float db = bB[pairIdx] + (aB[pairIdx] - bB[pairIdx]) * blendA;
                float em = bE[pairIdx] + (aE[pairIdx] - bE[pairIdx]) * blendA;

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
    /// Whether a shell can be cut for this overlay at all — the precondition on the auto-promotion in
    /// <see cref="RenderModeInference.ShouldPromoteToGear"/>. True for every surface the shell builder knows
    /// how to cut: the body, and the character's own face, hair, tail and ears. False for gear, accessories,
    /// weapons, mounts and minions, which have no shell path and never will.
    /// <para/>
    /// Deliberately PATH-based only, never "is that surface loaded right now". The editor mirrors this
    /// predicate to enable or grey its Cloth/Glow controls, and a live-state-dependent answer would make
    /// those flicker as the draw-object walk comes and goes. "This can never be shelled" and "this isn't
    /// drawn at the moment" are different failures with different remedies; the second is the shell
    /// builder's to report, and it does (see ResolveHumanSurface).
    /// <para/>
    /// An overlay that names no material at all is left shellable: it can't be placed either way, so this
    /// keeps the prior behaviour rather than silently dropping a feature from it.
    /// </summary>
    internal static bool CanRenderAsShell(OverlayDescriptor d)
        => ShellSurface.CanShell(d.MaterialGamePaths);

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

        // ISLANDS IN PARALLEL. Measured at 1717ms across 5 calls — 61% of ambient occlusion, half of the
        // whole blend — and every iteration is independent: it reads shared inputs read-only, writes only
        // texels carrying its OWN label, and stores its cached blurs at its own index of Bd/Cd (which
        // Reset pre-sizes). Disjoint writes mean the result does not depend on the order, so this stays
        // byte-identical to the serial version — which matters, because the output filenames are content
        // hashes and a different byte would re-bake and re-upload every texture.
        //
        // Held to half the cores rather than all of them: each island allocates eight crop buffers, and a
        // large island's crop approaches the full map, so unbounded width here trades a fixed win for an
        // unbounded memory spike. The box blurs inside are themselves parallel, so the remaining cores are
        // not idle — they fill in underneath.
        var islandOpts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) };
        Parallel.For(1, n, islandOpts, L =>
        {
            if (bx1[L] < 0) return;
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
        });

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

    /// <summary>Any sub-row in this option asks for glow. Narrower than
    /// <see cref="RenderModeInference.HasCloth"/>, which also answers true for sphere/metal — used only to
    /// decide whether the promotion notice is about glow specifically.</summary>
    private static bool HasEmissiveRow(List<ColorTableRowPreset>? rows)
        => rows?.Any(r => r.SubRowA?.Emissive > 0f || r.SubRowB?.Emissive > 0f) == true;

    /// <summary>
    /// Tell the author, once per mod per session, that a Glow they declared on a skin layer now renders as
    /// a cloth shell. Silent unless glow is actually what moved it — sphere/metal promoted before this
    /// change too, so saying "your look changed" for those would be noise.
    /// <para/>
    /// Marshalled onto the framework thread: the composite runs off it, and ChatGui's queue is not
    /// safe to enqueue into concurrently with the tick that drains it.
    /// </summary>
    /// <summary>
    /// The highest priority <see cref="TryRaisePriorityAbove"/> has already tried to set this session. The
    /// loop-stop: a write Penumbra accepts but does not apply would otherwise be re-attempted forever, since
    /// the read-back keeps reporting the old value and the target keeps clearing it. Reset the moment every
    /// path resolves to us again, so a genuine recurrence later in the session is still acted on.
    ///
    /// Written only from the composite thread, and composites do not overlap meaningfully — a torn read
    /// costs one duplicate attempt, which the same guard then absorbs.
    /// </summary>
    private int _highestPriorityRaiseAttempted = int.MinValue;

    /// <summary>
    /// Raise the managed mod above every mod in <paramref name="owners"/>, which have been CONFIRMED to be
    /// taking paths Proteus publishes. Returns whether the priority was actually changed.
    ///
    /// This is the fix for the whole class of "my overlays half-apply" reports. A tattoo or skin mod that
    /// ships its own copy of a body texture wins that one path on priority, so the diffuse renders as the
    /// other mod's while the normal — which it doesn't ship — stays ours. Nothing on screen, in the UI, or in
    /// any other log line hints at a second mod being involved.
    ///
    /// Safe to run every composite because it is driven by measurement, not suspicion: <see
    /// cref="VerifyRedirectsLive"/> only reaches here for a path proven lost against a live manifest.
    /// <para/>
    /// Convergence is latched on what was ATTEMPTED, not on what Penumbra reports back. Guarding purely on
    /// the read-back — which is what this did — only converges if the write is observable through the read,
    /// and there is a live case where it may not be: <c>GetPlayerCollection</c> returns the EFFECTIVE
    /// collection, which for a Mare-synced character is a temporary one, and a priority written there need
    /// not survive or be readable. Every composite would then compute the same target, raise again, and force
    /// another composite — an unbounded loop at roughly one full composite per second, climbing the priority
    /// by one each time. <see cref="_highestPriorityRaiseAttempted"/> caps that at one wasted attempt.
    /// </summary>
    private bool TryRaisePriorityAbove(IReadOnlyCollection<string> owners)
    {
        if (!config.AutoRaiseModPriority) return false;

        var collId = penumbra.GetPlayerCollectionId();
        if (!collId.HasValue) return false;

        int current = penumbra.GetModSettings(collId.Value, SidecarDiscoveryService.ManagedModDir)?.Priority
                   ?? config.ManagedModPriority;

        // The highest priority among the mods actually taking paths from us. A folder we can't read settings
        // for isn't a mod Penumbra knows, so it can't be what beat us — skip rather than guess. Collected
        // separately from `owners` because only these took part in choosing the target, and claiming a count
        // that includes the others would overstate what was measured.
        int highest = int.MinValue;
        string? highestOwner = null;
        int outranked = 0;
        foreach (var owner in owners)
        {
            // NEVER outrank ourselves. The caller already filters this out, but the cost of one slipping
            // through is not a bad message — it is an unbounded self-chase, because raising our priority
            // above our own priority moves the target up with it and the attempted-target latch only ever
            // sees a novel value. Two lines here bound a fault that otherwise runs until the user quits.
            if (string.Equals(owner, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            {
                log.Warning("[Proteus] the managed mod appeared in its own outranked set — ignoring. A path "
                          + "resolving to our own folder is a stale publish, not a conflict.");
                continue;
            }

            if (penumbra.GetModSettings(collId.Value, owner)?.Priority is not { } p) continue;
            outranked++;
            if (p > highest) { highest = p; highestOwner = owner; }
        }
        if (highestOwner == null) return false;

        if (highest == int.MaxValue)
        {
            log.Warning("[Proteus] \"{0}\" sits at the maximum priority ({1}), so the managed mod cannot be "
                      + "raised above it — move that mod down instead.", highestOwner, int.MaxValue);
            return false;
        }

        int target = highest + 1;
        if (target <= current) return false;   // already above it; whatever beat us wasn't priority

        if (target <= _highestPriorityRaiseAttempted)
        {
            log.Warning("[Proteus] already raised the managed mod to {0} this session and \"{1}\" still wins "
                      + "its paths — the priority change is not taking effect (a temporary collection, e.g. "
                      + "one Mare created, cannot always be written to). Not retrying; set it by hand in "
                      + "Penumbra if the overlays stay missing.", target, highestOwner);
            return false;
        }
        _highestPriorityRaiseAttempted = target;

        var ec = penumbra.SetModPriority(collId.Value, SidecarDiscoveryService.ManagedModDir, target);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] could not raise the managed mod's priority to {0}: {1}", target, ec);
            return false;
        }

        // Accepted is not the same as applied. Say so here rather than leaving the next composite to
        // rediscover the same loss and blame the mod again.
        if (penumbra.GetModSettings(collId.Value, SidecarDiscoveryService.ManagedModDir)?.Priority is { } after
            && after != target)
            log.Warning("[Proteus] Penumbra accepted the priority change to {0} but still reports {1} — the "
                      + "collection in use may be a temporary one that cannot be written to.", target, after);

        config.ManagedModPriority = target;
        config.Save();
        log.Information("[Proteus] raised managed mod priority {0} -> {1}, above \"{2}\" ({3})",
            current, target, highestOwner, highest);

        // Two whole phrases rather than an inline "(s)": the one-mod case has no count in it at all, and
        // the many case labels its count instead of inflecting a noun, which is the only form that
        // translates into every target language.
        var names = outranked == 1
            ? string.Format(Loc.Localize("Chat.PriorityRaised.One.Fmt", "\"{0}\""), highestOwner)
            : string.Format(Loc.Localize("Chat.PriorityRaised.Many.Fmt", "\"{0}\" and {1} more"),
                highestOwner, outranked - 1);
        var msg = string.Format(Loc.Localize("Chat.PriorityRaised.Fmt",
            "[Proteus] {0} was overriding the skin textures Proteus composites your overlays into, so they "
            + "could not show up. Raised Proteus' Penumbra priority to {1} to fix it. (Turn off "
            + "\"Auto-raise mod priority\" in Proteus' settings if you'd rather it didn't.)"), names, target);
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 45).Build()));

        // The redirects are already published; only who wins them changed. A recomposite is the simplest way
        // to get Penumbra to recompute and the character to re-sample, and the guard above means it cannot
        // become a loop.
        TriggerRecomposite("priority-raised", force: true);
        return true;
    }

    /// <summary>
    /// Which mod folder was last REPORTED as taking each path off us, so the notice below fires on a change
    /// of state rather than on every composite. Cleared for a path the moment we win it back, so a
    /// recurrence — the mod reinstalled, a different one installed above us — is announced again.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _reportedRedirectLosses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Say in chat that another mod has taken a path Proteus composites, because the log cannot be the only
    /// place this appears. The symptom — overlays half-applying, or not at all — looks exactly like Proteus
    /// being broken, and nothing on screen or in the UI hints that a second mod is overwriting the same
    /// texture. This is the one failure where naming the culprit IS the fix.
    ///
    /// Once per path per change of owner. A composite runs on every gear change and zone, so an unconditional
    /// print would be unusable; a plain once-per-session latch would stay quiet after the user fixes the
    /// priority and a different mod later takes the same path.
    /// <para/>
    /// Marshalled onto the framework thread for the same reason as <see cref="NotifyGlowPromoted"/>: the
    /// composite runs off it, and ChatGui's queue is not safe to enqueue into concurrently with the tick
    /// that drains it.
    /// </summary>
    private void NotifyRedirectLost(string gamePath, string? winningDisk)
    {
        var owner = ModFolderOf(winningDisk) ?? winningDisk;
        if (string.IsNullOrEmpty(owner)) return;   // nothing provides it — not another mod's doing

        if (_reportedRedirectLosses.TryGetValue(gamePath, out var reported)
            && string.Equals(reported, owner, StringComparison.OrdinalIgnoreCase))
            return;
        _reportedRedirectLosses[gamePath] = owner;

        // The file name alone: "bibo_mid_base.tex" is recognisable, the full invented game path is not, and a
        // chat line has no room for it. The log line right above carries the full path for anyone who needs it.
        var msg = string.Format(Loc.Localize("Chat.RedirectLost.Fmt",
            "[Proteus] \"{0}\" overrides {1}, which Proteus composites your overlays into — so they will "
            + "not show up. Fix: in Penumbra, raise the \"Proteus\" mod's priority above \"{0}\"."),
            owner, Path.GetFileName(gamePath));
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 17).Build()));
    }

    private void NotifyGlowPromoted(OverlayEntry entry, List<ColorTableRowPreset>? rows)
    {
        if (!HasEmissiveRow(rows) || !_glowPromotedMods.TryAdd(entry.ModDirectory, 0)) return;

        var msg = string.Format(Loc.Localize("Chat.GlowPromoted.Fmt",
            "[Proteus] \"{0}\" sets Glow on a skin layer. Skin can no longer glow, so that option now "
            + "renders as a cloth layer — it needs a free accessory to sit on, and its surface will look "
            + "slightly different."), entry.ModName);
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
    }

    /// <summary>
    /// A skin overlay asked for something only a shell can render (glow, sphere, metal, specular — or it
    /// sits above gear), but it paints a surface no shell can be cut from: the face, hair, or a tail. The
    /// overlay stays skin and loses that feature, which is the only correct outcome — the alternative,
    /// promoting it anyway, wrapped the body in a shell carrying the face's art.
    /// </summary>
    private void NotifyNoShellSurface(OverlayEntry entry, List<ColorTableRowPreset>? rows, OverlayDescriptor d)
    {
        if (!_noShellMods.TryAdd(entry.ModDirectory, 0)) return;

        log.Information("[Proteus] no shell surface for \"{0}\" (layer {1}): overlay targets [{2}] — not a "
            + "surface a shell can be cut from, so it stays skin and its Glow/Cloth features go unrendered",
            entry.ModName, d.Layer, string.Join(", ", d.MaterialGamePaths));

        // Chat only when a glow is what's being lost: that is the visible symptom the user came for. A plain
        // Cloth overlay loses only sphere/metal, which never showed there in the first place.
        if (!HasEmissiveRow(rows)) return;

        var msg = string.Format(Loc.Localize("Chat.NoShellSurface.Fmt",
            "[Proteus] \"{0}\" sets Glow on an overlay Proteus cannot build a layer for. Glow needs a layer "
            + "over the mesh, and that only works on your own skin — body, face, hair, tail or ears — not "
            + "on gear, accessories or weapons."), entry.ModName);
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
    }

    /// <summary>
    /// Key for the inherited-mask-colorset table: one entry per (body material, mod). NUL-joined because
    /// neither half can contain it, and the dictionary itself is OrdinalIgnoreCase — the same comparison
    /// both <c>byMaterial</c> and every mod-directory lookup already use.
    /// </summary>
    private static string MaskFallbackKey(string mtrlGamePath, string modDir) => mtrlGamePath + '\0' + modDir;

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

        // Sixteen possible row pairs, resolved once rather than looked up per texel — see
        // ApplyIndexedOverlay.
        //
        // A separate `present` flag, not a NaN sentinel in the value array: NaN cannot be told apart from
        // a row whose Opacity genuinely IS NaN (corrupt sidecar JSON, or a deserializer turning a
        // malformed number into one), and that row would then be silently skipped rather than applied.
        const int Pairs = 16;
        var present = new bool[Pairs];
        var opA = new float[Pairs];
        var opB = new float[Pairs];
        for (int p = 0; p < Pairs; p++)
            if (rows.TryGetValue(p, out var r)) { present[p] = true; opA[p] = r.A.Opacity; opB[p] = r.B.Opacity; }

        ParallelPixels(0, dst.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = dst[i + 3] / 255f;
                if (a <= 0f) continue;
                int pairIdx = idx[i] / 17;
                if (!present[pairIdx]) continue;              // no row configured for this pair
                float blendA = idx[i + 1] / 255f;
                float op = opB[pairIdx] + (opA[pairIdx] - opB[pairIdx]) * blendA;
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

    /// <summary>
    /// Bump this whenever the WRITER's output changes for identical inputs — the .tex header layout, the
    /// mip policy, or the BC7 encoder.
    ///
    /// It is folded into every output filename because a content hash alone only covers the inputs. Change
    /// the encoder without bumping this and the new bytes land under the OLD name: the file on disk is
    /// right, but everything downstream is keyed on the path and keeps serving the previous version — the
    /// game's texture cache, every paired client's content cache, and this run's own stash reuse (which
    /// treats a matching name as proof of matching bytes and skips the encode entirely). The symptom is a
    /// texture that refuses to update until some unrelated edit happens to change a pixel.
    /// </summary>
    internal const int OutputFormatVersion = 1;

    /// <summary>
    /// Eight hex chars identifying <paramref name="data"/> (plus any <paramref name="salt"/> values that
    /// change how it will be encoded) — the filename suffix for every skin texture and material we write.
    ///
    /// This replaced a per-run GUID. The GUID's job was to force a cache miss: FFXIV caches textures by
    /// resolved path, so reusing a filename means the game keeps rendering the old bytes. A content hash
    /// keeps that guarantee where it matters — changed bytes ⇒ changed name ⇒ miss — while giving the
    /// reverse for free: UNCHANGED bytes keep their name. That second half is what sync plugins need. They
    /// key transfers on content, and a GUID renamed all ~300 MB of skin output on every composite, so a
    /// paired client re-fetched the whole set each time a slider moved and often never finished — leaving
    /// the body invisible, because Bibo-style skin paths are mod-invented and have no vanilla fallback to
    /// fail back to.
    /// </summary>
    internal static string ContentTag(byte[] data, params int[] salt)
    {
        // FNV-1a over 8 bytes at a time. The byte-wise variant costs ~1s across five 64 MB skin textures,
        // which is real time on a path that already runs per keystroke; this is ~8× cheaper and the tag
        // only has to be collision-resistant against the PREVIOUS content of one texture, not globally.
        ulong h = 14695981039346656037;
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var w in words) { h ^= w; h *= 1099511628211; }
        for (int i = words.Length * 8; i < data.Length; i++) { h ^= data[i]; h *= 1099511628211; }
        foreach (var s in salt) { h ^= (ulong)s; h *= 1099511628211; }
        return h.ToString("x16")[..8];
    }
}
