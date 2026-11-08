using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Proteus.Services.Matcap;

namespace Proteus.Gui;

/// <summary>
/// The Matcaps tool's click preview, decided without ImGui or the game: whether a preview is wanted (a thumbnail was
/// clicked), which one is on the character (<see cref="ShownKey"/>) or being built, and what to do next when the ticks,
/// the choice or the model change. A build's result is taken only while it is still the latest for what is ticked now.
/// </summary>
internal sealed class MatcapPreviewState
{
    public enum Step
    {
        /// <summary>Nothing to do this frame.</summary>
        None,
        /// <summary>No preview can be shown; <see cref="Note"/> says why.</summary>
        Note,
        /// <summary>Build a preview for the current key (<see cref="Generation"/> marks it).</summary>
        Build,
        /// <summary>Take the preview on the character down (it can't be shown for what is ticked now).</summary>
        End,
    }

    /// <summary>A thumbnail was clicked, and the preview follows the ticks until it is stopped.</summary>
    public bool Wanted { get; private set; }
    public MatcapChoice? Choice { get; private set; }
    /// <summary>What is on the character, or null.</summary>
    public string? ShownKey { get; private set; }
    /// <summary>What is being built, or null.</summary>
    public string? BuildingKey { get; private set; }
    /// <summary>Bumped by every build and every stop: a build of an older generation is never shown.</summary>
    public int Generation { get; private set; }
    /// <summary>Why no preview is shown, or null.</summary>
    public string? Note { get; private set; }
    /// <summary>The "your character draws another file" line, worked out before the first push; kept while shown.</summary>
    public string? DrawnNote { get; private set; }
    private string? failedKey;

    /// <summary>Requested, building or shown: brush pushes stand down.</summary>
    public bool Active => Wanted || BuildingKey != null || ShownKey != null;

    /// <summary>
    /// The ticked parts' tint is off on the character: a preview is being built, waits to be pushed
    /// (<paramref name="pushPending"/>) or is shown. A note, a failed build or an ended preview brings it back.
    /// </summary>
    public bool HidesTicks(bool pushPending) => ShownKey != null || BuildingKey != null || pushPending;

    public void Click(MatcapChoice choice)
    {
        Wanted = true;
        Choice = choice;
        Note = null;
        failedKey = null;
    }

    /// <param name="key">What is ticked and chosen now (<see cref="Key"/>).</param>
    /// <param name="blocked">Why no preview can be shown for it, or null.</param>
    /// <param name="busy">A matcap job, a build or a push is under way: wait.</param>
    public Step Next(string key, string? blocked, bool busy)
    {
        if (!Wanted) return Step.None;
        if (blocked != null)
        {
            Note = blocked;
            return ShownKey != null && !busy ? Step.End : Step.Note;
        }
        if (busy || BuildingKey != null) return Step.None;
        if (key == ShownKey) return Step.None;
        if (key == failedKey) return Step.Note;
        Note = null;
        BuildingKey = key;
        Generation++;
        return Step.Build;
    }

    /// <summary>A build finished: whether to show it. Only the latest build, for what is still ticked.</summary>
    public bool Accept(int gen, string builtKey, string currentKey)
    {
        if (gen != Generation) return false;
        BuildingKey = null;
        return Wanted && builtKey == currentKey;
    }

    public void Shown(string key, string? drawnNote)
    {
        if (ShownKey == null) DrawnNote = drawnNote;
        ShownKey = key;
        Note = null;
        failedKey = null;
    }

    public void Failed(string key, string note)
    {
        failedKey = key;
        Note = note;
    }

    /// <summary>The preview was taken off the character.</summary>
    /// <param name="keepWanted">Still follow the ticks (they no longer allow a preview), rather than stop.</param>
    public void Ended(bool keepWanted = false)
    {
        ShownKey = null;
        BuildingKey = null;
        DrawnNote = null;
        Generation++;
        if (keepWanted) return;
        Wanted = false;
        Choice = null;
        Note = null;
        failedKey = null;
    }

    /// <summary>What a preview depends on: the model file, the choice and the ticked parts.</summary>
    public static string Key(string modelFile, MatcapChoice choice, IEnumerable<ModelPart> ticked)
    {
        var c = choice.Image is { } img ? "img:" + img.Path : $"lib:{choice.Library?.Mod}|{choice.Library?.Group}|{choice.Option}";
        return modelFile + "\n" + c + "\n" + string.Join(",", ticked.Select(p => p.Label).Order(StringComparer.Ordinal));
    }

    /// <summary>A preview that must redraw (no in-place reload) goes out at most once per <paramref name="interval"/>.</summary>
    public static bool PushDue(long now, long lastPush, bool redraw, long interval) => !redraw || now - lastPush >= interval;
}
