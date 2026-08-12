using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    private readonly EventSubscriber<nint, StateFinalizationType>? stateFinalizedSub;

    // When we last called ReapplyPlayerState, so its own finalization echo can be filtered out. The window
    // only has to cover Glamourer raising the event for a call we just made, not any real user action.
    private const int OwnReapplyEchoMs = 250;
    private long lastOwnReapplyTick;
    private readonly GetDesignList getDesignList;
    private readonly GetDesignJObject getDesignJObject;
    private readonly GetState getState;
    private readonly ReapplyState reapplyState;
    private readonly SetBonusItem setBonusItem;
    private readonly SetItem setItem;

    public bool IsAvailable { get; private set; }

    /// <summary>Fired when Glamourer applies a design, resets, or reapplies state on the local player.</summary>
    public event Action? LocalPlayerStateChanged;

    /// <summary>
    /// The local player's state finished a Glamourer operation, carrying WHICH operation
    /// (<see cref="StateFinalizationType.DesignApplied"/>, <c>ReapplyAutomation</c>, the <c>Revert*</c>
    /// family, <c>Gearset</c>…). This is what design binding listens to: the per-field
    /// <see cref="StateChangeType"/> cannot distinguish an automation design-apply from a revert (both
    /// arrive as Reapply/Reset), and fires several times per operation where this fires once.
    /// </summary>
    public event Action<StateFinalizationType>? LocalPlayerStateFinalized;

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
        setItem          = new SetItem(pluginInterface);

        try
        {
            stateChangedSub   = StateChangedWithType.Subscriber(pluginInterface, OnStateChanged);
            stateFinalizedSub = StateFinalized.Subscriber(pluginInterface, OnStateFinalized);
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
            // Stamp BEFORE invoking: Glamourer may raise the finalization synchronously, and
            // OnStateFinalized uses this to recognise the echo as ours (see OwnReapplyEchoMs).
            Interlocked.Exchange(ref lastOwnReapplyTick, Environment.TickCount64);

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

    /// <summary>
    /// Equip an item in a RING slot on the local player, or clear it with <paramref name="itemId"/> 0. Same
    /// contract as <see cref="SetGlasses"/>: <see cref="ApplyFlag.Once"/> so it reverts on the next
    /// design/reset (the caller re-asserts) and is deliberately NOT locked, leaving the slot under the
    /// player's control. MUST be called on the framework thread. Returns true only on Glamourer success.
    /// <para/>
    /// The slot must match the one the shell was published for: its path and EQDP entry name either
    /// RFinger (c….a0053_rir.mdl) or LFinger (…_ril.mdl), and the game only loads the one it asked for.
    /// </summary>
    public bool SetRing(ulong itemId, bool leftHand)
    {
        if (!IsAvailable) return false;
        var slot = leftHand ? ApiEquipSlot.LFinger : ApiEquipSlot.RFinger;
        try
        {
            // No stains: an invisible ring has nothing to dye, and passing the player's would be a change
            // we have no business making.
            var ec = setItem.Invoke(0, slot, itemId, [], key: 0, ApplyFlag.Once);
            if (ec != GlamourerApiEc.Success)
            {
                log.Debug("[Proteus] SetItem({0},{1}) -> {2}", slot, itemId, ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Warning("[Proteus] SetItem({0},{1}) failed: {2}", slot, itemId, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Glamourer finished an operation on the local player, reported once per operation (the change-type
    /// event fires per field). This drives design binding — see
    /// <see cref="DesignBindingService.IsApplySignal(StateFinalizationType)"/> for why the finalization
    /// type is the right signal and the change type is not.
    /// </summary>
    private void OnStateFinalized(nint address, StateFinalizationType type)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != address) return;

        // Proteus ends most composites by calling ReapplyPlayerState, and Glamourer reports that back as
        // a Reapply finalization — indistinguishable from an automation-applied design. Swallow our own
        // echo here, at the point that knows it caused it, so no subscriber re-derives state from a change
        // Proteus itself made. Only Reapply is suppressed: it is the only type our reapply produces, so a
        // genuine design application landing in the same window is still delivered.
        if (type == StateFinalizationType.Reapply && WithinOwnReapplyEcho())
        {
            log.Debug("[Proteus] glamourer signal: finalized=Reapply (our own reapply echo, ignored)");
            return;
        }

        log.Information("[Proteus] glamourer signal: finalized={0}", type);
        LocalPlayerStateFinalized?.Invoke(type);
    }

    private bool WithinOwnReapplyEcho()
    {
        var since = unchecked(Environment.TickCount64 - Interlocked.Read(ref lastOwnReapplyTick));
        return since >= 0 && since < OwnReapplyEchoMs;
    }

    private void OnStateChanged(nint address, StateChangeType changeType)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != address) return;

        // Debug-level: this no longer drives design binding (the finalized= signal does), but keeping it
        // paired in the log makes it obvious which signals an action produced.
        log.Debug("[Proteus] glamourer signal: changeType={0}", changeType);

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

        LocalPlayerStateChanged?.Invoke();
    }

    public void Dispose()
    {
        stateChangedSub?.Dispose();
        stateFinalizedSub?.Dispose();
    }
}
