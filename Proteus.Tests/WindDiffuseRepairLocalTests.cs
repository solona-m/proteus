using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Services.ShaderUpgradePlan;

namespace Proteus.Tests;

/// <summary>
/// #65 on real data, read-only: every installed mod's wind-brushed material whose backup is the legacy diffuse form and
/// whose file is now character.shpk, classified as a recipe match (would be repaired) or a near miss (left alone), with
/// why. Measures how many damaged files the exact-recipe detector leaves dark. Writes nothing.
/// </summary>
[Trait("Speed", "Slow")]
public class WindDiffuseRepairLocalTests(ITestOutputHelper o)
{
    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "AC-issue-sweep-b-8")]
    [Trait("Plan", "AC-issue-sweep-b-9")]
    [Trait("Plan", "EDGE-issue-sweep-b-23")]
    public void Near_miss_report()
    {
        string root = LocalData.Path(LocalData.Mods);
        int matches = 0, misses = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var recordPath = Path.Combine(dir, "Proteus", MeshVolumeService.RecordFile);
            if (!File.Exists(recordPath) || MeshVolumeService.ReadRecord(dir) is not { } record) continue;
            var proteus = Path.Combine(dir, "Proteus");
            var stamps = Directory.EnumerateFiles(proteus, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);
            foreach (var rel in record.Files.Where(f => f.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)))
            {
                var file = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                var backup = Path.Combine(dir, "Proteus", MeshVolumeService.BackupSubdir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file) || !File.Exists(backup)) continue;
                byte[] b = File.ReadAllBytes(backup), c = File.ReadAllBytes(file);
                if (FormOf(b) != LegacyForm.Diffuse
                    || !string.Equals(CharacterShaderUpgrade.ShaderOf(c), CharacterShaderUpgrade.Character, StringComparison.OrdinalIgnoreCase))
                    continue;
                var why = MeshVolumeService.WindRecipeMismatch(c, b);
                if (why == null) matches++; else misses++;
                o.WriteLine($"{(why == null ? "MATCH" : "near miss")}  {Path.GetFileName(dir)}/{rel}{(why == null ? "" : "  — " + why)}");
            }
            foreach (var (f, t) in stamps) Assert.Equal(t, File.GetLastWriteTimeUtc(f));   // read-only: nothing under Proteus/ written
        }
        o.WriteLine($"{matches} would be repaired, {misses} near misses left as they are");
    }
}
