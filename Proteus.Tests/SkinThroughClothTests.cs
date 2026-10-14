using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="SkinThroughCloth"/>: a garment's own skin standing outside the cloth over it, counted from the skin's side.
/// A flat skin grid (5 mm cells, 0..40 mm) under a cloth quad 1 mm above it over 10..30 mm.
/// </summary>
public class SkinThroughClothTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e6144_dwn_c.mtrl";

    /// <param name="raise">Lift one skin vertex, at (x, y) in millimetres, to z (millimetres).</param>
    /// <param name="clothPos">The cloth's vertices instead of the one quad, with <paramref name="clothTris"/>.</param>
    private static ModelParts Model((float X, float Y, float Z)? raise = null, bool cloth = true,
                                    Vector3[]? clothPos = null, int[]? clothTris = null)
    {
        const int n = 9;
        var pos = new Vector3[n * n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                var p = new Vector3(c * 0.005f, r * 0.005f, 0f);
                if (raise is { } at && System.MathF.Abs(p.X - at.X / 1000f) < 1e-6f && System.MathF.Abs(p.Y - at.Y / 1000f) < 1e-6f)
                    p.Z = at.Z / 1000f;
                pos[r * n + c] = p;
            }
        var tris = new List<int>();
        for (int r = 0; r + 1 < n; r++)
            for (int c = 0; c + 1 < n; c++)
            {
                int a = r * n + c;
                tris.AddRange([a, a + 1, a + n + 1, a, a + n + 1, a + n]);
            }
        (string, float)[]?[] Bones(int count) => Enumerable.Repeat<(string, float)[]?>([("j_kosi", 1f)], count).ToArray();
        var meshes = new List<SyntheticModel.SurfaceMesh>
        {
            new(Skin, pos, Enumerable.Repeat(Vector3.UnitZ, pos.Length).ToArray(), new Vector2[pos.Length], Bones(pos.Length),
                [(0u, tris.ToArray())]),
        };
        if (cloth)
        {
            Vector3[] quad = clothPos ?? [new(0.010f, 0.010f, 0.001f), new(0.030f, 0.010f, 0.001f),
                                          new(0.030f, 0.030f, 0.001f), new(0.010f, 0.030f, 0.001f)];
            meshes.Add(new(Cloth, quad, Enumerable.Repeat(Vector3.UnitZ, quad.Length).ToArray(), new Vector2[quad.Length],
                           Bones(quad.Length), [(0u, clothTris ?? [0, 1, 2, 0, 2, 3])]));
        }
        return ModelPartReader.Read(SyntheticModel.BuildSurface([], meshes.ToArray()))!;
    }

    [Fact]
    [Trait("Plan", "AC-snap-push-4")]
    public void Flat_skin_under_the_cloth_is_covered_and_not_through()
    {
        var (covered, outside) = SkinThroughCloth.Find(Model());
        Assert.True(covered > 0);
        Assert.Empty(outside);
    }

    [Fact]
    [Trait("Plan", "AC-snap-push-4")]
    public void A_skin_vertex_through_a_face_is_reported_with_its_part_and_depth()
    {
        var model = Model(raise: (20f, 15f, 1.5f));
        var clothLabel = model.Parts.Single(p => p.Island < 0 && p.Material == Cloth).Label;

        var (_, outside) = SkinThroughCloth.Find(model);

        var point = Assert.Single(outside);
        Assert.Equal(clothLabel, point.Over);
        Assert.InRange(point.By, 0.00049f, 0.00051f);
    }

    [Fact]
    [Trait("Plan", "AC-snap-push-4")]
    public void A_skin_vertex_through_an_edge_two_faces_share_still_counts()
    {
        // On the quad's diagonal, which both its triangles use: under the cloth, not past a hem.
        var (_, outside) = SkinThroughCloth.Find(Model(raise: (20f, 20f, 1.5f)));

        Assert.Single(outside);
    }

    [Fact]
    [Trait("Plan", "EDGE-snap-push-6")]
    public void Skin_past_a_hem_is_not_through_the_cloth()
    {
        // Nearest the quad's open edge at x = 30 mm, and above the quad's plane: the body going on past the hem.
        var (_, outside) = SkinThroughCloth.Find(Model(raise: (35f, 20f, 1.5f)));

        Assert.Empty(outside);
    }

    /// <summary>A fan of four cloth faces around V = (20, 10) mm on the cloth's lower hem, 1 mm above the skin; the two
    /// middle faces meet V along edges both share. The skin at (20, 5) raised to 1.5 mm is nearest V itself — past the
    /// hem, whichever face around V the search reads first.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Plan", "EDGE-snap-push-6")]
    public void Skin_past_a_hem_corner_is_not_through_whatever_face_comes_first(bool middleFirst)
    {
        var plain = Model(raise: (20f, 5f, 1.5f), cloth: false);
        var skin = plain.Parts.Single(p => p.Island < 0);
        var skinPos = Enumerable.Range(0, plain.Positions.Length / 3)
                                .Select(i => new Vector3(plain.Positions[i * 3], plain.Positions[i * 3 + 1], plain.Positions[i * 3 + 2]))
                                .ToArray();
        // V, L, R, TL, T, TR.
        Vector3[] fan = [new(0.020f, 0.010f, 0.001f), new(0.010f, 0.010f, 0.001f), new(0.030f, 0.010f, 0.001f),
                         new(0.010f, 0.030f, 0.001f), new(0.020f, 0.030f, 0.001f), new(0.030f, 0.030f, 0.001f)];
        int[] outer = [0, 3, 1, 0, 2, 5];   // V-TL-L, V-R-TR: each with a hem edge at V
        int[] middle = [0, 4, 3, 0, 5, 4];  // V-T-TL, V-TR-T: both edges at V shared
        int[] tris = middleFirst ? [.. middle, .. outer] : [.. outer, .. middle];
        (string, float)[]?[] Bones(int count) => Enumerable.Repeat<(string, float)[]?>([("j_kosi", 1f)], count).ToArray();
        var model = ModelPartReader.Read(SyntheticModel.BuildSurface([],
            new SyntheticModel.SurfaceMesh(Skin, skinPos, Enumerable.Repeat(Vector3.UnitZ, skinPos.Length).ToArray(),
                                           new Vector2[skinPos.Length], Bones(skinPos.Length), [(0u, skin.Triangles)]),
            new SyntheticModel.SurfaceMesh(Cloth, fan, Enumerable.Repeat(Vector3.UnitZ, fan.Length).ToArray(),
                                           new Vector2[fan.Length], Bones(fan.Length), [(0u, tris)])))!;

        var (covered, outside) = SkinThroughCloth.Find(model);

        Assert.True(covered > 0);
        Assert.Empty(outside);
    }

    /// <summary>A 3x3-vertex cloth sheet over 10..30 mm, whose middle vertex (20, 20) is inside the cloth, on no hem.</summary>
    private static (Vector3[] Pos, int[] Tris) Sheet(bool splitSeam = false)
    {
        var pos = new List<Vector3>();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                pos.Add(new Vector3(0.010f + c * 0.010f, 0.010f + r * 0.010f, 0.001f));
        int V(int r, int c) => r * 3 + c;
        var tris = new List<int>();
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                tris.AddRange([V(r, c), V(r, c + 1), V(r + 1, c + 1), V(r, c), V(r + 1, c + 1), V(r + 1, c)]);
        if (splitSeam)
        {
            // The right column of cells on its own copies of the x = 20 mm vertices: a uv seam down the sheet.
            int[] seam = [V(0, 1), V(1, 1), V(2, 1)];
            var copy = new Dictionary<int, int>();
            foreach (int v in seam) { copy[v] = pos.Count; pos.Add(pos[v]); }
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                bool right = Enumerable.Range(0, 3).Any(k => pos[tris[t + k]].X > 0.025f);
                if (!right) continue;
                for (int k = 0; k < 3; k++) if (copy.TryGetValue(tris[t + k], out int c)) tris[t + k] = c;
            }
        }
        return (pos.ToArray(), tris.ToArray());
    }

    [Fact]
    [Trait("Plan", "AC-snap-push-4")]
    public void Skin_through_a_cloth_vertex_inside_the_cloth_counts()
    {
        // Nearest the sheet's middle vertex itself: no blind spot where the cloth's own vertices are.
        var (pos, tris) = Sheet();
        var (_, outside) = SkinThroughCloth.Find(Model(raise: (20f, 20f, 1.5f), clothPos: pos, clothTris: tris));

        var point = Assert.Single(outside);
        Assert.InRange(point.By, 0.00049f, 0.00051f);
    }

    [Fact]
    [Trait("Plan", "EDGE-snap-push-6")]
    public void A_uv_seam_is_not_a_hem()
    {
        // The sheet split down x = 20 mm, as a model splits its vertices along a uv seam: skin through the seam's middle
        // vertex, and through the seam between two of them, is through the cloth all the same.
        var (pos, tris) = Sheet(splitSeam: true);

        Assert.Single(SkinThroughCloth.Find(Model(raise: (20f, 20f, 1.5f), clothPos: pos, clothTris: tris)).Outside);
        Assert.Single(SkinThroughCloth.Find(Model(raise: (20f, 15f, 1.5f), clothPos: pos, clothTris: tris)).Outside);
    }

    [Fact]
    [Trait("Plan", "EDGE-snap-push-7")]
    public void A_model_without_cloth_reports_nothing()
    {
        var (covered, outside) = SkinThroughCloth.Find(Model(raise: (20f, 15f, 1.5f), cloth: false));

        Assert.Equal(0, covered);
        Assert.Empty(outside);
    }
}
