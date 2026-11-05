using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Proteus.Services;
using Xunit;
using static Proteus.Services.ShaderUpgradePlan;
using Kit = Proteus.Tests.ShaderUpgradeTestKit;

namespace Proteus.Tests;

/// <summary>
/// #65: from build 1290 to 1424 a wind save moved a diffuse/specular (characterlegacy) material to character.shpk by the
/// index/mask recipe, without its diffuse, so it drew dark; the gear shader upgrade only touches characterlegacy, so it
/// never repaired them. The upgrade's walk of worn mods now puts back the brush's backup of exactly such a file — only
/// one byte-equal to what that wind save made, constants' values aside — so the repair is lossless.
/// </summary>
public sealed class WindDiffuseRepairTests : IDisposable
{
    private const string Rel = "red/top.mtrl";
    private readonly string mod = Path.Combine(Path.GetTempPath(), "proteus-wind65-" + Guid.NewGuid().ToString("N"));

    public WindDiffuseRepairTests() => Directory.CreateDirectory(mod);
    public void Dispose() { try { Directory.Delete(mod, true); } catch (IOException) { } }

    private static byte[] Constants(byte[] m, float scale, float max)
        => TextureLoader.EnsureConstantValues(
               TextureLoader.EnsureConstantValues(m, MeshVolumeService.VertexMovementScaleId, scale).data,
               MeshVolumeService.VertexMovementMaxLengthId, max).data;

    /// <summary>Recipe A: a 1290–1424 save on the pristine legacy file.</summary>
    private static byte[] RecipeA(byte[] legacy, float scale, float max) => Constants(CharacterShaderUpgrade.Upgrade(legacy)!, scale, max);

    /// <summary>Recipe B: a 1289 save added the constants to the legacy file; a 1290 save then upgraded it and patched them.</summary>
    private static byte[] RecipeB(byte[] legacy, float scale, float max)
        => Constants(CharacterShaderUpgrade.Upgrade(Constants(legacy, 1f, 1f))!, scale, max);

    private string File_(string rel) => Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
    private string Backup(string rel) => Path.Combine(mod, "Proteus", MeshVolumeService.BackupSubdir, rel.Replace('/', Path.DirectorySeparatorChar));
    private string RecordPath => Path.Combine(mod, "Proteus", MeshVolumeService.RecordFile);

    /// <summary>A mod as an older wind save left it: the darkened file, the brush's backup, and its record.</summary>
    private void Brushed(byte[] current, params string[] rels)
    {
        foreach (var rel in rels)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_(rel))!);
            File.WriteAllBytes(File_(rel), current);
            Directory.CreateDirectory(Path.GetDirectoryName(Backup(rel))!);
            File.WriteAllBytes(Backup(rel), Kit.FormB());
            File.SetLastWriteTimeUtc(Backup(rel), DateTime.UtcNow.AddMinutes(-10));
        }
        File.WriteAllText(RecordPath, JsonSerializer.Serialize(new MeshVolumeRecord { Files = [.. rels] }));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-8")]
    [Trait("Plan", "EDGE-issue-sweep-b-25")]
    public void Brush_recipe_file_is_restored_and_forgotten()
    {
        foreach (var (scale, max) in new[] { (100f, 1f), (37f, 0.4f) })
        {
            Brushed(RecipeA(Kit.FormB(), scale, max), Rel);
            Assert.Equal([Rel], MeshVolumeService.RepairWindDiffuse(mod, [Rel]));
            Assert.Equal(Kit.FormB(), File.ReadAllBytes(File_(Rel)));
            Assert.Equal(LegacyForm.Diffuse, FormOf(File.ReadAllBytes(File_(Rel))));
            Assert.False(File.Exists(RecordPath));   // the last rel: record and folder go
            Assert.False(Directory.Exists(Path.Combine(mod, "Proteus", MeshVolumeService.BackupSubdir)));
        }
        // With a second brushed file the record and the other backup stay; only this rel's backup goes.
        Brushed(RecipeA(Kit.FormB(), 100f, 1f), Rel, "blue/top.mtrl");
        Assert.Equal([Rel], MeshVolumeService.RepairWindDiffuse(mod, [Rel]));
        Assert.Equal(["blue/top.mtrl"], MeshVolumeService.ReadRecord(mod)!.Files);
        Assert.False(File.Exists(Backup(Rel)));
        Assert.True(File.Exists(Backup("blue/top.mtrl")));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-8")]
    [Trait("Plan", "EDGE-issue-sweep-b-31")]
    public void Saved_by_1289_then_1290_is_detected()
    {
        var a = RecipeA(Kit.FormB(), 100f, 1f);
        var b = RecipeB(Kit.FormB(), 100f, 1f);
        Assert.True(MeshVolumeService.IsWindDamagedDiffuse(b, Kit.FormB()));
        Console.WriteLine($"recipes A and B {(a.AsSpan().SequenceEqual(b) ? "give the same bytes" : "differ")}");
        Brushed(b, Rel);
        Assert.Equal([Rel], MeshVolumeService.RepairWindDiffuse(mod, [Rel]));
        Assert.Equal(Kit.FormB(), File.ReadAllBytes(File_(Rel)));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-9")]
    [Trait("Plan", "EDGE-issue-sweep-b-22")]
    [Trait("Plan", "EDGE-issue-sweep-b-23")]
    public void Anything_but_the_recipe_is_left_alone()
    {
        var damaged = RecipeA(Kit.FormB(), 100f, 1f);
        Assert.NotNull(MeshVolumeService.WindRecipeMismatch(RecipeA(Kit.FormA(), 100f, 1f), Kit.FormA()));   // index/mask backup
        Assert.NotNull(MeshVolumeService.WindRecipeMismatch(Kit.FormB(), Kit.FormB()));   // still legacy
        var edited = (byte[])damaged.Clone();
        edited[GearMaterialWriter.ColorTableStart(edited) + 2] ^= 0x10;   // one colour changed since
        Assert.Contains("differs", MeshVolumeService.WindRecipeMismatch(edited, Kit.FormB()));
        Assert.Contains("no movement constants", MeshVolumeService.WindRecipeMismatch(CharacterShaderUpgrade.Upgrade(Kit.FormB())!, Kit.FormB()));
        Assert.False(MeshVolumeService.IsWindDamagedDiffuse(edited, Kit.FormB()));
        // Not in the record, or no backup: nothing.
        Brushed(damaged, "blue/top.mtrl");
        Directory.CreateDirectory(Path.GetDirectoryName(File_(Rel))!);
        File.WriteAllBytes(File_(Rel), damaged);
        Assert.Empty(MeshVolumeService.RepairWindDiffuse(mod, [Rel]));
        File.Delete(Backup("blue/top.mtrl"));
        Assert.Empty(MeshVolumeService.RepairWindDiffuse(mod, ["blue/top.mtrl"]));
        Assert.Equal(damaged, File.ReadAllBytes(File_("blue/top.mtrl")));
        // An edited file: untouched.
        Brushed(edited, Rel);
        Assert.Empty(MeshVolumeService.RepairWindDiffuse(mod, [Rel]));
        Assert.Equal(edited, File.ReadAllBytes(File_(Rel)));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-9")]
    public void Later_feature_backup_blocks_the_repair()
    {
        var damaged = RecipeA(Kit.FormB(), 100f, 1f);
        Brushed(damaged, Rel);
        var later = Path.Combine(mod, "Proteus", ShaderUpgradeWriter.BackupSubdir, "red", "top.mtrl");
        Directory.CreateDirectory(Path.GetDirectoryName(later)!);
        File.WriteAllBytes(later, damaged);
        File.SetLastWriteTimeUtc(later, DateTime.UtcNow);
        Assert.Empty(MeshVolumeService.RepairWindDiffuse(mod, [Rel]));
        Assert.Equal(damaged, File.ReadAllBytes(File_(Rel)));
        Assert.True(File.Exists(Backup(Rel)));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-b-26")]
    public void Failed_restore_changes_nothing()
    {
        var damaged = RecipeA(Kit.FormB(), 100f, 1f);
        Brushed(damaged, Rel);
        var warnings = new List<string>();
        using (new FileStream(File_(Rel), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Empty(MeshVolumeService.RepairWindDiffuse(mod, [Rel], warnings.Add));
        Assert.Contains(Rel, Assert.Single(warnings));
        Assert.Equal(damaged, File.ReadAllBytes(File_(Rel)));
        Assert.Equal([Rel], MeshVolumeService.ReadRecord(mod)!.Files);
        Assert.True(File.Exists(Backup(Rel)));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-b-25")]
    public void A_failed_record_write_keeps_every_backup()
    {
        var damaged = RecipeA(Kit.FormB(), 100f, 1f);
        Brushed(damaged, Rel, "blue/top.mtrl");
        var warnings = new List<string>();
        IReadOnlyList<string> repaired;
        using (new FileStream(RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read))   // readable, not replaceable
            repaired = MeshVolumeService.RepairWindDiffuse(mod, [Rel], warnings.Add);
        Assert.Equal([Rel], repaired);
        Assert.Equal(Kit.FormB(), File.ReadAllBytes(File_(Rel)));   // restored all the same
        Assert.True(File.Exists(Backup(Rel)));                       // but no backup goes while the record names it
        Assert.True(File.Exists(Backup("blue/top.mtrl")));
        Assert.Equal([Rel, "blue/top.mtrl"], MeshVolumeService.ReadRecord(mod)!.Files);
        Assert.Contains("backups are kept", Assert.Single(warnings));
        // A malformed file is judged in a guard of its own: one bad material never aborts the rest of the run.
        var src = File.ReadAllText(Path.Combine(ProteusDir(), "Services", "MeshVolumeService.cs"));
        Assert.Contains("catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException", src);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-8")]
    [Trait("Plan", "EDGE-issue-sweep-b-24")]
    [Trait("Plan", "EDGE-issue-sweep-b-27")]
    public void Watcher_wiring()
    {
        var src = File.ReadAllText(Path.Combine(ProteusDir(), "Services", "ShaderUpgrade", "ShaderUpgradeWatcher.cs"));
        int run = src.IndexOf("private void Run(RunKind kind, bool decline)", StringComparison.Ordinal);
        int undoReturn = src.IndexOf("if (kind == RunKind.UndoWorn) { RunUndo(worn, inputs, decline); return; }", run, StringComparison.Ordinal);
        int repair = src.IndexOf("MeshVolumeService.RepairWindDiffuse(", run, StringComparison.Ordinal);
        int candidates = src.IndexOf("Candidates(worn.Materials, inputs);", run, StringComparison.Ordinal);
        Assert.True(undoReturn > run && repair > undoReturn && candidates > repair, "repair after the undo return, before planning");
        var block = src[repair..candidates];
        Assert.Contains("repaired.Add(g.Key);", block);
        Assert.Contains("aborted = true; break;", src[undoReturn..candidates]);   // a stop mid-repair still reloads what was restored
        Assert.Contains("changed.Keys.Union(repaired, StringComparer.OrdinalIgnoreCase)", src[candidates..]);
        Assert.Contains("if (stopped", src[undoReturn..candidates]);
        var undo = src[src.IndexOf("private void RunUndo(", StringComparison.Ordinal)..];
        Assert.DoesNotContain("RepairWindDiffuse", undo[..undo.IndexOf("private static string KindName", StringComparison.Ordinal)]);
        Assert.DoesNotContain("ChatGui", block);
    }

    private static string ProteusDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");
}
