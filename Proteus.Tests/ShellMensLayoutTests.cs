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
    public void Shell_moved_out_of_his_layout_takes_no_route() // [EDGE-tbse-uvmap-13]
    {
        // A vanilla man (shell in gen2) un-mirrored the women's way into bibo, because a layer of his own is on it: the
        // woman's bibo layer is native to the bibo shell now and must not be carried into men's vanilla.
        var woman = Art("bibo", asym: true, paths: ManGen3);
        Assert.Equal("bibo", SecondSkinService.BodyLayerSource(woman, UVRemapService.MaleVanillaSpace, false, "bibo", "gen2",
                                                               d => RouteOf(d, UVRemapService.MaleVanillaSpace)));
        // No men's layout read at all: today's type.
        Assert.Equal("bibo", SecondSkinService.BodyLayerSource(woman, null, false, "gen3", null, d => RouteOf(d, "tbse")));
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-13")]
    public void Women_way_unmirror_drops_his_layout() // [EDGE-tbse-uvmap-13]
    {
        const string mv = UVRemapService.MaleVanillaSpace;
        // Nothing un-mirrored: his layout, in the space it was read in.
        Assert.Equal((mv, "gen2"), SecondSkinService.MenAfterUnmirror(mv, "gen2", unmirror: false, menUnmirror: false));
        // Un-mirrored by the men's maps: kept (the women's layers are then native to the shell anyway).
        Assert.Equal((mv, "gen2"), SecondSkinService.MenAfterUnmirror(mv, "gen2", unmirror: true, menUnmirror: true));
        // Un-mirrored the women's way: dropped, so the woman's layer takes no route into a layout the shell has left.
        Assert.Equal(((string?)null, (string?)null), SecondSkinService.MenAfterUnmirror(mv, "gen2", unmirror: true, menUnmirror: false));
        Assert.Equal(((string?)null, (string?)null), SecondSkinService.MenAfterUnmirror(null, "gen3", unmirror: false, menUnmirror: false));

        // And the build feeds exactly these into the per-layer decision (no compositor harness: read from source).
        var src = File.ReadAllText(RepoFile("Proteus", "Services", "SecondSkinService.ShellSetBuild.cs"));
        Assert.Contains("(menLayout, menOwnSpace) = MenAfterUnmirror(menLayout, menOwnSpace, unmirror, menUnmirror);", src);
        Assert.Contains("BodyLayerSource(d, menLayout, menUnmirror, s.UvSpace, menOwnSpace,", src);
    }

    private static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(System.AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine([d.FullName, .. parts]);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(Path.Combine(parts));
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

    // ── women's layers held off a man's shell ([womens-overlays-men]) ─────────────────────────

    private static (OverlayEntry Entry, ResolvedOverlay Overlay) Layer(string mod, OverlayDescriptor d)
        => (new OverlayEntry(mod, mod, 0, true, new ProteusMetadata(), "C:/x/Proteus"), new ResolvedOverlay(d, null, null, null));

    private const string WomanBibo = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
    private const string WomanFace = "chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl";

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-4")]
    public void Shell_hold_drops_womens_layer_while_maps_absent() // [AC-womens-overlays-men-4]
    {
        var hers = Layer("Stockings", Art(null, paths: WomanBibo));
        var (kept, held) = WomensArtOnMen.HoldShellLayers([hers], "tbse", true, menMapsReady: false, _ => true, _ => true,
                                                          SecondSkinService.ShellLegacySourceOf);
        Assert.Empty(kept);
        Assert.Equal(WomensArtOnMen.MenVerdict.AwaitMaps, Assert.Single(held).Verdict);
        Assert.Contains("service.uvRemap.RequestMenMaps();", File.ReadAllText(RepoFile("Proteus", "Services", "SecondSkinService.ShellSetBuild.cs")));
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-7")]
    [Trait("Plan", "EDGE-womens-overlays-men-17")]
    public void Womens_shell_is_never_held() // [AC-womens-overlays-men-7] [EDGE-womens-overlays-men-17]
    {
        var hers = Layer("Stockings", Art(null, paths: WomanBibo));
        var (kept, held) = WomensArtOnMen.HoldShellLayers([hers], null, wearerIsMan: false, false, _ => false, _ => false,
                                                          SecondSkinService.ShellLegacySourceOf);
        Assert.Single(kept);
        Assert.Empty(held);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-17")]
    public void All_held_leaves_nothing_his_own_layers_kept_face_layers_untouched() // [EDGE-womens-overlays-men-17]
    {
        var hers = Layer("Stockings", Art(null, paths: WomanBibo));
        var mask = Layer("Stockings", new OverlayDescriptor { IsMaskShell = true, MaterialGamePaths = [WomanBibo] });
        var his = Layer("Mine", Art("gen3", paths: ManGen3));
        var face = Layer("Makeup", Art("bibo", paths: WomanFace));
        var (kept, held) = WomensArtOnMen.HoldShellLayers([hers, mask, his, face], "tbse", true, false, _ => true, _ => true,
                                                          SecondSkinService.ShellLegacySourceOf);
        Assert.Equal(["Mine", "Makeup"], kept.ConvertAll(k => k.Entry.ModDirectory));
        Assert.Equal(2, held.Count);
        var (none, all) = WomensArtOnMen.HoldShellLayers([hers, mask], "tbse", true, false, _ => true, _ => true,
                                                          SecondSkinService.ShellLegacySourceOf);
        Assert.Empty(none);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-17")]
    public void Empty_after_hold_abandons() // [EDGE-womens-overlays-men-17]
    {
        var src = File.ReadAllText(RepoFile("Proteus", "Services", "SecondSkinService.ShellSetBuild.cs"));
        Assert.Contains("if (!HoldWomensLayers()) return Abandoned();", src);
        Assert.Contains("if (kept.Count == 0 && contentIn == 0) { result = null; return false; }", src);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-13")]
    [Trait("Plan", "EDGE-womens-overlays-men-18")]
    public void Hold_runs_after_uv_space_before_unmirror() // [EDGE-womens-overlays-men-13] [EDGE-womens-overlays-men-18]
    {
        var src = File.ReadAllText(RepoFile("Proteus", "Services", "SecondSkinService.ShellSetBuild.cs"));
        int choose = src.IndexOf("            ChooseUvSpace();", System.StringComparison.Ordinal);
        int hold = src.IndexOf("if (!HoldWomensLayers()) return Abandoned();", System.StringComparison.Ordinal);
        int unmirror = src.IndexOf("            UnmirrorBody();", System.StringComparison.Ordinal);
        int after = src.IndexOf("if (!HoldWomensLayersAfterUnmirror()) return Abandoned();", System.StringComparison.Ordinal);
        int convert = src.IndexOf("            ConvertUvSpace();", System.StringComparison.Ordinal);
        Assert.True(choose >= 0 && choose < hold && hold < unmirror && unmirror < after && after < convert);
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-18")]
    public void Unmirror_decides_from_kept_layers() // [EDGE-womens-overlays-men-18]
    {
        // The held woman's asymmetric layer is gone before the un-mirror looks: only his own symmetric layer is left, so the
        // men's un-mirror has nothing to un-mirror for.
        var hers = Layer("Stockings", Art(null, asym: true, paths: WomanBibo));
        var his = Layer("Mine", Art("gen3", paths: ManGen3));
        var (kept, _) = WomensArtOnMen.HoldShellLayers([hers, his], UVRemapService.MaleVanillaSpace, true, false, _ => true, _ => true,
                                                       SecondSkinService.ShellLegacySourceOf);
        Assert.Null(SecondSkinService.MenUnmirrorInto(kept.ConvertAll(k => k.Overlay.Descriptor),
                                                      d => RouteOf(d, UVRemapService.MaleVanillaSpace)));
    }

    [Fact]
    [Trait("Plan", "EDGE-womens-overlays-men-18")]
    public void Vanilla_man_own_asymmetric_plus_womans_layer_never_pastes_legacy() // [EDGE-womens-overlays-men-18]
    {
        // His own asymmetric bibo layer un-mirrors his vanilla shell the women's way: the shell leaves his layout, so the
        // woman's layer still on it is held (holdEveryRouted), never pasted in her layout. His own layer stays.
        var hers = Layer("Stockings", Art(null, paths: WomanBibo));
        var hisAsym = Layer("Mine", Art("gen2", asym: true, paths: ManGen3));
        var (kept, held) = WomensArtOnMen.HoldShellLayers([hers, hisAsym], UVRemapService.MaleVanillaSpace, true, true, _ => true,
            _ => true, SecondSkinService.ShellLegacySourceOf, holdEveryRouted: true);
        Assert.Equal("Mine", Assert.Single(kept).Entry.ModDirectory);
        Assert.Equal("Stockings", Assert.Single(held).Pair.Entry.ModDirectory);

        var src = File.ReadAllText(RepoFile("Proteus", "Services", "SecondSkinService.ShellSetBuild.cs"));
        // A woman's layer is never the women-way un-mirror's pick on a man.
        Assert.Contains("if (WearerIsMan && UVRemapService.MensRouteFor(d.SourceBodyType, ShellLegacySourceOf(d), d.MaterialGamePaths,", src);
        Assert.Contains("HoldWomen(holdEveryRouted: true, menLayoutAtChoice)", src);
    }
}
