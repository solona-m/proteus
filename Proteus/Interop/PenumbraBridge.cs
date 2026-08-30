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
    private readonly TrySetModSetting trySetModSetting;
    private readonly TrySetModSettings trySetModSettings;
    private readonly RedrawObject redrawObject;
    private readonly OpenMainWindow openMainWindow;
    private readonly GetGameObjectResourcePaths getGameObjectResourcePaths;

    private readonly EventSubscriber<ModSettingChange, Guid, string, bool> modSettingChangedSub;
    private readonly EventSubscriber<string> modAddedSub;
    private readonly EventSubscriber<string> modDeletedSub;
    private readonly EventSubscriber<nint, int> gameObjectRedrawnSub;
    private readonly EventSubscriber initializedSub;
    private readonly EventSubscriber disposedSub;

    // Last collection GUID seen for the local player (object 0). Penumbra has no
    // "collection changed" event, so we detect reassignment on the player's redraw.
    private Guid? _lastPlayerCollection;

    public bool IsAvailable { get; private set; }

    public event Action<ModSettingChange, Guid, string, bool>? ModSettingChanged;
    public event Action<string>? ModAdded;
    public event Action<string>? ModDeleted;
    /// <summary>Fired when the collection assigned to the local player changes.</summary>
    public event Action? PlayerCollectionChanged;
    /// <summary>Fired on the framework thread each time the local player's draw object is redrawn.</summary>
    public event Action? LocalPlayerRedrawn;
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
        trySetModSetting = new TrySetModSetting(pluginInterface);
        trySetModSettings = new TrySetModSettings(pluginInterface);
        redrawObject = new RedrawObject(pluginInterface);
        openMainWindow = new OpenMainWindow(pluginInterface);
        getGameObjectResourcePaths = new GetGameObjectResourcePaths(pluginInterface);

        modSettingChangedSub = Penumbra.Api.IpcSubscribers.ModSettingChanged.Subscriber(pluginInterface,
            (change, collId, modDir, inherited) => ModSettingChanged?.Invoke(change, collId, modDir, inherited));
        modAddedSub = Penumbra.Api.IpcSubscribers.ModAdded.Subscriber(pluginInterface,
            modDir => ModAdded?.Invoke(modDir));
        modDeletedSub = Penumbra.Api.IpcSubscribers.ModDeleted.Subscriber(pluginInterface,
            modDir => ModDeleted?.Invoke(modDir));
        gameObjectRedrawnSub = Penumbra.Api.IpcSubscribers.GameObjectRedrawn.Subscriber(pluginInterface, OnGameObjectRedrawn);
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

    // Penumbra redraws a character when its assigned collection changes. There is no dedicated
    // collection-changed event, so on the local player's redraw we compare the effective
    // collection GUID against the last seen one and fire PlayerCollectionChanged when it differs.
    private void OnGameObjectRedrawn(nint _, int objectTableIndex)
    {
        if (objectTableIndex != 0) return; // local player only

        LocalPlayerRedrawn?.Invoke();

        var current = GetPlayerCollectionId();
        if (current == null) return;
        if (_lastPlayerCollection == current) return;

        bool first = _lastPlayerCollection == null;
        _lastPlayerCollection = current;
        if (!first) PlayerCollectionChanged?.Invoke();
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
        => GetPlayerCollection()?.Id;

    /// <summary>
    /// The local player's effective collection, id and display name. The name is only useful for logging
    /// and UI — everything else keys off the GUID, which is what Penumbra's API takes.
    /// </summary>
    public (Guid Id, string Name)? GetPlayerCollection()
    {
        if (!IsAvailable) return null;
        try
        {
            var result = getCollectionForObject.Invoke(0);
            if (!result.ObjectValid) return null;
            return result.EffectiveCollection;
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

    /// <summary>
    /// Returns the set of material game paths currently loaded by the local player's draw object,
    /// or null when unavailable or the player is not in game.
    /// </summary>
    public HashSet<string>? GetActivePlayerMaterialPaths()
    {
        if (!IsAvailable) return null;
        try
        {
            var results = getGameObjectResourcePaths.Invoke(0);
            var dict = results[0];
            if (dict == null) return null;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gamePaths in dict.Values)
                foreach (var p in gamePaths)
                    if (p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                        paths.Add(p);
            return paths.Count > 0 ? paths : null;
        }
        catch (Exception ex) { log.Warning(ex, "GetGameObjectResourcePaths failed; compositing all materials"); return null; }
    }

    /// <summary>
    /// The set of MODEL (.mdl) game paths currently loaded by the local player's draw object, or null
    /// when unavailable. The second skin uses this to find which gear model is drawn on each slot — the
    /// model loads reliably even when a gear piece's own materials fail or resolve to odd paths, so this
    /// is a sturdier signal than the material snapshot.
    /// </summary>
    public HashSet<string>? GetActivePlayerModelPaths()
    {
        if (!IsAvailable) return null;
        try
        {
            var results = getGameObjectResourcePaths.Invoke(0);
            var dict = results[0];
            if (dict == null) return null;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gamePaths in dict.Values)
                foreach (var p in gamePaths)
                    if (p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        paths.Add(p);
            return paths.Count > 0 ? paths : null;
        }
        catch (Exception ex) { log.Warning(ex, "GetGameObjectResourcePaths failed (models)"); return null; }
    }

    /// <summary>
    /// Both path sets from ONE GetGameObjectResourcePaths call.
    /// <para/>
    /// The two getters above make the same IPC call and differ only in which extension they keep, so asking
    /// for both separately pays it twice — and, worse, the two answers can straddle two frames. A caller
    /// deciding "has the draw object stopped changing" must compare readings of the SAME frame or it is
    /// diffing noise, so that caller needs this rather than the pair.
    /// <para/>
    /// Each set is null when empty, matching the getters above: a character that exists always draws
    /// something, so empty means the walk caught the draw object mid-teardown.
    /// </summary>
    public (HashSet<string>? Models, HashSet<string>? Materials) GetActivePlayerResourcePaths()
    {
        if (!IsAvailable) return (null, null);
        try
        {
            var results = getGameObjectResourcePaths.Invoke(0);
            var dict = results[0];
            if (dict == null) return (null, null);
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gamePaths in dict.Values)
                foreach (var p in gamePaths)
                {
                    if (p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) models.Add(p);
                    else if (p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) materials.Add(p);
                }
            return (models.Count > 0 ? models : null, materials.Count > 0 ? materials : null);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "GetGameObjectResourcePaths failed (combined)");
            return (null, null);
        }
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

    /// <summary>
    /// Set the selected option(s) for one of a mod's option groups in a collection.
    /// An empty list clears the selection. Uses the single-option API for exactly one selection.
    /// </summary>
    public PenumbraApiEc SetModOption(Guid collectionId, string modDirectory, string groupName, IReadOnlyList<string> options)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try
        {
            return options.Count == 1
                ? trySetModSetting.Invoke(collectionId, modDirectory, groupName, options[0])
                : trySetModSettings.Invoke(collectionId, modDirectory, groupName, options);
        }
        catch (Exception ex) { log.Error(ex, "TrySetModSetting(s) failed for {0}/{1}", modDirectory, groupName); return PenumbraApiEc.UnknownError; }
    }

    /// <summary>
    /// Ask Penumbra to redraw the local player. Returns false when the request never reached the game —
    /// Penumbra absent, or the IPC threw — which callers need in order to know whether to expect the
    /// redraw's downstream echoes (see <c>CompositorService.StampOwnRedraw</c>).
    /// </summary>
    public bool RedrawPlayer()
    {
        if (!IsAvailable) return false;
        try { redrawObject.Invoke(0, RedrawType.Redraw); return true; }
        catch (Exception ex) { log.Error(ex, "RedrawObject failed"); return false; }
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
        gameObjectRedrawnSub.Dispose();
        initializedSub.Dispose();
        disposedSub.Dispose();
    }
}
