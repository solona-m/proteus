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

    // When someone OTHER than us last had Glamourer reapply state on the local player. Automation applying
    // a design on a gearset or job change is invisible in every other way — Glamourer raises no
    // Design/DesignApplied signal for it (the actor set is empty for StateSource.Fixed) and the follow-up
    // uses ReapplyState rather than ReapplyAutomationState — so this Reapply, paired with the Gearset
    // finalization that follows it, is the only evidence the apply happened. See
    // DesignBindingService.IsInferredAutomationApply.
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

    /// <summary>
    /// Fired when Glamourer changes an equipped item or bonus item (glasses) on the local player.
    /// <para/>
    /// Exists because an equipment change had NO route to the compositor. It changes no mod settings, so
    /// none of the OnModSettingChanged triggers fire; it is not Design/Reset/Reapply, so it was filtered
    /// out below; and when Glamourer applies it without a redraw, the redraw hook's equipment-change
    /// trigger never runs either. Measured: an equipment-only design apply produced a Design signal, a
    /// DesignApplied finalization, and not one recomposite — leaving the second-skin shell cut for the
    /// PREVIOUS outfit until something unrelated happened to trigger a composite.
    /// <para/>
    /// Consumers should treat this as AMBIENT: it fires per changed slot, so a design apply produces a
    /// burst, and the unchanged-inputs gate is what makes that cheap. It deliberately does not carry which
    /// slot changed — the compositor re-walks the draw object anyway, and the walk is the authority.
    /// </summary>
    public event Action? LocalPlayerEquipmentChanged;

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

    /// <summary>
    /// The user's <c>GlamourerDesignDirOverride</c>, when set. Assigned once at startup by the plugin,
    /// which owns the configuration.
    /// </summary>
    public string? DesignsDirectoryOverride { get; set; }

    /// <summary>
    /// Where design files actually live: the override if the user set one, else the derived path.
    /// <para/>
    /// One property so every consumer agrees. The design WATCHER already honoured the override while
    /// <see cref="ReadDesignFile"/> did not, which would have pointed the two at different folders — the
    /// fallback then finds nothing for exactly the users who most need it, and reports a second failure
    /// on top of the first.
    /// </summary>
    public string? EffectiveDesignsDirectory
        => string.IsNullOrWhiteSpace(DesignsDirectoryOverride) ? DesignsDirectory : DesignsDirectoryOverride;

    /// <summary>Glamourer's design list (GUID → display name); empty on failure.</summary>
    public Dictionary<Guid, string> GetDesigns()
    {
        try { return getDesignList.Invoke() ?? new(); }
        // The exception OBJECT, not ex.Message — see SetItem below for why: an IPC failure arrives wrapped
        // in a TargetInvocationException whose message is a constant, and the cause is the inner one.
        catch (Exception ex) { log.Warning(ex, "[Proteus] GetDesignList failed"); return new(); }
    }

    /// <summary>Whether the stack trace for a GetDesignJObject failure has been logged this session.</summary>
    private int loggedDesignIpcFailure;

    /// <summary>
    /// The serialized data for a single design (includes equipment + apply flags), or null on failure.
    /// <para/>
    /// Falls back to the design's own file on disk, because the IPC is not reliable for every design:
    /// Glamourer's serializer emits JSON it then fails to re-parse for certain designs — the observed
    /// failure is <c>JsonReaderException: ':' is invalid after a value</c> raised inside
    /// <c>SerializeToElement</c>, several thousand bytes into the output, for 7 of one user's designs
    /// while the rest serialize fine. That is a Glamourer bug and nothing here can fix it; what it costs
    /// Proteus is the ability to identify which design is applied, so those designs' bindings silently
    /// never restore.
    /// <para/>
    /// The file is the same data by a different road, and Proteus already knows where it lives (see
    /// <see cref="DesignsDirectory"/>, which GlamourerDesignWatcher watches). Its storage format carries
    /// the Equipment / Bonus / Customize / Parameters sections that DesignBindingService.StateMatches
    /// reads, in the same shape.
    /// </summary>
    public JObject? GetDesign(Guid id)
    {
        try { return getDesignJObject.Invoke(id); }
        catch (Exception ex)
        {
            // The stack ONCE per session, then a one-liner. It is the same Glamourer defect every time and
            // it fires for every affected design on every boot restore — seven full traces per attempt
            // buries the rest of the log without adding anything after the first.
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
            log.Warning(ex, "[Proteus] ReapplyState failed");   // see SetRing on logging the object
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
        if (objectTable.LocalPlayer == null) return false;   // see SetRing
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
            log.Warning(ex, "[Proteus] SetBonusItem(Glasses,{0}) failed", itemId);   // see SetRing
            return false;
        }
    }

    /// <summary>
    /// Both dye channels set to "no dye". A <see cref="List{T}"/>, and deliberately NOT a <c>byte[]</c>:
    /// Dalamud round-trips IPC arguments through Newtonsoft when the runtime type isn't the declared
    /// parameter type, and Newtonsoft serialises a byte ARRAY as a base64 string. The receiving side then
    /// tries to turn <c>"AAA="</c> into an <c>IReadOnlyList&lt;byte&gt;</c> and throws IpcTypeMismatchError.
    /// An empty byte[] is worse, not better — it serialises to <c>""</c>, which deserialises to NULL and
    /// crashes Glamourer inside StainIds' constructor instead of failing cleanly.
    /// <para/>
    /// A List&lt;byte&gt; serialises as a JSON array (<c>[0,0]</c>) and survives the trip intact. Anything
    /// passed to an <c>IReadOnlyList&lt;byte&gt;</c> IPC parameter has to avoid byte[] for this reason.
    /// </summary>
    private static readonly List<byte> NoStains = [0, 0];

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
        => SetAccessory(itemId, leftHand ? "ril" : "rir");

    /// <summary>
    /// As <see cref="SetRing"/>, for any accessory slot a carrier can ride: "rir", "ril", "wrs", "nek".
    /// Same contract in every respect — the slot must match the one the shell was published for, because its
    /// path and EQDP entry name that slot (c….a0053_wrs.mdl) and the game only loads the one it asked for.
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
            // Two explicit zero stains, NOT an empty list. Semantically identical — an invisible ring has
            // nothing to dye, and passing the player's own would be a change we have no business making —
            // but `[]` reached Glamourer as a NULL and crashed it in StainIds' constructor on stains.Count,
            // which surfaced here as an opaque TargetInvocationException on every equip and unequip. Glamourer
            // never passes an empty list either; its own legacy provider forwards a one-element array.
            var ec = setItem.Invoke(0, slot, itemId, NoStains, key: 0, ApplyFlag.Once);
            if (ec != GlamourerApiEc.Success)
            {
                log.Debug("[Proteus] SetItem({0},{1}) -> {2}", slot, itemId, ec);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // The exception OBJECT, not ex.Message: this arrives as a TargetInvocationException whose own
            // message is the useless constant "Exception has been thrown by the target of an invocation."
            // The cause is the inner exception, and logging only the message threw it away — which is why a
            // field report of this told us nothing beyond the fact that it happened.
            log.Warning(ex, "[Proteus] SetItem({0},{1}) failed", slot, itemId);
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

    /// <summary>
    /// How long ago someone other than Proteus had Glamourer reapply the local player's state, or
    /// <see cref="long.MaxValue"/> if that has never happened this session. Half of the automation-apply
    /// inference (<see cref="DesignBindingService.IsInferredAutomationApply"/>); on its own it means very
    /// little, since a plain <c>/glamour reapply</c> lands here too.
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

        // Debug-level: this no longer drives design binding (the finalized= signal does), but keeping it
        // paired in the log makes it obvious which signals an action produced.
        log.Debug("[Proteus] glamourer signal: changeType={0}", changeType);

        // Stamp before the filtering below: a Reapply we did NOT cause is the first half of the
        // automation-apply signature. Our own IPC reapply is excluded by the same echo window that
        // suppresses its finalization — what that window cannot exclude is the reapply Glamourer performs
        // after a Penumbra redraw (including ours), which is why the consumer also discounts our own
        // composites. See DesignBindingService.IsInferredAutomationApply.
        if (changeType is StateChangeType.Reapply && !WithinOwnReapplyEcho())
            Interlocked.Exchange(ref lastForeignReapplyTick, Environment.TickCount64);

        // Model/EntireCustomize can change race/body without touching mod settings.
        // Fire the customization event so the compositor recomposites unconditionally.
        if (changeType is StateChangeType.Model or StateChangeType.EntireCustomize)
        {
            LocalPlayerCustomizationChanged?.Invoke();
            return;
        }

        // An equipped item or a bonus item (glasses) moved. Neither changes the mod set, so this is the
        // only signal that reaches the compositor — see LocalPlayerEquipmentChanged. BonusItem matters as
        // much as Equip: the gear shell is hosted on the facewear slot, so glasses coming off take the
        // shell's host with them.
        if (changeType is StateChangeType.Equip or StateChangeType.BonusItem)
        {
            LocalPlayerEquipmentChanged?.Invoke();
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
