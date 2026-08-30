using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The eye-pack importer: what a bare <c>.zip</c> is taken for, and the mod it writes. Everything runs
/// offline — <c>BuildPreview</c> and <c>WriteMod</c> both take the decoder as a delegate, which is the
/// only part that would need a live game.
/// </summary>
public class EyeImportTests
{
    private static readonly string[] Irises =
    [
        "chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_iri_a.mtrl",
        "chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_iri_a.mtrl",
    ];

    /// <summary>Decodes the synthetic PNGs the way the service does, so the preview measures real pixels.</summary>
    private static (byte[] Rgba, int Width, int Height)? Decode(byte[] bytes, string name)
    {
        var img = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return img?.Data == null ? null : (img.Data, img.Width, img.Height);
    }

    private static (float Falloff, float Artwork)? Measure(EyePackage.PackFile f, string zipPath)
        => Decode(EyePackage.ReadEntry(zipPath, f.Entry), f.Name) is { } d
            ? (EyeImportService.Cutout(d.Rgba, EyeImportService.EyeCutout.Falloff).Fraction,
               EyeImportService.Cutout(d.Rgba, EyeImportService.EyeCutout.Artwork).Fraction)
            : null;

    private static EyeImportService.ImportPreview Preview(
        SyntheticEyeZip zip, IReadOnlyList<string>? irises = null, bool faceFromWearer = true)
        => EyeImportService.BuildPreview(
            EyePackage.Read(zip.Path), irises ?? Irises, "f0001", faceFromWearer, Measure);

    // ── the reader ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Butterfly_eye_base.png", EyeSlot.Base)]
    [InlineData("Butterfly_eye_mask.png", EyeSlot.Mask)]
    [InlineData("Butterfly_eye_norm.png", EyeSlot.Norm)]
    [InlineData("eye01_base.tex", EyeSlot.Base)]
    [InlineData("whatever_d.png", EyeSlot.Base)]
    [InlineData("whatever_s.png", EyeSlot.Mask)]
    [InlineData("whatever_multi.png", EyeSlot.Mask)]
    [InlineData("whatever_n.png", EyeSlot.Norm)]
    [InlineData("readme.png", null)]
    [InlineData("noUnderscore.png", null)]
    [InlineData("trailing_.png", null)]
    public void SlotOf_reads_the_token_after_the_last_underscore(string name, EyeSlot? expected)
        => Assert.Equal(expected, EyePackage.SlotOf(name));

    [Fact]
    public void Reads_a_three_file_pack_and_names_it_after_its_folder()
    {
        using var zip = SyntheticEyeZip.Standard();
        var pack = EyePackage.Read(zip.Path);

        Assert.Equal("DT ButterflyEffect", pack.Name);
        Assert.Equal(3, pack.Files.Count);
        Assert.Equal(3, pack.BySlot.Count);
        Assert.True(EyePackage.LooksLikeEyes(pack));
    }

    /// <summary>Readmes and preview images ride along in these packs; they are not a fault.</summary>
    [Fact]
    public void Non_images_are_ignored_rather_than_reported()
    {
        using var zip = SyntheticEyeZip.Of(
            new SyntheticEyeZip.Entry("pack/readme.txt", [1, 2, 3]),
            new SyntheticEyeZip.Entry("pack/pack_eye_mask.png", SyntheticEyeZip.Png(8, SyntheticEyeZip.Ring(8))));

        var pack = EyePackage.Read(zip.Path);
        Assert.Single(pack.Files);
    }

    [Fact]
    public void An_archive_with_no_images_is_refused()
    {
        using var zip = SyntheticEyeZip.Of(new SyntheticEyeZip.Entry("readme.txt", [1]));
        Assert.Throws<InvalidDataException>(() => EyePackage.Read(zip.Path));
    }

    [Fact]
    public void A_traversal_entry_is_refused_outright()
    {
        using var zip = SyntheticEyeZip.Of(
            new SyntheticEyeZip.Entry("../escape_eye_base.png", SyntheticEyeZip.Png(4, (x, y) => 0xFFFFFFFF)));
        Assert.Throws<InvalidDataException>(() => EyePackage.Read(zip.Path));
    }

    // ── classification ───────────────────────────────────────────────────────

    [Fact]
    public void A_standard_pack_imports_all_three_and_can_glow()
    {
        using var zip = SyntheticEyeZip.Standard();
        var preview = Preview(zip);

        Assert.Equal(3, preview.Importable.Count);
        Assert.All(preview.Files, f => Assert.True(f.Import, f.SkipReason));
        Assert.True(preview.CanGlow);
        Assert.Empty(preview.Warnings);

        // Each lands on the game path its token names.
        Assert.Equal("chara/common/texture/eye/eye01_base.tex",
            preview.Files.Single(f => f.Slot == EyeSlot.Base).GamePath);
        Assert.Equal("chara/common/texture/eye/eye01_mask.tex",
            preview.Files.Single(f => f.Slot == EyeSlot.Mask).GamePath);
        Assert.Equal("chara/common/texture/eye/eye01_norm.tex",
            preview.Files.Single(f => f.Slot == EyeSlot.Norm).GamePath);
    }

    /// <summary>
    /// A zip of body textures satisfies the token rule and nothing else. Pointing it at the wearer's irises
    /// on that basis would rewrite the eyes of every character the collection covers, so the names have to
    /// actually say "eye".
    /// </summary>
    [Fact]
    public void Loose_textures_that_dont_say_eye_are_refused()
    {
        using var zip = SyntheticEyeZip.Of(
            new SyntheticEyeZip.Entry("pack/body_base.png", SyntheticEyeZip.Png(8, (x, y) => 0xFFFFFFFF)),
            new SyntheticEyeZip.Entry("pack/body_mask.png", SyntheticEyeZip.Png(8, SyntheticEyeZip.Ring(8))));

        var preview = Preview(zip);

        Assert.False(preview.AnyImportable);
        Assert.All(preview.Files, f => Assert.Contains("eye pack", f.SkipReason));
    }

    [Fact]
    public void An_unrecognised_name_is_skipped_with_a_reason_and_the_rest_still_import()
    {
        using var zip = SyntheticEyeZip.Of(
            new SyntheticEyeZip.Entry("p/p_eye_mask.png", SyntheticEyeZip.Png(8, SyntheticEyeZip.Ring(8))),
            new SyntheticEyeZip.Entry("p/p_eye_catchlight.png", SyntheticEyeZip.Png(8, (x, y) => 0xFFFFFFFF)));

        var preview = Preview(zip);

        Assert.Single(preview.Importable);
        Assert.Contains("base, mask or norm",
            preview.Files.Single(f => !f.Import).SkipReason);
    }

    [Fact]
    public void A_second_file_claiming_a_taken_slot_is_skipped()
    {
        using var zip = SyntheticEyeZip.Of(
            new SyntheticEyeZip.Entry("p/a_eye_mask.png", SyntheticEyeZip.Png(8, SyntheticEyeZip.Ring(8))),
            new SyntheticEyeZip.Entry("p/b_eye_mask.png", SyntheticEyeZip.Png(8, SyntheticEyeZip.Ring(8))));

        var preview = Preview(zip);

        Assert.Single(preview.Importable);
        Assert.Contains("already", preview.Files.Single(f => !f.Import).SkipReason);
    }

    /// <summary>No mask, no shape for the animation to fill — the textures still import.</summary>
    [Fact]
    public void A_pack_with_no_mask_imports_without_a_glow()
    {
        using var zip = SyntheticEyeZip.Of(
            new SyntheticEyeZip.Entry("p/p_eye_base.png", SyntheticEyeZip.Png(8, (x, y) => 0xFF808080)));

        var preview = Preview(zip);

        Assert.True(preview.AnyImportable);
        Assert.False(preview.CanGlow);
        Assert.Null(preview.GlowFraction);
        Assert.NotEmpty(preview.Warnings);
    }

    /// <summary>The red channel is where the game reads the limbal ring from; empty means nothing glows.</summary>
    [Fact]
    public void A_mask_with_an_empty_red_channel_imports_without_a_glow()
    {
        using var zip = SyntheticEyeZip.Standard(mask: SyntheticEyeZip.Unlit);
        var preview = Preview(zip);

        Assert.True(preview.AnyImportable);
        Assert.Equal(0f, preview.GlowFraction);
        Assert.False(preview.CanGlow);
        Assert.NotEmpty(preview.Warnings);
    }

    [Fact]
    public void With_no_iris_material_the_textures_import_but_nothing_glows()
    {
        using var zip = SyntheticEyeZip.Standard();
        var preview = Preview(zip, irises: []);

        Assert.True(preview.AnyImportable);
        Assert.False(preview.CanGlow);
        Assert.NotEmpty(preview.Warnings);
    }

    // ── the written mod ──────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "proteus-eye-out-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* a temp dir that outlives the test is harmless */ }
        }
    }

    /// <summary>Writes with no encoder, which is the PNG path — the .tex encoder needs a live loader.</summary>
    private static (int Written, bool Glow) Write(
        EyeImportService.ImportPreview preview, TempDir dir,
        EyeImportService.EyeCutout? cutout = null)
        => EyeImportService.WriteMod(dir.Path, "Butterfly Eyes", "Tests", preview,
                                     cutout ?? preview.Cutout, encoder: null, decode: Decode);

    /// <summary>
    /// With no encoder the textures cannot be written as .tex, and the import reports zero rather than
    /// producing a mod whose redirects point at nothing. That is the guard, asserted deliberately.
    /// </summary>
    [Fact]
    public void Without_an_encoder_no_texture_is_written()
    {
        using var zip = SyntheticEyeZip.Standard();
        using var dir = new TempDir();

        var (written, _) = Write(Preview(zip), dir);

        Assert.Equal(0, written);
        Assert.False(File.Exists(Path.Combine(dir.Path, "chara", "common", "texture", "eye", "eye01_base.tex")));
    }

    /// <summary>
    /// The glow layer does not depend on the texture encoder — its art falls back to PNG, which the
    /// compositor reads perfectly well. So the sidecar is written even on the path above.
    /// </summary>
    [Fact]
    public void The_glow_overlay_is_gear_on_characterscroll_with_a_moving_scroll()
    {
        using var zip = SyntheticEyeZip.Standard();
        using var dir = new TempDir();

        var (_, glow) = Write(Preview(zip), dir);
        Assert.True(glow);

        var metadata = JsonSerializer.Deserialize<ProteusMetadata>(
            File.ReadAllText(Path.Combine(dir.Path, "Proteus", "metadata.json")), ProteusJson.MetadataRead)!;

        // Everything in OptionGroups: ResolveActiveOverlays returns early on a non-empty Overlays list and
        // never reads the groups.
        Assert.Null(metadata.Overlays);
        var group = Assert.Single(metadata.OptionGroups!);
        Assert.Equal(EyeImportService.GroupName, group.PenumbraGroupName);
        var option = Assert.Single(group.Options);
        var d = Assert.Single(option.Overlays);

        // Layer AND Shader. Promotion alone moves the layer and leaves the shader at plain character.shpk,
        // which has no scroll map — the effect is then silently dropped.
        Assert.Equal(OverlayLayer.Gear, d.Layer);
        Assert.Equal(RenderModeInference.GlowShader, d.Shader);
        Assert.Equal(RenderModeInference.GlowShader, d.ShaderPackage);

        // A scroll map, shipped with the mod. Declaring the shader and the speeds is not enough: with no
        // map characterscroll samples a fabricated BLACK catc, and the cutout renders as an opaque black
        // patch that never moves — the exact opposite of the feature.
        Assert.False(string.IsNullOrEmpty(d.Scroll), "no scroll map was set");
        Assert.DoesNotContain('/', d.Scroll!);   // a bare name, resolved in the mod's own Effects folder
        Assert.True(File.Exists(Path.Combine(dir.Path, "Proteus", "Effects", d.Scroll!)),
            $"the scroll map {d.Scroll} was not written into the mod");

        // It has to actually move, and at a rate that reads on something a few millimetres across.
        Assert.True(d.ScrollSpeedX is > 0f and < 0.15f, $"speed was {d.ScrollSpeedX}");
        Assert.Equal(d.ScrollSpeedX, d.ScrollSpeedY);
        Assert.Equal(1f, d.ScrollTilingX);
        Assert.Equal(1f, d.ScrollTilingY);

        // A human part is painted in its own layout and the shell builder forces the UV conversion to
        // native at both ends; a value here would be a lie about the art.
        Assert.Null(d.SourceBodyType);
        Assert.Equal(Irises, d.MaterialGamePaths);
    }

    /// <summary>
    /// Every material the overlay names must resolve to ONE shell surface.
    /// <para/>
    /// An iris surface is keyed by face (<c>Iris:f0001</c>), and <c>SecondSkinService.SurfaceKeyOf</c>
    /// takes <c>keys[0]</c> and warns when an overlay spans more, because the split is not built. Listing
    /// every face the way a body overlay lists every race would leave anyone not on the first face with
    /// nothing, and log about it on every composite. Races collapse into one key; faces do not.
    /// </summary>
    [Fact]
    public void The_overlay_names_exactly_one_shell_surface()
    {
        using var zip = SyntheticEyeZip.Standard();
        using var dir = new TempDir();
        Write(Preview(zip), dir);

        var metadata = JsonSerializer.Deserialize<ProteusMetadata>(
            File.ReadAllText(Path.Combine(dir.Path, "Proteus", "metadata.json")), ProteusJson.MetadataRead)!;
        var paths = metadata.OptionGroups![0].Options[0].Overlays[0].MaterialGamePaths;

        // Two races, same face — and therefore one surface.
        Assert.Equal(2, paths.Count);
        var key = Assert.Single(ShellSurface.KeysFor(paths));
        Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Iris, "f0001"), key);
    }

    [Theory]
    [InlineData("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_iri_a.mtrl", "f0001")]
    [InlineData("chara/human/c1401/obj/face/f0104/material/mt_c1401f0104_iri_a.mtrl", "f0104")]
    [InlineData("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl", null)]
    [InlineData(null, null)]
    public void FaceIdOf_reads_the_face_folder(string? path, string? expected)
        => Assert.Equal(expected, EyeImportService.FaceIdOf(path));

    [Fact]
    public void A_guessed_face_is_warned_about()
    {
        using var zip = SyntheticEyeZip.Standard();

        Assert.Empty(Preview(zip).Warnings);
        Assert.Contains(Preview(zip, faceFromWearer: false).Warnings, w => w.Contains("f0001"));
    }

    [Fact]
    public void Only_the_glow_option_carries_an_emissive_row()
    {
        using var zip = SyntheticEyeZip.Standard();
        using var dir = new TempDir();
        Write(Preview(zip), dir);

        var metadata = JsonSerializer.Deserialize<ProteusMetadata>(
            File.ReadAllText(Path.Combine(dir.Path, "Proteus", "metadata.json")), ProteusJson.MetadataRead)!;

        Assert.Null(metadata.ColorTableRows);
        var rows = metadata.OptionGroups![0].Options[0].ColorTableRows!;
        var row = Assert.Single(rows);
        Assert.Equal(16, row.Row);   // the row a shell with no _id samples
        Assert.Null(row.SubRowB);
        // 75%, measured in game — half the editor's default. Higher clips the scroll map to flat white and
        // the mask's falloff, which the default cutout goes out of its way to keep, stops being visible.
        Assert.Equal(0.75f, row.SubRowA!.Emissive);
        Assert.True(row.SubRowA.Emissive < RenderModeInference.GlowEmissive);
        Assert.Equal("#000000", row.SubRowA.Diffuse);   // characterscroll has no base texture
        Assert.True(RenderModeInference.HasCloth(rows));
    }

    /// <summary>
    /// The cutout must be the SHAPE the artist drew, not their whole glow gradient.
    /// <para/>
    /// Regression for the first render: a real mask's red channel is 92% near-zero with a smooth tail
    /// (a radial fan filling the entire iris) and a separate spike for the artwork. Taking any lit pixel
    /// as coverage cut a shell over the whole iris disc and the animation escaped the butterfly.
    /// </summary>
    [Fact]
    public void A_dim_gradient_around_the_artwork_is_cut_away()
    {
        const int Size = 16;
        // A bright shape in the middle (255) inside a dim fan (60) — the real mask's shape, in miniature.
        static uint Pixel(int x, int y)
        {
            bool shape = x >= 7 && x <= 8 && y >= 7 && y <= 8;
            bool fan = x >= 3 && x < 13 && y >= 3 && y < 13;
            byte r = shape ? (byte)255 : fan ? (byte)60 : (byte)0;
            return 0xFF000000u | r;
        }

        var rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                uint p = Pixel(x, y);
                int i = (y * Size + x) * 4;
                rgba[i] = (byte)p; rgba[i + 1] = (byte)(p >> 8);
                rgba[i + 2] = (byte)(p >> 16); rgba[i + 3] = (byte)(p >> 24);
            }

        var (coverage, fraction) = EyeImportService.Cutout(rgba, EyeImportService.EyeCutout.Artwork);

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                byte c = coverage[y * Size + x];
                bool shape = x >= 7 && x <= 8 && y >= 7 && y <= 8;
                if (shape) Assert.Equal(255, c);
                else Assert.Equal(0, c);   // the fan at 60 is well under 70% of the 255 peak
            }

        // Four pixels of 256 — the shape, not the fan's hundred.
        Assert.Equal(4 / 256f, fraction);
    }

    /// <summary>
    /// The other cutout keeps the fan, at an opacity proportional to the channel — which is what makes the
    /// glow carry the artist's own falloff rather than stopping at the shape's edge.
    /// </summary>
    [Fact]
    public void The_falloff_cutout_keeps_the_gradient_in_proportion()
    {
        const int Size = 16;
        var rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool shape = x >= 7 && x <= 8 && y >= 7 && y <= 8;
                bool fan = x >= 3 && x < 13 && y >= 3 && y < 13;
                int i = (y * Size + x) * 4;
                rgba[i] = shape ? (byte)255 : fan ? (byte)60 : (byte)0;
                rgba[i + 3] = 255;
            }

        var (coverage, fraction) = EyeImportService.Cutout(rgba, EyeImportService.EyeCutout.Falloff);

        Assert.Equal(255, coverage[7 * Size + 7]);   // the shape stays full
        Assert.Equal(60, coverage[3 * Size + 3]);    // the fan survives at its own level, unscaled
        Assert.Equal(0, coverage[0]);                // and nothing outside it appears

        // The whole fan, not just the shape — 100 pixels of 256.
        Assert.Equal(100 / 256f, fraction);
    }

    /// <summary>A clean binary silhouette has nothing between zero and its peak, so the floor changes
    /// nothing and the shape survives intact.</summary>
    [Fact]
    public void A_binary_mask_passes_through_the_cutout_unchanged()
    {
        const int Size = 8;
        var rgba = new byte[Size * Size * 4];
        var expected = SyntheticEyeZip.Ring(Size);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                uint p = expected(x, y);
                int i = (y * Size + x) * 4;
                rgba[i] = (byte)p; rgba[i + 1] = (byte)(p >> 8);
                rgba[i + 2] = (byte)(p >> 16); rgba[i + 3] = (byte)(p >> 24);
            }

        foreach (var mode in new[] { EyeImportService.EyeCutout.Falloff, EyeImportService.EyeCutout.Artwork })
        {
            var (coverage, _) = EyeImportService.Cutout(rgba, mode);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    Assert.Equal((byte)expected(x, y), coverage[y * Size + x]);
        }
    }

    /// <summary>
    /// The written art's alpha is the cutout, which is what the shell builder reads as coverage. Asserted
    /// on pixels, because the confinement is the whole point of this importer.
    /// </summary>
    [Fact]
    public void Coverage_is_the_cutout_of_the_masks_red_channel()
    {
        const int Size = 8;
        using var zip = SyntheticEyeZip.Standard(size: Size);
        using var dir = new TempDir();
        Write(Preview(zip), dir);

        var art = Path.Combine(dir.Path, "Proteus", "overlays", "eye_glow.png");
        Assert.True(File.Exists(art));

        using var stream = File.OpenRead(art);
        var img = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        var expected = SyntheticEyeZip.Ring(Size);

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                byte red = (byte)expected(x, y);          // the mask's red channel
                byte alpha = img.Data[(y * Size + x) * 4 + 3];
                Assert.Equal(red, alpha);
            }
    }

    [Fact]
    public void Writes_a_penumbra_manifest_and_redirects_for_every_texture()
    {
        using var zip = SyntheticEyeZip.Standard();
        using var dir = new TempDir();
        Write(Preview(zip), dir);

        var meta = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir.Path, PenumbraModMeta.MetaFile))).RootElement;
        Assert.Equal("Butterfly Eyes", meta.GetProperty("Name").GetString());
        Assert.Contains("eye texture pack", meta.GetProperty("Description").GetString());

        // The group arrives with the glow ticked — unlike the content importer's pieces, the animation is
        // the reason someone imported this.
        var group = JsonDocument.Parse(File.ReadAllText(
            Directory.EnumerateFiles(dir.Path, "group_*.json").Single())).RootElement;
        Assert.Equal("Multi", group.GetProperty("Type").GetString());
        Assert.Equal(EyeImportService.GroupName, group.GetProperty("Name").GetString());
        Assert.Equal(1, group.GetProperty("DefaultSettings").GetInt64());
    }

    /// <summary>
    /// The write takes the cutout as an argument rather than reading the preview's mutable field, so a
    /// click on the combo while the pool thread is writing cannot change what gets baked.
    /// </summary>
    [Fact]
    public void The_write_uses_the_cutout_it_was_given_not_the_previews()
    {
        const int Size = 16;
        // A bright shape inside a dim fan, so the two cutouts genuinely differ.
        static uint Pixel(int x, int y)
        {
            bool shape = x >= 7 && x <= 8 && y >= 7 && y <= 8;
            bool fan = x >= 3 && x < 13 && y >= 3 && y < 13;
            return 0xFF000000u | (shape ? 255u : fan ? 60u : 0u);
        }

        using var zip = SyntheticEyeZip.Standard(size: Size, mask: Pixel);
        var preview = Preview(zip);
        preview.Cutout = EyeImportService.EyeCutout.Falloff;   // what the panel is showing

        using var dir = new TempDir();
        Write(preview, dir, EyeImportService.EyeCutout.Artwork);   // what was snapshotted at click time

        using var stream = File.OpenRead(Path.Combine(dir.Path, "Proteus", "overlays", "eye_glow.png"));
        var img = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        // Artwork: the fan is gone. Had the write read preview.Cutout it would be there at 60.
        Assert.Equal(255, img.Data[(7 * Size + 7) * 4 + 3]);
        Assert.Equal(0, img.Data[(3 * Size + 3) * 4 + 3]);
    }

    [Fact]
    public void A_pack_that_cannot_glow_writes_no_sidecar()
    {
        using var zip = SyntheticEyeZip.Standard(mask: SyntheticEyeZip.Unlit);
        using var dir = new TempDir();

        var (_, glow) = Write(Preview(zip), dir);

        Assert.False(glow);
        Assert.False(File.Exists(Path.Combine(dir.Path, "Proteus", "metadata.json")));
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "group_*.json"));
    }
}
