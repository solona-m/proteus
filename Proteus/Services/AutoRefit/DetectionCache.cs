using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// What the automatic refit read off each garment — which body and size it was made on, which other parts of that body
/// it reaches, or that it could not be read at all — kept on disk so a garment is read once, not once per equip.
/// <para/>
/// Reading a garment is the expensive half of a refit: every installed body is sampled and the winning body mod's sizes
/// are ranked, hundreds of megabytes of models the first time. A piece put on again, in this session or the next, used
/// to do all of it again before even looking for the refit it already had — and a piece that could not be read at all
/// (gloves with no skin in them) did it on every equip. Keyed by the garment's CONTENT, the slot and race it was read
/// at, the body refitted onto (which wins ties), and <see cref="BodyModIndex.Version"/>: installing, removing or editing
/// a body mod changes the answer, and so the key.
/// </summary>
internal sealed class DetectionCache(string path, IPluginLog log)
{
    /// <param name="Dir">The body mod it was made on (<see cref="VanillaBodyCatalog.Key"/> for the game's body), or ""
    /// when nothing could be read.</param>
    /// <param name="Rel">That body's size, by file.</param>
    /// <param name="Others">Per other slot it reaches, the size there, by file.</param>
    /// <param name="Confidence">Written by NAME: a number would silently change meaning if the enum were ever
    /// reordered, turning a saved "Ambiguous" into something it never was. Numbers written before still read.</param>
    internal sealed record Entry(string Dir, string Rel,
                                 [property: System.Text.Json.Serialization.JsonConverter(
                                     typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BodySizeMatch.Confidence>))]
                                 BodySizeMatch.Confidence Confidence,
                                 bool FromCloth, Dictionary<string, string> Others)
    {
        public long Used { get; set; }
    }

    /// <summary>Entries kept; the least recently used go first. A few hundred bytes each.</summary>
    internal const int Capacity = 4000;

    private readonly object gate = new();
    private Dictionary<string, Entry>? entries;

    /// <param name="bodies">Which body mods are installed (<see cref="BodyModIndex.State.Version"/>) and which of them
    /// the collection has switched on — between two copies of one body, the switched-on one is the answer — and the
    /// game's own body, which a patch can change.</param>
    internal static string KeyOf(byte[] garment, string slot, string race, string preferredDir, string bodies)
        => $"{Convert.ToHexString(SHA256.HashData(garment))}|{slot}|{race}|{preferredDir.ToLowerInvariant()}|{bodies}";

    /// <summary>
    /// What <see cref="KeyOf"/>'s <c>bodies</c> is made of, in one short string: the installed set's version, the body
    /// mods switched on (in any order), and a fingerprint of the game's own body.
    /// </summary>
    internal static string BodiesOf(string installed, IEnumerable<string> enabled, string vanilla)
    {
        string on = string.Join(",", enabled.Select(d => d.ToLowerInvariant()).Distinct().OrderBy(d => d, StringComparer.Ordinal));
        return installed + ":" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(on + "|" + vanilla)))[..16];
    }

    public Entry? TryGet(string key)
    {
        lock (gate)
        {
            if (!Loaded().TryGetValue(key, out var entry)) return null;
            entry.Used = DateTime.UtcNow.Ticks;
            return entry;
        }
    }

    public void Put(string key, Entry entry)
    {
        lock (gate)
        {
            var all = Loaded();
            entry.Used = DateTime.UtcNow.Ticks;
            all[key] = entry;
            if (all.Count > Capacity)
                foreach (string old in all.OrderBy(e => e.Value.Used).Take(all.Count - Capacity).Select(e => e.Key).ToList())
                    all.Remove(old);
            dirty = true;
        }
    }

    /// <summary>Readings added since the file was last written.</summary>
    private bool dirty;

    /// <summary>
    /// Write what was added. Once per batch rather than per reading: the file holds every reading kept, a megabyte or
    /// two when full, and a design changing four slots would otherwise rewrite it four times in a row.
    /// </summary>
    public void Flush()
    {
        lock (gate)
        {
            if (!dirty || entries == null) return;
            dirty = false;
            Save(entries);
        }
    }

    private Dictionary<string, Entry> Loaded()
    {
        if (entries != null) return entries;
        entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) is { } read)
                foreach (var (key, entry) in read)
                    entries[key] = entry with { Others = new Dictionary<string, string>(entry.Others ?? [], StringComparer.Ordinal) };
        }
        catch (Exception ex)
        {
            // Only a cache: a file that cannot be read is started over, never trusted half-read.
            log.Warning(ex, "[Proteus] auto refit: the garment cache at {0} could not be read; starting it over", path);
            entries.Clear();
        }
        return entries;
    }

    private void Save(Dictionary<string, Entry> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PenumbraModMeta.AtomicWrite(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all)));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] auto refit: could not write the garment cache to {0}", path);
        }
    }
}
