using System;

namespace Proteus.Services;

/// <summary>
/// The Studio's bookkeeping for sharing the live brush preview: which save a push tried to share (so a share that
/// ended up private is retried once what it waited on changes, and never otherwise), and whether locked parts are
/// shown again after the brush was left idle (see <see cref="LivePreviewPolicy"/>). Kept out of the panel so the
/// sequences can be tested; times are tick counts in milliseconds.
/// </summary>
internal sealed class LivePreviewShareState
{
    /// <summary>How often an owed share looks at the disk again.</summary>
    public const long OwedCheckMs = 1000;

    /// <summary>How often an idle brush with parts hidden looks at the saved file to decide on a reveal.</summary>
    public const long RevealCheckMs = 250;

    internal enum RevealStep { None, Reveal, Hide }

    private string? wantedStamp;
    private string? parkedOn;
    private long owedCheckedAt = long.MinValue / 2;

    private long lastActivityAt;
    private long revealCheckedAt = long.MinValue / 2;
    private int lastLockedHides;

    /// <summary>Locked parts are shown again — <see cref="Tick"/> said reveal, and nothing has hidden them since.</summary>
    public bool Revealed { get; private set; }

    /// <summary>A push tried to share <paramref name="stamp"/>, depending on <paramref name="token"/>.</summary>
    public void SharePushed(string stamp, string token)
    {
        wantedStamp = stamp;
        parkedOn = token;
    }

    /// <summary>
    /// The character no longer shows a share attempt — a private push, a refit preview, a redraw, the preview taken
    /// down — so nothing is owed. Every path that replaces a share must come through here.
    /// </summary>
    public void NotSharing()
    {
        wantedStamp = null;
        parkedOn = null;
    }

    /// <summary>
    /// Whether an owed share should be pushed again: it ended up private (<paramref name="sharedStamp"/> is not the one
    /// wanted) and what it waited on has changed. <paramref name="token"/> is asked for at most every
    /// <see cref="OwedCheckMs"/>.
    /// </summary>
    public bool OwedShareDue(long now, string? sharedStamp, Func<string> token)
    {
        if (wantedStamp == null || sharedStamp == wantedStamp) return false;
        if (now - owedCheckedAt < OwedCheckMs) return false;
        owedCheckedAt = now;
        return LivePreviewPolicy.OwedShareDue(owed: true, parkedOn, token());
    }

    /// <summary>
    /// One frame of the lock reveal. Editing, hovering the garment, a save waiting, and a change of what the locks
    /// hide (a lock ticked, another tool) all count as activity. Reveals after
    /// <see cref="LivePreviewPolicy.LockRevealIdleMs"/> of none, while parts are hidden on the character and the saved
    /// file still matches; hides again on the garment or an edit.
    /// </summary>
    /// <param name="lockedHides">What the tool and locks would leave off the character, revealed or not (0: nothing).</param>
    /// <param name="hidingOnCharacter">The preview on the character has parts left off right now.</param>
    /// <param name="savedMatches">Looked at only when everything else says reveal, at most every <see cref="RevealCheckMs"/>.</param>
    public RevealStep Tick(long now, bool editing, bool hovering, bool savePending, int lockedHides,
                           bool hidingOnCharacter, Func<bool> savedMatches)
    {
        if (editing || hovering || savePending || lockedHides != lastLockedHides) lastActivityAt = now;
        lastLockedHides = lockedHides;

        if (Revealed)
        {
            if (!LivePreviewPolicy.ShouldHideAgain(editing, hovering) && lockedHides != 0) return RevealStep.None;
            Revealed = false;
            return RevealStep.Hide;
        }

        long idle = now - lastActivityAt;
        if (idle < LivePreviewPolicy.LockRevealIdleMs || !hidingOnCharacter || lockedHides == 0) return RevealStep.None;
        if (now - revealCheckedAt < RevealCheckMs) return RevealStep.None;
        revealCheckedAt = now;

        if (!LivePreviewPolicy.ShouldRevealLocked(true, editing, hovering, savePending, savedMatches(), idle))
            return RevealStep.None;
        Revealed = true;
        return RevealStep.Reveal;
    }

    /// <summary>The preview was taken down or the model changed: nothing revealed, nothing owed.</summary>
    public void Reset()
    {
        Revealed = false;
        NotSharing();
    }
}
