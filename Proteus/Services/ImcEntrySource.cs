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
    /// The vanilla entry for <paramref name="modelGamePath"/>'s item.
    /// <para/>
    /// The file is a header — <c>u16 variantCount</c>, <c>u16 partMask</c> — followed by the DEFAULT variant's
    /// entries and then one set per variant, each set carrying one 6-byte entry per bit set in the part mask.
    /// So reaching a given (variant, part) is arithmetic over the part mask's population count, not an index.
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
        ushort partMask = BitConverter.ToUInt16(bytes, 2);
        int parts = System.Numerics.BitOperations.PopCount(partMask);
        if (parts == 0) return null;

        // Which part within a set this slot is. Equipment and accessories each list five, in the order their
        // path suffixes appear below; anything else has a single part and takes index 0.
        int part = PartIndexOf(parsed.Label, partMask);
        if (part < 0) return null;

        // Variant 0 (or anything past the end) means the default set, which sits immediately after the
        // header. Real variants follow it.
        int set = variant >= 1 && variant <= variantCount ? variant : 0;
        int at = 4 + (set * parts + part) * 6;
        if (at + 6 > bytes.Length) return null;

        ushort attrSound = BitConverter.ToUInt16(bytes, at + 2);
        return new ImcEntry(
            bytes[at], bytes[at + 1], (ushort)(attrSound & 0x3FF), (byte)(attrSound >> 10),
            bytes[at + 4], bytes[at + 5]);
    }

    /// <summary>
    /// Position of this slot's entry within one variant's set, accounting for the parts the mask says are
    /// absent. -1 when the mask does not carry this slot at all.
    /// </summary>
    private static int PartIndexOf(string label, ushort partMask)
    {
        // The game's own ordering within an equipment or accessory set.
        string[] order = label is "Earrings" or "Necklace" or "Bracelets" or "Right ring" or "Left ring"
            ? ["Earrings", "Necklace", "Bracelets", "Right ring", "Left ring"]
            : ["Head", "Body", "Hands", "Legs", "Feet"];

        int bit = Array.IndexOf(order, label);
        if (bit < 0) return partMask != 0 ? 0 : -1;      // a single-part object (hair, face, a weapon)
        if ((partMask & (1 << bit)) == 0) return -1;     // this item has no piece in that slot

        // Entries are only stored for the parts the mask declares, so count the ones before this.
        return System.Numerics.BitOperations.PopCount((ushort)(partMask & ((1 << bit) - 1)));
    }

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
