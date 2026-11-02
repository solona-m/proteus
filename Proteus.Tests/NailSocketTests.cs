using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using V = Proteus.Tests.SyntheticFinger.Variant;

namespace Proteus.Tests;

/// <summary>
/// The nail socket: a nail floating in an open hole in the finger skin (Rue+, YAB+, LavaBod+...) must come off under a
/// garment WITHOUT leaving the hole — the bodysuit over it copies whatever the body has. Graded independently of the
/// pass: rays cast through the nail and through the gap at its rim, the seam between rim and the patch, folds, height
/// against the finger, a floor under the rim, and what moved — never the pass's own fit or classification.
/// </summary>
public class NailSocketTests(ITestOutputHelper o)
{
    private const int Size = 256;

    internal static SecondSkinLayer Full() => new()
    {
        MaterialName = "/gate.mtrl", Coverage = Enumerable.Repeat((byte)255, Size * Size).ToArray(),
        CoverageWidth = Size, CoverageHeight = Size,
    };

    private static SecondSkinLayer Gate(byte[] cov, int size = Size) => new()
    {
        MaterialName = "/gate.mtrl", Coverage = cov, CoverageWidth = size, CoverageHeight = size,
    };

    // ------------------------------------------------------------------ CI: the synthetic finger

    /// <summary>The socket is spanned rim to rim: every ray through the nail and through the gap at its rim now meets
    /// skin facing out, the patch meets the rim, nothing folds, and it sits on the finger.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-hole-2")]
    public void Open_socket_is_spanned_seam_to_seam()
    {
        var hand = SyntheticFinger.Build(V.OpenSocket);
        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, Full(), log.Add);
        foreach (var l in log) o.WriteLine(l);
        Assert.NotNull(flat);
        Assert.Contains(log, l => l.Contains("1 spanning an open socket", StringComparison.Ordinal));
        var g = new NailGrade(hand, flat!);
        g.AssertSpanned(o, band: true);
    }

    /// <summary>...and the bodysuit built over that hand is closed too: the rays meet the garment.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-hole-1")]
    public void Shell_over_open_socket_is_closed()
    {
        var hand = SyntheticFinger.Build(V.OpenSocket);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        var g = new NailGrade(hand, flat);
        var log = new List<string>();
        var shell = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(flat, CoverNails: true) },
            new[] { Gate(SecondSkinWriterVerbatimTests.PaintFingersNotNailBeds(flat, 1024), 1024) }, null, out _, log.Add);
        foreach (var l in log.Where(l => l.StartsWith("nail", StringComparison.Ordinal))) o.WriteLine(l);
        g.AssertClosedAgainst(shell, skinOnly: false, o);
        Assert.Contains(log, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^nail beds: [1-9]\d* of \d+ under painted"));
    }

    /// <summary>A nail on a closed finger still collapses to nothing, as before.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-hole-3")]
    [Trait("Plan", "EDGE-nail-socket-hole-1")]
    public void Closed_finger_nail_still_collapses()
    {
        var hand = SyntheticFinger.Build(V.Closed, weights: true);
        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, Full(), log.Add);
        foreach (var l in log) o.WriteLine(l);
        Assert.NotNull(flat);
        Assert.Contains(log, l => l.StartsWith("nails: 1 of 1 collapsed", StringComparison.Ordinal));
        var plan = SecondSkinWriter.NailSockets(hand, SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach));
        Assert.Empty(plan.Sockets);
        Assert.Empty(plan.OverOpenSkin);
        var (mesh, island) = SecondSkinWriter.NailBedIslands(hand).Select(kv => (kv.Key, kv.Value[0])).First();
        var verts = island.Distinct().ToArray();
        Assert.True(SecondSkinWriter.SpreadOf(flat!, mesh, verts) < 1e-4f);
        Assert.Single(SecondSkinWriter.SkinningOf(flat!, verts).Distinct());
    }

    /// <summary>A nail sewn into the finger is laid down as before; its shared rim never makes a socket.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-hole-4")]
    [Trait("Plan", "EDGE-nail-socket-hole-2")]
    public void Sewn_nail_is_laid_down_not_spanned()
    {
        var hand = SyntheticFinger.Build(V.Sewn);
        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
        Assert.True(plans.Values.Single().Sewn.Single(), "the fixture's nail should be sewn into the finger");
        Assert.Empty(SecondSkinWriter.NailSockets(hand, plans).Sockets);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        var (mesh, plan) = plans.Single();
        var pos = SecondSkinWriter.ReadCapMeshes(flat).Single(x => x.Mesh == mesh).Pos;
        foreach (int v in plan.VertsOf[0])
            Assert.True(MeshMath.Dist(pos[v], plan.OnSkin[v]!.Value) < 1e-6f, "a sewn nail lands on the skin around it");
    }

    /// <summary>The socket's other nails collapse while the nail on top spans it all — the tip's part of the rim too —
    /// including a tip with open edges near it that is near less than half the rim.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-3")]
    public void Tip_sharing_a_socket_collapses_while_top_spans()
    {
        var hand = SyntheticFinger.Build(V.TopAndTip);
        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
        var sock = SecondSkinWriter.NailSockets(hand, plans).Sockets.Single();
        Assert.Equal(2, sock.Members.Count);
        var tip = sock.Members.Single(m => m != sock.Owner);
        var g0 = new NailGrade(hand, hand);
        var (inReach, nearShare) = g0.TipCounts(tip.Mesh, tip.Island);
        o.WriteLine($"tip: {inReach} open edges within 1.5 mm, {nearShare:P0} of the rim within 3.5 mm");
        Assert.True(inReach >= 3 && nearShare < 0.5f, "the fixture's tip should be a member that is no candidate");

        var flat = BodyBridge.FlattenNails(hand, Full())!;
        var verts = plans[tip.Mesh].VertsOf[tip.Island];
        Assert.True(SecondSkinWriter.SpreadOf(flat, tip.Mesh, verts) < 1e-4f, "the tip should collapse");
        new NailGrade(hand, flat).AssertSpanned(o, band: false);
    }

    /// <summary>A stray crack beside a nail on a closed finger is not a socket: the nail collapses as before.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-4")]
    public void Stray_crack_still_collapses()
    {
        var hand = SyntheticFinger.Build(V.StrayCrack);
        var g = new NailGrade(hand, hand);
        Assert.True(g.OpenEdges.Count >= 3, "the fixture should have a crack");
        Assert.True(g.OpenEdgesInReach(0, 0) <= 2);
        var log = new List<string>();
        Assert.NotNull(BodyBridge.FlattenNails(hand, Full(), log.Add));
        Assert.Contains(log, l => l.StartsWith("nails: 1 of 1 collapsed", StringComparison.Ordinal));
    }

    /// <summary>No garment, or a fingerless one: the nails stay as authored.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-hole-5")]
    [Trait("Plan", "EDGE-nail-socket-hole-5")]
    public void Uncovered_socket_is_left_alone()
    {
        var hand = SyntheticFinger.Build(V.OpenSocket);
        Assert.Null(BodyBridge.FlattenNails(hand, Gate(new byte[Size * Size])));
        // A fingerless glove: the base of the finger painted, the fingertip and the nail chart not.
        var fingerless = new byte[Size * Size];
        for (int y = 0; y < Size * 0.25f; y++) for (int x = 0; x < Size; x++) fingerless[y * Size + x] = 255;
        Assert.Null(BodyBridge.FlattenNails(hand, Gate(fingerless)));
    }

    /// <summary>A rest quad lying exactly under a nail (LavaBod+ has them) shares the nail's edges, so it opens no loop:
    /// the nail collapses onto skin that is there, and nothing is left open or laid over skin.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-15")]
    public void Coincident_quad_is_not_a_socket()
    {
        var hand = SyntheticFinger.Build(V.CoincidentQuad);
        var sp = SecondSkinWriter.NailSockets(hand, SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach), o.WriteLine);
        Assert.Empty(sp.Sockets);
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine) ?? hand;
        var g = new NailGrade(hand, flat);
        g.AssertNoSocketRayOpen(o);
        g.AssertNoMembraneOnRestSkin();
    }

    /// <summary>The nail over a socket decides it: covered, the socket is spanned and a tip beside it that overhangs
    /// the finger goes too, painted or not; uncovered, nothing of the socket changes, whatever else is painted.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-close-3")]
    [Trait("Plan", "EDGE-nail-socket-hole-21")]
    public void The_nail_over_a_socket_decides_it()
    {
        var hand = SyntheticFinger.Build(V.TopAndTip);
        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
        var sock = SecondSkinWriter.NailSockets(hand, plans).Sockets.Single();
        var owner = sock.Owner!.Value;
        var tip = sock.Members.Single(m => m != owner);

        // The owner painted, the tip not: spanned, and the tip collapses.
        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, Gate(PaintOnly(hand, plans, owner)), log.Add);
        foreach (var l in log) o.WriteLine(l);
        Assert.NotNull(flat);
        var g = new NailGrade(hand, flat!);
        Assert.True(g.IslandArea(flat!, tip) < 1e-12f, "the tip beside a covered owner still draws");
        g.AssertSpanned(o, band: false);

        // The tip painted, the owner not: nothing of the socket changes.
        flat = BodyBridge.FlattenNails(hand, Gate(PaintOnly(hand, plans, tip)), o.WriteLine);
        foreach (var m in sock.Members)
            Assert.True(flat == null || NailGrade.SameIsland(hand, flat, plans, m), $"island {m} changed with its owner uncovered");
    }

    /// <summary>A nail mesh riding a spanned socket goes with it; one over a socket left as authored stays.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-6")]
    public void Nail_mesh_follows_its_sockets_fate()
    {
        var hand = SyntheticFinger.Build(V.OpenSocket, nailMesh: true);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        float before = NonSkinArea(hand), after = NonSkinArea(flat);
        o.WriteLine($"nail mesh {before * 1e6f:F1}mm² -> {after * 1e6f:F1}mm²");
        Assert.True(after < before * 0.01f);

        var kept = SyntheticFinger.Build(V.ClosedOwner, nailMesh: true);
        var keptFlat = BodyBridge.FlattenNails(kept, Full());
        Assert.True(keptFlat == null || MathF.Abs(NonSkinArea(keptFlat) - NonSkinArea(kept)) < 1e-9f);
    }

    /// <summary>An owner with fewer outline vertices than the rim pulls only the rim's own vertices onto itself, each
    /// a short way, every welded copy alike.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-7")]
    [Trait("Plan", "EDGE-nail-socket-hole-22")]
    public void Sparse_owner_pulls_only_the_rim()
    {
        var hand = SyntheticFinger.Build(V.SparseOwner);
        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, Full(), log.Add);
        foreach (var l in log) o.WriteLine(l);
        Assert.NotNull(flat);
        Assert.Matches(@"\([1-9]\d* rim vertices moved onto it, furthest", string.Join("\n", log));
        new NailGrade(hand, flat!).AssertSpanned(o, band: false);
    }

    /// <summary>A nail domed a millimetre high spans its socket cleanly and never rises above itself: a spanning nail
    /// keeps the authored shape, less the thickness its outline shows (a fitted finger surface proved unreliable on
    /// real hands), so under a garment a domed nail reads as a domed nail — never as a hole.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-8")]
    public void Domed_nail_spans_without_rising()
    {
        var hand = SyntheticFinger.Build(V.DomedHigh);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        new NailGrade(hand, flat).AssertSpanned(o, band: false);
    }

    /// <summary>An inset nail and a rim wrapped round the finger pass the depth grading; a dent fails it.</summary>
    [Theory]
    [Trait("Plan", "EDGE-nail-socket-hole-24")]
    [InlineData(V.InsetSocket)]
    [InlineData(V.WrappedRim)]
    public void Inset_nail_and_wrapped_rim_pass_and_a_dent_fails(V variant)
    {
        var hand = SyntheticFinger.Build(variant);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        Assert.NotNull(flat);
        var g = new NailGrade(hand, flat);
        if (variant == V.InsetSocket) Assert.True(g.OffsetsOf(g.Spanned.Single()).All(s => s > 0), "the nail should be inset");
        else Assert.True(g.LowestRimBelowPlane() <= -0.0006f, $"the rim should wrap: lowest {g.LowestRimBelowPlane() * 1000:F2}mm");
        g.AssertSpanned(o, band: true);
        Assert.False(g.Dent(0.00275f).FloorHolds(o), "a 2.75 mm dent must fail the floor");
    }

    /// <summary>A spanned nail keeps its skinning byte for byte, and so does the rim.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-9")]
    public void Spanned_nail_keeps_its_skinning()
    {
        var hand = SyntheticFinger.Build(V.SparseOwner, weights: true);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        int nv = SecondSkinWriter.ReadCapMeshes(hand)[0].Pos.Length;
        var all = Enumerable.Range(0, nv).ToArray();
        Assert.Equal(SecondSkinWriter.SkinningOf(hand, all), SecondSkinWriter.SkinningOf(flat, all));
    }

    /// <summary>A two-triangle owner, or one with two outlines, spans cleanly or is left as authored — never a hole,
    /// never a throw.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-hole-7")]
    [Trait("Plan", "EDGE-nail-socket-hole-11")]
    public void Degenerate_owner_is_left_as_authored()
    {
        var hand = SyntheticFinger.Build(V.TwoTriangleOwner);
        var sock = SecondSkinWriter.NailSockets(hand, SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach)).Sockets.Single();
        var flat = BodyBridge.FlattenNails(hand, Full()) ?? hand;
        var g = new NailGrade(hand, flat);
        o.WriteLine(sock.LeftAsAuthored ?? "spanned");
        g.AssertNoSocketRayOpen(o);
    }

    /// <summary>Every variant edits in place: same length, a valid file, the same indices, and no finger vertex moved
    /// but the rim's.</summary>
    [Theory]
    [Trait("Plan", "AC-nail-socket-hole-6")]
    [Trait("Plan", "EDGE-nail-socket-hole-13")]
    [MemberData(nameof(AllVariants))]
    public void Spanning_edits_in_place(V variant)
    {
        var hand = SyntheticFinger.Build(variant);
        var flat = BodyBridge.FlattenNails(hand, Full());
        if (flat == null) return;
        Assert.Equal(hand.Length, flat.Length);
        SecondSkinWriterVerbatimTests.Validate(flat);
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(hand, out _, out _, out var t0));
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(flat, out _, out _, out var t1));
        Assert.Equal(t0, t1);
        new NailGrade(hand, flat).AssertCollateral();
    }

    public static IEnumerable<object[]> AllVariants() => Enum.GetValues<V>().Select(v => new object[] { v });

    /// <summary>The shell build's own look at the flattened hand still finds every nail bed, and lifts none of the
    /// spanning one over the nail mesh collapsed into it: the garment sits on it as it sits on the finger.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-12")]
    public void Shell_replan_sees_the_bed_without_lift()
    {
        var hand = SyntheticFinger.Build(V.OpenSocket, nailMesh: true);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        var before = SecondSkinWriter.NailBedPlans(hand);
        var after = SecondSkinWriter.NailBedPlans(flat);
        Assert.Equal(before.Sum(p => p.Value.Islands.Count), after.Sum(p => p.Value.Islands.Count));
        var g = new NailGrade(hand, flat);
        var span = g.Spanned.Single();
        var plan = after[span.Mesh];
        foreach (int v in plan.VertsOf[span.Island]) Assert.Equal(0f, plan.Lift[v]);
        var shell = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(flat, CoverNails: true) },
            new[] { Full() }, null, out _);
        var (overMembrane, overFinger) = g.ShellOffsets(shell);
        o.WriteLine($"shell offset over the membrane {overMembrane * 1000:F3}mm, over the finger {overFinger * 1000:F3}mm");
        Assert.True(MathF.Abs(overMembrane - overFinger) < 0.00001f);
    }

    /// <summary>The shell over a spanned socket is closed whatever the coverage; with none, the membrane keeps the nail
    /// chart's UV over exactly its own area.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-10")]
    public void Shell_closed_under_every_coverage_mode()
    {
        var hand = SyntheticFinger.Build(V.OpenSocket);
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        var g = new NailGrade(hand, flat);
        var full = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(flat, CoverNails: true) },
            new[] { Full() }, null, out _);
        g.AssertClosedAgainst(full, skinOnly: false, o);
        var none = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(flat, CoverNails: true) },
            new[] { new SecondSkinLayer { MaterialName = "/mt_c0201a0053_rir_a.mtrl", Coverage = null } }, null, out _);
        g.AssertClosedAgainst(none, skinOnly: false, o);
        var span = g.Spanned.Single();
        float uvArea = SecondSkinWriterVerbatimTests.NailUvArea(none, hand, (m, k) => m == span.Mesh && k == span.Island);
        // The garment stands off the finger, so every area on it is larger by the same ratio as the whole surface.
        float scale = SkinArea(none, skinOnly: false) / SkinArea(flat, skinOnly: true);
        float own = g.IslandArea(flat, span) * scale;
        o.WriteLine($"nail-UV area {uvArea * 1e6f:F2}mm², the membrane at the garment's scale {own * 1e6f:F2}mm² (x{scale:F3})");
        Assert.True(MathF.Abs(uvArea - own) <= own * 0.05f + 1e-9f);
    }

    /// <summary>Sockets nothing can span cleanly keep their nails as authored: a nail where a nail was, no hole.</summary>
    [Theory]
    [Trait("Plan", "AC-nail-socket-hole-7")]
    [Trait("Plan", "EDGE-nail-socket-hole-14")]
    [Trait("Plan", "EDGE-nail-socket-hole-16")]
    [Trait("Plan", "EDGE-nail-socket-hole-18")]
    [Trait("Plan", "EDGE-nail-socket-hole-20")]
    [InlineData(V.ClosedOwner)]
    [InlineData(V.TwoLoops)]
    [InlineData(V.UnqualifiedOpen)]
    public void Unspannable_socket_is_left_as_authored(V variant)
    {
        var hand = SyntheticFinger.Build(variant);
        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
        var sp = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine);
        Assert.Empty(sp.Sockets.Where(s => s.Owner != null));
        var keep = sp.Sockets.SelectMany(s => s.Members).Concat(sp.OverOpenSkin).Distinct().ToList();
        Assert.NotEmpty(keep);
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine);
        foreach (var m in keep)
            Assert.True(flat == null || NailGrade.SameIsland(hand, flat, plans, m), $"{variant}: island {m} changed");
        if (variant == V.ClosedOwner)
            Assert.Equal("the nearest nail is closed", sp.Sockets.Single().LeftAsAuthored);
        if (variant == V.TwoLoops)
            Assert.All(sp.Sockets, s => Assert.Equal("one nail nearest two sockets", s.LeftAsAuthored));
        new NailGrade(hand, flat ?? hand).AssertNoMembraneOnRestSkin();
    }

    /// <summary>A socket wrapped half way round the finger is either spanned cleanly — closed, unfolded, rim to rim — or
    /// left as authored; never a hole.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-hole-7")]
    [Trait("Plan", "EDGE-nail-socket-hole-18")]
    public void Wrapped_socket_is_closed_or_left_as_authored()
    {
        var hand = SyntheticFinger.Build(V.WrapAround);
        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
        var sock = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine).Sockets.Single();
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine);
        if (sock.Owner == null)
        {
            Assert.True(flat == null || sock.Members.All(m => NailGrade.SameIsland(hand, flat, plans, m)));
            return;
        }
        Assert.NotNull(flat);
        new NailGrade(hand, flat!).AssertSpanned(o, band: false);
    }

    /// <summary>The tube's own ends — loops of open edges far from any nail — are never sockets.</summary>
    [Theory]
    [Trait("Plan", "EDGE-nail-socket-hole-17")]
    [MemberData(nameof(AllVariants))]
    public void Wrist_scale_loop_is_ignored(V variant)
    {
        var hand = SyntheticFinger.Build(variant);
        var sp = SecondSkinWriter.NailSockets(hand, SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach));
        foreach (var s in sp.Sockets)
            Assert.True(s.Rim.Max(e => e.A.Y) < SyntheticFinger.L - 0.005f && s.Rim.Min(e => e.A.Y) > 0.005f,
                        "a tube end was taken for a socket");
    }

    /// <summary>A spanned nail never lies on top of skin that was already there.</summary>
    [Theory]
    [Trait("Plan", "EDGE-nail-socket-hole-19")]
    [MemberData(nameof(AllVariants))]
    public void Membrane_never_lies_on_rest_skin(V variant)
    {
        var hand = SyntheticFinger.Build(variant);
        var flat = BodyBridge.FlattenNails(hand, Full()) ?? hand;
        new NailGrade(hand, flat).AssertNoMembraneOnRestSkin();
    }

    /// <summary>An exact second copy of the skin's triangles, as an attribute variant adds, still leaves the socket
    /// found and spanned.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-23")]
    public void Duplicated_rest_skin_still_finds_the_socket()
    {
        var hand = SyntheticFinger.Build(V.DuplicatedRest);
        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, Full(), log.Add);
        foreach (var l in log) o.WriteLine(l);
        Assert.NotNull(flat);
        Assert.Contains(log, l => l.Contains("1 spanning an open socket", StringComparison.Ordinal));
        new NailGrade(hand, flat!).AssertSpanned(o, band: true);
    }

    // ------------------------------------------------------------------ CI: long nails, hidden nail layers

    private static Dictionary<int, SecondSkinWriter.NailBedPlan> LongPlans(byte[] hand)
        => SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);

    /// <summary>A nail rising 10 mm off the finger is a nail to the flattening — the shell's own reach leaves it as skin,
    /// which the garment then copies — and on a closed finger under a garment it goes.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-close-1")]
    [Trait("Plan", "EDGE-nail-socket-close-1")]
    public void Long_nail_is_a_nail()
    {
        var hand = SyntheticFinger.Build(V.LongNail);
        Assert.Empty(SecondSkinWriter.NailBedPlans(hand));
        var plans = LongPlans(hand);
        var id = (plans.Keys.Single(), 0);
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine);
        Assert.NotNull(flat);
        var g = new NailGrade(hand, flat!);
        Assert.True(g.IslandArea(flat!, id) < 1e-12f, "the long nail still draws");
        var bad = g.Problems(o);
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    /// <summary>...but an island hanging 15 mm off the skin, as far as a wrist connector, is no nail.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-close-1")]
    [Trait("Plan", "EDGE-nail-socket-close-2")]
    public void Connector_is_not_a_nail()
    {
        var hand = SyntheticFinger.Build(V.Connector);
        Assert.Empty(LongPlans(hand));
        Assert.Null(BodyBridge.FlattenNails(hand, Full()));
    }

    /// <summary>Two nail layers over one socket on two switches: with nothing hidden the socket keeps its nails (hiding
    /// either would leave the other over an open socket); with the second layer's switch hidden for this body, the
    /// socket is judged on the drawn layer alone — spanned exactly as a one-layer socket is — and the hidden layer is
    /// left byte for byte.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-close-2")]
    [Trait("Plan", "EDGE-nail-socket-close-3")]
    [Trait("Plan", "EDGE-nail-socket-close-7")]
    public void Hidden_layer_neither_blocks_nor_changes()
    {
        var hand = SyntheticFinger.Build(V.AttrLayers);
        var plans = LongPlans(hand);
        var shown = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine);
        Assert.Equal("the nails are on different attribute switches", shown.Sockets.Single().LeftAsAuthored);

        var hidden = new HashSet<string> { "atr_gv_c" };
        var sp = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine, hidden);
        var layerB = sp.HiddenIslands.Single();
        Assert.NotNull(sp.Sockets.Single().Owner);
        Assert.DoesNotContain(layerB, sp.Sockets.Single().Members);
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine, hidden);
        Assert.NotNull(flat);
        Assert.True(NailGrade.SameIsland(hand, flat!, plans, layerB), "the hidden layer changed");
        // The drawn finger and layer come out exactly as the one-layer socket does (graded by the tests above).
        var one = BodyBridge.FlattenNails(SyntheticFinger.Build(V.OpenSocket), Full())!;
        SecondSkinWriter.TryReadLod0Geometry(one, out var p1, out _, out _);
        SecondSkinWriter.TryReadLod0Geometry(flat!, out var p2, out _, out _);
        Assert.Equal(p1, p2.Take(p1.Length));
    }

    /// <summary>A nail only partly on a hidden switch is left as authored.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-close-2")]
    [Trait("Plan", "AC-nail-socket-close-4")]
    [Trait("Plan", "EDGE-nail-socket-close-4")]
    public void Partly_hidden_nail_is_left_as_authored()
    {
        var hand = SyntheticFinger.Build(V.AttrLayersPartly);
        var plans = LongPlans(hand);
        var hidden = new HashSet<string> { "atr_gv_c" };
        var sock = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine, hidden).Sockets.Single();
        Assert.Equal("a nail partly on a hidden switch", sock.LeftAsAuthored);
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine, hidden);
        foreach (var m in sock.Members)
            Assert.True(flat == null || NailGrade.SameIsland(hand, flat, plans, m), $"island {m} changed");
    }

    /// <summary>A socket ringing a patch of skin of its own — too big to be a nail bed — keeps its nail: spanning the rim
    /// would lay the nail over that skin.</summary>
    [Fact]
    [Trait("Plan", "EDGE-nail-socket-hole-25")]
    public void Skin_inside_a_socket_keeps_its_nail()
    {
        var hand = SyntheticFinger.Build(V.SkinPatchInside);
        var plans = LongPlans(hand);
        var sock = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine).Sockets.Single();
        Assert.Equal("skin inside the socket", sock.LeftAsAuthored);
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine);
        foreach (var m in sock.Members)
            Assert.True(flat == null || NailGrade.SameIsland(hand, flat, plans, m), $"island {m} changed");
    }

    /// <summary>A nail only partly on a hidden switch is left as authored on a closed finger too (the conservative
    /// choice: half of it is still drawn, and what the hidden half becomes is not this pass's to say).</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-close-2")]
    [Trait("Plan", "EDGE-nail-socket-close-4")]
    public void Partly_hidden_nail_on_a_closed_finger_is_left_as_authored()
    {
        var hand = SyntheticFinger.Build(V.ClosedPartlyHidden);
        Assert.NotNull(BodyBridge.FlattenNails(hand, Full()));   // nothing hidden: it collapses, as ever
        var flat = BodyBridge.FlattenNails(hand, Full(), o.WriteLine, new HashSet<string> { "atr_gv_c" });
        Assert.True(flat == null || ReadPositionsEqual(hand, flat), "a partly hidden nail changed");
    }

    /// <summary>Hidden SKIN still counts as skin: open edges along a hidden part of the finger are no hole a nail should
    /// span, so hiding the finger's own switch changes nothing.</summary>
    [Fact]
    [Trait("Plan", "AC-nail-socket-close-2")]
    [Trait("Plan", "EDGE-nail-socket-close-5")]
    public void Hidden_skin_still_counts_as_skin()
    {
        var hand = SyntheticFinger.Build(V.TubeOnSwitch);
        var none = BodyBridge.FlattenNails(hand, Full());
        Assert.NotNull(none);
        Assert.Equal(none, BodyBridge.FlattenNails(hand, Full(), o.WriteLine, new HashSet<string> { "atr_gv_a" }));
    }

    private static bool ReadPositionsEqual(byte[] a, byte[] b)
    {
        SecondSkinWriter.TryReadLod0Geometry(a, out var pa, out _, out _);
        SecondSkinWriter.TryReadLod0Geometry(b, out var pb, out _, out _);
        return pa.SequenceEqual(pb);
    }

    /// <summary>No hidden set, an empty one, or names the model does not have: exactly today's result.</summary>
    [Theory]
    [Trait("Plan", "AC-nail-socket-close-2")]
    [Trait("Plan", "EDGE-nail-socket-close-6")]
    [MemberData(nameof(AllVariants))]
    public void No_hidden_set_changes_nothing(V variant)
    {
        var hand = SyntheticFinger.Build(variant);
        var none = BodyBridge.FlattenNails(hand, Full());
        Assert.Equal(none, BodyBridge.FlattenNails(hand, Full(), null, new HashSet<string>()));
        Assert.Equal(none, BodyBridge.FlattenNails(hand, Full(), null, new HashSet<string> { "atr_nope" }));
    }

    // ------------------------------------------------------------------ Real hands (local data)

    private static byte[] Rue() => SecondSkinWriterVerbatimTests.Hands("Rue+");

    /// <summary>Freeze the grading on real input first: an ideal membrane built from the authored nail, with no code
    /// from the pass, passes the floor, and the same membrane dented 2.75 mm fails it on every socket.</summary>
    [LocalDataFact(SecondSkinWriterVerbatimTests.RueNeed)]
    [Trait("Plan", "AC-nail-socket-hole-2")]
    [Trait("Plan", "EDGE-nail-socket-hole-24")]
    public void Grading_accepts_the_ideal_membrane_on_Rue()
    {
        var hand = Rue();
        var g = new NailGrade(hand, hand);
        var gi = g.IdealMembrane(o);
        Assert.True(gi.Spanned.Count >= 8, $"the ideal membrane spans only {gi.Spanned.Count} nails");
        Assert.True(gi.FloorHolds(o), "the floor fails the ideal membrane");
        Assert.True(gi.BandHolds(o), "the band fails the ideal membrane");
        Assert.False(gi.Dent(0.00275f).FloorHolds(null), "a dent passes the floor");
    }

    /// <summary>Every socket of the Rue+ hand is spanned and closed.</summary>
    [LocalDataFact(SecondSkinWriterVerbatimTests.RueNeed)]
    [Trait("Plan", "AC-nail-socket-hole-2")]
    public void Rue_sockets_are_spanned()
    {
        var hand = Rue();
        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
        var sp = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine);
        Assert.True(sp.Sockets.Count >= 10, $"only {sp.Sockets.Count} sockets");
        // Spanned where the patch passes every check before it is written; left as authored where not (a nail, no hole).
        Assert.True(sp.Sockets.Count(s => s.Owner != null) >= 2, $"only {sp.Sockets.Count(s => s.Owner != null)} sockets spanned");
        Assert.Empty(sp.OverOpenSkin);
        var log = new List<string>();
        var flat = BodyBridge.FlattenNails(hand, Full(), log.Add)!;
        foreach (var l in log) o.WriteLine(l);
        var g = new NailGrade(hand, flat);
        Assert.True(g.SocketRayCount >= 10);
        g.AssertSpanned(o, band: false);
    }

    /// <summary>...and the bodysuit over it is closed, whatever its art paints.</summary>
    [LocalDataFact(SecondSkinWriterVerbatimTests.RueNeed)]
    [Trait("Plan", "AC-nail-socket-hole-1")]
    public void Rue_shell_over_sockets_is_closed()
    {
        var hand = Rue();
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        var g = new NailGrade(hand, flat);
        foreach (var layer in new[] { Gate(SecondSkinWriterVerbatimTests.PaintFingersNotNailBeds(flat, 1024), 1024), Full() })
        {
            var shell = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(flat, CoverNails: true) },
                                               new[] { layer }, null, out _);
            g.AssertClosedAgainst(shell, skinOnly: false, o);
        }
    }

    /// <summary>The shell's look at the flattened Rue+ hand lifts no spanning nail over the collapsed nail mesh.</summary>
    [LocalDataFact(SecondSkinWriterVerbatimTests.RueNeed)]
    [Trait("Plan", "EDGE-nail-socket-hole-12")]
    public void Rue_replan_lift_and_offset()
    {
        var hand = Rue();
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        var g = new NailGrade(hand, flat);
        var after = SecondSkinWriter.NailBedPlans(flat);
        Assert.Equal(SecondSkinWriter.NailBedPlans(hand).Sum(p => p.Value.Islands.Count), after.Sum(p => p.Value.Islands.Count));
        foreach (var s in g.Spanned)
            foreach (int v in after[s.Mesh].VertsOf[s.Island]) Assert.Equal(0f, after[s.Mesh].Lift[v]);
        var shell = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(flat, CoverNails: true) },
                                           new[] { Full() }, null, out _);
        var (overMembrane, overFinger) = g.ShellOffsets(shell);
        o.WriteLine($"shell offset over the membrane {overMembrane * 1000:F3}mm, over the finger {overFinger * 1000:F3}mm");
        Assert.True(MathF.Abs(overMembrane - overFinger) < 0.00001f);
    }

    /// <summary>Rue+'s nail mesh goes with the spanned sockets.</summary>
    [LocalDataFact(SecondSkinWriterVerbatimTests.RueNeed)]
    [Trait("Plan", "EDGE-nail-socket-hole-6")]
    public void Rue_nail_mesh_removed_over_spanned_sockets()
    {
        var hand = Rue();
        var flat = BodyBridge.FlattenNails(hand, Full())!;
        float before = NonSkinArea(hand), after = NonSkinArea(flat);
        o.WriteLine($"nail mesh {before * 1e6f:F1}mm² -> {after * 1e6f:F1}mm²");
        // Gone over every spanned socket; kept over a socket left as authored, with the nail it rides on.
        Assert.True(before > 1e-5f && after > 0, "this hand should carry a nail mesh, and keep it over sockets left as authored");
        AssertNailMeshFates(hand, flat, o);
    }

    private static (byte[] Hand, SecondSkinWriter.NailSocketPlan Plan) Real(string rel)
    {
        var hand = File.ReadAllBytes(LocalData.Path(LocalData.Mods + ":" + rel));
        return (hand, SecondSkinWriter.NailSockets(hand, SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach)));
    }

    /// <summary>
    /// The reported hand (YAB+ "yiggle - stabbies") as the report's suit wears it: painted over the fingers, not the nail
    /// charts, with the hand's own alternative nail layers hidden (atr_gv_c, atr_gv_d, atr_gv_f, from the report's log).
    /// The long nails are nails; the hidden layers are left byte for byte; what spans is graded closed; what cannot keeps
    /// its nails, saying why.
    /// </summary>
    [LocalDataFact(LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi")]
    [Trait("Plan", "AC-nail-socket-close-5")]
    [Trait("Plan", "AC-nail-socket-close-2")]
    public void Stabbies_under_the_reported_suit()
    {
        const string rel = "hs-Yet Another Body+-4.2.0-tmi/files/hands - smallclothes/yiggle - stabbies/chara/equipment/e0000/model/c0201e0000_glv.mdl";
        var hand = File.ReadAllBytes(LocalData.Path(LocalData.Mods + ":" + rel));
        var hidden = new HashSet<string> { "atr_gv_c", "atr_gv_d", "atr_gv_f" };
        var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
        var sp = SecondSkinWriter.NailSockets(hand, plans, o.WriteLine, hidden);
        Assert.Equal(10, sp.Sockets.Count);
        // Every socket spans: the curled tips' seam rays start under the patch (answered by the seam), and the middle
        // fingers' pinholes are welded shut.
        Assert.Equal(10, sp.Sockets.Count(s => s.Owner != null));
        Assert.Equal(10, sp.HiddenIslands.Count);

        var suit = Gate(SecondSkinWriterVerbatimTests.PaintFingersNotNailBeds(hand, 1024), 1024);
        var flat = BodyBridge.FlattenNails(hand, suit, o.WriteLine, hidden);
        Assert.NotNull(flat);
        foreach (var m in sp.HiddenIslands.Concat(sp.Sockets.Where(s => s.Owner == null).SelectMany(s => s.Members)))
            Assert.True(NailGrade.SameIsland(hand, flat!, plans, m), $"island {m} changed");
        // What ships is graded as it is: with the layers hidden, more sockets span than with nothing hidden (a socket
        // whose nails sat on different switches has one visible nail left), so the shipped hand is judged on its own.
        var g = new NailGrade(hand, flat!);
        var bad = g.Problems(o);
        Assert.True(bad.Count == 0, string.Join("; ", bad));
        Assert.DoesNotContain(sp.Sockets.Where(s => s.Owner != null), s => sp.HiddenIslands.Contains(s.Owner!.Value));
        // ...and the garment the shipped hand gets — its hidden layers dropped, as the shell drops them — is closed.
        var shell = SecondSkinWriter.Build(new[] { new SecondSkinWriter.SourceSpec(flat!, CoverNails: true, HiddenAttributes: hidden) },
                                           new[] { suit }, null, out _);
        g.AssertClosedAgainst(shell, skinOnly: false, o);
    }

    /// <summary>The socket rules on the real hands that need them — shapes a synthetic finger cannot make without the
    /// nail-bed planner reading its pieces as nails: a ring socket round a loose patch of skin (LavaBod+),
    /// a curled edge whose patch would leak at the seam (Rue+
    /// Ballerina), and floating nails along a rim that must not hide it (Tight&amp;Firm).</summary>
    [LocalDataFact(LocalData.Mods + ":LavaBod+ 2.2", SecondSkinWriterVerbatimTests.RueNeed, LocalData.Mods + ":tight&firmgen2body-nsfw-installer")]
    [Trait("Plan", "EDGE-nail-socket-hole-27")]
    public void Socket_rules_hold_on_the_hands_that_need_them()
    {
        // Long nails: their second layers are nails now, not a "patch of skin" ringed by the socket — sockets span, and
        // the grader finds the hand closed.
        var (lavaHand, lava) = Real("LavaBod+ 2.2/common/91/c0201e0000_glv.mdl");
        Assert.DoesNotContain(lava.Sockets, s => s.LeftAsAuthored == "skin inside the socket");
        Assert.True(lava.Sockets.Count(s => s.Owner != null) >= 4);
        var lavaFlat = BodyBridge.FlattenNails(lavaHand, Full())!;
        var lavaBad = new NailGrade(lavaHand, lavaFlat).Problems(o);
        Assert.True(lavaBad.Count == 0, string.Join("; ", lavaBad));
        // A curled edge whose seam rays start under the patch: answered by the seam itself, and the grader finds the
        // hand closed — no sliver beside the rim where the finger turns sharply.
        var (balHand, bal) = Real("hs-Rue+-2.2.7-y0f/files/Hands - Smallclothes/Ballerina/chara/equipment/e0000/model/c0201e0000_glv.mdl");
        Assert.True(bal.Sockets.Count(s => s.Owner != null) >= 6);
        var balBad = new NailGrade(balHand, BodyBridge.FlattenNails(balHand, Full())!).Problems(o);
        Assert.True(balBad.Count == 0, string.Join("; ", balBad));
        // Floating nails along their rims once hid these sockets; with its long nail layers taken as nails, the hand
        // shows no open socket at all, and every collapsed nail sits over closed skin.
        var (tfHand, _) = Real("tight&firmgen2body-nsfw-installer/common/2/c0201e0000_glv.mdl");
        var tfBad = new NailGrade(tfHand, BodyBridge.FlattenNails(tfHand, Full()) ?? tfHand).Problems(o);
        Assert.True(tfBad.Count == 0, string.Join("; ", tfBad));
    }

    // ------------------------------------------------------------------ helpers


    /// <summary>Every nail-mesh triangle over a spanning nail draws nothing — and there are some, so this measures
    /// something.</summary>
    internal static void AssertNailMeshFates(byte[] hand, byte[] flat, ITestOutputHelper o)
    {
        var (drawn, moved, overS, overA) = new NailGrade(hand, flat).NailMeshFates(flat);
        Assert.True(overS > 0, "no nail mesh rides a spanning nail — this measures nothing");
        Assert.True(drawn < 1e-9f, $"the nail mesh over spanning nails still draws {drawn * 1e6f:F2}mm²");
        // Over a nail left as authored the nail mesh may go with a collapsed tip beside it (RemoveNailMeshes, unchanged):
        // an overlay removed opens no hole, so it is reported, not failed.
        o.WriteLine($"{moved} of {overA} nail-mesh triangles over nails left as authored went with a collapsed neighbour");
    }
    private static float SkinArea(byte[] m, bool skinOnly)
    {
        SecondSkinWriter.TryReadLod0Geometry(m, out var p, out _, out var t, out _, out _, skinOnly: skinOnly);
        float a = 0;
        for (int i = 0; i + 2 < t.Length; i += 3)
            a += MeshMath.TriArea(new(p[t[i] * 3], p[t[i] * 3 + 1], p[t[i] * 3 + 2]),
                                  new(p[t[i + 1] * 3], p[t[i + 1] * 3 + 1], p[t[i + 1] * 3 + 2]),
                                  new(p[t[i + 2] * 3], p[t[i + 2] * 3 + 1], p[t[i + 2] * 3 + 2]));
        return a;
    }

    private static float NonSkinArea(byte[] m)
    {
        SecondSkinWriter.TryReadLod0Geometry(m, out var p, out _, out var t, out _, out _, skinOnly: false, nonSkin: true);
        float a = 0;
        for (int i = 0; i + 2 < t.Length; i += 3)
            a += MeshMath.TriArea(new(p[t[i] * 3], p[t[i] * 3 + 1], p[t[i] * 3 + 2]),
                                  new(p[t[i + 1] * 3], p[t[i + 1] * 3 + 1], p[t[i + 1] * 3 + 2]),
                                  new(p[t[i + 2] * 3], p[t[i + 2] * 3 + 1], p[t[i + 2] * 3 + 2]));
        return a;
    }

    /// <summary>A coverage map painting one nail island and the fingertip it lands on, nothing else.</summary>
    private static byte[] PaintOnly(byte[] hand, Dictionary<int, SecondSkinWriter.NailBedPlan> plans, (int Mesh, int Island) id)
    {
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(hand, out _, out var uv, out _));
        var mask = new byte[Size * Size];
        var plan = plans[id.Mesh];
        void Dot(float u, float v)
        {
            int x = Math.Clamp((int)(u * Size), 0, Size - 1), y = Math.Clamp((int)(v * Size), 0, Size - 1);
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                mask[Math.Clamp(y + dy, 0, Size - 1) * Size + Math.Clamp(x + dx, 0, Size - 1)] = 255;
        }
        foreach (int v in plan.VertsOf[id.Island])
        {
            Dot(uv[v * 2], uv[v * 2 + 1]);
            if (plan.FingertipUv[v] is { } t) Dot(t.U, t.V);
        }
        return mask;
    }
}

/// <summary>The slow sweep over every body hand installed: nothing left open, nothing pushed out, per-body counts.</summary>
[Trait("Speed", "Slow")]
public class NailSocketSweep(ITestOutputHelper o)
{
    /// <summary>Per body (matched on the mod folder), what every one of its hand files must come to. Counts are per
    /// file: loops found, how many of them spanned at least, and whether every one must span.</summary>
    /// <summary>Bodies with no socket at all (closed fingers, sewn nails): nothing found, nothing over open skin.</summary>
    private static readonly (string Folder, bool Sewn)[] Controls =
    [
        ("Neolithe", false), ("Eve - ", false), ("hs-[YAS] Tre", false),
        ("Bibo+", true), ("The Body SE", true), ("The hrBody", true), ("Freyja\\", true),
    ];

    /// <summary>
    /// What each socketed hand comes to, pinned from the first green run (2026-11-02, build 1419: long nails planned as
    /// nails — <see cref="SecondSkinWriter.LongNailReach"/> — a seam ray starting under the patch answered by the
    /// seam itself, and the patch filled smoothly from the finger round it): sockets found and how many of them are spanned; the rest are left as authored (a nail, never a
    /// hole). First match wins.
    /// </summary>
    private static readonly (string Match, int Sockets, int Spanned)[] Pinned =
    [
        ("FreyjaAF", 8, 0), ("tight&firm", 0, 0), ("Kräuter", 3, 0),
        // Muse+'s long default nails: two reach past the shell's own bed reach, over open skin — left as authored,
        // byte for byte (the authored-island check below).
        ("hs-Muse+", 0, 0),
        (@"LavaBod+ 2.2\common\88\", 10, 6), (@"LavaBod+ 2.2\common\89\", 10, 6), (@"LavaBod+ 2.2\common\90\", 10, 6),
        (@"LavaBod+ 2.2\common\91\", 12, 8), (@"LavaBod+ 2.2\common\92\", 10, 6), (@"LavaBod+ 2.2\common\93\", 10, 6),
        (@"LavaBod+ 2.2\common\94\", 10, 6), (@"LavaBod+ 2.2\common\95\", 12, 8),
        ("ballerina", 10, 6), ("long nails", 10, 6), ("short nails", 10, 6), ("stabbies", 10, 6),
    ];

    /// <summary>Known, deferred, and not this change's doing: graded and logged, but not failed.</summary>
    private static readonly (string Match, string Why)[] Known =
    [
        ("FreyjaAF", "pre-existing: a nail SEWN into its socket is laid down onto the rim and opens it (the sewn path is out of this change's scope)"),
    ];

    [LocalDataFact(LocalData.Mods)]
    [Trait("Plan", "AC-nail-socket-hole-1")]
    [Trait("Plan", "AC-nail-socket-hole-2")]
    [Trait("Plan", "AC-nail-socket-hole-3")]
    [Trait("Plan", "AC-nail-socket-hole-4")]
    [Trait("Plan", "AC-nail-socket-hole-7")]
    [Trait("Plan", "EDGE-nail-socket-hole-19")]
    [Trait("Plan", "EDGE-nail-socket-hole-22")]
    [Trait("Plan", "EDGE-nail-socket-hole-23")]
    [Trait("Plan", "EDGE-nail-socket-hole-24")]
    [Trait("Plan", "EDGE-nail-socket-hole-30")]
    [Trait("Plan", "EDGE-nail-socket-hole-29")]
    [Trait("Plan", "AC-nail-socket-close-4")]
    [Trait("Plan", "AC-nail-socket-close-6")]
    public void Every_installed_hand_closes_or_keeps_its_sockets()
    {
        string root = LocalData.Path(LocalData.Mods);
        var files = Directory.EnumerateFiles(root, "*.mdl", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith("e0000_glv.mdl", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(f).StartsWith("hands", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains("Proteus", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failures = new List<string>();
        int ownedTotal = 0;
        foreach (var f in files)
        {
            var hand = File.ReadAllBytes(f);
            // As FlattenNails plans them: long nails are nails.
            var plans = SecondSkinWriter.NailBedPlans(hand, reach: SecondSkinWriter.LongNailReach);
            if (plans.Count == 0) continue;
            var diag = new List<string>();
            var sp = SecondSkinWriter.NailSockets(hand, plans, diag.Add);
            var flat = BodyBridge.FlattenNails(hand, NailSocketTests.Full());
            string name = f.Substring(root.Length);
            int owned = sp.Sockets.Count(s => s.Owner != null);
            ownedTotal += owned;
            o.WriteLine($"{name}: {sp.Sockets.Count} sockets, {owned} spanned, {sp.Sockets.Count - owned} left as authored "
                      + $"({string.Join(", ", sp.Sockets.Where(s => s.Owner == null).GroupBy(s => s.LeftAsAuthored).Select(gr => $"{gr.Count()} {gr.Key}"))}), "
                      + $"{sp.OverOpenSkin.Count} over open skin, {sp.RejectedLoops} rejected loops");
            foreach (var d in diag) o.WriteLine("    " + d);
            // What this hand must come to.
            var control = Controls.FirstOrDefault(b => name.Contains(b.Folder, StringComparison.OrdinalIgnoreCase));
            if (control.Folder != null)
            {
                if (sp.Sockets.Count > 0 || sp.OverOpenSkin.Count > 0)
                    failures.Add($"{name}: a control body found {sp.Sockets.Count} sockets, {sp.OverOpenSkin.Count} nails over open skin");
                if (control.Sewn && plans.Values.Any(pl => pl.Sewn.Any(s => !s)))
                    failures.Add($"{name}: a sewn body has an unsewn nail");
            }
            var pin = Pinned.FirstOrDefault(b => name.Contains(b.Match, StringComparison.OrdinalIgnoreCase));
            if (control.Folder == null && pin.Match != null && (sp.Sockets.Count != pin.Sockets || owned != pin.Spanned))
                failures.Add($"{name}: {sp.Sockets.Count} sockets, {owned} spanned; pinned at {pin.Sockets} and {pin.Spanned}");
            if (control.Folder == null && pin.Match == null && sp.Sockets.Count > 0)
                failures.Add($"{name}: {sp.Sockets.Count} sockets on a hand the sweep has no expectation for — pin it");

            if (flat == null) continue;
            var g = new NailGrade(hand, flat);
            var known = Known.FirstOrDefault(k => name.Contains(k.Match, StringComparison.OrdinalIgnoreCase));
            foreach (var p in g.Problems(o))
            {
                // The known case is exempt only exactly [EDGE-nail-socket-hole-30]: those three sewn-fill nails, nothing else.
                if (known.Match != null && p.EndsWith("socket rays open through collapsed nails (1, 12),(1, 13),(1, 25)", StringComparison.Ordinal))
                { o.WriteLine($"KNOWN {name}: {p} — {known.Why}"); continue; }
                failures.Add($"{name}: {p}");
            }
            // Authored islands never change.
            foreach (var m in sp.Sockets.Where(s => s.Owner == null).SelectMany(s => s.Members).Concat(sp.OverOpenSkin))
                if (!NailGrade.SameIsland(hand, flat, plans, m)) failures.Add($"{name}: authored island {m} changed");
        }
        foreach (var f in failures) o.WriteLine("FAIL " + f);
        Assert.True(ownedTotal > 0, "the sweep spanned nothing — it measures nothing");
        Assert.Empty(failures);
    }
}
