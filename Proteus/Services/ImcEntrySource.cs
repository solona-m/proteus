using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Proteus.Services;

/// <summary>One item's IMC entry: material variant, decal, attribute bits, sound, VFX and material animation.</summary>
/// <remarks>
/// Every field but <see cref="AttributeMask"/> is carried only to be written back UNCHANGED: a Penumbra IMC group
/// replaces the whole entry.
/// </remarks>
internal readonly record struct ImcEntry(
    byte MaterialId, byte DecalId, ushort AttributeMask, byte SoundId, byte VfxId, byte MaterialAnimationId);

/// <summary>
/// Finds the IMC entry an item is currently using, so a switch can be added without changing anything else. The
/// mod's own IMC group comes first; the vanilla entry only when there is none.
/// </summary>
internal static class ImcEntrySource
{
    /// <summary>The entry the mod declares for this item, from the group Penumbra would apply (<see cref="AppliedGroupFor"/>), or null.</summary>
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
    /// Whether this group edits this item: set AND slot. <c>Variant</c> is not compared, since Proteus's groups are
    /// <c>AllVariants</c>.
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
    /// The mod's own IMC group for this item that Penumbra would actually apply, or null. Only one per identifier
    /// survives: highest priority, a later array position breaking a tie (Penumbra's reversed, stable ordering).
    /// </summary>
    /// <param name="exceptGroup">A group to ignore by name: the caller's own.</param>
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

    /// <summary>The <c>Imc</c> group of this name, or null.</summary>
    public static PenumbraModMeta.GroupRef? GroupNamed(string modRoot, string name)
    {
        foreach (var g in ImcGroups(modRoot))
            if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }

    /// <summary>Every attribute bit any of this group's options sets, so a new switch can prefer an unused one.</summary>
    public static ushort BitsUsedByOptions(JsonElement group)
    {
        ushort bits = 0;
        if (group.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            foreach (var o in opts.EnumerateArray())
                bits |= (ushort)((Int(o, "AttributeMask") ?? 0) & 0x3FF);
        return bits;
    }

    /// <summary>The item one named <c>Imc</c> group edits, or null (<c>MeshToggleService.BackfillIdentity</c>).</summary>
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
    /// The highest <c>Priority</c> among the mod's OTHER <c>Imc</c> groups for this item, or -1. A competing group must
    /// outrank them, since Penumbra discards all but one.
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
    /// Every named <c>Imc</c> group in the mod (v4 only), each with its position in the WHOLE <c>Groups</c> array,
    /// which rewrites and priority ties rely on.
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
    /// The header type (second <c>u16</c>, not a mask) of a five-slot gear <c>.imc</c>; weapons and monsters use 1.
    /// </summary>
    private const ushort SetType = 31;

    private const int SetSlots = 5;

    /// <summary>Bytes per entry: material id, decal id, attribute+sound, vfx id, material animation id.</summary>
    private const int EntrySize = 6;

    /// <summary>
    /// The vanilla entry for <paramref name="modelGamePath"/>'s item. Layout: <c>u16 variantCount</c>, <c>u16 type</c>,
    /// the default set, then one set per variant (variant <c>n</c> is set <c>n</c>), each one entry per slot.
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
        // Only the five-slot layout.
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
    /// Where this slot's entry sits within a set: met, top, glv, dwn, sho or ear, nek, wrs, rir, ril (as
    /// xivModdingFramework's <c>SlotOffsetDictionary</c>).
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

    /// <summary>The <c>.imc</c> beside an equipment or accessory model, or null for any other layout rather than a guess.</summary>
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
    /// The item variant the mod is dressing, read off its material paths (<c>.../material/v0003/...</c>); defaults
    /// to 1.
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
