using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace Proteus.Interop;

/// <summary>
/// Re-asserts each second-skin shell's colour table onto the LIVE material every time the game rebuilds it.
///
/// The game cooks a material's colour-table GPU texture at LOAD time, and for our shells that cook loses the
/// colorset's diffuse tint — so a dyed cloth shell (especially a white-base "dyeable" fabric) renders with no
/// colour even though its .mtrl DataSet holds the right values. Uploading the raw DataSet straight into the
/// colour-table slot (exactly what the highlighter's "restore baseline" does) bypasses that cook and shows
/// the real colours — verified in-game. This keeps that upload in place: each frame it finds the shell
/// materials and, only when the game has swapped in a texture that isn't ours (i.e. after a redraw/reload),
/// re-uploads the material's own baseline. Steady-state it does nothing (the slot already holds our texture),
/// so there's no per-frame GPU churn or table copy.
///
/// While a shell is being GLOW-highlighted, its slot is left to the highlighter — otherwise the two would
/// fight over the same texture every frame and the glow would never show. The applier resumes control once
/// the highlight clears (the highlighter's own baseline restore leaves a texture the applier then re-asserts).
/// </summary>
internal sealed unsafe class ShellColorsetApplier : IDisposable
{
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly ColorTableHighlighter highlighter;

    // Colour-table slot address -> the texture pointer we last uploaded there. When the live slot no longer
    // matches (the game rebuilt the table on a redraw), we re-assert; when it matches, we skip.
    private readonly Dictionary<nint, nint> _applied = new();

    public ShellColorsetApplier(IFramework framework, IObjectTable objects, ColorTableHighlighter highlighter)
    {
        this.framework   = framework;
        this.objects     = objects;
        this.highlighter = highlighter;
        framework.Update += OnFramework;
    }

    private static bool IsShellLeaf(string leaf)
        => leaf.StartsWith("ss_", StringComparison.OrdinalIgnoreCase)
        && leaf.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase);

    private void OnFramework(IFramework fw)
    {
        var addr = objects.LocalPlayer?.Address ?? 0;
        if (addr == 0) { _applied.Clear(); return; }

        var slots = ColorTableInterop.FindColorTableSlots(addr, IsShellLeaf);
        if (slots.Count == 0)
        {
            if (_applied.Count > 0) _applied.Clear();
            return;
        }

        var seen = new HashSet<nint>();
        foreach (var slot in slots)
        {
            seen.Add(slot.Slot);

            // The glow highlighter owns this slot while it's active — don't fight it.
            if (highlighter.IsHighlighting(slot.Leaf))
                continue;

            // Still our upload — nothing to do (and no 2 KB table copy).
            if (_applied.TryGetValue(slot.Slot, out var ours) && ours == slot.CurrentTex)
                continue;

            // First time we've seen this slot, or the game rebuilt the table (redraw): re-assert the raw
            // DataSet colorset so the diffuse tint survives.
            var table = ColorTableInterop.ReadTable(slot.DataSet);
            if (ColorTableInterop.Upload(slot.Slot, table))
                _applied[slot.Slot] = *(nint*)slot.Slot;   // remember the texture we just swapped in
        }

        // Drop slots that vanished (equipment removed, character redrawn to a new CharacterBase).
        List<nint>? stale = null;
        foreach (var k in _applied.Keys)
            if (!seen.Contains(k)) (stale ??= new()).Add(k);
        if (stale != null)
            foreach (var k in stale) _applied.Remove(k);
    }

    public void Dispose()
    {
        framework.Update -= OnFramework;
        _applied.Clear();
    }
}
