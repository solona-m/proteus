using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace Proteus.Interop;

/// <summary>
/// Makes a colorset row's mesh "glow" on the local player by hue-cycling its live colour-table texture,
/// like Penumbra's Advanced Material Editing target button. A target is one game colour-table row (0-31)
/// across a SET of shell materials (a mod/option can carry several gear overlays that all bake the same
/// row) — identified by their <c>ss_{letter}.mtrl</c> file names, which is what the live resource handle
/// reports after Penumbra redirects the accessory path. Each framework tick walks the character's
/// materials once: it rebuilds each targeted material's table from its own baseline (DataSet) so only the
/// row changes, and restores any material it previously highlighted that is no longer targeted.
/// </summary>
public sealed class ColorTableHighlighter : IDisposable
{
    private readonly IFramework framework;
    private readonly IObjectTable objects;

    private readonly HashSet<string> _targetLeaves = new(StringComparer.OrdinalIgnoreCase);
    private int _targetRow;
    private HashSet<string> _appliedLeaves = new(StringComparer.OrdinalIgnoreCase);

    public ColorTableHighlighter(IFramework framework, IObjectTable objects)
    {
        this.framework = framework;
        this.objects   = objects;
        framework.Update += OnFramework;
    }

    /// <summary>Glow game row <paramref name="gameRow"/> (0-31) of every shell material in <paramref name="materialLeaves"/>.</summary>
    public void SetTarget(IReadOnlyList<string> materialLeaves, int gameRow)
    {
        _targetLeaves.Clear();
        foreach (var leaf in materialLeaves) _targetLeaves.Add(leaf);
        _targetRow = gameRow;
    }

    public void Clear() => _targetLeaves.Clear();

    public bool IsTarget(IReadOnlyList<string> materialLeaves, int gameRow)
        => _targetRow == gameRow && _targetLeaves.Count == materialLeaves.Count
        && _targetLeaves.SetEquals(materialLeaves);

    private void OnFramework(IFramework fw) => Apply(objects.LocalPlayer?.Address ?? 0);

    private void Apply(nint addr)
    {
        if (addr == 0) { _appliedLeaves.Clear(); return; }   // character gone; its textures went with it
        if (_targetLeaves.Count == 0 && _appliedLeaves.Count == 0) return;

        var mats = ColorTableInterop.FindColorTableMaterials(addr,
            leaf => _targetLeaves.Contains(leaf) || _appliedLeaves.Contains(leaf));

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (r, g, b) = HueNow();
        foreach (var m in mats)
        {
            if (_targetLeaves.Contains(m.Leaf))
            {
                var table = (byte[])m.Baseline.Clone();
                ColorTableInterop.WriteHalfColor(table, _targetRow, ColorTableInterop.OffDiffuse, r, g, b);
                ColorTableInterop.WriteHalfColor(table, _targetRow, ColorTableInterop.OffEmissive, r / 8f, g / 8f, b / 8f);
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
        _targetLeaves.Clear();
        Apply(objects.LocalPlayer?.Address ?? 0);   // restore anything still highlighted
        framework.Update -= OnFramework;
    }
}
