using System;

namespace Proteus.Gui;

/// <summary>
/// How many dabs a held brush owes this frame, so a stroke does the same in the same time on any PC. Without it a brush
/// dabbed once per frame: at 144 fps a held Pull out pushed more than twice as far per second as at 60, and a stutter
/// weakened it.
/// <para/>
/// A fixed clock rather than scaling each dab by the frame's length: relax, smooth, bridge and wind are rates that
/// compound dab on dab, and a smooth's Taubin pair goes unstable when one step is stretched over a long frame. At
/// <see cref="DabsPerSecond"/> every brush paints exactly as it did at 60 fps.
/// </summary>
internal sealed class DabClock
{
    /// <summary>The rate dabs land at while the brush is held.</summary>
    public const int DabsPerSecond = 60;

    /// <summary>Dabs per quarter second — the unit the strength slider shows a distance brush in.</summary>
    public const float DabsPerQuarterSecond = DabsPerSecond / 4f;

    private const float Interval = 1f / DabsPerSecond;

    /// <summary>
    /// The longest a single frame may count for, in seconds: a hitch (a save, a reload) must not land a burst of dabs
    /// all at once on whatever is under the mouse.
    /// </summary>
    private const float MaxFrame = 0.1f;

    private float owed;
    private bool running;

    /// <summary>
    /// The dabs owed for a frame of <paramref name="seconds"/> while the brush is held. The press itself always
    /// dabs at once, so a click paints however short it is.
    /// </summary>
    public int Take(float seconds)
    {
        if (!running)
        {
            running = true;
            owed = 0f;
            return 1;
        }

        owed += Math.Clamp(seconds, 0f, MaxFrame);
        int dabs = (int)(owed / Interval);
        owed -= dabs * Interval;
        return dabs;
    }

    /// <summary>The brush came up: the next press starts the clock afresh.</summary>
    public void Stop()
    {
        running = false;
        owed = 0f;
    }
}
