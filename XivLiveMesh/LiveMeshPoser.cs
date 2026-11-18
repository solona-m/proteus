// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Numerics;

namespace XivLiveMesh;

/// <summary>
/// Poses a <see cref="SkinnedMesh"/> onto a <see cref="LivePose"/>: where each vertex is in the world this
/// frame. Linear blend skinning, the same as the game's vertex shader, preceded by the racial deformer.
/// <para/>
/// Holds its working buffers between calls, so posing every frame allocates nothing once warm.
/// </summary>
public sealed class LiveMeshPoser
{
    private Matrix4x4[] skin = [];
    private readonly Dictionary<(ushort, ushort), IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>>> chains = [];

    // Each bone's deformation resolved through its chain, per (model race, character race). The skeleton's
    // hierarchy the fallback walks does not change between frames, so neither does the answer.
    private readonly Dictionary<(ushort, ushort), Dictionary<string, Matrix4x4>> resolved = [];

    /// <summary>Apply the racial deformer. Off only to compare against, when checking alignment.</summary>
    public bool ApplyDeformer { get; set; } = true;

    /// <summary>How many of the last mesh's bones the skeleton did not have; their vertices only follow the root.</summary>
    public int MissingBones { get; private set; }

    /// <summary>How many deformers the last call applied (0 when the model was authored for this body).</summary>
    public int DeformerSteps { get; private set; }

    /// <summary>
    /// Pose every vertex of <paramref name="mesh"/> into <paramref name="world"/>, which must hold at least
    /// <see cref="SkinnedMesh.VertexCount"/> entries.
    /// </summary>
    /// <param name="pbd">The racial deformer file, or null to skip deformation.</param>
    /// <param name="modelGenderRace">The race the model was authored for, overriding the mesh's own when non-zero.</param>
    /// <param name="positions">Bind-pose positions to pose instead of the mesh's own — an edited copy, same
    /// order and count. Null poses <see cref="SkinnedMesh.Positions"/>.</param>
    public void Pose(SkinnedMesh mesh, LivePose pose, PbdFile? pbd, Vector3[] world, ushort modelGenderRace = 0,
                     Vector3[]? positions = null)
    {
        if (world.Length < mesh.VertexCount) throw new ArgumentException("output buffer too small", nameof(world));
        if (positions != null && positions.Length < mesh.VertexCount)
            throw new ArgumentException("positions buffer too small", nameof(positions));
        BuildSkin(mesh, pose, pbd, modelGenderRace != 0 ? modelGenderRace : mesh.GenderRace);

        var root = pose.Root;
        positions ??= mesh.Positions;
        var indices = mesh.BoneIndices;
        var weights = mesh.BoneWeights;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var p = positions[v];
            Vector3 sum = default;
            float total = 0f;
            int o = v * SkinnedMesh.MaxInfluences;
            for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
            {
                float w = weights[o + k];
                if (w <= 0f) continue;
                sum += Vector3.Transform(p, skin[indices[o + k]]) * w;
                total += w;
            }
            world[v] = total > 1e-6f ? sum / total : Vector3.Transform(p, root);
        }
    }

    /// <summary>
    /// The matrix that took vertex <paramref name="vertex"/> from bind pose to the world in the last
    /// <see cref="Pose"/> call, its bones blended by weight. Inverted, it carries a world direction back into the
    /// model's own space at that point — which way the camera is, as the model sees it.
    /// </summary>
    public Matrix4x4 SkinAt(SkinnedMesh mesh, int vertex, Matrix4x4 root)
    {
        int o = vertex * SkinnedMesh.MaxInfluences;
        Matrix4x4 sum = default;
        float total = 0f;
        for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
        {
            float w = mesh.BoneWeights[o + k];
            if (w <= 0f) continue;
            sum += skin[mesh.BoneIndices[o + k]] * w;
            total += w;
        }
        return total > 1e-6f ? sum * (1f / total) : root;
    }

    /// <summary>
    /// Build one matrix per mesh bone taking a bind-pose vertex to the world: racial deformation, then out of
    /// the bone's reference pose, into its current pose, and into the world.
    /// </summary>
    private void BuildSkin(SkinnedMesh mesh, LivePose pose, PbdFile? pbd, ushort from)
    {
        if (skin.Length < mesh.BoneNames.Length) skin = new Matrix4x4[mesh.BoneNames.Length];

        IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain = [];
        Dictionary<string, Matrix4x4>? resolved = null;
        if (ApplyDeformer && pbd != null && from != 0 && pose.GenderRace != 0 && from != pose.GenderRace)
        {
            var key = (from, pose.GenderRace);
            if (!chains.TryGetValue(key, out chain!))
                chains[key] = chain = pbd.Chain(from, pose.GenderRace);
            if (!this.resolved.TryGetValue(key, out resolved))
                this.resolved[key] = resolved = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        }
        DeformerSteps = chain.Count;

        var root = pose.Root;
        int missing = 0;
        for (int j = 0; j < mesh.BoneNames.Length; j++)
        {
            var name = mesh.BoneNames[j];
            var deform = Matrix4x4.Identity;
            if (chain.Count > 0 && resolved != null && !resolved.TryGetValue(name, out deform))
                resolved[name] = deform = PbdFile.Resolve(chain, name, pose.ParentOf);
            if (pose.TryGetBone(name, out int b) && Matrix4x4.Invert(pose.Bind(b), out var unbind))
                skin[j] = deform * unbind * pose.Pose(b) * root;
            else
            {
                skin[j] = deform * root;
                missing++;
            }
        }
        MissingBones = missing;
    }
}
