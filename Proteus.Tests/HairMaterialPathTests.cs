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

    /// <summary>[EDGE-hair-material-scalp-fit-1] Where the target reads a folder of its own — below 101, Hrothgar, a
    /// Miqo'te's own 101–115 — or the port stays within a sex, the name is only the target's race code.</summary>
    [Theory]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-1")]
    [InlineData((ushort)201, (ushort)101, 5)]
    [InlineData((ushort)201, (ushort)1501, 130)]
    [InlineData((ushort)801, (ushort)701, 105)]
    [InlineData((ushort)701, (ushort)801, 105)]
    [InlineData((ushort)801, (ushort)201, 105)]
    public void PortName_keeps_the_name_where_the_target_reads_its_own_folder(ushort from, ushort to, int id)
    {
        var name = $"/mt_c{from:D4}h{id:D4}_hir_a.mtrl";
        Assert.Equal(HairMaterialPath.RenameRace(name, to), HairMaterialPath.PortName(name, from, to, id));
    }

    /// <summary>[EDGE-hair-material-scalp-fit-2] A women's hairstyle 168 for a Viera or Elezen man — who read the Midlander
    /// man's folder — gets the port's own name; a Hrothgar man, who reads his own, does not need one.</summary>
    [Theory]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-2")]
    [InlineData((ushort)1701)]
    [InlineData((ushort)501)]
    public void PortName_gives_viera_and_elezen_men_their_own(ushort to)
    {
        Assert.Equal($"/mt_c{to:D4}h0168_hir_b_port{to:D4}.mtrl", HairMaterialPath.PortName("/mt_c0201h0168_hir_b.mtrl", 201, to, 168));
        Assert.True(HairMaterialPath.IsShared(to, 168));
        Assert.False(HairMaterialPath.IsShared(1501, 168));
        Assert.Equal("chara/human/c0101/obj/hair/h0168/material/v0001/mt_c0101h0168_hir_b_port" + $"{to:D4}.mtrl",
                     HairMaterialPath.GamePath(to, 168, $"/mt_c{to:D4}h0168_hir_b_port{to:D4}.mtrl"));
        Assert.Equal(to, HairMaterialPath.OwnRace($"/mt_c{to:D4}h0168_hir_b_port{to:D4}.mtrl"));
    }

    /// <summary>[EDGE-hair-material-scalp-fit-4] The own name is made once: given it again it is unchanged, and a name that
    /// is not short (an absolute path) is left alone.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-4")]
    public void OwnName_is_idempotent_and_ignores_long_names()
    {
        var once = HairMaterialPath.OwnName("/mt_c0201h0168_hir_b.mtrl", 101);
        Assert.Equal("/mt_c0101h0168_hir_b_port0101.mtrl", once);
        Assert.Equal(once, HairMaterialPath.OwnName(once, 101));
        Assert.Equal("chara/x/mt_c0201h0168_hir_b.mtrl", HairMaterialPath.OwnName("chara/x/mt_c0201h0168_hir_b.mtrl", 101));
        Assert.True(HairMaterialPath.IsOwnName(once));
        Assert.False(HairMaterialPath.IsOwnName("/mt_c0101h0168_hir_b.mtrl"));
        Assert.False(HairMaterialPath.ReadBy(101, 168, HairMaterialPath.GamePath(101, 168, once)!));
        Assert.True(HairMaterialPath.ReadBy(301, 168, "chara/human/c0101/obj/hair/h0168/material/v0001/mt_c0101h0168_hir_b.mtrl"));
    }
}
