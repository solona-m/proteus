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
)
{
    /// <summary>
    /// The Penumbra mod folder this entry lives in — the parent of its <c>Proteus/</c> sidecar, and what
    /// every path a content pack stores (models, materials, textures) is relative to.
    /// <para/>
    /// One place rather than four: this convention was open-coded in the compositor and three times over in
    /// the status window, and a mod folder that failed to derive in one of them but not the others is the
    /// kind of drift nothing would catch. Null only for a sidecar path with no parent at all.
    /// </summary>
    public string? ModRoot => Path.GetDirectoryName(
        SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>
/// A single overlay descriptor paired with the color table rows that apply to it.
/// ColorTableRows comes from the option that owns the descriptor (if any), falling
/// back to the top-level metadata ColorTableRows.
/// </summary>
public record ResolvedOverlay(
    OverlayDescriptor Descriptor,
    List<ColorTableRowPreset>? ColorTableRows,
    string? OptionGroup,
    string? Option,
    /// <summary>
    /// Penumbra's own group ordinal (its index in meta.json's Groups array). LOWER = higher priority, and a higher
    /// group wins wherever it is visible: the compositor suppresses lower groups underneath it.
    /// int.MaxValue for top-level overlays, which belong to no group.
    /// </summary>
    int GroupOrder = int.MaxValue
);

/// <summary>
/// One geometry piece an imported content pack currently contributes, paired with the colour rows that
/// apply to it. Mirrors <see cref="ResolvedOverlay"/> field for field so the compositor, the design
/// bindings and the editor can key both on the same <c>(mod, group, option)</c> triple.
/// </summary>
public record ResolvedContent(
    ContentPiece Piece,
    List<ColorTableRowPreset>? ColorTableRows,
    string? OptionGroup,
    string? Option,
    int GroupOrder = int.MaxValue,
    /// <summary>
    /// The animated glow this piece's material takes, resolved with the same option-then-mod fallback the
    /// colour rows use. Null — the usual case — publishes the pack's own material untouched.
    /// </summary>
    GearSettingsPreset? Glow = null
);

public class SidecarDiscoveryService
{
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;

    // Public so PenumbraModMeta.CleanLegacyFiles can sweep this folder too — metadata.json is written
    // through AtomicWrite and strands its temp file here, one level below the mod root.
    public const string SidecarSubdir = "Proteus";
    private const string MetadataFile  = "metadata.json";
    // The mod's settings as Proteus first found them, copied aside just before our first write so the
    // editor's "Reset to defaults" can restore them. Discovery only ever looks for MetadataFile by exact
    // name, so this sits inertly beside it.
    private const string DefaultsFile  = "metadata.default.json";
    public  const string ManagedModDir = "Proteus";  // directory name of the managed output mod

    // Convention-based "Masks" feature: a Penumbra multi-select group named exactly "Masks"
    // whose selected options each correspond to a grayscale PNG in the Proteus/Masks/ subfolder
    // (Masks/<OptionName>.png). These masks reduce the coverage of every other overlay in the
    // same mod. No metadata.json entry is required — selections are read straight from Penumbra.
    public  const string MaskGroupName = "Masks";
    private const string MaskSubdir    = "Masks";

    /// <summary>Plugin assembly directory — the bundled DefaultEffects live under it. Set once at startup.</summary>
    public string? AssemblyDir { get; set; }

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

        // Priority comes from Penumbra's group numbering, not the order the groups happen to sit in
        // metadata.json — the author controls it by ordering the groups, which is what they expect.
        var modRoot = Path.GetDirectoryName(
            entry.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var groupOrder = modRoot != null ? ReadGroupOrder(modRoot) : [];

        var resolved = new List<ResolvedOverlay>();
        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;

            int order = groupOrder.TryGetValue(group.PenumbraGroupName, out var n) ? n : int.MaxValue;

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
                    resolved.Add(new ResolvedOverlay(desc, rows, group.PenumbraGroupName, opt.Name, order));
            }
        }
        return resolved;
    }

    /// <summary>
    /// Resolve the geometry an imported content pack currently contributes: its unconditional
    /// <see cref="ProteusMetadata.Content"/> pieces, or the pieces of whichever options are selected in
    /// Penumbra. The selection read is identical to <see cref="ResolveActiveOverlays"/>'s — Penumbra owns
    /// which options are on, and Proteus only mirrors it.
    /// </summary>
    public List<ResolvedContent> ResolveActiveContent(OverlayEntry entry)
    {
        var meta = entry.Metadata;
        if (!meta.HasContent) return [];

        // Ask Penumbra only when there is something to ask about. A pack whose pieces are all unconditional
        // and ungated resolves without a single IPC hop, which is the same rule the sidecar pre-filter in
        // Discover follows and the reason this stays cheap for the common case.
        bool needsSettings = meta.PieceGroupName is { Length: > 0 } || meta.ContentGroups is { Count: > 0 };
        (bool Enabled, int Priority, Dictionary<string, List<string>> Options)? settings = null;
        if (needsSettings)
        {
            var collId = penumbra.GetPlayerCollectionId();
            settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;
        }

        List<string>? Selection(string group)
            => settings?.Options
                .FirstOrDefault(kv => string.Equals(kv.Key, group, StringComparison.OrdinalIgnoreCase))
                .Value;

        // The synthesized piece group, if the importer added one. A gated piece whose option is not ticked
        // is not worn — and when the selection cannot be read at all, nothing gated is worn either: the
        // safe direction is to leave off something the user never asked for.
        var gateOn = meta.PieceGroupName is { Length: > 0 } gateGroup ? Selection(gateGroup) : null;

        bool Ungated(ContentPiece p) => PieceIsOn(p, gateOn);

        var resolved = new List<ResolvedContent>();

        // Unconditional pieces. Additive with the groups below rather than an either/or: one pack can
        // legitimately ship both, and returning early on the first would silently drop the rest.
        foreach (var piece in meta.Content ?? [])
            if (Ungated(piece))
                resolved.Add(new ResolvedContent(piece, meta.ColorTableRows, null, null, Glow: meta.ContentGlow));

        if (meta.ContentGroups == null || !settings.HasValue) return resolved;

        var modRoot = entry.ModRoot;
        var groupOrder = modRoot != null ? ReadGroupOrder(modRoot) : [];

        foreach (var group in meta.ContentGroups)
        {
            if (group.Options.Count == 0) continue;

            int order = groupOrder.TryGetValue(group.PenumbraGroupName, out var n) ? n : int.MaxValue;

            var selected = Selection(group.PenumbraGroupName);
            if (selected is not { Count: > 0 }) continue;

            foreach (var opt in group.Options.Where(o => selected.Any(sel =>
                         string.Equals(o.Name, sel, StringComparison.OrdinalIgnoreCase))))
            {
                var rows = opt.ColorTableRows ?? meta.ColorTableRows;
                // Same option-then-mod fallback the rows take, so a pack-wide glow reaches an option that
                // never set one of its own.
                var glow = opt.Glow ?? meta.ContentGlow;
                foreach (var piece in opt.Pieces)
                    if (Ungated(piece))
                        resolved.Add(new ResolvedContent(
                            piece, rows, group.PenumbraGroupName, opt.Name, order, glow));
            }
        }
        return resolved;
    }

    /// <summary>
    /// Whether a piece's gate is open: it has none, or the option that switches it on is among
    /// <paramref name="selection"/>.
    /// <para/>
    /// A null selection means the gate group's state could not be read at all, and everything gated stays
    /// OFF. That is the safe direction — the alternative is wearing something the user never ticked — and it
    /// is why this is a decision worth naming rather than an inline condition.
    /// </summary>
    internal static bool PieceIsOn(ContentPiece piece, IReadOnlyList<string>? selection)
        => piece.GateOption == null
        || (selection != null
            && selection.Any(sel => string.Equals(sel, piece.GateOption, StringComparison.OrdinalIgnoreCase)));

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
            var maskPath = ResolveMaskAsset(Path.Combine(entry.SidecarRoot, MaskSubdir, option));
            if (maskPath == null || !seen.Add(maskPath)) continue;

            var normalPath = ResolveMaskAsset(Path.Combine(entry.SidecarRoot, MaskSubdir, option + "_n"));
            var indexPath  = ResolveMaskAsset(Path.Combine(entry.SidecarRoot, MaskSubdir, option + "_id"));
            result.Add((maskPath, normalPath, indexPath));
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
            var stem = Path.Combine(sidecarRoot, MaskSubdir, option);
            var path = ResolveMaskAsset(stem);
            if (path != null && seen.Add(path))
                result.Add(path);
        }
        return result;
    }

    // Resolve a Masks/ asset given its path without extension, preferring .png but falling back to
    // the BC7-capable containers (.dds then .tex) so a mod packaged entirely in BC7 (masks included)
    // still resolves. Null if none exist.
    internal static string? ResolveMaskAsset(string basePathNoExt)
    {
        foreach (var ext in MaskAssetExtensions)
        {
            var path = basePathNoExt + ext;
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static readonly string[] MaskAssetExtensions = [".png", ".dds", ".tex"];

    // ── Effects (characterscroll `_o` / `catc` scroll maps) ──────────────────

    private const string EffectsSubdir = "Effects";

    /// <summary>
    /// Image types an effect can be. .tex and .dds get the game-format decoders; everything else goes
    /// through StbImageSharp, which reads all of these — so the list is just what we're willing to
    /// enumerate, not a decoding constraint.
    /// </summary>
    private static readonly string[] EffectExtensions =
        [".png", ".dds", ".tex", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif"];

    /// <summary>
    /// The global effects library: <c>&lt;penumbra mods&gt;\Proteus\Effects\</c>, i.e. inside Proteus's own
    /// managed mod folder. Self-locating, so there's nothing for the user to configure — drop scroll maps
    /// in there and they show up in every gear overlay's Effect dropdown. Created on demand.
    /// </summary>
    public string? EffectsLibraryPath()
    {
        var root = penumbra.GetModDirectory();
        if (string.IsNullOrWhiteSpace(root)) return null;

        var dir = Path.Combine(root, ManagedModDir, EffectsSubdir);
        try { Directory.CreateDirectory(dir); } catch { return null; }
        return dir;
    }

    /// <summary>
    /// Copy the plugin's bundled starter effects (shipped in <c>&lt;assembly&gt;\DefaultEffects\</c>) into the
    /// user's global effects library, skipping any file already there. Runs once on startup; a file the
    /// user has since deleted stays deleted (we only fill gaps, and only for names that aren't present).
    /// Never overwrites — a user's edited copy of a bundled effect is left alone.
    /// </summary>
    public void SeedDefaultEffects()
    {
        if (AssemblyDir == null) return;
        var src = Path.Combine(AssemblyDir, "DefaultEffects");
        var dst = EffectsLibraryPath();
        if (dst == null || !Directory.Exists(src)) return;

        try
        {
            foreach (var f in Directory.EnumerateFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(f));
                if (File.Exists(target)) continue;
                try { File.Copy(f, target); }
                catch (Exception ex) { log.Warning(ex, "[Proteus] could not seed effect {0}", Path.GetFileName(f)); }
            }
        }
        catch (Exception ex) { log.Warning(ex, "[Proteus] SeedDefaultEffects failed"); }
    }

    /// <summary>
    /// The scroll maps an overlay can choose from: the mod's own <c>Proteus/Effects/</c> first, then the
    /// user's global library folder. A mod that ships its own effects stays portable; the global folder
    /// is a personal library. Deduped by file name — the mod's copy wins.
    /// </summary>
    public List<(string Name, string Path, bool FromMod)> ResolveAvailableEffects(
        OverlayEntry entry, string? globalFolder)
    {
        var result = new List<(string, string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Scan(string? dir, bool fromMod)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!EffectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    continue;
                var name = Path.GetFileName(f);
                if (seen.Add(name))
                    result.Add((name, f, fromMod));
            }
        }

        Scan(Path.Combine(entry.SidecarRoot, EffectsSubdir), true);
        Scan(globalFolder, false);
        return result;
    }

    /// <summary>
    /// Resolve an overlay's stored <c>Scroll</c> value to a file on disk: a bare file name is looked up
    /// in the mod's Effects/ then the global folder; a relative path is taken as sidecar-relative (what
    /// hand-written metadata does today). Null when nothing matches.
    /// </summary>
    public static string? ResolveEffectPath(OverlayEntry entry, string? globalFolder, string scroll)
    {
        if (string.IsNullOrWhiteSpace(scroll)) return null;

        // Bare file name → the effects folders.
        if (!scroll.Contains('/') && !scroll.Contains('\\'))
        {
            var inMod = Path.Combine(entry.SidecarRoot, EffectsSubdir, scroll);
            if (File.Exists(inMod)) return inMod;

            if (!string.IsNullOrWhiteSpace(globalFolder))
            {
                var inLib = Path.Combine(globalFolder, scroll);
                if (File.Exists(inLib)) return inLib;
            }
            return null;
        }

        // Otherwise a sidecar-relative path (back-compat with metadata written by hand).
        var rel = Path.Combine(entry.SidecarRoot, scroll);
        return File.Exists(rel) ? rel : null;
    }

    /// <summary>
    /// Reads the option-name order of the Penumbra group named <see cref="MaskGroupName"/> from the mod's
    /// manifest in <paramref name="modRoot"/> — <c>meta.json</c>'s <c>Groups</c> array (Penumbra v4),
    /// falling back to the legacy <c>group_*.json</c> files. Returns the names top-to-bottom as shown in
    /// Penumbra, or an empty list if no such group is found or it can't be parsed.
    /// </summary>
    internal static List<string> ReadMaskGroupOptionOrder(string modRoot)
    {
        if (PenumbraModMeta.TryReadGroups(modRoot) is { } groups)
        {
            foreach (var (name, group) in groups)
                if (string.Equals(name, MaskGroupName, StringComparison.OrdinalIgnoreCase))
                    return PenumbraModMeta.ReadOptionNames(group);
            return [];
        }

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
    /// Penumbra group name → its ordinal: the index in <c>meta.json</c>'s <c>Groups</c> array (Penumbra
    /// v4), or the legacy filename number (<c>group_002_fabric.json</c> → 2) for unmigrated folders.
    /// LOWER is higher priority. This — not the order groups happen to appear in metadata.json — is what
    /// decides which group wins where two of them overlay the same skin.
    /// </summary>
    internal static Dictionary<string, int> ReadGroupOrder(string modRoot)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (PenumbraModMeta.TryReadGroups(modRoot) is { } groups)
        {
            for (int i = 0; i < groups.Count; i++)
                result[groups[i].Name] = i;
            return result;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json"))
            {
                try
                {
                    // group_002_fabric.json -> 2
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var parts = stem.Split('_');
                    if (parts.Length < 2 || !int.TryParse(parts[1], out var number)) continue;

                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("Name", out var nameEl)
                        && nameEl.GetString() is { Length: > 0 } name)
                        result[name] = number;
                }
                catch { /* skip a malformed group file, keep scanning */ }
            }
        }
        catch { /* modRoot missing/unreadable */ }
        return result;
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
            SnapshotDefaults(entry, path);

            var json = JsonSerializer.Serialize(entry.Metadata, ProteusJson.MetadataWrite);
            // AtomicWrite, not File.WriteAllText: this is the authored overlay — material paths, body
            // type, shader, colour rows — and nothing can rebuild it. Truncating it in place to refill
            // it means a crash mid-save loses the mod's whole descriptor, and the editor saves often.
            //
            // Interactive retry budget, NOT the default: every caller of this reaches it from a thread the
            // user can feel — the editor saves from the ImGui draw path, and IpcProvider from whichever
            // thread a peer plugin called on. The full ~1.55 s backoff would show up as a hung colour
            // slider. 150 ms still beats the File.WriteAllText this replaced (which got one attempt and no
            // retry at all), and a save that still loses is rewritten by the next edit.
            //
            // Synchronous on purpose. Deferring it to a background task would be the obvious way to spend
            // nothing here, but Discover re-reads this file from disk (see the metaPath parse above) and
            // the editor recomposites as soon as it returns — so a write still in flight means the
            // composite picks up the PREVIOUS metadata and the edit looks like it did nothing.
            PenumbraModMeta.AtomicWrite(path, json, maxRetries: 2);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to save Proteus metadata for {0}", entry.ModDirectory);
        }
    }

    /// <summary>
    /// Preserve the mod's settings as they were BEFORE Proteus ever wrote to them, so the editor's "Reset
    /// to defaults" has something to restore. The editor mutates <c>entry.Metadata</c> in memory and only
    /// then calls <see cref="SaveMetadata"/>, so at this moment the file on disk is still the original —
    /// copying it here (once, before the first overwrite) captures the author's values. Best-effort: a
    /// failed snapshot must never stop the save.
    /// </summary>
    private void SnapshotDefaults(OverlayEntry entry, string metaPath)
    {
        try
        {
            var defaults = Path.Combine(entry.SidecarRoot, DefaultsFile);
            if (File.Exists(defaults) || !File.Exists(metaPath)) return;
            File.Copy(metaPath, defaults);
            log.Information("[Proteus] captured original settings for {0} -> {1}", entry.ModDirectory, DefaultsFile);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to snapshot original Proteus metadata for {0}", entry.ModDirectory);
        }
    }

    /// <summary>True when this mod has a recorded pre-edit snapshot to reset back to.</summary>
    public bool HasDefaults(OverlayEntry entry)
        => File.Exists(Path.Combine(entry.SidecarRoot, DefaultsFile));

    /// <summary>
    /// The mod's settings as first seen by Proteus, or null when none were recorded / the file is broken.
    /// Each call re-parses, so the returned graph is freshly owned and can be assigned into the live
    /// metadata without cloning.
    /// </summary>
    public ProteusMetadata? TryLoadDefaults(OverlayEntry entry)
    {
        var defaults = Path.Combine(entry.SidecarRoot, DefaultsFile);
        return File.Exists(defaults) ? TryParseMetadata(defaults) : null;
    }

    private ProteusMetadata? TryParseMetadata(string metaPath)
    {
        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<ProteusMetadata>(json, ProteusJson.MetadataRead);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to parse Proteus metadata: {0}", metaPath);
            return null;
        }
    }
}
