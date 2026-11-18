using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A hat-fit record ships INSIDE the mod (<c>Proteus/hatcompat.json</c>), so a third-party mod controls it. None of
/// its paths, nor a manifest redirect, may make the fit or its undo touch a file outside the mod.
/// </summary>
public partial class HatCompatServiceTests
{
    public static TheoryData<string> HostileForms => new() { "absolute", "climb" };

    private static string HostileRel(HostilePathKit kit, string form)
        => form == "absolute" ? kit.AbsoluteVictim : kit.ClimbToVictim();

    private static void WriteHatRecord(string modRoot, IEnumerable<string> files, IEnumerable<string>? replaced = null,
                                       IDictionary<string, int>? versions = null)
    {
        var dir = Path.Combine(modRoot, "Proteus");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, HatCompatService.RecordFile), JsonSerializer.Serialize(new
        {
            Files = files.ToList(),
            Replaced = (replaced ?? []).ToList(),
            Hidden = new Dictionary<string, List<string>>(),
            Versions = versions ?? new Dictionary<string, int>(),
        }));
    }

    private static void WriteHairMeta(string modRoot, params string[] siblingFiles)
    {
        var options = string.Join(",", siblingFiles.Select((f, i) =>
            $"{{\"Name\":\"o{i}\",\"Files\":{{\"{GamePath}\":{JsonSerializer.Serialize(f)}}}}}"));
        File.WriteAllText(Path.Combine(modRoot, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Bob\",\"Groups\":[{\"Name\":\"Style\",\"Type\":\"Single\",\"Options\":["
            + options + "]}],\"DefaultData\":{\"Files\":{" + $"\"{GamePath}\":\"{ModelRel}\"" + "}}}");
    }

    private static void PutModel(string modRoot, string rel, byte[] model)
    {
        var dest = Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, model);
    }

    // [AC-mod-path-containment-1][EDGE-mod-path-containment-1][EDGE-mod-path-containment-2] Sink.
    [Theory, MemberData(nameof(HostileForms))]
    public void RevertWithHostileRecordLeavesTheVictimUntouched(string form)
    {
        using var kit = new HostilePathKit();
        var rel = HostileRel(kit, form);
        kit.PlantOldBackup(HatCompatService.BackupSubdir, rel, "attacker"u8.ToArray());
        WriteHatRecord(kit.ModRoot, [rel], [rel]);

        HatCompatService.Revert(kit.ModRoot);
        HatCompatService.Revert(kit.ModRoot, rel);
        HatCompatService.RevertOrForgetAll(kit.ModRoot);
        HatCompatService.RevertOrForget(kit.ModRoot, rel, out _);

        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-2][EDGE-mod-path-containment-13]
    [Fact]
    public void ReadRecordDropsHostileEntries()
    {
        using var kit = new HostilePathKit();
        var bad = new[] { kit.AbsoluteVictim, kit.ClimbToVictim(), "a/../b.mdl", "C:victim.bin", @"\\srv\share\x.mdl" };
        WriteHatRecord(kit.ModRoot, bad.Append(ModelRel), bad.Append(ModelRel),
                       bad.Append(ModelRel).ToDictionary(k => k, _ => 1));

        var record = HatCompatService.ReadRecord(kit.ModRoot)!;

        Assert.Equal([ModelRel], record.Files);
        Assert.Equal([ModelRel], record.Replaced);
        Assert.Equal([ModelRel], record.Versions.Keys.ToList());
    }

    // [AC-mod-path-containment-3][EDGE-mod-path-containment-12] Sink.
    [Fact]
    public void MixedRecordRestoresTheLegitimateFile()
    {
        using var kit = new HostilePathKit();
        File.WriteAllText(Path.Combine(kit.ModRoot, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Bob\",\"Groups\":[],\"DefaultData\":{\"Files\":{"
            + $"\"{GamePath}\":\"{ModelRel}\"" + "}}}");
        var original = Hair();
        PutModel(kit.ModRoot, ModelRel, original);
        var parts = ModelPartReader.Read(original)!;
        Assert.True(HatCompatService.Apply(kit.ModRoot, original, Proposal(original, parts), [PartOf(parts, 1)]).Ok);

        // A hostile entry slipped into the record beside the real one.
        var hostile = kit.AbsoluteVictim;
        var recordPath = Path.Combine(kit.ModRoot, "Proteus", HatCompatService.RecordFile);
        var json = JsonDocument.Parse(File.ReadAllText(recordPath)).RootElement;
        var files = json.GetProperty("Files").EnumerateArray().Select(e => e.GetString()!).Append(hostile).ToList();
        WriteHatRecord(kit.ModRoot, files, [hostile],
                       new Dictionary<string, int> { [ModelRel] = HatCompatSolve.Version, [hostile] = 0 });

        var revert = HatCompatService.Revert(kit.ModRoot);

        Assert.True(revert.Ok, revert.Message);
        Assert.Equal(original, File.ReadAllBytes(kit.InMod(ModelRel)));
        Assert.False(File.Exists(recordPath) && File.ReadAllText(recordPath).Contains("victim"));
        kit.AssertVictimUntouched();
    }

    // [EDGE-mod-path-containment-11]
    [Fact]
    public void RecordWithOnlyHostileEntriesSaysNothingToUndo()
    {
        using var kit = new HostilePathKit();
        WriteHatRecord(kit.ModRoot, [kit.AbsoluteVictim, kit.ClimbToVictim()]);

        var revert = HatCompatService.Revert(kit.ModRoot);

        Assert.False(revert.Ok);
        Assert.Equal("This mod has no Proteus hat-compatibility edits to undo.", revert.Message);
        Assert.Equal(HatCompatService.Undone.Nothing, HatCompatService.RevertOrForget(kit.ModRoot, kit.AbsoluteVictim, out _));
        Assert.Equal(0, HatCompatService.PatchedCount(kit.ModRoot));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-1] Sink.
    [Theory, MemberData(nameof(HostileForms))]
    public void ForgetWithHostileRelLeavesTheVictimUntouched(string form)
    {
        using var kit = new HostilePathKit();
        var rel = HostileRel(kit, form);
        kit.PlantOldBackup(HatCompatService.BackupSubdir, rel, "attacker"u8.ToArray());
        WriteHatRecord(kit.ModRoot, [rel, ModelRel]);

        HatCompatService.Forget(kit.ModRoot, rel);

        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-4][EDGE-mod-path-containment-20] Sink: what the watcher does with a worn hairstyle's
    /// siblings — list them, then fit each one.
    [Theory, MemberData(nameof(HostileForms))]
    public void SiblingFilesIgnoresHostileRedirects(string form)
    {
        using var kit = new HostilePathKit();
        var hostile = HostileRel(kit, form);
        kit.SetVictimContent(Hair());
        WriteHairMeta(kit.ModRoot, hostile);
        PutModel(kit.ModRoot, ModelRel, Hair());

        var siblings = HatCompatService.SiblingFiles(kit.ModRoot, GamePath, ModelRel);

        Assert.Equal([ModelRel], siblings);
        foreach (var sibling in siblings)
        {
            var mdl = File.ReadAllBytes(Path.Combine(kit.ModRoot, sibling.Replace('/', Path.DirectorySeparatorChar)));
            var parts = ModelPartReader.Read(mdl)!;
            HatCompatService.Apply(kit.ModRoot, mdl, Proposal(mdl, parts) with { Rel = sibling }, [PartOf(parts, 1)]);
        }
        Assert.Equal(HatCompatService.Undone.Nothing, HatCompatService.RevertOrForget(kit.ModRoot, hostile, out _));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-4] Sink: switching "replace authored hat compat" off walks EVERY mod's record.
    [Theory, MemberData(nameof(HostileForms))]
    public void RestoreReplacedSkipsHostileReplacedEntries(string form)
    {
        using var kit = new HostilePathKit("Hostile");
        var hostile = HostileRel(kit, form);
        kit.PlantOldBackup(HatCompatService.BackupSubdir, hostile, "attacker"u8.ToArray());
        WriteHatRecord(kit.ModRoot, [hostile], [hostile]);

        var authored = AuthoredHair();
        using var good = new Mod(authored, kit.ModsRoot);
        var parts = ModelPartReader.Read(authored)!;
        Assert.True(HatCompatService.Apply(good.Root, authored, Proposal(authored, parts) with { Replaced = true }, []).Ok);

        var (restored, mods, _) = HatCompatService.RestoreReplaced(kit.ModsRoot);

        Assert.Equal(1, restored);
        Assert.Equal([good.Root], mods);
        Assert.Equal(authored, good.Model());
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-25] Sink: a caller handing Apply a hostile rel directly.
    [Theory, MemberData(nameof(HostileForms))]
    public void ApplyWithHostileProposalRelFailsAndWritesNothing(string form)
    {
        using var kit = new HostilePathKit();
        var hostile = HostileRel(kit, form);
        kit.SetVictimContent(Hair());
        var mdl = Hair();
        var parts = ModelPartReader.Read(mdl)!;

        var outcome = HatCompatService.Apply(kit.ModRoot, mdl, Proposal(mdl, parts) with { Rel = hostile }, [PartOf(parts, 1)]);

        Assert.False(outcome.Ok);
        Assert.False(File.Exists(Path.Combine(kit.ModRoot, "Proteus", HatCompatService.RecordFile)));
        kit.AssertVictimUntouched();
    }
}
