using System;

namespace Proteus.Services;

/// <summary>
/// When the live brush's preview may be shared with sync pairs, and when locked parts come back on the character so
/// it can be. The character and a pair always see the same redirect, so sharing is only ever the saved model, whole.
/// </summary>
internal static class LivePreviewPolicy
{
    /// <summary>
    /// How long the brush must be left alone, off the garment, before locked parts show again and the whole model is
    /// shared. Longer than the 1.5 s autosave, so the save has landed; short enough that a pair catches up in seconds.
    /// </summary>
    public const long LockRevealIdleMs = 3000;

    /// <summary>
    /// The mod file the character can be shown as is the one on disk: nothing changing, nothing waiting to save, every
    /// part drawn, and the file still the one last saved or opened.
    /// </summary>
    public static bool ShouldShare(bool editing, bool savePending, bool hidesParts, bool savedMatches)
        => !editing && !savePending && !hidesParts && savedMatches;

    /// <summary>Show the locked parts again: they would be hidden, and the brush has been left idle long enough.</summary>
    public static bool ShouldRevealLocked(bool locksWouldHide, bool editing, bool hovering, bool savePending,
                                          bool savedMatches, long msSinceActivity)
        => locksWouldHide && !editing && !hovering && !savePending && savedMatches
        && msSinceActivity >= LockRevealIdleMs;

    /// <summary>Put revealed parts away again: the brush is back on the garment, or an edit has begun.</summary>
    public static bool ShouldHideAgain(bool editing, bool hovering) => editing || hovering;

    /// <summary>
    /// A share that could not be made (no managed mod yet, a failed copy) is tried again only once something it
    /// depended on has changed — never every frame on the same answer.
    /// </summary>
    public static bool OwedShareDue(bool owed, string? parkedOn, string current)
        => owed && !string.Equals(parkedOn, current, StringComparison.Ordinal);
}

/// <summary>The mod file the edit on screen was last saved to (or opened from), as it stood on disk then.</summary>
internal sealed record SavedEdit(string ModDir, string Rel, string FullPath, long Length, DateTime WriteUtc)
{
    /// <summary>Identifies this exact save, for telling whether it is already shared.</summary>
    public string Stamp => $"{FullPath}|{Length}|{WriteUtc.Ticks}";

    public bool Matches(string? modDir, string? rel, long length, DateTime writeUtc)
        => string.Equals(ModDir, modDir, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Norm(Rel), Norm(rel), StringComparison.OrdinalIgnoreCase)
        && Length == length && WriteUtc == writeUtc;

    private static string? Norm(string? rel) => rel?.Replace('\\', '/');
}
