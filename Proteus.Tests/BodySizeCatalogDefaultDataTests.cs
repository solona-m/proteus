using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A body mod does not have to have options. A pack can be one file — the smallclothes model of one slot — and
/// nothing else, which Penumbra stores in the manifest's <c>DefaultData</c> rather than in any group. Reading only
/// the groups, such a mod has no sizes, so <c>IsBody</c> is false and it is filtered out of the Body size tool's
/// list before anything else gets a chance to look at it. Reported as "a mod pack with only one file replacement
/// won't show up in the list of body resize targets".
/// </summary>
public class BodySizeCatalogDefaultDataTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("proteus-body-default").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* a test folder, not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    /// <param name="groups">The <c>Groups</c> array, as JSON text; empty for a mod that has none.</param>
    private void WriteMod(string defaultFiles, string groups = "[]")
    {
        File.WriteAllText(Path.Combine(root, "meta.json"), $$"""
            {
              "FileVersion": 4,
              "Name": "One File Body",
              "Author": "somebody",
              "Version": "1.0",
              "DefaultData": { "Files": {{{defaultFiles}}}, "Manipulations": [] },
              "Groups": {{groups}}
            }
            """);
    }

    private void WriteFile(string rel)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0]);
    }

    [Fact]
    public void A_body_shipped_with_no_options_at_all_is_still_a_body()
    {
        WriteMod("""
            "chara/equipment/e0000/model/c0201e0000_top.mdl": "body/c0201e0000_top.mdl"
            """);
        WriteFile("body/c0201e0000_top.mdl");

        var catalog = BodySizeCatalog.Read(root);

        Assert.True(catalog.IsBody, "a one-file body pack was not recognised as a body");
        var only = Assert.Single(catalog.For("_top", "0201"));
        Assert.Equal("_top", only.Slot);
        Assert.True(File.Exists(catalog.PathOf(only)), "the size resolves to a file that is not there");
    }

    [Fact]
    public void Every_slot_the_pack_replaces_is_offered()
    {
        WriteMod("""
            "chara/equipment/e0000/model/c0201e0000_top.mdl": "body/top.mdl",
            "chara/equipment/e0000/model/c0201e0000_dwn.mdl": "body/dwn.mdl"
            """);
        WriteFile("body/top.mdl");
        WriteFile("body/dwn.mdl");

        var catalog = BodySizeCatalog.Read(root);

        Assert.Equal(["_top", "_dwn"], catalog.Slots);
    }

    [Fact]
    public void Files_that_are_not_the_body_are_left_out()
    {
        // The same test that keeps an outfit with a size group from being taken for a body: only e0000 counts.
        WriteMod("""
            "chara/equipment/e6010/model/c0201e6010_top.mdl": "outfit/top.mdl",
            "chara/equipment/e0000/material/v0001/mt_c0201e0000_top_a.mtrl": "body/top.mtrl"
            """);
        WriteFile("outfit/top.mdl");
        WriteFile("body/top.mtrl");

        Assert.False(BodySizeCatalog.Read(root).IsBody, "an outfit's own model was taken for a body");
    }

    [Fact]
    public void A_mod_with_both_lists_its_plain_files_and_its_options()
    {
        WriteMod("""
            "chara/equipment/e0000/model/c0201e0000_top.mdl": "body/plain.mdl"
            """,
            """
            [
              {
                "Name": "Chest",
                "Type": "Single",
                "Options": [
                  {
                    "Name": "Large",
                    "Files": { "chara/equipment/e0000/model/c0201e0000_top.mdl": "body/large.mdl" }
                  }
                ]
              }
            ]
            """);
        WriteFile("body/plain.mdl");
        WriteFile("body/large.mdl");

        var names = BodySizeCatalog.Read(root).For("_top", "0201").Select(o => o.Name).ToList();

        Assert.Equal([BodySizeCatalog.DefaultSizeName, "Large"], names);
    }
}
