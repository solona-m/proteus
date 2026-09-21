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

        // The earlier L is replaced where the batch says, not kept alongside it; the batch keeps its order.
        var options = PenumbraModMeta.TryReadFileOptions(root, Group)!;
        Assert.Equal([BodyRetargetWriter.OriginalOption, "Neolithe SFW XS", "Neolithe SFW S", "Neolithe SFW L"],
                     options.Select(o => o.Name));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(root, options[1].Files[GamePath])));
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(root, options[2].Files[GamePath])));
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(root, options[3].Files[GamePath])));

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
