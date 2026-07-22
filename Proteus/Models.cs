using System;
using System.Collections.Generic;
using System.Text.Json;
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

    /// <summary>
    /// Per-row color table overrides (rows 1–16, matching FFXIV colorset numbering).
    /// Written by both mod authors and the Proteus UI. Drives diffuse tint and emissive.
    /// </summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }
}

/// <summary>Which surface an overlay renders on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayLayer
{
    /// <summary>The character's own skin, composited into the body material. Always skin.shpk.</summary>
    Skin,

    /// <summary>
    /// A "second skin": the body's skin meshes duplicated, pushed out along their normals, and drawn as
    /// gear so they can run a full gear shader (color table, sphere maps, metalness, scrolling emissive)
    /// — none of which skin.shpk offers. Shells stack, gated by their normal map's blue channel.
    /// </summary>
    Gear,
}

/// <summary>Describes one set of overlay textures targeting one or more materials.</summary>
public class OverlayDescriptor
{
    /// <summary>Surface this overlay renders on. Defaults to the skin itself.</summary>
    [JsonPropertyName("Layer")]
    public OverlayLayer Layer { get; set; } = OverlayLayer.Skin;

    /// <summary>
    /// Shader package for a <see cref="OverlayLayer.Gear"/> overlay — e.g. "character.shpk" (default)
    /// or "characterscroll.shpk" (adds a time-animated scrolling emissive driven by an _o texture).
    /// Ignored on the skin layer, which is always skin.shpk. Prefer <see cref="ShaderPackage"/>.
    /// </summary>
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

    /// <summary>
    /// When true, the editor stops auto-inferring <see cref="Layer"/>/<see cref="Shader"/> from the
    /// features in use — the user pinned the render mode by hand (Advanced). Off = the mode follows the
    /// features (a sphere map or metal ⇒ Cloth gear, a scroll effect ⇒ animated-glow gear).
    /// </summary>
    [JsonPropertyName("ManualShaderLock")]
    public bool ManualShaderLock { get; set; }

    /// <summary>
    /// Penumbra game path(s) of the .mtrl file(s). Accepts a single string or a JSON array.
    /// The same overlay textures are composited onto every listed material.
    /// </summary>
    [JsonPropertyName("MaterialGamePath")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string> MaterialGamePaths { get; set; } = [];

    /// <summary>Relative path (from Proteus/ sidecar root) to the diffuse overlay PNG. Optional.</summary>
    [JsonPropertyName("Diffuse")]
    public string? Diffuse { get; set; }

    /// <summary>Relative path (from Proteus/ sidecar root) to the normal overlay PNG. Optional.</summary>
    [JsonPropertyName("Normal")]
    public string? Normal { get; set; }

    /// <summary>Relative path (from Proteus/ sidecar root) to the mask overlay PNG. Optional.</summary>
    [JsonPropertyName("Mask")]
    public string? Mask { get; set; }

    /// <summary>
    /// Relative path (from Proteus/ sidecar root) to the index PNG (_id.png).
    /// Red channel selects color table row pair (value/17 → 0–15).
    /// Green channel blends sub-row A (255) and sub-row B (0).
    /// </summary>
    [JsonPropertyName("Index")]
    public string? Index { get; set; }

    /// <summary>
    /// Gear layer only. Relative path to the scrolling emissive map (vanilla calls this texture "_catc";
    /// mods often name it "_o"). Its color and intensity become the glow, animated by the shader from
    /// global time. Requires Shader = "characterscroll.shpk"; ignored otherwise.
    /// </summary>
    [JsonPropertyName("Scroll")]
    public string? Scroll { get; set; }

    /// <summary>
    /// Gear + characterscroll only. How fast the scroll map flows, per axis. These live in material
    /// constants, and vanilla ships them at ZERO — so without a speed the pattern sits still. ~0.01 is a
    /// typical rate; negative reverses the direction. Null = the default.
    /// </summary>
    [JsonPropertyName("ScrollSpeedX")]
    public float? ScrollSpeedX { get; set; }

    [JsonPropertyName("ScrollSpeedY")]
    public float? ScrollSpeedY { get; set; }

    /// <summary>
    /// Gear + characterscroll only. How many times the scroll map repeats across the surface, per axis.
    /// 1 = once. Null = the default.
    /// </summary>
    [JsonPropertyName("ScrollTilingX")]
    public float? ScrollTilingX { get; set; }

    [JsonPropertyName("ScrollTilingY")]
    public float? ScrollTilingY { get; set; }

    /// <summary>
    /// For a normal-only overlay (no Diffuse), whether to synthesize a diffuse tint from the
    /// normal's coverage and Row 16's color. Default true (legacy behaviour). Set false when the
    /// overlay should only touch the normal/mask and leave the skin diffuse untouched
    /// (e.g. a wetness normal+mask). Ignored when a Diffuse overlay is present.
    /// </summary>
    [JsonPropertyName("GenerateDiffuse")]
    public bool GenerateDiffuse { get; set; } = true;

    /// <summary>
    /// How strongly to mask the character's skin tone out of this overlay's opaque pixels (0–1).
    /// Omitted (null) = full masking — the default; a bright opaque overlay renders at its authored
    /// color on any skin tone. 0 = no masking: skin tone shows through fully (use for tattoos,
    /// decals, or anything meant to sit on the skin and take its color). Multiplies the user's global
    /// "Skin-tint suppression" setting, so an author's 0 always wins. Only affects diffuse overlays.
    /// </summary>
    [JsonPropertyName("SkinToneMask")]
    public float? SkinToneMask { get; set; }

    /// <summary>
    /// UV space the overlay PNGs were painted for: "bibo", "gen3", or "gen2".
    /// When set and different from the target material's body type (inferred from the
    /// material path suffix), Proteus remaps overlay pixels before compositing.
    /// Omit (null) when the overlay is already in the target body's UV space.
    /// </summary>
    [JsonPropertyName("SourceBodyType")]
    public string? SourceBodyType { get; set; }
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

    /// <summary>
    /// Per-row color overrides for this option's overlays.
    /// Overrides the top-level ColorTableRows when present; falls back to top-level when null.
    /// </summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }
}

/// <summary>Resolved texture game paths extracted from a parsed .mtrl file.</summary>
public record MtrlTexturePaths(
    string? Diffuse,
    string? Normal,
    string? Mask
);

// ── Color table types ────────────────────────────────────────────────────────

/// <summary>
/// Serialised form of a single color table row override stored in metadata.json.
/// Row is 1-based (1–16) matching what FFXIV modders know.
/// </summary>
public class ColorTableRowPreset
{
    [JsonPropertyName("Row")]
    public int Row { get; set; }

    [JsonPropertyName("SubRowA")]
    public ColorTableSubRowPreset? SubRowA { get; set; }

    [JsonPropertyName("SubRowB")]
    public ColorTableSubRowPreset? SubRowB { get; set; }
}

public class ColorTableSubRowPreset
{
    /// <summary>Hex color string, e.g. "#FF0000" or "#F00". White if null.</summary>
    [JsonPropertyName("Diffuse")]
    public string? Diffuse { get; set; }

    /// <summary>Emissive intensity 0–1. Zero means no glow.</summary>
    [JsonPropertyName("Emissive")]
    public float Emissive { get; set; } = 0f;

    /// <summary>
    /// Gear layer only. Glow colour, independent of <see cref="Diffuse"/>. Defaults to the diffuse
    /// colour when omitted.
    ///
    /// These have to be separate: a scrolling-emissive material typically wants a nearly BLACK diffuse
    /// (so the glow reads against it) with a WHITE emissive — e.g. Luci's shirt is diffuse (0.08,0,0),
    /// emissive (1,1,1). Deriving the glow from the diffuse cannot express that.
    /// </summary>
    [JsonPropertyName("EmissiveColor")]
    public string? EmissiveColor { get; set; }

    /// <summary>
    /// Opacity adjustment −100…100. Negative fades the overlay toward transparent;
    /// positive pushes semi-transparent pixels toward fully opaque. Zero = no change.
    /// </summary>
    [JsonPropertyName("Opacity")]
    public int Opacity { get; set; } = 0;

    /// <summary>
    /// Gear layer only. Sphere map to reflect on this row — a slice of the game's shared
    /// chara/common/texture/sphere_d_array.tex. Needs no texture of our own.
    /// Has no effect unless <see cref="SphereIntensity"/> is also non-zero, and does not work under
    /// characterscroll.shpk (use the default character.shpk).
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
}

/// <summary>Runtime (0-based) representation of a single color table sub-row.</summary>
public class ColorTableSubRow
{
    public float DiffuseR { get; set; } = 1f;
    public float DiffuseG { get; set; } = 1f;
    public float DiffuseB { get; set; } = 1f;
    public float Emissive { get; set; } = 0f;
    public int   Opacity  { get; set; } = 0;
}

/// <summary>Runtime pair of sub-rows A and B for one color table row pair.</summary>
public class ColorTableRowOverride
{
    public ColorTableSubRow A { get; set; } = new();
    public ColorTableSubRow B { get; set; } = new();
}

/// <summary>
/// Non-persistent per-mod color override pushed by the design-binding system into the compositor.
/// Mirrors the metadata color structure (a top-level row list plus per-group/per-option lists), but
/// is applied only at composite time — metadata.json is never modified. Stored in design_bindings.json.
/// </summary>
public class OverlayColorOverride
{
    [JsonPropertyName("Top")]
    public List<ColorTableRowPreset>? Top { get; set; }

    /// <summary>group → option → rows.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, Dictionary<string, List<ColorTableRowPreset>>>? Options { get; set; }

    /// <summary>
    /// Resolve the rows for an overlay: the matching option's rows if present, else the top-level rows.
    /// Returns null when nothing is stored, so callers can fall back to the live metadata colors.
    /// </summary>
    public List<ColorTableRowPreset>? Resolve(string? group, string? option)
    {
        if (group != null && option != null && Options != null
            && Options.TryGetValue(group, out var opts) && opts.TryGetValue(option, out var rows))
            return rows;
        return Top;
    }
}

/// <summary>
/// The gear-layer settings of one overlay option: which layer and shader it renders with, and its
/// scrolling-effect setup. These live on <see cref="OverlayDescriptor"/> rather than the colour rows,
/// so a design binding has to capture them separately from the colours.
/// </summary>
public class GearSettingsPreset
{
    [JsonPropertyName("Layer")]
    public OverlayLayer? Layer { get; set; }

    [JsonPropertyName("Shader")]
    public string? Shader { get; set; }

    [JsonPropertyName("Scroll")]
    public string? Scroll { get; set; }

    [JsonPropertyName("ScrollSpeedX")]
    public float? ScrollSpeedX { get; set; }

    [JsonPropertyName("ScrollSpeedY")]
    public float? ScrollSpeedY { get; set; }

    [JsonPropertyName("ScrollTilingX")]
    public float? ScrollTilingX { get; set; }

    [JsonPropertyName("ScrollTilingY")]
    public float? ScrollTilingY { get; set; }

    [JsonPropertyName("ManualShaderLock")]
    public bool? ManualShaderLock { get; set; }

    /// <summary>Snapshot an overlay's gear settings.</summary>
    public static GearSettingsPreset From(OverlayDescriptor d) => new()
    {
        Layer = d.Layer,
        Shader = d.Shader,
        Scroll = d.Scroll,
        ScrollSpeedX = d.ScrollSpeedX,
        ScrollSpeedY = d.ScrollSpeedY,
        ScrollTilingX = d.ScrollTilingX,
        ScrollTilingY = d.ScrollTilingY,
        ManualShaderLock = d.ManualShaderLock,
    };

    /// <summary>Apply onto a descriptor (used on a clone, so metadata.json is never mutated).</summary>
    public void ApplyTo(OverlayDescriptor d)
    {
        if (Layer is { } l) d.Layer = l;
        d.Shader = Shader;
        d.Scroll = Scroll;
        d.ScrollSpeedX = ScrollSpeedX;
        d.ScrollSpeedY = ScrollSpeedY;
        d.ScrollTilingX = ScrollTilingX;
        d.ScrollTilingY = ScrollTilingY;
        d.ManualShaderLock = ManualShaderLock ?? false;
    }
}

/// <summary>
/// Per-mod gear-settings override pushed by the design-binding system into the compositor — the same
/// shape as <see cref="OverlayColorOverride"/>, and applied the same way: only at composite time, onto a
/// copy of the descriptor. metadata.json is never modified.
/// </summary>
public class OverlayGearOverride
{
    [JsonPropertyName("Top")]
    public GearSettingsPreset? Top { get; set; }

    /// <summary>group → option → settings.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, Dictionary<string, GearSettingsPreset>>? Options { get; set; }

    public GearSettingsPreset? Resolve(string? group, string? option)
    {
        if (group != null && option != null && Options != null
            && Options.TryGetValue(group, out var opts) && opts.TryGetValue(option, out var s))
            return s;
        return Top;
    }
}

/// <summary>
/// Deserialises MaterialGamePath as either a JSON string or a JSON array of strings.
/// Serialises a single-element list back as a plain string for compact output.
/// </summary>
public class StringOrStringArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                if (reader.TokenType == JsonTokenType.String)
                    list.Add(reader.GetString()!);
            return list;
        }

        throw new JsonException($"Expected string or array for MaterialGamePath, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
            writer.WriteStringValue(value[0]);
        else
        {
            writer.WriteStartArray();
            foreach (var s in value)
                writer.WriteStringValue(s);
            writer.WriteEndArray();
        }
    }
}
