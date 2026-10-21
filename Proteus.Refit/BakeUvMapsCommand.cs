using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Proteus.Services;

namespace Proteus.Refit;

/// <summary>
/// <c>--bake-uvmaps</c>: the bibo↔TBSE transfer maps baked from the Bibo+ and TBSE body mods, the game read only for
/// its racial deformer and the c0101 skeleton. A dev tool: run once, its maps committed and published, its report the
/// record of what they were made from (see <c>docs/uvmaps.md</c>).
/// </summary>
internal static class BakeUvMapsCommand
{
    internal static int Run(Dictionary<string, string> opts, Func<Dictionary<string, string>, string, string> required)
    {
        string game = required(opts, "game");
        string biboRoot = required(opts, "bibo-root");
        string tbseRoot = required(opts, "tbse-root");
        string outDir = required(opts, "out-dir");
        bool reportOnly = opts.ContainsKey("report-only");

        var data = new Lumina.GameData(game);
        byte[] Game(string path) => data.GetFile(path)?.Data
                                    ?? throw new FileNotFoundException($"the game has no {path}");
        var inputs = new UvTransferBakeJob.Inputs(
            UvBakeInputs.Read(biboRoot, UvBakeInputs.BiboFiles()),
            UvBakeInputs.Read(tbseRoot, UvBakeInputs.TbseFiles()),
            Game(OtherRaceWearer.PbdPath),
            Game(OtherRaceWearer.BaseSkeletonPath(101)));

        var result = UvTransferBakeJob.Run(inputs, reportOnly: reportOnly);
        Console.WriteLine(result.Report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!result.Passed)
        {
            foreach (var f in result.Failures) Console.Error.WriteLine(f);
            return 1;
        }
        if (reportOnly) return 0;

        Directory.CreateDirectory(outDir);
        string first = Path.Combine(outDir, UvTransferBakeJob.BiboToTbse);
        UvTransferMapFile.WriteAtomic(first, result.BiboToTbseBytes!);
        try
        {
            UvTransferMapFile.WriteAtomic(Path.Combine(outDir, UvTransferBakeJob.TbseToBibo), result.TbseToBiboBytes!);
        }
        catch
        {
            // A pair or nothing: half a pair would load as a map with no reverse to check it against.
            File.Delete(first);
            throw;
        }
        return 0;
    }
}
