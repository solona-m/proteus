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
}
