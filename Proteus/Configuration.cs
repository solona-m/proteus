using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Proteus;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool PluginEnabled { get; set; } = true;

    public int ManagedModPriority { get; set; } = 999;

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
