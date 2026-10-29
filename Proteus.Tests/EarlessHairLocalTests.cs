using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Earless copies of the Miqo'te hair mods installed here, measured inside the pass on the files themselves: no ear fur
/// drawn in any shape, nothing else lost, and the mods' own earless options found.
/// </summary>
public class EarlessHairLocalTests(ITestOutputHelper o)
{
    /// <summary>Every author's Miqo'te hair model of the installed mods, as (mod, file), each file once.</summary>
    private static IEnumerable<(string ModRoot, string Rel)> MiqoteHairs()
    {
        foreach (var root in Directory.EnumerateDirectories(LocalData.Path(LocalData.Mods)))
        {
            List<HairPortPlan.Owner>? owners;
            try { owners = HairPortPlan.ReadOwners(root); }
            catch (Exception) { continue; }
            if (owners == null) continue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in owners)
                foreach (var (path, rel) in owner.Files)
                    if (HairPortPlan.Parse(path) is { Race: 701 or 801 } && !HairPortPlan.IsPortFile(rel)
                        && !EarlessHairPlan.IsEarlessFile(rel) && seen.Add(rel))
                        yield return (root, rel);
        }
    }

    private static int Drawn(byte[] mdl)
        => ModelPartReader.Read(mdl)?.Parts.Where(p => p.Island == -1).Sum(p =>
           {
               int n = 0;
               for (int t = 0; t + 2 < p.Triangles.Length; t += 3)
                   if (p.Triangles[t] != p.Triangles[t + 1] && p.Triangles[t + 1] != p.Triangles[t + 2]
                       && p.Triangles[t] != p.Triangles[t + 2]) n++;
               return n;
           }) ?? 0;

    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "EDGE-earless-miqote-hair-17")]
    [Trait("Plan", "EDGE-earless-miqote-hair-22")]
    [Trait("Speed", "Slow")]
    public void Real_miqote_hairs_come_out_earless_in_every_shape()
    {
        int eared = 0, trimmed = 0, withShapes = 0;
        var refused = new List<string>();
        foreach (var (root, rel) in MiqoteHairs())
        {
            if (HairPortBuilder.ReadMod(root, rel) is not { } src) continue;
            int fur = EarlessHair.DrawnFur(src);
            if (fur == 0) continue;
            eared++;
            var copy = EarlessHair.Make(src, out var why);
            if (copy == null) { refused.Add($"{Path.GetFileName(root)}/{rel}: {why}"); continue; }
            SecondSkinWriter.Parse(copy.Model);
            Assert.Equal(0, EarlessHair.DrawnFur(copy.Model, shapes: true));
            // Only the ears went.
            int lost = Drawn(src) - Drawn(copy.Model);
            Assert.True(lost <= fur, $"{Path.GetFileName(root)}/{rel}: {lost} triangles went, {fur} of them ear");
            if (copy.LodTrimmed) { trimmed++; Assert.Equal(1, ModelLodTrimmer.LodCount(copy.Model)); }
            else if (ModelLodTrimmer.LodCount(src) > 1) Assert.NotEqual("", copy.TrimRefusal);
            if (SecondSkinWriter.Parse(src).Shapes.Count > 0) withShapes++;
        }
        o.WriteLine($"{eared} eared Miqo'te hair models; {trimmed} cut to LOD0; {withShapes} with shape keys; " +
                    $"refused {refused.Count}: {string.Join("; ", refused)}");
        Assert.True(eared > 0, "no installed Miqo'te hair mod with ears");
        Assert.True(refused.Count * 20 <= eared, string.Join("; ", refused));
    }

    [LocalDataTheory(LocalData.Mods)]
    [Trait("Plan", "EDGE-earless-miqote-hair-25")]
    [InlineData("⟡LM_TideRot")]
    [InlineData("[169] Revenge")]
    [InlineData("Lunaria Hair Mashup")]
    public void Real_mods_earless_option_hint(string modName)
    {
        var root = Path.Combine(LocalData.Path(LocalData.Mods), modName);
        Assert.True(Directory.Exists(root), $"{root} is not installed");
        var owners = HairPortPlan.ReadOwners(root)!;
        var hints = new List<string>();
        foreach (var owner in owners)
            foreach (var (path, rel) in owner.Files)
            {
                if (HairPortPlan.Parse(path) is not { Race: 701 or 801 }) continue;
                if (HairPortBuilder.ReadMod(root, rel) is not { } bytes || EarlessHair.DrawnFur(bytes) == 0) continue;
                hints.AddRange(EarlessHairPlan.EarlessOptionHint(owners, path, rel, r => HairPortBuilder.ReadMod(root, r)));
            }
        o.WriteLine($"{modName}: {string.Join(", ", hints.Distinct())}");
        Assert.NotEmpty(hints);
        Assert.Contains(hints, h => h.Contains("earless", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How many edges of drawn triangles the hide leaves open, with the ear skin hidden and with the fur only:
    /// the numbers to look at in game, where a Miqo'te face must cover the ring the skin rule takes away.</summary>
    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "EDGE-earless-miqote-hair-38")]
    [Trait("Speed", "Slow")]
    public void Ear_skin_boundary_edges_report()
    {
        int reported = 0;
        foreach (var (root, rel) in MiqoteHairs())
        {
            if (HairPortBuilder.ReadMod(root, rel) is not { } src || EarlessHair.DrawnFur(src) == 0) continue;
            var withSkin = EarlessHair.Make(src, out _);
            EarlessHair.Result? furOnly;
            HatCompatSolve.WithoutEarSkin = true;
            try { furOnly = EarlessHair.Make(src, out _); }
            finally { HatCompatSolve.WithoutEarSkin = false; }
            if (withSkin == null || furOnly == null) continue;
            int a = OpenEdges(withSkin.Model), b = OpenEdges(furOnly.Model), s = OpenEdges(src);
            // Hiding more can only take triangles away from the fur-only copy, never draw any it does not.
            Assert.True(Drawn(withSkin.Model) <= Drawn(furOnly.Model), $"{Path.GetFileName(root)}/{rel}");
            o.WriteLine($"{Path.GetFileName(root)}/{rel}: open edges {s} as authored, {b} fur only, {a} with ear skin");
            reported++;
        }
        Assert.True(reported > 0);
    }

    /// <summary>Edges of drawn LOD0 triangles that only one of them uses, by welded position.</summary>
    private static int OpenEdges(byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) return 0;
        var p = parts.Positions;
        string Key(int v) => $"{MathF.Round(p[v * 3] * 1e4f)},{MathF.Round(p[v * 3 + 1] * 1e4f)},{MathF.Round(p[v * 3 + 2] * 1e4f)}";
        var edges = new Dictionary<(string, string), int>();
        foreach (var part in parts.Parts.Where(x => x.Island == -1))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int i = part.Triangles[t], j = part.Triangles[t + 1], k = part.Triangles[t + 2];
                if (i == j || j == k || i == k) continue;
                foreach (var (x, y) in new[] { (i, j), (j, k), (k, i) })
                {
                    string kx = Key(x), ky = Key(y);
                    var e = string.CompareOrdinal(kx, ky) < 0 ? (kx, ky) : (ky, kx);
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                }
            }
        return edges.Values.Count(n => n == 1);
    }
}
