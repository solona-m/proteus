using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Localization;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which way a garment goes before it is refitted: baked up into the wearer's race, or — racial gear, Au Ra's Auri set —
/// bent down into the shared shape the bodies are made for, and saved back where it is drawn.
/// </summary>
public class RacialRouteTests
{
    private const string Auri = "chara/equipment/e0258/model/c1401e0258_dwn.mdl";
    private const string ManBoots = "chara/equipment/e6023/model/c0101e6023_sho.mdl";

    private static bool None(ushort _) => false;

    private static Func<ushort, bool> Only(params int[] codes) => code => codes.Contains(code);

    private static RacialModelBake.Route Route(string path, int wearer, Func<ushort, bool> target,
                                               Func<ushort, bool> both)
        => RefitCore.Route(path, (ushort)wearer, target, both, [], null);

    [Theory]
    [InlineData(1401, 201)]   // Au Ra woman: Midlander woman
    [InlineData(1301, 101)]   // Au Ra man: Midlander man
    [InlineData(1801, 201)]   // Viera woman
    [InlineData(1601, 201)]   // Hrothgar woman
    [InlineData(1501, 101)]   // Hrothgar man: through Roegadyn men to the root when neither has bodies
    [InlineData(1201, 0)]     // Lalafell woman falls to Lalafell men: never refitted between men's bodies
    [InlineData(201, 0)]      // already the shared shape
    [InlineData(101, 0)]
    [Trait("Plan", "AC-racial-gear-upscale-1")]
    [Trait("Plan", "EDGE-racial-gear-upscale-1")]
    [Trait("Plan", "EDGE-racial-gear-upscale-2")]
    [Trait("Plan", "EDGE-racial-gear-upscale-3")]
    public void The_shared_shape_follows_the_same_sex_fall_through(int garment, int expected)
        => Assert.Equal((ushort)expected, RacialModelBake.SharedShape((ushort)garment, None));

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-2")]
    public void A_Hrothgar_man_stops_at_Roegadyn_men_when_there_are_bodies_of_them()
        => Assert.Equal((ushort)901, RacialModelBake.SharedShape(1501, Only(901, 101)));

    [Fact]
    [Trait("Plan", "AC-racial-gear-upscale-1")]
    public void Racial_gear_on_its_own_race_goes_down_when_neither_side_has_its_bodies()
    {
        var route = Route(Auri, 1401, Only(201), Only(201));
        Assert.True(route.Down);
        Assert.Equal((ushort)1401, route.Drawn);
        Assert.Equal((ushort)201, route.To);
        Assert.Equal("0201", route.Race(Auri));
    }

    [Fact]
    [Trait("Plan", "AC-racial-gear-upscale-4")]
    [Trait("Plan", "AC-racial-gear-upscale-8")]
    public void A_down_route_saves_at_the_drawn_path_without_EQDP()
    {
        var down = Route(Auri, 1401, Only(201), Only(201));
        Assert.Equal(Auri, down.SavePath(Auri));
        Assert.Null(down.Eqdp(down.SavePath(Auri)));

        // A man's boots baked up for a woman: saved under her race, with the entry that makes the game load it.
        var up = Route(ManBoots, 201, Only(201), Only(201));
        Assert.False(up.Down);
        Assert.Equal((ushort)201, up.To);
        Assert.Equal("chara/equipment/e6023/model/c0201e6023_sho.mdl", up.SavePath(ManBoots));
        Assert.NotNull(up.Eqdp(up.SavePath(ManBoots)));
    }

    [Theory]
    [InlineData(101, 201)]
    [InlineData(101, 401)]
    [InlineData(101, 1801)]
    [InlineData(101, 1201)]
    [InlineData(101, 101)]
    [InlineData(101, 901)]
    [InlineData(201, 401)]
    [InlineData(301, 201)]
    [InlineData(101, 0)]
    [Trait("Plan", "AC-racial-gear-upscale-8")]
    public void Gear_with_a_midlander_model_and_men_s_gear_route_as_before(int garment, int wearer)
    {
        string path = $"chara/equipment/e6023/model/c{garment:D4}e6023_sho.mdl";
        var route = Route(path, wearer, None, None);
        Assert.Equal(RefitCore.BakeTarget(path, (ushort)wearer, None, [], null), route.To);
        Assert.False(route.Down);
    }

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-4")]
    public void Both_sides_with_the_garment_s_race_route_nowhere()
        => Assert.False(Route(Auri, 1401, Only(1401, 201), Only(1401, 201)).Any);

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-5")]
    public void Only_the_made_for_side_lacking_the_race_still_goes_down()
    {
        var route = Route(Auri, 1401, Only(1401, 201), Only(201));
        Assert.True(route.Down);
        Assert.Equal((ushort)201, route.To);
    }

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-6")]
    public void Nothing_known_goes_to_the_root_of_her_sex()
    {
        var route = Route(Auri, 1401, None, None);
        Assert.True(route.Down);
        Assert.Equal((ushort)201, route.To);
    }

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-7")]
    public void No_wearer_routes_nowhere()
        => Assert.False(Route(Auri, 0, Only(201), Only(201)).Any);

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-8")]
    public void A_racial_model_on_another_race_routes_nowhere()
        => Assert.False(Route(Auri, 201, Only(201), Only(201)).Any);

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-11")]
    public void Changing_the_made_for_side_changes_the_route()
    {
        Assert.True(Route(Auri, 1401, Only(1401, 201), Only(201)).Down);
        Assert.False(Route(Auri, 1401, Only(1401, 201), Only(1401, 201)).Any);
    }

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-15")]
    public void An_author_c0201_model_does_not_stop_the_route()
    {
        // The author also ships a Midlander woman's model of the set: she still draws the Au Ra one, so that is routed.
        var redirects = new List<PenumbraModMeta.Redirect>
        {
            new(Auri, "dwn/au ra.mdl", "Default"),
            new("chara/equipment/e0258/model/c0201e0258_dwn.mdl", "dwn/midlander.mdl", "Default"),
        };
        var route = RefitCore.Route(Auri, 1401, Only(201), Only(201), redirects, null);
        Assert.True(route.Down);
        Assert.Equal(Auri, route.SavePath(Auri));
    }

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-9")]
    public void A_down_route_is_not_itself_a_change()
    {
        var down = Route(Auri, 1401, Only(201), Only(201));
        var up = Route(ManBoots, 201, Only(201), Only(201));
        Assert.False(down.ChangesModel);
        Assert.True(up.ChangesModel);

        byte[] model = [1, 2, 3];
        Assert.False(RetargetInputs.SkinOnlyChanges(down.ChangesModel, model, model));
        Assert.True(RetargetInputs.SkinOnlyChanges(up.ChangesModel, model, model));
    }

    [Fact]
    [Trait("Plan", "EDGE-racial-gear-upscale-14")]
    public void Author_sizes_and_siblings_are_not_read_for_a_routed_garment()
    {
        const string root = "mod";
        var down = Route(Auri, 1401, Only(201), Only(201));
        var up = Route(ManBoots, 201, Only(201), Only(201));
        Assert.False(AutoRefitDecisions.AuthorSizesApply(root, down));
        Assert.False(AutoRefitDecisions.AuthorSizesApply(root, up));
        Assert.True(AutoRefitDecisions.AuthorSizesApply(root, RacialModelBake.Route.None));
        Assert.False(SiblingPiece.Applies(root, down.To));
        Assert.False(SiblingPiece.Applies(root, up.To));
    }

    [Fact]
    [Trait("Plan", "AC-racial-gear-upscale-2")]
    public void The_shaped_down_note_names_both_races()
    {
        string note = string.Format(Strings.Parts.RetargetShapedDownFmt, ModelRace.Describe("1401"),
                                    ModelRace.Describe("0201"));
        Assert.Contains(ModelRace.Describe("1401"), note);
        Assert.Contains(ModelRace.Describe("0201"), note);
        Assert.NotEqual(ModelRace.Describe("1401"), ModelRace.Describe("0201"));
    }
}
