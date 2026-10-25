using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CheapLoc;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Item-bound effects in an imported pack: an <c>Imc</c> manipulation that gives the replaced item a <c>VfxId</c>, and
/// the <c>.avfx</c> that id loads. The game spawns it only while that exact item is drawn, so once the import takes the
/// model over it is recorded on the piece (<see cref="ContentPiece.Effect"/>) and the composite moves it onto a carrier.
/// The pack's own copy is taken out of the leftover mod, so the vanilla item looks vanilla again.
/// </summary>
public sealed partial class ContentImportService
{
    /// <summary>
    /// The effect the pack attaches to <paramref name="slot"/>'s item, or null. The unit's own option is asked first,
    /// then the default data, then any option: an Items option that also sets the IMC is the usual shape.
    /// </summary>
    internal static ContentEffect? FindEffect(
        PenumbraPackage.Contents pack, string? group, string? option, ContentSlot.Parsed slot)
    {
        if (ContentSlot.SetIdOf(slot.SetTag) is not { } setId || slot.SetTag.Length < 2) return null;
        char prefix = slot.SetTag[0];
        string? objectType = ObjectTypeOf(prefix);
        if (objectType == null) return null;

        bool Matches(PenumbraPackage.PackImc m)
            => m.VfxId != 0 && m.PrimaryId == setId
            && string.Equals(m.ObjectType, objectType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ContentSlot.LabelForEquipSlot(m.EquipSlot), slot.Label, StringComparison.Ordinal);

        var own = pack.Groups.Where(g => g.Name == group).SelectMany(g => g.Options)
            .Where(o => o.Name == option).SelectMany(o => o.Imc);
        var any = pack.Groups.SelectMany(g => g.Options).SelectMany(o => o.Imc);
        var imc = own.Concat(pack.DefaultImc).Concat(any).FirstOrDefault(Matches);
        if (imc == null) return null;

        var gamePath = PenumbraManipulations.VfxGamePath(prefix, setId, imc.VfxId);
        var effect = new ContentEffect { Variant = imc.Variant, VfxId = imc.VfxId, GamePath = gamePath };

        void Source(string gp, string entry, string? g, string? o)
        {
            if (!string.Equals(PenumbraPackage.Normalize(gp), gamePath, StringComparison.OrdinalIgnoreCase)) return;
            if (effect.Sources.Any(s => s.Group == g && s.Option == o
                                     && string.Equals(s.File, entry, StringComparison.OrdinalIgnoreCase))) return;
            effect.Sources.Add(new ContentMaterialSource { Group = g, Option = o, File = entry });
        }

        // Default first: SelectedMaterialFile ranks it below every selected option, which is Penumbra's rule.
        foreach (var (gp, entry) in pack.DefaultFiles) Source(gp, entry, null, null);
        foreach (var g in pack.Groups)
            foreach (var o in g.Options)
                foreach (var (gp, entry) in o.Files)
                    Source(gp, entry, g.Name, o.Name);

        // No file of its own: the pack switches on one of the game's effects for that item. Left on the item where the pack
        // put it — moving a game effect onto a carrier is not what this carries.
        return effect.Sources.Count > 0 ? effect : null;
    }

    /// <summary>Penumbra's <c>ObjectType</c> for a set tag's kind letter, or null for one that carries no item effect.</summary>
    private static string? ObjectTypeOf(char prefix) => char.ToLowerInvariant(prefix) switch
    {
        'a' => "Accessory",
        'e' => "Equipment",
        _   => null,
    };

    /// <summary>
    /// The warnings an effect earns at import: its file is named but missing from the archive. Not fatal — the
    /// composite reports the effect again when it cannot publish it.
    /// </summary>
    private static IEnumerable<string> EffectWarnings(PenumbraPackage.Contents pack, PieceUnit unit)
    {
        if (unit.Effect is not { } e || !unit.Import) yield break;
        if (e.Sources.All(s => !pack.Entries.ContainsKey(PenumbraPackage.Normalize(s.File))))
            yield return string.Format(ProteusLoc.Localize("ContentImport.Warn.EffectFileMissing.Fmt",
                "\"{0}\" has an animated effect, but its file ({1}) is not in the pack, so it cannot be shown."),
                unit.Label, e.Sources[0].File);
    }

    /// <summary>
    /// Take the imported effects' own copies out of the leftover mod, in either layout. Only for units that import
    /// and carry an effect, so a refused piece keeps working as the pack built it.
    /// <list type="bullet">
    /// <item>The <c>.avfx</c> redirects at each effect's game path.</item>
    /// <item>For an effect-only unit, every redirect of its item (its set folder, its slot's files): the composite reads
    /// them from the pack, and leaving them would put the pack's materials on the vanilla item.</item>
    /// <item>Its <c>Imc</c> edits that set the recorded <c>VfxId</c>: deleted when the rest of the entry is vanilla
    /// (<paramref name="readGameFile"/>), else kept with <c>VfxId 0</c>.</item>
    /// <item>Its <c>Eqdp</c> edits, which only routed every race to the placeholder.</item>
    /// </list>
    /// A v3 folder is edited here, before Penumbra sees it, like <see cref="StripModelRedirects"/>; nothing is authored.
    /// </summary>
    internal static void StripEffects(
        string root, ImportPreview preview, Func<string, byte[]?>? readGameFile, IPluginLog? log)
    {
        var units = preview.Units.Where(u => u.Import && u.Effect != null).ToList();
        if (units.Count == 0) return;

        const string cost = "the vanilla item it replaced keeps the pack's effect and look";
        var pack = preview.Pack;

        bool Strip(string gamePath)
        {
            var gp = PenumbraPackage.Normalize(gamePath);
            foreach (var u in units)
            {
                if (string.Equals(gp, u.Effect!.GamePath, StringComparison.OrdinalIgnoreCase)) return true;
                if (u.EffectOnly && IsItemFile(gp, u)) return true;
            }
            return false;
        }

        void EditOwner(JsonObject owner)
        {
            if (owner["Files"] is JsonObject files)
                foreach (var key in files.Select(p => p.Key).Where(Strip).ToList())
                    files.Remove(key);
            if (owner["Manipulations"] is JsonArray manips)
                EditManipulations(manips, units, readGameFile);
        }

        void EditGroup(JsonObject group)
        {
            foreach (var key in new[] { "Options", "Containers" })
                if (group[key] is JsonArray arr)
                    foreach (var o in arr)
                        if (o is JsonObject oo) EditOwner(oo);
        }

        if (pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log, cost, manifest =>
            {
                if (manifest["DefaultData"] is JsonObject dd) EditOwner(dd);
                if (manifest["Groups"] is JsonArray groups)
                    foreach (var g in groups)
                        if (g is JsonObject go) EditGroup(go);
            });
            return;
        }

        EditJson(Path.Combine(root, PenumbraModMeta.LegacyDefaultMod), log, cost, EditOwner);
        foreach (var group in pack.Groups)
            if (group.Entry != null)
                EditJson(Path.Combine(root, group.Entry.Replace('/', Path.DirectorySeparatorChar)), log, cost, EditGroup);
    }

    /// <summary>
    /// Whether <paramref name="gamePath"/> is one of the unit's item's own files: under its set folder, and named for
    /// its slot (<c>_wrs.</c>, <c>_wrs_</c>). One accessory set holds every slot's files, so the folder alone is too wide.
    /// </summary>
    private static bool IsItemFile(string gamePath, PieceUnit unit)
    {
        if (unit.Slot.SetTag.Length < 2 || ObjectTypeOf(unit.Slot.SetTag[0]) is not { } type) return false;
        var folder = $"chara/{(type == "Accessory" ? "accessory" : "equipment")}/{unit.Slot.SetTag}/";
        if (!gamePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        if (SlotSuffix(unit) is not { } suffix) return false;
        var leaf = Path.GetFileName(gamePath);
        return leaf.Contains($"_{suffix}.", StringComparison.OrdinalIgnoreCase)
            || leaf.Contains($"_{suffix}_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The slot suffix of a unit's model ("wrs" out of <c>c0101a0002_wrs.mdl</c>), or null.</summary>
    private static string? SlotSuffix(PieceUnit unit)
    {
        foreach (var v in unit.Variants)
        {
            var leaf = Path.GetFileNameWithoutExtension(v.GamePath);
            int at = leaf.LastIndexOf('_');
            if (at > 0 && at < leaf.Length - 1) return leaf[(at + 1)..];
        }
        return null;
    }

    private static void EditManipulations(
        JsonArray manips, IReadOnlyList<PieceUnit> units, Func<string, byte[]?>? readGameFile)
    {
        for (int i = manips.Count - 1; i >= 0; i--)
        {
            if (manips[i] is not JsonObject mo || mo["Manipulation"] is not JsonObject inner) continue;
            var type = mo["Type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;

            if (string.Equals(type, "Eqdp", StringComparison.OrdinalIgnoreCase))
            {
                var set = PenumbraPackage.Int(inner, "SetId");
                var slot = inner["Slot"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
                if (set != null && slot != null && units.Any(u => Owns(u, set.Value, slot, null)))
                    manips.RemoveAt(i);
                continue;
            }

            if (!string.Equals(type, "Imc", StringComparison.OrdinalIgnoreCase)
             || inner["Entry"] is not JsonObject entry) continue;
            var primary = PenumbraPackage.Int(inner, "PrimaryId");
            var equipSlot = inner["EquipSlot"] is JsonValue ev && ev.TryGetValue<string>(out var es) ? es : null;
            var objectType = inner["ObjectType"] is JsonValue ov && ov.TryGetValue<string>(out var ot) ? ot : null;
            // Only the effect that was recorded and carried: any other id on the item (a game effect it switches on, a second
            // effect) stays exactly as the pack wrote it.
            var vfx = PenumbraPackage.Int(entry, "VfxId") ?? 0;
            if (primary == null || equipSlot == null || vfx == 0
             || units.FirstOrDefault(u => u.Effect!.VfxId == vfx && Owns(u, primary.Value, equipSlot, objectType))
                    is not { } owner) continue;

            if (VanillaButEffect(owner, PenumbraPackage.Int(inner, "Variant") ?? -1, entry, readGameFile))
                manips.RemoveAt(i);
            else
                entry["VfxId"] = 0;
        }
    }

    /// <summary>Whether a manipulation's set and slot (and kind, when it names one) are the unit's item.</summary>
    private static bool Owns(PieceUnit unit, int setId, string equipSlot, string? objectType)
        => ContentSlot.SetIdOf(unit.Slot.SetTag) == setId
        && string.Equals(ContentSlot.LabelForEquipSlot(equipSlot), unit.Slot.Label, StringComparison.Ordinal)
        && (objectType == null || unit.Slot.SetTag.Length < 2
            || string.Equals(ObjectTypeOf(unit.Slot.SetTag[0]), objectType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the pack's entry equals the game's own for that variant in everything but the effect, so dropping it
    /// leaves the item exactly vanilla. False when the game's entry cannot be read (tests, no game data).
    /// </summary>
    private static bool VanillaButEffect(
        PieceUnit unit, int variant, JsonObject entry, Func<string, byte[]?>? readGameFile)
    {
        if (readGameFile == null || variant < 1) return false;
        var model = unit.Variants.FirstOrDefault()?.GamePath;
        if (model == null || ImcEntrySource.FromGame(readGameFile, model, variant) is not { } vanilla) return false;

        int packed = PenumbraPackage.Int(entry, "AttributeAndSound") ?? 0;
        return (PenumbraPackage.Int(entry, "MaterialId") ?? 0) == vanilla.MaterialId
            && (PenumbraPackage.Int(entry, "DecalId") ?? 0) == vanilla.DecalId
            && (PenumbraPackage.Int(entry, "MaterialAnimationId") ?? 0) == vanilla.MaterialAnimationId
            && ((PenumbraPackage.Int(entry, "AttributeMask") ?? packed) & 0x3FF) == vanilla.AttributeMask
            && (PenumbraPackage.Int(entry, "SoundId") ?? (packed >> 10)) == vanilla.SoundId;
    }
}
