using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// A piece of cloth the author covered with another stays behind it. Reported on a tank top over a bra: after a refit
/// onto a larger breast the bra came through the top across the front.
/// </summary>
public class LayerOrderTests
{
    private const float Gap = 0.003f;
    private const float Step = 0.005f;
    private const int Side = 5;

    /// <summary>Two flat sheets facing +Z, the second <see cref="Gap"/> in front of the first: two pieces, one model.</summary>
    private static ModelParts Layered(string innerMaterial, string outerMaterial)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var inner = new List<int>();
        var outer = new List<int>();
        for (int layer = 0; layer < 2; layer++)
        {
            int start = pos.Count / 3;
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                {
                    pos.AddRange([x * Step, y * Step, layer * Gap]);
                    nrm.AddRange([0f, 0f, 1f]);
                }
            var tris = layer == 0 ? inner : outer;
            for (int y = 0; y + 1 < Side; y++)
                for (int x = 0; x + 1 < Side; x++)
                {
                    int a = start + y * Side + x;
                    tris.AddRange([a, a + 1, a + Side + 1, a, a + Side + 1, a + Side]);
                }
        }
        var p = pos.ToArray();
        ModelPart Part(int sub, string material, int[] tris) => new()
        {
            Mesh = 0, Submesh = sub, Island = -1, Label = $"1.{sub + 1}", Material = material, Triangles = tris,
            Ordinals = Enumerable.Range(0, tris.Length / 3).ToArray(), AttributeMask = 0,
            Min = Vector3.Zero, Max = new Vector3(Side * Step, Side * Step, Gap), Toggleable = true,
        };
        return new ModelParts
        {
            Positions = p,
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, p.Length / 3)],
            Parts = [Part(0, innerMaterial, inner.ToArray()), Part(1, outerMaterial, outer.ToArray())],
            AttributeNames = [],
            Min = Vector3.Zero,
            Max = new Vector3(Side * Step, Side * Step, Gap),
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    /// <summary>The middle point of the inner sheet, as a vertex.</summary>
    private const int Middle = (Side / 2) * Side + Side / 2;

    private static Vec3[] ThroughTheTop(BodyRetarget.Sets sets)
    {
        // Every point carried 10 mm out with the body; the middle of the inner sheet 5 mm further, through the outer.
        var delta = new Vec3[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++) delta[n] = new Vec3(0f, 0f, 0.01f);
        delta[sets.NodeOf[Middle]] = new Vec3(0f, 0f, 0.015f);
        return delta;
    }

    [Fact]
    public void A_covered_point_brought_through_its_cover_goes_back_behind_it()
    {
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered);
        Assert.Contains(covered, c => c.Node == sets.NodeOf[Middle]);

        var delta = ThroughTheTop(sets);
        var outerBefore = Enumerable.Range(Side * Side, Side * Side).Select(v => delta[sets.NodeOf[v]]).ToArray();
        BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered);

        // Behind the outer sheet (which is now at 3 + 10 mm), by the kept gap; the inner sheet was authored at 0.
        float z = delta[sets.NodeOf[Middle]].Z;
        Assert.True(z <= Gap + 0.01f - BodyRetarget.LayerOrderGap + 1e-5f, $"inner middle at {z * 1000:F2} mm");
        // The cover is the piece seen, and stays where it was put.
        Assert.Equal(outerBefore, Enumerable.Range(Side * Side, Side * Side).Select(v => delta[sets.NodeOf[v]]));
    }

    [Fact]
    public void Layers_that_kept_their_order_are_left_alone()
    {
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var delta = new Vec3[sets.NodeCount];
        for (int n = 0; n < sets.NodeCount; n++) delta[n] = new Vec3(0f, 0f, 0.01f);
        var was = (Vec3[])delta.Clone();

        Assert.Equal(0, BodyRetarget.KeepLayerOrder(sets, BodyRetarget.CoveredPoints(sets, layered), delta, null, layered));
        Assert.Equal(was, delta);
    }

    [Fact]
    public void A_point_facing_in_is_not_covered_by_the_layer_beneath_it()
    {
        // The outer sheet faces IN, as a lining's inside does: along its normal lies the inner sheet, which is beneath
        // it, not over it. Kept "behind" that, it would be pushed outward.
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        for (int v = Side * Side; v < 2 * Side * Side; v++) garment.Normals[v * 3 + 2] = -1f;
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered, BodyRetarget.FacesOneWay(garment, sets));

        var outer = Enumerable.Range(Side * Side, Side * Side).Select(v => sets.NodeOf[v]).ToHashSet();
        Assert.DoesNotContain(covered, c => outer.Contains(c.Node));
    }

    // ── the skin floor: no move of the layer order takes cloth deeper into the drawn skin than it stood ────────────────
    // Slay Ride on Rue+ Yiggle Large: the fur trim's lifts, faded onto the bodice beside it, drove the bodice up to 19 mm
    // into the breast — the pull is held to the skin, the lift was not.

    /// <summary>The drawn skin under the sheets, facing +Z, <see cref="SkinBelow"/> under the inner sheet's middle.</summary>
    private static BodyRetarget.TargetBody FlatSkin() => Skin(0f);

    /// <summary>
    /// The drawn skin under the sheets, <see cref="SkinBelow"/> under the inner sheet's middle and falling away from it by
    /// <paramref name="curve"/> times the squared distance (per metre): a dome, as a breast is under the bodice.
    /// </summary>
    private static BodyRetarget.TargetBody Skin(float curve, float height = -SkinBelow)
    {
        const int count = 49;
        const float step = 0.00125f, from = -0.02f;
        const float mid = (Side - 1) * Step / 2f;
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();
        for (int i = 0; i < count; i++)
        for (int k = 0; k < count; k++)
        {
            float x = from + i * step, y = from + k * step, dx = x - mid, dy = y - mid;
            pos.AddRange([x, y, height - curve * (dx * dx + dy * dy)]);
            var n = Vector3.Normalize(new Vector3(2f * curve * dx, 2f * curve * dy, 1f));
            nrm.AddRange([n.X, n.Y, n.Z]);
        }
        for (int i = 0; i + 1 < count; i++)
        for (int k = 0; k + 1 < count; k++)
        {
            int a = i * count + k, b = a + count, c = a + 1, d = b + 1;
            tris.AddRange([a, b, d, a, d, c]);
        }
        var p = pos.ToArray();
        var min = new Vector3(from, from, MathF.Min(height, height - curve * 0.0008f));
        var max = new Vector3(from + (count - 1) * step, from + (count - 1) * step, MathF.Max(height, height - curve * 0.0008f));
        var body = new ModelParts
        {
            Positions = p,
            Normals = nrm.ToArray(),
            MeshSpans = [new MeshSpan(0, 0, p.Length / 3)],
            Parts =
            [
                new ModelPart
                {
                    Mesh = 0, Submesh = 0, Island = -1, Label = "0.0", Material = "/mt_c0201b0001_bibo.mtrl",
                    Triangles = tris.ToArray(), Ordinals = Enumerable.Range(0, tris.Count / 3).ToArray(),
                    AttributeMask = 0, Min = min, Max = max, Toggleable = true,
                },
            ],
            AttributeNames = [],
            Min = min,
            Max = max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
        Assert.True(IdentityCorrespondence.TryBuild(body, body, "chest", out var same, out string why), why);
        return BodyRetarget.TargetBody.Build([new BodyRetarget.SlotPair("_top", same!, body)], null, null, before: false);
    }

    private const float SkinBelow = 0.0005f;

    /// <summary>
    /// The outer sheet's middle point taken for covered by the inner sheet's face under it, and dragged 5 mm down through
    /// that face into the skin: it has no room to go back, so the whole shortfall lifts the inner sheet away from it —
    /// which here is into the body, as the fur trim's lift was on the bodice beside it.
    /// </summary>
    private static (BodyRetarget.Sets Sets, bool[] Layered, List<BodyRetarget.Covered> Covered, Vec3[] Delta) LiftedInward()
    {
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        int a = Middle, b = Middle + 1, c = Middle + Side + 1;   // the inner face under the outer middle, wound +Z
        // Facing -1: away from the covered point as authored, which lay above the face.
        var covered = new List<BodyRetarget.Covered>
        {
            new(sets.NodeOf[Side * Side + Middle], sets.NodeOf[a], sets.NodeOf[b], sets.NodeOf[c], 0f, 0f, Gap, -1f),
        };
        var delta = new Vec3[sets.NodeCount];
        delta[sets.NodeOf[Side * Side + Middle]] = new Vec3(0f, 0f, -0.005f);
        return (sets, layered, covered, delta);
    }

    private static float Standoff(BodyRetarget.Sets sets, Vec3[] delta, int vertex)
        => sets.NodeAt[sets.NodeOf[vertex]].Z + delta[sets.NodeOf[vertex]].Z + SkinBelow;

    /// <summary>How far a vertex stands off the drawn skin, signed: negative inside.</summary>
    private static float Off(BodyRetarget.TargetBody drawn, BodyRetarget.Sets sets, Vec3[] delta, int vertex)
    {
        int n = sets.NodeOf[vertex];
        var p = new Vector3(sets.NodeAt[n].X + delta[n].X, sets.NodeAt[n].Y + delta[n].Y, sets.NodeAt[n].Z + delta[n].Z);
        Assert.True(drawn.Deepest(p, 0.03f, out var hit), $"vertex {vertex} found no skin");
        return Vector3.Dot(p - hit.Point, hit.Normal);
    }

    [Theory]
    [InlineData(0f)]    // flat
    [InlineData(8f)]    // a dome falling 0.8 mm to the sheet's edges (1.6 mm to its corners), as a breast under a bodice
    [InlineData(-100f)] // a bowl, as the crease under a breast: a move along it, held only by the plane at its start, sinks
    [Trait("Plan", "AC-bodice-nipple-clip-5")]
    [Trait("Plan", "EDGE-bodice-nipple-clip-5")]
    public void A_cover_lift_spread_inward_never_sinks_cloth_into_the_skin(float curve)
    {
        var drawn = Skin(curve);
        var (_, _, _, start) = LiftedInward();

        // Without the floor, the inner sheet is lifted into the skin: the mechanism, reproduced.
        var (sets, layered, covered, delta) = LiftedInward();
        var startOff = Enumerable.Range(0, Side * Side).Select(v => Off(drawn, sets, start, v)).ToArray();
        BodyRetarget.WithTuning(BodyRetarget.Tuned with { NoLayerFloor = true },
                                () => BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered, drawn, floor: true));
        float sunk = Enumerable.Range(0, Side * Side).Min(v => Off(drawn, sets, delta, v));
        Assert.True(sunk < -0.001f, $"without the floor the inner sheet should sink into the skin, lowest {sunk * 1000:F2} mm");

        // With it, over all three rounds, no inner point ends deeper than it stood or the skin, whichever is less deep.
        (sets, layered, covered, delta) = LiftedInward();
        BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered, drawn, floor: true);
        for (int v = 0; v < Side * Side; v++)
        {
            float off = Off(drawn, sets, delta, v);
            Assert.True(off >= MathF.Min(startOff[v], 0f) - 0.0001f,
                        $"inner vertex {v} at {off * 1000:F2} mm off the skin, from {startOff[v] * 1000:F2}");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-bodice-nipple-clip-2")]
    public void A_node_without_a_skin_reading_is_not_floored()
    {
        // The skin 50 mm below the sheets, past the push-out's probe: no reading anywhere, so nothing to floor.
        var (sets, layered, covered, floored) = LiftedInward();
        for (int n = 0; n < sets.NodeCount; n++) floored[n] = new Vec3(floored[n].X, floored[n].Y, floored[n].Z + 0.05f);
        var unfloored = (Vec3[])floored.Clone();
        var drawn = FlatSkin();
        BodyRetarget.KeepLayerOrder(sets, covered, floored, null, layered, drawn, floor: true);
        BodyRetarget.KeepLayerOrder(sets, covered, unfloored, null, layered, drawn, floor: false);
        Assert.Equal(unfloored, floored);
    }

    [Fact]
    [Trait("Plan", "AC-bodice-nipple-clip-6")]
    public void The_floor_turns_no_triangle_over()
    {
        var (sets, layered, covered, delta) = LiftedInward();
        BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered, FlatSkin(), floor: true);
        Assert.Equal(0, BodyRetarget.FoldedTriangles(sets, delta));
    }

    [Fact]
    [Trait("Plan", "EDGE-bodice-nipple-clip-1")]
    public void A_point_already_inside_is_never_moved_further_in()
    {
        // The inner middle — a corner of the lifted face — already 2 mm inside the skin when the pass begins.
        var (sets, layered, covered, delta) = LiftedInward();
        delta[sets.NodeOf[Middle]] = new Vec3(0f, 0f, -0.0025f);
        float start = Standoff(sets, delta, Middle);
        Assert.True(start < 0f);

        BodyRetarget.KeepLayerOrder(sets, covered, delta, null, layered, FlatSkin(), floor: true);
        Assert.True(Standoff(sets, delta, Middle) >= start - 1e-5f,
                    $"moved from {start * 1000:F2} to {Standoff(sets, delta, Middle) * 1000:F2} mm off the skin");
    }

    [Fact]
    [Trait("Plan", "EDGE-bodice-nipple-clip-6")]
    [Trait("Plan", "AC-bodice-nipple-clip-8")]
    public void A_pull_within_room_is_unchanged_by_the_floor()
    {
        // A covered point brought through its cover and pulled back behind it, far off the skin: no lift, nothing to floor.
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered);
        var drawn = FlatSkin();

        var floored = ThroughTheTop(sets);
        BodyRetarget.KeepLayerOrder(sets, covered, floored, null, layered, drawn, floor: true);
        var unfloored = ThroughTheTop(sets);
        BodyRetarget.WithTuning(BodyRetarget.Tuned with { NoLayerFloor = true },
                                () => BodyRetarget.KeepLayerOrder(sets, covered, unfloored, null, layered, drawn, floor: true));
        Assert.Equal(unfloored, floored);
        Assert.True(floored[sets.NodeOf[Middle]].Z < 0.015f, "the covered point should still have gone back behind");
    }

    [Theory]
    [InlineData(8f, 0.009f)]      // a dome, the bra's middle carried to 15 mm standing ~6 mm off it
    [InlineData(-100f, 0.0115f)]  // a bowl rising round the bra's middle to its edges
    [Trait("Plan", "EDGE-bodice-nipple-clip-6")]
    public void A_pull_alone_over_curved_skin_is_unchanged_by_the_floor(float curve, float height)
    {
        // A bra brought through its top and pulled back behind it over a curved skin: the floor and its read where a
        // lifted point's move ends leave a pull alone as it was, however the skin curves under it.
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered);
        var drawn = Skin(curve, height);
        var floored = ThroughTheTop(sets);
        BodyRetarget.KeepLayerOrder(sets, covered, floored, null, layered, drawn, floor: true);
        var unfloored = ThroughTheTop(sets);
        BodyRetarget.WithTuning(BodyRetarget.Tuned with { NoLayerFloor = true },
                                () => BodyRetarget.KeepLayerOrder(sets, covered, unfloored, null, layered, drawn, floor: true));
        Assert.Equal(unfloored, floored);
        Assert.NotEqual(ThroughTheTop(sets), floored);   // it did pull
    }

    [Fact]
    [Trait("Plan", "EDGE-bodice-nipple-clip-3")]
    public void Without_a_drawn_skin_or_the_flag_the_floor_does_nothing()
    {
        // No skin to floor against (no push-out); the outfit order's lift-only call, even over skin; the flag off: as before.
        foreach (var (drawn, liftOnly, floor) in new (BodyRetarget.TargetBody? Drawn, bool LiftOnly, bool Floor)[]
                 { (null, false, true), (FlatSkin(), true, true), (FlatSkin(), false, false) })
        {
            var (sets, layered, covered, floored) = LiftedInward();
            BodyRetarget.KeepLayerOrder(sets, covered, floored, null, layered, drawn, liftOnly, floor);
            var (_, _, _, unfloored) = LiftedInward();
            BodyRetarget.WithTuning(BodyRetarget.Tuned with { NoLayerFloor = true },
                                    () => BodyRetarget.KeepLayerOrder(sets, covered, unfloored, null, layered, drawn, liftOnly, floor));
            Assert.Equal(unfloored, floored);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-bodice-nipple-clip-4")]
    [Trait("Plan", "AC-bodice-nipple-clip-9")]
    public void A_game_tops_refit_keeps_the_layer_order_as_it_was()
    {
        // A game top's refit clears its body (clearBody): Solve passes the floor off, and the inward lift is as before.
        Assert.False(BodyRetarget.LayerFloorFor(clearBody: true));
        Assert.True(BodyRetarget.LayerFloorFor(clearBody: false));
        var drawn = FlatSkin();
        var (sets, layered, covered, cleared) = LiftedInward();
        BodyRetarget.KeepLayerOrder(sets, covered, cleared, null, layered, drawn, floor: BodyRetarget.LayerFloorFor(clearBody: true));
        var (_, _, _, before) = LiftedInward();
        BodyRetarget.WithTuning(BodyRetarget.Tuned with { NoLayerFloor = true },
                                () => BodyRetarget.KeepLayerOrder(sets, covered, before, null, layered, drawn, floor: true));
        Assert.Equal(before, cleared);
        Assert.True(Enumerable.Range(0, Side * Side).Min(v => Off(drawn, sets, cleared, v)) < -0.001f,
                    "the inward lift should have run unfloored, or this proves nothing");
    }

    [Fact]
    [Trait("Plan", "AC-bodice-nipple-clip-8")]
    public void A_cover_lifted_outward_is_not_held_back_by_the_floor()
    {
        // A bra under a top, brought through it by the refit, its middle a hair off the skin: it cannot go back, so the
        // top is lifted off it — outward, which the floor never takes back.
        var garment = Layered("/mt_c0201e6143_top_a.mtrl", "/mt_c0201e6143_top_b.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered);
        var drawn = Skin(0f, 0.0149f);   // 0.1 mm under the bra's middle, carried to 15 mm

        var floored = ThroughTheTop(sets);
        BodyRetarget.KeepLayerOrder(sets, covered, floored, null, layered, drawn, floor: true);
        var unfloored = ThroughTheTop(sets);
        BodyRetarget.WithTuning(BodyRetarget.Tuned with { NoLayerFloor = true },
                                () => BodyRetarget.KeepLayerOrder(sets, covered, unfloored, null, layered, drawn, floor: true));
        Assert.Equal(unfloored, floored);
        // The top over the bra's middle was lifted: in front of the bra again.
        int topOverMiddle = sets.NodeOf[Side * Side + Middle];
        Assert.True(floored[topOverMiddle].Z + Gap > floored[sets.NodeOf[Middle]].Z,
                    "the top should have been lifted in front of the bra");
        Assert.True(floored[topOverMiddle].Z > 0.01f + 1e-5f, "the top should have been lifted");
    }

    [Fact]
    public void A_body_mods_alternatives_are_not_layers()
    {
        // Two pubic hair meshes on the same skin, one switched on at a time: keeping one behind the other would sink
        // whichever is drawn into the skin.
        var garment = Layered("/mt_c0201b0001_bibopube.mtrl", "/mt_c0201b0001_betterpube.mtrl");
        var sets = BodyRetarget.Sets.From(garment);
        var layered = BodyRetarget.GarmentCloth(garment, sets);
        Assert.Empty(BodyRetarget.CoveredPoints(sets, layered));

        var delta = ThroughTheTop(sets);
        var was = (Vec3[])delta.Clone();
        BodyRetarget.KeepLayerOrder(sets, BodyRetarget.CoveredPoints(sets, layered), delta, null, layered);
        Assert.Equal(was, delta);
    }
}
