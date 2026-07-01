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
    bool Enabled,        // current enabled state in the player's Penumbra collection
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
    List<ColorTableRowPreset>? ColorTableRows,
    string? OptionGroup,
    string? Option
);

public class SidecarDiscoveryService
{
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;

    private const string SidecarSubdir = "Proteus";
    private const string MetadataFile  = "metadata.json";
    public  const string ManagedModDir = "Proteus";  // directory name of the managed output mod

    // Convention-based "Masks" feature: a Penumbra multi-select group named exactly "Masks"
    // whose selected options each correspond to a grayscale PNG in the Proteus/Masks/ subfolder
    // (Masks/<OptionName>.png). These masks reduce the coverage of every other overlay in the
    // same mod. No metadata.json entry is required — selections are read straight from Penumbra.
    public  const string MaskGroupName = "Masks";
    private const string MaskSubdir    = "Masks";

    public SidecarDiscoveryService(PenumbraBridge penumbra, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.log = log;
    }

    /// <summary>
    /// Discover all Penumbra mods that contain a Proteus/ sidecar, carrying each mod's current
    /// enabled state and priority from the player's collection. Ordered by priority ascending
    /// (lowest priority = bottom of composite stack). The managed Proteus mod is excluded.
    /// Used by the UI so disabled mods stay listed (and can be re-enabled).
    /// </summary>
    public List<OverlayEntry> DiscoverAll() => Discover(enabledOnly: false);

    /// <summary>
    /// Like <see cref="DiscoverAll"/> but only mods currently enabled in Penumbra — the set the
    /// compositor actually composites.
    /// </summary>
    public List<OverlayEntry> DiscoverEnabled() => Discover(enabledOnly: true);

    private List<OverlayEntry> Discover(bool enabledOnly)
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
            if (settings == null) continue;
            if (enabledOnly && !settings.Value.Enabled) continue;

            var metadata = TryParseMetadata(metaPath);
            if (metadata == null) continue;

            results.Add(new OverlayEntry(modDir, modName, settings.Value.Priority,
                settings.Value.Enabled, metadata, sidecarDir));
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
                .Select(d => new ResolvedOverlay(d, entry.Metadata.ColorTableRows, null, null))
                .ToList();

        if (entry.Metadata.OptionGroups == null) return [];

        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        var resolved = new List<ResolvedOverlay>();
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
                active = group.Options.Where(o => selected.Any(s =>
                    string.Equals(o.Name, s, StringComparison.OrdinalIgnoreCase)));
            else
                continue;

            foreach (var opt in active)
            {
                var rows = opt.ColorTableRows ?? entry.Metadata.ColorTableRows;
                foreach (var desc in opt.Overlays)
                    resolved.Add(new ResolvedOverlay(desc, rows, group.PenumbraGroupName, opt.Name));
            }
        }
        return resolved;
    }

    /// <summary>
    /// Resolve the grayscale transparency-mask images currently selected for an entry. These come
    /// from a Penumbra multi-select group named <see cref="MaskGroupName"/> (no metadata.json entry
    /// needed); each selected option <c>Foo</c> maps to <c>Proteus/Masks/Foo.png</c>. Returns the
    /// absolute paths of the mask files that exist on disk, ordered by the group's option order so
    /// that masks higher in the Penumbra list take priority where they overlap (highest first).
    /// Empty when none are selected.
    /// </summary>
    public List<string> ResolveActiveMasks(OverlayEntry entry)
    {
        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null) return [];

        var settings = penumbra.GetModSettings(collId.Value, entry.ModDirectory);
        if (settings == null) return [];

        var selected = settings.Value.Options
            .FirstOrDefault(kv => string.Equals(kv.Key, MaskGroupName, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (selected is not { Count: > 0 }) return [];

        // Penumbra hands us the selected option names as a set; the authoritative top-to-bottom
        // order lives in the mod's group JSON. The mod root is the parent of the Proteus sidecar.
        var modRoot = Path.GetDirectoryName(
            entry.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var order   = modRoot != null ? ReadMaskGroupOptionOrder(modRoot) : [];

        return ResolveMaskPaths(entry.SidecarRoot, OrderByGroup(selected, order));
    }

    /// <summary>
    /// Like <see cref="ResolveActiveMasks"/>, but also resolves each mask's optional companion
    /// relief normal (<c>Masks/&lt;Option&gt;_n.png</c>) and color-row index
    /// (<c>Masks/&lt;Option&gt;_id.png</c>) — present only for mask layers exported with bump
    /// detail or their own row assignment (see the Substance export packager). Null when absent.
    /// </summary>
    public List<(string MaskPath, string? NormalPath, string? IndexPath)> ResolveActiveMaskAssets(OverlayEntry entry)
    {
        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null) return [];

        var settings = penumbra.GetModSettings(collId.Value, entry.ModDirectory);
        if (settings == null) return [];

        var selected = settings.Value.Options
            .FirstOrDefault(kv => string.Equals(kv.Key, MaskGroupName, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (selected is not { Count: > 0 }) return [];

        var modRoot = Path.GetDirectoryName(
            entry.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var order = modRoot != null ? ReadMaskGroupOptionOrder(modRoot) : [];

        var result = new List<(string, string?, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in OrderByGroup(selected, order))
        {
            if (string.IsNullOrWhiteSpace(option)) continue;
            var maskPath = Path.Combine(entry.SidecarRoot, MaskSubdir, option + ".png");
            if (!seen.Add(maskPath) || !File.Exists(maskPath)) continue;

            var normalPath = Path.Combine(entry.SidecarRoot, MaskSubdir, option + "_n.png");
            var indexPath  = Path.Combine(entry.SidecarRoot, MaskSubdir, option + "_id.png");
            result.Add((maskPath,
                File.Exists(normalPath) ? normalPath : null,
                File.Exists(indexPath) ? indexPath : null));
        }
        return result;
    }

    /// <summary>
    /// Pure mapping from selected mask-option names to existing <c>Masks/&lt;name&gt;.png</c> files
    /// under <paramref name="sidecarRoot"/>, preserving the input order. Skips options whose file is
    /// missing; dedupes case-insensitively. Factored out so it can be unit-tested without IPC.
    /// </summary>
    internal static List<string> ResolveMaskPaths(string sidecarRoot, IEnumerable<string>? selectedOptions)
    {
        var result = new List<string>();
        if (selectedOptions == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in selectedOptions)
        {
            if (string.IsNullOrWhiteSpace(option)) continue;
            var path = Path.Combine(sidecarRoot, MaskSubdir, option + ".png");
            if (seen.Add(path) && File.Exists(path))
                result.Add(path);
        }
        return result;
    }

    /// <summary>
    /// Reads the option-name order of the Penumbra group named <see cref="MaskGroupName"/> from the
    /// mod's <c>group_*.json</c> files in <paramref name="modRoot"/>. Returns the names top-to-bottom
    /// as shown in Penumbra, or an empty list if no such group file is found or it can't be parsed.
    /// </summary>
    internal static List<string> ReadMaskGroupOptionOrder(string modRoot)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("Name", out var nameEl)
                        || !string.Equals(nameEl.GetString(), MaskGroupName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!root.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array)
                        continue;

                    var names = new List<string>();
                    foreach (var o in opts.EnumerateArray())
                        if (o.TryGetProperty("Name", out var on) && on.GetString() is { } s)
                            names.Add(s);
                    return names;
                }
                catch { /* skip a malformed group file, keep scanning */ }
            }
        }
        catch { /* modRoot missing/unreadable */ }
        return [];
    }

    /// <summary>
    /// Orders <paramref name="selected"/> option names by their index in <paramref name="order"/>
    /// (the group's display order, highest priority first). Names not present in <paramref name="order"/>
    /// keep their relative position after all known ones. Stable.
    /// </summary>
    internal static List<string> OrderByGroup(IEnumerable<string> selected, List<string> order)
        => selected
            .OrderBy(s =>
            {
                int i = order.FindIndex(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? int.MaxValue : i;
            })
            .ToList();

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
