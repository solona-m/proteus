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

    private const string Piercings = "/mt_c0201b0001_piercings.mtrl";

    /// <summary><see cref="Chest"/> with a ring on its first island, which <see cref="BareTop"/> draws.</summary>
    private static byte[] PiercedChest(float z) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, Islands: 16, TrianglesPerIsland: 1, OffsetZ: z)),
        new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0, OffsetZ: z + 0.001f)));

    /// <summary>A top drawing 4 of the chest's 16 islands as its own skin, and no cloth over them.</summary>
    private static byte[] BareTop() => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, Islands: 4, TrianglesPerIsland: 1)),
        new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetY: 50f)));

    [Theory]
    [Trait("Plan", "AC-refit-piercings-option-4")]
    [InlineData(false)]
    [InlineData(true)]
    public void Auto_refit_carries_piercings_only_when_asked(bool gameGear)
    {
        // [AC-refit-piercings-option-4] A mod's top and the game's own alike: the setting, not the skin rule, decides.
        var bytes = BareTop();
        var garment = ModelPartReader.Read(bytes)!;
        var off = RefitCore.PlanRefit(garment, bytes, [Raised(PiercedChest)], "_top", across: true, gameGear, out _);
        var on = RefitCore.PlanRefit(garment, bytes, [Raised(PiercedChest)], "_top", across: true, gameGear, out _,
                                     carryPiercings: true);
        output.WriteLine($"off {off.Report.Swap}; on {on.Report.Swap}");
        Assert.Equal(0, off.Report.Swap!.Value.Adorned);
        Assert.True(on.Report.Swap!.Value.Adorned > 0, $"no ring came in: {on.Report.Swap}");
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-piercings-option-9")]
    public void Each_plan_uses_the_value_it_was_given()
    {
        // [EDGE-refit-piercings-option-9] Nothing is held between refits: on, then off, is the same off as ever.
        var bytes = BareTop();
        var garment = ModelPartReader.Read(bytes)!;
        var on = RefitCore.PlanRefit(garment, bytes, [Raised(PiercedChest)], "_top", across: true, gameGear: false, out _,
                                     carryPiercings: true);
        var off = RefitCore.PlanRefit(garment, bytes, [Raised(PiercedChest)], "_top", across: true, gameGear: false, out _,
                                      carryPiercings: false);
        var fresh = RefitCore.PlanRefit(garment, bytes, [Raised(PiercedChest)], "_top", across: true, gameGear: false, out _);
        Assert.NotEqual(on.Report.Swap!.Value.Adorned, off.Report.Swap!.Value.Adorned);
        Assert.Equal(fresh.Model, off.Model);
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-5")]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-1")]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-3")]
    [Trait("Plan", "AC-neckline-float-6")]
    [Trait("Plan", "EDGE-neckline-float-14")]
    public void Only_the_games_own_garment_is_game_gear_and_only_it_is_cut_and_cleared()
    {
        // The game's own file: no mod root. A mod serving the game's model — a recolour, a "— Proteus edit" copy — serves
        // a file of its own, and keeps whole skin; the rule reads the worn piece, never a cached reading of what it was
        // made on.
        Assert.True(AutoRefitDecisions.IsGameGear(null, Top_, [], null, madeByProteus: false));
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Coat — Proteus edit", Top_,
                                                 [new PenumbraModMeta.Redirect(Top_, Top_, "")], null, madeByProteus: true));
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Recolour", Top_,
                                                 [new PenumbraModMeta.Redirect(Top_, "coat.mdl", "Colour / Red")],
                                                 Refitted("Body — Neolithe/neolithe m.mdl"), madeByProteus: false));

        Assert.Equal(new AutoRefitDecisions.SkinRule(CutHidden: true, ClearBody: true, TuckUnderCloth: true, DeepClear: true,
                                                     KeepStandoff: true),
                     AutoRefitDecisions.SkinFor(true));
        Assert.Equal(new AutoRefitDecisions.SkinRule(CutHidden: false, ClearBody: false, TuckUnderCloth: false, DeepClear: false,
                                                     KeepStandoff: false),
                     AutoRefitDecisions.SkinFor(false));
        Assert.True(RefitCore.DeepClearFor(true, "_top"));
        Assert.False(RefitCore.DeepClearFor(true, "_dwn"));
        Assert.False(RefitCore.DeepClearFor(false, "_top"));
        Assert.True(RefitCore.KeepStandoffFor(true, "_top"));
        Assert.False(RefitCore.KeepStandoffFor(true, "_dwn"));
        Assert.False(RefitCore.KeepStandoffFor(false, "_top"));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-8")]
    public void A_game_items_own_refit_mod_is_still_game_gear()
    {
        // "Coat — Neolithe", the mod a game item's auto refit is made in: its only file for the path is the refit Proteus
        // recorded, and its Original is the game's own coat. Refitted again onto another body, it is the game's garment
        // still, and keeps the cut. A back slash in a file, and a leading slash or other case in the worn path, change
        // nothing.
        const string refit = "Body — Neolithe/neolithe m.mdl";
        var redirects = new List<PenumbraModMeta.Redirect> { new(Top_, @"Body — Neolithe\neolithe m.mdl", "Body — Neolithe / Neolithe M") };
        Assert.True(AutoRefitDecisions.IsGameGear("mods/Coat — Neolithe", "/" + Top_.ToUpperInvariant(), redirects,
                                                  Refitted(refit), madeByProteus: true));

        // The mod the Body size tool made to hold a modder's garment refitted by hand: Proteus's, nothing but refits,
        // none of them from the game. The modder's garment, and whole skin.
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Their Coat — Neolithe", Top_, redirects,
                                                   Refitted(refit, fromGame: false), madeByProteus: true));

        // The same files in a mod Proteus did not make: an author's, whatever it serves.
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Coat — Neolithe", Top_, redirects, Refitted(refit), madeByProteus: false));

        // With a file of another's under it as well, the garment underneath is that one.
        redirects.Add(new PenumbraModMeta.Redirect(Top_, "coat.mdl", ""));
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Coat — Neolithe", Top_, redirects, Refitted(refit), madeByProteus: true));

        // A mod serving nothing at the worn path is not what the piece is drawn from.
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Coat — Neolithe", Top_,
                                                 [new PenumbraModMeta.Redirect(Dwn_, refit, "Body — Neolithe / Neolithe M")],
                                                 Refitted(refit, Dwn_), madeByProteus: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-1")]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-8")]
    public void A_modders_top_baked_for_the_wearer_is_still_the_modders()
    {
        // A male-only top: the author's model at c0101, the refit baked to c0201 saved beside it. At the worn c0201 path
        // only the refit is served, but the garment underneath is the author's — the whole mod says so.
        const string baked = "Body — Neolithe/neolithe m.mdl";
        var redirects = new List<PenumbraModMeta.Redirect>
        {
            new(Male_, "coat.mdl", ""),
            new(Top_, baked, "Body — Neolithe / Neolithe M"),
        };
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Coat", Top_, redirects, Refitted(baked), madeByProteus: false));
        Assert.False(AutoRefitDecisions.IsGameGear("mods/Coat", Top_, redirects, Refitted(baked), madeByProteus: true));

        // The game's own male-only top, baked into the mod Proteus made for it: only the refit, anywhere. Game gear.
        Assert.True(AutoRefitDecisions.IsGameGear("mods/Coat — Neolithe", Top_, [redirects[1]], Refitted(baked),
                                                madeByProteus: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-8")]
    public void A_mod_is_Proteus_s_own_when_its_manifest_says_Proteus_wrote_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-skinrule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(RefitModService.IsMadeByProteus(root));
            RefitModService.WriteScaffold(root, "Coat — Neolithe", "Coat, refitted by Proteus.");
            Assert.True(RefitModService.IsMadeByProteus(root));
            PenumbraModMeta.AtomicWrite(Path.Combine(root, PenumbraModMeta.MetaFile),
                                        PenumbraModMeta.NewMetaJson("Coat", "An author", ""));
            Assert.False(RefitModService.IsMadeByProteus(root));
            PenumbraModMeta.AtomicWrite(Path.Combine(root, PenumbraModMeta.MetaFile),
                                        PenumbraModMeta.NewMetaJson("Coat", "proteus", ""));
            Assert.False(RefitModService.IsMadeByProteus(root));
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """{ "FileVersion": 3, "Name": "Coat", "Author": 5 }""");
            Assert.False(RefitModService.IsMadeByProteus(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string Top_ = "chara/equipment/e0631/model/c0201e0631_top.mdl";
    private const string Male_ = "chara/equipment/e0631/model/c0101e0631_top.mdl";
    private const string Dwn_ = "chara/equipment/e0631/model/c0201e0631_dwn.mdl";

    private static BodyRetargetWriter.Record Refitted(string file, string gamePath = Top_, bool fromGame = true)
        => new()
        {
            Group = "Body — Neolithe",
            Options = [new() { GamePath = gamePath, File = file, Name = "Neolithe M", FromGame = fromGame }],
        };

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-8")]
    public void A_game_items_own_mod_stays_game_gear_however_it_is_refitted_again()
    {
        // What the Body size tool and the every-size refit record when they save into a mod: read off the mod on disk.
        // A game item's own mod refitted again by hand — its flag carried forward — is still the game's garment; one
        // refit in it recorded as not from the game makes it a mod, and whole skin, the safe way round.
        string root = Path.Combine(Path.GetTempPath(), "proteus-skinrule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(RefitCore.IsGameGear(null, Top_));

            RefitModService.WriteScaffold(root, "Coat — Neolithe", "Coat, refitted by Proteus.");
            Assert.True(BodyRetargetWriter.Save(root, "Body — Neolithe", Top_, "Neolithe", "Vanilla",
                                                [new BodyRetargetWriter.Refit("Neolithe M", [1, 2, 3], "M")],
                                                fromGame: RefitCore.IsGameGear(null, Top_)).Ok);
            Assert.True(RefitCore.IsGameGear(root, Top_));

            Assert.True(BodyRetargetWriter.Save(root, "Body — Neolithe", Top_, "Neolithe", "Neolithe M",
                                                [new BodyRetargetWriter.Refit("Neolithe L", [4, 5, 6], "L")],
                                                fromGame: RefitCore.IsGameGear(root, Top_)).Ok);
            Assert.True(RefitCore.IsGameGear(root, Top_));

            Assert.True(BodyRetargetWriter.Save(root, "Body — Neolithe", Top_, "Neolithe", "Neolithe M",
                                                [new BodyRetargetWriter.Refit("Neolithe XL", [7, 8, 9], "XL")]).Ok);
            Assert.False(RefitCore.IsGameGear(root, Top_));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-8")]
    public void A_save_records_whether_it_was_made_from_the_games_own_garment()
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-skinrule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RefitModService.WriteScaffold(root, "Coat — Neolithe", "Coat, refitted by Proteus.");
            Assert.True(BodyRetargetWriter.Save(root, "Body — Neolithe", Top_, "Neolithe", "Vanilla",
                                                [new BodyRetargetWriter.Refit("Neolithe M", [1, 2, 3], "M")],
                                                fromGame: true).Ok);
            Assert.True(BodyRetargetWriter.Save(root, "Body — Neolithe", Top_, "Neolithe", "Vanilla",
                                                [new BodyRetargetWriter.Refit("Neolithe L", [4, 5, 6], "L")]).Ok);

            var record = BodyRetargetWriter.ReadRecord(root)!;
            Assert.True(record.Options.Single(e => e.Name == "Neolithe M").FromGame);
            Assert.False(record.Options.Single(e => e.Name == "Neolithe L").FromGame);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
        var now = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across, gameGear: false, out _);

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

        var game = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _);
        var mod = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _);
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

        var game = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _);
        var mod = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _);
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

        var plan = RefitCore.PlanRefit(garment, bytes, [Raised(z => Chest(z))], "_top", across: true, gameGear: true, out _);

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
        var plan = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _);

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
    [Trait("Plan", "AC-neckline-skin-tuck-5")]
    public void A_game_tops_tuck_is_logged()
    {
        var bytes = SkinTuckSheet.LooseTop(looseFrom: 5);
        var garment = ModelPartReader.Read(bytes)!;
        var game = new List<string>();
        var mod = new List<string>();

        var plan = RefitCore.PlanRefit(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", across: true,
                                       gameGear: true, out _, log: game.Add);
        RefitCore.PlanRefit(garment, bytes, [SkinTuckSheet.Resized(SkinTuckSheet.Body())], "_top", across: true,
                            gameGear: false, out _, log: mod.Add);

        Assert.True(plan.Report.Swap?.Tucked > 0, $"{plan.Report.Swap}");
        Assert.Contains(game, l => l.StartsWith($"skin tucked: {plan.Report.Swap!.Value.Tucked} triangles", StringComparison.Ordinal));
        Assert.DoesNotContain(mod, l => l.StartsWith("skin tucked:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-neckline-float-7")]
    public void A_game_tops_standoff_is_logged()
    {
        var (bytes, pair) = SkinTuckSheet.FloatingNeckline(coarse: true);
        var garment = ModelPartReader.Read(bytes)!;
        var game = new List<string>();
        var mod = new List<string>();
        var studio = new List<string>();

        var plan = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _, log: game.Add);
        RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _, log: mod.Add);
        BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
                          clearBody: true, cutHidden: true,
                          keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)),
                          tuckSlot: RefitCore.TuckSlot(true, "_top"), log: studio.Add);

        Assert.True(plan.Report.StandoffPulled > 0);
        Assert.Contains(game, l => l.StartsWith($"cloth brought back toward the author's standoff: {plan.Report.StandoffPulled} points",
                                                StringComparison.Ordinal));
        Assert.DoesNotContain(mod, l => l.StartsWith("cloth brought back", StringComparison.Ordinal));
        Assert.DoesNotContain(studio, l => l.StartsWith("cloth brought back", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-neckline-large-bust-8")]
    public void A_game_tops_deep_clear_is_logged()
    {
        var bytes = SkinTuckSheet.CoarseBuriedTop(0.012f);
        var garment = ModelPartReader.Read(bytes)!;
        var pair = SkinTuckSheet.Grown(SkinTuckSheet.Bust(0f), SkinTuckSheet.Bust(0.02f));
        var game = new List<string>();
        var mod = new List<string>();
        var studio = new List<string>();

        var plan = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _, log: game.Add);
        RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _, log: mod.Add);
        BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
                          clearBody: true, cutHidden: true,
                          keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)),
                          tuckSlot: RefitCore.TuckSlot(true, "_top"), log: studio.Add);

        Assert.True(plan.Report.DeepCleared > 0);
        Assert.Contains(game, l => l.StartsWith($"cloth cleared deep: {plan.Report.DeepCleared} points", StringComparison.Ordinal));
        Assert.DoesNotContain(mod, l => l.StartsWith("cloth cleared deep:", StringComparison.Ordinal));
        Assert.DoesNotContain(studio, l => l.StartsWith("cloth cleared deep:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-refit-cut-skin-6")]
    public void A_refit_within_one_body_swaps_no_skin_whatever_the_rule()
    {
        var bytes = Top();
        var garment = ModelPartReader.Read(bytes)!;

        var plan = RefitCore.PlanRefit(garment, bytes, [Raised(z => Chest(z))], "_top", across: false, gameGear: true, out _);

        Assert.Null(plan.Report.Swap);
    }
}
