using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

public class DrawnMaterialNamesTests
{
    // Every size but Tre declares an emptied vanilla _a skin mesh beside the real _bibo one; Tre declares
    // only _bibo. Skips when the mod is not installed.
    private const string HeartBreakerSizes = @"E:\Penumbradt\[HS] Heart Breaker (Default)\files\size";

    [Fact]
    public void A_part_is_typed_by_the_skin_it_draws_not_an_emptied_vanilla_binding()
    {
        if (!Directory.Exists(HeartBreakerSizes)) return;
        var models = Directory.GetFiles(HeartBreakerSizes, "*.mdl", SearchOption.AllDirectories);
        Assert.NotEmpty(models);

        bool sawLeftover = false;
        foreach (var path in models)
        {
            var model = File.ReadAllBytes(path);
            var declared = SecondSkinWriter.MaterialNames(model);
            sawLeftover |= declared.Any(n => SecondSkinWriter.SkinMaterialBodyType(n) == "gen2");

            // The gen2 gate drops a vanilla part from the shell; this one is bibo and must survive it.
            Assert.Equal("bibo", SecondSkinService.SkinBodyType(model));
        }
        Assert.True(sawLeftover, "no model declares the vanilla leftover — the test no longer covers the bug");
    }

    [Fact]
    public void Drawn_materials_agree_with_the_geometry_read()
    {
        // DrawnMaterialNames reads only the mesh table; UsedMaterialNames decodes vertices. Same answer.
        if (!Directory.Exists(HeartBreakerSizes)) return;
        foreach (var path in Directory.GetFiles(HeartBreakerSizes, "*.mdl", SearchOption.AllDirectories))
        {
            var model = File.ReadAllBytes(path);
            Assert.Equal(SecondSkinService.UsedMaterialNames(model, SecondSkinWriter.MaterialNames(model)),
                         SecondSkinWriter.DrawnMaterialNames(model));
        }
    }
}
