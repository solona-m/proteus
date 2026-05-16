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

    private const string CommandName = "/proteus";

    private readonly Configuration config;
    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly TextureLoader textureLoader;
    private readonly SidecarDiscoveryService discovery;
    private readonly CompositorService compositor;
    private readonly WindowSystem windowSystem;
    private readonly StatusWindow statusWindow;

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
        compositor = new CompositorService(penumbra, glamourer, discovery, textureLoader, config, log);

        statusWindow = new StatusWindow(compositor, discovery, penumbra, config);

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
    }

    private void DrawUi() => windowSystem.Draw();

    private void OpenMainUi() => statusWindow.IsOpen = true;

    private void OnCommand(string command, string args)
        => statusWindow.IsOpen = !statusWindow.IsOpen;

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        windowSystem.RemoveAllWindows();
        compositor.Dispose();
        glamourer.Dispose();
        penumbra.Dispose();
    }
}
