using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.SkinTuckSheet;

namespace Proteus.Tests;

/// <summary>
/// The cut's rule for skin the author pressed in (see <see cref="BodyRetarget.PressedUnder"/>): a body vertex counts as
/// drawn when the author's laid skin within 12 mm lands inside one of its triangles, or on an edge two share, facing the
/// vertex's way — though further off it than the chord rule (<see cref="BodyRetarget.Covers"/>) allows. "Starlight"'s
/// Valentione Skirt presses the thighs in under its stocking band in coarse skin, and laid onto rounder bodies the cut took
/// the new skin out at the front of the thighs where the author's skin was whole.
/// </summary>
public partial class BodyRetargetTests
{
    // ── the predicate ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A laid skin patch: a 4 × 4 grid 25 mm apart (triangles 25 mm on their legs, chord 35 mm: the chord rule allows 7.1
    /// mm), flat at z = 0, its normals <paramref name="normal"/>. Its middle quad's triangles touch none of its edge.
    /// </summary>
    private static (BodySurface Drawn, BodyRetarget.SkinEdges Edges) LaidPatch(Vector3 normal)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                pos.AddRange([i * 0.025f, j * 0.025f, 0f]);
                nrm.AddRange([normal.X, normal.Y, normal.Z]);
            }
        var tris = new List<int>();
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                int a = j * 4 + i, b = a + 1, d = a + 4, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        var laid = Build(pos.ToArray(), nrm.ToArray(), tris.ToArray(), SkinMaterial);
        return (new BodySurface(laid, BodySurface.CellFor(0.025f)), BodyRetarget.SkinEdges.Of(laid));
    }

    /// <summary>Every laid corner standing 6 mm off the new body: the author's skin pressed in that far.</summary>
    private static float Off(int vertex) => 0.006f;

    /// <summary>A point over the middle of one of the patch's middle triangles, <paramref name="height"/> above it.</summary>
    private static Vector3 OverMiddle(float height) => new(0.0417f, 0.0333f, height);

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-2")]
    public void A_body_point_over_pressed_in_laid_skin_is_pressed_covered()
    {
        var (drawn, edges) = LaidPatch(Vector3.UnitZ);
        var p = OverMiddle(0.0075f);
        Assert.False(BodyRetarget.Covers(drawn, p, edges));   // 7.5 mm under a 35 mm chord: past the chord rule's 20%
        Assert.True(BodyRetarget.PressedUnder(drawn, p, Vector3.UnitZ, edges, Off));
    }

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-8")]
    public void Past_the_authors_edge_the_laid_skin_does_not_cover()
    {
        var (drawn, edges) = LaidPatch(Vector3.UnitZ);
        // Beyond the patch's edge (an edge one triangle uses), and beyond its corner.
        Assert.False(BodyRetarget.PressedUnder(drawn, new Vector3(0.0375f, -0.006f, 0.005f), Vector3.UnitZ, edges, Off));
        Assert.False(BodyRetarget.PressedUnder(drawn, new Vector3(-0.006f, -0.006f, 0.005f), Vector3.UnitZ, edges, Off));
        // Over a triangle on the author's edge: the rim of a cut, not a reshape inside the skin (Tre's crotch inside the
        // skirt's crotch hole landed on the inner thighs' rim triangles).
        Assert.False(BodyRetarget.PressedUnder(drawn, new Vector3(0.0167f, 0.0083f, 0.0075f), Vector3.UnitZ, edges, Off));
        Assert.True(BodyRetarget.PressedUnder(drawn, OverMiddle(0.0075f), Vector3.UnitZ, edges, Off));
    }

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-8")]
    public void Laid_skin_on_the_new_body_over_its_own_detail_does_not_cover()
    {
        // Every corner of the laid triangle on the new body: a chord over the body's own detail (Rue's genitals inside
        // the skirt's crotch cut), not skin the author pressed in.
        var (drawn, edges) = LaidPatch(Vector3.UnitZ);
        Assert.False(BodyRetarget.PressedUnder(drawn, OverMiddle(0.0075f), Vector3.UnitZ, edges, _ => 0f));
        Assert.True(BodyRetarget.PressedUnder(drawn, OverMiddle(0.0075f), Vector3.UnitZ, edges, v => v == 5 ? 0.006f : 0f));
        // Pressed, but not enough to explain the gap: 2 mm of press under 11 mm leaves 9, past the chord rule's 7.1.
        Assert.False(BodyRetarget.PressedUnder(drawn, OverMiddle(0.011f), Vector3.UnitZ, edges, _ => 0.002f));
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-9")]
    public void Laid_skin_facing_away_does_not_cover()
    {
        var p = OverMiddle(0.0075f);
        var (away, awayEdges) = LaidPatch(-Vector3.UnitZ);
        Assert.False(BodyRetarget.PressedUnder(away, p, Vector3.UnitZ, awayEdges, Off));   // the other thigh: cosine -1
        var tilted = Vector3.Normalize(new Vector3(MathF.Sin(70f * MathF.PI / 180f), 0f, MathF.Cos(70f * MathF.PI / 180f)));
        var (lean, leanEdges) = LaidPatch(tilted);
        Assert.False(BodyRetarget.PressedUnder(lean, p, Vector3.UnitZ, leanEdges, Off));   // 70°: cosine 0.34
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-10")]
    public void A_body_vertex_with_no_direction_gets_the_old_rule()
    {
        var (drawn, edges) = LaidPatch(Vector3.UnitZ);
        Assert.False(BodyRetarget.PressedUnder(drawn, OverMiddle(0.0075f), Vector3.Zero, edges, Off));

        // A vertex with no normal in the file is judged on its triangles' faces: a flat sheet with zero normals reads +Z.
        float[] pos = [0f, 0f, 0f, 0.01f, 0f, 0f, 0f, 0.01f, 0f];
        var bare = Build(pos, new float[9], [0, 1, 2], SkinMaterial);
        var normals = BodyRetarget.SkinVertexNormals(bare);
        Assert.All(normals, n => Assert.Equal(1f, n.Z, 5));
        var shaded = Build(pos, [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 1, 2], SkinMaterial);
        Assert.Equal(normals, BodyRetarget.SkinVertexNormals(shaded));
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-24")]
    public void A_laid_triangle_with_no_normals_gets_the_old_rule()
    {
        // The author's skin with no normal at the triangle's corners: no direction to judge the facing by, so the old rule.
        var (bare, bareEdges) = LaidPatch(Vector3.Zero);
        Assert.False(BodyRetarget.PressedUnder(bare, OverMiddle(0.0075f), Vector3.UnitZ, bareEdges, Off));
        var (shaded, shadedEdges) = LaidPatch(Vector3.UnitZ);
        Assert.True(BodyRetarget.PressedUnder(shaded, OverMiddle(0.0075f), Vector3.UnitZ, shadedEdges, Off));
    }

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-9")]
    public void A_body_point_behind_the_laid_skin_is_not_pressed()
    {
        // The new body behind the author's skin, facing its way: tucked in under it, not standing out through it — under
        // the skirt's crotch the new body's genitals sat 10 mm above skin facing down.
        var (drawn, edges) = LaidPatch(Vector3.UnitZ);
        Assert.False(BodyRetarget.PressedUnder(drawn, OverMiddle(-0.0075f), Vector3.UnitZ, edges, Off));
        Assert.True(BodyRetarget.PressedUnder(drawn, OverMiddle(0.0075f), Vector3.UnitZ, edges, Off));
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-11")]
    public void Laid_skin_past_twelve_millimetres_does_not_cover()
    {
        var (drawn, edges) = LaidPatch(Vector3.UnitZ);
        Assert.False(BodyRetarget.PressedUnder(drawn, OverMiddle(0.0125f), Vector3.UnitZ, edges, Off));
        Assert.True(BodyRetarget.PressedUnder(drawn, OverMiddle(0.0115f), Vector3.UnitZ, edges, Off));
    }

    // ── the cut ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A garment whose skin is a coarse grid over rows 4-13 (corners at rows 4, 7, 10 and 13, columns 0, 2, 4, 6, 8:
    /// triangles 20 × 30 mm, chord 36 mm) at z = 0, its middle corners (rows 7 and 10, columns 2-6) pressed
    /// <paramref name="press"/> in, with cloth 15 mm off over rows 0-3 and no skin there: the author's cut. The triangles
    /// between rows 7 and 10, columns 2-6, touch none of the skin's edge.
    /// </summary>
    private static byte[] PressedGarment(bool hole = false, float press = 0.004f)
    {
        int[] cols = [0, 2, 4, 6, 8];
        int[] rows = [4, 7, 10, 13];
        var pos = new List<Vector3>();
        foreach (int r in rows)
            foreach (int c in cols)
                pos.Add(new Vector3(X(c), Y(r), r is 7 or 10 && c is 2 or 4 or 6 ? -press : 0f));
        var tris = new List<int>();
        for (int j = 0; j + 1 < rows.Length; j++)
            for (int k = 0; k + 1 < cols.Length; k++)
            {
                if (hole && j == 1 && k is 1 or 2) continue;   // the author deleted the skin over rows 7-10, columns 2-6
                int a = j * cols.Length + k, b = a + 1, d = a + cols.Length, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        var skin = new SyntheticModel.SurfaceMesh(Skin, [.. pos], [.. pos.Select(_ => Vector3.UnitZ)],
                                                  [.. pos.Select(_ => Vector2.Zero)],
                                                  [.. pos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                  [(0u, [.. tris])]);
        var cpos = new List<Vector3>();
        for (int r = 0; r <= 3; r++)
            for (int c = 0; c < Columns; c++) cpos.Add(new Vector3(X(c), Y(r), 0.015f));
        var ctris = new List<int>();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c + 1 < Columns; c++)
            {
                int a = r * Columns + c, b = a + 1, d = a + Columns, e = d + 1;
                ctris.AddRange([a, b, e, a, e, d]);
            }
        var cloth = new SyntheticModel.SurfaceMesh(Cloth, [.. cpos], [.. cpos.Select(_ => Vector3.UnitZ)],
                                                   [.. cpos.Select(_ => Vector2.Zero)],
                                                   [.. cpos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])],
                                                   [(0u, [.. ctris])]);
        return SyntheticModel.BuildSurface([], skin, cloth);
    }

    /// <summary>The body: the grid with rows 8-9, columns 3-5 standing 5 mm out — 9 mm over the garment's skin pressed
    /// 4 mm in, past its chord's 7.2 mm, as the rounder thighs stood through Starlight's pressed crease.</summary>
    private static byte[] PressedBody() => Body(z: (r, c) => r is 8 or 9 && c is >= 3 and <= 5 ? 0.005f : 0f);

    /// <summary>The same bump 9 mm out, under the garment's skin not pressed at all (on the body at every corner): the
    /// body's own detail, which the author never drew over.</summary>
    private static byte[] DetailBody() => Body(z: (r, c) => r is 8 or 9 && c is >= 3 and <= 5 ? 0.009f : 0f);

    private static (byte[] Model, BodyRetarget.SwapReport Report) SwapPressed(byte[] garment, BodyRetarget.Tuning? tuning = null,
                                                                             byte[]? body = null)
    {
        BodyRetarget.SwapReport report = default;
        var model = BodyRetarget.WithTuning(tuning ?? new BodyRetarget.Tuning(), () => BodyRetarget.Rebuild(
            garment, [SkinTuckSheet.Resized(body ?? PressedBody())], swapSkin: true, weights: null, out report, cutHidden: true));
        Assert.NotNull(model);
        return (model!, report);
    }

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-2")]
    public void The_cut_keeps_and_draws_body_skin_over_a_pressed_crease()
    {
        var now = SwapPressed(PressedGarment());
        var before = SwapPressed(PressedGarment(), new BodyRetarget.Tuning(NoPressedSkinCut: true));
        Assert.True(before.Report.Cut > 0, "the author's cut took nothing: the test checks nothing");
        Assert.False(DrawsDip(before.Model));   // the old cut took the pressed skin out
        Assert.True(DrawsDip(now.Model));       // drawn: its corners are in the written draw set
        Assert.True(now.Report.Cut < before.Report.Cut);
        // The author's own cut (rows 0-3) still stands.
        Assert.DoesNotContain(DrawnRows(now.Model).Keys, r => r < 3);
    }

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-8")]
    public void A_hole_the_author_cut_over_the_crease_stays_cut()
    {
        var now = SwapPressed(PressedGarment(hole: true));
        Assert.False(DrawsDip(now.Model));
    }

    [Fact]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-8")]
    public void The_new_bodys_own_detail_under_unpressed_skin_stays_cut()
    {
        // The skirt's crotch on Rue, YAB and Neolithe: the new body's genitals up through coarse laid skin that sits on the
        // body at every corner. The author cut them; so does the cut.
        var now = SwapPressed(PressedGarment(press: 0f), body: DetailBody());
        var before = SwapPressed(PressedGarment(press: 0f), new BodyRetarget.Tuning(NoPressedSkinCut: true), DetailBody());
        Assert.False(DrawsDip(before.Model));
        Assert.False(DrawsDip(now.Model));
        Assert.Equal(before.Model, now.Model);
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-13")]
    public void No_pressed_skin_cut_restores_the_old_cut()
    {
        // With the knob the cut is the chord rule's alone: the same model as a garment with nothing pressed gives both.
        var flat = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoPressedSkinCut: true), () => BodyRetarget.Rebuild(
            PressedGarment(), [SkinTuckSheet.Resized(Body())], swapSkin: true, weights: null, out _, cutHidden: true));
        var plain = BodyRetarget.Rebuild(PressedGarment(), [SkinTuckSheet.Resized(Body())], swapSkin: true, weights: null, out _, cutHidden: true);
        Assert.Equal(flat, plain);
    }

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-12")]
    public void Smallclothes_are_cut_as_before()
    {
        // The pressed rule drives the skin's cut only (decision 4: Starlight's Neolithe sizes lost no smallclothes to it):
        // a body's smallclothes under the garment are cut exactly as with the knob.
        var worn = SmallclothesBody();
        var now = BodyRetarget.Rebuild(PressedGarment(), [SkinTuckSheet.Resized(worn)], swapSkin: true, weights: null, out _, cutHidden: true);
        var before = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoPressedSkinCut: true), () => BodyRetarget.Rebuild(
            PressedGarment(), [SkinTuckSheet.Resized(worn)], swapSkin: true, weights: null, out _, cutHidden: true));
        Assert.NotNull(now);
        Assert.Equal(SmallclothesTriangles(before!), SmallclothesTriangles(now!));
    }

    /// <summary>The flat body with a smallclothes sheet 1 mm over its rows 9-12.</summary>
    private static byte[] SmallclothesBody()
    {
        var spos = new List<Vector3>();
        for (int r = 9; r <= 12; r++)
            for (int c = 0; c < Columns; c++) spos.Add(new Vector3(X(c), Y(r), 0.001f));
        var stris = new List<int>();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c + 1 < Columns; c++)
            {
                int a = r * Columns + c, b = a + 1, d = a + Columns, e = d + 1;
                stris.AddRange([a, b, e, a, e, d]);
            }
        var body = ModelPartReader.Read(Body())!;
        var bpos = Enumerable.Range(0, body.Positions.Length / 3)
                             .Select(v => new Vector3(body.Positions[v * 3], body.Positions[v * 3 + 1], body.Positions[v * 3 + 2])).ToArray();
        var btris = body.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).ToArray();
        var skin = new SyntheticModel.SurfaceMesh(Skin, bpos, [.. bpos.Select(_ => Vector3.UnitZ)], [.. bpos.Select(_ => Vector2.Zero)],
                                                  [.. bpos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])], [(0u, btris)]);
        var under = new SyntheticModel.SurfaceMesh(Smallclothes, [.. spos], [.. spos.Select(_ => Vector3.UnitZ)],
                                                   [.. spos.Select(_ => Vector2.Zero)],
                                                   [.. spos.Select(_ => ((string, float)[]?)[("j_sebo_c", 1f)])], [(0u, [.. stris])]);
        return SyntheticModel.BuildSurface([], skin, under);
    }

    private static int SmallclothesTriangles(byte[] model)
        => ModelPartReader.Read(model)!.Parts.Where(p => p.Island < 0 && p.Material.EndsWith(Smallclothes, StringComparison.Ordinal))
                                       .Sum(p => p.Triangles.Length / 3);

    [Fact]
    [Trait("Plan", "EDGE-starlight-bottoms-27")]
    public void Tuck_and_open_skin_are_unchanged_without_pressed_skin()
    {
        // Garments with nothing pressed: the reaching dress through the open-skin pass, and a tucked top, give the same
        // model with and without the knob — and their reported counts agree.
        var dress = ReachingDress();
        BodyRetarget.SwapReport r1 = default, r2 = default;
        var pairs = new[] { SkinTuckSheet.Resized(dress.Chest, "_top"), SkinTuckSheet.Resized(dress.Legs, "_dwn") };
        var a = BodyRetarget.Rebuild(dress.Garment, pairs, swapSkin: true, weights: null, out r1, cutHidden: false, openSkin: true);
        var b = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoPressedSkinCut: true), () => BodyRetarget.Rebuild(
            dress.Garment, pairs, swapSkin: true, weights: null, out r2, cutHidden: false, openSkin: true));
        Assert.True(r1.Spanned > 0, "the open-skin pass drew nothing: the test checks nothing");
        Assert.Equal(b, a);
        Assert.Equal(r2.Spanned, r1.Spanned);

        // With a pressed strip: the tuck is told what the cut drew (pressed corners included), so every triangle it
        // reports as added is one more drawn — none the cut kept is counted again.
        BodyRetarget.SwapReport pr = default;
        var pressedTuck = BodyRetarget.Rebuild(PressedGarment(), [SkinTuckSheet.Resized(PressedBody())], swapSkin: true,
                                               weights: null, out pr, cutHidden: true, tuckSlot: "_top");
        Assert.NotNull(pressedTuck);
        Assert.True(DrawsDip(pressedTuck!), "the pressed strip is not drawn: the test checks nothing");
        int drawnTris = ModelPartReader.Read(pressedTuck!)!.Parts
            .Where(q => q.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(q.Material)).Sum(q => q.Triangles.Length / 3);
        Assert.Equal(drawnTris, pr.Added);

        var top = LooseTop();
        BodyRetarget.SwapReport t1 = default, t2 = default;
        var tucked = BodyRetarget.Rebuild(top, [SkinTuckSheet.Resized(Body())], swapSkin: true, weights: null, out t1, cutHidden: true, tuckSlot: "_top");
        var tuckedOld = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoPressedSkinCut: true), () => BodyRetarget.Rebuild(
            top, [SkinTuckSheet.Resized(Body())], swapSkin: true, weights: null, out t2, cutHidden: true, tuckSlot: "_top"));
        Assert.Equal(tuckedOld, tucked);
        Assert.Equal(t2.Tucked, t1.Tucked);
    }
}
