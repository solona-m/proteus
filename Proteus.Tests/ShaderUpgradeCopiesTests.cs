using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;
using static Proteus.Services.ShaderUpgradePlan;
using Kit = Proteus.Tests.ShaderUpgradeTestKit;

namespace Proteus.Tests;

/// <summary>The gear shader upgrade's file writer and its gates (<see cref="ShaderUpgradeCopies"/>).</summary>
public class ShaderUpgradeCopiesTests
{
    private static Dictionary<string, string> HashTree(string dir)
        => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-4")]
    public void Writing_copies_leaves_the_mod_folder_byte_identical()
    {
        using var kit = new Kit();
        kit.SpecDisk = kit.Put("Bandana", "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        var a = kit.Put("Old Top", "a.mtrl", Kit.FormA());
        var b = kit.Put("Bandana", "b.mtrl", Kit.FormB());
        var modDirs = new[] { "Old Top", "Bandana" }.Select(m => Path.Combine(kit.ModsRoot, m)).ToList();
        var before = modDirs.Select(HashTree).ToList();

        var plan = Build([new Worn(Kit.TopPath, a), new Worn(Kit.DwnPath, b)], kit.Inputs());
        Assert.Equal(2, plan.Units.Count);
        var written = new List<string>();
        foreach (var u in plan.Units)
        {
            written.Add(ShaderUpgradeCopies.WriteMaterial(kit.ManagedRoot, u.MaterialCopyName, u.MaterialBytes,
                                                          (p, bytes) => File.WriteAllBytes(p, bytes)));
            if (u.Mask is { } m) written.Add(ShaderUpgradeCopies.WriteMask(kit.ManagedRoot, m, TexEncoding.Uncompressed, Kit.WriteTex));
        }

        Assert.Equal(before, modDirs.Select(HashTree).ToList());
        Assert.Equal(3, written.Count);
        Assert.All(written, w => Assert.True(ManagedModPaths.IsShaderUpgradeCopy(w, kit.ManagedRoot)));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-4")]
    public void Writers_refuse_a_target_outside_the_ShaderUpgrade_folder()
    {
        using var kit = new Kit();
        Assert.Throws<ShaderUpgradeCopies.WriteRefused>(() =>
            ShaderUpgradeCopies.WriteMaterial(kit.ManagedRoot, @"..\..\Old Top\a.mtrl", [1], (p, b) => File.WriteAllBytes(p, b)));
        Assert.Throws<ShaderUpgradeCopies.WriteRefused>(() =>
            ShaderUpgradeCopies.WriteMaterial(kit.ManagedRoot, @"sub\a.mtrl", [1], (p, b) => File.WriteAllBytes(p, b)));
        var mask = new MaskSpec("chara/proteus/shaderupgrade/x_mask.tex", @"..\x_mask.tex", 4, 4, new byte[64], default);
        Assert.Throws<ShaderUpgradeCopies.WriteRefused>(() =>
            ShaderUpgradeCopies.WriteMask(kit.ManagedRoot, mask, TexEncoding.Uncompressed, Kit.WriteTex));
        Assert.False(File.Exists(Path.Combine(kit.ModsRoot, "Old Top", "a.mtrl")));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-19")]
    public void Material_write_failure_is_left_as_write_failed_with_no_partial_file()
    {
        using var kit = new Kit();
        Assert.Throws<ShaderUpgradeCopies.WriteRefused>(() =>
            ShaderUpgradeCopies.WriteMaterial(kit.ManagedRoot, "1-aaaaaaaaaaaaaaaa.mtrl", [1, 2, 3],
                (p, _) => { File.WriteAllBytes(p, [1]); throw new IOException("disk full"); }));
        Assert.False(File.Exists(Path.Combine(ManagedModPaths.ShaderUpgradeDir(kit.ManagedRoot), "1-aaaaaaaaaaaaaaaa.mtrl")));
        Assert.Equal("write-failed", ShaderUpgradeWatcher.ReasonName(LeftReason.WriteFailed));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-39")]
    public void Mask_gate_refuses_bad_files_and_writes_atomically()
    {
        using var kit = new Kit();
        var px = new byte[8 * 8 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)i; px[i + 1] = 9; px[i + 2] = 255; px[i + 3] = 255; }
        var mask = new MaskSpec("chara/proteus/shaderupgrade/a_mask.tex", "1-aaaaaaaaaaaaaaaa_mask.tex", 8, 8, px, default);
        var dir = ManagedModPaths.ShaderUpgradeDir(kit.ManagedRoot);

        // An encoder that writes the wrong size: refused, nothing left behind.
        Assert.Throws<ShaderUpgradeCopies.WriteRefused>(() => ShaderUpgradeCopies.WriteMask(kit.ManagedRoot, mask,
            TexEncoding.Uncompressed, (rgba, _, _, path, e) => Kit.WriteTex(new byte[4 * 4 * 4], 4, 4, path, e)));
        Assert.Empty(Directory.GetFiles(dir));
        // An encoder that fails: refused, nothing left behind.
        Assert.Throws<ShaderUpgradeCopies.WriteRefused>(() =>
            ShaderUpgradeCopies.WriteMask(kit.ManagedRoot, mask, TexEncoding.Uncompressed, (_, _, _, _, _) => false));
        Assert.Empty(Directory.GetFiles(dir));
        // A sound one lands under its final name only.
        var path = ShaderUpgradeCopies.WriteMask(kit.ManagedRoot, mask, TexEncoding.Uncompressed, Kit.WriteTex);
        Assert.Equal([path], Directory.GetFiles(dir));
        Assert.True(TextureLoader.IsCompleteTex(path));

        // The header gate itself.
        var head = File.ReadAllBytes(path)[..16];
        long len = new FileInfo(path).Length;
        Assert.True(IsSoundMaskHeader(head, len, 8, 8));
        Assert.False(IsSoundMaskHeader(head, len - 1, 8, 8));
        Assert.False(IsSoundMaskHeader(head, len, 16, 8));
        var twoMips = (byte[])head.Clone(); twoMips[14] = 2;
        Assert.False(IsSoundMaskHeader(twoMips, len, 8, 8));
        var bc5 = (byte[])head.Clone(); BitConverter.TryWriteBytes(bc5.AsSpan(4), 0x6230u);
        Assert.False(IsSoundMaskHeader(bc5, len, 8, 8));

        // The buffer gate: B and A must be 255, R the source's luma, G the strategy's.
        var (s, w, h) = Kit.Spec();
        var good = MaskFrom(s, w, h, MaskGloss.InverseSpecular).Rgba;
        Assert.True(IsSoundMaskBuffer(good, w, h, s, w, h, MaskGloss.InverseSpecular));
        var badB = (byte[])good.Clone(); badB[2] = 200;
        Assert.False(IsSoundMaskBuffer(badB, w, h, s, w, h, MaskGloss.InverseSpecular));
        Assert.False(IsSoundMaskBuffer(good, w, h, s, w, h, MaskGloss.MidConstant));
        Assert.False(IsSoundMaskBuffer(good, w / 2, h, s, w, h, MaskGloss.InverseSpecular));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-13")]
    public void Sweep_keeps_own_and_live_processes_copies()
    {
        var files = new[] { @"d\100-aa.mtrl", @"d\200-bb.mtrl", @"d\300-cc_mask.tex", @"d\readme.txt", @"d\x-y.mtrl" };
        var stale = StaleCopies(files, ownPid: 100, alive: pid => pid == 200).ToList();
        Assert.Equal([@"d\300-cc_mask.tex"], stale);

        using var kit = new Kit();
        var dir = ManagedModPaths.ShaderUpgradeDir(kit.ManagedRoot);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "1-aa.mtrl", "99999999-bb.mtrl" }) File.WriteAllBytes(Path.Combine(dir, f), [1]);
        int pid = Environment.ProcessId;
        File.WriteAllBytes(Path.Combine(dir, $"{pid}-cc.mtrl"), [1]);
        int swept = ShaderUpgradeCopies.Sweep(kit.ManagedRoot, pid, p => p == 1);
        Assert.Equal(1, swept);
        Assert.True(File.Exists(Path.Combine(dir, "1-aa.mtrl")));
        Assert.True(File.Exists(Path.Combine(dir, $"{pid}-cc.mtrl")));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-52")]
    public void PruneMasks_deletes_superseded_own_masks_keeping_referenced_tail_and_foreign()
    {
        using var kit = new Kit();
        var dir = ManagedModPaths.ShaderUpgradeDir(kit.ManagedRoot);
        Directory.CreateDirectory(dir);
        const int pid = 7;
        string[] names = ["7-a_mask.tex", "7-b_mask.tex", "7-c_mask.tex", "7-d_mask.tex", "8-e_mask.tex", "7-f.mtrl"];
        foreach (var n in names) File.WriteAllBytes(Path.Combine(dir, n), [1]);
        var tail = new ShaderUpgradeCopies.ReplacedTail(MaskTail);
        tail.Add("7-b_mask.tex");
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "7-a_mask.tex" };

        // A delete that fails is retried by the next prune.
        int first = ShaderUpgradeCopies.PruneMasks(kit.ManagedRoot, referenced, tail, pid,
            f => { if (f.EndsWith("7-d_mask.tex")) throw new IOException("in use"); File.Delete(f); });
        Assert.Equal(1, first);
        Assert.False(File.Exists(Path.Combine(dir, "7-c_mask.tex")));
        Assert.True(File.Exists(Path.Combine(dir, "7-d_mask.tex")));
        Assert.Equal(1, ShaderUpgradeCopies.PruneMasks(kit.ManagedRoot, referenced, tail, pid));
        Assert.Equal(["7-a_mask.tex", "7-b_mask.tex", "7-f.mtrl", "8-e_mask.tex"],
                     Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToList());

        // The tail keeps only the last few.
        for (int i = 0; i < 6; i++) tail.Add($"7-t{i}_mask.tex");
        Assert.Equal(MaskTail, tail.Snapshot().Count);
        Assert.DoesNotContain("7-b_mask.tex", tail.Snapshot());
    }
}
