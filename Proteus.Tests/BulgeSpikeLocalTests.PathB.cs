using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;

namespace Proteus.Tests;

/// <summary>Path B (plan featureless-bulge rev11, B0.1–B0.3): lava's bulge onto the bibo-layout bodies with a penis, and
/// what brings its patch folds to zero without flattening it.</summary>
public partial class BulgeSpikeLocalTests
{
    /// <summary>The bulge copy's vertices moved more than <paramref name="threshold"/> off the base, plus two rings.</summary>
    internal static HashSet<int> Core(BulgeAsset asset, float threshold)
    {
        Vector3 Base(int v) => new(asset.Base[v * 3], asset.Base[v * 3 + 1], asset.Base[v * 3 + 2]);
        var tris = asset.Copy.SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                         .Select(t => (p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2]))).ToList();
        var core = asset.Copy.SelectMany(p => p.Triangles).Distinct()
                        .Where(v => Vector3.Distance(At(asset.Lava, v), Base(v)) > threshold).ToHashSet();
        for (int ring = 0; ring < 2; ring++)
            core.UnionWith(tris.Where(t => core.Contains(t.Item1) || core.Contains(t.Item2) || core.Contains(t.Item3))
                               .SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).ToList());
        return core;
    }

    /// <summary>The patch: the bulge copy's triangles with every corner in <paramref name="footprint"/> on a cloth material;
    /// with <paramref name="clothOnly"/>, every other triangle collapsed, so the garment is the patch alone.</summary>
    internal static byte[] PatchOf(BulgeAsset asset, IReadOnlySet<int> footprint, bool clothOnly)
    {
        var keep = new HashSet<ModelMaterialWriter.TriangleKey>();
        foreach (var part in asset.Copy)
            for (int t = 0; t < part.Triangles.Length / 3; t++)
                if (Enumerable.Range(0, 3).All(k => footprint.Contains(part.Triangles[t * 3 + k])))
                    keep.Add(new ModelMaterialWriter.TriangleKey(part.Mesh, part.Submesh, part.Ordinals[t]));
        var patch = ModelMaterialWriter.MoveToMaterial(asset.Bytes, asset.Copy, "/mt_c0201e9999_dwn_a.mtrl", keepTriangle: keep.Contains).Model;
        if (!clothOnly) return patch;
        var parts = ModelPartReader.Read(patch)!;
        var hide = parts.Parts.Where(p => p.Island < 0 && !p.Material.Contains("e9999", StringComparison.Ordinal))
                        .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                   .Select(t => PolygonSelection.Key.Of(p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2])))
                        .ToHashSet();
        ModelPartHider.Hide(patch, parts.MeshSpans, hide);
        return patch;
    }

    /// <summary>lava's inner copy alone, the bulge copy collapsed: the body the patch is made on.</summary>
    internal static byte[] LavaBase(BulgeAsset asset)
    {
        var bytes = (byte[])asset.Bytes.Clone();
        var hide = asset.Copy.SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                        .Select(t => PolygonSelection.Key.Of(p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2])))
                        .ToHashSet();
        ModelPartHider.Hide(bytes, asset.Lava.MeshSpans, hide);
        return bytes;
    }

    /// <summary>One patch refitted onto one body without its genitals: patch folds (in-place solve, cloth only), relief kept.</summary>
    internal (int Folds, int Faces, float Height, float LavaHeight, byte[] Moved, ModelParts PatchParts, BodySurface? Onto, BodySurface LavaSkin) RefitPatch(
        byte[] patch, byte[] source, byte[] bodyBytes, UVRemapService maps)
    {
        var patchParts = ModelPartReader.Read(patch)!;
        var sourceParts = ModelPartReader.Read(source)!;
        var cloth = patchParts.Parts.Where(p => p.Island < 0 && p.Material.Contains("e9999", StringComparison.Ordinal)).ToList();
        var clothVerts = cloth.SelectMany(p => p.Triangles).Distinct().ToArray();
        static float Off(BodySurface s, Vector3 p) => s.Nearest(p, 0.25f, out var hit) ? hit.Distance : 0.25f;
        var lavaSkin = new BodySurface(sourceParts, 0.01f);
        float lavaHeight = clothVerts.Max(v => Off(lavaSkin, At(patchParts, v)));

        var body = ModelPartReader.Read(bodyBytes)!;
        var reading = GenitalParts.Read(body, ModelSkinReader.Read(bodyBytes, null, null)!)!;
        var drop = reading.Genitals.Where(g => g.Rule != "b").Select(g => g.Part).ToHashSet();
        var without = new ModelParts
        {
            Positions = body.Positions, Normals = body.Normals, MeshSpans = body.MeshSpans,
            Parts = body.Parts.Where(p => !drop.Contains(p)).ToList(), AttributeNames = body.AttributeNames,
            Min = body.Min, Max = body.Max, ShatteredSubmeshes = body.ShatteredSubmeshes,
        };
        if (!BodyCorrespondence.TryBuild(sourceParts, BodyRetarget.Uv(source), without, BodyRetarget.Uv(bodyBytes), "_dwn",
                                         out var corr, out string refusal, out _, maps, false, false))
        {
            output.WriteLine("   refused: " + refusal);
            return (-1, 0, 0f, lavaHeight, patch, patchParts, null, lavaSkin);
        }
        var pair = new BodyRetarget.SlotPair("_dwn", corr!, without, bodyBytes, source);
        var planned = BodyRetarget.Plan(patchParts, (byte[])patch.Clone(), [pair], garmentSlot: "_dwn", acrossBodies: true);
        var moved = MeshVolumeService.Inflate((byte[])patch.Clone(), planned.Edit).Model;
        var movedParts = ModelPartReader.Read(moved)!;
        int folds = 0, faces = 0;
        foreach (var part in cloth)
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int x = part.Triangles[t], y = part.Triangles[t + 1], z = part.Triangles[t + 2];
                var n0 = Vector3.Cross(At(patchParts, y) - At(patchParts, x), At(patchParts, z) - At(patchParts, x));
                var n1 = Vector3.Cross(At(movedParts, y) - At(movedParts, x), At(movedParts, z) - At(movedParts, x));
                if (n0.LengthSquared() < 1e-14f) continue;
                faces++;
                if (n1.LengthSquared() < 1e-14f || Vector3.Dot(n0, n1) < 0f) folds++;
            }
        var onto = new BodySurface(without, 0.01f);
        float height = clothVerts.Max(v => Off(onto, At(movedParts, v)));
        return (folds, faces, height, lavaHeight, moved, patchParts, onto, lavaSkin);
    }

    /// <summary>
    /// B0.3: the refitted patch's folds taken out by relaxing its DISPLACEMENT (placed − lava), not its shape: each
    /// vertex within two rings of a folded face takes the mean displacement of its neighbours, the patch's open rim held,
    /// round after round until nothing is folded (or 400 rounds). Points are welded by position. Returns the repaired
    /// positions (indexed like the patch's) and the rounds it took (−1 when it never cleared).
    /// </summary>
    internal static (Vector3[] At, int Rounds, int Left) RelaxFolds(ModelParts patch, ModelParts placed, IReadOnlyList<ModelPart> cloth,
                                                                     int globalRounds = 0)
    {
        int vc = patch.Positions.Length / 3;
        var verts = cloth.SelectMany(p => p.Triangles).Distinct().ToArray();
        var nodeOf = new Dictionary<int, int>();
        var byPlace = new Dictionary<(long, long, long), int>();
        foreach (int v in verts)
        {
            var p = At(patch, v);
            var key = ((long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f));
            if (!byPlace.TryGetValue(key, out int n)) byPlace[key] = n = byPlace.Count;
            nodeOf[v] = n;
        }
        int nc = byPlace.Count;
        var orig = new Vector3[nc];
        var disp = new Vector3[nc];
        foreach (var (v, n) in nodeOf) { orig[n] = At(patch, v); disp[n] = At(placed, v) - At(patch, v); }
        var tris = cloth.SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                   .Select(t => (nodeOf[p.Triangles[t * 3]], nodeOf[p.Triangles[t * 3 + 1]], nodeOf[p.Triangles[t * 3 + 2]])))
                        .Where(t => t.Item1 != t.Item2 && t.Item2 != t.Item3 && t.Item1 != t.Item3).ToList();
        var adj = Enumerable.Range(0, nc).Select(_ => new HashSet<int>()).ToArray();
        var edgeUse = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in tris)
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                adj[x].Add(y); adj[y].Add(x);
                var e = x < y ? (x, y) : (y, x);
                edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
            }
        var rim = edgeUse.Where(kv => kv.Value == 1).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }).ToHashSet();
        bool Folded((int, int, int) t)
        {
            var (a, b, c) = t;
            var n0 = Vector3.Cross(orig[b] - orig[a], orig[c] - orig[a]);
            var pa = orig[a] + disp[a];
            var n1 = Vector3.Cross(orig[b] + disp[b] - pa, orig[c] + disp[c] - pa);
            return n0.LengthSquared() > 1e-14f && (n1.LengthSquared() < 1e-14f || Vector3.Dot(n0, n1) < 0f);
        }
        // Optionally first a whole-patch smoothing of the displacement (rim held): the relief carried more rigidly.
        for (int g = 0; g < globalRounds; g++)
        {
            var next = (Vector3[])disp.Clone();
            for (int n = 0; n < nc; n++)
            {
                if (rim.Contains(n) || adj[n].Count == 0) continue;
                var sum = Vector3.Zero;
                foreach (int m in adj[n]) sum += disp[m];
                next[n] = sum / adj[n].Count;
            }
            disp = next;
        }
        int rounds = 0;
        for (; rounds < 400; rounds++)
        {
            var bad = tris.Where(Folded).ToList();
            if (bad.Count == 0) break;
            var near = bad.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).ToHashSet();
            for (int ring = 0; ring < 2; ring++) near.UnionWith(near.SelectMany(n => adj[n]).ToList());
            var next = (Vector3[])disp.Clone();
            foreach (int n in near)
            {
                if (rim.Contains(n) || adj[n].Count == 0) continue;
                var sum = Vector3.Zero;
                foreach (int m in adj[n]) sum += disp[m];
                next[n] = sum / adj[n].Count;
            }
            disp = next;
        }
        int left = tris.Count(Folded);
        var outAt = new Vector3[vc];
        foreach (var (v, n) in nodeOf) outAt[v] = orig[n] + disp[n];
        return (outAt, left == 0 ? rounds : -1, left);
    }

    /// <summary>B0.1/B0.2: patch folds on the bibo-layout penis bodies, by patch variant.</summary>
    [LocalDataFact(LocalData.UvMaps)]
    public void Path_b_patch_variants()
    {
        var asset = BulgeAsset.Load(null, out var why);
        Assert.True(asset != null, why);
        var maps = new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.UvMaps));
        var lavaBase = LavaBase(asset!);
        var variants = new (string Name, byte[] Patch, byte[] Source)[]
        {
            ("1 mm, with lava's skin", PatchOf(asset!, asset!.Footprint, false), PatchOf(asset, asset.Footprint, false)),
            ("1 mm, cloth only", PatchOf(asset, asset.Footprint, true), lavaBase),
            ("5 mm core, cloth only", PatchOf(asset, Core(asset, 0.005f), true), lavaBase),
            ("10 mm core, cloth only", PatchOf(asset, Core(asset, 0.010f), true), lavaBase),
        };
        foreach (var (name, need, penis, _) in Bodies.Where(b => b.Penis && b.Need.Contains("c0201", StringComparison.Ordinal)))
        {
            if (LocalData.Missing([need]) is { } miss) { output.WriteLine($"== {name}: SKIPPED ({miss})"); continue; }
            var bytes = File.ReadAllBytes(LocalData.Path(need));
            foreach (var (vname, patch, source) in variants)
            {
                var r = RefitPatch(patch, source, bytes, maps);
                output.WriteLine($"{name} / {vname}: patch folds {r.Folds} of {r.Faces}; height {r.Height * 1000f:0.0} mm "
                                 + $"of lava's {r.LavaHeight * 1000f:0.0} ({r.Height / r.LavaHeight * 100f:0}%)");
            }
        }
    }

    /// <summary>
    /// B0.3: the 5 mm core patch, cloth only, refitted and its folds relaxed out by displacement — then the anti-flattening
    /// gates: p95 move from the pre-repair placement ≤ 3 mm·s; per vertex the height over the body against lava's over its
    /// base within ±15% (vertices lava lifts more than 5 mm); no face angle under 5°, no face under 10% of its lava area.
    /// </summary>
    [LocalDataFact(LocalData.UvMaps)]
    public void Path_b_fold_repair()
    {
        var asset = BulgeAsset.Load(null, out var why);
        Assert.True(asset != null, why);
        var maps = new UVRemapService(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.UvMaps));
        var core = Core(asset!, 0.005f);
        var patch = PatchOf(asset!, core, true);
        var source = PatchOf(asset!, core, false);
        static float Off(BodySurface s, Vector3 p) => s.Nearest(p, 0.25f, out var hit) ? hit.Distance : 0.25f;
        foreach (var (name, need, penis, _) in Bodies.Where(b => b.Penis && b.Need.Contains("c0201", StringComparison.Ordinal)))
        {
            if (LocalData.Missing([need]) is { } miss) { output.WriteLine($"== {name}: SKIPPED ({miss})"); continue; }
            var r = RefitPatch(patch, source, File.ReadAllBytes(LocalData.Path(need)), maps);
            if (r.Onto == null) { output.WriteLine($"{name}: refused"); continue; }
            var placed = ModelPartReader.Read(r.Moved)!;
            var cloth = r.PatchParts.Parts.Where(p => p.Island < 0 && p.Material.Contains("e9999", StringComparison.Ordinal)).ToList();
            foreach (int global in new[] { 0, 20, 100, 400 })
            {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (fixedAt, rounds, left) = RelaxFolds(r.PatchParts, placed, cloth, global);
            sw.Stop();
            var verts = cloth.SelectMany(p => p.Triangles).Distinct().ToArray();
            var moves = verts.Select(v => Vector3.Distance(fixedAt[v], At(placed, v))).OrderBy(x => x).ToList();
            float p95 = moves[(int)(moves.Count * 0.95f)];
            var lifted = verts.Where(v => Off(r.LavaSkin, At(r.PatchParts, v)) > 0.005f).ToList();
            int outOfBand = lifted.Count(v =>
            {
                float lavaH = Off(r.LavaSkin, At(r.PatchParts, v)), h = Off(r.Onto, fixedAt[v]);
                return h < 0.85f * lavaH || h > 1.15f * lavaH;
            });
            float minAngle = 180f, minArea = float.MaxValue;
            foreach (var part in cloth)
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    float lavaArea = Vector3.Cross(At(r.PatchParts, b) - At(r.PatchParts, a), At(r.PatchParts, c) - At(r.PatchParts, a)).Length();
                    if (lavaArea < 1e-12f) continue;
                    float area = Vector3.Cross(fixedAt[b] - fixedAt[a], fixedAt[c] - fixedAt[a]).Length();
                    minArea = MathF.Min(minArea, area / lavaArea);
                    for (int k = 0; k < 3; k++)
                    {
                        var p0 = fixedAt[part.Triangles[t + k]];
                        var u = fixedAt[part.Triangles[t + (k + 1) % 3]] - p0;
                        var w = fixedAt[part.Triangles[t + (k + 2) % 3]] - p0;
                        if (u.Length() < 1e-9f || w.Length() < 1e-9f) continue;
                        minAngle = MathF.Min(minAngle, MathF.Acos(Math.Clamp(Vector3.Dot(u, w) / u.Length() / w.Length(), -1f, 1f)) * 180f / MathF.PI);
                    }
                }
            output.WriteLine($"{name} (global {global}): folds {r.Folds} -> {left} after {rounds} rounds ({sw.ElapsedMilliseconds} ms); repair moved p95 {p95 * 1000f:0.0} mm; "
                             + $"height off its band {outOfBand} of {lifted.Count}; smallest angle {minAngle:0.0} deg, smallest area {minArea * 100f:0}% of lava's");
            }
        }
    }
}
