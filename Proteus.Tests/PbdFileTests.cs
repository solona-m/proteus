using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XivLiveMesh;
using Xunit;

namespace Proteus.Tests;

/// <summary>The racial deformer parser, on layouts the game's own file never reaches.</summary>
public class PbdFileTests
{
    /// <summary>
    /// NIRÆ's human_crimson_v10.pbd: deformers of ~650 bones, so the bone-name offsets (from the deformer's start)
    /// pass 32767. Read signed, they went negative and the whole file failed to load — no racial bend anywhere.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-1")]
    public void Reads_a_deformer_whose_bone_names_sit_past_32_KB()
    {
        const int boneCount = 700;   // names start at 4 + 700 × 2 + 700 × 48 = 35004 bytes in
        var bones = Enumerable.Range(0, boneCount)
            .ToDictionary(i => $"j_bone_{i:D3}", i => Matrix4x4.CreateTranslation(i * 0.001f, 0f, 0f));
        var pbd = new PbdFile(HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, new Dictionary<string, Matrix4x4> { ["j_kao"] = Matrix4x4.Identity }),
            (201, 101, bones),
        ]));

        var chain = pbd.Chain(101, 201);

        var deformer = Assert.Single(chain);
        Assert.Equal(boneCount, deformer.Count);
        Assert.Equal(0.699f, deformer["j_bone_699"].Translation.X, 5);
        Assert.Equal(0f, deformer["j_bone_000"].Translation.X, 5);
    }

    // ---- Written from docs/pbd-format-spec.md (the clean-room spec); every expectation below is the spec's. ----

    private static Dictionary<string, Matrix4x4> Bones(params (string Name, float X)[] bones)
        => bones.ToDictionary(b => b.Name, b => Matrix4x4.CreateTranslation(b.X, 0f, 0f));

    /// <summary>Spec §7.4: 101 (root) listing j_kao, 201 (under 101) listing j_kao and j_kosi, 301 (under 201)
    /// listing j_kao. 312 bytes; links at 40; blocks at 64, 128 and 248.</summary>
    private static byte[] ThreeRaces() => HairPortFixtures.BuildPbd(
    [
        (101, ushort.MaxValue, Bones(("j_kao", 1f))),
        (201, 101, Bones(("j_kao", 2f), ("j_kosi", 3f))),
        (301, 201, Bones(("j_kao", 4f))),
    ]);

    private static void U16(byte[] b, int at, int value) => BitConverter.TryWriteBytes(b.AsSpan(at, 2), (ushort)value);
    private static void I32(byte[] b, int at, int value) => BitConverter.TryWriteBytes(b.AsSpan(at, 4), value);

    // Spec §1.4 positions for N = 3.
    private static int EntryRace(int i) => 4 + 12 * i;
    private static int EntryLink(int i) => 6 + 12 * i;
    private static int EntryBlock(int i) => 8 + 12 * i;
    private static int LinkParent(int j) => 4 + 12 * 3 + 8 * j;
    private static int LinkEntry(int j) => 10 + 12 * 3 + 8 * j;

    private static int[] Lengths(PbdFile pbd)
        => [pbd.Chain(101, 201).Count, pbd.Chain(101, 301).Count, pbd.Chain(201, 301).Count, pbd.Chain(301, 101).Count];

    /// <summary>Refused the way R-1 allows: an ArgumentException (or subclass), IndexOutOfRangeException or
    /// OverflowException, nothing else.</summary>
    private static void AssertRefused(Func<object> load)
    {
        var ex = Record.Exception(load);
        Assert.NotNull(ex);
        Assert.True(ex is ArgumentException or IndexOutOfRangeException or OverflowException,
                    $"threw {ex!.GetType().Name}: {ex.Message}");
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-3")]
    public void The_spec_fixture_has_the_layout_the_spec_states()
    {
        var b = ThreeRaces();
        Assert.Equal(312, b.Length);
        Assert.Equal(64, BitConverter.ToInt32(b, EntryBlock(0)));
        Assert.Equal(128, BitConverter.ToInt32(b, EntryBlock(1)));
        Assert.Equal(248, BitConverter.ToInt32(b, EntryBlock(2)));
        Assert.Equal(new[] { 1, 2, 1, 0 }, Lengths(new PbdFile(b)));
    }

    [Theory]
    [Trait("Plan", "EDGE-pbd-clean-room-3")]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(312)]
    public void A_bad_block_offset_follows_the_spec(int offset)
    {
        var b = ThreeRaces();
        I32(b, EntryBlock(1), offset);
        var pbd = new PbdFile(b);
        Assert.Equal(new[] { 0, 0, 1, 0 }, Lengths(pbd));
        Assert.Equal(4f, pbd.Chain(201, 301)[0]["j_kao"].Translation.X);   // the other race is unchanged
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-4")]
    public void Two_races_sharing_a_block_follow_the_spec()
    {
        var b = ThreeRaces();
        I32(b, EntryBlock(2), 128);
        var pbd = new PbdFile(b);
        Assert.Equal(new[] { 1, 2, 1, 0 }, Lengths(pbd));
        var shared = pbd.Chain(201, 301)[0];
        Assert.Equal(2, shared.Count);
        Assert.Same(pbd.Chain(101, 201)[0], shared);   // spec §2.4: one dictionary, shared
    }

    [Theory]
    [Trait("Plan", "EDGE-pbd-clean-room-5")]
    [InlineData("link 2's parent set to 7")]
    [InlineData("link 1's entry index set to 9")]
    [InlineData("entry 2's link index set to 9")]
    public void A_link_out_of_range_follows_the_spec(string edit)
    {
        var b = ThreeRaces();
        switch (edit)
        {
            case "link 2's parent set to 7": U16(b, LinkParent(2), 7); break;
            case "link 1's entry index set to 9": U16(b, LinkEntry(1), 9); break;
            default: U16(b, EntryLink(2), 9); break;
        }
        Assert.Equal(new[] { 1, 0, 0, 0 }, Lengths(new PbdFile(b)));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-6")]
    public void A_loop_in_the_tree_gives_no_chain()
    {
        var self = ThreeRaces();
        U16(self, LinkParent(0), 0);   // the root is its own parent
        var selfLoop = new PbdFile(self);
        Assert.Equal(new[] { 1, 2, 1, 0 }, Lengths(selfLoop));
        Assert.Empty(selfLoop.Chain(999, 301));

        var cycle = ThreeRaces();
        U16(cycle, LinkParent(0), 2);  // 101 → 301 → 201 → 101
        var loop = new PbdFile(cycle);
        Assert.Equal(new[] { 1, 2, 1, 1 }, Lengths(loop));
        Assert.Equal(1f, loop.Chain(301, 101)[0]["j_kao"].Translation.X);
        Assert.Empty(loop.Chain(999, 301));   // goes round the loop, never meets the source
        Assert.Empty(loop.Chain(999, 101));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-8")]
    public void Two_entries_for_one_race_use_the_first()
    {
        var b = ThreeRaces();
        U16(b, EntryRace(1), 301);
        var pbd = new PbdFile(b);
        Assert.Equal(new[] { 0, 1, 0, 0 }, Lengths(pbd));
        Assert.Equal(2, pbd.Chain(101, 301)[0].Count);   // entry 1's block, not entry 2's
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-7")]
    public void Same_race_or_race_zero_needs_no_bending()
    {
        var pbd = new PbdFile(ThreeRaces());
        Assert.Empty(pbd.Chain(201, 201));
        Assert.Empty(pbd.Chain(0, 201));
        Assert.Empty(pbd.Chain(101, 0));
        Assert.Empty(pbd.Chain(0, 0));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-8")]
    public void An_unknown_race_or_unreached_source_has_no_chain()
    {
        var pbd = new PbdFile(ThreeRaces());
        Assert.Empty(pbd.Chain(101, 9999));   // the target is not in the file
        Assert.Empty(pbd.Chain(9999, 301));   // the source is not in the file
        Assert.Empty(pbd.Chain(301, 201));    // the source is a descendant, never reached
        Assert.Empty(pbd.Chain(201, 101));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-8")]
    public void The_chain_runs_from_the_source_side_to_the_target()
    {
        var chain = new PbdFile(ThreeRaces()).Chain(101, 301);
        Assert.Equal(2, chain.Count);
        Assert.Equal(2f, chain[0]["j_kao"].Translation.X);   // 201's block first
        Assert.Equal(4f, chain[1]["j_kao"].Translation.X);   // the target's own block last
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-2")]
    public void A_file_of_no_races_constructs_and_has_no_chains()
    {
        var pbd = new PbdFile(new byte[4]);
        Assert.Empty(pbd.Chain(101, 201));
    }

    public static TheoryData<string> Damaged => new()
    {
        "empty",
        "header cut short",
        "a race count of -1",
        "a race count larger than the file",
        "cut in the middle of a matrix",
        "a block offset two bytes before the end",
        "a bone count of 1 000 000",
        "a bone count of -1",
        "a name offset past the end",
        "a name offset exactly at the end",
        "the last name has no terminator",
    };

    [Theory]
    [Trait("Plan", "EDGE-pbd-clean-room-2")]
    [MemberData(nameof(Damaged))]
    public void A_damaged_file_follows_the_spec(string damage)
    {
        var b = ThreeRaces();
        switch (damage)
        {
            case "empty": AssertRefused(() => new PbdFile([])); return;
            case "header cut short": AssertRefused(() => new PbdFile([1, 0, 0])); return;
            case "a race count of -1": AssertRefused(() => new PbdFile([0xFF, 0xFF, 0xFF, 0xFF])); return;
            case "a race count larger than the file": AssertRefused(() => new PbdFile([16, 0, 0, 0])); return;
            case "cut in the middle of a matrix": AssertRefused(() => new PbdFile(b.AsSpan(0, 276).ToArray())); return;
            case "a block offset two bytes before the end":
                I32(b, EntryBlock(1), b.Length - 2);
                AssertRefused(() => new PbdFile(b));
                return;
            case "a bone count of 1 000 000":
                I32(b, 128, 1_000_000);
                AssertRefused(() => new PbdFile(b));
                return;
            case "a bone count of -1":
                I32(b, 128, -1);
                AssertRefused(() => new PbdFile(b));
                return;
            case "a name offset past the end":
                U16(b, 132, 60000);
                AssertRefused(() => new PbdFile(b));
                return;
            case "a name offset exactly at the end":
            {
                U16(b, 132, b.Length - 128);
                var block = new PbdFile(b).Chain(101, 201)[0];
                Assert.True(block.ContainsKey(""));
                Assert.Equal(2f, block[""].Translation.X);
                return;
            }
            default:
            {
                int end = b.Length;
                while (b[end - 1] == 0) end--;
                var block = new PbdFile(b.AsSpan(0, end)).Chain(201, 301)[0];
                Assert.Equal(4f, block["j_kao"].Translation.X);
                return;
            }
        }
    }

    [Theory]
    [Trait("Plan", "EDGE-pbd-clean-room-18")]
    [Trait("CleanRoom", "NewBehaviour")]
    [InlineData("race count")]
    [InlineData("bone count")]
    public void A_count_larger_than_the_file_is_refused_before_allocating(string which)
    {
        byte[] b;
        if (which == "race count")
            b = [0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0];
        else
        {
            b = ThreeRaces();
            I32(b, 128, int.MaxValue);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        AssertRefused(() => new PbdFile(b));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000,
                    "the reader allocated from the count before checking it");
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-11")]
    public void A_name_listed_twice_follows_the_spec()
    {
        var b = HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, Bones(("j_kao", 1f))),
            (201, 101, Bones(("j_aaa", 5f), ("j_bbb", 6f))),
        ]);
        int origin = BitConverter.ToInt32(b, EntryBlock(1));
        U16(b, origin + 6, BitConverter.ToUInt16(b, origin + 4));   // the second bone takes the first one's name
        var block = Assert.Single(new PbdFile(b).Chain(101, 201));
        var only = Assert.Single(block);
        Assert.Equal("j_aaa", only.Key);
        Assert.Equal(5f, only.Value.Translation.X);   // the earliest occurrence keeps its matrix
    }

    [Theory]
    [Trait("Plan", "EDGE-pbd-clean-room-14")]
    [InlineData(new byte[] { 0xC3, 0xA9 }, "j_écd")]
    [InlineData(new byte[] { 0xFF }, "j_�bcd")]
    public void A_non_ascii_name_follows_the_spec(byte[] patch, string expected)
    {
        var b = HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, Bones(("j_kao", 1f))),
            (201, 101, Bones(("j_abcd", 7f))),
        ]);
        int origin = BitConverter.ToInt32(b, EntryBlock(1));
        int name = origin + BitConverter.ToUInt16(b, origin + 4);
        patch.CopyTo(b, name + 2);
        var block = Assert.Single(new PbdFile(b).Chain(101, 201));
        Assert.Equal(expected, Assert.Single(block.Keys));
    }

    [Fact]
    public void A_block_with_no_bones_is_an_empty_deformer_in_the_chain()
    {
        // Spec §2.4: B = 0 is valid, gives an empty dictionary, and still counts as a deformer in a chain.
        var b = HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, Bones(("j_kao", 1f))),
            (201, 101, new Dictionary<string, Matrix4x4>()),
        ]);
        var block = Assert.Single(new PbdFile(b).Chain(101, 201));
        Assert.Empty(block);
    }

    [Fact]
    public void Names_differing_only_in_case_are_two_bones()
    {
        // Spec §2.2: names are compared ordinal, case-sensitive.
        var b = HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, Bones(("j_kao", 1f))),
            (201, 101, Bones(("j_kao", 2f), ("J_KAO", 3f))),
        ]);
        var block = Assert.Single(new PbdFile(b).Chain(101, 201));
        Assert.Equal(2, block.Count);
        Assert.Equal(2f, block["j_kao"].Translation.X);
        Assert.Equal(3f, block["J_KAO"].Translation.X);
        Assert.False(block.ContainsKey("J_Kao"));
    }

    [Fact]
    public void The_twelve_floats_land_where_the_spec_table_puts_them()
    {
        // Spec §2.3: f0..f11 fill M11 M21 M31 M41 / M12 M22 M32 M42 / M13 M23 M33 M43; M14 = M24 = M34 = 0, M44 = 1.
        var b = HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, Bones(("j_kao", 1f))),
            (201, 101, Bones(("j_abc", 0f))),
        ]);
        int origin = BitConverter.ToInt32(b, EntryBlock(1));
        int matrices = origin + 4 + 2 * 1 + 2;   // B = 1: one name offset, then (2B) mod 4 = 2 bytes of padding
        for (int f = 0; f < 12; f++)
            BitConverter.TryWriteBytes(b.AsSpan(matrices + 4 * f, 4), (float)(f + 1));

        var m = Assert.Single(Assert.Single(new PbdFile(b).Chain(101, 201)).Values);
        Assert.Equal(new Matrix4x4(1, 5, 9, 0,
                                   2, 6, 10, 0,
                                   3, 7, 11, 0,
                                   4, 8, 12, 1), m);
    }

    private static Func<string, string?> Parents(params (string Bone, string Parent)[] links)
    {
        var map = links.ToDictionary(l => l.Bone, l => l.Parent);
        return bone => map.TryGetValue(bone, out var p) ? p : null;
    }

    private static IReadOnlyDictionary<string, Matrix4x4> D(params (string Name, Matrix4x4 M)[] bones)
        => bones.ToDictionary(b => b.Name, b => b.M);

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-9")]
    public void An_unlisted_bone_takes_its_nearest_listed_ancestor()
    {
        var parentOf = Parents(("c", "b"), ("b", "a"));
        var near = D(("a", Matrix4x4.CreateTranslation(1, 0, 0)), ("b", Matrix4x4.CreateTranslation(0, 2, 0)));
        var far = D(("a", Matrix4x4.CreateTranslation(0, 0, 3)));

        Assert.Equal(new Vector3(0, 2, 0), PbdFile.Resolve([near], "c", parentOf).Translation);
        // Each deformer is searched on its own: b from the first, a from the second.
        Assert.Equal(new Vector3(0, 2, 3), PbdFile.Resolve([near, far], "c", parentOf).Translation);
        Assert.True(PbdFile.Lists([far], "c", parentOf));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-9")]
    public void A_bone_with_nothing_listed_follows_the_spec()
    {
        var chain = new[] { D(("a", Matrix4x4.CreateScale(2))), D(("b", Matrix4x4.CreateTranslation(1, 1, 1))) };
        var parentOf = Parents(("x", "y"));
        Assert.Equal(Matrix4x4.Identity, PbdFile.Resolve(chain, "x", parentOf));
        Assert.False(PbdFile.Lists(chain, "x", parentOf));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-9")]
    public void Resolve_multiplies_in_chain_order()
    {
        // Spec §5: identity × R(D0) × R(D1); a scale then a translation keeps the translation unscaled.
        var chain = new[] { D(("j", Matrix4x4.CreateScale(2))), D(("j", Matrix4x4.CreateTranslation(1, 0, 0))) };
        var m = PbdFile.Resolve(chain, "j", _ => null);
        Assert.Equal(Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(1, 0, 0), m);
        Assert.Equal(1f, m.M41);
        Assert.True(PbdFile.Lists(chain, "j", _ => null));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-10")]
    public void A_parent_cycle_follows_the_spec()
    {
        var chain = new[] { D(("listed", Matrix4x4.CreateScale(3))) };
        var parentOf = Parents(("x_a", "x_b"), ("x_b", "x_a"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(Matrix4x4.Identity, PbdFile.Resolve(chain, "x_a", parentOf));
        Assert.False(PbdFile.Lists(chain, "x_a", parentOf));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [Trait("Plan", "EDGE-pbd-clean-room-10")]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void The_depth_limit_is_the_bone_and_255_ancestors(int listedAt, bool found)
    {
        Func<string, string?> parentOf = bone => $"n{int.Parse(bone[1..]) + 1}";
        var chain = new[] { D(($"n{listedAt}", Matrix4x4.CreateTranslation(9, 0, 0))) };
        Assert.Equal(found, PbdFile.Lists(chain, "n0", parentOf));
        Assert.Equal(found ? 9f : 0f, PbdFile.Resolve(chain, "n0", parentOf).M41);
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-10")]
    public void A_bone_listed_itself_never_asks_for_its_parent()
    {
        Func<string, string?> parentOf = _ => throw new InvalidOperationException("parentOf was called");
        var chain = new[] { D(("j", Matrix4x4.CreateScale(2))), D(("j", Matrix4x4.CreateScale(3))) };
        Assert.Equal(Matrix4x4.CreateScale(6), PbdFile.Resolve(chain, "j", parentOf));
        Assert.True(PbdFile.Lists(chain, "j", parentOf));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-12")]
    public void An_empty_chain_follows_the_spec()
    {
        Func<string, string?> parentOf = _ => throw new InvalidOperationException("parentOf was called");
        var empty = Array.Empty<IReadOnlyDictionary<string, Matrix4x4>>();
        Assert.Equal(Matrix4x4.Identity, PbdFile.Resolve(empty, "j_kosi", parentOf));
        Assert.False(PbdFile.Lists(empty, "j_kosi", parentOf));
    }

    [Fact]
    [Trait("Plan", "EDGE-pbd-clean-room-13")]
    public void One_instance_gives_the_same_answers_across_threads()
    {
        var pbd = new PbdFile(ThreeRaces());
        var parentOf = Parents(("j_kosi", "j_kao"), ("x_child", "j_kosi"));
        string[] bones = ["j_kao", "j_kosi", "x_child", "x_none"];
        (ushort, ushort)[] pairs = [(101, 201), (101, 301), (201, 301), (301, 101)];

        var expected = pairs.SelectMany(p => bones.Select(b =>
            (p, b, M: PbdFile.Resolve(pbd.Chain(p.Item1, p.Item2), b, parentOf),
                   L: PbdFile.Lists(pbd.Chain(p.Item1, p.Item2), b, parentOf)))).ToArray();

        var mismatches = 0;
        System.Threading.Tasks.Parallel.For(0, 4000, i =>
        {
            var e = expected[i % expected.Length];
            var chain = pbd.Chain(e.p.Item1, e.p.Item2);
            if (PbdFile.Resolve(chain, e.b, parentOf) != e.M || PbdFile.Lists(chain, e.b, parentOf) != e.L)
                System.Threading.Interlocked.Increment(ref mismatches);
        });
        Assert.Equal(0, mismatches);
    }

    /// <summary>Spec §6 and §7: the game's own human.pbd. Never skipped on the owner's machine.</summary>
    [Trait("Speed", "Slow")]
    public class RealFile
    {
        private static readonly Lazy<PbdFile> Pbd = new(() =>
            new PbdFile(new Lumina.GameData(LocalData.Path(LocalData.GameData))
                .GetFile("chara/xls/boneDeformer/human.pbd")!.Data));

        // Spec §7.2.
        private static readonly Dictionary<string, string?> ParentTable = new()
        {
            ["n_root"] = null, ["n_hara"] = "n_root", ["j_kosi"] = "n_hara", ["j_sebo_a"] = "j_kosi",
            ["j_sebo_b"] = "j_sebo_a", ["j_sebo_c"] = "j_sebo_b", ["j_kubi"] = "j_sebo_c", ["j_kao"] = "j_kubi",
            ["j_mune_l"] = "j_sebo_b", ["x_probe_child"] = "j_kao", ["x_probe_orphan"] = null,
        };

        private static string? ParentOf(string bone) => ParentTable.GetValueOrDefault(bone);

        private static void AssertMatrix(float[] expected, Matrix4x4 m)
        {
            float[] actual = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                              m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
            for (int i = 0; i < 16; i++)
                Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-5,
                            $"element M{i / 4 + 1}{i % 4 + 1}: expected {expected[i]:F6}, got {actual[i]:F6}");
        }

        public static TheoryData<ushort, ushort, int[]> ChainLengths => new()
        {
            { 101, 101, [] }, { 101, 0, [] }, { 0, 201, [] }, { 101, 9999, [] }, { 9999, 201, [] },
            { 201, 101, [] }, { 1201, 101, [] }, { 102, 201, [] },
            { 101, 901, [105] }, { 101, 201, [103] }, { 101, 1201, [105, 100] }, { 101, 204, [105, 103] },
            { 104, 204, [103] }, { 201, 1801, [100] },
        };

        [LocalDataTheory(LocalData.GameData)]
        [Trait("Plan", "AC-pbd-clean-room-6")]
        [MemberData(nameof(ChainLengths))]
        public void Chain_lengths_match_the_spec(ushort from, ushort to, int[] blockSizes)
            => Assert.Equal(blockSizes, Pbd.Value.Chain(from, to).Select(d => d.Count).ToArray());

        [LocalDataFact(LocalData.GameData)]
        [Trait("Plan", "AC-pbd-clean-room-6")]
        public void A_two_step_chain_without_listed_sizes_has_two_blocks()
            => Assert.Equal(2, Pbd.Value.Chain(101, 202).Count);

        private static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

        public static TheoryData<string, ushort, ushort, string, bool, float[]> Matrices => new()
        {
            { "A", 101, 901, "j_kosi", true, [1.734764f, 0, 0, 0, 0, 1.161724f, 0.123583f, 0, 0, 0.123583f, 1.700067f, 0, 0, -0.015559f, -0.133071f, 1] },
            { "B", 101, 1201, "j_kosi", true, [0.791213f, 0, 0, 0, 0, 0.599831f, 0.049041f, 0, 0, 0.049041f, 0.813460f, 0, 0, -0.212461f, -0.071442f, 1] },
            { "C", 101, 201, "j_kao", true, [1.001208f, 0, 0, 0, 0, 1.010835f, 0, 0, 0, 0, 1.012414f, 0, 0, -0.133022f, -0.004906f, 1] },
            { "D", 101, 204, "j_mune_l", true, [0.716123f, 0.002477f, 0.026286f, 0, 0.002152f, 0.730797f, -0.000681f, 0, 0.026315f, 0.018558f, 0.822960f, 0, -0.002695f, 0.093284f, -0.001429f, 1] },
            { "E", 101, 1201, "x_probe_child", true, [1.120343f, 0, 0, 0, 0, 1.120344f, 0, 0, 0, 0, 1.120345f, 0, 0, -1.047983f, -0.024068f, 1] },
            { "F", 101, 901, "x_probe_child", true, [1.370024f, 0, 0, 0, 0, 1.197100f, 0, 0, 0, 0, 1.385931f, 0, 0, 0.126864f, -0.006652f, 1] },
            { "G", 101, 1201, "x_probe_orphan", false, Identity },
            { "G2", 101, 901, "x_probe_orphan", false, Identity },
            { "H", 201, 101, "j_kosi", false, Identity },
        };

        [LocalDataTheory(LocalData.GameData)]
        [Trait("Plan", "AC-pbd-clean-room-6")]
        [MemberData(nameof(Matrices))]
        public void Resolved_matrices_match_the_spec(string @case, ushort from, ushort to, string bone, bool lists, float[] m)
        {
            _ = @case;
            _ = lists;
            AssertMatrix(m, PbdFile.Resolve(Pbd.Value.Chain(from, to), bone, ParentOf));
        }

        [LocalDataTheory(LocalData.GameData)]
        [Trait("Plan", "AC-pbd-clean-room-6")]
        [MemberData(nameof(Matrices))]
        [Trait("Suite", "Diagnostic")]
        public void Lists_matches_the_spec(string @case, ushort from, ushort to, string bone, bool lists, float[] m)
        {
            _ = @case;
            _ = m;
            Assert.Equal(lists, PbdFile.Lists(Pbd.Value.Chain(from, to), bone, ParentOf));
        }

        [LocalDataFact(LocalData.GameData)]
        [Trait("Plan", "AC-pbd-clean-room-6")]
        public void A_looping_parent_lookup_returns_the_identity()
        {
            Func<string, string?> loop = b => b == "x_a" ? "x_b" : b == "x_b" ? "x_a" : null;
            var chain = Pbd.Value.Chain(101, 901);
            AssertMatrix(Identity, PbdFile.Resolve(chain, "x_a", loop));
            Assert.False(PbdFile.Lists(chain, "x_a", loop));
        }

        [LocalDataFact(LocalData.GameData)]
        [Trait("Plan", "AC-pbd-clean-room-6")]
        public void The_worked_example_moves_the_point_where_the_spec_says()
        {
            var m = PbdFile.Resolve(Pbd.Value.Chain(101, 901), "j_kosi", ParentOf);
            var p = Vector3.Transform(new Vector3(0.1f, 1.0f, 0.05f), m);
            Assert.True(Math.Abs(p.X - 0.173476f) <= 1e-5, $"x {p.X:F6}");
            Assert.True(Math.Abs(p.Y - 1.152345f) <= 1e-5, $"y {p.Y:F6}");
            Assert.True(Math.Abs(p.Z - 0.075516f) <= 1e-5, $"z {p.Z:F6}");
        }
    }
}
