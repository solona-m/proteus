using System;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;

namespace Proteus.Tests;

/// <summary>The featureless body (plan featureless-bulge, path A): the owner's bulge drawn in place of a TBSE-like body's
/// genital piece, on synthetic bodies built round the shipped bulge's hole.</summary>
public class BulgeTransplantTests
{
    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-1")]
    [Trait("Plan", "EDGE-featureless-bulge-5")]
    public void Same_mesh_swap()
    {
        var bulge = TbseBulge.Shared;
        Assert.NotNull(bulge);
        var body = SyntheticBulge.Body();
        var before = ModelPartReader.Read(body)!;
        var reading = GenitalParts.Read(before, ModelSkinReader.Read(body, null, null)!);
        Assert.NotNull(reading);
        Assert.True(reading!.Penis);
        Assert.Contains(reading.Genitals, g => g.Part.Label == "1.2" && g.Rule == "c");

        var swapped = BulgeSwap.Featureless(body, bulge!, out var why);
        Assert.True(swapped != null, why);
        Assert.Equal(body.Length, swapped!.Model.Length);   // in place: nothing grows or shrinks

        var after = ModelPartReader.Read(swapped.Model)!;
        Assert.Equal(before.Positions.Length, after.Positions.Length);
        var piece = after.Parts.Single(p => p.Island < 0 && p.Label == "1.2");
        int drawn = Enumerable.Range(0, piece.Triangles.Length / 3).Count(t =>
            piece.Triangles[t * 3] != piece.Triangles[t * 3 + 1] && piece.Triangles[t * 3 + 1] != piece.Triangles[t * 3 + 2]
            && piece.Triangles[t * 3] != piece.Triangles[t * 3 + 2]);
        Assert.Equal(bulge!.Data.triangles.Length / 3, drawn);
        Assert.Equal(before.Parts.Single(p => p.Island < 0 && p.Label == "1.2").Triangles.Length / 3 - drawn, swapped.Collapsed);
        for (int i = 0; i < bulge.Positions.Length; i++)
        {
            // A rim vertex lands exactly on its hole point (the sculpt's rim sits within 0.1 mm of it); the rest as shipped.
            var expected = bulge.Data.rim[i] >= 0 ? bulge.Hole[bulge.Data.rim[i]] : bulge.Positions[i];
            Assert.True(Vector3.Distance(expected, At(after, swapped.SlotOf[i])) < 1e-5f, $"bulge vertex {i} not written");
        }
        // The main skin is untouched, byte for byte in its vertices.
        var skin = before.Parts.Single(p => p.Island < 0 && p.Label == "1.1").Triangles.Distinct();
        Assert.All(skin, v => Assert.Equal(At(before, v), At(after, v)));
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-5")]
    [Trait("Plan", "EDGE-featureless-bulge-16")]
    public void Rim_takes_the_bodys_weights_and_no_penis_bone_remains()
    {
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body(domeBone: "iv_funyachin_phy_a");
        var swapped = BulgeSwap.Featureless(body, bulge, out var why);
        Assert.True(swapped != null, why);
        var skin = ModelSkinReader.Read(swapped!.Model, null, null)!;
        var before = ModelSkinReader.Read(body, null, null)!;
        var parts = ModelPartReader.Read(body)!;
        for (int i = 0; i < bulge.Positions.Length; i++)
        {
            int v = swapped.SlotOf[i];
            var bones = Enumerable.Range(0, XivLiveMesh.SkinnedMesh.MaxInfluences)
                                  .Where(k => skin.BoneWeights[v * 8 + k] > 0f)
                                  .Select(k => skin.BoneNames[skin.BoneIndices[v * 8 + k]]).ToList();
            Assert.DoesNotContain(bones, GenitalParts.IsPenisBone);
            if (bulge.Data.rim[i] < 0) continue;
            // The body's own vertex at that hole point, on the main skin.
            var main = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.1").Triangles.Distinct()
                            .MinBy(u => Vector3.Distance(At(parts, u), bulge.Hole[bulge.Data.rim[i]]));
            for (int k = 0; k < 4; k++)
                Assert.Equal(before.BoneWeights[main * 8 + k], skin.BoneWeights[v * 8 + k], 2);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-39")]
    public void Topology_mismatch_left_as_today()
    {
        var bulge = TbseBulge.Shared!;
        // A hole the bulge does not fit: every point 2 cm forward, past what the bulge may be warped by.
        var other = bulge.Hole.Select(p => p + new Vector3(0f, 0f, 0.02f)).ToArray();
        var body = SyntheticBulge.Body(hole: other);
        Assert.Null(BulgeSwap.Featureless(body, bulge, out var why));
        Assert.Equal("no sculpt for this body", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-2")]
    public void No_penis_no_swap()
    {
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body(height: 0.004f);   // a dome barely off the skin: not a penis
        Assert.Null(BulgeSwap.Featureless(body, bulge, out var why));
        Assert.Equal("no penis", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-33")]
    public void Too_few_slots_refused()
    {
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body(rings: 2);   // 85 slots, 140 triangles: less than the bulge needs
        Assert.Null(BulgeSwap.Featureless(body, bulge, out var why));
        Assert.Contains("fewer than the bulge's", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-21")]
    public void A_hole_a_few_millimetres_off_takes_the_bulge_warped_onto_it()
    {
        var bulge = TbseBulge.Shared!;
        // TBSE-X's hole is The Body SE's 1.85 mm off: here every point 3 mm forward.
        var shifted = bulge.Hole.Select(p => p + new Vector3(0f, 0f, 0.003f)).ToArray();
        var body = SyntheticBulge.Body(hole: shifted);
        var swapped = BulgeSwap.Featureless(body, bulge, out var why);
        Assert.True(swapped != null, why);
        var after = ModelPartReader.Read(swapped!.Model)!;
        for (int i = 0; i < bulge.Positions.Length; i++)
            if (bulge.Data.rim[i] >= 0)
                Assert.True(Vector3.Distance(shifted[bulge.Data.rim[i]], At(after, swapped.SlotOf[i])) < 1e-5f, $"rim vertex {i} is not on the body's hole");
        Assert.False(GenitalParts.Read(after, ModelSkinReader.Read(swapped.Model, null, null)!)?.Penis ?? false,
                     "the featureless body still reads as having a penis");
    }
}
