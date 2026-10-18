using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What every refit that does not tuck the new skin under the cloth wrote before the tuck existed, pinned as SHA-256 of
/// the planned model on <c>test</c> 054b7d3 (each value taken from two separate test runs that agreed). Equality of two
/// runs in one process proves nothing about a change to the shared cut; these do.
/// </summary>
public class SkinTuckGoldenTests(ITestOutputHelper output)
{
    private static readonly HashSet<int> NoneHeld = [];

    /// <summary>A game top as it was before the neckline tuck and the deep clear.</summary>
    private static readonly BodyRetarget.Tuning Before = new(NoTuck: true, NoDeepClear: true, NoKeepStandoff: true);

    /// <summary>A game top with the tuck as change (a) made it: no deep clear, no open skin.</summary>
    private static readonly BodyRetarget.Tuning TuckOnly = new(NoDeepClear: true, NoOpenSkin: true, NoKeepStandoff: true);

    /// <summary>A game top as it was before the standoff pass: everything else on.</summary>
    private static readonly BodyRetarget.Tuning NoPull = new(NoKeepStandoff: true);

    private string Pinned(byte[] model, string expected)
    {
        string actual = SkinTuckSheet.Sha(model);
        output.WriteLine($"sha {actual}");
        Assert.Equal(expected, actual);
        return actual;
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void Studio_cut_plans_as_before()
    {
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.Plan(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", held: NoneHeld,
                                     replaceSkin: true, acrossBodies: true, clearBody: false, cutHidden: true,
                                     keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
        Pinned(plan.Model, StudioCut);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void Studio_cut_and_clear_tucks_a_top()
    {
        // The Body size tool's call for a top with "Cut hidden skin" and "Clear body" both on: it tucks, as the automatic
        // refit of the game's own top does. Without the tuck, it is what it was.
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        BodyRetarget.Planned Studio() => BodyRetarget.Plan(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())],
                                                           "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true,
                                                           clearBody: true, cutHidden: true,
                                                           keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)),
                                                           tuckSlot: RefitCore.TuckSlot(true, "_top"));
        Assert.True(Studio().Report.Swap?.Tucked > 0, "the Body size tool's cut-and-clear top did not tuck");
        Pinned(BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoTuck: true), Studio).Model, StudioCutAndClear);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-5")]
    public void A_cut_PullToEdge_moves_plans_as_before()
    {
        // Cloth 1 mm over the strip past the author's edge: the kept corners there are pulled back to it.
        var body = SkinTuckSheet.Body();
        var bytes = SkinTuckSheet.Top(_ => 0.001f, clothFrom: 4);
        var rebuilt = BodyRetarget.SwapSkin(bytes, [SkinTuckSheet.Resized(body)], out var report)!;
        output.WriteLine($"{report}");

        var bodyAt = Positions(body);
        var read = ModelPartReader.Read(rebuilt)!;
        bool moved = read.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                         .SelectMany(p => p.Triangles).Distinct()
                         .Any(v => !bodyAt.Contains(Key(read, v)));
        Assert.True(moved, "nothing was pulled back: the fixture no longer exercises the pull");
        Pinned(rebuilt, PullMoves);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void A_game_top_without_the_tuck_plans_as_before()
    {
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.WithTuning(Before,
            () => RefitCore.PlanRefit(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", across: true,
                                      gameGear: true, out _));
        Pinned(plan.Model, GameTopUntucked);
    }

    // ── a top whose cloth is buried 12 mm in a body that grew a 20 mm bust under it: where the deep clear would act ──
    // Pinned on 72dde80 (change (a)'s final commit), before any of the deep clear's production code.

    private static (byte[] Bytes, ModelParts Garment, BodyRetarget.SlotPair Pair, IReadOnlyList<IReadOnlyCollection<int>> Pieces) GrownBust()
    {
        var bytes = SkinTuckSheet.BuriedTop(0.012f);
        var garment = ModelPartReader.Read(bytes)!;
        return (bytes, garment, SkinTuckSheet.Grown(SkinTuckSheet.Bust(0f), SkinTuckSheet.Bust(0.02f)),
                RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-9")]
    public void Studio_cut_on_a_grown_bust_plans_as_before()
    {
        var (bytes, garment, pair, pieces) = GrownBust();
        var plan = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true,
                                     clearBody: false, cutHidden: true, keepShape: pieces);
        Pinned(plan.Model, GrownStudioCut);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-9")]
    public void A_mods_garment_on_a_grown_bust_plans_as_before()
    {
        var (bytes, garment, pair, _) = GrownBust();
        var plan = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _);
        Pinned(plan.Model, GrownModGarment);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-9")]
    public void An_accessory_over_a_grown_bust_plans_as_before()
    {
        var (bytes, garment, pair, _) = GrownBust();
        var plan = RefitCore.PlanAccessoryRefit(garment, bytes, [pair], "_top", across: true, out _);
        Pinned(plan.Model, GrownAccessory);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-9")]
    public void KeepEnds_on_a_grown_bust_plans_as_before()
    {
        // KeepEnds' call, a glove reaching up the arm with the chest as its limb, the body cleared as the Studio may ask.
        var (bytes, garment, pair, pieces) = GrownBust();
        var plan = BodyRetarget.Plan(garment, bytes, [pair], "_glv", held: NoneHeld, replaceSkin: true, acrossBodies: true,
                                     clearBody: true, cutHidden: true, keepShape: pieces, swapSlot: "_top");
        Pinned(plan.Model, GrownKeepEnds);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-9")]
    public void A_size_set_plan_on_a_grown_bust_plans_as_before()
    {
        // The every-size refit's call (SizeSetRefit): cut, never cleared.
        var (bytes, garment, pair, _) = GrownBust();
        var plan = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true,
                                     acrossBodies: true, clearBody: false, cutHidden: true, keepShape: []);
        Pinned(plan.Model, GrownSizeSet);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-24")]
    public void Studio_cut_and_clear_on_a_grown_bust_never_deep_clears()
    {
        // The Body size tool's top with "Cut hidden skin" and "Clear body": it tucks, and never moves the cloth further.
        var (bytes, garment, pair, pieces) = GrownBust();
        BodyRetarget.Planned Studio() => BodyRetarget.Plan(garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true,
                                                           acrossBodies: true, clearBody: true, cutHidden: true,
                                                           keepShape: pieces, tuckSlot: RefitCore.TuckSlot(true, "_top"));
        Pinned(BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoTuck: true), Studio).Model, GrownStudioCutAndClear);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-22")]
    public void A_game_top_on_a_grown_bust_before_the_tuck_plans_as_before()
    {
        var (bytes, garment, pair, _) = GrownBust();
        var plan = BodyRetarget.WithTuning(Before,
            () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _));
        Pinned(plan.Model, GrownGameTopBefore);
    }

    // ── the same paths on coarse cloth buried 12 mm, which the usual clear's spread does not drag out: where the deep clear
    // DOES act on a game top (the fine sheet above it never reaches). Pinned on 78eefd4 — (a)'s production code with the
    // deep clear's knobs still inert — in a clean worktree holding only this SkinTuckSheet.cs and a theory printing each
    // hash, two runs agreeing. ──

    private static (byte[] Bytes, ModelParts Garment, BodyRetarget.SlotPair Pair, IReadOnlyList<IReadOnlyCollection<int>> Pieces) CoarseGrownBust()
    {
        var bytes = SkinTuckSheet.CoarseBuriedTop(0.012f);
        var garment = ModelPartReader.Read(bytes)!;
        return (bytes, garment, SkinTuckSheet.Grown(SkinTuckSheet.Bust(0f), SkinTuckSheet.Bust(0.02f)),
                RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
    }

    public static TheoryData<string, string> CoarseGrownPaths => new()
    {
        { "studio cut", CoarseStudioCut },
        { "mod garment", CoarseModGarment },
        { "accessory", CoarseAccessory },
        { "keep ends", CoarseKeepEnds },
        { "size set", CoarseSizeSet },
        { "studio cut and clear, untucked", CoarseStudioCutAndClear },
        { "game top before", CoarseGameTopBefore },
        { "game top, deep clear off", CoarseGameTopTuckOnly },
    };

    [Theory]
    [MemberData(nameof(CoarseGrownPaths))]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-9")]
    [Trait("Plan", "EDGE-neckline-large-bust-24")]
    [Trait("Plan", "EDGE-neckline-large-bust-22")]
    public void Every_other_path_on_a_coarse_grown_bust_plans_as_before(string path, string expected)
    {
        var (bytes, garment, pair, pieces) = CoarseGrownBust();
        BodyRetarget.Planned Studio(bool clear) => BodyRetarget.Plan(
            garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true, clearBody: clear,
            cutHidden: true, keepShape: pieces, tuckSlot: clear ? RefitCore.TuckSlot(true, "_top") : null);
        var plan = path switch
        {
            "studio cut" => Studio(false),
            "mod garment" => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _),
            "accessory" => RefitCore.PlanAccessoryRefit(garment, bytes, [pair], "_top", across: true, out _),
            "keep ends" => BodyRetarget.Plan(garment, bytes, [pair], "_glv", held: NoneHeld, replaceSkin: true,
                                             acrossBodies: true, clearBody: true, cutHidden: true, keepShape: pieces,
                                             swapSlot: "_top"),
            "size set" => BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true,
                                            acrossBodies: true, clearBody: false, cutHidden: true, keepShape: []),
            "studio cut and clear, untucked" => BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoTuck: true), () => Studio(true)),
            "game top before" => BodyRetarget.WithTuning(Before,
                () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _)),
            // The tuck on, the deep clear's knob off: where the deep clear would act, the knob must hold it back.
            "game top, deep clear off" => BodyRetarget.WithTuning(TuckOnly,
                () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _)),
            _ => throw new System.ArgumentException(path),
        };
        Pinned(plan.Model, expected);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-9")]
    [Trait("Plan", "EDGE-neckline-large-bust-24")]
    public void The_coarse_grown_bust_is_where_a_game_top_deep_clears_and_the_Studio_does_not()
    {
        // The goldens above are not vacuous: on this fixture the game top's auto refit does clear deep, and the Studio's
        // cut and clear with the tuck on still does not.
        var (bytes, garment, pair, pieces) = CoarseGrownBust();
        var game = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _);
        Assert.True(game.Report.DeepCleared > 0);
        var studio = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true,
                                       clearBody: true, cutHidden: true, keepShape: pieces,
                                       tuckSlot: RefitCore.TuckSlot(true, "_top"));
        Assert.Equal(0, studio.Report.DeepCleared);
    }

    // ── a neckline the refit carries off the upper chest (SkinTuckSheet.FloatingNeckline): where the standoff pass will
    // act. Pinned on 4432f7c + the inert push trace (f0c2e0a), before any of the pass's production code. ──

    public static TheoryData<string, string> FloatingPaths => new()
    {
        { "studio cut", FloatStudioCut },
        { "mod garment", FloatModGarment },
        { "accessory", FloatAccessory },
        { "keep ends", FloatKeepEnds },
        { "size set", FloatSizeSet },
        { "studio cut and clear", FloatStudioCutAndClear },
    };

    [Theory]
    [MemberData(nameof(FloatingPaths))]
    [Trait("Plan", "AC-neckline-float-6")]
    public void Every_other_path_on_a_floating_neckline_plans_as_before(string path, string expected)
    {
        var (bytes, pair) = SkinTuckSheet.FloatingNeckline();
        var garment = ModelPartReader.Read(bytes)!;
        var pieces = RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment));
        var plan = path switch
        {
            "studio cut" => BodyRetarget.Plan(garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true,
                                              acrossBodies: true, clearBody: false, cutHidden: true, keepShape: pieces),
            "mod garment" => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _),
            "accessory" => RefitCore.PlanAccessoryRefit(garment, bytes, [pair], "_top", across: true, out _),
            "keep ends" => BodyRetarget.Plan(garment, bytes, [pair], "_glv", held: NoneHeld, replaceSkin: true,
                                             acrossBodies: true, clearBody: true, cutHidden: true, keepShape: pieces,
                                             swapSlot: "_top"),
            "size set" => BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true,
                                            acrossBodies: true, clearBody: false, cutHidden: true, keepShape: []),
            "studio cut and clear" => BodyRetarget.Plan(garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true,
                                                        acrossBodies: true, clearBody: true, cutHidden: true,
                                                        keepShape: pieces, tuckSlot: RefitCore.TuckSlot(true, "_top")),
            _ => throw new System.ArgumentException(path),
        };
        Pinned(plan.Model, expected);
    }

    public static TheoryData<string, string> FloatingGameTops => new()
    {
        { "fine", FloatGameTop },
        { "coarse", FloatGameTopCoarse },
        { "layered", FloatGameTopLayered },
        { "coarse grown bust", CoarseGrownGameTop },
    };

    [Theory]
    [MemberData(nameof(FloatingGameTops))]
    [Trait("Plan", "EDGE-neckline-float-21")]
    public void A_game_top_without_the_standoff_pass_plans_as_before(string fixture, string expected)
    {
        var (bytes, pair) = fixture switch
        {
            "fine" => SkinTuckSheet.FloatingNeckline(),
            "coarse" => SkinTuckSheet.FloatingNeckline(coarse: true),
            "layered" => SkinTuckSheet.FloatingNeckline(lining: 0.004f),
            _ => (CoarseGrownBust().Bytes, CoarseGrownBust().Pair),
        };
        var garment = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.WithTuning(NoPull,
            () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _));
        Pinned(plan.Model, expected);
    }

    // ── change (a)'s tuck as it stands at 72dde80: what (b)'s open skin and deep clear, switched off, must leave alone ──

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-22")]
    public void A_game_tops_tuck_on_a_grown_bust_is_as_change_a_made_it()
    {
        var (bytes, garment, pair, _) = GrownBust();
        var plan = BodyRetarget.WithTuning(TuckOnly,
            () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _));
        Pinned(plan.Model, GrownGameTopTucked);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-22")]
    public void A_game_tops_tuck_on_the_loose_sheet_is_as_change_a_made_it()
    {
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.WithTuning(TuckOnly,
            () => RefitCore.PlanRefit(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", across: true,
                                      gameGear: true, out _));
        Pinned(plan.Model, LooseGameTopTucked);
    }

    [Fact]
    [Trait("Plan", "EDGE-neckline-large-bust-22")]
    public void The_Studio_cut_and_clear_tuck_is_as_change_a_made_it()
    {
        // Without the open skin only: the Studio never deep-clears whatever the knobs say, and that is left to show here.
        var noOpen = new BodyRetarget.Tuning(NoOpenSkin: true);
        var bytes = SkinTuckSheet.LooseTop();
        var garment = ModelPartReader.Read(bytes)!;
        var loose = BodyRetarget.WithTuning(noOpen, () => BodyRetarget.Plan(
            garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", held: NoneHeld, replaceSkin: true,
            acrossBodies: true, clearBody: true, cutHidden: true,
            keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)),
            tuckSlot: RefitCore.TuckSlot(true, "_top")));
        Pinned(loose.Model, LooseStudioTucked);

        var (gBytes, gGarment, pair, pieces) = GrownBust();
        var grown = BodyRetarget.WithTuning(noOpen, () => BodyRetarget.Plan(
            gGarment, gBytes, [pair], "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true, clearBody: true,
            cutHidden: true, keepShape: pieces, tuckSlot: RefitCore.TuckSlot(true, "_top")));
        Assert.Equal(0, grown.Report.DeepCleared);
        Pinned(grown.Model, GrownStudioTucked);
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-6")]
    [Trait("Plan", "EDGE-neckline-large-bust-25")]
    public void Studio_cut_through_the_chord_sag_plans_as_before()
    {
        // A coarse two-triangle skin over a body with a 6 mm bulge under it: the cut keeps the bulge through its chord-sag
        // allowance, not its 4 mm reach — the branch the open-skin rule shares with it.
        var body = SkinTuckSheet.Body(z: (r, c) => r is 11 or 12 && c is >= 3 and <= 5 ? 0.006f : 0f);
        var bytes = SkinTuckSheet.CoarseSkinTop();
        var garment = ModelPartReader.Read(bytes)!;
        var laid = new BodySurface(garment, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(garment)));
        var edges = BodyRetarget.SkinEdges.Of(garment);
        var read = ModelPartReader.Read(body)!;
        bool sagged = Enumerable.Range(0, read.Positions.Length / 3).Any(v =>
        {
            var p = new System.Numerics.Vector3(read.Positions[v * 3], read.Positions[v * 3 + 1], read.Positions[v * 3 + 2]);
            return BodyRetarget.Covers(laid, p, edges) && laid.Nearest(p, 0.05f, out var hit) && hit.Distance > BodyRetarget.CutReach;
        });
        Assert.True(sagged, "no body vertex is kept through the chord sag: the fixture misses the branch");

        var plan = BodyRetarget.Plan(garment, bytes, [SkinTuckSheet.Resized(body)], "_top", held: NoneHeld, replaceSkin: true,
                                     acrossBodies: true, clearBody: false, cutHidden: true,
                                     keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
        Pinned(plan.Model, ChordSagStudioCut);
    }

    // ── on the game's own files and the user's body mods: inconclusive when an input is not the one pinned ──

    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";

    /// <summary>
    /// Whether the input files are the ones pinned. A game patch or a body mod update is not a regression: the test says
    /// INCONCLUSIVE and checks nothing (xunit 2 has no run-time skip without another package).
    /// </summary>
    private bool SameInputs(params (string Name, byte[] Bytes, string Sha)[] inputs)
    {
        foreach (var (name, bytes, sha) in inputs)
            if (SkinTuckSheet.Sha(bytes) != sha)
            {
                output.WriteLine($"INCONCLUSIVE: {name} is not the file the golden was pinned on");
                return false;
            }
        return true;
    }

    private (byte[] Garment, string Source, string Target, BodyRetarget.SlotPair Pair, UVRemapService Uv)
        Setup(string garmentName, string rel)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = VanillaGearRefitLocalTests.TopOf(data, garmentName);
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_top", "0201")[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(Neolithe));
        var option = mod.For("_top", "0201").First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel));
        string target = mod.PathOf(option);
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", false, null, null, uv, out var pair));
        foreach (var (name, b) in new[] { ("garment", bytes), ("source", File.ReadAllBytes(source)), ("target", File.ReadAllBytes(target)) })
            output.WriteLine($"input {name} sha {SkinTuckSheet.Sha(b)}");
        return (bytes, source, target, pair, uv);
    }

    [LocalDataFact(LocalData.GameData, Neolithe)]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    [Trait("Plan", "AC-open-patch-authored-4")]
    public void A_modders_cut_on_Neolithe_plans_as_before()
    {
        var (bytes, source, target, pair, _) = Setup("Neo-Ishgardian Top of Maiming", "neobelly chest - smallclothes/sfw m.mdl");
        if (!SameInputs(("garment", bytes, MaimingSha), ("source", File.ReadAllBytes(source), VanillaTopSha),
                        ("target", File.ReadAllBytes(target), NeobellyMSha))) return;
        var garment = ModelPartReader.Read(bytes)!;
        // As it was pinned: before cloth past the top's own open skin edge was read against the body it was made on
        // (build 1323), which takes 80 of its points in — see A_modders_cut_on_Neolithe_plans_as_fitted.
        var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoOpenEdgeFit: true), () => MaimingCut(garment, bytes, pair));
        Pinned(plan.Model, MaimingStudioCut);
    }

    /// <summary>
    /// The same cut with the made-on body's reading (<see cref="BodyRetarget.TargetBody.Fitted"/>), pinned on build 1323.
    /// Measured against the refit above: cloth inside the new body 40 → 14 points, folds 0 → 0, nothing turned over, and
    /// stretched against the game's own top 4 → 10 edges across the bust — where it used to pass through it. Accepted
    /// (build 1323); judged in OpenPatchGameTopLocalTests.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Neolithe)]
    [Trait("Plan", "AC-open-patch-authored-9")]
    public void A_modders_cut_on_Neolithe_plans_as_fitted()
    {
        var (bytes, source, target, pair, _) = Setup("Neo-Ishgardian Top of Maiming", "neobelly chest - smallclothes/sfw m.mdl");
        if (!SameInputs(("garment", bytes, MaimingSha), ("source", File.ReadAllBytes(source), VanillaTopSha),
                        ("target", File.ReadAllBytes(target), NeobellyMSha))) return;
        Pinned(MaimingCut(ModelPartReader.Read(bytes)!, bytes, pair).Model, MaimingStudioCutFitted);
    }

    private static BodyRetarget.Planned MaimingCut(ModelParts garment, byte[] bytes, BodyRetarget.SlotPair pair)
        => BodyRetarget.Plan(garment, bytes, [pair], "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true,
                             clearBody: false, cutHidden: true,
                             keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));

    [LocalDataFact(LocalData.GameData, Neolithe)]
    [Trait("Plan", "AC-neckline-skin-tuck-3")]
    public void The_Dirndl_game_refit_without_the_tuck_plans_as_before()
    {
        var (bytes, source, target, pair, uv) = Setup("Dirndl's Bodice", "extra chest - smallclothes/nsfw almond xs.mdl");
        if (!SameInputs(("garment", bytes, DirndlSha), ("source", File.ReadAllBytes(source), VanillaTopSha),
                        ("target", File.ReadAllBytes(target), AlmondXsSha))) return;
        var garment = ModelPartReader.Read(bytes)!;
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        // The new body's piercings carried, as every refit did when this was pinned.
        var plan = BodyRetarget.WithTuning(Before,
            () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _,
                                      carryPiercings: true));
        Pinned(RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log), DirndlGameRefit);
    }

    private static HashSet<(int, int, int)> Positions(byte[] model)
    {
        var read = ModelPartReader.Read(model)!;
        return [.. Enumerable.Range(0, read.Positions.Length / 3).Select(v => Key(read, v))];
    }

    private static (int, int, int) Key(ModelParts m, int v)
        => ((int)System.MathF.Round(m.Positions[v * 3] * 1e5f), (int)System.MathF.Round(m.Positions[v * 3 + 1] * 1e5f),
            (int)System.MathF.Round(m.Positions[v * 3 + 2] * 1e5f));

    // Pinned on test 054b7d3.
    private const string StudioCut = "E3DA5ACD964F2FD5ACC8E7BC87EA163456C2955C9035B9B864F755DD3F9742F7";
    private const string StudioCutAndClear = "E3DA5ACD964F2FD5ACC8E7BC87EA163456C2955C9035B9B864F755DD3F9742F7";
    private const string PullMoves = "7859DFEE4147CB00F617F9325A92C1A76C1E23150EB68A1ED516F79DBD892A81";
    private const string GameTopUntucked = "E3DA5ACD964F2FD5ACC8E7BC87EA163456C2955C9035B9B864F755DD3F9742F7";
    private const string MaimingStudioCut = "6B0216E852B601965EDB47C63572FCA53FFCDB7C80CC1E36CB821DCDB30DADE1";
    // Pinned on build 1323.
    private const string MaimingStudioCutFitted = "28EF6EB3F30337E37E268FC8D5DC9330841188A93D15514B32D8E89FD027C703";
    private const string DirndlGameRefit = "1B6456AF7163D39F5EDB5B2E38EE8F50B35D1B15BDCE0DDEF414BD4FDC5FD61B";

    // Pinned on 72dde80, change (a)'s final commit.
    private const string GrownStudioCut = "B45430CC61D76DE199C6EF53366368A4F368C578C31535BD8E0C8A9E8F800AD9";
    private const string GrownModGarment = "FF0DAF950878FC7F1544B73893595DE4D4E9B0272EEB04542D88CCA9AFFA3440";
    private const string GrownAccessory = "F0A18569C9FB1E999FFD368CCEF1963E2913F494D55348455F3A392797CED19C";
    private const string GrownKeepEnds = "A613999B0DE8E1C453015A53642B3C8F6F2D113D74DDD2E0C017BB866C01D50A";
    private const string GrownSizeSet = "B45430CC61D76DE199C6EF53366368A4F368C578C31535BD8E0C8A9E8F800AD9";
    private const string GrownStudioCutAndClear = "B1463795421A89BD7F1B7821AF27AABFB98A0DC70E48B2833C6D38F692E1A9AB";
    private const string GrownGameTopBefore = "B1463795421A89BD7F1B7821AF27AABFB98A0DC70E48B2833C6D38F692E1A9AB";
    private const string CoarseStudioCut = "21B091D9AFB675E5713076EAC3B37BAF66DF4DADBE20BAECAB7B8BB81AF5C35A";
    private const string CoarseModGarment = "DFDEC2DE7D75335FC6393A5D741D8B1B94BE8C1C83DA54EE31A0068E2A557CF0";
    private const string CoarseAccessory = "F528490D6C4ECD6FA6AE201A92B420BB4FF49FC88762B59DD19406236CD54941";
    private const string CoarseKeepEnds = "8EBC3389DDBFB920865E12A10C28CE81190D1C07307E3D056184F0372254262A";
    private const string CoarseSizeSet = "21B091D9AFB675E5713076EAC3B37BAF66DF4DADBE20BAECAB7B8BB81AF5C35A";
    private const string CoarseStudioCutAndClear = "8EBC3389DDBFB920865E12A10C28CE81190D1C07307E3D056184F0372254262A";
    private const string CoarseGameTopBefore = "8EBC3389DDBFB920865E12A10C28CE81190D1C07307E3D056184F0372254262A";
    private const string CoarseGameTopTuckOnly = "8EBC3389DDBFB920865E12A10C28CE81190D1C07307E3D056184F0372254262A";
    private const string FloatStudioCut = "E5AE8557FA4485B8CC2801B07DE482652D51DB4734955038422DD75DA7DBE018";
    private const string FloatModGarment = "20633D6DEC0C8E9A01C9859490CF275A4ED1C1157629ACDE4543E1ADC5317051";
    private const string FloatAccessory = "455A6CABB5E960B5C487C7001724324CEB2BF2175EA96FF796C6D63116FBF886";
    private const string FloatKeepEnds = "0BEB5729AFEFA7230D231CF413802754F739BAD21D6ED343EEAC37150299624C";
    private const string FloatSizeSet = "E5AE8557FA4485B8CC2801B07DE482652D51DB4734955038422DD75DA7DBE018";
    private const string FloatStudioCutAndClear = "F712B6FC7BD95B90D2FF123FFDBB7E0169F3D58BFBFACE168BB8E89EED775B1C";
    private const string FloatGameTop = "F712B6FC7BD95B90D2FF123FFDBB7E0169F3D58BFBFACE168BB8E89EED775B1C";
    private const string FloatGameTopCoarse = "5FF3A7838F12B64BABE4A9BD0D95D73A903C72D262E0DB1BF90E6BAA11EA1763";
    private const string FloatGameTopLayered = "09B325290977D5B083D2D2E1648E59E6BFBFC328D5190BB53920D65478D29052";
    private const string CoarseGrownGameTop = "D273BB294DD31AEADEE320153F8FD3470AFDBB87306A3F4AC600E04CEFE8BA04";
    private const string ChordSagStudioCut = "96F3E796E4D5AFD1C3A1F268DF77997FDEEED7E5720DCF6673C6CACAF122E6D3";
    private const string GrownGameTopTucked = "F0637CE799121F93B6B3CD40FB3BA06864DB30330B14BE21F09D31F60D1BFED6";
    private const string LooseGameTopTucked = "D80A3792966E94E9A937CEF3D655EACB20998A6903E6521DD3561B4022C3E4E1";
    private const string LooseStudioTucked = "D80A3792966E94E9A937CEF3D655EACB20998A6903E6521DD3561B4022C3E4E1";
    private const string GrownStudioTucked = "F0637CE799121F93B6B3CD40FB3BA06864DB30330B14BE21F09D31F60D1BFED6";

    // The inputs those were pinned on.
    private const string MaimingSha = "E2985DE7C93082F91EB987372F6701429B0A280A5D5A213E044EED8C1E4210CB";
    private const string DirndlSha = "24BE9C61E0A7FB57D8B307AC761846B596896E8C6008AE8F84562EAAF2AD77DF";
    private const string VanillaTopSha = "2073B644BD3EBE2E21D84C98C622F9412E200642BAD1310DE4D20409355A2C6F";
    private const string NeobellyMSha = "EAFA97B411C607BF4F8D260EF4948A7958573F6E8A05644A633C3EA500A22C2E";
    private const string AlmondXsSha = "0D90681073C3F19A06D320210C79AF46854E3D512EE35E5C1076DE1CBFC16EA2";
}
