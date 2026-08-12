using System;
using System.Collections.Generic;
using System.IO;
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

    [Fact]
    public void AtomicWrite_leaves_no_temp_files_behind()
    {
        using var tmp = new TempDir();
        PenumbraModMeta.AtomicWrite(tmp.File("x.json"), "{}");
        Assert.Equal("{}", File.ReadAllText(tmp.File("x.json")));
        Assert.Empty(Directory.EnumerateFiles(tmp.Path, "*.tmp"));
    }
}
