using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public sealed partial class EmissiveSkinImportService
{
    private sealed class EmissiveModWrite
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
        private string? destinationType;
        private Dictionary<string, byte[]> payloads = null!;
        private List<OverlayOption> options = null!;
        private List<string?> wearsBodyType = null!;

        public EmissiveModWrite(string root, string modName, string author, ImportPreview preview, IReadOnlyList<string> materials, string? suffixOverride, TextureLoader? encodeTo, Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode, IPluginLog? log)
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

            // An override only overrides when the user actually RETARGETED — measured against the value the
            // Import tab seeded its combo with, not against each plan's own suffix. See the same guard in
            // LuminisImportService.WriteMod for what testing it per plan silently broke.
            retargeted = suffixOverride != null
                           && !string.Equals(suffixOverride, preview.DefaultSuffix, StringComparison.OrdinalIgnoreCase);

            // The UV space the art is being aimed AT, which is what decides the default option below: of several
            // sheets of one tattoo, the one already in the destination's space is the one that needs no
            // resampling, so it is the one to arrive switched on.
            destinationType = UVRemapService.InferBodyType(materials.FirstOrDefault() ?? "");

            // Re-decoded rather than carried on the preview: holding an 8192² sheet as RGBA for as long as the
            // preview is on screen costs a quarter of a gigabyte, and this pass runs off the framework thread
            // where the second decode costs nobody anything.
            payloads = PenumbraPackage.ReadEntries(
                preview.SourcePath, importable.Select(t => t.Entry));

            options = new List<OverlayOption>();
            wearsBodyType = new List<string?>();   // parallel to options; see the default-selection below
        }

        private void WriteOptions()
        {
            foreach (var plan in importable) WriteMaterialOption(plan);
        }

        /// <summary>Writes one material's glowing art into the mod and adds its option.</summary>
        private void WriteMaterialOption(TexturePlan plan)
        {
            if (!payloads.TryGetValue(plan.Entry, out var bytes))
            {
                log?.Warning("[Proteus] emissive import: {0} was in the archive on preview and not on write "
                           + "— skipped", plan.Label);
                return;
            }

            if (decode(bytes, plan.Label) is not { } src)
            {
                log?.Warning("[Proteus] emissive import: {0} could not be decoded — skipped", plan.Label);
                return;
            }

            var (rgba, w, h) = Fit(src.Rgba, src.Width, src.Height);
            string bodyType = retargeted
                ? destinationType ?? plan.BodyType ?? ""
                : plan.BodyType ?? "";

            // ── the art ──
            // Coverage is the mask AS IT STANDS. Nothing is inverted and nothing is gained: an emissive
            // map's alpha already says "there is paint here", opaque across the artwork and ramping only at
            // its outline. That is the one place this format differs from Atramentum Luminis, whose alpha is
            // an inverted INTENSITY and needs both (see LuminisImportService.CoverageGain).
            //
            // The RGB rides along untouched — it is the shell's own art, and the colour the author chose.
            var art = new byte[rgba.Length];
            Buffer.BlockCopy(rgba, 0, art, 0, rgba.Length);

            // The scroll map carries the COLOUR and the INTENSITY: a coloured emissive glows in its own hue
            // per pixel, scaled here by how strongly the mask said that pixel should emit. Black where
            // nothing glows at all, so the shell's unlit surface shows through as the row's own black.
            var scroll = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int lit = rgba[i + 3];
                scroll[i] = (byte)((rgba[i] * lit + 127) / 255);
                scroll[i + 1] = (byte)((rgba[i + 1] * lit + 127) / 255);
                scroll[i + 2] = (byte)((rgba[i + 2] * lit + 127) / 255);
                scroll[i + 3] = 255;
            }

            var artFile = Materialize(art, w, h, overlaysDir, plan.Stem, encodeTo);
            var scrollFile = Materialize(scroll, w, h, effectsDir, plan.Stem, encodeTo);

            var descriptor = new OverlayDescriptor
            {
                // Layer AND Shader, stated outright — the pair ColorTableEditor.ApplyMode writes for
                // RenderMode.Glow, which is the mode this is. Leaving them to ShouldPromoteToGear moves the
                // LAYER only: the shader then falls through to plain character.shpk, which has no scroll map
                // at all, and the effect is silently dropped.
                Layer = OverlayLayer.Gear,
                Shader = RenderModeInference.GlowShader,
                SourceBodyType = string.IsNullOrEmpty(bodyType) ? null : bodyType,
                MaterialGamePaths = [.. materials],
                Diffuse = "overlays/" + artFile,
                Scroll = scrollFile,
                // Zero, explicitly. The material constants ship at zero and an unset speed would take
                // GearMaterialWriter's own default instead, sliding a tattoo across the skin it is drawn on.
                ScrollSpeedX = 0f,
                ScrollSpeedY = 0f,
                // One-to-one: the scroll map IS the body sheet, so tiling it would repeat the tattoo.
                ScrollTilingX = 1f,
                ScrollTilingY = 1f,
            };

            // One row per plateau, so each region of the tattoo can later be given its own colour, its own
            // brightness and its own light response. Every row is written IDENTICALLY here on purpose: the
            // regions differ in what they let the user do, not in how the import looks, and the per-pixel
            // intensity that actually separates them is already baked into the scroll map above.
            var intensity = Alpha(rgba);
            var bands = GlowShell.Bands(intensity);
            int rowCount = Math.Max(1, bands.Count);
            if (bands.Count > 1)
            {
                // PNG, not the .tex path Materialize would otherwise take: an index texture is a lookup, and
                // BC7 is lossy enough to move a texel's red across a row boundary — which is why
                // SecondSkinService refuses to compress the id slot either.
                descriptor.Index = "overlays/" + Materialize(
                    GlowShell.Index(intensity, bands), w, h, overlaysDir, plan.Stem + "_id", encodeTo: null);
                log?.Information("[Proteus] emissive import: {0} — {1} glow region(s), rows 1–{1}",
                    plan.Label, bands.Count);
            }

            var rows = new List<ColorTableRowPreset>();
            for (int r = 1; r <= rowCount; r++)
                rows.Add(new ColorTableRowPreset
                {
                    // With an index the rows start at 1 and count up with the plateaus; without one the
                    // shell samples the fabricated (255,255,0), which is row 16.
                    Row = bands.Count > 1 ? r : GlowShell.Row,
                    SubRowA = new ColorTableSubRowPreset
                    {
                        Emissive = GlowShell.Emissive,
                        // Neutral: the scroll map carries its own hue, and a tinted emissive would only push
                        // everything toward that tint.
                        EmissiveColor = RenderModeInference.GlowEmissiveColour,
                        Diffuse = GlowShell.SurfaceColour,
                        // No LightResponse and no HideInLight, which is where this parts company with the
                        // Atramentum Luminis import. That mod's tattoos were dark-only by design; an
                        // emissive sampler on skin.shpk simply adds light, at noon as much as at midnight,
                        // so an unconditional glow is what parity means here. The Colors tab turns it into a
                        // dark-only one in two clicks for anyone who wants that instead.
                    },
                });

            options.Add(new OverlayOption
            {
                Name = GlowOptionName(plan, qualified),
                Overlays = [descriptor],
                // On the OPTION, never at the top level. Top-level rows are inherited by every option that
                // declares none, so these would reach the pack's other body layouts as well.
                ColorTableRows = rows,
            });
            wearsBodyType.Add(plan.BodyType);
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

            PenumbraModMeta.AtomicWrite(
                Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json"),
                JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite));

            var imported = string.Format(Loc.Localize("Import.Emissive.Description.Fmt",
                "Imported from the emissive skin pack \"{0}\"."), Path.GetFileName(preview.SourcePath));
            var description = string.IsNullOrWhiteSpace(preview.Description)
                ? imported
                : preview.Description + "\n\n" + imported;

            PenumbraModMeta.AtomicWrite(
                Path.Combine(root, PenumbraModMeta.MetaFile),
                PenumbraModMeta.NewMetaJson(modName, author, description, preview.Version, preview.Website));

            // Proteus does the real texture redirection itself at composite time, so the default option would be
            // empty — which Penumbra flags as "changes nothing". Same harmless self-swap the Create tab uses.
            //
            // The pack's OWN redirects are deliberately not carried over: they are body materials rewired to
            // name an emissive sampler that only a replaced skin.shpk has, and republishing them would put this
            // mod into a fight with Proteus over the very material it is compositing into.
            PenumbraModMeta.WriteRedirects(
                root, modName,
                files: new Dictionary<string, string>(),
                swaps: new Dictionary<string, string>
                    { [ModCreationService.DummySwapPath] = ModCreationService.DummySwapPath });

            // Exactly ONE option on, where the Atramentum Luminis import turns on a pair. Several options here
            // are several UV layouts of the SAME tattoo, all aimed at the one body the user picked, so wearing
            // two would stack a shell on its own copy. The one already in the destination's space is the one
            // that needs no resampling; failing that, the first.
            var defaultOn = new List<string>();
            ulong defaults = 0;
            if (options.Count > 0)
            {
                int pick = wearsBodyType.FindIndex(
                    t => t != null && string.Equals(t, destinationType, StringComparison.OrdinalIgnoreCase));
                if (pick < 0) pick = 0;
                defaultOn.Add(options[pick].Name);
                defaults |= 1UL << pick;
            }

            PenumbraModMeta.WriteMultiSelectGroup(
                root, 0, GroupName, [.. options.Select(o => o.Name)], defaults);

            return new([.. options.Select(o => o.Name)], defaultOn);
        }
    }
}
