using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="SecondSkinWriter.Parse"/> against both .mdl versions.
/// <para/>
/// Bone tables are the one block whose layout changed at Dawntrail: v6 stores a 4-byte header per table
/// plus ONE shared pool of indices (whose total size the model header carries), while v5 stores a fixed
/// 132-byte struct per table and no pool at all. Everything the parser reads afterwards — the shape block,
/// the submesh bone map, the bounding boxes — is positioned relative to the end of that block, so a reader
/// that assumes v6 walks the wrong distance through a v5 model and every later read lands mid-file.
/// <para/>
/// The symptom is not a clean failure. The shape block comes out as arbitrary bytes, a shape's name offset
/// points outside the string table, and the name read runs off the end of the array — an
/// IndexOutOfRangeException from a model that is perfectly well formed. Mods still ship v5 models
/// (Rinoa's c0201e6019_top.mdl is one), and equipping one killed the whole second-skin build.
/// </summary>
public class ModelVersionParseTests
{
    /// <summary>
    /// A shape key is deliberately present: the shape block sits immediately after the bone table, so it is
    /// where a wrong bone-table width first bites, and a shape is located by a name offset into the string
    /// block — which is what turns the misalignment into a read off the end of the array rather than merely
    /// wrong data. Garment models carry shape keys as a matter of course.
    /// </summary>
    private static byte[] Model(uint version) => SyntheticModel.Build(
        ["atr_top"],
        [new SyntheticModel.Mesh("/mt_c0201b0001_a.mtrl", new SyntheticModel.Sub(0))],
        version,
        ["shp_base"]);

    [Theory]
    [InlineData(SyntheticModel.V6)]
    [InlineData(SyntheticModel.V5)]
    public void Parse_reads_both_model_versions(uint version)
    {
        var src = SecondSkinWriter.Parse(Model(version));

        // Landed in the right place: the material name is read through the string block, and the bone table
        // is the block whose width the version decides.
        Assert.Equal("/mt_c0201b0001_a.mtrl", Assert.Single(src.MatNames));
        Assert.Equal(1, src.BoneCount);
        Assert.Equal("n_root", Assert.Single(src.BoneNames));
        Assert.Equal(1, src.MeshCount);

        // The table's CONTENTS, not just that one exists: reading a v5 table with the v6 layout finds a
        // zero offset and a zero size, so it comes back empty rather than throwing.
        Assert.Single(src.BoneTables);
        Assert.Equal([0], src.BoneTables[0]);
    }

    /// <summary>
    /// The block AFTER the bone table is where a wrong width actually shows up, so assert the parser gets
    /// there intact rather than only that it didn't throw. A fixture with no shapes must report none — a
    /// mis-walked v5 model instead finds garbage shape counts and either throws or invents shapes.
    /// </summary>
    [Theory]
    [InlineData(SyntheticModel.V6)]
    [InlineData(SyntheticModel.V5)]
    public void The_blocks_after_the_bone_table_still_line_up(uint version)
    {
        var src = SecondSkinWriter.Parse(Model(version));

        // The shape's NAME is the assertion: it is read through an offset into the string block, so it only
        // comes out right if the parser reached the shape block at the correct position.
        Assert.Equal("shp_base", Assert.Single(src.Shapes).Key);
        Assert.Empty(src.SubmeshBoneMap);
        Assert.Equal("atr_top", Assert.Single(src.AttrNames));
    }

    /// <summary>A v5 model still yields usable LOD0 geometry — the reader the mirror detection asks.</summary>
    [Theory]
    [InlineData(SyntheticModel.V6)]
    [InlineData(SyntheticModel.V5)]
    public void Lod0_geometry_reads_from_both_versions(uint version)
    {
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(Model(version), out var pos, out var uv, out var tris));
        Assert.NotEmpty(pos);
        Assert.NotEmpty(uv);
        Assert.NotEmpty(tris);
    }
}
