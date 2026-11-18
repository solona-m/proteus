using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A garment split at the waist between its top and its legs, refitted one piece at a time: the seam the two share is
/// held where the author put it (see <see cref="BodyRetarget.PinSharedSeam"/>). Synthetic: a tube cut at y = 1.0 m over a
/// round body, the legs refitted onto a body 6 mm rounder than the one they were made on, the top onto its own.
/// </summary>
public class SharedSeamTests
{
    internal const string Cloth = "/mt_c0201e0628_top_dress.mtrl";
    internal const string Skin = "/mt_c0201b0001_bibo.mtrl";
    internal const int Segments = 24;
    internal const float ClothRadius = 0.14f;
    internal const float Split = 1.0f;

    /// <summary>A tube's rings from <paramref name="y0"/> to <paramref name="y1"/>; ring 0 at y0.</summary>
    internal static SyntheticModel.SurfaceMesh Tube(string material, float radius, float y0, float y1, int rows,
                                                    Func<int, float, (string, float)[]> weight, bool doubleSided = false,
                                                    bool cap = false)
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var w = new List<(string, float)[]?>();
        for (int r = 0; r <= rows; r++)
        {
            float y = y0 + (y1 - y0) * r / rows;
            for (int s = 0; s < Segments; s++)
            {
                float a = MathF.Tau * s / Segments;
                pos.Add(new Vector3(MathF.Cos(a) * radius, y, MathF.Sin(a) * radius));
                nrm.Add(new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)));
                w.Add(weight(r, y));
            }
        }
        var tris = new List<int>();
        for (int r = 0; r < rows; r++)
            for (int s = 0; s < Segments; s++)
            {
                int a = r * Segments + s, b = r * Segments + (s + 1) % Segments;
                int c = a + Segments, d = b + Segments;
                tris.AddRange([a, c, b, b, c, d]);
            }
        if (cap)
            foreach (var (ring, y, up) in new[] { (0, y0, -1f), (rows, y1, 1f) })
            {
                int centre = pos.Count;
                pos.Add(new Vector3(0f, y, 0f));
                nrm.Add(new Vector3(0f, up, 0f));
                w.Add(weight(ring, y));
                for (int s = 0; s < Segments; s++)
                {
                    int a = ring * Segments + s, b = ring * Segments + (s + 1) % Segments;
                    tris.AddRange(up > 0 ? [centre, b, a] : [centre, a, b]);
                }
            }
        if (doubleSided)
        {
            int n = pos.Count;
            for (int i = 0; i < n; i++)
            {
                pos.Add(pos[i]);
                nrm.Add(-nrm[i]);
                w.Add(w[i]);
            }
            int t = tris.Count;
            for (int i = 0; i < t; i += 3) tris.AddRange([tris[i] + n, tris[i + 2] + n, tris[i + 1] + n]);
        }
        return new SyntheticModel.SurfaceMesh(material, [.. pos], [.. nrm], new Vector2[pos.Count], [.. w], [(0u, [.. tris])]);
    }

    /// <summary>The ring at the split weighted to <c>j_kosi</c> in both pieces; the rest to the piece's own bone.</summary>
    internal static Func<int, float, (string, float)[]> Weights(string own, string ringBone = "j_kosi")
        => (_, y) => MathF.Abs(y - Split) < 1e-6f ? [(ringBone, 1f)] : [(own, 1f)];

    internal static byte[] TopBytes(bool doubleSided = false, string ringBone = "j_kosi", float y1 = 1.2f)
        => SyntheticModel.BuildSurface([], Tube(Cloth, ClothRadius, Split, y1, 4, Weights("j_sebo_a", ringBone), doubleSided));

    internal static byte[] LegsBytes(bool doubleSided = false, string ringBone = "j_kosi", float y0 = 0.85f)
        => SyntheticModel.BuildSurface([], Tube(Cloth, ClothRadius, y0, Split, 3, Weights("j_asi_a_l", ringBone), doubleSided));

    internal static ModelParts Body(float radius)
        => ModelPartReader.Read(SyntheticModel.BuildSurface([], Tube(Skin, radius, 0.7f, 1.4f, 14, (_, _) => [("j_kosi", 1f)],
                                                                     cap: true)))!;

    internal static BodyRetarget.SlotPair Pair(string slot, float from, float to)
    {
        var source = Body(from);
        var target = Body(to);
        Assert.True(IdentityCorrespondence.TryBuild(source, target, slot, out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, target);
    }

    internal static (ModelParts Parts, XivLiveMesh.SkinnedMesh Skin) Read(byte[] bytes)
        => (ModelPartReader.Read(bytes)!, ModelSkinReader.Read(bytes, null, null)!);

    /// <summary>The seam each piece holds, against the other's file.</summary>
    internal static (BodyRetarget.SharedSeam Top, BodyRetarget.SharedSeam Legs) Seams(byte[] top, byte[] legs)
    {
        var t = Read(top);
        var l = Read(legs);
        var points = BodyRetarget.SplitPoints(t.Parts, t.Skin, [(l.Parts, l.Skin)])!;
        var tv = BodyRetarget.SeamVertices(t.Parts, t.Skin, points);
        var lv = BodyRetarget.SeamVertices(l.Parts, l.Skin, points);
        return (new(tv.Vertices, tv.Positions, "synthetic"), new(lv.Vertices, lv.Positions, "synthetic"));
    }

    internal static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    internal static Vector3 Moved(ModelParts m, BodyRetarget.Planned plan, int v)
    {
        var d = plan.Edit.DeltaAt(v);
        return At(m, v) + new Vector3(d.X, d.Y, d.Z);
    }

    /// <summary>The worst distance between each top ring vertex and the legs' vertex at its authored place, in the pass.</summary>
    internal static float RingGap(byte[] top, BodyRetarget.Planned topPlan, byte[] legs, BodyRetarget.Planned legsPlan)
    {
        var t = ModelPartReader.Read(top)!;
        var l = ModelPartReader.Read(legs)!;
        float worst = 0f;
        for (int i = 0; i < t.Positions.Length / 3; i++)
        {
            if (MathF.Abs(At(t, i).Y - Split) > 1e-6f) continue;
            for (int j = 0; j < l.Positions.Length / 3; j++)
                if (Vector3.Distance(At(t, i), At(l, j)) < 1e-6f)
                    worst = MathF.Max(worst, Vector3.Distance(Moved(t, topPlan, i), Moved(l, legsPlan, j)));
        }
        return worst;
    }

    /// <summary>Top refitted onto the body it was made on, legs from a body 6 mm slimmer onto it.</summary>
    internal static (BodyRetarget.Planned Top, BodyRetarget.Planned Legs) Refit(byte[] top, byte[] legs, bool seam,
                                                                                List<string>? log = null)
    {
        var (topSeam, legsSeam) = seam ? Seams(top, legs) : (null!, null!);
        var topPlan = BodyRetarget.Plan(ModelPartReader.Read(top)!, top, [Pair("_top", 0.126f, 0.126f)], "_top",
                                        log: log == null ? null : log.Add, sharedSeam: seam ? topSeam : null);
        var legsPlan = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn",
                                         log: log == null ? null : log.Add, sharedSeam: seam ? legsSeam : null);
        return (topPlan, legsPlan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Plan", "AC-split-waist-seam-1")]
    public void TwoPieces_SeamHeldOnWrittenModels(bool doubleSided)
    {
        byte[] top = TopBytes(doubleSided), legs = LegsBytes(doubleSided);
        var (bareTop, bareLegs) = Refit(top, legs, seam: false);
        Assert.True(RingGap(top, bareTop, legs, bareLegs) > 0.004f, "without the hold the legs' waist should move away");

        var (heldTop, heldLegs) = Refit(top, legs, seam: true);
        Assert.True(RingGap(top, heldTop, legs, heldLegs) <= 5e-5f);
        Assert.Equal(Segments, heldTop.Report.SeamPinnedNodes);
        Assert.Equal(Segments, heldLegs.Report.SeamPinnedNodes);
        Assert.Equal(doubleSided ? 2 * Segments : Segments, heldLegs.Report.SeamPinnedVertices);

        // On the written models: every authored ring place drawn exactly there in both.
        var t = ModelPartReader.Read(top)!;
        var writtenTop = ModelPartReader.Read(heldTop.Model)!;
        var writtenLegs = ModelPartReader.Read(heldLegs.Model)!;
        for (int i = 0; i < t.Positions.Length / 3; i++)
        {
            if (MathF.Abs(At(t, i).Y - Split) > 1e-6f) continue;
            var p = At(t, i);
            Assert.Contains(Enumerable.Range(0, writtenTop.Positions.Length / 3), v => Vector3.Distance(At(writtenTop, v), p) <= 5e-5f);
            Assert.Contains(Enumerable.Range(0, writtenLegs.Positions.Length / 3), v => Vector3.Distance(At(writtenLegs, v), p) <= 5e-5f);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-35")]
    public void OpenEdgesOf_DoubleSided_MergesSheets()
    {
        var single = Read(LegsBytes(doubleSided: false)).Parts;
        var both = Read(LegsBytes(doubleSided: true)).Parts;
        int Ring(BodyRetarget.OpenEdges e) => Enumerable.Range(0, e.Count).Count(n => e.Open[n] && MathF.Abs(e.NodeAt[n].Y - Split) < 1e-6f);
        int Inner(BodyRetarget.OpenEdges e) => Enumerable.Range(0, e.Count).Count(n => e.Open[n] && e.NodeAt[n].Y is > 0.86f and < 0.99f);

        Assert.Equal(Segments, Ring(BodyRetarget.OpenEdgesOf(single)));
        Assert.Equal(Segments, Ring(BodyRetarget.OpenEdgesOf(both)));
        Assert.Equal(0, Ring(BodyRetarget.OpenEdgesOf(both, mergeSheets: false)));
        Assert.Equal(0, Inner(BodyRetarget.OpenEdgesOf(both)));
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-2")]
    [Trait("Plan", "EDGE-split-waist-seam-1")]
    public void LegsRefit_TakesNothingFromTheTop_ClosesAgainstAnyTop()
    {
        // The legs refit is handed no part of the top's refit (no outfit, no top model): its bytes are the same made alone,
        // before the top's refit or after it. What matters is that it closes against whatever top is worn.
        byte[] top = TopBytes(), legs = LegsBytes();
        var (_, legsSeamAlone) = Seams(top, legs);
        var legsAlone = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn",
                                          sharedSeam: legsSeamAlone);
        var (_, legsFirst) = Refit(top, legs, seam: true);
        Assert.Equal(legsAlone.Model, legsFirst.Model);

        // Against the author's own top (not refitted): its ring is where the author put it, and so is the legs'.
        var l = ModelPartReader.Read(legs)!;
        for (int j = 0; j < l.Positions.Length / 3; j++)
            if (MathF.Abs(At(l, j).Y - Split) < 1e-6f)
                Assert.True(Vector3.Distance(Moved(l, legsFirst, j), At(l, j)) <= 5e-5f);

        // Against the top refitted onto a bigger chest as well: the top holds the same ring.
        var (topSeam, _) = Seams(top, legs);
        var bigger = BodyRetarget.Plan(ModelPartReader.Read(top)!, top, [Pair("_top", 0.126f, 0.13f)], "_top", sharedSeam: topSeam);
        Assert.True(RingGap(top, bigger, legs, legsFirst) <= 5e-5f);
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-5")]
    [Trait("Plan", "EDGE-split-waist-seam-14")]
    public void OutsideBand_BitIdentical_NoNewFolds_BandFloorHeld()
    {
        byte[] top = TopBytes(), legs = LegsBytes();
        var (_, bare) = Refit(top, legs, seam: false);
        var (_, held) = Refit(top, legs, seam: true);
        var l = ModelPartReader.Read(legs)!;
        for (int j = 0; j < l.Positions.Length / 3; j++)
        {
            float along = Split - At(l, j).Y;      // straight down the tube: the distance along the cloth
            if (along > BodyRetarget.SeamBlendReach + 1e-4f)
                Assert.Equal(bare.Edit.DeltaAt(j), held.Edit.DeltaAt(j));
        }
        Assert.True(held.Report.Folded <= bare.Report.Folded);
        // The band never comes nearer the 0.126 body than 1 mm, nor nearer than it stood.
        for (int j = 0; j < l.Positions.Length / 3; j++)
        {
            var p = Moved(l, held, j);
            float off = new Vector2(p.X, p.Z).Length() - 0.126f;
            Assert.True(off >= BodyRetarget.Clearance - 1e-4f, $"vertex {j} {off * 1000f:0.00} mm off the body");
        }
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-6")]
    public void NoSeam_OrNoSharedSeamTuning_ByteIdentical()
    {
        byte[] top = TopBytes(), legs = LegsBytes();
        var (_, bare) = Refit(top, legs, seam: false);
        var (_, legsSeam) = Seams(top, legs);
        var off = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSharedSeam: true),
            () => BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn", sharedSeam: legsSeam));
        var empty = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn",
                                      sharedSeam: new BodyRetarget.SharedSeam([], 0, "none"));
        Assert.Equal(bare.Model, off.Model);
        Assert.Equal(bare.Model, empty.Model);
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-7")]
    [Trait("Plan", "EDGE-split-waist-seam-30")]
    public void LowCoverage_ByteIdentical()
    {
        // Legs whose waist ring is half off the top's: the gate's seam top covers it at 50%, below SeamCover.
        var t = Read(TopBytes());
        var points = BodyRetarget.SplitPoints(t.Parts, t.Skin, [Read(LegsBytes())])!;
        var half = new BodyRetarget.SeamPoints([.. points.At.Take(points.Count / 2), .. points.At.Skip(points.Count / 2).Select(p => p + new Vector3(0f, 0.01f, 0f))],
                                               points.Weights);
        var gate = new RefitCore.SeamGate(new RefitCore.SeamTop("top.mdl", "Body", half), null);
        byte[] legs = LegsBytes();
        var (seam, key, why) = RefitCore.SeamForGarment(gate, ModelPartReader.Read(legs)!, legs, "Body", "Body");
        Assert.Null(seam);
        Assert.Null(key);
        Assert.Contains("another waist", why, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-8")]
    [Trait("Plan", "EDGE-split-waist-seam-26")]
    public void Fallback_DropsPin_WhenFoldsOrBurialRise()
    {
        // Onto a body rounder than the cloth itself: the refit carries the waist out of it, and holding the waist where
        // the author put it would bury it — the hold is let go, and the piece comes out as without it.
        byte[] top = TopBytes(), legs = LegsBytes();
        var (_, legsSeam) = Seams(top, legs);
        var log = new List<string>();
        var bare = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.15f)], "_dwn");
        var held = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.15f)], "_dwn",
                                     log: log.Add, sharedSeam: legsSeam);
        Assert.NotNull(held.Report.SeamDropped);
        Assert.Equal(0, held.Report.SeamPinnedNodes);
        Assert.Equal(bare.Model, held.Model);
        Assert.Contains(log, l => l.StartsWith("shared seam: not held", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-9")]
    public void PlanLogsSeamLines()
    {
        var log = new List<string>();
        var (_, held) = Refit(TopBytes(), LegsBytes(), seam: true, log);
        // One line per piece; the legs' names its counts as its report does.
        Assert.Equal(2, log.Count(l => l.StartsWith("shared seam:", StringComparison.Ordinal)));
        Assert.Contains(log, l => l.StartsWith("shared seam:", StringComparison.Ordinal)
                                  && l.Contains($"{held.Report.SeamPinnedNodes} positions ({held.Report.SeamPinnedVertices} vertices)", StringComparison.Ordinal)
                                  && l.Contains($"{held.Report.SeamBandHeld} band points", StringComparison.Ordinal)
                                  && l.Contains("(synthetic)", StringComparison.Ordinal));
        var bare = new List<string>();
        Refit(TopBytes(), LegsBytes(), seam: false, bare);
        Assert.DoesNotContain(bare, l => l.StartsWith("shared seam", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-13")]
    public void KeptHeldOrSkinWeldedSeam_SkippedCounted()
    {
        byte[] top = TopBytes(), legs = LegsBytes();
        var (_, legsSeam) = Seams(top, legs);
        var parts = ModelPartReader.Read(legs)!;
        var held = BodyRetarget.Plan(parts, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn", held: legsSeam.Vertices.ToHashSet(),
                                     sharedSeam: legsSeam);
        Assert.Equal(0, held.Report.SeamPinnedNodes);
        Assert.Equal(Segments, held.Report.SeamSkipped);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-15")]
    public void CoincidentSkin_NotASeam()
    {
        // Two skin tubes meeting at the waist, as a body's chest and legs do: never a split.
        var topSkin = SyntheticModel.BuildSurface([], Tube(Skin, ClothRadius, Split, 1.2f, 4, Weights("j_sebo_a")));
        var legsSkin = SyntheticModel.BuildSurface([], Tube(Skin, ClothRadius, 0.85f, Split, 3, Weights("j_asi_a_l")));
        var t = Read(topSkin);
        Assert.Null(BodyRetarget.SplitPoints(t.Parts, t.Skin, [Read(legsSkin)]));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-16")]
    public void DifferentWeights_NotASeam_NotCovered()
    {
        var t = Read(TopBytes());
        Assert.Null(BodyRetarget.SplitPoints(t.Parts, t.Skin, [Read(LegsBytes(ringBone: "j_asi_b_l"))]));
        var points = BodyRetarget.SplitPoints(t.Parts, t.Skin, [Read(LegsBytes())])!;
        var other = Read(LegsBytes(ringBone: "j_asi_b_l"));
        Assert.Equal(0f, BodyRetarget.SeamVertices(other.Parts, other.Skin, points).Coverage);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-31")]
    [Trait("Plan", "EDGE-split-waist-seam-32")]
    public void BeltCopiedIntoBothPieces_RefittedNotPinned()
    {
        // A belt band (y 0.98-1.02) copied into both files: its faces are shared corner for corner, its interior is on no
        // open edge, and its rims are not where the other piece's edge is — never a split.
        static byte[] With(SyntheticModel.SurfaceMesh piece)
            => SyntheticModel.BuildSurface([], piece, Tube(Cloth, ClothRadius + 0.004f, 0.98f, 1.02f, 2, (_, _) => [("j_kosi", 1f)]));
        var top = Read(With(Tube(Cloth, ClothRadius, 1.05f, 1.2f, 3, Weights("j_sebo_a"))));
        var legs = Read(With(Tube(Cloth, ClothRadius, 0.85f, 0.95f, 2, Weights("j_asi_a_l"))));
        Assert.Null(BodyRetarget.SplitPoints(top.Parts, top.Skin, [legs]));
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-23")]
    public void SameRefitTwice_SameBytes()
    {
        var (a, b) = Refit(TopBytes(true), LegsBytes(true), seam: true);
        var (c, d) = Refit(TopBytes(true), LegsBytes(true), seam: true);
        Assert.Equal(a.Model, c.Model);
        Assert.Equal(b.Model, d.Model);
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-3")]
    [Trait("Plan", "EDGE-split-waist-seam-17")]
    public void SeamWeightsAuthored_BandFades_NoNewCrease()
    {
        var (parts, skin) = Read(LegsBytes(y0: 0.97f));      // rows 10 mm apart: some inside the 40 mm band
        int vc = parts.Positions.Length / 3;
        var refit = new (string Bone, float W)[]?[vc];
        for (int v = 0; v < vc; v++) refit[v] = [("j_kosi_b", 1f)];
        var fade = new float[vc];
        for (int v = 0; v < vc; v++)
        {
            float along = Split - At(parts, v).Y;
            fade[v] = along <= 0f ? 0f : along >= BodyRetarget.SeamBlendReach ? 1f : along / BodyRetarget.SeamBlendReach;
        }
        // One vertex sharing a place with a seam vertex (a class of the in-model seam weights): it keeps the author's too.
        int ringVertex = Enumerable.Range(0, vc).First(v => fade[v] == 0f);
        int classmate = Enumerable.Range(0, vc).First(v => fade[v] > 0f && fade[v] < 1f);
        var classOf = Enumerable.Repeat(-1, vc).ToArray();
        classOf[ringVertex] = classOf[classmate] = 0;

        BodyRetarget.FadeSeamWeights(skin, refit, fade, classOf);

        Assert.Null(refit[ringVertex]);
        Assert.Null(refit[classmate]);
        for (int v = 0; v < vc; v++)
        {
            if (v == classmate) continue;
            if (fade[v] >= 1f) Assert.Equal([("j_kosi_b", 1f)], refit[v]!);
            else if (fade[v] > 0f)
            {
                float share = refit[v]!.Where(i => i.Bone == "j_kosi_b").Sum(i => i.W);
                Assert.InRange(share, fade[v] - 1e-4f, fade[v] + 1e-4f);
            }
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-18")]
    public void BeltOverSplit_StaysOutside()
    {
        // A belt band over the legs' waist, 3 mm outside the dress, in the legs file: the legs' waist is held back where
        // the author put it and the belt is carried with it — never left inside the dress.
        var belt = Tube(Cloth, ClothRadius + 0.003f, 0.985f, 1.0f, 3, (_, _) => [("j_kosi", 1f)]) with
        {
            Material = "/mt_c0201e0628_dwn_belt.mtrl",
        };
        byte[] legs = SyntheticModel.BuildSurface([], Tube(Cloth, ClothRadius, 0.97f, Split, 3, Weights("j_asi_a_l")), belt);
        byte[] top = TopBytes();
        var (_, legsSeam) = Seams(top, legs);
        var plan = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn", sharedSeam: legsSeam);
        Assert.Null(plan.Report.SeamDropped);
        var parts = ModelPartReader.Read(legs)!;
        var beltVerts = parts.Parts.Where(p => p.Material.Contains("belt", StringComparison.Ordinal) && p.Island < 0)
                             .SelectMany(p => p.Triangles).Distinct().ToList();
        float dressAtWaist = ClothRadius;                       // the held waist, where the author put it
        foreach (int v in beltVerts)
        {
            var p = Moved(parts, plan, v);
            Assert.True(new Vector2(p.X, p.Z).Length() > dressAtWaist, $"belt vertex {v} came inside the dress");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-29")]
    public void BodyCopiedSuit_NoWorse()
    {
        // Both pieces made on the same body and refitted alike (a suit copied off one body): the hold changes nothing it
        // does not have to, adds no fold and is not dropped.
        byte[] top = TopBytes(true), legs = LegsBytes(true);
        var (topSeam, legsSeam) = Seams(top, legs);
        var bareTop = BodyRetarget.Plan(ModelPartReader.Read(top)!, top, [Pair("_top", 0.12f, 0.126f)], "_top");
        var heldTop = BodyRetarget.Plan(ModelPartReader.Read(top)!, top, [Pair("_top", 0.12f, 0.126f)], "_top", sharedSeam: topSeam);
        var bareLegs = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn");
        var heldLegs = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn", sharedSeam: legsSeam);
        Assert.Null(heldTop.Report.SeamDropped);
        Assert.Null(heldLegs.Report.SeamDropped);
        Assert.True(heldTop.Report.Folded <= bareTop.Report.Folded);
        Assert.True(heldLegs.Report.Folded <= bareLegs.Report.Folded);
        Assert.True(RingGap(top, heldTop, legs, heldLegs) <= 5e-5f);
        Assert.True(Inside(legs, heldLegs) <= Inside(legs, bareLegs));
        Assert.True(Inside(top, heldTop) <= Inside(top, bareTop));
    }

    /// <summary>Cloth vertices inside the 0.126 m body after a refit.</summary>
    private static int Inside(byte[] model, BodyRetarget.Planned plan)
    {
        var m = ModelPartReader.Read(model)!;
        return Enumerable.Range(0, m.Positions.Length / 3).Count(v =>
        {
            var p = Moved(m, plan, v);
            return new Vector2(p.X, p.Z).Length() < 0.126f - BodyRetarget.UnfoldBuryTolerance;
        });
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-21")]
    public void HipLevelSplit_NoNewFoldsOrDropped()
    {
        // The split at the hips (0.92 m) rather than the waist, the legs piece short: a steeper band, still no new fold,
        // not dropped, closed.
        const float hip = 0.92f;
        Func<int, float, (string, float)[]> W(string own) => (_, y) => MathF.Abs(y - hip) < 1e-6f ? [("j_kosi", 1f)] : [(own, 1f)];
        byte[] top = SyntheticModel.BuildSurface([], Tube(Cloth, ClothRadius, hip, 1.2f, 7, W("j_sebo_a"), doubleSided: true));
        byte[] legs = SyntheticModel.BuildSurface([], Tube(Cloth, ClothRadius, 0.86f, hip, 3, W("j_asi_a_l"), doubleSided: true));
        var (topSeam, legsSeam) = Seams(top, legs);
        var bare = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn");
        var held = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [Pair("_dwn", 0.12f, 0.126f)], "_dwn", sharedSeam: legsSeam);
        var heldTop = BodyRetarget.Plan(ModelPartReader.Read(top)!, top, [Pair("_top", 0.126f, 0.126f)], "_top", sharedSeam: topSeam);
        Assert.True(held.Report.SeamKept);
        Assert.True(held.Report.Folded <= bare.Report.Folded);
        var t = ModelPartReader.Read(top)!;
        var l = ModelPartReader.Read(legs)!;
        for (int i = 0; i < t.Positions.Length / 3; i++)
        {
            if (MathF.Abs(At(t, i).Y - hip) > 1e-6f) continue;
            for (int j = 0; j < l.Positions.Length / 3; j++)
                if (Vector3.Distance(At(t, i), At(l, j)) < 1e-6f)
                    Assert.True(Vector3.Distance(Moved(t, heldTop, i), Moved(l, held, j)) <= 5e-5f);
        }
    }

    [Fact]
    [Trait("Plan", "AC-split-waist-seam-3")]
    public void BandWeights_FadeSmoothly_ThroughThePlan()
    {
        // Legs rigged to the body bone j_kosi, refitted across bodies onto one rigged to j_kosi_b: without the hold every
        // cloth vertex takes j_kosi_b and the waist parts from the top's j_kosi when posed. With it the ring keeps the
        // author's j_kosi and the band fades to j_kosi_b along the cloth, no edge jumping more than the fade's own slope.
        static byte[] RiggedBody(float r, string bone) => SyntheticModel.BuildSurface([], Tube(Skin, r, 0.7f, 1.4f, 14, (_, _) => [(bone, 1f)], cap: true));
        byte[] src = RiggedBody(0.12f, "j_kosi"), dst = RiggedBody(0.126f, "j_kosi_b");
        var source = ModelPartReader.Read(src)!;
        var target = ModelPartReader.Read(dst)!;
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "_dwn", out var built, out string refusal), refusal);
        var pair = new BodyRetarget.SlotPair("_dwn", built!, target, dst, src);
        byte[] legs = SyntheticModel.BuildSurface([], Tube(Cloth, ClothRadius, 0.9f, Split, 10, (_, _) => [("j_kosi", 1f)]));
        byte[] top = SyntheticModel.BuildSurface([], Tube(Cloth, ClothRadius, Split, 1.2f, 4, (_, _) => [("j_kosi", 1f)]));
        var (_, legsSeam) = Seams(top, legs);
        var held = BodyRetarget.Plan(ModelPartReader.Read(legs)!, legs, [pair], "_dwn", acrossBodies: true, sharedSeam: legsSeam);
        Assert.True(held.Report.SeamKept);

        var parts = ModelPartReader.Read(held.Model)!;
        var skin = ModelSkinReader.Read(held.Model, null, null)!;
        float Share(int v) => BodyRetarget.Influences(skin, v).Where(i => i.Bone == "j_kosi").Sum(i => i.W);
        float worst = 0f, longest = 0f;
        var tris = parts.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).ToArray();
        for (int k = 0; k + 2 < tris.Length; k += 3)
            foreach (var (a, b) in new[] { (tris[k], tris[k + 1]), (tris[k + 1], tris[k + 2]), (tris[k + 2], tris[k]) })
            {
                worst = MathF.Max(worst, MathF.Abs(Share(a) - Share(b)));
                longest = MathF.Max(longest, MathF.Abs(At(parts, a).Y - At(parts, b).Y));
            }
        // Smoothstep's steepest slope is 1.5 over the reach; neighbour averaging only flattens it.
        Assert.True(worst <= 1.5f * longest / BodyRetarget.SeamBlendReach + 0.02f, $"a band edge jumps {worst:0.000}");
        for (int v = 0; v < parts.Positions.Length / 3; v++)
            if (MathF.Abs(At(parts, v).Y - Split) < 1e-6f) Assert.Equal(1f, Share(v), 3);
            else if (Split - At(parts, v).Y > BodyRetarget.SeamBlendReach + 0.011f) Assert.Equal(0f, Share(v), 3);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-24")]
    public void HalfPositions_SeamBytesEqualAuthored()
    {
        // Positions stored as Half4: the written seam positions are the authored half values exactly, in both pieces.
        byte[] top = SyntheticModel.BuildSurface([], halfPositions: true, Tube(Cloth, ClothRadius, Split, 1.2f, 4, Weights("j_sebo_a"), true));
        byte[] legs = SyntheticModel.BuildSurface([], halfPositions: true, Tube(Cloth, ClothRadius, 0.85f, Split, 3, Weights("j_asi_a_l"), true));
        Assert.Equal(14, SecondSkinWriter.Parse(legs).Decls[0].First(e => e.Usage == SecondSkinWriter.UsePosition).Type);
        var (topPlan, legsPlan) = Refit(top, legs, seam: true);
        Assert.True(legsPlan.Report.SeamKept);
        var authored = ModelPartReader.Read(legs)!;
        var writtenTop = ModelPartReader.Read(topPlan.Model)!;
        var writtenLegs = ModelPartReader.Read(legsPlan.Model)!;
        var topAt = Enumerable.Range(0, writtenTop.Positions.Length / 3).Select(v => At(writtenTop, v)).ToHashSet();
        var legsAt = Enumerable.Range(0, writtenLegs.Positions.Length / 3).Select(v => At(writtenLegs, v)).ToHashSet();
        int ring = 0;
        for (int v = 0; v < authored.Positions.Length / 3; v++)
        {
            var p = At(authored, v);
            if (MathF.Abs(p.Y - Split) > 1e-3f) continue;
            ring++;
            Assert.Contains(p, legsAt);         // the same half value, to the bit
            Assert.Contains(p, topAt);
        }
        Assert.Equal(2 * Segments, ring);
    }
}
