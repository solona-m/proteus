using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A refit's undo and its re-save delete the files an option names under <c>Body Retarget/</c> — and an option's files
/// are the mod author's to write. A name that only STARTS with that folder must never reach anything outside it.
/// </summary>
public partial class BodyRetargetWriterTests
{
    private static string Hostile(HostilePathKit kit, string form) => form switch
    {
        "climb" => kit.ClimbToVictim(BodyRetargetWriter.Subfolder),
        "absolute" => kit.AbsoluteVictim,
        _ => BodyRetargetWriter.Subfolder + "/../meta.json",
    };

    private static HostilePathKit NewRetargetMod(bool authorGroup)
    {
        var kit = new HostilePathKit();
        if (!authorGroup)
            File.WriteAllText(Path.Combine(kit.ModRoot, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Test Outfit", "Someone", ""));
        else
            File.WriteAllText(Path.Combine(kit.ModRoot, PenumbraModMeta.MetaFile), $$"""
            {
              "FileVersion": 4, "Name": "Test Outfit",
              "Groups": [
                { "Version": 0, "Name": "{{TopSize}}", "Priority": 3, "Type": "Single", "DefaultSettings": 0,
                  "Options": [
                    { "Name": "Rue Med", "Files": { "{{GamePath}}": "top size/rue med/top.mdl",
                                                    "chara/x/good.tex": "top size/rue med/good.tex",
                                                    "chara/x/climb.tex": "{{kit.ClimbToVictim().Replace("\\", "/")}}",
                                                    "chara/x/abs.tex": "{{kit.AbsoluteVictim}}" },
                      "FileSwaps": {}, "Manipulations": [] }
                  ] }
              ]
            }
            """);
        return kit;
    }

    /// Adds a file redirect to one option of one group, straight into the manifest — as the mod's author could.
    private static void AddFileToOption(string modRoot, string group, string option, string gamePath, string file)
    {
        var path = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var g = root["Groups"]!.AsArray().Single(x => (string?)x!["Name"] == group)!;
        var o = g["Options"]!.AsArray().Single(x => (string?)x!["Name"] == option)!;
        o["Files"]![gamePath] = file;
        File.WriteAllText(path, root.ToJsonString());
    }

    // [AC-mod-path-containment-5][EDGE-mod-path-containment-16] Sink.
    [Theory]
    [InlineData("climb", false)]
    [InlineData("absolute", false)]
    [InlineData("meta", false)]
    [InlineData("climb", true)]
    [InlineData("absolute", true)]
    [InlineData("meta", true)]
    public void UndoDoesNotDeleteHostileOptionFiles(string form, bool authorGroup)
    {
        using var kit = NewRetargetMod(authorGroup);
        var group = authorGroup ? TopSize : Group;
        Assert.True(BodyRetargetWriter.Save(kit.ModRoot, group, "Neolithe L", GamePath, [1, 2, 3], "Neolithe", "SFW M", "SFW L").Ok);
        var saved = PenumbraModMeta.TryReadFileOptions(kit.ModRoot, group)!.Single(o => o.Name == "Neolithe L").Files[GamePath];
        AddFileToOption(kit.ModRoot, group, "Neolithe L", "chara/x/hostile.tex", Hostile(kit, form));

        var outcome = BodyRetargetWriter.Undo(kit.ModRoot, group, "Neolithe L");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.False(File.Exists(Path.Combine(kit.ModRoot, saved)));         // our own file still goes
        Assert.True(File.Exists(Path.Combine(kit.ModRoot, PenumbraModMeta.MetaFile)));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-4][EDGE-mod-path-containment-16] Sink: saving an option again drops the material copies
    // its earlier save published — named by the option, so by the author too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveDropStaleNeverDeletesHostilePaths(bool auto)
    {
        using var kit = NewRetargetMod(authorGroup: false);
        Assert.True(BodyRetargetWriter.Save(kit.ModRoot, Group, "Neolithe L", GamePath, [1], "Neolithe", "SFW M", "SFW L").Ok);
        AddFileToOption(kit.ModRoot, Group, "Neolithe L", MaterialPath, kit.ClimbToVictim(BodyRetargetWriter.Subfolder));

        var outcome = BodyRetargetWriter.Save(kit.ModRoot, Group, GamePath, "Neolithe", "SFW M",
                                              [new BodyRetargetWriter.Refit("Neolithe L", [2], "SFW L")], auto: auto);

        Assert.True(outcome.Ok, outcome.Message);
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-5][EDGE-mod-path-containment-18] Sink: a refit carries the files of the option it was cut
    // from — but never one naming a place outside the mod.
    [Fact]
    public void CutFromCarryDropsHostileEntries()
    {
        using var kit = NewRetargetMod(authorGroup: true);

        var outcome = BodyRetargetWriter.Save(kit.ModRoot, TopSize, GamePath, "Rue+", "Yiggle - Medium",
                                              [new BodyRetargetWriter.Refit("Rue Yiggle Small", [5], "Yiggle - Small")],
                                              fromOption: "Rue Med");

        Assert.True(outcome.Ok, outcome.Message);
        var files = PenumbraModMeta.TryReadFileOptions(kit.ModRoot, TopSize)!.Single(o => o.Name == "Rue Yiggle Small").Files;
        Assert.Equal("top size/rue med/good.tex", files["chara/x/good.tex"]);
        Assert.False(files.ContainsKey("chara/x/climb.tex"));
        Assert.False(files.ContainsKey("chara/x/abs.tex"));
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-5][EDGE-mod-path-containment-17] Sink: the folder an option's files already sit in is
    // reused — unless that "folder" is a climb.
    [Fact]
    public void HostileExistingFolderIsIgnoredForTheRefitWrite()
    {
        using var kit = NewRetargetMod(authorGroup: false);
        Assert.True(BodyRetargetWriter.Save(kit.ModRoot, Group, "Neolithe L", GamePath, [1], "Neolithe", "SFW M", "SFW L").Ok);
        AddFileToOption(kit.ModRoot, Group, "Neolithe L", GamePath, BodyRetargetWriter.Subfolder + "/../x/top.mdl");
        var outside = Directory.EnumerateFileSystemEntries(kit.Sandbox).Count();

        var outcome = BodyRetargetWriter.Save(kit.ModRoot, Group, GamePath, "Neolithe", "SFW M",
                                              [new BodyRetargetWriter.Refit("Neolithe L", [7, 7], "SFW L")]);

        Assert.True(outcome.Ok, outcome.Message);
        var at = PenumbraModMeta.TryReadFileOptions(kit.ModRoot, Group)!.Single(o => o.Name == "Neolithe L").Files[GamePath];
        Assert.StartsWith(BodyRetargetWriter.Subfolder + "/" + BodyRetargetWriter.Sanitise("Neolithe L") + "/", at.Replace('\\', '/'));
        Assert.Equal([7, 7], File.ReadAllBytes(Path.Combine(kit.ModRoot, at)));
        Assert.False(Directory.Exists(Path.Combine(kit.ModRoot, "chara")));   // nothing landed beside Body Retarget
        Assert.Equal(outside, Directory.EnumerateFileSystemEntries(kit.Sandbox).Count());
        kit.AssertVictimUntouched();
    }

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-3]
    [Theory]
    [InlineData("Body Retarget/../../x")]
    [InlineData("Body Retarget/a/../../../x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("C:x")]
    [InlineData("Body Retarget/C:/x")]
    public void DeletableRejectsUnsafeRels(string rel)
    {
        Assert.False(BodyRetargetWriter.Deletable(rel, new HashSet<string>()));
        Assert.True(BodyRetargetWriter.Deletable("Body Retarget/L/x.mdl", new HashSet<string>()));
    }
}
