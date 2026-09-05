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
/// The Atramentum Luminis import: which textures it recognises, which body it aims them at, and the mod it
/// writes. Everything runs offline — <c>BuildPreview</c> and <c>WriteMod</c> both take the texture decoder
/// as a delegate, which is the only part that would need a live game.
/// </summary>
public class LuminisImportTests
{
    /// <summary>A wearer on Bibo+; the material path Penumbra would report for one.</summary>
    private const string BiboBody = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";

    /// <summary>A wearer on a male body — TBSE and HRBody both sit at the <c>_b</c> suffix.</summary>
    private const string MaleBody = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl";

    /// <summary>Every texture measures as a real glow mask at the given size.</summary>
    private static Func<byte[], string, (int Width, int Height, float Glow)?> Glowing(float glow = 0.37f)
        => (_, _) => (2048, 2048, glow);

    private static LuminisImportService.ImportPreview Preview(
        SyntheticTtmp pack,
        string? wearer = BiboBody,
        Func<byte[], string, (int Width, int Height, float Glow)?>? measure = null)
        => LuminisImportService.BuildPreview(
            pack.Path, TexToolsPackage.Read(pack.Path), wearer, measure ?? Glowing());

    private static SyntheticTtmp.Entry Tex(string path, byte[]? slice = null)
        => new(path, slice ?? SyntheticTtmp.TextureSlice(16, 16, 2));

    // ── path recognition ─────────────────────────────────────────────────────

    [Fact]
    public void RecognisesABiboPackAndCollapsesItsAliases()
    {
        var slice = SyntheticTtmp.TextureSlice(16, 16, 2);
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Dame Douleur",
            Tex("chara/bibo/highlander_d.tex", slice),
            Tex("chara/bibo/viera_d.tex", slice),
            Tex("chara/bibo/midlander_d.tex", slice),
            Tex("chara/bibo_high_base.tex", slice),
            Tex("chara/bibo_viera_base.tex", slice),
            Tex("chara/bibo_mid_base.tex", slice));

        var preview = Preview(pack);

        // Six paths, one picture — the shape every real pack of these has.
        var plan = Assert.Single(preview.Textures);
        Assert.True(plan.Import);
        Assert.Equal(6, plan.Paths.Count);
        Assert.Equal("bibo", plan.Token);
        Assert.Equal("bibo", plan.BodyType);
        Assert.Equal("_bibo.mtrl", plan.Suffix);
        Assert.False(plan.FromWearer);
        Assert.Equal("highlander", plan.Stem);
        Assert.Equal("Dame Douleur", preview.Author);
    }

    [Fact]
    public void RefusesAnOrdinaryTexToolsModByName()
    {
        using var pack = SyntheticTtmp.Wizard("Real paths", "Tests",
            Tex("chara/human/c0201/obj/body/b0001/texture/--c0201b0001_b_d.tex"));

        var preview = Preview(pack);

        var plan = Assert.Single(preview.Textures);
        Assert.False(plan.Import);
        Assert.Contains("TexTools", plan.SkipReason);
        Assert.False(preview.AnyImportable);
    }

    [Fact]
    public void SkipsATextureWhoseAlphaCarriesNoGlow()
    {
        using var pack = SyntheticTtmp.Wizard("Flat", "Tests", Tex("chara/bibo/midlander_d.tex"));

        var preview = Preview(pack, measure: (_, _) => (2048, 2048, 0f));

        var plan = Assert.Single(preview.Textures);
        Assert.False(plan.Import);
        Assert.Contains("alpha", plan.SkipReason);
    }

    /// <summary>Legal, but usually a texture with no real alpha channel — warned about, not refused.</summary>
    [Fact]
    public void WarnsWhenAlmostEverythingGlowsButStillImports()
    {
        using var pack = SyntheticTtmp.Wizard("All glow", "Tests", Tex("chara/bibo/midlander_d.tex"));

        var preview = Preview(pack, measure: (_, _) => (2048, 2048, 0.99f));

        Assert.True(Assert.Single(preview.Textures).Import);
        Assert.NotEmpty(preview.Warnings);
    }

    [Fact]
    public void ReportsAModelOrMaterialRatherThanFailingThePack()
    {
        using var pack = SyntheticTtmp.Wizard("Mixed", "Tests",
            Tex("chara/bibo/midlander_d.tex"),
            new SyntheticTtmp.Entry("chara/bibo/thing.tex", SyntheticTtmp.BinarySlice()));

        var preview = Preview(pack);

        Assert.Equal(2, preview.Textures.Count);
        Assert.Single(preview.Textures, t => t.Import);
        var skipped = Assert.Single(preview.Textures, t => !t.Import);
        Assert.Contains("SqPack", skipped.SkipReason);
    }

    // ── body resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// The male path. TBSE is not in the token table and never needs to be: its material is <c>_b.mtrl</c>
    /// under <c>/obj/body/</c>, which <c>UVRemapService.InferBodyType</c> already calls "gen3", so
    /// declaring the art to be in that space makes the remap a no-op and it paints one-to-one.
    /// </summary>
    [Fact]
    public void AnUnknownBodyFallsBackToTheOneTheCharacterIsWearing()
    {
        using var pack = SyntheticTtmp.Wizard("TBSE tattoo", "Tests", Tex("chara/tbse/highlander_d.tex"));

        var preview = Preview(pack, wearer: MaleBody);

        var plan = Assert.Single(preview.Textures);
        Assert.True(plan.Import);
        Assert.Equal("tbse", plan.Token);
        Assert.Equal("_b.mtrl", plan.Suffix);
        Assert.True(plan.FromWearer);

        // Source equals destination, so UvConverter short-circuits and nothing is resized.
        Assert.Equal(UVRemapService.InferBodyType(MaleBody), plan.BodyType);
        Assert.Equal("_b.mtrl", preview.DefaultSuffix);
        Assert.NotEmpty(preview.Warnings);   // the guess is stated, not made silently
    }

    [Fact]
    public void AnUnknownBodyIsSkippedOnlyWhenTheCharacterIsntDrawn()
    {
        using var pack = SyntheticTtmp.Wizard("TBSE tattoo", "Tests", Tex("chara/tbse/highlander_d.tex"));

        var preview = Preview(pack, wearer: null);

        var plan = Assert.Single(preview.Textures);
        Assert.False(plan.Import);
        Assert.Contains("tbse", plan.SkipReason);
    }

    /// <summary>A KNOWN token answers from the table even when the wearer is on something else — that is
    /// what the art was painted for, and the remap exists to carry it across.</summary>
    [Fact]
    public void AKnownBodyIgnoresWhatTheCharacterIsWearing()
    {
        using var pack = SyntheticTtmp.Wizard("Bibo tattoo", "Tests", Tex("chara/bibo/midlander_d.tex"));

        var plan = Assert.Single(Preview(pack, wearer: MaleBody).Textures);

        Assert.Equal("_bibo.mtrl", plan.Suffix);
        Assert.Equal("bibo", plan.BodyType);
        Assert.False(plan.FromWearer);
    }

    [Theory]
    [InlineData("chara/bibo/highlander_d.tex", "bibo")]
    [InlineData("chara/gen3/midlander_d.tex", "gen3")]
    // The two-segment form: the token is the longest KNOWN prefix, so this is bibo and not "bibo_high".
    [InlineData("chara/bibo_high_base.tex", "bibo")]
    [InlineData("chara/tbse/hrothgar_d.tex", "tbse")]
    [InlineData("chara/human/c0201/obj/body/b0001/texture/x.tex", null)]
    [InlineData("chara/equipment/e0001/texture/x.tex", null)]
    [InlineData("chara/weapon/w0001/obj/body/b0001/texture/x.tex", null)]
    public void TokenOfReadsTheBodyOutOfAVirtualPath(string path, string? expected)
        => Assert.Equal(expected, LuminisImportService.TokenOf(path));

    [Theory]
    [InlineData("chara/bibo/highlander_d.tex", "highlander")]
    [InlineData("chara/bibo_high_base.tex", "bibo_high_base")]
    public void StemOfDropsTheDiffuseSuffix(string path, string expected)
        => Assert.Equal(expected, LuminisImportService.StemOf(path));

    [Theory]
    [InlineData(BiboBody, "_bibo.mtrl")]
    [InlineData(MaleBody, "_b.mtrl")]
    [InlineData(null, null)]
    public void SuffixOfReadsTheMaterialSuffix(string? path, string? expected)
        => Assert.Equal(expected, LuminisImportService.SuffixOf(path));

    // ── the written mod ──────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "proteus-luminis-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* a temp dir that outlives the test is harmless */ }
        }
    }

    /// <summary>
    /// A 2×2 sheet whose alpha runs 255, 128, 0, 255 — one pixel that does not glow, one that glows at
    /// half, one at full, and one more that does not. Enough to assert every channel transform.
    /// </summary>
    private static readonly byte[] SourceRgba =
    [
        10, 20, 30, 255,
        40, 50, 60, 128,
        70, 80, 90, 0,
        100, 110, 120, 255,
    ];

    private static (byte[] Rgba, int Width, int Height)? Decode(byte[] tex, string what)
        => (SourceRgba, 2, 2);

    private static ProteusMetadata WriteAndRead(
        SyntheticTtmp pack, TempDir dir, out List<string> glowOptions, string? suffixOverride = null,
        string? wearer = BiboBody)
        => WriteAndRead(pack, dir, out glowOptions, out _, suffixOverride, wearer);

    private static ProteusMetadata WriteAndRead(
        SyntheticTtmp pack, TempDir dir, out List<string> glowOptions, out List<string> defaultOn,
        string? suffixOverride = null, string? wearer = BiboBody)
    {
        var preview = Preview(pack, wearer);
        var written = LuminisImportService.WriteMod(
            dir.Path, "Synthwave", "Dame Douleur", preview,
            ["chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl"],
            suffixOverride, encodeTo: null, decode: Decode);
        glowOptions = written.Glow;
        defaultOn = written.DefaultOn;

        var json = File.ReadAllText(Path.Combine(dir.Path, "Proteus", "metadata.json"));
        return JsonSerializer.Deserialize<ProteusMetadata>(json, ProteusJson.MetadataRead)!;
    }

    [Fact]
    public void WritesOneGroupWithTheSkinOptionFirstAndTheGlowSecond()
    {
        var slice = SyntheticTtmp.TextureSlice(16, 16, 2);
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Dame Douleur",
            Tex("chara/bibo/highlander_d.tex", slice),
            Tex("chara/bibo_high_base.tex", slice));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out var glowOptions);

        // Everything in OptionGroups and NOTHING at the top level: ResolveActiveOverlays returns early on a
        // non-empty Overlays list and never reads the groups, so a top-level overlay would silently drop
        // the gated one.
        Assert.Null(metadata.Overlays);
        var group = Assert.Single(metadata.OptionGroups!);
        Assert.Equal(LuminisImportService.GroupName, group.PenumbraGroupName);
        Assert.Equal(2, group.Options.Count);

        // Skin first, so the author's body paints UNDER the glow.
        var skin = group.Options[0];
        var glow = group.Options[1];
        Assert.Contains("skin", skin.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(glowOptions, new[] { glow.Name });
    }

    /// <summary>
    /// The author's-skin option is a whole BODY, not a decal, and both skin-layer defaults are wrong for it.
    /// <para/>
    /// An unset <c>SkinToneMask</c> coalesces to 1, which is maximum masking: the compositor fades
    /// skin.shpk's skin-colour-influence channel out under the overlay, scaled by coverage × luminance.
    /// This art is opaque across the whole sheet and bright, so the wearer's tone was deleted over the
    /// entire body and the pack's pale skin rendered literally — a dark- or blue-skinned character came
    /// out white. And an unpinned skin overlay can be auto-promoted to a gear shell, which has no
    /// skin-tone term at all and where the mask is never read, so it would defeat the first fix entirely.
    /// </summary>
    [Fact]
    public void TheAuthorsSkinTakesTheWearersToneAndStaysSkin()
    {
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Tests", Tex("chara/bibo/highlander_d.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out _);
        var skin = Assert.Single(metadata.OptionGroups![0].Options[0].Overlays);

        Assert.Equal(0f, skin.SkinToneMask);
        Assert.True(skin.ManualShaderLock);
        Assert.Equal(OverlayLayer.Skin, skin.Layer);

        // The pin has to actually hold, not merely be set: this is the condition that would otherwise
        // move it onto character.shpk the moment anyone reorders the mod's stack tabs.
        Assert.False(RenderModeInference.ShouldPromoteToGear(
            skin.Layer, skin.ManualShaderLock, rows: null, aboveGear: true, canShell: true));
        Assert.True(RenderModeInference.ShouldPromoteToGear(
            skin.Layer, pinned: false, rows: null, aboveGear: true, canShell: true));
    }

    /// <summary>The glow rides its own gear shell, which never reaches the skin-tone path — claiming a
    /// mask there would imply it did.</summary>
    [Fact]
    public void TheGlowOptionClaimsNoSkinToneMask()
    {
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Tests", Tex("chara/bibo/highlander_d.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out _);
        var glow = Assert.Single(metadata.OptionGroups![0].Options[1].Overlays);

        Assert.Null(glow.SkinToneMask);
        Assert.Equal(OverlayLayer.Gear, glow.Layer);
    }

    [Fact]
    public void TheGlowOptionCarriesAScrollMapThatDoesNotScroll()
    {
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Tests", Tex("chara/bibo/highlander_d.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out _);
        var glow = Assert.Single(metadata.OptionGroups![0].Options[1].Overlays);

        // Layer AND Shader both stated. Promotion alone moves the layer but leaves the shader at plain
        // character.shpk, which has no scroll map — the shell then renders the art as an ordinary lit
        // surface and the effect is silently dropped.
        Assert.Equal(OverlayLayer.Gear, glow.Layer);
        Assert.Equal(RenderModeInference.GlowShader, glow.Shader);
        Assert.Equal(RenderModeInference.GlowShader, glow.ShaderPackage);
        Assert.Equal("bibo", glow.SourceBodyType);
        Assert.Equal("highlander_glow.png", glow.Scroll);
        // Zero explicitly: an unset speed takes GearMaterialWriter's default and slides the tattoo across
        // the skin it is drawn on.
        Assert.Equal(0f, glow.ScrollSpeedX);
        Assert.Equal(0f, glow.ScrollSpeedY);
        // One-to-one: the scroll map IS the body sheet, so tiling it would repeat the tattoo.
        Assert.Equal(1f, glow.ScrollTilingX);
        Assert.Equal(1f, glow.ScrollTilingY);

        Assert.True(File.Exists(Path.Combine(dir.Path, "Proteus", "Effects", "highlander_glow.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Proteus", "overlays", "highlander_glow.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Proteus", "overlays", "highlander_skin.png")));
    }

    /// <summary>
    /// The emissive lives on the GLOW OPTION, never at the top level. Top-level rows are inherited by any
    /// option that declares none, and any emissive makes <c>RenderModeInference.HasCloth</c> true — so an
    /// emissive up there would promote the author's plain body texture to a gear shell as well.
    /// </summary>
    [Fact]
    public void OnlyTheGlowOptionCarriesAnEmissiveRow()
    {
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Tests", Tex("chara/bibo/highlander_d.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out _);

        Assert.Null(metadata.ColorTableRows);
        Assert.Null(metadata.OptionGroups![0].Options[0].ColorTableRows);

        var rows = metadata.OptionGroups[0].Options[1].ColorTableRows!;
        Assert.NotEmpty(rows);

        // Every row is written identically, whether the sheet resolved to one region or several: the
        // plateaus differ in what the user can later do with them, not in how the import looks. The
        // per-pixel intensity that actually separates them is baked into the scroll map.
        foreach (var row in rows)
        {
            Assert.Null(row.SubRowB);
            // 300%, measured in game. Far above what Proteus writes anywhere else, because the scroll map is
            // mostly black with thin neon on it — only the lines have anything to scale.
            Assert.Equal(3.0f, row.SubRowA!.Emissive);
            Assert.True(row.SubRowA.Emissive <= 10f, "the editor's Glow dial clamps to 0-10");
            Assert.Equal(RenderModeInference.GlowEmissiveColour, row.SubRowA.EmissiveColor);
            // Black, not a near-black grey: characterscroll has no base texture, so this row colour is the
            // entire unlit surface and anything above black reads as lifted charcoal under scene light.
            Assert.Equal("#000000", row.SubRowA.Diffuse);
            // Dark-only, which is what an Atramentum Luminis tattoo was. Both halves: the glow fades with
            // the light and the surface goes with it, or the art reads as a black patch at noon.
            Assert.Equal(1f, row.SubRowA.LightResponse);
            Assert.True(row.SubRowA.HideInLight);
        }

        // Rows and index agree: with several regions the rows count up from 1 and an index selects them;
        // with one, the shell samples the fabricated (255, 255, 0) — row 16 — and no index is authored.
        var glow = metadata.OptionGroups[0].Options[1].Overlays![0];
        if (rows.Count > 1)
        {
            Assert.Equal([.. Enumerable.Range(1, rows.Count)], rows.Select(r => r.Row));
            Assert.NotNull(glow.Index);
            // PNG, never .tex: an index is a lookup, and BC7 would move a texel's red across a row bucket.
            Assert.EndsWith(".png", glow.Index);
            Assert.True(File.Exists(Path.Combine(dir.Path, "Proteus", glow.Index!.Replace('/', Path.DirectorySeparatorChar))));
        }
        else
        {
            Assert.Equal(16, rows[0].Row);
            Assert.Null(glow.Index);
        }

        Assert.True(RenderModeInference.HasCloth(rows));
        Assert.False(RenderModeInference.HasCloth([]));
    }

    /// <summary>
    /// The plateau split, on a mask shaped the way a real Atramentum Luminis one is: flat fills with an
    /// antialiased edge between them. Two fills must come back as two regions, brightest first, and the
    /// ramp between them must not earn a region of its own.
    /// </summary>
    [Fact]
    public void SplitsFlatPlateausIntoRegionsBrightestFirst()
    {
        // 64×64. Left half glows fully (alpha 0), right half at half strength (alpha 128), with one column
        // of ramp between them standing in for the antialiased outline.
        const int w = 64, h = 64;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = 200;
                rgba[i + 3] = x < 31 ? (byte)0 : x == 31 ? (byte)64 : (byte)128;
            }

        var bands = LuminisImportService.GlowBands(rgba);

        Assert.Equal(2, bands.Count);
        Assert.Equal(255, bands[0]);   // alpha 0 → fully lit, and first
        Assert.Equal(127, bands[1]);   // alpha 128 → half lit

        var id = LuminisImportService.BuildGlowIndex(rgba, bands);
        // (row − 1) × 17, so band 0 → row 1 → red 0, band 1 → row 2 → red 17. Green 255 = sub-row A.
        Assert.Equal(0, id[0]);
        Assert.Equal(255, id[1]);
        Assert.Equal(17, id[(0 * w + 63) * 4]);
        // Round-trips through the decoder the shell actually uses, which is the only reading that matters.
        Assert.Equal(1, ContentIndexTexture.RowOf(0));
        Assert.Equal(2, ContentIndexTexture.RowOf(17));
    }

    /// <summary>
    /// A mask with nothing on it, and one with a single flat fill, both keep the old single-row shape —
    /// an index that says "every texel is row 1" is a texture and a row nobody needed.
    /// </summary>
    [Theory]
    [InlineData(255, 0)]   // opaque everywhere: nothing glows at all
    [InlineData(0, 1)]     // one flat fill
    public void OneRegionOrNoneAuthorsNoIndex(byte alpha, int expectedBands)
    {
        var rgba = new byte[32 * 32 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 180;
            rgba[i + 3] = alpha;
        }

        Assert.Equal(expectedBands, LuminisImportService.GlowBands(rgba).Count);
    }

    /// <summary>
    /// The channel transforms, asserted on real pixels rather than inferred from the file existing.
    /// <para/>
    /// The split that matters: Atramentum Luminis's alpha is INTENSITY, not opacity, so coverage says only
    /// whether a pixel is part of the panel (opaque wherever it glows at all) and the scroll map carries
    /// how brightly. Putting the intensity in coverage instead made half-lit panels translucent, which
    /// showed up in game as two different blacks meeting mid-thigh.
    /// </summary>
    [Fact]
    public void CoverageIsPresenceAndTheScrollMapCarriesColourAndIntensity()
    {
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Tests", Tex("chara/bibo/highlander_d.tex"));
        using var dir = new TempDir();

        WriteAndRead(pack, dir, out _);

        var overlays = Path.Combine(dir.Path, "Proteus", "overlays");
        var glow = Png(Path.Combine(overlays, "highlander_glow.png"));
        var skin = Png(Path.Combine(overlays, "highlander_skin.png"));
        var scroll = Png(Path.Combine(dir.Path, "Proteus", "Effects", "highlander_glow.png"));

        // Alpha 255 -> absent; anything that glows at all -> OPAQUE, half-lit included.
        Assert.Equal([0, 255, 255, 0], [glow[3], glow[7], glow[11], glow[15]]);
        // Colour is carried through untouched — the shell's own art.
        Assert.Equal([10, 20, 30], glow[..3]);

        // The author's picture, made opaque.
        Assert.Equal([255, 255, 255, 255], [skin[3], skin[7], skin[11], skin[15]]);
        Assert.Equal(SourceRgba[..3], skin[..3]);

        // Black where nothing glows; scaled by intensity where something does — half at alpha 128, full
        // at alpha 0. This is where the mask's mid-tones went once coverage stopped carrying them.
        Assert.Equal([0, 0, 0, 255], scroll[..4]);
        Assert.Equal([20, 25, 30, 255], scroll[4..8]);
        Assert.Equal([70, 80, 90, 255], scroll[8..12]);
    }

    /// <summary>
    /// Stems name the written files and the Penumbra options, so two payloads may not share one — a pack
    /// with the same leaf under two bodies would otherwise write both over one file and offer two options
    /// with the same name.
    /// </summary>
    [Fact]
    public void TwoPicturesWithTheSameNameGetDistinctStems()
    {
        using var pack = SyntheticTtmp.Wizard("Clash", "Tests",
            Tex("chara/bibo/midlander_d.tex", SyntheticTtmp.TextureSlice(16, 16, 2)),
            Tex("chara/gen3/midlander_d.tex", SyntheticTtmp.TextureSlice(8, 8, 2)));

        var stems = Preview(pack).Textures.Select(t => t.Stem).ToList();

        Assert.Equal(["midlander", "midlander_gen3"], stems);
    }

    [Fact]
    public void QualifiesTheOptionNamesOnlyWhenThePackShipsSeveralPictures()
    {
        using var one = SyntheticTtmp.Wizard("One", "Tests", Tex("chara/bibo/highlander_d.tex"));
        using var oneDir = new TempDir();
        var single = WriteAndRead(one, oneDir, out _);
        Assert.DoesNotContain("highlander", single.OptionGroups![0].Options[1].Name,
                              StringComparison.OrdinalIgnoreCase);

        using var many = SyntheticTtmp.Wizard("Many", "Tests",
            Tex("chara/bibo/highlander_d.tex", SyntheticTtmp.TextureSlice(16, 16, 2)),
            Tex("chara/bibo/viera_d.tex", SyntheticTtmp.TextureSlice(8, 8, 2)));
        using var manyDir = new TempDir();
        var several = WriteAndRead(many, manyDir, out var glowOptions);

        Assert.Equal(4, several.OptionGroups![0].Options.Count);
        Assert.Equal(2, glowOptions.Count);
        Assert.All(glowOptions, n => Assert.Contains("—", n));
    }

    /// <summary>
    /// The FIRST pair on and the rest off — the first skin option and the first glow option, which are two
    /// halves of one drawing. The glow options come after every skin option, so the glow's bit is its index
    /// past them, and DefaultOn has to name exactly the same two: Penumbra's DefaultSettings only reaches a
    /// collection that has never seen this mod, and Finish asserts DefaultOn for the ones that have.
    /// </summary>
    [Fact]
    public void TheGroupArrivesWithTheFirstPairTicked()
    {
        using var pack = SyntheticTtmp.Wizard("Many", "Tests",
            Tex("chara/bibo/highlander_d.tex", SyntheticTtmp.TextureSlice(16, 16, 2)),
            Tex("chara/bibo/viera_d.tex", SyntheticTtmp.TextureSlice(8, 8, 2)));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out _, out var defaultOn);

        var group = JsonDocument.Parse(File.ReadAllText(
            Directory.EnumerateFiles(dir.Path, "group_*.json").Single())).RootElement;

        Assert.Equal("Multi", group.GetProperty("Type").GetString());
        Assert.Equal(LuminisImportService.GroupName, group.GetProperty("Name").GetString());
        // Two skins then two glows: bit 0 is the first skin, bit 2 the first glow.
        Assert.Equal(4, group.GetProperty("Options").GetArrayLength());
        Assert.Equal(0b0101, group.GetProperty("DefaultSettings").GetInt64());

        var names = metadata.OptionGroups![0].Options.Select(o => o.Name).ToList();
        Assert.Equal([names[0], names[2]], defaultOn);
    }

    /// <summary>
    /// A pack mixing body tokens keeps each texture's OWN UV space when the user hasn't retargeted.
    /// <para/>
    /// The Import tab seeds its body combo from the resolved default and hands that back on every import,
    /// so "an override was supplied" never meant "the user aimed this somewhere". Testing the override
    /// against each plan's own suffix made the gen3 plan — which differs from the seeded bibo default —
    /// read as retargeted, and its art was declared to already be in bibo space: the remap that would have
    /// made it fit was skipped, silently, on exactly the case the guard existed for.
    /// </summary>
    [Fact]
    public void AMixedBodyPackKeepsEachTexturesOwnUvSpace()
    {
        using var pack = SyntheticTtmp.Wizard("Mixed bodies", "Tests",
            Tex("chara/bibo/highlander_d.tex", SyntheticTtmp.TextureSlice(16, 16, 2)),
            Tex("chara/gen3/highlander_d.tex", SyntheticTtmp.TextureSlice(8, 8, 2)));
        using var dir = new TempDir();

        var preview = Preview(pack);
        Assert.Equal(["bibo", "gen3"], preview.Importable.Select(p => p.BodyType));

        // What the tab passes when nobody touches the combo: the default it seeded.
        LuminisImportService.WriteMod(
            dir.Path, "Mixed", "Tests", preview, ["chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl"],
            suffixOverride: preview.DefaultSuffix, encodeTo: null, decode: Decode);

        var metadata = JsonSerializer.Deserialize<ProteusMetadata>(
            File.ReadAllText(Path.Combine(dir.Path, "Proteus", "metadata.json")), ProteusJson.MetadataRead)!;
        var glows = metadata.OptionGroups![0].Options
            .Where(o => o.ColorTableRows != null)
            .Select(o => o.Overlays[0].SourceBodyType)
            .ToList();

        Assert.Equal(["bibo", "gen3"], glows);
    }

    [Fact]
    public void AimsAtTheOverriddenBodyWithoutRemappingTheArt()
    {
        using var pack = SyntheticTtmp.Wizard("Bibo tattoo", "Tests", Tex("chara/bibo/highlander_d.tex"));
        using var dir = new TempDir();

        var preview = Preview(pack);
        LuminisImportService.WriteMod(
            dir.Path, "Overridden", "Tests", preview, [MaleBody],
            suffixOverride: "_b.mtrl", encodeTo: null, decode: Decode);

        var json = File.ReadAllText(Path.Combine(dir.Path, "Proteus", "metadata.json"));
        var metadata = JsonSerializer.Deserialize<ProteusMetadata>(json, ProteusJson.MetadataRead)!;
        var glow = metadata.OptionGroups![0].Options[1].Overlays[0];

        Assert.Equal([MaleBody], glow.MaterialGamePaths);
        // Declared to be in the DESTINATION's space, so the remap is a no-op. Running bibo art through a
        // transfer map onto a body the user has just said it is not for would be the one wrong answer.
        Assert.Equal(UVRemapService.InferBodyType(MaleBody), glow.SourceBodyType);
    }

    [Fact]
    public void WritesAPenumbraManifestAndASelfSwapSoTheModIsntEmpty()
    {
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Dame Douleur", Tex("chara/bibo/highlander_d.tex"));
        using var dir = new TempDir();

        WriteAndRead(pack, dir, out _);

        var meta = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir.Path, PenumbraModMeta.MetaFile))).RootElement;
        Assert.Equal("Synthwave", meta.GetProperty("Name").GetString());
        Assert.Equal("Dame Douleur", meta.GetProperty("Author").GetString());
        Assert.Contains("Atramentum Luminis", meta.GetProperty("Description").GetString());

        // Penumbra flags a mod that redirects nothing as "changes nothing"; Proteus does the real
        // redirection itself at composite time, so the same harmless self-swap the Create tab uses.
        // NewMetaJson pins the folder to the pre-v4 layout, where the key is "Swaps" (v4 renamed it to
        // "FileSwaps" inside DefaultData) — accept either, since WriteRedirects picks by format.
        var def = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir.Path, PenumbraModMeta.LegacyDefaultMod))).RootElement;
        var swaps = def.TryGetProperty("Swaps", out var s) ? s : def.GetProperty("FileSwaps");
        Assert.True(swaps.EnumerateObject().Any());
    }

    /// <summary>RGBA8 straight out of a PNG the import wrote.</summary>
    private static byte[] Png(string path)
    {
        using var stream = File.OpenRead(path);
        var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return image.Data;
    }
}
