using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// One channel of live, non-destructive per-mod overrides (colour tables, gear settings, stack order), keyed
/// by mod directory and never written to <c>metadata.json</c>. Held by design bindings and presets.
/// <para/>
/// <b>Peek never creates; only an edit creates</b>: a snapshot created on read would shadow later metadata edits.
/// <para/>
/// <b>Nested mutation in place, structural change copy-on-write</b>: the compositor reads the published
/// dictionary on a background thread, so adding or removing a mod key publishes a fresh dictionary.
/// </summary>
public sealed class OverlayOverrideBag
{
    private readonly Action<IReadOnlyDictionary<string, OverlayColorOverride>?> publishColors;
    private readonly Action<IReadOnlyDictionary<string, OverlayGearOverride>?>  publishGear;
    private readonly Action<IReadOnlyDictionary<string, List<string>>?>         publishStack;

    private readonly object gate = new();
    private Dictionary<string, OverlayColorOverride>? colors;
    private Dictionary<string, OverlayGearOverride>?  gear;
    private Dictionary<string, List<string>>?         stack;

    public OverlayOverrideBag(
        Action<IReadOnlyDictionary<string, OverlayColorOverride>?> publishColors,
        Action<IReadOnlyDictionary<string, OverlayGearOverride>?>  publishGear,
        Action<IReadOnlyDictionary<string, List<string>>?>         publishStack)
    {
        this.publishColors = publishColors;
        this.publishGear   = publishGear;
        this.publishStack  = publishStack;
    }

    /// <summary>A bag that publishes nowhere — for unit tests, which have no compositor.</summary>
    public static OverlayOverrideBag Detached() => new(_ => { }, _ => { }, _ => { });

    // ── Whole-channel state ─────────────────────────────────────────────────────

    /// <summary>True when this channel supplies colours for the mod — i.e. it, not the mod's own
    /// metadata, is what the composite and the editor are looking at.</summary>
    public bool Governs(string modDir)
    {
        lock (gate) return colors != null && colors.ContainsKey(modDir);
    }

    /// <summary>True when any mod at all is governed. Distinct from <see cref="Governs"/>: a design
    /// binding publishes an entry for every mod it captured, including ones with nothing stored.</summary>
    public bool Active
    {
        get { lock (gate) return colors != null; }
    }

    public IReadOnlyDictionary<string, OverlayColorOverride>? Colors { get { lock (gate) return colors; } }
    public IReadOnlyDictionary<string, OverlayGearOverride>?  Gear   { get { lock (gate) return gear; } }
    public IReadOnlyDictionary<string, List<string>>?         Stack  { get { lock (gate) return stack; } }

    /// <summary>Replace all three dictionaries at once and publish — a design binding adopting a whole
    /// captured look. The caller owns cloning; whatever is handed in is mutated in place by later edits.</summary>
    public void Adopt(
        Dictionary<string, OverlayColorOverride> newColors,
        Dictionary<string, OverlayGearOverride>  newGear,
        Dictionary<string, List<string>>         newStack)
    {
        lock (gate)
        {
            colors = newColors;
            gear   = newGear;
            stack  = newStack;
        }

        // Published from the locals: re-reading the fields could publish another thread's swap.
        publishColors(newColors);
        publishGear(newGear);
        publishStack(newStack);
    }

    /// <summary>Drop everything and un-publish. Returns whether anything was actually set.</summary>
    public bool Clear()
    {
        bool changed;
        lock (gate)
        {
            changed = colors != null || gear != null || stack != null;
            colors  = null;
            gear    = null;
            stack   = null;
        }

        if (!changed) return false;
        publishColors(null);
        publishGear(null);
        publishStack(null);
        return true;
    }

    /// <summary>
    /// Add or replace one mod's entry, copy-on-write, and publish (the preset path). A null or empty
    /// <paramref name="stackOrder"/> removes the stack entry so the global config applies.
    /// </summary>
    public void SetMod(string modDir, OverlayColorOverride modColors, OverlayGearOverride modGear,
        List<string>? stackOrder)
    {
        Dictionary<string, OverlayColorOverride> nextColors;
        Dictionary<string, OverlayGearOverride>  nextGear;
        Dictionary<string, List<string>>         nextStack;

        lock (gate)
        {
            nextColors = Copy(colors);
            nextGear   = Copy(gear);
            nextStack  = Copy(stack);

            nextColors[modDir] = modColors;
            nextGear[modDir]   = modGear;
            if (stackOrder is { Count: > 0 }) nextStack[modDir] = stackOrder;
            else                              nextStack.Remove(modDir);

            colors = nextColors;
            gear   = nextGear;
            stack  = nextStack;
        }

        publishColors(nextColors);
        publishGear(nextGear);
        publishStack(nextStack);
    }

    /// <summary>
    /// Drop one mod's entry, copy-on-write, and publish. When that was the last one the channel goes fully
    /// null, the compositor's cheap path.
    /// </summary>
    public bool RemoveMod(string modDir)
    {
        Dictionary<string, OverlayColorOverride>? nextColors;
        Dictionary<string, OverlayGearOverride>?  nextGear;
        Dictionary<string, List<string>>?         nextStack;

        lock (gate)
        {
            if (colors == null || !colors.ContainsKey(modDir)) return false;

            nextColors = Copy(colors);
            nextGear   = Copy(gear);
            nextStack  = Copy(stack);
            nextColors.Remove(modDir);
            nextGear.Remove(modDir);
            nextStack.Remove(modDir);

            if (nextColors.Count == 0)
            {
                nextColors = null;
                nextGear   = null;
                nextStack  = null;
            }

            colors = nextColors;
            gear   = nextGear;
            stack  = nextStack;
        }

        publishColors(nextColors);
        publishGear(nextGear);
        publishStack(nextStack);
        return true;
    }

    private static Dictionary<string, T> Copy<T>(Dictionary<string, T>? source)
        => source == null
            ? new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, T>(source, StringComparer.OrdinalIgnoreCase);

    // ── Colour rows ─────────────────────────────────────────────────────────────

    /// <summary>The stored rows for a mod's top-level colorset, or one option's, or null when this
    /// channel has none. NEVER creates one — see the class remarks.</summary>
    public List<ColorTableRowPreset>? PeekRows(string modDir, string? group, string? option)
    {
        lock (gate)
        {
            if (colors == null || !colors.TryGetValue(modDir, out var ovr)) return null;
            if (group == null || option == null) return ovr.Top;
            return ovr.Options != null && ovr.Options.TryGetValue(group, out var inner)
                && inner.TryGetValue(option, out var rows) ? rows : null;
        }
    }

    /// <summary>Install rows as the live override, on an actual edit. False when this channel does not
    /// govern the mod, so the caller persists to the metadata instead.</summary>
    public bool SetRows(string modDir, string? group, string? option, List<ColorTableRowPreset> rows)
    {
        lock (gate)
        {
            if (colors == null || !colors.TryGetValue(modDir, out var ovr)) return false;
            if (group == null || option == null) { ovr.Top = rows; return true; }
            ovr.Options ??= new();
            if (!ovr.Options.TryGetValue(group, out var inner)) ovr.Options[group] = inner = new();
            inner[option] = rows;
            return true;
        }
    }

    /// <summary>This channel's rows for one of an imported pack's materials, or null. NEVER creates one.</summary>
    public List<ColorTableRowPreset>? PeekContentMaterialRows(string modDir, string materialRel)
    {
        lock (gate)
            return colors != null && colors.TryGetValue(modDir, out var ovr)
                ? ovr.ResolveMaterial(materialRel) : null;
    }

    /// <summary>
    /// Install one content material's rows as the live override, on an actual edit. False when this channel does
    /// not govern the mod, so the caller persists to the metadata instead. Per material, not per option: that is
    /// the level the colour panel edits at, and the level the composite reads first
    /// (<see cref="ContentSettingLevels"/>).
    /// </summary>
    public bool SetContentMaterialRows(string modDir, string materialRel, List<ColorTableRowPreset> rows)
    {
        lock (gate)
        {
            if (colors == null || !colors.TryGetValue(modDir, out var ovr)) return false;
            // Copy-and-swap, like ProteusMetadata.MaterialSettings: the composite reads this map from another
            // thread, and it is handed to the run whole (ResolvedContent.MaterialRows).
            var next = ovr.Materials == null
                ? new Dictionary<string, List<ColorTableRowPreset>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<ColorTableRowPreset>>(ovr.Materials, StringComparer.OrdinalIgnoreCase);
            next[materialRel] = rows;
            ovr.Materials = next;
            return true;
        }
    }

    /// <summary>The mod's stored mask rows — the single shared Masks tab's colorset — or null. NEVER
    /// creates one.</summary>
    public List<ColorTableRowPreset>? PeekMaskRows(string modDir)
    {
        lock (gate)
            return colors != null && colors.TryGetValue(modDir, out var ovr) ? ovr.Mask : null;
    }

    /// <summary>Install the mask rows as the live override, on an actual edit.</summary>
    public bool SetMaskRows(string modDir, List<ColorTableRowPreset> rows)
    {
        lock (gate)
        {
            if (colors == null || !colors.TryGetValue(modDir, out var ovr)) return false;
            ovr.Mask = rows;
            return true;
        }
    }

    // ── Gear / layer settings ───────────────────────────────────────────────────

    /// <summary>
    /// The mutable gear settings the layer/shader editor binds to, or null when this channel does not
    /// govern the mod. group/option null targets the top-level overlay; otherwise that option's. Seeds
    /// from the metadata descriptor when nothing is stored yet, so editing starts from what's on screen.
    /// </summary>
    public GearSettingsPreset? GetEditableGear(string modDir, string? group, string? option,
        OverlayDescriptor seed)
    {
        lock (gate)
        {
            if (gear == null || !gear.TryGetValue(modDir, out var ovr)) return null;
            if (group != null && option != null)
            {
                ovr.Options ??= new();
                if (!ovr.Options.TryGetValue(group, out var inner)) ovr.Options[group] = inner = new();
                if (!inner.TryGetValue(option, out var g)) inner[option] = g = GearSettingsPreset.From(seed);
                return g;
            }
            return ovr.Top ??= GearSettingsPreset.From(seed);
        }
    }

    /// <summary>
    /// The same, seeded from a cloned preset rather than a descriptor — for a content pack's glow. An
    /// unconditional piece lands in <see cref="OverlayGearOverride.Content"/>, never sharing
    /// <see cref="OverlayGearOverride.Top"/> with the overlays.
    /// </summary>
    public GearSettingsPreset? GetEditableContentGear(string modDir, string? group, string? option,
        GearSettingsPreset seed)
    {
        lock (gate)
        {
            if (gear == null || !gear.TryGetValue(modDir, out var ovr)) return null;
            if (group != null && option != null)
            {
                ovr.Options ??= new();
                if (!ovr.Options.TryGetValue(group, out var inner)) ovr.Options[group] = inner = new();
                if (!inner.TryGetValue(option, out var g)) inner[option] = g = seed.Clone();
                return g;
            }
            return ovr.Content ??= seed.Clone();
        }
    }

    /// <summary>
    /// The mutable glow the content colour panel binds to for one MATERIAL, seeded from what is on screen when
    /// this channel has nothing stored for it yet. Null when the channel does not govern the mod.
    /// </summary>
    public GearSettingsPreset? GetEditableContentMaterialGear(
        string modDir, string materialRel, GearSettingsPreset seed)
    {
        lock (gate)
        {
            if (gear == null || !gear.TryGetValue(modDir, out var ovr)) return null;
            if (ovr.ResolveMaterial(materialRel) is { } have) return have;

            // Copy-and-swap for the same reason as SetContentMaterialRows.
            var made = seed.Clone();
            var next = ovr.Materials == null
                ? new Dictionary<string, GearSettingsPreset>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GearSettingsPreset>(ovr.Materials, StringComparer.OrdinalIgnoreCase);
            next[materialRel] = made;
            ovr.Materials = next;
            return made;
        }
    }

    /// <summary>Read-only peek at one content material's glow in this channel; null when it stores none.</summary>
    public GearSettingsPreset? PeekContentMaterialGear(string modDir, string materialRel)
    {
        lock (gate)
            return gear != null && gear.TryGetValue(modDir, out var ovr)
                ? ovr.ResolveMaterial(materialRel) : null;
    }

    /// <summary>The mutable gear settings the Masks tab binds to, seeded from the descriptor.</summary>
    public GearSettingsPreset? GetEditableMaskGear(string modDir, OverlayDescriptor seed)
    {
        lock (gate)
        {
            if (gear == null || !gear.TryGetValue(modDir, out var ovr)) return null;
            return ovr.Mask ??= GearSettingsPreset.From(seed);
        }
    }

    /// <summary>
    /// Read-only peek at one option's effective gear settings. Resolves through
    /// <see cref="OverlayGearOverride.Resolve"/>, the same call the compositor makes, so the Top fallback matches.
    /// </summary>
    public GearSettingsPreset? PeekGear(string modDir, string group, string option)
    {
        lock (gate)
            return gear != null && gear.TryGetValue(modDir, out var ovr) ? ovr.Resolve(group, option) : null;
    }

    /// <summary>Read-only peek at a content material's glow, resolved through
    /// <see cref="OverlayGearOverride.ResolveContent"/> — its own slot, never <c>Top</c>.</summary>
    public GearSettingsPreset? PeekContentGear(string modDir, string? group, string? option)
    {
        lock (gate)
            return gear != null && gear.TryGetValue(modDir, out var ovr) ? ovr.ResolveContent(group, option) : null;
    }

    // ── Stack order ─────────────────────────────────────────────────────────────

    /// <summary>This channel's mod-wide tab order (<see cref="Configuration.ModStackEntry"/> keys,
    /// top-first), or null when it doesn't override this mod's; callers then fall back to the global config.</summary>
    public IReadOnlyList<string>? StackOrderFor(string modDir)
    {
        lock (gate)
            return stack != null && stack.TryGetValue(modDir, out var o) ? new List<string>(o) : null;
    }

    /// <summary>Record a mod-wide tab restack into this channel rather than the global stack config.
    /// False when the channel is inactive, so the caller persists globally instead.</summary>
    public bool SetStackOrder(string modDir, IEnumerable<(string Group, string Option)> topFirst)
    {
        Dictionary<string, List<string>> published;
        lock (gate)
        {
            if (stack == null) return false;
            // Copy-on-write: adding a key in place would race the compositor's background read.
            var next = new Dictionary<string, List<string>>(stack, StringComparer.OrdinalIgnoreCase)
            {
                [modDir] = topFirst.Select(x => Configuration.ModStackEntry(x.Group, x.Option)).ToList(),
            };
            stack = published = next;
        }

        publishStack(published);
        return true;
    }

    // ── Clearing one option ─────────────────────────────────────────────────────

    /// <summary>
    /// Drop one option's colour + gear override from the live copies and republish. The owner must also
    /// forget the option in whatever it persists.
    /// </summary>
    public bool ClearOption(string modDir, string? group, string? option)
    {
        bool touched = false;
        IReadOnlyDictionary<string, OverlayColorOverride>? publishedColors;
        IReadOnlyDictionary<string, OverlayGearOverride>?  publishedGear;

        lock (gate)
        {
            if (colors != null && colors.TryGetValue(modDir, out var col))
                touched |= ClearScope(col.Options, group, option,
                    () => { bool had = col.Top != null; col.Top = null; return had; });
            if (gear != null && gear.TryGetValue(modDir, out var g))
                touched |= ClearScope(g.Options, group, option, () => ClearTopGear(g));

            publishedColors = colors;
            publishedGear   = gear;
        }

        if (!touched) return false;
        publishColors(publishedColors);
        publishGear(publishedGear);
        return true;
    }

    /// <summary>
    /// Clear the mod-wide gear scopes: the overlays' <see cref="OverlayGearOverride.Top"/> and an
    /// imported pack's <see cref="OverlayGearOverride.Content"/>. Both are mod-wide.
    /// </summary>
    public static bool ClearTopGear(OverlayGearOverride gear)
    {
        bool had = gear.Top != null || gear.Content != null;
        gear.Top     = null;
        gear.Content = null;
        return had;
    }

    /// <summary>Remove one group/option entry from an override map (pruning the group when it empties),
    /// or clear the top-level entry when BOTH group and option are null. Returns whether anything was
    /// there. A half-specified scope is refused rather than clearing Top.</summary>
    public static bool ClearScope<T>(Dictionary<string, Dictionary<string, T>>? options,
        string? group, string? option, Func<bool> clearTop)
    {
        if (group == null && option == null) return clearTop();
        if (group == null || option == null) return false;
        if (options == null || !options.TryGetValue(group, out var inner)) return false;
        if (!inner.Remove(option)) return false;
        if (inner.Count == 0) options.Remove(group);
        return true;
    }
}
