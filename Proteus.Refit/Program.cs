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

            var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, slot, replaceSkin: true,
                                            acrossBodies: acrossBodies, cutHidden: true);
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
            opts[key] = key is "list" or "detect" or "finish" or "inspect" or "save" ? "" : i + 1 < args.Length ? args[++i] : throw new UsageException($"--{key} needs a value.");
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
