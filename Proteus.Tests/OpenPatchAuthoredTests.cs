using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SkinTuckSheet;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// Cloth whose nearest authored skin is an open edge it lies behind, read against the body the garment was made on (see
/// <see cref="BodyRetarget.TargetBody.Fitted"/>), on <see cref="SkinTuckSheet"/>'s sheets. The trap, as Dei Gratia set it:
/// a top's own skin is an upper-chest patch over rows <see cref="SkinTuckSheet.Edge"/> and up, its normal tilted up and
/// out, and the cloth below it lands on the patch's bottom edge, where that normal reads it 10-75 mm "inside" — so the
/// push-out took it for cloth tucked under the skin on purpose and left it buried in a grown bust. Each test shows its
/// fixture sets that trap before it judges the rule, and judges the interior columns well below the edge, where neither
/// the sheet's side edges nor the patch's own edge in the new body are near.
/// </summary>
public class OpenPatchAuthoredTests
{
    private static readonly BodyRetarget.Tuning Off = new(NoOpenEdgeFit: true);

    private static readonly HashSet<int> NoneHeld = [];

    /// <summary>A refit, the solve's nodes, its last stage, the points the rule took in, and its log.</summary>
    private sealed record Run(BodyRetarget.Planned Plan, ModelParts Garment, BodyRetarget.Sets Sets, Vec3[] Final,
                              bool[] Fitted, float[] NeedDirect, List<string> Log);

    private static Run Refit(byte[] top, BodyRetarget.SlotPair[] pairs, BodyRetarget.Tuning? tuning = null, string slot = "_top",
                             bool clearBody = false, bool replaceSkin = true)
    {
        var garment = ModelPartReader.Read(top)!;
        Vec3[]? final = null;
        float[]? fitted = null, needDirect = null;
        var log = new List<string>();
        BodyRetarget.Planned Plan()
        {
            BodyRetarget.PushTrace.Value = (k, v) =>
            {
                if (k == "fitted") fitted = v;
                if (k == "need-direct") needDirect = v;
            };
            try
            {
                return BodyRetarget.Plan(garment, top, pairs, slot, held: NoneHeld, replaceSkin: replaceSkin, acrossBodies: true,
                                         clearBody: clearBody, cutHidden: true, log: log.Add, trace: (_, d) => final = d);
            }
            finally { BodyRetarget.PushTrace.Value = null; }
        }
        var plan = tuning != null ? BodyRetarget.WithTuning(tuning, Plan) : Plan();
        var sets = BodyRetarget.Sets.From(garment, NoneHeld, BodyRetarget.PartedSkinMeshes(garment, pairs, slot, replaceSkin, null));
        Assert.Equal(sets.NodeCount, final!.Length);
        var taken = fitted is { } f ? Array.ConvertAll(f, x => x > 0f) : new bool[sets.NodeCount];
        return new Run(plan, garment, sets, final, taken, needDirect ?? new float[sets.NodeCount], log);
    }

    private static Vector3 Authored(BodyRetarget.Sets sets, int n) => new(sets.NodeAt[n].X, sets.NodeAt[n].Y, sets.NodeAt[n].Z);

    private static Vector3 Final(Run r, int n) => Authored(r.Sets, n) + new Vector3(r.Final[n].X, r.Final[n].Y, r.Final[n].Z);

    /// <summary>The cloth nodes judged: interior columns 2-6, rows <paramref name="from"/>..<paramref name="to"/>.</summary>
    private static List<int> Judged(Run r, int from = 2, int to = Edge - 4)
        => r.Sets.ClothNodes.Where(n =>
        {
            var p = Authored(r.Sets, n);
            int row = (int)MathF.Round((p.Y - Bottom) / Step), column = (int)MathF.Round(p.X / Step + (Columns - 1) / 2f);
            return row >= from && row <= to && column is >= 2 and <= 6;
        }).ToList();

    private static BodySurface Surface(byte[] body) => new(ModelPartReader.Read(body)!, 0.01f);

    /// <summary>The trap, read the way the push-out reads it: the BEFORE surface's nearest landing for some cloth node is past
    /// an open edge and reads the node inside.</summary>
    private static void AssertTrap(Run r, BodyRetarget.SlotPair[] pairs, string slot = "_top", int row = 5)
    {
        var before = BodyRetarget.TargetBody.Build(pairs, slot, r.Garment, before: true);
        var node = Judged(r, row, row).First();
        var p = Authored(r.Sets, node);
        Assert.True(before.Nearest(p, BodyRetarget.AuthoredProbeRange, out var hit, out _, out bool pastEdge));
        Assert.True(Vector3.Dot(p - hit.Point, hit.Normal) < 0f, "the fixture's cloth does not read inside the patch");
        Assert.True(pastEdge, "the fixture's cloth does not land past the patch's edge");
    }

    private static Func<int, int, float> At(float z) => (_, _) => z;

    // ── the rule ──

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-1")]
    [Trait("Plan", "AC-open-patch-authored-2")]
    public void Cloth_below_an_open_skin_edge_comes_out_of_the_new_bust()
    {
        byte[] top = OpenPatchTop(At(0.003f), UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f))];
        var off = Refit(top, pairs, Off);
        var on = Refit(top, pairs);
        AssertTrap(on, pairs);
        var target = Surface(Bust(0.02f));
        var judged = Judged(on);

        Assert.Contains(judged, n => -Standoff(Final(off, n), target) > 0.005f);
        Assert.All(judged, n => Assert.True(on.Fitted[n], $"node {n} not taken in"));
        Assert.All(judged, n => Assert.True(Standoff(Final(on, n), target) >= -(BodyRetarget.Clearance + 1e-4f),
                                            $"node {n} {-Standoff(Final(on, n), target) * 1000:0.0} mm inside"));
        Assert.Equal(0, on.Plan.Report.Folded);
        // Against the authored garment, among the judged columns: the sheet's own side columns land past the new body's
        // side edge, where the push is gated (EDGE-16), and stay as they were while their neighbours come out.
        var laid = Laid(top, on.Plan);
        Assert.Equal(0, FoldedAgainstAuthored(on.Garment, laid));
        var stretched = new List<string>();
        ClothStretch(on.Garment, laid, stretched.Add);
        static bool Interior(Vector3 p) => MathF.Abs(p.X) <= X(6) + 1e-4f;
        Assert.DoesNotContain(stretched, s =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"edge (\d+)-(\d+)");
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            Vector3 P(int v) => new(on.Garment.Positions[v * 3], on.Garment.Positions[v * 3 + 1], on.Garment.Positions[v * 3 + 2]);
            return Interior(P(a)) && Interior(P(b));
        });
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-19")]
    public void A_top_made_over_the_bodys_own_smallclothes_reads_against_them()
    {
        byte[] top = OpenPatchTop(At(0.003f), UpAndOut);
        byte[] filled = WornBody((3, 8), (1, 7), filled: true), open = WornBody((3, 8), (1, 7), filled: false);
        var p = new Vector3(X(4), Y(5), 0.003f);

        // Read on its skin alone the body has a hole there, and the cloth over it lands on its rim; read as worn, on the
        // smallclothes that fill it, 3 mm under the cloth.
        var source = ModelPartReader.Read(filled)!;
        Assert.True(new BodySurface(source, 0.01f).Nearest(p, 0.15f, out var bare));
        Assert.True(BodyRetarget.PastEdge(bare, BodyRetarget.SkinEdges.Of(source)));
        Assert.True(BodySurface.OfBodyWorn(source, 0.01f).Nearest(p, 0.15f, out var worn));
        Assert.False(BodyRetarget.PastEdge(worn, BodyRetarget.SkinEdges.OfWorn(source)));
        Assert.Equal(0.003f, Vector3.Dot(p - worn.Point, worn.Normal), 4);

        BodyRetarget.SlotPair[] pairs = [Undershot(filled, Bust(0.02f))];
        var on = Refit(top, pairs);
        AssertTrap(on, pairs);
        var target = Surface(Bust(0.02f));
        var overHole = Judged(on, 4, 6).Where(n => Authored(on.Sets, n).X is > -0.025f and < 0.025f).ToList();
        Assert.NotEmpty(overHole);
        Assert.All(overHole, n => Assert.True(on.Fitted[n], $"node {n} not taken in"));
        Assert.All(overHole, n => Assert.True(Standoff(Final(on, n), target) >= -(BodyRetarget.Clearance + 1e-4f)));

        // Unfilled, the hole's rim is an open edge of the body as well: no reading, and nothing over it taken in.
        var bare2 = Refit(top, [Undershot(open, Bust(0.02f))]);
        Assert.All(overHole, n => Assert.False(bare2.Fitted[n], $"node {n} taken in over an empty hole"));
    }

    public static TheoryData<float> WithinSlack => new() { 0.002f, 0.0039f };

    [Theory]
    [MemberData(nameof(WithinSlack))]
    [Trait("Plan", "EDGE-open-patch-authored-20")]
    public void Cloth_lying_just_inside_the_made_on_body_joins_at_the_clearance(float depth)
    {
        byte[] top = OpenPatchTop(At(-depth), UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f))];
        var on = Refit(top, pairs);
        AssertTrap(on, pairs);
        var target = Surface(Bust(0.02f));
        Assert.All(Judged(on), n =>
        {
            Assert.True(on.Fitted[n], $"node {n} not taken in");
            Assert.True(Standoff(Final(on, n), target) >= BodyRetarget.Clearance - 1e-4f,
                        $"node {n} {Standoff(Final(on, n), target) * 1000:0.00} mm off");
        });
    }

    public static TheoryData<float> PastSlack => new() { 0.0041f, 0.006f, 0.012f };

    [Theory]
    [MemberData(nameof(PastSlack))]
    [Trait("Plan", "EDGE-open-patch-authored-21")]
    [Trait("Plan", "EDGE-open-patch-authored-2")]
    [Trait("Plan", "AC-open-patch-authored-4")]
    public void Cloth_buried_in_the_made_on_body_refits_as_before(float depth)
    {
        byte[] top = OpenPatchTop(At(-depth), UpAndOut);
        foreach (var pair in new[] { Undershot(Bust(0f), Bust(0.02f)), Grown(Bust(0f), Bust(0.02f)) })
        {
            var on = Refit(top, [pair]);
            AssertTrap(on, [pair]);
            Assert.DoesNotContain(true, on.Fitted);
            Assert.Equal(Sha(Refit(top, [pair], Off).Plan.Model), Sha(on.Plan.Model));
        }
        // And the game's way of burying it, under a flat patch.
        byte[] buried = BuriedTop(0.012f);
        foreach (var pair in new[] { Undershot(Bust(0f), Bust(0.02f)), Grown(Bust(0f), Bust(0.02f)) })
            Assert.Equal(Sha(Refit(buried, [pair], Off).Plan.Model), Sha(Refit(buried, [pair]).Plan.Model));
    }

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-1")]
    public void A_point_deeper_than_the_slack_stays_and_its_neighbours_still_come_out()
    {
        byte[] top = OpenPatchTop((r, c) => r == 5 && c == 4 ? -0.006f : 0.003f, UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f))];
        var off = Refit(top, pairs, Off);
        var on = Refit(top, pairs);
        var target = Surface(Bust(0.02f));
        int deep = Judged(on, 5, 5).Single(n => MathF.Abs(Authored(on.Sets, n).X - X(4)) < 1e-4f);
        Assert.False(on.Fitted[deep]);
        Assert.True(-Standoff(Final(on, deep), target) <= -Standoff(Final(off, deep), target) + 1e-4f);
        foreach (int n in on.Sets.Adj[deep].Where(m => on.Fitted[m]))
            Assert.True(-Standoff(Final(on, n), target) <= 0.005f, $"neighbour {n} {-Standoff(Final(on, n), target) * 1000:0.0} mm inside");
    }

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-4")]
    public void The_sheet_goldens_take_in_no_point()
    {
        var cases = new List<(byte[] Top, BodyRetarget.SlotPair Pair)>
        {
            FloatingNeckline(), FloatingNeckline(coarse: true), FloatingNeckline(lining: 0.004f),
            (BuriedTop(0.012f), Grown(Bust(0f), Bust(0.02f))), (CoarseBuriedTop(0.012f), Grown(Bust(0f), Bust(0.02f))),
            (LooseTop(), Grown(Bust(0f), Bust(0.02f))),
        };
        foreach (var (top, pair) in cases)
        {
            var garment = ModelPartReader.Read(top)!;
            // The automatic refit's mod garment, and the Studio's cut (the size set's call).
            Func<BodyRetarget.Planned>[] paths =
            [
                () => RefitCore.PlanRefit(garment, top, [pair], "_top", across: true, gameGear: false, out _),
                () => BodyRetarget.Plan(garment, top, [pair], "_top", held: NoneHeld, replaceSkin: true, acrossBodies: true,
                                        clearBody: false, cutHidden: true,
                                        keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment))),
            ];
            foreach (var path in paths)
            {
                float[]? fitted = null;
                BodyRetarget.PushTrace.Value = (k, v) => { if (k == "fitted") fitted = v; };
                BodyRetarget.Planned on;
                try { on = path(); }
                finally { BodyRetarget.PushTrace.Value = null; }
                Assert.True(fitted == null || fitted.All(f => f == 0f));
                Assert.Equal(Sha(BodyRetarget.WithTuning(Off, path).Model), Sha(on.Model));
            }
        }
    }

    // ── the new body's side ──

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-6")]
    [Trait("Plan", "EDGE-open-patch-authored-8")]
    public void Cloth_below_a_downward_edge_outside_both_bodies_is_not_moved()
    {
        // The patch over rows Edge and up faces down and out, as an underbust does; the cloth below stands 3 mm off the
        // body, which sits 20 mm behind the patch there.
        var downAndOut = Vector3.Normalize(new Vector3(0f, -0.6f, 0.8f));
        byte[] body = Stepped(0.02f);
        byte[] top = OpenPatchTop(At(-0.017f), downAndOut);
        BodyRetarget.SlotPair[] pairs = [Grown(body, body)];
        var off = Refit(top, pairs, Off);
        var on = Refit(top, pairs);
        AssertTrap(on, pairs, row: Edge - 1);

        // Ungated, the moved patch's edge would read the row below it inside — a push, and an underbust lift's seed.
        var after = BodyRetarget.TargetBody.Build(pairs, null, ModelPartReader.Read(top)!, before: false);
        int node = Judged(on, Edge - 1, Edge - 1).First();
        Assert.True(on.Fitted[node]);
        Assert.True(after.Deepest(Final(on, node), BodyRetarget.PushProbeRange, out var hit));
        float s = Vector3.Dot(Final(on, node) - hit.Point, hit.Normal);
        Assert.True(s < 0f && s >= -BodyRetarget.ClearDepth && BodyRetarget.IsUnderbust(hit.Normal), $"{s * 1000:0.0} mm");

        Assert.Equal(0, on.Plan.Report.Pushed);
        Assert.Equal(Sha(off.Plan.Model), Sha(on.Plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-15")]
    public void A_point_with_only_misread_landings_in_reach_reads_nothing()
    {
        // The only skin in reach is a patch's open edge reading the point inside: the gated reading finds nothing, so the
        // point takes no direction from it (and any spread push goes along its own normal).
        var patch = ModelPartReader.Read(OpenPatchTop(At(0.003f), UpAndOut, clothTo: 0))!;
        var after = BodyRetarget.TargetBody.Build([], null, patch, before: false);
        var p = new Vector3(X(4), Y(Edge - 1), 0.003f);
        Assert.True(after.Deepest(p, BodyRetarget.PushProbeRange, out var hit));
        Assert.True(Vector3.Dot(p - hit.Point, hit.Normal) < 0f);
        Assert.False(after.Deepest(p, BodyRetarget.PushProbeRange, out _, openEdgeOutside: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-16")]
    public void A_fitted_point_past_the_new_bodys_own_open_edge_takes_no_push_from_it()
    {
        // The new body starts at row 4 on its breast's rise, its bottom edge facing down and out; the cloth below it, taken
        // in by the made-on body, would read inside that edge.
        byte[] target = BodyFrom(4, (r, _) => Ridge(r, 0.02f));
        byte[] top = OpenPatchTop(At(0.003f), UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), target)];
        var on = Refit(top, pairs);
        AssertTrap(on, pairs, row: 3);
        var edges = BodyRetarget.SkinEdges.Of(ModelPartReader.Read(target)!);
        var surface = Surface(target);
        foreach (int n in Judged(on, 3, 3))
        {
            Assert.True(on.Fitted[n]);
            Assert.True(surface.Nearest(Authored(on.Sets, n), BodyRetarget.PushProbeRange, out var hit));
            Assert.True(Vector3.Dot(Authored(on.Sets, n) - hit.Point, hit.Normal) < 0f && BodyRetarget.PastEdge(hit, edges));
            Assert.Equal(0f, on.NeedDirect[n]);
        }
    }

    // ── the made-on body ──

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-17")]
    public void A_point_exactly_on_the_made_on_body_joins_with_no_standoff()
    {
        // On the plane, half a cell off the body's vertices: cloth exactly on a vertex is snapped by the transfer and is
        // the surface itself, never the push-out's.
        byte[] top = OpenPatchTop(At(0f), UpAndOut, clothShift: new Vector3(Step / 2, Step / 2, 0f));
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f))];
        var on = Refit(top, pairs);
        var before = BodyRetarget.TargetBody.Build(pairs, "_top", on.Garment, before: true);
        var target = Surface(Bust(0.02f));
        foreach (int n in Judged(on))
        {
            Assert.True(before.Fitted(Authored(on.Sets, n), BodyRetarget.AuthoredProbeRange, out float standoff));
            Assert.Equal(0f, standoff, 5);
            Assert.True(on.Fitted[n]);
            Assert.True(Standoff(Final(on, n), target) >= -1e-4f);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-18")]
    public void A_second_body_for_the_garments_own_slot_is_read_too()
    {
        var garment = ModelPartReader.Read(OpenPatchTop(At(0.003f), UpAndOut))!;
        BodyRetarget.SlotPair[] pairs = [Undershot(BodyFrom(Edge), Bust(0.02f)), Undershot(Bust(0f), Bust(0.02f))];
        var before = BodyRetarget.TargetBody.Build(pairs, "_top", garment, before: true);
        Assert.True(before.Fitted(new Vector3(X(4), Y(5), 0.003f), BodyRetarget.AuthoredProbeRange, out float standoff));
        Assert.Equal(0.003f, standoff, 4);
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-4")]
    public void A_fitted_body_that_ends_above_the_cloth_reads_nothing()
    {
        byte[] top = OpenPatchTop(At(0.003f), UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Undershot(BodyFrom(Edge), Bust(0.02f))];
        var before = BodyRetarget.TargetBody.Build(pairs, "_top", ModelPartReader.Read(top)!, before: true);
        Assert.False(before.Fitted(new Vector3(X(4), Y(5), 0.003f), BodyRetarget.AuthoredProbeRange, out _));
        Assert.Equal(Sha(Refit(top, pairs, Off).Plan.Model), Sha(Refit(top, pairs).Plan.Model));
        // Nor one whose own nearest skin is out of reach.
        Assert.False(before.Fitted(new Vector3(X(4), Y(5), 0.3f), BodyRetarget.AuthoredProbeRange, out _));
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-22")]
    public void A_made_on_body_without_smallclothes_reads_as_its_skin()
    {
        var body = ModelPartReader.Read(Bust(0.02f))!;
        var p = new Vector3(X(4), Y(5), 0.03f);
        Assert.True(new BodySurface(body, 0.01f).Nearest(p, 0.15f, out var skin));
        Assert.True(BodySurface.OfBodyWorn(body, 0.01f).Nearest(p, 0.15f, out var worn));
        Assert.Equal(skin.Point, worn.Point);
        Assert.Equal(Vector3.Dot(p - skin.Point, skin.Normal), Vector3.Dot(p - worn.Point, worn.Normal));
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-23")]
    public void Smallclothes_never_join_the_drawn_surfaces()
    {
        byte[] filled = WornBody((3, 8), (1, 7), filled: true);
        var pair = Grown(filled, filled);
        var drawn = BodyRetarget.TargetBody.Build([pair], null, null, before: false);
        Assert.Equal(new BodySurface(ModelPartReader.Read(filled)!, 0.01f).SkinVertices.Count, drawn.Points().Count());
        // A garment the rule takes nothing of refits onto it as before.
        byte[] tucked = OpenPatchTop(At(0f), UpAndOut, clothTo: 0, underPatch: (11, 12, -0.002f));
        Assert.Equal(Sha(Refit(tucked, [pair], Off).Plan.Model), Sha(Refit(tucked, [pair]).Plan.Model));
    }

    // ── left as it was ──

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-4")]
    [Trait("Plan", "EDGE-open-patch-authored-1")]
    public void Cloth_tucked_under_the_patch_refits_as_before()
    {
        byte[] top = OpenPatchTop(At(0f), UpAndOut, clothTo: 0, underPatch: (11, 12, -0.002f));
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f))];
        Assert.Equal(Sha(Refit(top, pairs, Off).Plan.Model), Sha(Refit(top, pairs).Plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-3")]
    public void Without_a_body_for_the_garments_own_slot_the_refit_is_as_before()
    {
        byte[] top = OpenPatchTop(At(0.003f), UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Grown(Bust(0f), Bust(0.02f), "_dwn")];
        var on = Refit(top, pairs);
        Assert.DoesNotContain(true, on.Fitted);
        Assert.Equal(Sha(Refit(top, pairs, Off).Plan.Model), Sha(on.Plan.Model));
    }

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-4")]
    [Trait("Plan", "EDGE-open-patch-authored-13")]
    public void A_shape_only_pair_for_the_garments_own_slot_is_no_fitted_body()
    {
        byte[] top = OpenPatchTop(At(0.003f), UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f)) with { ShapeOnly = true }];
        var on = Refit(top, pairs);
        Assert.DoesNotContain(true, on.Fitted);
        Assert.Equal(Sha(Refit(top, pairs, Off).Plan.Model), Sha(on.Plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-5")]
    public void A_seam_through_the_patch_is_not_an_open_edge()
    {
        var garment = ModelPartReader.Read(OpenPatchTop(At(0f), UpAndOut, clothTo: 0, seamColumn: 4))!;
        var before = BodyRetarget.TargetBody.Build([], "_top", garment, before: true);
        // Under the seam, inside the patch: its landing is on the seam's edge, which two faces share once welded.
        var p = new Vector3(X(4), Y(11) + 0.004f, -0.002f);
        Assert.True(before.Nearest(p, BodyRetarget.AuthoredProbeRange, out var hit, out _, out bool pastEdge));
        Assert.True(Vector3.Dot(p - hit.Point, hit.Normal) < 0f);
        Assert.False(pastEdge);
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-6")]
    public void A_landing_on_the_patchs_corner_is_past_its_edge()
    {
        var garment = ModelPartReader.Read(OpenPatchTop(At(0f), UpAndOut, clothTo: 0))!;
        var before = BodyRetarget.TargetBody.Build([], "_top", garment, before: true);
        var p = new Vector3(X(0) - 0.02f, Y(Edge) - 0.02f, 0f);
        Assert.True(before.Nearest(p, BodyRetarget.AuthoredProbeRange, out _, out _, out bool pastEdge));
        Assert.True(pastEdge);
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-7")]
    public void A_legs_garments_shallow_chest_clip_is_as_before()
    {
        // Legs cloth 3 mm inside the chest beside it, which a legs garment's author never saw: included already, unchanged.
        byte[] legs = Top(_ => -0.003f, withSkin: false);
        BodyRetarget.SlotPair[] pairs = [Grown(Bust(0f), Bust(0.02f))];
        var on = Refit(legs, pairs, slot: "_dwn");
        Assert.DoesNotContain(true, on.Fitted);
        Assert.Equal(Sha(Refit(legs, pairs, Off, slot: "_dwn").Plan.Model), Sha(on.Plan.Model));
    }

    public static TheoryData<float, bool> LegsDepths => new() { { 0.003f, true }, { -0.002f, true }, { -0.012f, false } };

    [Theory]
    [MemberData(nameof(LegsDepths))]
    [Trait("Plan", "EDGE-open-patch-authored-14")]
    public void A_legs_garment_below_the_chests_waist_edge_is_read_against_its_legs(float z, bool joins)
    {
        // The chest starts at the waist (row Edge), its edge facing up and out; the legs cloth below lands on it, read deep
        // "inside", and is read against the legs it was made on instead.
        byte[] legs = Top(_ => z, withSkin: false);
        BodyRetarget.SlotPair[] pairs =
        [
            Undershot(BodyFrom(Edge, normal: UpAndOut), BodyFrom(Edge, normal: UpAndOut)),
            Undershot(Bust(0f), Bust(0.02f), "_dwn"),
        ];
        var on = Refit(legs, pairs, slot: "_dwn");
        AssertTrap(on, pairs, "_dwn");
        if (joins) Assert.Contains(true, on.Fitted);
        else
        {
            Assert.DoesNotContain(true, on.Fitted);
            Assert.Equal(Sha(Refit(legs, pairs, Off, slot: "_dwn").Plan.Model), Sha(on.Plan.Model));
        }
    }

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-5")]
    [Trait("Plan", "EDGE-open-patch-authored-9")]
    public void Clearing_the_body_is_as_before()
    {
        byte[] top = OpenPatchTop(At(0.003f), UpAndOut);
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f))];
        Assert.Equal(Sha(Refit(top, pairs, Off, clearBody: true).Plan.Model), Sha(Refit(top, pairs, clearBody: true).Plan.Model));
    }

    [Fact]
    [Trait("Plan", "EDGE-open-patch-authored-11")]
    public void An_edge_landing_in_front_of_the_patch_is_as_before()
    {
        byte[] top = OpenPatchTop(At(0.003f), Vector3.UnitZ);
        BodyRetarget.SlotPair[] pairs = [Undershot(Bust(0f), Bust(0.02f))];
        var on = Refit(top, pairs);
        Assert.DoesNotContain(true, on.Fitted);
        Assert.Equal(Sha(Refit(top, pairs, Off).Plan.Model), Sha(on.Plan.Model));
    }

    [Fact]
    [Trait("Plan", "AC-open-patch-authored-8")]
    public void No_fitted_points_logs_no_fitted_line()
    {
        byte[] top = OpenPatchTop(At(0f), UpAndOut, clothTo: 0, underPatch: (11, 12, -0.002f));
        var on = Refit(top, [Undershot(Bust(0f), Bust(0.02f))]);
        Assert.DoesNotContain(on.Log, l => l.StartsWith("fitted: ", StringComparison.Ordinal));
        Assert.Equal(0, on.Plan.Report.OpenEdgeFitted);
    }
}
