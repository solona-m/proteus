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
/// <item>only material redirects are added: copies of the mod's own <c>…/vNNNN/&lt;name&gt;</c> redirects under the other
/// folders its item's variants use, each in the same container (default data, option or combining container) and
/// pointing at the same file. No IMC entry is written, so the fix follows Penumbra's mod priority like the model does.
/// No group is ever added, moved or reordered, and no key the author wrote is replaced;</item>
/// <item>a record (<c>Proteus/variantrepoint.json</c>) names every copy, written BEFORE the manifest (pending), so a
/// crash between the two leaves something the next run can settle;</item>
/// <item>copies are known by their content (key and file), the recorded container only a hint, so a renamed option or a
/// re-indexed combining container keeps them ours;</item>
/// <item>v3 folders and Proteus sidecar packs are never written;</item>
/// <item>undo removes only copies still as written; a copy someone repointed is theirs (released), one someone removed
/// is never put back (the item is declined), and an author update that drops them all is planned afresh;</item>
/// <item>IMC entries written by build 1428 (record version 1) are still reconciled and undone, never written again.</item>
/// </list>
/// Every write runs under <see cref="ModManifestLock.Gate"/>.
/// </summary>
internal static class VariantRepointWriter
{
    public const string RecordFile = "variantrepoint.json";

    // ── the record ──────────────────────────────────────────────────────────

    internal sealed class Record
    {
        public int Version { get; set; } = 2;
        /// <summary>The mod's <see cref="VariantRepointPlan.Fingerprint"/> (our copies left out) when its fixes were last seen intact.</summary>
        public string Fingerprint { get; set; } = "";
        public List<ItemEntry> Items { get; set; } = [];
        /// <summary>Items the user undid (<see cref="Item.Key"/>): not repointed again until "Repoint what I'm wearing now".</summary>
        public List<string> Declined { get; set; } = [];

        public ItemEntry? Find(Item item)
            => Items.FirstOrDefault(i => string.Equals(i.Key, item.Key, StringComparison.OrdinalIgnoreCase));

        /// <summary>Every copy Proteus recorded, where it sits: what the fingerprint and our own reading leave out.</summary>
        public List<PenumbraModMeta.ContainerRedirect> AllCopies()
            => Items.SelectMany(i => i.Redirects).Select(r => r.AsRedirect()).ToList();
    }

    internal sealed class ItemEntry
    {
        public string ObjectType { get; set; } = "";
        public int SetId { get; set; }
        public string Slot { get; set; } = "";
        /// <summary>The mod's folder the copies come from.</summary>
        public int MaterialId { get; set; }
        public bool Pending { get; set; }
        /// <summary>Proteus's redirect copies.</summary>
        public List<RedirectEntry> Redirects { get; set; } = [];
        /// <summary>Copies someone repointed at another file: theirs now, but not a folder the author deliberately ships.</summary>
        public List<RedirectEntry> Released { get; set; } = [];
        /// <summary>Legacy IMC entries (build 1428, record version 1): reconciled and undone, never written.</summary>
        public List<VariantEntry> Variants { get; set; } = [];

        [JsonIgnore] public string Key => $"{ObjectType}/{SetId}/{Slot}";

        [JsonIgnore] public bool Empty => Redirects.Count == 0 && Variants.Count == 0 && Released.Count == 0;

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

        /// <summary>
        /// Whether a redirect of the mod is one of our copies (where it sits, key and file) or a released key (where it
        /// sits and key, whatever file it names now — repointed again, it is still not a folder the author ships).
        /// </summary>
        public bool Covers(PenumbraModMeta.ContainerRedirect r)
            => Redirects.Any(e => e.Container.Ref.SameAs(r.Container) && PenumbraModMeta.SameFile(e.GamePath, r.GamePath)
                                  && PenumbraModMeta.SameFile(e.File, r.File))
            || Released.Any(e => e.Container.Ref.SameAs(r.Container) && PenumbraModMeta.SameFile(e.GamePath, r.GamePath));
    }

    /// <summary>One redirect: the folder it was copied into, its container (a hint), game path and file.</summary>
    internal sealed class RedirectEntry
    {
        public int Folder { get; set; }
        public ContainerJson Container { get; set; } = new();
        public string GamePath { get; set; } = "";
        public string File { get; set; } = "";

        public PenumbraModMeta.ContainerRedirect AsRedirect() => new(Container.Ref, GamePath, File);
    }

    internal sealed class ContainerJson
    {
        public string Group { get; set; } = "";
        public string? Option { get; set; }
        public int? Index { get; set; }

        [JsonIgnore] public PenumbraModMeta.ContainerRef Ref => new(Group, Option, Index);

        public static ContainerJson From(PenumbraModMeta.ContainerRef r) => new() { Group = r.Group, Option = r.Option, Index = r.Index };
    }

    internal sealed class VariantEntry
    {
        public int Variant { get; set; }
        public EntryJson Entry { get; set; } = new();
    }

    /// <summary>An IMC entry as the version-1 record spells it.</summary>
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

    /// <summary>The record, or null when there is none — or when it cannot be read (see <see cref="TryReadRecord"/>).</summary>
    internal static Record? ReadRecord(string modRoot) => TryReadRecord(modRoot, out var r) ? r : null;

    /// <summary>
    /// The record: true with null when there is none, true with it when read (a version-1 record reads with no copies),
    /// false when it is there but cannot be read (corrupt, or held open). A write must never take an unreadable record for
    /// a missing one: it would replace it and lose every earlier fix and decline.
    /// </summary>
    internal static bool TryReadRecord(string modRoot, out Record? record)
    {
        record = null;
        try
        {
            var at = RecordPath(modRoot);
            if (!File.Exists(at)) return true;
            record = JsonSerializer.Deserialize<Record>(File.ReadAllText(at));
            return record != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static readonly JsonSerializerOptions RecordJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void WriteRecord(string modRoot, Record r)
    {
        var at = RecordPath(modRoot);
        r.Version = 2;
        r.Items.RemoveAll(i => i.Empty && !i.Pending);
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

    /// <summary>
    /// The record exactly as it is on disk, to put back after a failed write: true with null when there is none, false
    /// when it is there but cannot be read (the write is then refused, never restored by deleting).
    /// </summary>
    private static bool TrySnapshot(string modRoot, out string? snapshot)
    {
        snapshot = null;
        try { var at = RecordPath(modRoot); if (File.Exists(at)) snapshot = File.ReadAllText(at); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Restore(string modRoot, string? snapshot)
    {
        var at = RecordPath(modRoot);
        try
        {
            if (snapshot != null) { PenumbraModMeta.AtomicWrite(at, snapshot); return; }
            if (File.Exists(at)) File.Delete(at);
            // A folder the record made, and nothing else: not left behind in the author's mod.
            var dir = Path.GetDirectoryName(at)!;
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next reconcile settles it */ }
    }

    // ── what is ours ────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a manifest entry is exactly a legacy IMC entry Proteus recorded: the same item and variant, all six fields
    /// equal, however Penumbra spells it.
    /// </summary>
    internal static bool IsOurs(JsonElement manipulation, ItemEntry item, VariantEntry variant)
        => ParseImc(manipulation) is { } edit && item.Item is { } i && edit.Item == i && edit.Variant == variant.Variant
           && edit.Entry == variant.Entry.ToImc();

    private static bool SameSlot(JsonElement manipulation, ItemEntry item, VariantEntry variant)
        => ParseImc(manipulation) is { } edit && item.Item is { } i && edit.Item == i && edit.Variant == variant.Variant;

    /// <summary>
    /// The default option's manipulations of a v4 manifest. A readable manifest with no <c>DefaultData</c> at all (most
    /// mods: everything lives in groups), or a <c>DefaultData</c> with no <c>Manipulations</c>, has none, as
    /// <c>RefitModService</c> reads it. Null ("unknown") when the manifest is held, missing or malformed, or
    /// <c>DefaultData</c> is not an object or its <c>Manipulations</c> not an array.
    /// </summary>
    /// <remarks>
    /// One read decides both: two reads could straddle Penumbra releasing a held manifest and take a held one (unknown)
    /// for "no DefaultData" (none), orphaning recorded legacy Imc entries. Every caller refuses a pre-v4 folder first:
    /// its meta.json has no DefaultData, which would read as none.
    /// </remarks>
    private static List<JsonElement>? DefaultManipulations(string modRoot)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modRoot, PenumbraModMeta.MetaFile)));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("DefaultData", out var dd)) return [];
            if (dd.ValueKind != JsonValueKind.Object) return null;
            if (!dd.TryGetProperty("Manipulations", out var ms) || ms.ValueKind == JsonValueKind.Null) return [];
            if (ms.ValueKind != JsonValueKind.Array) return null;
            return ms.EnumerateArray().Select(m => m.Clone()).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// The names of the mod's groups, so a search can tell a renamed option from a group that is gone. Null when the
    /// manifest cannot be read: every group would look gone, and the search would widen to the whole mod.
    /// </summary>
    private static HashSet<string>? GroupNamesOf(string modRoot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modRoot, PenumbraModMeta.MetaFile)));
            if (doc.RootElement.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                    if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String)
                        names.Add(n.GetString()!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        return names;
    }

    /// <summary>
    /// Where a recorded redirect is now, by its content (key and file, case- and slash-insensitive): its hint container
    /// first, then anywhere in the same group, and anywhere in the mod only when that group is gone altogether — so an
    /// identical redirect the author wrote elsewhere is never taken for ours. Each manifest redirect is claimed once.
    /// </summary>
    internal static int Locate(RedirectEntry e, IReadOnlyList<PenumbraModMeta.ContainerRedirect> reds, ISet<int> claimed,
                               ISet<string> groups)
    {
        var hint = e.Container.Ref;
        int best = -1, rank = int.MaxValue;
        bool groupGone = !hint.IsDefault && !groups.Contains(hint.Group);
        for (int i = 0; i < reds.Count; i++)
        {
            if (claimed.Contains(i)) continue;
            var r = reds[i];
            if (!PenumbraModMeta.SameFile(r.GamePath, e.GamePath) || !PenumbraModMeta.SameFile(r.File, e.File)) continue;
            int at = r.Container.SameAs(hint) ? 0
                   : string.Equals(r.Container.Group, hint.Group, StringComparison.OrdinalIgnoreCase) ? 1
                   : groupGone ? 2 : int.MaxValue;
            if (at < rank) { rank = at; best = i; }
        }
        return best;
    }

    // ── reconcile ───────────────────────────────────────────────────────────

    /// <param name="ForgottenKeys">The <see cref="Item.Key"/> of each fix forgotten as replaced (one per fix).</param>
    internal sealed record Reconciled(bool ManifestChanged, int Forgotten, int Dropped, bool Replanned, bool Stamped,
                                      IReadOnlyList<string>? ForgottenKeys = null)
    {
        /// <summary>Fixes forgotten as replaced, of this item only.</summary>
        public int ForgottenOf(Item item)
            => ForgottenKeys?.Count(k => string.Equals(k, item.Key, StringComparison.OrdinalIgnoreCase)) ?? 0;
    }

    /// <summary>
    /// Bring the record in line with the mod as it is now, before anything is planned. Under the manifest lock:
    /// <list type="number">
    /// <item>each recorded copy is found by content (<see cref="Locate"/>), and its container hint follows it;</item>
    /// <item>an author update — every recorded fix gone at once and the mod's content changed since they were last seen
    /// intact — forgets them all (declines stand) so the pieces are planned afresh;</item>
    /// <item>a fix present is still ours; a copy whose key now names another file was repointed by someone: it is
    /// released (theirs), and nothing else of the item is touched; a fix missing was removed by someone, and its item is
    /// declined;</item>
    /// <item>a pending item (a crash between record and manifest) adopts what is present and drops the rest, with no
    /// decline;</item>
    /// <item>an item whose mod now dresses another folder, or several, has its fixes removed;</item>
    /// <item>the fingerprint is stamped afresh, since every fix left is intact.</item>
    /// </list>
    /// Nothing for a mod with no (readable) record, a v3 folder or an unreadable manifest.
    /// </summary>
    public static Reconciled Reconcile(string modRoot)
    {
        lock (ModManifestLock.Gate)
        {
            var none = new Reconciled(false, 0, 0, false, false);
            if (!TryReadRecord(modRoot, out var read) || read is not { } r) return none;
            if (PenumbraModMeta.IsLegacyFolder(modRoot)) return none;
            if (DefaultManipulations(modRoot) is not { } manips) return none;
            var before = JsonSerializer.Serialize(r, RecordJson);
            // An unreadable read is "unknown", never "no redirects": judging copies missing from it would orphan them.
            if (PenumbraModMeta.TryReadContainerRedirects(modRoot) is not { } reds) return none;
            if (GroupNamesOf(modRoot) is not { } groups) return none;
            var claimed = new HashSet<int>();

            // Where each copy and released key is now. Released keys are found by content too (their file is theirs).
            var copyAt = new Dictionary<RedirectEntry, int>();
            foreach (var i in r.Items)
                foreach (var e in i.Redirects.Concat(i.Released))
                    if (Locate(e, reds, claimed, groups) is >= 0 and var at) { copyAt[e] = at; claimed.Add(at); }

            bool LegacyPresent(ItemEntry i, VariantEntry v) => manips.Any(m => IsOurs(m, i, v));
            bool LegacyReplaced(ItemEntry i, VariantEntry v) => !LegacyPresent(i, v) && manips.Any(m => SameSlot(m, i, v));
            bool CopyReplaced(RedirectEntry e, out string file)
            {
                file = "";
                foreach (var x in reds)
                    if (x.Container.SameAs(e.Container.Ref) && PenumbraModMeta.SameFile(x.GamePath, e.GamePath)
                        && !PenumbraModMeta.SameFile(x.File, e.File))
                    {
                        file = x.File;
                        return true;
                    }
                return false;
            }

            var presentCopies = new List<PenumbraModMeta.ContainerRedirect>();
            foreach (var (e, at) in copyAt)
                if (r.Items.Any(i => i.Redirects.Contains(e))) presentCopies.Add(reds[at]);
            var fingerprint = VariantRepointPlan.Fingerprint(modRoot, presentCopies);
            if (fingerprint.Length == 0) return none;

            int forgotten = 0, dropped = 0;
            var forgottenKeys = new List<string>();
            bool replanned = false;
            var done = r.Items.Where(i => !i.Pending).ToList();
            bool AllGone(ItemEntry i)
                => i.Redirects.All(e => !copyAt.ContainsKey(e) && !CopyReplaced(e, out _))
                && i.Variants.All(v => !LegacyPresent(i, v) && !LegacyReplaced(i, v));
            if (done.Any(i => i.Redirects.Count + i.Variants.Count > 0) && done.All(AllGone)
                && !string.Equals(fingerprint, r.Fingerprint, StringComparison.Ordinal))
            {
                foreach (var i in done) r.Items.Remove(i);
                replanned = true;
            }

            var strip = new List<(PenumbraModMeta.ContainerRedirect Copy, ItemEntry Item, VariantEntry? Legacy)>();
            foreach (var i in r.Items.ToList())
            {
                // The hints follow the copies (a renamed option, a re-indexed container).
                foreach (var e in i.Redirects.Concat(i.Released))
                    if (copyAt.TryGetValue(e, out var at)) e.Container = ContainerJson.From(reds[at].Container);
                // A released key gone from the mod altogether (our own copies of the same key don't count) is dropped.
                i.Released.RemoveAll(e => !copyAt.ContainsKey(e)
                    && !reds.Where((x, at) => !claimed.Contains(at)).Any(x => PenumbraModMeta.SameFile(x.GamePath, e.GamePath)));

                if (i.Pending)
                {
                    i.Redirects.RemoveAll(e => !copyAt.ContainsKey(e));
                    i.Variants.RemoveAll(v => !LegacyPresent(i, v));
                    i.Pending = false;
                }
                else
                {
                    bool removed = false;
                    foreach (var e in i.Redirects.ToList())
                    {
                        if (copyAt.ContainsKey(e)) continue;
                        i.Redirects.Remove(e);
                        if (CopyReplaced(e, out var theirs))
                        {
                            // Repointed by someone: theirs. Kept as released so it never reads as a second folder.
                            i.Released.Add(new RedirectEntry { Folder = e.Folder, Container = e.Container, GamePath = e.GamePath, File = theirs });
                            forgotten++;
                            forgottenKeys.Add(i.Key);
                        }
                        else { removed = true; dropped++; }
                    }
                    foreach (var v in i.Variants.ToList())
                    {
                        if (LegacyPresent(i, v)) continue;
                        i.Variants.Remove(v);
                        if (LegacyReplaced(i, v)) { forgotten++; forgottenKeys.Add(i.Key); }
                        else { removed = true; dropped++; }
                    }
                    if (removed && !r.Declined.Contains(i.Key, StringComparer.OrdinalIgnoreCase)) r.Declined.Add(i.Key);
                }

                if (i.Item is not { } item) continue;
                var own = reds.Where(x => !i.Covers(x)).Select(x => x.GamePath);
                var (folders, _) = DressedFolders(own, item);
                if (folders.Count == 1 && folders.Min == i.MaterialId) continue;
                // The mod dresses another folder now (or several): what we wrote points at the wrong one.
                foreach (var e in i.Redirects) strip.Add((e.AsRedirect(), i, null));
                foreach (var v in i.Variants) strip.Add((default, i, v));
                r.Items.Remove(i);
            }

            bool manifestChanged = false;
            if (strip.Count > 0)
            {
                var edits = strip.Where(s => s.Legacy == null)
                                 .GroupBy(s => s.Copy.Container)
                                 .Select(g => new PenumbraModMeta.ContainerEdit(g.Key, new Dictionary<string, string>(),
                                     g.ToDictionary(s => s.Copy.GamePath, s => s.Copy.File, StringComparer.OrdinalIgnoreCase)))
                                 .ToList();
                var legacy = strip.Where(s => s.Legacy != null).ToList();
                PenumbraModMeta.EditContainers(modRoot, edits,
                    legacy.Count == 0 ? null : m => legacy.Any(s => IsOurs(m, s.Item, s.Legacy!)));
                manifestChanged = true;
            }

            r.Items.RemoveAll(i => i.Empty);
            var stamp = manifestChanged ? VariantRepointPlan.Fingerprint(modRoot, r.AllCopies()) : fingerprint;
            bool stamped = false;
            if (stamp.Length > 0 && !string.Equals(r.Fingerprint, stamp, StringComparison.Ordinal))
            {
                r.Fingerprint = stamp;
                stamped = true;
            }
            // Only a record that changed is written: a walk must not rewrite a file inside the author's mod for nothing.
            if (!string.Equals(before, JsonSerializer.Serialize(r, RecordJson), StringComparison.Ordinal))
                WriteRecord(modRoot, r);
            return new Reconciled(manifestChanged, forgotten, dropped, replanned, stamped, forgottenKeys);
        }
    }

    // ── apply ───────────────────────────────────────────────────────────────

    /// <summary>What a write did: per item, the folders copied into, the variants they fix, and how many copies.</summary>
    internal sealed record Outcome(List<(Item Item, List<int> Folders, List<int> Variants, int Copies)> Written,
                                   List<(Item Item, RepointReason Reason)> Left)
    {
        public bool ChangedAny => Written.Count > 0;
        public int Redirects => Written.Sum(w => w.Copies);
    }

    /// <summary>
    /// Test seam: a stop injected after a named step ("pending", "manifest"). Per thread, so tests running in parallel
    /// never see each other's.
    /// </summary>
    [ThreadStatic] internal static Action<string>? AfterStep;

    /// <summary>
    /// Write a mod's planned items, one manifest write each: record the copies (pending), add them to their containers,
    /// record them done with a fresh fingerprint. A failed manifest write puts the record back as it was.
    /// <paramref name="mayWrite"/> is asked before every item.
    /// </summary>
    public static Outcome Apply(string modRoot, IReadOnlyList<ItemWrite> items, Func<bool> mayWrite)
    {
        var written = new List<(Item, List<int>, List<int>, int)>();
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
                if (!TrySnapshot(modRoot, out var snapshot) || !TryReadRecord(modRoot, out var read))
                {
                    left.Add((w.Item, RepointReason.WriteFailed));   // an unreadable record is never replaced
                    continue;
                }
                var r = read ?? new Record();
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
                var add = w.Copies.Where(c => !entry.Redirects.Any(e => e.Container.Ref.SameAs(c.Copy.Container)
                                                                         && PenumbraModMeta.SameFile(e.GamePath, c.Copy.GamePath)))
                                  .ToList();
                if (add.Count == 0) continue;
                entry.Pending = true;
                entry.Redirects.AddRange(add.Select(c => new RedirectEntry
                {
                    Folder = c.Folder, Container = ContainerJson.From(c.Copy.Container), GamePath = c.Copy.GamePath,
                    File = c.Copy.File,
                }));

                try
                {
                    WriteRecord(modRoot, r);
                    AfterStep?.Invoke("pending");
                    var edits = add.GroupBy(c => c.Copy.Container)
                                   .Select(g => new PenumbraModMeta.ContainerEdit(g.Key,
                                       g.ToDictionary(c => c.Copy.GamePath, c => c.Copy.File, StringComparer.OrdinalIgnoreCase),
                                       new Dictionary<string, string>()))
                                   .ToList();
                    PenumbraModMeta.EditContainers(modRoot, edits);
                    AfterStep?.Invoke("manifest");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                               or JsonException or PenumbraModMeta.LegacyFolderException)
                {
                    Restore(modRoot, snapshot);
                    left.Add((w.Item, RepointReason.WriteFailed));
                    continue;
                }

                // The copies are in the manifest now, so the item is written whatever happens to this record write:
                // left pending, the next reconcile adopts what is present.
                written.Add((w.Item, add.Select(c => c.Folder).Distinct().Order().ToList(), w.Variants.ToList(), add.Count));
                entry.Pending = false;
                r.Fingerprint = VariantRepointPlan.Fingerprint(modRoot, r.AllCopies());
                try { WriteRecord(modRoot, r); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays pending */ }
            }
        }
        return new Outcome(written, left);
    }

    // ── undo ────────────────────────────────────────────────────────────────

    internal enum Undone { Removed, NothingRecorded, Failed }

    /// <summary>
    /// "Undo for what I'm wearing", for one item: take out the copies (and legacy IMC entries) still as written — the rest
    /// were changed by someone and are theirs, counted as kept — forget the item, and decline it unless
    /// <paramref name="decline"/> is false. Released keys are theirs and left alone. An unreadable manifest or record
    /// changes nothing.
    /// </summary>
    public static Undone UndoItem(string modRoot, Item item, bool decline, out int removed, out int kept, out string message)
    {
        removed = kept = 0;
        message = "";
        lock (ModManifestLock.Gate)
        {
            if (!TryReadRecord(modRoot, out var read))
            {
                message = string.Format(ProteusLoc.Localize("Service.ModMeta.Unreadable.Fmt",
                    "The manifest of {0} could not be read, so it was not changed."),
                    Path.Combine(Path.GetFileName(modRoot), SidecarDiscoveryService.SidecarSubdir, RecordFile));
                return Undone.Failed;
            }
            if (read is not { } r || r.Find(item) is not { } e) return Undone.NothingRecorded;
            // A pre-v4 folder is never written (Apply refuses it); if one holds a record, leave it and the record alone.
            if (PenumbraModMeta.IsLegacyFolder(modRoot) || DefaultManipulations(modRoot) is not { } manips)
            {
                message = string.Format(ProteusLoc.Localize("Service.ModMeta.Unreadable.Fmt",
                    "The manifest of {0} could not be read, so it was not changed."), Path.GetFileName(modRoot));
                return Undone.Failed;
            }
            if (PenumbraModMeta.TryReadContainerRedirects(modRoot) is not { } reds || GroupNamesOf(modRoot) is not { } groups)
            {
                message = string.Format(ProteusLoc.Localize("Service.ModMeta.Unreadable.Fmt",
                    "The manifest of {0} could not be read, so it was not changed."), Path.GetFileName(modRoot));
                return Undone.Failed;
            }
            var claimed = new HashSet<int>();
            var copies = new List<PenumbraModMeta.ContainerRedirect>();
            foreach (var c in e.Redirects)
                if (Locate(c, reds, claimed, groups) is >= 0 and var at) { claimed.Add(at); copies.Add(reds[at]); }
            var legacy = e.Variants.Where(v => manips.Any(m => IsOurs(m, e, v))).ToList();
            try
            {
                if (copies.Count > 0 || legacy.Count > 0)
                    PenumbraModMeta.EditContainers(modRoot,
                        copies.GroupBy(c => c.Container)
                              .Select(g => new PenumbraModMeta.ContainerEdit(g.Key, new Dictionary<string, string>(),
                                  g.ToDictionary(c => c.GamePath, c => c.File, StringComparer.OrdinalIgnoreCase)))
                              .ToList(),
                        legacy.Count == 0 ? null : m => legacy.Any(v => IsOurs(m, e, v)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or JsonException or PenumbraModMeta.LegacyFolderException)
            {
                message = ex.Message;
                return Undone.Failed;
            }
            removed = copies.Count + legacy.Count;
            // Copies someone repointed since (released, by this walk's reconcile or an earlier one) are theirs: kept.
            kept = e.Redirects.Count + e.Variants.Count - removed + e.Released.Count;
            // Released keys stay recorded, so their redirect never reads as a folder the author ships (several-variants).
            e.Redirects.Clear();
            e.Variants.Clear();
            e.Pending = false;
            if (e.Released.Count == 0) r.Items.Remove(e);
            if (decline && !r.Declined.Contains(item.Key, StringComparer.OrdinalIgnoreCase)) r.Declined.Add(item.Key);
            r.Fingerprint = VariantRepointPlan.Fingerprint(modRoot, r.AllCopies());
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
