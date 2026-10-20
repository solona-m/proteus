using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Who is a man, and which of his materials are drawn in TBSE's layout or the men's vanilla one.</summary>
public class MensLayoutTests
{
    private const string TbseSkin = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl";
    private const string MaleVanillaSkin = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl";

    private static string Skin(string race, string suffix) => $"chara/human/c{race}/obj/body/b0001/material/v0001/mt_c{race}b0001{suffix}.mtrl";

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Tbse_body_material_is_tbse_for_a_man() // [AC-tbse-uvmap-3]
    {
        Assert.Equal(UVRemapService.TbseSpace, UVRemapService.LayoutOfMaterial(TbseSkin, wearerIsMan: true));
        Assert.Null(UVRemapService.LayoutOfMaterial(TbseSkin, wearerIsMan: false));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-4")]
    public void Male_vanilla_material_is_male_vanilla() // [AC-tbse-uvmap-4]
        => Assert.Equal(UVRemapService.MaleVanillaSpace, UVRemapService.LayoutOfMaterial(MaleVanillaSkin, wearerIsMan: true));

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-6")]
    public void Womens_destinations_never_get_a_route() // [AC-tbse-uvmap-6]
    {
        // Every women's race and every skin suffix: no men's layout, so no route — whatever the art.
        foreach (var race in new[] { "0201", "0401", "0601", "0801", "1001", "1401", "1601", "1801" })
            foreach (var suffix in new[] { "_a", "_b", "_bibo", "_eve" })
            {
                var path = Skin(race, suffix);
                var layout = UVRemapService.LayoutOfMaterial(path, wearerIsMan: true);
                Assert.Null(layout);
                Assert.Null(UVRemapService.MensRouteFor("bibo", "bibo", [path], layout));
            }
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-6")]
    [Trait("Plan", "EDGE-tbse-uvmap-5")]
    public void Woman_drawing_c0101_skin_is_not_a_man() // [AC-tbse-uvmap-6] [EDGE-tbse-uvmap-5]
    {
        // c0101-only boots on a woman: her body codes are both hers and the fallback's.
        Assert.False(UVRemapService.WearerIsMan(null, ["c0201", "c0101"], ["c0201"]));
        Assert.Null(UVRemapService.LayoutOfMaterial(TbseSkin, UVRemapService.WearerIsMan(null, ["c0201", "c0101"], ["c0201"])));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-6")]
    public void LayoutOfMaterial_null_for_women_so_dstBodyType_is_InferBodyType() // [AC-tbse-uvmap-6]
    {
        // The composite keeps dstBodyType = InferBodyType for everyone; dstLayout is the only new reading, and it is null
        // for a woman, so nothing she draws can take the men's branch.
        Assert.Equal("gen3", UVRemapService.InferBodyType(TbseSkin));
        Assert.Null(UVRemapService.LayoutOfMaterial(Skin("0201", "_b"), wearerIsMan: false));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-7")]
    public void Men_route_off_without_v2_files() // [AC-tbse-uvmap-7]
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proteus-men-maps-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "uvmaps"));
        try
        {
            var uv = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), dir);
            Assert.False(uv.MenMapsPresent());
            Assert.False(uv.MensRouteAvailable(new MensRoute("bibo", UVRemapService.TbseSpace)));
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-1")]
    public void Lalafell_and_male_hrothgar_unchanged() // [EDGE-tbse-uvmap-1]
    {
        foreach (var race in new[] { "1101", "1201", "1501" })
        {
            Assert.False(UVRemapService.WearerIsMan($"c{race}", null, null));
            Assert.Null(UVRemapService.LayoutOfMaterial(Skin(race, "_b"), wearerIsMan: true));
            Assert.Null(UVRemapService.LayoutOfMaterial(Skin(race, "_a"), wearerIsMan: true));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-2")]
    public void Womens_races_unchanged() // [EDGE-tbse-uvmap-2]
    {
        foreach (var code in new[] { "c0201", "c0401", "c0601", "c0801", "c1001", "c1401", "c1601", "c1801" })
            Assert.False(UVRemapService.WearerIsMan(code, null, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-3")]
    public void Equipment_hosted_skin_unchanged() // [EDGE-tbse-uvmap-3]
        => Assert.Null(UVRemapService.LayoutOfMaterial("chara/equipment/e6255/material/v0001/mt_c0101e6255_top_b.mtrl", true));

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-4")]
    public void Bibo_or_eve_on_a_man_unchanged() // [EDGE-tbse-uvmap-4]
    {
        Assert.Null(UVRemapService.LayoutOfMaterial(Skin("0101", "_bibo"), true));
        Assert.Null(UVRemapService.LayoutOfMaterial(Skin("0101", "_eve"), true));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-6")]
    public void Womans_head_code_vetoes_all_c0101_body_codes() // [EDGE-tbse-uvmap-6]
    {
        Assert.False(UVRemapService.WearerIsMan(null, ["c0101"], ["c0201"]));
        Assert.True(UVRemapService.WearerIsMan(null, ["c0101"], ["c0101"]));
        Assert.True(UVRemapService.WearerIsMan(null, ["c0101"], []));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-6")]
    public void Empty_codes_are_not_a_man() // [EDGE-tbse-uvmap-6]
    {
        Assert.False(UVRemapService.WearerIsMan(null, null, null));
        Assert.False(UVRemapService.WearerIsMan(null, [], ["c0101"]));
        Assert.False(UVRemapService.WearerIsMan(null, ["not a code"], null));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-7")]
    public void Glamourer_code_decides_over_snapshot() // [EDGE-tbse-uvmap-7]
    {
        Assert.True(UVRemapService.WearerIsMan("c0101", ["c0201"], ["c0201"]));
        Assert.False(UVRemapService.WearerIsMan("c0201", ["c0101"], ["c0101"]));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-21")]
    public void Highlander_follows_step0_measurement() // [EDGE-tbse-uvmap-21]
    {
        // Step 0 measured TBSE's Highlander chest on the Midlander layout texel for texel (IoU 1.0): c0301 is TBSE's.
        Assert.Equal(UVRemapService.TbseSpace, UVRemapService.LayoutOfMaterial(Skin("0301", "_b"), true));
        Assert.True(UVRemapService.WearerIsMan(null, ["c0301"], ["c0301"]));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-21")]
    public void Highlander_mixed_codes_route_only_when_all_in_tbse_races() // [EDGE-tbse-uvmap-21]
    {
        // A Highlander in Midlander gear: every code is a men's TBSE race, so each body material is TBSE's.
        Assert.True(UVRemapService.WearerIsMan(null, ["c0301", "c0101"], ["c0301"]));
        Assert.Equal(UVRemapService.TbseSpace, UVRemapService.LayoutOfMaterial(Skin("0101", "_b"), true));
        Assert.Equal(UVRemapService.TbseSpace, UVRemapService.LayoutOfMaterial(Skin("0301", "_b"), true));
        // With a code outside the group (male Hrothgar's), nobody is a man here.
        Assert.False(UVRemapService.WearerIsMan(null, ["c0301", "c1501"], ["c0301"]));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Part_material_names_read_like_paths() // [AC-tbse-uvmap-3]
    {
        Assert.Equal(UVRemapService.TbseSpace, UVRemapService.LayoutOfPartMaterial("/mt_c0101b0001_b.mtrl", true));
        Assert.Equal(UVRemapService.MaleVanillaSpace, UVRemapService.LayoutOfPartMaterial("/mt_c0101b0001_a.mtrl", true));
        Assert.Null(UVRemapService.LayoutOfPartMaterial("/mt_c0201b0001_b.mtrl", true));
        Assert.Null(UVRemapService.LayoutOfPartMaterial("/mt_c0101e0000_dwn_b.mtrl", true));   // undies, not skin
        Assert.Null(UVRemapService.LayoutOfPartMaterial("/mt_c0101b0001_b.mtrl", false));
    }
}
