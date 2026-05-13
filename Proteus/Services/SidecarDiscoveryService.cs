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

/// <summary>
/// A single overlay descriptor paired with the color table rows that apply to it.
/// ColorTableRows comes from the option that owns the descriptor (if any), falling
/// back to the top-level metadata ColorTableRows.
/// </summary>
public record ResolvedOverlay(
    OverlayDescriptor Descriptor,
    List<ColorTableRowPreset>? ColorTableRows
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
    /// Resolve the active overlays for an entry, paired with their applicable color table rows.
    /// Simple mods (top-level Overlays): all overlays are active, using top-level ColorTableRows.
    /// Option-group mods: all currently-selected options contribute their overlays, supporting
    /// both single-select and multi-select Penumbra groups. Each option's ColorTableRows overrides
    /// the top-level rows; falls back to top-level if the option has none.
    /// </summary>
    public List<ResolvedOverlay> ResolveActiveOverlays(OverlayEntry entry)
    {
        if (entry.Metadata.Overlays is { Count: > 0 })
            return entry.Metadata.Overlays
                .Select(d => new ResolvedOverlay(d, entry.Metadata.ColorTableRows))
                .ToList();

        if (entry.Metadata.OptionGroups == null) return [];

        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        var result = new List<ResolvedOverlay>();

        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;

            List<string>? selected = null;
            if (settings.HasValue)
                selected = settings.Value.Options
                    .FirstOrDefault(kv => string.Equals(kv.Key, group.PenumbraGroupName, StringComparison.OrdinalIgnoreCase))
                    .Value;

            log.Debug("[Proteus] ResolveActive: group={0} selected={1}",
                group.PenumbraGroupName,
                selected != null ? string.Join(",", selected) : "<default>");

            if (selected is { Count: > 0 })
            {
                // Include ALL selected options — handles both single-select and multi-select groups.
                foreach (var name in selected)
                {
                    var opt = group.Options.FirstOrDefault(o =>
                        string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (opt == null) continue;
                    var rows = opt.ColorTableRows ?? entry.Metadata.ColorTableRows;
                    foreach (var d in opt.Overlays)
                        result.Add(new ResolvedOverlay(d, rows));
                }
            }
            else
            {
                // Nothing selected — default to first option.
                var opt  = group.Options[0];
                var rows = opt.ColorTableRows ?? entry.Metadata.ColorTableRows;
                foreach (var d in opt.Overlays)
                    result.Add(new ResolvedOverlay(d, rows));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the ColorTableRows list that the editor should read/write for the currently
    /// active option. Creates an empty list in the right place if absent.
    /// Unconditional mods: top-level ColorTableRows.
    /// Option-group mods: the currently selected option's ColorTableRows.
    /// </summary>
    public List<ColorTableRowPreset> GetActiveColorRows(OverlayEntry entry)
    {
        if (entry.Metadata.Overlays is { Count: > 0 } || entry.Metadata.OptionGroups == null)
        {
            entry.Metadata.ColorTableRows ??= new List<ColorTableRowPreset>();
            return entry.Metadata.ColorTableRows;
        }

        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;
            List<string>? selected = null;
            settings?.Options.TryGetValue(group.PenumbraGroupName, out selected);

            OverlayOption? opt = null;
            if (selected is { Count: > 0 })
                opt = group.Options.FirstOrDefault(o =>
                    string.Equals(o.Name, selected[0], StringComparison.OrdinalIgnoreCase));
            opt ??= group.Options[0];

            opt.ColorTableRows ??= new List<ColorTableRowPreset>();
            return opt.ColorTableRows;
        }

        entry.Metadata.ColorTableRows ??= new List<ColorTableRowPreset>();
        return entry.Metadata.ColorTableRows;
    }

    public void SaveMetadata(OverlayEntry entry)
    {
        try
        {
            var path = Path.Combine(entry.SidecarRoot, MetadataFile);
            var json = JsonSerializer.Serialize(entry.Metadata,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to save Proteus metadata for {0}", entry.ModDirectory);
        }
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
