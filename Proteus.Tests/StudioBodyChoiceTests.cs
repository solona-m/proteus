using System;
using System.Collections.Generic;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The body mod the Studio's Body size tool offers in one click when the chosen one has no sizes for the garment's part
/// (<see cref="StudioBodyChoice"/>): the one the character wears for that part, never a guess.
/// </summary>
public class StudioBodyChoiceTests
{
    /// <summary>A chest-only body mod <c>A</c> being refitted onto, and a legs mod <c>B</c> worn for the legs.</summary>
    private static readonly Dictionary<string, string[]> Offers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = ["_top"],
        ["B"] = ["_dwn", "_sho"],
        ["C"] = ["_top"],
        ["LegsOnly"] = ["_dwn"],
    };

    private static IReadOnlyCollection<string>? Offered(string dir) => Offers.TryGetValue(dir, out var o) ? o : null;

    private static StudioBodyChoice.Candidate Candidate(string? own, string? current, Func<string, string?> worn,
                                                        bool bodiesKnown = true,
                                                        Func<string, IReadOnlyCollection<string>?>? offered = null,
                                                        bool limbUsable = true)
        => StudioBodyChoice.WornCandidate(own, current, bodiesKnown, worn, offered ?? Offered, _ => limbUsable);

    [Fact]
    [Trait("Plan", "AC-studio-slot-body-3")]
    public void Worn_legs_body_is_offered_for_a_leg_piece()   // [AC-studio-slot-body-3]
    {
        var c = Candidate("_dwn", "A", slot => slot == "_dwn" ? "B" : "A");
        Assert.Equal(new StudioBodyChoice.Candidate(true, "B"), c);

        // Once picked, the leg piece has its own part's rows.
        string[] b = Offers["B"];
        var layout = RetargetInputs.LayoutFor(RetargetInputs.GarmentSlot("_dwn", b), true, false, false, false);
        Assert.Equal("_dwn", layout.Primary);
        Assert.Null(RetargetInputs.MissingSlot(b, layout));
    }

    [Fact]
    [Trait("Plan", "AC-studio-slot-body-4")]
    public void No_qualifying_worn_body_offers_no_button()   // [AC-studio-slot-body-4]
    {
        Assert.Equal(new StudioBodyChoice.Candidate(true, null), Candidate("_dwn", "A", _ => "C"));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-1")]
    public void Accessory_has_no_candidate()   // [EDGE-studio-slot-body-1]
    {
        Assert.Equal(new StudioBodyChoice.Candidate(true, null), Candidate(null, "A", _ => "B"));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-2")]
    public void Worn_body_equal_to_current_is_not_offered()   // [EDGE-studio-slot-body-2]
    {
        // Folder names compare as Penumbra's do, without case.
        Assert.Null(Candidate("_dwn", "b", _ => "B").Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-3")]
    public void Worn_body_with_only_other_sex_legs_is_not_offered()   // [EDGE-studio-slot-body-3]
    {
        // Filtered to this garment's race, a men's legs mod offers a women's leg piece nothing.
        Assert.Null(Candidate("_dwn", "A", _ => "B", offered: dir => dir == "B" ? [] : Offered(dir)).Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-4")]
    public void Unresolved_worn_body_is_not_offered()   // [EDGE-studio-slot-body-4]
    {
        Assert.Equal(new StudioBodyChoice.Candidate(true, null), Candidate("_dwn", "A", _ => null));
        // A mod that cannot be read is no candidate either.
        Assert.Null(Candidate("_dwn", "A", _ => "Gone").Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-5")]
    public void Before_the_scan_the_candidate_is_unknown()   // [EDGE-studio-slot-body-5]
    {
        var c = Candidate("_dwn", "A", _ => "B", bodiesKnown: false);
        Assert.False(c.Known);
        Assert.Null(c.Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-6")]
    public void Key_changes_with_race()   // [EDGE-studio-slot-body-6]
    {
        Assert.NotEqual(StudioBodyChoice.Key("m", "0101", "A", null, true), StudioBodyChoice.Key("m", "0201", "A", null, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-7")]
    [Trait("Plan", "EDGE-studio-slot-body-12")]
    public void Key_changes_with_body_and_candidate_goes_after_its_pick()   // [EDGE-studio-slot-body-7] [EDGE-studio-slot-body-12]
    {
        Assert.NotEqual(StudioBodyChoice.Key("m", "0201", "A", null, true), StudioBodyChoice.Key("m", "0201", "B", null, true));
        Assert.Null(Candidate("_dwn", "B", _ => "B").Dir);
        // Picking a body without the part again offers the worn one again: nothing is remembered between questions.
        Assert.Equal("B", Candidate("_dwn", "C", _ => "B").Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-8")]
    public void Shoe_is_sized_by_legs_when_ends_are_kept()   // [EDGE-studio-slot-body-8]
    {
        Assert.True(StudioBodyChoice.Sizes(["_top", "_dwn"], "_sho", limbUsable: true));
        Assert.False(StudioBodyChoice.Sizes(["_top"], "_sho", limbUsable: true));
        Assert.False(StudioBodyChoice.Sizes(["_top"], "_dwn", limbUsable: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-10")]
    public void Glove_is_sized_by_chest_when_ends_are_kept()   // [EDGE-studio-slot-body-10]
    {
        Assert.True(StudioBodyChoice.Sizes(["_top"], "_glv", limbUsable: true));
        Assert.False(StudioBodyChoice.Sizes(["_dwn"], "_glv", limbUsable: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-11")]
    public void Made_for_equal_to_pick_is_cleared()   // [EDGE-studio-slot-body-11]
    {
        Assert.Null(StudioBodyChoice.SourceAfterPick("B", "b"));
        Assert.Equal("C", StudioBodyChoice.SourceAfterPick("C", "B"));
        Assert.Equal(VanillaBodyCatalog.Key, StudioBodyChoice.SourceAfterPick(VanillaBodyCatalog.Key, "B"));
        Assert.Null(StudioBodyChoice.SourceAfterPick(null, "B"));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-13")]
    public void Mod_owning_matches_whole_folders()   // [EDGE-studio-slot-body-13]
    {
        string root = Path.Combine(Path.GetTempPath(), "mods");
        string[] dirs = ["Bibo", "Bibo+"];
        string file = Path.GetFullPath(Path.Combine(root, "Bibo+", "chara", "c0201e0000_dwn.mdl"));
        Assert.Equal("Bibo+", StudioBodyChoice.ModOwning(file, root, dirs));
        Assert.Equal("Bibo+", StudioBodyChoice.ModOwning(file.ToUpperInvariant(), root, dirs));
        Assert.Null(StudioBodyChoice.ModOwning(Path.GetFullPath(Path.Combine(root, "Other", "x.mdl")), root, dirs));
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-14")]
    public void Explicit_race_pick_counts_only_that_races_legs()   // [EDGE-studio-slot-body-14]
    {
        var catalog = new BodySizeCatalog("B",
        [
            new BodyOption("Legs", "", "S", "legs/s.mdl", "chara/equipment/e0000/model/c0101e0000_dwn.mdl", "_dwn"),
            new BodyOption("Legs", "", "M", "legs/m.mdl", "chara/equipment/e0000/model/c0101e0000_dwn.mdl", "_dwn"),
        ]);
        var picked = OtherRaceRefit.OnlyRace(catalog, 0301);
        var offers = new List<string>(picked.SlotsFor("0301"));
        Assert.DoesNotContain("_dwn", offers);
        Assert.Null(Candidate("_dwn", "A", _ => "B", offered: dir => dir == "B" ? offers : Offered(dir)).Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-16")]
    public void Shoe_on_chest_only_body_falls_back_to_worn_legs_body()   // [EDGE-studio-slot-body-16]
    {
        // The feet come from the game, the legs from a legs-only mod: that mod refits the shoe by its legs.
        Assert.Equal("LegsOnly", Candidate("_sho", "A", slot => slot == "_dwn" ? "LegsOnly" : null).Dir);
        // Likewise when the feet come from the body being refitted onto.
        Assert.Equal("LegsOnly", Candidate("_sho", "A", slot => slot == "_dwn" ? "LegsOnly" : "A").Dir);
    }

    [Fact]
    [Trait("Plan", "EDGE-studio-slot-body-18")]
    public void Limb_only_candidate_needs_kept_ends()   // [EDGE-studio-slot-body-18]
    {
        // Without kept ends the shoe is laid out as feet, and a legs-only mod would leave it short of sizes again.
        Assert.Equal(new StudioBodyChoice.Candidate(true, null),
                     Candidate("_sho", "A", slot => slot == "_dwn" ? "LegsOnly" : null, limbUsable: false));
        Assert.False(StudioBodyChoice.Sizes(["_top", "_dwn"], "_sho", limbUsable: false));

        // A mod with the shoe's own feet is offered either way.
        Assert.Equal("Feet", Candidate("_sho", "A", slot => slot == "_sho" ? "Feet" : null,
                                       offered: dir => dir == "Feet" ? ["_sho"] : Offered(dir), limbUsable: false).Dir);

        // Whether the ends are wanted, and the body refitted from, are part of the question.
        Assert.NotEqual(StudioBodyChoice.Key("m", "0201", "A", null, true), StudioBodyChoice.Key("m", "0201", "A", null, false));
        Assert.NotEqual(StudioBodyChoice.Key("m", "0201", "A", null, true), StudioBodyChoice.Key("m", "0201", "A", "S", true));
    }
}
