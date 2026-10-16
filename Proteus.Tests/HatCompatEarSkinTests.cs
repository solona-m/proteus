using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Many Miqo'te hairstyles ship the ear as one piece: fur on the ear bones and a ring of skin around the ear's root that
/// rides the HEAD bone. The skinning alone calls that skin hair, the cut tagged it <c>atr_kam</c>, and under a hat the
/// ear's base went missing — reported as auto hat compat "hiding the skin". Measured on the installed hairs: the skin is
/// in the fur's own submesh, within 60 mm of the fur.
/// </summary>
public class HatCompatEarSkinTests
{
    private static readonly (string Bone, float W)[] Head = [("j_kao", 1f)];

    /// <summary>
    /// One mesh, one submesh: <paramref name="furTriangles"/> triangles on the ear bone at x 0..0.01, then
    /// <paramref name="skinTriangles"/> on the head bone starting <paramref name="skinAt"/> metres away. Real scale.
    /// </summary>
    private static byte[] Ear(int furTriangles, int skinTriangles, float skinAt, string bone = "j_mimi_l",
                              (string Bone, float W)[]? furWeights = null)
    {
        var pos = new List<Vector3>();
        var weights = new List<(string Bone, float W)[]?>();
        var tris = new List<int>();
        void Tri(float x, (string Bone, float W)[] w)
        {
            int at = pos.Count;
            pos.Add(new Vector3(x, 1.55f, 0)); pos.Add(new Vector3(x + 0.002f, 1.55f, 0)); pos.Add(new Vector3(x, 1.552f, 0));
            weights.Add(w); weights.Add(w); weights.Add(w);
            tris.AddRange([at, at + 1, at + 2]);
        }
        for (int i = 0; i < furTriangles; i++) Tri(i * 0.0005f, furWeights ?? [(bone, 1f)]);
        for (int i = 0; i < skinTriangles; i++) Tri(skinAt + i * 0.0005f, Head);
        var n = pos.Select(_ => Vector3.UnitZ).ToArray();
        var uv = pos.Select(_ => Vector2.Zero).ToArray();
        return SyntheticModel.BuildSurface(["atr_hv_a"],
            new SyntheticModel.SurfaceMesh("/mt_c0801h0118_hir_a.mtrl", pos.ToArray(), n, uv, weights.ToArray(),
                                           [(0u, tris.ToArray())]));
    }

    private static HatCompatSolve.EarGeometry Read(byte[] mdl)
        => HatCompatSolve.ReadEarGeometry(mdl, SecondSkinWriter.Parse(mdl), HatCompatSolve.ReadLod0Meshes(mdl));

    /// <summary>Corners of the triangles from <paramref name="first"/> on, as vertex keys of mesh 0.</summary>
    private static IEnumerable<long> Corners(int first, int count)
        => Enumerable.Range(first * 3, count * 3).Select(v => (long)(uint)v);

    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    public void TheSkinBesideTheFurIsSparedWithIt()
    {
        var ears = Read(Ear(furTriangles: 10, skinTriangles: 4, skinAt: 0.02f));
        Assert.True(Corners(10, 4).All(ears.Fur.Contains));
    }

    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    public void SkinBeyondTheReachIsLeftToTheCut()
    {
        var ears = Read(Ear(furTriangles: 10, skinTriangles: 4, skinAt: HatCompatSolve.EarSkinReach + 0.01f));
        Assert.DoesNotContain(Corners(10, 4), ears.Fur.Contains);
    }

    /// <summary>One hair welds its whole scalp into the fur's submesh: that hair still goes under the hat.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    [Trait("Plan", "EDGE-miqote-ear-skin-1")]
    public void HairWeldedIntoTheFursSubmeshIsNotSparedAsSkin()
    {
        var ears = Read(Ear(furTriangles: 4, skinTriangles: 10, skinAt: 0.01f));
        Assert.DoesNotContain(Corners(4, 10), ears.Fur.Contains);
        Assert.True(Corners(0, 4).All(ears.Fur.Contains));   // the fur itself is spared as ever
    }

    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-4")]
    [Trait("Plan", "EDGE-miqote-ear-skin-4")]
    public void AHairWithNoEarBoneSparesNoSkin()
    {
        var ears = Read(Ear(furTriangles: 10, skinTriangles: 4, skinAt: 0.02f, bone: "j_kami_a"));
        Assert.Empty(ears.Fur);
        Assert.Empty(ears.Touched);
    }

    /// <summary>
    /// The second cut (<c>CutUnaffordable</c>) takes vertices the press wanted and could not afford; the press never
    /// wants a <c>Touched</c> vertex. So every spared skin vertex must be in <c>Touched</c>, or the second cut takes it.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-1")]
    public void SparedSkinIsOutOfThePressSoTheSecondCutCannotTakeIt()
    {
        var ears = Read(Ear(furTriangles: 10, skinTriangles: 4, skinAt: 0.02f));
        Assert.True(ears.Fur.IsSubsetOf(ears.Touched));
    }

    [Fact]
    [Trait("Plan", "EDGE-miqote-ear-skin-3")]
    public void AStrayEarWeightBelowTheFloorSparesNoSkin()
    {
        // An earless conversion: the "fur" rows carry a hundredth of an ear bone. No fur, so no skin either.
        var ears = Read(Ear(furTriangles: 10, skinTriangles: 4, skinAt: 0.02f,
                            furWeights: [("j_kao", 0.99f), ("j_mimi_l", 0.01f)]));
        Assert.Empty(ears.Fur);
    }
}
