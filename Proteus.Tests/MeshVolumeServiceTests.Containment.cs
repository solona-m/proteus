using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <c>Proteus/meshvolume.json</c> ships inside the mod, and its redirects are the author's: neither may make the brush,
/// its wind materials or its undo touch a file outside the mod.
/// </summary>
public partial class MeshVolumeServiceTests
{
    public static TheoryData<string> HostileForms => new() { "absolute", "climb" };

    private static string HostileRel(HostilePathKit kit, string form)
        => form == "absolute" ? kit.AbsoluteVictim : kit.ClimbToVictim();

    private static void WriteBrushRecord(string modRoot, IEnumerable<string> files, IDictionary<string, int>? versions = null,
                                         IDictionary<string, float>? worst = null)
    {
        var dir = Path.Combine(modRoot, "Proteus");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MeshVolumeService.RecordFile), JsonSerializer.Serialize(new
        {
            Files = files.ToList(),
            Versions = versions ?? new Dictionary<string, int>(),
            Worst = worst ?? new Dictionary<string, float>(),
        }));
    }

    // [AC-mod-path-containment-1][EDGE-mod-path-containment-2] Sink.
    [Theory, MemberData(nameof(HostileForms))]
    public void RevertWithHostileFilesLeavesTheVictimUntouched(string form)
    {
        using var kit = new HostilePathKit();
        var rel = HostileRel(kit, form);
        kit.PlantOldBackup(MeshVolumeService.BackupSubdir, rel, "attacker"u8.ToArray());
        WriteBrushRecord(kit.ModRoot, [rel]);

        MeshVolumeService.Revert(kit.ModRoot);

        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-25] Sink: a wind material redirect pointing out of the mod
    // is skipped; the legitimate one beside it is edited and is the only one recorded.
    [Theory, MemberData(nameof(HostileForms))]
    public void WindMaterialsSkipHostileRedirects(string form)
    {
        // The redirect's file has to look like a material for the old code to take it.
        using var kit = new HostilePathKit(victimName: "victim.mtrl");
        var mtrl = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gear_top_a.mtrl"));
        kit.SetVictimContent(mtrl);
        const string good = "chara/equipment/e0141/material/v0001/mt_c0201e0141_top_b.mtrl";
        Directory.CreateDirectory(Path.GetDirectoryName(kit.InMod(good))!);
        File.WriteAllBytes(kit.InMod(good), mtrl);

        var outcome = MeshVolumeService.ApplyWindMaterials(kit.ModRoot, ["/mt_c0201e0141_top_b.mtrl"],
        [
            new PenumbraModMeta.Redirect("chara/equipment/e0141/material/v0001/mt_c0201e0141_top_b.mtrl", HostileRel(kit, form), ""),
            new PenumbraModMeta.Redirect("chara/equipment/e0141/material/v0001/mt_c0201e0141_top_b.mtrl", good, ""),
        ]);

        Assert.Equal(1, outcome.Changed);
        Assert.NotEqual(mtrl, File.ReadAllBytes(kit.InMod(good)));
        Assert.Equal([good], MeshVolumeService.ReadRecord(kit.ModRoot)!.Files);
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-2][EDGE-mod-path-containment-13]
    [Fact]
    public void ReadRecordDropsHostileFilesAndKeys()
    {
        using var kit = new HostilePathKit();
        const string good = "chara/equipment/e0001/model/c0201e0001_top.mdl";
        var bad = new[] { kit.AbsoluteVictim, kit.ClimbToVictim(), "a/../b.mdl", "x.mdl:s" };
        WriteBrushRecord(kit.ModRoot, bad.Append(good),
                         bad.Append(good).ToDictionary(k => k, _ => 3), bad.Append(good).ToDictionary(k => k, _ => 0.01f));

        var record = MeshVolumeService.ReadRecord(kit.ModRoot)!;

        Assert.Equal([good], record.Files);
        Assert.Equal([good], record.Versions.Keys.ToList());
        Assert.Equal([good], record.Worst.Keys.ToList());
    }

    // [EDGE-mod-path-containment-5] The reader's own canonical form trims a leading slash: what is left is inside the mod.
    [Fact]
    public void LeadingSlashRelIsTrimmedAndContained()
    {
        using var kit = new HostilePathKit();
        WriteBrushRecord(kit.ModRoot, ["/x.mdl"]);

        Assert.Equal(["x.mdl"], MeshVolumeService.ReadRecord(kit.ModRoot)!.Files);
    }

    // [AC-mod-path-containment-3] Sink.
    [Fact]
    public void MixedRecordRevertsLegitimateModel()
    {
        using var kit = new HostilePathKit();
        const string good = "chara/equipment/e0001/model/c0201e0001_top.mdl";
        byte[] original = [1, 2, 3, 4], brushed = [5, 6, 7, 8];
        Directory.CreateDirectory(Path.GetDirectoryName(kit.InMod(good))!);
        File.WriteAllBytes(kit.InMod(good), brushed);
        var backup = Path.Combine(kit.ModRoot, "Proteus", MeshVolumeService.BackupSubdir, good.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(backup, original);
        WriteBrushRecord(kit.ModRoot, [good, kit.AbsoluteVictim]);

        var outcome = MeshVolumeService.Revert(kit.ModRoot);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(original, File.ReadAllBytes(kit.InMod(good)));
        kit.AssertVictimUntouched();
    }
}
