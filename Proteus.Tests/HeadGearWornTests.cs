using System.Collections.Generic;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Hat compat fits a hairstyle only while a hat is worn, and "a hat" is read off the drawn model list. The "_met"
/// path is shared by head gear and facewear, so glasses alone must never count as a hat.
/// </summary>
public class HeadGearWornTests
{
    private static readonly HashSet<int> Facewear = [5521, 5501];

    [Fact]
    public void A_head_item_is_a_hat()
        => Assert.True(DrawnModelPaths.HeadGearWornFromModels(
            ["chara/equipment/e6085/model/c0201e6085_met.mdl"], Facewear));

    [Fact]
    public void Glasses_alone_are_not_a_hat()
        => Assert.False(DrawnModelPaths.HeadGearWornFromModels(
            ["chara/equipment/e5521/model/c0201e5521_met.mdl"], Facewear));

    [Fact]
    public void A_hat_beside_glasses_is_still_a_hat()
        => Assert.True(DrawnModelPaths.HeadGearWornFromModels(
            ["chara/equipment/e5521/model/c0201e5521_met.mdl",
             "chara/equipment/e6085/model/c0201e6085_met.mdl"], Facewear));

    [Fact]
    public void A_bare_head_is_not_a_hat()
        => Assert.False(DrawnModelPaths.HeadGearWornFromModels(
            ["chara/equipment/e0000/model/c0201e0000_met.mdl",
             "chara/human/c0201/obj/hair/h0001/model/c0201h0001_hir.mdl"], Facewear));

    /// <summary>Without the Glasses sheet a pair of glasses is indistinguishable from a hat, so nothing is.</summary>
    [Fact]
    public void Nothing_is_a_hat_when_facewear_is_unknown()
        => Assert.False(DrawnModelPaths.HeadGearWornFromModels(
            ["chara/equipment/e6085/model/c0201e6085_met.mdl"], null));
}
