using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The glass → character.shpk graft on real files: the game's template (<see cref="MatcapGlassCut.TemplatePath"/>), the
/// shader's key defaults the graft relies on, and the user's real glass material (the Enaretos bracelet's <c>_b</c>).
/// Read-only.
/// </summary>
public class MatcapGlassCutLocalTests
{
    private const string GlassRel = "enaretos bracelet/chara/accessory/a0153/material/v0001/mt_c0201a0153_wrs_b.mtrl";

    private static readonly Lazy<Lumina.GameData> Game = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    private static byte[] Template() => Game.Value.GetFile(MatcapGlassCut.TemplatePath)!.Data;

    private static byte[] RealGlass() =>
        File.ReadAllBytes(Path.Combine(LocalData.Path(LocalData.EnaretosMod), GlassRel.Replace('/', Path.DirectorySeparatorChar)));

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-row-matcap-glass-5")]
    public void Template_is_a_game_NMI_character_material()
    {
        var t = Template();
        var l = MatcapGlassCut.Walk(t)!;
        Assert.Equal(t.Length, l.End);
        Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(t));
        var h = MatcapMaterial.ReadHeader(t)!;
        Assert.Equal(new[] { MatcapMaterial.SamplerMask, MatcapMaterial.SamplerNormal, ShaderUpgradePlan.SamplerIndex }.Order(),
                     h.Samplers.Select(s => s.Id).Order());
        Assert.Equal(1, l.Uv);
        Assert.Equal(2048, l.DataSet);
        Assert.Equal(4, l.Add);
        Assert.Equal(0x534u, BitConverter.ToUInt32(t, l.AddAt));
        Assert.Equal(0x1u, l.Flags);
        Assert.Equal(13, l.ConstCount);
        Assert.Equal(0.5f, h.AlphaThreshold);
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-row-matcap-glass-5")]
    public void Character_shpk_key_defaults()
    {
        // ShPk stores each material key as an adjacent (id, default value) pair of u32s.
        var shpk = Game.Value.GetFile("shader/sm5/shpk/character.shpk")!.Data;
        bool Pair(uint id, uint value)
        {
            var want = BitConverter.GetBytes(id).Concat(BitConverter.GetBytes(value)).ToArray();
            return shpk.AsSpan().IndexOf(want) >= 0;
        }
        Assert.True(Pair(0xB616DC5A, 0x5CC605B5));   // why the template's texture-mode key can be dropped
        Assert.True(Pair(0xD2777173, 0x4242B842));   // likewise
        Assert.True(Pair(MatcapGlassCut.VertexColourKey, 0xDFE74BAC));       // why the vertex-colour key is written
        Assert.False(Pair(MatcapGlassCut.VertexColourKey, MatcapGlassCut.VertexColourMask));
    }

    [LocalDataFact(LocalData.EnaretosMod, LocalData.GameData)]
    [Trait("Plan", "AC-row-matcap-glass-1")]
    [Trait("Plan", "EDGE-row-matcap-glass-8")]
    [Trait("Plan", "EDGE-row-matcap-glass-9")]
    [Trait("Plan", "EDGE-row-matcap-glass-10")]
    public void Real_b_converts_and_parses()
    {
        var glass = RealGlass();
        var template = Template();
        var (bytes, why) = MatcapGlassCut.Convert(glass, template);
        Assert.Equal(MatcapGlassCut.Failure.None, why);
        Assert.True(MatcapGlassCut.IsSound(glass, bytes!, template));
        // Lumina loads it too (it reads no Dawntrail keys or samplers, but it walks the file).
        Assert.NotNull(TextureLoader.LoadLuminaFileFromBytes<Lumina.Data.Files.MtrlFile>(bytes!));

        var g = MatcapGlassCut.Walk(glass)!;
        var c = MatcapGlassCut.Walk(bytes!)!;
        Assert.Equal(glass.AsSpan(g.SampsAt, 12 * g.SampCount).ToArray(), bytes.AsSpan(c.SampsAt, 12 * c.SampCount).ToArray());
        Assert.Equal(0xF8000u, BitConverter.ToUInt32(bytes!, c.SampsAt + 4 + 12 * Enumerable.Range(0, c.SampCount)
            .First(i => BitConverter.ToUInt32(bytes!, c.SampsAt + 12 * i) == MatcapMaterial.SamplerNormal)));
        Assert.Equal(2176, c.DataSet);
        Assert.Equal(glass.AsSpan(g.AddAt + 4, g.DataSet).ToArray(), bytes.AsSpan(c.AddAt + 4, c.DataSet).ToArray());
        Assert.Equal(0x53Cu, BitConverter.ToUInt32(bytes!, c.AddAt));
        Assert.Equal(0x1u, c.Flags);
        Assert.Equal(2, c.KeyCount);
        Assert.Equal(0f, TextureLoader.ConstantValue(bytes!, MeshVolumeService.VertexMovementScaleId));
        Assert.Equal(0f, TextureLoader.ConstantValue(bytes!, MeshVolumeService.VertexMovementMaxLengthId));
        Assert.Equal(0.5f, TextureLoader.ConstantValue(bytes!, MatcapMaterial.ConstAlphaThreshold));
    }
}
