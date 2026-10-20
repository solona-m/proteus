using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The mods renamed in Penumbra this session, for a short while: what the compositor remaps every list and per-run
/// cache it publishes through, so a composite or discovery that started before a rename cannot bring the old folder
/// back. Thread-safe; time is passed in.
/// </summary>
internal sealed class ModMoveLedger
{
    /// <summary>How long a move is remembered: far longer than any composite runs.</summary>
    public const long LifetimeMs = 120_000;

    /// <param name="NewDir">The folder the mod lives in now.</param>
    /// <param name="NewName">Its display name when the move was handled, or null when not known.</param>
    /// <param name="ExpiresAt">The tick count after which the move is forgotten.</param>
    public sealed record MoveRecord(string NewDir, string? NewName, long ExpiresAt);

    private readonly object gate = new();
    private Dictionary<string, MoveRecord> moves = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, MoveRecord> None =
        new Dictionary<string, MoveRecord>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Note <paramref name="oldDir"/> became <paramref name="newDir"/>. Chains collapse (A→B then B→C leaves A→C and
    /// B→C), a move back (A→B then B→A) drops A's record rather than mapping A onto itself, and the new folder is
    /// forgotten as an old one: it is in use again.
    /// </summary>
    public void Record(string oldDir, string newDir, string? newName, long now)
    {
        lock (gate)
        {
            var next = new Dictionary<string, MoveRecord>(StringComparer.OrdinalIgnoreCase);
            var record = new MoveRecord(newDir, newName, now + LifetimeMs);
            foreach (var (from, to) in moves)
            {
                if (to.ExpiresAt <= now || ModMoveMigration.Same(from, newDir)) continue;
                next[from] = ModMoveMigration.Same(to.NewDir, oldDir) ? record : to;
            }
            next[oldDir] = record;
            moves = next;
        }
    }

    /// <summary>A folder in use again (a mod installed under that name): nothing is remapped away from it any more.</summary>
    public void Forget(string dir)
    {
        lock (gate)
        {
            if (!moves.Keys.Any(k => ModMoveMigration.Same(k, dir))) return;
            var next = new Dictionary<string, MoveRecord>(moves, StringComparer.OrdinalIgnoreCase);
            next.Remove(dir);
            moves = next;
        }
    }

    /// <summary>The moves still remembered at <paramref name="now"/>; an empty map when there are none.</summary>
    public IReadOnlyDictionary<string, MoveRecord> Live(long now)
    {
        lock (gate)
        {
            if (moves.Count == 0) return None;
            if (moves.Values.All(m => m.ExpiresAt > now)) return moves;
            moves = moves.Where(kv => kv.Value.ExpiresAt > now)
                         .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            return moves.Count == 0 ? None : moves;
        }
    }

    /// <summary>
    /// <paramref name="list"/> with every moved mod under its new folder, sidecar and name; the same instance when
    /// nothing in it moved. An old-folder entry whose new folder is also listed is dropped.
    /// </summary>
    public static List<OverlayEntry> Remap(List<OverlayEntry> list, IReadOnlyDictionary<string, MoveRecord> moves,
                                           string modsRoot)
    {
        if (moves.Count == 0 || !list.Any(e => moves.ContainsKey(e.ModDirectory))) return list;

        var present = list.Select(e => e.ModDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<OverlayEntry>(list.Count);
        foreach (var e in list)
        {
            if (!moves.TryGetValue(e.ModDirectory, out var m)) { result.Add(e); continue; }
            if (present.Contains(m.NewDir)) continue;
            result.Add(e with
            {
                ModDirectory = m.NewDir,
                ModName      = m.NewName ?? e.ModName,
                SidecarRoot  = Path.Combine(modsRoot, m.NewDir, SidecarDiscoveryService.SidecarSubdir),
            });
        }
        return result;
    }

    /// <summary>A per-run cache keyed by mod folder, remapped; the same instance when nothing in it moved.</summary>
    public static Dictionary<string, T> RemapKeys<T>(Dictionary<string, T> map, IReadOnlyDictionary<string, MoveRecord> moves)
    {
        if (moves.Count == 0 || !map.Keys.Any(moves.ContainsKey)) return map;
        var result = new Dictionary<string, T>(map.Comparer);
        foreach (var (k, v) in map)
            if (!moves.ContainsKey(k)) result[k] = v;
        foreach (var (k, v) in map)
            if (moves.TryGetValue(k, out var m)) result[m.NewDir] = v;
        return result;
    }

    /// <summary><see cref="RemapKeys{T}"/> for a cache keyed by (mod, group, option).</summary>
    public static Dictionary<(string ModDir, string? Group, string? Option), V> RemapShell<V>(
        Dictionary<(string ModDir, string? Group, string? Option), V> map, IReadOnlyDictionary<string, MoveRecord> moves)
    {
        if (moves.Count == 0 || !map.Keys.Any(k => moves.ContainsKey(k.ModDir))) return map;
        var result = new Dictionary<(string ModDir, string? Group, string? Option), V>(map.Comparer);
        foreach (var (k, v) in map)
            if (!moves.ContainsKey(k.ModDir)) result[k] = v;
        foreach (var (k, v) in map)
            if (moves.TryGetValue(k.ModDir, out var m)) result[(m.NewDir, k.Group, k.Option)] = v;
        return result;
    }

    /// <summary>A cache whose values are files on disk, each under a moved mod pointed into its new folder.</summary>
    public static Dictionary<string, string> RemapPathValues(Dictionary<string, string> map,
                                                             IReadOnlyDictionary<string, MoveRecord> moves, string modsRoot)
    {
        if (moves.Count == 0 || string.IsNullOrEmpty(modsRoot)) return map;
        Dictionary<string, string>? result = null;
        foreach (var (k, v) in map)
        {
            if (CompositorService.ModFolderOf(v, modsRoot) is not { } folder || !moves.TryGetValue(folder, out var m))
                continue;
            result ??= new Dictionary<string, string>(map, map.Comparer);
            result[k] = ModMoveMigration.RemapPathFolder(v, modsRoot, folder, m.NewDir);
        }
        return result ?? map;
    }
}
