using Proteus.Services;
using Xunit;
using static Proteus.Tests.VariantRepointTestKit;

namespace Proteus.Tests;

/// <summary>The IMC section of Penumbra's meta blob (<see cref="PenumbraMetaImc"/>).</summary>
public class PenumbraMetaImcTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Plan", "EDGE-imc-all-variants-12")]
    public void Reads_v1_and_v2_imc_records(int version)
    {
        var blob = Blob([(11, 6255, 2, 4, E(1, 2, 3, 4, 5, 6)), (3, 53, 1, 12, E(7)), (3, 53, 1, 14, E(8)),
                         (2, 201, 1, 0, E(9))],   // a weapon: not gear, ignored
                        version);
        var map = PenumbraMetaImc.Parse(blob)!;
        Assert.Equal(3, map.Count);
        Assert.Equal(E(1, 2, 3, 4, 5, 6), map[new('e', 6255, "Body", 2)]);
        Assert.Equal(E(7), map[new('a', 53, "Right ring", 1)]);
        Assert.Equal(E(8), map[new('a', 53, "Left ring", 1)]);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-12")]
    public void Unknown_or_truncated_blob_is_null()
    {
        Assert.Null(PenumbraMetaImc.Parse(null));
        Assert.Null(PenumbraMetaImc.Parse("not base64!"));
        Assert.Null(PenumbraMetaImc.Parse(Blob([(11, 6255, 2, 4, E(1))], truncate: true)));
        Assert.Null(PenumbraMetaImc.ParseDecompressed([3, (byte)'M', (byte)'E', (byte)'T', (byte)'A', (byte)'0', (byte)'0', (byte)'0', (byte)'3', 0, 0, 0, 0]));
        // A gear record in a slot the layout does not have: the layout moved, trust none of it.
        Assert.Null(PenumbraMetaImc.Parse(Blob([(11, 6255, 2, 13, E(1))])));
        Assert.Empty(PenumbraMetaImc.Parse(Blob([]))!);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-12")]
    public void Gear_slot_under_the_other_object_type_is_skipped_not_fatal()
    {
        // Penumbra accepts an Equipment identifier with an accessory slot: one such entry must not blind the collection.
        var map = PenumbraMetaImc.Parse(Blob([(11, 53, 1, 12, E(5)), (11, 6255, 2, 4, E(1))]))!;
        Assert.Equal(E(1), Assert.Single(map).Value);
    }
}
