using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

public class MatcapPlanTests
{
    [Fact]
    [Trait("Plan", "AC-matcaps-11")]
    public void Zear_paths_are_targets_faces_tails_not()
    {
        var horn = MatcapPlan.TargetOf("chara/human/c1401/obj/zear/z0001/model/c1401z0001_zer.mdl", out _)!;
        Assert.Equal(MatcapPlan.Kind.Ear, horn.Kind);
        Assert.Equal((ushort)1401, horn.Race);
        Assert.Equal(MatcapPlan.Kind.Hair, MatcapPlan.TargetOf("chara/human/c0201/obj/hair/h0168/model/c0201h0168_hir.mdl", out _)!.Kind);
        Assert.Equal(MatcapPlan.Kind.Gear, MatcapPlan.TargetOf("chara/equipment/e6255/model/c0201e6255_top.mdl", out _)!.Kind);
        Assert.Equal(MatcapPlan.Kind.Gear, MatcapPlan.TargetOf("chara/accessory/a0053/model/c0201a0053_rir.mdl", out _)!.Kind);
        Assert.Null(MatcapPlan.TargetOf("chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl", out var why));
        Assert.Equal("not-target", why);
        Assert.Null(MatcapPlan.TargetOf("chara/human/c1401/obj/tail/t0001/model/c1401t0001_til.mdl", out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-42")]
    public void Viera_ears_allowed_faces_tails_refused()
    {
        var ear = MatcapPlan.TargetOf("chara/human/c1801/obj/zear/z0003/model/c1801z0003_zer.mdl", out _)!;
        Assert.Equal(MatcapPlan.Kind.Ear, ear.Kind);
        Assert.False(ZearMaterialPath.IsHorn(1801));
        Assert.True(ZearMaterialPath.IsHorn(1301));
        Assert.Null(MatcapPlan.TargetOf("chara/human/c1801/obj/face/f0001/model/c1801f0001_fac.mdl", out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-31")]
    public void Name_skips_taken_suffixes()
    {
        var token = "3fa21c";
        Assert.Equal("/mt_c0201e6255_top_a_mcap3fa21c1.mtrl", MatcapPlan.NewName("/mt_c0201e6255_top_a.mtrl", token, []));
        Assert.Equal("/mt_c0201e6255_top_a_mcap3fa21c3.mtrl", MatcapPlan.NewName("/mt_c0201e6255_top_a.mtrl", token,
            ["/mt_c0201e6255_top_a_mcap3fa21c1.mtrl", "chara/equipment/e6255/material/v0001/mt_c0201e6255_top_a_mcap3fa21c2.mtrl"]));
        Assert.True(MatcapPlan.IsMatcapName("/mt_c0201e6255_top_a_mcap3fa21c12.mtrl"));
        Assert.False(MatcapPlan.IsMatcapName("/mt_c0201e6255_top_a.mtrl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-28")]
    public void Token_reused_from_record_after_rename()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var token = MatcapRecord.Read(f.ModRoot).Token;
        Assert.Equal(MatcapPlan.TokenFor("Gear Mod"), token);
        // Renamed: the folder moves, the record (inside the mod) keeps the token.
        var renamed = Path.Combine(f.Mods, "Gear Mod (renamed)");
        Directory.Move(f.ModRoot, renamed);
        var bytes = File.ReadAllBytes(MatcapWriter.Full(renamed, MatcapFixture.ModelRel));
        var part = ModelPartReader.Read(bytes)!.Parts.First(p => p.Label == "2.1");
        var p = MatcapStudio.Prepare(renamed, MatcapFixture.ModelRel, MatcapFixture.ModelPath, bytes, [part],
                                     MatcapChoice.OfImage(f.Image("b.tex", seed: 2)), f.Env());
        Assert.True(p.Ok, p.Message);
        Assert.Contains(token, p.Request!.MaterialName);
        Directory.Move(renamed, f.ModRoot);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-49")]
    public void Absolute_material_name_published_at_exact_path()
    {
        var target = MatcapPlan.TargetOf("chara/equipment/e6255/model/c0201e6255_top.mdl", out _)!;
        var name = MatcapPlan.NewName("chara/equipment/e6255/material/v0001/mt_custom_a.mtrl", "abcdef", []);
        Assert.Equal("chara/equipment/e6255/material/v0001/mt_custom_a_mcapabcdef1.mtrl", name);
        Assert.Equal([name], MatcapPlan.MaterialGamePaths(target, name, [], [1, 2]));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-20")]
    public void Hair_paths_per_published_race()
    {
        var target = MatcapPlan.TargetOf("chara/human/c0801/obj/hair/h0130/model/c0801h0130_hir.mdl", out _)!;
        var paths = MatcapPlan.MaterialGamePaths(target, "/mt_c0801h0130_hir_a_mcapabcdef1.mtrl",
            ["chara/human/c0801/obj/hair/h0130/model/c0801h0130_hir.mdl", "chara/human/c0101/obj/hair/h0130/model/c0101h0130_hir.mdl"], null);
        // h0130 is shared by sex: the Miqo'te file reads Midlander's (c0201) folder with that code; published at c0101
        // it reads c0101's own folder, with the name as the file writes it (HairMaterialPath's rule).
        Assert.Contains("chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_a_mcapabcdef1.mtrl", paths);
        Assert.Contains("chara/human/c0101/obj/hair/h0130/material/v0001/mt_c0801h0130_hir_a_mcapabcdef1.mtrl", paths);
        Assert.All(paths, p => Assert.EndsWith("_hir_a_mcapabcdef1.mtrl", p));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-21")]
    public void Unredirected_sister_imc_variant_gets_material()
    {
        var item = new VariantRepointPlan.Item('e', 6255, "top");
        // The game's IMC sends variant 2 to folder 3; the mod only dresses folder 1.
        var game = new[] { new ImcEntry(1, 0, 0, 0, 0, 0), new ImcEntry(1, 0, 0, 0, 0, 0), new ImcEntry(3, 0, 0, 0, 0, 0) };
        var folders = MatcapPlan.GearFolders(item, game, [], ["chara/equipment/e6255/material/v0001/mt_c0201e6255_top_a.mtrl"], wornFolder: 1);
        Assert.Equal([1, 3], folders);
        var target = MatcapPlan.TargetOf("chara/equipment/e6255/model/c0201e6255_top.mdl", out _)!;
        var paths = MatcapPlan.MaterialGamePaths(target, "/mt_c0201e6255_top_a_mcapabcdef1.mtrl", [], folders);
        Assert.Equal(["chara/equipment/e6255/material/v0001/mt_c0201e6255_top_a_mcapabcdef1.mtrl",
                      "chara/equipment/e6255/material/v0003/mt_c0201e6255_top_a_mcapabcdef1.mtrl"], paths);
        // The mod's own IMC edits count too.
        Assert.Contains(7, MatcapPlan.GearFolders(item, game, [new ImcEntry(7, 0, 0, 0, 0, 0)], [], 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-41")]
    public void Horn_published_at_two_races_gets_each_races_paths()
    {
        var target = MatcapPlan.TargetOf("chara/human/c1401/obj/zear/z0001/model/c1401z0001_zer.mdl", out _)!;
        var paths = MatcapPlan.MaterialGamePaths(target, "/mt_c1401z0001_a_mcapabcdef1.mtrl",
            ["chara/human/c1301/obj/zear/z0001/model/c1301z0001_zer.mdl"], null);
        Assert.Contains("chara/human/c1401/obj/zear/z0001/material/mt_c1401z0001_a_mcapabcdef1.mtrl", paths);
        Assert.Contains("chara/human/c1301/obj/zear/z0001/material/mt_c1401z0001_a_mcapabcdef1.mtrl", paths);
        // The c1301 file names a c1401 material: the name re-derived from its folder is published as well.
        Assert.Contains("chara/human/c1301/obj/zear/z0001/material/mt_c1301z0001_a_mcapabcdef1.mtrl", paths);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-11")]
    public void SizeSet_refit_and_hairport_outputs_refused()
    {
        using var f = new MatcapFixture();
        Assert.Null(MatcapPlan.Regenerated(f.ModRoot, MatcapFixture.ModelRel));
        Assert.Equal("hair-port", MatcapPlan.Regenerated(f.ModRoot, HairPortPlan.Folder + "/x.mdl"));
        Assert.Equal("earless", MatcapPlan.Regenerated(f.ModRoot, EarlessHairPlan.Folder + "/x.mdl"));
        Assert.Equal("refit", MatcapPlan.Regenerated(f.ModRoot, BodyRetargetWriter.Subfolder + "/x/top.mdl"));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-4")]
    public void Normal_is_parts_own_normal_sampler_texture()
    {
        using var f = new MatcapFixture();
        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.Equal(MatcapFixture.Normal, p.Request!.Normal);
        Assert.Null(p.Request.FlatNormalTemp);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-37")]
    public void Missing_normal_uses_flat_normal_alpha_255()
    {
        using var f = new MatcapFixture();
        File.Delete(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"));
        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.NotNull(p.Request!.FlatNormalTemp);
        Assert.EndsWith("/flatnormal.tex", p.Request.Normal);
        var rgba = f.Loader.LoadImageAsRgba(p.Request.FlatNormalTemp!)!.Value.rgba;
        Assert.Equal(MatcapMaterial.FlatNormalRgba, rgba[..4]);
    }
}

public class ZearMaterialPathTests
{
    [Fact]
    [Trait("Plan", "EDGE-matcaps-50")]
    public void Name_race_id_differs_from_folder_yields_all_derivations()
    {
        var paths = ZearMaterialPath.GamePaths("chara/human/c1301/obj/zear/z0002/model/c1301z0002_zer.mdl", "/mt_c1401z0001_a_mcapx1.mtrl");
        Assert.Equal([
            "chara/human/c1301/obj/zear/z0002/material/mt_c1401z0001_a_mcapx1.mtrl",
            "chara/human/c1301/obj/zear/z0002/material/mt_c1301z0002_a_mcapx1.mtrl"], paths);
        Assert.All(paths, p => Assert.DoesNotContain("/v0001/", p));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-11")]
    public void The_rule_s_path_has_no_variant_folder_and_keeps_the_name()
    {
        var paths = ZearMaterialPath.GamePaths("chara/human/c1401/obj/zear/z0001/model/c1401z0001_zer.mdl", "/mt_c1401z0001_a.mtrl");
        Assert.Equal(["chara/human/c1401/obj/zear/z0001/material/mt_c1401z0001_a.mtrl"], paths);
        Assert.Equal(["chara/x/y.mtrl"], ZearMaterialPath.GamePaths("chara/human/c1401/obj/zear/z0001/model/c1401z0001_zer.mdl", "chara/x/y.mtrl"));
    }
}

/// <summary>The ear rule against the game's own horn and ear models: every material each names resolves by the rule.</summary>
public class ZearMaterialPathLocalTests(ITestOutputHelper output)
{
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-11")]
    public void Vanilla_ear_materials_resolve_by_rule()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        int checkedCount = 0;
        foreach (var race in new[] { 1301, 1401, 1701, 1801 })
            for (int id = 1; id <= 6; id++)
            {
                var model = $"chara/human/c{race:D4}/obj/zear/z{id:D4}/model/c{race:D4}z{id:D4}_zer.mdl";
                var bytes = data.GetFile(model)?.Data;
                if (bytes == null) continue;
                foreach (var name in SecondSkinWriter.MaterialNames(bytes))
                {
                    var paths = ZearMaterialPath.GamePaths(model, name);
                    var found = paths.FirstOrDefault(p => data.FileExists(p));
                    output.WriteLine($"{model} {name} -> {found ?? "(none)"}");
                    Assert.True(found != null, $"{model}: no rule path for {name} exists ({string.Join(", ", paths)})");
                    // The rule's own path (no variant folder) is the one the game ships.
                    Assert.Equal(paths[0], found);
                    checkedCount++;
                }
            }
        Assert.True(checkedCount > 0);
    }
}

public sealed class MatcapImagesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-matcap-img-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    [Fact]
    [Trait("Plan", "AC-matcaps-2")]
    [Trait("Plan", "EDGE-matcaps-33")]
    public void Lists_global_and_mod_images_mod_wins_by_name()
    {
        var global = Path.Combine(root, "global");
        var mod = Path.Combine(root, "mod");
        Directory.CreateDirectory(global); Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(global, "gem.png"), "g");
        File.WriteAllText(Path.Combine(global, "pearl.jpg"), "g");
        File.WriteAllText(Path.Combine(global, "notes.txt"), "g");
        File.WriteAllText(Path.Combine(global, "layers.psd"), "g");
        File.WriteAllText(Path.Combine(mod, "GEM.png"), "m");
        var list = MatcapImages.List(global, mod);
        Assert.Equal(["GEM.png", "pearl.jpg"], list.Select(i => i.Name));
        Assert.True(list[0].FromMod);
        Assert.Equal(Path.Combine(mod, "GEM.png"), MatcapImages.Find("gem.png", global, mod)!.Path);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-32")]
    public void Square_rule_via_LoadImageAsRgba_resample_and_cap()
    {
        Assert.Equal(256, MatcapImages.BakeSide(256, 256, out _));
        Assert.Equal(256, MatcapImages.BakeSide(300, 300, out _));
        Assert.Equal(1024, MatcapImages.BakeSide(4096, 4096, out _));
        Assert.Null(MatcapImages.BakeSide(256, 128, out var why));
        Assert.Equal("not-square", why);
        Assert.Null(MatcapImages.BakeSide(2, 2, out why));
        Assert.Equal("unreadable", why);

        using var f = new MatcapFixture();
        var img = f.Image("odd.tex", 96, 96);
        var baked = MatcapImages.Bake(f.Loader, img.Path, Path.Combine(f.Temp, "o.tex"), compress: false);
        Assert.Null(baked.Refusal);
        Assert.Equal(64, baked.Side);
        Assert.Equal(64, f.Loader.LoadImageAsRgba(baked.TempTex!)!.Value.width);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-2")]
    public void MatcapsLibraryPath_is_beside_Effects()
    {
        Assert.Equal("Matcaps", SidecarDiscoveryService.MatcapsSubdir);
        Assert.Equal(Path.Combine(@"C:\m", "Proteus", "Matcaps"), MatcapImages.ModFolder(@"C:\m"));
    }
}
