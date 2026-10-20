using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>The earless copy of a Miqo'te hairstyle, on synthetic models: what it draws, what it keeps.</summary>
public class EarlessHairTests
{
    /// <summary>LOD0 triangles that draw (three distinct corners).</summary>
    private static int Drawn(byte[] mdl)
        => ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island == -1).Sum(p =>
           {
               int n = 0;
               for (int t = 0; t + 2 < p.Triangles.Length; t += 3)
                   if (p.Triangles[t] != p.Triangles[t + 1] && p.Triangles[t + 1] != p.Triangles[t + 2]
                       && p.Triangles[t] != p.Triangles[t + 2]) n++;
               return n;
           });

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-2")]
    public void Make_collapses_every_fur_triangle_and_keeps_the_rest()
    {
        var author = EaredHairModel(801);
        int fur = EarlessHair.DrawnFur(author);
        Assert.True(fur > 0);

        var result = EarlessHair.Make(author, out var why);
        Assert.True(result != null, why);
        Assert.Equal(0, EarlessHair.DrawnFur(result!.Model));
        Assert.Equal(0, EarlessHair.DrawnFur(result.Model, shapes: true));
        // Only the ear went: every other triangle still draws, and nothing moved or was renumbered.
        Assert.Equal(Drawn(author) - fur, Drawn(result.Model));
        Assert.Equal(ModelPartReader.Read(author)!.Positions, ModelPartReader.Read(result.Model)!.Positions);
        Assert.Equal(fur, result.Hidden);
        Assert.Equal(author.Length, result.Model.Length);
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-6")]
    [Trait("Plan", "EDGE-earless-miqote-hair-3")]
    public void Hair_without_drawn_fur_is_not_converted()
    {
        var made = EarlessHair.Make(EaredHairModel(801), out _)!.Model;
        foreach (var hair in new[] { HairModel(801), EaredHairModel(801, earWeight: 0.1f), made })
        {
            Assert.Equal(0, EarlessHair.DrawnFur(hair));
            Assert.Null(EarlessHair.Make(hair, out var why));
            Assert.Equal("", why);
        }
    }

    [Fact]
    [Trait("Plan", "AC-earless-miqote-hair-7")]
    public void Port_onto_a_miqote_draws_no_fur()
    {
        var port = HairPortBaker.Prepare(HairModel(201), 201, 801, Id, Pbd(), new System.Collections.Generic.HashSet<string>(),
                                         HeadModel(201), HeadModel(801), out var why);
        Assert.True(port != null, why);
        Assert.Equal(0, EarlessHair.DrawnFur(port!.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-earless-miqote-hair-17")]
    public void Multi_lod_model_is_trimmed_to_lod0()
    {
        // The header says three levels: either cut to LOD0, or the cut refused and said why — LOD0 earless either way.
        var author = EaredHairModel(801);
        author[64] = 3;
        var result = EarlessHair.Make(author, out var why);
        Assert.True(result != null, why);
        Assert.Equal(0, EarlessHair.DrawnFur(result!.Model));
        if (result.LodTrimmed) Assert.Equal(1, ModelLodTrimmer.LodCount(result.Model));
        else Assert.NotEqual("", result.TrimRefusal);

        // One level already: nothing to cut, nothing to say.
        var single = EarlessHair.Make(EaredHairModel(801), out _)!;
        Assert.False(single.LodTrimmed);
        Assert.Equal("", single.TrimRefusal);
    }
}
