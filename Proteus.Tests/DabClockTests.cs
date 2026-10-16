using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>The brush's dab clock: the same dabs in the same time, whatever the frame rate.</summary>
public class DabClockTests
{
    /// <summary>Dabs over <paramref name="seconds"/> of holding the brush at <paramref name="fps"/>.</summary>
    private static int Held(float fps, float seconds)
    {
        var clock = new DabClock();
        int frames = (int)(fps * seconds), dabs = 0;
        for (int f = 0; f < frames; f++) dabs += clock.Take(1f / fps);
        return dabs;
    }

    /// <summary>A second held lands sixty dabs, give or take one, at 30, 60, 144 and 240 fps alike.</summary>
    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(144f)]
    [InlineData(240f)]
    public void ASecondIsSixtyDabsAtAnyFrameRate(float fps)
        => Assert.InRange(Held(fps, 1f), DabClock.DabsPerSecond - 1, DabClock.DabsPerSecond + 1);

    /// <summary>The press itself paints, however quick the click.</summary>
    [Fact]
    public void ThePressDabsAtOnce()
    {
        var clock = new DabClock();
        Assert.Equal(1, clock.Take(0f));
        Assert.Equal(0, clock.Take(0.001f));
    }

    /// <summary>A hitch counts for at most a tenth of a second, not a burst of every dab it held up.</summary>
    [Fact]
    public void AHitchIsNotABurst()
    {
        var clock = new DabClock();
        clock.Take(0f);
        Assert.Equal(6, clock.Take(2f));
    }

    /// <summary>Letting go starts the next press afresh: nothing owed carries over.</summary>
    [Fact]
    public void StopForgetsWhatWasOwed()
    {
        var clock = new DabClock();
        clock.Take(0f);
        clock.Take(0.012f);   // most of a dab owed
        clock.Stop();
        Assert.Equal(1, clock.Take(0.012f));   // a fresh press: one dab, not the leftover plus one
        Assert.Equal(0, clock.Take(0.012f));   // 12 ms since the press: not a dab yet
        Assert.Equal(1, clock.Take(0.012f));   // 24 ms: one owed
    }
}
