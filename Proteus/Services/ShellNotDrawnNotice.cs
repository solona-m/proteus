using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// What the post-redraw drawn check does about a shell the game is not drawing (the owner's report on #91's family):
/// one automatic full redraw first, with no chat — a redraw is what fixes it — and only if the shell is still missing
/// after that, one plainly worded message naming the accessory it rides on. Pure; the compositor supplies the state.
/// </summary>
internal static class ShellNotDrawnNotice
{
    internal enum Step { AutoRedraw, Notify, NotifyRedrawOff, Silent, Defer }

    /// <summary>Automatic redraws allowed in any five minutes; a third goes straight to the message.</summary>
    internal const int MaxAutoRedraws = 2;
    internal const long AutoRedrawWindowMs = 5 * 60_000;

    /// <summary>
    /// The next step for a shell found partly or not drawn, keyed by <paramref name="key"/> (the probe's content key).
    /// A superseded probe or a shell already told about is silent; with auto redraw off the user is asked to redraw; after
    /// our own redraw (or once this shell has had one) it is the message; a busy composite, held redraws or a publish that
    /// had write failures (the retry composite owns that) defer once; past the rate limit it is the message; otherwise
    /// one automatic redraw.
    /// </summary>
    internal static Step Next(string key, string? autoRedrawnKey, string? noticedKey, bool autoRedraw, bool afterOwnRedraw,
                              bool probeCurrent, bool compositeBusy, bool redrawHeld, bool publishHadWriteFailures,
                              int autoRedrawsLast5Min, int deferrals)
    {
        if (!probeCurrent) return Step.Silent;
        if (string.Equals(noticedKey, key, StringComparison.Ordinal)) return Step.Silent;
        if (!autoRedraw) return Step.NotifyRedrawOff;
        if (afterOwnRedraw || string.Equals(autoRedrawnKey, key, StringComparison.Ordinal)) return Step.Notify;
        if (publishHadWriteFailures || compositeBusy || redrawHeld) return deferrals < 1 ? Step.Defer : Step.Notify;
        if (autoRedrawsLast5Min >= MaxAutoRedraws) return Step.Notify;
        return Step.AutoRedraw;
    }

    /// <summary>
    /// The accessories <paramref name="modelPaths"/> are published at, for a person: "Item name (Slot)" from the Item
    /// sheet; a facewear set's glasses name from the Glasses sheet; otherwise "Slot (e6085)", or the file name for a path
    /// that names no slot. Distinct, joined with ", ".
    /// </summary>
    internal static string HostLabel(IEnumerable<string> modelPaths, Func<int, int, string?> item, Func<int, string?> facewear,
                                     Func<int, bool> isFacewearSet, Func<string, string> slotDisplay)
    {
        var labels = new List<string>();
        foreach (var path in modelPaths)
        {
            string label;
            if (ContentSlot.Parse(path) is not { } p || !int.TryParse(p.SetTag.AsSpan(1), out int set))
                label = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            else
            {
                var slot = slotDisplay(p.Label);
                if (p.Label == "Head" && p.SetTag.StartsWith('e') && isFacewearSet(set)
                    && facewear(set) is { } glasses && !string.IsNullOrWhiteSpace(glasses))
                    label = glasses;
                else if (p.Category > 0 && item(p.Category, set) is { } name && !string.IsNullOrWhiteSpace(name))
                    label = $"{name} ({slot})";
                else
                    label = $"{slot} ({p.SetTag})";
            }
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase)) labels.Add(label);
        }
        return string.Join(", ", labels);
    }

    /// <summary>
    /// Whether <paramref name="modelPath"/> is one of Proteus's own invisible carriers — the Emperor's New ring, bracelets
    /// or necklace in a carrier slot, or the invisible glasses — which the wearer never put on themselves.
    /// </summary>
    internal static bool IsCarrierHost(string modelPath, int? glassesCarrierSet)
    {
        if (ContentSlot.Parse(modelPath) is not { } p || !int.TryParse(p.SetTag.AsSpan(1), out int set)) return false;
        var stem = Path.GetFileNameWithoutExtension(modelPath.Replace('\\', '/'));
        var suffix = stem[(stem.LastIndexOf('_') + 1)..];
        if (p.SetTag.StartsWith('a') && set == InvisibleRing.EmperorSetId)
            return InvisibleRing.CarrierSlots.Any(c => string.Equals(c.Slot, suffix, StringComparison.OrdinalIgnoreCase));
        return p.SetTag.StartsWith('e') && glassesCarrierSet == set
            && string.Equals(suffix, "met", StringComparison.OrdinalIgnoreCase);
    }

    private const string RedrawOffKey = "Chat.ShellNotDrawnRedrawOff.Fmt";
    private const string RedrawOffText =
        "[Proteus] Part of your second skin isn't showing. It rides on {0}. Auto redraw is off, so Proteus didn't "
        + "redraw your character: use Penumbra's Redraw button or change zones, and if it still doesn't show, "
        + "unequip and re-equip that accessory.";
    private const string CarrierKey = "Chat.ShellCarrierNotDrawn.Fmt";
    private const string CarrierText =
        "[Proteus] Part of your second skin isn't showing. It rides on {0}, the invisible accessory Proteus puts on "
        + "to carry it. Click \"Recomposite now\" in Proteus's window; if it still doesn't show, take that accessory "
        + "off and Proteus will put it back on.";
    private const string PartKey = "Chat.ShellPartNotDrawn.Fmt";
    private const string PartText =
        "[Proteus] Part of your second skin still isn't showing. That part rides on {0}. Unequip and re-equip it, "
        + "or wear that layer on a different accessory.";
    private const string WholeKey = "Chat.ShellNotDrawn.Fmt";
    private const string WholeText =
        "[Proteus] Your second skin still isn't showing, although {0} is equipped. Unequip and re-equip it, or wear "
        + "the layer on a different accessory.";

    /// <summary>The localization key and English text for the message, by wording.</summary>
    internal static (string Key, string Fallback) StringFor(bool partly, bool carrier, bool redrawOff)
        => redrawOff ? (RedrawOffKey, RedrawOffText)
         : carrier ? (CarrierKey, CarrierText)
         : partly ? (PartKey, PartText)
         : (WholeKey, WholeText);

    /// <summary>The message template in the user's language, by wording (see <see cref="StringFor"/>).</summary>
    internal static string Localized(bool partly, bool carrier, bool redrawOff)
        => redrawOff ? ProteusLoc.Localize(RedrawOffKey, RedrawOffText)
         : carrier ? ProteusLoc.Localize(CarrierKey, CarrierText)
         : partly ? ProteusLoc.Localize(PartKey, PartText)
         : ProteusLoc.Localize(WholeKey, WholeText);
}
