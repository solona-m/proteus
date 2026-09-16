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
    /// <summary>2 added <see cref="DesignBinding.CharacterMods"/>. A version-1 store loads unchanged; its
    /// bindings simply have no character snapshot until they are next captured.</summary>
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
    /// The Penumbra settings of every mod on the character when the design was saved — whatever it drew
    /// from, plus every enabled mod whose selected options reach what it drew, plus every Proteus mod. Read
    /// underneath any temporary settings, because a restore writes them back permanently.
    /// <para/>
    /// Empty on a binding captured before this existed. Such a binding restores the way it always did —
    /// Proteus mods only — until it is captured again.
    /// </summary>
    public List<PenumbraModSetting> CharacterMods { get; set; } = new();

    /// <summary>The game paths the character was drawing at capture. A restore switches off the unbound mods
    /// that reach these — the look this design is — and nothing else.</summary>
    public List<string> CharacterGamePaths { get; set; } = new();

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

    // The live colour / gear / stack overrides this design publishes, and the rules for editing them.
    // Shared with PresetService, which owns a second bag on its own compositor channel — see
    // OverlayOverrideBag for why one implementation rather than two.
    private readonly OverlayOverrideBag overrides;

    /// <summary>
    /// Raised when a design takes over the look and any per-mod preset pinned on top of the previous one
    /// no longer applies. Wired to <c>PresetService.ClearAllApplied</c> in Plugin.cs; an event rather than
    /// a direct call because PresetService already depends on this service (it captures through
    /// <see cref="CaptureMod"/>), and naming it here would close that into a construction cycle.
    /// <para/>
    /// Only the "currently applied" pins drop — the saved presets themselves are never touched.
    /// </summary>
    public event Action? PresetsSuperseded;

    // All of the below are touched only on the framework thread (watcher callbacks marshal first).
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

        overrides = new OverlayOverrideBag(
            compositor.SetActiveColorOverride, compositor.SetActiveGearOverride, compositor.SetActiveStackOverride);

        storePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "design_bindings.json");
        Load();

        glamourer.LocalPlayerStateFinalized += OnGlamourerStateFinalized;
        glamourer.LocalPlayerStateChangedAny += OnGlamourerStateChangedAny;
        penumbra.ModSettingChanged += OnPenumbraModSettingChanged;

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
        glamourer.LocalPlayerStateChangedAny -= OnGlamourerStateChangedAny;
        penumbra.ModSettingChanged -= OnPenumbraModSettingChanged;
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

            // Only a design the character is actually WEARING may be captured. Glamourer rewrites a design's
            // file on every edit — a rename, a folder move, a tweak to a design nobody has on — and capturing on
            // each of those wrote the current look into an unrelated design's binding, which that design's next
            // apply then restored instead of its own. Judged exactly as an apply is (carriers retired, the same
            // StateMatches), so anything this captures is something the apply path can recognise again.
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

            lock (gate)
            {
                // The character snapshot follows off-thread; until it lands, keep the previous one rather than
                // leaving the binding looking as if it had never had one.
                if (store.Bindings.TryGetValue(designId, out var previous))
                {
                    binding.CharacterMods      = previous.CharacterMods;
                    binding.CharacterGamePaths = previous.CharacterGamePaths;
                }
                store.Bindings[designId] = binding;
                Save();
            }

            CaptureCharacterMods(binding, collId.Value);

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
        // The EFFECTIVE colours, which means asking the compositor rather than this service's own bag: a
        // preset applied on top of this binding is what the player is looking at, and "Update binding"
        // must fold that in rather than silently reverting to what the binding already held.
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

    /// <summary>
    /// Snapshot ONE mod's current live state: its Penumbra enable/priority/option settings plus the
    /// effective colours, gear and stack order — effective meaning what the composite is actually using,
    /// so unsaved editor tweaks and any pinned preset are included.
    /// <para/>
    /// Public because presets capture through exactly this call. A preset then keeps only the portable
    /// fields (see <see cref="ModPreset"/>); sharing the capture rather than writing a second one is
    /// what stops the two drifting over which colour sources count — content packs' own option colours
    /// were once missed here, and one implementation can only be wrong once.
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

    // Snapshot every discovered Proteus mod's current live state (Penumbra enable/priority/options +
    // effective colors) into fresh binding entries. Shared by design-save capture and the manual
    // "Update binding" action.
    private List<ProteusModBinding> BuildModBindings(Guid collId)
        => discovery.DiscoverAll().Select(e => CaptureMod(e, collId)).ToList();

    // The mod-wide tab/stack order to record: the effective override for this mod if one is live (so an
    // in-progress restack, or a preset's arrangement, is captured), else the global config order. Empty
    // when neither has one.
    private List<string> CaptureStackOrder(string modDir)
    {
        if (compositor.EffectiveStackOverrideFor(modDir) is { } live)
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
        lock (gate)
        {
            if (suppressEcho) suppressUntilTick = Environment.TickCount64 + RestoreSuppressMs;
            activeDesignId = designId;
        }

        PersistActiveDesignId(designId);

        // A design is a whole-look switch, so it supersedes any per-mod preset pinned on top of the
        // previous look — otherwise applying a design would appear to do nothing for that one mod. The
        // presets themselves are untouched; only the "currently applied" pins drop.
        PresetsSuperseded?.Invoke();

        // Clone so live colour edits preview without mutating the stored binding (they only fold in via
        // UpdateActiveBindingFromCurrentState).
        overrides.Adopt(CloneOverrides(b.Mods), CloneGear(b.Mods), CloneStack(b.Mods));
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
    /// <para/>
    /// The exception is <paramref name="stripImported"/>: an imported pack (<see cref="ProteusMetadata.HasContent"/>)
    /// is NOT held then. Its pieces are drawn through Proteus on a carrier accessory rather than a gear slot, so
    /// unequipping the slots a design leaves unset can't take them off — switching the pack off is the only way
    /// they leave with the previous look. Its remaining Penumbra files only serve those pieces, so nothing else
    /// goes with them.
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
        bool hadDesign;
        lock (gate)
        {
            hadDesign      = activeDesignId != null;
            activeDesignId = null;
        }

        // Both of these run even when nothing was active, because both are about the state OUTSIDE this
        // object: the persisted pointer can be non-null while nothing is active (a boot restore that
        // abstained), and a revert arriving mid-boot must supersede the reconstruction rather than let
        // it re-adopt what the player just took off. PersistActiveDesignId no-ops when already null, and
        // FinishBootRestore is a one-shot, so neither costs anything on the ambient zone-in path.
        PersistActiveDesignId(null);
        if (BootRestoreArmed) FinishBootRestore("superseded by an explicit clear");
        // Nothing is being restored any more, so a temporary setting landing now is the player's (or
        // Glamourer's, for whatever replaced the design) and must be left alone.
        DisarmTemporaryGuard();
        // And a character restore still planning in the background is for a look that is no longer wanted.
        characterWorkGeneration++;

        // Ordered so the return value keeps its old meaning: "anything was active" is true when either
        // the design pointer or the override bag held something. Clear() un-publishes on its own.
        return overrides.Clear() | hadDesign;
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

    /// <param name="stripImported">Also switch off imported Proteus packs the binding didn't capture — see
    /// <see cref="UnboundContentMods(HashSet{string}, IReadOnlyList{OverlayEntry}, bool)"/>. Set on the same
    /// applies that unequip the slots a design leaves unset.</param>
    public void Restore(Guid designId, bool stripImported = false)
    {
        DesignBinding? b;
        lock (gate) store.Bindings.TryGetValue(designId, out b);
        if (b == null) return;

        if (b.HasCharacterSnapshot) RestoreCharacter(b, designId, stripImported);
        else                        RestoreProteusOnly(b, designId, stripImported);
    }

    /// <summary>
    /// The restore a binding captured before character snapshots gets: Proteus mods only. Kept exactly as it
    /// was, so an old binding does what it always did until it is captured again.
    /// </summary>
    private void RestoreProteusOnly(DesignBinding b, Guid designId, bool stripImported)
    {
        // Supersedes any character restore still planning — its writes would otherwise land after these.
        characterWorkGeneration++;

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

    // ── Character snapshot (capture) ────────────────────────────────────────────

    // Bumped by everything that supersedes an in-flight character restore — another restore of either kind, or
    // a clear (unbound design, revert, unbind) — so a plan that finishes after it drops its writes instead of
    // landing them on top of the newer look. Captures don't use it: they check the binding instance instead.
    // Framework thread only.
    private int characterWorkGeneration;

    /// <summary>
    /// Fill <paramref name="binding"/>'s character snapshot. The IPC reads happen here, on the framework
    /// thread; the manifest parsing — every enabled mod in the collection — happens on a worker; the result is
    /// stored back on the framework thread, and only if the binding hasn't been replaced in the meantime.
    /// </summary>
    private void CaptureCharacterMods(DesignBinding binding, Guid collId)
    {
        var modsRoot    = penumbra.GetModDirectory();
        var resources   = penumbra.GetActivePlayerResourceMap();
        var permanent   = penumbra.GetCollectionModSettings(collId, ignoreTemporary: true);
        var names       = penumbra.GetAllMods();
        if (modsRoot == null || resources == null || permanent == null || names == null)
        {
            // Nothing to read the character from (not drawn, or Penumbra gone). The binding keeps whatever
            // snapshot it had, which is better than wiping it on a design saved from the title screen.
            log.Information("[Proteus] design-binding: character snapshot skipped for {0} — Penumbra state unreadable (root={1}, tree={2}, settings={3}).",
                binding.DesignName ?? binding.DesignId.ToString(), modsRoot != null, resources != null, permanent != null);
            return;
        }

        var proteusDirs = discovery.DiscoverAll().Select(e => e.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Task.Run(() =>
        {
            try
            {
                var (mods, paths) = SelectCharacterMods(modsRoot, resources, permanent, names, proteusDirs,
                    (dir, sel) => ModActivePaths.Read(Path.Combine(modsRoot, dir), sel));

                framework.RunOnFrameworkThread(() =>
                {
                    lock (gate)
                    {
                        // Replaced by a newer capture while we parsed: that one owns the snapshot now.
                        if (!store.Bindings.TryGetValue(binding.DesignId, out var current) || !ReferenceEquals(current, binding))
                            return;
                        binding.CharacterMods      = mods;
                        binding.CharacterGamePaths = paths;
                        SaveDeferred();
                    }
                    log.Information("[Proteus] design-binding: captured {0} mod(s) on the character for {1} ({2} game paths).",
                        mods.Count, binding.DesignName ?? binding.DesignId.ToString(), paths.Count);
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] design-binding: character snapshot failed for {0}", binding.DesignId);
            }
        });
    }

    /// <summary>
    /// Which mods make up the character, and what it was drawing.
    /// <list type="number">
    /// <item>Every mod a drawn file resolves into — read straight off the resource tree.</item>
    /// <item>Every enabled mod whose selected options reach a drawn path or a drawn object's metadata. This is
    /// what catches a mod LOSING a conflict (drawn by nobody, but it would be the moment its rival went), and a
    /// mod that only edits metadata — a body scaling, a shape key — which no file in the tree names.</item>
    /// <item>Every Proteus mod, whatever its state: its output is drawn through the managed mod, so the tree
    /// never names it, and the Proteus-only restore always covered all of them.</item>
    /// </list>
    /// Settings are the PERMANENT ones. A mod drawn only because a temporary setting switched it on is still
    /// recorded, with the off state underneath — a restore clears the temporary setting, so that is what the
    /// look will be.
    /// </summary>
    internal static (List<PenumbraModSetting> Mods, List<string> GamePaths) SelectCharacterMods(
        string modsRoot,
        IReadOnlyDictionary<string, HashSet<string>> resources,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> permanent,
        IReadOnlyDictionary<string, string> installed,
        IReadOnlySet<string> proteusDirs,
        Func<string, IReadOnlyDictionary<string, List<string>>?, ModActivePaths.Contribution?> contribution)
    {
        var paths = ModActivePaths.NewPathSet();
        foreach (var gamePaths in resources.Values)
            foreach (var p in gamePaths)
                paths.Add(ModActivePaths.NormalizePath(p));
        var targets = ModActivePaths.TargetsOf(paths);

        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resolved in resources.Keys)
            if (OwningMod(modsRoot, resolved) is { } owner && installed.ContainsKey(owner))
                chosen.Add(owner);

        foreach (var (dir, s) in permanent)
        {
            if (!s.Enabled || chosen.Contains(dir) || !installed.ContainsKey(dir)) continue;
            if (contribution(dir, s.Options) is { } c && Reaches(c, paths, targets))
                chosen.Add(dir);
        }

        foreach (var dir in proteusDirs)
            if (installed.ContainsKey(dir))
                chosen.Add(dir);

        chosen.Remove(SidecarDiscoveryService.ManagedModDir);

        var mods = chosen
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(dir =>
            {
                permanent.TryGetValue(dir, out var s);
                return new PenumbraModSetting
                {
                    ModDirectory = dir,
                    ModName      = installed.TryGetValue(dir, out var n) ? n : null,
                    Enabled      = s.Options != null && s.Enabled,
                    Priority     = s.Options != null ? s.Priority : 0,
                    Options      = s.Options?.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value))
                                   ?? new Dictionary<string, List<string>>(),
                };
            })
            .ToList();

        return (mods, paths.OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    /// <summary>The mod directory a resolved file lives in, or null when it is not under the mods folder (game
    /// data, or a temporary mod's file somewhere else).</summary>
    internal static string? OwningMod(string modsRoot, string resolvedPath)
    {
        var root = modsRoot.Replace('\\', '/').TrimEnd('/') + "/";
        var file = resolvedPath.Replace('\\', '/');
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var rest  = file.AsSpan(root.Length);
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash].ToString() : null;
    }

    private static bool Reaches(ModActivePaths.Contribution c, HashSet<string> paths, ModActivePaths.CharacterTargets targets)
        => c.GamePaths.Overlaps(paths) || targets.IsReachedByAny(c.Meta);

    // ── Character snapshot (restore) ────────────────────────────────────────────

    /// <summary>Every Penumbra write one character restore makes, in the order it makes them.</summary>
    internal sealed record RestorePlan(
        List<string> ClearTemporary,
        List<string> Disable,
        List<(string ModDirectory, string Group, List<string> Options)> SetOptions,
        List<(string ModDirectory, int Priority)> SetPriority,
        List<(string ModDirectory, bool Enabled)> SetEnabled,
        List<string> Missing,
        int PriorityOffset)
    {
        internal int WriteCount => ClearTemporary.Count + Disable.Count + SetOptions.Count + SetPriority.Count + SetEnabled.Count;
    }

    /// <summary>
    /// Work out a character restore without touching anything.
    /// <list type="bullet">
    /// <item>Bound mods get their recorded enable state, options and priority — skipped where Penumbra already
    /// holds that value, so an apply that changes nothing makes no writes. Bound mods no longer installed are
    /// reported, not written.</item>
    /// <item>Every mod that is ON now (temporary settings included — that is what draws), not bound, and reaches
    /// what the design drew is switched off. So is every unbound Proteus mod, as the Proteus-only restore always
    /// did, except the ones that also ship their own content (<paramref name="heldProteus"/>).</item>
    /// <item>Temporary settings go from every mod written or switched off, or they would mask the write.</item>
    /// <item>Bound priorities are all raised by one shared offset, just far enough that each enabled bound mod
    /// outranks every rival still on that contests any of its paths. See <see cref="ComputePriorityOffset"/>.</item>
    /// </list>
    /// </summary>
    internal static RestorePlan PlanRestore(
        IReadOnlyList<PenumbraModSetting> bound,
        IReadOnlyCollection<string> capturedGamePaths,
        IReadOnlySet<string> installed,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> permanent,
        IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> effective,
        IReadOnlySet<string> proteusDirs,
        IReadOnlySet<string> heldProteus,
        Func<string, IReadOnlyDictionary<string, List<string>>?, ModActivePaths.Contribution?> contribution)
    {
        var boundDirs = bound.Select(m => m.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present   = bound.Where(m => installed.Contains(m.ModDirectory)).ToList();
        var missing   = bound.Where(m => !installed.Contains(m.ModDirectory)).Select(m => m.ModDirectory).ToList();

        var paths = ModActivePaths.NewPathSet();
        foreach (var p in capturedGamePaths) paths.Add(ModActivePaths.NormalizePath(p));
        var targets = ModActivePaths.TargetsOf(paths);

        // ── Switch-offs ──
        var disable = new List<string>();
        var disabling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Disable(string dir) { if (disabling.Add(dir)) disable.Add(dir); }

        foreach (var (dir, s) in effective.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!s.Enabled || boundDirs.Contains(dir)) continue;
            if (string.Equals(dir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (contribution(dir, s.Options) is { } c && Reaches(c, paths, targets))
                Disable(dir);
        }

        foreach (var dir in proteusDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (boundDirs.Contains(dir) || heldProteus.Contains(dir)) continue;
            if (IsOn(permanent, dir) || IsOn(effective, dir)) Disable(dir);
        }

        // ── Temporary settings to clear: anything we write, or switch off, that one is holding ──
        var clearTemporary = present.Select(m => m.ModDirectory).Concat(disable)
            .Where(dir => effective.TryGetValue(dir, out var s) && s.Temporary)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A disable only needs a permanent write if the permanent setting is on; one that was only on through
        // a temporary setting is already off once that is cleared.
        var disableWrites = disable.Where(dir => IsOn(permanent, dir)).ToList();

        // ── Priority offset against the rivals that stay on ──
        var rivals = effective
            .Where(kv => kv.Value.Enabled && !boundDirs.Contains(kv.Key) && !disabling.Contains(kv.Key)
                      && !string.Equals(kv.Key, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Value.Priority, contribution(kv.Key, kv.Value.Options)))
            .ToList();
        var boundContributions = present
            .Where(m => m.Enabled)
            .Select(m => (m.Priority, contribution(m.ModDirectory, m.Options)))
            .ToList();
        var offset = ComputePriorityOffset(boundContributions, rivals);

        // ── Bound writes, diffed against the permanent settings ──
        var setOptions  = new List<(string, string, List<string>)>();
        var setPriority = new List<(string, int)>();
        var setEnabled  = new List<(string, bool)>();
        foreach (var m in present)
        {
            permanent.TryGetValue(m.ModDirectory, out var cur);
            var curKnown = cur.Options != null;

            foreach (var (group, sel) in m.Options)
                if (!curKnown || !cur.Options!.TryGetValue(group, out var curSel) || !SameSelection(curSel, sel))
                    setOptions.Add((m.ModDirectory, group, sel));

            var priority = m.Priority + offset;
            if (!curKnown || cur.Priority != priority)
                setPriority.Add((m.ModDirectory, priority));

            if ((curKnown && cur.Enabled) != m.Enabled)
                setEnabled.Add((m.ModDirectory, m.Enabled));
        }

        return new RestorePlan(clearTemporary, disableWrites, setOptions, setPriority, setEnabled, missing, offset);
    }

    private static bool IsOn(IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> settings, string dir)
        => settings.TryGetValue(dir, out var s) && s.Enabled;

    private static bool SameSelection(List<string> a, List<string> b)
        => a.Count == b.Count && a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    /// <summary>
    /// How far every bound mod's recorded priority must rise so each enabled bound mod beats every rival that
    /// contests one of its paths or metadata objects. ONE offset for all of them, not one per mod: the bound
    /// mods' order among themselves is part of the look, and a shared offset keeps it exactly. Zero when
    /// nothing contests them, so an uncontested restore writes the recorded priorities unchanged.
    /// <para/>
    /// Strictly above, never equal: at equal priority Penumbra keeps whichever mod it happened to load first,
    /// and that order changes between sessions.
    /// <para/>
    /// A mod whose manifest couldn't be read (null contribution) contests nothing — bumping for every
    /// unreadable mod would inflate priorities for conflicts nobody can see.
    /// </summary>
    internal static int ComputePriorityOffset(
        IReadOnlyList<(int Priority, ModActivePaths.Contribution? Contribution)> bound,
        IReadOnlyList<(int Priority, ModActivePaths.Contribution? Contribution)> rivals)
    {
        long offset = 0;
        foreach (var (priority, c) in bound)
        {
            if (c == null) continue;
            foreach (var (rivalPriority, rc) in rivals)
            {
                if (rc == null || !c.Meets(rc)) continue;
                offset = Math.Max(offset, (long)rivalPriority + 1 - priority);
            }
        }
        return (int)Math.Clamp(offset, 0, int.MaxValue / 2);
    }

    /// <summary>
    /// Restore a whole-character binding: publish its overrides now (as every restore does), then read the
    /// collection, plan off-thread and write on the framework thread. The writes happen inside one compositor
    /// suppression scope, so dozens of Penumbra events cost one recomposite rather than one each.
    /// </summary>
    private void RestoreCharacter(DesignBinding b, Guid designId, bool stripImported)
    {
        AdoptOverrides(b, designId, suppressEcho: true);

        var collId      = penumbra.GetPlayerCollectionId();
        var modsRoot    = penumbra.GetModDirectory();
        var names       = penumbra.GetAllMods();
        var permanent   = collId is { } c1 ? penumbra.GetCollectionModSettings(c1, ignoreTemporary: true)  : null;
        var effective   = collId is { } c2 ? penumbra.GetCollectionModSettings(c2, ignoreTemporary: false) : null;
        if (collId == null || modsRoot == null || names == null || permanent == null || effective == null)
        {
            log.Warning("[Proteus] design-restore: Penumbra state unreadable — overrides applied, mod settings left as they are.");
            compositor.TriggerRecomposite($"design-restore:{designId}");
            return;
        }

        var discovered = discovery.DiscoverAll();
        var bound      = b.CharacterMods.ToList();
        var captured   = b.CharacterGamePaths.ToList();
        var boundAll   = bound.Select(m => m.ModDirectory)
                              .Concat(b.Mods.Select(m => m.ModDirectory))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generation = ++characterWorkGeneration;
        var coll       = collId.Value;

        Task.Run(() =>
        {
            try
            {
                var held = UnboundContentMods(boundAll, discovered, stripImported);
                var plan = PlanRestore(bound, captured,
                    names.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    permanent, effective,
                    discovered.Select(e => e.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    held,
                    (dir, sel) => ModActivePaths.Read(Path.Combine(modsRoot, dir), sel));

                framework.RunOnFrameworkThread(() =>
                {
                    // Superseded by a newer restore or a clear, or a different design became active some other
                    // way (a capture adopting its own) — either way these writes are for a look that's gone.
                    if (generation != characterWorkGeneration || ActiveDesignId != designId)
                    {
                        log.Debug("[Proteus] design-restore: plan for {0} superseded by a newer restore — dropped.", designId);
                        return;
                    }
                    ExecuteRestore(b, designId, coll, plan);
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] design-restore: planning failed for {0}", designId);
                framework.RunOnFrameworkThread(() => compositor.TriggerRecomposite($"design-restore:{designId}"));
            }
        });
    }

    private void ExecuteRestore(DesignBinding b, Guid designId, Guid collId, RestorePlan plan)
    {
        // Re-armed here, not only in AdoptOverrides: the plan took a moment, and it is THESE writes whose
        // finalization echo the window exists to ignore.
        lock (gate) suppressUntilTick = Environment.TickCount64 + RestoreSuppressMs;

        using (compositor.SuppressModSettingEvents())
        {
            foreach (var dir in plan.ClearTemporary)
                penumbra.ClearTemporaryModSettings(collId, dir);
            foreach (var dir in plan.Disable)
                penumbra.SetModEnabled(collId, dir, false);
            foreach (var (dir, group, options) in plan.SetOptions)
                penumbra.SetModOption(collId, dir, group, options);
            foreach (var (dir, priority) in plan.SetPriority)
                penumbra.SetModPriority(collId, dir, priority);
            foreach (var (dir, enabled) in plan.SetEnabled)
                penumbra.SetModEnabled(collId, dir, enabled);
        }

        ArmTemporaryGuard(collId, plan.ClearTemporary.Concat(b.CharacterMods.Select(m => m.ModDirectory)).Concat(plan.Disable));

        compositor.TriggerRecomposite($"design-restore:{designId}");
        log.Information(
            "[Proteus] design-restore: {0} — {1} write(s): {2} temporary cleared, {3} switched off, {4} option group(s), {5} priorit(ies), {6} enable change(s); offset +{7}{8}.",
            b.DesignName ?? designId.ToString(), plan.WriteCount, plan.ClearTemporary.Count, plan.Disable.Count,
            plan.SetOptions.Count, plan.SetPriority.Count, plan.SetEnabled.Count, plan.PriorityOffset,
            plan.Missing.Count > 0 ? $"; not installed: {string.Join(", ", plan.Missing)}" : "");
        if (plan.Disable.Count > 0)
            log.Debug("[Proteus] design-restore: switched off {0}", string.Join(", ", plan.Disable));
    }

    // ── Temporary-setting guard ─────────────────────────────────────────────────
    //
    // Glamourer re-asserts a design's associated mods as temporary settings — on the apply itself, and again
    // when automation reapplies after a redraw, which our own recomposite causes. Clearing them once would
    // lose to that. So for a short while after a restore, a temporary setting landing on a mod the restore
    // governs is cleared again — a bounded number of times, so two plugins can never fight forever.

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

        // Next tick, never inline: this runs inside Penumbra's own event dispatch, and editing the collection
        // from there would re-enter the edit that raised it.
        framework.RunOnTick(() =>
        {
            if (Environment.TickCount64 >= temporaryGuardUntil) return;
            if (collId != temporaryGuardCollection || !temporaryGuardMods.Contains(modDir)) return;

            var n = temporaryGuardClears.GetValueOrDefault(modDir);
            if (n >= TemporaryGuardMaxClear) return;

            // NothingChanged when the event was our own clear, or the setting was a removal — both harmless,
            // and neither raises another event, so this never feeds itself.
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
    //
    // All of these are thin delegations to the shared override bag. The rules they used to spell out —
    // peek never creates, an edit does; nested mutation in place, structural change copy-on-write —
    // now live once in OverlayOverrideBag, because presets need exactly the same rules and two copies
    // of them would drift.

    /// <summary>True when a binding is active and supplies colors for this mod.</summary>
    public bool IsOverrideActiveFor(string modDir) => overrides.Governs(modDir);

    /// <inheritdoc cref="OverlayOverrideBag.PeekMaskRows"/>
    public List<ColorTableRowPreset>? PeekMaskRows(string modDir) => overrides.PeekMaskRows(modDir);

    /// <summary>Install the mask rows as this binding's LIVE override, on an actual edit. Live preview
    /// only — the stored binding on disk is untouched until "Update binding". Returns false when no
    /// binding is active, so the caller persists to the metadata instead.</summary>
    public bool SetMaskRows(string modDir, List<ColorTableRowPreset> rows)
        => overrides.SetMaskRows(modDir, rows);

    /// <inheritdoc cref="OverlayOverrideBag.PeekRows"/>
    public List<ColorTableRowPreset>? PeekOverrideRows(string modDir, string? group, string? option)
        => overrides.PeekRows(modDir, group, option);

    /// <summary>Install rows as this binding's LIVE override, on an actual edit. Preview only — the
    /// stored binding is untouched until "Update binding". False when no binding is active.</summary>
    public bool SetOverrideRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows)
        => overrides.SetRows(modDir, group, option, rows);

    /// <summary>
    /// The active design binding's mod-wide tab order for this mod (<see cref="Configuration.ModStackEntry"/>
    /// keys, top-first), or null when no binding overrides it — so the tab strip orders its buttons by the
    /// same source the composite does (see CompositorService.ModStackIndexFor). Falls back to the global
    /// stack config when this returns null.
    /// </summary>
    public IReadOnlyList<string>? ActiveStackOrderFor(string modDir) => overrides.StackOrderFor(modDir);

    /// <summary>
    /// Record a mod-wide tab restack while a design binding is active: into the live stack override (live
    /// preview, folded into the binding on "Update binding"), NOT the global stack config — mirroring how
    /// colour/gear edits stay on the binding. Returns false when no binding is active, so the caller
    /// persists to the global config instead.
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

    /// <inheritdoc cref="OverlayOverrideBag.PeekGear"/>
    /// <remarks>Overlays only. A content pack's glow reads <see cref="PeekContentGearOverride"/>, which
    /// resolves against its own slot rather than <c>Top</c> — see <see cref="OverlayGearOverride.Content"/>
    /// for why the two must not share.</remarks>
    public GearSettingsPreset? PeekGearOverride(string modDir, string group, string option)
        => overrides.PeekGear(modDir, group, option);

    /// <inheritdoc cref="OverlayOverrideBag.GetEditableMaskGear"/>
    public GearSettingsPreset? GetEditableMaskGearOverride(string modDir, OverlayDescriptor seed)
        => overrides.GetEditableMaskGear(modDir, seed);

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
        Guid id;
        lock (gate)
        {
            if (activeDesignId is not { } active) return false;
            id = active;
        }

        // The live copies, OUTSIDE `gate`: the bag has its own lock, and nesting the two here is the
        // only place they would ever meet.
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

            // Serialising must happen under the lock (a consistent `store`), but the write must not:
            // this store reaches tens of MB and the caller is an ImGui button on the framework thread.
            if (touched) SaveDeferred();
        }

        if (!touched) return false;

        log.Information("[Proteus] cleared binding override for {0} [{1}/{2}] from the active design",
            modDir, group ?? "(top)", option ?? "(top)");
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

        DesignBinding updated;
        lock (gate)
        {
            if (activeDesignId != id) return false; // active binding changed underfoot
            updated = new DesignBinding
            {
                DesignId    = id,
                DesignName  = name,
                CapturedUtc = DateTime.UtcNow,
                Mods        = mods,
            };
            if (store.Bindings.TryGetValue(id, out var previous))
            {
                updated.CharacterMods      = previous.CharacterMods;
                updated.CharacterGamePaths = previous.CharacterGamePaths;
            }
            store.Bindings[id] = updated;
            Save();
        }

        CaptureCharacterMods(updated, collId.Value);

        // All three, gear included. This used to skip gear, on the reasoning that the live gear objects
        // were the very ones just captured so replacing them with clones bought nothing. That stopped
        // being true when presets arrived: the capture reads the EFFECTIVE gear, which for a mod wearing
        // a preset comes out of the preset's bag, while this bag still holds the design's older copy.
        // Publishing colours but not gear left the stored binding holding the preset's glow and the
        // screen showing the design's — and the line below then drops the preset that was making up the
        // difference, so the glow visibly switched off the moment you pressed Update.
        overrides.Adopt(CloneOverrides(mods), CloneGear(mods), CloneStack(mods));

        // Everything the update just folded in came from the effective state, presets included, so the
        // binding now holds it outright and the pins have nothing left to say.
        PresetsSuperseded?.Invoke();

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

    // ── The state from before the current apply ─────────────────────────────────
    //
    // Refreshed a tick after anything changes the character, so when a finalization arrives it still holds the
    // state from BEFORE that operation: Glamourer raises an apply's per-field changes and its finalization inside
    // one call, and the refresh they queue runs on the next tick. See AppliedInsteadOfActive for what reads it.
    // Framework thread only; null until the first refresh.

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

        // Only a design the player applied themselves may strip slots. Automation merges several designs, so a
        // slot this one leaves unset is routinely filled by another — and inferred applies are guesses anyway.
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

    // Glamourer's "Nothing" item for an equipment slot is uint.MaxValue - 128 - slot index (ItemManager.NothingId).
    // A small window rather than the exact per-slot value, so the slot-index mapping never has to be copied here.
    private const ulong NothingIdHigh = uint.MaxValue - 128UL;
    private const ulong NothingIdLow  = NothingIdHigh - 31UL;

    /// <summary>
    /// The slots to unequip so the character wears only what <paramref name="design"/> puts on it: every slot the
    /// design does not apply, that the character currently has something in, and that Proteus isn't borrowing.
    /// <para/>
    /// A slot missing from the design counts as unset. A slot already holding Glamourer's Nothing is skipped, so
    /// re-applying the same design asks for nothing. Takes the design UNSTRIPPED: a carrier slot is judged by the
    /// live <paramref name="carriers"/>, not by the design having captured it.
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
    /// Switch off the imported Proteus packs a design's binding didn't capture — the re-apply counterpart of
    /// <see cref="Restore"/>'s <c>stripImported</c>, for when the design is already active and no restore runs.
    /// Their pieces ride a carrier accessory rather than a gear slot, so unequipping can't reach them.
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
    /// Unequip what the applied design leaves unset. Deferred a tick: this is reached from inside Glamourer's own
    /// finalization event, and editing its state from there would re-enter the apply that raised it.
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
    /// <param name="unequipUnset">Also clear the gear slots the matched design leaves unset — see
    /// <see cref="UnsetSlots"/>. Only for an explicit design apply.</param>
    /// <param name="preApply">The state from before this apply, for explicit applies only — see
    /// <see cref="AppliedInsteadOfActive"/>. Automation stacks designs, so "the active design's extra fields
    /// didn't change" says nothing about which one it applied.</param>
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

        // Judge each design on the player's OWN choices: retire the slots Proteus is borrowing (the
        // invisible glasses and the accessory carriers) or nothing would ever match and every Proteus mod
        // would be disabled. The boot restore has always done this; the live path compared designs as
        // saved, so a design saved while the shell was hosted demanded our carrier ring for ever after.
        var carriers = LiveCarriers();
        var pick = MatchBinding(state, carriers, preApply);

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

            // No binding matched, so overrides get dropped and gear dyes revert to metadata. The warning
            // says what it COST; the debug lines below say why, for the handful of candidates that could
            // plausibly have matched. A "REJECTED: <first field that differed>" line for every binding the
            // player owns used to be emitted here as a warning, which fired on the ordinary path (most
            // designs are SUPPOSED not to match the state being applied) — hence both the cap and the level.
            log.Warning("[Proteus] design-binding: NO binding matched the applied state — dropping colour/gear overrides (dyes revert to metadata white).");
            ReportNoMatch(state, carriers);
            HandleUnboundDesign();
            return;
        }

        // Before the already-applied return: re-applying the same design after changing a piece by hand should
        // still take that piece back off.
        if (unequipUnset) UnequipUnsetSlots(pick.Value, carriers);

        if (activeDesignId == pick)
        {
            // Already applied, so no restore runs — but a pack switched on since still has to come off, just as
            // a piece of gear put on since did above.
            if (unequipUnset) DisableUnboundImportedPacks(pick.Value);
            return;
        }
        Restore(pick.Value, stripImported: unequipUnset);
    }

    /// <summary>
    /// The binding that best matches the player's live state, or null when none does.
    /// <para/>
    /// For ambiguous matches (variations of the same outfit share a gear set), prefer the most
    /// *specific* match — the design that constrains the most applied fields (e.g. one that also
    /// matches the applied dye beats a gear-only design). Break remaining ties by the most recently
    /// captured binding, which avoids stale older overrides sticking around.
    /// <para/>
    /// Shared by the live apply path and the boot restore so both resolve a look identically — including
    /// <paramref name="carriers"/>, which both must pass. The live path skipping it is what left a design
    /// saved while the shell was hosted unable to match anything ever again.
    /// </summary>
    /// <param name="carriers">
    /// What Proteus has on the player, so each candidate design can have those slots retired before it is
    /// compared — see <see cref="StripCarriers"/> and <see cref="BestMatches"/>.
    /// </param>
    /// <param name="preApply">The state from just before this apply, on the live path; null at boot, where there
    /// was no apply. See <see cref="AppliedInsteadOfActive"/>.</param>
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
    /// The designs that were really applied when the best match is the design ALREADY active.
    /// <para/>
    /// Matching reads the state after the apply, and a design only sets the fields it applies, so whatever it
    /// leaves unset keeps the previous look's value. Wearing A (an outfit plus a hat) and applying B (the same
    /// outfit, no hat) leaves the hat on — and A, matching one more field, outranks B. Stripping what B leaves
    /// unset then never happens, because the design it would be judged by is A.
    /// <para/>
    /// The state from just before the apply separates the two. Another candidate qualifies when it matches, sets
    /// a strict subset of the active design's fields, and NONE of the fields only the active design sets changed
    /// during this apply. Had the active design been re-applied over a hand edit, one of those would have
    /// changed back; an unchanged one is only a leftover.
    /// <para/>
    /// That leaves one case the state cannot decide: re-applying the active design with nothing touched since
    /// looks exactly like applying the subset design. It resolves to the subset, because moving to a different
    /// design is the more common action — and applying the active design again then brings its fields back,
    /// which does register as a change.
    /// <para/>
    /// Returns the qualifying candidates at the highest specificity (ties go to recency); empty when none.
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
    /// The fields a design applies, as <c>Container/Name/Part</c> keys — the same fields, under the same rules,
    /// that <see cref="StateMatches"/> compares, so "sets a subset" means exactly "is judged on a subset".
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
    /// The candidates that match best, as a list because the caller breaks remaining ties on recency.
    /// Empty when none matched.
    /// <para/>
    /// TWO PASSES, and the order is the whole point:
    /// <list type="number">
    /// <item>STRICT — retire only the slots a design demonstrably captured FROM us, by carrier item id.
    /// That rescue cannot distort anything: a design naming our carrier ring is naming an item the player
    /// never chose, whoever else it is compared against.</item>
    /// <item>LOOSE — additionally retire every slot we are currently BORROWING, whatever the design names
    /// there. Necessary for a design saved before we took the slot (their own ring is simply not in the
    /// live state to compare against), but it drops a real criterion, so two designs differing only by
    /// that ring stop being distinguishable and the recency tie-break decides between them.</item>
    /// </list>
    /// Running loose only when strict finds NOTHING keeps that cost where it belongs. It matters because
    /// the loser of a bad tie-break is not just a wrong look: <see cref="Restore"/> writes enable /
    /// priority / options into the Penumbra collection and disables every unbound mod, so picking the
    /// wrong sibling design leaves edits behind that outlive the apply.
    /// <para/>
    /// It also contains the one ownership signal we cannot fully trust. The glasses flag is set by
    /// ADOPTION — the reconcile claims a pair of our set that it finds already worn — so a player who
    /// wears the invisible facewear as their own glamour reads as us borrowing the slot. On the strict
    /// pass that misreading cannot cost them anything, and by the time the loose pass runs there is
    /// nothing left to lose.
    /// </summary>
    internal static List<Guid> BestMatches(
        IReadOnlyList<(Guid Id, JObject Design)> candidates, JObject state, Carriers carriers)
    {
        var top = Pass(carriers.ItemsOnly);
        // Skip the second pass when it would repeat the first: no borrowed slots means the two are the
        // same comparison, and re-running it would clone every design again for an identical answer.
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

    // How many bindings a failed match explains itself against. The reason is only interesting for designs
    // that could plausibly have been the one being applied, and every binding the player owns is far too
    // many lines for something that also fires whenever they apply a design they never bound.
    private const int NoMatchReportCandidates = 4;

    /// <summary>
    /// Say, at Debug, why the likeliest candidates were rejected: the design that WAS active (the one whose
    /// colours just got dropped, and the only one whose failure is definitely a fault) followed by the most
    /// recently captured bindings. Reaches the same verdict as <see cref="MatchBinding"/> because it runs
    /// the same comparison, only asking <see cref="StateMatches"/> for the field that differed.
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
    private void TryBootRestore(JObject state, bool final)
    {
        // Only the DESIGN is touched, never the state: StateMatches reads a state slot only where the
        // design carries one (both its loops walk the design's properties), so retiring a slot from the
        // design retires it as a criterion outright — including the case where our carrier is still
        // equipped at load (a crash, or a Dispose removal that has not landed yet).
        var carriers = BootCarriers();

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
                design = StripCarriers(design, carriers);
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
    /// (a deleted one reads as null) and still match the character's live state. The design must already
    /// have had its carrier slots retired — see <see cref="StripCarriers"/>.</summary>
    /// <param name="onMismatch">Reports the first field that differed, for the boot log.</param>
    internal static bool BootIdStillApplies(JObject? design, JObject state,
                                            Action<string>? onMismatch = null)
    {
        if (design == null) return false;
        return StateMatches(design, state, out _, onMismatch);
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
    /// Compares the design against the player's OWN choices, so the caller must first retire whatever
    /// Proteus put on them — see <see cref="StripCarriers"/>, which the caller applies to the DESIGN.
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

    // Glamourer packs a bonus item as (type << 48) | row id, so the sheet row we resolve a carrier by
    // lives in the low 48 bits — e.g. Glasses row 1 reads as 562949953421313.
    private const ulong BonusIdRowMask = 0x0000_FFFF_FFFF_FFFF;

    /// <summary>The Glamourer equipment-slot names a carrier can ride, from the one place that decides
    /// them — <see cref="InvisibleRing.CarrierSlots"/>. Duplicating the list here is how the neutralizer
    /// and the stripper drifted apart before.</summary>
    private static readonly string[] CarrierSlotNames =
        InvisibleRing.CarrierSlots.Select(c => c.EqdpSlot).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// What Proteus currently has on the player, in the two forms design matching needs.
    /// </summary>
    /// <param name="GlassesRow">The Glasses SHEET ROW of the invisible facewear host (not the packed
    /// BonusId a design stores — <see cref="StripCarriers"/> masks before comparing).</param>
    /// <param name="AccessoryItems">Item ids of the invisible accessories that can host a shell. A LIST
    /// because the pieces share a model set but are separate items — the Emperor's New Ring, Bracelets
    /// and Necklace — so one id could not cover them all.</param>
    /// <param name="OwnedSlots">Glamourer slot names we are CURRENTLY borrowing. Empty where ownership is
    /// unknowable (the boot path's glasses), which falls back to id-matching alone.</param>
    /// <param name="GlassesSlotOwned">Whether the Glasses slot is one we are currently borrowing.</param>
    internal readonly record struct Carriers(
        ulong? GlassesRow,
        IReadOnlyList<ulong>? AccessoryItems,
        IReadOnlyList<string>? OwnedSlots = null,
        bool GlassesSlotOwned = false)
    {
        /// <summary>Does this retire whole SLOTS, over and above the carrier items themselves? When it
        /// does not, the strict and loose passes in <see cref="BestMatches"/> are the same comparison.</summary>
        internal bool RetiresSlots => GlassesSlotOwned || OwnedSlots is { Count: > 0 };

        /// <summary>This, with slot retirement dropped — the strict pass's view, where only a design that
        /// actually names one of our carrier items gives anything up.</summary>
        internal Carriers ItemsOnly => new(GlassesRow, AccessoryItems);
    }

    /// <summary>
    /// Retire the slots PROTEUS owns from a DESIGN, so it is never judged on a slot it did not really
    /// choose. Returns the input unchanged when there is nothing to retire.
    /// <para/>
    /// Two separate rescues, and a design needs whichever applies:
    /// <list type="bullet">
    /// <item>By ITEM — the design CAPTURED a carrier. Saving a Glamourer design while the shell is hosted
    /// bakes our injected facewear or ring into the look, so the design then demands an item the player
    /// never picked. Observed as <c>Bonus/Glasses: BonusId 562949953421313 != state 844424946909184</c>
    /// and as an <c>RFinger</c> holding carrier item 9295.</item>
    /// <item>By SLOT — the design named the player's OWN jewellery in a slot we have since borrowed. Their
    /// choice is gone from the live state while we hold the slot, so it cannot be compared to anything.</item>
    /// </list>
    /// <para/>
    /// DESIGNS ONLY — never pass a state. StateMatches walks the design's properties and looks the state up
    /// per design-carried slot, so dropping a slot from the design retires it as a criterion (what we want),
    /// while dropping one from the state turns a slot the design DOES carry into "state has no item".
    /// <para/>
    /// Retiring is deliberately the WHOLE mechanism. The state used to be doctored in parallel — our
    /// carrier's ItemId overwritten with 0, on the premise that "a design that saved an empty ring stores
    /// ItemId 0". It does not: Glamourer writes a per-slot sentinel (4294967155 and neighbours, and
    /// 844424946909184 for bare Glasses), never 0, so that pass could not make a single design match and
    /// merely hid the real fix. One mechanism, on one side.
    /// <para/>
    /// Retiring a whole SLOT drops a real criterion, so two designs differing only by that ring stop being
    /// distinguishable. That is why <see cref="BestMatches"/> asks for it only once the strict, item-only
    /// view has failed outright — see there for what a bad tie-break costs.
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

    /// <summary>What Proteus has on the player right now, for the live apply path. Straight from the
    /// compositor, which knows what it actually injected — deriving it from the feature toggles instead
    /// misses our item in the window between switching a feature off and the recomposite pulling it, and
    /// blanking by id whenever a feature is off would erase the player's OWN Emperor's New Ring (a common
    /// invisible-ring glamour) from every comparison.</summary>
    private Carriers LiveCarriers()
    {
        // Non-null only while WE have a pair on, so it doubles as the ownership flag — with the caveat
        // that "ours" there includes a pair the reconcile ADOPTED because it found our set already worn.
        // Someone wearing the invisible facewear as their own glamour therefore reads as us borrowing the
        // slot; BestMatches is what keeps that from costing them a match.
        var glasses = compositor.InjectedGlassesItemId;
        return new Carriers(glasses, compositor.InjectedCarrierItemIds,
            compositor.InjectedCarrierSlots, GlassesSlotOwned: glasses != null);
    }

    /// <summary>The carriers to retire at boot.</summary>
    // The accessory SLOTS are known (ownership is persisted in the config), but the item ids come from the
    // game sheets: a carrier can be left worn in a slot we did not record, and the glasses flag in
    // particular is in-memory only, so it is false at boot however things really stand. Harmless in this
    // direction — an id only retires a slot if that exact item is actually named there, so the worst case
    // is that a player who genuinely wears the carrier item stops having it count toward a match, which
    // loosens specificity rather than fabricating one.
    private Carriers BootCarriers()
        => new(InvisibleGlasses.Resolve(Plugin.DataManager, log)?.ItemId,
               InvisibleRing.CarrierSlots
                   .Select(c => InvisibleRing.ResolveFor(Plugin.DataManager, log, c.Slot)?.ItemId)
                   .Where(id => id != null).Select(id => id!.Value).Distinct().ToList(),
               compositor.InjectedCarrierSlots);

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
        var active = compositor.EffectiveGearOverrideFor(e.ModDirectory);

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
