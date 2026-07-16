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

            // Penumbra manifest + default option exist.
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

            // default_mod.json: no file redirects, but a dummy self-swap of the target material so Penumbra
            // doesn't flag the mod as changing nothing.
            var def = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "default_mod.json")));
            Assert.Equal(JsonValueKind.Object, def.RootElement.GetProperty("Files").ValueKind);
            Assert.Empty(def.RootElement.GetProperty("Files").EnumerateObject());
            Assert.Equal(JsonValueKind.Array, def.RootElement.GetProperty("Manipulations").ValueKind);
            // Dummy self-swap on a harmless vanilla monster path (not the target body material).
            var swaps = def.RootElement.GetProperty("Swaps");
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
