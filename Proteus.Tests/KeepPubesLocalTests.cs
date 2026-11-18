using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// "Keep the new body's pubes" on the real bodies that ship pubes: Bibo+ (<c>_bibopube</c>) and Rue+ (its crotch mesh
/// <c>_betterpube</c> and its pubic hair <c>_bibopube</c>). Bare legs refitted across the two, through the Studio's own plan
/// call — the same <see cref="BodyRetarget.Plan"/> the size set and the automatic refit make.
/// </summary>
public class KeepPubesLocalTests(ITestOutputHelper output)
{
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string BiboLegs = "Bottoms - Small Clothes/Type A - Medium/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string RueLegs = "files/legs - smallclothes/skull crushers - a/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string BiboPube = "/mt_c0201b0001_bibopube.mtrl";
    private const string BetterPube = "/mt_c0201b0001_betterpube.mtrl";

    private static string PathOf(string mod, string rel) => BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), rel);

    /// <summary>The bare legs at <paramref name="fromPath"/> refitted onto the legs at <paramref name="toPath"/>.</summary>
    private static byte[] Refit(string fromPath, string toPath, bool pubes)
    {
        var bytes = File.ReadAllBytes(fromPath);
        var garment = ModelPartReader.Read(bytes)!;
        Assert.Null(BodyRetarget.BuildPair("_dwn", fromPath, toPath, "_dwn", false, null, null, null, out var pair));
        var rule = RetargetInputs.StudioSkin(replaceSkin: true, cutHidden: true, clearBody: false, gameGear: false, "_dwn",
                                             crossSex: false);
        return RetargetInputs.StudioPlan(garment, bytes, [pair], "_dwn", null, layOnBody: true, acrossBodies: true, rule,
                                         cut: true, pieces: null, carryPiercings: false, liftUnderbust: true,
                                         carryPubes: pubes).Model;
    }

    private static int Drawn(byte[] mdl, string material)
        => ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island < 0 && p.Material == material).Sum(p => p.Triangles.Length / 3);

    /// <summary>Every name drawn is in the table, once; no triangle is degenerate.</summary>
    private static void AssertSound(byte[] mdl)
    {
        var table = SecondSkinWriter.MaterialNames(mdl);
        Assert.Empty(SecondSkinWriter.DrawnMaterialNames(mdl).Except(table));
        foreach (string pube in new[] { BiboPube, BetterPube })
            Assert.True(table.Count(t => t == pube) <= 1, $"{pube} named {table.Count(t => t == pube)} times");
        var parts = ModelPartReader.Read(mdl)!;
        Vector3 At(int v) => new(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]);
        foreach (var part in parts.Parts.Where(p => p.Island < 0 && BodyRetarget.IsBodyPubeMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var a = At(part.Triangles[t]);
                Assert.True(Vector3.Cross(At(part.Triangles[t + 1]) - a, At(part.Triangles[t + 2]) - a).Length() > 0f,
                            $"degenerate triangle in {part.Material}");
            }
    }

    [LocalDataFact(Rue, Bibo)]
    [Trait("Plan", "AC-refit-keep-pubes-3")]
    [Trait("Plan", "AC-refit-keep-pubes-1")]
    [Trait("Plan", "EDGE-refit-keep-pubes-6")]
    [Trait("Speed", "Slow")]
    public void Rue_legs_onto_Bibo_carry_Bibo_s_pubes_only_when_asked()
    {
        // [AC-refit-keep-pubes-3] [AC-refit-keep-pubes-1] [EDGE-refit-keep-pubes-6] Rue+ legs carry _betterpube and
        // _bibopube; onto Bibo+, Bibo's come in only when asked, and Rue's crotch mesh leaves by name either way.
        string rue = PathOf(Rue, RueLegs), bibo = PathOf(Bibo, BiboLegs);
        Assert.True(Drawn(File.ReadAllBytes(rue), BetterPube) > 0, "the case is not reached: Rue's legs carry no _betterpube");

        var on = Refit(rue, bibo, pubes: true);
        var off = Refit(rue, bibo, pubes: false);
        output.WriteLine($"on: bibopube {Drawn(on, BiboPube)}, betterpube {Drawn(on, BetterPube)}; "
                       + $"off: bibopube {Drawn(off, BiboPube)}, betterpube {Drawn(off, BetterPube)}");
        Assert.True(Drawn(on, BiboPube) > 0, "Bibo's pubes did not come in");
        Assert.Equal(0, Drawn(on, BetterPube));
        Assert.DoesNotContain(BetterPube, SecondSkinWriter.MaterialNames(on));
        Assert.Equal(0, Drawn(off, BiboPube));
        Assert.Equal(0, Drawn(off, BetterPube));
        AssertSound(on);
        AssertSound(off);
    }

    [LocalDataFact(Bibo, Rue)]
    [Trait("Plan", "AC-refit-keep-pubes-5")]
    [Trait("Plan", "EDGE-refit-keep-pubes-19")]
    [Trait("Plan", "EDGE-refit-keep-pubes-16")]
    public void Bibo_legs_onto_Rue_carry_both_of_Rue_s_pube_materials()
    {
        // [AC-refit-keep-pubes-5] [EDGE-refit-keep-pubes-19] [EDGE-refit-keep-pubes-16] Rue's two pube materials, both, each
        // named once; none with the box off.
        string rue = PathOf(Rue, RueLegs), bibo = PathOf(Bibo, BiboLegs);
        var on = Refit(bibo, rue, pubes: true);
        var off = Refit(bibo, rue, pubes: false);
        output.WriteLine($"on: bibopube {Drawn(on, BiboPube)}, betterpube {Drawn(on, BetterPube)}");
        Assert.True(Drawn(on, BetterPube) > 0, "Rue's crotch mesh did not come in");
        Assert.True(Drawn(on, BiboPube) > 0, "Rue's pubic hair did not come in");
        Assert.Equal(0, Drawn(off, BetterPube));
        Assert.Equal(0, Drawn(off, BiboPube));
        AssertSound(on);
        AssertSound(off);
    }
}
