using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Proteus.Gui;

/// <summary>
/// Frame-rate independent easing for hand-drawn widgets. State is keyed by ImGui id, lives only in memory, and is
/// pruned once an id has not been touched for a while. With <see cref="ReduceMotion"/> on, every value lands on its
/// target at once and nothing loops.
/// </summary>
internal static class UiAnim
{
    /// <summary>Mirrors <c>Configuration.ReduceMotion</c>; the status window copies it in every frame.</summary>
    public static bool ReduceMotion { get; set; }

    /// <summary>Seconds since ImGui started. Only meaningful for loops and phases, never as a duration.</summary>
    public static float Time => (float)ImGui.GetTime();

    private sealed class Slot
    {
        public float Value;
        public int Frame;
        /// <summary>When the current hover began, for <see cref="Sheen"/>; negative when not hovered.</summary>
        public float HoverStart = -1f;
    }

    private static readonly Dictionary<uint, Slot> Slots = new();
    private static int lastPrune;

    /// <summary>Frames an id may go untouched before its state is dropped.</summary>
    private const int PruneAfterFrames = 600;

    private static Slot Get(uint id, float initial)
    {
        var frame = ImGui.GetFrameCount();
        if (!Slots.TryGetValue(id, out var s))
            Slots[id] = s = new Slot { Value = initial };
        s.Frame = frame;

        if (frame - lastPrune > PruneAfterFrames)
        {
            lastPrune = frame;
            List<uint>? stale = null;
            foreach (var (k, v) in Slots)
                if (frame - v.Frame > PruneAfterFrames)
                    (stale ??= new()).Add(k);
            if (stale != null)
                foreach (var k in stale)
                    Slots.Remove(k);
        }
        return s;
    }

    /// <summary>Ease toward 1 while <paramref name="on"/>, toward 0 otherwise. Starts at 0.</summary>
    public static float Ease(uint id, bool on, float speed = 12f) => Ease(id, on ? 1f : 0f, speed, initial: 0f);

    /// <summary>
    /// Ease toward <paramref name="target"/>: <c>v += (t - v) · dt · speed</c>, which reaches ~95% in 3/speed seconds
    /// at any frame rate.
    /// </summary>
    public static float Ease(uint id, float target, float speed, float initial)
    {
        var s = Get(id, initial);
        if (ReduceMotion)
        {
            s.Value = target;
            return target;
        }

        // Clamped: a hitch (a loading screen, a breakpoint) must not overshoot.
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        s.Value += (target - s.Value) * MathF.Min(1f, dt * speed);
        if (MathF.Abs(target - s.Value) < 0.002f)
            s.Value = target;
        return s.Value;
    }

    /// <summary>
    /// A one-shot sweep that starts when <paramref name="hovered"/> turns on: 0→1 over <paramref name="duration"/>
    /// seconds, then stays finished until the hover ends. Negative when there is nothing to draw.
    /// </summary>
    public static float Sheen(uint id, bool hovered, float duration = 0.65f)
    {
        // Its own key space: a caller may already ease the same item id.
        var s = Get(id ^ 0x5EE5_5EE5u, 0f);
        if (!hovered || ReduceMotion)
        {
            s.HoverStart = -1f;
            return -1f;
        }
        if (s.HoverStart < 0f)
            s.HoverStart = Time;

        var t = (Time - s.HoverStart) / duration;
        return t >= 1f ? -1f : EaseOutCubic(t);
    }

    /// <summary>A slow 0..1 wave with the given period; a steady 1 under <see cref="ReduceMotion"/>.</summary>
    public static float Pulse(float periodSeconds, float phase = 0f)
        => ReduceMotion ? 1f : 0.5f + (0.5f * MathF.Sin(((Time / periodSeconds) + phase) * MathF.Tau));

    /// <summary>A -1..1 drift for ambient motion; 0 (frozen at the rest position) under <see cref="ReduceMotion"/>.</summary>
    public static float Drift(float periodSeconds, float phase = 0f)
        => ReduceMotion ? 0f : MathF.Sin(((Time / periodSeconds) + phase) * MathF.Tau);

    public static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var u = 1f - t;
        return 1f - (u * u * u);
    }
}
