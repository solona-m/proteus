using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XivLiveMesh;
using Xunit;

namespace Proteus.Tests;

/// <summary>The racial deformer parser, on layouts the game's own file never reaches.</summary>
public class PbdFileTests
{
    /// <summary>
    /// NIRÆ's human_crimson_v10.pbd: deformers of ~650 bones, so the bone-name offsets (from the deformer's start)
    /// pass 32767. Read signed, they went negative and the whole file failed to load — no racial bend anywhere.
    /// </summary>
    [Fact]
    public void Reads_a_deformer_whose_bone_names_sit_past_32_KB()
    {
        const int boneCount = 700;   // names start at 4 + 700 × 2 + 700 × 48 = 35004 bytes in
        var bones = Enumerable.Range(0, boneCount)
            .ToDictionary(i => $"j_bone_{i:D3}", i => Matrix4x4.CreateTranslation(i * 0.001f, 0f, 0f));
        var pbd = new PbdFile(HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, new Dictionary<string, Matrix4x4> { ["j_kao"] = Matrix4x4.Identity }),
            (201, 101, bones),
        ]));

        var chain = pbd.Chain(101, 201);

        var deformer = Assert.Single(chain);
        Assert.Equal(boneCount, deformer.Count);
        Assert.Equal(0.699f, deformer["j_bone_699"].Translation.X, 5);
        Assert.Equal(0f, deformer["j_bone_000"].Translation.X, 5);
    }
}
