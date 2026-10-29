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

    /// <summary>How many texels inside its edge the refit map's inverse is eased into the plain map: about 2 cm of skin at 4096.</summary>
    internal static int FeatherTexels(int size) => Math.Max(2, 80 * size / 4096);

    /// <summary>No map: what a pair holds when a chest field could not be made and nothing was baked.</summary>
    private static readonly UvTransferBake.Result None = new([], [], [], 0, new UvTransferBake.Stats());

    /// <summary>Both maps of a pair, their figures, each map's chest field and checks, and the gate's misses.</summary>
    internal sealed record Pair(UvTransferBake.Result AtoB, UvTransferBake.Result BtoA,
                                UvTransferQuality.Figures AtoBFigures, UvTransferQuality.Figures BtoAFigures,
                                List<string> Failures,
                                ChestField? AtoBField = null, ChestField.Checks? AtoBChecks = null);

    /// <summary>
    /// The map pair between bodies <paramref name="aDest"/>/<paramref name="aSrc"/> and <paramref name="bDest"/>/<paramref name="bSrc"/>
    /// — each body as a destination (every option) and as a source (one) — measured. <c>AtoB</c> is indexed by a B
    /// pixel and stores the A coordinate, as <c>a_to_b_transfer.tif</c> is. With <paramref name="chestFields"/>, each
    /// map is baked through its <see cref="ChestField"/> and measured against the plain bake as well.
    /// </summary>
    internal static Pair BakePair(BakeBody aDest, BakeBody aSrc, BakeBody bDest, BakeBody bSrc, int size,
                                  bool chestFields = false, string aToBName = "a_to_b", string bToAName = "b_to_a")
    {
        var failures = new List<string>();
        var bRaster = UvTransferBake.Rasterize(bDest, size);
        var aRaster = UvTransferBake.Rasterize(aDest, size);
        var aBvh = new TriangleBvh(aSrc);
        var bBvh = new TriangleBvh(bSrc);

        (ChestField? Field, ChestField.Checks? Checks) Field(string name, BakeBody dest, BakeBody src, TriangleBvh srcBvh)
        {
            if (!chestFields) return (null, null);
            var (field, failure) = ChestField.Build(dest, src, srcBvh);
            if (field == null) { failures.Add($"{name}: chest field: {failure}"); return (null, null); }
            var checks = field.Measure(dest);
            failures.AddRange(field.Check(checks).Select(m => $"{name}: {m}"));
            return (field, checks);
        }
        // A is the body with the breast. Its map onto B is cast from B's flat chest, through B's chest field; B's map
        // onto A is, where that field reaches, that map's inverse — a breast cast onto a flat chest crosses its own
        // rays, by ray or by nearness — and the plain bake elsewhere. The pair then agrees with itself on the chest.
        var (fieldAtoB, checksAtoB) = Field(aToBName, bDest, aSrc, aBvh);
        if (chestFields && fieldAtoB == null)
            return new Pair(None, None, new UvTransferQuality.Figures(), new UvTransferQuality.Figures(), failures,
                            fieldAtoB, checksAtoB);

        var aToB = MirrorFill(UvTransferBake.Bake(bDest, aSrc, size, bRaster, fieldAtoB));
        var bToAPlain = MirrorFill(UvTransferBake.Bake(aDest, bSrc, size, aRaster));
        var bToA = bToAPlain;
        Func<int, bool> onAsChest = _ => false;
        bool[] drawn = [];
        if (fieldAtoB != null)
        {
            // Only the front of B's chest is inverted: the field's reach wraps round the flanks, where B's side and back
            // islands land on A's front too and would draw over it.
            bool Reach(int i)
            {
                int t = bRaster.Owner[i];
                if (t < 0) return false;
                float u = bRaster.BaryU[i], v = bRaster.BaryV[i];
                return bDest.TriRegion[t] == UvBakeBones.Region.Torso && bDest.SmoothAt(t, u, v, 1 - u - v).Z > ChestField.MinFrontZ
                       && fieldAtoB.InSupport(bDest.At(t, u, v, 1 - u - v).Pos);
            }
            // And drawn only onto A's chest, found apart from the drawing: not facing back — a breast's underside faces
            // down and takes B's chest — within the field's band and its bumps' reach of a site.
            float far = ChestField.Radius + fieldAtoB.Sites.Max(site => site.Offset.Length());
            bool OnAsChest(int i)
            {
                int t = aRaster.Owner[i];
                if (t < 0) return false;
                float u = aRaster.BaryU[i], v = aRaster.BaryV[i];
                var p = aDest.At(t, u, v, 1 - u - v).Pos;
                return aDest.TriRegion[t] == UvBakeBones.Region.Torso && aDest.SmoothAt(t, u, v, 1 - u - v).Z > -ChestField.MinFrontZ
                       && p.Y > fieldAtoB.YLow && p.Y < fieldAtoB.YClav
                       && fieldAtoB.Sites.Any(site => System.Numerics.Vector3.Distance(site.Centre, p) < far);
            }
            onAsChest = OnAsChest;
            (bToA, drawn) = UvTransferBake.InvertInto(aToB, Reach, bToAPlain, OnAsChest);
            // Where the inverse meets the plain map the two disagree by what the plain pair always did (some mm): eased
            // into it over a band inside the edge, on the body, rather than met in a step.
            bToA = UvTransferBake.Feather(bToA, drawn, bToAPlain, FeatherTexels(size),
                                          bSrc, UvTransferBake.Rasterize(bSrc, Math.Min(size, 2048)), bBvh);
        }

        int lookup = Math.Min(size, 2048);
        var aSrcRaster = UvTransferBake.Rasterize(aSrc, lookup);
        var bSrcRaster = UvTransferBake.Rasterize(bSrc, lookup);
        var fAtoB = UvTransferQuality.Measure(aToB, bRaster, bDest, aSrc, aSrcRaster);
        var fBtoA = UvTransferQuality.Measure(bToA, aRaster, aDest, bSrc, bSrcRaster);
        (fAtoB.RoundTripModerate, fAtoB.RoundTripGross) = UvTransferQuality.RoundTrip(aToB, bToA);
        (fBtoA.RoundTripModerate, fBtoA.RoundTripGross) = UvTransferQuality.RoundTrip(bToA, aToB);
        fAtoB.LandmarksMm = UvTransferQuality.LandmarkErrors(aToB, bDest, aSrc, aSrcRaster, fAtoB.LandmarksTangentMm);
        fBtoA.LandmarksMm = UvTransferQuality.LandmarkErrors(bToA, aDest, bSrc, bSrcRaster, fBtoA.LandmarksTangentMm);

        // The plain bake beside each field's: what the field may not change, how much it stretches what it does, and
        // where the plain projection puts the crease (its bound).
        if (fieldAtoB != null)
        {
            var plain = MirrorFill(UvTransferBake.Bake(bDest, aSrc, size, bRaster));
            fAtoB.PlainLandmarksMm = UvTransferQuality.LandmarkErrors(plain, bDest, aSrc, aSrcRaster);
            fAtoB.Stretch = UvTransferQuality.WarpStretch(aToB, plain, bRaster, i =>
            {
                int t = bRaster.Owner[i];
                return fieldAtoB.InSupport(bDest.At(t, bRaster.BaryU[i], bRaster.BaryV[i], 1 - bRaster.BaryU[i] - bRaster.BaryV[i]).Pos);
            }, null, (r, g) => UvTransferQuality.OnSource(r, g, aSrc, aSrcRaster));
            (fAtoB.Stretch.PredictedArea, fAtoB.Stretch.PredictedAniso) = fieldAtoB.Predict(bDest, aSrc, aBvh);
            fBtoA.PlainLandmarksMm = UvTransferQuality.LandmarkErrors(bToAPlain, aDest, bSrc, bSrcRaster);
            // Distortion over the chest it may draw on; the tear test at the edge of what it did draw, where the
            // inverse meets the plain map.
            fBtoA.Stretch = UvTransferQuality.WarpStretch(bToA, bToAPlain, aRaster, onAsChest, i => drawn[i],
                                                          (r, g) => UvTransferQuality.OnSource(r, g, bSrc, bSrcRaster));
            fBtoA.Stretch.Margin = 1;   // the inverse straightens what the plain map squeezed: it may be no worse there

            (fBtoA.Stretch.PredictedArea, fBtoA.Stretch.PredictedAniso) = (double.NaN, double.NaN);
        }
        failures.AddRange(UvTransferQuality.Check(aToBName, fAtoB).Concat(UvTransferQuality.Check(bToAName, fBtoA)));
        return new Pair(aToB, bToA, fAtoB, fBtoA, failures, fieldAtoB, checksAtoB);
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
            var pair = BakePair(biboDest, biboSrc, tbseDest, tbseSrc, size, chestFields: true, BiboToTbse, TbseToBibo);
            failures.AddRange(pair.Failures);
            report["chestField"] = new JsonObject
            {
                [BiboToTbse] = Describe(pair.AtoBField, pair.AtoBChecks),
                [TbseToBibo] = "the inverse of " + BiboToTbse + " where its field reaches",
            };
            if (failures.Count > 0 && pair.AtoBField == null)
            {
                report["gate"] = new JsonObject { ["passed"] = false, ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f).ToArray()) };
                report["elapsedMs"] = sw.ElapsedMilliseconds;
                return new Output(null, null, report, failures);
            }
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
            ["c0301Iou"] = Num(c0301),
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

    /// <summary>A map's chest field for the report: its sites and offsets, its band, conditioning and checks.</summary>
    private static JsonNode? Describe(ChestField? field, ChestField.Checks? c)
    {
        if (field == null || c == null) return null;
        var sites = new JsonObject();
        foreach (var s in field.Sites)
            sites[s.Name] = new JsonObject
            {
                ["pin"] = s.Pin,
                ["at"] = new JsonArray(Math.Round(s.Centre.X, 4), Math.Round(s.Centre.Y, 4), Math.Round(s.Centre.Z, 4)),
                ["offsetMm"] = new JsonArray(Math.Round(s.Offset.X * 1000, 1), Math.Round(s.Offset.Y * 1000, 1), Math.Round(s.Offset.Z * 1000, 1)),
            };
        var residual = new JsonObject();
        foreach (var (name, mm) in field.ResidualMm.OrderBy(r => r.Key, StringComparer.Ordinal)) residual[name] = Math.Round(mm, 2);
        return new JsonObject
        {
            ["radius"] = ChestField.Radius, ["yLow"] = Math.Round(field.YLow, 4), ["yClavicle"] = Math.Round(field.YClav, 4),
            ["condition"] = Num(field.Condition), ["ridged"] = field.Ridged, ["iterations"] = field.Iterations,
            ["residualMm"] = residual, ["sites"] = sites,
            ["minJacobian"] = Math.Round(c.MinJacobian, 3), ["maxPinMm"] = Math.Round(c.MaxPinMm, 3),
            ["maxOffTorsoMm"] = Math.Round(c.MaxOffTorsoMm, 2), ["maxMm"] = Math.Round(c.MaxMm, 1),
            ["maxReverseMm"] = Math.Round(c.MaxReverseMm, 2), ["maxStillMm"] = Math.Round(c.MaxStillMm, 2),
        };
    }

    /// <summary>A figure for the report: JSON has no NaN, so a figure that could not be measured is null.</summary>
    private static JsonNode? Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : JsonValue.Create(v);

    private static JsonObject Stats(UvTransferBake.Result map, UvTransferQuality.Figures f)
    {
        var regions = new JsonObject();
        foreach (var (name, r) in f.Regions)
            regions[name] = new JsonObject { ["coverage"] = Num(r.Coverage), ["rimSnaps"] = Num(r.RimSnapShare), ["folds"] = Num(r.FoldShare), ["texels"] = r.Owned };
        var landmarks = new JsonObject();
        foreach (var (name, mm) in f.LandmarksMm.OrderBy(k => k.Key, StringComparer.Ordinal))
            landmarks[name] = float.IsNaN(mm) ? null : Math.Round(mm, 1);
        var tangent = new JsonObject();
        foreach (var (name, mm) in f.LandmarksTangentMm.OrderBy(k => k.Key, StringComparer.Ordinal))
            tangent[name] = float.IsNaN(mm) ? null : Math.Round(mm, 1);
        var plain = new JsonObject();
        foreach (var (name, mm) in f.PlainLandmarksMm.OrderBy(k => k.Key, StringComparer.Ordinal))
            plain[name] = float.IsNaN(mm) ? null : Math.Round(mm, 1);
        var bounds = new JsonObject();
        foreach (var name in f.LandmarksMm.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (!name.StartsWith("nipple", StringComparison.Ordinal) && !name.StartsWith("underBustOld", StringComparison.Ordinal))
                bounds[name] = UvTransferQuality.LandmarkLimitMm(name, f);
        JsonNode? stretch = f.Stretch is not { } w ? null : new JsonObject
        {
            ["measured"] = w.Measured, ["changedInside"] = w.ChangedInside, ["changedOutside"] = w.ChangedOutside,
            ["edge"] = new JsonObject { ["pairs"] = w.EdgePairs, ["p99"] = Num(w.EdgeJumpP99), ["max"] = Num(w.EdgeJumpMax), ["maxAt"] = w.EdgeJumpAt < 0 ? null : new JsonArray(w.EdgeJumpAt % map.Size, w.EdgeJumpAt / map.Size),
                                        ["plainP99"] = Num(w.PlainEdgeJumpP99), ["plainMax"] = Num(w.PlainEdgeJumpMax) },
            ["area"] = new JsonObject { ["p50"] = Num(w.AreaP50), ["p99"] = Num(w.AreaP99), ["p999"] = Num(w.AreaP999), ["max"] = Num(w.AreaMax), ["plainP99"] = Num(w.PlainAreaP99), ["predicted"] = Num(w.PredictedArea) },
            ["aniso"] = new JsonObject { ["p50"] = Num(w.AnisoP50), ["p99"] = Num(w.AnisoP99), ["p999"] = Num(w.AnisoP999), ["max"] = Num(w.AnisoMax), ["plainP99"] = Num(w.PlainAnisoP99), ["predicted"] = Num(w.PredictedAniso) },
        };
        var s = map.Stats;
        return new JsonObject
        {
            ["plainLandmarksMm"] = plain, ["landmarkBoundsMm"] = bounds, ["warpStretch"] = stretch,
            ["validPixels"] = s.ValidPixels, ["destCoverage"] = s.DestCoverage, ["rayHits"] = s.RayHits,
            ["closestFallbacks"] = s.ClosestFallbacks, ["regionAdjacentHits"] = s.RegionAdjacentHits,
            ["unreached"] = s.Unreached, ["overlapTexels"] = s.OverlapPixels,
            ["mirrorFilled"] = s.MirrorFilled, ["folds"] = Num(f.FoldShare),
            ["roundTripModerate"] = Num(f.RoundTripModerate), ["roundTripGross"] = Num(f.RoundTripGross),
            ["regions"] = regions, ["landmarksMm"] = landmarks, ["landmarksAcrossSkinMm"] = tangent,
        };
    }
}
