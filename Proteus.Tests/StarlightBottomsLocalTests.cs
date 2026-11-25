extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// "Starlight"'s Valentione Skirt refitted onto Tre Medium (Gen A legs), as the Studio's Body size refits it. Reported
/// 2026-11-24: every bottoms size showed skin through the coarse stockings (faces 40-80 mm across, the rounder new legs up
/// through their middles — see <see cref="BodyRetarget.ClearFaceCap(float)"/>), and many had the new skin cut away at the
/// front of the thighs, where the author pressed their skin in under the stocking band (see
/// <see cref="BodyRetarget.PressedUnder"/>). Each test reads the refit as it is beside the build before the fix (both
/// knobs on), run once each in the class fixture.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class StarlightBottomsLocalTests(StarlightBottomsLocalTests.Runs runs, ITestOutputHelper o)
    : IClassFixture<StarlightBottomsLocalTests.Runs>
{
    internal const string Garment = "valentione skirt\\chara\\equipment\\e0145\\model\\c0201e0145_dwn.mdl";
    internal const string MadeOn = "hip size\\large\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl";
    internal const string TreMedium = "files\\common\\15\\c0201e0000_dwn.mdl";

    /// <summary>The build before the fix.</summary>
    internal const string Before = "NoCoarseSag,NoPressedSkinCut,NoKneeStandoff";

    /// <summary>The inputs the references below were measured on (SHA-256): a change of any is a re-measure, not a pass.</summary>
    internal static readonly (string Mod, string Rel, string Sha)[] Inputs =
    [
        (LocalData.StarlightMod, Garment, "bfb6e6ef541307ecab9554dba963858578c2c63b522e64714913d6fe42aa113a"),
        (LocalData.TightFirmMod, MadeOn, "1981bfa00014d81310717bc3fd3ab0049b18dde436f77ce8b5f5f38493fee6c4"),
        (LocalData.TreMod, TreMedium, "da27c673e378439417cc0ace588b93c2df145191cfa33107ec49bc5b78848827"),
    ];

    /// <summary>Measured on these inputs before the fix (2026-11-24): skin points through the stockings, and Tre's skin
    /// triangles cut where the author's skin was whole (on the legs below 0.90 m, outside the crotch: the front of both
    /// thighs, y 0.78-0.82 m).</summary>
    internal const int StockingSkinOutBefore = 642, LeftOutBefore = 34;

    /// <summary>The bar fixed before the fix was tuned: 5% of <see cref="StockingSkinOutBefore"/>.</summary>
    internal const int StockingBar = 33;

    /// <summary>The bar recorded once the cap's curve was final (50 mm): the fix left 0 skin points through the stockings
    /// on Tre Medium; a little room for rounding across machines.</summary>
    internal const int StockingAfterBar = 5;

    /// <summary>How much more cloth movement out of line with its neighbours the fix may leave on the fine parts (the
    /// skirt, the ring) than the build before it (0.5 mm), max and 99th percentile.</summary>
    internal const float OutOfLineSlack = 0.0005f;

    /// <summary>Skin points that may newly stand through the panties, band and skirt (2; one was measured, 0.7 mm over
    /// the skirt).</summary>
    internal const int CrotchSkinOutSlack = 2;

    /// <summary>
    /// How much steeper a coarse part's movement (the stockings, their band, the panties: see
    /// <see cref="StarlightMeasure.CoarseParts"/>) may be out of line with its neighbours than before the fix (0.07: 7 mm
    /// over 100 mm). The fix lifts them face by face by each face's sag, L²/8r, so two neighbours' lifts can differ by up
    /// to that over their spacing L — a slope of L/8r, 0.1-0.2 for faces 40-80 mm across. Measured 2026-11-24 over the
    /// twelve sizes: up to +0.06 (Neolithe's shins, 0.059 → 0.119); Tre's thighs 0.105 → 0.132. Set after that
    /// measurement, under the bound; a first guess of 0.02 had no data.
    /// </summary>
    internal const float CoarseSlopeSlack = 0.07f;

    public sealed class Runs : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "proteus-starlight-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, ModelParts> models = new();
        public ModelParts Original { get; }
        public ModelParts Body { get; }

        public Runs()
        {
            Original = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(LocalData.Path(LocalData.StarlightMod), Garment)))!;
            Body = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(LocalData.Path(LocalData.TreMod), TreMedium)))!;
        }

        /// <summary>The skirt refit onto Tre Medium with the given --tune knobs (none: the build as it is).</summary>
        public ModelParts Model(string tune = "")
        {
            if (models.TryGetValue(tune, out var m)) return m;
            // Each tune its own folder (its name, sanitised): two tunes never write over each other.
            string outDir = Path.Combine(Dir, tune.Length == 0 ? "as-is" : new string(tune.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()));
            Directory.CreateDirectory(outDir);
            var args = new List<string>
            {
                "--garment", Path.Combine(LocalData.Path(LocalData.StarlightMod), Garment),
                "--from-root", LocalData.Path(LocalData.TightFirmMod), "--from", MadeOn,
                "--body-root", LocalData.Path(LocalData.TreMod), "--to", TreMedium + "=TreM", "--slot", "_dwn",
                "--uvmaps", LocalData.Path(LocalData.UvMaps), "--out-dir", outDir,
            };
            if (tune.Length > 0) args.AddRange(["--tune", tune]);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var console = Console.Out;
            int exit;
            Console.SetOut(stdout);   // the report it prints is not read here
            try { exit = RefitCli::Proteus.Refit.Program.Dispatch([.. args], stdout, stderr); }
            finally { Console.SetOut(console); }
            if (exit != 0) throw new InvalidOperationException($"refit onto Tre Medium failed: {stderr}");
            return models[tune] = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(outDir, "TreM.mdl")))!;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void InputsAreTheMeasuredOnes()
    {
        foreach (var (mod, rel, sha) in Inputs)
        {
            string path = Path.Combine(LocalData.Path(mod), rel.Replace('\\', Path.DirectorySeparatorChar));
            string now = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.True(now == sha, $"inputs changed ({rel}): re-measure the references");
        }
    }

    [LocalDataFact(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.TreMod, LocalData.UvMaps)]
    [Trait("Plan", "AC-starlight-bottoms-1")]
    [Trait("Plan", "EDGE-starlight-bottoms-21")]
    [Trait("Plan", "EDGE-starlight-bottoms-22")]
    public void The_stockings_cover_the_skin_on_Tre_Medium()
    {
        InputsAreTheMeasuredOnes();
        int before = StarlightMeasure.StockingSkinOut(runs.Model(Before));
        int now = StarlightMeasure.StockingSkinOut(runs.Model());
        o.WriteLine($"skin points through the stockings: {now} (before the fix {before})");
        Assert.Equal(StockingSkinOutBefore, before);
        Assert.True(now <= StockingBar && now <= StockingAfterBar, $"{now} skin points through the stockings");
    }

    [LocalDataFact(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.TreMod, LocalData.UvMaps)]
    [Trait("Plan", "AC-starlight-bottoms-2")]
    public void No_skin_is_cut_at_the_thigh_fronts_on_Tre_Medium()
    {
        InputsAreTheMeasuredOnes();
        var before = StarlightMeasure.LeftOutBodySkin(runs.Body, runs.Model(Before));
        var now = StarlightMeasure.LeftOutBodySkin(runs.Body, runs.Model());
        o.WriteLine($"skin left out where the author's was whole: {now.Count} (before the fix {before.Count})");
        Assert.Equal(LeftOutBefore, before.Count);
        Assert.Empty(now);
    }

    [LocalDataFact(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.TreMod, LocalData.UvMaps)]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-8")]
    public void The_authors_crotch_cut_still_stands_on_Tre_Medium()
    {
        // The skirt's crotch hole still cuts: the new rule keeps skin only where the author pressed theirs in (measured:
        // 646 of 646 still cut). Through the panties, band and skirt no more skin stands than before, but for one point
        // 0.7 mm over the skirt, from the sag cap.
        InputsAreTheMeasuredOnes();
        int before = StarlightMeasure.LeftOutInAuthorsHole(runs.Body, runs.Model(Before));
        int now = StarlightMeasure.LeftOutInAuthorsHole(runs.Body, runs.Model());
        int outBefore = StarlightMeasure.SkinOutOffStockings(runs.Model(Before));
        int outNow = StarlightMeasure.SkinOutOffStockings(runs.Model());
        o.WriteLine($"skin left out in the author's crotch hole: {now} (before the fix {before}); "
                    + $"skin through the panties, band and skirt: {outNow} (before {outBefore})");
        int waistBefore = StarlightMeasure.LeftOutAboveLegs(runs.Body, runs.Model(Before));
        int waistNow = StarlightMeasure.LeftOutAboveLegs(runs.Body, runs.Model());
        o.WriteLine($"left out at the waist edge: {waistNow} (before {waistBefore})");
        Assert.True(before > 0, "the author's crotch hole cut nothing: the test checks nothing");
        Assert.Equal(before, now);
        Assert.True(waistNow >= waistBefore, $"the author's waist edge lost {waistBefore - waistNow} triangles");
        Assert.True(outNow <= outBefore + CrotchSkinOutSlack, $"{outNow} skin points through the panties, band and skirt");
    }

    [LocalDataFact(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.TreMod, LocalData.UvMaps)]
    [Trait("Plan", "AC-starlight-bottoms-1")]
    [Trait("Plan", "EDGE-starlight-bottoms-5")]
    public void The_stockings_do_not_fold_on_Tre_Medium()
    {
        InputsAreTheMeasuredOnes();
        var before = StarlightMeasure.TurnedOver(runs.Original, runs.Model(Before));
        var now = StarlightMeasure.TurnedOver(runs.Original, runs.Model());
        foreach (string part in StarlightMeasure.LegsParts(runs.Original))
        {
            o.WriteLine($"{part}: turned over {now.GetValueOrDefault(part)} (before the fix {before.GetValueOrDefault(part)})");
            Assert.True(now.GetValueOrDefault(part) <= before.GetValueOrDefault(part), $"part {part} folds more");
        }
    }

    [LocalDataFact(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.TreMod, LocalData.UvMaps)]
    [Trait("Plan", "AC-starlight-bottoms-5")]
    [Trait("Plan", "EDGE-starlight-bottoms-14")]
    public void The_stockings_move_in_line_on_Tre_Medium()
    {
        // The skirt and the ring: no more out of line than before, by 0.5 mm. The coarse parts are lifted face by face (the
        // fix), their corners 20-80 mm apart: their figure is a slope (see CoarseSlopeSlack).
        InputsAreTheMeasuredOnes();
        var stockings = StarlightMeasure.CoarseParts(runs.Original);
        foreach (string part in StarlightMeasure.LegsParts(runs.Original).OrderBy(x => x))
        {
            bool stocking = stockings.Contains(part);
            var before = StarlightMeasure.OutOfLine(runs.Original, runs.Model(Before), [part], perLength: stocking);
            // The 1501 fix as it was: the knee pass moves the stockings by design and is bounded against its knob
            // instead (StarlightBottomsDiagnosticTests.Knee_pass_per_size, AC-knee-standoff-4).
            var now = StarlightMeasure.OutOfLine(runs.Original, runs.Model("NoKneeStandoff"), [part], perLength: stocking);
            float slack = stocking ? CoarseSlopeSlack : OutOfLineSlack;
            string unit = stocking ? "" : " mm";
            float k = stocking ? 1f : 1000f;
            o.WriteLine($"{part}{(stocking ? " (coarse, slope)" : "")}: max {now.Max * k:F3}{unit}, p99 {now.P99 * k:F3}{unit} "
                        + $"(before the fix {before.Max * k:F3}, {before.P99 * k:F3})");
            Assert.True(now.Max <= before.Max + slack, $"part {part}: max {now.Max * k:F3}{unit}");
            Assert.True(now.P99 <= before.P99 + slack, $"part {part}: p99 {now.P99 * k:F3}{unit}");
        }
    }
}
