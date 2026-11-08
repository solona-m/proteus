using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>What <c>/health</c> reports.</summary>
internal sealed record HealthInfo(bool Penumbra, bool LoggedIn, bool PluginEnabled);

/// <summary>The live character, by numbers only: no names.</summary>
internal sealed record CharacterInfo(int Race, int Tribe, int Gender, int BodyType, string? DrawnRaceCode);

/// <summary>Auto refit's view and pending work.</summary>
internal sealed record AutoRefitInfo(AutoRefitWatcher.View View, AutoRefitWatcher.ActivitySnapshot Activity)
{
    public bool Busy => AutoRefitQuiet.Of(View, Activity);
}

/// <summary>Everything <c>/state</c> needs, as read on the framework thread; joined and shaped off it.</summary>
internal sealed record RawState(CharacterInfo? Character, Guid? CollectionId,
                                IReadOnlyList<(int Slot, string? File)> Loaded,
                                IReadOnlyDictionary<string, HashSet<string>>? ResourceMap, string? ModsRoot,
                                CompositorService.ActivitySnapshot Compositor, CompositorResult? LastResult,
                                CompositorService.RefreshRecord? LastRefresh, AutoRefitInfo AutoRefit)
{
    /// <summary>The ten gear slots as Glamourer reports them; null when it cannot be read.</summary>
    public IReadOnlyDictionary<string, Worn>? Gear { get; init; }
}

/// <summary>The automatic hair path right now, and the Hair settings: read-only.</summary>
/// <param name="CheckOwed">A check of the player's hair mods is owed but not started.</param>
internal sealed record HairPortInfo(HairPortWatcher.View View, HairPortWatcher.ScanView Scan, bool CheckOwed, bool AutoFit,
                                    bool Earless)
{
    /// <summary>Examining the worn hairstyle or running a check right now.</summary>
    public bool Working => View.Busy || Scan.Running;

    /// <summary>Working, or a check is owed: what refuses a hair port.</summary>
    public bool Busy => Working || CheckOwed;
}

/// <summary>Penumbra's mod list and mods folder.</summary>
internal sealed record ModList(IReadOnlyDictionary<string, string> Mods, string ModsRoot);

/// <summary>The size set's settings a harness refit runs with: the Studio's current ones.</summary>
internal sealed record RefitOptions(bool Yas, bool Ends, bool Piercings, bool Remake, bool KeepHiddenSkin = false);

/// <summary>What an after-change did.</summary>
/// <param name="Reloaded">Penumbra answered Success to the reload.</param>
/// <param name="RedrawRequested">A redraw was asked for (it is fire-and-forget: see <see cref="IHarnessGame.RedrawCount"/>).</param>
/// <param name="Baseline">The redraw counter just before the redraw was asked for.</param>
internal sealed record AfterChangeResult(bool Reloaded, bool RedrawRequested, long Baseline);

/// <summary>
/// The game, Penumbra, the compositor, auto refit and the Studio, as the harness sees them. The one implementation
/// that touches them is <c>GameHarnessAdapter</c>; everything else in the harness runs against this, so it is tested
/// headlessly. Members say which thread they belong on.
/// </summary>
internal interface IHarnessGame
{
    /// <summary>Framework thread.</summary>
    HealthInfo Health();

    /// <summary>Framework thread.</summary>
    RawState State();

    /// <summary>Every collection name and the local character's name, to scrub from logs. Framework thread.</summary>
    (IReadOnlyCollection<string> Collections, string? Character) NamesNow();

    /// <summary>Penumbra's mods and folder; null when Penumbra is unavailable. Framework thread.</summary>
    ModList? Mods();

    /// <summary>Where names seen between requests go (the local character, sampled once a second); null while off.</summary>
    SessionNames? NameSink { get; set; }

    /// <summary>Proteus's own output mod's folder name.</summary>
    string ManagedModDir { get; }

    /// <summary>Auto refit right now. Any thread.</summary>
    AutoRefitInfo AutoRefit();

    /// <summary>A pre-v4 mod the size set must not write. Worker thread.</summary>
    bool IsLegacyFolder(string modRoot);

    /// <summary>The body keys the mod's pieces are offered (key, body). Worker thread: reads its manifest.</summary>
    IReadOnlyList<(string Key, string Body)> OfferedKeys(string modRoot);

    /// <summary>The body keys whose body mod is installed. Worker thread: refreshes the body index.</summary>
    IReadOnlySet<string> InstalledKeys(ModList mods);

    /// <summary>The size set's settings now (the Studio's).</summary>
    RefitOptions Options(bool remake);

    /// <summary>The run's request. Framework thread: reads the game's bodies, asks Penumbra for skeletons.</summary>
    SizeSetLaunch.Prepared PrepareSizeSet(string modRoot, IReadOnlyList<string> keys, RefitOptions options, ModList mods);

    /// <summary>Start the run. It posts <paramref name="afterChange"/> through <paramref name="postToFramework"/> as it
    /// ends, however it ends.</summary>
    Task<SizeSetLaunch.Outcome> RunSizeSet(SizeSetLaunch.Prepared prepared, Action<string> progress, Action afterChange,
                                           Action<Action> postToFramework, CancellationToken ct);

    /// <summary>Reload the mod, ask for a redraw, refresh an open Studio. Framework thread.</summary>
    AfterChangeResult AfterModChange(string modDir);

    /// <summary>Local-player redraws seen so far. Any thread.</summary>
    long RedrawCount { get; }

    /// <summary>When the last one was seen, or null. Any thread.</summary>
    DateTime? LastRedrawUtc { get; }

    /// <summary>The main viewport's id. Framework thread.</summary>
    uint ViewportId();

    /// <summary>Capture the screen and read it back as B8G8R8A8. Off the framework thread; honours the token.</summary>
    Task<RawFrame> CaptureRaw(uint viewportId, bool includeUi, CancellationToken ct);

    /// <summary>The gear shader upgrade right now. Any thread.</summary>
    ShaderUpgradeWatcher.View ShaderUpgrade();

    /// <summary>
    /// Switch the gear shader upgrade as its checkbox does, and/or choose this session's mask strategy (one of
    /// "inverse-specular", "low", "mid", already validated). Returns the state after. Framework thread.
    /// </summary>
    (bool Enabled, string MaskGloss) SetShaderUpgrade(bool? enabled, string? maskGloss);

    /// <summary>
    /// Start "Undo for what I'm wearing" (<paramref name="undoWorn"/>, declining unless <paramref name="decline"/> is
    /// false) or "Upgrade what I'm wearing now". False when a run is already going. Framework thread.
    /// </summary>
    bool StartShaderUpgradeRun(bool undoWorn, bool decline);

    /// <summary>"Gear variants" right now. Any thread.</summary>
    VariantRepointWatcher.View VariantRepoint();

    /// <summary>Switch "Gear variants" as its checkbox does. Returns the state after. Framework thread.</summary>
    bool SetVariantRepoint(bool enabled);

    /// <summary>Whether the shader upgrade or auto refit is working, so a "Gear variants" run would collide. Framework thread.</summary>
    bool VariantRepointBlocked();

    /// <summary>
    /// Start "Undo for what I'm wearing" (<paramref name="undoWorn"/>, declining unless <paramref name="decline"/> is
    /// false) or "Repoint what I'm wearing now". False when a run is already going. Framework thread.
    /// </summary>
    bool StartVariantRepointRun(bool undoWorn, bool decline);

    /// <summary>The automatic hair path and the Hair settings. Any thread.</summary>
    HairPortInfo HairPort();

    /// <summary>A file of the game's own data (the reader the Studio's hair panel uses); null for none. Worker thread.</summary>
    byte[]? ReadGameFile(string gamePath);

    /// <summary>Whether a mod holds hair (<see cref="HairPortJob.IsHairMod"/>). Reads files: never the framework thread.</summary>
    bool IsHairMod(string modRoot);

    // ── equip and hold (HarnessChanges) ─────────────────────────────────────

    /// <summary>The ten gear slots worn now, from Glamourer's state; null when it cannot be read. Framework thread.</summary>
    IReadOnlyDictionary<string, Worn>? GearNow();

    /// <summary>Whether there is a local player to equip. Framework thread.</summary>
    bool PlayerPresent();

    /// <summary>
    /// Equip an item or custom id in a gear slot through Glamourer (Once, key 0, no lock). Null on Success; else
    /// "unavailable" or Glamourer's answer by name. Framework thread.
    /// </summary>
    string? SetGear(string slot, ulong id, byte dye1, byte dye2);

    /// <summary>The Glamourer slot names Proteus's invisible carriers occupy. Framework thread.</summary>
    IReadOnlyCollection<string> CarrierSlots();

    /// <summary>The player's effective collection, or null. Framework thread.</summary>
    Guid? PlayerCollection();

    /// <summary>Penumbra's temporary setting on a mod; null when it could not answer. Framework thread.</summary>
    PenumbraBridge.TemporaryHold? QueryHold(Guid collection, string modDir);

    /// <summary>The mod's effective settings, telling a failed read from none. Framework thread.</summary>
    PenumbraBridge.SettingsRead ReadSettings(Guid collection, string modDir);

    /// <summary>Hold a mod with a temporary setting. Null on Success, else Penumbra's answer by name. Framework thread.</summary>
    string? SetHold(Guid collection, string modDir, bool enabled, int priority,
                    IReadOnlyDictionary<string, List<string>> options, string source, int key);

    /// <summary>Remove a mod's temporary setting. Null on Success, else Penumbra's answer by name. Framework thread.</summary>
    string? ClearHold(Guid collection, string modDir);

    /// <summary>Remove every temporary setting locked with <paramref name="key"/> in a collection: Penumbra's answer by name. Framework thread.</summary>
    string SweepHolds(Guid collection, int key);

    /// <summary>Ask for a redraw of the local player. Framework thread.</summary>
    void RequestRedraw();

    // ── the Studio (HarnessChanges) ─────────────────────────────────────────

    /// <summary>The Studio panel as the harness drives it; null when there is none. Its members: framework thread.</summary>
    IStudioHarness? Studio { get; }

    // ── design binding (HarnessBindings) ────────────────────────────────────

    /// <summary>Glamourer's designs (id → name); null when Glamourer cannot answer. Framework thread.</summary>
    IReadOnlyDictionary<Guid, string>? Designs();

    /// <summary>A design as Glamourer stores it; null when it cannot be read. Framework thread.</summary>
    JObject? DesignJson(Guid id);

    /// <summary>The local player's whole Glamourer state; null when it cannot be read. Framework thread.</summary>
    JObject? GlamourerState();

    /// <summary>Apply a design to the local player as a user or plugin does (DesignsApi.ApplyDesign, Once, key 0), raising
    /// Glamourer's DesignApplied. Null on Success, else Glamourer's answer by name. Framework thread.</summary>
    string? ApplyDesign(Guid id);

    /// <summary>Whether a DesignApplied finalization for the local player has been seen so far (a counter). Any thread.</summary>
    long DesignAppliedCount { get; }

    /// <summary>
    /// Put a Glamourer state back (ApplyState, Once, key 0) with Proteus's apply signals suppressed, leaving the named
    /// gear slots (and the glasses when <paramref name="skipGlasses"/>) alone. Null on Success. Framework thread.
    /// </summary>
    string? RestoreGlamourerState(JObject state, IReadOnlyCollection<string> skipSlots, bool skipGlasses);

    /// <summary>What Proteus has on the player right now, as design matching sees it. Framework thread.</summary>
    DesignBindingService.Carriers CarriersNow();

    /// <summary>Milliseconds left in design binding's echo window. Framework thread.</summary>
    long EchoMsLeft();

    /// <summary>Stop a restore's deferred work (post-load sweep, temporary guard, queued unequip). Framework thread.</summary>
    void QuiesceBinding();

    /// <summary>Design binding as <c>GET /bindings</c> shows it (names unscrubbed). Framework thread.</summary>
    HarnessBindingView BindingView(string? mod, string? group, string? option);

    /// <summary>A design's applied-field keys, or null. Framework thread.</summary>
    IReadOnlyList<string>? DesignFields(Guid id);

    /// <summary>Whether a design has a binding. Framework thread.</summary>
    bool IsBound(Guid id);

    /// <summary>Whether any per-mod preset is pinned. Framework thread.</summary>
    bool PresetsPinned();

    /// <summary>Whether design binding may act, a design is active, it governs the mod, and a preset drives it. Framework thread.</summary>
    BindingEditState EditState(string modDir);

    /// <summary>The live override rows a mod/option shows (top level when group/option are null). Framework thread.</summary>
    List<ColorTableRowPreset>? LiveRows(string modDir, string? group, string? option);

    /// <summary>Install live override rows through the colour editor's path and recomposite. Framework thread.</summary>
    bool SetLiveRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows);

    /// <summary>Clear one option's live override through Reset's path (live only) and recomposite. Framework thread.</summary>
    bool ClearLiveOption(string modDir, string? group, string? option);

    /// <summary>The live binding state, for put-back. Framework thread.</summary>
    HarnessLiveSnapshot LiveSnapshot();

    /// <summary>Put the live binding state back. Framework thread.</summary>
    void RestoreLive(HarnessLiveSnapshot snapshot);

    /// <summary>The Proteus (sidecar) mods' folders. Framework thread.</summary>
    IReadOnlySet<string> ProteusDirs();

    /// <summary>Every configured mod's own settings in a collection (temporary settings ignored), keyed by folder; a mod
    /// that only inherits is <see cref="PermanentSetting.Own"/> false, one never configured is absent. Null when Penumbra
    /// cannot answer. Framework thread.</summary>
    IReadOnlyDictionary<string, PermanentSetting>? PermanentSettings(Guid collection);

    /// <summary>Write a mod's own settings back: enabled, priority and options, or back to inheriting when
    /// <see cref="PermanentSetting.Own"/> is false. Null on Success, else Penumbra's answer by name. Framework thread.</summary>
    string? WritePermanent(Guid collection, string modDir, PermanentSetting setting);

    /// <summary>Whether the compositor is idle. Framework thread.</summary>
    bool CompositorIdle();

    /// <summary>The automatic writers switched on (auto refit, gear shader upgrade, Gear variants, auto hair port, auto hat
    /// compat), by name: they write into worn mods as gear changes, which put-back cannot undo. Framework thread.</summary>
    IReadOnlyList<string> AutoWritersOn();

    // ── idle disk I/O checks (HarnessRouter.Qa) ─────────────────────────────

    /// <summary>The boot probe's latch, ladder step and time to its next look. Any thread.</summary>
    (bool Latched, int Step, long NextDueInMs) Boot();

    /// <summary>Re-arm the boot probe as the character going away does. Any thread.</summary>
    void RearmBoot();

    /// <summary>Every collection id and whether it is the player's: ids only, never names. Null when Penumbra cannot answer.
    /// Framework thread.</summary>
    IReadOnlyList<(Guid Id, bool Player)>? Collections();

    /// <summary>Auto refit's switch: the user's setting, the harness's session override (null: none), and the effect. Any thread.</summary>
    (bool Config, bool? Override, bool Effective) AutoRefitEnabled();

    /// <summary>Set (or with null, clear) the harness's session-only auto-refit override: memory only, never saved. Any thread.</summary>
    void SetAutoRefitOverride(bool? enabled);

    /// <summary>The mods auto refit wrote into while the override stood; the list is emptied. Any thread.</summary>
    IReadOnlyList<string> TakeRefitsWhileOverridden();

    /// <summary>The body index: built, stale, being read, generation. Any thread.</summary>
    (bool Built, bool Stale, bool Refreshing, long Generation) BodyIndex();

    /// <summary>The idle I/O counters' current window, copied without resetting; null when there are none. Any thread.</summary>
    (long WindowAgeMs, IReadOnlyDictionary<string, long> Counts)? Io();

    /// <summary>A Proteus mod as discovery sees it (through its cache), with a freshly parsed metadata graph; null when it
    /// has no sidecar. Framework thread.</summary>
    OverlayEntry? DiscoverEntry(string modDir);

    /// <summary>Save an entry's metadata through Proteus's own save. File work only, no framework hop: worker thread, under
    /// <see cref="HarnessSidecar"/>'s lock.</summary>
    bool SaveSidecar(OverlayEntry entry);

    /// <summary>Whether the colour editor holds an unsaved edit of the mod. Any thread.</summary>
    bool HasStaged(string modDir);

    /// <summary>Forget what discovery's cache holds about the mod. Any thread.</summary>
    void ForgetSidecar(string modDir);

    /// <summary>Ask for a recomposite. Framework thread.</summary>
    void Recomposite(string reason);

    // ── shell output failures (HarnessRouter.Output, issue sweep D) ─────────

    /// <summary>The folder of Proteus's own output mod (textures/, materials/, models/). Any thread.</summary>
    string ManagedOutputDir();

    /// <summary>Arm the shell load fault; false when one is armed already. Any thread.</summary>
    bool ArmShellFault(string shell, string slot, string until);

    /// <summary>Clear the shell load fault; true when one was there. The recomposite is the caller's. Any thread.</summary>
    bool ClearShellFault();

    /// <summary>Auto redraw: the user's setting, the harness's session override (null: none), and the effect. Any thread.</summary>
    (bool Config, bool? Override, bool Effective) AutoRedrawEnabled();

    /// <summary>Set (or with null, clear) the harness's session-only auto-redraw override: memory only. Any thread.</summary>
    void SetAutoRedrawOverride(bool? enabled);

    /// <summary>The write-retry latch, the drawn check's steps, the shell fault and the move fault, for /state. Any thread.</summary>
    object RecoveryState();
}

/// <summary>A mod's own (permanent) settings in a collection. <see cref="Own"/> false: the collection has no setting of its
/// own for it (it inherits, with the inherited values here; or was never configured: <see cref="Inherits"/>), and putting
/// it back means letting it inherit again.</summary>
internal sealed record PermanentSetting(bool Enabled, int Priority, IReadOnlyDictionary<string, List<string>> Options, bool Own)
{
    public PermanentSetting(bool enabled, int priority) : this(enabled, priority, new Dictionary<string, List<string>>(), true) { }

    /// <summary>No setting of its own: inherited, or never configured.</summary>
    public static readonly PermanentSetting Inherits = new(false, 0, new Dictionary<string, List<string>>(), false);

    public bool Equals(PermanentSetting? other)
        => other is not null && Enabled == other.Enabled && Priority == other.Priority && Own == other.Own
        && Options.Count == other.Options.Count
        && Options.All(kv => other.Options.TryGetValue(kv.Key, out var o) && o.SequenceEqual(kv.Value, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Enabled, Priority, Own, Options.Count);
}

/// <summary>Whether a binding edit may go ahead on a mod.</summary>
internal sealed record BindingEditState(bool FeatureOn, bool HasActive, bool Governed, bool PresetDriven);
