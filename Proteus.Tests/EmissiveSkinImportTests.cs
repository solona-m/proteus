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
/// The emissive-skin import: which packs it claims, which textures it recognises, and the mod it writes.
/// Everything runs offline — <c>BuildPreview</c> and <c>WriteMod</c> both take the texture decoder as a
/// delegate, which is the only part that would need a live game.
/// </summary>
public class EmissiveSkinImportTests
{
    /// <summary>A wearer on Bibo+; the material path Penumbra would report for one.</summary>
    private const string BiboBody = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";

    /// <summary>A wearer on a male body — TBSE and HRBody both sit at the <c>_b</c> suffix.</summary>
    private const string MaleBody = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl";

    /// <summary>One of the body materials these packs rewire, which the import must leave alone.</summary>
    private static SyntheticPack.Redirect BodyMaterial(string race = "c0201")
        => new($"chara/human/{race}/obj/body/b0001/material/v0001/mt_{race}b0001_bibo.mtrl",
               $"races/{race}.mtrl");

    /// <summary>A redirect whose archive entry is named after its game path, which is how these packs are
    /// laid out: the art sits at the virtual path it is addressed by.</summary>
    private static SyntheticPack.Redirect Art(string gamePath, string? entry = null, bool present = true)
        => new(gamePath, entry ?? gamePath, present);

    /// <summary>Every texture measures as a real mask at the given size.</summary>
    private static Func<byte[], string, (int Width, int Height, float Glow)?> Masked(
        float glow = 0.0007f, int size = 4096)
        => (_, _) => (size, size, glow);

    private static EmissiveSkinImportService.ImportPreview Preview(
        SyntheticPack pack,
        string? wearer = BiboBody,
        Func<byte[], string, (int Width, int Height, float Glow)?>? measure = null)
        => EmissiveSkinImportService.BuildPreview(
            pack.Path, PenumbraPackage.Read(pack.Path), wearer, measure ?? Masked());

    // ── which reader gets the pack ───────────────────────────────────────────

    /// <summary>
    /// The Secret Succubus shape: body materials rewired to name an emissive sampler, and the art those
    /// materials point at on a path the game will never ask for. Nothing here is geometry, so the content
    /// importer would have refused it with "no option in this pack redirects a model".
    /// </summary>
    [Fact]
    public void ClaimsAPackOfVirtualArtWithNoGeometry()
    {
        using var pack = SyntheticPack.NoGeometry("Secret Succubus", "Ram Ram",
            BodyMaterial(), BodyMaterial("c0401"),
            Art("chara/bibo/emissive.tex"));

        Assert.True(EmissiveSkinImportService.Claims(PenumbraPackage.Read(pack.Path)));
    }

    /// <summary>
    /// Geometry wins, whatever else the pack carries. A model is the content importer's whole subject and
    /// something this one cannot place at all, so routing an outfit here to chase one virtual texture would
    /// lose every mesh in it.
    /// </summary>
    [Fact]
    public void LeavesAPackThatShipsAModelToTheContentImporter()
    {
        using var pack = SyntheticPack.NoGeometry("Outfit", "Tests",
            Art("chara/bibo/emissive.tex"),
            Art("chara/equipment/e6046/model/c0201e6046_top.mdl", "model.mdl"));

        Assert.False(EmissiveSkinImportService.Claims(PenumbraPackage.Read(pack.Path)));
    }

    /// <summary>An ordinary texture mod redirects real game paths. Penumbra installs those perfectly well
    /// on its own, and there is no glow art here to take.</summary>
    [Fact]
    public void LeavesAnOrdinaryTextureModAlone()
    {
        using var pack = SyntheticPack.NoGeometry("Real paths", "Tests",
            BodyMaterial(),
            Art("chara/human/c0201/obj/body/b0001/texture/--c0201b0001_b_d.tex", "skin_d.tex"));

        Assert.False(EmissiveSkinImportService.Claims(PenumbraPackage.Read(pack.Path)));
    }

    // ── what the preview makes of it ─────────────────────────────────────────

    [Fact]
    public void ReadsTheBodyAndTheMaskOutOfAVirtualPath()
    {
        using var pack = SyntheticPack.NoGeometry("Secret Succubus", "Ram Ram",
            BodyMaterial(),
            Art("chara/bibo/emissive.tex"));

        var preview = Preview(pack);

        // The body materials are not textures on virtual paths, so they never become plans at all.
        var plan = Assert.Single(preview.Textures);
        Assert.True(plan.Import);
        Assert.Equal("bibo", plan.Token);
        Assert.Equal("bibo", plan.BodyType);
        Assert.Equal("_bibo.mtrl", plan.Suffix);
        Assert.False(plan.FromWearer);
        Assert.Equal("bibo_emissive", plan.Stem);
        Assert.Equal("Ram Ram", preview.Author);
        Assert.Empty(preview.Warnings);
    }

    /// <summary>
    /// Tight &amp; Firm's Gen3, which these packs address by that name. Without the token in the body table
    /// it falls through to whatever the wearer has on, and a pack shipping bibo and tfgen3 sheets of one
    /// tattoo then declares the tfgen3 one to already be in bibo space — skipping the very remap that would
    /// have made it land correctly.
    /// </summary>
    [Fact]
    public void KnowsTfGen3IsGen3ForABiboWearer()
    {
        using var pack = SyntheticPack.NoGeometry("Two bodies", "Tests",
            Art("chara/tfgen3/emissive.tex"));

        var plan = Assert.Single(Preview(pack).Textures);

        Assert.Equal("gen3", plan.BodyType);
        Assert.Equal("_b.mtrl", plan.Suffix);
        Assert.False(plan.FromWearer);
    }

    /// <summary>These packs name their sheets for what they ARE rather than for what is on them, so a pack
    /// shipping two body layouts collides on every file name. Qualifying by token is what keeps the two
    /// distinguishable in the mod folder afterwards.</summary>
    [Fact]
    public void TwoBodiesOfOneTattooGetStemsNamedForTheirBody()
    {
        using var pack = SyntheticPack.NoGeometry("Two bodies", "Tests",
            Art("chara/bibo/emissive.tex"),
            Art("chara/tfgen3/emissive.tex"));

        Assert.Equal(["bibo_emissive", "tfgen3_emissive"],
                     Preview(pack).Textures.Select(t => t.Stem));
    }

    /// <summary>One picture named by several paths is one tattoo. Importing it once per path would stack a
    /// shell on its own copy.</summary>
    [Fact]
    public void CollapsesSeveralPathsOverOneArchiveEntry()
    {
        using var pack = SyntheticPack.NoGeometry("Aliased", "Tests",
            Art("chara/bibo/emissive.tex", "art.tex"),
            Art("chara/bibo/emissive_hi.tex", "art.tex"));

        var plan = Assert.Single(Preview(pack).Textures);
        Assert.Equal(2, plan.Paths.Count);
    }

    /// <summary>
    /// The pack's other virtual texture: a shader effect or palette map, 32² in the pack this was written
    /// against. Usually its alpha is empty and the mask test rejects it anyway; this is what stops one that
    /// ISN'T empty from being stretched across a whole body as though it were a tattoo.
    /// </summary>
    [Fact]
    public void SkipsAnEffectMapTooSmallToBeBodyArt()
    {
        using var pack = SyntheticPack.NoGeometry("With effect", "Tests",
            Art("chara/bibo/effect.tex"));

        var plan = Assert.Single(Preview(pack, measure: Masked(glow: 0.9f, size: 32)).Textures);

        Assert.False(plan.Import);
        Assert.Contains("32×32", plan.SkipReason);
    }

    [Fact]
    public void SkipsATextureWhoseAlphaMarksNothing()
    {
        using var pack = SyntheticPack.NoGeometry("Flat", "Tests",
            Art("chara/bibo/emissive.tex"));

        var plan = Assert.Single(Preview(pack, measure: Masked(glow: 0f)).Textures);

        Assert.False(plan.Import);
        Assert.Contains("alpha", plan.SkipReason);
    }

    /// <summary>Legal, but usually a texture with no real alpha channel — warned about, not refused.</summary>
    [Fact]
    public void WarnsWhenAlmostEverythingGlowsButStillImports()
    {
        using var pack = SyntheticPack.NoGeometry("All glow", "Tests",
            Art("chara/bibo/emissive.tex"));

        var preview = Preview(pack, measure: Masked(glow: 0.99f));

        Assert.True(Assert.Single(preview.Textures).Import);
        Assert.NotEmpty(preview.Warnings);
    }

    /// <summary>A manifest naming a file the archive doesn't hold is a broken pack, not a crash.</summary>
    [Fact]
    public void ReportsAnEntryTheArchiveDoesNotHold()
    {
        using var pack = SyntheticPack.NoGeometry("Broken", "Tests",
            Art("chara/bibo/emissive.tex", "missing.tex", present: false));

        var plan = Assert.Single(Preview(pack).Textures);

        Assert.False(plan.Import);
        Assert.Contains("missing.tex", plan.SkipReason);
    }

    [Fact]
    public void AnUnknownBodyFallsBackToTheOneTheCharacterIsWearing()
    {
        using var pack = SyntheticPack.NoGeometry("TBSE glow", "Tests",
            Art("chara/tbse/emissive.tex"));

        var preview = Preview(pack, wearer: MaleBody);

        var plan = Assert.Single(preview.Textures);
        Assert.True(plan.Import);
        Assert.Equal("_b.mtrl", plan.Suffix);
        Assert.True(plan.FromWearer);
        // Source equals destination, so UvConverter short-circuits and nothing is resized.
        Assert.Equal(UVRemapService.InferBodyType(MaleBody), plan.BodyType);
        Assert.NotEmpty(preview.Warnings);   // the guess is stated, not made silently
    }

    [Fact]
    public void AnUnknownBodyIsSkippedOnlyWhenTheCharacterIsntDrawn()
    {
        using var pack = SyntheticPack.NoGeometry("TBSE glow", "Tests",
            Art("chara/tbse/emissive.tex"));

        var plan = Assert.Single(Preview(pack, wearer: null).Textures);

        Assert.False(plan.Import);
        Assert.Contains("tbse", plan.SkipReason);
    }

    // ── the written mod ──────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "proteus-emissive-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* a temp dir that outlives the test is harmless */ }
        }
    }

    /// <summary>
    /// A 2×2 sheet whose alpha runs 0, 128, 255, 0 — one pixel outside the art, one at half strength, one
    /// at full, and one more outside. Enough to assert every channel transform, and two plateaus, so the
    /// region split has something to find.
    /// </summary>
    private static readonly byte[] SourceRgba =
    [
        10, 20, 30, 0,
        40, 50, 60, 128,
        70, 80, 90, 255,
        100, 110, 120, 0,
    ];

    /// <summary>The same sheet with one flat fill, which is what a real mask usually is: a binary shape
    /// with an antialiased outline, 70% of it at 255.</summary>
    private static readonly byte[] FlatRgba =
    [
        200, 200, 200, 0,
        200, 200, 200, 255,
        200, 200, 200, 255,
        200, 200, 200, 0,
    ];

    private static Func<byte[], string, (byte[] Rgba, int Width, int Height)?> Decoding(byte[] rgba)
        => (_, _) => (rgba, 2, 2);

    private static ProteusMetadata WriteAndRead(
        SyntheticPack pack, TempDir dir, out EmissiveSkinImportService.WrittenOptions written,
        byte[]? rgba = null, string? suffixOverride = null, IReadOnlyList<string>? materials = null)
    {
        var preview = Preview(pack);
        written = EmissiveSkinImportService.WriteMod(
            dir.Path, "Succubus", "Ram Ram", preview, materials ?? [BiboBody],
            suffixOverride, encodeTo: null, decode: Decoding(rgba ?? SourceRgba));

        var json = File.ReadAllText(Path.Combine(dir.Path, "Proteus", "metadata.json"));
        return JsonSerializer.Deserialize<ProteusMetadata>(json, ProteusJson.MetadataRead)!;
    }

    [Fact]
    public void WritesOneGlowOptionOnTheAnimatedGlowShader()
    {
        using var pack = SyntheticPack.NoGeometry("Succubus", "Ram Ram",
            BodyMaterial(),
            Art("chara/bibo/emissive.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out var written);

        // Everything in OptionGroups and NOTHING at the top level: ResolveActiveOverlays returns early on a
        // non-empty Overlays list and never reads the groups.
        Assert.Null(metadata.Overlays);
        var group = Assert.Single(metadata.OptionGroups!);
        Assert.Equal(EmissiveSkinImportService.GroupName, group.PenumbraGroupName);
        var option = Assert.Single(group.Options);
        Assert.Equal(written.Options, new[] { option.Name });

        var glow = Assert.Single(option.Overlays);
        // Layer AND Shader both stated. Promotion alone moves the layer but leaves the shader at plain
        // character.shpk, which has no scroll map — the effect is then silently dropped.
        Assert.Equal(OverlayLayer.Gear, glow.Layer);
        Assert.Equal(RenderModeInference.GlowShader, glow.Shader);
        Assert.Equal(RenderModeInference.GlowShader, glow.ShaderPackage);
        Assert.Equal("bibo", glow.SourceBodyType);
        Assert.Equal([BiboBody], glow.MaterialGamePaths);
        Assert.Equal("bibo_emissive.png", glow.Scroll);
        // Zero explicitly: an unset speed takes GearMaterialWriter's default and slides the tattoo across
        // the skin it is drawn on. One-to-one tiling: the map IS the body sheet.
        Assert.Equal(0f, glow.ScrollSpeedX);
        Assert.Equal(0f, glow.ScrollSpeedY);
        Assert.Equal(1f, glow.ScrollTilingX);
        Assert.Equal(1f, glow.ScrollTilingY);
        // No skin-tone mask: this rides its own gear shell, which never reaches the skin-tone path at all.
        Assert.Null(glow.SkinToneMask);

        Assert.True(File.Exists(Path.Combine(dir.Path, "Proteus", "Effects", "bibo_emissive.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Proteus", "overlays", "bibo_emissive.png")));
    }

    /// <summary>
    /// The glow row, and the one place this parts company with the Atramentum Luminis import: no
    /// LightResponse and no HideInLight. That mod's tattoos were dark-only by design; an emissive sampler on
    /// skin.shpk adds light at noon as much as at midnight, so an unconditional glow is what parity means
    /// here.
    /// </summary>
    [Fact]
    public void TheGlowRowIsUnconditionalAndSharesTheShellTuning()
    {
        using var pack = SyntheticPack.NoGeometry("Succubus", "Tests",
            Art("chara/bibo/emissive.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out _, FlatRgba);

        // On the OPTION, never at the top level: top-level rows are inherited by every option that declares
        // none, and any emissive makes RenderModeInference.HasCloth true.
        Assert.Null(metadata.ColorTableRows);
        var rows = metadata.OptionGroups![0].Options[0].ColorTableRows!;
        var row = Assert.Single(rows);

        // One flat fill, so the shell samples the fabricated (255,255,0) — row 16 — and no index is written.
        Assert.Equal(GlowShell.Row, row.Row);
        Assert.Null(metadata.OptionGroups[0].Options[0].Overlays[0].Index);

        Assert.Null(row.SubRowB);
        Assert.Equal(GlowShell.Emissive, row.SubRowA!.Emissive);
        Assert.Equal(RenderModeInference.GlowEmissiveColour, row.SubRowA.EmissiveColor);
        Assert.Equal(GlowShell.SurfaceColour, row.SubRowA.Diffuse);
        Assert.Null(row.SubRowA.LightResponse);
        Assert.False(row.SubRowA.HideInLight);

        Assert.True(RenderModeInference.HasCloth(rows));
    }

    /// <summary>
    /// The channel transforms, asserted on real pixels. The split that matters is the one against
    /// Atramentum Luminis: an emissive map's alpha is already presence, the right way up, so coverage is the
    /// mask verbatim — neither inverted nor multiplied toward opaque.
    /// </summary>
    [Fact]
    public void CoverageIsTheMaskVerbatimAndTheScrollMapCarriesColourTimesIt()
    {
        using var pack = SyntheticPack.NoGeometry("Succubus", "Tests",
            Art("chara/bibo/emissive.tex"));
        using var dir = new TempDir();

        WriteAndRead(pack, dir, out _);

        var art = Png(Path.Combine(dir.Path, "Proteus", "overlays", "bibo_emissive.png"));
        var scroll = Png(Path.Combine(dir.Path, "Proteus", "Effects", "bibo_emissive.png"));

        Assert.Equal([0, 128, 255, 0], [art[3], art[7], art[11], art[15]]);
        // Colour carried through untouched — the shell's own art, at the colour the author chose.
        Assert.Equal(SourceRgba[..3], art[..3]);

        // Black where nothing glows; scaled by the mask where something does.
        Assert.Equal([0, 0, 0, 255], scroll[..4]);
        Assert.Equal([20, 25, 30, 255], scroll[4..8]);
        Assert.Equal([70, 80, 90, 255], scroll[8..12]);
    }

    /// <summary>Two plateaus earn a row each, so one half of a tattoo can later be made dark-only while the
    /// other stays on. The index is the only thing that can address them separately.</summary>
    [Fact]
    public void TwoPlateausGetARowEachAndAnIndexToSelectThem()
    {
        using var pack = SyntheticPack.NoGeometry("Succubus", "Tests",
            Art("chara/bibo/emissive.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(pack, dir, out _);
        var option = metadata.OptionGroups![0].Options[0];

        Assert.Equal([1, 2], option.ColorTableRows!.Select(r => r.Row));
        // PNG, never .tex: an index is a lookup, and BC7 would move a texel's red across a row bucket.
        Assert.Equal("overlays/bibo_emissive_id.png", option.Overlays[0].Index);

        var id = Png(Path.Combine(dir.Path, "Proteus", "overlays", "bibo_emissive_id.png"));
        // (row − 1) × 17, brightest band first: alpha 255 is band 0 → row 1, alpha 128 is band 1 → row 2.
        Assert.Equal(0, id[2 * 4]);
        Assert.Equal(17, id[1 * 4]);
        Assert.Equal(255, id[1 * 4 + 1]);   // sub-row A
        Assert.Equal(1, ContentIndexTexture.RowOf(0));
        Assert.Equal(2, ContentIndexTexture.RowOf(17));
    }

    /// <summary>
    /// Exactly ONE option on, where the Atramentum Luminis import turns on a pair. Several options here are
    /// several UV layouts of the SAME tattoo, all aimed at the one body the user picked, so wearing two
    /// would stack a shell on its own copy. The one already in the destination's space is the one that needs
    /// no resampling.
    /// </summary>
    [Fact]
    public void ArrivesWearingTheLayoutThatMatchesTheTargetBody()
    {
        using var pack = SyntheticPack.NoGeometry("Two bodies", "Tests",
            Art("chara/bibo/emissive.tex"),
            Art("chara/tfgen3/emissive.tex"));
        using var dir = new TempDir();

        // Aimed at a gen3 body, so the tfgen3 sheet — the SECOND option — is the one to wear.
        var metadata = WriteAndRead(pack, dir, out var written, materials: [MaleBody]);

        var names = metadata.OptionGroups![0].Options.Select(o => o.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.Contains("—", n));
        Assert.Equal([names[1]], written.DefaultOn);

        var group = JsonDocument.Parse(File.ReadAllText(
            Directory.EnumerateFiles(dir.Path, "group_*.json").Single())).RootElement;
        Assert.Equal("Multi", group.GetProperty("Type").GetString());
        Assert.Equal(EmissiveSkinImportService.GroupName, group.GetProperty("Name").GetString());
        Assert.Equal(0b10, group.GetProperty("DefaultSettings").GetInt64());
    }

    [Fact]
    public void QualifiesTheOptionNameOnlyWhenThePackShipsSeveralLayouts()
    {
        using var one = SyntheticPack.NoGeometry("One", "Tests",
            Art("chara/bibo/emissive.tex"));
        using var dir = new TempDir();

        var metadata = WriteAndRead(one, dir, out _);

        Assert.DoesNotContain("—", metadata.OptionGroups![0].Options[0].Name);
    }

    /// <summary>
    /// The pack's OWN redirects are deliberately not carried over. They are body materials rewired to name
    /// an emissive sampler that only a replaced skin.shpk has, and republishing them would put the imported
    /// mod into a fight with Proteus over the very material it composites into.
    /// </summary>
    [Fact]
    public void RepublishesNoneOfThePacksOwnMaterialRedirects()
    {
        using var pack = SyntheticPack.NoGeometry("Succubus", "Ram Ram",
            BodyMaterial(), BodyMaterial("c0401"),
            Art("chara/bibo/emissive.tex"));
        using var dir = new TempDir();

        WriteAndRead(pack, dir, out _);

        var meta = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir.Path, PenumbraModMeta.MetaFile))).RootElement;
        Assert.Equal("Succubus", meta.GetProperty("Name").GetString());
        Assert.Equal("Ram Ram", meta.GetProperty("Author").GetString());
        Assert.Contains("emissive skin pack", meta.GetProperty("Description").GetString());

        var def = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir.Path, PenumbraModMeta.LegacyDefaultMod))).RootElement;
        Assert.Empty(def.GetProperty("Files").EnumerateObject());

        // Penumbra flags a mod that redirects nothing as "changes nothing", so the same harmless self-swap
        // the Create tab uses. NewMetaJson pins the folder to the pre-v4 layout, where the key is "Swaps".
        var swaps = def.TryGetProperty("Swaps", out var s) ? s : def.GetProperty("FileSwaps");
        Assert.True(swaps.EnumerateObject().Any());
    }

    /// <summary>
    /// A sheet larger than the composite's own working size is resampled once, here, rather than on every
    /// composite that reads it back. These masks really do arrive at 8192².
    /// </summary>
    [Theory]
    [InlineData(8192, 8192, 4096, 4096)]
    [InlineData(4096, 4096, 4096, 4096)]
    [InlineData(2048, 1024, 2048, 1024)]
    public void OversizeSheetsAreBroughtDownToTheCompositesOwnSize(int w, int h, int fitW, int fitH)
    {
        var rgba = new byte[w * h * 4];
        var (fitted, gotW, gotH) = EmissiveSkinImportService.Fit(rgba, w, h);

        Assert.Equal(fitW, gotW);
        Assert.Equal(fitH, gotH);
        Assert.Equal(fitW * fitH * 4, fitted.Length);
        // Untouched when it already fits, so the common case copies nothing.
        if (w == fitW && h == fitH) Assert.Same(rgba, fitted);
    }

    /// <summary>
    /// The <c>.tex</c> header read that lets an undersized sheet be rejected without decoding it. Worth a
    /// test of its own because the offsets are hand-decoded: a wrong pair of bytes here would either inflate
    /// a palette map to RGBA anyway (harmless, just slow) or throw away real art on a bogus size.
    /// </summary>
    [Theory]
    [InlineData(8192, 8192)]
    [InlineData(4096, 4096)]
    [InlineData(32, 32)]
    public void TexSizeReadsTheDimensionsOffTheHeader(int width, int height)
    {
        var tex = new byte[80];
        BitConverter.TryWriteBytes(tex.AsSpan(8), (ushort)width);
        BitConverter.TryWriteBytes(tex.AsSpan(10), (ushort)height);

        Assert.Equal((width, height), EmissiveSkinImportService.TexSize(tex));
    }

    [Fact]
    public void TexSizeDeclinesToGuessAtBytesThatAreNotAHeader()
    {
        Assert.Null(EmissiveSkinImportService.TexSize(new byte[79]));   // too short to hold one
        Assert.Null(EmissiveSkinImportService.TexSize(new byte[80]));   // a header saying 0×0
    }

    /// <summary>RGBA8 straight out of a PNG the import wrote.</summary>
    private static byte[] Png(string path)
    {
        using var stream = File.OpenRead(path);
        var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return image.Data;
    }
}
