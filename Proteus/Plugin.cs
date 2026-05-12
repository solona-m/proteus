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
    [PluginService] public static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/proteus";

    private readonly Configuration config;
    private readonly PenumbraBridge penumbra;
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
        textureLoader = new TextureLoader(DataManager, log);
        discovery = new SidecarDiscoveryService(penumbra, log);
        compositor = new CompositorService(penumbra, discovery, textureLoader, config, log);

        statusWindow = new StatusWindow(compositor, discovery, penumbra, config);

        windowSystem = new WindowSystem("Proteus");
        windowSystem.AddWindow(statusWindow);

        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Proteus overlay compositor status window.",
        });

        // Recomposite on startup to sync with current Penumbra state.
        if (config.PluginEnabled && penumbra.IsAvailable)
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
        penumbra.Dispose();
    }
}
