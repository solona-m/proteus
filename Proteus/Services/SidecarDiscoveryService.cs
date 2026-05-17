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

            // Check for sidecar before calling GetModSettings: a local File.Exists costs ~0.1 ms
            // while a Penumbra IPC call costs ~2–5 ms per hop through the framework thread.
            // Users with 500+ enabled mods would otherwise spend 1–2 s on IPC alone per discovery.
            var sidecarDir = Path.Combine(modsRoot, modDir, SidecarSubdir);
            var metaPath   = Path.Combine(sidecarDir, MetadataFile);
            if (!File.Exists(metaPath)) continue;

            var settings = penumbra.GetModSettings(collId.Value, modDir);
            if (settings == null || !settings.Value.Enabled) continue;

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

        // Two-pass resolution: collect overlays and color rows independently across all groups.
        // Color-only groups (no overlays on their options) can still contribute color table rows
        // that apply to overlays from style groups, enabling independent style + color option groups.
        var rawOverlays  = new List<OverlayDescriptor>();
        // Seed with top-level rows so they act as the lowest-priority fallback.
        var mergedRows   = new Dictionary<int, ColorTableRowPreset>();
        if (entry.Metadata.ColorTableRows != null)
            foreach (var row in entry.Metadata.ColorTableRows)
                mergedRows[row.Row] = row;

        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;

            List<string>? selected = null;
            if (settings.HasValue)
                selected = settings.Value.Options
                    .FirstOrDefault(kv => string.Equals(kv.Key, group.PenumbraGroupName, StringComparison.OrdinalIgnoreCase))
                    .Value;

            IEnumerable<OverlayOption> active;
            if (selected is { Count: > 0 })
                // Include ALL selected options — handles both single-select and multi-select groups.
                active = group.Options.Where(o => selected.Any(s =>
                    string.Equals(o.Name, s, StringComparison.OrdinalIgnoreCase)));
            else
                // Nothing selected — default to first option.
                active = [group.Options[0]];

            foreach (var opt in active)
            {
                rawOverlays.AddRange(opt.Overlays);
                // Later groups override earlier groups for the same row index.
                if (opt.ColorTableRows != null)
                    foreach (var row in opt.ColorTableRows)
                        mergedRows[row.Row] = row;
            }
        }

        var finalRows = mergedRows.Count > 0 ? mergedRows.Values.ToList() : null;
        return rawOverlays.Select(d => new ResolvedOverlay(d, finalRows)).ToList();
    }

    /// <summary>
    /// Returns the merged color table rows across all active options in all groups — the same
    /// view the compositor uses. For display only; do not write to this list.
    /// </summary>
    public List<ColorTableRowPreset> GetMergedColorRows(OverlayEntry entry)
    {
        if (entry.Metadata.Overlays is { Count: > 0 } || entry.Metadata.OptionGroups == null)
            return entry.Metadata.ColorTableRows ?? [];

        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        var merged = new Dictionary<int, ColorTableRowPreset>();
        if (entry.Metadata.ColorTableRows != null)
            foreach (var row in entry.Metadata.ColorTableRows)
                merged[row.Row] = row;

        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;
            List<string>? selected = null;
            settings?.Options.TryGetValue(group.PenumbraGroupName, out selected);
            var opt = (selected is { Count: > 0 }
                ? group.Options.FirstOrDefault(o => string.Equals(o.Name, selected[0], StringComparison.OrdinalIgnoreCase))
                : null) ?? group.Options[0];
            if (opt.ColorTableRows != null)
                foreach (var row in opt.ColorTableRows)
                    merged[row.Row] = row;
        }

        return merged.Values.ToList();
    }

    /// <summary>
    /// Returns the ColorTableRows list of the highest-priority active option (last group in the
    /// array) — the edit target for the color picker. Writes to this list take effect over any
    /// rows set by earlier groups. Creates an empty list in the right place if absent.
    /// </summary>
    public List<ColorTableRowPreset> GetEditableColorRows(OverlayEntry entry)
    {
        if (entry.Metadata.Overlays is { Count: > 0 } || entry.Metadata.OptionGroups == null)
        {
            entry.Metadata.ColorTableRows ??= [];
            return entry.Metadata.ColorTableRows;
        }

        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        OverlayOption? lastOpt = null;
        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;
            List<string>? selected = null;
            settings?.Options.TryGetValue(group.PenumbraGroupName, out selected);
            lastOpt = (selected is { Count: > 0 }
                ? group.Options.FirstOrDefault(o => string.Equals(o.Name, selected[0], StringComparison.OrdinalIgnoreCase))
                : null) ?? group.Options[0];
        }

        if (lastOpt == null)
        {
            entry.Metadata.ColorTableRows ??= [];
            return entry.Metadata.ColorTableRows;
        }

        lastOpt.ColorTableRows ??= [];
        return lastOpt.ColorTableRows;
    }

    /// <summary>Backward-compat alias for <see cref="GetEditableColorRows"/>.</summary>
    public List<ColorTableRowPreset> GetActiveColorRows(OverlayEntry entry)
        => GetEditableColorRows(entry);

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
