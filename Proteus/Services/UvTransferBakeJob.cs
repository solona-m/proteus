using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// The bibo↔TBSE transfer maps baked from the Bibo+ and TBSE body files: TBSE bent to a woman's proportions by the
/// game's racial deformer, both maps baked, measured, gated and encoded. Bytes in, bytes out — the caller reads the
/// files and writes the maps.
/// <para/>
/// Each side's legs are every smallclothes option at once where that side is the destination, so the map has a texel
/// for whichever option a player wears; where it is the source, the one option covering most of its sheet.
/// </summary>
internal static class UvTransferBakeJob
{
    internal const int Size = 4096;
    internal const string BiboToTbse = "bibo_to_tbse_transfer.tif";
    internal const string TbseToBibo = "tbse_to_bibo_transfer.tif";

    internal sealed record Inputs(IReadOnlyDictionary<string, byte[]> Bibo, IReadOnlyDictionary<string, byte[]> Tbse,
                                  byte[] Pbd, byte[] Skeleton);

    internal sealed record Output(byte[]? BiboToTbseBytes, byte[]? TbseToBiboBytes, JsonObject Report, List<string> Failures)
    {
        public bool Passed => Failures.Count == 0;
    }

    /// <summary>Both maps of a pair, their figures and the gate's misses.</summary>
    internal sealed record Pair(UvTransferBake.Result AtoB, UvTransferBake.Result BtoA,
                                UvTransferQuality.Figures AtoBFigures, UvTransferQuality.Figures BtoAFigures,
                                List<string> Failures);

    /// <summary>
    /// The map pair between bodies <paramref name="aDest"/>/<paramref name="aSrc"/> and <paramref name="bDest"/>/<paramref name="bSrc"/>
    /// — each body as a destination (every option) and as a source (one) — measured. <c>AtoB</c> is indexed by a B
    /// pixel and stores the A coordinate, as <c>a_to_b_transfer.tif</c> is.
    /// </summary>
    internal static Pair BakePair(BakeBody aDest, BakeBody aSrc, BakeBody bDest, BakeBody bSrc, int size)
    {
        var bRaster = UvTransferBake.Rasterize(bDest, size);
        var aRaster = UvTransferBake.Rasterize(aDest, size);
        var aToB = MirrorFill(UvTransferBake.Bake(bDest, aSrc, size, bRaster));
        var bToA = MirrorFill(UvTransferBake.Bake(aDest, bSrc, size, aRaster));

        int lookup = Math.Min(size, 2048);
        var aSrcRaster = UvTransferBake.Rasterize(aSrc, lookup);
        var bSrcRaster = UvTransferBake.Rasterize(bSrc, lookup);
        var fAtoB = UvTransferQuality.Measure(aToB, bRaster, bDest, aSrc, aSrcRaster);
        var fBtoA = UvTransferQuality.Measure(bToA, aRaster, aDest, bSrc, bSrcRaster);
        (fAtoB.RoundTripModerate, fAtoB.RoundTripGross) = UvTransferQuality.RoundTrip(aToB, bToA);
        (fBtoA.RoundTripModerate, fBtoA.RoundTripGross) = UvTransferQuality.RoundTrip(bToA, aToB);
        fAtoB.LandmarksMm = UvTransferQuality.LandmarkErrors(aToB, bDest, aSrc, aSrcRaster);
        fBtoA.LandmarksMm = UvTransferQuality.LandmarkErrors(bToA, aDest, bSrc, bSrcRaster);
        var failures = UvTransferQuality.Check("a_to_b", fAtoB).Concat(UvTransferQuality.Check("b_to_a", fBtoA)).ToList();
        return new Pair(aToB, bToA, fAtoB, fBtoA, failures);
    }

    /// <summary>
    /// A map one half of whose sheet came out empty while the other did not, the empty half filled with the other's
    /// exact mirror (u → 1 - u). Not expected of two asymmetric layouts, and reported when it happens.
    /// </summary>
    internal static UvTransferBake.Result MirrorFill(UvTransferBake.Result map)
    {
        int size = map.Size;
        long left = 0, right = 0;
        for (int i = 0; i < map.Valid.Length; i++)
            if (map.Valid[i]) { if (i % size < size / 2) left++; else right++; }
        if ((left == 0) == (right == 0)) return map;
        bool fillLeft = left == 0;
        var r = (ushort[])map.R.Clone();
        var g = (ushort[])map.G.Clone();
        var valid = (bool[])map.Valid.Clone();
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool inLeft = x < size / 2;
                if (inLeft != fillLeft) continue;
                int i = y * size + x, m = y * size + (size - 1 - x);
                if (!map.Valid[m]) continue;
                r[i] = (ushort)(ushort.MaxValue - map.R[m]);
                g[i] = map.G[m];
                valid[i] = true;
            }
        map.Stats.MirrorFilled = fillLeft ? "left" : "right";
        return map with { R = r, G = g, Valid = valid };
    }

    private static BakeBody Body(params BakeBody.Part?[] parts) => new(parts.OfType<BakeBody.Part>().ToList());

    /// <summary>Of a body's legs options, the one covering most of the sheet; the first (the default) on a tie.</summary>
    private static int BestLegs(IReadOnlyList<BakeBody.Part?> legs, int size)
    {
        int best = 0;
        long bestCover = -1;
        for (int i = 0; i < legs.Count; i++)
        {
            if (legs[i] == null) continue;
            long cover = UvTransferBake.Rasterize(Body(legs[i]), size).Owner.LongCount(o => o >= 0);
            if (cover > bestCover) { best = i; bestCover = cover; }
        }
        return best;
    }

    /// <summary>The whole bake. Never throws for bad data: a failure is in <see cref="Output.Failures"/>, and no map comes back.</summary>
    internal static Output Run(Inputs inputs, int size = Size, bool reportOnly = false)
    {
        var sw = Stopwatch.StartNew();
        var report = new JsonObject();
        var failures = new List<string>();
        try
        {
            var pbd = new PbdFile(inputs.Pbd);
            var skeleton = HavokSkeleton.Read(inputs.Skeleton) ?? throw new InvalidOperationException("the c0101 skeleton could not be read");
            var parents = skeleton.ParentMap();
            var inSkeleton = skeleton.Names.ToHashSet(StringComparer.Ordinal);
            if (pbd.Chain(101, 201).Count == 0) throw new InvalidOperationException("the racial deformer has no way from c0101 to c0201");
            // A bone the skeleton does not have walks to no ancestor; bent as the pelvis it hangs off.
            string? ParentOf(string bone) => parents.TryGetValue(bone, out var p) ? p : bone == "j_kosi" ? null : "j_kosi";

            byte[] Bibo(string rel) => inputs.Bibo[rel];
            byte[] Bent(string rel) => UvBakeInputs.Bend(inputs.Tbse[rel], pbd, ParentOf);

            var biboLegs = UvBakeInputs.BiboLegs.Select((l, i) => UvBakeInputs.Part(Bibo(l.Rel), i)).ToList();
            var tbseLegs = UvBakeInputs.TbseLegs.Select((l, i) => UvBakeInputs.Part(Bent(l.Rel), i)).ToList();
            int biboBest = BestLegs(biboLegs, 1024), tbseBest = BestLegs(tbseLegs, 1024);

            var biboChest = UvBakeInputs.Part(Bibo(UvBakeInputs.BiboChest));
            var biboHands = UvBakeInputs.Part(Bibo(UvBakeInputs.BiboHands));
            var biboFeet = UvBakeInputs.Part(Bibo(UvBakeInputs.BiboFeet));
            var tbseChest = UvBakeInputs.Part(Bent(UvBakeInputs.TbseChest));
            var tbseHands = UvBakeInputs.Part(Bent(UvBakeInputs.TbseHands));
            var tbseFeet = UvBakeInputs.Part(Bent(UvBakeInputs.TbseFeet));

            var biboDest = Body([biboChest, .. biboLegs, biboHands, biboFeet]);
            var biboSrc = Body(biboChest, biboLegs[biboBest], biboHands, biboFeet);
            var tbseDest = Body([tbseChest, .. tbseLegs, tbseHands, tbseFeet]);
            var tbseSrc = Body(tbseChest, tbseLegs[tbseBest], tbseHands, tbseFeet);

            report["inputs"] = Describe(inputs);
            report["choice"] = new JsonObject
            {
                ["biboLegs"] = UvBakeInputs.BiboLegs[biboBest].Name,
                ["tbseLegs"] = UvBakeInputs.TbseLegs[tbseBest].Name,
                ["biboChest"] = "Nude - Small",
            };
            report["measure"] = Measure(inputs, tbseDest, biboDest, inSkeleton, size);

            if (reportOnly)
            {
                report["elapsedMs"] = sw.ElapsedMilliseconds;
                return new Output(null, null, report, failures);
            }

            // bibo is A, tbse is B: a_to_b = bibo_to_tbse, indexed by a TBSE pixel, storing the bibo coordinate.
            var pair = BakePair(biboDest, biboSrc, tbseDest, tbseSrc, size);
            failures.AddRange(pair.Failures.Select(f => f.Replace("a_to_b", "bibo_to_tbse").Replace("b_to_a", "tbse_to_bibo")));
            report["stats"] = new JsonObject
            {
                [BiboToTbse] = Stats(pair.AtoB, pair.AtoBFigures),
                [TbseToBibo] = Stats(pair.BtoA, pair.BtoAFigures),
            };
            report["gate"] = new JsonObject
            {
                ["passed"] = failures.Count == 0,
                ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f).ToArray()),
            };
            byte[] aToB = UvTransferMapFile.Encode(pair.AtoB.R, pair.AtoB.G, pair.AtoB.Valid, size);
            byte[] bToA = UvTransferMapFile.Encode(pair.BtoA.R, pair.BtoA.G, pair.BtoA.Valid, size);
            report["files"] = new JsonArray(
                new JsonObject { ["name"] = BiboToTbse, ["bytes"] = aToB.Length, ["sha256"] = UvBakeInputs.Sha256(aToB) },
                new JsonObject { ["name"] = TbseToBibo, ["bytes"] = bToA.Length, ["sha256"] = UvBakeInputs.Sha256(bToA) });
            report["elapsedMs"] = sw.ElapsedMilliseconds;
            return failures.Count == 0 ? new Output(aToB, bToA, report, failures) : new Output(null, null, report, failures);
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
            report["error"] = ex.Message;
            report["elapsedMs"] = sw.ElapsedMilliseconds;
            return new Output(null, null, report, failures);
        }
    }

    private static JsonObject Describe(Inputs inputs)
    {
        JsonArray Files(IReadOnlyDictionary<string, byte[]> files, string role)
            => new(files.OrderBy(f => f.Key, StringComparer.Ordinal)
                        .Select(f => (JsonNode)new JsonObject
                        {
                            ["role"] = role, ["rel"] = f.Key, ["bytes"] = f.Value.Length, ["sha256"] = UvBakeInputs.Sha256(f.Value),
                        }).ToArray());
        return new JsonObject
        {
            ["bibo"] = Files(inputs.Bibo, "bibo"),
            ["tbse"] = Files(inputs.Tbse, "tbse"),
            ["pbd"] = UvBakeInputs.Sha256(inputs.Pbd),
            ["skeleton"] = UvBakeInputs.Sha256(inputs.Skeleton),
        };
    }

    /// <summary>Step 0: the facts about the two bodies the maps rest on.</summary>
    private static JsonObject Measure(Inputs inputs, BakeBody tbseDest, BakeBody biboDest, HashSet<string> inSkeleton, int size)
    {
        var bones = new JsonArray();
        foreach (var (name, files) in new[] { ("bibo", inputs.Bibo), ("tbse", inputs.Tbse) })
            foreach (var (rel, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
                if (UvBakeInputs.Part(bytes) is { } part)
                    foreach (var (bone, vertices, how) in UvBakeBones.Report(part.Weights, inSkeleton.Contains))
                        if (how != UvBakeBones.Resolution.Own)
                            bones.Add(new JsonObject { ["body"] = name, ["part"] = rel, ["bone"] = bone, ["vertices"] = vertices, ["resolution"] = how.ToString() });

        var tbseRaster = UvTransferBake.Rasterize(tbseDest, 1024);
        var genital = new JsonObject();
        foreach (var ((a, b), texels) in tbseRaster.OverlapByOption.OrderBy(k => k.Key))
            genital[$"{UvBakeInputs.TbseLegs[a].Name}|{UvBakeInputs.TbseLegs[b].Name}"] = texels;

        double c0301 = double.NaN;
        if (inputs.Tbse.TryGetValue(UvBakeInputs.TbseHighlanderChest, out var hl) && UvBakeInputs.Part(hl) is { } hlPart
            && UvBakeInputs.Part(inputs.Tbse[UvBakeInputs.TbseChest]) is { } midPart)
            c0301 = Iou(new BakeBody([hlPart]), new BakeBody([midPart]), 1024);

        var chest = new JsonObject();
        foreach (var s in UvBakeInputs.BiboChestSizes)
            if (inputs.Bibo.TryGetValue(UvBakeInputs.BiboChestOf(s), out var bytes) && UvBakeInputs.Part(bytes) is { } p)
                chest[s] = UvTransferBake.Rasterize(new BakeBody([p]), 1024).Owner.LongCount(o => o >= 0);

        return new JsonObject
        {
            ["bones"] = bones,
            ["genitalOverlap"] = genital,
            ["c0301Iou"] = c0301,
            ["biboChestCover"] = chest,
            ["rims"] = new JsonObject { ["bibo"] = biboDest.Rim.SegmentCount, ["tbse"] = tbseDest.Rim.SegmentCount },
        };
    }

    /// <summary>The overlap of two bodies' skin on the sheet: shared texels over texels either covers.</summary>
    internal static double Iou(BakeBody a, BakeBody b, int size)
    {
        var ra = UvTransferBake.Rasterize(a, size).Owner;
        var rb = UvTransferBake.Rasterize(b, size).Owner;
        long both = 0, either = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            bool x = ra[i] >= 0, y = rb[i] >= 0;
            if (x && y) both++;
            if (x || y) either++;
        }
        return either == 0 ? 0 : (double)both / either;
    }

    private static JsonObject Stats(UvTransferBake.Result map, UvTransferQuality.Figures f)
    {
        var regions = new JsonObject();
        foreach (var (name, r) in f.Regions)
            regions[name] = new JsonObject { ["coverage"] = r.Coverage, ["rimSnaps"] = r.RimSnapShare, ["folds"] = r.FoldShare, ["texels"] = r.Owned };
        var landmarks = new JsonObject();
        foreach (var (name, mm) in f.LandmarksMm.OrderBy(k => k.Key, StringComparer.Ordinal))
            landmarks[name] = float.IsNaN(mm) ? null : Math.Round(mm, 1);
        var s = map.Stats;
        return new JsonObject
        {
            ["validPixels"] = s.ValidPixels, ["destCoverage"] = s.DestCoverage, ["rayHits"] = s.RayHits,
            ["closestFallbacks"] = s.ClosestFallbacks, ["regionAdjacentHits"] = s.RegionAdjacentHits,
            ["rimRejected"] = s.RimRejected, ["unreached"] = s.Unreached, ["overlapTexels"] = s.OverlapPixels,
            ["mirrorFilled"] = s.MirrorFilled, ["folds"] = f.FoldShare,
            ["roundTripModerate"] = f.RoundTripModerate, ["roundTripGross"] = f.RoundTripGross,
            ["regions"] = regions, ["landmarksMm"] = landmarks,
        };
    }
}
