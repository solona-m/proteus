using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CheapLoc;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

public sealed partial class EyeImportService
{
    private sealed class EyeModWrite
    {
        private readonly string root;
        private readonly string modName;
        private readonly string author;
        private readonly ImportPreview preview;
        private readonly EyeCutout cutout;
        private readonly TextureLoader? encoder;
        private readonly Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode;
        private readonly IPluginLog? log;
        private Dictionary<string, string> redirects = null!;
        private (byte[] Rgba, int Width, int Height)? maskPixels;
        private (byte[] Rgba, int Width, int Height)? basePixels;
        private int written;
        private bool glow;

        public EyeModWrite(string root, string modName, string author, ImportPreview preview, EyeCutout cutout, TextureLoader? encoder, Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode, IPluginLog? log)
        {
            this.root = root;
            this.modName = modName;
            this.author = author;
            this.preview = preview;
            this.cutout = cutout;
            this.encoder = encoder;
            this.decode = decode;
            this.log = log;
        }

        public (int Written, bool Glow) Run()
        {
            WriteIrisTextures();
            WriteGlowLayer();
            return WriteRedirects();
        }

        private void WriteIrisTextures()
        {
            Directory.CreateDirectory(root);

            redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            maskPixels = null;
            basePixels = null;
            written = 0;

            foreach (var plan in preview.Importable)
            {
                if (plan.Slot is not { } slot || plan.GamePath is not { } gamePath) continue;

                var bytes = EyePackage.ReadEntry(preview.SourcePath, plan.File.Entry);
                if (decode(bytes, plan.Name) is not { } img)
                {
                    log?.Warning("[Proteus] eye import: {0} could not be decoded — skipped", plan.Name);
                    continue;
                }
                if (slot == EyeSlot.Mask) maskPixels = img;
                if (slot == EyeSlot.Base) basePixels = img;

                // Written as .tex rather than copied through. The redirect target has to be a texture the game
                // can read, and these packs ship PNGs; converting here is the difference between a mod that
                // works and one whose eyes go missing.
                var dest = Path.Combine(root, gamePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (encoder == null
                 || !(encoder.WriteTex(img.Rgba, img.Width, img.Height, dest, TexEncoding.Bc7)
                   || encoder.WriteTex(img.Rgba, img.Width, img.Height, dest, TexEncoding.Uncompressed)))
                {
                    log?.Warning("[Proteus] eye import: {0} could not be written as .tex — skipped", plan.Name);
                    continue;
                }

                redirects[gamePath] = gamePath.Replace('/', Path.DirectorySeparatorChar);
                written++;
            }
        }

        private void WriteGlowLayer()
        {
            // ── the glow layer ──
            glow = false;
            if (preview.CanGlowWith(cutout) && maskPixels is { } mask)
            {
                var overlaysDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "overlays");
                var effectsDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir,
                                              SidecarDiscoveryService.EffectsSubdir);
                Directory.CreateDirectory(overlaysDir);
                Directory.CreateDirectory(effectsDir);

                // Coverage is the CUTOUT of the mask's glow channel — see Cutout for why it is not the channel
                // itself. The shell is trimmed to exactly that shape, so the animation plays inside the artwork
                // and the rest of the eye is left alone. RGB carries the same value so the file reads as the
                // picture it is; characterscroll has no base texture, so nothing samples it.
                var (cut, _) = Cutout(mask.Rgba, cutout);
                var art = new byte[mask.Rgba.Length];
                for (int p = 0; p < cut.Length; p++)
                {
                    byte v = cut[p];
                    art[p * 4] = art[p * 4 + 1] = art[p * 4 + 2] = v;
                    art[p * 4 + 3] = v;
                }

                const string stem = "eye_glow";
                var artFile = Materialize(art, mask.Width, mask.Height, overlaysDir, stem, encoder);

                // THE SCROLL MAP, written into the mod's own Effects folder.
                //
                // Without one, characterscroll samples a fabricated black `catc` and the emissive scales
                // nothing: the cutout renders as an opaque black patch over the iris and never moves. Declaring
                // the shader and the speeds is not enough — the map is what is being scrolled.
                //
                // Shipped with the mod rather than named out of the shared effects library, because that
                // library is downloaded once per machine and a mod that depends on a file the user may not
                // have is a mod that silently doesn't glow. The pack's own base texture is the natural
                // choice: it is the artwork's own palette, so the colours moving inside the shape belong to
                // it. Any library effect can be swapped in from the Colors tab afterwards.
                var scrollPixels = basePixels ?? mask;
                var scrollFile = Materialize(
                    Opaque(scrollPixels.Rgba), scrollPixels.Width, scrollPixels.Height,
                    effectsDir, stem, encoder);

                var descriptor = new OverlayDescriptor
                {
                    // Layer AND Shader, both stated. Promotion alone moves the layer and leaves the shader at
                    // plain character.shpk, which has no scroll map at all — the effect is then silently
                    // dropped and no amount of tuning the emissive brings it back.
                    Layer = OverlayLayer.Gear,
                    Shader = RenderModeInference.GlowShader,
                    MaterialGamePaths = [.. preview.IrisMaterials],
                    Diffuse = "overlays/" + artFile,
                    // A bare file name, which SidecarDiscoveryService.ResolveEffectPath looks up in the mod's
                    // own Effects folder before the shared library — so this always resolves.
                    Scroll = scrollFile,
                    // No SourceBodyType: a human part is painted in its own layout and the shell builder forces
                    // the UV conversion to native at both ends. A stray value here would be ignored, but it
                    // would still be a lie about the art.
                    ScrollSpeedX = ScrollSpeed,
                    ScrollSpeedY = ScrollSpeed,
                    ScrollTilingX = ScrollTiling,
                    ScrollTilingY = ScrollTiling,
                };

                var metadata = new ProteusMetadata
                {
                    FormatVersion = 1,
                    Name = modName,
                    Author = author,
                    OptionGroups =
                    [
                        new OverlayOptionGroup
                        {
                            PenumbraGroupName = GroupName,
                            Options =
                            [
                                new OverlayOption
                                {
                                    Name = Loc.Localize("Import.Eye.Option.Glow", "Animated glow"),
                                    Overlays = [descriptor],
                                    // On the option, never at the top level: top-level rows are inherited by
                                    // every option that declares none, and any emissive makes HasCloth true.
                                    ColorTableRows =
                                    [
                                        new ColorTableRowPreset
                                        {
                                            Row = GlowRow,
                                            SubRowA = new ColorTableSubRowPreset
                                            {
                                                Emissive = GlowEmissive,
                                                EmissiveColor = RenderModeInference.GlowEmissiveColour,
                                                Diffuse = GlowSurfaceColour,
                                            },
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                };

                PenumbraModMeta.AtomicWrite(
                    Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json"),
                    JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite));
                glow = true;
            }

            var description = string.Format(Loc.Localize("Import.Eye.Description.Fmt",
                "Imported from the eye texture pack \"{0}\"."), Path.GetFileName(preview.SourcePath));
            PenumbraModMeta.AtomicWrite(
                Path.Combine(root, PenumbraModMeta.MetaFile),
                PenumbraModMeta.NewMetaJson(modName, author, description));
        }

        private (int Written, bool Glow) WriteRedirects()
        {
            // Unlike the overlay importers this mod DOES redirect real game files, so there is nothing to fake:
            // the textures are its default data. The dummy self-swap those need exists only for a mod that
            // redirects nothing.
            PenumbraModMeta.WriteRedirects(root, modName, redirects);

            if (glow)
                PenumbraModMeta.WriteMultiSelectGroup(
                    root, 0, GroupName,
                    [Loc.Localize("Import.Eye.Option.Glow", "Animated glow")],
                    defaultSettings: 1);

            return (written, glow);
        }
    }
}
