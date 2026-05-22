using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace Proteus.Interop;

public class PenumbraBridge : IDisposable
{
    private readonly IPluginLog log;
    private readonly IDalamudPluginInterface pluginInterface;

    private readonly ApiVersion apiVersion;
    private readonly GetModList getModList;
    private readonly GetModDirectory getModDirectory;
    private readonly GetCollectionForObject getCollectionForObject;
    private readonly GetCurrentModSettingsWithTemp getCurrentModSettings;
    private readonly ResolvePlayerPath resolvePlayerPath;
    private readonly AddMod addMod;
    private readonly ReloadMod reloadMod;
    private readonly TrySetMod trySetMod;
    private readonly TrySetModPriority trySetModPriority;
    private readonly RedrawObject redrawObject;
    private readonly OpenMainWindow openMainWindow;

    private readonly EventSubscriber<ModSettingChange, Guid, string, bool> modSettingChangedSub;
    private readonly EventSubscriber<string> modAddedSub;
    private readonly EventSubscriber<string> modDeletedSub;
    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;

    public bool IsAvailable { get; private set; }

    public event Action<ModSettingChange, Guid, string, bool>? ModSettingChanged;
    public event Action<string>? ModAdded;
    public event Action<string>? ModDeleted;
    /// <summary>Fired when Penumbra becomes available (including late initialization after plugin load).</summary>
    public event Action? PenumbraReady;

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.pluginInterface = pluginInterface;

        apiVersion = new ApiVersion(pluginInterface);
        getModList = new GetModList(pluginInterface);
        getModDirectory = new GetModDirectory(pluginInterface);
        getCollectionForObject = new GetCollectionForObject(pluginInterface);
        getCurrentModSettings = new GetCurrentModSettingsWithTemp(pluginInterface);
        resolvePlayerPath = new ResolvePlayerPath(pluginInterface);
        addMod = new AddMod(pluginInterface);
        reloadMod = new ReloadMod(pluginInterface);
        trySetMod = new TrySetMod(pluginInterface);
        trySetModPriority = new TrySetModPriority(pluginInterface);
        redrawObject = new RedrawObject(pluginInterface);
        openMainWindow = new OpenMainWindow(pluginInterface);

        modSettingChangedSub = Penumbra.Api.IpcSubscribers.ModSettingChanged.Subscriber(pluginInterface,
            (change, collId, modDir, inherited) => ModSettingChanged?.Invoke(change, collId, modDir, inherited));
        modAddedSub = Penumbra.Api.IpcSubscribers.ModAdded.Subscriber(pluginInterface,
            modDir => ModAdded?.Invoke(modDir));
        modDeletedSub = Penumbra.Api.IpcSubscribers.ModDeleted.Subscriber(pluginInterface,
            modDir => ModDeleted?.Invoke(modDir));
        initializedSub = Penumbra.Api.IpcSubscribers.Initialized.Subscriber(pluginInterface, OnPenumbraInitialized);
        disposedSub    = Penumbra.Api.IpcSubscribers.Disposed.Subscriber(pluginInterface, OnPenumbraDisposed);

        CheckAvailability();
    }

    private void OnPenumbraInitialized()
    {
        CheckAvailability();
        if (IsAvailable)
            PenumbraReady?.Invoke();
    }

    private void OnPenumbraDisposed()
    {
        IsAvailable = false;
    }

    private void CheckAvailability()
    {
        try
        {
            var version = apiVersion.Invoke();
            IsAvailable = true;
            log.Information("Penumbra IPC available (v{0}.{1}).", version.Breaking, version.Features);
        }
        catch
        {
            IsAvailable = false;
            log.Warning("Penumbra IPC not available.");
        }
    }

    /// <summary>Returns all mods known to Penumbra as modDirectory → modName.</summary>
    public Dictionary<string, string>? GetAllMods()
    {
        if (!IsAvailable) return null;
        try { return getModList.Invoke(); }
        catch (Exception ex) { log.Error(ex, "GetModList failed"); return null; }
    }

    public string? GetModDirectory()
    {
        if (!IsAvailable) return null;
        try
        {
            var dir = getModDirectory.Invoke();
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception ex) { log.Error(ex, "GetModDirectory failed"); return null; }
    }

    /// <summary>Returns the effective collection GUID for the local player (object index 0).</summary>
    public Guid? GetPlayerCollectionId()
    {
        if (!IsAvailable) return null;
        try
        {
            var result = getCollectionForObject.Invoke(0);
            if (!result.ObjectValid) return null;
            return result.EffectiveCollection.Id;
        }
        catch (Exception ex) { log.Error(ex, "GetCollectionForObject failed"); return null; }
    }

    /// <summary>
    /// Returns (enabled, priority, optionSelections) for a mod in the player's effective collection,
    /// or null if the mod is not found / Penumbra unavailable.
    /// optionSelections: groupName → list of selected option names.
    /// </summary>
    public (bool Enabled, int Priority, Dictionary<string, List<string>> Options)? GetModSettings(
        Guid collectionId, string modDirectory)
    {
        if (!IsAvailable) return null;
        try
        {
            var (ec, t) = getCurrentModSettings.Invoke(collectionId, modDirectory);
            if (ec != PenumbraApiEc.Success || t == null) return null;
            var (enabled, priority, options, _, _) = t.Value;
            return (enabled, priority, options);
        }
        catch (Exception ex) { log.Error(ex, "GetCurrentModSettingsWithTemp failed for {0}", modDirectory); return null; }
    }

    /// <summary>Resolve a game path to the player's current on-disk file (respects all active mods).</summary>
    public string? ResolvePlayer(string gamePath)
    {
        if (!IsAvailable) return null;
        try
        {
            var resolved = resolvePlayerPath.Invoke(gamePath);
            return string.IsNullOrEmpty(resolved) ? null : resolved;
        }
        catch (Exception ex) { log.Error(ex, "ResolvePlayerPath failed for {0}", gamePath); return null; }
    }

    /// <summary>Register a new mod directory with Penumbra.</summary>
    public PenumbraApiEc AddModDirectory(string modDirectory)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try { return addMod.Invoke(modDirectory); }
        catch (Exception ex) { log.Error(ex, "AddMod failed"); return PenumbraApiEc.UnknownError; }
    }

    /// <summary>Tell Penumbra to reload a mod from disk.</summary>
    public PenumbraApiEc ReloadModDirectory(string modDirectory)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try { return reloadMod.Invoke(modDirectory); }
        catch (Exception ex) { log.Error(ex, "ReloadMod failed"); return PenumbraApiEc.UnknownError; }
    }

    /// <summary>Enable or disable a mod in a collection.</summary>
    public PenumbraApiEc SetModEnabled(Guid collectionId, string modDirectory, bool enabled)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try { return trySetMod.Invoke(collectionId, modDirectory, enabled); }
        catch (Exception ex) { log.Error(ex, "TrySetMod failed"); return PenumbraApiEc.UnknownError; }
    }

    /// <summary>Set a mod's priority in a collection.</summary>
    public PenumbraApiEc SetModPriority(Guid collectionId, string modDirectory, int priority)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try { return trySetModPriority.Invoke(collectionId, modDirectory, priority); }
        catch (Exception ex) { log.Error(ex, "TrySetModPriority failed"); return PenumbraApiEc.UnknownError; }
    }

    public void RedrawPlayer()
    {
        if (!IsAvailable) return;
        try { redrawObject.Invoke(0, RedrawType.Redraw); }
        catch (Exception ex) { log.Error(ex, "RedrawObject failed"); }
    }

    public void OpenToMod(string modDirectory)
    {
        if (!IsAvailable) return;
        try { openMainWindow.Invoke(TabType.Mods, modDirectory); }
        catch (Exception ex) { log.Error(ex, "OpenMainWindow failed"); }
    }

    public void Dispose()
    {
        modSettingChangedSub.Dispose();
        modAddedSub.Dispose();
        modDeletedSub.Dispose();
        initializedSub.Dispose();
        disposedSub.Dispose();
    }
}
