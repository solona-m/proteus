using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Services.ModMoveMigration;

namespace Proteus.Tests;

/// <summary>What moves from one Penumbra mod folder to another when the mod is renamed: <see cref="ModMoveMigration"/>
/// over synthetic settings, presets and bindings.</summary>
public class ModMoveMigrationTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "mods");

    private static AutoRefitPreference Pref(Configuration c, string key = "11111111-1111-1111-1111-111111111111")
        => c.AutoRefitByCollection[key] = new AutoRefitPreference();

    [Fact]
    [Trait("Plan", "AC-mod-rename-3")]
    public void Configuration_carries_ao_vanilla_stack_and_classification()
    {
        var c = new Configuration();
        c.AmbientOcclusionOverrides["Old"] = false;
        c.AmbientOcclusionDisabledMods.Add("Old");
        c.SiblingSynthesis["Old"] = SiblingSynthesisMode.Off;
        c.OverlayModStackOrder["Old"] = ["a"];
        c.OverlayStackOrder[Configuration.StackKey("Old", "Tattoos")] = ["x", "y"];
        c.KnownBodyMods["Old"] = new BodyModCacheEntry { Fingerprint = 7 };
        bool aoBefore = c.AmbientOcclusionEnabledFor("Old", true);
        bool vanillaBefore = c.OverlaysVanillaFor("Old");

        int n = ModMoveMigration.Configuration(c, "Old", "New");

        Assert.Equal(6, n);
        Assert.Equal(aoBefore, c.AmbientOcclusionEnabledFor("New", true));
        Assert.Equal(vanillaBefore, c.OverlaysVanillaFor("New"));
        Assert.Contains("New", c.AmbientOcclusionDisabledMods);
        Assert.Equal(["a"], c.OverlayModStackOrder["New"]);
        Assert.Equal(["x", "y"], c.OverlayStackOrder[Configuration.StackKey("New", "Tattoos")]);
        Assert.Equal(7, c.KnownBodyMods["New"].Fingerprint);
        Assert.DoesNotContain("Old", c.AmbientOcclusionOverrides.Keys);
        Assert.DoesNotContain(c.OverlayStackOrder.Keys, k => k.StartsWith("Old"));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-3")]
    public void Configuration_keeps_dictionary_comparers()
    {
        var c = new Configuration();
        var overrides = c.AmbientOcclusionOverrides;
        var disabled = c.AmbientOcclusionDisabledMods;
        overrides["Old"] = true;
        disabled.Add("Old");

        ModMoveMigration.Configuration(c, "Old", "New");

        Assert.Same(overrides, c.AmbientOcclusionOverrides);
        Assert.Same(disabled, c.AmbientOcclusionDisabledMods);
        Assert.True(c.AmbientOcclusionOverrides.ContainsKey("NEW"));
        Assert.Contains("new", c.AmbientOcclusionDisabledMods);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-8")]
    public void Configuration_carries_auto_refit_garment_keys()
    {
        var c = new Configuration();
        var p = Pref(c);
        p.SwitchedOn.Add(AutoRefitDecisions.GroupKey("Old", "Size"));
        p.AuthorSwitchedOn[AutoRefitDecisions.GroupKey("Old", "Size")] = "M";
        p.Before[AutoRefitDecisions.GroupKey("Old", "Size")] = ["S"];
        p.HeldBefore[AutoRefitDecisions.GroupKey("Old", "Size")] = ["L"];
        p.InheritedBefore.Add("Old");
        p.Declined.Add(new AutoRefitDecisions.Worn("chara/equipment/e0001/model/c0201e0001_top.mdl", "Old", null).Key);

        ModMoveMigration.Configuration(c, "Old", "New");

        var key = AutoRefitDecisions.GroupKey("New", "Size");
        Assert.Contains(key, p.SwitchedOn);
        Assert.Equal("M", p.AuthorSwitchedOn[key]);
        Assert.Equal(["S"], p.Before[key]);
        Assert.Equal(["L"], p.HeldBefore[key]);
        Assert.Contains("New", p.InheritedBefore);
        Assert.Contains(new AutoRefitDecisions.Worn("chara/equipment/e0001/model/c0201e0001_top.mdl", "New", null).Key,
                        p.Declined);
        Assert.Single(p.Declined);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-20")]
    public void Configuration_leaves_body_targets()
    {
        var c = new Configuration();
        var p = Pref(c);
        p.ChestBody = "Old";
        c.RetargetBodyDir = "Old";

        ModMoveMigration.Configuration(c, "Old", "New");

        Assert.Equal("Old", p.ChestBody);
        Assert.Equal("Old", c.RetargetBodyDir);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-3")]
    public void Configuration_case_only_rename_rewrites_casing_in_ordinal_dictionaries()
    {
        // Ordinal dictionaries, as deserialisation hands these back.
        var c = new Configuration
        {
            SiblingSynthesis     = new() { ["Body"] = SiblingSynthesisMode.Off },
            KnownBodyMods        = new() { ["Body"] = new BodyModCacheEntry { Fingerprint = 3 } },
            OverlayModStackOrder = new() { ["Body"] = ["a"] },
            OverlayStackOrder    = new() { [Configuration.StackKey("Body", "g")] = ["b"] },
        };

        ModMoveMigration.Configuration(c, "Body", "BODY");

        Assert.Equal(["BODY"], c.SiblingSynthesis.Keys);
        Assert.Equal(["BODY"], c.KnownBodyMods.Keys);
        Assert.Equal(["BODY"], c.OverlayModStackOrder.Keys);
        Assert.Equal([Configuration.StackKey("BODY", "g")], c.OverlayStackOrder.Keys);
        Assert.Equal(MoveKind.Normal, Classify("Body", "BODY", null));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-4")]
    public void Configuration_moved_value_wins_over_leftover()
    {
        var c = new Configuration();
        c.AmbientOcclusionOverrides["Old"] = true;
        c.AmbientOcclusionOverrides["New"] = false;   // a deleted mod's leftover

        ModMoveMigration.Configuration(c, "Old", "New");

        Assert.True(c.AmbientOcclusionOverrides["New"]);
        Assert.Single(c.AmbientOcclusionOverrides);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-4")]
    public void RekeySet_unions()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Old", "New", "Other" };
        Assert.True(RekeySet(s, "Old", "New"));
        Assert.Equal(new[] { "New", "Other" }.OrderBy(x => x), s.OrderBy(x => x));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-5")]
    public void Prefix_lookalikes_are_untouched()
    {
        var c = new Configuration();
        c.OverlayStackOrder[Configuration.StackKey("Boney", "g")] = ["a"];
        var p = Pref(c);
        p.SwitchedOn.Add("Boney|g");
        p.Declined.Add("path|boney");

        Assert.Equal(0, ModMoveMigration.Configuration(c, "Bone", "X"));
        Assert.Contains(Configuration.StackKey("Boney", "g"), c.OverlayStackOrder.Keys);
        Assert.Contains("Boney|g", p.SwitchedOn);
        Assert.Contains("path|boney", p.Declined);

        var hat = HatCompatService.ModRootOf(Root, "Boney") + "|hair.mdl";
        Assert.Equal(hat, HatIdentity(hat, Root, "Bone", "X"));

        var file = Path.Combine(Root, "Boney", "tex.tex");
        Assert.Equal(file, RemapPathFolder(file, Root, "Bone", "X"));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-7")]
    public void Configuration_returns_zero_for_a_mod_with_no_state()
    {
        var c = new Configuration();
        c.AmbientOcclusionOverrides["Else"] = true;
        Assert.Equal(0, ModMoveMigration.Configuration(c, "Old", "New"));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-8")]
    public void Configuration_migrates_regardless_of_PluginEnabled()
    {
        var c = new Configuration { PluginEnabled = false };
        c.AmbientOcclusionOverrides["Old"] = true;
        Assert.Equal(1, ModMoveMigration.Configuration(c, "Old", "New"));
        Assert.True(c.AmbientOcclusionOverrides["New"]);
    }

    // ── presets ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mod-rename-4")]
    public void Presets_rekeys_saved_presets_and_pin()
    {
        var mine = new ModPreset { Id = Guid.NewGuid(), Name = "Mine" };
        var store = new ModPresetStore();
        store.Presets["Old"] = [mine];
        store.Applied["Old"] = mine.Id;

        Assert.True(ModMoveMigration.Presets(store, "Old", "New", packPresets: []));
        Assert.Same(mine, store.Presets["New"].Single());
        Assert.Equal(mine.Id, store.Applied["New"]);
        Assert.False(store.Presets.ContainsKey("Old"));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-4")]
    public void Presets_rederives_an_idless_pack_preset_pin()
    {
        var pack = new ModPreset { Name = "Author look" };
        var store = new ModPresetStore();
        store.Applied["Old"] = PresetService.PackId("Old", pack);

        ModMoveMigration.Presets(store, "Old", "New", [pack]);

        Assert.Equal(PresetService.PackId("New", pack), store.Applied["New"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-14")]
    public void Presets_leaves_pin_when_pack_presets_unavailable()
    {
        var pack = new ModPreset { Name = "Author look" };
        var store = new ModPresetStore();
        var pin = PresetService.PackId("Old", pack);
        store.Applied["Old"] = pin;

        ModMoveMigration.Presets(store, "Old", "New", packPresets: null);

        Assert.Equal(pin, store.Applied["New"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-15")]
    public void Presets_keeps_author_id_pin()
    {
        var pack = new ModPreset { Id = Guid.NewGuid(), Name = "Author look" };
        var store = new ModPresetStore();
        store.Applied["Old"] = pack.Id;

        ModMoveMigration.Presets(store, "Old", "New", [pack]);

        Assert.Equal(pack.Id, store.Applied["New"]);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-4")]
    public void OverrideBag_RenameMod_moves_and_republishes()
    {
        IReadOnlyDictionary<string, OverlayColorOverride>? colors = null;
        IReadOnlyDictionary<string, OverlayGearOverride>? gear = null;
        IReadOnlyDictionary<string, List<string>>? stack = null;
        var bag = new OverlayOverrideBag(c => colors = c, g => gear = g, s => stack = s);
        var look = new OverlayColorOverride();
        bag.SetMod("Old", look, new OverlayGearOverride(), ["a"]);

        Assert.True(bag.RenameMod("Old", "New"));

        Assert.Same(look, colors!["New"]);
        Assert.False(colors.ContainsKey("Old"));
        Assert.True(gear!.ContainsKey("New") && !gear.ContainsKey("Old"));
        Assert.Equal(["a"], stack!["New"]);
        Assert.False(bag.RenameMod("Nope", "Other"));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-24")]
    public void Preset_and_binding_bags_both_move_and_MergeByMod_keeps_preset_on_top()
    {
        IReadOnlyDictionary<string, OverlayColorOverride>? presetColors = null, bindingColors = null;
        var preset = new OverlayOverrideBag(c => presetColors = c, _ => { }, _ => { });
        var binding = new OverlayOverrideBag(c => bindingColors = c, _ => { }, _ => { });
        var fromPreset = new OverlayColorOverride();
        preset.SetMod("Old", fromPreset, new(), null);
        binding.SetMod("Old", new OverlayColorOverride(), new(), null);

        preset.RenameMod("Old", "New");
        binding.RenameMod("Old", "New");

        var merged = CompositorService.MergeByMod(presetColors, bindingColors);
        Assert.Same(fromPreset, merged!["New"]);
        Assert.False(merged.ContainsKey("Old"));
    }

    // ── design bindings ──────────────────────────────────────────────────────

    private static DesignBindingStore Store(params (string Proteus, string Character)[] bindings)
    {
        var store = new DesignBindingStore();
        foreach (var (p, ch) in bindings)
            store.Bindings[Guid.NewGuid()] = new DesignBinding
            {
                Mods = [new ProteusModBinding { ModDirectory = p }],
                CharacterMods = [new PenumbraModSetting { ModDirectory = ch }, new PenumbraModSetting { ModDirectory = "Else" }],
            };
        return store;
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-5")]
    public void Bindings_rewrites_proteus_and_character_snapshots()
    {
        var store = Store(("Old", "Old"), ("Old", "Old"));

        Assert.Equal(4, ModMoveMigration.Bindings(store, "Old", "New"));

        foreach (var b in store.Bindings.Values)
        {
            Assert.Equal("New", b.Mods.Single().ModDirectory);
            Assert.Equal(["New", "Else"], b.CharacterMods.Select(m => m.ModDirectory));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-4")]
    public void Bindings_drops_stale_new_folder_entry_in_same_list()
    {
        var store = Store(("Old", "Old"));
        var b = store.Bindings.Values.Single();
        b.CharacterMods.Add(new PenumbraModSetting { ModDirectory = "New", Enabled = true });

        ModMoveMigration.Bindings(store, "Old", "New");

        Assert.Single(b.CharacterMods, m => m.ModDirectory == "New");
        Assert.False(b.CharacterMods.Single(m => m.ModDirectory == "New").Enabled);
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-5")]
    [Trait("Plan", "EDGE-mod-rename-22")]
    public void MoveHeld_moves_record_and_its_ModDirectory()
    {
        var held = new Dictionary<string, PenumbraModSetting>(StringComparer.OrdinalIgnoreCase)
        {
            ["Old"] = new PenumbraModSetting { ModDirectory = "Old", Enabled = true },
        };

        Assert.True(MoveHeld(held, "Old", "New"));

        Assert.Equal("New", held["New"].ModDirectory);
        Assert.False(held.ContainsKey("Old"));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-22")]
    public void RekeySet_moves_guard_mod()
    {
        var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Old" };
        Assert.True(RekeySet(guard, "Old", "New"));
        Assert.Equal(["New"], guard);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-22")]
    public void RekeyKey_moves_guard_clear_count()
    {
        var clears = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Old"] = 2 };
        Assert.True(RekeyKey(clears, "Old", "New"));
        Assert.Equal(2, clears["New"]);
        Assert.Single(clears);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-16")]
    public void Chain_A_to_B_to_C_ends_on_C()
    {
        var c = new Configuration();
        c.AmbientOcclusionOverrides["A"] = true;
        var presets = new ModPresetStore();
        presets.Presets["A"] = [new ModPreset { Id = Guid.NewGuid() }];
        var bindings = Store(("A", "A"));

        foreach (var (o, n) in new[] { ("A", "B"), ("B", "C") })
        {
            ModMoveMigration.Configuration(c, o, n);
            ModMoveMigration.Presets(presets, o, n, []);
            ModMoveMigration.Bindings(bindings, o, n);
        }

        Assert.Equal(["C"], c.AmbientOcclusionOverrides.Keys);
        Assert.Equal(["C"], presets.Presets.Keys);
        Assert.Equal("C", bindings.Bindings.Values.Single().Mods.Single().ModDirectory);
    }

    // ── hats, auto refit, selection ──────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-mod-rename-8")]
    public void HatIdentity_rewrites_the_mod_root()
    {
        // Built exactly as an examination builds one: InMods on a file under the folder.
        HatCompatService.InMods(Path.Combine(Root, "Old", "chara", "hair.mdl"), Root, out var oldRoot, out var rel);
        HatCompatService.InMods(Path.Combine(Root, "New", "chara", "hair.mdl"), Root, out var newRoot, out _);

        Assert.Equal($"{newRoot}|{rel}", HatIdentity($"{oldRoot}|{rel}", Root, "Old", "New"));
        Assert.Null(HatIdentity(null, Root, "Old", "New"));
    }

    private static Dictionary<string, AutoRefitDecisions.Worn> Worn(string dir) => new()
    {
        ["_top"] = new("chara/equipment/e0001/model/c0201e0001_top.mdl", dir, "refit/top.mdl"),
        ["_dwn"] = new("chara/equipment/e0002/model/c0201e0002_dwn.mdl", "Else", "legs.mdl"),
    };

    [Fact]
    [Trait("Plan", "AC-mod-rename-8")]
    public void Renamed_worn_refitted_garment_reports_no_changed_slot()
    {
        var before = Worn("Old");
        var nowAfterRename = Worn("New");

        Assert.Empty(AutoRefitDecisions.Changed(MoveWorn(before, "Old", "New"), nowAfterRename));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-8")]
    public void Without_MoveWorn_the_renamed_garment_would_be_refitted()
    {
        Assert.Equal(["_top"], AutoRefitDecisions.Changed(Worn("Old"), Worn("New")));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-33")]
    public void Queued_requests_for_old_folder_are_dropped()
    {
        Assert.True(AutoRefitWatcher.RequestTargets("old", "Old"));
        Assert.False(AutoRefitWatcher.RequestTargets("Oldish", "Old"));
        Assert.False(AutoRefitWatcher.RequestTargets(null, "Old"));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-7")]
    [Trait("Plan", "EDGE-mod-rename-21")]
    public void Follow_repoints_selection_on_the_moved_mod_only()
    {
        Assert.Equal("B", Follow("A", "A", "B"));
        Assert.Equal("B", Follow("a", "A", "B"));
        Assert.Equal("C", Follow("C", "A", "B"));
        Assert.Null(Follow(null, "A", "B"));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-20")]
    public void RemapPathFolder_moves_base_upstream_paths()
    {
        var file = Path.Combine(Root, "Body", "chara", "skin.tex");
        Assert.Equal(Path.Combine(Root, "Body 2", "chara", "skin.tex"), RemapPathFolder(file, Root, "Body", "Body 2"));
    }

    // ── classification ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-2")]
    public void Classify_ignores_empty_and_identical()
    {
        Assert.Equal(MoveKind.Ignore, Classify("", "New", null));
        Assert.Equal(MoveKind.Ignore, Classify("Old", null, null));
        Assert.Equal(MoveKind.Ignore, Classify("Same", "Same", null));
    }

    [Fact]
    [Trait("Plan", "AC-mod-rename-9")]
    [Trait("Plan", "EDGE-mod-rename-18")]
    public void Classify_managed_renamed()
    {
        Assert.Equal(MoveKind.ManagedRenamed, Classify("Proteus", "Proteus backup", null));
        Assert.Equal(MoveKind.ManagedRenamed, Classify("proteus", "X", null));
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-19")]
    public void Classify_onto_managed()
        => Assert.Equal(MoveKind.OntoManaged, Classify("Mine", "Proteus", null));

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-27")]
    public void Classify_gone_when_new_folder_is_not_installed()
    {
        var installed = new Dictionary<string, string> { ["Other"] = "Other" };
        Assert.Equal(MoveKind.Gone, Classify("Old", "New", installed));
        installed["new"] = "New name";
        Assert.Equal(MoveKind.Normal, Classify("Old", "New", installed));
    }

    [Fact]
    [Trait("Plan", "AC-womens-overlays-men-5")]
    [Trait("Plan", "EDGE-womens-overlays-men-19")]
    public void Men_opt_out_follows_a_moved_mod() // [AC-womens-overlays-men-5] [EDGE-womens-overlays-men-19]
    {
        var c = new Configuration();
        c.MenOverlayOffMods.Add("Old");
        var set = c.MenOverlayOffMods;
        Assert.Equal(1, ModMoveMigration.Configuration(c, "Old", "New"));
        Assert.False(c.OverlaysMenFor("New"));
        Assert.True(c.OverlaysMenFor("Old"));
        Assert.Same(set, c.MenOverlayOffMods);
        Assert.False(c.OverlaysMenFor("NEW"));   // the comparer survives
    }
}
