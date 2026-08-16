using System;
using System.Linq;
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
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;

    /// <summary>Bumped when there's something worth calling out. NOT a reliable "did my rebuild load?"
    /// signal on its own — it is hand-maintained, and it sat at 254 across dozens of builds because
    /// bumping it is easy to forget. <see cref="BuildStamp"/> is the one that can't go stale.</summary>
    public const int BuildNumber = 419;

    /// <summary>
    /// When this assembly was compiled, as MM-dd HH:mm:ss. Baked in by the csproj (an AssemblyMetadata
    /// attribute) rather than read from the file, because Dalamud loads plugins from a stream — so
    /// <c>Assembly.Location</c> is empty and there is no DLL path to stat at runtime.
    /// <para/>
    /// This is the value to trust when asking "did my rebuild actually load?": it moves on every compile
    /// whether or not anyone remembered to touch <see cref="BuildNumber"/>.
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
    private readonly CompositorService compositor;
    private volatile bool _disposed;
    private readonly DesignBindingService designBindings;
    private readonly GlamourerDesignWatcher designWatcher;
    private readonly WindowSystem windowSystem;
    private readonly StatusWindow statusWindow;
    private readonly IpcProvider ipcProvider;
    private readonly SphereMapPreview spherePreview;
    private readonly Gui.ProteusFonts fonts;
    private readonly ColorTableHighlighter highlighter;
    private readonly SkinDiffuseGlow skinGlow;
    private readonly ShellNormalGhost shellGhost;
    private readonly ShellColorsetApplier shellColorset;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework)
    {
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        penumbra = new PenumbraBridge(pluginInterface, log);
        glamourer = new GlamourerBridge(pluginInterface, ObjectTable, log);
        textureLoader = new TextureLoader(DataManager, log)
        {
            DecodeCacheBudgetBytes = Math.Max(256, config.DecodeCacheBudgetMb) * 1024L * 1024,
        };
        discovery = new SidecarDiscoveryService(penumbra, log)
        {
            AssemblyDir = pluginInterface.AssemblyLocation.DirectoryName,
        };
        // Fill the global effects library with the bundled starter set (skips names already there).
        // Safe to call before Penumbra is up — it no-ops until the mod directory is resolvable, and
        // OnPenumbraReady calls it again once it is.
        discovery.SeedDefaultEffects();
        uvRemap = new UVRemapService(log, pluginInterface.AssemblyLocation.DirectoryName!);
        uvMapDl = new UVMapDownloadService(log, pluginInterface.AssemblyLocation.DirectoryName!);
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
        designWatcher = new GlamourerDesignWatcher(designBindings, config.GlamourerDesignDirOverride ?? glamourer.DesignsDirectory, log);
        ipcProvider = new IpcProvider(pluginInterface, compositor, discovery, log);

        // Sphere-map thumbnails for the colour table editor.
        spherePreview = new SphereMapPreview(TextureProvider, log);
        Gui.ColorTableEditor.Spheres = spherePreview;

        // Display typography (the game's Jupiter) for section headings and the header band. The atlas
        // builds asynchronously; drawing before it lands falls back to the default font on its own.
        fonts = new Gui.ProteusFonts(pluginInterface);
        Gui.ProteusStyle.Fonts = fonts;

        // Glow-effect thumbnails (loaded per-file, cached by Dalamud's texture provider).
        Gui.ColorTableEditor.EffectThumbs = new Gui.EffectPreview(TextureProvider);

        // Ghosts the gear shells stacked above a highlighted layer so an occluded colorset Glow shows
        // through them (semi-transparent). Driven by both highlighters below.
        shellGhost = new ShellNormalGhost(Framework, ObjectTable, textureLoader, Log);

        // Live colorset "glow / target" highlighter (framework-thread material editing).
        highlighter = new ColorTableHighlighter(Framework, ObjectTable) { Ghost = shellGhost };
        Gui.ColorTableEditor.Highlighter = highlighter;
        compositor.Highlighter = highlighter;   // so a recomposite (shell may rebuild) clears a stale glow

        // Live skin (body diffuse) glow via render-material texture rebind.
        skinGlow = new SkinDiffuseGlow(Framework, ObjectTable, textureLoader, Log) { Ghost = shellGhost };
        Gui.ColorTableEditor.SkinGlow = skinGlow;

        // Re-asserts each shell's colorset onto the live material after the game rebuilds it (redraw), so a
        // dyed cloth shell shows its colour — the game's load-time colour-table cook drops the diffuse tint.
        // Takes the highlighter so it leaves a glow-highlighted shell's slot alone instead of fighting it.
        shellColorset = new ShellColorsetApplier(Framework, ObjectTable, highlighter);

        var modCreation = new ModCreationService(penumbra, compositor, config, textureLoader, log);
        // Body catalogue for imports: probes the game data for the human bodies that exist, so an imported
        // overlay can name every race's material without anyone maintaining a race table.
        var bodyCatalog = new BodyMaterialCatalog(DataManager.FileExists);
        var onionImport = new OnionImportService(
            penumbra, compositor, modCreation, textureLoader, bodyCatalog, config, log);
        var modExport = new ModExportService(penumbra, log);
        statusWindow = new StatusWindow(compositor, discovery, penumbra, config, designBindings, uvMapDl, uvRemap,
            modCreation, onionImport, modExport);

        windowSystem = new WindowSystem("Proteus");
        windowSystem.AddWindow(statusWindow);

        pluginInterface.UiBuilder.DisableGposeUiHide = true;
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Proteus overlay compositor status window. \"/proteus config\" opens it on Settings.",
        });

        // The boot composite is held from CompositorService's construction (see BootCompositeHold) so
        // the design-binding boot restore can publish its overrides before the first composite reads
        // them. Nothing armed a restore → nothing to wait for, release it now.
        if (!designBindings.BootRestoreArmed)
            compositor.BootCompositeHold = false;

        // Recomposite on startup only if Penumbra's mod list is already readable, and only when nothing
        // holds the boot composite — a composite that ran before the overrides landed would paint
        // metadata colours and have to be redone, i.e. two multi-second pipelines and two redraws per
        // load. At early load GetPlayerCollectionId() returns null and discovery returns empty, which
        // would wipe the existing output. OnPenumbraReady handles the normal boot path; this covers
        // plugin-reload where PenumbraReady won't fire again (and OnBootPoll covers the held case,
        // once FinishBootRestore releases it).
        if (config.PluginEnabled && penumbra.IsAvailable && !compositor.BootCompositeHold
            && discovery.DiscoverEnabled().Count > 0)
            compositor.TriggerRecomposite("startup");

        log.Information("Proteus loaded. Penumbra={0} [build: equipped-model second-skin]", penumbra.IsAvailable);
        ChatGui.Print($"[Proteus] loaded — build #{BuildNumber} ({BuildStamp})");
    }

    private void DrawUi()
    {
        // Before the window system, and unconditionally: an .omp import writes its mod on the thread pool,
        // and Dalamud stops calling a CLOSED window's Draw — so pumping this from the window itself would
        // strand a mod that finished copying after the user closed Proteus. See StatusWindow.TickImport.
        statusWindow.TickImport();
        windowSystem.Draw();
    }

    private void OpenMainUi() => statusWindow.Show();

    // The gear icon in the plugin installer — settings live in the status window's Settings tab.
    private void OpenConfigUi() => statusWindow.OpenToSettings();

    private void OnCommand(string command, string args)
    {
        // "/proteus config" is a request to see the settings, so it opens rather than toggles — typing it
        // while the window is already up would otherwise close it, the opposite of what was asked.
        switch (args.Trim())
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

        // Last chance for an import whose disk work landed after the final frame: the mod is already
        // written into Penumbra's directory, and unloading without registering it would leave a folder
        // Penumbra never adds — enabled by nobody, with no layout selected, painting nothing. "unloading"
        // keeps it to the registration: no window, and above all no recomposite, which would still pass
        // CompositorService's disposal guard (that runs further down) and wake into a torn-down plugin.
        try { statusWindow.TickImport(unloading: true); } catch { /* tearing down — never block the unload */ }

        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        windowSystem.RemoveAllWindows();
        Gui.ColorTableEditor.Spheres = null;
        Gui.ColorTableEditor.EffectThumbs = null;
        Gui.ColorTableEditor.Highlighter = null;
        Gui.ColorTableEditor.SkinGlow = null;
        Gui.ProteusStyle.Fonts = null;   // before the dispose below, matching the null-then-dispose order above
        skinGlow.Dispose();
        shellColorset.Dispose();   // before the highlighter it references
        highlighter.Dispose();
        shellGhost.Dispose();   // after the highlighters (they may still be calling it) — restores ghosted normals
        spherePreview.Dispose();
        fonts.Dispose();
        ipcProvider.Dispose();
        designWatcher.Dispose();
        designBindings.Dispose();
        uvMapDl.Dispose();
        compositor.Dispose();
        glamourer.Dispose();
        penumbra.Dispose();
    }
}
