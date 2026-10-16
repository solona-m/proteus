using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="CharacterShaderUpgrade"/> against a REAL legacy material (<c>Fixtures/gear_top_a.mtrl</c>,
/// characterlegacy.shpk with a Dawntrail table and dye table, its rows zeroed): the rows and dye rows the test writes
/// into it are moved to the new shader's meaning, and nothing else the material had is lost.
/// </summary>
public class CharacterShaderUpgradeTests
{
    private const int RowBytes = 64;

    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gear_top_a.mtrl"));

    private static Half HalfAt(byte[] mtrl, int row, int field) =>
        BitConverter.ToHalf(mtrl, GearMaterialWriter.ColorTableStart(mtrl) + row * RowBytes + field * 2);

    private static void SetHalf(byte[] mtrl, int row, int field, float v) =>
        BitConverter.TryWriteBytes(
            mtrl.AsSpan(GearMaterialWriter.ColorTableStart(mtrl) + row * RowBytes + field * 2), (Half)v);

    private static int DyeAt(byte[] mtrl, int row) => GearMaterialWriter.ColorTableStart(mtrl) + 32 * RowBytes + row * 4;

    /// <summary>The material names character.shpk, carries the flow-map key, and keeps its textures and constants.</summary>
    [Fact]
    public void A_legacy_material_moves_to_the_character_shader()
    {
        var mtrl = Fixture();
        var up = CharacterShaderUpgrade.Upgrade(mtrl);
        Assert.NotNull(up);

        Assert.Equal("characterlegacy.shpk", CharacterShaderUpgrade.ShaderOf(mtrl));
        Assert.Equal("character.shpk", CharacterShaderUpgrade.ShaderOf(up!));
        Assert.Equal("character.shpk", TextureLoader.GetMtrlInfo(up!).shader);
        Assert.Equal(up!.Length, BitConverter.ToUInt16(up, 4));

        var before = TextureLoader.ParseMtrlBytes(mtrl);
        var after = TextureLoader.ParseMtrlBytes(up);
        Assert.Equal(before.Normal, after.Normal);
        Assert.Equal(before.Mask, after.Mask);
        Assert.Equal(before.Index, after.Index);
        Assert.True(after.HasColorTable);
        Assert.Equal(TextureLoader.GetMtrlInfo(mtrl).constIds, TextureLoader.GetMtrlInfo(up).constIds);

        // The flow-map key, added; the material's own keys, as they were.
        Assert.True(TextureLoader.PatchShaderKey(up, CharacterShaderUpgrade.FlowMapKey, 0).found);
        Assert.True(TextureLoader.PatchShaderKey(up, 0xF52CCF05, 0).found);
        Assert.True(TextureLoader.PatchShaderKey(up, 0xD2777173, 0).found);
    }

    /// <summary>
    /// A row: the legacy shader's shininess (field 3) and specular mask (field 7) become the new shader's 1 and 0, field
    /// 11 becomes 1, the shininess is carried into roughness, and a row with no sheen gets the game's default sheen.
    /// Colours are kept.
    /// </summary>
    [Fact]
    public void Rows_take_the_new_shaders_meaning()
    {
        var mtrl = Fixture();
        SetHalf(mtrl, 3, 0, 0.721f);
        SetHalf(mtrl, 3, 3, 20f);
        SetHalf(mtrl, 3, 4, 0.937f);
        SetHalf(mtrl, 3, 7, 1f);

        var up = CharacterShaderUpgrade.Upgrade(mtrl)!;

        Assert.Equal((Half)0.721f, HalfAt(up, 3, 0));
        Assert.Equal((Half)0.937f, HalfAt(up, 3, 4));
        Assert.Equal((Half)1f, HalfAt(up, 3, 3));
        Assert.Equal((Half)0f, HalfAt(up, 3, 7));
        Assert.Equal((Half)1f, HalfAt(up, 3, 11));
        Assert.Equal((Half)CharacterShaderUpgrade.RoughnessFromShininess(20f), HalfAt(up, 3, 16));
        Assert.Equal((Half)0.1f, HalfAt(up, 3, 12));
        Assert.Equal((Half)0.2f, HalfAt(up, 3, 13));
        Assert.Equal((Half)5f, HalfAt(up, 3, 14));
    }

    /// <summary>
    /// The reflection-mip match: shininess so high its old mip is 0 is perfectly smooth, and roughness rises as
    /// shininess falls.
    /// </summary>
    [Fact]
    public void Roughness_falls_as_shininess_rises()
    {
        Assert.Equal(0f, CharacterShaderUpgrade.RoughnessFromShininess(MathF.Pow(2f, 6.25f / 0.75f)), 1e-4f);
        float[] shininess = [2f, 8f, 20f, 64f, 255f];
        var rough = shininess.Select(CharacterShaderUpgrade.RoughnessFromShininess).ToArray();
        for (int i = 1; i < rough.Length; i++) Assert.True(rough[i] < rough[i - 1]);
        Assert.All(rough, r => Assert.InRange(r, 0f, 1f));
    }

    /// <summary>
    /// A dye row: the three-digit legacy template becomes its four-digit Dawntrail twin, the shininess flag the
    /// roughness one, and the specular mask flag is dropped; the colour flags and channel stay.
    /// </summary>
    [Fact]
    public void Dye_rows_take_the_dawntrail_templates()
    {
        var mtrl = Fixture();
        // Template 400, channel 1, diffuse + specular + shininess + specular mask.
        uint legacy = (400u << 16) | (1u << 27) | 0x01 | 0x02 | 0x08 | 0x10;
        BitConverter.TryWriteBytes(mtrl.AsSpan(DyeAt(mtrl, 15)), legacy);
        // Already four digits: left alone.
        BitConverter.TryWriteBytes(mtrl.AsSpan(DyeAt(mtrl, 16)), (1202u << 16) | 0x01);

        var up = CharacterShaderUpgrade.Upgrade(mtrl)!;
        uint v = BitConverter.ToUInt32(up, DyeAt(up, 15));
        Assert.Equal(1400u, (v >> 16) & 0x7FF);
        Assert.Equal(1u, (v >> 27) & 0x3);
        Assert.Equal(0x01u | 0x02 | 0x20, v & 0xFFF);
        Assert.Equal((1202u << 16) | 0x01, BitConverter.ToUInt32(up, DyeAt(up, 16)));
        Assert.Equal(0u, BitConverter.ToUInt32(up, DyeAt(up, 0)));
    }

    /// <summary>Only legacy materials are upgraded: one already on character.shpk is not touched again.</summary>
    [Fact]
    public void An_upgraded_material_is_not_upgraded_twice()
    {
        var up = CharacterShaderUpgrade.Upgrade(Fixture())!;
        Assert.Null(CharacterShaderUpgrade.Upgrade(up));
    }

    /// <summary>
    /// A legacy material whose table is not Dawntrail's 32×32 — what a pre-Dawntrail tool wrote — is refused rather
    /// than having its rows read in the wrong layout.
    /// </summary>
    [Fact]
    public void A_pre_dawntrail_table_is_refused()
    {
        var mtrl = Fixture();
        int flagsAt = GearMaterialWriter.ColorTableStart(mtrl) - mtrl[15];
        uint flags = BitConverter.ToUInt32(mtrl, flagsAt);
        BitConverter.TryWriteBytes(mtrl.AsSpan(flagsAt), flags & ~0xFF0u);   // no width or height: the old 16-row table
        Assert.Null(CharacterShaderUpgrade.Upgrade(mtrl));
    }
}
