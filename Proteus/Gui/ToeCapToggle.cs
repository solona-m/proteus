using System;
using System.Collections.Generic;
using System.Linq;
using Penumbra.Api.Enums;
using Proteus.Interop;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>Where the tick writes: the mod's own Toe Cap option in Penumbra, or the mod's "Cap the toes" Proteus setting.</summary>
internal enum ToeCapMode { Option, Flag }

/// <summary>What the Geometry section's "Cap the toes" tick shows this frame.</summary>
/// <param name="Visible">Penumbra answered for the mod.</param>
/// <param name="On">The next composite would cap this mod: its own mapped cap is selected, or its setting is on.</param>
/// <param name="Held">Option mode, and a temporary setting (a Glamourer design, typically) holds the mod, so a write would not show.</param>
/// <param name="Inherited">Option mode, and the collection inherits this mod's settings; a write gives it its own.</param>
/// <param name="Mode">Which store the tick writes.</param>
/// <param name="Locked">Flag mode, but the mod's own single-select cap is selected in Penumbra: only Penumbra can turn it off.</param>
internal readonly record struct ToeCapTickView(bool Visible, bool On, bool Held, bool Inherited, ToeCapMode Mode, bool Locked);

/// <summary>What one click writes: the Masks group's new selection (null = no Penumbra write) and the setting (null = unchanged).</summary>
internal readonly record struct ToeCapWrite(IReadOnlyList<string>? MaskSelection, bool? CapToes);

internal enum ToeCapApplyStatus { Ok, PenumbraRefused, SaveFailed }

/// <summary>How a click went; <paramref name="Ec"/> is Penumbra's answer when it refused.</summary>
internal readonly record struct ToeCapApplyResult(ToeCapApplyStatus Status, PenumbraApiEc? Ec = null);

/// <summary>Whether a ticked cap can show from this mod: the hint under the tick.</summary>
internal enum ToeCapReach
{
    /// <summary>Some active overlay becomes a garment on the body, which the cap shapes.</summary>
    Draws,
    /// <summary>The mod is off in the collection.</summary>
    ModOff,
    /// <summary>Nothing active becomes a garment on the body.</summary>
    NoShell,
    /// <summary>Can't tell from here (a design binding, or a possible mask shell): say nothing.</summary>
    Unknown,
}

/// <summary>
/// The "Cap the toes" tick in the Colors window's Geometry section, on every mod. On a mod that ships its own toe cap in
/// a multi-select Masks group it is that reserved <see cref="SidecarDiscoveryService.ToeCapOptionName"/> option in
/// Penumbra (per collection, kept by designs); on any other mod it is the <see cref="ProteusMetadata.CapToes"/> setting,
/// which caps with Proteus's built-in cap. The compositor's precedence lives in
/// <see cref="SidecarDiscoveryService.ChooseToeCap"/>. Pure, so every state the tick shows is tested; StatusWindow only
/// maps it onto ImGui and supplies the two writes.
/// </summary>
internal static class ToeCapToggle
{
    /// <summary>
    /// The tick's state from this frame's settings read and the mod's setting. Hidden without settings. On follows the
    /// compositor's rule (<see cref="SidecarDiscoveryService.FirstSelectedToeCap"/>, then its map, else the setting), so
    /// the tick and the cap cannot disagree.
    /// </summary>
    public static ToeCapTickView View(ToeCapOptionRef? option, PenumbraBridge.ModSettingsSnapshot? settings, bool capToes)
    {
        if (settings is not { } s) return default;
        bool optionOn = false;
        if (option != null)
        {
            var first = SidecarDiscoveryService.FirstSelectedToeCap(GroupSelection(s.Options, option.Group));
            optionOn = first != null && option.CappedOptions.Contains(first, StringComparer.OrdinalIgnoreCase);
        }
        var mode = option is { Writable: true } ? ToeCapMode.Option : ToeCapMode.Flag;
        bool isOption = mode == ToeCapMode.Option;
        return new ToeCapTickView(true, optionOn || capToes, s.Temporary && isOption, s.Inherited && isOption, mode,
                                  optionOn && !isOption);
    }

    /// <summary>
    /// What a click writes. Ticking selects the mod's own cap in option mode, or sets the setting in flag mode. Unticking
    /// deselects every toe-cap option (when one is selected and the group is writable) and clears the setting if set.
    /// </summary>
    public static ToeCapWrite WriteFor(ToeCapOptionRef? option, IReadOnlyDictionary<string, List<string>>? options,
                                       ToeCapTickView view, bool capToes, bool on)
    {
        if (on)
            return view.Mode == ToeCapMode.Option && option != null
                ? new ToeCapWrite(SelectionAfter(GroupSelection(options, option.Group), option.Option, true), null)
                : new ToeCapWrite(null, true);

        IReadOnlyList<string>? selection = null;
        if (option is { Writable: true })
        {
            var current = GroupSelection(options, option.Group);
            if (SidecarDiscoveryService.FirstSelectedToeCap(current) != null)
                selection = SelectionAfter(current, option.Option, false);
        }
        return new ToeCapWrite(selection, capToes ? false : null);
    }

    /// <summary>
    /// Write the "Cap the toes" setting into <paramref name="md"/> (true or removed, never false) and save it; when the
    /// save fails, put the old value back, so the tick and the next composite still read what the file says.
    /// </summary>
    public static bool WriteFlag(ProteusMetadata md, bool flag, Func<bool> save)
    {
        var prev = md.CapToes;
        md.CapToes = flag ? true : null;
        if (save()) return true;
        md.CapToes = prev;
        return false;
    }

    /// <summary>
    /// Carry out a click: the Penumbra write first, and the setting only once that took (or none was needed), so a
    /// refusal leaves both stores as they were. <paramref name="writeFlag"/> returns whether the save succeeded.
    /// </summary>
    public static ToeCapApplyResult Apply(ToeCapWrite write, Func<IReadOnlyList<string>, PenumbraApiEc> writeOption,
                                          Func<bool, bool> writeFlag)
    {
        if (write.MaskSelection is { } selection)
        {
            var ec = writeOption(selection);
            if (!Succeeded(ec)) return new ToeCapApplyResult(ToeCapApplyStatus.PenumbraRefused, ec);
        }
        if (write.CapToes is { } flag && !writeFlag(flag))
            return new ToeCapApplyResult(ToeCapApplyStatus.SaveFailed);
        return new ToeCapApplyResult(ToeCapApplyStatus.Ok);
    }

    /// <summary>A group's selection from Penumbra's settings, by name in any case; null when the group is absent.</summary>
    public static IReadOnlyList<string>? GroupSelection(IReadOnlyDictionary<string, List<string>>? options, string group)
    {
        if (options == null) return null;
        foreach (var (key, value) in options)
            if (string.Equals(key, group, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>
    /// The group's new selection after a click: every other mask kept in order (blanks and repeats dropped), every
    /// toe-cap option removed, and <paramref name="capOption"/> appended when ticking. Never changes its input.
    /// </summary>
    public static List<string> SelectionAfter(IReadOnlyList<string>? current, string capOption, bool on)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var o in current ?? [])
            if (!string.IsNullOrWhiteSpace(o) && !SidecarDiscoveryService.IsToeCapOption(o) && seen.Add(o))
                result.Add(o);
        if (on) result.Add(capOption);
        return result;
    }

    /// <summary>Whether Penumbra took the write.</summary>
    public static bool Succeeded(PenumbraApiEc ec) => ec is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged;

    /// <summary>
    /// Whether a ticked cap can show from this mod. A prediction of the compositor's own rules, kept in step with them:
    /// promotion in <c>CompositorService.CompositeRun.RouteOverlays</c>, mask shells in <c>CompositeRun.ResolveMasks</c>,
    /// a layer's surface in <c>SecondSkinService.SurfaceAssignment.SurfaceKeyOf</c>, the cap cut on body surfaces only in
    /// <c>SecondSkinService.ShellTextureBake</c>, and the strength gate in <c>SecondSkinService.ToeCapFor</c>. Says
    /// <see cref="ToeCapReach.Unknown"/> rather than guess: under a design binding (its layers aren't visible here) and
    /// when a mask shell is possible (that needs file probes per frame). Does not predict the toe-coverage gate.
    /// </summary>
    /// <param name="selection">This frame's Penumbra settings for the mod, the Masks group included.</param>
    public static ToeCapReach Reach(ProteusMetadata md, bool modEnabled,
        IReadOnlyDictionary<string, List<string>>? selection, bool bindingActive)
    {
        if (bindingActive) return ToeCapReach.Unknown;
        if (!modEnabled) return ToeCapReach.ModOff;

        var active = ActiveOverlays(md, selection);
        bool geometry = RenderModeInference.WantsGeometry(md);
        if (active.Any(p => CarriesCap(p.Overlay, p.Rows, geometry))) return ToeCapReach.Draws;

        // A mask shell is built on the body and gets the cap too (full strength, whatever the overlays say). It is built
        // for a mod with any shell of its own, or an all-skin mod whose masks render as cloth, when a selected mask has
        // _id/_n files; those aren't probed per frame, so a possible one means "can't tell".
        bool otherMask = GroupSelection(selection, SidecarDiscoveryService.MaskGroupName)?
            .Any(o => !string.IsNullOrWhiteSpace(o) && !SidecarDiscoveryService.IsToeCapOption(o)) == true;
        bool anyShell = active.Any(p => CompositorService.CanRenderAsShell(p.Overlay) && BecomesGear(p.Overlay, p.Rows, geometry));
        if (otherMask && (md.MaskDescriptor?.Layer == OverlayLayer.Gear || anyShell))
            return ToeCapReach.Unknown;

        return ToeCapReach.NoShell;
    }

    /// <summary>
    /// Whether this overlay becomes a garment the cap shapes: it can be a shell, sits on the body, has the cap on, and
    /// is Gear or promoted to it with a cap wanted.
    /// </summary>
    internal static bool CarriesCap(OverlayDescriptor d, IReadOnlyList<ColorTableRowPreset>? rows, bool geometryWanted)
        => CompositorService.CanRenderAsShell(d)
        && OnBodySurface(d.MaterialGamePaths)
        && (d.ToeCapStrength ?? 1f) > 0f
        && BecomesGear(d, rows, geometryWanted);

    /// <summary>
    /// Whether the shell builder puts this overlay on the body: no material names the body, otherwise the first surface
    /// named decides (<c>SecondSkinService.SurfaceAssignment.SurfaceKeyOf</c>).
    /// </summary>
    internal static bool OnBodySurface(IReadOnlyList<string>? materialGamePaths)
    {
        var keys = ShellSurface.KeysFor(materialGamePaths);
        return keys.Count == 0 ? materialGamePaths == null || materialGamePaths.Count == 0 : keys[0].IsBody;
    }

    // aboveGear and needsUnmirroredShell are false: with toeCapWanted they sit in the same unpinned branch.
    private static bool BecomesGear(OverlayDescriptor d, IReadOnlyList<ColorTableRowPreset>? rows, bool geometryWanted)
        => d.Layer == OverlayLayer.Gear
        || RenderModeInference.ShouldPromoteToGear(OverlayLayer.Skin, d.ManualShaderLock, rows, aboveGear: false,
               canShell: true, needsUnmirroredShell: false, toeCapWanted: true, geometryWanted: geometryWanted);

    /// <summary>
    /// The overlays the composite draws for this selection, with the rows each reads: the mod's own overlays whenever it
    /// has any, otherwise every selected option of each group, with the option's rows else the mod's. Mirrors
    /// <c>SidecarDiscoveryService.ResolveActiveOverlays</c>.
    /// </summary>
    private static List<(OverlayDescriptor Overlay, IReadOnlyList<ColorTableRowPreset>? Rows)> ActiveOverlays(
        ProteusMetadata md, IReadOnlyDictionary<string, List<string>>? selection)
    {
        var result = new List<(OverlayDescriptor, IReadOnlyList<ColorTableRowPreset>?)>();
        if (md.Overlays is { Count: > 0 } own)
        {
            foreach (var d in own) result.Add((d, md.ColorTableRows));
            return result;
        }

        foreach (var group in md.OptionGroups ?? [])
        {
            var sel = GroupSelection(selection, group.PenumbraGroupName);
            if (sel is not { Count: > 0 }) continue;
            foreach (var opt in group.Options)
            {
                if (!sel.Any(s => string.Equals(s, opt.Name, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var d in opt.Overlays) result.Add((d, opt.ColorTableRows ?? md.ColorTableRows));
            }
        }
        return result;
    }
}

/// <summary>
/// Which mods have a usable toe cap, by folder: reading it parses the manifest, so it is read once per mod and dropped
/// when the colour window opens for the mod or the mod is renamed.
/// </summary>
internal sealed class ToeCapOptionCache
{
    private readonly Dictionary<string, ToeCapOptionRef?> byMod = new(StringComparer.OrdinalIgnoreCase);

    public ToeCapOptionRef? Get(string modDir, string? modRoot, string sidecarRoot)
    {
        if (!byMod.TryGetValue(modDir, out var found))
            byMod[modDir] = found = SidecarDiscoveryService.FindToeCapOption(modRoot, sidecarRoot);
        return found;
    }

    public void Forget(string modDir) => byMod.Remove(modDir);
}
