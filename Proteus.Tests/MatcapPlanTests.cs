using System;
using System.Collections.Generic;
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
    public void Face_and_zear_paths_are_targets_tails_not()
    {
        var face = MatcapPlan.TargetOf("chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl", out _)!;
        Assert.Equal(MatcapPlan.Kind.Face, face.Kind);
        Assert.Equal((ushort)1401, face.Race);
        Assert.Equal(MatcapPlan.Kind.Ear, MatcapPlan.TargetOf("chara/human/c1801/obj/zear/z0001/model/c1801z0001_zer.mdl", out _)!.Kind);
        Assert.Equal(MatcapPlan.Kind.Hair, MatcapPlan.TargetOf("chara/human/c0201/obj/hair/h0168/model/c0201h0168_hir.mdl", out _)!.Kind);
        Assert.Equal(MatcapPlan.Kind.Gear, MatcapPlan.TargetOf("chara/equipment/e6255/model/c0201e6255_top.mdl", out _)!.Kind);
        Assert.Equal(MatcapPlan.Kind.Gear, MatcapPlan.TargetOf("chara/accessory/a0053/model/c0201a0053_rir.mdl", out _)!.Kind);
        Assert.Null(MatcapPlan.TargetOf("chara/human/c1401/obj/tail/t0001/model/c1401t0001_til.mdl", out var why));
        Assert.Equal("not-target", why);
    }

    /// <summary>A face like an Au Ra's: skin, a horn mesh (fac_c, whole), a mesh with a horn and an untagged submesh
    /// (fac_b), and irises. Attribute bit 2 is atr_hrn.</summary>
    internal static byte[] SyntheticFace() => SyntheticModel.Build(["atr_kao", "atr_fv_a", "atr_hrn"],
        [new SyntheticModel.Mesh("/mt_c1401f0001_fac_a.mtrl", new SyntheticModel.Sub(0b001, TrianglesPerIsland: 3)),
         new SyntheticModel.Mesh("/mt_c1401f0001_fac_c.mtrl", new SyntheticModel.Sub(0b100, TrianglesPerIsland: 2, OffsetX: 20f)),
         new SyntheticModel.Mesh("/mt_c1401f0001_fac_b.mtrl", new SyntheticModel.Sub(0b100, TrianglesPerIsland: 2, OffsetX: 40f),
                                                             new SyntheticModel.Sub(0b001, OffsetX: 40f, OffsetY: 5f)),
         new SyntheticModel.Mesh("/mt_c1401f0001_iri_a.mtrl", new SyntheticModel.Sub(0b001, OffsetX: 60f))],
        SyntheticModel.V6, (IReadOnlyList<string>?)null);

    [Fact]
    [Trait("Plan", "AC-matcaps-11")]
    [Trait("Plan", "EDGE-matcaps-42")]
    public void Face_only_hrn_parts_tickable()
    {
        var read = ModelPartReader.Read(SyntheticFace())!;
        bool Tick(string label) => MatcapPlan.Tickable(read.Parts.Single(p => p.Label == label), MatcapPlan.Kind.Face, read.AttributeNames);
        Assert.False(Tick("1.1"));   // face skin
        Assert.True(Tick("2.1"));    // horn, whole mesh
        Assert.True(Tick("3.1"));    // horn submesh
        Assert.False(Tick("3.2"));   // untagged submesh of the same mesh
        Assert.False(Tick("4.1"));   // irises
        Assert.True(MatcapPlan.HasHorns(read.Parts, read.AttributeNames));
        // Elsewhere the attribute is not asked for.
        Assert.True(MatcapPlan.Tickable(read.Parts.Single(p => p.Label == "4.1"), MatcapPlan.Kind.Gear, read.AttributeNames));
        Assert.False(CustomizationMaterialPath.IsHorn(1801));
        Assert.True(CustomizationMaterialPath.IsHorn(1301));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-42")]
    public void Face_non_horn_part_refused_not_horn_and_hornless_face_no_horns()
    {
        const string FacePath = "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl";
        using var f = new MatcapFixture(SyntheticFace(), extra: new Dictionary<string, (string, byte[])>
        {
            [FacePath] = (MatcapFixture.ModelRel, SyntheticFace()),
        });
        var bytes = f.Current();
        var parts = ModelPartReader.Read(bytes)!.Parts;
        var image = MatcapChoice.OfImage(f.Image("gem.tex"));
        Assert.Equal("not-horn", MatcapStudio.Prepare(f.ModRoot, MatcapFixture.ModelRel, FacePath, bytes,
            [parts.Single(p => p.Label == "1.1")], image, f.Env()).Code);
        var ok = MatcapStudio.Prepare(f.ModRoot, MatcapFixture.ModelRel, FacePath, bytes,
            [parts.Single(p => p.Label == "2.1"), parts.Single(p => p.Label == "3.1")], image, f.Env());
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(2, ok.Request!.Materials.Count);   // one per horn material
        Assert.All(ok.Request.Materials.SelectMany(m => m.GamePaths), p =>
        {
            Assert.StartsWith("chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_", p);
            Assert.DoesNotContain("/v0001/", p);
        });

        var hornless = SyntheticModel.Build(["atr_kao"], new SyntheticModel.Mesh("/mt_c1401f0091_fac_a.mtrl", new SyntheticModel.Sub(1)));
        var hp = ModelPartReader.Read(hornless)!.Parts;
        Assert.Equal("no-horns", MatcapStudio.Prepare(f.ModRoot, MatcapFixture.ModelRel, "chara/human/c1401/obj/face/f0091/model/c1401f0091_fac.mdl",
            hornless, [hp[0]], image, f.Env()).Code);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-56")]
    public void Face_horn_submesh_moved_keeps_atr_hrn()
    {
        var face = SyntheticFace();
        var read = ModelPartReader.Read(face)!;
        var result = ModelMaterialWriter.MoveToMaterial(face, [read.Parts.Single(p => p.Label == "3.1")], "/mt_c1401f0001_fac_b_mcapabcdef1.mtrl");
        var after = ModelPartReader.Read(result.Model)!;
        var moved = after.Parts.Where(p => p.Material.Contains("_mcap") && p.Island < 0).ToList();
        Assert.NotEmpty(moved);
        Assert.All(moved, p => Assert.True(MatcapPlan.IsHorn(p, after.AttributeNames)));
        // A whole horn mesh is rebound in place, keeping its mask too.
        var whole = ModelMaterialWriter.MoveToMaterial(face, [read.Parts.Single(p => p.Label == "2.1")], "/mt_c1401f0001_fac_c_mcapabcdef1.mtrl");
        Assert.Equal(0, whole.MeshesAdded);
        Assert.True(MatcapPlan.IsHorn(ModelPartReader.Read(whole.Model)!.Parts.Single(p => p.Label == "2.1"), after.AttributeNames));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-60")]
    public void Face_material_paths_never_v0001()
    {
        var target = MatcapPlan.TargetOf("chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl", out _)!;
        var paths = MatcapPlan.MaterialGamePaths(target, "/mt_c1401f0001_fac_c_mcapabcdef1.mtrl",
            ["chara/human/c1301/obj/face/f0001/model/c1301f0001_fac.mdl"], null);
        Assert.Contains("chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_c_mcapabcdef1.mtrl", paths);
        Assert.Contains("chara/human/c1301/obj/face/f0001/material/mt_c1301f0001_fac_c_mcapabcdef1.mtrl", paths);   // EDGE-41
        Assert.All(paths, p => Assert.DoesNotContain("/v0001/", p));
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
        // Only hair ports and earless copies refuse; refits take a matcap.
        Assert.Equal("hair-port", MatcapPlan.RefusedRegenerated(f.ModRoot, HairPortPlan.Folder + "/x.mdl"));
        Assert.Equal("earless", MatcapPlan.RefusedRegenerated(f.ModRoot, EarlessHairPlan.Folder + "/x.mdl"));
        Assert.Null(MatcapPlan.RefusedRegenerated(f.ModRoot, BodyRetargetWriter.Subfolder + "/x/top.mdl"));
        Assert.Null(MatcapPlan.RefusedRegenerated(f.ModRoot, MatcapFixture.ModelRel));
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

public class CustomizationMaterialPathTests
{
    [Fact]
    [Trait("Plan", "EDGE-matcaps-50")]
    public void Name_race_id_differs_from_folder_yields_all_derivations()
    {
        var paths = CustomizationMaterialPath.GamePaths("chara/human/c1301/obj/zear/z0002/model/c1301z0002_zer.mdl", "/mt_c1401z0001_a_mcapx1.mtrl");
        Assert.Equal([
            "chara/human/c1301/obj/zear/z0002/material/mt_c1401z0001_a_mcapx1.mtrl",
            "chara/human/c1301/obj/zear/z0002/material/mt_c1301z0002_a_mcapx1.mtrl"], paths);
        Assert.All(paths, p => Assert.DoesNotContain("/v0001/", p));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-11")]
    public void The_rule_s_path_has_no_variant_folder_and_keeps_the_name()
    {
        var paths = CustomizationMaterialPath.GamePaths("chara/human/c1401/obj/zear/z0001/model/c1401z0001_zer.mdl", "/mt_c1401z0001_a.mtrl");
        Assert.Equal(["chara/human/c1401/obj/zear/z0001/material/mt_c1401z0001_a.mtrl"], paths);
        Assert.Equal(["chara/x/y.mtrl"], CustomizationMaterialPath.GamePaths("chara/human/c1401/obj/zear/z0001/model/c1401z0001_zer.mdl", "chara/x/y.mtrl"));
    }
}

/// <summary>The ear rule against the game's own horn and ear models: every material each names resolves by the rule.</summary>
public class CustomizationMaterialPathLocalTests(ITestOutputHelper output)
{
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-11")]
    public void Vanilla_ear_materials_resolve_by_rule()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        int checkedCount = 0;
        var models = new[] { 1301, 1401, 1701, 1801 }.SelectMany(race => Enumerable.Range(1, 6).SelectMany(id => new[]
        {
            $"chara/human/c{race:D4}/obj/zear/z{id:D4}/model/c{race:D4}z{id:D4}_zer.mdl",
            $"chara/human/c{race:D4}/obj/face/f{id:D4}/model/c{race:D4}f{id:D4}_fac.mdl",
        }));
        foreach (var model in models)
            {
                var bytes = data.GetFile(model)?.Data;
                if (bytes == null) continue;
                foreach (var name in SecondSkinWriter.MaterialNames(bytes))
                {
                    var paths = CustomizationMaterialPath.GamePaths(model, name);
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
