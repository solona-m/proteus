using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CheapLoc;
using Dalamud.Plugin.Services;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Packs a Proteus mod folder as a Penumbra <c>.pmp</c> — the Export tab's engine, and the inverse of
/// <see cref="OnionImportService"/>.
/// <para/>
/// A <c>.pmp</c> is nothing more than a zip of the mod ROOT (see "For Creators.md", Distributing Your
/// Mod): <c>meta.json</c> at the top level, the redirect manifest beside it, and the whole
/// <c>Proteus/</c> sidecar underneath. Because the folder is copied verbatim rather than transformed,
/// the export is lossless — colour tables, masks, glow effects, gear shells and option groups all
/// survive, and the recipient's Proteus discovers the sidecar exactly as the author's did.
/// </summary>
public sealed class ModExportService
{
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;

    /// <summary>The extension Penumbra imports directly.</summary>
    public const string Extension = ".pmp";

    /// <summary>
    /// Files that must never ship. <c>*.tmp</c> is the important one: <see cref="PenumbraModMeta.AtomicWrite"/>
    /// writes <c>meta.json.&lt;guid&gt;.tmp</c> siblings, and an interrupted write leaves one behind — shipping
    /// it would put a half-written manifest in someone else's mod folder. The rest is OS noise that means
    /// nothing on another machine.
    /// </summary>
    private static bool IsExcluded(string fileName)
        => fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "Thumbs.db", StringComparison.OrdinalIgnoreCase);

    public ModExportService(PenumbraBridge penumbra, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.log = log;
    }

    /// <summary>
    /// A reference type, unlike <see cref="ModCreationService.CreateResult"/>: the export runs on the
    /// thread pool and the UI picks the result up through a <c>volatile</c> field, which a struct can't be.
    /// </summary>
    public sealed record ExportResult(bool Ok, string Message);

    /// <summary>
    /// The filename to pre-fill the save dialog with: the mod's display name, reduced to characters a
    /// filesystem accepts, plus <c>.pmp</c>. Falls back to the mod's directory name when the display name
    /// sanitises away to nothing (a mod called "★" would otherwise offer an empty filename).
    /// </summary>
    public static string SuggestedFileName(OverlayEntry entry)
        => (ModCreationService.Sanitize(entry.ModName) ?? ModCreationService.Sanitize(entry.ModDirectory)
            ?? "mod") + Extension;

    /// <summary>
    /// Write <paramref name="entry"/>'s mod folder to <paramref name="targetPath"/> as a .pmp. Safe off the
    /// framework thread — the one Penumbra call is the mod-root lookup, which discovery already makes from
    /// a background thread. Returns a user-facing result; nothing is left behind when it fails.
    /// </summary>
    public ExportResult Export(OverlayEntry entry, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return new(false, Loc.Localize("Export.Error.NoPath", "Pick somewhere to save the file."));

        // The dialog's default extension only applies when the user leaves the name alone; someone who
        // types "Ven" gets a file Penumbra won't offer to install.
        if (!targetPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            targetPath += Extension;

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return new(false, Loc.Localize("Service.NoPenumbraDir", "Penumbra's mod directory isn't available."));

        var modRoot = Path.Combine(modsRoot, entry.ModDirectory);
        if (!Directory.Exists(modRoot))
            return new(false, string.Format(
                Loc.Localize("Export.Error.FolderGone.Fmt", "The mod folder is gone: {0}"), modRoot));

        try
        {
            var count = WritePmp(modRoot, targetPath);
            if (count == 0)
                return new(false, Loc.Localize("Export.Error.EmptyFolder",
                    "That mod folder is empty — there's nothing to export."));

            var size = new FileInfo(targetPath).Length / 1024f / 1024f;
            log.Information("[Proteus] exported {0} -> {1} ({2} file(s), {3:0.#} MB)",
                entry.ModDirectory, targetPath, count, size);
            // "{1} files" rather than English's inline "file(s)": a count glued to a noun has no correct
            // translation into Russian (three plural forms) or Japanese (none), so the count is labelled
            // instead of inflected.
            return new(true, string.Format(
                Loc.Localize("Export.Ok.Fmt", "Exported \"{0}\" — files: {1}, {2} MB.\n{3}"),
                entry.ModName, count, size.ToString("0.#"), targetPath));
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] export failed for {0}", entry.ModDirectory);
            return new(false, string.Format(
                Loc.Localize("Export.Error.WriteFailed.Fmt", "Couldn't write the file: {0}"), ex.Message));
        }
    }

    /// <summary>
    /// Zip <paramref name="modRoot"/>'s contents to <paramref name="targetPath"/> and return how many files
    /// went in. Pure filesystem work, so it can be exercised offline against a temp directory.
    /// <para/>
    /// Hand-walked rather than <see cref="ZipFile.CreateFromDirectory(string,string)"/> because entries have
    /// to be filtered (see <see cref="IsExcluded"/>) and because the names must be root-relative with
    /// forward slashes — Penumbra expects <c>meta.json</c> at the archive root, not nested inside a folder
    /// named after the mod.
    /// <para/>
    /// Writes to a <c>.tmp</c> sibling and moves it into place, so overwriting an existing pack can't
    /// truncate it if the write fails partway. That suffix is also what makes exporting INTO the mod folder
    /// harmless: the archive being written is excluded from its own walk.
    /// </summary>
    internal static int WritePmp(string modRoot, string targetPath)
    {
        var full = Path.GetFullPath(modRoot);
        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(Path.GetFileName(f)))
            .ToList();
        if (files.Count == 0) return 0;

        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = new ZipArchive(File.Create(tmp), ZipArchiveMode.Create))
                foreach (var file in files)
                    zip.CreateEntryFromFile(file, EntryName(full, file), CompressionLevel.Optimal);

            File.Move(tmp, targetPath, overwrite: true);
            tmp = null!;   // moved — nothing left to clean up
            return files.Count;
        }
        finally
        {
            if (tmp != null) try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Archive-relative entry name: forward slashes, no leading separator, no root folder.</summary>
    private static string EntryName(string modRootFull, string filePath)
        => Path.GetRelativePath(modRootFull, filePath).Replace('\\', '/');
}
