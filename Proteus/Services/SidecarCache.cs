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

    private sealed record TextEntry(string ModDir, SidecarStamp Stamp, string? Text, long FirstSeenAt, bool Unparsable);

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

    internal const string PresenceStatsKey = "presence stats";
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

        SidecarStamp? s;
        try
        {
            s = stat(MetaPath(modsRoot, modDir));
            count?.Invoke(PresenceStatsKey, 1);
        }
        catch { return true; }

        lock (gate)
            if (epoch == e) presence[modDir] = new Presence(s != null, nowMs());
        return s != null;
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

        SidecarStamp? s;
        try
        {
            s = stat(metaPath);
            count?.Invoke(PresenceStatsKey, 1);
        }
        catch { return new MetaRead(MetaReadKind.IoError, null, default); }

        if (s is not { } stamp)
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
        if (sameStamp && now - cached!.FirstSeenAt > racyWindowMs)
            return cached.Unparsable
                ? new MetaRead(MetaReadKind.Unparsable, null, stamp)
                : new MetaRead(MetaReadKind.Ok, cached.Text, stamp);

        string text;
        try
        {
            text = readText(metaPath);
            count?.Invoke(MetadataReadsKey, 1);
        }
        catch { return new MetaRead(MetaReadKind.IoError, null, stamp); }

        lock (gate)
            if (epoch == e)
            {
                // The trust window runs from the first read under this stamp, not from this one.
                texts[metaPath] = new TextEntry(modDir, stamp, text, sameStamp ? cached!.FirstSeenAt : now, false);
                presence[modDir] = new Presence(true, now);
            }
        return new MetaRead(MetaReadKind.Ok, text, stamp);
    }

    /// <summary>The text cached at <paramref name="stamp"/> does not parse: serve <see cref="MetaReadKind.Unparsable"/>
    /// for it, once trusted, instead of reading and parsing it again.</summary>
    public void NoteUnparsable(string metaPath, SidecarStamp stamp)
    {
        lock (gate)
            if (texts.TryGetValue(metaPath, out var t) && t.Stamp == stamp)
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
