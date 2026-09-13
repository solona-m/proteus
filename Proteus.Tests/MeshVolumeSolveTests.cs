using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The brush's geometry, against a mesh built here rather than a <c>.mdl</c>.
/// <para/>
/// <see cref="SyntheticModel"/> deliberately emits isolated triangles that share corner POSITIONS and no
/// indices, which is the right fixture for the island split and the wrong one here: its triangles share no
/// edges, so there is no neighbourhood for the slope limit or the normals to work over. A displacement pass
/// needs a connected surface, so these build one — <see cref="ModelParts"/> is
/// public and its members are init-only, so the solve can be fed directly without a file in the way.
/// </summary>
public class MeshVolumeSolveTests
{
    private const float Spacing = 0.01f;    // 10 mm, so a 20 mm brush reaches a handful of nodes

    /// <summary>
    /// A flat <paramref name="n"/>×<paramref name="n"/> grid in the XZ plane, normals along +Y.
    /// <para/>
    /// Triangulated so every interior edge is used by two faces and only the perimeter is a boundary. That
    /// is the property the rim pin is tested against, and getting the diagonal wrong would silently make the
    /// whole grid a rim.
    /// </summary>
    private static (ModelParts Model, int[,] Index) Grid(int n)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var index = new int[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            index[i, j] = pos.Count / 3;
            pos.Add(i * Spacing); pos.Add(0f); pos.Add(j * Spacing);
            nrm.Add(0f); nrm.Add(1f); nrm.Add(0f);
        }

        var tris = new List<int>();
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            int a = index[i, j], b = index[i + 1, j], c = index[i, j + 1], d = index[i + 1, j + 1];
            tris.AddRange([a, b, d]);
            tris.AddRange([a, d, c]);
        }

        return (Assemble(pos, nrm, tris), index);
    }

    private static ModelParts Assemble(List<float> pos, List<float> nrm, List<int> tris,
                                       int[]? secondPart = null, string secondMaterial = "/mt_test.mtrl")
    {
        var parts = new List<ModelPart> { Part(0, 0, "1.1", tris.ToArray()) };
        if (secondPart != null) parts.Add(Part(0, 1, "1.2", secondPart, secondMaterial));

        return new ModelParts
        {
            Positions = pos.ToArray(),
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, pos.Count / 3)],
            Parts = parts,
            AttributeNames = [],
            Min = Vector3.Zero,
            Max = Vector3.One,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    private static ModelPart Part(int mesh, int submesh, string label, int[] tris,
                                  string material = "/mt_test.mtrl") => new()
    {
        Mesh = mesh,
        Submesh = submesh,
        Island = -1,
        Label = label,
        Material = material,
        Triangles = tris,
        Ordinals = [.. Enumerable.Range(0, tris.Length / 3)],
        AttributeMask = 0,
        Toggleable = true,
        Min = Vector3.Zero,
        Max = Vector3.One,
    };

    private static Vector3 At(float[] positions, int vertex)
        => new(positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);

    /// <summary>
    /// The middle of the brush moves by the full strength, and a point half way out moves by the falloff —
    /// so the shape of the soft selection is the shape of the edit, not an approximation of it.
    /// <para/>
    /// Measured before the stroke ends on purpose. The settle passes are allowed to pull a displacement
    /// back, so asserting exact values after them would be asserting the slope limiter's output rather than
    /// the brush's.
    /// </summary>
    [Fact]
    public void PaintsStrengthAtTheCentreAndTheFalloffFurtherOut()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);

        int middle = index[5, 5];
        var centre = At(model.Positions, middle);

        const float radius = 0.04f, strength = 0.001f;
        Assert.True(solve.Paint(centre, radius, strength) > 0);

        var after = solve.Positions();

        // Dead centre: falloff is 1, so the whole strength lands, straight up the +Y normal.
        Assert.Equal(strength, At(after, middle).Y, 1e-6f);
        Assert.Equal(centre.X, At(after, middle).X, 1e-6f);
        Assert.Equal(centre.Z, At(after, middle).Z, 1e-6f);

        // Two cells out is 20 mm of a 40 mm radius — exactly half way, where smoothstep gives 0.5.
        int half = index[5, 7];
        float expected = strength * MeshVolumeSolve.Falloff(0.5f);
        Assert.Equal(expected, At(after, half).Y, 1e-6f);

        // And nothing outside the radius moved at all.
        Assert.Equal(0f, At(after, index[0, 0]).Y, 1e-9f);
    }

    /// <summary>
    /// Open edges move like everything else.
    /// <para/>
    /// They used to be pinned, to keep a seam shared with another model file from tearing — but a garment's
    /// hem, neckline and sleeve ends are open edges too, and pinning them made the brush refuse to pull cloth
    /// away anywhere near one. This holds the brush to moving them.
    /// </summary>
    [Fact]
    public void MovesOpenEdges()
    {
        var (model, index) = Grid(9);
        var solve = new MeshVolumeSolve(model);

        // Wide enough to cover the whole grid at full weight near the corner it is centred on.
        solve.Paint(At(model.Positions, index[0, 0]), 1f, 0.002f);
        solve.EndStroke();

        var after = solve.Positions();
        for (int k = 0; k < 9; k++)
        {
            Assert.True(At(after, index[0, k]).Y > 0f, $"edge node [0,{k}] did not move");
            Assert.True(At(after, index[k, 0]).Y > 0f, $"edge node [{k},0] did not move");
        }
    }

    /// <summary>
    /// Two vertices at the same position move by the same amount, so a UV seam does not crack open.
    /// <para/>
    /// This is the failure that makes welding non-negotiable rather than tidy: a garment duplicates vertices
    /// along every seam and every hard crease, so the same point on the surface is several vertices with
    /// different neighbours. Displace one copy and not the other and the mesh splits along the seam.
    /// </summary>
    [Fact]
    public void MovesBothCopiesOfASeamVertexTogether()
    {
        var (model, index) = Grid(11);

        // Duplicate one interior vertex and hand the copy to a single triangle, which is precisely what an
        // exporter does at a seam: same place, different index, different set of faces.
        var pos = model.Positions.ToList();
        var nrm = model.Normals.ToList();
        int original = index[5, 5];
        int copy = pos.Count / 3;
        pos.AddRange([model.Positions[original * 3], model.Positions[original * 3 + 1],
                      model.Positions[original * 3 + 2]]);
        nrm.AddRange([0f, 1f, 0f]);

        var tris = model.Parts[0].Triangles.ToArray();
        for (int t = 0; t < tris.Length; t++)
            if (tris[t] == original) { tris[t] = copy; break; }

        var seamed = Assemble(pos, nrm, tris.ToList());
        var solve = new MeshVolumeSolve(seamed);

        solve.Paint(At(seamed.Positions, original), 0.04f, 0.001f);
        solve.EndStroke();

        var after = solve.Positions();
        Assert.Equal(At(after, original).X, At(after, copy).X, 1e-9f);
        Assert.Equal(At(after, original).Y, At(after, copy).Y, 1e-9f);
        Assert.Equal(At(after, original).Z, At(after, copy).Z, 1e-9f);
        Assert.True(At(after, original).Y > 0f);
    }

    /// <summary>
    /// Skin never moves, locked or not — the brush is for pushing clothing clear of the body, and a garment
    /// model routinely carries the body underneath it. Cloth right beside it still moves.
    /// </summary>
    [Fact]
    public void NeverMovesSkin()
    {
        var (model, index) = Grid(11);

        // Every triangle touching the middle node becomes a body-skin part; the rest stays cloth.
        var all = model.Parts[0].Triangles;
        var cloth = new List<int>();
        var body = new List<int>();
        int mid = index[5, 5];
        for (int t = 0; t + 2 < all.Length; t += 3)
        {
            var target = all[t] == mid || all[t + 1] == mid || all[t + 2] == mid ? body : cloth;
            target.AddRange([all[t], all[t + 1], all[t + 2]]);
        }

        var split = Assemble(model.Positions.ToList(), model.Normals.ToList(), cloth, body.ToArray(),
                             "/mt_c0201b0001_b.mtrl");
        Assert.True(SecondSkinWriter.IsBodySkinMaterial("/mt_c0201b0001_b.mtrl"));
        var solve = new MeshVolumeSolve(split);

        solve.Paint(At(split.Positions, mid), 0.04f, 0.001f);
        solve.EndStroke();

        var after = solve.Positions();
        Assert.Equal(Vector3.Zero, At(after, mid) - At(split.Positions, mid));

        // In any direction: cloth beside skin in the same plane is pulled away from it sideways, not up.
        var moved = At(after, index[5, 8]) - At(split.Positions, index[5, 8]);
        Assert.True(moved.Length() > 0f, "cloth inside the brush should still have moved");
    }

    /// <summary>
    /// Cloth pulls AWAY FROM THE SKIN, not along its own normal.
    /// <para/>
    /// The case from game: the hem of a pair of shorts has normals pointing down the leg, and pulling along
    /// them slid the hem down instead of lifting it off the thigh. Here the cloth sits 10 mm above a skin
    /// sheet with its normals deliberately pointing sideways; it must move up, away from the skin.
    /// <para/>
    /// And it must still be there after the stroke ends — the slope limit that used to run on release pulled
    /// most of a pull back, which read in game as the cloth snapping back when the button came up.
    /// </summary>
    [Fact]
    public void PullsAwayFromSkinNotAlongTheNormalAndKeepsItOnRelease()
    {
        const int n = 9;
        var pos = new List<float>();
        var nrm = new List<float>();
        var cloth = new List<int>();
        var body = new List<int>();

        // Two sheets over the same XZ square: skin at y = 0, cloth at y = 10 mm.
        int Sheet(float y, float nx, float ny, List<int> tris)
        {
            int start = pos.Count / 3;
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                pos.AddRange([i * Spacing, y, j * Spacing]);
                nrm.AddRange([nx, ny, 0f]);
            }
            for (int i = 0; i + 1 < n; i++)
            for (int j = 0; j + 1 < n; j++)
            {
                int a = start + i * n + j, b = start + (i + 1) * n + j;
                int c = start + i * n + j + 1, d = start + (i + 1) * n + j + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }
            return start;
        }

        Sheet(0f, 0f, 1f, body);
        int clothStart = Sheet(0.01f, 1f, 0f, cloth);      // normals along +X: the "hem pointing the wrong way"

        var model = Assemble(pos, nrm, cloth, body.ToArray(), "/mt_c0201b0001_bibo.mtrl");
        var solve = new MeshVolumeSolve(model);

        int middle = clothStart + 4 * n + 4;
        solve.Paint(At(model.Positions, middle), 0.2f, 0.004f);
        var during = At(solve.Positions(), middle) - At(model.Positions, middle);
        solve.EndStroke();
        var after = At(solve.Positions(), middle) - At(model.Positions, middle);

        Assert.True(after.Y > 0.003f, $"cloth should lift off the skin, moved {after}");
        Assert.True(MathF.Abs(after.X) < after.Y * 0.2f, $"cloth followed its sideways normal: {after}");

        // Nothing meaningful taken back when the stroke ended. The light smoothing that runs on release may
        // nudge a point, but it is a Taubin pass that does not shrink — unlike the slope limit it replaced,
        // which took back most of the pull.
        Assert.True(after.Y > during.Y * 0.97f, $"the pull snapped back on release: {during.Y} -> {after.Y}");
    }

    /// <summary>
    /// Holding the brush down cannot walk a vertex away without limit.
    /// <para/>
    /// A stroke applies a dab per frame, so on a fast machine a held button is hundreds of dabs a second —
    /// without a ceiling, a moment's inattention turns a garment into a balloon.
    /// </summary>
    [Fact]
    public void CapsTotalDisplacement()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);

        var centre = At(model.Positions, index[5, 5]);
        for (int i = 0; i < 500; i++) solve.Paint(centre, 0.04f, 0.001f);

        Assert.True(solve.Worst <= MeshVolumeSolve.MaxDisplacement + 1e-6f,
                    $"worst {solve.Worst} exceeded the {MeshVolumeSolve.MaxDisplacement} cap");
        Assert.Equal(MeshVolumeSolve.MaxDisplacement, At(solve.Positions(), index[5, 5]).Y, 1e-5f);
    }

    /// <summary>Undo puts a stroke back where it found the surface, not back to flat.</summary>
    [Fact]
    public void UndoReturnsTheSurfaceToWhereTheStrokeFoundIt()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        int middle = index[5, 5];
        var centre = At(model.Positions, middle);

        solve.Paint(centre, 0.04f, 0.001f);
        solve.EndStroke();
        float afterFirst = At(solve.Positions(), middle).Y;
        Assert.True(afterFirst > 0f);

        solve.Paint(centre, 0.04f, 0.001f);
        solve.EndStroke();
        Assert.True(At(solve.Positions(), middle).Y > afterFirst);

        solve.Undo();
        Assert.Equal(afterFirst, At(solve.Positions(), middle).Y, 1e-6f);
        Assert.True(solve.CanUndo);

        solve.Undo();
        Assert.Equal(0f, At(solve.Positions(), middle).Y, 1e-9f);
        Assert.False(solve.CanUndo);
        Assert.False(solve.Dirty);
    }

    /// <summary>Deflate is the same brush with the sign flipped, and it is not clamped differently.</summary>
    [Fact]
    public void DeflatePullsIn()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);
        int middle = index[5, 5];

        solve.Paint(At(model.Positions, middle), 0.04f, -0.001f);
        Assert.Equal(-0.001f, At(solve.Positions(), middle).Y, 1e-6f);
    }

    /// <summary>
    /// A brush smaller than the mesh reaches nothing, and says so by moving nothing — which is what the
    /// panel's warning about the mesh's own resolution exists to explain before the user concludes the tool
    /// is broken.
    /// </summary>
    [Fact]
    public void ReportsReachingNothingWhenTheBrushIsTinierThanTheMesh()
    {
        var (model, index) = Grid(11);
        var solve = new MeshVolumeSolve(model);

        // Between two nodes, with a radius far smaller than the 10 mm spacing.
        var between = At(model.Positions, index[5, 5]) + new Vector3(Spacing * 0.5f, 0f, 0f);
        Assert.Equal(0, solve.Paint(between, 0.001f, 0.001f));
        Assert.False(solve.Dirty);

        // And the mesh's own resolution is reported, so the panel can compare a radius against it.
        Assert.Equal(Spacing, solve.MeanEdge, Spacing * 0.5f);
    }
}
