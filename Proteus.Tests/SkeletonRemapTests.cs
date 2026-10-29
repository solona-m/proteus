using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The bones a garment leans on that the race it is refitted for lacks, moved onto ones it has (cross-sex-tops-yas): where
/// each goes, and the rename that moves it.
/// </summary>
public class SkeletonRemapTests
{
    private const string Cloth = "/mt_c0201e6212_dwn_a.mtrl";
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";

    private static HashSet<string> Bones(params string[] names) => new(names, StringComparer.Ordinal);

    private static Func<string, string?> Parents(params (string Bone, string Parent)[] links)
    {
        var map = links.ToDictionary(l => l.Bone, l => l.Parent, StringComparer.Ordinal);
        return bone => map.TryGetValue(bone, out var parent) ? parent : null;
    }

    private static SkeletonRemap.Move Only(SkeletonRemap.Planner planner, string bone)
        => Assert.Single(planner.MovesFor([bone]));

    // ── where each bone goes ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-3")]
    public void A_yas_bone_moves_to_its_ivcs_counterpart() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-3]
    {
        var planner = new SkeletonRemap.Planner(Bones("j_kosi", "iv_shiri_l", "iv_shiri_r"), Parents(("ya_shiri_phys_l", "j_kosi")));
        Assert.Equal(new SkeletonRemap.Move("ya_shiri_phys_l", "iv_shiri_l", SkeletonRemap.How.Counterpart),
                     Only(planner, "ya_shiri_phys_l"));
        Assert.Equal("iv_shiri_r", Only(planner, "ya_shiri_phys_r").To);
        // A bone the wearer has is never moved.
        Assert.Empty(planner.MovesFor(["iv_shiri_l", "j_kosi"]));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-4")]
    public void Without_the_counterpart_the_nearest_ancestor_takes_it() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-4]
    {
        var yas = Parents(("ya_daitai_phys_l", "iv_daitai_phys_l"), ("iv_daitai_phys_l", "j_asi_a_l"), ("j_asi_a_l", "j_kosi"),
                          ("ya_shiri_phys_l", "iv_shiri_l"), ("iv_shiri_l", "j_kosi"));
        var game = new SkeletonRemap.Planner(Bones("j_kosi", "j_asi_a_l"), yas);
        Assert.Equal(new SkeletonRemap.Move("ya_daitai_phys_l", "j_asi_a_l", SkeletonRemap.How.Ancestor),
                     Only(game, "ya_daitai_phys_l"));
        // The game's men's skeleton has no iv_shiri: YAS's buttock bone goes up to the hip.
        Assert.Equal(new SkeletonRemap.Move("ya_shiri_phys_l", "j_kosi", SkeletonRemap.How.Ancestor), Only(game, "ya_shiri_phys_l"));
    }

    /// <summary>
    /// A women's glove made a men's: IVCS's third finger joints, which the game's men's skeleton lacks and whose parents it
    /// does not know, stand in on the same finger's last joint the game has — never the hip, which took the fingertips
    /// with it (Stormblessed's gloves, 2026-10-29). A toe's joint still goes to the foot.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-mens-ends-crotch-2")]
    public void A_finger_joint_the_skeleton_lacks_stands_in_on_its_finger() // [AC-mens-ends-crotch-2]
    {
        var wearer = Bones("j_kosi", "j_te_l", "j_te_r", "j_hito_b_l", "j_oya_b_r", "j_naka_b_l", "j_kusu_b_r", "j_ko_b_l",
                           "j_asi_e_l");
        var planner = new SkeletonRemap.Planner(wearer, _ => null);
        Assert.Equal(new SkeletonRemap.Move("iv_hito_c_l", "j_hito_b_l", SkeletonRemap.How.StandIn), Only(planner, "iv_hito_c_l"));
        Assert.Equal("j_oya_b_r", Only(planner, "iv_oya_c_r").To);
        Assert.Equal("j_naka_b_l", Only(planner, "iv_naka_c_l").To);
        Assert.Equal("j_kusu_b_r", Only(planner, "iv_kusu_c_r").To);
        Assert.Equal("j_ko_b_l", Only(planner, "iv_ko_c_l").To);
        Assert.Equal("j_ko_b_l", Only(planner, "iv_ko_d_l").To);
        Assert.Equal("j_asi_e_l", Only(planner, "iv_asi_ko_a_l").To);
        // Not a finger joint past the game's: no stand-in.
        Assert.Null(SkeletonRemap.StandIn("iv_hito_l"));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-5")]
    public void A_bone_no_skeleton_knows_stands_in_by_region() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-5]
    {
        var wearer = Bones("j_kosi", "j_mune_l", "j_mune_r", "j_asi_a_l", "j_asi_a_r", "j_sebo_a", "j_sebo_b", "j_asi_e_l");
        var planner = new SkeletonRemap.Planner(wearer, _ => null);
        Assert.Equal(new SkeletonRemap.Move("iv_c_mune_l", "j_mune_l", SkeletonRemap.How.StandIn), Only(planner, "iv_c_mune_l"));
        Assert.Equal("j_mune_r", Only(planner, "iv_kyokin_phys_r").To);
        Assert.Equal("j_asi_a_r", Only(planner, "ya_daitai_phys_r").To);
        Assert.Equal("j_sebo_a", Only(planner, "ya_fukubu_phys").To);
        Assert.Equal("j_kosi", Only(planner, "iv_kintama_phys_l").To);
        Assert.Equal("j_asi_e_l", Only(planner, "iv_asi_oya_a_l").To);

        // An ancestor that would put a breast or thigh bone on the hip is passed over for its own part of the body.
        var hipParent = new SkeletonRemap.Planner(wearer, Parents(("iv_c_mune_l", "j_kosi"), ("ya_daitai_phys_l", "j_kosi")));
        Assert.Equal(new SkeletonRemap.Move("iv_c_mune_l", "j_mune_l", SkeletonRemap.How.StandIn), Only(hipParent, "iv_c_mune_l"));
        Assert.Equal(new SkeletonRemap.Move("ya_daitai_phys_l", "j_asi_a_l", SkeletonRemap.How.StandIn),
                     Only(hipParent, "ya_daitai_phys_l"));
        Assert.False(SkeletonRemap.KeepsItsPart("iv_c_mune_l", "j_kosi"));
        Assert.False(SkeletonRemap.KeepsItsPart("ya_daitai_phys_l", "j_asi_a_r"));
        Assert.True(SkeletonRemap.KeepsItsPart("ya_shiri_phys_l", "j_kosi"));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-6")]
    public void A_bone_nothing_knows_falls_back_to_the_hip_and_is_said() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-6]
    {
        var planner = new SkeletonRemap.Planner(Bones("j_kosi"), _ => null);
        var move = Only(planner, "foo_cape_a");
        Assert.Equal(new SkeletonRemap.Move("foo_cape_a", SkeletonRemap.Fallback, SkeletonRemap.How.Fallback), move);
        Assert.Equal("foo_cape_a → j_kosi", SkeletonRemap.Describe([move]));
        Assert.Equal("foo_cape_a → j_kosi (fallback)", SkeletonRemap.DescribeHow([move]));
        Assert.True(CrossSexRefitReport.WantsFromSkeleton([move]));
        Assert.False(CrossSexRefitReport.WantsFromSkeleton([new("ya_shiri_phys_l", "iv_shiri_l", SkeletonRemap.How.Counterpart)]));
        Assert.Contains("pass --from-skeleton", CrossSexRefitReport.FromSkeletonHint);
        Assert.Contains("Console.Error.WriteLine(CrossSexRefitReport.FromSkeletonHint)",
                        File.ReadAllText(SourceFile("Proteus.Refit/CrossSexCommand.cs")));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-15")]
    public void A_cyclic_parent_chain_ends() // [EDGE-cross-sex-tops-yas-15]
    {
        var planner = new SkeletonRemap.Planner(Bones("j_kosi", "j_sebo_a"), Parents(("a_fukubu", "b"), ("b", "a_fukubu")));
        Assert.Equal(new SkeletonRemap.Move("a_fukubu", "j_sebo_a", SkeletonRemap.How.StandIn), Only(planner, "a_fukubu"));
        var nothing = new SkeletonRemap.Planner(Bones("j_kosi"), Parents(("x", "y"), ("y", "x")));
        Assert.Equal(SkeletonRemap.How.Fallback, Only(nothing, "x").How);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    public void The_planner_never_leaves_a_bone_missing() // [AC-cross-sex-tops-yas-4]
    {
        var random = new Random(1345);
        string[] pool = ["ya_shiri_phys_l", "ya_shiri_phys_r", "ya_daitai_phys_l", "ya_daitai_phys_r", "ya_fukubu_phys",
                         "iv_daitai_phys_l", "iv_fukubu_phys", "iv_c_mune_l", "iv_kintama_phys_r", "iv_shiri_l", "j_ex_top_a_l",
                         "foo_cape", "iv_asi_oya_a_r"];
        string[] all = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_mune_l", "j_mune_r", "j_asi_a_l", "j_asi_a_r",
                        "j_asi_e_r", "iv_shiri_l", "iv_shiri_r", "j_kao"];
        for (int run = 0; run < 200; run++)
        {
            var wearer = Bones([.. all.Where(b => b == "j_kosi" || random.NextDouble() < 0.6)]);
            var missing = pool.Where(_ => random.NextDouble() < 0.5).ToList();
            var moves = new SkeletonRemap.Planner(wearer, Parents(("ya_daitai_phys_l", "j_asi_a_l"), ("j_ex_top_a_l", "j_sebo_c")))
                .MovesFor(missing);
            Assert.All(moves, m => Assert.DoesNotContain(m.From, wearer));
            Assert.All(moves, m => Assert.Contains(m.To, wearer));
            var after = SkeletonRemap.Apply(missing, moves).ToList();
            Assert.All(after, b => Assert.Contains(b, wearer.Concat(missing.Where(wearer.Contains))));
        }
    }

    // ── the rename ───────────────────────────────────────────────────────────

    /// <summary>A skirt on YAS bones: cloth leaning on a YAS buttock bone and its IVCS twin, a belly piece wholly on YAS's
    /// belly bone, a skin mesh, and a shape key.</summary>
    private static byte[] YasSkirt()
        => SyntheticModel.Build([],
            [
                new SyntheticModel.Mesh(Cloth,
                    new SyntheticModel.Sub(0, TrianglesPerIsland: 3,
                                           Weights: [("ya_shiri_phys_l", 0.3f), ("iv_shiri_l", 0.2f), ("j_kosi", 0.5f)]),
                    new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 5f, Weights: [("ya_fukubu_phys", 1f)])),
                new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 3f,
                                                                     Weights: [("j_kosi", 1f)])),
            ],
            SyntheticModel.V6, shapeNames: ["shp_yas"]);

    private static readonly IReadOnlyList<SkeletonRemap.Move> YasMoves =
    [
        new("ya_fukubu_phys", "j_sebo_a", SkeletonRemap.How.StandIn),   // not in the garment's bone list
        new("ya_shiri_phys_l", "iv_shiri_l", SkeletonRemap.How.Counterpart),
    ];

    private static List<(string Bone, float W)>[] ByName(byte[] model)
    {
        var skin = ModelSkinReader.Read(model, null, null)!;
        var all = new List<(string, float)>[skin.VertexCount];
        for (int v = 0; v < skin.VertexCount; v++)
        {
            var sum = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float w = skin.BoneWeights[v * 8 + k];
                if (w > 0f) sum[skin.BoneNames[skin.BoneIndices[v * 8 + k]]] = sum.GetValueOrDefault(skin.BoneNames[skin.BoneIndices[v * 8 + k]]) + w;
            }
            all[v] = sum.Select(p => (p.Key, p.Value)).ToList();
        }
        return all;
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-7")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-8")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-9")]
    public void Rename_moves_weights_by_name_and_keeps_everything_else() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-7] [EDGE-cross-sex-tops-yas-8] [EDGE-cross-sex-tops-yas-9]
    {
        var before = YasSkirt();
        var after = SkeletonRemap.Rename(before, YasMoves, out string why);
        Assert.True(after != null, why);

        var names = SecondSkinWriter.Parse(after!).BoneNames;
        Assert.DoesNotContain("ya_shiri_phys_l", names);
        Assert.DoesNotContain("ya_fukubu_phys", names);
        Assert.Contains("j_sebo_a", names);
        Assert.Equal(SecondSkinWriter.Parse(before).BoneNames.Length, names.Length);   // renamed, nothing added or taken
        Assert.Equal(SecondSkinWriter.Parse(before).Shapes.Count, SecondSkinWriter.Parse(after!).Shapes.Count);
        Assert.Equal(ModelLodTrimmer.LodCount(before), ModelLodTrimmer.LodCount(after!));

        var a = ModelSkinReader.Read(before, null, null)!;
        var b = ModelSkinReader.Read(after!, null, null)!;
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.BoneIndices, b.BoneIndices);   // every vertex's slots and weights as they were
        Assert.Equal(a.BoneWeights, b.BoneWeights);
        Assert.DoesNotContain(OtherRaceRefit.Weighted(b), YasMoves.Select(m => m.From).Contains);

        var was = ByName(before);
        var now = ByName(after!);
        for (int v = 0; v < was.Length; v++)
        {
            var want = SkeletonRemap.Apply(was[v], YasMoves).ToDictionary(i => i.Bone, i => i.W);
            Assert.Equal(want.Count, now[v].Count);
            foreach (var (bone, w) in now[v]) Assert.Equal(want[bone], w, 6);
            Assert.Equal(1f, now[v].Sum(i => i.W), 2);
        }
        // The two buttock bones are one now: 0.3 + 0.2 on iv_shiri_l.
        Assert.Contains(now, w => w.Any(i => i.Bone == "iv_shiri_l" && MathF.Abs(i.W - 0.5f) < 0.01f));
        // The belly piece follows the spine entirely.
        Assert.Contains(now, w => w.Count == 1 && w[0].Bone == "j_sebo_a");
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-10")]
    public void A_rename_that_does_not_match_its_moves_fails_verify() // [EDGE-cross-sex-tops-yas-10]
    {
        var before = YasSkirt();
        var renamed = SkeletonRemap.Rename(before, YasMoves, out _)!;
        Assert.True(SkeletonRemap.Verify(before, renamed, YasMoves, out _));

        // Renamed one way, checked against another.
        var other = ModelAttributeWriter.RenameBone(before, "ya_shiri_phys_l", "j_kosi");
        Assert.False(SkeletonRemap.Verify(before, other, YasMoves, out string why));
        Assert.NotEmpty(why);

        // A bone left with its old name.
        var half = ModelAttributeWriter.RenameBone(before, "ya_shiri_phys_l", "iv_shiri_l");
        Assert.False(SkeletonRemap.Verify(before, half, YasMoves, out why));
        Assert.Contains("ya_fukubu_phys", why);

        // A vertex moved.
        var moved = (byte[])renamed.Clone();
        var parsed = SecondSkinWriter.Parse(moved);
        int at = parsed.Vb;   // the first vertex's x, at the start of stream 0
        moved[at + 3] ^= 0x40;
        Assert.False(SkeletonRemap.Verify(before, moved, YasMoves, out why));
    }

    /// <summary>A model that already names the moved bone twice has both entries renamed, not refused.</summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-8")]
    public void A_bone_named_twice_is_renamed_everywhere() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-8]
    {
        var twice = ModelAttributeWriter.RenameBone(YasSkirt(), "iv_shiri_l", "ya_shiri_phys_l");
        Assert.Equal(2, SecondSkinWriter.Parse(twice).BoneNames.Count(b => b == "ya_shiri_phys_l"));
        var after = SkeletonRemap.Rename(twice, YasMoves, out string why);
        Assert.True(after != null, why);
        Assert.DoesNotContain("ya_shiri_phys_l", SecondSkinWriter.Parse(after!).BoneNames);
        Assert.Equal(2, SecondSkinWriter.Parse(after!).BoneNames.Count(b => b == "iv_shiri_l"));
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-24")]
    public void A_rename_refusal_reaches_the_user() // [EDGE-cross-sex-tops-yas-24]
    {
        Assert.Null(SkeletonRemap.Rename(new byte[64], YasMoves, out string refusal));
        Assert.NotEmpty(refusal);
        Assert.Contains("the bones could not be renamed (x)", SkeletonRemap.RenameRefusal("x"));
        Assert.Contains("return Refused(SkeletonRemap.RenameRefusal(refusal));",
                        File.ReadAllText(SourceFile("Proteus/Services/CrossSexRefitJob.cs")));
        Assert.Contains("return (key, null, SkeletonRemap.RenameRefusal(refusal));",
                        File.ReadAllText(SourceFile("Proteus/Gui/BodyRetargetPanel.Race.cs")));
    }

    // ── through the weight plan ──────────────────────────────────────────────

    private static byte[] Body(params (string, float)[] weights)
        => SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: weights)));

    private static BodyRetarget.SlotPair Pair(byte[] source, byte[] target)
    {
        var t = ModelPartReader.Read(target)!;
        Assert.True(IdentityCorrespondence.TryBuild(t, t, "legs", out var built, out string why), why);
        return new BodyRetarget.SlotPair("_dwn", built!, t, target, source);
    }

    /// <summary>
    /// A skirt whose YAS buttock bone was renamed onto its IVCS twin names that bone twice; every pass of the weight plan
    /// reads them added up (once one built a dictionary by name and threw), so the refit is that of the skirt with the
    /// two merged.
    /// </summary>
    [Theory]
    [InlineData(0.001f)]
    [InlineData(0.015f)]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-18")]
    public void Duplicate_bone_names_are_summed_through_the_weight_plan(float off) // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-18]
    {
        var source = Body(("j_asi_a_l", 0.6f), ("j_kosi", 0.2f), ("iv_shiri_l", 0.2f));
        var target = Body(("j_asi_a_l", 0.3f), ("j_kosi", 0.4f), ("iv_shiri_l", 0.3f));
        byte[] Skirt(params (string, float)[] weights) => SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: off, Weights: weights)));
        var renamed = SkeletonRemap.Rename(Skirt(("j_kosi", 0.5f), ("j_sk_f_a_l", 0.2f), ("ya_shiri_phys_l", 0.1f), ("iv_shiri_l", 0.2f)),
                                           [new("ya_shiri_phys_l", "iv_shiri_l", SkeletonRemap.How.Counterpart)], out string why);
        Assert.True(renamed != null, why);
        Assert.Contains(SkeletonRemap.DuplicateBones(renamed!), b => b == "iv_shiri_l");
        var merged = Skirt(("j_kosi", 0.5f), ("j_sk_f_a_l", 0.2f), ("iv_shiri_l", 0.3f));
        var pairs = new[] { Pair(source, target) };

        byte[] Refit(byte[] garment)
            => BodyRetarget.Rebuild(garment, pairs, swapSkin: false,
                                    BodyRetarget.PlanWeights(garment, pairs, acrossBodies: true, before: garment), out _)!;
        var one = ByName(Refit(renamed!));
        var two = ByName(Refit(merged));
        Assert.Equal(two.Length, one.Length);
        for (int v = 0; v < one.Length; v++)
        {
            var want = two[v].ToDictionary(i => i.Bone, i => i.W);
            foreach (var (bone, w) in one[v]) Assert.Equal(want.GetValueOrDefault(bone), w, 2);
            Assert.Equal(1f, one[v].Sum(i => i.W), 2);
        }

        var skin = ModelSkinReader.Read(renamed!, null, null)!;
        var influences = BodyRetarget.Influences(skin, 0);
        Assert.Single(influences, i => i.Bone == "iv_shiri_l");
        Assert.Equal(0.3f, influences.Single(i => i.Bone == "iv_shiri_l").W, 2);
    }

    // ── the bodies it was made on ────────────────────────────────────────────

    /// <summary>
    /// A made-for body rigged to YAS's buttock bone is renamed as the garment is before it bends — not refused, not left
    /// unbent — bent or copied alike, its file named for the moves; a body with nothing to move keeps its old name.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-9")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-13")]
    public void Made_for_bodies_are_renamed_before_they_bend() // [AC-cross-sex-tops-yas-9] [EDGE-cross-sex-tops-yas-13]
    {
        string root = Path.Combine(Path.GetTempPath(), $"proteus-remap-{Guid.NewGuid():N}");
        string yiggle = Path.Combine(root, "yiggle"), plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(yiggle);
        Directory.CreateDirectory(plain);
        try
        {
            File.WriteAllBytes(Path.Combine(yiggle, "c0101e0000_top.mdl"), Body(("j_kosi", 0.7f), ("ya_shiri_phys_l", 0.3f)));
            File.WriteAllBytes(Path.Combine(plain, "c0101e0000_top.mdl"), Body(("j_kosi", 1f)));
            var catalog = new BodySizeCatalog(root,
            [
                new BodyOption("Body", "", "Yiggle", "yiggle/c0101e0000_top.mdl", "chara/equipment/e0000/model/c0101e0000_top.mdl", "_top"),
                new BodyOption("Body", "", "Plain", "plain/c0101e0000_top.mdl", "chara/equipment/e0000/model/c0101e0000_top.mdl", "_top"),
            ]);
            var tree = SyntheticSkeleton.Human("c0301");
            var who = new RacialModelBake.Wearer(301, HavokSkeleton(tree), new XivLiveMesh.PbdFile(SyntheticSkeleton.HighlanderPbd()));
            var planner = new SkeletonRemap.Planner(new HashSet<string>(tree.Bones, StringComparer.Ordinal),
                                                    Parents(("ya_shiri_phys_l", "j_kosi")));
            var logged = new List<string>();
            var bent = BakedBodyCatalog.Of(catalog, "0101", 301, who, ["_top"], out var failed, planner: planner, log: logged.Add);
            Assert.Empty(failed);
            Assert.Equal(2, bent.For("_top").Count);
            string yiggleOut = bent.PathOf(bent.For("_top").Single(o => o.Name == "Yiggle"));
            string plainOut = bent.PathOf(bent.For("_top").Single(o => o.Name == "Plain"));
            Assert.DoesNotContain("ya_shiri_phys_l", OtherRaceRefit.Weighted(ModelSkinReader.Read(File.ReadAllBytes(yiggleOut), null, null)!));
            Assert.Contains("-r", Path.GetFileName(yiggleOut));
            Assert.DoesNotContain("-r", Path.GetFileName(plainOut));
            Assert.Contains(logged, l => l.Contains("ya_shiri_phys_l → j_kosi"));
            // The rigs the two were made with still tell them apart.
            Assert.Contains("ya_shiri_phys_l", BakedBodyCatalog.RiggedBefore(yiggleOut)!);
            Assert.Null(BakedBodyCatalog.RiggedBefore(plainOut));

            // Unbent — the body already the race's — it is renamed all the same.
            var same = BakedBodyCatalog.Of(catalog, "0101", 101, who, ["_top"], out failed, planner: planner);
            Assert.DoesNotContain("ya_shiri_phys_l", OtherRaceRefit.Weighted(ModelSkinReader.Read(
                File.ReadAllBytes(same.PathOf(same.For("_top").Single(o => o.Name == "Yiggle"))), null, null)!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Rue's plain and Yiggle sizes are one mesh rigged two ways; bent for a man, the Yiggle size's YAS bones are moved
    /// onto the plain size's own, and the two would read as one body. Ranked by the rigs they were made with
    /// (BakedBodyCatalog.RiggedBefore), both stay, and a garment rigged to YAS reads as Yiggle.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-9")]
    [Trait("Speed", "Slow")]
    public void Renamed_bodies_still_detect_the_yiggle_size() // [AC-cross-sex-tops-yas-9]
    {
        byte[] Legs(params (string, float)[] weights)
            => SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 40, Weights: weights)));
        string root = Path.Combine(Path.GetTempPath(), $"proteus-rank-{Guid.NewGuid():N}");
        foreach (string size in new[] { "plain", "yiggle" }) Directory.CreateDirectory(Path.Combine(root, size));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "plain", "c0201e0000_dwn.mdl"), Legs(("j_kosi", 1f)));
            File.WriteAllBytes(Path.Combine(root, "yiggle", "c0201e0000_dwn.mdl"), Legs(("j_kosi", 0.7f), ("ya_shiri_phys_l", 0.3f)));
            // The plain size listed first, as Rue lists it: only the rig can put Yiggle ahead.
            var catalog = new BodySizeCatalog(root,
            [
                new BodyOption("Legs", "", "Plain", "plain/c0201e0000_dwn.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn"),
                new BodyOption("Legs", "", "Yiggle", "yiggle/c0201e0000_dwn.mdl", "chara/equipment/e0000/model/c0201e0000_dwn.mdl", "_dwn"),
            ]);
            var tree = SyntheticSkeleton.Human("c0101");
            var who = new RacialModelBake.Wearer(201, HavokSkeleton(tree), new XivLiveMesh.PbdFile(SyntheticSkeleton.HighlanderPbd()));
            var planner = new SkeletonRemap.Planner(new HashSet<string>(tree.Bones, StringComparer.Ordinal), _ => null);
            var bent = BakedBodyCatalog.Of(catalog, "0201", 201, who, ["_dwn"], out var failed, planner: planner);
            Assert.Empty(failed);

            var garment = Legs(("j_kosi", 0.7f), ("ya_shiri_phys_l", 0.3f));
            var parts = ModelPartReader.Read(garment)!;
            var bones = new HashSet<string>(SecondSkinWriter.Parse(garment).BoneNames, StringComparer.Ordinal);
            var options = bent.For("_dwn");

            // Read by the moved names alone, the two are one body.
            Assert.Single(BodySizeMatch.Rank(parts, options, bent.PathOf, bones).Scores);
            var ranked = BodySizeMatch.Rank(parts, options, bent.PathOf, bones, rigByPath: BakedBodyCatalog.RiggedBefore);
            Assert.Equal(2, ranked.Scores.Count);
            Assert.Equal("Yiggle", ranked.Best!.Value.Option.Name);
            Assert.Contains("rigByPath: BakedBodyCatalog.RiggedBefore", File.ReadAllText(SourceFile("Proteus/Gui/BodyRetargetPanel.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, string?> HavokSkeleton(SyntheticSkeleton.Tree tree)
        => XivLiveMesh.HavokSkeleton.Read(SyntheticSkeleton.Sklb(tree))!.ParentMap();

    private static string SourceFile(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(file);
    }
}
