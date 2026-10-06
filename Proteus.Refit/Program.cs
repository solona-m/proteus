using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CheapLoc;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Refit;

/// <summary>
/// Refit a garment .mdl from one body option to others, outside the game — the Studio "Body size" tool's refit, run
/// headless so a mod packer can generate the sizes an author did not model.
/// <para/>
/// Bodies are named by their FILE, relative to the body mod's root (<c>DEFAULT CHEST - SmallClothes/NSFW XS.mdl</c>),
/// never by option name: Neolithe has eight options called "SFW M".
/// <para/>
/// The source body may be in another body mod (<c>--from-root</c>): Neolithe to Rue+, say. That is the Studio's
/// "across bodies" refit — the weights are rewritten for the new body's rig — and where the two bodies' textures are
/// laid out differently the correspondence goes through Proteus's layout maps (<c>--uvmaps</c>).
/// <para/>
/// The legs pair: a top's hem hangs over the hips, and refitting the chest alone left the cloth six times further from
/// the author's own sizes. The legs the hem was made on are detected in the source body mod, then either moved along
/// that family's size axis by the size word at the end of the file name (<c>--legs</c>, within one body mod) or sent
/// to one legs option of the target mod for every size (<c>--legs-to</c>).
/// <para/>
/// Writes <c>&lt;out-dir&gt;/&lt;label&gt;.mdl</c> for each target and one JSON report on stdout. A refused size is in the
/// report with its reason and no file; the exit code is non-zero only when nothing could run at all.
/// </summary>
internal static class Program
{
    private const string Usage =
        """
        Proteus.Refit --garment <xs.mdl> --body-root <body mod> --from <rel|auto> --to <rel>=<label>[,...] --out-dir <dir>
                      [--from-root <body mod the garment was made on>] [--uvmaps <Proteus plugin dir>]
                      [--slot _top] [--race 0201] [--legs-from <rel>]
                      [--legs <label>=<size word>,... | --legs-to <rel in the target mod>]
                      [--chest-from <rel> --chest-to <rel in the target mod>]   (legs garments only)
        Proteus.Refit --list --body-root <body mod> [--slot _top]
        Proteus.Refit --inspect --garment <model.mdl>
        Proteus.Refit --detect --garment <xs.mdl> --body-root <body mod> [--slot _top] [--race 0201]
        Proteus.Refit --finish --garment <in.mdl> --out <out.mdl> [--body-root <body mod> [--from <rel|auto>]]
                      [--slot _top] [--race 0201] [--tag-legs <rel|auto>]
        Proteus.Refit --save --manifest <save.json>
        Proteus.Refit --measure --garment <model.mdl> --bodies <body.mdl>[;<body.mdl>...]
        Proteus.Refit --measure --garment <model.mdl>     (cloth points within 2 mm rigged apart)
        """;

    private static int Main(string[] args)
    {
        // Loc.Localize returns "#key" for an assembly that was never set up, and refusals are worded through it.
        Loc.SetupWithFallbacks(typeof(Proteus.Plugin).Assembly);

        try
        {
            var opts = Parse(args);
            if (opts.ContainsKey("list")) return List(opts);
            if (opts.ContainsKey("inspect")) return Inspect(opts);
            if (opts.ContainsKey("detect")) return Detect(opts);
            if (opts.ContainsKey("finish")) return Finish(opts);
            if (opts.ContainsKey("save")) return Save(opts);
            if (opts.TryGetValue("obj", out var objOut)) return ExportObj(opts, objOut);
            if (opts.ContainsKey("measure") && opts.ContainsKey("face-poke")) return MeasureFacePoke(opts);
            if (opts.ContainsKey("measure"))
                return opts.ContainsKey("skin-map") ? MeasureSkinMap(opts)
                     : opts.ContainsKey("shell") ? MeasureShell(opts)
                     : opts.ContainsKey("bodies") ? Measure(opts)
                     : opts.ContainsKey("source") ? MeasureCrossings(opts)
                     : opts.ContainsKey("other") ? MeasureDiff(opts) : MeasureSplit(opts);
            return Run(opts);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>Each LOD0 submesh of a model — mesh.submesh, material, triangles, tags — to check what a build kept.</summary>
    private static int Inspect(Dictionary<string, string> opts)
    {
        var model = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "garment")))
                 ?? throw new UsageException("The model could not be read.");
        foreach (var part in model.Parts.Where(p => p.Island < 0))
        {
            var tags = Enumerable.Range(0, Math.Min(32, model.AttributeNames.Count))
                                 .Where(i => (part.AttributeMask & (1u << i)) != 0)
                                 .Select(i => model.AttributeNames[i]);
            // Wind: how many of the part's vertices carry any, and the strongest — a refit must copy it through.
            var verts = part.Triangles.Distinct().ToList();
            int windy = model.Wind.Length == 0 ? 0 : verts.Count(v => model.Wind[v] > 0f);
            float peak = model.Wind.Length == 0 || verts.Count == 0 ? 0f : verts.Max(v => model.Wind[v]);
            Console.WriteLine($"{part.Mesh}.{part.Submesh}\t{part.Material}\t{part.TriangleCount}\t{string.Join(",", tags)}" +
                              $"\twind {windy}/{verts.Count} max {peak:0.00}");
        }
        return 0;
    }

    private static int List(Dictionary<string, string> opts)
    {
        var catalog = BodySizeCatalog.Read(Required(opts, "body-root"));
        var slots = opts.TryGetValue("slot", out var s) ? [s] : catalog.Slots.ToArray();
        foreach (string slot in slots)
            foreach (var option in catalog.For(slot))
                Console.WriteLine($"{slot}\t{option.Rel}\t{option.FullLabel}");
        return 0;
    }

    /// <summary>
    /// Which body option the garment was made on, as JSON, without refitting anything. A packer asks this first
    /// so it can refit onto the same family's other sizes: a top made on Almond XS belongs on Almond S, not on the
    /// plain S.
    /// </summary>
    private static int Detect(Dictionary<string, string> opts)
    {
        string garmentPath = Required(opts, "garment");
        string slot = opts.GetValueOrDefault("slot", "_top");
        string? race = opts.GetValueOrDefault("race", "0201");
        var catalog = BodySizeCatalog.Read(Required(opts, "body-root"));
        var garmentBytes = File.ReadAllBytes(garmentPath);
        var garment = ModelPartReader.Read(garmentBytes)
                   ?? throw new UsageException($"{garmentPath} could not be read as a model.");
        var from = DetectSource(catalog, slot, garment, garmentBytes, race, out string confidence);
        Console.WriteLine(JsonSerializer.Serialize(new { from = from.Rel, confidence },
                                                   new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// Finish a hand-made garment the way a refitted size already comes out: its body skin swapped for the body mod's
    /// own, and only LOD0 kept.
    /// <para/>
    /// The skin swap is a refit onto the body the garment was made on, so nothing moves and the push-out is off; what it
    /// buys is the body mod's skin meshes with the body mod's tags — atr_nek, atr_ude and atr_hij on a chest, atr_sne and
    /// atr_hiz on legs — which is how long gloves, boots or a high collar hide the skin under them. The body's variant
    /// tags are dropped, and its skin is cut where the author cut theirs (see <c>BodyRetarget.SwapSkin</c>).
    /// <para/>
    /// Without <c>--body-root</c>, or when the body cannot be told, only the LOD cut is made.
    /// </summary>
    private static int Finish(Dictionary<string, string> opts)
    {
        string garmentPath = Required(opts, "garment");
        string outPath = Required(opts, "out");
        var bytes = File.ReadAllBytes(garmentPath);
        byte[] model = bytes;
        int lodsBefore = ModelLodTrimmer.LodCount(bytes);

        object? skin = null;
        string? skinSkipped = null;
        if (opts.TryGetValue("body-root", out var bodyRoot))
        {
            string slot = opts.GetValueOrDefault("slot", "_top");
            string? race = opts.GetValueOrDefault("race", "0201");
            bool male = race != null && BodySizeCatalog.IsMaleRace(race);
            var catalog = BodySizeCatalog.Read(bodyRoot);
            var garment = ModelPartReader.Read(bytes)
                       ?? throw new UsageException($"{garmentPath} could not be read as a model.");

            string fromArg = opts.GetValueOrDefault("from", "auto");
            string confidence = "Given";
            BodyOption? from = null;
            try
            {
                from = fromArg == "auto"
                    ? DetectSource(catalog, slot, garment, bytes, race, out confidence)
                    : Find(catalog, slot, fromArg);
            }
            catch (UsageException ex) when (fromArg == "auto")
            {
                skinSkipped = ex.Message;
            }

            if (from != null)
            {
                ushort? mask = MaskOf(catalog, slot);
                string path = catalog.PathOf(from);
                if (BodyRetarget.BuildPair(slot, path, path, slot, male, mask, mask, null, out var pair) is { } refusal)
                    skinSkipped = refusal;
                else
                {
                    var planned = BodyRetarget.Plan(garment, bytes, [pair], slot, pushOut: false, replaceSkin: true);
                    model = planned.Model;
                    var swap = planned.Report.Swap;
                    skin = new
                    {
                        from = from.Rel,
                        confidence,
                        removed = swap?.Removed ?? 0,
                        added = swap?.Added ?? 0,
                        kept = swap?.Kept ?? 0,
                        cut = swap?.Cut ?? 0,
                        lostShapes = swap?.LostShapes ?? 0,
                        worstMoveMm = planned.Report.WorstMove * 1000f,
                    };
                }
            }
        }

        string? lodRefusal = null;
        if (ModelLodTrimmer.LodCount(model) > 1)
        {
            if (ModelLodTrimmer.KeepLod0(model, out var why) is { } trimmed) model = trimmed;
            else lodRefusal = why;
        }

        // A full-body piece: its own skin runs down the legs, on neither slot's body alone, so the swap kept it
        // untagged. The legs body's calf and knee tags are carried onto it triangle by triangle, which is what lets
        // boots hide the skin under their shafts.
        object? legTags = null;
        string? legTagsSkipped = null;
        if (opts.TryGetValue("tag-legs", out var tagLegs))
        {
            var catalog = BodySizeCatalog.Read(Required(opts, "body-root"));
            string? race = opts.GetValueOrDefault("race", "0201");
            BodyOption? legsBody = null;
            string confidence = "Given";
            if (tagLegs == "auto")
            {
                // Optional work on top of a finish already done: a legs body that cannot be found skips the tagging, and
                // the swapped, trimmed model is still written.
                try
                {
                    legsBody = ModelPartReader.Read(model) is { } parts
                        ? DetectSource(catalog, "_dwn", parts, model, race, out confidence)
                        : throw new UsageException("the finished model could not be read back");
                }
                catch (UsageException ex)
                {
                    try
                    {
                        legsBody = Find(catalog, "_dwn", DefaultTagLegs);
                        confidence = $"Fallback ({ex.Message})";
                    }
                    catch (UsageException fallback)
                    {
                        legTagsSkipped = $"{ex.Message} {fallback.Message}";
                    }
                }
            }
            else legsBody = Find(catalog, "_dwn", tagLegs);

            if (legsBody != null)
            {
                model = SkinTagTransfer.Transfer(model, File.ReadAllBytes(catalog.PathOf(legsBody)), LegTags, out var tagReport);
                legTags = new { from = legsBody.Rel, confidence, tagged = tagReport.Tagged, skinTriangles = tagReport.SkinTriangles };
                if (tagReport.Tagged.Values.All(n => n == 0)) legTagsSkipped = "no skin triangle sits on the legs body's calf or knee";
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, model);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            written = outPath,
            lodsBefore,
            lods = ModelLodTrimmer.LodCount(model),
            lodRefusal,
            skin,
            skinSkipped,
            legTags,
            legTagsSkipped,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// How a garment sits on the bodies it is worn over, per cloth submesh: how far each vertex is from the nearest skin of
    /// any of them, signed by that skin's normal. Inside (below -0.5 mm) is a clip — skin showing through in game.
    /// </summary>
    private static int Measure(Dictionary<string, string> opts)
    {
        var garment = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "garment")))
                   ?? throw new UsageException("The garment could not be read.");
        var bodyFiles = Required(opts, "bodies").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var bodies = bodyFiles
            .Select(p => ModelPartReader.Read(File.ReadAllBytes(p)) ?? throw new UsageException($"{p} could not be read."))
            .Select(b => new BodySurface(b, 0.01f))
            .ToList();
        var bodySkins = bodyFiles.Select(p => ModelSkinReader.Read(File.ReadAllBytes(p), null, null)).ToList();
        var garmentSkin = ModelSkinReader.Read(File.ReadAllBytes(Required(opts, "garment")), null, null);

        Dictionary<string, float> WeightsOf(XivLiveMesh.SkinnedMesh s, int v, float scale, Dictionary<string, float>? into = null)
        {
            into ??= new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float w = s.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                if (w <= 0f) continue;
                string bone = s.BoneNames[s.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]];
                into[bone] = into.GetValueOrDefault(bone) + w * scale;
            }
            return into;
        }
        // Cloth within 5 mm (or --weight-reach-mm) of drawn skin, rigged more than a tenth apart from the skin under it.
        float weightReach = opts.TryGetValue("weight-reach-mm", out var reachArg)
            ? float.Parse(reachArg, System.Globalization.CultureInfo.InvariantCulture) / 1000f : 0.005f;
        int nearSkin = 0, apartFromSkin = 0;
        var insideBands = new Dictionary<(float Y, string Side, string Face), (int N, float Deepest)>();
        // How far the cloth sits off the skin, by height and face: what "it stands off the body" looks like in numbers.
        var offBands = new Dictionary<(float Y, string Face), List<float>>();
        // Which island each cloth vertex is on, and per island the points more than 5 mm inside.
        var islandOf = new Dictionary<int, string>();
        var islandSize = new Dictionary<string, int>();
        foreach (var part in garment.Parts.Where(p => p.Island >= 0))
        {
            var vs = part.Triangles.Distinct().ToList();
            islandSize[part.Label] = vs.Count;
            foreach (int v in vs) islandOf[v] = part.Label;
        }
        var deepIslands = new Dictionary<string, (int N, float Deepest, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)>();
        // Per island, every vertex's signed distance from the skin: a coarse piece standing off is lost in a whole-garment median.
        var islandOff = new Dictionary<string, List<float>>();
        var detail = new SortedDictionary<(float Y, string Side), List<float>>();
        var coarse = new HashSet<int>();
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                System.Numerics.Vector3 Pt(int i) => new(garment.Positions[i * 3], garment.Positions[i * 3 + 1], garment.Positions[i * 3 + 2]);
                float longest = MathF.Max(System.Numerics.Vector3.Distance(Pt(a), Pt(b)),
                                MathF.Max(System.Numerics.Vector3.Distance(Pt(b), Pt(c)), System.Numerics.Vector3.Distance(Pt(c), Pt(a))));
                if (longest > 0.015f) { coarse.Add(a); coarse.Add(b); coarse.Add(c); }
            }
        var apartBands = new SortedDictionary<float, int>();

        const float Reach = 0.05f, Clip = -0.0005f;
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
        {
            var verts = part.Triangles.Distinct().ToList();
            var signed = new List<float>();
            foreach (int v in verts)
            {
                var p = new System.Numerics.Vector3(garment.Positions[v * 3], garment.Positions[v * 3 + 1], garment.Positions[v * 3 + 2]);
                BodySurface.Hit best = default;
                bool found = false;
                int on = -1;
                for (int b = 0; b < bodies.Count; b++)
                    if (bodies[b].Nearest(p, found ? best.Distance : Reach, out var hit)) { best = hit; found = true; on = b; }
                if (!found) continue;
                float s = System.Numerics.Vector3.Dot(p - best.Point, best.Normal);
                signed.Add(s);
                if (islandOf.TryGetValue(v, out var isl))
                {
                    if (!islandOff.TryGetValue(isl, out var il)) islandOff[isl] = il = [];
                    il.Add(s);
                    // --island-detail <label suffix>: that island's distances by 2 cm of height and by side.
                    // --island-detail coarse: instead every vertex of a triangle with an edge over 15 mm (a low-poly panel).
                    if (opts.TryGetValue("island-detail", out var want)
                        && (want == "coarse" ? coarse.Contains(v) : isl.EndsWith(want, StringComparison.Ordinal)))
                    {
                        var dk = (MathF.Floor(p.Y * 50f) / 50f, p.X >= 0 ? "x+" : "x-");
                        if (!detail.TryGetValue(dk, out var dl)) detail[dk] = dl = [];
                        dl.Add(s);
                    }
                }
                var offKey = (MathF.Floor(p.Y * 20f) / 20f, p.Z >= 0 ? "front" : "back");
                if (!offBands.TryGetValue(offKey, out var offList)) offBands[offKey] = offList = [];
                offList.Add(best.Distance);
                if (s < Clip)
                {
                    var key = (MathF.Floor(p.Y * 20f) / 20f, p.X >= 0 ? "x+" : "x-", p.Z >= 0 ? "front" : "back");
                    var (n, deep) = insideBands.GetValueOrDefault(key);
                    insideBands[key] = (n + 1, MathF.Min(deep, s));
                    if (s < -0.005f && islandOf.TryGetValue(v, out var island))
                    {
                        var (count, deepest, min, max) = deepIslands.GetValueOrDefault(island, (0, 0f, p, p));
                        deepIslands[island] = (count + 1, MathF.Min(deepest, s),
                                               System.Numerics.Vector3.Min(min, p), System.Numerics.Vector3.Max(max, p));
                    }
                }

                if (best.Distance <= weightReach && garmentSkin != null && bodySkins[on] is { } under)
                {
                    nearSkin++;
                    var mine = WeightsOf(garmentSkin, v, 1f);
                    var theirs = WeightsOf(under, best.A, best.U);
                    WeightsOf(under, best.B, best.V, theirs);
                    WeightsOf(under, best.C, best.W, theirs);
                    float diff = mine.Keys.Union(theirs.Keys).Sum(k => MathF.Abs(mine.GetValueOrDefault(k) - theirs.GetValueOrDefault(k)));
                    if (diff > 0.2f)
                    {
                        apartFromSkin++;
                        float band = MathF.Floor(p.Y * 20f) / 20f;
                        apartBands[band] = apartBands.GetValueOrDefault(band) + 1;
                    }
                }
            }
            if (signed.Count == 0) { Console.WriteLine($"{part.Label}\t{part.Material}\tno skin within 5 cm"); continue; }
            signed.Sort();
            int inside = signed.Count(s => s < Clip);
            Console.WriteLine($"{part.Label}\t{Path.GetFileName(part.Material)}\tverts {verts.Count}\tinside {inside}" +
                              $" ({100f * inside / signed.Count:0.0}%)\tdeepest {signed[0] * 1000f:0.0} mm" +
                              $"\tmedian {signed[signed.Count / 2] * 1000f:0.0} mm\tp95 {signed[(int)(signed.Count * 0.95f)] * 1000f:0.0} mm");
        }
        if (opts.ContainsKey("bands"))
            foreach (var (key, (n, deep)) in insideBands.Where(kv => kv.Value.N >= 10)
                                                        .OrderByDescending(kv => kv.Key.Y).ThenBy(kv => kv.Key.Side))
                Console.WriteLine($"  inside at y {key.Y:0.00} {key.Side} {key.Face}\t{n}\tdeepest {deep * 1000f:0.0} mm");
        if (opts.ContainsKey("islands"))
            foreach (var (island, list) in islandOff.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                list.Sort();
                Console.WriteLine($"  island {island}\t{list.Count} verts\tmedian {list[list.Count / 2] * 1000f:0.0} mm" +
                                  $"\tp90 {list[(int)(list.Count * 0.9f)] * 1000f:0.0} mm\tmax {list[^1] * 1000f:0.0} mm");
            }
        foreach (var (key, list) in detail.Reverse())
        {
            list.Sort();
            Console.WriteLine($"  detail y {key.Y:0.00} {key.Side}\t{list.Count}\tmin {list[0] * 1000f:0.0}\tmedian {list[list.Count / 2] * 1000f:0.0}\tmax {list[^1] * 1000f:0.0} mm");
        }
        if (opts.ContainsKey("off"))
            foreach (var (key, list) in offBands.Where(kv => kv.Value.Count >= 20)
                                                .OrderByDescending(kv => kv.Key.Y).ThenBy(kv => kv.Key.Face))
            {
                list.Sort();
                Console.WriteLine($"  off the skin at y {key.Y:0.00} {key.Face}\t{list.Count}\tmedian {list[list.Count / 2] * 1000f:0.0} mm" +
                                  $"\tp90 {list[(int)(list.Count * 0.9f)] * 1000f:0.0} mm");
            }
        if (opts.ContainsKey("bands"))
            foreach (var (island, (n, deep, min, max)) in deepIslands.OrderByDescending(kv => kv.Value.N).Take(15))
                Console.WriteLine($"  island {island} ({islandSize[island]} verts): {n} more than 5 mm inside, deepest {deep * 1000f:0.0} mm," +
                                  $" x {min.X:0.000}..{max.X:0.000} y {min.Y:0.000}..{max.Y:0.000} z {min.Z:0.000}..{max.Z:0.000}");
        Console.WriteLine($"cloth within {weightReach * 1000f:0} mm of skin: {nearSkin}\trigged >0.2 apart from the skin under it: {apartFromSkin}");
        foreach (var (band, n) in apartBands.Reverse())
            Console.WriteLine($"  y {band:0.00}-{band + 0.05f:0.00} m\t{n}");
        return 0;
    }

    /// <summary>
    /// Cloth points within 2 mm of each other — stacked layers, seam twins — whose weights differ by more than a tenth
    /// (summed difference over 0.2). Close and rigged apart is what pulls apart in a pose: skin, or a layer beneath,
    /// showing through. By height band, and the bones most often behind the split.
    /// </summary>
    private static int MeasureSplit(Dictionary<string, string> opts)
    {
        var bytes = File.ReadAllBytes(Required(opts, "garment"));
        var model = ModelPartReader.Read(bytes) ?? throw new UsageException("The garment could not be read.");
        var skin = ModelSkinReader.Read(bytes, null, null) ?? throw new UsageException("The garment's skinning could not be read.");
        if (skin.VertexCount * 3 != model.Positions.Length) throw new UsageException("Skinning and geometry disagree.");

        float Near = opts.TryGetValue("near-mm", out var nearArg)
            ? float.Parse(nearArg, System.Globalization.CultureInfo.InvariantCulture) / 1000f : 0.002f;
        float minY = opts.TryGetValue("min-y", out var minYArg)
            ? float.Parse(minYArg, System.Globalization.CultureInfo.InvariantCulture) : float.MinValue;
        const float Apart = 0.2f;
        System.Numerics.Vector3 At(int v) => new(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
        var cloth = model.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct().Where(v => At(v).Y >= minY).ToList();
        Dictionary<string, float> Weights(int v)
        {
            var d = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float w = skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                if (w <= 0f) continue;
                string bone = skin.BoneNames[skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]];
                d[bone] = d.GetValueOrDefault(bone) + w;
            }
            return d;
        }

        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(System.Numerics.Vector3 p) => ((int)MathF.Floor(p.X / Near), (int)MathF.Floor(p.Y / Near), (int)MathF.Floor(p.Z / Near));
        foreach (int v in cloth)
        {
            var key = Cell(At(v));
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
            bucket.Add(v);
        }

        var weights = cloth.ToDictionary(v => v, Weights);
        var split = new HashSet<int>();
        var bones = new Dictionary<string, int>(StringComparer.Ordinal);
        int pairs = 0, splitPairs = 0;
        foreach (int v in cloth)
        {
            var p = At(v);
            var (cx, cy, cz) = Cell(p);
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int z = cz - 1; z <= cz + 1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int u in bucket)
                {
                    if (u <= v || System.Numerics.Vector3.Distance(p, At(u)) > Near) continue;
                    pairs++;
                    var a = weights[v];
                    var b = weights[u];
                    float diff = a.Keys.Union(b.Keys).Sum(k => MathF.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k)));
                    if (diff <= Apart) continue;
                    splitPairs++;
                    split.Add(v);
                    split.Add(u);
                    string worst = a.Keys.Union(b.Keys).OrderByDescending(k => MathF.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k))).First();
                    bones[worst] = bones.GetValueOrDefault(worst) + 1;
                }
            }
        }

        Console.WriteLine($"cloth verts {cloth.Count}\tclose pairs {pairs}\trigged apart {splitPairs}\tverts involved {split.Count}");
        foreach (var band in split.GroupBy(v => MathF.Floor(At(v).Y * 20f) / 20f).OrderByDescending(g => g.Key))
            Console.WriteLine($"  y {band.Key:0.00}-{band.Key + 0.05f:0.00} m\t{band.Count()}");
        foreach (var (bone, n) in bones.OrderByDescending(kv => kv.Value).Take(8))
            Console.WriteLine($"  {bone}\t{n}");

        // What the waist follows: summed weight per bone over cloth above the band given (default 0.92 m).
        float above = opts.TryGetValue("above", out var aboveArg) ? float.Parse(aboveArg,System.Globalization.CultureInfo.InvariantCulture) : 0.92f;
        var waist = cloth.Where(v => At(v).Y > above).ToList();
        var share = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (int v in waist)
            foreach (var (bone, w) in weights[v])
                share[bone] = share.GetValueOrDefault(bone) + w;
        Console.WriteLine($"  cloth above {above:0.00} m: {waist.Count} verts, by bone:");
        foreach (var (bone, w) in share.OrderByDescending(kv => kv.Value).Take(8))
            Console.WriteLine($"    {bone}\t{100f * w / Math.Max(1, waist.Count):0.0}%");
        return 0;
    }

    /// <summary>
    /// Two builds of one garment, vertex by vertex (they must share a vertex order): how many cloth vertices moved more
    /// than 2 mm between them, the farthest, and where — by height band and side.
    /// </summary>
    private static int MeasureDiff(Dictionary<string, string> opts)
    {
        var a = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "garment"))) ?? throw new UsageException("garment unreadable");
        var b = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "other"))) ?? throw new UsageException("other unreadable");
        if (a.Positions.Length != b.Positions.Length) throw new UsageException("The two models do not share a vertex order.");
        var cloth = a.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                           .SelectMany(p => p.Triangles).Distinct().ToList();
        var moved = new List<(int V, float D)>();
        foreach (int v in cloth)
        {
            var pa = new System.Numerics.Vector3(a.Positions[v * 3], a.Positions[v * 3 + 1], a.Positions[v * 3 + 2]);
            var pb = new System.Numerics.Vector3(b.Positions[v * 3], b.Positions[v * 3 + 1], b.Positions[v * 3 + 2]);
            float d = System.Numerics.Vector3.Distance(pa, pb);
            if (d > 0.002f) moved.Add((v, d));
        }
        Console.WriteLine($"cloth verts {cloth.Count}\tmoved > 2 mm: {moved.Count}" +
                          (moved.Count > 0 ? $"\tfarthest {moved.Max(m => m.D) * 1000f:0.0} mm" : ""));
        foreach (var band in moved.GroupBy(m => (Y: MathF.Floor(a.Positions[m.V * 3 + 1] * 20f) / 20f,
                                                 Side: a.Positions[m.V * 3] >= 0 ? "x+" : "x-",
                                                 Face: a.Positions[m.V * 3 + 2] >= 0 ? "front" : "back"))
                                  .OrderByDescending(g => g.Key.Y).ThenBy(g => g.Key.Side))
            Console.WriteLine($"  y {band.Key.Y:0.00} {band.Key.Side} {band.Key.Face}\t{band.Count()}\tmax {band.Max(m => m.D) * 1000f:0.0} mm");
        return 0;
    }

    /// <summary>
    /// Layers that crossed: pairs of cloth points stacked as authored (one within 6 mm in front of the other along its
    /// normal, within 2 mm to the side, on a parallel sheet) whose order along that normal flipped in the refit, or whose
    /// gap closed to under 0.2 mm. Cloth vertices are paired in order, so the two must carry the same cloth.
    /// </summary>
    private static int MeasureCrossings(Dictionary<string, string> opts)
    {
        var refit = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "garment"))) ?? throw new UsageException("garment unreadable");
        var src = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "source"))) ?? throw new UsageException("source unreadable");
        static List<int> Cloth(ModelParts m) => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                                 .SelectMany(p => p.Triangles).Distinct().OrderBy(v => v).ToList();
        var cs = Cloth(src);
        var cr = Cloth(refit);
        if (cs.Count != cr.Count) throw new UsageException($"cloth differs: {cs.Count} vs {cr.Count} vertices");
        static System.Numerics.Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        static System.Numerics.Vector3 N(ModelParts m, int v) => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);

        const float Reach = 0.006f, Side = 0.002f, Closed = 0.0002f, Cell = 0.006f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(System.Numerics.Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
        for (int i = 0; i < cs.Count; i++)
        {
            var k = Key(P(src, cs[i]));
            if (!grid.TryGetValue(k, out var b)) grid[k] = b = [];
            b.Add(i);
        }

        int stacked = 0, flipped = 0, closed = 0;
        var bands = new SortedDictionary<float, int>();
        for (int i = 0; i < cs.Count; i++)
        {
            var p = P(src, cs[i]);
            var n = N(src, cs[i]);
            if (n.LengthSquared() < 1e-12f) continue;
            n = System.Numerics.Vector3.Normalize(n);
            var (x0, y0, z0) = Key(p);
            for (int x = x0 - 1; x <= x0 + 1; x++)
            for (int y = y0 - 1; y <= y0 + 1; y++)
            for (int z = z0 - 1; z <= z0 + 1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int j in bucket)
                {
                    if (j == i) continue;
                    var v = P(src, cs[j]) - p;
                    float along = System.Numerics.Vector3.Dot(v, n);
                    if (along < 0.0005f || along > Reach) continue;   // in front, and not the same point
                    if ((v - n * along).Length() > Side) continue;
                    if (MathF.Abs(System.Numerics.Vector3.Dot(N(src, cs[j]), n)) < 0.5f) continue;
                    stacked++;

                    var rn = N(refit, cr[i]);
                    if (rn.LengthSquared() < 1e-12f) rn = n;
                    float now = System.Numerics.Vector3.Dot(P(refit, cr[j]) - P(refit, cr[i]), System.Numerics.Vector3.Normalize(rn));
                    if (now >= Closed) continue;
                    if (now < 0f) flipped++; else closed++;
                    float band = MathF.Floor(p.Y * 20f) / 20f;
                    bands[band] = bands.GetValueOrDefault(band) + 1;
                }
            }
        }
        // Turned-over cloth triangles, against the author's winding — counted on the finished file, after every pass.
        var toRefit = new Dictionary<int, int>(cs.Count);
        for (int i = 0; i < cs.Count; i++) toRefit[cs[i]] = cr[i];
        int turned = 0;
        foreach (var part in src.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var n0 = System.Numerics.Vector3.Cross(P(src, b) - P(src, a), P(src, c) - P(src, a));
                if (n0.LengthSquared() < 1e-14f) continue;
                int ra = toRefit[a], rb = toRefit[b], rc = toRefit[c];
                var n1 = System.Numerics.Vector3.Cross(P(refit, rb) - P(refit, ra), P(refit, rc) - P(refit, ra));
                if (System.Numerics.Vector3.Dot(n0, n1) <= 0f) turned++;
            }
        Console.WriteLine($"stacked pairs {stacked}\tcrossed {flipped}\tclosed to < 0.2 mm {closed}\tturned-over triangles {turned}");
        foreach (var (band, count) in bands.Reverse())
            Console.WriteLine($"  y {band:0.00}-{band + 0.05f:0.00} m\t{count}");
        return 0;
    }

    /// <summary>
    /// A garment against a Proteus second-skin shell (every mesh of it, whatever its material): per cloth vertex, the
    /// nearest shell vertex within 2 cm and the signed distance along that vertex's normal. Below zero the shell is in
    /// front of the cloth — drawn through it in game.
    /// </summary>
    private static int MeasureShell(Dictionary<string, string> opts)
    {
        var garment = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "garment"))) ?? throw new UsageException("garment unreadable");
        var shell = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "shell"))) ?? throw new UsageException("shell unreadable");
        static System.Numerics.Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        static System.Numerics.Vector3 N(ModelParts m, int v) => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);

        const float Cell = 0.01f, Reach = 0.02f;
        (int, int, int) Key(System.Numerics.Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
        var grid = new Dictionary<(int, int, int), List<int>>();
        var shellVerts = shell.Parts.Where(p => p.Island < 0).SelectMany(p => p.Triangles).Distinct().ToList();
        foreach (var part in shell.Parts.Where(p => p.Island < 0))
            Console.WriteLine($"  shell mesh {part.Label}\t{Path.GetFileName(part.Material)}\t{part.TriangleCount} tris");
        foreach (int v in shellVerts)
        {
            var k = Key(P(shell, v));
            if (!grid.TryGetValue(k, out var b)) grid[k] = b = [];
            b.Add(v);
        }

        // --skin-side: the garment's own skin instead of its cloth — how far the shell stands off the skin it was cut from.
        bool skinSide = opts.ContainsKey("skin-side");
        var cloth = garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) == skinSide)
                                 .SelectMany(p => p.Triangles).Distinct().ToList();
        if (skinSide)
        {
            // Signed distance of each skin vertex from the nearest shell vertex, as a spread.
            var offs = new List<float>();
            foreach (int v in cloth)
            {
                var p = P(garment, v);
                var (cx, cy, cz) = Key(p);
                int best = -1;
                float bestD = Reach * Reach;
                for (int x = cx - 2; x <= cx + 2; x++)
                for (int y = cy - 2; y <= cy + 2; y++)
                for (int z = cz - 2; z <= cz + 2; z++)
                {
                    if (!grid.TryGetValue((x, y, z), out var b)) continue;
                    foreach (int s in b)
                    {
                        float d = System.Numerics.Vector3.DistanceSquared(p, P(shell, s));
                        if (d < bestD) { bestD = d; best = s; }
                    }
                }
                if (best < 0) continue;
                var n = N(shell, best);
                if (n.LengthSquared() < 1e-12f) continue;
                // Shell's offset from the skin, outward positive.
                offs.Add(-System.Numerics.Vector3.Dot(p - P(shell, best), System.Numerics.Vector3.Normalize(n)));
            }
            offs.Sort();
            if (offs.Count == 0) { Console.WriteLine("no skin near the shell"); return 0; }
            Console.WriteLine($"skin verts {cloth.Count}\tnear the shell {offs.Count}\tshell off the skin: median {offs[offs.Count / 2] * 1000f:0.00} mm" +
                              $"\tp95 {offs[(int)(offs.Count * 0.95f)] * 1000f:0.00} mm\tmax {offs[^1] * 1000f:0.00} mm" +
                              $"\tover 1 mm {offs.Count(o => o > 0.001f)}");
            return 0;
        }
        int near = 0, through = 0;
        var bands = new SortedDictionary<(float Y, string Side, string Face), (int N, float Deepest)>();
        foreach (int v in cloth)
        {
            var p = P(garment, v);
            var (cx, cy, cz) = Key(p);
            int best = -1;
            float bestD = Reach * Reach;
            for (int x = cx - 2; x <= cx + 2; x++)
            for (int y = cy - 2; y <= cy + 2; y++)
            for (int z = cz - 2; z <= cz + 2; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var b)) continue;
                foreach (int s in b)
                {
                    float d = System.Numerics.Vector3.DistanceSquared(p, P(shell, s));
                    if (d < bestD) { bestD = d; best = s; }
                }
            }
            if (best < 0) continue;
            near++;
            var n = N(shell, best);
            if (n.LengthSquared() < 1e-12f) continue;
            float signed = System.Numerics.Vector3.Dot(p - P(shell, best), System.Numerics.Vector3.Normalize(n));
            if (signed >= -0.0002f) continue;
            through++;
            var key = (MathF.Floor(p.Y * 20f) / 20f, p.X >= 0 ? "x+" : "x-", p.Z >= 0 ? "front" : "back");
            var (count, deep) = bands.GetValueOrDefault(key);
            bands[key] = (count + 1, MathF.Min(deep, signed));
        }
        Console.WriteLine($"cloth verts {cloth.Count}\twithin 2 cm of the shell {near}\tbehind the shell {through}");
        foreach (var (key, (count, deep)) in bands.Where(kv => kv.Value.N >= 5).Reverse())
            Console.WriteLine($"  y {key.Y:0.00} {key.Side} {key.Face}\t{count}\tdeepest {deep * 1000f:0.0} mm");
        return 0;
    }

    /// <summary>
    /// Write models' LOD0 as one .obj to look at: <c>--obj out.obj --models a.mdl;b.mdl</c>. Each model's cloth and skin
    /// come out as separate objects, named after the file's parent folders, in the game's own units (metres, Y up).
    /// </summary>
    private static int ExportObj(Dictionary<string, string> opts, string outPath)
    {
        using var w = new StreamWriter(outPath);
        int baseIndex = 1;
        foreach (string path in Required(opts, "models").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var model = ModelPartReader.Read(File.ReadAllBytes(path)) ?? throw new UsageException($"{path} unreadable");
            string name = string.Join("_", path.Split(Path.DirectorySeparatorChar).TakeLast(6).Take(2)).Replace(' ', '_');
            int vc = model.Positions.Length / 3;
            for (int v = 0; v < vc; v++)
                w.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"v {model.Positions[v * 3]} {model.Positions[v * 3 + 1]} {model.Positions[v * 3 + 2]}"));
            foreach (bool skin in new[] { false, true })
            {
                w.WriteLine($"o {name}_{(skin ? "skin" : "cloth")}");
                foreach (var part in model.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) == skin))
                    for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                        w.WriteLine($"f {part.Triangles[t] + baseIndex} {part.Triangles[t + 1] + baseIndex} {part.Triangles[t + 2] + baseIndex}");
            }
            baseIndex += vc;
        }
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    /// <summary>
    /// How far the skin pokes through the MIDDLE of big cloth triangles (an edge over 15 mm): per such triangle, the
    /// furthest a skin vertex of the given models that projects inside it lies on the cloth's outer side of its plane.
    /// <c>--measure --face-poke --garment g.mdl --bodies a.mdl;b.mdl</c>.
    /// </summary>
    private static int MeasureFacePoke(Dictionary<string, string> opts)
    {
        var garment = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "garment"))) ?? throw new UsageException("unreadable");
        static System.Numerics.Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
        var skin = new List<System.Numerics.Vector3>();
        foreach (string path in Required(opts, "bodies").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var body = ModelPartReader.Read(File.ReadAllBytes(path)) ?? throw new UsageException($"{path} unreadable");
            foreach (int v in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                        .SelectMany(p => p.Triangles).Distinct())
                skin.Add(P(body, v));
        }
        const float Cell = 0.01f;
        (int, int, int) Key(System.Numerics.Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < skin.Count; i++)
        {
            var k = Key(skin[i]);
            if (!grid.TryGetValue(k, out var b)) grid[k] = b = [];
            b.Add(i);
        }

        var pokes = new List<(float Depth, float Y)>();
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var a = P(garment, part.Triangles[t]);
                var b = P(garment, part.Triangles[t + 1]);
                var c = P(garment, part.Triangles[t + 2]);
                float longest = MathF.Max(System.Numerics.Vector3.Distance(a, b), MathF.Max(System.Numerics.Vector3.Distance(b, c), System.Numerics.Vector3.Distance(c, a)));
                if (longest <= 0.015f) continue;
                var n = System.Numerics.Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-14f) continue;
                n = System.Numerics.Vector3.Normalize(n);
                var centre = (a + b + c) / 3f;

                // Candidates near the triangle; the cloth's outer side is the side away from the nearest skin point.
                var lo = System.Numerics.Vector3.Min(a, System.Numerics.Vector3.Min(b, c)) - new System.Numerics.Vector3(0.01f);
                var hi = System.Numerics.Vector3.Max(a, System.Numerics.Vector3.Max(b, c)) + new System.Numerics.Vector3(0.01f);
                var (x0, y0, z0) = Key(lo);
                var (x1, y1, z1) = Key(hi);
                var near = new List<System.Numerics.Vector3>();
                for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    if (grid.TryGetValue((x, y, z), out var bucket))
                        foreach (int i in bucket) near.Add(skin[i]);
                if (near.Count == 0) continue;
                var closest = near.MinBy(s => System.Numerics.Vector3.DistanceSquared(s, centre));
                if (System.Numerics.Vector3.Dot(centre - closest, n) < 0f) n = -n;

                float worst = 0f;
                foreach (var s in near)
                {
                    float h = System.Numerics.Vector3.Dot(s - a, n);
                    if (h <= worst || h > 0.01f) continue;
                    // Inside the triangle when projected onto its plane.
                    var q = s - n * h;
                    var v0 = b - a; var v1 = c - a; var v2 = q - a;
                    float d00 = System.Numerics.Vector3.Dot(v0, v0), d01 = System.Numerics.Vector3.Dot(v0, v1), d11 = System.Numerics.Vector3.Dot(v1, v1);
                    float d20 = System.Numerics.Vector3.Dot(v2, v0), d21 = System.Numerics.Vector3.Dot(v2, v1);
                    float den = d00 * d11 - d01 * d01;
                    if (MathF.Abs(den) < 1e-20f) continue;
                    float bv = (d11 * d20 - d01 * d21) / den, bw = (d00 * d21 - d01 * d20) / den;
                    if (bv < 0.05f || bw < 0.05f || bv + bw > 0.95f) continue;
                    worst = h;
                }
                if (worst > 0f) pokes.Add((worst, centre.Y));
            }
        pokes.Sort((p, q) => p.Depth.CompareTo(q.Depth));
        Console.WriteLine($"big cloth triangles with skin through their middle: {pokes.Count}" +
                          (pokes.Count > 0 ? $"\tmedian {pokes[pokes.Count / 2].Depth * 1000f:0.0} mm\tp90 {pokes[(int)(pokes.Count * 0.9f)].Depth * 1000f:0.0} mm\tmax {pokes[^1].Depth * 1000f:0.0} mm" : ""));
        foreach (var band in pokes.GroupBy(p => MathF.Floor(p.Y * 50f) / 50f).OrderByDescending(g => g.Key))
            Console.WriteLine($"  y {band.Key:0.00}\t{band.Count()}\tmax {band.Max(p => p.Depth) * 1000f:0.0} mm");
        return 0;
    }

    /// <summary>Where a model carries body skin: skin vertices per 5 cm height band, front and back.</summary>
    private static int MeasureSkinMap(Dictionary<string, string> opts)
    {
        var model = ModelPartReader.Read(File.ReadAllBytes(Required(opts, "garment"))) ?? throw new UsageException("unreadable");
        var skin = model.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                              .SelectMany(p => p.Triangles).Distinct().ToList();
        Console.WriteLine($"skin verts {skin.Count}\tLODs {ModelLodTrimmer.LodCount(File.ReadAllBytes(Required(opts, "garment")))}");
        foreach (var band in skin.GroupBy(v => (Y: MathF.Floor(model.Positions[v * 3 + 1] * 20f) / 20f,
                                               Face: model.Positions[v * 3 + 2] >= 0 ? "front" : "back"))
                                 .OrderByDescending(g => g.Key.Y).ThenBy(g => g.Key.Face))
            Console.WriteLine($"  y {band.Key.Y:0.00} {band.Key.Face}\t{band.Count()}");
        return 0;
    }

    /// <summary>What <c>--save</c> reads: refitted sizes of one garment, all refitted onto one body mod.</summary>
    private sealed record SaveManifest(string Mod, string Group, string GamePath, string BodyMod, string From,
                                       List<SaveRefit> Refits);

    /// <param name="Option">The option the size becomes.</param>
    /// <param name="Model">The refitted model file.</param>
    /// <param name="To">The body it was refitted onto, as the record names it.</param>
    private sealed record SaveRefit(string Option, string Model, string To);

    /// <summary>
    /// Save refitted sizes into a mod as options of a size group, exactly as the Studio's Body size Save does
    /// (<see cref="BodyRetargetWriter.Save(string, string, string, string, string, IReadOnlyList{BodyRetargetWriter.Refit}, string?, IReadOnlyList{object}?)"/>):
    /// one manifest write, the group outranking the mod's others, and each option recorded so the Studio can undo it.
    /// </summary>
    private static int Save(Dictionary<string, string> opts)
    {
        var manifest = JsonSerializer.Deserialize<SaveManifest>(File.ReadAllText(Required(opts, "manifest")),
                                                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new UsageException("The manifest is empty.");
        var refits = manifest.Refits
            .Select(r => new BodyRetargetWriter.Refit(r.Option, File.ReadAllBytes(r.Model), r.To))
            .ToList();
        var outcome = BodyRetargetWriter.Save(manifest.Mod, manifest.Group, manifest.GamePath, manifest.BodyMod,
                                              manifest.From, refits);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = outcome.Ok, group = outcome.Group, options = outcome.Option,
                                                         message = outcome.Message },
                                                   new JsonSerializerOptions { WriteIndented = true }));
        return outcome.Ok ? 0 : 1;
    }

    /// <summary>The tags <c>--tag-legs</c> carries: the knee (<c>atr_hiz</c>) and the calf/shin (<c>atr_sne</c>).</summary>
    private static readonly string[] LegTags = ["atr_hiz", "atr_sne"];

    /// <summary>The legs body <c>--tag-legs auto</c> falls back to when the garment's cannot be told.</summary>
    private const string DefaultTagLegs = "default legs - smallclothes/sfw small.mdl";

    private static int Run(Dictionary<string, string> opts)
    {
        string garmentPath = Required(opts, "garment");
        string outDir = Required(opts, "out-dir");
        string slot = opts.GetValueOrDefault("slot", "_top");
        string? race = opts.GetValueOrDefault("race", "0201");
        bool male = race != null && BodySizeCatalog.IsMaleRace(race);

        string targetRoot = Required(opts, "body-root");
        string sourceRoot = opts.GetValueOrDefault("from-root", targetRoot);
        bool acrossBodies = !string.Equals(Path.GetFullPath(sourceRoot).TrimEnd('\\'),
                                           Path.GetFullPath(targetRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var dst = BodySizeCatalog.Read(targetRoot);
        var src = acrossBodies ? BodySizeCatalog.Read(sourceRoot) : dst;
        var uvRemap = opts.TryGetValue("uvmaps", out var pluginDir) ? new UVRemapService(NullLog(), pluginDir) : null;

        string fromArg = Required(opts, "from");
        var garmentBytes = File.ReadAllBytes(garmentPath);
        var garment = ModelPartReader.Read(garmentBytes)
                   ?? throw new UsageException($"{garmentPath} could not be read as a model.");

        string fromConfidence = "Given";
        var from = fromArg == "auto"
            ? DetectSource(src, slot, garment, garmentBytes, race, out fromConfidence)
            : Find(src, slot, fromArg);
        var targets = Required(opts, "to").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Select(t => Pair(t, "--to"))
                                          .Select(t => (Label: t.Value, Option: Find(dst, slot, t.Key)))
                                          .ToList();
        Directory.CreateDirectory(outDir);

        ushort? sourceMask = MaskOf(src, slot), targetMask = MaskOf(dst, slot);

        string? legsSkipped = null;
        Legs? legs = null;
        if (slot != "_dwn" && (opts.ContainsKey("legs") || opts.ContainsKey("legs-to")))
        {
            if (acrossBodies && opts.ContainsKey("legs") && !opts.ContainsKey("legs-to"))
                throw new UsageException("--legs moves along one body mod's size words; across body mods give --legs-to.");
            var pinned = opts.TryGetValue("legs-from", out var legsFrom) ? Find(src, "_dwn", legsFrom) : null;
            var fixedTo = opts.TryGetValue("legs-to", out var legsTo) ? Find(dst, "_dwn", legsTo) : null;
            legs = Legs.Detect(src, dst, garment, garmentBytes, race, opts.GetValueOrDefault("legs"), fixedTo,
                               targets.Select(t => t.Label).ToList(), pinned, out legsSkipped);
        }

        // A legs garment that rises over the waist covers some of the chest body too, so it moves with the chest as well —
        // the Studio's "other slot" pair. One chest for every size of the call, built once and shared, as there.
        BodyRetarget.SlotPair? chestPair = null;
        string? chestTo = null, chestNote = null;
        if (opts.ContainsKey("chest-from") || opts.ContainsKey("chest-to"))
        {
            if (slot != "_dwn") throw new UsageException("--chest-from/--chest-to are for a legs garment (--slot _dwn).");
            var chestSource = Find(src, "_top", Required(opts, "chest-from"));
            var chestTarget = Find(dst, "_top", Required(opts, "chest-to"));
            string chestSourcePath = src.PathOf(chestSource), chestTargetPath = dst.PathOf(chestTarget);
            if (string.Equals(Path.GetFullPath(chestSourcePath), Path.GetFullPath(chestTargetPath),
                              StringComparison.OrdinalIgnoreCase))
                chestNote = $"the waist already sits on \"{chestTarget.Label}\"";
            else if (BodyRetarget.BuildPair("_top", chestSourcePath, chestTargetPath, "_top", male, MaskOf(src, "_top"),
                                            MaskOf(dst, "_top"), uvRemap, out var built) is { } chestRefusal)
                chestNote = chestRefusal;
            else
            {
                chestPair = built;
                chestTo = chestTarget.Rel;
            }
        }

        var sizes = new List<object>();
        foreach (var (label, option) in targets)
        {
            var pairs = new List<BodyRetarget.SlotPair>();
            if (BodyRetarget.BuildPair(slot, src.PathOf(from), dst.PathOf(option), slot, male, sourceMask, targetMask,
                                       uvRemap, out var pair) is { } refusal)
            {
                sizes.Add(new { label, to = option.Rel, refusal });
                continue;
            }
            pairs.Add(pair);
            if (chestPair is { } chest) pairs.Add(chest);

            // The hips follow along; a size whose legs are the source's own needs none.
            string? legsTo = legs?.TargetFor(label);
            string? legsNote = legs == null ? null : legsTo == null ? legs.NoteFor(label) : null;
            if (legs != null && legsTo != null)
            {
                if (BodyRetarget.BuildPair("_dwn", legs.SourcePath, legsTo, "_dwn", male, legs.SourceMask,
                                           legs.TargetMask, uvRemap, out var legsPair) is { } legsRefusal)
                    legsNote = legsRefusal;
                else
                    pairs.Add(legsPair);
            }

            // --tune NoSettle,NoStackPush,...: a diagnostic's knobs, each named as BodyRetarget.Tuning names it.
            var tuning = new BodyRetarget.Tuning();
            foreach (string knob in opts.GetValueOrDefault("tune", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                tuning = knob.Trim() switch
                {
                    "NoSettle" => tuning with { NoSettle = true },
                    "NoFaceSettle" => tuning with { NoFaceSettle = true },
                    "NoLayerGuard" => tuning with { NoLayerGuard = true },
                    "NoUnderbustLift" => tuning with { NoUnderbustLift = true },
                    "NoRelax" => tuning with { NoRelax = true },
                    "NoGiveUp" => tuning with { NoGiveUp = true },
                    "NoLayerKnit" => tuning with { NoLayerKnit = true },
                    "NoFollow" => tuning with { NoFollow = true },
                    _ => throw new UsageException($"Unknown --tune knob \"{knob}\"."),
                };
            var planned = BodyRetarget.WithTuning(tuning, () => BodyRetarget.Plan(
                garment, garmentBytes, pairs, slot, pushOut: !opts.ContainsKey("no-push"),
                replaceSkin: true, acrossBodies: acrossBodies, cutHidden: true));
            string written = Path.Combine(outDir, label + ".mdl");
            File.WriteAllBytes(written, planned.Model);

            var r = planned.Report;
            sizes.Add(new
            {
                label,
                to = option.Rel,
                legsTo = legsTo == null ? null : Path.GetRelativePath(dst.ModRoot, legsTo),
                legsNote,
                chestTo,
                chestNote,
                written,
                report = new
                {
                    nodes = r.Nodes, snapped = r.Snapped, transferred = r.Transferred, missed = r.Missed,
                    pushed = r.Pushed, folded = r.Folded, laid = r.Laid,
                    worstMoveMm = r.WorstMove * 1000f, worstPushMm = r.WorstPush * 1000f,
                },
            });
        }

        var result = new
        {
            garment = garmentPath,
            from = from.Rel,
            fromConfidence,
            acrossBodies,
            legs = legs == null
                ? (object?)(legsSkipped == null ? null : new { skipped = legsSkipped })
                : new { from = legs.SourceRel, confidence = legs.Confidence, note = legs.Note },
            sizes,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// The body option the garment was made on, read from the garment's own body mesh the way the Studio does
    /// (<see cref="BodySizeMatch.Rank"/>). For a mod made by hand, where nobody wrote down which of Neolithe's 114 chests
    /// a size was fitted to. Refused unless the reading is at least a guess: refitting from the wrong body moves every
    /// point by the difference between two bodies the garment never sat on.
    /// </summary>
    private static BodyOption DetectSource(BodySizeCatalog catalog, string slot, ModelParts garment, byte[] garmentBytes,
                                           string? race, out string confidence)
    {
        var bones = new HashSet<string>(SecondSkinWriter.Parse(garmentBytes).BoneNames, StringComparer.Ordinal);
        var ranking = BodySizeMatch.Rank(garment, catalog.For(slot, race), catalog.PathOf, bones);
        confidence = ranking.Confidence.ToString();
        if (ranking.Best is not { } best
            || ranking.Confidence is BodySizeMatch.Confidence.NoBodyMesh or BodySizeMatch.Confidence.TooLittle
                                  or BodySizeMatch.Confidence.Ambiguous)
            throw new UsageException($"Could not tell which {slot} body of {catalog.ModRoot} the garment was made on " +
                                     $"({ranking.Confidence}). Pass --from.");
        return best.Option;
    }

    /// <summary>A body mod's own IMC mask for a slot, under its default settings — no player here to ask.</summary>
    private static ushort? MaskOf(BodySizeCatalog catalog, string slot)
        => BodyRetarget.ImcSlotName(slot) is { } equip ? ImcEntrySource.MaskFor(catalog.ModRoot, 0, equip, null) : null;

    /// <summary>The option whose model is <paramref name="rel"/> — slashes and case as Penumbra treats them.</summary>
    private static BodyOption Find(BodySizeCatalog catalog, string slot, string rel)
    {
        string want = Normal(rel);
        return catalog.For(slot).FirstOrDefault(o => Normal(o.Rel) == want)
            ?? throw new UsageException($"No {slot} body option in {catalog.ModRoot} uses \"{rel}\". Run --list to see them.");
    }

    internal static string Normal(string rel) => rel.Replace('\\', '/').Trim('/').ToLowerInvariant();

    private static KeyValuePair<string, string> Pair(string text, string what)
    {
        int eq = text.LastIndexOf('=');
        if (eq <= 0 || eq == text.Length - 1) throw new UsageException($"{what} wants <value>=<label>, got \"{text}\".");
        return new(text[..eq].Trim(), text[(eq + 1)..].Trim());
    }

    private static string Required(Dictionary<string, string> opts, string key)
        => opts.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new UsageException($"--{key} is required.");

    private static Dictionary<string, string> Parse(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"Unexpected argument \"{args[i]}\".");
            string key = args[i][2..];
            opts[key] = key is "list" or "detect" or "finish" or "inspect" or "save" or "measure" or "no-push" or "bands" or "skin-map" or "skin-side" or "off" or "islands" or "face-poke" ? "" : i + 1 < args.Length ? args[++i] : throw new UsageException($"--{key} needs a value.");
        }
        return opts;
    }

    /// <summary>
    /// A log that says nothing, for <see cref="UVRemapService"/>, which only wants somewhere to write. Every member
    /// returns its type's default.
    /// </summary>
    private static IPluginLog NullLog() => DispatchProxy.Create<IPluginLog, SilentLog>();

    /// <summary>
    /// Where the garment's hem sits on the legs, and which legs each size moves it to.
    /// <para/>
    /// Detected the way the Studio panel does (<see cref="BodySizeMatch.Rank"/>), which for a top reads the hips from the
    /// cloth within the author's first-listed legs family. Then, within one body mod, moved along that family by the
    /// size word ending its file name (<c>GEN A Small.mdl</c> for S becomes <c>GEN A Large.mdl</c> for L); or, into
    /// another body mod, sent to the one legs option given for every size — Rue+ files every size as
    /// <c>c0201e0000_dwn.mdl</c> in a folder of its own, and its hips do not change with the chest size anyway.
    /// </summary>
    private sealed class Legs
    {
        public required string SourceRel { get; init; }
        public required string SourcePath { get; init; }
        public required string Confidence { get; init; }
        public string? Note { get; init; }
        public ushort? SourceMask { get; init; }
        public ushort? TargetMask { get; init; }
        public required Dictionary<string, string?> Targets { get; init; }
        public required Dictionary<string, string> Notes { get; init; }

        public string? TargetFor(string label) => Targets.GetValueOrDefault(label);
        public string? NoteFor(string label) => Notes.GetValueOrDefault(label);

        /// <param name="src">The body mod the garment was made on — where its legs are detected.</param>
        /// <param name="dst">The body mod the sizes are for.</param>
        /// <param name="map"><c>S=Small,M=Medium,L=Large</c>: the legs size word each garment size wants, within
        /// <paramref name="src"/>. Null with <paramref name="fixedTo"/>.</param>
        /// <param name="fixedTo">One legs option of <paramref name="dst"/> every size moves the hem to.</param>
        /// <param name="pinned">The legs the garment was made on, when the caller knows: detection is skipped. Worth
        /// giving — within one mesh the cloth reading favours whichever legs fill the hem most, so a top made on the
        /// plain legs can read as Neobelly.</param>
        /// <param name="why">When null is returned, why the hips are not refitted.</param>
        public static Legs? Detect(BodySizeCatalog src, BodySizeCatalog dst, ModelParts garment, byte[] garmentBytes,
                                   string? race, string? map, BodyOption? fixedTo, IReadOnlyList<string> labels,
                                   BodyOption? pinned, out string? why)
        {
            why = null;
            var options = src.For("_dwn", race);
            if (options.Count == 0)
            {
                why = "the body mod the garment was made on has no legs options";
                return null;
            }

            BodyOption source;
            string confidence;
            string? note = null;
            if (pinned != null)
            {
                source = pinned;
                confidence = "Given";
            }
            else
            {
                var bones = new HashSet<string>(SecondSkinWriter.Parse(garmentBytes).BoneNames, StringComparer.Ordinal);
                var ranking = BodySizeMatch.Rank(garment, options, src.PathOf, bones);
                if (ranking.Best is not { } best
                    || ranking.Confidence is BodySizeMatch.Confidence.NoBodyMesh or BodySizeMatch.Confidence.TooLittle
                                          or BodySizeMatch.Confidence.Ambiguous)
                {
                    why = $"could not tell which legs the garment was made on ({ranking.Confidence})";
                    return null;
                }
                source = best.Option;
                confidence = ranking.Confidence.ToString();
                if (ranking.FromCloth) note = "read from how the cloth sits on the hips";
            }

            string sourcePath = src.PathOf(source);
            var targets = new Dictionary<string, string?>(StringComparer.Ordinal);
            var notes = new Dictionary<string, string>(StringComparer.Ordinal);

            if (fixedTo != null)
            {
                string to = dst.PathOf(fixedTo);
                foreach (string label in labels)
                {
                    if (string.Equals(Path.GetFullPath(to), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                        notes[label] = $"the hem already sits on \"{fixedTo.Label}\"";
                    else
                        targets[label] = to;
                }
            }
            else
            {
                var words = (map ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Select(t => Pair(t, "--legs"))
                                       .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                string stem = Path.GetFileNameWithoutExtension(sourcePath);
                string? word = words.Values.Distinct()
                                    .FirstOrDefault(w => stem.EndsWith(" " + w, StringComparison.OrdinalIgnoreCase));
                foreach (var (label, want) in words)
                {
                    if (word == null)
                    {
                        notes[label] = $"the detected legs \"{stem}\" do not end in a size word, so the hips were not refitted";
                        continue;
                    }
                    if (string.Equals(want, word, StringComparison.OrdinalIgnoreCase))
                    {
                        notes[label] = $"the hem already sits on \"{stem}\"";
                        continue;
                    }
                    string file = Path.Combine(Path.GetDirectoryName(sourcePath)!,
                                               stem[..^word.Length] + want + Path.GetExtension(sourcePath));
                    string? match = options.Select(src.PathOf)
                                           .FirstOrDefault(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(file),
                                                                              StringComparison.OrdinalIgnoreCase));
                    if (match == null) notes[label] = $"no legs option \"{Path.GetFileName(file)}\" beside \"{stem}\"";
                    else targets[label] = match;
                }
            }

            return new Legs
            {
                SourceRel = source.Rel,
                SourcePath = sourcePath,
                Confidence = confidence,
                Note = note,
                SourceMask = MaskOf(src, "_dwn"),
                TargetMask = MaskOf(dst, "_dwn"),
                Targets = targets,
                Notes = notes,
            };
        }
    }

    private sealed class UsageException(string message) : Exception(message);
}

/// <summary>The body of <c>Program.NullLog</c>. Public and unsealed, as <see cref="DispatchProxy"/> requires.</summary>
public class SilentLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.ReturnType is { IsValueType: true } t && t != typeof(void) ? Activator.CreateInstance(t) : null;
}
