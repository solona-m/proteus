using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class ModelJoinTests
{
    private static Dictionary<string, HashSet<string>> Map(params (string Resolved, string GamePath)[] entries)
    {
        var m = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (r, g) in entries) m[r] = new HashSet<string>([g], StringComparer.OrdinalIgnoreCase);
        return m;
    }

    [Fact]
    public void PrefixedRendererNames_JoinResolved()
    {
        // [AC-qa-harness-5] The renderer's |prefix| name joins Penumbra's resolved file.
        var models = ModelJoin.Join([(1, @"|abc_123|E:\mods\Outfit\chara\top.mdl")],
                                    Map((@"E:\mods\Outfit\chara\top.mdl", "chara/equipment/e0001/model/c0201e0001_top.mdl")),
                                    @"E:\mods");
        var m = Assert.Single(models);
        Assert.Equal(1, m.Slot);
        Assert.Equal(@"E:\mods\Outfit\chara\top.mdl", m.Loaded);
        Assert.Equal("chara/equipment/e0001/model/c0201e0001_top.mdl", m.GamePath);
        Assert.Equal("Outfit", m.ModDir);
    }

    [Fact]
    public void CaseAndSeparators_Join()
    {
        // [EDGE-qa-harness-25]
        var models = ModelJoin.Join([(0, "|x|e:/MODS/outfit/Chara/TOP.mdl")],
                                    Map((@"E:\mods\Outfit\chara\top.mdl", "chara/a.mdl")), @"E:\mods\");
        Assert.Equal("Outfit", Assert.Single(models).ModDir);
    }

    [Fact]
    public void PenumbraOnly_SlotMinus1()
    {
        // [EDGE-qa-harness-25]
        var models = ModelJoin.Join([], Map((@"E:\mods\A\b.mdl", "chara/b.mdl"), (@"E:\mods\A\t.tex", "chara/t.tex")), @"E:\mods");
        var m = Assert.Single(models);
        Assert.Equal(-1, m.Slot);
        Assert.Null(m.Loaded);
    }

    [Fact]
    public void NoCharacterBase_LoadedNull()
    {
        // [EDGE-qa-harness-25] A model with no handle: listed, nothing loaded.
        var models = ModelJoin.Join([(3, null)], null, null);
        var m = Assert.Single(models);
        Assert.Equal(3, m.Slot);
        Assert.Null(m.Loaded);
        Assert.Null(m.Resolved);
    }

    [Fact]
    public void ModDir_OnlyUnderModsRoot()
    {
        // [EDGE-qa-harness-25]
        Assert.Null(ModelJoin.ModDirOf(@"C:\elsewhere\A\b.mdl", @"E:\mods"));
        Assert.Null(ModelJoin.ModDirOf("chara/equipment/x.mdl", @"E:\mods"));
        Assert.Null(ModelJoin.ModDirOf(@"E:\modsX\A\b.mdl", @"E:\mods"));
        Assert.Equal("A", ModelJoin.ModDirOf(@"E:\mods\A\b.mdl", @"E:\mods"));
        Assert.Null(ModelJoin.ModDirOf(@"E:\mods\A\b.mdl", null));
    }

    [Fact]
    public void GameFileDrawnByGamePath_Joins()
    {
        var models = ModelJoin.Join([(2, "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl")],
                                    Map(("chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl",
                                         "chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl")), @"E:\mods");
        Assert.Single(models);
        Assert.Equal(2, models.Single().Slot);
    }
}
