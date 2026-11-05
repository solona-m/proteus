using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The gear shader upgrade's Penumbra temporary mod, one per collection, built only from whole
/// <see cref="RedirectUnit"/>s. Mirrors <see cref="LiveBrushPreview"/>'s bookkeeping: a generation gate so a result that
/// lands after a teardown is never applied, and every collection a redirect was put into kept until it lets go (the
/// player's collection can change under it). Penumbra is reached through delegates, so this needs no game to test.
/// </summary>
internal sealed class ShaderUpgradeRedirects(Func<Guid, Dictionary<string, string>, bool> set, Func<Guid, bool> remove)
{
    private readonly object gate = new();
    private int generation, endedAt, lastApplied;

    /// <summary>Each held collection with the units last put into it (a refused removal keeps older units here).</summary>
    private readonly Dictionary<Guid, List<RedirectUnit>> holding = [];

    /// <summary>The collection the current units are applied in, or null.</summary>
    private Guid? current;

    /// <summary>
    /// Every held unit, replaced wholesale under <see cref="gate"/>, read without it: the compositor's workers ask
    /// <see cref="SourceFor"/> while the framework thread may be inside a Penumbra call under the gate.
    /// </summary>
    private volatile IReadOnlyList<RedirectUnit> snapshot = [];

    private void Snap() => snapshot = holding.Values.SelectMany(u => u).ToList();

    /// <summary>A peek took the current map down; the next apply or restore puts a map back.</summary>
    private bool peekDown;

    /// <summary>Start a new evaluation; its result may only be applied while no teardown came after.</summary>
    public int Begin() { lock (gate) return ++generation; }

    public bool AnythingHeld { get { lock (gate) return holding.Count > 0; } }

    public IReadOnlyList<RedirectUnit> CurrentUnits
    {
        get { lock (gate) return current is { } c && holding.TryGetValue(c, out var u) ? [.. u] : []; }
    }

    public Guid? CurrentCollection { get { lock (gate) return current; } }

    /// <summary>The map a list of units makes: material and mask game paths to their copies.</summary>
    public static Dictionary<string, string> MapOf(IEnumerable<RedirectUnit> units)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in units)
        {
            map[u.MaterialGamePath] = u.MaterialCopy;
            if (u.Mask is { } m) map[m.GamePath] = m.Copy;
        }
        return map;
    }

    private static bool SameMap(IEnumerable<RedirectUnit> a, IEnumerable<RedirectUnit> b)
    {
        var ma = MapOf(a);
        var mb = MapOf(b);
        return ma.Count == mb.Count
            && ma.All(kv => mb.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Put <paramref name="units"/> in <paramref name="collection"/>, replacing what was there. Refused for a generation
    /// at or below the last teardown, or older than the last apply. The redirect comes out of every other collection
    /// first. An unchanged map makes no Penumbra call; an empty one removes.
    /// </summary>
    public bool Apply(int gen, Guid collection, IReadOnlyList<RedirectUnit> units)
    {
        lock (gate)
        {
            if (gen <= endedAt || gen < lastApplied) return false;
            lastApplied = gen;

            foreach (var other in holding.Keys.Where(c => c != collection).ToList())
                if (remove(other)) holding.Remove(other);

            if (units.Count == 0)
            {
                if (holding.ContainsKey(collection) && remove(collection)) holding.Remove(collection);
                current = holding.ContainsKey(collection) ? collection : null;
                peekDown = false;
                Snap();
                return true;
            }

            if (!peekDown && holding.TryGetValue(collection, out var was) && SameMap(was, units))
            {
                current = collection;
                Snap();
                return true;
            }

            // Recorded before asking, so a teardown removes it even if Penumbra took it and still reported failure.
            holding[collection] = [.. units];
            current = collection;
            peekDown = false;
            Snap();
            return set(collection, MapOf(units));
        }
    }

    /// <summary>
    /// Take the current map down in <paramref name="collection"/> for a peek. The map stays recorded; the caller resolves
    /// and then calls <see cref="PeekRestore"/> (trusted) or leaves it down until the next apply (untrusted).
    /// </summary>
    public bool PeekBegin(Guid collection)
    {
        lock (gate)
        {
            if (!holding.ContainsKey(collection)) return false;
            if (!remove(collection)) return false;
            peekDown = true;
            return true;
        }
    }

    /// <summary>Put the recorded map back after a trusted peek, materials and masks together.</summary>
    public bool PeekRestore(Guid collection)
    {
        lock (gate)
        {
            if (!peekDown) return true;
            peekDown = false;
            if (!holding.TryGetValue(collection, out var units) || units.Count == 0) return true;
            return set(collection, MapOf(units));
        }
    }

    /// <summary>A peek in one step: remove, resolve, put the same map back.</summary>
    public T Peek<T>(Guid collection, Func<T> resolve)
    {
        lock (gate)
        {
            PeekBegin(collection);
            try { return resolve(); }
            finally { PeekRestore(collection); }
        }
    }

    /// <summary>
    /// Take every redirect down; a result begun before this is never applied. A removal Penumbra refuses stays held for
    /// the next attempt. Never waits on anything. Returns whether anything came down, and the game paths that were
    /// redirected (for the compositor to forget).
    /// </summary>
    public (bool Removed, List<string> HeldPaths, List<string> HeldMasks) Teardown()
    {
        lock (gate)
        {
            endedAt = generation;
            var paths = holding.Values.SelectMany(MapOf).Select(kv => kv.Key)
                               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var masks = holding.Values.SelectMany(u => u).Where(u => u.Mask != null)
                               .Select(u => Path.GetFileName(u.Mask!.Copy)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool any = false;
            foreach (var c in holding.Keys.ToList())
                if (remove(c)) { holding.Remove(c); any = true; }
            current = null;
            peekDown = false;
            Snap();
            return (any, paths, masks);
        }
    }

    /// <summary>The mask copy file names any held collection still points at.</summary>
    public IReadOnlySet<string> ReferencedMasks()
        => snapshot.Where(u => u.Mask != null)
                   .Select(u => Path.GetFileName(u.Mask!.Copy))
                   .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The source a redirected game path's copy was made from. Lock-free (see <see cref="snapshot"/>).</summary>
    public string? SourceFor(string gamePath)
    {
        foreach (var u in snapshot)
            if (string.Equals(u.MaterialGamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                return u.Upstream;
        return null;
    }

    /// <summary>The upstream of the copy a file is (by copy path), per game path when given; null when it is not ours.</summary>
    public string? SourceForCopy(string file, IEnumerable<string>? gamePaths = null)
    {
        var units = snapshot;
        if (gamePaths != null)
            foreach (var g in gamePaths)
                foreach (var u in units)
                    if (string.Equals(u.MaterialGamePath, g, StringComparison.OrdinalIgnoreCase)
                        && SamePath(u.MaterialCopy, file))
                        return u.Upstream;
        foreach (var u in units)
            if (SamePath(u.MaterialCopy, file)) return u.Upstream;
        return null;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(ShaderUpgradePlan.NormalPath(ShaderUpgradePlan.StripPenumbraPrefix(a)),
                         ShaderUpgradePlan.NormalPath(ShaderUpgradePlan.StripPenumbraPrefix(b)),
                         StringComparison.OrdinalIgnoreCase);
}
