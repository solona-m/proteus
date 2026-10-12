using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Shoes and gloves keeping their own feet and hands in the automatic refit (<see cref="KeepEnds"/>): which vertices
/// are the end, what is read, what the solver holds and swaps, and what the watcher is told to do. Against hand-built
/// models; the same against the installed mods is <see cref="KeepEndsLocalTests"/>.
/// </summary>
public class KeepEndsTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e6070_sho_a.mtrl";

    // ── which vertices are the end ───────────────────────────────────────────────────────────────────────

    private static readonly (string, float)[] Foot = [("j_asi_d_l", 1f)];
    private static readonly (string, float)[] Calf = [("j_asi_c_l", 1f)];
    private static readonly (string, float)[] Hand = [("j_te_l", 1f)];
    private static readonly (string, float)[] Forearm = [("j_ude_b_l", 1f)];

    /// <summary>A skinned model, read the way the watcher reads one: its parts and its rig, in one vertex order.</summary>
    private static (ModelParts Parts, XivLiveMesh.SkinnedMesh Skin, byte[] Bytes) Skinned(params SyntheticModel.Sub[] subs)
    {
        var bytes = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, subs));
        return (ModelPartReader.Read(bytes)!, ModelSkinReader.Read(bytes, null, null)!, bytes);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-19")]
    public void A_vertex_mostly_on_foot_bones_is_the_foot_and_one_mostly_on_the_calf_is_not()
    {
        var (parts, skin, _) = Skinned(
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, Weights: Foot),                                     // 0..2
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 100f, Weights: Calf),                      // 3..5
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 200f, Weights: [("j_asi_c_l", 0.3f), ("j_asi_e_l", 0.7f)]),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 300f, Weights: [("j_asi_d_l", 0.3f), ("j_asi_c_l", 0.7f)]),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 400f, Weights: [("iv_asi_oya_a_l", 1f)])); // Rue's toes

        var ends = BodyRetarget.EndVertices(parts, skin, "_sho", [], out _);

        Assert.Equal(new[] { 0, 1, 2, 6, 7, 8, 12, 13, 14 }, ends.OrderBy(v => v));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-19")]
    public void A_gloves_end_is_the_hand_and_its_fingers()
    {
        var (parts, skin, _) = Skinned(
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, Weights: Hand),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 100f, Weights: Forearm),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 200f, Weights: [("j_hito_a_l", 1f)]),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 1, OffsetX: 300f, Weights: [("iv_naka_c_l", 1f)]));

        var ends = BodyRetarget.EndVertices(parts, skin, "_glv", [], out _);

        Assert.Equal(new[] { 0, 1, 2, 6, 7, 8, 9, 10, 11 }, ends.OrderBy(v => v));
        // A shoe's end is not a glove's.
        Assert.Empty(BodyRetarget.EndVertices(parts, skin, "_sho", [], out _));
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-10")]
    public void Tops_and_legs_have_no_end_and_no_limb()
    {
        var (parts, skin, _) = Skinned(new SyntheticModel.Sub(0, Weights: Foot));
        Assert.Empty(BodyRetarget.EndVertices(parts, skin, "_top", [], out _));
        Assert.Empty(BodyRetarget.EndVertices(parts, skin, "_dwn", [], out _));
        Assert.Null(AutoRefitDecisions.LimbOf("_top"));
        Assert.Null(AutoRefitDecisions.LimbOf("_dwn"));
        Assert.Equal("_dwn", AutoRefitDecisions.LimbOf("_sho"));
        Assert.Equal("_top", AutoRefitDecisions.LimbOf("_glv"));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-5")]
    public void A_strap_across_the_ankle_is_held_or_moved_whole()
    {
        // Two triangles of foot and two of calf, far apart, and one hard piece of each mix.
        var (parts, skin, _) = Skinned(
            new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: Foot),                                     // 0..8
            new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetX: 100f, Weights: Calf));                     // 9..17

        // Mostly foot: the strap is held, the calf part of it too.
        var mostlyFoot = BodyRetarget.EndVertices(parts, skin, "_sho", [new[] { 0, 1, 2, 3, 4, 9, 10 }], out _);
        Assert.Contains(9, mostlyFoot);
        Assert.Contains(10, mostlyFoot);

        // Mostly calf and touching nothing kept (vertex 8 is the foot's last corner, shared with no other): the strap
        // moves, its foot part with it.
        var mostlyCalf = BodyRetarget.EndVertices(parts, skin, "_sho", [new[] { 8, 9, 10, 11, 12, 13, 14 }], out _);
        Assert.DoesNotContain(8, mostlyCalf);
        Assert.DoesNotContain(9, mostlyCalf);
        Assert.Contains(0, mostlyCalf);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-5")]
    public void A_strap_welded_to_the_kept_foot_is_held_whole()
    {
        // A calf strap sharing its hub with the foot's (both at the origin): left free, it would be held where it is
        // welded and bent there. It goes with the foot instead.
        var (parts, skin, _) = Skinned(
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),                                     // 0..5
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Calf));                                    // 6..11

        var ends = BodyRetarget.EndVertices(parts, skin, "_sho", [new[] { 6, 7, 8, 9, 10, 11 }], out _);

        Assert.True(Enumerable.Range(6, 6).All(ends.Contains), "the welded strap was split from the foot");
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-4")]
    public void The_limb_view_reads_the_leg_alone()
    {
        var (parts, skin, bytes) = Skinned(
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Calf));
        var reading = KeepEnds.Read("_sho", parts, bytes)!;

        Assert.Equal("_dwn", reading.Limb);
        Assert.Equal("_sho>_dwn", reading.CacheSlot);
        Assert.Equal(parts.Positions.Length, reading.LimbView.Positions.Length);   // numbered as before
        var viewed = reading.LimbView.Parts.SelectMany(p => p.Triangles).ToHashSet();
        Assert.DoesNotContain(viewed, reading.Ends.Contains);
        // Only the calf is left to read.
        Assert.All(BodySizeMatch.SkinPoints(reading.LimbView), p => Assert.True(p.X >= 100f));
    }

    [Fact]
    public void A_piece_whose_rig_cannot_be_read_is_not_split()
    {
        var bytes = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0)));
        var parts = ModelPartReader.Read(bytes)!;
        Assert.Null(KeepEnds.Read("_sho", parts, [1, 2, 3]));
        Assert.Null(KeepEnds.Read("_top", parts, bytes));
    }

    // ── the solve: the end held, the limb swapped ────────────────────────────────────────────────────────

    /// <summary>
    /// A shoe at body scale on a cube "leg" (half 0.10 m, growing to 0.12): a leg skin triangle on the leg's +Z face
    /// (mesh 0, vertices 0-2), a foot skin triangle well off the body (mesh 1, 3-5), and stocking cloth welded to the leg
    /// skin's corner (mesh 2, 6-8; vertex 6 is where vertex 1 is).
    /// </summary>
    private static ModelParts Shoe(string skinMaterial = Skin)
    {
        float[] pos =
        [
            0f, 0f, 0.10f, 0.01f, 0f, 0.10f, 0f, 0.01f, 0.10f,              // leg skin, on the leg
            0f, -0.30f, 0.20f, 0.01f, -0.30f, 0.20f, 0f, -0.29f, 0.20f,     // the foot, posed off any body
            0.01f, 0f, 0.10f, 0.02f, 0f, 0.10f, 0.02f, 0.01f, 0.10f,        // stocking, welded at vertex 6 = vertex 1
        ];
        var nrm = Enumerable.Range(0, 9).SelectMany(_ => new[] { 0f, 0f, 1f }).ToArray();
        return Parts(pos, nrm, (0, skinMaterial, [0, 1, 2]), (1, skinMaterial, [3, 4, 5]), (2, Cloth, [6, 7, 8]));
    }

    private static readonly HashSet<int> FootVertices = [3, 4, 5];

    /// <summary>The leg pair: Cube(0.10) to Cube(0.12). It carries a "file" so the solver treats its skin as swapped;
    /// nothing here reads it.</summary>
    private static BodyRetarget.SlotPair Legs(string slot = "_dwn")
    {
        var source = Cube(0.10f);
        var target = Cube(0.12f);
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "legs", out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, target, TargetModel: [0]);
    }

    private static Vector3 Delta(BodyRetarget.Solved solved, int v)
    {
        var d = solved.Edit.DeltaAt(v);
        return new Vector3(d.X, d.Y, d.Z);
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-1")]
    public void A_shoes_foot_is_left_exactly_where_its_author_put_it()
    {
        var feet = Legs("_sho") with { ShapeOnly = true };
        var solved = BodyRetarget.Solve(Shoe(), [Legs(), feet], "_sho", held: FootVertices, replaceSkin: true,
                                        swapSlot: "_dwn");

        foreach (int v in FootVertices) Assert.Equal(Vector3.Zero, Delta(solved, v));
        // The leg was refitted: laid onto the larger leg.
        Assert.True(Delta(solved, 0).Z > 0.015f, $"the leg skin moved only {Delta(solved, 0).Z * 1000f:F1} mm");
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-3")]
    public void Gloves_keep_their_hands_and_refit_their_arms()
    {
        var solved = BodyRetarget.Solve(Shoe(), [Legs("_top")], "_glv", held: FootVertices, replaceSkin: true,
                                        swapSlot: "_top");

        foreach (int v in FootVertices) Assert.Equal(Vector3.Zero, Delta(solved, v));
        Assert.True(Delta(solved, 0).Z > 0.015f, "the arm was not refitted");
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-2")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-3")]
    public void Stocking_cloth_welded_to_swapped_leg_skin_is_not_laid_flat()
    {
        // The stocking's corner is welded to the leg skin. Worn in its own slot the shoe swaps no skin of that slot, and
        // before swapSlot the solve parted the cloth from nothing — so the stocking's corner stayed skin, laid flat onto
        // the new leg with its edges sinking between corners (the Pioneer's Bottoms' slit). Told the legs' skin is the
        // one swapped, it parts the stocking from the leg skin, and not from the kept foot.
        var shoe = Shoe();
        Assert.Null(BodyRetarget.PartedSkinMeshes(shoe, [Legs()], "_sho", replaceSkin: true, swapSlot: null));
        var parted = BodyRetarget.PartedSkinMeshes(shoe, [Legs()], "_sho", replaceSkin: true, swapSlot: "_dwn");
        Assert.Equal(new HashSet<int> { 0 }, parted);

        var sets = BodyRetarget.Sets.From(shoe, FootVertices, parted);
        Assert.NotEqual(sets.NodeOf[1], sets.NodeOf[6]);
        Assert.Contains(sets.NodeOf[6], sets.ClothNodes);
        // Only when the swap really happens: a legs pair without the body's file swaps nothing, and parts nothing.
        Assert.Null(BodyRetarget.PartedSkinMeshes(shoe, [Legs() with { TargetModel = null }], "_sho", true, "_dwn"));
    }

    [Fact]
    public void Cloth_beside_the_kept_foot_is_not_pulled_toward_the_new_leg()
    {
        // An ankle strap 5 mm out from the kept foot, nearer it than any leg skin: its gap was measured from that foot,
        // which the new legs do not draw. Following the laid skin, it is left exactly as the transfer put it.
        float[] pos =
        [
            0f, 0f, 0.10f, 0.01f, 0f, 0.10f, 0f, 0.01f, 0.10f,              // leg skin
            0f, -0.12f, 0.10f, 0.02f, -0.12f, 0.10f, 0f, -0.10f, 0.10f,     // the foot, held
            0f, -0.12f, 0.105f, 0.02f, -0.12f, 0.105f, 0f, -0.10f, 0.105f,  // the strap, 5 mm over the foot
        ];
        var nrm = Enumerable.Range(0, 9).SelectMany(_ => new[] { 0f, 0f, 1f }).ToArray();
        var shoe = Parts(pos, nrm, (0, Skin, [0, 1, 2]), (1, Skin, [3, 4, 5]), (2, Cloth, [6, 7, 8]));

        var following = BodyRetarget.Solve(shoe, [Legs()], "_sho", held: FootVertices, replaceSkin: true, swapSlot: "_dwn");
        var notFollowing = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoFollow: true),
            () => BodyRetarget.Solve(shoe, [Legs()], "_sho", held: FootVertices, replaceSkin: true, swapSlot: "_dwn"));

        foreach (int v in new[] { 6, 7, 8 }) Assert.Equal(Delta(notFollowing, v), Delta(following, v));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-1")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-2")]
    public void The_rebuild_swaps_exactly_the_meshes_the_solve_parted()
    {
        // Two skin meshes both lying on the leg: judged on its own the rebuild would swap both. Told the solve parted the
        // cloth from mesh 0 alone — mesh 1 held an ankle ring the hold kept off the new body — it swaps mesh 0 and keeps 1.
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)),
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 1)),
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        var legs = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));

        Assert.NotNull(BodyRetarget.Rebuild(garment, [Resized("_dwn", legs)], true, null, out var judged, cutHidden: false));
        Assert.Equal(3, judged.Removed);

        Assert.NotNull(BodyRetarget.Rebuild(garment, [Resized("_dwn", legs)], true, null, out var told, cutHidden: false,
                                            swapMeshes: new HashSet<int> { 0 }));
        Assert.Equal(2, told.Removed);
        Assert.Equal(1, told.Kept);
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-1")]
    public void A_feet_pair_for_its_shape_never_puts_the_feet_in()
    {
        // The feet ride along for their shape only: their skin is not swapped in, whatever the garment's skin lies on.
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)),
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f)));
        var feet = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));

        Assert.Null(BodyRetarget.Rebuild(garment, [Resized("_sho", feet) with { ShapeOnly = true }], true, null,
                                         out var report, cutHidden: false));
        Assert.Equal(0, report.Removed);
        Assert.Equal(0, report.Added);
        Assert.Empty(BodyRetarget.SwappedSkinMeshes(ModelPartReader.Read(garment)!,
                                                    [Resized("_sho", feet) with { ShapeOnly = true }]));
    }

    // ── reading ──────────────────────────────────────────────────────────────────────────────────────────

    private static readonly BodyOption LegsS = new("Legs", "", "S", "s.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
    private static readonly BodyOption LegsM = new("Legs", "", "M", "m.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn");
    private static readonly BodyOption FeetM = new("Feet", "", "M", "fm.mdl", "chara/equipment/e0000/model/c0201e0000_sho.mdl", "_sho");
    private static readonly BodySizeCatalog Body = new("<body>", [LegsS, LegsM, FeetM]);

    private static AutoRefitDecisions.Pick Pick(BodyOption option, BodySizeMatch.Confidence confidence = BodySizeMatch.Confidence.Exact,
                                                bool fromCloth = false)
        => new("body", option, confidence, fromCloth);

    /// <summary>A shoe whose leg carries skin, or only cloth: what <see cref="KeepEnds.Find"/> is handed.</summary>
    private static (KeepEnds.Reading Reading, ModelParts Parts) ReadShoe(bool legSkin)
    {
        var (parts, _, bytes) = Skinned(
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),
            new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Calf));
        if (!legSkin)
        {
            // The same rig with the leg drawn in cloth.
            bytes = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot)),
                                         new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Calf)));
            parts = ModelPartReader.Read(bytes)!;
        }
        return (KeepEnds.Read("_sho", parts, bytes)!, parts);
    }

    private static Func<ModelParts, string, AutoRefitDecisions.Pick?> Detect(AutoRefitDecisions.Pick? limb, AutoRefitDecisions.Pick? own)
        => (_, slot) => slot == "_dwn" ? limb : own;

    private static readonly IReadOnlySet<string> NoBones = new HashSet<string>();

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-4")]
    public void A_leg_with_skin_is_read_by_its_skin()
    {
        var (reading, parts) = ReadShoe(legSkin: true);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(Pick(LegsM), null), _ => Body);
        Assert.Equal(KeepEnds.Route.Skin, found.Route);
        Assert.Equal(LegsM, found.Pick!.Option);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-10")]
    public void An_unclear_leg_reading_is_unsure_not_redrawn()
    {
        var (reading, parts) = ReadShoe(legSkin: true);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201",
                                  Detect(Pick(LegsM, BodySizeMatch.Confidence.Ambiguous), Pick(FeetM)), _ => Body);
        Assert.Equal(KeepEnds.Route.Unsure, found.Route);
        Assert.IsType<KeepEnds.LeftAsIs>(KeepEnds.Decide(found, reading, NoBones, "0201", "body", _ => Body, _ => LegsM));
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-11")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-9")]
    public void A_cloth_only_boot_shaft_is_read_by_its_cloth()
    {
        // No skin above the foot: the body is read off the foot, as the boot always was, and the size of its legs off how
        // the shaft's cloth sits — handed only the shaft, never the kept foot.
        var (reading, parts) = ReadShoe(legSkin: false);
        ModelParts? ranked = null;
        bool? withSkin = null;
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (view, options, _, skin) =>
                                  {
                                      ranked = view;
                                      withSkin = skin;
                                      return new BodySizeMatch.Ranking(BodySizeMatch.Confidence.Likely,
                                                                       [new BodySizeMatch.Score(LegsM, 0f, 0.002f)],
                                                                       FromCloth: true);
                                  });

        Assert.Equal(KeepEnds.Route.Cloth, found.Route);
        Assert.Equal(LegsM, found.Pick!.Option);
        Assert.True(found.Pick.FromCloth);
        Assert.False(withSkin);
        Assert.DoesNotContain(ranked!.Parts.SelectMany(p => p.Triangles), reading.Ends.Contains);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-14")]
    public void A_body_with_one_size_of_legs_needs_no_reading_of_them()
    {
        // The game's own body: its gear is made on it, and it has one size of legs. The cloth reading could only guess
        // at a lone size, so it is not asked.
        var (reading, parts) = ReadShoe(legSkin: false);
        var game = new BodySizeCatalog("<game>", [LegsM, FeetM]);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => game,
                                  (_, _, _, _) => new BodySizeMatch.Ranking(BodySizeMatch.Confidence.Guess,
                                      [new BodySizeMatch.Score(LegsM, 0f, 0.002f)], FromCloth: true));

        Assert.Equal(KeepEnds.Route.Cloth, found.Route);
        Assert.Equal(LegsM, found.Pick!.Option);
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-5")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-14")]
    public void A_plain_game_shoe_reaching_no_leg_is_a_skin_only_redraw()
    {
        // The game's body has one size of legs, but a shoe that is all foot reaches none of it: there is nothing to refit,
        // only its skin to redraw — not a refit under a legs size name, saved again for every size chosen.
        var (parts, _, bytes) = Skinned(new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot));
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        Assert.Empty(reading.LimbView.Parts);
        var game = new BodySizeCatalog("<game>", [LegsM, FeetM]);

        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => game,
                                  (view, options, catalog, skin) => BodySizeMatch.RankByCloth(view, options, catalog.PathOf));

        Assert.Equal(KeepEnds.Route.SkinOnly, found.Route);
        Assert.Equal(FeetM, found.Pick!.Option);
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-5")]
    public void A_shoe_reaching_no_leg_has_only_its_skin_redrawn_once_per_body()
    {
        var (reading, parts) = ReadShoe(legSkin: false);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, Pick(FeetM)), _ => Body,
                                  (_, _, _, _) => null);   // the cloth reaches no leg either

        Assert.Equal(KeepEnds.Route.SkinOnly, found.Route);
        var decision = Assert.IsType<KeepEnds.SkinOnly>(
            KeepEnds.Decide(found, reading, NoBones, "0201", "other", _ => Body, s => s == "_sho" ? FeetM : LegsM));
        Assert.Equal(FeetM, decision.Target);
        Assert.Equal("Neolithe — skin", KeepEnds.SkinOnlyOption("Neolithe"));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-8")]
    public void A_piece_whose_body_cannot_be_told_is_left_as_is()
    {
        var (reading, parts) = ReadShoe(legSkin: false);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201", Detect(null, null), _ => Body);
        Assert.Equal(KeepEnds.Route.NoFamily, found.Route);
        Assert.Equal(new KeepEnds.LeftAsIs(KeepEnds.Route.NoFamily),
                     KeepEnds.Decide(found, reading, NoBones, "0201", "body", _ => Body, _ => LegsM));

        var noSkin = KeepEnds.Find(reading, parts, NoBones, "0201",
                                   Detect(null, Pick(FeetM, BodySizeMatch.Confidence.NoBodyMesh)), _ => Body);
        Assert.Equal(KeepEnds.Route.NoSkin, noSkin.Route);
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-6")]
    public void A_refit_is_named_for_the_legs_size_and_takes_the_feet_along_for_their_shape()
    {
        var (reading, _) = ReadShoe(legSkin: true);
        var target = new BodyOption("Legs", "", "L", "l.mdl", LegsM.GamePath, "_dwn");
        var targetFeet = new BodyOption("Feet", "", "L", "fl.mdl", FeetM.GamePath, "_sho");
        var decision = Assert.IsType<KeepEnds.Refit>(
            KeepEnds.Decide(new KeepEnds.Found(KeepEnds.Route.Skin, Pick(LegsM)), reading, NoBones, "0201", "other",
                            _ => Body, s => s == "_dwn" ? target : targetFeet));

        Assert.Equal(target, decision.Target);
        Assert.Equal(FeetM, decision.EndFrom);
        Assert.Equal(targetFeet, decision.EndTo);
        Assert.Equal("Neolithe — L", RefitCore.OptionName("Neolithe", [decision.Target.Label]));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-11")]
    public void A_leg_already_the_chosen_size_already_fits()
    {
        var (reading, _) = ReadShoe(legSkin: true);
        Assert.IsType<KeepEnds.AlreadyFits>(
            KeepEnds.Decide(new KeepEnds.Found(KeepEnds.Route.Skin, Pick(LegsM)), reading, NoBones, "0201", "body",
                            _ => Body, _ => LegsM));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-12")]
    public void No_legs_size_for_the_race_is_said_by_the_legs()
    {
        var (reading, _) = ReadShoe(legSkin: true);
        Assert.Equal(new KeepEnds.NoSize("_dwn"),
                     KeepEnds.Decide(new KeepEnds.Found(KeepEnds.Route.Skin, Pick(LegsM)), reading, NoBones, "0201",
                                     "other", _ => Body, _ => null));
        // A legs target follows the chosen chest when only a chest was chosen.
        var chest = new BodyOption("Chest", "", "M", "c.mdl", "chara/equipment/e0000/model/c0201e0000_top.mdl", "_top");
        Assert.Equal(LegsM, AutoRefitDecisions.Companion([LegsS, LegsM], chest, null));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-20")]
    public void Only_shoes_look_for_the_authors_own_size()
    {
        Assert.True(KeepEnds.ComparesAuthorSizes("_sho"));
        Assert.False(KeepEnds.ComparesAuthorSizes("_glv"));
    }

    // ── the skin redrawn, the rest left ──────────────────────────────────────────────────────────────────

    /// <summary>A body file on disk drawing <paramref name="material"/>, for what reads bodies by path.</summary>
    private static string BodyFile(string dir, string name, string material)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, SyntheticModel.Build([], new SyntheticModel.Mesh(material, new SyntheticModel.Sub(0, TrianglesPerIsland: 2))));
        return path;
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-7")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-18")]
    public void A_kept_skin_with_nothing_to_redraw_is_the_very_same_model()
    {
        // The watcher writes a skin-only option only when the redraw changed something: a foot already drawn with the new
        // body's skin, or one whose layout cannot be converted, comes back as the same bytes and nothing is written. The
        // first is also every redraw put on again — it is drawn like the new body already — which is so left quietly,
        // before any put-back, instead of being reloaded and announced on every equip.
        string dir = Path.Combine(Path.GetTempPath(), "proteus-keepends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            var shoe = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)));
            string bibo = BodyFile(dir, "bibo.mdl", Skin);
            Assert.Same(shoe, RefitCore.MatchSkinToBody(shoe, bibo, bibo, null, false, log));

            var gen3 = SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201b0001_b.mtrl",
                                                                       new SyntheticModel.Sub(0, TrianglesPerIsland: 2)));
            string gen3Body = BodyFile(dir, "gen3.mdl", "/mt_c0201b0001_b.mtrl");
            Assert.Same(gen3, RefitCore.MatchSkinToBody(gen3, gen3Body, bibo, null, false, log));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-17")]
    public void Toenails_on_the_kept_foot_stay()
    {
        // The legs' skin is swapped; the toenails sit on the kept foot, far from the legs, and go nowhere.
        const string Nails = "/mt_c0201b0001_toenails.mtrl";
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2)),
            new SyntheticModel.Mesh(Nails, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetX: 100f)));
        var legs = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)));

        var rebuilt = BodyRetarget.Rebuild(garment, [Resized("_dwn", legs)], true, null, out var report, cutHidden: false,
                                           swapMeshes: new HashSet<int> { 0 });

        Assert.NotNull(rebuilt);
        Assert.Equal(2, report.Removed);
        Assert.Equal(0, report.Adorned);
        Assert.Equal(3, ModelPartReader.Read(rebuilt!)!.Parts.Where(p => p.Island < 0 && p.Material == Nails)
                                                           .Sum(p => p.TriangleCount));
    }

    // ── a refit made before ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-10")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-16")]
    public void A_refit_made_before_is_put_back_as_it_always_was()
    {
        // Unchanged for every slot: an option this tool saved before, automatic or by hand, still offered, is put back;
        // one never saved is not.
        string root = Path.Combine(Path.GetTempPath(), "proteus-keepends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), PenumbraModMeta.NewMetaJson("Shoes", "Someone", ""));
            const string GamePath = "chara/equipment/e6070/model/c0201e6070_sho.mdl";
            var model = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0)));
            Assert.True(BodyRetargetWriter.Save(root, "Size", GamePath, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit("Neolithe — M", model, "M")], auto: true).Ok);
            Assert.True(BodyRetargetWriter.Save(root, "Size", GamePath, "neo", "Bibo+ — M",
                                                [new BodyRetargetWriter.Refit("Neolithe — L", model, "L")], auto: false).Ok);

            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Neolithe — M", GamePath));
            Assert.True(AutoRefitWatcher.Existing(root, "Size", "Neolithe — L", GamePath));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Neolithe — S", GamePath));
            Assert.False(AutoRefitWatcher.Existing(root, "Size", "Neolithe — M", "chara/equipment/e6070/model/c0201e6070_top.mdl"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-8")]
    public void A_foot_reading_too_weak_to_refit_from_is_not_redrawn_from_either()
    {
        // Its body would decide the texture layout the kept skin is converted from: unsure, nothing is written.
        var (reading, parts) = ReadShoe(legSkin: false);
        var found = KeepEnds.Find(reading, parts, NoBones, "0201",
                                  Detect(null, Pick(FeetM, BodySizeMatch.Confidence.Ambiguous)), _ => Body,
                                  (_, _, _, _) => null);
        Assert.Equal(KeepEnds.Route.Unsure, found.Route);
        Assert.Equal(new KeepEnds.LeftAsIs(KeepEnds.Route.Unsure),
                     KeepEnds.Decide(found, reading, NoBones, "0201", "other", _ => Body, _ => FeetM));
    }

    // ── the whole plan: what is swapped, cut and held ────────────────────────────────────────────────────

    /// <summary>A legs body: <paramref name="triangles"/> triangles of skin, raised by <paramref name="z"/>, rigged to the calf.</summary>
    /// <param name="islands">Lay the triangles out as that many islands apart instead, which share no corner.</param>
    private static byte[] LegsBody(int triangles, float z, float x = 0f, int islands = 1) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, Islands: islands, TrianglesPerIsland: triangles / islands,
                                                             OffsetX: x, OffsetZ: z, Weights: Calf)));

    /// <summary>A legs pair from <paramref name="body"/> at rest to the same body raised 10 mm, its files carried.</summary>
    private static BodyRetarget.SlotPair Raised(Func<float, byte[]> body)
    {
        var source = ModelPartReader.Read(body(0f))!;
        var targetBytes = body(0.01f);
        var target = ModelPartReader.Read(targetBytes)!;
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "_dwn", out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair("_dwn", built!, target, targetBytes, body(0f));
    }

    /// <summary>A shoe: its leg skin (mesh 0) lying on the legs, its foot (mesh 1) far off them, stocking cloth (mesh 2).</summary>
    private static byte[] ShoeModel(int legTriangles, int islands = 1) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, Islands: islands, TrianglesPerIsland: legTriangles / islands,
                                                             Weights: Calf)),
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Foot)),
        new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.001f, Weights: Calf)));

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-2")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-4")]
    public void A_leg_mesh_with_a_held_ankle_ring_is_swapped_as_the_solve_parted_it()
    {
        // Three of the leg mesh's 48 vertices (6%) are held, and stay where the old legs had them while the rest is laid
        // 10 mm out onto the new ones. Judged again on that laid model the rebuild would keep the mesh — 6% off the body —
        // though the solve had already parted the stocking from it as swapped, opening the seam. It is handed the solve's
        // own set, and swaps it.
        var bytes = ShoeModel(16);
        var parts = ModelPartReader.Read(bytes)!;
        var plan = BodyRetarget.Plan(parts, bytes, [Raised(z => LegsBody(16, z))], "_sho",
                                     held: new HashSet<int> { 1, 2, 4 }, replaceSkin: true, cutHidden: false,
                                     swapSlot: "_dwn");

        Assert.Equal(16, plan.Report.Swap?.Removed);
        Assert.Equal(0, plan.Report.Folded);
    }

    [Fact]
    [Trait("Plan", "AC-keep-hand-foot-skin-2")]
    [Trait("Plan", "AC-keep-hand-foot-skin-1")]
    public void A_shoes_new_legs_are_cut_to_where_it_drew_skin_and_its_foot_is_kept()
    {
        // The shoe's leg skin covers 4 of the legs' 16 triangles, each its own island. Swapped whole, the new legs would
        // carry all 16 into the shoe; cut, only what the shoe drew. The foot, a mesh of its own off the legs, is kept, and
        // nothing folds.
        var bytes = ShoeModel(4, islands: 4);
        var parts = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        var plan = KeepEnds.Plan(parts, bytes, reading, Raised(z => LegsBody(16, z, islands: 16)), null, across: true);

        Assert.Equal(4, plan.Report.Swap?.Removed);
        Assert.True(plan.Report.Swap?.Cut > 0, $"nothing of the legs was cut: {plan.Report.Swap}");
        Assert.True(plan.Report.Swap?.Added < 16, $"the legs went in whole: {plan.Report.Swap}");
        Assert.Equal(1, plan.Report.Swap?.Kept);
        Assert.Equal(0, plan.Report.Folded);
        foreach (int v in reading.Ends) Assert.Equal(default, plan.Edit.DeltaAt(v));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-11")]
    public void Legs_of_one_shape_within_a_body_mod_already_fit()
    {
        // Read as one size and aimed at another of the same body mod, whose legs are the same where the shoe is: the
        // refit would move nothing, and nothing is saved. Where they differ, it is refitted.
        string dir = Path.Combine(Path.GetTempPath(), "proteus-keepends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 120 triangles: enough skin points on the legs for the ranking to read (BodySizeMatch.MinEvidence).
            var (parts, _, bytes) = Skinned(
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 120, OffsetX: 100f, Weights: Calf));
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            File.WriteAllBytes(Path.Combine(dir, "s.mdl"), LegsBody(120, 0f, 100f));
            File.WriteAllBytes(Path.Combine(dir, "same.mdl"), LegsBody(120, 0f, 100f));
            File.WriteAllBytes(Path.Combine(dir, "other.mdl"), LegsBody(120, 0.02f, 100f));
            var s = LegsS with { Rel = "s.mdl" };
            var same = LegsM with { Rel = "same.mdl" };
            var other = LegsM with { Name = "L", Rel = "other.mdl" };
            var body = new BodySizeCatalog(dir, [s, same, other, FeetM]);
            var found = new KeepEnds.Found(KeepEnds.Route.Skin, Pick(s));

            Assert.IsType<KeepEnds.AlreadyFits>(
                KeepEnds.Decide(found, reading, NoBones, "0201", "body", _ => body, slot => slot == "_dwn" ? same : FeetM));
            Assert.IsType<KeepEnds.Refit>(
                KeepEnds.Decide(found, reading, NoBones, "0201", "body", _ => body, slot => slot == "_dwn" ? other : FeetM));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── the shared core: the automatic refit's output, pinned ────────────────────────────────────────────

    /// <summary>The automatic refit's keep-ends plan for <see cref="ShoeModel"/>, with and without the feet's shape.</summary>
    private static (BodyRetarget.Planned WithFeet, BodyRetarget.Planned LegsOnly) AutomaticPlans()
    {
        var bytes = ShoeModel(4, islands: 4);
        var parts = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        var legs = Raised(z => LegsBody(16, z, islands: 16));
        var feet = Resized("_sho", SyntheticModel.Build([], new SyntheticModel.Mesh(Skin,
                               new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Foot))));
        return (KeepEnds.Plan(parts, bytes, reading, legs, feet, across: true),
                KeepEnds.Plan(parts, bytes, reading, legs, null, across: false));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>
    /// The automatic refit's plan, byte for byte as it was before the Studio came to share it: recorded from the code
    /// before the shared core was lifted out of it, so the lifting cannot have changed what the watcher writes.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-10")]
    public void Automatic_keep_ends_plan_is_unchanged()   // [AC-keep-ends-studio-10]
    {
        var (withFeet, legsOnly) = AutomaticPlans();
        Assert.Equal("54513857BC98FE4A14E87E3B9D7EF82C75A69063768CC3DD14A1DDFF62B86CCE", Hash(withFeet.Model));
        Assert.Equal("1398DC77C9E775E242310F71BB027817162C177C71B7ECA11F7873C9781DBBB6", Hash(legsOnly.Model));
    }

    // ── the shared core: pairs, the Studio's plan, the Studio's reading ──────────────────────────────────

    /// <summary>A temp folder of synthetic body files, deleted after <paramref name="body"/> runs.</summary>
    private static void InBodies(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "proteus-keepends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A feet body: two triangles of foot skin, rigged to the foot, raised by <paramref name="z"/>.</summary>
    private static byte[] FeetBody(float z) => SyntheticModel.Build([],
        new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, OffsetZ: z, Weights: Foot)));

    private static string Write(string dir, string name, byte[] bytes)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static (ushort?, ushort?) NoMask(string _) => (null, null);

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-10")]
    public void BuildPairs_matches_the_watchers_two_pairs()   // [AC-keep-ends-studio-10]
    {
        InBodies(dir =>
        {
            var bytes = ShoeModel(4, islands: 4);
            var parts = ModelPartReader.Read(bytes)!;
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            string legsFrom = Write(dir, "ls.mdl", LegsBody(16, 0f, islands: 16));
            string legsTo = Write(dir, "lm.mdl", LegsBody(16, 0.01f, islands: 16));
            string feetFrom = Write(dir, "fs.mdl", FeetBody(0f));
            string feetTo = Write(dir, "fm.mdl", FeetBody(0.01f));

            Assert.Null(KeepEnds.BuildPairs(reading, legsFrom, legsTo, feetFrom, feetTo, false, NoMask, s => s, null, null,
                                            out var limb, out var end));
            Assert.Null(BodyRetarget.BuildPair("_dwn", legsFrom, legsTo, "_dwn", false, null, null, null, out var watcherLimb));
            Assert.Null(BodyRetarget.BuildPair("_sho", feetFrom, feetTo, "_sho", false, null, null, null, out var watcherEnd));

            Assert.NotNull(end);
            Assert.Equal(KeepEnds.Plan(parts, bytes, reading, watcherLimb, watcherEnd, true).Model,
                         KeepEnds.Plan(parts, bytes, reading, limb, end, true).Model);
        });
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-3")]
    public void Refused_end_pair_is_reported_and_left_out()   // [EDGE-keep-ends-studio-3]
    {
        InBodies(dir =>
        {
            var bytes = ShoeModel(4, islands: 4);
            var parts = ModelPartReader.Read(bytes)!;
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            string legsFrom = Write(dir, "ls.mdl", LegsBody(16, 0f, islands: 16));
            string legsTo = Write(dir, "lm.mdl", LegsBody(16, 0.01f, islands: 16));
            string broken = Write(dir, "broken.mdl", [1, 2, 3, 4]);
            string? reported = null;

            Assert.Null(KeepEnds.BuildPairs(reading, legsFrom, legsTo, broken, broken, false, NoMask, s => s, null,
                                            why => reported = why, out var limb, out var end));
            Assert.Null(end);
            Assert.NotNull(reported);
            Assert.Equal("_dwn", limb.Slot);
        });
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-4")]
    public void Refused_limb_pair_is_returned()   // [EDGE-keep-ends-studio-4]
    {
        InBodies(dir =>
        {
            var bytes = ShoeModel(4, islands: 4);
            var parts = ModelPartReader.Read(bytes)!;
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            string broken = Write(dir, "broken.mdl", [1, 2, 3, 4]);
            string legs = Write(dir, "lm.mdl", LegsBody(16, 0.01f, islands: 16));

            Assert.NotNull(KeepEnds.BuildPairs(reading, broken, legs, null, null, false, NoMask, s => s, null, null,
                                               out _, out var end));
            Assert.Null(end);
        });
    }

    /// <summary>The Body size tool's plan for a shoe, with the panel's defaults, onto <paramref name="targets"/>.</summary>
    private static KeepEnds.StudioPlanned StudioPlanOf(ModelParts parts, byte[] bytes, BodySizeCatalog source, BodyOption from,
                                                       BodySizeCatalog target, IReadOnlyList<BodyOption> targets)
        => KeepEnds.StudioPlan(new KeepEnds.StudioPlanInput(
                                   parts, bytes, "_sho", [], source, target, "0201", false, from, targets,
                                   SameBodyMod: false, FromCloth: false, ReplaceSkin: true, Across: true, ClearBody: false,
                                   Held: null, Shared: null, _ => (null, null), s => s, "test"),
                               (p, _) => p, null, NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>());

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-1")]
    [Trait("Plan", "EDGE-keep-ends-studio-4")]
    public void Studio_plan_says_why_it_planned_nothing()   // [EDGE-keep-ends-studio-1] [EDGE-keep-ends-studio-4]
    {
        InBodies(dir =>
        {
            var bytes = ShoeModel(4, islands: 4);
            var parts = ModelPartReader.Read(bytes)!;
            Write(dir, "broken.mdl", [1, 2, 3, 4]);
            Write(dir, "lm.mdl", LegsBody(16, 0.01f, islands: 16));
            Write(dir, "ll.mdl", LegsBody(16, 0.02f, islands: 16));
            var broken = LegsS with { Rel = "broken.mdl" };
            var m = LegsM with { Rel = "lm.mdl" };
            var l = LegsM with { Name = "L", Rel = "ll.mdl" };
            var catalog = new BodySizeCatalog(dir, [broken, m, l]);

            // A rig that cannot be read: nothing planned, and the tool says the foot cannot be kept.
            var unreadable = StudioPlanOf(parts, [1, 2, 3], catalog, broken, catalog, [m]);
            Assert.Null(unreadable.Planned);
            Assert.Equal(Proteus.Localization.Strings.Parts.RetargetEndsUnreadable, unreadable.Error);

            // A legs pair refused: the refusal, named for the size it was refused for when there are several.
            var one = StudioPlanOf(parts, bytes, catalog, broken, catalog, [m]);
            var two = StudioPlanOf(parts, bytes, catalog, broken, catalog, [m, l]);
            Assert.Null(one.Planned);
            Assert.NotEmpty(one.Error);
            Assert.Null(two.Planned);
            Assert.Equal($"{m.Label}: {one.Error}", two.Error);
        });
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-3")]
    public void End_pair_is_the_companion_of_the_limb_sizes()   // [AC-keep-ends-studio-3]
    {
        var feetS = FeetM with { Name = "S", Rel = "fs.mdl" };
        var source = new BodySizeCatalog("<a>", [LegsS, LegsM, feetS, FeetM]);
        var target = new BodySizeCatalog("<b>", [LegsS, LegsM, feetS, FeetM]);

        Assert.Equal((FeetM, FeetM), KeepEnds.EndPair(source, target, "_sho", "0201", LegsM, LegsM));
        Assert.Equal((feetS, FeetM), KeepEnds.EndPair(source, target, "_sho", "0201", LegsS, LegsM));
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-2")]
    public void No_end_option_plans_the_limb_alone()   // [EDGE-keep-ends-studio-2]
    {
        // A body with no feet at this race: no end pair, and the plan is the legs-only plan.
        var noFeet = new BodySizeCatalog("<b>", [LegsS, LegsM]);
        Assert.Equal((null, null), KeepEnds.EndPair(Body, noFeet, "_sho", "0201", LegsM, LegsM));
        Assert.Equal((null, null), KeepEnds.EndPair(noFeet, Body, "_sho", "0201", LegsM, LegsM));

        var bytes = ShoeModel(4, islands: 4);
        var parts = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        var legs = Raised(z => LegsBody(16, z, islands: 16));
        Assert.Equal(KeepEnds.Plan(parts, bytes, reading, legs, null, across: true).Model,
                     KeepEnds.Plan(parts, bytes, reading, legs, null, true, true, false, null, null).Model);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-7")]
    public void Unticked_cloth_part_stays_put()   // [EDGE-keep-ends-studio-7]
    {
        var bytes = ShoeModel(4, islands: 4);
        var parts = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        var cloth = parts.Parts.Where(p => p.Material == Cloth).SelectMany(p => p.Triangles).ToHashSet();
        Assert.NotEmpty(cloth);

        var plan = KeepEnds.Plan(parts, bytes, reading, Raised(z => LegsBody(16, z, islands: 16)), null,
                                 replaceSkin: true, across: true, clearBody: false, alsoHeld: cloth, extra: null);

        foreach (int v in cloth.Concat(reading.Ends)) Assert.Equal(default, plan.Edit.DeltaAt(v));
        Assert.Equal(0, plan.Report.Folded);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-8")]
    public void Studio_plan_always_cuts_hidden_skin()   // [EDGE-keep-ends-studio-8]
    {
        // The Studio's plan has no cut setting: the limb's skin always goes in cut to where the piece drew skin.
        var bytes = ShoeModel(4, islands: 4);
        var parts = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        var plan = KeepEnds.Plan(parts, bytes, reading, Raised(z => LegsBody(16, z, islands: 16)), null,
                                 replaceSkin: true, across: false, clearBody: false, alsoHeld: null, extra: null);

        Assert.True(plan.Report.Swap?.Cut > 0, $"nothing of the legs was cut: {plan.Report.Swap}");
        Assert.True(plan.Report.Swap?.Added < 16, $"the legs went in whole: {plan.Report.Swap}");
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-9")]
    public void Studio_plan_without_new_skin_swaps_nothing()   // [EDGE-keep-ends-studio-9]
    {
        var bytes = ShoeModel(4, islands: 4);
        var parts = ModelPartReader.Read(bytes)!;
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        var plan = KeepEnds.Plan(parts, bytes, reading, Raised(z => LegsBody(16, z, islands: 16)), null,
                                 replaceSkin: false, across: true, clearBody: false, alsoHeld: null, extra: null);

        // Across body mods the weights are still the new body's (reweighted), but no skin goes out or in.
        Assert.Equal(0, plan.Report.Swap?.Removed ?? 0);
        Assert.Equal(0, plan.Report.Swap?.Added ?? 0);
        foreach (int v in reading.Ends) Assert.Equal(default, plan.Edit.DeltaAt(v));
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-3")]
    public void Studio_limb_prefills_the_best_guess_from_one_ranking()   // [AC-keep-ends-studio-3]
    {
        InBodies(dir =>
        {
            // The leg's skin, on the S legs exactly; the M legs 20 mm out.
            var (parts, _, bytes) = Skinned(
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 120, OffsetX: 100f, Weights: Calf));
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            Write(dir, "s.mdl", LegsBody(120, 0f, 100f));
            Write(dir, "m.mdl", LegsBody(120, 0.02f, 100f));
            var catalog = new BodySizeCatalog(dir, [LegsS with { Rel = "s.mdl" }, LegsM with { Rel = "m.mdl" }, FeetM]);

            var read = KeepEnds.StudioLimb(reading, parts, NoBones, "0201", "body", catalog);

            Assert.Equal(KeepEnds.Route.Skin, read.Route);
            Assert.NotNull(read.Shown);
            Assert.Equal(read.Shown!.Best!.Value.Option, read.Pick);
            Assert.Equal("s.mdl", read.Pick!.Rel);
        });
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-25")]
    public void Studio_limb_ambiguous_still_prefills_and_no_skin_does_not()   // [EDGE-keep-ends-studio-25]
    {
        InBodies(dir =>
        {
            var (parts, _, bytes) = Skinned(
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 120, OffsetX: 100f, OffsetZ: 0.01f, Weights: Calf));
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            // The leg sits halfway between two sizes: no clear answer, and still a guess is filled in.
            Write(dir, "s.mdl", LegsBody(120, 0f, 100f));
            Write(dir, "m.mdl", LegsBody(120, 0.02f, 100f));
            var catalog = new BodySizeCatalog(dir, [LegsS with { Rel = "s.mdl" }, LegsM with { Rel = "m.mdl" }, FeetM]);

            var read = KeepEnds.StudioLimb(reading, parts, NoBones, "0201", "body", catalog);
            Assert.NotNull(read.Pick);
            Assert.Equal(read.Shown!.Best!.Value.Option, read.Pick);

            // No skin anywhere: nothing to read a body off, and nothing filled in.
            var clothOnly = SyntheticModel.Build([], new SyntheticModel.Mesh(Cloth,
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 100f, Weights: Calf)));
            var clothParts = ModelPartReader.Read(clothOnly)!;
            var noSkin = KeepEnds.StudioLimb(KeepEnds.Read("_sho", clothParts, clothOnly)!, clothParts, NoBones, "0201",
                                             "body", catalog);
            Assert.Equal(KeepEnds.Route.NoSkin, noSkin.Route);
            Assert.Null(noSkin.Pick);
        });
    }

    [Fact]
    [Trait("Plan", "AC-keep-ends-studio-4")]
    public void Studio_reads_a_shoe_reaching_no_leg_as_skin_only()   // [AC-keep-ends-studio-4]
    {
        InBodies(dir =>
        {
            // All foot: nothing of the leg to read, so its own feet are what its skin is redrawn from.
            var (parts, _, bytes) = Skinned(new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot));
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            Write(dir, "s.mdl", LegsBody(120, 0f, 100f));
            Write(dir, "fm.mdl", SyntheticModel.Build([], new SyntheticModel.Mesh(Skin,
                                     new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: Foot))));
            var feet = FeetM with { Rel = "fm.mdl" };
            var catalog = new BodySizeCatalog(dir, [LegsS with { Rel = "s.mdl" }, feet]);

            var read = KeepEnds.StudioLimb(reading, parts, NoBones, "0201", "body", catalog);

            Assert.Equal(KeepEnds.Route.SkinOnly, read.Route);
            // Two triangles of skin are too little to be sure by; its own feet are still the pick (and are filled in —
            // RetargetInputs.StudioPrefill), since they are the only row it has.
            Assert.Equal(BodySizeMatch.Confidence.TooLittle, read.Shown?.Confidence);
            Assert.Equal(feet, read.Pick);
            var planned = KeepEnds.SkinOnlyPlanned(bytes);
            Assert.Same(bytes, planned.Model);
            Assert.Equal(0f, planned.Report.WorstMove);
        });
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-ends-studio-1")]
    public void Unreadable_rig_falls_back()   // [EDGE-keep-ends-studio-1]
    {
        var bytes = SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0)));
        var parts = ModelPartReader.Read(bytes)!;
        Assert.Null(KeepEnds.Read("_sho", parts, [1, 2, 3], null));
        Assert.Equal(new RetargetInputs.Layout("_sho", "_sho", null),
                     RetargetInputs.LayoutFor("_sho", true, true, true, endsUnreadable: true));
    }

    // ── the cache ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Skin")]
    [InlineData("Cloth")]
    [InlineData("SkinOnly")]
    [InlineData("Unsure")]
    [InlineData("NoFamily")]
    [InlineData("NoSkin")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-15")]
    public void Every_route_comes_back_from_the_cache_as_it_went_in(string name)
    {
        var route = Enum.Parse<KeepEnds.Route>(name);
        var (reading, _) = ReadShoe(legSkin: true);
        var option = route == KeepEnds.Route.SkinOnly ? FeetM : LegsM;
        var pick = route is KeepEnds.Route.Unsure or KeepEnds.Route.NoFamily or KeepEnds.Route.NoSkin
            ? null
            : Pick(option, BodySizeMatch.Confidence.Likely, fromCloth: route == KeepEnds.Route.Cloth);
        var entry = KeepEnds.ToCache(new KeepEnds.Found(route, pick));

        var back = KeepEnds.FromCache(entry, reading, "0201", _ => Body);

        Assert.Equal(new KeepEnds.Found(route, pick), back);
    }

    [Fact]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-15")]
    public void A_reading_of_the_old_kind_is_not_taken_for_a_kept_end_one()
    {
        var (reading, _) = ReadShoe(legSkin: true);
        var old = new DetectionCache.Entry("body", FeetM.Rel, BodySizeMatch.Confidence.Exact, false, []);
        Assert.Null(KeepEnds.FromCache(old, reading, "0201", _ => Body));
        Assert.NotEqual(DetectionCache.KeyOf([1, 2, 3], "_sho", "0201", "body", "v"),
                        DetectionCache.KeyOf([1, 2, 3], reading.CacheSlot, "0201", "body", "v"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static BodyRetarget.SlotPair Resized(string slot, byte[] body)
    {
        var target = ModelPartReader.Read(body)!;
        Assert.True(IdentityCorrespondence.TryBuild(target, target, slot, out var built, out string refusal), refusal);
        return new BodyRetarget.SlotPair(slot, built!, target, body);
    }

    /// <summary>One vertex array, one part per (mesh, material, triangles), each its own mesh.</summary>
    private static ModelParts Parts(float[] pos, float[] nrm, params (int Mesh, string Material, int[] Tris)[] parts)
    {
        var list = parts.Select(p => new ModelPart
        {
            Mesh = p.Mesh,
            Submesh = 0,
            Island = -1,
            Label = $"{p.Mesh}.0",
            Material = p.Material,
            Triangles = p.Tris,
            Ordinals = Enumerable.Range(0, p.Tris.Length / 3).ToArray(),
            AttributeMask = 0,
            Min = Vector3.Zero,
            Max = Vector3.One,
            Toggleable = true,
        }).ToList();
        var spans = parts.Select(p => new MeshSpan(p.Mesh, p.Tris.Min(), p.Tris.Max() - p.Tris.Min() + 1)).ToList();
        return new ModelParts
        {
            Positions = pos,
            Normals = nrm,
            MeshSpans = spans,
            Parts = list,
            AttributeNames = [],
            Min = new Vector3(-1f),
            Max = new Vector3(1f),
            ShatteredSubmeshes = new Dictionary<string, int>(),
        };
    }

    /// <summary>A closed cube of half-extent <paramref name="half"/>, each face a 2x2 grid of quads, drawn with skin.</summary>
    private static ModelParts Cube(float half)
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
            if (sign < 0) (u, v) = (v, u);
            int baseVertex = pos.Count / 3;
            for (int iu = 0; iu <= 2; iu++)
            for (int iv = 0; iv <= 2; iv++)
            {
                var p = n * half + u * ((iu - 1) * half) + v * ((iv - 1) * half);
                pos.AddRange([p.X, p.Y, p.Z]);
                nrm.AddRange([n.X, n.Y, n.Z]);
            }
            for (int iu = 0; iu < 2; iu++)
            for (int iv = 0; iv < 2; iv++)
            {
                int p00 = baseVertex + iu * 3 + iv, p10 = baseVertex + (iu + 1) * 3 + iv;
                int p01 = baseVertex + iu * 3 + iv + 1, p11 = baseVertex + (iu + 1) * 3 + iv + 1;
                tris.AddRange([p00, p10, p11, p00, p11, p01]);
            }
        }
        var tri = tris.ToArray();
        return Parts(pos.ToArray(), nrm.ToArray(), (0, Skin, tri));
    }
}
