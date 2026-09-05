using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// Decides where a colour-editor edit lands, per mod. There are now three possible homes and the rule
/// had been written out three times over in the editor, once per tab layout; this is that rule, once.
/// <para/>
/// <list type="number">
///   <item>A preset is pinned on the mod → the preset's live override. The wearer is trying looks on;
///         edits belong to the look, and drift from the saved preset is what raises the `●` marker.</item>
///   <item>Else a design binding is active for the mod → the binding's live override, folded in only
///         when the wearer presses "Update binding".</item>
///   <item>Else nothing overrides it → the caller writes <c>metadata.json</c>, which is what
///         <see cref="HasOverride"/> returning false means.</item>
/// </list>
/// <para/>
/// Getting this wrong is invisible rather than loud: an edit written to <c>metadata.json</c> while an
/// override sits on top of it changes a file nobody is reading, so the editor shows the new value, the
/// character keeps the old one, and nothing errors. That is why every editor path goes through here
/// rather than choosing for itself.
/// </summary>
public class OverlayEditRouter
{
    private readonly PresetService presets;
    private readonly DesignBindingService bindings;

    public OverlayEditRouter(PresetService presets, DesignBindingService bindings)
    {
        this.presets  = presets;
        this.bindings = bindings;
    }

    /// <summary>The bag that owns this mod's look, or null when the mod's own metadata does.</summary>
    private OverlayOverrideBag? BagFor(string modDir)
    {
        if (presets.Overrides.Governs(modDir)) return presets.Overrides;
        return null;
    }

    /// <summary>True when something other than the mod's own metadata supplies its colours — so the
    /// caller previews instead of persisting, and the editor can say whose look is on screen.</summary>
    public bool HasOverride(string modDir)
        => presets.Overrides.Governs(modDir) || bindings.IsOverrideActiveFor(modDir);

    /// <summary>True when the mod's look comes from a pinned preset rather than a design binding.
    /// Only for wording and badges — every edit path routes on its own.</summary>
    public bool IsPresetDriven(string modDir) => presets.Overrides.Governs(modDir);

    // ── Colour rows ─────────────────────────────────────────────────────────────

    public List<ColorTableRowPreset>? PeekOverrideRows(string modDir, string? group, string? option)
        => BagFor(modDir) is { } bag
            ? bag.PeekRows(modDir, group, option)
            : bindings.PeekOverrideRows(modDir, group, option);

    public bool SetOverrideRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows)
        => BagFor(modDir) is { } bag
            ? bag.SetRows(modDir, group, option, rows)
            : bindings.SetOverrideRows(modDir, group, option, rows);

    public List<ColorTableRowPreset>? PeekMaskRows(string modDir)
        => BagFor(modDir) is { } bag ? bag.PeekMaskRows(modDir) : bindings.PeekMaskRows(modDir);

    public bool SetMaskRows(string modDir, List<ColorTableRowPreset> rows)
        => BagFor(modDir) is { } bag ? bag.SetMaskRows(modDir, rows) : bindings.SetMaskRows(modDir, rows);

    // ── Gear / layer settings ───────────────────────────────────────────────────

    public GearSettingsPreset? GetEditableGearOverride(
        string modDir, string? group, string? option, OverlayDescriptor seed)
        => BagFor(modDir) is { } bag
            ? bag.GetEditableGear(modDir, group, option, seed)
            : bindings.GetEditableGearOverride(modDir, group, option, seed);

    public GearSettingsPreset? GetEditableContentGearOverride(
        string modDir, string? group, string? option, GearSettingsPreset seed)
        => BagFor(modDir) is { } bag
            ? bag.GetEditableContentGear(modDir, group, option, seed)
            : bindings.GetEditableContentGearOverride(modDir, group, option, seed);

    public GearSettingsPreset? GetEditableMaskGearOverride(string modDir, OverlayDescriptor seed)
        => BagFor(modDir) is { } bag
            ? bag.GetEditableMaskGear(modDir, seed)
            : bindings.GetEditableMaskGearOverride(modDir, seed);

    public GearSettingsPreset? PeekGearOverride(string modDir, string group, string option)
        => BagFor(modDir) is { } bag
            ? bag.PeekGear(modDir, group, option)
            : bindings.PeekGearOverride(modDir, group, option);

    public GearSettingsPreset? PeekContentGearOverride(string modDir, string? group, string? option)
        => BagFor(modDir) is { } bag
            ? bag.PeekContentGear(modDir, group, option)
            : bindings.PeekContentGearOverride(modDir, group, option);

    // ── Stack order ─────────────────────────────────────────────────────────────

    public IReadOnlyList<string>? ActiveStackOrderFor(string modDir)
        => BagFor(modDir) is { } bag ? bag.StackOrderFor(modDir) : bindings.ActiveStackOrderFor(modDir);

    public bool SetEditableStackOrder(string modDir, IEnumerable<(string Group, string Option)> topFirst)
        => BagFor(modDir) is { } bag
            ? bag.SetStackOrder(modDir, topFirst)
            : bindings.SetEditableStackOrder(modDir, topFirst);

    // ── Clearing one option ─────────────────────────────────────────────────────

    /// <summary>
    /// "Reset to defaults" for one option. Against a pinned preset this drops the option from the live
    /// override only — the saved preset keeps it, and the `●` marker appears, because a reset is an edit
    /// like any other and the wearer decides whether it sticks. A design binding also forgets it in the
    /// stored binding, since re-applying that design would otherwise bring it straight back.
    /// </summary>
    public bool ClearOptionOverride(string modDir, string? group, string? option)
        => BagFor(modDir) is { } bag
            ? bag.ClearOption(modDir, group, option)
            : bindings.ClearOptionOverride(modDir, group, option);
}
