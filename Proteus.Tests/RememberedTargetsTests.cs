using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Filling in the sizes the Body size tool was last used with, for every body part.
/// </summary>
public class RememberedTargetsTests
{
    private static readonly Dictionary<string, string> Last = new(StringComparer.Ordinal)
    {
        ["_top"] = "chest/SFW L.mdl",
        ["_dwn"] = "legs/Gen C Large.mdl",
        ["_glv"] = "hands/Short nails.mdl",
    };

    private static List<(string Slot, string Rel)> Fill(
        string? bodyDir = "Neolithe", string? rememberedFor = "Neolithe",
        Func<string, bool>? isDrawn = null, Func<string, bool>? hasTarget = null)
        => RememberedTargets.Fillable(Last, bodyDir, rememberedFor,
                                      isDrawn ?? (_ => true), hasTarget ?? (_ => false)).ToList();

    [Fact]
    public void Every_remembered_size_is_filled_in()
    {
        // The point of the feature: one choice, remembered, applied to all of them rather than a dropdown each.
        //
        // Every part, with no regard for whether it has a "made for" size to pair with — that is not this method's
        // business. A part joins the refit only once both ends are chosen, so an unpaired size sits out, and filling
        // it means that when the detector does find a source there, a frame or a garment later, the size is waiting.
        // What has to hold for that to be safe is downstream: BodyRetargetPanel.Chosen, which every consumer of a
        // pair goes through, and which the Refit button no longer blocks on.
        Assert.Equal(["_top", "_dwn", "_glv"], Fill().Select(f => f.Slot));
        Assert.Equal("legs/Gen C Large.mdl", Fill().Single(f => f.Slot == "_dwn").Rel);
    }

    [Fact]
    public void A_part_the_panel_does_not_draw_is_left_alone()
    {
        // The panel leaves a part out when this body mod has a single model for it, or none for the garment's race.
        // There is no dropdown there to show a filled-in size in and none to untick it from, so a size put there is
        // state the user can neither see nor reach — and it would outlive the garment that caused it.
        var filled = Fill(isDrawn: slot => slot != "_glv");

        Assert.Equal(["_top", "_dwn"], filled.Select(f => f.Slot));
    }

    [Fact]
    public void A_choice_already_made_is_never_overwritten()
    {
        // Including one made seconds ago for this garment: this runs every frame, and a remembered size that kept
        // replacing the user's would make the dropdown impossible to use.
        var filled = Fill(hasTarget: slot => slot == "_dwn");

        Assert.Equal(["_top", "_glv"], filled.Select(f => f.Slot));
    }

    [Fact]
    public void Sizes_of_another_body_mod_are_not_offered()
    {
        // They are remembered by the option's file relative to its own mod. Against a different mod they are at best
        // not found, and at worst — two mods laying their files out alike — a size of this one nobody chose.
        Assert.Empty(Fill(bodyDir: "Rue+", rememberedFor: "Neolithe"));
    }

    [Fact]
    public void Nothing_is_filled_before_a_body_is_chosen()
    {
        Assert.Empty(Fill(bodyDir: null));
    }
}
