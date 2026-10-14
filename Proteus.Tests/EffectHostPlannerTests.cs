using System.Linq;
using Proteus.Services;
using Xunit;
using C = Proteus.Services.EffectHostPlanner.Candidate;
using R = Proteus.Services.EffectHostPlanner.Request;

namespace Proteus.Tests;

/// <summary>Which drawn host carries each imported item effect.</summary>
public class EffectHostPlannerTests
{
    private static C Carrier(int index, string slot = "rir") => new(index, slot, IsCarrier: true, Variant: 1, OwnVfxId: 0);
    private static C Worn(int index, string slot = "ril") => new(index, slot, IsCarrier: false, Variant: 3, OwnVfxId: 0);

    [Fact]
    public void Carriers_are_preferred_over_worn_items()   // [AC-item-vfx-host-2] [EDGE-item-vfx-host-15]
    {
        // The piece landed on a worn ring; a carrier is also drawn. The effect goes to the carrier, never the worn ring.
        var (assigned, dropped) = EffectHostPlanner.Assign([Worn(0), Carrier(1)], [new R(0, OwnHost: 0)]);
        Assert.Empty(dropped);
        Assert.Equal(new EffectHostPlanner.Assignment(0, 1, 1), Assert.Single(assigned));
    }

    [Fact]
    public void The_pieces_own_carrier_comes_first()   // [AC-item-vfx-host-2]
    {
        var (assigned, _) = EffectHostPlanner.Assign([Carrier(0, "rir"), Carrier(1, "wrs")], [new R(0, OwnHost: 1)]);
        Assert.Equal(new EffectHostPlanner.Assignment(0, 1, 3), Assert.Single(assigned));
    }

    [Fact]
    public void A_host_with_its_own_vfx_is_skipped()   // [AC-item-vfx-host-7] [EDGE-item-vfx-host-4]
    {
        var glowing = Carrier(0) with { OwnVfxId = 3 };
        var (assigned, _) = EffectHostPlanner.Assign([glowing, Carrier(1, "ril")], [new R(0, OwnHost: 0)]);
        Assert.Equal(1, Assert.Single(assigned).Host);

        var (none, dropped) = EffectHostPlanner.Assign([glowing], [new R(0, OwnHost: 0)]);
        Assert.Empty(none);
        Assert.Contains("effect of its own", Assert.Single(dropped).Reason);
    }

    [Fact]
    public void One_effect_per_host()   // [AC-item-vfx-host-7] [EDGE-item-vfx-host-5]
    {
        var (assigned, dropped) = EffectHostPlanner.Assign([Carrier(0)], [new R(0, 0), new R(1, 0)]);
        Assert.Equal(0, Assert.Single(assigned).Request);
        Assert.Equal(1, Assert.Single(dropped).Request);
    }

    [Fact]
    public void Two_carriers_of_one_set_get_two_effect_ids()   // [AC-item-vfx-host-2]
    {
        // Every Emperor's New piece is set a0053, and the effect folder is per set: one id each, or one file for both.
        var (assigned, _) = EffectHostPlanner.Assign([Carrier(0, "rir"), Carrier(1, "ril")], [new R(0, 0), new R(1, 1)]);
        Assert.Equal([1, 2], assigned.Select(a => a.VfxId).OrderBy(x => x).ToList());
        Assert.Equal("chara/accessory/a0053/vfx/eff/va0001.avfx", PenumbraManipulations.VfxGamePath('a', 53, 1));
        Assert.Equal("chara/accessory/a0053/vfx/eff/va0002.avfx", PenumbraManipulations.VfxGamePath('a', 53, 2));
    }

    [Fact]
    public void No_publishing_host_drops_the_effect()   // [EDGE-item-vfx-host-6]
    {
        var (assigned, dropped) = EffectHostPlanner.Assign([], [new R(0, -1)]);
        Assert.Empty(assigned);
        Assert.Contains("no accessory is drawn", Assert.Single(dropped).Reason);
    }

    [Fact]
    public void Unknown_variant_is_ineligible()   // [EDGE-item-vfx-host-11]
    {
        var (assigned, dropped) = EffectHostPlanner.Assign(
            [Carrier(0) with { Variant = null }, Carrier(1) with { OwnVfxId = null }], [new R(0, 0)]);
        Assert.Empty(assigned);
        Assert.Single(dropped);
    }

    [Fact]
    public void Worn_items_never_carry_an_effect()   // [EDGE-item-vfx-host-16]
    {
        // Only worn items drawn: the effect is dropped rather than rewriting the player's own item's IMC entry.
        var (assigned, dropped) = EffectHostPlanner.Assign([Worn(0), Worn(1, "wrs")], [new R(0, 0)]);
        Assert.Empty(assigned);
        Assert.Contains("worn item", Assert.Single(dropped).Reason);
    }

    [Fact]
    public void Facewear_is_never_an_effect_host()   // [EDGE-item-vfx-host-17]
    {
        var glasses = new C(0, "met", IsCarrier: false, Variant: 1, OwnVfxId: 0);
        Assert.Equal(0, EffectHostPlanner.VfxIdFor("met"));
        var (assigned, _) = EffectHostPlanner.Assign([glasses], [new R(0, 0)]);
        Assert.Empty(assigned);
    }

    [Fact]
    public void A_path_the_game_already_ships_is_not_taken()   // [EDGE-item-vfx-host-19]
    {
        var (assigned, _) = EffectHostPlanner.Assign([Carrier(0) with { IdFree = false }, Carrier(1, "nek")],
            [new R(0, 0)]);
        Assert.Equal(new EffectHostPlanner.Assignment(0, 1, 4), Assert.Single(assigned));
    }
}
