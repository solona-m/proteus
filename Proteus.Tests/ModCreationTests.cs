using System.IO;
using System.Text.Json;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Offline coverage for the Create-tab mod writer: the pure filesystem step (no Penumbra IPC) and the
/// directory-name sanitizer.
/// </summary>
public class ModCreationTests
{
    /// <summary>Run <see cref="ModCreationService.WriteMod"/> against a throwaway directory and hand the
    /// caller the parsed sidecar. Every glow test needs the same six lines otherwise.</summary>
    private static void WithMod(GlowStyle glow, byte[]? scroll, int w, int h,
                                System.Action<string, ProteusMetadata> check, string? artColour = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus_create_" + Path.GetRandomFileName());
        var diffuse = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        File.WriteAllBytes(diffuse, new byte[] { 1, 2, 3, 4 });

        try
        {
            ModCreationService.WriteMod(
                root, "Glowy", "Artist",
                "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
                diffuseSrc: diffuse, maskSrc: null, normalSrc: null, indexSrc: null,
                glow: glow, scrollRgba: scroll, scrollW: w, scrollH: h, artColour: artColour);

            var meta = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(Path.Combine(root, "Proteus", "metadata.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            check(root, meta);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { File.Delete(diffuse); } catch { }
        }
    }

    /// <summary>
    /// "Always glow" carries NO light response, which is what makes its name true. An earlier draft faded
    /// it to 15% at midday; on character.shpk the emissive is a flat additive tint that a lit scene already
    /// swamps, so the response bought nothing and contradicted the label. It also keeps this style working
    /// with the light feature switched off entirely.
    /// </summary>
    [Fact]
    public void WriteMod_always_glow_is_unconditional()
    {
        WithMod(GlowStyle.Always, null, 0, 0, (root, meta) =>
        {
            var ov = Assert.Single(meta.Overlays!);
            Assert.Equal(OverlayLayer.Gear, ov.Layer);              // skin cannot emit
            Assert.Equal("character.shpk", ov.ShaderPackage);       // the shader that takes a base texture
            Assert.Null(ov.Scroll);
            Assert.False(Directory.Exists(Path.Combine(root, "Proteus", "Effects")));

            var row = Assert.Single(meta.ColorTableRows!);
            Assert.Equal(ModCreationService.GlowRow, row.Row);      // the only cell a fabricated index reads
            Assert.Equal("#FFFFFF", row.SubRowA!.Diffuse);          // don't tint the art
            Assert.Equal(ModCreationService.AlwaysGlowEmissive, row.SubRowA.Emissive);
            Assert.Equal("#C86432", row.SubRowA.EmissiveColor);     // the art's own colour, not white
            Assert.Null(row.SubRowA.LightResponse);
            Assert.False(row.SubRowA.HideInLight);
        }, artColour: "#C86432");
    }

    [Fact]
    public void WriteMod_dark_only_writes_the_scroll_map_and_hides_in_light()
    {
        var scroll = new byte[2 * 2 * 4];
        WithMod(GlowStyle.DarkOnly, scroll, 2, 2, (root, meta) =>
        {
            var ov = Assert.Single(meta.Overlays!);
            Assert.Equal(OverlayLayer.Gear, ov.Layer);
            Assert.Equal("characterscroll.shpk", ov.ShaderPackage);
            // A BARE file name: ResolveEffectPath looks it up in Proteus/Effects and the shared library,
            // unlike the overlay slots, which are sidecar-relative.
            Assert.Equal("glow.png", ov.Scroll);
            Assert.True(File.Exists(Path.Combine(root, "Proteus", "Effects", "glow.png")));
            // Explicitly zero and one: an unset speed takes the writer's default and slides the tattoo
            // across the skin it is drawn on.
            Assert.Equal(0f, ov.ScrollSpeedX);
            Assert.Equal(1f, ov.ScrollTilingX);

            var row = Assert.Single(meta.ColorTableRows!);
            Assert.Equal("#000000", row.SubRowA!.Diffuse);   // characterscroll has no base texture
            Assert.Equal(1f, row.SubRowA.LightResponse);
            Assert.True(row.SubRowA.HideInLight);
        });
    }

    /// <summary>
    /// The default path must stay exactly what it was: Skin, no shader, no rows. Every glow field is
    /// opt-in, and a mod created without ticking the box is not allowed to gain one.
    /// </summary>
    [Fact]
    public void WriteMod_without_glow_is_unchanged()
    {
        WithMod(GlowStyle.None, null, 0, 0, (root, meta) =>
        {
            var ov = Assert.Single(meta.Overlays!);
            Assert.Equal(OverlayLayer.Skin, ov.Layer);
            Assert.Null(ov.Shader);
            Assert.Null(ov.Scroll);
            Assert.Null(ov.ScrollSpeedX);
            Assert.Null(meta.ColorTableRows);
            Assert.False(Directory.Exists(Path.Combine(root, "Proteus", "Effects")));
        });
    }

    /// <summary>A dark-only style with no scroll buffer leaves the effect unwritten rather than throwing —
    /// WriteMod decodes nothing itself, so a caller that couldn't build one must not crash it.</summary>
    [Fact]
    public void WriteMod_dark_only_without_a_scroll_map_degrades()
    {
        WithMod(GlowStyle.DarkOnly, null, 0, 0, (root, meta) =>
        {
            var ov = Assert.Single(meta.Overlays!);
            Assert.Null(ov.Scroll);
            Assert.Null(ov.ScrollSpeedX);
            Assert.False(Directory.Exists(Path.Combine(root, "Proteus", "Effects")));
        });
    }

    /// <summary>
    /// The scroll map IS the light on characterscroll, so it has to be the art's colour scaled by its own
    /// coverage, on black, and fully opaque — a transparent scroll map renders nothing at all.
    /// </summary>
    /// <summary>
    /// The always-glow emissive is the art's own average, never white. The emissive on character.shpk is
    /// one flat colour for the whole region, so it cannot follow the picture — but white adds grey to every
    /// hue at once, which is what bleached a pale watercolour to near-white in game.
    /// </summary>
    [Fact]
    public void AverageArtColour_weights_by_coverage()
    {
        // Two opaque texels of one colour, one transparent texel of another. The transparent one is the
        // background the art sits on and must not pull the answer toward it.
        var art = new byte[]
        {
            200, 100,  50, 255,
            200, 100,  50, 255,
              0, 255,   0,   0,
        };

        Assert.Equal("#C86432", ModCreationService.AverageArtColour(art));
    }

    [Fact]
    public void AverageArtColour_is_null_when_nothing_is_covered()
    {
        // An all-transparent sheet has no colour to emit in. Unweighted this would average to black and
        // emit nothing; null lets the caller fall back to white instead.
        Assert.Null(ModCreationService.AverageArtColour(new byte[] { 200, 100, 50, 0 }));
    }

    [Fact]
    public void BuildScrollMap_lays_the_art_on_black_and_makes_it_opaque()
    {
        var art = new byte[]
        {
            200, 100,  50, 255,   // fully covered — keeps its colour
            200, 100,  50, 128,   // half covered — half as bright
            200, 100,  50,   0,   // transparent — contributes nothing
        };

        var scroll = ModCreationService.BuildScrollMap(art);

        Assert.Equal(new byte[] { 200, 100, 50, 255 }, scroll[0..4]);
        Assert.Equal(new byte[] { 100,  50, 25, 255 }, scroll[4..8]);
        Assert.Equal(new byte[] {   0,   0,  0, 255 }, scroll[8..12]);
    }

    [Fact]
    public void WriteMod_produces_a_valid_proteus_sidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus_create_" + Path.GetRandomFileName());
        var diffuse = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        File.WriteAllBytes(diffuse, new byte[] { 1, 2, 3, 4 });   // stand-in image bytes

        try
        {
            ModCreationService.WriteMod(
                root, "My Tattoo", "Artist",
                "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
                diffuseSrc: diffuse, maskSrc: null, normalSrc: null, indexSrc: null);

            // Penumbra manifest + default option exist. A new mod is written in the pre-v4 layout on
            // purpose: every Penumbra reads it, and a newer one migrates it into meta.json on load.
            Assert.True(File.Exists(Path.Combine(root, "meta.json")));
            Assert.True(File.Exists(Path.Combine(root, "default_mod.json")));

            // The picked texture was copied into the sidecar, keeping its extension.
            Assert.True(File.Exists(Path.Combine(root, "Proteus", "overlays", "diffuse.png")));

            // metadata.json round-trips into the model with exactly one Skin overlay.
            var metaPath = Path.Combine(root, "Proteus", "metadata.json");
            Assert.True(File.Exists(metaPath));
            var meta = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(metaPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(meta);
            Assert.Equal("My Tattoo", meta!.Name);
            Assert.Equal("Artist", meta.Author);
            Assert.NotNull(meta.Overlays);
            var ov = Assert.Single(meta.Overlays!);
            Assert.Equal(OverlayLayer.Skin, ov.Layer);
            Assert.Equal("overlays/diffuse.png", ov.Diffuse);
            Assert.Null(ov.Mask);
            Assert.Null(ov.Normal);
            Assert.Equal(
                "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
                Assert.Single(ov.MaterialGamePaths));

            var pmeta = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json")));
            Assert.Equal(3, pmeta.RootElement.GetProperty("FileVersion").GetInt32());
            Assert.Equal("My Tattoo", pmeta.RootElement.GetProperty("Name").GetString());

            // default_mod.json: no file redirects, but a dummy self-swap of the target material so Penumbra
            // doesn't flag the mod as changing nothing.
            var def = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "default_mod.json"))).RootElement;
            Assert.Equal(JsonValueKind.Object, def.GetProperty("Files").ValueKind);
            Assert.Empty(def.GetProperty("Files").EnumerateObject());
            Assert.Equal(JsonValueKind.Array, def.GetProperty("Manipulations").ValueKind);
            // Dummy self-swap on a harmless vanilla monster path (not the target body material).
            var swaps = def.GetProperty("Swaps");
            var swap = Assert.Single(swaps.EnumerateObject());
            Assert.Equal(
                "chara/monster/m8030/obj/body/b0001/material/v0001/mt_m8030b0001_a.mtrl", swap.Name);
            Assert.Equal(swap.Name, swap.Value.GetString());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { File.Delete(diffuse); } catch { }
        }
    }

    /// <summary>
    /// The whole-skin tick has to reach the sidecar, because nothing downstream can infer it: a converted
    /// skin's normal is indistinguishable from a detail overlay's until someone says which it is, and
    /// guessing wrong doubles the relief (see <see cref="NormalMode.Replace"/>).
    /// <para/>
    /// It sets TWO things. Skin-tint suppression keeps fabric at its authored colour on any wearer, which is
    /// exactly backwards for art that is itself the skin — that has to take the wearer's tone, the way their
    /// face already does — so the tick turns it off as well.
    /// </summary>
    [Theory]
    [InlineData(false, NormalMode.Compound, null)]
    [InlineData(true,  NormalMode.Replace,  0f)]
    public void WriteMod_records_the_normal_blend(bool wholeSkin, NormalMode expected, float? expectedTint)
    {
        var root   = Path.Combine(Path.GetTempPath(), "proteus_create_" + Path.GetRandomFileName());
        var normal = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        File.WriteAllBytes(normal, new byte[] { 1, 2, 3, 4 });

        try
        {
            ModCreationService.WriteMod(
                root, "Whole Skin", "Artist",
                "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
                diffuseSrc: null, maskSrc: null, normalSrc: normal, indexSrc: null,
                wholeSkin: wholeSkin);

            var meta = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(Path.Combine(root, "Proteus", "metadata.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var ov = Assert.Single(meta!.Overlays!);
            Assert.Equal(expected, ov.NormalMode);
            // null, not 1: the default stays OMITTED from an ordinary sidecar, which is what keeps
            // "absent = full masking" true and the file free of no-op lines.
            Assert.Equal(expectedTint, ov.SkinToneMask);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { File.Delete(normal); } catch { }
        }
    }

    /// <summary>
    /// A new mod NEVER claims its art is one-sided. That has to be the author's statement, and nothing here
    /// can stand in for it: a real skin texture is never symmetric — freckles, moles — so a measurement says
    /// "asymmetric" about ordinary skin and moves it onto a shell, where it loses the wearer's tone and
    /// renders grey and glossy. Proteus used to probe the art and write the answer here, and that is exactly
    /// what it did to every skin mod on the machine.
    /// <para/>
    /// The field must be OMITTED, not written false, so the sidecar carries no claim either way.
    /// </summary>
    [Fact]
    public void WriteMod_never_declares_the_art_one_sided()
    {
        var root = Path.Combine(Path.GetTempPath(), "proteus_create_" + Path.GetRandomFileName());
        var diffuse = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        File.WriteAllBytes(diffuse, new byte[] { 1, 2, 3, 4 });

        try
        {
            ModCreationService.WriteMod(
                root, "Asym", "Artist",
                "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
                diffuseSrc: diffuse, maskSrc: null, normalSrc: null, indexSrc: null);

            var path = Path.Combine(root, "Proteus", "metadata.json");
            Assert.DoesNotContain("AsymmetricArt", File.ReadAllText(path));

            var meta = JsonSerializer.Deserialize<ProteusMetadata>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Null(Assert.Single(meta!.Overlays!).AsymmetricArt);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { File.Delete(diffuse); } catch { }
        }
    }

    /// <summary>
    /// The Create tab's auto-detect, first gate. Measured against the real art: a converted skin's base
    /// bottoms out at alpha 251, while a detail overlay ("Boney Diffuse" — collarbones and ribs) peaks at
    /// 117 over a mean of 6. This only rules sparse art OUT; garment art is routinely opaque end to end and
    /// is separated by the likeness test below instead.
    /// </summary>
    [Theory]
    [InlineData(255, 1.00, true,  "a converted skin's normal — flat opaque")]
    [InlineData(251, 1.00, true,  "a converted skin's base — a lossy step short of 255")]
    [InlineData(255, 0.995, true, "opaque but for a feathered UV island edge")]
    [InlineData(255, 0.95, false, "a garment with real gaps")]
    [InlineData(255, 0.02, false, "a tattoo")]
    [InlineData(117, 1.00, false, "a soft shading overlay, opaque nowhere")]
    public void IsFullCoverage_separates_a_skin_from_an_overlay(
        int alpha, double opaqueFraction, bool expected, string what)
    {
        const int texels = 256 * 256;
        var rgba = new byte[texels * 4];
        int opaque = (int)(texels * opaqueFraction);
        for (int i = 0; i < texels; i++)
            rgba[i * 4 + 3] = i < opaque ? (byte)alpha : (byte)0;

        Assert.True(ModCreationService.IsFullCoverage(rgba, texels) == expected, what);
    }

    /// <summary>A failed decode must read as "not a whole skin" — the safe answer, since compounding a
    /// detail overlay is right while replacing a skin's normal by mistake is not.</summary>
    [Fact]
    public void IsFullCoverage_treats_an_unreadable_buffer_as_not_covered()
    {
        Assert.False(ModCreationService.IsFullCoverage(null, 256 * 256));
        Assert.False(ModCreationService.IsFullCoverage(new byte[16], 256 * 256));   // short
    }

    /// <summary>
    /// The gate that actually separates a converted skin from garment art, since both are opaque. Scored
    /// over 238 real overlays: bibo skins landed 0.98–21.34 against another skin, and the nearest garment
    /// — a full-coverage tartan bodysuit — 34.42. These two stand in for either side of the threshold, so
    /// moving it far enough to swallow a garment or drop a skin fails here.
    /// </summary>
    [Theory]
    [InlineData(21, true,  "another artist's skin — a different tone, same flesh")]
    [InlineData(35, false, "a full-coverage bodysuit, the closest garment measured")]
    public void MeanAbsDifference_scores_a_uniform_offset(int offset, bool underThreshold, string what)
    {
        const int texels = 256 * 256;
        var skin    = new byte[texels * 4];
        var overlay = new byte[texels * 4];
        for (int i = 0; i < texels * 4; i += 4)
        {
            skin[i] = skin[i + 1] = skin[i + 2] = 128;
            overlay[i] = overlay[i + 1] = overlay[i + 2] = (byte)(128 + offset);
            skin[i + 3] = overlay[i + 3] = 255;
        }

        var mad = ModCreationService.MeanAbsDifference(overlay, skin, texels);
        Assert.Equal(offset, mad, 3);
        // Against the shipped constant, not a copy of its value: moving SkinLikenessMad far enough to
        // swallow a garment or drop a skin has to fail here, which a duplicated literal would not catch.
        Assert.True((mad <= ModCreationService.SkinLikenessMad) == underThreshold, what);
    }

    /// <summary>Alpha is not part of the comparison: a whole skin and the base under it can differ by a few
    /// counts of alpha (251 against 255) and still be the same picture.</summary>
    [Fact]
    public void MeanAbsDifference_ignores_alpha()
    {
        const int texels = 4;
        var a = new byte[texels * 4];
        var b = new byte[texels * 4];
        for (int i = 3; i < texels * 4; i += 4) { a[i] = 251; b[i] = 255; }
        Assert.Equal(0f, ModCreationService.MeanAbsDifference(a, b, texels));
    }

    /// <summary>An unreadable side is "as unlike as possible", never a match — the same fail-safe direction
    /// as the coverage gate.</summary>
    [Fact]
    public void MeanAbsDifference_treats_a_missing_side_as_maximally_different()
    {
        Assert.Equal(float.MaxValue, ModCreationService.MeanAbsDifference(null, new byte[64], 4));
        Assert.Equal(float.MaxValue, ModCreationService.MeanAbsDifference(new byte[64], new byte[8], 4));
    }

    [Theory]
    [InlineData("My Cool Mod", "My Cool Mod")]
    [InlineData("  spaced  out  ", "spaced out")]
    [InlineData("bad/slash:name*", "badslashname")]
    [InlineData("émigré-2", "émigré-2")]
    public void Sanitize_keeps_safe_characters(string input, string expected)
        => Assert.Equal(expected, ModCreationService.Sanitize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void Sanitize_rejects_empty_or_all_stripped(string input)
        => Assert.Null(ModCreationService.Sanitize(input));
}
