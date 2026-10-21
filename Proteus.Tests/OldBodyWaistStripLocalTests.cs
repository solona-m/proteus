using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// "[HS] Metro Style" pants, made on Lithe, refitted onto Neolithe with the new body's skin: the waist strip of skin the
/// pants carry runs up past where Neolithe's legs stop (its chest draws that band), so laid onto Neolithe only 89% of it
/// sits on the legs. The solver, judging it on the body it was made on, parted the cloth from it all the same, and the
/// rebuild must swap it too — kept, it drew Lithe's hips on the Neolithe character.
/// </summary>
public class OldBodyWaistStripLocalTests(ITestOutputHelper output)
{
    private const string Pants = LocalData.Mods + ":hs-Metro Style-1.0.0-zOJ/files/common/3/c0201e0705_dwn.mdl";
    private const string LitheLegs = LocalData.Mods
        + ":l ⬦ lithe + [nsfw]/p3/smallclothes legs/[nsfw] large, default, gen a/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string NeolitheLegs = LocalData.Mods + ":Neolithe [ALL IN ONE]/default legs - smallclothes/gen a large.mdl";

    [LocalDataFact(Pants, LitheLegs, NeolitheLegs)]
    public void Old_body_skin_reaching_past_the_new_legs_is_swapped()
    {
        var bytes = File.ReadAllBytes(LocalData.Path(Pants));
        var garment = ModelPartReader.Read(bytes)!;
        Assert.Null(BodyRetarget.BuildPair("_dwn", LocalData.Path(LitheLegs), LocalData.Path(NeolitheLegs), "_dwn", false,
                                           null, null, null, out var pair));
        int authoredSkin = garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                        .Sum(p => p.TriangleCount);

        var plan = BodyRetarget.Plan(garment, bytes, [pair], "_dwn", replaceSkin: true, acrossBodies: true);
        output.WriteLine($"swap: {plan.Report.Swap}");

        var swap = Assert.NotNull(plan.Report.Swap);
        Assert.Equal(authoredSkin, swap.Removed);
        Assert.Equal(0, swap.Kept);
        Assert.True(swap.Added > 0, "Neolithe's legs skin should have gone in");
    }
}
