using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Services.SizeSetRefit;

namespace Proteus.Tests;

/// <summary>
/// #43 on real mods: a women's piece is made men's when every file of the mod's own men's model of it can't be read as a
/// model at all, and only then (plan 42-43-tbsex-legs-cross).
/// </summary>
[Trait("Speed", "Slow")]
public sealed class AuthorsMensFallbackLocalTests(ITestOutputHelper o) : IDisposable
{
    internal const string LaVieEnRose = LocalData.Mods + ":La Vie en Rose - by Solona";
    private const string Stormblessed = SizeSetCrossSexLocalTests.Stormblessed;
    private const string MensGlove = "chara/equipment/e0633/model/c0101e0633_glv.mdl";
    private const string WomensGlove = "chara/equipment/e0633/model/c0201e0633_glv.mdl";
    private const string TbseHands = "common/3/c0101e0000_glv.mdl";

    private readonly List<string> copies = [];

    public void Dispose()
    {
        foreach (string root in copies)
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
    }

    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    private static byte[]? Game(string path)
    {
        try { return GameData.Value.GetFile(path)?.Data; }
        catch (Exception) { return null; }
    }

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    private string Copy(string need)
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-authorsmens-" + Guid.NewGuid().ToString("N"));
        copies.Add(copy);
        SizeSetRefitTests.CopyModels(LocalData.Path(need), copy);
        return copy;
    }

    /// <summary>The file the mod's manifest gives <paramref name="gamePath"/> (one only), on disk.</summary>
    private static string FileAt(string root, string gamePath)
    {
        var at = PenumbraModMeta.ReadAllRedirects(root)
                                .Where(r => string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                                .Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return BodySizeCatalog.ResolveCaseInsensitive(root, Assert.Single(at));
    }

    private RunLog Run(string root, string[] asked, IReadOnlyList<BodyModIndex.Entry> bodies)
    {
        var pieces = PiecesOf(root, Game, includeEnds: true);
        var keys = SizeSetBatch.RunKeys(pieces, asked, men: false);
        var gameBodies = GameBodies(keys, Game, ends: true);
        var runLog = new RunLog();
        SizeSetRefit.Run(new Request(root, keys, false, false, bodies, Game, Ends: true, race => gameBodies.GetValueOrDefault(race),
                                     Across: new AcrossInputs(_ => null)),
                         runLog, Maps(), Substitute.For<IPluginLog>(), _ => { }, default);
        foreach (var n in runLog.Notes) o.WriteLine("note: " + n);
        foreach (var w in runLog.Warnings) o.WriteLine("check: " + w);
        return runLog;
    }

    private static List<(string Name, string Json)> Groups(string root)
        => (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => (g.Name, JsonNode.Parse(g.Group.GetRawText())!.ToJsonString()))
                                                     .ToList();

    /// <summary>
    /// Stormblessed with its men's glove's file emptied (zero bytes): the author's men's glove is now a piece — a copy of
    /// the game's no longer — that can't be read as a model. Its women's glove is made men's in the men's body group, and
    /// said; again with Remake off, it is already there; once the men's glove is a model again (TBSE's own hands), the
    /// earlier men's version is kept and said to still stand over it.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Stormblessed, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse,
                   LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-1")]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-2")]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-3")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-9")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-10")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-13")]
    public void Stormblessed_unreadable_mens_glove_is_crossed() // [AC-42-43-tbsex-legs-cross-1] [AC-42-43-tbsex-legs-cross-2] [AC-42-43-tbsex-legs-cross-3] [EDGE-42-43-tbsex-legs-cross-9] [EDGE-42-43-tbsex-legs-cross-10] [EDGE-42-43-tbsex-legs-cross-13]
    {
        string root = Copy(Stormblessed);
        string mensFile = FileAt(root, MensGlove);
        File.WriteAllBytes(mensFile, []);
        string men = Strings.SizeSet.MensGroup;
        var bodies = SizeSetCrossSexLocalTests.Installed(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse);

        // The setup is what it says: a piece now, keeping the women's glove from being crossed as 1410 would.
        var pieces = PiecesOf(root, Game, includeEnds: true);
        Assert.Contains(pieces, p => p.GamePath == MensGlove && p.Race == "0101" && p.Slot == "_glv");
        Assert.DoesNotContain(AcrossPieces(pieces), p => p.GamePath == WomensGlove);
        Assert.Contains("tbse@women", SizeSetBatch.RunKeys(pieces, ["tbse", "tbse@women"], men: false));
        Assert.True(AuthorMensRedirectsUnparsable(root, MensGlove, BodyRetargetWriter.ReadRecord(root)));
        var authors = Groups(root).Where(g => g.Name != men).ToList();

        var run = Run(root, ["tbse", "tbse@women"], bodies);
        var mensOwn = run.Sizes.Where(s => s.GamePath == MensGlove && !s.Men).ToList();
        Assert.Contains(mensOwn, s => s.ReasonCode == Codes.EndsUnreadable);
        Assert.False(MensModelUsable(mensOwn));
        var women = pieces.Single(p => p.GamePath == WomensGlove);
        var crossed = Assert.Single(run.Sizes, s => s.Men && s.Slot == "_glv" && s.Option == "TBSE" && s.GamePath == MensGlove);
        Assert.True(crossed.Status == SizeStatus.Made, crossed.Reason);
        Assert.DoesNotContain(run.Sizes, s => s.ReasonCode == Codes.AuthorsMens && s.GamePath == MensGlove);
        Assert.Single(run.Warnings, w => w == string.Format(Strings.SizeSet.AuthorsMensCrossedFmt, women.Label, men));
        Assert.DoesNotContain(run.Warnings, w => w.StartsWith(women.Label, StringComparison.Ordinal)
                                                 && w.Contains("earlier run", StringComparison.Ordinal));
        var option = PenumbraModMeta.TryReadFileOptions(root, men)!.First(x => x.Name == "TBSE");
        Assert.Contains(option.Files.Keys, k => string.Equals(k, MensGlove, StringComparison.OrdinalIgnoreCase));

        // [EDGE-13] The author's groups are as they were; ours is last, and keeps Original as its default.
        Assert.Equal(authors, Groups(root).Where(g => g.Name != men).ToList());
        Assert.Equal(men, Groups(root)[^1].Name);
        var group = JsonNode.Parse(Groups(root)[^1].Json)!;
        Assert.Equal(BodyRetargetWriter.OriginalOption, (string?)group["Options"]![(int)group["DefaultSettings"]!]!["Name"]);

        // [EDGE-9] Again, Remake off: already there; nothing more claims the path.
        var files = PenumbraModMeta.TryReadFileOptions(root, men)!.Select(x => (x.Name, string.Join("|", x.Files.OrderBy(f => f.Key))))
                                   .ToList();
        var again = Run(root, ["tbse", "tbse@women"], bodies);
        Assert.Equal(Codes.AlreadyThere,
                     Assert.Single(again.Sizes, s => s.Men && s.Slot == "_glv" && s.Option == "TBSE" && s.GamePath == MensGlove).ReasonCode);
        Assert.Equal(files, PenumbraModMeta.TryReadFileOptions(root, men)!.Select(x => (x.Name, string.Join("|", x.Files.OrderBy(f => f.Key))))
                                           .ToList());
        Assert.DoesNotContain(again.Notes, n => n == string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label));

        // [AC-3] [EDGE-10] The author's men's glove a model again: it is sized as it is, and the men's version made from the
        // women's glove is kept — and said to stand over it, never "no men's version was made".
        File.Copy(Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Tbse), TbseHands), mensFile, overwrite: true);
        Assert.False(AuthorMensRedirectsUnparsable(root, MensGlove, BodyRetargetWriter.ReadRecord(root)));
        var readable = Run(root, ["tbse", "tbse@women"], bodies);
        o.WriteLine("men's glove now: " + string.Join("; ", readable.Sizes.Where(s => s.GamePath == MensGlove && !s.Men)
                                                                          .Select(s => $"{s.Status} {s.ReasonCode} {s.Option}")));
        Assert.True(MensModelUsable(readable.Sizes.Where(s => s.GamePath == MensGlove && !s.Men)));
        Assert.Single(readable.Warnings, w => w == string.Format(Strings.SizeSet.AuthorsMensStillReplacedFmt, women.Label, men));
        Assert.DoesNotContain(readable.Notes, n => n == string.Format(Strings.SizeSet.AuthorsMensFmt, women.Label));
        Assert.Contains(PenumbraModMeta.TryReadFileOptions(root, men)!.First(x => x.Name == "TBSE").Files.Keys,
                        k => string.Equals(k, MensGlove, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The issue's own case: La Vie en Rose's men's glove couldn't be placed on a body (build 1400's "unreadable"), but it
    /// is a model the game draws — so it is kept, and its women's glove is not made men's.
    /// </summary>
    [LocalDataFact(LocalData.GameData, LaVieEnRose)]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-1")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-17")]
    public void La_vie_en_rose_glove_repro() // [AC-42-43-tbsex-legs-cross-1] [EDGE-42-43-tbsex-legs-cross-17]
    {
        string root = LocalData.Path(LaVieEnRose);
        const string mens = "chara/equipment/e6097/model/c0101e6097_glv.mdl";
        var pieces = PiecesOf(root, Game, includeEnds: true);
        o.WriteLine("pieces: " + string.Join(", ", pieces.Select(p => p.GamePath)));
        Assert.False(AuthorMensRedirectsUnparsable(root, mens, BodyRetargetWriter.ReadRecord(root)));
        var log = new RunLog();
        if (pieces.FirstOrDefault(p => p.GamePath == mens) is { } piece)
            log.Add(piece, SizeStatus.Skipped, Codes.EndsUnreadable, "as build 1400 read it");
        Assert.DoesNotContain(mens, MensModelsUnparsable(pieces, log, root, null));
    }

    /// <summary>
    /// Every installed mod's men's models that have a women's piece beside them: how many the fallback would replace
    /// (every file of the author's at that path missing, zero bytes or unparsable), against those it keeps. A report for
    /// the #43 closing comment; each candidate re-checked file by file.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-1")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-29")]
    public void Fallback_candidates_census() // [AC-42-43-tbsex-legs-cross-1] [EDGE-42-43-tbsex-legs-cross-29]
    {
        int mods = 0, seen = 0;
        var candidates = new List<string>();
        foreach (string root in Directory.EnumerateDirectories(LocalData.Path(LocalData.Mods)))
        {
            if (!File.Exists(Path.Combine(root, PenumbraModMeta.MetaFile))) continue;
            List<Piece> pieces;
            try { pieces = PiecesOf(root, Game, includeEnds: true); }
            catch (Exception e) when (e is not OutOfMemoryException) { continue; }
            mods++;
            var paths = pieces.Select(p => p.GamePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var record = BodyRetargetWriter.ReadRecord(root);
            foreach (var women in pieces.Where(Crossable))
            {
                string mens = MensPathOf(women);
                if (!paths.Contains(mens)) continue;
                seen++;
                if (!AuthorMensRedirectsUnparsable(root, mens, record)) continue;
                candidates.Add($"{Path.GetFileName(root)}: {mens}");
                var ours = OurFiles(record);
                foreach (var r in PenumbraModMeta.ReadAllRedirects(root)
                                                 .Where(r => string.Equals(r.GamePath, mens, StringComparison.OrdinalIgnoreCase)
                                                             && !IsOurFile(ours, r.File)))
                {
                    string path = BodySizeCatalog.ResolveCaseInsensitive(root, r.File);
                    bool bad = !File.Exists(path) || new FileInfo(path).Length == 0;
                    if (!bad)
                        try { SecondSkinWriter.Parse(File.ReadAllBytes(path)); }
                        catch (Exception) { bad = true; }
                    Assert.True(bad, $"{root}: {r.File} parses, but was counted as a fallback");
                }
            }
        }
        o.WriteLine($"{mods} mods; {seen} men's models beside a women's piece; {candidates.Count} the fallback would replace, "
                    + $"{seen - candidates.Count} kept");
        foreach (string c in candidates) o.WriteLine("  " + c);
        Assert.True(seen > 0, "no men's model beside a women's piece in the library");
    }
}
