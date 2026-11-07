using System;
using System.Collections.Generic;
using System.IO;

namespace Proteus.Services;

/// <summary>A file's last write time (UTC ticks) and length: what tells a cached copy of it from a changed one.</summary>
internal readonly record struct SidecarStamp(long LastWriteTicksUtc, long Length);

/// <summary>How a <see cref="SidecarCache.ReadMetadata"/> went.</summary>
internal enum MetaReadKind
{
    /// <summary>The text is in <see cref="MetaRead.Text"/>.</summary>
    Ok,
    /// <summary>The file is not there.</summary>
    Missing,
    /// <summary>The file could not be stat'ed or read (locked, access denied…). Never cached: the next call tries again.</summary>
    IoError,
    /// <summary>This very stamp was already found not to parse (<see cref="SidecarCache.NoteUnparsable"/>).</summary>
    Unparsable,
}

internal readonly record struct MetaRead(MetaReadKind Kind, string? Text, SidecarStamp Stamp);

/// <summary>
/// What discovery has already learned about the mods folder, so it does not ask the drive again: which mod folders have
/// a <c>Proteus/metadata.json</c>, and each one's text, keyed by its <see cref="SidecarStamp"/>.
/// <list type="bullet">
/// <item>A folder found to have one is re-checked by the single stat <see cref="ReadMetadata"/> makes anyway; one found
/// without is trusted for <c>negativeTtlMs</c>, so a missed Penumbra event corrects itself.</item>
/// <item>Text is cached, never a parsed object: callers mutate the graphs discovery hands out, so each one is parsed afresh.</item>
/// <item>A stamp is trusted only once it has stayed put for longer than <c>racyWindowMs</c> since it was first read:
/// a filesystem with coarse timestamps can keep the same stamp across a quick rewrite of the same length.</item>
/// <item>An IO failure is never stored. A store that raced a <see cref="Forget"/> is dropped (the epoch moved).</item>
/// </list>
/// Thread-safe; no file I/O happens under the lock.
/// </summary>
internal sealed class SidecarCache
{
    private sealed record Presence(bool Present, long At);

    /// <param name="FirstSeenAt">When the file was first read under this stamp.</param>
    /// <param name="LastReadAt">When <paramref name="Text"/> was read. Trust needs a read taken more than the racy window
    /// after the first: a same-stamp rewrite lands within the timestamp's granularity of the original write, so a read that
    /// late has seen it.</param>
    private sealed record TextEntry(string ModDir, SidecarStamp Stamp, string? Text, long FirstSeenAt, long LastReadAt,
                                    bool Unparsable)
    {
        public bool Trusted(long racyWindowMs) => LastReadAt - FirstSeenAt > racyWindowMs;
    }

    private enum StatKind { Found, Missing, IoError }

    /// <summary>
    /// One stat. An IO or access failure is <see cref="StatKind.IoError"/> (never cached); any other failure (an odd
    /// path) is <see cref="StatKind.Missing"/>, as <see cref="File.Exists(string)"/> has always treated it.
    /// </summary>
    private (StatKind Kind, SidecarStamp Stamp) TryStat(string path, string countKey)
    {
        SidecarStamp? s;
        try { s = stat(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (StatKind.IoError, default); }
        catch { return (StatKind.Missing, default); }
        count?.Invoke(countKey, 1);
        return s is { } found ? (StatKind.Found, found) : (StatKind.Missing, default);
    }

    private readonly Func<string, SidecarStamp?> stat;
    private readonly Func<string, string> readText;
    private readonly Func<long> nowMs;
    private readonly Action<string, int>? count;
    private readonly long negativeTtlMs;
    private readonly long racyWindowMs;

    private readonly object gate = new();
    private readonly Dictionary<string, Presence> presence = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextEntry> texts = new(StringComparer.OrdinalIgnoreCase);
    private string? root;
    private long epoch;

    /// <summary>Stats made looking for a sidecar in a mod folder: the 30 s sweep.</summary>
    internal const string PresenceStatsKey = "presence stats";
    /// <summary>Stats made checking a known sidecar's stamp: one per Proteus mod per discovery.</summary>
    internal const string MetadataStatsKey = "metadata stats";
    internal const string MetadataReadsKey = "metadata reads";

    public SidecarCache(Func<string, SidecarStamp?>? stat = null, Func<string, string>? readText = null,
                        Func<long>? nowMs = null, Action<string, int>? count = null,
                        long negativeTtlMs = 30_000, long racyWindowMs = 3_000)
    {
        this.stat          = stat ?? StatFile;
        this.readText      = readText ?? File.ReadAllText;
        this.nowMs         = nowMs ?? (() => Environment.TickCount64);
        this.count         = count;
        this.negativeTtlMs = negativeTtlMs;
        this.racyWindowMs  = racyWindowMs;
    }

    /// <summary>The file's stamp, or null when it does not exist.</summary>
    internal static SidecarStamp? StatFile(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? new SidecarStamp(fi.LastWriteTimeUtc.Ticks, fi.Length) : null;
    }

    /// <summary>
    /// Whether <paramref name="modDir"/> may have a sidecar. True when it was last seen with one (the caller's
    /// <see cref="ReadMetadata"/> re-checks), false when it was seen without one within the TTL, otherwise one stat. A
    /// stat that fails says true, so the read surfaces the failure.
    /// </summary>
    public bool MayHaveSidecar(string modsRoot, string modDir)
    {
        long e;
        lock (gate)
        {
            if (!string.Equals(root, modsRoot, StringComparison.OrdinalIgnoreCase))
            {
                presence.Clear();
                texts.Clear();
                root = modsRoot;
                epoch++;
            }
            if (presence.TryGetValue(modDir, out var p))
            {
                if (p.Present) return true;
                if (nowMs() - p.At < negativeTtlMs) return false;
            }
            e = epoch;
        }

        var (kind, _) = TryStat(MetaPath(modsRoot, modDir), PresenceStatsKey);
        if (kind == StatKind.IoError) return true;
        bool present = kind == StatKind.Found;

        lock (gate)
            if (epoch == e) presence[modDir] = new Presence(present, nowMs());
        return present;
    }

    /// <summary>
    /// The metadata text at <paramref name="metaPath"/>: from the cache when its stamp is unchanged and trusted,
    /// otherwise read from the drive. One stat either way.
    /// </summary>
    public MetaRead ReadMetadata(string modDir, string metaPath)
    {
        long e;
        TextEntry? cached;
        lock (gate)
        {
            e = epoch;
            texts.TryGetValue(metaPath, out cached);
        }

        var (kind, stamp) = TryStat(metaPath, MetadataStatsKey);
        if (kind == StatKind.IoError) return new MetaRead(MetaReadKind.IoError, null, default);

        if (kind == StatKind.Missing)
        {
            // Unknown, not absent: a sidecar missing for a moment (an atomic replace, a reinstall) is looked at again
            // on the very next call. Dropping an entry can never hide a mod, so no epoch check.
            lock (gate)
            {
                presence.Remove(modDir);
                texts.Remove(metaPath);
            }
            return new MetaRead(MetaReadKind.Missing, null, default);
        }

        long now = nowMs();
        bool sameStamp = cached != null && cached.Stamp == stamp;
        if (sameStamp && cached!.Trusted(racyWindowMs))
            return cached.Unparsable
                ? new MetaRead(MetaReadKind.Unparsable, null, stamp)
                : new MetaRead(MetaReadKind.Ok, cached.Text, stamp);

        string text;
        // Any failure to read is a failure this time, never a cached answer: the next call tries again.
        try { text = readText(metaPath); }
        catch { return new MetaRead(MetaReadKind.IoError, null, stamp); }
        count?.Invoke(MetadataReadsKey, 1);

        lock (gate)
            if (epoch == e)
            {
                // The window runs from the first read under this stamp; this read is the newest text.
                texts[metaPath] = new TextEntry(modDir, stamp, text, sameStamp ? cached!.FirstSeenAt : now, now, false);
                presence[modDir] = new Presence(true, now);
            }
        return new MetaRead(MetaReadKind.Ok, text, stamp);
    }

    /// <summary>The <paramref name="text"/> read at <paramref name="stamp"/> does not parse: serve
    /// <see cref="MetaReadKind.Unparsable"/> for it, once trusted, instead of reading and parsing it again. Only when
    /// that very text is still the one cached: a newer read under the same stamp may have parsed.</summary>
    public void NoteUnparsable(string metaPath, SidecarStamp stamp, string? text)
    {
        lock (gate)
            if (texts.TryGetValue(metaPath, out var t) && t.Stamp == stamp && ReferenceEquals(t.Text, text))
                texts[metaPath] = t with { Text = null, Unparsable = true };
    }

    /// <summary>Forget everything about one mod folder: added, deleted, renamed or edited.</summary>
    public void Forget(string modDir)
    {
        lock (gate)
        {
            epoch++;
            presence.Remove(modDir);
            List<string>? drop = null;
            foreach (var (path, t) in texts)
                if (string.Equals(t.ModDir, modDir, StringComparison.OrdinalIgnoreCase))
                    (drop ??= []).Add(path);
            if (drop != null)
                foreach (var path in drop) texts.Remove(path);
        }
    }

    /// <summary>Forget one metadata file's text: Proteus just wrote it.</summary>
    public void ForgetPath(string metaPath)
    {
        lock (gate)
        {
            epoch++;
            texts.Remove(metaPath);
        }
    }

    /// <summary>Forget everything: Penumbra came back, or the user asked for a refresh.</summary>
    public void ForgetAll()
    {
        lock (gate)
        {
            epoch++;
            presence.Clear();
            texts.Clear();
        }
    }

    internal static string MetaPath(string modsRoot, string modDir)
        => Path.Combine(modsRoot, modDir, SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile);
}
