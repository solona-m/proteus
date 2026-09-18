using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Proteus.Services;

namespace Proteus;

/// <summary>Where a preset came from, which decides whether it can be edited in place.</summary>
public enum PresetSource
{
    /// <summary>Saved by the wearer, in this machine's presets.json. Editable.</summary>
    User,

    /// <summary>Shipped by the mod author in the sidecar's metadata.json. Read-only; editing forks a User copy.</summary>
    Pack,
}

/// <summary>
/// A named look for one mod: ticked options, colorsets, layer/glow settings and overlay stack order. The portable
/// subset of <see cref="Services.ProteusModBinding"/>: Enabled and Priority belong to a collection, not a look.
/// </summary>
public class ModPreset
{
    /// <summary>
    /// Empty until stored, never a fresh Guid: an initializer would give an id-less pack preset a new id on every
    /// read. <c>PresetService.PackId</c> derives a stable one; <c>PresetService.Add</c> mints user ids.
    /// </summary>
    [JsonPropertyName("Id")]
    public Guid Id { get; set; }

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    /// <summary>Not serialised: implied by the store, so a shared file cannot claim to come from a pack.</summary>
    [JsonIgnore]
    public PresetSource Source { get; set; } = PresetSource.User;

    /// <summary>
    /// The mod this look was made for, by display name and author. Names, not the mod directory: a directory is
    /// local to one machine.
    /// </summary>
    [JsonPropertyName("ModName")]
    public string? ModName { get; set; }

    [JsonPropertyName("ModAuthor")]
    public string? ModAuthor { get; set; }

    [JsonPropertyName("CreatedUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("LastEditUtc")]
    public DateTime LastEditUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Penumbra option group name → the option names ticked in it.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, List<string>> Options { get; set; } = new();

    [JsonPropertyName("Colors")]
    public OverlayColorOverride Colors { get; set; } = new();

    [JsonPropertyName("Gear")]
    public OverlayGearOverride Gear { get; set; } = new();

    /// <summary>The overlay stack order top-first (<see cref="Configuration.ModStackEntry"/> keys); empty keeps the global order.</summary>
    [JsonPropertyName("StackOrder")]
    public List<string> StackOrder { get; set; } = new();

    /// <summary>A deep copy; everything that hands a preset out goes through here so no two owners share a row list.</summary>
    public ModPreset Clone() => new()
    {
        Id          = Id,
        Name        = Name,
        Description = Description,
        Source      = Source,
        ModName     = ModName,
        ModAuthor   = ModAuthor,
        CreatedUtc  = CreatedUtc,
        LastEditUtc = LastEditUtc,
        Options     = Options.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
        Colors      = CloneColors(Colors),
        Gear        = CloneGear(Gear),
        StackOrder  = new List<string>(StackOrder),
    };

    private static OverlayColorOverride CloneColors(OverlayColorOverride o) => new()
    {
        Top     = o.Top?.Select(r => r.Clone()).ToList(),
        Mask    = o.Mask?.Select(r => r.Clone()).ToList(),
        Options = o.Options?.ToDictionary(
            g => g.Key,
            g => g.Value.ToDictionary(x => x.Key, x => x.Value.Select(r => r.Clone()).ToList())),
        Materials = o.Materials?.ToDictionary(
            m => m.Key, m => m.Value.Select(r => r.Clone()).ToList(), StringComparer.OrdinalIgnoreCase),
    };

    private static OverlayGearOverride CloneGear(OverlayGearOverride o) => new()
    {
        Top     = o.Top?.Clone(),
        Mask    = o.Mask?.Clone(),
        Content = o.Content?.Clone(),
        Options = o.Options?.ToDictionary(
            g => g.Key,
            g => g.Value.ToDictionary(x => x.Key, x => x.Value.Clone())),
        Materials = o.Materials?.ToDictionary(
            m => m.Key, m => m.Value.Clone(), StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// Keeps an override's per-material map keyed the way material paths are compared everywhere else
/// (<see cref="ProteusMetadata.ContentMaterials"/>, <see cref="ContentPiece.MaterialFor"/>): case-insensitively.
/// </summary>
/// <remarks>
/// It lives in the property setter because the map does not reach the compositor the way it was built: every
/// adopt round-trips it through <c>design_bindings.json</c> (and a preset through its share code), and
/// System.Text.Json hands back a dictionary with the DEFAULT ordinal comparer. A pack whose metadata spells a
/// material path in different case from the model's binding would then miss here and hit in the metadata — the
/// override silently shadowed again, which is the whole defect this map exists to fix.
/// </remarks>
internal static class MaterialMap
{
    public static Dictionary<string, T>? CaseInsensitive<T>(Dictionary<string, T>? map)
        => map == null || ReferenceEquals(map.Comparer, StringComparer.OrdinalIgnoreCase)
            ? map
            : new Dictionary<string, T>(map, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Per-mod colour override from a design binding, applied only at composite time; metadata.json is never modified.
/// Stored in design_bindings.json.
/// </summary>
public class OverlayColorOverride
{
    [JsonPropertyName("Top")]
    public List<ColorTableRowPreset>? Top { get; set; }

    /// <summary>group → option → rows.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, Dictionary<string, List<ColorTableRowPreset>>>? Options { get; set; }

    /// <summary>
    /// The mod's shared "Masks" colorset (<see cref="ProteusMetadata.MaskColorTableRows"/>), which has no option
    /// group. Null falls back to the live metadata mask colours.
    /// </summary>
    [JsonPropertyName("Mask")]
    public List<ColorTableRowPreset>? Mask { get; set; }

    /// <summary>
    /// An imported pack's rows per MATERIAL, keyed exactly like <see cref="ProteusMetadata.ContentMaterials"/>.
    /// The colour panel edits a content pack one material at a time, so a binding that could only answer per
    /// option would be shadowed by the mod's own per-material rows and change nothing — see
    /// <see cref="ContentSettingLevels"/>.
    /// </summary>
    [JsonPropertyName("Materials")]
    public Dictionary<string, List<ColorTableRowPreset>>? Materials
    {
        get => materials;
        set => materials = MaterialMap.CaseInsensitive(value);
    }

    private Dictionary<string, List<ColorTableRowPreset>>? materials;

    /// <summary>This override's rows for one content material, or null when it stores none.</summary>
    public List<ColorTableRowPreset>? ResolveMaterial(string? materialRel)
        => materialRel != null && materials != null && materials.TryGetValue(materialRel, out var rows)
            ? rows : null;

    /// <summary>The option's rows, else the top-level rows; null when nothing is stored.</summary>
    public List<ColorTableRowPreset>? Resolve(string? group, string? option)
    {
        if (group != null && option != null && Options != null
            && Options.TryGetValue(group, out var opts) && opts.TryGetValue(option, out var rows))
            return rows;
        return Top;
    }
}

/// <summary>
/// The gear-layer settings of one overlay option (layer, shader, scroll effect). They live on
/// <see cref="OverlayDescriptor"/>, so a design binding captures them separately from the colours.
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

    /// <summary>The overlay's skin-tint suppression, 0–1, or null for the default (full). Skin layer only.</summary>
    [JsonPropertyName("SkinToneMask")]
    public float? SkinToneMask { get; set; }

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
        // GetEditableGearOverride seeds from this; omitting it would reset an author's 0 to full suppression.
        SkinToneMask = d.SkinToneMask,
    };

    /// <summary>An independent copy, so editing a binding never moves the sidecar's own settings.</summary>
    public GearSettingsPreset Clone() => new()
    {
        Layer = Layer,
        Shader = Shader,
        Scroll = Scroll,
        ScrollSpeedX = ScrollSpeedX,
        ScrollSpeedY = ScrollSpeedY,
        ScrollTilingX = ScrollTilingX,
        ScrollTilingY = ScrollTilingY,
        ManualShaderLock = ManualShaderLock,
        SkinToneMask = SkinToneMask,
    };

    /// <summary>
    /// The scroll settings as one comparable string, or null when there is no glow. Part of a content material's
    /// merge key, so it covers the effect and its numbers but not Layer, Shader or ManualShaderLock.
    /// </summary>
    public string? GlowKey()
        => string.IsNullOrEmpty(Scroll)
            ? null
            : string.Join(' ', Scroll,
                ScrollSpeedX?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
                ScrollSpeedY?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
                ScrollTilingX?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
                ScrollTilingY?.ToString("R", CultureInfo.InvariantCulture) ?? "-");

    /// <summary>The scroll settings with defaults filled in; null when this preset names no effect.</summary>
    public ScrollSettings? ToScrollSettings()
        => string.IsNullOrEmpty(Scroll)
            ? null
            : new ScrollSettings(
                ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                ScrollSpeedY ?? ScrollSettings.Default.SpeedY,
                ScrollTilingX ?? ScrollSettings.Default.TilingX,
                ScrollTilingY ?? ScrollSettings.Default.TilingY);

    /// <summary>
    /// Copy just the scroll settings out of <paramref name="from"/>: a content glow must not touch the layer and
    /// shader an overlay of the same mod may have stored here.
    /// </summary>
    public void ApplyScrollFrom(GearSettingsPreset from)
    {
        Scroll = from.Scroll;
        ScrollSpeedX = from.ScrollSpeedX;
        ScrollSpeedY = from.ScrollSpeedY;
        ScrollTilingX = from.ScrollTilingX;
        ScrollTilingY = from.ScrollTilingY;
    }

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
        // Conditional: null means "this binding says nothing about skin tint" (a binding saved without the field),
        // so it must not clobber the author's value. The editor always writes an explicit 1 into an override.
        if (SkinToneMask is { } skinTone) d.SkinToneMask = skinTone;
    }
}

/// <summary>
/// Per-mod gear-settings override from a design binding, applied like <see cref="OverlayColorOverride"/> onto a
/// copy of the descriptor at composite time.
/// </summary>
public class OverlayGearOverride
{
    [JsonPropertyName("Top")]
    public GearSettingsPreset? Top { get; set; }

    /// <summary>group → option → settings.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, Dictionary<string, GearSettingsPreset>>? Options { get; set; }

    /// <summary>
    /// The mod's shared "Masks" tab gear settings (<see cref="ProteusMetadata.MaskDescriptor"/>), which has no
    /// option group. Null falls back to the live metadata mask descriptor.
    /// </summary>
    [JsonPropertyName("Mask")]
    public GearSettingsPreset? Mask { get; set; }

    /// <summary>
    /// The animated glow of content pieces that belong to no option (<see cref="ProteusMetadata.ContentGlow"/>).
    /// Separate from <see cref="Top"/>, which is captured from the mod's first overlay.
    /// </summary>
    [JsonPropertyName("Content")]
    public GearSettingsPreset? Content { get; set; }

    /// <summary>
    /// An imported pack's glow per MATERIAL, the gear twin of <see cref="OverlayColorOverride.Materials"/> and
    /// keyed the same way.
    /// </summary>
    [JsonPropertyName("Materials")]
    public Dictionary<string, GearSettingsPreset>? Materials
    {
        get => materials;
        set => materials = MaterialMap.CaseInsensitive(value);
    }

    private Dictionary<string, GearSettingsPreset>? materials;

    /// <summary>This override's glow for one content material, or null when it stores none.</summary>
    public GearSettingsPreset? ResolveMaterial(string? materialRel)
        => materialRel != null && materials != null && materials.TryGetValue(materialRel, out var g) ? g : null;

    public GearSettingsPreset? Resolve(string? group, string? option)
        => ResolveOption(group, option) ?? Top;

    /// <summary>A content piece's option entry, else <see cref="Content"/>; never <see cref="Top"/>, which is the overlays'.</summary>
    public GearSettingsPreset? ResolveContent(string? group, string? option)
        => ResolveOption(group, option) ?? Content;

    private GearSettingsPreset? ResolveOption(string? group, string? option)
        => group != null && option != null && Options != null
        && Options.TryGetValue(group, out var opts) && opts.TryGetValue(option, out var s)
            ? s : null;
}
