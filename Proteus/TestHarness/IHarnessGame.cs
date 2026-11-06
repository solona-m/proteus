using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
internal sealed record RefitOptions(bool Yas, bool Ends, bool Piercings, bool Remake);

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
}
