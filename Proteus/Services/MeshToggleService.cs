using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheapLoc;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// What Proteus wrote into a mod when it added switches, so the edit can be undone. Kept as
/// <c>Proteus/parts.json</c> INSIDE the mod so it travels with the backups it names.
/// </summary>
internal sealed class MeshToggleRecord
{
    /// <summary>
    /// One entry per ITEM, not per mod: an IMC group edits one set and slot, so each item has its own group
    /// and letter budget.
    /// </summary>
    [JsonPropertyName("Items")] public List<MeshToggleItem> Items { get; set; } = [];

    // ── the shape this file had before it was per-item ──
    // Read so a mod edited by an older build can still be undone; never written (MigrateLegacy folds them in).
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

    /// <summary>
    /// True when <see cref="GroupName"/> names a group the MOD'S AUTHOR wrote and Proteus merged into; Revert
    /// then removes only our options. Absent means Proteus created the group.
    /// </summary>
    [JsonPropertyName("AdoptedGroup")] public bool? AdoptedGroup { get; set; }

    /// <summary>
    /// The adopted group's <c>DefaultEntry</c> attribute mask as Proteus FIRST found it, so a revert can restore
    /// the bits it cleared. Written once only: a later write would record our bits already cleared.
    /// </summary>
    [JsonPropertyName("AdoptedEntryMask")] public int? AdoptedEntryMask { get; set; }
}

/// <summary>
/// Writes the switches: edits the model, adds the IMC group over it, and remembers enough to undo both. Every
/// model is patched in memory first and only written once all have succeeded, so a mod is never half-tagged.
/// </summary>
internal sealed class MeshToggleService
{
    public const string RecordFile = "parts.json";
    public const string BackupSubdir = "parts-backup";

    /// <summary>
    /// The stem of the per-item Penumbra group name ("Toggles (Legs)"), for new items only; an existing
    /// <see cref="MeshToggleItem"/> keeps the name its group was written under.
    /// </summary>
    public const string DefaultGroupName = "Toggles";

    /// <summary>The group name for one item. See <see cref="DefaultGroupName"/>.</summary>
    public static string GroupNameFor(string slotLabel) => $"{DefaultGroupName} ({slotLabel})";

    /// <summary>One switch the user asked for: a name, and the parts it covers.</summary>
    public sealed record Plan(string Name, IReadOnlyList<ModelPart> Parts);

    /// <param name="GroupName">The Penumbra group the switches actually landed in (the record's own name).
    /// Empty when nothing was written.</param>
    public sealed record Outcome(
        bool Ok, string Message, int FilesPatched, IReadOnlyList<string> Skipped, string GroupName = "");

    /// <summary>
    /// Add <paramref name="toggles"/> to <paramref name="model"/>'s item.
    /// </summary>
    /// <param name="siblings">Every redirect the mod publishes. Files serving the SAME game path are patched
    /// too when their claimed geometry matches.</param>
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

        // Checked here too: PenumbraModMeta throws on a pre-v4 folder, which mid-Apply would land after models
        // were written.
        if (PenumbraModMeta.IsLegacyFolder(modRoot))
            return new Outcome(false, Strings.Parts.LegacyMod, 0, []);

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

        int variant = ImcEntrySource.VariantOf(siblings, slot.SetTag);

        // Merge into a group the author already has for this item rather than writing a competing one; see
        // WriteGroup.
        var adopt = AdoptionTarget(modRoot, item, setId, equipSlot, variant);

        // A repeated name would overwrite the letter the first switch is remembered by, orphaning its attribute.
        if (toggles.Select(t => t.Name).Concat(item?.Toggles.Keys ?? Enumerable.Empty<string>())
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.DuplicateName.Fmt",
                "This item already has a switch called \"{0}\". Give the new one a different name."),
                dup.Key), 0, []);

        // Merging drops every option whose name Proteus owns, so a switch named after an author option would
        // delete it.
        if (adopt is { } target
            && AuthorOptionNames(target.Group, item) is var authorNames
            && toggles.Select(t => t.Name).FirstOrDefault(authorNames.Contains) is { } taken)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.AuthorOptionName.Fmt",
                "This mod's own \"{0}\" group already has an option called \"{1}\". Give the new switch a "
              + "different name."), target.Name, taken), 0, []);

        // Two switches cannot both claim one submesh when one takes it whole.
        if (OverlappingClaim(toggles) is { } clash)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.Overlap.Fmt",
                "\"{0}\" and \"{1}\" both claim part {2}. One of them takes the whole part, so they cannot "
              + "be separate switches."), clash.A, clash.B, clash.Part), 0, []);

        // Letters, not table positions: an IMC bit is named by the attribute's trailing letter (see
        // ContentPieceResolver.PartAttributeBit). The record's letters are excluded as well as the model's table,
        // because a skipped sibling file's table may not carry letters the item already uses.
        var claimed = item?.Toggles.Values.Where(v => v.Length > 0).Select(v => v[0]).ToHashSet() ?? [];
        var free = ModelPartReader.FreeLetters(parts.AttributeNames).Where(c => !claimed.Contains(c)).ToList();

        // Letters the author's own options already set are taken LAST (their masks would force our geometry on).
        // A stable sort: this reorders the budget, never shrinks it.
        if (adopt is { } pref)
        {
            ushort used = ImcEntrySource.BitsUsedByOptions(pref.Group);
            free = free.OrderBy(c => (used >> (c - 'a')) & 1).ToList();
        }

        if (free.Count < toggles.Count)
            return new Outcome(false, string.Format(Loc.Localize("Parts.Write.NoRoom.Fmt",
                "This model has {0} switch slot(s) left and {1} were asked for."),
                free.Count, toggles.Count), 0, []);

        var assigned = toggles.Select((t, i) => (Toggle: t, Letter: free[i])).ToList();

        // The item's entry as it stands: the mod's own IMC group wins over the game's file, and the adopted
        // group's entry is read directly since it is written back into that group.
        var entry = (adopt is { } src ? ImcEntrySource.EntryOf(src.Group) : null)
                 ?? ImcEntrySource.FromMod(modRoot, setId, equipSlot)
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

            // Only a file that agrees about the CLAIMED geometry can take the same edit: plans address by number.
            var theirs = rel.Equals(model.File, StringComparison.OrdinalIgnoreCase)
                ? parts : ModelPartReader.Read(bytes);
            if (theirs == null || !ClaimsLineUp(theirs, toggles)) { skipped.Add(rel); continue; }

            try { patched[rel] = Apply(bytes, assigned, slotLetter); }
            catch (ModelAttributeWriter.ModelEditException ex)
            {
                // Nothing is on disk yet, so a refusal leaves the mod untouched.
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

            WriteGroup(modRoot, item, baseEntry, variant, adopt);
            WriteRecord(modRoot, record);
            SyncContentAttributes(modRoot);
        }
        catch (Exception ex)
        {
            return new Outcome(false, string.Format(
                Loc.Localize("Parts.Write.Failed.Fmt", "Writing failed: {0}"), ex.Message), 0, skipped);
        }

        return new Outcome(true, "", patched.Count, skipped, item.GroupName);
    }

    /// <summary>
    /// The mod's own IMC group these switches should be merged into, or null to write Proteus's own. An
    /// already-adopted group is looked up BY NAME: by identity the exclude-my-own-group rule would hide it.
    /// </summary>
    /// <param name="variant">The variant this item is worn at, for the pinned-variant case below.</param>
    private static PenumbraModMeta.GroupRef? AdoptionTarget(
        string modRoot, MeshToggleItem? item, int setId, string equipSlot, int variant)
    {
        var found = item?.AdoptedGroup is true
            ? ImcEntrySource.GroupNamed(modRoot, item.GroupName)
            : ImcEntrySource.AppliedGroupFor(modRoot, setId, equipSlot, item?.GroupName);

        if (found is not { } g) return null;

        // A group pinned to a variant the item is not worn at is not adopted: our bits there would never be
        // set, hiding the parts. Writing our own group is the lesser harm.
        bool allVariants = !g.Group.TryGetProperty("AllVariants", out var av)
                        || av.ValueKind != JsonValueKind.False;
        if (!allVariants
            && g.Group.TryGetProperty("Identifier", out var id) && id.ValueKind == JsonValueKind.Object
            && id.TryGetProperty("Variant", out var v) && v.TryGetInt32(out var pinned)
            && pinned != variant)
            return null;

        return g;
    }

    /// <summary>
    /// The option names in an adopted group that belong to its AUTHOR: everything Proteus does not own. A new
    /// switch must not reuse one.
    /// </summary>
    private static HashSet<string> AuthorOptionNames(JsonElement group, MeshToggleItem? item)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (group.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            foreach (var o in opts.EnumerateArray())
                if (o.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    names.Add(name);
        if (item != null) names.ExceptWith(item.Toggles.Keys);
        return names;
    }

    /// <summary>
    /// Two switches claiming one submesh, where at least one takes it whole, or null when all are disjoint.
    /// <see cref="Apply"/> would otherwise silently drop the finer claim.
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
    /// Apply every switch to one model: split what has to be split, then tag it. Splits run in ascending order
    /// with a running offset per mesh (a split renumbers only later submeshes); whole-submesh claims are
    /// resolved LAST, renumbered against the splits that happened.
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

        // A switch that takes a whole submesh makes any island claim on it redundant.
        foreach (var key in whole.Keys) claims.Remove(key);

        // Which submeshes each switch will end up tagging.
        var byToggle = new Dictionary<int, List<(int Mesh, int Submesh)>>();

        // Records added per mesh, by the ORIGINAL submesh index, to renumber whole-submesh claims.
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

                // Every record beyond the first is new; later submeshes moved along by that many.
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
    /// Whether <paramref name="sibling"/> can take the same edit: every CLAIMED part is the same geometry there,
    /// with the same ordinals (not just counts), material and attribute mask. Vertex positions are not
    /// compared; sizes of a garment should differ there. Island claims match by island number.
    /// </summary>
    private static bool ClaimsLineUp(ModelParts sibling, IReadOnlyList<Plan> toggles)
    {
        foreach (var claim in toggles.SelectMany(t => t.Parts))
        {
            var theirs = sibling.Parts.FirstOrDefault(
                p => p.Mesh == claim.Mesh && p.Submesh == claim.Submesh && p.Island == claim.Island);

            if (theirs == null
                || theirs.AttributeMask != claim.AttributeMask
                || !string.Equals(theirs.Material, claim.Material, StringComparison.OrdinalIgnoreCase)
                || !theirs.Ordinals.AsSpan().SequenceEqual(claim.Ordinals))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Put the item's switches into a Penumbra IMC group — the author's own where there is one, otherwise a
    /// group of Proteus's own making.
    /// </summary>
    /// <param name="adopt">The author's group to merge into, or null. See <see cref="AdoptionTarget"/>.</param>
    private static void WriteGroup(
        string modRoot, MeshToggleItem item, ImcEntry baseEntry, int variant,
        PenumbraModMeta.GroupRef? adopt)
    {
        ushort ours = 0;
        var options = new List<(string Name, ushort Mask)>();
        foreach (var (name, letter) in item.Toggles.OrderBy(t => t.Value, StringComparer.Ordinal))
        {
            ushort bit = (ushort)(1 << (letter[0] - 'a'));
            ours |= bit;
            options.Add((name, bit));
        }

        // Our bits are CLEARED in the default entry and set by the options; see PenumbraModMeta.WriteImcGroup.
        var entry = baseEntry with { AttributeMask = (ushort)(baseEntry.AttributeMask & ~ours & 0x3FF) };

        // ── the author already has a group for this item: merge into it ──────
        //
        // Penumbra keeps only the FIRST IMC group reached per identifier and discards the rest, so outranking
        // the author would kill their options. Our switches go into their group instead.
        if (adopt is { } target)
        {
            item.AdoptedEntryMask ??= baseEntry.AttributeMask;
            var previous = item.AdoptedGroup is true ? null : item.GroupName;

            PenumbraModMeta.MergeImcGroup(
                modRoot, target, options, item.Toggles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                entry.AttributeMask, baseEntry);

            item.GroupName = target.Name;
            item.AdoptedGroup = true;

            // A group Proteus wrote earlier for this item would keep killing the author's group: remove it.
            if (previous is { Length: > 0 }
                && !string.Equals(previous, target.Name, StringComparison.OrdinalIgnoreCase)
                && ImcEntrySource.GroupNamed(modRoot, previous) != null)
                PenumbraModMeta.DeleteGroup(modRoot, previous);
            return;
        }

        // ── nothing to merge into: our own group ─────────────────────────────

        // Every option ticked, so adding switches to a mod changes nothing until one is unticked.
        ulong allOn = options.Count >= 64 ? ulong.MaxValue : (1UL << options.Count) - 1;

        var identifier = new PenumbraModMeta.ImcIdentifier(
            item.Slot is "Ears" or "Neck" or "Wrists" or "RFinger" or "LFinger" ? "Accessory" : "Equipment",
            item.SetId, variant, item.Slot);

        // Still above any IMC group the mod has for this item (e.g. one pinned to another variant), since
        // Penumbra keeps only the first reached; reversal already wins ties for a later group.
        int priority = ImcEntrySource.MaxPriorityFor(modRoot, item.SetId, item.Slot, item.GroupName) + 1;

        // Last in the group array, which through that same Reverse is where a manipulation wants to be.
        PenumbraModMeta.WriteImcGroup(
            modRoot, PenumbraModMeta.GroupCount(modRoot), item.GroupName, identifier, entry, options, allOn,
            priority);
    }

    // ── revert ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Put every edited model back and drop the groups. Backups are deleted LAST so a failure stays retryable.
    /// A created group is deleted whole; from an adopted one only our options are removed and
    /// <see cref="MeshToggleItem.AdoptedEntryMask"/> is restored. A missing adopted group is not an error.
    /// </summary>
    public static Outcome Revert(string modRoot)
    {
        var record = ReadRecord(modRoot);
        if (record == null || record.Items.Count == 0)
            return new Outcome(false, Loc.Localize("Parts.Revert.Nothing",
                "This mod has no Proteus switches to remove."), 0, []);

        // Refused while a feature that edited the same model LATER holds its own backup; see ModelBackupOrder.
        foreach (var rel in record.Items.SelectMany(i => i.Files).Distinct(StringComparer.OrdinalIgnoreCase))
            if (ModelBackupOrder.LaterFeature(modRoot, BackupSubdir, rel) is { } later)
                return new Outcome(false, ModelBackupOrder.BlockedMessage(later), 0, []);

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
            foreach (var i in record.Items)
            {
                if (i.AdoptedGroup is true && ImcEntrySource.GroupNamed(modRoot, i.GroupName) is { } theirs)
                {
                    // Merging with nothing to add is the removal. False means the group is left empty: delete it.
                    if (!PenumbraModMeta.MergeImcGroup(
                            modRoot, theirs, [], i.Toggles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                            i.AdoptedEntryMask is { } m ? (ushort)(m & 0x3FF) : null, default))
                        PenumbraModMeta.DeleteGroup(modRoot, i.GroupName);
                    continue;
                }
                PenumbraModMeta.DeleteGroup(modRoot, i.GroupName);
            }
            SyncContentAttributes(modRoot);
        }
        catch (Exception ex)
        {
            // The models are back but the groups remain; every backup is still in place to retry from.
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

    // ── imported mods ───────────────────────────────────────────────────────

    /// <summary>
    /// Bring an imported mod's sidecar <c>ContentAttributes</c> back in line with the IMC groups its manifest now
    /// has. An imported piece is appended onto a host accessory, so the game never applies this item's IMC mask to
    /// it; the composite hides parts itself from that list (<see cref="ContentPieceResolver.HiddenAttributes"/>),
    /// which the import wrote once. Rebuilt from the manifest rather than patched, so an adopted group, its changed
    /// default mask and a revert all come out right. A mod with no imported pieces is left alone.
    /// </summary>
    internal static void SyncContentAttributes(string modRoot)
    {
        if (SidecarDiscoveryService.TryReadMetadata(modRoot) is not { } meta
         || (meta.Content is not { Count: > 0 } && meta.ContentGroups is not { Count: > 0 }))
            return;

        var groups = ContentImportService.AttributeGroups(PenumbraPackage.Read(modRoot));

        // Edited in place, so every field this build does not know about survives.
        var path = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile);
        if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject root)
            throw new InvalidDataException($"{path} is not a JSON object.");

        // Whatever spelling the file uses: it is read case-insensitively.
        foreach (var key in root.Select(p => p.Key)
                     .Where(k => string.Equals(k, "ContentAttributes", StringComparison.OrdinalIgnoreCase)).ToList())
            root.Remove(key);
        if (groups != null)
            root["ContentAttributes"] = JsonSerializer.SerializeToNode(groups, ProteusJson.MetadataWrite);

        PenumbraModMeta.AtomicWrite(path, root.ToJsonString(ProteusJson.MetadataWrite));
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

            // A pre-per-item record must still be undoable and RECOGNISED, so a later write updates its group.
            record.MigrateLegacy();
            BackfillIdentity(modRoot, record);
            return record;
        }
        catch { return null; }
    }

    /// <summary>
    /// Give a legacy item back the set and slot it never recorded, read off the group it wrote. Without them
    /// <see cref="MeshToggleRecord.Find"/> cannot match it and a second, dead group for the same identifier is written.
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
        // Nulls suppressed so the read-only legacy fields are not written back.
        PenumbraModMeta.AtomicWrite(Path.Combine(dir, RecordFile),
            JsonSerializer.Serialize(record, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
    }

    /// <summary>
    /// Copy a model aside before its first edit only, so revert always restores the author's file.
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
    /// The letter an attribute name carries for this slot: <c>atr_<b>t</b>v_a</c> on a top, <c>atr_<b>d</b>v_a</c>
    /// on legs. The game matches an IMC bit to an attribute BY NAME, so a wrong letter is never looked at. It is
    /// the first letter of the slot's path suffix (Penumbra's <c>ShapeAttributeManager.AccessoryByte</c>).
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
