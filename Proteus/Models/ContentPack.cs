using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Proteus;

// ── Content packs (imported .pmp mods that ship their own geometry) ──────────

/// <summary>One model an imported pack contributes, copied verbatim into the carrier accessory, with its materials.</summary>
public class ContentPiece
{
    /// <summary>The .mdl, as a path relative to the MOD ROOT (not the Proteus/ sidecar).</summary>
    [JsonPropertyName("Model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Material leaf name → its .mtrl relative to the mod root; a mesh whose material has no entry is dropped.</summary>
    [JsonPropertyName("Materials")]
    public Dictionary<string, string> Materials { get; set; } = new();

    /// <summary>
    /// The .mtrl backing <paramref name="materialName"/> (leaf-to-leaf, case-insensitive), or null. A method because
    /// System.Text.Json replaces a settable dictionary on load, discarding its comparer.
    /// </summary>
    public string? MaterialFor(string materialName)
    {
        var leaf = materialName.TrimStart('/');
        foreach (var (k, v) in Materials)
            if (string.Equals(k.TrimStart('/'), leaf, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>Which surface the piece belongs to, and so which hosts can carry it (default Body).</summary>
    [JsonPropertyName("Surface")]
    public ShellSurfaceKind Surface { get; set; } = ShellSurfaceKind.Body;

    /// <summary>The part id for a non-body surface ("f0001", "h0133"); empty for the body.</summary>
    [JsonPropertyName("SurfaceId")]
    public string SurfaceId { get; set; } = string.Empty;

    /// <summary>The option in <see cref="ProteusMetadata.PieceGroupName"/> that must also be selected to wear this; null = ungated.</summary>
    [JsonPropertyName("GateOption")]
    public string? GateOption { get; set; }

    /// <summary>Where the piece rides, as a label ("Head", "Body"). Display only.</summary>
    [JsonPropertyName("Slot")]
    public string? Slot { get; set; }

    /// <summary>Equipment model code ("0201") → the model authored for that race; null means <see cref="Model"/> fits everyone.</summary>
    [JsonPropertyName("Models")]
    public Dictionary<string, string>? Models { get; set; }

    /// <summary>
    /// The model authored for <paramref name="modelCode"/> (exact lookup; race fall-through is <c>SecondSkinService</c>'s),
    /// or <see cref="Model"/> for a single-model pack.
    /// </summary>
    public string? ModelFor(string? modelCode)
    {
        if (Models is not { Count: > 0 })
            return string.IsNullOrWhiteSpace(Model) ? null : Model;
        if (modelCode == null) return null;
        foreach (var (k, v) in Models)
            if (string.Equals(k, modelCode, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>The model codes this piece has a variant for.</summary>
    [JsonIgnore]
    public IEnumerable<string> ModelCodes
        => Models is { Count: > 0 } ? Models.Keys : [];

    /// <summary>Every model file this piece can be worn as; the one a character wears is <see cref="ModelFor"/>.</summary>
    public IEnumerable<string> ModelFiles()
    {
        if (Models is { Count: > 0 })
        {
            foreach (var path in Models.Values)
                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(Model)) yield return Model;
    }

    /// <summary>The pack's own options that reveal each material; a material with no gate is drawn unconditionally.</summary>
    [JsonPropertyName("MaterialGates")]
    public List<ContentMaterialGate>? MaterialGates { get; set; }

    /// <summary>
    /// Material leaf → the game paths this pack redirects it under, option-varied paths first. Resolved through
    /// Penumbra so the live option selection wins, falling back to <see cref="Materials"/>.
    /// </summary>
    [JsonPropertyName("MaterialGamePaths")]
    public Dictionary<string, List<string>>? MaterialGamePaths { get; set; }

    /// <summary>
    /// Material leaf → every file this pack can supply it from and its option, best first. Read from the pack's own
    /// options: Penumbra's resolve reports the winner across every installed mod.
    /// </summary>
    [JsonPropertyName("MaterialOptions")]
    public Dictionary<string, List<ContentMaterialSource>>? MaterialOptions { get; set; }

    /// <summary>
    /// Texture game path → every file this pack can supply it from and its option. Keyed by full game path because
    /// two folders can share a filename.
    /// </summary>
    [JsonPropertyName("TextureOptions")]
    public Dictionary<string, List<ContentMaterialSource>>? TextureOptions { get; set; }

    /// <summary>Every file the texture at <paramref name="gamePath"/> can come from; empty for a vanilla texture.</summary>
    public IReadOnlyList<ContentMaterialSource> TextureSourcesFor(string gamePath)
        => TextureOptions is { Count: > 0 } && TextureOptions.TryGetValue(gamePath, out var v) ? v : [];

    /// <summary>Every file <paramref name="materialLeaf"/> can come from, best first; compared like <see cref="MaterialFor"/>.</summary>
    public IReadOnlyList<ContentMaterialSource> SourcesFor(string materialLeaf)
    {
        if (MaterialOptions is not { Count: > 0 }) return [];
        var leaf = materialLeaf.TrimStart('/');
        foreach (var (k, v) in MaterialOptions)
            if (string.Equals(k.TrimStart('/'), leaf, StringComparison.OrdinalIgnoreCase))
                return v;
        return [];
    }

    /// <summary>The game paths <paramref name="materialLeaf"/> may be published under, best first; compared like <see cref="MaterialFor"/>.</summary>
    public IReadOnlyList<string> GamePathsFor(string materialLeaf)
    {
        if (MaterialGamePaths is not { Count: > 0 }) return [];
        var leaf = materialLeaf.TrimStart('/');
        foreach (var (k, v) in MaterialGamePaths)
            if (string.Equals(k.TrimStart('/'), leaf, StringComparison.OrdinalIgnoreCase))
                return v;
        return [];
    }

    /// <summary>Every option that reveals <paramref name="materialLeaf"/>, empty when ungated; compared like <see cref="MaterialFor"/>.</summary>
    public IReadOnlyList<ContentMaterialGate> GatesFor(string materialLeaf)
    {
        if (MaterialGates is not { Count: > 0 }) return [];
        var leaf = materialLeaf.TrimStart('/');
        return [.. MaterialGates.Where(g =>
            string.Equals(g.Material.TrimStart('/'), leaf, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The surface this piece is cut for, as the host chooser understands it.</summary>
    [JsonIgnore]
    public ShellSurfaceKey SurfaceKey
        => new(Surface, Surface == ShellSurfaceKind.Body ? string.Empty : SurfaceId);
}

/// <summary>One file a pack can supply a material from and its option; <see cref="Group"/> null means the pack's default data.</summary>
public class ContentMaterialSource
{
    [JsonPropertyName("Group")]
    public string? Group { get; set; }

    [JsonPropertyName("Option")]
    public string? Option { get; set; }

    /// <summary>The pack file, relative to the mod root.</summary>
    [JsonPropertyName("File")]
    public string File { get; set; } = string.Empty;
}

/// <summary>
/// An extra skeleton a pack's piece needs; the composite re-points the EST entry at the body part actually worn
/// (<c>PenumbraManipulations.EstManipulation</c>).
/// </summary>
public class ContentSkeleton
{
    /// <summary>The Penumbra group and option that must be selected for this to apply; null = unconditional.</summary>
    [JsonPropertyName("Group")]
    public string? Group { get; set; }

    [JsonPropertyName("Option")]
    public string? Option { get; set; }

    /// <summary>The EST slot ("Body", "Head", "Hair", "Face") whose entry is written, not the slot the model rides.</summary>
    [JsonPropertyName("Slot")]
    public string Slot { get; set; } = string.Empty;

    /// <summary>The extra skeleton id; never 0 (0 means none and is dropped at import).</summary>
    [JsonPropertyName("Entry")]
    public int Entry { get; set; }
}

/// <summary>
/// A pack's show/hide toggles for one item as a 10-bit IMC attribute mask (one bit per attribute by position). It
/// belongs to the pack's set, not the host's, so the composite drops submeshes itself (<c>ContentPieceResolver.HiddenAttributes</c>).
/// </summary>
public class ContentAttributeGroup
{
    /// <summary>The Penumbra group whose selection drives this, read live.</summary>
    [JsonPropertyName("Group")]
    public string Group { get; set; } = string.Empty;

    /// <summary>The equipment set the mask belongs to; -1 matches every piece of the mod.</summary>
    [JsonPropertyName("SetId")]
    public int SetId { get; set; } = -1;

    /// <summary>The equipment slot the mask belongs to, as Penumbra names it ("Body", "Legs"); null matches any slot.</summary>
    [JsonPropertyName("Slot")]
    public string? Slot { get; set; }

    /// <summary>The bits set when none of the options are selected.</summary>
    [JsonPropertyName("DefaultMask")]
    public int DefaultMask { get; set; }

    /// <summary>Option name → the bits it CLEARS while selected.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, int> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The default mask with every selected option's bits toggled (xor): packs place bits both inside and outside it.</summary>
    public int MaskFor(IEnumerable<string>? selected)
    {
        int mask = DefaultMask;
        foreach (var name in selected ?? [])
            if (Options.TryGetValue(name, out var bits)) mask ^= bits;
        return mask & 0x3FF;
    }
}

/// <summary>What the colour panel stores for one of an imported pack's materials.</summary>
public class ContentMaterialSettings
{
    /// <summary>Colour table overrides stamped into this material. Null keeps the author's own table.</summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }

    /// <summary>Animated glow for this material. A preset naming no effect clears it; null falls through to the per-option glow.</summary>
    [JsonPropertyName("Glow")]
    public GearSettingsPreset? Glow { get; set; }
}

/// <summary>One of the pack's options that reveals one of its materials; stored because computing it reads the .mdl.</summary>
public class ContentMaterialGate
{
    /// <summary>The model's own material name, leading slash and all.</summary>
    [JsonPropertyName("Material")]
    public string Material { get; set; } = string.Empty;

    [JsonPropertyName("Group")]
    public string Group { get; set; } = string.Empty;

    [JsonPropertyName("Option")]
    public string Option { get; set; } = string.Empty;
}

/// <summary>One Penumbra option's geometry contribution.</summary>
public class ContentOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Pieces")]
    public List<ContentPiece> Pieces { get; set; } = new();

    /// <summary>
    /// Colour table overrides for every material this option's pieces bind; null keeps the authored colorset. Different
    /// rows on the same .mtrl each cost a host material slot.
    /// </summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }

    /// <summary>
    /// Animated glow for this option's materials; null publishes them as authored. Only
    /// <see cref="GearSettingsPreset.Scroll"/> and the four scroll numbers are read.
    /// </summary>
    [JsonPropertyName("Glow")]
    public GearSettingsPreset? Glow { get; set; }
}

/// <summary>Captions for content pieces over a shared colour grid, by the piece's gate rather than its option.</summary>
public static class ContentLabels
{
    /// <summary>
    /// Each piece's label: its gate, else its owning option, else <paramref name="unconditional"/>. Distinct, in
    /// encounter order.
    /// </summary>
    public static List<string> For(
        IEnumerable<(string? Option, IReadOnlyList<ContentPiece> Pieces)> owners, string unconditional)
    {
        var seen = new List<string>();
        foreach (var (option, pieces) in owners)
            foreach (var piece in pieces)
            {
                var label = piece.GateOption ?? option ?? unconditional;
                if (!seen.Contains(label, StringComparer.Ordinal)) seen.Add(label);
            }
        return seen;
    }
}

/// <summary>Maps one Penumbra option group to per-option geometry.</summary>
public class ContentOptionGroup
{
    /// <summary>Must match the group name exactly as it appears in Penumbra.</summary>
    [JsonPropertyName("PenumbraGroupName")]
    public string PenumbraGroupName { get; set; } = string.Empty;

    [JsonPropertyName("Options")]
    public List<ContentOption> Options { get; set; } = new();
}
