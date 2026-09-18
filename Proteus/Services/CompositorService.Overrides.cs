using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Proteus.Services;

public partial class CompositorService
{
    // ── Color override (design bindings) ───────────────────────────────────────

    /// <summary>
    /// Gear-layer settings pushed by a design binding. Applied at composite time onto a copy of the descriptor,
    /// so metadata.json is never mutated.
    /// </summary>
    private volatile IReadOnlyDictionary<string, OverlayGearOverride>? _gearOverride;

    public void SetActiveGearOverride(IReadOnlyDictionary<string, OverlayGearOverride>? overrideByMod)
        => _gearOverride = overrideByMod;

    public void SetActiveColorOverride(IReadOnlyDictionary<string, OverlayColorOverride>? overrideByMod)
        => _colorOverride = overrideByMod;

    /// <summary>
    /// Mod-wide overlay stack order pushed by a design binding: modDir → option keys
    /// (<see cref="Configuration.ModStackEntry"/>) top-first, used in place of <see cref="Configuration.ModStackIndexOf"/>.
    /// Null ⇒ fall back to the config order.
    /// </summary>
    private volatile IReadOnlyDictionary<string, List<string>>? _stackOverride;

    public void SetActiveStackOverride(IReadOnlyDictionary<string, List<string>>? overrideByMod)
        => _stackOverride = overrideByMod;

    // ── Color override (presets) ───────────────────────────────────────────────
    // A second channel of the same shape, pushed by PresetService, kept separate because its lifetime differs.
    // Precedence is per mod: a preset pinned on a mod wins for that mod (MergeByMod, applied at the snapshot).

    private volatile IReadOnlyDictionary<string, OverlayColorOverride>? _presetColorOverride;
    private volatile IReadOnlyDictionary<string, OverlayGearOverride>?  _presetGearOverride;
    private volatile IReadOnlyDictionary<string, List<string>>?         _presetStackOverride;

    public void SetPresetColorOverride(IReadOnlyDictionary<string, OverlayColorOverride>? overrideByMod)
        => _presetColorOverride = overrideByMod;

    public void SetPresetGearOverride(IReadOnlyDictionary<string, OverlayGearOverride>? overrideByMod)
        => _presetGearOverride = overrideByMod;

    public void SetPresetStackOverride(IReadOnlyDictionary<string, List<string>>? overrideByMod)
        => _presetStackOverride = overrideByMod;

    /// <summary>
    /// One dictionary with <paramref name="top"/>'s entries winning per mod. Returns the other side outright when
    /// either is empty, allocating nothing.
    /// </summary>
    internal static IReadOnlyDictionary<string, T>? MergeByMod<T>(
        IReadOnlyDictionary<string, T>? top, IReadOnlyDictionary<string, T>? bottom)
    {
        if (top == null || top.Count == 0) return bottom;
        if (bottom == null || bottom.Count == 0) return top;

        var merged = new Dictionary<string, T>(bottom, StringComparer.OrdinalIgnoreCase);
        foreach (var (mod, value) in top) merged[mod] = value;
        return merged;
    }

    /// <summary>
    /// The colour override the composite would actually use for this mod: a pinned preset's, else the design
    /// binding's, else null. Anything capturing the current look must read through here.
    /// </summary>
    public OverlayColorOverride? EffectiveColorOverrideFor(string modDir)
    {
        if (_presetColorOverride is { } p && p.TryGetValue(modDir, out var preset)) return preset;
        return _colorOverride is { } d && d.TryGetValue(modDir, out var design) ? design : null;
    }

    /// <inheritdoc cref="EffectiveColorOverrideFor"/>
    public OverlayGearOverride? EffectiveGearOverrideFor(string modDir)
    {
        if (_presetGearOverride is { } p && p.TryGetValue(modDir, out var preset)) return preset;
        return _gearOverride is { } d && d.TryGetValue(modDir, out var design) ? design : null;
    }

    /// <inheritdoc cref="EffectiveColorOverrideFor"/>
    public IReadOnlyList<string>? EffectiveStackOverrideFor(string modDir)
    {
        if (_presetStackOverride is { } p && p.TryGetValue(modDir, out var preset)) return preset;
        return _stackOverride is { } d && d.TryGetValue(modDir, out var design) ? design : null;
    }

    /// <summary>
    /// Apply the plugin's enabled state, both visually and in Penumbra. Off: clear the managed mod's redirects,
    /// redraw, then disable the mod (order matters). On: enable the mod, then recomposite.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        var collId = penumbra.GetPlayerCollectionId();

        // Either direction changes what is published out from under the gate.
        _lastCompositeFingerprint = null;

        if (enabled)
        {
            // Off the framework thread: EnsureManagedModExists does file I/O and may wait on _manifestLock.
            Task.Run(() =>
            {
                try
                {
                    EnsureManagedModExists();
                    if (collId.HasValue)
                        penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, true);
                    TriggerRecomposite("enabled");
                }
                catch (Exception ex) { log.Error(ex, "[Proteus] failed to enable cleanly"); }
            });
            return;
        }

        // Disabling stops all hosting, so pull our injected host items off the player's Glamourer state.
        RemoveInjectedGlasses();
        RemoveInjectedRing();

        // A hosted accessory's (or rewritten skin material's) redirect won't be undone by an in-place reload, so
        // force a full redraw.
        bool restoreAccessory = _secondSkinActive || _lastSkinMaterialRedirects.Count > 0;

        Task.Run(() =>
        {
            try
            {
                WriteManagedModJson(new Dictionary<string, string>());
                penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                // Nothing is hosted, so the redraw hook must not re-equip a carrier.
                RememberHostDecision(gearWanted: false, shellBuilt: false, onFacewear: false, carrierSlots: []);
                if (restoreAccessory) _needFullRedraw = true;
                // userRequested: the output is already withdrawn, so a suppressed redraw would leave the character
                // rendering textures nothing points at.
                ReloadAndRedraw(userRequested: true);   // character reverts to un-composited
                _secondSkinActive = false;
                _lastSkinMaterialRedirects = new(StringComparer.OrdinalIgnoreCase);
                ClearShellLocators();   // the shell is off the character; nothing left for them to describe

                if (collId.HasValue)
                    penumbra.SetModEnabled(collId.Value, SidecarDiscoveryService.ManagedModDir, false);

                log.Debug("[Proteus] disabled: output cleared, redrawn ({0}), Penumbra mod off",
                    restoreAccessory ? "full — accessory restored" : "in-place");
            }
            catch (Exception ex) { log.Error(ex, "[Proteus] failed to disable cleanly"); }
        });
    }
}
