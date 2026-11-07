using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>The Matcaps pane's thumbnail grid fits its tiles to the pane (the last column was cut off).</summary>
public class MatcapGridTests
{
    [Fact]
    public void A_tile_counts_the_image_buttons_frame_padding()
    {
        Assert.Equal(64f, MatcapPanel.TileWidth(56f, 4f));
        Assert.Equal(56f, MatcapPanel.TileWidth(56f, 0f));
    }

    [Fact]
    public void Columns_never_run_past_the_pane()
    {
        // The reported pane: 56 px pictures, 4 px frame padding, 8 px gaps. Counting the picture alone fitted one
        // column more than the pane holds.
        const float picture = 56f, pad = 4f, gap = 8f;
        float tile = MatcapPanel.TileWidth(picture, pad);
        for (float avail = 40f; avail <= 1200f; avail += 1f)
        {
            float x = 0f, right = 0f;
            for (int i = 0; i < 50; i++)
            {
                if (MatcapPanel.WrapsBefore(x, tile, avail)) x = 0f;
                right = x + tile;
                if (x > 0f) Assert.True(right <= avail, $"tile {i} ends at {right} in a pane {avail} wide");
                x += tile + gap;
            }
        }
    }

    [Fact]
    public void A_rows_first_tile_never_wraps()
    {
        Assert.False(MatcapPanel.WrapsBefore(0f, 64f, 10f));   // narrower than one tile: it still draws, on its own row
        Assert.True(MatcapPanel.WrapsBefore(72f, 64f, 100f));
        Assert.False(MatcapPanel.WrapsBefore(72f, 64f, 136f)); // exactly fits
    }
}
