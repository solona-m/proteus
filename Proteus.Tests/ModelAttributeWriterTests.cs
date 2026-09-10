using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

    /// <summary>
    /// A duplicate name used to throw. It does not any more: the name is already in the table, so the
    /// submesh is tagged against the bit it has and no second copy is added. Refusing abandoned the whole
    /// patch on any hair mod that shipped the attribute already, which is most of them.
    /// </summary>
    [Fact]
    public void AddAttribute_DoesNotAddASecondCopyOfANameItAlreadyHas()
    {
        var model = SyntheticModel.Build(["atr_tv_a"], Mesh(new SyntheticModel.Sub(0)));

        var after = ModelAttributeWriter.AddAttribute(model, "atr_tv_a", [(0, 0)]);

        Assert.Single(SecondSkinWriter.Parse(after).AttrNames, "atr_tv_a");
        Assert.Equal(model.Length, after.Length);
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

    // ── an attribute the model already declares ─────────────────────────────

    /// <summary>
    /// A name already in the table is tagged against the bit it has, not refused. Hair mods ship
    /// <c>atr_kam</c> without a hat shape routinely, and refusing abandoned the whole patch.
    /// </summary>
    [Fact]
    public void AddAttribute_ReusesABitTheModelAlreadyDeclares()
    {
        var model = SyntheticModel.Build(["atr_kam"],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0)));
        var before = SecondSkinWriter.Parse(model);
        int bit = Array.IndexOf(before.AttrNames, "atr_kam");
        Assert.True(bit >= 0);

        var after = ModelAttributeWriter.AddAttribute(model, "atr_kam", [(0, 1)]);
        var src = SecondSkinWriter.Parse(after);

        // Same table, same length: nothing was inserted, so no offset moved.
        Assert.Equal(before.AttrNames, src.AttrNames);
        Assert.Equal(model.Length, after.Length);
        // The named submesh carries it and its neighbour does not.
        var parts = ModelPartReader.Read(after)!;
        Assert.Equal(0u, parts.Parts[0].AttributeMask & (1u << bit));
        Assert.NotEqual(0u, parts.Parts[1].AttributeMask & (1u << bit));
    }

    /// <summary>
    /// The reuse path needs no free slot, so a full attribute table must not block it — that refused the one
    /// model shape it exists to rescue.
    /// </summary>
    [Fact]
    public void AddAttribute_ReusesABitEvenWhenTheTableIsFull()
    {
        var names = Enumerable.Range(0, ModelAttributeWriter.MaxAttributes - 1)
            .Select(i => $"atr_x{i}").Append("atr_kam").ToArray();
        var model = SyntheticModel.Build(names, Mesh(new SyntheticModel.Sub(0)));
        Assert.Equal(ModelAttributeWriter.MaxAttributes, SecondSkinWriter.Parse(model).AttrNames.Length);

        var after = ModelAttributeWriter.AddAttribute(model, "atr_kam", [(0, 0)]);

        int bit = Array.IndexOf(SecondSkinWriter.Parse(after).AttrNames, "atr_kam");
        Assert.NotEqual(0u, ModelPartReader.Read(after)!.Parts[0].AttributeMask & (1u << bit));
        // A genuinely NEW name still cannot fit.
        Assert.Throws<ModelAttributeWriter.ModelEditException>(
            () => ModelAttributeWriter.AddAttribute(model, "atr_new", [(0, 0)]));
    }

    /// <summary>
    /// Clearing takes the bit off LOD0 and leaves the name in the table, so a following add can reuse it.
    /// </summary>
    [Fact]
    public void ClearAttribute_TakesTheBitOffAndKeepsTheName()
    {
        var model = SyntheticModel.Build(["atr_kam"],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0)));
        var tagged = ModelAttributeWriter.AddAttribute(model, "atr_kam", [(0, 0), (0, 1)]);
        int bit = Array.IndexOf(SecondSkinWriter.Parse(tagged).AttrNames, "atr_kam");
        Assert.All(ModelPartReader.Read(tagged)!.Parts, p => Assert.NotEqual(0u, p.AttributeMask & (1u << bit)));

        var cleared = ModelAttributeWriter.ClearAttribute(tagged, "atr_kam");

        Assert.All(ModelPartReader.Read(cleared)!.Parts, p => Assert.Equal(0u, p.AttributeMask & (1u << bit)));
        // The NAME survives — the table is untouched, so the bit can be handed straight back out.
        Assert.Contains("atr_kam", SecondSkinWriter.Parse(cleared).AttrNames);
        Assert.Equal(tagged.Length, cleared.Length);

        var retagged = ModelAttributeWriter.AddAttribute(cleared, "atr_kam", [(0, 1)]);
        var parts = ModelPartReader.Read(retagged)!;
        Assert.Equal(0u, parts.Parts[0].AttributeMask & (1u << bit));
        Assert.NotEqual(0u, parts.Parts[1].AttributeMask & (1u << bit));
    }

    [Fact]
    public void ClearAttribute_LeavesAModelThatNeverDeclaredItAlone()
    {
        var model = SyntheticModel.Build(["atr_tv_a"], Mesh(new SyntheticModel.Sub(0)));
        Assert.Same(model, ModelAttributeWriter.ClearAttribute(model, "atr_kam"));
    }

    // ── RegroupSubmesh ──────────────────────────────────────────────────────

    /// <summary>
    /// The case the plain split refuses, and the reason this exists: a set chosen by geometry rather than by
    /// the author's layout, scattered right through the index buffer.
    /// </summary>
    [Fact]
    public void RegroupSubmesh_TakesGeometryTooInterleavedToSplit()
    {
        const int tris = ModelAttributeWriter.MaxRuns + 4;
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: tris)));
        var wanted = Enumerable.Range(0, tris).Where(i => i % 2 == 0).ToHashSet();

        var (after, groups) = ModelAttributeWriter.RegroupSubmesh(
            model, 0, 0, t => wanted.Contains(t) ? 0 : -1);

        // Two records, whatever the interleaving was: the wanted triangles, then the rest.
        var parts = ModelPartReader.Read(after)!;
        Assert.Equal([0], groups[0]);
        Assert.Equal(2, parts.Parts.Count);
        Assert.Equal(wanted.Count, parts.Parts[0].TriangleCount);
        Assert.Equal(tris - wanted.Count, parts.Parts[1].TriangleCount);
    }

    /// <summary>
    /// The permutation must be of whole triples and must lose none of them — a reorder that dropped or
    /// duplicated a triangle would show up in game as a hole, not as an error.
    /// </summary>
    [Fact]
    public void RegroupSubmesh_KeepsEveryTriangleExactlyOnce()
    {
        const int tris = 12;
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: tris)));
        var wanted = new HashSet<int> { 1, 4, 5, 9 };
        var before = ModelPartReader.Read(model)!.Parts[0].Triangles;

        var (after, groups) = ModelAttributeWriter.RegroupSubmesh(
            model, 0, 0, t => wanted.Contains(t) ? 0 : -1);
        var parts = ModelPartReader.Read(after)!;

        static IEnumerable<(int, int, int)> Triples(IEnumerable<int> ix)
        {
            var a = ix.ToArray();
            for (int i = 0; i < a.Length; i += 3) yield return (a[i], a[i + 1], a[i + 2]);
        }

        // Same multiset of triples, and each corner still with its own two — only the order differs.
        Assert.Equal(
            Triples(before).OrderBy(t => t).ToArray(),
            Triples(parts.Parts.SelectMany(p => p.Triangles)).OrderBy(t => t).ToArray());
        // And the record the caller was handed holds exactly the triangles it asked for. Which record that
        // is depends on where the first wanted triangle falls, so it has to come from the return value.
        var mine = Assert.Single(groups[0]);
        Assert.Equal(Triples(before).Where((_, i) => wanted.Contains(i)).OrderBy(t => t).ToArray(),
                     Triples(parts.Parts[mine].Triangles).OrderBy(t => t).ToArray());
    }

    [Fact]
    public void RegroupSubmesh_LeavesAnAlreadyGroupedSubmeshAlone()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 6)));

        // A leading run needs no reorder, so this must behave exactly like the plain split.
        var (after, groups) = ModelAttributeWriter.RegroupSubmesh(
            model, 0, 0, t => t < 2 ? 0 : -1);
        var (split, subs) = ModelAttributeWriter.SplitSubmesh(model, 0, 0, new HashSet<int> { 0, 1 });

        Assert.Equal(subs, groups[0]);
        Assert.Equal(split, after);
    }

    /// <summary>
    /// A shape names index-buffer SLOTS, so a reorder underneath one rewires it to the wrong corners unless
    /// the values are carried through the same permutation. Silent in every other way — the model loads, the
    /// counts add up, and the deformation lands on unrelated triangles.
    /// </summary>
    [Fact]
    public void RegroupSubmesh_CarriesAnExistingShapeThroughThePermutation()
    {
        var model = SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, TrianglesPerIsland: 8)));
        var moved = new Dictionary<int, IReadOnlyDictionary<int, Vector3>>
        {
            [0] = new Dictionary<int, Vector3> { [1] = new(9f, 9f, 9f) },
        };
        var shaped = ModelAttributeWriter.AddShape(model, "shp_test", moved);
        var wanted = new HashSet<int> { 0, 3, 6 };

        var (after, _) = ModelAttributeWriter.RegroupSubmesh(shaped, 0, 0, t => wanted.Contains(t) ? 0 : -1);

        // The slots a shape value names must still hold the vertex that value replaces. Read them back
        // through the file itself rather than trusting the arithmetic that wrote them.
        Assert.Equal(ShapeTargets(shaped).OrderBy(v => v), ShapeTargets(after).OrderBy(v => v));
    }

    /// <summary>
    /// For each of the model's shape values, the vertex index sitting in the slot it names — which is what
    /// has to survive a reorder, since that is the vertex the shape swaps out.
    /// </summary>
    private static List<int> ShapeTargets(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int shapeCount = BitConverter.ToUInt16(mdl, src.Mh + 16);
        int shapeMeshCount = BitConverter.ToUInt16(mdl, src.Mh + 18);
        int shapeValueCount = BitConverter.ToUInt16(mdl, src.Mh + 20);
        int meshBlock = src.ShapeBlock + shapeCount * 16;
        int valBlock = meshBlock + shapeMeshCount * 12;

        var found = new List<int>();
        for (int m = 0; m < shapeMeshCount; m++)
        {
            uint at = BitConverter.ToUInt32(mdl, meshBlock + m * 12);
            uint count = BitConverter.ToUInt32(mdl, meshBlock + m * 12 + 4);
            uint start = BitConverter.ToUInt32(mdl, meshBlock + m * 12 + 8);
            for (uint v = start; v < start + count && v < shapeValueCount; v++)
                found.Add(BitConverter.ToUInt16(
                    mdl, src.Ib + (int)(at + BitConverter.ToUInt16(mdl, valBlock + (int)v * 4)) * 2));
        }
        return found;
    }
}
