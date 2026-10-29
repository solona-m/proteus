using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Writing a retarget into somebody else's mod. Over a real temp folder rather than a mock, because the thing being
/// tested is the shape of the json Penumbra parses.
/// </summary>
public class BodyRetargetWriterTests : IDisposable
{
    private const string GamePath = "chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string Group = "Body — c0201e6255_top";

    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-retarget-" + Guid.NewGuid().ToString("N"));

    public BodyRetargetWriterTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile),
                          PenumbraModMeta.NewMetaJson("Test Outfit", "Someone", ""));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Plan", "EDGE-mod-rename-33")]
    public void Save_refuses_when_mod_folder_is_gone()
    {
        // Renamed in Penumbra while the refit ran: writing would bring the old folder back as a ghost.
        string gone = root + "-renamed-away";

        var outcome = BodyRetargetWriter.Save(gone, Group, "Neolithe SFW L", GamePath, [1, 2, 3],
                                              "Neolithe", "SFW M", "SFW L");

        Assert.False(outcome.Ok);
        Assert.Contains(Path.GetFileName(gone), outcome.Message);
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public void Saving_adds_a_group_with_an_original_option_that_carries_nothing()
    {
        var outcome = BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1, 2, 3],
                                              "Neolithe", "SFW M", "SFW L");
        Assert.True(outcome.Ok, outcome.Message);

        var options = PenumbraModMeta.TryReadFileOptions(root, Group);
        Assert.NotNull(options);
        Assert.Equal(2, options!.Count);

        // "Original" must carry no files at all, or selecting it would freeze a copy of the author's model rather
        // than falling through to whatever the mod already does for the path.
        Assert.Equal(BodyRetargetWriter.OriginalOption, options[0].Name);
        Assert.Empty(options[0].Files);

        Assert.Equal("Neolithe SFW L", options[1].Name);
        Assert.Equal(new[] { GamePath }, options[1].Files.Keys);

        string rel = options[1].Files[GamePath];
        Assert.True(File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))),
                    $"the option points at {rel}, which is not there");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void A_refit_never_writes_over_the_file_a_renamed_option_points_at()
    {
        // The player's own sequence: save a refit, rename the option in Penumbra, then save another refit whose
        // option has the name the first one had. The file is named after the option, so the second save landed on the
        // first's file and both options drew the model saved last.
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        string first = PenumbraModMeta.TryReadFileOptions(root, Group)!
            .Single(o => o.Name == "Neolithe SFW L").Files[GamePath];

        Rename(Group, "Neolithe SFW L", "My top, large");

        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [2], "Neolithe", "SFW M", "SFW L");

        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        string renamed = options.Single(o => o.Name == "My top, large").Files[GamePath];
        string fresh = options.Single(o => o.Name == "Neolithe SFW L").Files[GamePath];

        Assert.Equal(first, renamed);                       // the rename left the file where it was
        Assert.NotEqual(renamed, fresh);                    // and the new option got one of its own
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(root, renamed.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(root, fresh.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Saving_the_same_option_again_still_reuses_its_file()
    {
        // The other half of the rule above: an option of a name we are saving is ours to replace, path and all, or
        // every re-save of one size would leave another folder behind.
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        string first = PenumbraModMeta.TryReadFileOptions(root, Group)!
            .Single(o => o.Name == "Neolithe SFW L").Files[GamePath];

        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [9], "Neolithe", "SFW M", "SFW L");

        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Equal(first, options.Single(o => o.Name == "Neolithe SFW L").Files[GamePath]);
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(root, first.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void An_option_s_files_stay_in_one_folder_even_when_its_own_name_is_taken()
    {
        // The folder a refit writes to is named after its option, and after the rename above that name belongs to
        // another option's folder, so this one takes the next free name. A second slot saved into it has to land in
        // that same folder: one folder per option, whatever it ended up called.
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        Rename(Group, "Neolithe SFW L", "My top, large");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [2], "Neolithe", "SFW M", "SFW L");

        const string legs = "chara/equipment/e6255/model/c0201e6255_dwn.mdl";
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", legs, [3], "Neolithe", "SFW M", "SFW L");

        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!
            .Single(o => o.Name == "Neolithe SFW L").Files;
        Assert.Equal(2, files.Count);
        var folders = files.Values.Select(at => at.Split('/')[1]).Distinct().ToList();
        Assert.Single(folders);
        Assert.Equal("Neolithe SFW L 2", folders[0]);

        // And the renamed option still has the first folder, and the first model.
        string renamed = PenumbraModMeta.TryReadFileOptions(root, Group)!
            .Single(o => o.Name == "My top, large").Files[GamePath];
        Assert.Equal("Neolithe SFW L", renamed.Split('/')[1]);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(root, renamed.Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>Rename one option in the manifest, as the player does in Penumbra's Edit Mod.</summary>
    private void Rename(string group, string from, string to)
    {
        string meta = Path.Combine(root, "meta.json");
        string text = File.ReadAllText(meta);
        text = text.Replace($"\"Name\": \"{from}\"", $"\"Name\": \"{to}\"");
        File.WriteAllText(meta, text);
    }

    [Fact]
    public void A_second_size_grows_the_group_rather_than_replacing_it()
    {
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW XS", GamePath, [2], "Neolithe", "SFW M", "SFW XS");

        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Equal([BodyRetargetWriter.OriginalOption, "Neolithe SFW L", "Neolithe SFW XS"],
                     options.Select(o => o.Name));
    }

    [Fact]
    public void Several_sizes_save_in_one_go_each_with_its_own_file()
    {
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        var outcome = BodyRetargetWriter.Save(root, Group, GamePath, "Neolithe", "SFW M",
        [
            new BodyRetargetWriter.Refit("Neolithe SFW XS", [2], "SFW XS"),
            new BodyRetargetWriter.Refit("Neolithe SFW S", [3], "SFW S"),
            new BodyRetargetWriter.Refit("Neolithe SFW L", [4], "SFW L"),
        ]);
        Assert.True(outcome.Ok, outcome.Message);

        // The earlier L is replaced where it stands, not kept alongside it: Penumbra keeps a group's choice by option
        // position, so moving it would switch what a player wears. The new sizes follow in the batch's order.
        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Equal([BodyRetargetWriter.OriginalOption, "Neolithe SFW L", "Neolithe SFW XS", "Neolithe SFW S"],
                     options.Select(o => o.Name));
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(root, options[1].Files[GamePath])));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(root, options[2].Files[GamePath])));
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(root, options[3].Files[GamePath])));

        var record = BodyRetargetWriter.ReadRecord(root)!;
        Assert.Equal(["SFW XS", "SFW S", "SFW L"], record.Options.Select(e => e.To));
    }

    [Fact]
    public void Saving_the_same_size_twice_replaces_that_option_only()
    {
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW XS", GamePath, [2], "Neolithe", "SFW M", "SFW XS");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [9], "Neolithe", "SFW M", "SFW L");

        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Equal(3, options.Count);

        var again = options.Single(o => o.Name == "Neolithe SFW L");
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(root, again.Files[GamePath])));
    }

    [Fact]
    public void A_second_slot_joins_the_option_it_belongs_to()
    {
        const string legs = "chara/equipment/e6255/model/c0201e6255_dwn.mdl";
        BodyRetargetWriter.Save(root, Group, "Neolithe L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        BodyRetargetWriter.Save(root, Group, "Neolithe L", legs, [2], "Neolithe", "SFW Medium", "SFW Large");

        var option = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Neolithe L");
        Assert.Equal(2, option.Files.Count);
        Assert.Contains(GamePath, option.Files.Keys);
        Assert.Contains(legs, option.Files.Keys);
    }

    [Fact]
    public void The_group_outranks_the_author_s_own()
    {
        // The author already replaces this model from a group of their own, at priority 7.
        PenumbraModMeta.WriteFileOptionGroup(root, 0, "Body", 7,
            [new PenumbraModMeta.FileOption("Bibo+", new Dictionary<string, string> { [GamePath] = "bibo/top.mdl" })],
            0);

        var clashes = BodyRetargetWriter.ClashingGroups(root, GamePath, Group);
        Assert.Equal(["Body"], clashes);

        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");

        Assert.True(PriorityOf(Group) > PriorityOf("Body"),
                    "the retarget group must win the path the author's group also claims");
    }

    [Fact]
    public void The_author_s_groups_keep_their_positions()
    {
        // Penumbra carries settings across a reload by group POSITION. A group inserted in front shifts every author
        // group down one and each inherits its neighbour's selection — on "Seaside" that unticked both garments and
        // left the character naked. So the author's groups must sit exactly where they were, and ours after them.
        WriteAuthorGroups("Items", "Top Size", "Bottom Size");

        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");

        Assert.Equal(["Items", "Top Size", "Bottom Size", Group], GroupNames());
    }

    [Fact]
    public void Saving_again_and_undoing_leave_every_position_alone()
    {
        WriteAuthorGroups("Items", "Top Size");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");

        // The author (or Penumbra) adds a group after ours; saving another size must not pull ours past it.
        WriteAuthorGroups("Items", "Top Size", Group, "Later");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW XS", GamePath, [2], "Neolithe", "SFW M", "SFW XS");
        Assert.Equal(["Items", "Top Size", Group, "Later"], GroupNames());

        BodyRetargetWriter.Undo(root, Group, "Neolithe SFW XS");
        Assert.Equal(["Items", "Top Size", Group, "Later"], GroupNames());
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-area-coverage-8")]
    public void An_option_saved_again_keeps_its_place() // [EDGE-cross-sex-area-coverage-8]
    {
        // Penumbra keeps a group's choice by option POSITION. The men's legs saved into "TBSE" after the top had made
        // TBSE and TBSE-X moved TBSE to the end, and a player wearing TBSE was switched to TBSE-X.
        const string legs = "chara/equipment/e6255/model/c0101e6255_dwn.mdl";
        BodyRetargetWriter.Save(root, Group, "TBSE", GamePath, [1], "TBSE", "Bibo M", "SFW");
        BodyRetargetWriter.Save(root, Group, "TBSE-X", GamePath, [2], "TBSE-X", "Bibo M", "SFW");
        Assert.True(BodyRetargetWriter.Save(root, Group, "TBSE", legs, [3], "TBSE", "Bibo M", "Gen A").Ok);

        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Equal([BodyRetargetWriter.OriginalOption, "TBSE", "TBSE-X"], options.Select(o => o.Name));
        Assert.Contains(GamePath, options[1].Files.Keys);
        Assert.Contains(legs, options[1].Files.Keys);
        Assert.Equal("TBSE", PenumbraModMeta.SingleDefault(root, Group));
    }

    [Fact]
    public void Undo_removes_the_option_its_file_and_finally_the_group()
    {
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW XS", GamePath, [2], "Neolithe", "SFW M", "SFW XS");

        string rel = PenumbraModMeta.TryReadFileOptions(root, Group)!
                                    .Single(o => o.Name == "Neolithe SFW L").Files[GamePath];

        Assert.True(BodyRetargetWriter.Undo(root, Group, "Neolithe SFW L").Ok);
        Assert.False(File.Exists(Path.Combine(root, rel)));
        Assert.Equal([BodyRetargetWriter.OriginalOption, "Neolithe SFW XS"],
                     PenumbraModMeta.TryReadFileOptions(root, Group)!.Select(o => o.Name));

        Assert.True(BodyRetargetWriter.Undo(root, Group, "Neolithe SFW XS").Ok);
        Assert.Null(PenumbraModMeta.TryReadFileOptions(root, Group));
        Assert.False(Directory.Exists(Path.Combine(root, BodyRetargetWriter.Subfolder)));
        Assert.Null(BodyRetargetWriter.ReadRecord(root));
    }

    [Fact]
    public void Undo_leaves_the_author_s_own_files_untouched()
    {
        // The whole point of writing a new file rather than editing theirs.
        string theirs = Path.Combine(root, "their model.mdl");
        File.WriteAllBytes(theirs, [7, 7, 7]);

        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        BodyRetargetWriter.Undo(root, Group, "Neolithe SFW L");

        Assert.Equal([7, 7, 7], File.ReadAllBytes(theirs));
    }

    [Fact]
    public void The_record_says_what_each_option_was_made_from()
    {
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");

        var record = BodyRetargetWriter.ReadRecord(root);
        Assert.NotNull(record);
        var entry = Assert.Single(record!.Options);
        Assert.Equal("Neolithe SFW L", entry.Name);
        Assert.Equal("Neolithe", entry.BodyMod);
        Assert.Equal("SFW M", entry.From);
        Assert.Equal("SFW L", entry.To);
        Assert.Equal(GamePath, entry.GamePath);
    }

    [Fact]
    public void A_legacy_folder_is_refused_with_the_way_out()
    {
        string legacy = Path.Combine(Path.GetTempPath(), "proteus-retarget-v3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, PenumbraModMeta.MetaFile),
                          """{"FileVersion": 3, "Name": "Old"}""");
        try
        {
            var outcome = BodyRetargetWriter.Save(legacy, Group, "X", GamePath, [1], "Neolithe", "a", "b");
            Assert.False(outcome.Ok);
            Assert.Contains("Enable it in Penumbra once", outcome.Message);
        }
        finally
        {
            try { Directory.Delete(legacy, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void An_option_name_that_is_not_a_folder_name_still_writes()
    {
        var outcome = BodyRetargetWriter.Save(root, Group, "Neolithe: SFW/L?", GamePath, [1], "Neolithe", "a", "b");
        Assert.True(outcome.Ok, outcome.Message);

        var option = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Neolithe: SFW/L?");
        Assert.True(File.Exists(Path.Combine(root, option.Files[GamePath].Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void A_refit_s_folder_is_plain_ascii_whatever_its_option_is_called()
    {
        // The Studio's live tools match the game's name for a drawn file against the path on disk, and the game hands
        // non-ASCII back in another encoding: a file under "— ·" was worn and never recognised.
        string folder = BodyRetargetWriter.Sanitise("[HS] Rue+ — None · Yiggle - Small");
        Assert.Equal("[HS] Rue+ - None - Yiggle - Small", folder);
        Assert.All(folder, c => Assert.InRange(c, ' ', '~'));
        Assert.Equal("caf_", BodyRetargetWriter.Sanitise("café"));
    }

    [Fact]
    public void A_worn_file_under_a_non_ascii_folder_is_still_recognised()
    {
        const string onDisk = "e:/penumbradt/seaside/body retarget/[hs] rue+ — none · yiggle - small/c0201e0194_top.mdl";
        // What the game hands back when it reads those UTF-8 bytes as one byte per character.
        string asDrawn = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes(onDisk));

        Assert.True(Proteus.Gui.LiveBrush.SameFile(asDrawn, onDisk));
        Assert.True(Proteus.Gui.LiveBrush.SameFile(onDisk, onDisk));
        Assert.False(Proteus.Gui.LiveBrush.SameFile(onDisk.Replace("small", "medium"), onDisk));
    }

    // ── saving into the author's own size group ─────────────────────────────────────────────────────────

    private const string TopSize = "Top Size";

    /// <summary>
    /// An author's size group as authors write them: a description, a priority, the second size selected, and options
    /// carrying swaps and manipulations of their own — everything a rewrite could lose. Plus a group after it, whose
    /// position must not move.
    /// </summary>
    private void WriteAuthorSizeGroup(string type = "Single", long defaultSettings = 1)
    {
        string json = $$"""
        {
          "FileVersion": 4,
          "Name": "Test Outfit",
          "Groups": [
            {
              "Version": 0, "Name": "{{TopSize}}", "Description": "Pick your body.", "Image": "", "Page": 0,
              "Priority": 3, "Type": "{{type}}", "DefaultSettings": {{defaultSettings}},
              "Options": [
                { "Name": "Rue Med", "Description": "medium", "Files": { "{{GamePath}}": "top size/rue med/top.mdl" },
                  "FileSwaps": { "a/b.tex": "c/d.tex" }, "Manipulations": [ { "Type": "Imc" } ] },
                { "Name": "Rue Large", "Description": "", "Files": { "{{GamePath}}": "top size/rue large/top.mdl" },
                  "FileSwaps": {}, "Manipulations": [] }
              ]
            },
            { "Version": 0, "Name": "Items", "Priority": 0, "Type": "Multi", "DefaultSettings": 3, "Options": [] }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), json);
    }

    [Fact]
    public void A_size_saved_into_the_author_s_group_is_one_more_option_and_nothing_else_changes()
    {
        WriteAuthorSizeGroup();
        var before = Read(TopSize);

        Assert.True(BodyRetargetWriter.IsAuthorGroup(root, TopSize));
        var outcome = BodyRetargetWriter.Save(root, TopSize, GamePath, "Rue+", "Yiggle - Medium",
        [
            new BodyRetargetWriter.Refit("Rue Yiggle Small", [5], "Yiggle - Small"),
            new BodyRetargetWriter.Refit("Rue Yiggle Large", [6], "Yiggle - Large"),
        ]);
        Assert.True(outcome.Ok, outcome.Message);

        // Appended, not inserted, and no Original option: the group is the author's, and it keeps its own default.
        Assert.Equal([TopSize, "Items"], GroupNames());
        var after = Read(TopSize);
        var options = after.GetProperty("Options").EnumerateArray().ToList();
        Assert.Equal(["Rue Med", "Rue Large", "Rue Yiggle Small", "Rue Yiggle Large"],
                     options.Select(o => o.GetProperty("Name").GetString()));
        foreach (string field in new[] { "Description", "Priority", "Type", "DefaultSettings" })
            Assert.True(Same(before.GetProperty(field), after.GetProperty(field)), field);
        Assert.True(Same(before.GetProperty("Options")[0], options[0]), "the author's option changed");

        var saved = PenumbraModMeta.TryReadFileOptions(root, TopSize)!.Single(o => o.Name == "Rue Yiggle Small");
        Assert.Equal([5], File.ReadAllBytes(Path.Combine(root, saved.Files[GamePath])));
        Assert.All(BodyRetargetWriter.ReadRecord(root)!.Options, e => Assert.True(e.InAuthorGroup));
    }

    // ── the new body's smallclothes material, published beside the model (skinless-target) ──

    private const string Boxers = "mt_c0201e6255_top_z.mtrl";

    private static byte[] ModelDrawing(string material)
        => SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl", new SyntheticModel.Sub(0)),
                                new SyntheticModel.Mesh("/" + material, new SyntheticModel.Sub(0)));

    private static BodyRetargetWriter.Refit WithBoxers(string option, byte[] material)
        => new(option, ModelDrawing(Boxers), "Muse SFW",
               Materials: [new BodyRetarget.PublishedMaterial(Boxers, material, "/mt_c0201e0000_dwn_b.mtrl")]);

    private string FileOf(string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    [Trait("Plan", "AC-skinless-target-8")]
    [Trait("Plan", "EDGE-skinless-target-25")]
    public void Save_publishes_the_material_beside_the_model_and_undo_removes_it()
    {
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [WithBoxers("Muse SFW", [4, 2])]).Ok);

        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        // The mod publishes no material of its own set: v0001 alone.
        string at = files["chara/equipment/e6255/material/v0001/" + Boxers];
        Assert.StartsWith(BodyRetargetWriter.Subfolder + "/", at, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([4, 2], File.ReadAllBytes(FileOf(at)));

        Assert.True(BodyRetargetWriter.Undo(root, Group, "Muse SFW").Ok);
        Assert.False(File.Exists(FileOf(at)));
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-25")]
    public void The_material_goes_in_every_variant_folder_the_mod_dresses()
    {
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
        {
          "FileVersion": 4, "Name": "Test Outfit",
          "DefaultData": { "Files": { "chara/equipment/e6255/material/v0001/mt_c0201e6255_top_a.mtrl": "a1.mtrl",
                                      "chara/equipment/e6255/material/v0003/mt_c0201e6255_top_a.mtrl": "a3.mtrl" } },
          "Groups": []
        }
        """);
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [WithBoxers("Muse SFW", [4, 2])]).Ok);

        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        string v1 = files["chara/equipment/e6255/material/v0001/" + Boxers];
        Assert.Equal(v1, files["chara/equipment/e6255/material/v0003/" + Boxers]);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-30")]
    public void A_name_another_option_publishes_moves_to_the_next_letter_and_renames_the_model()
    {
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
        {
          "FileVersion": 4, "Name": "Test Outfit",
          "DefaultData": { "Files": { "chara/equipment/e6255/material/v0001/{{Boxers}}": "theirs.mtrl" } },
          "Groups": []
        }
        """);
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [WithBoxers("Muse SFW", [4, 2])]).Ok);

        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        const string next = "mt_c0201e6255_top_y.mtrl";
        Assert.Equal([4, 2], File.ReadAllBytes(FileOf(files["chara/equipment/e6255/material/v0001/" + next])));
        Assert.False(files.ContainsKey("chara/equipment/e6255/material/v0001/" + Boxers));
        var model = File.ReadAllBytes(FileOf(files[GamePath]));
        Assert.Contains("/" + next, SecondSkinWriter.Parse(model).MatNames);
        Assert.DoesNotContain("/" + Boxers, SecondSkinWriter.Parse(model).MatNames);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-31")]
    public void Saving_again_onto_a_body_with_skin_drops_the_old_material_redirect()
    {
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [WithBoxers("Muse SFW", [4, 2])]).Ok);
        // The same option made again onto a body with skin there: nothing to publish.
        string copy = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW")
                                     .Files["chara/equipment/e6255/material/v0001/" + Boxers];
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M",
                                            [new BodyRetargetWriter.Refit("Muse SFW", [9], "Gen A")]).Ok);
        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        Assert.Equal([GamePath], files.Keys);
        // And its file goes with the redirect: nothing names it any more.
        Assert.False(File.Exists(FileOf(copy)));
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-31")]
    public void A_legs_model_drawing_the_top_s_material_keeps_its_copy_when_the_top_is_saved()
    {
        // Legs that share the outfit's top material: the copy is named for the LEGS model it is saved beside, so the top
        // saved into the same option afterwards does not take it for its own stale copy.
        var legs = new BodyRetargetWriter.Refit("Muse SFW", ModelDrawing(Boxers), "Muse SFW",
                                                Materials: [new BodyRetarget.PublishedMaterial(Boxers, [4, 2], "/b")]);
        Assert.True(BodyRetargetWriter.Save(root, Group, LegsPath, "Muse+", "SFW M", [legs]).Ok);
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M",
                                            [new BodyRetargetWriter.Refit("Muse SFW", [7], "Muse SFW")]).Ok);

        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        const string legsCopy = "mt_c0201e6255_dwn_z.mtrl";
        Assert.Equal([4, 2], File.ReadAllBytes(FileOf(files["chara/equipment/e6255/material/v0001/" + legsCopy])));
        Assert.Contains("/" + legsCopy, SecondSkinWriter.Parse(File.ReadAllBytes(FileOf(files[LegsPath]))).MatNames);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-30")]
    public void Two_copies_in_one_model_never_end_up_under_one_name()
    {
        // A model bringing in two of the new body's smallclothes (a bra and boxers), its first copy's name taken by
        // another option: that copy moves, and not onto the name the second still has.
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
        {
          "FileVersion": 4, "Name": "Test Outfit",
          "DefaultData": { "Files": { "chara/equipment/e6255/material/v0001/mt_c0201e6255_top_z.mtrl": "theirs.mtrl" } },
          "Groups": []
        }
        """);
        var model = SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl", new SyntheticModel.Sub(0)),
                                         new SyntheticModel.Mesh("/mt_c0201e6255_top_z.mtrl", new SyntheticModel.Sub(0)),
                                         new SyntheticModel.Mesh("/mt_c0201e6255_top_y.mtrl", new SyntheticModel.Sub(0)));
        var refit = new BodyRetargetWriter.Refit("Muse SFW", model, "Muse SFW",
            Materials: [new BodyRetarget.PublishedMaterial("mt_c0201e6255_top_z.mtrl", [1], "/bra"),
                        new BodyRetarget.PublishedMaterial("mt_c0201e6255_top_y.mtrl", [2], "/boxers")]);
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [refit]).Ok);

        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        var names = SecondSkinWriter.Parse(File.ReadAllBytes(FileOf(files[GamePath]))).MatNames;
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal([1], File.ReadAllBytes(FileOf(files["chara/equipment/e6255/material/v0001/mt_c0201e6255_top_x.mtrl"])));
        Assert.Equal([2], File.ReadAllBytes(FileOf(files["chara/equipment/e6255/material/v0001/mt_c0201e6255_top_y.mtrl"])));
        Assert.Contains("/mt_c0201e6255_top_x.mtrl", names);
        Assert.Contains("/mt_c0201e6255_top_y.mtrl", names);
    }

    [Fact]
    public void A_model_outside_an_equipment_set_cannot_save_a_material_and_says_so()
    {
        var outcome = BodyRetargetWriter.Save(root, Group, "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl",
                                              "Muse+", "SFW M", [WithBoxers("Muse SFW", [4, 2])]);
        Assert.False(outcome.Ok);
        Assert.Contains("underwear", outcome.Message);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-31")]
    public void Saving_again_keeps_one_copy()
    {
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [WithBoxers("Muse SFW", [4, 2])]).Ok);
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [WithBoxers("Muse SFW", [5, 5])]).Ok);
        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        var materials = files.Where(f => f.Key.EndsWith(".mtrl", StringComparison.Ordinal)).ToList();
        var only = Assert.Single(materials);
        Assert.EndsWith(Boxers, only.Key);
        Assert.Equal([5, 5], File.ReadAllBytes(FileOf(only.Value)));
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-31")]
    public void A_second_slot_saved_into_the_option_keeps_the_first_slot_s_material()
    {
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M", [WithBoxers("Muse SFW", [4, 2])]).Ok);
        Assert.True(BodyRetargetWriter.Save(root, Group, LegsPath, "Muse+", "SFW M",
                                            [new BodyRetargetWriter.Refit("Muse SFW", [7], "Muse SFW")]).Ok);
        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files;
        Assert.True(files.ContainsKey("chara/equipment/e6255/material/v0001/" + Boxers));
    }

    [Fact]
    [Trait("Plan", "AC-skinless-target-8")]
    public void Without_materials_the_manifest_is_as_before()
    {
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Muse+", "SFW M",
                                            [new BodyRetargetWriter.Refit("Muse SFW", [1], "Gen A")]).Ok);
        Assert.Equal([GamePath], PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Muse SFW").Files.Keys);
    }

    /// <summary>An author's size group whose two sizes each carry their own material and legs, and Small a texture only it
    /// has.</summary>
    private void WriteSizesWithMaterials(string type)
    {
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
        {
          "FileVersion": 4, "Name": "Test Outfit",
          "Groups": [
            { "Version": 0, "Name": "{{TopSize}}", "Priority": 0, "Type": "{{type}}", "DefaultSettings": 0, "Options": [
              { "Name": "Small", "Files": { "{{GamePath}}": "small/top.mdl", "{{MaterialPath}}": "small/top.mtrl",
                                            "{{TexturePath}}": "small/only.tex" } },
              { "Name": "Large", "Files": { "{{GamePath}}": "large/top.mdl", "{{MaterialPath}}": "large/top.mtrl",
                                            "{{LegsPath}}": "large/dwn.mdl" } } ] } ]
        }
        """);
    }

    private const string MaterialPath = "chara/equipment/e6255/material/v0001/mt_c0201e6255_top_a.mtrl";
    private const string TexturePath = "chara/equipment/e6255/texture/v01_c0201e6255_top_d.tex";
    private const string LegsPath = "chara/equipment/e6255/model/c0201e6255_dwn.mdl";

    [Theory]
    [InlineData("Single")]
    [InlineData("Multi")]
    [Trait("Plan", "AC-bulk-nearest-source-6")]
    [Trait("Plan", "EDGE-bulk-nearest-source-21")]
    [Trait("Plan", "EDGE-bulk-nearest-source-23")]
    public void Made_again_from_another_size_its_files_replace_the_old_ones(string type)
    {
        WriteSizesWithMaterials(type);
        Assert.True(BodyRetargetWriter.Save(root, TopSize, GamePath, "Rue+", "Small",
                                            [new BodyRetargetWriter.Refit("Rue L", [1], "Large")], fromOption: "Small").Ok);
        // The outfit's legs refitted into the same option afterwards: a refit of ours, never replaced.
        Assert.True(BodyRetargetWriter.Save(root, TopSize, LegsPath, "Rue+", "Small",
                                            [new BodyRetargetWriter.Refit("Rue L", [2], "WC")], fromOption: "Small").Ok);
        IReadOnlyDictionary<string, string> Files() => PenumbraModMeta.TryReadFileOptions(root, TopSize)!.Single(o => o.Name == "Rue L").Files;
        Assert.Equal("small/top.mtrl", Files()[MaterialPath]);
        // And a file the player added to the option by hand, at a path no size of the author's gives.
        const string handPath = "chara/equipment/e6255/texture/v01_c0201e6255_top_n.tex";
        var meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)))!;
        var rueL = meta["Groups"]!.AsArray().Single(g => (string?)g!["Name"] == TopSize)!["Options"]!.AsArray()
                                 .Single(o => (string?)o!["Name"] == "Rue L")!;
        rueL["Files"]![handPath] = "mine/normal.tex";
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), meta.ToJsonString());

        // Saved again from Large without the flag: what the option carried stays, as every other save keeps it.
        Assert.True(BodyRetargetWriter.Save(root, TopSize, GamePath, "Rue+", "Large",
                                            [new BodyRetargetWriter.Refit("Rue L", [3], "Large")], fromOption: "Large").Ok);
        Assert.Equal("small/top.mtrl", Files()[MaterialPath]);

        // With it, Large's material replaces Small's; the legs refit and this refit's own model are ours and stay ours,
        // though Large gives the legs a model of its own; and what the player added stays.
        string legs = Files()[LegsPath];
        Assert.StartsWith(BodyRetargetWriter.Subfolder + "/", legs, StringComparison.OrdinalIgnoreCase);
        Assert.True(BodyRetargetWriter.Save(root, TopSize, GamePath, "Rue+", "Large",
                                            [new BodyRetargetWriter.Refit("Rue L", [4], "Large")], fromOption: "Large",
                                            sourceFilesWin: true).Ok);
        var files = Files();
        Assert.Equal("large/top.mtrl", files[MaterialPath]);
        Assert.Equal(legs, files[LegsPath]);
        Assert.StartsWith(BodyRetargetWriter.Subfolder + "/", files[GamePath], StringComparison.OrdinalIgnoreCase);
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(root, files[GamePath])));
        // A path only Small gave stays carried: which option the size was cut from before is not recorded.
        Assert.Equal("small/only.tex", files[TexturePath]);
        Assert.Equal("mine/normal.tex", files[handPath]);
        // And the author's options are as they were.
        Assert.Equal("small/top.mtrl", PenumbraModMeta.TryReadFileOptions(root, TopSize)!.Single(o => o.Name == "Small").Files[MaterialPath]);
    }

    /// <summary>The same in a group of Proteus's own, which the writer rewrites whole rather than appending to.</summary>
    [Fact]
    [Trait("Plan", "AC-bulk-nearest-source-6")]
    [Trait("Plan", "EDGE-bulk-nearest-source-21")]
    public void Made_again_in_a_group_of_ours_its_source_s_files_win_too()
    {
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Rue+", "Small",
                                            [new BodyRetargetWriter.Refit("Rue L", [1], "Large")]).Ok);
        Assert.False(BodyRetargetWriter.IsAuthorGroup(root, Group));
        // An option of the group the next save is cut from, and what "Rue L" carried before.
        var meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)))!;
        var options = meta["Groups"]!.AsArray().Single(g => (string?)g!["Name"] == Group)!["Options"]!.AsArray();
        options.Single(o => (string?)o!["Name"] == "Rue L")!["Files"]![MaterialPath] = "old/top.mtrl";
        options.Add(System.Text.Json.Nodes.JsonNode.Parse($$"""{ "Name": "Source", "Files": { "{{MaterialPath}}": "source/top.mtrl" } }"""));
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), meta.ToJsonString());
        string Material() => PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Rue L").Files[MaterialPath];

        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Rue+", "Source",
                                            [new BodyRetargetWriter.Refit("Rue L", [2], "Large")], fromOption: "Source").Ok);
        Assert.Equal("old/top.mtrl", Material());
        Assert.True(BodyRetargetWriter.Save(root, Group, GamePath, "Rue+", "Source",
                                            [new BodyRetargetWriter.Refit("Rue L", [3], "Large")], fromOption: "Source",
                                            sourceFilesWin: true).Ok);
        Assert.Equal("source/top.mtrl", Material());
        var files = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(o => o.Name == "Rue L").Files;
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(root, files[GamePath])));
    }

    [Fact]
    public void A_size_saved_into_a_multi_choice_list_joins_it_beside_the_piece_it_was_cut_from()
    {
        // The automatic refit's case: no size group switches the model, so the size goes into the list the piece itself
        // is ticked in. Appended — Penumbra keeps a multi-choice selection as a bitmask, so nothing may move — and the
        // list stays multi-choice, with the author's selection untouched.
        WriteAuthorSizeGroup("Multi", 0b11L);
        var outcome = BodyRetargetWriter.Save(root, TopSize, GamePath, "Neolithe", "Rue Med",
                                              [new BodyRetargetWriter.Refit("Neolithe — S", [7], "S")],
                                              fromOption: "Rue Med");
        Assert.True(outcome.Ok, outcome.Message);

        var after = Read(TopSize);
        Assert.Equal("Multi", after.GetProperty("Type").GetString());
        Assert.Equal(0b11L, after.GetProperty("DefaultSettings").GetInt64());
        Assert.Equal(["Rue Med", "Rue Large", "Neolithe — S"],
                     after.GetProperty("Options").EnumerateArray().Select(o => o.GetProperty("Name").GetString()));
        Assert.True(BodyRetargetWriter.ReadRecord(root)!.Options.Single().InAuthorGroup);
    }

    [Fact]
    public void Undo_takes_only_its_own_option_out_of_the_author_s_group()
    {
        WriteAuthorSizeGroup();
        var before = Read(TopSize);
        BodyRetargetWriter.Save(root, TopSize, "Rue Yiggle Small", GamePath, [5], "Rue+", "Yiggle - Medium",
                                "Yiggle - Small");
        string rel = PenumbraModMeta.TryReadFileOptions(root, TopSize)!.Single(o => o.Name == "Rue Yiggle Small")
                                    .Files[GamePath];

        var outcome = BodyRetargetWriter.Undo(root, TopSize, "Rue Yiggle Small");
        Assert.True(outcome.Ok, outcome.Message);

        // The group is still there, byte for byte as the author left it; only our file and record are gone.
        Assert.Equal([TopSize, "Items"], GroupNames());
        Assert.True(Same(before, Read(TopSize)), "the author's group changed");
        Assert.False(File.Exists(Path.Combine(root, rel)));
        Assert.Null(BodyRetargetWriter.ReadRecord(root));
    }

    [Fact]
    public void An_author_s_option_of_the_same_name_is_never_overwritten()
    {
        WriteAuthorSizeGroup();
        var outcome = BodyRetargetWriter.Save(root, TopSize, "Rue Large", GamePath, [9], "Rue+", "Medium", "Large");

        Assert.False(outcome.Ok);
        var large = PenumbraModMeta.TryReadFileOptions(root, TopSize)!.Single(o => o.Name == "Rue Large");
        Assert.Equal("top size/rue large/top.mdl", large.Files[GamePath]);
    }

    [Theory]
    [InlineData("Single", 1L, 1L)]    // the second option selected stays selected
    [InlineData("Multi", 0b11L, 0b11L)]
    public void Removing_an_option_after_the_author_s_leaves_their_selection_alone(string type, long before, long after)
    {
        WriteAuthorSizeGroup(type, before);
        BodyRetargetWriter.Save(root, TopSize, "Extra", GamePath, [1], "Rue+", "a", "b");
        BodyRetargetWriter.Undo(root, TopSize, "Extra");
        Assert.Equal(after, Read(TopSize).GetProperty("DefaultSettings").GetInt64());
    }

    [Theory]
    [InlineData("Single", 1L, 0, 0L)]      // the selected option moves up a place with the rest
    [InlineData("Single", 1L, 1, 0L)]      // the selected option itself removed: back to the first
    [InlineData("Single", 2L, 0, 1L)]
    [InlineData("Multi", 0b101L, 1, 0b11L)] // the middle bit goes, the one above it drops a place
    public void Removing_an_option_corrects_the_default_for_the_ones_that_move_up(string type, long before, int remove,
                                                                                  long after)
    {
        WriteAuthorSizeGroup(type, before);
        // Three options, so there is one above the removed one.
        BodyRetargetWriter.Save(root, TopSize, "Third", GamePath, [1], "Rue+", "a", "b");
        string name = PenumbraModMeta.TryReadFileOptions(root, TopSize)![remove].Name;
        Assert.NotNull(PenumbraModMeta.RemoveOption(root, TopSize, name));
        Assert.Equal(after, Read(TopSize).GetProperty("DefaultSettings").GetInt64());
    }

    [Fact]
    public void An_option_deleted_outside_Proteus_does_not_jam_undo()
    {
        // Two saves; then the whole group of the first is deleted in Penumbra, behind the record's back.
        BodyRetargetWriter.Save(root, "Body — gone", "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW XS", GamePath, [2], "Neolithe", "SFW M", "SFW XS");
        PenumbraModMeta.DeleteGroup(root, "Body — gone");

        // The ghost is not offered: only the option that is really there.
        var record = BodyRetargetWriter.ReadRecord(root)!;
        Assert.Equal(["Neolithe SFW XS"], record.Options.Select(e => e.Name));

        // And undoing it works, and leaves nothing behind to press again.
        var outcome = BodyRetargetWriter.Undo(root, record.GroupOf(record.Options[^1]), record.Options[^1].Name);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Null(BodyRetargetWriter.ReadRecord(root));
    }

    [Fact]
    public void An_unreadable_manifest_prunes_nothing()
    {
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), "{ not json");

        Assert.Single(BodyRetargetWriter.ReadRecord(root)!.Options);
    }

    [Fact]
    public void A_group_a_refit_made_is_not_mistaken_for_the_author_s()
    {
        WriteAuthorSizeGroup();
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");
        Assert.False(BodyRetargetWriter.IsAuthorGroup(root, Group));
        Assert.True(BodyRetargetWriter.IsAuthorGroup(root, TopSize));
        Assert.False(BodyRetargetWriter.IsAuthorGroup(root, "No Such Group"));
    }

    private static readonly object Eqdp = PenumbraManipulations.EqdpManipulation("0201", "Feet", 6023);

    [Fact]
    [Trait("Plan", "AC-racial-gear-upscale-4")]
    [Trait("Plan", "EDGE-racial-gear-upscale-13")]
    public void A_down_route_save_redirects_the_racial_path_and_undo_leaves_nothing()
    {
        // Something already in the mod, so the refit's group is seen to go after it, not before.
        const string auri = "chara/equipment/e0258/model/c1401e0258_dwn.mdl";
        const string group = "Body — c1401e0258_dwn";
        BodyRetargetWriter.Save(root, Group, "Neolithe SFW L", GamePath, [1], "Neolithe", "SFW M", "SFW L");

        var route = RefitCore.Route(auri, 1401, code => code == 201, code => code == 201, [], null);
        Assert.True(route.Down);
        string path = route.SavePath(auri);
        var outcome = BodyRetargetWriter.Save(root, group, path, "Bibo+", "Vanilla",
                                              [new BodyRetargetWriter.Refit("Bibo+ Large", [7], "Large")],
                                              manipulations: route.Eqdp(path) is { } eqdp ? [eqdp] : null);
        Assert.True(outcome.Ok, outcome.Message);

        var options = PenumbraModMeta.TryReadFileOptions(root, group)!;
        var refit = options.Single(o => o.Name == "Bibo+ Large");
        Assert.Equal(new[] { auri }, refit.Files.Keys);
        Assert.Null(refit.Manipulations);
        var groups = PenumbraModMeta.TryReadGroups(root)!.Select(g => g.Name).ToList();
        Assert.True(groups.IndexOf(group) > groups.IndexOf(Group), "the refit's group goes after the mod's own");

        Assert.True(BodyRetargetWriter.Undo(root, group, "Bibo+ Large").Ok);
        Assert.DoesNotContain(PenumbraModMeta.TryReadFileOptions(root, group) ?? [],
                              o => o.Name == "Bibo+ Large" || o.Manipulations is { Count: > 0 });
    }

    [Fact]
    public void A_baked_refit_carries_its_EQDP_entry_and_Original_carries_none()
    {
        var outcome = BodyRetargetWriter.Save(root, Group, GamePath, "Neolithe", "SFW M",
                                              [new BodyRetargetWriter.Refit("Neolithe SFW L", [1], "SFW L")],
                                              manipulations: [Eqdp]);
        Assert.True(outcome.Ok, outcome.Message);

        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Null(options.Single(o => o.Name == BodyRetargetWriter.OriginalOption).Manipulations);
        var manips = options.Single(o => o.Name == "Neolithe SFW L").Manipulations;
        Assert.NotNull(manips);
        var eqdp = Assert.IsType<JsonElement>(Assert.Single(manips!));
        Assert.Equal("Eqdp", eqdp.GetProperty("Type").GetString());
        Assert.Equal(6023, eqdp.GetProperty("Manipulation").GetProperty("SetId").GetInt32());
        Assert.Equal("Female", eqdp.GetProperty("Manipulation").GetProperty("Gender").GetString());
    }

    [Fact]
    public void Saving_another_size_into_the_group_keeps_the_first_one_s_EQDP_entry()
    {
        // The group is rewritten whole on every save; an option read back without its manipulations would lose them.
        BodyRetargetWriter.Save(root, Group, GamePath, "Neolithe", "SFW M",
                                [new BodyRetargetWriter.Refit("Neolithe SFW L", [1], "SFW L")], manipulations: [Eqdp]);
        BodyRetargetWriter.Save(root, Group, GamePath, "Neolithe", "SFW M",
                                [new BodyRetargetWriter.Refit("Neolithe SFW S", [2], "SFW S")]);

        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Single(options.Single(o => o.Name == "Neolithe SFW L").Manipulations!);
        Assert.Null(options.Single(o => o.Name == "Neolithe SFW S").Manipulations);
    }

    [Fact]
    public void Saving_the_same_option_again_writes_its_EQDP_entry_once()
    {
        for (int i = 0; i < 2; i++)
            BodyRetargetWriter.Save(root, Group, GamePath, "Neolithe", "SFW M",
                                    [new BodyRetargetWriter.Refit("Neolithe SFW L", [1], "SFW L")],
                                    manipulations: [Eqdp]);

        Assert.Single(PenumbraModMeta.TryReadFileOptions(root, Group)!
                          .Single(o => o.Name == "Neolithe SFW L").Manipulations!);
    }

    [Fact]
    public void An_EQDP_entry_Penumbra_rewrote_is_the_same_entry_as_the_one_Proteus_writes()
    {
        // As Penumbra writes the manifest back: fields reordered, ShiftedEntry dropped.
        var rewritten = JsonDocument.Parse("""
            { "Type": "Eqdp", "Manipulation": { "Gender": "Female", "Race": "Midlander", "SetId": 6023,
                                                "Slot": "Feet", "Entry": 768 } }
            """).RootElement;
        Assert.Equal(BodyRetargetWriter.ManipulationKey(Eqdp), BodyRetargetWriter.ManipulationKey(rewritten));

        var otherSet = PenumbraManipulations.EqdpManipulation("0201", "Feet", 6024);
        var otherRace = PenumbraManipulations.EqdpManipulation("0401", "Feet", 6023);
        Assert.NotEqual(BodyRetargetWriter.ManipulationKey(Eqdp), BodyRetargetWriter.ManipulationKey(otherSet));
        Assert.NotEqual(BodyRetargetWriter.ManipulationKey(Eqdp), BodyRetargetWriter.ManipulationKey(otherRace));
    }

    [Fact]
    public void Saving_again_after_Penumbra_rewrote_the_manifest_still_writes_one_EQDP_entry()
    {
        BodyRetargetWriter.Save(root, Group, GamePath, "Neolithe", "SFW M",
                                [new BodyRetargetWriter.Refit("Neolithe SFW L", [1], "SFW L")], manipulations: [Eqdp]);

        // Penumbra's shape of the same entry, put back where ours was.
        string meta = Path.Combine(root, PenumbraModMeta.MetaFile);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!;
        foreach (var group in node["Groups"]!.AsArray())
            foreach (var option in group!["Options"]!.AsArray())
                if (option!["Manipulations"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } manips)
                    manips[0] = System.Text.Json.Nodes.JsonNode.Parse("""
                        { "Type": "Eqdp", "Manipulation": { "Gender": "Female", "Race": "Midlander", "SetId": 6023,
                                                            "Slot": "Feet", "Entry": 768 } }
                        """);
        File.WriteAllText(meta, node.ToJsonString());

        BodyRetargetWriter.Save(root, Group, GamePath, "Neolithe", "SFW M",
                                [new BodyRetargetWriter.Refit("Neolithe SFW L", [2], "SFW L")], manipulations: [Eqdp]);

        Assert.Single(PenumbraModMeta.TryReadFileOptions(root, Group)!
                          .Single(o => o.Name == "Neolithe SFW L").Manipulations!);
    }

    [Fact]
    public void A_refit_saved_at_a_path_is_not_the_author_s_model_for_it()
    {
        const string female = "chara/equipment/e6023/model/c0201e6023_sho.mdl";
        var record = new BodyRetargetWriter.Record
        {
            Options = [new BodyRetargetWriter.Entry { File = "Body Retarget/Neolithe L/chara/c0201e6023_sho.mdl" }],
        };
        var ours = new PenumbraModMeta.Redirect(female, "body retarget\\neolithe l\\chara\\c0201e6023_sho.mdl", "Size / L");
        var authors = new PenumbraModMeta.Redirect(female, "female\\c0201e6023_sho.mdl", "Size / Female");

        Assert.False(BodyRetargetWriter.AuthorProvides([ours], record, female));
        Assert.True(BodyRetargetWriter.AuthorProvides([ours, authors], record, female));
        Assert.False(BodyRetargetWriter.AuthorProvides([], record, female));
    }

    [Fact]
    public void An_author_s_option_keeps_its_manipulations_when_a_refit_joins_the_group()
    {
        WriteAuthorSizeGroup();
        BodyRetargetWriter.Save(root, TopSize, GamePath, "Rue", "Rue Med",
                                [new BodyRetargetWriter.Refit("Rue Small", [1], "Small")], manipulations: [Eqdp]);

        var options = PenumbraModMeta.TryReadFileOptions(root, TopSize)!;
        Assert.Equal("Imc", Assert.IsType<JsonElement>(Assert.Single(options.Single(o => o.Name == "Rue Med")
                                                                            .Manipulations!))
                                .GetProperty("Type").GetString());
        Assert.Single(options.Single(o => o.Name == "Rue Small").Manipulations!);
    }

    // ── a refit for a race picked in "Refit for" ────────────────────────────────────────────────────────

    private const string Shirt = "chara/equipment/e6091/model/c0101e6091_top.mdl";
    private const string HighlanderShirt = "chara/equipment/e6091/model/c0301e6091_top.mdl";
    private const string ShirtMaterial = "chara/equipment/e6091/material/v0001/mt_c0101e6091_top_a.mtrl";
    private const string Lumberjack = "Summer Indigo Shirt";

    /// <summary>The Lumberjack's layout: one group, its option publishing the Midlander shirt and its material.</summary>
    private void WriteShirtGroup(string type)
        => File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
        {
          "FileVersion": 4, "Name": "The Lumberjack",
          "Groups": [
            { "Version": 0, "Name": "{{Lumberjack}}", "Priority": 0, "Type": "{{type}}", "DefaultSettings": 1, "Options": [
              { "Name": "Everything", "Files": { "{{Shirt}}": "shirt/c0101e6091_top.mdl", "{{ShirtMaterial}}": "shirt/top_a.mtrl" },
                "Manipulations": [ { "Type": "Est", "Manipulation": { "Gender": "Female", "Race": "Midlander", "SetId": 6091,
                                                                         "Slot": "Body", "Entry": 6085 } } ] } ] },
            { "Version": 0, "Name": "After", "Priority": 0, "Type": "Single", "DefaultSettings": 0, "Options": [] }
          ]
        }
        """);

    private static List<object> HighlanderManipulations()
    {
        var route = new RacialModelBake.Route(101, 301, Down: false);
        return [route.Eqdp(route.SavePath(Shirt))!, PenumbraManipulations.EstManipulation("0301", "Body", 6091, 6085)];
    }

    [Theory]
    [InlineData("Single")]
    [InlineData("Multi")]
    [Trait("Plan", "AC-refit-other-race-3")]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-15")]
    public void A_highlander_refit_joins_the_author_s_group_and_keeps_the_midlander_model(string type)
    {
        WriteShirtGroup(type);
        var before = Read(Lumberjack);
        var route = new RacialModelBake.Route(101, 301, Down: false);
        var outcome = BodyRetargetWriter.Save(root, Lumberjack, route.SavePath(Shirt), "TBSE-X", "Core files",
                                              [new BodyRetargetWriter.Refit("TBSE-X — M Highlander", [9], "M Highlander")],
                                              fromOption: "Everything", manipulations: HighlanderManipulations());
        Assert.True(outcome.Ok, outcome.Message);

        // Appended after the author's option, in their group, and nothing else moved.
        Assert.Equal([Lumberjack, "After"], GroupNames());
        var options = PenumbraModMeta.TryReadFileOptions(root, Lumberjack)!;
        Assert.Equal(["Everything", "TBSE-X — M Highlander"], options.Select(o => o.Name));
        Assert.True(Same(before.GetProperty("Options")[0], Read(Lumberjack).GetProperty("Options")[0]),
                    "the author's option changed");

        // Highlander men load the refit; the Midlander model and the material ride along, so everyone else draws what
        // they drew with the option it was cut from.
        var refit = options[1];
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(root, refit.Files[HighlanderShirt])));
        Assert.Equal("shirt/c0101e6091_top.mdl", refit.Files[Shirt]);
        Assert.Equal("shirt/top_a.mtrl", refit.Files[ShirtMaterial]);

        // His EQDP entry and the extra skeleton carried for him, once each, beside the author's own entry.
        var manips = refit.Manipulations!.Cast<JsonElement>().ToList();
        Assert.Single(manips, m => m.GetProperty("Type").GetString() == "Eqdp");
        Assert.Single(EstTable.FromManipulations(refit.Manipulations), e => e.Race == 301);
        Assert.Single(EstTable.FromManipulations(refit.Manipulations), e => e.Race == 201);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-7")]
    public void Saving_the_highlander_size_again_keeps_one_eqdp_and_one_est_entry()
    {
        WriteShirtGroup("Single");
        var route = new RacialModelBake.Route(101, 301, Down: false);
        for (int i = 0; i < 2; i++)
            Assert.True(BodyRetargetWriter.Save(root, Lumberjack, route.SavePath(Shirt), "TBSE-X", "Core files",
                                                [new BodyRetargetWriter.Refit("TBSE-X — M Highlander", [(byte)i], "M Highlander")],
                                                fromOption: "Everything", manipulations: HighlanderManipulations()).Ok);

        var refit = PenumbraModMeta.TryReadFileOptions(root, Lumberjack)!.Single(o => o.Name == "TBSE-X — M Highlander");
        var manips = refit.Manipulations!.Cast<JsonElement>().ToList();
        Assert.Single(manips, m => m.GetProperty("Type").GetString() == "Eqdp");
        Assert.Single(EstTable.FromManipulations(refit.Manipulations), e => e.Race == 301);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(root, refit.Files[HighlanderShirt])));
    }

    // ── a Highlander-only group refitted for Midlanders (refit-race-down) ───

    private const string HighlanderMaterial = "chara/equipment/e6091/material/v0001/mt_c0301e6091_top_a.mtrl";

    private void WriteHighlanderGroup(string type)
        => File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
        {
          "FileVersion": 4, "Name": "Highlander only",
          "Groups": [
            { "Version": 0, "Name": "{{Lumberjack}}", "Priority": 0, "Type": "{{type}}", "DefaultSettings": 1, "Options": [
              { "Name": "Everything", "Files": { "{{HighlanderShirt}}": "shirt/c0301e6091_top.mdl",
                                                 "{{HighlanderMaterial}}": "shirt/top_a.mtrl" } } ] },
            { "Version": 0, "Name": "After", "Priority": 0, "Type": "Single", "DefaultSettings": 0, "Options": [] }
          ]
        }
        """);

    private static readonly RacialModelBake.Route Lowered = new(301, 101, Down: false, Lowered: true);

    private static List<object> MidlanderManipulations()
        => [Lowered.Eqdp(Lowered.SavePath(HighlanderShirt))!, PenumbraManipulations.EstManipulation("0101", "Body", 6091, 6085)];

    [Theory]
    [InlineData("Single")]
    [InlineData("Multi")]
    [Trait("Plan", "AC-refit-race-down-3")]
    public void A_midlander_refit_of_a_highlander_only_group_keeps_the_highlander_model(string type)
    {
        WriteHighlanderGroup(type);
        var before = Read(Lumberjack);
        Assert.Equal(Shirt, Lowered.SavePath(HighlanderShirt));
        var outcome = BodyRetargetWriter.Save(root, Lumberjack, Lowered.SavePath(HighlanderShirt), "TBSE-X", "Core files",
                                              [new BodyRetargetWriter.Refit("TBSE-X — Large (Midlander M)", [7], "Large")],
                                              fromOption: "Everything", manipulations: [Lowered.Eqdp(Shirt)!]);
        Assert.True(outcome.Ok, outcome.Message);

        Assert.Equal([Lumberjack, "After"], GroupNames());
        var options = PenumbraModMeta.TryReadFileOptions(root, Lumberjack)!;
        Assert.Equal(["Everything", "TBSE-X — Large (Midlander M)"], options.Select(o => o.Name));
        Assert.True(Same(before.GetProperty("Options")[0], Read(Lumberjack).GetProperty("Options")[0]),
                    "the author's option changed");

        // Midlander men load the refit; the Highlander model and its material ride along.
        var refit = options[1];
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(root, refit.Files[Shirt])));
        Assert.Equal("shirt/c0301e6091_top.mdl", refit.Files[HighlanderShirt]);
        Assert.Equal("shirt/top_a.mtrl", refit.Files[HighlanderMaterial]);
        var eqdp = Assert.Single(refit.Manipulations!.Cast<JsonElement>());
        var m = eqdp.GetProperty("Manipulation");
        Assert.Equal(("Eqdp", "Midlander", "Male", 6091),
                     (eqdp.GetProperty("Type").GetString(), m.GetProperty("Race").GetString(),
                      m.GetProperty("Gender").GetString(), m.GetProperty("SetId").GetInt32()));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-11")]
    public void Saving_the_midlander_size_again_keeps_one_eqdp_and_one_est_entry()
    {
        WriteHighlanderGroup("Single");
        for (int i = 0; i < 2; i++)
            Assert.True(BodyRetargetWriter.Save(root, Lumberjack, Lowered.SavePath(HighlanderShirt), "TBSE-X", "Core files",
                                                [new BodyRetargetWriter.Refit("TBSE-X — Large (Midlander M)", [(byte)i], "Large")],
                                                fromOption: "Everything", manipulations: MidlanderManipulations()).Ok);

        var refit = PenumbraModMeta.TryReadFileOptions(root, Lumberjack)!.Single(o => o.Name == "TBSE-X — Large (Midlander M)");
        var manips = refit.Manipulations!.Cast<JsonElement>().ToList();
        Assert.Single(manips, m => m.GetProperty("Type").GetString() == "Eqdp");
        Assert.Single(EstTable.FromManipulations(refit.Manipulations), e => e.Race == 101);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(root, refit.Files[Shirt])));
    }

    /// <summary>A bake refused: the refit is of the model as drawn, saved over it, with no entry for another race.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-6")]
    public void A_refused_bake_saves_at_the_drawn_path_with_no_eqdp()
    {
        var none = RacialModelBake.Route.None;
        Assert.Equal(Shirt, none.SavePath(Shirt));
        Assert.Null(none.Eqdp(none.SavePath(Shirt)));

        WriteShirtGroup("Single");
        Assert.True(BodyRetargetWriter.Save(root, Lumberjack, none.SavePath(Shirt), "TBSE-X", "Core files",
                                            [new BodyRetargetWriter.Refit("TBSE-X — M", [3], "M")],
                                            fromOption: "Everything").Ok);
        var refit = PenumbraModMeta.TryReadFileOptions(root, Lumberjack)!.Single(o => o.Name == "TBSE-X — M");
        Assert.False(refit.Files.ContainsKey(HighlanderShirt));
        Assert.DoesNotContain(refit.Manipulations?.Cast<JsonElement>() ?? [], m => m.GetProperty("Type").GetString() == "Eqdp");
    }

    // ── own-model-overwrite: a save over the author's model for another race ─────────────────────────────────────────

    private const string MensTop = "chara/equipment/e0391/model/c0101e0391_top.mdl";
    private const string WomensTop = "chara/equipment/e0391/model/c0201e0391_top.mdl";

    private static System.Text.Json.Nodes.JsonObject Files(params (string Path, string File)[] files)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (path, file) in files) o[path] = file;
        return o;
    }

    private static System.Text.Json.Nodes.JsonObject Option(string name, int? priority = null,
                                                            System.Text.Json.Nodes.JsonObject? files = null,
                                                            System.Text.Json.Nodes.JsonObject? swaps = null)
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["Name"] = name, ["Files"] = files ?? [] };
        if (priority != null) o["Priority"] = priority;
        if (swaps != null) o["FileSwaps"] = swaps;
        return o;
    }

    private static System.Text.Json.Nodes.JsonObject GroupOf(string? name, string type, int priority,
                                                             params System.Text.Json.Nodes.JsonObject[] options)
    {
        var g = new System.Text.Json.Nodes.JsonObject { ["Type"] = type, ["Priority"] = priority, ["DefaultSettings"] = 0 };
        if (name != null) g["Name"] = name;
        g["Options"] = new System.Text.Json.Nodes.JsonArray(options.Select(o => (System.Text.Json.Nodes.JsonNode)o).ToArray());
        return g;
    }

    /// <summary>The mod's manifest with these groups (and default files), as Penumbra lays it out.</summary>
    private void Manifest(System.Text.Json.Nodes.JsonObject? defaults, params System.Text.Json.Nodes.JsonObject[] groups)
    {
        string path = Path.Combine(root, PenumbraModMeta.MetaFile);
        var meta = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        meta["Groups"] = new System.Text.Json.Nodes.JsonArray(groups.Select(g => (System.Text.Json.Nodes.JsonNode)g).ToArray());
        meta["DefaultData"] = new System.Text.Json.Nodes.JsonObject { ["Files"] = defaults ?? [] };
        File.WriteAllText(path, meta.ToJsonString());
        Directory.CreateDirectory(Path.Combine(root, "body"));
        File.WriteAllBytes(Path.Combine(root, "body", "c0101.mdl"), [9, 9, 9]);
    }

    /// <summary>Thorn Princess's layout: the men's top a copy in a multi-choice body group at priority 1, the women's
    /// sizes in a single-choice "Size" group at priority 4.</summary>
    private void ThornLayout()
        => Manifest(null,
            GroupOf("Body", "Multi", 1, Option("Everything", files: Files((MensTop, "body/c0101.mdl")))),
            GroupOf("Size", "Single", 4, Option("Small", files: Files((WomensTop, "size/small.mdl"))),
                                         Option("Large", files: Files((WomensTop, "size/large.mdl")))));

    private Dictionary<string, IReadOnlyCollection<string>> Ticks(params (string Group, string[] Options)[] ticks)
        => ticks.ToDictionary(t => t.Group, t => (IReadOnlyCollection<string>)t.Options);

    private string? RefitFile(string group, string option)
        => (PenumbraModMeta.TryReadFileOptions(root, group) ?? []).Where(o => o.Name == option)
                          .Select(o => o.Files.GetValueOrDefault(MensTop)?.Replace('\\', '/')).SingleOrDefault();

    private List<JsonElement> RawGroups() => PenumbraModMeta.TryReadRawGroups(root)!;

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-4")]
    public void A_cross_sex_save_into_the_author_s_size_group_is_what_penumbra_serves()
    {
        ThornLayout();
        var bodyBefore = Read("Body").GetRawText();

        var saved = BodyRetargetWriter.Save(root, "Size", MensTop, "The Body SE", "Small", [new BodyRetargetWriter.Refit("TBSE M", [1, 2], "M")],
                                            fromOption: "Small");
        Assert.True(saved.Ok, saved.Message);
        string refit = RefitFile("Size", "TBSE M")!;
        Assert.NotNull(refit);

        var on = RefitCore.SelectionFor(false, "TBSE M", ["Small"], "Small");
        Assert.Equal(refit, PenumbraPrecedence.Winner(root, MensTop, Ticks(("Body", ["Everything"]), ("Size", [.. on]))));
        Assert.Equal("body/c0101.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Body", ["Everything"]), ("Size", ["Small"]))));
        Assert.True(Same(JsonDocument.Parse(bodyBefore).RootElement, Read("Body")));
        Assert.Equal(4, PriorityOf("Size"));
        Assert.Equal(["Body", "Size"], GroupNames());
        Assert.Empty(BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small"));
    }

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-4")]
    public void A_new_group_save_is_what_penumbra_serves_over_an_author_model()
    {
        ThornLayout();
        var saved = BodyRetargetWriter.Save(root, Group, "TBSE M", MensTop, [1, 2], "The Body SE", "Small", "M");
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal(["Body", "Size", Group], GroupNames());
        string refit = RefitFile(Group, "TBSE M")!;
        var worn = Ticks(("Body", ["Everything"]), ("Size", ["Small"]), (Group, ["TBSE M"]));
        Assert.Equal(refit, PenumbraPrecedence.Winner(root, MensTop, worn));
        worn[Group] = [BodyRetargetWriter.OriginalOption];
        Assert.Equal("body/c0101.mdl", PenumbraPrecedence.Winner(root, MensTop, worn));
    }

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-4")]
    public void Winner_follows_penumbra_s_order()
    {
        // Higher group priority wins; on a tie the later group.
        Manifest(Files((MensTop, "default.mdl")),
            GroupOf("A", "Single", 2, Option("a", files: Files((MensTop, "a.mdl")))),
            GroupOf("B", "Single", 1, Option("b", files: Files((MensTop, "b.mdl")))),
            GroupOf("C", "Single", 2, Option("c", files: Files((MensTop, "c.mdl")))));
        Assert.Equal("c.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("A", ["a"]), ("B", ["b"]), ("C", ["c"]))));
        Assert.Equal("a.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("A", ["a"]), ("B", ["b"]))));
        Assert.Equal("default.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks()));

        // In a multi-choice group the earlier option on a tie, the higher option priority first; files before swaps.
        Manifest(null,
            GroupOf("M", "Multi", 0, Option("x", files: Files((MensTop, "x.mdl"))), Option("y", files: Files((MensTop, "y.mdl"))),
                                     Option("z", 5, swaps: Files((MensTop, "z-swap.mdl")))));
        Assert.Equal("x.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("M", ["x", "y"]))));
        Assert.Equal("z-swap.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("M", ["x", "z"]))));

        // A combining group: the container numbered by the ticked bits.
        Manifest(null, Combining("K", 0, 3, (5, MensTop, "five.mdl")));
        Assert.Equal("five.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("K", ["o0", "o2"]))));
        Assert.Null(PenumbraPrecedence.Winner(root, MensTop, Ticks(("K", ["o0"]))));
    }

    /// <summary>A combining group of <paramref name="options"/> options, every container empty but the one named.</summary>
    private static System.Text.Json.Nodes.JsonObject Combining(string name, int priority, int options,
                                                               (int Index, string Path, string File) at)
    {
        var g = GroupOf(name, "Combining", priority, Enumerable.Range(0, options).Select(i => Option($"o{i}")).ToArray());
        g["Containers"] = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, 1 << options).Select(i =>
            (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["Files"] = i == at.Index ? Files((at.Path, at.File)) : new System.Text.Json.Nodes.JsonObject(),
            }).ToArray());
        return g;
    }

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-5")]
    public void Outranking_names_a_higher_priority_group()
    {
        Manifest(null,
            GroupOf("Size", "Single", 0, Option("Small", files: Files((WomensTop, "s.mdl"))), Option("Refit", files: Files((MensTop, "r.mdl")))),
            GroupOf("Body", "Multi", 7, Option("Everything", files: Files((MensTop, "body/c0101.mdl")))));
        Assert.Equal([new BodyRetargetWriter.Rival("Body", null)], BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small"));
        Assert.Equal("body/c0101.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Size", ["Refit"]), ("Body", ["Everything"]))));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-5")]
    public void Outranking_ignores_default_data()
    {
        Manifest(Files((MensTop, "body/c0101.mdl")),
            GroupOf("Size", "Single", 0, Option("Small", files: Files((WomensTop, "s.mdl"))), Option("Refit", files: Files((MensTop, "r.mdl")))));
        Assert.Empty(BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small"));
        Assert.Equal("r.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Size", ["Refit"]))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Plan", "EDGE-own-model-overwrite-6")]
    public void Outranking_breaks_a_group_tie_by_later_position(bool rivalLater)
    {
        var size = GroupOf("Size", "Single", 3, Option("Small", files: Files((WomensTop, "s.mdl"))), Option("Refit", files: Files((MensTop, "r.mdl"))));
        var body = GroupOf("Body", "Multi", 3, Option("Everything", files: Files((MensTop, "body/c0101.mdl"))));
        if (rivalLater) Manifest(null, size, body); else Manifest(null, body, size);
        var rivals = BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small");
        string? served = PenumbraPrecedence.Winner(root, MensTop, Ticks(("Size", ["Refit"]), ("Body", ["Everything"])));
        if (rivalLater)
        {
            Assert.Equal([new BodyRetargetWriter.Rival("Body", null)], rivals);
            Assert.Equal("body/c0101.mdl", served);
        }
        else
        {
            Assert.Empty(rivals);
            Assert.Equal("r.mdl", served);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-7")]
    public void Outranking_never_counts_a_single_destination_s_own_options()
    {
        Manifest(null, GroupOf("Size", "Single", 0, Option("Small", files: Files((WomensTop, "s.mdl"))),
                                                    Option("Men", files: Files((MensTop, "m.mdl")))));
        Assert.Empty(BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small"));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-8")]
    public void Outranking_is_empty_for_a_group_not_in_the_manifest()
    {
        ThornLayout();
        Assert.Empty(BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Nowhere", null));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-9")]
    public void Outranking_is_empty_without_a_manifest()
    {
        Assert.Empty(BodyRetargetWriter.Outranking(null, MensTop, "Size", null));
        Assert.Null(PenumbraModMeta.TryReadRawGroups(Path.Combine(root, "no such mod")));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-12")]
    public void Undo_of_a_save_over_the_author_s_model_leaves_it_untouched()
    {
        ThornLayout();
        BodyRetargetWriter.Save(root, "Size", MensTop, "The Body SE", "Small", [new BodyRetargetWriter.Refit("TBSE M", [1, 2], "M")],
                                fromOption: "Small");
        Assert.Equal(["Body", "Size"], GroupNames());
        Assert.True(BodyRetargetWriter.Undo(root, "Size", "TBSE M").Ok);
        Assert.Equal(["Body", "Size"], GroupNames());
        Assert.Equal(["Small", "Large"], PenumbraModMeta.TryReadFileOptions(root, "Size")!.Select(o => o.Name));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(root, "body", "c0101.mdl")));
        Assert.Equal("body/c0101.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Body", ["Everything"]), ("Size", ["Small"]))));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-13")]
    public void Outranking_reports_a_ticked_sibling_of_a_multi_destination()
    {
        // An outfit ticked in one "Items" list: the refit, appended with no priority, comes after every sibling at 0 or more.
        System.Text.Json.Nodes.JsonObject[] Items() =>
            [Option("Choker"), Option("Shirt", files: Files((WomensTop, "shirt.mdl"))),
             Option("Undershirt", 0, Files((MensTop, "under.mdl"))), Option("Cape", -1, Files((MensTop, "cape.mdl")))];
        Manifest(null, GroupOf("Items", "Multi", 0, Items()));
        Assert.Equal([new BodyRetargetWriter.Rival("Items", "Undershirt")],
                     BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Items", "Shirt"));

        // Saved: the refit's option appended last — and never its own rival when the panel asks again, or a save of the
        // same size again replaces it.
        Manifest(null, GroupOf("Items", "Multi", 0, [.. Items(), Option("TBSE M", files: Files((MensTop, "Body Retarget/refit.mdl")))]));
        Assert.Equal([new BodyRetargetWriter.Rival("Items", "Undershirt")],
                     BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Items", "Shirt", ["TBSE M"]));

        // Saved before, between the shirt and the undershirt: re-saved in place, it comes before the undershirt.
        Manifest(null, GroupOf("Items", "Multi", 0, Option("Choker"), Option("Shirt", files: Files((WomensTop, "shirt.mdl"))),
            Option("TBSE M", files: Files((MensTop, "Body Retarget/refit.mdl"))), Option("Undershirt", 0, Files((MensTop, "under.mdl")))));
        Assert.Empty(BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Items", "Shirt", ["TBSE M"]));
        Assert.Equal("Body Retarget/refit.mdl",
                     PenumbraPrecedence.Winner(root, MensTop, Ticks(("Items", ["Choker", "TBSE M", "Undershirt"]))));
        // A sibling with a priority above the refit's 0 comes first wherever it stands, even after it.
        Manifest(null, GroupOf("Items", "Multi", 0, Option("Shirt", files: Files((WomensTop, "shirt.mdl"))),
            Option("TBSE M", files: Files((MensTop, "Body Retarget/refit.mdl"))), Option("Cape", 2, Files((MensTop, "cape.mdl")))));
        Assert.Equal([new BodyRetargetWriter.Rival("Items", "Cape")],
                     BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Items", "Shirt", ["TBSE M"]));
        Assert.Equal("cape.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Items", ["TBSE M", "Cape"]))));

        // Back to the refit appended last, for what is worn below.
        Manifest(null, GroupOf("Items", "Multi", 0, [.. Items(), Option("TBSE M", files: Files((MensTop, "Body Retarget/refit.mdl")))]));
        var ticked = RefitCore.SelectionFor(true, "TBSE M", ["Choker", "Shirt", "Undershirt", "Cape"], "Shirt");
        Assert.Equal("under.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Items", [.. ticked]))));
        Assert.Equal("Body Retarget/refit.mdl",
                     PenumbraPrecedence.Winner(root, MensTop, Ticks(("Items", [.. ticked.Where(o => o != "Undershirt")]))));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-14")]
    public void Outranking_takes_positions_from_the_raw_groups_array()
    {
        // A nameless group at 0: Size is at 1 and Body at 2 in the raw array, so Body is the later of the tie.
        Manifest(null, GroupOf(null, "Single", 3, Option("x")),
            GroupOf("Size", "Single", 3, Option("Small", files: Files((WomensTop, "s.mdl"))), Option("Refit", files: Files((MensTop, "r.mdl")))),
            GroupOf("Body", "Multi", 3, Option("Everything", files: Files((MensTop, "body/c0101.mdl")))));
        Assert.Equal([new BodyRetargetWriter.Rival("Body", null)], BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small"));
        Assert.Equal("body/c0101.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Size", ["Refit"]), ("Body", ["Everything"]))));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-15")]
    public void Outranking_counts_a_file_swap()
    {
        Manifest(null,
            GroupOf("Size", "Single", 0, Option("Small", files: Files((WomensTop, "s.mdl"))), Option("Refit", files: Files((MensTop, "r.mdl")))),
            GroupOf("Vanilla men", "Single", 2, Option("On", swaps: Files((MensTop, "chara/equipment/e0100/model/c0101e0100_top.mdl")))));
        Assert.Equal([new BodyRetargetWriter.Rival("Vanilla men", null)], BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small"));
        Assert.Equal("chara/equipment/e0100/model/c0101e0100_top.mdl",
                     PenumbraPrecedence.Winner(root, MensTop, Ticks(("Size", ["Refit"]), ("Vanilla men", ["On"]))));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-16")]
    public void Outranking_counts_a_combining_container()
    {
        Manifest(null,
            GroupOf("Size", "Single", 0, Option("Small", files: Files((WomensTop, "s.mdl"))), Option("Refit", files: Files((MensTop, "r.mdl")))),
            Combining("Pieces", 2, 2, (1, MensTop, "body/c0101.mdl")));
        Assert.Equal([new BodyRetargetWriter.Rival("Pieces", null)], BodyRetargetWriter.Outranking(RawGroups(), MensTop, "Size", "Small"));
        Assert.Equal("body/c0101.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Size", ["Refit"]), ("Pieces", ["o0"]))));
        Assert.Equal("r.mdl", PenumbraPrecedence.Winner(root, MensTop, Ticks(("Size", ["Refit"]))));
    }

    /// <summary>
    /// Lay out author groups in this order, each a one-option group with nothing in it — except any group of ours
    /// already present, which is kept exactly as it is and simply placed where the list says.
    /// </summary>
    private void WriteAuthorGroups(params string[] names)
    {
        var ours = PenumbraModMeta.TryReadFileOptions(root, Group);
        foreach (string name in names.Where(n => n != Group))
            PenumbraModMeta.DeleteGroup(root, name);

        for (int i = 0; i < names.Length; i++)
        {
            if (names[i] == Group)
            {
                if (ours != null) PenumbraModMeta.WriteFileOptionGroup(root, i, Group, PriorityOf(Group), ours, 0);
                continue;
            }
            PenumbraModMeta.WriteFileOptionGroup(root, i, names[i], 0,
                [new PenumbraModMeta.FileOption("A", new Dictionary<string, string>())], 0);
        }
    }

    /// <summary>The same json, whatever the whitespace: the manifest writer re-indents everything it writes.</summary>
    private static bool Same(JsonElement a, JsonElement b)
        => System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(a.GetRawText()),
                                                      System.Text.Json.Nodes.JsonNode.Parse(b.GetRawText()));

    private List<string> GroupNames() => (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name).ToList();

    private int PriorityOf(string group) => Read(group).GetProperty("Priority").GetInt32();

    private JsonElement Read(string group)
        => (PenumbraModMeta.TryReadGroups(root) ?? []).Single(g => g.Name == group).Group;
}
