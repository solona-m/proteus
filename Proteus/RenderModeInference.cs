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
    /// The row emissive an animated-glow surface starts at: 150%, white.
    /// <para/>
    /// High on purpose. Under <see cref="GlowShader"/> the scroll map supplies the colour and the pattern
    /// and this scales them, so it is what makes the map read at all; white keeps it from tinting a map
    /// that already has its own colours. Shared by the editor's mode transition and by the Atramentum
    /// Luminis importer, which builds the same kind of surface and must not drift from it.
    /// </summary>
    public const float GlowEmissive = 1.5f;

    /// <summary>The glow colour that pairs with <see cref="GlowEmissive"/>.</summary>
    public const string GlowEmissiveColour = "#FFFFFF";

    /// <summary>A sub-row uses a feature that genuinely needs the gear shader — a sphere map, metalness, a
    /// specular colour, or glow (skin.shpk can't do those). Roughness is NOT counted: skin has roughness
    /// too, and a bare/zero roughness does nothing on its own, so it shouldn't force Cloth.
    /// <para/>
    /// Glow counts because skin no longer emits: the emissive bake into the skin normal's alpha (and the
    /// skin.shpk glow shader key that read it) is gone, so the only surface that can glow is a gear shell
    /// with its own material. Setting Glow therefore has to move the overlay onto one.</summary>
    public static bool IsClothSub(ColorTableSubRowPreset? s)
        => s != null
        && (s.Specular != null
         || s.Metalness.GetValueOrDefault() > 0f
         || s.SphereMap.GetValueOrDefault() > 0
         || s.SphereIntensity.GetValueOrDefault() > 0f
         || s.Emissive > 0f);

    /// <summary>Any Cloth feature (sphere/metal/specular/glow) is set across the option's rows.</summary>
    public static bool HasCloth(IEnumerable<ColorTableRowPreset> rows)
        => rows.Any(r => IsClothSub(r.SubRowA) || IsClothSub(r.SubRowB));

    /// <summary>
    /// Whether a stored Skin overlay has to be composited as a gear shell instead. Two reasons, both
    /// recomputed every composite so either can revert on its own:
    /// <list type="bullet">
    /// <item><paramref name="aboveGear"/> — it sits above a gear layer in the stack, so there is no skin
    /// left underneath it to paint into.</item>
    /// <item>Its rows use a feature skin.shpk can't render — sphere, metal, specular, or glow. Glow is
    /// why authored metadata gets promoted: skin no longer emits, so a declared glow would just go dark.</item>
    /// <item><paramref name="needsUnmirroredShell"/> — its art differs left from right and the body being
    /// worn is mirrored, so painting it into the skin would fold it in half. Only a shell has geometry of
    /// its own to send the two sides to two halves of the sheet.</item>
    /// <item><paramref name="toeCapWanted"/> — a toe cap is selected somewhere in the look. The cap is
    /// GEOMETRY: it rebuilds the toes as one rounded shape, and only a shell has geometry to rebuild.
    /// Painted into the skin the option simply does nothing, which is what it looked like — a whole
    /// composite with no second-skin phase at all, because every active overlay was a skin layer.</item>
    /// </list>
    /// A hand-pinned overlay is never promoted — the user's choice outranks the inference. <paramref
    /// name="pinned"/> is passed in rather than read off the descriptor because a design binding can
    /// override the pin, and the two callers learn that from different places.
    /// <para/>
    /// <paramref name="canShell"/> is the veto neither reason can override: it is false when the overlay
    /// paints something no shell can be cut from — gear, an accessory, a weapon, a mount — as opposed to the
    /// character's own skin (body, face, hair, tail, ears), which all have geometry a shell is cut from.
    /// Such an overlay stays skin and simply doesn't glow.
    /// <para/>
    /// Historical note, because the parameter's shape only makes sense with it: this began life meaning
    /// "isn't the body". While the builder could cut from the body alone, promoting a FACE overlay produced a
    /// body shell wearing the face's art in body UV — the whole character lit up wearing a face texture. The
    /// veto was the right answer to "there is nowhere to put this", never a claim that faces cannot glow, and
    /// it narrowed to its present meaning once the other surfaces could be cut.
    /// </summary>
    public static bool ShouldPromoteToGear(OverlayLayer layer, bool pinned,
        IEnumerable<ColorTableRowPreset>? rows, bool aboveGear, bool canShell = true,
        bool needsUnmirroredShell = false, bool toeCapWanted = false)
        => layer == OverlayLayer.Skin
        && !pinned
        && canShell
        && (aboveGear || needsUnmirroredShell || toeCapWanted || HasCloth(rows ?? []));

    /// <summary>
    /// Which shader a PROMOTED overlay renders on. Beside <see cref="ShouldPromoteToGear"/> and for the same
    /// reason: the compositor and the editor must not disagree about what was composited.
    /// <para/>
    /// An overlay auto-promoted from Skin stays on <c>skin.shpk</c> where it can. It was authored as skin and
    /// only moved because the skin layer had nowhere to put it; on character.shpk it loses the wearer's tone
    /// entirely, because the tone reaches art through the normal map's blue channel and a gear shell spends
    /// blue on its per-pixel alpha gate. A skin shell keeps blue, so the art is lit and tinted as skin.
    /// <para/>
    /// Only when nothing needs the gear colour table. skin.shpk declares no <c>g_SamplerIndex</c>, so its
    /// table cannot be addressed per texel — row presets, per-row opacity, and sphere/metal/specular/glow all
    /// require character.shpk. An overlay whose shader the author PINNED keeps their choice.
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
