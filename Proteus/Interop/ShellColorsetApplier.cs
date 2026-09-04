using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Proteus.Services;

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
///
/// It is also where a LIGHT-SENSITIVE glow is dimmed, because the table it re-asserts is the same buffer
/// that carries the emissive. Scaling it here rather than baking it into the material is what keeps the
/// response free: the .mtrl on disk stays the authored, full-brightness one, a light change costs one 2 KB
/// upload and no recomposite, and switching the feature off restores the original bytes exactly.
/// </summary>
internal sealed unsafe class ShellColorsetApplier : IDisposable
{
    /// <summary>
    /// How finely the light level is quantised before it is allowed to change the table. At 1/48 a full
    /// fade crosses about fifty steps — smooth to the eye at any sane transition speed — while a light that
    /// is merely flickering or a probe result jittering in its last decimal changes nothing at all, which is
    /// what keeps a still scene at zero uploads.
    /// </summary>
    private const float LightQuantum = 1f / 48f;

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly ColorTableHighlighter highlighter;
    private readonly SceneLightService light;
    private readonly Configuration config;

    /// <summary>Shell material leaf → its light response. Set by the compositor's publish; null until one
    /// runs, which reads as "no light response anywhere" and costs nothing.</summary>
    public Func<string, ShellLightProfile?>? LightFor { get; set; }

    /// <summary>
    /// Colour-table slot address → the texture we last uploaded there and the quantised light level it was
    /// built for. When the live slot no longer matches (the game rebuilt the table on a redraw) or the light
    /// has moved to another bucket, we re-assert; when both match, we skip.
    /// <para/>
    /// ONE dictionary holding both, not two side by side. The two were cleared together in the stale-slot
    /// prune and separately everywhere else, so the teardown paths dropped the textures and kept the buckets
    /// — an entry per slot address leaked for the life of the session on every logout and zone change. A
    /// single map cannot fall out of step with itself.
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

            // What the scene light is doing to this material, if anything. A leaf with no profile — every
            // shell on a character with no light-sensitive glow — takes the original path untouched.
            var profile = config.LightResponseEnabled ? LightFor?.Invoke(slot.Leaf) : null;
            int bucket = profile == null ? 0
                       : (int)MathF.Round(Math.Clamp(light.Sample(profile.ProbeHeight), 0f, 1f) / LightQuantum);

            // Still our upload, and built for the light we're still in — nothing to do (and no 2 KB copy).
            if (_applied.TryGetValue(slot.Slot, out var ours)
                && ours.Tex == slot.CurrentTex && ours.Bucket == bucket)
                continue;

            // First time we've seen this slot, the game rebuilt the table (redraw), or the light moved far
            // enough to matter: re-assert the raw DataSet colorset so the diffuse tint survives, dimmed by
            // however much light is falling on it.
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
    /// Take each light-sensitive row's glow down by however much light is on it.
    /// <para/>
    /// Two halves move, not one. The EMISSIVE is the glow on character.shpk and the scroll map's brightness
    /// dial on characterscroll; taking only that down on a scrolling material leaves the effect armed and
    /// its pattern still drawn, just unlit — so the effect's own VISIBILITY (half 21, which
    /// <c>GearMaterialWriter</c> writes when it arms a glow row) comes down with it and the animation fades
    /// out with the light instead of freezing in place.
    /// <para/>
    /// Rows are scaled, never assigned, so at full darkness the table is byte-identical to the material's
    /// own and nothing has to be restored.
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
            // ONLY on a scrolling material. Half 21 is the effect's visibility there, but the sphere map's
            // intensity on character.shpk — see ShellLightProfile.IsScroll.
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
