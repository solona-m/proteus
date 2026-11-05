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
/// "Gear variants" writes into a mod, end to end on disk (<see cref="VariantRepointWriter"/>): plan, copy, walk again,
/// undo, and every way the mod can change under it. SENSITIVE writer: these stay in the quick suite.
/// </summary>
public class VariantRepointWriterTests
{
    private static string RecordPath(string mod) => Path.Combine(mod, "Proteus", VariantRepointWriter.RecordFile);

    private static readonly PenumbraModMeta.ContainerRef Red = new("Colour", "Red");
    private static readonly PenumbraModMeta.ContainerRef Blue = new("Colour", "Blue");

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-1")]
    public void Wrong_worn_variant_gets_redirect_copies_for_every_wrong_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(2), E(1), E(2), E(3), E(1));
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        var w = Assert.Single(Assert.Single(outcomes).Written);
        Assert.Equal([2, 3], w.Folders);
        Assert.Equal([0, 2, 3], w.Variants);
        foreach (var (opt, file) in new[] { (Red, "red/m.mtrl"), (Blue, "blue/m.mtrl") })
        {
            var files = FilesOf(mod, opt);
            Assert.Equal(file, files[MaterialPath(Top, 2)]);
            Assert.Equal(file, files[MaterialPath(Top, 3)]);
        }
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-1")]
    public void Accessory_item_copies_under_accessory_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Earrings", Earring);
        k.Imc(Earring, E(1), E(1), E(2));
        k.Run([k.Worn(Earring, 2, mod)]);
        Assert.Equal("ear_a.mtrl", FilesOf(mod, Default)["chara/accessory/a0053/material/v0002/mt_c0201a0053_ear_a.mtrl"]);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-1")]
    [Trait("Plan", "EDGE-imc-all-variants-41")]
    public void Competing_model_mods_material_gets_redirect_copies_moonfire_lace()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("moonfire lace", Top, material: "b");
        k.Dress("Rose", Top, folder: 2, material: "b");
        k.Dress("Sweetheart Lingerie", Top, folder: 2, material: "b");
        k.Imc(Top, E(1), E(1), E(2));
        Assert.Contains("Rose", k.ResolveOne(MaterialPath(Top, 2, "b")));
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        Assert.Single(outcomes);
        Assert.Equal("red/m.mtrl", FilesOf(mod, Red)[MaterialPath(Top, 2, "b")]);
        Assert.Equal("blue/m.mtrl", FilesOf(mod, Blue)[MaterialPath(Top, 2, "b")]);
        Assert.StartsWith(mod, k.ResolveOne(MaterialPath(Top, 2, "b")));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-2")]
    public void No_imc_entry_is_ever_written()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(ManifestImcEdits(mod));
        Assert.Empty(Manifest(mod)["DefaultData"]!["Manipulations"]!.AsArray());
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-33")]
    public void Custom_suffix_material_without_vanilla_file_gets_copies()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("This Old Thing", Top, material: "b");
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.Equal("top_b.mtrl", FilesOf(mod, Default)[MaterialPath(Top, 2, "b")]);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-5")]
    [Trait("Plan", "EDGE-imc-all-variants-14")]
    [Trait("Plan", "EDGE-imc-all-variants-35")]
    public void Second_walk_after_copies_writes_nothing()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        var manifest = File.ReadAllText(Path.Combine(mod, "meta.json"));
        var record = File.ReadAllText(RecordPath(mod));
        foreach (var worn in new[] { 2, 3, 1 })
        {
            var (outcomes, left) = k.Run([k.Worn(Top, worn, mod)]);
            Assert.Empty(outcomes);
            Assert.Empty(left);
        }
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(mod, "meta.json")));
        Assert.Equal(record, File.ReadAllText(RecordPath(mod)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-35")]
    public void Walk_after_write_has_no_left_and_no_write()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        VariantRepointWriter.AfterStep = s => { if (s == "manifest") throw new CrashException(); };
        Assert.Throws<CrashException>(() => k.Run([k.Worn(Top, 2, mod)]));
        VariantRepointWriter.AfterStep = null;
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);   // the pending item is adopted
        Assert.Empty(outcomes);
        Assert.Empty(left);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-9")]
    public void Only_copied_redirects_change()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        var before = Manifest(mod);
        before["Custom"] = new JsonObject { ["Kept"] = true };
        WriteManifest(mod, before);
        File.WriteAllText(Path.Combine(mod, "x.bin"), "x");
        var hashes = ShaderUpgradeTestKit.Hashes(mod);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);

        var after = Manifest(mod);
        Assert.Equal(before.Select(p => p.Key), after.Select(p => p.Key));
        foreach (var (key, value) in before)
            if (key != "Groups") Assert.True(JsonNode.DeepEquals(value, after[key]), key);
        for (int o = 0; o < 2; o++)
        {
            var bf = before["Groups"]![0]!["Options"]![o]!["Files"]!.AsObject();
            var af = after["Groups"]![0]!["Options"]![o]!["Files"]!.AsObject();
            Assert.Equal(bf.Select(p => p.Key).Append(MaterialPath(Top, 2)), af.Select(p => p.Key));   // appended (V5)
            af.Remove(MaterialPath(Top, 2));
        }
        Assert.True(JsonNode.DeepEquals(before["Groups"], after["Groups"]));   // nothing else in any group changed
        var now = ShaderUpgradeTestKit.Hashes(mod);
        foreach (var (file, hash) in hashes) Assert.Equal(hash, now[file]);
        Assert.Equal(["Proteus/" + VariantRepointWriter.RecordFile], now.Keys.Except(hashes.Keys));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-61")]
    public void Material_variant_of_still_returns_the_mods_folder_after_copies()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.Equal(1, ImcEntrySource.MaterialVariantOf(PenumbraModMeta.ReadAllRedirects(mod), Top.SetTag));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-8")]
    public void Outcome_names_item_folders_and_variants()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        var (outcomes, _) = k.Run([k.Worn(Top, 3, mod)]);
        var (item, folders, variants, copies) = Assert.Single(Assert.Single(outcomes).Written);
        Assert.Equal("e6255 Body", item.Label);
        Assert.Equal([2, 3], folders);
        Assert.Equal([2, 3], variants);
        Assert.Equal(4, copies);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-6")]
    public void Undo_removes_copies_still_as_written()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        var original = File.ReadAllText(Path.Combine(mod, "meta.json"));
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.Equal(VariantRepointWriter.Undone.Removed,
                     VariantRepointWriter.UndoItem(mod, Top, true, out var removed, out var kept, out _));
        Assert.Equal((2, 0), (removed, kept));
        Assert.False(FilesOf(mod, Red).ContainsKey(MaterialPath(Top, 2)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original)!["Groups"], Manifest(mod)["Groups"]));
        Assert.Contains(Top.Key, VariantRepointWriter.ReadRecord(mod)!.Declined);
        Assert.Equal(RepointReason.Declined, Assert.Single(k.Plan([k.Worn(Top, 2, mod)]).Left).Reason);
        VariantRepointWriter.ClearDeclines(mod, [Top]);
        Assert.Single(k.Plan([k.Worn(Top, 2, mod)]).PerMod);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-1")]
    [Trait("Plan", "EDGE-imc-all-variants-64")]
    public void Material_id_0_is_never_a_copy_target()
    {
        // In-game QA (1433): moonfire lace gained a dead v0000 key for a variant whose IMC entry names material 0.
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(0));
        k.Run([k.Worn(Top, 2, mod)]);
        Assert.True(FilesOf(mod, Red).ContainsKey(MaterialPath(Top, 2)));
        Assert.False(FilesOf(mod, Red).ContainsKey(MaterialPath(Top, 0)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-64")]
    public void Worn_variant_on_material_0_writes_nothing()
    {
        // The worn variant names material 0 (no folder the game loads): copies to other folders cannot change what is
        // worn, so the mod is not written for it.
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        var before = File.ReadAllText(Path.Combine(mod, "meta.json"));
        k.Imc(Top, E(1), E(0), E(2));
        k.Run([k.Worn(Top, 1, mod)]);
        Assert.Equal(before, File.ReadAllText(Path.Combine(mod, "meta.json")));
        Assert.Null(VariantRepointWriter.ReadRecord(mod));
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-6")]
    [Trait("Plan", "EDGE-imc-all-variants-63")]
    public void Undo_and_reconcile_work_on_a_manifest_without_DefaultData()
    {
        // In-game QA (1433): most mods keep everything in groups and have no DefaultData at all; undo refused them as
        // "could not be read" and reconcile skipped them. No DefaultData is still a readable manifest: none, not unknown.
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        var meta = Path.Combine(mod, "meta.json");
        var stripped = JsonNode.Parse(File.ReadAllText(meta))!.AsObject();
        stripped.Remove("DefaultData");
        File.WriteAllText(meta, stripped.ToJsonString());
        k.Imc(Top, E(1), E(1), E(2));

        k.Run([k.Worn(Top, 2, mod)]);
        Assert.True(FilesOf(mod, Red).ContainsKey(MaterialPath(Top, 2)));
        // Reconcile acts on it: a copy the user repointed since is released (it used to skip the mod entirely).
        EditFiles(mod, f => f[MaterialPath(Top, 2)] = "red/custom.mtrl", "Colour", "Red");
        Assert.Equal(1, VariantRepointWriter.Reconcile(mod).Forgotten);

        Assert.Equal(VariantRepointWriter.Undone.Removed,
                     VariantRepointWriter.UndoItem(mod, Top, true, out var removed, out _, out var message));
        Assert.True(string.IsNullOrEmpty(message), message);
        Assert.Equal(1, removed);   // Blue's copy; Red's is the user's now
        Assert.False(Manifest(mod).ContainsKey("DefaultData"));
        Assert.False(FilesOf(mod, Blue).ContainsKey(MaterialPath(Top, 2)));
        Assert.Equal("red/custom.mtrl", FilesOf(mod, Red)[MaterialPath(Top, 2)]);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-6")]
    [Trait("Plan", "EDGE-imc-all-variants-49")]
    public void Undo_removes_legacy_imc_entries()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        [Imc(Top, 2, E(1, attr: 1))]);
        LegacyRecord(mod, Top, 1, (2, E(1, attr: 1)));
        VariantRepointWriter.UndoItem(mod, Top, false, out var removed, out _, out _);
        Assert.Equal(1, removed);
        Assert.Empty(ManifestImcEdits(mod));
        Assert.False(File.Exists(RecordPath(mod)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-49")]
    public void Legacy_v1_record_is_read_reconciled_and_undone()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        [Imc(Top, 2, E(1))]);
        LegacyRecord(mod, Top, 1, (2, E(1)));
        k.Imc(Top, E(1), E(1), E(2), E(3));
        // Variant 2 reads Dressed through the legacy entry; variant 3 gets copies; no new IMC is written.
        k.Run([k.Worn(Top, 3, mod)]);
        var record = VariantRepointWriter.ReadRecord(mod)!;
        Assert.Equal(2, record.Version);
        var item = Assert.Single(record.Items);
        Assert.Equal([2], item.Variants.Select(v => v.Variant));
        Assert.Equal([3], item.Redirects.Select(r => r.Folder));
        Assert.Single(ManifestImcEdits(mod));
        VariantRepointWriter.UndoItem(mod, Top, false, out var removed, out _, out _);
        Assert.Equal(2, removed);
        Assert.Empty(ManifestImcEdits(mod));
        Assert.False(FilesOf(mod, Default).ContainsKey(MaterialPath(Top, 3)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-49")]
    public void Legacy_imc_entries_stripped_on_stale_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        [Imc(Top, 2, E(1))]);
        LegacyRecord(mod, Top, 1, (2, E(1)));
        EditFiles(mod, f => f[MaterialPath(Top, 4)] = "other.mtrl");
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.True(rc.ManifestChanged);
        Assert.Empty(ManifestImcEdits(mod));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-49")]
    public void Legacy_imc_variants_read_dressed_no_new_imc()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        [Imc(Top, 2, E(1))]);
        LegacyRecord(mod, Top, 1, (2, E(1)));
        k.Imc(Top, E(1), E(1), E(2));
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Empty(left);
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
    [Trait("Plan", "EDGE-imc-all-variants-20")]
    public void Unreadable_record_is_never_replaced()
    {
        using var k = new VariantRepointTestKit();
        var legs = new Item('e', 6255, "dwn");
        var mod = k.Mod("Outfit", new()
        {
            [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl",
            [ModelPath(legs)] = "dwn.mdl", [MaterialPath(legs, 1)] = "dwn_a.mtrl",
        });
        k.ImcSlots(Top.SetTag, (1, [E(1), E(1), E(2)]), (3, [E(1), E(1), E(2)]));
        k.Run([k.Worn(Top, 2, mod)]);
        File.WriteAllText(RecordPath(mod), "{ corrupt");
        var manifest = File.ReadAllText(Path.Combine(mod, "meta.json"));
        var copy = new PenumbraModMeta.ContainerRedirect(Default, MaterialPath(legs, 2), "dwn_a.mtrl");
        var outcome = VariantRepointWriter.Apply(mod, [new ItemWrite(legs, 1, [(2, copy)], [2])], () => true);
        Assert.Empty(outcome.Written);
        Assert.Equal(RepointReason.WriteFailed, Assert.Single(outcome.Left).Reason);
        Assert.Equal("{ corrupt", File.ReadAllText(RecordPath(mod)));
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(mod, "meta.json")));
        Assert.Equal(VariantRepointWriter.Undone.Failed, VariantRepointWriter.UndoItem(mod, Top, true, out _, out _, out _));
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
        var copy = new PenumbraModMeta.ContainerRedirect(Default, MaterialPath(Top, 2), "a.mtrl");
        Assert.Equal(RepointReason.LegacyMod,
                     Assert.Single(VariantRepointWriter.Apply(mod, [new ItemWrite(Top, 1, [(2, copy)], [2])], () => true).Left).Reason);
        Assert.Equal(meta, File.ReadAllBytes(Path.Combine(mod, "meta.json")));
        Assert.Equal(dm, File.ReadAllBytes(Path.Combine(mod, "default_mod.json")));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-53")]
    public void Penumbra_respelled_copy_still_ours()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        // Penumbra re-saves: the key upper-cased, the file with back-slashes.
        EditFiles(mod, f =>
        {
            var key = f.First(p => p.Key == MaterialPath(Top, 2)).Key;
            var file = f[key]!.GetValue<string>();
            f.Remove(key);
            f[key.ToUpperInvariant()] = file.Replace('/', '\\').ToUpperInvariant();
        });
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Empty(left);
        Assert.Single(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Redirects);
        VariantRepointWriter.UndoItem(mod, Top, false, out var removed, out var kept, out _);
        Assert.Equal((1, 0), (removed, kept));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-16")]
    [Trait("Plan", "EDGE-imc-all-variants-48")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Removed_copies_decline_the_item()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        EditFiles(mod, f => f.Remove(MaterialPath(Top, 2)), "Colour", "Red");
        var (outcomes, _) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Contains(Top.Key, VariantRepointWriter.ReadRecord(mod)!.Declined);
        Assert.False(FilesOf(mod, Red).ContainsKey(MaterialPath(Top, 2)));   // never put back
        Assert.True(FilesOf(mod, Blue).ContainsKey(MaterialPath(Top, 2)));    // the other stands, recorded
        Assert.Single(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Redirects);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-17")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Changed_legacy_imc_entry_forgotten_and_kept()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "top_a.mtrl" },
                        [Imc(Top, 2, E(1))]);
        LegacyRecord(mod, Top, 1, (2, E(1)));
        var m = Manifest(mod);
        m["DefaultData"]!["Manipulations"]![0]!["Manipulation"]!["Entry"]!["DecalId"] = 7;
        WriteManifest(mod, m);
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.Equal(1, rc.Forgotten);
        Assert.Equal(VariantRepointWriter.Undone.NothingRecorded, VariantRepointWriter.UndoItem(mod, Top, true, out _, out _, out _));
        Assert.Single(ManifestImcEdits(mod));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-52")]
    [Trait("Plan", "EDGE-imc-all-variants-48")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Repointed_copy_released_others_stand()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        EditFiles(mod, f => f[MaterialPath(Top, 2)] = "red/custom.mtrl", "Colour", "Red");
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.Equal(1, rc.Forgotten);
        Assert.False(rc.ManifestChanged);
        var item = VariantRepointWriter.ReadRecord(mod)!.Items.Single();
        Assert.Single(item.Redirects);
        var released = Assert.Single(item.Released);
        Assert.Equal("red/custom.mtrl", released.File);
        Assert.DoesNotContain(Top.Key, VariantRepointWriter.ReadRecord(mod)!.Declined);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Empty(left);   // not "several-variants": the released key is not a folder
        VariantRepointWriter.UndoItem(mod, Top, false, out var removed, out _, out _);
        Assert.Equal(1, removed);
        Assert.Equal("red/custom.mtrl", FilesOf(mod, Red)[MaterialPath(Top, 2)]);   // theirs is left alone
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-52")]
    public void Released_entry_kept_while_the_key_exists_and_dropped_when_gone()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        EditFiles(mod, f => f[MaterialPath(Top, 2)] = "red/custom.mtrl", "Colour", "Red");
        VariantRepointWriter.Reconcile(mod);
        EditFiles(mod, f => f[MaterialPath(Top, 2)] = "red/again.mtrl", "Colour", "Red");
        VariantRepointWriter.Reconcile(mod);
        Assert.Single(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Released);
        EditFiles(mod, f => f.Remove(MaterialPath(Top, 2)), "Colour", "Red");
        VariantRepointWriter.Reconcile(mod);
        Assert.Empty(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Released);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-50")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Renamed_option_copy_is_found_and_adopted()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var m = Manifest(mod);
        m["Groups"]![0]!["Options"]![0]!["Name"] = "Crimson";
        WriteManifest(mod, m);
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.Equal((0, 0, false), (rc.Dropped, rc.Forgotten, rc.ManifestChanged));
        var record = VariantRepointWriter.ReadRecord(mod)!;
        Assert.Empty(record.Declined);
        Assert.Contains(record.Items.Single().Redirects, r => r.Container.Option == "Crimson");
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Empty(left);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-51")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Removed_combining_option_copy_adopted_at_new_index()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Combo", new() { [ModelPath(Top)] = "top.mdl" },
                        groups: [("Looks", "Combining", [("Plain", new() { ["chara/x.tex"] = "x.tex" }, []),
                                                         ("Shiny", new() { [MaterialPath(Top, 1)] = "a.mtrl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var m = Manifest(mod);
        m["Groups"]![0]!["Containers"]!.AsArray().RemoveAt(0);
        m["Groups"]![0]!["Options"]!.AsArray().RemoveAt(0);
        WriteManifest(mod, m);
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.Equal(0, rc.Dropped);
        Assert.Equal(0, VariantRepointWriter.ReadRecord(mod)!.Items.Single().Redirects.Single().Container.Index);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-51")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Combining_copy_content_gone_declines()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Combo", new() { [ModelPath(Top)] = "top.mdl", [MaterialPath(Top, 1)] = "base.mtrl" },
                        groups: [("Looks", "Combining", [("Shiny", new() { [MaterialPath(Top, 1)] = "a.mtrl" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        EditFiles(mod, f => f.Remove(MaterialPath(Top, 2)), "Looks", index: 0);
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.Equal(1, rc.Dropped);
        Assert.Contains(Top.Key, VariantRepointWriter.ReadRecord(mod)!.Declined);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-51")]
    public void Whole_group_gone_searches_mod_wide()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        // The author folds the "Colour" group into a new "Paint" group, keeping both options (and our copies) as they were.
        var m = Manifest(mod);
        m["Groups"]![0]!["Name"] = "Paint";
        WriteManifest(mod, m);
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.Equal(0, rc.Dropped);
        Assert.All(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Redirects, r => Assert.Equal("Paint", r.Container.Group));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-60")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Identical_pair_in_another_group_is_not_adopted()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", new() { [ModelPath(Top)] = "top.mdl" },
                        groups: [("Colour", "Single", [("Red", new() { [MaterialPath(Top, 1)] = "a.mtrl" }, [])]),
                                 ("Other", "Single", [("One", new() { ["chara/y.tex"] = "y.tex" }, [])])]);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        // An author update drops our copy and ships the same pair in another group, with new content.
        var m = Manifest(mod);
        m["Version"] = "2.0";
        m["Groups"]![0]!["Options"]![0]!["Files"]!.AsObject().Remove(MaterialPath(Top, 2));
        m["Groups"]![1]!["Options"]![0]!["Files"]!.AsObject()[MaterialPath(Top, 2)] = "a.mtrl";
        WriteManifest(mod, m);
        var rc = VariantRepointWriter.Reconcile(mod);
        Assert.True(rc.Replanned);
        Assert.Equal(VariantRepointWriter.Undone.NothingRecorded, VariantRepointWriter.UndoItem(mod, Top, true, out _, out _, out _));
        Assert.Equal("a.mtrl", FilesOf(mod, new("Other", "One"))[MaterialPath(Top, 2)]);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-18")]
    [Trait("Plan", "EDGE-imc-all-variants-48")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Author_update_new_folder_strips_and_replans()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        // The author now ships v0004 instead of v0001 (our copies at v0002/v0003 point at a folder it no longer ships).
        EditFiles(mod, f => { f.Remove(MaterialPath(Top, 1)); f[MaterialPath(Top, 4)] = "new_a.mtrl"; });
        k.Run([k.Worn(Top, 2, mod)]);
        var files = FilesOf(mod, Default);
        Assert.Equal("new_a.mtrl", files[MaterialPath(Top, 2)]);
        Assert.Equal("new_a.mtrl", files[MaterialPath(Top, 3)]);
        Assert.Equal("new_a.mtrl", files[MaterialPath(Top, 1)]);
        Assert.Equal(4, VariantRepointWriter.ReadRecord(mod)!.Items.Single().MaterialId);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-18")]
    [Trait("Plan", "EDGE-imc-all-variants-48")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Author_update_several_folders_strips_ours()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2), E(3));
        k.Run([k.Worn(Top, 2, mod)]);
        EditFiles(mod, f => f[MaterialPath(Top, 4)] = "other.mtrl");
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Equal(RepointReason.SeveralVariants, Assert.Single(left).Reason);
        Assert.False(FilesOf(mod, Default).ContainsKey(MaterialPath(Top, 2)));
        Assert.False(File.Exists(RecordPath(mod)));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-34")]
    [Trait("Plan", "EDGE-imc-all-variants-48")]
    [Trait("Plan", "AC-imc-all-variants-7")]
    public void Author_update_fingerprint_change_replans_not_declines()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var m = Manifest(mod);
        m["Version"] = "2.0";
        var files = m["DefaultData"]!["Files"]!.AsObject();
        files.Remove(MaterialPath(Top, 2));
        files["chara/new.tex"] = "new.tex";
        WriteManifest(mod, m);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        Assert.Single(outcomes);
        Assert.True(FilesOf(mod, Default).ContainsKey(MaterialPath(Top, 2)));
        Assert.Empty(VariantRepointWriter.ReadRecord(mod)!.Declined);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-34")]
    [Trait("Plan", "EDGE-imc-all-variants-46")]
    public void Copies_are_not_a_second_folder_and_keep_the_fingerprint()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        var before = Fingerprint(mod);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var record = VariantRepointWriter.ReadRecord(mod)!;
        Assert.Equal(before, record.Fingerprint);
        Assert.Equal(before, Fingerprint(mod, record.AllCopies()));
        var (candidates, _) = Candidates([k.Worn(Top, 2, mod)], k.Inputs());
        var c = Assert.Single(candidates);
        Assert.Equal(1, c.Folder);
        Assert.Equal(2, c.Sources.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-34")]
    public void Fingerprint_unchanged_by_penumbra_resave()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        var before = Fingerprint(mod);
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
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        PenumbraModMeta.MergeOptionRedirects(mod, "Colour", "Red",
            new System.Collections.Generic.Dictionary<string, string> { ["chara/x_mask.tex"] = "Shader Upgrade/m.tex" });
        k.Run([k.Worn(Top, 2, mod)]);   // a walk finds our copies intact and re-stamps
        Assert.Equal(Fingerprint(mod, VariantRepointWriter.ReadRecord(mod)!.AllCopies()),
                     VariantRepointWriter.ReadRecord(mod)!.Fingerprint);
        EditFiles(mod, f => f.Remove(MaterialPath(Top, 2)), "Colour", "Red");
        EditFiles(mod, f => f.Remove(MaterialPath(Top, 2)), "Colour", "Blue");
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(outcomes);
        Assert.Equal(RepointReason.Declined, Assert.Single(left).Reason);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-19")]
    public void Pending_copies_after_crash_are_resolved()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Dress("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        VariantRepointWriter.AfterStep = s => { if (s == "pending") throw new CrashException(); };
        Assert.Throws<CrashException>(() => k.Run([k.Worn(Top, 2, mod)]));
        VariantRepointWriter.AfterStep = null;
        Assert.True(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Pending);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        Assert.Single(outcomes);
        Assert.False(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Pending);
        Assert.Empty(VariantRepointWriter.ReadRecord(mod)!.Declined);
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
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(mod, "meta.json")));
        Assert.False(File.Exists(RecordPath(mod)));
        Assert.False(Directory.Exists(Path.Combine(mod, "Proteus")));
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
        k.ImcSlots(RightRing.SetTag, (3, [E(1), E(1), E(2)]), (4, [E(1), E(1), E(1)]));
        k.Run([k.Worn(RightRing, 2, mod), k.Worn(LeftRing, 2, mod)]);
        var files = FilesOf(mod, Default);
        Assert.True(files.ContainsKey(MaterialPath(RightRing, 2)));
        Assert.False(files.ContainsKey(MaterialPath(LeftRing, 2)));
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
        k.Priority[k.Priority.IndexOf(mod)] = moved;
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
        Assert.Equal("ünï/top_a.mtrl", FilesOf(mod, Default)[MaterialPath(Top, 2)]);
    }

    [Fact]
    [Trait("Plan", "AC-imc-all-variants-6")]
    public void Undo_after_reconcile_counts_replaced_copies_as_kept()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        EditFiles(mod, f => f[MaterialPath(Top, 2)] = "red/custom.mtrl", "Colour", "Red");
        k.Run([k.Worn(Top, 2, mod)]);   // a walk in between releases the repointed copy
        k.Run([k.Worn(Top, 3, mod)]);
        VariantRepointWriter.UndoItem(mod, Top, false, out var removed, out var kept, out _);
        Assert.Equal((1, 1), (removed, kept));
        // The released key stays recorded, so the next walk doesn't read their redirect as a second folder.
        Assert.Single(VariantRepointWriter.ReadRecord(mod)!.Items.Single().Released);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.DoesNotContain(left, l => l.Reason == RepointReason.SeveralVariants);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-26")]
    public void Held_manifest_during_reconcile_and_undo_changes_nothing()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        var record = File.ReadAllText(RecordPath(mod));
        using (new FileStream(Path.Combine(mod, "meta.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rc = VariantRepointWriter.Reconcile(mod);
            Assert.Equal((0, 0, false), (rc.Dropped, rc.Forgotten, rc.ManifestChanged));
            Assert.Equal(VariantRepointWriter.Undone.Failed, VariantRepointWriter.UndoItem(mod, Top, true, out _, out _, out _));
        }
        Assert.Equal(record, File.ReadAllText(RecordPath(mod)));
        Assert.Equal(2, VariantRepointWriter.ReadRecord(mod)!.Items.Single().Redirects.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-15")]
    [Trait("Plan", "EDGE-imc-all-variants-45")]
    public void Source_added_after_the_first_copies_is_copied_not_outranked()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Colours("Dress", Top);
        k.Imc(Top, E(1), E(1), E(2));
        k.Run([k.Worn(Top, 2, mod)]);
        // The author adds a Green option holding v0001 in place (no author update: our copies are intact).
        var m = Manifest(mod);
        m["Groups"]![0]!["Options"]!.AsArray().Add(new JsonObject
        {
            ["Name"] = "Green", ["Description"] = "", ["Files"] = new JsonObject { [MaterialPath(Top, 1)] = "green/m.mtrl" },
            ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray(),
        });
        WriteManifest(mod, m);
        // Green selected: nothing serves v0002 (the kit counts every option as on, so say it outright).
        k.Served[MaterialPath(Top, 2)] = MaterialPath(Top, 2);
        var (outcomes, left) = k.Run([k.Worn(Top, 2, mod)]);
        Assert.Empty(left);
        Assert.Equal(1, Assert.Single(outcomes).Redirects);
        Assert.Equal("green/m.mtrl", FilesOf(mod, new("Colour", "Green"))[MaterialPath(Top, 2)]);
    }
}
