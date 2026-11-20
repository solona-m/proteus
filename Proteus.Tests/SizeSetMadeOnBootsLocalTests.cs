extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Codes = Proteus.Services.SizeSetRefit.Codes;
using Data = Proteus.Tests.SizeSetBatchData;
using SizeStatus = Proteus.Services.SizeSetRefit.SizeStatus;

namespace Proteus.Tests;

/// <summary>
/// Oops I Did It Again as it ships, unpacked, sized with "Made on" = Bibo+ for every women's body, then once more; and a
/// second copy sized without "Made on". Its Wild Rose boots ("Boot Size" Small/Medium/Large) have no skin at all: before
/// this, no size of them was made (<c>SizeSetRefit.MadeOnEnds.cs</c>).
/// </summary>
public sealed class OopsBootsRun : IDisposable
{
    internal static readonly string[] Bodies = [Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline];

    internal static readonly string[] Needs = [Data.OopsPack, .. Bodies, LocalData.GameData, LocalData.UvMaps];

    internal string Copy { get; } = "";
    internal string Plain { get; } = "";
    internal List<(string Group, List<string> Options, int? Default)> Before { get; } = [];
    internal SizeSetRefit.RunLog First { get; } = new();
    internal SizeSetRefit.RunLog Second { get; } = new();
    internal SizeSetRefit.RunLog Without { get; } = new();
    internal string Log { get; } = "";

    public OopsBootsRun()
    {
        if (LocalData.Missing(Needs) != null) return;
        var readGame = Data.Game();
        Copy = Data.UnpackedPack(Data.OopsPack);
        PenumbraModMeta.MigrateWorkCopy(Copy, LocalData.Path(LocalData.Mods));
        Before = Data.Shape(Copy);
        var said = new StringWriter();
        First = Data.Batch(Copy, SizeSetBatch.WomensKeys, men: false, Bodies, readGame: readGame, madeOn: Data.Folder(Data.Bibo),
                           log: RefitCli::Proteus.Refit.ConsoleLog.To(said)).Log;
        Second = Data.Batch(Copy, SizeSetBatch.WomensKeys, men: false, Bodies, readGame: readGame,
                            madeOn: Data.Folder(Data.Bibo)).Log;
        Log = said.ToString();

        Plain = Data.UnpackedPack(Data.OopsPack);
        PenumbraModMeta.MigrateWorkCopy(Plain, LocalData.Path(LocalData.Mods));
        Without = Data.Batch(Plain, ["bibo", "rue"], men: false, [Data.Bibo, Data.Rue], readGame: readGame).Log;
    }

    public void Dispose()
    {
        Data.Delete(Copy);
        Data.Delete(Plain);
    }
}

[Trait("Speed", "Slow")]
[Trait("Suite", "Diagnostic")]
public class SizeSetMadeOnBootsLocalTests(ITestOutputHelper output, OopsBootsRun oops) : IClassFixture<OopsBootsRun>
{
    private const string Boots = "chara/equipment/e0284/model/c0201e0284_sho.mdl";
    private const string Group = "Boot Size";
    private static readonly string[] AuthorSizes = ["Small", "Medium", "Large"];

    /// <summary>Why a body's size of the boots may be left out, said: a refusal of the pair, or nothing to go onto.</summary>
    private static readonly string[] AllowedLeftOut = [Codes.RefusedPair, Codes.NoSuchSize, Codes.NoOwnLegs, Codes.NotInstalled];

    private static bool IsBoot(SizeSetRefit.SizeOutcome s) => !s.Men && s.GamePath.Equals(Boots, StringComparison.OrdinalIgnoreCase);

    private List<SizeSetRefit.SizeOutcome> BootsOf(SizeSetRefit.RunLog log)
    {
        var boots = log.Sizes.Where(IsBoot).ToList();
        foreach (var s in boots)
            output.WriteLine($"{s.Body} {s.Option} {s.Status} {s.ReasonCode} cut from {s.CutFrom}"
                             + (s.Measures is { } m ? $" folded {m.Folded} move {m.WorstMoveMm:F1}" : "")
                             + (s.Reason is { } r ? $" — {r}" : ""));
        return boots;
    }

    private string Label => oops.First.Sizes.First(IsBoot).Piece;

    /// <summary>How the log says each of the author's sizes was placed on Bibo+: "cloth", "name", or null.</summary>
    private string? PlacedBy(string option)
    {
        var line = oops.Log.Split('\n').FirstOrDefault(l => l.Contains($"{Label}: {option}: no skin;", StringComparison.Ordinal));
        return line == null ? null : line.Contains("by its shaft's cloth") ? "cloth" : line.Contains("by its name") ? "name" : "none";
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, LocalData.GameData,
                   LocalData.UvMaps)]
    [Trait("Plan", "AC-made-on-skinless-boots-1")]
    [Trait("Plan", "AC-made-on-skinless-boots-9")]
    public void Oops_boots_made_on_Bibo_gain_the_other_bodies_in_Boot_Size() // [AC-made-on-skinless-boots-1] [AC-made-on-skinless-boots-9]
    {
        var boots = BootsOf(oops.First);
        foreach (string key in SizeSetBatch.WomensKeys.Where(k => k != "bibo"))
        {
            if (SupportedBodies.Of(key, "_dwn", false) is null) continue;
            var theirs = boots.Where(s => s.Body == key).ToList();
            if (theirs.All(s => s.ReasonCode == Codes.NotInstalled)) continue;   // a body this machine lacks
            foreach (var s in theirs)
            {
                Assert.Equal(Group, s.Group);
                if (s.Status == SizeStatus.Made)
                    Assert.Contains(s.CutFrom, AuthorSizes);
                else
                    Assert.True(AllowedLeftOut.Contains(s.ReasonCode) && !string.IsNullOrEmpty(s.Reason),
                                $"{s.Body} {s.Option}: {s.ReasonCode} — {s.Reason}");
            }
        }
        foreach (var s in boots.Where(s => s.Status != SizeStatus.Made && s.Body != "bibo"))
            output.WriteLine($"left out: {s.Body} {s.Option}: {s.ReasonCode} — {s.Reason}");
        var made = boots.Where(s => s.Status == SizeStatus.Made && s.Body != "bibo").ToList();
        Assert.NotEmpty(made);
        Assert.DoesNotContain(boots, s => s.ReasonCode is Codes.Unreadable or Codes.NotMadeOn or Codes.EndsUnreadable);

        var after = Data.Shape(oops.Copy);
        Assert.Equal(oops.Before.Count, after.Count);
        var group = after.Single(g => g.Group == Group);
        Assert.All(made, s => Assert.Contains(s.Option!, group.Options));
        Data.AssertOnlyAppended(oops.Before, after);
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, LocalData.GameData,
                   LocalData.UvMaps)]
    [Trait("Plan", "AC-made-on-skinless-boots-2")]
    [Trait("Plan", "AC-made-on-skinless-boots-3")]
    public void Oops_boot_log_says_how_each_size_was_read() // [AC-made-on-skinless-boots-2] [AC-made-on-skinless-boots-3]
    {
        var placed = AuthorSizes.ToDictionary(o => o, PlacedBy);
        foreach (var (option, how) in placed) output.WriteLine($"{option}: {how ?? "(no line)"}");
        Assert.All(placed.Values, how => Assert.True(how is "cloth" or "name", how));
        if (oops.Log.Contains("every size taken by its name")) Assert.All(placed.Values, how => Assert.Equal("name", how));
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, LocalData.GameData,
                   LocalData.UvMaps)]
    [Trait("Plan", "AC-made-on-skinless-boots-5")]
    [Trait("Plan", "AC-made-on-skinless-boots-9")]
    public void Oops_adds_no_Bibo_option_to_Boot_Size() // [AC-made-on-skinless-boots-5] [AC-made-on-skinless-boots-9]
    {
        var boots = BootsOf(oops.First);
        var before = oops.Before.Single(g => g.Group == Group).Options;
        var added = Data.Shape(oops.Copy).Single(g => g.Group == Group).Options.Except(before).ToList();
        output.WriteLine("added: " + string.Join(", ", added));
        Assert.DoesNotContain(added, o => o.StartsWith("Bibo", StringComparison.OrdinalIgnoreCase));
        var bibo = boots.Where(s => s.Body == "bibo").ToList();
        Assert.NotEmpty(bibo);
        Assert.All(bibo, s => Assert.True(s.ReasonCode == Codes.MadeOnIt
                                          || (s.ReasonCode == Codes.AuthorsOwn && AuthorSizes.Any(a => s.Reason!.Contains($"\"{a}\""))),
                                          $"{s.Option}: {s.ReasonCode} — {s.Reason}"));
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, LocalData.GameData,
                   LocalData.UvMaps)]
    [Trait("Plan", "AC-made-on-skinless-boots-3")]
    [Trait("Plan", "AC-made-on-skinless-boots-4")]
    public void Oops_check_in_game_matches_how_the_boots_were_read() // [AC-made-on-skinless-boots-3] [AC-made-on-skinless-boots-4]
    {
        var made = BootsOf(oops.First).Where(s => s.Status == SizeStatus.Made).ToList();
        Assert.NotEmpty(made);
        var warnings = oops.First.Warnings.Where(w => w.StartsWith(Label, StringComparison.Ordinal)).ToList();
        foreach (string w in warnings) output.WriteLine(w);
        Assert.Equal(warnings.Count, warnings.Distinct().Count());
        string trusted = string.Format(Strings.SizeSet.MadeOnTrustedFmt, Label, "Bibo+");
        bool byName = made.Select(s => s.CutFrom!).Distinct().Any(o => PlacedBy(o) == "name");
        Assert.Equal(byName ? 1 : 0, warnings.Count(w => w == trusted));
        // A cloth-gap line only ever names an author size the sizes were cut from.
        var cutFrom = made.Select(s => s.CutFrom!).ToHashSet();
        foreach (string w in warnings.Where(w => w != trusted))
            Assert.Contains(cutFrom, o => w.StartsWith($"{Label}, \"{o}\":", StringComparison.Ordinal));
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, LocalData.GameData,
                   LocalData.UvMaps)]
    [Trait("Plan", "AC-made-on-skinless-boots-10")]
    public void Oops_ankle_join_stays_within_limits_on_every_body() // [AC-made-on-skinless-boots-10]
    {
        // First measured (build 1489, Made on Bibo+, all three sizes placed by name, cut from Small): at most 1.1 mm over
        // 244 edges, 0 folded, on every body. The limits are never raised: a size over them is a finding.
        var made = BootsOf(oops.First).Where(s => s.Status == SizeStatus.Made).ToList();
        Assert.NotEmpty(made);
        int triangles = ModelPartReader.Read(AuthorModel(made[0].CutFrom!))!.Parts.Sum(p => p.TriangleCount);
        foreach (var s in made)
        {
            var lines = Regex.Matches(oops.Log, Regex.Escape($"{Label} {s.Option}: ankle join stretched up to ")
                                                + @"([\d.]+) mm over (\d+) edges, (\d+) triangles folded");
            Assert.True(lines.Count == 1, $"{s.Option}: {lines.Count} ankle join lines");
            float mm = float.Parse(lines[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            int edges = int.Parse(lines[0].Groups[2].Value), folded = int.Parse(lines[0].Groups[3].Value);
            output.WriteLine($"{s.Option}: {mm:F1} mm over {edges} edges, {folded} of {triangles} folded");
            Assert.True(edges > 0, $"{s.Option}: no join measured");
            Assert.True(mm <= 2 * KeepEnds.SeamWarn * 1000f, $"{s.Option}: the ankle join stretched {mm} mm");
            Assert.True(folded <= triangles / 100, $"{s.Option}: {folded} folded of {triangles}");
            Assert.Equal(s.Measures!.Folded, folded);
        }
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, LocalData.GameData,
                   LocalData.UvMaps)]
    [Trait("Plan", "EDGE-made-on-skinless-boots-19")]
    [Trait("Plan", "AC-made-on-skinless-boots-1")]
    public void Oops_boots_keep_the_authors_foot_and_gain_no_skin() // [EDGE-made-on-skinless-boots-19] [AC-made-on-skinless-boots-1]
    {
        var made = BootsOf(oops.First).Where(s => s.Status == SizeStatus.Made).ToList();
        Assert.NotEmpty(made);
        foreach (var s in made)
        {
            byte[] author = AuthorModel(s.CutFrom!), saved = SavedModel(s.Option!);
            var authorParts = ModelPartReader.Read(author)!;
            var foot = KeepEnds.Read("_sho", authorParts, author)!.Ends;
            Assert.NotEmpty(foot);
            var savedParts = ModelPartReader.Read(saved);
            Assert.NotNull(savedParts);
            var at = Enumerable.Range(0, savedParts!.Positions.Length / 3)
                               .Select(v => (savedParts.Positions[v * 3], savedParts.Positions[v * 3 + 1], savedParts.Positions[v * 3 + 2]))
                               .ToHashSet();
            int moved = foot.Count(v => !at.Contains((authorParts.Positions[v * 3], authorParts.Positions[v * 3 + 1],
                                                       authorParts.Positions[v * 3 + 2])));
            output.WriteLine($"{s.Option}: {moved} of {foot.Count} foot vertices not where the author put them");
            Assert.Equal(0, moved);
            Assert.Empty(BodySizeMatch.SkinPoints(savedParts));
            Assert.True(s.Measures!.WorstMoveMm is { } move && float.IsFinite(move) && move < 100f, $"{s.Option}: moved {s.Measures.WorstMoveMm}");
        }
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, LocalData.GameData,
                   LocalData.UvMaps)]
    [Trait("Plan", "EDGE-made-on-skinless-boots-18")]
    public void Second_Oops_run_adds_no_boots() // [EDGE-made-on-skinless-boots-18]
    {
        var boots = BootsOf(oops.Second);
        Assert.NotEmpty(boots);
        Assert.DoesNotContain(boots, s => s.Status == SizeStatus.Made);
        Assert.All(boots, s => Assert.True(s.ReasonCode is Codes.AlreadyThere or Codes.AuthorsOwn or Codes.MadeOnIt
                                           || AllowedLeftOut.Contains(s.ReasonCode), $"{s.Option}: {s.ReasonCode}"));
    }

    [LocalDataFact(Data.OopsPack, Data.Bibo, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-made-on-skinless-boots-20")]
    [Trait("Plan", "AC-made-on-skinless-boots-7")]
    public void Oops_without_made_on_still_leaves_the_boots_out() // [EDGE-made-on-skinless-boots-20] [AC-made-on-skinless-boots-7]
    {
        var boots = BootsOf(oops.Without);
        var boot = Assert.Single(boots);
        Assert.Equal(Codes.Unreadable, boot.ReasonCode);
        Assert.Equal(string.Format(Strings.SizeSet.UnreadableFmt, boot.Piece), boot.Reason);
        Assert.Equal(AuthorSizes, Data.Shape(oops.Plain).Single(g => g.Group == Group).Options);
    }

    private byte[] AuthorModel(string option) => ModelOf(oops.Copy, option);

    private byte[] SavedModel(string option) => ModelOf(oops.Copy, option);

    private static byte[] ModelOf(string root, string option)
    {
        var o = PenumbraModMeta.TryReadFileOptions(root, Group)!.Single(x => x.Name == option);
        string file = o.Files.First(f => string.Equals(f.Key, Boots, StringComparison.OrdinalIgnoreCase)).Value;
        return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, file));
    }
}
