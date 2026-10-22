using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Services;
using XivLiveMesh;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Men's legs refitted for women are saved without TBSE's earring nodes (earring-bones): every skeleton hangs
/// <c>n_ear_*</c> under the ear, so the crotch they are weighted to would follow her head. They are moved onto the hip,
/// and the bodies the garment was made on the same way.
/// </summary>
public class EarringBonesTests
{
    private const string Cloth = "/mt_c0101e0488_dwn_a.mtrl";
    private const string Skin = "/mt_c0101b0001_a.mtrl";

    /// <summary>A trimmed human skeleton with both thighs and the ear chain the game hangs the earring nodes on.</summary>
    private static SyntheticSkeleton.Tree Human(string name, params (string Bone, string Parent)[] extra)
        => SyntheticSkeleton.Human(name, [("j_asi_a_r", "j_kosi"), ("j_mimi_l", "j_kao"), ("j_mimi_r", "j_kao"),
                                          ("n_ear_a_l", "j_mimi_l"), ("n_ear_a_r", "j_mimi_r"), ("n_ear_b_l", "n_ear_a_l"),
                                          ("n_ear_b_r", "n_ear_a_r"), .. extra]);

    private static readonly SyntheticSkeleton.Tree Women = Human("c0201");

    /// <summary>The ear lifted far more than the hip, so a vertex bent as one is told from one bent as the other.</summary>
    private static float Lift(string bone) => UvBakeBones.IsEarringNode(bone) ? 0.07f : bone == "j_kosi" ? 0.01f : 0.03f;

    private static readonly string[] Deformed =
        [.. SyntheticSkeleton.Listed, "j_kao", "j_asi_a_l", "j_asi_a_r", "j_mimi_l", "n_ear_a_l", "n_ear_b_l", "n_ear_b_r"];

    private static OtherRaceWearer.Inputs Inputs(ushort race = 201, ushort drawn = 101, string slot = "_dwn",
                                                 SyntheticSkeleton.Tree? men = null)
        => new(race, drawn, slot, 488, null, null, SyntheticSkeleton.Sklb(race == 201 ? Women : Human($"c{race:D4}")),
               SyntheticSkeleton.Pbd(drawn, new Dictionary<ushort, string[]> { [race] = Deformed }, Lift), [], null,
               new Dictionary<string, byte[]>(), [SyntheticSkeleton.Sklb(men ?? Human($"c{drawn:D4}"))]);

    private static byte[] Legs(params (string, float)[] weights)
        => SyntheticModel.Build([], new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: weights)));

    /// <summary>TBSE-rigged chaps: crotch cloth with a little of each earring node beside the thigh, no hip of its own.</summary>
    private static byte[] Chaps()
        => SyntheticModel.Build([],
            [
                new SyntheticModel.Mesh(Cloth,
                    new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: [("j_asi_a_l", 0.95f), ("n_ear_b_l", 0.03f), ("n_ear_b_r", 0.02f)]),
                    new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetY: 2f, Weights: [("j_asi_a_r", 0.9f), ("n_ear_a_l", 0.1f)])),
            ]);

    private static OtherRaceWearer.Made Build(byte[] garment, OtherRaceWearer.Inputs? inputs = null)
    {
        var made = OtherRaceWearer.Build(inputs ?? Inputs(), ModelSkinReader.Read(garment, null, null)!);
        Assert.True(made.Wearer != null, made.Refusal);
        return made;
    }

    internal static List<(string Bone, float W)>[] ByName(byte[] model)
    {
        var skin = ModelSkinReader.Read(model, null, null)!;
        var all = new List<(string, float)>[skin.VertexCount];
        for (int v = 0; v < skin.VertexCount; v++)
        {
            var sum = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
            {
                int at = v * SkinnedMesh.MaxInfluences + k;
                if (skin.BoneWeights[at] > 0f) sum[skin.BoneNames[skin.BoneIndices[at]]] = sum.GetValueOrDefault(skin.BoneNames[skin.BoneIndices[at]]) + skin.BoneWeights[at];
            }
            all[v] = sum.Select(p => (p.Key, p.Value)).ToList();
        }
        return all;
    }

    // ── the garment ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-earring-bones-1")]
    [Trait("Plan", "EDGE-earring-bones-2")]
    [Trait("Plan", "EDGE-earring-bones-17")]
    public void Mens_legs_for_women_lose_their_earring_bones() // [AC-earring-bones-1] [EDGE-earring-bones-2] [EDGE-earring-bones-17]
    {
        var garment = Chaps();
        var made = Build(garment);
        Assert.Equal(["n_ear_a_l", "n_ear_b_l", "n_ear_b_r"], made.Earrings!.Select(m => m.From));
        Assert.All(made.Earrings!, m => Assert.Equal(new SkeletonRemap.Move(m.From, "j_kosi", SkeletonRemap.How.Earring), m));
        Assert.Empty(made.Moves!);   // the race has them: never called missing
        Assert.Equal(made.Earrings, made.Renames);

        var renamed = SkeletonRemap.Rename(garment, made.Renames, out string why);
        Assert.True(renamed != null, why);
        var names = SecondSkinWriter.Parse(renamed!).BoneNames;
        Assert.DoesNotContain(names, UvBakeBones.IsEarringNode);
        Assert.Contains("j_kosi", names);
        var was = ByName(garment);
        var now = ByName(renamed!);
        for (int v = 0; v < now.Length; v++)
        {
            float ears = was[v].Where(i => UvBakeBones.IsEarringNode(i.Bone)).Sum(i => i.W);
            Assert.Equal(ears, now[v].Where(i => i.Bone == "j_kosi").Sum(i => i.W), 4);
            Assert.Equal(1f, now[v].Sum(i => i.W), 2);
        }
    }

    [Theory]
    [InlineData("n_ear_a_l")]
    [InlineData("n_ear_a_r")]
    [InlineData("n_ear_b_l")]
    [InlineData("n_ear_b_r")]
    [Trait("Plan", "AC-earring-bones-1")]
    [Trait("Plan", "EDGE-earring-bones-16")]
    [Trait("Plan", "EDGE-earring-bones-17")]
    public void Earring_target_is_the_hip(string ear) // [AC-earring-bones-1] [EDGE-earring-bones-16] [EDGE-earring-bones-17]
    {
        // Measured (EarringBonesLocalTests): the weight sits on the crotch beside j_kosi, its _l/_r is not its leg, and
        // its parent is the ear — so the hip, whichever side it names.
        Assert.True(UvBakeBones.IsEarringNode(ear));
        string to = SkeletonRemap.EarringTarget(ear);
        Assert.Equal("j_kosi", to);
        Assert.Contains(to, Women.Bones);
        Assert.False(UvBakeBones.IsEarringNode(to));
        Assert.Equal(UvBakeBones.Region.Pelvis, UvBakeBones.RegionOf(to));
        Assert.Equal(UvBakeBones.Alias(ear), to);   // the bone the bake already bends it as
        Assert.False(UvBakeBones.IsEarringNode("j_mimi_l"));
    }

    [Fact]
    [Trait("Plan", "AC-earring-bones-1")]
    [Trait("Plan", "AC-earring-bones-6")]
    public void Job_and_panel_rename_every_move_before_the_bake() // [AC-earring-bones-1] [AC-earring-bones-6]
    {
        foreach (var (file, original) in new[] { ("Proteus/Services/CrossSexRefitJob.cs", "i.Garment"),
                                                 ("Proteus/Gui/BodyRetargetPanel.Race.cs", "bytes") })
        {
            string text = File.ReadAllText(SourceFile(file));
            int rename = text.IndexOf($"SkeletonRemap.Rename({original}, renames, out string refusal)", StringComparison.Ordinal);
            Assert.True(rename >= 0, file);
            Assert.True(text.IndexOf("if (made.Renames is { Count: > 0 } renames)", StringComparison.Ordinal) is var guard
                        && guard >= 0 && guard < rename, file);
            Assert.True(text.IndexOf("route.Into(drawnModel", rename, StringComparison.Ordinal) > rename, file);
            Assert.Contains("SkeletonRemap.RenameRefusal(refusal)", text[rename..(rename + 300)]);
        }
    }

    // ── its bend ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-earring-bones-2")]
    public void Former_earrings_bend_as_the_hip() // [AC-earring-bones-2]
    {
        var garment = Chaps();
        var made = Build(garment);
        var route = new RacialModelBake.Route(101, 201, Down: false) { ByAlias = true };
        var renamed = SkeletonRemap.Rename(garment, made.Renames, out _)!;
        float[] Ys(byte[] m) => ModelSkinReader.Read(m, null, null)!.Positions.Select(p => p.Y).ToArray();

        var before = Ys(route.Into(garment, made.Wearer!, out string why) ?? throw new InvalidOperationException(why));
        var after = Ys(route.Into(renamed, made.Wearer!, out why) ?? throw new InvalidOperationException(why));
        // The bake already bent earring nodes as the hip: the shape is the one it always was.
        Assert.Equal(before.Length, after.Length);
        for (int v = 0; v < before.Length; v++) Assert.Equal(before[v], after[v], 6);

        // Control: bent by their own name, they would have lifted as the ear does — the pbd tells the two apart.
        var byName = Ys(new RacialModelBake.Route(101, 201, Down: false).Into(garment, made.Wearer!, out why)!);
        var flat = Ys(garment);
        Assert.Contains(Enumerable.Range(0, flat.Length), v => MathF.Abs(byName[v] - after[v]) > 1e-3f);
        Assert.All(Enumerable.Range(0, flat.Length), v => Assert.True(after[v] - flat[v] < 0.04f, $"vertex {v} lifted as an ear"));
    }

    [Fact]
    [Trait("Plan", "EDGE-earring-bones-12")]
    public void The_preview_follows_the_hip_too() // [EDGE-earring-bones-12]
    {
        var garment = Chaps();
        var made = Build(garment);
        var up = new RacialModelBake.Route(101, 201, Down: false) { ByAlias = true };
        var saved = up.Into(SkeletonRemap.Rename(garment, made.Renames, out _)!, made.Wearer!, out _)!;
        var kept = up.Into(garment, made.Wearer!, out _)!;
        var forward = new RacialModelBake.Route(101, 201, Down: false);

        // Round trip: the preview, bent forward by name as the game does, lands back on the saved model.
        var preview = up.Back(saved, made.Wearer!, out string why) ?? throw new InvalidOperationException(why);
        var again = ModelSkinReader.Read(forward.Into(preview, made.Wearer!, out why)!, null, null)!.Positions;
        var want = ModelSkinReader.Read(saved, null, null)!.Positions;
        for (int v = 0; v < want.Length; v++) Assert.Equal(want[v].Y, again[v].Y, 5);

        // Unbent by the hip now, not the ear: the old model's preview drops its earring vertices further.
        var old = ModelSkinReader.Read(up.Back(kept, made.Wearer!, out _)!, null, null)!.Positions;
        var now = ModelSkinReader.Read(preview, null, null)!.Positions;
        Assert.Contains(Enumerable.Range(0, now.Length), v => MathF.Abs(old[v].Y - now[v].Y) > 1e-3f);
    }

    // ── the bodies it was made on ────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-earring-bones-3")]
    [Trait("Plan", "AC-earring-bones-5")]
    [Trait("Plan", "EDGE-earring-bones-3")]
    [Trait("Plan", "EDGE-earring-bones-6")]
    public void Made_for_legs_take_the_same_rename_only_when_the_garment_has_earrings() // [AC-earring-bones-3] [AC-earring-bones-5] [EDGE-earring-bones-3] [EDGE-earring-bones-6]
    {
        string root = Path.Combine(Path.GetTempPath(), $"proteus-earrings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sfw"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "sfw", "c0101e0000_dwn.mdl"),
                SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3,
                                                                       Weights: [("n_ear_b_l", 0.4f), ("j_asi_a_l", 0.6f)]))));
            var catalog = new BodySizeCatalog(root,
            [
                new BodyOption("Legs", "", "SFW", "sfw/c0101e0000_dwn.mdl", "chara/equipment/e0000/model/c0101e0000_dwn.mdl", "_dwn"),
            ]);
            string Bent(OtherRaceWearer.Made made)
            {
                var bent = BakedBodyCatalog.Of(catalog, "0101", 201, made.Wearer!, ["_dwn"], out var failed, byAlias: true,
                                               made.Planner);
                Assert.Empty(failed);
                return bent.PathOf(Assert.Single(bent.For("_dwn")));
            }

            string renamed = Bent(Build(Chaps()));
            Assert.DoesNotContain(OtherRaceRefit.Weighted(ModelSkinReader.Read(File.ReadAllBytes(renamed), null, null)!),
                                  UvBakeBones.IsEarringNode);
            Assert.Contains("-r", Path.GetFileName(renamed));
            Assert.Contains("n_ear_b_l", BakedBodyCatalog.RiggedBefore(renamed)!);

            // A garment with no earring nodes leaves its bodies as they were: no rename, no new file.
            string plain = Bent(Build(Legs(("j_kosi", 0.3f), ("j_asi_a_l", 0.7f))));
            Assert.DoesNotContain("-r", Path.GetFileName(plain));
            Assert.NotEqual(Path.GetFileName(renamed), Path.GetFileName(plain));
            Assert.Contains("n_ear_b_l", OtherRaceRefit.Weighted(ModelSkinReader.Read(File.ReadAllBytes(plain), null, null)!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Body(params (string, float)[] weights)
        => SyntheticModel.Build([], new SyntheticModel.Mesh(Skin, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, Weights: weights)));

    private static BodyRetarget.SlotPair Pair(byte[] source, byte[] target)
    {
        var t = ModelPartReader.Read(target)!;
        Assert.True(IdentityCorrespondence.TryBuild(t, t, "legs", out var built, out string why), why);
        return new BodyRetarget.SlotPair("_dwn", built!, t, target, source);
    }

    /// <summary>
    /// Garment and made-for body renamed alike refit exactly as if both had been rigged to the hip all along — the weight
    /// plan subtracts the old body's earring share where it added it — and the rebuilt model names no earring node. The
    /// cloth's own weights differ from the body's, so the plan reweights rather than copies.
    /// </summary>
    /// <param name="reweighted">Cloth off the skin, in the band where the plan adds the new body's share and takes the
    /// old one's; cloth on the skin takes the new body's whole, whatever the old one was.</param>
    [Theory]
    [InlineData(0.001f, false)]
    [InlineData(0.015f, true)]
    [Trait("Plan", "AC-earring-bones-1")]
    [Trait("Plan", "AC-earring-bones-3")]
    [Trait("Plan", "EDGE-earring-bones-1")]
    public void Renamed_legs_rebuild_with_no_earring_bones(float off, bool reweighted) // [AC-earring-bones-1] [AC-earring-bones-3] [EDGE-earring-bones-1]
    {
        byte[] Garment(params (string, float)[] weights) => SyntheticModel.Build([],
            new SyntheticModel.Mesh(Cloth, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetZ: off, Weights: weights)));
        var moves = SkeletonRemap.EarringMoves(["n_ear_b_l"]);
        var garment = SkeletonRemap.Rename(Garment(("j_kosi", 0.3f), ("n_ear_b_l", 0.1f), ("j_asi_a_l", 0.6f)), moves, out _)!;
        Assert.Contains("j_kosi", SkeletonRemap.DuplicateBones(garment));
        var tbse = Body(("j_kosi", 0.6f), ("n_ear_b_l", 0.1f), ("j_asi_a_l", 0.3f));
        var bibo = Body(("j_kosi", 0.3f), ("j_asi_a_l", 0.7f));

        byte[] Refit(byte[] g, byte[] source)
        {
            var pairs = new[] { Pair(source, bibo) };
            return BodyRetarget.Rebuild(g, pairs, swapSkin: false, BodyRetarget.PlanWeights(g, pairs, acrossBodies: true, before: g), out _)!;
        }
        var both = Refit(garment, SkeletonRemap.Rename(tbse, moves, out _)!);
        var hip = Refit(Garment(("j_kosi", 0.4f), ("j_asi_a_l", 0.6f)), Body(("j_kosi", 0.7f), ("j_asi_a_l", 0.3f)));
        var garmentOnly = Refit(garment, tbse);

        Assert.DoesNotContain(OtherRaceRefit.Weighted(ModelSkinReader.Read(both, null, null)!), UvBakeBones.IsEarringNode);
        var one = ByName(both);
        var two = ByName(hip);
        var control = ByName(garmentOnly);
        bool differs = false;
        for (int v = 0; v < one.Length; v++)
        {
            var want = two[v].ToDictionary(i => i.Bone, i => i.W);
            foreach (var (bone, w) in one[v]) Assert.Equal(want.GetValueOrDefault(bone), w, 2);
            Assert.Equal(1f, one[v].Sum(i => i.W), 2);
            differs |= control[v].Any(i => MathF.Abs(want.GetValueOrDefault(i.Bone) - i.W) > 0.02f);
        }
        // Control: the body left with its earring node, the plan reads the old body's hip share short.
        Assert.True(differs == reweighted, reweighted ? "renaming the body changed nothing: the test cannot tell"
                                                      : "cloth on the skin read the old body");
    }

    // ── said, never called missing ───────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-earring-bones-4")]
    [Trait("Plan", "EDGE-earring-bones-14")]
    public void Earring_moves_are_reported_and_not_called_missing() // [AC-earring-bones-4] [EDGE-earring-bones-14]
    {
        var made = Build(Chaps());
        Assert.Empty(made.Moves!);
        var result = new CrossSexRefitJob.Result("", new RacialModelBake.Route(101, 201, false), made.Wearer, null, null, null, [],
                                                 made.Moves, made.Est, made.NeedsSkeleton, made.Earrings);
        var report = CrossSexRefitReport.Report(result, "chaps.mdl", "chara/equipment/e0488/model/c0101e0488_dwn.mdl", 101, 201,
                                                withSkeleton: false, new JsonArray());
        var moved = report["movedBones"]!.AsArray();
        Assert.Equal(3, moved.Count);
        Assert.All(moved, m => Assert.Equal("earring", (string?)m!["how"]));
        Assert.Contains(moved, m => (string?)m!["from"] == "n_ear_b_l" && (string?)m["to"] == "j_kosi");
        Assert.False(CrossSexRefitReport.WantsFromSkeleton(made.Earrings));
        Assert.Equal("n_ear_b_l → j_kosi (earring)", SkeletonRemap.DescribeHow([made.Earrings![1]]));

        foreach (string file in new[] { "Proteus/Services/CrossSexRefitJob.cs", "Proteus/Gui/BodyRetargetPanel.Race.cs" })
            Assert.Contains("\"[Proteus] retarget: earring bones moved for c{0:D4}: {1}\"", File.ReadAllText(SourceFile(file)));
        // The panel's "lacks" note counts the race's missing bones alone.
        string race = File.ReadAllText(SourceFile("Proteus/Gui/BodyRetargetPanel.Race.cs"));
        Assert.Contains("SkeletonRemap.Describe(b.Moves)", race);
        Assert.DoesNotContain("Moves = made.Renames", race);
    }

    // ── nothing else ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(101, 201, "_dwn", true)]
    [InlineData(101, 401, "_dwn", true)]    // Highlander women
    [InlineData(101, 801, "_dwn", true)]    // Miqo'te women, who fall through to c0201
    [InlineData(101, 1401, "_dwn", true)]   // Au Ra women
    [InlineData(101, 301, "_dwn", false)]   // men of another race
    [InlineData(201, 101, "_dwn", false)]   // women's legs made men's
    [InlineData(201, 201, "_dwn", false)]
    [InlineData(101, 201, "_top", false)]   // a top
    [InlineData(101, 201, "_glv", false)]
    [InlineData(0, 201, "_dwn", false)]
    [InlineData(101, 0, "_dwn", false)]
    [Trait("Plan", "AC-earring-bones-5")]
    [Trait("Plan", "EDGE-earring-bones-7")]
    [Trait("Plan", "EDGE-earring-bones-8")]
    [Trait("Plan", "EDGE-earring-bones-9")]
    [Trait("Plan", "EDGE-earring-bones-11")]
    [Trait("Plan", "EDGE-earring-bones-13")]
    public void Only_mens_legs_for_women_drop_earrings(int drawn, int to, string slot, bool drops) // [AC-earring-bones-5] [EDGE-earring-bones-7] [EDGE-earring-bones-8] [EDGE-earring-bones-9] [EDGE-earring-bones-11] [EDGE-earring-bones-13]
    {
        Assert.Equal(drops, SkeletonRemap.DropsEarrings((ushort)drawn, (ushort)to, slot));
        if (drops || drawn == 0 || to == 0 || drawn == to) return;
        // Built for that route, the garment keeps its earring nodes and its bodies theirs.
        var made = Build(Chaps(), Inputs((ushort)to, (ushort)drawn, slot));
        Assert.Empty(made.Earrings!);
        Assert.Empty(made.Planner!.MovesFor(["n_ear_b_l", "j_kosi"]));
    }

    [Fact]
    [Trait("Plan", "AC-earring-bones-5")]
    [Trait("Plan", "EDGE-earring-bones-3")]
    public void Legs_without_earrings_come_out_as_they_went_in() // [AC-earring-bones-5] [EDGE-earring-bones-3]
    {
        var garment = Legs(("j_kosi", 0.3f), ("j_asi_a_l", 0.7f));
        var made = Build(garment);
        Assert.Empty(made.Earrings!);
        Assert.Empty(made.Renames);
        Assert.Same(garment, SkeletonRemap.Rename(garment, made.Renames, out _));
        // Its planner is the one it always was: a body's earring nodes are bones the wearer has, left alone.
        Assert.Empty(made.Planner!.MovesFor(["n_ear_b_l", "n_ear_b_r", "j_kosi"]));
    }

    [Fact]
    [Trait("Plan", "AC-earring-bones-5")]
    [Trait("Plan", "EDGE-earring-bones-15")]
    public void Automatic_paths_are_untouched() // [AC-earring-bones-5] [EDGE-earring-bones-15]
    {
        foreach (string file in new[] { "Proteus/Gui/BodyRetargetPanel.cs", "Proteus/Services/AutoRefit/AutoRefitWatcher.cs",
                                        "Proteus/Services/AutoRefit/RefitCore.cs" })
            foreach (string api in new[] { "DropsEarrings", "EarringMoves", "Renames", "dropEarrings" })
                Assert.DoesNotContain(api, File.ReadAllText(SourceFile(file)));
        // The size set reaches the job, but only women's garments made men's: earrings are never dropped there.
        Assert.Contains("CrossSexRefitJob.Run", File.ReadAllText(SourceFile("Proteus/Services/SizeSet/SizeSetRefit.CrossSex.cs")));
        Assert.False(SkeletonRemap.DropsEarrings(201, 101, "_dwn"));
    }

    [Fact]
    [Trait("Plan", "AC-earring-bones-6")]
    [Trait("Plan", "EDGE-earring-bones-10")]
    public void A_failed_rename_is_refused_not_half_saved() // [AC-earring-bones-6] [EDGE-earring-bones-10]
    {
        Assert.Null(SkeletonRemap.Rename(new byte[64], SkeletonRemap.EarringMoves(["n_ear_b_l"]), out string why));
        Assert.NotEmpty(why);
    }

    // ── beside the bones the race lacks ──────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-earring-bones-4")]
    public void Earrings_and_missing_bones_move_together() // [EDGE-earring-bones-4]
    {
        var garment = Legs(("j_asi_a_l", 0.8f), ("n_ear_b_l", 0.1f), ("iv_ochi_l", 0.1f));
        var made = Build(garment, Inputs(men: Human("c0101", ("iv_ochi_l", "j_kosi"))));
        Assert.Equal(["n_ear_b_l"], made.Earrings!.Select(m => m.From));
        Assert.Equal(new SkeletonRemap.Move("iv_ochi_l", "j_kosi", SkeletonRemap.How.Ancestor), Assert.Single(made.Moves!));
        Assert.Equal("iv_ochi_l → j_kosi", SkeletonRemap.Describe(made.Moves!));
        Assert.Equal(2, made.Renames.Count);
        var renamed = SkeletonRemap.Rename(garment, made.Renames, out string why);
        Assert.True(renamed != null, why);
        Assert.True(SkeletonRemap.Verify(garment, renamed!, made.Renames, out why), why);
    }

    [Fact]
    [Trait("Plan", "EDGE-earring-bones-5")]
    public void A_missing_bone_never_lands_on_an_earring_node() // [EDGE-earring-bones-5]
    {
        var wearer = new HashSet<string>(Women.Bones, StringComparer.Ordinal);
        string? Parent(string b) => b == "x_bone" ? "n_ear_b_l" : b == "n_ear_b_l" ? "j_kosi" : null;
        Assert.Equal(new SkeletonRemap.Move("x_bone", "j_kosi", SkeletonRemap.How.Ancestor),
                     Assert.Single(new SkeletonRemap.Planner(wearer, Parent, dropEarrings: true).MovesFor(["x_bone"])));
        // Control: without the flag the walk stops on the earring node.
        Assert.Equal("n_ear_b_l", Assert.Single(new SkeletonRemap.Planner(wearer, Parent).MovesFor(["x_bone"])).To);

        // Through the wearer: a garment bone hung under an earring node in the men's skeleton.
        var garment = Legs(("j_asi_a_l", 0.8f), ("n_ear_b_l", 0.1f), ("x_bone", 0.1f));
        var made = Build(garment, Inputs(men: Human("c0101", ("x_bone", "n_ear_b_l"))));
        Assert.DoesNotContain(made.Renames, m => UvBakeBones.IsEarringNode(m.To));
        Assert.Contains(made.Moves!, m => m.From == "x_bone");
    }

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
