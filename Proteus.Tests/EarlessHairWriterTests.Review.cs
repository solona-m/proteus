using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>The writer's refusals, rollbacks and recoveries after the code review: each branch that writes into a
/// user's mod has a test that would fail if it broke.</summary>
public partial class EarlessHairWriterTests
{
    private static void PlantHatFit(Mod mod, string rel)
    {
        mod.Put($"Proteus/{HatCompatService.BackupSubdir}/{rel}", mod.Get(rel));
        mod.Put($"Proteus/{HatCompatService.RecordFile}", System.Text.Encoding.UTF8.GetBytes("{\"Files\":[\"" + rel + "\"]}"));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-8")]
    public void Hat_fit_on_the_copy_is_forgotten_on_remake()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        PlantHatFit(mod, Copy);
        mod.Put("long.mdl", EaredHairModel(801, radius: 0.103f));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.True(EarlessHairWriter.Save(mod.Root, plan.Writes).Ok);
        Assert.DoesNotContain(Copy, HatCompatService.ReadRecord(mod.Root)?.Files ?? [], StringComparer.OrdinalIgnoreCase);
        Assert.False(mod.Has($"Proteus/{HatCompatService.BackupSubdir}/{Copy}"));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-8")]
    public void A_hat_fit_that_cannot_be_forgotten_aborts_the_remake_and_keeps_the_copy()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        PlantHatFit(mod, Copy);
        var copyBefore = mod.Get(Copy);
        var shaBefore = EarlessHairRecord.Read(mod.Root).Swaps.Single().AuthorSha8;
        mod.Put("long.mdl", EaredHairModel(801, radius: 0.103f));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        var backup = Path.Combine(mod.Root, "Proteus", HatCompatService.BackupSubdir, "Proteus Earless", "long.mdl");
        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var saved = EarlessHairWriter.Save(mod.Root, plan.Writes);
            Assert.False(saved.Ok);
            // The restore that runs meanwhile cannot forget it either: the copy it would delete stays.
            PenumbraModMeta.SwapOptionRedirects(mod.Root, "Cut", "Long",
                new Dictionary<string, (string, string)> { [Worn] = (Copy, "long.mdl") });
            EarlessHairWriter.Restore(mod.Root);
            Assert.True(mod.Has(Copy));
        }
        Assert.Equal(copyBefore, mod.Get(Copy));
        Assert.Contains(Copy, HatCompatService.ReadRecord(mod.Root)!.Files);
        Assert.NotEqual("", shaBefore);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-10")]
    public void Save_refuses_a_manifest_changed_since_the_plan_and_an_option_gone()
    {
        using var mod = Cut();
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        mod.Put("other.mdl", EaredHairModel(801, radius: 0.102f));
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "Cut", "Long", new Dictionary<string, string> { [Worn] = "other.mdl" });
        var changed = EarlessHairWriter.Save(mod.Root, plan.Writes);
        Assert.False(changed.Ok);
        Assert.Equal("other.mdl", Serves(mod, "Cut", "Long"));
        Assert.False(mod.Has(Copy));

        using var gone = new Mod("[" + Group("Cut", "Single", ("Short", new() { [Worn] = "long.mdl" })) + "]");
        gone.Put("long.mdl", EaredHairModel(801));
        var noOption = EarlessHairWriter.Save(gone.Root, plan.Writes);
        Assert.False(noOption.Ok);
        Assert.Contains("Long", noOption.Message);
        Assert.False(gone.Has(Copy));
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-8")]
    public void Author_who_took_the_ears_out_gets_their_own_file_back()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        mod.Put("long.mdl", EarlessHairModel(801));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        var write = Assert.Single(plan.Writes);
        Assert.Equal("long.mdl", write.To);
        Assert.True(EarlessHairWriter.Save(mod.Root, plan.Writes).Ok);
        Assert.Equal("long.mdl", Serves(mod, "Cut", "Long"));
        Assert.False(mod.Has(Copy));
        Assert.False(EarlessHairRecord.Exists(mod.Root));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-13")]
    public void A_place_that_cannot_be_swapped_back_keeps_its_copy()
    {
        using var mod = new Mod("[" + Group("Cut", "Single", ("Long", new() { [Worn] = "long.mdl" })) + ","
                                + Group("More", "Single", ("Short", new() { [Worn] = "short.mdl" })) + "]");
        mod.Put("long.mdl", EaredHairModel(801));
        mod.Put("short.mdl", EaredHairModel(801, radius: 0.104f));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        // The first place is swapped; before the second, its group goes (so it cannot be swapped back) and the save fails.
        EarlessHairWriter.BeforeSwap = n =>
        {
            if (n != 1) return;
            var meta = Path.Combine(mod.Root, "meta.json");
            File.WriteAllText(meta, "{\"FileVersion\":4,\"Name\":\"Hair\",\"Groups\":[" + Group("More", "Single",
                ("Short", new() { [Worn] = "short.mdl" })) + "],\"DefaultData\":{\"Files\":{},\"FileSwaps\":{},\"Manipulations\":[]}}");
            throw new IOException("disk full");
        };
        try { Assert.False(EarlessHairWriter.Save(mod.Root, plan.Writes).Ok); }
        finally { EarlessHairWriter.BeforeSwap = null; }
        // The copy the lost place may still name stays; the other is gone.
        Assert.True(mod.Has(Copy));
        Assert.False(mod.Has("Proteus Earless/short.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-13")]
    [Trait("Plan", "EDGE-earless-miqote-hair-26")]
    public void A_record_that_cannot_be_written_after_the_swaps_still_reports_them()
    {
        using var mod = Cut();
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        EarlessHairWriter.AfterSwaps = () => throw new IOException("locked");
        EarlessHairWriter.Outcome saved, restored;
        try
        {
            saved = EarlessHairWriter.Save(mod.Root, plan.Writes);
            Assert.Equal(Copy, Serves(mod, "Cut", "Long"));
            restored = EarlessHairWriter.Restore(mod.Root);
        }
        finally { EarlessHairWriter.AfterSwaps = null; }
        // Saved and restored as asked — counted, so the caller reloads — with why the record lags as a warning only.
        Assert.True(saved.Ok);
        Assert.Equal("", saved.Message);
        Assert.Contains("locked", saved.Warning);
        Assert.True(restored.Ok);
        Assert.Equal(1, restored.Count);
        Assert.Equal("", restored.Message);
        Assert.Contains("locked", restored.Warning);
        Assert.Equal("long.mdl", Serves(mod, "Cut", "Long"));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-11")]
    public void A_second_place_joins_a_current_copy_without_writing_it_again()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        PlantHatFit(mod, Copy);
        // The author adds an option naming the same file.
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "", "", new Dictionary<string, string> { [Worn] = "long.mdl" });
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        var write = Assert.Single(plan.Writes);
        Assert.Empty(write.Model);
        Assert.True(EarlessHairWriter.Save(mod.Root, plan.Writes).Ok);
        Assert.Equal(Copy, HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Worn));
        Assert.Contains(Copy, HatCompatService.ReadRecord(mod.Root)!.Files);
        Assert.Equal(2, EarlessHairRecord.Read(mod.Root).Swaps.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-7")]
    [Trait("Plan", "EDGE-earless-miqote-hair-11")]
    public void An_edited_copy_is_not_spread_to_another_place()
    {
        using var mod = Cut();
        Assert.True(Convert(mod).Ok);
        mod.Put($"Proteus/{MeshVolumeService.BackupSubdir}/{Copy}", [1, 2, 3]);
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "", "", new Dictionary<string, string> { [Worn] = "long.mdl" });
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Empty(plan.Writes);
        Assert.NotEqual("", plan.Refusal);
        Assert.Equal("long.mdl", HairPortPlan.FileFor(mod.Owners.First(o => o.IsDefault), Worn));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-5")]
    public void A_path_climbing_out_of_the_mod_is_never_copied()
    {
        using var mod = new Mod("[" + Group("Cut", "Single", ("Long", new() { [Worn] = "../outside.mdl" })) + "]");
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(mod.Root)!, "outside.mdl"), EaredHairModel(801));
        try
        {
            var plan = EarlessHairBuilder.Build(mod.Root, Worn);
            Assert.Empty(plan.Writes);
            Assert.False(Directory.Exists(Path.Combine(mod.Root, EarlessHairPlan.Folder)));
            Assert.False(EarlessHairPlan.StaysInside("../x.mdl"));
            Assert.False(EarlessHairPlan.StaysInside(@"a\..\..\x.mdl"));
            Assert.False(EarlessHairPlan.StaysInside("C:/x.mdl"));
            Assert.True(EarlessHairPlan.StaysInside("hair/a..b.mdl"));
            // A copy path naming one is no way back either.
            Assert.Null(EarlessHairPlan.AuthorFileFor(new EarlessHairRecord(), mod.Owner("Cut", "Long"), Worn,
                                                      "Proteus Earless/../../outside.mdl", _ => true));
        }
        finally { File.Delete(Path.Combine(Path.GetDirectoryName(mod.Root)!, "outside.mdl")); }
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-3")]
    public void An_option_naming_a_file_it_does_not_ship_is_skipped()
    {
        using var mod = Cut(extraOption: "Short");
        File.Delete(Path.Combine(mod.Root, "short.mdl"));
        var plan = EarlessHairBuilder.Build(mod.Root, Worn);
        Assert.Equal("", plan.Refusal);
        Assert.Equal(["Long"], plan.Writes.Select(w => w.Into.Option));
    }
}
