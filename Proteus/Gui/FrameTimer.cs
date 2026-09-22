using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Proteus.Gui;

/// <summary>
/// Where one UI frame's time went, logged only when the frame was slow enough to be felt. A draw that stalls the game
/// shows up in the log as a breakdown by step, so the cause is read rather than guessed.
/// </summary>
internal sealed class FrameTimer
{
    private readonly Stopwatch clock = new();
    private readonly List<(string Step, double Ms)> steps = [];
    private double last;

    public void Begin()
    {
        steps.Clear();
        last = 0;
        clock.Restart();
    }

    /// <summary>The time since the previous mark belongs to <paramref name="step"/>.</summary>
    public void Mark(string step)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        steps.Add((step, now - last));
        last = now;
    }

    /// <summary>Log the frame when it took longer than <paramref name="slowMs"/>, naming every step that took time.</summary>
    public void End(IPluginLog log, string what, double slowMs = 250)
    {
        double total = clock.Elapsed.TotalMilliseconds;
        clock.Stop();
        if (total < slowMs) return;
        Mark("(rest)");
        var named = steps.Where(s => s.Ms >= 5).Select(s => $"{s.Step} {s.Ms:F0}");
        log.Warning($"[Proteus] slow {what} frame: {total:F0} ms — {string.Join(" | ", named)}");
    }
}
