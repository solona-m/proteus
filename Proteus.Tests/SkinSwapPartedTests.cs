using System.Collections.Generic;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The rebuild's <c>parted</c>: skin meshes the solver parted the cloth from, judged on the body the garment was made
/// on, are swapped even where, laid, some of them lies past the new body — an old body's legs reaching further up the
/// waist than the new one's (Metro Style, Lithe to Neolithe; see <see cref="OldBodyWaistStripLocalTests"/>). Every other
/// mesh is judged as before, so a posed foot the solver did not part is still kept.
/// </summary>
public class SkinSwapPartedTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e0705_dwn_a.mtrl";

    private static readonly byte[] Legs =
        SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 12)));

    /// <summary>A skin mesh mostly on the legs, with a piece (well over 5% of it) far past them; then
    /// <paramref name="other"/> more skin meshes on the legs, and cloth.</summary>
    private static byte[] Garment(int other = 0)
    {
        var meshes = new List<SyntheticModel.Mesh>
        {
            new(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 6),
                      new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f)),
        };
        for (int i = 0; i < other; i++) meshes.Add(new(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)));
        meshes.Add(new(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        return SyntheticModel.Build([], meshes.ToArray());
    }

    private static BodyRetarget.SlotPair Resized(byte[] body)
    {
        var target = ModelPartReader.Read(body)!;
        Assert.True(IdentityCorrespondence.TryBuild(target, target, "_dwn", out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair("_dwn", built!, target, body);
    }

    [Fact]
    public void A_parted_mesh_partly_past_the_new_body_is_swapped()
    {
        BodyRetarget.Rebuild(Garment(), [Resized(Legs)], true, null, out var judged, cutHidden: false);
        Assert.Equal(0, judged.Removed);
        Assert.Equal(1, judged.Kept);
        Assert.Equal(1, judged.Posed);

        Assert.NotNull(BodyRetarget.Rebuild(Garment(), [Resized(Legs)], true, null, out var told, cutHidden: false,
                                            parted: new HashSet<int> { 0 }));
        Assert.Equal(8, told.Removed);
        Assert.Equal(0, told.Kept);
    }

    [Fact]
    public void A_mesh_the_solver_did_not_part_is_judged_as_before()
    {
        // Mesh 1 parted and on the legs; mesh 0, partly off them, is not in the set and stays, as a posed foot does.
        Assert.NotNull(BodyRetarget.Rebuild(Garment(other: 1), [Resized(Legs)], true, null, out var report, cutHidden: false,
                                            parted: new HashSet<int> { 1 }));
        Assert.Equal(2, report.Removed);
        Assert.Equal(1, report.Kept);
        Assert.Equal(1, report.Posed);
    }

    [Fact]
    public void A_parted_mesh_on_no_new_body_is_kept()
    {
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f)),
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));

        BodyRetarget.Rebuild(garment, [Resized(Legs)], true, null, out var report, cutHidden: false,
                             parted: new HashSet<int> { 0 });
        Assert.Equal(0, report.Removed);
        Assert.Equal(1, report.Kept);
        Assert.Equal(0, report.Posed);
    }

    [Fact]
    public void The_held_set_wins_over_parted()
    {
        BodyRetarget.Rebuild(Garment(other: 1), [Resized(Legs)], true, null, out var report, cutHidden: false,
                             swapMeshes: new HashSet<int> { 1 }, parted: new HashSet<int> { 0, 1 });
        Assert.Equal(2, report.Removed);
        Assert.Equal(1, report.Kept);
    }
}
