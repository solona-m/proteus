using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Compressed copies of a content pack's own UNCOMPRESSED textures, kept beside that pack's sidecar in
/// <c>{mod}/Proteus/compressed/</c>. The author's files are never touched or replaced: these sit next to them, and
/// the composite republishes from here instead of copying the author's bytes.
/// <para/>
/// Why at all: art an author left uncompressed is exactly what a viewer's sync plugin re-encodes on THEIR machine,
/// with no check on what the pixels mean — an index map becomes BC5 and starts naming the wrong colour-table rows.
/// Converting it here, once, keeps that decision ours. Anything the author already block-compressed is left alone,
/// because those passes skip it too.
/// <para/>
/// Built at import (see <see cref="CompositorService.PrewarmPackTextures"/>) and again on demand during a composite,
/// so a pack imported before compression was switched on, or one whose art changed since, still gets a copy.
/// </summary>
internal static class PackTextureCompressionCache
{
    /// <summary>Under the pack's own <c>Proteus/</c> sidecar, so it travels and is removed with the mod.</summary>
    internal const string Subdir = "compressed";

    /// <summary>
    /// Sources a conversion has already refused, keyed by the same stamp the copy would have been named after.
    /// A refusal of this kind is a property of the bytes — art that will not decode, an index that cannot keep its
    /// rows, a sheet the writer will only store uncompressed — so the answer cannot change until the file does, and
    /// the stamp moves when it does. Without this the whole trial (two BC5 encodes and two decode-and-compare passes
    /// over a 4K sheet) ran again on every composite, for ever, for one stubborn texture.
    /// <para/>
    /// Transient failures are deliberately NOT recorded: a locked file or a full disk has to be free to recover.
    /// In memory only, because a restart is a cheap place to try again and the encoder can differ between sessions.
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, byte> Refused = new();

    /// <summary>How many distinct sources have been refused this session. For the tests, and for a log line.</summary>
    internal static int RefusedCount => Refused.Count;

    /// <summary>Forget every refusal, so they are judged again. For tests, which share the static above.</summary>
    internal static void ClearRefusals() => Refused.Clear();

    /// <summary>
    /// The compressed copy of <paramref name="srcDisk"/>, built if it is not already there. False when there is
    /// nothing usable — no sidecar root, an undecodable source, or an index map that cannot survive the trial, in
    /// which case the caller republishes the author's bytes exactly as they are.
    /// </summary>
    internal static bool TryEnsure(
        string? modRoot, string srcDisk, bool isIndex, TextureLoader loader, IPluginLog? log, out string compressed)
    {
        compressed = string.Empty;
        if (string.IsNullOrEmpty(modRoot)) return false;

        ulong stamp;
        try
        {
            var info = new FileInfo(srcDisk);
            if (!info.Exists) return false;
            stamp = Stamp(srcDisk, info.LastWriteTimeUtc.Ticks, info.Length, isIndex);
        }
        catch { return false; }

        var dir = Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, Subdir);
        var stem = Stem(srcDisk);
        var path = Path.Combine(dir, $"{stem}_{stamp.ToString("x16", CultureInfo.InvariantCulture)}.tex");

        if (File.Exists(path))
        {
            compressed = path;
            return true;
        }

        // Asked and answered: these same bytes were refused already, and nothing about them has changed.
        if (Refused.ContainsKey(stamp)) return false;

        if (loader.LoadTexAsRgba(srcDisk) is not { } decoded)
        {
            log?.Debug("[Proteus] pack textures: {0} could not be decoded — republishing the author's file",
                       Path.GetFileName(srcDisk));
            Refused[stamp] = 0;
            return false;
        }

        var (rgba, w, h) = decoded;

        var encoding = TexEncoding.Bc7;
        if (isIndex)
        {
            // An index names colour-table rows, so it is compressed only when a trial encode proves every texel
            // still reads the same row — the same rule our own index textures go through.
            var plan = loader.PlanIndexBc5(rgba, w, h);
            if (plan.Plan == TextureLoader.IndexPlan.Uncompressed)
            {
                log?.Information("[Proteus] pack textures: {0} still moves {1} texel(s) to another colour-table row "
                               + "after snapping — leaving the author's file uncompressed",
                                 Path.GetFileName(srcDisk), plan.Flipped);
                Refused[stamp] = 0;
                return false;
            }

            if (plan.Plan == TextureLoader.IndexPlan.Bc5AfterSnap)
            {
                rgba = plan.Snapped ?? TextureLoader.SnapIndexForBc5(rgba, w, h, out _, out _);
                log?.Debug("[Proteus] pack textures: {0} snapped {1} texel(s) ({2} to another row) for BC5",
                           Path.GetFileName(srcDisk), plan.SnappedTexels, plan.SnappedRows);
            }

            encoding = TexEncoding.Bc5;
        }

        try
        {
            Directory.CreateDirectory(dir);
            if (!loader.WriteTex(rgba, w, h, path, encoding)) return false;
        }
        catch (Exception ex)
        {
            log?.Warning("[Proteus] pack textures: could not write a compressed copy of {0} ({1})",
                         Path.GetFileName(srcDisk), ex.Message);
            return false;
        }

        // WriteTex downgrades silently: dimensions off the 4-block grid fall back to uncompressed, and a solid
        // colour collapses to an uncompressed 16x16. Either way the copy is still something a viewer's sync client
        // would re-encode, so it buys nothing — say so and let the author's own file be republished instead.
        if (!TextureLoader.IsSyncRecompressible(path))
        {
            PruneOlder(dir, stem, path, log);
            compressed = path;
            return true;
        }

        log?.Debug("[Proteus] pack textures: {0} came back uncompressed ({1}x{2}) — republishing the author's file",
                   Path.GetFileName(srcDisk), w, h);
        try { File.Delete(path); } catch { /* it is only a stale copy */ }
        Refused[stamp] = 0;
        return false;
    }

    /// <summary>
    /// Drop earlier copies of the same source — a pack that updates its art leaves one behind every time, and these
    /// are full-size textures. Best effort: a file we cannot delete is only wasted space.
    /// </summary>
    private static void PruneOlder(string dir, string stem, string keep, IPluginLog? log)
    {
        try
        {
            foreach (var old in Directory.GetFiles(dir, stem + "_*.tex"))
                if (!string.Equals(old, keep, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch { /* in use, or gone already */ }
        }
        catch (Exception ex)
        {
            log?.Debug("[Proteus] pack textures: could not prune older copies in {0} ({1})", dir, ex.Message);
        }
    }

    /// <summary>
    /// The copy's name prefix: the source's file name plus a hash of its full path. The path hash is what makes the
    /// prefix unique — packs routinely ship the same file name in several option folders, and a prefix that was the
    /// leaf alone made <see cref="PruneOlder"/> delete one option's copy whenever another option built its own,
    /// leaving both to be re-encoded on every composite for ever.
    /// </summary>
    private static string Stem(string srcDisk)
    {
        var name = Path.GetFileNameWithoutExtension(srcDisk);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        if (string.IsNullOrEmpty(name)) name = "tex";

        ulong h = 14695981039346656037;
        foreach (var c in srcDisk) { h ^= char.ToLowerInvariant(c); h *= 1099511628211; }
        return $"{name}_{(uint)(h ^ (h >> 32)):x8}";
    }

    /// <summary>
    /// Identity of one compressed copy: the source path, its write time and length, whether it was treated as an
    /// index, and the writer's own output version — so a changed pack, a changed classification or a changed encoder
    /// all produce a different file rather than serving a stale one.
    /// </summary>
    private static ulong Stamp(string srcDisk, long ticks, long length, bool isIndex)
    {
        ulong h = 14695981039346656037;   // FNV-1a, as elsewhere
        foreach (var c in srcDisk) { h ^= char.ToLowerInvariant(c); h *= 1099511628211; }
        h ^= (ulong)ticks;  h *= 1099511628211;
        h ^= (ulong)length; h *= 1099511628211;
        h ^= isIndex ? 1ul : 0ul; h *= 1099511628211;
        h ^= (ulong)CompositorService.OutputFormatVersion; h *= 1099511628211;
        return h;
    }
}
