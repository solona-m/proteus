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
    [Trait("Plan", "AC-featureless-bulge-6")]
    [Trait("Plan", "EDGE-featureless-bulge-29")]
    public void Rim_takes_the_bodys_weights_and_no_penis_bone_remains()
    {
        var bulge = TbseBulge.Shared!;
        // The hole ring itself on a penis bone too (as TBSE-X), so the rim's and the skin's genital share must both go.
        var body = SyntheticBulge.Body(domeBone: "iv_funyachin_phy_a", ringBone: "iv_funyachin_phy_a");
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
            Assert.Contains("j_kosi", bones);   // the rim's penis share went to the hips
            // The body's own vertex at that hole point, on the main skin.
            var main = parts.Parts.Single(p => p.Island < 0 && p.Label == "1.1").Triangles.Distinct()
                            .MinBy(u => Vector3.Distance(At(parts, u), bulge.Hole[bulge.Data.rim[i]]));
            // The rim and the main skin beside it move as one: the same weights, read back from the written body.
            for (int k = 0; k < 4; k++)
                Assert.Equal(skin.BoneWeights[main * 8 + k], skin.BoneWeights[v * 8 + k], 2);
            Assert.DoesNotContain(Enumerable.Range(0, 4).Where(k => skin.BoneWeights[main * 8 + k] > 0f)
                                            .Select(k => skin.BoneNames[skin.BoneIndices[main * 8 + k]]), GenitalParts.IsPenisBone);
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
    [Trait("Plan", "AC-featureless-bulge-9")]
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

    /// <summary>The swap's inputs to its self-check, read back from a swap of the synthetic body that passed it.</summary>
    private static (byte[] Model, ModelParts Before, ModelPart Piece, BulgeSwap.FittedBulge Fitted, int[] SlotOf, int[] BodyAt) Swapped()
    {
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body();
        var swapped = BulgeSwap.Featureless(body, bulge, out var why);
        Assert.True(swapped != null, why);
        var before = ModelPartReader.Read(body)!;
        var piece = before.Parts.Single(p => p.Island < 0 && p.Label == "1.2");
        var bodyAt = BulgeSwap.MatchHole(before, piece, bulge.Hole)!;
        var after = ModelPartReader.Read(swapped!.Model)!;
        var positions = swapped.SlotOf.Select(v => At(after, v)).ToArray();
        var fitted = BulgeSwap.Exact(bulge, before, bodyAt) with { Positions = positions };
        Assert.Null(BulgeSwap.SelfCheck(swapped.Model, before, piece, bulge, fitted, swapped.SlotOf));
        return (swapped.Model, before, piece, fitted, swapped.SlotOf, bodyAt);
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-13")]
    public void Self_check_refuses_a_face_turned_over()
    {
        var (model, before, piece, fitted, slotOf, bodyAt) = Swapped();
        var t = TbseBulge.Shared!.Data.triangles;
        var flipped = (Vector3[])fitted.Positions.Clone();
        // Push one corner through the face's plane, past the opposite edge: the face now points the other way.
        var a = flipped[t[0]]; var b = flipped[t[1]]; var c = flipped[t[2]];
        flipped[t[0]] = (b + c) - a;
        Assert.Contains("turned over", BulgeSwap.SelfCheck(model, before, piece, TbseBulge.Shared, fitted with { Positions = flipped }, slotOf) ?? "");
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-13")]
    public void Self_check_refuses_a_face_collapsed()
    {
        var (model, before, piece, fitted, slotOf, bodyAt) = Swapped();
        var t = TbseBulge.Shared!.Data.triangles;
        var collapsed = (Vector3[])fitted.Positions.Clone();
        collapsed[t[1]] = collapsed[t[0]];
        collapsed[t[2]] = collapsed[t[0]];
        Assert.Contains("collapsed", BulgeSwap.SelfCheck(model, before, piece, TbseBulge.Shared, fitted with { Positions = collapsed }, slotOf) ?? "");
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-13")]
    public void Self_check_refuses_an_edge_off_the_hole()
    {
        var (model, before, piece, fitted, slotOf, bodyAt) = Swapped();
        // The hole taken one point round: its points are no longer where the bulge's open edge is... the set is the same,
        // so take a body vertex off the hole instead — the main skin's next ring.
        var shifted = (int[])bodyAt.Clone();
        var main = before.Parts.Single(p => p.Island < 0 && p.Label == "1.1").Triangles.Distinct()
                         .Where(v => !bodyAt.Contains(v)).ToList();
        shifted[0] = main.MinBy(v => Vector3.Distance(At(before, v), At(before, bodyAt[0])));
        Assert.Contains("open edge is not the body's hole", BulgeSwap.SelfCheck(model, before, piece, TbseBulge.Shared!, fitted with { Hole = shifted }, slotOf) ?? "");
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-13")]
    [Trait("Plan", "EDGE-featureless-bulge-29")]
    public void Self_check_refuses_a_rim_off_the_bodys_weights()
    {
        var (model, before, piece, fitted, slotOf, bodyAt) = Swapped();
        var bulge = TbseBulge.Shared!;
        int i = Enumerable.Range(0, bulge.Positions.Length).First(k => bulge.Data.rim[k] >= 0);
        // Rewrite that rim vertex's first weight byte and its partner: no longer the body's at the seam.
        var bad = (byte[])model.Clone();
        var src = SecondSkinWriter.Parse(bad);
        int mo = src.MeshStart + piece.Mesh * 36;
        var we = src.Decls[piece.Mesh].First(e => e.Usage == SecondSkinWriter.UseBlendWeight);
        uint vbo = BitConverter.ToUInt32(bad, mo + 20 + we.Stream * 4);
        int stride = bad[mo + 32 + we.Stream];
        int at = (int)(src.Vb + vbo) + slotOf[i] * stride + we.Offset;
        byte first = bad[at];
        bad[at] = (byte)Math.Max(0, first - 60);
        bad[at + 1] = (byte)Math.Min(255, bad[at + 1] + 60);
        Assert.Contains("weights are not the body's", BulgeSwap.SelfCheck(bad, before, piece, bulge, fitted, slotOf) ?? "");
    }

    [Fact]
    [Trait("Plan", "EDGE-featureless-bulge-11")]
    public void A_same_mesh_source_never_lands_by_identity()
    {
        // Two sizes of one body as two files: BuildPair lands them by identity. The featureless pair must not — an identity
        // landing would put the old genitals onto bulge slots in whatever order they were written. The rewritten piece
        // changes the mesh's structure, so identity declines it and the landing is by uv (the factory also refuses an
        // identity landing outright, should one ever come back).
        var body = SyntheticBulge.Body(outerRings: 60);
        var source = SyntheticBulge.Body(height: 0.065f, outerRings: 60);
        Assert.Null(BodyRetarget.BuildPair("_dwn", source, body, "_dwn", true, null, null, null, out var pair));
        Assert.NotNull(pair.Featureless);
        Assert.IsType<IdentityCorrespondence>(pair.Correspondence);
        var (made, why) = pair.Featureless!();
        Assert.True(made != null, why);
        Assert.IsNotType<IdentityCorrespondence>(made!.Value.Correspondence);
    }

    [Fact]
    [Trait("Plan", "AC-featureless-bulge-1")]
    public void The_bulge_faces_outward()
    {
        // Every bulge face at the rim turns the way the body's skin there does: a reversed winding would be culled.
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body();
        var swapped = BulgeSwap.Featureless(body, bulge, out var why)!;
        Assert.True(swapped != null, why);
        var before = ModelPartReader.Read(body)!;
        var after = ModelPartReader.Read(swapped.Model)!;
        var t = bulge.Data.triangles;
        int checkedFaces = 0;
        for (int f = 0; f + 2 < t.Length; f += 3)
        {
            int r = Enumerable.Range(0, 3).Select(k => t[f + k]).FirstOrDefault(v => bulge.Data.rim[v] >= 0, -1);
            if (r < 0) continue;
            var a = At(after, swapped.SlotOf[t[f]]); var b = At(after, swapped.SlotOf[t[f + 1]]); var c = At(after, swapped.SlotOf[t[f + 2]]);
            var face = Vector3.Cross(b - a, c - a);
            var hole = bulge.Hole[bulge.Data.rim[r]];
            int bv = before.Parts.Single(p => p.Island < 0 && p.Label == "1.1").Triangles.Distinct().MinBy(v => Vector3.Distance(At(before, v), hole));
            var n = new Vector3(before.Normals[bv * 3], before.Normals[bv * 3 + 1], before.Normals[bv * 3 + 2]);
            Assert.True(Vector3.Dot(face, n) > 0f, $"bulge face {f / 3} at the rim faces into the body");
            checkedFaces++;
        }
        Assert.True(checkedFaces > 0);
    }
}
