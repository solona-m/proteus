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

    /// <summary>As many triangles beside the fur as fur: not an ear submesh, since fur must outnumber the rest.</summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    public void ATieIsNotAnEarSubmesh()
    {
        var ears = Read(Ear(furTriangles: 6, skinTriangles: 6, skinAt: 0.01f));
        Assert.DoesNotContain(Corners(6, 6), ears.Fur.Contains);
    }

    /// <summary>
    /// The rule looks inside the fur's own submesh only: hair in another submesh of the same mesh, however close to the
    /// fur, is hair. And a second mesh is read with its own vertices.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-miqote-ear-skin-3")]
    [Trait("Plan", "EDGE-miqote-ear-skin-1")]
    public void SkinIsLookedForInTheFursOwnSubmeshAndMeshOnly()
    {
        var pos = new List<Vector3>();
        var weights = new List<(string Bone, float W)[]?>();
        List<int> Tris(int count, float x0, (string Bone, float W)[] w)
        {
            var t = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int at = pos.Count;
                float x = x0 + i * 0.0005f;
                pos.Add(new Vector3(x, 1.55f, 0)); pos.Add(new Vector3(x + 0.002f, 1.55f, 0)); pos.Add(new Vector3(x, 1.552f, 0));
                weights.Add(w); weights.Add(w); weights.Add(w);
                t.AddRange([at, at + 1, at + 2]);
            }
            return t;
        }
        var ear = Tris(10, 0f, [("j_mimi_r", 1f)]).Concat(Tris(2, 0.02f, Head)).ToArray();   // submesh 0: fur + skin
        var scalp = Tris(4, 0.01f, Head).ToArray();                                         // submesh 1: hair, close by
        var mesh0 = new SyntheticModel.SurfaceMesh("/mt_c0801h0118_hir_a.mtrl", pos.ToArray(),
            pos.Select(_ => Vector3.UnitZ).ToArray(), pos.Select(_ => Vector2.Zero).ToArray(), weights.ToArray(),
            [(0u, ear), (0u, scalp)]);
        // A second mesh: the hair, all on the head bone.
        var hairPos = Enumerable.Range(0, 3).Select(i => new Vector3(0.01f + i * 0.001f, 1.6f, 0)).ToArray();
        var mesh1 = new SyntheticModel.SurfaceMesh("/mt_c0801h0118_hir_b.mtrl", hairPos,
            hairPos.Select(_ => Vector3.UnitZ).ToArray(), hairPos.Select(_ => Vector2.Zero).ToArray(),
            hairPos.Select(_ => (((string, float)[]?)Head)).ToArray(), [(0u, new[] { 0, 1, 2 })]);
        var mdl = SyntheticModel.BuildSurface(["atr_hv_a"], mesh0, mesh1);

        var ears = Read(mdl);
        Assert.True(Corners(10, 2).All(ears.Fur.Contains));            // the skin in the fur's submesh
        Assert.DoesNotContain(Corners(12, 4), ears.Fur.Contains);       // the hair in the next submesh
        Assert.DoesNotContain(ears.Fur, k => (k >> 32) == 1);           // nothing of the other mesh
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
