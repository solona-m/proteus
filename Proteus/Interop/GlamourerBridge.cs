using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;

namespace Proteus.Interop;

public class GlamourerBridge : IDisposable
{
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly EventSubscriber<nint, StateChangeType>? stateChangedSub;

    public bool IsAvailable { get; private set; }

    /// <summary>Fired when Glamourer applies a design, resets, or reapplies state on the local player.</summary>
    public event Action? LocalPlayerStateChanged;

    public GlamourerBridge(IDalamudPluginInterface pluginInterface, IObjectTable objectTable, IPluginLog log)
    {
        this.log         = log;
        this.objectTable = objectTable;

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

    private void OnStateChanged(nint address, StateChangeType changeType)
    {
        // Only care about state-wide changes that can affect which mods are active.
        // Individual tweaks (Customize, Equip, Stains, etc.) don't touch mod settings.
        if (changeType is not (StateChangeType.Design or StateChangeType.Reset or StateChangeType.Reapply))
            return;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != address) return;

        LocalPlayerStateChanged?.Invoke();
    }

    public void Dispose()
    {
        stateChangedSub?.Dispose();
    }
}
