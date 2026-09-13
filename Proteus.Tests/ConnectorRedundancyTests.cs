using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// When the redundancy pass drops a submesh, and when it must not.
/// <para/>
/// It exists to remove skin a shell would otherwise draw twice, in two shapes that are redundant against
/// two different things: a thin seam RING at a joint, redundant because the NEIGHBOURING PART draws the
/// same stretch of body, and a DUPLICATE of a region, redundant because a SIBLING SUBMESH already draws it.
/// <para/>
/// Both rules have eaten real skin in the past, in ways worth keeping tests for:
/// <list type="number">
/// <item>The ring size test was absolute ("under 200 triangles"), read off a body whose real parts run
/// 800+. Gear that ships its own skin cuts it far coarser — a garment's whole exposed torso can be 500
/// triangles, so its neck (20) and elbow (144) both looked like rings.</item>
/// <item>Redundancy was assumed rather than checked. A ring at the top of a hand model IS covered by the
/// part above it; a neck ring at the top of a torso has nothing above it and is the only thing painting
/// that band of the character.</item>
/// <item>The duplicate rule fired on a mesh's LAST submesh whenever a sibling's Y band contained it. That
/// is a fact about where one body mod puts its second calf, not evidence that anything is drawn twice, and
/// the setting had to name that body to be safe. It asks about the SURFACE now.</item>
/// </list>
/// The two rules stay exclusive on SIZE, which is what protects a bare neck: a ring at a mesh's own edge is
/// always nested inside that mesh's main submesh, so a duplicate test applied to it would delete it.
/// </summary>
public class ConnectorRedundancyTests
{
    private const string BodyMaterial = "/mt_c0201b0001_a.mtrl";

    private const int MainTris = 400;
    private const int TailTris = 8;

    /// <summary>Far enough apart that two submeshes are unmistakably different regions — see
    /// <see cref="SyntheticModel.Sub.OffsetX"/> for why anything less is the fixture answering for the
    /// code.</summary>
    private const float Elsewhere = 100f;

    /// <summary>
    /// A mesh shaped like the real thing: a big main part, the submesh under test in the MIDDLE, and a small
    /// trailing one. The subject must not be last — the rule that used to fire on a mesh's final submesh is
    /// exactly what these tests have to be able to tell apart from the ring rule. Rinoa's elbow is submesh 1
    /// of 4 for the same reason.
    /// <para/>
    /// Each submesh sits in its OWN place. Left at the default they would be stacked on top of one another,
    /// and every duplicate verdict below would be the fixture's doing rather than the code's.
    /// </summary>
    private static byte[] Model(int subjectTris) => SyntheticModel.Build(
        ["atr_top"],
        new SyntheticModel.Mesh(BodyMaterial,
            new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris),
            new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: subjectTris, OffsetX: Elsewhere),
            new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: TailTris, OffsetX: 2 * Elsewhere)));

    /// <summary>
    /// A neighbouring part, as one submesh — so nothing of it can itself be judged redundant and it
    /// contributes only its extent. Islands spread it across X (10 units apart, per
    /// <see cref="SyntheticModel.Sub.Islands"/>) because the ring rule compares all three axes: a part at
    /// the same HEIGHT as a ring is not evidence that it draws it.
    /// </summary>
    private static byte[] Neighbour(float lo, int islands, int tris) => SyntheticModel.Build(
        ["atr_top"],
        new SyntheticModel.Mesh(BodyMaterial,
            new SyntheticModel.Sub(0, Islands: islands, TrianglesPerIsland: tris, OffsetY: lo)));

    /// <summary>A part that encloses the whole subject model, and one nowhere near it. The shell's layout
    /// is read off the SOURCES now, so a neighbour is a source, not a number handed in beside one — there
    /// is no longer a way to claim coverage without geometry to back it.</summary>
    private static byte[] Covering() => Neighbour(-5f, islands: 30, tris: 20);
    private static byte[] FarAway() => Neighbour(500f, islands: 30, tris: 5);

    /// <summary>Triangles the pass removed from <paramref name="model"/>, with the given neighbours.</summary>
    private static int Removed(byte[] model, params byte[][] neighbours)
        => Build(model, drop: false, neighbours).TrianglesOut
         - Build(model, drop: true, neighbours).TrianglesOut;

    private static SecondSkinWriter.Stats Build(byte[] model, bool drop, byte[][] neighbours)
    {
        var layers = new[] { new SecondSkinLayer { MaterialName = "/ss_0.mtrl", Coverage = null } };
        var sources = new List<SecondSkinWriter.SourceSpec>
        {
            new(model, DropConnectors: drop),
        };
        // The neighbours never run the pass themselves: they are here to be a part layout, and leaving them
        // out of it keeps every triangle the assertions count attributable to the model under test.
        sources.AddRange(neighbours.Select(n => new SecondSkinWriter.SourceSpec(n, DropConnectors: false)));
        SecondSkinWriter.Build(sources, layers, null, out var stats);
        return stats;
    }

    /// <summary>The case the ring rule is FOR: a small submesh whose band another part already draws.</summary>
    [Fact]
    public void A_ring_another_part_covers_is_dropped()
    {
        // Both the subject and the trailing submesh are ring-sized and both bands are covered, so both go.
        Assert.Equal(TailTris + TailTris, Removed(Model(TailTris), Covering()));
    }

    /// <summary>
    /// The neck: identical in shape and size to a seam ring, but nothing else in the shell covers it. It is
    /// the only geometry painting that band, so it stays — and so does the trailing submesh, which is also
    /// the mesh's LAST. That second half is the guard on the two rules staying exclusive: a ring at a mesh's
    /// edge is nested inside that mesh's main submesh, so a duplicate test reaching it would delete it.
    /// </summary>
    [Fact]
    public void A_ring_nothing_covers_is_kept()
    {
        Assert.Equal(0, Removed(Model(TailTris), FarAway()));
    }

    /// <summary>
    /// A part alone in the shell has no neighbour, so none of its RINGS can be redundant. "Nothing else is
    /// here" is a real answer, and it used to be confused with "nobody told me what else is here" — which
    /// the old API read as licence to drop on shape alone.
    /// </summary>
    [Fact]
    public void A_lone_part_keeps_every_ring()
    {
        Assert.Equal(0, Removed(Model(TailTris)));
    }

    /// <summary>
    /// The duplicate, in the shape a real body has it: a submesh far too big to be a seam ring, drawing a
    /// surface a sibling already draws. It goes even though the part is ALONE in the shell — what makes it
    /// redundant is its own sibling, and no arrangement of other parts has any bearing on it.
    /// <para/>
    /// This is the doubled-stocking case. Judging it by the parts is what kept it: on the real body it is
    /// 2184 triangles from the ankle to below the knee, and nothing else in a shell reaches there.
    /// </summary>
    [Fact]
    public void A_duplicate_a_sibling_already_draws_is_dropped_even_when_alone()
    {
        const int VariantTris = 200;   // half the main submesh — a body region, nowhere near ring-shaped
        var model = SyntheticModel.Build(
            ["atr_top"],
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris),
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: VariantTris)));

        Assert.Equal(VariantTris, Removed(model));
    }

    /// <summary>
    /// The same two submeshes, moved apart. Nothing else about them changed — same sizes, same order, same
    /// Y band — so this is the whole difference between the old rule and this one: being the smaller of two
    /// submeshes is not evidence, and occupying the same SURFACE is.
    /// </summary>
    [Fact]
    public void A_second_submesh_somewhere_else_is_not_a_duplicate()
    {
        var model = SyntheticModel.Build(
            ["atr_top"],
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris),
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: 200, OffsetX: Elsewhere)));

        Assert.Equal(0, Removed(model));
    }

    /// <summary>
    /// A connector shipped as its OWN MESH is the same connector.
    /// <para/>
    /// Where a body puts these is an authoring choice: two bodies make them extra submeshes of the skin
    /// mesh, a third gives each its own single-submesh mesh. Judged per mesh, the third is invisible to
    /// both rules by construction — a mesh's only submesh is 100% of its mesh, so it is never small enough
    /// to read as a ring, and it has no siblings to duplicate. The pass found nothing at all on that body
    /// while the wearer was looking at the seams.
    /// </summary>
    [Fact]
    public void A_duplicate_shipped_as_its_own_mesh_is_still_a_duplicate()
    {
        const int BandTris = 200;
        var model = SyntheticModel.Build(
            ["atr_top"],
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris)),
            // Its own mesh, one submesh, drawn over the same surface as the first.
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: BandTris)));

        Assert.Equal(BandTris, Removed(model));
    }

    /// <summary>The same second mesh somewhere else is its own region and stays — the scope widened, the
    /// evidence did not.</summary>
    [Fact]
    public void A_second_mesh_somewhere_else_is_not_a_duplicate()
    {
        var model = SyntheticModel.Build(
            ["atr_top"],
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris)),
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: 200, OffsetX: Elsewhere)));

        Assert.Equal(0, Removed(model));
    }

    /// <summary>
    /// Three copies of one region, each fully drawn by the other two. A symmetric rule has no way to choose
    /// and would drop all three, leaving a hole where the geometry used to be; the pass walks largest-first
    /// against what it has already kept, so exactly one survives.
    /// </summary>
    [Fact]
    public void Mutually_coincident_submeshes_leave_exactly_one()
    {
        var model = SyntheticModel.Build(
            ["atr_top"],
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris),
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris),
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris)));

        Assert.Equal(2 * MainTris, Removed(model));
        Assert.Equal(MainTris, Build(model, drop: true, []).TrianglesOut);
    }

    /// <summary>
    /// Two limbs in one mesh sit at the same height and differ only across the body. A Y-only comparison
    /// cannot tell them from a duplicate and would delete one; the surface test measures in three dimensions,
    /// and 20cm apart is not the same surface however much of a band they share.
    /// </summary>
    [Fact]
    public void Mirrored_limbs_in_one_mesh_both_survive()
    {
        var model = SyntheticModel.Build(
            ["atr_top"],
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris),
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris, OffsetX: 0.2f)));

        Assert.Equal(0, Removed(model));
    }

    /// <summary>
    /// Two parts that each ship the same joint ring must not talk each other into dropping both of them.
    /// <para/>
    /// A part's extent is what justifies dropping another part's ring, so it has to be built from the
    /// geometry that will still be there afterwards. Counting the rings makes it circular: each part
    /// reaches the joint only BECAUSE of its own ring, each ring is then found inside the other part's
    /// extent, and both go — leaving nothing drawing that band. Measured on a real body at the wrist,
    /// where a top and a hands model each carry a 120-triangle copy of the same ring.
    /// <para/>
    /// One copy of a doubled ring is the right answer, and which one does not matter.
    /// </summary>
    [Fact]
    public void Two_parts_shipping_the_same_ring_do_not_both_drop_it()
    {
        // Each part is a big body region plus a small ring at the SAME place — the join between them.
        // Neither part's substantial geometry reaches the other's, so the only thing that could make
        // either ring look redundant is the other ring.
        byte[] Part(float bodyY) => SyntheticModel.Build(
            ["atr_top"],
            new SyntheticModel.Mesh(BodyMaterial,
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: MainTris, OffsetY: bodyY),
                new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: TailTris, OffsetX: Elsewhere)));

        var lower = Part(-MainTris);   // spans y -400..0
        var upper = Part(1f);          // spans y 1..401

        // Both rings sit at y 0..8, in the gap between the two bodies, and both parts run the pass.
        var layers = new[] { new SecondSkinLayer { MaterialName = "/ss_0.mtrl", Coverage = null } };
        SecondSkinWriter.Build(
            [new SecondSkinWriter.SourceSpec(lower, DropConnectors: true),
             new SecondSkinWriter.SourceSpec(upper, DropConnectors: true)],
            layers, null, out var trimmed);

        Assert.True(trimmed.RedundantSubs <= 1,
            $"both copies of the ring were dropped ({trimmed.RedundantSubs} submeshes) — nothing is left "
          + "drawing that band");
    }

    /// <summary>
    /// The elbow: big RELATIVE to its own mesh, so it is not ring-shaped however few triangles it has in
    /// absolute terms — and it is its own region, so nothing else draws it. Only the trailing submesh goes.
    /// This is what the old flat "under 200" test got wrong on a low-poly source.
    /// </summary>
    [Fact]
    public void A_submesh_large_relative_to_its_mesh_is_kept_even_where_covered()
    {
        // 120 against a 400-triangle main part is 30% of the mesh — a body region, not a seam.
        Assert.Equal(TailTris, Removed(Model(120), Covering()));
    }

    // ── the verdict rules, without a model ────────────────────────────────────────────────────────
    //
    // Straight at PlanConnectorDrops, because the cases below are about the DECISION and reaching them
    // through Build means encoding each one as .mdl geometry first. Splitting the measurement out from the
    // verdict is what makes this possible at all — a profile is a handful of numbers.

    /// <summary>One triangle of the fixture profile, as a corner origin. Triangles are spaced far enough
    /// apart in Y not to touch, so a submesh covering N of another's triangles covers exactly 3N of its
    /// vertices and a coverage fraction is an exact figure rather than a near one.</summary>
    private static (float X, float Y)[] Row(int n, float x, float y)
        => Enumerable.Range(0, n).Select(i => (x, y + i * 10f)).ToArray();

    /// <summary>A profile of one mesh, from an explicit list of submeshes and where each one's triangles
    /// sit. Two submeshes sharing origins are the same surface; that is the whole vocabulary needed here.
    /// </summary>
    private static SecondSkinWriter.ConnectorProfile Profile(
        params (uint Attr, (float X, float Y)[] Tris)[] subs)
    {
        var pos = new List<float>();
        var tris = new List<ushort>();
        var verts = new List<ushort>();
        var list = new List<SecondSkinWriter.ConnectorProfile.Sub>();
        float lo = float.MaxValue, hi = float.MinValue;

        for (int i = 0; i < subs.Length; i++)
        {
            var (attr, origins) = subs[i];
            int triFirst = tris.Count / 3, vertFirst = verts.Count;
            float mnx = float.MaxValue, mny = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue;
            foreach (var (x, y) in origins)
            {
                foreach (var (vx, vy) in new[] { (x, y), (x + 1f, y), (x, y + 1f) })
                {
                    ushort v = (ushort)(pos.Count / 3);
                    pos.Add(vx); pos.Add(vy); pos.Add(0f);
                    tris.Add(v);
                    verts.Add(v);
                    if (vx < mnx) mnx = vx;
                    if (vx > mxx) mxx = vx;
                    if (vy < mny) mny = vy;
                    if (vy > mxy) mxy = vy;
                }
            }
            if (mny < lo) lo = mny;
            if (mxy > hi) hi = mxy;
            list.Add(new SecondSkinWriter.ConnectorProfile.Sub(
                Mesh: 0, Index: i, AttrMask: attr, Triangles: origins.Length,
                Box: new SecondSkinWriter.ConnectorProfile.Box(mnx, mny, 0f, mxx, mxy, 0f),
                VertFirst: vertFirst, VertCount: verts.Count - vertFirst,
                TriFirst: triFirst, TriCount: tris.Count / 3 - triFirst));
        }

        int largest = list.Count == 0 ? 0 : list.Max(s => s.Triangles);
        return new SecondSkinWriter.ConnectorProfile
        {
            Meshes = [new SecondSkinWriter.ConnectorProfile.MeshProfile(
                0, largest, [.. pos], [.. tris], [.. verts], 0, list.Count)],
            Subs = [.. list],
            PartBox = lo <= hi
                ? new SecondSkinWriter.ConnectorProfile.Box(
                    list.Min(s => s.Box.MinX), lo, 0f, list.Max(s => s.Box.MaxX), hi, 0f)
                : null,
            Vertices = verts.Count,
        };
    }

    private static HashSet<(int Mesh, int Sub)> Plan(
        SecondSkinWriter.ConnectorProfile profile,
        IReadOnlyList<SecondSkinWriter.ConnectorProfile.Box>? boxes = null,
        System.Func<uint, bool>? isHidden = null)
        => SecondSkinWriter.PlanConnectorDrops(profile, boxes ?? [], isHidden, null, "test", out _, out _);

    /// <summary>
    /// A sibling the pack's own toggles have switched off does not count as cover. It is emptied a few lines
    /// after this decision is read, so counting it would drop a real region on the strength of geometry that
    /// never draws — and leave the band bare.
    /// </summary>
    [Fact]
    public void A_hidden_sibling_does_not_cover_anything()
    {
        // THREE submeshes, and the third is load-bearing. With only the cover and the candidate, hiding the
        // cover leaves one live submesh and the mesh is skipped wholesale by "a mesh drawing one thing IS
        // that thing" — so the assertion passes without the hidden-cover logic ever running. The spare
        // keeps two submeshes live, so the pass reaches the decision and has to get it right.
        var profile = Profile(
            (0b1u, Row(100, 0f, 0f)),         // the cover — same place as the candidate
            (0u,   Row(100, 0f, 0f)),         // the candidate
            (0u,   Row(100, 5000f, 0f)));     // somewhere else entirely, and never redundant

        Assert.Equal([(0, 1)], Plan(profile));

        // Cover hidden: it is not going to be drawn, so it cannot be what already draws the candidate.
        Assert.Empty(Plan(profile, isHidden: mask => (mask & 1u) != 0));
    }

    /// <summary>
    /// 90% of a submesh already drawn is a duplicate; 89% is a region that happens to overlap. The threshold
    /// is high because the two failure directions are not symmetric — too low takes a band of skin off the
    /// character, too high leaves the doubled alpha this exists to remove.
    /// </summary>
    [Theory]
    [InlineData(90, true)]
    [InlineData(89, false)]
    public void The_coincidence_threshold_is_where_it_says_it_is(int coveredTris, bool dropped)
    {
        // The cover draws `coveredTris` of the candidate's 100 triangles and spends the rest of its own
        // budget elsewhere, so the two are the same size and the tie-break — ascending index — decides which
        // is judged against which.
        var cover = Row(coveredTris, 0f, 0f).Concat(Row(100 - coveredTris, 5000f, 0f)).ToArray();
        var profile = Profile((0u, cover), (0u, Row(100, 0f, 0f)));

        Assert.Equal(dropped ? 1 : 0, Plan(profile).Count);
    }

    /// <summary>
    /// Equal-sized submeshes drawing the same surface must not have the answer decided by sort stability.
    /// The lower index survives, always.
    /// </summary>
    [Fact]
    public void Ties_break_on_the_lower_submesh_index()
    {
        var profile = Profile((0u, Row(50, 0f, 0f)), (0u, Row(50, 0f, 0f)), (0u, Row(50, 0f, 0f)));
        Assert.Equal([(0, 1), (0, 2)], Plan(profile).OrderBy(d => d.Sub).ToArray());
    }

    /// <summary>
    /// A mesh with one submesh IS that submesh — there is nothing beside it for it to be redundant against,
    /// whatever the rest of the shell looks like.
    /// </summary>
    [Fact]
    public void A_single_submesh_mesh_is_never_touched()
    {
        var profile = Profile((0u, Row(4, 0f, 0f)));
        Assert.Empty(Plan(profile, [Everything]));
    }

    /// <summary>
    /// A ring at the same HEIGHT as a neighbouring part but somewhere else entirely — the other arm, the
    /// far side of the body — is not drawn by it. This is the case a Y-only comparison could not see, and
    /// it is not hypothetical: measured on a real body, a Y-band test read a Neolithe top's neck ring and
    /// its whole 840-triangle shoulder region as covered by a neighbouring torso.
    /// </summary>
    [Fact]
    public void A_ring_a_neighbour_merely_shares_a_height_with_is_kept()
    {
        var profile = Profile((0u, Row(100, 0f, 0f)), (0u, Row(4, 0f, 0f)));
        var sameHeightElsewhere = new SecondSkinWriter.ConnectorProfile.Box(
            500f, -1000f, -1000f, 600f, 1000f, 1000f);

        Assert.Empty(Plan(profile, [sameHeightElsewhere]));
        Assert.Equal([(0, 1)], Plan(profile, [Everything]));
    }

    private static readonly SecondSkinWriter.ConnectorProfile.Box Everything =
        new(-1000f, -1000f, -1000f, 1000f, 1000f, 1000f);

    /// <summary>
    /// A triangle whose corners are absurdly far apart must not be allowed to size a loop.
    /// <para/>
    /// The cover grid buckets each triangle into every cell its bounding box touches, at 3 mm a cell. A
    /// corner at 1e30 — which is what a misread vertex declaration produces, reading arbitrary bytes as a
    /// float — puts one end of that box past what a cell index can hold, and the span guard was computing
    /// <c>(x1 - x0 + 1)</c> in <c>int</c>: it wrapped to a negative number, sailed past the check meant to
    /// catch exactly this, and left the loop counting from 0 to <c>int.MaxValue</c> doing a dictionary
    /// insert each time. The composite thread never came back.
    /// <para/>
    /// Values, not cell indices, are what this is tested on, so the guard holds however the cell size is
    /// retuned. A test that hangs rather than fails is worth the boilerplate of a hand-built profile.
    /// </summary>
    [Theory]
    [InlineData(1e30f)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NaN)]
    public void A_triangle_with_an_absurd_corner_does_not_hang_the_pass(float far)
    {
        // Sub 0 is the cover and is kept first, so its triangles are what go into the grid. One of them
        // reaches from the origin to `far`; the other is ordinary, so the submesh still measures as the
        // largest and the pass carries on past it.
        float[] pos =
        [
            0f, 0f, 0f,   far, 0f, 0f,   0f, 1f, 0f,      // the absurd one
            0f, 0f, 0f,   1f, 0f, 0f,    0f, 1f, 0f,      // an ordinary one
            0f, 0f, 0f,   1f, 0f, 0f,    0f, 1f, 0f,      // sub 1, coincident with it
        ];
        ushort[] tris = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        ushort[] verts = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        var box = new SecondSkinWriter.ConnectorProfile.Box(0f, 0f, 0f, 1f, 1f, 0f);

        var profile = new SecondSkinWriter.ConnectorProfile
        {
            Meshes = [new SecondSkinWriter.ConnectorProfile.MeshProfile(0, 2, pos, tris, verts, 0, 2)],
            Subs =
            [
                new SecondSkinWriter.ConnectorProfile.Sub(0, 0, 0u, 2, box, 0, 6, 0, 2),
                new SecondSkinWriter.ConnectorProfile.Sub(0, 1, 0u, 1, box, 6, 3, 2, 1),
            ],
            PartBox = box,
            Vertices = 9,
        };

        // Returning at all is the assertion. Nothing is dropped either way: sub 1 is three vertices, well
        // under the floor the coincidence fraction needs to mean anything.
        Assert.Empty(Plan(profile));
    }
}
