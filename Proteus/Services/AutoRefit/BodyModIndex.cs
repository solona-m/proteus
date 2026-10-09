using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Every installed mod that publishes a choice of bodies, with its sizes read — what the automatic refit searches to
/// find the body a garment was made on, and what its settings offer to refit onto.
/// <para/>
/// A mod is read again only when its manifests change (<see cref="Fingerprint"/>), so refreshing before every refit
/// costs a directory listing per mod rather than a parse of every manifest: a body pack's manifest can be 400 KB.
/// Thread-safe; <see cref="Refresh"/> belongs on a worker.
/// </summary>
internal sealed class BodyModIndex
{
    /// <param name="Dir">The mod's folder under Penumbra's mods root.</param>
    /// <param name="Name">What Penumbra calls it.</param>
    internal sealed record Entry(string Dir, string Name, BodySizeCatalog Catalog);

    private sealed record Known(Entry? Entry, long Fingerprint);

    private readonly object gate = new();

    /// <summary>Every mod looked at, body or not, so a mod that is not a body is not parsed again either.</summary>
    private readonly Dictionary<string, Known> known = new(StringComparer.OrdinalIgnoreCase);

    private volatile IReadOnlyList<Entry>? snapshot;

    /// <summary>The body mods, by name; null until the first <see cref="Refresh"/> has finished.</summary>
    public IReadOnlyList<Entry>? Snapshot => snapshot;

    /// <summary>One body mod by folder, or null when it is not installed or not a body.</summary>
    public Entry? Find(string dir) => snapshot?.FirstOrDefault(e => string.Equals(e.Dir, dir, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bring the index up to date with Penumbra's mod list: forget mods that are gone, read new ones, re-read any whose
    /// manifests changed. Returns the new snapshot.
    /// </summary>
    /// <param name="mods">Mod folder to display name, as Penumbra lists them (IPC, so the caller asks on the framework
    /// thread and hands the answer over).</param>
    public IReadOnlyList<Entry> Refresh(IReadOnlyDictionary<string, string> mods, string modsRoot)
    {
        lock (gate)
        {
            foreach (string gone in known.Keys.Where(d => !mods.ContainsKey(d)).ToList()) known.Remove(gone);

            foreach (var (dir, name) in mods)
            {
                string root = Path.Combine(modsRoot, dir);
                long fingerprint = Fingerprint(root);
                if (known.TryGetValue(dir, out var had) && had.Fingerprint == fingerprint)
                {
                    // Renamed in Penumbra without touching a manifest: same sizes, new name.
                    if (had.Entry != null && had.Entry.Name != name) known[dir] = had with { Entry = had.Entry with { Name = name } };
                    continue;
                }

                Entry? entry = null;
                try
                {
                    var catalog = BodySizeCatalog.Read(root);
                    if (catalog.IsBody) entry = new Entry(dir, name, catalog);
                }
                catch
                {
                    // A mod whose manifest cannot be read is not a body as far as anyone here is concerned.
                }
                known[dir] = new Known(entry, fingerprint);
            }

            var list = known.Values.Select(k => k.Entry).OfType<Entry>()
                            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            snapshot = list;
            return list;
        }
    }

    /// <summary>
    /// Size and write time over a mod's manifests — meta.json, default_mod.json and every group file — so an edit to any
    /// of them, by its author or by Penumbra, is seen without a restart. Zero for a folder that cannot be listed.
    /// </summary>
    internal static long Fingerprint(string modRoot)
    {
        long fp = 17;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (!string.Equals(name, PenumbraModMeta.MetaFile, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, PenumbraModMeta.LegacyDefaultMod, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(file);
                fp = unchecked(fp * 31 + info.Length + info.LastWriteTimeUtc.Ticks);
            }
        }
        catch
        {
            return 0;
        }
        return fp;
    }
}
