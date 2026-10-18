using System;
using System.Linq;
using XivLiveMesh;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The skeleton reader on tagfiles built in memory: both versions the game and skeleton mods are written in, the
/// skeleton named by the animation container rather than a mapper's, and damaged files turned down without a throw.
/// </summary>
public class HavokSkeletonTests
{
    private static readonly SyntheticSkeleton.Tree Main = SyntheticSkeleton.Human("c0301", ("iv_extra", "j_kosi"));

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [Trait("Plan", "AC-refit-other-race-1")]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void Both_versions_read_as_the_container_s_skeleton(int version)
    {
        var s = HavokSkeleton.Read(SyntheticSkeleton.Sklb(Main, version), out string why);
        Assert.True(s != null, why);
        Assert.Equal(Main.Bones, s!.Names);
        Assert.Equal(Main.Parents, s.Parents);

        var parents = s.ParentMap();
        Assert.Null(parents["n_root"]);
        Assert.Equal("n_hara", parents["j_kosi"]);
        Assert.Equal("j_kosi", parents["iv_extra"]);
    }

    /// <summary>A mapper's skeletons come first in the file, and one is bigger: still not the one taken.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void A_mapper_s_skeletons_are_not_the_one_taken()
    {
        var s = HavokSkeleton.Read(SyntheticSkeleton.Sklb(Main, 5));
        Assert.NotNull(s);
        Assert.Contains("iv_extra", s!.Names);
        Assert.DoesNotContain("x", s.Names);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void Without_a_container_the_largest_skeleton_is_taken()
    {
        var big = new SyntheticSkeleton.Tree("big", Enumerable.Range(0, 15).Select(i => $"b{i}").ToArray(),
                                             Enumerable.Range(-1, 15).ToArray());
        var s = HavokSkeleton.Read(SyntheticSkeleton.Sklb(Main, 3, decoyA: big, withContainer: false));
        Assert.NotNull(s);
        Assert.Equal(big.Bones, s!.Names);   // bigger than the main one, with no container to say otherwise

        // With the container, the main one all the same.
        Assert.Equal(Main.Bones, HavokSkeleton.Read(SyntheticSkeleton.Sklb(Main, 3, decoyA: big))!.Names);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void A_parent_after_its_child_is_not_a_tree()
    {
        var looped = Main with { Parents = Main.Parents.Select((p, i) => i == 2 ? 5 : p).ToArray() };
        Assert.Null(HavokSkeleton.Read(SyntheticSkeleton.Sklb(looped, 3, withContainer: true), out string why));
        Assert.Contains("no bone tree", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void No_tagfile_at_all_says_so()
    {
        Assert.Null(HavokSkeleton.Read(new byte[64], out string why));
        Assert.Contains("no Havok tagfile", why);
    }

    /// <summary>
    /// A file that asks for far more than its size could describe — a million structs of a class with no members, or
    /// classes inheriting from each other four hundred deep — is turned down before it is built, not built until the
    /// game runs out of memory.
    /// </summary>
    [Theory]
    [InlineData(1 << 20)]
    [InlineData(5000)]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void A_file_asking_for_more_than_its_size_holds_is_turned_down(int count)
    {
        var bytes = SyntheticSkeleton.EmptyStructs(count);
        Assert.True(bytes.Length < 64);
        Assert.Null(HavokSkeleton.Read(bytes, out string why));
        Assert.Contains("unreadable", why);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void A_deep_chain_of_classes_is_turned_down()
    {
        var bytes = SyntheticSkeleton.DeepClasses(400, 8);
        Assert.Null(HavokSkeleton.Read(bytes, out string why));
        Assert.Contains("more than a file of this size", why);
        // A shallow one is only a file with no skeleton in it.
        Assert.Null(HavokSkeleton.Read(SyntheticSkeleton.DeepClasses(3, 2), out why));
        Assert.Contains("no bone tree", why);
    }

    /// <summary>Cut anywhere, or with bytes changed, a file reads as null; nothing it holds can make the reader throw.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [Trait("Plan", "EDGE-refit-other-race-18")]
    public void A_damaged_file_never_throws(int version)
    {
        var bytes = SyntheticSkeleton.Sklb(Main, version);
        for (int cut = 0; cut < bytes.Length; cut++)
            _ = HavokSkeleton.Read(bytes.AsSpan(0, cut));

        var random = new Random(7);
        for (int round = 0; round < 2000; round++)
        {
            var damaged = (byte[])bytes.Clone();
            for (int k = 0; k < 3; k++) damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            _ = HavokSkeleton.Read(damaged);
        }

        // Runs of bytes with the continuation bit set read as ints far past the end: refused, not allocated for.
        for (int start = 16; start + 12 < bytes.Length; start += 7)
        {
            var huge = (byte[])bytes.Clone();
            for (int i = start; i < start + 12; i++) huge[i] = 0xFE;
            _ = HavokSkeleton.Read(huge);
        }
    }
}
