using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Choice = Proteus.Services.SizeSetRefit.EndMadeForChoice;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Route = Proteus.Services.KeepEnds.Route;

namespace Proteus.Tests;

/// <summary>
/// A skinless women's shoe made a men's from the body its outfit's own legs were made on (hands-feet-port D20): after
/// "Made on" and the cloth, before the game's body; the same item's legs; the first exact reading; one reading a run.
/// Halone's Chosen's boots, on real data, are in <see cref="HandsFeetPortLocalTests"/>.
/// </summary>
public class HandsFeetPortOutfitLegsTests
{
    [Theory]
    [Trait("Plan", "AC-hands-feet-port-12")]
    [Trait("Plan", "EDGE-hands-feet-port-43")]
    [Trait("Plan", "EDGE-hands-feet-port-44")]
    [InlineData("NoSkin", null, false, false, true, true, "Outfit")]
    [InlineData("NoSkin", null, false, false, false, true, "Outfit")]
    [InlineData("NoSkin", null, true, false, true, true, "MadeOn")]
    [InlineData("NoSkin", null, false, true, true, true, "Cloth")]
    [InlineData("NoSkin", null, true, true, true, true, "MadeOn")]
    [InlineData("NoSkin", null, false, false, true, false, "GameBody")]
    [InlineData("NoSkin", null, false, false, false, false, "NoSource")]
    [InlineData("SkinOnly", "bibo", false, false, true, true, "GameBody")]
    [InlineData("SkinOnly", null, false, true, true, true, "Cloth")]
    [InlineData("SkinOnly", null, false, false, false, true, "NoSource")]
    [InlineData("Skin", "bibo", false, false, true, true, "Read")]
    [InlineData("Skin", "gen3", false, false, true, true, "NoSource")]
    public void Outfit_legs_come_after_cloth_before_the_game(string route, string? layout, bool madeOn, bool cloth,
                                                             bool game, bool outfit, string expected) // [AC-hands-feet-port-12] [EDGE-hands-feet-port-43] [EDGE-hands-feet-port-44]
        => Assert.Equal(Enum.Parse<Choice>(expected),
                        SizeSetRefit.EndMadeFor(Enum.Parse<Route>(route), layout, madeOn, cloth, game, outfit));

    private static SizeSetRefit.Piece Piece(string path, params (string? Option, string File)[] options)
        => new(path, BodySizeCatalog.SlotOf(path)!, "G", options.Length > 0 ? options : [("A", "a.mdl")], path, "G",
               BodySizeCatalog.RaceOf(path)!);

    private const string Boot = "chara/equipment/e0613/model/c0201e0613_sho.mdl";
    private const string Legs = "chara/equipment/e0613/model/c0201e0613_dwn.mdl";

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-42")]
    [Trait("Plan", "EDGE-hands-feet-port-43")]
    public void Outfit_legs_are_the_same_item() // [EDGE-hands-feet-port-42] [EDGE-hands-feet-port-43]
    {
        var boot = Piece(Boot);
        var legs = Piece(Legs.Replace("/model/", "/Model/"));   // a path spelled in another case is the same path
        Assert.Same(legs, SizeSetRefit.OutfitLegsOf([boot, legs], boot));
        Assert.Null(SizeSetRefit.OutfitLegsOf([boot, Piece("chara/equipment/e0614/model/c0201e0614_dwn.mdl")], boot));
        Assert.Null(SizeSetRefit.OutfitLegsOf([boot, Piece("chara/equipment/e0613/model/c0101e0613_dwn.mdl")], boot));
        Assert.Null(SizeSetRefit.OutfitLegsOf([boot], boot));
        var glove = Piece("chara/equipment/e0613/model/c0201e0613_glv.mdl");
        Assert.Null(SizeSetRefit.OutfitLegsOf([glove, legs], glove));

        // Legs given only by our own refit are no piece of the mod's, so there are no outfit legs.
        string root = Directory.CreateTempSubdirectory("proteus-outfit-legs-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
                { "FileVersion": 4, "Name": "Boots", "DefaultData": { "Files": {
                    "{{Boot}}": "boot.mdl", "{{Legs}}": "{{BodyRetargetWriter.Subfolder}}/legs.mdl" }, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [] }
                """);
            var pieces = SizeSetRefit.PiecesOf(root, includeEnds: true);
            Assert.Null(SizeSetRefit.OutfitLegsOf(pieces, pieces.Single(p => p.Slot == "_sho")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BodyOption Body(string name) => new("Legs", "", name, name + ".mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-39")]
    [Trait("Plan", "EDGE-hands-feet-port-40")]
    [Trait("Plan", "EDGE-hands-feet-port-41")]
    public void Outfit_legs_reading_picks_exact_then_first() // [EDGE-hands-feet-port-39] [EDGE-hands-feet-port-40] [EDGE-hands-feet-port-41]
    {
        (string?, string)[] options = [("Small", "s"), ("Medium", "m"), ("Large", "l")];
        AutoRefitDecisions.Pick Pick(string body, Confidence c) => new(body, Body(body), c, false);
        var read = new List<string>();
        Func<string, AutoRefitDecisions.Pick?> Reader(Dictionary<string, AutoRefitDecisions.Pick?> picks)
            => file => { read.Add(file); return picks.GetValueOrDefault(file); };
        static bool Eligible(AutoRefitDecisions.Pick p) => p.Dir != "neolithe";

        // The first Exact: only it is read (EDGE-39).
        var got = SizeSetRefit.PickOutfitLegs(options, Reader(new() { ["s"] = Pick("bibo", Confidence.Exact),
                                                                      ["m"] = Pick("bibo", Confidence.Likely) }), Eligible);
        Assert.Equal("Small", got!.Value.Legs.Option);
        Assert.Equal(["s"], read);

        // A later Exact wins; with none, the first eligible (EDGE-40).
        read.Clear();
        got = SizeSetRefit.PickOutfitLegs(options, Reader(new() { ["m"] = Pick("bibo", Confidence.Likely),
                                                                  ["l"] = Pick("bibo", Confidence.Exact) }), Eligible);
        Assert.Equal("Large", got!.Value.Legs.Option);
        Assert.Equal(["s", "m", "l"], read);
        got = SizeSetRefit.PickOutfitLegs(options, Reader(new() { ["m"] = Pick("bibo", Confidence.Likely),
                                                                  ["l"] = Pick("bibo", Confidence.Likely) }), Eligible);
        Assert.Equal("Medium", got!.Value.Legs.Option);

        // A body no map joins to a man's is passed over; nothing eligible gives nothing (EDGE-41).
        got = SizeSetRefit.PickOutfitLegs(options, Reader(new() { ["s"] = Pick("neolithe", Confidence.Exact),
                                                                  ["m"] = Pick("bibo", Confidence.Likely) }), Eligible);
        Assert.Equal("Medium", got!.Value.Legs.Option);
        Assert.Null(SizeSetRefit.PickOutfitLegs(options, Reader(new() { ["s"] = Pick("neolithe", Confidence.Exact) }), Eligible));
        Assert.Null(SizeSetRefit.PickOutfitLegs(options, Reader([]), Eligible));

        // A reading too weak to make anything from is passed over: legs with no skin give no body (second-look review #1).
        foreach (var weak in new[] { Confidence.NoBodyMesh, Confidence.TooLittle, Confidence.Ambiguous })
        {
            Assert.False(AutoRefitDecisions.Proceed(weak));
            Assert.Null(SizeSetRefit.PickOutfitLegs(options, Reader(new() { ["s"] = Pick("bibo", weak), ["m"] = Pick("bibo", weak) }),
                                                    Eligible));
            got = SizeSetRefit.PickOutfitLegs(options, Reader(new() { ["s"] = Pick("bibo", weak),
                                                                      ["m"] = Pick("bibo", Confidence.Likely) }), Eligible);
            Assert.Equal("Medium", got!.Value.Legs.Option);
        }
    }

    [Fact]
    [Trait("Plan", "AC-hands-feet-port-12")]
    public void Check_in_game_names_the_outfit_legs() // [AC-hands-feet-port-12]
    {
        Assert.Equal([string.Format(Strings.SizeSet.EndFromOutfitLegsFmt, "Feet", "TBSE", "Bibo+ — Type C - Small", "Small")],
                     SizeSetRefit.EndWarnings("Feet", "TBSE", Choice.Outfit, null, "Bibo+", ("Bibo+ — Type C - Small", "Small")));
        Assert.Equal([string.Format(Strings.SizeSet.EndFromGameBodyFmt, "Feet", "TBSE")],
                     SizeSetRefit.EndWarnings("Feet", "TBSE", Choice.GameBody, null, null));
        Assert.Empty(SizeSetRefit.EndWarnings("Feet", "TBSE", Choice.Read, null, "Bibo+"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hands-feet-port-46")]
    public void Outfit_legs_reuse_the_runs_reading() // [EDGE-hands-feet-port-46]
    {
        var bodies = new List<BodyModIndex.Entry>
        {
            new("The Body SE", "TBSE", new BodySizeCatalog("tbse", [])),
            new("Bibo+", "Bibo+", new BodySizeCatalog("bibo", [Body("Type C - Small")])),
        };
        // Only "for men" keys ticked, where the table gives no preferred body: still the crossed legs' own key.
        var request = new SizeSetRefit.Request("nowhere", ["tbse@women"], false, false, bodies);
        Assert.Equal(SizeSetRefit.ReadingKey("leg size/small/x.mdl", "_dwn", "0201", "The Body SE"),
                     SizeSetRefit.OutfitLegsKey(request, "leg size/small/x.mdl"));

        // The crossed legs' own reading is kept under that very key: RunAcrossPiece prefers the first body mod given, and
        // its Sources read by ReadingKey(file, slot, race, preferred) — pinned so the two can't drift apart (review #16).
        string across = CrossSexRefitTests.Source("Proteus/Services/SizeSet/SizeSetRefit.CrossSex.cs");
        string runPiece = across[across.IndexOf("private static int RunAcrossPiece(", StringComparison.Ordinal)..];
        Assert.Contains("string preferred = mods[0].Dir;", runPiece[..runPiece.IndexOf("new Sources(", StringComparison.Ordinal)]);
        Assert.Contains("var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();", runPiece);
        Assert.Contains("ReadingKey(file, slot, race, preferred)",
                        CrossSexRefitTests.Source("Proteus/Services/SizeSet/SizeSetRefit.Source.cs"));

        // Read before in this run (by the crossed legs): handed back, never read again from a file that isn't there.
        var seeded = new AutoRefitDecisions.Pick("Bibo+", Body("Type C - Small"), Confidence.Exact, false);
        var readings = new Dictionary<string, AutoRefitDecisions.Pick?>(StringComparer.Ordinal)
        {
            [SizeSetRefit.OutfitLegsKey(request, "leg size/small/x.mdl")] = seeded,
        };
        var said = new List<string>();
        Assert.Same(seeded, SizeSetRefit.ReadOutfitLegs(request, "leg size/small/x.mdl", readings, said.Add));
        Assert.Contains(said, s => s.Contains("read before in this run", StringComparison.Ordinal));
    }
}
