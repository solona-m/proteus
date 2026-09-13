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

    /// <summary>
    /// Every writer refuses a pre-v4 folder outright, and does so without touching it. Proteus reads that
    /// layout but does not write it; Penumbra migrates such a folder itself the moment it loads it.
    /// </summary>
    [Fact]
    public void EveryWriter_refuses_a_v3_folder_and_changes_nothing()
    {
        using var tmp = new TempDir();
        var meta = """{"FileVersion":3,"Name":"Proteus","Author":"Proteus"}""";
        File.WriteAllText(tmp.File("meta.json"), meta);

        Assert.Throws<PenumbraModMeta.LegacyFolderException>(
            () => PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect));
        Assert.Throws<PenumbraModMeta.LegacyFolderException>(
            () => PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 0, "Fabric", ["velvet"], 0));
        Assert.Throws<PenumbraModMeta.LegacyFolderException>(
            () => PenumbraModMeta.WriteMultiSelectGroup(tmp.Path, 0, "Pieces", ["bow"]));
        Assert.Throws<PenumbraModMeta.LegacyFolderException>(
            () => PenumbraModMeta.DeleteGroup(tmp.Path, "Fabric"));
        Assert.Throws<PenumbraModMeta.LegacyFolderException>(
            () => PenumbraModMeta.MergeImcGroup(
                tmp.Path, new PenumbraModMeta.GroupRef("Straps", 0, default),
                [("Bow", 4)], new HashSet<string>(), null, default));

        // Refused, not half-done: the folder is exactly as it was.
        Assert.Equal(meta, File.ReadAllText(tmp.File("meta.json")));
        Assert.False(File.Exists(tmp.File("default_mod.json")));
        Assert.Empty(Directory.EnumerateFiles(tmp.Path, "group_*.json"));
    }

    /// <summary>
    /// A folder Proteus owns is migrated rather than refused. The managed mod is rewritten on every
    /// composite, so refusing one an older build stamped v3 would take the compositor down with it.
    /// </summary>
    [Fact]
    public void MigrateToCurrent_folds_a_v3_folder_into_the_manifest()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"),
            """{"FileVersion":3,"Name":"Proteus","ModTags":["keep"]}""");
        File.WriteAllText(tmp.File("default_mod.json"),
            """{"Files":{"chara/foo_d.tex":"textures\\foo_d.tex"},"Swaps":{"a":"b"},"Manipulations":[]}""");

        PenumbraModMeta.MigrateToCurrent(tmp.Path);

        var meta = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement;
        Assert.Equal(4, meta.GetProperty("FileVersion").GetInt32());
        var dd = meta.GetProperty("DefaultData");
        Assert.Equal(@"textures\foo_d.tex", dd.GetProperty("Files").GetProperty("chara/foo_d.tex").GetString());
        // Swaps carried across under the name v4 gave them.
        Assert.Equal("b", dd.GetProperty("FileSwaps").GetProperty("a").GetString());
        Assert.Equal("keep", Assert.Single(meta.GetProperty("ModTags").EnumerateArray()).GetString());
        Assert.False(File.Exists(tmp.File("default_mod.json")));

        // And the folder is now writable, which is the whole point.
        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect);
        Assert.False(PenumbraModMeta.IsLegacyFolder(tmp.Path));
    }

    /// <summary>A v4 folder is left alone — the migration is a no-op, not a rewrite.</summary>
    [Fact]
    public void MigrateToCurrent_leaves_a_v4_folder_untouched()
    {
        using var tmp = new TempDir();
        var meta = """{"FileVersion":4,"Identifier":"abc-123","Name":"Proteus"}""";
        File.WriteAllText(tmp.File("meta.json"), meta);

        PenumbraModMeta.MigrateToCurrent(tmp.Path);

        Assert.Equal(meta, File.ReadAllText(tmp.File("meta.json")));
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

    /// <summary>
    /// A folder Proteus is in the middle of creating has no manifest yet, and <c>ReadFileVersion</c> reports
    /// one of those as v3. It must not be mistaken for an old mod and refused its own first write — which is
    /// why the legacy check asks <c>HasReadableManifest</c> first.
    /// </summary>
    [Fact]
    public void WriteRedirects_with_no_manifest_at_all_creates_a_v4_one()
    {
        using var tmp = new TempDir();

        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect);

        var meta = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement;
        Assert.Equal(4, meta.GetProperty("FileVersion").GetInt32());
        Assert.Equal(@"textures\foo_d.tex",
            meta.GetProperty("DefaultData").GetProperty("Files").GetProperty("chara/foo_d.tex").GetString());
        Assert.False(File.Exists(tmp.File("default_mod.json")));
    }

    // A second-skin shell needs its EQDP entry to survive being written out — without it the accessory the
    // shell rides on loads the wrong race/gender model. Pinned because the writer resolves each element's
    // runtime type explicitly, and an object[] that lost its shape on the way through would produce an
    // EQDP row that parses and does nothing.
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
    public void WriteRedirects_keeps_manipulation_contents_on_the_v4_path()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":4,"Name":"Proteus"}""");

        PenumbraModMeta.WriteRedirects(tmp.Path, "Proteus", OneRedirect, manipulations: Eqdp);

        var meta = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement;
        AssertEqdpRoundTripped(meta.GetProperty("DefaultData").GetProperty("Manipulations"));
    }

    /// <summary>
    /// New folders are created at the current version. They used to be stamped v3 so that every Penumbra
    /// could read them, which also made Proteus the biggest writer of the format it now refuses — and left
    /// every folder it created one its own writers would decline until Penumbra migrated it.
    /// </summary>
    [Fact]
    public void NewMetaJson_is_written_at_the_current_version()
    {
        var doc = JsonDocument.Parse(PenumbraModMeta.NewMetaJson("Mod", "Me", "desc")).RootElement;
        Assert.Equal(PenumbraModMeta.SingleFileVersion, doc.GetProperty("FileVersion").GetInt32());
        Assert.Equal("Mod", doc.GetProperty("Name").GetString());
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

    [Fact]
    public void TryReadDefaultData_round_trips_files_and_manipulations()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":4,"Name":"Proteus"}""");
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

        AssertEqdpRoundTripped(JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json"))).RootElement
            .GetProperty("DefaultData").GetProperty("Manipulations"));
    }

    /// <summary>
    /// Reading the pre-v4 layout is still supported even though writing it is not — a <c>.pmp</c> from a
    /// mod site is frequently v3 inside, and a composite has to know what such a folder publishes.
    /// </summary>
    [Fact]
    public void TryReadDefaultData_still_reads_the_v3_layout()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Proteus"}""");
        File.WriteAllText(tmp.File("default_mod.json"),
            """{"Files":{"chara/foo_d.tex":"textures\\foo_d.tex"},"Swaps":{},"Manipulations":[]}""");

        var read = PenumbraModMeta.TryReadDefaultData(tmp.Path);
        Assert.NotNull(read);
        Assert.Equal(@"textures\foo_d.tex", read!.Value.Files["chara/foo_d.tex"]);
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
    public void WriteSingleSelectGroup_writes_non_ascii_names_as_themselves()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":4,"Name":"彩绘比基尼"}""");

        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 0, "Style", ["正常", "光沢"], 0);

        var written = File.ReadAllText(tmp.File("meta.json"));
        Assert.Contains("正常", written);
        Assert.DoesNotContain("\\u", written);

        // The rewrite copies untouched fields through as JsonElements, which are re-escaped by the WRITER's
        // encoder — so the mod's own name is only safe if that writer was configured too.
        Assert.Contains("彩绘比基尼", written);
    }

    // ── Merging into a group the mod's author wrote ──────────────────────────

    private static void WriteImcFixture(TempDir tmp) => File.WriteAllText(tmp.File("meta.json"), """
        {"FileVersion":4,"Identifier":"abc-123","Name":"Frock","Groups":[
          {"Name":"First","Type":"Single"},
          {"Type":"Imc","Name":"Straps","Priority":4,"Page":7,"Unknown":{"deep":[1,2]},
           "Identifier":{"ObjectType":"Equipment","PrimaryId":43,"Variant":1,"EquipSlot":"Body"},
           "DefaultEntry":{"MaterialId":6,"AttributeMask":1023},"DefaultSettings":5,
           "Options":[{"Name":"A","AttributeMask":1},{"Name":"Mine","AttributeMask":2},
                      {"Name":"C","AttributeMask":4}]},
          {"Name":"Last","Type":"Single"}]}
        """);

    /// <summary>
    /// A merged group goes back at its own ordinal. The writer replaces by NAME and splices at an INDEX, so
    /// passing the group's own index is only a no-op on position because the same-named group is dropped
    /// from the array first — worth pinning, since getting it wrong reorders the mod's settings silently.
    /// </summary>
    [Fact]
    public void MergeImcGroup_keeps_the_groups_position_and_every_field_it_does_not_own()
    {
        using var tmp = new TempDir();
        WriteImcFixture(tmp);

        var target = ImcEntrySource.GroupNamed(tmp.Path, "Straps")!.Value;
        Assert.True(PenumbraModMeta.MergeImcGroup(
            tmp.Path, target, [("Bow", 64)], new HashSet<string>(), 959, default));

        var groups = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray().ToList();
        Assert.Equal(["First", "Straps", "Last"], groups.Select(g => g.GetProperty("Name").GetString()));

        var g = groups[1];
        Assert.Equal(4, g.GetProperty("Priority").GetInt32());
        Assert.Equal(7, g.GetProperty("Page").GetInt32());
        // A field this file has never heard of survives whole, nested values and all.
        Assert.Equal(2, g.GetProperty("Unknown").GetProperty("deep")[1].GetInt32());
        // DefaultEntry keeps every field but the mask it was told to change.
        Assert.Equal(6, g.GetProperty("DefaultEntry").GetProperty("MaterialId").GetInt32());
        Assert.Equal(959, g.GetProperty("DefaultEntry").GetProperty("AttributeMask").GetInt32());
        Assert.Equal(["A", "Mine", "C", "Bow"],
            g.GetProperty("Options").EnumerateArray().Select(o => o.GetProperty("Name").GetString()));
    }

    /// <summary>
    /// DefaultSettings is a bitmask over option INDEX, so dropping an option shifts every bit above it down.
    /// The fixture ticks options 0 and 2 (5 = 0b101); removing option 1 must leave them ticked at their new
    /// positions 0 and 1, and anything appended ships ticked.
    /// </summary>
    [Fact]
    public void MergeImcGroup_shifts_default_settings_when_options_are_removed()
    {
        using var tmp = new TempDir();
        WriteImcFixture(tmp);

        var target = ImcEntrySource.GroupNamed(tmp.Path, "Straps")!.Value;
        Assert.True(PenumbraModMeta.MergeImcGroup(
            tmp.Path, target, [("Bow", 64)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mine" }, null, default));

        var g = JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray()
            .Single(x => x.GetProperty("Name").GetString() == "Straps");

        Assert.Equal(["A", "C", "Bow"],
            g.GetProperty("Options").EnumerateArray().Select(o => o.GetProperty("Name").GetString()));
        Assert.Equal(0b111, g.GetProperty("DefaultSettings").GetInt32());
        // Null mask means "leave it alone".
        Assert.Equal(1023, g.GetProperty("DefaultEntry").GetProperty("AttributeMask").GetInt32());
    }

    /// <summary>
    /// Removing every option would leave the author with an empty group, which Penumbra shows as a selector
    /// with nothing in it. Reported rather than written, so the caller can delete it instead.
    /// </summary>
    [Fact]
    public void MergeImcGroup_refuses_to_empty_a_group()
    {
        using var tmp = new TempDir();
        WriteImcFixture(tmp);

        var target = ImcEntrySource.GroupNamed(tmp.Path, "Straps")!.Value;
        var before = File.ReadAllText(tmp.File("meta.json"));

        Assert.False(PenumbraModMeta.MergeImcGroup(
            tmp.Path, target, [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "Mine", "C" }, null, default));

        Assert.Equal(before, File.ReadAllText(tmp.File("meta.json")));
    }

    /// <summary>
    /// Only one IMC group per identifier survives in Penumbra — the highest priority, and among equals the
    /// one later in the array, because it iterates the groups reversed before a stable sort by priority.
    /// Merging into any other would be merging into a group the game never reads.
    /// </summary>
    [Fact]
    public void AppliedGroupFor_picks_the_one_Penumbra_would_keep()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Name":"Frock","Groups":[
              {"Type":"Imc","Name":"Low","Priority":1,
               "Identifier":{"PrimaryId":43,"EquipSlot":"Body"},"Options":[]},
              {"Type":"Imc","Name":"TieFirst","Priority":9,
               "Identifier":{"PrimaryId":43,"EquipSlot":"Body"},"Options":[]},
              {"Type":"Imc","Name":"TieLast","Priority":9,
               "Identifier":{"PrimaryId":43,"EquipSlot":"Body"},"Options":[]},
              {"Type":"Imc","Name":"OtherSlot","Priority":99,
               "Identifier":{"PrimaryId":43,"EquipSlot":"Legs"},"Options":[]}]}
            """);

        Assert.Equal("TieLast", ImcEntrySource.AppliedGroupFor(tmp.Path, 43, "Body", null)!.Value.Name);
        // Excluding one by name is how a second write avoids adopting the group it wrote last time.
        Assert.Equal("TieFirst", ImcEntrySource.AppliedGroupFor(tmp.Path, 43, "Body", "TieLast")!.Value.Name);
        Assert.Null(ImcEntrySource.AppliedGroupFor(tmp.Path, 43, "Feet", null));
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
    public void WriteSingleSelectGroup_writes_the_group_into_the_manifest()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":4,"Name":"Ven"}""");

        PenumbraModMeta.WriteSingleSelectGroup(tmp.Path, 0, "Body UV", ["bibo", "gen3"], 1);

        var g = Assert.Single(JsonDocument.Parse(File.ReadAllText(tmp.File("meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray());
        Assert.Equal("Body UV", g.GetProperty("Name").GetString());
        Assert.Equal("Single", g.GetProperty("Type").GetString());
        Assert.Equal(1, g.GetProperty("DefaultSettings").GetInt32());
        Assert.Equal(["bibo", "gen3"],
            g.GetProperty("Options").EnumerateArray().Select(o => o.GetProperty("Name").GetString()));
        // Options carry no redirects — the group exists so Penumbra shows a selector.
        Assert.Empty(g.GetProperty("Options")[0].GetProperty("Files").EnumerateObject());
        Assert.Empty(Directory.EnumerateFiles(tmp.Path, "group_*.json"));
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
    public void WriteSingleSelectGroup_puts_an_out_of_range_ordinal_last()
    {
        // Appended, so last.
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
    public void ReadAllRedirects_finds_files_in_default_data_and_every_group_option()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Name":"Dress",
             "DefaultData":{"Files":{"chara/equipment/e6238/model/c0201e6238_dwn.mdl":"base/dwn.mdl"}},
             "Groups":[{"Type":"Multi","Name":"Items","Options":[
                {"Name":"Long","Files":{"chara/equipment/e6238/model/c0201e6238_top.mdl":"long/top.mdl"}},
                {"Name":"Short","Files":{"chara/equipment/e6238/model/c0201e6238_top.mdl":"short/top.mdl"}}]}]}
            """);

        var found = PenumbraModMeta.ReadAllRedirects(tmp.Path);

        Assert.Equal(3, found.Count);
        Assert.Contains(found, r => r.File == "base/dwn.mdl" && r.Source == "");
        Assert.Contains(found, r => r.File == "long/top.mdl" && r.Source == "Items / Long");
        // Two options claiming ONE game path is the normal shape of a mod with variants, and an edit that
        // has to reach the geometry has to reach both files — so neither may be deduplicated away.
        Assert.Equal(2, found.Count(r => r.GamePath.EndsWith("_top.mdl", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Declaration order is preserved, and the Toggles tab depends on it: once a model row is labelled by
    /// the option that supplies it, the author's order IS the meaningful one — sizes do not sort
    /// alphabetically into size order.
    /// </summary>
    [Fact]
    public void ReadAllRedirects_keeps_the_manifest_order()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """
            {"FileVersion":4,"Name":"Trousers",
             "Groups":[{"Type":"Single","Name":"Pant Size","Options":[
                {"Name":"Small","Files":{"chara/equipment/e0488/model/c0201e0488_dwn.mdl":"s/dwn.mdl"}},
                {"Name":"Medium","Files":{"chara/equipment/e0488/model/c0201e0488_dwn.mdl":"m/dwn.mdl"}},
                {"Name":"Large","Files":{"chara/equipment/e0488/model/c0201e0488_dwn.mdl":"l/dwn.mdl"}}]}]}
            """);

        Assert.Equal(
            ["Pant Size / Small", "Pant Size / Medium", "Pant Size / Large"],
            PenumbraModMeta.ReadAllRedirects(tmp.Path).Select(r => r.Source));
    }

    [Fact]
    public void ReadAllRedirects_reads_the_v3_layout_and_Combining_containers()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), """{"FileVersion":3,"Name":"Pack"}""");
        File.WriteAllText(tmp.File("default_mod.json"),
            """{"Files":{"chara/equipment/e0043/model/c0201e0043_top.mdl":"base/top.mdl"}}""");
        File.WriteAllText(tmp.File("group_001_sizes.json"), """
            {"Name":"Sizes","Type":"Combining","Options":[{"Name":"Large"}],
             "Containers":[{},{"Files":{"chara/equipment/e0043/model/c0201e0043_dwn.mdl":"large/dwn.mdl"}}]}
            """);

        var found = PenumbraModMeta.ReadAllRedirects(tmp.Path);

        Assert.Contains(found, r => r.File == "base/top.mdl" && r.Source == "");
        Assert.Contains(found, r => r.File == "large/dwn.mdl" && r.Source == "Sizes / #2");
    }

    [Fact]
    public void ReadAllRedirects_on_an_unreadable_mod_is_empty_not_a_throw()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("meta.json"), "{ this is not json");
        Assert.Empty(PenumbraModMeta.ReadAllRedirects(tmp.Path));
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
