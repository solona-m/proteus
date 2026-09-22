using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Refitting across bodies: the cloth's body weights come from the new body, every bone of the garment's own keeps
/// its weights, and no vertex ever has more than eight influences.
/// </summary>
public class BodyWeightsTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";
    private const string Cloth = "/mt_c0201e6255_top_a.mtrl";
    private const string Piercings = "/mt_c0201b0001_piercings.mtrl";

    private static readonly IReadOnlySet<string> BodyBones = new HashSet<string> { "j_kosi", "j_mune_l", "j_mune_r" };

    // ── the rule for one vertex ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_vertex_on_the_body_takes_the_new_body_s_weights()
    {
        var w = BodyRetarget.Combine([("j_kosi", 1f)], [("j_mune_l", 0.6f), ("j_kosi", 0.4f)], BodyBones, out bool cut)!;

        Assert.False(cut);
        Assert.Equal(0.6f, w.Single(i => i.Bone == "j_mune_l").W, 3);
        Assert.Equal(0.4f, w.Single(i => i.Bone == "j_kosi").W, 3);
    }

    [Fact]
    public void A_vertex_on_the_garment_s_own_bones_is_not_touched()
    {
        Assert.Null(BodyRetarget.Combine([("j_sk_b_a_l", 1f)], [("j_mune_l", 1f)], BodyBones, out _));
    }

    [Fact]
    public void A_blended_vertex_keeps_its_own_share_and_the_body_takes_the_rest()
    {
        var w = BodyRetarget.Combine([("j_sk_b_a_l", 0.3f), ("j_kosi", 0.7f)],
                                     [("j_mune_l", 0.5f), ("j_mune_r", 0.5f)], BodyBones, out _)!;

        Assert.Equal(("j_sk_b_a_l", 0.3f), w[0]);                              // first, and exactly
        Assert.Equal(0.35f, w.Single(i => i.Bone == "j_mune_l").W, 3);
        Assert.Equal(0.35f, w.Single(i => i.Bone == "j_mune_r").W, 3);
        Assert.DoesNotContain(w, i => i.Bone == "j_kosi");
    }

    [Fact]
    public void Six_skirt_influences_leave_room_for_two_body_ones()
    {
        var mine = Enumerable.Range(0, 6).Select(k => ($"j_sk_{k}", 0.1f)).Append(("j_kosi", 0.4f)).ToList();
        var body = Enumerable.Range(0, 5).Select(k => ($"j_mune_{k}", 0.1f + k * 0.05f)).ToList();
        var bones = new HashSet<string>(body.Select(b => b.Item1)) { "j_kosi" };

        var w = BodyRetarget.Combine(mine, body, bones, out bool cut)!;

        Assert.True(cut);
        Assert.Equal(8, w.Length);
        Assert.Equal(6, w.Count(i => i.Bone.StartsWith("j_sk_", StringComparison.Ordinal)));
        Assert.All(w.Where(i => i.Bone.StartsWith("j_sk_", StringComparison.Ordinal)), i => Assert.Equal(0.1f, i.W, 5));
        // The two strongest body influences, sharing the body's 0.4.
        Assert.Equal(["j_mune_4", "j_mune_3"], w.Skip(6).Select(i => i.Bone));
        Assert.Equal(0.4f, w.Skip(6).Sum(i => i.W), 4);
    }

    [Fact]
    public void Eight_skirt_influences_take_the_whole_vertex()
    {
        var mine = Enumerable.Range(0, 8).Select(k => ($"j_sk_{k}", 0.1f)).Append(("j_kosi", 0.2f)).ToList();
        var w = BodyRetarget.Combine(mine, [("j_mune_l", 1f)], BodyBones, out bool cut)!;

        Assert.True(cut);
        Assert.Equal(8, w.Length);
        Assert.All(w, i => Assert.StartsWith("j_sk_", i.Bone));
        Assert.Equal(1f, w.Sum(i => i.W), 4);
    }

    // ── through a real model file ──────────────────────────────────────────────────────────────────────

    /// <summary>A body: one skin mesh whose every vertex follows <paramref name="weights"/>.</summary>
    private static byte[] Body(params (string, float)[] weights)
        => SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3,
                                                                                         Weights: weights)));

    /// <summary>The refitted slot: from <paramref name="source"/> onto <paramref name="target"/>.</summary>
    private static BodyRetarget.SlotPair Pair(byte[] source, byte[] target)
    {
        var t = ModelPartReader.Read(target)!;
        Assert.True(IdentityCorrespondence.TryBuild(t, t, "chest", out var built, out string why), why);
        return new BodyRetarget.SlotPair("_top", built!, t, target, source);
    }

    /// <summary>Every vertex's influences, by bone name, in the part reader's order.</summary>
    private static List<(string Bone, float W)>[] Weights(byte[] mdl)
    {
        var skin = ModelSkinReader.Read(mdl, null, null)!;
        var all = new List<(string, float)>[skin.VertexCount];
        for (int v = 0; v < skin.VertexCount; v++)
        {
            all[v] = [];
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float w = skin.BoneWeights[v * 8 + k];
                if (w > 0f) all[v].Add((skin.BoneNames[skin.BoneIndices[v * 8 + k]], w));
            }
        }
        return all;
    }

    private static int[] VerticesOf(byte[] mdl, string material, int sub)
        => ModelPartReader.Read(mdl)!.Parts.Where(p => p.Island < 0 && p.Material == material)
                                          .ElementAt(sub).Triangles.Distinct().ToArray();

    [Fact]
    public void Refitting_across_rigs_rewrites_the_cloth_and_keeps_the_garment_s_own_bones()
    {
        var source = Body(("j_kosi", 1f));
        var target = Body(("j_mune_l", 0.6f), ("j_kosi", 0.4f));
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: [("j_kosi", 1f)])),
            new SyntheticModel.Mesh(Cloth,
                new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 0.001f, Weights: [("j_kosi", 1f)]),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 0.001f, Weights: [("j_sk_b_a_l", 1f)]),
                new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 0.001f,
                                       Weights: [("j_sk_b_a_l", 0.5f), ("j_kosi", 0.5f)])));
        var pairs = new[] { Pair(source, target) };

        var plan = BodyRetarget.PlanWeights(garment, pairs);
        Assert.NotNull(plan);
        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, plan, out var report)!;
        Assert.Equal(0, report.Unplaced);

        var before = Weights(garment);
        var after = Weights(rebuilt);

        // On the body: the new body's weights, j_mune_l included although the garment never had that bone.
        Assert.Contains("j_mune_l", SecondSkinWriter.Parse(rebuilt).BoneNames);
        foreach (int v in VerticesOf(garment, Cloth, 0))
        {
            Assert.Equal(0.6f, after[v].Single(i => i.Bone == "j_mune_l").W, 2);
            Assert.Equal(0.4f, after[v].Single(i => i.Bone == "j_kosi").W, 2);
        }

        // On the skirt alone: exactly as it was.
        foreach (int v in VerticesOf(garment, Cloth, 1))
            Assert.Equal(before[v], after[v]);

        // Half skirt, half body: the skirt's half kept, the body's half from the new body.
        foreach (int v in VerticesOf(garment, Cloth, 2))
        {
            Assert.Equal(0.5f, after[v].Single(i => i.Bone == "j_sk_b_a_l").W, 2);
            Assert.Equal(0.3f, after[v].Single(i => i.Bone == "j_mune_l").W, 2);
            Assert.Equal(0.2f, after[v].Single(i => i.Bone == "j_kosi").W, 2);
        }
    }

    [Fact]
    public void A_vertex_needing_more_than_four_influences_is_widened_to_eight()
    {
        var source = Body(("j_kosi", 1f));
        var target = Body(("j_mune_l", 0.25f), ("j_mune_r", 0.25f), ("j_kosi", 0.25f), ("j_sebo_b", 0.25f));
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 0.001f,
                Weights: [("j_sk_b_a_l", 0.2f), ("j_sk_b_a_r", 0.2f), ("j_kosi", 0.6f)])));
        var pairs = new[] { Pair(source, target) };

        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, BodyRetarget.PlanWeights(garment, pairs),
                                           out _)!;

        var after = Weights(rebuilt);
        Assert.All(VerticesOf(garment, Cloth, 0), v =>
        {
            Assert.Equal(6, after[v].Count);                                    // two skirt, four body
            Assert.True(after[v].Count <= 8);
            Assert.Equal(1f, after[v].Sum(i => i.W), 2);
        });
        var decl = SecondSkinWriter.Parse(rebuilt).Decls[0];
        Assert.Contains(decl, e => e.Usage == SecondSkinWriter.UseBlendWeight && e.Type == 17);
    }

    [Fact]
    public void The_bytes_always_come_to_255_even_when_the_first_influence_is_tiny()
    {
        // A kept skirt influence of 0.4% leads (one byte); four body weights each round UP. The error must not land on
        // the one-byte influence, which cannot absorb it.
        (string, float)[] w = [("j_sk_b_a_l", 0.004f), ("a", 0.2495f), ("b", 0.2495f), ("c", 0.2495f), ("d", 0.2495f)];
        var names = w.Select(i => i.Item1).ToList();
        Span<byte> wb = stackalloc byte[8], ib = stackalloc byte[8];
        int dropped = 0;

        int used = SecondSkinWriter.EncodeBlend(w, 8, bone => names.IndexOf(bone), wb, ib, ref dropped);

        Assert.Equal(5, used);
        int sum = 0;
        for (int k = 0; k < 8; k++) sum += wb[k];
        Assert.Equal(255, sum);
        Assert.Equal(1, wb[0]);                                                // the skirt's byte, untouched
    }

    [Fact]
    public void Between_body_mods_the_cloth_is_reweighted_even_when_the_rigs_match()
    {
        // YAB's and Rue's plain sizes rig the same bones; each weights them to its own shape.
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: [("j_kosi", 1f)])),
            new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: [("j_kosi", 1f)])));
        var pairs = new[] { Pair(Body(("j_kosi", 0.7f), ("j_mune_l", 0.3f)), Body(("j_mune_l", 0.6f), ("j_kosi", 0.4f))) };

        Assert.Null(BodyRetarget.PlanWeights(garment, pairs));                // within one body mod: the author's weights
        var plan = BodyRetarget.PlanWeights(garment, pairs, acrossBodies: true);
        Assert.NotNull(plan);

        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, plan, out var report)!;
        Assert.Equal(2, report.ExtrasDropped);                                // and the old body's piercings go
        // The bodies differ by 0.3 moved from j_kosi to j_mune_l, and the cloth takes that change: 0.7 and 0.3, not
        // the new body's 0.4 and 0.6 outright.
        foreach (int v in VerticesOf(garment, Cloth, 0))
        {
            Assert.Equal(0.3f, Weights(rebuilt)[v].Single(i => i.Bone == "j_mune_l").W, 2);
            Assert.Equal(0.7f, Weights(rebuilt)[v].Single(i => i.Bone == "j_kosi").W, 2);
        }
    }

    [Fact]
    public void Where_the_two_bodies_agree_the_author_s_weighting_stands()
    {
        // TBSE and TBSE-X weight their shared bones alike; the author weighted a loose shirt by hand, unlike the skin.
        var body = new (string, float)[] { ("j_kosi", 0.7f), ("j_mune_l", 0.3f) };
        var author = new List<(string, float)> { ("j_sebo_c", 0.8f), ("j_kosi", 0.2f) };

        var changed = BodyRetarget.Change(author, body, body, new HashSet<string>(BodyBones) { "j_sebo_c" });

        Assert.Equal(0.8f, changed.Single(i => i.Bone == "j_sebo_c").W, 4);
        Assert.Equal(0.2f, changed.Single(i => i.Bone == "j_kosi").W, 4);
        Assert.DoesNotContain(changed, i => i.Bone == "j_mune_l");
    }

    [Fact]
    public void A_bone_the_new_body_adds_takes_its_share_from_what_the_old_body_had_there()
    {
        // TBSE-X hands part of the chest to its pec bone; the cloth over it follows, the rest of its weighting kept.
        var oldBody = new (string, float)[] { ("j_mune_l", 1f) };
        var newBody = new (string, float)[] { ("j_mune_l", 0.8f), ("iv_kyokin_phys_l", 0.2f) };
        var author = new List<(string, float)> { ("j_mune_l", 0.5f), ("j_sebo_c", 0.5f) };
        var bones = new HashSet<string>(BodyBones) { "iv_kyokin_phys_l", "j_sebo_c" };

        var changed = BodyRetarget.Change(author, oldBody, newBody, bones);

        Assert.Equal(0.3f, changed.Single(i => i.Bone == "j_mune_l").W, 4);
        Assert.Equal(0.5f, changed.Single(i => i.Bone == "j_sebo_c").W, 4);
        Assert.Equal(0.2f, changed.Single(i => i.Bone == "iv_kyokin_phys_l").W, 4);
        Assert.Equal(1f, changed.Sum(i => i.W), 4);
    }

    [Fact]
    public void A_change_that_would_go_below_zero_stops_there_and_the_body_share_is_kept()
    {
        // The old body put the vertex on j_mune_l, which the author never used; the new body moves it to iv_c_mune.
        var oldBody = new (string, float)[] { ("j_mune_l", 1f) };
        var newBody = new (string, float)[] { ("iv_c_mune_l", 1f) };
        var author = new List<(string, float)> { ("j_sebo_c", 0.6f), ("j_sk_f_a_l", 0.4f) };
        var bones = new HashSet<string>(BodyBones) { "iv_c_mune_l", "j_sebo_c" };

        var changed = BodyRetarget.Change(author, oldBody, newBody, bones);
        Assert.DoesNotContain(changed, i => i.Bone == "j_mune_l");            // -0.6 stops at nothing
        var w = BodyRetarget.Combine(author, changed, bones, out _)!;

        Assert.Equal(0.4f, w.Single(i => i.Bone == "j_sk_f_a_l").W, 4);       // the skirt, exactly
        Assert.Equal(0.6f, w.Where(i => i.Bone != "j_sk_f_a_l").Sum(i => i.W), 4);   // the body keeps its 0.6
        Assert.Equal(1f, w.Sum(i => i.W), 4);
    }

    [Fact]
    public void Two_bodies_rigged_alike_keep_the_author_s_weights()
    {
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: [("j_kosi", 1f)])));
        var pairs = new[] { Pair(Body(("j_kosi", 1f)), Body(("j_kosi", 1f))) };

        Assert.Null(BodyRetarget.PlanWeights(garment, pairs));
    }

    [Fact]
    public void The_old_body_s_piercings_are_left_out_of_a_refit_across_bodies()
    {
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: [("j_kosi", 1f)])),
            new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, Weights: [("j_kosi", 1f)])));
        var pairs = new[] { Pair(Body(("j_kosi", 1f)), Body(("j_mune_l", 1f))) };

        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, BodyRetarget.PlanWeights(garment, pairs),
                                           out var report)!;

        Assert.Equal(2, report.ExtrasDropped);
        Assert.DoesNotContain(ModelPartReader.Read(rebuilt)!.Parts, p => p.Material == Piercings);
        Assert.True(BodyRetarget.IsBodyExtraMaterial("/mt_c0201b0001_bibopube.mtrl"));
        Assert.True(BodyRetarget.IsBodyExtraMaterial("/mt_c0201b0001_neolithe_piercings.mtrl"));
        Assert.False(BodyRetarget.IsBodyExtraMaterial(Skin));
        Assert.False(BodyRetarget.IsBodyExtraMaterial("/mt_c0201e0000_top_neolithe_undies.mtrl"));
    }

    [Fact]
    public void Two_bodies_a_whole_tile_apart_in_uv_still_pair()
    {
        // Bibo+ stores its legs at v -0.67..-0.02 where Neolithe stores them at 0.02..0.98. A sheet repeats, so those
        // are the same texels; compared as stored they had nothing in common and the pair was refused.
        var body = ModelPartReader.Read(SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 4))))!;
        int vc = body.Positions.Length / 3;
        var uv = new float[vc * 2];
        for (int v = 0; v < vc; v++) { uv[v * 2] = 0.1f + 0.2f * (v % 3); uv[v * 2 + 1] = 0.2f + 0.15f * (v % 4); }
        var shifted = uv.Select((x, i) => i % 2 == 1 ? x - 1f : x).ToArray();

        Assert.True(BodyCorrespondence.TryBuild(body, shifted, body, uv, "legs", out var c, out string why), why);
        Assert.Contains("100.0%", c!.Describe());
    }

    [Fact]
    public void A_held_part_keeps_the_bones_the_author_gave_it()
    {
        // Unticking a part holds it where the author put it. It has to hold the bones too: a rigid heel held in place
        // but reweighted stands still in the bind pose and flies apart in a posed one.
        var source = Body(("j_asi_d_l", 1f));
        var target = Body(("j_asi_e_l", 1f));
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 0.001f,
                                                                  Weights: [("j_asi_d_l", 1f)])));
        var pairs = new[] { Pair(source, target) };
        var mine = VerticesOf(garment, Cloth, 0);

        Assert.NotNull(BodyRetarget.PlanWeights(garment, pairs, acrossBodies: true));
        var plan = BodyRetarget.PlanWeights(garment, pairs, acrossBodies: true, held: mine.ToHashSet());

        Assert.Equal(0, plan?.Reweighted ?? 0);
        Assert.All(mine, v => Assert.Null(plan?.For(0)?[v]));
    }

    [Fact]
    public void Cloth_out_of_reach_of_the_body_keeps_the_weights_it_had()
    {
        // A thigh-high stocking refitted on the FEET slot: the body is the foot, and the cloth up the leg is nowhere
        // near it. Weighting that cloth to the foot's bones is what tore a stocking apart.
        var source = Body(("j_asi_d_l", 1f));
        var target = Body(("j_asi_e_l", 1f));
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 0.001f,
                                                                  Weights: [("j_asi_d_l", 1f)])),
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: 0.5f,
                                                                  Weights: [("j_asi_d_l", 1f)])));
        var pairs = new[] { Pair(source, target) };

        var plan = BodyRetarget.PlanWeights(garment, pairs, acrossBodies: true)!;
        var near = VerticesOf(garment, Cloth, 0);
        var far = VerticesOf(garment, Cloth, 1);
        var rebuilt = BodyRetarget.Rebuild(garment, pairs, swapSkin: false, plan, out _)!;
        var w = Weights(rebuilt);

        // On the body: it follows the new body's bone. Half a metre off it: untouched.
        Assert.All(near, v => Assert.Equal("j_asi_e_l", w[v].OrderByDescending(i => i.W).First().Bone));
        Assert.All(far, v => Assert.Equal([("j_asi_d_l", 1f)], w[v]));
    }

    [Fact]
    public void Bodies_in_different_texture_layouts_with_no_map_between_them_are_refused()
    {
        var bibo = ModelPartReader.Read(SyntheticModel.Build([],
            new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3))))!;
        var gen3 = ModelPartReader.Read(SyntheticModel.Build([],
            new SyntheticModel.Mesh("/mt_c0201b0001_eve.mtrl", new SyntheticModel.Sub(0, TrianglesPerIsland: 3,
                                                                                    OffsetX: 0.5f))))!;
        var uv = new float[gen3.Positions.Length / 3 * 2];

        Assert.Equal("bibo", BodyCorrespondence.LayoutOf(bibo));
        Assert.Equal("gen3", BodyCorrespondence.LayoutOf(gen3));
        // No maps on disk for this service: bibo to gen3 has nothing to go through.
        var uvRemap = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        Assert.False(BodyCorrespondence.TryBuild(gen3, uv, bibo, uv, "chest", out _, out string refusal, uvRemap));
        Assert.Contains("different texture layouts", refusal);
    }
}
