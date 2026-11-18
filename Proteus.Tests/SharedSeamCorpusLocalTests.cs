using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// How widely a waist split (see <see cref="BodyRetarget.SplitPoints"/>) is found across the installed mods — the plan's
/// Step 0(e): which mods have one, where it lies, and how much of each legs file meets it. Reads models only; detection
/// (which body a top was made on) is left to the Sarelle tests, so this stays a scan. Prints a table.
/// </summary>
[Trait("Speed", "Slow")]
public class SharedSeamCorpusLocalTests(ITestOutputHelper output)
{
    private const string Race = "0201";

    [LocalDataFact(LocalData.Mods, LocalData.SarelleMod)]
    public void Corpus_SplitsFound_Reported()
    {
        var clock = Stopwatch.StartNew();
        int scanned = 0, withFiles = 0, split = 0;
        var rows = new List<string>();
        foreach (string root in Directory.EnumerateDirectories(LocalData.Path(LocalData.Mods)))
        {
            if (!File.Exists(Path.Combine(root, PenumbraModMeta.MetaFile))) continue;
            scanned++;
            RefitCore.SeamFiles? files;
            try
            {
                files = RefitCore.SeamFilesOf(PenumbraModMeta.ReadAllRedirects(root), BodyRetargetWriter.ReadRecord(root), Race);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                continue;
            }
            if (files == null) continue;
            withFiles++;
            var legs = files.Legs.Select(f => Open(root, f.File)).OfType<(ModelParts, XivLiveMesh.SkinnedMesh)>().ToList();
            if (legs.Count == 0) continue;
            foreach (var top in files.Tops)
            {
                if (Open(root, top.File) is not { } t) continue;
                if (BodyRetarget.SplitPoints(t.Item1, t.Item2, legs) is not { } points) continue;
                split++;
                float lo = points.At.Min(p => p.Y), hi = points.At.Max(p => p.Y);
                var cover = legs.Select(l => BodyRetarget.SeamVertices(l.Item1, l.Item2, points).Coverage).ToList();
                rows.Add($"{Path.GetFileName(root)}: {top.File} — {points.Count} places, y {lo:0.000}-{hi:0.000}; legs cover "
                         + string.Join(" ", cover.Select(c => c.ToString("0.00"))));
                break;      // the first split top per mod, as the gate reads them
            }
        }
        output.WriteLine($"{scanned} mods, {withFiles} with chest and legs files, {split} with a waist split, in {clock.Elapsed.TotalSeconds:0} s");
        rows.ForEach(output.WriteLine);
        Assert.Contains(rows, r => r.StartsWith(Path.GetFileName(LocalData.Path(LocalData.SarelleMod)) + ":", StringComparison.Ordinal));
    }

    private static (ModelParts, XivLiveMesh.SkinnedMesh)? Open(string root, string file)
    {
        if (SiblingPiece.TryOpen(root, file) is not { } opened) return null;
        return ModelSkinReader.Read(opened.Bytes, null, null) is { } skin && skin.VertexCount * 3 == opened.Model.Positions.Length
            ? (opened.Model, skin)
            : null;
    }

    private const string Farfalla = LocalData.Mods + ":Farfalla - by Solona";

    /// <summary>
    /// A big mod with no split (Farfalla: 39 chest and 21 legs files) answers quickly cold: each legs file's edges are built
    /// once, not once per top. In game it took 32 s and 16 GB before, holding the redraws past their timeout; measured
    /// 6.6 s and 2.5 GB cold after (2026-11-18; reading the 30 files is 1.0 s of it), off the framework thread and cached.
    /// </summary>
    [LocalDataFact(Farfalla, LocalData.TreMod)]
    [Trait("Plan", "EDGE-split-waist-seam-34")]
    public void Gate_BigModWithoutSplit_IsQuickCold()
    {
        string root = LocalData.Path(Farfalla);
        var files = RefitCore.SeamFilesOf(PenumbraModMeta.ReadAllRedirects(root), BodyRetargetWriter.ReadRecord(root), Race)!;
        var tre = BodySizeCatalog.Read(LocalData.Path(LocalData.TreMod));
        long allocated = GC.GetTotalAllocatedBytes();
        var clock = Stopwatch.StartNew();
        var gate = RefitCore.ResolveSeamGate(root, files, Race, "Tre", tre, "Tre", tre, (_, _, _, _) => null);
        clock.Stop();
        long mb = (GC.GetTotalAllocatedBytes() - allocated) >> 20;
        output.WriteLine($"{files.Tops.Count} chest, {files.Legs.Count} legs files: {gate.Refusal} in {clock.Elapsed.TotalSeconds:0.0} s, {mb} MB");
        Assert.True(gate.NoSplit);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"{clock.Elapsed.TotalSeconds:0.0} s");
    }
}
