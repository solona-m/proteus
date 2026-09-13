using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// Posing a model onto a skeleton on the CPU: the maths, the racial deformer file, and the one invariant the
/// live brush depends on — that the posed mesh numbers its vertices exactly as the brush's model does.
/// </summary>
public class LiveMeshTests(ITestOutputHelper o)
{
    private static SkinnedMesh OneVertex(Vector3 p, params (int Bone, float W)[] influences)
    {
        var idx = new ushort[SkinnedMesh.MaxInfluences];
        var w = new float[SkinnedMesh.MaxInfluences];
        for (int k = 0; k < influences.Length; k++) { idx[k] = (ushort)influences[k].Bone; w[k] = influences[k].W; }
        return new SkinnedMesh
        {
            Positions = [p, p, p],
            Triangles = [0, 1, 2],
            BaseTriangles = [0, 1, 2],
            MaterialNames = ["/mt_test.mtrl"],
            TriangleMaterials = [0],
            BoneNames = ["root", "arm"],
            BoneIndices = [.. idx, .. idx, .. idx],
            BoneWeights = [.. w, .. w, .. w],
        };
    }

    /// <summary>
    /// A vertex bound to a bone follows that bone out of its reference pose and into its current one, then into
    /// the world — in that order. Getting the multiplication order wrong moves the vertex by the bone's
    /// reference offset twice, or rotates it about the world origin instead of the joint.
    /// </summary>
    [Fact]
    public void AVertexFollowsItsBoneFromBindToPoseToWorld()
    {
        // The arm bone sits 1 m up at rest; this frame it is rotated 90° about Z at the same joint.
        var bindArm = Matrix4x4.CreateTranslation(0f, 1f, 0f);
        var poseArm = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(0f, 1f, 0f);
        var root = Matrix4x4.CreateTranslation(10f, 0f, 0f);

        var pose = new LivePose();
        pose.Set(["root", "arm"], [-1, 0], [Matrix4x4.Identity, bindArm], [Matrix4x4.Identity, poseArm], root, 101);

        // A vertex 1 m along +X from the arm's joint, weighted fully to the arm.
        var mesh = OneVertex(new Vector3(1f, 1f, 0f), (1, 1f));
        var world = new Vector3[3];
        new LiveMeshPoser().Pose(mesh, pose, null, world);

        // Rotated 90° about the joint: +X becomes +Y, so (0, 2, 0) in model space, then +10 X into the world.
        Assert.True(Vector3.Distance(world[0], new Vector3(10f, 2f, 0f)) < 1e-4f, $"got {world[0]}");
    }

    /// <summary>Weights blend linearly between bones, the way the game's vertex shader does.</summary>
    [Fact]
    public void WeightsBlendBetweenBones()
    {
        var pose = new LivePose();
        pose.Set(["root", "arm"], [-1, 0],
                 [Matrix4x4.Identity, Matrix4x4.Identity],
                 [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0f, 0f, 2f)], Matrix4x4.Identity, 101);

        var mesh = OneVertex(Vector3.Zero, (0, 0.5f), (1, 0.5f));
        var world = new Vector3[3];
        new LiveMeshPoser().Pose(mesh, pose, null, world);
        Assert.True(Vector3.Distance(world[0], new Vector3(0f, 0f, 1f)) < 1e-5f, $"got {world[0]}");
    }

    /// <summary>A bone the skeleton does not have leaves its vertices following the root, and is counted.</summary>
    [Fact]
    public void AMissingBoneFollowsTheRootAndIsReported()
    {
        var pose = new LivePose();
        pose.Set(["root"], [-1], [Matrix4x4.Identity], [Matrix4x4.Identity], Matrix4x4.CreateTranslation(0f, 5f, 0f), 101);

        var mesh = OneVertex(Vector3.Zero, (1, 1f));   // "arm", which this skeleton lacks
        var world = new Vector3[3];
        var poser = new LiveMeshPoser();
        poser.Pose(mesh, pose, null, world);
        Assert.Equal(1, poser.MissingBones);
        Assert.True(Vector3.Distance(world[0], new Vector3(0f, 5f, 0f)) < 1e-5f);
    }

    /// <summary>
    /// The picker finds the nearest triangle along the ray, from either side, and its barycentrics put the hit
    /// back on the same point — the round trip that carries a click on the character into the model.
    /// </summary>
    [Fact]
    public void ThePickerFindsTheNearestTriangleAndItsBarycentrics()
    {
        Vector3[] world =
        [
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0),      // triangle 0 at z = 0
            new(0, 0, 2), new(1, 0, 2), new(0, 1, 2),      // triangle 1 at z = 2, nearer a ray coming from +z
        ];
        int[] tris = [0, 1, 2, 3, 4, 5];

        var origin = new Vector3(0.25f, 0.25f, 5f);
        var hit = LiveMeshPicker.Raycast(world, tris, origin, -Vector3.UnitZ);
        Assert.NotNull(hit);
        Assert.Equal(1, hit!.Value.Triangle);
        Assert.True(Vector3.Distance(hit.Value.World, new Vector3(0.25f, 0.25f, 2f)) < 1e-5f);
        var back = hit.Value.Interpolate(world[3], world[4], world[5]);
        Assert.True(Vector3.Distance(back, hit.Value.World) < 1e-5f);
        Assert.True(hit.Value.Normal.Z > 0.99f, "the normal faces the ray's origin");

        // From the other side: the far triangle is now the near one, and back faces still count.
        var below = LiveMeshPicker.Raycast(world, tris, new Vector3(0.25f, 0.25f, -5f), Vector3.UnitZ);
        Assert.Equal(0, below!.Value.Triangle);

        // A refused triangle does not block the ray.
        var through = LiveMeshPicker.Raycast(world, tris, origin, -Vector3.UnitZ, t => t != 1);
        Assert.Equal(0, through!.Value.Triangle);

        Assert.Null(LiveMeshPicker.Raycast(world, tris, new Vector3(5f, 5f, 5f), -Vector3.UnitZ));
    }

    [Fact]
    public void RaceCodesComeFromGamePaths()
    {
        Assert.Equal(201, ModelSkinReader.RaceOf("chara/equipment/e6116/model/c0201e6116_top.mdl"));
        Assert.Equal(1401, ModelSkinReader.RaceOf("chara/human/c1401/obj/body/b0001/model/c1401b0001_top.mdl"));
        Assert.Equal(101, ModelSkinReader.RaceOf("chara/accessory/a0053/model/c0101a0053_rir.mdl"));
        Assert.Equal(0, ModelSkinReader.RaceOf("mods/acc0101/thing.mdl"));   // a letter before the c: not a race
        Assert.Equal(0, ModelSkinReader.RaceOf(null));
    }

    /// <summary>
    /// THE invariant the live brush stands on: the posed mesh's vertex i is the brush model's vertex i. Checked
    /// on every shipped toe-cap model, which carry real bone weights.
    /// </summary>
    [Fact]
    public void PosedVerticesLineUpWithTheBrushModel()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Meshes");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.mdl") : [];
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var parts = ModelPartReader.Read(bytes);
            var skinned = ModelSkinReader.Read(bytes, null, null);
            Assert.NotNull(parts);
            Assert.NotNull(skinned);

            Assert.Equal(parts!.Positions.Length / 3, skinned!.VertexCount);
            for (int v = 0; v < skinned.VertexCount; v++)
                Assert.Equal(new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]),
                             skinned.Positions[v]);

            int weighted = Enumerable.Range(0, skinned.VertexCount)
                .Count(v => skinned.BoneWeights[v * SkinnedMesh.MaxInfluences] > 0f);
            o.WriteLine($"{Path.GetFileName(file)}: {skinned.VertexCount} verts, {weighted} weighted, " +
                        $"{skinned.BoneNames.Length} bones, {skinned.TriangleCount} tris");
            Assert.True(weighted > skinned.VertexCount / 2, "most vertices should carry weights");
        }
    }

    /// <summary>
    /// The game's own racial deformer file parses, and bends a Midlander model onto a Roegadyn: a non-empty
    /// chain whose pelvis matrix is not the identity. Skipped without a local game install.
    /// </summary>
    [Fact]
    public void TheGamesRacialDeformerParsesAndChains()
    {
        var game = Environment.GetEnvironmentVariable("PROTEUS_GAME")
                ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        var sqpack = Path.Combine(game, "game", "sqpack");
        if (!Directory.Exists(sqpack)) { o.WriteLine($"no game data at {sqpack}"); return; }

        var data = new Lumina.GameData(sqpack);
        var file = data.GetFile("chara/xls/boneDeformer/human.pbd");
        Assert.NotNull(file);
        var pbd = new PbdFile(file!.Data);

        Assert.Empty(pbd.Chain(101, 101));
        var chain = pbd.Chain(101, 901);   // Midlander male -> Roegadyn male
        o.WriteLine($"c0101 -> c0901: {chain.Count} deformer(s)");
        Assert.NotEmpty(chain);

        var pelvis = PbdFile.Resolve(chain, "j_kosi", _ => null);
        o.WriteLine($"j_kosi: {pelvis}");
        Assert.NotEqual(Matrix4x4.Identity, pelvis);
    }
}
