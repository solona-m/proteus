using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The shipped bulge against the body it was sculpted on: lava_bulge is LavaBod+ 2.2's <c>common/64</c> legs with the
/// crotch sculpted into a featureless bulge. Its footprint — the vertices the sculpt moved, plus a margin — is ours to
/// ship as a list of indices; no LavaBod geometry ships.
/// </summary>
[Trait("Speed", "Slow")]
public partial class BulgeAssetLocalTests(ITestOutputHelper output)
{
    internal const string Common64 = LocalData.Mods + ":LavaBod+ 2.2/common/64/c0201e0000_dwn.mdl";

    /// <summary>How far a sculpted vertex must have moved to be in the footprint (1 mm). The plan said 0.1 mm; the bulge
    /// copy also carries a sub-millimetre smoothing over the whole hips (783 vertices, x ±18 cm), which at 0.1 mm would
    /// make the whole pelvis the footprint.</summary>
    private const float Moved = 1e-3f;

    /// <summary>lava_bulge's bulge copy: <c>0.3</c> (the whole skin, sculpted) and <c>0.4</c> (its thighs, untagged).
    /// <c>0.0</c>–<c>0.2</c> are common/64 with only the midline crease closed, drawn inside it.</summary>
    internal static readonly string[] BulgeCopy = ["1.4", "1.5"];

    /// <summary>How near two uvs must be to name the same point of the atlas.</summary>
    private const float SameUv = 1e-4f;

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, rel);
    }

    /// <summary>The skin vertices of a body: each one's position and uv, by the part reader's vertex index.</summary>
    private static (Dictionary<int, (Vector3 P, Vector2 Uv)> Verts, List<int[]> Tris) Skin(byte[] mdl, string[]? labels = null)
    {
        var parts = ModelPartReader.Read(mdl)!;
        var uv = BodyRetarget.Uv(mdl);
        Assert.Equal(parts.Positions.Length / 3 * 2, uv.Length);
        var verts = new Dictionary<int, (Vector3, Vector2)>();
        var tris = new List<int[]>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var part in parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)
                                                     && (labels == null || labels.Contains(p.Label))))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (!seen.Add((a, b, c))) continue;   // the duplicated submesh ranges draw the same triangles twice
                tris.Add([a, b, c]);
                foreach (int v in (int[])[a, b, c])
                    verts.TryAdd(v, (new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]),
                                     new Vector2(uv[v * 2], uv[v * 2 + 1])));
            }
        return (verts, tris);
    }

    /// <summary>Diagnostic: each skin submesh of lava against common/64 — its vertices, how many land on a common/64
    /// vertex of the same uv, and how many of those the sculpt moved.</summary>
    [LocalDataFact(Common64)]
    public void Submesh_census()
    {
        var lavaBytes = File.ReadAllBytes(RepoFile("Proteus/Resources/lava_bulge.mdl"));
        var parts = ModelPartReader.Read(lavaBytes)!;
        var uv = BodyRetarget.Uv(lavaBytes);
        var (body, _) = Skin(File.ReadAllBytes(LocalData.Path(Common64)));
        var byUv = body.GroupBy(kv => (MathF.Round(kv.Value.Uv.X / SameUv), MathF.Round(kv.Value.Uv.Y / SameUv)))
                       .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        foreach (var part in parts.Parts)
        {
            var vs = part.Triangles.Distinct().ToArray();
            int matched = 0, moved = 0;
            float most = 0f;
            foreach (int v in vs)
            {
                var p = new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]);
                var key = (MathF.Round(uv[v * 2] / SameUv), MathF.Round(uv[v * 2 + 1] / SameUv));
                if (!byUv.TryGetValue(key, out var c)) continue;
                matched++;
                float d = c.Min(x => Vector3.Distance(body[x].P, p));
                if (d > Moved) { moved++; most = MathF.Max(most, d); }
            }
            output.WriteLine($"{part.Label} {part.Material} mask {part.AttributeMask:x} island {part.Island}: tris {part.Triangles.Length / 3}, "
                             + $"verts {vs.Length} [{vs.Min()}..{vs.Max()}], uv-matched {matched}, moved {moved} (up to {most * 1000f:0.0} mm)");
            if (moved == 0 || part.Island >= 0) continue;
            // Where the moves are: a histogram of distance, the moved points' extent, and the mean move direction.
            var ds = new List<(Vector3 P, Vector3 D)>();
            foreach (int v in vs)
            {
                var p = new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]);
                var key = (MathF.Round(uv[v * 2] / SameUv), MathF.Round(uv[v * 2 + 1] / SameUv));
                if (!byUv.TryGetValue(key, out var c)) continue;
                int b = c.MinBy(x => Vector3.Distance(body[x].P, p));
                if (Vector3.Distance(body[b].P, p) > Moved) ds.Add((body[b].P, p - body[b].P));
            }
            foreach (var (lo, hi) in new[] { (0.0001f, 0.001f), (0.001f, 0.005f), (0.005f, 0.015f), (0.015f, 0.04f), (0.04f, 1f) })
            {
                var bin = ds.Where(d => d.D.Length() > lo && d.D.Length() <= hi).ToList();
                if (bin.Count == 0) continue;
                var mn = new Vector3(bin.Min(d => d.P.X), bin.Min(d => d.P.Y), bin.Min(d => d.P.Z));
                var mx = new Vector3(bin.Max(d => d.P.X), bin.Max(d => d.P.Y), bin.Max(d => d.P.Z));
                var mean = bin.Aggregate(Vector3.Zero, (s, d) => s + d.D) / bin.Count;
                output.WriteLine($"    {lo * 1000:0.0}-{hi * 1000:0.0} mm: {bin.Count} at x {mn.X:0.000}..{mx.X:0.000} y {mn.Y:0.000}..{mx.Y:0.000} "
                                 + $"z {mn.Z:0.000}..{mx.Z:0.000}, mean move ({mean.X * 1000:0.0}, {mean.Y * 1000:0.0}, {mean.Z * 1000:0.0}) mm");
            }
        }
    }

    [LocalDataFact(Common64)]
    [Trait("Plan", "AC-featureless-bulge-11")]
    public void Generate_footprint()
    {
        var lavaBytes = File.ReadAllBytes(RepoFile("Proteus/Resources/lava_bulge.mdl"));
        var (lava, lavaTris) = Skin(lavaBytes, BulgeCopy);
        var (body, _) = Skin(File.ReadAllBytes(LocalData.Path(Common64)), ["1.1", "1.2"]);
        output.WriteLine($"lava skin: {lava.Count} vertices, {lavaTris.Count} distinct triangles; common/64: {body.Count}");

        // Each lava vertex to the common/64 vertex of the same uv; among several (seams, mirrored atlas), the nearest.
        var byUv = body.GroupBy(kv => (MathF.Round(kv.Value.Uv.X / SameUv), MathF.Round(kv.Value.Uv.Y / SameUv)))
                       .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        // Within each uv, pairs are taken nearest first, each vertex once: a seam's twins share a uv, and the sculpt can
        // carry one of them nearer the other's partner than its own.
        var match = new Dictionary<int, int>();
        var unmatched = new List<int>();
        foreach (var group in lava.GroupBy(kv => (MathF.Round(kv.Value.Uv.X / SameUv), MathF.Round(kv.Value.Uv.Y / SameUv))))
        {
            if (!byUv.TryGetValue(group.Key, out var cands)) { unmatched.AddRange(group.Select(kv => kv.Key)); continue; }
            var pairs = group.SelectMany(kv => cands.Select(c => (L: kv.Key, B: c, D: Vector3.DistanceSquared(body[c].P, kv.Value.P))))
                             .OrderBy(x => x.D).ThenBy(x => x.L).ThenBy(x => x.B);
            var usedB = new HashSet<int>();
            foreach (var (l, b, _) in pairs)
                if (!match.ContainsKey(l) && usedB.Add(b)) match[l] = b;
            unmatched.AddRange(group.Select(kv => kv.Key).Where(l => !match.ContainsKey(l)));
        }
        int taken = match.Values.Distinct().Count();
        output.WriteLine($"matched {match.Count}, unmatched {unmatched.Count}, distinct targets {taken}");
        foreach (int v in unmatched)
            output.WriteLine($"  unmatched lava {v} at {lava[v].P} uv {lava[v].Uv}");
        // Every common/64 vertex has exactly one lava partner. The sculpt may also have ADDED a few vertices (3 on the
        // crotch midline): those have no partner, and must lie in the sculpt — they join the footprint below.
        Assert.True(taken == match.Count && match.Count == body.Count,
                    $"the match is not one-to-one: {match.Count} of {lava.Count} lava vertices onto {taken} distinct of {body.Count}");
        Assert.True(unmatched.Count <= 5, $"{unmatched.Count} lava vertices the sculpt added — more than a sculpt adds");

        // The sculpt: every vertex moved more than Moved, then two rings of triangle neighbours, welded by position so
        // the margin crosses uv seams.
        var moved = match.Where(kv => Vector3.Distance(lava[kv.Key].P, body[kv.Value].P) > Moved).Select(kv => kv.Key).ToHashSet();
        foreach (int v in unmatched)
            Assert.True(moved.Any(m => Vector3.Distance(lava[m].P, lava[v].P) < 0.01f),
                        $"lava vertex {v}, which the sculpt added, lies outside the sculpt");
        moved.UnionWith(unmatched);
        var ext = (Min: new Vector3(moved.Min(v => lava[v].P.X), moved.Min(v => lava[v].P.Y), moved.Min(v => lava[v].P.Z)),
                   Max: new Vector3(moved.Max(v => lava[v].P.X), moved.Max(v => lava[v].P.Y), moved.Max(v => lava[v].P.Z)));
        output.WriteLine($"moved extent: {ext.Min} .. {ext.Max}");
        float most = moved.Where(match.ContainsKey).Max(v => Vector3.Distance(lava[v].P, body[match[v]].P));
        output.WriteLine($"moved: {moved.Count} vertices, up to {most * 1000f:0.0} mm");
        Assert.NotEmpty(moved);

        var weld = lava.Keys.GroupBy(v => (MathF.Round(lava[v].P.X * 1e5f), MathF.Round(lava[v].P.Y * 1e5f), MathF.Round(lava[v].P.Z * 1e5f)))
                       .SelectMany(g => g.Select(v => (v, g.Key))).ToDictionary(x => x.v, x => x.Key);
        var adj = new Dictionary<(float, float, float), HashSet<(float, float, float)>>();
        foreach (var t in lavaTris)
            for (int k = 0; k < 3; k++)
            {
                var a = weld[t[k]];
                if (!adj.TryGetValue(a, out var set)) adj[a] = set = [];
                set.Add(weld[t[(k + 1) % 3]]);
                set.Add(weld[t[(k + 2) % 3]]);
            }
        var nodes = moved.Select(v => weld[v]).ToHashSet();
        const int margin = 2;
        for (int ring = 0; ring < margin; ring++)
            nodes.UnionWith(nodes.SelectMany(n => adj.TryGetValue(n, out var s) ? s : []).ToList());
        var footprint = lava.Keys.Where(v => nodes.Contains(weld[v])).OrderBy(v => v).ToArray();
        output.WriteLine($"footprint: {footprint.Length} vertices with a {margin}-ring margin");

        var json = JsonSerializer.Serialize(new Footprint(Convert.ToHexString(SHA256.HashData(lavaBytes)).ToLowerInvariant(),
                                                          footprint, margin));
        var path = RepoFile("Proteus/Resources/lava_bulge.footprint.json");
        if (File.Exists(path))
            Assert.True(File.ReadAllText(path).Trim() == json,
                        "the committed footprint differs from what lava_bulge vs common/64 gives — regenerate it on purpose");
        else
            File.WriteAllText(path, json + "\n");
    }

    /// <summary>The footprint file's shape: the asset it was made from, its vertices, and the margin they include.</summary>
    internal sealed record Footprint(string asset, int[] vertices, int margin);

    /// <summary>The embedded file is the user's own copy, byte for byte.</summary>
    [LocalDataFact(LocalData.BulgeReference)]
    [Trait("Plan", "AC-featureless-bulge-11")]
    public void Embedded_asset_is_the_users_copy()
        => Assert.Equal(File.ReadAllBytes(LocalData.Path(LocalData.BulgeReference)),
                        File.ReadAllBytes(RepoFile("Proteus/Resources/lava_bulge.mdl")));
}
