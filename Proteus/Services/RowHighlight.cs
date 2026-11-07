using System;
using System.Collections.Generic;
using System.IO;

namespace Proteus.Services;

// The decisions behind "point at a colour-table row and see it on the character". Pure: no Dalamud or ImGui types,
// so every rule here is unit-tested. The two live highlighters (ColorTableHighlighter for gear, SkinDiffuseGlow for
// skin) and the row picker (ColorTableEditor.DrawRows) only wire these up.

/// <summary>Which sub-row(s) of a row pair a skin highlight lights: one of the pair (the Glow button) or both (hover).</summary>
public enum SubRowPick { A, B, Both }

/// <summary>Bit masks over the 32 game colour-table rows. Row pair <c>r</c> (1-16) is game rows <c>2(r-1)</c> (A) and
/// <c>2(r-1)+1</c> (B). Anything out of range is an empty mask, so a bad index paints nothing rather than throwing.</summary>
public static class ColorRowMask
{
    /// <summary>Both game rows of row pair <paramref name="row"/> (1-16).</summary>
    public static uint Pair(int row) => row is < 1 or > 16 ? 0u : 3u << ((row - 1) * 2);

    /// <summary>One sub-row of row pair <paramref name="row"/> (1-16).</summary>
    public static uint Sub(int row, bool isA) => row is < 1 or > 16 ? 0u : 1u << ((row - 1) * 2 + (isA ? 0 : 1));

    /// <summary>One game row (0-31).</summary>
    public static uint Row(int gameRow) => gameRow is < 0 or > 31 ? 0u : 1u << gameRow;

    public static bool Contains(uint mask, int gameRow) => gameRow is >= 0 and <= 31 && (mask & (1u << gameRow)) != 0;
}

public enum HighlightRoute { None, Gear, Skin }

/// <summary>Which highlighter can show a table's rows. Shared by the sub-row Glow button and the row hover, so the two
/// never disagree: gear/cloth/glow tables hue-cycle their shell materials' live colour table; skin tables repaint the
/// composited body diffuse. Either needs its highlighter and something on the character to find.</summary>
public static class RowHighlightRoute
{
    public static HighlightRoute Pick(RenderMode mode, bool hasHighlighter, int shellLeafCount,
                                      bool hasSkinGlow, int skinTargetCount)
        => mode != RenderMode.Skin
            ? (hasHighlighter && shellLeafCount > 0 ? HighlightRoute.Gear : HighlightRoute.None)
            : (hasSkinGlow && skinTargetCount > 0 ? HighlightRoute.Skin : HighlightRoute.None);
}

/// <summary>The one clock the hover uses, on both sides: the UI that renews a preview and the framework tick that
/// expires it.</summary>
public static class RowHighlightClock
{
    public static long NowMs() => Environment.TickCount64;
}

/// <summary>A file's last-write time and length: changes whenever the file is rewritten. (0,0) when missing or unreadable.</summary>
public readonly record struct FileStamp(long WriteTicks, long Length)
{
    public static FileStamp Of(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new FileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length) : default;
        }
        catch { return default; }
    }
}

/// <summary>
/// Keeps a hover preview alive only while the UI keeps renewing it, so a window that stops drawing (closed main
/// window, UI hidden in a cutscene) can't strand a highlight. Expiry needs BOTH the time and at least two framework
/// ticks without a renewal: one long frame (a hitch, a zone load) must not drop a preview that is still hovered.
/// Main thread only — the UI draw and the framework tick share it.
/// </summary>
public sealed class PreviewKeepAlive
{
    public const long TtlMs = 500;
    public const int MinTicks = 2;

    private long _lastRenew;
    private int _ticks;

    public void Renew(long nowMs) { _lastRenew = nowMs; _ticks = 0; }

    public void Tick() { if (_ticks < int.MaxValue) _ticks++; }

    public bool Expired(long nowMs) => nowMs - _lastRenew >= TtlMs && _ticks >= MinTicks;
}

/// <summary>
/// #82: a pinned Glow lives only while an open Colors window keeps renewing it, so the plugin UI being hidden (a cutscene,
/// the UI toggled off) can't leave a pin lit with no window to turn it off. Same keep-alive rule as a hover preview.
/// </summary>
public static class RowHighlight
{
    /// <summary>Whether the pin (if any) has gone unrenewed past <see cref="PreviewKeepAlive"/>'s time and tick rule.</summary>
    public static bool PinExpired(bool pinned, PreviewKeepAlive keep, long nowMs) => pinned && keep.Expired(nowMs);
}

/// <summary>
/// Turns the row under the pointer, frame by frame, into the row to highlight. A new row must be held for the dwell
/// (skin only: each skin preview is a full-resolution repaint, so a sweep must not start one per row passed); until
/// then the previous row stays lit. With nothing hovered the active row survives the grace, so the gap between two
/// buttons doesn't flash the character back.
/// </summary>
public sealed class RowHoverDwell
{
    public const long GraceMs = 150, SkinDwellMs = 200, GearDwellMs = 0;

    private (string Scope, int Row)? _active;
    private (string Scope, int Row)? _candidate;
    private long _candidateSince;
    private long _lastHover;

    public (string Scope, int Row)? Update((string Scope, int Row)? hovered, long nowMs, long dwellMs)
    {
        if (hovered is { } h)
        {
            _lastHover = nowMs;
            if (_active == h) { _candidate = null; return _active; }
            if (_candidate != h) { _candidate = h; _candidateSince = nowMs; }
            if (nowMs - _candidateSince >= dwellMs) { _active = h; _candidate = null; }
            return _active;
        }

        _candidate = null;
        if (_active != null && nowMs - _lastHover >= GraceMs) _active = null;
        return _active;
    }

    public void Reset() { _active = null; _candidate = null; }
}

/// <summary>What one hovered row button offers: the table it belongs to and where its highlight would go.</summary>
public sealed record RowHoverOffer(string Scope, int Row, HighlightRoute Route,
    IReadOnlyList<string>? ShellLeaves, IReadOnlyList<Proteus.Interop.SkinGlowTarget>? SkinTargets);

/// <summary>This frame's hover offer. Several tables can draw in one frame; only one can be under the pointer, so the
/// first offer wins and a later table that isn't hovered never clears it.</summary>
public sealed class RowHoverFrame
{
    private RowHoverOffer? _offer;

    public void Offer(RowHoverOffer offer) => _offer ??= offer;

    /// <summary>This frame's offer, cleared for the next frame.</summary>
    public RowHoverOffer? Take()
    {
        var o = _offer;
        _offer = null;
        return o;
    }
}

public enum HoverAction { EndAll, Gear, Skin, WaitSkin }

public static class RowHoverCommit
{
    /// <summary>What the frame's hover does. A skin build never starts while a composite is pending or running (it
    /// rewrites the diffuses a build reads); the current skin preview is kept meanwhile.</summary>
    public static HoverAction Decide(bool pluginEnabled, HighlightRoute? activeRoute,
                                     bool compositeBusy, bool skinNeedsBuild)
        => !pluginEnabled ? HoverAction.EndAll
         : activeRoute switch
           {
               HighlightRoute.Gear => HoverAction.Gear,
               HighlightRoute.Skin => compositeBusy && skinNeedsBuild ? HoverAction.WaitSkin : HoverAction.Skin,
               _ => HoverAction.EndAll,
           };
}

/// <summary>When a skin preview needs a (re)build. Identity is the compositor's target list by reference (a composite
/// publishes a new list, so republished row maps rebuild), the row and the pick. A failed build retries no sooner than
/// <see cref="FailedRetryMs"/>, at most <see cref="MaxAttempts"/> times per hover of that target.</summary>
public static class SkinPreviewRule
{
    public const long FailedRetryMs = 500;
    public const int MaxAttempts = 3;

    public static bool SameTarget(object listA, int rowA, SubRowPick pickA,
                                  object listB, int rowB, SubRowPick pickB)
        => ReferenceEquals(listA, listB) && rowA == rowB && pickA == pickB;

    public static bool NeedsBuild(bool sameAsCurrent, bool currentFailed, int attempt, long failedAtMs, long nowMs)
        => !sameAsCurrent
        || (currentFailed && attempt < MaxAttempts && nowMs - failedAtMs >= FailedRetryMs);
}
