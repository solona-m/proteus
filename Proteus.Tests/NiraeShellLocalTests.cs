using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A bodysuit over the real NIRÆ body: its chest model carries every chest, tagged <c>atrx_cb_chest_*</c>, and its Chest
/// option draws one. Measured inside the built shell, against the same build without the options' say, so the cuts the
/// writer makes on its own (connector drops, overlap) are counted the same on both sides.
/// </summary>
public class NiraeShellLocalTests(ITestOutputHelper output)
{
    private const string Nirae = LocalData.Mods + ":NIRÆ body";
    private const string ChestPrefix = "atrx_cb_chest_";

    /// <summary>
    /// Triangles drawn per chest attribute in a built model; with <paramref name="drawnUnder"/>, only those of submeshes
    /// the game would draw with those attributes off (a submesh draws only when every attribute it carries is on —
    /// some of NIRÆ's chest geometry is tagged with two chests).
    /// </summary>
    private static Dictionary<string, int> ChestTriangles(byte[] built, IReadOnlySet<string>? drawnUnder = null)
    {
        var parts = ModelPartReader.Read(built)!;
        uint off = 0;
        for (int bit = 0; bit < 32 && bit < parts.AttributeNames.Count; bit++)
            if (drawnUnder?.Contains(parts.AttributeNames[bit]) == true) off |= 1u << bit;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int bit = 0; bit < 32 && bit < parts.AttributeNames.Count; bit++)
        {
            var name = parts.AttributeNames[bit];
            if (!name.StartsWith(ChestPrefix, StringComparison.Ordinal)) continue;
            counts[name] = parts.Parts.Where(p => (p.AttributeMask & (1u << bit)) != 0 && (p.AttributeMask & off) == 0)
                                      .Sum(p => p.TriangleCount);
        }
        return counts;
    }

    [LocalDataTheory(Nirae)]
    [Trait("Plan", "AC-nirae-shell-chest-1")]
    [InlineData(null, "large", false)]
    [InlineData(null, "large", true)]
    [InlineData("Small Perky", "smallperky", false)]
    [InlineData("Small Perky", "smallperky", true)]
    public void NIRAE_shell_draws_exactly_the_selected_chest(string? chest, string drawn, bool dropConnectors)
    {
        string modsRoot = LocalData.Path(LocalData.Mods), dir = LocalData.Path(Nirae);
        var catalog = BodySizeCatalog.Read(dir);
        var top = catalog.PathOf(catalog.For("_top", "0201").First());
        var selected = chest == null ? new Dictionary<string, List<string>>() : new() { ["Chest"] = [chest] };

        var atr = ShellBodyAtr.ForPart("top", true, top, modsRoot, 201, _ => new ShellBodyAtr.ModSettings(true, selected));
        output.WriteLine($"{Path.GetFileName(top)}: leaving out [{atr.Use?.Key}]");
        Assert.NotNull(atr.Off);

        var bytes = File.ReadAllBytes(top);
        var layer = new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null };
        byte[] Build(IReadOnlySet<string>? hidden)
        {
            var built = SecondSkinWriter.Build(
                [new SecondSkinWriter.SourceSpec(bytes, DropConnectors: dropConnectors, HiddenAttributes: hidden)],
                [layer], null, out _);
            SecondSkinWriterVerbatimTests.Validate(built);
            return built;
        }

        var unhidden = Build(null);
        var baseline = ChestTriangles(unhidden);
        // What the game draws of the selected chest out of that same build.
        var drawnOfBaseline = ChestTriangles(unhidden, atr.Off);
        var hiddenBuild = ChestTriangles(Build(ShellBodyAtr.Merge(null, atr.Off)));
        foreach (var (name, n) in baseline.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            output.WriteLine($"{name}: {n} -> {hiddenBuild.GetValueOrDefault(name)}");

        string want = ChestPrefix + drawn;
        Assert.True(baseline.Count >= 2, "the shell carries one chest at most even without the options — this measures nothing");
        foreach (var (name, n) in baseline.Where(kv => kv.Key != want))
        {
            Assert.True(n > 0, $"{name} had no triangles even in the baseline");
            Assert.Equal(0, hiddenBuild.GetValueOrDefault(name));
        }
        int kept = hiddenBuild.GetValueOrDefault(want), expected = drawnOfBaseline[want];
        output.WriteLine($"{want}: {kept} kept, {expected} of the baseline drawn by the game");
        Assert.True(kept > 0, $"{want} lost its cloth");
        if (dropConnectors)
            Assert.True(kept >= expected, $"{want}: {kept} < {expected}");   // other chests no longer count as cover
        else
            Assert.Equal(expected, kept);
    }
}
