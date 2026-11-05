using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
                                CompositorService.RefreshRecord? LastRefresh, AutoRefitInfo AutoRefit);

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

    /// <summary>The automatic hair path and the Hair settings. Any thread.</summary>
    HairPortInfo HairPort();

    /// <summary>A file of the game's own data (the reader the Studio's hair panel uses); null for none. Worker thread.</summary>
    byte[]? ReadGameFile(string gamePath);

    /// <summary>Whether a mod holds hair (<see cref="HairPortJob.IsHairMod"/>). Reads files: never the framework thread.</summary>
    bool IsHairMod(string modRoot);
}
