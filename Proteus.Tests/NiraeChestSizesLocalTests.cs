using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The installed NIRÆ body: four chest models (one per build) under twelve "Build &amp; Hips" options, and a file-less
/// "Chest" group picking which of its chests each model draws — its chest sizes are build × chest
/// (<see cref="BodySizeCatalog.IsShape"/>), its legs build × hips as before.
/// </summary>
public class NiraeChestSizesLocalTests(ITestOutputHelper output)
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

    // ── the Squish group (#59): physics variants of each build's chest, not more builds ──────────────────

    private static readonly (string Variant, string Build, string Section)[] SquishPairs =
    [
        ("c0201e0000_top_crimson_curvy_v69.mdl", "c0201e0000_top_crimson_curvy_v68.mdl", "Curvy"),
        ("c0201e0000_top_crimson_thick_squish_v1.mdl", "c0201e0000_top_crimson_thick_v63.mdl", "Thick"),
        ("c0201e0000_top_crimson_athletic_squish_v1.mdl", "c0201e0000_top_crimson_athletic_v63.mdl", "Athletic"),
        ("c0201e0000_top_crimson_slim_squish_v1.mdl", "c0201e0000_top_crimson_slim_v63.mdl", "Slim"),
    ];

    private static string Leaf(string rel) => Path.GetFileName(BodySizeCatalog.FileOf(rel).Replace('\\', '/')).ToLowerInvariant();

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-59-nirae-squish-1")]
    public void Nirae_squish_models_are_not_sizes()
    {
        var cat = BodySizeCatalog.Read(LocalData.Path(Nirae));
        var top = cat.For("_top", "0201");
        Assert.DoesNotContain(top, o => Leaf(o.Rel).Contains("_squish_") || Leaf(o.Rel).Contains("curvy_v69"));
        Assert.Equal(SquishPairs.Select(p => p.Build).Order(), top.Select(o => Leaf(o.Rel)).Distinct().Order());
        Assert.Equal(Builds, top.Select(o => o.Section).Distinct());
        Assert.Equal(SquishPairs.Select(p => (p.Variant, p.Build)).Order(),
                     cat.Variants.Select(v => (Leaf(v.Rel), Leaf(v.BuildRel))).Order());
    }

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-59-nirae-squish-2")]
    public void Nirae_worn_squish_reads_its_build()
    {
        var cat = BodySizeCatalog.Read(LocalData.Path(Nirae));
        var tick = new Dictionary<string, List<string>> { ["Chest"] = ["Small Perky"] };
        foreach (var (variant, _, section) in SquishPairs)
        {
            var v = cat.Variants.Single(x => Leaf(x.Rel) == variant);
            string file = BodySizeCatalog.ResolveCaseInsensitive(cat.ModRoot, v.Rel);
            var worn = RefitCore.WornOption(cat, "_top", "0201", p => p == v.GamePath ? file : null, tick);
            Assert.Equal($"{section} · Small Perky", worn?.Label);
        }
    }

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-59-nirae-squish-2")]
    [Trait("Plan", "EDGE-59-nirae-squish-14")]
    [Trait("Speed", "Slow")]
    public void Nirae_squish_models_have_their_builds_shape()
    {
        string dir = LocalData.Path(Nirae);
        var cat = BodySizeCatalog.Read(dir);
        var axis = BodyAtrSwitches.ShapeGroup(BodyAtrSwitches.Manifest(dir)!, "Body")!;
        var builds = cat.For("_top", "0201").GroupBy(o => Leaf(o.Rel))
                        .ToDictionary(g => g.Key, g => File.ReadAllBytes(cat.PathOf(g.First())));
        foreach (var v in cat.Variants)
        {
            var variantBytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(dir, v.Rel));
            var mine = ModelPartReader.Read(variantBytes)!;
            double Distance(byte[] other)
            {
                var b = ModelPartReader.Read(other)!;
                if (b.Positions.Length != mine.Positions.Length) return double.PositiveInfinity;
                double worst = 0;
                for (int i = 0; i < b.Positions.Length; i += 3)
                {
                    double dx = b.Positions[i] - mine.Positions[i], dy = b.Positions[i + 1] - mine.Positions[i + 1],
                           dz = b.Positions[i + 2] - mine.Positions[i + 2];
                    worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                }
                return worst;
            }
            var to = builds[Leaf(v.BuildRel)];
            double own = Distance(to);
            output.WriteLine($"{Leaf(v.Rel)} -> {Leaf(v.BuildRel)}: {mine.Positions.Length / 3} vertices, worst {own:E2}");
            Assert.True(own <= 1e-4, $"{Leaf(v.Rel)} is not {Leaf(v.BuildRel)}'s shape (worst {own:E2})");
            foreach (var (leaf, other) in builds.Where(b => b.Key != Leaf(v.BuildRel)))
                Assert.True(Distance(other) > own, $"{Leaf(v.Rel)} is as near {leaf} as its own build");

            // The same body family, not the same build: every NIRÆ chest model carries these names.
            var built = ModelPartReader.Read(to)!;
            Assert.Equal(built.AttributeNames.Order(), mine.AttributeNames.Order());
            Assert.True(mine.AttributeNames.Count(axis.Attributes.Contains) >= 2);
            var extra = SecondSkinWriter.Parse(variantBytes).BoneNames.Except(SecondSkinWriter.Parse(to).BoneNames).ToList();
            output.WriteLine($"  {extra.Count} bone(s) the build lacks: {string.Join(", ", extra.Take(12))}{(extra.Count > 12 ? ", …" : "")}");
        }
    }
}
