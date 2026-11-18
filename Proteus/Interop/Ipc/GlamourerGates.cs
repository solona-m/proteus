using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace Proteus.Interop.Ipc;

// Glamourer's call gates as Proteus's shipped code uses them, one class per row of docs/ipc-contract.md §1 and §2
// (G1–G8). Codes cross as int, apply flags as ulong and slots as byte; Invoke casts them.

/// <summary>G1: design id to display name.</summary>
public sealed class GlamourerGetDesignList(IDalamudPluginInterface pi)
    : IpcFunc<Dictionary<Guid, string>>(pi, Label)
{
    public const string Label = "Glamourer.GetDesignList.V2";
    public Dictionary<Guid, string> Invoke() => Call();
}

/// <summary>G2.</summary>
public sealed class GlamourerGetDesignJObject(IDalamudPluginInterface pi)
    : IpcFunc<Guid, JObject?>(pi, Label)
{
    public const string Label = "Glamourer.GetDesignJObject";
    public JObject? Invoke(Guid designId) => Call(designId);
}

/// <summary>G3.</summary>
public sealed class GlamourerGetState(IDalamudPluginInterface pi)
    : IpcFunc<int, uint, (int, JObject?)>(pi, Label)
{
    public const string Label = "Glamourer.GetState";

    public (GlamourerApiEc, JObject?) Invoke(int objectIndex, uint key = 0)
    {
        var (ec, state) = Call(objectIndex, key);
        return ((GlamourerApiEc)ec, state);
    }
}

/// <summary>G4.</summary>
public sealed class GlamourerReapplyState(IDalamudPluginInterface pi)
    : IpcFunc<int, uint, ulong, int>(pi, Label)
{
    public const string Label = "Glamourer.ReapplyState";

    public GlamourerApiEc Invoke(int objectIndex, uint key, ApplyFlag flags)
        => (GlamourerApiEc)Call(objectIndex, key, (ulong)flags);
}

/// <summary>G5.</summary>
public sealed class GlamourerSetBonusItem(IDalamudPluginInterface pi)
    : IpcFunc<int, byte, ulong, uint, ulong, int>(pi, Label)
{
    public const string Label = "Glamourer.SetBonusItem";

    public GlamourerApiEc Invoke(int objectIndex, ApiBonusSlot slot, ulong itemId, uint key, ApplyFlag flags)
        => (GlamourerApiEc)Call(objectIndex, (byte)slot, itemId, key, (ulong)flags);
}

/// <summary>G6. The stains must be a non-empty list, never a byte array (see GlamourerBridge.NoStains).</summary>
public sealed class GlamourerSetItem(IDalamudPluginInterface pi)
    : IpcFunc<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>(pi, Label)
{
    public const string Label = "Glamourer.SetItem.V3";

    public GlamourerApiEc Invoke(int objectIndex, ApiEquipSlot slot, ulong itemId, IReadOnlyList<byte> stains, uint key,
        ApplyFlag flags)
        => (GlamourerApiEc)Call(objectIndex, (byte)slot, itemId, stains, key, (ulong)flags);
}

// ── Events (§2). Both labels begin with "Penumbra." although they are Glamourer's: that is the label Glamourer
// registers them under. ─────────────────────────────────────────────────────────────────────────

/// <summary>G7: (actor address, change type).</summary>
public sealed class GlamourerStateChangedWithType(IDalamudPluginInterface pi, Action<nint, StateChangeType> handler)
    : IpcEvent<nint, StateChangeType>(pi, Label, handler)
{
    public const string Label = "Penumbra.StateChangedWithType";
}

/// <summary>G8: (actor address, finalization type).</summary>
public sealed class GlamourerStateFinalized(IDalamudPluginInterface pi, Action<nint, StateFinalizationType> handler)
    : IpcEvent<nint, StateFinalizationType>(pi, Label, handler)
{
    public const string Label = "Penumbra.StateFinalized";
}
