using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// "Refit for": which races of a garment's sex Body size offers, the route a pick makes, and what the list and the
/// saved option say — the rules, apart from the panel that draws them.
/// </summary>
public class OtherRaceRefitTests
{
    private const string Shirt = "chara/equipment/e6091/model/c0101e6091_top.mdl";
    private const string HighlanderShirt = "chara/equipment/e6091/model/c0301e6091_top.mdl";

    private static BodyOption Body(string race, string slot, string name, string? rel = null)
        => new("Torso", "", name, rel ?? $"{name}/{race}{slot}.mdl", $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl", slot);

    /// <summary>TBSE-X as it is laid out: a Midlander and a Highlander torso, Midlander legs.</summary>
    private static BodySizeCatalog TbseX(params BodyOption[] more)
        => new("root", [Body("0101", "_top", "M Midlander Miqo'te Au'ra Elezen Viera"), Body("0301", "_top", "M Highlander"),
                        Body("0101", "_dwn", "SFW"), .. more]);

    private static PenumbraModMeta.Redirect Model(string gamePath, string file)
        => new(gamePath, file, "Summer Indigo Shirt / Everything");

    private static readonly PenumbraModMeta.Redirect[] OnlyMidlander = [Model(Shirt, "shirt/c0101e6091_top.mdl")];

    private static IReadOnlyList<OtherRaceRefit.Candidate> Offered(string path, BodySizeCatalog target,
                                                                   IEnumerable<PenumbraModMeta.Redirect>? redirects = null,
                                                                   BodyRetargetWriter.Record? record = null)
        => OtherRaceRefit.Offered(path, target, redirects ?? OnlyMidlander, record, isVanilla: false);

    private static readonly RacialModelBake.Route Up301 = new(101, 301, Down: false);

    // ── AC ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]      // nobody logged in
    [InlineData(101)]
    [InlineData(201)]    // the user's case: a woman playing
    [InlineData(301)]
    [Trait("Plan", "AC-refit-other-race-1")]
    [Trait("Plan", "EDGE-refit-other-race-1")]
    [Trait("Plan", "EDGE-refit-other-race-11")]
    public void A_midlander_man_s_top_is_offered_highlander_whatever_the_wearer(int wearer)
    {
        var target = TbseX();
        var offered = Offered(Shirt, target);
        var only = Assert.Single(offered);
        Assert.Equal(301, only.Race);
        Assert.Equal(OtherRaceRefit.Block.None, only.Blocked);

        var automatic = wearer == 0 ? RacialModelBake.Route.None
            : RefitCore.Route(Shirt, (ushort)wearer, c => RefitCore.RacesOf(target).Contains($"{c:D4}"),
                              c => RefitCore.RacesOf(target).Contains($"{c:D4}"), OnlyMidlander, null);
        Assert.Equal(Up301, OtherRaceRefit.Resolve(automatic, 301, 101, offered));

        var entries = OtherRaceRefit.PickerEntries(automatic, 101, offered);
        Assert.Contains(entries, e => e.Kind == OtherRaceRefit.EntryKind.ThisModel && e.Race == 101);
        Assert.Contains(entries, e => e.Kind == OtherRaceRefit.EntryKind.Other && e.Race == 301);
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-2")]
    public void Picking_highlander_bakes_up_and_every_list_is_highlander_s()
    {
        var target = TbseX();
        var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 301, 101, Offered(Shirt, target));
        Assert.Equal(Up301, route);
        Assert.True(route.ChangesModel);
        Assert.Equal("0301", route.Race(Shirt));
        Assert.Equal("M Highlander", Assert.Single(target.For("_top", route.Race(Shirt))).Name);
    }

    /// <summary>
    /// A part the refit-onto mod has no body of the picked race for — TBSE-X's legs are Midlander only — sits out: the
    /// garment is bent for Highlander, and refitting its legs onto unbent Midlander legs would keep the whole bend on a
    /// body that does not have it. The race's own list never falls back across races; the plain one still does.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-2")]
    [Trait("Plan", "EDGE-refit-other-race-4")]
    public void A_part_without_a_body_of_the_picked_race_sits_out()
    {
        var target = TbseX();
        var onto = OtherRaceRefit.OnlyRace(target, 301);
        Assert.Equal(["_top"], onto.SlotsFor("0301"));
        Assert.Empty(onto.For("_dwn", "0301"));
        Assert.Equal("M Highlander", Assert.Single(onto.For("_top", "0301")).Name);
        Assert.Equal(target.ModRoot, onto.ModRoot);
        // As it was: the full catalog's legs list falls back to the Midlander legs.
        Assert.NotEmpty(target.For("_dwn", "0301"));
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-3")]
    public void A_highlander_refit_saves_at_his_path_with_his_eqdp_entry()
    {
        Assert.Equal(HighlanderShirt, Up301.SavePath(Shirt));
        var eqdp = JsonSerializer.SerializeToElement(Up301.Eqdp(Up301.SavePath(Shirt))!);
        Assert.Equal("Eqdp", eqdp.GetProperty("Type").GetString());
        var m = eqdp.GetProperty("Manipulation");
        Assert.Equal("Highlander", m.GetProperty("Race").GetString());
        Assert.Equal("Male", m.GetProperty("Gender").GetString());
        Assert.Equal("Body", m.GetProperty("Slot").GetString());
        Assert.Equal(6091, m.GetProperty("SetId").GetInt32());
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(101, 201, false)]
    [InlineData(1401, 201, true)]
    [InlineData(101, 901, false)]
    [Trait("Plan", "AC-refit-other-race-4")]
    [Trait("Plan", "EDGE-refit-other-race-16")]
    public void With_nothing_picked_the_route_is_today_s(int drawn, int to, bool down)
    {
        var automatic = new RacialModelBake.Route((ushort)drawn, (ushort)to, down);
        Assert.Equal(automatic, OtherRaceRefit.Resolve(automatic, 0, 101, Offered(Shirt, TbseX())));

        // And a size remembered for the other race is not offered in the drawn race's lists.
        var target = TbseX();
        var chosen = new Dictionary<string, List<BodyOption>> { ["_top"] = [target.For("_top", "0301")[0]] };
        Assert.Empty(RetargetInputs.StillOffered(chosen, slot => target.For(slot, "0101")));
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-5")]
    public void The_other_sex_is_never_offered()
    {
        var target = TbseX(Body("0201", "_top", "F"), Body("0401", "_top", "F Highlander"), Body("1401", "_top", "F Au Ra"));
        Assert.Equal([(ushort)301], Offered(Shirt, target).Select(c => c.Race));

        const string womans = "chara/equipment/e6091/model/c0201e6091_top.mdl";
        var forHer = Offered(womans, target, [Model(womans, "shirt/c0201e6091_top.mdl")]).Select(c => c.Race).ToList();
        Assert.DoesNotContain((ushort)301, forHer);
        Assert.Contains((ushort)401, forHer);
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-6")]
    public void A_race_the_mod_already_has_a_model_of_is_pointed_at_not_baked()
    {
        var redirects = new[] { Model(Shirt, "shirt/c0101e6091_top.mdl"), Model(HighlanderShirt, "shirt/c0301e6091_top.mdl") };
        var offered = Offered(Shirt, TbseX(), redirects);
        var own = Assert.Single(offered);
        Assert.Equal(OtherRaceRefit.Block.OwnModel, own.Blocked);
        Assert.Equal(HighlanderShirt, own.OwnModelPath);
        // Picked anyway, it bakes nothing: the route stays as it was.
        Assert.Equal(RacialModelBake.Route.None, OtherRaceRefit.Resolve(RacialModelBake.Route.None, 301, 101, offered));
    }

    [Theory]
    [InlineData("TBSE-X IVCS2 — Large", "TBSE-X IVCS2 — Large (Highlander M)")]
    [InlineData("TBSE-X IVCS2 — M Highlander", "TBSE-X IVCS2 — M Highlander")]
    [Trait("Plan", "AC-refit-other-race-7")]
    [Trait("Plan", "EDGE-refit-other-race-14")]
    public void The_option_name_says_the_race_once(string name, string expected)
    {
        Assert.Equal(expected, OtherRaceRefit.OptionName(name, 301));
    }

    [Theory]
    [InlineData(true, 301, "None")]
    [InlineData(true, 101, "OtherRace")]
    [InlineData(true, 201, "OtherRace")]
    [InlineData(false, 301, "NotDrawn")]
    [InlineData(true, 0, "NotDrawn")]
    [Trait("Plan", "AC-refit-other-race-9")]
    [Trait("Plan", "EDGE-refit-other-race-24")]
    [Trait("Plan", "EDGE-refit-other-race-12")]
    public void The_preview_note_says_what_the_preview_is(bool draws, int character, string expected)
    {
        Assert.Equal(expected, OtherRaceRefit.NoteFor(draws, (ushort)character, 301).ToString());
        // Every note the panel can show has text of its own.
        Assert.NotEmpty(string.Format(Proteus.Localization.Strings.Parts.RetargetRacePreviewOtherFmt, "Highlander M"));
        Assert.NotEmpty(string.Format(Proteus.Localization.Strings.Parts.RetargetRacePreviewNotDrawnFmt, "Highlander M"));
    }

    // ── edge cases ──────────────────────────────────────────────────────────

    /// <summary>
    /// A pick is in force only for an offered race the mod has no model of. Picked anyway where the mod has its own,
    /// nothing may be refitted under it — even where the character's own route reaches that race.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-6")]
    [Trait("Plan", "EDGE-refit-other-race-13")]
    public void A_pick_is_in_force_only_for_an_offered_race_without_the_mod_s_own_model()
    {
        var free = Offered(Shirt, TbseX());
        Assert.True(OtherRaceRefit.InForce(301, 101, free));
        Assert.False(OtherRaceRefit.Blocked(301, free));
        Assert.False(OtherRaceRefit.InForce(0, 101, free));
        Assert.False(OtherRaceRefit.InForce(101, 101, free));   // the drawn race: as drawn, no bake

        var own = Offered(Shirt, TbseX(), [Model(Shirt, "s/mid.mdl"), Model(HighlanderShirt, "s/high.mdl")]);
        Assert.False(OtherRaceRefit.InForce(301, 101, own));
        Assert.True(OtherRaceRefit.Blocked(301, own));
        // A Highlander man playing reaches c0301 by himself; the pick still is not in force, and blocks.
        var his = new RacialModelBake.Route(101, 301, Down: false);
        Assert.Equal(his, OtherRaceRefit.Resolve(his, 301, 101, own));
        Assert.False(OtherRaceRefit.InForce(301, 101, own));
    }

    /// <summary>
    /// An extra skeleton is taken from the mod only where the saved refit will load it: the mod's default files or the
    /// very option it is cut from — not another option, even one publishing the same model (physics on and off).
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-19")]
    public void An_extra_skeleton_is_taken_only_from_files_the_refit_carries()
    {
        const string skeleton = "chara/human/c0301/skeleton/top/t6085/skl_c0301t6085.sklb";
        PenumbraModMeta.Redirect R(string game, string file, string source) => new(game, file, source);
        var redirects = new[]
        {
            R(Shirt, "shirt/c0101e6091_top.mdl", "Shirt / Physics on"),
            R(Shirt, "shirt/c0101e6091_top.mdl", "Shirt / Physics off"),
            R(skeleton, "on/skl.sklb", "Shirt / Physics on"),
        };
        Assert.Equal("Shirt / Physics on", OtherRaceRefit.CutFrom(redirects, "shirt/c0101e6091_top.mdl", "Shirt"));
        Assert.Null(OtherRaceRefit.CutFrom(redirects, "shirt/c0101e6091_top.mdl", "A new group"));
        Assert.Null(OtherRaceRefit.CutFrom(redirects, "shirt/c0101e6091_top.mdl", null));

        Assert.Equal("on/skl.sklb", OtherRaceRefit.CarriedFile(redirects, "Shirt / Physics on", skeleton));
        Assert.Null(OtherRaceRefit.CarriedFile(redirects, "Shirt / Physics off", skeleton));
        Assert.Null(OtherRaceRefit.CarriedFile(redirects, null, skeleton));
        Assert.Equal("dflt/skl.sklb", OtherRaceRefit.CarriedFile([.. redirects, R(skeleton, "dflt/skl.sklb", "")], null, skeleton));
    }

    /// <summary>
    /// Whether only the cut-from option supplies the extra skeleton — then a save cut from any other option is refused:
    /// its skeleton file, or for an entry the race already has, the option's own entry.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-21")]
    public void An_extra_skeleton_needs_its_option_only_when_the_option_supplies_it()
    {
        string path = EstTable.SkeletonPath(301, "_top", 6085)!;
        var carry = new EstPlan(EstOutcome.Carry, 6085);
        var present = new EstPlan(EstOutcome.Present, 6085);
        var noFiles = new HashSet<string>();
        EstTable.ModEntry[] itsEntry = [new(301, "Body", 6091, 6085)];

        Assert.True(OtherRaceRefit.EstNeedsOption(carry, 301, "_top", 6091, new HashSet<string> { path }, []));
        Assert.False(OtherRaceRefit.EstNeedsOption(carry, 301, "_top", 6091, noFiles, itsEntry));   // written by the save
        Assert.True(OtherRaceRefit.EstNeedsOption(present, 301, "_top", 6091, noFiles, itsEntry));
        Assert.False(OtherRaceRefit.EstNeedsOption(present, 301, "_top", 6091, noFiles, []));       // the game's
        Assert.False(OtherRaceRefit.EstNeedsOption(EstPlan.NotNeeded, 301, "_top", 6091, new HashSet<string> { path }, itsEntry));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-3")]
    public void A_pick_no_longer_offered_falls_back()
    {
        var automatic = new RacialModelBake.Route(101, 201, Down: false);
        Assert.Equal(automatic, OtherRaceRefit.Resolve(automatic, 301, 101, []));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-4")]
    [Trait("Plan", "EDGE-refit-other-race-8")]
    public void No_row_where_no_other_race_has_a_body_nor_for_the_game_s_gear()
    {
        Assert.Empty(Offered("chara/equipment/e6091/model/c0101e6091_dwn.mdl", TbseX()));
        Assert.Empty(OtherRaceRefit.Offered(Shirt, TbseX(), OnlyMidlander, null, isVanilla: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-5")]
    [Trait("Plan", "EDGE-refit-other-race-13")]
    public void No_choice_down_and_the_drawn_race_picked_is_as_drawn()
    {
        // A Highlander man's model is never offered for Midlanders.
        Assert.Empty(Offered(HighlanderShirt, TbseX(), [Model(HighlanderShirt, "s.mdl")]));
        // The drawn race picked: refitted as drawn, even where the character would have had it baked.
        var forRoegadyn = new RacialModelBake.Route(101, 901, Down: false);
        Assert.Equal(RacialModelBake.Route.None, OtherRaceRefit.Resolve(forRoegadyn, 101, 101, Offered(Shirt, TbseX())));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-7")]
    public void A_refit_this_tool_saved_for_the_race_is_not_the_author_s()
    {
        var ours = new BodyRetargetWriter.Record { Options = [new BodyRetargetWriter.Entry { File = "Body Retarget/a.mdl" }] };
        var redirects = new[] { Model(Shirt, "shirt/c0101e6091_top.mdl"), Model(HighlanderShirt, "Body Retarget/a.mdl") };
        Assert.Equal(OtherRaceRefit.Block.None, Assert.Single(Offered(Shirt, TbseX(), redirects, ours)).Blocked);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-9")]
    public void A_pick_belongs_to_the_model_it_was_made_on()
    {
        var choice = new OtherRaceRefit.Choice("shirt.mdl", 301);
        Assert.Equal(301, choice.For("shirt.mdl"));
        Assert.Equal(0, choice.For("trousers.mdl"));
        Assert.Equal(0, default(OtherRaceRefit.Choice).For("shirt.mdl"));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-10")]
    public void A_race_between_the_pick_and_the_drawn_one_the_author_made_is_pointed_at()
    {
        // Hrothgar men fall through to Roegadyn men before Midlanders: the author's Roegadyn model is what they draw.
        var target = TbseX(Body("1501", "_top", "M Hrothgar"));
        const string roegadyn = "chara/equipment/e6091/model/c0901e6091_top.mdl";
        var redirects = new[] { Model(Shirt, "s/c0101.mdl"), Model(roegadyn, "s/c0901.mdl") };
        var hrothgar = Offered(Shirt, target, redirects).Single(c => c.Race == 1501);
        Assert.Equal(OtherRaceRefit.Block.OwnModel, hrothgar.Blocked);
        Assert.Equal(roegadyn, hrothgar.OwnModelPath);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-11")]
    public void A_woman_playing_a_men_only_mod_gets_her_route_first_and_this_model_means_as_drawn()
    {
        // Her automatic route bakes the man's shirt for her: across the sexes, so a race none of the others is.
        var hers = new RacialModelBake.Route(101, 201, Down: false);
        var offered = Offered(Shirt, TbseX());
        var entries = OtherRaceRefit.PickerEntries(hers, 101, offered);
        Assert.Equal(OtherRaceRefit.EntryKind.AsWorn, entries[0].Kind);
        Assert.Equal(0, entries[0].Race);
        Assert.Equal(entries[0], OtherRaceRefit.Current(entries, 0, hers, 101));
        Assert.Equal(RacialModelBake.Route.None, OtherRaceRefit.Resolve(hers, 101, 101, offered));
        Assert.Equal(Up301, OtherRaceRefit.Resolve(hers, 301, 101, offered));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-12")]
    public void A_highlander_man_playing_keeps_today_s_default()
    {
        var target = TbseX();
        var races = RefitCore.RacesOf(target);
        var automatic = RefitCore.Route(Shirt, 301, c => races.Contains($"{c:D4}"), c => races.Contains($"{c:D4}"),
                                        OnlyMidlander, null);
        Assert.Equal(RacialModelBake.Route.None, automatic);
        var offered = Offered(Shirt, target);
        Assert.Contains(offered, c => c.Race == 301);
        Assert.Equal(RacialModelBake.Route.None, OtherRaceRefit.Resolve(automatic, 0, 101, offered));
        var entries = OtherRaceRefit.PickerEntries(automatic, 101, offered);
        Assert.Equal(OtherRaceRefit.EntryKind.ThisModel, OtherRaceRefit.Current(entries, 0, automatic, 101).Kind);
    }
}
