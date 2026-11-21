using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Proteus.Services;

using static Proteus.Services.PenumbraManipulations;

/// <summary>
/// Which file a body part is read from when it resolves to our own output (the nipple smooth and nail flatten
/// republish it): one rule for the shell build and the skin's seam map, so the two phases of one composite never read
/// different bodies (#56). The shell used to cut from the settled upstream while the seam map, a plain resolve, read
/// our previous publication — stale right after a body mod's priority change.
/// </summary>
public sealed partial class SecondSkinService
{
    internal enum BodySourceKind { Resolved, Settled, Remembered, Kept, OwnHeld }

    /// <summary>
    /// The shell's order: the resolved file when it is not ours; else the settled upstream (only when it LOADED), the
    /// bytes remembered in memory, the copy kept on disk, and last our own file, held (never smoothed again).
    /// </summary>
    internal static BodySourceKind ChooseBodySource(bool resolvedIsOurs, bool hasSettled, bool hasRemembered, bool keptExists)
        => !resolvedIsOurs ? BodySourceKind.Resolved
         : hasSettled ? BodySourceKind.Settled
         : hasRemembered ? BodySourceKind.Remembered
         : keptExists ? BodySourceKind.Kept
         : BodySourceKind.OwnHeld;

    // ── the remembered or kept upstream follows edits to the file it came from ─────────────────────────────

    /// <summary>A source file's identity for freshness: its length and last write time.</summary>
    internal readonly record struct SourceStamp(long Length, long WriteTicks);

    /// <summary>The stamp of a rooted, existing file; null for anything else, an I/O error included.</summary>
    internal static SourceStamp? StampOf(string? file)
    {
        if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file)) return null;
        try
        {
            var fi = new FileInfo(file);
            return fi.Exists ? new SourceStamp(fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                         or NotSupportedException) { return null; }
    }

    /// <summary>A whole file, or null when it cannot be read.</summary>
    internal static byte[]? SafeReadFile(string disk)
    {
        try { return File.ReadAllBytes(disk); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                         or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Whether a remembered or kept upstream may be re-read from <paramref name="sourceDisk"/>: a real file, never game
    /// data, never our own published output (smoothing it again would compound), never a live-brush preview.
    /// </summary>
    internal static bool MayRefreshFrom(string? sourceDisk, string gamePath, string outputRoot)
        => !string.IsNullOrEmpty(sourceDisk)
        && !string.Equals(sourceDisk, gamePath, StringComparison.OrdinalIgnoreCase)
        && Path.IsPathRooted(sourceDisk)
        && !IsInsideOutputRoot(sourceDisk, outputRoot)
        && !LiveBrushPreview.IsPreviewFile(sourceDisk, outputRoot);

    /// <summary>
    /// The mod file a kept copy refreshed from <paramref name="keptSrc"/> names, as the shell's length-checked read will
    /// see it once the copy is rewritten: the source when its length is the fresh bytes', else none.
    /// </summary>
    internal static string? FreshKeptModFile(string? keptSrc, long freshLength)
        => StampOf(keptSrc) is { } s && s.Length == freshLength ? keptSrc : null;

    /// <summary>Whether bytes read as a model to the parser the smoothing pass itself uses.</summary>
    internal static bool ParsesAsModel(byte[] bytes)
    {
        try { SecondSkinWriter.Parse(bytes); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// What <see cref="RefreshUpstream"/> found: fresh bytes to use instead (null = keep the current ones), the stamp to
    /// record for the bytes now in use (null = record nothing, so the next composite looks again), and whether the file
    /// changed into something that does not read as a model.
    /// </summary>
    internal readonly record struct UpstreamRefresh(byte[]? Fresh, SourceStamp? Stamp, bool Rejected = false);

    /// <summary>
    /// Re-read a remembered or kept upstream from the file it came from when that file changed since
    /// <paramref name="recorded"/> (the stamp recorded for exactly <paramref name="current"/>; null = unknown). An
    /// unchanged stamp costs a stat and no read. A read the file moved under (a write in progress) records nothing, so
    /// the next composite retries; a stable file that does not parse is rejected under its stamp, so it is not read
    /// again until it changes.
    /// </summary>
    internal static UpstreamRefresh RefreshUpstream(byte[] current, string? sourceDisk, SourceStamp? recorded,
                                                    string gamePath, string outputRoot,
                                                    Func<string, SourceStamp?> stat, Func<string, byte[]?> read)
    {
        if (!MayRefreshFrom(sourceDisk, gamePath, outputRoot)) return default;
        var src = sourceDisk!;
        SourceStamp? now;
        try { now = stat(src); }
        catch (Exception ex) when (IsIoFailure(ex)) { return default; }
        if (now == null || now == recorded) return default;

        byte[]? bytes;
        SourceStamp? after;
        try
        {
            bytes = read(src);
            after = stat(src);
        }
        catch (Exception ex) when (IsIoFailure(ex)) { return default; }
        if (bytes == null || after != now) return default;
        if (!ParsesAsModel(bytes)) return new(null, now, Rejected: true);
        if (bytes.AsSpan().SequenceEqual(current)) return new(null, now);
        return new(bytes, now);
    }

    /// <summary>
    /// The mod file a kept upstream copy's record names, whatever its length now (an edit may have changed it); null when
    /// there is no kept copy or no usable record.
    /// </summary>
    internal static string? KeptSourceOf(string outputRoot, string gamePath, string? modsRoot)
    {
        var kept = UpstreamKeptPath(outputRoot, gamePath);
        return File.Exists(kept) ? ShellBodyAtr.ReadSourcePath(kept, modsRoot) : null;
    }

    private static bool IsIoFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>
    /// The identity (path, length, write time) of the mod file a republished part's upstream came from: the remembered
    /// record first, then the kept copy's. Null when neither names a real file. What the composite fingerprint carries,
    /// so a write to that file is never gated away as "inputs unchanged".
    /// </summary>
    internal static string? UpstreamSourceIdOf(string? remembered, string? keptSource, string gamePath)
    {
        foreach (var file in new[] { remembered, keptSource })
            if (!string.IsNullOrEmpty(file) && Path.IsPathRooted(file)
                && !string.Equals(file, gamePath, StringComparison.OrdinalIgnoreCase)
                && StampOf(file) != null)
                return BodySourceId(BodySourceKind.Resolved, file, null, gamePath);
        return null;
    }

    /// <summary><see cref="UpstreamSourceIdOf"/> from this service's own record.</summary>
    internal string? UpstreamSourceId(string gamePath, string outputRoot, string? modsRoot)
    {
        var (remembered, kept) = KnownBodyFiles(gamePath, outputRoot, modsRoot);
        return UpstreamSourceIdOf(remembered, kept, gamePath);
    }

    /// <summary>Where the shell keeps a body part's upstream on disk (a subfolder: PruneManagedOutput does not recurse).</summary>
    internal static string UpstreamKeptPath(string outputRoot, string gamePath)
        => Path.Combine(outputRoot, "models", "upstream", CompositorService.SanitizeName(gamePath) + ".mdl");

    /// <summary>A body part's chosen source: what it is, its file (null for remembered bytes; for game data the game
    /// path itself, as Penumbra resolves an unmodded file — not rooted), a cache identity, and how to read it.</summary>
    internal readonly record struct BodySource(BodySourceKind Kind, string? Disk, string Id, Func<byte[]?> Load,
                                               string? ModFile = null, string? GamePath = null);

    /// <summary>
    /// The mod file a part's bytes really come from, as the shell works out its <c>sourceDisk</c>: the resolved or settled
    /// file, the remembered copy's recorded disk, the kept copy's recorded source; none for our own output held.
    /// </summary>
    internal static string? SourceModFile(BodySourceKind kind, string? disk, string? rememberedDisk, string? keptRecord)
        => kind switch
        {
            BodySourceKind.Resolved or BodySourceKind.Settled => disk,
            BodySourceKind.Remembered => rememberedDisk,
            BodySourceKind.Kept => keptRecord,
            _ => null,
        };

    /// <summary>
    /// A cache identity for a source: a file by path, size and write time; remembered bytes by game path, size and
    /// hash; a missing file or game data by its game path.
    /// </summary>
    internal static string BodySourceId(BodySourceKind kind, string? disk, byte[]? bytes, string gamePath)
    {
        // Settled as game data (#63): the game path is the file; no file-system lookup of a relative path.
        if (disk != null && string.Equals(disk, gamePath, StringComparison.OrdinalIgnoreCase)) return gamePath;
        if (kind == BodySourceKind.Remembered && bytes != null)
            return $"mem|{gamePath}|{bytes.Length}|{Convert.ToHexString(SHA256.HashData(bytes), 0, 8)}";
        try
        {
            if (!string.IsNullOrEmpty(disk) && new FileInfo(disk) is { Exists: true } fi)
                return $"{disk}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                         or NotSupportedException) { /* unreadable — the game path */ }
        return gamePath;
    }

    /// <summary>
    /// What we remember for a body path, written nowhere: the mod file its remembered bytes came from, and the one the
    /// kept copy's record names. For the prime's false-echo guard (<see cref="CompositorService.NamesModFile"/>).
    /// </summary>
    internal (string? Remembered, string? KeptSource) KnownBodyFiles(string gamePath, string outputRoot, string? modsRoot)
    {
        var remembered = _upstreamBodyDisks.GetValueOrDefault(gamePath);
        // Whatever its length now: an edit to the source must not hide which file it is.
        var keptSource = KeptSourceOf(outputRoot, gamePath, modsRoot);
        return (remembered, keptSource);
    }

    /// <summary>
    /// The "second skin part … &lt;-" log's source: what the bytes were really read from — not the live resolve, which is
    /// our own output whenever we republish the part (#63).
    /// </summary>
    internal static string SourceLabel(BodySourceKind kind, string? sourceDisk, string? bodyDisk, string gamePath)
    {
        static bool IsGame(string? d, string gp) => d == null || string.Equals(d, gp, StringComparison.OrdinalIgnoreCase)
                                                    || !Path.IsPathRooted(d);
        return kind switch
        {
            BodySourceKind.OwnHeld => $"{kind}: {bodyDisk ?? "(game data)"} (our own output, held)",
            BodySourceKind.Remembered when sourceDisk == null => $"{kind}: (remembered copy)",
            _ when IsGame(sourceDisk, gamePath) => $"{kind}: (game data)",
            _ => $"{kind}: {sourceDisk}",
        };
    }

    /// <summary>A copy of remembered bytes: the shell cuts from the remembered array, and nothing else shares it.</summary>
    internal static byte[] RememberedLoad(byte[] remembered) => (byte[])remembered.Clone();

    /// <summary>
    /// The source the shell would read this part from, without changing anything: no memo or file writes. The settled
    /// file is read here (only when it would be chosen) because "settled" means it loaded, as in the shell.
    /// </summary>
    internal BodySource BodySourceFor(string gamePath, string outputRoot)
    {
        var resolved = penumbra.ResolvePlayer(gamePath);
        bool ours = resolved != null && IsInsideOutputRoot(resolved, outputRoot);
        var kept = UpstreamKeptPath(outputRoot, gamePath);
        bool hasRemembered = _upstreamBodies.TryGetValue(gamePath, out var remembered);
        bool keptExists = ours && File.Exists(kept);

        byte[]? settledBytes = null;
        string? settledDisk = null;
        if (ours && settledUpstream?.Invoke(gamePath) is { } sd)
        {
            settledDisk = sd;
            settledBytes = textureLoader.LoadRawFile(sd, gamePath);
        }
        var kind = ChooseBodySource(ours, settledBytes != null, hasRemembered, keptExists);
        switch (kind)
        {
            case BodySourceKind.Settled:
                var settled = settledBytes!;
                return new(kind, settledDisk, BodySourceId(kind, settledDisk, settled, gamePath), () => settled,
                           SourceModFile(kind, settledDisk, null, null), gamePath);
            case BodySourceKind.Remembered:
            {
                // Read-only: the shell, which runs after, records what it finds; both read the same bytes (#56).
                var memDisk = _upstreamBodyDisks.GetValueOrDefault(gamePath);
                var mem = remembered!;
                var refreshed = RefreshUpstream(mem, memDisk, StampFor(gamePath, mem), gamePath, outputRoot,
                                                StampOf, SafeReadFile);
                if (refreshed.Fresh != null) mem = refreshed.Fresh;
                return new(kind, null, BodySourceId(kind, null, mem, gamePath), () => RememberedLoad(mem),
                           SourceModFile(kind, null, memDisk, null), gamePath);
            }
            case BodySourceKind.Kept:
                var modsRoot = modsRootOf?.Invoke();
                string? keptRecord = null;
                try
                {
                    if (new FileInfo(kept) is { Exists: true } fi)
                        keptRecord = ShellBodyAtr.ReadSource(kept, modsRoot, fi.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                byte[]? keptBytes = null;
                try { keptBytes = File.ReadAllBytes(kept); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                var keptSrc = keptBytes != null ? ShellBodyAtr.ReadSourcePath(kept, modsRoot) : null;
                if (keptBytes != null
                    && RefreshUpstream(keptBytes, keptSrc, null, gamePath, outputRoot, StampOf, SafeReadFile).Fresh is { } freshKept)
                    // The file the copy came from changed: read as the shell will, under the remembered-bytes identity, and
                    // named by that file — the shell's length check matches the refreshed copy, so both phases name it (#56).
                    return new(BodySourceKind.Kept, kept, BodySourceId(BodySourceKind.Remembered, null, freshKept, gamePath),
                               () => RememberedLoad(freshKept), FreshKeptModFile(keptSrc, freshKept.Length), gamePath);
                return new(kind, kept, BodySourceId(kind, kept, null, gamePath), () =>
                {
                    try { return File.ReadAllBytes(kept); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
                }, SourceModFile(kind, kept, null, keptRecord), gamePath);
            default:   // Resolved or OwnHeld: the resolved file itself (null = game data)
                return new(kind, resolved, BodySourceId(kind, resolved, null, gamePath),
                           () => textureLoader.LoadRawFile(resolved, gamePath), SourceModFile(kind, resolved, null, null),
                           gamePath);
        }
    }
}
