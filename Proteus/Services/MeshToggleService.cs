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
    /// <summary>
    /// One entry per ITEM, not per mod. An IMC group edits one set and slot, so a mod carrying a top and a
    /// pair of trousers needs two groups and two independent letter budgets — folding them together made
    /// the second garment's write overwrite the first's group and hand both switches the same bit.
    /// </summary>
    [JsonPropertyName("Items")] public List<MeshToggleItem> Items { get; set; } = [];

    // ── the shape this file had before it was per-item ──
    // Read so a mod edited by an older build can still be undone; never written. MigrateLegacy folds them
    // into Items on load, which is enough for Revert — that only needs the group name and the file list.
    [JsonPropertyName("GroupName")] public string? GroupName { get; set; }
    [JsonPropertyName("Files")] public List<string>? Files { get; set; }
    [JsonPropertyName("Toggles")] public Dictionary<string, string>? Toggles { get; set; }

    public void MigrateLegacy()
    {
        if (Items.Count > 0 || GroupName is not { Length: > 0 }) return;
        Items.Add(new MeshToggleItem
        {
            GroupName = GroupName,
            Files = Files ?? [],
            Toggles = Toggles ?? new Dictionary<string, string>(StringComparer.Ordinal),
        });
        GroupName = null;
        Files = null;
        Toggles = null;
    }

    /// <summary>The entry for one item, or null. Matched on set AND slot, as an IMC identity is.</summary>
    public MeshToggleItem? Find(int setId, string slot)
        => Items.FirstOrDefault(i => i.SetId == setId
            && string.Equals(i.Slot, slot, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The switches Proteus added to one item, and the files it changed to do it.</summary>
internal sealed class MeshToggleItem
{
    /// <summary>The Penumbra group these live in. Unique per item within the mod.</summary>
    [JsonPropertyName("GroupName")] public string GroupName { get; set; } = string.Empty;

    /// <summary>Equipment set id, e.g. 488 for <c>e0488</c>. -1 for a legacy record that predates this.</summary>
    [JsonPropertyName("SetId")] public int SetId { get; set; } = -1;

    /// <summary>Penumbra's <c>EquipSlot</c> spelling — "Legs", "Body", "RFinger".</summary>
    [JsonPropertyName("Slot")] public string Slot { get; set; } = string.Empty;

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

    /// <summary>
    /// The stem of the Penumbra group Proteus writes its switches into.
    /// <para/>
    /// The group is named per ITEM — "Toggles (Legs)" — because an IMC group edits one set and slot, so a
    /// mod with two garments needs two. Only ever used for an item Proteus has not edited before: an
    /// existing <see cref="MeshToggleItem"/> carries the name its group was actually written under, so
    /// something edited under an older naming keeps its own group rather than growing a second beside it.
    /// </summary>
    public const string DefaultGroupName = "Toggles";

    /// <summary>The group name for one item. See <see cref="DefaultGroupName"/>.</summary>
    public static string GroupNameFor(string slotLabel) => $"{DefaultGroupName} ({slotLabel})";

    /// <summary>One switch the user asked for: a name, and the parts it covers.</summary>
    public sealed record Plan(string Name, IReadOnlyList<ModelPart> Parts);

    /// <param name="GroupName">The Penumbra group the switches actually landed in, which is the record's own
    /// name rather than <see cref="DefaultGroupName"/> for a mod edited before that constant changed. Empty
    /// when nothing was written.</param>
    public sealed record Outcome(
        bool Ok, string Message, int FilesPatched, IReadOnlyList<string> Skipped, string GroupName = "");

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
        if (equipSlot == null
            || AttributeSlotLetter(slot.Label) is not { } slotLetter
            || ImcEntrySource.ImcPathFor(model.GamePath) == null)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.NotGear.Fmt",
                "Switches can only be added to equipment and accessories, and this is {0}."),
                slot.Label), 0, []);

        var record = ReadRecord(modRoot) ?? new MeshToggleRecord();
        var item = record.Find(setId, equipSlot);

        // A repeated name would overwrite the letter the first switch is remembered by, orphaning the
        // attribute it tagged: nothing would clear that bit any more, so its geometry would be stuck on
        // with no control over it at all.
        if (toggles.Select(t => t.Name).Concat(item?.Toggles.Keys ?? Enumerable.Empty<string>())
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.DuplicateName.Fmt",
                "This item already has a switch called \"{0}\". Give the new one a different name."),
                dup.Key), 0, []);

        // Two switches cannot both claim one submesh, because one of them takes it whole and the other only
        // part of it — and a submesh cannot be cut for the second while going as one piece for the first.
        if (OverlappingClaim(toggles) is { } clash)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.Overlap.Fmt",
                "\"{0}\" and \"{1}\" both claim part {2}. One of them takes the whole part, so they cannot "
              + "be separate switches."), clash.A, clash.B, clash.Part), 0, []);

        // Letters, not table positions: an IMC bit is named by the trailing letter of the attribute — see
        // SecondSkinService.PartAttributeBit — so the budget is the letters the author left unused.
        //
        // Two sources, and the second is not redundant. This model's own table normally carries every letter
        // an earlier write claimed, so reading it is usually enough — but only for the files that write
        // actually reached. A mod supplying one game path from two models whose triangle order differs has
        // the second SKIPPED by SameShape, and adding a switch while that one is selected would find its
        // table empty and hand out a letter the item is already using. Both options would then carry the
        // same bit: ticking one would flip the other, and ticking both would XOR the bit back to nothing.
        var claimed = item?.Toggles.Values.Where(v => v.Length > 0).Select(v => v[0]).ToHashSet() ?? [];
        var free = ModelPartReader.FreeLetters(parts.AttributeNames).Where(c => !claimed.Contains(c)).ToList();
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

            try { patched[rel] = Apply(bytes, assigned, slotLetter); }
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
            if (item == null)
            {
                item = new MeshToggleItem
                {
                    GroupName = GroupNameFor(slot.Label),
                    SetId = setId,
                    Slot = equipSlot,
                };
                record.Items.Add(item);
            }

            foreach (var (rel, bytes) in patched)
            {
                Backup(modRoot, rel);
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), bytes);
                if (!item.Files.Contains(rel, StringComparer.OrdinalIgnoreCase)) item.Files.Add(rel);
            }

            foreach (var (toggle, letter) in assigned) item.Toggles[toggle.Name] = letter.ToString();

            WriteGroup(modRoot, item, baseEntry, variant);
            WriteRecord(modRoot, record);
        }
        catch (Exception ex)
        {
            return new Outcome(false, string.Format(
                Loc.Localize("Parts.Write.Failed.Fmt", "Writing failed: {0}"), ex.Message), 0, skipped);
        }

        return new Outcome(true, "", patched.Count, skipped, item.GroupName);
    }

    /// <summary>
    /// Two switches claiming one submesh, where at least one takes it whole — or null when they are all
    /// disjoint. Checked up front because <see cref="Apply"/> resolves it by dropping the finer claim, which
    /// would write a switch that controls nothing.
    /// </summary>
    private static (string A, string B, string Part)? OverlappingClaim(IReadOnlyList<Plan> toggles)
    {
        for (int i = 0; i < toggles.Count; i++)
        for (int j = i + 1; j < toggles.Count; j++)
        foreach (var a in toggles[i].Parts)
        foreach (var b in toggles[j].Parts)
        {
            if (a.Mesh != b.Mesh || a.Submesh != b.Submesh) continue;
            // Two different islands of one submesh are fine — the split serves both in one pass.
            if (a.Island >= 0 && b.Island >= 0 && a.Island != b.Island) continue;
            return (toggles[i].Name, toggles[j].Name, a.Island < 0 ? a.Label : b.Label);
        }
        return null;
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
    private static byte[] Apply(byte[] bytes, List<(Plan Toggle, char Letter)> assigned, char slot)
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
            edited = ModelAttributeWriter.AddAttribute(edited, $"atr_{slot}v_{letter}", list);
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
    /// Whether two models can take the same edit: the same parts, in the same order, addressing the same
    /// triangles.
    /// <para/>
    /// The ORDINALS are compared, not merely the counts, and that is the whole point of the check. A plan
    /// names triangles by their position in a submesh's index range, so two size variants of a garment that
    /// happen to agree on every count but order their triangles differently would take the tag on the wrong
    /// geometry — the switch would hide a sleeve on one size and a hem on another. Vertex positions are
    /// deliberately NOT compared: two sizes SHOULD differ there, and that difference moves nothing.
    /// <para/>
    /// The attribute masks are compared too, for a reason the reference model never needs: a submesh the
    /// author already tagged is kept out of the picker by <see cref="ModelPart.Toggleable"/>, but a SIBLING
    /// file gets no such filter. Without this, a mod supplying one game path from two files — one of which
    /// already tags a submesh — would have our bit OR'd onto it, leaving a submesh carrying two attributes,
    /// which is exactly the multi-bit case this design refuses to make assumptions about.
    /// </summary>
    private static bool SameShape(ModelParts a, ModelParts b)
        => a.Parts.Count == b.Parts.Count
        && a.Parts.Zip(b.Parts).All(p =>
               p.First.Mesh == p.Second.Mesh
            && p.First.Submesh == p.Second.Submesh
            && p.First.Island == p.Second.Island
            && p.First.AttributeMask == p.Second.AttributeMask
            && p.First.Ordinals.AsSpan().SequenceEqual(p.Second.Ordinals));

    private static void WriteGroup(string modRoot, MeshToggleItem item, ImcEntry baseEntry, int variant)
    {
        ushort ours = 0;
        var options = new List<(string Name, ushort Mask)>();
        foreach (var (name, letter) in item.Toggles.OrderBy(t => t.Value, StringComparer.Ordinal))
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
            item.Slot is "Ears" or "Neck" or "Wrists" or "RFinger" or "LFinger" ? "Accessory" : "Equipment",
            item.SetId, variant, item.Slot);

        // Above any IMC group the mod already has for this same item, because two of them are not merged:
        // Penumbra collects manipulations with `Groups.Index().Reverse().OrderByDescending(Priority)` and
        // each group calls MetaDictionary.TryAdd, so for one identifier the FIRST group reached wins and
        // every later one is discarded outright. Sitting at priority 0 under an author's own IMC edit meant
        // our group was never applied at all — the switches would be listed in the mod's settings and do
        // nothing. Reversal already puts a later group first when priorities tie, so this only has to break
        // the ties it cannot win.
        int priority = ImcEntrySource.MaxPriorityFor(modRoot, item.SetId, item.Slot, item.GroupName) + 1;

        // Last in the group array, which — through that same Reverse — is where a manipulation wants to be.
        PenumbraModMeta.WriteImcGroup(
            modRoot, PenumbraModMeta.GroupCount(modRoot), item.GroupName, identifier, entry, options, allOn,
            priority);
    }

    // ── revert ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Put every edited model back and drop the groups. Leaves the mod as Proteus found it.
    /// <para/>
    /// Ordered so a failure is always retryable. The backups are deleted LAST, after the groups are gone
    /// and the record with them: deleting each backup as its model was restored meant that a throw from the
    /// group removal — a locked <c>meta.json</c> is enough — left the record naming backups that no longer
    /// existed, so a second attempt could only report them as missing.
    /// </summary>
    public static Outcome Revert(string modRoot)
    {
        var record = ReadRecord(modRoot);
        if (record == null || record.Items.Count == 0)
            return new Outcome(false, Loc.Localize("Parts.Revert.Nothing",
                "This mod has no Proteus switches to remove."), 0, []);

        var skipped = new List<string>();
        var restoredFrom = new List<string>();
        foreach (var rel in record.Items.SelectMany(i => i.Files).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
            if (!File.Exists(backup)) { skipped.Add(rel); continue; }
            try
            {
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), File.ReadAllBytes(backup));
                restoredFrom.Add(backup);
            }
            catch { skipped.Add(rel); }
        }

        try
        {
            foreach (var i in record.Items) PenumbraModMeta.DeleteGroup(modRoot, i.GroupName);
        }
        catch (Exception ex)
        {
            // The models are already back, so the mod renders correctly — but its groups still advertise
            // switches that no longer exist. Reported, with every backup still in place to retry from.
            return new Outcome(false, string.Format(
                Loc.Localize("Parts.Revert.Failed.Fmt",
                    "The models were restored, but the option group could not be removed: {0}"),
                ex.Message), restoredFrom.Count, skipped);
        }

        try { File.Delete(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile)); } catch { }
        foreach (var backup in restoredFrom)
            try { File.Delete(backup); } catch { /* harmless leftover */ }

        return new Outcome(true, "", restoredFrom.Count, skipped);
    }

    // ── the record ──────────────────────────────────────────────────────────

    public static MeshToggleRecord? ReadRecord(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<MeshToggleRecord>(File.ReadAllText(path));
            if (record == null) return null;

            // A record written before the file became per-item still has to be undoable, and — the part
            // that matters more — has to be RECOGNISED, so a later write updates its group instead of
            // adding a second one beside it.
            record.MigrateLegacy();
            BackfillIdentity(modRoot, record);
            return record;
        }
        catch { return null; }
    }

    /// <summary>
    /// Give a legacy item back the set and slot it never recorded, by reading them off the group it wrote.
    /// <para/>
    /// Without this the item can never be matched — <see cref="MeshToggleRecord.Find"/> compares set and
    /// slot, and a legacy item has neither — so the next write to that same garment adds a SECOND item and
    /// a second group. Both would then carry the same IMC identifier, and Penumbra keeps only the first it
    /// reaches (<c>manipulations.TryAdd</c>): the newly added switch would appear in the mod's settings,
    /// report success, and do nothing at all.
    /// <para/>
    /// The group itself is the authority here, not a guess from the file list: it is what Penumbra is
    /// actually applying, and it names the identity outright.
    /// </summary>
    private static void BackfillIdentity(string modRoot, MeshToggleRecord record)
    {
        foreach (var item in record.Items)
        {
            if (item.SetId >= 0 && item.Slot.Length > 0) continue;
            if (item.GroupName.Length == 0) continue;
            if (ImcEntrySource.IdentityOfGroup(modRoot, item.GroupName) is not { } id) continue;
            item.SetId = id.SetId;
            item.Slot = id.Slot;
        }
    }

    private static void WriteRecord(string modRoot, MeshToggleRecord record)
    {
        var dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        // Nulls suppressed so the legacy fields — which exist only to be READ from an older file — do not
        // reappear as "GroupName": null beside the real data and invite someone to read them as meaningful.
        PenumbraModMeta.AtomicWrite(Path.Combine(dir, RecordFile),
            JsonSerializer.Serialize(record, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
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

    /// <summary>
    /// The letter an attribute name carries for this slot: <c>atr_<b>t</b>v_a</c> on a top,
    /// <c>atr_<b>d</b>v_a</c> on legs.
    /// <para/>
    /// This is not decoration, it is how the game finds the attribute at all. It matches an IMC bit to a
    /// model attribute BY NAME — <c>atr_</c>, this letter, <c>v_</c>, then <c>'a' + bit</c> — so an
    /// attribute carrying the wrong slot letter is simply never looked at. The tag sits on the geometry, the
    /// IMC group flips its bit, and nothing happens.
    /// <para/>
    /// The letter is the first of the slot's own path suffix (met, top, glv, dwn, sho, ear, nek, wrs, rir,
    /// ril), which is the rule Penumbra's own accessory workaround uses (<c>ShapeAttributeManager
    /// .AccessoryByte</c>), and which a survey of 4,000 installed models bears out: tops carry
    /// <c>atr_tv_*</c>, legs <c>atr_dv_*</c>, hands <c>atr_gv_*</c>, feet <c>atr_sv_*</c>, heads
    /// <c>atr_mv_*</c>.
    /// </summary>
    private static char? AttributeSlotLetter(string label) => label switch
    {
        "Head" => 'm',
        "Body" => 't',
        "Hands" => 'g',
        "Legs" => 'd',
        "Feet" => 's',
        "Earrings" => 'e',
        "Necklace" => 'n',
        "Bracelets" => 'w',
        "Right ring" or "Left ring" => 'r',
        _ => null,
    };

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
