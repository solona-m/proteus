using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Proteus.Services.VariantRepointPlan;

namespace Proteus.Services;

/// <summary>
/// The "Gear variants" writes into a worn mod, and their undo. SENSITIVE ("Writes into users' Penumbra mods"):
/// <list type="bullet">
/// <item>only <c>Imc</c> entries appended to the mod's <c>DefaultData.Manipulations</c> — no group is ever added, moved
/// or reordered, and every other key, option and setting is left as it was;</item>
/// <item>a record (<c>Proteus/variantrepoint.json</c>) names every entry, written BEFORE the manifest (pending), so a
/// crash between the two leaves something the next run can settle;</item>
/// <item>v3 folders and Proteus sidecar packs are never written;</item>
/// <item>undo removes only entries still exactly as written; an entry someone replaced is theirs, one someone removed
/// is never put back (the item is declined), and an author update that drops them all is planned afresh;</item>
/// <item>no copy of the manifest is kept: putting back an old one would undo Penumbra's saves and the author's edits.</item>
/// </list>
/// Every write runs under <see cref="ModManifestLock.Gate"/>.
/// </summary>
internal static class VariantRepointWriter
{
    public const string RecordFile = "variantrepoint.json";

    // ── the record ──────────────────────────────────────────────────────────

    internal sealed class Record
    {
        public int Version { get; set; } = 1;
        /// <summary>The mod's <see cref="VariantRepointPlan.Fingerprint"/> when its recorded entries were last seen intact.</summary>
        public string Fingerprint { get; set; } = "";
        public List<ItemEntry> Items { get; set; } = [];
        /// <summary>Items the user undid (<see cref="Item.Key"/>): not repointed again until "Repoint what I'm wearing now".</summary>
        public List<string> Declined { get; set; } = [];

        public ItemEntry? Find(Item item)
            => Items.FirstOrDefault(i => string.Equals(i.Key, item.Key, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class ItemEntry
    {
        public string ObjectType { get; set; } = "";
        public int SetId { get; set; }
        public string Slot { get; set; } = "";
        /// <summary>The mod's folder the entries point at.</summary>
        public int MaterialId { get; set; }
        public bool Pending { get; set; }
        public List<VariantEntry> Variants { get; set; } = [];

        [JsonIgnore] public string Key => $"{ObjectType}/{SetId}/{Slot}";

        [JsonIgnore]
        public Item? Item
        {
            get
            {
                char kind = string.Equals(ObjectType, "Accessory", StringComparison.OrdinalIgnoreCase) ? 'a'
                          : string.Equals(ObjectType, "Equipment", StringComparison.OrdinalIgnoreCase) ? 'e' : '\0';
                if (kind == '\0' || ContentSlot.LabelForEquipSlot(Slot) is not { } label) return null;
                foreach (var s in new[] { "met", "top", "glv", "dwn", "sho", "ear", "nek", "wrs", "rir", "ril" })
                    if (ItemFor(s, SetId) is { } i && i.Kind == kind && i.SlotLabel == label) return i;
                return null;
            }
        }
    }

    internal sealed class VariantEntry
    {
        public int Variant { get; set; }
        public EntryJson Entry { get; set; } = new();
    }

    /// <summary>An IMC entry as the record spells it.</summary>
    internal sealed class EntryJson
    {
        public int MaterialId { get; set; }
        public int DecalId { get; set; }
        public int AttributeMask { get; set; }
        public int SoundId { get; set; }
        public int VfxId { get; set; }
        public int MaterialAnimationId { get; set; }

        public ImcEntry ToImc() => new((byte)MaterialId, (byte)DecalId, (ushort)(AttributeMask & 0x3FF), (byte)(SoundId & 0x3F),
                                       (byte)VfxId, (byte)MaterialAnimationId);

        public static EntryJson From(ImcEntry e) => new()
        {
            MaterialId = e.MaterialId, DecalId = e.DecalId, AttributeMask = e.AttributeMask, SoundId = e.SoundId,
            VfxId = e.VfxId, MaterialAnimationId = e.MaterialAnimationId,
        };
    }

    private static string RecordPath(string modRoot)
        => Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);

    internal static Record? ReadRecord(string modRoot)
    {
        try
        {
            var at = RecordPath(modRoot);
            if (!File.Exists(at)) return null;
            return JsonSerializer.Deserialize<Record>(File.ReadAllText(at));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions RecordJson = new() { WriteIndented = true };

    private static void WriteRecord(string modRoot, Record r)
    {
        var at = RecordPath(modRoot);
        r.Items.RemoveAll(i => i.Variants.Count == 0 && !i.Pending);
        if (r.Items.Count == 0 && r.Declined.Count == 0)
        {
            if (File.Exists(at)) File.Delete(at);
            // The Proteus/ folder was made for this record alone: take it away with it.
            var dir = Path.GetDirectoryName(at)!;
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (IOException) { /* harmless */ }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        PenumbraModMeta.AtomicWrite(at, JsonSerializer.Serialize(r, RecordJson));
    }

    /// <summary>The record exactly as it is on disk, to put back after a failed write; null when there is none.</summary>
    private static string? Snapshot(string modRoot)
    {
        try { var at = RecordPath(modRoot); return File.Exists(at) ? File.ReadAllText(at) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Restore(string modRoot, string? snapshot)
    {
        var at = RecordPath(modRoot);
        try
        {
            if (snapshot == null) { if (File.Exists(at)) File.Delete(at); }
            else PenumbraModMeta.AtomicWrite(at, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next reconcile settles it */ }
    }

    // ── what is ours ────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a manifest entry is exactly one Proteus recorded: the same item and variant, all six fields equal,
    /// however Penumbra spells it.
    /// </summary>
    internal static bool IsOurs(JsonElement manipulation, ItemEntry item, VariantEntry variant)
        => ParseImc(manipulation) is { } edit && item.Item is { } i && edit.Item == i && edit.Variant == variant.Variant
           && edit.Entry == variant.Entry.ToImc();

    /// <summary>Whether a manifest entry names the same item and variant (whatever its fields).</summary>
    private static bool SameSlot(JsonElement manipulation, ItemEntry item, VariantEntry variant)
        => ParseImc(manipulation) is { } edit && item.Item is { } i && edit.Item == i && edit.Variant == variant.Variant;

    private static List<JsonElement>? DefaultManipulations(string modRoot)
        => PenumbraModMeta.TryReadDefaultData(modRoot) is { } dd ? dd.Manipulations.OfType<JsonElement>().ToList() : null;

    // ── reconcile ───────────────────────────────────────────────────────────

    internal sealed record Reconciled(bool ManifestChanged, int Forgotten, int Dropped, bool Replanned, bool Stamped);

    /// <summary>
    /// Bring the record in line with the mod as it is now, before anything is planned. Under the manifest lock:
    /// <list type="number">
    /// <item>an author update — every recorded entry gone at once and the mod's content changed since they were last
    /// seen intact — forgets them all (declines stand) so the pieces are planned afresh;</item>
    /// <item>an entry present and equal is still ours; present but different was replaced, and is forgotten (theirs
    /// kept); missing was removed by someone, and its item is declined;</item>
    /// <item>a pending item (a crash between record and manifest) adopts what is present and drops the rest, with no
    /// decline;</item>
    /// <item>an item whose mod now dresses another folder, or several, has its entries removed;</item>
    /// <item>the fingerprint is stamped afresh, since every entry left is now intact — so other edits to the mod (the
    /// shader upgrade's masks, a refit) never make a later removal look like an author update.</item>
    /// </list>
    /// Nothing for a mod with no record, a v3 folder or an unreadable manifest.
    /// </summary>
    public static Reconciled Reconcile(string modRoot)
    {
        lock (ModManifestLock.Gate)
        {
            var none = new Reconciled(false, 0, 0, false, false);
            if (ReadRecord(modRoot) is not { } r) return none;
            if (PenumbraModMeta.IsLegacyFolder(modRoot)) return none;
            if (DefaultManipulations(modRoot) is not { } manips) return none;
            var fingerprint = VariantRepointPlan.Fingerprint(modRoot);
            if (fingerprint.Length == 0) return none;
            var before = JsonSerializer.Serialize(r, RecordJson);

            bool Present(ItemEntry i, VariantEntry v) => manips.Any(m => IsOurs(m, i, v));
            bool Replaced(ItemEntry i, VariantEntry v) => !Present(i, v) && manips.Any(m => SameSlot(m, i, v));

            int forgotten = 0, dropped = 0;
            bool replanned = false;
            var done = r.Items.Where(i => !i.Pending).ToList();
            if (done.Count > 0 && done.All(i => i.Variants.All(v => !Present(i, v) && !Replaced(i, v)))
                && !string.Equals(fingerprint, r.Fingerprint, StringComparison.Ordinal))
            {
                foreach (var i in done) r.Items.Remove(i);
                replanned = true;
            }

            var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
            var strip = new List<(ItemEntry Item, VariantEntry Variant)>();
            foreach (var i in r.Items.ToList())
            {
                if (i.Pending)
                {
                    i.Variants.RemoveAll(v => !Present(i, v));
                    i.Pending = false;
                }
                else
                {
                    bool removed = false;
                    foreach (var v in i.Variants.ToList())
                    {
                        if (Present(i, v)) continue;
                        i.Variants.Remove(v);
                        if (Replaced(i, v)) forgotten++;
                        else { removed = true; dropped++; }
                    }
                    if (removed && !r.Declined.Contains(i.Key, StringComparer.OrdinalIgnoreCase)) r.Declined.Add(i.Key);
                }

                if (i.Item is not { } item) continue;
                var (folders, _) = DressedFolders(redirects, item);
                if (folders.Count == 1 && folders.Min == i.MaterialId) continue;
                // The mod dresses another folder now (or several): what we wrote points at the wrong one.
                strip.AddRange(i.Variants.Select(v => (i, v)));
                r.Items.Remove(i);
            }

            bool manifestChanged = false;
            if (strip.Count > 0)
            {
                PenumbraModMeta.RemoveOptionRedirects(modRoot, "", "", new Dictionary<string, string>(),
                    m => strip.Any(s => IsOurs(m, s.Item, s.Variant)));
                manifestChanged = true;
            }

            r.Items.RemoveAll(i => i.Variants.Count == 0);
            bool stamped = false;
            if (!string.Equals(r.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                r.Fingerprint = fingerprint;
                stamped = true;
            }
            // Only a record that changed is written: a walk must not rewrite a file inside the author's mod for nothing.
            if (!string.Equals(before, JsonSerializer.Serialize(r, RecordJson), StringComparison.Ordinal))
                WriteRecord(modRoot, r);
            return new Reconciled(manifestChanged, forgotten, dropped, replanned, stamped);
        }
    }

    // ── apply ───────────────────────────────────────────────────────────────

    internal sealed record Outcome(List<(Item Item, List<int> Variants)> Written, List<(Item Item, RepointReason Reason)> Left)
    {
        public bool ChangedAny => Written.Count > 0;
        public int Entries => Written.Sum(w => w.Variants.Count);
    }

    /// <summary>
    /// Test seam: a stop injected after a named step ("pending", "manifest"). Per thread, so tests running in parallel
    /// never see each other's.
    /// </summary>
    [ThreadStatic] internal static Action<string>? AfterStep;

    /// <summary>
    /// Write a mod's planned items, one manifest write each: record the item (pending), append its entries to the
    /// default data, record it done with a fresh fingerprint. A failed manifest write puts the record back as it was.
    /// <paramref name="mayWrite"/> is asked before every item.
    /// </summary>
    public static Outcome Apply(string modRoot, IReadOnlyList<ItemWrite> items, Func<bool> mayWrite)
    {
        var written = new List<(Item, List<int>)>();
        var left = new List<(Item, RepointReason)>();
        if (!Directory.Exists(modRoot))
            return new Outcome(written, items.Select(i => (i.Item, RepointReason.WriteFailed)).ToList());
        lock (ModManifestLock.Gate)
        {
            if (PenumbraModMeta.IsLegacyFolder(modRoot))
                return new Outcome(written, items.Select(i => (i.Item, RepointReason.LegacyMod)).ToList());
            if (ShaderUpgradePlan.IsProteusPack(modRoot))
                return new Outcome(written, items.Select(i => (i.Item, RepointReason.ProteusPack)).ToList());

            foreach (var w in items)
            {
                if (!mayWrite()) break;
                var snapshot = Snapshot(modRoot);
                var r = ReadRecord(modRoot) ?? new Record();
                var entry = r.Find(w.Item);
                if (entry != null && entry.MaterialId != w.Folder)
                {
                    left.Add((w.Item, RepointReason.WriteFailed));   // reconcile clears a stale folder before any plan
                    continue;
                }
                if (entry == null)
                {
                    entry = new ItemEntry { ObjectType = w.Item.ObjectType, SetId = w.Item.SetId, Slot = w.Item.EquipSlot,
                                            MaterialId = w.Folder };
                    r.Items.Add(entry);
                }
                var add = w.Add.Where(a => entry.Variants.All(v => v.Variant != a.Variant)).ToList();
                if (add.Count == 0) continue;
                entry.Pending = true;
                entry.Variants.AddRange(add.Select(a => new VariantEntry { Variant = a.Variant, Entry = EntryJson.From(a.Entry) }));

                try
                {
                    WriteRecord(modRoot, r);
                    AfterStep?.Invoke("pending");
                    var manips = add.Select(a => PenumbraManipulations.ImcManipulation(w.Item.Kind, w.Item.SetId, a.Variant,
                                                                                       w.Item.EquipSlot, a.Entry))
                                    .ToList();
                    PenumbraModMeta.MergeOptionRedirects(modRoot, "", "", new Dictionary<string, string>(), manips);
                    AfterStep?.Invoke("manifest");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                               or JsonException or PenumbraModMeta.LegacyFolderException)
                {
                    Restore(modRoot, snapshot);
                    left.Add((w.Item, RepointReason.WriteFailed));
                    continue;
                }

                entry.Pending = false;
                r.Fingerprint = VariantRepointPlan.Fingerprint(modRoot);
                WriteRecord(modRoot, r);
                written.Add((w.Item, add.Select(a => a.Variant).ToList()));
            }
        }
        return new Outcome(written, left);
    }

    // ── undo ────────────────────────────────────────────────────────────────

    internal enum Undone { Removed, NothingRecorded, Failed }

    /// <summary>
    /// "Undo for what I'm wearing", for one item: take out the entries still exactly as written (the rest were changed
    /// by someone and are theirs: counted as kept), forget the item, and decline it unless <paramref name="decline"/> is
    /// false. An unreadable manifest changes nothing and keeps the record.
    /// </summary>
    public static Undone UndoItem(string modRoot, Item item, bool decline, out int removed, out int kept, out string message)
    {
        removed = kept = 0;
        message = "";
        lock (ModManifestLock.Gate)
        {
            if (ReadRecord(modRoot) is not { } r || r.Find(item) is not { } e) return Undone.NothingRecorded;
            if (DefaultManipulations(modRoot) is not { } manips)
            {
                message = string.Format(ProteusLoc.Localize("Service.ModMeta.Unreadable.Fmt",
                    "The manifest of {0} could not be read, so it was not changed."), Path.GetFileName(modRoot));
                return Undone.Failed;
            }
            var present = e.Variants.Where(v => manips.Any(m => IsOurs(m, e, v))).ToList();
            try
            {
                if (present.Count > 0)
                    PenumbraModMeta.RemoveOptionRedirects(modRoot, "", "", new Dictionary<string, string>(),
                        m => present.Any(v => IsOurs(m, e, v)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or JsonException or PenumbraModMeta.LegacyFolderException)
            {
                message = ex.Message;
                return Undone.Failed;
            }
            removed = present.Count;
            kept = e.Variants.Count - present.Count;
            r.Items.Remove(e);
            if (decline && !r.Declined.Contains(item.Key, StringComparer.OrdinalIgnoreCase)) r.Declined.Add(item.Key);
            r.Fingerprint = VariantRepointPlan.Fingerprint(modRoot);
            WriteRecord(modRoot, r);
            return Undone.Removed;
        }
    }

    /// <summary>"Repoint what I'm wearing now": the user asks again, so these items are no longer declined.</summary>
    public static void ClearDeclines(string modRoot, IEnumerable<Item> items)
    {
        lock (ModManifestLock.Gate)
        {
            if (ReadRecord(modRoot) is not { } r) return;
            var keys = items.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (r.Declined.RemoveAll(keys.Contains) > 0)
            {
                try { WriteRecord(modRoot, r); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays declined */ }
            }
        }
    }
}
