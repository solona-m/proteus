using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>Local-player redraws: how many, and when the last one came. Thread-safe.</summary>
internal sealed class RedrawCounter
{
    private long count;
    private long lastTicks;

    public void Note()
    {
        Interlocked.Exchange(ref lastTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref count);
    }

    public long Count => Interlocked.Read(ref count);

    public DateTime? LastUtc => Interlocked.Read(ref lastTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;
}

/// <summary>
/// The harness's one door into the game: Dalamud, Penumbra, the compositor, auto refit and the Studio. Kept thin —
/// it reads and forwards; every decision lives in the tested core. UNVERIFIED in game until adversarial QA runs it.
/// </summary>
internal sealed class GameHarnessAdapter : IHarnessGame, IDisposable
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly AutoRefitWatcher autoRefit;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private readonly Configuration config;
    private readonly UVRemapService uvRemap;
    private readonly Func<string, byte[]?> readGameFile;
    private readonly Action<string> onModChanged;
    private readonly IPluginLog log;
    private readonly LiveModelDump models;

    private readonly RedrawCounter redraws = new();
    private long nextNameSample;

    /// <summary>Where the once-a-second character-name sample goes while the harness listens.</summary>
    public SessionNames? NameSink { get; set; }

    public GameHarnessAdapter(PenumbraBridge penumbra, CompositorService compositor, AutoRefitWatcher autoRefit,
                              IObjectTable objects, IClientState clientState, IFramework framework,
                              ITextureProvider textures, ITextureReadbackProvider readback, Configuration config,
                              UVRemapService uvRemap, Func<string, byte[]?> readGameFile, Action<string> onModChanged,
                              IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.autoRefit = autoRefit;
        this.objects = objects;
        this.clientState = clientState;
        this.framework = framework;
        this.textures = textures;
        this.readback = readback;
        this.config = config;
        this.uvRemap = uvRemap;
        this.readGameFile = readGameFile;
        this.onModChanged = onModChanged;
        this.log = log;
        models = new LiveModelDump(objects);
        penumbra.LocalPlayerRedrawn += redraws.Note;
        framework.Update += OnUpdate;
    }

    /// <summary>Once a second, no IPC: the local character's name, for the log scrub.</summary>
    private void OnUpdate(IFramework _)
    {
        if (NameSink is not { } sink) return;
        long now = Environment.TickCount64;
        if (now < nextNameSample) return;
        nextNameSample = now + 1000;
        try { sink.Add(objects.LocalPlayer?.Name.TextValue, NameKind.Character); }
        catch (Exception) { /* mid-zone: next second */ }
    }

    public long RedrawCount => redraws.Count;

    public DateTime? LastRedrawUtc => redraws.LastUtc;

    public string ManagedModDir => SidecarDiscoveryService.ManagedModDir;

    public HealthInfo Health() => new(penumbra.IsAvailable, clientState.IsLoggedIn, config.PluginEnabled);

    public RawState State()
    {
        CharacterInfo? character = null;
        var (loaded, _) = models.LoadedModels();
        var resourceMap = penumbra.GetActivePlayerResourceMap();
        if (objects.LocalPlayer is ICharacter c && c.Customize is { Length: > 4 } cz)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resourceMap != null) foreach (var gamePaths in resourceMap.Values) paths.UnionWith(gamePaths);
            foreach (var (_, file) in loaded) if (file != null) paths.Add(LiveCharacter.FilePath(file));
            string? code = DrawnModelPaths.DrawnRaceCodeFromModels(paths);
            character = new CharacterInfo(cz[0], cz[4], cz[1], cz[2], code == null ? null : "c" + code);
        }
        var view = autoRefit.Current;
        return new RawState(character, penumbra.GetPlayerCollectionId(), loaded, resourceMap, penumbra.GetModDirectory(),
                            compositor.Activity(), compositor.LastResult, compositor.LastRefresh,
                            new AutoRefitInfo(view, autoRefit.Activity()));
    }

    public (IReadOnlyCollection<string> Collections, string? Character) NamesNow()
        => ((IReadOnlyCollection<string>?)penumbra.GetCollections()?.Values.ToList() ?? [],
            objects.LocalPlayer?.Name.TextValue);

    public ModList? Mods()
        => penumbra.GetAllMods() is { } mods && penumbra.GetModDirectory() is { } root ? new ModList(mods, root) : null;

    public AutoRefitInfo AutoRefit() => new(autoRefit.Current, autoRefit.Activity());

    public bool IsLegacyFolder(string modRoot) => PenumbraModMeta.IsLegacyFolder(modRoot);

    public IReadOnlyList<(string Key, string Body)> OfferedKeys(string modRoot)
    {
        var (pieces, _, _) = SizeSetRefit.ReadPieces(modRoot, readGameFile, config.SizeSetEnds);
        return SizeSetRefit.Offered(pieces).Select(c => (c.Key, c.Body)).ToList();
    }

    public IReadOnlySet<string> InstalledKeys(ModList mods)
    {
        var bodies = autoRefit.Index.RefreshState(mods.Mods, mods.ModsRoot).List;
        return SupportedBodies.Choices.Select(c => c.Key).Where(k => SupportedBodies.Installed(k, bodies))
                              .ToHashSet(StringComparer.Ordinal);
    }

    public RefitOptions Options(bool remake) => new(config.SizeSetYas, config.SizeSetEnds, config.SizeSetPiercings, remake);

    public SizeSetLaunch.Prepared PrepareSizeSet(string modRoot, IReadOnlyList<string> keys, RefitOptions options, ModList mods)
        => SizeSetLaunch.Prepare(mods.Mods, mods.ModsRoot, penumbra.ResolvePlayerPaths, modRoot, keys, options.Yas,
                                 options.Remake, options.Ends, options.Piercings, null, readGameFile, log);

    public Task<SizeSetLaunch.Outcome> RunSizeSet(SizeSetLaunch.Prepared prepared, Action<string> progress, Action afterChange,
                                                  Action<Action> postToFramework, CancellationToken ct)
        => SizeSetLaunch.Start(prepared, autoRefit.Index, uvRemap, log, progress, afterChange, postToFramework, ct);

    public AfterChangeResult AfterModChange(string modDir)
    {
        compositor.ExpectOwnModEdit(modDir);
        bool reloaded = penumbra.ReloadModDirectory(modDir) == PenumbraApiEc.Success;
        long baseline = RedrawCount;
        compositor.RedrawForChangedModel("test harness");
        try { onModChanged(modDir); }
        catch (Exception e) { log.Warning(e, "[Proteus] test harness: the Studio could not refresh {0}", modDir); }
        return new AfterChangeResult(reloaded, true, baseline);
    }

    public uint ViewportId() => ImGui.GetMainViewport().ID;

    public async Task<RawFrame> CaptureRaw(uint viewportId, bool includeUi, CancellationToken ct)
    {
        var wrap = await textures.CreateFromImGuiViewportAsync(
            new ImGuiViewportTextureArgs { ViewportId = viewportId, TakeBeforeImGuiRender = !includeUi, KeepTransparency = false },
            "proteus-test-harness", ct).ConfigureAwait(false);
        // Disposed here, however the readback ends (a cancelled token included), rather than trusting the readback to.
        using (wrap)
        {
            var (spec, data) = await readback.GetRawImageAsync(wrap, new TextureModificationArgs { DxgiFormat = FramePng.B8G8R8A8Unorm },
                                                              leaveWrapOpen: true, ct).ConfigureAwait(false);
            return new RawFrame(spec.Width, spec.Height, spec.Pitch, spec.DxgiFormat, data);
        }
    }

    public void Dispose()
    {
        penumbra.LocalPlayerRedrawn -= redraws.Note;
        framework.Update -= OnUpdate;
        NameSink = null;
    }
}
