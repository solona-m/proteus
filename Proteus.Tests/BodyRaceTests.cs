using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A refit never crosses sexes, and men's bodies have texture layouts of their own: a male body's <c>_b</c> skin is
/// The Body's layout, not the women's gen3, so two TBSE-family bodies pair and a man's body never pairs with a woman's.
/// </summary>
public class BodyRaceTests
{
    private static BodyOption Option(string name, string race, string slot = "_top")
        => new("Body", "", name, $"{name}/c{race}e0000{slot}.mdl",
               $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl", slot);

    private static ModelParts Body(string material, int triangles = 3, float offsetX = 0f)
        => ModelPartReader.Read(SyntheticModel.Build([],
            new SyntheticModel.Mesh(material, new SyntheticModel.Sub(0, TrianglesPerIsland: triangles,
                                                                     OffsetX: offsetX))))!;

    private static UVRemapService NoMaps()
        => new(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");

    [Theory]
    [InlineData("0101", true)]    // Midlander man
    [InlineData("0104", true)]    // Midlander man, NPC variant
    [InlineData("0201", false)]   // Midlander woman
    [InlineData("0804", false)]   // Miqo'te woman, NPC variant
    [InlineData("1301", true)]    // Au Ra man
    [InlineData("1701", true)]    // Viera man
    [InlineData("1801", false)]   // Viera woman
    public void Race_codes_pair_male_then_female(string race, bool male)
        => Assert.Equal(male, BodySizeCatalog.IsMaleRace(race));

    [Fact]
    public void An_outfit_is_offered_its_own_race_s_bodies_when_the_mod_has_them()
    {
        // TBSE ships a Midlander and a Highlander chest.
        var catalog = new BodySizeCatalog("", [Option("mid", "0101"), Option("high", "0301")]);

        Assert.Equal(["mid"], catalog.For("_top", "0101").Select(o => o.Name));
        Assert.Equal(["high"], catalog.For("_top", "0301").Select(o => o.Name));
    }

    [Fact]
    public void Another_race_of_the_same_sex_falls_back_to_the_mod_s_bodies_of_that_sex()
    {
        var catalog = new BodySizeCatalog("", [Option("man", "0101"), Option("woman", "0201")]);

        // An Au Ra woman's outfit on a mod with only Midlander bodies: the woman's body, never the man's.
        Assert.Equal(["woman"], catalog.For("_top", "1401").Select(o => o.Name));
        Assert.Equal(["man"], catalog.For("_top", "1301").Select(o => o.Name));
    }

    [Fact]
    public void A_man_s_outfit_is_never_offered_a_woman_s_body()
    {
        var neolithe = new BodySizeCatalog("", [Option("xs", "0201"), Option("l", "0201")]);

        Assert.Empty(neolithe.For("_top", "0101"));
        Assert.Empty(neolithe.SlotsFor("0101"));
        Assert.Equal(["_top"], neolithe.SlotsFor("0201"));
    }

    [Theory]
    [InlineData("chara/equipment/e0141/model/c0201e0141_top.mdl", "0201")]
    [InlineData("chara/equipment/e0000/model/c0101e0000_top.mdl", "0101")]
    [InlineData("c1701e0000_dwn.mdl", "1701")]
    [InlineData("chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl", null)]
    public void The_race_is_read_off_an_equipment_path(string gamePath, string? race)
        => Assert.Equal(race, BodySizeCatalog.RaceOf(gamePath));

    [Fact]
    public void A_male_body_s_b_skin_is_the_tbse_layout_not_gen3()
    {
        Assert.Equal("tbse", BodyCorrespondence.LayoutOf(Body("/mt_c0101b0001_b.mtrl")));
        Assert.Equal("male vanilla", BodyCorrespondence.LayoutOf(Body("/mt_c0101b0001_a.mtrl")));
        Assert.Equal("gen3", BodyCorrespondence.LayoutOf(Body("/mt_c0201b0001_b.mtrl")));
        Assert.Equal(true, BodyCorrespondence.IsMale(Body("/mt_c0101b0001_b.mtrl")));
        Assert.Equal(false, BodyCorrespondence.IsMale(Body("/mt_c0201b0001_bibo.mtrl")));
    }

    [Fact]
    public void A_man_s_body_and_a_woman_s_are_never_paired()
    {
        var tbse = Body("/mt_c0101b0001_b.mtrl");
        var neolithe = Body("/mt_c0201b0001_bibo.mtrl");
        var uv = new float[tbse.Positions.Length / 3 * 2];

        Assert.False(BodyCorrespondence.TryBuild(tbse, uv, neolithe, uv, "chest", out _, out string refusal, NoMaps()));
        Assert.Contains("male body and the other a female one", refusal);
        Assert.False(BodyCorrespondence.TryBuild(neolithe, uv, tbse, uv, "chest", out _, out refusal, NoMaps()));
        Assert.Contains("male body and the other a female one", refusal);
    }

    [Fact]
    public void Two_men_s_bodies_in_the_tbse_layout_pair_without_a_map()
    {
        // TBSE and TBSE-X: different meshes, one layout. Neither the sex nor the layout stands between them.
        var tbse = Body("/mt_c0101b0001_b.mtrl", triangles: 3);
        var tbseX = Body("/mt_c0101b0001_b.mtrl", triangles: 4, offsetX: 0.01f);
        var sourceUv = new float[tbse.Positions.Length / 3 * 2];
        var targetUv = new float[tbseX.Positions.Length / 3 * 2];

        BodyCorrespondence.TryBuild(tbse, sourceUv, tbseX, targetUv, "chest", out _, out string refusal, NoMaps());
        Assert.DoesNotContain("texture layouts", refusal);
        Assert.DoesNotContain("female", refusal);
    }

    [Fact]
    public void Two_men_s_bodies_in_different_layouts_are_refused_by_layout()
    {
        var tbse = Body("/mt_c0101b0001_b.mtrl", triangles: 3);
        var vanilla = Body("/mt_c0101b0001_a.mtrl", triangles: 4, offsetX: 0.01f);
        var sourceUv = new float[tbse.Positions.Length / 3 * 2];
        var targetUv = new float[vanilla.Positions.Length / 3 * 2];

        Assert.False(BodyCorrespondence.TryBuild(tbse, sourceUv, vanilla, targetUv, "chest", out _, out string refusal,
                                                 NoMaps()));
        Assert.Contains("different texture layouts (tbse and male vanilla)", refusal);
    }
}
