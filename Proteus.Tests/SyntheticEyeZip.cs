using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Proteus.Tests;

/// <summary>
/// A plain <c>.zip</c> of loose eye textures, written to a temp file — the shape eye mods are distributed
/// in, with no manifest of any kind.
/// <para/>
/// Same reason as <see cref="SyntheticPack"/> and <see cref="SyntheticTtmp"/>: the pack these tests were
/// written against lives on one machine's Desktop, and a test gated on its existence stops asserting the
/// moment it moves.
/// </summary>
internal sealed class SyntheticEyeZip : IDisposable
{
    internal string Path { get; }

    private SyntheticEyeZip(string path) => Path = path;

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* a temp file that outlives the test harms nothing */ }
    }

    /// <summary>One entry: an archive path and the image bytes behind it.</summary>
    internal sealed record Entry(string Name, byte[] Bytes);

    /// <summary>The three-file shape a real pack has, under a folder named after the mod.</summary>
    internal static SyntheticEyeZip Standard(string folder = "DT ButterflyEffect", int size = 8,
        Func<int, int, uint>? mask = null)
        => Of(
            new Entry($"{folder}/{folder}_eye_base.png", Png(size, (x, y) => 0xFF808080)),
            new Entry($"{folder}/{folder}_eye_mask.png", Png(size, mask ?? Ring(size))),
            new Entry($"{folder}/{folder}_eye_norm.png", Png(size, (x, y) => 0xFFFF8080)));

    internal static SyntheticEyeZip Of(params Entry[] entries)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "proteus-eye-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var e in entries)
            {
                using var st = zip.CreateEntry(e.Name).Open();
                st.Write(e.Bytes, 0, e.Bytes.Length);
            }
        return new SyntheticEyeZip(path);
    }

    /// <summary>A mask whose RED channel is lit over the middle of the sheet — the glow region, in the
    /// channel the game reads the limbal ring from.</summary>
    internal static Func<int, int, uint> Ring(int size)
        => (x, y) =>
        {
            bool lit = x >= size / 4 && x < size - size / 4 && y >= size / 4 && y < size - size / 4;
            // ABGR as packed below: red is the low byte.
            return lit ? 0xFF00_00FFu : 0xFF00_0000u;
        };

    /// <summary>A mask with nothing in its red channel at all.</summary>
    internal static readonly Func<int, int, uint> Unlit = (x, y) => 0xFF00_5500u;

    /// <summary>An RGBA8 PNG. <paramref name="pixel"/> returns 0xAABBGGRR.</summary>
    internal static byte[] Png(int size, Func<int, int, uint> pixel)
    {
        var raw = new byte[(size * 4 + 1) * size];
        int at = 0;
        for (int y = 0; y < size; y++)
        {
            raw[at++] = 0;   // filter: none
            for (int x = 0; x < size; x++)
            {
                uint p = pixel(x, y);
                raw[at++] = (byte)p;              // R
                raw[at++] = (byte)(p >> 8);       // G
                raw[at++] = (byte)(p >> 16);      // B
                raw[at++] = (byte)(p >> 24);      // A
            }
        }

        using var mem = new MemoryStream();
        mem.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(mem, "IHDR", Header(size));
        Chunk(mem, "IDAT", Deflate(raw));
        Chunk(mem, "IEND", []);
        return mem.ToArray();

        static byte[] Header(int n)
        {
            var h = new byte[13];
            BeInt(h, 0, n); BeInt(h, 4, n);
            h[8] = 8; h[9] = 6;   // 8-bit, RGBA
            return h;
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var mem = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(mem, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return mem.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BeInt(len, 0, data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        s.Write(body);
        var crc = new byte[4];
        BeInt(crc, 0, unchecked((int)Crc32(body)));
        s.Write(crc);
    }

    private static void BeInt(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        }
        return crc ^ 0xFFFFFFFF;
    }
}
