using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The body retarget's geometry, against hand-built <see cref="ModelParts"/> at real body scale.
/// <para/>
/// Built by hand rather than through <see cref="SyntheticModel"/> on purpose: that builder's triangles are a metre
/// across, and every constant this feature has — a 40 mm near band, a 30 mm push probe, a 0.1 mm snap — is sized for a
/// character. A metre-wide triangle makes all of them degenerate and the tests would pass for the wrong reason.
/// </summary>
public class BodyRetargetTests
{
    private const string SkinMaterial = "/mt_c0201b0001_bibo.mtrl";
    private const string ClothMaterial = "/mt_c0201e6255_top_a.mtrl";

    // ── the two senses of "is this skin?", asserted next to each other ──────────────────────────────────
    //
    // The transfer and the push-out consult SecondSkinWriter.IsBodySkinMaterial with OPPOSITE senses, and the brush
    // has a third, opposite again to the transfer's. The three tests below sit together so that changing any one of
    // them is visibly a change to a set, not an isolated tweak.

    [Fact]
    public void Retarget_moves_the_garment_s_own_skin_mesh()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);

        // A garment whose body mesh is a verbatim copy of the source body's, which is how gear is usually built.
        var garment = Copy(source, SkinMaterial);

        var solved = Solve(garment, source, target);

        Assert.True(solved.Snapped > 0, "the garment's body mesh is a copy of the body, so it should snap");
        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            var landed = At(garment, v) + Delta(solved, v);
            var wanted = At(target, v);
            Assert.True(Vector3.Distance(landed, wanted) < 1e-5f,
                        $"vertex {v} landed at {landed}, wanted the target body's {wanted}");
        }
    }

    [Fact]
    public void The_brush_leaves_the_same_vertices_alone()
    {
        // The contrast case. A brush must never move skin; the retarget must always move it. Both guarantees are
        // real and they are opposites, so they are asserted in one place.
        var source = Cube(0.20f, SkinMaterial);
        var garment = Copy(source, SkinMaterial);

        var solve = new MeshVolumeSolve(garment);
        solve.Paint(new Vector3(0f, 0f, 0.20f), radius: 0.5f, strength: 0.05f,
                    toViewer: new Vector3(0f, 0f, 1f));

        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            var d = solve.DeltaAt(v);
            Assert.True(d.X == 0f && d.Y == 0f && d.Z == 0f, $"the brush moved skin vertex {v}");
        }
    }

    [Fact]
    public void Push_out_leaves_the_garment_s_skin_mesh_alone()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        var garment = Copy(source, SkinMaterial);

        var solved = Solve(garment, source, target);

        // Every vertex is snapped and therefore excluded from the push-out. Bit-identical, not "close": the point is
        // that the push-out did not touch them at all, and an epsilon would hide a small nudge.
        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            var expected = At(target, v) - At(source, v);
            var actual = Delta(solved, v);
            Assert.Equal(expected.X, actual.X);
            Assert.Equal(expected.Y, actual.Y);
            Assert.Equal(expected.Z, actual.Z);
        }
        Assert.Equal(0, solved.Pushed);
    }

    [Fact]
    public void Push_out_leaves_an_unsnapped_skin_vertex_alone()
    {
        // The test above proves the SNAP exclusion; this one proves the SKIN exclusion, which is a different rule and
        // was silently uncovered while the two overlapped. Here the garment's body mesh was sculpted rather than
        // copied, so nothing snaps — only membership of the cloth set keeps the push-out off it.
        var body = Cube(0.20f, SkinMaterial);
        var garment = Patch(SkinMaterial, new Vector3(0f, 0f, 0.19f));

        var solved = Solve(garment, body, body);

        Assert.Equal(0, solved.Snapped);
        Assert.Equal(0, solved.Pushed);
        for (int v = 0; v < garment.Positions.Length / 3; v++)
            Assert.Equal(0f, Delta(solved, v).Length(), 6);
    }

    [Fact]
    public void An_unsnapped_cloth_patch_in_the_same_place_is_pushed()
    {
        // The control for the test above: identical geometry, cloth material. If this did not move, the previous test
        // would be proving nothing about the skin rule.
        var body = Cube(0.20f, SkinMaterial);
        var garment = Patch(ClothMaterial, new Vector3(0f, 0f, 0.19f));

        var solved = Solve(garment, body, body);

        Assert.True(solved.Pushed > 0, "cloth inside the body should have been pushed out");
    }

    [Fact]
    public void Cloth_inside_the_target_body_is_pushed_out()
    {
        // Source and target identical, so the transfer moves nothing and the push-out is the only pass with an
        // opinion. One cloth vertex sits 10 mm inside the +Z face.
        var body = Cube(0.20f, SkinMaterial);
        var garment = Points(ClothMaterial, new Vector3(0f, 0f, 0.19f));

        var solved = Solve(garment, body, body);

        Assert.Equal(1, solved.Pushed);
        var landed = At(garment, 0) + Delta(solved, 0);
        Assert.True(landed.Z > 0.20f, $"the vertex stayed inside the body at z={landed.Z}");
        Assert.True(landed.Z < 0.20f + 0.002f, $"the vertex overshot to z={landed.Z}");
    }

    [Fact]
    public void Snapped_cloth_vertices_are_not_pushed()
    {
        // A CLOTH vertex that happens to sit exactly on a body vertex is snapped, and a snapped node is excluded
        // from the push-out by set membership — never by a distance test. This target body has its +Z face centre
        // dimpled inward, so the snapped vertex lands well INSIDE it: the winding number says "inside" with no
        // ambiguity at all, and only the membership exclusion can keep it still.
        var source = Cube(0.20f, SkinMaterial);
        var target = Dimple(Cube(0.20f, SkinMaterial), new Vector3(0f, 0f, 0.20f), new Vector3(0f, 0f, 0.10f));

        int centre = IndexOf(source, new Vector3(0f, 0f, 0.20f));
        Assert.True(centre >= 0, "the source body should have a vertex at the centre of its +Z face");

        var garment = Points(ClothMaterial, new Vector3(0f, 0f, 0.20f));
        var solved = Solve(garment, source, target);

        Assert.Equal(1, solved.Snapped);
        Assert.Equal(0, solved.Pushed);

        var landed = At(garment, 0) + Delta(solved, 0);
        Assert.True(Vector3.Distance(landed, new Vector3(0f, 0f, 0.10f)) < 1e-6f,
                    $"the snapped vertex should sit on the dimpled body vertex, not be pushed off it; it is at {landed}");
    }

    // ── the transfer ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cloth_resting_on_the_body_keeps_its_standoff()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);

        // 10 mm off the +Z face, well inside the near band, so it should follow the body exactly.
        var garment = Points(ClothMaterial, new Vector3(0f, 0f, 0.21f));
        var solved = Solve(garment, source, target);

        var landed = At(garment, 0) + Delta(solved, 0);
        Assert.True(MathF.Abs(landed.Z - 0.26f) < 1e-4f,
                    $"the vertex should have kept its 10 mm standoff over the grown body; it is at z={landed.Z}");
    }

    [Fact]
    public void Cloth_hanging_free_does_not_move()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);

        // Past the far band from any part of the body.
        var garment = Points(ClothMaterial, new Vector3(0f, 0f, 0.20f + BodyRetarget.FarBand + 0.05f));
        var solved = Solve(garment, source, target);

        var d = Delta(solved, 0);
        Assert.Equal(0f, d.Length(), 6);
    }

    [Fact]
    public void The_falloff_has_no_step()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);

        // A column of vertices marching away from the +Z face, each its own node.
        const int count = 60;
        const float spacing = 0.005f;
        var column = new Vector3[count];
        for (int i = 0; i < count; i++) column[i] = new Vector3(0f, 0f, 0.20f + 0.001f + i * spacing);

        var garment = Points(ClothMaterial, column);
        var solved = Solve(garment, source, target);

        float previous = float.MaxValue;
        float worstJump = 0f;
        for (int i = 0; i < count; i++)
        {
            float here = Delta(solved, i).Length();
            Assert.True(here <= previous + 1e-6f, $"the field grew with distance at sample {i}");
            if (i > 0) worstJump = MathF.Max(worstJump, MathF.Abs(previous - here));
            previous = here;
        }

        // The steepest a smoothstep over the band can be is 1.5 * range / width; anything near the full displacement
        // would be the crease a hard cutoff leaves.
        float ceiling = 1.5f * 0.05f * spacing / (BodyRetarget.FarBand - BodyRetarget.NearBand) * 1.2f;
        Assert.True(worstJump <= ceiling, $"a step of {worstJump:F6} between neighbours exceeds {ceiling:F6}");
        Assert.Equal(0f, previous, 6);
    }

    [Fact]
    public void The_snap_is_decided_per_node_across_a_uv_seam()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);

        // The same point twice, as a uv seam stores it: two vertices, one node.
        var garment = Points(SkinMaterial, new Vector3(0f, 0f, 0.20f), new Vector3(0f, 0f, 0.20f));
        var solved = Solve(garment, source, target);

        var a = Delta(solved, 0);
        var b = Delta(solved, 1);
        Assert.Equal(a.X, b.X);
        Assert.Equal(a.Y, b.Y);
        Assert.Equal(a.Z, b.Z);
    }

    [Fact]
    public void The_push_moves_along_the_body_normal()
    {
        // Aiming along (p - landing) instead would drive an interior point further in; this fails outright then.
        var body = Cube(0.20f, SkinMaterial);
        var garment = Points(ClothMaterial, new Vector3(0.05f, 0.05f, 0.185f));

        var solved = Solve(garment, body, body);

        var d = Delta(solved, 0);
        Assert.True(d.Z > 0f, $"the push went inward: {d}");
    }

    // ── the topology guard ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_guard_accepts_the_same_topology_at_different_positions()
    {
        var a = Cube(0.20f, SkinMaterial);
        var b = Cube(0.25f, SkinMaterial);

        Assert.True(IdentityCorrespondence.TryBuild(a, b, "chest", out var built, out string refusal), refusal);
        Assert.NotNull(built);
        Assert.Equal(a.Positions.Length / 3, built!.Field.Count);
    }

    [Fact]
    public void The_guard_refuses_a_different_vertex_count()
    {
        var a = Cube(0.20f, SkinMaterial);
        var b = Points(SkinMaterial, new Vector3(0f, 0f, 0f));

        Assert.False(IdentityCorrespondence.TryBuild(a, b, "chest", out _, out string refusal));
        Assert.Contains("vertices", refusal);
    }

    [Fact]
    public void The_guard_accepts_a_pair_that_indexes_its_triangles_differently()
    {
        // Real size pairs do exactly this: one or two submeshes of every Neolithe family reference a different set of
        // vertex indices for the same triangle count, because each size was exported separately. Demanding identical
        // triangles refused every real pair, so the guard must not.
        var a = Cube(0.20f, SkinMaterial);
        var b = Cube(0.25f, SkinMaterial);

        var tris = (int[])b.Parts[0].Triangles.Clone();
        (tris[0], tris[1]) = (tris[1], tris[0]);
        var rewired = Replace(b, tris);

        var uv = Uv(a);
        Assert.True(IdentityCorrespondence.TryBuild(a, rewired, "chest", out _, out string refusal, uv, uv), refusal);
    }

    [Fact]
    public void The_guard_refuses_a_mesh_that_numbers_its_vertices_differently()
    {
        // Same vertex count, same submesh layout, but the uvs disagree — so vertex i of one is not vertex i of the
        // other, and a per-index displacement field would scramble the mesh. This is the check that replaced
        // comparing index buffers.
        var a = Cube(0.20f, SkinMaterial);
        var b = Cube(0.25f, SkinMaterial);

        var uvA = Uv(a);
        var uvB = Uv(a);
        for (int i = 0; i < uvB.Length; i += 2) uvB[i] += 0.25f;

        Assert.False(IdentityCorrespondence.TryBuild(a, b, "chest", out _, out string refusal, uvA, uvB));
        Assert.Contains("number their vertices the same way", refusal);
    }

    [Fact]
    public void The_guard_refuses_when_the_uvs_cannot_be_read()
    {
        var a = Cube(0.20f, SkinMaterial);
        var b = Cube(0.25f, SkinMaterial);

        Assert.False(IdentityCorrespondence.TryBuild(a, b, "chest", out _, out string refusal, Uv(a), []));
        Assert.Contains("could not be read", refusal);
    }

    [Fact]
    public void The_guard_ignores_a_different_island_split()
    {
        // ModelPartReader welds islands from POSITIONS, so two sizes of one body can legitimately split into
        // different island counts where a gap closes as the body grows. The guard must not look at them.
        var a = Cube(0.20f, SkinMaterial);
        var b = Cube(0.25f, SkinMaterial);

        var withIslands = WithIslandParts(b, 3);

        Assert.True(IdentityCorrespondence.TryBuild(a, withIslands, "chest", out _, out string refusal), refusal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static BodyRetarget.Solved Solve(ModelParts garment, ModelParts source, ModelParts target)
    {
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out string refusal),
                    refusal);
        return BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top", built!, target)]);
    }

    /// <summary>A distinct uv per vertex, so the guard's numbering proof has something real to compare.</summary>
    private static float[] Uv(ModelParts m)
    {
        int vc = m.Positions.Length / 3;
        var uv = new float[vc * 2];
        for (int i = 0; i < vc; i++)
        {
            uv[i * 2] = i * 0.001f;
            uv[i * 2 + 1] = 1f - i * 0.001f;
        }
        return uv;
    }

    private static Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static Vector3 Delta(BodyRetarget.Solved solved, int v)
    {
        var d = solved.Edit.DeltaAt(v);
        return new Vector3(d.X, d.Y, d.Z);
    }

    private static int IndexOf(ModelParts m, Vector3 p)
    {
        for (int v = 0; v < m.Positions.Length / 3; v++)
            if (Vector3.Distance(At(m, v), p) < 1e-6f) return v;
        return -1;
    }

    /// <summary>
    /// A closed cube of half-extent <paramref name="half"/>, each face a 2x2 grid of quads so that faces have a centre
    /// vertex as well as corners. Closed, because the push-out's winding number needs a solid to be inside of.
    /// </summary>
    private static ModelParts Cube(float half, string material)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var tris = new List<int>();

        Span<Vector3> axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        for (int a = 0; a < 3; a++)
        for (int sign = -1; sign <= 1; sign += 2)
        {
            var n = axes[a] * sign;
            var u = axes[(a + 1) % 3];
            var v = axes[(a + 2) % 3];
            // Wind the face so its normal points out on both sides of the cube.
            if (sign < 0) (u, v) = (v, u);

            int baseVertex = pos.Count / 3;
            for (int iu = 0; iu <= 2; iu++)
            for (int iv = 0; iv <= 2; iv++)
            {
                var p = n * half + u * ((iu - 1) * half) + v * ((iv - 1) * half);
                pos.Add(p.X); pos.Add(p.Y); pos.Add(p.Z);
                nrm.Add(n.X); nrm.Add(n.Y); nrm.Add(n.Z);
            }

            for (int iu = 0; iu < 2; iu++)
            for (int iv = 0; iv < 2; iv++)
            {
                int p00 = baseVertex + iu * 3 + iv;
                int p10 = baseVertex + (iu + 1) * 3 + iv;
                int p01 = baseVertex + iu * 3 + iv + 1;
                int p11 = baseVertex + (iu + 1) * 3 + iv + 1;
                tris.AddRange([p00, p10, p11]);
                tris.AddRange([p00, p11, p01]);
            }
        }

        return Build(pos.ToArray(), nrm.ToArray(), tris.ToArray(), material);
    }

    /// <summary>The same model with one vertex moved — a target body that is not simply a scaled source.</summary>
    private static ModelParts Dimple(ModelParts m, Vector3 from, Vector3 to)
    {
        var pos = (float[])m.Positions.Clone();
        for (int v = 0; v < pos.Length / 3; v++)
        {
            if (Vector3.Distance(new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]), from) > 1e-6f) continue;
            pos[v * 3] = to.X;
            pos[v * 3 + 1] = to.Y;
            pos[v * 3 + 2] = to.Z;
        }
        return Build(pos, m.Normals, m.Parts[0].Triangles, m.Parts[0].Material);
    }

    /// <summary>A copy of <paramref name="m"/>'s geometry drawn with another material.</summary>
    private static ModelParts Copy(ModelParts m, string material)
        => Build((float[])m.Positions.Clone(), (float[])m.Normals.Clone(),
                 (int[])m.Parts[0].Triangles.Clone(), material);

    /// <summary>
    /// A small triangle centred on <paramref name="centre"/>, facing +Z.
    /// <para/>
    /// Needed wherever a test cares which SET a node lands in, because skin membership is read off a part's triangles
    /// — exactly as <see cref="MeshVolumeSolve"/> reads it — so a part with no faces contributes no skin nodes and
    /// silently behaves like cloth.
    /// </summary>
    private static ModelParts Patch(string material, Vector3 centre)
    {
        const float r = 0.002f;
        var a = centre + new Vector3(-r, -r, 0f);
        var b = centre + new Vector3(r, -r, 0f);
        var c = centre + new Vector3(0f, r, 0f);
        float[] pos = [a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1];
        return Build(pos, nrm, [0, 1, 2], material);
    }

    /// <summary>Loose vertices with no triangles: the transfer works per node and does not need faces.</summary>
    private static ModelParts Points(string material, params Vector3[] points)
    {
        var pos = new float[points.Length * 3];
        var nrm = new float[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            pos[i * 3] = points[i].X;
            pos[i * 3 + 1] = points[i].Y;
            pos[i * 3 + 2] = points[i].Z;
            nrm[i * 3 + 2] = 1f;
        }
        return Build(pos, nrm, [], material);
    }

    private static ModelParts Replace(ModelParts m, int[] triangles)
        => Build(m.Positions, m.Normals, triangles, m.Parts[0].Material);

    /// <summary>The same model with extra ISLAND parts bolted on, which the guard must ignore.</summary>
    private static ModelParts WithIslandParts(ModelParts m, int islands)
    {
        var parts = new List<ModelPart>(m.Parts);
        var whole = m.Parts[0];
        for (int i = 0; i < islands; i++)
            parts.Add(new ModelPart
            {
                Mesh = whole.Mesh,
                Submesh = whole.Submesh,
                Island = i,
                Label = $"{whole.Label}{(char)('b' + i)}",
                Material = whole.Material,
                Triangles = whole.Triangles.Take(3).ToArray(),
                Ordinals = [0],
                AttributeMask = 0,
                Min = whole.Min,
                Max = whole.Max,
                Toggleable = true,
            });

        return new ModelParts
        {
            Positions = m.Positions,
            Normals = m.Normals,
            MeshSpans = m.MeshSpans,
            Parts = parts,
            AttributeNames = m.AttributeNames,
            Min = m.Min,
            Max = m.Max,
            ShatteredSubmeshes = m.ShatteredSubmeshes,
        };
    }

    private static ModelParts Build(float[] pos, float[] nrm, int[] tris, string material)
    {
        int vc = pos.Length / 3;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int v = 0; v < vc; v++)
        {
            var p = new Vector3(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (vc == 0) { min = Vector3.Zero; max = Vector3.Zero; }

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
}
