using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SkinTuckSheet;

namespace Proteus.Tests;

/// <summary>
/// The size set's seen-slit pass (<see cref="BodyRetarget.GrowSeenSlits"/>): one ring of the new body's skin left out in
/// the open just past the author's edge, drawn after all. Equinox's bikini top on Rosaline's Coconut: the ring's edge
/// corners lay 4.2-6.0 mm from the laid skin (past the cut's 4 mm reach), its outer corners 6.5-8.2 mm off, and the ring
/// beyond lay under the cup's rim.
/// <para/>
/// The sheet: <see cref="SkinTuckSheet.Body"/>'s flat grid, 10 mm apart. The top's own skin lies on rows 11 and up, then
/// lifts 5 mm off the body over row 10 and reaches on to 5 mm short of it — so row 10 is 4.5-5 mm from it (cut) and row
/// 9 7.1 mm (inside <see cref="BodyRetarget.SlitBand"/>), row 8 15.8 mm. The cup: cloth 2 mm over rows 0-8 and on to
/// 3 mm short of row 9 — row 8 crowded, row 9 open beside it.
/// </summary>
public class SeenSlitsTests
{
    private const float Lift = 0.005f;

    /// <summary>A sheet of rows at the given heights (y) and stand-offs (z), every column.</summary>
    private static SyntheticModel.SurfaceMesh Strip(string material, float[] ys, float[] zs, bool facingOut = true)
    {
        var pos = new List<Vector3>();
        var uv = new List<Vector2>();
        var w = new List<(string, float)[]?>();
        for (int r = 0; r < ys.Length; r++)
            for (int c = 0; c < Columns; c++)
            {
                pos.Add(new Vector3(X(c), ys[r], zs[r]));
                uv.Add(new Vector2((float)c / (Columns - 1), (float)r / Math.Max(1, ys.Length - 1)));
                w.Add([("j_sebo_c", 1f)]);
            }
        var tris = new List<int>();
        for (int r = 0; r + 1 < ys.Length; r++)
            for (int c = 0; c + 1 < Columns; c++)
            {
                int a = r * Columns + c, b = a + 1, d = a + Columns, e = d + 1;
                if (facingOut) tris.AddRange([a, b, e, a, e, d]);
                else tris.AddRange([a, e, b, a, d, e]);
            }
        return new SyntheticModel.SurfaceMesh(material, [.. pos], [.. pos.Select(_ => facingOut ? Vector3.UnitZ : -Vector3.UnitZ)],
                                              [.. uv], [.. w], [(0u, [.. tris])]);
    }

    /// <summary>The slit's top. <paramref name="cupZ"/>: the cup's stand-off; <paramref name="rimTo"/>: where its rim
    /// ends below row 9; <paramref name="reach"/>: how far below row 10 the author's skin reaches.</summary>
    private static byte[] SlitTop(float cupZ = 0.002f, float rimTo = 0.003f, float reach = 0.005f,
                                  (float From, float To, float Z, string? Attr)? over = null)
    {
        var skinY = new List<float> { Y(10) - reach, Y(10) };
        var skinZ = new List<float> { Lift, Lift };
        for (int r = 11; r < Rows; r++) { skinY.Add(Y(r)); skinZ.Add(0f); }
        var cupY = Enumerable.Range(0, 9).Select(Y).Append(Y(9) - rimTo).ToArray();
        var meshes = new List<SyntheticModel.SurfaceMesh>
        {
            Strip(Skin, [.. skinY], [.. skinZ]),
            Strip(Cloth, cupY, [.. cupY.Select(_ => cupZ)]),
        };
        var attrs = new List<string>();
        if (over is { } o)
        {
            var sheet = Strip(Cloth, [o.From, o.To], [o.Z, o.Z]);
            if (o.Attr != null) { attrs.Add(o.Attr); sheet = sheet with { Submeshes = [(1u, sheet.Submeshes[0].Triangles)] }; }
            meshes.Add(sheet);
        }
        return SyntheticModel.BuildSurface(attrs, [.. meshes]);
    }

    private static byte[]? Rebuilt(byte[] top, byte[] body, string? slitSlot = "_top", string? tuckSlot = null,
                                   bool carryPiercings = false, IReadOnlySet<string>? hidden = null)
        => BodyRetarget.Rebuild(top, [Resized(body, "_top", hidden)], swapSkin: true, weights: null, out _, cutHidden: true,
                                tuckSlot: tuckSlot, carryPiercings: carryPiercings, slitSlot: slitSlot);

    private static int Slits(byte[] top, byte[] body, string? slitSlot = "_top", IReadOnlySet<string>? hidden = null)
    {
        BodyRetarget.Rebuild(top, [Resized(body, "_top", hidden)], swapSkin: true, weights: null, out var report,
                             cutHidden: true, slitSlot: slitSlot);
        return report.Slits;
    }

    private static int Row9(byte[] model) => DrawnRows(model).GetValueOrDefault(9);

    /// <summary>The rebuilt model is valid, its cloth where it was, and it only gains skin over the one without slits.</summary>
    private static void OnlySkinAdded(byte[] with, byte[] without)
    {
        var a = ModelPartReader.Read(with)!;
        var b = ModelPartReader.Read(without)!;
        static IEnumerable<Vector3> ClothOf(ModelParts m)
            => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                      .SelectMany(p => p.Triangles).Select(v => new Vector3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]));
        Assert.Equal(ClothOf(b), ClothOf(a));
        foreach (var part in a.Parts)
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                Vector3 P(int v) => new(a.Positions[v * 3], a.Positions[v * 3 + 1], a.Positions[v * 3 + 2]);
                Assert.True(Vector3.Cross(P(part.Triangles[t + 1]) - P(part.Triangles[t]), P(part.Triangles[t + 2]) - P(part.Triangles[t]))
                                   .LengthSquared() > 1e-14f, "a degenerate triangle");
            }
        var rowsA = DrawnRows(with);
        foreach (var (row, n) in DrawnRows(without)) Assert.True(rowsA.GetValueOrDefault(row) >= n, $"row {row} lost skin");
    }

    [Fact]
    [Trait("Plan", "AC-equinox-legs-slits-4")]
    [Trait("Plan", "EDGE-equinox-legs-slits-13")]
    public void An_open_ring_just_past_the_authors_edge_is_drawn()
    {
        var body = Body();
        var top = SlitTop();

        var with = Rebuilt(top, body)!;
        var without = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeenSlits: true), () => Rebuilt(top, body))!;
        var tucked = Rebuilt(top, body, slitSlot: null, tuckSlot: "_top");

        Assert.Equal(0, Row9(without));
        Assert.Equal(2 * (Columns - 1), Row9(with));                 // the ring between rows 9 and 10, both triangles per cell
        Assert.Equal(0, DrawnRows(with).GetValueOrDefault(8));         // and no further: row 8 lies under the cup
        Assert.True(Slits(top, body) > 0);
        Assert.Equal(0, Row9(tucked!));                                 // the tuck's own rule does not draw it
        OnlySkinAdded(with, without);
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-13")]
    public void A_ring_under_cloth_stays_cut()
    {
        // Cloth 40 mm over the ring, inside TuckOver: hidden, not a slit.
        var top = SlitTop(over: (Y(8) + 0.002f, Y(10) + 0.002f, 0.04f, null));

        Assert.Equal(0, Row9(Rebuilt(top, Body())!));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-10")]
    public void Open_skin_crowded_by_cloth_stays_cut()
    {
        // The rim reaches past row 9, 2 mm over it.
        Assert.Equal(0, Row9(Rebuilt(SlitTop(rimTo: -0.003f), Body())!));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-11")]
    public void Open_skin_far_from_this_garments_cloth_stays_cut()
    {
        // The cup 30 mm off the body: no cloth within SlitClothNear of row 9 (nor crowding row 8).
        Assert.Equal(0, Row9(Rebuilt(SlitTop(cupZ: 0.03f), Body())!));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-12")]
    public void Corners_past_the_slit_band_stay_cut()
    {
        // The author's skin stops at row 10 itself: row 9 is 11.2 mm from it, past SlitBand.
        Assert.Equal(0, Row9(Rebuilt(SlitTop(reach: 0f), Body())!));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-14")]
    public void Arm_skin_and_unread_weights_are_never_drawn()
    {
        Assert.Equal(0, Row9(Rebuilt(SlitTop(), Body(arm: r => r is 9 or 10))!));
        Assert.Equal(0, Slits(SlitTop(), Body(arm: r => r is 9 or 10)));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-15")]
    public void A_hidden_body_part_is_never_drawn()
    {
        var hidden = new HashSet<string> { "atr_tv_b" };
        var body = Body(hiddenBand: (8, 10));

        var model = Rebuilt(SlitTop(), body, hidden: hidden);

        // Nothing of the hidden band is drawn: no model to rebuild at all, or one with no skin in its rows.
        Assert.True(model == null || (Row9(model) == 0 && DrawnRows(model).GetValueOrDefault(8) == 0));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-20")]
    public void Variant_cloth_over_the_ring_keeps_it_cut()
    {
        // A variant-tagged sheet 1 mm over the ring: whichever variant the wearer picks may draw it, so it crowds.
        var crowded = SlitTop(over: (Y(8) + 0.002f, Y(10) + 0.002f, 0.001f, "atr_tv_a"));
        // The cup far off, and the only cloth near the ring a variant's: not "near".
        var onlyVariantNear = SlitTop(cupZ: 0.03f, over: (Y(9) - 0.012f, Y(9) - 0.004f, 0.002f, "atr_tv_a"));

        Assert.Equal(0, Row9(Rebuilt(crowded, Body())!));
        Assert.Equal(0, Row9(Rebuilt(onlyVariantNear, Body())!));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-21")]
    public void No_piercing_lands_in_a_slit()
    {
        var body = Body(piercingAt: (9.5f, 0f));

        var model = Rebuilt(SlitTop(), body, carryPiercings: true)!;

        Assert.True(Row9(model) > 0);
        Assert.DoesNotContain(ModelPartReader.Read(model)!.Parts, p => p.Material.Contains("piercing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-16")]
    public void The_slit_rule_runs_only_on_its_slot_and_never_with_the_tuck()
    {
        var body = Body();
        var top = SlitTop();
        var plain = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeenSlits: true), () => Rebuilt(top, body))!;

        Assert.Equal(Sha(plain), Sha(Rebuilt(top, body, slitSlot: null)!));
        Assert.Null(RefitCore.SlitSlot(true, "_dwn"));
        Assert.Null(RefitCore.SlitSlot(false, "_top"));
        Assert.Equal("_top", RefitCore.SlitSlot(true, "_top"));
        Assert.Equal(Sha(Rebuilt(top, body, slitSlot: null, tuckSlot: "_top")!),
                     Sha(Rebuilt(top, body, slitSlot: "_top", tuckSlot: "_top")!));
    }

    [Fact]
    [Trait("Plan", "EDGE-equinox-legs-slits-17")]
    [Trait("Plan", "EDGE-equinox-legs-slits-18")]
    public void Seen_slits_add_skin_only()
    {
        var body = Body();
        var top = SlitTop();
        var with = Rebuilt(top, body)!;
        var without = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeenSlits: true), () => Rebuilt(top, body))!;

        Assert.NotEqual(Sha(with), Sha(without));
        // Only skin added, and the cloth's every vertex where it was — so no cloth face turned over either.
        OnlySkinAdded(with, without);
    }
}
