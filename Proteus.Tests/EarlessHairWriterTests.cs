using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// Earless copies written into a Miqo'te hair mod and taken out again: which redirects change, what is written and
/// deleted, and what is never touched — through <see cref="EarlessHairBuilder"/> and <see cref="EarlessHairWriter"/>.
/// </summary>
public partial class EarlessHairWriterTests
{
    private static readonly string Worn = HairPortPlan.HairPath(801, Id);
    private const string Copy = "Proteus Earless/long.mdl";

    private static Mod Cut(IDictionary<string, string>? defaults = null, string? extraOption = null)
    {
        var options = new List<(string, Dictionary<string, string>)> { ("Long", new() { [Worn] = "long.mdl" }) };
        if (extraOption != null) options.Add((extraOption, new() { [Worn] = extraOption.ToLowerInvariant() + ".mdl" }));
        var mod = new Mod("[" + Group("Cut", "Single", [.. options]) + "]", defaults);
        mod.Put("long.mdl", EaredHairModel(801));
        if (extraOption != null) mod.Put(extraOption.ToLowerInvariant() + ".mdl", EaredHairModel(801, radius: 0.104f));
        return mod;
    }

    private static string? Serves(Mod mod, string group, string option) => HairPortPlan.FileFor(mod.Owner(group, option), Worn);

    private static EarlessHairWriter.Outcome Convert(Mod mod)
    {
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.True(plan.Refusal.Length == 0, plan.Refusal);
        return EarlessHairWriter.Save(mod.Root, plan.Writes);
    }

    private static string Snapshot(Mod mod)
        => string.Join("\n", Directory.EnumerateFiles(mod.Root, "*", SearchOption.AllDirectories).Order()
                                      .Select(f => $"{Path.GetRelativePath(mod.Root, f)} {HairPortPlan.Sha8(File.ReadAllBytes(f))}"));

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-3")]
    public void Every_serving_option_and_default_data_is_swapped()
    {
        using var mod = Cut(new Dictionary<string, string> { [Worn] = "long.mdl" }, extraOption: "Short");
        var author = mod.Get("long.mdl");
        var saved = Convert(mod);
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal(3, saved.Count);
        Assert.Equal(Copy, Serves(mod, "Cut", "Long"));
        Assert.Equal("Proteus Earless/short.mdl", Serves(mod, "Cut", "Short"));
        Assert.Equal(Copy, HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Worn));
        Assert.Equal(author, mod.Get("long.mdl"));
        Assert.Equal(0, EarlessHair.DrawnFur(mod.Get(Copy)));
        Assert.Equal(3, EarlessHairRecord.Read(mod.Root).Swaps.Count);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-5")]
    public void Restore_puts_the_author_file_back_and_deletes_unnamed_copies()
    {
        using var mod = Cut(extraOption: "Short");
        Assert.True(Convert(mod).Ok);
        var restored = EarlessHairWriter.Restore(mod.Root);
        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(2, restored.Count);
        Assert.All(restored.RestoredPaths, p => Assert.Equal(Worn, p, ignoreCase: true));
        Assert.Equal("long.mdl", Serves(mod, "Cut", "Long"));
        Assert.Equal("short.mdl", Serves(mod, "Cut", "Short"));
        Assert.False(Directory.Exists(Path.Combine(mod.Root, EarlessHairPlan.Folder)));
        Assert.False(EarlessHairRecord.Exists(mod.Root));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-4")]
    public void Combining_group_is_refused_and_nothing_written()
    {
        var combining = "{\"Name\":\"Mix\",\"Priority\":0,\"Type\":\"Combining\",\"DefaultSettings\":0,"
                      + "\"Options\":[{\"Name\":\"a\"}],\"Containers\":[{\"Files\":{\"" + Worn + "\":\"long.mdl\"}}]}";
        using var mod = new Mod("[" + combining + "]");
        mod.Put("long.mdl", EaredHairModel(801));
        var before = Snapshot(mod);
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Contains("Mix", plan.Refusal);
        Assert.Empty(plan.Writes);
        Assert.Equal(before, Snapshot(mod));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-5")]
    public void Legacy_folder_is_refused_and_not_migrated()
    {
        using var mod = new Mod(defaults: new Dictionary<string, string> { [Worn] = "long.mdl" }, fileVersion: 3);
        mod.Put("long.mdl", EaredHairModel(801));
        var before = Snapshot(mod);
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.NotEqual("", plan.Refusal);
        Assert.Empty(plan.Writes);
        Assert.False(EarlessHairWriter.Restore(mod.Root).Ok);
        Assert.Equal(before, Snapshot(mod));
    }

    [Theory]
    [Trait("Plan", "EDGE-earless-miqote-hair-6")]
    [InlineData("manifest")]
    [InlineData("record")]
    public void Unreadable_manifest_or_record_changes_nothing(string which)
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        if (which == "manifest") File.WriteAllText(Path.Combine(mod.Root, "meta.json"), "{ not json");
        else File.WriteAllText(Path.Combine(mod.Root, "Proteus", EarlessHairRecord.RecordFile), "{ not json");
        var before = Snapshot(mod);
        Assert.True(EarlessHairBuilder.Build(mod.Root, Worn).Unreadable);
        var restored = EarlessHairWriter.Restore(mod.Root);
        Assert.True(restored.Unreadable);
        Assert.Equal(before, Snapshot(mod));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-7")]
    public void Edited_copy_blocks_remake_and_restore()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        mod.Put($"Proteus/{MeshVolumeService.BackupSubdir}/{Copy}", [1, 2, 3]);
        mod.Put("long.mdl", EaredHairModel(801, radius: 0.103f));   // the author updated: a remake is due
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Empty(plan.Writes);
        Assert.NotEqual("", plan.Refusal);
        var restored = EarlessHairWriter.Restore(mod.Root);
        Assert.Equal(0, restored.Count);
        Assert.NotEqual("", restored.Message);
        Assert.Equal(Copy, Serves(mod, "Cut", "Long"));
        Assert.True(mod.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-8")]
    public void Hat_fit_on_the_copy_is_forgotten_on_restore()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        mod.Put($"Proteus/{HatCompatService.BackupSubdir}/{Copy}", mod.Get(Copy));
        mod.Put($"Proteus/{HatCompatService.RecordFile}", System.Text.Encoding.UTF8.GetBytes("{\"Files\":[\"" + Copy + "\"]}"));
        Assert.True(EarlessHairWriter.Restore(mod.Root).Ok);
        Assert.DoesNotContain(Copy, HatCompatService.ReadRecord(mod.Root)?.Files ?? [], StringComparer.OrdinalIgnoreCase);
        Assert.False(mod.Has($"Proteus/{HatCompatService.BackupSubdir}/{Copy}"));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-9")]
    public void Author_hat_backup_is_untouched()
    {
        using var mod = Cut();
        var backup = EaredHairModel(801, radius: 0.098f);
        mod.Put($"Proteus/{HatCompatService.BackupSubdir}/long.mdl", backup);
        mod.Put($"Proteus/{HatCompatService.RecordFile}", System.Text.Encoding.UTF8.GetBytes("{\"Files\":[\"long.mdl\"]}"));
        var author = mod.Get("long.mdl");
        Assert.True(Convert(mod).Ok);
        Assert.True(EarlessHairWriter.Restore(mod.Root).Ok);
        Assert.Equal(backup, mod.Get($"Proteus/{HatCompatService.BackupSubdir}/long.mdl"));
        Assert.Equal(author, mod.Get("long.mdl"));
        Assert.Contains("long.mdl", HatCompatService.ReadRecord(mod.Root)!.Files);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-10")]
    public void Repointed_redirect_is_never_overwritten()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        // The author's update points the option at a file of their own.
        mod.Put("other.mdl", EaredHairModel(801, radius: 0.102f));
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "Cut", "Long", new Dictionary<string, string> { [Worn] = "other.mdl" });
        Assert.Empty(EarlessHairRecord.Read(mod.Root).Swaps);
        var restored = EarlessHairWriter.Restore(mod.Root);
        Assert.Equal(0, restored.Count);
        Assert.Equal("other.mdl", Serves(mod, "Cut", "Long"));
        Assert.False(mod.Has(Copy));
        Assert.False(EarlessHairRecord.Exists(mod.Root));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-11")]
    public void Shared_author_file_shares_one_copy()
    {
        using var mod = new Mod("[" + Group("Cut", "Multi", ("A", new() { [Worn] = "long.mdl" }), ("B", new() { [Worn] = "long.mdl" })) + "]");
        mod.Put("long.mdl", EaredHairModel(801));
        Assert.True(Convert(mod).Ok);
        Assert.Equal(Copy, Serves(mod, "Cut", "A"));
        Assert.Equal(Copy, Serves(mod, "Cut", "B"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(mod.Root, EarlessHairPlan.Folder), "*", SearchOption.AllDirectories));

        // One pointed back by hand: the copy stays while the other still names it.
        PenumbraModMeta.SwapOptionRedirects(mod.Root, "Cut", "A",
            new Dictionary<string, (string, string)> { [Worn] = (Copy, "long.mdl") });
        Assert.Equal(0, EarlessHairWriter.Restore(mod.Root, HairPortPlan.HairPath(701, Id)).Count);   // a sweep only
        Assert.True(mod.Has(Copy));
        Assert.True(EarlessHairWriter.Restore(mod.Root).Ok);
        Assert.Equal("long.mdl", Serves(mod, "Cut", "B"));
        Assert.False(mod.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-13")]
    public void Failed_save_rolls_back()
    {
        using var mod = new Mod("[" + Group("Cut", "Single", ("Long", new() { [Worn] = "long.mdl" })) + ","
                                + Group("More", "Single", ("Short", new() { [Worn] = "short.mdl" })) + "]");
        mod.Put("long.mdl", EaredHairModel(801));
        mod.Put("short.mdl", EaredHairModel(801, radius: 0.104f));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        EarlessHairWriter.BeforeSwap = n => { if (n == 1) throw new IOException("disk full"); };
        try
        {
            var saved = EarlessHairWriter.Save(mod.Root, plan.Writes);
            Assert.False(saved.Ok);
            Assert.Contains("disk full", saved.Message);
        }
        finally { EarlessHairWriter.BeforeSwap = null; }
        Assert.Equal("long.mdl", Serves(mod, "Cut", "Long"));
        Assert.Equal("short.mdl", Serves(mod, "More", "Short"));
        Assert.False(EarlessHairRecord.Exists(mod.Root));
        Assert.False(mod.Has(Copy));
        Assert.False(mod.Has("Proteus Earless/short.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-18")]
    public void Unreadable_author_model_is_refused()
    {
        using var mod = Cut();
        mod.Put("long.mdl", new byte[64]);
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Empty(plan.Writes);
        Assert.Contains("could not be read", plan.Refusal);
        Assert.False(mod.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-8")]
    [Trait("Plan", "EDGE-earless-miqote-hair-19")]
    public void Missing_copy_is_made_again()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        Assert.Equal(1, EarlessHairBuilder.Build(mod.Root, Worn).Current);
        File.Delete(Path.Combine(mod.Root, EarlessHairPlan.Folder, "long.mdl"));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Single(plan.Writes);
        Assert.True(plan.Writes[0].Remake);
        Assert.True(EarlessHairWriter.Save(mod.Root, plan.Writes).Ok);
        Assert.True(mod.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-8")]
    public void Author_change_remakes_the_copy()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        var newer = EaredHairModel(801, radius: 0.103f);
        mod.Put("long.mdl", newer);
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Single(plan.Writes);
        Assert.True(EarlessHairWriter.Save(mod.Root, plan.Writes).Ok);
        Assert.Equal(HairPortPlan.Sha8(newer), EarlessHairRecord.Read(mod.Root).Swaps.Single().AuthorSha8);
        Assert.Equal(EarlessHair.Make(newer, out _)!.Model, mod.Get(Copy));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-20")]
    public void Missing_author_file_keeps_the_swap_on_restore()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        File.Delete(Path.Combine(mod.Root, "long.mdl"));
        var restored = EarlessHairWriter.Restore(mod.Root);
        Assert.Equal(0, restored.Count);
        Assert.Contains("long.mdl", restored.Message);
        Assert.Equal(Copy, Serves(mod, "Cut", "Long"));
        Assert.True(mod.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-26")]
    public void Lost_record_with_setting_on_derives_the_author_file_and_remakes()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        File.Delete(Path.Combine(mod.Root, "Proteus", EarlessHairRecord.RecordFile));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Single(plan.Writes);
        Assert.Equal("long.mdl", plan.Writes[0].AuthorFile);
        Assert.True(plan.Writes[0].Remake);
        Assert.True(EarlessHairWriter.Save(mod.Root, plan.Writes).Ok);
        var entry = EarlessHairRecord.Read(mod.Root).Swaps.Single();
        Assert.Equal("long.mdl", entry.AuthorFile);
        Assert.Equal(Copy, entry.File);
        Assert.Equal(1, EarlessHairBuilder.Build(mod.Root, Worn).Current);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-27")]
    public void Lost_record_with_setting_off_restores_to_the_derived_author_file()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        File.Delete(Path.Combine(mod.Root, "Proteus", EarlessHairRecord.RecordFile));
        var restored = EarlessHairWriter.Restore(mod.Root, Worn);
        Assert.True(restored.Ok, restored.Message);
        Assert.Equal("long.mdl", Serves(mod, "Cut", "Long"));
        Assert.False(mod.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-28")]
    public void Unnamed_files_in_the_earless_folder_go_and_files_outside_never_do()
    {
        using var mod = Cut();
        mod.Put("Proteus Earless/stray.mdl", [9]);
        mod.Put("Earless Hair/authors.mdl", [8]);
        Assert.True(Convert(mod).Ok);
        Assert.False(mod.Has("Proteus Earless/stray.mdl"));
        Assert.True(mod.Has("Earless Hair/authors.mdl"));
        Assert.True(mod.Has(Copy));

        // Nothing is deleted while the manifest cannot be read.
        mod.Put("Proteus Earless/stray.mdl", [9]);
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"), "{ not json");
        EarlessHairWriter.Restore(mod.Root);
        Assert.True(mod.Has("Proteus Earless/stray.mdl"));
        Assert.True(mod.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-29")]
    public void Lost_record_with_missing_author_file_is_refused()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        File.Delete(Path.Combine(mod.Root, "Proteus", EarlessHairRecord.RecordFile));
        File.Delete(Path.Combine(mod.Root, "long.mdl"));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Empty(plan.Writes);
        Assert.Contains("long.mdl", plan.Refusal);
        Assert.Equal(Copy, Serves(mod, "Cut", "Long"));
    }
}
