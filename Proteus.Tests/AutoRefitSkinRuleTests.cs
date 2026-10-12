using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The automatic refit's skin: the new body's in whole for a mod's garment, and cut to where the garment drew its own —
/// with the cloth cleared of the body — for the game's own gear, whose cloth the game buries in its body on purpose.
/// Reported on the Neo-Ishgardian Top of Maiming refitted onto Neolithe: skin through the cloth under the bust and belly.
/// </summary>
public class AutoRefitSkinRuleTests(ITestOutputHelper output)
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string GameSkin = "/mt_c0201b0001_a.mtrl";
    private const string Cloth = "/mt_c0201e0631_top_a.mtrl";

    /// <summary>A chest of <paramref name="islands"/> one-triangle islands, raised by <paramref name="z"/>.</summary>
    private static byte[] Chest(float z, int islands = 16, string skin = Skin) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(skin, new SyntheticModel.Sub(0, Islands: islands, TrianglesPerIsland: 1, OffsetZ: z)));

    /// <summary>A chest pair from <paramref name="body"/> at rest to the same body raised 10 mm, its files carried.</summary>
    private static BodyRetarget.SlotPair Raised(Func<float, byte[]> body, float grow = 0.01f)
    {
        var source = ModelPartReader.Read(body(0f))!;
        var targetBytes = body(grow);
        var target = ModelPartReader.Read(targetBytes)!;
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "_top", out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair("_top", built!, target, targetBytes, body(0f));
    }

    /// <summary>A top drawing 4 of the chest's 16 islands as its own skin (mesh 0), cloth over it (mesh 1).</summary>
    private static byte[] Top(string skin = Skin, float clothZ = 0.001f) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(skin, new SyntheticModel.Sub(0, Islands: 4, TrianglesPerIsland: 1)),
        new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: clothZ)));

    private static int SkinTriangles(byte[] model)
        => ModelPartReader.Read(model)!.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                          .Sum(p => p.Triangles.Length / 3);

    [Fact]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-5")]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-1")]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-3")]
    public void Only_the_games_own_garment_is_game_gear_and_only_it_is_cut_and_cleared()
    {
        // The game's own file: no mod root. A mod serving the game's model — a recolour, a "— Proteus edit" copy — serves
        // a file of its own, and keeps whole skin; the rule reads the worn piece, never a cached reading of what it was
        // made on.
        Assert.True(AutoRefitWatcher.IsGameGear(null, Top_, [], null));
        Assert.False(AutoRefitWatcher.IsGameGear("mods/Coat — Proteus edit", Top_,
                                                 [new PenumbraModMeta.Redirect(Top_, Top_, "")], null));
        Assert.False(AutoRefitWatcher.IsGameGear("mods/Recolour", Top_,
                                                 [new PenumbraModMeta.Redirect(Top_, "coat.mdl", "Colour / Red")],
                                                 Refitted("Body — Neolithe/neolithe m.mdl")));

        Assert.Equal(new AutoRefitDecisions.SkinRule(CutHidden: true, ClearBody: true), AutoRefitDecisions.SkinFor(true));
        Assert.Equal(new AutoRefitDecisions.SkinRule(CutHidden: false, ClearBody: false), AutoRefitDecisions.SkinFor(false));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-8")]
    public void A_game_items_own_refit_mod_is_still_game_gear()
    {
        // "Coat — Neolithe", the mod a game item's auto refit is made in: its only file for the path is the refit Proteus
        // recorded, and its Original is the game's own coat. Refitted again onto another body, it is the game's garment
        // still, and keeps the cut. Back slashes and a leading slash in a path are the same path.
        const string refit = "Body — Neolithe/neolithe m.mdl";
        var redirects = new List<PenumbraModMeta.Redirect> { new(Top_, @"Body — Neolithe\neolithe m.mdl", "Body — Neolithe / Neolithe M") };
        Assert.True(AutoRefitWatcher.IsGameGear("mods/Coat — Neolithe", Top_, redirects, Refitted(refit)));

        // With an author's file under it as well, the garment underneath is the author's.
        redirects.Add(new PenumbraModMeta.Redirect(Top_, "coat.mdl", ""));
        Assert.False(AutoRefitWatcher.IsGameGear("mods/Coat — Neolithe", Top_, redirects, Refitted(refit)));

        // A record of refits of another path says nothing about this one.
        Assert.False(AutoRefitWatcher.IsGameGear("mods/Coat — Neolithe", Top_,
                                                 [new PenumbraModMeta.Redirect(Top_, refit, "Body — Neolithe / Neolithe M")],
                                                 Refitted(refit, "chara/equipment/e0631/model/c0201e0631_dwn.mdl")));
    }

    private const string Top_ = "chara/equipment/e0631/model/c0201e0631_top.mdl";

    private static BodyRetargetWriter.Record Refitted(string file, string gamePath = "/" + Top_)
        => new() { Group = "Body — Neolithe", Options = [new() { GamePath = gamePath, File = file, Name = "Neolithe M" }] };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-5")]
    public void A_mods_garment_plans_byte_for_byte_as_before(bool across)
    {
        var bytes = Top();
        var garment = ModelPartReader.Read(bytes)!;
        var pair = Raised(z => Chest(z));

        // The watcher's call as it stood before the exception.
        var before = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(),
                                       replaceSkin: across, acrossBodies: across, clearBody: false, cutHidden: false,
                                       keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
        var now = AutoRefitWatcher.PlanRefit(garment, bytes, [pair], "_top", across, gameGear: false);

        Assert.Equal(before.Model, now.Model);
        Assert.Equal(before.Report, now.Report);
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-1")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-4")]
    public void The_games_own_top_takes_the_new_skin_only_where_it_drew_its_own()
    {
        // The top draws 4 of the chest's 16 islands. A mod's garment takes the new chest whole, all 16; the game's own
        // top only what it drew, as the game itself draws skin under it.
        var bytes = Top();
        var garment = ModelPartReader.Read(bytes)!;
        var pair = Raised(z => Chest(z));

        var game = AutoRefitWatcher.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true);
        var mod = AutoRefitWatcher.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false);
        output.WriteLine($"game: {game.Report.Swap}; mod: {mod.Report.Swap}");

        Assert.True(game.Report.Swap?.Cut > 0, $"nothing of the chest was cut: {game.Report.Swap}");
        Assert.True(game.Report.Swap?.Added < 16, $"the chest went in whole: {game.Report.Swap}");
        Assert.Equal(0, mod.Report.Swap?.Cut);
        Assert.Equal(16, mod.Report.Swap?.Added);
        Assert.True(game.Report.Folded <= mod.Report.Folded, $"folded {game.Report.Folded} against {mod.Report.Folded}");
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-2")]
    public void The_games_own_top_is_cleared_of_the_body_it_was_buried_in()
    {
        // Cloth 4 mm under the game's skin, on a body the refit does not move: nothing the refit did buried it. A mod's
        // garment keeps it there (its author's choice); the game's own top has it pulled out.
        var bytes = Top(clothZ: -0.004f);
        var garment = ModelPartReader.Read(bytes)!;
        var pair = Raised(z => Chest(z), grow: 0f);

        var game = AutoRefitWatcher.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true);
        var mod = AutoRefitWatcher.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false);
        output.WriteLine($"game pushed {game.Report.Pushed} (worst {game.Report.WorstPush}); mod pushed {mod.Report.Pushed}");

        Assert.True(game.Report.Pushed > 0, "the game's own top should have been pulled out of the body");
        Assert.Equal(0, mod.Report.Pushed);
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-2")]
    public void A_game_top_with_no_skin_of_its_own_takes_none()
    {
        var bytes = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        var garment = ModelPartReader.Read(bytes)!;

        var plan = AutoRefitWatcher.PlanRefit(garment, bytes, [Raised(z => Chest(z))], "_top", across: true, gameGear: true);

        Assert.True(plan.Report.Swap is not { Added: > 0 }, $"skin went in: {plan.Report.Swap}");
        Assert.Equal(0, plan.Report.Folded);
        Assert.NotNull(ModelPartReader.Read(plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-4")]
    public void A_cut_refit_baked_for_the_wearers_race_keeps_its_cut()
    {
        // The game's top drawing the game's _a skin, refitted onto a body drawing _bibo, then baked: its skin is drawn
        // with the body's material and the cut stays as it was.
        var bytes = Top(GameSkin);
        var garment = ModelPartReader.Read(bytes)!;
        var pair = Raised(z => Chest(z));
        var plan = AutoRefitWatcher.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true);

        string target = Path.Combine(Path.GetTempPath(), "proteus-skinrule-" + Guid.NewGuid().ToString("N") + ".mdl");
        File.WriteAllBytes(target, Chest(0.01f));
        try
        {
            var baked = RefitCore.WithBodySkin(plan, target, Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
            var read = ModelPartReader.Read(baked.Model);
            Assert.NotNull(read);
            Assert.Equal(SkinTriangles(plan.Model), SkinTriangles(baked.Model));
            Assert.All(read!.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)),
                       p => Assert.Equal(Skin, p.Material));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-6")]
    public void A_refit_within_one_body_swaps_no_skin_whatever_the_rule()
    {
        var bytes = Top();
        var garment = ModelPartReader.Read(bytes)!;

        var plan = AutoRefitWatcher.PlanRefit(garment, bytes, [Raised(z => Chest(z))], "_top", across: false, gameGear: true);

        Assert.Null(plan.Report.Swap);
    }
}
