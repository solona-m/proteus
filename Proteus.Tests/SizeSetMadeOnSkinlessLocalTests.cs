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
/// Tutting (KAN-6) as it ships, unpacked, sized once with "Made on" = Bibo+ for every women's body and the men's, then
/// once more. Its pants are one cloth mesh with no skin: before KAN-6 no size of them was made.
/// </summary>
public sealed class DanceRun : IDisposable
{
    internal static readonly string[] Bodies =
        [Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse, Data.TbseX];

    internal static readonly string[] Needs =
        [Data.DancePack, .. Bodies, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps];

    internal string Copy { get; } = "";
    internal List<(string Group, List<string> Options, int? Default)> Before { get; } = [];
    internal List<SizeSetRefit.Piece> Pieces { get; } = [];
    internal SizeSetRefit.RunLog First { get; } = new();
    internal SizeSetRefit.RunLog Second { get; } = new();
    internal string Log { get; } = "";

    public DanceRun()
    {
        if (LocalData.Missing(Needs) != null) return;
        Copy = Data.UnpackedPack(Data.DancePack);
        PenumbraModMeta.MigrateWorkCopy(Copy, LocalData.Path(LocalData.Mods));
        Before = Data.Shape(Copy);
        var readGame = Data.Game();
        Pieces = SizeSetRefit.PiecesOf(Copy, readGame);
        var said = new StringWriter();
        First = Data.Batch(Copy, SizeSetBatch.WomensKeys, men: true, Bodies, readGame: readGame,
                           uvmaps: LocalData.Path(LocalData.TbseUvMaps), madeOn: Data.Folder(Data.Bibo),
                           log: RefitCli::Proteus.Refit.ConsoleLog.To(said)).Log;
        Second = Data.Batch(Copy, SizeSetBatch.WomensKeys, men: true, Bodies, readGame: readGame,
                            uvmaps: LocalData.Path(LocalData.TbseUvMaps), madeOn: Data.Folder(Data.Bibo)).Log;
        Log = said.ToString();
    }

    public void Dispose() => Data.Delete(Copy);
}

public class SizeSetMadeOnSkinlessLocalTests(ITestOutputHelper output, DanceRun dance) : IClassFixture<DanceRun>
{
    private const string Pants = "chara/equipment/e6118/model/c0201e6118_dwn.mdl";
    private const string Top = "chara/equipment/e6118/model/c0201e6118_top.mdl";
    private static readonly string[] AuthorSizes = ["Small", "Medium", "Large"];
    private static readonly string[] WomensKeys = ["neolithe", "rue", "yab", "yab-buff", "tre", "rosaline"];

    /// <summary>
    /// Step 0 of KAN-6, measured with the CLI's refit across the sexes on a build off test 908cf4f3: Tutting's Pant Size
    /// Small from Bibo+ "type a - small" legs. TBSE's SFW legs matched 0.8833 (refused), its Gen A legs 0.9869 with 40
    /// folds; TBSE-X's SFW legs were refused by texture layout (62%), its Flaccid M A legs 0.9869 with 32 folds.
    /// </summary>
    private const double MeasuredNudeLegsCoverage = 0.9869;

    private static bool Is(SizeSetRefit.SizeOutcome s, string gamePath, bool men = false)
        => s.Men == men && s.GamePath.Equals(men ? RacialModelBake.WithRace(gamePath, CrossSexRefit.Men) : gamePath,
                                             StringComparison.OrdinalIgnoreCase);

    private void Print(SizeSetRefit.RunLog log)
    {
        foreach (var s in log.Sizes)
            output.WriteLine($"{s.Slot} {(s.Men ? "men " : "")}{s.Body} {s.Option} {s.Status} {s.ReasonCode} cut from {s.CutFrom}"
                             + (s.Measures is { } m ? $" folded {m.Folded} move {m.WorstMoveMm:F1} cover {m.Coverage:F4}" : "")
                             + (s.Reason is { } r ? $" — {r}" : ""));
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-1")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-11")]
    [Trait("Speed", "Slow")]
    public void Tutting_pants_made_on_Bibo_gain_every_other_body() // [AC-kan-6-skinless-made-on-1] [EDGE-kan-6-skinless-made-on-11]
    {
        Print(dance.First);
        var pants = dance.First.Sizes.Where(s => Is(s, Pants)).ToList();
        foreach (string key in WomensKeys)
        {
            if (SupportedBodies.Of(key, "_dwn", false) is not { } table) continue;
            foreach (var size in table.Sizes)
            {
                string option = SupportedBodies.OptionName(table, size);
                var made = pants.Where(s => s.Body == key && s.Option == option && s.Status == SizeStatus.Made).ToList();
                Assert.True(made.Count == 1, $"{option}: {made.Count} made");
                Assert.Contains(made[0].CutFrom, AuthorSizes);
            }
        }
        // Bibo's own sizes are the author's own, named by the option.
        foreach (var (letter, word) in new[] { ("S", "Small"), ("M", "Medium"), ("L", "Large") })
            Assert.Contains(pants, s => s.Body == "bibo" && s.Option == "Bibo " + letter
                                        && s.ReasonCode == SizeSetRefit.Codes.AuthorsOwn && s.Reason!.Contains($"\"{word}\""));
        Assert.DoesNotContain(pants, s => s.ReasonCode is SizeSetRefit.Codes.NotMadeOn or SizeSetRefit.Codes.Unreadable);
        Data.AssertOnlyAppended(dance.Before, Data.Shape(dance.Copy));
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-1")]
    [Trait("Plan", "AC-kan-6-skinless-made-on-3")]
    [Trait("Speed", "Slow")]
    public void Tutting_pants_sizes_are_valid_meshes() // [AC-kan-6-skinless-made-on-1] [AC-kan-6-skinless-made-on-3]
    {
        // The author's Small pants, read as the game would: the fold budget is 1% of its triangles. Never raised: a
        // size over it is a finding to look into.
        string small = Directory.GetFiles(dance.Copy, "c0201e6118_dwn.mdl", SearchOption.AllDirectories)
                                .First(f => f.Replace('\\', '/').Contains("/pant size/small/", StringComparison.OrdinalIgnoreCase));
        int triangles = ModelPartReader.Read(File.ReadAllBytes(small))!.Parts.Sum(p => p.TriangleCount);
        var made = dance.First.Sizes.Where(s => (Is(s, Pants) || Is(s, Pants, men: true)) && s.Status == SizeStatus.Made).ToList();
        Assert.NotEmpty(made);
        foreach (var s in made)
        {
            var m = s.Measures;
            Assert.NotNull(m);
            output.WriteLine($"{(s.Men ? "men " : "")}{s.Option}: {m!.Folded} of {triangles} folded, moved up to {m.WorstMoveMm:F1} mm");
            if (m.WorstMoveMm is { } move) Assert.True(float.IsFinite(move) && move < 100f, $"{s.Option}: moved {move} mm");
            Assert.True(m.Folded <= triangles / 100, $"{s.Option}: {m.Folded} folded of {triangles}");
        }
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-2")]
    [Trait("Speed", "Slow")]
    public void Tutting_larger_sizes_never_come_from_smaller_author_sizes() // [AC-kan-6-skinless-made-on-2]
    {
        // Observed on Tutting; a failure is a re-plan, never a looser assertion.
        int CutFrom(string option)
            => Array.IndexOf(AuthorSizes, dance.First.Sizes.Single(s => Is(s, Pants) && s.Option == option
                                                                         && s.Status == SizeStatus.Made).CutFrom);
        var neo = new[] { "Neolithe S", "Neolithe M", "Neolithe L" }.Select(CutFrom).ToList();
        output.WriteLine($"Neolithe S/M/L from {string.Join(", ", neo)}; YAB WC/SC from {CutFrom("YAB WC")}, {CutFrom("YAB SC")}");
        Assert.Equal(neo.OrderBy(i => i), neo);
        Assert.True(CutFrom("YAB WC") <= CutFrom("YAB SC"));
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-3")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-17")]
    [Trait("Speed", "Slow")]
    public void Tutting_pants_are_made_for_men_on_the_nude_legs() // [AC-kan-6-skinless-made-on-3] [EDGE-kan-6-skinless-made-on-17]
    {
        // A refusal here means the men's ask is not delivered: reported and filed, never a relaxed test.
        foreach (var (key, label) in new[] { ("tbse", "TBSE"), ("tbse-x", "TBSE-X") })
        {
            var legs = dance.First.Sizes.Where(s => Is(s, Pants, men: true) && s.Body == key).ToList();
            Assert.NotEmpty(legs);
            Assert.DoesNotContain(legs, s => s.ReasonCode is SizeSetRefit.Codes.NotMadeOn or SizeSetRefit.Codes.Unreadable);
            var made = Assert.Single(legs, s => s.Status == SizeStatus.Made);
            Assert.Equal(label, made.Option);
            double coverage = made.Measures!.Coverage!.Value;
            output.WriteLine($"{label} from {made.CutFrom}: coverage {coverage:F4}");
            Assert.True(coverage >= 0.90, $"{label}: {coverage}");
            if (made.CutFrom == "Small") Assert.Equal(MeasuredNudeLegsCoverage, coverage, 0.001);
        }
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-4")]
    [Trait("Speed", "Slow")]
    public void Tutting_check_in_game_says_the_sizes_were_taken_by_name() // [AC-kan-6-skinless-made-on-4]
    {
        string bibo = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods), [Data.Folder(Data.Bibo)])[0].Name;
        var pants = dance.Pieces.Single(p => p.GamePath.Equals(Pants, StringComparison.OrdinalIgnoreCase));
        var top = dance.Pieces.Single(p => p.GamePath.Equals(Top, StringComparison.OrdinalIgnoreCase));
        foreach (string w in dance.First.Warnings) output.WriteLine(w);
        Assert.Single(dance.First.Warnings, w => w == string.Format(Strings.SizeSet.MadeOnTrustedFmt, pants.Label, bibo));
        Assert.DoesNotContain(dance.First.Warnings, w => w == string.Format(Strings.SizeSet.MadeOnTrustedFmt, top.Label, bibo));
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-6")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-20")]
    [Trait("Speed", "Slow")]
    public void Tutting_top_is_sized_as_before() // [AC-kan-6-skinless-made-on-6] [EDGE-kan-6-skinless-made-on-20]
    {
        var top = dance.First.Sizes.Where(s => Is(s, Top) && s.Status == SizeStatus.Made).ToList();
        foreach (string key in WomensKeys) Assert.Contains(top, s => s.Body == key);
        foreach (string key in new[] { "tbse", "tbse-x" })
            Assert.Contains(dance.First.Sizes, s => Is(s, Top, men: true) && s.Body == key && s.Status == SizeStatus.Made);
        Assert.DoesNotContain(dance.First.Sizes, s => Is(s, Top) && s.ReasonCode is SizeSetRefit.Codes.NotMadeOn);
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Neolithe, Data.Rue, Data.Yab, Data.TreYas, Data.Rosaline, Data.Tbse,
                   Data.TbseX, LocalData.GameData, LocalData.UvMaps, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-12")]
    [Trait("Speed", "Slow")]
    public void Second_Tutting_run_adds_nothing() // [EDGE-kan-6-skinless-made-on-12]
    {
        Assert.Equal(0, dance.Second.Made);
        Assert.All(dance.Second.Sizes.Where(s => Is(s, Pants)),
                   s => Assert.True(s.ReasonCode is SizeSetRefit.Codes.AlreadyThere or SizeSetRefit.Codes.AuthorsOwn,
                                    $"{s.Option}: {s.ReasonCode}"));
    }

    private static string MigratedDance()
    {
        string copy = Data.UnpackedPack(Data.DancePack);
        PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
        return copy;
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-6")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-8")]
    [Trait("Speed", "Slow")]
    public void Tutting_without_made_on_still_asks_for_it() // [AC-kan-6-skinless-made-on-6] [EDGE-kan-6-skinless-made-on-8]
    {
        string copy = MigratedDance();
        try
        {
            var readGame = Data.Game();
            var pants = SizeSetRefit.PiecesOf(copy, readGame).Single(p => p.GamePath.Equals(Pants, StringComparison.OrdinalIgnoreCase));
            var (_, log) = Data.Batch(copy, ["rue"], men: false, [Data.Bibo, Data.Rue], readGame: readGame);
            Print(log);
            Assert.Contains(log.Sizes, s => Is(s, Pants) && s.ReasonCode == SizeSetRefit.Codes.Unreadable
                                            && s.Reason == string.Format(Strings.SizeSet.UnreadableMadeOnFmt, pants.Label));
            Assert.DoesNotContain(log.Sizes, s => Is(s, Pants) && s.Status == SizeStatus.Made);
        }
        finally { Data.Delete(copy); }
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-5")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-3")]
    [Trait("Speed", "Slow")]
    public void Tutting_pants_with_unnamed_sizes_are_left_out_and_said_so() // [AC-kan-6-skinless-made-on-5] [EDGE-kan-6-skinless-made-on-3]
    {
        string copy = Data.UnpackedPack(Data.DancePack);
        try
        {
            // The Pant Size options renamed to words that give no size, in the pack's own group file (the conversion
            // below folds it into the manifest).
            string group = Directory.GetFiles(copy, "group_*.json")
                                    .Single(f => (string?)JsonNode.Parse(File.ReadAllText(f))!["Name"] == "Pant Size");
            var json = JsonNode.Parse(File.ReadAllText(group))!;
            var names = new Dictionary<string, string> { ["Small"] = "Petite", ["Medium"] = "Regular", ["Large"] = "Curvy" };
            foreach (var option in json["Options"]!.AsArray())
                if (names.TryGetValue((string)option!["Name"]!, out var renamed)) option["Name"] = renamed;
            File.WriteAllText(group, json.ToJsonString());
            PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));

            var readGame = Data.Game();
            var pants = SizeSetRefit.PiecesOf(copy, readGame).Single(p => p.GamePath.Equals(Pants, StringComparison.OrdinalIgnoreCase));
            string bibo = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods), [Data.Folder(Data.Bibo)])[0].Name;
            var (_, log) = Data.Batch(copy, ["rue"], men: false, [Data.Bibo, Data.Rue], readGame: readGame,
                                      madeOn: Data.Folder(Data.Bibo));
            Print(log);
            string note = string.Format(Strings.SizeSet.MadeOnNoSizeNameFmt, pants.Label, bibo);
            var left = Assert.Single(log.Sizes, s => Is(s, Pants));
            Assert.Equal((SizeSetRefit.Codes.NotMadeOn, note), (left.ReasonCode, left.Reason));
            Assert.Contains(note, log.Notes);
            Assert.Contains(log.Sizes, s => Is(s, Top) && s.Body == "rue" && s.Status == SizeStatus.Made);
        }
        finally { Data.Delete(copy); }
    }

    /// <summary>
    /// The paths Tutting itself never takes: a skinless chest, and skinless legs following it. Tutting's top made
    /// skinless (its body material renamed, same length, so the file stays valid) and its pants moved into the top's
    /// group, so they follow it; Made on Bibo+, sized for Rue and Tre.
    /// </summary>
    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Rue, Data.TreYas, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-8")]
    [Trait("Plan", "EDGE-kan-6-skinless-made-on-15")]
    [Trait("Speed", "Slow")]
    public void A_skinless_chest_and_the_legs_following_it_are_placed_by_name() // [AC-kan-6-skinless-made-on-8] [EDGE-kan-6-skinless-made-on-15]
    {
        string copy = Data.UnpackedPack(Data.DancePack);
        try
        {
            byte[] skin = System.Text.Encoding.ASCII.GetBytes("mt_c0201b0001_bibo.mtrl");
            byte[] cloth = System.Text.Encoding.ASCII.GetBytes("mt_c0201x0001_bibo.mtrl");
            foreach (string top in Directory.GetFiles(copy, "c0201e6118_top.mdl", SearchOption.AllDirectories))
            {
                byte[] bytes = File.ReadAllBytes(top);
                int replaced = 0;
                for (int i = bytes.AsSpan().IndexOf(skin); i >= 0; i = bytes.AsSpan().IndexOf(skin))
                {
                    cloth.CopyTo(bytes, i);
                    replaced++;
                }
                Assert.True(replaced > 0, $"{top}: no body material to rename");
                File.WriteAllBytes(top, bytes);
                Assert.DoesNotContain(ModelPartReader.Read(bytes)!.Parts, p => SecondSkinWriter.IsBodySkinMaterial(p.Material));
            }
            // The pants into the top's group: each top size wears the pant size of its name (XLarge the Large).
            string topGroup = Directory.GetFiles(copy, "group_*.json")
                                       .Single(f => (string?)JsonNode.Parse(File.ReadAllText(f))!["Name"] == "Top Size");
            string pantGroup = Directory.GetFiles(copy, "group_*.json")
                                        .Single(f => (string?)JsonNode.Parse(File.ReadAllText(f))!["Name"] == "Pant Size");
            var json = JsonNode.Parse(File.ReadAllText(topGroup))!;
            foreach (var option in json["Options"]!.AsArray())
            {
                string size = ((string)option!["Name"]!).ToLowerInvariant() == "xlarge" ? "large" : ((string)option["Name"]!).ToLowerInvariant();
                option["Files"]![Pants] = $"pant size\\{size}\\chara\\equipment\\e6118\\model\\c0201e6118_dwn.mdl";
            }
            File.WriteAllText(topGroup, json.ToJsonString());
            File.Delete(pantGroup);
            PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));

            var readGame = Data.Game();
            var pieces = SizeSetRefit.PiecesOf(copy, readGame);
            var chest = pieces.Single(p => p.GamePath.Equals(Top, StringComparison.OrdinalIgnoreCase));
            var legs = pieces.Single(p => p.GamePath.Equals(Pants, StringComparison.OrdinalIgnoreCase));
            Assert.True(legs.FollowsChest);
            var said = new StringWriter();
            var (_, log) = Data.Batch(copy, ["bibo", "rue", "tre"], men: false, [Data.Bibo, Data.Rue, Data.TreYas],
                                      readGame: readGame, madeOn: Data.Folder(Data.Bibo),
                                      log: RefitCli::Proteus.Refit.ConsoleLog.To(said));
            Print(log);
            string text = said.ToString();
            string bibo = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods), [Data.Folder(Data.Bibo)])[0].Name;

            // The chest, placed by name: Bibo's own sizes are the author's, the others are cut from them, and each hem
            // follows the legs its Bibo size is worn with.
            Assert.Contains(string.Format(Strings.SizeSet.MadeOnTrustedFmt, chest.Label, bibo), log.Warnings);
            foreach (string option in new[] { "Bibo S", "Bibo M", "Bibo L" })
                Assert.Contains(log.Sizes, s => Is(s, Top) && s.Option == option && s.ReasonCode == SizeSetRefit.Codes.AuthorsOwn);
            var madeChest = log.Sizes.Where(s => Is(s, Top) && s.Status == SizeStatus.Made).ToList();
            Assert.Contains(madeChest, s => s.Body == "rue");
            Assert.Contains(madeChest, s => s.Body == "tre");
            foreach (var s in madeChest)
                Assert.True(text.Split('\n').Any(l => l.Contains($"size set: {chest.Label} {s.Option}: hips:", StringComparison.Ordinal)),
                            $"{s.Option}: the hem's hips were not moved");

            // The legs follow it, placed through the chest table: "Medium" is the legs Bibo M is worn with.
            Assert.Contains("(the legs of Bibo M)", text);
            Assert.Contains(string.Format(Strings.SizeSet.MadeOnTrustedFmt, legs.Label, bibo), log.Warnings);
            Assert.Contains(log.Sizes, s => Is(s, Pants) && s.Body == "rue" && s.Status == SizeStatus.Made);
            Assert.Contains(log.Sizes, s => Is(s, Pants) && s.Body == "tre" && s.Status == SizeStatus.Made);
            // On Bibo itself the chest made nothing, so its legs have nothing to join: never remade from the author's own.
            Assert.DoesNotContain(log.Sizes, s => Is(s, Pants) && s.Body == "bibo" && s.Status == SizeStatus.Made);
        }
        finally { Data.Delete(copy); }
    }

    [LocalDataFact(Data.DancePack, Data.Bibo, Data.Rue, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-kan-6-skinless-made-on-7")]
    [Trait("Speed", "Slow")]
    public void Cli_made_on_sizes_Tutting_pants_and_reports_it() // [AC-kan-6-skinless-made-on-7]
    {
        string copy = Data.UnpackedPack(Data.DancePack);
        string modsRoot = Data.ModsRootWith(Data.Bibo, Data.Rue);
        string report = Path.Combine(copy, "..", Path.GetFileName(copy) + "-report.json");
        try
        {
            var stdout = new StringWriter();
            int exit = RefitCli::Proteus.Refit.Program.Dispatch(
                ["--size-set", copy, "--bodies", "bibo,rue", "--mods-root", modsRoot, "--game", LocalData.Path(LocalData.GameData),
                 "--uvmaps", LocalData.Path(LocalData.UvMaps), "--made-on", Data.Folder(Data.Bibo), "--report", report],
                stdout, TextWriter.Null);
            Assert.Equal(0, exit);
            var json = JsonNode.Parse(File.ReadAllText(report))!;
            string text = json.ToJsonString();
            output.WriteLine(text[..Math.Min(4000, text.Length)]);
            Assert.True((int)json["made"]! > 0);
            var sizes = json["sizes"]!.AsArray().OfType<JsonObject>().ToList();
            Assert.Contains(sizes, s => ((string?)s["gamePath"])?.Equals(Pants, StringComparison.OrdinalIgnoreCase) == true
                                        && (string?)s["option"] == "Rue WC" && (string?)s["status"] == "made");
        }
        finally
        {
            Data.Delete(copy);
            if (File.Exists(report)) File.Delete(report);
            Data.DeleteModsRoot(modsRoot);
        }
    }
}
