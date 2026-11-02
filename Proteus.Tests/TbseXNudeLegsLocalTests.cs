using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// TBSE-X's nude legs (#42): which of its smallclothes legs a men's version fits to when its made-for legs match TBSE-X's
/// SFW legs too little, the way TBSE's Gen A stands in for its SFW. Measured against a gate fixed before any number was
/// seen (plan 42-43-tbsex-legs-cross, G1).
/// </summary>
public class TbseXNudeLegsLocalTests(ITestOutputHelper o)
{
    private const string TbseX = CrossSexRefitLocalTests.TbseX;

    /// <summary>TBSE-X's legs options tried as its nude legs, in the order the plan fixed (D7).</summary>
    internal static readonly string[] Candidates = ["flaccid m a", "flaccid m c", "flaccid m d"];

    private const string LegsDir = "p1/legs (smallclothes legs)/";
    private const string LegsFile = "/chara/equipment/e0000/model/c0101e0000_dwn.mdl";

    internal static string TbseXLegs(string folder) => Path.Combine(LocalData.Path(TbseX), LegsDir + folder + LegsFile);

    private static Vector3 At(ModelParts body, int v)
        => new(body.Positions[v * 3], body.Positions[v * 3 + 1], body.Positions[v * 3 + 2]);

    /// <summary>What gate G1(a) reads off one candidate.</summary>
    internal sealed record Shape(int SfwSkin, int SfwOutsideFar, float SfwOutsideWorstMm, int Ring, float RingWorstMm,
                                 bool RingAllRound, int CandidateOnly, int CandidateOnlyOutside, float YWaist)
    {
        public bool Passes => SfwOutsideFar == 0 && RingAllRound && RingWorstMm <= 0.1f && CandidateOnlyOutside == 0;

        /// <summary>Where the candidate-only skin outside the box lies, for the record.</summary>
        public string OutsideBox { get; init; } = "";
    }

    /// <summary>
    /// G1(a), frozen before measuring. The waist ring: SFW's open skin edge within 3 cm of its top, with points on both
    /// sides and both in front of and behind its centre; yWaist its lowest. The crotch box: |x| ≤ 0.12, yWaist − 0.30 ≤ y ≤
    /// yWaist. (i) no SFW skin vertex outside the box further than 0.5 mm from the candidate; (ii) the ring within 0.1 mm;
    /// (iii) every candidate skin vertex further than 0.5 mm from the SFW surface — the very surface and reach
    /// <see cref="CrossSexRefit.NudeSkinShown"/> counts by — inside the box.
    /// </summary>
    internal static Shape Measure(string sfwPath, string candidatePath)
    {
        var sfw = ModelPartReader.Read(File.ReadAllBytes(sfwPath))!;
        var cand = ModelPartReader.Read(File.ReadAllBytes(candidatePath))!;
        var sfwSkin = new BodySurface(sfw, BodySurface.CellFor(0.01f)).SkinVertices;
        var sfwSurface = new BodySurface(sfw, BodySurface.CellFor(0.01f));
        var candSurface = new BodySurface(cand, BodySurface.CellFor(0.01f));
        float top = sfwSkin.Max(v => At(sfw, v).Y);
        var ring = CrossSexNudeLegsLocalTests.OpenEdgeVertices(sfw).Where(v => At(sfw, v).Y > top - 0.03f).ToList();
        float yWaist = ring.Count > 0 ? ring.Min(v => At(sfw, v).Y) : top;
        var centre = ring.Count > 0 ? ring.Aggregate(Vector3.Zero, (a, v) => a + At(sfw, v)) / ring.Count : Vector3.Zero;
        bool allRound = ring.Any(v => At(sfw, v).X < 0) && ring.Any(v => At(sfw, v).X > 0)
                        && ring.Any(v => At(sfw, v).Z < centre.Z) && ring.Any(v => At(sfw, v).Z > centre.Z);
        bool InBox(Vector3 p) => MathF.Abs(p.X) <= 0.12f && p.Y >= yWaist - 0.30f && p.Y <= yWaist;
        static float Dist(BodySurface s, Vector3 p) => s.Nearest(p, 0.05f, out var hit) ? hit.Distance : 0.05f;

        int outsideFar = 0;
        float outsideWorst = 0f;
        foreach (int v in sfwSkin)
        {
            var p = At(sfw, v);
            if (InBox(p)) continue;
            float d = Dist(candSurface, p);
            outsideWorst = MathF.Max(outsideWorst, d);
            if (d > 0.0005f) outsideFar++;
        }
        float ringWorst = ring.Select(v => Dist(candSurface, At(sfw, v))).DefaultIfEmpty(0f).Max();
        int only = 0, onlyOutside = 0;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        float farthest = 0f;
        foreach (int v in new BodySurface(cand, BodySurface.CellFor(0.01f)).SkinVertices)
        {
            var p = At(cand, v);
            float d = Dist(sfwSurface, p);
            if (d <= 0.0005f) continue;
            only++;
            if (InBox(p)) continue;
            onlyOutside++;
            (min, max, farthest) = (Vector3.Min(min, p), Vector3.Max(max, p), MathF.Max(farthest, d));
        }
        return new Shape(sfwSkin.Count, outsideFar, outsideWorst * 1000f, ring.Count, ringWorst * 1000f, allRound, only,
                         onlyOutside, yWaist)
        {
            OutsideBox = onlyOutside == 0 ? ""
                : $" (outside: x {min.X:0.000}..{max.X:0.000} y {min.Y:0.000}..{max.Y:0.000} z {min.Z:0.000}..{max.Z:0.000}, "
                  + $"up to {farthest * 1000f:F1} mm off the SFW skin)",
        };
    }

    private static string Describe(Shape s)
        => $"sfw skin {s.SfwSkin}: {s.SfwOutsideFar} outside the box further than 0.5 mm (worst {s.SfwOutsideWorstMm:F3} mm); " +
           $"waist ring {s.Ring} (all round {s.RingAllRound}) worst {s.RingWorstMm:F3} mm at yWaist {s.YWaist:F3}; " +
           $"candidate-only skin {s.CandidateOnly}, {s.CandidateOnlyOutside} outside the box{s.OutsideBox} → {(s.Passes ? "passes" : "fails")}";

    /// <summary>
    /// G1(a) on every candidate, measured 2026-11-01 (build 1412): none passes, so TBSE-X gets no nude legs and its
    /// refusals stand as they were.
    /// <list type="bullet">
    /// <item>The reference, TBSE SFW against Gen A, passes: 2434 SFW skin vertices all on Gen A, the 44-vertex waist ring
    /// 0.000 mm off, and Gen A's own 917 skin vertices all inside the crotch box.</item>
    /// <item>TBSE-X SFW (1884 skin vertices) lies on every candidate (0 off outside the box) and its waist ring is exact —
    /// but each candidate has 112 skin vertices outside the box, at x ±0.177 m, y 0.866–1.018 m, as far as the 50 mm
    /// search reaches from any SFW skin: the outer hips and upper thighs, where TBSE-X's SFW smallclothes draw cloth and
    /// no skin at all. Fitted there, a man's garment would show bare hips where TBSE-X men wear briefs, well beyond the
    /// crotch Gen A shows on TBSE — and it is why the game's women's legs land only 81.6% on TBSE-X's SFW legs.</item>
    /// </list>
    /// The gate was fixed before measuring and is not loosened: #42 stays open with these numbers.
    /// </summary>
    internal const string? Winner = null;

    [LocalDataFact(CrossSexRefitLocalTests.Tbse, TbseX)]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-6")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-26")]
    [Trait("Speed", "Slow")]
    public void M1_candidates_shape() // [AC-42-43-tbsex-legs-cross-6] [EDGE-42-43-tbsex-legs-cross-26]
    {
        string tbse = LocalData.Path(CrossSexRefitLocalTests.Tbse);
        var reference = Measure(Path.Combine(tbse, UvBakeInputs.TbseLegs[0].Rel), Path.Combine(tbse, UvBakeInputs.TbseLegs[1].Rel));
        o.WriteLine($"reference TBSE sfw → gen a: {Describe(reference)}");
        // The control: the gate passes the pair it was modelled on, so a candidate failing it is the candidate.
        Assert.True(reference.Passes, "the gate fails TBSE's own SFW and Gen A legs: the gate is wrong, not the candidates");
        string? winner = null;
        foreach (string c in Candidates)
        {
            var shape = Measure(TbseXLegs("sfw"), TbseXLegs(c));
            o.WriteLine($"TBSE-X sfw → {c}: {Describe(shape)}");
            if (shape.Passes) winner ??= c;
        }
        Assert.Equal(Winner, winner);
    }

    /// <summary>With no candidate passing, TBSE-X's legs name no nude legs: its men's versions stay on its SFW legs.</summary>
    [Fact]
    [Trait("Plan", "AC-42-43-tbsex-legs-cross-6")]
    [Trait("Plan", "EDGE-42-43-tbsex-legs-cross-26")]
    public void Supported_bodies_names_the_measured_winner() // [AC-42-43-tbsex-legs-cross-6] [EDGE-42-43-tbsex-legs-cross-26]
    {
        var legs = SupportedBodies.Of("tbse-x", "_dwn", false)!;
        Assert.Equal(Winner is null ? null : LegsDir + Winner + LegsFile, legs.Sizes.Single().NudeRel);
        Assert.Null(SupportedBodies.Of("tbse-x", "_top", false)!.Sizes.Single().NudeRel);
    }
}
