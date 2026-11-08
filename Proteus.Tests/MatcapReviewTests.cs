using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>The writer's crash, backup and naming guards, and the apply group (code review round 1).</summary>
public class MatcapWriterGuardTests
{
    private static Dictionary<string, string> Defaults(string modRoot) =>
        PenumbraModMeta.TryReadDefaultData(modRoot)?.Files ?? new Dictionary<string, string>();

    private static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-36")]
    public void Crash_after_the_model_write_leaves_a_record_undo_can_act_on()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        var crash = Path.Combine(f.Root, "crash");
        // The disk as the game left it when it died just after the model was written (before the final record write).
        MatcapWriter.AfterStep = s => { if (s == "model") { CopyDir(f.ModRoot, crash); throw new IOException("game closed"); } };
        try { MatcapWriter.Apply(f.ModRoot, p.Request!); }
        finally { MatcapWriter.AfterStep = null; }
        Directory.Delete(f.ModRoot, true);
        CopyDir(crash, f.ModRoot);

        Assert.NotEqual(original, f.Current());                          // the model names the matcap
        Assert.NotEmpty(MatcapRecord.Read(f.ModRoot).Shared.Mask?.File ?? "");   // shared files named before placing
        Assert.Equal(MatcapWriter.State.Ok, MatcapWriter.Detect(f.ModRoot).Values.Single());
        var undo = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(original, f.Current());
        Assert.DoesNotContain(Defaults(f.ModRoot).Keys, k => k.Contains("mcap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-24")]
    public void A_stale_backup_is_retired_never_restored_over_the_authors_update()
    {
        using var f = new MatcapFixture();
        Assert.True(f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2").Ok);
        // The record is lost (deleted by hand) while the backup stays; then the author ships an update.
        File.Delete(Path.Combine(f.ModRoot, SidecarDiscoveryService.SidecarSubdir, MatcapWriter.RecordFile));
        var update = SyntheticModel.Build([],
            [new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl",
                new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 1f),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 10f, OffsetZ: 1f)),
             new SyntheticModel.Mesh("/mt_c0201e6255_top_b.mtrl",
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 50f, OffsetZ: 1f))], SyntheticModel.V6, (IReadOnlyList<string>?)null);
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, MatcapFixture.ModelRel), update);

        Assert.True(f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2").Ok);
        var undo = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(undo.Ok, undo.Message);
        Assert.Equal(update, f.Current());
        Assert.True(File.Exists(MatcapWriter.BackupPath(f.ModRoot, MatcapWriter.SupersededSubdir, MatcapFixture.ModelRel)));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-26")]
    public void A_hand_edited_file_kept_by_undo_is_never_written_over()
    {
        using var f = new MatcapFixture();
        Assert.True(f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2").Ok);
        var first = MatcapRecord.Read(f.ModRoot).Materials.Single();
        var kept = MatcapWriter.Full(f.ModRoot, first.File);
        File.AppendAllText(kept, "hand edit");
        var keptBytes = File.ReadAllBytes(kept);
        var undo = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);
        Assert.True(undo.Ok, undo.Message);
        Assert.True(File.Exists(kept));

        Assert.True(f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2").Ok);
        var second = MatcapRecord.Read(f.ModRoot).Materials.Single();
        Assert.NotEqual(first.File, second.File, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(keptBytes, File.ReadAllBytes(kept));
    }

    [Fact]
    [Trait("Plan", "AC-matcaps-7")]
    [Trait("Plan", "EDGE-matcaps-46")]
    public void Apply_group_is_the_closure_over_shared_materials_only()
    {
        var r = new MatcapRecord();
        r.Models.AddRange(new[] { "top.mdl", "top_b.mdl", "legs.mdl" }.Select(m => new MatcapRecord.ModelEntry { File = m }));
        r.Materials.Add(new MatcapRecord.MaterialEntry { Name = "/a_mcapabcdef1.mtrl", Meshes = [new() { Model = "top.mdl" }, new() { Model = "top_b.mdl" }] });
        r.Materials.Add(new MatcapRecord.MaterialEntry { Name = "/b_mcapabcdef1.mtrl", Meshes = [new() { Model = "legs.mdl" }] });
        Assert.Equal(["top.mdl", "top_b.mdl"], MatcapWriter.ApplyGroupOf(r, "top_b.mdl").Order(StringComparer.Ordinal));
        Assert.Equal(["legs.mdl"], MatcapWriter.ApplyGroupOf(r, "legs.mdl"));
        Assert.Single(MatcapWriter.GroupMaterials(r, "legs.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-62")]
    public void Two_source_materials_two_entries_each_own_normal_undo_removes_both()
    {
        using var f = new MatcapFixture();
        var original = f.Current();
        var p = f.Prepare(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2", "2.1");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(2, p.Request!.Materials.Count);
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request).Ok);
        Assert.Equal(2, MatcapRecord.Read(f.ModRoot).Materials.Count);
        Assert.True(MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel).Ok);
        Assert.Equal(original, f.Current());
        Assert.False(MatcapRecord.Exists(f.ModRoot));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-59")]
    public void Vanilla_face_copy_entry_undo_removes_edit_files()
    {
        const string FacePath = "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl";
        var face = MatcapPlanTests.SyntheticFace();
        using var f = new MatcapFixture(folder: "Horns (face f0001, c1401) - " + RefitModService.EditQualifier,
                                        extra: new Dictionary<string, (string, byte[])> { [FacePath] = (FacePath, face) });
        var parts = ModelPartReader.Read(face)!.Parts;
        var p = MatcapStudio.Prepare(f.ModRoot, FacePath, FacePath, face, [parts.Single(x => x.Label == "2.1")],
                                     MatcapChoice.OfImage(f.Image("gem.tex")), f.Env());
        Assert.True(p.Ok, p.Message);
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok);

        var undo = MatcapWriter.Undo(f.ModRoot, FacePath, gp => gp == FacePath ? face : null);
        Assert.True(undo.Ok, undo.Message);
        Assert.False(File.Exists(MatcapWriter.Full(f.ModRoot, FacePath)));      // the game's own again: left the mod
        Assert.DoesNotContain(FacePath, Defaults(f.ModRoot).Keys);
        Assert.Contains(MatcapFixture.ModelPath, Defaults(f.ModRoot).Keys);     // nothing else touched
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-59")]
    public void Undo_in_an_ordinary_mod_keeps_the_face_file()
    {
        const string FacePath = "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl";
        var face = MatcapPlanTests.SyntheticFace();
        using var f = new MatcapFixture(folder: "Some Face Mod",
                                        extra: new Dictionary<string, (string, byte[])> { [FacePath] = (FacePath, face) });
        var parts = ModelPartReader.Read(face)!.Parts;
        var p = MatcapStudio.Prepare(f.ModRoot, FacePath, FacePath, face, [parts.Single(x => x.Label == "2.1")],
                                     MatcapChoice.OfImage(f.Image("gem.tex")), f.Env());
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok);
        Assert.True(MatcapWriter.Undo(f.ModRoot, FacePath, gp => gp == FacePath ? face : null).Ok);
        Assert.Equal(face, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, FacePath)));
        Assert.Contains(FacePath, Defaults(f.ModRoot).Keys);
    }
}

/// <summary>The line after an Apply about which file the character draws (EDGE-47, EDGE-58).</summary>
public sealed class MatcapDrawnStatusTests : IDisposable
{
    private readonly string mods = Path.Combine(Path.GetTempPath(), "proteus-drawn-" + Guid.NewGuid().ToString("N"));
    private const string FacePath = "chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl";
    private const string TopPath = "chara/equipment/e6255/model/c0201e6255_top.mdl";

    public void Dispose()
    {
        try { Directory.Delete(mods, true); } catch { }
    }

    private string Mod(string name) { var d = Path.Combine(mods, name); Directory.CreateDirectory(d); return d; }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-47")]
    public void Own_file_drawn_is_quiet_another_mods_file_warns()
    {
        var mine = Mod("Mine");
        Assert.Null(MatcapPanelModel.DrawnStatus(mine, TopPath, ["model/top.mdl"], Path.Combine(mine, "model", "top.mdl"), mods, null));
        var other = Mod("Other");
        var line = MatcapPanelModel.DrawnStatus(mine, TopPath, ["model/top.mdl"], Path.Combine(other, "model", "top.mdl"), mods, null);
        Assert.NotNull(line);
        Assert.Contains("top.mdl", line);
        // Game data (outside the mods root) says nothing.
        Assert.Null(MatcapPanelModel.DrawnStatus(mine, TopPath, ["model/top.mdl"], "chara/x.mdl", mods, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-58")]
    public void Face_through_the_overlay_is_neutral_unless_another_face_mod_feeds_it()
    {
        var mine = Mod("Face Mod");
        var managed = Mod(SidecarDiscoveryService.ManagedModDir);
        var drawn = Path.Combine(managed, "models", "facelr_x.mdl");
        var written = Path.Combine(mine, "face.mdl");
        Assert.Equal(Strings.Matcap.DrawnThroughFace, MatcapPanelModel.DrawnStatus(mine, FacePath, ["face.mdl"], drawn, mods, null));
        Assert.Equal(Strings.Matcap.DrawnThroughFace, MatcapPanelModel.DrawnStatus(mine, FacePath, ["face.mdl"], drawn, mods, written));
        var other = Path.Combine(Mod("Other Face"), "face.mdl");
        var line = MatcapPanelModel.DrawnStatus(mine, FacePath, ["face.mdl"], drawn, mods, other);
        Assert.NotEqual(Strings.Matcap.DrawnThroughFace, line);
        Assert.Contains("Other Face/face.mdl", line);
    }
}

/// <summary>Real game data: a split that appends a mesh read back stream by stream, and the face rewrite over split faces.</summary>
public class MatcapReadbackLocalTests(ITestOutputHelper output)
{
    /// <summary>Every vertex's uv0 in every mesh, NaN anywhere is a failure.</summary>
    internal static void AssertNoNaNUv(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var s = src.S;
        Span<float> tmp = stackalloc float[4];
        for (int m = 0; m < src.MeshCount; m++)
        {
            int mo = src.MeshStart + m * 36;
            ushort vc = BitConverter.ToUInt16(s, mo);
            foreach (var e in src.Decls[m].Where(e => e.Usage == SecondSkinWriter.UseUV))
            {
                uint vbo = BitConverter.ToUInt32(s, mo + 20 + e.Stream * 4);
                byte stride = s[mo + 32 + e.Stream];
                for (int v = 0; v < vc; v++)
                {
                    SecondSkinWriter.ReadTyped(s, src.Vb + (int)vbo + v * stride + e.Offset, e.Type, tmp);
                    Assert.False(float.IsNaN(tmp[0]) || float.IsNaN(tmp[1]), $"mesh {m} vertex {v}: uv NaN");
                }
            }
        }
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-matcaps-4")]
    [Trait("Plan", "EDGE-matcaps-54")]
    public void A_split_that_adds_a_mesh_keeps_every_stream_and_reads_back()
    {
        var data = ModelMaterialWriterLocalTests.Game();
        int added = 0;
        foreach (var (path, kind) in new[] { ("chara/equipment/e6200/model/c0201e6200_top.mdl", MatcapPlan.Kind.Gear),
                                             ("chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl", MatcapPlan.Kind.Face) })
        {
            var bytes = data.GetFile(path)!.Data;
            var read = ModelPartReader.Read(bytes)!;
            var tickable = read.Parts.Where(p => p.Island < 0 && MatcapPlan.Tickable(p, kind, read.AttributeNames) && !MatcapPlan.IsOnMatcap(p)).ToList();
            // A part sharing its mesh with another, so the split appends a mesh (a lone part would be rebound whole).
            var part = tickable.FirstOrDefault(p => read.Parts.Count(q => q.Island < 0 && q.Mesh == p.Mesh) > 1) ?? tickable[0];
            var result = ModelMaterialWriter.MoveToMaterial(bytes, [part], MatcapPlan.NewName(part.Material, "abcdef", SecondSkinWriter.MaterialNames(bytes)));
            added += result.MeshesAdded;
            var cut = ModelLodTrimmer.LodCount(bytes) > 1 ? ModelLodTrimmer.KeepLod0(bytes, out _)! : bytes;

            // Every drawn triangle keeps every vertex stream byte for byte (normals, UVs, weights, colours), copies too.
            Assert.Equal(ModelMaterialWriterTests.Drawn(cut, allElements: true).Select(d => d[(d.IndexOf('|') + 1)..]).Order(StringComparer.Ordinal),
                         ModelMaterialWriterTests.Drawn(result.Model, allElements: true).Select(d => d[(d.IndexOf('|') + 1)..]).Order(StringComparer.Ordinal));

            // No Lumina read-back: its MdlFile throws on ModelLodTrimmer's plain LOD0 cut of this very model (a file that
            // loads in game), and its model builder on the game's own Dawntrail faces — it cannot judge these files.
            // The tables are read back by the part reader and the parser instead, appended mesh included.
            var after = SecondSkinWriter.Parse(result.Model);
            Assert.Equal(SecondSkinWriter.Parse(cut).MeshCount + result.MeshesAdded, after.MeshCount);
            Assert.Contains(after.MatNames, n => n.Contains("_mcap", StringComparison.Ordinal));
            Assert.NotNull(ModelPartReader.Read(result.Model));
            output.WriteLine($"{path}: part {part.Label}, meshes added {result.MeshesAdded}, {after.MeshCount} meshes");

            // EDGE-54: the face overlay's rewrite of the face's skin after the split converts, and no uv turns NaN.
            if (kind == MatcapPlan.Kind.Face)
            {
                var convert = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".")
                    .UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace, unmirror: true)!;
                var rewritten = FaceUvRewriter.RewriteFaceUv0(result.Model, n => n.Contains("_fac_a", StringComparison.Ordinal), convert, out _);
                Assert.NotNull(rewritten);
                AssertNoNaNUv(rewritten!);
                Assert.Equal(SecondSkinWriter.MaterialNames(result.Model), SecondSkinWriter.MaterialNames(rewritten!));
            }
        }
        Assert.True(added > 0, "no split appended a mesh");
    }

    [Fact]
    [Trait("Plan", "EDGE-matcaps-55")]
    public void Overlay_on_a_horn_material_after_a_split_rewrites_over_collapsed_triangles_without_NaN()
    {
        // fac_b holds a horn submesh and an untagged one: moving the horn appends M' and leaves collapsed triangles in M.
        var face = MatcapPlanTests.SyntheticFace();
        var read = ModelPartReader.Read(face)!;
        var result = ModelMaterialWriter.MoveToMaterial(face, [read.Parts.Single(p => p.Label == "3.1")], "/mt_c1401f0001_fac_b_mcapabcdef1.mtrl");
        Assert.True(result.MeshesAdded > 0);
        var convert = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".")
            .UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace, unmirror: true)!;
        byte[]? rewritten = null;
        var ex = Record.Exception(() => rewritten = FaceUvRewriter.RewriteFaceUv0(result.Model,
            n => n.TrimStart('/') == "mt_c1401f0001_fac_b.mtrl", convert, out _));   // matched by exact name, as the compositor does
        Assert.Null(ex);
        Assert.NotNull(rewritten);
        AssertNoNaNUv(rewritten!);
    }
}