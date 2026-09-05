using System.Collections.Generic;
using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="SurfaceMirror"/> — the pair of measurements that make un-mirroring data-driven rather than a
/// table of body types: which side of the character each vertex is on, and whether a mesh's UV layout is
/// mirrored at all.
/// <para/>
/// Synthetic meshes on purpose. The real numbers are recorded on the type itself (vanilla reads 89-97%
/// mirrored, Bibo+ the reverse) and re-measuring them needs a game install; what has to be pinned here is
/// the LOGIC, which is what a refactor can break silently.
/// </summary>
public class SurfaceMirrorTests
{
    // ── AssignSides ──────────────────────────────────────────────────────────

    /// <summary>
    /// The ordinary case: triangles wholly on one side hand their side to their vertices, including the ones
    /// sitting exactly on the midline whose own X says nothing.
    /// </summary>
    [Fact]
    public void A_triangle_gives_its_side_to_its_vertices_midline_ones_included()
    {
        //             0    1    2     3     4     5    6    7
        var x = new[] { 1f, 1f, 1f, -1f, -1f, -1f, 0f, 0f };
        var tris = new List<ushort> { 0, 1, 6, 3, 4, 7 };

        var sides = SurfaceMirror.AssignSides(x, tris, out int conflicts, out int straddling);

        Assert.Equal(0, conflicts);
        Assert.Equal(0, straddling);
        Assert.Equal(1, sides[0]);
        Assert.Equal(1, sides[1]);
        Assert.Equal(1, sides[6]);    // on the midline, placed by the triangle that uses it
        Assert.Equal(-1, sides[3]);
        Assert.Equal(-1, sides[4]);
        Assert.Equal(-1, sides[7]);
        Assert.Equal(0, sides[2]);    // never referenced by any triangle
        Assert.Equal(0, sides[5]);
    }

    /// <summary>
    /// A midline vertex claimed by triangles on BOTH sides has no single answer. It must come back 0 — which
    /// the converter treats as "leave it where the author put it" — rather than taking whichever triangle was
    /// visited last. Guessing here stretches that vertex's triangles across the whole sheet, since under
    /// un-mirroring the two halves are a long way apart.
    /// </summary>
    [Fact]
    public void A_vertex_both_sides_claim_is_reported_unplaced()
    {
        var x = new[] { 1f, 1f, -1f, -1f, 0f };
        // Both triangles use vertex 4, from opposite sides.
        var tris = new List<ushort> { 0, 1, 4, 2, 3, 4 };

        var sides = SurfaceMirror.AssignSides(x, tris, out int conflicts, out int straddling);

        Assert.Equal(1, conflicts);
        Assert.Equal(0, straddling);
        Assert.Equal(0, sides[4]);
        // The undisputed vertices still get their side — one bad vertex must not poison the mesh.
        Assert.Equal(1, sides[0]);
        Assert.Equal(-1, sides[2]);
    }

    /// <summary>
    /// A triangle spanning x = 0 is counted and skipped, not assigned. A mirrored UV layout needs a seam down
    /// the body's centre, which splits those vertices already — so a straddling triangle means that
    /// assumption failed, and the count is what surfaces it (measured zero on every vanilla part).
    /// </summary>
    [Fact]
    public void A_triangle_spanning_the_midline_is_counted_and_placed_nowhere()
    {
        var x = new[] { 1f, 1f, -1f };
        var tris = new List<ushort> { 0, 1, 2 };

        var sides = SurfaceMirror.AssignSides(x, tris, out int conflicts, out int straddling);

        Assert.Equal(1, straddling);
        Assert.Equal(0, conflicts);
        Assert.All(sides, s => Assert.Equal(0, s));
    }

    /// <summary>
    /// A triangle lying entirely within the midline band can't place itself either, and leaving it
    /// unassigned is deliberate: its vertices stay free for a neighbouring triangle that DOES know its side
    /// to claim. It is not a straddle — nothing about it is inconsistent.
    /// </summary>
    [Fact]
    public void A_triangle_wholly_on_the_midline_leaves_its_vertices_for_a_neighbour()
    {
        var x = new[] { 0f, 0f, 0f, 1f, 1f };
        var tris = new List<ushort> { 0, 1, 2, /* then a real one reusing vertex 2 */ 3, 4, 2 };

        var sides = SurfaceMirror.AssignSides(x, tris, out int conflicts, out int straddling);

        Assert.Equal(0, straddling);
        Assert.Equal(0, conflicts);
        Assert.Equal(1, sides[2]);   // claimed by the +X triangle, not zeroed by the midline one
    }

    /// <summary>Out-of-range indices are skipped rather than throwing — the writer feeds raw index data.</summary>
    [Fact]
    public void Indices_past_the_vertex_count_are_ignored()
    {
        var x = new[] { 1f, 1f, 1f };
        var tris = new List<ushort> { 0, 1, 99 };

        var sides = SurfaceMirror.AssignSides(x, tris, out _, out _);
        Assert.All(sides, s => Assert.Equal(0, s));
    }

    // ── LooksMirrored ────────────────────────────────────────────────────────

    /// <summary>
    /// A lateral pair of grids: every vertex at +x has a partner at -x in the same place. <paramref name="uv"/>
    /// decides what UV the -X partner gets, which is the whole question.
    /// </summary>
    private static (float[] Pos, float[] Uv) Slab(bool mirroredUv)
    {
        const int N = 12;   // 12 x 12 x 2 sides = 288 vertices, well past the 64-pair floor
        var pos = new List<float>();
        var uv = new List<float>();
        for (int j = 0; j < N; j++)
            for (int k = 0; k < N; k++)
            {
                float y = 0.1f + j * 0.05f, z = 0.1f + k * 0.05f;
                float u = 0.55f + j * 0.02f, v = 0.1f + k * 0.05f;

                pos.Add(0.5f); pos.Add(y); pos.Add(z);      // +X
                uv.Add(u); uv.Add(v);

                pos.Add(-0.5f); pos.Add(y); pos.Add(z);     // -X, its mirror partner
                // Mirrored: the two sides share a texel. Un-mirrored: they reflect about u = 0.5.
                uv.Add(mirroredUv ? u : 1f - u); uv.Add(v);
            }
        return (pos.ToArray(), uv.ToArray());
    }

    /// <summary>Vanilla's layout: mirror partners share a UV, so one sheet describes both sides.</summary>
    [Fact]
    public void Partners_sharing_a_uv_read_as_mirrored()
    {
        var (pos, uv) = Slab(mirroredUv: true);
        Assert.True(SurfaceMirror.LooksMirrored(pos, uv));
    }

    /// <summary>Bibo's layout: partners reflect about the middle of the sheet, so each side owns its own half.</summary>
    [Fact]
    public void Partners_reflecting_about_the_middle_do_not()
    {
        var (pos, uv) = Slab(mirroredUv: false);
        Assert.False(SurfaceMirror.LooksMirrored(pos, uv));
    }

    /// <summary>
    /// Too small to judge, or laterally asymmetric enough that no partners are found, must answer false —
    /// the safe answer, which leaves the caller on the behaviour it already had rather than un-mirroring a
    /// mesh on no evidence.
    /// </summary>
    [Fact]
    public void Too_little_evidence_answers_false()
    {
        Assert.False(SurfaceMirror.LooksMirrored([], []));
        Assert.False(SurfaceMirror.LooksMirrored([1f, 0f, 0f], [0.5f, 0.5f]));

        // A mesh with no mirror partners at all: every vertex on one side of the body.
        var (pos, uv) = Slab(mirroredUv: true);
        var oneSided = new List<float>();
        var oneSidedUv = new List<float>();
        for (int i = 0; i < pos.Length / 3; i++)
        {
            if (pos[i * 3] < 0) continue;
            oneSided.Add(pos[i * 3]); oneSided.Add(pos[i * 3 + 1]); oneSided.Add(pos[i * 3 + 2]);
            oneSidedUv.Add(uv[i * 2]); oneSidedUv.Add(uv[i * 2 + 1]);
        }
        Assert.False(SurfaceMirror.LooksMirrored(oneSided.ToArray(), oneSidedUv.ToArray()));
    }

    /// <summary>
    /// The verdict must survive a mesh whose UVs are authored on another tile — vanilla parts sit at u in
    /// [1..2] and v in [-1..0], and the writer shifts them back before use, so the detector has to do the
    /// same or it reads every real body part as un-mirrored.
    /// </summary>
    [Fact]
    public void A_uv_set_on_another_tile_still_reads_as_mirrored()
    {
        var (pos, uv) = Slab(mirroredUv: true);
        for (int i = 0; i < uv.Length; i += 2) { uv[i] += 1f; uv[i + 1] -= 1f; }
        Assert.True(SurfaceMirror.LooksMirrored(pos, uv));
    }
}
