using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

public static partial class ColorTableEditor
{
    private sealed class SubRowPanel
    {
        // Dimmed-but-clickable: a feature the mode ignores.
        private const float DimAlpha = 0.5f;

        private readonly string id;
        private readonly List<ColorTableRowPreset> rows;
        private readonly int row;
        private readonly bool isA;
        private readonly RenderMode mode;
        private readonly IReadOnlyList<string>? shellMaterialLeaves;
        private readonly IReadOnlyList<Interop.SkinGlowTarget>? skinGlowTargets;
        private readonly bool authoredPhysical;
        private readonly IReadOnlyList<(float Roughness, float Metalness)>? physicalBaseline;
        private readonly bool mirrorUnsetSubRows;
        private readonly bool lightResponseApplies;
        private bool gear;
        private bool material;
        private ColorTableSubRowPreset? sub;
        private ColorsStrings cs = null!;

        public SubRowPanel(string id, List<ColorTableRowPreset> rows, int row, bool isA, RenderMode mode, IReadOnlyList<string>? shellMaterialLeaves, IReadOnlyList<Interop.SkinGlowTarget>? skinGlowTargets, bool authoredPhysical, IReadOnlyList<(float Roughness, float Metalness)>? physicalBaseline, bool mirrorUnsetSubRows, bool lightResponseApplies)
        {
            this.id = id;
            this.rows = rows;
            this.row = row;
            this.isA = isA;
            this.mode = mode;
            this.shellMaterialLeaves = shellMaterialLeaves;
            this.skinGlowTargets = skinGlowTargets;
            this.authoredPhysical = authoredPhysical;
            this.physicalBaseline = physicalBaseline;
            this.mirrorUnsetSubRows = mirrorUnsetSubRows;
            this.lightResponseApplies = lightResponseApplies;
        }

        public void Run(ref FeatureEdit edited, ref bool changed)
        {
            ResolveSubRow();
            DrawCopyPaste(ref changed);
            DrawLocator();
            DrawColours(ref edited, ref changed);
            DrawGlow(ref edited, ref changed);
            DrawSurface(ref edited, ref changed);
        }

        private void ResolveSubRow()
        {
            gear = mode != RenderMode.Skin;
            material = mode == RenderMode.Cloth;   // sphere / metal / roughness live here

            var preset = rows.FirstOrDefault(r => r.Row == row);
            // DISPLAY falls back to the other sub-row where that is what RENDERS — the Masks tab, whose shell
            // mirrors a half-authored pair (ShellColorRows.BuildRows). Showing this panel's defaults there
            // would put the picker and the model on different colours. Off elsewhere, where an unset sub-row
            // really is neutral. Only the read is mirrored — Edit() below still materialises a fresh preset, so
            // merely LOOKING at an unset half never turns it into an authored one.
            sub = (isA ? preset?.SubRowA : preset?.SubRowB)
                   ?? (mirrorUnsetSubRows ? (isA ? preset?.SubRowB : preset?.SubRowA) : null);
        }

        private void DrawCopyPaste(ref bool changed)
        {
            // ── copy / paste this single sub-row (the "column") ──────────────────
            // The clipboard is shared with the other panel and with "Copy row", so copying sub-row A and
            // pasting into B works, and a row copied whole can seed a single sub-row here.
            cs = Strings.Colors;
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
        }

        private void DrawLocator()
        {
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
        }

        private void DrawColours(ref FeatureEdit edited, ref bool changed)
        {
            // ── Colours ──────────────────────────────────────────────────────────
            ProteusStyle.SubHeader(cs.Colours);

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

            // Opacity applies to both layers: on skin it scales the overlay's alpha, on gear it scales the
            // coverage that becomes the normal map's blue channel (the transparency gate).
            //
            // Sits at METHOD-BODY depth, deliberately outside the bare block below. One brace lower and it
            // would inherit the glow colour's `gear ? 1f : DimAlpha` push, greying out the most-used control on
            // the panel on exactly the mode — Skin — where it does the most work.
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
                    var e = Edit();
                    e.Blend = (RowBlend)Math.Clamp(bl, 0, cs.BlendNames.Length - 1);
                    // A row becoming a print for the first time lands on everything beneath, like a Photoshop blend
                    // layer. An unset target on a row saved earlier still reads as OwnPaint, so old prints do not move.
                    if (e.Blend != RowBlend.Paint && e.PrintOnto == null) e.PrintOnto = PrintTarget.Beneath;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(cs.BlendTip);

                if (sub is { Blend: not RowBlend.Paint })
                {
                    int onto = (int)(sub.PrintOnto ?? PrintTarget.OwnPaint);
                    ImGui.SetNextItemWidth(170);
                    if (ImGui.Combo($"{cs.PrintOnto}##po_{id}", ref onto, cs.PrintOntoNames, cs.PrintOntoNames.Length))
                    {
                        Edit().PrintOnto = (PrintTarget)Math.Clamp(onto, 0, cs.PrintOntoNames.Length - 1);
                        changed = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(cs.PrintOntoTip);
                }
            }
        }

        private void DrawGlow(ref FeatureEdit edited, ref bool changed)
        {
            // ── Glow ─────────────────────────────────────────────────────────────
            // Its own section rather than four more rows under "Colours", which named none of them. Everything
            // below to the end of the light-response pair belongs to it.
            //
            // The heading is at method-body depth, ABOVE the bare block: the glow colour is dimmed on Skin but
            // the amount below it is not (raising it is a request for a shell), so a heading that faded with
            // the colour picker alone would be lying about the section it names.
            ProteusStyle.SubHeader(cs.GlowSection);

            // Glow colour applies in any gear mode (it's the emissive colour); dimmed only on Skin.
            //
            // The bare braces are load-bearing and not style: they bound the alpha push to this one control.
            // Without them it would run to the end of the method and silently dim the glow amount, the
            // light-response pair, and — through the nested push further down — the whole Physical block.
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
        }

        private void DrawSurface(ref FeatureEdit edited, ref bool changed)
        {
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

                ProteusStyle.SubHeader(cs.Physical, material ? null : cs.ClothSuffix);

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

                ProteusStyle.SubHeader(cs.SphereMap);

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
                ProteusStyle.SubHeader(cs.Tile);

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

        private ColorTableSubRowPreset Edit()
        {
            var p = EnsurePreset(rows, row);
            if (isA) return p.SubRowA ??= new ColorTableSubRowPreset();
            return p.SubRowB ??= new ColorTableSubRowPreset();
        }
    }
}
