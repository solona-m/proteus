using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Reported: the vanilla Oversized Plain Neotunic refitted onto Neolithe (Almond XS) shows a blue patch over its
/// printed front and symmetrical missing polygons on both forearms. Compares the saved refit with the game's own file.
/// </summary>
public class NeotunicRefitDiagTests(ITestOutputHelper output)
{
    private const string GamePath = "chara/equipment/e6234/model/c0201e6234_top.mdl";
    private const string Saved = @"E:\Penumbradt\Oversized Plain Neotunic Neolithe ALL IN ONE\Body Retarget\" +
                                 @"Neolithe [ALL IN ONE] - DEFAULT ALMOND - NSFW Almond XS\chara\equipment\e6234\model\c0201e6234_top.mdl";

    private static byte[]? Vanilla(string path)
    {
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) return null;
        return new Lumina.GameData(sqpack).GetFile(path)?.Data;
    }

    [Fact]
    public void Compare_the_saved_refit_with_the_game_s_file()
    {
        if (!File.Exists(Saved) || Vanilla(GamePath) is not { } before) { output.WriteLine("not available"); return; }
        byte[] after = File.ReadAllBytes(Saved);

        var a = ModelPartReader.Read(before)!;
        var b = ModelPartReader.Read(after)!;
        Show("vanilla", a, before);
        Show("refit", b, after);

        // Cloth meshes matched by material and order.
        float[] ua = BodyRetargetDiagTests.Uv(before), ub = BodyRetargetDiagTests.Uv(after);
        var clothA = a.MeshSpans.Where(s => !IsSkin(a, s.Mesh)).ToList();
        var clothB = b.MeshSpans.Where(s => !IsSkin(b, s.Mesh)).ToList();
        output.WriteLine($"cloth meshes {clothA.Count} -> {clothB.Count}");
        for (int k = 0; k < Math.Min(clothA.Count, clothB.Count); k++)
        {
            var sa = clothA[k];
            var sb = clothB[k];

            // The writer compacts each mesh to the vertices its triangles use, in order: map across that.
            var usedA = a.Parts.Where(p => p.Mesh == sa.Mesh && p.Island < 0).SelectMany(p => p.Triangles)
                         .Distinct().OrderBy(v => v).ToList();
            var map = new Dictionary<int, int>();
            for (int i = 0; i < usedA.Count; i++) map[usedA[i]] = sb.BaseVertex + i;
            output.WriteLine($"  cloth {k}: vertex count {sa.Count} -> {sb.Count}, used as shipped {usedA.Count}");
            if (usedA.Count != sb.Count) continue;

            int uvMoved = 0, far = 0;
            float worstUv = 0f, worstMove = 0f;
            Vector3 lo = new(9f), hi = new(-9f), mv = default;
            foreach (int va in usedA)
            {
                int vb = map[va];
                lo = Vector3.Min(lo, P(a, va));
                hi = Vector3.Max(hi, P(a, va));
                mv += P(b, vb) - P(a, va);
                float du = MathF.Abs(ua[va * 2] - ub[vb * 2]) + MathF.Abs(ua[va * 2 + 1] - ub[vb * 2 + 1]);
                if (du > 1e-4f) uvMoved++;
                worstUv = MathF.Max(worstUv, du);
                float d = Vector3.Distance(P(a, va), P(b, vb));
                worstMove = MathF.Max(worstMove, d);
                if (d > 0.03f) far++;
            }
            mv /= usedA.Count;
            output.WriteLine($"  cloth {k} (mesh {sa.Mesh}->{sb.Mesh}): uv changed {uvMoved}, worst {worstUv:F5}; " +
                             $"moved up to {worstMove * 1000:F1} mm, {far} over 30 mm; mean move " +
                             $"({mv.X * 1000:F1},{mv.Y * 1000:F1},{mv.Z * 1000:F1}) mm; box {lo:F3}..{hi:F3}");
            if (usedA.Count <= 16)
                foreach (int va in usedA)
                    output.WriteLine($"      v{va}: {P(a, va):F4} -> {P(b, map[va]):F4}  uv ({ua[va * 2]:F3},{ua[va * 2 + 1]:F3})");

            // Triangles facing the other way than as shipped.
            var partsA = a.Parts.Where(p => p.Mesh == sa.Mesh && p.Island < 0).ToList();
            var partsB = b.Parts.Where(p => p.Mesh == sb.Mesh && p.Island < 0).ToList();
            for (int q = 0; q < Math.Min(partsA.Count, partsB.Count); q++)
            {
                int[] ta = partsA[q].Triangles, tb = partsB[q].Triangles;
                if (ta.Length != tb.Length) { output.WriteLine($"    {partsA[q].Label}: tri count differs"); continue; }
                int flipped = 0, degenerate = 0, stretched = 0;
                var where = new List<Vector3>();
                for (int t = 0; t + 2 < ta.Length; t += 3)
                {
                    Vector3 na = N(a, ta[t], ta[t + 1], ta[t + 2]), nb = N(b, tb[t], tb[t + 1], tb[t + 2]);
                    float areaA = na.Length(), areaB = nb.Length();
                    if (areaB < 1e-10f && areaA > 1e-9f) degenerate++;
                    else if (Vector3.Dot(na, nb) < 0f) { flipped++; if (where.Count < 12) where.Add(P(b, tb[t])); }
                    if (areaA > 1e-9f && areaB > areaA * 20f) stretched++;
                }
                output.WriteLine($"    {partsA[q].Label} [{partsA[q].Material}]: flipped {flipped}, collapsed {degenerate}, " +
                                 $"stretched x20 {stretched}" +
                                 (where.Count > 0 ? "  at " + string.Join(" ", where.Select(w => $"({w.X:F3},{w.Y:F3},{w.Z:F3})")) : ""));
            }
        }

        string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   @"OneDrive\Desktop\Neotunic refit");
        Directory.CreateDirectory(desk);
        ToeCapDiagTests.WriteObj(before, Path.Combine(desk, "vanilla e6234 top.obj"));
        ToeCapDiagTests.WriteObj(after, Path.Combine(desk, "refit e6234 top (Almond XS).obj"));
        output.WriteLine($"wrote {desk}");
    }

    /// <summary>
    /// The front panel (top_b) against the shirt under it (top_a): signed distance of each surface's vertices from the
    /// other, as shipped and as refitted. A vertex of the shirt that ends up IN FRONT of the panel shows through it.
    /// </summary>
    [Fact]
    public void Does_the_shirt_come_through_the_front_panel()
    {
        if (!File.Exists(Saved) || Vanilla(GamePath) is not { } before) { output.WriteLine("not available"); return; }
        var a = ModelPartReader.Read(before)!;
        var b = ModelPartReader.Read(File.ReadAllBytes(Saved))!;

        foreach (var (label, m) in new[] { ("vanilla", a), ("refit", b) })
        {
            var shirt = Tris(m, "top_a");
            var panel = Tris(m, "top_b");
            foreach (var (what, from, onto) in new[] { ("shirt vs panel", Verts(m, "top_a"), panel),
                                                        ("panel vs shirt", Verts(m, "top_b"), shirt) })
            {
                int near = 0, front = 0, behind = 0;
                var worst = new List<(float D, Vector3 P)>();
                foreach (var p in from)
                {
                    if (!Signed(p, onto, 0.01f, out float d)) continue;
                    near++;
                    if (d > 0.0002f) front++;
                    else if (d < -0.0002f) behind++;
                    worst.Add((d, p));
                }
                output.WriteLine($"{label,-8} {what}: within 10 mm {near}, in front {front}, behind {behind}");
                if (what == "shirt vs panel")
                    foreach (var (d, p) in worst.OrderByDescending(w => w.D).Take(8))
                        output.WriteLine($"      shirt {d * 1000:F2} mm in front at ({p.X:F3},{p.Y:F3},{p.Z:F3})");
            }
        }
    }

    /// <summary>
    /// The refit as the current code makes it, measured on both reports: open skin edges the body itself does not have
    /// (the arm holes), and panel/shirt weight disagreement (the blue patch).
    /// </summary>
    [Fact]
    public void The_refit_as_made_now()
    {
        if (Vanilla(GamePath) is not { } garmentBytes || Vanilla("chara/equipment/e0000/model/c0201e0000_top.mdl") is not { } sourceBytes)
            return;
        var catalog = BodySizeCatalog.Read(@"E:\Penumbradt\Neolithe [ALL IN ONE]");
        var option = catalog.For("_top").First(o => o.FullLabel.Contains("NSFW Almond XS") && o.FullLabel.Contains("DEFAULT ALMOND"));
        byte[] targetBytes = File.ReadAllBytes(catalog.PathOf(option));
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");
        Assert.True(BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sourceBytes), target,
                                                BodyRetargetDiagTests.Uv(targetBytes), "_top", out var built, out _, remap));
        var garment = ModelPartReader.Read(garmentBytes)!;
        var pairs = new List<BodyRetarget.SlotPair> { new("_top", built!, target, targetBytes, sourceBytes) };
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: true, acrossBodies: true);
        output.WriteLine($"swap: {planned.Report.Swap}");

        var refit = ModelPartReader.Read(planned.Model)!;
        var bodyOpen = OpenEdges(target);
        var extra = OpenEdges(refit).Where(e => !bodyOpen.Contains(e)).ToList();
        output.WriteLine($"open skin edges the body does not have: {extra.Count}, on the arms below the sleeve hem " +
                         $"(|x| 0.25..0.45): {extra.Count(e => Math.Abs(e.Item1.Item1) is > 2500 and < 4500)}");
        PanelShirt("fresh refit", planned.Model);
        if (File.Exists(Saved)) PanelShirt("saved in game", File.ReadAllBytes(Saved));

        string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   @"OneDrive\Desktop\Neotunic refit");
        Directory.CreateDirectory(desk);
        ToeCapDiagTests.WriteObj(planned.Model, Path.Combine(desk, "refit e6234 top FIXED (Almond XS).obj"));
    }

    private static HashSet<((int, int, int), (int, int, int))> OpenEdges(ModelParts m)
    {
        var tris = m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                          .SelectMany(p => p.Triangles).ToArray();
        (int, int, int) K(int v) { var p = P(m, v); return ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f)); }
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                var x = K(tris[t + e]);
                var y = K(tris[t + (e + 1) % 3]);
                var key = x.CompareTo(y) < 0 ? (x, y) : (y, x);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        return edges.Where(e => e.Value == 1).Select(e => e.Key).ToHashSet();
    }

    /// <summary>Holes in the swapped skin: position-welded edges used by one triangle, in the refit and in the body.</summary>
    [Fact]
    public void Where_the_swapped_skin_is_open()
    {
        if (!File.Exists(Saved)) return;
        var b = ModelPartReader.Read(File.ReadAllBytes(Saved))!;
        var catalog = BodySizeCatalog.Read(@"E:\Penumbradt\Neolithe [ALL IN ONE]");
        var option = catalog.For("_top").FirstOrDefault(o => o.FullLabel.Contains("NSFW Almond XS") && o.FullLabel.Contains("DEFAULT ALMOND"));
        output.WriteLine($"target: {option?.FullLabel}");
        var models = new List<(string, ModelParts)> { ("refit", b) };
        if (option != null) models.Add(("body", ModelPartReader.Read(File.ReadAllBytes(catalog.PathOf(option)))!));
        if (Vanilla(GamePath) is { } van) models.Add(("vanilla garment skin", ModelPartReader.Read(van)!));
        foreach (var (label, m) in models)
        {
            var tris = m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                              .SelectMany(p => p.Triangles).ToArray();
            (int, int, int) K(int v) { var p = P(m, v); return ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f)); }
            var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
            for (int t = 0; t + 2 < tris.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    var x = K(tris[t + e]);
                    var y = K(tris[t + (e + 1) % 3]);
                    var key = x.CompareTo(y) < 0 ? (x, y) : (y, x);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            var open = edges.Where(e => e.Value == 1).Select(e => e.Key).ToList();
            output.WriteLine($"{label}: {tris.Length / 3} skin tris, {open.Count} open edges");
            // Bucket by height and side, arms only (|x| > 0.12).
            foreach (var g in open.Where(e => e.Item1.Item1 > 1200)
                                  .GroupBy(e => e.Item1.Item1 / 100)
                                  .OrderBy(g => g.Key))
                output.WriteLine($"   arm L x~{g.Key / 100f:F2}: {g.Count()} open edges, y " +
                                 $"{g.Min(e => e.Item1.Item2) / 1e4f:F3}..{g.Max(e => e.Item1.Item2) / 1e4f:F3}, z " +
                                 $"{g.Min(e => e.Item1.Item3) / 1e4f:F3}..{g.Max(e => e.Item1.Item3) / 1e4f:F3}");
        }
    }

    /// <summary>
    /// The refit reproduced, then the cut measured: for every body skin vertex, the distance to the garment's skin as
    /// laid onto the body, and whether that landing is inside a triangle or off its edge.
    /// </summary>
    [Fact]
    public void Why_the_arm_skin_is_cut()
    {
        if (Vanilla(GamePath) is not { } garmentBytes || Vanilla("chara/equipment/e0000/model/c0201e0000_top.mdl") is not { } sourceBytes)
            return;
        var catalog = BodySizeCatalog.Read(@"E:\Penumbradt\Neolithe [ALL IN ONE]");
        var option = catalog.For("_top").First(o => o.FullLabel.Contains("NSFW Almond XS") && o.FullLabel.Contains("DEFAULT ALMOND"));
        byte[] targetBytes = File.ReadAllBytes(catalog.PathOf(option));
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;

        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var remap = new UVRemapService(log, @"E:\repos\Proteus\Proteus");
        Assert.True(BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sourceBytes), target,
                                                BodyRetargetDiagTests.Uv(targetBytes), "_top", out var built,
                                                out string refusal, remap), refusal);
        output.WriteLine($"correspondence: {built!.Describe()}");

        var garment = ModelPartReader.Read(garmentBytes)!;
        var pairs = new List<BodyRetarget.SlotPair> { new("_top", built, target, targetBytes, sourceBytes) };
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: true, acrossBodies: true);
        output.WriteLine($"swap: {planned.Report.Swap}");

        var solved = BodyRetarget.Solve(garment, pairs, "_top", replaceSkin: true);
        var laid = ModelPartReader.Read(MeshVolumeService.Inflate(garmentBytes, solved.Edit).Model)!;
        var drawn = new BodySurface(laid, 0.03f);

        var skinVerts = target.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                              .SelectMany(p => p.Triangles).Distinct().ToList();
        output.WriteLine("arm vertices (x > 0.24), by x band: count, >4mm, of those interior landing; distance p50/p90/max");
        foreach (var band in skinVerts.Select(v => P(target, v)).Where(p => p.X > 0.24f && p.X < 0.46f)
                                      .GroupBy(p => (int)(p.X * 50)).OrderBy(g => g.Key))
        {
            var ds = new List<float>();
            int far = 0, interior = 0;
            foreach (var p in band)
            {
                if (!drawn.Nearest(p, 0.05f, out var hit)) { ds.Add(0.05f); far++; continue; }
                ds.Add(hit.Distance);
                if (hit.Distance <= BodyRetarget.CutReach) continue;
                far++;
                if (hit.U > 1e-4f && hit.V > 1e-4f && hit.W > 1e-4f) interior++;
            }
            ds.Sort();
            output.WriteLine($"   x {band.Key / 50f:F2}: {ds.Count,4}, far {far,3}, interior {interior,3};  " +
                             $"{ds[ds.Count / 2] * 1000:F1} / {ds[ds.Count * 9 / 10] * 1000:F1} / {ds[^1] * 1000:F1} mm");
        }

        // The laid skin's triangle size on the arm, for scale.
        var edges = laid.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                         .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3).Select(t => p.Triangles.Skip(t * 3).Take(3).ToArray()))
                         .Where(t => P(laid, t[0]).X > 0.24f)
                         .SelectMany(t => new[] { Vector3.Distance(P(laid, t[0]), P(laid, t[1])), Vector3.Distance(P(laid, t[1]), P(laid, t[2])) })
                         .OrderBy(d => d).ToList();
        output.WriteLine($"laid garment skin edges on the arm: p50 {edges[edges.Count / 2] * 1000:F1} mm, max {edges[^1] * 1000:F1} mm");
    }

    /// <summary>
    /// Layers a millimetre apart must follow the same bones, or they come apart once posed. For each panel vertex, the
    /// nearest shirt vertex within 5 mm: how different their weights are, as shipped and as saved.
    /// </summary>
    [Fact]
    public void Do_the_panel_and_the_shirt_follow_the_same_bones()
    {
        if (!File.Exists(Saved) || Vanilla(GamePath) is not { } before) return;
        foreach (var (label, bytes) in new[] { ("vanilla", before), ("refit", File.ReadAllBytes(Saved)) })
            PanelShirt(label, bytes);
    }

    private void PanelShirt(string label, byte[] bytes)
    {
        {
            var m = ModelPartReader.Read(bytes)!;
            var skin = ModelSkinReader.Read(bytes, null, null)!;
            var shirt = m.Parts.Where(p => p.Island < 0 && p.Material.Contains("top_a")).SelectMany(p => p.Triangles).Distinct().ToList();
            var panel = m.Parts.Where(p => p.Island < 0 && p.Material.Contains("top_b")).SelectMany(p => p.Triangles).Distinct().ToList();
            var diffs = new List<(float D, int V, int S)>();
            foreach (int v in panel)
            {
                var p = P(m, v);
                int best = -1;
                float bd = 0.005f;
                foreach (int s in shirt)
                {
                    float d = Vector3.Distance(p, P(m, s));
                    if (d < bd) { bd = d; best = s; }
                }
                if (best < 0) continue;
                diffs.Add((L1(W(skin, v), W(skin, best)), v, best));
            }
            diffs.Sort((x, y) => y.D.CompareTo(x.D));
            output.WriteLine($"{label}: {diffs.Count} panel vertices over the shirt; weight L1 mean {diffs.Average(d => d.D):F3}, " +
                             $">0.2: {diffs.Count(d => d.D > 0.2f)}, >0.5: {diffs.Count(d => d.D > 0.5f)}");
            foreach (var (d, v, s) in diffs.Take(6))
                output.WriteLine($"   {d:F2} at {P(m, v):F3}: panel {Fmt(W(skin, v))} | shirt {Fmt(W(skin, s))}");
        }

        static Dictionary<string, float> W(XivLiveMesh.SkinnedMesh skin, int v)
        {
            var w = new Dictionary<string, float>();
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float x = skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                if (x <= 0f) continue;
                string bone = skin.BoneNames[skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]];
                w[bone] = w.GetValueOrDefault(bone) + x;
            }
            return w;
        }
        static float L1(Dictionary<string, float> a, Dictionary<string, float> b)
            => a.Keys.Union(b.Keys).Sum(k => MathF.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k)));
        static string Fmt(Dictionary<string, float> w)
            => string.Join(" ", w.OrderByDescending(x => x.Value).Select(x => $"{x.Key}:{x.Value:F2}"));
    }

    /// <summary>The worst panel/shirt pair, through each step of the weight change.</summary>
    [Fact]
    public void Trace_the_worst_pair_through_the_weight_change()
    {
        if (Vanilla(GamePath) is not { } garmentBytes || Vanilla("chara/equipment/e0000/model/c0201e0000_top.mdl") is not { } sourceBytes)
            return;
        var catalog = BodySizeCatalog.Read(@"E:\Penumbradt\Neolithe [ALL IN ONE]");
        var option = catalog.For("_top").First(o => o.FullLabel.Contains("NSFW Almond XS") && o.FullLabel.Contains("DEFAULT ALMOND"));
        byte[] targetBytes = File.ReadAllBytes(catalog.PathOf(option));
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");
        Assert.True(BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sourceBytes), target,
                                                BodyRetargetDiagTests.Uv(targetBytes), "_top", out var built, out _, remap));
        var garment = ModelPartReader.Read(garmentBytes)!;
        var pairs = new List<BodyRetarget.SlotPair> { new("_top", built!, target, targetBytes, sourceBytes) };
        var solved = BodyRetarget.Solve(garment, pairs, "_top", replaceSkin: true);
        var moved = ModelPartReader.Read(MeshVolumeService.Inflate(garmentBytes, solved.Edit).Model)!;

        var own = ModelSkinReader.Read(garmentBytes, null, null)!;
        var tSkin = ModelSkinReader.Read(targetBytes, null, null)!;
        var sSkin = ModelSkinReader.Read(sourceBytes, null, null)!;
        var tSurf = new BodySurface(target, 0.01f);
        var sSurf = new BodySurface(source, 0.01f);

        foreach (var part in source.Parts.Where(p => p.Island < 0))
            output.WriteLine($"source body part {part.Label} [{part.Material}] {part.Triangles.Length / 3} tris, " +
                             $"skin {SecondSkinWriter.IsBodySkinMaterial(part.Material)}, y {part.Min.Y:F3}..{part.Max.Y:F3}");

        // The vertices nearest the reported spot, one of each layer.
        var at = new Vector3(-0.077f, 1.222f, 0.120f);
        foreach (string layer in new[] { "top_b", "top_a" })
        {
            int v = garment.Parts.Where(p => p.Island < 0 && p.Material.Contains(layer)).SelectMany(p => p.Triangles)
                           .Distinct().OrderBy(i => Vector3.Distance(P(moved, i), at)).First();
            output.WriteLine($"{layer} v{v}: was {P(garment, v):F4} now {P(moved, v):F4}");
            output.WriteLine($"   authored: {Fmt(Inf(own, v))}");
            if (sSurf.Nearest(P(garment, v), 0.04f, out var sh))
                output.WriteLine($"   old body at was ({sh.Distance * 1000:F1} mm, tri {sh.A},{sh.B},{sh.C}): {Fmt(Blend(sSkin, sh))}");
            if (tSurf.Nearest(P(moved, v), 0.04f, out var th))
                output.WriteLine($"   new body at now ({th.Distance * 1000:F1} mm, tri {th.A},{th.B},{th.C}): {Fmt(Blend(tSkin, th))}");
        }

        static List<(string Bone, float W)> Inf(XivLiveMesh.SkinnedMesh skin, int v)
        {
            var list = new List<(string, float)>();
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float w = skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                if (w > 0f) list.Add((skin.BoneNames[skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]], w));
            }
            return list;
        }
        static List<(string Bone, float W)> Blend(XivLiveMesh.SkinnedMesh skin, BodySurface.Hit h)
            => MeshMath.BlendWeights([.. Inf(skin, h.A)], h.U, [.. Inf(skin, h.B)], h.V, [.. Inf(skin, h.C)], h.W, 8).ToList();
        static string Fmt(IEnumerable<(string Bone, float W)> w)
            => string.Join(" ", w.OrderByDescending(x => x.W).Select(x => $"{x.Bone}:{x.W:F2}"));
    }

    /// <summary>The shape keys the game's file carries, and where the vertices they swap in sit.</summary>
    [Fact]
    public void What_shape_the_refit_lost()
    {
        if (Vanilla(GamePath) is not { } bytes) return;
        var src = SecondSkinWriter.Parse(bytes);
        var m = ModelPartReader.Read(bytes)!;
        foreach (var (name, entries) in src.Shapes)
            foreach (var e in entries)
            {
                output.WriteLine($"{name}: mesh index offset {e.MeshIndexOffset}, {e.Values.Length} values, " +
                                 $"replacements {e.Values.Min(v => v.Replace)}..{e.Values.Max(v => v.Replace)}");
            }
        foreach (var span in m.MeshSpans)
            output.WriteLine($"mesh {span.Mesh}: base vertex {span.BaseVertex}, {span.Count} verts");
    }

    private static List<Vector3> Verts(ModelParts m, string material)
        => m.Parts.Where(p => p.Island < 0 && p.Material.Contains(material)).SelectMany(p => p.Triangles).Distinct()
                  .Select(v => P(m, v)).ToList();

    private static List<(Vector3 A, Vector3 B, Vector3 C)> Tris(ModelParts m, string material)
        => m.Parts.Where(p => p.Island < 0 && p.Material.Contains(material))
                  .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                             .Select(t => (P(m, p.Triangles[t * 3]), P(m, p.Triangles[t * 3 + 1]),
                                                           P(m, p.Triangles[t * 3 + 2]))))
                  .ToList();

    private static bool Signed(Vector3 p, List<(Vector3 A, Vector3 B, Vector3 C)> tris, float reach, out float d)
    {
        float best = reach * reach;
        d = 0f;
        bool any = false;
        foreach (var (a, b, c) in tris)
        {
            var q = BrushTransfer.ClosestOnTriangle(p, a, b, c, out _, out _, out _);
            float dd = Vector3.DistanceSquared(p, q);
            if (dd >= best) continue;
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-14f) continue;
            best = dd;
            any = true;
            d = Vector3.Dot(p - q, Vector3.Normalize(n));
        }
        return any;
    }

    private static bool IsSkin(ModelParts m, int mesh)
        => m.Parts.Any(p => p.Mesh == mesh && SecondSkinWriter.IsBodySkinMaterial(p.Material));

    private static Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static Vector3 N(ModelParts m, int i, int j, int k)
        => Vector3.Cross(P(m, j) - P(m, i), P(m, k) - P(m, i));

    private void Show(string label, ModelParts m, byte[] mdl)
    {
        output.WriteLine($"--- {label}: {m.Positions.Length / 3:N0} verts, version 0x{BitConverter.ToUInt32(mdl, 0):x8}");
        foreach (var span in m.MeshSpans)
        {
            var parts = m.Parts.Where(p => p.Mesh == span.Mesh && p.Island < 0).ToList();
            output.WriteLine($"    mesh {span.Mesh} {span.Count,6:N0} verts  " +
                             string.Join(", ", parts.Select(p => $"{p.Label} [{p.Material}] {p.Triangles.Length / 3} tris attr 0x{p.AttributeMask:x}")));
        }
        output.WriteLine($"    attributes: {string.Join(", ", m.AttributeNames)}");
    }
}
