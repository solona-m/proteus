using System;
using System.IO;
using System.Linq;
using Proteus.Services.Matcap;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

public class MatcapMaterialTests
{
    [Theory]
    [Trait("Plan", "AC-matcaps-4")]
    [InlineData(512)]
    [InlineData(2048)]
    public void Built_material_retargets_three_textures_rows_equal_row0(int table)
    {
        var template = SyntheticMatcapMod.Reflection(colorTableBytes: table);
        var built = MatcapMaterial.Build(template, "chara/e/n.tex", "chara/common/texture/proteus/matcap/abc/mask.tex",
                                         "chara/common/texture/proteus/matcap/abc/m.tex");
        var h = MatcapMaterial.ReadHeader(built)!;
        Assert.True(h.IsReflection);
        Assert.Equal("chara/e/n.tex", h.TextureOf(MatcapMaterial.SamplerNormal));
        Assert.Equal("chara/common/texture/proteus/matcap/abc/mask.tex", h.TextureOf(MatcapMaterial.SamplerMask));
        Assert.Equal("chara/common/texture/proteus/matcap/abc/m.tex", h.TextureOf(MatcapMaterial.SamplerMatcap));
        Assert.Equal(table, h.ColorTableBytes);
        // Constants verbatim.
        Assert.Equal(MatcapMaterial.ReadHeader(template)!.Constants.Keys.Order(), h.Constants.Keys.Order());
        Assert.Equal(0.5f, h.AlphaThreshold);

        // Every row equals row 0.
        int rowBytes = table >= 2048 ? 64 : 32, rows = table >= 2048 ? 32 : 16;
        int at = 16 + built[12] * 4 + built[13] * 4 + built[14] * 4 + BitConverter.ToUInt16(built, 8) + built[15];
        var row0 = built.AsSpan(at, rowBytes).ToArray();
        for (int r = 1; r < rows; r++) Assert.Equal(row0, built.AsSpan(at + r * rowBytes, rowBytes).ToArray());
        Assert.Equal(built.Length, BitConverter.ToUInt16(built, 4));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-4")]
    public void Retargets_by_index_even_when_the_template_repeats_a_path()
    {
        var template = SyntheticMatcapMod.Reflection(normal: "chara/x/same.tex", mask: "chara/x/same.tex");
        var built = MatcapMaterial.Build(template, "n.tex", "m.tex", "c.tex");
        var h = MatcapMaterial.ReadHeader(built)!;
        Assert.Equal("n.tex", h.TextureOf(MatcapMaterial.SamplerNormal));
        Assert.Equal("m.tex", h.TextureOf(MatcapMaterial.SamplerMask));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-48")]
    public void Source_alpha_threshold_carried()
    {
        var built = MatcapMaterial.Build(SyntheticMatcapMod.Reflection(), "n.tex", "m.tex", "c.tex", alphaThreshold: 0.25f);
        Assert.Equal(0.25f, MatcapMaterial.ReadHeader(built)!.AlphaThreshold);
        var gear = MatcapMaterial.ReadHeader(SyntheticMatcapMod.Gear("chara/e/n.tex", alphaThreshold: 0.3f))!;
        Assert.Equal(0.3f, gear.AlphaThreshold);
        Assert.Null(MatcapMaterial.ReadHeader(SyntheticMatcapMod.Gear("chara/e/n.tex"))!.AlphaThreshold);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-4")]
    public void A_non_reflection_template_is_refused()
    {
        Assert.Throws<InvalidDataException>(() =>
            MatcapMaterial.Build(SyntheticMatcapMod.Gear("n.tex"), "n.tex", "m.tex", "c.tex"));
        Assert.Null(MatcapMaterial.ReadHeader(new byte[8]));
    }
}

/// <summary>
/// Gate G1: the game's own reflection material — the template — read from game data, compared with the MatCap Library's
/// material where that pack is on the machine. Owner's machine only.
/// </summary>
public class MatcapMaterialLocalTests(ITestOutputHelper output)
{
    private static byte[] Template()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return data.GetFile(MatcapMaterial.VanillaTemplatePath)?.Data
               ?? throw new FileNotFoundException(MatcapMaterial.VanillaTemplatePath);
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-4")]
    public void Vanilla_template_is_reflection_with_three_samplers_and_a_colour_table()
    {
        var h = MatcapMaterial.ReadHeader(Template())!;
        Assert.True(h.IsReflection, h.Shader);
        Assert.NotNull(h.TextureOf(MatcapMaterial.SamplerNormal));
        Assert.NotNull(h.TextureOf(MatcapMaterial.SamplerMask));
        Assert.NotNull(h.TextureOf(MatcapMaterial.SamplerMatcap));
        Assert.True(h.ColorTableBytes >= 512, $"colour table {h.ColorTableBytes} B");
        output.WriteLine($"colour table {h.ColorTableBytes} B; constants {string.Join(", ", h.Constants.Select(c => $"{c.Key:X8}={string.Join("/", c.Value)}"))}");
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-4")]
    public void Template_texture_paths_distinct_or_index_retarget()
    {
        var h = MatcapMaterial.ReadHeader(Template())!;
        var three = new[] { MatcapMaterial.SamplerNormal, MatcapMaterial.SamplerMask, MatcapMaterial.SamplerMatcap }
            .Select(h.TextureOf).ToList();
        output.WriteLine(string.Join(" | ", three));
        // Distinct here; MatcapMaterial retargets by index regardless, so a repeat would still build correctly.
        Assert.Equal(3, three.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var built = MatcapMaterial.Build(Template(), "a.tex", "b.tex", "c.tex");
        Assert.Equal("c.tex", MatcapMaterial.ReadHeader(built)!.TextureOf(MatcapMaterial.SamplerMatcap));
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-matcaps-48")]
    public void Template_alpha_threshold_logged()
    {
        var h = MatcapMaterial.ReadHeader(Template())!;
        output.WriteLine($"g_AlphaThreshold = {h.AlphaThreshold}");
        Assert.NotNull(h.AlphaThreshold);
    }

    [LocalDataFact(LocalData.GameData, LocalData.MatcapLibraryPack)]
    [Trait("Plan", "AC-matcaps-4")]
    public void Template_compared_with_the_library_material()
    {
        var lib = MatcapMaterial.ReadHeader(LocalData.PackEntryEndingWith(LocalData.MatcapLibraryPack,
            "material for slot a/chara/monster/m8058/obj/body/b0001/material/v0001/mt_m8058b0001_b.mtrl"))!;
        var game = MatcapMaterial.ReadHeader(Template())!;
        Assert.Equal(lib.Shader, game.Shader);
        Assert.Equal(lib.Samplers.Select(s => s.Id).Order(), game.Samplers.Select(s => s.Id).Order());
        Assert.Equal(lib.AlphaThreshold, game.AlphaThreshold);
        output.WriteLine($"library only: {string.Join(", ", lib.Constants.Keys.Except(game.Constants.Keys).Select(k => k.ToString("X8")))}");
        output.WriteLine($"game only: {string.Join(", ", game.Constants.Keys.Except(lib.Constants.Keys).Select(k => k.ToString("X8")))}");
        foreach (var k in lib.Constants.Keys.Intersect(game.Constants.Keys))
            if (!lib.Constants[k].SequenceEqual(game.Constants[k]))
                output.WriteLine($"differs {k:X8}: library {string.Join("/", lib.Constants[k])} game {string.Join("/", game.Constants[k])}");
        output.WriteLine($"colour table: library {lib.ColorTableBytes} B, game {game.ColorTableBytes} B");
    }
}
