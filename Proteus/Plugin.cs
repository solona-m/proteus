using System;
using System.Linq;
using CheapLoc;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Proteus.Gui;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;

    /// <summary>Hand-maintained; bump it for in-game testing. <see cref="BuildStamp"/> is the one that can't go stale.</summary>
    public const int BuildNumber = 924;

    /// <summary>
    /// When this assembly was compiled, as MM-dd HH:mm:ss, baked in by the csproj: Dalamud loads plugins from a stream,
    /// so there is no DLL path to stat. The value to trust for "did my rebuild load?".
    /// </summary>
    public static string BuildStamp { get; } =
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildStamp")?.Value ?? "?";

    private const string CommandName = "/proteus";

    private readonly Configuration config;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly TextureLoader textureLoader;
    private readonly SidecarDiscoveryService discovery;
    private readonly UVRemapService uvRemap;
    private readonly UVMapDownloadService uvMapDl;
    private readonly DefaultEffectsDownloadService effectsDl;
    private readonly CompositorService compositor;
    private volatile bool _disposed;
    private readonly DesignBindingService designBindings;
    private readonly GlamourerDesignWatcher designWatcher;
    private readonly PresetService presets;
    private readonly OverlayEditRouter editRouter;
    private readonly WindowSystem windowSystem;
    private readonly StatusWindow statusWindow;
    private readonly IpcProvider ipcProvider;
    private readonly SphereMapPreview spherePreview;
    private readonly TilePreview tilePreview;
    private readonly Gui.PartViewport partViewport;
    private readonly Gui.PartsPanel partsPanel;
    private readonly HatCompatWatcher hatCompat;
    private readonly Gui.ProteusFonts fonts;
    private readonly Localization.LocSetup loc;
    private readonly ColorTableHighlighter highlighter;
    private readonly SkinDiffuseGlow skinGlow;
    private readonly ShellNormalGhost shellGhost;
    private readonly ShellColorsetApplier shellColorset;
    private readonly SceneLightService sceneLight;
    private readonly ShellCoverageFade shellCoverageFade;
    private readonly Gui.LiveMeshOverlay liveMesh;
    private readonly Gui.LiveBrush liveBrush;
    private readonly UsageStats usageStats;
    private readonly Gui.UsageConsentWindow consentWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework)
    {
        // FIRST, before anything that can put a string on screen or in chat: an early call silently renders English.
        loc = new Localization.LocSetup(pluginInterface);

        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        penumbra = new PenumbraBridge(pluginInterface, log);
        glamourer = new GlamourerBridge(pluginInterface, ObjectTable, log);
        textureLoader = new TextureLoader(DataManager, log)
        {
            DecodeCacheBudgetBytes = Math.Max(256, config.DecodeCacheBudgetMb) * 1024L * 1024,
        };
        // Data lives in ConfigDirectory, which survives updates (the assembly folder is per version); the assembly
        // directory is passed only so an older install's copies can be reclaimed.
        var dataDir     = pluginInterface.ConfigDirectory.FullName;
        var assemblyDir = pluginInterface.AssemblyLocation.DirectoryName;

        // Opt-in usage counts. Early, so every service below can count through UsageStats.Current; inert
        // (no file, no request) unless the user has said yes.
        usageStats = new UsageStats(config, config.Save, dataDir, log, () => loc.Current,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0", BuildNumber);
        usageStats.Start();

        effectsDl = new DefaultEffectsDownloadService(log, dataDir, assemblyDir);
        discovery = new SidecarDiscoveryService(penumbra, log)
        {
            DefaultEffectsDir = effectsDl.EffectsDir,
            AssemblyDir       = assemblyDir,
        };
        // Seed the global effects library with the starter set. No-ops until Penumbra's mod directory resolves;
        // OnPenumbraReady and each finished starter download call it again.
        discovery.SeedDefaultEffects();
        effectsDl.EnsureAsync(onProgress: () => { if (!_disposed) discovery.SeedDefaultEffects(); });
        uvRemap = new UVRemapService(log, dataDir);
        uvMapDl = new UVMapDownloadService(log, dataDir, assemblyDir);
        compositor = new CompositorService(penumbra, glamourer, discovery, textureLoader, config, log, uvRemap);

        if (!uvMapDl.MapsPresent())
        {
            uvMapDl.EnsureMapsAsync(onComplete: () =>
            {
                if (!_disposed && config.PluginEnabled && penumbra.IsAvailable)
                    compositor.TriggerRecomposite("uvmaps-downloaded");
            });
        }
        designBindings = new DesignBindingService(penumbra, glamourer, discovery, compositor, config, pluginInterface, framework, log);
        // Told to the bridge too: GetDesign's on-disk fallback must read the same folder as the watcher.
        glamourer.DesignsDirectoryOverride = config.GlamourerDesignDirOverride;
        designWatcher = new GlamourerDesignWatcher(designBindings, glamourer.EffectiveDesignsDirectory, log);

        // After the bindings, which presets capture through (CaptureMod); the reverse link is an event to avoid a construction cycle.
        presets = new PresetService(penumbra, discovery, compositor, designBindings, pluginInterface, log);
        designBindings.PresetsSuperseded += presets.ClearAllApplied;
        editRouter = new OverlayEditRouter(presets, designBindings);

        ipcProvider = new IpcProvider(pluginInterface, compositor, discovery, log);

        // Sphere-map thumbnails for the colour table editor.
        spherePreview = new SphereMapPreview(TextureProvider, log);
        Gui.ColorTableEditor.Spheres = spherePreview;
        tilePreview = new TilePreview(TextureProvider, log);
        Gui.ColorTableEditor.Tiles = tilePreview;

        // Display typography (the game's Jupiter); the atlas builds asynchronously and falls back to the default font.
        fonts = new Gui.ProteusFonts(pluginInterface);
        Gui.ProteusStyle.Fonts = fonts;

        // Glow-effect thumbnails (loaded per-file, cached by Dalamud's texture provider).
        Gui.ColorTableEditor.EffectThumbs = new Gui.EffectPreview(TextureProvider);

        // Ghosts the gear shells stacked above a highlighted layer so an occluded colorset Glow shows through; driven by both highlighters below.
        shellGhost = new ShellNormalGhost(Framework, ObjectTable, textureLoader, Log);

        // Live colorset "glow / target" highlighter (framework-thread material editing).
        highlighter = new ColorTableHighlighter(Framework, ObjectTable) { Ghost = shellGhost };
        Gui.ColorTableEditor.Highlighter = highlighter;
        compositor.Highlighter = highlighter;   // so a recomposite (shell may rebuild) clears a stale glow

        // Live skin (body diffuse) glow via render-material texture rebind.
        skinGlow = new SkinDiffuseGlow(Framework, ObjectTable, textureLoader, Log) { Ghost = shellGhost };
        Gui.ColorTableEditor.SkinGlow = skinGlow;

        // How lit the wearer actually is, for light-sensitive glow: the zone's placed lights plus a sky term outdoors.
        sceneLight = new SceneLightService(Framework, ObjectTable, config, Log);
        Gui.StatusWindow.SceneLight = sceneLight;

        // Re-asserts each shell's colorset onto the live material after the game rebuilds it, and scales a light-sensitive
        // row's emissive. Takes the highlighter so it leaves a glow-highlighted slot alone.
        shellColorset = new ShellColorsetApplier(Framework, ObjectTable, highlighter, sceneLight, config)
        {
            // Read through the compositor rather than a snapshot, which would go stale on the next composite.
            LightFor = compositor.GetShellLight,
        };

        // A dark-only glow's hiding row fades its coverage (the shell normal's blue) with its glow.
        shellCoverageFade = new ShellCoverageFade(Framework, ObjectTable, sceneLight, textureLoader, config, Log)
        {
            LightFor = compositor.GetShellLight,
            // Skips the whole per-frame character walk when nothing asks for a fade.
            AnyLight = () => compositor.AnyShellLight,
            // Both swap the shell normal's Texture** slot, so the fade stands aside while the ghost holds one.
            Ghost = shellGhost,
        };

        var modCreation = new ModCreationService(penumbra, compositor, config, textureLoader, log);
        // Body catalogue for imports: probes the game data for the human bodies that exist.
        var bodyCatalog = new BodyMaterialCatalog(DataManager.FileExists);
        var onionImport = new OnionImportService(
            penumbra, compositor, modCreation, textureLoader, bodyCatalog, config, log);
        var contentImport = new ContentImportService(penumbra, compositor, log);
        // Atramentum Luminis .ttmp2 glow-tattoo packs.
        var luminisImport = new LuminisImportService(
            penumbra, compositor, modCreation, textureLoader, bodyCatalog, log);
        // Emissive-skin .pmp packs; shares the body catalogue.
        var emissiveImport = new EmissiveSkinImportService(
            penumbra, compositor, modCreation, textureLoader, bodyCatalog, log);
        // Loose eye-texture zips. Its own catalogue: faces are not shared between races the way bodies are.
        var irisCatalog = new IrisMaterialCatalog(DataManager.FileExists);
        var eyeImport = new EyeImportService(penumbra, compositor, textureLoader, irisCatalog, log);
        var modExport = new ModExportService(penumbra, log);

        // The clickable model view, and the panel that turns a mod's geometry into on/off switches.
        partViewport = new Gui.PartViewport(TextureProvider, log);
        liveBrush = new Gui.LiveBrush(ObjectTable, DataManager, penumbra, log);
        partsPanel = new Gui.PartsPanel(penumbra, compositor, partViewport, liveBrush, textureLoader, log);

        // A service, not part of the panel: it subscribes to the hairstyle change so it works with the window shut.
        hatCompat = new HatCompatWatcher(compositor, penumbra, glamourer, config, log);

        statusWindow = new StatusWindow(compositor, discovery, penumbra, config, designBindings,
            presets, editRouter, uvMapDl, uvRemap,
            modCreation, onionImport, contentImport, luminisImport, emissiveImport, eyeImport, modExport,
            textureLoader, partsPanel, hatCompat);

        liveMesh = new Gui.LiveMeshOverlay(ObjectTable, DataManager, penumbra, ChatGui, log);

        windowSystem = new WindowSystem("Proteus");
        windowSystem.AddWindow(statusWindow);
        consentWindow = new Gui.UsageConsentWindow(usageStats);
        windowSystem.AddWindow(consentWindow);

        pluginInterface.UiBuilder.DisableGposeUiHide = true;
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            // Read once, at registration: Dalamud caches the help line, so it keeps the load-time language.
            HelpMessage = Loc.Localize("Command.Help",
                "Toggle the Proteus overlay compositor status window. \"/proteus config\" opens it on Settings."),
        });

        // The boot composite is held (see BootCompositeHold) for the design-binding restore; with nothing armed, release it now.
        if (!designBindings.BootRestoreArmed)
            compositor.BootCompositeHold = false;

        // Recomposite on startup (plugin reload) only if Penumbra's mod list is readable and nothing holds the boot
        // composite; an early discovery returns empty and would wipe the output. OnPenumbraReady covers the normal boot.
        if (config.PluginEnabled && penumbra.IsAvailable && !compositor.BootCompositeHold
            && discovery.DiscoverEnabled().Count > 0)
            compositor.TriggerRecomposite("startup");

        log.Information("Proteus loaded. Penumbra={0} [build: equipped-model second-skin]", penumbra.IsAvailable);
        ChatGui.Print($"[Proteus] loaded — build #{BuildNumber} ({BuildStamp})");

        SuggestMirrorRepo(pluginInterface);
    }

    /// <summary>How many times <see cref="SuggestMirrorRepo"/> may speak before it gives up.</summary>
    private const int MirrorNoticeLimit = 3;

    /// <summary>The plugin repo URL this notice asks people to move away from.</summary>
    private const string LegacyRepoHost = "raw.githubusercontent.com/solona-m/plugins";

    /// <summary>
    /// Nudges anyone still installed from the raw.githubusercontent.com manifest towards the mirrored one, at most
    /// <see cref="MirrorNoticeLimit"/> times ever. Matched positively against the legacy host, so anything else stays silent.
    /// </summary>
    private void SuggestMirrorRepo(IDalamudPluginInterface pluginInterface)
    {
        var source = pluginInterface.SourceRepository;

        // Logged unconditionally, before the early-outs: a dev-loaded plugin can never trigger the notice.
        Log.Information("[Proteus] SourceRepository={0} (mirror notice shown {1}/{2})",
                        string.IsNullOrEmpty(source) ? "<none, dev install>" : source,
                        config.MirrorNoticeShown, MirrorNoticeLimit);

        if (config.MirrorNoticeShown >= MirrorNoticeLimit) return;
        if (string.IsNullOrEmpty(source) ||
            !source.Contains(LegacyRepoHost, StringComparison.OrdinalIgnoreCase))
            return;

        config.MirrorNoticeShown++;
        config.Save();

        // No count of remaining reminders: plural agreement across machine-translated locales is not worth it.
        ChatGui.Print(new Dalamud.Game.Text.SeStringHandling.SeStringBuilder()
            .AddUiForeground(
                Loc.Localize("Chat.MirrorRepo",
                    "[Proteus] You installed from the old plugin repo URL. Switching it to " +
                    "https://dl.solona.info/repo.json under /xlplugins > Experimental makes updates " +
                    "faster and takes load off GitHub. Your install keeps working either way. " +
                    "This reminder shows a few times, then stops."), 45)
            .Build());
    }

    private void DrawUi()
    {
        // Before the window system, and unconditionally: Dalamud stops calling a CLOSED window's Draw, which would strand an import.
        statusWindow.TickImport();
        liveMesh.Draw();
        // Before the windows: the Studio tab reads this frame's stroke from it.
        liveBrush.Update();
        // The usage-stats question, asked once, and only once the user has opened Proteus themselves.
        if (statusWindow.IsOpen && !consentWindow.IsOpen && usageStats.NeedsConsentPrompt)
            consentWindow.IsOpen = true;
        windowSystem.Draw();
    }

    private void OpenMainUi() => statusWindow.Show();

    // The gear icon in the plugin installer — settings live in the status window's Settings tab.
    private void OpenConfigUi() => statusWindow.OpenToSettings();

    private void OnCommand(string command, string args)
    {
        // "/proteus models [filter]": what the RENDERER loaded, not what we wrote.
        var a = args.Trim();

        // "/proteus livemesh [filter|off|nodeform|deform]" — draw a worn model posed on the CPU over the character.
        if (a.StartsWith("livemesh", StringComparison.OrdinalIgnoreCase))
        {
            ChatGui.Print($"[Proteus] {liveMesh.Command(a[8..])}");
            return;
        }

        // "/proteus stats [send|sendtoday]" — what the usage tally holds, and a way to send it without waiting a day.
        if (a.StartsWith("stats", StringComparison.OrdinalIgnoreCase))
        {
            var sub = a[5..].Trim().ToLowerInvariant();
            if (sub is "send" or "sendtoday")
            {
                usageStats.SendNowAsync(includeToday: sub == "sendtoday").ContinueWith(t =>
                    Framework.RunOnFrameworkThread(() => ChatGui.Print(t.IsCompletedSuccessfully
                        ? $"[Proteus] usage stats: sent {t.Result} day(s); {usageStats.Describe()}"
                        : "[Proteus] usage stats: send failed")));
                return;
            }
            ChatGui.Print($"[Proteus] usage stats: {usageStats.Describe()}");
            return;
        }

        if (a.StartsWith("models", StringComparison.OrdinalIgnoreCase))
        {
            var filter = a.Length > 6 ? a[6..].Trim() : "";
            var dump = new LiveModelDump(ObjectTable);
            var lines = filter.Length > 0
                ? dump.DumpMatching(filter, System.IO.Path.Combine(
                      System.IO.Path.GetTempPath(), "proteus-live-models"))
                : dump.DescribeLocalPlayer();
            foreach (var l in lines)
            {
                Log.Information("[Proteus] {0}", l);
                ChatGui.Print($"[Proteus] {l}");
            }
            return;
        }

        // "/proteus config" opens rather than toggles.
        switch (a)
        {
            case "config":
            case "settings":
                statusWindow.OpenToSettings();
                break;
            default:
                if (statusWindow.IsOpen)
                    statusWindow.IsOpen = false;
                else
                    statusWindow.Show();
                break;
        }
    }

    public void Dispose()
    {
        _disposed = true;

        // Last chance for an import whose disk work landed after the final frame, registration only: no window, no recomposite.
        try { statusWindow.TickImport(unloading: true); } catch { /* tearing down — never block the unload */ }

        // A window resize still inside the save debounce.
        try { statusWindow.FlushPendingSize(); } catch { /* tearing down — never block the unload */ }

        // A brush stroke still inside its autosave debounce: written, without the reload and redraw.
        try { statusWindow.FlushPendingBrush(); } catch { /* tearing down — never block the unload */ }

        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        windowSystem.RemoveAllWindows();
        Gui.ColorTableEditor.Spheres = null;
        Gui.ColorTableEditor.Tiles = null;
        Gui.ColorTableEditor.EffectThumbs = null;
        Gui.ColorTableEditor.Highlighter = null;
        Gui.ColorTableEditor.SkinGlow = null;
        Gui.StatusWindow.SceneLight = null;
        Gui.ProteusStyle.Fonts = null;   // before the dispose below, matching the null-then-dispose order above
        skinGlow.Dispose();
        shellCoverageFade.Dispose();   // restores the shell normals it faded
        shellColorset.Dispose();   // before the highlighter and the light probe it references
        sceneLight.Dispose();
        highlighter.Dispose();
        shellGhost.Dispose();   // after the highlighters (they may still be calling it) — restores ghosted normals
        spherePreview.Dispose();
        tilePreview.Dispose();

        partViewport.Dispose();
        fonts.Dispose();
        ipcProvider.Dispose();
        designWatcher.Dispose();
        designBindings.PresetsSuperseded -= presets.ClearAllApplied;
        presets.Dispose();
        designBindings.Dispose();
        uvMapDl.Dispose();
        effectsDl.Dispose();
        usageStats.Dispose();   // writes the day's tally; the window referencing it is already removed
        hatCompat.Dispose();   // before the compositor: it unsubscribes from that object's event
        compositor.Dispose();
        glamourer.Dispose();
        penumbra.Dispose();
        loc.Dispose();   // last: unhooks LanguageChanged, which anything above could still be drawing under
    }
}
