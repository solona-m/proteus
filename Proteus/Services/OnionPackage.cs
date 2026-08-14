using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services;

/// <summary>
/// Reader for Onion's <c>.omp</c> overlay packs — the format the competing overlay plugin ships.
/// <para/>
/// An <c>.omp</c> is a plain ZIP holding a <c>meta.json</c> manifest plus the layer images it names
/// (<c>layers/*.png</c> in every pack seen so far). Nothing here writes: the reader hands back the parsed
/// manifest and the archive's entry table, and <see cref="OnionImportService"/> decides what becomes a
/// Proteus overlay.
/// <para/>
/// The format is not published, so every field is parsed defensively and unknown values are REPORTED
/// rather than guessed at — see the Layout/Map/Mode mapping in <see cref="OnionImportService"/>. Only one
/// FormatVersion (2) has been observed; a newer one is read anyway and flagged.
/// </summary>
public static class OnionPackage
{
    public const string Extension = ".omp";

    /// <summary>The manifest file at the archive root.</summary>
    private const string ManifestEntry = "meta.json";

    /// <summary>The highest FormatVersion this reader has actually been checked against.</summary>
    public const int KnownFormatVersion = 2;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Onion writes numeric fields plainly, but a hand-edited pack with "Opacity": "1.0" shouldn't
        // take down the whole import.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>A parsed pack: the manifest, plus every archive entry's name and uncompressed size.</summary>
    public sealed record Contents(OnionManifest Manifest, IReadOnlyDictionary<string, long> Entries)
    {
        /// <summary>
        /// The archive entry backing a layer's <c>File</c>, or null when the manifest names an image the
        /// archive doesn't carry. Normalising the separators is this method's whole job — the case
        /// insensitivity comes from <see cref="Entries"/>'s own comparer, since the manifest and the entry
        /// table are written by different code paths and needn't agree on either.
        /// </summary>
        public string? ResolveEntry(string? file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            var norm = Normalize(file);
            return Entries.ContainsKey(norm) ? norm : null;
        }
    }

    /// <summary>
    /// Parse the manifest and entry table of <paramref name="ompPath"/>. Throws
    /// <see cref="InvalidDataException"/> when the file isn't a readable pack — the caller turns that into
    /// a user-facing message.
    /// </summary>
    public static Contents Read(string ompPath)
    {
        using var zip = ZipFile.OpenRead(ompPath);

        var entries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;              // directory marker
            // Nothing here extracts BY entry name — destination filenames are generated — so a traversal
            // entry can't escape on its own. It's still rejected outright: a pack carrying one is either
            // corrupt or hostile, and neither should be half-imported.
            var name = Normalize(e.FullName);
            if (Path.IsPathRooted(name) || name.Split('/').Any(s => s == ".."))
                throw new InvalidDataException($"The pack contains an unsafe entry path: {e.FullName}");
            entries[name] = e.Length;
        }

        var manifestEntry = zip.GetEntry(ManifestEntry)
            ?? zip.Entries.FirstOrDefault(e =>
                   string.Equals(Normalize(e.FullName), ManifestEntry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Not an Onion pack — it has no meta.json.");

        OnionManifest? manifest;
        using (var s = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<OnionManifest>(s, ReadOptions);

        if (manifest == null)
            throw new InvalidDataException("The pack's meta.json is empty or unreadable.");

        return new Contents(manifest, entries);
    }

    /// <summary>The raw bytes of one archive entry. Re-opens the archive; call it once per layer.</summary>
    public static byte[] ReadEntry(string ompPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(ompPath);
        var entry = zip.GetEntry(entryName)
            ?? zip.Entries.FirstOrDefault(e =>
                   string.Equals(Normalize(e.FullName), Normalize(entryName), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The pack no longer contains {entryName}.");

        using var src = entry.Open();
        using var mem = new MemoryStream(entry.Length > 0 && entry.Length < int.MaxValue ? (int)entry.Length : 0);
        src.CopyTo(mem);
        return mem.ToArray();
    }

    private static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');
}

/// <summary>Root of an <c>.omp</c> pack's <c>meta.json</c>.</summary>
public sealed class OnionManifest
{
    public int FormatVersion { get; set; }
    public string? Identifier { get; set; }
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public string? Website { get; set; }

    public List<OnionLayer>? Layers { get; set; }

    /// <summary>
    /// Onion's own option groups. Kept as raw JSON: the element shape is unpublished and no observed pack
    /// has a non-empty one, so this exists to be counted and reported, never interpreted.
    /// </summary>
    public JsonElement? Groups { get; set; }

    public int TotalLayerCount { get; set; }
}

/// <summary>One image in an <c>.omp</c> pack.</summary>
public sealed class OnionLayer
{
    /// <summary>Archive-relative path of the image, e.g. <c>layers/foo.png</c>.</summary>
    public string? File { get; set; }

    /// <summary>UV space the art was painted for: <c>bibo</c>, <c>gen3</c>, <c>vanilla</c>.</summary>
    public string? Layout { get; set; }

    /// <summary>Texture slot: <c>base</c> is the diffuse.</summary>
    public string? Map { get; set; }

    /// <summary>Blend mode. Only <c>Normal</c> has a Proteus equivalent.</summary>
    public string? Mode { get; set; }

    /// <summary>Paint order, ascending.</summary>
    public int Order { get; set; }

    /// <summary>0–1 layer opacity, baked into the image's alpha on import.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>
    /// Race restriction; empty means every race. Raw JSON because the element type is unpublished — a
    /// non-empty value is reported as unsupported rather than half-understood.
    /// </summary>
    public JsonElement? Races { get; set; }

    /// <summary>Set by Onion when it derived this layer from another layout rather than the artist doing so.</summary>
    public string? GeneratedFrom { get; set; }

    public string? SourceHash { get; set; }
}
