extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Data = Proteus.Tests.SizeSetBatchData;
using SizeStatus = Proteus.Services.SizeSetRefit.SizeStatus;

namespace Proteus.Tests;

/// <summary>
/// Equinox as it shipped, unpacked and converted, sized once with "Made on" = Bibo+ for every women's body and the men's,
/// over the bodies and the Elegy underwear a scan of the whole mod folder finds (Elegy ships Rue's skin: its Rue Large
/// used to read as made on it) — the run the tests below read.
/// </summary>
public sealed class EquinoxRun : IDisposable
{
    internal static readonly string[] Bodies =
        [Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse, Data.TbseX, Data.ElegySylvie];

    internal static readonly string[] Needs =
        [Data.EquinoxPack, .. Bodies, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps];

    internal string A { get; } = "";
    internal List<(string Group, List<string> Options, int? Default)> Before { get; } = [];
    internal List<SizeSetRefit.Piece> Pieces { get; } = [];
    internal SizeSetRefit.RunLog LogA { get; } = new();
    internal string SaidA { get; } = "";
    internal TimeSpan TimeA { get; }

    public EquinoxRun()
    {
        if (LocalData.Missing(Needs) != null) return;
        var readGame = Data.Game();
        A = Data.UnpackedPack(Data.EquinoxPack);
        PenumbraModMeta.MigrateWorkCopy(A, LocalData.Path(LocalData.Mods));
        Before = Data.Shape(A);
        Pieces = SizeSetRefit.PiecesOf(A, readGame);
        var said = new StringWriter();
        var clock = Stopwatch.StartNew();
        LogA = Data.Batch(A, SizeSetBatch.WomensKeys, men: true, Bodies, readGame: readGame,
                          uvmaps: LocalData.Path(LocalData.TbseUvMaps), madeOn: Data.Folder(Data.Bibo),
                          log: RefitCli::Proteus.Refit.ConsoleLog.To(said)).Log;
        TimeA = clock.Elapsed;
        SaidA = said.ToString();
    }

    public void Dispose() => Data.Delete(A);
}

[Trait("Speed", "Slow")]
public class EquinoxSizeSetLocalTests(ITestOutputHelper output, EquinoxRun run) : IClassFixture<EquinoxRun>
{
    private const string Top = "chara/equipment/e0317/model/c0201e0317_top.mdl";
    private const string Dwn = "chara/equipment/e0317/model/c0201e0317_dwn.mdl";

    private static readonly string[] BottomSize =
        ["Small", "Medium", "Large", "Small Tummy", "Medium Tummy", "Large Tummy", "Yab WC", "Yab SC", "Rue WC"];

    private void Print(SizeSetRefit.RunLog log)
    {
        foreach (var s in log.Sizes)
            output.WriteLine($"{s.Slot} {(s.Men ? "men " : "")}{s.Group} / {s.Option} {s.Status} {s.ReasonCode} cut from {s.CutFrom}"
                             + (s.Measures is { } m ? $" folded {m.Folded}" : ""));
    }

    /// <summary>A size's model in a run's copy: the file our record names for that option and game path.</summary>
    private static ModelParts ModelOf(string copy, string option, string gamePath)
    {
        var entry = BodyRetargetWriter.ReadRecord(copy)!.Options
            .Single(e => e.Name == option && string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
        return ModelPartReader.Read(File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(copy, entry.File)))!;
    }

    private static void AssertNoDegenerate(ModelParts m)
    {
        System.Numerics.Vector3 P(int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        foreach (var part in m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                Assert.True(System.Numerics.Vector3.Cross(P(part.Triangles[t + 1]) - P(part.Triangles[t]),
                                                          P(part.Triangles[t + 2]) - P(part.Triangles[t])).LengthSquared() > 1e-16f,
                            "a degenerate skin triangle");
    }

    /// <summary>[HS] Rosaline (Base)'s "Coconut" smallclothes chest, as the size set's Rosaline Coconut size reads it.</summary>
    private static ModelParts CoconutBody()
    {
        string rel = SupportedBodies.Of("rosaline", "_top", false)!.Sizes.Single(z => z.Name == "Coconut").Rel;
        return ModelPartReader.Read(File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Data.Rosaline), rel)))!;
    }

    [LocalDataFact(Data.EquinoxPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, Data.ElegySylvie, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-equinox-legs-slits-1")]
    [Trait("Plan", "AC-equinox-legs-slits-3")]
    public void Equinox_legs_are_sized_in_Bottom_Size_for_every_body()
    {
        Print(run.LogA);
        output.WriteLine($"run {run.TimeA.TotalSeconds:F0} s");

        var legs = Assert.Single(run.Pieces, p => p.Slot == "_dwn" && p.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Bottom Size", legs.Group);
        Assert.Equal(BottomSize, legs.Options.Select(o => o.Option));
        Assert.False(legs.FollowsChest);

        var madeLegs = run.LogA.Sizes.Where(s => s.Slot == "_dwn" && !s.Men && s.Status == SizeStatus.Made).ToList();
        Assert.All(madeLegs, s => Assert.Equal("Bottom Size", s.Group));
        foreach (string body in new[] { "neolithe", "bibo", "tre", "rosaline" })
            Assert.Contains(madeLegs, s => s.Body == body);
        Assert.Equal(3, madeLegs.Count(s => s.Body == "neolithe"));
        Assert.Equal(3, madeLegs.Count(s => s.Body == "bibo"));
        Assert.Equal(2, madeLegs.Count(s => s.Body == "tre"));

        // No legs refit went into Top Size (a new chest size there still carries its source option's own legs, which
        // Bottom Size outranks); its author options keep the legs they carried.
        var after = Data.Shape(run.A);
        Data.AssertOnlyAppended(run.Before, after);
        var redirects = PenumbraModMeta.ReadAllRedirects(run.A);
        Assert.DoesNotContain(BodyRetargetWriter.ReadRecord(run.A)!.Options,
                              e => e.Group == "Top Size" && e.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(redirects, r => r.Source == "Top Size / Rue Large" && r.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("sized in \"Bottom Size\", not \"Top Size\"", run.SaidA);
        Assert.Contains("applied before it while on: none", run.SaidA);
    }

    [LocalDataFact(Data.EquinoxPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, Data.ElegySylvie, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-equinox-legs-slits-2")]
    public void Equinox_gets_TBSE_and_TBSE_X_tops_and_legs()
    {
        var mens = run.LogA.Sizes.Where(s => s.Men && s.Status == SizeStatus.Made).ToList();
        foreach (var s in mens) output.WriteLine($"{s.Slot} {s.Body} {s.Option} folded {s.Measures?.Folded} cut from {s.CutFrom}");

        foreach (string body in new[] { "tbse", "tbse-x" })
            foreach (string slot in new[] { "_top", "_dwn" })
            {
                var size = Assert.Single(mens, s => s.Body == body && s.Slot == slot);
                Assert.True(size.Measures!.Folded <= 100, $"{body} {slot}: {size.Measures.Folded} folded");
                if (slot == "_dwn") Assert.Contains(size.CutFrom ?? "", BottomSize);
            }
    }

    [LocalDataFact(Data.EquinoxPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, Data.ElegySylvie, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-equinox-legs-slits-4")]
    [Trait("Plan", "AC-equinox-legs-slits-8")]
    public void Equinox_Rosaline_Coconut_top_is_read_on_Rue_and_left_whole()
    {
        // Rue Large ties Rue+ and the Elegy underwear (55.2% each): the body mod wins, so Coconut is cut from Rue's body.
        // Read on Elegy, as before, the same size left 10.2 cm² of the cups uncovered, 37.41 cm² of skin through the
        // cloth and 19 folds.
        // (Its legs' Yab WC is no tie: it shares 96.8% with Elegy's legs and 88.0% with YAB+'s, so it reads on Elegy, as
        // a clear share should.)
        Assert.DoesNotContain(run.LogA.Sizes, s => s.Slot == "_top" && (s.From ?? "").Contains("Elegy", StringComparison.OrdinalIgnoreCase));
        var coconut = run.LogA.Sizes.Single(s => s.Option == "Rosaline Coconut" && s.Slot == "_top");
        Assert.Equal("Rue Large", coconut.CutFrom);
        Assert.Contains("Rue+", coconut.From ?? "");

        var body = CoconutBody();
        var a = ModelOf(run.A, "Rosaline Coconut", Top);
        double uncovered = VanillaGearRefitLocalTests.UncoveredUndrawn(a, body, new SortedDictionary<(float, string), double>());
        double seen = VanillaGearRefitLocalTests.SeenIntoGarmentAllAround(a, body);
        double through = VanillaGearRefitLocalTests.SkinThrough(a, VanillaGearRefitLocalTests.Under);
        output.WriteLine($"from {coconut.From}: uncovered {uncovered:0.0} cm², seen into {seen:0.0} cm², skin through {through:0.00} cm², "
                         + $"folded {coconut.Measures!.Folded}");

        Assert.True(uncovered <= 1.0, $"{uncovered:0.0} cm² uncovered");
        Assert.True(seen <= 1.0, $"{seen:0.0} cm² seen into");
        Assert.True(through <= 37.41, $"skin through {through:0.00}");
        Assert.True(coconut.Measures.Folded <= 19, $"{coconut.Measures.Folded} folded");
        AssertNoDegenerate(a);
    }

    [LocalDataFact(Data.EquinoxPack, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-equinox-legs-slits-9")]
    public void Equinox_without_made_on_sizes_its_legs_from_Bottom_Size_alone()
    {
        string copy = Data.UnpackedPack(Data.EquinoxPack);
        try
        {
            PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
            var legs = SizeSetRefit.PiecesOf(copy).Single(p => p.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase));
            var log = Data.Batch(copy, ["rue"], men: false, [Data.Rue], readGame: Data.Game()).Log;
            foreach (string note in log.Notes) output.WriteLine(note);

            // Read from Bottom Size alone: its Rue WC reads on Rue without "Made on", so the legs are no longer the
            // unread "0 of the author's 6" Top Size gave; none of Top Size's legs is a source.
            Assert.Equal("Bottom Size", legs.Group);
            Assert.DoesNotContain(legs.Options, o => o.File.StartsWith("top size", StringComparison.OrdinalIgnoreCase));
            var legsOutcomes = log.Sizes.Where(s => s.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(legsOutcomes);
            Assert.All(legsOutcomes, s => Assert.Equal("Bottom Size", s.Group));
            Assert.DoesNotContain(legsOutcomes, s => s.ReasonCode == SizeSetRefit.Codes.NotMadeOn);
        }
        finally
        {
            Data.Delete(copy);
        }
    }
}
