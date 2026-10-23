extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>The real mods the headless size set's tests read, by their folders under <see cref="LocalData.Mods"/>.</summary>
internal static class SizeSetBatchData
{
    internal const string ThisOldThing = LocalData.Mods + ":This Old Thing - by Solona";
    internal const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    internal const string NeolitheYas = LocalData.Mods + ":Neolithe YAS AIO";
    internal const string Neolithe4k = LocalData.Mods + ":Neolithe 4k";
    internal const string Bibo = LocalData.Mods + ":Bibo+";
    internal const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    internal const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    internal const string Rosaline = LocalData.Mods + ":hs-Rosaline-1.1.2-hM5N";
    internal const string TreYas = LocalData.Mods + ":hs-[YAS] Tre-5.5.3-8xB";
    internal const string Tbse = LocalData.Mods + ":The Body SE";
    internal const string TbseX = LocalData.Mods + ":The Body SE-X IVCS2 V1_1_0";
    internal const string Sheer = LocalData.Mods + ":BiboPlus Sheer Elegance";
    internal const string TwillTrousers = LocalData.Mods + ":-Twill Trousers- [YAB, TBSE, TBSE-X]";
    internal const string HellForLeather = LocalData.Mods + ":Hell for Leather - by Solona";
    internal const string Heartthrob = LocalData.Mods + ":Heartthrob - by Solona";
    internal const string ScarletPack = LocalData.ModPacks + ":Scarlet - by Solona.pmp";

    internal static string Folder(string need) => Path.GetFileName(LocalData.Path(need));

    internal static string NewCopy() => Path.Combine(Path.GetTempPath(), "proteus-sizeset-batch-" + Guid.NewGuid().ToString("N"));

    /// <summary>A copy of a mod's manifests and models, outside the mod folder: what the CLI sizes.</summary>
    internal static string CopyOf(string need, bool withRecord = true)
    {
        string copy = NewCopy();
        SizeSetRefitTests.CopyModels(LocalData.Path(need), copy, withRecord);
        return copy;
    }

    /// <summary>Every group and its options, in order: what a size set may only add to.</summary>
    internal static List<(string Group, List<string> Options, int? Default)> Shape(string root)
        => (PenumbraModMeta.TryReadGroups(root) ?? [])
           .Select(g => (g.Name, PenumbraModMeta.ReadOptionNames(g.Group),
                         g.Group.TryGetProperty("DefaultSettings", out var d) && d.ValueKind == JsonValueKind.Number ? (int?)d.GetInt32() : null))
           .ToList();

    /// <summary>The groups and options before are the first of those after, defaults alike; an author's group gains no "Original".</summary>
    internal static void AssertOnlyAppended(List<(string Group, List<string> Options, int? Default)> before,
                                            List<(string Group, List<string> Options, int? Default)> after)
    {
        Assert.True(after.Count >= before.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Group, after[i].Group);
            Assert.Equal(before[i].Options, after[i].Options.Take(before[i].Options.Count));
            Assert.Equal(before[i].Default, after[i].Default);
            Assert.DoesNotContain(BodyRetargetWriter.OriginalOption, after[i].Options.Skip(before[i].Options.Count));
        }
    }

    internal static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>Run the batch job straight, over chosen body mods only — the same path the CLI takes, for tests that
    /// need no argument parsing and must not scan the whole mod folder.</summary>
    /// <param name="modsRoot">The mod folder the bodies are under, when not <see cref="LocalData.Mods"/>; then
    /// <paramref name="bodyMods"/> are folder names in it.</param>
    internal static (SizeSetBatch.Outcome Outcome, SizeSetRefit.RunLog Log) Batch(
        string copy, IReadOnlyList<string> keys, bool men, string[] bodyMods, bool ends = true, string? uvmaps = null,
        Func<string, byte[]?>? readGame = null, Action<string>? progress = null, CancellationToken ct = default,
        string? modsRoot = null)
    {
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var runLog = new SizeSetRefit.RunLog();
        var outcome = SizeSetBatch.Run(
            new SizeSetBatch.Inputs(copy, modsRoot ?? LocalData.Path(LocalData.Mods), keys, men, ends, readGame ?? (_ => null),
                                    new UVRemapService(log, uvmaps ?? LocalData.Path(LocalData.UvMaps)), log,
                                    ModDirs: modsRoot == null ? bodyMods.Select(Folder).ToList() : bodyMods),
            runLog, progress ?? (_ => { }), ct);
        return (outcome, runLog);
    }

    /// <summary>A folder standing in for Penumbra's mod folder: each named body mod linked in, read only.</summary>
    internal static string ModsRootWith(params string[] needs)
    {
        string root = NewCopy();
        Directory.CreateDirectory(root);
        foreach (string need in needs)
        {
            var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{Path.Combine(root, Folder(need))}\" \"{LocalData.Path(need)}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            link.WaitForExit();
            Assert.Equal(0, link.ExitCode);
        }
        return root;
    }

    /// <summary>Remove a stand-in mod folder: its links first, which must never be followed into the real mods.</summary>
    internal static void DeleteModsRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string dir in Directory.GetDirectories(root))
            if (new DirectoryInfo(dir).LinkTarget != null || new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(dir);
        Directory.Delete(root, recursive: true);
    }

    internal static Func<string, byte[]?> Game()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return path =>
        {
            try { return data.GetFile(path)?.Data; }
            catch (Exception) { return null; }
        };
    }
}

/// <summary>
/// This Old Thing — Neolithe XS to L and its own Rue M and L — sized once through the CLI over the whole mod folder,
/// and once more straight through <see cref="SizeSetRefit.Run"/> built as the panel builds it, for the tests that read
/// those runs.
/// </summary>
public sealed class ThisOldThingRun : IDisposable
{
    internal const string Keys = "neolithe,rue,bibo,rosaline";
    internal string CliCopy { get; } = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
    internal string PanelCopy { get; } = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
    internal List<(string Group, List<string> Options, int? Default)> Before { get; }
    internal byte[] ManifestBefore { get; }
    internal JsonNode Report { get; }
    internal int Exit { get; }
    internal JsonNode SecondReport { get; }
    internal byte[] ManifestAfterFirst { get; }
    internal byte[] ManifestAfterSecond { get; }
    internal Dictionary<string, (DateTime, int)> BodyModsBefore { get; }
    internal Dictionary<string, (DateTime, int)> BodyModsAfter { get; }
    internal string? Skipped { get; }

    public ThisOldThingRun()
    {
        Before = SizeSetBatchData.Shape(CliCopy);
        ManifestBefore = File.ReadAllBytes(Path.Combine(CliCopy, PenumbraModMeta.MetaFile));
        Skipped = LocalData.Missing([SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue,
                                     SizeSetBatchData.Bibo, SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps]);
        BodyModsBefore = BodyModStamps();
        if (Skipped != null)
        {
            Report = SecondReport = new JsonObject();
            ManifestAfterFirst = ManifestAfterSecond = [];
            BodyModsAfter = BodyModsBefore;
            return;
        }
        string[] Args(string copy)
            => ["--size-set", copy, "--bodies", Keys, "--mods-root", LocalData.Path(LocalData.Mods),
                "--game", LocalData.Path(LocalData.GameData), "--uvmaps", LocalData.Path(LocalData.UvMaps)];
        var stdout = new StringWriter();
        Exit = RefitCli::Proteus.Refit.Program.Dispatch(Args(CliCopy), stdout, TextWriter.Null);
        Report = JsonNode.Parse(stdout.ToString())!;
        ManifestAfterFirst = File.ReadAllBytes(Path.Combine(CliCopy, PenumbraModMeta.MetaFile));

        var again = new StringWriter();
        RefitCli::Proteus.Refit.Program.Dispatch(Args(CliCopy), again, TextWriter.Null);
        SecondReport = JsonNode.Parse(again.ToString())!;
        ManifestAfterSecond = File.ReadAllBytes(Path.Combine(CliCopy, PenumbraModMeta.MetaFile));

        // The panel's run on the other copy: the same bodies, keys in the order it offers them, its defaults.
        var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
        var readGame = SizeSetBatchData.Game();
        var pieces = SizeSetRefit.PiecesOf(PanelCopy, readGame, includeEnds: true);
        var keys = SizeSetBatch.RunKeys(pieces, SizeSetBatch.ParseKeys(Keys, out _)!, men: false);
        var gameBodies = SizeSetRefit.GameBodies(keys, readGame, ends: true);
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        SizeSetRefit.Run(new SizeSetRefit.Request(PanelCopy, keys, Yas: true, Remake: false, bodies, readGame, Ends: true,
                                                  race => gameBodies.GetValueOrDefault(race), Piercings: false),
                         new UVRemapService(log, LocalData.Path(LocalData.UvMaps)), log, _ => { }, CancellationToken.None);
        BodyModsAfter = BodyModStamps();
    }

    /// <summary>Each body mod's manifest time and file count.</summary>
    private static Dictionary<string, (DateTime, int)> BodyModStamps()
        => new[] { SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo, SizeSetBatchData.Rosaline }
           .Select(LocalData.Path).Where(Directory.Exists)
           .ToDictionary(d => d, d => (File.GetLastWriteTimeUtc(Path.Combine(d, PenumbraModMeta.MetaFile)),
                                       Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length));

    public void Dispose()
    {
        SizeSetBatchData.Delete(CliCopy);
        SizeSetBatchData.Delete(PanelCopy);
    }
}

/// <summary>The headless size set on real mods and bodies (<c>Proteus.Refit --size-set</c>).</summary>
public class SizeSetBatchLocalTests(ThisOldThingRun tot, ITestOutputHelper output) : IClassFixture<ThisOldThingRun>
{
    private static readonly string[] TotNeeds =
        [SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
         SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps];

    private IEnumerable<JsonNode> Sizes(JsonNode report) => report["sizes"]!.AsArray().Select(s => s!);

    private void Print(JsonNode report)
    {
        foreach (var s in Sizes(report))
            output.WriteLine($"{s["piece"]} {s["option"]} {s["status"]} {s["reasonCode"]} {s["reason"]} folded={s["folded"]}");
    }

    // ── ThisOldThingRun ──

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-1")]
    public void Cli_matches_the_panel_run_byte_for_byte() // [AC-headless-size-set-1]
    {
        Assert.Null(tot.Skipped);
        Print(tot.Report);
        Assert.Equal(0, tot.Exit);
        Assert.Equal(SizeSetBatchData.Shape(tot.PanelCopy), SizeSetBatchData.Shape(tot.CliCopy).Select(g => g).ToList(),
                     new ShapeComparer());
        // Every option's files, and the bytes of every file a refit wrote.
        var cliRedirects = PenumbraModMeta.ReadAllRedirects(tot.CliCopy).Select(r => (r.Source, r.GamePath, RefitCore.Normal(r.File))).Order().ToList();
        var panelRedirects = PenumbraModMeta.ReadAllRedirects(tot.PanelCopy).Select(r => (r.Source, r.GamePath, RefitCore.Normal(r.File))).Order().ToList();
        Assert.Equal(panelRedirects, cliRedirects);
        string sub = Path.Combine(tot.CliCopy, BodyRetargetWriter.Subfolder);
        Assert.True(Directory.Exists(sub));
        foreach (string file in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(tot.CliCopy, file);
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(tot.PanelCopy, rel))), rel);
        }
    }

    private sealed class ShapeComparer : IEqualityComparer<(string Group, List<string> Options, int? Default)>
    {
        public bool Equals((string Group, List<string> Options, int? Default) a, (string Group, List<string> Options, int? Default) b)
            => a.Group == b.Group && a.Options.SequenceEqual(b.Options) && a.Default == b.Default;
        public int GetHashCode((string Group, List<string> Options, int? Default) g) => g.Group.GetHashCode();
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-1")]
    public void Cli_writes_into_the_given_folder_through_the_writer() // [AC-headless-size-set-1]
    {
        var made = Sizes(tot.Report).Where(s => (string)s["status"]! == "made").ToList();
        Assert.NotEmpty(made);
        var record = BodyRetargetWriter.ReadRecord(tot.CliCopy)!;
        foreach (var s in made)
        {
            string option = (string)s["option"]!;
            var entry = Assert.Single(record.Options, e => e.Name == option && e.GamePath == (string)s["gamePath"]!);
            Assert.StartsWith(BodyRetargetWriter.Subfolder + "/" + option + "/", entry.File.Replace('\\', '/'),
                              StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(BodySizeCatalog.ResolveCaseInsensitive(tot.CliCopy, entry.File)), entry.File);
        }
        // Into that folder only: nothing beside it.
        Assert.Equal(["Body Retarget", "Proteus"],
                     Directory.GetDirectories(tot.CliCopy).Select(d => Path.GetFileName(d))
                              .Where(d => d is "Body Retarget" or "Proteus").Order().ToArray());
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-2")]
    public void Cli_sizes_Rue_onto_YAS_and_names_options_with_the_body() // [AC-headless-size-set-2]
    {
        var rue = Sizes(tot.Report).Where(s => (string?)s["body"] == "rue" && (string)s["status"]! == "made").ToList();
        Assert.NotEmpty(rue);
        foreach (var s in rue)
        {
            Assert.True((bool)s["yas"]!);
            Assert.StartsWith("Rue ", (string)s["option"]!, StringComparison.Ordinal);
            var entry = BodyRetargetWriter.ReadRecord(tot.CliCopy)!.Options.Single(e => e.Name == (string)s["option"]!);
            byte[] model = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(tot.CliCopy, entry.File));
            Assert.True(SupportedBodies.IsYas(SecondSkinWriter.Parse(model).BoneNames), $"{entry.Name} is not rigged for YAS");
        }
        Assert.All(Sizes(tot.Report).Where(s => s["option"] is not null && s["body"] is not null),
                   s => Assert.StartsWith(SupportedBodies.Choices.First(c => c.Key == (string)s["body"]!).Label,
                                          (string)s["option"]!, StringComparison.Ordinal));
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-4")]
    public void Cli_only_appends_groups_and_options() // [AC-headless-size-set-4]
        => SizeSetBatchData.AssertOnlyAppended(tot.Before, SizeSetBatchData.Shape(tot.CliCopy));

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-5")]
    public void Report_has_every_field_and_made_counts_made_entries() // [AC-headless-size-set-5]
    {
        var r = tot.Report;
        Assert.Equal(Plugin.BuildNumber, (int)r["build"]!);
        var sizes = Sizes(r).ToList();
        var made = sizes.Where(s => (string)s["status"]! == "made").ToList();
        Assert.Equal(made.Count, (int)r["made"]!);
        Assert.All(sizes, s => Assert.Equal(24, s.AsObject().Count));
        foreach (var s in made)
            foreach (string key in new[] { "folded", "unmatched", "worstMoveMm", "worstPushMm", "pushed", "snapRate", "shapeFallbacks", "seconds" })
                Assert.NotNull(s[key]);
        Assert.All(made, s => Assert.True((double)s["seconds"]! > 0));
        Assert.True((double)r["seconds"]! >= made.Sum(s => (double)s["seconds"]!));
        // One row per (key, slot, yas) the run touched, by folder name.
        Assert.Contains(r["bodyMods"]!.AsArray(), b => (string)b!["key"]! == "rue" && (string?)b["folder"] == SizeSetBatchData.Folder(SizeSetBatchData.Rue));
        Assert.Empty(r["missingBodies"]!.AsArray());
    }

    /// <summary>
    /// The fold count is the saved cloth's own: each made top's cloth parts, paired with the source size's by label and
    /// triangle count, triangles of 1 mm² or more that turned over.
    /// </summary>
    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-5")]
    public void Folded_is_counted_on_the_saved_model() // [AC-headless-size-set-5]
    {
        var record = BodyRetargetWriter.ReadRecord(tot.CliCopy)!;
        var redirects = PenumbraModMeta.ReadAllRedirects(tot.CliCopy);
        int checkedSizes = 0;
        foreach (var s in Sizes(tot.Report).Where(s => (string)s["status"]! == "made" && (string)s["slot"]! == "_top"))
        {
            var entry = record.Options.Single(e => e.Name == (string)s["option"]! && e.GamePath == (string)s["gamePath"]!);
            var saved = ModelPartReader.Read(File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(tot.CliCopy, entry.File)))!;
            // The author's size it was cut from, by the option the report names, paired vertex for vertex with the saved
            // cloth (as --measure --source pairs them).
            string cutFrom = (string)s["cutFrom"]!;
            var source = redirects.Single(r => r.GamePath == entry.GamePath
                                               && r.Source.EndsWith(" / " + cutFrom, StringComparison.Ordinal));
            int? counted = Folded(ModelPartReader.Read(File.ReadAllBytes(
                                      BodySizeCatalog.ResolveCaseInsensitive(tot.CliCopy, source.File)))!, saved);
            Assert.True(counted != null, $"{entry.Name}: the cloth of \"{cutFrom}\" does not pair with the saved model's");
            output.WriteLine($"{entry.Name} from {cutFrom}: report {s["folded"]}, counted {counted}");
            Assert.Equal(counted!.Value, (int)s["folded"]!);
            checkedSizes++;
        }
        Assert.True(checkedSizes > 0);
    }

    /// <summary>
    /// Cloth triangles of 1 mm² or more turned over from <paramref name="source"/> to <paramref name="saved"/>: the cloth
    /// vertices of each taken in order and paired one for one, the source's triangles laid on the saved positions. Null
    /// when the two have a different number of cloth vertices.
    /// </summary>
    private static int? Folded(ModelParts source, ModelParts saved)
    {
        static List<int> Cloth(ModelParts m) => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                                 .SelectMany(p => p.Triangles).Distinct().Order().ToList();
        var from = Cloth(source);
        var to = Cloth(saved);
        if (from.Count != to.Count) return null;
        var map = new Dictionary<int, int>(from.Count);
        for (int i = 0; i < from.Count; i++) map[from[i]] = to[i];
        int folded = 0;
        foreach (var part in source.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = Normal(source.Positions, a, b, c);
                if (was.Length() * 0.5f < 1e-6f) continue;
                if (System.Numerics.Vector3.Dot(was, Normal(saved.Positions, map[a], map[b], map[c])) <= 0f) folded++;
            }
        return folded;
    }

    private static System.Numerics.Vector3 Normal(float[] p, int a, int b, int c)
    {
        System.Numerics.Vector3 At(int i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);
        return System.Numerics.Vector3.Cross(At(b) - At(a), At(c) - At(a));
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-9")]
    [Trait("Plan", "EDGE-headless-size-set-14")]
    public void Second_run_adds_nothing() // [AC-headless-size-set-9] [EDGE-headless-size-set-14]
    {
        Assert.Equal(0, (int)tot.SecondReport["made"]!);
        Assert.Null(tot.SecondReport["migrated"]);
        var madeFirst = Sizes(tot.Report).Where(s => (string)s["status"]! == "made").Select(s => ((string)s["gamePath"]!, (string)s["option"]!)).ToList();
        foreach (var (gamePath, option) in madeFirst)
            Assert.Contains(Sizes(tot.SecondReport), s => (string)s["gamePath"]! == gamePath && (string?)s["option"] == option
                                                          && (string?)s["reasonCode"] == "already-there");
        Assert.Equal(tot.ManifestAfterFirst, tot.ManifestAfterSecond);
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-13")]
    public void Authors_own_sizes_are_skipped_by_name() // [EDGE-headless-size-set-13]
    {
        foreach (string option in new[] { "Rue M", "Rue L" })
        {
            var s = Sizes(tot.Report).Single(s => (string?)s["option"] == option && (string)s["slot"]! == "_top");
            Assert.Equal(("skipped", "authors-own"), ((string)s["status"]!, (string)s["reasonCode"]!));
            Assert.Contains("\"", (string)s["reason"]!);
        }
        var group = SizeSetBatchData.Shape(tot.CliCopy).Single(g => g.Options.Contains("Rue S"));
        Assert.Single(group.Options, o => o == "Rue M");
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-21")]
    public void Pack_holds_no_local_paths() // [EDGE-headless-size-set-21]
        => AssertNoLocalPaths(tot.CliCopy);

    internal static void AssertNoLocalPaths(string copy)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        foreach (string file in new[] { Path.Combine(copy, PenumbraModMeta.MetaFile), Path.Combine(copy, "Proteus", "bodyretarget.json") })
        {
            if (!File.Exists(file)) continue;
            string text = File.ReadAllText(file);
            Assert.DoesNotContain(":\\\\", text);
            Assert.DoesNotContain(":/", text.Replace("https:/", "").Replace("http:/", ""));
            Assert.DoesNotContain(modsRoot.Replace("\\", "\\\\"), text, StringComparison.OrdinalIgnoreCase);
            // The user name as a word, not inside a longer one (an author name may contain it).
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(
                $@"(?i)(?<![a-z0-9]){System.Text.RegularExpressions.Regex.Escape(Environment.UserName)}(?![a-z0-9])"), text);
        }
        foreach (var e in BodyRetargetWriter.ReadRecord(copy)?.Options ?? [])
            Assert.Equal(Path.GetFileName(e.BodyMod), e.BodyMod);
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-24")]
    public void Body_mods_are_not_written() // [EDGE-headless-size-set-24]
        => Assert.Equal(tot.BodyModsBefore, tot.BodyModsAfter);

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rosaline, LocalData.GameData, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-25")]
    public void Neolithe_family_sizes_come_from_the_garments_own_family() // [EDGE-headless-size-set-25]
    {
        // Made on a Neolithe family, it is offered that family's four sizes — which are the author's own: had it taken
        // the table's NSFW sizes, they would have been made, not found already there.
        var neo = Sizes(tot.Report).Where(s => (string?)s["body"] == "neolithe" && (string)s["slot"]! == "_top").ToList();
        Assert.Equal(["Neolithe XS", "Neolithe S", "Neolithe M", "Neolithe L"], neo.Select(s => (string)s["option"]!));
        Assert.All(neo, s => Assert.Contains((string)s["reasonCode"]!, new[] { "authors-own", "made-on-it" }));
    }

    // ── one run each ──

    [LocalDataFact(SizeSetBatchData.Neolithe, SizeSetBatchData.NeolitheYas, SizeSetBatchData.Neolithe4k, SizeSetBatchData.Bibo,
                   SizeSetBatchData.Rue, SizeSetBatchData.Yab, SizeSetBatchData.Rosaline, SizeSetBatchData.TreYas,
                   SizeSetBatchData.Tbse, SizeSetBatchData.TbseX)]
    [Trait("Plan", "AC-headless-size-set-6")]
    [Trait("Plan", "EDGE-headless-size-set-31")]
    public void Every_key_resolves_to_the_expected_folder_over_the_full_root() // [AC-headless-size-set-6] [EDGE-headless-size-set-31]
    {
        var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
        var rows = SizeSetBatch.BodyModsOf(SupportedBodies.Choices.Select(c => c.Key), bodies);
        foreach (var row in rows) output.WriteLine($"{row.Key} {row.Slot} yas={row.Yas}: {row.Folder}");
        string? Folder(string key, string slot, bool yas) => rows.Single(r => r.Key == key && r.Slot == slot && r.Yas == yas).Folder;
        string F(string need) => SizeSetBatchData.Folder(need);

        foreach (string slot in new[] { "_top", "_dwn" })
        {
            Assert.Equal(F(SizeSetBatchData.Neolithe), Folder("neolithe", slot, false));
            Assert.Equal(F(SizeSetBatchData.Bibo), Folder("bibo", slot, false));
            Assert.Equal(F(SizeSetBatchData.Rue), Folder("rue", slot, false));
            Assert.Equal(F(SizeSetBatchData.Rue), Folder("rue", slot, true));
            Assert.Equal(F(SizeSetBatchData.Yab), Folder("yab", slot, false));
            Assert.Equal(F(SizeSetBatchData.Yab), Folder("yab", slot, true));
            // Plain Tre is not installed here, and LavaBod+ 2.2 carries plain Tre's files: Find takes it for plain Tre,
            // as the in-game panel would with YAS off. Reported, not changed here (it is Find's, and the panel's); the
            // size set runs with YAS on, and takes Tre from its YAS release.
            Assert.Contains(Folder("tre", slot, false), new[] { null, "LavaBod+ 2.2" });
            Assert.Equal(F(SizeSetBatchData.TreYas), Folder("tre", slot, true));
            Assert.Equal(F(SizeSetBatchData.Rosaline), Folder("rosaline", slot, false));
            Assert.Equal(F(SizeSetBatchData.Tbse), Folder("tbse", slot, false));
            Assert.Equal(F(SizeSetBatchData.TbseX), Folder("tbse-x", slot, false));
        }
        Assert.Equal(F(SizeSetBatchData.Yab), Folder("yab-buff", "_top", false));
        Assert.Equal(F(SizeSetBatchData.Yab), Folder("yab-buff", "_top", true));
        // No row the size set uses goes to an outfit or another body that only shares a body's files or a name's words.
        string[] never = ["[HS] Dei Gratia", "LavaBod+ 2.2", "[IVCS] Bibo+", "Neolithe YAS AIO", "Neolithe 4k"];
        Assert.DoesNotContain(rows.Where(r => !(r.Key == "tre" && !r.Yas)),
                              r => r.Folder != null && (never.Contains(r.Folder, StringComparer.OrdinalIgnoreCase)
                                                              || r.Folder.Contains("lithe +", StringComparison.OrdinalIgnoreCase)
                                                              || r.Folder.StartsWith("Isle Vacationers", StringComparison.OrdinalIgnoreCase)));
    }

    [LocalDataFact(SizeSetBatchData.Neolithe, SizeSetBatchData.NeolitheYas, SizeSetBatchData.Neolithe4k)]
    [Trait("Plan", "EDGE-headless-size-set-9")]
    public void Neolithe_never_comes_from_its_YAS_release() // [EDGE-headless-size-set-9]
    {
        var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods),
            [SizeSetBatchData.Folder(SizeSetBatchData.Neolithe), SizeSetBatchData.Folder(SizeSetBatchData.NeolitheYas),
             SizeSetBatchData.Folder(SizeSetBatchData.Neolithe4k)]);
        foreach (string slot in new[] { "_top", "_dwn" })
            Assert.NotEqual(SizeSetBatchData.Folder(SizeSetBatchData.NeolitheYas),
                            SupportedBodies.Find(SupportedBodies.Of("neolithe", slot, false)!, bodies)?.Dir);
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-7")]
    public void Missing_body_is_reported_and_the_rest_made() // [EDGE-headless-size-set-7]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
        try
        {
            var (outcome, runLog) = SizeSetBatchData.Batch(copy, ["rue", "bibo"], men: false,
                                                           [SizeSetBatchData.Neolithe, SizeSetBatchData.Rue]);
            Assert.Equal(["bibo"], outcome.MissingBodies);
            Assert.All(outcome.BodyMods.Where(b => b.Key == "bibo"), b => Assert.Null(b.Folder));
            Assert.Contains(runLog.Sizes, s => s.Body == "bibo" && s.ReasonCode == SizeSetRefit.Codes.NotInstalled);
            Assert.Contains(runLog.Sizes, s => s.Body == "rue" && s.Status == SizeSetRefit.SizeStatus.Made);
            Assert.Null(outcome.RefusalCode);
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.TreYas, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-8")]
    public void Tre_is_made_from_its_YAS_release_when_plain_is_absent() // [EDGE-headless-size-set-8]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
        try
        {
            var (_, runLog) = SizeSetBatchData.Batch(copy, ["tre"], men: false, [SizeSetBatchData.Neolithe, SizeSetBatchData.TreYas]);
            var tre = runLog.Sizes.Where(s => s.Body == "tre" && s.Status == SizeSetRefit.SizeStatus.Made).ToList();
            Assert.NotEmpty(tre);
            Assert.All(tre, s => Assert.Equal((SizeSetBatchData.Folder(SizeSetBatchData.TreYas), true), (s.BodyMod, s.Yas)));
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    /// <summary>
    /// A size refused at its pair is said so and saved nowhere, while the body's other sizes are still made: Rosaline's
    /// Coconut chest, cut short in a copy of the body mod, cannot be read, and Rosaline M and L are made as ever.
    /// </summary>
    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rosaline, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-11")]
    public void Refused_size_is_reported_and_the_rest_made() // [EDGE-headless-size-set-11]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
        string modsRoot = SizeSetBatchData.ModsRootWith(SizeSetBatchData.Neolithe);
        try
        {
            string rosaline = Path.Combine(modsRoot, SizeSetBatchData.Folder(SizeSetBatchData.Rosaline));
            SizeSetRefitTests.CopyModels(LocalData.Path(SizeSetBatchData.Rosaline), rosaline, withRecord: false);
            string coconut = Path.Combine(rosaline, "files", "common", "7", "c0201e0000_top.mdl");
            File.WriteAllBytes(coconut, File.ReadAllBytes(coconut)[..64]);

            var (_, runLog) = SizeSetBatchData.Batch(copy, ["rosaline"], men: false,
                                                     [SizeSetBatchData.Folder(SizeSetBatchData.Neolithe),
                                                      SizeSetBatchData.Folder(SizeSetBatchData.Rosaline)], modsRoot: modsRoot);
            foreach (var s in runLog.Sizes) output.WriteLine($"{s.Option} {s.Status} {s.ReasonCode} {s.Reason}");
            var refused = Assert.Single(runLog.Sizes, s => s.Status == SizeSetRefit.SizeStatus.Refused);
            Assert.Equal(("Rosaline Coconut", SizeSetRefit.Codes.RefusedPair), (refused.Option, refused.ReasonCode));
            Assert.False(string.IsNullOrWhiteSpace(refused.Reason));
            Assert.DoesNotContain("Rosaline Coconut", SizeSetBatchData.Shape(copy).SelectMany(g => g.Options));
            Assert.Contains(runLog.Sizes, s => s.Option == "Rosaline M" && s.Status == SizeSetRefit.SizeStatus.Made);
            Assert.Contains(runLog.Sizes, s => s.Option == "Rosaline L" && s.Status == SizeSetRefit.SizeStatus.Made);
        }
        finally
        {
            SizeSetBatchData.Delete(copy);
            SizeSetBatchData.DeleteModsRoot(modsRoot);
        }
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-18")]
    public void A_throwing_piece_is_failed_and_others_run() // [EDGE-headless-size-set-18]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
        try
        {
            // A second top, in the default files, whose model is cut short.
            var some = Directory.GetFiles(copy, "*.mdl", SearchOption.AllDirectories).First();
            byte[] bytes = File.ReadAllBytes(some);
            Directory.CreateDirectory(Path.Combine(copy, "broken"));
            File.WriteAllBytes(Path.Combine(copy, "broken", "top.mdl"), bytes[..(bytes.Length / 3)]);
            var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, PenumbraModMeta.MetaFile)))!.AsObject();
            meta["DefaultData"] ??= new JsonObject { ["Files"] = new JsonObject(), ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() };
            meta["DefaultData"]!["Files"]!["chara/equipment/e6001/model/c0201e6001_top.mdl"] = "broken\\top.mdl";
            File.WriteAllText(Path.Combine(copy, PenumbraModMeta.MetaFile), meta.ToJsonString());

            var (outcome, runLog) = SizeSetBatchData.Batch(copy, ["rue"], men: false, [SizeSetBatchData.Neolithe, SizeSetBatchData.Rue]);
            var broken = runLog.Sizes.Where(s => s.GamePath.Contains("e6001")).ToList();
            foreach (var s in broken) output.WriteLine($"{s.Status} {s.ReasonCode}: {s.Reason}");
            var only = Assert.Single(broken);
            Assert.Equal((SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.Unreadable), (only.Status, only.ReasonCode));
            Assert.Contains(runLog.Sizes, s => s.GamePath.Contains("e6255") && s.Status == SizeSetRefit.SizeStatus.Made);
            Assert.Equal(0, SizeSetCommandExit(outcome));
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    private static int SizeSetCommandExit(SizeSetBatch.Outcome outcome) => RefitCli::Proteus.Refit.SizeSetCommand.ExitCode(outcome);

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Bibo, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-19")]
    public void Cancelled_run_lists_saved_sizes() // [EDGE-headless-size-set-19]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
        try
        {
            var before = SizeSetBatchData.Shape(copy);
            using var cancel = new CancellationTokenSource();
            int working = 0;
            string workingWord = Proteus.Localization.Strings.SizeSet.WorkingFmt.Split('{')[0];
            var (outcome, runLog) = SizeSetBatchData.Batch(copy, ["bibo"], men: false, [SizeSetBatchData.Neolithe, SizeSetBatchData.Bibo],
                progress: p =>
                {
                    // The second size being made: the first is saved. Stop now; the second is left unsaved.
                    if (workingWord.Length > 0 && p.StartsWith(workingWord, StringComparison.Ordinal) && ++working == 2) cancel.Cancel();
                }, ct: cancel.Token);
            Assert.True(outcome.Cancelled);
            var made = runLog.Sizes.Where(s => s.Status == SizeSetRefit.SizeStatus.Made).ToList();
            Assert.Single(made);
            Assert.Contains(made[0].Option, SizeSetBatchData.Shape(copy).SelectMany(g => g.Options));
            // This Old Thing's sizes are in its own group: its default is the author's, untouched.
            Assert.Equal(before.Select(g => g.Default), SizeSetBatchData.Shape(copy).Take(before.Count).Select(g => g.Default));
            Assert.Equal(1, SizeSetCommandExit(outcome));
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Rue, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-4")]
    [Trait("Plan", "EDGE-headless-size-set-17")]
    public void Cli_new_group_starts_with_Original_as_default() // [AC-headless-size-set-4] [EDGE-headless-size-set-17]
    {
        // This Old Thing's M top, alone in the default files: no size group of the author's.
        string full = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
        string copy = SizeSetBatchData.NewCopy();
        try
        {
            var top = PenumbraModMeta.ReadAllRedirects(full).First(r => r.GamePath.EndsWith("_top.mdl", StringComparison.Ordinal));
            Directory.CreateDirectory(Path.Combine(copy, "top"));
            File.Copy(BodySizeCatalog.ResolveCaseInsensitive(full, top.File), Path.Combine(copy, "top", "top.mdl"));
            File.WriteAllText(Path.Combine(copy, PenumbraModMeta.MetaFile), $$"""
                { "FileVersion": 4, "Name": "One top", "Groups": [],
                  "DefaultData": { "Files": { "{{top.GamePath}}": "top\\top.mdl" }, "FileSwaps": {}, "Manipulations": [] } }
                """);
            var (_, runLog) = SizeSetBatchData.Batch(copy, ["rue"], men: false, [SizeSetBatchData.Neolithe, SizeSetBatchData.Rue]);
            Assert.Contains(runLog.Sizes, s => s.Status == SizeSetRefit.SizeStatus.Made);
            var group = SizeSetBatchData.Shape(copy).Last();
            Assert.Equal(BodyRetargetWriter.OriginalOption, group.Options[0]);
            Assert.Equal(BodyRetargetWriter.OriginalOption, PenumbraModMeta.SingleDefault(copy, group.Group));
            Assert.Empty(PenumbraModMeta.TryReadFileOptions(copy, group.Group)!.First().Files);
        }
        finally
        {
            SizeSetBatchData.Delete(full);
            SizeSetBatchData.Delete(copy);
        }
    }

    // ── men ──

    [LocalDataFact(SizeSetBatchData.Sheer, SizeSetBatchData.Bibo, SizeSetBatchData.Tbse, SizeSetBatchData.TbseX,
                   LocalData.GameData, LocalData.TbseUvMaps, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-3")]
    [Trait("Plan", "EDGE-headless-size-set-12")]
    public void Cli_men_appends_a_mens_group_for_a_womens_mod() // [AC-headless-size-set-3] [EDGE-headless-size-set-12]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.Sheer);
        try
        {
            var before = SizeSetBatchData.Shape(copy);
            var (_, runLog) = SizeSetBatchData.Batch(copy, ["bibo"], men: true,
                                                     [SizeSetBatchData.Bibo, SizeSetBatchData.Tbse, SizeSetBatchData.TbseX],
                                                     readGame: SizeSetBatchData.Game());
            foreach (var s in runLog.Sizes) output.WriteLine($"{s.Piece} {s.Option} {s.Status} {s.ReasonCode} {s.Reason} {s.Measures?.Folded}");
            var after = SizeSetBatchData.Shape(copy);
            SizeSetBatchData.AssertOnlyAppended(before, after);
            var mens = after.Last();
            Assert.Equal(Proteus.Localization.Strings.SizeSet.MensGroup, mens.Group);
            Assert.Equal(BodyRetargetWriter.OriginalOption, mens.Options[0]);
            Assert.Subset(new HashSet<string> { BodyRetargetWriter.OriginalOption, "TBSE", "TBSE-X" }, mens.Options.ToHashSet());
            Assert.Equal(BodyRetargetWriter.OriginalOption, PenumbraModMeta.SingleDefault(copy, mens.Group));
            var made = runLog.Sizes.Where(s => s.Men && s.Status == SizeSetRefit.SizeStatus.Made).ToList();
            Assert.NotEmpty(made);
            Assert.All(made, s => Assert.Contains("c0101", s.GamePath));
            Assert.All(made, s => Assert.NotNull(s.Measures!.Coverage));
            // Each saved at the men's path, carrying the EQDP entry that loads it.
            foreach (var option in PenumbraModMeta.TryReadFileOptions(copy, mens.Group)!.Skip(1))
            {
                Assert.Contains(option.Files.Keys, k => k.Contains("c0101", StringComparison.Ordinal));
                Assert.NotEmpty(option.Manipulations ?? []);
            }
            // Folds are said, never refused: Sheer's TBSE version folds (about 122), and is made all the same.
            var folded = made.Where(s => s.Measures!.Folded > 0).ToList();
            output.WriteLine($"men's sizes with folds: {string.Join(", ", folded.Select(s => $"{s.Option} {s.Measures!.Folded}"))}");
            Assert.Contains(folded, s => s.Body == "tbse");
            Assert.Contains(runLog.Warnings, w => w.Contains("TBSE", StringComparison.Ordinal));
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    /// <summary>
    /// A men's top made on TBSE — TBSE's own chest, worn as a garment, the one men's piece sure to read as made on TBSE
    /// (the men's mods here read Ambiguous) — is sized onto TBSE-X directly, in its own group, and left as it is on TBSE.
    /// </summary>
    [LocalDataFact(SizeSetBatchData.Tbse, SizeSetBatchData.TbseX, LocalData.GameData, LocalData.TbseUvMaps, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-3")]
    [Trait("Plan", "EDGE-headless-size-set-16")]
    public void Cli_men_sizes_a_mens_piece_directly() // [AC-headless-size-set-3] [EDGE-headless-size-set-16]
    {
        string copy = SizeSetBatchData.NewCopy();
        Directory.CreateDirectory(Path.Combine(copy, "top"));
        File.Copy(Path.Combine(LocalData.Path(SizeSetBatchData.Tbse), "common", "1", "c0101e0000_top.mdl"),
                  Path.Combine(copy, "top", "top.mdl"));
        File.WriteAllText(Path.Combine(copy, PenumbraModMeta.MetaFile), """
            { "FileVersion": 4, "Name": "Men's top", "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [ { "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                { "Name": "TBSE", "Files": { "chara/equipment/e6000/model/c0101e6000_top.mdl": "top\\top.mdl" } } ] } ] }
            """);
        try
        {
            var (_, runLog) = SizeSetBatchData.Batch(copy, [], men: true, [SizeSetBatchData.Tbse, SizeSetBatchData.TbseX],
                                                     readGame: SizeSetBatchData.Game());
            foreach (var s in runLog.Sizes) output.WriteLine($"{s.Piece} {s.Group} {s.Option} {s.Status} {s.ReasonCode} {s.Reason}");
            Assert.DoesNotContain(runLog.Sizes, s => s.Men);
            var tbseX = Assert.Single(runLog.Sizes, s => s.Body == "tbse-x");
            Assert.Equal((SizeSetRefit.SizeStatus.Made, "Size", "TBSE-X"), (tbseX.Status, tbseX.Group, tbseX.Option));
            Assert.Contains(runLog.Sizes, s => s.Body == "tbse" && s.ReasonCode is SizeSetRefit.Codes.AuthorsOwn
                                                                                   or SizeSetRefit.Codes.MadeOnIt);
            Assert.Equal(["TBSE", "TBSE-X"], SizeSetBatchData.Shape(copy).Single(g => g.Group == "Size").Options);
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    /// <summary>
    /// A women's top none of whose sizes can be crossed — here because the game gives no men's skeleton to build a
    /// man's wearer from: no men's version is cut for TBSE, nor for TBSE-X after it, and each men's body is said so,
    /// not only the first.
    /// </summary>
    [LocalDataFact(SizeSetBatchData.ThisOldThing, SizeSetBatchData.Neolithe, SizeSetBatchData.Tbse, SizeSetBatchData.TbseX,
                   LocalData.TbseUvMaps, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-30")]
    public void Every_mens_key_gets_an_outcome_when_none_can_be_crossed() // [EDGE-headless-size-set-30]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.ThisOldThing);
        try
        {
            var (_, runLog) = SizeSetBatchData.Batch(copy, [], men: true,
                                                     [SizeSetBatchData.Neolithe, SizeSetBatchData.Tbse, SizeSetBatchData.TbseX],
                                                     readGame: _ => null);
            foreach (var s in runLog.Sizes) output.WriteLine($"{s.Piece} {s.Option} {s.Body} {s.Status} {s.ReasonCode} {s.Reason}");
            var mens = runLog.Sizes.Where(s => s.Men).ToList();
            Assert.Equal(["TBSE", "TBSE-X"], mens.Select(s => s.Option));
            Assert.All(mens, s => Assert.Equal((SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.NoSourceMen),
                                               (s.Status, s.ReasonCode)));
            Assert.Equal([SizeSetBatchData.Folder(SizeSetBatchData.Tbse), SizeSetBatchData.Folder(SizeSetBatchData.TbseX)],
                         mens.Select(s => s.BodyMod));
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    [LocalDataFact(SizeSetBatchData.Sheer, SizeSetBatchData.Bibo, SizeSetBatchData.Tbse, SizeSetBatchData.TbseX, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-headless-size-set-15")]
    public void Mens_group_name_taken_is_reported() // [EDGE-headless-size-set-15]
    {
        string copy = SizeSetBatchData.CopyOf(SizeSetBatchData.Sheer);
        try
        {
            var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, PenumbraModMeta.MetaFile)))!.AsObject();
            meta["Groups"]!.AsArray().Add(new JsonObject
            {
                ["Name"] = Proteus.Localization.Strings.SizeSet.MensGroup, ["Type"] = "Single", ["Priority"] = 0,
                ["DefaultSettings"] = 0, ["Options"] = new JsonArray(new JsonObject { ["Name"] = "Theirs", ["Files"] = new JsonObject() }),
            });
            File.WriteAllText(Path.Combine(copy, PenumbraModMeta.MetaFile), meta.ToJsonString());
            var (_, runLog) = SizeSetBatchData.Batch(copy, [], men: true, [SizeSetBatchData.Bibo, SizeSetBatchData.Tbse, SizeSetBatchData.TbseX],
                                                     readGame: _ => null);
            var mens = runLog.Sizes.Where(s => s.Men).ToList();
            Assert.NotEmpty(mens);
            Assert.All(mens, s => Assert.Equal(SizeSetRefit.Codes.MenGroupTaken, s.ReasonCode));
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    [LocalDataFact(LocalData.GameData, SizeSetBatchData.HellForLeather)]
    [Trait("Plan", "EDGE-headless-size-set-22")]
    public void Game_models_shipped_by_TexTools_get_no_mens_sizes() // [EDGE-headless-size-set-22]
    {
        var pieces = SizeSetRefit.PiecesOf(LocalData.Path(SizeSetBatchData.HellForLeather), SizeSetBatchData.Game());
        // The game's own Anemos gambison and chaps it ships for men are not pieces: no men's chest or legs to size. (The
        // men's Anemos gloves are the author's own edit, and are one.)
        Assert.DoesNotContain(pieces, p => p.Race == "0101" && p.Slot is "_top" or "_dwn");
        var keys = SizeSetBatch.RunKeys(pieces, ["bibo"], men: true);
        Assert.Contains("tbse@women", keys);
        // The men's versions are of its women's chest and legs only.
        Assert.All(SizeSetRefit.AcrossPieces(pieces), p => Assert.Equal("0201", p.Race));
        Assert.Equal(["_top", "_dwn"], SizeSetRefit.AcrossPieces(pieces).Select(p => p.Slot));
    }

    [LocalDataFact(SizeSetBatchData.Heartthrob)]
    [Trait("Plan", "EDGE-headless-size-set-23")]
    public void No_ends_leaves_shoes_out() // [EDGE-headless-size-set-23]
    {
        string root = LocalData.Path(SizeSetBatchData.Heartthrob);
        Assert.Contains(SizeSetRefit.PiecesOf(root, includeEnds: true), p => p.Slot is "_sho" or "_glv");
        Assert.DoesNotContain(SizeSetRefit.PiecesOf(root, includeEnds: false), p => p.Slot is "_sho" or "_glv");
    }

    // ── v3 ──

    /// <summary>Scarlet's pack, v3, unpacked into a work folder.</summary>
    private static string Unpacked(string pack)
    {
        string copy = SizeSetBatchData.NewCopy();
        ZipFile.ExtractToDirectory(LocalData.Path(pack), copy);
        return copy;
    }

    [LocalDataFact(SizeSetBatchData.ScarletPack, SizeSetBatchData.Neolithe, SizeSetBatchData.Bibo, LocalData.UvMaps)]
    [Trait("Plan", "AC-headless-size-set-10")]
    [Trait("Plan", "AC-headless-size-set-4")]
    [Trait("Plan", "EDGE-headless-size-set-21")]
    public void V3_pack_is_converted_then_sized() // [AC-headless-size-set-10] [AC-headless-size-set-4] [EDGE-headless-size-set-21]
    {
        string copy = Unpacked(SizeSetBatchData.ScarletPack);
        try
        {
            Assert.True(PenumbraModMeta.IsLegacyFolder(copy));
            var v3Groups = Directory.GetFiles(copy, "group_*.json").Length;
            var (outcome, runLog) = SizeSetBatchData.Batch(copy, ["bibo"], men: false, [SizeSetBatchData.Neolithe, SizeSetBatchData.Bibo]);
            foreach (var s in runLog.Sizes) output.WriteLine($"{s.Piece} {s.Option} {s.Status} {s.ReasonCode} {s.Reason}");
            Assert.Equal((3, v3Groups), (outcome.Migrated!.FromVersion, outcome.Migrated.Groups));
            Assert.Empty(Directory.GetFiles(copy, "group_*.json"));
            Assert.False(File.Exists(Path.Combine(copy, PenumbraModMeta.LegacyDefaultMod)));
            Assert.Equal(4, PenumbraModMeta.ReadFileVersion(copy));
            Assert.Contains(runLog.Sizes, s => s.Body == "bibo" && s.Status == SizeSetRefit.SizeStatus.Made);

            // Only appended to, against the groups the conversion wrote: re-read from a fresh conversion of the pack.
            string fresh = Unpacked(SizeSetBatchData.ScarletPack);
            try
            {
                PenumbraModMeta.MigrateWorkCopy(fresh, LocalData.Path(LocalData.Mods));
                SizeSetBatchData.AssertOnlyAppended(SizeSetBatchData.Shape(fresh), SizeSetBatchData.Shape(copy));
            }
            finally { SizeSetBatchData.Delete(fresh); }
            AssertNoLocalPaths(copy);
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    /// <summary>
    /// The installed mods Penumbra converted from v3 and nothing has written since: their v3 files kept beside as
    /// <c>.bak</c>, the manifest no newer than them. Each a pair of what went in and what Penumbra made of it.
    /// </summary>
    public static IEnumerable<object[]> UntouchedMigrations()
    {
        if (LocalData.Missing([LocalData.Mods]) != null) yield break;
        foreach (string dir in Directory.EnumerateDirectories(LocalData.Path(LocalData.Mods)))
        {
            var baks = Directory.GetFiles(dir, "group_*.json.bak");
            string meta = Path.Combine(dir, PenumbraModMeta.MetaFile);
            if (baks.Length == 0 || !File.Exists(meta)) continue;
            var newest = baks.Append(Path.Combine(dir, "default_mod.json.bak")).Where(File.Exists).Max(File.GetLastWriteTimeUtc);
            if ((File.GetLastWriteTimeUtc(meta) - newest).Duration() <= TimeSpan.FromSeconds(5))
                yield return [Path.GetFileName(dir)];
        }
    }

    /// <summary>
    /// The conversion against Penumbra's own, over every untouched pair: the same groups in the same order, each the
    /// same type, priority, default and page, the same options in the same order with the same files and swaps — file
    /// paths ignoring case, which Penumbra lowercases — and every manipulation Penumbra kept. Penumbra also drops
    /// manipulations that change nothing; the author's are kept here.
    /// </summary>
    [LocalDataTheory(LocalData.Mods)]
    [MemberData(nameof(UntouchedMigrations))]
    [Trait("Plan", "AC-headless-size-set-10")]
    public void Work_copy_conversion_matches_Penumbras(string folder) // [AC-headless-size-set-10]
    {
        string installed = Path.Combine(LocalData.Path(LocalData.Mods), folder);
        string copy = SizeSetBatchData.NewCopy();
        Directory.CreateDirectory(copy);
        try
        {
            var penumbra = JsonNode.Parse(File.ReadAllText(Path.Combine(installed, PenumbraModMeta.MetaFile)))!.AsObject();
            var v3 = new JsonObject { ["FileVersion"] = 3 };
            foreach (var (key, value) in penumbra)
                if (key is not ("FileVersion" or "Groups" or "DefaultData")) v3[key] = value?.DeepClone();
            File.WriteAllText(Path.Combine(copy, PenumbraModMeta.MetaFile), v3.ToJsonString());
            foreach (string bak in Directory.GetFiles(installed, "*.json.bak").Where(f => !Path.GetFileName(f).StartsWith("meta", StringComparison.OrdinalIgnoreCase)))
                File.Copy(bak, Path.Combine(copy, Path.GetFileName(bak)[..^4]));

            PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
            var ours = JsonNode.Parse(File.ReadAllText(Path.Combine(copy, PenumbraModMeta.MetaFile)))!.AsObject();

            // Groups Proteus added after the migration (its "(Proteus)" groups) were never in the v3 files.
            var v3Names = Directory.GetFiles(installed, "group_*.json.bak")
                                   .Select(f => (string?)JsonNode.Parse(File.ReadAllText(f))?["Name"]).ToHashSet();
            var theirGroups = new JsonArray((penumbra["Groups"]?.AsArray() ?? [])
                                            .Where(g => v3Names.Contains((string?)g!["Name"])).Select(g => g!.DeepClone()).ToArray());
            var ourGroups = ours["Groups"]!.AsArray();
            Assert.Equal(theirGroups.Select(g => (string?)g!["Name"]), ourGroups.Select(g => (string?)g!["Name"]));
            foreach (var (theirs, mine) in theirGroups.Zip(ourGroups))
            {
                string name = (string)theirs!["Name"]!;
                foreach (string key in new[] { "Type", "Priority", "DefaultSettings", "Page" })
                    Assert.True(Same(theirs[key], mine![key], key is "Priority" or "DefaultSettings" or "Page"), $"{name}: {key}");
                Assert.False(mine!.AsObject().ContainsKey("Version"));
                var theirOptions = theirs["Options"]?.AsArray() ?? [];
                var myOptions = mine["Options"]?.AsArray() ?? [];
                Assert.Equal(theirOptions.Select(o => (string?)o!["Name"]), myOptions.Select(o => (string?)o!["Name"]));
                foreach (var (to, mo) in theirOptions.Zip(myOptions))
                    AssertSameData($"{name} / {to!["Name"]}", installed, to, mo!);
            }
            AssertSameData("default", installed, penumbra["DefaultData"], ours["DefaultData"]!);
        }
        finally { SizeSetBatchData.Delete(copy); }
    }

    /// <summary>
    /// One option's (or the default's) data alike: Penumbra's files and swaps all ours, ignoring case — any more of ours
    /// redirect to files the mod does not have, which Penumbra drops as it loads; and Penumbra's manipulations all ours,
    /// read as Penumbra reads them (a number written as text is that number; key order and case of names do not matter).
    /// </summary>
    private static void AssertSameData(string where, string installed, JsonNode? theirs, JsonNode mine)
    {
        foreach (string key in new[] { "Files", "FileSwaps" })
        {
            var penumbra = Paths(theirs?[key]);
            var ours = Paths(mine[key]);
            Assert.True(penumbra.All(ours.Contains), $"{where}: {key}: Penumbra has {string.Join(", ", penumbra.Except(ours))} that ours has not");
            if (key == "Files")
                foreach (var (_, file) in ours.Except(penumbra))
                    Assert.False(File.Exists(Path.Combine(installed, file.Replace('/', Path.DirectorySeparatorChar))),
                                 $"{where}: ours redirects to {file}, which Penumbra dropped though the mod has it");
        }
        var myManipulations = (mine["Manipulations"]?.AsArray() ?? []).ToList();
        foreach (var m in theirs?["Manipulations"]?.AsArray() ?? [])
            Assert.True(myManipulations.Any(x => Covers(x, m)), $"{where}: Penumbra's {m!.ToJsonString()} is missing");
    }

    /// <summary>
    /// Whether ours says everything Penumbra's says: each key Penumbra kept, with the same value as Penumbra reads it.
    /// Penumbra drops what it works out for itself (an Eqdp's ShiftedEntry, an Imc's SecondaryId and BodySlot).
    /// </summary>
    private static bool Covers(JsonNode? mine, JsonNode? theirs)
    {
        if (theirs is JsonObject t)
        {
            if (mine is not JsonObject m) return false;
            foreach (var (key, value) in t)
            {
                var match = m.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
                if (match.Key == null || !Covers(match.Value, value)) return false;
            }
            return true;
        }
        return Canonical(mine) == Canonical(theirs);
    }

    /// <summary>A manipulation as Penumbra reads it: numbers whether written as numbers or text, keys sorted, names in
    /// one case.</summary>
    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                                                .Select(p => p.Key.ToLowerInvariant() + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.TryGetValue<string>(out var s) => decimal.TryParse(s, System.Globalization.NumberStyles.Float,
                                                                 System.Globalization.CultureInfo.InvariantCulture, out var d)
                                                                 ? d.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                                 : "\"" + s.ToLowerInvariant() + "\"",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v => decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var n)
                           ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : v.ToJsonString(),
        _ => "null",
    };

    /// <summary>Two values alike, a missing number being 0 where Penumbra leaves defaults out.</summary>
    private static bool Same(JsonNode? a, JsonNode? b, bool number)
        => number ? (a is null ? 0 : (long)a) == (b is null ? 0 : (long)b) : JsonNode.DeepEquals(a, b);

    /// <summary>A Files or FileSwaps object as sorted pairs, ignoring case and slash direction.</summary>
    private static List<(string, string)> Paths(JsonNode? files)
        => (files as JsonObject ?? [])
           .Select(p => (p.Key.ToLowerInvariant().Replace('\\', '/'), ((string?)p.Value ?? "").ToLowerInvariant().Replace('\\', '/')))
           .Order().ToList();
}
