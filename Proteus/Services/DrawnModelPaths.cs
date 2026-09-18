using System;
using System.Collections.Generic;
using System.Linq;
using Penumbra.Api.Enums;

namespace Proteus.Services;
using static Proteus.Services.CompositorService;

internal static class DrawnModelPaths
{
    // The second skin cuts each slot's shell from the gear MODEL the character is drawing there, e.g.
    // chara/equipment/e6039/model/c0201e6039_sho.mdl → sho. The model, NOT the material: a model loads
    // reliably even when a gear piece's materials fail. Bare slots (e0000) are omitted.
    private static readonly System.Text.RegularExpressions.Regex EquipModelRe = new(
        @"chara/equipment/(e\d+)/model/c\d+e\d+_(top|dwn|glv|sho)\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // "_met" is BOTH the head-equipment slot and the Dawntrail facewear/glasses bonus item, and the two can be
    // worn together, so these are a SORTED LIST rather than a by-suffix map where one would clobber the other.
    private static readonly System.Text.RegularExpressions.Regex MetModelRe = new(
        @"chara/equipment/(e\d+)/model/c\d+e\d+_met\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static List<string> EquippedMetModelsFromModels(HashSet<string>? modelPaths, HashSet<int>? facewearSets)
    {
        var met = new List<string>();
        if (modelPaths == null) return met;
        foreach (var p in modelPaths)
        {
            var match = MetModelRe.Match(p);
            if (!match.Success) continue;
            if (string.Equals(match.Groups[1].Value, "e0000", StringComparison.OrdinalIgnoreCase)) continue;
            // Only facewear can host the shell; a head item on the same "_met" path must be ignored.
            // facewearSets == null (sheet not readable yet) means don't filter.
            if (facewearSets != null
                && int.TryParse(match.Groups[1].Value.AsSpan(1), out var set)
                && !facewearSets.Contains(set))
                continue;
            met.Add(match.Value);
        }
        met.Sort(StringComparer.OrdinalIgnoreCase);   // stable order — the host must not flip run to run
        return met;
    }

    // The character's own HUMAN part models: face, hair, tail, Viera ears. A face draws SEVERAL models (_fac
    // beside _iri, _etc and a race's extras), so this is a list, not a by-slot map.
    private static readonly System.Text.RegularExpressions.Regex HumanPartModelRe = new(
        @"chara/human/c\d+/obj/(face|hair|tail|zear)/[fhtz]\d+/model/c\d+[fhtz]\d+_\w+\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static List<string> HumanPartModelsFromModels(HashSet<string>? modelPaths)
    {
        var parts = new List<string>();
        if (modelPaths == null) return parts;
        foreach (var p in modelPaths)
            if (HumanPartModelRe.IsMatch(p)) parts.Add(p);
        parts.Sort(StringComparer.OrdinalIgnoreCase);   // stable order, same reason as the met list
        return parts;
    }

    /// <summary>
    /// Whether a walk caught a character with a body at all: at least one top/dwn/glv/sho model, equipped or
    /// bare. Non-empty is not enough: mid-redraw the draw object has face and hair loaded but no body part,
    /// and a drawn character always has at least one of these slots, so a walk with none is a teardown.
    /// </summary>
    internal static bool HasBodySlotModel(HashSet<string>? modelPaths)
        => modelPaths != null && modelPaths.Any(p => EquipModelRe.IsMatch(p));

    internal static Dictionary<string, string> EquippedPartModelsFromModels(HashSet<string>? modelPaths)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (modelPaths == null) return models;
        foreach (var p in modelPaths)
        {
            var match = EquipModelRe.Match(p);
            if (!match.Success) continue;
            if (string.Equals(match.Groups[1].Value, "e0000", StringComparison.OrdinalIgnoreCase)) continue;
            models[match.Groups[2].Value.ToLowerInvariant()] = match.Value;
        }
        return models;
    }

    internal static string? DrawnRaceCodeFromModels(HashSet<string>? modelPaths)
    {
        if (modelPaths == null) return null;
        foreach (var p in modelPaths)
            if (ExtractHumanCharCode(p) is { Length: 5 } code && (code[0] == 'c' || code[0] == 'C'))
                return code[1..];
        return null;
    }

    internal static Dictionary<string, string> BareBodyModelsFromModels(HashSet<string>? modelPaths)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (modelPaths == null) return models;
        foreach (var p in modelPaths)
        {
            var match = EquipModelRe.Match(p);
            if (!match.Success) continue;
            if (!string.Equals(match.Groups[1].Value, "e0000", StringComparison.OrdinalIgnoreCase)) continue;
            models[match.Groups[2].Value.ToLowerInvariant()] = match.Value;
        }
        return models;
    }

    // The second skin appends its shell into a ring/bracelet the player already wears (so the accessory
    // stays visible), keyed by slot — chara/accessory/a0114/model/c0201a0114_rir.mdl → rir. Detect them
    // the same way as the equipment models above.
    private static readonly System.Text.RegularExpressions.Regex AccessoryModelRe = new(
        @"chara/accessory/(a\d+)/model/c\d+a\d+_(rir|ril|wrs|nek)\.mdl",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static Dictionary<string, string> EquippedAccessoryModelsFromModels(HashSet<string>? modelPaths)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (modelPaths == null) return models;
        foreach (var p in modelPaths)
        {
            var match = AccessoryModelRe.Match(p);
            if (!match.Success) continue;
            models[match.Groups[2].Value.ToLowerInvariant()] = match.Value;
        }
        return models;
    }
}
