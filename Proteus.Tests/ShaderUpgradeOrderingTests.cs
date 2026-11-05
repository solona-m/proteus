using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;
using Kit = Proteus.Tests.ShaderUpgradeTestKit;

namespace Proteus.Tests;

/// <summary>
/// The gear shader upgrade beside Proteus's other in-place writers: undo order (<see cref="ModelBackupOrder"/>), the
/// shared write lock with the wind brush, and the brush leaving the old diffuse layout alone.
/// </summary>
public class ShaderUpgradeOrderingTests
{
    private const string Red = "red/top.mtrl";

    private static MeshVolumeService.MaterialOutcome Brush(string mod, string rel)
        => MeshVolumeService.ApplyWindMaterials(mod, ["/mt_c0201e6024_top_b.mtrl"],
               [new PenumbraModMeta.Redirect(Kit.TopPath, rel.Replace('/', '\\'), "")]);

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-12")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-60")]
    public void Undo_is_refused_after_a_later_brush_edit()
    {
        using var kit = new Kit();
        var mod = kit.Dress("D", Kit.FormA());
        Assert.True(kit.Upgrade(mod, [Kit.Worn(Kit.TopPath, mod, Red)]).ChangedAny);
        Thread.Sleep(20);   // file times on a fast disk can tie
        var brushed = Brush(mod, Red);
        Assert.Equal(1, brushed.Changed);

        var ours = Path.Combine(mod, "Proteus", ShaderUpgradeWriter.BackupSubdir, "red", "top.mtrl");
        var theirs = Path.Combine(mod, "Proteus", MeshVolumeService.BackupSubdir, "red", "top.mtrl");
        Assert.True(File.GetLastWriteTimeUtc(ours) < File.GetLastWriteTimeUtc(theirs));

        var file = Kit.Get(mod, Red);
        Assert.Equal(ShaderUpgradeWriter.Undone.Refused, ShaderUpgradeWriter.RevertOrForget(mod, Red, false, out var why));
        Assert.Contains("brush", why);
        Assert.Equal(file, Kit.Get(mod, Red));
        Assert.NotEmpty(ShaderUpgradeWriter.ReadRecord(mod)!.Files);
        Assert.Null(ModelBackupOrder.LaterFeature(mod, MeshVolumeService.BackupSubdir, Red));   // the brush may go first

        Assert.True(MeshVolumeService.Revert(mod).Ok);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, ShaderUpgradeWriter.RevertOrForget(mod, Red, false, out _));
        Assert.Equal(Kit.FormA(), Kit.Get(mod, Red));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-60")]
    public void The_brushs_undo_is_refused_while_the_upgrade_is_newer()
    {
        using var kit = new Kit();
        // A wind save on a Form A material first (it upgrades it itself), then the author ships a legacy update and we
        // upgrade that: ours is newer.
        var mod = kit.Dress("D", Kit.FormA());
        Assert.Equal(1, Brush(mod, Red).Changed);
        Thread.Sleep(20);
        var update = Kit.FormA();
        BitConverter.TryWriteBytes(update.AsSpan(GearMaterialWriter.ColorTableStart(update)), (Half)0.6f);
        File.WriteAllBytes(Path.Combine(mod, "red", "top.mtrl"), update);
        Assert.True(kit.Upgrade(mod, [Kit.Worn(Kit.TopPath, mod, Red)]).ChangedAny);
        Assert.Equal(ProteusLocName(), ModelBackupOrder.LaterFeature(mod, MeshVolumeService.BackupSubdir, Red));
        Assert.False(MeshVolumeService.Revert(mod).Ok);
    }

    private static string ProteusLocName() => "gear shader upgrade";

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-64")]
    public void Wind_save_and_upgrade_on_one_file_never_lose_a_write()
    {
        for (int i = 0; i < 50; i++)
        {
            using var kit = new Kit();
            var mod = kit.Dress("D" + i, Kit.FormA());
            var plan = kit.Plan([Kit.Worn(Kit.TopPath, mod, Red)]);
            // Dedicated threads, not the pool: beside the parallel quick suite a starved pool left both tasks unstarted past
            // the deadlock guard (#49's kind); a real deadlock still trips it.
            var up = Task.Factory.StartNew(() => ShaderUpgradeWriter.Apply(mod, plan.PerMod[0].Writes, () => true, Kit.WriteTex,
                                                                             TexEncoding.Uncompressed),
                                           CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var brush = Task.Factory.StartNew(() => Brush(mod, Red), CancellationToken.None, TaskCreationOptions.LongRunning,
                                              TaskScheduler.Default);
            Assert.True(Task.WaitAll([up, brush], TimeSpan.FromSeconds(10)), "deadlock");
            var after = Kit.Get(mod, Red);
            Assert.Equal("character.shpk", CharacterShaderUpgrade.ShaderOf(after));
            // Whoever ran second saw character.shpk; the file is one coherent write, and the backups hold the original.
            var backups = new[] { ShaderUpgradeWriter.BackupSubdir, MeshVolumeService.BackupSubdir }
                .Select(s => Path.Combine(mod, "Proteus", s, "red", "top.mtrl")).Where(File.Exists).ToList();
            Assert.Contains(backups, b => File.ReadAllBytes(b).AsSpan().SequenceEqual(Kit.FormA()));
            Assert.True(ShaderUpgradePlan.IsSound(Kit.FormA(), after, LegacyForm.IndexMask, null)
                     || MeshVolumeService.IsPatched(mod, Red));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-74")]
    public void Wind_save_leaves_a_diffuse_specular_material_legacy()
    {
        using var kit = new Kit();
        // The fixture declares no movement constants; a variant that does (as most game-derived materials) must not be
        // patched either, which would leave a brush backup that tangles the undo order.
        foreach (var src in new[] { Kit.FormB(), WithConstants(Kit.FormB()) })
        {
            var mod = kit.Dress("B" + src.Length, src);
            var outcome = Brush(mod, Red);
            Assert.Equal(1, outcome.Legacy);
            Assert.Equal(0, outcome.Upgraded);
            Assert.Equal(0, outcome.Changed);
            Assert.Equal(src, Kit.Get(mod, Red));
            Assert.False(Directory.Exists(Path.Combine(mod, "Proteus", MeshVolumeService.BackupSubdir)));
            Assert.False(MeshVolumeService.IsPatched(mod, Red));
        }
        var a = kit.Dress("A", Kit.FormA());
        Assert.Equal(1, Brush(a, Red).Upgraded);
    }

    private static byte[] WithConstants(byte[] mtrl)
    {
        var (a, _) = TextureLoader.EnsureConstantValues(mtrl, MeshVolumeService.VertexMovementScaleId, 1f);
        var (b, _) = TextureLoader.EnsureConstantValues(a, MeshVolumeService.VertexMovementMaxLengthId, 1f);
        Assert.NotNull(ShaderUpgradePlan.Constant(b, MeshVolumeService.VertexMovementScaleId));
        return b;
    }
}

/// <summary>For Creators.md explains what the upgrade changes in a user's copy of a mod.</summary>
public class ShaderUpgradeDocsTests
{
    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-14")]
    public void For_creators_explains_the_gear_shader_upgrade()
    {
        var doc = File.ReadAllText(ShaderUpgradePlanTests.SourceFile("For Creators.md"));
        foreach (var s in new[] { "Gear shader upgrade", "Shader Upgrade/", "Proteus/shaderupgrade-backup",
                                  "shaderupgrade.json", "Undo for what I'm wearing" })
            Assert.Contains(s, doc);
    }
}
