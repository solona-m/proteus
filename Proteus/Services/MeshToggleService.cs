using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// What Proteus wrote into a mod when it added switches, so the edit can be undone.
/// <para/>
/// Kept as <c>Proteus/parts.json</c> INSIDE the mod rather than in Proteus's own configuration, so it
/// travels with the folder: the backup it names lives there too, and a record that could be separated from
/// its backups would eventually describe files it could no longer restore. It sits inertly beside a Proteus
/// sidecar — discovery matches <c>metadata.json</c> by exact name, so this never makes a stranger's mod look
/// like an overlay pack.
/// </summary>
internal sealed class MeshToggleRecord
{
    [JsonPropertyName("GroupName")] public string GroupName { get; set; } = string.Empty;

    /// <summary>Model files that were edited, relative to the mod root.</summary>
    [JsonPropertyName("Files")] public List<string> Files { get; set; } = [];

    /// <summary>Switch name → the IMC attribute letter it owns.</summary>
    [JsonPropertyName("Toggles")] public Dictionary<string, string> Toggles { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Writes the switches: edits the model, adds the IMC group over it, and remembers enough to undo both.
/// <para/>
/// The whole operation is arranged so the mod is never left half-edited. Every model is patched IN MEMORY
/// first and only written once they have all succeeded, because the failure that matters is the third file
/// of four throwing after two are already on disk — the mod would then have geometry tagged with an
/// attribute no IMC group drives, which renders as parts silently missing.
/// </summary>
internal sealed class MeshToggleService
{
    public const string RecordFile = "parts.json";
    public const string BackupSubdir = "parts-backup";

    /// <summary>The Penumbra group Proteus writes its switches into. One per mod, by name.</summary>
    public const string DefaultGroupName = "Parts";

    /// <summary>One switch the user asked for: a name, and the parts it covers.</summary>
    public sealed record Plan(string Name, IReadOnlyList<ModelPart> Parts);

    public sealed record Outcome(bool Ok, string Message, int FilesPatched, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Add <paramref name="toggles"/> to <paramref name="model"/>'s item.
    /// </summary>
    /// <param name="siblings">Every redirect the mod publishes. Files serving the SAME game path are patched
    /// too when their geometry matches — a mod with a long and a short version of one garment supplies that
    /// path from two files, and a switch that only reached one of them would look broken on the other.</param>
    /// <param name="readGameFile">Reads a path from the game's own data, for the vanilla IMC entry.</param>
    public static Outcome Write(
        string modRoot,
        PenumbraModMeta.Redirect model,
        ModelParts parts,
        IReadOnlyList<Plan> toggles,
        IReadOnlyList<PenumbraModMeta.Redirect> siblings,
        Func<string, byte[]?> readGameFile)
    {
        if (toggles.Count == 0)
            return new Outcome(false, Loc.Localize("Parts.Write.Nothing", "No switches to write."), 0, []);

        if (ContentSlot.Parse(model.GamePath) is not { } slot
            || ContentSlot.SetIdOf(slot.SetTag) is not { } setId)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.NotAnItem.Fmt",
                "{0} is not a character model path, so there is no item to attach a switch to."),
                model.GamePath), 0, []);

        var equipSlot = PenumbraEquipSlot(slot.Label);
        if (equipSlot == null || ImcEntrySource.ImcPathFor(model.GamePath) == null)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.NotGear.Fmt",
                "Switches can only be added to equipment and accessories, and this is {0}."),
                slot.Label), 0, []);

        // Letters, not table positions: an IMC bit is named by the trailing letter of the attribute — see
        // SecondSkinService.PartAttributeBit — so the budget is the letters the author left unused.
        var free = ModelPartReader.FreeLetters(parts.AttributeNames);
        if (free.Count < toggles.Count)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.NoRoom.Fmt",
                "This model has {0} switch slot(s) left and {1} were asked for."),
                free.Count, toggles.Count), 0, []);

        var assigned = toggles.Select((t, i) => (Toggle: t, Letter: free[i])).ToList();

        // The item's entry as it stands. The mod's own IMC group wins over the game's file: if the author
        // already edits this entry, that edit is what the game sees, and rebuilding from vanilla would
        // silently revert it.
        int variant = ImcEntrySource.VariantOf(siblings, slot.SetTag);
        var entry = ImcEntrySource.FromMod(modRoot, setId, equipSlot)
                 ?? ImcEntrySource.FromGame(readGameFile, model.GamePath, variant);
        if (entry is not { } baseEntry)
            return new Outcome(false, Loc.Localize("Parts.Write.NoImc",
                "Could not read this item's IMC entry, and guessing it would change which material the item "
              + "loads. Nothing has been written."), 0, []);

        // Which files to patch: this one, plus anything else serving the same game path whose parts line up.
        var targets = siblings
            .Where(r => string.Equals(r.GamePath, model.GamePath, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.File)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!targets.Contains(model.File, StringComparer.OrdinalIgnoreCase)) targets.Add(model.File);

        var patched = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();

        foreach (var rel in targets)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(modRoot, Native(rel))); }
            catch { skipped.Add(rel); continue; }

            // Only a file whose parts are laid out identically can take the same edit — the plan addresses
            // meshes and submeshes by number, and a different model would take the tags on whatever geometry
            // happened to sit at those numbers.
            var theirs = rel.Equals(model.File, StringComparison.OrdinalIgnoreCase)
                ? parts : ModelPartReader.Read(bytes);
            if (theirs == null || !SameShape(parts, theirs)) { skipped.Add(rel); continue; }

            try { patched[rel] = Apply(bytes, assigned); }
            catch (ModelAttributeWriter.ModelEditException ex)
            {
                // Nothing is on disk yet — every model is edited in memory first precisely so a refusal
                // here leaves the mod untouched rather than half-tagged.
                return new Outcome(false, string.Format(Loc.Localize("Parts.Write.EditFailed.Fmt",
                    "{0} could not be edited: {1}. Nothing has been written."), rel, ex.Message), 0, []);
            }
        }

        if (patched.Count == 0)
            return new Outcome(false, Loc.Localize("Parts.Write.NoFiles",
                "None of this item's model files could be edited."), 0, skipped);

        // ── from here on the mod is being changed ───────────────────────────
        try
        {
            var record = ReadRecord(modRoot) ?? new MeshToggleRecord { GroupName = DefaultGroupName };

            foreach (var (rel, bytes) in patched)
            {
                Backup(modRoot, rel);
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), bytes);
                if (!record.Files.Contains(rel, StringComparer.OrdinalIgnoreCase)) record.Files.Add(rel);
            }

            foreach (var (toggle, letter) in assigned) record.Toggles[toggle.Name] = letter.ToString();

            WriteGroup(modRoot, record, baseEntry, setId, variant, equipSlot);
            WriteRecord(modRoot, record);
        }
        catch (Exception ex)
        {
            return new Outcome(false, string.Format(
                Loc.Localize("Parts.Write.Failed.Fmt", "Writing failed: {0}"), ex.Message), 0, skipped);
        }

        return new Outcome(true, "", patched.Count, skipped);
    }

    /// <summary>
    /// Apply every switch to one model: split what has to be split, then tag it.
    /// <para/>
    /// Splits run FIRST and in ascending order, with a running offset per mesh. A split renumbers only the
    /// submeshes AFTER it, so going upwards means every index a split has already handed back stays correct
    /// while the ones still to come shift by a known amount. Going downwards would invalidate what was
    /// already recorded, which is the same class of bug as editing a list while iterating it.
    /// <para/>
    /// Whole-submesh claims are resolved LAST for that same reason. They are named against the model as the
    /// user saw it, and a split at a lower index in the same mesh moves them — so they cannot be recorded up
    /// front and must be renumbered against the splits that actually happened. Getting this wrong tags a
    /// neighbouring submesh instead, which reads in game as the wrong piece disappearing.
    /// </summary>
    private static byte[] Apply(byte[] bytes, List<(Plan Toggle, char Letter)> assigned)
    {
        // (mesh, submesh) → ordinal → which switch claims it. -1 is "nothing claims this triangle".
        var claims = new Dictionary<(int Mesh, int Submesh), Dictionary<int, int>>();
        var whole = new Dictionary<(int Mesh, int Submesh), int>();

        for (int i = 0; i < assigned.Count; i++)
            foreach (var part in assigned[i].Toggle.Parts)
            {
                var key = (part.Mesh, part.Submesh);
                if (part.Island < 0) { whole[key] = i; continue; }
                if (!claims.TryGetValue(key, out var byOrdinal)) claims[key] = byOrdinal = [];
                foreach (var ordinal in part.Ordinals) byOrdinal[ordinal] = i;
            }

        // A switch that takes a whole submesh makes any island claim on it redundant — the submesh is going
        // as one piece, so cutting it up first would only add records.
        foreach (var key in whole.Keys) claims.Remove(key);

        // Which submeshes each switch will end up tagging.
        var byToggle = new Dictionary<int, List<(int Mesh, int Submesh)>>();

        // Records added per mesh, by the ORIGINAL submesh index the split happened at, so a whole-submesh
        // claim can be moved by however many records were inserted below it.
        var inserted = new Dictionary<int, List<(int At, int Added)>>();

        var edited = bytes;
        foreach (var mesh in claims.Keys.Select(k => k.Mesh).Distinct().OrderBy(m => m))
        {
            int shift = 0;
            foreach (var key in claims.Keys.Where(k => k.Mesh == mesh).OrderBy(k => k.Submesh))
            {
                var byOrdinal = claims[key];
                var (next, groups) = ModelAttributeWriter.SplitSubmesh(
                    edited, mesh, key.Submesh + shift, t => byOrdinal.TryGetValue(t, out var g) ? g : -1);
                edited = next;

                foreach (var (group, subs) in groups)
                    if (group >= 0)
                        Claim(byToggle, group).AddRange(subs.Select(s => (mesh, s)));

                // Every record beyond the first is new, so the submeshes after this one moved along by that
                // many places.
                int added = groups.Values.Sum(v => v.Count) - 1;
                if (!inserted.TryGetValue(mesh, out var list)) inserted[mesh] = list = [];
                list.Add((key.Submesh, added));
                shift += added;
            }
        }

        foreach (var ((mesh, submesh), toggle) in whole)
        {
            int at = submesh;
            if (inserted.TryGetValue(mesh, out var list))
                at += list.Where(s => s.At < submesh).Sum(s => s.Added);
            Claim(byToggle, toggle).Add((mesh, at));
        }

        foreach (var (toggle, letter) in assigned.Select((a, i) => (i, a.Letter)))
        {
            if (!byToggle.TryGetValue(toggle, out var list) || list.Count == 0) continue;
            edited = ModelAttributeWriter.AddAttribute(edited, $"atr_tv_{letter}", list);
        }
        return edited;
    }

    private static List<(int Mesh, int Submesh)> Claim(
        Dictionary<int, List<(int Mesh, int Submesh)>> byToggle, int toggle)
    {
        if (!byToggle.TryGetValue(toggle, out var list)) byToggle[toggle] = list = [];
        return list;
    }

    /// <summary>
    /// Whether two models can take the same edit: the same parts, in the same order, at the same sizes.
    /// Compared on what the plan actually addresses — a difference in vertex positions is a different
    /// garment, but not one that would make the tags land anywhere else.
    /// </summary>
    private static bool SameShape(ModelParts a, ModelParts b)
        => a.Parts.Count == b.Parts.Count
        && a.Parts.Zip(b.Parts).All(p =>
               p.First.Mesh == p.Second.Mesh
            && p.First.Submesh == p.Second.Submesh
            && p.First.Island == p.Second.Island
            && p.First.TriangleCount == p.Second.TriangleCount);

    private static void WriteGroup(
        string modRoot, MeshToggleRecord record, ImcEntry baseEntry, int setId, int variant, string equipSlot)
    {
        ushort ours = 0;
        var options = new List<(string Name, ushort Mask)>();
        foreach (var (name, letter) in record.Toggles.OrderBy(t => t.Value, StringComparer.Ordinal))
        {
            ushort bit = (ushort)(1 << (letter[0] - 'a'));
            ours |= bit;
            options.Add((name, bit));
        }

        // Our bits are CLEARED in the default entry and set by the options, so a switch means the same thing
        // however Penumbra combines the two — see PenumbraModMeta.WriteImcGroup. Clearing a bit no attribute
        // name uses changes nothing about the item.
        var entry = baseEntry with { AttributeMask = (ushort)(baseEntry.AttributeMask & ~ours & 0x3FF) };

        // Every option ticked, so adding switches to a mod changes nothing until one is unticked.
        ulong allOn = options.Count >= 64 ? ulong.MaxValue : (1UL << options.Count) - 1;

        var identifier = new PenumbraModMeta.ImcIdentifier(
            equipSlot is "Ears" or "Neck" or "Wrists" or "RFinger" or "LFinger" ? "Accessory" : "Equipment",
            setId, variant, equipSlot);

        // Last in the group list: it gates geometry the mod's own groups supply, and Penumbra applies groups
        // in order, so a switch that came first could be overruled by a later option changing the model.
        var count = PenumbraModMeta.TryReadGroups(modRoot)?.Count ?? int.MaxValue;
        PenumbraModMeta.WriteImcGroup(modRoot, count, record.GroupName, identifier, entry, options, allOn);
    }

    // ── revert ──────────────────────────────────────────────────────────────

    /// <summary>Put every edited model back and drop the group. Leaves the mod as Proteus found it.</summary>
    public static Outcome Revert(string modRoot)
    {
        var record = ReadRecord(modRoot);
        if (record == null)
            return new Outcome(false, Loc.Localize("Parts.Revert.Nothing",
                "This mod has no Proteus switches to remove."), 0, []);

        var skipped = new List<string>();
        int restored = 0;
        foreach (var rel in record.Files)
        {
            var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
            if (!File.Exists(backup)) { skipped.Add(rel); continue; }
            try
            {
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), File.ReadAllBytes(backup));
                File.Delete(backup);
                restored++;
            }
            catch { skipped.Add(rel); }
        }

        PenumbraModMeta.DeleteGroup(modRoot, record.GroupName);
        try { File.Delete(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile)); } catch { }

        return new Outcome(true, "", restored, skipped);
    }

    // ── the record ──────────────────────────────────────────────────────────

    public static MeshToggleRecord? ReadRecord(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<MeshToggleRecord>(File.ReadAllText(path))
                : null;
        }
        catch { return null; }
    }

    private static void WriteRecord(string modRoot, MeshToggleRecord record)
    {
        var dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        PenumbraModMeta.AtomicWrite(Path.Combine(dir, RecordFile),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Copy a model aside before its first edit, and only then — a second pass must not overwrite the
    /// pristine copy with an already-tagged one, or revert would restore the previous edit instead of the
    /// author's file.
    /// </summary>
    private static void Backup(string modRoot, string rel)
    {
        var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
        if (File.Exists(backup)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(Path.Combine(modRoot, Native(rel)), backup);
    }

    private static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Penumbra's own <c>EquipSlot</c> spelling for one of <see cref="ContentSlot"/>'s labels.</summary>
    private static string? PenumbraEquipSlot(string label) => label switch
    {
        "Head" => "Head",
        "Body" => "Body",
        "Hands" => "Hands",
        "Legs" => "Legs",
        "Feet" => "Feet",
        "Earrings" => "Ears",
        "Necklace" => "Neck",
        "Bracelets" => "Wrists",
        "Right ring" => "RFinger",
        "Left ring" => "LFinger",
        _ => null,
    };
}
