using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

public static partial class ColorTableEditor
{
    private sealed class GlowFooter
    {
        private readonly string idScope;
        private readonly string advancedScope;
        private readonly IReadOnlyList<OverlayDescriptor> overlays;
        private readonly GearSettingsPreset? ovr;
        private readonly IReadOnlyList<(string Name, string Path, bool FromMod)> effects;
        private readonly Func<bool>? onReset;
        private readonly string? resetDisabledReason;
        private readonly Action? drawExtraAdvanced;
        private readonly Action? drawBelowGlow;
        private readonly Action? drawInEffects;
        private readonly string? modeForced;
        private readonly string? modeForcedTip;
        private readonly bool promotedToGear;
        private readonly string? noShellReason;
        private readonly bool skinTintApplies;
        private readonly bool overrideActive;
        private readonly bool toeCapActive;
        private bool changed;
        private OverlayDescriptor first = null!;
        private string? curScroll;
        private float? curSpeedX;
        private float? curSpeedY;
        private float? curTileX;
        private float? curTileY;
        private bool curLock;
        private float? curSkinMask;
        private RenderMode mode;
        private ColorsStrings cs = null!;

        public GlowFooter(string idScope, string advancedScope, IReadOnlyList<OverlayDescriptor> overlays, GearSettingsPreset? ovr, IReadOnlyList<(string Name, string Path, bool FromMod)> effects, Func<bool>? onReset, string? resetDisabledReason, Action? drawExtraAdvanced, Action? drawBelowGlow, Action? drawInEffects, string? modeForced, string? modeForcedTip, bool promotedToGear, string? noShellReason, bool skinTintApplies, bool overrideActive, bool toeCapActive)
        {
            this.idScope = idScope;
            this.advancedScope = advancedScope;
            this.overlays = overlays;
            this.ovr = ovr;
            this.effects = effects;
            this.onReset = onReset;
            this.resetDisabledReason = resetDisabledReason;
            this.drawExtraAdvanced = drawExtraAdvanced;
            this.drawBelowGlow = drawBelowGlow;
            this.drawInEffects = drawInEffects;
            this.modeForced = modeForced;
            this.modeForcedTip = modeForcedTip;
            this.promotedToGear = promotedToGear;
            this.noShellReason = noShellReason;
            this.skinTintApplies = skinTintApplies;
            this.overrideActive = overrideActive;
            this.toeCapActive = toeCapActive;
        }

        public bool Run(out FeatureEdit edited)
        {
            edited = FeatureEdit.Neutral;
            if (!ResolveState()) return false;
            DrawEffects(ref edited);
            return DrawAdvanced();
        }

        private bool ResolveState()
        {
            if (overlays.Count == 0)
            {
                // Nothing for the glow controls or Advanced to edit, but the mod-wide sections are the mod's, not
                // this option's: returning before them left Skindent and Geometry unreachable whenever the only
                // active option carried no overlay art (a content-only option in a mixed pack).
                if (EffectsHeader(advancedScope))
                    drawInEffects?.Invoke();
                drawBelowGlow?.Invoke();
                return false;
            }

            changed = false;
            first = overlays[0];

            curScroll = ovr != null ? ovr.Scroll : first.Scroll;
            curSpeedX = ovr != null ? ovr.ScrollSpeedX : first.ScrollSpeedX;
            curSpeedY = ovr != null ? ovr.ScrollSpeedY : first.ScrollSpeedY;
            curTileX = ovr != null ? ovr.ScrollTilingX : first.ScrollTilingX;
            curTileY = ovr != null ? ovr.ScrollTilingY : first.ScrollTilingY;
            curLock = ovr != null ? (ovr.ManualShaderLock ?? false) : first.ManualShaderLock;
            curSkinMask = ovr != null ? ovr.SkinToneMask : first.SkinToneMask;
            mode = RenderModeInference.ModeOf(ovr?.Layer ?? first.Layer, ovr != null ? ovr.Shader : first.Shader);
            if (promotedToGear && mode == RenderMode.Skin) mode = RenderMode.Cloth;   // stacked above gear → renders as a shell

            cs = Strings.Colors;
            return true;
        }

        private void DrawEffects(ref FeatureEdit edited)
        {
            // ── Effects: the glow effect (+ its scroll speed/tiling) and whatever the caller adds after it ──
            if (EffectsHeader(advancedScope))
            {
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

                drawInEffects?.Invoke();
            }

            // Mod-wide sections that belong below the glow controls but above Advanced. Between the two because
            // Advanced is where the per-OPTION settings live, and burying a mod-wide section inside it reads as
            // belonging to whichever tab happens to be open.
            drawBelowGlow?.Invoke();
        }

        private bool DrawAdvanced()
        {
            // ── Advanced (mode pin) at the very bottom, with the "Rendering as" badge to its right ──
            // AllowItemOverlap (this ImGui predates AllowOverlap) so the header does not swallow the "Back to auto" click on top of it;
            // the explicit SetItemAllowOverlap below is redundant but kept deliberately.
            // "###" and not "##": the id then ignores the localized label, so a language switch does not reset the state.
            bool advOpen = ImGui.CollapsingHeader($"{cs.Advanced}###adv_{advancedScope}",
                ImGuiTreeNodeFlags.AllowItemOverlap);
            ImGui.SetItemAllowOverlap();

            ImGui.SameLine(0f, 24f);
            DrawRenderingAsBadge(mode);
            ImGui.SameLine();
            // A forced mode says so beside the badge, rather than "(auto)" crediting the inference.
            ImGui.TextDisabled(modeForced ?? (curLock ? cs.Pinned : cs.Auto));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(modeForced != null ? (modeForcedTip ?? modeForced)
                                                    : (curLock ? cs.PinnedTip : cs.AutoTip));
            // No "Back to auto" when the mode is not this option's to release.
            if (curLock && modeForced == null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"{cs.BackToAuto}##{idScope}")) { SetLock(false); changed = true; }
            }

            // A non-default skin tint, readable while Advanced is shut, since it varies per option.
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
                DrawMode();
                DrawSkinTint(showSkinTint);
                DrawWholeSkin(showSkinTint);
                DrawAsymmetric(showSkinTint);
                DrawReinforcedToe();
                DrawModSettings();
            }

            return changed;
        }

        private void DrawMode()
        {
            // Only the mode radios are replaced when the mode is forced; the rest of the footer still draws.
            if (modeForced != null)
            {
                // The full explanation here, where the radios' hint would be; the short marker belongs beside the badge.
                ImGui.TextDisabled(modeForcedTip ?? modeForced);
            }
            else
            {
                ImGui.TextDisabled(cs.ForceModeHint);
                foreach (var m in new[] { RenderMode.Skin, RenderMode.Cloth, RenderMode.Glow })
                {
                    bool sel = curLock && mode == m;
                    // With no body to cut a shell from, Cloth and Glow are not reachable modes for this option.
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
            }
        }

        private void DrawSkinTint(bool showSkinTint)
        {
            // Skin-tint suppression, per option, drawn only in Skin mode: the suppression pass never runs for a shell.
            if (showSkinTint)
            {
                float tint = curSkinMask ?? 1f;
                ImGui.SetNextItemWidth(90);
                if (ImGui.DragFloat($"{cs.SkinTint}##skintint_{idScope}", ref tint, 0.01f, 0f, 1f, "%.2f"))
                {
                    SetSkinMask(Math.Clamp(tint, 0f, 1f));
                    // Deliberately does NOT set `edited`: a tint nudge must not re-infer the render mode.
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.SkinTintTip);
            }
        }

        private void DrawWholeSkin(bool showSkinTint)
        {
            // "This overlay IS the skin": one declaration moving normal mode and suppression together.
            // NormalMode has no binding override, so `overrideActive` hides the control.
            if (showSkinTint && !overrideActive && overlays.Any(d => d.Normal != null))
            {
                bool wholeSkin = first.NormalMode == NormalMode.Replace;
                if (ImGui.Checkbox($"{cs.WholeSkin}##normalmode_{idScope}", ref wholeSkin))
                {
                    foreach (var d in overlays)
                        d.NormalMode = wholeSkin ? NormalMode.Replace : NormalMode.Compound;
                    // Untick restores the default (1, stored as omitted), so the switch is its own inverse.
                    SetSkinMask(wholeSkin ? 0f : 1f);
                    // Not a FeatureEdit: how a normal blends says nothing about skin vs shell.
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.WholeSkinTip);
            }
        }

        private void DrawAsymmetric(bool showSkinTint)
        {
            // Asymmetric art is DECLARED, because nothing can measure it. Skin layer only: a shell never folds its art.
            if (showSkinTint && !overrideActive)
            {
                bool asymmetric = first.AsymmetricArt == true;
                if (ImGui.Checkbox($"{cs.Asymmetric}##asymmetric_{idScope}", ref asymmetric))
                {
                    // Cleared to null rather than false: absent means symmetric.
                    foreach (var d in overlays) d.AsymmetricArt = asymmetric ? true : null;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.AsymmetricTip);
            }
        }

        private void DrawReinforcedToe()
        {
            // Reinforced toe: a SHELL setting (density is the shell normal's blue), shown only with a cap. Still live under a
            // preset or design, because no override carries density; the caller saves it to the mod directly.
            if (toeCapActive && mode != RenderMode.Skin)
            {
                bool reinforced = first.ToeCapDensity > 0;
                if (ImGui.Checkbox($"{cs.ReinforcedToe}##reinftoe_{idScope}", ref reinforced))
                {
                    foreach (var d in overlays)
                        d.ToeCapDensity = reinforced ? ReinforcedToeDefault : 0;
                    // Not a FeatureEdit: toe density says nothing about skin vs shell.
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.ReinforcedToeTip);

                if (reinforced)
                {
                    int density = Math.Clamp(first.ToeCapDensity, 1, 100);
                    ImGui.SetNextItemWidth(140);
                    if (ImGui.SliderInt($"{cs.ToeDensity}##reinfamt_{idScope}", ref density, 1, 100, "%d%%"))
                    {
                        foreach (var d in overlays) d.ToeCapDensity = Math.Clamp(density, 1, 100);
                        changed = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(cs.ToeDensityTip);
                }

                if (overrideActive)
                    ImGui.TextDisabled(cs.ReinforcedToeSavedNote);
            }
        }

        private void DrawModSettings()
        {
            // Whole-mod settings the caller owns; everything above this line is per-option, everything below is not.
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

        private void SetScroll(string? s)       { if (ovr != null) ovr.Scroll = s;  else foreach (var d in overlays) d.Scroll = s; }

        private void SetSpeed(float x, float y) { if (ovr != null) { ovr.ScrollSpeedX = x; ovr.ScrollSpeedY = y; } else foreach (var d in overlays) { d.ScrollSpeedX = x; d.ScrollSpeedY = y; } }

        private void SetTile(float x, float y)  { if (ovr != null) { ovr.ScrollTilingX = x; ovr.ScrollTilingY = y; } else foreach (var d in overlays) { d.ScrollTilingX = x; d.ScrollTilingY = y; } }

        private void SetLock(bool v)            => SetManualShaderLock(overlays, ovr, v);

        // Asymmetric on purpose, and GearSettingsPreset.ApplyTo depends on it. On the DESCRIPTORS, exactly
        // 1 is the default, so it is stored as omitted — that keeps sidecars free of no-op
        // "SkinToneMask": 1 lines and keeps the documented "omitted = full masking" true. In a design
        // OVERRIDE, null is reserved to mean "this binding predates the field, defer to the mod", so a
        // user who drags to 1.00 must write an explicit 1.00 or their choice would read as silence and
        // the author's value would win instead.
        private void SetSkinMask(float v)
        {
            if (ovr != null) { ovr.SkinToneMask = v; return; }
            float? stored = Math.Abs(v - 1f) < SkinTintEpsilon ? null : v;
            foreach (var d in overlays) d.SkinToneMask = stored;
        }
    }
}
