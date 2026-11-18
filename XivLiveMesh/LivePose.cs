// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace XivLiveMesh;

/// <summary>
/// A character's skeleton as it stands this frame: every bone's current and reference transform in the
/// character's model space, and the transform from model space into the world.
/// <para/>
/// A snapshot, copied out of the game in one go, so posing a mesh never reads a skeleton the game is halfway
/// through updating. Reused between frames: <see cref="Read"/> refills the same arrays.
/// </summary>
public sealed class LivePose
{
    private readonly Dictionary<string, int> index = new(StringComparer.Ordinal);
    private readonly List<string> names = [];
    private readonly List<int> parents = [];
    private Matrix4x4[] pose = [];
    private Matrix4x4[] bind = [];

    /// <summary>Model space to world space: the skeleton's own transform, which carries position, facing and
    /// the character's height scale.</summary>
    public Matrix4x4 Root { get; private set; } = Matrix4x4.Identity;

    /// <summary>The character's body shape family as a cXXXX number (101 for c0101), 0 when not a human.</summary>
    public ushort GenderRace { get; private set; }

    public int BoneCount => names.Count;

    public bool TryGetBone(string name, out int bone) => index.TryGetValue(name, out bone);

    public string BoneName(int bone) => names[bone];

    /// <summary>The bone's parent, or null for a root bone.</summary>
    public string? ParentOf(string name)
        => index.TryGetValue(name, out var i) && parents[i] >= 0 ? names[parents[i]] : null;

    /// <summary>Bone <paramref name="bone"/>'s current transform in model space.</summary>
    public ref readonly Matrix4x4 Pose(int bone) => ref pose[bone];

    /// <summary>Bone <paramref name="bone"/>'s reference (bind) transform in model space.</summary>
    public ref readonly Matrix4x4 Bind(int bone) => ref bind[bone];

    /// <summary>
    /// Fill the snapshot from arrays rather than from the game — for tests, and for tools that pose a mesh
    /// offline. Names must be unique; a parent index of -1 marks a root.
    /// </summary>
    public void Set(IReadOnlyList<string> boneNames, IReadOnlyList<int> parentIndices, IReadOnlyList<Matrix4x4> bindPose,
                    IReadOnlyList<Matrix4x4> currentPose, Matrix4x4 root, ushort genderRace)
    {
        index.Clear();
        names.Clear();
        parents.Clear();
        int n = boneNames.Count;
        pose = new Matrix4x4[n];
        bind = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            index[boneNames[i]] = i;
            names.Add(boneNames[i]);
            parents.Add(parentIndices[i]);
            bind[i] = bindPose[i];
            pose[i] = currentPose[i];
        }
        Root = root;
        GenderRace = genderRace;
    }

    /// <summary>
    /// Refill the snapshot from a live character. Must be called on the game's main thread — the framework
    /// update or the UI draw — while <paramref name="character"/> is known to be alive.
    /// </summary>
    /// <returns>False when the character has no skeleton to read (mid-redraw, not loaded yet).</returns>
    public unsafe bool Read(CharacterBase* character)
    {
        index.Clear();
        names.Clear();
        parents.Clear();
        if (character == null) return false;

        var skeleton = character->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount == 0)
            return false;

        var t = skeleton->Transform;
        Root = Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation)
             * Matrix4x4.CreateTranslation(t.Position);

        GenderRace = character->GetModelType() == CharacterBase.ModelType.Human
            ? ((Human*)character)->RaceSexId
            : (ushort)0;

        int total = 0;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var hk = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (hk != null && hk->Skeleton != null) total += hk->Skeleton->Bones.Length;
        }
        if (pose.Length < total) { pose = new Matrix4x4[total]; bind = new Matrix4x4[total]; }

        // Every partial skeleton — body, face, hair, tail — into one flat list. A name two partials share keeps
        // its first entry: the body's, which is the one gear is weighted to.
        Span<int> localToFlat = stackalloc int[1024];
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var hk = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (hk == null || hk->Skeleton == null) continue;
            var hs = hk->Skeleton;
            int n = Math.Min(hs->Bones.Length, Math.Min(hk->ModelPose.Length, hs->ReferencePose.Length));
            if (n > localToFlat.Length) n = localToFlat.Length;

            for (int b = 0; b < n; b++)
            {
                var name = hs->Bones[b].Name.String;
                int parentLocal = b < hs->ParentIndices.Length ? hs->ParentIndices[b] : -1;

                // Reference pose is stored per bone relative to its parent; accumulate into model space.
                var local = ToMatrix(hs->ReferencePose[b]);
                var model = parentLocal >= 0 && parentLocal < b ? local * bind[localToFlat[parentLocal]] : local;

                if (string.IsNullOrEmpty(name) || index.ContainsKey(name))
                {
                    // Still needs a flat slot so its children can accumulate through it.
                    int dup = names.Count;
                    names.Add(name ?? "");
                    parents.Add(parentLocal >= 0 && parentLocal < b ? localToFlat[parentLocal] : -1);
                    bind[dup] = model;
                    pose[dup] = ToMatrix(hk->ModelPose[b]);
                    localToFlat[b] = dup;
                    continue;
                }

                int flat = names.Count;
                names.Add(name);
                parents.Add(parentLocal >= 0 && parentLocal < b ? localToFlat[parentLocal] : -1);
                index[name] = flat;
                bind[flat] = model;
                pose[flat] = ToMatrix(hk->ModelPose[b]);
                localToFlat[b] = flat;
            }
        }
        return names.Count > 0;
    }

    /// <summary>A Havok scale-rotation-translation as a System.Numerics matrix (row vectors: scale first).</summary>
    public static Matrix4x4 ToMatrix(in hkQsTransformf t)
        => Matrix4x4.CreateScale(t.Scale.X, t.Scale.Y, t.Scale.Z)
         * Matrix4x4.CreateFromQuaternion(new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W))
         * Matrix4x4.CreateTranslation(t.Translation.X, t.Translation.Y, t.Translation.Z);
}
