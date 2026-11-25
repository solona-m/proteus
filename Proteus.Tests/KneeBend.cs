using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Proteus.Interop;
using Proteus.Services;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// Real poses for the knee standoff's measure: the owner's live pose dump (<c>/proteus posedump</c> or the harness's
/// <c>posedump</c>), read through <see cref="LivePoseDump.Load"/> with the root set to identity, its legs mirrored, and
/// knee bends scaled from it (only the knee bones; the hip held as dumped). Models are posed by CPU skinning
/// (<see cref="LiveMeshPoser"/>), vertex for vertex in <see cref="ModelPartReader"/>'s order.
/// </summary>
internal static class KneeBend
{
    /// <summary>The knee bones scaled by a bend; everything else keeps its dumped values.</summary>
    internal static readonly string[] KneeBones = ["j_asi_b", "j_asi_c"];

    /// <summary>Read a dump's <c>pose.json</c> as a pose with the root at identity (model space).</summary>
    internal static LivePose Load(string path)
    {
        var snapshot = JsonSerializer.Deserialize<LivePoseDump.Snapshot>(File.ReadAllText(path))
                       ?? throw new InvalidDataException($"{path} is not a pose dump");
        var dumped = LivePoseDump.Load(snapshot);
        return Rebuild(dumped, b => dumped.Pose(b), b => dumped.Bind(b), dumped.GenderRace);
    }

    /// <summary>A pose with the same bones, each bone's model-space pose and bind given by <paramref name="poseOf"/> and
    /// <paramref name="bindOf"/>, the root at identity.</summary>
    private static LivePose Rebuild(LivePose p, Func<int, Matrix4x4> poseOf, Func<int, Matrix4x4> bindOf, ushort genderRace)
    {
        int n = p.BoneCount;
        var names = Enumerable.Range(0, n).Select(p.BoneName).ToList();
        var parents = names.Select(name => p.ParentOf(name) is { } parent && p.TryGetBone(parent, out var pi) ? pi : -1).ToList();
        var r = new LivePose();
        r.Set(names, parents, Enumerable.Range(0, n).Select(bindOf).ToList(),
              Enumerable.Range(0, n).Select(poseOf).ToList(), Matrix4x4.Identity, genderRace);
        return r;
    }

    private static Vector3 At(in Matrix4x4 m) => m.Translation;

    /// <summary>The knee's flexion (degrees) in a model-space set of matrices: the angle between the thigh
    /// (<c>j_asi_a</c>→<c>j_asi_b</c>) and the shin (<c>j_asi_c</c>→<c>j_asi_d</c>).</summary>
    private static float Flexion(LivePose p, string side, bool bind)
    {
        Matrix4x4 M(string bone) => p.TryGetBone($"{bone}_{side}", out var b) ? (bind ? p.Bind(b) : p.Pose(b))
                                                                              : throw new InvalidDataException($"no {bone}_{side}");
        var thigh = At(M("j_asi_b")) - At(M("j_asi_a"));
        var shin = At(M("j_asi_d")) - At(M("j_asi_c"));
        float cos = Vector3.Dot(Vector3.Normalize(thigh), Vector3.Normalize(shin));
        return MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>A bone's rotation from bind to pose (degrees), in its parent's frame.</summary>
    private static float DeltaDegrees(LivePose p, string bone)
    {
        var (bindLocal, poseLocal) = Locals(p, bone);
        Matrix4x4.Decompose(bindLocal, out _, out var qb, out _);
        Matrix4x4.Decompose(poseLocal, out _, out var qp, out _);
        var d = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Conjugate(qb), qp));
        return 2f * MathF.Acos(Math.Clamp(MathF.Abs(d.W), 0f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>The leg's angles: knee flexion at bind and pose (and their difference, the flexion added beyond bind), and
    /// the hip's and knee bones' rotations from bind.</summary>
    internal sealed record LegAngles(float KneeBind, float KneePose, float Added, float Hip, float KneeB, float KneeC);

    internal static LegAngles Angles(LivePose p, string side)
    {
        float bind = Flexion(p, side, true), pose = Flexion(p, side, false);
        return new LegAngles(bind, pose, pose - bind, DeltaDegrees(p, $"j_asi_a_{side}"),
                             DeltaDegrees(p, $"j_asi_b_{side}"), DeltaDegrees(p, $"j_asi_c_{side}"));
    }

    /// <summary>The bone's local (parent-relative) bind and pose matrices.</summary>
    private static (Matrix4x4 Bind, Matrix4x4 Pose) Locals(LivePose p, string bone)
    {
        if (!p.TryGetBone(bone, out var b)) throw new InvalidDataException($"no {bone}");
        if (p.ParentOf(bone) is not { } parent || !p.TryGetBone(parent, out var pi)) return (p.Bind(b), p.Pose(b));
        Matrix4x4.Invert(p.Bind(pi), out var ib);
        Matrix4x4.Invert(p.Pose(pi), out var ip);
        return (p.Bind(b) * ib, p.Pose(b) * ip);
    }

    private static readonly Matrix4x4 MirrorX = Matrix4x4.CreateScale(-1f, 1f, 1f);

    private static string OtherSide(string name)
        => name.EndsWith("_l", StringComparison.Ordinal) ? name[..^2] + "_r"
         : name.EndsWith("_r", StringComparison.Ordinal) ? name[..^2] + "_l"
         : name;

    /// <summary>The pose mirrored left to right: every bone takes its other side's pose and bind, reflected in x (skinning
    /// is bind⁻¹·pose, so a bone's bind goes with its pose: a left bone then moves its vertices as the mirror of the right).</summary>
    internal static LivePose Mirrored(LivePose p)
    {
        int Src(int b) => p.TryGetBone(OtherSide(p.BoneName(b)), out var o) ? o : b;
        return Rebuild(p, b => MirrorX * p.Pose(Src(b)) * MirrorX, b => MirrorX * p.Bind(Src(b)) * MirrorX, p.GenderRace);
    }

    /// <summary>Every bone below <paramref name="root"/> (itself included).</summary>
    private static HashSet<int> Subtree(LivePose p, string root)
    {
        var set = new HashSet<int>();
        for (int b = 0; b < p.BoneCount; b++)
        {
            for (string? n = p.BoneName(b); n != null; n = p.ParentOf(n))
                if (n == root) { set.Add(b); break; }
        }
        return set;
    }

    /// <summary>
    /// The dump with <paramref name="addedDegrees"/> of knee flexion beyond bind on both legs: the right leg's (the more
    /// flexed one in the owner's dump) knee bones' rotations from bind scaled by k = θ / θ_dump(right), keeping their
    /// axes and split; the hip and every other bone as dumped; the left leg replaced with the mirror of the result.
    /// </summary>
    internal static LivePose Scaled(LivePose p, float addedDegrees, string baseSide = "r")
    {
        var dumped = Angles(p, baseSide);
        // A leg barely flexed beyond bind gives no axis to scale (k would run away or flip sign).
        if (!(dumped.Added >= 1f))
            throw new InvalidDataException($"the {baseSide} knee is only {dumped.Added:F1}° beyond bind in this dump: nothing to scale");
        float k = addedDegrees / dumped.Added;
        // Locals of every bone as posed; the knee bones' rotations scaled.
        var local = new Matrix4x4[p.BoneCount];
        for (int b = 0; b < p.BoneCount; b++) local[b] = Locals(p, p.BoneName(b)).Pose;
        foreach (var bone in KneeBones)
        {
            string name = $"{bone}_{baseSide}";
            var (bindLocal, poseLocal) = Locals(p, name);
            Matrix4x4.Decompose(bindLocal, out _, out var qb, out _);
            Matrix4x4.Decompose(poseLocal, out var scale, out var qp, out var translation);
            // Row vectors: the pose's rotation is the delta first, then bind's (qp = qd then qb).
            var qd = Quaternion.Normalize(Quaternion.Concatenate(qp, Quaternion.Conjugate(qb)));
            if (qd.W < 0) qd = -qd;
            float angle = 2f * MathF.Acos(Math.Clamp(qd.W, -1f, 1f));
            var axis = angle > 1e-6f ? Vector3.Normalize(new Vector3(qd.X, qd.Y, qd.Z)) : Vector3.UnitX;
            var qs = Quaternion.CreateFromAxisAngle(axis, angle * k);
            var q = Quaternion.Normalize(Quaternion.Concatenate(qs, qb));
            p.TryGetBone(name, out var bi);
            local[bi] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(translation);
        }
        // Model space down the chain (parents come before children in a Havok skeleton; walked to be sure).
        var model = new Matrix4x4?[p.BoneCount];
        Matrix4x4 Model(int b)
        {
            if (model[b] is { } m) return m;
            string name = p.BoneName(b);
            var r = p.ParentOf(name) is { } parent && p.TryGetBone(parent, out var pi) ? local[b] * Model(pi) : local[b];
            model[b] = r;
            return r;
        }
        var right = Subtree(p, $"j_asi_a_{baseSide}");
        string other = baseSide == "r" ? "l" : "r";
        var left = Subtree(p, $"j_asi_a_{other}");
        int? Mirror(int b) => left.Contains(b) && p.TryGetBone(OtherSide(p.BoneName(b)), out var src) && right.Contains(src) ? src : null;
        return Rebuild(p, b => Mirror(b) is { } src ? MirrorX * Model(src) * MirrorX : Model(b),
                          b => Mirror(b) is { } src ? MirrorX * p.Bind(src) * MirrorX : p.Bind(b), p.GenderRace);
    }

    /// <summary>Mesh bones a dump may lack, followed by the bone they hang from (IVCS and physics bones of body mods).</summary>
    internal static readonly (string Prefix, string Parent)[] Fallback =
    [
        ("iv_asi_", "j_asi_e"), ("ya_daitai_phys", "j_asi_a"), ("iv_daitai_phys", "j_asi_a"), ("iv_shiri", "j_kosi"),
        ("iv_hiza", "j_asi_b"), ("iv_sune", "j_asi_c"), ("iv_kobusi", "j_asi_c"),
    ];

    /// <summary>A model posed onto <paramref name="pose"/>: each vertex's posed position, in <see cref="ModelPartReader"/>'s
    /// order, and the mesh bones the pose lacked that no fallback named (their vertices follow only the root).</summary>
    internal static (Vector3[] Posed, IReadOnlyList<string> Unmapped) Posed(byte[] mdl, LivePose pose)
    {
        var mesh = ModelSkinReader.Read(mdl, null, null) ?? throw new InvalidDataException("the model cannot be skinned");
        var missing = mesh.BoneNames.Where(n => !pose.TryGetBone(n, out _)).Distinct().ToList();
        var unmapped = new List<string>();
        var use = pose;
        if (missing.Count > 0)
        {
            // Add each missing bone with its fallback parent's transforms: it then follows that bone exactly.
            int n = pose.BoneCount;
            var names = Enumerable.Range(0, n).Select(pose.BoneName).ToList();
            var parents = names.Select(name => pose.ParentOf(name) is { } pa && pose.TryGetBone(pa, out var pi) ? pi : -1).ToList();
            var binds = Enumerable.Range(0, n).Select(b => pose.Bind(b)).ToList();
            var poses = Enumerable.Range(0, n).Select(b => pose.Pose(b)).ToList();
            foreach (var bone in missing)
            {
                string side = bone.EndsWith("_l") ? "_l" : bone.EndsWith("_r") ? "_r" : "";
                var map = Fallback.FirstOrDefault(f => bone.StartsWith(f.Prefix, StringComparison.Ordinal));
                if (map.Parent == null || !pose.TryGetBone(map.Parent + side, out var parent) && !pose.TryGetBone(map.Parent, out parent))
                {
                    unmapped.Add(bone);
                    continue;
                }
                names.Add(bone);
                parents.Add(parent);
                binds.Add(pose.Bind(parent));
                poses.Add(pose.Pose(parent));
            }
            use = new LivePose();
            use.Set(names, parents, binds, poses, pose.Root, pose.GenderRace);
        }
        var posed = new Vector3[mesh.VertexCount];
        new LiveMeshPoser { ApplyDeformer = false }.Pose(mesh, use, null, posed);
        return (posed, unmapped);
    }

    /// <summary>Which mesh bones weigh on vertex <paramref name="vertex"/> (for naming an unmapped bone that matters).</summary>
    internal static IEnumerable<string> BonesOf(SkinnedMesh mesh, int vertex)
    {
        int o = vertex * SkinnedMesh.MaxInfluences;
        for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
            if (mesh.BoneWeights[o + k] > 0f) yield return mesh.BoneNames[mesh.BoneIndices[o + k]];
    }
}
