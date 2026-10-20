using System.Collections.Generic;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>A man's second-skin shell: a woman's layer carried into his layout by its art, his own layers untouched.</summary>
public class ShellMensLayoutTests
{
    private static OverlayDescriptor Art(string? declared, bool asym = false, params string[] paths)
        => new() { SourceBodyType = declared, AsymmetricArt = asym, MaterialGamePaths = [.. paths] };

    private const string ManGen3 = "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl";

    /// <summary>The route decision as the shell makes it, maps assumed here.</summary>
    private static MensRoute? RouteOf(OverlayDescriptor d, string layout)
        => UVRemapService.MensRouteFor(d.SourceBodyType, d.SourceBodyType ?? (d.MaterialGamePaths.Count > 0 ? "gen3" : null),
                                       d.MaterialGamePaths, layout);

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Tbse_shell_part_converts_into_bibo_art() // [AC-tbse-uvmap-3]
    {
        var woman = Art("bibo", paths: ManGen3);
        var src = SecondSkinService.BodyLayerSource(woman, "tbse", false, "gen3", "gen3", d => RouteOf(d, "tbse"));
        Assert.Equal((new MensRoute("bibo", "tbse"), "bibo"), UVRemapService.ParseMensKey(src));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-5")]
    public void Mixed_shell_own_layer_unchanged() // [AC-tbse-uvmap-5]
    {
        // One shell, a woman's bibo layer and his own gen3 layer: his keeps today's type; vertices are not un-mirrored.
        var woman = Art("bibo", asym: true, paths: ManGen3);
        var his = Art("gen3", paths: ManGen3);
        Assert.Equal("gen3", SecondSkinService.BodyLayerSource(his, "tbse", false, "gen3", "gen3", d => RouteOf(d, "tbse")));
        Assert.NotNull(UVRemapService.ParseMensKey(
            SecondSkinService.BodyLayerSource(woman, "tbse", false, "gen3", "gen3", d => RouteOf(d, "tbse"))));
        Assert.Null(SecondSkinService.MenUnmirrorInto([woman, his], d => RouteOf(d, UVRemapService.MaleVanillaSpace)));
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-4")]
    [Trait("Plan", "EDGE-tbse-uvmap-13")]
    public void Asymmetric_art_on_vanilla_man_unmirrors() // [AC-tbse-uvmap-4] [EDGE-tbse-uvmap-13]
    {
        var woman = Art("bibo", asym: true, paths: ManGen3);
        Assert.Equal("bibo", SecondSkinService.MenUnmirrorInto([woman], d => RouteOf(d, UVRemapService.MaleVanillaSpace)));
        // Un-mirrored into bibo, the woman's bibo art is native to the shell: no men's key on it.
        Assert.Equal("bibo", SecondSkinService.BodyLayerSource(woman, UVRemapService.MaleVanillaSpace, true, "bibo", "bibo",
                                                               d => RouteOf(d, UVRemapService.MaleVanillaSpace)));
        // Symmetric art needs no un-mirroring.
        Assert.Null(SecondSkinService.MenUnmirrorInto([Art("bibo", paths: ManGen3)], d => RouteOf(d, UVRemapService.MaleVanillaSpace)));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-13")]
    public void Vanilla_gate_applies_to_male_vanilla_parts() // [EDGE-tbse-uvmap-13]
    {
        // A man's vanilla part reads as gen2 to the shell, so the "Overlay gen2/vanilla" gate applies to it as today.
        Assert.Equal("gen2", SecondSkin_SkinType("/mt_c0101b0001_a.mtrl"));
    }

    private static string? SecondSkin_SkinType(string material) => SecondSkinWriter.SkinMaterialBodyType(material);

    [LocalDataFact(UvTransferBakeLocalTests.Tbse, UvTransferBakeLocalTests.Bibo)]
    [Trait("Plan", "AC-tbse-uvmap-3")]
    public void Part_layout_read_from_mdl_bytes() // [AC-tbse-uvmap-3]
    {
        // As a shell part is read: from the model's drawn skin material, no game path.
        var tbse = File.ReadAllBytes(Path.Combine(LocalData.Path(UvTransferBakeLocalTests.Tbse), "common", "1", "c0101e0000_top.mdl"));
        var bibo = File.ReadAllBytes(Path.Combine(LocalData.Path(UvTransferBakeLocalTests.Bibo), UvBakeInputs.BiboChest));
        Assert.Equal(UVRemapService.TbseSpace, SecondSkinService.PartMenLayout(tbse));
        Assert.Null(SecondSkinService.PartMenLayout(bibo));
    }
}
