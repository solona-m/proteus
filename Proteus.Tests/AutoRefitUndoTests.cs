using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Undoing the automatic refit of what is worn, on disk: the worn piece's automatic refits go, a size made by hand and
/// every other piece's refits stay, and a mod made for the game's own gear is marked for deletion only once nothing is
/// left in it. Over real temp mod folders.
/// </summary>
public class AutoRefitUndoTests : IDisposable
{
    private const string Top = "chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string Legs = "chara/equipment/e6255/model/c0201e6255_dwn.mdl";

    private readonly string mods = Path.Combine(Path.GetTempPath(), "proteus-autoundo-" + Guid.NewGuid().ToString("N"));

    public AutoRefitUndoTests() => Directory.CreateDirectory(mods);

    public void Dispose()
    {
        try { Directory.Delete(mods, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string AuthorMod(string dir)
    {
        string root = Path.Combine(mods, dir);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson(dir, "Someone", ""));
        return root;
    }

    private string MadeMod(string dir)
    {
        string root = Path.Combine(mods, dir);
        Directory.CreateDirectory(root);
        RefitModService.WriteScaffold(root, dir, "made by a test");
        return root;
    }

    private static void Save(string root, string group, string option, string gamePath, bool auto)
        => Assert.True(BodyRetargetWriter.Save(root, group, gamePath, "Rue+", "Bibo+ — Medium",
                                               [new BodyRetargetWriter.Refit(option, [1, 2, 3], "Medium")], auto: auto).Ok);

    private static List<string> Options(string root, string group)
        => (PenumbraModMeta.TryReadFileOptions(root, group) ?? []).Select(o => o.Name).ToList();

    private static readonly IReadOnlySet<string> NoneSwitched = new HashSet<string>();

    [Fact]
    public void The_record_says_which_refits_were_automatic()
    {
        string root = AuthorMod("Outfit");
        Save(root, "Body — top", "Rue+ — Medium", Top, auto: true);
        Save(root, "Body — legs", "Rue+ — A", Legs, auto: false);

        var record = BodyRetargetWriter.ReadRecord(root)!;
        Assert.True(record.Options.Single(e => e.Name == "Rue+ — Medium").Auto);
        Assert.False(record.Options.Single(e => e.Name == "Rue+ — A").Auto);
    }

    [Fact]
    public void The_worn_pieces_automatic_refit_goes_and_a_size_made_by_hand_stays()
    {
        string root = AuthorMod("Outfit");
        Save(root, "Body — top", "Rue+ — Medium", Top, auto: true);
        Save(root, "Body — top", "Rue+ — Large", Top, auto: false);

        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;

        Assert.Equal(["Rue+ — Medium"], result.Removed);
        Assert.Equal(["Body — top"], result.Groups);
        Assert.Empty(result.Failed);
        Assert.False(result.Empty);
        Assert.Equal([BodyRetargetWriter.OriginalOption, "Rue+ — Large"], Options(root, "Body — top"));
    }

    [Fact]
    public void Another_pieces_refit_stays()
    {
        // The legs' refit is the automatic refit's too, but the top is the piece being undone.
        string root = AuthorMod("Outfit");
        Save(root, "Body — top", "Rue+ — Medium", Top, auto: true);
        Save(root, "Body — legs", "Rue+ — A", Legs, auto: true);

        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;

        Assert.Equal(["Rue+ — Medium"], result.Removed);
        Assert.Equal([BodyRetargetWriter.OriginalOption, "Rue+ — A"], Options(root, "Body — legs"));
    }

    [Fact]
    public void A_refit_saved_but_not_drawn_is_the_pieces_refit_all_the_same()
    {
        // "Thorn Princess": its design held it in the author's small size with a temporary setting, so the slot drew
        // the author's file while the refit sat in the size group. Undo matched the drawn file and found nothing.
        // Nothing here says what is drawn — the piece is the game path, and its refit goes.
        string root = AuthorMod("Thorn Princess");
        Save(root, "Size", "Neolithe — XS", Top, auto: true);

        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;

        Assert.Equal(["Neolithe — XS"], result.Removed);
        Assert.Null(BodyRetargetWriter.ReadRecord(root));
    }

    [Fact]
    public void A_piece_with_only_a_size_made_by_hand_has_nothing_to_undo()
    {
        string root = AuthorMod("Outfit");
        Save(root, "Body — top", "Rue+ — Medium", Top, auto: false);
        string meta = File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile));

        Assert.Null(AutoRefitUndo.Undo(root, Top, NoneSwitched));
        Assert.Equal(meta, File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
    }

    [Fact]
    public void A_refit_from_before_the_mark_goes_when_its_group_was_switched_on_automatically()
    {
        // Saved before entries said whether they were automatic: the collection's record of groups the automatic
        // refit switched on is all that tells.
        string root = AuthorMod("Outfit");
        Save(root, "Body — top", "Rue+ — Medium", Top, auto: false);

        var switchedOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AutoRefitDecisions.GroupKey("Outfit", "Body — top"),
        };
        var result = AutoRefitUndo.Undo(root, Top, switchedOn)!;

        Assert.Equal(["Rue+ — Medium"], result.Removed);
        Assert.Null(PenumbraModMeta.TryReadFileOptions(root, "Body — top"));          // its own group, now empty, is gone
    }

    [Fact]
    public void An_option_covering_two_slots_is_removed_once_files_and_all()
    {
        string root = AuthorMod("Coat");
        Save(root, "Body — coat", "Rue+ — Medium", Top, auto: true);
        Save(root, "Body — coat", "Rue+ — Medium", Legs, auto: true);

        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;

        Assert.Equal(["Rue+ — Medium"], result.Removed);
        Assert.Empty(result.Failed);
        Assert.Null(BodyRetargetWriter.ReadRecord(root));
        Assert.False(Directory.Exists(Path.Combine(root, BodyRetargetWriter.Subfolder)), "the refit's files were left behind");
    }

    [Fact]
    public void A_mod_made_for_the_games_gear_is_deleted_only_once_nothing_is_left_in_it()
    {
        string coat = MadeMod("Ala Mhigan Coat — Rue+");
        Save(coat, "Body — Ala Mhigan Coat", "Rue+ — Medium", Top, auto: true);

        string boots = MadeMod("Boots — Rue+");
        Save(boots, "Body — Boots", "Rue+ — Medium", Top, auto: true);
        Save(boots, "Body — Boots", "Rue+ — Large", Top, auto: false);   // one the player made by hand

        string outfit = AuthorMod("Outfit");                              // somebody else's mod, emptied
        Save(outfit, "Body — top", "Rue+ — Medium", Top, auto: true);

        Assert.True(AutoRefitUndo.Undo(coat, Top, NoneSwitched)!.Empty);
        Assert.False(AutoRefitUndo.Undo(boots, Top, NoneSwitched)!.Empty);     // the hand-made size keeps it
        Assert.False(AutoRefitUndo.Undo(outfit, Top, NoneSwitched)!.Empty);    // never ours to delete
    }

    [Fact]
    public void Only_the_pieces_own_groups_are_switched_back()
    {
        // The author's own size switched on in both the top's group and the legs' group of one outfit: undoing the
        // top switches back the top's group only.
        string root = AuthorMod("Outfit");
        Save(root, "Top size", "Rue+ — Medium", Top, auto: true);
        Save(root, "Legs size", "Rue+ — A", Legs, auto: true);
        var keys = new[]
        {
            AutoRefitDecisions.GroupKey("Outfit", "Top size"),
            AutoRefitDecisions.GroupKey("Outfit", "Legs size"),
            AutoRefitDecisions.GroupKey("Another mod", "Top size"),
        };

        Assert.Equal([AutoRefitDecisions.GroupKey("Outfit", "Top size")], AutoRefitUndo.GroupsServing(root, Top, keys));
    }
}
