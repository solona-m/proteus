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

    /// <summary>Bumped every dev build so a reload is unmistakable in chat.</summary>
    public const int BuildNumber = 61;

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
        textureLoader = new TextureLoader(DataManager, log);
        discovery = new SidecarDiscoveryService(penumbra, log);
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

        statusWindow = new StatusWindow(compositor, discovery, penumbra, config, designBindings, uvMapDl, uvRemap);

        windowSystem = new WindowSystem("Proteus");
        windowSystem.AddWindow(statusWindow);

        pluginInterface.UiBuilder.DisableGposeUiHide = true;
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Proteus overlay compositor status window.",
        });

        // Recomposite on startup only if Penumbra's mod list is already readable.
        // At early load GetPlayerCollectionId() returns null and discovery returns empty,
        // which would wipe the existing output. OnPenumbraReady handles the normal boot
        // path; this covers plugin-reload where PenumbraReady won't fire again.
        if (config.PluginEnabled && penumbra.IsAvailable && discovery.DiscoverEnabled().Count > 0)
            compositor.TriggerRecomposite("startup");

        log.Information("Proteus loaded. Penumbra={0}", penumbra.IsAvailable);
        ChatGui.Print($"[Proteus] loaded — build #{BuildNumber}");
    }

    private void DrawUi() => windowSystem.Draw();

    private void OpenMainUi() => statusWindow.IsOpen = true;

    private void OnCommand(string command, string args)
        => statusWindow.IsOpen = !statusWindow.IsOpen;

    public void Dispose()
    {
        _disposed = true;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        windowSystem.RemoveAllWindows();
        Gui.ColorTableEditor.Spheres = null;
        spherePreview.Dispose();
        ipcProvider.Dispose();
        designWatcher.Dispose();
        designBindings.Dispose();
        uvMapDl.Dispose();
        compositor.Dispose();
        glamourer.Dispose();
        penumbra.Dispose();
    }
}
