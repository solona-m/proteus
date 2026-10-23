using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A v3 work copy converted to v4 before the headless size set edits it (<see cref="PenumbraModMeta.MigrateWorkCopy"/>):
/// synthetic folders, so every rule runs on CI. The comparison with Penumbra's own conversion is in
/// <see cref="SizeSetBatchLocalTests"/>.
/// </summary>
public class MigrateWorkCopyTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "proteus-migrate-" + Guid.NewGuid().ToString("N"));
    private readonly string modsRoot;

    public MigrateWorkCopyTests()
    {
        modsRoot = Path.Combine(work, "penumbra");
        Directory.CreateDirectory(modsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
    }

    private string Mod(string meta, params (string Name, string Text)[] files)
    {
        string root = Path.Combine(work, "copy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), meta);
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(root, name), text);
        return root;
    }

    private const string V3Meta = """{ "FileVersion": 3, "Name": "Old", "Author": "Solona", "Version": "1.1.0" }""";

    private static string Group(string name, string extra = "")
        => $$"""
           { "Version": 0, "Name": "{{name}}", "Description": "", "Priority": 0, "Type": "Single", "DefaultSettings": 0{{extra}},
             "Options": [ { "Name": "A", "Files": { "chara/x/{{name}}.mdl": "{{name}}\\a.mdl" } } ] }
           """;

    private static JsonObject Manifest(string root)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)))!.AsObject();

    private static string Snapshot(string root)
        => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order()
                                      .Select(f => Path.GetRelativePath(root, f) + "=" + File.ReadAllText(f)));

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-2")]
    public void Group_files_fold_in_the_order_Penumbra_loads_them() // [EDGE-headless-size-set-2]
    {
        // By file name, as the folder lists them: Penumbra renumbers only FileVersion 1 mods, so an unpadded number or
        // no number at all sorts by its name, not its value.
        string root = Mod(V3Meta, ("group_002_b.json", Group("b")), ("group_010_c.json", Group("c")),
                          ("group_1_a.json", Group("a")), ("group_extra.json", Group("x")),
                          (PenumbraModMeta.LegacyDefaultMod, """{ "Version": 0, "Files": {} }"""));
        var outcome = PenumbraModMeta.MigrateWorkCopy(root, modsRoot)!;

        Assert.Equal((3, 4), (outcome.FromVersion, outcome.Groups));
        Assert.Empty(outcome.Leftover);
        var meta = Manifest(root);
        Assert.Equal(4, (int)meta["FileVersion"]!);
        Assert.Equal(["b", "c", "a", "x"], meta["Groups"]!.AsArray().Select(g => (string)g!["Name"]!));
        // Each group kept key for key, but the v3 Version Penumbra drops.
        var a = meta["Groups"]![2]!.AsObject();
        Assert.False(a.ContainsKey("Version"));
        Assert.Equal(["Name", "Description", "Priority", "Type", "DefaultSettings", "Options"], a.Select(p => p.Key));
        // The manifest's own keys stay, the mod's Version among them.
        Assert.Equal(("Old", "Solona", "1.1.0"), ((string)meta["Name"]!, (string)meta["Author"]!, (string)meta["Version"]!));
        Assert.Empty(Directory.GetFiles(root, "group_*.json"));
        Assert.False(File.Exists(Path.Combine(root, PenumbraModMeta.LegacyDefaultMod)));
        Assert.False(PenumbraModMeta.IsLegacyFolder(root));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-26")]
    public void V3_without_group_files_converts_default_data() // [EDGE-headless-size-set-26]
    {
        string root = Mod(V3Meta, (PenumbraModMeta.LegacyDefaultMod,
                                   """{ "Version": 0, "Files": { "chara/x.mdl": "x.mdl" }, "FileSwaps": {}, "Manipulations": [] }"""));
        var outcome = PenumbraModMeta.MigrateWorkCopy(root, modsRoot)!;

        Assert.Equal(0, outcome.Groups);
        var meta = Manifest(root);
        Assert.Equal(4, (int)meta["FileVersion"]!);
        Assert.Empty(meta["Groups"]!.AsArray());
        Assert.Equal("x.mdl", (string)meta["DefaultData"]!["Files"]!["chara/x.mdl"]!);
        Assert.False(File.Exists(Path.Combine(root, PenumbraModMeta.LegacyDefaultMod)));
    }

    [Theory]
    [Trait("Plan", "EDGE-headless-size-set-27")]
    [InlineData("FileSwaps")]
    [InlineData("Swaps")]
    public void V3_swaps_files_and_manipulations_are_carried(string swapKey) // [EDGE-headless-size-set-27]
    {
        string manipulation = """{ "Type": "Eqdp", "Manipulation": { "Entry": 3, "Gender": "Female", "Race": "Midlander", "SetId": 6000, "Slot": "Body" } }""";
        string root = Mod(V3Meta, (PenumbraModMeta.LegacyDefaultMod, $$"""
            { "Version": 0, "Files": { "chara/a.mdl": "a.mdl" }, "{{swapKey}}": { "chara/b.mdl": "chara/c.mdl" },
              "Manipulations": [ {{manipulation}} ] }
            """));
        PenumbraModMeta.MigrateWorkCopy(root, modsRoot);

        var data = Manifest(root)["DefaultData"]!;
        Assert.Equal("a.mdl", (string)data["Files"]!["chara/a.mdl"]!);
        Assert.Equal("chara/c.mdl", (string)data["FileSwaps"]!["chara/b.mdl"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(manipulation), data["Manipulations"]![0]));
        // And it reads back as the mod's default redirects.
        Assert.Contains(PenumbraModMeta.ReadAllRedirects(root), r => r.GamePath == "chara/a.mdl" && r.File == "a.mdl");
    }

    [Theory]
    [Trait("Plan", "EDGE-headless-size-set-28")]
    [InlineData("group", "migration-failed")]
    [InlineData("default", "migration-failed")]
    [InlineData("version", "version-too-old")]
    [InlineData("manifest", "manifest-unreadable")]
    public void Bad_v3_input_refuses_and_leaves_the_copy(string what, string code) // [EDGE-headless-size-set-28]
    {
        string root = what switch
        {
            "group" => Mod(V3Meta, ("group_001_a.json", "{ not json"), ("group_002_b.json", Group("b"))),
            "default" => Mod(V3Meta, ("group_001_a.json", Group("a")), (PenumbraModMeta.LegacyDefaultMod, "[1, 2")),
            "version" => Mod("""{ "FileVersion": 2, "Name": "Ancient" }""", ("group_001_a.json", Group("a"))),
            _ => Mod("""{ "FileVersion": 3, "Name": """, ("group_001_a.json", Group("a"))),
        };
        string before = Snapshot(root);
        var refused = Assert.Throws<PenumbraModMeta.MigrationRefusedException>(() => PenumbraModMeta.MigrateWorkCopy(root, modsRoot));
        Assert.Equal(code, refused.Code);
        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-27")]
    public void FileSwaps_wins_over_the_old_Swaps_when_both_are_there() // [EDGE-headless-size-set-27]
    {
        string root = Mod(V3Meta, (PenumbraModMeta.LegacyDefaultMod, """
            { "Version": 0, "Files": {}, "FileSwaps": { "chara/a.mdl": "chara/new.mdl" }, "Swaps": { "chara/a.mdl": "chara/old.mdl" } }
            """));
        PenumbraModMeta.MigrateWorkCopy(root, modsRoot);
        Assert.Equal("chara/new.mdl", (string)Manifest(root)["DefaultData"]!["FileSwaps"]!["chara/a.mdl"]!);
    }

    [Fact]
    [Trait("Plan", "AC-headless-size-set-10")]
    public void Conversion_never_touches_the_mods_root() // [AC-headless-size-set-10]
    {
        string installed = Path.Combine(modsRoot, "Old - by Solona");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, PenumbraModMeta.MetaFile), V3Meta);
        File.WriteAllText(Path.Combine(installed, "group_001_a.json"), Group("a"));
        string before = Snapshot(installed);

        foreach (string path in new[] { installed, modsRoot, installed.ToUpperInvariant() })
        {
            var refused = Assert.Throws<PenumbraModMeta.MigrationRefusedException>(
                () => PenumbraModMeta.MigrateWorkCopy(path, modsRoot));
            Assert.Equal("forbidden-root", refused.Code);
        }
        Assert.Equal(before, Snapshot(installed));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-32")]
    public void Locked_group_file_is_left_and_reported() // [EDGE-headless-size-set-32]
    {
        string root = Mod(V3Meta, ("group_001_a.json", Group("a")), ("group_002_b.json", Group("b")));
        PenumbraModMeta.MigrationOutcome outcome;
        using (new FileStream(Path.Combine(root, "group_002_b.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            outcome = PenumbraModMeta.MigrateWorkCopy(root, modsRoot)!;

        Assert.Equal(["group_002_b.json"], outcome.Leftover);
        Assert.Equal(["a", "b"], PenumbraModMeta.TryReadGroups(root)!.Select(g => g.Name));
        Assert.False(PenumbraModMeta.IsLegacyFolder(root));
    }

    [Fact]
    [Trait("Plan", "EDGE-headless-size-set-33")]
    public void V3_group_details_are_kept() // [EDGE-headless-size-set-33]
    {
        string combining = """
            { "Version": 0, "Name": "Mix", "Type": "Combining", "Priority": 2, "DefaultSettings": 1,
              "Options": [ { "Name": "On" } ],
              "Containers": [ { "Files": {} }, { "Files": { "chara/m.mdl": "m.mdl" } } ] }
            """;
        string imc = """
            { "Version": 0, "Name": "Toggles", "Type": "Imc", "Priority": 0, "DefaultSettings": 0,
              "Identifier": { "ObjectType": "Equipment", "PrimaryId": 6000, "EquipSlot": "Body", "Variant": 1 },
              "DefaultEntry": { "AttributeMask": 0 }, "Options": [ { "Name": "Hide", "AttributeMask": 1 } ] }
            """;
        string single = """
            { "Version": 0, "Name": "Größe – 大", "Type": "Single", "Priority": 0, "DefaultSettings": 0,
              "Options": [ { "Name": "Groß", "Description": "über", "Image": "img.png", "Priority": 3,
                             "Files": { "chara/g.mdl": "größe\\g.mdl" } } ] }
            """;
        string root = Mod(V3Meta, ("group_001_mix.json", combining), ("group_002_toggles.json", imc),
                          ("group_003_size.json", single));
        var before = PenumbraModMeta.ReadAllRedirects(root).Select(r => (r.Source, r.GamePath, r.File)).Order().ToList();
        PenumbraModMeta.MigrateWorkCopy(root, modsRoot);

        var groups = Manifest(root)["Groups"]!.AsArray();
        foreach (var (written, text) in groups.Zip(new[] { combining, imc, single }))
        {
            var expected = JsonNode.Parse(text)!.AsObject();
            expected.Remove("Version");
            Assert.True(JsonNode.DeepEquals(expected, written), written!.ToJsonString());
        }
        Assert.Equal(before, PenumbraModMeta.ReadAllRedirects(root).Select(r => (r.Source, r.GamePath, r.File)).Order().ToList());
        // Non-ASCII written as itself, as Penumbra writes it.
        Assert.Contains("Größe – 大", File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
    }

    [Theory]
    [Trait("Plan", "AC-headless-size-set-10")]
    [InlineData(@"E:\SomeMod", @"E:\", true)]
    [InlineData(@"E:\", @"E:\", true)]
    [InlineData(@"E:\Penumbra\Mod", @"E:\Penumbra", true)]
    [InlineData(@"e:\penumbra", @"E:\Penumbra\", true)]
    [InlineData(@"E:\PenumbraOther\Mod", @"E:\Penumbra", false)]
    [InlineData(@"F:\SomeMod", @"E:\", false)]
    [InlineData(@"\\server\share\Mods\A", @"\\server\share", true)]
    [InlineData(@"\\server\shared\A", @"\\server\share", false)]
    public void A_path_is_at_or_under_a_root_drives_and_shares_included(string path, string root, bool under) // [AC-headless-size-set-10]
        => Assert.Equal(under, PenumbraModMeta.IsAtOrUnder(path, root));

    [Fact]
    public void A_v4_folder_is_left_alone()
    {
        string meta = """{ "FileVersion": 4, "Name": "New", "Groups": [], "DefaultData": { "Files": {} } }""";
        string root = Mod(meta);
        Assert.Null(PenumbraModMeta.MigrateWorkCopy(root, modsRoot));
        Assert.Equal(meta, File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile)));
    }
}
