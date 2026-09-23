using System.Text.Json.Serialization;

namespace Proteus;

/// <summary>
/// Resolved texture game paths extracted from a parsed .mtrl file. <paramref name="Index"/> is the material's own
/// <c>_id</c> sampler; skin materials never declare one.
/// </summary>
/// <param name="Parsed">
/// Whether the walk reached the sampler array. The parser is fail-open, so callers that act on the ABSENCE of a
/// texture must check this first.
/// </param>
/// <param name="HasColorTable">
/// Whether the material carries a Dawntrail 32×64 colour table; without one, colour edits are discarded by
/// <c>GearMaterialWriter.PatchColorTable</c>.
/// </param>
public record MtrlTexturePaths(
    string? Diffuse,
    string? Normal,
    string? Mask,
    string? Index = null,
    bool Parsed = false,
    bool HasColorTable = false
);

// ── Color table types ────────────────────────────────────────────────────────

/// <summary>A colour table row override in metadata.json. Row is 1-based (1–16).</summary>
public class ColorTableRowPreset
{
    [JsonPropertyName("Row")]
    public int Row { get; set; }

    [JsonPropertyName("SubRowA")]
    public ColorTableSubRowPreset? SubRowA { get; set; }

    [JsonPropertyName("SubRowB")]
    public ColorTableSubRowPreset? SubRowB { get; set; }

    /// <summary>Deep copy.</summary>
    public ColorTableRowPreset Clone() => new() { Row = Row, SubRowA = SubRowA?.Clone(), SubRowB = SubRowB?.Clone() };
}

public class ColorTableSubRowPreset
{
    /// <summary>Hex color string, e.g. "#FF0000" or "#F00". White if null.</summary>
    [JsonPropertyName("Diffuse")]
    public string? Diffuse { get; set; }

    /// <summary>Emissive intensity 0–1. Zero means no glow.</summary>
    [JsonPropertyName("Emissive")]
    public float Emissive { get; set; } = 0f;

    /// <summary>Gear layer only. Glow colour, independent of <see cref="Diffuse"/>; defaults to the diffuse colour.</summary>
    [JsonPropertyName("EmissiveColor")]
    public string? EmissiveColor { get; set; }

    /// <summary>
    /// Opacity adjustment −100…100. Negative fades the overlay toward transparent;
    /// positive pushes semi-transparent pixels toward fully opaque. Zero = no change.
    /// </summary>
    [JsonPropertyName("Opacity")]
    public int Opacity { get; set; } = 0;

    /// <summary>
    /// How this region's art combines with what this mod already painted (default <see cref="RowBlend.Paint"/>).
    /// For a print, <see cref="Opacity"/> is its strength.
    /// </summary>
    [JsonPropertyName("Blend")]
    public RowBlend Blend { get; set; } = RowBlend.Paint;

    /// <summary>
    /// What a print lands on. Null = <see cref="PrintTarget.OwnPaint"/>: rows saved before the choice existed were all
    /// clipped to their mod's paint and must keep rendering that way. The editor fills it in when a row becomes a print.
    /// </summary>
    [JsonPropertyName("PrintOnto")]
    public PrintTarget? PrintOnto { get; set; }

    /// <summary>
    /// How much of this region's glow the scene's light takes away, 0–1 (default 0 = unconditional): the emissive
    /// scales by <c>1 − LightResponse × light</c>. Per sub-row; needs <see cref="Emissive"/> above zero.
    /// </summary>
    [JsonPropertyName("LightResponse")]
    public float? LightResponse { get; set; }

    /// <summary>
    /// Whether this region's opacity (the shell normal's blue channel) follows its glow, so where light takes the
    /// glow away only skin is left. Fades only as far as the glow does under <see cref="LightResponse"/>.
    /// </summary>
    [JsonPropertyName("HideInLight")]
    public bool HideInLight { get; set; }

    /// <summary>
    /// Gear layer only. Sphere map slice of chara/common/texture/sphere_d_array.tex. Needs a non-zero
    /// <see cref="SphereIntensity"/>; does not work under characterscroll.shpk.
    /// </summary>
    [JsonPropertyName("SphereMap")]
    public int? SphereMap { get; set; }

    /// <summary>Gear layer only. How strongly the sphere map blends in (0–1).</summary>
    [JsonPropertyName("SphereIntensity")]
    public float? SphereIntensity { get; set; }

    /// <summary>Gear layer only. Specular colour. Null keeps the template's value.</summary>
    [JsonPropertyName("Specular")]
    public string? Specular { get; set; }

    /// <summary>Gear layer only. Surface roughness (0–1). Null keeps the shader default.</summary>
    [JsonPropertyName("Roughness")]
    public float? Roughness { get; set; }

    /// <summary>Gear layer only. Metalness (0–1). Null keeps the shader default.</summary>
    [JsonPropertyName("Metalness")]
    public float? Metalness { get; set; }

    /// <summary>
    /// Gear layer only. Fabric weave slice of chara/common/texture/tile_norm_array.tex (0–63). Null = no weave;
    /// zero is a real tile, not "none".
    /// </summary>
    [JsonPropertyName("Tile")]
    public int? Tile { get; set; }

    /// <summary>Gear layer only. How strongly the weave shows (0–1, null = full). No effect without a <see cref="Tile"/>.</summary>
    [JsonPropertyName("TileStrength")]
    public float? TileStrength { get; set; }

    /// <summary>
    /// Gear layer only. Weave repeats per UV axis (null = game default 16; higher is finer). No effect without a
    /// <see cref="Tile"/>. Scalars because System.Text.Json would serialise a ValueTuple as an empty object.
    /// </summary>
    [JsonPropertyName("TileScaleU")]
    public float? TileScaleU { get; set; }

    /// <inheritdoc cref="TileScaleU"/>
    [JsonPropertyName("TileScaleV")]
    public float? TileScaleV { get; set; }

    /// <summary>Copy; every member is a value type or string, so the shallow copy is a deep one.</summary>
    public ColorTableSubRowPreset Clone() => (ColorTableSubRowPreset)MemberwiseClone();
}

/// <summary>Runtime (0-based) representation of a single color table sub-row.</summary>
public class ColorTableSubRow
{
    public float DiffuseR { get; set; } = 1f;
    public float DiffuseG { get; set; } = 1f;
    public float DiffuseB { get; set; } = 1f;
    public float Emissive { get; set; } = 0f;
    public int   Opacity  { get; set; } = 0;

    /// <summary>How this row composites; <see cref="RowBlend.Paint"/> for an unconfigured cell.</summary>
    public RowBlend Blend { get; set; } = RowBlend.Paint;

    /// <summary>A print that blends with everything beneath it instead of only its own mod's paint.</summary>
    public bool OntoBeneath { get; set; }
}

/// <summary>Runtime pair of sub-rows A and B for one color table row pair.</summary>
public class ColorTableRowOverride
{
    public ColorTableSubRow A { get; set; } = new();
    public ColorTableSubRow B { get; set; } = new();
}
