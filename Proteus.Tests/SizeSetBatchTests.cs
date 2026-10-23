extern alias RefitCli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The headless size set (<c>Proteus.Refit --size-set</c>) on synthetic folders: its arguments, its refusals, its report
/// and what it never touches. Real refits are in <see cref="SizeSetBatchLocalTests"/>.
/// </summary>
public class SizeSetBatchTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "proteus-sizeset-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string modsRoot, game, plugin;

    public SizeSetBatchTests()
    {
        modsRoot = Path.Combine(work, "penumbra");
        game = Path.Combine(work, "sqpack");
        plugin = Path.Combine(work, "plugin");
        Directory.CreateDirectory(modsRoot);
        Directory.CreateDirectory(Path.Combine(game, "ffxiv"));
        Maps(plugin, men: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
    }

    /// <summary>A plugin folder whose maps open as TIFFs: the header alone, which is all the check reads.</summary>
    private static void Maps(string pluginDir, bool men, params string[] except)
    {
        Directory.CreateDirectory(Path.Combine(pluginDir, "uvmaps"));
        foreach (string name in men ? [.. UvMapFiles.WomenMaps, .. UvMapFiles.MenMaps] : UvMapFiles.WomenMaps)
            if (!except.Contains(name))
                File.WriteAllBytes(Path.Combine(pluginDir, "uvmaps", name), [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0]);
    }

    /// <summary>A mod whose only model is a hair: nothing for the size set. v4, or v3 with a group file.</summary>
    private string HairMod(bool v3 = false)
    {
        string root = Path.Combine(work, "hair-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        const string hair = """{ "chara/human/c0201/obj/hair/h0101/model/c0201h0101_hir.mdl": "hair.mdl" }""";
        if (v3)
        {
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """{ "FileVersion": 3, "Name": "Hair" }""");
            File.WriteAllText(Path.Combine(root, "group_001_hair.json"), $$"""
                { "Version": 0, "Name": "Hair", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
                  "Options": [ { "Name": "On", "Files": {{hair}} } ] }
                """);
        }
        else
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), $$"""
                { "FileVersion": 4, "Name": "Hair", "DefaultData": { "Files": {{hair}}, "FileSwaps": {}, "Manipulations": [] },
                  "Groups": [] }
                """);
        return root;
    }

    private static string Snapshot(string root)
        => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order()
                                      .Select(f => Path.GetRelativePath(root, f) + "=" + File.ReadAllText(f)));

    private (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = RefitCli::Proteus.Refit.Program.Dispatch(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string[] Args(string mod, string bodies = "all", params string[] more)
        => ["--size-set", mod, "--bodies", bodies, "--mods-root", modsRoot, "--game", game, "--uvmaps", plugin, .. more];

    [Fact]
    [Trait("Plan", "AC-headless-size-set-2")]
    public void Size_table_is_the_site_table() // [AC-headless-size-set-2]
    {
        string[] Names(string key, string slot, bool yas = false)
            => SupportedBodies.Of(key, slot, yas) is { } body ? body.Sizes.Select(s => SupportedBodies.OptionName(body, s)).ToArray() : [];

        Assert.Equal(["Neolithe XS", "Neolithe S", "Neolithe M", "Neolithe L"], Names("neolithe", "_top"));
        Assert.Equal(["Bibo S", "Bibo M", "Bibo L"], Names("bibo", "_top"));
        Assert.Equal(["Rue S", "Rue M", "Rue L"], Names("rue", "_top"));
        Assert.Equal(["YAB S", "YAB M", "YAB L"], Names("yab", "_top"));
        Assert.Equal(["YAB Buff S", "YAB Buff M", "YAB Buff L"], Names("yab-buff", "_top"));
        Assert.Equal(["Tre S", "Tre M", "Tre L"], Names("tre", "_top"));
        Assert.Equal(["Rosaline M", "Rosaline L", "Rosaline Coconut"], Names("rosaline", "_top"));

        Assert.Equal(["Neolithe S", "Neolithe M", "Neolithe L"], Names("neolithe", "_dwn"));
        Assert.Equal(["Bibo S", "Bibo M", "Bibo L"], Names("bibo", "_dwn"));
        Assert.Equal(["Rue WC"], Names("rue", "_dwn"));
        Assert.Equal(["YAB WC", "YAB SC"], Names("yab", "_dwn"));
        Assert.Equal(["Tre Medium", "Tre Large"], Names("tre", "_dwn"));
        Assert.Equal(["Rosaline"], Names("rosaline", "_dwn"));

        Assert.Equal(["TBSE"], Names("tbse", "_top"));
        Assert.Equal(["TBSE-X"], Names("tbse-x", "_top"));

        // YAS for Rue, YAB, YAB Buff and Tre, the same sizes; never for Neolithe.
        foreach (string key in new[] { "rue", "yab", "yab-buff", "tre" })
            Assert.Equal(Names(key, "_top"), Names(key, "_top", yas: true));
        Assert.Null(SupportedBodies.Of("neolithe", "_top", true));
        Assert.Null(SupportedBodies.Of("neolithe", "_dwn", true));
        foreach (var (key, label, _) in SupportedBodies.Choices)
            foreach (string slot in new[] { "_top", "_dwn" })
                Assert.All(Names(key, slot), n => Assert.StartsWith(label, n, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-headless-size-set-7")]
    public void Rosaline_Coconut_is_a_Rosaline_size() // [AC-headless-size-set-7]
    {
        var tops = SupportedBodies.Of("rosaline", "_top", false)!;
        Assert.Equal(["M", "L", "Coconut"], tops.Sizes.Select(s => s.Name));
        var coconut = tops.Sizes[2];
        Assert.Equal("files/common/7/c0201e0000_top.mdl", coconut.Rel);
        Assert.Equal("files/common/13/c0201e0000_dwn.mdl", coconut.LegsRel);
        Assert.Equal("Rosaline Coconut", SupportedBodies.OptionName(tops, coconut));
    }

    [Fact]
    [Trait("Plan", "AC-headless-size-set-5")]
    public void Report_builder_shape() // [AC-headless-size-set-5]
    {
        var runLog = new SizeSetRefit.RunLog();
        var piece = new SizeSetRefit.Piece("chara/equipment/e6255/model/c0201e6255_top.mdl", "_top", "Size", [("M", "m.mdl")],
                                           "Body — e6255", "Body — e6255", "0201");
        runLog.Add(piece, SizeSetRefit.SizeStatus.Made, null, null, "Size", "Rue S", "rue", "hs-Rue+", true, from: "Neolithe M",
                   measures: new SizeSetRefit.Measures(2, 12, 8.333f, 3.1f, 140, 0.9712f, 1), seconds: 11.437);
        runLog.Add(piece, SizeSetRefit.SizeStatus.Refused, SizeSetRefit.Codes.RefusedPair, "uv coverage 80%", "Size", "Tre M", "tre");
        runLog.Add(piece, SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.NotInstalled, "Tre is not installed", "Size", body: "tre");
        runLog.Add(piece, SizeSetRefit.SizeStatus.Failed, SizeSetRefit.Codes.PieceFailed, "boom");
        runLog.Add(piece, SizeSetRefit.SizeStatus.Made, null, null, "Men's body", "TBSE", "tbse", "The Body SE", men: true,
                   measures: new SizeSetRefit.Measures(0, 3, 1f, 0f, 0, 1f, 0, 0.95), seconds: 2);
        var outcome = new SizeSetBatch.Outcome(["rue", "tre"], null, null, new PenumbraModMeta.MigrationOutcome(3, 4, []),
                                               false, [new SizeSetBatch.BodyMod("rue", "_top", true, "hs-Rue+")], ["tre"],
                                               30.123, 6.1, 0.04);

        var report = SizeSetReport.Build("work/mod", 1368, outcome, runLog, men: true, ends: true);

        Assert.Equal(2, (int)report["made"]!);
        Assert.Equal(1368, (int)report["build"]!);
        Assert.Equal(3, (int)report["migrated"]!["fromVersion"]!);
        Assert.Equal("hs-Rue+", (string)report["bodyMods"]![0]!["folder"]!);
        Assert.Equal(["tre"], report["missingBodies"]!.AsArray().Select(k => (string)k!));
        var sizes = report["sizes"]!.AsArray();
        Assert.Equal(["made", "refused", "skipped", "failed", "made"], sizes.Select(s => (string)s!["status"]!));
        string[] keys = ["piece", "gamePath", "slot", "group", "option", "body", "bodyMod", "yas", "men", "status", "reasonCode",
                         "reason", "from", "folded", "unmatched", "worstMoveMm", "worstPushMm", "pushed", "snapRate",
                         "shapeFallbacks", "coverage", "skinOnly", "seconds"];
        Assert.All(sizes, s => Assert.Equal(keys, s!.AsObject().Select(p => p.Key)));
        var made = sizes[0]!;
        Assert.Equal((2, 12, 8.33, 0.971, 11.44), ((int)made["folded"]!, (int)made["unmatched"]!, (double)made["worstMoveMm"]!,
                                                  (double)made["snapRate"]!, (double)made["seconds"]!));
        Assert.Null(made["coverage"]);
        Assert.Equal(0.95, (double)sizes[4]!["coverage"]!);
        Assert.All(sizes.Skip(1).Take(3), s => Assert.Null(s!["folded"]));
        Assert.Equal("refused-pair", (string)sizes[1]!["reasonCode"]!);
        // Invariant numbers whatever the culture.
        Assert.Contains("\"worstMoveMm\": 8.33", SizeSetReport.ToText(report));
    }

    [Fact]
    [Trait("Plan", "AC-headless-size-set-5")]
    public void Report_file_matches_stdout() // [AC-headless-size-set-5]
    {
        string mod = HairMod();
        string reportFile = Path.Combine(work, "report.json");
        var (exit, stdout, _) = Cli(Args(mod, "all", "--report", reportFile));
        Assert.Equal(0, exit);
        Assert.Equal(stdout.Trim(), File.ReadAllText(reportFile).Trim());
    }

    [Fact]
    [Trait("Plan", "AC-headless-size-set-6")]
    public void Cli_runs_with_no_plugin_or_ipc() // [AC-headless-size-set-6]
    {
        var (exit, stdout, stderr) = Cli(Args(HairMod()));
        Assert.True(exit == 0, stderr);
        var report = JsonNode.Parse(stdout)!;
        Assert.Equal(Plugin.BuildNumber, (int)report["build"]!);
        Assert.Null(report["refusal"]);
    }

    [Theory]
    [Trait("Plan", "AC-headless-size-set-8")]
    [InlineData("no-bodies")]
    [InlineData("unknown-body")]
    [InlineData("no-mods-root")]
    [InlineData("missing-mods-root")]
    [InlineData("missing-game")]
    [InlineData("not-sqpack")]
    [InlineData("missing-skeleton")]
    [InlineData("no-uvmaps")]
    [InlineData("no-meta")]
    public void Usage_errors_exit_2_and_write_nothing(string error) // [AC-headless-size-set-8]
    {
        string mod = HairMod();
        string before = Snapshot(mod);
        string[] args = error switch
        {
            "no-bodies" => ["--size-set", mod, "--mods-root", modsRoot, "--game", game, "--uvmaps", plugin],
            "unknown-body" => Args(mod, "rue,lavabod"),
            "no-mods-root" => ["--size-set", mod, "--bodies", "all", "--game", game, "--uvmaps", plugin],
            "missing-mods-root" => ["--size-set", mod, "--bodies", "all", "--mods-root", Path.Combine(work, "nope"), "--game", game,
                                    "--uvmaps", plugin],
            "missing-game" => ["--size-set", mod, "--bodies", "all", "--mods-root", modsRoot, "--game", Path.Combine(work, "nope"),
                               "--uvmaps", plugin],
            "not-sqpack" => ["--size-set", mod, "--bodies", "all", "--mods-root", modsRoot, "--game", work, "--uvmaps", plugin],
            "missing-skeleton" => Args(mod, "all", "--men", "--skeleton", Path.Combine(work, "none.sklb")),
            "no-uvmaps" => ["--size-set", mod, "--bodies", "all", "--mods-root", modsRoot, "--game", game],
            _ => Args(Directory.CreateDirectory(Path.Combine(work, "no-manifest")).FullName),
        };
        var (exit, stdout, stderr) = Cli(args);
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("--", stderr);
        Assert.Equal(before, Snapshot(mod));
    }

    [Theory]
    [Trait("Plan", "AC-headless-size-set-8")]
    [Trait("Plan", "EDGE-headless-size-set-2")]
    [InlineData("""{ "FileVersion": 4, "Name": """, "manifest-unreadable")]
    [InlineData("""{ "FileVersion": 2, "Name": "Ancient" }""", "version-too-old")]
    public void Unreadable_or_too_old_manifest_is_refused_with_a_report(string meta, string code) // [AC-headless-size-set-8]
    {
        string mod = Path.Combine(work, "bad");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, PenumbraModMeta.MetaFile), meta);
        string before = Snapshot(mod);
        var (exit, stdout, _) = Cli(Args(mod));
        Assert.Equal(1, exit);
        var report = JsonNode.Parse(stdout)!;
        Assert.Equal(code, (string)report["refusalCode"]!);
        Assert.NotNull(report["refusal"]);
        Assert.Empty(report["sizes"]!.AsArray());
        Assert.Equal(before, Snapshot(mod));
    }

    [Theory]
    [Trait("Plan", "AC-headless-size-set-8")]
    [Trait("Plan", "EDGE-headless-size-set-1")]
    [InlineData(false)]
    [InlineData(true)]
    public void Mod_without_body_gear_is_skipped_and_untouched(bool v3) // [AC-headless-size-set-8] [EDGE-headless-size-set-1]
    {
        string mod = HairMod(v3);
        string before = Snapshot(mod);
        var (exit, stdout, stderr) = Cli(Args(mod, "all", "--men"));
        Assert.True(exit == 0, stderr);
        var report = JsonNode.Parse(stdout)!;
        Assert.Equal(0, (int)report["made"]!);
        Assert.Null(report["migrated"]);
        var only = Assert.Single(report["sizes"]!.AsArray());
        Assert.Equal(("skipped", "no-pieces"), ((string)only!["status"]!, (string)only["reasonCode"]!));
        Assert.Equal(before, Snapshot(mod));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-3")]
    public void Mod_inside_the_mods_root_is_refused() // [EDGE-headless-size-set-3]
    {
        string inside = Path.Combine(modsRoot, "Installed");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, PenumbraModMeta.MetaFile), """{ "FileVersion": 3, "Name": "Installed" }""");
        string before = Snapshot(inside);
        foreach (string mod in new[] { inside, inside.ToUpperInvariant() })
        {
            var (exit, stdout, stderr) = Cli(Args(mod));
            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("inside --mods-root", stderr);
        }
        // The mod folder itself is not a mod: no manifest.
        Assert.Equal(2, Cli(Args(modsRoot)).Exit);
        Assert.Equal(before, Snapshot(inside));
    }

    [Theory]
    [Trait("Plan", "EDGE-headless-size-set-4")]
    [InlineData("pmp", "unpack it first")]
    [InlineData("missing", "does not exist")]
    [InlineData("no-meta", "has no meta.json")]
    public void Pmp_or_missing_folder_is_a_usage_error(string what, string said) // [EDGE-headless-size-set-4]
    {
        string path = Path.Combine(work, "x-" + what);
        if (what == "pmp") File.WriteAllBytes(path += ".pmp", [0x50, 0x4b, 3, 4]);
        if (what == "no-meta") Directory.CreateDirectory(path);
        var (exit, _, stderr) = Cli(Args(path));
        Assert.Equal(2, exit);
        Assert.Contains(said, stderr);
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-5")]
    public void Body_keys_are_normalised_and_validated() // [EDGE-headless-size-set-5]
    {
        Assert.Equal(["bibo", "rue"], SizeSetBatch.ParseKeys(" Rue,BIBO,rue ", out _));
        Assert.Equal(SizeSetBatch.WomensKeys, SizeSetBatch.ParseKeys("all", out _));
        Assert.Equal(["neolithe", "bibo", "rue", "yab", "yab-buff", "tre", "rosaline"], SizeSetBatch.WomensKeys);
        Assert.Equal(["rue", "tbse"], SizeSetBatch.ParseKeys("tbse, rue", out _));
        Assert.Null(SizeSetBatch.ParseKeys("foo", out var unknown));
        Assert.Contains("foo", unknown);
        Assert.Contains("neolithe", unknown);
        Assert.Null(SizeSetBatch.ParseKeys(" , ", out var none));
        Assert.Contains("no body", none);
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-6")]
    public void Maps_must_be_real_tiffs() // [EDGE-headless-size-set-6]
    {
        string bad = Path.Combine(work, "badmaps");
        Maps(bad, men: false, except: UvMapFiles.WomenMaps[0]);
        File.WriteAllText(Path.Combine(bad, "uvmaps", UvMapFiles.WomenMaps[1]),
                          "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 123\n");
        Assert.Equal(UvMapFiles.WomenMaps, UvMapFiles.Missing(bad, men: false));
        Assert.Equal([.. UvMapFiles.WomenMaps, .. UvMapFiles.MenMaps], UvMapFiles.Missing(bad, men: true));

        var (exit, _, stderr) = Cli(["--size-set", HairMod(), "--bodies", "all", "--mods-root", modsRoot, "--game", game,
                                     "--uvmaps", bad]);
        Assert.Equal(2, exit);
        Assert.Contains(UvMapFiles.WomenMaps[0], stderr);
        Assert.Contains(UvMapFiles.WomenMaps[1], stderr);

        // Women's maps alone do for a run without men's versions, not one with them.
        string women = Path.Combine(work, "womenmaps");
        Maps(women, men: false);
        Assert.Equal(0, Cli(["--size-set", HairMod(), "--bodies", "all", "--mods-root", modsRoot, "--game", game,
                             "--uvmaps", women]).Exit);
        var (menExit, _, menErr) = Cli(["--size-set", HairMod(), "--bodies", "all", "--men", "--mods-root", modsRoot,
                                        "--game", game, "--uvmaps", women]);
        Assert.Equal(2, menExit);
        Assert.Contains(UvMapFiles.MenMaps[0], menErr);

        string tiff = Path.Combine(work, "mm.tif");
        File.WriteAllBytes(tiff, [(byte)'M', (byte)'M', 0, 42]);
        Assert.True(UvMapFiles.IsMap(tiff));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-16")]
    public void Women_piece_with_authors_mens_model_is_not_crossed() // [EDGE-headless-size-set-16]
    {
        SizeSetRefit.Piece Piece(string race, int set)
            => new($"chara/equipment/e{set:D4}/model/c{race}e{set:D4}_top.mdl", "_top", null, [(null, "x.mdl")], $"e{set}", $"e{set}", race);
        var across = SizeSetRefit.AcrossPieces([Piece("0201", 6000), Piece("0101", 6000), Piece("0201", 6001)]);
        Assert.Equal(["chara/equipment/e6001/model/c0201e6001_top.mdl"], across.Select(p => p.GamePath));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-20")]
    public void Dispatch_leaves_the_temp_folders_alone() // [EDGE-headless-size-set-20]
    {
        var before = (VanillaBodyCatalog.Dir, BakedBodyCatalog.Dir);
        Assert.Equal(0, Cli(Args(HairMod())).Exit);
        Assert.Equal(before, (VanillaBodyCatalog.Dir, BakedBodyCatalog.Dir));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-20")]
    public void Temp_paths_are_per_run() // [EDGE-headless-size-set-20]
    {
        var a = RefitCli::Proteus.Refit.TempFolders.Paths(1234, Guid.NewGuid());
        var b = RefitCli::Proteus.Refit.TempFolders.Paths(1234, Guid.NewGuid());
        Assert.StartsWith(Path.GetTempPath(), a.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1234", a.Root);
        Assert.NotEqual(a.Root, b.Root);
        Assert.StartsWith(a.Root, a.Vanilla, StringComparison.Ordinal);
        Assert.StartsWith(a.Root, a.Baked, StringComparison.Ordinal);
        Assert.NotEqual(a.Vanilla, a.Baked);
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-23")]
    public void No_game_bodies_without_ends() // [EDGE-headless-size-set-23]
        => Assert.Empty(SizeSetRefit.GameBodies(["rue", "tbse"], _ => throw new InvalidOperationException("read"), ends: false));

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-29")]
    public void Made_counts_entries_not_options() // [EDGE-headless-size-set-29]
    {
        var runLog = new SizeSetRefit.RunLog();
        var top = new SizeSetRefit.Piece("chara/equipment/e6000/model/c0201e6000_top.mdl", "_top", "Size", [("M", "t.mdl")], "Top", "Top", "0201");
        var legs = top with { GamePath = "chara/equipment/e6000/model/c0201e6000_dwn.mdl", Slot = "_dwn", FollowsChest = true };
        // The legs join the chest's "Rue S": one option, two sizes made.
        runLog.Add(top, SizeSetRefit.SizeStatus.Made, null, null, "Size", "Rue S", "rue");
        runLog.Add(legs, SizeSetRefit.SizeStatus.Made, null, null, "Size", "Rue S", "rue");
        runLog.Add(top, SizeSetRefit.SizeStatus.Skipped, SizeSetRefit.Codes.AlreadyThere, "there", "Size", "Rue M", "rue");
        Assert.Equal(2, runLog.Made);
        Assert.Single(runLog.Sizes.Where(s => s.Status == SizeSetRefit.SizeStatus.Made).Select(s => s.Option).Distinct());
    }

    /// <summary>A women's top in a mod, its model absent: the men's versions are asked for but can never be made here.</summary>
    private string WomensTopMod()
    {
        string root = Path.Combine(work, "top-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), """
            { "FileVersion": 4, "Name": "Top", "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
              "Groups": [ { "Name": "Size", "Type": "Single", "Priority": 0, "DefaultSettings": 0, "Options": [
                { "Name": "M", "Files": { "chara/equipment/e6000/model/c0201e6000_top.mdl": "m\\top.mdl",
                                          "chara/equipment/e6000/model/c0201e6000_dwn.mdl": "m\\dwn.mdl" } } ] } ] }
            """);
        return root;
    }

    [Theory]
    [Trait("Plan", "EDGE-headless-size-set-30")]
    [InlineData(false, "men-maps-missing")]
    [InlineData(true, "no-game-data")]
    public void Every_piece_and_mens_key_has_an_outcome(bool maps, string code) // [EDGE-headless-size-set-30]
    {
        string mod = WomensTopMod();
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        string pluginDir = Path.Combine(work, "p-" + maps);
        if (maps)
            foreach (string name in UvMapFiles.MenMaps)
            {
                Directory.CreateDirectory(Path.Combine(pluginDir, "uvmaps"));
                File.WriteAllBytes(Path.Combine(pluginDir, "uvmaps", name), [(byte)'I', (byte)'I', 42, 0]);
            }
        var runLog = new SizeSetRefit.RunLog();
        SizeSetRefit.Run(new SizeSetRefit.Request(mod, ["tbse@women", "tbse-x@women"], Yas: true, Remake: false, [],
                                                  Across: new SizeSetRefit.AcrossInputs(_ => null)),
                         runLog, maps ? new UVRemapService(log, pluginDir) : null, log, _ => { }, CancellationToken.None);

        var mens = runLog.Sizes.Where(s => s.Men).ToList();
        Assert.Equal(4, mens.Count);   // two pieces × two men's bodies
        Assert.All(mens, s => Assert.Equal((SizeSetRefit.SizeStatus.Skipped, code), (s.Status, s.ReasonCode)));
        Assert.Equal(["TBSE", "TBSE", "TBSE-X", "TBSE-X"], mens.Select(s => s.Option!).Order());
        Assert.All(mens, s => Assert.Contains("c0101e6000", s.GamePath));
    }
}
