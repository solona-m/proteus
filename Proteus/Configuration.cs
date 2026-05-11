using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Proteus;

[Serializable]
public class ModOverride
{
    public bool Disabled { get; set; } = false;
    public int? PriorityOverride { get; set; } = null;
    public float TintR { get; set; } = 1f;
    public float TintG { get; set; } = 1f;
    public float TintB { get; set; } = 1f;
    public float TintA { get; set; } = 1f; // tint strength: 0 = no tint, 1 = full tint
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool PluginEnabled { get; set; } = true;

    public int ManagedModPriority { get; set; } = 999;

    public Dictionary<string, ModOverride> ModOverrides { get; set; } = new();

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
