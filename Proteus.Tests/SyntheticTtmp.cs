using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Proteus.Tests;

/// <summary>
/// A minimal TexTools <c>.ttmp2</c>, written to a temp file.
/// <para/>
/// Same reason as <see cref="SyntheticPack"/>: the pack these tests were written against lives on one
/// machine's Desktop, and a test gated on <c>if (!File.Exists(...)) return;</c> stops asserting the moment
/// it moves — silently, and while the code it covers can still break.
/// <para/>
/// The SqPack side is built here rather than mocked because the block layout IS the thing under test: a
/// 128-byte-aligned chain of deflate blocks addressed through a mip table, where every offset is relative
/// to a different base.
/// </summary>
internal sealed class SyntheticTtmp : IDisposable
{
    /// <summary>SqPack splits a mip into chunks so no block's DEFLATED size can reach the 32000 sentinel
    /// that means "stored". Real files use 16 KB; matching that also gives multi-block mips for free.</summary>
    private const int ChunkSize = 16000;

    internal string Path { get; }

    private SyntheticTtmp(string path) => Path = path;

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* a temp file that outlives the test harms nothing */ }
    }

    /// <summary>One file the manifest declares, and the SqPack slice behind it.</summary>
    internal sealed record Entry(string GamePath, byte[] Slice, string? Category = "Unknown");

    // ── the pack ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A wizard-layout pack (the shape TexTools writes today) with one group and one option holding every
    /// file. Files sharing a slice are written once and aliased, which is what a real Atramentum Luminis
    /// pack does — its six paths all point at byte 0.
    /// </summary>
    internal static SyntheticTtmp Wizard(string name, string author, params Entry[] files)
        => Write(name, author, "2.0w", files, simple: false);

    /// <summary>A simple-layout pack: one flat <c>SimpleModsList</c>, no groups or options.</summary>
    internal static SyntheticTtmp Simple(string name, string author, params Entry[] files)
        => Write(name, author, "2.0s", files, simple: true);

    /// <summary>A pack declaring the old v1 format, which the reader must refuse by name.</summary>
    internal static SyntheticTtmp Version1(string name, params Entry[] files)
        => Write(name, "Tests", "1.0", files, simple: true);

    /// <summary>A pack whose manifest promises a file the data blob is far too short to hold.</summary>
    internal static SyntheticTtmp Overrunning(string name, Entry file)
    {
        var blob = file.Slice;
        var rows = new List<string>
        {
            Row(file.GamePath, 0, blob.Length + 4096, file.Category),
        };
        return WriteRaw(name, "Tests", "2.0s", simple: true, rows, blob);
    }

    private static SyntheticTtmp Write(
        string name, string author, string version, Entry[] files, bool simple)
    {
        // Distinct slices are concatenated once; a repeated slice is aliased at the same offset, exactly
        // as TexTools writes a pack whose several paths share one texture.
        var blob = new MemoryStream();
        var offsets = new Dictionary<byte[], long>(ReferenceEqualityComparer.Instance);
        var rows = new List<string>();
        foreach (var f in files)
        {
            if (!offsets.TryGetValue(f.Slice, out var offset))
            {
                offset = blob.Length;
                blob.Write(f.Slice, 0, f.Slice.Length);
                offsets[f.Slice] = offset;
            }
            rows.Add(Row(f.GamePath, offset, f.Slice.Length, f.Category));
        }
        return WriteRaw(name, author, version, simple, rows, blob.ToArray());
    }

    private static SyntheticTtmp WriteRaw(
        string name, string author, string version, bool simple, List<string> rows, byte[] blob)
    {
        var sb = new StringBuilder();
        sb.Append("{\"MinimumFrameworkVersion\":\"1.3.0.0\",\"TTMPVersion\":").Append(Quote(version));
        sb.Append(",\"Name\":").Append(Quote(name));
        sb.Append(",\"Author\":").Append(Quote(author));
        sb.Append(",\"Version\":\"1.0.0\",\"Description\":null,\"Url\":\"\",");

        if (simple)
        {
            sb.Append("\"ModPackPages\":null,\"SimpleModsList\":[");
            sb.Append(string.Join(",", rows));
            sb.Append("]}");
        }
        else
        {
            sb.Append("\"ModPackPages\":[{\"PageIndex\":0,\"ModGroups\":[{\"GroupName\":\"Default Group\",");
            sb.Append("\"SelectionType\":\"Single\",\"OptionList\":[{\"Name\":\"Default Option\",");
            sb.Append("\"Description\":null,\"ImagePath\":\"\",\"ModsJsons\":[");
            sb.Append(string.Join(",", rows));
            sb.Append("],\"GroupName\":\"Default Group\",\"SelectionType\":\"Single\",\"IsChecked\":true}]}]}],");
            sb.Append("\"SimpleModsList\":null}");
        }

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".ttmp2");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "TTMPL.mpl", Encoding.UTF8.GetBytes(sb.ToString()));
            if (blob.Length > 0) WriteEntry(zip, "TTMPD.mpd", blob);
        }
        return new SyntheticTtmp(path);
    }

    /// <summary>A pack with a manifest but no data blob at all.</summary>
    internal static SyntheticTtmp WithoutData(string name)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-synth-" + Guid.NewGuid().ToString("N") + ".ttmp2");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            WriteEntry(zip, "TTMPL.mpl", Encoding.UTF8.GetBytes(
                "{\"TTMPVersion\":\"2.0s\",\"Name\":" + Quote(name)
              + ",\"Author\":\"Tests\",\"SimpleModsList\":[]}"));
        return new SyntheticTtmp(path);
    }

    private static string Row(string gamePath, long offset, long size, string? category)
        => "{\"Name\":\"Body\",\"Category\":" + Quote(category ?? "Unknown")
         + ",\"FullPath\":" + Quote(gamePath)
         + ",\"ModOffset\":" + offset + ",\"ModSize\":" + size
         + ",\"DatFile\":\"040000\",\"IsDefault\":false,\"ModPackEntry\":null}";

    // ── the SqPack slice ─────────────────────────────────────────────────────

    /// <summary>
    /// A type-4 (texture) SqPack slice for an uncompressed B8G8R8A8 image with a full mip chain.
    /// </summary>
    /// <param name="pixel">
    /// Mip level, x, y → the BGRA word written at that texel. Called for every mip so a test can assert
    /// the decoded bytes rather than only the length.
    /// </param>
    /// <param name="stored">
    /// Write the blocks VERBATIM behind the 32000 sentinel rather than deflating them — the other half of
    /// the block format, and the half a compressible test image would never reach on its own.
    /// </param>
    /// <param name="overstateLastMipBy">
    /// Add this to the LAST mip's stated <c>DecompressedSize</c> without changing what its blocks hold.
    /// Real packs do this: the pack these tests were written against states 16 bytes for its 1×1 mip and
    /// decodes 8. Sizing the output from the mip table instead of the block headers failed that texture on
    /// its final block, so a decoder that trusts the table must fail this.
    /// </param>
    internal static byte[] TextureSlice(
        int width, int height, int mipCount,
        Func<int, int, int, uint>? pixel = null, bool stored = false, int overstateLastMipBy = 0)
    {
        pixel ??= (mip, x, y) => (uint)(0xFF000000 | ((x * 7 + y * 13 + mip * 29) & 0xFFFFFF));

        // Mip pixels, level 0 first.
        var mips = new List<byte[]>();
        for (int m = 0; m < mipCount; m++)
        {
            int w = Math.Max(1, width >> m), h = Math.Max(1, height >> m);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((y * w + x) * 4, 4), pixel(m, x, y));
            mips.Add(buf);
        }

        // Each mip's blocks, and the on-disk size of each.
        var blocks = mips.Select(Chunk).Select(cs => cs.Select(c => Block(c, stored)).ToList()).ToList();

        int lodCount = mipCount;
        int totalBlocks = blocks.Sum(b => b.Count);
        int headerSize = (int)Align(0x18 + lodCount * 20 + totalBlocks * 2, 128);

        // .tex header: 80 bytes, with surface offsets relative to the START of the .tex file.
        var tex = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(0, 4), 0x00800000);   // attribute
        BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(4, 4), 0x1450);       // B8G8R8A8
        BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(8, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(10, 2), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(tex.AsSpan(12, 2), 1);           // depth
        tex[14] = (byte)mipCount;
        for (int i = 0; i < 3; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(16 + i * 4, 4), (uint)Math.Min(i, mipCount - 1));
        long surface = tex.Length;
        for (int m = 0; m < mipCount && m < 13; m++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tex.AsSpan(28 + m * 4, 4), (uint)surface);
            surface += mips[m].Length;
        }

        // Body: the .tex header, then every block, each starting on a 128-byte boundary.
        var body = new MemoryStream();
        body.Write(tex, 0, tex.Length);
        var lodOffsets = new List<(long Offset, long Size, int Index)>();
        int blockIndex = 0;
        foreach (var mipBlocks in blocks)
        {
            long start = body.Length;
            foreach (var b in mipBlocks)
            {
                // Padded from the BLOCK's own start, not from the body origin: a block occupies
                // align(header + payload, 128) bytes and the next one begins right after. Aligning to an
                // absolute grid instead put the second block at a different place than the reader's
                // relative step lands on, and it read the padding as a header.
                long blockStart = body.Length;
                body.Write(b, 0, b.Length);
                long target = blockStart + Align(b.Length, 128);
                while (body.Length < target) body.WriteByte(0);
            }
            lodOffsets.Add((start, body.Length - start, blockIndex));
            blockIndex += mipBlocks.Count;
        }

        var slice = new byte[headerSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(0, 4), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(4, 4), 4);   // type: texture
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(8, 4), (uint)(tex.Length + mips.Sum(m => m.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(0x14, 4), (uint)lodCount);
        for (int m = 0; m < lodCount; m++)
        {
            int at = 0x18 + m * 20;
            long stated = mips[m].Length + (m == lodCount - 1 ? overstateLastMipBy : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(at, 4), (uint)lodOffsets[m].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(at + 4, 4), (uint)lodOffsets[m].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(at + 8, 4), (uint)stated);
            BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(at + 12, 4), (uint)lodOffsets[m].Index);
            BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(at + 16, 4), (uint)blocks[m].Count);
        }
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(slice.AsSpan(headerSize));
        return slice;
    }

    /// <summary>A slice of some type other than texture — a binary file, as a .mtrl arrives.</summary>
    internal static byte[] BinarySlice(int payloadBytes = 256)
    {
        var slice = new byte[128 + payloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(0, 4), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(4, 4), 2);   // type: binary
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(8, 4), (uint)payloadBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(0x14, 4), 1);
        return slice;
    }

    /// <summary>The BGRA bytes the slice's mip 0 should decode to, for asserting a round-trip.</summary>
    internal static byte[] ExpectedMip0(int width, int height, Func<int, int, int, uint> pixel)
    {
        var buf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((y * width + x) * 4, 4), pixel(0, x, y));
        return buf;
    }

    private static IEnumerable<byte[]> Chunk(byte[] data)
    {
        if (data.Length == 0) { yield return data; yield break; }
        for (int at = 0; at < data.Length; at += ChunkSize)
            yield return data.AsSpan(at, Math.Min(ChunkSize, data.Length - at)).ToArray();
    }

    /// <summary>One SqPack data block: a 16-byte header, then raw deflate or the bytes verbatim.</summary>
    private static byte[] Block(byte[] payload, bool stored)
    {
        byte[] body;
        uint compressedField;
        if (stored)
        {
            body = payload;
            compressedField = 32000;   // the sentinel: "not deflated, read DecompressedSize bytes"
        }
        else
        {
            var mem = new MemoryStream();
            using (var deflate = new DeflateStream(mem, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(payload, 0, payload.Length);
            body = mem.ToArray();
            if (body.Length >= 32000)
                throw new InvalidOperationException(
                    "A synthetic block deflated to the stored-block sentinel; use a smaller chunk.");
            compressedField = (uint)body.Length;
        }

        var block = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), 16);   // block header size
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8, 4), compressedField);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12, 4), (uint)payload.Length);
        body.CopyTo(block.AsSpan(16));
        return block;
    }

    private static long Align(long v, int to) => (v + to - 1) / to * to;

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var st = zip.CreateEntry(name).Open();
        st.Write(bytes, 0, bytes.Length);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
