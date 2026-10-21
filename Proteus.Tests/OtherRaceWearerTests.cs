using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using XivLiveMesh;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The wearer a picked race is baked for, made from files — its skeleton, the game's deformer, the extra skeleton the
/// item needs — and the bodies the garment was made on, bent the same way.
/// </summary>
public class OtherRaceWearerTests
{
    private const string Shirt = "chara/equipment/e6091/model/c0101e6091_top.mdl";

    private static readonly SyntheticSkeleton.Tree Game301 = SyntheticSkeleton.Human("c0301");
    private static readonly SyntheticSkeleton.Tree Ivcs301 = SyntheticSkeleton.Human("c0301", ("iv_ochi_l", "j_kosi"));

    private static OtherRaceWearer.Inputs Inputs(byte[]? modded = null, byte[]? game = null, byte[]? pbd = null,
                                                 IReadOnlyList<EstTable.ModEntry>? modEst = null,
                                                 IReadOnlyDictionary<(int, ushort), int>? gameEst = null,
                                                 IReadOnlyDictionary<string, byte[]>? skeletons = null,
                                                 IReadOnlyList<byte[]>? drawn = null)
        => new(301, 101, "_top", 6091, modded, modded != null ? "ivcs.sklb" : null, game ?? SyntheticSkeleton.Sklb(Game301),
               pbd ?? SyntheticSkeleton.HighlanderPbd(), modEst ?? [], gameEst, skeletons ?? new Dictionary<string, byte[]>(),
               drawn);

    /// <summary>The drawn race's (c0101's) skeleton with IVCS's own bones on it.</summary>
    private static readonly SyntheticSkeleton.Tree Ivcs101 = SyntheticSkeleton.Human("c0101", ("iv_ochi_l", "j_kosi"),
                                                                                      ("j_ex_top_a_l", "j_sebo_c"),
                                                                                      ("j_ex_top_b_l", "j_sebo_c"));

    // ── the wearer ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-refit-other-race-1")]
    [Trait("Plan", "EDGE-refit-other-race-1")]
    public void The_wearer_is_made_from_files_with_no_character()
    {
        var made = OtherRaceWearer.Build(Inputs(), SyntheticSkeleton.Leaning("j_kosi", "j_sebo_c", "j_kao"));
        Assert.True(made.Wearer != null, made.Refusal);
        Assert.Equal(301, made.Wearer!.Race);
        Assert.Equal("j_kubi", made.Wearer.ParentOf("j_kao"));
        Assert.False(made.FellBack);
        Assert.Equal(EstOutcome.NotNeeded, made.Est.Outcome);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void An_unreadable_custom_skeleton_falls_back_to_the_game_s_then_refuses()
    {
        var fell = OtherRaceWearer.Build(Inputs(modded: new byte[40]), SyntheticSkeleton.Leaning("j_kosi"));
        Assert.NotNull(fell.Wearer);
        Assert.True(fell.FellBack);

        var none = OtherRaceWearer.Build(Inputs(modded: new byte[40], game: new byte[40]), SyntheticSkeleton.Leaning("j_kosi"));
        Assert.Null(none.Wearer);
        Assert.Contains(ModelRace.Describe("0301"), none.Refusal);

        var noDeformer = OtherRaceWearer.Build(Inputs(pbd: []), SyntheticSkeleton.Leaning("j_kosi"));
        Assert.Null(noDeformer.Wearer);
        Assert.NotEmpty(noDeformer.Refusal);
    }

    /// <summary>A deformer file cut short, or with a count no file could hold, is a refusal saying why — never a throw,
    /// which the panel would retry every frame.</summary>
    [Theory]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0 })]                 // one header promised, two bytes of it there
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 })] // a count of -1
    [InlineData(new byte[] { 0x10, 0, 0, 0 })]                    // sixteen headers, none there
    [Trait("Plan", "EDGE-refit-other-race-27")]
    public void A_damaged_deformer_is_refused_not_thrown(byte[] pbd)
    {
        var made = OtherRaceWearer.Build(Inputs(pbd: pbd), SyntheticSkeleton.Leaning("j_kosi"));
        Assert.Null(made.Wearer);
        Assert.Contains("(", made.Refusal);   // the cause, after the reason
    }

    /// <summary>
    /// A custom skeleton's bone is moved with its parent: IVCS's own bones bend like the spine they hang from. Without
    /// that skeleton — the mod not enabled — the same bone has nothing to follow. Once the bake was refused naming it
    /// (EDGE-refit-other-race-23); now it is moved onto its nearest ancestor the race has (cross-sex-tops-yas).
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-23")]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    public void A_bone_with_nothing_to_follow_is_moved_not_refused() // [AC-cross-sex-tops-yas-4]
    {
        var garment = SyntheticSkeleton.Leaning("j_kosi", "iv_ochi_l");
        var ivcs = OtherRaceWearer.Build(Inputs(modded: SyntheticSkeleton.Sklb(Ivcs301, 5)), garment);
        Assert.True(ivcs.Wearer != null, ivcs.Refusal);
        Assert.Equal("j_kosi", ivcs.Wearer!.ParentOf("iv_ochi_l"));
        Assert.Empty(ivcs.Moves ?? []);

        var game = OtherRaceWearer.Build(Inputs(drawn: [SyntheticSkeleton.Sklb(Ivcs101)]), garment);
        Assert.True(game.Wearer != null, game.Refusal);
        var move = Assert.Single(game.Moves!);
        Assert.Equal(new SkeletonRemap.Move("iv_ochi_l", "j_kosi", SkeletonRemap.How.Ancestor), move);
        Assert.Equal(EstOutcome.NotNeeded, game.Est.Outcome);
        Assert.Empty(game.Unbendable);
    }

    /// <summary>
    /// An extra bone the new race's skeletons lack once refused the bake even where the deformer lists that bone itself
    /// (AC-refit-other-race-8): the game would load no bone to draw it with. Now it is moved onto the bone it hangs from
    /// in the drawn race's skeleton, which the race has (cross-sex-tops-yas).
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-20")]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    public void A_missing_extra_bone_is_moved_to_its_parent() // [AC-cross-sex-tops-yas-4]
    {
        var pbd = SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]>
        {
            [301] = [.. SyntheticSkeleton.Listed, "j_ex_top_a_l"],
        });
        var made = OtherRaceWearer.Build(Inputs(pbd: pbd, gameEst: new Dictionary<(int, ushort), int> { [(6091, 101)] = 6085 },
                                                drawn: [SyntheticSkeleton.Sklb(Ivcs101)]),
                                         SyntheticSkeleton.Leaning("j_kosi", "j_ex_top_a_l"));
        Assert.True(made.Wearer != null, made.Refusal);
        Assert.Equal(new SkeletonRemap.Move("j_ex_top_a_l", "j_sebo_c", SkeletonRemap.How.Ancestor), Assert.Single(made.Moves!));
        Assert.Equal(EstOutcome.NotNeeded, made.Est.Outcome);
    }

    /// <summary>
    /// An extra skeleton the item loads that has some of the garment's extra bones: those are carried with its entry, as
    /// before; only the rest are moved.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-11")]
    public void An_extra_skeleton_s_bones_are_carried_and_only_the_rest_move() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-11]
    {
        var half = new SyntheticSkeleton.Tree("half", ["n_root", "j_ex_top_a_l"], [-1, 0]);
        var pbd = SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]>
        {
            [301] = [.. SyntheticSkeleton.Listed, "j_ex_top_a_l"],
        });
        var made = OtherRaceWearer.Build(
            Inputs(pbd: pbd, gameEst: new Dictionary<(int, ushort), int> { [(6091, 101)] = 6085 },
                   skeletons: new Dictionary<string, byte[]> { [EstTable.SkeletonPath(301, "_top", 6085)!] = SyntheticSkeleton.Sklb(half) },
                   drawn: [SyntheticSkeleton.Sklb(Ivcs101)]),
            SyntheticSkeleton.Leaning("j_kosi", "j_ex_top_a_l", "j_ex_top_b_l"));
        Assert.True(made.Wearer != null, made.Refusal);
        Assert.Equal(EstOutcome.Carry, made.Est.Outcome);
        Assert.Equal(6085, made.Est.Entry);
        var move = Assert.Single(made.Moves!);
        Assert.Equal("j_ex_top_b_l", move.From);
        Assert.Equal("j_sebo_c", move.To);
        Assert.Equal("n_root", made.Wearer!.ParentOf("j_ex_top_a_l"));
    }

    /// <summary>A garment that leans on no bone the race lacks has no moves: the bake takes it as it came.</summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-9")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-14")]
    public void A_garment_with_no_missing_bones_is_baked_as_it_came() // [AC-cross-sex-tops-yas-9] [EDGE-cross-sex-tops-yas-14]
    {
        var made = OtherRaceWearer.Build(Inputs(), SyntheticSkeleton.Leaning("j_kosi", "j_sebo_c", "j_kao"));
        Assert.True(made.Wearer != null, made.Refusal);
        Assert.Empty(made.Moves!);
        var garment = SyntheticModel.Build(["j_kosi"], new SyntheticModel.Mesh("mt_c0101e6091_top_a.mtrl", new SyntheticModel.Sub(0)));
        Assert.Same(garment, SkeletonRemap.Rename(garment, [], out _));
        string job = File.ReadAllText(SourceFile("Proteus/Services/CrossSexRefitJob.cs"));
        Assert.Contains("if (moves.Count > 0)", job);
        Assert.Contains("byte[] drawnModel = i.Garment;", job);
        string panel = File.ReadAllText(SourceFile("Proteus/Gui/BodyRetargetPanel.Race.cs"));
        Assert.Contains("byte[] drawnModel = bytes;", panel);
    }

    /// <summary>
    /// Moves and bones only a custom skeleton gives the race are said: the panel's notes, the job's log, the CLI's report.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-cross-sex-tops-yas-6")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-19")]
    public void Moved_bones_and_custom_skeleton_bones_are_said() // [AC-cross-sex-tops-yas-6] [EDGE-cross-sex-tops-yas-19]
    {
        var garment = SyntheticSkeleton.Leaning("j_kosi", "iv_ochi_l", "ya_ochi_phys_l");
        var drawn = SyntheticSkeleton.Human("c0101", ("iv_ochi_l", "j_kosi"), ("ya_ochi_phys_l", "iv_ochi_l"));
        var ivcs = OtherRaceWearer.Build(Inputs(modded: SyntheticSkeleton.Sklb(Ivcs301, 5), drawn: [SyntheticSkeleton.Sklb(drawn)]),
                                         garment);
        Assert.True(ivcs.Wearer != null, ivcs.Refusal);
        Assert.Equal(new SkeletonRemap.Move("ya_ochi_phys_l", "iv_ochi_l", SkeletonRemap.How.Ancestor), Assert.Single(ivcs.Moves!));
        Assert.Equal(["iv_ochi_l"], ivcs.NeedsSkeleton);
        Assert.Equal("ya_ochi_phys_l → iv_ochi_l", SkeletonRemap.Describe(ivcs.Moves!));

        var game = OtherRaceWearer.Build(Inputs(drawn: [SyntheticSkeleton.Sklb(drawn)]), garment);
        Assert.Empty(game.NeedsSkeleton!);

        string race = File.ReadAllText(SourceFile("Proteus/Gui/BodyRetargetPanel.Race.cs"));
        int notes = race.IndexOf("private void DrawRaceNotes(Baked b)", StringComparison.Ordinal);
        string body = race[notes..race.IndexOf("internal static string SkeletonMod", notes, StringComparison.Ordinal)];
        Assert.Contains("ps.RetargetRaceBonesMovedFmt", body);
        Assert.Contains("ps.RetargetRaceNeedsSkeletonFmt", body);
        Assert.Equal(2, body.Split("if (b.Moves.Count > 0)\n            using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))").Length);
        Assert.Contains("retarget: bones moved for c{0:D4}", race);
        Assert.Contains("retarget: bones moved for c{0:D4}", File.ReadAllText(SourceFile("Proteus/Services/CrossSexRefitJob.cs")));
        // The CLI's report says the same (CrossSexRefitReport, tested in CrossSexRefitTests).
        string cli = File.ReadAllText(SourceFile("Proteus.Refit/CrossSexCommand.cs"));
        Assert.Contains("CrossSexRefitReport.Report(result, garmentPath, gamePath, drawn, to, skeleton != null, sizes)", cli);
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

    /// <summary>The deformer is the game's own: a posing mod's in the creator's collection is never asked for.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-27")]
    public void The_deformer_is_read_only_from_the_game()
    {
        var asked = new List<string>();
        var inputs = OtherRaceWearer.Gather(301, Shirt,
            readGame: p => p == OtherRaceWearer.PbdPath ? SyntheticSkeleton.HighlanderPbd()
                         : p == OtherRaceWearer.BaseSkeletonPath(301) ? SyntheticSkeleton.Sklb(Game301) : null,
            resolveCollection: p => { asked.Add(p); return null; },
            modFile: _ => null, modEst: []);
        // The collection is asked for base skeletons alone: the race's, and the drawn race's (where a bone the race lacks
        // hangs) — never the deformer.
        Assert.Equal([OtherRaceWearer.BaseSkeletonPath(301), OtherRaceWearer.BaseSkeletonPath(101)], asked);
        Assert.NotNull(inputs.Pbd);
        Assert.Null(inputs.ModdedBase);
        Assert.Equal(101, inputs.Drawn);
        Assert.Equal(6091, inputs.Set);
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-1")]
    public void The_collection_s_skeleton_is_read_from_its_file()
    {
        string file = Path.Combine(Path.GetTempPath(), $"proteus-test-{Guid.NewGuid():N}.sklb");
        File.WriteAllBytes(file, SyntheticSkeleton.Sklb(Ivcs301, 5));
        try
        {
            var inputs = OtherRaceWearer.Gather(301, Shirt, _ => null, _ => file, _ => null, []);
            Assert.Equal(file, inputs.ModdedFrom);
            Assert.Contains("iv_ochi_l", HavokSkeleton.Read(inputs.ModdedBase!)!.Names);

            // A game path back (nothing replaces it) is the game's own: nothing modded to read.
            var vanilla = OtherRaceWearer.Gather(301, Shirt, _ => null, p => p, _ => null, []);
            Assert.Null(vanilla.ModdedBase);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void The_synthetic_deformer_has_a_way_from_midlander_to_highlander()
    {
        var pbd = new PbdFile(SyntheticSkeleton.HighlanderPbd());
        var chain = pbd.Chain(101, 301);
        Assert.Single(chain);
        Assert.True(chain[0].ContainsKey("j_kosi"), string.Join(",", chain[0].Keys));
    }

    [Fact]
    public void Parity_names_the_bones_two_skeletons_parent_differently()
    {
        var file = new Dictionary<string, string?> { ["a"] = null, ["b"] = "a", ["c"] = "a" };
        var live = new Dictionary<string, string?> { ["a"] = null, ["b"] = "a", ["c"] = "b", ["d"] = "a" };
        var differ = OtherRaceWearer.Parity(file, live);
        Assert.Contains(differ, d => d.StartsWith("c:", StringComparison.Ordinal));
        Assert.Contains(differ, d => d.Contains("only live", StringComparison.Ordinal));
        Assert.Empty(OtherRaceWearer.Parity(file, file));
    }

    // ── the extra skeleton ──────────────────────────────────────────────────

    private static readonly SyntheticSkeleton.Tree Tails = new("tails", ["n_root", "j_ex_top_a_l", "j_ex_top_b_l"], [-1, 0, 1]);
    private static string TailsPath(ushort race, int entry) => EstTable.SkeletonPath(race, "_top", entry)!;

    private static EstPlan Plan(IReadOnlyList<EstTable.ModEntry>? modEst = null,
                                IReadOnlyDictionary<(int, ushort), int>? gameEst = null,
                                IReadOnlyDictionary<string, SyntheticSkeleton.Tree>? skeletons = null,
                                params string[] weighted)
        => OtherRaceRefit.PlanEst(301, 101, "_top", 6091, weighted, Game301.Bones.ToHashSet(), modEst ?? [], gameEst,
                                  path => skeletons != null && skeletons.TryGetValue(path, out var t)
                                              ? HavokSkeleton.Read(SyntheticSkeleton.Sklb(t)) : null);

    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-19")]
    public void The_drawn_race_s_extra_skeleton_is_carried_when_the_new_race_has_its_file()
    {
        var plan = Plan(gameEst: new Dictionary<(int, ushort), int> { [(6091, 101)] = 6085 },
                        skeletons: new Dictionary<string, SyntheticSkeleton.Tree> { [TailsPath(301, 6085)] = Tails },
                        weighted: ["j_kosi", "j_ex_top_a_l"]);
        Assert.Equal(EstOutcome.Carry, plan.Outcome);
        Assert.Equal(6085, plan.Entry);

        // The panel saves it beside the EQDP entry as Penumbra's Est manipulation for the new race.
        var est = JsonSerializer.SerializeToElement(PenumbraManipulations.EstManipulation("0301", "Body", 6091, plan.Entry));
        var m = est.GetProperty("Manipulation");
        Assert.Equal(("Highlander", "Male", 6091, 6085, "Body"),
                     (m.GetProperty("Race").GetString(), m.GetProperty("Gender").GetString(), m.GetProperty("SetId").GetInt32(),
                      m.GetProperty("Entry").GetInt32(), m.GetProperty("Slot").GetString()));
        Assert.Single(EstTable.FromManipulations([est]), e => e is { Race: 301, Set: 6091, Entry: 6085, Slot: "Body" });
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-20")]
    [Trait("Plan", "EDGE-refit-other-race-23")]
    public void Bones_no_skeleton_the_new_race_loads_has_are_named()
    {
        // The drawn race has an entry, but the new race has no skeleton file for it in the mod or the game.
        var plan = Plan(gameEst: new Dictionary<(int, ushort), int> { [(6091, 101)] = 6085 },
                        weighted: ["j_kosi", "j_ex_top_a_l", "iv_ochi_l"]);
        Assert.Equal(EstOutcome.Missing, plan.Outcome);
        Assert.Equal(["iv_ochi_l", "j_ex_top_a_l"], plan.Missing);

        // No entry at all for either race.
        Assert.Equal(EstOutcome.Missing, Plan(weighted: ["j_ex_top_a_l"]).Outcome);
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-21")]
    public void An_entry_the_new_race_already_has_adds_nothing()
    {
        var skeletons = new Dictionary<string, SyntheticSkeleton.Tree> { [TailsPath(301, 7000)] = Tails };
        var fromMod = Plan(modEst: [new EstTable.ModEntry(301, "Body", 6091, 7000)], skeletons: skeletons,
                           weighted: ["j_ex_top_b_l"]);
        Assert.Equal(EstOutcome.Present, fromMod.Outcome);
        Assert.Equal(7000, fromMod.Entry);

        var fromGame = Plan(gameEst: new Dictionary<(int, ushort), int> { [(6091, 301)] = 7000, [(6091, 101)] = 6085 },
                            skeletons: skeletons, weighted: ["j_ex_top_b_l"]);
        Assert.Equal(EstOutcome.Present, fromGame.Outcome);
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-22")]
    public void A_garment_on_the_base_skeleton_alone_needs_no_entry()
        => Assert.Equal(EstOutcome.NotNeeded, Plan(gameEst: new Dictionary<(int, ushort), int> { [(6091, 101)] = 6085 },
                                                   weighted: ["j_kosi", "j_kao"]).Outcome);

    /// <summary>A carried skeleton's bones join the wearer, hanging where the extra skeleton hangs them.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-19")]
    public void A_carried_skeleton_s_bones_bend_with_their_parents()
    {
        var made = OtherRaceWearer.Build(
            Inputs(gameEst: new Dictionary<(int, ushort), int> { [(6091, 101)] = 6085 },
                   skeletons: new Dictionary<string, byte[]> { [TailsPath(301, 6085)] = SyntheticSkeleton.Sklb(Tails) }),
            SyntheticSkeleton.Leaning("j_kosi", "j_ex_top_b_l"));
        Assert.True(made.Wearer != null, made.Refusal);
        Assert.Equal(EstOutcome.Carry, made.Est.Outcome);
        Assert.Equal("j_ex_top_a_l", made.Wearer!.ParentOf("j_ex_top_b_l"));
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    public void The_game_s_table_reads()
    {
        // Two entries: (6091, c0101) → 6085, (6091, c0301) → 7000.
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(2));
        foreach (var (set, race) in new (ushort, ushort)[] { (6091, 101), (6091, 301) })
        {
            data.AddRange(BitConverter.GetBytes(set));
            data.AddRange(BitConverter.GetBytes(race));
        }
        data.AddRange(BitConverter.GetBytes((ushort)6085));
        data.AddRange(BitConverter.GetBytes((ushort)7000));
        var table = EstTable.Parse(data.ToArray())!;
        Assert.Equal(6085, table[(6091, 101)]);
        Assert.Equal(7000, table[(6091, 301)]);
        Assert.Null(EstTable.Parse(data.Take(10).ToArray()));
        Assert.Equal("chara/human/c0301/skeleton/top/t6085/skl_c0301t6085.sklb", EstTable.SkeletonPath(301, "_top", 6085));
        Assert.Null(EstTable.EstSlot("_dwn"));
    }

    // ── the bodies it was made on, bent ─────────────────────────────────────

    /// <summary>
    /// A made-for body leaning on a bone the wearer cannot move — a skeleton mod's, not enabled — is left out, saying
    /// which, rather than bent half-way.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-23")]
    public void A_made_for_body_with_bones_that_would_not_move_is_left_out()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"proteus-test-made-for-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "body.mdl"), SyntheticChest.Body(0.02f, 0.02f));
            var source = new BodySizeCatalog(dir, [new BodyOption("Chest", "", "M", "body.mdl",
                                                                   "chara/equipment/e0000/model/c0101e0000_top.mdl", "_top")]);
            // The deformer knows the spine only, and nothing says the breast bones hang from it.
            var pbd = new PbdFile(SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]> { [301] = ["j_sebo_c"] }));
            var bent = BakedBodyCatalog.Of(source, "0101", 301, new RacialModelBake.Wearer(301, new Dictionary<string, string?>(), pbd),
                                           ["_top"], out var leftOut);
            Assert.Empty(bent.Options);
            var why = Assert.Single(leftOut);
            Assert.Contains("j_mune_l", why);

            // Hung from the spine by the skeleton, they bend with it, and the body is bent.
            var parents = new Dictionary<string, string?> { ["j_mune_l"] = "j_sebo_c", ["j_mune_r"] = "j_sebo_c" };
            Assert.Single(BakedBodyCatalog.Of(source, "0101", 301, new RacialModelBake.Wearer(301, parents, pbd),
                                              ["_top"], out leftOut).Options);
            Assert.Empty(leftOut);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Game gear's "made for" side is the game's own body: for a picked race, the game's body of the DRAWN race — not the
    /// copy the panel holds, which follows the picked race by then — bent like the garment and filed under the new race.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-26")]
    public void A_vanilla_made_for_side_is_the_game_s_drawn_race_body_bent()
    {
        // The game's body as the lists read it, here a synthetic one: the real cache is the whole run's, keyed by race.
        string dir = Path.Combine(Path.GetTempPath(), $"proteus-test-game-body-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "c0101e0000_top.mdl"), SyntheticChest.Body(0.01f, 0.01f));
        var asked = new List<string>();
        BodySizeCatalog GameBody(string race)
        {
            asked.Add(race);
            return new BodySizeCatalog(dir, [new BodyOption("Vanilla", "", "The vanilla body", "c0101e0000_top.mdl",
                                                            "chara/equipment/e0000/model/c0101e0000_top.mdl", "_top")]);
        }
        var stale = new BodySizeCatalog("x", [new BodyOption("Vanilla", "", "c0301", "c0301.mdl",
                                                             "chara/equipment/e0000/model/c0301e0000_top.mdl", "_top")]);

        var made = OtherRaceRefit.MadeFor(VanillaBodyCatalog.Key, stale, null, "0101", GameBody)!;
        Assert.Equal(["0101"], asked);
        var body = Assert.Single(made.For("_top", "0101"));

        var pbd = new PbdFile(SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]> { [301] = ["j_sebo_c", "j_mune_l", "j_mune_r"] }));
        var bent = BakedBodyCatalog.Of(made, "0101", 301, new RacialModelBake.Wearer(301, new Dictionary<string, string?>(), pbd),
                                       ["_top"], out var failed);
        Assert.Empty(failed);
        var baked = Assert.Single(bent.For("_top", "0301"));
        Assert.Equal("chara/equipment/e0000/model/c0301e0000_top.mdl", baked.GamePath);
        Assert.NotEqual(File.ReadAllBytes(made.PathOf(body)), File.ReadAllBytes(bent.PathOf(baked)));

        // A made-for mod, or none (refit between the refit-onto mod's own sizes), is used as it is.
        Assert.Same(stale, OtherRaceRefit.MadeFor("SomeBody", stale, null, "0101", GameBody));
        Assert.Same(stale, OtherRaceRefit.MadeFor(null, null, stale, "0101", GameBody));
        Assert.Single(asked);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    // ── down to a race the drawn one falls through to (refit-race-down) ────

    private static readonly SyntheticSkeleton.Tree Game101 = SyntheticSkeleton.Human("c0101");

    private static OtherRaceWearer.Inputs Lowering(byte[] pbd, IReadOnlyList<EstTable.ModEntry>? modEst = null,
                                                   IReadOnlyDictionary<(int, ushort), int>? gameEst = null,
                                                   IReadOnlyDictionary<string, byte[]>? skeletons = null)
        => new(101, 301, "_top", 6091, null, null, SyntheticSkeleton.Sklb(Game101), pbd, modEst ?? [], gameEst,
               skeletons ?? new Dictionary<string, byte[]>());

    /// <summary>
    /// Bent down, a bone the deformer chain from Midlander up to Highlander cannot move is refused by name, as it is going
    /// up — the chain runs the other way, and was once found empty, which let every bone through.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-race-down-2")]
    [Trait("Plan", "EDGE-refit-race-down-4")]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-12")]
    public void A_lowered_wearer_refuses_bones_that_would_not_move() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-12]
    {
        var spine = SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]> { [301] = ["j_sebo_c"] });
        var stuck = OtherRaceWearer.Build(Lowering(spine), SyntheticSkeleton.Leaning("j_sebo_c", "j_asi_a_l"));
        Assert.Null(stuck.Wearer);
        Assert.Equal(["j_asi_a_l"], stuck.Unbendable);
        Assert.Empty(stuck.Moves ?? []);   // a bone the race has but the deformer cannot move is not moved (cross-sex-tops-yas)
        Assert.Contains("j_asi_a_l", stuck.Refusal);
        Assert.Contains(ModelRace.Describe("0101"), stuck.Refusal);

        // A bone hung below one the deformer lists bends with it.
        var bends = OtherRaceWearer.Build(Lowering(spine), SyntheticSkeleton.Leaning("j_sebo_c", "j_kao"));
        Assert.True(bends.Wearer != null, bends.Refusal);
        Assert.Equal(101, bends.Wearer!.Race);
    }

    [Fact]
    [Trait("Plan", "AC-refit-race-down-8")]
    [Trait("Plan", "EDGE-refit-race-down-10")]
    public void A_lowered_garment_s_extra_skeleton_is_planned_for_the_picked_race()
    {
        string[] weighted = ["j_kosi", "j_ex_top_a_l"];
        var bones = Game101.Bones.ToHashSet();
        HavokSkeleton? At(string path, string want) => path == want ? HavokSkeleton.Read(SyntheticSkeleton.Sklb(Tails)) : null;

        // Midlander's own entry: used as it is.
        var present = OtherRaceRefit.PlanEst(101, 301, "_top", 6091, weighted, bones, [],
            new Dictionary<(int, ushort), int> { [(6091, 101)] = 7000, [(6091, 301)] = 6085 },
            p => At(p, TailsPath(101, 7000)));
        Assert.Equal((EstOutcome.Present, 7000), (present.Outcome, present.Entry));

        // Only Highlander's: carried when Midlander's skeleton for it exists.
        var highlanderOnly = new Dictionary<(int, ushort), int> { [(6091, 301)] = 6085 };
        var carry = OtherRaceRefit.PlanEst(101, 301, "_top", 6091, weighted, bones, [], highlanderOnly,
                                           p => At(p, TailsPath(101, 6085)));
        Assert.Equal((EstOutcome.Carry, 6085), (carry.Outcome, carry.Entry));
        var missing = OtherRaceRefit.PlanEst(101, 301, "_top", 6091, weighted, bones, [], highlanderOnly, _ => null);
        Assert.Equal(EstOutcome.Missing, missing.Outcome);
        Assert.Equal(["j_ex_top_a_l"], missing.Missing);
    }

    private static BodyOption Option(string name, string rel, string race, string slot = "_top")
        => new("Chest", "", name, rel, $"chara/equipment/e0000/model/c{race}e0000{slot}.mdl", slot);

    [Fact]
    [Trait("Plan", "AC-refit-race-down-2")]
    [Trait("Plan", "EDGE-refit-race-down-12")]
    public void The_made_for_bodies_are_bent_down_and_filed_under_the_picked_race()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"proteus-test-made-for-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "small.mdl"), SyntheticChest.Body(0.01f, 0.01f));
            File.WriteAllBytes(Path.Combine(dir, "large.mdl"), SyntheticChest.Body(0.03f, 0.03f));
            var source = new BodySizeCatalog(dir, [Option("Small", "small.mdl", "0301"), Option("Large", "large.mdl", "0301")]);
            var pbd = new PbdFile(SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]> { [301] = ["j_sebo_c", "j_mune_l", "j_mune_r"] }, lift: 0.02f));
            var who = new RacialModelBake.Wearer(101, new Dictionary<string, string?>(), pbd);

            var bent = BakedBodyCatalog.Lowered(source, 301, 101, who, ["_top"], out var failed);
            Assert.Empty(failed);
            Assert.Equal(["Small", "Large"], bent.For("_top", "0101").Select(o => o.Name));
            Assert.All(bent.Options, o => Assert.Equal("chara/equipment/e0000/model/c0101e0000_top.mdl", o.GamePath));

            // Each moved down by the deformer's lift; the same file twice is bent once.
            var before = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(dir, "small.mdl")))!;
            var after = ModelPartReader.Read(File.ReadAllBytes(bent.PathOf(bent.Options[0])))!;
            Assert.Equal(-0.02f, after.Positions[1] - before.Positions[1], 4);
            var again = BakedBodyCatalog.Lowered(source, 301, 101, who, ["_top"], out _);
            Assert.Equal(bent.Options.Select(o => o.Rel), again.Options.Select(o => o.Rel));

            // The same bytes bent up, as a Midlander body for a Highlander pick, are another file.
            var asMidlander = new BodySizeCatalog(dir, [Option("Small", "small.mdl", "0101")]);
            var up = BakedBodyCatalog.Of(asMidlander, "0101", 301, who, ["_top"], out failed);
            Assert.Empty(failed);
            Assert.NotEqual(bent.Options[0].Rel, Assert.Single(up.Options).Rel);
            Assert.NotEqual(File.ReadAllBytes(bent.PathOf(bent.Options[0])), File.ReadAllBytes(up.PathOf(up.Options[0])));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// TBSE-X has a Highlander torso but Midlander legs only, which a Highlander draws bent: the legs are the body the
    /// garment's legs were cut against as he wears them, so bent down they are just themselves — taken byte for byte.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-2")]
    public void A_slot_without_a_drawn_race_body_uses_the_nearest_on_the_line()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"proteus-test-made-for-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "top.mdl"), SyntheticChest.Body(0.01f, 0.01f));
            File.WriteAllBytes(Path.Combine(dir, "mid.mdl"), SyntheticChest.Body(0.02f, 0.02f));
            File.WriteAllBytes(Path.Combine(dir, "legs.mdl"), SyntheticChest.Body(0.03f, 0.03f));
            var source = new BodySizeCatalog(dir, [
                Option("Highlander", "top.mdl", "0301"),
                Option("Midlander", "mid.mdl", "0101"),   // the drawn race has its own torso: this one is not used
                Option("Legs", "legs.mdl", "0101", "_dwn"),
            ]);
            var pbd = new PbdFile(SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]> { [301] = ["j_sebo_c", "j_mune_l", "j_mune_r"] }));
            var who = new RacialModelBake.Wearer(101, new Dictionary<string, string?>(), pbd);

            var bent = BakedBodyCatalog.Lowered(source, 301, 101, who, ["_top", "_dwn"], out var failed);
            Assert.Empty(failed);
            Assert.Equal("Highlander", Assert.Single(bent.For("_top", "0101")).Name);
            var legs = Assert.Single(bent.For("_dwn", "0101"));
            Assert.Equal("chara/equipment/e0000/model/c0101e0000_dwn.mdl", legs.GamePath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "legs.mdl")), File.ReadAllBytes(bent.PathOf(legs)));
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(dir, "top.mdl")),
                            File.ReadAllBytes(bent.PathOf(bent.For("_top", "0101")[0])));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>A made-for mod, or none, is used as it is; the game is not read. (The game's own side is read race by
    /// race in <c>OtherRaceRefitLocalTests</c>, against the real data: its body cache is the whole run's.)</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-3")]
    public void A_made_for_mod_below_is_used_as_it_is()
    {
        var asked = new List<string>();
        byte[]? Read(string path) { asked.Add(path); return null; }
        var mod = new BodySizeCatalog("mod", []);
        Assert.Same(mod, OtherRaceRefit.MadeForBelow("SomeBody", mod, null, 301, 101, Read));
        Assert.Same(mod, OtherRaceRefit.MadeForBelow(null, null, mod, 301, 101, Read));
        Assert.Empty(asked);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-race-down-4")]
    public void A_made_for_body_that_would_not_bend_down_is_left_out()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"proteus-test-made-for-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "body.mdl"), SyntheticChest.Body(0.02f, 0.02f));
            var source = new BodySizeCatalog(dir, [Option("M", "body.mdl", "0301")]);
            var pbd = new PbdFile(SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]> { [301] = ["j_sebo_c"] }));
            var bent = BakedBodyCatalog.Lowered(source, 301, 101, new RacialModelBake.Wearer(101, new Dictionary<string, string?>(), pbd),
                                                ["_top"], out var leftOut);
            Assert.Empty(bent.Options);
            Assert.Contains("j_mune_l", Assert.Single(leftOut));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Plan", "AC-refit-other-race-2")]
    [Trait("Plan", "EDGE-refit-other-race-2")]
    public void The_made_for_bodies_are_bent_and_filed_under_the_new_race()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"proteus-test-made-for-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "small.mdl"), SyntheticChest.Body(0.01f, 0.01f));
            File.WriteAllBytes(Path.Combine(dir, "large.mdl"), SyntheticChest.Body(0.03f, 0.03f));
            // The made-for mod has no Highlander body: the Midlander ones are bent, under the same names.
            var source = new BodySizeCatalog(dir, [
                new BodyOption("Chest", "", "Small", "small.mdl", "chara/equipment/e0000/model/c0101e0000_top.mdl", "_top"),
                new BodyOption("Chest", "", "Large", "large.mdl", "chara/equipment/e0000/model/c0101e0000_top.mdl", "_top"),
                new BodyOption("Legs", "", "Legs", "small.mdl", "chara/equipment/e0000/model/c0101e0000_dwn.mdl", "_dwn"),
            ]);
            var pbd = new PbdFile(SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]> { [301] = ["j_sebo_c", "j_mune_l", "j_mune_r"] }, lift: 0.02f));
            var who = new RacialModelBake.Wearer(301, new Dictionary<string, string?>(), pbd);

            var bent = BakedBodyCatalog.Of(source, "0101", 301, who, ["_top"], out var failed);
            Assert.Empty(failed);
            Assert.Equal(["Small", "Large"], bent.For("_top", "0301").Select(o => o.Name));
            Assert.Empty(bent.For("_dwn", "0301"));   // only the slots asked for
            Assert.All(bent.Options, o => Assert.Equal("chara/equipment/e0000/model/c0301e0000_top.mdl", o.GamePath));

            // Each moved by the deformer's lift; the same file twice is bent once.
            var before = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(dir, "small.mdl")))!;
            var after = ModelPartReader.Read(File.ReadAllBytes(bent.PathOf(bent.Options[0])))!;
            Assert.Equal(0.02f, after.Positions[1] - before.Positions[1], 4);
            var again = BakedBodyCatalog.Of(source, "0101", 301, who, ["_top"], out _);
            Assert.Equal(bent.Options.Select(o => o.Rel), again.Options.Select(o => o.Rel));
            Assert.Equal(2, bent.Options.Select(o => o.Rel).Distinct().Count());
            Assert.All(bent.Options, o => Assert.True(File.Exists(bent.PathOf(o))));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
