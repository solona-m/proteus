using System.Linq;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Matcaps row picker's rows (<see cref="MatcapStudio.UsedRows"/>): rows the art really uses, not the sprinkle of
/// in-between rows block compression leaves along the edges between two painted rows.
/// </summary>
public class MatcapRowPickerTests
{
    /// <summary>An index map: <paramref name="texels"/> of (row pair, sub-row A?) each.</summary>
    private static byte[] Index(params (int Row, bool A, int Count)[] texels)
        => texels.SelectMany(t => Enumerable.Repeat(new byte[] { (byte)((t.Row - 1) * 17), (byte)(t.A ? 255 : 0), 0, 255 }, t.Count))
                 .SelectMany(px => px).ToArray();

    [Fact]
    public void Rows_under_half_a_percent_are_compression_noise_and_hidden()
    {
        // The Farfalla jacket's shape: rows 1 B and 16 A painted, 15 B the stripes, 8 and 12 edge noise.
        var rgba = Index((1, false, 3800), (16, true, 5500), (15, false, 600), (8, false, 20), (12, true, 10));
        var (rows, sub) = MatcapStudio.UsedRows(rgba, p => p[0], p => p[1] > 127);
        Assert.Equal(new[] { 1, 15, 16 }, rows!.Order());
        Assert.Null(sub);                      // both columns are painted
    }

    [Fact]
    public void One_column_is_named_when_the_used_rows_agree_on_it()
    {
        var rgba = Index((3, false, 900), (5, false, 100), (5, true, 1));   // one stray A texel
        var (rows, sub) = MatcapStudio.UsedRows(rgba, p => p[0], p => p[1] > 127);
        Assert.Equal(new[] { 3, 5 }, rows!.Order());
        Assert.Equal("B", sub);
    }

    [Fact]
    public void Nothing_qualifying_is_unknown_not_empty()
    {
        Assert.Equal((null, null), MatcapStudio.UsedRows([], p => p[0], null));
    }
}
