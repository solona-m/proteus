using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Mod-relative paths from data a mod author controls — Proteus's own records inside the mod (<c>Proteus/*.json</c>),
/// which travel with it, and the mod's option file maps — joined onto the mod folder, CONTAINED. A <c>.pmp</c> from
/// anyone can ship any of them, so a path from them is never trusted to stay inside the mod:
/// <see cref="Path.Combine(string, string)"/> drops the mod folder for a rooted path, and <c>..</c> climbs out of it.
/// <para/>
/// Refused on two grounds only, never on length or the environment:
/// <list type="bullet">
/// <item>lexical — blank, a NUL, any <c>:</c> (a drive, a drive-relative path, an alternate data stream, <c>\\?\C:</c>),
/// rooted or starting with a separator (UNC, <c>//server</c>), or any segment made only of dots and spaces
/// (<c>.</c>, <c>..</c>, <c>...</c>, <c>.. </c>) — a benign <c>a/../b</c> too: Proteus never writes one;</item>
/// <item>containment — the full path must land STRICTLY under the allowed folder (the folder itself is refused).</item>
/// </list>
/// Record readers drop what fails (<see cref="NoteDropped"/>); every write, copy, move and delete in the features that
/// keep such records goes through <see cref="TryResolve"/>, <see cref="TryBackup"/> or, for a single target inside an
/// existing try before any I/O, the throwing <see cref="Resolve"/>.
/// </summary>
internal static class ModPath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The lexical check alone (see the class remarks); '/' and '\' both separate.</summary>
    internal static bool IsSafeRel(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (rel.Contains('\0') || rel.Contains(':')) return false;
        if (rel[0] is '/' or '\\' || Path.IsPathRooted(rel)) return false;
        foreach (var segment in rel.Split(Separators))
            if (segment.Length > 0 && segment.All(c => c is '.' or ' ')) return false;
        return true;
    }

    /// <summary>
    /// <paramref name="rel"/> joined onto <paramref name="root"/>: true only when it is <see cref="IsSafeRel"/> and its
    /// full path is strictly under <paramref name="root"/> — and, given <paramref name="within"/> (relative to the root),
    /// strictly under that folder. Never throws.
    /// </summary>
    internal static bool TryResolve(string root, string? rel, out string full, string? within = null)
    {
        full = "";
        if (!IsSafeRel(rel)) return false;
        if (within != null && !IsSafeRel(within)) return false;
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(root, rel!.Replace('/', Path.DirectorySeparatorChar)));
            if (!StrictlyUnder(candidate, root)) return false;
            if (within != null && !StrictlyUnder(candidate, Path.Combine(root, within))) return false;
            full = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                         or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>As <see cref="TryResolve"/>, but throws <see cref="UnsafeModPathException"/> where it would return false.
    /// Only for a single target inside an existing try, before any I/O.</summary>
    internal static string Resolve(string root, string rel, string? within = null)
        => TryResolve(root, rel, out var full, within) ? full : throw new UnsafeModPathException(rel);

    /// <summary>A feature's backup of <paramref name="rel"/>: under <c>&lt;mod&gt;/Proteus/&lt;subdir&gt;</c>, never outside it.</summary>
    internal static bool TryBackup(string modRoot, string subdir, string? rel, out string full)
        => TryResolve(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir, subdir), rel, out full);

    /// <inheritdoc cref="TryBackup"/>
    internal static string Backup(string modRoot, string subdir, string rel)
        => TryBackup(modRoot, subdir, rel, out var full) ? full : throw new UnsafeModPathException(rel);

    private static bool StrictlyUnder(string full, string folder)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // ── dropped entries, said once ──────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, int> Dropped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A record reader dropped <paramref name="count"/> entries naming a path outside the mod: one warning per feature
    /// and mod per session, counts only — no paths (they can carry a user name) and no mod names. Readers run on hot
    /// watcher paths, so the same mod read again says nothing more.
    /// </summary>
    /// <returns>Whether it logged.</returns>
    internal static bool NoteDropped(string feature, string modRoot, int count)
    {
        if (count <= 0) return false;
        string key;
        try { key = feature + "|" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(modRoot)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { key = feature + "|" + modRoot; }
        if (!Dropped.TryAdd(key, count)) return false;
        Plugin.Log?.Warning("[Proteus] {0}: ignored {1} record entries naming a path outside the mod", feature, count);
        return true;
    }

    /// <summary>The drops <see cref="NoteDropped"/> saw this session for mods under <paramref name="root"/>, keyed by
    /// "feature|mod folder" — for the scan of installed mods.</summary>
    internal static IReadOnlyDictionary<string, int> DropsUnder(string root)
    {
        string prefix;
        try { prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new Dictionary<string, int>(); }
        return Dropped.Where(kv => kv.Key[(kv.Key.IndexOf('|') + 1)..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                      .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>A path from a mod's own data that would land outside the mod (see <see cref="ModPath"/>). An
/// <see cref="IOException"/>, so the writers' existing handling of a file they could not touch reports it.</summary>
internal sealed class UnsafeModPathException(string rel) : IOException($"\"{rel}\" is not a path inside the mod");
