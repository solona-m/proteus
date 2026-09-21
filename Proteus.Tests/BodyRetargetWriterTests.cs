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

    private List<string> GroupNames() => (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name).ToList();

    private int PriorityOf(string group) => Read(group).GetProperty("Priority").GetInt32();

    private JsonElement Read(string group)
        => (PenumbraModMeta.TryReadGroups(root) ?? []).Single(g => g.Name == group).Group;
}
