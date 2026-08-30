using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The structural edits. Both grow the file, so most of what is checked here is that everything which did
/// NOT change is still exactly where the game will look for it — the vertex and index buffers byte for byte,
/// the absolute offsets that point at them, and every table in between.
/// </summary>
public class ModelAttributeWriterTests
{
    private static SyntheticModel.Mesh Mesh(params SyntheticModel.Sub[] subs)
        => new("/mt_test.mtrl", subs);

    /// <summary>
    /// The header's own statement of where the vertex and index data begin, and the LODs' copies of it. If a
    /// shift is missed, one of these stops landing on the data — which in game is a model that either fails
    /// to load or goes missing at distance.
    /// </summary>
    private static void AssertOffsetsLandOnTheirData(byte[] before, byte[] after, int delta)
    {
        uint vtxBefore = BitConverter.ToUInt32(before, 16), idxBefore = BitConverter.ToUInt32(before, 28);
        uint vtxSize = BitConverter.ToUInt32(before, 40), idxSize = BitConverter.ToUInt32(before, 52);

        uint vtxAfter = BitConverter.ToUInt32(after, 16), idxAfter = BitConverter.ToUInt32(after, 28);
        Assert.Equal(vtxBefore + (uint)delta, vtxAfter);
        Assert.Equal(idxBefore + (uint)delta, idxAfter);

        // The buffers themselves are untouched — this is what proves the offsets still mean the same thing.
        Assert.Equal(
            before.Skip((int)vtxBefore).Take((int)vtxSize).ToArray(),
            after.Skip((int)vtxAfter).Take((int)vtxSize).ToArray());
        Assert.Equal(
            before.Skip((int)idxBefore).Take((int)idxSize).ToArray(),
            after.Skip((int)idxAfter).Take((int)idxSize).ToArray());

        // RuntimeSize is defined as the gap between the header and the vertex data, so it grew by the same.
        Assert.Equal(
            BitConverter.ToUInt32(before, 8) + (uint)delta, BitConverter.ToUInt32(after, 8));
        Assert.Equal(BitConverter.ToUInt32(before, 4), BitConverter.ToUInt32(after, 4));   // StackSize unchanged
    }

    // ── AddAttribute ────────────────────────────────────────────────────────

    [Fact]
    public void AddAttribute_AppendsTheName_AndTagsOnlyTheNamedSubmeshes()
    {
        var model = SyntheticModel.Build(["atr_tv_a"],
            Mesh(new SyntheticModel.Sub(1), new SyntheticModel.Sub(0), new SyntheticModel.Sub(0)));

        var after = ModelAttributeWriter.AddAttribute(model, "atr_tv_b", [(0, 2)]);

        Assert.Equal(["atr_tv_a", "atr_tv_b"], SecondSkinWriter.AttributeNames(after));

        var parts = ModelPartReader.Read(after)!;
        Assert.Equal(1u, parts.Parts[0].AttributeMask);   // untouched
        Assert.Equal(0u, parts.Parts[1].AttributeMask);   // untouched
        Assert.Equal(2u, parts.Parts[2].AttributeMask);   // bit 1 = the new attribute's table position
    }

    [Fact]
    public void AddAttribute_MovesEveryAbsoluteOffsetWithTheData()
    {
        var model = SyntheticModel.Build(["atr_tv_a"],
            Mesh(new SyntheticModel.Sub(0)), Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0)));

        var after = ModelAttributeWriter.AddAttribute(model, "atr_tv_c", [(1, 0)]);

        AssertOffsetsLandOnTheirData(model, after, after.Length - model.Length);
    }

    /// <summary>The tables that come after the string block must still parse — this is the whole risk.</summary>
    [Fact]
    public void AddAttribute_LeavesTheRestOfTheModelReadable()
    {
        var model = SyntheticModel.Build(["atr_hij", "atr_tv_a"],
            Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 3)),
            Mesh(new SyntheticModel.Sub(2), new SyntheticModel.Sub(0, Islands: 2)));
        var before = ModelPartReader.Read(model)!;

        var after = ModelPartReader.Read(ModelAttributeWriter.AddAttribute(model, "atr_tv_b", [(0, 0)]))!;

        Assert.Equal(before.Parts.Select(p => p.Label), after.Parts.Select(p => p.Label));
        Assert.Equal(before.Parts.Select(p => p.TriangleCount), after.Parts.Select(p => p.TriangleCount));
        Assert.Equal(before.Parts.Select(p => p.Material), after.Parts.Select(p => p.Material));
        Assert.Equal(before.Positions, after.Positions);
        Assert.Equal(SecondSkinWriter.MaterialNames(model), SecondSkinWriter.MaterialNames(
            ModelAttributeWriter.AddAttribute(model, "atr_tv_b", [(0, 0)])));
    }

    /// <summary>
    /// The string block has to stay four-byte aligned or every u32 table behind it lands on an odd address.
    /// Names of different lengths are the way that goes wrong.
    /// </summary>
    [Theory]
    [InlineData("atr_b")]
    [InlineData("atr_tv_b")]
    [InlineData("atr_long_name_c")]
    public void AddAttribute_KeepsTheFileFourByteAligned(string name)
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0)));
        var after = ModelAttributeWriter.AddAttribute(model, name, [(0, 0)]);

        Assert.Equal(0, (after.Length - model.Length) % 4);
        Assert.Equal([name], SecondSkinWriter.AttributeNames(after));
        AssertOffsetsLandOnTheirData(model, after, after.Length - model.Length);
    }

    [Fact]
    public void AddAttribute_RefusesADuplicateName()
    {
        var model = SyntheticModel.Build(["atr_tv_a"], Mesh(new SyntheticModel.Sub(0)));
        Assert.Throws<ModelAttributeWriter.ModelEditException>(
            () => ModelAttributeWriter.AddAttribute(model, "atr_tv_a", [(0, 0)]));
    }

    [Fact]
    public void AddAttribute_RefusesASubmeshThatIsNotThere()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0)));
        Assert.Throws<ModelAttributeWriter.ModelEditException>(
            () => ModelAttributeWriter.AddAttribute(model, "atr_tv_a", [(0, 3)]));
    }

    [Fact]
    public void AddAttribute_CanBeAppliedRepeatedly()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0)));

        var once = ModelAttributeWriter.AddAttribute(model, "atr_tv_a", [(0, 0)]);
        var twice = ModelAttributeWriter.AddAttribute(once, "atr_tv_b", [(0, 1)]);

        Assert.Equal(["atr_tv_a", "atr_tv_b"], SecondSkinWriter.AttributeNames(twice));
        var parts = ModelPartReader.Read(twice)!;
        Assert.Equal(1u, parts.Parts[0].AttributeMask);
        Assert.Equal(2u, parts.Parts[1].AttributeMask);
        AssertOffsetsLandOnTheirData(model, twice, twice.Length - model.Length);
    }

    // ── SplitSubmesh ────────────────────────────────────────────────────────

    [Fact]
    public void SplitSubmesh_CutsAtRunBoundaries_AndKeepsEveryTriangle()
    {
        // Six triangles; ask for the middle two, which is one run inside the submesh -> three records.
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 6)));

        var (after, subs) = ModelAttributeWriter.SplitSubmesh(model, 0, 0, new HashSet<int> { 2, 3 });
        var parts = ModelPartReader.Read(after)!;

        Assert.Equal([1], subs);
        Assert.Equal(["1.1", "1.2", "1.3"], parts.Parts.Select(p => p.Label));
        Assert.Equal([2, 2, 2], parts.Parts.Select(p => p.TriangleCount));
        // Same triangles, same order, just described by three records instead of one.
        Assert.Equal(
            ModelPartReader.Read(model)!.Parts[0].Triangles,
            parts.Parts.SelectMany(p => p.Triangles).ToArray());
    }

    [Fact]
    public void SplitSubmesh_MovesNoIndexEntry()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));
        var (after, _) = ModelAttributeWriter.SplitSubmesh(model, 0, 0, new HashSet<int> { 0, 1 });

        // The index buffer is byte-identical: a shape key addresses positions in it, so a permutation here
        // would silently break every body slider the garment supports.
        AssertOffsetsLandOnTheirData(model, after, after.Length - model.Length);
        // Two runs (the wanted pair, then the rest) means one record more than before.
        Assert.Equal(16, after.Length - model.Length);
    }

    [Fact]
    public void SplitSubmesh_ThatChangesNothing_ReturnsTheModelUntouched()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 4)));

        // The whole submesh is one run, so there is nothing to cut.
        var (after, subs) = ModelAttributeWriter.SplitSubmesh(model, 0, 0, new HashSet<int> { 0, 1, 2, 3 });

        Assert.Same(model, after);
        Assert.Equal([0], subs);
    }

    /// <summary>
    /// The later meshes' submesh indices have to follow the insert, or a mesh ends up drawing another's
    /// records.
    /// </summary>
    [Fact]
    public void SplitSubmesh_RenumbersTheMeshesAfterIt()
    {
        var model = SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 4)),
            Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 2), new SyntheticModel.Sub(0)));
        var before = ModelPartReader.Read(model)!;

        var (after, _) = ModelAttributeWriter.SplitSubmesh(model, 0, 0, new HashSet<int> { 0 });
        var parts = ModelPartReader.Read(after)!;

        // Mesh 1 is unaffected: same parts, same sizes, same geometry.
        Assert.Equal(["2.1", "2.2"], parts.Parts.Where(p => p.Label.StartsWith("2.")).Select(p => p.Label));
        Assert.Equal(
            before.Parts.Where(p => p.Label.StartsWith("2.")).Select(p => p.Triangles),
            parts.Parts.Where(p => p.Label.StartsWith("2.")).Select(p => p.Triangles));
    }

    [Fact]
    public void SplitSubmesh_ThenAddAttribute_TagsOnlyThePieceThatWasSplitOut()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 6)));

        var (split, subs) = ModelAttributeWriter.SplitSubmesh(model, 0, 0, new HashSet<int> { 2, 3 });
        var after = ModelAttributeWriter.AddAttribute(split, "atr_tv_a", subs.Select(s => (0, s)).ToList());

        var parts = ModelPartReader.Read(after)!;
        Assert.Equal([0u, 1u, 0u], parts.Parts.Select(p => p.AttributeMask));
        AssertOffsetsLandOnTheirData(model, after, after.Length - model.Length);
    }

    [Fact]
    public void SplitSubmesh_RefusesGeometryTooInterleavedToCut()
    {
        var model = SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: ModelAttributeWriter.MaxRuns + 4)));

        // Every other triangle -> a run each, far past what a real island looks like.
        var alternating = Enumerable.Range(0, ModelAttributeWriter.MaxRuns + 4).Where(i => i % 2 == 0).ToHashSet();
        Assert.Throws<ModelAttributeWriter.ModelEditException>(
            () => ModelAttributeWriter.SplitSubmesh(model, 0, 0, alternating));
    }
}
