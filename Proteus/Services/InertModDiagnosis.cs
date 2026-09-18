using System;
using System.Collections.Generic;
using System.Linq;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public static class InertModDiagnosis
{
    /// <summary>
    /// One body material's outcome on the last composite: how many overlays blended into each channel, whether
    /// any asked for a diffuse, and whether a non-overlay pass (AO, skin-tint suppression) edited a buffer.
    /// </summary>
    public readonly record struct ChannelContribution(
        string Material, int Diffuse, int Normal, int Mask, bool DiffuseWanted, bool Touched);

    /// <summary>
    /// Why an enabled mod contributed nothing at all, in the order <see cref="Explain"/> tests them.
    /// </summary>
    public enum InertCause
    {
        /// <summary>Not inert. Never stored; the absence of an entry is what says a mod is fine.</summary>
        None,
        /// <summary>Penumbra did not answer when asked which options are on; says nothing about the mod.</summary>
        SettingsUnreadable,
        /// <summary>Every option group the pack declares is absent from Penumbra's copy: an authoring error.</summary>
        GroupsMissing,
        /// <summary>The groups are all there and the user has ticked nothing in any of them.</summary>
        NothingTicked,
        /// <summary>Options are ticked, but every material the pack paints belongs to a body or race this
        /// character is not wearing.</summary>
        WrongRace,
        /// <summary>The masks render as a gear shell built FROM a mask, and no mask is ticked.</summary>
        MaskNeedsShell,
        /// <summary>Options resolved but none reached a surface this character has loaded: the "we don't know" rung.</summary>
        NothingReached,
    }

    /// <summary>
    /// One enabled mod's reason for contributing nothing, with the parts both formatters (English log and
    /// localized tooltip) need already joined into readable text.
    /// </summary>
    /// <param name="GroupCount">How many option groups the pack declares. Only set for
    /// <see cref="InertCause.NothingTicked"/>.</param>
    /// <param name="Groups">The groups involved, comma-joined and in the pack's own order.</param>
    /// <param name="Wants">What the pack paints (bodies and races) for <see cref="InertCause.WrongRace"/>.</param>
    /// <param name="Have">What the character actually is, same shape as <paramref name="Wants"/>.</param>
    public readonly record struct InertReason(
        InertCause Cause, int GroupCount, string Groups, string Wants, string Have);

    /// <summary>
    /// Which rung one inert mod lands on. Pure, so the ladder is testable; first match wins, most specific first.
    /// </summary>
    /// <param name="masksSelected">Mask options ticked, toe cap excluded (it is not a mask).</param>
    /// <param name="materialsFiltered">The live-material filter dropped at least one of this mod's
    /// materials AND the sibling pass did not put it back.</param>
    /// <param name="wants">What the pack paints, already readable. Empty when unknown.</param>
    /// <param name="have">What the character is, same shape. Empty when unknown (mid-redraw); never a mismatch.</param>
    internal static InertReason Explain(
        ResolutionDiagnostic diag,
        bool maskGroupPresent,
        int masksSelected,
        bool maskLayerIsGear,
        bool materialsFiltered,
        string wants,
        string have)
    {
        // 1. We could not find out. Only Unavailable counts; NotAsked is healthy.
        if (diag.Settings == SettingsRead.Unavailable)
            return new InertReason(InertCause.SettingsUnreadable, 0, "", "", "");

        // 2. The pack names groups Penumbra has not got. Only when ALL of them are missing.
        if (diag.GroupCount > 0 && diag.MissingGroups.Count == diag.GroupCount)
            return new InertReason(InertCause.GroupsMissing, diag.GroupCount,
                string.Join(", ", diag.MissingGroups), "", "");

        // 3. Nothing is ticked anywhere; masks count as a group.
        int selectable = diag.GroupCount + (maskGroupPresent ? 1 : 0);
        int untouched  = diag.EmptyGroups.Count + diag.MissingGroups.Count
                       + (maskGroupPresent && masksSelected == 0 ? 1 : 0);
        if (selectable > 0 && untouched == selectable)
        {
            var names = diag.EmptyGroups.Concat(diag.MissingGroups).ToList();
            if (maskGroupPresent && masksSelected == 0) names.Add(SidecarDiscoveryService.MaskGroupName);
            return new InertReason(InertCause.NothingTicked, selectable, string.Join(", ", names), "", "");
        }

        // 4. Ticked, but for a body nobody here is wearing. Requires a known wearer.
        if (materialsFiltered && have.Length > 0 && wants.Length > 0)
            return new InertReason(InertCause.WrongRace, 0, "", wants, have);

        // 5. Ticked fabric with nothing to cut it into: with the Masks tab set to Gear, no mask means no shell.
        if (maskLayerIsGear && masksSelected == 0)
            return new InertReason(InertCause.MaskNeedsShell, 0,
                SidecarDiscoveryService.MaskGroupName, "", "");

        // 6. Everything resolved and none of it arrived; a wrong specific reason is worse than an honest vague one.
        return new InertReason(InertCause.NothingReached, 0, "", "", "");
    }

    /// <summary>A body-type set and a race-code set as one deduplicated phrase.</summary>
    internal static string Describe(IEnumerable<string> bodyTypes, IEnumerable<string> charCodes)
    {
        var bodies = string.Join("+", bodyTypes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x));
        var races  = ModelRace.DescribeAll(charCodes);
        if (bodies.Length == 0) return races;
        return races.Length == 0 ? bodies : $"{bodies} · {races}";
    }

    /// <summary>One <see cref="InertReason"/> as English; the log stays English in every locale. The translated
    /// wording lives in <c>Strings.Mods.Inert*</c>.</summary>
    internal static string EnglishInert(InertReason r) => r.Cause switch
    {
        InertCause.SettingsUnreadable =>
            "Penumbra did not answer when asked which of its options are on",
        InertCause.GroupsMissing =>
            $"its Proteus data names option group(s) [{r.Groups}] that Penumbra's copy of the mod has not "
          + "got — renamed or dropped on re-export, so nothing in them can ever be selected",
        InertCause.NothingTicked =>
            $"nothing is ticked in Penumbra — its {r.GroupCount} option group(s) [{r.Groups}] are all empty",
        InertCause.WrongRace =>
            $"it paints {r.Wants}, and this character is {r.Have}",
        InertCause.MaskNeedsShell =>
            $"its masks render as gear, which needs a mask to build the shell from, and nothing is ticked "
          + $"in its \"{r.Groups}\" group",
        _ => "its ticked options resolved, but none of them reached a surface this character has loaded",
    };
}
