using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Data = Proteus.Tests.SizeSetBatchData;
using SizeStatus = Proteus.Services.SizeSetRefit.SizeStatus;

namespace Proteus.Tests;

/// <summary>
/// The TexTools copies of the game's own models in real mods: not men's pieces when the author's women's design is
/// beside them, and the author's real men's pieces still are.
/// </summary>
public class SizeSetGameCopyLocalTests(ITestOutputHelper output)
{
    /// <summary>BOObies as TexTools upgraded it, its Midlander men's copy of the game's halter still given: the installed
    /// mod lost that redirect to a fix by hand.</summary>
    private const string BOObiesPack = LocalData.ModPacks + ":BOObies - by Solona (TexTools upgraded).pmp";

    private const string ThreePieceSuitsPack = LocalData.ModPacks + ":Three-Piece Suits - by Solona.pmp";
    private const string IvcsRacialPack = LocalData.ModPacks + ":[IVCS] Unique Racial Bodies Base.pmp";

    private const string HalterTop = "chara/equipment/e6013/model/c0201e6013_top.mdl";
    private const string HalterCopy = "chara/equipment/e6013/model/c0101e6013_top.mdl";

    private static IEnumerable<string> Names(IEnumerable<SizeSetRefit.Piece> pieces)
        => pieces.Select(p => Path.GetFileNameWithoutExtension(p.GamePath).ToLowerInvariant());

    /// <summary>A pack unpacked and, when v3, converted: what the size set reads.</summary>
    private static string Unpacked(string pack)
    {
        string copy = Data.UnpackedPack(pack);
        if (PenumbraModMeta.IsLegacyFolder(copy)) PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
        return copy;
    }

    private static string Installed(string folder)
    {
        string path = Path.Combine(LocalData.Path(LocalData.Mods), folder);
        Assert.True(Directory.Exists(path), $"{folder} is not installed");
        return path;
    }

    [LocalDataFact(BOObiesPack, LocalData.GameData, LocalData.Mods)]
    [Trait("Plan", "AC-old-game-copies-1")]
    public void BOObies_pieces_are_the_design_alone()   // [AC-old-game-copies-1]
    {
        string copy = Unpacked(BOObiesPack);
        try
        {
            Assert.Contains(PenumbraModMeta.ReadAllRedirects(copy),
                            r => string.Equals(r.GamePath, HalterCopy, StringComparison.OrdinalIgnoreCase));
            var game = Data.Game();
            var clock = Stopwatch.StartNew();
            var pieces = SizeSetRefit.PiecesOf(copy, game);
            output.WriteLine($"PiecesOf: {clock.ElapsedMilliseconds} ms");
            Assert.Equal(["c0201e6013_top"], Names(pieces));
            Assert.Equal(["c0201e6013_top"], Names(SizeSetRefit.AcrossPieces(pieces)));
        }
        finally
        {
            Data.Delete(copy);
        }
    }

    [LocalDataFact(BOObiesPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse, Data.TbseX,
                   LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-old-game-copies-1")]
    [Trait("Plan", "AC-old-game-copies-2")]
    [Trait("Speed", "Slow")]
    public void BOObies_men_get_the_design_and_keep_the_copy()   // [AC-old-game-copies-1] [AC-old-game-copies-2]
    {
        string copy = Unpacked(BOObiesPack);
        try
        {
            var before = Data.Shape(copy);
            var copyFile = PenumbraModMeta.ReadAllRedirects(copy)
                                          .Single(r => string.Equals(r.GamePath, HalterCopy, StringComparison.OrdinalIgnoreCase));
            byte[] copyBytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(copy, copyFile.File));
            var readGame = Data.Game();
            string mensGroup = SizeSetRefit.MensGroupName(copy, SizeSetRefit.AcrossPieces(SizeSetRefit.PiecesOf(copy, readGame)));

            // Every body and the men's: the CLI's --bodies all --men.
            var (outcome, log) = Data.Batch(copy, SizeSetBatch.ParseKeys("all", out _)!, men: true, ObliqueRun.Bodies,
                                            readGame: readGame, uvmaps: LocalData.Path(LocalData.TbseUvMaps));
            foreach (var s in log.Sizes)
                output.WriteLine($"{s.GamePath} [{s.Group} / {s.Option}] {s.Body} men={s.Men} {s.Status} {s.ReasonCode} from {s.From}");
            Assert.Null(outcome.RefusalCode);

            // The men's body: TBSE and TBSE-X made from the women's design, in the men's group.
            foreach (string body in new[] { "tbse", "tbse-x" })
                Assert.Contains(log.Sizes, s => s.Men && s.Body == body && s.Status == SizeStatus.Made && s.Group == mensGroup
                                                && string.Equals(s.GamePath, RacialModelBake.WithRace(HalterTop, CrossSexRefit.Men),
                                                                 StringComparison.OrdinalIgnoreCase));
            // None sized from the copy.
            Assert.DoesNotContain(log.Sizes, s => !s.Men && s.Body is "tbse" or "tbse-x");
            var after = Data.Shape(copy);
            Data.AssertOnlyAppended(before, after);
            var mens = after.Single(g => g.Group == mensGroup);
            Assert.Contains(BodyRetargetWriter.OriginalOption, mens.Options);
            Assert.All(after.Where(g => g.Group != mensGroup),
                       g => Assert.DoesNotContain(g.Options, o => o.StartsWith("TBSE", StringComparison.OrdinalIgnoreCase)));

            // The copy is given as it was, byte for byte.
            var still = PenumbraModMeta.ReadAllRedirects(copy)
                                       .Single(r => string.Equals(r.GamePath, HalterCopy, StringComparison.OrdinalIgnoreCase)
                                                    && r.Source == copyFile.Source);
            Assert.Equal(RefitCore.Normal(copyFile.File), RefitCore.Normal(still.File));
            Assert.Equal(copyBytes, File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(copy, still.File)));
        }
        finally
        {
            Data.Delete(copy);
        }
    }

    [LocalDataTheory(LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "AC-old-game-copies-5")]
    [Trait("Plan", "EDGE-old-game-copies-1")]
    [Trait("Plan", "EDGE-old-game-copies-2")]
    [Trait("Plan", "EDGE-old-game-copies-3")]
    [Trait("Plan", "EDGE-old-game-copies-13")]
    [InlineData("Iron Claws - by Solona", "c0101e0268_glv", "c0201e0268_glv")]   // a subset: the game added 20 triangles since
    [InlineData("socialite - by solona", "c0101e6066_top", "c0201e6066_top")]    // its meshes in the other order
    [InlineData("socialite - by solona", "c0101e6066_dwn", "c0201e6066_dwn")]
    [InlineData("Avaleine - by Solona", "c0101e0141_top", "c0201e0141_top")]     // 95.9%: just over the bar
    public void Older_game_copies_in_installed_mods_are_not_pieces(string mod, string copy, string design)
        // [AC-old-game-copies-5] [EDGE-old-game-copies-1] [EDGE-old-game-copies-2] [EDGE-old-game-copies-3] [EDGE-old-game-copies-13]
    {
        var pieces = SizeSetRefit.PiecesOf(Installed(mod), Data.Game());
        output.WriteLine(string.Join(" ", Names(pieces)));
        Assert.DoesNotContain(copy, Names(pieces));
        Assert.Contains(design, Names(SizeSetRefit.AcrossPieces(pieces)));
    }

    [LocalDataTheory(LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "AC-old-game-copies-3")]
    [Trait("Plan", "EDGE-old-game-copies-4")]
    [InlineData("-Twill Trousers- [YAB, TBSE, TBSE-X]", "c0101e6269_dwn c0101e6270_dwn c0101e6271_dwn c0201e6269_dwn c0201e6270_dwn c0201e6271_dwn")]
    [InlineData("Ruffles - by Solona", "c0101e6010_dwn c0101e6010_top c0201e6010_dwn c0201e6010_top")]
    [InlineData("the lumberjack - by solona", "c0101e6091_top")]
    public void Real_mens_pieces_stay_pieces(string mod, string expected)   // [AC-old-game-copies-3] [EDGE-old-game-copies-4]
    {
        var pieces = SizeSetRefit.PiecesOf(Installed(mod), Data.Game());
        Assert.Equal(expected.Split(' '), Names(pieces).Order());
        Assert.Contains("tbse", SizeSetRefit.Offered(pieces).Select(c => c.Key));
    }

    [LocalDataFact(ThreePieceSuitsPack, IvcsRacialPack, LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "AC-old-game-copies-3")]
    [Trait("Plan", "EDGE-old-game-copies-8")]
    public void Real_mens_pieces_in_packs_stay_pieces()   // [AC-old-game-copies-3] [EDGE-old-game-copies-8]
    {
        var game = Data.Game();
        // A men's design made on the game's body; a body mod's per-race hand fits, 58.6% the game's model.
        foreach (var (pack, mens) in new[] { (ThreePieceSuitsPack, new[] { "c0101e0234_top", "c0101e0200_dwn" }),
                                             (IvcsRacialPack, new[] { "c0101e9903_glv", "c0101e0279_glv" }) })
        {
            string copy = Unpacked(pack);
            try
            {
                var pieces = SizeSetRefit.PiecesOf(copy, game);
                output.WriteLine($"{pack}: {string.Join(" ", Names(pieces))}");
                foreach (string piece in mens) Assert.Contains(piece, Names(pieces));
                Assert.Contains("tbse", SizeSetRefit.Offered(pieces).Select(c => c.Key));
            }
            finally
            {
                Data.Delete(copy);
            }
        }
    }

    [LocalDataTheory(LocalData.Mods, LocalData.GameData)]
    [Trait("Plan", "EDGE-old-game-copies-19")]
    [Trait("Plan", "EDGE-old-game-copies-3")]
    [InlineData("Calithea - by Solona", "c0101e0141_glv")]           // 76.3%
    [InlineData("black market - by solona", "c0101e9071_top")]       // 67.6%
    [InlineData("Sunfairy (Bibo+) - by Solona", "c0101e0141_top")]   // 94.5%: just under the bar
    public void Older_copies_below_the_bar_stay_pieces(string mod, string piece)   // [EDGE-old-game-copies-19] [EDGE-old-game-copies-3]
        => Assert.Contains(piece, Names(SizeSetRefit.PiecesOf(Installed(mod), Data.Game())));
}
