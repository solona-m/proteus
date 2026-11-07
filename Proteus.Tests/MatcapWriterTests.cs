using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A temp Penumbra-like folder with one v4 gear mod (a two-material top published in the default data, its own
/// <c>_a</c> material binding a normal map), the user's images, and an environment that reads a synthetic reflection
/// template instead of the game's.
/// </summary>
internal sealed class MatcapFixture : IDisposable
{
    public const string ModelPath = "chara/equipment/e6255/model/c0201e6255_top.mdl";
    public const string ModelRel = "model/top.mdl";
    public const string MatPath = "chara/equipment/e6255/material/v0001/mt_c0201e6255_top_a.mtrl";
    public const string Normal = "chara/equipment/e6255/texture/v01_c0201e6255_top_n.tex";

    public readonly string Root = Path.Combine(Path.GetTempPath(), "proteus-matcap-" + Guid.NewGuid().ToString("N"));
    public string Mods => Path.Combine(Root, "mods");
    public string ModRoot { get; }
    public string Images => Path.Combine(Root, "images");
    public string Temp => Path.Combine(Root, "temp");
    public readonly TextureLoader Loader = new(Substitute.For<Dalamud.Plugin.Services.IDataManager>(),
                                               Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
    public byte[] Model { get; }
    public byte[]? Template = SyntheticMatcapMod.Reflection();

    public MatcapFixture(byte[]? model = null, string folder = "Gear Mod", IDictionary<string, (string, byte[])>? extra = null)
    {
        Model = model ?? SyntheticModel.Build([],
            [new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl",
                new SyntheticModel.Sub(0, TrianglesPerIsland: 3),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 10f)),
             new SyntheticModel.Mesh("/mt_c0201e6255_top_b.mtrl",
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 50f))], SyntheticModel.V6, (IReadOnlyList<string>?)null);
        var published = new Dictionary<string, (string, byte[])>
        {
            [ModelPath] = (ModelRel, Model),
            [MatPath] = ("mat/top_a.mtrl", SyntheticMatcapMod.Gear(Normal)),
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, (string, byte[])>()) published[k] = v;
        ModRoot = SyntheticMatcapMod.TargetMod(Mods, folder, published);
        Directory.CreateDirectory(Images);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }

    public MatcapStudio.Environment Env(bool compress = false) =>
        new(p => p == MatcapMaterial.VanillaTemplatePath ? Template : null, Loader, compress, Temp, WornFolder: 1);

    /// <summary>A gradient image (a .tex, which the image loader reads like any picture).</summary>
    public MatcapImage Image(string name, int w = 64, int h = 64, byte seed = 0)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { rgba[i * 4] = (byte)(i + seed); rgba[i * 4 + 1] = (byte)(i / w); rgba[i * 4 + 2] = 200; rgba[i * 4 + 3] = 255; }
        var path = Path.Combine(Images, name);
        Assert.True(Loader.WriteTex(rgba, w, h, path));
        return new MatcapImage(name, path, false);
    }

    public MatcapLibrary Library(string slot = "chara/common/texture/testlib_a.tex", string folder = "Lib") =>
        MatcapLibrary.Detect(SyntheticMatcapMod.Library(Mods, folder, "Test Library", "https://example.test", [slot], ["Gem Red", "Pearl"]),
                             folder, folder).Single();

    public byte[] Current(string rel = ModelRel) => File.ReadAllBytes(MatcapWriter.Full(ModRoot, rel));

    public IReadOnlyList<ModelPart> Parts(params string[] labels)
    {
        var parts = ModelPartReader.Read(Current())!.Parts;
        return labels.Select(l => parts.Single(p => p.Label == l)).ToList();
    }

    public MatcapStudio.Prepared Prepare(MatcapChoice choice, params string[] labels) =>
        MatcapStudio.Prepare(ModRoot, ModelRel, ModelPath, Current(), Parts(labels), choice, Env());

    public MatcapWriter.Outcome Apply(MatcapChoice choice, params string[] labels)
    {
        var p = Prepare(choice, labels);
        Assert.True(p.Ok, p.Message);
        return MatcapWriter.Apply(ModRoot, p.Request!);
    }

    /// <summary>Every file in the mod with its bytes, plus the manifest — what "untouched" compares.</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(ModRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(ModRoot, f), f => HairPortPlan.Sha8(File.ReadAllBytes(f)));
}

public class MatcapWriterTests
{
    private static Dictionary<string, string> Defaults(string modRoot) =>
        PenumbraModMeta.TryReadDefaultData(modRoot)?.Files ?? new Dictionary<string, string>();

    [Fact]
    [Trait("Plan", "AC-matcaps-4")]
    public void Image_apply_writes_mtrl_tex_mask_defaultdata_record_backup()
    {
        using var f = new MatcapFixture();
        var before = f.Current();
        var outcome = f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.True(outcome.Ok, outcome.Message);

        var record = MatcapRecord.Read(f.ModRoot);
        var mat = Assert.Single(record.Materials);
        Assert.StartsWith("/mt_c0201e6255_top_a_mcap", mat.Name);
        Assert.False(mat.Pending);
        Assert.Equal(MatcapFixture.Normal, mat.Normal);
        Assert.Equal("image", mat.Source.Kind);
        Assert.Equal("gem.tex", mat.Source.Image);

        // Files, under Proteus Matcaps, and their default-data redirects.
        var defaults = Defaults(f.ModRoot);
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, mat.File)));
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, mat.Source.Tex!.File)));
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, record.Shared.Mask!.File)));
        Assert.Contains($"chara/equipment/e6255/material/v0001{mat.Name}", defaults.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(mat.Source.Tex.GamePath, defaults.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(record.Shared.Mask.GamePath, defaults.Keys, StringComparer.OrdinalIgnoreCase);

        // The material points at the parts' normal, our mask and our baked texture.
        var h = MatcapMaterial.ReadHeader(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File)))!;
        Assert.True(h.IsReflection);
        Assert.Equal(MatcapFixture.Normal, h.TextureOf(MatcapMaterial.SamplerNormal));
        Assert.Equal(record.Shared.Mask.GamePath, h.TextureOf(MatcapMaterial.SamplerMask));
        Assert.Equal(mat.Source.Tex.GamePath, h.TextureOf(MatcapMaterial.SamplerMatcap));

        // The model now names the material; the backup is the original.
        Assert.Contains(mat.Name, SecondSkinWriter.MaterialNames(f.Current()));
        Assert.Equal(before, File.ReadAllBytes(MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.BackupSubdir, MatcapFixture.ModelRel)));
        Assert.Contains(HairPortPlan.Sha8(f.Current()), record.Model(MatcapFixture.ModelRel)!.WrittenSha8s);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-1")]
    public void Vanilla_first_apply_places_into_copy_mod()
    {
        // The copy CopyVanillaForEdit makes: the game's model alone, in a new mod's default data.
        using var f = new MatcapFixture(folder: "e6255 Body — Proteus edit");
        File.Delete(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"));
        var outcome = f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.True(outcome.Ok, outcome.Message);
        // No normal to read: the generated flat one.
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        Assert.Equal(MatcapWriter.FlatNormalGamePath(MatcapRecord.Read(f.ModRoot).Token), mat.Normal);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-5")]
    public void Library_apply_names_shared_path_and_copies_no_library_file()
    {
        using var f = new MatcapFixture();
        var lib = f.Library();
        var outcome = f.Apply(MatcapChoice.OfLibrary(lib, "Pearl"), "1.2");
        Assert.True(outcome.Ok, outcome.Message);

        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        Assert.True(mat.Source.IsLibrary);
        Assert.Equal("Pearl", mat.Source.Library!.Option);
        Assert.Equal("https://example.test", mat.Source.Library.Website);
        Assert.Null(mat.Source.Tex);
        var h = MatcapMaterial.ReadHeader(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File)))!;
        Assert.Equal(lib.Texture, h.TextureOf(MatcapMaterial.SamplerMatcap));

        // Not one file of the library's is in the mod.
        var libHashes = Directory.EnumerateFiles(Path.Combine(f.Mods, "Lib"), "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".tex") || p.EndsWith(".mtrl")).Select(p => HairPortPlan.Sha8(File.ReadAllBytes(p))).ToHashSet();
        Assert.DoesNotContain(f.Snapshot().Values, libHashes.Contains);
        Assert.DoesNotContain(lib.Texture, Defaults(f.ModRoot).Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-6")]
    public void Change_image_rebakes_own_tex_only()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        var texBefore = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.Source.Tex!.File));
        var mtrlBefore = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File));
        var model = f.Current();

        var (src, mtrl, temp, code, msg) = MatcapStudio.PrepareChange(f.ModRoot, mat.Name, MatcapChoice.OfImage(f.Image("pearl.tex", seed: 77)), f.Env());
        Assert.Equal("", code);
        var outcome = MatcapWriter.Change(f.ModRoot, mat.Name, src!, mtrl, temp);
        Assert.True(outcome.Ok, outcome.Message);

        var after = MatcapRecord.Read(f.ModRoot).Materials.Single();
        Assert.Equal("pearl.tex", after.Source.Image);
        Assert.NotEqual(texBefore, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, after.Source.Tex!.File)));
        Assert.Equal(mtrlBefore, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, after.File)));
        Assert.Equal(model, f.Current());
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-6")]
    public void Change_library_option_or_library_repoints_mtrl()
    {
        using var f = new MatcapFixture();
        var a = f.Library("chara/common/texture/testlib_a.tex", "LibA");
        var b = f.Library("chara/common/texture/testlib_b.tex", "LibB");
        f.Apply(MatcapChoice.OfLibrary(a, "Pearl"), "1.2");
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        var mtrlBefore = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File));

        // Another option of the same slot: the material stays; only the record (and the collection) change.
        var c1 = MatcapStudio.PrepareChange(f.ModRoot, mat.Name, MatcapChoice.OfLibrary(a, "Gem Red"), f.Env());
        Assert.Null(c1.Material);
        Assert.True(MatcapWriter.Change(f.ModRoot, mat.Name, c1.Source!, c1.Material, null).Ok);
        Assert.Equal(mtrlBefore, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File)));
        Assert.Equal("Gem Red", MatcapRecord.Read(f.ModRoot).Materials.Single().Source.Library!.Option);

        // Another library: the material is repointed at its slot.
        var c2 = MatcapStudio.PrepareChange(f.ModRoot, mat.Name, MatcapChoice.OfLibrary(b, "Pearl"), f.Env());
        Assert.True(MatcapWriter.Change(f.ModRoot, mat.Name, c2.Source!, c2.Material, null).Ok);
        var h = MatcapMaterial.ReadHeader(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, mat.File)))!;
        Assert.Equal(b.Texture, h.TextureOf(MatcapMaterial.SamplerMatcap));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-6")]
    public void Change_writes_no_model()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var name = MatcapRecord.Read(f.ModRoot).Materials.Single().Name;
        var model = f.Current();
        var c = MatcapStudio.PrepareChange(f.ModRoot, name, MatcapChoice.OfImage(f.Image("b.tex", seed: 9)), f.Env());
        MatcapWriter.Change(f.ModRoot, name, c.Source!, c.Material, c.TexTemp);
        Assert.Equal(model, f.Current());
        Assert.Equal(model, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-45")]
    public void Cross_kind_change_refused()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var name = MatcapRecord.Read(f.ModRoot).Materials.Single().Name;
        var c = MatcapStudio.PrepareChange(f.ModRoot, name, MatcapChoice.OfLibrary(f.Library(), "Pearl"), f.Env());
        Assert.Equal(MatcapWriter.CodeCrossKind, c.Code);
        var direct = MatcapWriter.Change(f.ModRoot, name, new MatcapRecord.SourceRef { Kind = "library" }, null, null);
        Assert.Equal(MatcapWriter.CodeCrossKind, direct.Code);
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-7")]
    public void Undo_restores_group_bytes_and_removes_additions()
    {
        using var f = new MatcapFixture();
        var snapshot = f.Snapshot();
        var manifest = File.ReadAllText(Path.Combine(f.ModRoot, "meta.json"));
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(outcome.Ok, outcome.Message);

        var now = f.Snapshot();
        now.Remove("meta.json"); snapshot.Remove("meta.json");
        Assert.Equal(snapshot.OrderBy(k => k.Key), now.OrderBy(k => k.Key));
        Assert.Equal(Defaults(f.ModRoot).Keys.Order(),
            JsonDocument.Parse(manifest).RootElement.GetProperty("DefaultData").GetProperty("Files").EnumerateObject().Select(p => p.Name).Order());
        Assert.False(MatcapRecord.Exists(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "AM-4")]
    public void Two_applies_then_undo_restores_original_bytes()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        f.Apply(MatcapChoice.OfImage(f.Image("pearl.tex", seed: 5)), "2.1");
        Assert.Equal(2, MatcapRecord.Read(f.ModRoot).Materials.Count);
        Assert.Equal(original, File.ReadAllBytes(MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.BackupSubdir, MatcapFixture.ModelRel)));
        Assert.True(MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel).Ok);
        Assert.Equal(original, f.Current());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-34")]
    public void Two_image_matcaps_two_tex_one_mask()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        f.Apply(MatcapChoice.OfImage(f.Image("pearl.tex", seed: 5)), "2.1");
        var record = MatcapRecord.Read(f.ModRoot);
        Assert.Equal(2, record.Materials.Select(m => m.Source.Tex!.File).Distinct().Count());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(f.ModRoot, MatcapWriter.FilesFolder), "mask_*"));
        var names = SecondSkinWriter.MaterialNames(f.Current());
        Assert.All(record.Materials, m => Assert.Contains(m.Name, names));
        Assert.Equal(2, ModelPartReader.Read(f.Current())!.Parts.Where(p => p.Island < 0).Select(p => p.Material).Count(MatcapPlan.IsMatcapName));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-7")]
    [Trait("Plan", "EDGE-matcaps-23")]
    public void Undo_refused_while_later_feature_backup_newer()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Brush(f);
        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.Equal(MatcapWriter.CodeLaterFeature, outcome.Code);
    }

    /// <summary>A brush edit: its own backup of the file as it is (a later time), then a nudged vertex.</summary>
    internal static void Brush(MatcapFixture f, string rel = MatcapFixture.ModelRel)
    {
        var full = MatcapWriter.Full(f.ModRoot, rel);
        var matcapBackup = MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.BackupSubdir, rel);
        if (File.Exists(matcapBackup)) File.SetLastWriteTimeUtc(matcapBackup, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(-5));
        var backup = MatcapWriter.BackupPath(f.ModRoot, MeshVolumeService.BackupSubdir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(full, backup, true);
        var bytes = File.ReadAllBytes(full);
        var src = SecondSkinWriter.Parse(bytes);
        bytes[src.Vb] ^= 0x01;
        File.WriteAllBytes(full, bytes);
    }

    internal static void BrushUndo(MatcapFixture f, string rel = MatcapFixture.ModelRel)
    {
        var backup = MatcapWriter.BackupPath(f.ModRoot, MeshVolumeService.BackupSubdir, rel);
        File.Copy(backup, MatcapWriter.Full(f.ModRoot, rel), true);
        File.Delete(backup);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-51")]
    public void Matcap_brush_matcap_brush_undo_reconciles()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");        // W1
        Brush(f);                                                          // brush backs W1 up, edits
        var second = f.Prepare(MatcapChoice.OfImage(f.Image("pearl.tex", seed: 5)), "2.1");
        Assert.True(MatcapWriter.Apply(f.ModRoot, second.Request!).Ok);   // W2
        // Undo matcaps is blocked by the brush; the panel sends the user to undo the brush first.
        Assert.Equal(MatcapWriter.CodeLaterFeature, MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel).Code);
        BrushUndo(f);                                                      // back to W1: matcap 2 is gone
        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(original, f.Current());
        Assert.False(MatcapRecord.Exists(f.ModRoot));
        Assert.False(Directory.Exists(Path.Combine(f.ModRoot, MatcapWriter.FilesFolder))
                     && Directory.EnumerateFiles(Path.Combine(f.ModRoot, MatcapWriter.FilesFolder)).Any());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-24")]
    public void Author_update_forget_retires_backup()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), original);   // the author's update
        Assert.Equal(MatcapWriter.State.Superseded, MatcapWriter.Detect(f.ModRoot).Values.Single());
        Assert.False(MatcapWriter.StillApplies(f.ModRoot, MatcapFixture.ModelRel));

        var outcome = MatcapWriter.Forget(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(original, f.Current());
        Assert.False(File.Exists(MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.BackupSubdir, MatcapFixture.ModelRel)));
        Assert.True(File.Exists(MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.SupersededSubdir, MatcapFixture.ModelRel)));
        Assert.False(MatcapRecord.Exists(f.ModRoot));
        Assert.DoesNotContain(Defaults(f.ModRoot).Keys, k => k.Contains("_mcap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "AM-3")]
    public void Missing_model_file_is_forgotten_not_refused()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        File.Delete(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel));
        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(File.Exists(MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.SupersededSubdir, MatcapFixture.ModelRel)));
        Assert.False(MatcapRecord.Exists(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-25")]
    public void Edited_on_top_refused_naming_backup()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var bytes = f.Current();
        bytes[SecondSkinWriter.Parse(bytes).Vb] ^= 0x02;                 // someone else's edit, no backup of theirs
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), bytes);
        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.Equal(MatcapWriter.CodeEdited, outcome.Code);
        Assert.Contains("matcap-backup", outcome.Message);
        Assert.Equal(bytes, f.Current());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-26")]
    public void Hand_edited_own_files_kept_and_named()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        var tex = MatcapWriter.Full(f.ModRoot, mat.Source.Tex!.File);
        File.AppendAllText(tex, "edited");

        var c = MatcapStudio.PrepareChange(f.ModRoot, mat.Name, MatcapChoice.OfImage(f.Image("b.tex", seed: 3)), f.Env());
        Assert.Equal(MatcapWriter.CodeEdited, MatcapWriter.Change(f.ModRoot, mat.Name, c.Source!, c.Material, c.TexTemp).Code);

        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(File.Exists(tex));
        Assert.Contains(mat.Source.Tex.File, outcome.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-36")]
    public void Failure_at_each_step_rolls_back()
    {
        foreach (var step in new[] { "pending", "files", "redirects", "model" })
        {
            using var f = new MatcapFixture();
            var snapshot = f.Snapshot();
            var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
            snapshot = f.Snapshot();
            MatcapWriter.AfterStep = s => { if (s == step) throw new IOException("disk full at " + s); };
            try
            {
                var outcome = MatcapWriter.Apply(f.ModRoot, p.Request!);
                Assert.Equal(MatcapWriter.CodeFailed, outcome.Code);
            }
            finally { MatcapWriter.AfterStep = null; }
            var now = f.Snapshot();
            Assert.Equal(snapshot.Where(k => k.Key != "meta.json").OrderBy(k => k.Key), now.Where(k => k.Key != "meta.json").OrderBy(k => k.Key));
            Assert.DoesNotContain(Defaults(f.ModRoot).Keys, k => k.Contains("matcap", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(MatcapRecord.Read(f.ModRoot).Materials);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-36")]
    public void Pending_entry_cleaned_by_undo()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        // A crash between the pending record and the model write: the record says pending, the model is the original.
        var record = MatcapRecord.Read(f.ModRoot);
        record.Materials.Single().Pending = true;
        MatcapRecord.Write(f.ModRoot, record);
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), original);
        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.False(MatcapRecord.Exists(f.ModRoot));
        Assert.Equal(original, f.Current());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-52")]
    public void Detect_on_open_writes_nothing()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), original);
        var before = f.Snapshot();
        var times = Directory.EnumerateFiles(f.ModRoot, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        Assert.Equal(MatcapWriter.State.Superseded, MatcapWriter.Detect(f.ModRoot).Single().Value);
        Assert.Equal(before, f.Snapshot());
        Assert.All(times, t => Assert.Equal(t.Value, File.GetLastWriteTimeUtc(t.Key)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-9")]
    public void Already_matcap_parts_refused()
    {
        using var f = new MatcapFixture();
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var parts = ModelPartReader.Read(f.Current())!.Parts;
        var onMatcap = parts.First(p => MatcapPlan.IsOnMatcap(p) && p.Island < 0);
        Assert.False(MatcapPlan.Tickable(onMatcap));
        var p = MatcapStudio.Prepare(f.ModRoot, MatcapFixture.ModelRel, MatcapFixture.ModelPath, f.Current(), [onMatcap],
                                     MatcapChoice.OfImage(f.Image("b.tex", seed: 1)), f.Env());
        Assert.Equal("already-matcap", p.Code);
    }

    public static TheoryData<string> RefusalCodes() => new()
    {
        "legacy-mod", "imported-piece", "not-target", "no-horns", "skin-part", "regenerated-model",
        "model-layout", "material-cap", "not-square", "image-unreadable", "no-template",
    };

    [Theory]
    [Trait("Plan", "AC-matcaps-8")]
    [Trait("Plan", "EDGE-matcaps-35")]
    [MemberData(nameof(RefusalCodes))]
    public void Refusals_leave_mod_untouched(string code)
    {
        byte[]? model = code switch
        {
            "skin-part" => SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201b0001_a.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 2))),
            "material-cap" => SyntheticModel.Build([], Enumerable.Range(0, SecondSkinWriter.MaxMaterials)
                .Select(i => new SyntheticModel.Mesh($"/mt_c0201e6255_top_{(char)('a' + i)}.mtrl", new SyntheticModel.Sub(0, OffsetX: i * 20f))).ToArray()),
            _ => null,
        };
        using var f = new MatcapFixture(model, folder: code == "imported-piece" ? "Proteus" : "Gear Mod");
        var image = code switch
        {
            "not-square" => f.Image("wide.tex", 64, 32),
            "image-unreadable" => new MatcapImage("junk.png", WriteJunk(f), false),
            _ => f.Image("gem.tex"),
        };
        string rel = MatcapFixture.ModelRel, gamePath = MatcapFixture.ModelPath;
        if (code == "legacy-mod")
        {
            // A v3 folder: default_mod.json, meta.json without groups.
            File.WriteAllText(Path.Combine(f.ModRoot, "default_mod.json"), "{\"Files\":{}}");
            File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), "{\"FileVersion\":3,\"Name\":\"x\"}");
        }
        if (code == "not-target") gamePath = "chara/human/c1401/obj/tail/t0001/model/c1401t0001_til.mdl";
        if (code == "no-horns") gamePath = "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl";
        if (code == "regenerated-model")
        {
            rel = HairPortPlan.Folder + "/top.mdl";
            Directory.CreateDirectory(Path.GetDirectoryName(MatcapWriter.Full(f.ModRoot, rel))!);
            File.Copy(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), MatcapWriter.Full(f.ModRoot, rel));
        }
        if (code == "no-template") f.Template = null;
        var bytes = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, rel));
        if (code == "model-layout") { bytes = (byte[])bytes.Clone(); BitConverter.TryWriteBytes(bytes.AsSpan(SecondSkinWriter.Parse(bytes).LodStart + 28), 4u); File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, rel), bytes); }

        var snapshot = f.Snapshot();
        var parts = ModelPartReader.Read(bytes)!.Parts.Where(p => p.Island < 0).Take(1).ToList();
        var prepared = MatcapStudio.Prepare(f.ModRoot, rel, gamePath, bytes, parts, MatcapChoice.OfImage(image), f.Env());
        string got = prepared.Ok ? MatcapWriter.Apply(f.ModRoot, prepared.Request!).Code : prepared.Code;
        Assert.Equal(code, got);
        Assert.Equal(snapshot, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-11")]
    public void A_refit_takes_the_matcap_and_undo_after_a_remake_leaves_the_remade_file()
    {
        using var f = new MatcapFixture();
        var rel = BodyRetargetWriter.Subfolder + "/x/top.mdl";
        var full = MatcapWriter.Full(f.ModRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), full);
        Assert.Equal("refit", MatcapPlan.Regenerated(f.ModRoot, rel));

        var bytes = File.ReadAllBytes(full);
        var parts = ModelPartReader.Read(bytes)!.Parts.Where(p => p.Label == "1.2").ToList();
        var prepared = MatcapStudio.Prepare(f.ModRoot, rel, MatcapFixture.ModelPath, bytes, parts,
                                            MatcapChoice.OfImage(f.Image("gem.tex")), f.Env());
        Assert.True(prepared.Ok, prepared.Message);
        var outcome = MatcapWriter.Apply(f.ModRoot, prepared.Request!);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.NotEqual(bytes, File.ReadAllBytes(full));

        // A remake writes the refit again from its source: Undo must not put the old refit back over it.
        var remade = (byte[])bytes.Clone();
        remade[^1] ^= 0x5A;
        File.WriteAllBytes(full, remade);
        var undo = MatcapWriter.Undo(f.ModRoot, rel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(remade, File.ReadAllBytes(full));
        var record = MatcapRecord.Read(f.ModRoot);
        Assert.Null(record.Model(rel));
        Assert.Empty(record.Materials);
        Assert.False(File.Exists(MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.BackupSubdir, rel)));
        Assert.DoesNotContain(Defaults(f.ModRoot).Keys, k => k.Contains("_mcap", StringComparison.Ordinal));
    }

    private const string RefitRel = BodyRetargetWriter.Subfolder + "/x/top.mdl";

    private static MatcapWriter.Outcome ApplyOn(MatcapFixture f, string rel, string image, out MatcapStudio.Prepared prepared)
    {
        var bytes = f.Current(rel);
        var part = ModelPartReader.Read(bytes)!.Parts.First(p => MatcapPlan.Tickable(p) && p.Island < 0);
        prepared = MatcapStudio.Prepare(f.ModRoot, rel, MatcapFixture.ModelPath, bytes, [part],
                                        MatcapChoice.OfImage(f.Image(image)), f.Env());
        Assert.True(prepared.Ok, prepared.Message);
        return MatcapWriter.Apply(f.ModRoot, prepared.Request!);
    }

    /// <summary>The author's file plus a refit of it, both at the model's game path (a Single group).</summary>
    private static MatcapFixture WithRefit()
    {
        var f = new MatcapFixture();
        var full = MatcapWriter.Full(f.ModRoot, RefitRel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), full);
        AddOption(f, "Size", ("Author", MatcapFixture.ModelRel), ("Refit", RefitRel));
        return f;
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-11")]
    public void Refits_and_the_authors_file_never_share_an_apply_group()
    {
        using var f = WithRefit();
        var refitBefore = f.Current(RefitRel);
        Assert.True(ApplyOn(f, MatcapFixture.ModelRel, "a.tex", out var onAuthor).Ok);
        Assert.Single(onAuthor.Request!.Models);
        Assert.Contains(onAuthor.SiblingsSkipped, s => PenumbraModMeta.SameFile(s, RefitRel));
        Assert.Equal(refitBefore, f.Current(RefitRel));

        Assert.True(ApplyOn(f, RefitRel, "b.tex", out var onRefit).Ok);
        Assert.Single(onRefit.Request!.Models);
        var record = MatcapRecord.Read(f.ModRoot);
        Assert.DoesNotContain(MatcapWriter.ApplyGroupOf(record, MatcapFixture.ModelRel), g => PenumbraModMeta.SameFile(g, RefitRel));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-11")]
    public void A_refit_remade_from_a_matcapped_file_never_strands_undo_or_draws_a_missing_material()
    {
        using var f = WithRefit();
        var author = f.Current(MatcapFixture.ModelRel);
        Assert.True(ApplyOn(f, RefitRel, "r.tex", out _).Ok);                  // M1 on the refit
        Assert.True(ApplyOn(f, MatcapFixture.ModelRel, "s.tex", out _).Ok);    // M2 on the author's file
        var m2 = MatcapRecord.Read(f.ModRoot).MaterialsOf(MatcapFixture.ModelRel).Single();

        // The remake: the refit written again from the author's file, so it now draws M2.
        var remade = f.Current(MatcapFixture.ModelRel);
        remade[^1] ^= 0x5A;
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, RefitRel), remade);

        // Undo on the refit: forgotten, not refused; the remade file is left alone; M1 goes, M2 stays.
        var undoRefit = MatcapWriter.Undo(f.ModRoot, RefitRel);
        Assert.True(undoRefit.Ok, undoRefit.Message);
        Assert.Equal(remade, f.Current(RefitRel));
        var record = MatcapRecord.Read(f.ModRoot);
        Assert.Null(record.Model(RefitRel));
        Assert.Equal(m2.Name, Assert.Single(record.Materials).Name);

        // Undo on the author's file: restored, but M2's files and redirects stay while the remade refit draws it.
        var undoAuthor = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(undoAuthor.Ok, undoAuthor.Message);
        Assert.Contains(RefitRel, undoAuthor.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(author, f.Current(MatcapFixture.ModelRel));
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, m2.File)));
        Assert.All(m2.GamePaths, p => Assert.True(Defaults(f.ModRoot).ContainsKey(p)));

        // Once no refit draws it, the next Forget removes it.
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, RefitRel), author);
        Assert.True(MatcapWriter.Forget(f.ModRoot, MatcapFixture.ModelRel).Ok);
        Assert.False(File.Exists(MatcapWriter.Full(f.ModRoot, m2.File)));
        Assert.Empty(MatcapRecord.Read(f.ModRoot).Materials);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-11")]
    public void A_matcap_on_a_remade_refit_backs_up_the_remade_file_not_the_old_one()
    {
        using var f = WithRefit();
        Assert.True(ApplyOn(f, RefitRel, "r.tex", out _).Ok);                  // M1 on the refit, backup = old refit
        Assert.True(ApplyOn(f, MatcapFixture.ModelRel, "s.tex", out _).Ok);    // M2 on the author's file
        var remade = f.Current(MatcapFixture.ModelRel);                         // remake: draws M2, one of ours
        remade[^1] ^= 0x5A;
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, RefitRel), remade);

        Assert.True(ApplyOn(f, RefitRel, "t.tex", out _).Ok);
        var undo = MatcapWriter.Undo(f.ModRoot, RefitRel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(remade, f.Current(RefitRel));                              // never the pre-remake refit
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-11")]
    public void A_matcapped_partner_hashes_as_the_refit_wrote_it()
    {
        using var f = WithRefit();
        var before = f.Current(RefitRel);
        var partner = new OutfitPartners.Partner("top", MatcapFixture.ModelRel, RefitRel);
        var layers = OutfitPartners.LayersOf(f.ModRoot, partner);
        Assert.True(ApplyOn(f, RefitRel, "r.tex", out _).Ok);
        Assert.NotEqual(before, f.Current(RefitRel));
        Assert.Equal(layers, OutfitPartners.LayersOf(f.ModRoot, partner));
        Assert.True(MatcapWriter.Undo(f.ModRoot, RefitRel).Ok);
        Assert.Equal(layers, OutfitPartners.LayersOf(f.ModRoot, partner));
    }

    private static string WriteJunk(MatcapFixture f)
    {
        var p = Path.Combine(f.Images, "junk.png");
        File.WriteAllText(p, "not an image");
        return p;
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-10")]
    public void Lined_up_siblings_changed_others_reported()
    {
        var other = SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl", new SyntheticModel.Sub(0)));
        using var f = new MatcapFixture();
        var sibling = SiblingOf(f, "model/top_rue.mdl", scaleX: 1.1f);
        AddOption(f, "Body", ("YAB", MatcapFixture.ModelRel), ("Rue", "model/top_rue.mdl"), ("Odd", "model/top_odd.mdl"), other);

        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(["model/top_odd.mdl"], p.SiblingsSkipped);
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok);
        var name = MatcapRecord.Read(f.ModRoot).Materials.Single().Name;
        Assert.Contains(name, SecondSkinWriter.MaterialNames(f.Current("model/top_rue.mdl")));
        Assert.DoesNotContain(name, SecondSkinWriter.MaterialNames(f.Current("model/top_odd.mdl")));
    }

    /// <summary>A sibling: the same model with every x scaled (another body's size of the garment).</summary>
    internal static byte[] SiblingOf(MatcapFixture f, string rel, float scaleX)
    {
        var bytes = f.Current();
        var src = SecondSkinWriter.Parse(bytes);
        for (int m = 0; m < src.MeshCount; m++)
        {
            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(bytes, mo);
            uint vbo = BitConverter.ToUInt32(bytes, mo + 20);
            byte stride = bytes[mo + 32];
            for (int v = 0; v < vc; v++)
            {
                int at = src.Vb + (int)vbo + v * stride;
                BitConverter.TryWriteBytes(bytes.AsSpan(at), BitConverter.ToSingle(bytes, at) * scaleX);
            }
        }
        var full = MatcapWriter.Full(f.ModRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return bytes;
    }

    /// <summary>A Single group publishing each file at the model's game path.</summary>
    internal static void AddOption(MatcapFixture f, string group, (string Name, string Rel) a, (string Name, string Rel) b,
                                   (string Name, string Rel)? c = null, byte[]? cBytes = null)
    {
        if (c is { } cc && cBytes != null)
        {
            var full = MatcapWriter.Full(f.ModRoot, cc.Rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, cBytes);
        }
        var meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(f.ModRoot, "meta.json")))!.AsObject();
        var opts = new System.Text.Json.Nodes.JsonArray();
        foreach (var o in new[] { a, b }.Concat(c is { } x ? [x] : []))
            opts.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["Name"] = o.Name,
                ["Files"] = new System.Text.Json.Nodes.JsonObject { [MatcapFixture.ModelPath] = o.Rel.Replace('/', '\\') },
            });
        meta["Groups"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["Name"] = group, ["Type"] = "Single", ["DefaultSettings"] = 0, ["Options"] = opts,
        });
        File.WriteAllText(Path.Combine(f.ModRoot, "meta.json"), meta.ToJsonString());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-46")]
    public void Sibling_group_undone_as_one_unit()
    {
        using var f = new MatcapFixture();
        var yab = f.Current();
        var rue = SiblingOf(f, "model/top_rue.mdl", 1.1f);
        AddOption(f, "Body", ("YAB", MatcapFixture.ModelRel), ("Rue", "model/top_rue.mdl"));
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var outcome = MatcapWriter.Undo(f.ModRoot, "model/top_rue.mdl");    // from the sibling
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(yab, f.Current());
        Assert.Equal(rue, f.Current("model/top_rue.mdl"));
        Assert.False(MatcapRecord.Exists(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-46")]
    public void Shared_keys_kept_while_any_model_names_them()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        SiblingOf(f, "model/top_rue.mdl", 1.1f);
        AddOption(f, "Body", ("YAB", MatcapFixture.ModelRel), ("Rue", "model/top_rue.mdl"));
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var mat = MatcapRecord.Read(f.ModRoot).Materials.Single();
        // The author updates the YAB file only: the Rue file still names the material.
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), original);
        Assert.True(MatcapWriter.Forget(f.ModRoot, MatcapFixture.ModelRel).Ok);
        Assert.Contains(Defaults(f.ModRoot).Keys, k => k.EndsWith(mat.Name.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(MatcapWriter.Full(f.ModRoot, mat.File)));
        Assert.Single(MatcapRecord.Read(f.ModRoot).Materials);
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-46")]
    public void Group_refused_whole_when_one_file_refuses()
    {
        using var f = new MatcapFixture();
        SiblingOf(f, "model/top_rue.mdl", 1.1f);
        AddOption(f, "Body", ("YAB", MatcapFixture.ModelRel), ("Rue", "model/top_rue.mdl"));
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var yab = f.Current();
        var rue = f.Current("model/top_rue.mdl");
        rue[SecondSkinWriter.Parse(rue).Vb] ^= 0x04;
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "model/top_rue.mdl"), rue);
        Assert.Equal(MatcapWriter.CodeEdited, MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel).Code);
        Assert.Equal(yab, f.Current());
        Assert.Equal(rue, f.Current("model/top_rue.mdl"));
    }
}

public class MatcapPerSourceMaterialTests
{
    private const string MatB = "chara/equipment/e6255/material/v0001/mt_c0201e6255_top_b.mtrl";
    private const string NormalB = "chara/equipment/e6255/texture/v01_c0201e6255_top_b_n.tex";

    [Fact]
    [Trait("Plan", "AM-7")]
    [Trait("Plan", "AC-matcaps-4")]
    public void Parts_from_two_materials_get_one_matcap_material_each_with_its_own_normal()
    {
        using var f = new MatcapFixture(extra: new Dictionary<string, (string, byte[])>
        {
            [MatB] = ("mat/top_b.mtrl", SyntheticMatcapMod.Gear(NormalB, alphaThreshold: 0.3f)),
        });
        var original = f.Current();
        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2", "2.1");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(2, p.Request!.Materials.Count);
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request).Ok);

        var record = MatcapRecord.Read(f.ModRoot);
        Assert.Equal(2, record.Materials.Count);
        var a = record.Materials.Single(m => m.Name.Contains("_top_a_mcap"));
        var b = record.Materials.Single(m => m.Name.Contains("_top_b_mcap"));
        Assert.Equal(MatcapFixture.Normal, a.Normal);
        Assert.Equal(NormalB, b.Normal);
        Assert.Equal(0.3f, b.AlphaThreshold);
        Assert.NotEqual(a.Source.Tex!.File, b.Source.Tex!.File);
        foreach (var m in record.Materials)
            Assert.Equal(m.Normal, MatcapMaterial.ReadHeader(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, m.File)))!
                                                 .TextureOf(MatcapMaterial.SamplerNormal));
        var names = SecondSkinWriter.MaterialNames(f.Current());
        Assert.Contains(a.Name, names);
        Assert.Contains(b.Name, names);

        Assert.True(MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel).Ok);
        Assert.Equal(original, f.Current());
        Assert.False(MatcapRecord.Exists(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "AM-7")]
    public void Not_enough_room_for_every_source_material_is_refused()
    {
        var meshes = Enumerable.Range(0, SecondSkinWriter.MaxMaterials - 1)
            .Select(i => new SyntheticModel.Mesh($"/mt_c0201e6255_top_{(char)('a' + i)}.mtrl",
                                                 new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: i * 20f)))
            .ToArray();
        using var f = new MatcapFixture(SyntheticModel.Build([], meshes));
        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.1", "2.1");
        Assert.Equal(ModelMaterialWriter.CodeMaterialCap, p.Code);
    }
}

public class ModelBackupOrderMatcapTests
{
    [Fact]
    [Trait("Plan", "AC-matcaps-7")]
    public void Matcap_feature_registered_with_predicate()
    {
        Assert.Contains(MatcapWriter.BackupSubdir, ModelBackupOrder.FeatureSubdirs());
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-24")]
    public void Brush_undo_not_blocked_after_author_update()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        // Brush first (older backup), then a matcap (newer): the brush is blocked while the matcap stands.
        MatcapWriterTests.Brush(f);
        var brushBackup = MatcapWriter.BackupPath(f.ModRoot, MeshVolumeService.BackupSubdir, MatcapFixture.ModelRel);
        File.SetLastWriteTimeUtc(brushBackup, DateTime.UtcNow.AddMinutes(-30));
        File.SetLastWriteTimeUtc(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), DateTime.UtcNow.AddMinutes(-20));
        f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.NotNull(ModelBackupOrder.LaterFeature(f.ModRoot, MeshVolumeService.BackupSubdir, MatcapFixture.ModelRel));

        // The author's update replaces the model: the matcap no longer stands, so it blocks nothing.
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), original);
        Assert.Null(ModelBackupOrder.LaterFeature(f.ModRoot, MeshVolumeService.BackupSubdir, MatcapFixture.ModelRel));
    }
}
