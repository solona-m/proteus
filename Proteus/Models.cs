using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Proteus;

/// <summary>
/// Root of Proteus/metadata.json inside a Penumbra mod sidecar.
/// A mod may use either the simple Overlays list (applied unconditionally)
/// or OptionGroups (one group per Penumbra option group, applied based on user selection).
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
}

/// <summary>Describes one set of overlay textures targeting a single material.</summary>
public class OverlayDescriptor
{
    /// <summary>
    /// Penumbra game path of the .mtrl file. Proteus resolves this via ResolvePlayerPath
    /// (body mod overrides respected) then parses the mtrl to find the actual texture paths.
    /// </summary>
    [JsonPropertyName("MaterialGamePath")]
    public string MaterialGamePath { get; set; } = string.Empty;

    /// <summary>Relative path (from Proteus/ sidecar root) to the diffuse overlay PNG. Optional.</summary>
    [JsonPropertyName("Diffuse")]
    public string? Diffuse { get; set; }

    /// <summary>Relative path (from Proteus/ sidecar root) to the normal overlay PNG. Optional.</summary>
    [JsonPropertyName("Normal")]
    public string? Normal { get; set; }

    /// <summary>Relative path (from Proteus/ sidecar root) to the mask overlay PNG. Optional.</summary>
    [JsonPropertyName("Mask")]
    public string? Mask { get; set; }
}

/// <summary>Maps one Penumbra option group to per-option overlay sets.</summary>
public class OverlayOptionGroup
{
    /// <summary>Must match the group name exactly as it appears in Penumbra.</summary>
    [JsonPropertyName("PenumbraGroupName")]
    public string PenumbraGroupName { get; set; } = string.Empty;

    [JsonPropertyName("Options")]
    public List<OverlayOption> Options { get; set; } = new();
}

public class OverlayOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Overlays")]
    public List<OverlayDescriptor> Overlays { get; set; } = new();
}

/// <summary>Resolved texture game paths extracted from a parsed .mtrl file.</summary>
public record MtrlTexturePaths(
    string? Diffuse,
    string? Normal,
    string? Mask
);
