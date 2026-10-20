using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A target body option that draws underwear where the source has skin (Muse+'s SFW legs: boxers modelled onto the body,
/// the skin cut away under them), by the uv correspondence alone, on synthetic bodies: a flat sheet of skin 1 m tall,
/// and a target that is the same sheet with the band between <see cref="Band"/>'s heights cut out and a sheet of
/// smallclothes laid over that band.
/// </summary>
public class SkinlessTargetTests
{
    internal const string Skin = "/mt_c0201b0001_bibo.mtrl";
    internal const string Boxers = "/mt_c0201e0000_dwn_b.mtrl";

    /// <summary>The hips: the band of the target with no skin, and smallclothes over it.</summary>
    private static readonly (float Y0, float Y1) Band = (0.6f, 0.9f);

    /// <summary>How far in front of the skin the smallclothes sit: 3 mm.</summary>
    private const float Standoff = 0.003f;

    private const int Nx = 6, Ny = 40;

    [Fact]
    [Trait("Plan", "AC-skinless-target-1")]
    public void A_hip_patch_over_modelled_boxers_is_paired()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out var m), refusal);
        Assert.True(built!.TargetWorn);
        int inside = BandVertices(source, Band).Count;
        Assert.True(m.Excused >= inside, $"{m.Excused} excused, {inside} inside the band");
        Assert.Equal(0, m.Astray);
    }

    [Fact]
    [Trait("Plan", "AC-skinless-target-2")]
    public void The_patch_lands_on_the_boxers()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out var m), refusal);
        foreach (int v in BandVertices(source, Band))
        {
            var landed = At(source, v) + built!.Field[v]!.Value;
            Assert.True(MathF.Abs(landed.Z - Standoff) < 1e-4f, $"vertex {v} landed {landed.Z * 1000f:F2} mm out");
        }
        Assert.Equal(0, Folded(source, built!));
        Assert.Equal(0, m.Unprojected);
    }

    [Fact]
    [Trait("Plan", "AC-skinless-target-7")]
    public void The_description_counts_what_followed_onto_the_boxers()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out var m), refusal);
        Assert.Contains($"{m.Excused:N0} where the target has no skin follow the skin around them onto its smallclothes",
                        built!.Describe());
    }

    [Fact]
    [Trait("Plan", "AC-skinless-target-3")]
    [Trait("Plan", "EDGE-skinless-target-3")]
    public void A_different_layout_is_still_refused_though_the_target_models_boxers()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);
        for (int i = 0; i < sourceUv.Length; i++) sourceUv[i] = (sourceUv[i] + 0.37f) % 1f;

        Assert.False(BodyCorrespondence.TryBuild(source, sourceUv, target, targetUv, "legs", out _, out string refusal));
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "AC-skinless-target-5")]
    [Trait("Plan", "EDGE-skinless-target-1")]
    public void Without_smallclothes_the_refusal_is_today_s()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, boxers: null, Standoff);

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out string refusal, out var m));
        Assert.Equal(string.Format("Only {1:P0} of the source {0} body finds its place on the target by texture coordinate, " +
                                   "so the two do not share a texture layout. They cannot be refitted onto each other.",
                                   "legs", m.VertexCoverage), refusal);
        Assert.Equal(0, m.Excused);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-2")]
    public void Boxers_too_far_away_excuse_nothing()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, standoff: 0.06f);

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out string refusal, out var m));
        Assert.Contains("texture layout", refusal);
        Assert.Equal(0, m.Excused);
    }

    [Fact]
    [Trait("Plan", "AC-skinless-target-4")]
    [Trait("Plan", "EDGE-skinless-target-4")]
    public void Too_little_on_skin_says_choose_an_option_with_skin()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target((0.2f, 0.9f), Boxers, Standoff);

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out string refusal, out var m));
        Assert.Equal(string.Format("Only {1:P0} of the source {0} body finds skin to land on: this {0} option of the " +
                                   "target body has no skin over the rest of it and draws something else there, such as " +
                                   "modelled underwear, that the refit could not fit it onto. Choose an option of that " +
                                   "body that has skin there, or refit onto another body.", "legs", m.VertexCoverage),
                     refusal);
        Assert.DoesNotContain("texture layout", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-5")]
    public void Uncovered_misses_beyond_a_fifth_of_the_rest_are_refused()
    {
        // The hips are covered, but a band of the shins has no skin and nothing over it either: more than a fifth of what
        // is left once the hips are set aside.
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff, alsoBare: (0.05f, 0.25f));

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out string refusal, out var m));
        Assert.True(m.Excused > 0);
        // The excused hips are more than half of what missed, so the target's missing skin is named.
        Assert.Contains("finds skin to land on", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-7")]
    public void A_pair_already_over_the_bar_is_untouched_by_boxers()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target((0.85f, 0.9f), Boxers, Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out var m), refusal);
        Assert.False(built!.TargetWorn);
        Assert.Equal(0, m.Excused);
        Assert.DoesNotContain("smallclothes", built.Describe());
        Assert.Contains(BandVertices(source, (0.86f, 0.89f)), v => built.Field[v] == null);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-8")]
    public void A_converted_pair_ignores_the_boxers()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);
        UVRemapService.UvConversion same = (u, v, _) => (u, v);

        Assert.False(UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, "legs", out _, out string refusal,
                                                    out var m, convert: same, sameLayout: true));
        Assert.Contains("texture layout", refusal);
        Assert.Equal(0, m.Excused);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-35")]
    public void Bodies_of_unknown_layout_get_no_excusal()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.False(UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, "legs", out _, out string refusal,
                                                    out var m, sameLayout: false));
        Assert.Contains("texture layout", refusal);
        Assert.Equal(0, m.Excused);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-6")]
    public void Astray_landings_are_never_excused()
    {
        // The band's uvs point at the far end of the sheet: they land, but 0.3 m and more away.
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);
        foreach (int v in BandVertices(source, Band)) sourceUv[v * 2 + 1] -= 0.5f;

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out _, out var m));
        Assert.True(m.Astray > 0);
        Assert.Equal(0, m.Excused);
    }

    [Theory]
    [InlineData("/mt_c0201b0001_betterpube.mtrl")]
    [InlineData("/mt_c0201b0001_neolithe_piercings.mtrl")]
    [InlineData("/mt_c0201b0001_nails.mtrl")]
    [Trait("Plan", "EDGE-skinless-target-13")]
    public void Pubes_piercings_and_nails_are_no_cover(string material)
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, material, Standoff);

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out string refusal, out var m));
        Assert.Contains("texture layout", refusal);
        Assert.Equal(0, m.Excused);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-13")]
    public void Undies_in_e0000_are_cover()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, "/mt_c0201e0000_top_neolithe_undies.mtrl", Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out _), refusal);
        Assert.True(built!.TargetWorn);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-12")]
    public void Boxers_the_mask_hides_are_no_cover()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff, boxersAttribute: "atr_sfw");
        var drawn = BodyRetarget.Without(target, new HashSet<string> { "atr_sfw" });

        Assert.False(Build(source, sourceUv, drawn, targetUv, out _, out string refusal, out var m));
        Assert.Contains("texture layout", refusal);
        Assert.Equal(0, m.Excused);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-17")]
    public void Boxers_alone_are_no_body_skin()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target((0f, 1.01f), Boxers, Standoff);

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out string refusal, out _));
        Assert.Contains("has no body skin to land on", refusal);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-14")]
    public void The_result_does_not_depend_on_vertex_order()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);
        var (reversed, reversedUv, newIndexOf) = Reversed(source, sourceUv);

        Assert.True(Build(source, sourceUv, target, targetUv, out var a, out string why, out _), why);
        Assert.True(Build(reversed, reversedUv, target, targetUv, out var b, out why, out _), why);
        for (int v = 0; v < newIndexOf.Length; v++)
        {
            var da = a!.Field[v];
            var db = b!.Field[newIndexOf[v]];
            Assert.Equal(da.HasValue, db.HasValue);
            if (da is { } x && db is { } y) Assert.True(Vector3.Distance(x, y) < 1e-5f, $"vertex {v}: {x} vs {y}");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-15")]
    public void Weld_twins_in_the_patch_land_together()
    {
        // A uv seam down the middle of the sheet: every vertex there is two, at one position.
        var (source, sourceUv) = Sheet(Skin, seamAt: Nx / 2);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out _), refusal);
        var byPosition = BandVertices(source, Band).GroupBy(v => At(source, v)).Where(g => g.Count() > 1).ToList();
        Assert.NotEmpty(byPosition);
        foreach (var twins in byPosition)
            Assert.Single(twins.Select(v => built!.Field[v]).Distinct());
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-16")]
    public void Uncovered_misses_on_an_accepted_pair_stay_null()
    {
        // The hips are covered; a thin band of the shins has no skin and nothing over it — few enough to pass the bar.
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff, alsoBare: (0.1f, 0.16f));

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out _), refusal);
        var shins = BandVertices(source, (0.1f, 0.16f));
        Assert.NotEmpty(shins);
        Assert.All(shins, v => Assert.Null(built!.Field[v]));
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-18")]
    public void A_man_s_body_onto_modelled_boxers_is_paired()
    {
        var (source, sourceUv) = Sheet("/mt_c0101b0001_b.mtrl");
        var (target, targetUv) = Target(Band, "/mt_c0101e0000_dwn_b.mtrl", Standoff, skin: "/mt_c0101b0001_b.mtrl");

        Assert.True(BodyCorrespondence.TryBuild(source, sourceUv, target, targetUv, "legs", out var built,
                                                out string refusal, male: true), refusal);
        Assert.True(built!.TargetWorn);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-28")]
    public void Smoothing_keeps_the_worn_target()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out _), refusal);
        var smoothed = new SmoothedCorrespondence(built!, target, new Vector3[target.Positions.Length / 3]);
        Assert.True(((IBodyCorrespondence)smoothed).TargetWorn);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-19")]
    public void Rim_snaps_next_to_no_skin_count_as_no_skin()
    {
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.True(Build(source, sourceUv, target, targetUv, out _, out string refusal, out var m), refusal);
        // The band's own rows, and the rows just inside its edges that only snap onto the cut's rim.
        Assert.True(m.NoLanding >= BandVertices(source, Band).Count);
        Assert.Equal(m.NoLanding, m.Excused);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-11")]
    public void Outside_the_midline_band_a_point_stays_on_its_side()
    {
        // Boxers only on the other side of the body, 25 mm across the midline from the sheet: in reach, but not its own.
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff, boxersX: (-0.2f, -0.005f));

        Assert.False(Build(source, sourceUv, target, targetUv, out _, out string refusal, out var m));
        Assert.Contains("texture layout", refusal);
        Assert.Equal(0, m.Excused);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-11")]
    public void In_the_band_the_crotch_midline_is_reachable()
    {
        // The boxers reach across the midline; the sheet's points (x >= 20 mm) land on their own side of it.
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff, boxersX: (-0.05f, 0.2f));

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out _), refusal);
        foreach (int v in BandVertices(source, Band))
            Assert.True(At(source, v).X + built!.Field[v]!.Value.X > 0f, $"vertex {v} crossed the midline");
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-9")]
    public void A_floating_skinless_island_takes_its_nearest_placed_move_then_the_boxers()
    {
        // A small island of its own in the band, joined to nothing, on a patch of the sheet the target has no skin on.
        var (sheet, uv) = Sheet(Skin);
        var pos = sheet.Positions.ToList();
        var uvs = uv.ToList();
        var tris = sheet.Parts[0].Triangles.ToList();
        int first = pos.Count / 3;
        for (int j = 0; j <= 2; j++)
            for (int i = 0; i <= 2; i++)
            {
                pos.AddRange([0.08f + 0.02f * i, 0.7f + 0.02f * j, -0.002f]);
                uvs.AddRange([0.6f + 0.02f * i, 0.6f + 0.02f * j]);
            }
        var island = Enumerable.Range(first, 9).ToList();
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int a = first + j * 3 + i, b = a + 1, d = a + 3, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        var source = Model(pos.ToArray(), [(Skin, tris.ToArray(), 0u)], []);
        var (target, targetUv) = Target(Band, Boxers, Standoff);

        Assert.True(Build(source, uvs.ToArray(), target, targetUv, out var built, out string refusal, out _), refusal);
        foreach (int v in island)
        {
            var landed = At(source, v) + built!.Field[v]!.Value;
            Assert.True(MathF.Abs(landed.Z - Standoff) < 1e-3f, $"island vertex {v} at {landed.Z * 1000f:F2} mm");
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-20")]
    public void A_wrong_landing_on_the_new_path_is_set_aside()
    {
        // One vertex beside the hips whose uv points 15 cm down the leg: it lands, wrongly, and its neighbours disagree.
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff);
        int lifted = Enumerable.Range(0, source.Positions.Length / 3)
                               .First(v => MathF.Abs(source.Positions[v * 3 + 1] - 0.55f) < 1e-3f
                                           && MathF.Abs(source.Positions[v * 3] - 0.11f) < 1e-3f);
        sourceUv[lifted * 2 + 1] -= 0.9f * 0.15f;

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out var m), refusal);
        Assert.True(m.Disagreed >= 1, "the wrong landing was trusted");
        // It follows the skin around it instead: a few millimetres, not 15 cm.
        Assert.True(built!.Field[lifted]!.Value.Length() < 0.01f);
    }

    [Fact]
    [Trait("Plan", "EDGE-skinless-target-10")]
    public void A_sharp_dent_in_the_boxers_leaves_no_fold()
    {
        // The seat's middle vertex pushed 30 mm in: landings round it pull in toward the dent and can cross.
        var (source, sourceUv) = Sheet(Skin);
        var (target, targetUv) = Target(Band, Boxers, Standoff, notch: 0.03f);

        Assert.True(Build(source, sourceUv, target, targetUv, out var built, out string refusal, out var m), refusal);
        Assert.Equal(0, Folded(source, built!));
        Assert.True(m.Unprojected <= UvAtlasCorrespondence.MaxUnprojectedShare * m.Excused);
    }

    private static bool Build(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv,
                              out UvAtlasCorrespondence? built, out string refusal, out UvAtlasCorrespondence.Stats m)
        => UvAtlasCorrespondence.TryBuild(source, sourceUv, target, targetUv, "legs", out built, out refusal, out m,
                                          sameLayout: true);

    /// <summary>
    /// A flat sheet of skin, 0.02..0.2 m across and 0..1 m tall at z = 0, facing +z; uv u 0.1..0.5, v 0.05..0.95.
    /// </summary>
    /// <param name="seamAt">A column of the grid split into two vertices at one position, as a uv seam does.</param>
    internal static (ModelParts Model, float[] Uv) Sheet(string material, int? seamAt = null)
    {
        var pos = new List<float>();
        var uv = new List<float>();
        var tris = new List<int>();
        int cols = Nx + 1 + (seamAt != null ? 1 : 0);
        for (int j = 0; j <= Ny; j++)
            for (int c = 0; c < cols; c++)
            {
                int i = seamAt is { } s && c > s ? c - 1 : c;
                float x = 0.02f + 0.18f * i / Nx, y = (float)j / Ny;
                pos.AddRange([x, y, 0f]);
                float u = 0.1f + 0.4f * i / Nx + (seamAt is { } s2 && c > s2 ? 0.0001f : 0f);
                uv.AddRange([u, 0.05f + 0.9f * y]);
            }
        for (int j = 0; j < Ny; j++)
            for (int c = 0; c < cols - 1; c++)
            {
                if (seamAt is { } s && c == s) continue;   // the seam: the two columns meet by position only
                int a = j * cols + c, b = a + 1, d = a + cols, e = d + 1;
                tris.AddRange([a, b, e, a, e, d]);
            }
        return (Model(pos.ToArray(), [(material, tris.ToArray(), 0u)], []), uv.ToArray());
    }

    /// <summary>
    /// The source's sheet with the skin cut out of <paramref name="bare"/> (and <paramref name="alsoBare"/>), and a sheet
    /// of <paramref name="boxers"/> laid <paramref name="standoff"/> in front of the bare band, on a uv sheet of its own.
    /// </summary>
    /// <param name="boxersX">Across where the boxers lie: the sheet's own 0.02..0.2 m when null.</param>
    /// <param name="notch">Pushes the boxers' vertex nearest the band's middle this far in, for a sharp dent.</param>
    internal static (ModelParts Model, float[] Uv) Target((float Y0, float Y1) bare, string? boxers, float standoff,
                                                         (float Y0, float Y1)? alsoBare = null, string skin = Skin,
                                                         string? boxersAttribute = null,
                                                         (float X0, float X1)? boxersX = null, float notch = 0f)
    {
        var (sheet, uv) = Sheet(skin);
        var pos = sheet.Positions.ToList();
        var uvs = uv.ToList();
        bool Cut(float y) => (y > bare.Y0 && y < bare.Y1) || (alsoBare is { } b && y > b.Y0 && y < b.Y1);
        var skinTris = new List<int>();
        var t = sheet.Parts[0].Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            float cy = (pos[t[i] * 3 + 1] + pos[t[i + 1] * 3 + 1] + pos[t[i + 2] * 3 + 1]) / 3f;
            if (!Cut(cy)) skinTris.AddRange([t[i], t[i + 1], t[i + 2]]);
        }
        var parts = new List<(string, int[], uint)> { (skin, skinTris.ToArray(), 0u) };
        if (boxers != null)
        {
            int first = pos.Count / 3, rows = 12;
            var (x0, x1) = boxersX ?? (0.02f, 0.2f);
            for (int j = 0; j <= rows; j++)
                for (int i = 0; i <= Nx; i++)
                {
                    float y = bare.Y0 - 0.02f + (bare.Y1 - bare.Y0 + 0.04f) * j / rows;
                    float z = standoff - (notch != 0f && j == rows / 2 && i == Nx / 2 ? notch : 0f);
                    pos.AddRange([x0 + (x1 - x0) * i / Nx, y, z]);
                    uvs.AddRange([0.6f + 0.3f * i / Nx, 0.1f + 0.3f * j / rows]);
                }
            var bt = new List<int>();
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < Nx; i++)
                {
                    int a = first + j * (Nx + 1) + i, b = a + 1, d = a + Nx + 1, e = d + 1;
                    bt.AddRange([a, b, e, a, e, d]);
                }
            parts.Add((boxers, bt.ToArray(), boxersAttribute != null ? 1u : 0u));
        }
        return (Model(pos.ToArray(), parts, boxersAttribute != null ? [boxersAttribute] : []), uvs.ToArray());
    }

    internal static ModelParts Model(float[] pos, List<(string Material, int[] Triangles, uint Mask)> parts,
                                     List<string> attributes)
    {
        int vc = pos.Length / 3;
        var nrm = new float[pos.Length];
        for (int v = 0; v < vc; v++) nrm[v * 3 + 2] = 1f;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < vc; v++)
        {
            min = Vector3.Min(min, At(pos, v));
            max = Vector3.Max(max, At(pos, v));
        }
        var list = parts.Select((p, i) => new ModelPart
        {
            Mesh = i,
            Submesh = 0,
            Island = -1,
            Label = $"{i}.0",
            Material = p.Material,
            Triangles = p.Triangles,
            Ordinals = Enumerable.Range(0, p.Triangles.Length / 3).ToArray(),
            AttributeMask = p.Mask,
            Min = min,
            Max = max,
            Toggleable = true,
        }).ToList();
        return new ModelParts
        {
            Positions = pos,
            Normals = nrm,
            MeshSpans = [new MeshSpan(0, 0, vc)],
            Parts = list,
            AttributeNames = attributes,
            Min = min,
            Max = max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    /// <summary>The same body, its vertices numbered backwards.</summary>
    private static (ModelParts Model, float[] Uv, int[] NewIndexOf) Reversed(ModelParts m, float[] uv)
    {
        int vc = m.Positions.Length / 3;
        var newIndexOf = Enumerable.Range(0, vc).Select(v => vc - 1 - v).ToArray();
        var pos = new float[m.Positions.Length];
        var nuv = new float[uv.Length];
        for (int v = 0; v < vc; v++)
        {
            Array.Copy(m.Positions, v * 3, pos, newIndexOf[v] * 3, 3);
            Array.Copy(uv, v * 2, nuv, newIndexOf[v] * 2, 2);
        }
        var parts = m.Parts.Select(p => (p.Material, p.Triangles.Select(i => newIndexOf[i]).ToArray(), p.AttributeMask)).ToList();
        return (Model(pos, parts, m.AttributeNames.ToList()), nuv, newIndexOf);
    }

    private static List<int> BandVertices(ModelParts m, (float Y0, float Y1) band)
        => Enumerable.Range(0, m.Positions.Length / 3).Where(v => m.Positions[v * 3 + 1] > band.Y0 + 0.01f
                                                                   && m.Positions[v * 3 + 1] < band.Y1 - 0.01f).ToList();

    private static Vector3 At(float[] pos, int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);

    private static Vector3 At(ModelParts m, int v) => At(m.Positions, v);

    private static int Folded(ModelParts source, IBodyCorrespondence built)
    {
        Vector3 Moved(int v) => At(source, v) + (built.Field[v] ?? Vector3.Zero);
        int folded = 0;
        foreach (var part in source.Parts)
        {
            var t = part.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                var before = Vector3.Cross(At(source, b) - At(source, a), At(source, c) - At(source, a));
                var after = Vector3.Cross(Moved(b) - Moved(a), Moved(c) - Moved(a));
                if (before.LengthSquared() > 0f && Vector3.Dot(before, after) <= 0f) folded++;
            }
        }
        return folded;
    }
}
