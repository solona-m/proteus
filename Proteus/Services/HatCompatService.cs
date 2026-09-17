using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// What Proteus changed in a hair mod to make it fit under a hat, so the edit can be undone. Kept as
/// <c>Proteus/hatcompat.json</c> INSIDE the mod, beside the backups it names.
/// </summary>
internal sealed class HatCompatRecord
{
    /// <summary>Model files that were edited, relative to the mod root, with forward slashes.</summary>
    [JsonPropertyName("Files")] public List<string> Files { get; set; } = [];

    /// <summary>Submeshes tagged to vanish under a hat, as "mesh.submesh", per file.</summary>
    [JsonPropertyName("Hidden")] public Dictionary<string, List<string>> Hidden { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Which <see cref="HatCompatSolve.Version"/> patched each file; missing means before stamping. Per FILE,
    /// since a hair pack's hairstyles are patched as they are worn, possibly months apart.
    /// </summary>
    [JsonPropertyName("Versions")] public Dictionary<string, int> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Makes a hairstyle hat-compatible in place by writing native data into the mod: a <c>shp_hib</c> shape key
/// and the <c>atr_kam</c> attribute. Every file is backed up before its first edit, and every model is patched
/// IN MEMORY and only written once all have succeeded, so the mod is never left half-edited.
/// </summary>
public static class HatCompatService
{
    public const string RecordFile = "hatcompat.json";
    public const string BackupSubdir = "hatcompat-backup";

    /// <summary>The shape the game blends to while a head piece is worn.</summary>
    public const string HatShape = "shp_hib";

    /// <summary>"Scalp" — the attribute the game drops entirely under a head piece.</summary>
    public const string ScalpAttribute = "atr_kam";

    /// <summary>
    /// The one spelling of a mod-relative path this class compares, records and looks up by: forward slashes.
    /// Every comparison here is a plain string compare, so one file under two spellings would be two files.
    /// Not <see cref="Native"/>, which goes the other way.
    /// </summary>
    internal static string Rel(string rel) => rel.Replace('\\', '/');

    /// <param name="FilesPatched">How many model files were changed.</param>
    /// <param name="Unaddressable">Vertices the press wanted to move that no shape value could name (see
    /// <see cref="ModelAttributeWriter.AddShape"/>).</param>
    public sealed record Outcome(bool Ok, string Message, int FilesPatched, int Unaddressable = 0);

    /// <summary>
    /// What Proteus proposes to do to one hair model, before anything is written, so the panel can describe it.
    /// </summary>
    /// <param name="Unmeasurable">The wearer's head could not be read, so there is no skull to fit against
    /// and Proteus does nothing. See <see cref="Inspect"/>.</param>
    /// <param name="TookOver">The hairstyle's own hat support measured as hiding hair no hat covers, so
    /// Proteus replaces its <c>atr_kam</c> mask and LEAVES its shape alone (see
    /// <see cref="HatCompatSolve.MeasureScalpTagging"/>).</param>
    public sealed record Proposal(
        string Rel,
        ModelParts Parts,
        HatCompatSolve.Result Solve,
        bool AlreadyCompatible,
        bool Unmeasurable = false,
        bool TookOver = false);

    /// <summary>The hair Proteus would patch, and the head it would press it against.</summary>
    /// <param name="ModRoot">The mod folder that supplies the hair (directly under Penumbra's mods root), the
    /// unit a backup and a record belong to.</param>
    /// <param name="Rel">The hair file's path inside that mod, with forward slashes, as a manifest names it.</param>
    /// <param name="Head">The wearer's face model, already read, or null when it could not be.</param>
    public sealed record Target(string GamePath, string ModRoot, string Rel, byte[] Model, byte[]? Head);

    /// <summary>
    /// Which hair file to patch, from the models the character is actually drawing, resolved through Penumbra.
    /// Returns null for VANILLA hair or a file outside the mods folder.
    /// </summary>
    /// <param name="resolve">Penumbra's game-path-to-file resolver, honouring the active collection.</param>
    /// <param name="readFile">Reads any game file, from the mod redirect if there is one and from the
    /// game's own data otherwise (<c>TextureLoader.LoadRawFile</c>). REQUIRED to find a vanilla face.</param>
    public static Target? FindEquippedHair(
        IReadOnlyList<string>? humanPartModels, Func<string, string?> resolve, string? modsRoot,
        Func<string, byte[]?> readFile)
    {
        if (Locate(humanPartModels, resolve, modsRoot) is not { } at) return null;
        var (hair, file, modRoot, rel) = at;

        byte[] model;
        try { model = File.ReadAllBytes(file); } catch (IOException) { return null; }

        // The face model carries the cranium the press aims at; it MUST be read from game data when no mod
        // supplies one (Penumbra returns an unmodded path unchanged, which is not a file on disk).
        byte[]? head = null;
        var facePath = humanPartModels?.FirstOrDefault(
            p => p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase));
        if (facePath != null)
            try { head = readFile(facePath); } catch (IOException) { /* refuses to fit */ }

        return new Target(hair, modRoot, rel, model, head);
    }

    /// <summary>Which file currently serves the equipped hairstyle, without reading it.</summary>
    private static (string GamePath, string File, string ModRoot, string Rel)? Locate(
        IReadOnlyList<string>? humanPartModels, Func<string, string?> resolve, string? modsRoot)
    {
        if (humanPartModels == null || modsRoot is not { Length: > 0 }) return null;

        var hair = humanPartModels.FirstOrDefault(p => p.Contains("/obj/hair/", StringComparison.OrdinalIgnoreCase));
        if (hair == null) return null;

        var file = resolve(hair);
        if (file == null || !File.Exists(file)) return null;
        if (!InMods(file, modsRoot, out var modRoot, out var rel)) return null;   // vanilla, or another source
        return (hair, file, modRoot, rel);
    }

    /// <summary>
    /// A cheap identity for the hairstyle on the character. The game path alone is NOT enough: switching the
    /// supplying mod swaps the file behind the same path. The file's length and timestamp also catch our own patch.
    /// </summary>
    public static string? EquippedHairKey(
        IReadOnlyList<string>? humanPartModels, Func<string, string?> resolve, string? modsRoot)
    {
        if (Locate(humanPartModels, resolve, modsRoot) is not { } at) return null;
        try
        {
            var info = new FileInfo(at.File);
            return $"{at.GamePath}|{at.File}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Split a resolved file into the mod folder that owns it (the FIRST segment below the mods root) and the
    /// path inside it. Compared case-insensitively on normalised full paths.
    /// </summary>
    internal static bool InMods(string file, string modsRoot, out string modRoot, out string rel)
    {
        modRoot = "";
        rel = "";
        string full, root;
        try
        {
            full = Path.GetFullPath(file);
            root = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = full[(root.Length + 1)..];
        int cut = tail.IndexOf(Path.DirectorySeparatorChar);
        if (cut <= 0 || cut == tail.Length - 1) return false;

        modRoot = Path.Combine(root, tail[..cut]);
        rel = tail[(cut + 1)..].Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    /// <summary>
    /// Whether this model already declares a hat shape, in which case its author has done this by hand. Reads
    /// the <c>Shape</c> records, not <see cref="SecondSkinWriter.Source.Shapes"/>, which drops non-LOD0 shapes.
    /// </summary>
    public static bool IsHatCompatible(byte[] mdl)
    {
        try { return ModelAttributeWriter.DeclaresShape(mdl, HatShape); }
        catch { return false; }
    }

    /// <summary>
    /// Every file in the mod that serves this same hairstyle: the one Penumbra resolves now, and any sibling an
    /// option would swap in, so the fit does not stop working when the wearer changes option.
    /// </summary>
    public static List<string> SiblingFiles(string modRoot, string gamePath, string rel)
    {
        // Canonicalised on the way in AND per manifest entry, or the dedupe misses. See Rel.
        var found = new List<string> { Rel(rel) };
        try
        {
            foreach (var r in PenumbraModMeta.ReadAllRedirects(modRoot))
            {
                if (!r.GamePath.Equals(gamePath, StringComparison.OrdinalIgnoreCase)) continue;
                var sibling = Rel(r.File);
                if (found.Contains(sibling, StringComparer.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(modRoot, Native(sibling)))) found.Add(sibling);
            }
        }
        catch (IOException) { /* the one we were given is still worth patching */ }
        return found;
    }

    /// <summary>
    /// How many files Proteus has patched anywhere in this mod, from the record. A hair mod ships one model per
    /// race, so this can exceed the files serving the worn hairstyle.
    /// </summary>
    public static int PatchedCount(string modRoot) => ReadRecord(modRoot)?.Files.Count ?? 0;

    /// <summary>Whether Proteus has already fitted this exact file, and if so whether that patch is current.</summary>
    /// <param name="stale">The patch was written by an older <see cref="HatCompatSolve.Version"/>.</param>
    public static bool IsPatched(string modRoot, string rel, out bool stale)
    {
        stale = false;
        rel = Rel(rel);
        var record = ReadRecord(modRoot);
        if (record?.Files.Contains(rel, StringComparer.OrdinalIgnoreCase) != true) return false;
        stale = (record.Versions.TryGetValue(rel, out var v) ? v : 0) < HatCompatSolve.Version;
        return true;
    }

    /// <summary>Work out what this hair needs, without writing anything.</summary>
    /// <param name="head">The wearer's face model. REQUIRED: null yields a proposal marked
    /// <see cref="Proposal.Unmeasurable"/> and nothing is fitted.</param>
    /// <param name="raceCode">The wearer's model code ("0801"), which picks the baked hat profile (see
    /// <see cref="HatProfile"/>).</param>
    public static Proposal? Inspect(byte[] mdl, string rel, byte[]? head, string? raceCode = null)
    {
        // Canonicalised here so everything downstream of a proposal (record, Revert) uses one spelling.
        rel = Rel(rel);
        var parts = ModelPartReader.Read(mdl);
        if (parts == null) return null;
        if (IsHatCompatible(mdl))
        {
            // Hat support of its own, but possibly a mask inherited from vanilla hair: measure what it costs.
            // Without a head it cannot be judged, and stands.
            var tagging = head != null ? HatCompatSolve.MeasureScalpTagging(mdl, head, raceCode) : null;
            if (tagging is { } t && t.HarmfulShare > HatCompatSolve.InheritedTagShare)
                return new Proposal(rel, parts, HatCompatSolve.Solve(mdl, parts, head, raceCode: raceCode), false,
                                    TookOver: true);

            return new Proposal(rel, parts, HatCompatSolve.Result.None, true);
        }

        // NO HEAD, NO FIT: a skull guessed from the hair is far off and cuts most of the hairstyle away.
        // Hat clipping is the better failure.
        if (head == null)
            return new Proposal(rel, parts, HatCompatSolve.Result.None, false, Unmeasurable: true);

        return new Proposal(rel, parts, HatCompatSolve.Solve(mdl, parts, head, raceCode: raceCode), false);
    }

    /// <summary>
    /// Write the shape and the tags into <paramref name="proposal"/>'s file inside <paramref name="modRoot"/>.
    /// </summary>
    /// <param name="extra">Further submeshes to tag alongside the cut; empty in the plugin, for tests.</param>
    public static Outcome Apply(string modRoot, byte[] mdl, Proposal proposal,
                                IReadOnlyList<ModelPart>? extra = null)
    {
        // Before the AlreadyCompatible check: without a skull nothing can be judged at all.
        if (proposal.Unmeasurable)
            return new Outcome(false, Loc.Localize("HatCompat.Apply.NoHead",
                "Proteus could not read your character's head, so it has not changed this hairstyle."), 0);

        if (proposal.AlreadyCompatible)
            return new Outcome(false, Loc.Localize("HatCompat.Apply.Already",
                "This hairstyle already has a hat shape of its own."), 0);
        // The geometry under the hat is dropped, which costs no shape values. Tagged in one pass so a submesh
        // claimed twice is cut once.
        var tag = (extra ?? []).Concat(proposal.Solve.Cut).ToList();

        if (proposal.Solve.Moved.Count == 0 && tag.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Apply.Nothing",
                "There is nothing to change: no hair stands proud of the scalp and no part needs hiding."), 0);

        byte[] patched;
        int unaddressable = 0;
        try
        {
            patched = mdl;
            if (tag.Count > 0)
            {
                // Clear whatever atr_kam the author had (usually inherited from vanilla hair) and replace it with
                // the cut. Only inside this branch, since clearing is defensible only when something replaces it.
                patched = ModelAttributeWriter.ClearAttribute(patched, ScalpAttribute);

                // Split the cut triangles out of their submeshes FIRST, since an attribute tags a whole submesh.
                // A split moves no vertex or index, so the press's mesh-relative numbers stay valid.
                var (split, targets) = ModelAttributeWriter.IsolateParts(patched, tag);
                patched = ModelAttributeWriter.AddAttribute(split, ScalpAttribute, targets);
            }
            // THE MASK ONLY, on a take-over: the model already declares shp_hib, and AddShape refuses a
            // duplicate name. The inherited mask is the harm; the author's press stays.
            if (proposal.TookOver)
            {
                if (tag.Count == 0)
                    return new Outcome(false, Loc.Localize("HatCompat.Apply.Nothing",
                        "There is nothing to change: no hair stands proud of the scalp and no part needs "
                      + "hiding."), 0);
            }
            else if (proposal.Solve.Moved.Count > 0)
            {
                patched = ModelAttributeWriter.AddShape(
                    patched, HatShape, proposal.Solve.Moved, out unaddressable);
            }
        }
        catch (ModelAttributeWriter.ModelEditException ex)
        {
            // Nothing is on disk yet.
            return new Outcome(false, string.Format(Loc.Localize("HatCompat.Apply.EditFailed.Fmt",
                "{0} could not be edited: {1}. Nothing has been written."), proposal.Rel, ex.Message), 0);
        }

        // ── from here on the mod is being changed ───────────────────────────
        try
        {
            Backup(modRoot, proposal.Rel);
            PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(proposal.Rel)), patched);

            var record = ReadRecord(modRoot) ?? new HatCompatRecord();
            if (!record.Files.Contains(proposal.Rel, StringComparer.OrdinalIgnoreCase))
                record.Files.Add(proposal.Rel);
            record.Hidden[proposal.Rel] = tag.Select(p => $"{p.Mesh}.{p.Submesh}").ToList();
            record.Versions[proposal.Rel] = HatCompatSolve.Version;
            WriteRecord(modRoot, record);
        }
        catch (Exception ex)
        {
            return new Outcome(false, string.Format(
                Loc.Localize("HatCompat.Apply.Failed.Fmt", "Writing failed: {0}"), ex.Message), 0);
        }

        return new Outcome(true, "", 1, unaddressable);
    }

    /// <summary>
    /// Put every patched model back the way its author shipped it. Backups are deleted only once restored, so a
    /// partial failure can be retried.
    /// </summary>
    /// <param name="only">One file to undo, or null for every file Proteus patched in this mod.</param>
    public static Outcome Revert(string modRoot, string? only = null)
    {
        var record = ReadRecord(modRoot);
        if (record == null || record.Files.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Revert.Nothing",
                "This mod has no Proteus hat-compatibility edits to undo."), 0);

        var target = only != null ? Rel(only) : null;
        var wanted = record.Files.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => target == null || f.Equals(target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (wanted.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Revert.Nothing",
                "This mod has no Proteus hat-compatibility edits to undo."), 0);

        // Refused before anything is restored, while a feature that edited the same model LATER still holds
        // its own backup of it — see ModelBackupOrder. Restoring ours would silently undo that edit too.
        foreach (var rel in wanted)
            if (ModelBackupOrder.LaterFeature(modRoot, BackupSubdir, rel) is { } later)
                return new Outcome(false, ModelBackupOrder.BlockedMessage(later), 0);

        var restoredFrom = new List<string>();
        var skipped = new List<string>();
        foreach (var rel in wanted)
        {
            var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
            if (!File.Exists(backup)) { skipped.Add(rel); continue; }
            try
            {
                PenumbraModMeta.AtomicWrite(Path.Combine(modRoot, Native(rel)), File.ReadAllBytes(backup));
                restoredFrom.Add(backup);
                record.Files.RemoveAll(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));
                record.Hidden.Remove(rel);
                record.Versions.Remove(rel);
            }
            catch { skipped.Add(rel); }
        }

        if (restoredFrom.Count == 0)
            return new Outcome(false, Loc.Localize("HatCompat.Revert.NoBackups",
                "None of the original models could be found to restore."), 0);

        // The record is deleted only when empty; otherwise rewritten to keep naming the remaining backups.
        var recordPath = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
        try
        {
            if (record.Files.Count == 0) File.Delete(recordPath);
            else WriteRecord(modRoot, record);
        }
        catch { /* the models are already back; a stale record only over-reports */ }

        foreach (var backup in restoredFrom)
            try { File.Delete(backup); } catch { /* harmless leftover */ }

        return new Outcome(true, "", restoredFrom.Count);
    }

    // ── the record ──────────────────────────────────────────────────────────

    /// <summary>
    /// Read the record with every path canonicalised. Dictionaries are rebuilt with explicit comparers because
    /// System.Text.Json replaces them wholesale, discarding the initializers' comparers.
    /// </summary>
    internal static HatCompatRecord? ReadRecord(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, RecordFile);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<HatCompatRecord>(File.ReadAllText(path));
            if (record == null) return null;

            // Distinct AFTER canonicalising, so two spellings of one file collapse to one entry.
            record.Files = record.Files.Select(Rel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var hidden = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in record.Hidden) hidden[Rel(k)] = v;
            record.Hidden = hidden;

            // The LOWEST of two colliding stamps: wrongly refitting a current patch is harmless, wrongly
            // keeping a stale one is permanent.
            var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in record.Versions)
            {
                var key = Rel(k);
                versions[key] = versions.TryGetValue(key, out var had) ? Math.Min(had, v) : v;
            }
            record.Versions = versions;

            return record;
        }
        catch { return null; }
    }

    private static void WriteRecord(string modRoot, HatCompatRecord record)
    {
        var dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(dir);
        PenumbraModMeta.AtomicWrite(Path.Combine(dir, RecordFile),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Copy a model aside before its FIRST edit only, so revert restores the author's file rather than an
    /// earlier patch.
    /// </summary>
    private static void Backup(string modRoot, string rel)
    {
        var backup = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, BackupSubdir, Native(rel));
        if (File.Exists(backup)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(Path.Combine(modRoot, Native(rel)), backup);
    }

    private static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);
}
