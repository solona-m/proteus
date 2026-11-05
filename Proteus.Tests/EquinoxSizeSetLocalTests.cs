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
/// Equinox as it shipped, unpacked and converted, sized with "Made on" = Bibo+: once for every women's body and the men's
/// (A), and once for the women's bodies with the seen-slit pass off (B) — the runs the tests below read.
/// </summary>
public sealed class EquinoxRun : IDisposable
{
    internal static readonly string[] Bodies =
        [Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse, Data.TbseX];

    internal static readonly string[] Needs =
        [Data.EquinoxPack, .. Bodies, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps];

    internal string A { get; } = "";
    internal string B { get; } = "";
    internal List<(string Group, List<string> Options, int? Default)> Before { get; } = [];
    internal List<SizeSetRefit.Piece> Pieces { get; } = [];
    internal SizeSetRefit.RunLog LogA { get; } = new();
    internal SizeSetRefit.RunLog LogB { get; } = new();
    internal string SaidA { get; } = "";
    internal TimeSpan TimeA { get; }
    internal TimeSpan TimeB { get; }

    public EquinoxRun()
    {
        if (LocalData.Missing(Needs) != null) return;
        var readGame = Data.Game();
        A = Prepared();
        B = Prepared();
        Before = Data.Shape(A);
        Pieces = SizeSetRefit.PiecesOf(A, readGame);
        var said = new StringWriter();
        var clock = Stopwatch.StartNew();
        LogA = Data.Batch(A, SizeSetBatch.WomensKeys, men: true, Bodies, readGame: readGame,
                          uvmaps: LocalData.Path(LocalData.TbseUvMaps), madeOn: Data.Folder(Data.Bibo),
                          log: RefitCli::Proteus.Refit.ConsoleLog.To(said)).Log;
        TimeA = clock.Elapsed;
        clock.Restart();
        LogB = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeenSlits: true),
            () => Data.Batch(B, SizeSetBatch.WomensKeys, men: false, Bodies, readGame: readGame,
                             uvmaps: LocalData.Path(LocalData.TbseUvMaps), madeOn: Data.Folder(Data.Bibo)).Log);
        TimeB = clock.Elapsed;
        SaidA = said.ToString();
    }

    private static string Prepared()
    {
        string copy = Data.UnpackedPack(Data.EquinoxPack);
        PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
        return copy;
    }

    public void Dispose()
    {
        Data.Delete(A);
        Data.Delete(B);
    }
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

    private static byte[] BytesOf(string copy, string option, string gamePath)
    {
        var entry = BodyRetargetWriter.ReadRecord(copy)!.Options
            .Single(e => e.Name == option && string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
        return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(copy, entry.File));
    }

    private static IEnumerable<System.Numerics.Vector3> ClothOf(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                  .SelectMany(p => p.Triangles)
                  .Select(v => new System.Numerics.Vector3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]));

    private static ModelParts CoconutBody()
    {
        var catalog = BodySizeCatalog.Read(LocalData.Path(Data.Rosaline));
        var coconut = catalog.For("_top").Single(o => o.Label.Contains("Coconut", StringComparison.OrdinalIgnoreCase));
        return ModelPartReader.Read(File.ReadAllBytes(catalog.PathOf(coconut)))!;
    }

    [LocalDataFact(Data.EquinoxPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-equinox-legs-slits-1")]
    [Trait("Plan", "AC-equinox-legs-slits-3")]
    public void Equinox_legs_are_sized_in_Bottom_Size_for_every_body()
    {
        Print(run.LogA);
        output.WriteLine($"run A {run.TimeA.TotalSeconds:F0} s, run B {run.TimeB.TotalSeconds:F0} s");

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

        // Top Size gained only tops; its author options keep the legs they carried.
        var after = Data.Shape(run.A);
        Data.AssertOnlyAppended(run.Before, after);
        var redirects = PenumbraModMeta.ReadAllRedirects(run.A);
        var topSize = run.Before.Single(g => g.Group == "Top Size");
        var added = after.Single(g => g.Group == "Top Size").Options.Skip(topSize.Options.Count).ToList();
        Assert.DoesNotContain(redirects, r => r.Source.StartsWith("Top Size / ", StringComparison.Ordinal)
                                               && added.Contains(r.Source["Top Size / ".Length..])
                                               && r.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(redirects, r => r.Source == "Top Size / Rue Large" && r.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("sized in \"Bottom Size\", not \"Top Size\"", run.SaidA);
        Assert.Contains("applied before it while on: none", run.SaidA);
    }

    [LocalDataFact(Data.EquinoxPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
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
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-equinox-legs-slits-4")]
    [Trait("Plan", "EDGE-equinox-legs-slits-10")]
    public void Equinox_Rosaline_Coconut_top_has_narrower_cup_slits()
    {
        var body = CoconutBody();
        var a = ModelOf(run.A, "Rosaline Coconut", Top);
        var b = ModelOf(run.B, "Rosaline Coconut", Top);
        var bands = new SortedDictionary<(float, string), double>();
        double uncoveredA = VanillaGearRefitLocalTests.UncoveredUndrawn(a, body, bands);
        double uncoveredB = VanillaGearRefitLocalTests.UncoveredUndrawn(b, body, new SortedDictionary<(float, string), double>());
        double seenA = VanillaGearRefitLocalTests.SeenIntoGarmentAllAround(a, body);
        double seenB = VanillaGearRefitLocalTests.SeenIntoGarmentAllAround(b, body);
        double throughA = VanillaGearRefitLocalTests.SkinThrough(a, VanillaGearRefitLocalTests.Under);
        double throughB = VanillaGearRefitLocalTests.SkinThrough(b, VanillaGearRefitLocalTests.Under);
        int foldedA = run.LogA.Sizes.Single(s => s.Option == "Rosaline Coconut" && s.Slot == "_top").Measures!.Folded;
        int foldedB = run.LogB.Sizes.Single(s => s.Option == "Rosaline Coconut" && s.Slot == "_top").Measures!.Folded;
        output.WriteLine($"uncovered {uncoveredB:0.0} → {uncoveredA:0.0} cm², seen into {seenB:0.0} → {seenA:0.0} cm², "
                         + $"skin through {throughB:0.00} → {throughA:0.00} cm², folded {foldedB} → {foldedA}");

        // The fixture still shows the slit without the pass…
        Assert.True(uncoveredB >= 9.0, $"{uncoveredB:0.0} cm² uncovered without the pass");
        Assert.True(seenB >= 9.0, $"{seenB:0.0} cm² seen into without the pass");
        // …and the pass draws into it, no skin through the cloth and no fold added.
        Assert.True(ClothOf(a).SequenceEqual(ClothOf(b)), "the cloth moved");
        Assert.True(uncoveredA <= 6.5, $"{uncoveredA:0.0} cm² uncovered");
        Assert.True(seenA <= 7.0, $"{seenA:0.0} cm² seen into");
        Assert.True(throughA <= throughB, $"skin through {throughA:0.00} against {throughB:0.00}");
        Assert.Equal(foldedB, foldedA);
    }

    [LocalDataFact(Data.EquinoxPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-equinox-legs-slits-5")]
    [Trait("Plan", "EDGE-equinox-legs-slits-17")]
    public void Every_other_Equinox_top_is_unchanged_or_only_gains_skin()
    {
        var made = run.LogB.Sizes.Where(s => !s.Men && s.Status == SizeStatus.Made).ToList();
        Assert.NotEmpty(made);
        int changed = 0;
        foreach (var s in made)
        {
            var bytesA = BytesOf(run.A, s.Option!, s.GamePath);
            var bytesB = BytesOf(run.B, s.Option!, s.GamePath);
            if (bytesA.AsSpan().SequenceEqual(bytesB)) continue;
            Assert.Equal("_top", s.Slot);   // legs never change
            changed++;
            var a = ModelPartReader.Read(bytesA)!;
            var b = ModelPartReader.Read(bytesB)!;
            Assert.True(ClothOf(a).SequenceEqual(ClothOf(b)), $"{s.Option}: the cloth moved");
            Assert.True(VanillaGearRefitLocalTests.SkinThrough(a, VanillaGearRefitLocalTests.Under)
                        <= VanillaGearRefitLocalTests.SkinThrough(b, VanillaGearRefitLocalTests.Under) + 1e-6, $"{s.Option}: skin through");
            Assert.Equal(s.Measures!.Folded,
                         run.LogA.Sizes.Single(x => !x.Men && x.Option == s.Option && x.GamePath == s.GamePath).Measures!.Folded);
            output.WriteLine($"{s.Option}: only skin added");
        }
        output.WriteLine($"{changed} of {made.Count} sizes gained skin; the rest byte-identical");
    }

    [LocalDataFact(Data.EquinoxPack, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-equinox-legs-slits-9")]
    public void Equinox_without_made_on_names_the_legs_it_cannot_read()
    {
        string copy = Data.UnpackedPack(Data.EquinoxPack);
        try
        {
            PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
            var legs = SizeSetRefit.PiecesOf(copy).Single(p => p.GamePath.Equals(Dwn, StringComparison.OrdinalIgnoreCase));
            var log = Data.Batch(copy, ["rue"], men: false, [Data.Rue], readGame: Data.Game()).Log;
            foreach (string note in log.Notes) output.WriteLine(note);

            Assert.Equal("Bottom Size", legs.Group);
            Assert.DoesNotContain(legs.Options, o => o.File.StartsWith("top size", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(string.Format(Strings.SizeSet.UnreadableMadeOnFmt, legs.Label), log.Notes);
        }
        finally
        {
            Data.Delete(copy);
        }
    }
}
