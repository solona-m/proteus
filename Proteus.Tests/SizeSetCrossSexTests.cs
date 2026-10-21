using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A women's mod's men's versions in the size set (sizeset-cross-sex), without game files: what is offered, which
/// pieces cross, where they go, what is said, and what stops them before anything is read.
/// </summary>
public class SizeSetCrossSexTests
{
    private const string Top = "chara/equipment/e6000/model/c0201e6000_top.mdl";
    private const string Dwn = "chara/equipment/e6000/model/c0201e6000_dwn.mdl";
    private const string MenTop = "chara/equipment/e6000/model/c0101e6000_top.mdl";
    private const string Sho = "chara/equipment/e6000/model/c0201e6000_sho.mdl";
    private const string Glv = "chara/equipment/e6000/model/c0201e6000_glv.mdl";

    /// <summary>A mod giving <paramref name="paths"/> in one "Size" group of options S and M, plus
    /// <paramref name="extraGroups"/> (JSON) after it. Only the manifest is written: models are absent.</summary>
    private static string Mod(string[] paths, string extraGroups = "")
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-sizeset-men-ci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Files(string option) => string.Join(", ", paths.Select((p, i) => $"\"{p}\": \"{option}\\\\{i}.mdl\""));
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
            { "FileVersion": 4, "Name": "Outfit",
              "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [
                { "Version": 0, "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                  { "Name": "S", "Files": { {{Files("s")}} } },
                  { "Name": "M", "Files": { {{Files("m")}} } } ] }{{extraGroups}} ] }
            """);
        return root;
    }

    private static List<SizeSetRefit.Piece> Pieces(params string[] paths) => SizeSetRefit.PiecesOf(Mod(paths));

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-1")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-15")]
    public void Mens_bodies_are_offered_for_a_womans_chest_or_legs() // [AC-sizeset-cross-sex-1] [EDGE-sizeset-cross-sex-15]
    {
        var offered = SizeSetRefit.Offered(Pieces(Top, Dwn));
        Assert.Contains(offered, c => c.Key == "rue" && !c.Male);
        Assert.DoesNotContain(offered, c => c.Key == "tbse");
        var tbse = Assert.Single(offered, c => c.Key == "tbse@women");
        Assert.Equal("tbse", tbse.Body);
        Assert.Equal(string.Format(Strings.SizeSet.ForMenFmt, "TBSE"), tbse.Label);
        Assert.True(tbse.Male && tbse.ForMen && tbse.AcrossTip);
        Assert.Contains(offered, c => c.Key == "tbse-x@women" && c.Label.Contains("TBSE-X"));

        // Women's shoes and gloves alone: no men's body at all.
        Assert.DoesNotContain(SizeSetRefit.Offered(Pieces(Sho, Glv)), c => c.Male);
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-1")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-1")]
    public void A_unisex_mod_offers_its_mens_sizes_and_mens_versions_apart() // [AC-sizeset-cross-sex-1] [EDGE-sizeset-cross-sex-1]
    {
        // Its men's top is sized onto TBSE as a man's piece; its women's legs, which have no men's model, are crossed.
        var pieces = Pieces(Top, MenTop, Dwn);
        var offered = SizeSetRefit.Offered(pieces);
        var plain = Assert.Single(offered, c => c.Key == "tbse");
        Assert.Equal("TBSE", plain.Label);
        Assert.False(plain.ForMen);
        Assert.Contains(offered, c => c.Key == "tbse@women" && c.ForMen && c.AcrossTip);
        Assert.Equal([Dwn], SizeSetRefit.AcrossPieces(pieces).Select(p => p.GamePath));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-1")]
    public void A_mens_mod_tick_does_not_cross_a_womans_mod() // [AC-sizeset-cross-sex-1]
    {
        Assert.Empty(SizeSetRefit.MensKeys(["tbse", "tbse-x", "rue"]));
        Assert.Equal(["tbse", "tbse-x"], SizeSetRefit.MensKeys(["tbse-x@women", "rue", "tbse@women"]));

        // Ticked plainly, a women's mod is not crossed: nothing is said of men's versions at all.
        string root = Mod([Top]);
        var log = Substitute.For<IPluginLog>();
        var across = new SizeSetRefit.AcrossInputs(_ => null);
        var result = SizeSetRefit.Run(new SizeSetRefit.Request(root, ["tbse"], false, false, [], Across: across),
                                      new UVRemapService(log, "."), log, _ => { }, default);
        Assert.Equal(0, result.Made);
        Assert.DoesNotContain(Strings.SizeSet.MenMapsMissing, result.Notes);
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-6")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-2")]
    public void A_womans_piece_with_its_own_mens_model_is_not_crossed() // [AC-sizeset-cross-sex-6] [EDGE-sizeset-cross-sex-2]
    {
        Assert.Empty(SizeSetRefit.AcrossPieces(Pieces(Top, MenTop)));
        Assert.DoesNotContain(SizeSetRefit.Offered(Pieces(Top, MenTop)), c => c.ForMen);
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-6")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-14")]
    public void Only_womens_chest_and_legs_cross() // [AC-sizeset-cross-sex-6] [EDGE-sizeset-cross-sex-14]
    {
        var across = SizeSetRefit.AcrossPieces(Pieces(Top, Dwn, Sho, Glv));
        Assert.Equal(["_top", "_dwn"], across.Select(p => p.Slot));

        // Ticked for men with shoes in the mod: one note that they get none, said before anything is read.
        string root = Mod([Top, Sho]);
        var log = Substitute.For<IPluginLog>();
        var result = SizeSetRefit.Run(new SizeSetRefit.Request(root, ["tbse@women"], false, false, [],
                                                               Across: new SizeSetRefit.AcrossInputs(_ => null)),
                                      new UVRemapService(log, "."), log, _ => { }, default);
        Assert.Single(result.Notes, n => n == Strings.SizeSet.EndsNotForMen);
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-2")]
    public void Mens_targets_and_names_come_from_the_table() // [AC-sizeset-cross-sex-2]
    {
        var top = SizeSetRefit.AcrossTarget("tbse", "_top")!.Value;
        Assert.Equal(SupportedBodies.Of("tbse", "_top", false)!.Sizes[0].Rel, top.Rel);
        Assert.Equal("TBSE", SupportedBodies.OptionName(top.Body, top.Size));
        var legs = SizeSetRefit.AcrossTarget("tbse-x", "_dwn")!.Value;
        Assert.Equal("TBSE-X", SupportedBodies.OptionName(legs.Body, legs.Size));
        Assert.Equal(SupportedBodies.Of("tbse-x", "_dwn", false)!.Sizes[0].Rel, legs.Rel);
        Assert.Null(SizeSetRefit.AcrossTarget("rue", "_top"));
        Assert.Null(SizeSetRefit.AcrossTarget("tbse", "_sho"));
        Assert.Equal("Men's body", Strings.SizeSet.MensGroup);
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-3")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-6")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-7")]
    public void Only_bibo_and_game_layouts_are_sources_for_men() // [AC-sizeset-cross-sex-3] [EDGE-sizeset-cross-sex-6] [EDGE-sizeset-cross-sex-7]
    {
        Assert.True(SizeSetRefit.AcrossEligible("bibo"));
        Assert.True(SizeSetRefit.AcrossEligible("gen2"));
        Assert.False(SizeSetRefit.AcrossEligible("gen3"));
        Assert.False(SizeSetRefit.AcrossEligible(UVRemapService.TbseSpace));
        Assert.False(SizeSetRefit.AcrossEligible(null));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-3")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-8")]
    public void The_nearest_bent_body_rigged_alike_is_chosen() // [AC-sizeset-cross-sex-3] [EDGE-sizeset-cross-sex-8]
    {
        // As measured on Thursday (Rue+): Small's body bent for a man lies 18 mm off TBSE, Medium's 37, Large's 54.
        var sizes = new[] { ("Small", false, 0.0184f), ("Medium", false, 0.037f), ("Large", true, 0.010f) };
        var alike = SizeSetRefit.RiggedAlike(sizes, s => s.Item2, targetYas: false);
        Assert.DoesNotContain(alike, s => s.Item1 == "Large");   // nearer, but rigged for YAS
        var chosen = SizeSetRefit.Choose(alike.Select((s, i) => (s, new SizeSetRefit.SourceDistance(s.Item3, s.Item3, 0f, 0f, i)))
                                              .ToList()).Chosen;
        Assert.Equal("Small", chosen.Item1);
        // Only YAS-rigged sizes (Starlit): they are used, their bones moved.
        Assert.Single(SizeSetRefit.RiggedAlike(new[] { true }, y => y, targetYas: false));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-4")]
    [Trait("Plan", "AC-sizeset-cross-sex-5")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-5")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-19")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-20")]
    [Trait("Plan", "AC-sizeset-cross-sex-8")]
    public void Judge_saves_on_coverage_and_says_folds() // [AC-sizeset-cross-sex-4] [AC-sizeset-cross-sex-5] [EDGE-sizeset-cross-sex-5] [EDGE-sizeset-cross-sex-19] [EDGE-sizeset-cross-sex-20] [AC-sizeset-cross-sex-8]
    {
        // Folds are said, never a refusal.
        var (save, note, warnings) = SizeSetRefit.Judge("Top", "TBSE", "", "", true, 0.993, 122, null, null);
        Assert.True(save);
        Assert.Null(note);
        var folded = Assert.Single(warnings);
        Assert.Contains("122", folded);
        Assert.StartsWith("Top, \"TBSE\": ", folded);

        // None folded, none said.
        Assert.Empty(SizeSetRefit.Judge("Top", "TBSE", "", "", true, 1.0, 0, null, null).Warnings);

        // Under 90% of the body matched: left out, saying how much did.
        (save, note, warnings) = SizeSetRefit.Judge("Top", "TBSE-X", "", "", true, 0.85, 3, null, null);
        Assert.False(save);
        Assert.Contains("85", note);
        Assert.Contains("TBSE-X", note);
        Assert.Empty(warnings);

        // The job or the size refused, or nothing made: left out with why.
        Assert.Contains("no wearer", SizeSetRefit.Judge("Top", "TBSE", "no wearer", null, false, 1, 0, null, null).Note);
        Assert.Contains("too far", SizeSetRefit.Judge("Top", "TBSE", "", "too far", false, 1, 0, null, null).Note);
        Assert.False(SizeSetRefit.Judge("Top", "TBSE", "", null, false, 1, 0, null, null).Save);
        Assert.False(SizeSetRefit.Judge("Top", "TBSE", "", "", false, 1, 0, null, null).Save);

        // Bones moved and bones only a custom skeleton gives: both said.
        var moved = new List<SkeletonRemap.Move> { new("ya_shiri_phys_l", "iv_shiri_l", SkeletonRemap.How.Counterpart) };
        warnings = SizeSetRefit.Judge("Skirt", "TBSE", "", "", true, 0.901, 0, moved, ["iv_ko_c_l"]).Warnings;
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("ya_shiri_phys_l → iv_shiri_l"));
        Assert.Contains(warnings, w => w.Contains("iv_ko_c_l"));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-5")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-4")]
    public void Without_maps_no_mens_version_is_attempted() // [AC-sizeset-cross-sex-5] [EDGE-sizeset-cross-sex-4]
    {
        string root = Mod([Top, Dwn]);
        string before = File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile));
        var log = Substitute.For<IPluginLog>();
        var maps = new UVRemapService(log, Path.Combine(root, "no-maps-here"));
        int wanted = 0;
        maps.MenMapsWanted += () => wanted++;
        var result = SizeSetRefit.Run(new SizeSetRefit.Request(root, ["tbse@women", "tbse-x@women"], false, false, [],
                                                               Across: new SizeSetRefit.AcrossInputs(_ => null)),
                                      maps, log, _ => { }, default);
        Assert.Equal(0, result.Made);
        Assert.Single(result.Notes, n => n == Strings.SizeSet.MenMapsMissing);
        Assert.Equal(1, wanted);
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-5")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-11")]
    public void An_authors_group_named_like_ours_is_left_alone() // [AC-sizeset-cross-sex-5] [EDGE-sizeset-cross-sex-11]
    {
        string root = Mod([Top], """
            , { "Version": 0, "Name": "Men's body", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                { "Name": "Off", "Files": {} } ] }
            """);
        string before = File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile));
        var log = Substitute.For<IPluginLog>();
        var result = SizeSetRefit.Run(new SizeSetRefit.Request(root, ["tbse@women"], false, false, [],
                                                               Across: new SizeSetRefit.AcrossInputs(_ => null)),
                                      new UVRemapService(log, "."), log, _ => { }, default);
        Assert.Equal(0, result.Made);
        Assert.Contains(string.Format(Strings.SizeSet.MensGroupTakenFmt, "Men's body"), result.Notes);
        Assert.DoesNotContain(Strings.SizeSet.MenMapsMissing, result.Notes);
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-7")]
    public void A_language_change_reuses_our_mens_group() // [AC-sizeset-cross-sex-7]
    {
        string root = Mod([Top]);
        var pieces = SizeSetRefit.PiecesOf(root);
        Assert.Equal(Strings.SizeSet.MensGroup, SizeSetRefit.MensGroupName(root, SizeSetRefit.AcrossPieces(pieces)));

        // Saved in another language's name: found again by what the record says it holds.
        var outcome = BodyRetargetWriter.Save(root, "Männerkörper", MenTop, "The Body SE", "Bibo+ — Small",
                                              [new BodyRetargetWriter.Refit("TBSE", [1, 2, 3], "TBSE")]);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("Männerkörper", SizeSetRefit.MensGroupName(root, SizeSetRefit.AcrossPieces(SizeSetRefit.PiecesOf(root))));
        Assert.False(BodyRetargetWriter.IsAuthorGroup(root, "Männerkörper"));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-7")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-11")]
    public void A_hand_made_refit_for_group_is_never_taken_for_ours() // [AC-sizeset-cross-sex-7] [EDGE-sizeset-cross-sex-11]
    {
        // The Body size panel's "Refit for" saved the top for men into its own group, beside a women's refit there.
        string root = Mod([Top]);
        Assert.True(BodyRetargetWriter.Save(root, "Body Retarget", Top, "Rue", "Bibo+ — Small",
                                            [new BodyRetargetWriter.Refit("Rue L", [1, 2, 3], "Rue L")]).Ok);
        Assert.True(BodyRetargetWriter.Save(root, "Body Retarget", MenTop, "The Body SE", "Bibo+ — Small",
                                            [new BodyRetargetWriter.Refit("TBSE", [1, 2, 3], "TBSE")]).Ok);
        var across = SizeSetRefit.AcrossPieces(SizeSetRefit.PiecesOf(root));
        Assert.Equal(Strings.SizeSet.MensGroup, SizeSetRefit.MensGroupName(root, across));

        // One holding only a men's option of another name (a panel save named after the size) is not ours either.
        string other = Mod([Top]);
        Assert.True(BodyRetargetWriter.Save(other, "Body — Top", MenTop, "The Body SE", "Bibo+ — Small",
                                            [new BodyRetargetWriter.Refit("Midlander M", [1, 2, 3], "TBSE")]).Ok);
        Assert.Equal(Strings.SizeSet.MensGroup,
                     SizeSetRefit.MensGroupName(other, SizeSetRefit.AcrossPieces(SizeSetRefit.PiecesOf(other))));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-3")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-17")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-18")]
    public void Legs_follow_the_chests_men_source() // [AC-sizeset-cross-sex-3] [EDGE-sizeset-cross-sex-17] [EDGE-sizeset-cross-sex-18]
    {
        string key = SizeSetRefit.LeadKey("Men's body", "0101", "TBSE");
        var leads = new Dictionary<string, string> { [key] = "Large" };
        var asked = new List<string>();
        string? Ready(string option) { asked.Add(option); return option == "Large" ? "legs in Large" : null; }

        // Legs sized with the chest: cut from the option the chest's men's version was.
        Assert.Equal((SizeSetRefit.LeadOutcome.FromLead, "Large", "legs in Large"),
                     SizeSetRefit.FromLead(true, leads, key, Ready));
        // That option's legs can't be crossed: their own nearest.
        Assert.Equal((SizeSetRefit.LeadOutcome.LeadUnusable, "Small", (string?)null),
                     SizeSetRefit.FromLead(true, new Dictionary<string, string> { [key] = "Small" }, key, Ready));
        // The chest made no men's version this run: their own nearest.
        Assert.Equal(SizeSetRefit.LeadOutcome.NoLead, SizeSetRefit.FromLead(true, new Dictionary<string, string>(), key, Ready).Outcome);
        // Legs in a group of their own: their own nearest, never asking for the chest's option.
        asked.Clear();
        Assert.Equal(SizeSetRefit.LeadOutcome.Own, SizeSetRefit.FromLead(false, leads, key, Ready).Outcome);
        Assert.Empty(asked);
        // Their option is still named by the men's body, "TBSE", as the chest's is.
        Assert.Equal("TBSE", SupportedBodies.OptionName(SizeSetRefit.AcrossTarget("tbse", "_dwn")!.Value.Body,
                                                        SizeSetRefit.AcrossTarget("tbse", "_dwn")!.Value.Size));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-5")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-6")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-20")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-21")]
    public void No_source_is_said_with_why() // [AC-sizeset-cross-sex-5] [EDGE-sizeset-cross-sex-6] [EDGE-sizeset-cross-sex-20] [EDGE-sizeset-cross-sex-21]
    {
        Assert.Equal(string.Format(Strings.SizeSet.NoSourceForMenFmt, "Top"), SizeSetRefit.NoSourceNote("Top", "TBSE", null));
        Assert.Equal(string.Format(Strings.SizeSet.RefusedFmt, "Top", "TBSE", "no deformer"),
                     SizeSetRefit.NoSourceNote("Top", "TBSE", "no deformer"));
    }

    [Fact]
    [Trait("Plan", "AC-sizeset-cross-sex-3")]
    public void Variants_give_one_mens_version_and_say_which() // [AC-sizeset-cross-sex-3]
    {
        // Sheer Elegance's "Content Type": SFW and NSFW, both made on Bibo+ Perky Medium; a third on another size.
        var piece = new SizeSetRefit.Piece(Top, "_top", "Content Type",
                                           [("SFW", "sfw.mdl"), ("NSFW", "nsfw.mdl"), ("Big", "big.mdl")], "Top", "Body — Top",
                                           "0201");
        var medium = new BodyOption("Perky", "", "Medium", "perky/m.mdl", Top, "_top");
        var large = medium with { Name = "Large", Rel = "perky/l.mdl" };
        var picks = new Dictionary<string, AutoRefitDecisions.Pick?>
        {
            [SizeSetRefit.ReadingKey("sfw.mdl", "_top", "0201", "Bibo+")] = new("Bibo+", medium, BodySizeMatch.Confidence.Exact, false),
            [SizeSetRefit.ReadingKey("nsfw.mdl", "_top", "0201", "Bibo+")] = new("Bibo+", medium, BodySizeMatch.Confidence.Exact, false),
            [SizeSetRefit.ReadingKey("big.mdl", "_top", "0201", "Bibo+")] = new("Bibo+", large, BodySizeMatch.Confidence.Exact, false),
        };
        Assert.Equal(["NSFW"], SizeSetRefit.OtherVariants(piece, "sfw.mdl", picks.First().Value!, "Bibo+", picks));
        string said = string.Format(Strings.SizeSet.OneVariantFmt, "Top", "SFW");
        Assert.Contains("\"SFW\"", said);
    }

    [Fact]
    [Trait("Plan", "EDGE-sizeset-cross-sex-10")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-24")]
    public void A_mens_version_carries_its_eqdp_and_an_est_carry() // [EDGE-sizeset-cross-sex-10] [EDGE-sizeset-cross-sex-24]
    {
        var route = new RacialModelBake.Route(CrossSexRefit.Women, CrossSexRefit.Men, Down: false, Lowered: true) { ByAlias = true };
        string Json(object m) => System.Text.Json.JsonSerializer.Serialize(m);

        var plain = SizeSetRefit.MensManipulations(route, EstPlan.NotNeeded, MenTop);
        var eqdp = Assert.Single(plain);
        Assert.Contains("Eqdp", Json(eqdp));

        var carried = SizeSetRefit.MensManipulations(route, new EstPlan(EstOutcome.Carry, Entry: 7), MenTop);
        Assert.Equal(2, carried.Count);
        string est = Json(carried[1]);
        Assert.Contains("Est", est);
        Assert.Contains("6000", est);
        Assert.Contains("\"Entry\":7", est.Replace(" ", ""));
    }

    [Fact]
    [Trait("Plan", "EDGE-sizeset-cross-sex-16")]
    public void Without_across_inputs_the_size_set_is_unchanged() // [EDGE-sizeset-cross-sex-16]
    {
        string root = Mod([Top, Sho]);
        string before = File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile));
        var log = Substitute.For<IPluginLog>();
        var result = SizeSetRefit.Run(new SizeSetRefit.Request(root, ["tbse@women"], false, false, []),
                                      new UVRemapService(log, "."), log, _ => { }, default);
        Assert.Equal(0, result.Made);
        Assert.Empty(result.Notes);
        Assert.Empty(result.Warnings!);
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
    }
}
