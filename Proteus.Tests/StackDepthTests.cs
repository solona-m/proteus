using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// Geometry passes must run in a bounded stack however large the mesh.
/// <para/>
/// The case that made this necessary: <c>RelaxedNormals</c> did <c>foreach (int n in stackalloc[] { na, nb,
/// nc })</c> inside its per-face loop. A stackalloc is only freed when the method returns, so the frame grew
/// by a few bytes per face — harmless on one mesh, and a stack overflow on the game's 1 MB render thread once
/// the brush fed it a whole model. A stack overflow cannot be caught in .NET, so the game died outright.
/// <para/>
/// Run on a thread with a 256 KB stack, a quarter of what the game gives the render thread, over more than
/// 100,000 faces. Before the fix these kill the test host, which is still an unmistakable failure.
/// </summary>
public class StackDepthTests
{
    private const int StackBytes = 256 * 1024;

    /// <summary>A flat grid of <paramref name="n"/>×<paramref name="n"/> vertices: 2·(n−1)² faces.</summary>
    private static (Vec3[] Pos, int[] Tris) Grid(int n)
    {
        var pos = new Vec3[n * n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            pos[i * n + j] = new Vec3(i * 0.005f, 0f, j * 0.005f);

        var tris = new List<int>(6 * (n - 1) * (n - 1));
        for (int i = 0; i + 1 < n; i++)
        for (int j = 0; j + 1 < n; j++)
        {
            int a = i * n + j, b = (i + 1) * n + j, c = i * n + j + 1, d = (i + 1) * n + j + 1;
            tris.AddRange([a, b, d, a, d, c]);
        }
        return (pos, tris.ToArray());
    }

    private static void OnSmallStack(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { failure = ex; }
        }, StackBytes);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "the pass did not finish");
        if (failure != null) throw new Exception("the pass threw on a small stack", failure);
    }

    [Fact]
    public void RelaxedNormalsRunsInASmallStackOverAHundredThousandFaces()
    {
        var (pos, tris) = Grid(230);                     // 104,882 faces
        Assert.True(tris.Length / 3 >= 100_000);

        int vc = pos.Length;
        var nrm = Enumerable.Repeat(new Vec3(0f, 1f, 0f), vc).ToArray();
        var delta = new Vec3[vc];
        var nodeOf = Enumerable.Range(0, vc).ToArray();
        var weight = Enumerable.Repeat(1f, vc).ToArray();   // every node touched, so the smoothing runs too

        Vec3[]? result = null;
        OnSmallStack(() => result = SecondSkinWriter.RelaxedNormals(pos, nrm, delta, nodeOf, weight, nrm, tris));

        Assert.NotNull(result);
        Assert.Equal(vc, result!.Length);
    }

    /// <summary>
    /// The path that actually crashed: a brush stroke over a whole model, finished, on a small stack.
    /// </summary>
    [Fact]
    public void ABrushStrokeOverALargeModelRunsInASmallStack()
    {
        var (pos, tris) = Grid(230);
        var flat = pos.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
        var normals = pos.SelectMany(_ => new[] { 0f, 1f, 0f }).ToArray();

        var model = new ModelParts
        {
            Positions = flat,
            Normals = normals,
            MeshSpans = [new MeshSpan(0, 0, pos.Length)],
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "1.1", Material = "/mt_test.mtrl",
                    Triangles = tris, Ordinals = [.. Enumerable.Range(0, tris.Length / 3)],
                    AttributeMask = 0, Toggleable = true, Min = Vector3.Zero, Max = Vector3.One,
                },
            ],
            AttributeNames = [],
            Min = Vector3.Zero,
            Max = Vector3.One,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };

        float worst = 0f;
        int relaxed = 0;
        OnSmallStack(() =>
        {
            var solve = new MeshVolumeSolve(model);
            solve.Paint(new Vector3(0.57f, 0f, 0.57f), 2f, 0.002f);   // covers the whole grid
            solve.EndStroke();
            worst = solve.Worst;

            // The relax and bridge brushes' per-dab loops over the whole model, on the same small stack.
            relaxed = solve.Relax(new Vector3(0.57f, 0f, 0.57f), 2f, 1f);
            solve.EndStroke();
            solve.Bridge(new Vector3(0.57f, 0f, 0.57f), 2f, 1f, Vector3.UnitY);
            solve.EndStroke(bridge: true);
        });

        Assert.True(worst > 0f);
        Assert.True(relaxed > 100_000 / 2);
    }
}
