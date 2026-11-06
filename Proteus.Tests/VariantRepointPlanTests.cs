using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.VariantRepointPlan;
using static Proteus.Tests.VariantRepointTestKit;

namespace Proteus.Tests;

/// <summary>The "Gear variants" policy (<see cref="VariantRepointPlan"/>): which pieces get copies, and why others are left.</summary>
public class VariantRepointPlanTests
{
    private static ItemWrite Only(Plan plan) => Assert.Single(Assert.Single(plan.PerMod).Items);

    private static RepointReason LeftReason(Plan plan) => Assert.Single(plan.Left).Reason;

    private static List<int> Folders(Plan plan) => Only(plan).Folders.ToList();

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
    [Trait("Plan", "AC-imc-all-variants-2")]
    public void Each_option_keeps_its_own_file_under_the_new_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        var write = Only(k.Plan([k.Worn(Top, 2, mod)]));
        Assert.Equal(2, write.Copies.Count);
        Assert.Contains(write.Copies, c => c.Copy.Container.Option == "Red" && c.Copy.File == "red/m.mtrl"
                                           && c.Copy.GamePath == MaterialPath(Top, 2));
        Assert.Contains(write.Copies, c => c.Copy.Container.Option == "Blue" && c.Copy.File == "blue/m.mtrl");
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-4")]
    public void Setting_on_by_default_and_transitions()
    {
        Assert.True(new Configuration().AutoRepointVariants);
        var cfg = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{\"Version\":9}")!;
        Assert.True(cfg.AutoRepointVariants);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            "{\"Version\":9,\"AutoRepointVariants\":false}")!.AutoRepointVariants);
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
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-1")]
    public void Several_folders_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
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
    public void Worn_folder_already_right_does_nothing()
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
    [Trait("Plan", "EDGE-imc-all-variants-9")]
    public void Worn_material_from_retexture_mod_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Mod("Retexture", new() { [MaterialPath(Top, 2)] = "b.mtrl" });   // materials, no model
        k.Priority.Reverse();   // the retexture above the dress
        k.Imc(Top, E(1), E(1), E(2));
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.OtherModMaterial, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-9")]
    public void Other_folder_from_retexture_mod_skipped()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Mod("Retexture", new() { [MaterialPath(Top, 3)] = "c.mtrl" });
        k.Imc(Top, E(1), E(1), E(2), E(3));
        Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-9")]
    public void Worn_material_from_own_output_or_outside_left()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        foreach (var file in new[] { Path.Combine(k.ManagedRoot, "files", "b.mtrl"), Path.Combine(Path.GetTempPath(), "x", "b.mtrl") })
        {
            k.Served[MaterialPath(Top, 2)] = file;
            Assert.Equal(RepointReason.OtherModMaterial, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-9")]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    public void Competing_model_on_another_race_is_other_mod_material()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("moonfire lace", Top, material: "b");
        k.Mod("Rival", new() { [ModelPath(Top, "1401")] = "top.mdl", [MaterialPath(Top, 2, "b")] = "b.mtrl" });
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal(RepointReason.OtherModMaterial, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    [Trait("Plan", "EDGE-imc-all-variants-9")]
    public void Retexture_only_mod_stays_other_mod_material()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("moonfire lace", Top, material: "b");
        k.Mod("Retexture", new() { [MaterialPath(Top, 2, "b")] = "b.mtrl" });
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal(RepointReason.OtherModMaterial, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    public void Competing_model_redirect_in_inactive_option_counts()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("moonfire lace", Top, material: "b");
        k.Mod("Rival", new() { [MaterialPath(Top, 2, "b")] = "b.mtrl" },
              groups: [("Body", "Single", [("Off", new() { [ModelPath(Top)] = "top.mdl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    public void Competing_model_rule_applies_to_other_folders()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("moonfire lace", Top, material: "b");
        k.Dress("Rose", Top, folder: 3, material: "b");
        k.Imc(Top, E(1), E(1), E(2), E(3));
        Assert.Equal([2, 3], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    public void Mixed_names_retexture_wins()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("moonfire lace", new()
        {
            [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1, "a")] = "a.mtrl", [MaterialPath(Top, 1, "b")] = "b.mtrl",
        });
        k.Dress("Rose", Top, folder: 2, material: "a");
        k.Mod("Retexture", new() { [MaterialPath(Top, 2, "b")] = "rb.mtrl" });
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal(RepointReason.OtherModMaterial, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    public void Redirects_read_once_per_mod_per_walk()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("moonfire lace", Top, material: "b");
        k.Dress("Rose", Top, folder: 2, material: "b");
        k.ImcSlots(Top.SetTag, (1, [E(1), E(1), E(2), E(2), E(3)]));
        k.Dress("Equinox", Top, folder: 3, material: "b");
        var reads = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
        var inp = new Inputs
        {
            ModsRoot = k.ModsRoot, ManagedRoot = k.ManagedRoot,
            ReadGameFile = p => k.Game.TryGetValue(p, out var b) ? b : null,
            ReadContainerRedirects = root =>
            {
                reads[root] = reads.GetValueOrDefault(root) + 1;
                return PenumbraModMeta.ReadContainerRedirects(root);
            },
        };
        Assert.Equal([2, 3], Folders(k.Plan([k.Worn(Top, 2, mod)], inputs: inp)));
        Assert.All(reads.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    public void Race_fallback_model_path_is_the_one_compared()
    {
        // The c0201 character wears X's c0101 model (EQDP fallback): a rival replacing c0101 competes, one replacing c0201 does not.
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("moonfire lace", new() { [ModelPath(Top, "0101")] = "top.mdl", [MaterialPath(Top, 1, "b")] = "b.mtrl" });
        k.Mod("Rival", new() { [ModelPath(Top, "0101")] = "top.mdl", [MaterialPath(Top, 2, "b")] = "b.mtrl" });
        k.Imc(Top, E(1), E(1), E(2));
        var worn = new WornItem(Top, 2, ModelPath(Top, "0101"), Path.Combine(mod, "top.mdl"));
        Assert.Equal([2], Folders(k.Plan([worn])));
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
            Assert.Equal(RepointReason.NoImc, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
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
    [Trait("Plan", "EDGE-imc-all-variants-12")]
    public void Failed_resolve_is_meta_unreadable()
    {
        // The watcher hands Build a null collection when ResolvePlayerPaths fails: the same reason.
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        var inp = k.Inputs();
        var (candidates, left) = Candidates([k.Worn(Top, 2, mod)], inp);
        var plan = Build(candidates, null, new Dictionary<string, string?>(), k.RefreshPending, inp, left);
        Assert.Equal(RepointReason.MetaUnreadable, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-13")]
    public void Variant_zero_folder_is_a_target_none_beyond_count()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        // Default set 4 (wrong), variants 1..3; worn variant 9 is beyond the count, so the game uses the default set.
        k.Imc(Top, E(4), E(1), E(2), E(2));
        var write = Only(k.Plan([k.Worn(Top, 9, mod)]));
        Assert.Equal([2, 4], write.Folders);
        Assert.Equal([0, 2, 3], write.Variants);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-54")]
    public void Variants_sharing_a_folder_get_one_set_of_copies()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(2), E(1), E(2), E(2));
        var write = Only(k.Plan([k.Worn(Top, 2, mod)]));
        Assert.Single(write.Copies);
        Assert.Equal([0, 2, 3], write.Variants);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-15")]
    public void Outranked_copy_reported_not_rewritten()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        k.Served[MaterialPath(Top, 2)] = Path.Combine(k.ManagedRoot, "files", "x.mtrl");   // something above our copy
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(RepointReason.Outranked, LeftReason(plan));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-15")]
    public void Inactive_option_competitor_outranking_reads_outranked()
    {
        // R4: Y's model sits only in an inactive option, so Y counts as competing; Y ranks above X and beats our copy.
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("moonfire lace", Top, material: "b");
        var rival = k.Mod("Rival", new() { [MaterialPath(Top, 2, "b")] = "b.mtrl" },
                          groups: [("Body", "Single", [("Off", new() { [ModelPath(Top)] = "top.mdl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        k.Priority.Remove(rival);
        k.Priority.Insert(0, rival);
        Assert.Equal(RepointReason.Outranked, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-37")]
    public void No_outranked_verdict_while_refresh_pending()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        k.Served[MaterialPath(Top, 2)] = MaterialPath(Top, 2);   // a stale answer, before the reload lands
        k.RefreshPending.Add(ShaderUpgradePlan.NormalPath(mod));
        var pending = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(pending.PerMod);
        Assert.Empty(pending.Left);
        k.RefreshPending.Clear();
        Assert.Equal(RepointReason.Outranked, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-21")]
    public void Switched_off_mid_run_still_reloads_written_mods()
    {
        // Switching off only stops further items (mayWrite); every mod written or reconciled is still reloaded.
        Assert.Equal(["A", "B", "C"], ReloadSet(["A", "B"], ["b", "C"], disposed: false).Order());
        Assert.Empty(ReloadSet(["A"], ["B"], disposed: true));
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
        Assert.Equal(2, Assert.Single(k.Plan([k.Worn(Top, 2, mod), k.Worn(legs, 2, mod)]).PerMod).Items.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-29")]
    public void Glasses_and_weapons_ignored()
    {
        using var k = new VariantRepointTestKit();
        var head = new Item('e', 6255, "met");
        var mod = k.Dress("Glasses", head);
        k.ImcSlots(head.SetTag, (0, [E(1), E(1), E(2)]));
        Assert.Empty(k.Plan([k.Worn(head, 2, mod) with { Glasses = true }]).PerMod);
        Assert.Null(ItemFor("w", 201));
        Assert.Null(ItemFor("hir", 1));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-32")]
    public void Combining_container_material_counts_and_gets_copies()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Combo", new() { [ModelPath(Top)] = "top.mdl" },
                        groups: [("Looks", "Combining", [("Shiny", new() { [MaterialPath(Top, 1)] = "a.mtrl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        var copy = Assert.Single(Only(k.Plan([k.Worn(Top, 2, mod)])).Copies).Copy;
        Assert.Equal(new PenumbraModMeta.ContainerRef("Looks", null, 0), copy.Container);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-45")]
    public void Copies_mirror_every_container_holding_the_source()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "base.mtrl" },
                        groups: [("Colour", "Single", [("Red", new() { [MaterialPath(Top, 1)] = "red.mtrl" }, [])]),
                                 ("Extras", "Multi", [("Glow", new() { [MaterialPath(Top, 1)] = "glow.mtrl" }, [])]),
                                 ("Looks", "Combining", [("Shiny", new() { [MaterialPath(Top, 1)] = "shiny.mtrl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        var copies = Only(k.Plan([k.Worn(Top, 2, mod)])).Copies.Select(c => c.Copy).ToList();
        Assert.Equal(["base.mtrl", "glow.mtrl", "red.mtrl", "shiny.mtrl"], copies.Select(c => c.File).Order());
        Assert.All(copies, c => Assert.Equal(MaterialPath(Top, 2), c.GamePath));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-40")]
    public void Material_served_by_own_mod_is_dressed()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Served[MaterialPath(Top, 2)] = Path.Combine(mod, "top_a.mtrl");   // a file swap of its own
        var nothing = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(nothing.PerMod);
        Assert.Empty(nothing.Left);
        Assert.Equal([3], Folders(k.Plan([k.Worn(Top, 3, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-42")]
    public void Own_folder_not_served_by_mod_is_other_mod_material()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Served[MaterialPath(Top, 1)] = Path.Combine(k.ManagedRoot, "files", "x.mtrl");   // something beats X's own v0001
        Assert.Equal(RepointReason.OtherModMaterial, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-43")]
    public void Own_mod_recognised_through_path_spelling()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Moonfire Lace", Top, material: "b");
        k.Imc(Top, E(1), E(1), E(2));
        var spelled = "|abc|" + Path.Combine(mod, "top_b.mtrl").Replace('\\', '/').ToUpperInvariant();
        k.Served[MaterialPath(Top, 1, "b")] = spelled;
        k.Served[MaterialPath(Top, 2, "b")] = spelled;
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        Assert.Empty(plan.PerMod);   // v0002 reads as X's own: Dressed
        Assert.Empty(plan.Left);     // and v0001 is X's (N2)
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-47")]
    public void Model_won_by_competitor_leaves_our_copies_inert()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("moonfire lace", Top, material: "b");
        var rose = k.Dress("Rose", Top, folder: 2, material: "b");
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var record = File.ReadAllText(Path.Combine(mod, "Proteus", VariantRepointWriter.RecordFile));
        // Rose is raised above moonfire lace: Rose's model and Rose's v0002 win; moonfire lace is not worn at all.
        var plan = k.Plan([k.Worn(Top, 2, rose)]);
        Assert.Empty(plan.PerMod);
        Assert.Equal(record, File.ReadAllText(Path.Combine(mod, "Proteus", VariantRepointWriter.RecordFile)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-55")]
    public void Only_attributes_groups_do_not_block_copies()
    {
        foreach (var proteusToggles in new[] { false, true })
        {
            using var k = new VariantRepointTestKit();
            var name = proteusToggles ? MeshToggleService.GroupNameFor("Body") : "Belt";
            var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                            rawGroups: [ImcGroup(name, Top)]);
            if (proteusToggles) ToggleRecord(mod, Top, name);
            k.Imc(Top, E(1), E(1), E(2));
            var group = Manifest(mod)["Groups"]![0]!.ToJsonString();
            Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
            Assert.Equal(group, Manifest(mod)["Groups"]![0]!.ToJsonString());
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-56")]
    public void Mods_own_imc_material_on_worn_variant_is_mod_imc()
    {
        using var k = new VariantRepointTestKit();
        k.Imc(Top, E(1), E(1), E(2), E(3));
        var a = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" }, [Imc(Top, 2, E(3))]);
        var b = k.Mod("B", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                      groups: [("Extras", "Multi", [("Alt", new(), [Imc(Top, 2, E(3))])])]);
        foreach (var mod in new[] { a, b })
        {
            var plan = k.Plan([k.Worn(Top, 2, mod)]);
            Assert.Empty(plan.PerMod);
            Assert.Equal(RepointReason.ModImc, LeftReason(plan));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-56")]
    public void Mods_own_imc_material_on_other_variant_skips_that_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        groups: [("Extras", "Multi", [("Alt", new(), [Imc(Top, 3, E(5))])])]);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-56")]
    public void Worn_folder_set_by_another_variants_claim_is_mod_imc()
    {
        // W2: variant 3 is sent to folder 2 by the author, and the worn variant 2 uses folder 2 too.
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        groups: [("Extras", "Multi", [("Alt", new(), [Imc(Top, 3, E(2))])])]);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        Assert.Equal(RepointReason.ModImc, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-56")]
    public void Two_claims_on_one_variant_both_keep_their_folders()
    {
        // Default data sends variant 3 to folder 5, an option sends it to folder 6: neither folder is a target.
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        [Imc(Top, 3, E(5))],
                        groups: [("Extras", "Multi", [("Alt", new(), [Imc(Top, 3, E(6))])])]);
        k.ImcSlots(Top.SetTag, (1, [E(1), E(1), E(2), E(3), E(5), E(6)]));
        Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-62")]
    public void Claim_on_own_folder_does_not_block()
    {
        // W1: an unticked option that sends the worn variant to the mod's own folder agrees with the fix.
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        groups: [("Fix", "Multi", [("All on v1", new(), [Imc(Top, 2, E(1))])])]);
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-57")]
    public void Folder_no_variant_uses_still_gets_copies()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top, folder: 7);
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal([1, 2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-58")]
    public void Non_only_attributes_group_material_is_a_claim()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        rawGroups: [ImcGroup("Dye", Top, onlyAttributes: false, material: 4)]);
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal(RepointReason.ModImc, LeftReason(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-58")]
    public void Pinned_non_only_attributes_group_skips_its_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        rawGroups: [ImcGroup("Dye", Top, allVariants: false, variant: 3, onlyAttributes: false, material: 3)]);
        k.Imc(Top, E(1), E(1), E(2), E(4));
        Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-59")]
    public void Attribute_only_mod_imc_entry_is_not_a_claim()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("A", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "a.mtrl" },
                        groups: [("Toggle", "Single", [("Off", new(), [Imc(Top, 2, E(2, attr: 0x3FE))])])]);
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Equal([2], Folders(k.Plan([k.Worn(Top, 2, mod)])));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-49")]
    public void Legacy_imc_entries_are_not_claims()
    {
        // A build-1428 IMC entry pointing variant 3 at folder 1 is Proteus's, not the author's: it never blocks a fix.
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        [Imc(Top, 3, E(5))]);
        LegacyRecord(mod, Top, 1, (3, E(5)));
        k.Imc(Top, E(1), E(1), E(2), E(3));
        var write = Only(k.Plan([k.Worn(Top, 2, mod)]));
        Assert.Contains(2, write.Folders);
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
    [Trait("Plan", "EDGE-imc-all-variants-53")]
    public void Penumbra_spelling_of_an_imc_entry_parses_the_same()
    {
        var proteus = System.Text.Json.JsonSerializer.SerializeToElement(Imc(Top, 2, E(1, 2, 3, 4, 5, 6)));
        var penumbra = System.Text.Json.JsonDocument.Parse(
            """{"Type":"Imc","Manipulation":{"Variant":"2","PrimaryId":6255,"EquipSlot":"Body","ObjectType":"Equipment","SecondaryId":0,"BodySlot":"Unknown","Entry":{"MaterialAnimationId":6,"VfxId":5,"SoundId":4,"AttributeMask":3,"DecalId":2,"MaterialId":1}}}""").RootElement;
        Assert.Equal(ParseImc(proteus), ParseImc(penumbra));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-45")]
    public void InFolder_moves_only_the_folder()
    {
        Assert.Equal("chara/equipment/e6255/material/v0012/mt_c0201e6255_top_a.mtrl",
                     InFolder("chara/equipment/e6255/material/v0001/mt_c0201e6255_top_a.mtrl", 12));
        Assert.Equal(@"chara\equipment\e6255\material\v0002\MT_c0201e6255_top_a.mtrl",
                     InFolder(@"chara\equipment\e6255\material\v0001\MT_c0201e6255_top_a.mtrl", 2));
    }
}
