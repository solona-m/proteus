using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Interop;

/// <summary>
/// Re-asserts each second-skin shell's colour table onto the live material every time the game rebuilds it:
/// the game's load-time cook loses the colorset's diffuse tint, and uploading the raw DataSet bypasses it.
/// Uploads only when the slot no longer holds our texture; a glow-highlighted slot is left to the highlighter.
/// Also dims light-sensitive glow here, so the .mtrl on disk stays at full brightness.
/// </summary>
internal sealed unsafe class ShellColorsetApplier : IDisposable
{
    /// <summary>
    /// How finely the light level is quantised before it may change the table, so jitter causes no uploads.
    /// </summary>
    private const float LightQuantum = 1f / 48f;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly ColorTableHighlighter highlighter;
    private readonly SceneLightService light;
    private readonly Configuration config;

    /// <summary>Shell material leaf → its light response. Set by the compositor's publish; null means none.</summary>
    public Func<string, ShellLightProfile?>? LightFor { get; set; }

    /// <summary>
    /// Colour-table slot address → the texture we last uploaded there and the quantised light level it was
    /// built for. When the live slot no longer matches (the game rebuilt the table on a redraw) or the light
    /// has moved to another bucket, we re-assert; when both match, we skip. One map, so the two cannot drift.
    /// </summary>
    private readonly Dictionary<nint, (nint Tex, int Bucket)> _applied = new();

    public ShellColorsetApplier(IFramework framework, IObjectTable objects, ColorTableHighlighter highlighter,
                                SceneLightService light, Configuration config)
    {
        this.framework   = framework;
        this.objects     = objects;
        this.highlighter = highlighter;
        this.light       = light;
        this.config      = config;
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

            // A leaf with no light profile takes the plain path untouched.
            var profile = config.LightResponseEnabled ? LightFor?.Invoke(slot.Leaf) : null;
            int bucket = profile == null ? 0
                       : (int)MathF.Round(Math.Clamp(light.Sample(profile.ProbeHeight), 0f, 1f) / LightQuantum);

            // Still our upload, built for the current light bucket.
            if (_applied.TryGetValue(slot.Slot, out var ours)
                && ours.Tex == slot.CurrentTex && ours.Bucket == bucket)
                continue;

            // New slot, rebuilt table, or light moved: re-assert the raw DataSet colorset, dimmed for the light.
            var table = ColorTableInterop.ReadTable(slot.DataSet);
            if (profile != null)
                ApplyLightResponse(table, profile, bucket * LightQuantum);
            if (ColorTableInterop.Upload(slot.Slot, table))
                _applied[slot.Slot] = (*(nint*)slot.Slot, bucket);   // the texture we just swapped in
        }

        // Drop slots that vanished (equipment removed, character redrawn to a new CharacterBase).
        List<nint>? stale = null;
        foreach (var k in _applied.Keys)
            if (!seen.Contains(k)) (stale ??= new()).Add(k);
        if (stale != null)
            foreach (var k in stale) _applied.Remove(k);
    }

    /// <summary>
    /// Take each light-sensitive row's glow down by however much light is on it: the emissive, and on a
    /// scrolling material also the effect visibility (half 21). Rows are scaled, never assigned, so at full
    /// darkness the table is byte-identical to the material's own.
    /// </summary>
    internal static void ApplyLightResponse(byte[] table, ShellLightProfile profile, float level)
    {
        for (int row = 0; row < ShellLightProfile.RowCount; row++)
        {
            float response = profile.RowResponse[row];
            if (response <= 0f) continue;

            float factor = Math.Clamp(1f - response * level, 0f, 1f);
            if (factor >= 1f) continue;

            ColorTableInterop.ScaleHalves(table, row, ColorTableInterop.OffEmissive, 3, factor);
            // Only on a scrolling material: half 21 is the sphere map's intensity on character.shpk.
            if (profile.IsScroll)
                ColorTableInterop.ScaleHalves(table, row, ColorTableInterop.OffEffectVisibility, 1, factor);
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFramework;
        _applied.Clear();
    }
}
