using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// Decides where a colour-editor edit lands, per mod; every editor path goes through here.
/// <list type="number">
///   <item>A preset is pinned on the mod → the preset's live override.</item>
///   <item>Else a design binding is active for the mod → the binding's live override.</item>
///   <item>Else → the caller writes <c>metadata.json</c> (<see cref="HasOverride"/> is false).</item>
/// </list>
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

    /// <summary>True when something other than the mod's own metadata supplies its colours, so the caller
    /// previews instead of persisting.</summary>
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
    {
        // Once per session: an edit fires per slider step.
        UsageStats.CountOncePerSession(UsageFeature.ColorsetEdit);
        return BagFor(modDir) is { } bag
            ? bag.SetRows(modDir, group, option, rows)
            : bindings.SetOverrideRows(modDir, group, option, rows);
    }

    public List<ColorTableRowPreset>? PeekContentMaterialRows(string modDir, string materialRel)
        => BagFor(modDir) is { } bag
            ? bag.PeekContentMaterialRows(modDir, materialRel)
            : bindings.PeekContentMaterialRows(modDir, materialRel);

    public bool SetContentMaterialRows(string modDir, string materialRel, List<ColorTableRowPreset> rows)
    {
        UsageStats.CountOncePerSession(UsageFeature.ColorsetEdit);
        return BagFor(modDir) is { } bag
            ? bag.SetContentMaterialRows(modDir, materialRel, rows)
            : bindings.SetContentMaterialRows(modDir, materialRel, rows);
    }

    public List<ColorTableRowPreset>? PeekMaskRows(string modDir)
        => BagFor(modDir) is { } bag ? bag.PeekMaskRows(modDir) : bindings.PeekMaskRows(modDir);

    public bool SetMaskRows(string modDir, List<ColorTableRowPreset> rows)
    {
        UsageStats.CountOncePerSession(UsageFeature.MasksEdit);
        return BagFor(modDir) is { } bag ? bag.SetMaskRows(modDir, rows) : bindings.SetMaskRows(modDir, rows);
    }

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

    public GearSettingsPreset? GetEditableContentMaterialGearOverride(
        string modDir, string materialRel, GearSettingsPreset seed)
        => BagFor(modDir) is { } bag
            ? bag.GetEditableContentMaterialGear(modDir, materialRel, seed)
            : bindings.GetEditableContentMaterialGearOverride(modDir, materialRel, seed);

    public GearSettingsPreset? PeekContentMaterialGearOverride(string modDir, string materialRel)
        => BagFor(modDir) is { } bag
            ? bag.PeekContentMaterialGear(modDir, materialRel)
            : bindings.PeekContentMaterialGearOverride(modDir, materialRel);

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
    /// "Reset to defaults" for one option. Against a pinned preset only the live override changes; a design
    /// binding also forgets it in the stored binding.
    /// </summary>
    public bool ClearOptionOverride(string modDir, string? group, string? option)
        => BagFor(modDir) is { } bag
            ? bag.ClearOption(modDir, group, option)
            : bindings.ClearOptionOverride(modDir, group, option);
}
