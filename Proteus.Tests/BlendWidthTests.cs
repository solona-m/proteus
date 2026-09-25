using System;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A mesh keeps the number of bone-influence slots its author gave it.
/// <para/>
/// A refit across bodies routinely wants a fifth influence, and the writer used to widen the vertex from four
/// slots to eight to hold it. That is not safe on a garment: the character shaders declare their input as
/// <c>float4 blendWeight</c> / <c>int4 blendIndices</c>, so anything past the fourth is never read. The weights
/// the shader does see then sum to less than one and the vertex draws pulled toward the origin — a dent whose
/// inner surface reads as a dark band.
/// <para/>
/// Measured on "Rana" refitted to Neolithe: 143 of the shirt's vertices wanted a fifth influence, 51 of them in a
/// symmetric pair of bands on the sleeves — where the artifact was. A body authored eight-wide keeps its eight;
/// this only stops a four-wide mesh being widened.
/// </summary>
public class BlendWidthTests
{
    private const string Material = "/mt_c0201e0485_top_a.mtrl";

    /// <summary>A four-influence host, as every garment authored before Dawntrail is.</summary>
    private static byte[] Host() => SyntheticModel.Build(
        ["atr_top"],
        [new SyntheticModel.Mesh(Material,
            new SyntheticModel.Sub(0, Weights: [("j_hij_l", 0.5f), ("j_sebo_c", 0.5f)]))],
        SyntheticModel.V6);

    private static (int Slots, int[] Sums) BlendOf(byte[] mdl)
    {
        var p = SecondSkinWriter.Parse(mdl);
        int mo = p.MeshStart;
        int vc = BitConverter.ToUInt16(p.S, mo);
        var we = p.Decls[0].First(e => e.Usage == 1);
        int slots = SecondSkinWriter.BlendCount(we.Type);
        uint[] vOff = { BitConverter.ToUInt32(p.S, mo + 20), BitConverter.ToUInt32(p.S, mo + 24),
                        BitConverter.ToUInt32(p.S, mo + 28) };
        byte[] str = { p.S[mo + 32], p.S[mo + 33], p.S[mo + 34] };

        var sums = new int[vc];
        for (int v = 0; v < vc; v++)
        {
            int at = (int)(p.Vb + vOff[we.Stream]) + v * str[we.Stream] + we.Offset;
            for (int k = 0; k < slots; k++) sums[v] += p.S[at + k];
        }
        return (slots, sums);
    }

    /// <summary>
    /// The case that broke: a reskin naming five bones for a four-slot mesh. The mesh stays four-slot, and the four
    /// influences it keeps still come to 255 — the dropped one is folded back in, so nothing shrinks.
    /// </summary>
    [Fact]
    public void A_reskin_needing_five_does_not_widen_the_mesh()
    {
        var host = Host();
        var vc = BitConverter.ToUInt16(SecondSkinWriter.Parse(host).S, SecondSkinWriter.Parse(host).MeshStart);

        var plan = new (string Bone, float W)[]?[vc];
        for (int i = 0; i < vc; i++)
            plan[i] = [("j_hij_l", 0.3f), ("j_sebo_c", 0.3f), ("n_root", 0.2f),
                       ("j_hij_l", 0.1f), ("j_sebo_c", 0.1f)];

        var built = SecondSkinWriter.Build([], [], host, out _, hostReskin: m => m == 0 ? plan : null);

        var (slots, sums) = BlendOf(built);
        Assert.Equal(4, slots);
        Assert.All(sums, x => Assert.Equal(255, x));
    }

    /// <summary>And an ordinary four-influence reskin is untouched, slots and sum alike.</summary>
    [Fact]
    public void A_reskin_within_four_keeps_its_weights()
    {
        var host = Host();
        var vc = BitConverter.ToUInt16(SecondSkinWriter.Parse(host).S, SecondSkinWriter.Parse(host).MeshStart);

        var plan = new (string Bone, float W)[]?[vc];
        for (int i = 0; i < vc; i++) plan[i] = [("j_hij_l", 0.75f), ("j_sebo_c", 0.25f)];

        var built = SecondSkinWriter.Build([], [], host, out _, hostReskin: m => m == 0 ? plan : null);

        var (slots, sums) = BlendOf(built);
        Assert.Equal(4, slots);
        Assert.All(sums, x => Assert.Equal(255, x));
    }
}
