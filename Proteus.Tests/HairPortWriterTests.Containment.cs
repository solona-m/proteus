using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// <c>Proteus/hairport.json</c> ships inside the mod and its redirects are the author's: a port's recorded file must
/// never send a remake — the automatic one on equip included — outside the mod.
/// </summary>
public partial class HairPortWriterTests
{
    private static void WritePortRecord(string modRoot, params object[] ports)
    {
        var dir = Path.Combine(modRoot, "Proteus");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, HairPortRecord.RecordFile), JsonSerializer.Serialize(new { Version = 1, Ports = ports, Declined = new string[0] }));
    }

    private static object PortEntry(string file, string sourceFile = "long/c0801h0130_hir.mdl", Dictionary<string, string>? materials = null) => new
    {
        Group = "Cut", Option = "Long", SourceGamePath = Source, SourceFile = sourceFile, SourceSha8 = "abcd1234",
        TargetGamePath = Target, File = file, Materials = materials ?? new Dictionary<string, string>(), Est = 0, SkeletonFrom = 0,
        Auto = true, PortVersion = 0,
    };

    // [AC-mod-path-containment-4][EDGE-mod-path-containment-19] Sink: the record and the manifest agree on a port file
    // outside the mod — the attacker wrote both — and the port is out of date, so the automatic save would remake it.
    // The record entry is dropped; the manifest's own redirect still owns the target, so the save is refused as covered
    // (a deviation from the plan's "writes a fresh port": the safer result, as the author's redirect stands).
    [Fact]
    public void HostileRecordFileMatchingManifestIsDroppedAndAutoSaveIsRefused()
    {
        using var kit = new HostilePathKit();
        var hostile = kit.ClimbToVictim();
        using var mod = new Mod("[" + Group("Cut", "Single",
                                    ("Long", new() { [Source] = "long/c0801h0130_hir.mdl", [Target] = hostile })) + "]",
                                root: kit.ModRoot);
        mod.Put("long/c0801h0130_hir.mdl", HairModel(801));
        WritePortRecord(kit.ModRoot, PortEntry(hostile));

        Assert.Empty(HairPortRecord.Read(kit.ModRoot).Ports);

        var outcome = HairPortWriter.Save(kit.ModRoot, [Write(mod, "Cut", "Long")], auto: true);

        Assert.False(outcome.Ok);
        Assert.Equal(HairPortWriter.Covered(Target), outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(kit.ModRoot, HairPortPlan.Folder)));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-25] A material copy of the port's own is named from the
    // model's material name, which the mod's author wrote: one climbing out of the mod refuses the save before anything
    // is written.
    [Fact]
    public void MaterialNameClimbingOutRefusesBeforeAnyWrite()
    {
        using var kit = new HostilePathKit();
        using var mod = TwoCuts(kit.ModRoot);
        var climb = "/mt_c0201/" + string.Concat(Enumerable.Repeat("../", 12)) + "victim.mtrl";
        var gamePath = HairMaterialPath.GamePath(201, Id, climb);
        Assert.NotNull(gamePath);

        var outcome = HairPortWriter.Save(kit.ModRoot,
            [Write(mod, "Cut", "Long", HairModel(201, material: climb), new HairPortWriter.MaterialWrite(gamePath!, null, [7, 7, 7]))],
            auto: true);

        Assert.False(outcome.Ok);
        Assert.Contains("is not a path inside the mod", outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(kit.ModRoot, HairPortPlan.Folder)));
        Assert.Empty(HairPortRecord.Read(kit.ModRoot).Ports);
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-2]
    [Fact]
    public void RecordDropsHostileSourceFileAndMaterials()
    {
        using var kit = new HostilePathKit();
        using var mod = TwoCuts(kit.ModRoot);
        Assert.True(HairPortWriter.Save(kit.ModRoot, [Write(mod, "Cut", "Long")], auto: true).Ok);
        var file = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;

        WritePortRecord(kit.ModRoot, PortEntry(file, sourceFile: kit.AbsoluteVictim));
        Assert.Empty(HairPortRecord.Read(kit.ModRoot).Ports);

        WritePortRecord(kit.ModRoot, PortEntry(file, materials: new()
        {
            ["chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_a.mtrl"] = kit.ClimbToVictim(),
            ["chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_b.mtrl"] = "Hair Ports/x.mtrl",
        }));
        var entry = Assert.Single(HairPortRecord.Read(kit.ModRoot).Ports);
        Assert.Equal(["Hair Ports/x.mtrl"], entry.Materials.Values.ToList());

        // A shipped record may hold anything: no entry, an entry with no file, a material with no file.
        var dir = Path.Combine(kit.ModRoot, "Proteus");
        File.WriteAllText(Path.Combine(dir, HairPortRecord.RecordFile),
            "{\"Version\":1,\"Ports\":[null,{\"File\":\"\"}," + JsonSerializer.Serialize(PortEntry(file)).TrimEnd('}')
            + ",\"Materials\":{\"k\":null}}],\"Declined\":[]}");
        var real = Assert.Single(HairPortRecord.Read(kit.ModRoot).Ports);
        Assert.Equal(file, real.File);
        Assert.Empty(real.Materials);
    }

    // [AC-mod-path-containment-3] A legitimate port is still remade where it is.
    [Fact]
    public void LegitimatePortStillRemadeInPlace()
    {
        using var kit = new HostilePathKit();
        using var mod = TwoCuts(kit.ModRoot);
        Assert.True(HairPortWriter.Save(kit.ModRoot, [Write(mod, "Cut", "Long")], auto: true).Ok);
        var file = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;
        var again = HairModel(201, material: "/mt_c0201h0130_hir_b.mtrl");

        Assert.True(HairPortWriter.Save(kit.ModRoot, [Write(mod, "Cut", "Long", again)], auto: true).Ok);

        Assert.Equal(file, HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.Equal(again, mod.Get(file));
        kit.AssertVictimUntouched();
    }

    private static Mod TwoCuts(string root)
    {
        var mod = new Mod("[" + Group("Cut", "Single",
                              ("Long", new() { [Source] = "long/c0801h0130_hir.mdl" }),
                              ("Short", new() { [Source] = "short/c0801h0130_hir.mdl" })) + "]", root: root);
        mod.Put("long/c0801h0130_hir.mdl", HairModel(801));
        mod.Put("short/c0801h0130_hir.mdl", HairModel(801));
        return mod;
    }
}
