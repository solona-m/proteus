using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The colour-table editor, shared by both overlay layers.
///
/// Skin and gear overlays store their colours in the SAME place — <see cref="ColorTableSubRowPreset"/>
/// in metadata.json. Only the consumer differs: the compositor bakes them into the skin's textures,
/// while a gear overlay's are written into a real .mtrl colour table by GearMaterialWriter. So this is
/// one editor with fields shown or hidden per layer, not two.
///
/// Layout is modelled on Penumbra's advanced material editor (row picker, then A/B detail panels), but
/// written from scratch: Penumbra ships no licence, and it edits a live MtrlFile.ColorTable rather than
/// our presets, so its code would not fit here anyway.
/// </summary>
public static class ColorTableEditor
{
    /// <summary>How close to 1.0 counts as "the default" for skin-tint suppression. Shared by the store
    /// decision and the collapsed-state readout so the two can never disagree about which values are
    /// worth showing.</summary>
    private const float SkinTintEpsilon = 0.0005f;

    private static readonly string[] GearShaders = ["character.shpk", "characterscroll.shpk"];

    /// <summary>What an unset glow colour looks like in the swatch, made explicit when the user raises Glow
    /// on a row that has no colour to fall back on. Shared with <see cref="ContentGlowRow"/>, which seeds
    /// the same pair when an animated effect is switched on.</summary>
    internal const string WhiteHex = "#FFFFFF";

    // Were `const string`; a localized lookup is not a compile-time constant, so they read through the
    // string table now. Still resolved once per language, not once per frame — see Strings.
    private static string SphereTip    => Strings.Colors.SphereTip;
    private static string MetalTip     => Strings.Colors.MetalTip;
    private static string TileTip      => Strings.Colors.TileTip;
    private static string TileScaleTip => Strings.Colors.TileScaleTip;

    /// <summary>
    /// Bottom of the colour editor: the "Glow effect" thumbnail picker (+ its scroll speed/tiling), the
    /// "Advanced" mode-pin, and the "Rendering as" badge to the right of Advanced. Kept below the rows so
    /// the eye lands on the colours first. Sets <paramref name="edited"/> = Glow when the effect changes.
    /// </summary>
    /// <param name="onReset">
    /// Restores this option's settings to the mod's recorded originals; returns true when something
    /// actually changed. Supplied by the caller because only it knows which mod/group/option is open.
    /// Null hides the button entirely.
    /// </param>
    /// <param name="resetDisabledReason">Non-null renders the reset button greyed out and explains why.</param>
    /// <param name="drawExtraAdvanced">
    /// Extra per-MOD settings to render inside the Advanced disclosure, above the reset button. Supplied by
    /// the caller because this editor is scoped to one option and knows nothing about the mod around it.
    /// Drawn on every option tab of the same mod — the value is the mod's, so it reads the same wherever it
    /// is opened from. It commits and recomposites for itself rather than reporting back through this
    /// method's return: what it edits isn't this option's colours, and folding it into that flag would make
    /// every caller save option metadata (or install a design-binding override) for a change that has
    /// nothing to do with either.
    /// </param>
    /// <param name="advancedScope">
    /// ImGui id for the Advanced disclosure alone, so its open/closed state can be shared where
    /// <paramref name="idScope"/> is not. Callers pass the mod: the state being remembered is "show me
    /// the advanced controls", not "…on this one tab", and keying it per option collapsed the section
    /// every time the user clicked to a neighbouring tab to compare.
    /// </param>
    public static bool DrawGlowFooter(
        string idScope,
        string advancedScope,
        IReadOnlyList<OverlayDescriptor> overlays,
        GearSettingsPreset? ovr,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects,
        out FeatureEdit edited,
        Func<bool>? onReset = null,
        string? resetDisabledReason = null,
        Action? drawExtraAdvanced = null,
        // The compositor promoted this auto skin overlay to a gear shell because it's stacked above gear;
        // show it as Cloth so the footer agrees with the (gear) colour panel, without persisting the change.
        bool promotedToGear = false,
        // Non-null when this option can't be a cloth/glow layer at all — it paints gear, an accessory or a
        // weapon rather than the character's own skin, and only skin (body, face, hair, tail, ears) has
        // geometry a shell can be cut from. Disables the controls that would move it there, and its text is
        // shown beneath the picker: a DISABLED item never reports hover, so a tooltip could not carry it.
        string? noShellReason = null,
        // False where the descriptors being edited are not ones the skin-tint pass ever reads, so the
        // slider would save a value and change nothing. The Masks tab is the case: a plain-Skin mask has
        // no entry in the compositor's maskDescByMod at all (MaskDescriptorFor returns null for it) and
        // paints into the diffuse in its own pass, while a promoted mask gets a freshly built shell
        // descriptor that copies only the shader and scroll fields. Worse than inert, in fact — the mask
        // descriptor IS serialized into the composite fingerprint, so a drag would fire a recomposite
        // that produced byte-identical output.
        bool skinTintApplies = true,
        // True while a design binding is driving this mod, in which case the caller previews edits into the
        // binding and does NOT write metadata.json — so any control here whose value has nowhere to go but
        // the sidecar has to be hidden, or it saves nothing and says nothing.
        //
        // Passed in rather than inferred from `ovr != null`, which looks equivalent and is not: `ovr` comes
        // from GetEditableGearOverride, which returns null whenever the binding's GEAR dictionary has no
        // entry for this mod, while `overrideActive` tests its COLOUR dictionary. A binding that has never
        // recorded gear settings — the ordinary case — has a live colour override and a null gear override
        // at the same time, so the inference is false exactly when it matters.
        bool overrideActive = false)
    {
        edited = FeatureEdit.Neutral;
        if (overlays.Count == 0) return false;

        bool changed = false;
        var first = overlays[0];

        string? curScroll = ovr != null ? ovr.Scroll : first.Scroll;
        float? curSpeedX = ovr != null ? ovr.ScrollSpeedX : first.ScrollSpeedX;
        float? curSpeedY = ovr != null ? ovr.ScrollSpeedY : first.ScrollSpeedY;
        float? curTileX  = ovr != null ? ovr.ScrollTilingX : first.ScrollTilingX;
        float? curTileY  = ovr != null ? ovr.ScrollTilingY : first.ScrollTilingY;
        bool curLock = ovr != null ? (ovr.ManualShaderLock ?? false) : first.ManualShaderLock;
        float? curSkinMask = ovr != null ? ovr.SkinToneMask : first.SkinToneMask;
        var mode = RenderModeInference.ModeOf(ovr?.Layer ?? first.Layer, ovr != null ? ovr.Shader : first.Shader);
        if (promotedToGear && mode == RenderMode.Skin) mode = RenderMode.Cloth;   // stacked above gear → renders as a shell

        void SetScroll(string? s)       { if (ovr != null) ovr.Scroll = s;  else foreach (var d in overlays) d.Scroll = s; }
        void SetSpeed(float x, float y) { if (ovr != null) { ovr.ScrollSpeedX = x; ovr.ScrollSpeedY = y; } else foreach (var d in overlays) { d.ScrollSpeedX = x; d.ScrollSpeedY = y; } }
        void SetTile(float x, float y)  { if (ovr != null) { ovr.ScrollTilingX = x; ovr.ScrollTilingY = y; } else foreach (var d in overlays) { d.ScrollTilingX = x; d.ScrollTilingY = y; } }
        void SetLock(bool v)            => SetManualShaderLock(overlays, ovr, v);
        // Asymmetric on purpose, and GearSettingsPreset.ApplyTo depends on it. On the DESCRIPTORS, exactly
        // 1 is the default, so it is stored as omitted — that keeps sidecars free of no-op
        // "SkinToneMask": 1 lines and keeps the documented "omitted = full masking" true. In a design
        // OVERRIDE, null is reserved to mean "this binding predates the field, defer to the mod", so a
        // user who drags to 1.00 must write an explicit 1.00 or their choice would read as silence and
        // the author's value would win instead.
        void SetSkinMask(float v)
        {
            if (ovr != null) { ovr.SkinToneMask = v; return; }
            float? stored = Math.Abs(v - 1f) < SkinTintEpsilon ? null : v;
            foreach (var d in overlays) d.SkinToneMask = stored;
        }

        // ── Glow effect: a thumbnail picker (like the sphere-map picker) — picking one switches to Animated glow ──
        using (ImRaii.Disabled(noShellReason != null))
        {
            DrawEffectPicker(idScope, effects, curScroll, out bool effChanged, out string? newScroll);
            if (effChanged)
            {
                SetScroll(newScroll);
                edited = FeatureEdit.Glow;
                changed = true;
            }
        }
        // Only reachable when the picker is ENABLED — ImGui reports no hover for a disabled item — so this
        // deliberately does not try to carry noShellReason. That is printed below instead.
        var cs = Strings.Colors;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.GlowEffectTip);
        if (noShellReason != null)
            ImGui.TextDisabled(noShellReason);
        else if (effects.Count == 0)
            // Names the Settings button verbatim: this is the exact moment someone needs that folder, so
            // the message has to point at a control they can actually find on screen.
            ImGui.TextDisabled(cs.NoEffects);

        // Scroll speed / tiling — only meaningful once glowing.
        if (mode == RenderMode.Glow)
        {
            var speed = new Vector2(curSpeedX ?? ScrollSettings.Default.SpeedX, curSpeedY ?? ScrollSettings.Default.SpeedY);
            var tile  = new Vector2(curTileX ?? ScrollSettings.Default.TilingX, curTileY ?? ScrollSettings.Default.TilingY);

            ImGui.SetNextItemWidth(150);
            if (ImGui.DragFloat2($"{cs.ScrollSpeed}##{idScope}", ref speed, 0.002f, -1f, 1f, "%.3f"))
            {
                SetSpeed(speed.X, speed.Y);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.ScrollSpeedTip);

            ImGui.SetNextItemWidth(150);
            if (ImGui.DragFloat2($"{cs.Tiling}##{idScope}", ref tile, 0.05f, 0.1f, 20f, "%.2f"))
            {
                SetTile(tile.X, tile.Y);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.TilingTip);
        }

        // ── Advanced (mode pin) at the very bottom, with the "Rendering as" badge to its right ──
        bool advOpen = ImGui.TreeNodeEx($"{cs.Advanced}##{advancedScope}", ImGuiTreeNodeFlags.NoTreePushOnOpen);

        ImGui.SameLine(0f, 24f);
        DrawRenderingAsBadge(mode);
        ImGui.SameLine();
        ImGui.TextDisabled(curLock ? cs.Pinned : cs.Auto);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(curLock ? cs.PinnedTip : cs.AutoTip);
        if (curLock)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{cs.BackToAuto}##{idScope}")) { SetLock(false); changed = true; }
        }

        // A non-default skin tint, readable while Advanced is shut — same reason Pinned/Auto is printed
        // here. Without it a tab whose suppression is switched off looks identical to an untouched one,
        // and the whole point of moving this off the global slider is that it now varies per option.
        bool showSkinTint = skinTintApplies && mode == RenderMode.Skin;
        if (showSkinTint && curSkinMask is { } shownMask && Math.Abs(shownMask - 1f) > SkinTintEpsilon)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(cs.SkinTintBadgeFmt, shownMask));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.SkinTintTip);
        }

        if (advOpen)
        {
            ImGui.TextDisabled(cs.ForceModeHint);
            foreach (var m in new[] { RenderMode.Skin, RenderMode.Cloth, RenderMode.Glow })
            {
                bool sel = curLock && mode == m;
                // Pinning is the user's override of the inference — but it can't conjure a surface. With no
                // body to cut a shell from, Cloth and Glow are simply not reachable modes for this option.
                using (ImRaii.Disabled(noShellReason != null && m != RenderMode.Skin))
                {
                    if (ImGui.RadioButton($"{ModeName(m)}##force_{idScope}_{m}", sel) && !sel)
                    {
                        ApplyMode(overlays, ovr, m);
                        SetLock(true);
                        changed = true;
                    }
                }
                if (m != RenderMode.Glow) ImGui.SameLine();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.ForceModeTip);

            // Skin-tint suppression, per option. Drawn only in Skin mode, the way scroll speed/tiling are
            // drawn only in Glow: the compositor's suppression pass never runs for a Cloth or Glow overlay
            // (those go down the shell path), so the control would be inert there. `mode` already accounts
            // for promotedToGear, so an auto-promoted overlay hides it too.
            if (showSkinTint)
            {
                float tint = curSkinMask ?? 1f;
                ImGui.SetNextItemWidth(90);
                if (ImGui.DragFloat($"{cs.SkinTint}##skintint_{idScope}", ref tint, 0.01f, 0f, 1f, "%.2f"))
                {
                    SetSkinMask(Math.Clamp(tint, 0f, 1f));
                    // Deliberately does NOT set `edited`: this is not a Skin/Cloth/Glow signal, and
                    // feeding it to ReconcileMode would let a tint nudge re-infer the render mode.
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.SkinTintTip);
            }

            // "This overlay IS the skin" — the editor's counterpart to the Create tab's tick, and the same
            // single declaration, so it moves the same two settings together. Splitting them was a trap:
            // the normal stopped doubling while suppression carried on fading the blue channel it had just
            // written, which is the original pale-body symptom back at half strength, fixable only by
            // noticing the slider below and dragging it to zero as well. It moves in view, which is the
            // feedback that keeps this honest.
            //
            // NormalMode is structural — which map ends up on the material — so it has no
            // GearSettingsPreset field and no binding override; `overrideActive` hides the control rather
            // than letting it write somewhere that is never saved. Skin mode and a normal to blend are both
            // required for it to mean anything.
            if (showSkinTint && !overrideActive && overlays.Any(d => d.Normal != null))
            {
                bool wholeSkin = first.NormalMode == NormalMode.Replace;
                if (ImGui.Checkbox($"{cs.WholeSkin}##normalmode_{idScope}", ref wholeSkin))
                {
                    foreach (var d in overlays)
                        d.NormalMode = wholeSkin ? NormalMode.Replace : NormalMode.Compound;
                    // Suppression keeps FABRIC at its authored colour on any wearer, which is backwards for
                    // art that is the skin. Untick restores the default (1 ⇒ stored as omitted) rather than
                    // leaving the zero behind, so the switch is its own inverse.
                    SetSkinMask(wholeSkin ? 0f : 1f);
                    // Like skin tint, deliberately not a FeatureEdit: which way a normal blends says
                    // nothing about whether this belongs on skin or on a shell.
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.WholeSkinTip);
            }

            // One-sided art. DECLARED, because nothing can measure it: a real skin texture is never
            // symmetric — freckles, moles — so probing the art called ordinary skin asymmetric and moved it
            // onto a shell, where it lost the wearer's tone. Only the author knows whether a difference
            // between the two sides is the point or just detail.
            //
            // Shown on the skin layer only. On a shell the art is already rendered through its own geometry
            // and nothing folds it, so the tick would decide nothing.
            if (showSkinTint && !overrideActive)
            {
                bool oneSided = first.AsymmetricArt == true;
                if (ImGui.Checkbox($"{cs.OneSided}##asymmetric_{idScope}", ref oneSided))
                {
                    // Cleared to null rather than false, so an untick leaves the sidecar as it was before
                    // anyone touched this — the documented "absent = symmetric" default.
                    foreach (var d in overlays) d.AsymmetricArt = oneSided ? true : null;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.OneSidedTip);
            }

            // Whole-mod settings the caller owns (currently which bodies to bake onto). Separated because
            // everything above this line is per-option and everything below it is not.
            if (drawExtraAdvanced != null)
            {
                ImGui.Separator();
                drawExtraAdvanced();
            }

            if (onReset != null)
            {
                ImGui.Separator();
                bool disabled = resetDisabledReason != null;
                // Ctrl-guarded: this overwrites the option's current colours/glow/mode with no undo.
                bool armed = !disabled && ImGui.GetIO().KeyCtrl;
                using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (armed ? 1f : 0.5f)))
                {
                    if (ImGui.Button($"{cs.ResetBtn}##reset_{idScope}") && armed && onReset())
                        changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(disabled ? resetDisabledReason : cs.ResetTip);
            }
        }

        return changed;
    }

    /// <summary>
    /// The glow footer for an imported content pack's material: pick an effect, then set its speed and
    /// tiling. Returns true when something changed and the caller should persist and recomposite.
    /// <para/>
    /// A sibling of <see cref="DrawGlowFooter"/> rather than a parameter on it, because almost none of that
    /// one applies here. It is built around an <c>OverlayDescriptor</c> a content piece does not have, and
    /// around render-mode INFERENCE — deciding whether art belongs on skin or on a shell — which is not a
    /// question a content pack raises: the pack shipped its own geometry and its own material, and this one
    /// control is the only thing that changes its shader. So no mode pin, no "Rendering as" badge, no
    /// shell/no-shell reason.
    /// <para/>
    /// <paramref name="glow"/> is mutated in place, exactly as <see cref="DrawGlowFooter"/> mutates its
    /// override — the caller owns whether that instance is the sidecar's or a binding's copy.
    /// </summary>
    public static bool DrawContentGlowFooter(
        string idScope,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects,
        GearSettingsPreset glow)
    {
        bool changed = false;
        var cs = Strings.Colors;

        DrawEffectPicker(idScope, effects, glow.Scroll, out bool effChanged, out string? newScroll);
        if (effChanged)
        {
            glow.Scroll = newScroll;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.GlowEffectTip);
        if (effects.Count == 0)
            // Names the Settings button verbatim: this is the exact moment someone needs that folder.
            ImGui.TextDisabled(cs.NoEffects);

        // Speed and tiling mean nothing until there is a pattern to move, so they appear with one.
        if (string.IsNullOrEmpty(glow.Scroll)) return changed;

        // A stored effect the picker can no longer offer — the file was removed from the mod's Effects
        // folder or the library after it was chosen. The composite falls back to the pack's own material and
        // says so in the log; without this the panel would show a glow that isn't rendering and give no clue.
        if (!effects.Any(e => string.Equals(e.Name, glow.Scroll, StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ProteusStyle.Warn, string.Format(Strings.Content.GlowEffectMissingFmt, glow.Scroll));
            ImGui.PopTextWrapPos();
        }

        var speed = new Vector2(glow.ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                                glow.ScrollSpeedY ?? ScrollSettings.Default.SpeedY);
        var tile  = new Vector2(glow.ScrollTilingX ?? ScrollSettings.Default.TilingX,
                                glow.ScrollTilingY ?? ScrollSettings.Default.TilingY);

        ImGui.SetNextItemWidth(150);
        if (ImGui.DragFloat2($"{cs.ScrollSpeed}##content_{idScope}", ref speed, 0.002f, -1f, 1f, "%.3f"))
        {
            glow.ScrollSpeedX = speed.X;
            glow.ScrollSpeedY = speed.Y;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.ScrollSpeedTip);

        ImGui.SetNextItemWidth(150);
        if (ImGui.DragFloat2($"{cs.Tiling}##content_{idScope}", ref tile, 0.05f, 0.1f, 20f, "%.2f"))
        {
            glow.ScrollTilingX = tile.X;
            glow.ScrollTilingY = tile.Y;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.TilingTip);

        return changed;
    }

    /// <summary>Thumbnail picker for the glow effect, modelled on <see cref="DrawSpherePicker"/>: the current
    /// effect's image sits beside the combo, and the list is clickable pictures. <paramref name="newScroll"/>
    /// is the chosen effect's file name (null = None) when <paramref name="changed"/> is true.</summary>
    private static void DrawEffectPicker(string id,
        IReadOnlyList<(string Name, string Path, bool FromMod)> effects,
        string? curScroll, out bool changed, out string? newScroll)
    {
        changed = false;
        newScroll = curScroll;
        const float current = 32f;
        const float thumb   = 56f;

        // Current effect's thumbnail beside the combo — the "none" icon when None, else the effect's image.
        var cur = effects.FirstOrDefault(e => string.Equals(e.Name, curScroll, StringComparison.OrdinalIgnoreCase));
        bool drewBeside = curScroll == null
            ? Spheres?.DrawNone(current) == true
            : cur.Path != null && EffectThumbs?.Draw(cur.Path, current) == true;
        if (drewBeside) ImGui.SameLine();

        var cs = Strings.Colors;
        var label = curScroll == null ? cs.None : Path.GetFileNameWithoutExtension(curScroll);
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo($"{cs.GlowEffect}##eff_{id}", label, ImGuiComboFlags.HeightLarge))
        {
            // None entry with the shared "none" icon (matches the sphere-map picker).
            bool noneHas = false, noneClicked = false;
            if (Spheres != null) noneClicked = Spheres.DrawNoneButton($"##effnone_{id}", thumb, out noneHas);
            if (noneClicked && curScroll != null)
            {
                newScroll = null;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            if (noneHas) ImGui.SameLine();
            if (ImGui.Selectable($"{cs.None}##effnonesel_{id}", curScroll == null, ImGuiSelectableFlags.None,
                    new Vector2(0, noneHas ? thumb : 0)) && curScroll != null)
            {
                newScroll = null;
                changed = true;
            }
            foreach (var (name, path, fromMod) in effects)
            {
                bool selected = string.Equals(name, curScroll, StringComparison.OrdinalIgnoreCase);

                bool clicked = false, hasThumb = false;
                if (EffectThumbs != null)
                    clicked = EffectThumbs.DrawButton($"##effimg_{id}_{name}", path, thumb, out hasThumb);
                if (clicked && !selected)
                {
                    newScroll = name;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (hasThumb) ImGui.SameLine();

                var text = fromMod
                    ? string.Format(cs.EffectFromModFmt, Path.GetFileNameWithoutExtension(name))
                    : Path.GetFileNameWithoutExtension(name);
                if (ImGui.Selectable($"{text}##eff_{id}_{name}", selected, ImGuiSelectableFlags.None,
                        new Vector2(0, hasThumb ? thumb : 0)) && !selected)
                {
                    newScroll = name;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// Effective (gear, shader) for an option's rows, resolving a live gear override first — exactly as
    /// <see cref="DrawGlowFooter"/> does — then the descriptor. Callers must pass the SAME override they hand
    /// the footer, or the row editor's gear controls (sphere map, metalness) would disagree with the mode
    /// the badge shows while a design binding is being edited.
    /// </summary>
    public static (bool Gear, string? Shader) EffectiveLayerShader(
        IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr)
    {
        if (overlays.Count == 0) return (false, null);
        var first = overlays[0];
        var layer = ovr?.Layer ?? first.Layer;
        if (layer == OverlayLayer.Skin) return (false, OverlayDescriptor.SkinShader);
        var shader = (ovr != null ? ovr.Shader : first.Shader) ?? OverlayDescriptor.DefaultGearShader;
        return (true, shader);
    }

    /// <summary>Friendly, mechanism-free mode name for the badge and Advanced labels.</summary>
    public static string ModeName(RenderMode m) => m switch
    {
        RenderMode.Skin  => Strings.Colors.ModeSkin,
        RenderMode.Cloth => Strings.Colors.ModeCloth,
        _                => Strings.Colors.ModeGlow,
    };

    /// <summary>Draws the "Rendering as: &lt;mode&gt;" badge (same colours as the footer) on the current line.</summary>
    public static void DrawRenderingAsBadge(RenderMode mode)
    {
        var badgeColor = mode switch
        {
            RenderMode.Cloth => new Vector4(0.60f, 0.80f, 1.00f, 1f),   // cool blue
            RenderMode.Glow  => new Vector4(0.96f, 0.77f, 0.19f, 1f),   // #F4C430 gold
            _                => new Vector4(0.80f, 0.75f, 0.68f, 1f),   // warm skin
        };
        ImGui.TextUnformatted(Strings.Colors.RenderingAs);
        ImGui.SameLine();
        ProteusStyle.Pill(ModeName(mode), badgeColor);
    }

    /// <summary>Point the descriptors' (or the live design-binding override's) Layer+Shader at
    /// <paramref name="mode"/> — how the inference result and the Advanced picker are both applied.</summary>
    public static void ApplyMode(IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr, RenderMode mode)
    {
        var (layer, shader) = mode switch
        {
            RenderMode.Skin  => (OverlayLayer.Skin, (string?)null),
            RenderMode.Cloth => (OverlayLayer.Gear, RenderModeInference.ClothShader),
            _                => (OverlayLayer.Gear, RenderModeInference.GlowShader),
        };
        if (ovr != null) { ovr.Layer = layer; ovr.Shader = shader; }
        else foreach (var d in overlays) { d.Layer = layer; d.Shader = shader; }
    }

    /// <summary>Set (or release) the manual mode pin, on the binding's override when one is active and on
    /// every descriptor otherwise — the same target <see cref="ApplyMode"/> writes to, so the pin and the
    /// mode it pins can never end up on different objects.</summary>
    public static void SetManualShaderLock(IReadOnlyList<OverlayDescriptor> overlays,
        GearSettingsPreset? ovr, bool locked)
    {
        if (ovr != null) ovr.ManualShaderLock = locked;
        else foreach (var d in overlays) d.ManualShaderLock = locked;
    }

    /// <summary>
    /// Row picker plus the A/B detail panels for the selected row.
    /// <paramref name="usedRows"/> (when non-null) limits the picker to rows the index texture uses.
    /// <paramref name="authoredPhysical"/> says the roughness and metalness in these rows came from the
    /// material's own author rather than from a template Proteus built — see the Physical block, which is
    /// otherwise hidden under Animated glow.
    /// </summary>
    public static void DrawRows(
        string idScope,
        List<ColorTableRowPreset> rows,
        HashSet<int>? usedRows,
        bool gear,
        string? shader,
        IReadOnlyList<string>? shellMaterialLeaves,
        IReadOnlyList<Proteus.Interop.SkinGlowTarget>? skinGlowTargets,
        out FeatureEdit edited,
        ref int selectedRow,
        ref bool changed,
        bool authoredPhysical = false,
        IReadOnlyList<(float Roughness, float Metalness)>? physicalBaseline = null,
        // The Masks tab. There a half-authored row pair renders with its unset half MIRRORED from the set
        // one (SecondSkinService.BuildRows), because a mask shell's colour is the colorset over a white
        // base. Display has to follow, or the picker and the model show different colours. False everywhere
        // else, where an unset sub-row really is neutral and showing it as its partner would be a lie.
        bool mirrorUnsetSubRows = false,
        // Whether the light response can actually reach this table. True for a shell Proteus builds, whose
        // colour table it re-asserts every frame and can therefore dim; FALSE for an imported pack's own
        // material, which is published verbatim and never touched at runtime. Drawing the controls there
        // would offer a setting that saves, reloads, and does nothing — the exact silent no-op this editor
        // hides sphere maps under characterscroll to avoid.
        bool lightResponseApplies = true,
        // The sub-row column the index actually lands in — "A", "B", or null for "both, or nobody knows".
        // The other column is then dimmed exactly as an unsampled ROW is, and for the same reason: on a
        // shell with no _id the fabricated index is (255, 255, 0), so column B is dead on every one of the
        // sixteen rows and the grid used to draw it as live anyway.
        //
        // Null is the honest default and stays honest: a gradient index genuinely uses both columns, and a
        // scan that read nothing knows nothing. Neither is something to narrow away.
        string? usedSubRow = null)
    {
        edited = FeatureEdit.Neutral;

        // Resolved once rather than per swatch — this runs sixteen times a frame inside the picker.
        //
        // Never while mirroring. On a mask shell an unset sub-row renders as its PARTNER's values
        // (SecondSkinService.BuildRows), so authoring the column the index doesn't sample still reaches the
        // screen through the one it does — and calling it dead would be flatly false, which is worse than
        // saying nothing.
        bool onlyA = !mirrorUnsetSubRows && string.Equals(usedSubRow, "A", StringComparison.Ordinal);
        bool onlyB = !mirrorUnsetSubRows && string.Equals(usedSubRow, "B", StringComparison.Ordinal);

        // The render mode drives which feature controls are LIVE vs dimmed; the features that are set
        // drive the mode back (inference happens in the caller). characterscroll drives its look from the
        // scroll map, so sphere/metal don't render there.
        var mode = RenderModeInference.ModeOf(gear ? OverlayLayer.Gear : OverlayLayer.Skin, shader);

        // Every row is shown; rows the index texture never selects are DIMMED rather than hidden or
        // disabled, so the table's shape stays constant and it's obvious which rows the overlay uses.
        //
        // Dimmed, not disabled, because the filter is a reading of an index texture and a reading can be
        // wrong. One was: another mod claiming the same game path handed back its own texture, the scan
        // called it row 16, and every other row — including the one that actually rendered — became
        // unclickable. A hint that turns out to be wrong should cost a moment's confusion, not the ability
        // to edit the material at all.
        bool InUse(int r) => usedRows == null || usedRows.Contains(r);

        // Land on a live row only when nothing has been chosen yet (0 = unset). Re-clamping every frame
        // would drag the selection back the instant someone picked a dimmed row on purpose.
        if (selectedRow is <= 0 or > 16)
        {
            int firstUsed = Enumerable.Range(1, 16).FirstOrDefault(InUse);
            selectedRow = firstUsed == 0 ? 1 : firstUsed;
        }
        int sel = selectedRow;                       // a ref param can't be captured by a lambda

        // ── row picker ───────────────────────────────────────────────────────
        // Each button previews its row: three columns (diffuse, specular, glow), each split top = A,
        // bottom = B — so the whole table is readable at a glance without clicking through it.
        // Left-align the label, or it centres itself under the swatches and disappears.
        using var align = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));

        // Wrap to whatever width the window actually has, rather than a fixed count that falls off the
        // right edge when the window is narrow.
        // Scaled together with the swatch insets in DrawRowSwatches — those are measured off this button's
        // rect, so the two must move as one or the swatches drift out of their button at non-1.0 UI scale.
        var btn = ProteusStyle.S(70f, 30f);
        float avail = ImGui.GetContentRegionAvail().X;
        var cs = Strings.Colors;
        int perLine = Math.Max(1, (int)((avail + ImGui.GetStyle().ItemSpacing.X) / (btn.X + ImGui.GetStyle().ItemSpacing.X)));

        for (int row = 1; row <= 16; row++)
        {
            if ((row - 1) % perLine != 0) ImGui.SameLine();

            bool used = InUse(row);
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, used ? 1f : 0.5f))
            using (ProteusStyle.Selected(row == selectedRow))
            {
                if (ImGui.Button($"#{row:D2}##row_{idScope}_{row}", btn))
                    selectedRow = row;
            }
            if (!used && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(cs.RowUnusedTip);

            DrawRowSwatches(rows, row, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), used,
                mirrorUnsetSubRows, onlyA, onlyB);
        }

        ImGui.Separator();

        // ── copy / paste the whole selected row-pair (both sub-rows) ─────────
        // Mirrors Penumbra's advanced-material row copy: grab one row's colours and stamp them onto
        // another. The per-sub-row buttons below (in each A/B panel) do the same for a single column,
        // and share their clipboard, so you can copy sub-row A and paste it into B.
        int curRow = selectedRow;   // a ref param can't be captured by a lambda
        if (ImGui.SmallButton($"{cs.CopyRow}##copyrow_{idScope}"))
            _rowClip = CloneRow(rows.FirstOrDefault(r => r.Row == curRow));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.CopyRowTip);

        ImGui.SameLine();
        using (ImRaii.Disabled(_rowClip == null))
        {
            if (ImGui.SmallButton($"{cs.PasteRow}##pasterow_{idScope}"))
            {
                var p = EnsurePreset(rows, selectedRow);
                p.SubRowA = _rowClip!.SubRowA is { } a ? Clone(a) : null;
                p.SubRowB = _rowClip!.SubRowB is { } b ? Clone(b) : null;
                changed = true;
            }
        }
        if (_rowClip == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(cs.NeedRowCopy);
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Format(cs.PasteRowTipFmt, selectedRow));

        using (ImRaii.PushColor(ImGuiCol.TableHeaderBg, ImGui.GetColorU32(ProteusStyle.AccentSoft)))
        if (ImGui.BeginTable($"##ab_{idScope}", 2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(string.Format(cs.SubRowAFmt, selectedRow));
            ImGui.TableSetupColumn(string.Format(cs.SubRowBFmt, selectedRow));
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();

            // Said in the dead column rather than dimmed like its swatch, because ImGui's Alpha style var
            // REPLACES rather than multiplies: the controls below push their own alpha (the light-response
            // pair pushes 1f whenever the row emits), so a wrapping push would be undone from the inside and
            // dim only the half of the panel that happened not to push. A line that says why is also the
            // thing someone hunting a control that "does nothing" actually needs.
            ImGui.TableNextColumn();
            if (onlyB) ProteusStyle.DisabledWrapped(cs.SubRowUnused);
            DrawSubRow($"{idScope}_A", rows, selectedRow, true, mode, shellMaterialLeaves, skinGlowTargets,
                authoredPhysical, physicalBaseline, ref edited, ref changed, mirrorUnsetSubRows,
                lightResponseApplies);

            ImGui.TableNextColumn();
            if (onlyA) ProteusStyle.DisabledWrapped(cs.SubRowUnused);
            DrawSubRow($"{idScope}_B", rows, selectedRow, false, mode, shellMaterialLeaves, skinGlowTargets,
                authoredPhysical, physicalBaseline, ref edited, ref changed, mirrorUnsetSubRows,
                lightResponseApplies);

            ImGui.EndTable();
        }
    }

    /// <summary>Thumbnails for the sphere-map picker. Set once at startup; null just means no previews.</summary>
    public static SphereMapPreview? Spheres { get; set; }

    /// <summary>Thumbnails for the tile picker. Set once at startup; null just means no previews.</summary>
    public static TilePreview? Tiles { get; set; }

    /// <summary>Thumbnails for the glow-effect picker. Set once at startup; null falls back to names only.</summary>
    public static EffectPreview? EffectThumbs { get; set; }

    /// <summary>Live colorset "glow / target" highlighter. Set once at startup; null disables the buttons.</summary>
    public static Proteus.Interop.ColorTableHighlighter? Highlighter { get; set; }

    /// <summary>Live skin-row glow via render-material diffuse rebind. Set once at startup; null disables the button.</summary>
    public static Proteus.Interop.SkinDiffuseGlow? SkinGlow { get; set; }

    /// <summary>Sphere map index, as a dropdown of thumbnails — an index alone tells you nothing.</summary>
    private static void DrawSpherePicker(string id, ref int index, out bool changed)
    {
        changed = false;
        const float current = 32f;   // the one in use, beside the combo
        const float thumb = 56f;     // the pictures in the list — click one to pick it

        Spheres?.Draw(index, current);
        if (Spheres != null) ImGui.SameLine();

        ImGui.SetNextItemWidth(70);
        if (ImGui.BeginCombo($"{Strings.Colors.SphereIndex}##sp_{id}", index.ToString(), ImGuiComboFlags.HeightLarge))
        {
            for (int i = 0; i < SphereMapPreview.Count; i++)
            {
                // The picture is the button — clicking it selects that index and closes the list.
                if (Spheres?.DrawButton($"##sphimg_{id}_{i}", i, thumb) == true && i != index)
                {
                    index = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (Spheres != null) ImGui.SameLine();

                if (ImGui.Selectable($"{i}##sph_{id}_{i}", i == index, ImGuiSelectableFlags.None,
                        new Vector2(0, thumb)) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(SphereTip);
    }

    /// <summary>
    /// Fabric weave, as a dropdown of thumbnails — an index alone tells you nothing.
    /// <para/>
    /// <see cref="DrawSpherePicker"/> with one addition: a None entry. The sphere needs none because slice 0
    /// there IS the game's empty sphere, but tile 0 is a real weave, so "no weave" has to be a choice of its
    /// own. It is <paramref name="index"/> below zero, and it borrows the shared none icon
    /// <see cref="SphereMapPreview.DrawNoneButton"/> exists to lend.
    /// </summary>
    private static void DrawTilePicker(string id, ref int index, out bool changed)
    {
        changed = false;
        const float current = 32f;   // the one in use, beside the combo
        const float thumb = 56f;     // the pictures in the list — click one to pick it

        var cs = Strings.Colors;
        if (index < 0) Spheres?.DrawNone(current);
        else Tiles?.Draw(index, current);
        if (Tiles != null || Spheres != null) ImGui.SameLine();

        ImGui.SetNextItemWidth(70);
        var label = index < 0 ? cs.TileNone : index.ToString();
        if (ImGui.BeginCombo($"{cs.TilePattern}##tl_{id}", label, ImGuiComboFlags.HeightLarge))
        {
            // "No weave" first, so switching it off doesn't mean hunting through sixty-four pictures.
            bool noneAvailable = false;
            if (Spheres?.DrawNoneButton($"##tlnone_{id}", thumb, out noneAvailable) == true && index >= 0)
            {
                index = -1;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            if (noneAvailable) ImGui.SameLine();

            if (ImGui.Selectable($"{cs.TileNone}##tln_{id}", index < 0, ImGuiSelectableFlags.None,
                    new Vector2(0, thumb)) && index >= 0)
            {
                index = -1;
                changed = true;
            }

            for (int i = 0; i < TilePreview.Count; i++)
            {
                // The picture is the button — clicking it selects that index and closes the list.
                if (Tiles?.DrawButton($"##tlimg_{id}_{i}", i, thumb) == true && i != index)
                {
                    index = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (Tiles != null) ImGui.SameLine();

                if (ImGui.Selectable($"{i}##tl_{id}_{i}", i == index, ImGuiSelectableFlags.None,
                        new Vector2(0, thumb)) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(TileTip);
    }

    /// <summary>
    /// Paint a row's colours onto its picker button: three columns — diffuse, specular, glow — each
    /// split top = sub-row A, bottom = sub-row B. The glow swatch is the emissive colour scaled by its
    /// intensity, i.e. what actually lands in the colour table, so a row with no glow reads as black.
    /// </summary>
    /// <param name="onlyA">The index lands in column A everywhere, so the B half of this strip is dead.</param>
    /// <param name="onlyB">The mirror of that. Both false = either the columns are shared or nothing is
    /// known, and neither half is dimmed.</param>
    private static void DrawRowSwatches(
        List<ColorTableRowPreset> rows, int row, Vector2 min, Vector2 max, bool used,
        bool mirrorUnsetSubRows = false, bool onlyA = false, bool onlyB = false)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        var draw = ImGui.GetWindowDrawList();

        // The label sits on the left; swatches fill the right half of the button. Scaled to match the
        // button size in DrawRows — an unscaled 34px inset leaves the swatches overlapping the label at
        // 1.5x and floating away from it at 0.75x.
        float x0 = min.X + ProteusStyle.S(34f), x1 = max.X - ProteusStyle.S(3f);
        float y0 = min.Y + ProteusStyle.S(3f), y1 = max.Y - ProteusStyle.S(3f);
        if (x1 <= x0) return;

        float colW = (x1 - x0) / 3f;
        float midY = (y0 + y1) * 0.5f;

        // Rows the index texture never selects are dimmed, so the ones actually in play stand out.
        // ImGui's Disabled only fades the widget itself — these swatches are hand-drawn, so dim them too.
        //
        // Per HALF, not per row: the column is a second axis of the same fact. A shell with no _id samples
        // (255, 255, 0), which is row 16 column A — so on most overlays fifteen rows are dead in full and
        // the bottom half of the sixteenth is dead as well.
        float dim = used ? 1f : 0.25f;
        float dimA = onlyB ? 0.25f : 1f;
        float dimB = onlyA ? 0.25f : 1f;

        Vector3 Swatch(ColorTableSubRowPreset? s, int column, float half) => dim * half * (column switch
        {
            0 => HexToVec3(s?.Diffuse),
            1 => HexToVec3(s?.Specular),
            _ => HexToVec3(s?.EmissiveColor ?? s?.Diffuse) * (s?.Emissive ?? 0f),
        });

        // Same mirror the detail panels and the renderer use, so the two halves of this swatch can't show
        // something the big A/B panels below contradict.
        var subA = preset?.SubRowA ?? (mirrorUnsetSubRows ? preset?.SubRowB : null);
        var subB = preset?.SubRowB ?? (mirrorUnsetSubRows ? preset?.SubRowA : null);

        for (int c = 0; c < 3; c++)
        {
            float cx0 = x0 + c * colW, cx1 = cx0 + colW - 1f;
            foreach (var (sub, ry0, ry1, half) in new[]
                     {
                         (subA, y0, midY, dimA),
                         (subB, midY, y1, dimB),
                     })
            {
                var v = Swatch(sub, c, half);
                uint col = ImGui.GetColorU32(new Vector4(v.X, v.Y, v.Z, 1f));
                draw.AddRectFilled(new Vector2(cx0, ry0), new Vector2(cx1, ry1), col);

                // A light-sensitive glow reads as an ordinary one here — the swatch shows the emissive it
                // reaches in the DARK, which is the whole point of the setting and not something to dim
                // away. Mark it instead: a notch in the glow swatch's top-right corner, so the strip says
                // which regions go dark-only without lying about their colour.
                if (c == 2 && (sub?.LightResponse ?? 0f) > 0f)
                {
                    float n = MathF.Min(ProteusStyle.S(4f), MathF.Min(cx1 - cx0, ry1 - ry0));
                    draw.AddTriangleFilled(
                        new Vector2(cx1 - n, ry0), new Vector2(cx1, ry0), new Vector2(cx1, ry0 + n),
                        ImGui.GetColorU32(ImGuiCol.Text));
                }
            }
        }

        draw.AddRect(new Vector2(x0, y0), new Vector2(x1, y1), ImGui.GetColorU32(ImGuiCol.Border));
    }

    private static void DrawSubRow(
        string id, List<ColorTableRowPreset> rows, int row, bool isA, RenderMode mode,
        IReadOnlyList<string>? shellMaterialLeaves,
        IReadOnlyList<Proteus.Interop.SkinGlowTarget>? skinGlowTargets,
        bool authoredPhysical,
        IReadOnlyList<(float Roughness, float Metalness)>? physicalBaseline,
        ref FeatureEdit edited, ref bool changed,
        bool mirrorUnsetSubRows = false,
        bool lightResponseApplies = true)
    {
        bool gear     = mode != RenderMode.Skin;
        bool material = mode == RenderMode.Cloth;   // sphere / metal / roughness live here
        const float DimAlpha = 0.5f;                // dimmed-but-clickable: a feature the mode ignores

        var preset = rows.FirstOrDefault(r => r.Row == row);
        // DISPLAY falls back to the other sub-row where that is what RENDERS — the Masks tab, whose shell
        // mirrors a half-authored pair (SecondSkinService.BuildRows). Showing this panel's defaults there
        // would put the picker and the model on different colours. Off elsewhere, where an unset sub-row
        // really is neutral. Only the read is mirrored — Edit() below still materialises a fresh preset, so
        // merely LOOKING at an unset half never turns it into an authored one.
        var sub = (isA ? preset?.SubRowA : preset?.SubRowB)
               ?? (mirrorUnsetSubRows ? (isA ? preset?.SubRowB : preset?.SubRowA) : null);

        ColorTableSubRowPreset Edit()
        {
            var p = EnsurePreset(rows, row);
            if (isA) return p.SubRowA ??= new ColorTableSubRowPreset();
            return p.SubRowB ??= new ColorTableSubRowPreset();
        }

        // ── copy / paste this single sub-row (the "column") ──────────────────
        // The clipboard is shared with the other panel and with "Copy row", so copying sub-row A and
        // pasting into B works, and a row copied whole can seed a single sub-row here.
        var cs = Strings.Colors;
        if (ImGui.SmallButton($"{cs.CopySub}##copysub_{id}"))
            _subClip = Clone(sub ?? new ColorTableSubRowPreset());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.CopySubTip);

        ImGui.SameLine();
        using (ImRaii.Disabled(_subClip == null))
        {
            if (ImGui.SmallButton($"{cs.PasteSub}##pastesub_{id}"))
            {
                var p = EnsurePreset(rows, row);
                if (isA) p.SubRowA = Clone(_subClip!);
                else p.SubRowB = Clone(_subClip!);
                changed = true;
            }
        }
        if (_subClip == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(cs.NeedSubCopy);
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.PasteSubTip);

        // ── glow / target: hue-cycle this sub-row's mesh on the live character ──
        // The overlay's colour table is baked into its shell material(s) ss_{letter}.mtrl; game row for
        // this sub-row is (row-1)*2 + (A?0:1). Click-toggle: clicking again (or another sub-row) moves the
        // glow. A mod/option with several gear overlays shares one colour table, so all of them glow.
        if (gear && Highlighter != null && shellMaterialLeaves is { Count: > 0 })
        {
            int gameRow = (row - 1) * 2 + (isA ? 0 : 1);
            bool active = Highlighter.IsTarget(shellMaterialLeaves, gameRow);
            ImGui.SameLine();
            using (ProteusStyle.Selected(active))
                if (ImGui.SmallButton($"{(active ? cs.Glowing : cs.Glow)}##glow_{id}"))
                {
                    if (active) Highlighter.Clear();
                    else Highlighter.SetTarget(shellMaterialLeaves, gameRow);
                }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.GlowGearTip);
        }
        // Skin overlays have no live colour table; SkinGlow rebinds the body diffuse instead. Static
        // bright (it can't hue-cycle — the highlight is a baked 4K texture, rebuilt only on click).
        else if (!gear && SkinGlow != null && skinGlowTargets is { Count: > 0 })
        {
            bool active = SkinGlow.IsTarget(skinGlowTargets, row, isA);
            ImGui.SameLine();
            using (ProteusStyle.Selected(active))
                if (ImGui.SmallButton($"{(active ? cs.Glowing : cs.Glow)}##glow_{id}"))
                {
                    if (active) SkinGlow.Clear();
                    else SkinGlow.SetTarget(skinGlowTargets, row, isA);
                }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.GlowSkinTip);
        }

        // ── Colours ──────────────────────────────────────────────────────────
        ImGui.TextDisabled(cs.Colours);

        var diffuse = HexToVec3(sub?.Diffuse);
        ImGui.SetNextItemWidth(22);
        if (ImGui.ColorEdit3($"{cs.Diffuse}##d_{id}", ref diffuse, ImGuiColorEditFlags.NoInputs))
        {
            Edit().Diffuse = Vec3ToHex(diffuse);
            changed = true;
        }
        if (gear && ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.DiffuseGearTip);

        // Specular is a Cloth feature. Dimmed-but-clickable in Skin (touch to switch to Cloth), active in
        // Cloth. HIDDEN in Animated glow — like the Physical/Sphere block below — so a stray touch on a
        // dimmed control can't silently flip the overlay out of glow.
        if (mode != RenderMode.Glow)
        {
            using var d = ImRaii.PushStyle(ImGuiStyleVar.Alpha, material ? 1f : DimAlpha);
            var spec = HexToVec3(sub?.Specular);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"{cs.Specular}##s_{id}", ref spec, ImGuiColorEditFlags.NoInputs))
            {
                Edit().Specular = Vec3ToHex(spec);
                edited = FeatureEdit.Cloth;
                changed = true;
            }
        }
        // Glow colour applies in any gear mode (it's the emissive colour); dimmed only on Skin.
        {
            using var d = ImRaii.PushStyle(ImGuiStyleVar.Alpha, gear ? 1f : DimAlpha);
            var emCol = HexToVec3(sub?.EmissiveColor ?? sub?.Diffuse);
            ImGui.SetNextItemWidth(22);
            if (ImGui.ColorEdit3($"{cs.GlowColour}##ec_{id}", ref emCol, ImGuiColorEditFlags.NoInputs))
            {
                Edit().EmissiveColor = Vec3ToHex(emCol);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.GlowColourTip);
        }

        // Shown as a percentage. On an ANIMATED GLOW this is what the effect's brightness scales with, and
        // 0 switches it off — a scroll map is usually a saturated colour, so pushing this to 100% blows it
        // out and the surface reads white rather than as the pattern. A quarter is a good starting point;
        // that is what a newly switched-on effect seeds (ContentGlowRow.DefaultGlow).
        // The cap goes past 100% because the value is written as a Half — vanilla materials push it above
        // 1.0 for a brighter bloom. Drag is coarse for a quick sweep; ctrl+click to type an exact value.
        float emPct = (sub?.Emissive ?? 0f) * 100f;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragFloat($"{cs.GlowAmount}##e_{id}", ref emPct, 2.5f, 0f, 1000f, "%.1f%%"))
        {
            var cell = Edit();
            cell.Emissive = Math.Clamp(emPct / 100f, 0f, 10f);
            // Make the swatch beside this TRUE rather than only apparent. It draws an unset glow colour as
            // white, but the colour is only stored once someone opens the picker — so raising this on an
            // otherwise untouched row asked for a white glow and got a black one, since the writer resolves
            // the colour EmissiveColor → Diffuse and had neither.
            //
            // Written here, at the moment the user asks for glow, rather than resolved white at composite
            // time: that reinterprets rows already authored, and a mod carrying an inert Glow value would
            // start emitting at full strength without anyone touching it.
            //
            // Only when there is no colour to fall back on — a row with a diffuse keeps the documented
            // EmissiveColor → Diffuse fallback, which is how a red garment glows red without being told to.
            if (cell.Emissive > 0f && cell.EmissiveColor == null && cell.Diffuse == null)
                cell.EmissiveColor = WhiteHex;
            // Skin cannot emit, so asking for glow asks for a shell: classify the edit as Cloth and let
            // the inference move the overlay there, exactly as a sphere map or metalness does. The one
            // exception is Animated glow, where this slider is the scroll effect's own strength rather than
            // a feature request — treating it as Cloth there would kick the overlay out of Glow the moment
            // the author turned the effect up or down.
            edited = mode == RenderMode.Glow ? FeatureEdit.Neutral : FeatureEdit.Cloth;
            changed = true;
        }
        if (gear && ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.GlowAmountTip);

        // How much of that glow the scene's light takes back, and whether the surface goes with it.
        //
        // Gear only — the light response is applied by rewriting this row's emissive in the live colour
        // table, and skin has no colour table to rewrite. Dimmed-but-clickable when the row doesn't emit:
        // there is nothing to fade yet, but the control still has to be reachable so it can be set up
        // before the Glow is raised, per the dim-don't-hide rule the physical block follows.
        //
        // Neither raises `edited`. They modify a glow that is already there rather than asking for one, so
        // flipping the overlay to Cloth off the back of them would move a deliberately-plain Skin overlay
        // onto a shell for a setting that does nothing until someone turns the Glow up.
        if (lightResponseApplies)
        {
            bool emits = (sub?.Emissive ?? 0f) > 0f;
            using var d = ImRaii.PushStyle(ImGuiStyleVar.Alpha, gear && emits ? 1f : DimAlpha);

            float lightPct = (sub?.LightResponse ?? 0f) * 100f;
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragFloat($"{cs.LightResponse}##lr_{id}", ref lightPct, 1f, 0f, 100f, "%.0f%%"))
            {
                var cell = Edit();
                float v = Math.Clamp(lightPct / 100f, 0f, 1f);
                // Stored as null at zero rather than an explicit 0: that is what ContentGlowRow.IsBlank
                // reads to decide a sub-row says nothing and can be dropped, so an explicit zero would
                // pin an otherwise empty row into metadata.json forever.
                cell.LightResponse = v > 0f ? v : null;
                changed = true;
            }
            if (gear && ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.LightResponseTip);

            bool hide = sub?.HideInLight ?? false;
            if (ImGui.Checkbox($"{cs.HideInLight}##hl_{id}", ref hide))
            {
                Edit().HideInLight = hide;
                changed = true;
            }
            if (gear && ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.HideInLightTip);
        }

        // Opacity applies to both layers: on skin it scales the overlay's alpha, on gear it scales the
        // coverage that becomes the normal map's blue channel (the transparency gate).
        int op = sub?.Opacity ?? 0;
        ImGui.SetNextItemWidth(70);
        if (ImGui.DragInt($"{cs.Opacity}##o_{id}", ref op, 1f, -100, 100, "%d%%"))
        {
            Edit().Opacity = Math.Clamp(op, -100, 100);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cs.OpacityTip);

        // Blend is a SKIN idea: a print recolours what this mod painted into the skin, and a shell has a
        // real colour table of its own instead. Shown only where it can do something.
        if (mode == RenderMode.Skin)
        {
            int bl = (int)(sub?.Blend ?? RowBlend.Paint);
            ImGui.SetNextItemWidth(110);
            if (ImGui.Combo($"{cs.Blend}##bl_{id}", ref bl, cs.BlendNames, cs.BlendNames.Length))
            {
                Edit().Blend = (RowBlend)Math.Clamp(bl, 0, cs.BlendNames.Length - 1);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(cs.BlendTip);
        }

        // Roughness / metalness / sphere map belong to Cloth. Dimmed-but-clickable in Skin (touch
        // one to switch to Cloth), active in Cloth. HIDDEN in Animated glow: they don't apply there, AND
        // SphereIntensity is repurposed by characterscroll as the effect's visibility — exposing it as a
        // "sphere" control let a stray 0 silently kill the glow.
        //
        // Except when the values are the AUTHOR'S. A shell's material is built from a neutral template, so
        // hiding these in glow hides nothing anyone chose; an imported pack's material is its author's and
        // arrives with whatever they set — the piercings carry metalness 1.0 — so the controls have to stay
        // reachable or a value that IS in the material cannot be changed. The sphere stays hidden either
        // way: on this shader its intensity is the effect's visibility, not a sphere at all.
        if (mode != RenderMode.Glow || authoredPhysical)
        {
            using var d = ImRaii.PushStyle(ImGuiStyleVar.Alpha, material ? 1f : DimAlpha);

            ImGui.TextDisabled(cs.Physical);
            if (!material) { ImGui.SameLine(); ImGui.TextDisabled(cs.ClothSuffix); }

            // The values the MATERIAL already holds, when the caller supplied them, instead of this editor's
            // neutral defaults. A shell's material is built from a neutral template so 0.5 / 0 is what is
            // really there; an imported pack's is its author's, and showing 0 over a metalness of 1.0 made
            // the panel describe a different material from the one on screen — and made the control that
            // would fix it look as though it already had.
            int cell = (row - 1) * 2 + (isA ? 0 : 1);
            var baseline = physicalBaseline is { } bl && cell >= 0 && cell < bl.Count
                ? bl[cell] : (Roughness: 0.5f, Metalness: 0f);

            float rough = sub?.Roughness ?? baseline.Roughness;
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragFloat($"{cs.Roughness}##r_{id}", ref rough, 0.01f, 0f, 1f, "%.2f"))
            {
                Edit().Roughness = Math.Clamp(rough, 0f, 1f);
                // Roughness isn't a mode trigger (it does nothing without metal/sphere), so don't flip to Cloth.
                changed = true;
            }

            float metal = sub?.Metalness ?? baseline.Metalness;
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragFloat($"{cs.Metalness}##m_{id}", ref metal, 0.01f, 0f, 1f, "%.2f"))
            {
                Edit().Metalness = Math.Clamp(metal, 0f, 1f);
                edited = FeatureEdit.Cloth;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(MetalTip);

            // Never on a scrolling material, even when the rest of this block is shown for an authored one:
            // SphereIntensity is that shader's effect visibility, so a "sphere" control here is a switch
            // labelled as something else, and a stray 0 in it silently kills the glow.
            if (mode == RenderMode.Glow) return;

            ImGui.TextDisabled(cs.SphereMap);

            int sphere = sub?.SphereMap ?? 0;
            DrawSpherePicker(id, ref sphere, out bool sphereChanged);
            if (sphereChanged)
            {
                var e = Edit();
                e.SphereMap = Math.Clamp(sphere, 0, SphereMapPreview.Count - 1);
                // A sphere needs BOTH index and intensity non-zero to show, so default the intensity to 3
                // when picking one for the first time (leave a user-set value alone).
                if (e.SphereMap > 0 && (e.SphereIntensity ?? 0f) <= 0f)
                    e.SphereIntensity = 3f;
                edited = FeatureEdit.Cloth;
                changed = true;
            }

            // Cap goes past 1.0 for the same reason as Glow: the value is written as a Half, so it can over-
            // drive the sphere-map contribution for a stronger effect than the vanilla 0–1 range allows.
            float sphereInt = sub?.SphereIntensity ?? 0f;
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragFloat($"{cs.Intensity}##si_{id}", ref sphereInt, 0.05f, 0f, 10f, "%.2f"))
            {
                Edit().SphereIntensity = Math.Clamp(sphereInt, 0f, 10f);
                edited = FeatureEdit.Cloth;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(SphereTip);

            // ── the fabric weave ─────────────────────────────────────────────
            // Below the Glow early-return above, so this is hidden on a scrolling material for the same
            // reason the sphere is: characterscroll reassigns halves in this neighbourhood, and a control
            // whose value the shader reads as something else is worse than no control.
            ImGui.TextDisabled(cs.Tile);

            int tile = sub?.Tile ?? -1;
            DrawTilePicker(id, ref tile, out bool tileChanged);
            if (tileChanged)
            {
                var e = Edit();
                e.Tile = tile < 0 ? null : Math.Clamp(tile, 0, TilePreview.Count - 1);
                if (e.Tile != null)
                {
                    // A weave needs BOTH a pattern and a strength: the shell's material is built with tile
                    // alpha zeroed on every row, so an index alone is an invisible tile. Seed it the way the
                    // sphere seeds its intensity, and leave a value the user has already chosen alone.
                    if ((e.TileStrength ?? 0f) <= 0f) e.TileStrength = 1f;
                }
                else
                {
                    // Clearing the pattern clears what only meant anything with it, so the sub-row can go
                    // back to reading as blank — see ContentGlowRow.IsBlank, which decides whether a cell
                    // survives at all.
                    e.TileStrength = null;
                    e.TileScaleU = null;
                    e.TileScaleV = null;
                }
                edited = FeatureEdit.Cloth;
                changed = true;
            }

            // Strength and scale do nothing without a pattern to apply them to. Dimmed rather than hidden:
            // reachable, so the panel doesn't reshuffle as soon as a weave is picked, but visibly inert.
            //
            // Multiplied INTO the enclosing alpha rather than set over it, because PushStyle assigns — the
            // whole block is already dimmed in Skin mode, and a bare 1f here would make these three controls
            // the brightest thing on a panel where everything around them is faded.
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha,
                       ImGui.GetStyle().Alpha * (sub?.Tile != null ? 1f : DimAlpha)))
            {
                float tileStrength = (sub?.TileStrength ?? 0f) * 100f;
                ImGui.SetNextItemWidth(70);
                if (ImGui.DragFloat($"{cs.TileStrength}##ts_{id}", ref tileStrength, 1f, 0f, 100f, "%.0f%%"))
                {
                    Edit().TileStrength = Math.Clamp(tileStrength / 100f, 0f, 1f);
                    edited = FeatureEdit.Cloth;
                    changed = true;
                }

                float scaleU = sub?.TileScaleU ?? GearMaterialWriter.DefaultTileScale;
                ImGui.SetNextItemWidth(70);
                if (ImGui.DragFloat($"{cs.TileScaleU}##tsu_{id}", ref scaleU, 0.25f, 0.1f, 256f, "%.1f"))
                {
                    Edit().TileScaleU = Math.Clamp(scaleU, 0.1f, 256f);
                    edited = FeatureEdit.Cloth;
                    changed = true;
                }

                float scaleV = sub?.TileScaleV ?? GearMaterialWriter.DefaultTileScale;
                ImGui.SetNextItemWidth(70);
                if (ImGui.DragFloat($"{cs.TileScaleV}##tsv_{id}", ref scaleV, 0.25f, 0.1f, 256f, "%.1f"))
                {
                    Edit().TileScaleV = Math.Clamp(scaleV, 0.1f, 256f);
                    edited = FeatureEdit.Cloth;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(TileScaleTip);
            }
        }
    }

    // ── copy / paste clipboard ────────────────────────────────────────────────
    // Static so it persists across options and windows for the session — copy from one overlay,
    // paste into another. Both hold deep copies, so later edits to the source don't mutate them.
    private static ColorTableSubRowPreset? _subClip;
    private static ColorTableRowPreset? _rowClip;

    /// <summary>Deep copy of a sub-row. All fields are value types or immutable strings.
    /// <para/>
    /// Delegates rather than listing the fields again: this was a second, hand-written copy of the same
    /// list, and a field added to the preset reached only one of them — so copy/paste in the grid would
    /// silently drop whatever was newest.</summary>
    private static ColorTableSubRowPreset Clone(ColorTableSubRowPreset s) => s.Clone();

    private static ColorTableRowPreset CloneRow(ColorTableRowPreset? p) => new()
    {
        SubRowA = p?.SubRowA is { } a ? Clone(a) : null,
        SubRowB = p?.SubRowB is { } b ? Clone(b) : null,
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static ColorTableRowPreset EnsurePreset(List<ColorTableRowPreset> rows, int row)
    {
        var p = rows.FirstOrDefault(r => r.Row == row);
        if (p == null) { p = new ColorTableRowPreset { Row = row }; rows.Add(p); }
        return p;
    }

    internal static Vector3 HexToVec3(string? hex)
    {
        if (hex == null) return Vector3.One;
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        try
        {
            int v = Convert.ToInt32(hex, 16);
            return new Vector3((v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
        }
        catch { return Vector3.One; }
    }

    internal static string Vec3ToHex(Vector3 c)
    {
        int r = Math.Clamp((int)(c.X * 255), 0, 255);
        int g = Math.Clamp((int)(c.Y * 255), 0, 255);
        int b = Math.Clamp((int)(c.Z * 255), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
