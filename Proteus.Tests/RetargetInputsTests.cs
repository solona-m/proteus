using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What the Studio's Body size tool refits a garment by (<see cref="RetargetInputs"/>): its rows, what a refit is made
/// from, its name and its key. Without kept ends every answer must be the one the tool always gave — the literals below
/// were written from the panel's code before it came to go through here.
/// </summary>
public class RetargetInputsTests
{
    private static readonly BodyOption LegsS = new("Legs", "", "S", "legs/s.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
    private static readonly BodyOption LegsM = new("Legs", "", "M", "legs/m.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
    private static readonly BodyOption FeetS = new("Feet", "", "S", "feet/s.mdl", "chara/equipment/e0000/model/c0201e0000_sho.mdl", "_sho");
    private static readonly BodyOption FeetM = new("Feet", "", "M", "feet/m.mdl", "chara/equipment/e0000/model/c0201e0000_sho.mdl", "_sho");
    private static readonly BodyOption HandsS = new("Hands", "", "S", "hands/s.mdl", "chara/equipment/e0000/model/c0201e0000_glv.mdl", "_glv");
    private static readonly BodyOption HandsM = new("Hands", "", "M", "hands/m.mdl", "chara/equipment/e0000/model/c0201e0000_glv.mdl", "_glv");

    private static readonly string[] CatalogSlots = ["_top", "_dwn", "_glv", "_sho"];

    /// <summary>Every slot of the body has two models, so every slot is offered.</summary>
    private static int Two(string _) => 2;

    private static RetargetInputs.Layout Shoe(bool keep = true, bool unreadable = false)
        => RetargetInputs.LayoutFor("_sho", keep, sourceHasLimb: true, targetHasLimb: true, unreadable);

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-3")]
    public void Shoe_layout_puts_legs_first_and_leaves_feet_out()   // [AC-keep-ends-studio-3]
    {
        var shoe = Shoe();
        Assert.Equal(new RetargetInputs.Layout("_dwn", "_sho", "_sho"), shoe);
        Assert.Equal(["_dwn", "_top", "_glv"], RetargetInputs.Slots(shoe, CatalogSlots, Two));

        var glove = RetargetInputs.LayoutFor("_glv", true, true, true, false);
        Assert.Equal(new RetargetInputs.Layout("_top", "_glv", "_glv"), glove);
        Assert.Equal(["_top", "_dwn", "_sho"], RetargetInputs.Slots(glove, CatalogSlots, Two));

        // Chests and legs are laid out as they always were, box or no box.
        foreach (string slot in new[] { "_top", "_dwn" })
        {
            var layout = RetargetInputs.LayoutFor(slot, true, true, true, false);
            Assert.Equal(new RetargetInputs.Layout(slot, slot, null), layout);
            Assert.False(RetargetInputs.OffersKeepEnds(slot));
        }
        Assert.True(RetargetInputs.OffersKeepEnds("_sho"));
        Assert.True(RetargetInputs.OffersKeepEnds("_glv"));
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-3")]
    public void Keep_ends_inputs_carry_the_other_chosen_slots()   // [AC-keep-ends-studio-3]
    {
        var layout = Shoe();
        var slots = RetargetInputs.Slots(layout, CatalogSlots, Two);
        var from = new Dictionary<string, BodyOption> { ["_dwn"] = LegsS, ["_glv"] = HandsS, ["_sho"] = FeetS };
        var to = new Dictionary<string, List<BodyOption>> { ["_dwn"] = [LegsM], ["_glv"] = [HandsM], ["_sho"] = [FeetM] };

        var inputs = RetargetInputs.Assemble(layout, slots, from, to, KeepEnds.Route.Skin);

        Assert.True(inputs.KeepEnds);
        Assert.False(inputs.SkinOnly);
        Assert.Equal("_sho", inputs.GarmentSlot);
        Assert.Equal("_dwn", inputs.Primary);
        Assert.Equal([LegsM], inputs.PrimaryTargets);
        // The hands ride along; the feet, chosen once for the old rows, are offered in none and go in for their shape.
        Assert.Equal([("_glv", HandsS, HandsM)], inputs.Shared);
        var chosen = RetargetInputs.Chosen(slots, from, to);
        Assert.Equal("LavaBod+ — M + M", RefitCore.OptionName("LavaBod+", RetargetInputs.ToLabels(inputs, chosen, LegsM, to)));
        Assert.Equal(["S", "S"], RetargetInputs.FromLabels(inputs, chosen, from));
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-4")]
    [Trait("Plan", "EDGE-keep-ends-studio-27")]
    public void Skin_only_is_ready_with_one_result_and_its_labels()   // [AC-keep-ends-studio-4] [EDGE-keep-ends-studio-27]
    {
        var layout = Shoe();
        var slots = RetargetInputs.Slots(layout, CatalogSlots, Two);
        // Its own feet read; no legs "made for" — it reaches none — though two legs sizes were remembered to go onto.
        var from = new Dictionary<string, BodyOption> { ["_sho"] = FeetM };
        var to = new Dictionary<string, List<BodyOption>> { ["_dwn"] = [LegsS, LegsM] };

        var inputs = RetargetInputs.Assemble(layout, slots, from, to, KeepEnds.Route.SkinOnly);

        Assert.True(inputs.SkinOnly);
        Assert.True(RetargetInputs.Ready(inputs, from, targetHasEnd: true, refused: false));
        Assert.False(RetargetInputs.Ready(inputs, from, targetHasEnd: false, refused: false));
        var chosen = RetargetInputs.Chosen(slots, from, to);
        Assert.Equal([KeepEnds.SkinOnlyLabel], RetargetInputs.ToLabels(inputs, chosen, LegsS, to));
        Assert.Equal([KeepEnds.SkinOnlyLabel], RetargetInputs.ToLabels(inputs, chosen, LegsM, to));
        Assert.Equal(["M"], RetargetInputs.FromLabels(inputs, chosen, from));
        Assert.Equal("Neolithe — skin",
                     RefitCore.OptionName("Neolithe", RetargetInputs.ToLabels(inputs, chosen, LegsS, to)));
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-4")]
    [Trait("Plan", "EDGE-keep-ends-studio-25")]
    public void A_weak_skin_only_reading_still_fills_in_its_own_feet()   // [AC-keep-ends-studio-4] [EDGE-keep-ends-studio-25]
    {
        // A sandal with little skin: its own feet read on too few points to be shown as sure, so the rankings fill no
        // row — and its own feet are the only row it has. The Studio's best guess fills it, or Preview could never run.
        var layout = Shoe();
        var weak = new BodySizeMatch.Ranking(BodySizeMatch.Confidence.TooLittle, [new BodySizeMatch.Score(FeetM, 1f, 0f)]);
        var read = new KeepEnds.StudioReading(KeepEnds.Route.SkinOnly, weak, FeetM);

        Assert.Equal(("_sho", FeetM), RetargetInputs.StudioPrefill(layout, read, new Dictionary<string, BodyOption>()));
        // The leg or arm, for a piece that reaches it.
        Assert.Equal(("_dwn", LegsS), RetargetInputs.StudioPrefill(layout,
            new KeepEnds.StudioReading(KeepEnds.Route.Cloth, null, LegsS), new Dictionary<string, BodyOption>()));
        // Never over a choice made, nor with nothing read, nor with the ends not kept.
        Assert.Null(RetargetInputs.StudioPrefill(layout, read, new Dictionary<string, BodyOption> { ["_sho"] = FeetS }));
        Assert.Null(RetargetInputs.StudioPrefill(layout, new KeepEnds.StudioReading(KeepEnds.Route.NoSkin, null, null),
                                                 new Dictionary<string, BodyOption>()));
        Assert.Null(RetargetInputs.StudioPrefill(Shoe(keep: false), read, new Dictionary<string, BodyOption>()));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-27")]
    public void Manual_legs_pick_overrides_skin_only()   // [EDGE-keep-ends-studio-27]
    {
        var layout = Shoe();
        var slots = RetargetInputs.Slots(layout, CatalogSlots, Two);
        var from = new Dictionary<string, BodyOption> { ["_sho"] = FeetM, ["_dwn"] = LegsS };
        var to = new Dictionary<string, List<BodyOption>> { ["_dwn"] = [LegsM] };

        foreach (var route in new KeepEnds.Route?[] { KeepEnds.Route.SkinOnly, KeepEnds.Route.NoSkin })
        {
            var inputs = RetargetInputs.Assemble(layout, slots, from, to, route);
            Assert.False(inputs.SkinOnly);
            Assert.True(inputs.KeepEnds);
            Assert.True(RetargetInputs.Ready(inputs, from, true, false));
        }

        // "None" again: back to the redraw.
        from.Remove("_dwn");
        Assert.True(RetargetInputs.Assemble(layout, slots, from, to, KeepEnds.Route.SkinOnly).SkinOnly);
        Assert.False(RetargetInputs.Assemble(layout, slots, from, to, KeepEnds.Route.NoSkin).SkinOnly);
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-5")]
    public void Keep_ends_off_assembles_todays_inputs()   // [AC-keep-ends-studio-5]
    {
        // Today: the garment's own slot is required and planned as, and every other slot with both ends chosen rides
        // along in the catalog's order. No end held, no slot swapped.
        var layout = Shoe(keep: false);
        Assert.Equal(new RetargetInputs.Layout("_sho", "_sho", null), layout);
        var slots = RetargetInputs.Slots(layout, CatalogSlots, Two);
        Assert.Equal(["_sho", "_top", "_dwn", "_glv"], slots);

        var from = new Dictionary<string, BodyOption> { ["_sho"] = FeetS, ["_dwn"] = LegsS, ["_glv"] = HandsS };
        var to = new Dictionary<string, List<BodyOption>> { ["_sho"] = [FeetM], ["_dwn"] = [LegsM], ["_glv"] = [HandsM] };
        var inputs = RetargetInputs.Assemble(layout, slots, from, to, KeepEnds.Route.Skin);

        Assert.False(inputs.KeepEnds);
        Assert.False(inputs.SkinOnly);
        Assert.Equal("_sho", inputs.GarmentSlot);
        Assert.Equal("_sho", inputs.Primary);
        Assert.Null(inputs.EndSlot);
        Assert.Equal([FeetM], inputs.PrimaryTargets);
        Assert.Equal([("_dwn", LegsS, LegsM), ("_glv", HandsS, HandsM)], inputs.Shared);
        var chosen = RetargetInputs.Chosen(slots, from, to);
        Assert.Equal(["M", "M", "M"], RetargetInputs.ToLabels(inputs, chosen, FeetM, to));
        Assert.Equal(["S", "S", "S"], RetargetInputs.FromLabels(inputs, chosen, from));
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-5")]
    public void Keep_ends_off_key_is_todays_key()   // [AC-keep-ends-studio-5]
    {
        var layout = Shoe(keep: false);
        var slots = RetargetInputs.Slots(layout, CatalogSlots, Two);
        var from = new Dictionary<string, BodyOption> { ["_sho"] = FeetS, ["_dwn"] = LegsS };
        var to = new Dictionary<string, List<BodyOption>> { ["_sho"] = [FeetM], ["_dwn"] = [LegsM] };
        var inputs = RetargetInputs.Assemble(layout, slots, from, to, null);
        var chosen = RetargetInputs.Chosen(slots, from, to);

        string key = RetargetInputs.Key("mod/shoe.mdl", "bibo", inputs, chosen, from, to, ["b", "a"], ["s"],
                                        replaceSkin: true, cutHidden: false, clearBody: true);

        Assert.Equal("mod/shoe.mdl|from:bibo|_sho:feet/s.mdl>feet/m.mdl|_dwn:legs/s.mdl>legs/m.mdl|held:a,b|shape:s|lay|whole|clear",
                     key);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-18")]
    public void Key_changes_with_keep_ends_skin_only_and_shapes()   // [EDGE-keep-ends-studio-18]
    {
        var from = new Dictionary<string, BodyOption> { ["_sho"] = FeetM, ["_dwn"] = LegsS };
        var to = new Dictionary<string, List<BodyOption>> { ["_dwn"] = [LegsM], ["_sho"] = [FeetM] };
        string Key(RetargetInputs.Layout layout, KeepEnds.Route? route, IEnumerable<string> shapes,
                   Dictionary<string, BodyOption>? f = null)
        {
            f ??= from;
            var slots = RetargetInputs.Slots(layout, CatalogSlots, Two);
            var inputs = RetargetInputs.Assemble(layout, slots, f, to, route);
            return RetargetInputs.Key("m", null, inputs, RetargetInputs.Chosen(slots, f, to), f, to, [], shapes,
                                      true, true, false);
        }

        string kept = Key(Shoe(), KeepEnds.Route.Skin, []);
        string whole = Key(Shoe(keep: false), KeepEnds.Route.Skin, []);
        string shaped = Key(Shoe(), KeepEnds.Route.Skin, ["1.1"]);
        string skinOnly = Key(Shoe(), KeepEnds.Route.SkinOnly, [], new() { ["_sho"] = FeetM });
        Assert.Equal(4, new[] { kept, whole, shaped, skinOnly }.Distinct().Count());
        Assert.Contains("|keep", kept);
        Assert.DoesNotContain("|keep", whole);
        Assert.Contains("|skin:", skinOnly);
    }

    [Fact]
    [Trait("Plan", "AC-refit-piercings-option-7")]
    [Trait("Plan", "EDGE-refit-piercings-option-7")]
    public void Piercings_take_effect_only_with_the_skin_replaced()
    {
        // [AC-refit-piercings-option-7] [EDGE-refit-piercings-option-7] The box greys out with "Use the new body's skin".
        Assert.False(RetargetInputs.CarryPiercings(replaceSkin: false, carryPiercings: true));
        Assert.True(RetargetInputs.CarryPiercings(replaceSkin: true, carryPiercings: true));
        Assert.False(RetargetInputs.CarryPiercings(replaceSkin: true, carryPiercings: false));
    }

    [Fact]
    [Trait("Plan", "AC-refit-piercings-option-7")]
    [Trait("Plan", "EDGE-refit-piercings-option-7")]
    [Trait("Plan", "EDGE-refit-piercings-option-12")]
    public void Key_changes_with_piercings()
    {
        // [AC-refit-piercings-option-7] Ticking the box takes a preview down; [EDGE-refit-piercings-option-7] without
        // the skin replaced, or on a skin-only redraw ([EDGE-refit-piercings-option-12]), it changes nothing and leaves the
        // preview up.
        var from = new Dictionary<string, BodyOption> { ["_sho"] = FeetM, ["_dwn"] = LegsS };
        var to = new Dictionary<string, List<BodyOption>> { ["_dwn"] = [LegsM], ["_sho"] = [FeetM] };
        string Key(KeepEnds.Route? route, bool replaceSkin, bool piercings, Dictionary<string, BodyOption>? f = null)
        {
            f ??= from;
            var slots = RetargetInputs.Slots(Shoe(), CatalogSlots, Two);
            var inputs = RetargetInputs.Assemble(Shoe(), slots, f, to, route);
            return RetargetInputs.Key("m", null, inputs, RetargetInputs.Chosen(slots, f, to), f, to, [], [],
                                      replaceSkin, true, false, piercings);
        }

        string off = Key(KeepEnds.Route.Skin, true, false);
        string on = Key(KeepEnds.Route.Skin, true, true);
        Assert.EndsWith("|pierce", on);
        Assert.NotEqual(off, on);
        Assert.DoesNotContain("|pierce", off);
        Assert.Equal(Key(KeepEnds.Route.Skin, false, false), Key(KeepEnds.Route.Skin, false, true));
        var feet = new Dictionary<string, BodyOption> { ["_sho"] = FeetM };
        Assert.Equal(Key(KeepEnds.Route.SkinOnly, true, false, feet), Key(KeepEnds.Route.SkinOnly, true, true, feet));
    }

    [Fact]
    [Trait("Plan", "AC-studio-game-top-3")]
    [Trait("Plan", "AC-studio-game-top-4")]
    [Trait("Plan", "EDGE-studio-game-top-1")]
    [Trait("Plan", "EDGE-studio-game-top-2")]
    [Trait("Plan", "EDGE-studio-game-top-3")]
    [Trait("Plan", "EDGE-studio-game-top-5")]
    [Trait("Plan", "EDGE-studio-game-top-6")]
    [Trait("Plan", "AC-coatee-strap-slit-5")]
    [Trait("Plan", "EDGE-coatee-strap-slit-12")]
    [Trait("Plan", "AC-relax-sink-guard-2")]
    [Trait("Plan", "AC-relax-sink-guard-3")]
    [Trait("Plan", "EDGE-relax-sink-guard-14")]
    public void Studio_skin_rule_for_game_and_modders_tops()
    {
        // [AC-studio-game-top-4] The game's own top with the new body's skin, as the Body size tool opens: the automatic
        // refit's rule for game gear, the Clear box forced on — the tuck's open skin where the inside shows included, and
        // [AC-relax-sink-guard-2] the relax guard.
        var game = AutoRefitDecisions.SkinFor(true);
        var top = RetargetInputs.StudioSkin(replaceSkin: true, cutHidden: true, clearBody: false, gameGear: true, "_top", crossSex: false);
        Assert.Equal(new RetargetInputs.StudioSkinRule(ClearForced: true, ClearBody: game.ClearBody,
                                                       TuckSlot: RefitCore.TuckSlot(game.TuckUnderCloth, "_top"),
                                                       DeepClear: RefitCore.DeepClearFor(game.DeepClear, "_top"),
                                                       KeepStandoff: RefitCore.KeepStandoffFor(game.KeepStandoff, "_top"),
                                                       SeeInside: RefitCore.SeeInsideFor(game.SeeInside, "_top"),
                                                       RelaxSinkGuard: game.RelaxSinkGuard), top);
        Assert.True(top.SeeInside);
        Assert.True(top.RelaxSinkGuard);

        // [EDGE-studio-game-top-2] Cut off: still cleared, deep cleared and pulled back, but nothing is tucked — and so
        // nothing drawn where the inside shows.
        Assert.Equal(new RetargetInputs.StudioSkinRule(true, true, null, true, true, false, true),
                     RetargetInputs.StudioSkin(true, cutHidden: false, clearBody: false, gameGear: true, "_top", false));

        // [AC-studio-game-top-3] A modder's garment, and [EDGE-studio-game-top-5] a "— Proteus edit" copy (a mod), get the
        // boxes as they always did: the tuck only with cut and clear both ticked, never the deep clear or the standoff —
        // [AC-coatee-strap-slit-5] and never the open skin where the inside shows, tuck or not, nor [AC-relax-sink-guard-3]
        // the relax guard.
        foreach (bool cut in new[] { true, false })
            foreach (bool clear in new[] { true, false })
                foreach (string slot in new[] { "_top", "_dwn", "_glv", "_sho" })
                    Assert.Equal(new RetargetInputs.StudioSkinRule(false, clear, RefitCore.TuckSlot(cut && clear, slot), false, false, false, false),
                                 RetargetInputs.StudioSkin(true, cut, clear, gameGear: false, slot, false));

        // [EDGE-studio-game-top-1] New skin off, [EDGE-studio-game-top-3] the game's legs, shoes or gloves, and
        // [EDGE-studio-game-top-6] a race of the other sex: the boxes as they are — [EDGE-relax-sink-guard-14] the relax
        // guard only on the game's legs with the new skin, as the automatic refit has it (in effect with the box ticked).
        Assert.Equal(new RetargetInputs.StudioSkinRule(false, false, null, false, false, false, false),
                     RetargetInputs.StudioSkin(replaceSkin: false, true, false, gameGear: true, "_top", false));
        Assert.Equal(new RetargetInputs.StudioSkinRule(false, false, null, false, false, false, true),
                     RetargetInputs.StudioSkin(true, true, false, gameGear: true, "_dwn", false));
        Assert.Equal(new RetargetInputs.StudioSkinRule(false, true, null, false, false, false, true),
                     RetargetInputs.StudioSkin(true, true, true, gameGear: true, "_dwn", false));
        Assert.False(RetargetInputs.StudioSkin(false, true, true, gameGear: true, "_dwn", false).RelaxSinkGuard);
        Assert.False(RetargetInputs.StudioSkin(true, true, true, gameGear: true, "_dwn", crossSex: true).RelaxSinkGuard);
        foreach (string slot in new[] { "_glv", "_sho" })
        {
            Assert.Equal(new RetargetInputs.StudioSkinRule(false, false, null, false, false, false, false),
                         RetargetInputs.StudioSkin(true, true, false, gameGear: true, slot, false));
            Assert.False(RetargetInputs.StudioSkin(true, true, true, gameGear: true, slot, false).RelaxSinkGuard);
        }
        Assert.Equal(new RetargetInputs.StudioSkinRule(false, false, null, false, false, false, false),
                     RetargetInputs.StudioSkin(true, true, false, gameGear: true, "_top", crossSex: true));
        // A top bent across the sexes with both boxes ticked tucks, as the boxes say, but never draws where the inside shows.
        var crossed = RetargetInputs.StudioSkin(true, true, true, gameGear: true, "_top", crossSex: true);
        Assert.Equal("_top", crossed.TuckSlot);
        Assert.False(crossed.SeeInside);
        Assert.False(crossed.RelaxSinkGuard);
    }

    [Fact]
    [Trait("Plan", "AC-studio-game-top-4")]
    public void A_game_top_rule_changes_the_plan_key()
    {
        // [AC-studio-game-top-4] A plan made under the game-top rule is never shown or saved for a garment judged a mod,
        // nor the other way round.
        var layout = RetargetInputs.LayoutFor("_top", keepEnds: false, false, false, false);
        var slots = RetargetInputs.Slots(layout, ["_top"], _ => 2);
        var from = new Dictionary<string, BodyOption> { ["_top"] = LegsS };
        var to = new Dictionary<string, List<BodyOption>> { ["_top"] = [LegsM] };
        var inputs = RetargetInputs.Assemble(layout, slots, from, to, null);
        string Key(bool gameTop) => RetargetInputs.Key("m", null, inputs, RetargetInputs.Chosen(slots, from, to), from, to, [], [],
                                                       true, true, false, gameTop: gameTop);
        Assert.EndsWith("|gametop", Key(true));
        Assert.DoesNotContain("|gametop", Key(false));
    }

    [Fact]
    [Trait("Plan", "AC-refit-piercings-option-8")]
    public void Piercings_flag_turns_the_cli_carry_on()
    {
        // [AC-refit-piercings-option-8] The headless refit carries none unless given --piercings.
        Assert.False(RetargetInputs.CliCarryPiercings(new Dictionary<string, string> { ["whole"] = "" }));
        Assert.True(RetargetInputs.CliCarryPiercings(new Dictionary<string, string> { ["piercings"] = "" }));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-19")]
    public void Flip_resets_presets_and_keeps_end_choices_aside()   // [EDGE-keep-ends-studio-19]
    {
        // The first layout of a garment is no change; ticking the box off, or the rig turning out unreadable, is.
        Assert.False(RetargetInputs.Flips(null, Shoe()));
        Assert.False(RetargetInputs.Flips(Shoe(), Shoe()));
        Assert.True(RetargetInputs.Flips(Shoe(), Shoe(keep: false)));
        Assert.True(RetargetInputs.Flips(Shoe(), Shoe(unreadable: true)));

        // The feet chosen for the old rows are kept, only not offered, and come back with them.
        var from = new Dictionary<string, BodyOption> { ["_sho"] = FeetS };
        var to = new Dictionary<string, List<BodyOption>> { ["_sho"] = [FeetM] };
        var kept = RetargetInputs.Assemble(Shoe(), RetargetInputs.Slots(Shoe(), CatalogSlots, Two), from, to, null);
        Assert.Empty(kept.Shared);
        Assert.Empty(kept.PrimaryTargets);
        var back = RetargetInputs.Assemble(Shoe(keep: false), RetargetInputs.Slots(Shoe(keep: false), CatalogSlots, Two),
                                           from, to, null);
        Assert.Equal([FeetM], back.PrimaryTargets);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-1")]
    public void Unreadable_rig_flips_to_todays_layout()   // [EDGE-keep-ends-studio-1]
        => Assert.Equal(Shoe(keep: false), Shoe(unreadable: true));

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-6")]
    public void No_limb_in_a_body_keeps_todays_layout()   // [EDGE-keep-ends-studio-6]
    {
        var today = new RetargetInputs.Layout("_sho", "_sho", null);
        Assert.Equal(today, RetargetInputs.LayoutFor("_sho", true, sourceHasLimb: false, targetHasLimb: true, false));
        Assert.Equal(today, RetargetInputs.LayoutFor("_sho", true, sourceHasLimb: true, targetHasLimb: false, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-19")]
    public void Studio_slots_never_offer_the_end_slot()   // [EDGE-keep-ends-studio-19]
    {
        Assert.DoesNotContain("_sho", RetargetInputs.Slots(Shoe(), CatalogSlots, Two));
        Assert.DoesNotContain("_glv", RetargetInputs.Slots(RetargetInputs.LayoutFor("_glv", true, true, true, false),
                                                           CatalogSlots, Two));
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-4")]
    [Trait("Plan", "EDGE-keep-ends-studio-11")]
    public void Unbaked_matching_skin_is_nothing_to_change_and_a_bake_always_saves()   // [AC-keep-ends-studio-4] [EDGE-keep-ends-studio-11]
    {
        byte[] model = [1, 2, 3];
        Assert.False(RetargetInputs.SkinOnlyChanges(baking: false, model, model));
        Assert.True(RetargetInputs.SkinOnlyChanges(baking: true, model, model));
        Assert.True(RetargetInputs.SkinOnlyChanges(baking: false, model, [1, 2, 3]));
    }

    // ── the garment's own part, whatever the body mod offers (studio-slot-body) ─────────────────────────────────────

    private static RetargetInputs.Layout LayoutOf(string? own, string[] offered, bool keep = true)
    {
        string worn = RetargetInputs.GarmentSlot(own, offered);
        string? limb = AutoRefitDecisions.LimbOf(worn);
        bool hasLimb = limb != null && offered.Contains(limb);
        return RetargetInputs.LayoutFor(worn, keep, hasLimb, hasLimb, endsUnreadable: false);
    }

    [Fact]
    [Trait("Plan", "AC-studio-slot-body-1")]
    public void Leg_piece_on_chest_only_body_is_missing_legs()   // [AC-studio-slot-body-1]
    {
        string[] chestOnly = ["_top"];
        Assert.Equal("_dwn", RetargetInputs.GarmentSlot("_dwn", chestOnly));
        var layout = LayoutOf("_dwn", chestOnly);
        Assert.Equal("_dwn", layout.Primary);
        Assert.Equal("_dwn", RetargetInputs.MissingSlot(chestOnly, layout));
    }

    [Fact]
    [Trait("Plan", "AC-studio-slot-body-2")]
    public void Own_part_is_never_replaced_by_another()   // [AC-studio-slot-body-2]
    {
        string[][] offers = [[], ["_top"], ["_dwn"], ["_top", "_dwn"], ["_glv", "_sho"], CatalogSlots];
        foreach (string own in CatalogSlots)
            foreach (var offered in offers)
                Assert.Equal(own, RetargetInputs.GarmentSlot(own, offered));
    }

    [Fact]
    [Trait("Plan", "AC-studio-slot-body-6")]
    public void Made_for_body_without_the_part_is_reported()   // [AC-studio-slot-body-6]
    {
        var legs = RetargetInputs.LayoutFor("_dwn", true, false, false, false);
        Assert.Equal("_dwn", RetargetInputs.MissingSlot(["_top"], legs));
        Assert.Null(RetargetInputs.MissingSlot(["_top", "_dwn"], legs));
    }

    [Fact]
    [Trait("Plan", "AC-studio-slot-body-7")]
    [Trait("Plan", "EDGE-studio-slot-body-8")]
    [Trait("Plan", "EDGE-studio-slot-body-10")]
    public void Shoe_and_glove_on_bodies_without_ends_get_the_limb_layout()   // [AC-studio-slot-body-7] [EDGE-studio-slot-body-8] [EDGE-studio-slot-body-10]
    {
        // Before, both were laid out as tops: a body without feet or hands put its first part, the chest, in their place.
        string[] chestAndLegs = ["_top", "_dwn"];
        Assert.Equal("_sho", RetargetInputs.GarmentSlot("_sho", chestAndLegs));
        var shoe = LayoutOf("_sho", chestAndLegs);
        Assert.Equal(new RetargetInputs.Layout("_dwn", "_sho", "_sho"), shoe);
        Assert.Null(RetargetInputs.MissingSlot(chestAndLegs, shoe));

        string[] chestOnly = ["_top"];
        Assert.Equal("_glv", RetargetInputs.GarmentSlot("_glv", chestOnly));
        var glove = LayoutOf("_glv", chestOnly);
        Assert.Equal(new RetargetInputs.Layout("_top", "_glv", "_glv"), glove);
        Assert.Null(RetargetInputs.MissingSlot(chestOnly, glove));
    }

    [Fact]
    [Trait("Plan", "AC-studio-slot-body-8")]
    [Trait("Plan", "EDGE-studio-slot-body-1")]
    public void Bodies_with_the_own_part_and_accessories_lay_out_as_before()   // [AC-studio-slot-body-8] [EDGE-studio-slot-body-1]
    {
        string[] chestAndLegs = ["_top", "_dwn"];
        Assert.Equal("_top", RetargetInputs.GarmentSlot("_top", chestAndLegs));
        Assert.Equal("_dwn", RetargetInputs.GarmentSlot("_dwn", chestAndLegs));
        Assert.Null(RetargetInputs.MissingSlot(chestAndLegs, LayoutOf("_top", chestAndLegs)));
        Assert.Null(RetargetInputs.MissingSlot(chestAndLegs, LayoutOf("_dwn", chestAndLegs)));

        // An accessory is no body part: it takes the body mod's first, as it always has, and is never short of one.
        Assert.Equal("_top", RetargetInputs.GarmentSlot(null, chestAndLegs));
        Assert.Equal("_dwn", RetargetInputs.GarmentSlot(null, ["_dwn"]));
        Assert.Equal("_top", RetargetInputs.GarmentSlot(null, []));
        Assert.Null(RetargetInputs.MissingSlot(chestAndLegs, LayoutOf(null, chestAndLegs)));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-9")]
    public void Shoe_unticked_on_body_without_feet_reports_feet()   // [EDGE-studio-slot-body-9]
    {
        string[] chestAndLegs = ["_top", "_dwn"];
        var shoe = LayoutOf("_sho", chestAndLegs, keep: false);
        Assert.Equal("_sho", shoe.Primary);
        Assert.Equal("_sho", RetargetInputs.MissingSlot(chestAndLegs, shoe));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-17")]
    public void Unticking_keep_feet_flips_into_a_missing_part()   // [EDGE-studio-slot-body-17]
    {
        // The panel takes a shown preview down on every flip, before it decides whether the rows can be drawn at all.
        string[] chestAndLegs = ["_top", "_dwn"];
        var kept = LayoutOf("_sho", chestAndLegs);
        var unticked = LayoutOf("_sho", chestAndLegs, keep: false);
        Assert.True(RetargetInputs.Flips(kept, unticked));
        Assert.Null(RetargetInputs.MissingSlot(chestAndLegs, kept));
        Assert.Equal("_sho", RetargetInputs.MissingSlot(chestAndLegs, unticked));
    }

    // ── a part on a body of its own (per-part-target-body) ───────────────────

    private static string KeyWith(IReadOnlyDictionary<string, string>? bodies, BodyOption legsTo)
    {
        var layout = new RetargetInputs.Layout("_top", "_top", null);
        var chest = new BodyOption("Chest", "", "M", "chest/m.mdl", "chara/equipment/e0000/model/c0201e0000_top.mdl", "_top");
        var from = new Dictionary<string, BodyOption> { ["_top"] = chest, ["_dwn"] = LegsS };
        var to = new Dictionary<string, List<BodyOption>> { ["_top"] = [chest], ["_dwn"] = [legsTo] };
        var inputs = RetargetInputs.Assemble(layout, ["_top", "_dwn"], from, to, null);
        return RetargetInputs.Key("m", null, inputs, RetargetInputs.Chosen(["_top", "_dwn"], from, to), from, to, [], null,
                                  true, true, false, partBodies: bodies);
    }

    [Fact]
    [Trait("Plan", "AC-per-part-target-body-4")]
    [Trait("Plan", "EDGE-per-part-target-body-10")]
    [Trait("Plan", "EDGE-per-part-target-body-19")]
    public void Key_unchanged_without_part_bodies_and_changes_with_them()
    {
        string today = KeyWith(null, LegsM);
        Assert.Equal(today, KeyWith(new Dictionary<string, string>(), LegsM));
        Assert.DoesNotContain("bodies", today);
        string yab = KeyWith(new Dictionary<string, string> { ["_dwn"] = "YAB+" }, LegsM);
        Assert.NotEqual(today, yab);
        Assert.NotEqual(yab, KeyWith(new Dictionary<string, string> { ["_dwn"] = "Bibo+" }, LegsM));
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-4")]
    public void Key_tells_two_bodies_with_the_same_files_apart()
    {
        // Rue and its fork LaRue lay their files out alike: the same rel, another body.
        Assert.NotEqual(KeyWith(new Dictionary<string, string> { ["_dwn"] = "Rue" }, LegsM),
                        KeyWith(new Dictionary<string, string> { ["_dwn"] = "LaRue" }, LegsM));
    }

    [Fact]
    [Trait("Plan", "EDGE-per-part-target-body-20")]
    public void Slots_list_only_main_body_parts_with_several_models()
    {
        // The rows a part body can be picked on are the main body's parts with more than one model, as always.
        var layout = new RetargetInputs.Layout("_top", "_top", null);
        var slots = RetargetInputs.Slots(layout, CatalogSlots, s => s == "_dwn" ? 1 : 2);
        Assert.Equal(["_top", "_glv", "_sho"], slots);
    }
}
