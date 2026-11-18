using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <c>Proteus/parts.json</c> ships inside the mod, and its redirects are the author's: neither may make the switches or
/// their undo touch a file outside the mod.
/// </summary>
public partial class MeshToggleServiceTests
{
    public static TheoryData<string> HostileForms => new() { "absolute", "climb" };

    private static string HostileRel(HostilePathKit kit, string form)
        => form == "absolute" ? kit.AbsoluteVictim : kit.ClimbToVictim();

    private static byte[] TwoPartTop() => SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0)));

    private static void WriteFrockMeta(string modRoot) =>
        File.WriteAllText(Path.Combine(modRoot, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Frock\",\"Groups\":[],\"DefaultData\":{\"Files\":{"
            + $"\"{GamePath}\":\"{ModelRel}\"" + "}}}");

    private static void PutModel(HostilePathKit kit, byte[] model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(kit.InMod(ModelRel))!);
        File.WriteAllBytes(kit.InMod(ModelRel), model);
    }

    private static MeshToggleService.Outcome WriteIn(string modRoot, ModelParts parts, System.Collections.Generic.IReadOnlyList<PenumbraModMeta.Redirect> siblings,
                                                     params MeshToggleService.Plan[] toggles)
        => MeshToggleService.Write(modRoot, new PenumbraModMeta.Redirect(GamePath, ModelRel, ""), parts, toggles, siblings,
                                   path => path == "chara/equipment/e0043/e0043.imc" ? Imc() : null);

    private static string RecordPath(string modRoot) => Path.Combine(modRoot, "Proteus", MeshToggleService.RecordFile);

    // [AC-mod-path-containment-1][EDGE-mod-path-containment-2] Sink.
    [Theory, MemberData(nameof(HostileForms))]
    public void RevertWithHostileFilesLeavesTheVictimUntouched(string form)
    {
        using var kit = new HostilePathKit();
        WriteFrockMeta(kit.ModRoot);
        var rel = HostileRel(kit, form);
        kit.PlantOldBackup(MeshToggleService.BackupSubdir, rel, "attacker"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(kit.ModRoot, "Proteus"));
        File.WriteAllText(RecordPath(kit.ModRoot), JsonSerializer.Serialize(new
        {
            Items = new[] { new { GroupName = "Toggles (Body)", SetId = 43, Slot = "Body", Files = new[] { rel }, Toggles = new { } } },
        }));

        MeshToggleService.Revert(kit.ModRoot);

        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-20] Sink: a redirect at the same game path pointing out of
    // the mod is a "sibling" the old code patched too.
    [Theory, MemberData(nameof(HostileForms))]
    public void ApplyIgnoresHostileSiblingRedirects(string form)
    {
        using var kit = new HostilePathKit();
        WriteFrockMeta(kit.ModRoot);
        var model = TwoPartTop();
        PutModel(kit, model);
        kit.SetVictimContent(model);
        var parts = ModelPartReader.Read(model)!;
        var hostile = HostileRel(kit, form);

        var result = WriteIn(kit.ModRoot, parts,
            [new PenumbraModMeta.Redirect(GamePath, ModelRel, ""), new PenumbraModMeta.Redirect(GamePath, hostile, "")],
            new MeshToggleService.Plan("Bow", [parts.Parts[1]]));

        Assert.True(result.Ok, result.Message);
        Assert.Equal([ModelRel], result.Files);
        Assert.NotEqual(model, File.ReadAllBytes(kit.InMod(ModelRel)));
        Assert.DoesNotContain("victim", File.ReadAllText(RecordPath(kit.ModRoot)));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-2][EDGE-mod-path-containment-14]
    [Fact]
    public void LegacyRecordHostileFileIsDropped()
    {
        using var kit = new HostilePathKit();
        WriteFrockMeta(kit.ModRoot);
        var model = TwoPartTop();
        PutModel(kit, model);
        var parts = ModelPartReader.Read(model)!;
        var written = WriteIn(kit.ModRoot, parts, PenumbraModMeta.ReadAllRedirects(kit.ModRoot),
                              new MeshToggleService.Plan("Bow", [parts.Parts[1]]));
        Assert.True(written.Ok, written.Message);

        // Rewritten in the old, pre-per-item shape, with a hostile file beside the real one.
        var item = JsonNode.Parse(File.ReadAllText(RecordPath(kit.ModRoot)))!["Items"]![0]!;
        File.WriteAllText(RecordPath(kit.ModRoot), JsonSerializer.Serialize(new
        {
            GroupName = item["GroupName"]!.GetValue<string>(),
            Files = new[] { ModelRel, kit.AbsoluteVictim, kit.ClimbToVictim() },
            Toggles = item["Toggles"]!.Deserialize<System.Collections.Generic.Dictionary<string, string>>(),
        }));

        var record = MeshToggleService.ReadRecord(kit.ModRoot)!;
        Assert.Equal([ModelRel], record.Items.Single().Files);

        var revert = MeshToggleService.Revert(kit.ModRoot);
        Assert.True(revert.Ok, revert.Message);
        Assert.Equal(model, File.ReadAllBytes(kit.InMod(ModelRel)));
        Assert.DoesNotContain(MeshToggleService.GroupNameFor("Body"), File.ReadAllText(Path.Combine(kit.ModRoot, "meta.json")));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-3][EDGE-mod-path-containment-12] Sink.
    [Fact]
    public void MixedRecordRestoresLegitimateModel()
    {
        using var kit = new HostilePathKit();
        WriteFrockMeta(kit.ModRoot);
        var model = TwoPartTop();
        PutModel(kit, model);
        var parts = ModelPartReader.Read(model)!;
        Assert.True(WriteIn(kit.ModRoot, parts, PenumbraModMeta.ReadAllRedirects(kit.ModRoot),
                            new MeshToggleService.Plan("Bow", [parts.Parts[1]])).Ok);

        var json = JsonNode.Parse(File.ReadAllText(RecordPath(kit.ModRoot)))!;
        json["Items"]![0]!["Files"]!.AsArray().Add(kit.AbsoluteVictim);
        File.WriteAllText(RecordPath(kit.ModRoot), json.ToJsonString());

        var revert = MeshToggleService.Revert(kit.ModRoot);

        Assert.True(revert.Ok, revert.Message);
        Assert.Equal(model, File.ReadAllBytes(kit.InMod(ModelRel)));
        kit.AssertVictimUntouched();
    }
}
