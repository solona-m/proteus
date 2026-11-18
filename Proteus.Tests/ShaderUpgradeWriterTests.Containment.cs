using System.IO;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <c>Proteus/shaderupgrade.json</c> ships inside the mod, so a third-party mod controls it: its mask path must never
/// reach a file outside <c>Shader Upgrade/</c>, nor its material path one outside the mod.
/// </summary>
public partial class ShaderUpgradeWriterTests
{
    public static TheoryData<string> HostileForms => new() { "absolute", "climb" };

    private static string HostileRel(HostilePathKit kit, string form)
        => form == "absolute" ? kit.AbsoluteVictim : kit.ClimbToVictim();

    private const string MatRel = "mat/a.mtrl";

    private static void WriteUpgradeMeta(string modRoot) =>
        File.WriteAllText(Path.Combine(modRoot, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Bob\",\"Groups\":[],\"DefaultData\":{\"Files\":{}}}");

    private static void WriteUpgradeRecord(string modRoot, params object[] files)
    {
        var dir = Path.Combine(modRoot, "Proteus");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ShaderUpgradeWriter.RecordFile), JsonSerializer.Serialize(new { Files = files, Declined = new string[0] }));
    }

    private static object Entry(string rel, string sourceSha, string upgradedSha, string? maskRel) => new
    {
        Rel = rel, Form = "diffuse", SourceSha8 = sourceSha, UpgradedSha8 = upgradedSha, Version = 1, Pending = false,
        Mask = maskRel == null ? null : new { Rel = maskRel, GamePath = "chara/x/m.tex", Owners = new object[0] },
    };

    private static void Put(HostilePathKit kit, string rel, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(kit.InMod(rel))!);
        File.WriteAllBytes(kit.InMod(rel), bytes);
    }

    // [AC-mod-path-containment-1][EDGE-mod-path-containment-2] Sink: forgetting an entry deletes its mask when nothing
    // names it — and the record says where the mask is.
    [Theory, MemberData(nameof(HostileForms))]
    public void ForgetWithHostileMaskRelLeavesTheVictimUntouched(string form)
    {
        using var kit = new HostilePathKit();
        WriteUpgradeMeta(kit.ModRoot);
        byte[] material = [1, 2, 3, 4];
        Put(kit, MatRel, material);
        WriteUpgradeRecord(kit.ModRoot, Entry(MatRel, ShaderUpgradePlan.Sha8(material), "0000000000000000", HostileRel(kit, form)));

        try { ShaderUpgradeWriter.RevertOrForget(kit.ModRoot, MatRel, decline: false, out _); } catch (IOException) { }

        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-2][EDGE-mod-path-containment-15] A mask inside the mod but outside Shader Upgrade/ is the
    // author's own texture, never ours to delete: the entry naming it is dropped whole.
    [Fact]
    public void MaskOutsideMaskFolderDropsTheEntry()
    {
        using var kit = new HostilePathKit();
        WriteUpgradeMeta(kit.ModRoot);
        byte[] material = [1, 2, 3, 4], author = [9, 9, 9];
        Put(kit, MatRel, material);
        Put(kit, "chara/x/author.tex", author);
        WriteUpgradeRecord(kit.ModRoot, Entry(MatRel, ShaderUpgradePlan.Sha8(material), "0000000000000000", "chara/x/author.tex"));

        Assert.Empty(ShaderUpgradeWriter.ReadRecord(kit.ModRoot)!.Files);
        ShaderUpgradeWriter.RevertOrForget(kit.ModRoot, MatRel, decline: false, out _);

        Assert.Equal(author, File.ReadAllBytes(kit.InMod("chara/x/author.tex")));
    }

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-25] A write handed a material outside the mod is left as a
    // failed write — not a reason to walk again, which would only meet it again.
    [Fact]
    public void WriteOneWithUnsafeRelIsLeftWriteFailedWithoutRewalk()
    {
        using var kit = new HostilePathKit();
        WriteUpgradeMeta(kit.ModRoot);
        var rel = kit.ClimbToVictim();
        var write = new ShaderUpgradePlan.MaterialWrite(rel, ["chara/x/a.mtrl"], LegacyForm.IndexMask,
                                                        ShaderUpgradePlan.Sha8(File.ReadAllBytes(kit.Victim)), [7, 7, 7], null);

        var outcome = ShaderUpgradeWriter.Apply(kit.ModRoot, [write], () => true, (_, _, _, _, _) => true, TexEncoding.Uncompressed);

        Assert.Empty(outcome.Written);
        Assert.Equal([(rel, LeftReason.WriteFailed)], outcome.Left);
        Assert.False(outcome.Rewalk);
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-3] Sink.
    [Fact]
    public void LegitimateEntryStillUndoesAfterHostileSiblingDropped()
    {
        using var kit = new HostilePathKit();
        WriteUpgradeMeta(kit.ModRoot);
        byte[] original = [1, 1, 1], upgraded = [2, 2, 2];
        Put(kit, MatRel, upgraded);
        Put(kit, "Proteus/" + ShaderUpgradeWriter.BackupSubdir + "/" + MatRel, original);
        WriteUpgradeRecord(kit.ModRoot,
            Entry(MatRel, ShaderUpgradePlan.Sha8(original), ShaderUpgradePlan.Sha8(upgraded), null),
            Entry("mat/b.mtrl", "1111111111111111", "2222222222222222", kit.AbsoluteVictim));

        var undone = ShaderUpgradeWriter.RevertOrForget(kit.ModRoot, MatRel, decline: false, out var message);

        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, undone);
        Assert.Equal(original, File.ReadAllBytes(kit.InMod(MatRel)));
        Assert.DoesNotContain("victim", File.Exists(Path.Combine(kit.ModRoot, "Proteus", ShaderUpgradeWriter.RecordFile))
            ? File.ReadAllText(Path.Combine(kit.ModRoot, "Proteus", ShaderUpgradeWriter.RecordFile)) : "");
        kit.AssertVictimUntouched();
    }
}
