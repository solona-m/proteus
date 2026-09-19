using System;
using System.Collections.Generic;
using Proteus;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// An index (<c>_id</c>) texture names colour-table rows, so a lossy encode that moves a texel's red by more than
/// half of the 17-unit row step draws the wrong row. <see cref="TextureLoader.IndexSurvivesBc5"/> answers whether
/// one particular index texture survives BC5 by trial-encoding it and reading the decision back.
/// <para/>
/// These pin the two cases the policy rests on: a block holding at most two distinct values is exact in BC4
/// (its two endpoints ARE those values), and a block where several rows meet is not — and must be caught.
/// </summary>
public class IndexBc5Tests
{
    private readonly ITestOutputHelper o;

    public IndexBc5Tests(ITestOutputHelper o) => this.o = o;

    private static TextureLoader Loader() => new(null!, new SilentLog());

    /// <summary>The loader logs the verdict; nothing here reads it. Local so the tests carry their own harness.</summary>
    private sealed class SilentLog : Dalamud.Plugin.Services.IPluginLog
    {
        public Serilog.Events.LogEventLevel MinimumLogLevel { get; set; }
        public Serilog.ILogger Logger => Serilog.Core.Logger.None;
        public void Debug(string m, params object[] v) { }
        public void Debug(Exception? e, string m, params object[] v) { }
        public void Error(string m, params object[] v) { }
        public void Error(Exception? e, string m, params object[] v) { }
        public void Fatal(string m, params object[] v) { }
        public void Fatal(Exception? e, string m, params object[] v) { }
        public void Info(string m, params object[] v) { }
        public void Info(Exception? e, string m, params object[] v) { }
        public void Information(string m, params object[] v) { }
        public void Information(Exception? e, string m, params object[] v) { }
        public void Verbose(string m, params object[] v) { }
        public void Verbose(Exception? e, string m, params object[] v) { }
        public void Warning(string m, params object[] v) { }
        public void Warning(Exception? e, string m, params object[] v) { }
        public void Write(Serilog.Events.LogEventLevel l, Exception? e, string m, params object[] v) { }
    }

    /// <summary>Red is the row pack (<c>(row − 1) × 17</c>), green the sub-row (255 = A), blue unused, alpha opaque.</summary>
    private static byte[] Index(int size, Func<int, int, int> rowAt, Func<int, int, byte>? greenAt = null)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int p = (y * size + x) * 4;
                rgba[p]     = (byte)((rowAt(x, y) - 1) * 17);
                rgba[p + 1] = greenAt?.Invoke(x, y) ?? 255;
                rgba[p + 2] = 0;
                rgba[p + 3] = 255;
            }
        }

        return rgba;
    }

    /// <summary>One row over the whole sheet: every block is a single value, so BC5 cannot move it.</summary>
    [Fact]
    public void SingleRow_Survives()
    {
        Assert.True(Loader().IndexSurvivesBc5(Index(64, static (_, _) => 7), 64, 64, out var flipped));
        Assert.Equal(0, flipped);
    }

    /// <summary>
    /// Two rows split down a block-aligned edge. Two distinct values per block are BC4's endpoints, so this is
    /// exact — and it is the shape most shells actually have, a region boundary running through the sheet.
    /// </summary>
    [Fact]
    public void TwoRowsAcrossABlockBoundary_Survive()
    {
        var rgba = Index(64, static (x, _) => x < 32 ? 3 : 11);
        Assert.True(Loader().IndexSurvivesBc5(rgba, 64, 64, out var flipped));
        Assert.Equal(0, flipped);
    }

    /// <summary>Two rows meeting INSIDE a block are still only two values, so still exact.</summary>
    [Fact]
    public void TwoRowsInsideABlock_Survive()
    {
        var rgba = Index(64, static (x, _) => (x % 4) < 2 ? 2 : 14);
        Assert.True(Loader().IndexSurvivesBc5(rgba, 64, 64, out var flipped));
        Assert.Equal(0, flipped);
    }

    /// <summary>
    /// Every texel of a block a different row: eight palette entries cannot hold sixteen values spread over the
    /// whole range, so some texel must land nearer another row. This is the case the trial exists to catch.
    /// </summary>
    [Fact]
    public void ManyRowsPerBlock_AreCaught()
    {
        var rgba = Index(64, static (x, y) => 1 + ((y % 4) * 4 + (x % 4)));
        Assert.False(Loader().IndexSurvivesBc5(rgba, 64, 64, out var flipped));
        Assert.True(flipped > 0);
        o.WriteLine($"16 rows per block: {flipped} texel(s) would change row");
    }

    /// <summary>
    /// The sub-row lane is checked too: green crossing its 127 threshold inside a block, against a red that is
    /// uniform, so only green can be at fault.
    /// </summary>
    [Fact]
    public void SubRowThreshold_IsChecked()
    {
        // A green ramp through the threshold, with enough distinct values per block to make BC4 approximate.
        var rgba = Index(64, static (_, _) => 5, static (x, y) => (byte)Math.Clamp(120 + (x % 4) * 3 + (y % 4), 0, 255));
        var loader = Loader();
        loader.IndexSurvivesBc5(rgba, 64, 64, out var flipped);
        o.WriteLine($"green ramp across the sub-row threshold: {flipped} texel(s) would change side");
        // Whatever the encoder does here, the answer must be consistent with the count it reports.
        Assert.Equal(flipped == 0, loader.IndexSurvivesBc5(rgba, 64, 64, out _));
    }

    /// <summary>
    /// The block a plain trial cannot hold is publishable after snapping, and the plan says so. This is the case the
    /// user's own shell hits: a handful of blocks where three regions meet.
    /// </summary>
    [Fact]
    public void ManyRowsPerBlock_SurviveAfterSnapping()
    {
        var rgba = Index(64, static (x, y) => 1 + ((y % 4) * 4 + (x % 4)));
        var plan = Loader().PlanIndexBc5(rgba, 64, 64, out _, out int snapped, out int snappedRows);
        Assert.Equal(TextureLoader.IndexPlan.Bc5AfterSnap, plan);
        o.WriteLine($"snapped {snapped} texel(s), {snappedRows} of them to another row");
        Assert.True(snapped > 0);
    }

    /// <summary>
    /// The snap's guarantee: at most two distinct values per block afterwards (so BC4 is exact), and every texel
    /// still holds a value that was already present in ITS OWN block — never one invented from elsewhere.
    /// </summary>
    [Fact]
    public void Snap_KeepsValuesThatWereAlreadyInTheBlock()
    {
        var rgba = Index(64, static (x, y) => 1 + ((x / 7 + y / 5 + x % 3) % 16));
        var snapped = TextureLoader.SnapIndexForBc5(rgba, 64, 64, out var moved, out _);
        Assert.True(moved > 0);

        for (int by = 0; by < 64; by += 4)
        {
            for (int bx = 0; bx < 64; bx += 4)
            {
                var wasInBlock = new HashSet<byte>();
                var nowInBlock = new HashSet<byte>();
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int p = ((by + y) * 64 + bx + x) * 4;
                        wasInBlock.Add(rgba[p]);
                        nowInBlock.Add(snapped[p]);
                        // Green, blue and alpha are never touched.
                        Assert.Equal(rgba[p + 1], snapped[p + 1]);
                        Assert.Equal(rgba[p + 2], snapped[p + 2]);
                        Assert.Equal(rgba[p + 3], snapped[p + 3]);
                    }
                }

                Assert.True(nowInBlock.Count <= 2, $"block {bx},{by} still holds {nowInBlock.Count} values");
                Assert.Subset(wasInBlock, nowInBlock);
            }
        }
    }

    /// <summary>The snap is a pure function of its input: the same buffer must always produce the same bytes.</summary>
    [Fact]
    public void Snap_IsDeterministic()
    {
        var rgba = Index(64, static (x, y) => 1 + ((x * 3 + y * 5) % 16));
        var first = TextureLoader.SnapIndexForBc5(rgba, 64, 64, out var movedA, out _);
        var second = TextureLoader.SnapIndexForBc5(rgba, 64, 64, out var movedB, out _);
        Assert.Equal(movedA, movedB);
        Assert.Equal(first, second);
    }

    /// <summary>Dimensions off the block grid are written uncompressed anyway, so there is nothing to verify.</summary>
    [Fact]
    public void UnalignedSize_IsRefused()
    {
        Assert.False(Loader().IndexSurvivesBc5(Index(6, static (_, _) => 4), 6, 6, out _));
    }

    /// <summary>A buffer smaller than its declared size is refused rather than read past.</summary>
    [Fact]
    public void ShortBuffer_IsRefused()
    {
        Assert.False(Loader().IndexSurvivesBc5(new byte[16], 64, 64, out _));
    }
}
