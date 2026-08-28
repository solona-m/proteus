using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Isolate preview. Its whole claim is that it changes what renders WITHOUT changing the file's shape,
/// so these tests check both halves: the right triangles went, and nothing else moved.
/// </summary>
public class ModelPartWriterTests
{
    private static SyntheticModel.Mesh Mesh(params SyntheticModel.Sub[] subs)
        => new("/mt_test.mtrl", subs);

    /// <summary>All three corners equal — zero area, rasterizes nothing.</summary>
    private static bool IsDegenerate(ModelPart p, int k)
        => p.Triangles[k * 3] == p.Triangles[k * 3 + 1] && p.Triangles[k * 3] == p.Triangles[k * 3 + 2];

    private static int DegenerateCount(ModelPart p)
        => Enumerable.Range(0, p.TriangleCount).Count(k => IsDegenerate(p, k));

    [Fact]
    public void Isolate_LeavesTheKeptSubmesh_AndFlattensTheRest()
    {
        var model = SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 3), new SyntheticModel.Sub(0, TrianglesPerIsland: 2)));
        var parts = ModelPartReader.Read(model)!;

        var isolated = ModelPartWriter.Isolate(model, [parts.Parts.Single(p => p.Label == "1.1")]);
        var after = ModelPartReader.Read(isolated!)!;

        Assert.Equal(0, DegenerateCount(after.Parts.Single(p => p.Label == "1.1")));
        var hidden = after.Parts.Single(p => p.Label == "1.2");
        Assert.Equal(hidden.TriangleCount, DegenerateCount(hidden));
    }

    /// <summary>
    /// Nothing structural may move. This is the property that makes it safe to publish over a mod's model
    /// on a button press: same length, same header, same tables — only index ENTRIES differ.
    /// </summary>
    [Fact]
    public void Isolate_ChangesNoStructure()
    {
        var model = SyntheticModel.Build(["atr_tv_a"],
            Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 3)),
            Mesh(new SyntheticModel.Sub(1, TrianglesPerIsland: 2)));
        var parts = ModelPartReader.Read(model)!;

        var isolated = ModelPartWriter.Isolate(model, [parts.Parts[0]])!;
        var after = ModelPartReader.Read(isolated)!;

        Assert.Equal(model.Length, isolated.Length);
        Assert.Equal(parts.Parts.Select(p => p.Label), after.Parts.Select(p => p.Label));
        Assert.Equal(parts.Parts.Select(p => p.TriangleCount), after.Parts.Select(p => p.TriangleCount));
        Assert.Equal(parts.Parts.Select(p => p.AttributeMask), after.Parts.Select(p => p.AttributeMask));
        Assert.Equal(parts.Positions, after.Positions);
    }

    /// <summary>
    /// Half a submesh: an island survives while its siblings in the SAME index range go. This is the case
    /// the ordinals exist for, and getting it wrong flattens the wrong bow.
    /// </summary>
    [Fact]
    public void Isolate_CanKeepOneIslandOfASubmesh()
    {
        var model = SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 3, TrianglesPerIsland: 2)));
        var parts = ModelPartReader.Read(model)!;
        var wanted = parts.Parts.Single(p => p.Label == "1.1b");

        var isolated = ModelPartWriter.Isolate(model, [wanted])!;
        var after = ModelPartReader.Read(isolated)!;

        var whole = after.Parts.Single(p => p.Label == "1.1");
        // Four of the six triangles are gone; the two that stay are exactly the island's own ordinals.
        Assert.Equal(4, DegenerateCount(whole));
        Assert.All(wanted.Ordinals, o => Assert.False(IsDegenerate(whole, o)));
    }

    [Fact]
    public void Isolate_KeepingNothing_FlattensEverything()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 4)));
        var after = ModelPartReader.Read(ModelPartWriter.Isolate(model, [])!)!;

        var whole = after.Parts.Single(p => p.Label == "1.1");
        Assert.Equal(whole.TriangleCount, DegenerateCount(whole));
    }

    [Fact]
    public void Isolate_OnAnUnreadableModel_IsNull()
        => Assert.Null(ModelPartWriter.Isolate([1, 2, 3, 4], []));
}
