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
/// The gear shader upgrade's policy (<see cref="ShaderUpgradePlan"/>) against the two real legacy materials and the
/// real grey specular map, in a temporary mods folder.
/// </summary>
public class ShaderUpgradePlanTests
{
    private static Plan BuildOne(Kit kit, string gamePath, string upstream, MaskGloss gloss = MaskGloss.InverseSpecular,
                                 IReadOnlySet<string>? published = null)
        => Build([new Worn(gamePath, upstream)], kit.Inputs(gloss, published));

    // ── forms and the core upgrade ──────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-11")]
    public void FormOf_recognises_index_mask_and_diffuse()
    {
        Assert.Equal(LegacyForm.IndexMask, FormOf(Kit.FormA()));
        Assert.Equal(LegacyForm.Diffuse, FormOf(Kit.FormB()));
        Assert.Equal(LegacyForm.Unsupported, FormOf([1, 2, 3]));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-2")]
    public void A_worn_index_mask_legacy_material_gets_a_sound_copy()
    {
        using var kit = new Kit();
        var up = kit.Put("Old Top", "mt_top_a.mtrl", Kit.FormA());
        var plan = BuildOne(kit, Kit.TopPath, up);

        var u = Assert.Single(plan.Units);
        Assert.Empty(plan.Left);
        Assert.Equal(Kit.TopPath, u.MaterialGamePath);
        Assert.Equal(LegacyForm.IndexMask, u.Form);
        Assert.Null(u.Mask);
        Assert.Equal("character.shpk", CharacterShaderUpgrade.ShaderOf(u.MaterialBytes));
        Assert.True(IsSound(Kit.FormA(), u.MaterialBytes, LegacyForm.IndexMask, null));
        Assert.Matches(@"^4242-[0-9a-f]{16}\.mtrl$", u.MaterialCopyName);
        Assert.Equal("Old Top/mt_top_a.mtrl", u.SourceLabel);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-11")]
    public void A_worn_diffuse_legacy_material_gets_a_compatibility_mode_copy_with_a_derived_mask()
    {
        using var kit = new Kit();
        var up = kit.Put("Bandana", "mt_top_b.mtrl", Kit.FormB());
        var plan = BuildOne(kit, Kit.TopPath, up);

        var u = Assert.Single(plan.Units);
        Assert.Equal(LegacyForm.Diffuse, u.Form);
        Assert.NotNull(u.Mask);
        Assert.Equal((64, 64), (u.Mask!.Width, u.Mask.Height));
        Assert.StartsWith(MaskGameRoot, u.Mask.GamePath);
        Assert.Equal(TextureModeCompatibility, Keys(u.MaterialBytes)![TextureModeKey]);
        Assert.Equal(u.Mask.GamePath, MaskSlotPath(u.MaterialBytes));
        Assert.True(IsSound(Kit.FormB(), u.MaterialBytes, LegacyForm.Diffuse, u.Mask.GamePath));
        // No added occlusion; the specular pattern kept.
        Assert.All(Enumerable.Range(0, 64 * 64), i => Assert.Equal(255, u.Mask.Rgba![i * 4 + 2]));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-25")]
    public void Diffuse_form_keeps_its_mode_and_bindings_and_retargets_only_the_mask()
    {
        using var kit = new Kit();
        var src = Kit.FormB();
        var u = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("B", "m.mtrl", src)).Units);
        var before = TexturePaths(src)!;
        var after = TexturePaths(u.MaterialBytes)!;
        Assert.Equal(Samplers(src), Samplers(u.MaterialBytes));
        int maskIndex = Samplers(src)!.Single(s => s.Id == SamplerMask).TextureIndex;
        for (int i = 0; i < before.Length; i++)
            if (i == maskIndex) Assert.Equal(u.Mask!.GamePath, after[i]);
            else Assert.Equal(before[i], after[i]);
        Assert.Equal(Keys(src)![TextureModeKey], Keys(u.MaterialBytes)![TextureModeKey]);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-25")]
    public void Diffuse_form_rows_take_the_dawntrail_meanings()
    {
        using var kit = new Kit();
        var src = Kit.FormB();
        int row = GearMaterialWriter.ColorTableStart(src) + 2 * 64;
        BitConverter.TryWriteBytes(src.AsSpan(row + 3 * 2), (Half)20f);
        BitConverter.TryWriteBytes(src.AsSpan(row + 7 * 2), (Half)1f);
        var u = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("B", "m.mtrl", src)).Units);
        int at = GearMaterialWriter.ColorTableStart(u.MaterialBytes) + 2 * 64;
        Assert.Equal((Half)1f, BitConverter.ToHalf(u.MaterialBytes, at + 3 * 2));
        Assert.Equal((Half)0f, BitConverter.ToHalf(u.MaterialBytes, at + 7 * 2));
        Assert.Equal((Half)CharacterShaderUpgrade.RoughnessFromShininess(20f), BitConverter.ToHalf(u.MaterialBytes, at + 16 * 2));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-26")]
    public void Copies_carry_zero_wind_constants()
    {
        using var kit = new Kit();
        foreach (var src in new[] { Kit.FormA(), Kit.FormB() })
        {
            var u = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("M" + src.Length, "m.mtrl", src)).Units);
            Assert.Equal([0f], Constant(u.MaterialBytes, MeshVolumeService.VertexMovementScaleId)!);
            Assert.Equal([0f], Constant(u.MaterialBytes, MeshVolumeService.VertexMovementMaxLengthId)!);
        }
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-7")]
    public void Only_recognised_dawntrail_table_mod_materials_are_upgraded()
    {
        using var kit = new Kit();
        var legacy = kit.Put("A", "legacy.mtrl", Kit.FormA());
        var dt = kit.Put("B", "dt.mtrl", CharacterShaderUpgrade.Upgrade(Kit.FormA())!);
        var old = Kit.FormA();
        ClearTableShape(old);
        var oldPath = kit.Put("C", "old.mtrl", old);
        var plan = Build([
            new Worn(Kit.TopPath, legacy),
            new Worn(Kit.DwnPath, dt),
            new Worn("chara/equipment/e0001/material/v0001/mt_c0201e0001_glv_a.mtrl", null),
            new Worn("chara/equipment/e0002/material/v0001/mt_c0201e0002_sho_a.mtrl", oldPath),
        ], kit.Inputs());
        Assert.Equal(Kit.TopPath, Assert.Single(plan.Units).MaterialGamePath);
        Assert.Equal(LeftReason.OldTable, Assert.Single(plan.Left).Reason);
    }

    private static void ClearTableShape(byte[] mtrl)
    {
        int flagsAt = GearMaterialWriter.ColorTableStart(mtrl) - mtrl[15];
        uint flags = BitConverter.ToUInt32(mtrl, flagsAt);
        BitConverter.TryWriteBytes(mtrl.AsSpan(flagsAt), flags & ~0xFF0u);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-1")]
    public void A_pre_dawntrail_table_is_left_as_old_table()
    {
        using var kit = new Kit();
        var old = Kit.FormA();
        ClearTableShape(old);
        var plan = BuildOne(kit, Kit.TopPath, kit.Put("C", "old.mtrl", old));
        Assert.Empty(plan.Units);
        Assert.Equal(LeftReason.OldTable, Assert.Single(plan.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-2")]
    public void Unreadable_or_unsound_is_left_and_never_written()
    {
        using var kit = new Kit();
        // Truncated past its header: the shader name still reads as legacy, but nothing behind it can be walked.
        var plan = BuildOne(kit, Kit.TopPath, kit.Put("T", "t.mtrl", Kit.FormA()[..200]));
        Assert.Empty(plan.Units);
        Assert.All(plan.Left, l => Assert.Equal(LeftReason.Unreadable, l.Reason));

        // A missing file reads as unreadable too.
        var gone = BuildOne(kit, Kit.TopPath, Path.Combine(kit.ModsRoot, "Gone", "x.mtrl"));
        Assert.Equal(LeftReason.Unreadable, Assert.Single(gone.Left).Reason);

        // IsSound refuses a header size off by one, or changed texture paths.
        var good = CharacterShaderUpgrade.Upgrade(Kit.FormA())!;
        var wind = WindNeutral(good)!;
        Assert.True(IsSound(Kit.FormA(), wind, LegacyForm.IndexMask, null));
        var badSize = (byte[])wind.Clone();
        BitConverter.TryWriteBytes(badSize.AsSpan(4), (ushort)(wind.Length - 1));
        Assert.False(IsSound(Kit.FormA(), badSize, LegacyForm.IndexMask, null));
        var paths = TexturePaths(Kit.FormA())!;
        var retargeted = TextureLoader.RetargetTexturePaths(wind, new Dictionary<string, string> { [paths[0]] = "chara/x/y.tex" })!;
        Assert.False(IsSound(Kit.FormA(), retargeted, LegacyForm.IndexMask, null));
        // Nothing upgraded means nothing written.
        Assert.False(Directory.Exists(ManagedModPaths.ShaderUpgradeDir(kit.ManagedRoot)));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-3")]
    public void Already_character_shader_is_untouched()
    {
        using var kit = new Kit();
        var plan = BuildOne(kit, Kit.TopPath, kit.Put("D", "d.mtrl", CharacterShaderUpgrade.Upgrade(Kit.FormA())!));
        Assert.Empty(plan.Units);
        Assert.Empty(plan.Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-35")]
    public void Unsupported_forms_are_left()
    {
        using var kit = new Kit();
        // Form A with the compatibility mode: neither form.
        var odd = Kit.FormA();
        odd = TextureLoader.EnsureShaderKey(odd, TextureModeKey, TextureModeCompatibility);
        Assert.Equal(LegacyForm.Unsupported, FormOf(odd));
        var plan = BuildOne(kit, Kit.TopPath, kit.Put("O", "o.mtrl", odd));
        Assert.Equal(LeftReason.UnsupportedForm, Assert.Single(plan.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-40")]
    public void Compat_mask_diffuse_form_is_left_unsupported()
    {
        using var kit = new Kit();
        var b = TextureLoader.EnsureShaderKey(Kit.FormB(), SpecularModeKey, SpecularModeMask);
        Assert.Equal(LegacyForm.Unsupported, FormOf(b));
        Assert.Equal(LeftReason.UnsupportedForm, Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("O", "o.mtrl", b)).Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-22")]
    public void Dye_table_survives_into_the_copy()
    {
        using var kit = new Kit();
        var src = Kit.FormA();
        int dyeAt = GearMaterialWriter.ColorTableStart(src) + 32 * 64 + 15 * 4;
        BitConverter.TryWriteBytes(src.AsSpan(dyeAt), (400u << 16) | 0x01);
        var u = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("Dy", "d.mtrl", src)).Units);
        uint v = BitConverter.ToUInt32(u.MaterialBytes, GearMaterialWriter.ColorTableStart(u.MaterialBytes) + 32 * 64 + 15 * 4);
        Assert.Equal(1400u, (v >> 16) & 0x7FF);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-34")]
    public void Dyed_diffuse_form_maps_its_dye_templates()
    {
        using var kit = new Kit();
        var src = Kit.FormB();
        int dyeAt = GearMaterialWriter.ColorTableStart(src) + 32 * 64 + 3 * 4;
        BitConverter.TryWriteBytes(src.AsSpan(dyeAt), (213u << 16) | 0x01 | 0x08);
        var u = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("Dy", "d.mtrl", src)).Units);
        uint v = BitConverter.ToUInt32(u.MaterialBytes, GearMaterialWriter.ColorTableStart(u.MaterialBytes) + 32 * 64 + 3 * 4);
        Assert.Equal(1213u, (v >> 16) & 0x7FF);
        Assert.Equal(0x01u | 0x20, v & 0xFFF);
    }

    // ── where files come from ───────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-7")]
    public void Only_equipment_and_accessory_materials_are_gear()
    {
        foreach (var s in new[] { "met", "top", "glv", "dwn", "sho" })
            Assert.True(IsGearMaterial($"chara/equipment/e0123/material/v0002/mt_c0201e0123_{s}_a.mtrl"));
        foreach (var s in new[] { "ear", "nek", "wrs", "rir", "ril" })
            Assert.True(IsGearMaterial($"chara/accessory/a0123/material/v0001/mt_c0201a0123_{s}_a.mtrl"));
        Assert.False(IsGearMaterial("chara/human/c0201/obj/hair/h0001/material/v0001/mt_c0201h0001_hir_a.mtrl"));
        Assert.False(IsGearMaterial("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl"));
        Assert.False(IsGearMaterial("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl"));
        Assert.False(IsGearMaterial("chara/weapon/w0101/obj/body/b0001/material/v0001/mt_w0101b0001_a.mtrl"));
        Assert.False(IsGearMaterial("chara/equipment/e0123/material/mt_c0201e0123_top_a.mtrl"));
        Assert.False(IsGearMaterial("/mt_c0201e0123_top_a.mtrl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-4")]
    public void Classify_game_served()
    {
        Assert.Equal(Source.Game, Classify(Kit.TopPath, null, @"C:\mods", @"C:\mods\Proteus"));
        Assert.Equal(Source.Game, Classify(Kit.TopPath, Kit.TopPath, @"C:\mods", @"C:\mods\Proteus"));
        Assert.Equal(Source.Game, Classify(Kit.TopPath, "some/relative/file.mtrl", @"C:\mods", @"C:\mods\Proteus"));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-6")]
    public void Classify_outside_the_mods_root()
    {
        Assert.Equal(Source.Outside, Classify(Kit.TopPath, @"D:\elsewhere\x.mtrl", @"C:\mods", @"C:\mods\Proteus"));
        Assert.Equal(Source.Mod, Classify(Kit.TopPath, @"C:\mods\Dress\x.mtrl", @"C:\mods", @"C:\mods\Proteus"));
        Assert.Equal(Source.Copy, Classify(Kit.TopPath, @"C:\mods\Proteus\ShaderUpgrade\1-ab.mtrl", @"C:\mods", @"C:\mods\Proteus"));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-5")]
    public void Published_or_own_output_paths_are_left_to_the_compositor()
    {
        using var kit = new Kit();
        Assert.Equal(Source.Own, Classify(Kit.TopPath, Path.Combine(kit.ManagedRoot, "materials", "x.mtrl"), kit.ModsRoot, kit.ManagedRoot));
        var own = BuildOne(kit, Kit.TopPath, Path.Combine(kit.ManagedRoot, "materials", "x.mtrl"));
        Assert.Equal(LeftReason.Compositor, Assert.Single(own.Left).Reason);

        var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Kit.TopPath };
        var pub = BuildOne(kit, Kit.TopPath, kit.Put("A", "a.mtrl", Kit.FormA()), published: published);
        Assert.Empty(pub.Units);
        Assert.Equal(LeftReason.Compositor, Assert.Single(pub.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-47")]
    public void Texture_level_compositor_ownership_is_form_B_mask_slot_only()
    {
        using var kit = new Kit();
        var bSlot = MaskSlotPath(Kit.FormB())!;
        var b = BuildOne(kit, Kit.TopPath, kit.Put("B", "b.mtrl", Kit.FormB()),
                         published: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bSlot });
        Assert.Empty(b.Units);
        Assert.Equal(LeftReason.Compositor, Assert.Single(b.Left).Reason);

        var aPaths = TexturePaths(Kit.FormA())!;
        var a = BuildOne(kit, Kit.TopPath, kit.Put("A", "a.mtrl", Kit.FormA()),
                         published: new HashSet<string>(aPaths, StringComparer.OrdinalIgnoreCase));
        var u = Assert.Single(a.Units);
        Assert.Equal(aPaths, TexturePaths(u.MaterialBytes));   // still samples the compositor's textures
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-17")]
    public void One_upstream_many_paths_one_copy()
    {
        using var kit = new Kit();
        var up = kit.Put("A", "a.mtrl", Kit.FormA());
        int reads = 0;
        var inp = kit.Inputs();
        inp = new Inputs
        {
            ModsRoot = inp.ModsRoot, ManagedRoot = inp.ManagedRoot, Pid = inp.Pid,
            Read = p => { reads++; return File.ReadAllBytes(p); },
            ResolveTexture = inp.ResolveTexture, DecodeNative = inp.DecodeNative, Stat = inp.Stat, MaskFileOk = inp.MaskFileOk,
            Memo = inp.Memo,
        };
        var plan = Build([new Worn(Kit.TopPath, up), new Worn(Kit.DwnPath, up)], inp);
        Assert.Equal(2, plan.Units.Count);
        Assert.Single(plan.Units.Select(u => u.MaterialCopyName).Distinct());
        Assert.Equal(1, reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-10")]
    public void Same_bytes_same_copy_name()
    {
        var a = CopyName(1, [1, 2, 3]);
        Assert.Equal(a, CopyName(1, [1, 2, 3]));
        Assert.NotEqual(a, CopyName(1, [1, 2, 4]));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-6")]
    public void A_changed_upstream_replaces_the_old_copy()
    {
        using var kit = new Kit();
        var first = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("A", "a.mtrl", Kit.FormA())).Units);
        var other = Kit.FormA();
        BitConverter.TryWriteBytes(other.AsSpan(GearMaterialWriter.ColorTableStart(other)), (Half)0.5f);
        var second = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("B", "b.mtrl", other)).Units);
        Assert.NotEqual(first.MaterialCopyName, second.MaterialCopyName);
        Assert.NotEqual(first.Upstream, second.Upstream);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-18")]
    public void Edited_upstream_gets_a_new_copy_and_a_wind_upgraded_one_is_dropped()
    {
        using var kit = new Kit();
        var path = kit.Put("A", "a.mtrl", Kit.FormA());
        var before = Assert.Single(BuildOne(kit, Kit.TopPath, path).Units).MaterialCopyName;
        var edited = Kit.FormA();
        BitConverter.TryWriteBytes(edited.AsSpan(GearMaterialWriter.ColorTableStart(edited) + 64), (Half)0.25f);
        File.WriteAllBytes(path, edited);
        Assert.NotEqual(before, Assert.Single(BuildOne(kit, Kit.TopPath, path).Units).MaterialCopyName);

        // The wind brush upgraded the file itself: it is character.shpk now, and no unit is made.
        File.WriteAllBytes(path, CharacterShaderUpgrade.Upgrade(edited)!);
        Assert.Empty(BuildOne(kit, Kit.TopPath, path).Units);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-24")]
    public void Mod_moved_peeks_and_copies_follow_the_managed_root()
    {
        Assert.True(SignalFor(Signal.ModMoved).Peek);
        using var kit = new Kit();
        var renamed = Path.Combine(kit.ModsRoot, "Proteus Renamed");
        Assert.Equal(Source.Copy, Classify(Kit.TopPath, Path.Combine(renamed, "ShaderUpgrade", "1-ab.mtrl"), kit.ModsRoot, renamed));
        var copy = ShaderUpgradeCopies.WriteMaterial(renamed, "1-aaaaaaaaaaaaaaaa.mtrl", [1, 2, 3], (p, b) => File.WriteAllBytes(p, b));
        Assert.StartsWith(Path.Combine(renamed, "ShaderUpgrade"), copy);
    }

    // ── the mask ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-38")]
    public void DeriveMask_maps_grey_and_coloured_specular()
    {
        byte[] src = [100, 100, 100, 7, 255, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255];
        var m = DeriveMask(src, MaskGloss.InverseSpecular);
        Assert.Equal(new byte[] { 100, 155, 255, 255 }, m[..4]);
        Assert.Equal(Luma(255, 0, 0), m[4]);
        Assert.Equal(255 - Luma(255, 0, 0), m[5]);
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, m[8..12]);
        Assert.Equal(new byte[] { 255, 1, 255, 255 }, m[12..16]);   // G never below 1
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-46")]
    public void MaskGloss_strategies_set_green_and_rename_the_mask()
    {
        var (rgba, w, h) = Kit.Spec();
        var low = MaskFrom(rgba, w, h, MaskGloss.LowConstant).Rgba;
        var mid = MaskFrom(rgba, w, h, MaskGloss.MidConstant).Rgba;
        var inv = MaskFrom(rgba, w, h, MaskGloss.InverseSpecular).Rgba;
        Assert.All(Enumerable.Range(0, w * h), i => Assert.Equal(26, low[i * 4 + 1]));
        Assert.All(Enumerable.Range(0, w * h), i => Assert.Equal(128, mid[i * 4 + 1]));
        Assert.All(Enumerable.Range(0, w * h), i => Assert.Equal(Math.Max(1, 255 - inv[i * 4]), inv[i * 4 + 1]));
        var names = new[] { MaskHash(low, w, h, MaskGloss.LowConstant, TexEncoding.Bc7),
                            MaskHash(mid, w, h, MaskGloss.MidConstant, TexEncoding.Bc7),
                            MaskHash(inv, w, h, MaskGloss.InverseSpecular, TexEncoding.Bc7) };
        Assert.Equal(3, names.Distinct().Count());
        Assert.False(SignalFor(Signal.Enabled).DelayMs > 0);   // a strategy change walks at once
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-46")]
    public void MaskGloss_defaults_to_inverse_specular()
    {
        using var kit = new Kit();
        Assert.Equal(MaskGloss.InverseSpecular, new Inputs
        {
            ModsRoot = null, ManagedRoot = kit.ManagedRoot, Pid = 1, Read = _ => null, ResolveTexture = _ => null,
            DecodeNative = (_, _) => null, Stat = _ => null, MaskFileOk = _ => false, Memo = new MaskMemo(),
        }.Gloss);
        Assert.Equal("inverse-specular", ShaderUpgradeWatcher.GlossName(default));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-45")]
    public void Mask_keeps_native_size_capped_at_2048()
    {
        Assert.Equal((64, 64), CapSize(64, 64));
        Assert.Equal((2048, 2048), CapSize(4096, 4096));
        Assert.Equal((2048, 1024), CapSize(4096, 2048));
        Assert.Equal((2048, 2048), CapSize(2048, 2048));
        var (rgba, w, h) = Kit.Spec();
        var m = MaskFrom(rgba, w, h, MaskGloss.InverseSpecular);
        Assert.Equal((64, 64), (m.W, m.H));
        var big = new byte[4096 * 2048 * 4];
        var bm = MaskFrom(big, 4096, 2048, MaskGloss.MidConstant);
        Assert.Equal((2048, 1024), (bm.W, bm.H));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-45")]
    public void Build_never_uses_the_upscaling_base_loader()
    {
        // The watcher decodes the specular map through LoadRawFile + LoadTexBytesAsRgba, never LoadBaseTexture (which
        // upscales to 4096).
        var src = File.ReadAllText(SourceFile("Proteus/Services/ShaderUpgrade/ShaderUpgradeWatcher.cs"));
        Assert.DoesNotContain("LoadBaseTexture", src);
        Assert.Contains("LoadTexBytesAsRgba", src);
        Assert.DoesNotContain("LoadBaseTexture", File.ReadAllText(SourceFile("Proteus/Services/ShaderUpgrade/ShaderUpgradePlan.cs")));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-50")]
    public void Mask_game_path_is_lowercase_ascii_and_matches_the_retargeted_string()
    {
        var p = MaskGamePath("ABCDEF0123456789");
        Assert.Equal("chara/proteus/shaderupgrade/abcdef0123456789_mask.tex", p);
        Assert.All(p, c => Assert.True(c < 128 && !char.IsUpper(c)));
        using var kit = new Kit();
        var u = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("B", "b.mtrl", Kit.FormB())).Units);
        Assert.Equal(u.Mask!.GamePath, MaskSlotPath(u.MaterialBytes));
        Assert.Equal(u.Mask.GamePath, u.Mask.GamePath.ToLowerInvariant());
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-39")]
    public void Original_specular_path_is_never_redirected()
    {
        using var kit = new Kit();
        var slot = MaskSlotPath(Kit.FormB())!;
        var u = Assert.Single(BuildOne(kit, Kit.TopPath, kit.Put("B", "b.mtrl", Kit.FormB())).Units);
        var unit = new RedirectUnit(u.MaterialGamePath, "c.mtrl", new RedirectMask(u.Mask!.GamePath, "m.tex"));
        var map = ShaderUpgradeRedirects.MapOf([unit]);
        Assert.DoesNotContain(slot, map.Keys);
        Assert.Contains(u.Mask.GamePath, map.Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-51")]
    public void Unchanged_walk_decodes_nothing()
    {
        using var kit = new Kit();
        kit.SpecDisk = kit.Put("B", "c0201e6024_top_b_s.tex", Kit.Fixture("legacy_spec_s.tex"));
        var up = kit.Put("B", "b.mtrl", Kit.FormB());
        var first = Assert.Single(BuildOne(kit, Kit.TopPath, up).Units);
        Assert.Equal(1, kit.Decodes);
        // Written, as the watcher does, and memoised.
        ShaderUpgradeCopies.WriteMask(kit.ManagedRoot, first.Mask!, TexEncoding.Uncompressed, Kit.WriteTex);
        kit.Memo.Put(first.Mask!.Key, new MaskMemo.Entry(first.Mask.CopyName, first.Mask.GamePath, 64, 64));

        var again = Build([new Worn(Kit.TopPath, up)], kit.Inputs());
        Assert.Equal(1, kit.Decodes);
        Assert.Equal(1, again.MemoHits);
        Assert.Equal(first.MaterialCopyName, Assert.Single(again.Units).MaterialCopyName);

        // A changed timestamp, another strategy, or a deleted mask file decodes again.
        File.SetLastWriteTimeUtc(kit.SpecDisk, DateTime.UtcNow.AddMinutes(1));
        Build([new Worn(Kit.TopPath, up)], kit.Inputs());
        Assert.Equal(2, kit.Decodes);
        Build([new Worn(Kit.TopPath, up)], kit.Inputs(MaskGloss.MidConstant));
        Assert.Equal(3, kit.Decodes);
        Build([new Worn(Kit.TopPath, up)], kit.Inputs(MaskGloss.MidConstant));   // never written, so never memoised
        Assert.Equal(4, kit.Decodes);
        File.Delete(Path.Combine(ManagedModPaths.ShaderUpgradeDir(kit.ManagedRoot), first.Mask.CopyName));
        File.SetLastWriteTimeUtc(kit.SpecDisk, DateTime.UtcNow.AddMinutes(-1));
        kit.Memo.Put(MaskMemo.KeyFor(kit.SpecDisk, MaskSlotPath(Kit.FormB())!, kit.Inputs().Stat, MaskGloss.InverseSpecular,
                                     TexEncoding.Uncompressed),
                     new MaskMemo.Entry(first.Mask.CopyName, first.Mask.GamePath, 64, 64));
        Build([new Worn(Kit.TopPath, up)], kit.Inputs());
        Assert.Equal(5, kit.Decodes);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-53")]
    public void UnitsReadyToApply_drops_units_whose_mask_file_is_missing()
    {
        using var kit = new Kit();
        kit.SpecDisk = kit.Put("B", "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        var up = kit.Put("B", "b.mtrl", Kit.FormB());
        var u = Assert.Single(BuildOne(kit, Kit.TopPath, up).Units);
        var maskPath = ShaderUpgradeCopies.WriteMask(kit.ManagedRoot, u.Mask!, TexEncoding.Uncompressed, Kit.WriteTex);
        var copy = ShaderUpgradeCopies.WriteMaterial(kit.ManagedRoot, u.MaterialCopyName, u.MaterialBytes, (p, b) => File.WriteAllBytes(p, b));
        kit.Memo.Put(u.Mask!.Key, new MaskMemo.Entry(u.Mask.CopyName, u.Mask.GamePath, 64, 64));
        var unit = new RedirectUnit(u.MaterialGamePath, copy, new RedirectMask(u.Mask.GamePath, maskPath));

        File.Delete(maskPath);
        var (ready, missing) = UnitsReadyToApply([unit], File.Exists, TextureLoader.IsCompleteTex);
        Assert.Empty(ready);
        Assert.Single(missing);
        kit.Memo.EvictMask(Path.GetFileName(maskPath));
        int before = kit.Decodes;
        Build([new Worn(Kit.TopPath, up)], kit.Inputs());
        Assert.Equal(before + 1, kit.Decodes);
    }

    // ── signals, reloads, peeks ─────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-2")]
    public void Equip_schedules_a_walk_after_the_settle_time() => Assert.Equal((750, false), SignalFor(Signal.Equip));

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-3")]
    public void Turning_on_or_starting_evaluates_what_is_worn()
    {
        Assert.Equal(TransitionKind.Evaluate, Transition(false, true));
        Assert.Equal((0, true), SignalFor(Signal.Enabled));
        Assert.Equal((0, true), SignalFor(Signal.PenumbraReady));
        Assert.Equal(TransitionKind.None, Transition(true, true));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-5")]
    public void Turning_off_reloads_even_with_auto_redraw_off()
    {
        Assert.Equal(TransitionKind.Teardown, Transition(true, false));
        Assert.NotEqual(ReloadRoute.None, ChooseReloadRoute(autoRedraw: false, userRequested: true, held: false,
                                                            useInPlace: true, glamourerAvailable: true, inPlaceBroken: false,
                                                            escalate: false));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-15")]
    public void Auto_redraw_off_means_no_reload_route()
        => Assert.Equal(ReloadRoute.None, ChooseReloadRoute(false, false, false, true, true, false, false));

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-16")]
    public void A_live_redraw_hold_defers()
        => Assert.Equal(ReloadRoute.Deferred, ChooseReloadRoute(true, false, held: true, true, true, false, false));

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-9")]
    public void Escalation_goes_in_place_then_redraw_then_gives_up()
    {
        var e = new Escalation();
        Assert.Equal(Escalation.Next.InPlace, e.Fail("a"));
        Assert.Equal(Escalation.Next.Redraw, e.Fail("a"));
        Assert.Equal(Escalation.Next.GaveUp, e.Fail("a"));
        Assert.Equal(Escalation.Next.GaveUp, e.Fail("a"));
        Assert.Equal(Escalation.Next.InPlace, e.Fail("b"));
        Assert.Equal(ReloadRoute.Redraw, ChooseReloadRoute(true, false, false, true, true, false, escalate: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-31")]
    public void Reload_route_is_learned_once_per_session()
    {
        var l = new ReloadLearning();
        Assert.Null(l.InPlaceLoadsMaterials);
        l.Observe(ReloadRoute.InPlace, resolvesToCopy: true, loadedCopy: true);
        Assert.True(l.InPlaceLoadsMaterials);
        l.Observe(ReloadRoute.InPlace, resolvesToCopy: true, loadedCopy: false);   // a later miss does not unlearn it
        Assert.True(l.InPlaceLoadsMaterials);
        var broken = new ReloadLearning();
        broken.Observe(ReloadRoute.InPlace, true, false);
        Assert.True(broken.Broken);
        Assert.Equal(ReloadRoute.Redraw, ChooseReloadRoute(true, false, false, true, true, broken.Broken, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-43")]
    public void Learning_counts_only_resolved_but_not_loaded_verifies()
    {
        var l = new ReloadLearning();
        l.Observe(ReloadRoute.InPlace, resolvesToCopy: false, loadedCopy: false);   // outranked or mid-rebuild
        Assert.Null(l.InPlaceLoadsMaterials);
        l.Observe(ReloadRoute.Redraw, resolvesToCopy: true, loadedCopy: false);    // not an in-place reload
        Assert.Null(l.InPlaceLoadsMaterials);
        l.Observe(ReloadRoute.InPlace, resolvesToCopy: true, loadedCopy: false);
        Assert.False(l.InPlaceLoadsMaterials);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-32")]
    public void Master_switch_off_tears_down_first_and_forces_a_full_redraw_when_in_place_is_unproven()
    {
        Assert.Equal((true, true), OrderMasterSwitch(turningOff: true, anythingHeld: true, inPlaceLoadsMaterials: null));
        Assert.Equal((true, true), OrderMasterSwitch(true, true, false));
        Assert.Equal((true, false), OrderMasterSwitch(true, true, true));
        Assert.Equal((true, false), OrderMasterSwitch(true, false, null));
        Assert.Equal((false, false), OrderMasterSwitch(false, true, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-8")]
    public void Outranked_units_are_dropped_whole_when_material_or_mask_misses()
    {
        var a = new RedirectUnit("p/a.mtrl", @"C:\c\a.mtrl", null);
        var b = new RedirectUnit("p/b.mtrl", @"C:\c\b.mtrl", new RedirectMask("p/b_mask.tex", @"C:\c\b.tex"));
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["p/a.mtrl"] = @"C:\c\a.mtrl", ["p/b.mtrl"] = @"C:\c\b.mtrl", ["p/b_mask.tex"] = @"C:\other\x.tex",
        };
        Assert.Equal([b], Outranked([a, b], resolved));
        var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["p/a.mtrl"] = @"C:\c\a.mtrl" };
        Assert.False(NeedsReload([a], loaded));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-10")]
    public void NeedsReload_false_when_loaded_from_the_copies()
    {
        var a = new RedirectUnit("p/a.mtrl", @"C:\c\a.mtrl", null);
        Assert.False(NeedsReload([a], new Dictionary<string, string> { ["p/a.mtrl"] = "c:/c/a.mtrl" }));
        Assert.True(NeedsReload([a], new Dictionary<string, string> { ["p/a.mtrl"] = @"C:\mods\X\a.mtrl" }));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-21")]
    public void Gear_taken_off_drops_its_unit_without_reload()
    {
        using var kit = new Kit();
        var up = kit.Put("A", "a.mtrl", Kit.FormA());
        var both = Build([new Worn(Kit.TopPath, up), new Worn(Kit.DwnPath, up)], kit.Inputs());
        var after = Build([new Worn(Kit.TopPath, up)], kit.Inputs());
        Assert.Equal(2, both.Units.Count);
        Assert.Equal(Kit.TopPath, Assert.Single(after.Units).MaterialGamePath);
        var unit = new RedirectUnit(Kit.TopPath, @"C:\c\a.mtrl", null);
        // The piece taken off is no longer in the resource map, so nothing asks for a reload.
        Assert.False(NeedsReload([unit], new Dictionary<string, string> { [Kit.TopPath] = @"C:\c\a.mtrl" }));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-20")]
    public void GearMaterials_is_null_mid_rebuild()
    {
        var rebuilding = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\f.mdl"] = ["chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl"],
            [@"C:\h.mtrl"] = ["chara/human/c0201/obj/hair/h0001/material/v0001/mt_c0201h0001_hir_a.mtrl"],
        };
        Assert.Null(GearMaterials(rebuilding));
        Assert.Null(GearMaterials(null));
        var drawn = new Dictionary<string, HashSet<string>>(rebuilding, StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\t.mdl"] = ["chara/equipment/e6024/model/c0201e6024_top.mdl"],
            [@"C:\t.mtrl"] = [Kit.TopPath],
        };
        var got = GearMaterials(drawn)!;
        Assert.Equal(@"C:\t.mtrl", Assert.Single(got).Value);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-14")]
    public void WaitingFor_penumbra_and_managed_mod()
    {
        Assert.Equal(Waiting.Penumbra, WaitingFor(false, @"C:\m\Proteus", true));
        Assert.Equal(Waiting.Penumbra, WaitingFor(true, null, false));
        Assert.Equal(Waiting.ManagedMod, WaitingFor(true, @"C:\m\Proteus", false));
        Assert.Equal(Waiting.None, WaitingFor(true, @"C:\m\Proteus", true));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-23")]
    public void Signal_table()
    {
        Assert.Equal((750, false), SignalFor(Signal.Equip));
        Assert.Equal((250, false), SignalFor(Signal.Redraw));
        Assert.Equal((400, true), SignalFor(Signal.ModSetting));
        Assert.Equal((250, true), SignalFor(Signal.Collection));
        Assert.Equal((250, false), SignalFor(Signal.Published));
        Assert.Equal((500, false), SignalFor(Signal.BlankRetry));
        Assert.Equal((1000, false), SignalFor(Signal.Verify));
        // The managed mod's own edits never peek.
        var player = Guid.NewGuid();
        Assert.False(PeekFor(player, player, SidecarDiscoveryService.ManagedModDir, true, new HashSet<string>(), _ => true));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-6")]
    public void Setting_change_on_a_source_or_shipping_mod_peeks()
    {
        var player = Guid.NewGuid();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dress" };
        Assert.True(PeekFor(player, player, "Dress", true, sources, _ => false));
        Assert.True(PeekFor(player, player, "Recolour", true, sources, m => m == "Recolour"));
        Assert.True(PeekFor(player, player, "", true, sources, _ => false));
        Assert.True(SignalFor(Signal.Collection).Peek);
        Assert.True(SignalFor(Signal.ModMoved).Peek);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-28")]
    public void PeekFor_ignores_unrelated_mods_other_collections_and_the_managed_mod()
    {
        var player = Guid.NewGuid();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Dress" };
        Assert.False(PeekFor(player, player, "Unrelated", true, sources, _ => false));
        Assert.False(PeekFor(Guid.NewGuid(), player, "Dress", true, sources, _ => true));
        Assert.False(PeekFor(player, player, "Proteus", true, sources, _ => true));
        Assert.False(PeekFor(player, player, "Dress", anythingRedirected: false, sources, _ => true));
        Assert.False(PeekFor(player, null, "Dress", true, sources, _ => true));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-44")]
    public void PeekCacheKey_changes_with_any_mod_json()
    {
        var files = new List<(string, long)> { ("meta.json", 10), ("default_mod.json", 20), ("group_001_a.json", 30) };
        Assert.Equal(30, PeekCacheKey(files));
        files[1] = ("default_mod.json", 40);
        Assert.Equal(40, PeekCacheKey(files));
        files.Add(("readme.json", 99));   // not an option file
        Assert.Equal(40, PeekCacheKey(files));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-27")]
    public void PeekSettle_accepts_waits_and_fails_open()
    {
        bool IsCopy(string? v) => v?.Contains("ShaderUpgrade") == true;
        var a = new Dictionary<string, string?> { ["p"] = @"C:\mods\X\a.mtrl" };
        var copy = new Dictionary<string, string?> { ["p"] = @"C:\mods\Proteus\ShaderUpgrade\1-a.mtrl" };
        Assert.Equal(PeekSettle.Outcome.Wait, PeekSettle.Step(null, a, IsCopy, 50));
        Assert.Equal(PeekSettle.Outcome.Accept, PeekSettle.Step(a, a, IsCopy, 100));
        Assert.Equal(PeekSettle.Outcome.Wait, PeekSettle.Step(copy, copy, IsCopy, 300));
        Assert.Equal(PeekSettle.Outcome.FailOpen, PeekSettle.Step(copy, copy, IsCopy, 600));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-29")]
    public void Upgrade_first_then_published_yields_the_path()
    {
        using var kit = new Kit();
        var up = kit.Put("A", "a.mtrl", Kit.FormA());
        Assert.Single(BuildOne(kit, Kit.TopPath, up).Units);
        // The compositor starts publishing the path: the next walk (a published-set change walks) yields it.
        var after = BuildOne(kit, Kit.TopPath, up, published: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Kit.TopPath });
        Assert.Empty(after.Units);
        Assert.Equal(LeftReason.Compositor, Assert.Single(after.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-42")]
    public void Any_published_set_change_walks() => Assert.Equal((250, false), SignalFor(Signal.Published));

    private static string SourceFile(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }
}
