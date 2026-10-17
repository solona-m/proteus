using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Which material file the game loads for a hair model's material, by race and hairstyle — Penumbra's rule.</summary>
public class HairMaterialPathTests
{
    [Theory]
    [Trait("Plan", "AC-hair-race-port-3")]
    // Below 101 and above 200 every race reads its own.
    [InlineData((ushort)801, 5, (ushort)801)]
    [InlineData((ushort)201, 230, (ushort)201)]
    // 101–115: the Miqo'te keep theirs; everyone else of that sex reads Midlander's.
    [InlineData((ushort)801, 105, (ushort)801)]
    [InlineData((ushort)701, 115, (ushort)701)]
    [InlineData((ushort)1401, 105, (ushort)201)]
    [InlineData((ushort)301, 101, (ushort)101)]
    // 116–200: shared by all but Hrothgar.
    [InlineData((ushort)801, 130, (ushort)201)]
    [InlineData((ushort)1101, 200, (ushort)101)]
    [InlineData((ushort)1601, 130, (ushort)1601)]
    [InlineData((ushort)1501, 105, (ushort)1501)]
    public void Matches_penumbras_rule(ushort wearer, int id, ushort expected)
        => Assert.Equal(expected, HairMaterialPath.Race(wearer, id));

    [Fact]
    [Trait("Plan", "AC-hair-race-port-3")]
    public void Game_path_uses_the_material_race_and_keeps_the_rest_of_the_name()
    {
        Assert.Equal("chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_a.mtrl",
                     HairMaterialPath.GamePath(801, 130, "/mt_c0801h0130_hir_a.mtrl"));
        // The model's own race: the name is used as it stands.
        Assert.Equal("chara/human/c0801/obj/hair/h0005/material/v0001/mt_c0801h0005_hir_a.mtrl",
                     HairMaterialPath.GamePath(801, 5, "/mt_c0801h0005_hir_a.mtrl"));
        Assert.Null(HairMaterialPath.GamePath(801, 5, "chara/common/texture/whatever.mtrl"));
    }

    [Theory]
    [Trait("Plan", "EDGE-hair-race-port-25")]
    [InlineData(5)]
    [InlineData(105)]
    [InlineData(130)]
    public void Hrothgar_never_share(int id)
    {
        Assert.Equal((ushort)1601, HairMaterialPath.Race(1601, id));
        Assert.Equal((ushort)1501, HairMaterialPath.Race(1501, id));
    }

    [Fact]
    public void Rename_changes_only_a_short_names_race()
    {
        Assert.Equal("/mt_c0201h0130_hir_a.mtrl", HairMaterialPath.RenameRace("/mt_c0801h0130_hir_a.mtrl", 201));
        Assert.Equal("chara/x/mt_c0801.mtrl", HairMaterialPath.RenameRace("chara/x/mt_c0801.mtrl", 201));
    }
}
