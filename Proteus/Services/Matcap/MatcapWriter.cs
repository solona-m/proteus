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

    /// <summary>One model file the apply rewrites: the bytes planned against, and the split.</summary>
    public sealed record ModelEdit(string Rel, string GamePath, byte[] SourceBytes, byte[] NewBytes, bool Lod0Cut,
                                   IReadOnlyList<(int Source, int Drawn)> Meshes);

    /// <summary>Everything an apply needs, prepared on a worker (<see cref="MatcapStudio"/>).</summary>
    public sealed record ApplyRequest(
        string Token, string MaterialName, byte[] MaterialBytes, IReadOnlyList<string> MaterialGamePaths,
        string Normal, float? AlphaThreshold, IReadOnlyList<ModelEdit> Models, MatcapRecord.SourceRef Source,
        string? ImageTexTemp, string MaskTemp, string? FlatNormalTemp);

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
            var entry = new MatcapRecord.MaterialEntry
            {
                Name = req.MaterialName, File = MaterialFile(req.MaterialName), GamePaths = [.. req.MaterialGamePaths],
                Normal = req.Normal, AlphaThreshold = req.AlphaThreshold, Pending = true, Source = req.Source,
                Meshes = req.Models.SelectMany(m => m.Meshes.Select(x => new MatcapRecord.MeshRef { Model = m.Rel, Mesh = x.Drawn, From = x.Source })).ToList(),
            };
            try
            {
                // Backups, the first time a model is edited only: the original is the original.
                foreach (var m in req.Models)
                {
                    var backup = BackupPath(modRoot, BackupSubdir, m.Rel);
                    if (File.Exists(backup)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(Full(modRoot, m.Rel), backup);
                    backups.Add(backup);
                }

                record.Materials.Add(entry);
                foreach (var m in req.Models)
                    if (record.Model(m.Rel) == null)
                        record.Models.Add(new MatcapRecord.ModelEntry { File = m.Rel, GamePath = m.GamePath });
                MatcapRecord.Write(modRoot, record);
                AfterStep?.Invoke("pending");

                // Files: the material, the baked texture, and the shared mask and flat normal on first need.
                Place(modRoot, entry.File, req.MaterialBytes, created);
                entry.Sha8 = HairPortPlan.Sha8(req.MaterialBytes);
                foreach (var p in req.MaterialGamePaths) addedKeys[p] = entry.File;

                if (req.Source.Tex is { } tex && req.ImageTexTemp != null)
                {
                    var bytes = File.ReadAllBytes(req.ImageTexTemp);
                    Place(modRoot, tex.File, bytes, created);
                    tex.Sha8 = HairPortPlan.Sha8(bytes);
                    addedKeys[tex.GamePath] = tex.File;
                }
                if (record.Shared.Mask == null || !File.Exists(Full(modRoot, record.Shared.Mask.File)))
                {
                    var bytes = File.ReadAllBytes(req.MaskTemp);
                    Place(modRoot, MaskFile(record.Token), bytes, created);
                    record.Shared.Mask = new MatcapRecord.FileRef { File = MaskFile(record.Token), GamePath = MaskGamePath(record.Token), Sha8 = HairPortPlan.Sha8(bytes) };
                    addedKeys[record.Shared.Mask.GamePath] = record.Shared.Mask.File;
                }
                if (req.FlatNormalTemp != null && (record.Shared.FlatNormal == null || !File.Exists(Full(modRoot, record.Shared.FlatNormal.File))))
                {
                    var bytes = File.ReadAllBytes(req.FlatNormalTemp);
                    Place(modRoot, FlatNormalFile(record.Token), bytes, created);
                    record.Shared.FlatNormal = new MatcapRecord.FileRef { File = FlatNormalFile(record.Token), GamePath = FlatNormalGamePath(record.Token), Sha8 = HairPortPlan.Sha8(bytes) };
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
                    var me = record.Model(m.Rel)!;
                    me.WrittenSha8s.Add(HairPortPlan.Sha8(m.NewBytes));
                    me.Lod0Cut |= m.Lod0Cut;
                }
                AfterStep?.Invoke("model");

                entry.Pending = false;
                MatcapRecord.Write(modRoot, record);
                return Outcome.Done(string.Format(ProteusLoc.Localize("Parts.Matcap.Applied.Fmt", "Matcap applied: {0} on {1} model file(s)."),
                    StemOf(req.MaterialName), req.Models.Count), [.. req.Models.Select(m => m.Rel), .. created]);
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
                        fresh.Materials.RemoveAll(x => string.Equals(x.Name, req.MaterialName, StringComparison.OrdinalIgnoreCase));
                        fresh.Models.RemoveAll(x => !fresh.MaterialsOf(x.File).Any() && req.Models.Any(r => PenumbraModMeta.SameFile(r.Rel, x.File)) && x.WrittenSha8s.Count == 0);
                        if (fresh.Shared.Mask is { } mk && created.Contains(mk.File)) fresh.Shared.Mask = null;
                        if (fresh.Shared.FlatNormal is { } fn && created.Contains(fn.File)) fresh.Shared.FlatNormal = null;
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
    public static Outcome Undo(string modRoot, string rel) => UndoOrForget(modRoot, rel, forgetOnly: false);

    /// <summary>Forget matcaps the model no longer names (the author updated it): retire their backups, remove their
    /// files and redirects. Changes no model.</summary>
    public static Outcome Forget(string modRoot, string rel) => UndoOrForget(modRoot, rel, forgetOnly: true);

    private static Outcome UndoOrForget(string modRoot, string rel, bool forgetOnly)
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

            // ── reconcile: a material no recorded model still names is forgotten with its own files ──
            foreach (var mat in record.Materials.ToList())
            {
                bool named = mat.Meshes.Select(x => x.Model).Distinct(StringComparer.OrdinalIgnoreCase)
                                .Any(m => NamesIn(modRoot, m)?.Contains(mat.Name) == true);
                if (named) continue;
                DropMaterial(modRoot, mat, removeKeys, kept);
                record.Materials.Remove(mat);
            }

            // ── the group: this model plus every model sharing a remaining material with it ──
            var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record.Model(rel)?.File ?? rel };
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var mat in record.Materials)
                    if (mat.Meshes.Any(x => group.Contains(x.Model)))
                        foreach (var x in mat.Meshes)
                            grew |= group.Add(x.Model);
            }

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
                if (forgetOnly) continue;
                if (ModelBackupOrder.LaterFeature(modRoot, BackupSubdir, g) is { } later)   // (a) a later feature
                    return Outcome.Refuse(CodeLaterFeature, ModelBackupOrder.BlockedMessage(later));
                var sha = HairPortPlan.Sha8(File.ReadAllBytes(full));
                if (record.Model(g) is { } me && me.WrittenSha8s.Contains(sha) && File.Exists(backup))
                    restore.Add((g, backup));                                               // (b) ours: restore it
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
            foreach (var g in forget) Retire(modRoot, g);

            var gone = new HashSet<string>(restore.Select(r => r.Rel).Concat(forget), StringComparer.OrdinalIgnoreCase);
            foreach (var mat in record.Materials.ToList())
            {
                // A material leaves with its models: every model it draws in was restored or forgotten.
                if (!mat.Meshes.All(x => gone.Contains(x.Model))) continue;
                DropMaterial(modRoot, mat, removeKeys, kept);
                record.Materials.Remove(mat);
            }
            record.Models.RemoveAll(m => gone.Contains(m.File));

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
            MatcapRecord.Write(modRoot, record);

            var msg = forgetOnly
                ? string.Format(ProteusLoc.Localize("Parts.Matcap.Forgotten.Fmt", "Forgot the matcaps this model no longer uses ({0} file(s))."), forget.Count)
                : string.Format(ProteusLoc.Localize("Parts.Matcap.Undone.Fmt", "Matcaps undone on {0} model file(s)."), restore.Count + forget.Count);
            if (kept.Count > 0)
                msg += " " + string.Format(ProteusLoc.Localize("Parts.Matcap.KeptEdited.Fmt", "Left in place because they were edited by hand: {0}."),
                    string.Join(", ", kept.Distinct()));
            return Outcome.Done(msg, touched);
        }
    }

    private static void DropMaterial(string modRoot, MatcapRecord.MaterialEntry mat, Dictionary<string, string> removeKeys, List<string> kept)
    {
        foreach (var p in mat.GamePaths) removeKeys[p] = mat.File;
        if (Edited(modRoot, mat.File, mat.Sha8) && mat.Sha8.Length > 0) kept.Add(mat.File); else TryDelete(Full(modRoot, mat.File));
        if (mat.Source.Tex is { } tex)
        {
            removeKeys[tex.GamePath] = tex.File;
            if (Edited(modRoot, tex.File, tex.Sha8) && tex.Sha8.Length > 0) kept.Add(tex.File); else TryDelete(Full(modRoot, tex.File));
        }
    }

    /// <summary>A backup no longer needed for an undo is retired, not deleted: moved to <see cref="SupersededSubdir"/>.</summary>
    private static void Retire(string modRoot, string rel)
    {
        var from = BackupPath(modRoot, BackupSubdir, rel);
        if (!File.Exists(from)) return;
        var to = BackupPath(modRoot, SupersededSubdir, rel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
        catch { }
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
