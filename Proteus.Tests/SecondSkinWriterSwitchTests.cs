using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The writer's switched copies (plan genital-switches, Phase 1; <see cref="ContentGeometry.Switches"/>): a body's piece
/// emitted once per tag set, each copy tagged, on the same vertices. A Sensitive-Area writer's own tests.
/// </summary>
public class SecondSkinWriterSwitchTests
{
    private static readonly IReadOnlySet<string> EJ = new HashSet<string> { "atr_dv_e", "atr_dv_j" };
    private static readonly IReadOnlySet<string> FJ = new HashSet<string> { "atr_dv_f", "atr_dv_j" };

    /// <summary>A host with cloth only: the garment the geometry is written into.</summary>
    private static byte[] Host() => BulgeRefitTests.Garment(SyntheticBulge.Body(), _ => true);

    private static byte[] Build(byte[] host, ContentGeometry geo, out SecondSkinWriter.ReskinReport report)
    {
        report = new SecondSkinWriter.ReskinReport();
        return SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(),
            [new SecondSkinLayer { MaterialName = SyntheticBulge.Skin, Geometry = [geo] }], host, out _, reskinReport: report);
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// <summary>The output's parts of the layer's mesh (the last one: the layer comes after the host's).</summary>
    private static List<ModelPart> LayerParts(ModelParts m)
    {
        int mesh = m.Parts.Where(p => p.Island < 0 && p.Material == SyntheticBulge.Skin).Max(p => p.Mesh);
        return m.Parts.Where(p => p.Island < 0 && p.Mesh == mesh).OrderBy(p => p.Submesh).ToList();
    }

    private static HashSet<string> Names(ModelParts m, ModelPart p)
        => Enumerable.Range(0, 32).Where(b => (p.AttributeMask & (1u << b)) != 0 && b < m.AttributeNames.Count)
                     .Select(b => m.AttributeNames[b]).ToHashSet();

    private static HashSet<(Vector3, Vector3, Vector3)> Faces(ModelParts m, ModelPart p)
    {
        Vector3 At(int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        return Enumerable.Range(0, p.Triangles.Length / 3)
                         .Select(t => (At(p.Triangles[t * 3]), At(p.Triangles[t * 3 + 1]), At(p.Triangles[t * 3 + 2]))).ToHashSet();
    }

    [Fact]
    [Trait("Plan", "AC-genital-switches-1")]
    [Trait("Plan", "EDGE-genital-switches-6")]
    public void Switched_triangles_are_copied_once_per_set()
    {
        var body = SyntheticBulge.Body();
        var bodyParts = ModelPartReader.Read(body)!;
        var dome = bodyParts.Parts.Single(p => p.Island < 0 && p.Label == "1.2");
        var plainOut = Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial), out _);
        var outBytes = Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial,
            Switches: [new SubmeshSwitch(dome.Mesh, dome.Submesh, null, [EJ, FJ])]), out var report);
        var m = ModelPartReader.Read(outBytes)!;
        var plain = ModelPartReader.Read(plainOut)!;
        var parts = LayerParts(m);
        Assert.Equal(3, parts.Count);   // the main skin, then one copy of the dome per set (the dome's own submesh is empty)
        Assert.Empty(Names(m, parts[0]));
        Assert.Equal(EJ, Names(m, parts[1]));
        Assert.Equal(FJ, Names(m, parts[2]));
        var domeFaces = Faces(plain, LayerParts(plain)[1]);
        Assert.Equal(domeFaces, Faces(m, parts[1]));
        Assert.Equal(domeFaces, Faces(m, parts[2]));
        Assert.Equal(parts[1].Triangles, parts[2].Triangles);   // the same vertices, indexed again
        Assert.Equal(plain.Positions.Length, m.Positions.Length); // no new vertices
        Assert.Equal(2, report.SwitchCopies);

        // A subset by ordinal: the rest stays in the dome's own submesh, untagged.
        var half = Enumerable.Range(0, dome.Ordinals.Length / 2).Select(k => dome.Ordinals[k]).ToHashSet();
        var subset = ModelPartReader.Read(Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial,
            Switches: [new SubmeshSwitch(dome.Mesh, dome.Submesh, half, [EJ])]), out _))!;
        var sp = LayerParts(subset);
        Assert.Equal(3, sp.Count);
        Assert.Empty(Names(subset, sp[1]));
        Assert.Equal(dome.Ordinals.Length - half.Count, sp[1].Triangles.Length / 3);
        Assert.Equal(EJ, Names(subset, sp[2]));
        Assert.Equal(half.Count, sp[2].Triangles.Length / 3);
    }

    [Fact]
    [Trait("Plan", "AC-genital-switches-7")]
    public void No_switches_is_byte_identical()
    {
        var body = SyntheticBulge.Body();
        string plain = Hash(Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial), out _));
        Assert.Equal(plain, Hash(Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial, Switches: null), out _)));
        Assert.Equal(plain, Hash(Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial, Switches: []), out _)));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-9")]
    public void A_name_without_a_slot_throws()
    {
        // A host whose attribute table is full (32 names), and a switch tag it does not carry.
        var names = Enumerable.Range(0, 32).Select(i => $"atrx_full_{i:00}").ToList();
        var body = SyntheticBulge.Body();
        var bp = ModelPartReader.Read(body)!;
        var host = SyntheticModel.BuildSurface(names, new SyntheticModel.SurfaceMesh(BulgeRefitTests.Cloth,
            [new Vector3(0, 1, 0), new Vector3(0.1f, 1, 0), new Vector3(0, 1.1f, 0)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [[("j_kosi", 1f)], [("j_kosi", 1f)], [("j_kosi", 1f)]], [(0xFFFFFFFFu, new[] { 0, 1, 2 })]));
        var dome = bp.Parts.Single(p => p.Island < 0 && p.Label == "1.2");
        var e = Assert.Throws<InvalidOperationException>(() => Build(host, new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial,
            Switches: [new SubmeshSwitch(dome.Mesh, dome.Submesh, null, [EJ])]), out _));
        Assert.Contains("has no attribute slot", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-40")]
    public void Switches_with_own_attributes_throw()
    {
        var body = SyntheticBulge.Body();
        var dome = ModelPartReader.Read(body)!.Parts.Single(p => p.Island < 0 && p.Label == "1.2");
        Assert.Throws<InvalidOperationException>(() => Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial,
            OwnAttributes: true, Switches: [new SubmeshSwitch(dome.Mesh, dome.Submesh, null, [EJ])]), out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-7")]
    public void Switched_copy_keeps_todays_other_tags()
    {
        // A body piece tagged with a variant tag (its body's option) and atr_hiz: with the variant tags dropped, as a refit
        // drops them, each copy carries atr_hiz and its set, never the variant tag.
        var tri = new[] { new Vector3(0, 1, 0), new Vector3(0.1f, 1, 0), new Vector3(0, 1.1f, 0), new Vector3(0.1f, 1.1f, 0) };
        var body = SyntheticModel.BuildSurface(["atr_tv_a", "atr_hiz"], new SyntheticModel.SurfaceMesh(SyntheticBulge.Skin, tri,
            [.. tri.Select(_ => Vector3.UnitZ)], [.. tri.Select(p => new Vector2(p.X, p.Y))], [.. tri.Select(_ => new[] { ("j_kosi", 1f) })],
            [(0u, new[] { 0, 1, 2 }), (3u, new[] { 1, 3, 2 })]));
        var piece = ModelPartReader.Read(body)!.Parts.Single(p => p.Island < 0 && p.Submesh == 1);
        var m = ModelPartReader.Read(Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial,
            DropVariantAttributes: true, Switches: [new SubmeshSwitch(piece.Mesh, piece.Submesh, null, [EJ])]), out _))!;
        var parts = LayerParts(m);
        Assert.Equal(new HashSet<string> { "atr_hiz", "atr_dv_e", "atr_dv_j" }, Names(m, parts[^1]));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-14")]
    public void Copies_follow_the_cut()
    {
        // The cut keeps the main skin and the dome's +x half: the copies carry only the dome triangles that survive it.
        var body = SyntheticBulge.Body();
        var bp = ModelPartReader.Read(body)!;
        var dome = bp.Parts.Single(p => p.Island < 0 && p.Label == "1.2");
        var main = bp.Parts.Single(p => p.Island < 0 && p.Label == "1.1");
        float midX = dome.Triangles.Average(v => bp.Positions[v * 3]);
        var keepVerts = main.Triangles.Concat(dome.Triangles.Where(v => bp.Positions[v * 3] > midX + 0.01f))
                            .Select(v => (ushort)v).ToHashSet();
        var drawOnly = new Dictionary<int, HashSet<ushort>> { [dome.Mesh] = keepVerts };
        var plain = ModelPartReader.Read(Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial, DrawOnly: drawOnly), out _))!;
        var cut = ModelPartReader.Read(Build(Host(), new ContentGeometry(body, SecondSkinWriter.IsBodySkinMaterial, DrawOnly: drawOnly,
            Switches: [new SubmeshSwitch(dome.Mesh, dome.Submesh, null, [EJ, FJ])]), out _))!;
        int keptDome = LayerParts(plain)[1].Triangles.Length / 3;
        Assert.True(keptDome > 0 && keptDome < dome.Triangles.Length / 3, $"the cut kept {keptDome} of {dome.Triangles.Length / 3}");
        var cp = LayerParts(cut);
        Assert.Equal(keptDome, cp[1].Triangles.Length / 3);
        Assert.Equal(keptDome, cp[2].Triangles.Length / 3);
    }
}
