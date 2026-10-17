using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>Redirects added to and taken out of somebody else's option, leaving everything else exactly as it was.</summary>
public class PenumbraModMetaOptionRedirectTests
{
    private static Mod Sample() => new("[" + Group("First", "Single", ("A", new() { ["p/a"] = "a.mdl" }), ("B", new())) + ","
                                       + Group("Second", "Multi", ("C", new() { ["p/c"] = "c.mdl" })) + "]",
                                       new Dictionary<string, string> { ["p/d"] = "d.mdl" });

    private static JsonElement Root(Mod mod) => JsonDocument.Parse(mod.Manifest).RootElement.Clone();

    [Fact]
    [Trait("Plan", "AC-hair-race-port-10")]
    public void Merge_preserves_groups_options_order_and_unknown_fields()
    {
        using var mod = Sample();
        var before = Root(mod);
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "Second", "C", new Dictionary<string, string> { ["p/new"] = "n.mdl" });
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "", "", new Dictionary<string, string> { ["p/new2"] = "m.mdl" });
        var after = Root(mod);

        // Every key kept (a group edit writes FileVersion and Identifier first, as every group writer here does).
        Assert.Equal(before.EnumerateObject().Select(p => p.Name).Order(), after.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("id-1", after.GetProperty("Identifier").GetString());
        Assert.Equal(4, after.GetProperty("FileVersion").GetInt32());
        Assert.Equal("Someone", after.GetProperty("Author").GetString());
        Assert.Equal(["First", "Second"], after.GetProperty("Groups").EnumerateArray().Select(g => g.GetProperty("Name").GetString()));
        Assert.Equal("n.mdl", mod.Owner("Second", "C").Files["p/new"]);
        Assert.Equal("c.mdl", mod.Owner("Second", "C").Files["p/c"]);
        Assert.Equal("m.mdl", mod.Owners.First(o => o.IsDefault).Files["p/new2"]);
        Assert.Equal("d.mdl", mod.Owners.First(o => o.IsDefault).Files["p/d"]);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(before.GetProperty("Groups")[0].GetRawText()),
                                                               System.Text.Json.Nodes.JsonNode.Parse(after.GetProperty("Groups")[0].GetRawText())),
                    "the group no redirect was added to changed");
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-10")]
    public void Remove_only_removes_keys_still_pointing_at_our_file()
    {
        using var mod = Sample();
        PenumbraModMeta.MergeOptionRedirects(mod.Root, "First", "A",
            new Dictionary<string, string> { ["p/x"] = "Hair Ports/x.mdl", ["p/y"] = "Hair Ports/y.mdl" });
        // Penumbra rewrote one in its own spelling; the author pointed the other elsewhere.
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"),
            mod.Manifest.Replace("Hair Ports/x.mdl", "Hair Ports\\\\x.mdl").Replace("Hair Ports/y.mdl", "theirs/y.mdl"));

        int removed = PenumbraModMeta.RemoveOptionRedirects(mod.Root, "First", "A",
            new Dictionary<string, string> { ["P/X"] = "Hair Ports/x.mdl", ["p/y"] = "Hair Ports/y.mdl" });
        Assert.Equal(1, removed);
        var files = mod.Owner("First", "A").Files;
        Assert.False(files.ContainsKey("p/x"));
        Assert.Equal("theirs/y.mdl", files["p/y"]);
        Assert.Equal("a.mdl", files["p/a"]);
    }

    [Fact]
    public void Missing_option_combining_group_and_unreadable_manifest_throw()
    {
        using var mod = Sample();
        var add = new Dictionary<string, string> { ["p/z"] = "z.mdl" };
        Assert.ThrowsAny<System.InvalidOperationException>(() => PenumbraModMeta.MergeOptionRedirects(mod.Root, "First", "Nope", add));
        Assert.ThrowsAny<System.InvalidOperationException>(() => PenumbraModMeta.MergeOptionRedirects(mod.Root, "Nope", "A", add));
        File.Delete(Path.Combine(mod.Root, "meta.json"));
        Assert.ThrowsAny<System.InvalidOperationException>(() => PenumbraModMeta.MergeOptionRedirects(mod.Root, "", "", add));
        Assert.False(File.Exists(Path.Combine(mod.Root, "meta.json")));
    }
}
