using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// Ports written into a mod on disk and taken out again. These edit somebody else's mod, so what matters most is what
/// they leave alone: the author's files, every other redirect, and the order of the groups.
/// </summary>
public class HairPortWriterTests
{
    private static readonly string Source = HairPortPlan.HairPath(801, Id);
    private static readonly string Target = HairPortPlan.HairPath(201, Id);

    /// <summary>A Miqo'te-only hair mod: a pick-one group of two cuts, and a group of the author's own beside it.</summary>
    private static Mod TwoCuts()
    {
        var mod = new Mod("[" + Group("Cut", "Single",
                              ("Long", new() { [Source] = "long/c0801h0130_hir.mdl" }),
                              ("Short", new() { [Source] = "short/c0801h0130_hir.mdl" }))
                          + "," + Group("Extras", "Multi", ("Bow", new() { ["chara/x/bow.mdl"] = "bow.mdl" })) + "]");
        mod.Put("long/c0801h0130_hir.mdl", HairModel(801));
        mod.Put("short/c0801h0130_hir.mdl", HairModel(801));
        mod.Put("bow.mdl", [1, 2, 3]);
        return mod;
    }

    private static HairPortWriter.PortWrite Write(Mod mod, string group, string option, byte[]? model = null,
                                                  params HairPortWriter.MaterialWrite[] materials)
    {
        var owner = mod.Owner(group, option);
        var rel = HairPortPlan.FileFor(owner, Source) ?? "x.mdl";
        return new HairPortWriter.PortWrite(owner, Source, rel, "abcd1234", Target, model ?? HairModel(201), materials);
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-4")]
    public void Each_option_serving_the_source_gets_its_own_port()
    {
        using var mod = TwoCuts();
        var before = mod.Owners.Select(o => (o.Group, o.Option)).ToList();
        var outcome = HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long"), Write(mod, "Cut", "Short")], auto: false);
        Assert.True(outcome.Ok, outcome.Message);

        var longFile = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target);
        var shortFile = HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target);
        Assert.NotNull(longFile);
        Assert.NotNull(shortFile);
        Assert.NotEqual(longFile, shortFile);
        Assert.True(mod.Has(longFile!) && mod.Has(shortFile!));
        // No group added or moved, and the author's files untouched.
        Assert.Equal(before, mod.Owners.Select(o => (o.Group, o.Option)).ToList());
        Assert.Null(HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Target));
        Assert.Equal(HairModel(801), mod.Get("long/c0801h0130_hir.mdl"));
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-4")]
    public void Port_of_default_data_hair_goes_into_default_data()
    {
        using var mod = new Mod(defaults: new Dictionary<string, string> { [Source] = "hair.mdl" });
        mod.Put("hair.mdl", HairModel(801));
        var outcome = HairPortWriter.Save(mod.Root, [Write(mod, "", "")], auto: true);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal($"Hair Ports/Default/{Target}", HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Target));
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-10")]
    public void Ports_are_plain_mod_content()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        // What Penumbra reads, with no Proteus state: a redirect in the option, to a file in the mod, FileVersion 4.
        var redirect = PenumbraModMeta.ReadAllRedirects(mod.Root).Single(r => r.GamePath == Target);
        Assert.Equal("Cut / Long", redirect.Source);
        Assert.True(mod.Has(redirect.File));
        Assert.Equal(4, PenumbraModMeta.ReadFileVersion(mod.Root));
        Assert.Contains("\"Identifier\": \"id-1\"", mod.Manifest);
        Assert.Contains("\"Author\": \"Someone\"", mod.Manifest);
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-5")]
    public void A_covered_race_is_never_overwritten()
    {
        using var mod = new Mod("[" + Group("Cut", "Single",
            ("Long", new() { [Source] = "long.mdl", [Target] = "mine/c0201.mdl" })) + "]");
        mod.Put("long.mdl", HairModel(801));
        mod.Put("mine/c0201.mdl", [9, 9, 9]);
        var outcome = HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true);
        Assert.False(outcome.Ok);
        Assert.Equal("mine/c0201.mdl", HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.Equal([9, 9, 9], mod.Get("mine/c0201.mdl"));
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-7")]
    public void Undo_removes_redirects_and_our_files_only()
    {
        using var mod = TwoCuts();
        var manifest = mod.Manifest;
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: false).Ok);
        var outcome = HairPortWriter.Undo(mod.Root, _ => true, decline: false);
        Assert.True(outcome.Ok, outcome.Message);

        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.False(Directory.Exists(Path.Combine(mod.Root, HairPortPlan.Folder)));
        Assert.Equal(HairModel(801), mod.Get("long/c0801h0130_hir.mdl"));
        Assert.Equal([1, 2, 3], mod.Get("bow.mdl"));
        // Every redirect and option the author wrote is as it was.
        Assert.Equal(Redirects(manifest, mod), PenumbraModMeta.ReadAllRedirects(mod.Root).Select(r => (r.GamePath, r.File, r.Source)));
    }

    private static IEnumerable<(string, string, string)> Redirects(string manifest, Mod mod)
    {
        var copy = Path.Combine(Path.GetTempPath(), "proteus_hairport_manifest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(copy);
        try
        {
            File.WriteAllText(Path.Combine(copy, "meta.json"), manifest);
            return PenumbraModMeta.ReadAllRedirects(copy).Select(r => (r.GamePath, r.File, r.Source)).ToList();
        }
        finally { Directory.Delete(copy, true); }
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-7")]
    public void Undo_all_in_mod_removes_every_port()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long"), Write(mod, "Cut", "Short")], auto: true).Ok);
        Assert.Equal(2, HairPortRecord.Read(mod.Root).Ports.Count);
        var outcome = HairPortWriter.Undo(mod.Root, _ => true, decline: true);
        Assert.Equal(2, outcome.Count);
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
        Assert.False(PenumbraModMeta.ReadAllRedirects(mod.Root).Any(r => r.GamePath == Target));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-23")]
    public void Undo_with_decline_persists_and_manual_port_clears_it()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        HairPortWriter.Undo(mod.Root, _ => true, decline: true);
        Assert.Contains(HairPortPlan.DeclineKey(Target), HairPortRecord.Read(mod.Root).Declined);

        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: false).Ok);
        Assert.Empty(HairPortRecord.Read(mod.Root).Declined);
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    public void Re_port_replaces_a_stale_port_in_place()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        var file = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;
        var again = HairModel(201, material: "/mt_c0201h0130_hir_b.mtrl");
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long", again)], auto: true).Ok);
        Assert.Equal(file, HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.Equal(again, mod.Get(file));
        Assert.Single(HairPortRecord.Read(mod.Root).Ports);
    }

    [Theory]
    [Trait("Plan", "AC-hair-race-port-8")]
    [Trait("Plan", "EDGE-hair-race-port-16")]
    [InlineData(MeshVolumeService.BackupSubdir)]
    [InlineData(MeshToggleService.BackupSubdir)]
    public void A_port_edited_since_is_neither_remade_nor_undone(string backupSubdir)
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        var file = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;
        var edited = mod.Get(file);
        mod.Put($"Proteus/{backupSubdir}/{file}", edited);

        var remake = HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long", HairModel(201, material: "/mt_x.mtrl"))], auto: true);
        Assert.False(remake.Ok);
        Assert.Contains(ModelBackupOrder.BlockedMessage("").Split(' ')[0], remake.Message);
        Assert.Equal(edited, mod.Get(file));

        Assert.False(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-16")]
    public void Hat_fitted_port_forgets_hat_record_before_re_port()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        var file = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;
        mod.Put($"Proteus/{HatCompatService.BackupSubdir}/{file}", [4, 4]);
        mod.Put($"Proteus/{HatCompatService.RecordFile}", System.Text.Encoding.UTF8.GetBytes(
            "{\"Files\":[\"" + file + "\"],\"Hidden\":{},\"Versions\":{},\"Replaced\":[]}"));

        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        Assert.False(mod.Has($"Proteus/{HatCompatService.BackupSubdir}/{file}"));
        Assert.Equal(0, HatCompatService.PatchedCount(mod.Root));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-7")]
    public void Material_redirect_points_at_the_authors_file_or_a_copy()
    {
        const string theirs = "chara/human/c0801/obj/hair/h0105/material/v0001/mt_c0801h0105_hir_a.mtrl";
        const string ours = "chara/human/c0201/obj/hair/h0105/material/v0001/mt_c0201h0105_hir_a.mtrl";
        const string copied = "chara/human/c0201/obj/hair/h0105/material/v0001/mt_c0201h0105_acc_b.mtrl";
        using var mod = TwoCuts();
        var outcome = HairPortWriter.Save(mod.Root,
            [Write(mod, "Cut", "Long", null, new HairPortWriter.MaterialWrite(ours, "mats/a.mtrl", null),
                                             new HairPortWriter.MaterialWrite(copied, null, [7, 7]))], auto: false);
        Assert.True(outcome.Ok, outcome.Message);
        var owner = mod.Owner("Cut", "Long");
        Assert.Equal("mats/a.mtrl", HairPortPlan.FileFor(owner, ours));
        var copy = HairPortPlan.FileFor(owner, copied)!;
        Assert.True(HairPortPlan.IsPortFile(copy));
        Assert.Equal([7, 7], mod.Get(copy));
        Assert.Null(HairPortPlan.FileFor(owner, theirs));

        // Undone, the copy goes and the author's file stays.
        mod.Put("mats/a.mtrl", [1]);
        HairPortWriter.Undo(mod.Root, _ => true, decline: false);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), ours));
        Assert.False(mod.Has(copy));
        Assert.True(mod.Has("mats/a.mtrl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-8")]
    public void Combining_group_source_is_refused()
    {
        var groups = "[{\"Name\":\"Mix\",\"Type\":\"Combining\",\"Options\":[{\"Name\":\"A\"}],\"Containers\":[{\"Files\":{\""
                     + Source + "\":\"a.mdl\"}}]}]";
        using var mod = new Mod(groups);
        var owner = mod.Owners.First(o => o.Group == "Mix");
        var outcome = HairPortWriter.Save(mod.Root,
            [new HairPortWriter.PortWrite(owner, Source, "a.mdl", "x", Target, HairModel(201), [])], auto: true);
        Assert.False(outcome.Ok);
        Assert.Contains("Mix", outcome.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-9")]
    public void Legacy_v3_mod_is_refused_and_untouched()
    {
        using var mod = TwoCuts();
        var v4 = mod.Manifest;
        var owner = mod.Owner("Cut", "Long");
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"), v4.Replace("\"FileVersion\":4", "\"FileVersion\":3"));
        var before = mod.Manifest;
        var outcome = HairPortWriter.Save(mod.Root,
            [new HairPortWriter.PortWrite(owner, Source, "long.mdl", "x", Target, HairModel(201), [])], auto: true);
        Assert.False(outcome.Ok);
        Assert.Equal(before, mod.Manifest);
        Assert.False(Directory.Exists(Path.Combine(mod.Root, HairPortPlan.Folder)));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-22")]
    public void Failed_manifest_write_leaves_no_files_or_record()
    {
        using var mod = TwoCuts();
        var meta = Path.Combine(mod.Root, "meta.json");
        var write = Write(mod, "Cut", "Long");
        File.SetAttributes(meta, FileAttributes.ReadOnly);
        try
        {
            var outcome = HairPortWriter.Save(mod.Root, [write], auto: true);
            Assert.False(outcome.Ok);
        }
        finally { File.SetAttributes(meta, FileAttributes.Normal); }
        Assert.False(mod.Has(HairPortPlan.PortFile(write.Into, Target)));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-14")]
    public void Record_prunes_entries_whose_redirect_is_gone()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        var file = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;
        // The author updates the mod: the manifest comes back without the port.
        PenumbraModMeta.RemoveOptionRedirects(mod.Root, "Cut", "Long", new Dictionary<string, string> { [Target] = file });
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-14")]
    public void Unreadable_manifest_prunes_nothing()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"), "{ not json");
        Assert.Single(HairPortRecord.Read(mod.Root).Ports);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-28")]
    public void Shared_file_ported_once_per_option_and_each_kept_until_its_own_undo()
    {
        using var mod = new Mod("[" + Group("Cut", "Single",
            ("Long", new() { [Source] = "same.mdl" }), ("Short", new() { [Source] = "same.mdl" })) + "]");
        mod.Put("same.mdl", HairModel(801));
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long"), Write(mod, "Cut", "Short")], auto: true).Ok);
        var shortFile = HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target)!;
        HairPortWriter.Undo(mod.Root, e => e.Option == "Long", decline: false);
        Assert.True(mod.Has(shortFile));
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target));
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-21")]
    public void Concurrent_saves_into_one_mod_both_survive()
    {
        using var mod = TwoCuts();
        var a = Write(mod, "Cut", "Long");
        var b = Write(mod, "Cut", "Short");
        Parallel.Invoke(
            () => Assert.True(HairPortWriter.Save(mod.Root, [a], auto: true).Ok),
            () => Assert.True(HairPortWriter.Save(mod.Root, [b], auto: true).Ok),
            () => { lock (ModManifestLock.Gate) PenumbraModMeta.MergeOptionRedirects(mod.Root, "Extras", "Bow",
                        new Dictionary<string, string> { ["chara/x/ribbon.mdl"] = "bow.mdl" }); });
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target));
        Assert.NotNull(HairPortPlan.FileFor(mod.Owner("Extras", "Bow"), "chara/x/ribbon.mdl"));
        Assert.Equal(2, HairPortRecord.Read(mod.Root).Ports.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-6")]
    public void Est_entries_are_not_carried()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        Assert.DoesNotContain("\"Type\": \"Est\"", mod.Manifest);
        Assert.Empty(PenumbraModMeta.TryReadDefaultData(mod.Root)!.Value.Manipulations);
    }

    /// <summary>
    /// A save into two options whose second manifest write fails: the first option's redirects are taken back out before
    /// the files they name are deleted, so nothing points at a file that is gone.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-22")]
    public void Failure_after_one_option_is_written_takes_that_option_back_out()
    {
        using var mod = TwoCuts();
        var manifest = mod.Manifest;
        HairPortWriter.BeforeMerge = done => { if (done == 1) throw new IOException("the manifest is in use"); };
        HairPortWriter.Outcome outcome;
        try { outcome = HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long"), Write(mod, "Cut", "Short")], auto: true); }
        finally { HairPortWriter.BeforeMerge = null; }

        Assert.False(outcome.Ok);
        Assert.False(PenumbraModMeta.ReadAllRedirects(mod.Root).Any(r => r.GamePath == Target));
        Assert.Equal(Redirects(manifest, mod), PenumbraModMeta.ReadAllRedirects(mod.Root).Select(r => (r.GamePath, r.File, r.Source)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(mod.Root, HairPortPlan.Folder), "*", SearchOption.AllDirectories));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
    }

    /// <summary>An undo that cannot read the manifest changes nothing: no file is deleted as if nothing named it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-14")]
    public void Undo_with_an_unreadable_manifest_deletes_nothing()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long"), Write(mod, "Cut", "Short")], auto: true).Ok);
        var files = Directory.EnumerateFiles(Path.Combine(mod.Root, HairPortPlan.Folder), "*", SearchOption.AllDirectories).ToList();
        var meta = Path.Combine(mod.Root, "meta.json");
        var text = File.ReadAllText(meta);
        File.WriteAllText(meta, "{ not json");
        var outcome = HairPortWriter.Undo(mod.Root, e => e.Option == "Long", decline: true);
        Assert.False(outcome.Ok);
        Assert.All(files, f => Assert.True(File.Exists(f)));
        File.WriteAllText(meta, text);
        Assert.Equal(2, HairPortRecord.Read(mod.Root).Ports.Count);
        Assert.Empty(HairPortRecord.Read(mod.Root).Declined);
    }

    /// <summary>
    /// Races that share one material redirect are undone together: one undone alone would draw its own game hairstyle
    /// with the material the others still publish.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-7")]
    [Trait("Plan", "EDGE-hair-race-port-23")]
    public void Undoing_one_race_of_a_shared_material_undoes_the_set()
    {
        const string shared = "chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_a.mtrl";
        using var mod = TwoCuts();
        var owner = mod.Owner("Cut", "Long");
        var mat = new HairPortWriter.MaterialWrite(shared, null, [3]);
        var highlander = HairPortPlan.HairPath(401, Id);
        Assert.True(HairPortWriter.Save(mod.Root,
        [
            new HairPortWriter.PortWrite(owner, Source, "long/c0801h0130_hir.mdl", "x", Target, HairModel(201), [mat]),
            new HairPortWriter.PortWrite(owner, Source, "long/c0801h0130_hir.mdl", "x", highlander, HairModel(401), [mat]),
        ], auto: true).Ok);

        var outcome = HairPortWriter.Undo(mod.Root, e => e.TargetGamePath == Target, decline: true);
        Assert.Equal(2, outcome.Count);
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), shared));
        Assert.Contains(HairPortPlan.DeclineKey(highlander), HairPortRecord.Read(mod.Root).Declined);
    }

    /// <summary>
    /// A shared-material set with one port the user edited since is undone all or not at all: the port kept must not
    /// lose the material the others would take out with them.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-7")]
    [Trait("Plan", "EDGE-hair-race-port-16")]
    public void A_shared_material_set_with_an_edited_port_is_left_whole()
    {
        const string shared = "chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_a.mtrl";
        using var mod = TwoCuts();
        var owner = mod.Owner("Cut", "Long");
        var mat = new HairPortWriter.MaterialWrite(shared, null, [3]);
        var highlander = HairPortPlan.HairPath(401, Id);
        Assert.True(HairPortWriter.Save(mod.Root,
        [
            new HairPortWriter.PortWrite(owner, Source, "long/c0801h0130_hir.mdl", "x", Target, HairModel(201), [mat]),
            new HairPortWriter.PortWrite(owner, Source, "long/c0801h0130_hir.mdl", "x", highlander, HairModel(401), [mat]),
        ], auto: true).Ok);
        var manifest = mod.Manifest;
        var edited = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;
        mod.Put($"Proteus/{MeshVolumeService.BackupSubdir}/{edited}", [1]);
        var copy = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), shared)!;

        var outcome = HairPortWriter.Undo(mod.Root, e => e.TargetGamePath == highlander, decline: true);
        Assert.False(outcome.Ok);
        Assert.Equal(manifest, mod.Manifest);
        Assert.True(mod.Has(copy));
        Assert.Equal(2, HairPortRecord.Read(mod.Root).Ports.Count);
        Assert.Empty(HairPortRecord.Read(mod.Root).Declined);
    }

    /// <summary>
    /// "Undo all" over two separate sets, one of them holding a port the user edited since: that set stays whole, the
    /// other still goes, and the result says why one was left.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-7")]
    [Trait("Plan", "EDGE-hair-race-port-16")]
    public void An_edited_set_is_left_and_the_other_sets_still_go()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long"), Write(mod, "Cut", "Short")], auto: true).Ok);
        var edited = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target)!;
        var other = HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target)!;
        mod.Put($"Proteus/{MeshVolumeService.BackupSubdir}/{edited}", [1]);

        var outcome = HairPortWriter.Undo(mod.Root, _ => true, decline: true);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(1, outcome.Count);
        Assert.Contains(ModelBackupOrder.BlockedMessage("").Split(' ')[0], outcome.Message);
        Assert.Equal(edited, HairPortPlan.FileFor(mod.Owner("Cut", "Long"), Target));
        Assert.True(mod.Has(edited));
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Short"), Target));
        Assert.False(mod.Has(other));
        Assert.Equal("Long", HairPortRecord.Read(mod.Root).Ports.Single().Option);
    }

    /// <summary>
    /// A port remade over itself keeps tracking the material redirect it added to the author's own file — which lives
    /// outside Hair Ports/ — so an undo after the remake still takes it out.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    [Trait("Plan", "EDGE-hair-race-port-7")]
    public void A_remade_port_keeps_its_author_file_material_and_undo_takes_it_out()
    {
        const string ours = "chara/human/c0201/obj/hair/h0005/material/v0001/mt_c0201h0005_hir_a.mtrl";
        using var mod = TwoCuts();
        mod.Put("mats/a.mtrl", [1]);
        var mat = new HairPortWriter.MaterialWrite(ours, "mats/a.mtrl", null);
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long", null, mat)], auto: true).Ok);
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long", HairModel(201, material: "/mt_x.mtrl"), mat)],
                                        auto: true).Ok);
        Assert.Equal("mats/a.mtrl", HairPortRecord.Read(mod.Root).Ports.Single().Materials[ours]);

        Assert.True(HairPortWriter.Undo(mod.Root, _ => true, decline: false).Ok);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), ours));
        Assert.True(mod.Has("mats/a.mtrl"));
    }

    private const string SharedMaterial = "chara/human/c0201/obj/hair/h0130/material/v0001/mt_c0201h0130_hir_a.mtrl";
    private static readonly string Highlander = HairPortPlan.HairPath(401, Id);

    private static HairPortWriter.PortWrite PortTo(Mod mod, string target, params HairPortWriter.MaterialWrite[] materials)
        => new(mod.Owner("Cut", "Long"), Source, "long/c0801h0130_hir.mdl", "x", target,
               HairModel(target == Target ? (ushort)201 : (ushort)401), materials);

    private static readonly HairPortWriter.MaterialWrite Shared = new(SharedMaterial, null, [3]);

    /// <summary>A port remade with the same material keeps publishing it.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    public void A_remade_port_keeps_publishing_its_material()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [PortTo(mod, Target, Shared)], auto: true).Ok);
        Assert.True(HairPortWriter.Save(mod.Root, [PortTo(mod, Target, Shared)], auto: true).Ok);
        var copy = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), SharedMaterial);
        Assert.NotNull(copy);
        Assert.True(mod.Has(copy!));
    }

    /// <summary>One port of a shared material remade without it: the other still uses it, so it stays.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    [Trait("Plan", "EDGE-hair-race-port-23")]
    public void A_material_another_port_still_uses_stays_when_one_is_remade_without_it()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [PortTo(mod, Target, Shared), PortTo(mod, Highlander, Shared)], auto: true).Ok);
        Assert.True(HairPortWriter.Save(mod.Root, [PortTo(mod, Target)], auto: true).Ok);
        var copy = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), SharedMaterial);
        Assert.NotNull(copy);
        Assert.True(mod.Has(copy!));
        Assert.Contains(SharedMaterial, HairPortRecord.Read(mod.Root).Ports.Single(p => p.TargetGamePath == Highlander).Materials.Keys);
    }

    /// <summary>Every port of a shared material remade without it in one save: it is taken out, its copy deleted.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    public void A_material_no_remade_port_uses_any_more_is_taken_out()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [PortTo(mod, Target, Shared), PortTo(mod, Highlander, Shared)], auto: true).Ok);
        var copy = HairPortPlan.FileFor(mod.Owner("Cut", "Long"), SharedMaterial)!;
        Assert.True(HairPortWriter.Save(mod.Root, [PortTo(mod, Target), PortTo(mod, Highlander)], auto: true).Ok);
        Assert.Null(HairPortPlan.FileFor(mod.Owner("Cut", "Long"), SharedMaterial));
        Assert.False(mod.Has(copy));
        Assert.All(HairPortRecord.Read(mod.Root).Ports, p => Assert.Empty(p.Materials));
    }

    /// <summary>
    /// A save that fails after one option's merge — which the rollback takes back out, port included — while that
    /// option's remade port no longer used a redirect it added before: nothing is left published that no record entry
    /// names.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    public void A_remake_that_fails_leaves_no_untracked_redirect()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [PortTo(mod, Target, Shared)], auto: true).Ok);
        // The first option's merge goes in; the second's fails.
        HairPortWriter.BeforeMerge = n => { if (n == 1) throw new IOException("boom"); };
        try { Assert.False(HairPortWriter.Save(mod.Root, [PortTo(mod, Target), Write(mod, "Cut", "Short")], auto: true).Ok); }
        finally { HairPortWriter.BeforeMerge = null; }

        var owner = mod.Owner("Cut", "Long");
        var tracked = HairPortRecord.Read(mod.Root).Ports.SelectMany(p => p.Materials.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(HairPortPlan.FileFor(owner, SharedMaterial) == null || tracked.Contains(SharedMaterial));
    }

    /// <summary>
    /// Which of a remade port's old redirects are no longer needed: not one it still publishes, nor one another port of
    /// its option still uses; one only another option's port uses, one no port uses, and one only a port remade in the
    /// same save used, are.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    public void Unneeded_redirects_are_those_no_port_of_the_option_still_uses()
    {
        static HairPortRecord.Entry Port(string option, string target, params string[] keys)
            => new() { Group = "Cut", Option = option, TargetGamePath = target,
                       Materials = keys.ToDictionary(k => k, k => "Hair Ports/" + k, StringComparer.OrdinalIgnoreCase) };
        var was = Port("Long", Target, "kept", "shared", "otherOption", "unused", "remadeToo");
        var sharer = Port("Long", Highlander, "shared");
        var elsewhere = Port("Short", Target, "otherOption");
        var remadeToo = Port("Long", HairPortPlan.HairPath(601, Id), "remadeToo");
        var record = new HairPortRecord { Ports = [was, sharer, elsewhere, remadeToo] };

        var drop = HairPortWriter.Unneeded(was, new HairPortPlan.Owner("Cut", "Long", "", new Dictionary<string, string>()),
                                           new Dictionary<string, string> { ["kept"] = "x" }, record, [was, remadeToo, null]);
        Assert.Equal(["otherOption", "remadeToo", "unused"], drop.Keys.Order());
    }

    /// <summary>The author's own redirect of a material path is still never taken over.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-5")]
    public void An_authors_material_redirect_is_never_taken_over()
    {
        const string theirs = "chara/human/c0201/obj/hair/h0005/material/v0001/mt_c0201h0005_hir_a.mtrl";
        using var mod = new Mod("[" + Group("Cut", "Single",
            ("Long", new() { [Source] = "long.mdl", [theirs] = "mine.mtrl" })) + "]");
        mod.Put("long.mdl", HairModel(801));
        var owner = mod.Owner("Cut", "Long");
        Assert.True(HairPortWriter.Save(mod.Root,
            [new HairPortWriter.PortWrite(owner, Source, "long.mdl", "x", Target, HairModel(201),
                                          [new HairPortWriter.MaterialWrite(theirs, "other.mtrl", null)])], auto: true).Ok);
        Assert.Equal("mine.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "Long"), theirs));
        Assert.Empty(HairPortRecord.Read(mod.Root).Ports.Single().Materials);
        HairPortWriter.Undo(mod.Root, _ => true, decline: false);
        Assert.Equal("mine.mtrl", HairPortPlan.FileFor(mod.Owner("Cut", "Long"), theirs));
    }

    /// <summary>A record that is there but cannot be read is never written over: no save, no undo.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-14")]
    public void An_unreadable_record_refuses_save_and_undo()
    {
        using var mod = TwoCuts();
        Assert.True(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Long")], auto: true).Ok);
        mod.Put("Proteus/hairport.json", System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var manifest = mod.Manifest;

        Assert.False(HairPortWriter.Save(mod.Root, [Write(mod, "Cut", "Short")], auto: true).Ok);
        Assert.False(HairPortWriter.Undo(mod.Root, _ => true, decline: true).Ok);
        Assert.Equal(manifest, mod.Manifest);
        Assert.Equal("{ not json", System.Text.Encoding.UTF8.GetString(mod.Get("Proteus/hairport.json")));
    }

    /// <summary>A save that fails partway takes out the skeleton entry it gave the first option, too.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-22")]
    public void A_failed_save_takes_the_carried_skeleton_back_out()
    {
        using var mod = TwoCuts();
        var a = Write(mod, "Cut", "Long") with { Est = 170 };
        var b = Write(mod, "Cut", "Short") with { Est = 170 };
        HairPortWriter.BeforeMerge = done => { if (done == 1) throw new IOException("the manifest is in use"); };
        try { Assert.False(HairPortWriter.Save(mod.Root, [a, b], auto: true).Ok); }
        finally { HairPortWriter.BeforeMerge = null; }
        Assert.Null(HairPortPlan.HairEst(mod.Owner("Cut", "Long"), 201, Id));
        Assert.Null(HairPortPlan.HairEst(mod.Owner("Cut", "Short"), 201, Id));
    }

    /// <summary>An edit of redirects alone leaves an option's other fields as they were — no manipulation list made up.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-10")]
    public void A_redirect_edit_adds_no_manipulation_list()
    {
        var groups = "[{\"Name\":\"Cut\",\"Type\":\"Single\",\"Options\":[{\"Name\":\"Long\",\"Files\":{\"" + Source
                     + "\":\"long.mdl\"}}]}]";
        using var mod = new Mod(groups);
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "Cut", "Long", new Dictionary<string, string> { [Target] = "x.mdl" });
        var option = System.Text.Json.Nodes.JsonNode.Parse(mod.Manifest)!["Groups"]![0]!["Options"]![0]!.AsObject();
        Assert.False(option.ContainsKey("Manipulations"));
        Assert.Equal("x.mdl", option["Files"]![Target]!.GetValue<string>());
    }
}
