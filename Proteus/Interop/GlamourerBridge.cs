using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace Proteus.Interop;

public class GlamourerBridge : IDisposable
{
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly IDalamudPluginInterface pluginInterface;

    private readonly EventSubscriber<nint, StateChangeType>? stateChangedSub;
    private readonly GetDesignList getDesignList;
    private readonly GetDesignJObject getDesignJObject;
    private readonly GetState getState;
    private readonly ReapplyState reapplyState;
    private readonly SetBonusItem setBonusItem;

    public bool IsAvailable { get; private set; }

    /// <summary>Fired when Glamourer applies a design, resets, or reapplies state on the local player.</summary>
    public event Action? LocalPlayerStateChanged;

    /// <summary>
    /// Like <see cref="LocalPlayerStateChanged"/> but carries the change type so consumers can
    /// react specifically to design applications (used by the design-binding heuristic).
    /// </summary>
    public event Action<StateChangeType>? LocalPlayerStateChangedTyped;

    /// <summary>
    /// Fired when the local player's character customization changes in a way that may affect
    /// which race/body materials are active (Model or EntireCustomize), without necessarily
    /// changing the mod set. Consumers should recomposite unconditionally.
    /// </summary>
    public event Action? LocalPlayerCustomizationChanged;

    public GlamourerBridge(IDalamudPluginInterface pluginInterface, IObjectTable objectTable, IPluginLog log)
    {
        this.log             = log;
        this.objectTable     = objectTable;
        this.pluginInterface = pluginInterface;

        // FuncSubscriber construction only creates the call gate (safe even if Glamourer is absent);
        // the Invoke() calls below are individually guarded.
        getDesignList    = new GetDesignList(pluginInterface);
        getDesignJObject = new GetDesignJObject(pluginInterface);
        getState         = new GetState(pluginInterface);
        reapplyState     = new ReapplyState(pluginInterface);
        setBonusItem     = new SetBonusItem(pluginInterface);

        try
        {
            stateChangedSub = StateChangedWithType.Subscriber(pluginInterface, OnStateChanged);
            IsAvailable = true;
            log.Information("[Proteus] Glamourer IPC subscribed.");
        }
        catch (Exception ex)
        {
            log.Warning("[Proteus] Glamourer IPC unavailable — Glamourer design changes won't auto-trigger recomposite. {0}", ex.Message);
        }
    }

    /// <summary>
    /// Glamourer's on-disk designs directory. Glamourer stores designs as {guid}.json under its own
    /// plugin config dir, which is a sibling of Proteus's. Returns null if it can't be determined.
    /// </summary>
    public string? DesignsDirectory
    {
        get
        {
            try
            {
                var parent = pluginInterface.ConfigDirectory.Parent;
                return parent == null ? null : Path.Combine(parent.FullName, "Glamourer", "designs");
            }
            catch { return null; }
        }
    }

    /// <summary>Glamourer's design list (GUID → display name); empty on failure.</summary>
    public Dictionary<Guid, string> GetDesigns()
    {
        try { return getDesignList.Invoke() ?? new(); }
        catch (Exception ex) { log.Warning("[Proteus] GetDesignList failed: {0}", ex.Message); return new(); }
    }

    /// <summary>The serialized data for a single design (includes equipment + apply flags), or null on failure.</summary>
    public JObject? GetDesign(Guid id)
    {
        try { return getDesignJObject.Invoke(id); }
        catch (Exception ex) { log.Warning("[Proteus] GetDesignJObject failed for {0}: {1}", id, ex.Message); return null; }
    }

    /// <summary>The current applied state of an object (default: local player, index 0), or null on failure.</summary>
    public JObject? GetObjectState(int objectIndex = 0)
    {
        try
        {
            var (ec, data) = getState.Invoke(objectIndex);
            return ec == GlamourerApiEc.Success ? data : null;
        }
        catch (Exception ex) { log.Warning("[Proteus] GetState failed: {0}", ex.Message); return null; }
    }

    /// <summary>
    /// Reapply the local player's Glamourer state piecewise (Equipment only), reloading each
    /// equipment slot — including the body slot that carries the skin material — in place via the
    /// game's FlagSlotForUpdate path, without a full despawn/respawn redraw. Returns true only on
    /// success; false (actor not found / no state / unavailable / error) signals the caller to fall
    /// back to a Penumbra redraw.
    /// </summary>
    public bool ReapplyPlayerState()
    {
        if (!IsAvailable) return false;
        try
        {
            // Equipment only: omit Customization (avoids Glamourer's customize-redraw path) and
            // Once/Lock (we don't want to fix or lock state, just trigger the in-place reload).
            var ec = reapplyState.Invoke(0, 0, ApplyFlag.Equipment);
            if (ec != GlamourerApiEc.Success)
            {
                log.Debug("[Proteus] ReapplyState -> {0} (falling back to redraw)", ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Warning("[Proteus] ReapplyState failed: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Equip a bonus (Glasses-slot) item on the local player, or clear it with <paramref name="itemId"/> 0.
    /// Applied with <see cref="ApplyFlag.Once"/> — it reverts on the next design/reset (caller re-asserts),
    /// and is deliberately NOT locked so the player and other plugins keep control of the slot. MUST be
    /// called on the framework thread (it mutates game state). Returns true only on Glamourer success.
    /// </summary>
    public bool SetGlasses(ulong itemId)
    {
        if (!IsAvailable) return false;
        try
        {
            var ec = setBonusItem.Invoke(0, ApiBonusSlot.Glasses, itemId, key: 0, ApplyFlag.Once);
            if (ec != GlamourerApiEc.Success)
            {
                log.Debug("[Proteus] SetBonusItem(Glasses,{0}) -> {1}", itemId, ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Warning("[Proteus] SetBonusItem(Glasses,{0}) failed: {1}", itemId, ex.Message);
            return false;
        }
    }

    private void OnStateChanged(nint address, StateChangeType changeType)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != address) return;

        // Model/EntireCustomize can change race/body without touching mod settings.
        // Fire the customization event so the compositor recomposites unconditionally.
        if (changeType is StateChangeType.Model or StateChangeType.EntireCustomize)
        {
            LocalPlayerCustomizationChanged?.Invoke();
            return;
        }

        // Only care about state-wide changes that can affect which mods are active.
        if (changeType is not (StateChangeType.Design or StateChangeType.Reset or StateChangeType.Reapply))
            return;

        // Typed first so the design-binding heuristic can set its color override before the
        // compositor's (debounced) recomposite reads it.
        LocalPlayerStateChangedTyped?.Invoke(changeType);
        LocalPlayerStateChanged?.Invoke();
    }

    public void Dispose()
    {
        stateChangedSub?.Dispose();
    }
}
