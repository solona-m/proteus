using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A body in a mirrored layout (gen2) refitted onto one in a doubled layout (bibo): judged by how much of its skin's
/// SURFACE lands, so a dense patch its target has no place for — Tight&amp;Firm Gen2's NSFW crotch, 60% of its legs'
/// vertices on a small share of their surface — follows the skin around it instead of sinking the pair.
/// <para/>
/// The bodies are strips: a coarse "leg" whose gen2 uvs convert onto the target's, and a dense "patch" on top of it, joined
/// at the same positions across a uv cut, whose uvs land where the target has no skin.
/// </summary>
public class Gen2LayoutRefitTests
{
    private const string Gen2 = "/mt_c0201b0001_a.mtrl";
    private const string Bibo = "/mt_c0201b0001_bibo.mtrl";

    private static readonly UVRemapService Remap = new(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");

    /// <summary>The leg's gen2 uvs: u 0.1..0.5, v 0.1..0.95. On the bibo sheet, +X: u 0.55..0.75.</summary>
    private static readonly (float U0, float U1, float V0, float V1) LegUv = (0.1f, 0.5f, 0.1f, 0.95f);

    /// <summary>The patch's gen2 uvs, which convert to u 0.85..0.975 — where the target has no skin.</summary>
    private static readonly (float U0, float U1, float V0, float V1) PatchUv = (0.7f, 0.95f, 0f, 0.05f);

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-2")]
    [Trait("Plan", "AC-gen2-legs-refit-4")]
    public void A_gen2_body_whose_dense_patch_has_no_place_on_bibo_is_paired_by_area()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        Assert.True(atlas.Measured.VertexCoverage < 0.8f, $"by count {atlas.Measured.VertexCoverage:P1}");
        Assert.True(atlas.Measured.AreaCoverage >= 0.8f, $"by area {atlas.Measured.AreaCoverage:P1}");
        Assert.Equal(source.Patch.Count, atlas.Measured.Carried);
        Assert.Contains("no place on the target's layout", atlas.Describe());
    }

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-4")]
    public void The_patch_follows_the_skin_around_it()
    {
        // Sheared and lifted unevenly, so a carry that ignored where the patch is joined could not pass: the leg's top
        // row moves by Move(y = 1), and so must every point of the patch above it.
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var top = Move(new Vector3(0.1f, 1f, 0f)) - new Vector3(0.1f, 1f, 0f);
        foreach (int v in source.Skin)
            Assert.True(built!.Field[v].HasValue, $"vertex {v} has no displacement");
        foreach (int v in source.Patch)
            Assert.True(Vector3.Distance(built!.Field[v]!.Value, top) < 1e-5f,
                        $"patch vertex {v} moved {built.Field[v]}, the leg's top {top}");
        Assert.Equal(0, Folded(source, built!));
    }

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-5")]
    [Trait("Plan", "EDGE-gen2-legs-refit-3")]
    public void A_gen2_body_in_an_unrelated_layout_is_still_refused()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        for (int i = 0; i < source.Uv.Length; i += 2) source.Uv[i] += 0.37f;
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                 out string refusal, Remap));
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-2")]
    public void A_region_with_no_place_over_a_fifth_of_the_skin_is_refused()
    {
        // A patch 0.4 tall on a leg 1.0 tall: 29% of the surface has no place.
        var source = Strip(Gen2, side: 1, patchTop: 1.4f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(UvAtlasCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                    out string refusal, out var measured, Gen2ToBibo(), null,
                                                    byArea: true));
        Assert.InRange(measured.AreaCoverage, 0.6f, 0.8f);
        Assert.Contains(string.Format("{0:P0}", measured.AreaCoverage), refusal);
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "AC-gen2-legs-refit-6")]
    [Trait("Plan", "EDGE-gen2-legs-refit-13")]
    public void A_pair_today_s_rule_accepts_is_matched_as_before()
    {
        // No patch; three of the leg's points drawn where the target has nothing. Enough lands by count, so the three are
        // left for the refit's own fill, exactly as before, and the description says nothing new.
        var source = Strip(Gen2, side: 1, patchTop: null);
        int[] stray = [source.Skin[1], source.Skin[2], source.Skin[3]];
        foreach (int v in stray) source.Uv[v * 2] = 0.9f;
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var atlas = Assert.IsType<UvAtlasCorrespondence>(built);
        foreach (int v in stray) Assert.Null(atlas.Field[v]);
        Assert.Equal(0, atlas.Measured.Carried);
        int placeable = source.Skin.Count;
        Assert.Equal($"legs: matched by texture coordinate, {placeable - 3:N0} of {placeable:N0} skin vertices " +
                     $"({(float)(placeable - 3) / placeable:P1})", atlas.Describe());
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-1")]
    public void The_same_patch_on_one_layout_is_still_judged_by_vertex()
    {
        // Both bodies bibo: no conversion, so no judging by surface — the dense patch sinks the pair as it always did.
        var source = Strip(Bibo, side: 1, patchTop: 1.02f, converted: true);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                 out string refusal, Remap));
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-4")]
    public void A_patch_on_the_mirrored_side_follows_its_own_side()
    {
        // Both sides share one gen2 sheet; the target moves them apart. Each patch takes its own side's move.
        var source = Join(Strip(Gen2, side: 1, patchTop: 1.02f), Strip(Gen2, side: -1, patchTop: 1.02f));
        var lift = new Vector3(0f, 0.03f, 0f);
        var target = Join(Leg(Bibo, side: 1, p => p + new Vector3(0.01f, 0f, 0f)),
                          Leg(Bibo, side: -1, p => p + lift));

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        foreach (int v in source.Patch)
        {
            var want = At(source.Model, v).X > 0 ? new Vector3(0.01f, 0f, 0f) : lift;
            Assert.True(Vector3.Distance(built!.Field[v]!.Value, want) < 1e-5f,
                        $"patch vertex {v} at {At(source.Model, v)} moved {built.Field[v]}, its side {want}");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-5")]
    public void An_unplaced_island_joined_to_nothing_takes_its_nearest_placed_point()
    {
        // The patch floats 0.1 above the leg, joined to it by nothing.
        var source = Strip(Gen2, side: 1, patchTop: 1.12f, patchGap: 0.1f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap), refusal);
        var placed = source.Skin.Except(source.Patch).ToList();
        foreach (int v in source.Patch)
        {
            int nearest = placed.MinBy(m => Vector3.DistanceSquared(At(source.Model, m), At(source.Model, v)));
            Assert.Equal(built!.Field[nearest], built.Field[v]);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-6")]
    public void Off_sheet_islands_and_unplaced_misses_are_carried_together()
    {
        // A third island, drawn off the sheet (u > 0.96 here), beside the patch: kept out of both coverages, and carried.
        var source = Strip(Gen2, side: 1, patchTop: 1.02f, offSheetTop: -0.02f);
        var target = Leg(Bibo, side: 1, Move);
        UVRemapService.UvConversion onSheet = (u, v, side) => u > 0.96f ? null : (u, v);

        Assert.True(UvAtlasCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs",
                                                   out var built, out string refusal, out var measured, Gen2ToBibo(),
                                                   onSheet, byArea: true), refusal);
        Assert.Equal(source.OffSheet.Count, measured.OffSheet);
        Assert.Equal(source.Skin.Count - source.OffSheet.Count, measured.Placeable);
        foreach (int v in source.Patch.Concat(source.OffSheet))
            Assert.True(built!.Field[v].HasValue, $"vertex {v} was not carried");
        Assert.Contains("drawn off the texture layout", built!.Describe());
        Assert.Contains("no place on the target's layout", built.Describe());

        // Mostly off its sheet, it is still refused.
        var mostly = Strip(Gen2, side: 1, patchTop: 1.02f, offSheetTop: -0.3f, offSheetCells: 200);
        Assert.False(UvAtlasCorrespondence.TryBuild(mostly.Model, mostly.Uv, target.Model, target.Uv, "legs", out _,
                                                    out refusal, out _, Gen2ToBibo(), onSheet, byArea: true));
        Assert.Contains("outside its texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-7")]
    public void A_man_s_vanilla_body_onto_TBSE_carries_its_patch_too()
    {
        var source = Strip("/mt_c0101b0001_a.mtrl", side: 1, patchTop: 1.02f);
        var target = Leg("/mt_c0101b0001_b.mtrl", side: 1, Move);

        Assert.True(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out var built,
                                                out string refusal, Remap, male: true), refusal);
        Assert.Equal(source.Patch.Count, Assert.IsType<UvAtlasCorrespondence>(built).Measured.Carried);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-8")]
    public void Without_the_uv_service_a_gen2_pair_is_refused_as_before()
    {
        var source = Strip(Gen2, side: 1, patchTop: 1.02f);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source.Model, source.Uv, target.Model, target.Uv, "legs", out _,
                                                 out string refusal, uvRemap: null));
        Assert.Contains("no map", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-gen2-legs-refit-9")]
    public void A_body_whose_skin_has_no_area_is_refused()
    {
        // Three points on a line, drawn where the target has nothing: nothing lands, and there is no surface to weigh.
        float[] pos = [0.05f, 0.5f, 0f, 0.1f, 0.5f, 0f, 0.15f, 0.5f, 0f];
        float[] uv = [0.9f, 0.5f, 0.92f, 0.5f, 0.94f, 0.5f];
        var source = Model(pos, [0, 1, 2], Gen2);
        var target = Leg(Bibo, side: 1, Move);

        Assert.False(BodyCorrespondence.TryBuild(source, uv, target.Model, target.Uv, "legs", out _, out string refusal,
                                                 Remap));
        Assert.Contains("texture layout", refusal);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How the target body differs from the source: sheared by height and lifted.</summary>
    private static Vector3 Move(Vector3 p) => p + new Vector3(0.02f * p.Y, 0.01f + 0.005f * p.Y * p.Y, 0f);

    private static UVRemapService.UvConversion Gen2ToBibo()
        => (u, v, side) => (side < 0 ? 0.5f - u * 0.5f : 0.5f + u * 0.5f, v);

    private sealed record Body(ModelParts Model, float[] Uv, List<int> Skin, List<int> Patch, List<int> OffSheet);

    /// <summary>
    /// A leg 1.0 tall on one side of the body, its gen2 uvs converting onto the target's, with a dense patch on top
    /// (to <paramref name="patchTop"/>; none when null) whose uvs have no place there.
    /// </summary>
    /// <param name="converted">The uvs are already in the bibo layout, for a source in that layout.</param>
    /// <param name="patchGap">How far above the leg the patch starts; 0 joins it to the leg's top row by position.</param>
    /// <param name="offSheetTop">Adds an off-sheet island below the leg, down to this height; null for none.</param>
    private static Body Strip(string material, int side, float? patchTop, bool converted = false, float patchGap = 0f,
                              float? offSheetTop = null, int offSheetCells = 4)
    {
        var b = new Builder(side);
        b.Grid(0f, 1f, 4, 10, LegUv, converted);
        var patch = patchTop is { } t ? b.Grid(1f + patchGap, t, 30, 10, PatchUv, converted) : [];
        var off = offSheetTop is { } o ? b.Grid(o, 0f, offSheetCells, 2, (0.97f, 0.99f, 0f, 0.05f), converted: true,
                                                rawU: true) : [];
        var model = Model(b.Positions.ToArray(), b.Triangles.ToArray(), material);
        return new Body(model, b.Uvs.ToArray(), Enumerable.Range(0, b.Positions.Count / 3).ToList(), patch, off);
    }

    /// <summary>The target: the leg alone, moved by <paramref name="move"/>, its uvs on the doubled sheet.</summary>
    private static Body Leg(string material, int side, Func<Vector3, Vector3> move)
    {
        var b = new Builder(side, move);
        b.Grid(0f, 1f, 4, 10, LegUv, converted: true);
        var model = Model(b.Positions.ToArray(), b.Triangles.ToArray(), material);
        return new Body(model, b.Uvs.ToArray(), Enumerable.Range(0, b.Positions.Count / 3).ToList(), [], []);
    }

    private static Body Join(Body a, Body b)
    {
        int offset = a.Model.Positions.Length / 3;
        var model = Model(a.Model.Positions.Concat(b.Model.Positions).ToArray(),
                          a.Model.Parts[0].Triangles.Concat(b.Model.Parts[0].Triangles.Select(i => i + offset)).ToArray(),
                          a.Model.Parts[0].Material);
        return new Body(model, a.Uv.Concat(b.Uv).ToArray(), a.Skin.Concat(b.Skin.Select(i => i + offset)).ToList(),
                        a.Patch.Concat(b.Patch.Select(i => i + offset)).ToList(),
                        a.OffSheet.Concat(b.OffSheet.Select(i => i + offset)).ToList());
    }

    private sealed class Builder(int side, Func<Vector3, Vector3>? move = null)
    {
        public readonly List<float> Positions = [];
        public readonly List<float> Uvs = [];
        public readonly List<int> Triangles = [];

        /// <summary>A grid of quads across x 0.02..0.2 (mirrored for -X) and the given heights; its vertex indices.</summary>
        public List<int> Grid(float y0, float y1, int nx, int ny, (float U0, float U1, float V0, float V1) uv,
                              bool converted, bool rawU = false)
        {
            int first = Positions.Count / 3;
            var made = new List<int>();
            for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                var p = new Vector3(side * (0.02f + 0.18f * i / nx), y0 + (y1 - y0) * j / ny, 0f);
                if (move != null) p = move(p);
                Positions.AddRange([p.X, p.Y, p.Z]);
                float u = uv.U0 + (uv.U1 - uv.U0) * i / nx, v = uv.V0 + (uv.V1 - uv.V0) * j / ny;
                // On the doubled sheet the +X side is the right half, the -X side its mirror.
                if (converted && !rawU) u = side < 0 ? 0.5f - u * 0.5f : 0.5f + u * 0.5f;
                Uvs.AddRange([u, v]);
                made.Add(Positions.Count / 3 - 1);
            }
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = first + j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                if (side > 0) Triangles.AddRange([a, b, d, a, d, c]);
                else Triangles.AddRange([a, d, b, a, c, d]);
            }
            return made;
        }
    }

    private static ModelParts Model(float[] pos, int[] tris, string material)
    {
        int vc = pos.Length / 3;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < vc; v++)
        {
            min = Vector3.Min(min, At(pos, v));
            max = Vector3.Max(max, At(pos, v));
        }
        var nrm = new float[pos.Length];
        for (int v = 0; v < vc; v++) nrm[v * 3 + 2] = 1f;

        var part = new ModelPart
        {
            Mesh = 0,
            Submesh = 0,
            Island = -1,
            Label = "0.0",
            Material = material,
            Triangles = tris,
            Ordinals = Enumerable.Range(0, tris.Length / 3).ToArray(),
            AttributeMask = 0,
            Min = min,
            Max = max,
            Toggleable = true,
        };
        return new ModelParts
        {
            Positions = pos,
            Normals = nrm,
            MeshSpans = [new MeshSpan(0, 0, vc)],
            Parts = [part],
            AttributeNames = [],
            Min = min,
            Max = max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    private static Vector3 At(float[] pos, int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);

    private static Vector3 At(ModelParts m, int v) => At(m.Positions, v);

    /// <summary>The source's skin triangles that turn over once the field is applied.</summary>
    private static int Folded(Body source, IBodyCorrespondence built)
    {
        Vector3 Moved(int v) => At(source.Model, v) + (built.Field[v] ?? Vector3.Zero);
        var tris = source.Model.Parts[0].Triangles;
        int folded = 0;
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            var before = Vector3.Cross(At(source.Model, b) - At(source.Model, a), At(source.Model, c) - At(source.Model, a));
            var after = Vector3.Cross(Moved(b) - Moved(a), Moved(c) - Moved(a));
            if (before.LengthSquared() > 0f && Vector3.Dot(before, after) <= 0f) folded++;
        }
        return folded;
    }
}
