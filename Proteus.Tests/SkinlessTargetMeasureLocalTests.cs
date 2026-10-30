using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Neolithe's legs onto Muse+'s SFW legs, measured inside the correspondence: how the skin missed, how far the carry
/// left it from Muse's boxers, and what the projection did. These numbers set the skinless-target thresholds
/// (<see cref="UvAtlasCorrespondence.CoverReach"/>, <see cref="UvAtlasCorrespondence.SideBand"/>, the 50% floor), so
/// the run stops here if they contradict them.
/// </summary>
public class SkinlessTargetMeasureLocalTests(ITestOutputHelper output)
{
    internal const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";

    internal static readonly string[] MuseSfw = ["Defined - SFW Flat", "Defined - SFW Bulge", "Soft - SFW Flat", "Soft - SFW Bulge"];

    internal static readonly string[] NeolitheSfwSmall = ["DEFAULT LEGS - SmallClothes/SFW Small.mdl",
                                                          "NEOBELLY LEGS - SmallClothes/SFW Small.mdl"];

    internal static string MuseLegs(string option) => $"Smallclothes Legs/{option}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    internal static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null) ? uv : [];

    /// <summary>One pair measured as the correspondence measures it, with Neolithe's own legs mask when it has one.</summary>
    internal static (bool Built, string Refusal, UvAtlasCorrespondence.Stats Measured, UvAtlasCorrespondence? Built_)
        Measure(string neolithe, string muse)
    {
        var neoRoot = LocalData.Path(Neolithe);
        var sourceBytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(neoRoot, neolithe));
        var targetBytes = LocalData.PackEntry(LocalData.MusePack, MuseLegs(muse));
        var source = ModelPartReader.Read(sourceBytes)!;
        var catalog = BodySizeCatalog.Read(neoRoot);
        if (RefitCore.MaskOf(Path.GetFileName(neoRoot), catalog, "_dwn", null) is { } mask)
            source = BodyRetarget.Without(source, BodyRetarget.UndrawnVariants(source.AttributeNames, "_dwn", mask));
        var target = ModelPartReader.Read(targetBytes)!;
        bool built = UvAtlasCorrespondence.TryBuild(source, BodyCorrespondence.SameTile(Uv(sourceBytes)), target,
                                                    BodyCorrespondence.SameTile(Uv(targetBytes)), "legs",
                                                    out var correspondence, out string refusal, out var measured,
                                                    sameLayout: true);
        return (built, refusal, measured, correspondence);
    }

    public static TheoryData<string, string> Pairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var n in NeolitheSfwSmall)
            foreach (var m in MuseSfw)
                data.Add(n, m);
        return data;
    }

    [LocalDataTheory(Neolithe, LocalData.MusePack)]
    [MemberData(nameof(Pairs))]
    [Trait("Speed", "Slow")]
    public void Neolithe_SFW_legs_onto_Muse_SFW_measure_within_the_thresholds(string neolithe, string muse)
    {
        var (built, refusal, m, _) = Measure(neolithe, muse);
        output.WriteLine($"{neolithe} -> {muse}: {(built ? "built" : refusal)}");
        output.WriteLine($"  landed {m.Landed}/{m.Placeable} ({m.VertexCoverage:P1}); no landing {m.NoLanding}; astray {m.Astray}; " +
                         $"excused {m.Excused}; set aside {m.Disagreed}+{m.Unvouched}; cover p99 {m.P99Cover * 1000f:F1} mm " +
                         $"max {m.MaxCover * 1000f:F1} mm; unprojected {m.Unprojected}; on skin {m.OnSkin}; " +
                         $"folded elsewhere {m.FoldedElsewhere}; " +
                         $"folded in islands {m.FoldedInIslands}");
        Assert.True(built, refusal);

        Assert.Equal(0, m.Astray);
        int trusted = m.Landed - m.Distrusted;
        Assert.True(trusted >= UvAtlasCorrespondence.MinLandedOnSkin * m.Placeable, $"only {trusted} of {m.Placeable} on skin");
        Assert.True(m.Excused >= 0.99f * m.NoLanding, $"{m.NoLanding - m.Excused} points with no skin beyond the boxers");
        Assert.True(trusted >= 0.8f * (m.Placeable - m.Excused), "under 80% of what remains");
        Assert.True(m.OnSkin <= 0.1f * m.Excused, $"{m.OnSkin} of {m.Excused} landed on skin, not the boxers");
    }

    /// <summary>The screenshot's figure: Neolithe's DEFAULT "SFW Small" onto Muse "Defined - SFW Flat" lands 55%.</summary>
    [LocalDataFact(Neolithe, LocalData.MusePack)]
    public void The_reported_pair_measures_as_the_screenshot()
    {
        var (_, _, m, _) = Measure(NeolitheSfwSmall[0], "Defined - SFW Flat");
        output.WriteLine($"{m.VertexCoverage:P1}");
        Assert.Equal("55%", m.VertexCoverage.ToString("P0", System.Globalization.CultureInfo.InvariantCulture).Replace(" ", ""));
    }
}
