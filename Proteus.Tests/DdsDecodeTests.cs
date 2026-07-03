using System;
using System.IO;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for <see cref="TextureLoader.LoadDdsAsRgba"/> — the DDS overlay path that lets Proteus
/// consume BC7-packed mods. The uncompressed 32-bpp branch is exercised directly (it touches
/// neither Lumina nor the game data manager); the BC7 branch is covered end-to-end at runtime.
/// </summary>
public class DdsDecodeTests
{
    private static TextureLoader NewLoader()
        => new(null!, Substitute.For<IPluginLog>());

    // Builds a minimal uncompressed A8R8G8B8 DDS (DDPF_RGB|ALPHAPIXELS, BGRA channel order) with the
    // given 2×2 RGBA pixels. Pixel dword layout is little-endian: byte order B,G,R,A.
    private static byte[] BuildBgraDds((byte R, byte G, byte B, byte A)[] px, int w, int h)
    {
        var dds = new byte[128 + w * h * 4];
        BitConverter.TryWriteBytes(dds.AsSpan(0),  0x20534444u); // "DDS "
        BitConverter.TryWriteBytes(dds.AsSpan(4),  124u);        // dwSize
        BitConverter.TryWriteBytes(dds.AsSpan(8),  0x1007u);     // flags: caps|height|width|pixelformat
        BitConverter.TryWriteBytes(dds.AsSpan(12), (uint)h);
        BitConverter.TryWriteBytes(dds.AsSpan(16), (uint)w);
        BitConverter.TryWriteBytes(dds.AsSpan(76), 32u);         // ddspf.dwSize
        BitConverter.TryWriteBytes(dds.AsSpan(80), 0x41u);       // DDPF_RGB | DDPF_ALPHAPIXELS
        BitConverter.TryWriteBytes(dds.AsSpan(88), 32u);         // rgbBitCount
        BitConverter.TryWriteBytes(dds.AsSpan(92),  0x00ff0000u); // R mask
        BitConverter.TryWriteBytes(dds.AsSpan(96),  0x0000ff00u); // G mask
        BitConverter.TryWriteBytes(dds.AsSpan(100), 0x000000ffu); // B mask
        BitConverter.TryWriteBytes(dds.AsSpan(104), 0xff000000u); // A mask

        for (int i = 0; i < px.Length; i++)
        {
            uint packed = (uint)(px[i].A << 24 | px[i].R << 16 | px[i].G << 8 | px[i].B);
            BitConverter.TryWriteBytes(dds.AsSpan(128 + i * 4), packed);
        }
        return dds;
    }

    [Fact]
    public void LoadDdsAsRgba_Uncompressed32Bpp_ReordersToRgba()
    {
        var pixels = new (byte, byte, byte, byte)[]
        {
            (10, 20, 30, 40),
            (200, 150, 100, 255),
            (0, 0, 0, 0),
            (255, 254, 253, 252),
        };
        var dds = BuildBgraDds(pixels, 2, 2);

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dds");
        try
        {
            File.WriteAllBytes(path, dds);
            var result = NewLoader().LoadDdsAsRgba(path);

            Assert.NotNull(result);
            var (rgba, w, h) = result.Value;
            Assert.Equal(2, w);
            Assert.Equal(2, h);
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.Equal(pixels[i].Item1, rgba[i * 4]);     // R
                Assert.Equal(pixels[i].Item2, rgba[i * 4 + 1]); // G
                Assert.Equal(pixels[i].Item3, rgba[i * 4 + 2]); // B
                Assert.Equal(pixels[i].Item4, rgba[i * 4 + 3]); // A
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadDdsAsRgba_NotADds_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dds");
        try
        {
            File.WriteAllBytes(path, new byte[200]); // zeroed — bad magic
            Assert.Null(NewLoader().LoadDdsAsRgba(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadDdsAsRgba_MissingFile_ReturnsNull()
        => Assert.Null(NewLoader().LoadDdsAsRgba(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dds")));
}
