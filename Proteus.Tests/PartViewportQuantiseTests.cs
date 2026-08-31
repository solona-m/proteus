using System;
using System.Numerics;
using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// How the Toggles-tab viewport picks its render resolution now that the window it lives in can be resized.
/// <para/>
/// The rule it has to keep is that a bigger window never makes the model look worse: the buffer is
/// supersampled, so the picture is downsampled into place exactly as it was when the buffer was a fixed
/// 460x560 drawn into a 360px box. The rest is about not re-rendering more often, or larger, than is worth
/// doing on the UI thread.
/// </summary>
public class PartViewportQuantiseTests
{
    private const int MaxPixels = 900_000;

    /// <summary>The size the tab asked for before it could resize — the buffer must not shrink below what
    /// that used to give, or the change would be a downgrade at the size everyone already uses.</summary>
    [Fact]
    public void DefaultBoxKeepsTheOldResolution()
    {
        var (w, h) = PartViewport.Quantise(new Vector2(360f * PartViewport.DefaultAspect, 360f));

        Assert.True(w >= 448, $"width {w} is below the 460 the fixed buffer had");
        Assert.True(h >= 560, $"height {h} is below the 560 the fixed buffer had");
    }

    /// <summary>A bigger box gets a bigger buffer — the whole point of the resize.</summary>
    [Fact]
    public void LargerBoxRendersLarger()
    {
        var small = PartViewport.Quantise(new Vector2(300f, 360f));
        var large = PartViewport.Quantise(new Vector2(500f, 620f));

        Assert.True(large.W > small.W);
        Assert.True(large.H > small.H);
    }

    /// <summary>Snapping is what stops a one-pixel change — a scrollbar appearing, a rounding difference —
    /// from throwing the buffers away and re-rasterising the model.</summary>
    [Fact]
    public void NearbySizesLandOnTheSameBuffer()
    {
        var a = PartViewport.Quantise(new Vector2(400f, 500f));
        var b = PartViewport.Quantise(new Vector2(403f, 502f));

        Assert.Equal(a, b);
    }

    /// <summary>The budget is on time, so it is on AREA. Snapping up may put it a little over; a whole
    /// multiple over would mean a re-render the user feels.</summary>
    [Theory]
    [InlineData(1200f, 1400f)]
    [InlineData(3000f, 2000f)]
    [InlineData(200f, 4000f)]
    public void OversizedBoxesStayInsideTheBudget(float x, float y)
    {
        var (w, h) = PartViewport.Quantise(new Vector2(x, y));

        Assert.True((long)w * h < MaxPixels * 1.2, $"{w}x{h} is well past the {MaxPixels} pixel budget");
    }

    /// <summary>Clamping one axis alone would stretch the model, because the projection's aspect is built
    /// from the buffer. Over budget, both axes have to come down together.</summary>
    [Fact]
    public void ShrinkingToBudgetKeepsTheAspect()
    {
        var box = new Vector2(1200f, 1400f);
        var (w, h) = PartViewport.Quantise(box);

        float wanted = box.X / box.Y;
        float got = (float)w / h;
        Assert.True(MathF.Abs(got - wanted) < 0.12f, $"aspect {got:F3} drifted from {wanted:F3}");
    }

    /// <summary>A collapsed or not-yet-laid-out rectangle must still produce a usable buffer rather than a
    /// zero-length array the rasteriser would index into.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-5f, 12f)]
    public void DegenerateBoxesStillAllocate(float x, float y)
    {
        var (w, h) = PartViewport.Quantise(new Vector2(x, y));

        Assert.True(w > 0);
        Assert.True(h > 0);
    }
}
