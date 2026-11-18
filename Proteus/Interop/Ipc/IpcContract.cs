using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Proteus.Interop.Ipc;

public enum IpcKind { Func, Action, Event }

/// <summary>
/// One call gate Proteus uses: its owner ("Penumbra" or "Glamourer"), label, kind and the exact types passed to
/// Dalamud's GetIpcSubscriber. For an action or an event <see cref="Returns"/> is null and <see cref="Arguments"/> leaves
/// out the trailing object Dalamud appends.
/// </summary>
public sealed record IpcContractEntry(string Owner, string Label, IpcKind Kind, Type[] Arguments, Type? Returns);

/// <summary>
/// Every call gate the shipped plugin uses (docs/ipc-contract.md §1 and §2), read off the gate classes themselves so the
/// list cannot drift from what is actually requested. The harness-only gates (§6) are deliberately not here.
/// </summary>
public static class IpcContract
{
    public static IReadOnlyList<IpcContractEntry> Entries { get; } = Build(
    [
        ("Penumbra", typeof(PenumbraApiVersion)),
        ("Penumbra", typeof(PenumbraGetModList)),
        ("Penumbra", typeof(PenumbraGetModDirectory)),
        ("Penumbra", typeof(PenumbraGetCollectionForObject)),
        ("Penumbra", typeof(PenumbraGetCollections)),
        ("Penumbra", typeof(PenumbraGetCurrentModSettingsWithTemp)),
        ("Penumbra", typeof(PenumbraGetAllModSettings)),
        ("Penumbra", typeof(PenumbraRemoveTemporaryModSettings)),
        ("Penumbra", typeof(PenumbraSetTemporaryModSettings)),
        ("Penumbra", typeof(PenumbraRemoveAllTemporaryModSettings)),
        ("Penumbra", typeof(PenumbraQueryTemporaryModSettings)),
        ("Penumbra", typeof(PenumbraResolvePlayerPath)),
        ("Penumbra", typeof(PenumbraResolvePlayerPaths)),
        ("Penumbra", typeof(PenumbraAddMod)),
        ("Penumbra", typeof(PenumbraReloadMod)),
        ("Penumbra", typeof(PenumbraDeleteMod)),
        ("Penumbra", typeof(PenumbraSetModPath)),
        ("Penumbra", typeof(PenumbraTrySetMod)),
        ("Penumbra", typeof(PenumbraTrySetModPriority)),
        ("Penumbra", typeof(PenumbraTryInheritMod)),
        ("Penumbra", typeof(PenumbraTrySetModSetting)),
        ("Penumbra", typeof(PenumbraTrySetModSettings)),
        ("Penumbra", typeof(PenumbraRedrawObject)),
        ("Penumbra", typeof(PenumbraOpenMainWindow)),
        ("Penumbra", typeof(PenumbraGetGameObjectResourcePaths)),
        ("Penumbra", typeof(PenumbraAddTemporaryMod)),
        ("Penumbra", typeof(PenumbraRemoveTemporaryMod)),
        ("Penumbra", typeof(PenumbraGetPlayerMetaManipulations)),
        ("Penumbra", typeof(PenumbraModSettingChanged)),
        ("Penumbra", typeof(PenumbraModAdded)),
        ("Penumbra", typeof(PenumbraModDeleted)),
        ("Penumbra", typeof(PenumbraModMoved)),
        ("Penumbra", typeof(PenumbraGameObjectRedrawn)),
        ("Penumbra", typeof(PenumbraInitialized)),
        ("Penumbra", typeof(PenumbraDisposed)),
        ("Glamourer", typeof(GlamourerGetDesignList)),
        ("Glamourer", typeof(GlamourerGetDesignJObject)),
        ("Glamourer", typeof(GlamourerGetState)),
        ("Glamourer", typeof(GlamourerReapplyState)),
        ("Glamourer", typeof(GlamourerSetBonusItem)),
        ("Glamourer", typeof(GlamourerSetItem)),
        ("Glamourer", typeof(GlamourerStateChangedWithType)),
        ("Glamourer", typeof(GlamourerStateFinalized)),
    ]);

    private static IpcContractEntry[] Build((string Owner, Type Gate)[] gates)
        => gates.Select(g => Describe(g.Owner, g.Gate)).ToArray();

    /// <summary>A gate class's entry: its Label constant, and its base type's generic arguments.</summary>
    internal static IpcContractEntry Describe(string owner, Type gate)
    {
        var label = (string)gate.GetField("Label", BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;
        var baseType = gate.BaseType!;
        var args = baseType.IsGenericType ? baseType.GetGenericArguments() : Type.EmptyTypes;
        if (typeof(IpcFuncGate).IsAssignableFrom(gate))
            return new IpcContractEntry(owner, label, IpcKind.Func, args[..^1], args[^1]);
        if (typeof(IpcActionGate).IsAssignableFrom(gate))
            return new IpcContractEntry(owner, label, IpcKind.Action, args, null);
        if (typeof(IpcEventGate).IsAssignableFrom(gate))
            return new IpcContractEntry(owner, label, IpcKind.Event, args, null);
        throw new InvalidOperationException("not a call gate: " + gate.Name);
    }
}
