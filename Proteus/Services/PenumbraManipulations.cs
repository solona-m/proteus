using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.ShellColorRows;

internal static class PenumbraManipulations
{
    /// <summary>The set id from a model path for the given tree prefix ("…/a0114/model/…", 'a') → 114. Null when
    /// absent; callers must skip the candidate rather than guess a set.</summary>
    internal static int? ParseSetId(string gamePath, char prefix)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, $@"/{prefix}(\d+)/");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// The race/gender code a model path is loaded under ("…/c0101e0279_met.mdl" → "0101"), or null. Not always the
    /// wearer's own: the game race-deforms a fallback path, so a wearer's shell must never go onto a foreign-race path.
    /// </summary>
    internal static string? PathCharCode(string gamePath)
    {
        // Human body and part letters (b/f/h/t/z) too: without them a face falls back to the equipment code
        // and gets race-deformed.
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, @"/c(\d+)[abefhtz]\d+_");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Is this resolved disk path one of OUR managed-mod files (i.e. our own composite output)?</summary>
    internal static bool IsInsideOutputRoot(string diskPath, string outputRoot)
    {
        try
        {
            var full = Path.GetFullPath(diskPath);
            var root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // unparseable path: treat as external
    }

    /// <summary>
    /// A model file from a Proteus mod (carries Proteus/metadata.json, not our output mod), by the same rule
    /// <see cref="SidecarDiscoveryService"/> uses.
    /// </summary>
    internal static bool IsProteusModFile(string? disk, string outputRoot)
    {
        if (string.IsNullOrEmpty(disk)) return false;
        string? modsRoot;
        try { modsRoot = Path.GetDirectoryName(Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch { return false; }
        return modsRoot != null
            && HatCompatService.InMods(disk, modsRoot, out var modRoot, out _)
            && !string.Equals(Path.GetFileName(modRoot), SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile));
    }

    /// <summary>
    /// Several "_met" candidates can be loaded at once (head and facewear). Order them deterministically, our
    /// injected pair first, then by set id, so the chosen host cannot flip between composites.
    /// </summary>
    internal static IEnumerable<string> OrderMetCandidates(IReadOnlyList<string>? metModels, int? invisibleGlassesSet)
        => metModels == null
            ? []
            : metModels
                .OrderByDescending(p => invisibleGlassesSet is int inv && ParseSetId(p, 'e') == inv)
                .ThenBy(p => ParseSetId(p, 'e') ?? int.MaxValue)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Slot's position in an EQDP entry; each slot owns two bits, so its mask is <c>3 &lt;&lt; 2*index</c>. Equipment and
    /// accessories are separate tables.
    /// </summary>
    private static int EqdpSlotIndex(string slot) => slot switch
    {
        "Head" => 0, "Body" => 1, "Hands" => 2, "Legs" => 3, "Feet" => 4,        // equipment
        "Ears" => 0, "Neck" => 1, "Wrists" => 2, "RFinger" => 3, "LFinger" => 4, // accessory
        _ => 0,
    };

    /// <summary>The race/gender index of a char code ("0801" → 8): odd = male, (n-1)/2 indexes <see cref="RaceNames"/>.
    /// Null outside the playable range 1..18.</summary>
    internal static int? RaceIndex(string? code) => ModelRace.Index(code);

    /// <summary>
    /// The race the game falls through to when a set declares no model for <paramref name="n"/>, or 0 at the root.
    /// Mirrors Penumbra.GameData's <c>GenderRace.Fallback</c>.
    /// </summary>
    internal static int EqdpFallbackIndex(int n) => ModelRace.Fallback(n);

    /// <summary>
    /// Would emptying <paramref name="from"/>'s EQDP entry actually land the game on <paramref name="to"/>? Requires
    /// <paramref name="to"/> on the fall-through chain and the same gender.
    /// </summary>
    internal static bool CanFallThrough(string? from, string? to)
    {
        if (RaceIndex(from) is not { } f || RaceIndex(to) is not { } t) return false;
        if (f % 2 != t % 2) return false;
        // The bound guards against a cycle, not a real depth.
        for (int i = 0, cur = f; i < 8; i++)
        {
            cur = EqdpFallbackIndex(cur);
            if (cur == 0) return false;
            if (cur == t) return true;
        }
        return false;
    }

    /// <summary>
    /// Declare whether a set has a model for a given race/gender. <paramref name="hasModel"/> false makes the game
    /// load the parent race's model with the racial deform; true loads it natively with none.
    /// </summary>
    internal static object EqdpManipulation(string charCode, string slot, int setId, bool hasModel = true)
    {
        int n = RaceIndex(charCode) ?? 2;
        string race = RaceNames[Math.Clamp((n - 1) / 2, 0, RaceNames.Length - 1)];
        string gender = n % 2 == 1 ? "Male" : "Female";

        return new
        {
            Type = "Eqdp",
            Manipulation = new
            {
                Entry = hasModel ? 3 << (2 * EqdpSlotIndex(slot)) : 0,
                Gender = gender,
                Race = race,
                SetId = setId,
                Slot = slot,
                ShiftedEntry = hasModel ? 3 : 0,
            },
        };
    }

    /// <summary>
    /// Point one body part's EST entry at an extra skeleton, so a pack's <c>j_ex_*</c> bones exist on a host
    /// accessory. REPLACES whatever extra skeleton the worn item had.
    /// </summary>
    /// <param name="estSlot">"Body", "Head", "Hair" or "Face" — Penumbra's own EST slot names.</param>
    internal static object EstManipulation(string charCode, string estSlot, int setId, int entry)
    {
        int n = RaceIndex(charCode) ?? 2;
        return new
        {
            Type = "Est",
            Manipulation = new
            {
                Gender = n % 2 == 1 ? "Male" : "Female",
                Race = RaceNames[Math.Clamp((n - 1) / 2, 0, RaceNames.Length - 1)],
                SetId = setId,
                Slot = estSlot,
                Entry = entry,
            },
        };
    }

    /// <summary>
    /// The set id whose EST entry governs <paramref name="estSlot"/> for this character, or null. Equipment falls back
    /// to the bare-body walk (set 0 has no equipped entry); hair and face come from the human-part folder id.
    /// </summary>
    internal static int? EstSetId(
        string estSlot,
        IReadOnlyDictionary<string, string>? equipped,
        IReadOnlyDictionary<string, string>? bare,
        IReadOnlyList<string>? humanParts)
    {
        if (EstPartKey(estSlot) is not { } key) return null;

        if (key is "hair" or "face")
        {
            var folder = $"/obj/{key}/";
            foreach (var p in humanParts ?? [])
            {
                int at = p.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var rest = p[(at + folder.Length)..];
                int end = rest.IndexOf('/');
                if (end <= 1) continue;
                if (int.TryParse(rest[1..end], out var id)) return id;   // skip the h/f kind letter
            }
            return null;
        }

        foreach (var map in new[] { equipped, bare })
            if (map != null && map.TryGetValue(key, out var path)
             && ContentSlot.Parse(path) is { } parsed)
                return ContentSlot.SetIdOf(parsed.SetTag);
        return null;
    }

    /// <summary>
    /// The equipment-walk model key a Penumbra EST slot name is about; null rather than a guess for an unknown name.
    /// </summary>
    internal static string? EstPartKey(string estSlot) => estSlot.ToLowerInvariant() switch
    {
        "body" => "top",
        "head" => "met",
        "hair" => "hair",
        "face" => "face",
        _      => null,
    };
}
