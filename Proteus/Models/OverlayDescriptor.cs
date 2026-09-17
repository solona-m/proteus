using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Proteus;

/// <summary>Which surface an overlay renders on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayLayer
{
    /// <summary>The character's own skin, composited into the body material. Always skin.shpk.</summary>
    Skin,

    /// <summary>A "second skin": the skin meshes duplicated, pushed out along their normals and drawn as gear.</summary>
    Gear,
}

/// <summary>How an overlay's normal map combines with the normal already on the material.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NormalMode
{
    /// <summary>Add this overlay's tangent slopes to the ones underneath, so relief stacks. The default.</summary>
    Compound,

    /// <summary>
    /// Overwrite the normal's RGB underneath, weighted by coverage; alpha stays the base's. For an overlay that is
    /// the whole skin.
    /// </summary>
    Replace,
}

/// <summary>
/// How one colour-table sub-row's art combines with what this mod already painted. Every mode but
/// <see cref="Paint"/> is a print: clipped to the mod's other layers, so it paints nothing on bare skin.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RowBlend
{
    /// <summary>Alpha-over: lay this row's art on top of whatever is beneath, by its own alpha. The default.</summary>
    Paint,

    /// <summary><c>dst · src</c>. Darkens: white is invisible, black is opaque.</summary>
    Multiply,

    /// <summary><c>1 − (1−dst)(1−src)</c>. Lightens: black is invisible, white is opaque.</summary>
    Screen,

    /// <summary><see cref="Multiply"/> in the fabric's shadows, <see cref="Screen"/> in its highlights, pivoting at mid grey.</summary>
    Overlay,

    /// <summary><c>min(1, dst + src)</c>. Purely additive; saturates to white where the fabric is already bright.</summary>
    Add,

    /// <summary>Take this row's colour outright, keeping the fabric's alpha.</summary>
    Replace,
}

/// <summary>Describes one set of overlay textures targeting one or more materials.</summary>
public class OverlayDescriptor
{
    /// <summary>Surface this overlay renders on. Defaults to the skin itself.</summary>
    [JsonPropertyName("Layer")]
    public OverlayLayer Layer { get; set; } = OverlayLayer.Skin;

    /// <summary>Gear shader: "character.shpk" (default) or "characterscroll.shpk". Ignored on skin; read <see cref="ShaderPackage"/>.</summary>
    [JsonPropertyName("Shader")]
    public string? Shader { get; set; }

    /// <summary>Skin overlays have no shader choice — the body material is skin.shpk.</summary>
    public const string SkinShader = "skin.shpk";

    /// <summary>Gear shells default to plain character.shpk unless the option names another.</summary>
    public const string DefaultGearShader = "character.shpk";

    /// <summary>The shader this overlay actually renders with, after applying the layer's rules.</summary>
    [JsonIgnore]
    public string ShaderPackage
        => Layer == OverlayLayer.Skin ? SkinShader : (Shader ?? DefaultGearShader);

    /// <summary>When true, the editor stops inferring <see cref="Layer"/>/<see cref="Shader"/> from the features in use.</summary>
    [JsonPropertyName("ManualShaderLock")]
    public bool ManualShaderLock { get; set; }

    /// <summary>Game path(s) of the .mtrl file(s) to composite onto; a single string or a JSON array.</summary>
    [JsonPropertyName("MaterialGamePath")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string> MaterialGamePaths { get; set; } = [];

    /// <summary>Relative path (from Proteus/ sidecar root) to the diffuse overlay PNG. Optional.</summary>
    [JsonPropertyName("Diffuse")]
    public string? Diffuse { get; set; }

    /// <summary>Relative path (from Proteus/ sidecar root) to the normal overlay PNG. Optional.</summary>
    [JsonPropertyName("Normal")]
    public string? Normal { get; set; }

    /// <summary>How <see cref="Normal"/> combines with the material's normal; default <see cref="NormalMode.Compound"/>.</summary>
    [JsonPropertyName("NormalMode")]
    public NormalMode NormalMode { get; set; } = NormalMode.Compound;

    /// <summary>Relative path (from Proteus/ sidecar root) to the mask overlay PNG. Optional.</summary>
    [JsonPropertyName("Mask")]
    public string? Mask { get; set; }

    /// <summary>
    /// Relative path to the index PNG (_id.png). Red selects the row pair (value/17 → 0–15); green blends sub-row
    /// A (255) and B (0).
    /// </summary>
    [JsonPropertyName("Index")]
    public string? Index { get; set; }

    /// <summary>Relative path to the scrolling emissive map ("_o"). Gear layer with characterscroll.shpk only.</summary>
    [JsonPropertyName("Scroll")]
    public string? Scroll { get; set; }

    /// <summary>Scroll speed per axis (~0.01 typical, negative reverses); null = default. Characterscroll only.</summary>
    [JsonPropertyName("ScrollSpeedX")]
    public float? ScrollSpeedX { get; set; }

    [JsonPropertyName("ScrollSpeedY")]
    public float? ScrollSpeedY { get; set; }

    /// <summary>How many times the scroll map repeats per axis (1 = once); null = default. Characterscroll only.</summary>
    [JsonPropertyName("ScrollTilingX")]
    public float? ScrollTilingX { get; set; }

    [JsonPropertyName("ScrollTilingY")]
    public float? ScrollTilingY { get; set; }

    /// <summary>For a normal-only overlay, whether to synthesize a diffuse tint from its coverage and Row 16. Default true.</summary>
    [JsonPropertyName("GenerateDiffuse")]
    public bool GenerateDiffuse { get; set; } = true;

    /// <summary>
    /// How strongly to mask skin tone out of this overlay's opaque pixels (0–1); null = full, 0 = tone shows through.
    /// Multiplies the user's global setting. Diffuse overlays only.
    /// </summary>
    [JsonPropertyName("SkinToneMask")]
    public float? SkinToneMask { get; set; }

    /// <summary>
    /// Path to a greyscale body-UV map of where the shell is webbed into a smooth toe cap (white = capped). Gear only;
    /// wins over the reserved "Toe Cap" mask (SidecarDiscoveryService.ToeCapOptionName).
    /// </summary>
    [JsonPropertyName("ToeCap")]
    public string? ToeCap { get; set; }

    /// <summary>How far the <see cref="ToeCap"/> region inflates toward its smoothed envelope (0–1, default 1; 0 disables).</summary>
    [JsonPropertyName("ToeCapStrength")]
    public float? ToeCapStrength { get; set; }

    /// <summary>
    /// How much denser the capped toe renders (a reinforced toe), 0–100, default 0 = off. Uses the curve of a positive
    /// <see cref="ColorTableSubRowPreset.Opacity"/>, weighted by the cap map.
    /// </summary>
    [JsonPropertyName("ToeCapDensity")]
    public int ToeCapDensity { get; set; }

    /// <summary>UV space the PNGs were painted for ("bibo", "gen3", "gen2"), remapped when it differs; null = the target's.</summary>
    [JsonPropertyName("SourceBodyType")]
    public string? SourceBodyType { get; set; }

    /// <summary>
    /// The author's statement that this art is one-sided and must not be folded (default false). On a mirrored
    /// surface it renders through an un-mirrored shell. Declared, never measured.
    /// </summary>
    [JsonPropertyName("AsymmetricArt")]
    public bool? AsymmetricArt { get; set; }

    /// <summary>
    /// Transient: the synthesized top gear shell for a mod's active masks, coloured by
    /// <see cref="ProteusMetadata.MaskColorTableRows"/>; SecondSkinService skips the mask merge for it.
    /// </summary>
    [JsonIgnore]
    public bool IsMaskShell { get; set; }
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

    /// <summary>Per-row colour overrides for this option's overlays; null falls back to the top-level ColorTableRows.</summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }
}
