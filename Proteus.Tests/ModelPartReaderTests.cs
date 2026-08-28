using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What a model offers as toggleable pieces. The island split carries the weight here: it is what makes the
/// feature work on the mods it exists for, which are exactly the ones whose author never split anything.
/// </summary>
public class ModelPartReaderTests
{
    private static SyntheticModel.Mesh Mesh(params SyntheticModel.Sub[] subs)
        => new("/mt_test.mtrl", subs);

    [Fact]
    public void EachSubmesh_IsAPart()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));

        Assert.NotNull(parts);
        Assert.Equal(["1.1", "1.2"], parts!.Parts.Select(p => p.Label));
        Assert.All(parts.Parts, p => Assert.Equal("/mt_test.mtrl", p.Material));
        Assert.All(parts.Parts, p => Assert.Equal(-1, p.Island));
    }

    [Fact]
    public void MeshOrdinal_CountsMeshes_SubmeshOrdinal_CountsWithinIt()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0)),
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));

        Assert.Equal(["1.1", "2.1", "2.2"], parts!.Parts.Select(p => p.Label));
    }

    /// <summary>
    /// The case the whole feature turns on: one submesh holding two separate objects, whose triangles share
    /// corner POSITIONS but never a vertex index. An index-based split would report six islands of one
    /// triangle each and the list would be useless.
    /// </summary>
    [Fact]
    public void OneSubmesh_SplitsIntoIslands_AcrossDuplicatedVertices()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 2, TrianglesPerIsland: 3))));

        Assert.Equal(["1.1", "1.1.1", "1.1.2"], parts!.Parts.Select(p => p.Label));
        Assert.Equal(6, parts.Parts[0].TriangleCount);
        Assert.Equal(3, parts.Parts[1].TriangleCount);
        Assert.Equal(3, parts.Parts[2].TriangleCount);
        Assert.Equal([0, 1], parts.Parts.Skip(1).Select(p => p.Island));
    }

    [Fact]
    public void Islands_AreSeparatedInSpace()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 2, TrianglesPerIsland: 2))));

        var a = parts!.Parts.Single(p => p.Label == "1.1.1");
        var b = parts.Parts.Single(p => p.Label == "1.1.2");
        Assert.True(a.Max.X < b.Min.X, $"islands overlap in X: {a.Max.X} .. {b.Min.X}");
        // The submesh row still spans both.
        Assert.Equal(parts.Parts[0].Min.X, a.Min.X);
        Assert.Equal(parts.Parts[0].Max.X, b.Max.X);
    }

    /// <summary>One island IS the submesh, and listing it again would be a row that toggles the row above it.</summary>
    [Fact]
    public void ASingleIsland_IsNotOfferedSeparately()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: 5))));

        Assert.Equal(["1.1"], parts!.Parts.Select(p => p.Label));
        Assert.Empty(parts.ShatteredSubmeshes);
    }

    /// <summary>
    /// The case the cap used to break. A pair of trousers put 78 belt straps in one submesh; the old limit
    /// of 64 suppressed every island and offered only the whole 53,000-triangle piece — precisely the
    /// "dependent on the authored subgrouping" outcome the feature exists to avoid.
    /// </summary>
    [Fact]
    public void ManyIslands_AreAllListed()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 78, TrianglesPerIsland: 2))));

        Assert.Equal(79, parts!.Parts.Count);           // the submesh, plus one row per strap
        Assert.Empty(parts.ShatteredSubmeshes);
        Assert.Equal("1.1.78", parts.Parts[^1].Label);  // numbered, because letters run out at 26
        Assert.All(parts.Parts.Skip(1), p => Assert.Equal(2, p.TriangleCount));
    }

    /// <summary>The bound that remains is a backstop against a model whose every triangle is its own island,
    /// not a judgement about how many pieces are useful.</summary>
    [Fact]
    public void ABsurdlyShatteredSubmesh_IsReported_NotListed()
    {
        int many = ModelPartReader.MaxIslands + 1;
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: many))));

        Assert.Equal(["1.1"], parts!.Parts.Select(p => p.Label));
        Assert.Equal(many, parts.ShatteredSubmeshes["1.1"]);
    }

    /// <summary>A submesh the author already gates is listed, so the user can see it, but not claimable.</summary>
    [Fact]
    public void AnAlreadyTaggedSubmesh_IsNotToggleable()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build(["atr_tv_a"],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(1))));

        Assert.True(parts!.Parts[0].Toggleable);
        Assert.False(parts.Parts[1].Toggleable);
    }

    [Fact]
    public void FreeLetters_ExcludesTheOnesTheModelAlreadyUses()
    {
        Assert.Equal(
            ['b', 'd', 'e', 'f', 'g', 'h', 'i', 'j'],
            ModelPartReader.FreeLetters(["atr_tv_a", "atr_tv_c"]));
    }

    /// <summary>
    /// Body-suppression attributes answer to no IMC bit — see <c>SecondSkinService.PartAttributeBit</c> — so
    /// they cost nothing from a budget that is only ten wide.
    /// </summary>
    [Fact]
    public void FreeLetters_IgnoresAttributesThatAnswerToNoBit()
    {
        Assert.Equal(10, ModelPartReader.FreeLetters(["atr_hij", "atr_nek", "atr_ude"]).Count);
    }

    [Fact]
    public void AModelThatCannotBeParsed_ReadsAsNull()
        => Assert.Null(ModelPartReader.Read([1, 2, 3, 4]));

    /// <summary>
    /// The ordinals are what an edit addresses triangles by, so they have to partition the submesh exactly:
    /// every triangle in one island and none in two.
    /// </summary>
    [Fact]
    public void IslandOrdinals_PartitionTheSubmesh()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 2, TrianglesPerIsland: 2))));

        var whole = parts!.Parts[0];
        var a = parts.Parts[1].Ordinals;
        var b = parts.Parts[2].Ordinals;

        Assert.Equal([0, 1, 2, 3], whole.Ordinals);
        Assert.Empty(a.Intersect(b));
        Assert.Equal([0, 1, 2, 3], a.Concat(b).Order());
        Assert.All(parts.Parts, p => Assert.Equal(p.TriangleCount, p.Ordinals.Length));
    }

    [Fact]
    public void PartTriangles_IndexTheModelWideVertexArray()
    {
        var parts = ModelPartReader.Read(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0)),
            Mesh(new SyntheticModel.Sub(0))));

        int vertices = parts!.Positions.Length / 3;
        Assert.All(parts.Parts, p => Assert.All(p.Triangles, v => Assert.InRange(v, 0, vertices - 1)));

        // The second mesh's triangles must have been rebased past the first mesh's vertices, or both parts
        // would draw on top of each other.
        Assert.True(parts.Parts[1].Triangles.Min() >= parts.Parts[0].Triangles.Max());
    }
}
