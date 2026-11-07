using System;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Penumbra's mod-setting events, narrowed to the ones that can change what the player wears: Gear variants and Gear
/// shaders used to run on every change in any collection, and sync plugins change other players' temporary collections
/// all the time.
/// <list type="bullet">
/// <item>Proteus's own managed mod is never a reason (every composite edits it).</item>
/// <item><see cref="ModSettingChange.Edited"/> passes from any collection: it is the mod's files that changed.</item>
/// <item>Anything else passes only for the player's collection, read live each time as the compositor's own filter does;
/// when that cannot be read it passes, to be safe.</item>
/// </list>
/// Handlers run on Penumbra's caller thread; the filter only makes the one collection IPC call.
/// </summary>
public sealed class PlayerSettingsFilter : IDisposable
{
    private readonly Func<Guid?> playerCollection;
    private PenumbraBridge? bridge;

    /// <summary>A setting event that passed the filter: its kind and mod folder.</summary>
    public event Action<ModSettingChange, string>? Passed;

    internal PlayerSettingsFilter(Func<Guid?> playerCollection) => this.playerCollection = playerCollection;

    public static PlayerSettingsFilter Attach(PenumbraBridge penumbra)
    {
        var filter = new PlayerSettingsFilter(penumbra.GetPlayerCollectionId) { bridge = penumbra };
        penumbra.ModSettingChanged += filter.OnModSettingChanged;
        return filter;
    }

    internal static bool Passes(ModSettingChange change, Guid collection, Guid? player, string? modDir)
    {
        if (string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)) return false;
        if (change == ModSettingChange.Edited) return true;
        return player == null || collection == player.Value;
    }

    internal void OnModSettingChanged(ModSettingChange change, Guid collection, string modDir, bool inherited)
    {
        // Ask for the collection only when the answer matters.
        bool needsPlayer = change != ModSettingChange.Edited
            && !string.Equals(modDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase);
        if (Passes(change, collection, needsPlayer ? playerCollection() : null, modDir))
            Passed?.Invoke(change, modDir);
    }

    public void Dispose()
    {
        if (bridge != null) bridge.ModSettingChanged -= OnModSettingChanged;
        bridge = null;
    }
}
