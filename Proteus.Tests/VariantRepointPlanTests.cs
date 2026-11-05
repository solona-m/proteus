using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.VariantRepointPlan;
using static Proteus.Tests.VariantRepointTestKit;

namespace Proteus.Tests;

/// <summary>The "Gear variants" policy (<see cref="VariantRepointPlan"/>): which pieces are repointed, and why others are left.</summary>
public class VariantRepointPlanTests
{
    /// <summary>The single item write of a plan.</summary>
    private static ItemWrite Only(Plan plan) => Assert.Single(Assert.Single(plan.PerMod).Items);

    private static RepointReason LeftReason(Plan plan) => Assert.Single(plan.Left).Reason;

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-2")]
    public void Dressed_entry_is_copied_whole_like_texttools()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        // Variant 1 is the mod's; variant 3 differs in every field, its own sound included.
        k.Imc(Top, E(1), E(1, 2, 0x3, 4, 5, 6), E(1), E(2, 7, 0x10, 9, 8, 1));
        var write = Only(k.Plan([k.Worn(Top, 3, mod)]));
        var (variant, entry) = Assert.Single(write.Add);
        Assert.Equal(3, variant);
        Assert.Equal(E(1, 2, 0x3, 4, 5, 6), entry);
        // TexTools' ApplyAllButton_Click copies XivImc.Mask, whose top six bits are the sound: so is the sound here.
        Assert.Equal(4, entry.SoundId);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-2")]
    public void Dressed_entry_prefers_mods_own_default_entry()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        [Imc(Top, 1, E(1, attr: 0x7))]);
        k.Imc(Top, E(1), E(1, attr: 0x1), E(2));
        Assert.Equal(E(1, attr: 0x7), Only(k.Plan([k.Worn(Top, 2, mod)])).Add.Single().Entry);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-36")]
    public void Other_mods_edit_of_dressed_variant_not_imported()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1, attr: 0x1), E(2));
        // Another mod hides parts of variant 1: its mask must not travel into this mod.
        k.Collection[new('e', 6255, "Body", 1)] = E(1, attr: 0x200, vfx: 3);
        Assert.Equal(E(1, attr: 0x1), Only(k.Plan([k.Worn(Top, 2, mod)])).Add.Single().Entry);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-1")]
    public void Reload_and_redraw_follow_a_write()
    {
        Assert.True(RedrawAfter(RunKind.Repoint, autoRedraw: true, changedAny: true));
        Assert.False(RedrawAfter(RunKind.Repoint, autoRedraw: true, changedAny: false));
        Assert.True(RedrawAfter(RunKind.RepointNow, autoRedraw: false, changedAny: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-25")]
    public void No_redraw_after_an_automatic_write_with_auto_redraw_off()
        => Assert.False(RedrawAfter(RunKind.Repoint, autoRedraw: false, changedAny: true));

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-4")]
    public void Setting_off_by_default_and_transitions()
    {
        Assert.False(new Configuration().AutoRepointVariants);
        var cfg = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{\"Version\":9}")!;
        Assert.False(cfg.AutoRepointVariants);
        Assert.Equal(9, cfg.Version);
        Assert.Equal(ShaderUpgradePlan.TransitionKind.Evaluate, ShaderUpgradePlan.Transition(false, true));
        Assert.Equal(ShaderUpgradePlan.TransitionKind.Stop, ShaderUpgradePlan.Transition(true, false));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-8")]
    public void Announces_once_per_mod()
    {
        var announced = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        Assert.True(ShaderUpgradePlan.ShouldAnnounce(announced, "Dress"));
        Assert.False(ShaderUpgradePlan.ShouldAnnounce(announced, "dress"));
        Assert.True(ShaderUpgradePlan.ShouldAnnounce(announced, "Skirt"));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-1")]
    public void Several_folders_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [MaterialPath(Top, 1)] = "a.mtrl" },
                        groups: [("Colour", "Single", [("Blue", new() { [MaterialPath(Top, 2)] = "b.mtrl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        var plan = k.Plan([k.Worn(Top, 3, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.SeveralVariants, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-2")]
    public void Model_only_mod_not_considered()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Model", new() { [ModelPath(Top)] = "top.mdl" });
        k.Imc(Top, E(1), E(1), E(2));
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Empty(plan.Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-3")]
    public void Worn_variant_already_right_does_nothing()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(1));
        var plan = k.Plan([k.Worn(Top, 3, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Empty(plan.Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-4")]
    public void Non_mod_models_ignored()
    {
        using var k = new VariantRepointTestKit();
        k.Imc(Top, E(1), E(1), E(2));
        var game = new WornItem(Top, 2, ModelPath(Top), ModelPath(Top));
        var own = new WornItem(Top, 2, ModelPath(Top), Path.Combine(k.ManagedRoot, "files", "top.mdl"));
        var outside = new WornItem(Top, 2, ModelPath(Top), Path.Combine(Path.GetTempPath(), "elsewhere", "top.mdl"));
        var plan = k.Plan([game, own, outside]);
        Assert.Empty(plan.PerMod);
        Assert.Empty(plan.Left);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-5")]
    public void Proteus_pack_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Pack", Top);
        Directory.CreateDirectory(Path.Combine(mod, SidecarDiscoveryService.SidecarSubdir));
        File.WriteAllText(Path.Combine(mod, SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile), "{}");
        k.Imc(Top, E(1), E(1), E(2));
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.ProteusPack, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-7")]
    public void Mod_imc_on_worn_variant_left()
    {
        using var k = new VariantRepointTestKit();
        k.Imc(Top, E(1), E(1), E(2), E(3));
        // In default data; and in an option that is not even on.
        var a = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                      [Imc(Top, 2, E(3))]);
        var b = k.Mod("B", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                      groups: [("Extras", "Multi", [("Glow", new(), [Imc(Top, 2, E(2, vfx: 1))])])]);
        foreach (var mod in new[] { a, b })
        {
            k.Collection.Clear();
            var plan = k.Plan([k.Worn(Top, 2, mod)]);
            Assert.Empty(plan.PerMod);
            Assert.Equal(RepointReason.ModImc, LeftReason(plan));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-7")]
    public void Mod_named_variants_skipped()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        groups: [("Extras", "Multi", [("Alt", new(), [Imc(Top, 3, E(3, attr: 2))])])]);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        var write = Only(k.Plan([k.Worn(Top, 2, mod)]));
        Assert.Equal([2], write.Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-8")]
    public void Author_imc_group_covers_item_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        rawGroups: [ImcGroup("Belt", Top)]);
        k.Imc(Top, E(1), E(1), E(2));
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.ImcGroup, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-8")]
    public void Pinned_group_other_variant_only_skips_it()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        rawGroups: [ImcGroup("Belt", Top, allVariants: false, variant: 3)]);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        Assert.Equal([2], Only(k.Plan([k.Worn(Top, 2, mod)])).Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-39")]
    public void Proteus_toggle_group_left_as_proteus_toggles()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        rawGroups: [ImcGroup(MeshToggleService.GroupNameFor("Body"), Top)]);
        ToggleRecord(mod, Top, MeshToggleService.GroupNameFor("Body"));
        k.Imc(Top, E(1), E(1), E(2));
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.ProteusToggles, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-39")]
    public void Recorded_toggle_group_deleted_falls_through()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("A", Top);
        ToggleRecord(mod, Top, MeshToggleService.GroupNameFor("Body"));   // the group itself is gone
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal([2], Only(k.Plan([k.Worn(Top, 2, mod)])).Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-39")]
    public void Recorded_toggle_group_no_longer_covers_item_falls_through()
    {
        using var k = new VariantRepointTestKit();
        var other = new Item('e', 6256, "top");
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        rawGroups: [ImcGroup(MeshToggleService.GroupNameFor("Body"), other)]);
        ToggleRecord(mod, Top, MeshToggleService.GroupNameFor("Body"));
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal([2], Only(k.Plan([k.Worn(Top, 2, mod)])).Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-9")]
    public void Worn_material_from_other_mod_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        var retex = k.Mod("Retexture");
        k.Imc(Top, E(1), E(1), E(2));
        k.Served[MaterialPath(Top, 2)] = Path.Combine(retex, "b.mtrl");
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.OtherModMaterial, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-9")]
    public void Other_variant_from_other_mod_skipped()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        var retex = k.Mod("Retexture");
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Served[MaterialPath(Top, 3)] = Path.Combine(retex, "c.mtrl");
        Assert.Equal([2], Only(k.Plan([k.Worn(Top, 2, mod)])).Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-40")]
    public void Material_served_by_own_mod_is_dressed()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        // The mod's own file swap serves v0002.
        k.Served[MaterialPath(Top, 2)] = Path.Combine(mod, "top_a.mtrl");
        var nothing = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(nothing.PerMod);
        Assert.Empty(nothing.Left);
        Assert.Equal([3], Only(k.Plan([k.Worn(Top, 3, mod)])).Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-10")]
    public void Unused_folder_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top, folder: 7);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.FolderUnused, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-11")]
    public void Missing_or_weapon_imc_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        var path = ImcEntrySource.ImcPathForSet(Top.SetTag)!;
        foreach (var bytes in new byte[]?[] { null, [2, 0], [2, 0, 1, 0, 0, 0, 0, 0, 0, 0] })
        {
            if (bytes == null) k.Game.Remove(path); else k.Game[path] = bytes;
            var plan = k.Plan([k.Worn(Top, 2, mod)]);
            Assert.Empty(plan.PerMod);
            Assert.Equal(RepointReason.NoImc, LeftReason(plan));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-12")]
    public void Unreadable_meta_writes_nothing()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        var plan = k.Plan([k.Worn(Top, 2, mod)], metaUnreadable: true);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.MetaUnreadable, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-13")]
    public void Variant_zero_included_none_beyond_count()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        // Default set 2 (wrong), variants 1..3; worn variant 9 is beyond the count, so the game uses the default set.
        k.Imc(Top, E(2), E(1), E(2), E(2));
        var write = Only(k.Plan([k.Worn(Top, 9, mod)]));
        Assert.Equal([0, 2, 3], write.Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-15")]
    public void Outranked_entries_reported_not_rewritten()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        k.Collection[new('e', 6255, "Body", 2)] = E(2, vfx: 4);   // a higher-priority mod
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.Outranked, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-37")]
    public void No_outranked_verdict_while_refresh_pending()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        k.Collection[new('e', 6255, "Body", 2)] = E(2);   // a stale collection, before the reload lands
        k.RefreshPending.Add(ShaderUpgradePlan.NormalPath(mod));
        var pending = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(pending.PerMod);
        Assert.Empty(pending.Left);
        k.RefreshPending.Clear();
        Assert.Equal(RepointReason.Outranked, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-38")]
    public void Attribute_only_difference_not_written()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        // Variant 3 already uses folder 1, with other parts: left alone (fewer edits than TexTools, on purpose).
        k.Imc(Top, E(1), E(1, attr: 1), E(2), E(1, attr: 6));
        Assert.Equal([2], Only(k.Plan([k.Worn(Top, 2, mod)])).Add.Select(a => a.Variant));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-22")]
    public void Walk_waits_for_auto_refit_and_shader_upgrade()
    {
        Assert.True(MayWalk(false, 0, false, false, false));
        Assert.False(MayWalk(true, 0, false, false, false));
        Assert.False(MayWalk(false, 1, false, false, false));
        Assert.False(MayWalk(false, 0, true, false, false));
        Assert.False(MayWalk(false, 0, false, true, false));
        Assert.False(MayWalk(false, 0, false, false, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-23")]
    public void Burst_collapses()
    {
        var (due, first) = ShaderUpgradePlan.NextDue(1000, 750, 0, 0);
        (due, first) = ShaderUpgradePlan.NextDue(1500, 750, due, first);
        (due, first) = ShaderUpgradePlan.NextDue(2900, 750, due, first);
        Assert.Equal(1000, first);
        Assert.Equal(1000 + ShaderUpgradePlan.MaxWalkWaitMs, due);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-24")]
    public void Waiting_for_penumbra()
    {
        Assert.Equal(ShaderUpgradePlan.Waiting.Penumbra, ShaderUpgradePlan.WaitingFor(false, "E:/mods"));
        Assert.Equal(ShaderUpgradePlan.Waiting.Penumbra, ShaderUpgradePlan.WaitingFor(true, null));
        Assert.Equal(ShaderUpgradePlan.Waiting.None, ShaderUpgradePlan.WaitingFor(true, "E:/mods"));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-27")]
    public void Two_items_one_mod_grouped()
    {
        using var k = new VariantRepointTestKit();
        var legs = new Item('e', 6255, "dwn");
        var mod = k.Mod("Outfit", new()
        {
            [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl",
            [ModelPath(legs)] = "dwn.mdl", [MaterialPath(legs, 1)] = "dwn_a.mtrl",
        });
        k.ImcSlots(Top.SetTag, (1, [E(1), E(1), E(2)]), (3, [E(1), E(1), E(2)]));
        var plan = k.Plan([k.Worn(Top, 2, mod), k.Worn(legs, 2, mod)]);
        var m = Assert.Single(plan.PerMod);
        Assert.Equal(2, m.Items.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-29")]
    public void Glasses_and_weapons_ignored()
    {
        using var k = new VariantRepointTestKit();
        var head = new Item('e', 6255, "met");
        var mod = k.Dress("Glasses", head);
        k.ImcSlots(head.SetTag, (0, [E(1), E(1), E(2)]));
        var glasses = k.Worn(head, 2, mod) with { Glasses = true };
        Assert.Empty(k.Plan([glasses]).PerMod);
        Assert.Null(ItemFor("w", 201));
        Assert.Null(ItemFor("hir", 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-32")]
    public void Combining_container_material_counts()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Combo", new() { [ModelPath(Top)] = "top.mdl" },
                        groups: [("Looks", "Combining", [("Shiny", new() { [MaterialPath(Top, 1)] = "a.mtrl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal(1, Only(k.Plan([k.Worn(Top, 2, mod)])).Folder);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-28")]
    public void Ring_items_are_two_slots()
    {
        Assert.Equal("RFinger", RightRing.EquipSlot);
        Assert.Equal("LFinger", LeftRing.EquipSlot);
        Assert.NotEqual(RightRing.Key, LeftRing.Key);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-14")]
    public void Penumbra_spelling_of_an_imc_entry_parses_the_same()
    {
        var proteus = System.Text.Json.JsonSerializer.SerializeToElement(Imc(Top, 2, E(1, 2, 3, 4, 5, 6)));
        var penumbra = System.Text.Json.JsonDocument.Parse(
            """{"Type":"Imc","Manipulation":{"Variant":"2","PrimaryId":6255,"EquipSlot":"Body","ObjectType":"Equipment","SecondaryId":0,"BodySlot":"Unknown","Entry":{"MaterialAnimationId":6,"VfxId":5,"SoundId":4,"AttributeMask":3,"DecalId":2,"MaterialId":1}}}""").RootElement;
        Assert.Equal(ParseImc(proteus), ParseImc(penumbra));
        Assert.Equal(E(1, 2, 3, 4, 5, 6), ParseImc(penumbra)!.Value.Entry);
    }
}
