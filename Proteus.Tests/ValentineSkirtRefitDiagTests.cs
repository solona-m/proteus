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
/// Reported: the vanilla Comfy Valentione Skirt (e6228 legs) refitted onto Neolithe YAS Gen A Small, with the skin
/// replaced — the body showed through the tops of the socks. Neolithe's legs carry the shins twice, <c>atr_dv_a</c> and
/// a thicker <c>atr_dv_b</c>, and an IMC option picks one; the swap dropped the variant tags and drew both, the thick
/// calf 10 mm proud of the thin one at the cuff. (The underwear measured clear of the skin: 0.2-1 mm outside it.)
/// </summary>
public class ValentineSkirtRefitDiagTests(ITestOutputHelper output)
{
    private const string GamePath = "chara/equipment/e6228/model/c0201e6228_dwn.mdl";
    private const string SourcePath = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string BodyMod = @"E:\Penumbradt\Neolithe YAS AIO";

    private static byte[]? Vanilla(string path)
    {
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) return null;
        return new Lumina.GameData(sqpack).GetFile(path)?.Data;
    }

    [Fact]
    public void The_swapped_skin_is_only_the_variant_the_body_draws()
    {
        if (!Directory.Exists(BodyMod) || Vanilla(GamePath) is not { } garmentBytes
            || Vanilla(SourcePath) is not { } sourceBytes)
        {
            output.WriteLine("not available");
            return;
        }

        var catalog = BodySizeCatalog.Read(BodyMod);
        var option = catalog.For("_dwn").First(o => o.FullLabel.Contains("Gen A Small") && o.FullLabel.Contains("DEFAULT"));
        byte[] targetBytes = File.ReadAllBytes(catalog.PathOf(option));
        var whole = ModelPartReader.Read(targetBytes)!;

        // The mod's defaults: thin shins (a), and its two pube options (c, d).
        ushort mask = ImcEntrySource.MaskFor(BodyMod, 0, "Legs", null)!.Value;
        var hidden = BodyRetarget.UndrawnVariants(whole.AttributeNames, "_dwn", mask);
        output.WriteLine($"mask 0x{mask:x}; not drawn: {string.Join(", ", hidden)}");
        Assert.Contains("atr_dv_b", hidden);
        Assert.DoesNotContain("atr_dv_a", hidden);

        var target = BodyRetarget.Without(whole, hidden);
        var source = ModelPartReader.Read(sourceBytes)!;
        var remap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), @"E:\repos\Proteus\Proteus");
        Assert.True(BodyCorrespondence.TryBuild(source, BodyRetargetDiagTests.Uv(sourceBytes), target,
                                                BodyRetargetDiagTests.Uv(targetBytes), "_dwn", out var built,
                                                out string refusal, remap), refusal);
        var pairs = new List<BodyRetarget.SlotPair> { new("_dwn", built!, target, targetBytes, sourceBytes, hidden) };

        var garment = ModelPartReader.Read(garmentBytes)!;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_dwn", replaceSkin: true, acrossBodies: true,
                                        clearBody: true);
        output.WriteLine($"{planned.Report}");
        var refit = ModelPartReader.Read(planned.Model)!;

        // Every skin vertex the refit draws lies on the drawn body. The thick shins sat up to 10 mm off it.
        var drawn = new BodySurface(target, BodySurface.CellFor(0.01f));
        float worst = 0f;
        foreach (int v in refit.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct())
        {
            var p = new Vector3(refit.Positions[v * 3], refit.Positions[v * 3 + 1], refit.Positions[v * 3 + 2]);
            worst = MathF.Max(worst, drawn.Nearest(p, 0.05f, out var hit) ? hit.Distance : 0.05f);
        }
        output.WriteLine($"worst skin vertex off the drawn body: {worst * 1000:F3} mm");
        Assert.True(worst < 1e-4f, $"a skin vertex sits {worst * 1000:F2} mm off the body the game draws");

        // Every copy of the cuff's seam vertices, front and back: the bones each follows in the written file.
        var skinOf = ModelSkinReader.Read(planned.Model, null, null)!;
        var shippedSkin = ModelSkinReader.Read(garmentBytes, null, null)!;
        var cuffPart = refit.Parts.First(p => p.Island < 0 && p.Label == "1.4");
        var cuffShipped = garment.Parts.First(p => p.Island < 0 && p.Label == "2.4");
        var shippedAt = cuffShipped.Triangles.Distinct().ToList();
        foreach (int v in cuffPart.Triangles.Distinct().Where(v => P(refit, v).X < 0f && MathF.Abs(P(refit, v).X + 0.096f) < 0.004f
                                                                 && MathF.Abs(P(refit, v).Z) > 0.03f)
                                  .OrderBy(v => P(refit, v).Z).ThenBy(v => P(refit, v).Y))
            output.WriteLine($"  seam v{v} {P(refit, v):F4}: {Bones(skinOf, v)}");
        foreach (int v in shippedAt.Where(v => P(garment, v).X < 0f && MathF.Abs(P(garment, v).X + 0.095f) < 0.004f
                                           && MathF.Abs(P(garment, v).Z) > 0.03f)
                                   .OrderBy(v => P(garment, v).Z).ThenBy(v => P(garment, v).Y))
            output.WriteLine($"  shipped seam v{v} {P(garment, v):F4}: {Bones(shippedSkin, v)}");

        // The cloth mesh's texture coordinates, vertex for vertex, as shipped and as written.
        float[] uvWas = BodyRetargetDiagTests.Uv(garmentBytes), uvNow = BodyRetargetDiagTests.Uv(planned.Model);
        var clothMeshOf = garment.Parts.First(p => p.Island < 0 && p.Label == "2.2").Mesh;
        var clothUsed = garment.Parts.Where(p => p.Island < 0 && p.Mesh == clothMeshOf).SelectMany(p => p.Triangles)
                               .Distinct().OrderBy(v => v).ToList();
        int clothBase = refit.MeshSpans.First(s => s.Mesh == refit.Parts.First(p => p.Island < 0 && p.Label == "1.4").Mesh).BaseVertex;
        int uvChanged = 0;
        foreach (int v in garment.Parts.First(p => p.Island < 0 && p.Label == "2.4").Triangles.Distinct().OrderBy(v => v))
        {
            int w = clothBase + clothUsed.IndexOf(v);
            float du = MathF.Abs(uvWas[v * 2] - uvNow[w * 2]) + MathF.Abs(uvWas[v * 2 + 1] - uvNow[w * 2 + 1]);
            bool moved = Vector3.Distance(P(garment, v), P(refit, w)) > 0.02f;
            if (du > 1e-4f || moved) uvChanged++;
            if (P(garment, v).X < 0f)
                output.WriteLine($"  uv v{v}->{w} {P(garment, v):F3}->{P(refit, w):F3}: ({uvWas[v * 2]:F4},{uvWas[v * 2 + 1]:F4}) -> " +
                                 $"({uvNow[w * 2]:F4},{uvNow[w * 2 + 1]:F4}){(du > 1e-4f ? "  CHANGED" : "")}");
        }
        output.WriteLine($"cuff vertices whose uv or place changed: {uvChanged}");

        // Every corner of the cuff was clear of the calf while the calf came 1 mm through the middle of its wall faces,
        // front and back: the pale notch. The push-out now looks at faces as well as vertices — with the body being
        // cleared, and without, where it may only undo what the refit did.
        Assert.Equal(0, SkinThroughCuff("clear", refit));
        var kept = BodyRetarget.Plan(garment, garmentBytes, pairs, "_dwn", replaceSkin: true, acrossBodies: true);
        output.WriteLine($"not clearing: {kept.Report}");
        Assert.Equal(0, SkinThroughCuff("not clearing", ModelPartReader.Read(kept.Model)!));

        SockClearance(garment, source, drawn, refit);
        CuffStandoff(garment, source, target, refit);
    }

    /// <summary>
    /// The sock's top and its cuff, one vertex per place round the leg: how far each stands off the calf, as shipped and
    /// as refitted. Reported second: the cuff sat 1-5 mm off the vanilla calf and 2-10 mm off Neolithe's, because cloth
    /// followed the eased field; the cuff's hidden lid showed in the gap as a pale notch.
    /// </summary>
    private void CuffStandoff(ModelParts garment, ModelParts source, ModelParts target, ModelParts refit)
    {
        var vanillaCalf = new BodySurface(source, BodySurface.CellFor(0.01f));
        var newCalf = new BodySurface(target, BodySurface.CellFor(0.01f));
        var clothMesh = garment.Parts.First(p => p.Island < 0 && p.Label == "2.2").Mesh;
        var used = garment.Parts.Where(p => p.Island < 0 && p.Mesh == clothMesh).SelectMany(p => p.Triangles)
                          .Distinct().OrderBy(v => v).ToList();
        int rb = refit.MeshSpans.First(s => s.Mesh == refit.Parts.First(p => p.Island < 0 && p.Label == "1.2").Mesh).BaseVertex;

        foreach (string label in new[] { "2.4", "2.2" })
        {
            var seen = new HashSet<(int, int, int)>();
            foreach (int v in garment.Parts.First(p => p.Island < 0 && p.Label == label).Triangles.Distinct()
                                     .Where(v => P(garment, v).X < 0f && P(garment, v).Y > 0.31f)
                                     .OrderBy(v => MathF.Atan2(P(garment, v).Z + 0.018f, P(garment, v).X + 0.094f)))
            {
                if (!seen.Add(Key(P(garment, v)))) continue;
                var p = P(garment, v);
                var q = P(refit, rb + used.IndexOf(v));
                string Gap(BodySurface s, Vector3 at) => s.Nearest(at, 0.03f, out var h)
                    ? $"{Vector3.Dot(at - h.Point, h.Normal) * 1000:F2} mm" : "none";
                output.WriteLine($"  {label} v{v} ({p.X:F3},{p.Y:F3},{p.Z:F3}): off the vanilla calf {Gap(vanillaCalf, p)}, " +
                                 $"off the new calf {Gap(newCalf, q)}");

            }
        }
    }

    /// <summary>The sock (2.2) and its cuff (2.4) against the drawn calf: how many points are inside, and whether each
    /// sat on a vanilla body vertex as shipped (which the transfer snaps, and the push-out never moves).</summary>
    private void SockClearance(ModelParts garment, ModelParts source, BodySurface drawn, ModelParts refit)
    {
        var onBody = new HashSet<(int, int, int)>();
        for (int v = 0; v < source.Positions.Length / 3; v++) onBody.Add(Key(P(source, v)));

        // The writer compacts the cloth mesh to the vertices its triangles use, in order.
        var clothMesh = garment.Parts.First(p => p.Island < 0 && p.Label == "2.2").Mesh;
        var used = garment.Parts.Where(p => p.Island < 0 && p.Mesh == clothMesh).SelectMany(p => p.Triangles)
                          .Distinct().OrderBy(v => v).ToList();
        int rb = refit.MeshSpans.First(s => s.Mesh == refit.Parts.First(p => p.Island < 0 && p.Label == "1.2").Mesh).BaseVertex;

        foreach (string label in new[] { "2.2", "2.4" })
        {
            var verts = garment.Parts.First(p => p.Island < 0 && p.Label == label).Triangles.Distinct().ToList();
            int inside = 0, snappedInside = 0, snapped = 0;
            foreach (int v in verts)
            {
                bool snap = onBody.Contains(Key(P(garment, v)));
                if (snap) snapped++;
                var p = P(refit, rb + used.IndexOf(v));
                if (!drawn.Nearest(p, 0.02f, out var hit)) continue;
                float s = Vector3.Dot(p - hit.Point, hit.Normal);
                if (s >= 0f) continue;
                inside++;
                if (snap) snappedInside++;
                output.WriteLine($"    {label} v{v} {(snap ? "SNAPPED" : "")} at ({p.X:F3},{p.Y:F3},{p.Z:F3}) " +
                                 $"{s * 1000:F2} mm; as shipped ({P(garment, v).X:F3},{P(garment, v).Y:F3},{P(garment, v).Z:F3})");
            }
            // The top of the sock, where the cuff's notch looks through to it, against the skin the refit DRAWS.
            var swapped = new BodySurface(new ModelParts
            {
                Positions = refit.Positions, Normals = refit.Normals, MeshSpans = refit.MeshSpans,
                Parts = refit.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList(),
                AttributeNames = refit.AttributeNames, Min = refit.Min, Max = refit.Max,
                ShatteredSubmeshes = refit.ShatteredSubmeshes,
            }, BodySurface.CellFor(0.01f));
            foreach (int v in verts.Where(v => P(refit, rb + used.IndexOf(v)).Y > 0.30f && P(refit, rb + used.IndexOf(v)).X < 0f)
                                   .OrderBy(v => P(refit, rb + used.IndexOf(v)).Z))
            {
                var p = P(refit, rb + used.IndexOf(v));
                string skin = swapped.Nearest(p, 0.02f, out var h)
                    ? $"{Vector3.Dot(p - h.Point, h.Normal) * 1000:F2} mm (landing {h.Distance * 1000:F2})" : "none";
                var w = P(garment, v);
                output.WriteLine($"    top {label} v{v}{(onBody.Contains(Key(P(garment, v))) ? " SNAPPED" : "")} " +
                                 $"({p.X:F3},{p.Y:F3},{p.Z:F3}) vs drawn skin {skin}; shipped ({w.X:F3},{w.Y:F3},{w.Z:F3})");
            }
            output.WriteLine($"  {label}: {verts.Count} verts, {snapped} on a vanilla body vertex; inside the drawn calf " +
                             $"{inside}, of them snapped {snappedInside}");
        }
    }

    /// <summary>The refit saved in game: every cuff vertex at the front and back of the right leg, duplicates and all.</summary>
    [Fact]
    public void The_saved_cuff_rim_copy_by_copy()
    {
        const string saved = @"E:\Penumbradt\Comfy Valentione Skirt Neolithe YAS AIO\Body Retarget\" +
                             @"Neolithe YAS AIO - DEFAULT - Gen A Small\chara\equipment\e6228\model\c0201e6228_dwn.mdl";
        if (!File.Exists(saved)) return;
        var m = ModelPartReader.Read(File.ReadAllBytes(saved))!;
        foreach (var part in m.Parts.Where(p => p.Island < 0 && p.Label is "1.2" or "1.4"))
        {
            output.WriteLine($"{part.Label} [{part.Material}] {part.Triangles.Length / 3} tris");
            foreach (int v in part.Triangles.Distinct().Where(v => P(m, v).X < 0f && P(m, v).Y > 0.31f
                                                                  && MathF.Abs(P(m, v).X + 0.095f) < 0.012f)
                                    .OrderBy(v => P(m, v).Z).ThenBy(v => P(m, v).Y))
                output.WriteLine($"   v{v} ({P(m, v).X:F4},{P(m, v).Y:F4},{P(m, v).Z:F4})");
            // Triangles using the front-centre rim, as drawn.
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (new[] { a, b, c }.All(v => P(m, v).Z > 0.015f && P(m, v).X < 0f && P(m, v).Y > 0.31f))
                    output.WriteLine($"   tri {a},{b},{c}: {P(m, a):F3} {P(m, b):F3} {P(m, c):F3}");
            }
        }
    }

    /// <summary>The cuff's triangles as the game ships them: which use the centre point, and the rest by height.</summary>
    [Fact]
    public void The_cuff_s_triangles()
    {
        if (Vanilla(GamePath) is not { } bytes) return;
        var m = ModelPartReader.Read(bytes)!;
        var cuff = m.Parts.First(p => p.Island < 0 && p.Label == "2.4");
        var norms = m.Normals;
        for (int t = 0; t + 2 < cuff.Triangles.Length; t += 3)
        {
            int a = cuff.Triangles[t], b = cuff.Triangles[t + 1], c = cuff.Triangles[t + 2];
            if (P(m, a).X > 0f) continue;   // right leg only
            var n = Vector3.Normalize(Vector3.Cross(P(m, b) - P(m, a), P(m, c) - P(m, a)));
            output.WriteLine($"tri {a},{b},{c}: {P(m, a):F3} {P(m, b):F3} {P(m, c):F3} face n {n:F2} " +
                             $"vertex n {new Vector3(norms[a * 3], norms[a * 3 + 1], norms[a * 3 + 2]):F2}");
        }
    }

    /// <summary>
    /// The skin the refit draws, against the cuff's WALL faces (not the cap, which faces up): how many skin points at the
    /// cuff's height stand in front of a wall face, within its reach, and so show through it.
    /// </summary>
    private int SkinThroughCuff(string label, ModelParts refit)
    {
        var cuffPart = refit.Parts.First(p => p.Island < 0 && p.Label == "1.4");
        var wall = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        for (int t = 0; t + 2 < cuffPart.Triangles.Length; t += 3)
        {
            Vector3 a = P(refit, cuffPart.Triangles[t]), b = P(refit, cuffPart.Triangles[t + 1]), c = P(refit, cuffPart.Triangles[t + 2]);
            if (MathF.Abs(Vector3.Normalize(Vector3.Cross(b - a, c - a)).Y) < 0.7f) wall.Add((a, b, c));
        }
        var skinVerts = refit.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                             .SelectMany(p => p.Triangles).Distinct()
                             .Where(v => P(refit, v).Y is > 0.318f and < 0.345f).ToList();
        int through = 0;
        foreach (int v in skinVerts)
        {
            var s = P(refit, v);
            float best = 1f, signed = 0f;
            foreach (var (a, b, c) in wall)
            {
                var q = BrushTransfer.ClosestOnTriangle(s, a, b, c, out _, out _, out _);
                float d = Vector3.Distance(s, q);
                if (d >= best) continue;
                best = d;
                signed = Vector3.Dot(s - q, Vector3.Normalize(Vector3.Cross(b - a, c - a)));
            }
            if (best > 0.004f || signed <= 0f) continue;
            through++;
            output.WriteLine($"  {label}: skin v{v} ({s.X:F3},{s.Y:F3},{s.Z:F3}) is {signed * 1000:F2} mm OUTSIDE the cuff wall");
        }
        output.WriteLine($"{label}: skin points outside the cuff wall: {through} of {skinVerts.Count} at the cuff's height");
        return through;
    }

    private static string Bones(XivLiveMesh.SkinnedMesh skin, int v)
    {
        var parts = new List<string>();
        for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
        {
            float w = skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
            if (w > 0f) parts.Add($"{skin.BoneNames[skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]]}:{w:F2}");
        }
        return string.Join(" ", parts);
    }

    private static Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static (int, int, int) Key(Vector3 p)
        => ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));
}
