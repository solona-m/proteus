using System;
using System.Numerics;

namespace Proteus.Gui;

/// <summary>
/// The window widening for the part drawer, and giving that width back when the drawer closes. Sizes are unscaled,
/// like every size <see cref="StatusWindow"/> keeps. The width is given back only while the window is still the size
/// the drawer gave it: a drag, a fit or the model view's own grow since then made the size someone else's.
/// </summary>
internal sealed class DrawerResize
{
    /// <summary>How far, per axis, the live size may sit from the size the drawer gave it and still count as that size:
    /// on a fractional scale the window lands a fraction of a pixel off what was asked.</summary>
    private const float Tolerance = 1f;

    private Vector2? preOpen, asked, landed;

    /// <summary>The drawer's grow was asked for and the size it actually came out at is not known yet.</summary>
    public bool AwaitingLanding => preOpen != null && landed == null;

    /// <summary>
    /// The size to give the window now the drawer opened, <paramref name="grow"/> wider and no wider than the screen;
    /// null for none. Never while the window fits itself: the fit already measures the drawer.
    /// </summary>
    public Vector2? Open(Vector2 current, float grow, bool fitting, float screenWidth)
    {
        Forget();
        if (fitting || !(grow > 0f)) return null;

        var size = new Vector2(MathF.Min(current.X + grow, MathF.Max(current.X, screenWidth)), current.Y);
        if (size.X <= current.X) return null;

        preOpen = current;
        asked = size;
        return size;
    }

    /// <summary>The size the window came out at once the grow landed, read back from the live window.</summary>
    public void Landed(Vector2 actual)
    {
        if (preOpen != null) landed = actual;
    }

    /// <summary>The size to give the window back now the drawer closed — the one it had before opening — or null.</summary>
    public Vector2? Close(Vector2 current, bool fitting)
    {
        var restore = !fitting && InEffect(current) ? preOpen : null;
        Forget();
        return restore;
    }

    /// <summary>The width to remember between sessions: the drawer starts closed next time, so its share is left out.</summary>
    public float SavedWidth(Vector2 current) => InEffect(current) ? preOpen!.Value.X : current.X;

    /// <summary>The window was sized afresh (a fit, the model view's grow): nothing of the drawer's is left to give back.</summary>
    public void Forget() => preOpen = asked = landed = null;

    private bool InEffect(Vector2 current)
        => preOpen != null && (landed ?? asked) is { } given
           && MathF.Abs(current.X - given.X) <= Tolerance && MathF.Abs(current.Y - given.Y) <= Tolerance;
}

/// <summary>
/// The part drawer opening or closing, posted by <see cref="PartsPanel"/> and taken by the window on its next frame —
/// once, so the window grows by the drawer once rather than every frame it stays open.
/// </summary>
internal sealed class DrawerRequest
{
    /// <param name="Open">Opened, or closed.</param>
    /// <param name="Grow">On opening, how much wider the window should be, unscaled; 0 on closing.</param>
    public readonly record struct Pending(bool Open, float Grow);

    private Pending? pending;

    public void Opened(float grow) => pending = new Pending(true, grow);

    public void Closed() => pending = new Pending(false, 0f);

    /// <summary>What is waiting, and nothing the next time.</summary>
    public Pending? Take()
    {
        var p = pending;
        pending = null;
        return p;
    }
}
