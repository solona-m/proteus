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
        Assert.Null(only.OwnModelPath);

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

    /// <summary>
    /// The rule this test was written for (AC-refit-other-race-5, "never the other sex") was changed on purpose by
    /// cross-sex-refit: the other sex is offered between the two Midlander models only, in the cells CrossSexRefit ships.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-5")]
    [Trait("Plan", "AC-cross-sex-refit-7")]
    public void The_other_sex_is_offered_only_between_the_midlander_models() // [AC-cross-sex-refit-7]
    {
        var target = TbseX(Body("0201", "_top", "F"), Body("0401", "_top", "F Highlander"), Body("1401", "_top", "F Au Ra"));
        var forHim = Offered(Shirt, target);
        Assert.Equal([(ushort)201, 301], forHim.Select(c => c.Race));
        Assert.True(forHim.Single(c => c.Race == 201).CrossSex);
        Assert.False(forHim.Single(c => c.Race == 301).CrossSex);

        // A women's top is offered no race of the other sex but Midlander (cross-sex-tops-yas made a women's top a men's,
        // where once it was not offered).
        const string womans = "chara/equipment/e6091/model/c0201e6091_top.mdl";
        var forHer = Offered(womans, target, [Model(womans, "shirt/c0201e6091_top.mdl")]).ToList();
        Assert.DoesNotContain(forHer, c => c.Race == 301);
        Assert.True(forHer.Single(c => c.Race == 101).CrossSex);
        Assert.Contains(forHer, c => c.Race == 401);
    }

    /// <summary>
    /// The rule this test was written for (AC-refit-other-race-6, "pointed at, not baked") was changed on purpose by
    /// own-model-overwrite: the mod's own model is said, and the pick is baked all the same.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-6")]
    [Trait("Plan", "AC-own-model-overwrite-2")]
    public void A_race_the_mod_already_has_a_model_of_is_warned_and_baked()
    {
        var redirects = new[] { Model(Shirt, "shirt/c0101e6091_top.mdl"), Model(HighlanderShirt, "shirt/c0301e6091_top.mdl") };
        var offered = Offered(Shirt, TbseX(), redirects);
        var own = Assert.Single(offered);
        Assert.Equal(HighlanderShirt, own.OwnModelPath);
        Assert.Equal(Up301, OtherRaceRefit.Resolve(RacialModelBake.Route.None, 301, 101, offered));
    }

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-1")]
    public void Nothing_greys_out_preview_or_save_for_a_race_the_mod_has_a_model_of()
    {
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                                                 | System.Reflection.BindingFlags.NonPublic;
        Assert.Null(typeof(OtherRaceRefit).GetMethod("Blocked", any));
        Assert.Null(typeof(OtherRaceRefit).GetNestedType("Block", any));
        Assert.DoesNotContain("pickBlocked", CrossSexRefitTests.Source("Proteus/Gui/BodyRetargetPanel.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-own-model-overwrite-3")]
    public void OwnModelOf_names_the_picked_race_s_author_model()
    {
        var own = Offered(Shirt, TbseX(), [Model(Shirt, "s/mid.mdl"), Model(HighlanderShirt, "s/high.mdl")]);
        Assert.Equal(HighlanderShirt, OtherRaceRefit.OwnModelOf(301, own)?.OwnModelPath);
        Assert.Null(OtherRaceRefit.OwnModelOf(0, own));
        Assert.Null(OtherRaceRefit.OwnModelOf(101, own));
        Assert.Null(OtherRaceRefit.OwnModelOf(301, Offered(Shirt, TbseX())));

        string said = string.Format(Proteus.Localization.Strings.Parts.RetargetRaceOwnModelReplacedFmt, "Midlander M");
        Assert.Equal(2, said.Split("Midlander M").Length - 1);
        Assert.Equal("This mod already has a model of its own that Midlander M wears. A refit for Midlander M saved into "
                   + "this mod takes its place while it is on. If that model is the author's own work, open it and refit "
                   + "it instead.", said);
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
    /// A pick is in force for every offered race, the mod's own model of it or not (own-model-overwrite changed this on
    /// purpose: it was in force only without one) — and never for none, or for the drawn race.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-6")]
    [Trait("Plan", "EDGE-refit-other-race-13")]
    [Trait("Plan", "AC-own-model-overwrite-2")]
    public void A_pick_is_in_force_for_every_offered_race()
    {
        var free = Offered(Shirt, TbseX());
        Assert.True(OtherRaceRefit.InForce(301, 101, free));
        Assert.False(OtherRaceRefit.InForce(0, 101, free));
        Assert.False(OtherRaceRefit.InForce(101, 101, free));   // the drawn race: as drawn, no bake

        var own = Offered(Shirt, TbseX(), [Model(Shirt, "s/mid.mdl"), Model(HighlanderShirt, "s/high.mdl")]);
        Assert.True(OtherRaceRefit.InForce(301, 101, own));
        Assert.False(OtherRaceRefit.InForce(0, 101, own));
        // A Highlander man playing reaches c0301 by himself; the pick gives the same route.
        var his = new RacialModelBake.Route(101, 301, Down: false);
        Assert.Equal(his, OtherRaceRefit.Resolve(his, 301, 101, own));
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
    /// The cut-from option is named as the mod spells it, whatever case the group was typed in; and where the option and
    /// the mod's default files both ship the extra skeleton, or both set the race's entry, the option's is the one taken —
    /// Penumbra lays an option over the default data.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-21")]
    public void The_cut_from_option_is_the_mod_s_own_and_wins_over_the_default_files()
    {
        const string skeleton = "chara/human/c0301/skeleton/top/t6085/skl_c0301t6085.sklb";
        PenumbraModMeta.Redirect R(string game, string file, string source) => new(game, file, source);
        var redirects = new[]
        {
            R(skeleton, "dflt/skl.sklb", ""),
            R(Shirt, "shirt/c0101e6091_top.mdl", "Summer Indigo Shirt / Everything"),
            R(skeleton, "opt/skl.sklb", "Summer Indigo Shirt / Everything"),
        };
        string? cutFrom = OtherRaceRefit.CutFrom(redirects, "shirt/c0101e6091_top.mdl", "summer indigo SHIRT");
        Assert.Equal("Summer Indigo Shirt / Everything", cutFrom);
        Assert.Equal("opt/skl.sklb", OtherRaceRefit.CarriedFile(redirects, cutFrom, skeleton));
        Assert.Equal("dflt/skl.sklb", OtherRaceRefit.CarriedFile(redirects, null, skeleton));

        // The entries in the order the panel reads them, the option's first: the option's 7000 is the race's.
        var tails = new SyntheticSkeleton.Tree("tails", ["n_root", "j_ex_top_a_l"], [-1, 0]);
        var plan = OtherRaceRefit.PlanEst(301, 101, "_top", 6091, ["j_ex_top_a_l"], new HashSet<string> { "n_root", "j_kosi" },
            [new EstTable.ModEntry(301, "Body", 6091, 7000), new EstTable.ModEntry(301, "Body", 6091, 6000)], null,
            path => path == EstTable.SkeletonPath(301, "_top", 7000) ? XivLiveMesh.HavokSkeleton.Read(SyntheticSkeleton.Sklb(tails)) : null);
        Assert.Equal((EstOutcome.Present, 7000), (plan.Outcome, plan.Entry));
    }

    /// <summary>
    /// The entries as the panel reads them out of a manifest: the cut-from option's ahead of the default data's, so where
    /// both set the race's entry the option's is taken — and no other option's at all.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-21")]
    public void Est_entries_are_the_cut_from_option_s_ahead_of_the_default_data_s()
    {
        static object Est(string race, int entry) => System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            Type = "Est",
            Manipulation = new { Gender = "Male", Race = race, SetId = 6091, Slot = "Body", Entry = entry },
        });
        var groups = System.Text.Json.JsonDocument.Parse("""
            [ { "Name": "Shirt", "Options": [
                { "Name": "Everything", "Files": {}, "Manipulations": [ { "Type": "Est", "Manipulation":
                    { "Gender": "Male", "Race": "Highlander", "SetId": 6091, "Slot": "Body", "Entry": 7000 } } ] },
                { "Name": "Other", "Files": {}, "Manipulations": [ { "Type": "Est", "Manipulation":
                    { "Gender": "Male", "Race": "Highlander", "SetId": 6091, "Slot": "Body", "Entry": 8000 } } ] } ] } ]
            """).RootElement.EnumerateArray().Select(g => (g.GetProperty("Name").GetString()!, g)).ToList();

        var entries = OtherRaceRefit.EstEntriesFor(groups, [Est("Highlander", 6000)], "Shirt / Everything");
        Assert.Equal([7000, 6000], entries.Select(e => e.Entry));
        Assert.Equal([6000], OtherRaceRefit.EstEntriesFor(groups, [Est("Highlander", 6000)], null).Select(e => e.Entry));
    }

    /// <summary>
    /// A change of where to save makes a picked race's model again only when it changes what the bake reads: the extra
    /// skeletons the option ships and the extra-skeleton entries it sets for this item. Most options give neither, and
    /// any two of those make the same model.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    public void Only_an_option_that_gives_the_bake_something_changes_its_fingerprint()
    {
        const string skeleton = "chara/human/c0301/skeleton/top/t6085/skl_c0301t6085.sklb";
        PenumbraModMeta.Redirect R(string game, string file, string source) => new(game, file, source);
        PenumbraModMeta.Redirect[] redirects =
        [
            R(Shirt, "s/c0101.mdl", "Size / M"),
            R("chara/equipment/e6091/material/v0001/mt_c0101e6091_top_a.mtrl", "s/a.mtrl", "Size / M"),
            R(Shirt, "s/c0101.mdl", "Size / L"),
            R(Shirt, "s/c0101.mdl", "Physics / On"),
            R(skeleton, "phys/skl.sklb", "Physics / On"),
        ];
        string Print(string? cutFrom, params EstTable.ModEntry[] est)
            => OtherRaceRefit.EstFingerprint(redirects, cutFrom, est, "_top", 6091);

        Assert.Equal("", Print(null));
        Assert.Equal("", Print("Size / M"));                                  // a material is nothing the bake reads
        Assert.Equal(Print("Size / M"), Print("Size / L"));
        Assert.NotEqual("", Print("Physics / On"));                           // ships an extra skeleton
        Assert.NotEqual("", Print("Size / L", new EstTable.ModEntry(301, "Body", 6091, 7000)));
        Assert.Equal("", Print("Size / L", new EstTable.ModEntry(301, "Body", 6092, 7000)));   // another item's
        Assert.Equal("", Print("Size / L", new EstTable.ModEntry(301, "Head", 6091, 7000)));   // the head's

        // Two options giving different things never share a model: another entry, or another skeleton file.
        Assert.NotEqual(Print("Size / L", new EstTable.ModEntry(301, "Body", 6091, 7000)),
                        Print("Size / L", new EstTable.ModEntry(301, "Body", 6091, 7001)));
        PenumbraModMeta.Redirect[] other =
        [
            .. redirects.Where(r => r.GamePath != skeleton),
            R(skeleton, "phys/other.sklb", "Physics / On"),
        ];
        Assert.NotEqual(Print("Physics / On"), OtherRaceRefit.EstFingerprint(other, "Physics / On", [], "_top", 6091));

        // Skeletons the bake never reads for this item — a hairstyle's, a base skeleton — are nothing to it.
        PenumbraModMeta.Redirect[] unread =
        [
            R("chara/human/c0301/skeleton/hair/h0001/skl_c0301h0001.sklb", "h.sklb", "Size / L"),
            R("chara/human/c0301/skeleton/base/b0001/skl_c0301b0001.sklb", "b.sklb", "Size / L"),
        ];
        Assert.Equal("", OtherRaceRefit.EstFingerprint(unread, "Size / L", [], "_top", 6091));

        // Spelled in capitals, or listed in another order, the same option gives the same fingerprint.
        PenumbraModMeta.Redirect[] shouting = [R(skeleton.ToUpperInvariant(), "phys/skl.sklb", "Physics / On")];
        Assert.Equal(Print("Physics / On"), OtherRaceRefit.EstFingerprint(shouting, "Physics / On", [], "_top", 6091));
        Assert.Equal(Print("Physics / On"), OtherRaceRefit.EstFingerprint(redirects.Reverse(), "Physics / On", [], "_top", 6091));
    }

    /// <summary>A save of a picked race's model is refused only when where it goes now gives the bake something else;
    /// the character's own bakes, which read nothing of the option, never are.</summary>
    [Theory]
    [InlineData(true, "", "", false)]
    [InlineData(true, "a|", "a|", false)]
    [InlineData(true, "", "a|", true)]
    [InlineData(true, "a|", "b|", true)]
    [InlineData(false, "", "a|", false)]
    [Trait("Plan", "AC-refit-other-race-8")]
    public void A_save_is_refused_only_when_its_option_changes_what_the_bake_read(bool picked, string baked, string now,
                                                                               bool refused)
        => Assert.Equal(refused, OtherRaceRefit.RefusesSave(picked, baked, now));

    /// <summary>
    /// The option the bake reads its extra skeleton by is the one the save carries, for an author's manifest too —
    /// Penumbra writes those with backslashes and its own case, the panel with forward slashes.
    /// </summary>
    [Theory]
    [InlineData("Shirt Size", "Shirt Size / M")]
    [InlineData("shirt size", "Shirt Size / M")]
    [InlineData("Other", null)]
    [Trait("Plan", "AC-refit-other-race-8")]
    public void The_bake_s_cut_from_is_the_save_s(string group, string? expected)
    {
        PenumbraModMeta.Redirect[] redirects =
        [
            new(Shirt, "shirt size\\m\\chara\\equipment\\e6091\\model\\c0101e6091_top.mdl", "Shirt Size / M"),
        ];
        const string model = "Shirt Size/M/chara/equipment/e6091/model/c0101e6091_top.mdl";
        Assert.Equal(expected, OtherRaceRefit.CutFrom(redirects, model, group));
        string? option = BodyRetargetWriter.OptionOfFile(redirects, model, group);
        Assert.Equal(expected, option == null ? null : "Shirt Size / " + option);
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
    [Trait("Plan", "AC-vanilla-cross-sex-legs-6")]
    public void No_row_where_no_other_race_has_a_body_nor_for_the_game_s_gear() // [AC-vanilla-cross-sex-legs-6]
    {
        Assert.Empty(Offered("chara/equipment/e6091/model/c0101e6091_dwn.mdl", TbseX()));
        Assert.Empty(OtherRaceRefit.Offered(Shirt, TbseX(), OnlyMidlander, null, isVanilla: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-5")]
    [Trait("Plan", "EDGE-refit-other-race-13")]
    public void The_drawn_race_picked_is_as_drawn()
    {
        // The drawn race picked: refitted as drawn, even where the character would have had it baked up.
        var forRoegadyn = new RacialModelBake.Route(101, 901, Down: false);
        Assert.Equal(RacialModelBake.Route.None, OtherRaceRefit.Resolve(forRoegadyn, 101, 101, Offered(Shirt, TbseX())));
    }

    // ── a race below the drawn one (refit-race-down) ────────────────────────

    private static readonly PenumbraModMeta.Redirect[] OnlyHighlander = [Model(HighlanderShirt, "shirt/c0301e6091_top.mdl")];

    private static readonly RacialModelBake.Route Lowered101 = new(301, 101, Down: false, Lowered: true);

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(301)]
    [InlineData(201)]
    [Trait("Plan", "AC-refit-race-down-1")]
    [Trait("Plan", "EDGE-refit-race-down-8")]
    public void A_highlander_only_top_is_offered_midlander_whatever_the_wearer(int wearer)
    {
        var target = TbseX(Body("0201", "_top", "F"));
        var offered = Offered(HighlanderShirt, target, OnlyHighlander);
        var only = Assert.Single(offered);   // never Midlander F
        Assert.Equal((101, (string?)null, true), (only.Race, only.OwnModelPath, only.Below));

        var races = RefitCore.RacesOf(target);
        var automatic = wearer == 0 ? RacialModelBake.Route.None
            : RefitCore.Route(HighlanderShirt, (ushort)wearer, c => races.Contains($"{c:D4}"),
                              c => races.Contains($"{c:D4}"), OnlyHighlander, null);
        var entries = OtherRaceRefit.PickerEntries(automatic, 301, offered);
        Assert.Contains(entries, e => e.Kind == OtherRaceRefit.EntryKind.ThisModel && e.Race == 301);
        Assert.Contains(entries, e => e.Kind == OtherRaceRefit.EntryKind.Other && e.Race == 101);
        Assert.Equal(Lowered101, OtherRaceRefit.Resolve(automatic, 101, 301, offered));

        // A Lalafell woman's model has no race of her sex below it.
        const string lala = "chara/equipment/e6091/model/c1201e6091_top.mdl";
        Assert.Empty(Offered(lala, TbseX(Body("1201", "_top", "Lala F"), Body("0201", "_top", "F")), [Model(lala, "l.mdl")]));
    }

    [Fact]
    [Trait("Plan", "AC-refit-race-down-2")]
    public void Picking_midlander_lowers_and_every_list_is_midlander_s()
    {
        var target = TbseX();
        var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 301, Offered(HighlanderShirt, target, OnlyHighlander));
        Assert.Equal(Lowered101, route);
        Assert.True(route.ChangesModel);
        Assert.True(route.BendsDown);
        Assert.Equal("0101", route.Race(HighlanderShirt));
        Assert.True(OtherRaceRefit.InForce(101, 301, Offered(HighlanderShirt, target, OnlyHighlander)));
        var onto = OtherRaceRefit.OnlyRace(target, 101);
        Assert.Equal(["_top", "_dwn"], onto.SlotsFor("0101"));
        Assert.DoesNotContain(onto.Options, o => BodySizeCatalog.RaceOf(o.GamePath) != "0101");
    }

    [Fact]
    [Trait("Plan", "AC-refit-race-down-4")]
    public void The_note_says_who_wears_a_lowered_refit()
    {
        Assert.Equal(OtherRaceRefit.WearNote.PickedAndFallThrough, OtherRaceRefit.Wears(Lowered101));
        Assert.Equal(OtherRaceRefit.WearNote.OnlyPicked, OtherRaceRefit.Wears(Up301));
        string said = string.Format(Proteus.Localization.Strings.Parts.RetargetRaceBelowFmt, "Midlander M", "Highlander M");
        Assert.Contains("Midlander M", said);
        Assert.Contains("Highlander M", said);
        // And the note above it says a model is made, never that none exists: Midlanders wear the game's today.
        string made = string.Format(Proteus.Localization.Strings.Parts.RetargetLoweredFmt, "Highlander M", "Midlander M");
        Assert.DoesNotContain("No Midlander", made);
        Assert.Contains("Midlander M", made);
    }

    /// <summary>
    /// The rule this test was written for (AC-refit-race-down-5, the author's Midlander model blocks) was changed on
    /// purpose by own-model-overwrite: it is said, and the lowered pick is in force.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-race-down-5")]
    [Trait("Plan", "EDGE-refit-race-down-5")]
    [Trait("Plan", "EDGE-own-model-overwrite-3")]
    public void A_midlander_model_the_author_made_is_warned_and_ours_is_not()
    {
        var authors = new[] { Model(HighlanderShirt, "s/c0301.mdl"), Model(Shirt, "s/c0101.mdl") };
        var own = Assert.Single(Offered(HighlanderShirt, TbseX(), authors));
        Assert.Equal(Shirt, own.OwnModelPath);
        Assert.Equal(Lowered101, OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 301, [own]));
        Assert.True(OtherRaceRefit.InForce(101, 301, [own]));

        var ours = new BodyRetargetWriter.Record { Options = [new BodyRetargetWriter.Entry { File = "Body Retarget/m.mdl" }] };
        var saved = new[] { Model(HighlanderShirt, "s/c0301.mdl"), Model(Shirt, "Body Retarget/m.mdl") };
        Assert.Null(Assert.Single(Offered(HighlanderShirt, TbseX(), saved, ours)).OwnModelPath);
    }

    [Theory]
    [InlineData(true, 301, "OtherRace")]   // a Highlander draws the preview, bent onto him
    [InlineData(false, 101, "NotDrawn")]   // a Midlander draws the game's c0101, not this model
    [InlineData(true, 101, "None")]
    [Trait("Plan", "AC-refit-race-down-6")]
    public void The_lowered_preview_note_follows_who_draws_the_highlander_path(bool draws, int character, string expected)
        => Assert.Equal(expected, OtherRaceRefit.NoteFor(draws, (ushort)character, 101).ToString());

    [Fact]
    [Trait("Plan", "AC-refit-race-down-7")]
    [Trait("Plan", "EDGE-refit-race-down-9")]
    public void Every_same_sex_ancestor_with_bodies_is_offered_below()
    {
        var target = TbseX(Body("0901", "_top", "M Roegadyn"), Body("1501", "_top", "M Hrothgar"));
        const string hrothgar = "chara/equipment/e6091/model/c1501e6091_top.mdl";
        var forHim = Offered(hrothgar, target, [Model(hrothgar, "h.mdl")]);
        Assert.Equal([(901, true), (101, true)], forHim.Select(c => ((int)c.Race, c.Below)).OrderByDescending(x => x.Item1));

        const string roegadyn = "chara/equipment/e6091/model/c0901e6091_top.mdl";
        var forRoe = Offered(roegadyn, target, [Model(roegadyn, "r.mdl")]);
        Assert.Equal([(101, true), (1501, false)], forRoe.Select(c => ((int)c.Race, c.Below)).OrderBy(x => x.Item1));
        Assert.Equal(new RacialModelBake.Route(901, 1501, false), OtherRaceRefit.Resolve(default, 1501, 901, forRoe));
        Assert.Equal(new RacialModelBake.Route(901, 101, false, true), OtherRaceRefit.Resolve(default, 101, 901, forRoe));

        Assert.Equal([1501, 901, 101], OtherRaceRefit.LineDown(1501, 101).Select(r => (int)r));
        Assert.Equal([301, 101], OtherRaceRefit.LineDown(301, 101).Select(r => (int)r));
        Assert.Empty(OtherRaceRefit.LineDown(301, 201));
        Assert.Empty(OtherRaceRefit.LineDown(101, 301));

        // The women's lines: Highlander and Au Ra women to Midlander women.
        var hers = TbseX(Body("0201", "_top", "F"));
        foreach (int race in new[] { 401, 1401 })
        {
            string path = $"chara/equipment/e6091/model/c{race:D4}e6091_top.mdl";
            var c = Assert.Single(Offered(path, hers, [Model(path, "f.mdl")]));
            Assert.Equal((201, true), (c.Race, c.Below));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-1")]
    public void This_model_keeps_the_character_s_down_route()
    {
        var autoDown = new RacialModelBake.Route(301, 101, Down: true);
        var offered = Offered(HighlanderShirt, TbseX(), OnlyHighlander);
        Assert.Equal(autoDown, OtherRaceRefit.Resolve(autoDown, 301, 301, offered));
        var entries = OtherRaceRefit.PickerEntries(autoDown, 301, offered);
        Assert.Equal(OtherRaceRefit.EntryKind.ThisModel, OtherRaceRefit.Current(entries, 301, autoDown, 301).Kind);
        Assert.Equal(OtherRaceRefit.EntryKind.ThisModel, OtherRaceRefit.Current(entries, 0, autoDown, 301).Kind);
        // An up route the character would have is still dropped for "this model", as before.
        Assert.Equal(RacialModelBake.Route.None, OtherRaceRefit.Resolve(Up301, 101, 101, Offered(Shirt, TbseX())));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-6")]
    [Trait("Plan", "EDGE-own-model-overwrite-2")]
    public void A_model_between_the_drawn_race_and_the_pick_does_not_block_it()
    {
        var target = TbseX(Body("0901", "_top", "M Roegadyn"), Body("1501", "_top", "M Hrothgar"));
        const string hrothgar = "chara/equipment/e6091/model/c1501e6091_top.mdl";
        const string roegadyn = "chara/equipment/e6091/model/c0901e6091_top.mdl";
        var offered = Offered(hrothgar, target, [Model(hrothgar, "h.mdl"), Model(roegadyn, "r.mdl")]);
        Assert.Null(offered.Single(c => c.Race == 101).OwnModelPath);
        Assert.True(OtherRaceRefit.InForce(101, 1501, offered));
        Assert.Equal(roegadyn, offered.Single(c => c.Race == 901).OwnModelPath);
        Assert.True(OtherRaceRefit.InForce(901, 1501, offered));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-7")]
    public void The_character_s_down_route_and_the_pick_are_different_models()
    {
        const string auri = "chara/equipment/e0258/model/c1401e0258_dwn.mdl";
        var down = new RacialModelBake.Route(1401, 201, Down: true);
        var lowered = new RacialModelBake.Route(1401, 201, Down: false, Lowered: true);
        Assert.NotEqual(down, lowered);
        Assert.NotEqual(down.Tag, lowered.Tag);
        Assert.NotEqual(Up301.Tag, lowered.Tag);
        Assert.Equal(auri, down.SavePath(auri));
        Assert.Equal("chara/equipment/e0258/model/c0201e0258_dwn.mdl", lowered.SavePath(auri));
        Assert.False(down.ChangesModel);
        Assert.True(lowered.ChangesModel);
    }

    [Theory]
    [InlineData("TBSE-X IVCS2 — Large", "TBSE-X IVCS2 — Large (Midlander M)")]
    [InlineData("TBSE-X IVCS2 — M Midlander Miqo'te Au'ra Elezen Viera", "TBSE-X IVCS2 — M Midlander Miqo'te Au'ra Elezen Viera")]
    [Trait("Plan", "EDGE-refit-race-down-13")]
    public void The_option_name_says_midlander_once(string name, string expected)
        => Assert.Equal(expected, OtherRaceRefit.OptionName(name, 101));

    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-14")]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-6")]
    public void No_row_below_for_the_game_s_gear() // [AC-vanilla-cross-sex-legs-6]
        => Assert.Empty(OtherRaceRefit.Offered(HighlanderShirt, TbseX(), OnlyHighlander, null, isVanilla: true));

    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-15")]
    public void A_lowered_extra_skeleton_garment_warns_about_fall_through_wearers()
    {
        var carry = new EstPlan(EstOutcome.Carry, 6085);
        var present = new EstPlan(EstOutcome.Present, 7000);
        Assert.True(OtherRaceRefit.WarnsFallThroughBones(Lowered101, carry));
        Assert.True(OtherRaceRefit.WarnsFallThroughBones(Lowered101, present));
        Assert.False(OtherRaceRefit.WarnsFallThroughBones(Lowered101, EstPlan.NotNeeded));
        Assert.False(OtherRaceRefit.WarnsFallThroughBones(Lowered101, null));
        Assert.False(OtherRaceRefit.WarnsFallThroughBones(Up301, carry));
        Assert.Contains("Midlander M", string.Format(Proteus.Localization.Strings.Parts.RetargetRaceBelowExtraBonesFmt, "Midlander M"));
    }

    /// <summary>
    /// After a save, a Midlander man draws the lowered refit at c0101 from the garment mod: auto refit sees a Midlander
    /// model on a Midlander, with no route at all — as it sees a Highlander refit saved for a Highlander.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-race-down-3")]
    public void Auto_refit_reads_a_saved_midlander_refit_as_a_plain_midlander_model()
    {
        var races = RefitCore.RacesOf(TbseX());
        var saved = new[] { Model(HighlanderShirt, "s/c0301.mdl"), Model(Shirt, "Body Retarget/m.mdl") };
        var ours = new BodyRetargetWriter.Record { Options = [new BodyRetargetWriter.Entry { File = "Body Retarget/m.mdl" }] };
        Assert.Equal(RacialModelBake.Route.None,
                     RefitCore.Route(Lowered101.SavePath(HighlanderShirt), 101, c => races.Contains($"{c:D4}"),
                                     c => races.Contains($"{c:D4}"), saved, ours));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-7")]
    [Trait("Plan", "EDGE-own-model-overwrite-1")]
    public void A_refit_this_tool_saved_for_the_race_is_not_the_author_s()
    {
        var ours = new BodyRetargetWriter.Record { Options = [new BodyRetargetWriter.Entry { File = "Body Retarget/a.mdl" }] };
        var redirects = new[] { Model(Shirt, "shirt/c0101e6091_top.mdl"), Model(HighlanderShirt, "Body Retarget/a.mdl") };
        var offered = Offered(Shirt, TbseX(), redirects, ours);
        Assert.Null(Assert.Single(offered).OwnModelPath);
        Assert.Null(OtherRaceRefit.OwnModelOf(301, offered));
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
    [Trait("Plan", "EDGE-own-model-overwrite-2")]
    public void A_race_between_the_pick_and_the_drawn_one_the_author_made_is_warned_not_blocked()
    {
        // Hrothgar men fall through to Roegadyn men before Midlanders: the author's Roegadyn model is what they draw.
        // EDGE-refit-other-race-10 pointed at it and refused; own-model-overwrite changed that on purpose.
        var target = TbseX(Body("1501", "_top", "M Hrothgar"));
        const string roegadyn = "chara/equipment/e6091/model/c0901e6091_top.mdl";
        var redirects = new[] { Model(Shirt, "s/c0101.mdl"), Model(roegadyn, "s/c0901.mdl") };
        var offered = Offered(Shirt, target, redirects);
        Assert.Equal(roegadyn, offered.Single(c => c.Race == 1501).OwnModelPath);
        Assert.True(OtherRaceRefit.InForce(1501, 101, offered));
        Assert.Equal(new RacialModelBake.Route(101, 1501, false),
                     OtherRaceRefit.Resolve(RacialModelBake.Route.None, 1501, 101, offered));
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-4")]
    public void Game_gear_never_has_an_own_model()
    {
        const string womans = "chara/equipment/e0258/model/c0201e0258_dwn.mdl";
        var offered = OtherRaceRefit.Offered(womans, TbseX(), [], null, isVanilla: true, gameHas: _ => true);
        Assert.NotEmpty(offered);
        Assert.All(offered, c => Assert.Null(c.OwnModelPath));
        Assert.All(offered, c => Assert.Null(OtherRaceRefit.OwnModelOf(c.Race, offered)));
        Assert.Equal((ushort?)101, OtherRaceRefit.ReplacesGame(101, offered)?.Race);
    }

    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-11")]
    public void The_cross_sex_hint_names_a_race_the_mod_has_a_model_of()
    {
        const string womans = "chara/equipment/e6091/model/c0201e6091_top.mdl";
        var offered = Offered(womans, TbseX(), [Model(womans, "w.mdl"), Model(Shirt, "m.mdl")]);
        Assert.Equal((ushort?)101, OtherRaceRefit.CrossSexHint(offered)?.Race);
        Assert.Equal(Shirt, OtherRaceRefit.CrossSexHint(offered)?.OwnModelPath);
    }

    /// <summary>A model swapped in for the race (FileSwaps) is not seen as the mod's own: a documented limit. The pick is
    /// in force all the same.</summary>
    [Fact]
    [Trait("Plan", "EDGE-own-model-overwrite-18")]
    public void A_file_swap_author_model_is_a_documented_limit()
    {
        // Redirects are files: a swap is never one, so none at c0301 here.
        var offered = Offered(Shirt, TbseX(), OnlyMidlander);
        Assert.Null(OtherRaceRefit.OwnModelOf(301, offered));
        Assert.True(OtherRaceRefit.InForce(301, 101, offered));
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

    // ── the game's own women's legs made men's (vanilla-cross-sex-legs) ─────

    private const string Pantaloons = "chara/equipment/e6224/model/c0201e6224_dwn.mdl";
    private const string MensPantaloons = "chara/equipment/e6224/model/c0101e6224_dwn.mdl";

    private static readonly RacialModelBake.Route Across101 = new(201, 101, Down: false, Lowered: true) { ByAlias = true };

    private static IReadOnlyList<OtherRaceRefit.Candidate> Game(string path, BodySizeCatalog target,
                                                                Func<string, bool>? gameHas = null)
        => OtherRaceRefit.Offered(path, target, [], null, isVanilla: true, gameHas);

    [Fact]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-1")]
    public void Vanilla_womens_legs_are_offered_midlander_men_on_tbse() // [AC-vanilla-cross-sex-legs-1]
    {
        var offered = Game(Pantaloons, TbseX());
        var men = Assert.Single(offered);
        Assert.Equal(101, men.Race);
        Assert.True(men.CrossSex);
        Assert.True(men.Below);
        Assert.Null(men.OwnModelPath);

        var entries = OtherRaceRefit.PickerEntries(RacialModelBake.Route.None, 201, offered);
        Assert.Contains(entries, e => e.Race == 201 && e.Kind == OtherRaceRefit.EntryKind.ThisModel);
        Assert.Contains(entries, e => e.Race == 101 && e.Kind == OtherRaceRefit.EntryKind.OtherSex);
        Assert.Equal(Across101, OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, offered));
        Assert.True(OtherRaceRefit.InForce(101, 201, offered));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-2")]
    public void The_no_bodies_hint_names_midlander_men_for_vanilla_legs() // [AC-vanilla-cross-sex-legs-2]
    {
        var hint = OtherRaceRefit.CrossSexHint(Game(Pantaloons, TbseX()));
        Assert.Equal((ushort)101, hint?.Race);
        Assert.Null(OtherRaceRefit.CrossSexHint([]));
        Assert.Contains("Midlander", string.Format(Localization.Strings.Parts.RetargetRaceCrossSexHintFmt,
                                                   ModelRace.Describe($"{hint!.Race:D4}")));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-4")]
    public void A_vanilla_cross_sex_pick_saves_at_the_c0101_path() // [AC-vanilla-cross-sex-legs-4]
    {
        var route = OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, Game(Pantaloons, TbseX()));
        Assert.Equal(MensPantaloons, route.SavePath(Pantaloons));
        Assert.NotNull(route.Eqdp(route.SavePath(Pantaloons)));
        Assert.EndsWith($"({ModelRace.Describe("0101")})", OtherRaceRefit.OptionName("The Body SE — SFW", 101));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-5")]
    public void A_vanilla_pick_says_it_replaces_the_game_model() // [AC-vanilla-cross-sex-legs-5]
    {
        var offered = Game(Pantaloons, TbseX(), p => p == MensPantaloons);
        Assert.Equal(MensPantaloons, Assert.Single(offered).GameModelPath);
        Assert.Equal(MensPantaloons, OtherRaceRefit.ReplacesGame(101, offered)?.GameModelPath);
        Assert.Null(OtherRaceRefit.ReplacesGame(0, offered));
        Assert.Null(OtherRaceRefit.ReplacesGame(201, offered));
    }

    [Fact]
    [Trait("Plan", "AC-vanilla-cross-sex-legs-8")]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-16")]
    public void A_man_whose_race_keeps_the_game_model_is_warned() // [AC-vanilla-cross-sex-legs-8] [EDGE-vanilla-cross-sex-legs-16]
    {
        IReadOnlyList<ushort> wearers = [701, 1101];
        Assert.True(OtherRaceRefit.MissesCharacter(301, Across101, wearers));
        Assert.False(OtherRaceRefit.MissesCharacter(101, Across101, wearers));
        Assert.False(OtherRaceRefit.MissesCharacter(701, Across101, wearers));
        Assert.False(OtherRaceRefit.MissesCharacter(201, Across101, wearers));
        Assert.False(OtherRaceRefit.MissesCharacter(801, Across101, wearers));
        Assert.False(OtherRaceRefit.MissesCharacter(0, Across101, wearers));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-1")]
    public void Vanilla_womens_top_is_not_offered() // [EDGE-vanilla-cross-sex-legs-1]
    {
        var offered = Game("chara/equipment/e6224/model/c0201e6224_top.mdl", TbseX());
        Assert.Empty(offered);
        Assert.Null(OtherRaceRefit.CrossSexHint(offered));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-2")]
    public void Vanilla_mens_legs_are_not_offered_women() // [EDGE-vanilla-cross-sex-legs-2]
        => Assert.Empty(Game(MensPantaloons, new BodySizeCatalog("root", [Body("0201", "_dwn", "Bibo")])));

    [Theory]
    [InlineData("1401")]
    [InlineData("0401")]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-3")]
    public void Other_womens_race_vanilla_legs_are_not_offered(string race) // [EDGE-vanilla-cross-sex-legs-3]
        => Assert.Empty(Game($"chara/equipment/e6224/model/c{race}e6224_dwn.mdl", TbseX()));

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-4")]
    public void Vanilla_legs_never_get_a_same_sex_race() // [EDGE-vanilla-cross-sex-legs-4]
        => Assert.Equal([101], Game(Pantaloons, TbseX(Body("0401", "_dwn", "F Highlander"))).Select(c => (int)c.Race));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-5")]
    public void No_game_model_no_replace_note(bool asked) // [EDGE-vanilla-cross-sex-legs-5]
    {
        var offered = Game(Pantaloons, TbseX(), asked ? _ => false : null);
        Assert.Null(Assert.Single(offered).GameModelPath);
        Assert.Null(OtherRaceRefit.ReplacesGame(101, offered));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-6")]
    public void No_mens_legs_no_vanilla_offer() // [EDGE-vanilla-cross-sex-legs-6]
        => Assert.Empty(Game(Pantaloons, new BodySizeCatalog("root", [Body("0101", "_top", "M")])));

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-10")]
    public void A_vanilla_pick_falls_back_when_the_body_mod_has_no_men() // [EDGE-vanilla-cross-sex-legs-10]
    {
        var offered = Game(Pantaloons, new BodySizeCatalog("root", [Body("0201", "_dwn", "Bibo")]), _ => true);
        Assert.Empty(offered);
        Assert.Equal(RacialModelBake.Route.None, OtherRaceRefit.Resolve(RacialModelBake.Route.None, 101, 201, offered));
        Assert.Null(OtherRaceRefit.ReplacesGame(101, offered));
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-13")]
    public void The_preview_on_a_woman_says_it_is_bent_onto_her() // [EDGE-vanilla-cross-sex-legs-13]
        => Assert.Equal(OtherRaceRefit.PreviewNote.OtherRace, OtherRaceRefit.NoteFor(true, 201, 101));

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-14")]
    public void Modded_offers_are_unchanged() // [EDGE-vanilla-cross-sex-legs-14]
    {
        foreach (var (path, target, redirects) in new[]
                 {
                     (Shirt, TbseX(), OnlyMidlander),
                     (HighlanderShirt, TbseX(Body("0201", "_top", "F")), OnlyHighlander),
                     (Pantaloons, TbseX(), Array.Empty<PenumbraModMeta.Redirect>()),
                 })
        {
            var before = OtherRaceRefit.Offered(path, target, redirects, null, isVanilla: false);
            var asked = OtherRaceRefit.Offered(path, target, redirects, null, isVanilla: false, _ => true);
            Assert.Equal(before, asked);
            Assert.All(asked, c => Assert.Null(c.GameModelPath));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-vanilla-cross-sex-legs-15")]
    public void A_man_with_the_womens_model_open_sees_the_not_drawn_note() // [EDGE-vanilla-cross-sex-legs-15]
        => Assert.Equal(OtherRaceRefit.PreviewNote.NotDrawn, OtherRaceRefit.NoteFor(false, 101, 101));
}
