using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Interop;

/// <summary>
/// Makes a colorset row's mesh "glow" on the local player by hue-cycling its live colour-table texture,
/// like Penumbra's Advanced Material Editing target button. A target is a set of game colour-table rows
/// (0-31, as a <see cref="ColorRowMask"/>) across a SET of shell materials (a mod/option can carry several
/// gear overlays that all bake the same rows) — identified by their <c>ss_{letter}.mtrl</c> file names, which
/// is what the live resource handle reports after Penumbra redirects the accessory path. Each framework tick
/// walks the character's materials once: it rebuilds each targeted material's table from its own baseline
/// (DataSet) so only the rows change, and restores any material it previously highlighted that is no longer
/// targeted.
///
/// Two targets: the PINNED one (the sub-row Glow button, click to toggle) and a transient PREVIEW (pointing at a
/// row button), shown over the pin while the UI keeps renewing it. Only the pin ghosts the layers above; a preview
/// never touches the ghost. The UI and the framework tick share the main thread; the references are volatile only
/// because a recomposite can <see cref="Clear"/> from another thread.
/// </summary>
public sealed class ColorTableHighlighter : IDisposable
{
    private readonly IFramework framework;
    private readonly IObjectTable objects;

    // Immutable once built: a target is replaced, never edited, so a reader always sees a whole one.
    private sealed record GearTarget(HashSet<string> Leaves, uint RowMask);

    private volatile GearTarget? _pinned;
    private volatile GearTarget? _preview;
    private readonly PreviewKeepAlive _keep = new();
    private readonly PreviewKeepAlive _pinKeep = new();   // #82: the pin lives while an open Colors window renews it
    private HashSet<string> _appliedLeaves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ghosts the shell layers stacked above the highlighted one so an occluded glow shows through.
    /// Set by Plugin; null-safe if absent.</summary>
    public ShellNormalGhost? Ghost { get; set; }

    public ColorTableHighlighter(IFramework framework, IObjectTable objects)
    {
        this.framework = framework;
        this.objects   = objects;
        framework.Update += OnFramework;
    }

    /// <summary>Glow game row <paramref name="gameRow"/> (0-31) of every shell material in <paramref name="materialLeaves"/>.</summary>
    public void SetTarget(IReadOnlyList<string> materialLeaves, int gameRow)
    {
        _pinned = new GearTarget(ToSet(materialLeaves), ColorRowMask.Row(gameRow));
        _pinKeep.Renew(RowHighlightClock.NowMs());
        // Ghost the shell layers stacked above this one so the glow shows even if an upper layer is opaque.
        Ghost?.GhostAbove(MaxLetter(materialLeaves));
    }

    /// <summary>Keeps the pin lit for another moment: called every frame an open Colors window draws (#82).</summary>
    public void RenewPin(long nowMs) => _pinKeep.Renew(nowMs);

    /// <summary>Drops the pin and any preview.</summary>
    public void Clear()
    {
        _pinned  = null;
        _preview = null;
        Ghost?.Clear();
    }

    /// <summary>Show <paramref name="rowMask"/> of these materials over the pin for as long as this keeps being
    /// called (see <see cref="PreviewKeepAlive"/>). Never ghosts.</summary>
    public void SetPreview(IReadOnlyList<string> materialLeaves, uint rowMask)
    {
        var cur = _preview;
        if (cur == null || cur.RowMask != rowMask || cur.Leaves.Count != materialLeaves.Count
            || !cur.Leaves.SetEquals(materialLeaves))
            _preview = new GearTarget(ToSet(materialLeaves), rowMask);
        _keep.Renew(RowHighlightClock.NowMs());
    }

    public void EndPreview() => _preview = null;

    private static HashSet<string> ToSet(IReadOnlyList<string> leaves)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in leaves) set.Add(leaf);
        return set;
    }

    // Highest ss_{letter} among the target's shell materials — the layers ABOVE it are what must ghost.
    private static char? MaxLetter(IReadOnlyList<string> leaves)
    {
        char? max = null;
        foreach (var leaf in leaves)
            if (leaf.Length >= 5 && leaf.StartsWith("ss_", StringComparison.OrdinalIgnoreCase))
            {
                char c = char.ToLowerInvariant(leaf[3]);
                // base-36 disk id (0-9a-z); ASCII-monotonic so the '> max' ordering still tracks the stack.
                if (((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z')) && (max == null || c > max)) max = c;
            }
        return max;
    }

    /// <summary>Whether the PINNED target is this one — the Glow button's state, which a hover never changes.</summary>
    public bool IsTarget(IReadOnlyList<string> materialLeaves, int gameRow)
    {
        var p = _pinned;
        return p != null && p.RowMask == ColorRowMask.Row(gameRow) && p.Leaves.Count == materialLeaves.Count
            && p.Leaves.SetEquals(materialLeaves);
    }

    /// <summary>True while this shell material leaf was glow-painted this tick, so another live colour-table writer
    /// (ShellColorsetApplier, which ticks after this) can leave its slot alone and not fight the glow.</summary>
    public bool IsHighlighting(string leaf) => _appliedLeaves.Contains(leaf);

    private void OnFramework(IFramework fw)
    {
        _keep.Tick();
        _pinKeep.Tick();
        var now = RowHighlightClock.NowMs();
        if (_preview != null && _keep.Expired(now)) _preview = null;
        // The UI stopped drawing the Colors window (hidden, cutscene): the pin goes too, restored on this same tick.
        if (RowHighlight.PinExpired(_pinned != null, _pinKeep, now)) Clear();
        Apply(objects.LocalPlayer?.Address ?? 0);
    }

    private void Apply(nint addr)
    {
        if (addr == 0) { _appliedLeaves.Clear(); return; }   // character gone; its textures went with it
        var eff = _preview ?? _pinned;                        // read once: Clear may null them from another thread
        if (eff == null && _appliedLeaves.Count == 0) return;

        var targetLeaves = eff?.Leaves;
        var mats = ColorTableInterop.FindColorTableMaterials(addr,
            leaf => (targetLeaves != null && targetLeaves.Contains(leaf)) || _appliedLeaves.Contains(leaf));

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (r, g, b) = HueNow();
        foreach (var m in mats)
        {
            if (targetLeaves != null && targetLeaves.Contains(m.Leaf))
            {
                var table = (byte[])m.Baseline.Clone();
                PaintRows(table, eff!.RowMask, r, g, b);
                ColorTableInterop.Upload(m.Slot, table);
                applied.Add(m.Leaf);
            }
            else
            {
                ColorTableInterop.Upload(m.Slot, m.Baseline);   // was highlighted, no longer target — restore
            }
        }
        _appliedLeaves = applied;
    }

    /// <summary>Paint every game row in <paramref name="rowMask"/>: diffuse to the hue, emissive to an eighth of it.</summary>
    internal static void PaintRows(byte[] table, uint rowMask, float r, float g, float b)
    {
        for (int row = 0; row < ColorTableInterop.RowCount; row++)
        {
            if (!ColorRowMask.Contains(rowMask, row)) continue;
            ColorTableInterop.WriteHalfColor(table, row, ColorTableInterop.OffDiffuse, r, g, b);
            ColorTableInterop.WriteHalfColor(table, row, ColorTableInterop.OffEmissive, r / 8f, g / 8f, b / 8f);
        }
    }

    // Hue cycle, matching Glamourer's LiveColorTablePreviewer.CalculateDiffuse timing. Kept as a pure
    // helper (not ImGui.ColorConvertHSVtoRGB): this runs on the framework thread, off the ImGui frame.
    private static (float R, float G, float B) HueNow()
    {
        const long frameLength = TimeSpan.TicksPerMillisecond * 5;
        const long steps       = 2000;
        var hueByte = DateTimeOffset.UtcNow.UtcTicks % (steps * frameLength) / frameLength;
        return HsvToRgb((float)hueByte / steps, 1f, 1f);
    }

    private static (float R, float G, float B) HsvToRgb(float h, float s, float v)
    {
        float i = MathF.Floor(h * 6f), f = h * 6f - i;
        float p = v * (1f - s), q = v * (1f - f * s), t = v * (1f - (1f - f) * s);
        return ((int)i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    public void Dispose()
    {
        Clear();
        Apply(objects.LocalPlayer?.Address ?? 0);   // restore anything still highlighted
        framework.Update -= OnFramework;
    }
}
