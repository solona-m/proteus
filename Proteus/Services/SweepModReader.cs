using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Proteus.Services;

/// <summary>One file a mod redirects, with what it references: a material's texture paths, a model's material names.</summary>
/// <param name="FullPath">The file on disk, normalised (<see cref="DesignRestoreSweep.NormFile"/>).</param>
/// <param name="DefaultData">Whether the redirect is in the mod's default data rather than an option.</param>
/// <param name="Unreadable">The file is a material or model that couldn't be read or parsed; it contributes no
/// references. A file missing on disk is not unreadable: it simply references nothing.</param>
internal sealed record ModFile(string GamePath, string FullPath, bool DefaultData, IReadOnlyList<string> Refs, bool Unreadable);

/// <summary>A mod's redirected files, or <see cref="ManifestFailed"/> when its file list couldn't be read at all.</summary>
internal sealed record ModDirFiles(bool ManifestFailed, IReadOnlyList<ModFile> Files)
{
    internal static readonly ModDirFiles Failed = new(true, []);
}

/// <summary>
/// Reads what the design-restore sweep needs from mods on disk: every redirect of every option, and for each material
/// or model what it references. Background thread only — it never touches a Dalamud service. Manifests are cached by
/// their files' stamps and references by each file's stamp; a failed read is never cached, so the next sweep retries.
/// </summary>
internal sealed class SweepModReader
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Stamp, List<PenumbraModMeta.Redirect> Redirects)> manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Mtime, long Length, string[] Refs)> refs = new(StringComparer.OrdinalIgnoreCase);
    private int coldReads, unreadable;

    /// <summary>Material and model files read from disk (not the cache) so far.</summary>
    public int ColdReads => Volatile.Read(ref coldReads);

    /// <summary>Material and model files that couldn't be read so far.</summary>
    public int Unreadable => Volatile.Read(ref unreadable);

    public ModDirFiles Files(string modsRoot, string dir)
    {
        var modRoot = Path.Combine(modsRoot, dir);
        var redirects = Redirects(modRoot);
        if (redirects == null) return ModDirFiles.Failed;

        var files = new List<ModFile>(redirects.Count);
        foreach (var r in redirects)
        {
            var full = DesignRestoreSweep.NormFile(Path.Combine(modRoot, r.File));
            var game = DesignRestoreSweep.NormGame(r.GamePath);
            IReadOnlyList<string> found = [];
            bool bad = false;
            if (game.EndsWith(".mtrl", StringComparison.Ordinal) || game.EndsWith(".mdl", StringComparison.Ordinal))
            {
                var read = References(full, model: game.EndsWith(".mdl", StringComparison.Ordinal));
                if (read == null) bad = true; else found = read;
            }
            files.Add(new ModFile(game, full, r.Source.Length == 0, found, bad));
        }
        return new ModDirFiles(false, files);
    }

    private List<PenumbraModMeta.Redirect>? Redirects(string modRoot)
    {
        var stamp = ManifestStamp(modRoot);
        lock (gate)
            if (stamp != null && manifests.TryGetValue(modRoot, out var hit) && hit.Stamp == stamp) return hit.Redirects;

        var read = PenumbraModMeta.TryReadAllRedirects(modRoot);
        if (read == null || stamp == null) return read;
        // Re-stamped after the read: a manifest saved during it is read again next time rather than cached stale.
        if (ManifestStamp(modRoot) == stamp)
            lock (gate) manifests[modRoot] = (stamp, read);
        return read;
    }

    /// <summary>Every manifest file of the mod with its time and length; null when the folder can't be listed.</summary>
    private static string? ManifestStamp(string modRoot)
    {
        try
        {
            var sb = new StringBuilder();
            var names = new[] { PenumbraModMeta.MetaFile, PenumbraModMeta.LegacyDefaultMod }
                .Select(n => Path.Combine(modRoot, n)).Where(File.Exists)
                .Concat(Directory.EnumerateFiles(modRoot, "group_*.json"))
                .Order(StringComparer.OrdinalIgnoreCase);
            foreach (var path in names)
            {
                var fi = new FileInfo(path);
                sb.Append(fi.Name).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('|').Append(fi.Length).Append(';');
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    private string[]? References(string fullPath, bool model)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(fullPath);
            // Missing on disk: Penumbra can't load it either, so it needs nothing — and it isn't a read to retry.
            if (!fi.Exists) return [];
        }
        catch { Interlocked.Increment(ref unreadable); return null; }

        lock (gate)
            if (refs.TryGetValue(fullPath, out var hit) && hit.Mtime == fi.LastWriteTimeUtc && hit.Length == fi.Length)
                return hit.Refs;

        Interlocked.Increment(ref coldReads);
        string[]? found;
        try
        {
            if (model)
            {
                using var s = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                found = ModelMaterialNames(s)?.ToArray();
            }
            else found = ShaderUpgradePlan.TexturePaths(File.ReadAllBytes(fullPath));
        }
        catch { found = null; }

        if (found == null) { Interlocked.Increment(ref unreadable); return null; }
        lock (gate) refs[fullPath] = (fi.LastWriteTimeUtc, fi.Length, found);
        return found;
    }

    private const int ModelHeaderSize = 0x44;
    private const int ModelDeclSize = 17 * 8;

    /// <summary>
    /// The material names a model declares, from its header, vertex declarations and string block only — never its
    /// vertex or index data. Every string in the block ending in <c>.mtrl</c> is a material name. Null when the
    /// stream isn't a model this can read.
    /// </summary>
    internal static IReadOnlyList<string>? ModelMaterialNames(Stream s)
    {
        var head = new byte[ModelHeaderSize];
        if (!ReadExactly(s, head, 0, head.Length)) return null;
        int declCount = BitConverter.ToUInt16(head, 12);
        long declEnd = ModelHeaderSize + (long)declCount * ModelDeclSize;
        if (declCount > 4096 || declEnd + 8 > s.Length) return null;

        s.Position = declEnd;
        var strHead = new byte[8];
        if (!ReadExactly(s, strHead, 0, 8)) return null;
        uint strSize = BitConverter.ToUInt32(strHead, 4);
        if (strSize > 16 * 1024 * 1024 || declEnd + 8 + strSize > s.Length) return null;

        var block = new byte[strSize];
        if (!ReadExactly(s, block, 0, block.Length)) return null;
        var names = new List<string>();
        int start = 0;
        for (int i = 0; i <= block.Length; i++)
        {
            if (i < block.Length && block[i] != 0) continue;
            if (i > start)
            {
                var str = Encoding.ASCII.GetString(block, start, i - start);
                if (str.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) names.Add(str);
            }
            start = i + 1;
        }
        return names;
    }

    private static bool ReadExactly(Stream s, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int n = s.Read(buffer, offset, count);
            if (n <= 0) return false;
            offset += n; count -= n;
        }
        return true;
    }
}
