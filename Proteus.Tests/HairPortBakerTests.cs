using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// The geometry half of a port on the synthetic deformer: a Miqo'te head 10 cm above a Midlander's, a Lalafell's 60 cm
/// below. A port must move the hair with the head, and refuse what it cannot place.
/// </summary>
public class HairPortBakerTests
{
    private static readonly HashSet<string> NoExtra = [];

    private static Vector3 Centre(byte[] mdl)
    {
        var p = ModelPartReader.Read(mdl)!.Positions;
        var sum = Vector3.Zero;
        for (int i = 0; i < p.Length; i += 3) sum += new Vector3(p[i], p[i + 1], p[i + 2]);
        return sum / (p.Length / 3);
    }

    private static HairPortBaker.Result? Port(byte[] hair, ushort from, ushort to, out string why,
                                              byte[]? targetHead = null, IReadOnlySet<string>? extra = null)
        => HairPortBaker.Prepare(hair, from, to, Id, Pbd(), extra ?? NoExtra, HeadModel(from), targetHead ?? HeadModel(to),
                                 out why);

    [Fact]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void Port_moves_the_hair_with_the_head()
    {
        var result = Port(HairModel(801), 801, 201, out var why);
        Assert.True(result != null, why);
        var moved = Centre(result!.Model) - Centre(HairModel(801));
        Assert.True(Vector3.Distance(moved, -Offset(801)) < 1e-4f, $"moved {moved}");
        Assert.True(result.Sunk <= HairPortBaker.SunkLimit);
        Assert.True(MathF.Abs(result.GapShift) < 0.002f, $"gap shift {result.GapShift}");
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-25")]
    public void Port_through_a_common_ancestor_on_another_line()
    {
        // c1201 falls through c1101 to c0101; c0201 to c0101: neither is the other's ancestor.
        var result = Port(HairModel(1201), 1201, 201, out var why);
        Assert.True(result != null, why);
        var moved = Centre(result!.Model) - Centre(HairModel(1201));
        Assert.True(Vector3.Distance(moved, Offset(201) - Offset(1201)) < 1e-4f, $"moved {moved}");
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-30")]
    public void A_port_that_sinks_into_the_head_is_refused()
    {
        // The target's head is a third larger than the hair: the hair lies inside it.
        var result = Port(HairModel(801), 801, 201, out var why, targetHead: HeadModel(201, 0.13f));
        Assert.Null(result);
        Assert.Contains("c0201", why);
        Assert.Contains("%", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-5")]
    public void Unsupplied_hair_bone_refuses_with_its_name()
    {
        var hair = HairModel(801, bones: [("j_ex_h0130_ke_l", 1f)]);
        Assert.Null(Port(hair, 801, 201, out var why));
        Assert.Contains("j_ex_h0130_ke_l", why);
        // The target's own model of the hairstyle has that bone: its skeleton does too.
        Assert.NotNull(Port(hair, 801, 201, out why, extra: new HashSet<string> { "j_ex_h0130_ke_l" }));
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void A_bone_the_deformer_cannot_place_is_refused()
    {
        var hair = HairModel(801, bones: [("j_unknown_bone", 1f)]);
        Assert.Null(Port(hair, 801, 201, out var why));
        Assert.Contains("j_unknown_bone", why);
    }

    [Theory]
    [Trait("Plan", "EDGE-hair-race-port-19")]
    [InlineData("pbd-missing-path")]
    [InlineData("no-head")]
    [InlineData("unreadable")]
    public void Missing_inputs_refuse(string cause)
    {
        string why;
        HairPortBaker.Result? result = cause switch
        {
            // A deformer with no c0801: no way from it to anything.
            "pbd-missing-path" => HairPortBaker.Prepare(HairModel(801), 801, 201, Id,
                new XivLiveMesh.PbdFile(BuildPbd([(101, ushort.MaxValue, new() { ["j_kao"] = Matrix4x4.Identity })])),
                NoExtra, HeadModel(801), HeadModel(201), out why),
            "no-head" => HairPortBaker.Prepare(HairModel(801), 801, 201, Id, Pbd(), NoExtra, HeadModel(801), null, out why),
            _ => HairPortBaker.Prepare(new byte[64], 801, 201, Id, Pbd(), NoExtra, HeadModel(801), HeadModel(201), out why),
        };
        Assert.Null(result);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-4")]
    public void Miqote_fur_is_hidden_and_rebound_for_other_races()
    {
        // An ear: a small cap on the ear bone, beside the head.
        var ear = Sphere("/mt_c0801h0130_hir_a.mtrl", Head + Offset(801) + new Vector3(0.07f, 0.08f, 0f), 0.02f,
                         [("j_mimi_l", 1f)], rings: 4, segments: 6);
        var hair = HairModel(801, extra: ear);
        var result = Port(hair, 801, 201, out var why);
        Assert.True(result != null, why);
        Assert.True(result!.FurHidden > 0);
        Assert.DoesNotContain(SecondSkinWriter.Parse(result.Model).BoneNames, b => b.StartsWith("j_mimi"));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-4")]
    public void Hair_ported_into_a_miqote_keeps_everything()
    {
        // Ears are taken out only on the way to a race without them; nothing is added on the way in.
        var result = Port(HairModel(201), 201, 801, out var why);
        Assert.True(result != null, why);
        Assert.Equal(0, result!.FurHidden);
        Assert.Equal(SecondSkinWriter.Parse(HairModel(201)).BoneNames, SecondSkinWriter.Parse(result.Model).BoneNames);
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-3")]
    public void Hair_materials_are_renamed_to_the_target_race()
    {
        var result = Port(HairModel(801), 801, 201, out var why);
        Assert.True(result != null, why);
        Assert.Equal(["/mt_c0201h0130_hir_a.mtrl"], SecondSkinWriter.Parse(result!.Model).MatNames);
        Assert.Equal([("/mt_c0801h0130_hir_a.mtrl", "/mt_c0201h0130_hir_a.mtrl")], result.Renamed);
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-26")]
    public void Port_keeps_the_layout()
    {
        var hair = HairModel(801);
        var result = Port(hair, 801, 201, out var why)!;
        Assert.True(result != null, why);
        var a = SecondSkinWriter.Parse(hair);
        var b = SecondSkinWriter.Parse(result.Model);
        Assert.Equal(a.MeshCount, b.MeshCount);
        Assert.Equal(a.BoneNames, b.BoneNames);
        Assert.Equal(ModelPartReader.Read(hair)!.Positions.Length, ModelPartReader.Read(result.Model)!.Positions.Length);
    }

    /// <summary>A bend that turns the surface inside out — here a mirror — is refused, not saved.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void A_port_that_turns_triangles_inside_out_is_refused()
    {
        var mirror = Matrix4x4.CreateScale(-1f, 1f, 1f);
        var pbd = new XivLiveMesh.PbdFile(BuildPbd(
        [
            (101, ushort.MaxValue, new() { ["j_kao"] = Matrix4x4.Identity }),
            (201, 101, new() { ["j_kao"] = Matrix4x4.Identity }),
            (801, 201, new() { ["j_kao"] = mirror }),
        ]));
        var result = HairPortBaker.Prepare(HairModel(801), 801, 201, Id, pbd, NoExtra, HeadModel(801), HeadModel(201),
                                           out var why);
        Assert.Null(result);
        Assert.Contains("inside out", why);
    }

    /// <summary>
    /// A target whose deformer has ear bones of its own turns a Miqo'te's fur over — a Lalafell's does — but the fur is
    /// taken out of the port, so it is not counted against it.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-4")]
    public void Fur_the_bend_turns_over_is_not_counted()
    {
        var mirror = Matrix4x4.CreateScale(-1f, 1f, 1f);
        var pbd = new XivLiveMesh.PbdFile(BuildPbd(
        [
            (101, ushort.MaxValue, new() { ["j_kao"] = Matrix4x4.Identity }),
            (201, 101, new() { ["j_kao"] = Matrix4x4.Identity }),
            (801, 201, new() { ["j_kao"] = Matrix4x4.CreateTranslation(Offset(801)) }),
            (1101, 101, new() { ["j_kao"] = Matrix4x4.CreateTranslation(Offset(1101)) }),
            (1201, 1101, new() { ["j_kao"] = Matrix4x4.Identity, ["j_mimi_l"] = mirror }),
        ]));
        var ear = Sphere("/mt_c0801h0130_hir_a.mtrl", Head + Offset(801) + new Vector3(0.07f, 0.08f, 0f), 0.02f,
                         [("j_mimi_l", 1f)], rings: 4, segments: 6);
        var hair = HairModel(801, extra: ear);
        // Counted, the turned fur alone would be over the limit.
        var a = ModelPartReader.Read(hair)!;
        var bent = RacialModelBake.Port(hair, 801, 1201, pbd, HairPortBaker.ParentOf, out var bendWhy);
        Assert.True(bent != null, bendWhy);
        Assert.True(HairPortBaker.Folded(a, ModelPartReader.Read(bent!)!)
                    > HairPortBaker.FoldedShare * a.Parts.Where(p => p.Island == -1).Sum(p => p.TriangleCount),
                    "the bend no longer turns the fur over");

        var result = HairPortBaker.Prepare(hair, 801, 1201, Id, pbd, NoExtra, HeadModel(801), HeadModel(1201), out var why);
        Assert.True(result != null, why);
        Assert.True(result!.FurHidden > 0);
    }
}
