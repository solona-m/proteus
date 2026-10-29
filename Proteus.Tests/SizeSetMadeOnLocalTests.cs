extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Data = Proteus.Tests.SizeSetBatchData;
using SizeStatus = Proteus.Services.SizeSetRefit.SizeStatus;

namespace Proteus.Tests;

/// <summary>
/// Oblique as it shipped, unpacked and converted, sized once with "Made on" = Bibo+ for every body and the men's, and
/// then once more: the runs the tests below read. Oblique was made on Bibo+ before Dawntrail, so the automatic reading
/// places none of its sizes.
/// </summary>
public sealed class ObliqueRun : IDisposable
{
    internal static readonly string[] Bodies =
        [Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse, Data.TbseX];

    internal static readonly string[] Needs =
        [Data.ObliquePack, .. Bodies, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps];

    internal string Copy { get; } = "";
    internal List<(string Group, List<string> Options, int? Default)> Before { get; } = [];
    internal List<SizeSetRefit.Piece> Pieces { get; } = [];
    internal SizeSetRefit.RunLog First { get; } = new();
    internal SizeSetRefit.RunLog Second { get; } = new();
    internal byte[] ManifestAfterFirst { get; } = [];
    internal byte[] ManifestAfterSecond { get; } = [];
    internal string Log { get; } = "";

    public ObliqueRun()
    {
        if (LocalData.Missing(Needs) != null) return;
        Copy = Data.UnpackedPack(Data.ObliquePack);
        PenumbraModMeta.MigrateWorkCopy(Copy, LocalData.Path(LocalData.Mods));
        Before = Data.Shape(Copy);
        var readGame = Data.Game();
        Pieces = SizeSetRefit.PiecesOf(Copy, readGame);
        var said = new StringWriter();
        var log = RefitCli::Proteus.Refit.ConsoleLog.To(said);
        First = Data.Batch(Copy, SizeSetBatch.WomensKeys, men: true, Bodies, readGame: readGame,
                           uvmaps: LocalData.Path(LocalData.TbseUvMaps), madeOn: Data.Folder(Data.Bibo), log: log).Log;
        ManifestAfterFirst = File.ReadAllBytes(Path.Combine(Copy, PenumbraModMeta.MetaFile));
        Second = Data.Batch(Copy, SizeSetBatch.WomensKeys, men: true, Bodies, readGame: readGame,
                            uvmaps: LocalData.Path(LocalData.TbseUvMaps), madeOn: Data.Folder(Data.Bibo)).Log;
        ManifestAfterSecond = File.ReadAllBytes(Path.Combine(Copy, PenumbraModMeta.MetaFile));
        Log = said.ToString();
    }

    public void Dispose() => Data.Delete(Copy);
}

public class SizeSetMadeOnLocalTests(ITestOutputHelper output, ObliqueRun oblique) : IClassFixture<ObliqueRun>
{
    private const string ObliqueTop = "chara/equipment/e0485/model/c0201e0485_top.mdl";
    private const string ObliqueLegs = "chara/equipment/e0483/model/c0201e0483_dwn.mdl";

    private static readonly string[] WomensKeys = ["neolithe", "rue", "yab", "yab-buff", "tre", "rosaline"];

    /// <summary>The pieces the size set takes from Oblique: its two Midlander women's ones, never the game's own models
    /// and other races' its Items group ships.</summary>
    private static void AssertObliquePieces(IReadOnlyList<SizeSetRefit.Piece> pieces)
        => Assert.Equal([ObliqueLegs, ObliqueTop], pieces.Select(p => p.GamePath.ToLowerInvariant()).OrderBy(p => p));

    private void Print(SizeSetRefit.RunLog log)
    {
        foreach (var s in log.Sizes)
            output.WriteLine($"{s.Slot} {(s.Men ? "men " : "")}{s.Option} {s.Status} {s.ReasonCode} cut from {s.CutFrom}"
                             + (s.Measures is { } m ? $" folded {m.Folded} push {m.WorstPushMm:F1}" : ""));
    }

    // ── Oblique, made on Bibo+ ───────────────────────────────────────────────────────────────────────────────────

    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-made-on-1")]
    [Trait("Plan", "EDGE-size-set-made-on-9")]
    [Trait("Plan", "EDGE-size-set-made-on-16")]
    [Trait("Plan", "EDGE-size-set-made-on-17")]
    [Trait("Speed", "Slow")]
    public void Oblique_made_on_Bibo_gains_every_other_body() // [AC-size-set-made-on-1] [EDGE-size-set-made-on-9] [EDGE-size-set-made-on-16] [EDGE-size-set-made-on-17]
    {
        Print(oblique.First);
        AssertObliquePieces(oblique.Pieces);
        bool Made(string gamePath, string key, bool men = false)
            => oblique.First.Sizes.Any(s => s.Status == SizeStatus.Made && s.Men == men && s.Body == key
                                            && s.GamePath.Equals(men ? RacialModelBake.WithRace(gamePath, CrossSexRefit.Men) : gamePath,
                                                                 StringComparison.OrdinalIgnoreCase));
        foreach (string key in WomensKeys) Assert.True(Made(ObliqueTop, key), $"no {key} top made");
        foreach (string key in new[] { "tbse", "tbse-x" }) Assert.True(Made(ObliqueTop, key, men: true), $"no {key} top for men");
        // Legs: every body whose table has legs (YAB Buff has none).
        foreach (string key in WomensKeys.Where(k => SupportedBodies.Of(k, "_dwn", false) != null))
            Assert.True(Made(ObliqueLegs, key), $"no {key} legs made");
        // The men's legs: made, or refused by the refit across the sexes with its reason — never left unsaid. Oblique's
        // are refused (the body maps match 88% of TBSE's legs, and TBSE-X's lay out apart).
        foreach (string key in new[] { "tbse", "tbse-x" })
        {
            string mens = RacialModelBake.WithRace(ObliqueLegs, CrossSexRefit.Men);
            var legs = oblique.First.Sizes.Where(s => s.Men && s.Body == key
                                                      && s.GamePath.Equals(mens, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(legs.Count > 0, $"no {key} legs outcome for men");
            // Refused only by those two gates — the body maps' coverage, or the legs' texture layout — never otherwise.
            string coverage = Strings.Parts.RetargetRefuseCrossSexCoverageFmt.Split('{')[0];
            Assert.All(legs, s => Assert.True(s.Status == SizeStatus.Made
                                              || (s.ReasonCode == SizeSetRefit.Codes.RefusedMen && s.Reason is { } why
                                                  && (why.Contains(coverage, StringComparison.Ordinal)
                                                      || why.Contains("by texture coordinate", StringComparison.Ordinal))),
                                              $"{s.Status} {s.ReasonCode}: {s.Reason}"));
            // And said under Left out.
            Assert.All(legs.Where(s => s.Status == SizeStatus.Refused), s => Assert.Contains(s.Reason, oblique.First.Notes));
        }
        Assert.All(oblique.First.Sizes.Where(s => s.Status != SizeStatus.Made), s => Assert.False(string.IsNullOrEmpty(s.Reason)));

        // Named by their bodies, only added after the author's.
        var labels = SupportedBodies.Choices.Select(c => c.Label).ToList();
        Assert.All(oblique.First.Sizes.Where(s => s.Status == SizeStatus.Made),
                   s => Assert.Contains(labels, l => s.Option!.StartsWith(l, StringComparison.Ordinal)));
        Data.AssertOnlyAppended(oblique.Before, Data.Shape(oblique.Copy));
        // The large busts fold, said and saved all the same.
        Assert.All(oblique.First.Sizes.Where(s => s.Status == SizeStatus.Made && s.Option is "Rue L" or "YAB L" or "YAB Buff L"),
                   s => Assert.NotNull(s.Measures));
    }

    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-size-set-made-on-2")]
    [Trait("Plan", "EDGE-size-set-made-on-14")]
    [Trait("Speed", "Slow")]
    public void Oblique_sizes_are_never_cut_from_a_tummy_option_and_follow_the_authors_order() // [AC-size-set-made-on-2] [EDGE-size-set-made-on-14]
    {
        var made = oblique.First.Sizes.Where(s => s.Status == SizeStatus.Made).ToList();
        Assert.NotEmpty(made);
        // Women's sizes and the men's versions alike: the threshold reaches both choosers.
        Assert.All(made, s => Assert.DoesNotContain("Tummy", s.CutFrom ?? "", StringComparison.Ordinal));
        Assert.Contains(made, s => s.Men);
        foreach (var piece in oblique.Pieces)
        {
            var order = piece.Options.Select(o => o.Option).ToList();
            foreach (string key in WomensKeys)
            {
                var sized = new[] { "S", "M", "L" }
                    .Select(n => made.FirstOrDefault(s => s.GamePath.Equals(piece.GamePath, StringComparison.OrdinalIgnoreCase)
                                                          && s.Body == key && s.Option!.EndsWith(" " + n, StringComparison.Ordinal)))
                    .OfType<SizeSetRefit.SizeOutcome>()
                    .Select(s => order.IndexOf(s.CutFrom))
                    .ToList();
                Assert.Equal(sized.OrderBy(i => i), sized);
            }
        }
    }

    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-size-set-made-on-14")]
    [Trait("Speed", "Slow")]
    public void Made_on_threshold_reaches_every_chooser() // [EDGE-size-set-made-on-14]
    {
        // The Small Tummy top reads on Bibo+ as Nude Large; the women's sizes and the men's versions both pass it over.
        var passedOver = oblique.Log.Split('\n')
                                .Where(l => l.Contains("Small Tummy:", StringComparison.Ordinal)
                                            && l.Contains("passed over", StringComparison.Ordinal))
                                .ToList();
        output.WriteLine(string.Join("\n", passedOver.Take(4)));
        Assert.Contains(passedOver, l => !l.Contains("for men", StringComparison.Ordinal));
        Assert.Contains(passedOver, l => l.Contains("for men", StringComparison.Ordinal));
    }

    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-size-set-made-on-7")]
    [Trait("Speed", "Slow")]
    public void Second_made_on_run_adds_nothing_and_says_so() // [EDGE-size-set-made-on-7]
    {
        Assert.Equal(0, oblique.Second.Made);
        Assert.Equal(oblique.ManifestAfterFirst, oblique.ManifestAfterSecond);
        foreach (var made in oblique.First.Sizes.Where(s => s.Status == SizeStatus.Made))
            Assert.Contains(oblique.Second.Sizes, s => s.GamePath == made.GamePath && s.Option == made.Option
                                                       && s.ReasonCode == SizeSetRefit.Codes.AlreadyThere);
        bool firstLeftNothing = oblique.First.Sizes.All(s => s.Status == SizeStatus.Made
                                                             || SizeSetRefit.Summarize(new() { Sizes = { s } }, 0)
                                                                == SizeSetRefit.Summary.HadEverySize);
        Assert.Equal(firstLeftNothing ? SizeSetRefit.Summary.HadEverySize : SizeSetRefit.Summary.SeeLeftOut,
                     SizeSetRefit.Summarize(oblique.Second, 0));
    }

    // ── Oblique without "Made on", and with the wrong one ─────────────────────────────────────────────────────────

    private static string MigratedOblique()
    {
        string copy = Data.UnpackedPack(Data.ObliquePack);
        PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
        return copy;
    }

    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-4")]
    [Trait("Plan", "EDGE-size-set-made-on-8")]
    public void Oblique_without_made_on_gets_nothing_and_is_told_to_choose_it() // [AC-size-set-made-on-4] [EDGE-size-set-made-on-8]
    {
        string copy = MigratedOblique();
        try
        {
            var readGame = Data.Game();
            AssertObliquePieces(SizeSetRefit.PiecesOf(copy, readGame));
            byte[] before = File.ReadAllBytes(Path.Combine(copy, PenumbraModMeta.MetaFile));
            var (_, log) = Data.Batch(copy, ["rue"], men: false, [Data.Bibo, Data.Rue], readGame: readGame);
            Print(log);
            Assert.Equal(0, log.Made);
            foreach (var piece in SizeSetRefit.PiecesOf(copy, readGame))
                Assert.Contains(string.Format(Strings.SizeSet.UnreadableMadeOnFmt, piece.Label), log.Notes);
            Assert.Equal(SizeSetRefit.Summary.SeeLeftOut, SizeSetRefit.Summarize(log, log.Made));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(copy, PenumbraModMeta.MetaFile)));

            // After a run with "Made on": the sizes it made are no sources, and a run without it touches nothing.
            var withMadeOn = Data.Batch(copy, ["rue"], men: false, [Data.Bibo, Data.Rue], readGame: readGame,
                                        madeOn: Data.Folder(Data.Bibo)).Log;
            Assert.True(withMadeOn.Made > 0);
            byte[] made = File.ReadAllBytes(Path.Combine(copy, PenumbraModMeta.MetaFile));
            var (_, after) = Data.Batch(copy, ["rue"], men: false, [Data.Bibo, Data.Rue], readGame: readGame);
            Assert.Equal(0, after.Made);
            foreach (var piece in SizeSetRefit.PiecesOf(copy, readGame))
                Assert.Contains(string.Format(Strings.SizeSet.UnreadableMadeOnFmt, piece.Label), after.Notes);
            Assert.Equal(made, File.ReadAllBytes(Path.Combine(copy, PenumbraModMeta.MetaFile)));
        }
        finally { Data.Delete(copy); }
    }

    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline)]
    [Trait("Plan", "AC-size-set-made-on-4")]
    public void Auto_refit_still_reads_Oblique_as_no_pick() // [AC-size-set-made-on-4]
    {
        string copy = Data.UnpackedPack(Data.ObliquePack);
        try
        {
            string top = Directory.GetFiles(copy, "c0201e0485_top.mdl", SearchOption.AllDirectories)
                                  .First(f => f.Replace('\\', '/').Contains("/top size/medium/", StringComparison.OrdinalIgnoreCase));
            byte[] bytes = File.ReadAllBytes(top);
            var garment = ModelPartReader.Read(bytes)!;
            var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
            var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods),
                                                 new[] { Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline }
                                                 .Select(Data.Folder).ToList());
            var bibo = bodies.First(b => b.Dir == Data.Folder(Data.Bibo));
            var order = RefitCore.SearchOrder(bibo.Dir, bibo.Catalog, bodies.Select(b => (b.Dir, b.Catalog)), null);
            Assert.Null(RefitCore.DetectSource(garment, bones, "_top", "0201", order, bibo.Dir, _ => true));
        }
        finally { Data.Delete(copy); }
    }

    [LocalDataFact(Data.ObliquePack, Data.Tbse, Data.Rue, Data.Neolithe, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-7")]
    [Trait("Plan", "EDGE-size-set-made-on-1")]
    public void Oblique_made_on_a_body_of_another_race_makes_nothing() // [AC-size-set-made-on-7] [EDGE-size-set-made-on-1]
    {
        string copy = MigratedOblique();
        try
        {
            var readGame = Data.Game();
            var (_, log) = Data.Batch(copy, ["rue", "neolithe"], men: false, [Data.Tbse, Data.Rue, Data.Neolithe],
                                      readGame: readGame, madeOn: Data.Folder(Data.Tbse));
            Print(log);
            Assert.Equal(0, log.Made);
            string tbse = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods), [Data.Folder(Data.Tbse)])[0].Name;
            foreach (var piece in SizeSetRefit.PiecesOf(copy, readGame))
            {
                Assert.Contains(string.Format(Strings.SizeSet.NotMadeOnFmt, piece.Label, tbse), log.Notes);
                Assert.Contains(log.Sizes, s => s.GamePath == piece.GamePath && s.ReasonCode == SizeSetRefit.Codes.NotMadeOn);
            }
        }
        finally { Data.Delete(copy); }
    }

    /// <summary>
    /// "Made on" naming a body of the right shape that Oblique was not made on, as measured: the skin-off cap catches
    /// neither Neolithe (2.7–4.2 mm off, its sizes read and cut from) nor Rosaline's tops (5.8–9.1 mm off); only
    /// Rosaline's legs, too unclear to tell apart, are refused. The author's choice is taken as said.
    /// </summary>
    [LocalDataFact(Data.ObliquePack, Data.Neolithe, Data.Rosaline, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-7")]
    [Trait("Plan", "EDGE-size-set-made-on-2")]
    [Trait("Speed", "Slow")]
    public void Oblique_made_on_a_wrong_body_is_as_measured() // [AC-size-set-made-on-7] [EDGE-size-set-made-on-2]
    {
        // Each of the author's sizes read on the body: how many were taken, and how many not, per piece.
        (SizeSetRefit.RunLog Log, (int Taken, int Not) Top, (int Taken, int Not) Legs) Run(string madeOn)
        {
            string copy = MigratedOblique();
            try
            {
                var said = new StringWriter();
                var log = Data.Batch(copy, ["rue"], men: false, [Data.Neolithe, Data.Rosaline, Data.Rue], readGame: Data.Game(),
                                     madeOn: Data.Folder(madeOn), log: RefitCli::Proteus.Refit.ConsoleLog.To(said)).Log;
                Print(log);
                // The reading of each size, once: not a choice's "only size read on" line, nor a cached answer.
                var lines = said.ToString().Split('\n')
                                .Where(l => l.Contains(" read on ", StringComparison.Ordinal)
                                            && !l.Contains("only size read on", StringComparison.Ordinal)
                                            && !l.Contains("read on the Made on body before", StringComparison.Ordinal))
                                .ToList();
                (int, int) Count(string piece)
                {
                    var of = lines.Where(l => l.Contains("size set: " + piece, StringComparison.Ordinal)).ToList();
                    int not = of.Count(l => l.Contains("; not taken", StringComparison.Ordinal));
                    return (of.Count - not, not);
                }
                return (log, Count("Body"), Count("Legs"));
            }
            finally { Data.Delete(copy); }
        }
        bool Made(SizeSetRefit.RunLog log, string gamePath)
            => log.Sizes.Any(s => s.Status == SizeStatus.Made && s.GamePath.Equals(gamePath, StringComparison.OrdinalIgnoreCase));

        // As measured: on Neolithe 9 of Oblique's 10 tops are taken (XLarge Tummy unclear), and its Small legs and Small
        // Tummy legs; on Rosaline 3 tops, and no legs at all.
        var (neolithe, neoTop, neoLegs) = Run(Data.Neolithe);
        Assert.Equal((9, 1), neoTop);
        Assert.Equal((2, 4), neoLegs);
        Assert.True(Made(neolithe, ObliqueTop));
        Assert.True(Made(neolithe, ObliqueLegs));

        var (rosaline, rosTop, rosLegs) = Run(Data.Rosaline);
        Assert.Equal((3, 7), rosTop);
        Assert.Equal((0, 6), rosLegs);
        Assert.True(Made(rosaline, ObliqueTop));
        Assert.False(Made(rosaline, ObliqueLegs));
        string name = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods), [Data.Folder(Data.Rosaline)])[0].Name;
        Assert.Contains(rosaline.Sizes, s => s.GamePath.Equals(ObliqueLegs, StringComparison.OrdinalIgnoreCase)
                                             && s.ReasonCode == SizeSetRefit.Codes.NotMadeOn);
        Assert.Contains(rosaline.Notes, n => n == string.Format(Strings.SizeSet.MadeOnUnclearFmt,
                                                                rosaline.Sizes.First(s => s.ReasonCode == SizeSetRefit.Codes.NotMadeOn).Piece,
                                                                name));
    }

    [LocalDataFact(Data.ObliquePack, Data.Bibo, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-7")]
    [Trait("Plan", "EDGE-size-set-made-on-4")]
    public void Made_on_missing_from_the_bodies_is_said() // [AC-size-set-made-on-7] [EDGE-size-set-made-on-4]
    {
        string copy = MigratedOblique();
        try
        {
            var readGame = Data.Game();
            var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods), [Data.Folder(Data.Bibo)]);
            byte[] before = File.ReadAllBytes(Path.Combine(copy, PenumbraModMeta.MetaFile));
            var log = new SizeSetRefit.RunLog();
            var plugin = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            SizeSetRefit.Run(new SizeSetRefit.Request(copy, ["bibo"], Yas: true, Remake: false, bodies, readGame,
                                                      MadeOn: "not-installed"),
                             log, new UVRemapService(plugin, LocalData.Path(LocalData.UvMaps)), plugin, _ => { },
                             System.Threading.CancellationToken.None);
            Assert.Equal(0, log.Made);
            foreach (var piece in SizeSetRefit.PiecesOf(copy, readGame))
                Assert.Contains(string.Format(Strings.SizeSet.MadeOnMissingFmt, piece.Label, "not-installed"), log.Notes);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(copy, PenumbraModMeta.MetaFile)));
        }
        finally { Data.Delete(copy); }
    }

    /// <summary>
    /// One of the author's files read twice in a run — Oblique's Medium top given a second item as well — is answered from
    /// the run's readings, and still read on the "Made on" body: its Ambiguous reading there is taken again, never put
    /// through the automatic reading's gate, and the second piece is sized from it.
    /// </summary>
    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-size-set-made-on-19")]
    [Trait("Speed", "Slow")]
    public void A_file_read_twice_keeps_its_made_on_reading() // [EDGE-size-set-made-on-19]
    {
        string copy = MigratedOblique();
        try
        {
            const string second = "chara/equipment/e0486/model/c0201e0486_top.mdl";
            var redirects = PenumbraModMeta.ReadAllRedirects(copy);
            string medium = redirects.First(r => r.Source == "Top Size / Medium"
                                                 && r.GamePath.Equals(ObliqueTop, StringComparison.OrdinalIgnoreCase)).File;
            var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, PenumbraModMeta.MetaFile)))!;
            meta["DefaultData"] ??= new JsonObject();
            meta["DefaultData"]!["Files"] ??= new JsonObject();
            meta["DefaultData"]!["Files"]![second] = medium;
            File.WriteAllText(Path.Combine(copy, PenumbraModMeta.MetaFile), meta.ToJsonString());

            var said = new StringWriter();
            var (_, log) = Data.Batch(copy, ["rue"], men: false, [Data.Bibo, Data.Rue], readGame: Data.Game(),
                                      madeOn: Data.Folder(Data.Bibo), log: RefitCli::Proteus.Refit.ConsoleLog.To(said));
            Print(log);
            string text = said.ToString();
            Assert.Contains("was read on the Made on body before in this run", text);
            Assert.Contains(log.Sizes, s => s.GamePath == second && s.Status == SizeStatus.Made);
            Assert.DoesNotContain(log.Sizes, s => s.GamePath == second && s.ReasonCode == SizeSetRefit.Codes.NotMadeOn);
            // Read on Bibo+ once: the second time is the run's own answer.
            // The top's Medium file, by either piece; the legs' Medium is another file.
            var readOn = text.Split('\n').Where(l => l.Contains("size set: Body", StringComparison.Ordinal)
                                                     && (l.Contains(": Medium read on Bibo+", StringComparison.Ordinal)
                                                         || l.Contains(medium + " read on Bibo+", StringComparison.OrdinalIgnoreCase))
                                                     && !l.Contains("for men", StringComparison.Ordinal)).ToList();
            output.WriteLine(string.Join("\n", readOn));
            Assert.Single(readOn);
        }
        finally { Data.Delete(copy); }
    }

    // ── the command line ──────────────────────────────────────────────────────────────────────────────────────────

    private static string[] CliArgs(string copy, string modsRoot, string madeOn, string bodies = "rue")
        => ["--size-set", copy, "--bodies", bodies, "--mods-root", modsRoot, "--game", LocalData.Path(LocalData.GameData),
            "--uvmaps", LocalData.Path(LocalData.UvMaps), "--made-on", madeOn];

    [LocalDataFact(Data.ObliquePack, Data.Bibo, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-3")]
    [Trait("Plan", "EDGE-size-set-made-on-15")]
    [Trait("Speed", "Slow")]
    public void Cli_made_on_sizes_Oblique_and_reports_it() // [AC-size-set-made-on-3] [EDGE-size-set-made-on-15]
    {
        string copy = Data.UnpackedPack(Data.ObliquePack);
        string modsRoot = Data.ModsRootWith(Data.Bibo, Data.Rue);
        try
        {
            var stdout = new StringWriter();
            int exit = RefitCli::Proteus.Refit.Program.Dispatch(CliArgs(copy, modsRoot, Data.Folder(Data.Bibo)), stdout, TextWriter.Null);
            var report = JsonNode.Parse(stdout.ToString())!;
            output.WriteLine(stdout.ToString()[..Math.Min(4000, stdout.ToString().Length)]);
            Assert.Equal(0, exit);
            Assert.Equal(Data.Folder(Data.Bibo), (string?)report["madeOn"]);
            Assert.True((int)report["made"]! > 0);
            Assert.Equal(3, (int)report["migrated"]!["fromVersion"]!);

            string fresh = MigratedOblique();
            try { Data.AssertOnlyAppended(Data.Shape(fresh), Data.Shape(copy)); }
            finally { Data.Delete(fresh); }
        }
        finally
        {
            Data.Delete(copy);
            Data.DeleteModsRoot(modsRoot);
        }
    }

    [LocalDataFact(Data.ObliquePack, Data.Bibo, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-3")]
    [Trait("Plan", "EDGE-size-set-made-on-12")]
    public void Cli_made_on_path_rules() // [AC-size-set-made-on-3] [EDGE-size-set-made-on-12]
    {
        string modsRoot = Data.ModsRootWith(Data.Bibo);
        try
        {
            Assert.Equal(Data.Folder(Data.Bibo), SizeSetBatch.MadeOnFolder(Path.Combine(modsRoot, Data.Folder(Data.Bibo)), modsRoot, out var error));
            Assert.Null(error);
            Assert.Equal(Data.Folder(Data.Bibo), SizeSetBatch.MadeOnFolder(Data.Folder(Data.Bibo), modsRoot, out _));
            Assert.Null(SizeSetBatch.MadeOnFolder(LocalData.Path(Data.Bibo), modsRoot, out error));
            Assert.Contains("is not a folder under --mods-root", error);
            // One folder by name: never the mod folder itself, its parent, or one further down.
            foreach (string notOne in new[] { ".", "..", Data.Folder(Data.Bibo) + "/files", Path.Combine(modsRoot, ".") })
            {
                Assert.Null(SizeSetBatch.MadeOnFolder(notOne, modsRoot, out error));
                Assert.NotNull(error);
            }
            Assert.Equal(Data.Folder(Data.Bibo),
                         SizeSetBatch.MadeOnFolder(Path.Combine(modsRoot, Data.Folder(Data.Bibo), "."), modsRoot, out _));

            string copy = Data.UnpackedPack(Data.ObliquePack);
            try
            {
                var stderr = new StringWriter();
                Assert.Equal(2, RefitCli::Proteus.Refit.Program.Dispatch(CliArgs(copy, modsRoot, LocalData.Path(Data.Bibo)),
                                                                          TextWriter.Null, stderr));
                Assert.Contains("--made-on", stderr.ToString());
            }
            finally { Data.Delete(copy); }
        }
        finally { Data.DeleteModsRoot(modsRoot); }
    }

    [LocalDataFact(Data.ObliquePack, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-3")]
    [Trait("Plan", "EDGE-size-set-made-on-12")]
    public void Cli_made_on_unknown_folder_is_a_usage_error() // [AC-size-set-made-on-3] [EDGE-size-set-made-on-12]
    {
        string copy = Data.UnpackedPack(Data.ObliquePack);
        string modsRoot = Data.NewCopy();
        Directory.CreateDirectory(modsRoot);
        try
        {
            var stderr = new StringWriter();
            Assert.Equal(2, RefitCli::Proteus.Refit.Program.Dispatch(CliArgs(copy, modsRoot, "Nobody"), TextWriter.Null, stderr));
            Assert.Contains("--made-on Nobody: no such folder", stderr.ToString());
        }
        finally
        {
            Data.Delete(copy);
            Data.Delete(modsRoot);
        }
    }

    [LocalDataFact(Data.ObliquePack, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-3")]
    [Trait("Plan", "EDGE-size-set-made-on-12")]
    public void Cli_made_on_not_a_body_is_refused_and_writes_nothing() // [AC-size-set-made-on-3] [EDGE-size-set-made-on-12]
    {
        // A mods folder with one outfit in it: no body files at all.
        string modsRoot = Data.NewCopy();
        string outfit = Path.Combine(modsRoot, "Outfit");
        Directory.CreateDirectory(outfit);
        File.WriteAllText(Path.Combine(outfit, PenumbraModMeta.MetaFile), "{ \"FileVersion\": 3, \"Name\": \"Outfit\" }");
        string copy = Data.UnpackedPack(Data.ObliquePack);
        string noPieces = Data.NewCopy();
        Directory.CreateDirectory(noPieces);
        File.WriteAllText(Path.Combine(noPieces, PenumbraModMeta.MetaFile),
                          "{ \"FileVersion\": 3, \"Name\": \"Nothing\", \"Groups\": [] }");
        try
        {
            var before = Directory.GetFiles(copy, "*", SearchOption.AllDirectories)
                                  .ToDictionary(f => f, File.ReadAllBytes);
            var stdout = new StringWriter();
            Assert.Equal(1, RefitCli::Proteus.Refit.Program.Dispatch(CliArgs(copy, modsRoot, "Outfit"), stdout, TextWriter.Null));
            Assert.Equal("made-on-not-a-body", (string?)JsonNode.Parse(stdout.ToString())!["refusalCode"]);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(copy, "*", SearchOption.AllDirectories).Order());
            Assert.All(before, f => Assert.Equal(f.Value, File.ReadAllBytes(f.Key)));
            Assert.True(PenumbraModMeta.IsLegacyFolder(copy));

            var again = new StringWriter();
            Assert.Equal(1, RefitCli::Proteus.Refit.Program.Dispatch(CliArgs(noPieces, modsRoot, "Outfit"), again, TextWriter.Null));
            Assert.Equal("made-on-not-a-body", (string?)JsonNode.Parse(again.ToString())!["refusalCode"]);
        }
        finally
        {
            Data.Delete(copy);
            Data.Delete(noPieces);
            Data.Delete(modsRoot);
        }
    }

    // ── a mod Proteus reads itself ────────────────────────────────────────────────────────────────────────────────

    [LocalDataFact(Data.ThisOldThing, Data.Neolithe, Data.Rue, Data.Bibo, Data.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-size-set-made-on-6")]
    [Trait("Plan", "AC-size-set-made-on-10")]
    [Trait("Plan", "EDGE-size-set-made-on-20")]
    [Trait("Speed", "Slow")]
    public void Made_on_is_only_a_fallback_for_This_Old_Thing() // [AC-size-set-made-on-6] [AC-size-set-made-on-10] [EDGE-size-set-made-on-20]
    {
        string[] bodies = [Data.Neolithe, Data.Rue, Data.Bibo, Data.Rosaline];
        var readGame = Data.Game();
        var installed = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods), bodies.Select(Data.Folder).ToList());
        var mods = installed.Select(b => (b.Dir, b.Catalog)).ToList();

        // Precondition: every chest and legs size of the mod reads automatically, at a confidence the size set takes.
        string probe = Data.CopyOf(Data.ThisOldThing);
        var autoReadings = new Dictionary<string, AutoRefitDecisions.Pick>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var piece in SizeSetRefit.PiecesOf(probe, readGame).Where(p => p.Slot is "_top" or "_dwn"))
                foreach (var (option, file) in piece.Options)
                {
                    byte[] bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(probe, file));
                    var pick = RefitCore.DetectSource(ModelPartReader.Read(bytes)!,
                                                      new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames), piece.Slot,
                                                      piece.Race, mods, Data.Folder(Data.Neolithe), _ => true);
                    output.WriteLine($"{piece.Label} {option}: {pick?.Dir} {pick?.Option.Label} ({pick?.Confidence})");
                    Assert.NotNull(pick);
                    Assert.True(AutoRefitDecisions.Proceed(pick.Confidence), $"{option} reads {pick.Confidence}");
                    if (option != null) autoReadings[piece.Label + "|" + option] = pick;
                }
        }
        finally { Data.Delete(probe); }
        Assert.Contains(autoReadings, r => r.Key.EndsWith("|Rue M", StringComparison.Ordinal) && r.Value.Dir == Data.Folder(Data.Rue));

        // A run on a fresh copy, and a second one on the same copy after it.
        (SizeSetRefit.RunLog Log, string Said, SizeSetRefit.RunLog Again) Run(string? madeOn)
        {
            string copy = Data.CopyOf(Data.ThisOldThing);
            try
            {
                var said = new StringWriter();
                var log = Data.Batch(copy, ["neolithe", "rue", "bibo"], men: false, bodies, readGame: readGame, madeOn: madeOn,
                                     log: RefitCli::Proteus.Refit.ConsoleLog.To(said)).Log;
                var again = madeOn == null
                    ? new SizeSetRefit.RunLog()
                    : Data.Batch(copy, ["neolithe", "rue", "bibo"], men: false, bodies, readGame: readGame, madeOn: madeOn).Log;
                return (log, said.ToString(), again);
            }
            finally { Data.Delete(copy); }
        }
        static List<string> Outcomes(SizeSetRefit.RunLog log)
            => log.Sizes.Select(s => $"{s.GamePath}|{s.Option}|{s.Status}|{s.CutFrom}|{s.From}").ToList();

        // Any "Made on" reads every size as before, none of them on the body chosen: only what cannot be read falls back.
        // The inexact ones read on another body than the one chosen are said, once per option a size was cut from.
        var (plain, _, _) = Run(null);
        foreach (string madeOn in new[] { Data.Neolithe, Data.Bibo })
        {
            var (run, said, again) = Run(Data.Folder(madeOn));
            Assert.Equal(Outcomes(plain), Outcomes(run));
            Assert.DoesNotContain(" read on ", said, StringComparison.Ordinal);
            string name = installed.First(b => b.Dir == Data.Folder(madeOn)).Name;
            var expected = run.Sizes.Where(s => s.Status == SizeStatus.Made && s.CutFrom != null
                                                && s.Slot is "_top" or "_dwn"
                                                && autoReadings.TryGetValue(s.Piece + "|" + s.CutFrom, out var p)
                                                && p.Confidence != BodySizeMatch.Confidence.Exact
                                                && p.Dir != Data.Folder(madeOn))
                                    .Select(s => (s.Piece, s.CutFrom!)).Distinct().ToList();
            var warned = run.Warnings.Where(w => w.Contains("\"Made on\"", StringComparison.Ordinal)).ToList();
            output.WriteLine($"{name}: {expected.Count} expected\n{string.Join("\n", warned)}");
            // Rue M reads Likely on Rue+: under any other "Made on", the sizes cut from it are said.
            Assert.NotEmpty(expected);
            Assert.Equal(expected.Count, warned.Count);
            // Nothing made the second time, and so nothing said.
            Assert.Equal(0, again.Made);
            Assert.DoesNotContain(again.Warnings, w => w.Contains("\"Made on\"", StringComparison.Ordinal));
            Assert.All(warned, w => Assert.Contains(name, w));
            Assert.All(expected, e => Assert.Contains(warned, w => w.StartsWith(e.Piece + ":", StringComparison.Ordinal)
                                                                   && w.Contains($"\"{e.Item2}\"", StringComparison.Ordinal)));
        }
    }

    // ── shoes and gloves, and a unisex mod's men's piece ─────────────────────────────────────────────────────────

    [LocalDataFact(LocalData.ShiftPack, Data.Yab, Data.Rue, Data.Bibo, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-size-set-made-on-6")]
    [Trait("Speed", "Slow")]
    public void A_shoe_only_mod_reads_the_same_with_made_on() // [EDGE-size-set-made-on-6]
    {
        List<string> Run(string? madeOn)
        {
            string root = Data.NewCopy();
            Directory.CreateDirectory(Path.Combine(root, "shoe"));
            File.WriteAllBytes(Path.Combine(root, "shoe", "sho.mdl"), LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl"));
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile),
                "{ \"FileVersion\": 4, \"Name\": \"Shoe\", \"DefaultData\": { \"Files\": {}, \"FileSwaps\": {}, \"Manipulations\": [] },"
              + " \"Groups\": [ { \"Name\": \"Shoes\", \"Type\": \"Single\", \"Priority\": 0, \"DefaultSettings\": 0,"
              + " \"Options\": [ { \"Name\": \"YAB WC\", \"Files\": { \"chara/equipment/e6219/model/c0201e6219_sho.mdl\": \"shoe\\\\sho.mdl\" } } ] } ] }");
            try
            {
                var log = Data.Batch(root, ["yab", "rue", "bibo"], men: false, [Data.Yab, Data.Rue, Data.Bibo], madeOn: madeOn).Log;
                return log.Sizes.Select(s => $"{s.Option}|{s.Status}|{s.ReasonCode}|{s.From}").ToList();
            }
            finally { Data.Delete(root); }
        }
        Assert.Equal(Run(null), Run(Data.Folder(Data.Bibo)));
    }

    [LocalDataFact(Data.Bibo, Data.Rue, Data.Tbse, Data.TbseX, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-size-set-made-on-18")]
    [Trait("Speed", "Slow")]
    public void A_unisex_mods_mens_piece_keeps_its_TBSE_reading_under_made_on() // [EDGE-size-set-made-on-18]
    {
        // An outfit whose men's top is TBSE's own chest and whose women's top is Bibo+'s Nude Medium.
        string root = Data.NewCopy();
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(LocalData.Path(Data.Tbse), "common", "1", "c0101e0000_top.mdl"), Path.Combine(root, "men.mdl"));
        File.Copy(Path.Combine(LocalData.Path(Data.Bibo), "breasts - small clothes", "nude - medium", "chara", "equipment", "e0000",
                               "model", "c0201e0000_top.mdl"), Path.Combine(root, "women.mdl"));
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile),
            "{ \"FileVersion\": 4, \"Name\": \"Unisex\", \"DefaultData\": { \"Files\": {"
          + " \"chara/equipment/e6001/model/c0101e6001_top.mdl\": \"men.mdl\","
          + " \"chara/equipment/e6001/model/c0201e6001_top.mdl\": \"women.mdl\" }, \"FileSwaps\": {}, \"Manipulations\": [] },"
          + " \"Groups\": [] }");
        try
        {
            var (_, log) = Data.Batch(root, ["rue", "tbse-x"], men: false, [Data.Bibo, Data.Rue, Data.Tbse, Data.TbseX],
                                      madeOn: Data.Folder(Data.Bibo));
            Print(log);
            var men = log.Sizes.Where(s => s.GamePath.Contains("c0101", StringComparison.Ordinal)).ToList();
            Assert.Contains(men, s => s.Body == "tbse-x" && s.Status == SizeStatus.Made);
            Assert.DoesNotContain(men, s => s.ReasonCode == SizeSetRefit.Codes.NotMadeOn);
            Assert.DoesNotContain(log.Warnings, w => w.Contains("Made on", StringComparison.Ordinal)
                                                     && w.StartsWith(men[0].Piece, StringComparison.Ordinal));
        }
        finally { Data.Delete(root); }
    }
}
