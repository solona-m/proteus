using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Proteus.Interop;

namespace Proteus.Services;

public record OverlayEntry(
    string ModDirectory,
    string ModName,
    int Priority,
    ProteusMetadata Metadata,
    string SidecarRoot   // absolute path to the Proteus/ subfolder
);

public class SidecarDiscoveryService
{
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;

    private const string SidecarSubdir = "Proteus";
    private const string MetadataFile  = "metadata.json";
    public  const string ManagedModDir = "Proteus";  // directory name of the managed output mod

    public SidecarDiscoveryService(PenumbraBridge penumbra, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.log = log;
    }

    /// <summary>
    /// Discover all enabled Penumbra mods that contain a Proteus/ sidecar.
    /// Returns them ordered by priority ascending (lowest priority = bottom of composite stack).
    /// The managed Proteus mod itself is excluded.
    /// </summary>
    public List<OverlayEntry> DiscoverEnabled()
    {
        var modsRoot = penumbra.GetModDirectory();
        if (modsRoot == null) return [];

        var allMods = penumbra.GetAllMods();
        if (allMods == null) return [];

        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null) return [];

        var results = new List<OverlayEntry>();

        foreach (var (modDir, modName) in allMods)
        {
            if (string.Equals(modDir, ManagedModDir, StringComparison.OrdinalIgnoreCase))
                continue;

            var settings = penumbra.GetModSettings(collId.Value, modDir);
            if (settings == null || !settings.Value.Enabled) continue;

            var sidecarDir = Path.Combine(modsRoot, modDir, SidecarSubdir);
            var metaPath   = Path.Combine(sidecarDir, MetadataFile);
            if (!File.Exists(metaPath)) continue;

            var metadata = TryParseMetadata(metaPath);
            if (metadata == null) continue;

            results.Add(new OverlayEntry(modDir, modName, settings.Value.Priority, metadata, sidecarDir));
        }

        results.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return results;
    }

    /// <summary>
    /// Resolve the list of active OverlayDescriptors for an entry.
    /// Simple mods: use Overlays directly.
    /// Option-group mods: look up the currently selected option per group and use its overlays.
    /// </summary>
    public List<OverlayDescriptor> ResolveActiveOverlays(OverlayEntry entry)
    {
        if (entry.Metadata.Overlays is { Count: > 0 })
            return entry.Metadata.Overlays;

        if (entry.Metadata.OptionGroups == null) return [];

        var collId = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue
            ? penumbra.GetModSettings(collId.Value, entry.ModDirectory)
            : null;

        var result = new List<OverlayDescriptor>();

        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;

            // Penumbra returns selected option names as a list per group name.
            List<string>? selected = null;
            settings?.Options.TryGetValue(group.PenumbraGroupName, out selected);

            // Default to first option if nothing selected.
            var selectedName = selected?.Count > 0 ? selected[0] : null;
            var option = selectedName != null
                ? group.Options.FirstOrDefault(o => string.Equals(o.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                : null;
            option ??= group.Options[0];

            result.AddRange(option.Overlays);
        }

        return result;
    }

    private ProteusMetadata? TryParseMetadata(string metaPath)
    {
        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<ProteusMetadata>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to parse Proteus metadata: {0}", metaPath);
            return null;
        }
    }
}
