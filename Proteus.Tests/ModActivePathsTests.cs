using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// ModActivePaths: which game paths and metadata objects a mod writes for one option selection, read from a
/// hand-written manifest in a temp folder.
/// </summary>
public class ModActivePathsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-activepaths-" + Guid.NewGuid().ToString("N"));

    public ModActivePathsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }

    private string Mod(object manifest)
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(manifest));
        return dir;
    }

    private static Dictionary<string, string> Files(params string[] gamePaths)
    {
        var d = new Dictionary<string, string>();
        foreach (var p in gamePaths) d[p] = "files\\" + Path.GetFileName(p);
        return d;
    }

    private static Dictionary<string, List<string>> Sel(string group, params string[] options)
        => new() { [group] = [.. options] };

    [Fact]
    public void DefaultData_Only()
    {
        var dir = Mod(new
        {
            FileVersion = 4,
            DefaultData = new { Files = Files("chara/equipment/e6255/model/c0201e6255_top.mdl") },
            Groups = Array.Empty<object>(),
        });

        var c = ModActivePaths.Read(dir, null);

        Assert.NotNull(c);
        Assert.Contains("chara/equipment/e6255/model/c0201e6255_top.mdl", c!.GamePaths);
    }

    [Fact]
    public void Single_UsesTickedOption_ElseDefault()
    {
        var dir = Mod(new
        {
            FileVersion = 4,
            DefaultData = new { },
            Groups = new object[]
            {
                new
                {
                    Name = "Colour", Type = "Single", DefaultSettings = 1,
                    Options = new object[]
                    {
                        new { Name = "Red",  Files = Files("a/red.tex") },
                        new { Name = "Blue", Files = Files("a/blue.tex") },
                    },
                },
            },
        });

        var ticked = ModActivePaths.Read(dir, Sel("Colour", "Red"))!;
        Assert.Contains("a/red.tex", ticked.GamePaths);
        Assert.DoesNotContain("a/blue.tex", ticked.GamePaths);

        var untouched = ModActivePaths.Read(dir, null)!;
        Assert.Contains("a/blue.tex", untouched.GamePaths);
        Assert.DoesNotContain("a/red.tex", untouched.GamePaths);
    }

    [Fact]
    public void Multi_TakesEveryTickedOption()
    {
        var dir = Mod(new
        {
            FileVersion = 4,
            DefaultData = new { },
            Groups = new object[]
            {
                new
                {
                    Name = "Parts", Type = "Multi",
                    Options = new object[]
                    {
                        new { Name = "Belt",  Files = Files("p/belt.mdl") },
                        new { Name = "Cape",  Files = Files("p/cape.mdl") },
                        new { Name = "Glove", Files = Files("p/glove.mdl") },
                    },
                },
            },
        });

        var c = ModActivePaths.Read(dir, Sel("Parts", "Belt", "Glove"))!;

        Assert.Contains("p/belt.mdl", c.GamePaths);
        Assert.Contains("p/glove.mdl", c.GamePaths);
        Assert.DoesNotContain("p/cape.mdl", c.GamePaths);
    }

    [Fact]
    public void Combining_IndexesTheContainerByTickedFlags()
    {
        var dir = Mod(new
        {
            FileVersion = 4,
            DefaultData = new { },
            Groups = new object[]
            {
                new
                {
                    Name = "Combo", Type = "Combining",
                    Options = new object[] { new { Name = "A" }, new { Name = "B" } },
                    Containers = new object[]
                    {
                        new { Files = Files("c/none.tex") },
                        new { Files = Files("c/a.tex") },
                        new { Files = Files("c/b.tex") },
                        new { Files = Files("c/ab.tex") },
                    },
                },
            },
        });

        Assert.Contains("c/b.tex", ModActivePaths.Read(dir, Sel("Combo", "B"))!.GamePaths);
        var both = ModActivePaths.Read(dir, Sel("Combo", "A", "B"))!;
        Assert.Contains("c/ab.tex", both.GamePaths);
        Assert.Single(both.GamePaths);
    }

    [Fact]
    public void FileSwaps_Count_ButNotIdentitySwaps()
    {
        var dir = Mod(new
        {
            FileVersion = 4,
            DefaultData = new
            {
                FileSwaps = new Dictionary<string, string>
                {
                    ["chara/equipment/e0001/model/c0101e0001_top.mdl"] = "chara/equipment/e0005/model/c0101e0005_top.mdl",
                    ["chara/common/texture/placeholder.tex"]           = "chara/common/texture/placeholder.tex",
                },
            },
            Groups = Array.Empty<object>(),
        });

        var c = ModActivePaths.Read(dir, null)!;

        Assert.Contains("chara/equipment/e0001/model/c0101e0001_top.mdl", c.GamePaths);
        Assert.DoesNotContain("chara/common/texture/placeholder.tex", c.GamePaths);
    }

    [Fact]
    public void LegacyLayout_ReadsDefaultModAndGroupFiles()
    {
        var dir = Path.Combine(root, "legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new { FileVersion = 3, Name = "Old" }));
        File.WriteAllText(Path.Combine(dir, "default_mod.json"), JsonSerializer.Serialize(new { Files = Files("d/base.tex") }));
        File.WriteAllText(Path.Combine(dir, "group_001_style.json"), JsonSerializer.Serialize(new
        {
            Name = "Style", Type = "Single",
            Options = new object[] { new { Name = "One", Files = Files("d/one.tex") } },
        }));

        var c = ModActivePaths.Read(dir, Sel("Style", "One"))!;

        Assert.Contains("d/base.tex", c.GamePaths);
        Assert.Contains("d/one.tex", c.GamePaths);
    }

    [Fact]
    public void UnreadableManifest_IsNull()
    {
        var dir = Path.Combine(root, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), "{ not json");

        Assert.Null(ModActivePaths.Read(dir, null));
        Assert.Null(ModActivePaths.Read(Path.Combine(root, "missing"), null));
    }

    [Fact]
    public void Manipulations_BecomeTargets_TheCharacterCanReach()
    {
        var dir = Mod(new
        {
            FileVersion = 4,
            DefaultData = new
            {
                Manipulations = new object[]
                {
                    new { Type = "Eqdp", Manipulation = new { SetId = 6255, Slot = "Body", Race = "Midlander", Gender = "Female" } },
                    new { Type = "Eqdp", Manipulation = new { SetId = 53,   Slot = "RFinger" } },
                    new { Type = "Rsp",  Manipulation = new { Attribute = "BustMaxX" } },
                },
            },
            Groups = Array.Empty<object>(),
        });

        var c = ModActivePaths.Read(dir, null)!;

        Assert.Contains(new ModActivePaths.MetaTarget('e', 6255, 201), c.Meta);
        Assert.Contains(new ModActivePaths.MetaTarget('a', 53), c.Meta);
        Assert.Contains(ModActivePaths.MetaTarget.RaceWide, c.Meta);

        var character = ModActivePaths.TargetsOf([
            "chara/equipment/e6255/model/c0201e6255_top.mdl",
            "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl",
        ]);
        Assert.Contains(('e', (ushort)6255), character.Objects);
        Assert.Contains(('b', (ushort)1), character.Objects);
        Assert.Equal([(ushort)201], character.Races);
        Assert.True(character.IsReachedBy(new ModActivePaths.MetaTarget('e', 6255, 201)));
        Assert.False(character.IsReachedBy(new ModActivePaths.MetaTarget('a', 53)));
    }

    [Fact]
    public void RaceLimitedEdits_OnlyReachThatRace()
    {
        var dir = Mod(new
        {
            FileVersion = 4,
            DefaultData = new
            {
                Manipulations = new object[]
                {
                    new { Type = "Rsp",  Manipulation = new { SubRace = "Hellsguard", Attribute = "MaleMaxSize" } },
                    new { Type = "Eqdp", Manipulation = new { SetId = 6255, Slot = "Body", Race = "Roegadyn", Gender = "Male" } },
                    new { Type = "Shp",  Manipulation = new { Shape = "shp_bibo", GenderRaceCondition = 901 } },
                },
            },
            Groups = Array.Empty<object>(),
        });

        var c = ModActivePaths.Read(dir, null)!;
        Assert.All(c.Meta, t => Assert.Equal((ushort)901, t.Race));

        var miqote = ModActivePaths.TargetsOf(["chara/equipment/e6255/model/c0801e6255_top.mdl"]);
        Assert.False(miqote.IsReachedByAny(c.Meta));

        var roe = ModActivePaths.TargetsOf(["chara/equipment/e6255/model/c0901e6255_top.mdl"]);
        Assert.True(roe.IsReachedByAny(c.Meta));
    }

    [Theory]
    [InlineData("Female", "Midlander", 201)]
    [InlineData("Male", "Midlander", 101)]
    [InlineData("Female", "Miqo'te", 801)]
    [InlineData("Male", "Au Ra", 1301)]
    [InlineData("Female", "Viera", 1801)]
    [InlineData("Unknown", "Viera", 0)]
    [InlineData("Female", "Unknown", 0)]
    public void GenderRaceCode_MatchesGamePathCodes(string gender, string race, int expected)
        => Assert.Equal(expected, ModActivePaths.GenderRaceCode(gender, race));

    [Fact]
    public void MetaConflicts_RespectRace()
    {
        var roe  = ModActivePaths.Contribution.Empty();
        var miq  = ModActivePaths.Contribution.Empty();
        var any  = ModActivePaths.Contribution.Empty();
        roe.Meta.Add(new ModActivePaths.MetaTarget('e', 6255, 901));
        miq.Meta.Add(new ModActivePaths.MetaTarget('e', 6255, 801));
        any.Meta.Add(new ModActivePaths.MetaTarget('e', 6255));

        Assert.False(roe.Meets(miq));
        Assert.True(roe.Meets(any));
    }
}
