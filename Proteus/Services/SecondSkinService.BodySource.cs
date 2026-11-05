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
        string? keptSource = null;
        try
        {
            var kept = UpstreamKeptPath(outputRoot, gamePath);
            if (new FileInfo(kept) is { Exists: true } fi) keptSource = ShellBodyAtr.ReadSource(kept, modsRoot, fi.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                         or NotSupportedException) { /* nothing kept to name */ }
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
                var mem = remembered!;
                return new(kind, null, BodySourceId(kind, null, mem, gamePath), () => RememberedLoad(mem),
                           SourceModFile(kind, null, _upstreamBodyDisks.GetValueOrDefault(gamePath), null), gamePath);
            case BodySourceKind.Kept:
                string? keptRecord = null;
                try
                {
                    if (new FileInfo(kept) is { Exists: true } fi)
                        keptRecord = ShellBodyAtr.ReadSource(kept, modsRootOf?.Invoke(), fi.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
