using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The installed NIRÆ body: four chest models (one per build) under twelve "Build &amp; Hips" options, and a file-less
/// "Chest" group picking which of nine chests each model draws — its chest sizes are build × chest
/// (<see cref="BodySizeCatalog.IsShape"/>), its legs build × hips as before.
/// </summary>
public class NiraeChestSizesLocalTests
{
    private const string Nirae = LocalData.Mods + ":NIRÆ body";
    private static readonly string[] Builds = ["Curvy", "Thick", "Athletic", "Slim"];

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-atr-chest-sizes-1")]
    public void Nirae_chest_lists_forty_sizes()
    {
        var cat = BodySizeCatalog.Read(LocalData.Path(Nirae));
        var top = cat.For("_top", "0201");
        Assert.Equal(40, top.Count);
        Assert.Equal(Builds, top.Select(o => o.Section).Distinct());
        Assert.Contains(top, o => o.Label == "Slim · Small Perky");
        Assert.Contains(top, o => o.Label == "Curvy · Large Round" && o.DefaultShape);
        Assert.DoesNotContain(top, o => o.Label.Contains("hips", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(40, top.Select(o => RefitCore.Normal(o.Rel)).Distinct().Count());
        Assert.All(top, o => Assert.True(File.Exists(cat.PathOf(o))));
    }

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-atr-chest-sizes-3")]
    public void Nirae_chest_models_carry_no_hips_or_belly_tags()
    {
        var cat = BodySizeCatalog.Read(LocalData.Path(Nirae));
        foreach (string path in cat.For("_top", "0201").Select(cat.PathOf).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var model = ModelPartReader.Read(File.ReadAllBytes(path))!;
            Assert.DoesNotContain(model.AttributeNames, a => a.StartsWith("atrx_cb_hips_", StringComparison.Ordinal)
                                                             || a.StartsWith("atrx_cb_belly_", StringComparison.Ordinal));
            Assert.Contains("atrx_cb_chest_smallperky", model.AttributeNames);
        }
    }

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-atr-chest-sizes-7")]
    public void Nirae_legs_list_is_unchanged()
    {
        var legs = BodySizeCatalog.Read(LocalData.Path(Nirae)).For("_dwn", "0201");
        Assert.Equal(12, legs.Count);
        Assert.All(legs, o => Assert.EndsWith("hips", o.Label));
        Assert.All(legs, o => Assert.False(BodySizeCatalog.IsShape(o)));
    }
}
