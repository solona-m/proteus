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

    // When someone OTHER than us last had Glamourer reapply the local player: with the Gearset finalization
    // after it, the only evidence of an automation design apply (DesignBindingService.IsInferredAutomationApply).
    private long lastForeignReapplyTick;
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
    /// The local player's state finished a Glamourer operation, carrying which one; fires once per operation. Design
    /// binding listens here: <see cref="StateChangeType"/> cannot tell an automation apply from a revert.
    /// </summary>
    public event Action<StateFinalizationType>? LocalPlayerStateFinalized;

    /// <summary>
    /// Fired when the local player's Model or EntireCustomize changes, which may change the race/body materials
    /// without changing the mod set. Consumers should recomposite unconditionally.
    /// </summary>
    public event Action? LocalPlayerCustomizationChanged;

    /// <summary>
    /// Fired when ONE customize value changes in place on the local player (no redraw, so nothing else notices).
    /// A dragged slider produces a stream; consumers must debounce.
    /// </summary>
    public event Action? LocalPlayerCustomizeChanged;

    /// <summary>
    /// Fired when Glamourer changes an equipped or bonus item on the local player: the only route an equipment
    /// change has to the compositor. Fires per slot, so treat it as ambient.
    /// </summary>
    public event Action? LocalPlayerEquipmentChanged;

    /// <summary>
    /// Every per-field state change on the local player, unfiltered. Raised synchronously inside Glamourer's
    /// operation, so a design apply's burst arrives BEFORE its <see cref="LocalPlayerStateFinalized"/>.
    /// </summary>
    public event Action<StateChangeType>? LocalPlayerStateChangedAny;

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

    /// <summary>Glamourer's designs directory ({guid}.json, beside Proteus's config dir), or null.</summary>
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

    /// <summary>The user's <c>GlamourerDesignDirOverride</c>, when set; assigned once at startup.</summary>
    public string? DesignsDirectoryOverride { get; set; }

    /// <summary>Where design files actually live: the override if set, else the derived path. Every consumer uses this.</summary>
    public string? EffectiveDesignsDirectory
        => string.IsNullOrWhiteSpace(DesignsDirectoryOverride) ? DesignsDirectory : DesignsDirectoryOverride;

    /// <summary>Glamourer's design list (GUID → display name); empty on failure.</summary>
    public Dictionary<Guid, string> GetDesigns()
    {
        try { return getDesignList.Invoke() ?? new(); }
        // Log the exception object: an IPC failure's message is a constant, the cause is the inner exception.
        catch (Exception ex) { log.Warning(ex, "[Proteus] GetDesignList failed"); return new(); }
    }

    /// <summary>Whether the stack trace for a GetDesignJObject failure has been logged this session.</summary>
    private int loggedDesignIpcFailure;

    /// <summary>
    /// The serialized data for a single design, or null on failure. Falls back to the design's file on disk, which
    /// has the same shape, because Glamourer's IPC serializer fails for some designs.
    /// </summary>
    public JObject? GetDesign(Guid id)
    {
        try { return getDesignJObject.Invoke(id); }
        catch (Exception ex)
        {
            // The stack once per session, then a one-liner: it is the same Glamourer defect every time.
            if (Interlocked.Exchange(ref loggedDesignIpcFailure, 1) == 0)
                log.Warning(ex, "[Proteus] GetDesignJObject failed for {0} — reading the design file instead. "
                              + "This is a Glamourer serialization fault, logged once per session", id);
            else
                log.Debug("[Proteus] GetDesignJObject failed for {0} — reading the design file instead", id);

            return ReadDesignFile(id);
        }
    }

    /// <summary>The design's own JSON from Glamourer's config folder, or null if it can't be read.</summary>
    private JObject? ReadDesignFile(Guid id)
    {
        try
        {
            if (EffectiveDesignsDirectory is not { } dir) return null;
            var path = Path.Combine(dir, id.ToString("D") + ".json");
            if (!File.Exists(path)) return null;
            return JObject.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Could not read the design file for {0} either", id);
            return null;
        }
    }

    /// <summary>The current applied state of an object (default: local player, index 0), or null on failure.</summary>
    public JObject? GetObjectState(int objectIndex = 0)
    {
        try
        {
            var (ec, data) = getState.Invoke(objectIndex);
            return ec == GlamourerApiEc.Success ? data : null;
        }
        catch (Exception ex) { log.Warning(ex, "[Proteus] GetState failed"); return null; }
    }

    /// <summary>
    /// Reapply the local player's Glamourer equipment state, reloading each slot in place without a full redraw.
    /// False tells the caller to fall back to a Penumbra redraw.
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
                // InvalidKey: another plugin holds a lock on this state.
                if (ec == GlamourerApiEc.InvalidKey)
                    log.Warning("[Proteus] ReapplyState refused: another plugin holds a Glamourer lock on this character. Falling back to a full redraw.");
                else
                    log.Debug("[Proteus] ReapplyState -> {0} (falling back to redraw)", ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] ReapplyState failed");   // see SetAccessory on logging the object
            return false;
        }
    }

    /// <summary>
    /// Equip a Glasses-slot bonus item on the local player, or clear it with <paramref name="itemId"/> 0. Applied
    /// <see cref="ApplyFlag.Once"/> and unlocked (the caller re-asserts). Framework thread only.
    /// </summary>
    public bool SetGlasses(ulong itemId)
    {
        if (!IsAvailable) return false;
        if (objectTable.LocalPlayer == null) return false;
        try
        {
            var ec = setBonusItem.Invoke(0, ApiBonusSlot.Glasses, itemId, key: 0, ApplyFlag.Once);
            if (ec != GlamourerApiEc.Success)
            {
                if (ec == GlamourerApiEc.InvalidKey)
                    log.Warning("[Proteus] SetBonusItem(Glasses,{0}) refused: another plugin holds a Glamourer lock on the Glasses slot.", itemId);
                else
                    log.Debug("[Proteus] SetBonusItem(Glasses,{0}) -> {1}", itemId, ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] SetBonusItem(Glasses,{0}) failed", itemId);   // see SetAccessory
            return false;
        }
    }

    /// <summary>
    /// Both dye channels set to "no dye". A List, never a <c>byte[]</c>: IPC round-trips through Newtonsoft, which
    /// serialises a byte array as base64, and an empty list reaches Glamourer as null and crashes it.
    /// </summary>
    private static readonly List<byte> NoStains = [0, 0];

    /// <summary>
    /// Equip an item in a ring slot, or clear it with <paramref name="itemId"/> 0; same contract as
    /// <see cref="SetAccessory"/>.
    /// </summary>
    public bool SetRing(ulong itemId, bool leftHand)
        => SetAccessory(itemId, leftHand ? "ril" : "rir");

    /// <summary>
    /// Unequip one gear slot on the local player, by its Glamourer design name ("Head" … "LFinger"). Item 0 is
    /// Glamourer's "Nothing" for the slot. <see cref="ApplyFlag.Once"/> and unlocked, as <see cref="SetAccessory"/>:
    /// the next design or revert decides the slot again. Framework thread only.
    /// </summary>
    public bool UnequipSlot(string slotName)
    {
        if (!IsAvailable || objectTable.LocalPlayer == null) return false;
        if (!Enum.TryParse<ApiEquipSlot>(slotName, out var slot) || slot is ApiEquipSlot.Unknown or ApiEquipSlot.MainHand or ApiEquipSlot.OffHand)
        {
            log.Warning("[Proteus] UnequipSlot: not an unequippable slot \"{0}\"", slotName);
            return false;
        }
        try
        {
            var ec = setItem.Invoke(0, slot, 0, NoStains, key: 0, ApplyFlag.Once);   // NoStains: see there
            if (ec != GlamourerApiEc.Success)
            {
                log.Debug("[Proteus] UnequipSlot({0}) -> {1}", slot, ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] UnequipSlot({0}) failed", slot);   // see SetAccessory on logging the object
            return false;
        }
    }

    /// <summary>
    /// Equip an item in an accessory slot ("rir", "ril", "wrs", "nek"), as <see cref="SetGlasses"/>. The slot must
    /// match the one the shell was published for: the game only loads the one it asked for.
    /// </summary>
    public bool SetAccessory(ulong itemId, string slotName)
    {
        if (!IsAvailable) return false;
        // Nothing to equip on without a drawn player, and no reason to pay the IPC to be told so.
        if (objectTable.LocalPlayer == null) return false;
        var slot = slotName switch
        {
            "ril" => ApiEquipSlot.LFinger,
            "rir" => ApiEquipSlot.RFinger,
            "wrs" => ApiEquipSlot.Wrists,
            "nek" => ApiEquipSlot.Neck,
            _     => ApiEquipSlot.Unknown,
        };
        if (slot == ApiEquipSlot.Unknown)
        {
            log.Warning("[Proteus] SetAccessory: unknown slot \"{0}\"", slotName);
            return false;
        }
        try
        {
            // Two explicit zero stains, not an empty list (see NoStains).
            var ec = setItem.Invoke(0, slot, itemId, NoStains, key: 0, ApplyFlag.Once);
            if (ec != GlamourerApiEc.Success)
            {
                if (ec == GlamourerApiEc.InvalidKey)
                    log.Warning("[Proteus] SetItem({0},{1}) refused: another plugin holds a Glamourer lock on the {0} slot.", slot, itemId);
                else
                    log.Debug("[Proteus] SetItem({0},{1}) -> {2}", slot, itemId, ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // The exception object, not ex.Message: a TargetInvocationException's message is a constant.
            log.Warning(ex, "[Proteus] SetItem({0},{1}) failed", slot, itemId);
            return false;
        }
    }

    /// <summary>
    /// Glamourer finished an operation on the local player, once per operation; drives design binding
    /// (<see cref="DesignBindingService.IsApplySignal(StateFinalizationType)"/>).
    /// </summary>
    private void OnStateFinalized(nint address, StateFinalizationType type)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != address) return;

        // Swallow the Reapply echo of our own ReapplyPlayerState; only Reapply, so a real design apply in the
        // same window is still delivered.
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
        if (Environment.TickCount64 < Interlocked.Read(ref ownEchoUntil)) return true;
        var since = unchecked(Environment.TickCount64 - Interlocked.Read(ref lastOwnReapplyTick));
        return since >= 0 && since < OwnReapplyEchoMs;
    }

    /// <summary>Until this tick, a reapply of the local player is one Proteus caused without calling it itself.</summary>
    private long ownEchoUntil;

    /// <summary>
    /// Glamourer will reapply the local player on its own because of a Penumbra temporary mod change Proteus made;
    /// treat its signals as our own echo until <paramref name="untilTick"/>.
    /// </summary>
    public void ExpectOwnReapplyUntil(long untilTick) => Interlocked.Exchange(ref ownEchoUntil, untilTick);

    /// <summary>
    /// How long ago someone other than Proteus had Glamourer reapply the local player, or <see cref="long.MaxValue"/>.
    /// Half of <see cref="DesignBindingService.IsInferredAutomationApply"/>; meaningless on its own.
    /// </summary>
    public long MsSinceForeignReapply
    {
        get
        {
            var stamp = Interlocked.Read(ref lastForeignReapplyTick);
            if (stamp == 0) return long.MaxValue;                  // never seen one
            var since = unchecked(Environment.TickCount64 - stamp);
            return since < 0 ? long.MaxValue : since;              // clock went backwards: treat as never
        }
    }

    private void OnStateChanged(nint address, StateChangeType changeType)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != address) return;

        log.Debug("[Proteus] glamourer signal: changeType={0}", changeType);

        // Stamp before the filtering below: a Reapply we did not cause is the first half of the automation-apply
        // signature (DesignBindingService.IsInferredAutomationApply).
        if (changeType is StateChangeType.Reapply && !WithinOwnReapplyEcho())
            Interlocked.Exchange(ref lastForeignReapplyTick, Environment.TickCount64);

        // Every change, before the echo suppression below: design binding must hear all of them, ours included.
        LocalPlayerStateChangedAny?.Invoke(changeType);

        // Model/EntireCustomize can change race/body without touching mod settings.
        if (changeType is StateChangeType.Model or StateChangeType.EntireCustomize)
        {
            LocalPlayerCustomizationChanged?.Invoke();
            return;
        }

        // An equipped or bonus item moved: BonusItem matters too, since a shell can be hosted on the facewear slot.
        if (changeType is StateChangeType.Equip or StateChangeType.BonusItem)
        {
            // Our own ReapplyState raises an Equip per slot, and each one schedules another composite
            // that reapplies again. Cost: a foreign equip inside the window is dropped until the next trigger.
            if (WithinOwnReapplyEcho()) return;

            LocalPlayerEquipmentChanged?.Invoke();
            return;
        }

        // A single customize value changed in place (a hairstyle change arrives only this way).
        if (changeType is StateChangeType.Customize)
        {
            LocalPlayerCustomizeChanged?.Invoke();
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
