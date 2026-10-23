using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What cloth rides when "Move skin too" moves the skin, and where the skin's open edges hold — over sheets built here.
/// </summary>
public class SkinFollowTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e6255_top_a.mtrl";
    private const float Spacing = 0.01f;

    /// <summary>Sheets of quads in planes y = height, each with its normal up or down, as parts of one model.</summary>
    private static (ModelParts Model, List<int[,]> Sheets) Sheets(int n, params (float Y, bool Up, string Material, int SkipEvery)[] layers)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var parts = new List<ModelPart>();
        var sheets = new List<int[,]>();
        foreach (var (y, up, material, skip) in layers)
        {
            var at = new int[n, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                at[i, j] = pos.Count / 3;
                pos.AddRange([i * Spacing, y, j * Spacing]);
                nrm.AddRange([0f, up ? 1f : -1f, 0f]);
            }
            var tris = new List<int>();
            for (int i = 0; i + 1 < n; i++)
            for (int j = 0; j + 1 < n; j++)
            {
                if (skip > 0 && i >= skip && i < n - 1 - skip && j >= skip && j < n - 1 - skip) continue;   // a hole
                int a = at[i, j], b = at[i + 1, j], c = at[i, j + 1], d = at[i + 1, j + 1];
                if (up) tris.AddRange([a, d, b, a, c, d]);
                else tris.AddRange([a, b, d, a, d, c]);
            }
            parts.Add(new ModelPart
            {
                Mesh = 0, Submesh = parts.Count, Island = -1, Label = $"1.{parts.Count + 1}", Material = material,
                Triangles = tris.ToArray(), Ordinals = [.. Enumerable.Range(0, tris.Count / 3)], AttributeMask = 0,
                Toggleable = true, Min = Vector3.Zero, Max = Vector3.One,
            });
            sheets.Add(at);
        }
        var model = new ModelParts
        {
            Positions = pos.ToArray(), Normals = nrm.ToArray(), MeshSpans = [new MeshSpan(0, 0, pos.Count / 3)], Parts = parts,
            AttributeNames = [], Min = Vector3.Zero, Max = Vector3.One, ShatteredSubmeshes = new Dictionary<string, int>(),
        };
        return (model, sheets);
    }

    private static (SkinFollow Follow, int[] NodeOf) Bind(ModelParts model)
    {
        var solve = new MeshVolumeSolve(model);
        var inputs = solve.FollowInputs;
        return (SkinFollow.Bind(inputs), inputs.NodeOf);
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void BindsToNearestSkinTriangleWithBarycentrics()
    {
        // Cloth offset half a cell in x and a quarter in z, 5 mm up: it lies over the inside of a skin triangle.
        var (model, sheets) = Sheets(5, (0f, true, Skin, 0), (0.005f, true, Cloth, 0));
        var pos = model.Positions.ToArray();
        foreach (int v in sheets[1]) { pos[v * 3] += 0.005f; pos[v * 3 + 2] += 0.0025f; }
        var shifted = new ModelParts
        {
            Positions = pos, Normals = model.Normals, MeshSpans = model.MeshSpans, Parts = model.Parts,
            AttributeNames = [], Min = Vector3.Zero, Max = Vector3.One, ShatteredSubmeshes = new Dictionary<string, int>(),
        };
        var (follow, nodeOf) = Bind(shifted);

        int rider = nodeOf[sheets[1][1, 1]];
        Assert.Equal(1f, follow.BindWeight(rider));
        Assert.Equal(0.005f, follow.AuthoredHeight(rider), 1e-5f);
        var corners = follow.CornersOf(rider);
        Assert.Equal(3, corners.Count);
        Assert.Equal(1f, corners.Sum(c => c.Share), 1e-5f);
        // The shares put the landing straight under the cloth point.
        var landing = corners.Aggregate(Vector3.Zero, (s, c) => s + c.Share * NodePos(shifted, nodeOf, c.Skin));
        Assert.Equal(pos[sheets[1][1, 1] * 3], landing.X, 1e-5f);
        Assert.Equal(pos[sheets[1][1, 1] * 3 + 2], landing.Z, 1e-5f);
        Assert.Contains(follow.RidersOf(corners[0].Skin), r => r.Rider == rider);
    }

    private static Vector3 NodePos(ModelParts m, int[] nodeOf, int node)
    {
        int v = System.Array.IndexOf(nodeOf, node);
        return new Vector3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
    }

    [Theory]
    [InlineData(0.005f, 1f)]
    [InlineData(0.010f, 1f)]
    [InlineData(0.020f, 0.5f)]
    [InlineData(0.030f, 0f)]
    [InlineData(0.040f, 0f)]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void FollowWeightFadesBetween10And30mm(float distance, float expected)
        => Assert.Equal(expected, SkinFollow.FollowWeight(distance), 1e-5f);

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-9")]
    public void SeamWeightIsZeroOnOpenEdgesAndOneBeyondThirtyMm()
    {
        var (model, sheets) = Sheets(11, (0f, true, Skin, 0));
        var solve = new MeshVolumeSolve(model);
        var skin = sheets[0];
        Assert.Equal(0f, solve.SeamWeightAt(skin[0, 5]));
        Assert.Equal(0f, solve.SeamWeightAt(skin[10, 10]));
        Assert.Equal(1f, solve.SeamWeightAt(skin[5, 5]));
        Assert.Equal(1f - MeshVolumeSolve.Falloff(1f / 3f), solve.SeamWeightAt(skin[1, 5]), 1e-4f);
    }

    /// <summary>Skin welded to cloth carries on into it, so that edge is not open; a hole cut in the skin is.</summary>
    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-19")]
    public void SkinWeldedToClothIsNotAnOpenEdgeButAHoleRimIs()
    {
        // One flat sheet: the left half skin, the right half cloth, welded down the middle; a hole in the skin.
        const int n = 21;
        var pos = new List<float>();
        var nrm = new List<float>();
        var at = new int[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            at[i, j] = pos.Count / 3;
            pos.AddRange([i * Spacing, 0f, j * Spacing]);
            nrm.AddRange([0f, 1f, 0f]);
        }
        var skin = new List<int>();
        var cloth = new List<int>();
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            if (i is >= 4 and < 6 && j is >= 9 and < 11) continue;   // the hole, in the skin half
            int a = at[i, j], b = at[i + 1, j], c = at[i, j + 1], d = at[i + 1, j + 1];
            (i < 10 ? skin : cloth).AddRange([a, d, b, a, c, d]);
        }
        var model = new ModelParts
        {
            Positions = pos.ToArray(), Normals = nrm.ToArray(), MeshSpans = [new MeshSpan(0, 0, pos.Count / 3)],
            Parts =
            [
                new ModelPart { Mesh = 0, Submesh = 0, Island = -1, Label = "1.1", Material = Skin, Triangles = skin.ToArray(),
                                Ordinals = [.. Enumerable.Range(0, skin.Count / 3)], AttributeMask = 0, Toggleable = true,
                                Min = Vector3.Zero, Max = Vector3.One },
                new ModelPart { Mesh = 0, Submesh = 1, Island = -1, Label = "1.2", Material = Cloth, Triangles = cloth.ToArray(),
                                Ordinals = [.. Enumerable.Range(0, cloth.Count / 3)], AttributeMask = 0, Toggleable = true,
                                Min = Vector3.Zero, Max = Vector3.One },
            ],
            AttributeNames = [], Min = Vector3.Zero, Max = Vector3.One, ShatteredSubmeshes = new Dictionary<string, int>(),
        };
        var solve = new MeshVolumeSolve(model);
        Assert.Equal(1f, solve.SeamWeightAt(at[10, 15]));   // the weld: skin carries on as cloth
        Assert.Equal(0f, solve.SeamWeightAt(at[4, 10]));     // the hole's rim
    }

    /// <summary>
    /// Two limbs side by side: skin A facing up at y = 0, skin B facing down at y = 15 mm (an inner thigh against the
    /// other). Cloth hugging A binds to A, cloth hugging B to B; cloth behind B's face — inside that limb — binds to
    /// nothing rather than through it.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-26")]
    public void ClothBindsToTheLimbItHugs()
    {
        var (model, sheets) = Sheets(5,
            (0f, true, Skin, 0), (0.015f, false, Skin, 0),
            (0.003f, true, Cloth, 0), (0.012f, false, Cloth, 0), (0.022f, true, Cloth, 0));
        var (follow, nodeOf) = Bind(model);

        static bool On(SkinFollow f, int[] nodeOf, int node, int[,] limb)
            => f.CornersOf(node).All(c => limb.Cast<int>().Any(v => nodeOf[v] == c.Skin));

        int nearA = nodeOf[sheets[2][2, 2]], nearB = nodeOf[sheets[3][2, 2]], behindB = nodeOf[sheets[4][2, 2]];
        Assert.True(On(follow, nodeOf, nearA, sheets[0]));
        Assert.True(On(follow, nodeOf, nearB, sheets[1]));
        Assert.Equal(0f, follow.BindWeight(behindB));
        Assert.Empty(follow.CornersOf(behindB));
    }
}
