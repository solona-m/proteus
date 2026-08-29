using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Guards <c>proteus_bcn.dll</c>'s block DECODER (<c>proteus_decode_bcn</c>), which
/// <see cref="Proteus.Services.TextureLoader"/> uses in place of Lumina's scalar managed decoder.
/// <para/>
/// A decoder that is merely PLAUSIBLE is the dangerous case: it bakes silently wrong pixels into an
/// output texture instead of raising anything anyone would notice. The runtime check in
/// <c>TextureLoader.DecodeTexSurface</c> compares the first decode of a session against Lumina and
/// latches the native path off on a mismatch, but that needs the game. These tests cover the shim's own
/// plumbing — the part that is hand-written and therefore the part that breaks — without it.
/// <para/>
/// The DLL is P/Invoked directly rather than through TextureLoader because
/// <c>EnsureNativeCompressor</c> resolves it via <c>Plugin.PluginInterface.AssemblyLocation</c>, which
/// only exists inside Dalamud. It reaches the test output as a Content copy from Proteus.csproj.
/// </summary>
public class NativeBcCodecTests
{
    private const string Dll = "proteus_bcn";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void proteus_encode_bc7(IntPtr rgba, int w, int h, int brStart, int brCount, IntPtr outPtr);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void proteus_encode_bc5(IntPtr rgba, int w, int h, int brStart, int brCount, IntPtr outPtr);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int proteus_decode_bcn(int fmt, IntPtr blocks, int w, int brStart, int brCount, IntPtr rgbaOut);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int proteus_bcn_block_bytes(int fmt);

    private const int Bc1 = 1, Bc3 = 3, Bc5 = 5, Bc7 = 7;

    // Not square, and 16 block-rows does not divide evenly by the chunk counts used below — so an
    // off-by-one in the final chunk has somewhere to show up.
    private const int W = 128, H = 64;

    private static bool NativePresent
        => File.Exists(Path.Combine(AppContext.BaseDirectory, Dll + ".dll"));

    /// <summary>Smooth gradients plus a little noise. Pure gradients are reproducible exactly by some BC7
    /// modes, which would hide a partition or index-table bug.</summary>
    private static byte[] MakeImage()
    {
        var img = new byte[W * H * 4];
        var rnd = new Random(12345);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                img[i + 0] = (byte)Math.Clamp(x * 255 / (W - 1) + rnd.Next(-8, 9), 0, 255);
                img[i + 1] = (byte)Math.Clamp(y * 255 / (H - 1) + rnd.Next(-8, 9), 0, 255);
                img[i + 2] = (byte)Math.Clamp((x + y) * 255 / (W + H - 2), 0, 255);
                img[i + 3] = 255;
            }
        return img;
    }

    private static byte[] Encode(byte[] rgba, bool bc7)
    {
        int bw = W / 4, bh = H / 4;
        var outBuf = new byte[bw * bh * 16];
        var hi = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        var ho = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            if (bc7) proteus_encode_bc7(hi.AddrOfPinnedObject(), W, H, 0, bh, ho.AddrOfPinnedObject());
            else proteus_encode_bc5(hi.AddrOfPinnedObject(), W, H, 0, bh, ho.AddrOfPinnedObject());
        }
        finally { hi.Free(); ho.Free(); }
        return outBuf;
    }

    /// <summary>Decode across <paramref name="chunks"/> block-row ranges, exactly as
    /// <c>TextureLoader.DecodeBlockCompressedNative</c> fans the work across cores. <paramref name="fill"/>
    /// pre-stains the output so coverage can be measured — see the coverage tests.</summary>
    private static byte[] Decode(int fmt, byte[] blocks, int chunks, byte fill = 0)
    {
        int bw = W / 4, bh = H / 4;
        var rgba = new byte[W * H * 4];
        if (fill != 0) Array.Fill(rgba, fill);
        var hi = GCHandle.Alloc(blocks, GCHandleType.Pinned);
        var ho = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            long inPtr = hi.AddrOfPinnedObject().ToInt64();
            long outPtr = ho.AddrOfPinnedObject().ToInt64();
            int chunkRows = Math.Max(1, (bh + chunks - 1) / chunks);
            int n = (bh + chunkRows - 1) / chunkRows;
            Parallel.For(0, n, ci =>
            {
                int start = ci * chunkRows;
                int count = Math.Min(chunkRows, bh - start);
                if (count <= 0) return;
                // Blocks point at THIS worker's slice; the output pointer is the whole image, because the
                // native scatter derives absolute row offsets from the block-row index.
                var bp = new IntPtr(inPtr + (long)start * bw * 16);
                Assert.NotEqual(0, proteus_decode_bcn(fmt, bp, W, start, count, new IntPtr(outPtr)));
            });
        }
        finally { hi.Free(); ho.Free(); }
        return rgba;
    }

    private static int MaxErr(byte[] a, byte[] b, int channels)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < channels; c++)
                max = Math.Max(max, Math.Abs(a[i + c] - b[i + c]));
        return max;
    }

    [Fact]
    public void Bc7_RoundTripsThroughEncodeAndDecode()
    {
        if (!NativePresent) return;
        var src = MakeImage();
        var decoded = Decode(Bc7, Encode(src, bc7: true), chunks: 1);

        // A wrong bit layout or format code yields noise, not a near miss, so a loose bound still catches
        // it. The encoder is mode-6-only for speed, hence a tolerance rather than equality.
        Assert.True(MaxErr(src, decoded, 3) <= 20, $"BC7 round-trip max RGB error {MaxErr(src, decoded, 3)}");
    }

    [Fact]
    public void Bc7_OpaqueSourceStaysOpaque()
    {
        if (!NativePresent) return;
        var decoded = Decode(Bc7, Encode(MakeImage(), bc7: true), chunks: 1);
        int min = 255;
        for (int i = 3; i < decoded.Length; i += 4) min = Math.Min(min, decoded[i]);
        Assert.True(min >= 235, $"alpha fell to {min} on an all-255 source");
    }

    [Theory]
    [InlineData(Bc7, 7)]
    [InlineData(Bc5, 5)]
    public void ChunkedDecodeMatchesSingleCall(int fmt, int chunks)
    {
        if (!NativePresent) return;
        var blocks = Encode(MakeImage(), bc7: fmt == Bc7);
        // The whole point of the block-row-range contract: how the work is divided must not change a byte.
        Assert.Equal(Decode(fmt, blocks, chunks: 1), Decode(fmt, blocks, chunks));
    }

    [Theory]
    [InlineData(Bc7, 7)]
    [InlineData(Bc5, 5)]
    public void EveryOutputByteIsWritten(int fmt, int chunks)
    {
        if (!NativePresent) return;
        var blocks = Encode(MakeImage(), bc7: fmt == Bc7);

        // Two different pre-fills, compared. Counting leftover fill bytes does NOT work: roughly one byte
        // in 256 legitimately decodes to any given value, so a 32 KB surface shows ~128 false positives.
        // A byte the decoder never touched is the only kind that can differ between these two runs.
        Assert.Equal(Decode(fmt, blocks, chunks, fill: 0xCD), Decode(fmt, blocks, chunks, fill: 0x3A));
    }

    [Fact]
    public void Bc5_LeavesBlueZeroAndAlphaOpaque()
    {
        if (!NativePresent) return;
        // BC5 is two-channel; TextureLoader's callers rely on this exact convention for normal maps.
        var decoded = Decode(Bc5, Encode(MakeImage(), bc7: false), chunks: 1);
        for (int i = 0; i < decoded.Length; i += 4)
        {
            Assert.Equal(0, decoded[i + 2]);
            Assert.Equal(255, decoded[i + 3]);
        }
    }

    [Theory]
    [InlineData(Bc1, 8)]
    [InlineData(Bc3, 16)]
    [InlineData(Bc5, 16)]
    [InlineData(Bc7, 16)]
    public void BlockStrideMatchesTheFormat(int fmt, int expected)
    {
        if (!NativePresent) return;
        // BC1 packs a block into EIGHT bytes; everything else uses sixteen. This is the same split
        // TextureLoader.Mip0ByteSize encodes, and the C# caller slices its per-worker block ranges with
        // whatever this returns — so if the two ever disagree, BC1 reads every other block and decodes a
        // scrambled image while still reporting success.
        Assert.Equal(expected, proteus_bcn_block_bytes(fmt));
    }

    [Fact]
    public void UnknownFormat_HasNoBlockStride()
    {
        if (!NativePresent) return;
        Assert.Equal(0, proteus_bcn_block_bytes(99));
    }

    /// <summary>
    /// BC1 decode, against hand-built blocks — there is no BC1 encoder to round-trip through, and that gap
    /// is exactly why a wrong 16-byte stride for this format survived the round-trip tests above.
    /// <para/>
    /// Two blocks with DIFFERENT colours, side by side: a 16-byte stride would read block 1's colour from
    /// what is actually block 2's index data, so the second block comes out wrong (and the read runs off
    /// the end of a tightly-sized buffer).
    /// </summary>
    [Fact]
    public void Bc1_DecodesConsecutiveBlocksAtTheRightStride()
    {
        if (!NativePresent) return;

        // 8x4 = two 4x4 blocks in one block-row.
        const int w = 8, h = 4;
        // RGB565: pure red = 0xF800, pure blue = 0x001F, pure green = 0x07E0, black = 0x0000.
        // color0 > color1 selects the 4-colour (opaque) mode; all-zero indices select color0 everywhere.
        var blocks = new byte[2 * 8];
        void Block(int i, ushort c0, ushort c1)
        {
            BitConverter.TryWriteBytes(blocks.AsSpan(i * 8), c0);
            BitConverter.TryWriteBytes(blocks.AsSpan(i * 8 + 2), c1);
            // indices already 0 → every texel takes color0
        }
        Block(0, 0xF800, 0x0000);   // red
        Block(1, 0x001F, 0x0000);   // blue

        var rgba = new byte[w * h * 4];
        var hb = GCHandle.Alloc(blocks, GCHandleType.Pinned);
        var ho = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try { Assert.NotEqual(0, proteus_decode_bcn(Bc1, hb.AddrOfPinnedObject(), w, 0, 1, ho.AddrOfPinnedObject())); }
        finally { hb.Free(); ho.Free(); }

        // Left block red, right block blue. 5/6-bit endpoints expand to 255/0 exactly for these values.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool left = x < 4;
                Assert.Equal(left ? 255 : 0, rgba[i + 0]);   // R
                Assert.Equal(0, rgba[i + 1]);                // G
                Assert.Equal(left ? 0 : 255, rgba[i + 2]);   // B
                Assert.Equal(255, rgba[i + 3]);              // A — opaque mode
            }
    }

    /// <summary>
    /// BC4/BC5 interpolants must be ROUNDED, not truncated.
    /// <para/>
    /// The two conventions differ by exactly 1 on a minority of values — rgbcx's <c>unpack_bc4</c>
    /// computes <c>(l*5 + h*2) / 7</c> where the D3D spec, Lumina and the GPU compute
    /// <c>(l*5 + h*2 + 3) / 7</c>. Measured on a real 2048x2048 BC5 normal map that was ~1200 bytes of
    /// 16.7M, which sounds ignorable and is not: Proteus names every composited texture by a hash of its
    /// content, so a channel shifted by one renames every baked output, forces a full re-bake and makes
    /// the sync plugins re-upload the lot. Matching Lumina bit-for-bit is the whole point.
    /// <para/>
    /// Both selector modes are covered: <c>l &gt; h</c> gives eight interpolated values, <c>l &lt;= h</c>
    /// gives six plus hard 0 and 255.
    /// </summary>
    [Theory]
    [InlineData(208, 127)]   // 8-value mode; the exact block from the measured normal map
    [InlineData(153, 8)]     // 8-value mode, second measured block
    [InlineData(255, 0)]
    [InlineData(11, 100)]    // 6-value mode, chosen so rounding and truncation disagree
    [InlineData(0, 255)]
    [InlineData(64, 64)]     // equal endpoints take the 6-value branch
    public void Bc5_InterpolatesWithRoundingNotTruncation(int l, int h)
    {
        if (!NativePresent) return;

        // One block-row of one block: endpoints in the RED sub-block, selectors 0..7 across pixels 0..7.
        const int w = 4, hgt = 4;
        var blocks = new byte[16];
        blocks[0] = (byte)l;
        blocks[1] = (byte)h;
        ulong sel = 0;
        for (int p = 0; p < 8; p++) sel |= (ulong)p << (3 * p);
        for (int i = 0; i < 6; i++) blocks[2 + i] = (byte)(sel >> (8 * i));

        var rgba = new byte[w * hgt * 4];
        var hb = GCHandle.Alloc(blocks, GCHandleType.Pinned);
        var ho = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try { Assert.NotEqual(0, proteus_decode_bcn(Bc5, hb.AddrOfPinnedObject(), w, 0, 1, ho.AddrOfPinnedObject())); }
        finally { hb.Free(); ho.Free(); }

        var expected = new int[8];
        expected[0] = l;
        expected[1] = h;
        if (l > h)
            for (int i = 2; i < 8; i++) expected[i] = ((8 - i) * l + (i - 1) * h + 3) / 7;
        else
        {
            for (int i = 2; i < 6; i++) expected[i] = ((6 - i) * l + (i - 1) * h + 2) / 5;
            expected[6] = 0;
            expected[7] = 255;
        }

        for (int p = 0; p < 8; p++)
            Assert.Equal(expected[p], rgba[p * 4]);
    }

    [Fact]
    public void UnknownFormat_ReturnsZeroAndWritesNothing()
    {
        if (!NativePresent) return;
        // TextureLoader treats 0 as "fall back to Lumina". If the shim wrote garbage first, that fallback
        // would be papering over a corrupted buffer.
        var blocks = Encode(MakeImage(), bc7: true);
        var outBuf = new byte[W * H * 4];
        Array.Fill(outBuf, (byte)0xCD);

        var hb = GCHandle.Alloc(blocks, GCHandleType.Pinned);
        var ho = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        int rc;
        try { rc = proteus_decode_bcn(99, hb.AddrOfPinnedObject(), W, 0, 1, ho.AddrOfPinnedObject()); }
        finally { hb.Free(); ho.Free(); }

        Assert.Equal(0, rc);
        Assert.All(outBuf, b => Assert.Equal(0xCD, b));
    }
}
