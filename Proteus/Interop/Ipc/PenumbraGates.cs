using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Penumbra.Api.Enums;

namespace Proteus.Interop.Ipc;

// Penumbra's call gates as Proteus uses them, one class per row of docs/ipc-contract.md §1 and §2 (P1–P35). Each base
// type's generic arguments are exactly the gate's; Invoke takes friendlier parameters, fills in the values the gate has
// no default for, and casts the int codes to the enums.

/// <summary>P1.</summary>
public sealed class PenumbraApiVersion(IDalamudPluginInterface pi)
    : IpcFunc<(int Breaking, int Features)>(pi, Label)
{
    public const string Label = "Penumbra.ApiVersion.V5";
    public (int Breaking, int Features) Invoke() => Call();
}

/// <summary>P2: mod directory to mod name.</summary>
public sealed class PenumbraGetModList(IDalamudPluginInterface pi)
    : IpcFunc<Dictionary<string, string>>(pi, Label)
{
    public const string Label = "Penumbra.GetModList";
    public Dictionary<string, string> Invoke() => Call();
}

/// <summary>P3.</summary>
public sealed class PenumbraGetModDirectory(IDalamudPluginInterface pi)
    : IpcFunc<string>(pi, Label)
{
    public const string Label = "Penumbra.GetModDirectory";
    public string Invoke() => Call();
}

/// <summary>P4.</summary>
public sealed class PenumbraGetCollectionForObject(IDalamudPluginInterface pi)
    : IpcFunc<int, (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection)>(pi, Label)
{
    public const string Label = "Penumbra.GetCollectionForObject.V5";
    public (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection) Invoke(int gameObjectIndex)
        => Call(gameObjectIndex);
}

/// <summary>P5: collection id to name.</summary>
public sealed class PenumbraGetCollections(IDalamudPluginInterface pi)
    : IpcFunc<Dictionary<Guid, string>>(pi, Label)
{
    public const string Label = "Penumbra.GetCollections.V5";
    public Dictionary<Guid, string> Invoke() => Call();
}

/// <summary>P6: (enabled, priority, options, inherited, temporary), or null.</summary>
public sealed class PenumbraGetCurrentModSettingsWithTemp(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, bool, bool, int, (int, (bool, int, Dictionary<string, List<string>>, bool, bool)?)>(pi, Label)
{
    public const string Label = "Penumbra.GetCurrentModSettingsWithTemp";

    public (PenumbraApiEc, (bool, int, Dictionary<string, List<string>>, bool, bool)?) Invoke(Guid collectionId,
        string modDirectory, string modName = "", bool ignoreInheritance = false, bool ignoreTemporary = false, int key = 0)
    {
        var (ec, settings) = Call(collectionId, modDirectory, modName, ignoreInheritance, ignoreTemporary, key);
        return ((PenumbraApiEc)ec, settings);
    }
}

/// <summary>P7: mod directory to (enabled, priority, options, inherited, temporary), or null.</summary>
public sealed class PenumbraGetAllModSettings(IDalamudPluginInterface pi)
    : IpcFunc<Guid, bool, bool, int, (int, Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>?)>(pi, Label)
{
    public const string Label = "Penumbra.GetAllModSettings";

    public (PenumbraApiEc, Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>?) Invoke(
        Guid collectionId, bool ignoreInheritance, bool ignoreTemporary, int key)
    {
        var (ec, all) = Call(collectionId, ignoreInheritance, ignoreTemporary, key);
        return ((PenumbraApiEc)ec, all);
    }
}

/// <summary>P8. At the gate the mod name comes before the key.</summary>
public sealed class PenumbraRemoveTemporaryModSettings(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, int, int>(pi, Label)
{
    public const string Label = "Penumbra.RemoveTemporaryModSettings.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory, int key = 0, string modName = "")
        => (PenumbraApiEc)Call(collectionId, modDirectory, modName, key);
}

/// <summary>P9: the settings cross as one (inherit, enabled, priority, options) tuple.</summary>
public sealed class PenumbraSetTemporaryModSettings(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int>(pi, Label)
{
    public const string Label = "Penumbra.SetTemporaryModSettings.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory, bool inherit, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> options, string source, int key, string modName = "")
        => (PenumbraApiEc)Call(collectionId, modDirectory, modName, (inherit, enabled, priority, options), source, key);
}

/// <summary>P10.</summary>
public sealed class PenumbraRemoveAllTemporaryModSettings(IDalamudPluginInterface pi)
    : IpcFunc<Guid, int, int>(pi, Label)
{
    public const string Label = "Penumbra.RemoveAllTemporaryModSettings.V5";
    public PenumbraApiEc Invoke(Guid collectionId, int key) => (PenumbraApiEc)Call(collectionId, key);
}

/// <summary>P11: the code, the temporary settings or null, and who placed them.</summary>
public sealed class PenumbraQueryTemporaryModSettings(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, int, (int, (bool ForceInherit, bool Enabled, int Priority, Dictionary<string, List<string>> Settings)?, string)>(pi, Label)
{
    public const string Label = "Penumbra.QueryTemporaryModSettings.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory,
        out (bool ForceInherit, bool Enabled, int Priority, Dictionary<string, List<string>> Settings)? settings,
        out string source, string modName = "", int key = 0)
    {
        var (ec, found, by) = Call(collectionId, modDirectory, modName, key);
        settings = found;
        source = by;
        return (PenumbraApiEc)ec;
    }
}

/// <summary>P12.</summary>
public sealed class PenumbraResolvePlayerPath(IDalamudPluginInterface pi)
    : IpcFunc<string, string>(pi, Label)
{
    public const string Label = "Penumbra.ResolvePlayerPath";
    public string Invoke(string gamePath) => Call(gamePath);
}

/// <summary>P13: (forward resolved, reverse resolved).</summary>
public sealed class PenumbraResolvePlayerPaths(IDalamudPluginInterface pi)
    : IpcFunc<string[], string[], (string[], string[][])>(pi, Label)
{
    public const string Label = "Penumbra.ResolvePlayerPaths";
    public (string[], string[][]) Invoke(string[] forward, string[] reverse) => Call(forward, reverse);
}

/// <summary>P14.</summary>
public sealed class PenumbraAddMod(IDalamudPluginInterface pi)
    : IpcFunc<string, int>(pi, Label)
{
    public const string Label = "Penumbra.AddMod.V5";
    public PenumbraApiEc Invoke(string modDirectory) => (PenumbraApiEc)Call(modDirectory);
}

/// <summary>P15.</summary>
public sealed class PenumbraReloadMod(IDalamudPluginInterface pi)
    : IpcFunc<string, string, int>(pi, Label)
{
    public const string Label = "Penumbra.ReloadMod.V5";
    public PenumbraApiEc Invoke(string modDirectory, string modName = "") => (PenumbraApiEc)Call(modDirectory, modName);
}

/// <summary>P16.</summary>
public sealed class PenumbraDeleteMod(IDalamudPluginInterface pi)
    : IpcFunc<string, string, int>(pi, Label)
{
    public const string Label = "Penumbra.DeleteMod.V5";
    public PenumbraApiEc Invoke(string modDirectory, string modName = "") => (PenumbraApiEc)Call(modDirectory, modName);
}

/// <summary>P17. At the gate the mod name is second and the new path third.</summary>
public sealed class PenumbraSetModPath(IDalamudPluginInterface pi)
    : IpcFunc<string, string, string, int>(pi, Label)
{
    public const string Label = "Penumbra.SetModPath.V5";

    public PenumbraApiEc Invoke(string modDirectory, string newPath, string modName = "")
        => (PenumbraApiEc)Call(modDirectory, modName, newPath);
}

/// <summary>P18: the bool is the enabled state.</summary>
public sealed class PenumbraTrySetMod(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, bool, int>(pi, Label)
{
    public const string Label = "Penumbra.TrySetMod.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory, bool enabled, string modName = "")
        => (PenumbraApiEc)Call(collectionId, modDirectory, modName, enabled);
}

/// <summary>P19.</summary>
public sealed class PenumbraTrySetModPriority(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, int, int>(pi, Label)
{
    public const string Label = "Penumbra.TrySetModPriority.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory, int priority, string modName = "")
        => (PenumbraApiEc)Call(collectionId, modDirectory, modName, priority);
}

/// <summary>P20.</summary>
public sealed class PenumbraTryInheritMod(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, bool, int>(pi, Label)
{
    public const string Label = "Penumbra.TryInheritMod.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory, bool inherit, string modName = "")
        => (PenumbraApiEc)Call(collectionId, modDirectory, modName, inherit);
}

/// <summary>P21: exactly one option.</summary>
public sealed class PenumbraTrySetModSetting(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, string, string, int>(pi, Label)
{
    public const string Label = "Penumbra.TrySetModSetting.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory, string optionGroupName, string optionName,
        string modName = "")
        => (PenumbraApiEc)Call(collectionId, modDirectory, modName, optionGroupName, optionName);
}

/// <summary>P22: zero or several options.</summary>
public sealed class PenumbraTrySetModSettings(IDalamudPluginInterface pi)
    : IpcFunc<Guid, string, string, string, IReadOnlyList<string>, int>(pi, Label)
{
    public const string Label = "Penumbra.TrySetModSettings.V5";

    public PenumbraApiEc Invoke(Guid collectionId, string modDirectory, string optionGroupName,
        IReadOnlyList<string> optionNames, string modName = "")
        => (PenumbraApiEc)Call(collectionId, modDirectory, modName, optionGroupName, optionNames);
}

/// <summary>P23: an action; the redraw type crosses as int.</summary>
public sealed class PenumbraRedrawObject(IDalamudPluginInterface pi)
    : IpcAction<int, int>(pi, Label)
{
    public const string Label = "Penumbra.RedrawObject.V5";
    public void Invoke(int gameObjectIndex, RedrawType type) => Call(gameObjectIndex, (int)type);
}

/// <summary>P24: the tab crosses as int.</summary>
public sealed class PenumbraOpenMainWindow(IDalamudPluginInterface pi)
    : IpcFunc<int, string, string, int>(pi, Label)
{
    public const string Label = "Penumbra.OpenMainWindow.V5";

    public PenumbraApiEc Invoke(TabType tab, string modDirectory, string modName = "")
        => (PenumbraApiEc)Call((int)tab, modDirectory, modName);
}

/// <summary>P25: one map (resolved file to game paths) per object index; an entry may be null.</summary>
public sealed class PenumbraGetGameObjectResourcePaths(IDalamudPluginInterface pi)
    : IpcFunc<ushort[], Dictionary<string, HashSet<string>>?[]>(pi, Label)
{
    public const string Label = "Penumbra.GetGameObjectResourcePaths.V5";
    public Dictionary<string, HashSet<string>>?[] Invoke(params ushort[] gameObjectIndices) => Call(gameObjectIndices);
}

/// <summary>P26: game path to file.</summary>
public sealed class PenumbraAddTemporaryMod(IDalamudPluginInterface pi)
    : IpcFunc<string, Guid, Dictionary<string, string>, string, int, int>(pi, Label)
{
    public const string Label = "Penumbra.AddTemporaryMod.V5";

    public PenumbraApiEc Invoke(string tag, Guid collectionId, Dictionary<string, string> paths, string manipString, int priority)
        => (PenumbraApiEc)Call(tag, collectionId, paths, manipString, priority);
}

/// <summary>P27.</summary>
public sealed class PenumbraRemoveTemporaryMod(IDalamudPluginInterface pi)
    : IpcFunc<string, Guid, int, int>(pi, Label)
{
    public const string Label = "Penumbra.RemoveTemporaryMod.V5";
    public PenumbraApiEc Invoke(string tag, Guid collectionId, int priority) => (PenumbraApiEc)Call(tag, collectionId, priority);
}

/// <summary>P28.</summary>
public sealed class PenumbraGetPlayerMetaManipulations(IDalamudPluginInterface pi)
    : IpcFunc<string>(pi, Label)
{
    public const string Label = "Penumbra.GetPlayerMetaManipulations";
    public string Invoke() => Call();
}

// ── Events (§2) ────────────────────────────────────────────────────────────────────────────────

/// <summary>P29: the change crosses as the enum.</summary>
public sealed class PenumbraModSettingChanged(IDalamudPluginInterface pi, Action<ModSettingChange, Guid, string, bool> handler)
    : IpcEvent<ModSettingChange, Guid, string, bool>(pi, Label, handler)
{
    public const string Label = "Penumbra.ModSettingChanged.V5";
}

/// <summary>P30.</summary>
public sealed class PenumbraModAdded(IDalamudPluginInterface pi, Action<string> handler)
    : IpcEvent<string>(pi, Label, handler)
{
    public const string Label = "Penumbra.ModAdded";
}

/// <summary>P31.</summary>
public sealed class PenumbraModDeleted(IDalamudPluginInterface pi, Action<string> handler)
    : IpcEvent<string>(pi, Label, handler)
{
    public const string Label = "Penumbra.ModDeleted";
}

/// <summary>P32: (old directory, new directory).</summary>
public sealed class PenumbraModMoved(IDalamudPluginInterface pi, Action<string, string> handler)
    : IpcEvent<string, string>(pi, Label, handler)
{
    public const string Label = "Penumbra.ModMoved";
}

/// <summary>P33: (game object address, object table index).</summary>
public sealed class PenumbraGameObjectRedrawn(IDalamudPluginInterface pi, Action<nint, int> handler)
    : IpcEvent<nint, int>(pi, Label, handler)
{
    public const string Label = "Penumbra.GameObjectRedrawn";
}

/// <summary>P34.</summary>
public sealed class PenumbraInitialized(IDalamudPluginInterface pi, Action handler)
    : IpcEvent(pi, Label, handler)
{
    public const string Label = "Penumbra.Initialized";
}

/// <summary>P35.</summary>
public sealed class PenumbraDisposed(IDalamudPluginInterface pi, Action handler)
    : IpcEvent(pi, Label, handler)
{
    public const string Label = "Penumbra.Disposed";
}
