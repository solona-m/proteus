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
    /// The Penumbra mod folder this entry lives in: the parent of its <c>Proteus/</c> sidecar, and what every path a
    /// content pack stores is relative to. Null only for a sidecar path with no parent.
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

/// <summary>
/// Whether Penumbra was asked which of a mod's options are on, and what came back. Three states because "never
/// needed to ask" is not "asked and got nothing", and only the latter makes a <see cref="ResolutionDiagnostic"/> untrustworthy.
/// </summary>
public enum SettingsRead
{
    /// <summary>No IPC hop was needed: nothing about this mod depends on the selection.</summary>
    NotAsked,
    /// <summary>Penumbra answered.</summary>
    Ok,
    /// <summary>Penumbra was asked and did not answer — no collection, or the call failed. Nothing else
    /// in the diagnostic can be trusted, because none of it could be determined.</summary>
    Unavailable,
}

/// <summary>
/// Why <see cref="SidecarDiscoveryService.ResolveActiveOverlays(OverlayEntry, out ResolutionDiagnostic)"/>
/// or <see cref="SidecarDiscoveryService.ResolveActiveContent(OverlayEntry, out ResolutionDiagnostic)"/>
/// resolved what it did. One type for overlays and content, which callers <see cref="Merge"/>; carried out rather
/// than logged, since only <see cref="CompositorService"/> knows whether an empty resolve matters.
/// </summary>
public readonly record struct ResolutionDiagnostic(
    /// <summary>Whether the selection could be read at all. See <see cref="SettingsRead"/>.</summary>
    SettingsRead Settings,
    /// <summary>Groups in metadata.json that declare at least one option.</summary>
    int GroupCount,
    /// <summary>Groups Penumbra knows about where the user has ticked nothing.</summary>
    IReadOnlyList<string> EmptyGroups,
    /// <summary>Groups named in metadata.json that Penumbra's copy of the mod has no group for: an authoring
    /// error, since nothing in them can ever be selected.</summary>
    IReadOnlyList<string> MissingGroups,
    /// <summary>The mod declares pieces or overlays that no option gates, so "tick something" is never the advice.</summary>
    bool Unconditional,
    /// <summary>Every group name in Penumbra's own copy of the mod, from its meta.json; empty when the manifest was
    /// never read. Lets a caller check for another group without re-parsing.</summary>
    IReadOnlyCollection<string> PenumbraGroups)
{
    /// <summary>The all-clear: what a resolve that had nothing to explain returns.</summary>
    public static ResolutionDiagnostic None => new(SettingsRead.NotAsked, 0, [], [], false, []);

    /// <summary>
    /// The two halves of one pack as a single picture. Counts and lists add; <see cref="Unconditional"/> is true if
    /// either half is; the worst <see cref="Settings"/> wins.
    /// </summary>
    public ResolutionDiagnostic Merge(ResolutionDiagnostic other) => new(
        Settings  == SettingsRead.Unavailable || other.Settings == SettingsRead.Unavailable
            ? SettingsRead.Unavailable
            : Settings == SettingsRead.Ok || other.Settings == SettingsRead.Ok
                ? SettingsRead.Ok
                : SettingsRead.NotAsked,
        GroupCount + other.GroupCount,
        [.. EmptyGroups,   .. other.EmptyGroups],
        [.. MissingGroups, .. other.MissingGroups],
        Unconditional || other.Unconditional,
        PenumbraGroups.Count > 0 ? PenumbraGroups : other.PenumbraGroups);
}

public class SidecarDiscoveryService
{
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;

    // Public so PenumbraModMeta.CleanLegacyFiles can sweep AtomicWrite's temp files from this folder too.
    public const string SidecarSubdir = "Proteus";
    internal const string MetadataFile = "metadata.json";
    // The mod's settings as Proteus first found them, copied aside before our first write for "Reset to defaults".
    private const string DefaultsFile  = "metadata.default.json";
    public  const string ManagedModDir = "Proteus";  // directory name of the managed output mod

    // Convention-based "Masks": a Penumbra multi-select group named exactly "Masks" whose selected options each map
    // to Proteus/Masks/<OptionName>.png, reducing the coverage of every other overlay in the mod.
    public  const string MaskGroupName = "Masks";
    private const string MaskSubdir    = "Masks";

    /// <summary>
    /// Where the starter scroll-effect library is cached (<see cref="DefaultEffectsDownloadService.EffectsDir"/>, set
    /// at startup). Null until then, which <see cref="SeedDefaultEffects"/> treats as nothing to seed.
    /// </summary>
    public string? DefaultEffectsDir { get; set; }

    /// <summary>
    /// Plugin assembly directory, for files that ship with the build (the authored toe caps under <c>Meshes</c>).
    /// </summary>
    public string? AssemblyDir { get; set; }

    /// <summary>
    /// Reserved option name inside the <see cref="MaskGroupName"/> group. <c>Masks/Toe Cap.png</c> is not a
    /// transparency mask: it marks where the second-skin shell webs into a toe cap, so every mask consumer skips it.
    /// </summary>
    public const string ToeCapOptionName = "Toe Cap";

    /// <summary>
    /// Is this the reserved toe-cap option? Matched on letters and digits only, case-insensitively: an unmatched
    /// name would silently fall through as an ordinary mask.
    /// </summary>
    private static bool IsToeCapOption(string? option) =>
        option != null && Squash(option) == Squash(ToeCapOptionName);

    private static string Squash(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public SidecarDiscoveryService(PenumbraBridge penumbra, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.log = log;
    }

    /// <summary>
    /// Discover all Penumbra mods with a Proteus/ sidecar, with each mod's enabled state and priority from the
    /// player's collection, ordered by priority ascending. The managed Proteus mod is excluded.
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

            // Check for the sidecar before GetModSettings: File.Exists is far cheaper than an IPC hop.
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
    /// Resolve the active overlays for an entry, paired with their colour table rows. Top-level Overlays are all
    /// active; otherwise every selected option contributes, its ColorTableRows overriding the top-level rows.
    /// </summary>
    public List<ResolvedOverlay> ResolveActiveOverlays(OverlayEntry entry)
        => ResolveActiveOverlays(entry, out _);

    /// <summary>
    /// <see cref="ResolveActiveOverlays(OverlayEntry)"/>, also reporting why it resolved what it did — see
    /// <see cref="ResolutionDiagnostic"/>.
    /// </summary>
    public List<ResolvedOverlay> ResolveActiveOverlays(OverlayEntry entry, out ResolutionDiagnostic diag)
    {
        if (entry.Metadata.Overlays is { Count: > 0 })
        {
            diag = ResolutionDiagnostic.None with { Unconditional = true };
            return entry.Metadata.Overlays
                .Select(d => new ResolvedOverlay(d, entry.Metadata.ColorTableRows, null, null))
                .ToList();
        }

        if (entry.Metadata.OptionGroups == null)
        {
            diag = ResolutionDiagnostic.None;
            return [];
        }

        var collId   = penumbra.GetPlayerCollectionId();
        var settings = collId.HasValue ? penumbra.GetModSettings(collId.Value, entry.ModDirectory) : null;

        // Priority comes from Penumbra's group numbering, not the order of groups in metadata.json.
        var modRoot = Path.GetDirectoryName(
            entry.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var groupOrder = modRoot != null ? ReadGroupOrder(modRoot) : [];

        // A group Penumbra has with nothing ticked is the user's to fix; one Penumbra has never heard of is the
        // author's.
        var emptyGroups   = new List<string>();
        var missingGroups = new List<string>();
        int groupCount    = 0;

        var resolved = new List<ResolvedOverlay>();
        foreach (var group in entry.Metadata.OptionGroups)
        {
            if (group.Options.Count == 0) continue;
            groupCount++;

            bool known = groupOrder.TryGetValue(group.PenumbraGroupName, out var n);
            int order  = known ? n : int.MaxValue;

            // Only meaningful when the group order was read: an unreadable meta.json must not make every group "missing".
            if (!known && groupOrder.Count > 0) missingGroups.Add(group.PenumbraGroupName);

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
            {
                if (known || groupOrder.Count == 0) emptyGroups.Add(group.PenumbraGroupName);
                continue;
            }

            foreach (var opt in active)
            {
                var rows = opt.ColorTableRows ?? entry.Metadata.ColorTableRows;
                foreach (var desc in opt.Overlays)
                    resolved.Add(new ResolvedOverlay(desc, rows, group.PenumbraGroupName, opt.Name, order));
            }
        }

        if (missingGroups.Count > 0) AnnounceGroupMismatch(entry, missingGroups, groupOrder.Keys);

        diag = new ResolutionDiagnostic(
            settings.HasValue ? SettingsRead.Ok : SettingsRead.Unavailable,
            groupCount, emptyGroups, missingGroups, false, groupOrder.Keys.ToList());
        return resolved;
    }

    /// <summary>
    /// Warn once per mod and group that metadata.json names an option group Penumbra's copy of the mod lacks; Debug
    /// after that, since this runs for every mod on every composite.
    /// </summary>
    private void AnnounceGroupMismatch(OverlayEntry entry, List<string> missing, IEnumerable<string> penumbraGroups)
    {
        var have = string.Join(", ", penumbraGroups);
        foreach (var group in missing)
        {
            if (_groupNameMismatch.TryAdd($"{entry.ModDirectory}\0{group}", 0))
                log.Warning("[Proteus] {0}: its Proteus data names the option group \"{1}\", but Penumbra's "
                          + "copy of this mod has no group by that name (it has [{2}]) — renamed or dropped "
                          + "on re-export, so nothing in that group can ever be selected",
                    entry.ModDirectory, group, have);
            else
                log.Debug("[Proteus] {0}: option group \"{1}\" is not in Penumbra's copy of this mod",
                    entry.ModDirectory, group);
        }
    }

    /// <summary>
    /// Which (mod, group) pairs have had their mismatch announced this session. Concurrent: composites can overlap.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _groupNameMismatch =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve the geometry an imported content pack currently contributes: its unconditional
    /// <see cref="ProteusMetadata.Content"/> pieces plus the pieces of whichever options are selected in Penumbra.
    /// </summary>
    public List<ResolvedContent> ResolveActiveContent(OverlayEntry entry)
        => ResolveActiveContent(entry, out _);

    /// <summary>
    /// <see cref="ResolveActiveContent(OverlayEntry)"/>, also reporting why it resolved what it did — the content-side
    /// twin of <see cref="ResolveActiveOverlays(OverlayEntry, out ResolutionDiagnostic)"/>, so a caller can merge them.
    /// </summary>
    public List<ResolvedContent> ResolveActiveContent(OverlayEntry entry, out ResolutionDiagnostic diag)
    {
        var meta = entry.Metadata;
        if (!meta.HasContent)
        {
            diag = ResolutionDiagnostic.None;
            return [];
        }

        // Counted from metadata rather than the walk below, which is skipped when the selection cannot be read.
        int groupCount = meta.ContentGroups?.Count(g => g.Options.Count > 0) ?? 0;
        var emptyGroups   = new List<string>();
        var missingGroups = new List<string>();

        // Ask Penumbra only when something is gated, so an all-unconditional pack costs no IPC hop.
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

        // The synthesized piece group, if the importer added one. An unreadable selection wears nothing gated.
        var gateOn = meta.PieceGroupName is { Length: > 0 } gateGroup ? Selection(gateGroup) : null;

        bool Ungated(ContentPiece p) => PieceIsOn(p, gateOn);

        var resolved = new List<ResolvedContent>();

        // Unconditional pieces, additive with the groups below.
        foreach (var piece in meta.Content ?? [])
            if (Ungated(piece))
                resolved.Add(new ResolvedContent(piece, meta.ColorTableRows, null, null, Glow: meta.ContentGlow));

        // How the read went. Unconditional pieces are recorded regardless: ticking something cannot fix them.
        var settingsRead = !needsSettings ? SettingsRead.NotAsked
                         : settings.HasValue ? SettingsRead.Ok
                         : SettingsRead.Unavailable;
        bool unconditional = meta.Content is { Count: > 0 };

        if (meta.ContentGroups == null || !settings.HasValue)
        {
            diag = new ResolutionDiagnostic(
                settingsRead, groupCount, emptyGroups, missingGroups, unconditional, []);
            return resolved;
        }

        var modRoot = entry.ModRoot;
        var groupOrder = modRoot != null ? ReadGroupOrder(modRoot) : [];

        foreach (var group in meta.ContentGroups)
        {
            if (group.Options.Count == 0) continue;

            bool known = groupOrder.TryGetValue(group.PenumbraGroupName, out var n);
            int order  = known ? n : int.MaxValue;

            // Same rule as the overlay resolver: an unreadable manifest must not make every group look renamed.
            if (!known && groupOrder.Count > 0) missingGroups.Add(group.PenumbraGroupName);

            var selected = Selection(group.PenumbraGroupName);
            if (selected is not { Count: > 0 })
            {
                if (known || groupOrder.Count == 0) emptyGroups.Add(group.PenumbraGroupName);
                continue;
            }

            foreach (var opt in group.Options.Where(o => selected.Any(sel =>
                         string.Equals(o.Name, sel, StringComparison.OrdinalIgnoreCase))))
            {
                var rows = opt.ColorTableRows ?? meta.ColorTableRows;
                // Same option-then-mod fallback the rows take.
                var glow = opt.Glow ?? meta.ContentGlow;
                foreach (var piece in opt.Pieces)
                    if (Ungated(piece))
                        resolved.Add(new ResolvedContent(
                            piece, rows, group.PenumbraGroupName, opt.Name, order, glow));
            }
        }

        if (missingGroups.Count > 0) AnnounceGroupMismatch(entry, missingGroups, groupOrder.Keys);

        diag = new ResolutionDiagnostic(
            settingsRead, groupCount, emptyGroups, missingGroups, unconditional, groupOrder.Keys.ToList());
        return resolved;
    }

    /// <summary>
    /// Whether a piece's gate is open: it has none, or its option is among <paramref name="selection"/>. A null
    /// selection (unreadable) keeps everything gated off.
    /// </summary>
    internal static bool PieceIsOn(ContentPiece piece, IReadOnlyList<string>? selection)
        => piece.GateOption == null
        || (selection != null
            && selection.Any(sel => string.Equals(sel, piece.GateOption, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Resolve the transparency-mask images currently selected in the <see cref="MaskGroupName"/> group: each
    /// selected option <c>Foo</c> maps to <c>Proteus/Masks/Foo.png</c>. Returns existing files' absolute paths in the
    /// group's option order (highest priority first); empty when none are selected.
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

        // Penumbra gives the selection as a set; the top-to-bottom order lives in the mod's group JSON.
        var modRoot = Path.GetDirectoryName(
            entry.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var order   = modRoot != null ? ReadMaskGroupOptionOrder(modRoot) : [];

        return ResolveMaskPaths(entry.SidecarRoot, OrderByGroup(selected, order));
    }

    /// <summary>
    /// Like <see cref="ResolveActiveMasks"/>, but also resolves each mask's optional relief normal
    /// (<c>Masks/&lt;Option&gt;_n.png</c>) and colour-row index (<c>Masks/&lt;Option&gt;_id.png</c>). Null when absent.
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
            if (string.IsNullOrWhiteSpace(option) || IsToeCapOption(option)) continue;   // caps aren't masks
            var maskPath = ResolveMaskAsset(Path.Combine(entry.SidecarRoot, MaskSubdir, option));
            if (maskPath == null || !seen.Add(maskPath)) continue;

            var normalPath = ResolveMaskAsset(Path.Combine(entry.SidecarRoot, MaskSubdir, option + "_n"));
            var indexPath  = ResolveMaskAsset(Path.Combine(entry.SidecarRoot, MaskSubdir, option + "_id"));
            result.Add((maskPath, normalPath, indexPath));
        }
        return result;
    }

    /// <summary>
    /// How the <see cref="MaskGroupName"/> group stands for this mod: whether the pack ships one, and how many of its
    /// options are ticked (toe cap excluded). Kept separable: "no masks" is the pack, "none ticked" is the user.
    /// Presence is read from the manifest, since an untouched group may be absent from Penumbra's settings.
    /// </summary>
    /// <param name="penumbraGroups">
    /// The mod's group names if the caller already has them (<see cref="ResolutionDiagnostic.PenumbraGroups"/>),
    /// skipping a re-parse; null or empty reads the manifest.
    /// </param>
    public (bool GroupPresent, int Selected) MaskSelectionState(
        OverlayEntry entry, Guid collId, IReadOnlyCollection<string>? penumbraGroups = null)
    {
        bool present;
        if (penumbraGroups is { Count: > 0 })
        {
            present = penumbraGroups.Contains(MaskGroupName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var modRoot = Path.GetDirectoryName(
                entry.SidecarRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            present = modRoot != null && ReadMaskGroupOptionOrder(modRoot).Count > 0;
        }

        var settings = penumbra.GetModSettings(collId, entry.ModDirectory);
        var selected = settings?.Options
            .FirstOrDefault(kv => string.Equals(kv.Key, MaskGroupName, StringComparison.OrdinalIgnoreCase))
            .Value;

        int count = selected?.Count(o => !string.IsNullOrWhiteSpace(o) && !IsToeCapOption(o)) ?? 0;
        return (present || count > 0, count);
    }

    /// <summary>
    /// Path of this mod's toe-cap map when the reserved <see cref="ToeCapOptionName"/> option is selected in the
    /// <see cref="MaskGroupName"/> group and its file exists; otherwise null.
    /// </summary>
    public string? ResolveActiveToeCap(OverlayEntry entry)
    {
        var collId = penumbra.GetPlayerCollectionId();
        return collId == null ? null : ResolveActiveToeCap(entry, collId.Value);
    }

    /// <summary>
    /// <see cref="ResolveActiveToeCap(OverlayEntry)"/> against a collection id the caller already has.
    /// </summary>
    public string? ResolveActiveToeCap(OverlayEntry entry, Guid collId)
    {
        var settings = penumbra.GetModSettings(collId, entry.ModDirectory);
        if (settings == null) return null;

        var selected = settings.Value.Options
            .FirstOrDefault(kv => string.Equals(kv.Key, MaskGroupName, StringComparison.OrdinalIgnoreCase))
            .Value;

        var option = selected?.FirstOrDefault(IsToeCapOption);
        if (option == null) return null;   // not selected — the ordinary case, say nothing

        var path = ResolveMaskAsset(Path.Combine(entry.SidecarRoot, MaskSubdir, option.Trim()));
        if (path == null)
            log.Warning("[Proteus] toe cap \"{0}\" is selected but {1}\\{2}\\{0}.png is missing — no cap",
                option, entry.SidecarRoot, MaskSubdir);
        // Announced once per mod and option, then Debug: the cap is absent from every mask list, so it needs
        // saying, but this runs for every mod on every composite.
        else if (_toeCapAnnounced.TryAdd($"{entry.ModDirectory}\0{option}", 0))
            log.Information("[Proteus] toe cap \"{0}\" is selected in \"{1}\" — if you did not tick it, the "
                          + "mod's option ORDER changed since the selection was saved (Penumbra stores it by "
                          + "index); re-tick the group to re-sync it",
                option, entry.ModDirectory);
        else
            log.Debug("[Proteus] toe cap \"{0}\" is selected in \"{1}\"", option, entry.ModDirectory);
        return path;
    }

    /// <summary>
    /// Which (mod, option) pairs have already had their toe cap announced at Information this session.
    /// Concurrent because composites resolve this off the framework thread and two can overlap.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _toeCapAnnounced =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pure mapping from selected mask-option names to existing <c>Masks/&lt;name&gt;.png</c> files under
    /// <paramref name="sidecarRoot"/>, in input order, deduped case-insensitively.
    /// </summary>
    internal static List<string> ResolveMaskPaths(string sidecarRoot, IEnumerable<string>? selectedOptions)
    {
        var result = new List<string>();
        if (selectedOptions == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in selectedOptions)
        {
            if (string.IsNullOrWhiteSpace(option) || IsToeCapOption(option)) continue;   // caps aren't masks
            var stem = Path.Combine(sidecarRoot, MaskSubdir, option);
            var path = ResolveMaskAsset(stem);
            if (path != null && seen.Add(path))
                result.Add(path);
        }
        return result;
    }

    // Resolve a Masks/ asset given its path without extension: .png, then .dds, then .tex. Null if none exist.
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

    /// <summary>Where a mod keeps its own scroll maps. Importers must write here, where
    /// <see cref="ResolveEffectPath"/> looks.</summary>
    internal const string EffectsSubdir = "Effects";

    /// <summary>
    /// Image types listed as effects. Not a decoding constraint: StbImageSharp reads all of these.
    /// </summary>
    private static readonly string[] EffectExtensions =
        [".png", ".dds", ".tex", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif"];

    /// <summary>
    /// The global effects library, <c>&lt;penumbra mods&gt;\Proteus\Effects\</c>, inside the managed mod folder.
    /// Created on demand.
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
    /// Copy the starter effects from <see cref="DefaultEffectsDir"/> into the global effects library, skipping any
    /// file already there. Never overwrites a user's copy.
    /// </summary>
    public void SeedDefaultEffects()
    {
        if (DefaultEffectsDir == null) return;
        var src = DefaultEffectsDir;
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
    /// The scroll maps an overlay can choose from: the mod's own <c>Proteus/Effects/</c> first, then the global
    /// library. Deduped by file name; the mod's copy wins.
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
    /// Resolve an overlay's stored <c>Scroll</c> value to a file: a bare file name is looked up in the mod's Effects/
    /// then the global folder; a relative path is sidecar-relative. Null when nothing matches.
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
    /// Reads the option-name order of the <see cref="MaskGroupName"/> group from the manifest in
    /// <paramref name="modRoot"/> (v4 <c>meta.json</c>, else legacy <c>group_*.json</c>), top to bottom; empty if absent.
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
    /// Penumbra group name → its ordinal: the index in <c>meta.json</c>'s <c>Groups</c> array, or the legacy filename
    /// number (<c>group_002_fabric.json</c> → 2). Lower is higher priority, and this decides which group wins.
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
    /// Orders <paramref name="selected"/> option names by their index in <paramref name="order"/>. Unknown names
    /// follow all known ones. Stable.
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
    /// The ColorTableRows list of the highest-priority active option (last group): the colour picker's edit target.
    /// Creates an empty list in the right place if absent.
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
            // AtomicWrite: this is the authored descriptor and nothing can rebuild it. Interactive retry budget, since
            // callers run on threads the user feels. Synchronous: the editor recomposites straight after and
            // Discover re-reads this file.
            PenumbraModMeta.AtomicWrite(path, json, maxRetries: 2);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to save Proteus metadata for {0}", entry.ModDirectory);
        }
    }

    /// <summary>
    /// Persist the reinforced-toe density of <paramref name="edited"/>, and nothing else, to the mod's sidecar;
    /// false, with a warning, when it could not. For edits under a preset or design, where the in-memory metadata
    /// holds previews: the file is read back and only this field is copied onto it.
    /// </summary>
    public bool SaveToeCapDensity(OverlayEntry entry, IReadOnlyList<OverlayDescriptor> edited)
    {
        try
        {
            var path = Path.Combine(entry.SidecarRoot, MetadataFile);
            var onDisk = File.Exists(path) ? TryParseMetadata(path) : null;
            if (onDisk == null)
            {
                log.Warning("[Proteus] reinforced toe not saved for {0}: its metadata could not be read back",
                    entry.ModDirectory);
                return false;
            }

            int applied = 0;
            foreach (var mem in edited)
            {
                if (TwinOnDisk(entry.Metadata, onDisk, mem) is not { } twin) continue;
                twin.ToeCapDensity = mem.ToeCapDensity;
                applied++;
            }
            if (applied == 0)
            {
                log.Warning("[Proteus] reinforced toe not saved for {0}: the edited option is no longer where "
                          + "the editor found it in the metadata", entry.ModDirectory);
                return false;
            }

            SnapshotDefaults(entry, path);
            var json = JsonSerializer.Serialize(onDisk, ProteusJson.MetadataWrite);
            PenumbraModMeta.AtomicWrite(path, json, maxRetries: 2);   // same budget and reason as SaveMetadata
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to save the reinforced toe for {0}", entry.ModDirectory);
            return false;
        }
    }

    /// <summary>
    /// The descriptor in <paramref name="onDisk"/> at the position <paramref name="d"/> occupies in
    /// <paramref name="inMemory"/>, found by reference. See <see cref="SaveToeCapDensity"/>.
    /// </summary>
    internal static OverlayDescriptor? TwinOnDisk(ProteusMetadata inMemory, ProteusMetadata onDisk, OverlayDescriptor d)
    {
        if (inMemory.Overlays is { } memTop)
        {
            int i = memTop.FindIndex(x => ReferenceEquals(x, d));
            if (i >= 0) return onDisk.Overlays is { } diskTop && i < diskTop.Count ? diskTop[i] : null;
        }

        if (inMemory.OptionGroups is not { } memGroups || onDisk.OptionGroups is not { } diskGroups) return null;
        for (int g = 0; g < memGroups.Count; g++)
            for (int o = 0; o < memGroups[g].Options.Count; o++)
            {
                var memOpt = memGroups[g].Options[o];
                int i = memOpt.Overlays.FindIndex(x => ReferenceEquals(x, d));
                if (i < 0) continue;

                if (g >= diskGroups.Count || o >= diskGroups[g].Options.Count) return null;
                var diskOpt = diskGroups[g].Options[o];
                if (!string.Equals(memOpt.Name, diskOpt.Name, StringComparison.OrdinalIgnoreCase)) return null;
                return i < diskOpt.Overlays.Count ? diskOpt.Overlays[i] : null;
            }
        return null;
    }

    /// <summary>
    /// Preserve the mod's settings as they were before Proteus first wrote to them, for "Reset to defaults". Called
    /// before the save overwrites the file. Best-effort: a failed snapshot never stops the save.
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

    /// <summary>
    /// One mod's sidecar metadata, read straight from its folder; null when it has none or it will not parse. For
    /// callers holding a mod root rather than a discovered entry.
    /// </summary>
    public static ProteusMetadata? TryReadMetadata(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, SidecarSubdir, MetadataFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ProteusMetadata>(File.ReadAllText(path), ProteusJson.MetadataRead)
                : null;
        }
        catch { return null; }
    }
}
