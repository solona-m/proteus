using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services.Matcap;

/// <summary>
/// Writes matcaps into a user's mod — a Sensitive writer (a malformed model or material crashes the game, and edits
/// travel with exports). What it adds:
/// <list type="bullet">
/// <item>the split model(s), each backed up first (<see cref="BackupSubdir"/>, first edit only);</item>
/// <item>the material, a white mask and — for an own image — the baked texture, under <see cref="FilesFolder"/>;</item>
/// <item>their redirects, in the mod's DEFAULT DATA only, under names no author or game file uses, so no group is ever
/// added or reordered and no author option is touched;</item>
/// <item>the record, <c>Proteus/matcaps.json</c>.</item>
/// </list>
/// A library is only ever referenced by its shared texture path; nothing of a library is written here.
/// <para/>
/// Writes run under <see cref="ModManifestLock.Gate"/> then <see cref="MeshVolumeService.WriteLock"/>, as every model
/// writer's do. Any failure rolls back what this call added. Undo works on the model's apply group (every file sharing a
/// material with it), reconciling first: a material no recorded model still names is forgotten with its own files.
/// </summary>
internal static class MatcapWriter
{
    public const string RecordFile = "matcaps.json";
    public const string BackupSubdir = "matcap-backup";
    public const string SupersededSubdir = "matcap-backup-superseded";
    public const string FilesFolder = "Proteus Matcaps";

    /// <summary>Test seam: called after each step of <see cref="Apply"/> ("pending", "files", "redirects", "model").</summary>
    [ThreadStatic] internal static Action<string>? AfterStep;

    // ── where things go ─────────────────────────────────────────────────────

    public static string TextureFolder(string token) => $"chara/common/texture/proteus/matcap/{token}";
    public static string MaskGamePath(string token) => $"{TextureFolder(token)}/mask.tex";
    public static string FlatNormalGamePath(string token) => $"{TextureFolder(token)}/flatnormal.tex";
    public static string MaskFile(string token) => $"{FilesFolder}/mask_{token}.tex";
    public static string FlatNormalFile(string token) => $"{FilesFolder}/flatnormal_{token}.tex";

    /// <summary>The file stem of a material name: <c>/mt_x_mcapabc1.mtrl</c> → <c>mt_x_mcapabc1</c>.</summary>
    public static string StemOf(string materialName)
    {
        var n = materialName.Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..];
        return n.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n;
    }

    public static string MaterialFile(string materialName) => $"{FilesFolder}/{StemOf(materialName)}.mtrl";
    public static string ImageTexFile(string materialName) => $"{FilesFolder}/{StemOf(materialName)}.tex";
    public static string ImageTexGamePath(string token, string materialName) => $"{TextureFolder(token)}/{StemOf(materialName).ToLowerInvariant()}.tex";

    // ── requests and outcomes ───────────────────────────────────────────────

    /// <summary>One model file the apply rewrites: the bytes planned against, and the bytes after every split.</summary>
    public sealed record ModelEdit(string Rel, string GamePath, byte[] SourceBytes, byte[] NewBytes, bool Lod0Cut);

    /// <summary>
    /// One new matcap material: its bytes and game paths, the normal and alpha threshold it took from its source
    /// material, where its matcap comes from (and the baked texture for an image), and the meshes that draw it.
    /// </summary>
    public sealed record MaterialPlan(string Name, byte[] Bytes, IReadOnlyList<string> GamePaths, string Normal,
                                      float? AlphaThreshold, MatcapRecord.SourceRef Source, string? ImageTexTemp,
                                      IReadOnlyList<(string Rel, int Source, int Drawn)> Meshes);

    /// <summary>
    /// Everything an apply needs, prepared on a worker (<see cref="MatcapStudio"/>): one material per source material
    /// among the ticked parts, and every model file after all of their splits.
    /// </summary>
    public sealed record ApplyRequest(string Token, IReadOnlyList<MaterialPlan> Materials, IReadOnlyList<ModelEdit> Models,
                                      string MaskTemp, string? FlatNormalTemp)
    {
        /// <summary>The first material's name (most applies have one).</summary>
        public string MaterialName => Materials[0].Name;
        public string Normal => Materials[0].Normal;
        public float? AlphaThreshold => Materials[0].AlphaThreshold;
    }

    /// <summary>What a call did. <see cref="Code"/> is empty on success, else a stable refusal code.</summary>
    public sealed record Outcome(string Code, string Message, IReadOnlyList<string> Files)
    {
        public bool Ok => Code.Length == 0;
        public static Outcome Done(string message, IReadOnlyList<string> files) => new("", message, files);
        public static Outcome Refuse(string code, string message) => new(code, message, []);
    }

    public const string CodeLegacy = "legacy-mod", CodeEdited = "edited", CodeLaterFeature = "later-feature",
                        CodeNothing = "nothing-to-undo", CodeCrossKind = "cross-kind-change", CodeUnreadable = "record-unreadable",
                        CodeFailed = "failed", CodeManaged = "imported-piece";


    // ── apply ───────────────────────────────────────────────────────────────

    public static Outcome Apply(string modRoot, ApplyRequest req)
    {
        lock (ModManifestLock.Gate)
        lock (MeshVolumeService.WriteLock)
        {
            if (string.Equals(Path.GetFileName(modRoot.TrimEnd('\\', '/')), SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
                return Outcome.Refuse(CodeManaged, ProteusLoc.Localize("Matcap.Refused.ImportedPiece",
                    "This piece belongs to an imported pack, which Proteus rebuilds itself. Put the matcap on the pack's own mod instead."));
            if (PenumbraModMeta.IsLegacyFolder(modRoot))
                return Outcome.Refuse(CodeLegacy, ProteusLoc.Localize("Matcap.Refused.Legacy",
                    "This mod is still in Penumbra's old folder layout. Open it in Penumbra once so it is updated, then try again."));

            var record = MatcapRecord.Read(modRoot);
            if (record.Unreadable)
                return Outcome.Refuse(CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RecordUnreadable",
                    "Proteus's record of this mod's matcaps (Proteus/matcaps.json) could not be read, so nothing was changed."));

            foreach (var m in req.Models)
            {
                var full = Full(modRoot, m.Rel);
                var now = File.Exists(full) ? File.ReadAllBytes(full) : [];
                if (!now.AsSpan().SequenceEqual(m.SourceBytes))
                    return Outcome.Refuse(CodeEdited, string.Format(ProteusLoc.Localize("Matcap.Refused.Changed.Fmt",
                        "{0} changed while the matcap was being prepared, so nothing was written. Try again."), m.Rel));
            }

            if (string.IsNullOrEmpty(record.Token)) record.Token = req.Token;
            var created = new List<string>();          // files this call created
            var backups = new List<string>();          // backups this call took
            var addedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var written = new List<ModelEdit>();
            var entries = req.Materials.Select(p => new MatcapRecord.MaterialEntry
            {
                Name = p.Name, File = MaterialFile(p.Name), GamePaths = [.. p.GamePaths],
                Normal = p.Normal, AlphaThreshold = p.AlphaThreshold, Pending = true, Source = p.Source,
                Meshes = p.Meshes.Select(x => new MatcapRecord.MeshRef { Model = x.Rel, Mesh = x.Drawn, From = x.Source }).ToList(),
            }).ToList();
            // A backup is the original only while the file still carries one of our matcaps (as written, or edited on
            // top by a later feature such as the brush, which keeps its own backup in the order). A backup over a file
            // that names none of them — left by a failed retire or a deleted record, the author's update since — is
            // stale: retired, and a fresh one taken, so an Undo never brings back an older model. A refit must also still
            // be as we wrote it: a remake writes it again from its source, which can carry one of our matcaps too.
            var staleBackups = new List<string>();
            foreach (var m in req.Models)
            {
                var backup = BackupPath(modRoot, BackupSubdir, m.Rel);
                if (!File.Exists(backup)) continue;
                var names = TryNames(m.SourceBytes);
                if (record.Model(m.Rel) is { } known && names != null && record.Materials.Any(x => names.Contains(x.Name))
                    && !RemadeRegenerated(modRoot, m.Rel, record, known, m.SourceBytes)) continue;
                staleBackups.Add(m.Rel);
            }

            // Everything this call will place, recorded before anything is placed: a crash at any later step leaves
            // a record Undo can act on (the model's new hash is known; shared files are named).
            var addedShas = new List<(string Rel, string Sha)>();
            bool maskSet = false, flatSet = false;
            byte[]? maskBytes = null, flatBytes = null;
            try
            {
                foreach (var rel in staleBackups)
                    if (!Retire(modRoot, rel)) throw new IOException($"could not retire the old backup of {rel}");

                // Backups, the first time a model is edited only: the original is the original.
                foreach (var m in req.Models)
                {
                    var backup = BackupPath(modRoot, BackupSubdir, m.Rel);
                    if (File.Exists(backup)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(Full(modRoot, m.Rel), backup);
                    backups.Add(backup);
                }

                record.Materials.AddRange(entries);
                for (int i = 0; i < req.Materials.Count; i++)
                {
                    entries[i].Sha8 = HairPortPlan.Sha8(req.Materials[i].Bytes);
                    if (entries[i].Source.Tex is { } tex && req.Materials[i].ImageTexTemp is { } tt)
                        tex.Sha8 = HairPortPlan.Sha8(File.ReadAllBytes(tt));
                }
                foreach (var m in req.Models)
                {
                    if (record.Model(m.Rel) == null)
                        record.Models.Add(new MatcapRecord.ModelEntry { File = m.Rel, GamePath = m.GamePath });
                    var me = record.Model(m.Rel)!;
                    var sha = HairPortPlan.Sha8(m.NewBytes);
                    if (!me.WrittenSha8s.Contains(sha)) { me.WrittenSha8s.Add(sha); addedShas.Add((m.Rel, sha)); }
                }
                if (record.Shared.Mask == null || !File.Exists(Full(modRoot, record.Shared.Mask.File)))
                {
                    maskBytes = File.ReadAllBytes(req.MaskTemp);
                    record.Shared.Mask = new MatcapRecord.FileRef { File = MaskFile(record.Token), GamePath = MaskGamePath(record.Token), Sha8 = HairPortPlan.Sha8(maskBytes) };
                    maskSet = true;
                }
                if (req.FlatNormalTemp != null && (record.Shared.FlatNormal == null || !File.Exists(Full(modRoot, record.Shared.FlatNormal.File))))
                {
                    flatBytes = File.ReadAllBytes(req.FlatNormalTemp);
                    record.Shared.FlatNormal = new MatcapRecord.FileRef { File = FlatNormalFile(record.Token), GamePath = FlatNormalGamePath(record.Token), Sha8 = HairPortPlan.Sha8(flatBytes) };
                    flatSet = true;
                }
                MatcapRecord.Write(modRoot, record);
                AfterStep?.Invoke("pending");

                // Files: each material and its baked texture, and the shared mask and flat normal on first need.
                for (int i = 0; i < req.Materials.Count; i++)
                {
                    var plan = req.Materials[i];
                    var entry = entries[i];
                    Place(modRoot, entry.File, plan.Bytes, created);
                    foreach (var p in plan.GamePaths) addedKeys[p] = entry.File;
                    if (entry.Source.Tex is { } tex && plan.ImageTexTemp != null)
                    {
                        Place(modRoot, tex.File, File.ReadAllBytes(plan.ImageTexTemp), created);
                        addedKeys[tex.GamePath] = tex.File;
                    }
                }
                if (maskSet)
                {
                    Place(modRoot, record.Shared.Mask!.File, maskBytes!, created);
                    addedKeys[record.Shared.Mask.GamePath] = record.Shared.Mask.File;
                }
                if (flatSet)
                {
                    Place(modRoot, record.Shared.FlatNormal!.File, flatBytes!, created);
                    addedKeys[record.Shared.FlatNormal.GamePath] = record.Shared.FlatNormal.File;
                }
                AfterStep?.Invoke("files");

                PenumbraModMeta.EditContainers(modRoot, [new PenumbraModMeta.ContainerEdit(
                    new PenumbraModMeta.ContainerRef(""), addedKeys, new Dictionary<string, string>())]);
                AfterStep?.Invoke("redirects");

                foreach (var m in req.Models)
                {
                    PenumbraModMeta.AtomicWrite(Full(modRoot, m.Rel), m.NewBytes);
                    written.Add(m);
                    record.Model(m.Rel)!.Lod0Cut |= m.Lod0Cut;
                }
                AfterStep?.Invoke("model");

                foreach (var e in entries) e.Pending = false;
                MatcapRecord.Write(modRoot, record);
                var applied = string.Format(ProteusLoc.Localize("Parts.Matcap.Applied.Fmt", "Matcap applied: {0} on {1} model file(s)."),
                    string.Join(", ", req.Materials.Select(p => StemOf(p.Name))), req.Models.Count);
                // A hair port with a matcap is kept as it is (HairPortWriter.EditedBy): say so, since nothing else would
                // until the hairstyle it was made from changes.
                if (req.Models.Any(m => MatcapPlan.Regenerated(modRoot, m.Rel) == MatcapPlan.HairPortKind))
                    applied += " " + ProteusLoc.Localize("Parts.Matcap.PortKept",
                        "That model is one of Proteus's hair ports. While it has a matcap, Proteus keeps it as it is, and doesn't by itself remake the ports of other races that share its material, until you undo the matcap. Porting one of those races by hand in the Studio still remakes it.");
                return Outcome.Done(applied, [.. req.Models.Select(m => m.Rel), .. created]);
            }
            catch (Exception ex)
            {
                // Roll back exactly what this call added.
                foreach (var m in written)
                    try { PenumbraModMeta.AtomicWrite(Full(modRoot, m.Rel), m.SourceBytes); } catch { }
                try
                {
                    if (addedKeys.Count > 0)
                        PenumbraModMeta.EditContainers(modRoot, [new PenumbraModMeta.ContainerEdit(
                            new PenumbraModMeta.ContainerRef(""), new Dictionary<string, string>(), addedKeys)]);
                }
                catch { }
                foreach (var f in created) TryDelete(Full(modRoot, f));
                foreach (var b in backups) TryDelete(b);
                try
                {
                    var fresh = MatcapRecord.Read(modRoot);
                    if (!fresh.Unreadable)
                    {
                        fresh.Materials.RemoveAll(x => req.Materials.Any(p => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)));
                        foreach (var (rel, sha) in addedShas) fresh.Model(rel)?.WrittenSha8s.Remove(sha);
                        fresh.Models.RemoveAll(x => !fresh.MaterialsOf(x.File).Any() && req.Models.Any(r => PenumbraModMeta.SameFile(r.Rel, x.File)) && x.WrittenSha8s.Count == 0);
                        if (maskSet) fresh.Shared.Mask = null;
                        if (flatSet) fresh.Shared.FlatNormal = null;
                        MatcapRecord.Write(modRoot, fresh);
                    }
                }
                catch { }
                return Outcome.Refuse(CodeFailed, string.Format(ProteusLoc.Localize("Matcap.Refused.Failed.Fmt",
                    "The matcap could not be written ({0}). Nothing was changed."), ex.Message));
            }
        }
    }

    // ── change ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Point an existing matcap at another matcap of the SAME kind, without splitting again: an image's texture is
    /// re-baked in place; a library's material is repointed when the slot path differs (an option alone changes nothing
    /// here — the collection setting does that). Refused across kinds, and when our file was edited by hand.
    /// </summary>
    public static Outcome Change(string modRoot, string materialName, MatcapRecord.SourceRef source,
                                 byte[]? newMaterialBytes, string? imageTexTemp)
    {
        lock (ModManifestLock.Gate)
        lock (MeshVolumeService.WriteLock)
        {
            var record = MatcapRecord.Read(modRoot);
            if (record.Unreadable)
                return Outcome.Refuse(CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RecordUnreadable",
                    "Proteus's record of this mod's matcaps (Proteus/matcaps.json) could not be read, so nothing was changed."));
            var entry = record.Materials.FirstOrDefault(m => string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Outcome.Refuse(CodeNothing, ProteusLoc.Localize("Matcap.Refused.NoEntry", "That matcap is not in this mod's record."));
            if (entry.Source.Kind != source.Kind)
                return Outcome.Refuse(CodeCrossKind, ProteusLoc.Localize("Parts.Matcap.ChangeKindTip",
                    "A matcap from your images can only change to another image, and a library's to another library option. Undo it to switch kinds."));

            var files = new List<string>();
            if (source.IsLibrary)
            {
                if (newMaterialBytes != null && !string.Equals(entry.Source.Library?.Texture, source.Library?.Texture, StringComparison.OrdinalIgnoreCase))
                {
                    if (Edited(modRoot, entry.File, entry.Sha8)) return HandEdited(entry.File);
                    PenumbraModMeta.AtomicWrite(Full(modRoot, entry.File), newMaterialBytes);
                    entry.Sha8 = HairPortPlan.Sha8(newMaterialBytes);
                    files.Add(entry.File);
                }
                entry.Source = source;
            }
            else
            {
                var tex = entry.Source.Tex;
                if (tex == null || imageTexTemp == null) return Outcome.Refuse(CodeFailed, ProteusLoc.Localize("Matcap.Refused.NoEntry", "That matcap is not in this mod's record."));
                if (Edited(modRoot, tex.File, tex.Sha8)) return HandEdited(tex.File);
                var bytes = File.ReadAllBytes(imageTexTemp);
                PenumbraModMeta.AtomicWrite(Full(modRoot, tex.File), bytes);
                tex.Sha8 = HairPortPlan.Sha8(bytes);
                entry.Source = new MatcapRecord.SourceRef { Kind = source.Kind, Image = source.Image, ImageSha8 = source.ImageSha8, Tex = tex };
                files.Add(tex.File);
            }
            MatcapRecord.Write(modRoot, record);
            return Outcome.Done(string.Format(ProteusLoc.Localize("Parts.Matcap.Changed.Fmt", "Matcap changed: {0}."), StemOf(materialName)), files);
        }
    }

    // ── undo, forget, reconcile ─────────────────────────────────────────────

    /// <summary>Whether <paramref name="rel"/> still names one of the record's materials — what keeps its backup counting
    /// in <see cref="ModelBackupOrder"/>. A superseded matcap (the author updated the model) never blocks another undo.</summary>
    public static bool StillApplies(string modRoot, string rel)
    {
        var record = MatcapRecord.Read(modRoot);
        if (record.Unreadable) return true;                // can't tell: keep counting it, never unblock by mistake
        if (record.Model(rel) == null) return false;
        var names = NamesIn(modRoot, rel);
        return names != null && record.Materials.Any(m => names.Contains(m.Name));
    }

    /// <summary>
    /// Whether <paramref name="rel"/> still draws one of its OWN matcaps (a material whose meshes are in it) — not merely
    /// one carried in from the file it was remade from. An unreadable record counts: never unblock by mistake. What makes
    /// a hair port count as edited (<see cref="HairPortWriter.EditedBy"/>).
    /// </summary>
    public static bool NamesOwnMatcap(string modRoot, string rel)
    {
        var record = MatcapRecord.Read(modRoot);
        if (record.Unreadable) return true;
        var names = NamesIn(modRoot, rel);
        // A file that is there but can't be read counts too (never overwritten by mistake); a missing one does not.
        if (names == null) return File.Exists(Full(modRoot, rel)) && record.MaterialsOf(rel).Any();
        return record.MaterialsOf(rel).Any(m => names.Contains(m.Name));
    }

    /// <summary>One entry's state, read-only: what opening a model shows. Writes nothing.</summary>
    public enum State { Ok, Superseded, Edited }

    public static IReadOnlyDictionary<string, State> Detect(string modRoot)
    {
        var result = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        var record = MatcapRecord.Read(modRoot);
        if (record.Unreadable) return result;
        foreach (var mat in record.Materials)
        {
            var models = mat.Meshes.Select(x => x.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool named = false, edited = false;
            foreach (var rel in models)
            {
                var full = Full(modRoot, rel);
                if (!File.Exists(full)) continue;
                var bytes = File.ReadAllBytes(full);
                var names = TryNames(bytes);
                if (names == null || !names.Contains(mat.Name)) continue;
                named = true;
                if (record.Model(rel) is { } me && !me.WrittenSha8s.Contains(HairPortPlan.Sha8(bytes))) edited = true;
            }
            result[mat.Name] = !named ? State.Superseded : edited ? State.Edited : State.Ok;
        }
        return result;
    }

    /// <summary>
    /// Undo every matcap on <paramref name="rel"/>'s apply group: restore each file byte for byte from its first-edit
    /// backup and remove everything Proteus added. Refused whole when any file was changed by a later feature or edited
    /// on top of ours.
    /// </summary>
    /// <param name="readGameFile">The game's own files: given, a game piece's edit mod drops a file that is the game's
    /// own again (see EDGE-59).</param>
    public static Outcome Undo(string modRoot, string rel, Func<string, byte[]?>? readGameFile = null) =>
        UndoOrForget(modRoot, rel, forgetOnly: false, readGameFile);

    /// <summary>Forget matcaps the model no longer names (the author updated it): retire their backups, remove their
    /// files and redirects. Changes no model.</summary>
    public static Outcome Forget(string modRoot, string rel) => UndoOrForget(modRoot, rel, forgetOnly: true);

    private static Outcome UndoOrForget(string modRoot, string rel, bool forgetOnly, Func<string, byte[]?>? readGameFile = null)
    {
        lock (ModManifestLock.Gate)
        lock (MeshVolumeService.WriteLock)
        {
            var record = MatcapRecord.Read(modRoot);
            if (record.Unreadable)
                return Outcome.Refuse(CodeUnreadable, ProteusLoc.Localize("Matcap.Refused.RecordUnreadable",
                    "Proteus's record of this mod's matcaps (Proteus/matcaps.json) could not be read, so nothing was changed."));
            if (record.Model(rel) == null && !record.MaterialsOf(rel).Any())
                return Outcome.Refuse(CodeNothing, ProteusLoc.Localize("Parts.Matcap.NothingToUndo", "There is no matcap on this model to undo."));

            var removeKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>();
            var touched = new List<string>();
            // Refits, hair ports and earless copies remade from a file carrying one of our matcaps draw it too: its files
            // stay while one names it.
            var refitUsers = new List<string>();
            var portUsers = new List<string>();
            // Their material names, read once per pass (a size set can hold hundreds of refits).
            var refits = new Lazy<List<Regenerated>>(() => RegeneratedNames(modRoot));

            // ── reconcile: a material no recorded model still names is forgotten with its own files ──
            foreach (var mat in record.Materials.ToList())
            {
                bool named = mat.Meshes.Select(x => x.Model).Distinct(StringComparer.OrdinalIgnoreCase)
                                .Any(m => NamesIn(modRoot, m)?.Contains(mat.Name) == true);
                if (named) continue;
                if (DropMaterial(modRoot, mat, removeKeys, kept, refitUsers, portUsers, refits.Value)) record.Materials.Remove(mat);
            }

            // ── the group: this model plus every model sharing a remaining material with it ──
            var group = ApplyGroupOf(record, rel);

            var restore = new List<(string Rel, string Backup)>();
            var forget = new List<string>();
            foreach (var g in group)
            {
                var full = Full(modRoot, g);
                var backup = BackupPath(modRoot, BackupSubdir, g);
                if (!File.Exists(full)) { forget.Add(g); continue; }                       // (e) gone: forget it
                var names = NamesIn(modRoot, g);
                bool namesOurs = names != null && record.Materials.Any(m => names.Contains(m.Name));
                if (!namesOurs) { forget.Add(g); continue; }                                // (c) superseded: forget it
                if (forgetOnly)
                {
                    // A refit or hair port written again from its source since (naming none of its own matcaps): forgotten
                    // too, its backup retired — as Undo does.
                    if (record.Model(g) is { } w && RemadeRegenerated(modRoot, g, record, w, File.ReadAllBytes(full))) forget.Add(g);
                    continue;
                }
                if (ModelBackupOrder.LaterFeature(modRoot, BackupSubdir, g) is { } later)   // (a) a later feature
                    return Outcome.Refuse(CodeLaterFeature, ModelBackupOrder.BlockedMessage(later));
                var bytes = File.ReadAllBytes(full);
                var sha = HairPortPlan.Sha8(bytes);
                if (record.Model(g) is { } me && me.WrittenSha8s.Contains(sha) && File.Exists(backup))
                    restore.Add((g, backup));                                               // (b) ours: restore it
                else if (record.Model(g) is { } was && RemadeRegenerated(modRoot, g, record, was, bytes))
                    forget.Add(g);                                                          // (f) a remade refit or port: forget it
                else                                                                        // (d) edited on top
                    return Outcome.Refuse(CodeEdited, string.Format(ProteusLoc.Localize("Matcap.Refused.Edited.Fmt",
                        "{0} was changed after its matcap was applied, so it was not undone. Its original is kept at {1}."),
                        g, Path.GetRelativePath(modRoot, backup)));
            }

            if (forgetOnly && forget.Count == 0 && removeKeys.Count == 0)
                return Outcome.Refuse(CodeNothing, ProteusLoc.Localize("Parts.Matcap.NothingToForget", "Every matcap on this model is still in use; there is nothing to forget."));

            // ── write ──
            foreach (var (g, backup) in restore)
            {
                PenumbraModMeta.AtomicWrite(Full(modRoot, g), File.ReadAllBytes(backup));
                TryDelete(backup);
                touched.Add(g);
            }
            var unretired = forget.Where(g => !Retire(modRoot, g)).ToList();

            var gone = new HashSet<string>(restore.Select(r => r.Rel).Concat(forget), StringComparer.OrdinalIgnoreCase);
            // Read again after the restores: a restored refit or port no longer names what it drew.
            var refitsNow = restore.Count > 0 ? new Lazy<List<Regenerated>>(() => RegeneratedNames(modRoot)) : refits;
            foreach (var mat in record.Materials.ToList())
            {
                // A material leaves with its models: every model it draws in was restored or forgotten.
                if (!mat.Meshes.All(x => gone.Contains(x.Model))) continue;
                if (DropMaterial(modRoot, mat, removeKeys, kept, refitUsers, portUsers, refitsNow.Value)) record.Materials.Remove(mat);
            }
            record.Models.RemoveAll(m => gone.Contains(m.File));

            // A game piece's "— Proteus edit" mod: a file that is the game's own again leaves the mod, so the copy stops
            // outranking face mods installed later (EDGE-59). Only when nothing else edited it (VanillaEditCopy.HoldsEdit).
            var dropped = new List<string>();
            if (readGameFile != null && Path.GetFileName(modRoot.TrimEnd('\\', '/')).Contains(RefitModService.EditQualifier, StringComparison.OrdinalIgnoreCase))
                foreach (var (g, _) in restore)
                {
                    var gamePath = g.Replace('\\', '/');
                    if (readGameFile(gamePath) is not { } game || VanillaEditCopy.HoldsEdit(modRoot, gamePath, game)) continue;
                    if (PenumbraModMeta.TryReadDefaultData(modRoot) is not { } data || !data.Files.TryGetValue(gamePath, out var target)
                        || !PenumbraModMeta.SameFile(target, g)) continue;
                    removeKeys[gamePath] = target;
                    dropped.Add(g);
                }

            // Shared files go with the last material.
            if (record.Materials.Count == 0)
            {
                foreach (var shared in new[] { record.Shared.Mask, record.Shared.FlatNormal })
                {
                    if (shared == null) continue;
                    removeKeys[shared.GamePath] = shared.File;
                    if (Edited(modRoot, shared.File, shared.Sha8)) kept.Add(shared.File); else TryDelete(Full(modRoot, shared.File));
                }
                record.Shared = new MatcapRecord.SharedFiles();
            }

            if (removeKeys.Count > 0)
                PenumbraModMeta.EditContainers(modRoot, [new PenumbraModMeta.ContainerEdit(
                    new PenumbraModMeta.ContainerRef(""), new Dictionary<string, string>(), removeKeys)]);
            foreach (var g in dropped) TryDelete(Full(modRoot, g));
            MatcapRecord.Write(modRoot, record);

            var msg = forgetOnly
                ? string.Format(ProteusLoc.Localize("Parts.Matcap.Forgotten.Fmt", "Forgot the matcaps this model no longer uses ({0} file(s))."), forget.Count)
                : string.Format(ProteusLoc.Localize("Parts.Matcap.Undone.Fmt", "Matcaps undone on {0} model file(s)."), restore.Count + forget.Count);
            if (kept.Count > 0)
                msg += " " + string.Format(ProteusLoc.Localize("Parts.Matcap.KeptEdited.Fmt", "Left in place because they were edited by hand: {0}."),
                    string.Join(", ", kept.Distinct()));
            if (refitUsers.Count > 0)
                msg += " " + string.Format(ProteusLoc.Localize("Parts.Matcap.KeptForRefits.Fmt",
                    "Refits made from it still draw the matcap, so its files stay until the sizes are remade: {0}."),
                    string.Join(", ", refitUsers));
            if (portUsers.Count > 0)
                msg += " " + string.Format(ProteusLoc.Localize("Parts.Matcap.KeptForPorts.Fmt",
                    "Hair ports or earless copies made from it still draw the matcap, so its files stay until they are remade: {0}."),
                    string.Join(", ", portUsers));
            if (unretired.Count > 0)
                msg += " " + string.Format(ProteusLoc.Localize("Parts.Matcap.BackupKept.Fmt",
                    "Their old backups could not be moved aside: {0}. The next matcap there moves them first."), string.Join(", ", unretired));
            return Outcome.Done(msg, touched);
        }
    }

    /// <summary>
    /// Removes the material's files and redirects — unless a refit, hair port or earless copy on disk still names it
    /// (remade from a file that carried it), which would then draw a missing material: kept, the file noted in
    /// <paramref name="refitUsers"/> or <paramref name="portUsers"/>, and false returned so the record keeps it until
    /// nothing names it (a later Undo or Forget checks again).
    /// </summary>
    private static bool DropMaterial(string modRoot, MatcapRecord.MaterialEntry mat, Dictionary<string, string> removeKeys,
                                     List<string> kept, List<string> refitUsers, List<string> portUsers, List<Regenerated> regenerated)
    {
        var users = regenerated.Where(r => Draws(r, mat)).ToList();
        if (users.Count > 0)
        {
            foreach (var u in users)
            {
                var into = u.Port ? portUsers : refitUsers;
                if (!into.Contains(u.Rel, StringComparer.OrdinalIgnoreCase)) into.Add(u.Rel);
            }
            return false;
        }
        foreach (var p in mat.GamePaths) removeKeys[p] = mat.File;
        if (Edited(modRoot, mat.File, mat.Sha8) && mat.Sha8.Length > 0) kept.Add(mat.File); else TryDelete(Full(modRoot, mat.File));
        if (mat.Source.Tex is { } tex)
        {
            removeKeys[tex.GamePath] = tex.File;
            if (Edited(modRoot, tex.File, tex.Sha8) && tex.Sha8.Length > 0) kept.Add(tex.File); else TryDelete(Full(modRoot, tex.File));
        }
        return true;
    }

    /// <summary>
    /// <paramref name="bytes"/> (the file at <paramref name="rel"/>) as it was before our matcap: the backup while the
    /// file is still as the matcap wrote it, else the bytes themselves. What the auto refit compares a partner by.
    /// </summary>
    internal static byte[] AsWrittenBeforeMatcap(string modRoot, string rel, byte[] bytes)
    {
        try
        {
            var record = MatcapRecord.Read(modRoot);
            var backup = BackupPath(modRoot, BackupSubdir, rel);
            return !record.Unreadable && record.Model(rel) is { } me && me.WrittenSha8s.Contains(HairPortPlan.Sha8(bytes))
                   && File.Exists(backup) ? File.ReadAllBytes(backup) : bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return bytes;
        }
    }

    /// <summary>A file Proteus remakes from a source (a refit, or a hair port / earless copy when <see cref="Port"/>)
    /// naming at least one matcap material, with the material names it uses — and, for a file at a hairstyle's game path,
    /// that hairstyle's race and number (0 otherwise).</summary>
    private readonly record struct Regenerated(string Rel, HashSet<string> Names, bool Port, ushort Race = 0, int Id = 0);

    /// <summary>
    /// Whether <paramref name="r"/> draws <paramref name="mat"/>: it names it, or — a hair port renames its materials for
    /// its own race (c0201… → c0401…) — one of its names is loaded, for that race's hairstyle, from a game path the
    /// matcap publishes (#87: the game reads a race's hair material from the folder its hairstyle shares).
    /// </summary>
    private static bool Draws(Regenerated r, MatcapRecord.MaterialEntry mat)
        => r.Names.Contains(mat.Name)
           || r.Race > 0 && r.Names.Any(n => HairMaterialPath.GamePath(r.Race, r.Id, n) is { } gp
                                             && mat.GamePaths.Contains(gp, StringComparer.OrdinalIgnoreCase));

    /// <summary>The hairstyle a file of the mod stands for, from the game path its rel ends in (a port's place:
    /// <c>Hair Ports/&lt;option&gt;/chara/human/c0401/obj/hair/h0108/model/c0401h0108_hir.mdl</c>).</summary>
    private static (ushort Race, int Id)? HairOf(string rel)
    {
        int at = rel.IndexOf("chara/", StringComparison.OrdinalIgnoreCase);
        return at >= 0 ? HairPortPlan.Parse(rel[at..]) : null;
    }

    /// <summary>The mod's remade files on disk that name a matcap material: its refits (the refit record's, and any under
    /// the refit folder), its hair ports (under <see cref="HairPortPlan.Folder"/>) and its earless copies (under
    /// <see cref="EarlessHairPlan.Folder"/>) — #87: a port or copy remade from a matcapped hairstyle names its material.</summary>
    private static List<Regenerated> RegeneratedNames(string modRoot)
    {
        var refitFiles = SizeSetRefit.OurFiles(BodyRetargetWriter.ReadRecord(modRoot));
        AddModels(modRoot, BodyRetargetWriter.Subfolder, refitFiles);
        var portFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddModels(modRoot, HairPortPlan.Folder, portFiles);
        AddModels(modRoot, EarlessHairPlan.Folder, portFiles);
        var found = new List<Regenerated>();
        var all = refitFiles.Select(r => (Rel: r, Port: false)).Concat(portFiles.Select(r => (Rel: r, Port: true)));
        foreach (var (rel, port) in all.OrderBy(x => x.Rel, StringComparer.Ordinal))
            if (NamesIn(modRoot, rel) is { Count: > 0 } names && names.Any(n => n.Contains("_mcap", StringComparison.OrdinalIgnoreCase)))
                found.Add(port && HairOf(rel) is { } hair ? new Regenerated(rel, names, true, hair.Race, hair.Id)
                                                          : new Regenerated(rel, names, port));
        return found;
    }

    /// <summary>Every <c>.mdl</c> under <paramref name="folder"/> of the mod, as normalised rels, into <paramref name="into"/>.</summary>
    private static void AddModels(string modRoot, string folder, ISet<string> into)
    {
        var dir = Path.Combine(modRoot, folder);
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.mdl", SearchOption.AllDirectories))
                    into.Add(RefitCore.Normal(Path.GetRelativePath(modRoot, f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A refit or hair port Proteus wrote again since our matcap went on (a remake from its source): not as we wrote it, and not an
    /// edit on top of ours either — the remake replaced the file whole, so its old backup is stale.
    /// </summary>
    /// (A refit or a hair port.) A remake writes the file from its source, so it never carries the file's own matcaps: one still naming one
    /// was edited on top (the brush, a hand edit) and keeps the old path — its backup, the later-feature order, a refusal.
    private static bool RemadeRegenerated(string modRoot, string rel, MatcapRecord record, MatcapRecord.ModelEntry known, byte[] bytes)
    {
        if (known.WrittenSha8s.Contains(HairPortPlan.Sha8(bytes)) || MatcapPlan.Regenerated(modRoot, rel) is not (MatcapPlan.RefitKind or MatcapPlan.HairPortKind))
            return false;
        var names = TryNames(bytes);
        return names != null && !record.MaterialsOf(rel).Any(m => names.Contains(m.Name));
    }

    /// <summary>
    /// <paramref name="rel"/>'s apply group: it plus every model sharing one of the record's materials with it, closed
    /// over. What an Undo restores together, and what the panel and the harness show as "the group".
    /// </summary>
    public static HashSet<string> ApplyGroupOf(MatcapRecord record, string rel)
    {
        var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record.Model(rel)?.File ?? rel };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var mat in record.Materials)
                if (mat.Meshes.Any(x => group.Contains(x.Model)))
                    foreach (var x in mat.Meshes)
                        grew |= group.Add(x.Model);
        }
        return group;
    }

    /// <summary>The record's materials drawn in <paramref name="rel"/>'s apply group.</summary>
    public static List<MatcapRecord.MaterialEntry> GroupMaterials(MatcapRecord record, string rel)
    {
        var group = ApplyGroupOf(record, rel);
        return record.Materials.Where(m => m.Meshes.Any(x => group.Contains(x.Model))).ToList();
    }

    /// <summary>A backup no longer needed for an undo is retired, not deleted: moved to <see cref="SupersededSubdir"/>.</summary>
    /// <returns>False when the backup is still in place (the move failed).</returns>
    private static bool Retire(string modRoot, string rel)
    {
        var from = BackupPath(modRoot, BackupSubdir, rel);
        if (!File.Exists(from)) return true;
        var to = BackupPath(modRoot, SupersededSubdir, rel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
            return true;
        }
        catch { return false; }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Outcome HandEdited(string file) => Outcome.Refuse(CodeEdited, string.Format(ProteusLoc.Localize("Matcap.Refused.HandEdited.Fmt",
        "{0} was edited by hand since Proteus wrote it, so it was left alone."), file));

    internal static string Full(string modRoot, string rel) => Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

    internal static string BackupPath(string modRoot, string subdir, string rel) =>
        Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, subdir, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

    private static void Place(string modRoot, string rel, byte[] bytes, List<string> created)
    {
        var full = Full(modRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        bool existed = File.Exists(full);
        PenumbraModMeta.AtomicWrite(full, bytes);
        if (!existed) created.Add(rel);
    }

    private static bool Edited(string modRoot, string rel, string sha8)
    {
        var full = Full(modRoot, rel);
        try { return File.Exists(full) && sha8.Length > 0 && HairPortPlan.Sha8(File.ReadAllBytes(full)) != sha8; }
        catch { return true; }
    }

    private static HashSet<string>? NamesIn(string modRoot, string rel)
    {
        var full = Full(modRoot, rel);
        try { return File.Exists(full) ? TryNames(File.ReadAllBytes(full)) : null; }
        catch { return null; }
    }

    private static HashSet<string>? TryNames(byte[] mdl)
    {
        try { return new HashSet<string>(SecondSkinWriter.MaterialNames(mdl), StringComparer.OrdinalIgnoreCase); }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
