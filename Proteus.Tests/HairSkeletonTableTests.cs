using System;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>The game's table of which skeleton each race's hairstyle loads, read as the game lays it out.</summary>
public class HairSkeletonTableTests
{
    [Fact]
    [Trait("Plan", "AC-hair-skeleton-infer-2")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-21")]
    public void A_table_reads_which_skeleton_each_hairstyle_loads()
    {
        var table = HairSkeletonTable.Parse(EstBytes((201, 8, 9), (201, 9, 10), (401, 8, 9), (1201, 9, 9)))!;
        Assert.Equal(4, table.Count);
        Assert.Equal(9, table.Loaded(201, 8));
        Assert.Equal(10, table.Loaded(201, 9));
        Assert.Equal(0, table.Loaded(201, 7));
        Assert.Equal(0, table.Loaded(801, 8));
        Assert.Equal([9], table.SetsLoading(1201, 9).ToArray());
        Assert.Equal([8], table.SetsLoading(201, 9).ToArray());
        Assert.Equal([9], table.SkeletonsLoaded(201, except: 9).ToArray());
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-21")]
    public void The_order_of_the_pairs_does_not_change_what_is_read()
    {
        var sorted = EstBytes((201, 8, 9), (401, 8, 9));
        Assert.True(HairSkeletonTable.IsSorted(sorted));
        // The same pairs written the other way round.
        var swapped = (byte[])sorted.Clone();
        Array.Copy(sorted, 4, swapped, 8, 4);
        Array.Copy(sorted, 8, swapped, 4, 4);
        Assert.False(HairSkeletonTable.IsSorted(swapped));
        var table = HairSkeletonTable.Parse(swapped)!;
        Assert.Equal(9, table.Loaded(201, 8));
        Assert.Equal(9, table.Loaded(401, 8));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-skeleton-infer-21")]
    public void A_file_that_is_not_a_whole_table_reads_as_none()
    {
        Assert.Null(HairSkeletonTable.Parse(null));
        Assert.Null(HairSkeletonTable.Parse([1, 0]));
        var whole = EstBytes((201, 8, 9), (401, 8, 9));
        Assert.Null(HairSkeletonTable.Parse(whole[..^1]));
        // A count beyond the file.
        Assert.Null(HairSkeletonTable.Parse([0xff, 0xff, 0xff, 0x7f, 0, 0]));
        // A pair twice.
        Assert.Null(HairSkeletonTable.Parse(EstBytes((201, 8, 9), (201, 8, 10))));
        Assert.NotNull(HairSkeletonTable.Parse(EstBytes()));
        Assert.Null(HairSkeletonTable.Read(_ => throw new System.IO.IOException("locked")));
    }
}
