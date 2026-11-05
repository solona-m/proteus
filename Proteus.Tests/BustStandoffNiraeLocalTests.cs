using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// #55 on the real NIRÆ chest model (every chest, switched by its Chest option): the standoff reads only the chest drawn,
/// and is measured inside the pass, filtered and unfiltered.
/// </summary>
[Trait("Speed", "Slow")]
public class BustStandoffNiraeLocalTests(ITestOutputHelper o)
{
    private const string Nirae = LocalData.Mods + ":NIRÆ body";

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    public void Real_chest_keeps_one_chest()
    {
        string modsRoot = LocalData.Path(LocalData.Mods), dir = LocalData.Path(Nirae);
        var catalog = BodySizeCatalog.Read(dir);
        var top = catalog.PathOf(catalog.For("_top", "0201").First());
        var selected = new Dictionary<string, List<string>> { ["Chest"] = ["Small Perky"] };
        var atr = ShellBodyAtr.ForPart("top", true, top, modsRoot, 201, _ => new ShellBodyAtr.ModSettings(true, selected));
        Assert.NotNull(atr.Off);
        o.WriteLine($"{Path.GetFileName(top)}: leaving out [{atr.Use?.Key}]");

        var bytes = File.ReadAllBytes(top);
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(bytes, out var pos, out _, out var all, out _, out _));
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(bytes, out _, out _, out var drawn, out _, out _, hiddenAttributes: atr.Off));
        int keptVerts = drawn.Distinct().Count();
        o.WriteLine($"triangles {drawn.Length / 3}/{all.Length / 3}, vertices {keptVerts}/{pos.Length / 3}");
        Assert.True(drawn.Length < all.Length);
        Assert.True(keptVerts < pos.Length / 3 / 4, "one chest of ~10 should keep well under a quarter of the vertices");

        const int size = 1024;
        var covered = Enumerable.Repeat((byte)255, size * size).ToArray();
        long Time(IReadOnlySet<string>? hidden)
        {
            var clock = Stopwatch.StartNew();
            BodyBridge.BustStandoffMap([bytes], covered, size, size, 1f, size, 0.0015f, o.WriteLine, [hidden]);
            return clock.ElapsedMilliseconds;
        }
        long filtered = Time(atr.Off), unfiltered = Time(null);
        o.WriteLine($"BustStandoffMap: filtered {filtered} ms, unfiltered {unfiltered} ms");
        Assert.True(filtered < unfiltered);
    }
}
