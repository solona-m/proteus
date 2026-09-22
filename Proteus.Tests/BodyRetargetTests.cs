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

    // ── swapping the garment's skin for the body's ──────────────────────────────────────────────────────

    /// <summary>Triangles a model draws, per material, read back through the reader the Studio uses.</summary>
    private static Dictionary<string, int> Drawn(byte[] mdl)
    {
        var drawn = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island < 0))
            drawn[part.Material.TrimStart('/')] = drawn.GetValueOrDefault(part.Material.TrimStart('/'))
                                                + part.Triangles.Length / 3;
        return drawn;
    }

    /// <summary>A resized slot whose new body is <paramref name="body"/>. The swap reads only the target and its file.</summary>
    private static BodyRetarget.SlotPair Resized(string slot, byte[] body)
    {
        var target = ModelPartReader.Read(body)!;
        Assert.True(IdentityCorrespondence.TryBuild(target, target, slot, out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, target, body);
    }

    private const string HipSkin = "/mt_c0201b0001_b.mtrl";

    [Fact]
    public void The_skin_swap_replaces_the_resized_slot_s_skin_mesh_with_the_body_s_whole()
    {
        // The garment's chest skin mesh, all of it on the chest: it goes whole, and the body's own skin takes its
        // place. A mesh is never cut into — it is swapped entire or kept entire.
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 2),
                                                  new SyntheticModel.Sub(0, TrianglesPerIsland: 1)),
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        // The chest's new body skin, denser than the garment's copy.
        var chest = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));

        var rebuilt = BodyRetarget.SwapSkin(garment, [Resized("_top", chest)], out var report);

        Assert.NotNull(rebuilt);
        Assert.Equal(3, report.Removed);
        Assert.Equal(5, report.Added);
        Assert.Equal(0, report.Kept);

        // The cloth untouched; the only skin drawn is the body's five triangles.
        var drawn = Drawn(rebuilt!);
        Assert.Equal(4, drawn[ClothMaterial.TrimStart('/')]);
        Assert.Equal(5, drawn.Where(d => SecondSkinWriter.IsBodySkinMaterial("/" + d.Key)).Sum(d => d.Value));
    }

    [Fact]
    public void A_skin_mesh_only_partly_on_the_body_is_kept_whole()
    {
        // A heeled shoe draws its own foot, turned onto the toe, in the same mesh as the lower leg: the leg sits on the
        // body and the foot does not. Swapping by majority handed the whole mesh to the body — whose skin has no foot
        // — and the foot vanished. Part on, part off: the author's mesh stays, all of it.
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 2),
                                                  new SyntheticModel.Sub(0, OffsetZ: 5f)),
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        var chest = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));

        Assert.Null(BodyRetarget.SwapSkin(garment, [Resized("_top", chest)], out var report));
        Assert.Equal(0, report.Removed);
        Assert.Equal(1, report.Kept);
        Assert.Equal(1, report.Posed);
    }

    [Fact]
    public void Skin_of_a_slot_nobody_is_resizing_stays_as_the_author_left_it()
    {
        // A long top: chest skin, and hip skin that belongs to the legs. Only the chest is being resized.
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)),
            new SyntheticModel.Mesh(HipSkin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetY: 50f)),
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        var chest = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));

        var rebuilt = BodyRetarget.SwapSkin(garment, [Resized("_top", chest)], out var report);

        Assert.NotNull(rebuilt);
        Assert.Equal(2, report.Removed);
        Assert.Equal(1, report.Kept);
        var drawn = Drawn(rebuilt!);
        Assert.Equal(3, drawn[HipSkin.TrimStart('/')]);                       // the hips, exactly as they were
        Assert.Equal(5, drawn[SkinMaterial.TrimStart('/')]);                  // the chest, the body's
    }

    [Fact]
    public void Each_resized_slot_takes_its_own_body_s_skin()
    {
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)),
            new SyntheticModel.Mesh(HipSkin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetY: 50f)),
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        var chest = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));
        var legs = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 7, OffsetY: 50f)));

        var rebuilt = BodyRetarget.SwapSkin(garment, [Resized("_top", chest), Resized("_dwn", legs)], out var report);

        Assert.NotNull(rebuilt);
        Assert.Equal(2 + 3, report.Removed);
        Assert.Equal(5 + 7, report.Added);
        var drawn = Drawn(rebuilt!);
        Assert.False(drawn.ContainsKey(HipSkin.TrimStart('/')));
        Assert.Equal(12, drawn.Where(d => SecondSkinWriter.IsBodySkinMaterial("/" + d.Key)).Sum(d => d.Value));
    }

    [Fact]
    public void The_swapped_skin_keeps_the_tags_other_gear_hides_it_by_and_drops_variant_tags()
    {
        // atr_ude is how long gloves hide the arm skin under them; atr_tv_a is a Neolithe body's own variant toggle,
        // which the garment's IMC mask was never written for.
        var garment = SyntheticModel.Build(["atr_ude"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0b1, TrianglesPerIsland: 2)),
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        var chest = SyntheticModel.Build(["atr_ude", "atr_tv_a"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0b11, TrianglesPerIsland: 5)));

        var rebuilt = BodyRetarget.SwapSkin(garment, [Resized("_top", chest)], out _);

        var model = ModelPartReader.Read(rebuilt!)!;
        var skin = model.Parts.Single(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material));
        var tags = Enumerable.Range(0, model.AttributeNames.Count)
                             .Where(bit => (skin.AttributeMask & (1u << bit)) != 0)
                             .Select(bit => model.AttributeNames[bit]).ToList();
        Assert.Equal(["atr_ude"], tags);
        Assert.DoesNotContain("atr_tv_a", model.AttributeNames);
    }

    [Fact]
    public void Nothing_is_rebuilt_when_no_skin_mesh_belongs_to_a_resized_slot()
    {
        var clothOnly = SyntheticModel.Build([],
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)));
        var hipsOnly = SyntheticModel.Build([],
            new SyntheticModel.Mesh(HipSkin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 50f)));
        var chest = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)));

        Assert.Null(BodyRetarget.SwapSkin(clothOnly, [Resized("_top", chest)], out _));
        Assert.Null(BodyRetarget.SwapSkin(hipsOnly, [Resized("_top", chest)], out _));
    }

    // ── which mods are bodies ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("chara/equipment/e0000/model/c0201e0000_top.mdl", true)]
    [InlineData("chara/equipment/e0000/model/c0101e0000_dwn.mdl", true)]
    [InlineData("Chara/Equipment/e0000/Model/c1401e0000_sho.mdl", true)]
    [InlineData("chara/equipment/e0279/model/c0201e0279_top.mdl", false)]   // the same files again: sizes listed twice
    [InlineData("chara/equipment/e0194/model/c0201e0194_top.mdl", false)]   // an outfit with a size group of its own
    [InlineData("chara/equipment/e0000/model/c0201e0000_met.mdl", false)]
    [InlineData("chara/equipment/e0000/texture/c0201e0000_top_n.tex", false)]
    public void Only_the_smallclothes_model_makes_a_mod_a_body(string gamePath, bool body)
        => Assert.Equal(body, BodySizeCatalog.IsBodyModel(gamePath));

    // ── the two senses of "is this skin?", asserted next to each other ──────────────────────────────────
    //
    // The transfer and the push-out consult SecondSkinWriter.IsBodySkinMaterial with OPPOSITE senses, and the brush
    // has a third, opposite again to the transfer's. The three tests below sit together so that changing any one of
    // them is visibly a change to a set, not an isolated tweak.

    [Fact]
    public void The_refit_never_touches_the_author_s_normals()
    {
        // Recomputing normals from the moved surface — relaxed, averaged over welded seams, blended toward the body's
        // under laid skin — blotched a shirt sleeve dark and light. Every normal comes back zero, which Inflate reads
        // as "leave this vertex's normal as the file has it".
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        var garment = Copy(source, ClothMaterial);
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out string why), why);

        foreach (bool lay in new[] { false, true })
        {
            var solved = BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top", built!, target)], replaceSkin: lay);
            Assert.True(solved.WorstMove > 0f, "the garment should have moved");
            for (int v = 0; v < garment.Positions.Length / 3; v++)
            {
                var n = solved.Edit.NormalAt(v);
                Assert.True(n.X == 0f && n.Y == 0f && n.Z == 0f, $"vertex {v} was given a normal ({n.X}, {n.Y}, {n.Z})");
            }
        }
    }

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

    // ── the push-out: only clips the refit CREATED ─────────────────────────────────────────────────────
    //
    // The scenario these share: two body slots. A holds still; B grows. The patch sits in the gap between them, 30 mm
    // off A and 70 mm off B, so as authored it is outside everything and its nearest body is A — which does not move,
    // so the transfer leaves the patch where it is. B's growth then swallows it, 10 mm deep. That is a clip the refit
    // created, which is the only kind the push-out may undo.

    [Fact]
    public void Cloth_the_refit_drives_into_a_body_is_pushed_out()
    {
        var (garment, pairs) = CreatedClip(ClothMaterial);

        var solved = BodyRetarget.Solve(garment, pairs);

        Assert.True(solved.Pushed > 0, "the refit swallowed this cloth, so the push-out should have freed it");

        // The deepest corner is 12.5 mm in. The slope-limited spread lifts the shallower corners towards that, so the
        // whole patch moves together rather than crumpling — which is the spread's job — but nothing may be flung
        // further than the worst clip needed.
        const float deepest = 0.0125f;
        foreach (int v in Enumerable.Range(0, garment.Positions.Length / 3))
        {
            var start = At(garment, v);
            var landed = start + Delta(solved, v);
            Assert.True(landed.X < GrownFace, $"vertex {v} is still inside the grown body at x={landed.X}");
            Assert.True(start.X - landed.X <= deepest + BodyRetarget.Clearance + 1e-5f,
                        $"vertex {v} was pushed {(start.X - landed.X) * 1000f:F2} mm, more than the worst clip needed");
        }
    }

    [Fact]
    public void The_push_moves_along_the_body_normal()
    {
        // B's face that swallowed the patch faces -X, so the patch must move towards -X. Aiming along
        // (p - landing) instead would drive an interior point further in, and this fails outright then.
        var (garment, pairs) = CreatedClip(ClothMaterial);

        var solved = BodyRetarget.Solve(garment, pairs);

        var d = Delta(solved, 0);
        Assert.True(d.X < 0f, $"the push went the wrong way: {d}");
    }

    [Fact]
    public void Push_out_leaves_an_unsnapped_skin_vertex_alone()
    {
        // The SKIN exclusion, which is a different rule from the snap one. The garment's own body mesh is itself one of
        // the drawn surfaces, so a skin node always reads as ON the skin and is never pushed for its own sake — but the
        // slope-limited spread lifts every node towards its neighbours' push, so body mesh beside a genuine cloth clip
        // would be peeled off the body it must coincide with. Only membership of the cloth set stops that. This skin
        // is sculpted rather than copied, 10 mm off any body vertex, so the snap cannot be what protects it.
        var (_, pairs) = CreatedClip(ClothMaterial);

        // Cloth: a clip patch in the gap (0-2), joined by an edge to vertex 3. Skin: a small patch (3-5) tucked 10 mm
        // inside body A, far enough from the clip that the clip's own before/after reading is unaffected by it.
        var pos = new List<float>
        {
            0.228f, -0.002f, 0f,
            0.232f, -0.002f, 0f,
            0.230f,  0.002f, 0f,
            0.190f,  0.001f, 0f,
            0.190f,  0.003f, 0f,
            0.192f,  0.002f, 0f,
        };
        var filler = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            int b = pos.Count / 3;
            float x = 5f + i * 0.01f;
            pos.AddRange([x, 5f, 5f, x + 0.001f, 5f, 5f, x, 5.001f, 5f]);
            filler.AddRange([b, b + 1, b + 2]);
        }
        var nrm = new float[pos.Count];
        for (int i = 0; i < nrm.Length; i += 3) nrm[i + 2] = 1f;

        int[] cloth = [0, 1, 2, 0, 2, 3, .. filler];
        int[] skin = [3, 4, 5];
        var garment = BuildTwoPart(pos.ToArray(), nrm, cloth, ClothMaterial, skin, SkinMaterial);

        var solved = BodyRetarget.Solve(garment, pairs);

        Assert.True(Delta(solved, 0).Length() > 0f, "the clip beside it should have been pushed, or this proves nothing");
        for (int v = 3; v <= 5; v++)
            Assert.Equal(0f, Delta(solved, v).Length(), 6);
    }

    [Fact]
    public void Cloth_the_author_put_inside_the_skin_is_left_there()
    {
        // The third exclusion, and the one that decides whether the push-out helps at all. Authors leave the whole
        // body mesh under the fabric where it is hidden, so cloth behind a skin surface is usually AUTHORED. Measured on
        // a real outfit, pushing it made the refit 30% worse against the author's own hand-fitted size. Here source
        // and target are the same body, so nothing about the refit put this cloth inside — the author did.
        var body = Cube(0.20f, SkinMaterial);
        var garment = Patch(ClothMaterial, new Vector3(0f, 0f, 0.19f));

        var solved = Solve(garment, body, body);

        Assert.Equal(0, solved.Pushed);
        for (int v = 0; v < garment.Positions.Length / 3; v++)
            Assert.Equal(0f, Delta(solved, v).Length(), 6);
    }

    [Fact]
    public void Hidden_cloth_beside_a_fresh_clip_is_not_dragged_out()
    {
        // What the explicit authored-inside exclusion does that the push amount alone does not. A node is only ever
        // pushed back to its OWN authored standoff, and a hidden node's is negative, so on its own it never moves — but
        // the slope-limited spread lifts every node towards its neighbours' push. Hidden cloth sitting beside a genuine
        // clip would be dragged out with it, which is the thing measured to hurt. Only set membership stops that.
        var (_, pairs) = CreatedClip(ClothMaterial);

        // A clip patch in the gap (vertices 0-2), joined by an edge to vertex 3, which the author tucked 2 mm inside body
        // A's +X face. Tiny far-off triangles keep the mesh's mean edge short, so the spread reaches vertex 3 in one hop.
        var pos = new List<float>
        {
            0.228f, -0.002f, 0f,
            0.232f, -0.002f, 0f,
            0.230f,  0.002f, 0f,
            0.198f,  0f,     0f,
        };
        var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
        for (int i = 0; i < 40; i++)
        {
            int b = pos.Count / 3;
            float x = 5f + i * 0.01f;
            pos.AddRange([x, 5f, 5f, x + 0.001f, 5f, 5f, x, 5.001f, 5f]);
            tris.AddRange([b, b + 1, b + 2]);
        }
        var nrm = new float[pos.Count];
        for (int i = 0; i < nrm.Length; i += 3) nrm[i + 2] = 1f;
        var garment = Build(pos.ToArray(), nrm, tris.ToArray(), ClothMaterial);

        var solved = BodyRetarget.Solve(garment, pairs);

        Assert.True(Delta(solved, 0).Length() > 0f, "the clip beside it should have been pushed, or this proves nothing");
        Assert.Equal(0f, Delta(solved, 3).Length(), 6);
    }

    [Fact]
    public void Snapped_cloth_vertices_are_not_pushed()
    {
        // A CLOTH vertex sitting exactly on a body vertex is snapped, and lands exactly on that vertex of the new body.
        // The push amount alone would leave it there — it is on the surface, not in it — but the slope-limited spread
        // lifts every node towards its neighbours' push, so cloth lying on the skin beside a genuine clip would be
        // peeled off it. Only set membership stops that; the snap is also the one place float noise could put a node
        // 1e-8 m either side of a surface, where no inside test can be trusted.
        var (_, pairs) = CreatedClip(ClothMaterial);

        // A clip patch in the gap (vertices 0-2), joined by an edge to vertex 3, which sits exactly on the centre vertex
        // of body A's +X face. Tiny far-off triangles keep the mean edge short, so the spread reaches it in one hop.
        var pos = new List<float>
        {
            0.228f, -0.002f, 0f,
            0.232f, -0.002f, 0f,
            0.230f,  0.002f, 0f,
            0.200f,  0f,     0f,
        };
        var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
        for (int i = 0; i < 40; i++)
        {
            int b = pos.Count / 3;
            float x = 5f + i * 0.01f;
            pos.AddRange([x, 5f, 5f, x + 0.001f, 5f, 5f, x, 5.001f, 5f]);
            tris.AddRange([b, b + 1, b + 2]);
        }
        var nrm = new float[pos.Count];
        for (int i = 0; i < nrm.Length; i += 3) nrm[i + 2] = 1f;
        var garment = Build(pos.ToArray(), nrm, tris.ToArray(), ClothMaterial);

        var solved = BodyRetarget.Solve(garment, pairs);

        Assert.True(solved.Snapped >= 1, "vertex 3 sits on a body vertex, so it should have snapped");
        Assert.True(Delta(solved, 0).Length() > 0f, "the clip beside it should have been pushed, or this proves nothing");
        Assert.Equal(0f, Delta(solved, 3).Length(), 6);
    }

    // ── held parts: unticked in the Studio's list ───────────────────────────────────────────────────────

    [Fact]
    public void A_held_part_stays_exactly_where_the_author_put_it()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        var garment = Patch(ClothMaterial, new Vector3(0f, 0f, 0.21f));

        var free = Solve(garment, source, target);
        Assert.True(Delta(free, 0).Length() > 0.01f, "unheld, this patch follows the growing body");

        var held = SolveHeld(garment, source, target, [0, 1, 2]);
        Assert.Equal(3, held.Held);
        for (int v = 0; v < 3; v++) Assert.Equal(0f, Delta(held, v).Length());
    }

    [Fact]
    public void A_held_skin_part_is_held_too()
    {
        // The brush never moves skin, so it offers no skin lock; the refit does move skin, so holding it has to work.
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        var garment = Copy(source, SkinMaterial);

        var solved = SolveHeld(garment, source, target, Enumerable.Range(0, garment.Positions.Length / 3));

        for (int v = 0; v < garment.Positions.Length / 3; v++) Assert.Equal(0f, Delta(solved, v).Length());
    }

    [Fact]
    public void A_point_shared_by_a_held_part_and_a_moving_one_is_held()
    {
        // Held per welded node, as the brush holds: vertex 3 sits exactly on vertex 0 (a uv seam between two parts).
        // If one copy moved and the other did not, the surface would crack open along the join.
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        var garment = Points(ClothMaterial,
                             new Vector3(0f, 0f, 0.21f), new Vector3(0.01f, 0f, 0.21f), new Vector3(0f, 0.01f, 0.21f),
                             new Vector3(0f, 0f, 0.21f));

        var solved = SolveHeld(garment, source, target, [0]);

        Assert.Equal(0f, Delta(solved, 0).Length());
        Assert.Equal(0f, Delta(solved, 3).Length());
        Assert.True(Delta(solved, 1).Length() > 0.01f, "the part that is not held still follows the body");
    }

    [Fact]
    public void A_held_part_is_not_dragged_by_a_neighbouring_push()
    {
        // The push-out spreads each push to its neighbours so it has no step; a held part beside a genuine clip must
        // not be lifted by that spread. The same scenario as the snapped and hidden-cloth tests, with vertex 3 held.
        var (_, pairs) = CreatedClip(ClothMaterial);
        var pos = new List<float>
        {
            0.228f, -0.002f, 0f,
            0.232f, -0.002f, 0f,
            0.230f,  0.002f, 0f,
            0.205f,  0f,     0f,
        };
        var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
        for (int i = 0; i < 40; i++)
        {
            int b = pos.Count / 3;
            float x = 5f + i * 0.01f;
            pos.AddRange([x, 5f, 5f, x + 0.001f, 5f, 5f, x, 5.001f, 5f]);
            tris.AddRange([b, b + 1, b + 2]);
        }
        var nrm = new float[pos.Count];
        for (int i = 0; i < nrm.Length; i += 3) nrm[i + 2] = 1f;
        var garment = Build(pos.ToArray(), nrm, tris.ToArray(), ClothMaterial);

        var solved = BodyRetarget.Solve(garment, pairs, held: new HashSet<int> { 3 });

        Assert.True(Delta(solved, 0).Length() > 0f, "the clip beside it should have been pushed, or this proves nothing");
        Assert.Equal(0f, Delta(solved, 3).Length());
    }

    // ── replacing the skin: laying the garment's body mesh onto the new body ───────────────────────────

    [Fact]
    public void Replacing_the_skin_lays_sculpted_skin_onto_the_new_body()
    {
        // The author sculpted this skin 10 mm under the body's surface (a top pressing the chest). Resizing keeps that;
        // replacing lays it exactly on the new body. Its normals are the author's either way — see
        // The_refit_never_touches_the_author_s_normals.
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        var garment = Patch(SkinMaterial, new Vector3(0.01f, 0.01f, 0.19f));
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out _));
        var pairs = new List<BodyRetarget.SlotPair> { new("_top", built!, target) };

        var resized = BodyRetarget.Solve(garment, pairs);
        var replaced = BodyRetarget.Solve(garment, pairs, replaceSkin: true);

        for (int v = 0; v < 3; v++)
        {
            float keptOffset = 0.25f - (At(garment, v) + Delta(resized, v)).Z;
            Assert.True(MathF.Abs(keptOffset - 0.01f) < 1e-4f, $"resizing should keep the 10 mm; it kept {keptOffset}");

            var laid = At(garment, v) + Delta(replaced, v);
            Assert.True(MathF.Abs(laid.Z - 0.25f) < 1e-5f, $"vertex {v} should sit on the new body's face, is at z={laid.Z}");
        }
        Assert.Equal(3, replaced.Laid);
    }

    [Fact]
    public void Replacing_the_skin_leaves_cloth_and_held_skin_alone()
    {
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out _));
        var pairs = new List<BodyRetarget.SlotPair> { new("_top", built!, target) };

        // Cloth: laying is for skin only; cloth keeps the transfer's answer either way.
        var cloth = Patch(ClothMaterial, new Vector3(0.01f, 0.01f, 0.21f));
        var a = BodyRetarget.Solve(cloth, pairs);
        var b = BodyRetarget.Solve(cloth, pairs, replaceSkin: true);
        for (int v = 0; v < 3; v++) Assert.Equal(Delta(a, v), Delta(b, v));
        Assert.Equal(0, b.Laid);

        // Held skin: the user asked for it to stay put, and laying must respect that like every other pass.
        var skin = Patch(SkinMaterial, new Vector3(0.01f, 0.01f, 0.19f));
        var held = BodyRetarget.Solve(skin, pairs, held: new HashSet<int> { 0, 1, 2 }, replaceSkin: true);
        for (int v = 0; v < 3; v++) Assert.Equal(0f, Delta(held, v).Length());
    }

    [Fact]
    public void Skin_far_from_the_body_is_not_laid_onto_it()
    {
        // More than LayReach off the body is not the body's surface: laying it would drag it across the gap.
        var source = Cube(0.20f, SkinMaterial);
        var target = Cube(0.25f, SkinMaterial);
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out _));
        var garment = Patch(SkinMaterial, new Vector3(0.01f, 0.01f, 0.20f + BodyRetarget.LayReach + 0.02f));

        var solved = BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top", built!, target)], replaceSkin: true);

        Assert.Equal(0, solved.Laid);
    }

    private static BodyRetarget.Solved SolveHeld(ModelParts garment, ModelParts source, ModelParts target,
                                                 IEnumerable<int> held)
    {
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out string refusal),
                    refusal);
        return BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top", built!, target)],
                                  held: new HashSet<int>(held));
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

    // ── by texture coordinate: bodies that are not the same mesh ─────────────────────────────────────────

    [Fact]
    public void A_renumbered_body_is_mapped_by_texture_coordinate()
    {
        // Same texture layout, same shape family, but every vertex renumbered — which is what a different mesh of the
        // same body looks like to the refit. Vertex for vertex is impossible; by uv, each point finds its twin exactly.
        var source = Cube(0.20f, SkinMaterial);
        var target = Scrambled(Cube(0.25f, SkinMaterial), out var targetUv);
        var sourceUv = CubeUv(source);

        Assert.False(IdentityCorrespondence.TryBuild(source, target, "chest", out _, out _, sourceUv, targetUv));
        Assert.True(BodyCorrespondence.TryBuild(source, sourceUv, target, targetUv, "chest", out var built,
                                                out string refusal), refusal);
        Assert.IsType<UvAtlasCorrespondence>(built);

        // The target is the source scaled by 1.25 about the origin, so every point should land at 1.25x itself.
        for (int v = 0; v < source.Positions.Length / 3; v++)
        {
            var p = At(source, v);
            Assert.True(built!.Field[v].HasValue, $"vertex {v} found no landing");
            var landed = p + built.Field[v]!.Value;
            Assert.True(Vector3.Distance(landed, p * 1.25f) < 1e-5f, $"vertex {v} landed at {landed}, wanted {p * 1.25f}");
        }
    }

    [Fact]
    public void A_refit_onto_a_renumbered_body_lands_the_skin_exactly()
    {
        // End to end through the solve: the garment's body mesh is a copy of the source, the target is a different mesh
        // of the grown body, and the skin must still land on it.
        var source = Cube(0.20f, SkinMaterial);
        var target = Scrambled(Cube(0.25f, SkinMaterial), out var targetUv);
        Assert.True(BodyCorrespondence.TryBuild(source, CubeUv(source), target, targetUv, "chest", out var built,
                                                out string refusal), refusal);

        var garment = Copy(source, SkinMaterial);
        var solved = BodyRetarget.Solve(garment, [new BodyRetarget.SlotPair("_top", built!, target)]);

        for (int v = 0; v < garment.Positions.Length / 3; v++)
        {
            var landed = At(garment, v) + Delta(solved, v);
            Assert.True(Vector3.Distance(landed, At(garment, v) * 1.25f) < 1e-5f,
                        $"vertex {v} landed at {landed}");
        }
    }

    [Fact]
    public void A_triangle_collapsed_in_uv_lands_its_own_vertex()
    {
        // Neolithe's neck opening is a ring of triangles with no area in uv: two corners share one coordinate. Vertex 3
        // here shares vertex 2's uv, so the uv names two places on the body, and triangle 1-3-2 is a line in the
        // texture. Treating it as "its first corner" once put a neck vertex 29 mm from home; it must land on itself.
        float[] pos =
        [
            0f,    0f,    0f,
            0.01f, 0f,    0f,
            0f,    0.01f, 0f,
            0.02f, 0.03f, 0f,
        ];
        float[] uv = [0f, 0f, 0.1f, 0f, 0f, 0.1f, 0f, 0.1f];
        float[] nrm = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        int[] tris = [0, 1, 2, 1, 3, 2];
        var source = Build(pos, nrm, tris, SkinMaterial);
        var target = Build(pos.Select(x => x * 1.1f).ToArray(), nrm, tris, SkinMaterial);

        Assert.True(UvAtlasCorrespondence.TryBuild(source, uv, target, uv, "chest", out var built, out string refusal),
                    refusal);
        for (int v = 0; v < 4; v++)
        {
            var landed = At(source, v) + built!.Field[v]!.Value;
            Assert.True(Vector3.Distance(landed, At(source, v) * 1.1f) < 1e-6f,
                        $"vertex {v} landed at {landed}, wanted {At(source, v) * 1.1f}");
        }
    }

    [Fact]
    public void Bodies_with_different_texture_layouts_are_refused()
    {
        // Nothing lines up by uv, so there is no way to say which point is which: the one case still refused.
        // Moved by part of a tile, not a whole one: a sheet repeats, so a whole-tile shift is the SAME layout and
        // pairs (see Two_bodies_a_whole_tile_apart_in_uv_still_pair).
        var source = Cube(0.20f, SkinMaterial);
        var target = Scrambled(Cube(0.25f, SkinMaterial), out var targetUv);
        for (int i = 0; i < targetUv.Length; i++) targetUv[i] += 0.37f;

        Assert.False(BodyCorrespondence.TryBuild(source, CubeUv(source), target, targetUv, "chest", out _,
                                                 out string refusal));
        Assert.Contains("texture layout", refusal);
    }

    [Fact]
    public void The_guard_accepts_a_variant_with_a_few_uvs_remapped()
    {
        // Neolithe's NSFW chests re-map about 1.2% of their uvs and are otherwise the same mesh vertex for vertex. That
        // must stay a vertex-for-vertex pair, not be refused and not drop to the approximate route.
        var a = Cube(0.20f, SkinMaterial);
        var b = Cube(0.25f, SkinMaterial);
        var uvA = Uv(a);
        var uvB = Uv(a);
        for (int i = 0; i < 2; i++) uvB[i] += 0.5f;   // one vertex of 54 re-mapped

        Assert.True(BodyCorrespondence.TryBuild(a, uvA, b, uvB, "chest", out var built, out string refusal), refusal);
        Assert.IsType<IdentityCorrespondence>(built);
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

    /// <summary>
    /// A real texture layout for <see cref="Cube"/>: each face gets its own cell of a 3x2 atlas, so no two faces
    /// overlap and a uv names exactly one point of the cube. Relies on Cube's vertex order (face, then 3x3 grid).
    /// </summary>
    private static float[] CubeUv(ModelParts cube)
    {
        int vc = cube.Positions.Length / 3;
        var uv = new float[vc * 2];
        for (int i = 0; i < vc; i++)
        {
            int face = i / 9, iu = i % 9 / 3, iv = i % 3;
            uv[i * 2] = (face % 3 + 0.05f + 0.45f * iu) / 3f;
            uv[i * 2 + 1] = (face / 3 + 0.05f + 0.45f * iv) / 2f;
        }
        return uv;
    }

    /// <summary>The same cube with every vertex renumbered (reversed), and its uvs carried along with them.</summary>
    private static ModelParts Scrambled(ModelParts cube, out float[] uv)
    {
        int vc = cube.Positions.Length / 3;
        var original = CubeUv(cube);
        var pos = new float[vc * 3];
        var nrm = new float[vc * 3];
        uv = new float[vc * 2];
        for (int n = 0; n < vc; n++)
        {
            int o = vc - 1 - n;   // new vertex n is old vertex o
            for (int k = 0; k < 3; k++)
            {
                pos[n * 3 + k] = cube.Positions[o * 3 + k];
                nrm[n * 3 + k] = cube.Normals[o * 3 + k];
            }
            uv[n * 2] = original[o * 2];
            uv[n * 2 + 1] = original[o * 2 + 1];
        }
        var tris = cube.Parts[0].Triangles.Select(o => vc - 1 - o).ToArray();
        return Build(pos, nrm, tris, cube.Parts[0].Material);
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

    /// <summary>Where the grown body B's -X face ends up in <see cref="CreatedClip"/>.</summary>
    private const float GrownFace = 0.22f;

    /// <summary>
    /// A patch that the refit genuinely drives into a body — see the comment above the push-out tests. Body A (a cube
    /// at the origin) holds still; body B (a small cube at x = 0.35) grows from half 0.05 to half 0.13, so its -X face
    /// sweeps from x = 0.30 to <see cref="GrownFace"/>. The patch sits at x = 0.23.
    /// </summary>
    private static (ModelParts Garment, List<BodyRetarget.SlotPair> Pairs) CreatedClip(string material)
    {
        var a = Cube(0.20f, SkinMaterial);
        var bFrom = Cube(0.05f, SkinMaterial, new Vector3(0.35f, 0f, 0f));
        var bTo = Cube(0.13f, SkinMaterial, new Vector3(0.35f, 0f, 0f));

        Assert.True(IdentityCorrespondence.TryBuild(a, a, "chest", out var still, out string r1), r1);
        Assert.True(IdentityCorrespondence.TryBuild(bFrom, bTo, "legs", out var grow, out string r2), r2);

        var garment = Patch(material, new Vector3(0.23f, 0f, 0f));
        return (garment, [new BodyRetarget.SlotPair("_top", still!, a), new BodyRetarget.SlotPair("_dwn", grow!, bTo)]);
    }

    private static ModelParts Cube(float half, string material, Vector3 centre)
    {
        var cube = Cube(half, material);
        var pos = (float[])cube.Positions.Clone();
        for (int v = 0; v < pos.Length / 3; v++)
        {
            pos[v * 3] += centre.X;
            pos[v * 3 + 1] += centre.Y;
            pos[v * 3 + 2] += centre.Z;
        }
        return Build(pos, cube.Normals, cube.Parts[0].Triangles, material);
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

    /// <summary>One vertex array, two submeshes with different materials — how a garment carries cloth beside skin.</summary>
    private static ModelParts BuildTwoPart(float[] pos, float[] nrm, int[] trisA, string materialA,
                                           int[] trisB, string materialB)
    {
        var a = Build(pos, nrm, trisA, materialA);
        var b = Build(pos, nrm, trisB, materialB);
        var partB = new ModelPart
        {
            Mesh = 0,
            Submesh = 1,
            Island = -1,
            Label = "0.1",
            Material = materialB,
            Triangles = trisB,
            Ordinals = b.Parts[0].Ordinals,
            AttributeMask = 0,
            Min = b.Min,
            Max = b.Max,
            Toggleable = true,
        };

        return new ModelParts
        {
            Positions = a.Positions,
            Normals = a.Normals,
            MeshSpans = a.MeshSpans,
            Parts = [a.Parts[0], partB],
            AttributeNames = [],
            Min = a.Min,
            Max = a.Max,
            ShatteredSubmeshes = new Dictionary<string, int>(),
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
