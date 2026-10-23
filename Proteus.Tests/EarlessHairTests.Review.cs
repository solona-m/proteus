using System.IO;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>Cutting an earless copy to LOD0, each way it can go, after the code review.</summary>
public partial class EarlessHairTests
{
    /// <summary>The game's own Miqo'te hairstyles carry three levels of detail; every one comes out cut to LOD0 and
    /// earless in every shape.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-earless-miqote-hair-17")]
    public void A_real_multi_lod_hair_is_cut_to_lod0()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        int cut = 0;
        foreach (int id in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 101, 102 })
        {
            if (data.GetFile(HairPortPlan.HairPath(801, id))?.Data is not { } src) continue;
            if (ModelLodTrimmer.LodCount(src) <= 1 || EarlessHair.DrawnFur(src) == 0) continue;
            var result = EarlessHair.Make(src, out var why);
            Assert.True(result != null, $"h{id:D4}: {why}");
            Assert.True(result!.LodTrimmed, $"h{id:D4}: {result.TrimRefusal}");
            Assert.Equal(1, ModelLodTrimmer.LodCount(result.Model));
            Assert.Equal(0, EarlessHair.DrawnFur(result.Model, shapes: true));
            cut++;
        }
        Assert.True(cut > 0, "no game Miqo'te hairstyle with several levels of detail and ears");
    }

    /// <summary>Three levels whose later two are empty: cut to LOD0, earless.</summary>
    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-17")]
    public void A_synthetic_three_lod_hair_is_cut_to_lod0()
    {
        var author = EaredHairModel(801);
        author[64] = 3;
        var result = EarlessHair.Make(author, out var why);
        Assert.True(result != null, why);
        Assert.True(result!.LodTrimmed, result.TrimRefusal);
        Assert.Equal(1, ModelLodTrimmer.LodCount(result.Model));
        Assert.Equal(0, EarlessHair.DrawnFur(result.Model, shapes: true));
    }

    /// <summary>A model the cut does not understand (its second level's vertex data lies inside the first's): the cut
    /// is refused, says why, and LOD0 is still earless.</summary>
    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-17")]
    public void A_refused_cut_keeps_lod0_earless_and_says_why()
    {
        var author = EaredHairModel(801);
        author[64] = 3;
        // File header: vertex offsets per level at 16, vertex buffer sizes at 40.
        System.BitConverter.GetBytes(System.BitConverter.ToUInt32(author, 16)).CopyTo(author, 20);
        System.BitConverter.GetBytes(4u).CopyTo(author, 44);
        var result = EarlessHair.Make(author, out var why);
        Assert.True(result != null, why);
        Assert.False(result!.LodTrimmed);
        Assert.NotEqual("", result.TrimRefusal);
        Assert.Equal(0, EarlessHair.DrawnFur(result.Model, shapes: true));
    }
}
