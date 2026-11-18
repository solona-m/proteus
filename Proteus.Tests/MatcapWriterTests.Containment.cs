using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <c>Proteus/matcaps.json</c> ships inside the mod, so a third-party mod controls it. None of its paths may make a
/// matcap's apply, change, undo or forget touch a file outside the mod.
/// </summary>
public partial class MatcapWriterTests
{
    public static TheoryData<string> HostileForms => new() { "absolute", "climb" };

    private static string HostileRel(HostilePathKit kit, string form)
        => form == "absolute" ? kit.AbsoluteVictim : kit.ClimbToVictim();

    private const string HostileMat = "/mt_c0201e6255_top_a_mcapabcdef1.mtrl";

    private static void WriteMeta(string modRoot) =>
        File.WriteAllText(Path.Combine(modRoot, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Bob\",\"Groups\":[],\"DefaultData\":{\"Files\":{}}}");

    private static void WriteMatcapRecord(string modRoot, object record)
    {
        var dir = Path.Combine(modRoot, "Proteus");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MatcapWriter.RecordFile), JsonSerializer.Serialize(record));
    }

    private static object HostileMaterialRecord(string file, string? texFile) => new
    {
        Version = 1,
        Token = "abcdef",
        Models = new[] { new { File = "model/top.mdl", GamePath = "chara/x.mdl", WrittenSha8s = new string[0] } },
        Materials = new[]
        {
            new
            {
                Name = HostileMat, File = file, Sha8 = "", GamePaths = new string[0], Normal = "",
                Meshes = new[] { new { Model = "model/top.mdl", Mesh = 0, From = 0 } },
                Source = new { Kind = "image", Tex = texFile == null ? null : new { File = texFile, GamePath = "chara/x/t.tex", Sha8 = "" } },
            },
        },
    };

    // [AC-mod-path-containment-1][EDGE-mod-path-containment-1][EDGE-mod-path-containment-2] Sink.
    [Theory, MemberData(nameof(HostileForms))]
    public void UndoWithHostileMaterialAndTexFilesLeavesTheVictimUntouched(string form)
    {
        using var kit = new HostilePathKit();
        WriteMeta(kit.ModRoot);
        var rel = HostileRel(kit, form);

        WriteMatcapRecord(kit.ModRoot, HostileMaterialRecord(rel, rel));
        try { MatcapWriter.Undo(kit.ModRoot, "model/top.mdl"); } catch (IOException) { }
        kit.AssertVictimUntouched();

        WriteMatcapRecord(kit.ModRoot, HostileMaterialRecord("Proteus Matcaps/x.mtrl", rel));
        try { MatcapWriter.Forget(kit.ModRoot, "model/top.mdl"); } catch (IOException) { }
        kit.AssertVictimUntouched();
    }

    // [EDGE-mod-path-containment-21] Sink: the shared mask goes with the last material; an empty Sha8 used to mean
    // "not edited", so the file it names was deleted.
    [Theory, MemberData(nameof(HostileForms))]
    public void HostileSharedMaskWithEmptyShaIsNotDeleted(string form)
    {
        using var kit = new HostilePathKit();
        WriteMeta(kit.ModRoot);
        var rel = HostileRel(kit, form);
        WriteMatcapRecord(kit.ModRoot, new
        {
            Version = 1, Token = "abcdef",
            Models = new[] { new { File = "model/top.mdl", GamePath = "chara/x.mdl", WrittenSha8s = new string[0] } },
            Materials = new object[0],
            Shared = new
            {
                Mask = new { File = rel, GamePath = MatcapWriter.MaskGamePath("abcdef"), Sha8 = "" },
                FlatNormal = new { File = rel, GamePath = MatcapWriter.FlatNormalGamePath("abcdef"), Sha8 = "" },
            },
        });

        try { MatcapWriter.Undo(kit.ModRoot, "model/top.mdl"); } catch (IOException) { }

        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-2][EDGE-mod-path-containment-21]
    [Fact]
    public void RecordReadDropsHostilePathsOnly()
    {
        using var kit = new HostilePathKit();
        var bad = kit.AbsoluteVictim;
        WriteMatcapRecord(kit.ModRoot, new
        {
            Version = 1, Token = "abcdef",
            Models = new[]
            {
                new { File = "model/top.mdl", GamePath = "chara/a.mdl", WrittenSha8s = new string[0] },
                new { File = bad, GamePath = "chara/b.mdl", WrittenSha8s = new string[0] },
            },
            Materials = new object[]
            {
                // kept: a library matcap has no baked texture
                new { Name = "/mt_a_mcapabcdef1.mtrl", File = "Proteus Matcaps/mt_a_mcapabcdef1.mtrl", Sha8 = "", GamePaths = new string[0], Normal = "",
                      Meshes = new[] { new { Model = "model/top.mdl", Mesh = 0, From = 0 } }, Source = new { Kind = "library" } },
                new { Name = "/mt_b_mcapabcdef1.mtrl", File = bad, Sha8 = "", GamePaths = new string[0], Normal = "",
                      Meshes = new[] { new { Model = "model/top.mdl", Mesh = 0, From = 0 } }, Source = new { Kind = "library" } },
                new { Name = "/mt_c_mcapabcdef1.mtrl", File = "Proteus Matcaps/c.mtrl", Sha8 = "", GamePaths = new string[0], Normal = "",
                      Meshes = new[] { new { Model = "model/top.mdl", Mesh = 0, From = 0 } },
                      Source = new { Kind = "image", Tex = new { File = kit.ClimbToVictim(), GamePath = "chara/t.tex", Sha8 = "" } } },
                new { Name = "/mt_d_mcapabcdef1.mtrl", File = "Proteus Matcaps/d.mtrl", Sha8 = "", GamePaths = new string[0], Normal = "",
                      Meshes = new[] { new { Model = "../../x.mdl", Mesh = 0, From = 0 } }, Source = new { Kind = "library" } },
            },
            Shared = new
            {
                Mask = new { File = bad, GamePath = "chara/m.tex", Sha8 = "" },
                FlatNormal = new { File = "Proteus Matcaps/flatnormal_abcdef.tex", GamePath = "chara/f.tex", Sha8 = "" },
            },
        });

        var record = MatcapRecord.Read(kit.ModRoot);

        Assert.False(record.Unreadable);
        Assert.Equal(["model/top.mdl"], record.Models.Select(m => m.File));
        Assert.Equal(["/mt_a_mcapabcdef1.mtrl"], record.Materials.Select(m => m.Name));
        Assert.Null(record.Shared.Mask);
        Assert.NotNull(record.Shared.FlatNormal);
    }

    // [AC-mod-path-containment-3] A record as Proteus really writes it: material names carry a leading '/', game paths
    // are not mod paths. Nothing is dropped.
    [Fact]
    public void RealShapedRecordDropsNothing()
    {
        using var kit = new HostilePathKit();
        WriteMatcapRecord(kit.ModRoot, new
        {
            Version = 1, Token = "399f5d",
            Models = new[] { new { File = "chara/equipment/e6003/model/c0201e6003_top.mdl", GamePath = "chara/equipment/e6003/model/c0201e6003_top.mdl",
                                   WrittenSha8s = new[] { "0123abcd" }, Lod0Cut = true } },
            Materials = new object[]
            {
                new { Name = "/mt_c0201e6003_top_a_mcap399f5d1.mtrl", File = "Proteus Matcaps/mt_c0201e6003_top_a_mcap399f5d1.mtrl", Sha8 = "aa11bb22",
                      GamePaths = new[] { "chara/equipment/e6003/material/v0001/mt_c0201e6003_top_a_mcap399f5d1.mtrl" },
                      Normal = "chara/equipment/e6003/texture/v01_c0201e6003_top_n.tex",
                      Meshes = new[] { new { Model = "chara/equipment/e6003/model/c0201e6003_top.mdl", Mesh = 3, From = 0 } },
                      Source = new { Kind = "library", Library = new { Mod = "Lib", Name = "Lib", Website = "", Group = "G", Option = "O",
                                                                       Texture = "chara/common/texture/lib_a.tex" } } },
                new { Name = "/mt_c0201e6003_top_b_mcap399f5d2.mtrl", File = "Proteus Matcaps/mt_c0201e6003_top_b_mcap399f5d2.mtrl", Sha8 = "cc33dd44",
                      GamePaths = new[] { "chara/equipment/e6003/material/v0001/mt_c0201e6003_top_b_mcap399f5d2.mtrl" },
                      Normal = "chara/equipment/e6003/texture/v01_c0201e6003_top_n.tex",
                      Meshes = new[] { new { Model = "chara/equipment/e6003/model/c0201e6003_top.mdl", Mesh = 4, From = 1 } },
                      Source = new { Kind = "image", Image = "gem.png", ImageSha8 = "ee55ff66",
                                     Tex = new { File = "Proteus Matcaps/mt_c0201e6003_top_b_mcap399f5d2.tex",
                                                 GamePath = "chara/common/texture/proteus/matcap/399f5d/mt_c0201e6003_top_b_mcap399f5d2.tex", Sha8 = "1234abcd" } } },
            },
            Shared = new
            {
                Mask = new { File = "Proteus Matcaps/mask_399f5d.tex", GamePath = "chara/common/texture/proteus/matcap/399f5d/mask.tex", Sha8 = "77889900" },
            },
        });

        var record = MatcapRecord.Read(kit.ModRoot);

        Assert.False(record.Unreadable);
        Assert.Equal("399f5d", record.Token);
        Assert.Single(record.Models);
        Assert.Equal(2, record.Materials.Count);
        Assert.NotNull(record.Materials[1].Source.Tex);
        Assert.NotNull(record.Shared.Mask);
    }

    // [EDGE-mod-path-containment-30] The token goes into file names: an invalid one is read as "no token yet" and Apply
    // takes the folder's own.
    [Theory]
    [InlineData("../x", false)]
    [InlineData("ABCDEFG", false)]
    [InlineData("12345", false)]
    [InlineData("ABCDEF", true)]
    public void InvalidTokenIsReadAsEmptyAndRefilledOnApply(string token, bool valid)
    {
        using var f = new MatcapFixture();
        WriteMatcapRecord(f.ModRoot, new { Version = 1, Token = token, Models = new object[0], Materials = new object[0] });

        Assert.Equal(valid ? token : "", MatcapRecord.Read(f.ModRoot).Token);

        var outcome = f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2");
        Assert.True(outcome.Ok, outcome.Message);
        var record = MatcapRecord.Read(f.ModRoot);
        Assert.Equal(valid ? token : MatcapPlan.TokenFor("Gear Mod"), record.Token);
        Assert.StartsWith(MatcapWriter.FilesFolder + "/", record.Shared.Mask!.File);
    }

    // [AC-mod-path-containment-6] Sink.
    [Theory, MemberData(nameof(HostileForms))]
    public void ChangeWithHostileTexFileLeavesTheVictimUntouched(string form)
    {
        using var kit = new HostilePathKit();
        WriteMeta(kit.ModRoot);
        WriteMatcapRecord(kit.ModRoot, HostileMaterialRecord("Proteus Matcaps/x.mtrl", HostileRel(kit, form)));
        var temp = Path.Combine(kit.Sandbox, "baked.tex");
        File.WriteAllBytes(temp, SyntheticMatcapMod.Tex(3));

        var outcome = MatcapWriter.Change(kit.ModRoot, HostileMat,
            new MatcapRecord.SourceRef { Kind = MatcapRecord.SourceRef.KindImage, Image = "x.png" }, null, temp);

        Assert.False(outcome.Ok);
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-7][EDGE-mod-path-containment-25] A caller handing Apply a
    // target outside the mod — a model rel climbing out, or a material name that would name a stream — is refused
    // before anything is written.
    [Theory]
    [InlineData("model")]
    [InlineData("material")]
    public void ApplyWithHostileTargetsRefusesBeforeAnyWrite(string which)
    {
        using var kit = new HostilePathKit();
        WriteMeta(kit.ModRoot);
        var victimBytes = File.ReadAllBytes(kit.Victim);
        var modelRel = which == "model" ? kit.ClimbToVictim() : "model/top.mdl";
        if (which != "model") { Directory.CreateDirectory(kit.InMod("model")); File.WriteAllBytes(kit.InMod(modelRel), victimBytes); }
        var name = which == "material" ? "/mt_x:evil.mtrl" : HostileMat;
        var mask = Path.Combine(kit.Sandbox, "mask.tex");
        File.WriteAllBytes(mask, SyntheticMatcapMod.Tex(1));
        var req = new MatcapWriter.ApplyRequest("abcdef",
            [new MatcapWriter.MaterialPlan(name, [1, 2, 3], ["chara/x/mt_x_mcapabcdef1.mtrl"], "", null,
                new MatcapRecord.SourceRef { Kind = MatcapRecord.SourceRef.KindLibrary }, null, [(modelRel, 0, 0)])],
            [new MatcapWriter.ModelEdit(modelRel, "chara/x.mdl", victimBytes, [9, 9, 9], false)],
            mask, null);

        var outcome = MatcapWriter.Apply(kit.ModRoot, req);

        Assert.Equal(MatcapWriter.CodeFailed, outcome.Code);
        Assert.False(File.Exists(Path.Combine(kit.ModRoot, "Proteus", MatcapWriter.RecordFile)));
        Assert.False(Directory.Exists(Path.Combine(kit.ModRoot, "Proteus", MatcapWriter.BackupSubdir)));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-3][EDGE-mod-path-containment-12] Sink.
    [Fact]
    public void MixedRecordStillUndoesTheLegitimateMatcap()
    {
        using var kit = new HostilePathKit();
        using var f = new MatcapFixture();
        var before = f.Current();
        Assert.True(f.Apply(MatcapChoice.OfImage(f.Image("gem.tex")), "1.2").Ok);

        var path = Path.Combine(f.ModRoot, "Proteus", MatcapWriter.RecordFile);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Materials"]!.AsArray().Add(JsonNode.Parse(JsonSerializer.Serialize(new
        {
            Name = "/mt_c0201e6255_top_b_mcapffffff1.mtrl", File = kit.AbsoluteVictim, Sha8 = "", GamePaths = new string[0], Normal = "",
            Meshes = new[] { new { Model = MatcapFixture.ModelRel, Mesh = 0, From = 0 } },
            Source = new { Kind = "image", Tex = new { File = kit.AbsoluteVictim, GamePath = "chara/x/t.tex", Sha8 = "" } },
        })));
        File.WriteAllText(path, json.ToJsonString());

        var outcome = MatcapWriter.Undo(f.ModRoot, MatcapFixture.ModelRel);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(before, f.Current());
        kit.AssertVictimUntouched();
    }
}
