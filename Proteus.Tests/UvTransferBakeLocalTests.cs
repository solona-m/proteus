using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>The bibo↔TBSE bake run on the real Bibo+ and TBSE files and the game's deformer.</summary>
public class UvTransferBakeLocalTests(ITestOutputHelper output)
{
    internal const string Bibo = LocalData.Mods + ":Bibo+";
    internal const string Tbse = LocalData.Mods + ":The Body SE";

    internal static UvTransferBakeJob.Inputs Inputs()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return new UvTransferBakeJob.Inputs(
            UvBakeInputs.Read(LocalData.Path(Bibo), UvBakeInputs.BiboFiles()),
            UvBakeInputs.Read(LocalData.Path(Tbse), UvBakeInputs.TbseFiles()),
            data.GetFile(OtherRaceWearer.PbdPath)!.Data,
            data.GetFile(OtherRaceWearer.BaseSkeletonPath(101))!.Data);
    }

    /// <summary>A body file the job cannot read fails the bake whole: no map comes back, and the reason does.</summary>
    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "EDGE-tbse-uvmap-28")]
    public void Unreadable_input_fails_without_maps() // [EDGE-tbse-uvmap-28]
    {
        var good = Inputs();
        var tbse = good.Tbse.ToDictionary(f => f.Key, f => f.Value);
        tbse[UvBakeInputs.TbseChest] = [1, 2, 3, 4];
        var result = UvTransferBakeJob.Run(good with { Tbse = tbse }, 256);
        Assert.False(result.Passed);
        Assert.Null(result.BiboToTbseBytes);
        Assert.Null(result.TbseToBiboBytes);
        output.WriteLine(string.Join("; ", result.Failures));
    }
}

/// <summary>The shipped bibo↔gen3 maps measured by the same round-trip yardstick, for comparison.</summary>
public class UvTransferBaselineLocalTests(ITestOutputHelper output)
{
    internal static UvTransferBake.Result ReadMap(string path)
    {
        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(path, "r")!;
        int w = tiff.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH)[0].ToInt();
        var r = new ushort[w * w]; var g = new ushort[w * w]; var ok = new bool[w * w];
        var line = new byte[tiff.ScanlineSize()];
        for (int y = 0; y < w; y++)
        {
            tiff.ReadScanline(line, y);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = BitConverter.ToUInt16(line, x * 8);
                g[i] = BitConverter.ToUInt16(line, x * 8 + 2);
                ok[i] = BitConverter.ToUInt16(line, x * 8 + 6) > 0;
            }
        }
        return new UvTransferBake.Result(r, g, ok, w, new UvTransferBake.Stats());
    }

    [LocalDataFact(LocalData.UvMaps)]
    public void Shipped_gen3_maps_round_trip()
    {
        string dir = System.IO.Path.Combine(LocalData.Path(LocalData.UvMaps), "uvmaps");
        var a = ReadMap(System.IO.Path.Combine(dir, "bibo_to_gen3_transfer.tif"));
        var b = ReadMap(System.IO.Path.Combine(dir, "gen3_to_bibo_transfer.tif"));
        var (m1, g1) = UvTransferQuality.RoundTrip(a, b);
        var (m2, g2) = UvTransferQuality.RoundTrip(b, a);
        output.WriteLine($"bibo_to_gen3: moderate {m1:P2} gross {g1:P2}; gen3_to_bibo: moderate {m2:P2} gross {g2:P2}");
    }
}
