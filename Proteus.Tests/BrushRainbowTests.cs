using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>The colour scale the brush's reach is shown in.</summary>
public class BrushRainbowTests
{
    [Fact]
    public void RedInTheMiddleVioletAtTheRim()
    {
        Assert.Equal((255, 0, 0), BrushRainbow.At(1f));
        Assert.Equal((128, 0, 255), BrushRainbow.At(0f));
        Assert.Equal((0, 255, 0), BrushRainbow.At(1f - 120f / 270f));   // green on the way
    }

    [Fact]
    public void OutOfRangeWeightsClamp()
    {
        Assert.Equal(BrushRainbow.At(1f), BrushRainbow.At(2f));
        Assert.Equal(BrushRainbow.At(0f), BrushRainbow.At(-1f));
    }

    /// <summary>Packed for ImGui as ABGR: red in the low byte, alpha in the high.</summary>
    [Fact]
    public void PacksAsAbgr()
    {
        Assert.Equal(0xFF0000FFu, BrushRainbow.Abgr(1f, 1f));
        Assert.Equal(0x000000FFu, BrushRainbow.Abgr(1f, 0f));
    }
}
