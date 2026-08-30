using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The TexTools <c>.ttmp2</c> reader: manifest parsing, and the SqPack block layout its data blob is made
/// of. Everything here goes through <see cref="SyntheticTtmp"/> rather than a pack on disk — see that
/// class for why.
/// </summary>
public class TexToolsPackageTests
{
    /// <summary>The decoded <c>.tex</c>, or a failure carrying the reader's own reason — which is the
    /// difference between "this test broke" and "this test broke because a block header ran past the
    /// file".</summary>
    private static byte[] TexOf(TexToolsPackage.Payload payload)
    {
        Assert.True(payload.IsTexture, payload.Error ?? $"decoded as SqPack type {payload.Type}");
        return payload.Tex!;
    }

    private static Dictionary<long, long> SlicesOf(TexToolsPackage.Contents pack)
    {
        var slices = new Dictionary<long, long>();
        foreach (var f in pack.Files)
            slices[f.Offset] = Math.Max(slices.TryGetValue(f.Offset, out var n) ? n : 0, f.Size);
        return slices;
    }

    // ── manifest ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReadsWizardLayoutWithGroupAndOption()
    {
        var slice = SyntheticTtmp.TextureSlice(64, 64, 3);
        using var pack = SyntheticTtmp.Wizard("Synthwave", "Dame Douleur",
            new SyntheticTtmp.Entry("chara/bibo/highlander_d.tex", slice),
            new SyntheticTtmp.Entry("chara/bibo_high_base.tex", slice, "Body"));

        var read = TexToolsPackage.Read(pack.Path);

        Assert.Equal("Synthwave", read.Name);
        Assert.Equal("Dame Douleur", read.Author);
        Assert.Equal(2, read.Files.Count);
        Assert.All(read.Files, f => Assert.Equal("Default Group", f.Group));
        Assert.All(read.Files, f => Assert.Equal("Default Option", f.Option));
        Assert.Equal("Body", read.Files[1].Category);
    }

    [Fact]
    public void ReadsSimpleLayout()
    {
        using var pack = SyntheticTtmp.Simple("Flat", "Tests",
            new SyntheticTtmp.Entry("chara/gen3/midlander_d.tex", SyntheticTtmp.TextureSlice(32, 32, 2)));

        var read = TexToolsPackage.Read(pack.Path);

        Assert.Single(read.Files);
        Assert.Equal("chara/gen3/midlander_d.tex", read.Files[0].GamePath);
        Assert.Null(read.Files[0].Group);
    }

    /// <summary>
    /// Several paths over ONE payload is the Atramentum Luminis shape — its six virtual paths all sit at
    /// byte 0 — so the reader must report every path while the offsets collapse to a single slice.
    /// </summary>
    [Fact]
    public void AliasedPathsShareOneOffset()
    {
        var slice = SyntheticTtmp.TextureSlice(32, 32, 2);
        using var pack = SyntheticTtmp.Wizard("Aliased", "Tests",
            new SyntheticTtmp.Entry("chara/bibo/highlander_d.tex", slice),
            new SyntheticTtmp.Entry("chara/bibo/viera_d.tex", slice),
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", slice));

        var read = TexToolsPackage.Read(pack.Path);

        Assert.Equal(3, read.Files.Count);
        Assert.Single(read.Files.Select(f => f.Offset).Distinct());
        Assert.Single(SlicesOf(read));
    }

    [Fact]
    public void RefusesVersionOnePacksByName()
    {
        using var pack = SyntheticTtmp.Version1("Old",
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", SyntheticTtmp.TextureSlice(16, 16, 1)));

        var ex = Assert.Throws<InvalidDataException>(() => TexToolsPackage.Read(pack.Path));
        Assert.Contains(".ttmp2", ex.Message);
    }

    [Fact]
    public void RefusesAPackWithNoDataBlob()
    {
        using var pack = SyntheticTtmp.WithoutData("Empty");
        var ex = Assert.Throws<InvalidDataException>(() => TexToolsPackage.Read(pack.Path));
        Assert.Contains("TTMPD.mpd", ex.Message);
    }

    // ── SqPack decoding ──────────────────────────────────────────────────────

    [Fact]
    public void DecodesATextureBackToItsPixels()
    {
        static uint Pixel(int mip, int x, int y)
            => 0xFF000000u | ((uint)x << 16) | ((uint)y << 8) | (uint)mip;

        var slice = SyntheticTtmp.TextureSlice(64, 64, 4, Pixel);
        using var pack = SyntheticTtmp.Wizard("Round", "Tests",
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", slice));

        var read = TexToolsPackage.Read(pack.Path);
        var payloads = TexToolsPackage.ReadPayloads(pack.Path, SlicesOf(read));
        var tex = TexOf(Assert.Single(payloads).Value);

        Assert.Equal(0x1450u, BitConverter.ToUInt32(tex, 4));   // B8G8R8A8
        Assert.Equal(64, BitConverter.ToUInt16(tex, 8));
        Assert.Equal(64, BitConverter.ToUInt16(tex, 10));
        Assert.Equal(4, tex[14]);

        // Mip 0 begins at the offset the .tex header's own surface table states, and holds the pixels the
        // slice was built from — the whole point of reassembling rather than just concatenating blocks.
        uint mip0At = BitConverter.ToUInt32(tex, 28);
        Assert.Equal(80u, mip0At);
        var expected = SyntheticTtmp.ExpectedMip0(64, 64, Pixel);
        Assert.Equal(expected, tex.AsSpan((int)mip0At, expected.Length).ToArray());
    }

    /// <summary>
    /// Every surface offset in the reassembled header must land on the mip it names. This is what makes the
    /// output a real <c>.tex</c> rather than a header followed by plausible bytes, and it is the invariant
    /// that would break if a block's padding were mis-stepped.
    /// </summary>
    [Fact]
    public void SurfaceOffsetsMatchTheConcatenatedMips()
    {
        var slice = SyntheticTtmp.TextureSlice(128, 128, 8);
        using var pack = SyntheticTtmp.Wizard("Offsets", "Tests",
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", slice));

        var read = TexToolsPackage.Read(pack.Path);
        var tex = TexOf(Assert.Single(TexToolsPackage.ReadPayloads(pack.Path, SlicesOf(read))).Value);

        long at = 80;
        for (int m = 0; m < 8; m++)
        {
            int w = Math.Max(1, 128 >> m), h = Math.Max(1, 128 >> m);
            Assert.Equal((uint)at, BitConverter.ToUInt32(tex, 28 + m * 4));
            at += (long)w * h * 4;
        }
        Assert.Equal(at, tex.Length);
    }

    /// <summary>
    /// A mip table that OVERSTATES its last level must not fail the texture.
    /// <para/>
    /// Regression for the real pack this was written against: it states 16 bytes for its 1×1 mip and its
    /// block decodes 8, while every other mip agrees exactly. Sizing the output buffer from the mip table
    /// left it 8 bytes long and threw on the final block, losing a texture that is otherwise perfect.
    /// </summary>
    [Fact]
    public void ToleratesAMipTableThatOverstatesTheSmallestMip()
    {
        var slice = SyntheticTtmp.TextureSlice(64, 64, 7, overstateLastMipBy: 8);
        using var pack = SyntheticTtmp.Wizard("Overstated", "Tests",
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", slice));

        var read = TexToolsPackage.Read(pack.Path);
        var tex = TexOf(Assert.Single(TexToolsPackage.ReadPayloads(pack.Path, SlicesOf(read))).Value);

        // Sized from the blocks, so the 8 phantom bytes are absent rather than left as a tail of zeroes.
        long expected = 80;
        for (int m = 0; m < 7; m++) { int d = Math.Max(1, 64 >> m); expected += (long)d * d * 4; }
        Assert.Equal(expected, tex.Length);
    }

    [Fact]
    public void DecodesStoredBlocks()
    {
        static uint Pixel(int mip, int x, int y) => (uint)(0xFF000000 | (x * 3 + y * 5 + mip));

        var slice = SyntheticTtmp.TextureSlice(32, 32, 3, Pixel, stored: true);
        using var pack = SyntheticTtmp.Wizard("Stored", "Tests",
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", slice));

        var read = TexToolsPackage.Read(pack.Path);
        var tex = TexOf(Assert.Single(TexToolsPackage.ReadPayloads(pack.Path, SlicesOf(read))).Value);

        var expected = SyntheticTtmp.ExpectedMip0(32, 32, Pixel);
        Assert.Equal(expected, tex.AsSpan(80, expected.Length).ToArray());
    }

    /// <summary>A model or material comes back named by its type rather than throwing, so the import can
    /// report what it skipped instead of failing the pack.</summary>
    [Fact]
    public void ReportsNonTexturePayloadsByType()
    {
        using var pack = SyntheticTtmp.Wizard("Mixed", "Tests",
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", SyntheticTtmp.TextureSlice(16, 16, 2)),
            new SyntheticTtmp.Entry("chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl",
                                    SyntheticTtmp.BinarySlice()));

        var read = TexToolsPackage.Read(pack.Path);
        var payloads = TexToolsPackage.ReadPayloads(pack.Path, SlicesOf(read));

        Assert.Equal(2, payloads.Count);
        Assert.Single(payloads.Values, p => p.IsTexture);
        var binary = Assert.Single(payloads.Values, p => !p.IsTexture);
        Assert.Equal(2, binary.Type);
        Assert.Null(binary.Tex);
    }

    /// <summary>A manifest promising bytes the blob does not hold reports that, rather than throwing or —
    /// worse — coming back absent and indistinguishable from a file nobody asked for.</summary>
    [Fact]
    public void ReportsASliceThatRunsPastTheBlob()
    {
        using var pack = SyntheticTtmp.Overrunning("Truncated",
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", SyntheticTtmp.TextureSlice(16, 16, 2)));

        var read = TexToolsPackage.Read(pack.Path);
        var payload = Assert.Single(TexToolsPackage.ReadPayloads(pack.Path, SlicesOf(read))).Value;

        Assert.False(payload.IsTexture);
        Assert.NotNull(payload.Error);
    }

    /// <summary>One corrupt file must not cost the pack's good ones.</summary>
    [Fact]
    public void ACorruptSliceLeavesItsNeighboursReadable()
    {
        var good = SyntheticTtmp.TextureSlice(32, 32, 2);
        var corrupt = SyntheticTtmp.TextureSlice(32, 32, 2);
        // Mip 0's block COUNT, now far more blocks than the slice holds — the walk runs off the end.
        // Deliberately not its compressed SIZE: the reader steps block to block by each block's own
        // header, so a wrong size there changes nothing and the "corrupt" slice decodes perfectly.
        corrupt[0x18 + 16] = 0xFF;
        corrupt[0x18 + 17] = 0xFF;

        using var pack = SyntheticTtmp.Wizard("Half", "Tests",
            new SyntheticTtmp.Entry("chara/bibo/broken_d.tex", corrupt),
            new SyntheticTtmp.Entry("chara/bibo/midlander_d.tex", good));

        var read = TexToolsPackage.Read(pack.Path);
        var payloads = TexToolsPackage.ReadPayloads(pack.Path, SlicesOf(read));

        Assert.Equal(2, payloads.Count);
        Assert.Single(payloads.Values, p => p.IsTexture);
    }
}
