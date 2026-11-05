using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;
using static Proteus.Services.VariantRepointPlan;
using static Proteus.Tests.VariantRepointTestKit;

namespace Proteus.Tests;

/// <summary>
/// "Gear variants" writes into a mod, end to end on disk (<see cref="VariantRepointWriter"/>): plan, write, walk again,
/// undo, and every way the mod can change under it. SENSITIVE writer: these stay in the quick suite.
/// </summary>
public class VariantRepointWriterTests
{
    private static string RecordPath(string mod) => Path.Combine(mod, "Proteus", VariantRepointWriter.RecordFile);

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-1")]
    public void Wrong_worn_variant_points_every_wrong_variant_at_the_mods_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(2), E(1), E(2), E(3), E(1));
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        var written = Assert.Single(Assert.Single(outcomes).Written);
        Assert.Equal([0, 2, 3], written.Variants);
        var entries = DefaultImc(mod, Top);
        Assert.Equal([0, 2, 3], entries.Keys.Order());
        Assert.All(entries.Values, e => Assert.Equal(1, e.MaterialId));
        var m = Manifest(mod)["DefaultData"]!["Manipulations"]!.AsArray()[0]!["Manipulation"]!;
        Assert.Equal("Equipment", m["ObjectType"]!.GetValue<string>());
        Assert.Equal("Body", m["EquipSlot"]!.GetValue<string>());
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-1")]
    public void Accessory_item_uses_accessory_identity()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Earrings", Earring);
        k.Imc(Earring, E(1), E(1), E(2));
        k.Run([k.Worn(Earring, 2, mod)]);
        var m = Manifest(mod)["DefaultData"]!["Manipulations"]!.AsArray().Single()!["Manipulation"]!;
        Assert.Equal("Accessory", m["ObjectType"]!.GetValue<string>());
        Assert.Equal("Ears", m["EquipSlot"]!.GetValue<string>());
        Assert.Equal(53, m["PrimaryId"]!.GetValue<int>());
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-33")]
    public void Custom_suffix_material_without_vanilla_file_is_written()
    {
        using var k = new VariantRepointTestKit();
        // This Old Thing: only v0001/mt_c0201e6255_top_b.mtrl, which the game has under no other folder. The resolver
        // echoes v0002's path, as Penumbra does for a file no mod serves, whether or not the game has it.
        var mod = k.Dress("This Old Thing", Top, material: "b");
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.Equal(1, DefaultImc(mod, Top)[2].MaterialId);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-5")]
    [Trait("Plan", "EDGE-imc-all-variants-35")]
    public void Second_walk_writes_nothing()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        var manifest = File.ReadAllText(Path.Combine(mod, "meta.json"));
        var record = File.ReadAllText(RecordPath(mod));
        foreach (var worn in new[] { 2, 3, 1 })
        {
            var (outcomes, left) = k.Run([k.Worn(Top, worn, mod)]);
            Assert.Empty(outcomes);
            Assert.Empty(left);   // our own entries never read as the mod's ("mod-imc")
        }
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(mod, "meta.json")));
        Assert.Equal(record, File.ReadAllText(RecordPath(mod)));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-9")]
    public void Only_default_manipulations_change()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        groups: [("Colour", "Single", [("Red", new() { ["chara/x.tex"] = "red.tex" }, []),
                                                       ("Blue", new() { ["chara/x.tex"] = "blue.tex" }, [])])]);
        var before = Manifest(mod);
        before["Custom"] = new JsonObject { ["Kept"] = true };
        WriteManifest(mod, before);
        File.WriteAllText(Path.Combine(mod, "top_a.mtrl"), "x");
        var hashes = ShaderUpgradeTestKit.Hashes(mod);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);

        var after = Manifest(mod);
        Assert.Equal(before.Select(p => p.Key), after.Select(p => p.Key));
        foreach (var (key, value) in before)
            if (key != "DefaultData") Assert.True(JsonNode.DeepEquals(value, after[key]), key);
        Assert.True(JsonNode.DeepEquals(before["DefaultData"]!["Files"], after["DefaultData"]!["Files"]));
        Assert.True(JsonNode.DeepEquals(before["DefaultData"]!["FileSwaps"], after["DefaultData"]!["FileSwaps"]));
        Assert.Single(after["DefaultData"]!["Manipulations"]!.AsArray());

        var now = ShaderUpgradeTestKit.Hashes(mod);
        foreach (var (file, hash) in hashes) Assert.Equal(hash, now[file]);
        Assert.Equal(["Proteus/" + VariantRepointWriter.RecordFile], now.Keys.Except(hashes.Keys));
        var record = VariantRepointWriter.ReadRecord(mod)!;
        var item = Assert.Single(record.Items);
        Assert.Equal(("Equipment", 6255, "Body", 1), (item.ObjectType, item.SetId, item.Slot, item.MaterialId));
        Assert.Equal([2], item.Variants.Select(v => v.Variant));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-8")]
    public void Outcome_names_item_and_variants()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        var (outcomes, _) = k.Run([k.Worn(Top, 3, mod)]);
        var (item, variants) = Assert.Single(Assert.Single(outcomes).Written);
        Assert.Equal("e6255 Body", item.Label);
        Assert.Equal([2, 3], variants);
        Assert.Equal(2, outcomes[0].Entries);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-6")]
    public void Undo_removes_ours_keeps_changed_and_declines()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        // Someone edits variant 3's entry by hand.
        EditDefaultManips(mod, a =>
        {
            var v3 = a.First(m => m!["Manipulation"]!["Variant"]!.GetValue<int>() == 3)!;
            v3["Manipulation"]!["Entry"]!["VfxId"] = 9;
        });
        Assert.Equal(VariantRepointWriter.Undone.Removed,
                     VariantRepointWriter.UndoItem(mod, Top, true, out var removed, out var kept, out _));
        Assert.Equal((1, 1), (removed, kept));
        Assert.Equal([3], DefaultImc(mod, Top).Keys);
        var record = VariantRepointWriter.ReadRecord(mod)!;
        Assert.Contains(Top.Key, record.Declined);
        Assert.Equal(RepointReason.Declined, Assert.Single(k.Plan([k.Worn(Top, 2, mod)]).Left).Reason);

        VariantRepointWriter.ClearDeclines(mod, [Top]);
        Assert.Single(Assert.Single(k.Plan([k.Worn(Top, 2, mod)]).PerMod).Items);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-26")]
    public void Undo_without_decline()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        VariantRepointWriter.UndoItem(mod, Top, false, out var removed, out _, out _);
        Assert.Equal(1, removed);
        Assert.Empty(DefaultImc(mod, Top));
        Assert.False(File.Exists(RecordPath(mod)));
        Assert.False(Directory.Exists(Path.Combine(mod, "Proteus")));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-26")]
    public void Undo_without_record_says_nothing_recorded()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        var manifest = File.ReadAllText(Path.Combine(mod, "meta.json"));
        Assert.Equal(VariantRepointWriter.Undone.NothingRecorded, VariantRepointWriter.UndoItem(mod, Top, true, out _, out _, out _));
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(mod, "meta.json")));
        Assert.False(File.Exists(RecordPath(mod)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-26")]
    public void Undo_unreadable_manifest_fails_and_keeps_record()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var record = File.ReadAllText(RecordPath(mod));
        File.WriteAllText(Path.Combine(mod, "meta.json"), "{ not json");
        Assert.Equal(VariantRepointWriter.Undone.Failed, VariantRepointWriter.UndoItem(mod, Top, true, out _, out _, out var message));
        Assert.NotEmpty(message);
        Assert.Equal(record, File.ReadAllText(RecordPath(mod)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-6")]
    public void Legacy_folder_never_written()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.LegacyMod("Old", Top);
        var meta = File.ReadAllBytes(Path.Combine(mod, "meta.json"));
        var dm = File.ReadAllBytes(Path.Combine(mod, "default_mod.json"));
        k.Imc(Top, E(1), E(1), E(2));
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Equal(RepointReason.LegacyMod, Assert.Single(left).Reason);
        Assert.Equal(RepointReason.LegacyMod,
                     Assert.Single(VariantRepointWriter.Apply(mod, [new ItemWrite(Top, 1, [(2, E(1))])], () => true).Left).Reason);
        Assert.Equal(meta, File.ReadAllBytes(Path.Combine(mod, "meta.json")));
        Assert.Equal(dm, File.ReadAllBytes(Path.Combine(mod, "default_mod.json")));
        Assert.False(Directory.Exists(Path.Combine(mod, "Proteus")));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-14")]
    public void Penumbra_respelled_entries_still_ours()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1, attr: 5, sound: 2), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        // Penumbra's own save: no AttributeAndSound, keys reordered, the variant as a string.
        EditDefaultManips(mod, a =>
        {
            var m = a[0]!["Manipulation"]!.AsObject();
            var e = m["Entry"]!.AsObject();
            a[0] = new JsonObject
            {
                ["Type"] = "Imc",
                ["Manipulation"] = new JsonObject
                {
                    ["Variant"] = m["Variant"]!.GetValue<int>().ToString(), ["PrimaryId"] = 6255, ["EquipSlot"] = "Body",
                    ["ObjectType"] = "Equipment", ["SecondaryId"] = 0, ["BodySlot"] = "Unknown",
                    ["Entry"] = new JsonObject
                    {
                        ["MaterialAnimationId"] = e["MaterialAnimationId"]!.GetValue<int>(), ["VfxId"] = e["VfxId"]!.GetValue<int>(),
                        ["SoundId"] = e["SoundId"]!.GetValue<int>(), ["AttributeMask"] = e["AttributeMask"]!.GetValue<int>(),
                        ["DecalId"] = e["DecalId"]!.GetValue<int>(), ["MaterialId"] = e["MaterialId"]!.GetValue<int>(),
                    },
                },
            };
        });
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Empty(left);
        Assert.Single(VariantRepointWriter.ReadRecord(mod)!.Items);
        VariantRepointWriter.UndoItem(mod, Top, false, out var removed, out var kept, out _);
        Assert.Equal((1, 0), (removed, kept));
        Assert.Empty(DefaultImc(mod, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-16")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Removed_entries_decline_the_item()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        // Partial removal: variant 3 deleted by someone.
        EditDefaultManips(mod, a => a.Remove(a.First(m => m!["Manipulation"]!["Variant"]!.GetValue<int>() == 3)));
        var (outcomes, left) = k.Run([k.Worn(Top, 3, mod)]);
        Assert.Empty(outcomes);
        Assert.Equal(RepointReason.Declined, Assert.Single(left).Reason);
        Assert.Equal([2], DefaultImc(mod, Top).Keys);

        // Full removal, the mod otherwise unchanged: declined too, never re-added.
        using var k2 = new VariantRepointTestKit();
        var mod2 = k2.Dress("Dress", Top);
        k2.Imc(Top, E(1), E(1), E(2));
        k2.Run([k2.Worn(Top, 2, mod2)]);
        EditDefaultManips(mod2, a => a.Clear());
        var (o2, l2) = k2.Run([k2.Worn(Top, 2, mod2)]);
        Assert.Empty(o2);
        Assert.Equal(RepointReason.Declined, Assert.Single(l2).Reason);
        Assert.Empty(DefaultImc(mod2, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-16")]
    public void Repoint_now_lifts_the_decline()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        EditDefaultManips(mod, a => a.Clear());
        k.Run([k.Worn(Top, 2, mod)]);
        VariantRepointWriter.ClearDeclines(mod, [Top]);
        var (outcomes, _) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Single(outcomes);
        Assert.Equal([2], DefaultImc(mod, Top).Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-17")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Replaced_entry_forgotten_and_kept()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        EditDefaultManips(mod, a => a[0]!["Manipulation"]!["Entry"]!["DecalId"] = 7);
        var theirs = DefaultImc(mod, Top)[2];
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Empty(left);
        Assert.Equal(theirs, DefaultImc(mod, Top)[2]);
        Assert.Equal(VariantRepointWriter.Undone.NothingRecorded, VariantRepointWriter.UndoItem(mod, Top, true, out _, out _, out _));
        Assert.Equal(theirs, DefaultImc(mod, Top)[2]);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-18")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Author_update_new_folder_replans()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        // The author now ships v0003 instead of v0001.
        var m = Manifest(mod);
        var files = m["DefaultData"]!["Files"]!.AsObject();
        files.Remove(MaterialPath(Top, 1));
        files[MaterialPath(Top, 3)] = "top_a.mtrl";
        WriteManifest(mod, m);
        k.Run([k.Worn(Top, 2, mod)]);
        var entries = DefaultImc(mod, Top);
        Assert.Equal([0, 1, 2], entries.Keys.Order());
        Assert.All(entries.Values, e => Assert.Equal(3, e.MaterialId));
        Assert.Equal(3, Assert.Single(VariantRepointWriter.ReadRecord(mod)!.Items).MaterialId);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-18")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Author_update_several_folders_removes_ours()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var m = Manifest(mod);
        m["DefaultData"]!["Files"]!.AsObject()[MaterialPath(Top, 2)] = "top_b.mtrl";
        WriteManifest(mod, m);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Equal(RepointReason.SeveralVariants, Assert.Single(left).Reason);
        Assert.Empty(DefaultImc(mod, Top));
        Assert.False(File.Exists(RecordPath(mod)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-34")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Author_update_fingerprint_change_replans_not_declines()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        // The author ships an update: a new version and files, and none of our entries.
        var m = Manifest(mod);
        m["Version"] = "2.0";
        m["DefaultData"]!["Files"]!.AsObject()["chara/new.tex"] = "new.tex";
        m["DefaultData"]!["Manipulations"] = new JsonArray();
        WriteManifest(mod, m);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        Assert.Single(outcomes);
        Assert.Equal([2], DefaultImc(mod, Top).Keys);
        Assert.Empty(VariantRepointWriter.ReadRecord(mod)!.Declined);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-34")]
    public void Author_update_keeps_existing_declines()
    {
        using var k = new VariantRepointTestKit();
        var legs = new Item('e', 6255, "dwn");
        var mod = k.Mod("Outfit", new()
        {
            [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl",
            [ModelPath(legs)] = "dwn.mdl", [MaterialPath(legs, 1)] = "dwn_a.mtrl",
        });
        k.ImcSlots(Top.SetTag, (1, [E(1), E(1), E(2)]), (3, [E(1), E(1), E(2)]));
        k.Run([k.Worn(Top, 2, mod), k.Worn(legs, 2, mod)]);
        VariantRepointWriter.UndoItem(mod, legs, true, out _, out _, out _);
        var m = Manifest(mod);
        m["Version"] = "2.0";
        m["DefaultData"]!["Manipulations"] = new JsonArray();
        WriteManifest(mod, m);
        var (_, left) = k.Run([k.Worn(Top, 2, mod), k.Worn(legs, 2, mod)]);
        Assert.Equal(RepointReason.Declined, Assert.Single(left).Reason);
        Assert.Equal([2], DefaultImc(mod, Top).Keys);
        Assert.Empty(DefaultImc(mod, legs));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-34")]
    public void Fingerprint_unchanged_by_our_write_and_penumbra_resave()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        var before = Fingerprint(mod);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.Equal(before, Fingerprint(mod));
        Assert.Equal(before, VariantRepointWriter.ReadRecord(mod)!.Fingerprint);
        // Penumbra re-saves: key order, spelling of slashes, a layout key.
        var m = Manifest(mod);
        var files = m["DefaultData"]!["Files"]!.AsObject();
        var pairs = files.Select(p => (p.Key, p.Value!.GetValue<string>())).Reverse().ToList();
        files.Clear();
        foreach (var (key, value) in pairs) files[key.ToUpperInvariant()] = value.Replace('/', '\\');
        m["Image"] = "cover.png";
        WriteManifest(mod, m);
        Assert.Equal(before, Fingerprint(mod));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-34")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Other_proteus_edit_then_removal_declines_not_replans()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        groups: [("Colour", "Single", [("Red", new() { ["chara/x.tex"] = "red.tex" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        // 1. our write
        k.Run([k.Worn(Top, 2, mod)]);
        // 2. a shader-upgrade-style edit: a mask redirect added inside an option
        PenumbraModMeta.MergeOptionRedirects(mod, "Colour", "Red",
            new System.Collections.Generic.Dictionary<string, string> { ["chara/x_mask.tex"] = "Shader Upgrade/m.tex" });
        Assert.NotEqual(VariantRepointWriter.ReadRecord(mod)!.Fingerprint, Fingerprint(mod));
        // 3. a walk finds our entries intact and re-stamps
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.Equal(Fingerprint(mod), VariantRepointWriter.ReadRecord(mod)!.Fingerprint);
        // 4. every entry of ours removed by the user: declined, not replanned
        EditDefaultManips(mod, a => a.Clear());
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Equal(RepointReason.Declined, Assert.Single(left).Reason);
        Assert.Empty(DefaultImc(mod, Top));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-19")]
    public void Pending_record_after_crash_is_resolved()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        VariantRepointWriter.AfterStep = step => { if (step == "pending") throw new CrashException(); };
        Assert.Throws<CrashException>(() => k.Run([k.Worn(Top, 2, mod)]));
        VariantRepointWriter.AfterStep = null;
        Assert.True(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Pending);
        Assert.Empty(DefaultImc(mod, Top));
        // The next run drops the absent entry without declining, and writes it properly.
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        Assert.Single(outcomes);
        var item = VariantRepointWriter.ReadRecord(mod)!.Items.Single();
        Assert.False(item.Pending);
        Assert.Equal([2], DefaultImc(mod, Top).Keys);

        // A crash after the manifest write: the present entry is adopted.
        using var k2 = new VariantRepointTestKit();
        var mod2 = k2.Dress("Dress", Top);
        k2.Imc(Top, E(1), E(1), E(2));
        VariantRepointWriter.AfterStep = step => { if (step == "manifest") throw new CrashException(); };
        Assert.Throws<CrashException>(() => k2.Run([k2.Worn(Top, 2, mod2)]));
        VariantRepointWriter.AfterStep = null;
        var (o2, l2) = k2.Run([k2.Worn(Top, 2, mod2)]);
        Assert.Empty(o2);
        Assert.Empty(l2);
        var adopted = VariantRepointWriter.ReadRecord(mod2)!.Items.Single();
        Assert.False(adopted.Pending);
        Assert.Equal([2], adopted.Variants.Select(v => v.Variant));
        Assert.Empty(VariantRepointWriter.ReadRecord(mod2)!.Declined);
    }

    private sealed class CrashException : Exception;

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-20")]
    public void Held_manifest_write_failed_rolls_back()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        var plan = k.Plan([k.Worn(Top, 2, mod)]);
        var manifest = File.ReadAllText(Path.Combine(mod, "meta.json"));
        VariantRepointWriter.Outcome outcome;
        using (new FileStream(Path.Combine(mod, "meta.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = VariantRepointWriter.Apply(mod, plan.PerMod.Single().Items, () => true);
        Assert.Equal(RepointReason.WriteFailed, Assert.Single(outcome.Left).Reason);
        Assert.Empty(outcome.Written);
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(mod, "meta.json")));
        Assert.False(File.Exists(RecordPath(mod)));
        // Retried later.
        Assert.Single(k.Run([k.Worn(Top, 2, mod)]).Outcomes);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-21")]
    public void MayWrite_false_stops_before_next_item()
    {
        using var k = new VariantRepointTestKit();
        var legs = new Item('e', 6255, "dwn");
        var mod = k.Mod("Outfit", new()
        {
            [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl",
            [ModelPath(legs)] = "dwn.mdl", [MaterialPath(legs, 1)] = "dwn_a.mtrl",
        });
        k.ImcSlots(Top.SetTag, (1, [E(1), E(1), E(2)]), (3, [E(1), E(1), E(2)]));
        int asked = 0;
        var (outcomes, _) = k.Run([k.Worn(Top, 2, mod), k.Worn(legs, 2, mod)], () => asked++ == 0);
        Assert.Single(Assert.Single(outcomes).Written);
        Assert.Single(VariantRepointWriter.ReadRecord(mod)!.Items);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-28")]
    public void Left_and_right_rings_are_separate_items()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Rings", new()
        {
            [ModelPath(RightRing)] = "rir.mdl", [MaterialPath(RightRing, 1)] = "rir_a.mtrl",
            [ModelPath(LeftRing)] = "ril.mdl", [MaterialPath(LeftRing, 1)] = "ril_a.mtrl",
        });
        // Right ring is slot 3 of the set, left ring slot 4: only the right ring's variant 2 misses.
        k.ImcSlots(RightRing.SetTag, (3, [E(1), E(1), E(2)]), (4, [E(1), E(1), E(1)]));
        k.Run([k.Worn(RightRing, 2, mod), k.Worn(LeftRing, 2, mod)]);
        Assert.Equal([2], DefaultImc(mod, RightRing).Keys);
        Assert.Empty(DefaultImc(mod, LeftRing));
        var m = Manifest(mod)["DefaultData"]!["Manipulations"]!.AsArray().Single()!["Manipulation"]!;
        Assert.Equal("RFinger", m["EquipSlot"]!.GetValue<string>());
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-30")]
    public void Record_moves_with_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var moved = Path.Combine(k.ModsRoot, "Dress (renamed)");
        Directory.Move(mod, moved);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, moved)]);
        Assert.Empty(outcomes);
        Assert.Empty(left);
        VariantRepointWriter.UndoItem(moved, Top, false, out var removed, out _, out _);
        Assert.Equal(1, removed);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-31")]
    public void Non_ascii_and_unknown_keys_preserved()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Кимоно ✿", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "ünï/top_a.mtrl" },
                        groups: [("色", "Single", [("赤", new() { ["chara/x.tex"] = "赤.tex" }, [])])]);
        var m = Manifest(mod);
        m["Description"] = "Ça va — ✓";
        m["SomethingNew"] = new JsonObject { ["Deep"] = new JsonArray(1, "二") };
        WriteManifest(mod, m);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var after = Manifest(mod);
        Assert.Equal("Ça va — ✓", after["Description"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(m["SomethingNew"], after["SomethingNew"]));
        Assert.True(JsonNode.DeepEquals(m["Groups"], after["Groups"]));
        Assert.Equal("ünï/top_a.mtrl", after["DefaultData"]!["Files"]![MaterialPath(Top, 1)]!.GetValue<string>());
        Assert.Contains("色", File.ReadAllText(Path.Combine(mod, "meta.json")));
    }
}
