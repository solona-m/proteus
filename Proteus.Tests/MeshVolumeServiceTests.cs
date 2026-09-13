using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The writer, against a genuinely valid <c>.mdl</c>. The edit goes into somebody else's mod, so what is
/// asserted here is mostly what must NOT change: the file's length, every byte outside the vertex buffer
/// and the stored extents, and the model's ability to be parsed afterwards.
/// <para/>
/// The displacement is produced by a real <see cref="MeshVolumeSolve"/>, but over a triangle list built here
/// rather than the file's own. <see cref="SyntheticModel"/> emits isolated triangles that share corner
/// positions and no indices, which gives the solve no connected surface to work over. The positions, the
/// normals and above all the
/// <see cref="ModelParts.MeshSpans"/> come from the real reader, so the mapping under test — concatenated
/// vertex back to a mesh's own buffer — is the real one.
/// </summary>
public class MeshVolumeServiceTests
{
    /// <summary>
    /// One mesh of two triangles is six vertices, which weld by position into four distinct points: the two
    /// triangles share their hub and one edge corner. Four points is exactly enough for a closed
    /// tetrahedron, where every edge is used by two faces and therefore nothing is a hole rim.
    /// </summary>
    private static (byte[] Mdl, MeshVolumeSolve Solve, ModelParts Model) Setup(
        IReadOnlyList<string>? shapes = null)
    {
        var mdl = SyntheticModel.Build(
            [],
            [new SyntheticModel.Mesh("/mt_test.mtrl", new SyntheticModel.Sub(0, Islands: 1,
                                                                            TrianglesPerIsland: 2))],
            SyntheticModel.V6, shapes);

        var read = ModelPartReader.Read(mdl);
        Assert.NotNull(read);
        Assert.Equal(6, read!.Positions.Length / 3);

        // Vertices 0, 1, 2 and 5 land on four distinct positions; 3 and 4 are duplicates of 0 and 2.
        int[] tetra = [0, 1, 2, 0, 2, 5, 0, 5, 1, 1, 5, 2];

        // Normals along +Y so the direction a vertex inflates is known rather than inferred from winding.
        var nrm = new float[read.Positions.Length];
        for (int i = 0; i < nrm.Length / 3; i++) nrm[i * 3 + 1] = 1f;

        var model = new ModelParts
        {
            Positions = read.Positions,
            Normals = nrm,
            MeshSpans = read.MeshSpans,
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "1.1", Material = "/mt_test.mtrl",
                    Triangles = tetra, Ordinals = [0, 1, 2, 3], AttributeMask = 0, Toggleable = true,
                    Min = Vector3.Zero, Max = Vector3.One,
                },
            ],
            AttributeNames = read.AttributeNames,
            Min = read.Min,
            Max = read.Max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };

        return (mdl, new MeshVolumeSolve(model), model);
    }

    private static float PositionY(byte[] mdl, int vertex)
    {
        var src = SecondSkinWriter.Parse(mdl);
        int mo = src.MeshStart + 0 * 36;
        var pe = src.Decls[0].First(e => e.Usage == SecondSkinWriter.UsePosition);
        uint vbo = BitConverter.ToUInt32(mdl, mo + 20 + pe.Stream * 4);
        byte stride = mdl[mo + 32 + pe.Stream];
        Span<float> tmp = stackalloc float[4];
        SecondSkinWriter.ReadTyped(mdl, src.Vb + (int)vbo + vertex * stride + pe.Offset, pe.Type, tmp);
        return tmp[1];
    }

    /// <summary>
    /// The file keeps its length and its shape, every brushed vertex moves by the displacement, and NOTHING
    /// outside the vertex buffer and the stored extents is touched.
    /// <para/>
    /// That last assertion is the one that makes this edit defensible on a mod that is not ours. Positions in
    /// place is only safe because nothing changes the file's length — no offset in the header moves, and
    /// every table, string, bone map and opaque tail stays exactly where it was.
    /// </summary>
    [Fact]
    public void WritesPositionsInPlaceAndTouchesNothingElse()
    {
        var (mdl, solve, model) = Setup();

        // Centred on vertex 0 with a radius that swallows the whole tetrahedron.
        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        Assert.True(solve.Paint(centre, 10f, 0.002f) > 0);
        solve.EndStroke();
        Assert.True(solve.Worst > 0f);

        var written = MeshVolumeService.Inflate(mdl, solve);

        Assert.Equal(mdl.Length, written.Model.Length);
        Assert.False(written.HasOtherLods);
        Assert.Equal(0, written.UnmappedSpares);

        // Still a model.
        var src = SecondSkinWriter.Parse(written.Model);
        Assert.Equal(1, src.MeshCount);

        // Every vertex moved by its own node's displacement, duplicates included — vertex 3 is a welded copy
        // of 0 and vertex 4 of 2, and a copy left behind is a crack down a UV seam.
        for (int v = 0; v < 6; v++)
        {
            float expected = PositionY(mdl, v) + solve.DeltaAt(v).Y;
            Assert.Equal(expected, PositionY(written.Model, v), 1e-6f);
        }
        Assert.Equal(PositionY(written.Model, 0), PositionY(written.Model, 3), 1e-9f);
        Assert.Equal(PositionY(written.Model, 2), PositionY(written.Model, 4), 1e-9f);

        // Nothing outside the ranges this is allowed to touch.
        int mo = src.MeshStart;
        ushort vc = BitConverter.ToUInt16(written.Model, mo);
        byte stride = written.Model[mo + 32];
        uint vbo = BitConverter.ToUInt32(written.Model, mo + 20);
        int vbLo = src.Vb + (int)vbo, vbHi = vbLo + vc * stride;

        var stray = new List<int>();
        for (int i = 0; i < mdl.Length; i++)
        {
            if (mdl[i] == written.Model[i]) continue;
            bool allowed = (i >= vbLo && i < vbHi)
                        || (i >= src.ModelBBoxAt && i < src.ModelBBoxAt + 4 * 32)
                        || (i >= src.BoneBBoxAt && i < src.BoneBBoxAt + src.BoneCount * 32)
                        || (i >= src.Mh && i < src.Mh + 4)          // Radius
                        || (i >= src.Mh + 28 && i < src.Mh + 36);   // model and shadow clip distances
            if (!allowed) stray.Add(i);
        }
        Assert.Empty(stray);
    }

    /// <summary>
    /// The stored extents grow with the geometry.
    /// <para/>
    /// Nothing in this project ever recomputed a bounding box, and an inflate is the edit that makes it
    /// matter: it moves geometry monotonically outward, which is exactly the direction that understates a
    /// box. Understating one makes the game cull the model while the body it belongs to is still on screen,
    /// with nothing in any log to say why.
    /// </summary>
    [Fact]
    public void GrowsTheStoredExtentsSoTheModelIsNotCulled()
    {
        var (mdl, solve, model) = Setup();

        // A bounding box the writer can widen, rather than the zeroed one the fixture ships. An all-zero box
        // is treated as unused, which is the right reading for the water and fog boxes on a character.
        var before = SecondSkinWriter.Parse(mdl);
        for (int box = 0; box < 2; box++)
        for (int c = 0; c < 3; c++)
        {
            BitConverter.GetBytes(-1f).CopyTo(mdl, before.ModelBBoxAt + box * 32 + c * 4);
            BitConverter.GetBytes(1f).CopyTo(mdl, before.ModelBBoxAt + box * 32 + 16 + c * 4);
        }
        BitConverter.GetBytes(2f).CopyTo(mdl, before.Mh);          // Radius
        BitConverter.GetBytes(50f).CopyTo(mdl, before.Mh + 28);    // ModelClipOutDistance
        BitConverter.GetBytes(30f).CopyTo(mdl, before.Mh + 32);    // ShadowClipOutDistance

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        var o = MeshVolumeService.Inflate(mdl, solve).Model;
        var src = SecondSkinWriter.Parse(o);
        float by = solve.Worst;
        Assert.True(by > 0f);

        for (int box = 0; box < 2; box++)
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(-1f - by, BitConverter.ToSingle(o, src.ModelBBoxAt + box * 32 + c * 4), 1e-6f);
            Assert.Equal(1f + by, BitConverter.ToSingle(o, src.ModelBBoxAt + box * 32 + 16 + c * 4), 1e-6f);
        }
        Assert.Equal(2f + by, BitConverter.ToSingle(o, src.Mh), 1e-6f);
        Assert.Equal(50f + by, BitConverter.ToSingle(o, src.Mh + 28), 1e-6f);
        Assert.Equal(30f + by, BitConverter.ToSingle(o, src.Mh + 32), 1e-6f);

        // The boxes the fixture left at zero stay at zero: turning an unused box into a tiny one around the
        // origin is inventing a volume rather than widening one.
        for (int box = 2; box < 4; box++)
        for (int c = 0; c < 8; c++)
            Assert.Equal(0f, BitConverter.ToSingle(o, src.ModelBBoxAt + box * 32 + c * 4));
    }

    /// <summary>
    /// A shape key's replacement vertex moves exactly once, and by its BASE vertex's displacement.
    /// <para/>
    /// A shape does not store offsets — each value rewires one index slot to a whole spare vertex carrying
    /// the shape's target pose. Left behind, enabling that shape puts the slots it rewires back where the
    /// author had them, so turning on a body slider undoes the clipping fix along whatever it touches.
    /// <para/>
    /// This fixture names vertex 0 as both the base and the replacement, which is the case that catches a
    /// double write: the main pass and the spare pass would each add the displacement and move it twice as
    /// far as everything around it. Hence spares being excluded from the main pass rather than merely
    /// handled afterwards.
    /// </summary>
    [Fact]
    public void CarriesAShapesSpareVertexExactlyOnce()
    {
        var (mdl, solve, model) = Setup(["shp_test"]);

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        var written = MeshVolumeService.Inflate(mdl, solve);
        Assert.Equal(0, written.UnmappedSpares);

        // Once, not twice: the fixture's shape names vertex 0 for both roles.
        float expected = PositionY(mdl, 0) + solve.DeltaAt(0).Y;
        Assert.Equal(expected, PositionY(written.Model, 0), 1e-6f);
    }

    /// <summary>
    /// A model carrying neck morph data is refused, with nothing written.
    /// <para/>
    /// The morph holds its own copy of the surface along the neck seam. Moving the vertices underneath it
    /// leaves the two describing different shapes, and the seam opens whenever the morph is active — and a
    /// model carrying one is a head, which is not what a garment brush is for.
    /// </summary>
    [Fact]
    public void RefusesAModelWithNeckMorphData()
    {
        var (mdl, solve, model) = Setup();

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        mdl[SecondSkinWriter.Parse(mdl).Mh + 43] = 1;   // NeckMorphCount

        var ex = Assert.Throws<ModelAttributeWriter.ModelEditException>(
            () => MeshVolumeService.Inflate(mdl, solve));
        Assert.Contains("neck morph", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A position format the writer cannot express is refused rather than silently doing nothing.
    /// <para/>
    /// <c>WriteXYZ</c> has no default arm: handed a type it does not know it writes nothing and returns, so
    /// the edit would be structurally perfect and move the model nowhere — which is indistinguishable from
    /// the brush having decided the geometry was fine.
    /// </summary>
    [Fact]
    public void RefusesAPositionFormatItCannotWrite()
    {
        var (mdl, solve, model) = Setup();

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        // Retype mesh 0's position element to Half2, which WriteXYZ knows but cannot hold three components
        // of — and which ModelAttributeWriter.PositionTypes therefore excludes.
        // The declaration blocks run from 0x44 to DeclEnd, eight bytes an entry:
        // stream, offset, type, usage, usageIndex, then padding.
        var src = SecondSkinWriter.Parse(mdl);
        bool retyped = false;
        for (int i = 0x44; i + 5 < src.DeclEnd && !retyped; i += 8)
            if (mdl[i] != 0xFF && mdl[i + 3] == SecondSkinWriter.UsePosition)
            { mdl[i + 2] = 13; retyped = true; }
        Assert.True(retyped, "the fixture's position element was not found");

        var ex = Assert.Throws<ModelAttributeWriter.ModelEditException>(
            () => MeshVolumeService.Inflate(mdl, solve));
        Assert.Contains("format", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Painted wind is saved into the model: the file gains the wind channel it did not have, reading it back
    /// gives the painted amount on every vertex, and the geometry is exactly what it was.
    /// </summary>
    [Fact]
    public void PaintedWindIsSavedIntoAnAddedChannel()
    {
        var (mdl, solve, model) = Setup();
        Assert.False(ModelPartReader.Read(mdl)!.HasWindChannel);

        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        Assert.True(solve.PaintWind(centre, 10f, 0.8f, 1f) > 0);
        solve.EndStroke(wind: true);
        Assert.True(solve.WindEdited);

        var written = MeshVolumeService.Inflate(mdl, solve);
        Assert.NotNull(written.WindChannel);
        Assert.Equal(1, written.WindChannel!.Value.MeshesAdded);

        var back = ModelPartReader.Read(written.Model)!;
        Assert.True(back.HasWindChannel);
        Assert.Equal(ModelPartReader.Read(mdl)!.Positions, back.Positions);
        for (int v = 0; v < back.Wind.Length; v++)
            Assert.Equal(solve.WindAt(v), back.Wind[v], 1f / 255f);

        // Opened again, the painted wind is what the brush starts from.
        var reopened = new MeshVolumeSolve(back);
        Assert.False(reopened.WindEdited);
        Assert.True(reopened.HasWindChannel);
        Assert.Equal(solve.WindAt(0), reopened.WindAt(0), 1f / 255f);
    }

    /// <summary>A model only pulled — no wind painted — keeps its vertex layout exactly as the author shipped it.</summary>
    [Fact]
    public void UnpaintedWindAddsNoChannel()
    {
        var (mdl, solve, model) = Setup();
        var centre = new Vector3(model.Positions[0], model.Positions[1], model.Positions[2]);
        solve.Paint(centre, 10f, 0.002f);
        solve.EndStroke();

        var written = MeshVolumeService.Inflate(mdl, solve);
        Assert.Null(written.WindChannel);
        Assert.Equal(mdl.Length, written.Model.Length);
    }

    /// <summary>Nothing painted, nothing saved — and the refusal is not an error the user has to clear.</summary>
    [Fact]
    public void RefusesToSaveAnUntouchedModel()
    {
        var (_, solve, _) = Setup();
        var outcome = MeshVolumeService.Apply("no-such-root", "a.mdl", [], solve);
        Assert.False(outcome.Ok);
        Assert.Equal(0, outcome.FilesWritten);
    }
}
