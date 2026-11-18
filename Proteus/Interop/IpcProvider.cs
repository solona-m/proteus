// SPDX-License-Identifier: LicenseRef-Proteus
// Copyright (c) Solona
// Written from docs/contrib-clean-room-spec.md (§2, §3, §9.2, §12) as the clean side of a clean-room rewrite.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Interop;

/// <summary>
/// Proteus's public IPC for other plugins: which overlay mods it knows about, which of their options are live, a way
/// to ask for a recomposite, and read/write access to a mod's colour-table rows (the top-level table or one option's).
/// Every call answers on the calling plugin's thread, with no marshalling and no locks.
/// </summary>
public class IpcProvider : IDisposable
{
    /// <summary>Every label starts with the plugin's name and a dot.</summary>
    private const string Prefix = "Proteus.";

    private const string RecompositeReason = "ipc-request";
    private const string ColorTableReason  = "ipc-color-table";

    private readonly Func<List<OverlayEntry>> lastDiscovered;
    private readonly Func<OverlayEntry, List<ResolvedOverlay>> resolveActiveOverlays;
    private readonly Func<OverlayEntry, bool> saveMetadata;
    private readonly Action<string> triggerRecomposite;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly ICallGateProvider<(int, int)> apiVersion;
    private readonly ICallGateProvider<List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)>> getOverlays;
    private readonly ICallGateProvider<List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)>> getActiveOverlays;
    private readonly ICallGateProvider<object> recomposite;
    private readonly ICallGateProvider<string, string?, string?, string> getColorTable;
    private readonly ICallGateProvider<string, string?, string?, string, bool> setColorTable;

    public IpcProvider(IDalamudPluginInterface pluginInterface, CompositorService compositor,
                       SidecarDiscoveryService discovery, Configuration config, IPluginLog log)
        // Deferred: each member is reached only when a call needs it, so a missing dependency fails at call time.
        : this(pluginInterface,
               () => compositor.LastDiscovered,
               entry => discovery.ResolveActiveOverlays(entry),
               entry => discovery.SaveMetadata(entry),
               reason => compositor.TriggerRecomposite(reason),
               config, log)
    {
    }

    /// <summary>
    /// The provider over plain delegates instead of the compositor and the discovery service, so a test can supply the
    /// entry list and watch the save and the recomposite.
    /// </summary>
    internal IpcProvider(IDalamudPluginInterface pluginInterface,
                         Func<List<OverlayEntry>> lastDiscovered,
                         Func<OverlayEntry, List<ResolvedOverlay>> resolveActiveOverlays,
                         Func<OverlayEntry, bool> saveMetadata,
                         Action<string> triggerRecomposite,
                         Configuration config, IPluginLog log)
    {
        this.lastDiscovered        = lastDiscovered;
        this.resolveActiveOverlays = resolveActiveOverlays;
        this.saveMetadata          = saveMetadata;
        this.triggerRecomposite    = triggerRecomposite;
        this.config                = config;
        this.log                   = log;

        apiVersion = pluginInterface.GetIpcProvider<(int, int)>(Prefix + "ApiVersion");
        apiVersion.RegisterFunc(ApiVersion);

        getOverlays = pluginInterface.GetIpcProvider<List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)>>(Prefix + "GetOverlays");
        getOverlays.RegisterFunc(GetOverlays);

        getActiveOverlays = pluginInterface.GetIpcProvider<List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)>>(Prefix + "GetActiveOverlays");
        getActiveOverlays.RegisterFunc(GetActiveOverlays);

        recomposite = pluginInterface.GetIpcProvider<object>(Prefix + "Recomposite");
        recomposite.RegisterAction(Recomposite);

        getColorTable = pluginInterface.GetIpcProvider<string, string?, string?, string>(Prefix + "GetColorTable");
        getColorTable.RegisterFunc(GetColorTable);

        setColorTable = pluginInterface.GetIpcProvider<string, string?, string?, string, bool>(Prefix + "SetColorTable");
        setColorTable.RegisterFunc(SetColorTable);
    }

    private static (int, int) ApiVersion() => (1, 0);

    /// <summary>Every enabled overlay mod, each with all of its option groups' option names (selected or not).</summary>
    private List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)> GetOverlays()
    {
        var entries = lastDiscovered();
        var list = new List<(string, string, int, Dictionary<string, List<string>>?)>();
        foreach (var entry in entries)
        {
            if (!entry.Enabled) continue;
            Dictionary<string, List<string>>? options = null;
            if (HasOptionGroups(entry.Metadata))
            {
                options = new Dictionary<string, List<string>>();
                foreach (var group in entry.Metadata.OptionGroups!)
                    // Add, not the indexer: a duplicate (or null) group name is the caller's to hear about.
                    options.Add(group.PenumbraGroupName, group.Options.Select(o => o.Name).ToList());
            }
            list.Add((entry.ModDirectory, entry.ModName, entry.Priority, options));
        }
        return list;
    }

    /// <summary>
    /// Every enabled overlay mod with the options that currently resolve, one name per resolved overlay descriptor.
    /// Ungrouped overlays come under the empty-string key with an empty-string option.
    /// </summary>
    private List<(string ModDirectory, string Name, int Priority, Dictionary<string, List<string>>? Options)> GetActiveOverlays()
    {
        var entries = lastDiscovered();
        var list = new List<(string, string, int, Dictionary<string, List<string>>?)>();
        foreach (var entry in entries)
        {
            if (!entry.Enabled) continue;
            Dictionary<string, List<string>>? options = null;
            if (HasOptionGroups(entry.Metadata))
            {
                options = new Dictionary<string, List<string>>();
                foreach (var resolved in resolveActiveOverlays(entry))
                {
                    var key = resolved.OptionGroup ?? string.Empty;
                    if (!options.TryGetValue(key, out var names))
                        options[key] = names = [];
                    names.Add(resolved.Option ?? string.Empty);
                }
            }
            list.Add((entry.ModDirectory, entry.ModName, entry.Priority, options));
        }
        return list;
    }

    /// <summary>Ask for a recomposite (default delay, forced). Not gated on the master switch.</summary>
    private void Recomposite() => triggerRecomposite(RecompositeReason);

    /// <summary>
    /// A mod's colour rows as JSON: one option's when <paramref name="group"/> is given, else the top-level table. The
    /// empty string for anything not found or not set, and for any failure. Reads only.
    /// </summary>
    private string GetColorTable(string modDirectory, string? group, string? option)
    {
        try
        {
            var entry = FindEntry(modDirectory);
            if (entry == null) return string.Empty;

            List<ColorTableRowPreset>? rows;
            if (group != null)
            {
                if (option == null || !HasOptionGroups(entry.Metadata)) return string.Empty;
                var match = FindOption(entry.Metadata, group, option);
                if (match == null) return string.Empty;
                rows = match.ColorTableRows;   // no fallback to the top-level rows here
            }
            else
                rows = entry.Metadata.ColorTableRows;

            return rows == null ? string.Empty : JsonSerializer.Serialize(rows);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] IPC GetColorTable failed for {0}", modDirectory ?? "(null)");
            return string.Empty;
        }
    }

    /// <summary>
    /// Replace a mod's colour rows (one option's, or the top-level table) from JSON; empty, whitespace or a JSON null
    /// clears them. Saves the sidecar and recomposites. Refused while Proteus is switched off.
    /// </summary>
    private bool SetColorTable(string modDirectory, string? group, string? option, string colorTableJson)
    {
        if (!config.PluginEnabled) return false;
        try
        {
            var entry = FindEntry(modDirectory);
            if (entry == null) return false;

            // Parsed before any group check: bad JSON is refused through the guard whatever else is wrong.
            List<ColorTableRowPreset>? rows = string.IsNullOrWhiteSpace(colorTableJson)
                ? null
                : JsonSerializer.Deserialize<List<ColorTableRowPreset>>(colorTableJson);

            if (group != null)
            {
                if (option == null || !HasOptionGroups(entry.Metadata)) return false;
                var match = FindOption(entry.Metadata, group, option);
                if (match == null) return false;
                match.ColorTableRows = rows;
            }
            else
                entry.Metadata.ColorTableRows = rows;

            _ = saveMetadata(entry);
            triggerRecomposite(ColorTableReason);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] IPC SetColorTable failed for {0}", modDirectory ?? "(null)");
            return false;
        }
    }

    private static bool HasOptionGroups(ProteusMetadata metadata) => metadata.OptionGroups is { Count: > 0 };

    /// <summary>The first discovered entry (enabled or not) whose folder matches, ignoring case; never for a null folder.</summary>
    private OverlayEntry? FindEntry(string? modDirectory)
    {
        var entries = lastDiscovered();
        if (modDirectory == null) return null;
        return entries.FirstOrDefault(e =>
            string.Equals(e.ModDirectory, modDirectory, StringComparison.InvariantCultureIgnoreCase));
    }

    /// <summary>The first group, then the first option in it, matching by name, ignoring case.</summary>
    private static OverlayOption? FindOption(ProteusMetadata metadata, string group, string option)
    {
        var g = metadata.OptionGroups!.FirstOrDefault(x =>
            string.Equals(x.PenumbraGroupName, group, StringComparison.InvariantCultureIgnoreCase));
        return g?.Options.FirstOrDefault(o =>
            string.Equals(o.Name, option, StringComparison.InvariantCultureIgnoreCase));
    }

    public void Dispose()
    {
        apiVersion.UnregisterFunc();
        getOverlays.UnregisterFunc();
        getActiveOverlays.UnregisterFunc();
        recomposite.UnregisterAction();
        getColorTable.UnregisterFunc();
        setColorTable.UnregisterFunc();
    }
}
