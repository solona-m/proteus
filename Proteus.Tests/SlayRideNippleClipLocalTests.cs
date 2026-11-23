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
/// Slay Ride's top, made on Neolithe SFW Almond XS, sized onto bigger busts: the bodice stays in front of the nipples.
/// Reported in game 2026-11-23 — on Rue+ Yiggle Medium and Large a disc of skin showed through the bodice around each
/// nipple (12.9 / 18.6 mm deep), because the layer order's cover lift, faded off the fur trim onto the bodice, drove it
/// into the breast. Refit headless as the size set refits (the CLI's output matched the in-game files within 0.9 mm).
/// </summary>
[Trait("Speed", "Slow")]
public sealed class SlayRideNippleClipLocalTests(SlayRideNippleClipLocalTests.Runs runs, ITestOutputHelper o)
    : IClassFixture<SlayRideNippleClipLocalTests.Runs>
{
    internal const string Garment = "items/Top/chara/equipment/e0231/model/c0201e0231_top.mdl";
    internal const string MadeOn = "extra chest - smallclothes\\sfw almond xs.mdl";
    internal const string NeolitheL = "extra chest - smallclothes\\sfw almond l.mdl";
    internal static string Rue(string size) => $"files\\chest - smallclothes\\yiggle - {size}\\chara\\equipment\\e0000\\model\\c0201e0000_top.mdl";

    /// <summary>The inputs the references below were measured on (SHA-256): a change of any is a re-measure, not a pass.</summary>
    internal static readonly (string Mod, string Rel, string Sha)[] Inputs =
    [
        (LocalData.SlayRideMod, Garment, "e58872f7d73474ce856867d554726ff8b8b289cc9ce9ffefc1377ac7f9d8135b"),
        (LocalData.NeolitheMod, MadeOn, "309edc33afd6bc867298fcbcd1123ce8f0cdb1e58c659ecce8cce6882b57bcc3"),
        (LocalData.NeolitheMod, NeolitheL, "d8f3eb526bcd95b7254230fc6cbb3409701d6f965e722c98ee8fff42bf983885"),
        (LocalData.RueMod, Rue("small"), "d4a3b5be47f878063577db075e0f32990cb6f6590cf8ad1c5624850b7c603e76"),
        (LocalData.RueMod, Rue("medium"), "d276bc2b5e621b8f5ddb14291ebc511436ddfdf28d760f5ad947b0b87a6b6415"),
        (LocalData.RueMod, Rue("large"), "2eb22e26bb501c643bc3743c1025da8653e476e1bc62183b09db9063a2f3a323"),
    ];

    /// <summary>
    /// Per size, measured on these inputs with the layer order off (NoLayerOrder) and with the floor off (NoLayerFloor —
    /// the builds before this fix): the deepest skin in front of the bodice parts (mm); turned-over triangles of the
    /// bodice (1.1) and of the fur roll it ends in (1.3); the 332 fur-over-bodice card pairs whose card ended more than
    /// 0.5 mm behind the cloth, and the worst card behind (mm). With the floor (2026-11-23): RueS 1.1 mm, RueM 1.6,
    /// RueL 2.6, NeoL 2.6.
    /// </summary>
    internal static readonly Dictionary<string, (float NoLayerOrder, float Before, int BodiceTurnedBefore, int RollTurnedBefore,
                                                 int CardsBehindBefore, float WorstCardBefore)> References = new()
    {
        ["RueS"] = (1.2f, 1.1f, 6, 656, 65, 6.7f),
        ["RueM"] = (2.6f, 12.9f, 7, 1866, 100, 11.2f),
        ["RueL"] = (2.4f, 18.6f, 23, 3564, 130, 26.7f),
        ["NeoL"] = (3.4f, 4.1f, 12, 1464, 91, 14.9f),
    };

    /// <summary>The fur roll (1.3) is a crumpled volume that already turns thousands of its triangles in every refit; the
    /// floor may add up to this share of them (it added 5-17: 0.3-1.2%). The bodice gets none.</summary>
    internal const float RollTurnSlack = 0.02f;

    /// <summary>Card pairs, of 332, that may newly end behind the cloth (the floor moved 1-2).</summary>
    internal const int CardSlack = 2;

    /// <summary>The bodice parts: the cloth the author laid on the skin (no deeper than 2 mm through it on the original) —
    /// the fur trim's cards, rooted 27-33 mm inside, are not.</summary>
    internal const float LaidOn = 0.002f;

    /// <summary>The fur cards' part.</summary>
    internal const string Cards = "1.2";

    public sealed class Runs : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "proteus-slayride-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, ModelParts> models = new();
        public ModelParts Original { get; }

        public Runs()
        {
            Original = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(LocalData.Path(LocalData.SlayRideMod), Garment)))!;
        }

        /// <summary>Rue+ Yiggle Medium and Large refit once, with the given --tune knobs (none for the build as it is).
        /// Rue+ Yiggle Small and Neolithe L, which were clear or nearly so, are measured on demand (see
        /// <see cref="SlayRideNippleClipDiagnosticTests"/>): the full suite has no room for two more refits.</summary>
        public ModelParts Model(string label, string tune = "")
        {
            string key = tune + "|" + label;
            if (models.TryGetValue(key, out var m)) return m;
            RunAll(tune);
            return models[key];
        }

        private void RunAll(string tune)
        {
            string outDir = Path.Combine(Dir, tune.Length == 0 ? "as-is" : tune);
            Directory.CreateDirectory(outDir);
            string garment = Path.Combine(LocalData.Path(LocalData.SlayRideMod), Garment);
            var common = new List<string>
            {
                "--garment", garment, "--from-root", LocalData.Path(LocalData.NeolitheMod), "--from", MadeOn,
                "--uvmaps", LocalData.Path(LocalData.UvMaps), "--whole", "--out-dir", outDir,
            };
            if (tune.Length > 0) common.AddRange(["--tune", tune]);
            Run([.. common, "--body-root", LocalData.Path(LocalData.RueMod),
                 "--to", $"{Rue("medium")}=RueM,{Rue("large")}=RueL"], "rue");
            foreach (var label in new[] { "RueM", "RueL" })
                models[tune + "|" + label] = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(outDir, label + ".mdl")))!;

            void Run(string[] args, string name)
            {
                var stdout = new StringWriter();
                var stderr = new StringWriter();
                var console = Console.Out;
                int exit;
                Console.SetOut(stdout);   // the report it prints is not read here
                try { exit = RefitCli::Proteus.Refit.Program.Dispatch(args, stdout, stderr); }
                finally { Console.SetOut(console); }
                if (exit != 0) throw new InvalidOperationException($"refit onto {name} failed: {stderr}");
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void InputsAreTheMeasuredOnes()
    {
        foreach (var (mod, rel, sha) in Inputs)
        {
            string path = Path.Combine(LocalData.Path(mod), rel.Replace('\\', Path.DirectorySeparatorChar));
            string now = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.True(now == sha, $"inputs changed ({rel}): re-measure the references in {nameof(References)}");
        }
    }

    /// <summary>The bodice parts of the original: laid on the skin, not rooted in it.</summary>
    internal static List<string> Bodice(ModelParts original)
        => NippleClipMeasure.Measure(original).Deepest
                            .Where(kv => MathF.Max(kv.Value.Left, kv.Value.Right) <= LaidOn).Select(kv => kv.Key).ToList();

    /// <summary>The bodice in front of both nipples on <paramref name="size"/>: within 3 mm, and within 0.5 mm of what the
    /// layer order off leaves, each side.</summary>
    internal static void BodiceInFront(string size, ModelParts original, ModelParts refit, Action<string> log)
    {
        var bodice = Bodice(original);
        // The bodice itself and the fur roll it ends in are what is measured — never vacuously.
        Assert.Contains("1.1", bodice);
        Assert.DoesNotContain(Cards, bodice);
        var r = NippleClipMeasure.Measure(refit);
        Assert.True(r.SkinVerticesRead > 100, $"only {r.SkinVerticesRead} skin vertices read around the nipples");
        float bar = MathF.Min(0.003f, References[size].NoLayerOrder / 1000f + 0.0005f);
        foreach (var part in bodice)
        {
            var (left, right) = r.Deepest[part];
            log($"{size} {part}: left {left * 1000:F1} mm, right {right * 1000:F1} mm (bar {bar * 1000:F1})");
            Assert.True(left <= bar, $"{size} {part}: skin {left * 1000:F1} mm in front on the left");
            Assert.True(right <= bar, $"{size} {part}: skin {right * 1000:F1} mm in front on the right");
        }
    }

    /// <summary>No more of the bodice's triangles turned over than before the fix; the fur roll within its slack.</summary>
    internal static void NoMoreFolds(string size, ModelParts original, ModelParts refit, Action<string> log)
    {
        var turned = NippleClipMeasure.TurnedOverByPart(original, refit);
        var reference = References[size];
        log($"{size}: bodice {turned["1.1"]} turned over (before {reference.BodiceTurnedBefore}), " +
            $"fur roll {turned["1.3"]} (before {reference.RollTurnedBefore})");
        Assert.True(turned["1.1"] <= reference.BodiceTurnedBefore,
                    $"{size}: {turned["1.1"]} bodice triangles turned over, before {reference.BodiceTurnedBefore}");
        Assert.True(turned["1.3"] <= reference.RollTurnedBefore * (1f + RollTurnSlack),
                    $"{size}: {turned["1.3"]} fur roll triangles turned over, before {reference.RollTurnedBefore}");
    }

    /// <summary>The fur cards the author laid over the bodice near the nipples no more behind it than before the fix.</summary>
    internal static void FurInFront(string size, ModelParts original, ModelParts refit, Action<string> log)
    {
        var pairs = NippleClipMeasure.CardPairs(original, Bodice(original), Cards);
        Assert.True(pairs.Count > 20, $"only {pairs.Count} fur-over-bodice pairs near the nipples");
        var behind = NippleClipMeasure.CardsBehind(original, refit, pairs, Cards);
        int far = behind.Count(b => b > 0.0005f);
        float worst = behind.Max();
        var reference = References[size];
        log($"{size}: {pairs.Count} pairs, {far} cards > 0.5 mm behind the cloth (before {reference.CardsBehindBefore}), " +
            $"worst {worst * 1000:F1} mm (before {reference.WorstCardBefore:F1})");
        Assert.True(far <= reference.CardsBehindBefore + CardSlack, $"{size}: {far} cards behind, before {reference.CardsBehindBefore}");
        Assert.True(worst <= reference.WorstCardBefore / 1000f + 0.0002f, $"{size}: worst card {worst * 1000:F1} mm behind");
    }

    [LocalDataTheory(LocalData.SlayRideMod, LocalData.NeolitheMod, LocalData.RueMod, LocalData.UvMaps)]
    [InlineData("RueM")]
    [InlineData("RueL")]
    [Trait("Plan", "AC-bodice-nipple-clip-1")]
    [Trait("Plan", "AC-bodice-nipple-clip-2")]
    [Trait("Plan", "EDGE-bodice-nipple-clip-7")]
    [Trait("Plan", "EDGE-bodice-nipple-clip-8")]
    public void The_bodice_stays_in_front_of_both_nipples(string size)
    {
        InputsAreTheMeasuredOnes();
        BodiceInFront(size, runs.Original, runs.Model(size), o.WriteLine);
    }

    [LocalDataTheory(LocalData.SlayRideMod, LocalData.NeolitheMod, LocalData.RueMod, LocalData.UvMaps)]
    [InlineData("RueM")]
    [InlineData("RueL")]
    [Trait("Plan", "AC-bodice-nipple-clip-6")]
    public void No_size_folds_more_than_before(string size)
    {
        InputsAreTheMeasuredOnes();
        NoMoreFolds(size, runs.Original, runs.Model(size), o.WriteLine);
    }

    [LocalDataTheory(LocalData.SlayRideMod, LocalData.NeolitheMod, LocalData.RueMod, LocalData.UvMaps)]
    [InlineData("RueM")]
    [InlineData("RueL")]
    [Trait("Plan", "AC-bodice-nipple-clip-7")]
    public void The_fur_stays_where_it_was(string size)
    {
        InputsAreTheMeasuredOnes();
        FurInFront(size, runs.Original, runs.Model(size), o.WriteLine);
    }
}
