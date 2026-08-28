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
    /// Matched on set AND slot: a mod can carry several IMC groups on one set that differ only by slot, and
    /// taking the first would hand a pair of shoes the dress's entry.
    /// </summary>
    public static ImcEntry? FromMod(string modRoot, int setId, string equipSlot)
    {
        foreach (var group in ImcGroups(modRoot))
        {
            if (!group.TryGetProperty("Identifier", out var id) || id.ValueKind != JsonValueKind.Object)
                continue;
            if (Int(id, "PrimaryId") != setId) continue;
            if (id.TryGetProperty("EquipSlot", out var es) && es.GetString() is { } slot
                && !string.Equals(slot, equipSlot, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!group.TryGetProperty("DefaultEntry", out var e) || e.ValueKind != JsonValueKind.Object)
                continue;

            return new ImcEntry(
                (byte)(Int(e, "MaterialId") ?? 1),
                (byte)(Int(e, "DecalId") ?? 0),
                (ushort)((Int(e, "AttributeMask") ?? 0) & 0x3FF),
                (byte)(Int(e, "SoundId") ?? 0),
                (byte)(Int(e, "VfxId") ?? 0),
                (byte)(Int(e, "MaterialAnimationId") ?? 0));
        }
        return null;
    }

    /// <summary>
    /// The item one named <c>Imc</c> group edits, or null when the mod has no such group. Used to recover
    /// the identity of a Proteus record written before it stored one — see
    /// <c>MeshToggleService.BackfillIdentity</c>.
    /// </summary>
    public static (int SetId, string Slot)? IdentityOfGroup(string modRoot, string groupName)
    {
        foreach (var group in ImcGroups(modRoot))
        {
            if (!group.TryGetProperty("Name", out var n)
                || !string.Equals(n.GetString(), groupName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!group.TryGetProperty("Identifier", out var id) || id.ValueKind != JsonValueKind.Object)
                continue;
            if (Int(id, "PrimaryId") is not { } setId) continue;
            var slot = id.TryGetProperty("EquipSlot", out var es) ? es.GetString() : null;
            if (slot is not { Length: > 0 }) continue;
            return (setId, slot);
        }
        return null;
    }

    /// <summary>
    /// The highest <c>Priority</c> among the mod's OTHER <c>Imc</c> groups that edit this same item, or -1
    /// when there are none.
    /// <para/>
    /// Needed because two groups editing one identifier are not merged — Penumbra keeps the first it reaches
    /// (<c>MetaDictionary.TryAdd</c>) and discards the rest, and the order is descending priority. A group
    /// sitting below an author's own IMC edit for the same item is therefore not merely overruled, it is
    /// never applied at all: its switches would appear in the mod's settings and do nothing.
    /// </summary>
    public static int MaxPriorityFor(string modRoot, int setId, string slot, string exceptGroup)
    {
        int max = -1;
        foreach (var group in ImcGroups(modRoot))
        {
            if (group.TryGetProperty("Name", out var n)
                && string.Equals(n.GetString(), exceptGroup, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!group.TryGetProperty("Identifier", out var id) || id.ValueKind != JsonValueKind.Object)
                continue;
            if (Int(id, "PrimaryId") != setId) continue;
            if (id.TryGetProperty("EquipSlot", out var es) && es.GetString() is { } s
                && !string.Equals(s, slot, StringComparison.OrdinalIgnoreCase))
                continue;
            max = Math.Max(max, Int(group, "Priority") ?? 0);
        }
        return max;
    }

    /// <summary>Every <c>Imc</c> group in the mod, in either manifest layout.</summary>
    private static IEnumerable<JsonElement> ImcGroups(string modRoot)
    {
        var groups = new List<JsonElement>();
        try
        {
            var meta = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
            if (File.Exists(meta))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                if (doc.RootElement.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                    groups.AddRange(gs.EnumerateArray().Where(IsImc).Select(g => g.Clone()));
            }
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (IsImc(doc.RootElement)) groups.Add(doc.RootElement.Clone());
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
