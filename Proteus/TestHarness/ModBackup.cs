using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Proteus.TestHarness;

/// <summary>A backup refused or failed, with the job error code it becomes.</summary>
internal sealed class BackupException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>What a backup is a copy of: the mod before a refit, or the mod as a restore found it.</summary>
internal enum BackupKind { Refit, PreRestore }

/// <summary>A backup's <c>meta.json</c>. Written last, atomically: a folder without it (complete) is incomplete.</summary>
internal sealed record BackupMeta(bool Complete, BackupKind Kind, string Session, long Seq, DateTime CreatedUtc,
                                  string? JobId, string Before);

/// <summary>A complete backup on disk.</summary>
internal sealed record BackupInfo(string ModDir, string Id, string Path, BackupMeta Meta)
{
    public string FilesPath => System.IO.Path.Combine(Path, ModBackup.FilesFolder);
}

/// <summary>
/// Whole-folder copies of a Penumbra mod under <c>&lt;ConfigDirectory&gt;\test-harness\backups\&lt;modDir&gt;\&lt;backupId&gt;\</c>,
/// and putting a mod back from one. The rules this class keeps, because the folders are the only way back for a
/// user's mod:
/// <list type="bullet">
/// <item>It never deletes a backup, and never writes into a folder it did not create and claim (an exclusive
/// <c>.owner</c> file). A failed copy deletes only the folder this call claimed.</item>
/// <item>A backup is complete only once its <c>meta.json</c> (complete: true) is written, last. An incomplete folder (a
/// crash mid-copy) is never selected or restored.</item>
/// <item>Copies keep last-write times: the plugin's feature undo orders backups inside the mod by them.</item>
/// <item>Restore copies only the backup's <c>files\</c> tree, so the mod ends with exactly the backup's files.</item>
/// <item>Every path is derived inside the backups root and checked to stay there.</item>
/// </list>
/// </summary>
internal sealed class ModBackup
{
    internal const string FilesFolder = "files";
    internal const string MetaFile = "meta.json";
    internal const string AfterFile = "after.json";
    internal const string OwnerFile = ".owner";
    internal const int MaxIdAttempts = 16;
    internal const long DefaultRefitCap = 2L * 1024 * 1024 * 1024;

    private static readonly Regex IdPattern = new(@"^\d{8}-\d{6}-[0-9a-f]{4}$", RegexOptions.CultureInvariant);

    private readonly string root;
    private readonly Func<string> newId;
    private readonly long refitCap;
    private readonly Func<string, long> freeSpace;

    /// <param name="backupsRoot"><c>&lt;ConfigDirectory&gt;\test-harness\backups</c>.</param>
    /// <param name="newId">The id source; UTC time plus 4 random hex digits unless a test stands in.</param>
    /// <param name="refitCap">Largest mod a refit backs up; the pre-restore snapshot has no cap.</param>
    /// <param name="freeSpace">Free bytes on the volume of a path.</param>
    public ModBackup(string backupsRoot, Func<string>? newId = null, long refitCap = DefaultRefitCap,
                     Func<string, long>? freeSpace = null)
    {
        root = Path.GetFullPath(backupsRoot);
        this.newId = newId ?? NewId;
        this.refitCap = refitCap;
        this.freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
    }

    public string Root => root;

    /// <summary>Test seam: runs after the folder was found free and before it is claimed — where another writer can race.</summary>
    internal Action<string>? BetweenCheckAndClaim { get; set; }

    internal static string NewId()
        => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-"
         + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    /// <summary>A mod folder name that is one plain segment; anything else could escape the backups root.</summary>
    internal static bool SafeModDir(string modDir)
        => modDir.Length > 0 && modDir != "." && modDir != ".." && modDir.IndexOfAny(['/', '\\', ':']) < 0
           && modDir.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && modDir.Trim() == modDir;

    /// <summary>The folder for a mod's backups, inside the root; throws for a name that would leave it.</summary>
    internal string ModFolder(string modDir)
    {
        if (!SafeModDir(modDir)) throw new BackupException("backup-failed", $"unsafe mod folder name '{modDir}'");
        string path = Path.GetFullPath(Path.Combine(root, modDir));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BackupException("backup-failed", $"mod folder '{modDir}' escapes the backups root");
        return path;
    }

    /// <summary>A backup's folder; null for an id that is not one of ours (it could otherwise name any path).</summary>
    internal string? BackupFolder(string modDir, string id)
    {
        if (!IdPattern.IsMatch(id)) return null;
        string path = Path.GetFullPath(Path.Combine(ModFolder(modDir), id));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>Total bytes of every file under a folder.</summary>
    internal static long SizeOf(string folder)
        => new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    /// <summary>
    /// Copy a mod folder into a new backup. Worker thread. Throws <see cref="BackupException"/> with the job error code:
    /// <c>backup-too-large</c> (a refit over the cap), <c>insufficient-space</c>, <c>backup-failed</c>, or
    /// <see cref="OperationCanceledException"/>. Nothing is ever written into an existing folder.
    /// </summary>
    public BackupInfo Create(string modRoot, string modDir, BackupKind kind, string session, long seq, string? jobId,
                             CancellationToken ct)
    {
        string modFolder = ModFolder(modDir);
        long size;
        try { size = SizeOf(modRoot); }
        catch (Exception e) { throw new BackupException("backup-failed", $"could not read the mod folder: {e.Message}", e); }
        if (kind == BackupKind.Refit && size > refitCap)
            throw new BackupException("backup-too-large", $"the mod is {size / 1048576} MiB, over the {refitCap / 1048576} MiB a refit backs up");
        long needed = (long)(size * 1.1) + 512L * 1024 * 1024;
        Directory.CreateDirectory(modFolder);
        if (freeSpace(modFolder) < needed)
            throw new BackupException("insufficient-space", $"backing up needs {needed / 1048576} MiB free");

        string folder = Claim(modDir);
        try
        {
            string before = Fingerprint(modRoot);
            CopyTree(modRoot, Path.Combine(folder, FilesFolder), ct);
            var meta = new BackupMeta(true, kind, session, seq, DateTime.UtcNow, jobId, before);
            WriteAtomic(Path.Combine(folder, MetaFile), HarnessJson.Bytes(meta));
            return new BackupInfo(modDir, Path.GetFileName(folder), folder, meta);
        }
        catch (Exception e)
        {
            // Only the folder this call claimed: never an existing backup.
            try { Directory.Delete(folder, recursive: true); } catch { /* left incomplete: ignored, deletable by hand */ }
            if (e is OperationCanceledException) throw;
            throw e as BackupException ?? new BackupException("backup-failed", $"the backup failed: {e.Message}", e);
        }
    }

    /// <summary>A new folder this call owns, at most <see cref="MaxIdAttempts"/> ids tried.</summary>
    private string Claim(string modDir)
    {
        for (int attempt = 0; attempt < MaxIdAttempts; attempt++)
        {
            string id = newId();
            string? folder = BackupFolder(modDir, id);
            if (folder == null) throw new BackupException("backup-failed", $"the id source gave '{id}', not a backup id");
            if (Directory.Exists(folder)) continue;
            BetweenCheckAndClaim?.Invoke(folder);
            Directory.CreateDirectory(folder);
            try
            {
                // CreateNew: whoever creates .owner owns the folder; anyone else leaves it untouched.
                using (new FileStream(Path.Combine(folder, OwnerFile), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return folder;
            }
            catch (IOException) when (File.Exists(Path.Combine(folder, OwnerFile)))
            {
                // Another writer claimed it between our check and our claim.
            }
        }
        throw new BackupException("backup-failed", $"no free backup id after {MaxIdAttempts} attempts");
    }

    private static void CopyTree(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, target, overwrite: false);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A folder's files as one comparable string: relative path, size and last-write time of each, hashed.</summary>
    internal static string Fingerprint(string folder)
    {
        var sb = new StringBuilder();
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                                                   .Select(f => (Rel: Path.GetRelativePath(folder, f.FullName).Replace('\\', '/').ToLowerInvariant(), f))
                                                   .OrderBy(x => x.Rel, StringComparer.Ordinal))
            sb.Append(f.Rel).Append('|').Append(f.f.Length).Append('|').Append(f.f.LastWriteTimeUtc.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Record the mod's state after a refit's run, for a later restore's <c>changedSinceJob</c>.</summary>
    public void WriteAfter(BackupInfo backup, string modRoot)
        => WriteAtomic(Path.Combine(backup.Path, AfterFile), HarnessJson.Bytes(new { after = Fingerprint(modRoot) }));

    /// <summary>Whether the mod changed since the refit's run: null when no after-state was recorded.</summary>
    public static bool? ChangedSince(BackupInfo backup, string modRoot)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(backup.Path, AfterFile)));
            return doc.RootElement.GetProperty("after").GetString() != Fingerprint(modRoot);
        }
        catch { return null; }
    }

    private static BackupMeta? ReadMeta(string folder)
    {
        try
        {
            var meta = JsonSerializer.Deserialize<BackupMeta>(File.ReadAllBytes(Path.Combine(folder, MetaFile)), HarnessJson.Options);
            return meta is { Complete: true } && Directory.Exists(Path.Combine(folder, FilesFolder)) ? meta : null;
        }
        catch { return null; }
    }

    /// <summary>Every complete backup of a mod. Incomplete folders are not listed.</summary>
    public List<BackupInfo> List(string modDir)
    {
        string folder = ModFolder(modDir);
        if (!Directory.Exists(folder)) return [];
        var result = new List<BackupInfo>();
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            string id = Path.GetFileName(dir);
            if (!IdPattern.IsMatch(id)) continue;
            if (ReadMeta(dir) is { } meta) result.Add(new BackupInfo(modDir, id, dir, meta));
        }
        return result;
    }

    /// <summary>One backup by id: the backup, or whether the folder exists but is incomplete.</summary>
    public (BackupInfo? Backup, bool Incomplete) Find(string modDir, string id)
    {
        if (BackupFolder(modDir, id) is not { } folder || !Directory.Exists(folder)) return (null, false);
        return ReadMeta(folder) is { } meta ? (new BackupInfo(modDir, id, folder, meta), false) : (null, true);
    }

    /// <summary>
    /// Ordering: within one session by <see cref="BackupMeta.Seq"/> (the true order even if the clock stepped back);
    /// across sessions by creation time, then seq. Never by the id string.
    /// </summary>
    internal static List<BackupInfo> InSessionOrder(IEnumerable<BackupInfo> backups)
        => backups.OrderBy(b => b.Meta.Seq).ToList();

    internal static List<BackupInfo> AcrossSessionsOrder(IEnumerable<BackupInfo> backups)
        => backups.OrderBy(b => b.Meta.CreatedUtc).ThenBy(b => b.Meta.Seq).ToList();

    /// <summary>
    /// Make <paramref name="modRoot"/> hold exactly the backup's files, byte for byte, last-write times included: copy
    /// the backup's <c>files\</c> tree over it, then delete the mod's files the backup lacks (the caller has taken a
    /// pre-restore snapshot, so they are kept there). Worker thread. Throws on failure, leaving a partial state that a
    /// retry puts right.
    /// </summary>
    public static (int Files, int Deleted) Restore(BackupInfo backup, string modRoot, CancellationToken ct)
    {
        string from = backup.FilesPath;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0, deleted = 0;
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(modRoot, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(from, file);
            wanted.Add(rel);
            string target = Path.Combine(modRoot, rel);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            files++;
        }
        foreach (var file in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (wanted.Contains(Path.GetRelativePath(modRoot, file))) continue;
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
            deleted++;
        }
        // Folders the refit added and the backup never had, now empty.
        foreach (var dir in Directory.EnumerateDirectories(modRoot, "*", SearchOption.AllDirectories)
                                     .OrderByDescending(d => d.Length).ToList())
        {
            if (Directory.Exists(Path.Combine(from, Path.GetRelativePath(modRoot, dir)))) continue;
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        return (files, deleted);
    }
}
