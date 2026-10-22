using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SkinTuckSheet;

namespace Proteus.Tests;

/// <summary>
/// <see cref="BodyRetarget.ClothCrossings.SeesInside"/> and the grid walk under it (<see cref="BodyRetarget.ClothCrossings.FirstHit"/>):
/// whether the first of a garment's cloth behind a skin point is seen from inside, and which face that is.
/// </summary>
public class ClothCrossingsTests
{
    private static readonly Vector3 OnSkin = new(0f, Y(5), 0f);
    private static readonly Vector3 Into = -Vector3.UnitZ;

    private static BodyRetarget.ClothCrossings Cloth(byte[] garment) => new(ModelPartReader.Read(garment)!);

    [Fact]
    [Trait("Plan", "EDGE-coatee-strap-slit-1")]
    public void Nothing_behind_is_not_inside()
    {
        var cloth = Cloth(Top(_ => 0.015f, clothFrom: Edge));
        Assert.Null(cloth.FirstHit(OnSkin, Into, BodyRetarget.TuckSeeReach));
        Assert.False(cloth.SeesInside(OnSkin, Into, BodyRetarget.TuckSeeReach));
        Assert.True(Cloth(OpenFrontTop()).SeesInside(OnSkin, Into, BodyRetarget.TuckSeeReach));
    }

    [Fact]
    [Trait("Plan", "EDGE-coatee-strap-slit-2")]
    public void Outside_face_is_not_inside()
        => Assert.False(Cloth(OpenFrontTop(facingOut: false)).SeesInside(OnSkin, Into, BodyRetarget.TuckSeeReach));

    [Fact]
    [Trait("Plan", "EDGE-coatee-strap-slit-3")]
    public void Beyond_reach_is_not_inside()
    {
        var cloth = Cloth(OpenFrontTop(backZ: -0.3f));
        Assert.True(cloth.SeesInside(OnSkin, Into, 0.31f));
        Assert.False(cloth.SeesInside(OnSkin, Into, 0.29f));
    }

    [Fact]
    [Trait("Plan", "EDGE-coatee-strap-slit-10")]
    public void Zero_area_face_is_not_inside()
    {
        // A garment whose only cloth behind the point is a face with no area.
        var at = new Vector3(0f, Y(5), -0.1f);
        var flat = Mesh([at + new Vector3(-0.05f, 0f, 0f), at, at + new Vector3(0.05f, 0f, 0f)], [0, 1, 2], -Vector3.UnitZ);
        var cloth = new BodyRetarget.ClothCrossings(ModelPartReader.Read(SyntheticModel.BuildSurface([], [flat]))!);
        Assert.Null(cloth.FirstHit(OnSkin, Into, BodyRetarget.TuckSeeReach));
        Assert.False(cloth.SeesInside(OnSkin, Into, BodyRetarget.TuckSeeReach));
    }

    [Fact]
    [Trait("Plan", "EDGE-coatee-strap-slit-18")]
    public void Reversed_winding_reads_inside_by_the_model_sign()
    {
        // The same garment from an exporter that winds its outer faces the other way throughout — its normals still out of
        // the cloth: the same answers.
        var (pos, tris) = Panels();
        var wound = new SyntheticModel.SurfaceMesh(Cloth_, [.. pos], [.. Shaded(pos, tris)], Uvs(pos), Weights(pos), [(0u, tris)]);
        var flipped = tris.Chunk(3).SelectMany(t => new[] { t[0], t[2], t[1] }).ToArray();
        var rewound = new SyntheticModel.SurfaceMesh(Cloth_, [.. pos], [.. Shaded(pos, tris)], Uvs(pos),
                                                     Weights(pos), [(0u, flipped)]);
        var a = new BodyRetarget.ClothCrossings(ModelPartReader.Read(SyntheticModel.BuildSurface([], [wound]))!);
        var b = new BodyRetarget.ClothCrossings(ModelPartReader.Read(SyntheticModel.BuildSurface([], [rewound]))!);
        foreach (var (p, d) in Probes())
            Assert.Equal(a.SeesInside(p, d, 1f), b.SeesInside(p, d, 1f));
        Assert.True(a.SeesInside(Vector3.Zero, -Vector3.UnitZ, 1f));    // from inside the box: its back, seen from inside
    }

    [Fact]
    [Trait("Plan", "EDGE-coatee-strap-slit-18")]
    public void A_face_with_disagreeing_normals_is_judged_by_winding()
    {
        // One face of the box's back has its vertex normals turned inward — as a refit leaves normals behind on cloth it
        // pulled hard: still read by the way it is wound, as the game culls it.
        var (pos, tris) = Panels();
        var normals = Shaded(pos, tris);
        var behind = Enumerable.Range(0, tris.Length / 3)
                               .First(t => Hits(Vector3.Zero, -Vector3.UnitZ, pos[tris[t * 3]], pos[tris[t * 3 + 1]], pos[tris[t * 3 + 2]], out _));
        var lone = pos.ToList();
        var loneTris = tris.ToArray();
        var loneNormals = normals.ToList();
        for (int k = 0; k < 3; k++)
        {
            lone.Add(pos[tris[behind * 3 + k]]);
            loneNormals.Add(-normals[tris[behind * 3 + k]]);
            loneTris[behind * 3 + k] = lone.Count - 1;
        }
        var mesh = new SyntheticModel.SurfaceMesh(Cloth_, [.. lone], [.. loneNormals], Uvs(lone), Weights(lone), [(0u, loneTris)]);
        var cloth = new BodyRetarget.ClothCrossings(ModelPartReader.Read(SyntheticModel.BuildSurface([], [mesh]))!);
        Assert.True(cloth.SeesInside(Vector3.Zero, -Vector3.UnitZ, 1f));
    }

    [Fact]
    [Trait("Plan", "EDGE-coatee-strap-slit-14")]
    public void Grid_walk_matches_brute_force_on_large_triangles()
    {
        // Faces 50-150 mm across, five or more of the grid's 20 mm cells, and rays of every kind: random, nearly along an
        // axis, and from a cell's corner.
        var rng = new Random(1358);
        float R(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
        var pos = new List<Vector3>();
        var tris = new List<int>();
        for (int k = 0; k < 120; k++)
        {
            var c = new Vector3(R(-0.2f, 0.2f), R(-0.2f, 0.2f), R(-0.2f, 0.2f));
            for (int j = 0; j < 3; j++) pos.Add(c + new Vector3(R(-0.075f, 0.075f), R(-0.075f, 0.075f), R(-0.075f, 0.075f)));
            tris.AddRange([k * 3, k * 3 + 1, k * 3 + 2]);
        }
        var mesh = new SyntheticModel.SurfaceMesh(Cloth_, [.. pos], [.. pos.Select(_ => Vector3.UnitZ)], Uvs(pos), Weights(pos),
                                                  [(0u, [.. tris])]);
        var read = ModelPartReader.Read(SyntheticModel.BuildSurface([], [mesh]))!;
        var cloth = new BodyRetarget.ClothCrossings(read);
        var faces = Faces(read);

        int hits = 0;
        for (int n = 0; n < 2000; n++)
        {
            Vector3 o, d;
            switch (n % 4)
            {
                case 0:
                    o = new Vector3(R(-0.3f, 0.3f), R(-0.3f, 0.3f), R(-0.3f, 0.3f));
                    d = new Vector3(R(-1f, 1f), R(-1f, 1f), R(-1f, 1f));
                    break;
                case 1:   // nearly along an axis
                    o = new Vector3(R(-0.3f, 0.3f), R(-0.3f, 0.3f), R(-0.3f, 0.3f));
                    d = (n / 4 % 3) switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => -Vector3.UnitZ } + new Vector3(1e-4f, -1e-4f, 1e-4f) * R(0f, 1f);
                    break;
                case 2:   // from a cell's corner
                    o = new Vector3(MathF.Round(R(-0.3f, 0.3f) / 0.02f) * 0.02f, MathF.Round(R(-0.3f, 0.3f) / 0.02f) * 0.02f,
                                    MathF.Round(R(-0.3f, 0.3f) / 0.02f) * 0.02f);
                    d = new Vector3(R(-1f, 1f), R(-1f, 1f), R(-1f, 1f));
                    break;
                default:  // exactly along an axis
                    o = new Vector3(R(-0.3f, 0.3f), R(-0.3f, 0.3f), R(-0.3f, 0.3f));
                    d = (n / 4 % 3) switch { 0 => -Vector3.UnitX, 1 => -Vector3.UnitY, _ => Vector3.UnitZ };
                    break;
            }
            float reach = R(0.05f, 0.8f);
            var walked = cloth.FirstHit(o, d, reach);
            var brute = BruteFirst(faces, o, Vector3.Normalize(d), reach);
            Assert.Equal(brute.HasValue, walked.HasValue);
            if (brute is not { } b) continue;
            hits++;
            Assert.True(MathF.Abs(b.T - walked!.Value.T) <= 1e-6f, $"ray {n}: walked t {walked.Value.T}, brute {b.T}");
            SameFace(faces, o, d, b, walked.Value, $"ray {n}");
        }
        Assert.True(hits > 300, $"only {hits} rays hit anything");
    }

    [LocalDataFact(LocalData.GameData, LocalData.Mods + ":Neolithe [ALL IN ONE]")]
    [Trait("Plan", "EDGE-coatee-strap-slit-14")]
    public void Grid_walk_matches_brute_force_on_the_Coatee()
    {
        // The game's Witch's Coatee as it ships, and a ray into the body from every torso vertex of Neolithe's Almond XS.
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var coatee = ModelPartReader.Read(VanillaGearRefitLocalTests.TopOf(data, "Witch's Coatee"))!;
        var cloth = new BodyRetarget.ClothCrossings(coatee);
        var faces = new List<(Vector3, Vector3, Vector3)>();
        foreach (var part in coatee.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                faces.Add((At(coatee, part.Triangles[t]), At(coatee, part.Triangles[t + 1]), At(coatee, part.Triangles[t + 2])));
        var mod = BodySizeCatalog.Read(LocalData.Path(LocalData.Mods + ":Neolithe [ALL IN ONE]"));
        var option = mod.For("_top", "0201").First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal("extra chest - smallclothes/nsfw almond xs.mdl"));
        var body = ModelPartReader.Read(File.ReadAllBytes(mod.PathOf(option)))!;
        int rays = 0, hits = 0;
        foreach (int v in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                              .SelectMany(p => p.Triangles).Distinct())
        {
            var p = At(body, v);
            var n = new Vector3(body.Normals[v * 3], body.Normals[v * 3 + 1], body.Normals[v * 3 + 2]);
            if (MathF.Abs(p.X) > 0.12f || p.Y < 0.95f || p.Y > 1.45f || n.LengthSquared() < 1e-12f) continue;
            rays++;
            var walked = cloth.FirstHit(p, -n, BodyRetarget.TuckSeeReach);
            var brute = BruteFirst(faces, p, Vector3.Normalize(-n), BodyRetarget.TuckSeeReach);
            Assert.Equal(brute.HasValue, walked.HasValue);
            if (brute is not { } b) continue;
            hits++;
            Assert.True(MathF.Abs(b.T - walked!.Value.T) <= 1e-6f, $"vertex {v}: walked t {walked.Value.T}, brute {b.T}");
            SameFace(faces, p, -n, b, walked.Value, $"vertex {v}");
        }
        Assert.True(rays > 500 && hits > rays / 2, $"{hits} of {rays} rays hit the Coatee");
    }

    // ── helpers ──

    private const string Cloth_ = SkinTuckSheet.Cloth;

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static SyntheticModel.SurfaceMesh Mesh(Vector3[] pos, int[] tris, Vector3 normal)
        => new(Cloth_, pos, [.. pos.Select(_ => normal)], Uvs(pos), Weights(pos), [(0u, tris)]);

    private static Vector2[] Uvs(IReadOnlyCollection<Vector3> pos) => [.. pos.Select(_ => Vector2.Zero)];

    private static (string Bone, float W)[]?[] Weights(IReadOnlyCollection<Vector3> pos)
        => [.. pos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])];

    /// <summary>A closed box of cloth, 0.3 m a side about the origin, wound and shaded outward: a garment round a body.</summary>
    private static (List<Vector3> Pos, int[] Tris) Panels()
    {
        var pos = new List<Vector3>();
        var tris = new List<int>();
        const float h = 0.15f;
        Vector3[] corners = [new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h), new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h)];
        pos.AddRange(corners);
        int[][] quads = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [2, 3, 7, 6], [1, 2, 6, 5], [0, 4, 7, 3]];
        foreach (var q in quads) tris.AddRange([q[0], q[1], q[2], q[0], q[2], q[3]]);
        return (pos, [.. tris]);
    }

    private static Vector3[] Shaded(List<Vector3> pos, int[] tris)
    {
        var n = new Vector3[pos.Count];
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            var f = Vector3.Cross(pos[tris[t + 1]] - pos[tris[t]], pos[tris[t + 2]] - pos[tris[t]]);
            n[tris[t]] += f; n[tris[t + 1]] += f; n[tris[t + 2]] += f;
        }
        return [.. n.Select(v => Vector3.Normalize(v))];
    }

    private static IEnumerable<(Vector3, Vector3)> Probes()
    {
        var rng = new Random(7);
        for (int k = 0; k < 200; k++)
        {
            var p = new Vector3((float)rng.NextDouble() * 0.2f - 0.1f, (float)rng.NextDouble() * 0.2f - 0.1f, (float)rng.NextDouble() * 0.2f - 0.1f);
            var d = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
            yield return (p, d);
            yield return (p * 4f, d);   // from outside the box too
        }
    }

    private static List<(Vector3, Vector3, Vector3)> Faces(ModelParts m)
    {
        var faces = new List<(Vector3, Vector3, Vector3)>();
        foreach (var part in m.Parts.Where(p => p.Island < 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                faces.Add((At(m, part.Triangles[t]), At(m, part.Triangles[t + 1]), At(m, part.Triangles[t + 2])));
        return faces;
    }

    /// <summary>The walk found the face brute force did — or, where two faces meet the line at the same point, the
    /// other of them, which the line crosses there too.</summary>
    private static void SameFace(List<(Vector3 A, Vector3 B, Vector3 C)> faces, Vector3 o, Vector3 d, (int Index, float T) brute,
                                 (int Index, float T) walked, string what)
    {
        if (brute.Index == walked.Index) return;
        var (a, b, c) = faces[walked.Index];
        Assert.True(Hits(o, Vector3.Normalize(d), a, b, c, out float t) && MathF.Abs(t - brute.T) <= 1e-6f,
                    $"{what}: walked face {walked.Index}, brute force face {brute.Index}");
    }

    private static (int Index, float T)? BruteFirst(List<(Vector3 A, Vector3 B, Vector3 C)> faces, Vector3 o, Vector3 d, float reach)
    {
        (int, float)? best = null;
        for (int i = 0; i < faces.Count; i++)
        {
            var (a, b, c) = faces[i];
            if (Vector3.Cross(b - a, c - a).LengthSquared() == 0f) continue;
            if (Hits(o, d, a, b, c, out float t) && t <= reach && (best == null || t < best.Value.Item2)) best = (i, t);
        }
        return best;
    }

    /// <summary>Möller–Trumbore, both faces, forward of <paramref name="o"/> only — as the production test reads it.</summary>
    private static bool Hits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0f;
    }
}
