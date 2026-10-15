using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// An author's single-choice group whose every option sizes the chest AND the legs — Arabella's "Gemma", "Body
/// Options": the automatic refits of both pieces share one option, named for both sizes, instead of two options that
/// switch each other off. Over real temp mod folders shaped like Gemma's (whose own files are not redistributable); the
/// watcher's jobs are scripted as <see cref="RefitCore.PlanOption"/> then <see cref="BodyRetargetWriter.Save"/>, each
/// with the group's selection as the job before left it.
/// </summary>
public class SharedGroupOneOptionTests : IDisposable
{
    private const string Group = "Body Options";
    private const string Top = "chara/equipment/e6186/model/c0201e6186_top.mdl";
    private const string Dwn = "chara/equipment/e6186/model/c0201e6186_dwn.mdl";
    private const string Glv = "chara/equipment/e6186/model/c0201e6186_glv.mdl";
    private const string NeoTop = "body options\\neolithe\\chara\\equipment\\e6186\\model\\c0201e6186_top.mdl";
    private const string NeoDwn = "body options\\neolithe\\chara\\equipment\\e6186\\model\\c0201e6186_dwn.mdl";
    private const string LibTop = "body options\\libelly shaper\\chara\\equipment\\e6186\\model\\c0201e6186_top.mdl";
    private const string LibDwn = "body options\\libelly shaper\\chara\\equipment\\e6186\\model\\c0201e6186_dwn.mdl";

    /// <summary>"•Arabella• Gemma [March 2025]", under <see cref="LocalData.ModPacks"/>.</summary>
    private const string GemmaPack = LocalData.ModPacks + ":Arabella• Gemma [March 2025].pmp";

    private static readonly BodyOption Chest = new("Neolithe", "--- DEFAULT HAZELNUT ---", "NSFW Hazelnut S",
                                                   "chest/hs.mdl", "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl",
                                                   "_top");
    private static readonly BodyOption Legs = new("Neolithe", "--- DEFAULT ---", "Gen B Small", "legs/gbs.mdl",
                                                  "chara/human/c0201/obj/body/b0001/model/c0201b0001_dwn.mdl", "_dwn");
    private static readonly BodyOption LegsM = new("Neolithe", "--- DEFAULT ---", "Gen B Medium", "legs/gbm.mdl",
                                                   "chara/human/c0201/obj/body/b0001/model/c0201b0001_dwn.mdl", "_dwn");

    private const string Merged = "Neolithe — DEFAULT HAZELNUT · NSFW Hazelnut S + DEFAULT · Gen B Small";
    private const string RivalTop = "Neolithe — DEFAULT HAZELNUT · NSFW Hazelnut S";
    private const string RivalDwn = "Neolithe — DEFAULT · Gen B Small";

    private readonly string mods = Path.Combine(Path.GetTempPath(), "proteus-pairopt-" + Guid.NewGuid().ToString("N"));

    public SharedGroupOneOptionTests() => Directory.CreateDirectory(mods);

    public void Dispose()
    {
        try { Directory.Delete(mods, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, string> Files(params (string Path, string File)[] files)
        => files.ToDictionary(f => f.Path, f => f.File, StringComparer.OrdinalIgnoreCase);

    /// <summary>An author's mod with one group of the given options, like Gemma's "Body Options".</summary>
    private string Mod(string dir, string group, params PenumbraModMeta.FileOption[] options)
    {
        string root = Path.Combine(mods, dir);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson(dir, "Arabella", ""));
        PenumbraModMeta.WriteFileOptionGroup(root, 0, group, 0, options, 1);
        return root;
    }

    /// <summary>Gemma's "Body Options": two options carrying a top and a bottom each, and an empty "x".</summary>
    private string Gemma(string dir = "Gemma")
        => Mod(dir, Group,
               new PenumbraModMeta.FileOption("Neolithe", Files((Top, NeoTop), (Dwn, NeoDwn))),
               new PenumbraModMeta.FileOption("Libelly Shaper", Files((Top, LibTop), (Dwn, LibDwn))),
               new PenumbraModMeta.FileOption("x", Files()));

    private static BodyOption? TargetsOf(string slot, BodyOption legs) => slot switch
    {
        "_top" => Chest,
        "_dwn" => legs,
        _ => null,
    };

    private static RefitCore.PairPlan Plan(string root, string drawnPath, string? selected,
                                           bool single = true, Func<string, bool>? chosen = null, BodyOption? legs = null,
                                           string group = Group)
    {
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        string drawnRel = DrawnBy(redirects, group, selected, drawnPath);
        return RefitCore.PlanOption(redirects, BodyRetargetWriter.ReadRecord(root), group, single,
                                    selected == null ? null : [selected], drawnPath, drawnRel, "Neolithe",
                                    chosen ?? (_ => true), s => TargetsOf(s, legs ?? Legs));
    }

    /// <summary>The file the collection draws at <paramref name="gamePath"/> with <paramref name="selected"/> ticked.</summary>
    private static string DrawnBy(IEnumerable<PenumbraModMeta.Redirect> redirects, string group, string? selected,
                                  string gamePath)
        => redirects.FirstOrDefault(d => d.Source == group + " / " + selected && d.GamePath == gamePath).File
           ?? redirects.First(d => d.Source == group + " / Neolithe" && d.GamePath == gamePath).File;

    /// <summary>One job, as the watcher runs it: planned off what is selected now, saved, and its option switched on.</summary>
    /// <returns>The option now selected.</returns>
    private static string Job(string root, string drawnPath, string selected, BodyOption? legs = null)
    {
        var plan = Plan(root, drawnPath, selected, legs: legs);
        Assert.True(plan.Paired, $"{drawnPath} cut from {selected} was not sized together");
        var saved = BodyRetargetWriter.Save(root, Group, drawnPath, "hs-Neolithe", "[HS] 9 AM",
                                            [new BodyRetargetWriter.Refit(plan.Option, [1, 2, 3], "size")], plan.CutFrom,
                                            auto: true);
        Assert.True(saved.Ok, saved.Message);
        return plan.Option;
    }

    private static List<PenumbraModMeta.FileOption> Options(string root, string group = Group)
        => PenumbraModMeta.TryReadFileOptions(root, group) ?? [];

    private static PenumbraModMeta.FileOption Option(string root, string name, string group = Group)
        => Options(root, group).Single(o => o.Name == name);

    private static bool IsRefit(string rel) => rel.Replace('\\', '/').StartsWith(BodyRetargetWriter.Subfolder + "/");

    private static readonly IReadOnlySet<string> NoneSwitched = new HashSet<string>();

    // ── the merged option ────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-1")]
    [Trait("Plan", "EDGE-fewer-redraws-28")]   // switch order under the redraw hold: unchanged, still one option
    public void Top_then_legs_cut_from_one_author_option_share_one_merged_option()
    {
        string root = Gemma();

        string on = Job(root, Top, "Neolithe");
        Assert.Equal(Merged, on);
        Assert.Equal(Merged, Job(root, Dwn, on));

        Assert.Equal(["Neolithe", "Libelly Shaper", "x", Merged], Options(root).Select(o => o.Name));
        var merged = Option(root, Merged);
        Assert.True(IsRefit(merged.Files[Top]), "the chest is not the refit");
        Assert.True(IsRefit(merged.Files[Dwn]), "the legs are not the refit");
        Assert.Equal(Files((Top, NeoTop), (Dwn, NeoDwn)), Option(root, "Neolithe").Files);
        Assert.Equal(2, BodyRetargetWriter.ReadRecord(root)!.Options.Count(e => e.Name == Merged));
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-1")]
    public void The_merged_name_is_the_body_plus_chest_and_legs_labels()
    {
        string root = Gemma();
        var plan = Plan(root, Top, "Neolithe");

        Assert.True(plan.Paired);
        Assert.Equal(RefitCore.OptionName("Neolithe", [Chest.Label, Legs.Label]), plan.Option);
        Assert.Equal(Merged, plan.Option);
        Assert.Equal("Neolithe", plan.CutFrom);
    }

    [LocalDataFact(GemmaPack)]
    [Trait("Plan", "AC-shared-group-one-option-1")]
    public void The_Gemma_manifest_reads_as_a_pair_option()
    {
        string root = Path.Combine(mods, "Gemma");
        Directory.CreateDirectory(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir));
        File.WriteAllBytes(Path.Combine(root, PenumbraModMeta.MetaFile), LocalData.PackEntryEndingWith(GemmaPack, "meta.json"));
        File.WriteAllBytes(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, BodyRetargetWriter.RecordFile),
                           LocalData.PackEntryEndingWith(GemmaPack, "bodyretarget.json"));
        var redirects = PenumbraModMeta.ReadAllRedirects(root);

        Assert.Equal(new Dictionary<string, string> { ["_top"] = Top, ["_dwn"] = Dwn },
                     RefitCore.BodySlotsOf(redirects, Group, "Neolithe", "0201"));
        // Worn as the reporter left it — the chest's rival on — the legs are cut from it and sized together.
        var plan = RefitCore.PlanOption(redirects, BodyRetargetWriter.ReadRecord(root), Group, true, [RivalTop], Dwn,
                                        NeoDwn, "Neolithe", _ => true, s => TargetsOf(s, Legs));
        Assert.True(plan.Paired);
        Assert.Equal(Merged, plan.Option);
        Assert.Equal(RivalTop, plan.CutFrom);
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-2")]
    public void Legs_then_top_and_pieces_put_on_separately_give_the_same_manifest()
    {
        string first = Gemma("Top first");
        Job(first, Dwn, Job(first, Top, "Neolithe"));
        string second = Gemma("Legs first");
        Job(second, Top, Job(second, Dwn, "Neolithe"));

        static List<(string, string)> Shape(string root)
            => Options(root).SelectMany(o => o.Files.Select(f => (o.Name + "|" + f.Key, f.Value.Replace('\\', '/'))))
                            .OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
        Assert.Equal(Shape(first), Shape(second));
        Assert.Equal(3, Options(second).FindIndex(o => o.Name == Merged));
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-3")]
    public void A_group_sizing_one_piece_keeps_its_per_slot_name()
    {
        string root = Mod("Shirt", "Chest Size",
                          new PenumbraModMeta.FileOption("Neolithe S", Files((Top, "s/top.mdl"), ("chara/x/mt_top.mtrl", "s/top.mtrl"))),
                          new PenumbraModMeta.FileOption("Neolithe M", Files((Top, "m/top.mdl"))));

        var plan = Plan(root, Top, "Neolithe S", group: "Chest Size");

        Assert.False(plan.Paired);
        Assert.Null(plan.Option);
        Assert.Equal("Neolithe S", plan.CutFrom);
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-4")]
    public void A_legs_refit_goes_to_the_group_that_switches_the_legs()
    {
        string root = Mod("Set", "Chest", new PenumbraModMeta.FileOption("S", Files((Top, "s/top.mdl"))));
        PenumbraModMeta.WriteFileOptionGroup(root, 1, "Legs", 0, [new PenumbraModMeta.FileOption("S", Files((Dwn, "s/dwn.mdl")))], 0);
        var redirects = PenumbraModMeta.ReadAllRedirects(root);

        Assert.Equal("Legs", RefitCore.SwitchingGroup(redirects, Dwn, null, RefitCore.SingleGroups(root)));
        Assert.False(RefitCore.PlanOption(redirects, null, "Chest", true, ["S"], Top, "s/top.mdl", "Neolithe",
                                          _ => true, s => TargetsOf(s, Legs)).Paired);
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-5")]
    public void Both_pieces_are_recognised_as_already_refitted()
    {
        string root = Gemma();
        Job(root, Dwn, Job(root, Top, "Neolithe"));

        Assert.True(AutoRefitWatcher.Existing(root, Group, Merged, Top));
        Assert.True(AutoRefitWatcher.Existing(root, Group, Merged, Dwn));

        Job(root, Top, Merged);                                  // the same refit saved again replaces its file only
        Assert.Equal(4, Options(root).Count);
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-6")]
    public void Undoing_the_merged_option_removes_both_refits_and_names_both_pieces()
    {
        string root = Gemma();
        Job(root, Dwn, Job(root, Top, "Neolithe"));

        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;

        Assert.Equal([Merged], result.Removed);
        Assert.Contains(Top, result.Paths);
        Assert.Contains(Dwn, result.Paths);
        Assert.Contains(Dwn, result.Recorded);
        Assert.False(Directory.Exists(Path.Combine(root, BodyRetargetWriter.Subfolder)), "the refits were left behind");
        Assert.Equal(["Neolithe", "Libelly Shaper", "x"], Options(root).Select(o => o.Name));
        Assert.Null(BodyRetargetWriter.ReadRecord(root));
        Assert.Null(AutoRefitUndo.Undo(root, Dwn, NoneSwitched));
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-6")]
    public void An_option_without_legs_never_declines_the_gloves_it_carried()
    {
        // A chest-only size group whose option ships the gloves too: the chest's refit carries them from its cut-from,
        // and undoing it is the chest's undo alone.
        string root = Mod("Shirt", "Chest Size", new PenumbraModMeta.FileOption("S", Files((Top, "s/top.mdl"), (Glv, "s/glv.mdl"))));
        Assert.True(BodyRetargetWriter.Save(root, "Chest Size", Top, "neo", "from", [new BodyRetargetWriter.Refit("Neolithe — S", [1], "s")],
                                            "S", auto: true).Ok);

        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;

        Assert.DoesNotContain(Glv, result.Paths);
        Assert.Equal([Top], AutoRefitUndo.ToDecline(result.Paths, result.Recorded, [Top, Glv]));
    }

    /// <summary>The reporter's mod as it was: one option per piece, each carrying the other piece's original.</summary>
    private string Rivals()
    {
        string root = Gemma();
        foreach (var (path, name) in new[] { (Top, RivalTop), (Dwn, RivalDwn) })
            Assert.True(BodyRetargetWriter.Save(root, Group, path, "hs-Neolithe", "[HS] 9 AM",
                                                [new BodyRetargetWriter.Refit(name, [7], "size")], "Neolithe", auto: true).Ok);
        return root;
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-7")]
    public void A_mod_holding_two_rivals_converges_on_one_merged_option()
    {
        string root = Rivals();
        string rivalTopFile = Option(root, RivalTop).Files[Top];

        // Wearing the chest's rival: the chest draws a refit and is left be; the legs are refitted, cut from it.
        Assert.Equal(Merged, Job(root, Dwn, RivalTop));
        var merged = Option(root, Merged);
        Assert.Equal(rivalTopFile, merged.Files[Top]);
        Assert.True(IsRefit(merged.Files[Dwn]));

        // The chest refit it carries is recorded as its own, so undoing the chest takes it with the chest's rival.
        Assert.Contains(BodyRetargetWriter.ReadRecord(root)!.Options, e => e.Name == Merged && e.GamePath == Top);
        var chest = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;
        Assert.Equal(new[] { RivalTop, Merged }.Order(StringComparer.Ordinal), chest.Removed.Order(StringComparer.Ordinal));
        Assert.Equal(["Neolithe", "Libelly Shaper", "x", RivalDwn], Options(root).Select(o => o.Name));
        Assert.False(File.Exists(Path.Combine(root, rivalTopFile)), "a chest refit nothing publishes was left behind");

        // Undo, then "Refit what I'm wearing now": one Proteus option left.
        AutoRefitUndo.Undo(root, Dwn, NoneSwitched);
        Assert.False(Directory.Exists(Path.Combine(root, BodyRetargetWriter.Subfolder)));
        Job(root, Dwn, Job(root, Top, "Neolithe"));
        Assert.Equal(["Neolithe", "Libelly Shaper", "x", Merged], Options(root).Select(o => o.Name));
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-7")]
    [Trait("Plan", "EDGE-shared-group-one-option-15")]
    public void A_merged_option_cut_from_a_size_made_by_hand_stays_ours()
    {
        // The player wears a chest size they made by hand; the legs' refit is cut from it, its chest carried along.
        string root = Gemma();
        Assert.True(BodyRetargetWriter.Save(root, Group, Top, "neo", "from", [new BodyRetargetWriter.Refit("Mine", [5], "s")],
                                            "Neolithe", auto: false).Ok);
        Assert.Equal(Merged, Job(root, Dwn, "Mine"));

        Assert.DoesNotContain(BodyRetargetWriter.ReadRecord(root)!.Options, e => e.Name == Merged && !e.Auto);
        Assert.True(Plan(root, Top, Merged).Paired, "the merged option reads as made by hand");
        // And the automatic undo of the chest leaves the size made by hand.
        AutoRefitUndo.Undo(root, Top, NoneSwitched);
        Assert.Contains(Options(root), o => o.Name == "Mine");
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-7")]
    public void A_size_made_by_hand_cut_from_an_automatic_refit_is_never_the_automatic_undos()
    {
        // The Studio's Body size tool saving legs cut from one of the automatic refit's options.
        string root = Gemma();
        Assert.True(BodyRetargetWriter.Save(root, Group, Top, "neo", "from", [new BodyRetargetWriter.Refit("Auto top", [1], "s")],
                                            "Neolithe", auto: true).Ok);
        Assert.True(BodyRetargetWriter.Save(root, Group, Dwn, "neo", "from", [new BodyRetargetWriter.Refit("By hand", [2], "s")],
                                            "Auto top", auto: false).Ok);

        Assert.DoesNotContain(BodyRetargetWriter.ReadRecord(root)!.Options, e => e.Name == "By hand" && e.Auto);
        Assert.Equal(["Auto top"], AutoRefitUndo.Undo(root, Top, NoneSwitched)!.Removed);
        Assert.Contains(Options(root), o => o.Name == "By hand");
        Assert.Null(AutoRefitUndo.Undo(root, Dwn, NoneSwitched));
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-7")]
    public void A_piece_saved_over_its_own_path_carries_nothing_there()
    {
        // The chest saved again, cut from an older option that refitted the chest too: the new option's chest is its own.
        string root = Gemma();
        Assert.True(BodyRetargetWriter.Save(root, Group, Top, "neo", "from", [new BodyRetargetWriter.Refit("Old", [1], "s")],
                                            "Neolithe", auto: true).Ok);
        Assert.True(BodyRetargetWriter.Save(root, Group, Top, "neo", "from", [new BodyRetargetWriter.Refit("New", [2], "s")],
                                            "Old", auto: true).Ok);

        string own = Option(root, "New").Files[Top];
        Assert.NotEqual(Option(root, "Old").Files[Top], own);
        Assert.Equal(own.Replace('\\', '/'),
                     BodyRetargetWriter.ReadRecord(root)!.Options.Single(e => e.Name == "New" && e.GamePath == Top).File);
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-8")]
    public void An_author_size_fitting_one_piece_is_refitted_instead_in_either_order()
    {
        // X fits the chest only; X and Y each fit one piece.
        Assert.Null(RefitCore.AuthorOptionForPair([["X"], null]));
        Assert.Null(RefitCore.AuthorOptionForPair([null, ["X"]]));
        Assert.Null(RefitCore.AuthorOptionForPair([["X"], ["Y"]]));

        string root = XY();
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var plan = Plan(root, Top, "Neolithe");
        Assert.True(RefitCore.PairMustFitAll(plan, otherWorn: true, Group, Group));

        // The chest job: X is its size; the legs' is Y, or none of the author's.
        Assert.Null(RefitCore.AuthorOptionForPieces(redirects, Group, Top, "X", plan.PairPaths, (_, _, _) => "Y", NoneExcluded, _ => true));
        Assert.Null(RefitCore.AuthorOptionForPieces(redirects, Group, Top, "X", plan.PairPaths, (_, _, _) => null, NoneExcluded, _ => true));
        // The legs job, the other way round.
        Assert.Null(RefitCore.AuthorOptionForPieces(redirects, Group, Dwn, "Y", Plan(root, Dwn, "Neolithe").PairPaths,
                                                    (_, _, _) => "X", NoneExcluded, _ => true));

        // Refitted instead: both pieces end in the one merged option, in either order, the group never on X.
        Assert.Equal(Merged, Job(root, Dwn, Job(root, Top, "Neolithe")));
        Assert.Equal(["Neolithe", "X", "Y", Merged], Options(root).Select(o => o.Name));
    }

    private static readonly IReadOnlySet<string> NoneExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gemma's group with two more of the author's options: X and Y, each a top and a bottom of its own.</summary>
    private string XY()
        => Mod("Gemma", Group,
               new PenumbraModMeta.FileOption("Neolithe", Files((Top, NeoTop), (Dwn, NeoDwn))),
               new PenumbraModMeta.FileOption("X", Files((Top, "x/top.mdl"), (Dwn, "x/dwn.mdl"))),
               new PenumbraModMeta.FileOption("Y", Files((Top, "y/top.mdl"), (Dwn, "y/dwn.mdl"))));

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-9")]
    public void A_new_size_pair_makes_a_second_merged_option_and_undo_keeps_the_other()
    {
        string root = Gemma();
        Job(root, Dwn, Job(root, Top, "Neolithe"));

        // Another legs size chosen; the player puts the author's option back on and asks again.
        string second = Job(root, Top, "Neolithe", LegsM);
        Assert.NotEqual(Merged, second);
        Assert.Equal(second, Job(root, Dwn, second, LegsM));
        var kept = Option(root, second).Files.Values.Where(IsRefit).ToList();
        Assert.Equal(2, kept.Count);

        Assert.True(BodyRetargetWriter.Undo(root, Group, Merged).Ok);
        Assert.All(kept, rel => Assert.True(File.Exists(Path.Combine(root, rel)), rel + " was deleted"));
    }

    [Fact]
    [Trait("Plan", "AC-shared-group-one-option-10")]
    public void An_author_size_fitting_both_pieces_is_switched_on_and_nothing_is_refitted()
    {
        Assert.Equal("X", RefitCore.AuthorOptionForPair([["X"], ["X"]]));
        Assert.Equal("X", RefitCore.AuthorOptionForPair([["Neolithe", "X"], ["X", "Y"]]));

        // X is the size chosen for the chest and for the legs: it is switched on, in either order.
        string root = XY();
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var pairPaths = Plan(root, Top, "Neolithe").PairPaths;
        Assert.Equal("X", RefitCore.AuthorOptionForPieces(redirects, Group, Top, "X", pairPaths, (_, _, _) => "X", NoneExcluded, _ => true));
        Assert.Equal("X", RefitCore.AuthorOptionForPieces(redirects, Group, Dwn, "X", pairPaths, (_, _, _) => "X", NoneExcluded, _ => true));
        // Asked of the legs' own sizes, by the legs' slot and path.
        RefitCore.AuthorOptionForPieces(redirects, Group, Top, "X", pairPaths, (slot, path, sizes) =>
        {
            Assert.Equal("_dwn", slot);
            Assert.Equal(Dwn, path);
            Assert.Equal(["Neolithe", "X", "Y"], sizes.Select(s => s.Option));
            return "X";
        }, NoneExcluded, _ => true);

        // The legs job, queued before the chest's switched the group to X, works on X's legs and is cut from X.
        var reread = AutoRefitDecisions.Reresolved(new AutoRefitDecisions.Worn(Dwn, "Gemma", NeoDwn),
                                                   new AutoRefitDecisions.Worn(Dwn, "Gemma", "x/dwn.mdl"));
        Assert.Equal(AutoRefitDecisions.Reread.Retarget, reread);
    }

    // ── edges ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-1")]
    public void Cut_from_falls_back_to_OptionOfFile_when_the_selection_does_not_publish_the_path()
    {
        string root = Gemma();
        var redirects = PenumbraModMeta.ReadAllRedirects(root);

        var plan = RefitCore.PlanOption(redirects, null, Group, true, ["x"], Top, NeoTop, "Neolithe", _ => true,
                                        s => TargetsOf(s, Legs));

        Assert.Equal("Neolithe", plan.CutFrom);
        Assert.True(plan.Paired);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-2")]
    public void A_first_refit_alone_carries_the_authors_other_piece()
    {
        string root = Gemma();
        Job(root, Top, "Neolithe");

        Assert.Equal(NeoDwn, Option(root, Merged).Files[Dwn]);
        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;
        Assert.Contains(Dwn, result.Paths);
        Assert.DoesNotContain(Dwn, result.Recorded);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-3")]
    public void A_queued_legs_job_after_the_tops_refit_cuts_from_the_merged_option()
    {
        string root = Gemma();
        string on = Job(root, Top, "Neolithe");

        // The legs still draw Neolithe's file, now through the merged option: the job proceeds, cut from it.
        Assert.Equal(AutoRefitDecisions.Reread.Proceed,
                     AutoRefitDecisions.Reresolved(new AutoRefitDecisions.Worn(Dwn, "Gemma", NeoDwn),
                                                   new AutoRefitDecisions.Worn(Dwn, "Gemma", NeoDwn.Replace('\\', '/'))));
        var plan = Plan(root, Dwn, on);
        Assert.Equal(Merged, plan.CutFrom);
        Assert.Equal(Merged, plan.Option);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-4")]
    public void An_option_publishing_one_piece_in_a_group_that_switches_two_is_not_merged()
    {
        string root = Mod("Split", Group,
                          new PenumbraModMeta.FileOption("A", Files((Top, "a/top.mdl"))),
                          new PenumbraModMeta.FileOption("B", Files((Dwn, "b/dwn.mdl"))));

        Assert.False(RefitCore.PlanOption(PenumbraModMeta.ReadAllRedirects(root), null, Group, true, ["A"], Top,
                                          "a/top.mdl", "Neolithe", _ => true, s => TargetsOf(s, Legs)).Paired);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-5")]
    public void Only_a_chest_size_chosen_means_no_merged_name()
    {
        var plan = Plan(Gemma(), Top, "Neolithe", chosen: s => s == "_top");

        Assert.False(plan.Paired);
        Assert.Null(plan.Option);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-6")]
    public void A_glove_in_the_pair_option_takes_the_merged_name_and_joins_otherwise()
    {
        string root = Mod("Gemma", Group,
                          new PenumbraModMeta.FileOption("Neolithe", Files((Top, NeoTop), (Dwn, NeoDwn), (Glv, "neo/glv.mdl"))));

        var plan = Plan(root, Glv, "Neolithe");
        Assert.True(plan.Paired);
        Assert.Equal(Merged, plan.Option);

        // Without a legs size, today's way: no shared option, the glove named as before for JoinOption to join.
        var unpaired = Plan(root, Glv, "Neolithe", chosen: s => s == "_top");
        Assert.False(unpaired.Paired);
        Assert.Null(unpaired.Option);

        // Worn under the top alone, a glove's author size need fit only the pieces worn with it.
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        Assert.Equal("Neolithe", RefitCore.AuthorOptionForPieces(redirects, Group, Glv, "Neolithe", plan.PairPaths,
                                                                 (slot, _, _) => slot == "_top" ? "Neolithe" : null,
                                                                 NoneExcluded, path => path == Top));
        Assert.Null(RefitCore.AuthorOptionForPieces(redirects, Group, Glv, "Neolithe", plan.PairPaths,
                                                    (slot, _, _) => slot == "_top" ? "Neolithe" : null,
                                                    NoneExcluded, _ => true));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-7")]
    public void A_skin_only_glove_cut_from_the_pair_option_takes_the_merged_name()
    {
        string root = Mod("Gemma", Group,
                          new PenumbraModMeta.FileOption("Neolithe", Files((Top, NeoTop), (Dwn, NeoDwn), (Glv, "neo/glv.mdl"))));

        Assert.Equal(Merged, Plan(root, Glv, "Neolithe").Option);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-8")]
    public void A_merged_name_the_author_already_uses_is_refused()
    {
        string root = Mod("Gemma", Group,
                          new PenumbraModMeta.FileOption("Neolithe", Files((Top, NeoTop), (Dwn, NeoDwn))),
                          new PenumbraModMeta.FileOption(Merged, Files((Top, "theirs/top.mdl"), (Dwn, "theirs/dwn.mdl"))));
        var plan = Plan(root, Top, "Neolithe");

        var saved = BodyRetargetWriter.Save(root, Group, Top, "hs-Neolithe", "[HS] 9 AM",
                                            [new BodyRetargetWriter.Refit(plan.Option, [1], "size")], plan.CutFrom, auto: true);

        Assert.False(saved.Ok);
        Assert.False(Directory.Exists(Path.Combine(root, BodyRetargetWriter.Subfolder)));
    }

    [Theory]
    [Trait("Plan", "EDGE-shared-group-one-option-9")]
    [InlineData(true)]
    [InlineData(false)]
    public void Undo_keeps_a_refit_file_another_option_still_publishes(bool authorGroup)
    {
        string root = authorGroup ? Gemma() : Mod("Plain", "Other", new PenumbraModMeta.FileOption("a", Files(("chara/a.mdl", "a.mdl"))));
        string group = authorGroup ? Group : "Body — own";
        string? cutFrom = authorGroup ? "Neolithe" : null;
        Assert.True(BodyRetargetWriter.Save(root, group, Top, "neo", "from", [new BodyRetargetWriter.Refit("P1", [1], "s")],
                                            cutFrom, auto: true).Ok);
        // P2 cut from P1: it carries P1's chest refit.
        Assert.True(BodyRetargetWriter.Save(root, group, Dwn, "neo", "from", [new BodyRetargetWriter.Refit("P2", [2], "s")],
                                            "P1", auto: true).Ok);
        string shared = Option(root, "P1", group).Files[Top];
        Assert.Equal(shared, Option(root, "P2", group).Files[Top]);

        Assert.True(BodyRetargetWriter.Undo(root, group, "P1").Ok);
        Assert.True(File.Exists(Path.Combine(root, shared)), "a file P2 still publishes was deleted");
        Assert.True(BodyRetargetWriter.Undo(root, group, "P2").Ok);
        Assert.False(File.Exists(Path.Combine(root, shared)));
        Assert.False(Directory.Exists(Path.Combine(root, BodyRetargetWriter.Subfolder)));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-9")]
    public void Undo_deletes_nothing_when_it_cannot_tell_what_is_published()
    {
        const string refit = "Body Retarget/M/chara/equipment/e6186/model/c0201e6186_top.mdl";
        var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Body Retarget/Other/x.mdl" };

        Assert.True(BodyRetargetWriter.Deletable(refit, published));
        Assert.False(BodyRetargetWriter.Deletable(refit.Replace('/', '\\'), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { refit }));
        Assert.False(BodyRetargetWriter.Deletable(refit, null));          // the manifest could not be read
        Assert.False(BodyRetargetWriter.Deletable(NeoTop, published));   // the author's file, never ours to delete
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-10")]
    public void The_second_save_moves_no_option()
    {
        string root = Gemma();
        string on = Job(root, Top, "Neolithe");
        var names = Options(root).Select(o => o.Name).ToList();
        string? byDefault = PenumbraModMeta.SingleDefault(root, Group);

        Job(root, Dwn, on);

        Assert.Equal(names, Options(root).Select(o => o.Name));
        Assert.Equal(byDefault, PenumbraModMeta.SingleDefault(root, Group));
        Assert.Equal(1, PenumbraModMeta.GroupCount(root));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-11")]
    public void A_partial_merged_option_undone_declines_the_worn_other_piece()
    {
        string root = Gemma();
        Job(root, Top, "Neolithe");

        var result = AutoRefitUndo.Undo(root, Top, NoneSwitched)!;

        Assert.Contains(Dwn, AutoRefitUndo.ToDecline(result.Paths, result.Recorded, [Top, Dwn]));
        Assert.Null(AutoRefitUndo.Undo(root, Dwn, NoneSwitched));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-12")]
    public void A_file_changed_since_the_look_is_classified()
    {
        var queued = new AutoRefitDecisions.Worn(Dwn, "Gemma", NeoDwn);
        AutoRefitDecisions.Reread Now(string? dir, string? rel)
            => AutoRefitDecisions.Reresolved(queued, new AutoRefitDecisions.Worn(Dwn, dir, rel));

        Assert.Equal(AutoRefitDecisions.Reread.Proceed, Now("gemma", NeoDwn.Replace('\\', '/').ToUpperInvariant()));
        Assert.Equal(AutoRefitDecisions.Reread.Retarget, Now("Gemma", LibDwn));
        Assert.Equal(AutoRefitDecisions.Reread.OwnRefit, Now("Gemma", "Body Retarget/M/chara/x_dwn.mdl"));
        Assert.Equal(AutoRefitDecisions.Reread.Abandon, Now("Another mod", NeoDwn));
        Assert.Equal(AutoRefitDecisions.Reread.Abandon, Now(null, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-13")]
    public void Only_models_of_the_drawn_race_count()
    {
        const string manDwn = "chara/equipment/e6186/model/c0101e6186_dwn.mdl";
        string root = Mod("Gemma", Group, new PenumbraModMeta.FileOption("Neolithe", Files((Top, NeoTop), (manDwn, "m/dwn.mdl"))));
        var redirects = PenumbraModMeta.ReadAllRedirects(root);

        Assert.Equal(["_top"], RefitCore.BodySlotsOf(redirects, Group, "Neolithe", "0201").Keys);
        Assert.False(Plan(root, Top, "Neolithe").Paired);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-14")]
    public void A_multi_choice_group_is_never_paired()
        => Assert.False(Plan(Gemma(), Top, "Neolithe", single: false).Paired);

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-15")]
    public void A_hand_made_size_holding_the_merged_name_is_left_alone()
    {
        string root = Gemma();
        Assert.True(BodyRetargetWriter.Save(root, Group, Top, "neo", "from", [new BodyRetargetWriter.Refit(Merged, [9], "s")],
                                            "Neolithe", auto: false).Ok);
        string file = Option(root, Merged).Files[Top];

        var plan = Plan(root, Top, "Neolithe");

        Assert.False(plan.Paired);
        Assert.Null(plan.Option);
        Assert.NotNull(plan.Note);
        Assert.Equal(file, Option(root, Merged).Files[Top]);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-16")]
    public void Geometrics_chest_size_is_unchanged()
    {
        // Every option serves the top and the jacket, both chest pieces: two models, but no legs.
        const string jacket = "chara/equipment/e6187/model/c0201e6187_top.mdl";
        string root = Mod("Geometric", "Chest Size",
                          new PenumbraModMeta.FileOption("S", Files((Top, "s/top.mdl"), (jacket, "s/jacket.mdl"))));

        Assert.True(RefitCore.IsSharedSingleGroup(PenumbraModMeta.ReadAllRedirects(root), null, "Chest Size",
                                                  RefitCore.SingleGroups(root)));
        var plan = Plan(root, Top, "S", group: "Chest Size");
        Assert.False(plan.Paired);
        Assert.Null(plan.Option);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-17")]
    public void A_baked_garment_pairs_by_its_drawn_path()
    {
        const string manTop = "chara/equipment/e6186/model/c0101e6186_top.mdl";
        const string manDwn = "chara/equipment/e6186/model/c0101e6186_dwn.mdl";
        string root = Mod("Gemma", Group, new PenumbraModMeta.FileOption("Neolithe", Files((manTop, "m/top.mdl"), (manDwn, "m/dwn.mdl"))));

        var plan = RefitCore.PlanOption(PenumbraModMeta.ReadAllRedirects(root), null, Group, true, ["Neolithe"], manTop,
                                        "m/top.mdl", "Neolithe", _ => true, s => TargetsOf(s, Legs));
        Assert.True(plan.Paired);

        // Saved at the wearer's own race's path, carrying the man's files it was cut from.
        Assert.True(BodyRetargetWriter.Save(root, Group, Top, "neo", "from", [new BodyRetargetWriter.Refit(plan.Option, [1], "s")],
                                            plan.CutFrom, auto: true).Ok);
        var merged = Option(root, Merged);
        Assert.True(IsRefit(merged.Files[Top]));
        Assert.Equal("m/dwn.mdl", merged.Files[manDwn]);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-18")]
    public void A_merged_option_put_back_keeps_the_files_it_was_saved_with()
    {
        string root = Gemma();
        Job(root, Top, "Neolithe");
        var files = Option(root, Merged).Files.ToDictionary(f => f.Key, f => f.Value);

        Assert.True(AutoRefitWatcher.Existing(root, Group, Merged, Top));
        Assert.Equal(files, Option(root, Merged).Files);
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-19")]
    public void Undo_never_declines_an_unworn_unrecorded_piece()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Top, Dwn };
        var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Top };

        Assert.Equal([Top], AutoRefitUndo.ToDecline(paths, recorded, [Top]));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-20")]
    public void A_grid_option_fitting_both_sizes_is_found_through_shared_files()
    {
        // Z1 and Z2 share the chest H; only Z2 carries legs B. AuthorSize names Z1 for the chest — the first with H.
        string root = Mod("Grid", Group,
                          new PenumbraModMeta.FileOption("Z1", Files((Top, "h/top.mdl"), (Dwn, "a/dwn.mdl"))),
                          new PenumbraModMeta.FileOption("Z2", Files((Top, "h/top.mdl"), (Dwn, "b/dwn.mdl"))),
                          new PenumbraModMeta.FileOption("M", Files((Top, "Body Retarget/M/top.mdl"), (Dwn, "b/dwn.mdl"))));
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "M" };

        var chest = RefitCore.OptionsWithFile(redirects, Group, Top, "h\\top.mdl", ours);
        var legs = RefitCore.OptionsWithFile(redirects, Group, Dwn, "b/dwn.mdl", ours);

        Assert.Equal(["Z1", "Z2"], chest);
        Assert.Equal(["Z2"], legs);
        Assert.Equal("Z2", RefitCore.AuthorOptionForPair([chest, legs]));

        // As the chest job asks it: the chest's size named Z1, the legs' Z2 — and our M is never one of the author's.
        var pairPaths = new Dictionary<string, string> { ["_top"] = Top, ["_dwn"] = Dwn };
        Assert.Equal("Z2", RefitCore.AuthorOptionForPieces(redirects, Group, Top, "Z1", pairPaths, (_, _, sizes) =>
        {
            Assert.DoesNotContain(sizes, s => s.Option == "M");
            return "Z2";
        }, ours, _ => true));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-21")]
    public void A_retargeted_request_keeps_the_walks_outcome_key()
    {
        var walked = new AutoRefitDecisions.Worn(Top, "Gemma", NeoTop);
        var now = new AutoRefitDecisions.Worn(Top, "Gemma", LibTop);

        var keys = AutoRefitWatcher.OutcomeKeys("_top", now, walked);

        Assert.Equal([AutoRefitWatcher.OutcomeKey("_top", now), AutoRefitWatcher.OutcomeKey("_top", walked)], keys);
        Assert.Equal([AutoRefitWatcher.OutcomeKey("_top", now)], AutoRefitWatcher.OutcomeKeys("_top", now, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-22")]
    public void Game_gear_keeps_todays_order()
    {
        // The game's own gear is never in an author's group: read again it is the game's still, and goes on as today,
        // with no pair plan (PlanPair has none for it) and so no pair check on its author sizes.
        var game = new AutoRefitDecisions.Worn(Top, null, null);
        Assert.Equal(AutoRefitDecisions.Reread.Proceed, AutoRefitDecisions.Reresolved(game, game));
        Assert.False(RefitCore.PairMustFitAll(null, otherWorn: true, null, Group));
    }

    [Fact]
    [Trait("Plan", "EDGE-shared-group-one-option-23")]
    public void With_the_other_piece_not_worn_the_authors_exact_size_is_switched_on()
    {
        var pair = new Dictionary<string, string> { ["_top"] = Top, ["_dwn"] = Dwn };

        Assert.False(RefitCore.OtherPieceWorn(pair, Top, new HashSet<string> { Top }));
        Assert.True(RefitCore.OtherPieceWorn(pair, Top, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Top, Dwn.ToUpperInvariant() }));
        Assert.False(RefitCore.OtherPieceWorn(pair, Top, null));

        // With the legs not worn the pair check stays shut: the author's size for the chest alone is switched on.
        var plan = Plan(Gemma("Gate"), Top, "Neolithe");
        Assert.False(RefitCore.PairMustFitAll(plan, otherWorn: false, Group, Group));
        Assert.True(RefitCore.PairMustFitAll(plan, otherWorn: true, Group, Group));
        Assert.False(RefitCore.PairMustFitAll(plan, otherWorn: true, Group, "Another group"));

        // The legs put on later are cut from the author's option switched on for the chest, its chest and all.
        string root = Mod("Gemma", Group,
                          new PenumbraModMeta.FileOption("Neolithe", Files((Top, NeoTop), (Dwn, NeoDwn))),
                          new PenumbraModMeta.FileOption("X", Files((Top, "x/top.mdl"), (Dwn, "x/dwn.mdl"))));
        Job(root, Dwn, "X");
        Assert.Equal("x/top.mdl", Option(root, Merged).Files[Top]);
    }
}
