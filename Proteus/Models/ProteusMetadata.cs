using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Proteus;

/// <summary>
/// Root of Proteus/metadata.json inside a Penumbra mod sidecar. A mod uses either the Overlays list
/// (unconditional) or OptionGroups (one per Penumbra option group, applied by user selection).
/// </summary>
public class ProteusMetadata
{
    [JsonPropertyName("FormatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Author")]
    public string Author { get; set; } = string.Empty;

    /// <summary>Unconditional overlays — used when the mod has no option groups.</summary>
    [JsonPropertyName("Overlays")]
    public List<OverlayDescriptor>? Overlays { get; set; }

    /// <summary>Option-gated overlays — used for multi-variant packs.</summary>
    [JsonPropertyName("OptionGroups")]
    public List<OverlayOptionGroup>? OptionGroups { get; set; }

    /// <summary>Starter looks the author ships with the pack. Read-only; editing one forks a user copy.</summary>
    [JsonPropertyName("Presets")]
    public List<ModPreset>? Presets { get; set; }

    /// <summary>Per-row colour table overrides (rows 1–16, FFXIV colorset numbering). Drives diffuse tint and emissive.</summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }

    /// <summary>The colorset shared by all active masks' top layer; null = each mask merges into the overlays' colorsets.</summary>
    [JsonPropertyName("MaskColorTableRows")]
    public List<ColorTableRowPreset>? MaskColorTableRows { get; set; }

    /// <summary>The mask layer's render mode (null = Skin). A mod with gear forces the mask to a Cloth shell.</summary>
    [JsonPropertyName("MaskDescriptor")]
    public OverlayDescriptor? MaskDescriptor { get; set; }

    /// <summary>
    /// Whether this pack wants the ambient-occlusion shadow and normal indent on its coverage (null = no). The user's
    /// per-mod override wins.
    /// </summary>
    [JsonPropertyName("AmbientOcclusion")]
    public bool? AmbientOcclusion { get; set; }

    /// <summary>
    /// Whether this mod's shells span the cleavage instead of following the body into it (null = off). Whole-mod:
    /// spanning one shell but not another drives the mask through the fabric. Body surfaces only.
    /// </summary>
    [JsonPropertyName("BustBridge")]
    public bool? BustBridge { get; set; }

    /// <summary>How far the <see cref="BustBridge"/> relaxes toward the flat span (0–1, default 1; 0 disables).</summary>
    [JsonPropertyName("BustBridgeStrength")]
    public float? BustBridgeStrength { get; set; }

    /// <summary>Whether this mod's shells smooth the nipple out (null = off). Whole-mod; body surfaces only.</summary>
    [JsonPropertyName("SmoothNipples")]
    public bool? SmoothNipples { get; set; }

    /// <summary>How far the <see cref="SmoothNipples"/> region is smoothed (0–1, default 1; 0 disables).</summary>
    [JsonPropertyName("SmoothNipplesStrength")]
    public float? SmoothNipplesStrength { get; set; }

    /// <summary>Whether this mod's shells span the gluteal cleft (null = off). Whole-mod; body surfaces only.</summary>
    [JsonPropertyName("CleftBridge")]
    public bool? CleftBridge { get; set; }

    /// <summary>How far the <see cref="CleftBridge"/> relaxes toward the flat span (0–1, default 1; 0 disables).</summary>
    [JsonPropertyName("CleftBridgeStrength")]
    public float? CleftBridgeStrength { get; set; }

    /// <summary>
    /// Whether this mod's shells flatten the crotch fold (null = off). A body pass: the body is republished and the
    /// shell cut from the result. Body only.
    /// </summary>
    [JsonPropertyName("SmoothFold")]
    public bool? SmoothFold { get; set; }

    /// <summary>How far the <see cref="SmoothFold"/> region is flattened (0–1, default 1; 0 disables).</summary>
    [JsonPropertyName("SmoothFoldStrength")]
    public float? SmoothFoldStrength { get; set; }

    /// <summary>Geometry this pack contributes unconditionally, when it declares no <see cref="ContentGroups"/>.</summary>
    [JsonPropertyName("Content")]
    public List<ContentPiece>? Content { get; set; }

    /// <summary>Option-gated geometry, one entry per Penumbra option group.</summary>
    [JsonPropertyName("ContentGroups")]
    public List<ContentOptionGroup>? ContentGroups { get; set; }

    /// <summary>The importer-synthesized multi-select group that also gates this pack's pieces; null when not needed.</summary>
    [JsonPropertyName("PieceGroupName")]
    public string? PieceGroupName { get; set; }

    /// <summary>Animated glow for a content piece that belongs to no option: the mod-wide fallback.</summary>
    [JsonPropertyName("ContentGlow")]
    public GearSettingsPreset? ContentGlow { get; set; }

    /// <summary>
    /// Per-material colours and glow, keyed by path relative to the mod root (<c>ContentPieceResolver.ContentUnitKey</c>).
    /// A field present here wins over the per-option values, even when empty.
    /// </summary>
    [JsonPropertyName("ContentMaterials")]
    public Dictionary<string, ContentMaterialSettings>? ContentMaterials { get; set; }

    /// <summary>The pack's own IMC show/hide toggles (<see cref="ContentAttributeGroup"/>); null for none.</summary>
    [JsonPropertyName("ContentAttributes")]
    public List<ContentAttributeGroup>? ContentAttributes { get; set; }

    /// <summary>The extra skeletons this pack's pieces need (<see cref="ContentSkeleton"/>); null for none.</summary>
    [JsonPropertyName("ContentSkeletons")]
    public List<ContentSkeleton>? ContentSkeletons { get; set; }

    /// <summary>
    /// The settings for one material path, created on first edit. Copy-and-swap: the composite reads this map from
    /// another thread while the panel writes it.
    /// </summary>
    public ContentMaterialSettings MaterialSettings(string materialRel)
    {
        if (ContentMaterials is { } cur && cur.TryGetValue(materialRel, out var have)) return have;

        var next = ContentMaterials == null
            ? new Dictionary<string, ContentMaterialSettings>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ContentMaterialSettings>(ContentMaterials, StringComparer.OrdinalIgnoreCase);
        var made = new ContentMaterialSettings();
        next[materialRel] = made;
        ContentMaterials = next;
        return made;
    }

    /// <summary>The settings for one material path, or null; never creates an entry.</summary>
    public ContentMaterialSettings? PeekMaterialSettings(string? materialRel)
        => materialRel != null && ContentMaterials is { } m && m.TryGetValue(materialRel, out var s) ? s : null;

    /// <summary>Whether this pack contributes any geometry at all (before selection is resolved).</summary>
    [JsonIgnore]
    public bool HasContent
        => Content is { Count: > 0 } || ContentGroups is { Count: > 0 };
}
