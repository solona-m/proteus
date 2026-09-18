using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public sealed partial class LuminisImportService
{
    private sealed class LuminisModWrite
    {
        private readonly string root;
        private readonly string modName;
        private readonly string author;
        private readonly ImportPreview preview;
        private readonly IReadOnlyList<string> materials;
        private readonly string? suffixOverride;
        private readonly TextureLoader? encodeTo;
        private readonly Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode;
        private readonly IPluginLog? log;
        private string overlaysDir = null!;
        private string effectsDir = null!;
        private IReadOnlyList<TexturePlan> importable = null!;
        private bool qualified;
        private bool retargeted;
        private Dictionary<long, TexToolsPackage.Payload> payloads = null!;
        private List<OverlayOption> skinOptions = null!;
        private List<OverlayOption> glowOptions = null!;
        private List<OverlayOption> options = null!;

        public LuminisModWrite(string root, string modName, string author, ImportPreview preview, IReadOnlyList<string> materials, string? suffixOverride, TextureLoader? encodeTo, Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode, IPluginLog? log)
        {
            this.root = root;
            this.modName = modName;
            this.author = author;
            this.preview = preview;
            this.materials = materials;
            this.suffixOverride = suffixOverride;
            this.encodeTo = encodeTo;
            this.decode = decode;
            this.log = log;
        }

        public WrittenOptions Run()
        {
            Prepare();
            WriteOptions();
            return WriteMetadata();
        }

        private void Prepare()
        {
            overlaysDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "overlays");
            effectsDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir,
                                          SidecarDiscoveryService.EffectsSubdir);
            Directory.CreateDirectory(overlaysDir);
            Directory.CreateDirectory(effectsDir);

            importable = preview.Importable;
            qualified = importable.Count > 1;

            // A retarget is the user moving the combo off the value the tab seeded, decided once for the whole
            // write, not per plan: otherwise mixed-body packs lose their per-token remap.
            retargeted = suffixOverride != null
                           && !string.Equals(suffixOverride, preview.DefaultSuffix, StringComparison.OrdinalIgnoreCase);

            // Re-decoded rather than carried on the preview, which would hold every sheet as RGBA.
            payloads = TexToolsPackage.ReadPayloads(
                preview.SourcePath, importable.ToDictionary(t => t.Offset, t => t.Size));

            // Skin options first, so the author's body paints under the glow. Declaration order is PAINT order,
            // and on a fresh import (all stack ranks tied) it is the only order.
            skinOptions = new List<OverlayOption>();
            glowOptions = new List<OverlayOption>();
        }

        private void WriteOptions()
        {
            foreach (var plan in importable) WriteMaterialOptions(plan);

            options = new List<OverlayOption>();
            options.AddRange(skinOptions);
            options.AddRange(glowOptions);
        }

        /// <summary>Writes one material's art into the mod and adds its skin and glow options.</summary>
        private void WriteMaterialOptions(TexturePlan plan)
        {
            if (!payloads.TryGetValue(plan.Offset, out var payload) || !payload.IsTexture)
            {
                log?.Warning("[Proteus] luminis import: {0} decoded on preview and not on write — skipped",
                    plan.Label);
                return;
            }

            if (decode(payload.Tex!, plan.Label) is not { } src)
            {
                log?.Warning("[Proteus] luminis import: {0} could not be decoded — skipped", plan.Label);
                return;
            }

            var (rgba, w, h) = src;
            string bodyType = retargeted
                ? UVRemapService.InferBodyType(materials.FirstOrDefault() ?? "") ?? plan.BodyType ?? ""
                : plan.BodyType ?? "";

            OverlayDescriptor Base(string diffuse) => new()
            {
                Layer = OverlayLayer.Skin,
                SourceBodyType = string.IsNullOrEmpty(bodyType) ? null : bodyType,
                MaterialGamePaths = [.. materials],
                Diffuse = diffuse,
            };

            // ── the glow ──
            // Coverage is PRESENCE, not intensity: AL's alpha says how brightly a pixel glows, not how solid it
            // is. Multiplied up rather than thresholded, so flat interior plateaus go opaque and the outline stays soft.
            var glow = new byte[rgba.Length];
            Buffer.BlockCopy(rgba, 0, glow, 0, rgba.Length);
            for (int i = 3; i < glow.Length; i += 4)
                glow[i] = (byte)Math.Min(255, (255 - rgba[i]) * CoverageGain);

            // The scroll map carries the colour scaled by AL's per-pixel intensity; black where nothing glows.
            var scroll = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int lit = 255 - rgba[i + 3];
                scroll[i] = (byte)((rgba[i] * lit + 127) / 255);
                scroll[i + 1] = (byte)((rgba[i + 1] * lit + 127) / 255);
                scroll[i + 2] = (byte)((rgba[i + 2] * lit + 127) / 255);
                scroll[i + 3] = 255;
            }

            var glowFile = Materialize(glow, w, h, overlaysDir, plan.Stem + "_glow", encodeTo);
            var scrollFile = Materialize(scroll, w, h, effectsDir, plan.Stem + "_glow", encodeTo);

            var glowDescriptor = Base("overlays/" + glowFile);

            // Layer AND Shader stated outright, as ColorTableEditor.ApplyMode writes for RenderMode.Glow:
            // promotion alone changes only the layer and leaves plain character.shpk, which has no scroll map.
            glowDescriptor.Layer = OverlayLayer.Gear;
            glowDescriptor.Shader = RenderModeInference.GlowShader;
            glowDescriptor.Scroll = scrollFile;
            // Zero explicitly: an unset speed takes GearMaterialWriter's default and slides the tattoo.
            glowDescriptor.ScrollSpeedX = 0f;
            glowDescriptor.ScrollSpeedY = 0f;
            // One-to-one: the scroll map IS the body sheet, so tiling it would repeat the tattoo.
            glowDescriptor.ScrollTilingX = 1f;
            glowDescriptor.ScrollTilingY = 1f;

            // One row per plateau, so each region can later get its own colour and light response; all written
            // identically here. A sheet with one plateau (or none) authors no index.
            var bands = GlowBands(rgba);
            int rowCount = Math.Max(1, bands.Count);
            if (bands.Count > 1)
            {
                // PNG, not BC7: an index texture is a lookup, and lossy compression can move a texel across a row.
                glowDescriptor.Index = "overlays/" + Materialize(
                    BuildGlowIndex(rgba, bands), w, h, overlaysDir, plan.Stem + "_glow_id", encodeTo: null);
                log?.Information("[Proteus] luminis import: {0} — {1} glow region(s), rows 1–{1}",
                    plan.Label, bands.Count);
            }

            var glowRows = new List<ColorTableRowPreset>();
            for (int r = 1; r <= rowCount; r++)
                glowRows.Add(new ColorTableRowPreset
                {
                    // With an index the rows start at 1 and count up with the plateaus; without one the
                    // shell samples the fabricated (255,255,0), which is row 16.
                    Row = bands.Count > 1 ? r : GlowShell.Row,
                    SubRowA = new ColorTableSubRowPreset
                    {
                        Emissive = GlowShell.Emissive,
                        // Neutral: the scroll map is coloured and carries its own hue.
                        EmissiveColor = RenderModeInference.GlowEmissiveColour,
                        Diffuse = GlowShell.SurfaceColour,
                        // Dark-only, which is what these tattoos were: see GlowLightResponse.
                        LightResponse = GlowLightResponse,
                        HideInLight = true,
                    },
                });

            glowOptions.Add(new OverlayOption
            {
                Name = GlowOptionName(plan, qualified),
                Overlays = [glowDescriptor],
                // On the OPTION, never top level: top-level rows are inherited by the skin option, and an
                // emissive there would promote it to a gear shell.
                ColorTableRows = glowRows,
            });

            // ── the author's own skin ──
            var skin = new byte[rgba.Length];
            Buffer.BlockCopy(rgba, 0, skin, 0, rgba.Length);
            for (int i = 3; i < skin.Length; i += 4) skin[i] = 255;

            var skinDescriptor = Base("overlays/" + Materialize(skin, w, h, overlaysDir, plan.Stem + "_skin", encodeTo));

            // SkinToneMask 0: this overlay IS skin, so the wearer's skin tone must not be suppressed under it.
            skinDescriptor.SkinToneMask = 0f;

            // Pinned so it is never promoted to a gear shell (character.shpk has no skin-tone term), e.g. after
            // the user reorders the stack tabs.
            skinDescriptor.ManualShaderLock = true;

            skinOptions.Add(new OverlayOption
            {
                Name = SkinOptionName(plan, qualified),
                Overlays = [skinDescriptor],
            });
        }

        private WrittenOptions WriteMetadata()
        {
            var metadata = new ProteusMetadata
            {
                FormatVersion = 1,
                Name = modName,
                Author = author,
                OptionGroups =
                [
                    new OverlayOptionGroup { PenumbraGroupName = GroupName, Options = options },
                ],
            };

            var metaJson = JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite);
            PenumbraModMeta.AtomicWrite(
                Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json"), metaJson);

            var description = string.IsNullOrWhiteSpace(preview.Description)
                ? string.Format(Loc.Localize("Import.Luminis.Description.Fmt",
                    "Imported from the Atramentum Luminis pack \"{0}\"."),
                    Path.GetFileName(preview.SourcePath))
                : preview.Description + "\n\n" + string.Format(Loc.Localize("Import.Luminis.Description.Fmt",
                    "Imported from the Atramentum Luminis pack \"{0}\"."),
                    Path.GetFileName(preview.SourcePath));

            PenumbraModMeta.AtomicWrite(
                Path.Combine(root, PenumbraModMeta.MetaFile),
                PenumbraModMeta.NewMetaJson(modName, author, description, preview.Version, preview.Website));

            // Proteus does the real texture redirection itself at composite time, so the default option would
            // be empty — which Penumbra flags as "changes nothing". Same harmless self-swap the Create tab uses.
            PenumbraModMeta.WriteRedirects(
                root, modName,
                files: new Dictionary<string, string>(),
                swaps: new Dictionary<string, string>
                    { [ModCreationService.DummySwapPath] = ModCreationService.DummySwapPath });

            // A fresh install wears the first pair: the glow and the author's body texture, two halves of one
            // artwork. Skin options come first in `options`, so the glow's bit is its index past them.
            var defaultOn = new List<string>();
            ulong defaults = 0;
            if (skinOptions.Count > 0)
            {
                defaultOn.Add(skinOptions[0].Name);
                defaults |= 1UL;
            }
            if (glowOptions.Count > 0)
            {
                defaultOn.Add(glowOptions[0].Name);
                defaults |= 1UL << skinOptions.Count;
            }

            PenumbraModMeta.WriteMultiSelectGroup(
                root, 0, GroupName, [.. options.Select(o => o.Name)], defaults);

            return new([.. glowOptions.Select(o => o.Name)], defaultOn);
        }
    }
}
