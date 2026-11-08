using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Proteus.Services.Matcap;

/// <summary>
/// Every matcap library among the installed mods, found by <see cref="MatcapLibrary.Detect"/> and cached: in memory by
/// each mod's manifest fingerprint (<see cref="BodyModIndex.Fingerprint"/>), and on disk in
/// <c>matcap-libraries.json</c> under the plugin's config folder, so a later session costs a directory listing per mod.
/// The library files themselves are never copied anywhere.
/// <para/>
/// Thread-safe; <see cref="Refresh"/> belongs on a worker. Penumbra's mod list is IPC, so the caller asks for it on the
/// framework thread and hands it over.
/// </summary>
internal sealed class MatcapIndex
{
    public const string CacheFile = "matcap-libraries.json";
    private const int CacheVersion = 1;

    /// <summary>The libraries as one refresh left them, replaced whole.</summary>
    /// <param name="Libraries">Every slot, ordered by label.</param>
    /// <param name="Providers">Per shared texture path, every mod publishing it (library or not), by folder.</param>
    internal sealed record State(IReadOnlyList<MatcapLibrary> Libraries, IReadOnlyDictionary<string, IReadOnlyList<string>> Providers)
    {
        public static readonly State Empty = new([], new Dictionary<string, IReadOnlyList<string>>());

        public IReadOnlyList<string> ProvidersOf(string texture) =>
            Providers.TryGetValue(texture.ToLowerInvariant(), out var list) ? list : [];

        public MatcapLibrary? Find(string mod, string group) =>
            Libraries.FirstOrDefault(l => l.Mod.Equals(mod, StringComparison.OrdinalIgnoreCase)
                                       && l.Group.Equals(group, StringComparison.Ordinal));
    }

    private sealed record Known(long Fingerprint, string Name, List<MatcapLibrary> Libraries, List<string> Textures);

    private sealed class CacheDoc
    {
        public int Version { get; set; }
        public Dictionary<string, Known> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object gate = new();
    private readonly string? cachePath;
    private readonly Dictionary<string, Known> known = new(StringComparer.OrdinalIgnoreCase);
    private volatile State? current;
    private volatile bool scanning;

    /// <param name="configDir">Where the cache lives; null keeps it in memory only.</param>
    public MatcapIndex(string? configDir)
    {
        cachePath = configDir == null ? null : Path.Combine(configDir, CacheFile);
        LoadCache();
    }

    /// <summary>The libraries, or null until the first refresh (or a cache load) has produced them.</summary>
    public State? Current => current;

    /// <summary>A refresh is running.</summary>
    public bool Scanning => scanning;

    /// <summary>How many mods the last refresh read rather than took from the cache (for tests and the log).</summary>
    public int LastReads { get; private set; }

    /// <summary>
    /// Bring the index up to date: forget mods that are gone, read new ones and any whose manifests changed — every mod
    /// when <paramref name="force"/> (Rescan). The managed Proteus mod is never a library.
    /// </summary>
    /// <param name="mods">Mod folder → display name, as Penumbra lists them.</param>
    public State Refresh(IReadOnlyDictionary<string, string> mods, string modsRoot, bool force = false)
    {
        lock (gate)
        {
            scanning = true;
            try
            {
                if (force) known.Clear();
                foreach (var gone in known.Keys.Where(d => !mods.ContainsKey(d)).ToList()) known.Remove(gone);

                int reads = 0;
                bool changed = force;
                foreach (var (dir, name) in mods)
                {
                    if (string.Equals(dir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)) continue;
                    var root = Path.Combine(modsRoot, dir);
                    long fp = BodyModIndex.Fingerprint(root);
                    if (known.TryGetValue(dir, out var had) && had.Fingerprint == fp)
                    {
                        if (had.Name != name) known[dir] = had with { Name = name };
                        continue;
                    }
                    reads++;
                    changed = true;
                    var libs = MatcapLibrary.Detect(root, dir, name);
                    var textures = MatcapLibrary.CommonTextures(root);
                    known[dir] = new Known(fp, name, libs, textures);
                }
                LastReads = reads;

                var state = Build();
                current = state;
                if (changed) SaveCache();
                return state;
            }
            finally { scanning = false; }
        }
    }

    private State Build()
    {
        var libs = known.Values.SelectMany(k => k.Libraries)
            .OrderBy(l => l.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var providers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libs)
        {
            if (providers.ContainsKey(lib.Texture)) continue;
            providers[lib.Texture] = known.Where(k => k.Value.Textures.Contains(lib.Texture, StringComparer.OrdinalIgnoreCase))
                                          .Select(k => k.Key).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new State(libs, providers);
    }

    private void LoadCache()
    {
        if (cachePath == null || !File.Exists(cachePath)) return;
        try
        {
            var doc = JsonSerializer.Deserialize<CacheDoc>(File.ReadAllText(cachePath));
            if (doc == null || doc.Version != CacheVersion) return;
            lock (gate)
            {
                foreach (var (dir, k) in doc.Mods)
                    if (k?.Libraries != null && k.Textures != null && k.Name != null)
                        known[dir] = k;
                current = Build();
            }
        }
        catch
        {
            // Corrupt or from another version: start over; the next refresh reads everything.
            lock (gate) { known.Clear(); current = null; }
        }
    }

    private void SaveCache()
    {
        if (cachePath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var doc = new CacheDoc { Version = CacheVersion, Mods = new(known, StringComparer.OrdinalIgnoreCase) };
            PenumbraModMeta.AtomicWrite(cachePath, JsonSerializer.Serialize(doc));
        }
        catch
        {
            // A cache that can't be written costs a re-read next session, nothing more.
        }
    }
}
