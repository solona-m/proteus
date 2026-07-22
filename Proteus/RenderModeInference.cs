using System.Collections.Generic;
using System.Linq;

namespace Proteus;

/// <summary>The user-facing "what does this overlay do" mode, inferred from the features in use.</summary>
public enum RenderMode
{
    /// <summary>Painted into the skin (skin.shpk). No sphere/metal/animation.</summary>
    Skin,
    /// <summary>Gear + character.shpk — sphere maps, metalness, specular.</summary>
    Cloth,
    /// <summary>Gear + characterscroll.shpk — animated scrolling glow.</summary>
    Glow,
}

/// <summary>Which class of feature a single edit touched, for the last-edit-wins conflict rule.</summary>
public enum FeatureEdit
{
    /// <summary>Nothing that affects the mode (diffuse, opacity, plain glow %).</summary>
    Neutral,
    /// <summary>A Cloth-only feature (sphere/metal/specular).</summary>
    Cloth,
    /// <summary>The animated-glow effect (scroll map).</summary>
    Glow,
}

/// <summary>
/// Derives an overlay's render mode from the features actually in use, so the user never has to pick
/// Skin/Gear/shader up front — setting a sphere map or metal implies Cloth gear, a scroll effect implies
/// animated-glow gear, and nothing special stays Skin. Pure logic (no ImGui), unit-tested; the editor
/// applies the result unless the user pinned the mode by hand (<see cref="OverlayDescriptor.ManualShaderLock"/>).
/// </summary>
public static class RenderModeInference
{
    public const string ClothShader = OverlayDescriptor.DefaultGearShader;   // character.shpk
    public const string GlowShader  = "characterscroll.shpk";

    /// <summary>A sub-row uses a feature that genuinely needs the gear shader — a sphere map, metalness, or a
    /// specular colour (skin.shpk can't do those). Roughness is NOT counted: skin has roughness too, and a
    /// bare/zero roughness does nothing on its own, so it shouldn't force Cloth.</summary>
    public static bool IsClothSub(ColorTableSubRowPreset? s)
        => s != null
        && (s.Specular != null
         || s.Metalness.GetValueOrDefault() > 0f
         || s.SphereMap.GetValueOrDefault() > 0
         || s.SphereIntensity.GetValueOrDefault() > 0f);

    /// <summary>Any Cloth feature (sphere/metal/specular) is set across the option's rows.</summary>
    public static bool HasCloth(IEnumerable<ColorTableRowPreset> rows)
        => rows.Any(r => IsClothSub(r.SubRowA) || IsClothSub(r.SubRowB));

    /// <summary>An animated-glow effect (scroll map) is selected.</summary>
    public static bool HasGlow(IEnumerable<OverlayDescriptor> overlays, GearSettingsPreset? ovr)
        => (ovr != null ? ovr.Scroll : overlays.Select(d => d.Scroll).FirstOrDefault(s => s != null)) != null;

    /// <summary>The mode a given stored layer/shader represents (for the "Rendering as" badge).</summary>
    public static RenderMode ModeOf(OverlayLayer layer, string? shader)
        => layer == OverlayLayer.Skin ? RenderMode.Skin
         : string.Equals(shader ?? OverlayDescriptor.DefaultGearShader, GlowShader,
               System.StringComparison.OrdinalIgnoreCase) ? RenderMode.Glow
         : RenderMode.Cloth;

    /// <summary>
    /// The mode the features imply. When both Cloth and Glow features are present (they can't share one
    /// material), the tie is broken by the class just edited (last-edit-wins); with no such hint the
    /// current mode is kept, defaulting to Glow only if we were on Skin.
    /// </summary>
    public static RenderMode Infer(IEnumerable<ColorTableRowPreset> rows,
        IEnumerable<OverlayDescriptor> overlays, GearSettingsPreset? ovr,
        RenderMode current, FeatureEdit edited)
    {
        bool cloth = HasCloth(rows);
        bool glow  = HasGlow(overlays, ovr);

        if (cloth && glow)
            return edited == FeatureEdit.Glow  ? RenderMode.Glow
                 : edited == FeatureEdit.Cloth ? RenderMode.Cloth
                 : current == RenderMode.Skin  ? RenderMode.Glow
                 : current;
        if (glow)  return RenderMode.Glow;
        if (cloth) return RenderMode.Cloth;
        return RenderMode.Skin;
    }
}
