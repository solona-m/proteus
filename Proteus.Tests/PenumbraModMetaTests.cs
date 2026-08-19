using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Redirect writing must follow the format the mod folder is already in. Penumbra's FileVersion 4 moved
/// the default option into meta.json's DefaultData, but an older Penumbra has never heard of that key and
/// applies no redirects at all — silently, with a clean log. These tests pin both directions.
/// </summary>
public class PenumbraModMetaTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "proteus_meta_" + System.IO.Path.GetRandomFileName());
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static readonly Dictionary<string, string> OneRedirect =
        new() { ["chara/foo_d.tex"] = @"textures\foo_d.tex" };

    [Fact]
    public void WriteRedirects_on_a_v3_folder_writes_default_mod_json_and_leaves_meta_alone()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"),
            """{"FileVersion":3,"Name":"Proteus","Author":"Proteus"}""");

        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect);

        // The redirect lands where an older Penumbra actually looks for it.
        var def = JsonDocument.Parse(File.ReadAllText(tmp.File("default_mod.json"))).RootElement;
        Assert.Equal(@"textures\foo_d.tex",
            def.GetProperty("Files").GetProperty("chara/foo_d.tex").GetString());

        // meta.json is untouched — no surprise upgrade to a format this Penumbra can't read.
        var meta = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement;
        Assert.Equal(3, meta.GetProperty("FileVersion").GetInt32());
        Assert.False(meta.TryGetProperty("DefaultData", out _));
    }

    [Fact]
    public void WriteRedirects_on_a_v4_folder_writes_DefaultData_and_removes_the_legacy_file()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"),
            """{"FileVersion":4,"Identifier":"abc-123","Name":"Proteus","ModTags":["keep"]}""");
        File.WriteAllText(tmp.File("default_mod.json"), """{"Files":{"stale":"stale"}}""");

        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect);

        var meta = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement;
        Assert.Equal(4, meta.GetProperty("FileVersion").GetInt32());
        Assert.Equal(@"textures\foo_d.tex",
            meta.GetProperty("DefaultData").GetProperty("Files").GetProperty("chara/foo_d.tex").GetString());
        // Identity and user-set fields survive the rewrite.
        Assert.Equal("abc-123", meta.GetProperty("Identifier").GetString());
        Assert.Equal("keep", Assert.Single(meta.GetProperty("ModTags").EnumerateArray()).GetString());
        // The superseded file is gone, so it can't be mistaken for live state.
        Assert.False(File.Exists(tmp.File("default_mod.json")));
    }

    // A folder Proteus has just created, before Penumbra has ever loaded (and possibly migrated) it.
    [Fact]
    public void WriteRedirects_with_no_manifest_at_all_uses_the_legacy_layout()
    {
        using var tmp = new TempDir();

        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect);

        Assert.True(File.Exists(tmp.File("default_mod.json")));
    }

    // A second-skin shell needs its EQDP entry to survive whichever format it is written in — without it
    // the accessory the shell rides on loads the wrong race/gender model. The two writers serialize
    // manipulations by different mechanisms, so both are pinned here: the legacy path hands an
    // IReadOnlyList<object> to the serializer (which resolves the runtime type only because the declared
    // element type is exactly `object`), the v4 path writes each element with an explicit GetType().
    private static readonly IReadOnlyList<object> Eqdp =
    [
        new
        {
            Type = "Eqdp",
            Manipulation = new { Gender = "Female", Race = "Midlander", SetId = 31, Slot = "RFinger", Entry = 192 },
        },
    ];

    private static void AssertEqdpRoundTripped(JsonElement manipulations)
    {
        var m = Assert.Single(manipulations.EnumerateArray());
        Assert.Equal("Eqdp", m.GetProperty("Type").GetString());
        var inner = m.GetProperty("Manipulation");
        Assert.Equal("Female", inner.GetProperty("Gender").GetString());
        Assert.Equal("RFinger", inner.GetProperty("Slot").GetString());
        Assert.Equal(192, inner.GetProperty("Entry").GetInt32());
    }

    [Fact]
    public void WriteRedirects_keeps_manipulation_contents_on_the_v3_path()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Proteus"}""");

        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect, manipulations: Eqdp);

        var def = JsonDocument.Parse(File.ReadAllText(tmp.File("default_mod.json"))).RootElement;
        AssertEqdpRoundTripped(def.GetProperty("Manipulations"));
    }

    [Fact]
    public void WriteRedirects_keeps_manipulation_contents_on_the_v4_path()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":4,"Name":"Proteus"}""");

        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect, manipulations: Eqdp);

        var meta = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement;
        AssertEqdpRoundTripped(meta.GetProperty("DefaultData").GetProperty("Manipulations"));
    }

    [Fact]
    public void NewMetaJson_is_written_in_the_universally_readable_format()
    {
        var doc = JsonDocument.Parse(PenumbraModMeta.NewMetaJson("Mod", "Me", "desc")).RootElement;
        Assert.Equal(3, doc.GetProperty("FileVersion").GetInt32());
        Assert.Equal("Mod", doc.GetProperty("Name").GetString());
        Assert.False(doc.TryGetProperty("DefaultData", out _));
    }

    [Theory]
    [InlineData("""{"FileVersion":4}""", 4)]
    [InlineData("""{"FileVersion":3}""", 3)]
    [InlineData("""{"Name":"no version"}""", 3)]   // absent → assume the older format
    [InlineData("not json at all", 3)]             // unreadable → assume the older format
    public void ReadFileVersion_defaults_to_the_legacy_format(string json, int expected)
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), json);
        Assert.Equal(expected, PenumbraModMeta.ReadFileVersion(tmp.Path));
    }

    // TryReadDefaultData is what lets a composite narrow the LIVE manifest for a moment (to unmask a base
    // path) and then put it back exactly as it was. Anything it drops on the way through is a redirect or
    // an EQDP row that silently stops applying, so both directions are pinned here.

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void TryReadDefaultData_round_trips_files_and_manipulations(int fileVersion)
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), $$"""{"FileVersion":{{fileVersion}},"Name":"Proteus"}""");
        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect, manipulations: Eqdp);

        var read = PenumbraModMeta.TryReadDefaultData(tmp.Path);
        Assert.NotNull(read);
        Assert.Equal(@"textures\foo_d.tex", read!.Value.Files["chara/foo_d.tex"]);
        Assert.Single(read.Value.Manipulations);

        // Write what we read straight back out: the EQDP row must survive being a JsonElement in between.
        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", read.Value.Files,
                                       manipulations: read.Value.Manipulations);

        var again = PenumbraModMeta.TryReadDefaultData(tmp.Path);
        Assert.NotNull(again);
        Assert.Equal(read.Value.Files, again!.Value.Files);

        var written = fileVersion >= 4
            ? JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement
                  .GetProperty("DefaultData").GetProperty("Manipulations")
            : JsonDocument.Parse(File.ReadAllText(tmp.File("default_mod.json"))).RootElement
                  .GetProperty("Manipulations");
        AssertEqdpRoundTripped(written);
    }

    [Fact]
    public void TryReadDefaultData_reports_unknown_rather_than_empty_when_there_is_no_manifest()
    {
        using var tmp = new TempDir();
        // No meta.json at all, and a v3 folder with no default_mod.json. Both must read as "unknown":
        // a caller that took an empty file map at face value would narrow the manifest to nothing.
        Assert.Null(PenumbraModMeta.TryReadDefaultData(tmp.Path));

        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Proteus"}""");
        Assert.Null(PenumbraModMeta.TryReadDefaultData(tmp.Path));
    }

    // Penumbra writes non-ASCII names as themselves (it serializes with Newtonsoft); System.Text.Json's
    // default encoder escapes them. Since Proteus REWRITES Penumbra's own files, the default would turn a
    // mod's 正常 into "正常" in its manifest — valid JSON, unreadable to its author. These
    // assert on the raw text on purpose: JsonDocument decodes both forms identically, so parsing the
    // result back could never catch a regression here.

    [Fact]
    public void NewMetaJson_writes_non_ascii_names_as_themselves()
    {
        var json = PenumbraModMeta.NewMetaJson("彩绘比基尼", "ttrrffxiv", "Ярко");
        Assert.Contains("\"Name\": \"彩绘比基尼\"", json);
        Assert.Contains("Ярко", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void WriteSingleSelectGroup_writes_non_ascii_names_as_themselves_in_both_formats()
    {
        foreach (var fileVersion in new[] { 3, 4 })
        {
            using var tmp = new TempDir();
            File.WriteAllText(tmp.File("meta.json"),
                $$"""{"FileVersion":{{fileVersion}},"Name":"彩绘比基尼"}""");

            PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 0, "Style", ["正常", "光沢"], 0);

            // v4 splices into meta.json; v3 leaves it alone and drops a group_NNN_ file beside it.
            var written = File.ReadAllText(fileVersion >= 4
                ? tmp.File("meta.json")
                : Directory.EnumerateFiles(tmp.Path, "group_*.json").Single());
            Assert.Contains("正常", written);
            Assert.DoesNotContain("\\u", written);

            // The v4 rewrite copies untouched fields through as JsonElements, which are re-escaped by the
            // WRITER's encoder — so the mod's own name is only safe if that writer was configured too.
            if (fileVersion >= 4) Assert.Contains("彩绘比基尼", File.ReadAllText(tmp.File("meta.json")));
        }
    }

    [Fact]
    public void AtomicWrite_leaves_no_temp_files_behind()
    {
        using var tmp = new TempDir();
        PenumbraModMeta.AtomicWrite(tmp.File("x.json"), "{}");
        Assert.Equal("{}", File.ReadAllText(tmp.File("x.json")));
        Assert.Empty(Directory.EnumerateFiles(tmp.Path, "*.tmp"));
    }

    // ── Option groups ────────────────────────────────────────────────────────

    [Fact]
    public void WriteSingleSelectGroup_on_a_v3_folder_writes_a_numbered_group_file()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Ven"}""");

        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 0, "Body UV", ["bibo", "gen3"], 1);

        // group_001_… : ReadGroupOrder takes the ordinal from THIS number on an unmigrated folder.
        var file = tmp.File("group_001_body uv.json");
        Assert.True(File.Exists(file));
        var g = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        Assert.Equal("Body UV", g.GetProperty("Name").GetString());
        Assert.Equal("Single", g.GetProperty("Type").GetString());
        Assert.Equal(1, g.GetProperty("DefaultSettings").GetInt32());
        Assert.Equal(["bibo", "gen3"],
            g.GetProperty("Options").EnumerateArray().Select(o => o.GetProperty("Name").GetString()));
        // Options carry no redirects — the group exists so Penumbra shows a selector.
        Assert.Empty(g.GetProperty("Options")[0].GetProperty("Files").EnumerateObject());

        // v3 stays v3: no surprise upgrade to a format an older Penumbra can't read.
        Assert.Equal(3, JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json")))
            .RootElement.GetProperty("FileVersion").GetInt32());
    }

    [Fact]
    public void WriteSingleSelectGroup_on_a_v4_folder_splices_at_the_requested_ordinal()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Identifier":"abc-123","Name":"Ven","ModTags":["keep"],
             "Groups":[{"Name":"First","Type":"Single"},{"Name":"Second","Type":"Single"}]}
            """);

        // Ordinal 1 = between the two. ReadGroupOrder reads a v4 group's priority from its ARRAY POSITION,
        // so appending instead would silently give it the lowest priority in the mod.
        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 1, "Body UV", ["bibo", "gen3"], 0);

        var meta = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement;
        Assert.Equal(["First", "Body UV", "Second"],
            meta.GetProperty("Groups").EnumerateArray().Select(g => g.GetProperty("Name").GetString()));

        // And the ordinal the discovery side derives matches what was asked for.
        Assert.Equal(1, SidecarDiscoveryService.ReadGroupOrder(tmp.Path)["Body UV"]);

        // Every other key survives — Identifier above all, since it's how Penumbra keys the mod.
        Assert.Equal("abc-123", meta.GetProperty("Identifier").GetString());
        Assert.Equal("keep", meta.GetProperty("ModTags")[0].GetString());
        Assert.False(File.Exists(tmp.File("group_002_body uv.json")));
    }

    [Fact]
    public void WriteSingleSelectGroup_replaces_a_group_of_the_same_name_rather_than_duplicating_it()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Identifier":"abc-123","Name":"Ven",
             "Groups":[{"Name":"body uv","Type":"Single","Options":[{"Name":"stale"}]},
                       {"Name":"Other","Type":"Single"}]}
            """);

        // Past the end clamps to last, and the case-insensitive name match drops the old one.
        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 99, "Body UV", ["bibo"], 0);

        var groups = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray().ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal(["Other", "Body UV"], groups.Select(g => g.GetProperty("Name").GetString()));
        Assert.Equal("bibo", groups[1].GetProperty("Options")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void WriteSingleSelectGroup_on_a_v3_folder_replaces_a_same_named_group_at_another_ordinal()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Ven"}""");
        File.WriteAllText(tmp.File("group_001_fabric.json"), """{"Name":"Fabric","Type":"Single","Options":[]}""");
        File.WriteAllText(tmp.File("group_003_body uv.json"),
            """{"Name":"Body UV","Type":"Single","Options":[{"Name":"stale"}]}""");

        // Writing the same group at another ordinal changes its filename. Without deleting the old file the
        // folder would hold TWO groups named "Body UV", and ReadGroupOrder's name→number map would take
        // whichever file the directory enumeration yielded last.
        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 1, "Body UV", ["bibo", "gen3"], 0);

        Assert.False(File.Exists(tmp.File("group_003_body uv.json")));
        Assert.True(File.Exists(tmp.File("group_002_body uv.json")));
        Assert.True(File.Exists(tmp.File("group_001_fabric.json")));   // someone else's group is untouched

        var order = SidecarDiscoveryService.ReadGroupOrder(tmp.Path);
        Assert.Equal(2, order["Body UV"]);
        Assert.Equal(1, order["Fabric"]);
        var g = JsonDocument.Parse(File.ReadAllText(tmp.File("group_002_body uv.json"))).RootElement;
        Assert.Equal(["bibo", "gen3"],
            g.GetProperty("Options").EnumerateArray().Select(o => o.GetProperty("Name").GetString()));
    }

    [Fact]
    public void WriteSingleSelectGroup_on_a_v3_folder_never_collides_with_another_groups_number()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Ven"}""");
        File.WriteAllText(tmp.File("group_001_fabric.json"), """{"Name":"Fabric","Type":"Single","Options":[]}""");
        File.WriteAllText(tmp.File("group_002_trim.json"), """{"Name":"Trim","Type":"Single","Options":[]}""");

        // Ordinal 0 is taken. v3 cannot insert BEFORE another author's group without renumbering their
        // files, so it walks up to the first free number instead — two files numbered 001 would make
        // ReadGroupOrder report both groups at the same ordinal.
        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 0, "Body UV", ["bibo"], 0);

        Assert.True(File.Exists(tmp.File("group_003_body uv.json")));
        var order = SidecarDiscoveryService.ReadGroupOrder(tmp.Path);
        Assert.Equal([1, 2, 3], new[] { order["Fabric"], order["Trim"], order["Body UV"] });
        Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(tmp.File("group_003_body uv.json")))
            .RootElement.GetProperty("Priority").GetInt32());
    }

    [Fact]
    public void WriteSingleSelectGroup_puts_an_out_of_range_ordinal_last_in_both_formats()
    {
        // v3: 99 is free, so it is taken verbatim — and 100 sorts after the existing 001 either way.
        using var v3 = new TempDir();
        File.WriteAllText(v3.File("meta.json"), """{"FileVersion":3,"Name":"Ven"}""");
        File.WriteAllText(v3.File("group_001_fabric.json"), """{"Name":"Fabric","Type":"Single","Options":[]}""");
        PenumbraModMeta.WriteSingleSelectGroup(v3.Path, 99, "Body UV", ["bibo"], 0);

        var v3Order = SidecarDiscoveryService.ReadGroupOrder(v3.Path);
        Assert.True(v3Order["Body UV"] > v3Order["Fabric"]);

        // v4: appended, so also last. The formats need not produce the same NUMBER — only the same order.
        using var v4 = new TempDir();
        File.WriteAllText(v4.File("meta.json"), """
            {"FileVersion":4,"Identifier":"abc-123","Name":"Ven","Groups":[{"Name":"Fabric","Type":"Single"}]}
            """);
        PenumbraModMeta.WriteSingleSelectGroup(v4.Path, 99, "Body UV", ["bibo"], 0);

        var groups = JsonDocument.Parse(File.ReadAllText(v4.File("meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray().ToList();
        Assert.Equal(["Fabric", "Body UV"], groups.Select(g => g.GetProperty("Name").GetString()));
        var v4Order = SidecarDiscoveryService.ReadGroupOrder(v4.Path);
        Assert.True(v4Order["Body UV"] > v4Order["Fabric"]);
    }

    [Fact]
    public void WriteSingleSelectGroup_with_no_options_writes_nothing()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Ven"}""");
        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 0, "Body UV", [], 0);
        Assert.Empty(Directory.EnumerateFiles(tmp.Path, "group_*.json"));
    }

    /// <summary>
    /// The sweep has to cover every AtomicWrite target, not just default_mod.json's — meta.json in the mod
    /// root and metadata.json in the sidecar folder both strand temps when a write is interrupted. It also
    /// has to leave a mod's own .tmp alone, which is why it matches the full &lt;name&gt;.&lt;32 hex&gt;.tmp
    /// shape rather than a bare *.tmp.
    /// </summary>
    [Fact]
    public void CleanLegacyFiles_sweeps_atomic_temps_in_the_root_and_the_sidecar_folder()
    {
        using var tmp = new TempDir();
        var sidecar = Path.Combine(tmp.Path, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(sidecar);
        var guid = Guid.NewGuid().ToString("N");

        File.WriteAllText(tmp.File($"meta.json.{guid}.tmp"), "{}");
        File.WriteAllText(tmp.File($"default_mod.json.{guid}.tmp"), "{}");
        File.WriteAllText(Path.Combine(sidecar, $"metadata.json.{guid}.tmp"), "{}");
        // Not ours: no guid segment, a short one, and a non-hex one of the right length.
        File.WriteAllText(tmp.File("scratch.tmp"), "keep");
        File.WriteAllText(tmp.File("meta.json.abc.tmp"), "keep");
        File.WriteAllText(tmp.File($"meta.json.{new string('z', 32)}.tmp"), "keep");
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Ven"}""");

        PenumbraModMeta.CleanLegacyFiles(tmp.Path);

        Assert.False(File.Exists(tmp.File($"meta.json.{guid}.tmp")));
        Assert.False(File.Exists(tmp.File($"default_mod.json.{guid}.tmp")));
        Assert.False(File.Exists(Path.Combine(sidecar, $"metadata.json.{guid}.tmp")));
        Assert.True(File.Exists(tmp.File("scratch.tmp")));
        Assert.True(File.Exists(tmp.File("meta.json.abc.tmp")));
        Assert.True(File.Exists(tmp.File($"meta.json.{new string('z', 32)}.tmp")));
        Assert.True(File.Exists(tmp.File("meta.json")));
    }

    [Fact]
    public void CleanLegacyFiles_is_quiet_when_there_is_no_sidecar_folder()
    {
        using var tmp = new TempDir();
        PenumbraModMeta.CleanLegacyFiles(tmp.Path);   // must not throw on a mod with no Proteus/ subdir
        Assert.Empty(Directory.EnumerateFiles(tmp.Path));
    }

    // ── PublishesGameContent ────────────────────────────────────────────────
    //
    // A design binding switches unbound overlay mods off in Penumbra. It must not do that to a mod that
    // also ships its own content, or the author's gear goes off with the overlays and stays off across a
    // reboot. These pin which side of that line each folder shape falls on.

    [Fact]
    public void PublishesGameContent_is_false_for_an_overlay_only_v4_pack()
    {
        using var tmp = new TempDir();
        // The self-swap several overlay packs carry so Penumbra doesn't see an empty mod: it redirects
        // nothing, so it must not read as content.
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Name":"Pack",
             "DefaultData":{"Files":{},"FileSwaps":{"chara/a.mtrl":"chara/a.mtrl"},"Manipulations":[]},
             "Groups":[{"Type":"Multi","Name":"Patterns","Options":[{"Name":"Lace"},{"Name":"Dots"}]}]}
            """);

        Assert.False(PenumbraModMeta.PublishesGameContent(tmp.Path));
    }

    [Fact]
    public void PublishesGameContent_sees_files_in_a_v4_group_option()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Name":"Dress",
             "Groups":[{"Type":"Multi","Name":"Items","Options":[
                {"Name":"Dress","Files":{"chara/equipment/e6238/model/c0201e6238_top.mdl":"items/top.mdl"}}]}]}
            """);

        Assert.True(PenumbraModMeta.PublishesGameContent(tmp.Path));
    }

    [Fact]
    public void PublishesGameContent_sees_manipulations_and_real_swaps_and_imc_groups()
    {
        using var manips = new TempDir();
        File.WriteAllText(manips.File("meta.json"),
            """{"FileVersion":4,"DefaultData":{"Manipulations":[{"Type":"Eqdp"}]}}""");
        Assert.True(PenumbraModMeta.PublishesGameContent(manips.Path));

        using var swap = new TempDir();
        File.WriteAllText(swap.File("meta.json"),
            """{"FileVersion":4,"DefaultData":{"FileSwaps":{"chara/a.mtrl":"chara/b.mtrl"}}}""");
        Assert.True(PenumbraModMeta.PublishesGameContent(swap.Path));

        // An IMC group edits the game by existing — its options carry an attribute mask, not files.
        using var imc = new TempDir();
        File.WriteAllText(imc.File("meta.json"),
            """{"FileVersion":4,"Groups":[{"Type":"Imc","Name":"Parts","Options":[{"Name":"A"}]}]}""");
        Assert.True(PenumbraModMeta.PublishesGameContent(imc.Path));
    }

    [Fact]
    public void PublishesGameContent_sees_a_Combining_groups_containers()
    {
        using var tmp = new TempDir();
        // A Combining group's options are bare flag labels; every redirect lives in Containers, one per
        // combination. Reading only Options would call a physics/body pack an empty overlay pack.
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Name":"Physics",
             "Groups":[{"Type":"Combining","Name":"Sizes",
                "Options":[{"Name":"Large"}],
                "Containers":[{},{"Files":{"chara/human/c0201/skeleton/base/b0001/phy_c0201b0001.phyb":"large/phy.phyb"}}]}]}
            """);

        Assert.True(PenumbraModMeta.PublishesGameContent(tmp.Path));
    }

    [Fact]
    public void PublishesGameContent_reads_the_v3_layout_too()
    {
        using var bare = new TempDir();
        File.WriteAllText(bare.File("meta.json"), """{"FileVersion":3,"Name":"Pack"}""");
        File.WriteAllText(bare.File("default_mod.json"), """{"Files":{},"Manipulations":[]}""");
        File.WriteAllText(bare.File("group_001_patterns.json"),
            """{"Name":"Patterns","Type":"Multi","Options":[{"Name":"Lace","Files":{}}]}""");
        Assert.False(PenumbraModMeta.PublishesGameContent(bare.Path));

        // Same folder, one group option that actually redirects something.
        File.WriteAllText(bare.File("group_002_items.json"),
            """{"Name":"Items","Type":"Multi","Options":[{"Name":"Dress","Files":{"chara/a.mdl":"a.mdl"}}]}""");
        Assert.True(PenumbraModMeta.PublishesGameContent(bare.Path));
    }

    [Fact]
    public void PublishesGameContent_says_content_when_the_manifest_cannot_be_read()
    {
        // A false is what justifies disabling the mod, so an unreadable folder must never produce one.
        using var missing = new TempDir();
        Assert.True(PenumbraModMeta.PublishesGameContent(missing.Path));

        using var corrupt = new TempDir();
        File.WriteAllText(corrupt.File("meta.json"), "{ not json");
        Assert.True(PenumbraModMeta.PublishesGameContent(corrupt.Path));
    }
}
