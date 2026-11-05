using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.ShaderUpgradePlan;
using Kit = Proteus.Tests.ShaderUpgradeTestKit;

namespace Proteus.Tests;

/// <summary>
/// The gear shader upgrade's writes into a worn mod, and their undo (<see cref="ShaderUpgradeWriter"/>). These edit
/// somebody else's mod, so what matters most is what they leave alone: the author's files, every other redirect, the
/// groups, and an author's later update.
/// </summary>
public class ShaderUpgradeWriterTests
{
    private const string Red = "red/top.mtrl";
    private static string Spec(string mod) => Path.Combine(mod, "tex", "top_s.tex");
    private static IReadOnlyList<Worn> WornRed(string mod) => [Kit.Worn(Kit.TopPath, mod, Red)];

    private static List<PenumbraModMeta.Redirect> Redirects(string mod) => PenumbraModMeta.ReadAllRedirects(mod);

    private static List<(string Group, string Option)> Groups(string mod)
        => HairPortPlan.ReadOwners(mod)!.Select(o => (o.Group, o.Option)).ToList();

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-2")]
    public void Index_mask_material_is_upgraded_in_place_after_a_backup()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Old Top", Kit.FormA());
        var outcome = kit.Upgrade(mod, WornRed(mod));
        Assert.Equal([Red], outcome.Written);
        Assert.Equal("character.shpk", CharacterShaderUpgrade.ShaderOf(Kit.Get(mod, Red)));
        Assert.Equal(Kit.FormA(), Kit.Get(mod, "Proteus/shaderupgrade-backup/red/top.mtrl"));
        Assert.True(ShaderUpgradeWriter.IsUpgraded(mod, Red));
        var e = Assert.Single(ShaderUpgradeWriter.ReadRecord(mod)!.Files);
        Assert.False(e.Pending);
        Assert.Equal("index-mask", e.Form);
        // The other option's copy of the file is not drawn, so it is not touched.
        Assert.Equal(Kit.FormA(), Kit.Get(mod, "blue/top.mtrl"));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-4")]
    public void Only_materials_masks_added_redirects_and_proteus_files_change()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Bandana", Kit.FormB(), withSpec: true);
        Kit.Put(mod, "readme.txt", "hello"u8.ToArray());
        var before = Kit.Hashes(mod);
        var redirectsBefore = Redirects(mod);
        var groupsBefore = Groups(mod);

        var outcome = kit.Upgrade(mod, WornRed(mod), _ => Spec(mod));
        Assert.True(outcome.ChangedAny);

        var after = Kit.Hashes(mod);
        foreach (var (rel, hash) in before)
            if (rel != Red) Assert.Equal(hash, after[rel]);
        var added = after.Keys.Except(before.Keys).ToList();
        Assert.All(added, rel => Assert.True(rel.StartsWith("Shader Upgrade/") || rel.StartsWith("Proteus/"), rel));
        Assert.Contains(added, r => r.StartsWith("Shader Upgrade/"));
        Assert.Equal(groupsBefore, Groups(mod));
        var newRedirects = Redirects(mod).Except(redirectsBefore).ToList();
        Assert.All(newRedirects, r => Assert.StartsWith("Shader Upgrade", r.File.Replace('\\', '/')));
        Assert.Equal(redirectsBefore.Count, Redirects(mod).Count - newRedirects.Count);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-4")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-55")]
    public void A_v3_folder_is_never_written()
    {
        using var kit = new Kit();
        var mod = kit.LegacyMod("Old", new Dictionary<string, string> { [Kit.TopPath] = "top.mtrl" });
        Kit.Put(mod, "top.mtrl", Kit.FormA());
        var before = Kit.Hashes(mod);
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, mod, "top.mtrl")]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(LeftReason.LegacyMod, Assert.Single(plan.Left).Reason);
        // And the writer refuses it under its lock even when handed a write.
        var write = new MaterialWrite("top.mtrl", [Kit.TopPath], LegacyForm.IndexMask, Sha8(Kit.FormA()),
                                      WindNeutral(CharacterShaderUpgrade.Upgrade(Kit.FormA())!)!, null);
        var outcome = ShaderUpgradeWriter.Apply(mod, [write], () => true, Kit.WriteTex, TexEncoding.Uncompressed);
        Assert.Equal(LeftReason.LegacyMod, Assert.Single(outcome.Left).Reason);
        Assert.Equal(before, Kit.Hashes(mod));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-5")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-68")]
    public void Writes_stop_when_mayWrite_turns_false()
    {
        using var kit = new Kit();
        var mod = kit.Mod("Two", new Dictionary<string, string> { [Kit.TopPath] = "a.mtrl", [Kit.DwnPath] = "b.mtrl" });
        Kit.Put(mod, "a.mtrl", Kit.FormA());
        Kit.Put(mod, "b.mtrl", Kit.FormA());
        int asked = 0;
        var outcome = kit.Upgrade(mod, [Kit.Worn(Kit.TopPath, mod, "a.mtrl"), Kit.Worn(Kit.DwnPath, mod, "b.mtrl")],
                                  mayWrite: () => ++asked == 1);
        Assert.Single(outcome.Written);
        Assert.Equal(1, new[] { "a.mtrl", "b.mtrl" }.Count(r => CharacterShaderUpgrade.ShaderOf(Kit.Get(mod, r)) == "character.shpk"));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-11")]
    public void Diffuse_material_gets_a_mask_file_and_redirects_beside_its_own()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Bandana", Kit.FormB(), withSpec: true);
        Assert.True(kit.Upgrade(mod, WornRed(mod), _ => Spec(mod)).ChangedAny);
        var mtrl = Kit.Get(mod, Red);
        var maskGame = MaskSlotPath(mtrl)!;
        Assert.StartsWith("chara/equipment/e6024/texture/dt_", maskGame);
        var red = HairPortPlan.ReadOwners(mod)!.Single(o => o.Option == "Red");
        var maskRel = HairPortPlan.FileFor(red, maskGame)!.Replace('\\', '/');
        Assert.StartsWith("Shader Upgrade/", maskRel);
        Assert.True(TextureLoader.IsCompleteTex(Path.Combine(mod, maskRel)));
        // Blue ships the same file name but its own copy: it gets the redirect only if it ships THIS file.
        Assert.Null(HairPortPlan.FileFor(HairPortPlan.ReadOwners(mod)!.Single(o => o.Option == "Blue"), maskGame));
        Assert.Equal(Kit.Fixture("legacy_spec_s.tex"), Kit.Get(mod, "tex/top_s.tex"));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-62")]
    public void Mask_redirect_lands_in_every_owner_of_the_material_and_undo_takes_each_out()
    {
        using var kit = new Kit();
        var mod = kit.Mod("Shared", null, ("Colour", "Single", [
            ("Red", new Dictionary<string, string> { [Kit.TopPath] = "top.mtrl", [Kit.SpecPath] = "s.tex" }),
            ("Blue", new Dictionary<string, string> { [Kit.TopPath] = "top.mtrl", [Kit.SpecPath] = "s.tex" })]));
        Kit.Put(mod, "top.mtrl", Kit.FormB());
        Kit.Put(mod, "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        Assert.True(kit.Upgrade(mod, [Kit.Worn(Kit.TopPath, mod, "top.mtrl")], _ => Path.Combine(mod, "s.tex")).ChangedAny);
        var maskGame = MaskSlotPath(Kit.Get(mod, "top.mtrl"))!;
        Assert.All(HairPortPlan.ReadOwners(mod)!.Where(o => !o.IsDefault), o => Assert.NotNull(HairPortPlan.FileFor(o, maskGame)));
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, ShaderUpgradeWriter.RevertOrForget(mod, "top.mtrl", false, out _));
        Assert.All(HairPortPlan.ReadOwners(mod)!, o => Assert.Null(HairPortPlan.FileFor(o, maskGame)));
        Assert.False(Directory.Exists(Path.Combine(mod, "Shader Upgrade")));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-12")]
    public void Undo_worn_restores_backups_removes_added_redirects_and_unnamed_masks_and_declines()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Bandana", Kit.FormB(), withSpec: true);
        var before = Kit.Hashes(mod);
        var redirectsBefore = Redirects(mod);
        Assert.True(kit.Upgrade(mod, WornRed(mod), _ => Spec(mod)).ChangedAny);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, ShaderUpgradeWriter.RevertOrForget(mod, Red, decline: true, out _));
        Assert.Equal(Kit.FormB(), Kit.Get(mod, Red));
        Assert.Equal(redirectsBefore, Redirects(mod));
        Assert.False(Directory.Exists(Path.Combine(mod, "Shader Upgrade")));
        Assert.False(Directory.Exists(Path.Combine(mod, "Proteus", "shaderupgrade-backup")));
        Assert.True(ShaderUpgradeWriter.IsDeclined(mod, Red));
        var after = Kit.Hashes(mod);
        Assert.Equal(before, after.Where(kv => !kv.Key.StartsWith("Proteus/")).ToDictionary());
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-66")]
    public void Undo_without_decline_leaves_no_declined_entry()
    {
        using var kit = new Kit();
        var mod = kit.Dress("D", Kit.FormA());
        Assert.True(kit.Upgrade(mod, WornRed(mod)).ChangedAny);
        ShaderUpgradeWriter.RevertOrForget(mod, Red, decline: false, out _);
        Assert.False(ShaderUpgradeWriter.IsDeclined(mod, Red));
        Assert.False(File.Exists(Path.Combine(mod, "Proteus", "shaderupgrade.json")));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-66")]
    public void ClearDeclines_touches_only_named_files()
    {
        using var kit = new Kit();
        var mod = kit.Mod("Two", new Dictionary<string, string> { [Kit.TopPath] = "a.mtrl", [Kit.DwnPath] = "b.mtrl" });
        Kit.Put(mod, "a.mtrl", Kit.FormA());
        Kit.Put(mod, "b.mtrl", Kit.FormA());
        kit.Upgrade(mod, [Kit.Worn(Kit.TopPath, mod, "a.mtrl"), Kit.Worn(Kit.DwnPath, mod, "b.mtrl")]);
        ShaderUpgradeWriter.RevertOrForget(mod, "a.mtrl", true, out _);
        ShaderUpgradeWriter.RevertOrForget(mod, "b.mtrl", true, out _);
        ShaderUpgradeWriter.ClearDeclines(mod, ["a.mtrl"]);
        Assert.False(ShaderUpgradeWriter.IsDeclined(mod, "a.mtrl"));
        Assert.True(ShaderUpgradeWriter.IsDeclined(mod, "b.mtrl"));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-13")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-61")]
    public void Undo_after_author_update_keeps_the_file_and_only_our_redirects_and_masks_go()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Bandana", Kit.FormB(), withSpec: true);
        Assert.True(kit.Upgrade(mod, WornRed(mod), _ => Spec(mod)).ChangedAny);
        var update = CharacterShaderUpgrade.Upgrade(Kit.FormA())!;   // the author ships a Dawntrail file
        File.WriteAllBytes(Path.Combine(mod, "red", "top.mtrl"), update);
        Assert.Equal(ShaderUpgradeWriter.Undone.Forgotten, ShaderUpgradeWriter.RevertOrForget(mod, Red, false, out _));
        Assert.Equal(update, Kit.Get(mod, Red));
        Assert.False(Directory.Exists(Path.Combine(mod, "Shader Upgrade")));
        Assert.DoesNotContain(Redirects(mod), r => r.File.Replace('\\', '/').StartsWith("Shader Upgrade"));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-13")]
    public void Author_update_is_kept_by_undo_and_upgraded_again_when_legacy()
    {
        using var kit = new Kit();
        var mod = kit.Dress("D", Kit.FormA());
        Assert.True(kit.Upgrade(mod, WornRed(mod)).ChangedAny);
        var update = Kit.FormA();
        BitConverter.TryWriteBytes(update.AsSpan(GearMaterialWriter.ColorTableStart(update)), (Half)0.75f);
        File.WriteAllBytes(Path.Combine(mod, "red", "top.mtrl"), update);
        Assert.Equal(ShaderUpgradeWriter.Undone.Forgotten, ShaderUpgradeWriter.RevertOrForget(mod, Red, false, out _));
        Assert.Equal(update, Kit.Get(mod, Red));
        Assert.True(kit.Upgrade(mod, WornRed(mod)).ChangedAny);
        Assert.Equal(update, Kit.Get(mod, "Proteus/shaderupgrade-backup/red/top.mtrl"));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-13")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-73")]
    public void Author_update_between_walks_then_undo_keeps_the_authors_file()
    {
        using var kit = new Kit();
        var mod = kit.Dress("D", Kit.FormA());
        Assert.True(kit.Upgrade(mod, WornRed(mod)).ChangedAny);   // walk 1: backup B1 = the original
        var f2 = Kit.FormA();
        BitConverter.TryWriteBytes(f2.AsSpan(GearMaterialWriter.ColorTableStart(f2)), (Half)0.5f);
        File.WriteAllBytes(Path.Combine(mod, "red", "top.mtrl"), f2);   // the author's new legacy file, no undo first
        Assert.True(kit.Upgrade(mod, WornRed(mod)).ChangedAny);   // walk 2: forgets B1, backs F2 up, upgrades
        Assert.Equal(f2, Kit.Get(mod, "Proteus/shaderupgrade-backup/red/top.mtrl"));
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, ShaderUpgradeWriter.RevertOrForget(mod, Red, false, out _));
        Assert.Equal(f2, Kit.Get(mod, Red));   // never the first backup
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-19")]
    public void Material_write_failure_leaves_file_and_takes_out_its_mask()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Bandana", Kit.FormB(), withSpec: true);
        var redirectsBefore = Redirects(mod);
        ShaderUpgradeWriter.AfterStep = step => { if (step == "redirects") throw new IOException("disk full"); };
        var outcome = kit.Upgrade(mod, WornRed(mod), _ => Spec(mod));
        ShaderUpgradeWriter.AfterStep = null;
        Assert.Empty(outcome.Written);
        Assert.Equal(LeftReason.WriteFailed, Assert.Single(outcome.Left).Reason);
        Assert.Equal(Kit.FormB(), Kit.Get(mod, Red));
        Assert.Equal(redirectsBefore, Redirects(mod));
        Assert.False(File.Exists(Path.Combine(mod, "Shader Upgrade")) && Directory.EnumerateFiles(Path.Combine(mod, "Shader Upgrade")).Any());
        Assert.Empty(ShaderUpgradeWriter.ReadRecord(mod)?.Files ?? []);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-59")]
    public void A_crash_after_the_pending_entry_is_completed_or_cleaned_at_every_step()
    {
        foreach (var step in new[] { "pending", "mask", "redirects", "material" })
        {
            // Completed by the next Apply.
            using (var kit = new Kit())
            {
                var mod = kit.Dress("B", Kit.FormB(), withSpec: true);
                Crash(kit, mod, step);
                Assert.True(ShaderUpgradeWriter.ReadRecord(mod)!.Files.Single().Pending || step == "material");
                if (step != "material") Assert.True(kit.Upgrade(mod, WornRed(mod), _ => Spec(mod)).ChangedAny, step);
                Assert.Equal("character.shpk", CharacterShaderUpgrade.ShaderOf(Kit.Get(mod, Red)));
                Assert.Equal(Kit.FormB(), Kit.Get(mod, "Proteus/shaderupgrade-backup/red/top.mtrl"));
            }
            // Or cleaned by an undo.
            using (var kit = new Kit())
            {
                var mod = kit.Dress("B", Kit.FormB(), withSpec: true);
                var redirectsBefore = Redirects(mod);
                Crash(kit, mod, step);
                ShaderUpgradeWriter.RevertOrForget(mod, Red, false, out _);
                Assert.Equal(Kit.FormB(), Kit.Get(mod, Red));
                Assert.Equal(redirectsBefore, Redirects(mod));
                Assert.False(Directory.Exists(Path.Combine(mod, "Shader Upgrade")), step);
                Assert.Empty(ShaderUpgradeWriter.ReadRecord(mod)?.Files ?? []);
            }
        }

        static void Crash(Kit kit, string mod, string at)
        {
            // A process kill: the step completes, then nothing more runs (not even the cleanup).
            ShaderUpgradeWriter.AfterStep = s => { if (s == at) throw new CrashException(); };
            try { kit.Upgrade(mod, WornRed(mod), _ => Spec(mod)); }
            catch (CrashException) { }
            finally { ShaderUpgradeWriter.AfterStep = null; }
        }
    }

    private sealed class CrashException : Exception;

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-24")]
    public void Record_and_backups_move_with_the_folder()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Before", Kit.FormA());
        Assert.True(kit.Upgrade(mod, WornRed(mod)).ChangedAny);
        var moved = Path.Combine(kit.ModsRoot, "After");
        Directory.Move(mod, moved);
        Assert.True(ShaderUpgradeWriter.IsUpgraded(moved, Red));
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, ShaderUpgradeWriter.RevertOrForget(moved, Red, false, out _));
        Assert.Equal(Kit.FormA(), Kit.Get(moved, Red));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-39")]
    public void Mask_gate_refuses_bad_files_and_writes_atomically()
    {
        using var kit = new Kit();
        var mod = kit.Dress("M", Kit.FormA());
        var px = new byte[8 * 8 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)i; px[i + 1] = 9; px[i + 2] = 255; px[i + 3] = 255; }
        var mask = new MaskWrite("Shader Upgrade/aaaaaaaaaaaaaaaa_mask.tex", "chara/x/dt_a_mask.tex", 8, 8, px, []);
        var dir = Path.Combine(mod, "Shader Upgrade");

        Assert.Throws<ShaderUpgradeWriter.WriteRefused>(() => ShaderUpgradeWriter.WriteMask(mod, mask, TexEncoding.Uncompressed,
            (rgba, _, _, path, e) => Kit.WriteTex(new byte[4 * 4 * 4], 4, 4, path, e)));
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Throws<ShaderUpgradeWriter.WriteRefused>(() =>
            ShaderUpgradeWriter.WriteMask(mod, mask, TexEncoding.Uncompressed, (_, _, _, _, _) => false));
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Throws<ShaderUpgradeWriter.WriteRefused>(() =>
            ShaderUpgradeWriter.WriteMask(mod, mask with { Rel = "red/evil_mask.tex" }, TexEncoding.Uncompressed, Kit.WriteTex));
        var path = ShaderUpgradeWriter.WriteMask(mod, mask, TexEncoding.Uncompressed, Kit.WriteTex);
        Assert.Equal([path], Directory.GetFiles(dir));
        Assert.True(TextureLoader.IsCompleteTex(path));

        var head = File.ReadAllBytes(path)[..16];
        long len = new FileInfo(path).Length;
        Assert.True(IsSoundMaskHeader(head, len, 8, 8));
        Assert.False(IsSoundMaskHeader(head, len - 1, 8, 8));
        var twoMips = (byte[])head.Clone(); twoMips[14] = 2;
        Assert.False(IsSoundMaskHeader(twoMips, len, 8, 8));
        var (s, w, h) = Kit.Spec();
        var good = MaskFrom(s, w, h, MaskGloss.InverseSpecular).Rgba;
        Assert.True(IsSoundMaskBuffer(good, w, h, s, w, h, MaskGloss.InverseSpecular));
        var badB = (byte[])good.Clone(); badB[2] = 200;
        Assert.False(IsSoundMaskBuffer(badB, w, h, s, w, h, MaskGloss.InverseSpecular));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-39")]
    public void The_mods_specular_file_is_never_written()
    {
        using var kit = new Kit();
        var mod = kit.Dress("B", Kit.FormB(), withSpec: true);
        var s = Kit.Get(mod, "tex/top_s.tex");
        kit.Upgrade(mod, WornRed(mod), _ => Spec(mod));
        ShaderUpgradeWriter.RevertOrForget(mod, Red, false, out _);
        Assert.Equal(s, Kit.Get(mod, "tex/top_s.tex"));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-46")]
    public void A_gloss_change_does_not_touch_upgraded_files()
    {
        using var kit = new Kit();
        var mod = kit.Dress("B", Kit.FormB(), withSpec: true);
        kit.Upgrade(mod, WornRed(mod), _ => Spec(mod));
        var after = Kit.Get(mod, Red);
        Assert.Empty(kit.Plan(WornRed(mod), _ => Spec(mod), MaskGloss.MidConstant).PerMod);   // character.shpk now: skipped
        Assert.Equal(after, Kit.Get(mod, Red));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-58")]
    public void A_file_changed_since_planning_is_skipped()
    {
        using var kit = new Kit();
        var mod = kit.Dress("D", Kit.FormA());
        var plan = kit.Plan(WornRed(mod));
        var changed = Kit.FormA();
        BitConverter.TryWriteBytes(changed.AsSpan(GearMaterialWriter.ColorTableStart(changed)), (Half)0.3f);
        File.WriteAllBytes(Path.Combine(mod, "red", "top.mtrl"), changed);
        var outcome = ShaderUpgradeWriter.Apply(mod, plan.PerMod[0].Writes, () => true, Kit.WriteTex, TexEncoding.Uncompressed);
        Assert.Empty(outcome.Written);
        Assert.True(outcome.Rewalk);
        Assert.Equal(changed, Kit.Get(mod, Red));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-65")]
    public void Identical_masks_share_a_file_and_survive_a_partial_undo()
    {
        using var kit = new Kit();
        var mod = kit.Mod("Twin", null,
            ("Top", "Single", [("A", new Dictionary<string, string> { [Kit.TopPath] = "a.mtrl", [Kit.SpecPath] = "s.tex" })]),
            ("Legs", "Single", [("B", new Dictionary<string, string> { [Kit.DwnPath] = "b.mtrl", [Kit.SpecPath] = "s.tex" })]));
        Kit.Put(mod, "a.mtrl", Kit.FormB());
        Kit.Put(mod, "b.mtrl", Kit.FormB());
        Kit.Put(mod, "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        kit.Upgrade(mod, [Kit.Worn(Kit.TopPath, mod, "a.mtrl"), Kit.Worn(Kit.DwnPath, mod, "b.mtrl")], _ => Path.Combine(mod, "s.tex"));
        var masks = Directory.GetFiles(Path.Combine(mod, "Shader Upgrade"));
        Assert.Single(masks);
        ShaderUpgradeWriter.RevertOrForget(mod, "a.mtrl", false, out _);
        Assert.True(File.Exists(masks[0]));   // b still names it
        ShaderUpgradeWriter.RevertOrForget(mod, "b.mtrl", false, out _);
        Assert.False(File.Exists(masks[0]));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-67")]
    public void A_vanished_mod_folder_fails_cleanly()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Gone", Kit.FormA());
        var plan = kit.Plan(WornRed(mod));
        Directory.Delete(mod, true);
        var outcome = ShaderUpgradeWriter.Apply(mod, plan.PerMod[0].Writes, () => true, Kit.WriteTex, TexEncoding.Uncompressed);
        Assert.Empty(outcome.Written);
        Assert.True(outcome.Rewalk);
        Assert.False(Directory.Exists(mod));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-70")]
    public void Concurrent_manifest_edits_survive_and_a_removed_option_fails_cleanly()
    {
        using var kit = new Kit();
        var mod = kit.Dress("B", Kit.FormB(), withSpec: true);
        var plan = kit.Plan(WornRed(mod), _ => Spec(mod));
        // Someone adds a redirect to Red after planning: it survives our merge.
        PenumbraModMeta.MergeOptionRedirects(mod, "Colour", "Red", new Dictionary<string, string> { ["chara/x/extra.tex"] = "extra.tex" });
        Assert.Single(ShaderUpgradeWriter.Apply(mod, plan.PerMod[0].Writes, () => true, Kit.WriteTex, TexEncoding.Uncompressed).Written);
        Assert.Contains(Redirects(mod), r => r.GamePath == "chara/x/extra.tex");

        // An option removed since planning: write-failed, and the mask is taken out again.
        using var kit2 = new Kit();
        var mod2 = kit2.Dress("C", Kit.FormB(), withSpec: true);
        var plan2 = kit2.Plan(WornRed(mod2), _ => Spec(mod2));
        var meta = File.ReadAllText(Path.Combine(mod2, "meta.json")).Replace("\"Name\":\"Red\"", "\"Name\":\"Crimson\"");
        File.WriteAllText(Path.Combine(mod2, "meta.json"), meta);
        var outcome = ShaderUpgradeWriter.Apply(mod2, plan2.PerMod[0].Writes, () => true, Kit.WriteTex, TexEncoding.Uncompressed);
        Assert.Equal(LeftReason.WriteFailed, Assert.Single(outcome.Left).Reason);
        Assert.Equal(Kit.FormB(), Kit.Get(mod2, Red));
        Assert.False(Directory.Exists(Path.Combine(mod2, "Shader Upgrade")) && Directory.EnumerateFiles(Path.Combine(mod2, "Shader Upgrade")).Any());
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-72")]
    public void Apply_refuses_a_sidecar_pack_folder()
    {
        using var kit = new Kit();
        var mod = kit.Dress("Pack", Kit.FormA());
        Kit.Put(mod, "Proteus/metadata.json", "{}"u8.ToArray());
        var before = Kit.Hashes(mod);
        var write = new MaterialWrite(Red, [Kit.TopPath], LegacyForm.IndexMask, Sha8(Kit.FormA()),
                                      WindNeutral(CharacterShaderUpgrade.Upgrade(Kit.FormA())!)!, null);
        var outcome = ShaderUpgradeWriter.Apply(mod, [write], () => true, Kit.WriteTex, TexEncoding.Uncompressed);
        Assert.Equal(LeftReason.ProteusPack, Assert.Single(outcome.Left).Reason);
        Assert.Equal(before, Kit.Hashes(mod));
    }
}
