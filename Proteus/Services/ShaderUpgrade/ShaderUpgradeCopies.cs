using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The gear shader upgrade's files: the upgraded <c>.mtrl</c> copies and Form B's derived <c>.tex</c> masks, written into
/// <see cref="ManagedModPaths.ShaderUpgradeSubdir"/> of the managed mod and nowhere else. SENSITIVE: the game loads these
/// files, so each passes its gate before it gets its final name, and a failure leaves no partial file.
/// </summary>
internal static class ShaderUpgradeCopies
{
    /// <summary>A write refused or failed; the unit is left as <see cref="LeftReason.WriteFailed"/>.</summary>
    public sealed class WriteRefused(string message) : Exception(message);

    /// <summary>The full path a copy name has under <paramref name="managedRoot"/>, refusing anything that escapes it.</summary>
    public static string PathFor(string managedRoot, string name)
    {
        var dir = Path.GetFullPath(ManagedModPaths.ShaderUpgradeDir(managedRoot));
        var full = Path.GetFullPath(Path.Combine(dir, name));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase))
            throw new WriteRefused($"\"{name}\" is outside {dir}");
        return full;
    }

    /// <summary>
    /// Write a material copy. A copy of the same name is the same bytes (content-addressed), so an existing one is kept.
    /// </summary>
    /// <param name="write">The atomic write, <see cref="PenumbraModMeta.AtomicWrite(string, byte[], int)"/> unless a test
    /// stands in for it.</param>
    public static string WriteMaterial(string managedRoot, string name, byte[] bytes, Action<string, byte[]>? write = null)
    {
        var path = PathFor(managedRoot, name);
        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { (write ?? ((p, b) => PenumbraModMeta.AtomicWrite(p, b)))(path, bytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            throw new WriteRefused(ex.Message);
        }
        return path;
    }

    /// <summary>
    /// Write a derived mask: to a temporary file first, its header checked against the plan, then moved to its final
    /// name. An existing complete file of that name is reused. Any failure deletes the temporary file.
    /// </summary>
    /// <param name="writeTex"><see cref="TextureLoader.WriteTex(byte[], int, int, string, TexEncoding)"/>.</param>
    public static string WriteMask(string managedRoot, ShaderUpgradePlan.MaskSpec mask, TexEncoding encoding,
                                   Func<byte[], int, int, string, TexEncoding, bool> writeTex)
    {
        var path = PathFor(managedRoot, mask.CopyName);
        if (TextureLoader.IsCompleteTex(path)) return path;
        if (mask.Rgba == null) throw new WriteRefused($"{mask.CopyName} is missing and has no pixels to write");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Path.GetRandomFileName() + ".part";
        try
        {
            if (!writeTex(mask.Rgba, mask.Width, mask.Height, tmp, encoding))
                throw new WriteRefused($"{mask.CopyName} could not be encoded");
            var header = new byte[16];
            long length;
            using (var fs = File.OpenRead(tmp))
            {
                length = fs.Length;
                if (fs.Read(header, 0, header.Length) != header.Length) throw new WriteRefused($"{mask.CopyName} is short");
            }
            if (!ShaderUpgradePlan.IsSoundMaskHeader(header, length, mask.Width, mask.Height)
                || !TextureLoader.IsCompleteTex(tmp))
                throw new WriteRefused($"{mask.CopyName} failed its header check");
            try { File.Move(tmp, path, overwrite: false); }
            catch (IOException) when (TextureLoader.IsCompleteTex(path)) { /* the same mask, written meanwhile */ }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WriteRefused(ex.Message);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// Write a plan's units, checking before every write that the watcher has not been stopped meanwhile (then nothing
    /// more is written and null is returned). A unit whose write is refused is reported and left out.
    /// </summary>
    public static List<RedirectUnit>? WriteUnits(string managedRoot, IEnumerable<ShaderUpgradePlan.Unit> units,
                                                 TexEncoding encoding, Func<bool> stopped,
                                                 Action<string, byte[]>? writeMaterial,
                                                 Func<byte[], int, int, string, TexEncoding, bool> writeTex,
                                                 Action<ShaderUpgradePlan.Unit, ShaderUpgradePlan.MaskSpec> written,
                                                 Action<ShaderUpgradePlan.Unit, string> refused)
    {
        var result = new List<RedirectUnit>();
        foreach (var u in units)
        {
            if (stopped()) return null;
            try
            {
                var copy = WriteMaterial(managedRoot, u.MaterialCopyName, u.MaterialBytes, writeMaterial);
                RedirectMask? mask = null;
                if (u.Mask is { } m)
                {
                    if (stopped()) return null;
                    var maskPath = WriteMask(managedRoot, m, encoding, writeTex);
                    written(u, m);
                    mask = new RedirectMask(m.GamePath, maskPath);
                }
                result.Add(new RedirectUnit(u.MaterialGamePath, copy, mask, u.Upstream, u.SourceLabel, u.Form,
                                            u.Mask?.Width ?? 0, u.Mask?.Height ?? 0));
            }
            catch (WriteRefused ex) { refused(u, ex.Message); }
        }
        return stopped() ? null : result;
    }

    /// <summary>
    /// The most recently replaced masks, kept since the game may still be reading one. A name added again moves to the
    /// back. Thread-safe.
    /// </summary>
    internal sealed class ReplacedTail(int keep)
    {
        private readonly List<string> names = [];
        private readonly object sync = new();

        public void Add(string name)
        {
            lock (sync)
            {
                names.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                names.Add(name);
                while (names.Count > keep) names.RemoveAt(0);
            }
        }

        public IReadOnlyCollection<string> Snapshot() { lock (sync) return [.. names]; }
    }

    /// <summary>
    /// Delete this process's masks that no held collection references and that are not in the tail. A delete that fails
    /// is left for the next prune. Returns how many went.
    /// </summary>
    public static int PruneMasks(string managedRoot, IReadOnlySet<string> referenced, ReplacedTail tail, int pid,
                                 Action<string>? delete = null)
    {
        var dir = ManagedModPaths.ShaderUpgradeDir(managedRoot);
        if (!Directory.Exists(dir)) return 0;
        string[] files;
        try { files = Directory.GetFiles(dir, "*_mask.tex"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        int n = 0;
        foreach (var f in ShaderUpgradePlan.MasksToPrune(pid, files, referenced, tail.Snapshot()))
        {
            try { (delete ?? File.Delete)(f); n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next prune */ }
        }
        return n;
    }

    /// <summary>Delete copies left by games no longer running (see <see cref="ShaderUpgradePlan.StaleCopies"/>).</summary>
    public static int Sweep(string managedRoot, int pid, Func<int, bool> alive)
    {
        var dir = ManagedModPaths.ShaderUpgradeDir(managedRoot);
        if (!Directory.Exists(dir)) return 0;
        int n = 0;
        try
        {
            foreach (var f in ShaderUpgradePlan.StaleCopies(Directory.GetFiles(dir), pid, alive))
                if (TryDelete(f)) n++;
            // Interrupted mask writes.
            foreach (var f in Directory.GetFiles(dir, "*.part"))
                if (ShaderUpgradePlan.StaleCopies([f], pid, alive).Any()) TryDelete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next load */ }
        return n;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}
