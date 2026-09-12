using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Proteus.Services;

/// <summary>
/// One item's IMC entry — the six fields the game reads to decide which material variant, decal, VFX and
/// sound an item uses, and which of its ten attribute bits are on.
/// </summary>
/// <remarks>
/// Every field but <see cref="AttributeMask"/> is carried purely so it can be written back UNCHANGED. A
/// Penumbra IMC group replaces the whole entry, so inventing a <see cref="MaterialId"/> would silently point
/// the item at a different material variant folder — the textures would change, or vanish, on a mod the user
/// only asked to add a switch to.
/// </remarks>
internal readonly record struct ImcEntry(
    byte MaterialId, byte DecalId, ushort AttributeMask, byte SoundId, byte VfxId, byte MaterialAnimationId);

/// <summary>
/// Finds the IMC entry an item is currently using, so a switch can be added to it without changing anything
/// else about it.
/// <para/>
/// Two sources, in order, and the order is the point. If the mod ALREADY carries an IMC group for this item
/// then that group's entry is what the game sees, and it is the only correct base — falling through to the
/// game's own file would quietly undo whatever the author changed. Only when there is no such group does the
/// vanilla entry apply.
/// </summary>
internal static class ImcEntrySource
{
    /// <summary>
    /// The entry the mod itself declares for this item, or null if it declares none.
    /// <para/>
    /// Read off the group Penumbra would actually APPLY — see <see cref="AppliedGroupFor"/> — which is not
    /// the same thing as the first one found. A mod carrying two IMC groups for one item has only one of
    /// them in effect, and rebuilding from the loser's entry would state a mask the game never sees.
    /// </summary>
    public static ImcEntry? FromMod(string modRoot, int setId, string equipSlot)
        => AppliedGroupFor(modRoot, setId, equipSlot, null) is { } g ? EntryOf(g.Group) : null;

    /// <summary>One group's <c>DefaultEntry</c>, or null when it declares none.</summary>
    public static ImcEntry? EntryOf(JsonElement group)
    {
        if (!group.TryGetProperty("DefaultEntry", out var e) || e.ValueKind != JsonValueKind.Object)
            return null;

        return new ImcEntry(
            (byte)(Int(e, "MaterialId") ?? 1),
            (byte)(Int(e, "DecalId") ?? 0),
            (ushort)((Int(e, "AttributeMask") ?? 0) & 0x3FF),
            (byte)(Int(e, "SoundId") ?? 0),
            (byte)(Int(e, "VfxId") ?? 0),
            (byte)(Int(e, "MaterialAnimationId") ?? 0));
    }

    /// <summary>
    /// Whether this group edits this item.
    /// <para/>
    /// Matched on set AND slot: a mod can carry several IMC groups on one set that differ only by slot, and
    /// taking the first would hand a pair of shoes the dress's entry. <c>Variant</c> is deliberately NOT
    /// compared — the groups Proteus writes are <c>AllVariants</c>, so they collide with an author's group
    /// on this set and slot whatever variant it names.
    /// </summary>
    private static bool Matches(JsonElement group, int setId, string equipSlot)
    {
        if (!group.TryGetProperty("Identifier", out var id) || id.ValueKind != JsonValueKind.Object)
            return false;
        if (Int(id, "PrimaryId") != setId) return false;
        return !id.TryGetProperty("EquipSlot", out var es) || es.GetString() is not { } slot
            || string.Equals(slot, equipSlot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The mod's own IMC group for this item that Penumbra would actually apply, or null when it has none.
    /// <para/>
    /// Only one group per identifier survives: Penumbra collects manipulations with
    /// <c>Groups.Index().Reverse().OrderByDescending(Priority)</c> and each calls
    /// <c>MetaDictionary.TryAdd</c>, so the FIRST group reached wins and the rest are discarded outright.
    /// That ordering is reproduced here — highest priority, and a later array position breaking a tie,
    /// because <c>OrderByDescending</c> is stable and the reversal therefore decides equal priorities.
    /// <para/>
    /// Which matters because the losers are already dead. Merging switches into one of them would put them
    /// in a group the game never reads, and they would be listed in the mod's settings doing nothing.
    /// </summary>
    /// <param name="exceptGroup">A group to ignore by name — the caller's own, so a second write does not
    /// find the group it wrote last time and treat it as the author's.</param>
    public static PenumbraModMeta.GroupRef? AppliedGroupFor(
        string modRoot, int setId, string equipSlot, string? exceptGroup)
    {
        PenumbraModMeta.GroupRef? best = null;
        int bestPriority = 0;
        foreach (var g in ImcGroups(modRoot))
        {
            if (exceptGroup is { Length: > 0 }
                && string.Equals(g.Name, exceptGroup, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Matches(g.Group, setId, equipSlot)) continue;

            int priority = Int(g.Group, "Priority") ?? 0;
            if (best is { } b && (priority < bestPriority || (priority == bestPriority && g.Index < b.Index)))
                continue;
            best = g;
            bestPriority = priority;
        }
        return best;
    }

    /// <summary>The <c>Imc</c> group of this name, or null. What a revert has to edit its options back out of.</summary>
    public static PenumbraModMeta.GroupRef? GroupNamed(string modRoot, string name)
    {
        foreach (var g in ImcGroups(modRoot))
            if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }

    /// <summary>
    /// Every attribute bit any of this group's options sets.
    /// <para/>
    /// Used to prefer a letter the author's own options leave alone: an option whose mask happens to carry
    /// the bit a new switch is given would force that geometry on whenever it is selected.
    /// </summary>
    public static ushort BitsUsedByOptions(JsonElement group)
    {
        ushort bits = 0;
        if (group.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            foreach (var o in opts.EnumerateArray())
                bits |= (ushort)((Int(o, "AttributeMask") ?? 0) & 0x3FF);
        return bits;
    }

    /// <summary>
    /// The item one named <c>Imc</c> group edits, or null when the mod has no such group. Used to recover
    /// the identity of a Proteus record written before it stored one — see
    /// <c>MeshToggleService.BackfillIdentity</c>.
    /// </summary>
    public static (int SetId, string Slot)? IdentityOfGroup(string modRoot, string groupName)
    {
        if (GroupNamed(modRoot, groupName) is not { } g) return null;
        if (!g.Group.TryGetProperty("Identifier", out var id) || id.ValueKind != JsonValueKind.Object)
            return null;
        if (Int(id, "PrimaryId") is not { } setId) return null;
        var slot = id.TryGetProperty("EquipSlot", out var es) ? es.GetString() : null;
        return slot is { Length: > 0 } ? (setId, slot) : null;
    }

    /// <summary>
    /// The highest <c>Priority</c> among the mod's OTHER <c>Imc</c> groups that edit this same item, or -1
    /// when there are none.
    /// <para/>
    /// Needed because two groups editing one identifier are not merged — Penumbra keeps the first it reaches
    /// (<c>MetaDictionary.TryAdd</c>) and discards the rest, and the order is descending priority. A group
    /// sitting below an author's own IMC edit for the same item is therefore not merely overruled, it is
    /// never applied at all: its switches would appear in the mod's settings and do nothing.
    /// <para/>
    /// This is now the FALLBACK, not the usual answer. <c>MeshToggleService</c> prefers to merge its
    /// switches into the group the author already has (see <see cref="AppliedGroupFor"/>), and only writes
    /// a competing group — which this prices — when there is nothing to merge into.
    /// </summary>
    public static int MaxPriorityFor(string modRoot, int setId, string slot, string exceptGroup)
    {
        int max = -1;
        foreach (var g in ImcGroups(modRoot))
        {
            if (string.Equals(g.Name, exceptGroup, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Matches(g.Group, setId, slot)) continue;
            max = Math.Max(max, Int(g.Group, "Priority") ?? 0);
        }
        return max;
    }

    /// <summary>
    /// Every <c>Imc</c> group in the mod, each with its position in the WHOLE <c>Groups</c> array.
    /// <para/>
    /// The index is the array position, not the position among Imc groups: it is what a rewrite passes back
    /// to put a group where it was, and it is what breaks a priority tie in <see cref="AppliedGroupFor"/>.
    /// Counting only the filtered ones would give both the wrong answer.
    /// <para/>
    /// v4 only. The pre-v4 <c>group_*.json</c> layout is not read here because nothing that consumes this
    /// can act on it — every caller is on its way to a write, and <c>PenumbraModMeta</c> refuses a legacy
    /// folder outright. A group with no <c>Name</c> is skipped for the same reason: a rewrite addresses it
    /// by name, so one without a name cannot be put back.
    /// </summary>
    internal static List<PenumbraModMeta.GroupRef> ImcGroups(string modRoot)
    {
        var groups = new List<PenumbraModMeta.GroupRef>();
        try
        {
            var meta = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
            if (!File.Exists(meta)) return groups;

            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            if (!doc.RootElement.TryGetProperty("Groups", out var gs) || gs.ValueKind != JsonValueKind.Array)
                return groups;

            int index = 0;
            foreach (var g in gs.EnumerateArray())
            {
                int at = index++;
                if (!IsImc(g)) continue;
                if (!g.TryGetProperty("Name", out var n) || n.GetString() is not { Length: > 0 } name)
                    continue;
                // Clone: the JsonDocument is disposed when this method returns.
                groups.Add(new PenumbraModMeta.GroupRef(name, at, g.Clone()));
            }
        }
        catch { /* unreadable — the caller falls through to the game's own entry */ }
        return groups;

        static bool IsImc(JsonElement g)
            => g.ValueKind == JsonValueKind.Object
            && g.TryGetProperty("Type", out var t)
            && string.Equals(t.GetString(), "Imc", StringComparison.OrdinalIgnoreCase);
    }

    private static int? Int(JsonElement o, string key)
        => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : null;

    // ── the game's own file ─────────────────────────────────────────────────

    /// <summary>
    /// A set with one entry per equipment or accessory slot — the layout every gear <c>.imc</c> uses.
    /// <para/>
    /// The second <c>u16</c> of the header is a TYPE, not a mask: 31 for these five-slot files and 1 for the
    /// single-slot ones weapons and monsters use. It was read here as a bitmask for a while and gave the
    /// right answers by luck, 31 being <c>0b11111</c> — its population count is five and the offsets of its
    /// low bits are 0 to 4. That coincidence does not survive contact with any other type, so it is read as
    /// what it is.
    /// </summary>
    private const ushort SetType = 31;

    private const int SetSlots = 5;

    /// <summary>Bytes per entry: material id, decal id, attribute+sound, vfx id, material animation id.</summary>
    private const int EntrySize = 6;

    /// <summary>
    /// The vanilla entry for <paramref name="modelGamePath"/>'s item.
    /// <para/>
    /// The file is a header — <c>u16 variantCount</c>, <c>u16 type</c> — followed by the DEFAULT set and then
    /// one set per variant, each set carrying one entry per slot. Variant ids are 1-based and the default
    /// sits before them, so variant <c>n</c> is set <c>n</c> counting the default as zero.
    /// </summary>
    /// <param name="readGameFile">Reads a game path out of the game's own data, or null if it is not there.</param>
    /// <param name="variant">The item variant, 1-based as the game numbers them.</param>
    public static ImcEntry? FromGame(Func<string, byte[]?> readGameFile, string modelGamePath, int variant)
    {
        if (ImcPathFor(modelGamePath) is not { } path) return null;
        if (ContentSlot.Parse(modelGamePath) is not { } parsed) return null;

        var bytes = readGameFile(path);
        if (bytes == null || bytes.Length < 4) return null;

        ushort variantCount = BitConverter.ToUInt16(bytes, 0);
        // Only the five-slot layout. ImcPathFor already admits nothing else, so anything different here is a
        // file we do not understand rather than a slot we chose not to support.
        if (BitConverter.ToUInt16(bytes, 2) != SetType) return null;

        int part = SlotIndexOf(parsed.Label);
        if (part < 0) return null;

        // Anything outside the declared variants falls back to the default set, which sits at index 0.
        int set = variant >= 1 && variant <= variantCount ? variant : 0;
        int at = 4 + (set * SetSlots + part) * EntrySize;
        if (at + EntrySize > bytes.Length) return null;

        ushort attrSound = BitConverter.ToUInt16(bytes, at + 2);
        return new ImcEntry(
            bytes[at], bytes[at + 1], (ushort)(attrSound & 0x3FF), (byte)(attrSound >> 10),
            bytes[at + 4], bytes[at + 5]);
    }

    /// <summary>
    /// Where this slot's entry sits within a set. The two families share the five positions — equipment runs
    /// met, top, glv, dwn, sho and accessories ear, nek, wrs, rir, ril — which is the same ordering
    /// xivModdingFramework's <c>SlotOffsetDictionary</c> uses.
    /// </summary>
    private static int SlotIndexOf(string label) => label switch
    {
        "Head" or "Earrings" => 0,
        "Body" or "Necklace" => 1,
        "Hands" or "Bracelets" => 2,
        "Legs" or "Right ring" => 3,
        "Feet" or "Left ring" => 4,
        _ => -1,
    };

    /// <summary>
    /// The <c>.imc</c> beside a model, or null for a path whose layout this does not know.
    /// <para/>
    /// Equipment and accessories only. Hair, faces and the rest keep theirs under the human-object tree with
    /// a naming Proteus has not verified, and writing an IMC edit against a guessed path would produce a
    /// manipulation aimed at the wrong item — worse than declining.
    /// </summary>
    public static string? ImcPathFor(string modelGamePath)
    {
        var p = modelGamePath.Replace('\\', '/');
        foreach (var kind in new[] { "equipment", "accessory" })
        {
            var prefix = $"chara/{kind}/";
            if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = p[prefix.Length..];
            int slash = rest.IndexOf('/');
            if (slash <= 0) return null;
            var set = rest[..slash];                     // "e0328" / "a0053"
            return $"{prefix}{set}/{set}.imc";
        }
        return null;
    }

    /// <summary>
    /// The item variant the mod is dressing, read off the material paths it publishes
    /// (<c>.../material/v0003/...</c>).
    /// <para/>
    /// Needed because the variant selects which entry of the IMC file applies, and a model path does not
    /// carry one. Defaults to 1, which is what an item has unless it was made for a specific dye or tier.
    /// </summary>
    public static int VariantOf(IEnumerable<PenumbraModMeta.Redirect> redirects, string setTag)
    {
        foreach (var r in redirects)
        {
            var p = r.GamePath.Replace('\\', '/');
            if (!p.Contains($"/{setTag}/", StringComparison.OrdinalIgnoreCase)) continue;
            int at = p.IndexOf("/material/v", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var digits = p[(at + "/material/v".Length)..];
            int end = digits.IndexOf('/');
            if (end > 0 && int.TryParse(digits[..end], out var v) && v > 0) return v;
        }
        return 1;
    }
}
