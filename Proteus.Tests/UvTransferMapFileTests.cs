using System;
using System.IO;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A map written by <see cref="UvTransferMapFile"/> read back through <see cref="UVRemapService"/>'s own loader, which
/// the new maps must not need changed.
/// </summary>
public class UvTransferMapFileTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "proteus-uvmapfile-" + Guid.NewGuid().ToString("N"));

    public UvTransferMapFileTests() => Directory.CreateDirectory(Path.Combine(dir, "uvmaps"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>A size² map whose every pixel points at (1 - u, v): a mirror, easy to check from anywhere.</summary>
    private static (ushort[] R, ushort[] G, bool[] Valid) Mirror(int size, Func<int, int, bool>? valid = null)
    {
        var r = new ushort[size * size];
        var g = new ushort[size * size];
        var ok = new bool[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                r[i] = UvTransferBake.Quantize(1f - (float)x / (size - 1));
                g[i] = UvTransferBake.Quantize((float)y / (size - 1));
                ok[i] = valid?.Invoke(x, y) ?? true;
            }
        return (r, g, ok);
    }

    [Fact]
    [Trait("Plan", "AC-tbse-uvmap-1")]
    public void Written_map_reads_through_unchanged_loader() // [AC-tbse-uvmap-1]
    {
        const int size = 64;
        var (r, g, ok) = Mirror(size);
        UvTransferMapFile.WriteAtomic(Path.Combine(dir, "uvmaps", "bibo_to_tbse_transfer.tif"),
                                      UvTransferMapFile.Encode(r, g, ok, size));

        // bibo_to_tbse is indexed by a tbse pixel and stores the bibo coordinate, so it carries tbse into bibo.
        var conv = new UVRemapService(Substitute.For<IPluginLog>(), dir).UvConverter("tbse", "bibo", reach: 0);
        Assert.NotNull(conv);
        for (int x = 0; x < size; x += 7)
        {
            float u = (float)x / (size - 1), v = 0.25f;
            var got = conv!(u, v, 0);
            Assert.NotNull(got);
            Assert.Equal(1f - u, got!.Value.U, 3);
            Assert.Equal(MathF.Round(v * (size - 1)) / (size - 1), got.Value.V, 3);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-29")]
    public void Compressed_map_reads_through_unchanged_loader() // [EDGE-tbse-uvmap-29]
    {
        const int size = 128;
        var (r, g, ok) = Mirror(size, (x, y) => (x / 16 + y / 16) % 2 == 0);
        byte[] bytes = UvTransferMapFile.Encode(r, g, ok, size);
        // Deflate with a predictor: far smaller than the 8 bytes a pixel the raw layout takes.
        Assert.True(bytes.Length < size * size * 8 / 4, $"{bytes.Length} bytes is not compressed");
        UvTransferMapFile.WriteAtomic(Path.Combine(dir, "uvmaps", "bibo_to_tbse_transfer.tif"), bytes);

        var conv = new UVRemapService(Substitute.For<IPluginLog>(), dir).UvConverter("tbse", "bibo", reach: 0)!;
        for (int y = 0; y < size; y += 5)
            for (int x = 0; x < size; x += 5)
            {
                var got = conv((float)x / (size - 1), (float)y / (size - 1), 0);
                if ((x / 16 + y / 16) % 2 != 0) { Assert.Null(got); continue; }
                Assert.NotNull(got);
                Assert.Equal(1f - (float)x / (size - 1), got!.Value.U, 3);
            }
    }

    [Fact]
    [Trait("Plan", "EDGE-tbse-uvmap-28")]
    public void Failed_write_leaves_no_file() // [EDGE-tbse-uvmap-28]
    {
        Assert.Throws<ArgumentException>(() => UvTransferMapFile.Encode(new ushort[4], new ushort[4], new bool[3], 2));
        string target = Path.Combine(dir, "uvmaps", "missing", "x.tif");
        Assert.ThrowsAny<IOException>(() => UvTransferMapFile.WriteAtomic(target, [1, 2, 3]));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".tmp"));
    }
}
