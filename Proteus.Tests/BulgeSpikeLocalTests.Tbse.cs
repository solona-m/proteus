using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Path A (plan featureless-bulge rev11): TBSE Gen A's genital piece and the hole it plugs, for the owner's sculpt.</summary>
public partial class BulgeSpikeLocalTests
{
    /// <summary>
    /// Diagnostic: Gen A's genital piece (<c>0.3</c>) — its open rim, which is the hole in <c>0.0</c> it plugs — written to
    /// the temp folder as JSON (positions of the rim loop in order, and of every <c>0.0</c> vertex), so an exported sculpt
    /// can be compared against it.
    /// </summary>
    [LocalDataFact(GenALegs)]
    public void Gen_a_genital_rim()
    {
        var bytes = File.ReadAllBytes(LocalData.Path(GenALegs));
        var parts = ModelPartReader.Read(bytes)!;
        var penis = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.4");
        var skin0 = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.1");
        // Open edges of 0.3, by welded position.
        string Key(int v) => $"{MathF.Round(At(parts, v).X * 1e5f)},{MathF.Round(At(parts, v).Y * 1e5f)},{MathF.Round(At(parts, v).Z * 1e5f)}";
        var edges = new Dictionary<(string, string), int>();
        for (int t = 0; t + 2 < penis.Triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                string a = Key(penis.Triangles[t + k]), b = Key(penis.Triangles[t + (k + 1) % 3]);
                var e = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        var rimKeys = edges.Where(kv => kv.Value == 1).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).ToHashSet();
        var keyAt = penis.Triangles.Distinct().GroupBy(Key).ToDictionary(g => g.Key, g => At(parts, g.First()));
        var rim = rimKeys.Select(k => keyAt[k]).ToList();
        var skin0Keys = skin0.Triangles.Select(Key).ToHashSet();
        int onSkin = rimKeys.Count(skin0Keys.Contains);
        output.WriteLine($"Gen A 0.3: {penis.Triangles.Distinct().Count()} vertices, {penis.Triangles.Length / 3} triangles; rim {rim.Count} points, "
                         + $"{onSkin} of them on 0.0; open edges {edges.Count(kv => kv.Value == 1)}");
        var all0 = skin0.Triangles.Distinct().Select(v => At(parts, v)).ToList();
        var path = Path.Combine(Path.GetTempPath(), "proteus_genA_rim.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            rim = rim.Select(p => new[] { p.X, p.Y, p.Z }),
            skin0 = all0.Select(p => new[] { p.X, p.Y, p.Z }),
        }));
        output.WriteLine("written " + path);
    }
}
