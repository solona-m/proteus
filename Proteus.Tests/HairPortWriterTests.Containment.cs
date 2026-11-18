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
    // outside the mod — the attacker wrote both — and the port is out of date, so the automatic save remakes it.
    [Fact]
    public void HostileRecordFileMatchingManifestIsDroppedAndAutoSaveWritesInside()
    {
        using var kit = new HostilePathKit();
        var hostile = kit.ClimbToVictim();
        using var mod = new Mod("[" + Group("Cut", "Single",
                                    ("Long", new() { [Source] = "long/c0801h0130_hir.mdl", [Target] = hostile })) + "]",
                                root: kit.ModRoot);
        mod.Put("long/c0801h0130_hir.mdl", HairModel(801));
        WritePortRecord(kit.ModRoot, PortEntry(hostile));

        var outcome = HairPortWriter.Save(kit.ModRoot, [Write(mod, "Cut", "Long")], auto: true);

        // Nothing outside the mod: either a fresh port inside it, or the option's redirect stands and is refused.
        if (outcome.Ok)
            Assert.All(HairPortRecord.Read(kit.ModRoot).Ports, p => Assert.StartsWith(HairPortPlan.Folder + "/", p.File));
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
