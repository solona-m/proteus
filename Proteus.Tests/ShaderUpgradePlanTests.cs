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
/// The gear shader upgrade's policy (<see cref="ShaderUpgradePlan"/>): what is planned, how, and why anything is left —
/// against the two real legacy materials and the real grey specular map, in temporary v4 mods.
/// </summary>
public class ShaderUpgradePlanTests
{
    private static MaterialWrite One(Plan plan) => Assert.Single(Assert.Single(plan.PerMod).Writes);

    // ── forms and the recipe ────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-11")]
    public void FormOf_recognises_index_mask_and_diffuse()
    {
        Assert.Equal(LegacyForm.IndexMask, FormOf(Kit.FormA()));
        Assert.Equal(LegacyForm.Diffuse, FormOf(Kit.FormB()));
        Assert.Equal(LegacyForm.Unsupported, FormOf([1, 2, 3]));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-25")]
    public void Diffuse_form_keeps_its_mode_and_bindings_and_retargets_only_the_mask()
    {
        using var kit = new Kit();
        var dress = kit.Dress("Bandana", Kit.FormB(), withSpec: true);
        var w = One(kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")], _ => Path.Combine(dress, "tex", "top_s.tex")));
        var src = Kit.FormB();
        var before = TexturePaths(src)!;
        var after = TexturePaths(w.Bytes)!;
        Assert.Equal(Samplers(src), Samplers(w.Bytes));
        int maskIndex = Samplers(src)!.Single(s => s.Id == SamplerMask).TextureIndex;
        for (int i = 0; i < before.Length; i++)
            if (i == maskIndex) Assert.Equal(w.Mask!.GamePath, after[i]);
            else Assert.Equal(before[i], after[i]);
        Assert.Equal(TextureModeCompatibility, Keys(w.Bytes)![TextureModeKey]);
        Assert.Equal("character.shpk", CharacterShaderUpgrade.ShaderOf(w.Bytes));
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
        var dress = kit.Dress("B", src, withSpec: true);
        var w = One(kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")], _ => Path.Combine(dress, "tex", "top_s.tex")));
        int at = GearMaterialWriter.ColorTableStart(w.Bytes) + 2 * 64;
        Assert.Equal((Half)1f, BitConverter.ToHalf(w.Bytes, at + 3 * 2));
        Assert.Equal((Half)0f, BitConverter.ToHalf(w.Bytes, at + 7 * 2));
        Assert.Equal((Half)CharacterShaderUpgrade.RoughnessFromShininess(20f), BitConverter.ToHalf(w.Bytes, at + 16 * 2));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-26")]
    public void Writes_carry_zero_wind_constants()
    {
        using var kit = new Kit();
        var a = kit.Dress("A", Kit.FormA());
        var b = kit.Dress("B", Kit.FormB(), withSpec: true);
        foreach (var (mod, resolve) in new[] { (a, (Func<string, string?>?)null), (b, _ => Path.Combine(b, "tex", "top_s.tex")) })
        {
            var w = One(kit.Plan([Kit.Worn(Kit.TopPath, mod, "red/top.mtrl")], resolve));
            Assert.Equal([0f], Constant(w.Bytes, MeshVolumeService.VertexMovementScaleId)!);
            Assert.Equal([0f], Constant(w.Bytes, MeshVolumeService.VertexMovementMaxLengthId)!);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-22")]
    public void Dye_table_survives_into_the_write()
    {
        using var kit = new Kit();
        var src = Kit.FormA();
        int dyeAt = GearMaterialWriter.ColorTableStart(src) + 32 * 64 + 15 * 4;
        BitConverter.TryWriteBytes(src.AsSpan(dyeAt), (400u << 16) | 0x01);
        var w = One(kit.Plan([Kit.Worn(Kit.TopPath, kit.Dress("Dy", src), "red/top.mtrl")]));
        uint v = BitConverter.ToUInt32(w.Bytes, GearMaterialWriter.ColorTableStart(w.Bytes) + 32 * 64 + 15 * 4);
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
        var dress = kit.Dress("Dy", src, withSpec: true);
        var w = One(kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")], _ => Path.Combine(dress, "tex", "top_s.tex")));
        uint v = BitConverter.ToUInt32(w.Bytes, GearMaterialWriter.ColorTableStart(w.Bytes) + 32 * 64 + 3 * 4);
        Assert.Equal(1213u, (v >> 16) & 0x7FF);
        Assert.Equal(0x01u | 0x20, v & 0xFFF);
    }

    // ── what is planned and what is left ───────────────────────────────────

    private static void ClearTableShape(byte[] mtrl)
    {
        int flagsAt = GearMaterialWriter.ColorTableStart(mtrl) - mtrl[15];
        uint flags = BitConverter.ToUInt32(mtrl, flagsAt);
        BitConverter.TryWriteBytes(mtrl.AsSpan(flagsAt), flags & ~0xFF0u);
    }

    private const string Glv = "chara/equipment/e000{0}/material/v0001/mt_c0201e000{0}_glv_a.mtrl";
    private static string G(int n) => string.Format(Glv, n);

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-7")]
    public void Only_recognised_mod_materials_are_planned()
    {
        using var kit = new Kit();
        var compat = TextureLoader.EnsureShaderKey(Kit.FormB(), SpecularModeKey, SpecularModeMask);
        var odd = TextureLoader.EnsureShaderKey(Kit.FormA(), TextureModeKey, TextureModeCompatibility);
        var old = Kit.FormA();
        ClearTableShape(old);
        var mod = kit.Mod("Mixed", new Dictionary<string, string>
        {
            [Kit.TopPath] = "a.mtrl", [Kit.DwnPath] = "b.mtrl", [Kit.SpecPath] = "s.tex",
            [G(1)] = "compat.mtrl", [G(2)] = "odd.mtrl", [G(3)] = "dt.mtrl", [G(4)] = "old.mtrl",
        });
        Kit.Put(mod, "a.mtrl", Kit.FormA());
        Kit.Put(mod, "b.mtrl", Kit.FormB());
        Kit.Put(mod, "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        Kit.Put(mod, "compat.mtrl", compat);
        Kit.Put(mod, "odd.mtrl", odd);
        Kit.Put(mod, "dt.mtrl", CharacterShaderUpgrade.Upgrade(Kit.FormA())!);
        Kit.Put(mod, "old.mtrl", old);
        var pack = kit.Mod("Pack", new Dictionary<string, string> { [G(5)] = "p.mtrl" });
        Kit.Put(pack, "p.mtrl", Kit.FormA());
        Kit.Put(pack, "Proteus/metadata.json", "{}"u8.ToArray());

        var plan = kit.Plan([
            Kit.Worn(Kit.TopPath, mod, "a.mtrl"), Kit.Worn(Kit.DwnPath, mod, "b.mtrl"),
            Kit.Worn(G(1), mod, "compat.mtrl"), Kit.Worn(G(2), mod, "odd.mtrl"), Kit.Worn(G(3), mod, "dt.mtrl"),
            Kit.Worn(G(4), mod, "old.mtrl"), Kit.Worn(G(5), pack, "p.mtrl"),
            new Worn(G(6), null),
            new Worn(G(7), Path.Combine(kit.ManagedRoot, "materials", "x.mtrl")),
            new Worn(G(8), @"Q:\elsewhere\x.mtrl"),
        ], _ => Path.Combine(mod, "s.tex"));
        Assert.Equal(["a.mtrl", "b.mtrl"], Assert.Single(plan.PerMod).Writes.Select(w => w.Rel).Order());
        Assert.Equal(new[] { LeftReason.OldTable, LeftReason.UnsupportedForm, LeftReason.UnsupportedForm,
                             LeftReason.Compositor, LeftReason.ProteusPack }.Order(),
                     plan.Left.Select(l => l.Reason).Order());
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-1")]
    public void A_pre_dawntrail_table_is_left_as_old_table()
    {
        using var kit = new Kit();
        var old = Kit.FormA();
        ClearTableShape(old);
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, kit.Dress("Old", old), "red/top.mtrl")]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(LeftReason.OldTable, Assert.Single(plan.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-2")]
    public void Unreadable_or_unsound_is_left_and_never_written()
    {
        using var kit = new Kit();
        var dress = kit.Dress("T", Kit.FormA()[..200]);
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")]);
        Assert.Empty(plan.PerMod);
        Assert.All(plan.Left, l => Assert.Equal(LeftReason.Unreadable, l.Reason));
        var gone = kit.Plan([new Worn(Kit.TopPath, Path.Combine(dress, "gone.mtrl"))]);
        Assert.Equal(LeftReason.Unreadable, Assert.Single(gone.Left).Reason);

        var good = WindNeutral(CharacterShaderUpgrade.Upgrade(Kit.FormA())!)!;
        Assert.True(IsSound(Kit.FormA(), good, LegacyForm.IndexMask, null));
        var badSize = (byte[])good.Clone();
        BitConverter.TryWriteBytes(badSize.AsSpan(4), (ushort)(good.Length - 1));
        Assert.False(IsSound(Kit.FormA(), badSize, LegacyForm.IndexMask, null));
        var paths = TexturePaths(Kit.FormA())!;
        Assert.False(IsSound(Kit.FormA(),
                             TextureLoader.RetargetTexturePaths(good, new Dictionary<string, string> { [paths[0]] = "chara/x/y.tex" })!,
                             LegacyForm.IndexMask, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-3")]
    public void Already_character_shader_is_untouched()
    {
        using var kit = new Kit();
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, kit.Dress("D", CharacterShaderUpgrade.Upgrade(Kit.FormA())!), "red/top.mtrl")]);
        Assert.Empty(plan.PerMod);
        Assert.Empty(plan.Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-18")]
    public void A_wind_upgraded_material_is_skipped()
    {
        using var kit = new Kit();
        var dress = kit.Dress("W", CharacterShaderUpgrade.Upgrade(Kit.FormA())!);
        Assert.Empty(kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")]).PerMod);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-35")]
    public void Unsupported_forms_are_left()
    {
        using var kit = new Kit();
        var odd = TextureLoader.EnsureShaderKey(Kit.FormA(), TextureModeKey, TextureModeCompatibility);
        Assert.Equal(LegacyForm.Unsupported, FormOf(odd));
        Assert.Equal(LeftReason.UnsupportedForm,
                     Assert.Single(kit.Plan([Kit.Worn(Kit.TopPath, kit.Dress("O", odd), "red/top.mtrl")]).Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-40")]
    public void Compat_mask_diffuse_form_is_left_unsupported()
    {
        using var kit = new Kit();
        var b = TextureLoader.EnsureShaderKey(Kit.FormB(), SpecularModeKey, SpecularModeMask);
        Assert.Equal(LegacyForm.Unsupported, FormOf(b));
        Assert.Equal(LeftReason.UnsupportedForm,
                     Assert.Single(kit.Plan([Kit.Worn(Kit.TopPath, kit.Dress("O", b), "red/top.mtrl")]).Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-7")]
    public void Only_equipment_and_accessory_materials_are_gear()
    {
        foreach (var s in new[] { "met", "top", "glv", "dwn", "sho" })
            Assert.True(IsGearMaterial($"chara/equipment/e0123/material/v0002/mt_c0201e0123_{s}_a.mtrl"));
        foreach (var s in new[] { "ear", "nek", "wrs", "rir", "ril" })
            Assert.True(IsGearMaterial($"chara/accessory/a0123/material/v0001/mt_c0201a0123_{s}_a.mtrl"));
        Assert.False(IsGearMaterial("chara/human/c0201/obj/hair/h0001/material/v0001/mt_c0201h0001_hir_a.mtrl"));
        Assert.False(IsGearMaterial("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl"));
        Assert.False(IsGearMaterial("chara/weapon/w0101/obj/body/b0001/material/v0001/mt_w0101b0001_a.mtrl"));
        Assert.False(IsGearMaterial("chara/equipment/e0123/material/mt_c0201e0123_top_a.mtrl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-4")]
    public void Classify_game_served()
    {
        static bool No(string _) => false;
        Assert.Equal(Source.Game, Classify(Kit.TopPath, null, @"C:\mods", @"C:\mods\Proteus", No));
        Assert.Equal(Source.Game, Classify(Kit.TopPath, Kit.TopPath, @"C:\mods", @"C:\mods\Proteus", No));
        Assert.Equal(Source.Game, Classify(Kit.TopPath, "relative/file.mtrl", @"C:\mods", @"C:\mods\Proteus", No));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-6")]
    public void Classify_outside_the_mods_root()
    {
        static bool No(string _) => false;
        Assert.Equal(Source.Outside, Classify(Kit.TopPath, @"D:\elsewhere\x.mtrl", @"C:\mods", @"C:\mods\Proteus", No));
        Assert.Equal(Source.Mod, Classify(Kit.TopPath, @"C:\mods\Dress\x.mtrl", @"C:\mods", @"C:\mods\Proteus", No));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-5")]
    public void Managed_mod_output_is_left_to_the_compositor()
    {
        using var kit = new Kit();
        var plan = kit.Plan([new Worn(Kit.TopPath, Path.Combine(kit.ManagedRoot, "materials", "x.mtrl"))]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(LeftReason.Compositor, Assert.Single(plan.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-72")]
    public void Proteus_sidecar_pack_is_left()
    {
        using var kit = new Kit();
        var pack = kit.Dress("Pack", Kit.FormA());
        Kit.Put(pack, "Proteus/metadata.json", "{}"u8.ToArray());
        Assert.True(IsProteusPack(pack));
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, pack, "red/top.mtrl")]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(LeftReason.ProteusPack, Assert.Single(plan.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-17")]
    public void One_file_many_paths_one_write()
    {
        using var kit = new Kit();
        var mod = kit.Mod("M", new Dictionary<string, string> { [Kit.TopPath] = "a.mtrl", [Kit.DwnPath] = "a.mtrl" });
        Kit.Put(mod, "a.mtrl", Kit.FormA());
        Assert.Equal(2, One(kit.Plan([Kit.Worn(Kit.TopPath, mod, "a.mtrl"), Kit.Worn(Kit.DwnPath, mod, "a.mtrl")])).GamePaths.Count);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-6")]
    public void A_newly_drawn_file_is_planned()
    {
        using var kit = new Kit();
        var dress = kit.Dress("D", Kit.FormA());
        Assert.Equal("red/top.mtrl", One(kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")])).Rel);
        // The player picked the other option: its file is what is drawn now, and it is planned.
        Assert.Equal("blue/top.mtrl", One(kit.Plan([Kit.Worn(Kit.TopPath, dress, "blue/top.mtrl")])).Rel);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-56")]
    public void Diffuse_material_in_a_combining_container_is_left_and_index_mask_is_not()
    {
        using var kit = new Kit();
        var mod = kit.Mod("C", null, ("Mix", "Combining", [
            ("A", new Dictionary<string, string> { [Kit.TopPath] = "b.mtrl", [Kit.SpecPath] = "s.tex" }),
            ("B", new Dictionary<string, string> { [Kit.DwnPath] = "a.mtrl" })]));
        Kit.Put(mod, "b.mtrl", Kit.FormB());
        Kit.Put(mod, "a.mtrl", Kit.FormA());
        Kit.Put(mod, "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, mod, "b.mtrl"), Kit.Worn(Kit.DwnPath, mod, "a.mtrl")], _ => Path.Combine(mod, "s.tex"));
        Assert.Equal("a.mtrl", One(plan).Rel);
        Assert.Equal(LeftReason.UnsupportedPlacement, Assert.Single(plan.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-57")]
    public void Owners_disagreeing_on_the_specular_file_leave_the_material()
    {
        using var kit = new Kit();
        var mod = kit.Mod("Mx", null, ("Colour", "Single", [
            ("Red", new Dictionary<string, string> { [Kit.TopPath] = "top.mtrl", [Kit.SpecPath] = "red_s.tex" }),
            ("Blue", new Dictionary<string, string> { [Kit.TopPath] = "top.mtrl", [Kit.SpecPath] = "blue_s.tex" })]));
        Kit.Put(mod, "top.mtrl", Kit.FormB());
        Kit.Put(mod, "red_s.tex", Kit.Fixture("legacy_spec_s.tex"));
        Kit.Put(mod, "blue_s.tex", Kit.Fixture("legacy_spec_s.tex"));
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, mod, "top.mtrl")], _ => Path.Combine(mod, "red_s.tex"));
        Assert.Empty(plan.PerMod);
        Assert.Equal(LeftReason.MixedSpecular, Assert.Single(plan.Left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-57")]
    public void A_specular_map_from_an_independent_option_is_mixed_unless_default_data_ships_it()
    {
        using var kit = new Kit();
        // The material in default data, the _s chosen by a colour group: baking it would go stale when the colour changes.
        var mod = kit.Mod("Indep", new Dictionary<string, string> { [Kit.TopPath] = "top.mtrl" },
            ("Spec", "Single", [("Shiny", new Dictionary<string, string> { [Kit.SpecPath] = "shiny_s.tex" }),
                                ("Dull", new Dictionary<string, string> { [Kit.SpecPath] = "dull_s.tex" })]));
        Kit.Put(mod, "top.mtrl", Kit.FormB());
        Kit.Put(mod, "shiny_s.tex", Kit.Fixture("legacy_spec_s.tex"));
        Kit.Put(mod, "dull_s.tex", Kit.Fixture("legacy_spec_s.tex"));
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, mod, "top.mtrl")], _ => Path.Combine(mod, "shiny_s.tex"));
        Assert.Equal(LeftReason.MixedSpecular, Assert.Single(plan.Left).Reason);

        // The material in an option, the _s in default data: fine.
        var ok = kit.Mod("Def", new Dictionary<string, string> { [Kit.SpecPath] = "s.tex" },
            ("Colour", "Single", [("Red", new Dictionary<string, string> { [Kit.TopPath] = "top.mtrl" })]));
        Kit.Put(ok, "top.mtrl", Kit.FormB());
        Kit.Put(ok, "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        Assert.Single(kit.Plan([Kit.Worn(Kit.TopPath, ok, "top.mtrl")], _ => Path.Combine(ok, "s.tex")).PerMod);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-71")]
    public void Specular_from_another_mod_leaves_the_material()
    {
        using var kit = new Kit();
        var dress = kit.Dress("Dress", Kit.FormB());   // ships the material, not the _s
        var retex = kit.Mod("Retexture", new Dictionary<string, string> { [Kit.SpecPath] = "s.tex" });
        Kit.Put(retex, "s.tex", Kit.Fixture("legacy_spec_s.tex"));
        var owners = OwnersOf(HairPortPlan.ReadOwners(dress)!, "red/top.mtrl");

        Assert.Equal(SpecularKind.OtherMod, SpecularSource(Path.Combine(retex, "s.tex"), Kit.SpecPath, dress, kit.ModsRoot, owners).Kind);
        var plan = kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")], _ => Path.Combine(retex, "s.tex"));
        Assert.Empty(plan.PerMod);
        Assert.Equal(LeftReason.SpecularFromOtherMod, Assert.Single(plan.Left).Reason);

        var own = kit.Dress("Own", Kit.FormB(), withSpec: true);
        var ownOwners = OwnersOf(HairPortPlan.ReadOwners(own)!, "red/top.mtrl");
        Assert.Equal((SpecularKind.File, "tex/top_s.tex"),
                     SpecularSource(Path.Combine(own, "tex", "top_s.tex"), Kit.SpecPath, own, kit.ModsRoot, ownOwners));
        Assert.Equal(SpecularKind.Game, SpecularSource(Kit.SpecPath, Kit.SpecPath, dress, kit.ModsRoot, owners).Kind);
        Assert.Single(kit.Plan([Kit.Worn(Kit.TopPath, dress, "red/top.mtrl")], s => s).PerMod);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-12")]
    [Trait("Plan", "EDGE-dt-shader-upgrade-66")]
    public void Declined_files_are_left_until_upgrade_now()
    {
        using var kit = new Kit();
        var dress = kit.Dress("D", Kit.FormA());
        var worn = new[] { Kit.Worn(Kit.TopPath, dress, "red/top.mtrl") };
        Assert.True(kit.Upgrade(dress, worn).ChangedAny);
        Assert.Equal(ShaderUpgradeWriter.Undone.Restored, ShaderUpgradeWriter.RevertOrForget(dress, "red/top.mtrl", decline: true, out _));
        var plan = kit.Plan(worn);
        Assert.Empty(plan.PerMod);
        Assert.Equal(LeftReason.Declined, Assert.Single(plan.Left).Reason);
        ShaderUpgradeWriter.ClearDeclines(dress, ["red/top.mtrl"]);   // what "Upgrade what I'm wearing now" does
        Assert.Single(kit.Plan(worn).PerMod);
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
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, m[8..12]);
        Assert.Equal(new byte[] { 255, 1, 255, 255 }, m[12..16]);
        Assert.Equal("chara/equipment/e6024/texture/dt_abcdef0123456789_mask.tex",
                     MaskGamePath("chara/equipment/e6024/texture/c0201e6024_top_b_s.tex", "ABCDEF0123456789"));
        Assert.Equal("Shader Upgrade/abcdef0123456789_mask.tex", MaskRel("ABCDEF0123456789"));
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
        Assert.Equal(3, new[] { MaskHash(low, w, h, MaskGloss.LowConstant, TexEncoding.Bc7),
                                MaskHash(mid, w, h, MaskGloss.MidConstant, TexEncoding.Bc7),
                                MaskHash(inv, w, h, MaskGloss.InverseSpecular, TexEncoding.Bc7) }.Distinct().Count());
        Assert.Equal("inverse-specular", ShaderUpgradeWatcher.GlossName(default));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-45")]
    public void Mask_keeps_native_size_capped_at_2048()
    {
        Assert.Equal((64, 64), CapSize(64, 64));
        Assert.Equal((2048, 2048), CapSize(4096, 4096));
        Assert.Equal((2048, 1024), CapSize(4096, 2048));
        var (rgba, w, h) = Kit.Spec();
        var m = MaskFrom(rgba, w, h, MaskGloss.InverseSpecular);
        Assert.Equal((64, 64), (m.W, m.H));
        var bm = MaskFrom(new byte[4096 * 2048 * 4], 4096, 2048, MaskGloss.MidConstant);
        Assert.Equal((2048, 1024), (bm.W, bm.H));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-45")]
    public void Build_never_uses_the_upscaling_base_loader()
    {
        foreach (var f in new[] { "ShaderUpgradeWatcher.cs", "ShaderUpgradePlan.cs", "ShaderUpgradeWriter.cs" })
            Assert.DoesNotContain("LoadBaseTexture", File.ReadAllText(SourceFile("Proteus/Services/ShaderUpgrade/" + f)));
        Assert.Contains("LoadTexBytesAsRgba", File.ReadAllText(SourceFile("Proteus/Services/ShaderUpgrade/ShaderUpgradeWatcher.cs")));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-51")]
    public void Unchanged_specular_decodes_nothing_and_reuses_the_mask_file()
    {
        using var kit = new Kit();
        var a = kit.Dress("A", Kit.FormB(), withSpec: true);
        Func<string, string?> resolve = _ => Path.Combine(a, "tex", "top_s.tex");
        Assert.True(kit.Upgrade(a, [Kit.Worn(Kit.TopPath, a, "red/top.mtrl")], resolve).ChangedAny);
        Assert.Equal(1, kit.Decodes);
        // The other option ships the same material with the same _s: no decode, the same mask file, no pixels to write.
        var second = One(kit.Plan([Kit.Worn(Kit.TopPath, a, "blue/top.mtrl")], resolve));
        Assert.Equal(1, kit.Decodes);
        Assert.Null(second.Mask!.Rgba);
        File.SetLastWriteTimeUtc(Path.Combine(a, "tex", "top_s.tex"), DateTime.UtcNow.AddMinutes(1));
        kit.Plan([Kit.Worn(Kit.TopPath, a, "blue/top.mtrl")], resolve);
        Assert.Equal(2, kit.Decodes);
    }

    // ── signals and the run ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-2")]
    public void Equip_schedules_a_walk_after_the_settle_time() => Assert.Equal(750, SignalFor(Signal.Equip));

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-2")]
    public void Chat_notice_once_per_mod_per_session()
    {
        var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(ShouldAnnounce(said, "Dress"));
        Assert.False(ShouldAnnounce(said, "dress"));
        Assert.True(ShouldAnnounce(said, "Other"));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-3")]
    public void Turning_on_or_starting_evaluates_what_is_worn()
    {
        Assert.Equal(TransitionKind.Evaluate, Transition(false, true));
        Assert.Equal(0, SignalFor(Signal.Enabled));
        Assert.Equal(0, SignalFor(Signal.PenumbraReady));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-5")]
    public void Turning_off_stops_walks_and_undoes_nothing()
    {
        Assert.Equal(TransitionKind.Stop, Transition(true, false));
        var src = File.ReadAllText(SourceFile("Proteus/Services/ShaderUpgrade/ShaderUpgradeWatcher.cs"));
        var stop = src[src.IndexOf("case TransitionKind.Stop:", StringComparison.Ordinal)..];
        stop = stop[..stop.IndexOf("}", StringComparison.Ordinal)];
        Assert.DoesNotContain("RevertOrForget", stop);
        Assert.DoesNotContain("UndoWorn", stop);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-6")]
    public void Setting_and_collection_changes_walk()
    {
        Assert.Equal(400, SignalFor(Signal.ModSetting));
        Assert.Equal(400, SignalFor(Signal.Collection));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-24")]
    public void Mod_moved_walks() => Assert.Equal(0, SignalFor(Signal.ModMoved));

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-23")]
    public void Signal_table()
    {
        Assert.Equal(250, SignalFor(Signal.Redraw));
        Assert.Equal(500, SignalFor(Signal.BlankRetry));
        var (due, first) = NextDue(1000, 750, 0, 0);
        (due, first) = NextDue(1500, 750, due, first);
        Assert.Equal(2250, due);
        Assert.Equal(1000 + MaxWalkWaitMs, NextDue(2900, 750, due, first).Due);   // never postponed for ever
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-23")]
    public void A_walk_after_our_own_reload_plans_nothing()
    {
        using var kit = new Kit();
        var dress = kit.Dress("D", Kit.FormA());
        var worn = new[] { Kit.Worn(Kit.TopPath, dress, "red/top.mtrl") };
        Assert.True(kit.Upgrade(dress, worn).ChangedAny);
        var again = kit.Plan(worn);
        Assert.Empty(again.PerMod);
        Assert.Empty(again.Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-15")]
    public void Auto_redraw_off_reloads_without_redraw_but_undo_redraws()
    {
        Assert.False(RedrawAfter(RunKind.Upgrade, autoRedraw: false, changedAny: true));
        Assert.True(RedrawAfter(RunKind.UndoWorn, autoRedraw: false, changedAny: true));
        Assert.True(RedrawAfter(RunKind.UpgradeNow, autoRedraw: false, changedAny: true));
        Assert.True(RedrawAfter(RunKind.Upgrade, autoRedraw: true, changedAny: true));
        Assert.False(RedrawAfter(RunKind.UndoWorn, true, changedAny: false));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-16")]
    public void Redraw_is_never_held_and_the_walk_waits_out_the_auto_refit()
    {
        // Only the auto refit's own work may be held (RedrawHoldWiringTests); the walk waits while it is busy instead.
        var src = File.ReadAllText(SourceFile("Proteus/Services/ShaderUpgrade/ShaderUpgradeWatcher.cs"));
        Assert.Contains("RedrawForChangedModel(\"shader upgrade\")", src);
        Assert.DoesNotContain("deferWhileHeld", src);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-20")]
    public void GearMaterials_is_null_mid_rebuild()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\f.mdl"] = ["chara/human/c0201/obj/face/f0001/model/c0201f0001_fac.mdl"],
        };
        Assert.Null(GearMaterials(map));
        Assert.Null(GearMaterials(null));
        map[@"C:\t.mdl"] = ["chara/equipment/e6024/model/c0201e6024_top.mdl"];
        map[@"C:\t.mtrl"] = [Kit.TopPath];
        Assert.Equal(@"C:\t.mtrl", Assert.Single(GearMaterials(map)!).Value);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-63")]
    public void Walk_waits_while_auto_refit_is_busy()
    {
        Assert.True(MayWalk(false, 0, false, false));
        Assert.False(MayWalk(true, 0, false, false));
        Assert.False(MayWalk(false, 2, false, false));
        Assert.False(MayWalk(false, 0, true, false));
        Assert.False(MayWalk(false, 0, false, true));
        Assert.Equal(500, SignalFor(Signal.AutoRefitBusy));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-69")]
    public void Undo_and_upgrade_now_are_refused_while_busy()
    {
        Assert.True(MayStart(busy: false));
        Assert.False(MayStart(busy: true));
    }

    internal static string SourceFile(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }
}
