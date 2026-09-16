using System;
using System.Collections.Generic;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The two pieces a colour edit now leans on to skip work: <see cref="SecondSkinService.SlotHash"/>, which decides
/// whether a shell texture is unchanged, and <see cref="SecondSkinService.ShellGeometryKey"/>, which decides whether
/// a built shell mesh can be reused. Both fail in the dangerous direction if they are wrong — a stale texture or a
/// stale mesh with nothing in the log to say so — so each is pinned against the change it must notice.
/// </summary>
public class ShellBuildMemoTests
{
    private static byte[] Buffer(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void SlotHash_IsStableForTheSameBytes()
    {
        var a = Buffer((9 << 20) + 13, 1);   // spans several chunks and ends mid-word
        Assert.Equal(SecondSkinService.SlotHash(a), SecondSkinService.SlotHash((byte[])a.Clone()));
    }

    /// <summary>One byte anywhere must move it — first chunk, a chunk boundary, and the trailing partial word.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData((4 << 20) - 1)]
    [InlineData(4 << 20)]
    [InlineData((9 << 20) + 12)]
    public void SlotHash_SeesASingleByteChange(int at)
    {
        var a = Buffer((9 << 20) + 13, 2);
        var b = (byte[])a.Clone();
        b[at] ^= 0x01;
        Assert.NotEqual(SecondSkinService.SlotHash(a), SecondSkinService.SlotHash(b));
    }

    /// <summary>Moving a chunk's content to another chunk must not hash the same — chunks are folded in order.</summary>
    [Fact]
    public void SlotHash_IsOrderSensitiveAcrossChunks()
    {
        const int Chunk = 4 << 20;
        var a = new byte[2 * Chunk];
        var b = new byte[2 * Chunk];
        Buffer(Chunk, 3).CopyTo(a, 0);
        Buffer(Chunk, 4).CopyTo(a, Chunk);
        Buffer(Chunk, 4).CopyTo(b, 0);
        Buffer(Chunk, 3).CopyTo(b, Chunk);
        Assert.NotEqual(SecondSkinService.SlotHash(a), SecondSkinService.SlotHash(b));
    }

    private static SecondSkinWriter.SourceSpec Body(byte[] model, string key = "keep:-|uv:-")
        => new(model, DelegateKey: key);

    private static SecondSkinLayer Layer(byte[]? coverage, float bust = 0f, string material = "/mt_a.mtrl")
        => new()
        {
            MaterialName = material,
            Coverage = coverage,
            CoverageWidth = coverage == null ? 0 : 16,
            CoverageHeight = coverage == null ? 0 : 16,
            BustBridgeStrength = bust,
        };

    [Fact]
    public void GeometryKey_IsEqualForEqualInputs()
    {
        var model = Buffer(4096, 5);
        var cov = Buffer(256, 6);
        Assert.Equal(
            SecondSkinService.ShellGeometryKey([Body(model)], [Layer(cov)], null),
            SecondSkinService.ShellGeometryKey([Body((byte[])model.Clone())], [Layer((byte[])cov.Clone())], null));
    }

    [Fact]
    public void GeometryKey_MovesWithEverythingAMeshIsMadeOf()
    {
        var model = Buffer(4096, 7);
        var cov = Buffer(256, 8);
        var baseline = SecondSkinService.ShellGeometryKey([Body(model)], [Layer(cov)], null);

        var otherModel = (byte[])model.Clone(); otherModel[100] ^= 1;
        var otherCov = (byte[])cov.Clone(); otherCov[3] ^= 1;

        Assert.NotEqual(baseline, SecondSkinService.ShellGeometryKey([Body(otherModel)], [Layer(cov)], null));
        Assert.NotEqual(baseline, SecondSkinService.ShellGeometryKey([Body(model)], [Layer(otherCov)], null));
        Assert.NotEqual(baseline, SecondSkinService.ShellGeometryKey([Body(model)], [Layer(cov, bust: 1f)], null));
        Assert.NotEqual(baseline, SecondSkinService.ShellGeometryKey([Body(model)], [Layer(cov, material: "/mt_b.mtrl")], null));
        Assert.NotEqual(baseline, SecondSkinService.ShellGeometryKey([Body(model, "keep:-|uv:gen3>bibo:False")], [Layer(cov)], null));
        Assert.NotEqual(baseline, SecondSkinService.ShellGeometryKey([Body(model)], [Layer(cov)], Buffer(64, 9)));
    }

    /// <summary>Anything the key cannot describe must make the build uncacheable, never cache it under a partial key.</summary>
    [Fact]
    public void GeometryKey_RefusesWhatItCannotDescribe()
    {
        var model = Buffer(4096, 10);
        Assert.Null(SecondSkinService.ShellGeometryKey([new SecondSkinWriter.SourceSpec(model)], [Layer(null)], null));
        Assert.Null(SecondSkinService.ShellGeometryKey(
            [new SecondSkinWriter.SourceSpec(model, KeepMaterial: _ => true, DelegateKey: "keep:-|uv:-")], [Layer(null)], null));
    }
}
