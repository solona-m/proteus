using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Dalamud.Plugin.Services;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The featureless bulge's spike (plan featureless-bulge, Phase 0): measurements on real bodies, kept as the regression
/// log of what the rules were fixed from. S0.1 is the genital inventory.
/// </summary>
[Trait("Speed", "Slow")]
public class BulgeSpikeLocalTests(ITestOutputHelper output)
{
    private const string M = LocalData.Mods + ":";
    private const string TbseOpt = M + "The Body SE/p2/step 6- smallclothes options/";
    private const string TbseXOpt = M + "The Body SE-X IVCS2 V1_1_0/p1/legs (smallclothes legs)/";
    private const string E0 = "/chara/equipment/e0000/model/";

    /// <summary>The bodies S0.1 reads: name, need, and whether a penis is drawn there (the inventory's ground truth).</summary>
    internal static readonly (string Name, string Need, bool Penis, string Genitals)[] Bodies =
    [
        ("TBSE Gen A", TbseOpt + "gen a" + E0 + "c0101e0000_dwn.mdl", true, "1.4c"),
        ("TBSE Gen D", TbseOpt + "gen d" + E0 + "c0101e0000_dwn.mdl", true, "1.4c"),
        ("TBSE SFW", TbseOpt + "sfw" + E0 + "c0101e0000_dwn.mdl", false, ""),
        ("TBSE-X Flaccid M A", TbseXOpt + "flaccid m a" + E0 + "c0101e0000_dwn.mdl", true, "1.4c 2.1a 2.2a"),
        ("TBSE-X Erect L cut", TbseXOpt + "erect l cut" + E0 + "c0101e0000_dwn.mdl", true, "1.1b 2.1a 2.2a"),
        ("TBSE-X SFW", TbseXOpt + "sfw" + E0 + "c0101e0000_dwn.mdl", false, ""),
        ("Bibo+ common/75", M + "[IVCS] Bibo+/common/75/c0201e0000_dwn.mdl", true, "2.1a"),
        ("Rosaline common/17", M + "hs-Rosaline-1.1.2-hM5N/files/common/17/c0201e0000_dwn.mdl", true, "3.1a"),
        ("Muse+ defined flaccid", M + "hs-Muse+-3.2.2-LbM/files/smallclothes legs/defined - flaccid" + E0 + "c0201e0000_dwn.mdl", true, "1.4c 1.5c 1.6c 1.7c"),
        ("Muse+ defined sfw bulge", M + "hs-Muse+-3.2.2-LbM/files/smallclothes legs/defined - sfw bulge" + E0 + "c0201e0000_dwn.mdl", false, ""),
        ("Hotdog watermelon cut", M + "[YAS] Nyl's Hotdog for YAB and Rue - Full Package/flaccid no addons - smallclothes legs/watermelon - cut" + E0 + "c0201e0000_dwn.mdl", true, "2.1a 3.1a"),
        ("Tre medium trans flaccid", M + "hs-[YAS] Tre-5.5.3-8xB/files/p4/tre legs (smallclothes)/medium - trans flaccid" + E0 + "c0201e0000_dwn.mdl", true, "3.1a 3.2a 3.3a 3.4a"),
        ("LavaBod+ common/64", BulgeAssetLocalTests.Common64, false, ""),
        ("Bibo+ Type A M", M + "Bibo+/bottoms - small clothes/type a - medium" + E0 + "c0201e0000_dwn.mdl", false, ""),
    ];

    /// <summary>The crotch window about an anchor (the plan's): |x−MidX| ≤ 8 cm·s, y −8…+16 cm·s, in front of the
    /// anchor's depth − 4 cm·s.</summary>
    private static bool InWindow(Vector3 p, BodyRetarget.CrotchAnchor a)
        => MathF.Abs(p.X - a.MidX) <= 0.08f * a.Scale && p.Y >= a.Y - 0.08f * a.Scale && p.Y <= a.Y + 0.16f * a.Scale
           && p.Z >= a.At.Z - 0.04f * a.Scale;

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>
    /// A crotch for a body whose crotch skin is a socket (Bibo+ common/75 under its <c>_benis</c>, briefs over a hole):
    /// no skin vertex carries both thighs, so <see cref="BodyRetarget.FindCrotch"/> finds none. The nearest pair of a
    /// left-thigh and a right-thigh skin vertex stands in, its midpoint the crotch.
    /// </summary>
    internal static BodyRetarget.CrotchAnchor? SocketCrotch(ModelParts body, XivLiveMesh.SkinnedMesh skin)
    {
        var left = new List<Vector3>();
        var right = new List<Vector3>();
        foreach (var part in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            foreach (int v in part.Triangles.Distinct())
            {
                float wl = 0f, wr = 0f;
                for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                {
                    float w = skin.BoneWeights[v * 8 + k];
                    if (w <= 0f) continue;
                    string b = skin.BoneNames[skin.BoneIndices[v * 8 + k]];
                    if (!BodyRetarget.IsThighBone(b)) continue;
                    if (b.EndsWith("_l", StringComparison.Ordinal)) wl += w; else wr += w;
                }
                var p = At(body, v);
                if (wl >= BodyRetarget.CrotchAnchorThigh && p.X > 0f) left.Add(p);
                if (wr >= BodyRetarget.CrotchAnchorThigh && p.X < 0f) right.Add(p);
            }
        if (left.Count == 0 || right.Count == 0) return null;
        // The pair nearest across the midline, among the top of the thighs (the highest fifth of each side).
        float topL = left.Max(p => p.Y), topR = right.Max(p => p.Y);
        var l = left.Where(p => p.Y > topL - 0.12f).ToList();
        var r = right.Where(p => p.Y > topR - 0.12f).ToList();
        (Vector3 A, Vector3 B, float D) best = (default, default, float.MaxValue);
        foreach (var a in l)
            foreach (var b in r)
            {
                float d = Vector3.DistanceSquared(a, b);
                if (d < best.D) best = (a, b, d);
            }
        var at = (best.A + best.B) / 2;
        float scale = Math.Clamp(at.Y / BodyRetarget.CrotchReferenceHeight, 0.4f, 1.5f);
        return new BodyRetarget.CrotchAnchor(at.Y, at.X, scale, at);
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-2")]
    [Trait("Plan", "EDGE-featureless-bulge-5")]
    public void Genital_inventory()
    {
        int read = 0;
        var wrong = new List<string>();
        foreach (var (name, need, penis, expected) in Bodies)
        {
            if (LocalData.Missing([need]) is { } why) { output.WriteLine($"== {name}: SKIPPED ({why})"); continue; }
            read++;
            var bytes = File.ReadAllBytes(LocalData.Path(need));
            var parts = ModelPartReader.Read(bytes)!;
            var skin = ModelSkinReader.Read(bytes, null, null)!;
            var reading = Proteus.Services.Bulge.GenitalParts.Read(parts, skin);
            string got = reading == null ? "" : string.Join(" ", reading.Genitals.OrderBy(g => g.Part.Label, StringComparer.Ordinal)
                                                                         .Select(g => g.Part.Label + g.Rule));
            output.WriteLine($"   reading: penis {reading?.Penis} genitals [{got}] "
                             + string.Join(" ", reading?.Genitals.Select(g => $"{g.Part.Label}:{g.Protrusion * 1000f:0}mm/{g.Triangles.Count}t") ?? []));
            if ((reading?.Penis ?? false) != penis || got != expected) wrong.Add($"{name}: penis {reading?.Penis} [{got}], expected {penis} [{expected}]");
            var anchor = BodyRetarget.FindCrotch([(parts, skin)]);
            string how = "FindCrotch";
            if (anchor == null) { anchor = SocketCrotch(parts, skin); how = "socket"; }
            output.WriteLine($"== {name} (penis: {penis}): {parts.Parts.Count(p => p.Island < 0)} parts, anchor ({how}) "
                             + (anchor is { } a0 ? $"y {a0.Y:0.000} midX {a0.MidX:0.000} z {a0.At.Z:0.000} s {a0.Scale:0.00}" : "NONE"));
            if (anchor is not { } a)
            {
                // Why FindCrotch found none: the parts, and each thigh's skin share.
                foreach (var part in parts.Parts.Where(p => p.Island < 0))
                {
                    var vs = part.Triangles.Distinct().ToArray();
                    float l = 0f, r = 0f, both = 0f;
                    foreach (int v in vs)
                    {
                        float wl = 0f, wr = 0f;
                        for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                        {
                            float w = skin.BoneWeights[v * 8 + k];
                            string b = skin.BoneNames[skin.BoneIndices[v * 8 + k]];
                            if (w > 0f && BodyRetarget.IsThighBone(b)) { if (b.EndsWith("_l", StringComparison.Ordinal)) wl += w; else wr += w; }
                        }
                        if (wl >= BodyRetarget.CrotchAnchorThigh) l++;
                        if (wr >= BodyRetarget.CrotchAnchorThigh) r++;
                        if (wl >= BodyRetarget.CrotchAnchorThigh && wr >= BodyRetarget.CrotchAnchorThigh) both++;
                    }
                    var mn = vs.Length == 0 ? Vector3.Zero : new Vector3(vs.Min(v => At(parts, v).X), vs.Min(v => At(parts, v).Y), vs.Min(v => At(parts, v).Z));
                    var mx = vs.Length == 0 ? Vector3.Zero : new Vector3(vs.Max(v => At(parts, v).X), vs.Max(v => At(parts, v).Y), vs.Max(v => At(parts, v).Z));
                    output.WriteLine($"  {part.Label} {part.Material} tris {part.Triangles.Length / 3} verts {vs.Length} left-thigh {l} right-thigh {r} both {both} "
                                     + $"extent {mn} .. {mx}");
                }
                continue;
            }

            // Every skin vertex, bucketed, for the protrusion of a part from the skin that is not it.
            var skinParts = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
            foreach (var part in parts.Parts.Where(p => p.Island < 0))
            {
                var vs = part.Triangles.Distinct().ToArray();
                int inWin = vs.Count(v => InWindow(At(parts, v), a));
                var tags = string.Join(",", Enumerable.Range(0, parts.AttributeNames.Count)
                                                      .Where(i => (part.AttributeMask & (1u << i)) != 0).Select(i => parts.AttributeNames[i]));
                // The bone shares of its vertices: every non-j_ bone carrying weight, summed over the part.
                var bones = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (int v in vs)
                    for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                    {
                        float w = skin.BoneWeights[v * 8 + k];
                        if (w <= 0f) continue;
                        string b = skin.BoneNames[skin.BoneIndices[v * 8 + k]];
                        if (b.StartsWith("j_", StringComparison.Ordinal)) continue;
                        bones[b] = bones.GetValueOrDefault(b) + w;
                    }
                // Protrusion: how far its farthest vertex in the window lies from every skin vertex NOT of this part.
                var others = skinParts.Where(p => p != part).SelectMany(p => p.Triangles).Distinct().Select(v => At(parts, v)).ToList();
                float protrusion = 0f;
                if (others.Count > 0 && inWin > 0)
                {
                    var grid = others.GroupBy(p => ((int)MathF.Floor(p.X / 0.01f), (int)MathF.Floor(p.Y / 0.01f), (int)MathF.Floor(p.Z / 0.01f)))
                                     .ToDictionary(g => g.Key, g => g.ToList());
                    foreach (int v in vs.Where(v => InWindow(At(parts, v), a)))
                    {
                        var p = At(parts, v);
                        float best = float.MaxValue;
                        for (int r = 0; r <= 12 && best > r * 0.01f; r++)
                        {
                            var c = ((int)MathF.Floor(p.X / 0.01f), (int)MathF.Floor(p.Y / 0.01f), (int)MathF.Floor(p.Z / 0.01f));
                            for (int dx = -r; dx <= r; dx++)
                                for (int dy = -r; dy <= r; dy++)
                                    for (int dz = -r; dz <= r; dz++)
                                    {
                                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                                        if (!grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var cell)) continue;
                                        foreach (var q in cell) best = MathF.Min(best, Vector3.Distance(p, q));
                                    }
                        }
                        if (best < float.MaxValue) protrusion = MathF.Max(protrusion, best);
                    }
                }
                output.WriteLine($"  {part.Label} {part.Material} [{tags}] tris {part.Triangles.Length / 3} verts {vs.Length} "
                                 + $"window {(vs.Length == 0 ? 0 : 100f * inWin / vs.Length):0}% protrudes {protrusion * 1000f:0.0} mm"
                                 + (bones.Count == 0 ? "" : " bones " + string.Join(" ", bones.OrderByDescending(b => b.Value).Take(6)
                                                                                       .Select(b => $"{b.Key}:{b.Value:0}"))));
            }

            // Shape keys, by name and size (which touch the window is S0.3's question once one appears on a penis body).
            var src = SecondSkinWriter.Parse(bytes);
            foreach (var shape in src.Shapes.Keys)
            {
                // A shape rewires triangle corners to morph vertices: count those whose morph or base lies in the window.
                var on = ModelSkinReader.Read(bytes, new HashSet<string> { shape }, null)!;
                int rewired = 0, inWin = 0;
                for (int i = 0; i < Math.Min(on.Triangles.Length, skin.Triangles.Length); i++)
                {
                    if (on.Triangles[i] == skin.Triangles[i]) continue;
                    rewired++;
                    if (InWindow(on.Positions[on.Triangles[i]], a) || InWindow(skin.Positions[skin.Triangles[i]], a)) inWin++;
                }
                output.WriteLine($"  shape {shape}: {src.Shapes[shape].Sum(e => e.Values.Length)} values, {rewired} corners rewired, {inWin} in the window");
            }
        }
        Assert.True(read > 0, "no body of the inventory is installed here");
        Assert.True(wrong.Count == 0, "the genital rules misread: " + string.Join("; ", wrong));
    }

    /// <summary>
    /// S0.2: the base (the bulge copy moved onto lava's inner copy) against the body lava was sculpted on, over the
    /// footprint but the crease the inner copy closed: within 2 mm at p95. And the relief (lava − base) against the sculpt itself (lava − common/64): within
    /// 1.5 mm at p95.
    /// </summary>
    [LocalDataFact(BulgeAssetLocalTests.Common64)]
    [Trait("Plan", "AC-featureless-bulge-10")]
    public void Asset_base_and_relief()
    {
        var asset = Proteus.Services.Bulge.BulgeAsset.Load(null, out var why);
        Assert.True(asset != null, why);
        var body = ModelPartReader.Read(File.ReadAllBytes(LocalData.Path(BulgeAssetLocalTests.Common64)))!;
        var bodyUv = BodyRetarget.Uv(File.ReadAllBytes(LocalData.Path(BulgeAssetLocalTests.Common64)));
        var lavaUv = BodyRetarget.Uv(asset!.Bytes);
        // lava footprint vertex -> the common/64 vertex of the same uv, nearest (the footprint generator's match).
        var bodyVerts = body.Parts.Where(p => p.Island < 0 && (p.Label is "1.1" or "1.2")).SelectMany(p => p.Triangles).Distinct().ToList();
        var byUv = bodyVerts.GroupBy(v => (MathF.Round(bodyUv[v * 2] / 1e-4f), MathF.Round(bodyUv[v * 2 + 1] / 1e-4f)))
                            .ToDictionary(g => g.Key, g => g.ToList());
        var baseErr = new List<float>();
        var reliefErr = new List<float>();
        float crease = 0f;
        foreach (int v in asset.Footprint)
        {
            var key = (MathF.Round(lavaUv[v * 2] / 1e-4f), MathF.Round(lavaUv[v * 2 + 1] / 1e-4f));
            if (!byUv.TryGetValue(key, out var c)) continue;
            var lava = At(asset.Lava, v);
            var b = new Vector3(asset.Base[v * 3], asset.Base[v * 3 + 1], asset.Base[v * 3 + 2]);
            var orig = c.Select(x => At(body, x)).MinBy(q => Vector3.DistanceSquared(q, b));
            // The inner copy closed lava's crotch crease (|x| < 1.5 cm, up to 11.8 mm): reported, not gated.
            if (MathF.Abs(orig.X) < 0.015f) { crease = MathF.Max(crease, Vector3.Distance(b, orig)); continue; }
            baseErr.Add(Vector3.Distance(b, orig));
            reliefErr.Add(Vector3.Distance(lava - b, lava - orig));
        }
        float P95(List<float> x) { x.Sort(); return x[(int)(x.Count * 0.95f)]; }
        float baseP95 = P95(baseErr), reliefP95 = P95(reliefErr);
        output.WriteLine($"footprint {asset.Footprint.Count} (rim {asset.Rim.Count}), relief up to {asset.ReliefMax * 1000f:0.0} mm; "
                         + $"base vs common/64 p95 {baseP95 * 1000f:0.00} mm max {baseErr.Max() * 1000f:0.0} mm (crease closed up to {crease * 1000f:0.0} mm); "
                         + $"relief error p95 {reliefP95 * 1000f:0.00} mm");
        Assert.True(baseP95 <= 0.002f, $"the base lies {baseP95 * 1000f:0.00} mm (p95) off common/64 — over the 2 mm gate");
        Assert.True(reliefP95 <= 0.0015f, $"the relief is {reliefP95 * 1000f:0.00} mm (p95) off the sculpt — over the 1.5 mm gate");
    }

    /// <summary>The bulge copy as cloth: lava with its bulge copy's footprint triangles moved onto a cloth material. Its
    /// skin is then the base (lava's inner copy) and the rest of the copy, coincident with it.</summary>
    internal static byte[] Patch(Proteus.Services.Bulge.BulgeAsset asset)
    {
        var parts = asset.Copy;
        var keep = new HashSet<ModelMaterialWriter.TriangleKey>();
        foreach (var part in parts)
            for (int t = 0; t < part.Triangles.Length / 3; t++)
                if (Enumerable.Range(0, 3).Any(k => asset.Footprint.Contains(part.Triangles[t * 3 + k])))
                    keep.Add(new ModelMaterialWriter.TriangleKey(part.Mesh, part.Submesh, part.Ordinals[t]));
        return ModelMaterialWriter.MoveToMaterial(asset.Bytes, parts, "/mt_c0201e9999_dwn_a.mtrl", keepTriangle: keep.Contains).Model;
    }

    /// <summary>
    /// S0.3 (ii): lava's bulge refitted as cloth from lava's base onto each body without its genitals — does the refit
    /// carry the relief? The bulge's height off the body it lands on, against lava's own off its base: within ±15%.
    /// </summary>
    [LocalDataFact(LocalData.TbseUvMaps, LocalData.UvMaps)]
    public void Transplant_relief()
    {
        var asset = Proteus.Services.Bulge.BulgeAsset.Load(null, out var why);
        Assert.True(asset != null, why);
        var patch = Patch(asset!);
        var patchParts = ModelPartReader.Read(patch)!;
        var cloth = patchParts.Parts.Where(p => p.Island < 0 && p.Material.Contains("e9999", StringComparison.Ordinal)).ToList();
        var clothVerts = cloth.SelectMany(p => p.Triangles).Distinct().ToArray();
        output.WriteLine($"patch: {cloth.Count} cloth parts, {clothVerts.Length} vertices; skin parts "
                         + string.Join(" ", patchParts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).Select(p => p.Label)));

        // lava's own relief: each cloth vertex's distance to the base skin (the patch's skin).
        var patchSkin = new BodySurface(patchParts, 0.01f);
        static float Off(BodySurface s, Vector3 p) => s.Nearest(p, 0.25f, out var hit) ? hit.Distance : 0.25f;
        float lavaHeight = clothVerts.Max(v => Off(patchSkin, At(patchParts, v)));
        output.WriteLine($"lava's bulge stands {lavaHeight * 1000f:0.0} mm off its base");

        var log = Substitute.For<IPluginLog>();
        var tbseMaps = new UVRemapService(log, LocalData.Path(LocalData.TbseUvMaps));
        foreach (var (name, need, penis, _) in Bodies.Where(b => b.Penis))
        {
            if (LocalData.Missing([need]) is { } miss) { output.WriteLine($"== {name}: SKIPPED ({miss})"); continue; }
            var bytes = File.ReadAllBytes(LocalData.Path(need));
            var body = ModelPartReader.Read(bytes)!;
            var skin = ModelSkinReader.Read(bytes, null, null)!;
            var reading = Proteus.Services.Bulge.GenitalParts.Read(body, skin)!;
            // The body without its genitals: whole genital parts out (merged-in triangles stay, for this measurement).
            var drop = reading.Genitals.Where(g => g.Rule != "b").Select(g => g.Part).ToHashSet();
            var without = new ModelParts
            {
                Positions = body.Positions, Normals = body.Normals, MeshSpans = body.MeshSpans,
                Parts = body.Parts.Where(p => !drop.Contains(p)).ToList(), AttributeNames = body.AttributeNames,
                Min = body.Min, Max = body.Max, ShatteredSubmeshes = body.ShatteredSubmeshes,
            };
            bool male = body.Parts.Any(p => p.Material.Contains("c0101", StringComparison.Ordinal) || p.Material.Contains("c0301", StringComparison.Ordinal));
            var maps = male ? tbseMaps : new UVRemapService(log, LocalData.Path(LocalData.UvMaps));
            if (!BodyCorrespondence.TryBuild(patchParts, BodyRetarget.Uv(patch), without, BodyRetarget.Uv(bytes), name,
                                             out var corr, out string refusal, out _, maps, false, male))
            {
                output.WriteLine($"== {name}: correspondence refused — {refusal}");
                continue;
            }
            var pair = new BodyRetarget.SlotPair("_dwn", corr!, without, bytes, patch);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var planned = BodyRetarget.Plan(patchParts, (byte[])patch.Clone(), [pair], garmentSlot: "_dwn", acrossBodies: true);
            // The patch's own folds: the in-place solve (RetargetEdit applied to the patch, vertex order kept), its cloth only.
            var moved = MeshVolumeService.Inflate((byte[])patch.Clone(), planned.Edit).Model;
            var movedParts = ModelPartReader.Read(moved)!;
            int patchFolds = 0, patchFaces = 0;
            foreach (var part in cloth)
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int x = part.Triangles[t], y = part.Triangles[t + 1], z = part.Triangles[t + 2];
                    var n0 = Vector3.Cross(At(patchParts, y) - At(patchParts, x), At(patchParts, z) - At(patchParts, x));
                    var n1 = Vector3.Cross(At(movedParts, y) - At(movedParts, x), At(movedParts, z) - At(movedParts, x));
                    if (n0.LengthSquared() < 1e-14f) continue;
                    patchFaces++;
                    if (n1.LengthSquared() < 1e-14f || Vector3.Dot(n0, n1) < 0f) patchFolds++;
                }
            output.WriteLine($"   patch cloth folds (in place): {patchFolds} of {patchFaces}");
            sw.Stop();
            var outParts = ModelPartReader.Read(planned.Model)!;
            var outCloth = outParts.Parts.Where(p => p.Island < 0 && p.Material.Contains("e9999", StringComparison.Ordinal))
                                   .SelectMany(p => p.Triangles).Distinct().ToArray();
            var onto = new BodySurface(without, 0.01f);
            float height = outCloth.Length == 0 ? 0f : outCloth.Max(v => Off(onto, At(outParts, v)));
            var lo = outCloth.Length == 0 ? Vector3.Zero : new Vector3(outCloth.Min(v => At(outParts, v).X), outCloth.Min(v => At(outParts, v).Y), outCloth.Min(v => At(outParts, v).Z));
            var hi = outCloth.Length == 0 ? Vector3.Zero : new Vector3(outCloth.Max(v => At(outParts, v).X), outCloth.Max(v => At(outParts, v).Y), outCloth.Max(v => At(outParts, v).Z));
            output.WriteLine($"== {name}: bulge {outCloth.Length} vertices stands {height * 1000f:0.0} mm off the body "
                             + $"({height / lavaHeight * 100f:0}% of lava's); folded {planned.Report.Folded}, pushed {planned.Report.Pushed}, "
                             + $"worst move {planned.Report.WorstMove * 1000f:0} mm; extent {lo} .. {hi}; {sw.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>
    /// S0.2′ (rev7/8): is lava's bulge a graph over the crotch chart? The chart: a cylinder about an axis parallel to X
    /// through (MidX, anchor.Y + h·s, anchor.Z + d·s); u = (x − MidX)/s, θ the angle about it. The bulge core is the
    /// bulge copy moved more than <paramref name="threshold"/> off the base, plus two rings; a triangle FLIPS when its
    /// signed (u, θ) area is below −1% of the core's mean |area| (the majority's sign taken as forward). Logged over a
    /// small grid of axis heights, depths and pitch tilts.
    /// </summary>
    [Fact]
    public void Lava_chart_flips()
    {
        var asset = Proteus.Services.Bulge.BulgeAsset.Load(null, out var why);
        Assert.True(asset != null, why);
        var lava = asset!.Lava;
        var skin = ModelSkinReader.Read(asset.Bytes, null, null)!;
        var inner = new ModelParts
        {
            Positions = lava.Positions, Normals = lava.Normals, MeshSpans = lava.MeshSpans,
            Parts = lava.Parts.Where(p => !Proteus.Services.Bulge.BulgeAsset.BulgeCopy.Contains(p.Label)).ToList(),
            AttributeNames = lava.AttributeNames, Min = lava.Min, Max = lava.Max, ShatteredSubmeshes = lava.ShatteredSubmeshes,
        };
        var a = BodyRetarget.FindCrotch([(inner, skin)])!.Value;
        output.WriteLine($"lava anchor y {a.Y:0.000} midX {a.MidX:0.000} z {a.At.Z:0.000} s {a.Scale:0.00}");
        Vector3 Base(int v) => new(asset.Base[v * 3], asset.Base[v * 3 + 1], asset.Base[v * 3 + 2]);
        var tris = asset.Copy.SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                         .Select(t => (p.Triangles[t * 3], p.Triangles[t * 3 + 1], p.Triangles[t * 3 + 2]))).ToList();
        foreach (float threshold in new[] { 0.005f, 0.010f })
        {
            var core = asset.Copy.SelectMany(p => p.Triangles).Distinct()
                            .Where(v => Vector3.Distance(At(lava, v), Base(v)) > threshold).ToHashSet();
            // two rings, by triangle adjacency in the copy
            for (int ring = 0; ring < 2; ring++)
                core.UnionWith(tris.Where(t => core.Contains(t.Item1) || core.Contains(t.Item2) || core.Contains(t.Item3))
                                   .SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).ToList());
            var coreTris = tris.Where(t => core.Contains(t.Item1) && core.Contains(t.Item2) && core.Contains(t.Item3)).ToList();
            var ext = (new Vector3(core.Min(v => At(lava, v).X), core.Min(v => At(lava, v).Y), core.Min(v => At(lava, v).Z)),
                       new Vector3(core.Max(v => At(lava, v).X), core.Max(v => At(lava, v).Y), core.Max(v => At(lava, v).Z)));
            output.WriteLine($"threshold {threshold * 1000:0} mm: core {core.Count} vertices, {coreTris.Count} triangles, extent {ext.Item1} .. {ext.Item2}");
            int bestFlips = int.MaxValue, bestBase = int.MaxValue;
            string best = "", bestBaseAt = "";
            foreach (float h in new[] { 0f, 0.03f, 0.06f, 0.12f, 0.18f, 0.24f, 0.32f })
                foreach (float d in new[] { -0.15f, -0.10f, -0.06f, -0.03f, 0f })
                    foreach (float pitch in new[] { -60f, -45f, -30f, -20f, -10f, 0f, 15f })
                    {
                        var o = new Vector3(a.MidX, a.Y + h * a.Scale, a.At.Z + d * a.Scale);
                        float c = MathF.Cos(pitch * MathF.PI / 180), sn = MathF.Sin(pitch * MathF.PI / 180);
                        (float U, float T) Chart(Vector3 p)
                        {
                            var q = p - o;
                            float y = q.Y * c - q.Z * sn, z = q.Y * sn + q.Z * c;   // pitch about X
                            return ((q.X) / a.Scale, MathF.Atan2(y, z));            // θ: 0 forward (+Z), −π/2 down
                        }
                        int Flips(Func<int, Vector3> pos)
                        {
                            var areas = coreTris.Select(t =>
                            {
                                var (u0, t0) = Chart(pos(t.Item1));
                                var (u1, t1) = Chart(pos(t.Item2));
                                var (u2, t2) = Chart(pos(t.Item3));
                                return ((u1 - u0) * (t2 - t0) - (u2 - u0) * (t1 - t0)) * 0.5f;
                            }).ToList();
                            float sign = MathF.Sign(areas.Sum());
                            float mean = areas.Average(MathF.Abs);
                            return areas.Count(x => x * sign < -0.01f * mean);
                        }
                        int flips = Flips(v => At(lava, v));
                        int baseFlips = Flips(Base);
                        if (flips < bestFlips) { bestFlips = flips; best = $"h {h:0.00} d {d:0.00} pitch {pitch:0} (base there {baseFlips})"; }
                        if (baseFlips < bestBase) { bestBase = baseFlips; bestBaseAt = $"h {h:0.00} d {d:0.00} pitch {pitch:0}"; }
                    }
            output.WriteLine($"  fewest flips {bestFlips} of {coreTris.Count} at {best}; the BASE's fewest {bestBase} at {bestBaseAt}");
        }
    }
}
