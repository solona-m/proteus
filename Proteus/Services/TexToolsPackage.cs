using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proteus.Services;

/// <summary>
/// Reader for TexTools <c>.ttmp2</c> modpacks: a ZIP of a JSON manifest (<c>TTMPL.mpl</c>) and one blob of
/// SqPack-compressed files (<c>TTMPD.mpd</c>) addressed by byte offset. Only textures are decoded; other
/// payloads come back with their type and no bytes.
/// </summary>
public static class TexToolsPackage
{
    public const string Extension = ".ttmp2";

    private const string ManifestEntry = "TTMPL.mpl";
    private const string DataEntry = "TTMPD.mpd";

    /// <summary>SqPack file type for a texture. The only one this reader decodes.</summary>
    public const int TextureType = 4;

    /// <summary>
    /// A block whose stated compressed length is at or above this is STORED, not deflated, and its
    /// decompressed length is the real byte count. SqPack's own sentinel, not a size limit.
    /// </summary>
    private const uint StoredBlockMarker = 32000;

    /// <summary>Data blocks start on a 128-byte boundary.</summary>
    private const int BlockAlignment = 128;

    /// <summary>Ceiling on one reassembled file, so a corrupt header cannot ask for a gigabyte.</summary>
    private const long MaxPayloadBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// One file the manifest declares, and where its bytes live in <c>TTMPD.mpd</c>.
    /// </summary>
    /// <param name="GamePath">The path the file would be redirected to — for an AL pack, a virtual one.</param>
    /// <param name="Group">The manifest group that ships it, or null for a simple pack's flat list.</param>
    public sealed record PackFile(
        string GamePath, long Offset, long Size, string? Group, string? Option, string? Category);

    /// <summary>A parsed pack: its manifest header fields, and every file it declares.</summary>
    public sealed record Contents(
        string Path,
        string Name,
        string Author,
        string? Description,
        string? Version,
        string? Website,
        IReadOnlyList<PackFile> Files);

    /// <summary>
    /// What one <c>.mpd</c> slice turned out to be. <paramref name="Tex"/> is non-null only for a texture that
    /// decoded; a slice that would not decode has <paramref name="Error"/> set rather than being absent.
    /// </summary>
    public readonly record struct Payload(int Type, byte[]? Tex, string? Error = null)
    {
        public bool IsTexture => Type == TextureType && Tex != null;
    }

    /// <summary>
    /// Parse the manifest of <paramref name="ttmpPath"/>. Throws <see cref="InvalidDataException"/> when
    /// the file isn't a readable pack.
    /// </summary>
    public static Contents Read(string ttmpPath)
    {
        using var zip = ZipFile.OpenRead(ttmpPath);

        var manifestEntry = FindEntry(zip, ManifestEntry)
            ?? throw new InvalidDataException(
                "Not a TexTools modpack — it has no TTMPL.mpl.");
        if (FindEntry(zip, DataEntry) == null)
            throw new InvalidDataException(
                "The modpack has no TTMPD.mpd, so none of the files it lists have any data.");

        TtmpManifest? manifest;
        try
        {
            using var s = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<TtmpManifest>(s, ReadOptions);
        }
        catch (JsonException ex)
        {
            // A v1 .ttmp is newline-delimited JSON with no root object, so it lands here.
            throw new InvalidDataException(
                "This looks like an old TexTools .ttmp (version 1), which Proteus can't read. "
              + "Re-export it from TexTools as a .ttmp2.", ex);
        }

        if (manifest == null)
            throw new InvalidDataException("The modpack's TTMPL.mpl is empty or unreadable.");
        if (manifest.TTMPVersion is { Length: > 0 } v && v[0] == '1')
            throw new InvalidDataException(
                $"The modpack declares TTMP version {v}, which Proteus can't read. Re-export it from "
              + "TexTools as a .ttmp2.");

        var files = new List<PackFile>();

        // Wizard packs ("2.0w") nest files under pages/groups/options; simple packs keep one flat list. Both are read.
        foreach (var page in manifest.ModPackPages ?? [])
            foreach (var group in page.ModGroups ?? [])
                foreach (var option in group.OptionList ?? [])
                    foreach (var mod in option.ModsJsons ?? [])
                        Add(mod, group.GroupName, option.Name);

        foreach (var mod in manifest.SimpleModsList ?? [])
            Add(mod, null, null);

        if (files.Count == 0)
            throw new InvalidDataException("The modpack lists no files at all.");

        return new Contents(
            ttmpPath,
            string.IsNullOrWhiteSpace(manifest.Name)
                ? System.IO.Path.GetFileNameWithoutExtension(ttmpPath)
                : manifest.Name!,
            manifest.Author ?? string.Empty,
            manifest.Description,
            manifest.Version,
            manifest.Url,
            files);

        void Add(TtmpMod mod, string? group, string? option)
        {
            if (string.IsNullOrWhiteSpace(mod.FullPath)) return;
            // An unusable row is dropped rather than thrown, so it does not cost the pack's other files.
            if (mod.ModOffset < 0 || mod.ModSize <= 0) return;
            files.Add(new PackFile(
                mod.FullPath!.Replace('\\', '/').TrimStart('/'),
                mod.ModOffset, mod.ModSize, group, option, mod.Category));
        }
    }

    /// <summary>
    /// Decode the requested slices of <c>TTMPD.mpd</c> in one ascending pass over the blob stream, keyed by
    /// offset. A slice that will not decode is recorded with an error rather than throwing.
    /// </summary>
    /// <param name="slices">Offset → byte length, as the manifest states them. Duplicates collapse.</param>
    public static Dictionary<long, Payload> ReadPayloads(
        string ttmpPath, IReadOnlyDictionary<long, long> slices)
    {
        var result = new Dictionary<long, Payload>();
        if (slices.Count == 0) return result;

        using var zip = ZipFile.OpenRead(ttmpPath);
        var data = FindEntry(zip, DataEntry)
            ?? throw new InvalidDataException("The modpack no longer contains TTMPD.mpd.");

        long total = data.Length;
        using var stream = data.Open();

        long position = 0;
        var skip = new byte[64 * 1024];

        foreach (var (offset, size) in slices.OrderBy(kv => kv.Key))
        {
            // Past the end of the blob or absurdly large: recorded as a failure, not dropped.
            if (offset < position || size > MaxPayloadBytes || offset + size > total)
            {
                result[offset] = new Payload(0, null,
                    $"its manifest places it at byte {offset} of a {total}-byte data blob");
                continue;
            }

            while (position < offset)
            {
                int want = (int)Math.Min(skip.Length, offset - position);
                int got = stream.Read(skip, 0, want);
                if (got <= 0) return result;   // truncated blob: nothing further can be reached
                position += got;
            }

            var slice = new byte[size];
            int filled = 0;
            while (filled < slice.Length)
            {
                int got = stream.Read(slice, filled, slice.Length - filled);
                if (got <= 0) break;
                filled += got;
            }
            position += filled;
            if (filled < slice.Length)
            {
                result[offset] = new Payload(0, null, "the modpack's data blob is truncated");
                return result;
            }

            try { result[offset] = DecodePayload(slice); }
            catch (InvalidDataException ex) { result[offset] = new Payload(0, null, ex.Message); }
        }

        return result;
    }

    /// <summary>
    /// Turn one SqPack slice into a loose <c>.tex</c>, the form <see cref="TextureLoader"/> reads.
    /// <code>
    /// header @ 0      : headerSize, type (4 = texture), uncompressedSize, _, _, lodCount   (6 × uint32)
    /// lods   @ 0x18   : lodCount × (compressedOffset, compressedSize, decompressedSize,
    ///                               blockIndex, blockCount)                                (5 × uint32)
    /// texhdr @ headerSize .. headerSize + lods[0].compressedOffset   — the .tex header, stored verbatim
    /// block  @ headerSize + lod.compressedOffset:
    ///           blockHeaderSize, version, compressedLength, decompressedLength             (4 × uint32)
    ///           compressedLength >= 32000 → stored raw, otherwise raw deflate
    ///           the next block starts at the next 128-byte boundary
    /// </code>
    /// The .tex header's surface-offset table already matches the unpadded mip concatenation; do not rewrite it.
    /// </summary>
    internal static Payload DecodePayload(byte[] slice)
    {
        uint headerSize = U32(slice, 0);
        int type = (int)U32(slice, 4);
        int lodCount = (int)U32(slice, 0x14);

        if (headerSize < 0x18 || headerSize > slice.Length)
            throw new InvalidDataException("SqPack header size is outside the file.");
        if (type != TextureType) return new Payload(type, null);
        // One LOD per mip level, and a texture has at most 13.
        if (lodCount is < 1 or > 13)
            throw new InvalidDataException($"A texture with {lodCount} mip levels is not readable.");
        if (0x18 + (long)lodCount * 20 > headerSize)
            throw new InvalidDataException("The mip table runs past the SqPack header.");

        var lods = new (uint Offset, uint Size, uint Decompressed, uint BlockIndex, uint BlockCount)[lodCount];
        for (int i = 0; i < lodCount; i++)
        {
            int at = 0x18 + i * 20;
            lods[i] = (U32(slice, at), U32(slice, at + 4), U32(slice, at + 8),
                       U32(slice, at + 12), U32(slice, at + 16));
        }

        // The .tex header sits uncompressed between the SqPack header and the first data block.
        long texHeaderLength = lods[0].Offset;
        if (texHeaderLength <= 0 || headerSize + texHeaderLength > slice.Length)
            throw new InvalidDataException("The texture header is missing or runs past the file.");

        // Sized from the block headers, not the mip table's DecompressedSize, which can overstate the tail mip.
        long dataLength = 0;
        WalkBlocks((_, _, _, decompressed) => dataLength += decompressed);
        if (texHeaderLength + dataLength > MaxPayloadBytes)
            throw new InvalidDataException($"The texture claims {texHeaderLength + dataLength} bytes.");

        var output = new byte[texHeaderLength + dataLength];
        Buffer.BlockCopy(slice, (int)headerSize, output, 0, (int)texHeaderLength);
        int written = (int)texHeaderLength;

        WalkBlocks((from, length, stored, decompressed) =>
        {
            if (stored)
            {
                Buffer.BlockCopy(slice, (int)from, output, written, (int)decompressed);
                written += (int)decompressed;
                return;
            }

            using var src = new MemoryStream(slice, (int)from, (int)length, writable: false);
            using var inflate = new DeflateStream(src, CompressionMode.Decompress);
            int filled = 0;
            while (filled < decompressed)
            {
                int got = inflate.Read(output, written + filled, (int)decompressed - filled);
                if (got <= 0) break;
                filled += got;
            }
            if (filled != decompressed)
                throw new InvalidDataException("A texture block did not decompress to its stated size.");
            written += filled;
        });

        return new Payload(type, output);

        // Walk every block of every mip in order; shared by the sizing and decode passes so they cannot drift.
        void WalkBlocks(Action<long, long, bool, uint> visit)
        {
            foreach (var lod in lods)
            {
                long at = headerSize + lod.Offset;
                for (uint b = 0; b < lod.BlockCount; b++)
                {
                    if (at + 16 > slice.Length)
                        throw new InvalidDataException("A texture block header runs past the file.");

                    uint blockHeaderSize = U32(slice, (int)at);
                    uint compressed = U32(slice, (int)at + 8);
                    uint decompressed = U32(slice, (int)at + 12);

                    // The payload starts after the header's own stated size, not a hardcoded 16.
                    if (blockHeaderSize is < 16 or > 128)
                        throw new InvalidDataException(
                            $"A texture block header of {blockHeaderSize} bytes is not readable.");

                    bool stored = compressed >= StoredBlockMarker;
                    long length = stored ? decompressed : compressed;
                    long from = at + blockHeaderSize;
                    if (length < 0 || from + length > slice.Length)
                        throw new InvalidDataException("A texture block runs past the file.");

                    visit(from, length, stored, decompressed);
                    at += Align(blockHeaderSize + length, BlockAlignment);
                }
            }
        }
    }

    private static long Align(long value, int to) => (value + to - 1) / to * to;

    private static uint U32(byte[] buffer, int at)
    {
        if (at < 0 || at + 4 > buffer.Length)
            throw new InvalidDataException("A SqPack field runs past the end of the file.");
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at, 4));
    }

    /// <summary>One archive entry by name, case-insensitively.</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
        => zip.GetEntry(name)
        ?? zip.Entries.FirstOrDefault(e => string.Equals(
               e.FullName.Replace('\\', '/').TrimStart('/'), name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Root of a <c>.ttmp2</c> pack's <c>TTMPL.mpl</c>.</summary>
public sealed class TtmpManifest
{
    public string? MinimumFrameworkVersion { get; set; }

    /// <summary>e.g. <c>"2.0w"</c> — the <c>w</c> suffix means the wizard layout (pages and groups).</summary>
    public string? TTMPVersion { get; set; }

    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }

    /// <summary>Wizard packs. Null for a simple one.</summary>
    public List<TtmpPage>? ModPackPages { get; set; }

    /// <summary>Simple packs: one flat file list, no options. Null for a wizard one.</summary>
    public List<TtmpMod>? SimpleModsList { get; set; }
}

public sealed class TtmpPage
{
    public int PageIndex { get; set; }
    public List<TtmpGroup>? ModGroups { get; set; }
}

public sealed class TtmpGroup
{
    public string? GroupName { get; set; }

    /// <summary><c>Single</c> or <c>Multi</c>.</summary>
    public string? SelectionType { get; set; }

    public List<TtmpOption>? OptionList { get; set; }
}

public sealed class TtmpOption
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool IsChecked { get; set; }
    public List<TtmpMod>? ModsJsons { get; set; }
}

/// <summary>One file, addressed by byte offset into <c>TTMPD.mpd</c>.</summary>
public sealed class TtmpMod
{
    public string? Name { get; set; }
    public string? Category { get; set; }

    /// <summary>The path the file redirects. Atramentum Luminis packs put a VIRTUAL path here.</summary>
    public string? FullPath { get; set; }

    public long ModOffset { get; set; }
    public long ModSize { get; set; }
    public string? DatFile { get; set; }
}
