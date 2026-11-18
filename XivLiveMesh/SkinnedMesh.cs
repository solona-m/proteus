// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Numerics;

namespace XivLiveMesh;

/// <summary>
/// A model's LOD0 geometry in its authored (bind) pose, with what it takes to pose it: which bones move each
/// vertex and how much. Plain arrays, filled by whoever reads the .mdl.
/// </summary>
public sealed class SkinnedMesh
{
    /// <summary>Most influences one vertex carries. The format stores 4 or 8; unused slots have weight 0.</summary>
    public const int MaxInfluences = 8;

    /// <summary>Bind-pose position per vertex, model space.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>
    /// Triangles as vertex indices into <see cref="Positions"/>, three per triangle, with the model's
    /// currently enabled shape keys already applied — a shape rewires index slots to spare vertices, so this
    /// is the surface the game is actually drawing.
    /// </summary>
    public required int[] Triangles { get; init; }

    /// <summary>
    /// For each entry of <see cref="Triangles"/>, the vertex it named BEFORE shapes were applied. The same as
    /// <see cref="Triangles"/> wherever no shape touched the slot. An editor that works on the unshaped
    /// model maps a hit back through this.
    /// </summary>
    public required int[] BaseTriangles { get; init; }

    /// <summary>The model's material paths, indexed by <see cref="TriangleMaterials"/>.</summary>
    public required string[] MaterialNames { get; init; }

    /// <summary>One entry per triangle: the index into <see cref="MaterialNames"/> it draws with.</summary>
    public required ushort[] TriangleMaterials { get; init; }

    /// <summary>The model's bone names, indexed by <see cref="BoneIndices"/>.</summary>
    public required string[] BoneNames { get; init; }

    /// <summary><see cref="MaxInfluences"/> entries per vertex: index into <see cref="BoneNames"/>.</summary>
    public required ushort[] BoneIndices { get; init; }

    /// <summary><see cref="MaxInfluences"/> entries per vertex, each vertex's weights summing to about 1.</summary>
    public required float[] BoneWeights { get; init; }

    /// <summary>The body shape family the model was authored for, as a cXXXX number, or 0 when unknown.</summary>
    public ushort GenderRace { get; init; }

    public int VertexCount => Positions.Length;

    public int TriangleCount => Triangles.Length / 3;

    /// <summary>Check the arrays agree with each other, so a malformed reader fails here and not mid-draw.</summary>
    public void Validate()
    {
        int n = Positions.Length;
        if (BoneIndices.Length != n * MaxInfluences || BoneWeights.Length != n * MaxInfluences)
            throw new ArgumentException("influence arrays must hold MaxInfluences entries per vertex");
        if (Triangles.Length % 3 != 0 || BaseTriangles.Length != Triangles.Length)
            throw new ArgumentException("triangle arrays must be the same length, three per triangle");
        if (TriangleMaterials.Length != Triangles.Length / 3)
            throw new ArgumentException("one material index per triangle");
        foreach (int v in Triangles)
            if ((uint)v >= (uint)n) throw new ArgumentException("triangle names a vertex that does not exist");
        foreach (int v in BaseTriangles)
            if ((uint)v >= (uint)n) throw new ArgumentException("base triangle names a vertex that does not exist");
        foreach (ushort b in BoneIndices)
            if (b >= BoneNames.Length && BoneNames.Length > 0)
                throw new ArgumentException("influence names a bone that does not exist");
    }
}
