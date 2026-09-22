using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A Miqo'te's ear fur ships inside the HAIRSTYLE, weighted to <c>j_mimi_l</c>/<c>j_mimi_r</c>, and it stands
/// exactly where a hat's rim plane says "under the hat". Tagged <c>atr_kam</c> with the rest of the hair it
/// vanishes when a hat goes on, and the ears come out bald — hats leave a Miqo'te's ears out, so the fur has
/// to survive both the cut and the press.
/// </summary>
public class HatCompatEarFurTests
{
    /// <summary>The wearer's face model: enough vertices for <c>HeadFrameFrom</c> to accept it.</summary>
    private static byte[] Head() => SyntheticModel.Build(
        ["atr_hv_a"],
        new SyntheticModel.Mesh("/mt_c0801f0001_fac_a.mtrl",
                                new SyntheticModel.Sub(0, TrianglesPerIsland: 32)));

    /// <summary>
    /// Well clear of the head the fixture above describes (centre y 16), so every triangle of the hair below
    /// sits above the hat's rim and would be cut away but for what carries it.
    /// </summary>
    private const float HairY = 40f;

    /// <summary>
    /// Two identical pieces of hair in the same place: mesh 0 on the head bone, mesh 1 on whatever
    /// <paramref name="fur"/> says. Only the skinning tells them apart, which is the whole point.
    /// </summary>
    private static byte[] Hair(params (string Bone, float W)[] fur) => SyntheticModel.Build(
        ["atr_hv_a"],
        new SyntheticModel.Mesh("/mt_c0801h0115_hir_b.mtrl",
            new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetY: HairY, Weights: [("j_kao", 1f)])),
        new SyntheticModel.Mesh("/mt_c0801h0115_hir_a.mtrl",
            new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetY: HairY, Weights: fur)));

    private static HatCompatSolve.Result Solve(byte[] hair)
    {
        var parts = ModelPartReader.Read(hair);
        Assert.NotNull(parts);
        return HatCompatSolve.Solve(hair, parts, Head(), raceCode: "0801");
    }

    /// <summary>
    /// The control. Both pieces hang off the head bone, both are above the rim, and both go — without this
    /// the test below could pass because the fixture was never under a hat at all.
    /// </summary>
    [Fact]
    public void HairAboveTheRimIsCutWhicheverMeshItIsIn()
    {
        var cut = Solve(Hair(("j_kao", 1f))).Cut;
        Assert.Contains(cut, p => p.Mesh == 0);
        Assert.Contains(cut, p => p.Mesh == 1);
    }

    [Fact]
    public void EarFurIsLeftAloneWhileTheHairBesideItIsCut()
    {
        var cut = Solve(Hair(("j_mimi_l", 1f))).Cut;
        Assert.Contains(cut, p => p.Mesh == 0);
        Assert.DoesNotContain(cut, p => p.Mesh == 1);
    }

    /// <summary>Pressing the fur onto the skull flattens the ears just as surely as cutting it hides them.</summary>
    [Fact]
    public void EarFurIsNeverPressed()
    {
        var moved = Solve(Hair(("j_mimi_r", 1f))).Moved;
        Assert.DoesNotContain(1, moved.Keys);
    }

    /// <summary>
    /// An earless conversion can leave one hair vertex holding a thousandth of an ear bone. That is not fur,
    /// and sparing it would leave a stray triangle standing through the hat.
    /// </summary>
    [Fact]
    public void AThousandthOfAnEarBoneIsNotFur()
    {
        var cut = Solve(Hair(("j_kao", 0.99f), ("j_mimi_l", 0.01f))).Cut;
        Assert.Contains(cut, p => p.Mesh == 1);
    }

    /// <summary>Every other race's ears live in the face model, so their hair must be read exactly as before.</summary>
    [Fact]
    public void AHairWithNoEarBoneSparesNothing()
    {
        var hair = Hair(("j_kao", 1f));
        var src = SecondSkinWriter.Parse(hair);
        var ears = HatCompatSolve.ReadEarGeometry(hair, src, HatCompatSolve.ReadLod0Meshes(hair));

        Assert.Empty(ears.Fur);
        Assert.Empty(ears.Touched);
    }

    /// <summary>
    /// What the fur costs: its own vertices, and every vertex sharing a triangle with one — the row it is
    /// rooted in, which the press has to leave where it is or the fur tears off its own base.
    /// </summary>
    [Fact]
    public void TheFurAndTheRowItGrowsFromAreBothSpared()
    {
        var hair = Hair(("j_mimi_l", 1f));
        var src = SecondSkinWriter.Parse(hair);
        var ears = HatCompatSolve.ReadEarGeometry(hair, src, HatCompatSolve.ReadLod0Meshes(hair));

        // Mesh 1 only: the hair in mesh 0 follows the head bone.
        Assert.All(ears.Fur, key => Assert.Equal(1, (int)(key >> 32)));
        Assert.Equal(12, ears.Fur.Count);                       // 4 triangles, 3 vertices each
        Assert.True(ears.Fur.IsSubsetOf(ears.Touched));
    }
}
