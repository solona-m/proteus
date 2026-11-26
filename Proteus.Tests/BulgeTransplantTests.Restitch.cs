using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Proteus.Services;
using Proteus.Services.Bulge;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The bulge re-stitched onto a hole of another point count (plan genital-switches, part 2), on the synthetic body whose
/// hole is the bulge's resampled: 25 points (three merges), 31 (three inserts).
/// </summary>
public partial class BulgeTransplantTests
{
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static (ModelPart Piece, ModelParts Parts) PieceOf(byte[] body)
    {
        var parts = ModelPartReader.Read(body)!;
        return (parts.Parts.Single(p => p.Island < 0 && p.Label == "1.2"), parts);
    }

    [Fact]
    [Trait("Plan", "AC-genital-switches-6")]
    [Trait("Plan", "EDGE-genital-switches-17")]
    public void Exact_hole_is_byte_identical()
    {
        var swapped = BulgeSwap.Featureless(SyntheticBulge.Body(), TbseBulge.Shared!, out var why);
        Assert.True(swapped != null, why);
        Assert.Null(swapped!.Restitched);
        Assert.Equal(GenitalSwitchesTests.TodayFeatureless, Sha(swapped.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-21")]
    public void Equal_count_past_holefit_is_no_sculpt()
    {
        // The bulge's own 28 points, 13 mm forward: past HoleFit, and never re-stitched.
        var body = SyntheticBulge.Body(hole: TbseBulge.Shared!.Hole.Select(p => p + new Vector3(0, 0, 0.013f)).ToArray());
        Assert.Null(BulgeSwap.Featureless(body, TbseBulge.Shared!, out var why));
        Assert.Equal("no sculpt for this body", why);
    }

    [Fact]
    [Trait("Plan", "AC-genital-switches-5")]
    [Trait("Plan", "EDGE-genital-switches-18")]
    [Trait("Plan", "EDGE-genital-switches-25")]
    public void Fewer_hole_points_restitched()
    {
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(25, 0.003f), rings: 6);
        var swapped = BulgeSwap.Featureless(body, bulge, out var why);
        Assert.True(swapped != null, why);
        Assert.StartsWith("bulge re-stitched onto a hole of 25 points (28 in the bulge): 3 merged, 0 added", swapped!.Restitched);
        var after = ModelPartReader.Read(swapped.Model)!;
        Assert.False(GenitalParts.Read(after, ModelSkinReader.Read(swapped.Model, null, null)!)!.Penis);
        var drawn = after.Parts.Single(p => p.Island < 0 && p.Label == "1.2");
        Assert.Equal('b', GenitalParts.ShippedBulgeBranch(after, drawn));
        Assert.True(swapped.Triangles < bulge.Data.triangles.Length / 3, $"{swapped.Triangles} triangles: nothing merged away");
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-19")]
    [Trait("Plan", "EDGE-genital-switches-28")]
    public void More_hole_points_restitched()
    {
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(31, 0.003f));
        var swapped = BulgeSwap.Featureless(body, bulge, out var why);
        Assert.True(swapped != null, why);
        Assert.StartsWith("bulge re-stitched onto a hole of 31 points (28 in the bulge): 0 merged, 3 added", swapped!.Restitched);
        Assert.Equal(bulge.Positions.Length + 3, swapped.SlotOf.Length);
        Assert.Equal(swapped.SlotOf.Length, swapped.Land.Length);
        Assert.Equal(bulge.Data.triangles.Length / 3 + 3, swapped.Triangles);   // each insert splits one rim triangle in two
        // Each added vertex's landing uv lies between the asset's (interpolated, not invented).
        var lands = bulge.Data.land.Where(l => l != null).Select(l => new Vector2(l![0], l[1])).ToList();
        foreach (var l in swapped.Land.Skip(bulge.Positions.Length).Where(l => l != null))
            Assert.InRange(l![0], lands.Min(x => x.X) - 1e-4f, lands.Max(x => x.X) + 1e-4f);
        var after = ModelPartReader.Read(swapped.Model)!;
        Assert.False(GenitalParts.Read(after, ModelSkinReader.Read(swapped.Model, null, null)!)!.Penis);
    }

    [Theory]
    [Trait("Plan", "EDGE-genital-switches-39")]
    [InlineData(21)] [InlineData(24)] [InlineData(25)] [InlineData(27)] [InlineData(29)] [InlineData(31)] [InlineData(37)]
    public void No_corner_steps_in_the_alignment(int points)
    {
        var hole = TbseBulge.Shared!.Hole;
        var al = BulgeSwap.Align(hole, SyntheticBulge.ResampledHole(points, 0.002f));
        Assert.NotNull(al);
        var map = al!.Value.Map;
        int shorter = Math.Min(points, hole.Length);
        Assert.Equal(Math.Max(points, hole.Length), map.Length);
        Assert.Equal(0, map[0]);
        Assert.Equal(shorter - 1, map[^1]);   // the wrap back to the first point is a diagonal
        int stays = 0;
        for (int i = 1; i < map.Length; i++)
        {
            int step = map[i] - map[i - 1];
            Assert.InRange(step, 0, 1);
            if (step == 0) stays++;
        }
        Assert.Equal(Math.Abs(points - hole.Length), stays);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-18")]
    public void Tie_break_is_deterministic()
    {
        var hole = TbseBulge.Shared!.Hole;
        var loop = SyntheticBulge.ResampledHole(25, 0.003f);
        var a = BulgeSwap.Align(hole, loop)!.Value;
        var b = BulgeSwap.Align(hole, loop)!.Value;
        Assert.Equal((a.Shift, a.Dir, a.Worst), (b.Shift, b.Dir, b.Worst));
        Assert.Equal(a.Map, b.Map);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-37")]
    public void Rim_count_outside_the_band_is_no_sculpt()
    {
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(20, 0.002f));
        Assert.Null(BulgeSwap.Featureless(body, TbseBulge.Shared!, out var why));
        Assert.Equal("no sculpt for this body", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-20")]
    public void Too_few_slots_for_inserts()
    {
        // A piece with two rings: fewer slots than the bulge and its three added points.
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(31, 0.003f), rings: 2);
        Assert.Null(BulgeSwap.Featureless(body, TbseBulge.Shared!, out var why));
        Assert.StartsWith("the genital piece has", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-21")]
    public void Restitch_beyond_reach_refused()
    {
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(25, 0.003f, forward: 0.02f), rings: 6);
        Assert.Null(BulgeSwap.Featureless(body, TbseBulge.Shared!, out var why));
        Assert.StartsWith("the body's hole is ", why);
        Assert.EndsWith(" mm from the bulge's", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-24")]
    [Trait("Plan", "EDGE-genital-switches-38")]
    public void A_main_skin_is_never_a_plug()
    {
        // The penis merged into the main skin (rule b): no piece to plug, so the main skin is never collapsed.
        var body = SyntheticBulge.Body(mergedDome: true, domeBone: "iv_ochinko_a");
        Assert.Null(BulgeSwap.Featureless(body, TbseBulge.Shared!, out var why));
        Assert.Equal("no sculpt for this body", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-25")]
    public void Shipped_bulge_recognition()
    {
        // A penis (the synthetic dome, on the bulge's own 28-point hole) reads as neither.
        var (dome, domeParts) = PieceOf(SyntheticBulge.Body());
        Assert.Null(GenitalParts.ShippedBulgeBranch(domeParts, dome));
        // The exact fit reads 'a'; the re-stitched one 'b'.
        var exact = BulgeSwap.Featureless(SyntheticBulge.Body(), TbseBulge.Shared!, out _)!;
        var (ePiece, eParts) = PieceOf(exact.Model);
        Assert.Equal('a', GenitalParts.ShippedBulgeBranch(eParts, ePiece));
        var re = BulgeSwap.Featureless(SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(25, 0.003f), rings: 6), TbseBulge.Shared!, out _)!;
        var (rPiece, rParts) = PieceOf(re.Model);
        Assert.Equal('b', GenitalParts.ShippedBulgeBranch(rParts, rPiece));
        // One vertex moved past RestitchReach from every bulge point: not 'b'.
        var moved = (float[])rParts.Positions.Clone();
        int v = re.SlotOf[re.SlotOf.Length / 2];
        moved[v * 3 + 2] += 0.08f;
        var movedParts = new ModelParts { Positions = moved, Normals = rParts.Normals, MeshSpans = rParts.MeshSpans, Parts = rParts.Parts,
                                          AttributeNames = rParts.AttributeNames, Min = rParts.Min, Max = rParts.Max, ShatteredSubmeshes = rParts.ShatteredSubmeshes };
        Assert.Null(GenitalParts.ShippedBulgeBranch(movedParts, rPiece));
        // One rim triangle taken out: its open edge is no longer one loop.
        var open = rPiece.Triangles.ToList();
        open.RemoveRange(0, 3);
        var holed = new ModelPart { Mesh = rPiece.Mesh, Submesh = rPiece.Submesh, Island = -1, Label = rPiece.Label, Material = rPiece.Material,
                                    Triangles = [.. open], Ordinals = rPiece.Ordinals.Skip(1).ToArray(), AttributeMask = 0, Min = rPiece.Min, Max = rPiece.Max,
                                    Toggleable = rPiece.Toggleable, AuthorSwitched = rPiece.AuthorSwitched };
        Assert.Null(GenitalParts.ShippedBulgeBranch(rParts, holed));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-26")]
    public void Restitch_self_check_refuses_a_turned_face()
    {
        var bulge = TbseBulge.Shared!;
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(25, 0.003f), rings: 6);
        var swapped = BulgeSwap.Featureless(body, bulge, out var why)!;
        Assert.NotNull(swapped);
        var (piece, before) = PieceOf(body);
        var fitted = BulgeSwap.FitHole(before, piece, bulge, out _)!;
        Assert.Null(BulgeSwap.SelfCheck(swapped.Model, before, piece, bulge, fitted, swapped.SlotOf));
        var flipped = (Vector3[])fitted.Positions.Clone();
        int a = fitted.Triangles[0], b = fitted.Triangles[1], c = fitted.Triangles[2];
        flipped[a] = flipped[b] + flipped[c] - flipped[a];
        Assert.Contains("turned over", BulgeSwap.SelfCheck(swapped.Model, before, piece, bulge, fitted with { Positions = flipped }, swapped.SlotOf) ?? "");
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-29")]
    public void Parallel_restitch_is_deterministic()
    {
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(25, 0.003f), rings: 6);
        string one = Sha(BulgeSwap.Featureless(body, TbseBulge.Shared!, out _)!.Model);
        var many = System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 8).Select(k => System.Threading.Tasks.Task.Run(
            () => Sha(BulgeSwap.Featureless(body, TbseBulge.Shared!, out _)!.Model)))).Result;
        Assert.All(many, h => Assert.Equal(one, h));
    }

    /// <summary>A covering garment refitted onto the 25-point body under <paramref name="tuning"/>; its log.</summary>
    private static (BodyRetarget.Planned Plan, List<string> Log) OntoRestitched(BodyRetarget.Tuning tuning)
    {
        var hole = SyntheticBulge.ResampledHole(25, 0.003f);
        var body = SyntheticBulge.Body(hole: hole, rings: 6);
        var source = SyntheticBulge.Body(hole: hole, rings: 6, height: 0.065f);
        var garment = BulgeRefitTests.Garment(source, _ => true);
        var log = new List<string>();
        var plan = BodyRetarget.WithTuning(tuning, () =>
            BodyRetarget.Plan(ModelPartReader.Read(garment)!, (byte[])garment.Clone(), [BulgeRefitTests.Pair(source, body)], "_dwn",
                              replaceSkin: true, log: log.Add, warn: log.Add));
        return (plan, log);
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-27")]
    public void Guard_applies_to_restitched_bodies()
    {
        var (guarded, log) = OntoRestitched(new BodyRetarget.Tuning(BulgeForceGuard: true));
        var (plain, _) = OntoRestitched(new BodyRetarget.Tuning(NoBulge: true));
        Assert.Contains(log, l => l.StartsWith("bulge: _dwn penis kept: guard", StringComparison.Ordinal));
        Assert.Equal(Sha(plain.Model), Sha(guarded.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-genital-switches-31")]
    public void A_fault_in_the_restitch_is_today()
    {
        var (faulted, log) = OntoRestitched(new BodyRetarget.Tuning(BulgeFault: true));
        var (plain, _) = OntoRestitched(new BodyRetarget.Tuning(NoBulge: true));
        Assert.Contains(log, l => l.Contains("left as it is — failed: bulge fault (test)", StringComparison.Ordinal));
        Assert.Equal(Sha(plain.Model), Sha(faulted.Model));
        // Unfaulted, the re-stitch says so beside its featureless line.
        var (_, said) = OntoRestitched(new BodyRetarget.Tuning());
        Assert.Contains(said, l => l.StartsWith("bulge: _dwn bulge re-stitched onto a hole of 25 points", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-genital-switches-7")]
    public void No_restitch_knob_is_today()
    {
        var body = SyntheticBulge.Body(hole: SyntheticBulge.ResampledHole(25, 0.003f), rings: 6);
        string reason = "";
        var r = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoHoleRestitch: true), () => BulgeSwap.Featureless(body, TbseBulge.Shared!, out reason));
        Assert.Null(r);
        Assert.Equal("no sculpt for this body", reason);
    }
}
