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
    /// <summary>Nothing that affects the mode (diffuse, opacity, glow colour).</summary>
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

    /// <summary>
    /// The row emissive an animated-glow surface starts at: 150%, white. Under <see cref="GlowShader"/> it scales
    /// the scroll map's colour and pattern. Shared by the editor and the Atramentum Luminis importer.
    /// </summary>
    public const float GlowEmissive = 1.5f;

    /// <summary>The glow colour that pairs with <see cref="GlowEmissive"/>.</summary>
    public const string GlowEmissiveColour = "#FFFFFF";

    /// <summary>A sub-row uses a feature that needs the gear shader: a sphere map, metalness, a specular
    /// colour, a tile, or glow (skin no longer emits). Roughness is NOT counted: skin has roughness too.</summary>
    public static bool IsClothSub(ColorTableSubRowPreset? s)
        => s != null
        && (s.Specular != null
         || s.Metalness.GetValueOrDefault() > 0f
         || s.SphereMap.GetValueOrDefault() > 0
         || s.SphereIntensity.GetValueOrDefault() > 0f
         // A weave lives in the colour table, which the skin layer lacks. "!= null" because tile 0 is real.
         // TileScaleU/V are not counted: a scale with no pattern renders nothing.
         || s.Tile != null
         || s.Emissive > 0f);

    /// <summary>Any Cloth feature (sphere/metal/specular/glow) is set across the option's rows.</summary>
    public static bool HasCloth(IEnumerable<ColorTableRowPreset> rows)
        => rows.Any(r => IsClothSub(r.SubRowA) || IsClothSub(r.SubRowB));

    /// <summary>
    /// Every sub-row the author configured composites as a print, so the option carries colour and no surface.
    /// Derived from the rows so compositor and editor cannot disagree.
    /// </summary>
    public static bool IsPrint(IEnumerable<ColorTableRowPreset> rows)
    {
        bool any = false;
        foreach (var r in rows)
        {
            if (r.SubRowA is { } a) { if (a.Blend == RowBlend.Paint) return false; any = true; }
            if (r.SubRowB is { } b) { if (b.Blend == RowBlend.Paint) return false; any = true; }
        }
        return any;
    }

    /// <summary>
    /// Whether this mod asks for any pass that reshapes GEOMETRY: the single definition feeding
    /// <see cref="ShouldPromoteToGear"/>'s <c>geometryWanted</c>. Body-relaxing passes count too, since the body
    /// pass runs inside the second-skin phase.
    /// </summary>
    public static bool WantsGeometry(ProteusMetadata? md)
        => md != null
        && (md.BustBridge == true
         || md.SmoothNipples == true
         || md.CleftBridge == true
         || md.SmoothFold == true);

    /// <summary>
    /// Whether a stored Skin overlay has to be composited as a gear shell instead, recomputed every composite:
    /// <list type="bullet">
    /// <item><paramref name="aboveGear"/>: it sits above a gear layer, with no skin left to paint into.</item>
    /// <item>Its rows use a feature skin.shpk can't render (sphere, metal, specular, glow).</item>
    /// <item><paramref name="needsUnmirroredShell"/>: its art differs left from right on a mirrored body.</item>
    /// <item><paramref name="toeCapWanted"/>: a toe cap is selected in the look, and only a shell has geometry.</item>
    /// <item><paramref name="geometryWanted"/>: the mod asks for a geometry pass (see
    /// <see cref="WantsGeometry"/>).</item>
    /// </list>
    /// A hand-<paramref name="pinned"/> overlay is never promoted except by <paramref name="geometryWanted"/>,
    /// since a skin layer cannot carry a geometry pass. <paramref name="pinned"/> is passed in because a design
    /// binding can override it. <paramref name="canShell"/> is an absolute veto: false when the overlay paints
    /// something no shell can be cut from (gear, a weapon, a mount).
    /// </summary>
    public static bool ShouldPromoteToGear(OverlayLayer layer, bool pinned,
        IEnumerable<ColorTableRowPreset>? rows, bool aboveGear, bool canShell = true,
        bool needsUnmirroredShell = false, bool toeCapWanted = false, bool geometryWanted = false)
        => layer == OverlayLayer.Skin
        && canShell
        // A print is never promoted, not even by geometryWanted: it colours the skin layers, and a shell would
        // take that away (and toeCapWanted would otherwise turn a full-body print into a bodysuit).
        && !IsPrint(rows ?? [])
        // The pin yields only to an explicit request for geometry.
        && (geometryWanted
            || (!pinned && (aboveGear || needsUnmirroredShell || toeCapWanted || HasCloth(rows ?? []))));

    /// <summary>
    /// Which shader a PROMOTED overlay renders on; beside <see cref="ShouldPromoteToGear"/> so compositor and
    /// editor agree. Stays on <c>skin.shpk</c> (which keeps the wearer's tone via normal blue) unless something
    /// needs the gear colour table or the author pinned a shader.
    /// </summary>
    /// </summary>
    public static string PromotedShader(OverlayDescriptor d, IEnumerable<ColorTableRowPreset>? rows)
        => d.Shader == null
        && !d.IsMaskShell
        && d.Scroll == null
        && rows?.Any() != true
        && !HasCloth(rows ?? [])
            ? OverlayDescriptor.SkinShader
            : d.Shader ?? OverlayDescriptor.DefaultGearShader;

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
