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
                                                 IReadOnlyDictionary<string, byte[]>? skeletons = null)
        => new(301, 101, "_top", 6091, modded, modded != null ? "ivcs.sklb" : null, game ?? SyntheticSkeleton.Sklb(Game301),
               pbd ?? SyntheticSkeleton.HighlanderPbd(), modEst ?? [], gameEst, skeletons ?? new Dictionary<string, byte[]>());

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
    /// that skeleton — the mod not enabled — the same bone has nothing to follow, and the bake is refused naming it.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-23")]
    public void A_bone_with_nothing_to_follow_refuses_the_bake_by_name()
    {
        var garment = SyntheticSkeleton.Leaning("j_kosi", "iv_ochi_l");
        var ivcs = OtherRaceWearer.Build(Inputs(modded: SyntheticSkeleton.Sklb(Ivcs301, 5)), garment);
        Assert.True(ivcs.Wearer != null, ivcs.Refusal);
        Assert.Equal("j_kosi", ivcs.Wearer!.ParentOf("iv_ochi_l"));

        var game = OtherRaceWearer.Build(Inputs(), garment);
        Assert.Null(game.Wearer);
        Assert.Equal(["iv_ochi_l"], game.Unbendable);
        Assert.Contains("iv_ochi_l", game.Refusal);
        Assert.Equal(EstOutcome.Missing, game.Est.Outcome);
    }

    /// <summary>
    /// An extra bone the new race's skeletons lack refuses the bake even where the deformer lists that bone itself, so
    /// would bend it: the game would load no bone to draw it with.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-refit-other-race-8")]
    [Trait("Plan", "EDGE-refit-other-race-20")]
    public void A_missing_extra_bone_the_deformer_lists_still_refuses()
    {
        var pbd = SyntheticSkeleton.Pbd(101, new Dictionary<ushort, string[]>
        {
            [301] = [.. SyntheticSkeleton.Listed, "j_ex_top_a_l"],
        });
        var made = OtherRaceWearer.Build(Inputs(pbd: pbd, gameEst: new Dictionary<(int, ushort), int> { [(6091, 101)] = 6085 }),
                                         SyntheticSkeleton.Leaning("j_kosi", "j_ex_top_a_l"));
        Assert.Null(made.Wearer);
        Assert.Equal(EstOutcome.Missing, made.Est.Outcome);
        Assert.Equal(["j_ex_top_a_l"], made.Unbendable);
        Assert.Contains("j_ex_top_a_l", made.Refusal);
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
        Assert.Equal([OtherRaceWearer.BaseSkeletonPath(301)], asked);
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
