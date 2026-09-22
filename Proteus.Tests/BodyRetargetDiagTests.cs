using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Instruments, not assertions — the house diag style. These run against the real body mods on this machine and print
/// numbers to judge by; they skip silently when those mods are not installed.
/// <para/>
/// The one that earns its keep is <see cref="Every_Neolithe_size_pair_shares_one_topology"/>: the whole v1 design rests
/// on the claim that every size in a family is the same mesh re-sculpted, and that claim was established by comparing
/// file lengths. This checks it across the real option space, through the reader the feature actually uses.
/// </summary>
public class BodyRetargetDiagTests(ITestOutputHelper output)
{
    private const string NeolitheRoot = @"E:\Penumbradt\Neolithe [ALL IN ONE]";
    private const string ChestFolder = NeolitheRoot + @"\DEFAULT CHEST - SmallClothes";
    private const string ExtraChestFolder = NeolitheRoot + @"\EXTRA CHEST - SmallClothes";
    private const string BuffChestFolder = NeolitheRoot + @"\EXTRA CHEST BUFF - SmallClothes";
    private const string LegsFolder = NeolitheRoot + @"\DEFAULT LEGS - SmallClothes";

    [Fact]
    public void Every_Neolithe_size_pair_shares_one_topology()
    {
        if (!Directory.Exists(ChestFolder)) return;

        // Sampled across the shape families as well as the size axis, because the picker allows any pair.
        var chest = Models(
            ChestFolder + @"\SFW XS.mdl",
            ChestFolder + @"\SFW S.mdl",
            ChestFolder + @"\SFW M.mdl",
            ChestFolder + @"\SFW L.mdl",
            ExtraChestFolder + @"\SFW Almond M.mdl",
            ExtraChestFolder + @"\SFW Hazelnut L.mdl",
            ExtraChestFolder + @"\SFW Macadamia XS.mdl",
            BuffChestFolder + @"\SFW Buff M.mdl");

        var legs = Models(
            LegsFolder + @"\SFW Small.mdl",
            LegsFolder + @"\SFW Medium.mdl",
            LegsFolder + @"\SFW Large.mdl");

        int pairs = 0, agreed = 0;
        foreach (var group in new[] { ("chest", chest), ("legs", legs) })
        {
            var (label, models) = group;
            output.WriteLine($"── {label}: {models.Count} models read ──");
            foreach (var a in models)
            foreach (var b in models)
            {
                if (ReferenceEquals(a.Model, b.Model)) continue;
                string nameA = a.Name, nameB = b.Name;
                pairs++;
                bool ok = IdentityCorrespondence.TryBuild(a.Model, b.Model, label, out var built, out string refusal,
                                                          a.Uv, b.Uv);
                if (ok)
                {
                    agreed++;
                    float worst = 0f;
                    foreach (var d in built!.Field)
                        if (d is { } v) worst = MathF.Max(worst, v.Length());
                    output.WriteLine($"  OK   {nameA,-22} -> {nameB,-22} worst move {worst * 1000f,7:F2} mm");
                }
                else
                {
                    output.WriteLine($"  FAIL {nameA,-22} -> {nameB,-22} {refusal}");
                }
            }
        }

        output.WriteLine($"{agreed}/{pairs} pairs share a topology.");
        Assert.Equal(pairs, agreed);
    }

    [Fact]
    public void A_different_genital_family_is_mapped_by_texture_coordinate()
    {
        // The legs axis is NOT one mesh: Gen A/B/C/Puffy each replace geometry and have their own vertex counts, so
        // vertex-for-vertex is impossible. They share the Bibo+ texture layout, though, so the pair is mapped by uv —
        // the same route a refit onto Rue+ takes — rather than refused.
        if (!File.Exists(LegsFolder + @"\GEN B Medium.mdl") || !File.Exists(LegsFolder + @"\SFW Medium.mdl")) return;

        var sfwBytes = File.ReadAllBytes(LegsFolder + @"\SFW Medium.mdl");
        var genBytes = File.ReadAllBytes(LegsFolder + @"\GEN B Medium.mdl");
        var sfw = ModelPartReader.Read(sfwBytes)!;
        var gen = ModelPartReader.Read(genBytes)!;

        Assert.False(IdentityCorrespondence.TryBuild(sfw, gen, "legs", out _, out _, Uv(sfwBytes), Uv(genBytes)));
        bool ok = BodyCorrespondence.TryBuild(sfw, Uv(sfwBytes), gen, Uv(genBytes), "legs", out var built,
                                              out string refusal);
        Assert.True(ok, refusal);
        Assert.IsType<UvAtlasCorrespondence>(built);
        output.WriteLine(built!.Describe());

        // Both Medium, so wherever the genitals are not, the legs should barely move.
        var moves = built.Field.Where(d => d.HasValue).Select(d => d!.Value.Length()).OrderBy(x => x).ToList();
        float median = moves[moves.Count / 2];
        output.WriteLine($"median move {median * 1000f:F2} mm, p95 {moves[(int)(moves.Count * 0.95f)] * 1000f:F2} mm");
        Assert.True(median < 0.001f, $"two Medium legs should mostly coincide; median move {median * 1000f:F2} mm");
    }

    /// <summary>
    /// Every chest option refitted from one source through the real correspondence choice, reporting the route taken.
    /// The pair that first failed in game was a Pushup chest onto a Neobelly Almond NSFW one: same structure, 98.8% of
    /// uvs in common, refused by a 99% bar. Nothing here may be refused.
    /// </summary>
    [Fact]
    public void Every_chest_option_can_be_refitted_onto()
    {
        if (!Directory.Exists(NeolitheRoot)) return;
        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var chest = catalog.For("_top").Where(o => o.Group == "CHEST: SmallClothes").ToList();
        var src = chest.First(o => o.Label == "DEFAULT PUSHUP · SFW Pushup M");
        var srcBytes = File.ReadAllBytes(catalog.PathOf(src));
        var source = ModelPartReader.Read(srcBytes)!;
        var sourceUv = Uv(srcBytes);

        int identity = 0, atlas = 0;
        var refused = new List<string>();
        foreach (var option in chest)
        {
            var bytes = File.ReadAllBytes(catalog.PathOf(option));
            var target = ModelPartReader.Read(bytes)!;
            if (!BodyCorrespondence.TryBuild(source, sourceUv, target, Uv(bytes), "chest", out var built, out string why))
            {
                refused.Add($"{option.Label}: {why}");
                continue;
            }
            if (built is IdentityCorrespondence) identity++;
            else
            {
                atlas++;
                output.WriteLine($"by uv: {option.Label} — {built!.Describe()}");
            }
        }

        output.WriteLine($"{chest.Count} options: {identity} vertex for vertex, {atlas} by texture coordinate, " +
                         $"{refused.Count} refused");
        foreach (var r in refused) output.WriteLine($"REFUSED {r}");
        Assert.Empty(refused);
    }

    /// <summary>
    /// How good the texture-coordinate route is, on a pair where the exact answer is known. Pushup M and L are the same
    /// mesh, so vertex for vertex is exact; forcing the uv route on them measures its error directly, and refitting the
    /// author's M both ways shows whether that error matters against the author's own L.
    /// </summary>
    [Fact]
    public void The_texture_coordinate_route_matches_the_exact_one()
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string fromPath = Path.Combine(ThisOldThing, "neolithe m", model);
        string toPath = Path.Combine(ThisOldThing, "neolithe l", model);
        if (!File.Exists(fromPath) || !File.Exists(toPath)) return;

        var srcBytes = File.ReadAllBytes(ExtraChestSmallClothes + @"\SFW Pushup M.mdl");
        var dstBytes = File.ReadAllBytes(ExtraChestSmallClothes + @"\SFW Pushup L.mdl");
        var source = ModelPartReader.Read(srcBytes)!;
        var target = ModelPartReader.Read(dstBytes)!;

        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var exact, out _, Uv(srcBytes), Uv(dstBytes)));
        Assert.True(UvAtlasCorrespondence.TryBuild(source, Uv(srcBytes), target, Uv(dstBytes), "chest", out var atlas,
                                                   out string refusal), refusal);

        var diff = new List<float>();
        var uvS = Uv(srcBytes);
        for (int v = 0; v < exact!.Field.Count; v++)
        {
            if (exact.Field[v] is not { } e || atlas!.Field[v] is not { } a) continue;
            float d = Vector3.Distance(e, a);
            diff.Add(d);
            if (d > 0.001f)
                output.WriteLine($"  v{v} at {At(source, v)} uv ({uvS[v * 2]:F5},{uvS[v * 2 + 1]:F5}): " +
                                 $"exact lands {At(source, v) + e}, by uv {At(source, v) + a} ({d * 1000f:F1} mm)");
        }
        output.WriteLine($"field disagreement over {diff.Count:N0} skin vertices: {Stats(diff)} (mean/p95/max mm)");

        var fromBytes = File.ReadAllBytes(fromPath);
        var authorM = ModelPartReader.Read(fromBytes)!;
        var authorL = ModelPartReader.Read(File.ReadAllBytes(toPath))!;
        var legs = new List<BodyRetarget.SlotPair>();
        AddPair(legs, "_dwn", LegsFolder + @"\SFW Medium.mdl", LegsFolder + @"\SFW Large.mdl");

        foreach (var (label, corr) in new (string, IBodyCorrespondence)[] { ("exact", exact), ("by uv", atlas!) })
        {
            var pairs = new List<BodyRetarget.SlotPair> { new("_top", corr, target) };
            pairs.AddRange(legs);
            var refit = ModelPartReader.Read(BodyRetarget.Plan(authorM, fromBytes, pairs, "_top").Model)!;
            output.WriteLine($"{label,-6} body mesh {Stats(Errors(refit, authorL, true, true))}   " +
                             $"cloth {Stats(Errors(refit, authorL, false, true))}");
        }

        // The WORST vertex, not a percentile: one vertex landing 29 mm off is a spike in game however good the rest is.
        // That is exactly what the neck opening's collapsed-uv triangles did before they were handled as segments.
        Assert.True(diff.Max() < 0.0005f, $"the uv route should agree with the exact one to 0.5 mm; worst {diff.Max() * 1000f:F2} mm");
    }

    /// <summary>
    /// What replacing the skin costs and buys, on an outfit whose author reshaped the skin under it. Against the author's
    /// own L, resizing keeps their reshaping and so should be closer; replacing lays the skin on the plain body and so
    /// should sit on it exactly. Both numbers are printed, so the trade is visible rather than asserted.
    /// </summary>
    [Fact]
    public void What_replacing_the_skin_costs_against_the_author()
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string fromPath = Path.Combine(ThisOldThing, "neolithe m", model);
        string toPath = Path.Combine(ThisOldThing, "neolithe l", model);
        if (!File.Exists(fromPath) || !File.Exists(toPath)) return;

        var fromBytes = File.ReadAllBytes(fromPath);
        var authorM = ModelPartReader.Read(fromBytes)!;
        var authorL = ModelPartReader.Read(File.ReadAllBytes(toPath))!;
        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", ExtraChestSmallClothes + @"\SFW Pushup M.mdl", ExtraChestSmallClothes + @"\SFW Pushup L.mdl");
        AddPair(pairs, "_dwn", LegsFolder + @"\SFW Medium.mdl", LegsFolder + @"\SFW Large.mdl");
        var targetChest = pairs[0].Target;

        foreach (bool replace in new[] { false, true })
        {
            var planned = BodyRetarget.Plan(authorM, fromBytes, pairs, "_top", replaceSkin: replace);
            var refit = ModelPartReader.Read(planned.Model)!;
            output.WriteLine($"{(replace ? "replaced" : "resized"),-9} vs author: body mesh " +
                             $"{Stats(Errors(refit, authorL, true, true))}  cloth {Stats(Errors(refit, authorL, false, true))}");
            output.WriteLine($"{"",-9} body mesh to the plain L body: {Stats(Errors(refit, targetChest, true, false))}" +
                             $"   laid {planned.Report.Laid:N0}, pushed {planned.Report.Pushed:N0}");
        }
    }

    private const string Seaside = @"E:\Penumbradt\Seaside - by Solona (Default)";

    /// <summary>
    /// The refit that came out wrong in game: Seaside's L top onto an NSFW XS chest, "use the new body's skin" on — the
    /// cloth did not shrink and the breast deformed. Seaside ships its own XS top, so it can be scored against the author.
    /// </summary>
    [Fact]
    public void Seaside_large_to_extra_small()
    {
        string lPath = Path.Combine(Seaside, @"common\7\c0201e0194_top.mdl");
        string xsPath = Path.Combine(Seaside, @"top size\neolithe xs\chara\equipment\e0194\model\c0201e0194_top.mdl");
        if (!File.Exists(lPath) || !File.Exists(xsPath) || !Directory.Exists(NeolitheRoot)) return;

        var lBytes = File.ReadAllBytes(lPath);
        var authorL = ModelPartReader.Read(lBytes)!;
        var authorXs = ModelPartReader.Read(File.ReadAllBytes(xsPath))!;
        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var chest = catalog.For("_top");

        var rankL = BodySizeMatch.Rank(authorL, chest, catalog.PathOf);
        var rankXs = BodySizeMatch.Rank(authorXs, chest, catalog.PathOf);
        output.WriteLine($"L top reads as: {rankL.Confidence}; {string.Join(" | ", rankL.Scores.Take(4).Select(s => $"{s.Option.Label} {s.Rms * 1000:F2}mm {s.HitRate:P0}"))}");
        output.WriteLine($"XS top reads as: {rankXs.Confidence}; {string.Join(" | ", rankXs.Scores.Take(4).Select(s => $"{s.Option.Label} {s.Rms * 1000:F2}mm {s.HitRate:P0}"))}");

        bool sameNumbering = authorL.Positions.Length == authorXs.Positions.Length;
        output.WriteLine($"author's L and XS share a numbering: {sameNumbering}");

        void Run(string label, string fromLabel, string toLabel, bool replace)
        {
            var src = chest.First(o => o.Group == "CHEST: SmallClothes" && o.Label == fromLabel);
            var dst = chest.First(o => o.Group == "CHEST: SmallClothes" && o.Label == toLabel);
            var sb = File.ReadAllBytes(catalog.PathOf(src));
            var tb = File.ReadAllBytes(catalog.PathOf(dst));
            var s = ModelPartReader.Read(sb)!;
            var t = ModelPartReader.Read(tb)!;
            Assert.True(BodyCorrespondence.TryBuild(s, Uv(sb), t, Uv(tb), "chest", out var built, out string why), why);
            var planned = BodyRetarget.Plan(authorL, lBytes, [new BodyRetarget.SlotPair("_top", built!, t)], "_top",
                                            replaceSkin: replace);
            var refit = ModelPartReader.Read(planned.Model)!;
            var r = planned.Report;
            output.WriteLine("");
            output.WriteLine($"{label}: {fromLabel} -> {toLabel}, replace {replace} ({built!.GetType().Name})");
            output.WriteLine($"  moved up to {r.WorstMove * 1000:F1} mm, snapped {r.Snapped:N0}, laid {r.Laid:N0}, pushed {r.Pushed:N0}, missed {r.Missed:N0}");
            if (sameNumbering)
                foreach (bool skin in new[] { true, false })
                    output.WriteLine($"  {(skin ? "body mesh" : "cloth"),-10} nothing {Stats(Errors(authorL, authorXs, skin, true))}   refit {Stats(Errors(refit, authorXs, skin, true))}");
            output.WriteLine($"  body mesh to the target body: {Stats(Errors(refit, t, true, false))}");
        }

        Run("as in game", "NEOBELLY ALMOND · SFW Almond L", "DEFAULT ALMOND · NSFW Almond XS", true);
        Run("as in game", "NEOBELLY ALMOND · SFW Almond L", "DEFAULT ALMOND · NSFW Almond XS", false);
        if (rankL.Best is { } bl && rankXs.Best is { } bx)
        {
            Run("detected", bl.Option.Label, bx.Option.Label, true);
            Run("detected", bl.Option.Label, bx.Option.Label, false);
        }
    }

    /// <summary>
    /// The support pass and skin replacement, on and off, against every author-made size on this machine: Seaside's M
    /// to XS, and "This Old Thing"'s M to L, S to L and M to S. A pass that fixes one outfit and hurts another shows up.
    /// </summary>
    [Fact]
    public void Against_every_author_made_size()
    {
        if (!Directory.Exists(NeolitheRoot)) return;
        const string tot = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        var cases = new List<(string Name, string From, string To, (string Slot, string Src, string Dst)[] Pairs)>
        {
            ("Seaside M->XS", Path.Combine(Seaside, @"common\7\c0201e0194_top.mdl"),
             Path.Combine(Seaside, @"top size\neolithe xs\chara\equipment\e0194\model\c0201e0194_top.mdl"),
             [("_top", ExtraChestSmallClothes + @"\SFW Almond M.mdl", ExtraChestSmallClothes + @"\SFW Almond XS.mdl")]),
            ("TOT M->L", Path.Combine(ThisOldThing, "neolithe m", tot), Path.Combine(ThisOldThing, "neolithe l", tot),
             [("_top", ExtraChestSmallClothes + @"\SFW Pushup M.mdl", ExtraChestSmallClothes + @"\SFW Pushup L.mdl"),
              ("_dwn", LegsFolder + @"\SFW Medium.mdl", LegsFolder + @"\SFW Large.mdl")]),
            ("TOT S->L", Path.Combine(ThisOldThing, "neolithe s", tot), Path.Combine(ThisOldThing, "neolithe l", tot),
             [("_top", ExtraChestSmallClothes + @"\SFW Pushup S.mdl", ExtraChestSmallClothes + @"\SFW Pushup L.mdl"),
              ("_dwn", LegsFolder + @"\SFW Small.mdl", LegsFolder + @"\SFW Large.mdl")]),
            ("TOT M->S", Path.Combine(ThisOldThing, "neolithe m", tot), Path.Combine(ThisOldThing, "neolithe s", tot),
             [("_top", ExtraChestSmallClothes + @"\SFW Pushup M.mdl", ExtraChestSmallClothes + @"\SFW Pushup S.mdl"),
              ("_dwn", LegsFolder + @"\SFW Medium.mdl", LegsFolder + @"\SFW Small.mdl")]),
        };

        output.WriteLine($"{"",-15}{"",-22}{"body mesh mean/p95/max",29}{"cloth mean/p95/max",29}");
        foreach (var (name, fromPath, toPath, pairSpec) in cases)
        {
            if (!File.Exists(fromPath) || !File.Exists(toPath)) continue;
            var fromBytes = File.ReadAllBytes(fromPath);
            var authorFrom = ModelPartReader.Read(fromBytes)!;
            var authorTo = ModelPartReader.Read(File.ReadAllBytes(toPath))!;
            var pairs = new List<BodyRetarget.SlotPair>();
            foreach (var (slot, src, dst) in pairSpec) AddPair(pairs, slot, src, dst);

            output.WriteLine($"{name,-15}{"nothing",-22}{Stats(Errors(authorFrom, authorTo, true, true)),29}" +
                             $"{Stats(Errors(authorFrom, authorTo, false, true)),29}");
            foreach (bool replace in new[] { false, true })
            {
                var planned = BodyRetarget.Plan(authorFrom, fromBytes, pairs, "_top", replaceSkin: replace);
                var refit = ModelPartReader.Read(planned.Model)!;
                string label = replace ? "refit + replace skin" : "refit";
                output.WriteLine($"{"",-15}{label,-22}{Stats(Errors(refit, authorTo, true, true)),29}" +
                                 $"{Stats(Errors(refit, authorTo, false, true)),29}");
            }
        }
    }

    /// <summary>Where Seaside's cloth error is: by garment part and by distance from the body, author's motion vs ours.</summary>
    [Fact]
    public void Seaside_cloth_error_by_part()
    {
        string lPath = Path.Combine(Seaside, @"common\7\c0201e0194_top.mdl");
        string xsPath = Path.Combine(Seaside, @"top size\neolithe xs\chara\equipment\e0194\model\c0201e0194_top.mdl");
        if (!File.Exists(lPath) || !File.Exists(xsPath) || !Directory.Exists(NeolitheRoot)) return;

        var lBytes = File.ReadAllBytes(lPath);
        var authorL = ModelPartReader.Read(lBytes)!;
        var authorXs = ModelPartReader.Read(File.ReadAllBytes(xsPath))!;
        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", ExtraChestSmallClothes + @"\SFW Almond M.mdl", ExtraChestSmallClothes + @"\SFW Almond XS.mdl");
        var refit = ModelPartReader.Read(BodyRetarget.Plan(authorL, lBytes, pairs, "_top").Model)!;
        var surface = new BodySurface(pairs[0].Correspondence.Source, 0.01f);

        output.WriteLine($"{"part",-8}{"verts",7}{"author moved",14}{"we moved",10}{"error",8}{"cos",7}{"from body",11}");
        foreach (var part in authorL.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
        {
            var verts = part.Triangles.Distinct().ToList();
            double a = 0, o = 0, e = 0, c = 0, d = 0;
            foreach (int v in verts)
            {
                var p = At(authorL, v);
                var author = At(authorXs, v) - p;
                var ours = At(refit, v) - p;
                a += author.Length();
                o += ours.Length();
                e += Vector3.Distance(author, ours);
                if (author.Length() > 1e-4f && ours.Length() > 1e-4f)
                    c += Vector3.Dot(Vector3.Normalize(author), Vector3.Normalize(ours));
                d += surface.Nearest(p, 0.3f, out var hit) ? hit.Distance : 0.3f;
            }
            int n = verts.Count;
            output.WriteLine($"{part.Label,-8}{n,7}{a / n * 1000,14:F2}{o / n * 1000,10:F2}{e / n * 1000,8:F2}{c / n,7:F2}{d / n * 1000,11:F1}");
        }

        // Island by island inside the worst part, so a separate piece shows up as its own row.
        foreach (var island in authorL.Parts.Where(p => p.Island >= 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
        {
            var verts = island.Triangles.Distinct().ToList();
            double a = 0, o = 0, d = 0;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (int v in verts)
            {
                var p = At(authorL, v);
                a += (At(authorXs, v) - p).Length();
                o += (At(refit, v) - p).Length();
                d += surface.Nearest(p, 0.3f, out var hit) ? hit.Distance : 0.3f;
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            int n = verts.Count;
            output.WriteLine($"  island {island.Label,-7}{n,6} verts  author {a / n * 1000,6:F2}  ours {o / n * 1000,6:F2}  " +
                             $"from body {d / n * 1000,5:F1}  box {lo} .. {hi}");
        }
    }

    /// <summary>The cup points where Seaside's rigid ring attaches: how the author moved them, and how we did.</summary>
    [Fact]
    public void Seaside_ring_attachment()
    {
        string lPath = Path.Combine(Seaside, @"common\7\c0201e0194_top.mdl");
        string xsPath = Path.Combine(Seaside, @"top size\neolithe xs\chara\equipment\e0194\model\c0201e0194_top.mdl");
        if (!File.Exists(lPath) || !File.Exists(xsPath) || !Directory.Exists(NeolitheRoot)) return;

        var lBytes = File.ReadAllBytes(lPath);
        var authorL = ModelPartReader.Read(lBytes)!;
        var authorXs = ModelPartReader.Read(File.ReadAllBytes(xsPath))!;
        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", ExtraChestSmallClothes + @"\SFW Almond M.mdl", ExtraChestSmallClothes + @"\SFW Almond XS.mdl");
        var refit = ModelPartReader.Read(BodyRetarget.Plan(authorL, lBytes, pairs, "_top").Model)!;
        var surface = new BodySurface(pairs[0].Correspondence.Source, 0.01f);

        var ring = authorL.Parts.First(p => p.Island < 0 && p.Label == "2.2").Triangles.Distinct().ToList();
        var cup = authorL.Parts.First(p => p.Label == "2.1.1").Triangles.Distinct().ToList();
        var ringAuthor = At(authorXs, ring[0]) - At(authorL, ring[0]);
        output.WriteLine($"ring moved by the author: {ringAuthor} ({ringAuthor.Length() * 1000:F2} mm)");

        foreach (float within in new[] { 0.002f, 0.005f, 0.01f, 0.02f })
        {
            var near = cup.Where(c => ring.Any(r => Vector3.Distance(At(authorL, c), At(authorL, r)) < within)).ToList();
            if (near.Count == 0) { output.WriteLine($"cup points within {within * 1000:F0} mm of the ring: none"); continue; }
            var a = near.Aggregate(Vector3.Zero, (s, v) => s + (At(authorXs, v) - At(authorL, v))) / near.Count;
            var o = near.Aggregate(Vector3.Zero, (s, v) => s + (At(refit, v) - At(authorL, v))) / near.Count;
            float d = near.Average(v => surface.Nearest(At(authorL, v), 0.3f, out var h) ? h.Distance : 0.3f);
            output.WriteLine($"cup points within {within * 1000:F0} mm of the ring: {near.Count}, author moved {a} " +
                             $"({a.Length() * 1000:F2} mm), we moved {o} ({o.Length() * 1000:F2} mm), from body {d * 1000:F1} mm");
        }
    }

    /// <summary>Which vertices of the Seaside refit are wrong, and what they have in common.</summary>
    [Fact]
    public void Seaside_worst_vertices()
    {
        string lPath = Path.Combine(Seaside, @"common\7\c0201e0194_top.mdl");
        string xsPath = Path.Combine(Seaside, @"top size\neolithe xs\chara\equipment\e0194\model\c0201e0194_top.mdl");
        if (!File.Exists(lPath) || !File.Exists(xsPath) || !Directory.Exists(NeolitheRoot)) return;

        var lBytes = File.ReadAllBytes(lPath);
        var authorL = ModelPartReader.Read(lBytes)!;
        var authorXs = ModelPartReader.Read(File.ReadAllBytes(xsPath))!;
        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", ExtraChestSmallClothes + @"\SFW Almond M.mdl", ExtraChestSmallClothes + @"\SFW Almond XS.mdl");
        var source = pairs[0].Correspondence.Source;
        var srcSurface = new BodySurface(source, 0.01f);

        var laid = ModelPartReader.Read(BodyRetarget.Plan(authorL, lBytes, pairs, "_top", replaceSkin: true).Model)!;
        var plain = ModelPartReader.Read(BodyRetarget.Plan(authorL, lBytes, pairs, "_top", replaceSkin: false).Model)!;

        var partOf = new string[authorL.Positions.Length / 3];
        foreach (var part in authorL.Parts.Where(p => p.Island < 0))
            foreach (int v in part.Triangles) partOf[v] = part.Label + " " + Path.GetFileName(part.Material);

        foreach (var (label, model) in new[] { ("laid skin", laid), ("plain", plain) })
        {
            output.WriteLine($"── {label}: worst 12 vs the author's XS ──");
            var worst = Enumerable.Range(0, authorL.Positions.Length / 3)
                .Select(v => (V: v, Err: Vector3.Distance(At(model, v), At(authorXs, v))))
                .OrderByDescending(x => x.Err).Take(12);
            foreach (var (v, err) in worst)
            {
                var p = At(authorL, v);
                float d = srcSurface.Nearest(p, 0.3f, out var hit) ? hit.Distance : -1f;
                output.WriteLine($"  v{v} {partOf[v]} at {p}  err {err * 1000:F1}  ours moved {(At(model, v) - p).Length() * 1000:F1} " +
                                 $"author moved {(At(authorXs, v) - p).Length() * 1000:F1}  from body {d * 1000:F1} mm");
            }
        }
    }

    [Fact]
    public void The_catalog_reads_Neolithe_s_real_option_list()
    {
        if (!Directory.Exists(NeolitheRoot)) return;

        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        Assert.True(catalog.IsBody);

        foreach (string slot in catalog.Slots)
        {
            var options = catalog.For(slot);
            output.WriteLine($"{slot}: {options.Count} options across " +
                             $"{options.Select(o => o.Group).Distinct().Count()} groups");
            foreach (var group in options.GroupBy(o => o.Group))
                output.WriteLine($"    {group.Count(),4}  {group.Key}");
        }

        // Separator options like "--- DEFAULT ---" carry no file, so they must not be offered.
        Assert.DoesNotContain(catalog.Options, o => o.Name.StartsWith("---", StringComparison.Ordinal));

        // Every listed option must actually resolve on disk, case-insensitively.
        var missing = catalog.Options.Where(o => !File.Exists(catalog.PathOf(o))).ToList();
        foreach (var m in missing.Take(10)) output.WriteLine($"MISSING {m.Label} -> {m.Rel}");
        Assert.Empty(missing);
    }

    [Fact]
    public void The_detector_finds_the_size_a_model_was_built_from()
    {
        string probe = ChestFolder + @"\SFW M.mdl";
        if (!File.Exists(probe) || !Directory.Exists(NeolitheRoot)) return;

        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var candidates = catalog.For("_top");
        if (candidates.Count == 0) return;

        // A body model stands in for a garment here: its skin mesh is trivially a copy of one size's, which is exactly
        // the situation a real gear model is in. If the detector cannot find the size a model IS, it will not find the
        // size a garment was built against either.
        var garment = ModelPartReader.Read(File.ReadAllBytes(probe))!;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ranking = BodySizeMatch.Rank(garment, candidates, catalog.PathOf);
        clock.Stop();

        output.WriteLine($"{candidates.Count} candidates scored in {clock.ElapsedMilliseconds} ms");
        output.WriteLine($"confidence {ranking.Confidence}");
        foreach (var s in ranking.Scores.Take(5))
            output.WriteLine($"  {s.HitRate,7:P1}  rms {s.Rms * 1000f,7:F3} mm  {s.Option.FullLabel}");

        Assert.Equal(BodySizeMatch.Confidence.Exact, ranking.Confidence);
        Assert.Equal("SFW M", ranking.Best!.Value.Option.Name);
    }

    private const string ThisOldThing = @"E:\Penumbradt\This Old Thing - by Solona\size";

    /// <summary>
    /// The one measurement here with a right answer to compare against.
    /// <para/>
    /// "This Old Thing" ships its top in four Neolithe sizes that its author fitted by hand. Refit the author's M onto
    /// the L body automatically and compare the result with the author's own L: every millimetre of difference is
    /// the retarget disagreeing with a person who knew what they wanted. "Did nothing" — the M left as it is — is
    /// printed beside it as the baseline the refit has to beat.
    /// </summary>
    [Theory]
    [InlineData("neolithe m", "neolithe l")]
    [InlineData("neolithe m", "neolithe s")]
    [InlineData("neolithe s", "neolithe l")]
    public void Refit_against_the_author_s_own_sizes(string fromSize, string toSize)
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string fromPath = Path.Combine(ThisOldThing, fromSize, model);
        string toPath = Path.Combine(ThisOldThing, toSize, model);
        if (!File.Exists(fromPath) || !File.Exists(toPath) || !Directory.Exists(NeolitheRoot)) return;

        var fromBytes = File.ReadAllBytes(fromPath);
        var toBytes = File.ReadAllBytes(toPath);
        var authorFrom = ModelPartReader.Read(fromBytes)!;
        var authorTo = ModelPartReader.Read(toBytes)!;

        // Exactly what the Studio does: detect the source size of every slot the body mod sizes, from the garment
        // itself, and take the target as the size detected for the author's other version — standing in for the size
        // a user would pick. Nothing here is forced.
        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var pairs = new List<BodyRetarget.SlotPair>();
        foreach (string slot in new[] { "_top", "_dwn" })
        {
            var options = catalog.For(slot);
            var rankFrom = BodySizeMatch.Rank(authorFrom, options, catalog.PathOf);
            var rankTo = BodySizeMatch.Rank(authorTo, options, catalog.PathOf);
            output.WriteLine($"{slot} {fromSize}: {rankFrom.Confidence}, {Top(rankFrom)}");
            output.WriteLine($"{slot} {toSize}: {rankTo.Confidence}, {Top(rankTo)}");
            if (rankFrom.Best is not { } src || rankTo.Best is not { } dst) continue;
            output.WriteLine($"{slot} refit {src.Option.FullLabel} -> {dst.Option.FullLabel}");
            if (src.Option.Rel == dst.Option.Rel) continue;   // the same size both ways: nothing to change here

            var sourceBytes = File.ReadAllBytes(catalog.PathOf(src.Option));
            var targetBytes = File.ReadAllBytes(catalog.PathOf(dst.Option));
            var source = ModelPartReader.Read(sourceBytes)!;
            var target = ModelPartReader.Read(targetBytes)!;
            if (!BodyCorrespondence.TryBuild(source, Uv(sourceBytes), target, Uv(targetBytes), slot,
                                             out var built, out string refusal))
            {
                output.WriteLine($"{slot}: {refusal}");
                continue;
            }
            output.WriteLine($"{slot}: {built!.Describe()}");
            pairs.Add(new BodyRetarget.SlotPair(slot, built, target));
        }
        if (pairs.Count == 0) return;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var planned = BodyRetarget.Plan(authorFrom, fromBytes, pairs, "_top");
        clock.Stop();
        var refit = ModelPartReader.Read(planned.Model)!;

        var r = planned.Report;
        output.WriteLine($"solve {clock.ElapsedMilliseconds} ms: snapped {r.Snapped:N0} ({r.SnapRate:P0}), " +
                         $"pushed {r.Pushed:N0} (worst {r.WorstPush * 1000f:F2} mm), worst move {r.WorstMove * 1000f:F2} mm");

        // Per vertex when the author's two sizes share a numbering (the reliable measure); otherwise nearest surface.
        bool sameNumbering = authorFrom.Positions.Length == authorTo.Positions.Length
                          && Uv(fromBytes).AsSpan().SequenceEqual(Uv(toBytes));
        output.WriteLine(sameNumbering
                             ? "the author's two sizes share a vertex numbering: comparing vertex for vertex"
                             : "the author's two sizes are different meshes: comparing nearest surface");

        output.WriteLine("");
        output.WriteLine($"{"",-14}{"",8}{"mean",9}{"p95",9}{"max",9}   (mm, against the author's {toSize})");
        foreach (bool skin in new[] { true, false })
        {
            string label = skin ? "body mesh" : "cloth";
            var nothing = Errors(authorFrom, authorTo, skin, sameNumbering);
            var ours = Errors(refit, authorTo, skin, sameNumbering);
            output.WriteLine($"{label,-14}{"nothing",8}{Stats(nothing)}");
            output.WriteLine($"{"",-14}{"refit",8}{Stats(ours)}");
        }
    }

    /// <summary>
    /// The same comparison with the body pair FORCED rather than detected, to separate "the detector picked the wrong
    /// bodies" from "the refit itself is wrong". Run over each reading of which Neolithe sizes the author meant.
    /// </summary>
    [Theory]
    [InlineData("neolithe m", "neolithe l", "DEFAULT · SFW M", "DEFAULT · SFW L")]
    [InlineData("neolithe m", "neolithe l", "DEFAULT · NSFW M", "DEFAULT · NSFW L")]
    [InlineData("neolithe m", "neolithe s", "DEFAULT · SFW M", "DEFAULT · SFW S")]
    public void Refit_with_a_forced_pair_against_the_author(string fromSize, string toSize, string fromOption,
                                                           string toOption)
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string fromPath = Path.Combine(ThisOldThing, fromSize, model);
        string toPath = Path.Combine(ThisOldThing, toSize, model);
        if (!File.Exists(fromPath) || !File.Exists(toPath) || !Directory.Exists(NeolitheRoot)) return;

        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var chest = catalog.For("_top").Where(o => o.Group == "CHEST: SmallClothes").ToList();
        var src = chest.FirstOrDefault(o => o.Label == fromOption);
        var dst = chest.FirstOrDefault(o => o.Label == toOption);
        Assert.True(src != null && dst != null, $"no option {fromOption} or {toOption}");

        var fromBytes = File.ReadAllBytes(fromPath);
        var authorFrom = ModelPartReader.Read(fromBytes)!;
        var authorTo = ModelPartReader.Read(File.ReadAllBytes(toPath))!;

        var sourceBytes = File.ReadAllBytes(catalog.PathOf(src!));
        var targetBytes = File.ReadAllBytes(catalog.PathOf(dst!));
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        Assert.True(IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out string refusal,
                                                    Uv(sourceBytes), Uv(targetBytes)), refusal);

        var planned = BodyRetarget.Plan(authorFrom, fromBytes, [new BodyRetarget.SlotPair("_top", built!, target)], "_top");
        var refit = ModelPartReader.Read(planned.Model)!;
        var r = planned.Report;
        output.WriteLine($"{fromOption} -> {toOption}: snapped {r.Snapped:N0} ({r.SnapRate:P0}), pushed {r.Pushed:N0} " +
                         $"(worst {r.WorstPush * 1000f:F2} mm), worst move {r.WorstMove * 1000f:F2} mm");

        // And how far the garment's own body mesh sits from each candidate body, so it is visible which reading the
        // author's model actually matches.
        output.WriteLine($"garment body mesh to source body: {Stats(Errors(authorFrom, source, true, false))}");

        output.WriteLine($"{"",-14}{"",8}{"mean",9}{"p95",9}{"max",9}   (mm, against the author's {toSize})");
        foreach (bool skin in new[] { true, false })
        {
            output.WriteLine($"{(skin ? "body mesh" : "cloth"),-14}{"nothing",8}{Stats(Errors(authorFrom, authorTo, skin, true))}");
            output.WriteLine($"{"",-14}{"refit",8}{Stats(Errors(refit, authorTo, skin, true))}");
        }
    }

    /// <summary>
    /// Whether the hem's motion belongs to the LEGS. A top's chest model is cut at the waist, so cloth over the hips is
    /// far from it and near the legs model instead; if the author fitted each size to matching hips too, only a refit
    /// that includes the legs pair can follow it. Tried under several readings of which hip sizes were meant.
    /// </summary>
    private const string ExtraChestSmallClothes = NeolitheRoot + @"\EXTRA CHEST - SmallClothes";

    [Theory]
    [InlineData(null, null, true, "default")]
    [InlineData("SFW Medium", "SFW Medium", true, "default")]
    [InlineData("SFW Medium", "SFW Large", true, "default")]
    [InlineData("SFW Medium", "SFW Large", false, "default")]
    [InlineData("SFW Small", "SFW Medium", true, "default")]
    [InlineData("SFW Small", "SFW Large", true, "default")]
    [InlineData("SFW Medium", "SFW Large", true, "pushup")]
    public void The_legs_pair_carries_the_hem(string? legsFrom, string? legsTo, bool pushOut, string chest)
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string fromPath = Path.Combine(ThisOldThing, "neolithe m", model);
        string toPath = Path.Combine(ThisOldThing, "neolithe l", model);
        if (!File.Exists(fromPath) || !File.Exists(toPath) || !Directory.Exists(NeolitheRoot)) return;

        var fromBytes = File.ReadAllBytes(fromPath);
        var authorM = ModelPartReader.Read(fromBytes)!;
        var authorL = ModelPartReader.Read(File.ReadAllBytes(toPath))!;

        var pairs = new List<BodyRetarget.SlotPair>();
        if (chest == "pushup")
            AddPair(pairs, "_top", ExtraChestSmallClothes + @"\SFW Pushup M.mdl", ExtraChestSmallClothes + @"\SFW Pushup L.mdl");
        else
            AddPair(pairs, "_top", ChestFolder + @"\SFW M.mdl", ChestFolder + @"\SFW L.mdl");
        if (legsFrom != null && legsTo != null)
            AddPair(pairs, "_dwn", LegsFolder + $@"\{legsFrom}.mdl", LegsFolder + $@"\{legsTo}.mdl");

        var planned = BodyRetarget.Plan(authorM, fromBytes, pairs, "_top", pushOut);
        var refit = ModelPartReader.Read(planned.Model)!;
        var r = planned.Report;

        string label = $"{chest} chest" + (legsFrom == null ? " only" : $" + legs {legsFrom} -> {legsTo}")
                     + (pushOut ? "" : ", NO push-out");
        output.WriteLine($"{label}: pushed {r.Pushed:N0} (worst {r.WorstPush * 1000f:F2} mm), missed {r.Missed:N0}");
        output.WriteLine($"{"",-14}{"",8}{"mean",9}{"p95",9}{"max",9}   (mm, against the author's L)");
        foreach (bool skin in new[] { true, false })
        {
            output.WriteLine($"{(skin ? "body mesh" : "cloth"),-14}{"nothing",8}{Stats(Errors(authorM, authorL, skin, true))}");
            output.WriteLine($"{"",-14}{"refit",8}{Stats(Errors(refit, authorL, skin, true))}");
        }
    }

    /// <summary>
    /// What the push-out is pushing against, when it pushes. Each node the push moved is attributed to the nearest
    /// drawn surface — the garment's own retargeted body mesh, or the legs body its hem hangs over — and scored on
    /// whether the push took it towards the author's answer or away from it.
    /// </summary>
    [Fact]
    public void What_the_push_out_pushes_against()
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string fromPath = Path.Combine(ThisOldThing, "neolithe m", model);
        string toPath = Path.Combine(ThisOldThing, "neolithe l", model);
        if (!File.Exists(fromPath) || !File.Exists(toPath) || !Directory.Exists(NeolitheRoot)) return;

        var fromBytes = File.ReadAllBytes(fromPath);
        var authorM = ModelPartReader.Read(fromBytes)!;
        var authorL = ModelPartReader.Read(File.ReadAllBytes(toPath))!;

        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", ChestFolder + @"\SFW M.mdl", ChestFolder + @"\SFW L.mdl");
        AddPair(pairs, "_dwn", LegsFolder + @"\SFW Medium.mdl", LegsFolder + @"\SFW Large.mdl");

        var pushed = ModelPartReader.Read(BodyRetarget.Plan(authorM, fromBytes, pairs, "_top", true).Model)!;
        var still = ModelPartReader.Read(BodyRetarget.Plan(authorM, fromBytes, pairs, "_top", false).Model)!;

        var ownSkin = new BodySurface(still, 0.02f);
        var legs = new BodySurface(pairs[1].Target, 0.02f);

        var tally = new Dictionary<string, (int N, double Better, double Worse, double Push)>();
        var seen = new HashSet<int>();
        foreach (var part in authorM.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (!seen.Add(v)) continue;
                var before = At(still, v);
                var after = At(pushed, v);
                float push = Vector3.Distance(before, after);
                if (push < 1e-6f) continue;

                float dOwn = ownSkin.Nearest(before, 0.05f, out var hOwn) ? hOwn.Distance : float.MaxValue;
                float dLegs = legs.Nearest(before, 0.05f, out var hLegs) ? hLegs.Distance : float.MaxValue;
                string by = dOwn <= dLegs ? "own body mesh" : "legs body";
                bool inside = dOwn <= dLegs
                                  ? Vector3.Dot(before - hOwn.Point, hOwn.Normal) < 0f
                                  : Vector3.Dot(before - hLegs.Point, hLegs.Normal) < 0f;
                by += inside ? " (inside)" : " (spread)";

                var truth = At(authorL, v);
                float gain = Vector3.Distance(before, truth) - Vector3.Distance(after, truth);
                var t = tally.GetValueOrDefault(by);
                tally[by] = (t.N + 1, t.Better + Math.Max(0, gain), t.Worse + Math.Max(0, -gain), t.Push + push);
            }
        }

        output.WriteLine($"{"pushed by",-26}{"nodes",7}{"mean push",11}{"closer",9}{"further",9}   (mm, vs author)");
        foreach (var (by, t) in tally.OrderBy(p => p.Key))
            output.WriteLine($"{by,-26}{t.N,7}{t.Push / t.N * 1000,11:F2}{t.Better / t.N * 1000,9:F2}{t.Worse / t.N * 1000,9:F2}");

        // Were those nodes ALREADY inside the garment's own body mesh as the author shipped it? If so the "clip" is
        // authored — cloth over skin the author left underneath, hidden — and a push-out has no business fixing it.
        var authoredSkin = new BodySurface(authorM, 0.02f);
        int already = 0, wasOutside = 0;
        var authoredDepth = new List<float>();
        seen.Clear();
        foreach (var part in authorM.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (!seen.Add(v)) continue;
                if (Vector3.Distance(At(still, v), At(pushed, v)) < 1e-6f) continue;
                var p0 = At(authorM, v);
                if (!authoredSkin.Nearest(p0, 0.05f, out var h0)) continue;
                float s0 = Vector3.Dot(p0 - h0.Point, h0.Normal);
                if (s0 < 0f) { already++; authoredDepth.Add(-s0); }
                else wasOutside++;
            }
        }
        output.WriteLine("");
        output.WriteLine($"of the pushed nodes, as the AUTHOR shipped M: {already} already inside its own body mesh, " +
                         $"{wasOutside} outside");
        if (authoredDepth.Count > 0) output.WriteLine($"authored depth inside: {Stats(authoredDepth)}");
    }

    /// <summary>
    /// Where the cloth error lives. For each cloth vertex: how far the AUTHOR moved it from M to L, how far the refit
    /// moved it, and how far apart those two answers are — binned by distance from the source body, so a falloff that
    /// gives up too early, and a field sampled in the wrong place, show up as different rows.
    /// </summary>
    [Fact]
    public void Where_the_cloth_error_lives()
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string fromPath = Path.Combine(ThisOldThing, "neolithe m", model);
        string toPath = Path.Combine(ThisOldThing, "neolithe l", model);
        if (!File.Exists(fromPath) || !File.Exists(toPath) || !Directory.Exists(NeolitheRoot)) return;

        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var chest = catalog.For("_top").Where(o => o.Group == "CHEST: SmallClothes").ToList();
        var src = chest.First(o => o.Label == "DEFAULT · SFW M");
        var dst = chest.First(o => o.Label == "DEFAULT · SFW L");

        var fromBytes = File.ReadAllBytes(fromPath);
        var authorM = ModelPartReader.Read(fromBytes)!;
        var authorL = ModelPartReader.Read(File.ReadAllBytes(toPath))!;
        var sourceBytes = File.ReadAllBytes(catalog.PathOf(src));
        var targetBytes = File.ReadAllBytes(catalog.PathOf(dst));
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        IdentityCorrespondence.TryBuild(source, target, "chest", out var built, out _, Uv(sourceBytes), Uv(targetBytes));

        var planned = BodyRetarget.Plan(authorM, fromBytes, [new BodyRetarget.SlotPair("_top", built!, target)], "_top");
        var refit = ModelPartReader.Read(planned.Model)!;
        var surface = new BodySurface(source, 0.02f);

        float[] edges = [0.005f, 0.01f, 0.02f, 0.04f, 0.08f, 0.25f, float.MaxValue];
        var rows = edges.Select(_ => (N: 0, Author: 0.0, Ours: 0.0, Miss: 0.0, Cos: 0.0)).ToArray();

        var seen = new HashSet<int>();
        foreach (var part in authorM.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (!seen.Add(v)) continue;
                var p = At(authorM, v);
                var author = At(authorL, v) - p;
                var ours = At(refit, v) - p;
                float d = surface.Nearest(p, 1f, out var hit) ? hit.Distance : float.MaxValue;
                int bin = Array.FindIndex(edges, e => d < e);

                float cos = author.Length() > 1e-5f && ours.Length() > 1e-5f
                                ? Vector3.Dot(Vector3.Normalize(author), Vector3.Normalize(ours))
                                : 0f;
                var row = rows[bin];
                rows[bin] = (row.N + 1, row.Author + author.Length(), row.Ours + ours.Length(),
                             row.Miss + Vector3.Distance(author, ours), row.Cos + cos);
            }
        }

        output.WriteLine($"{"from body",-12}{"verts",7}{"author moved",14}{"we moved",10}{"disagree",10}{"cos",7}");
        float lo = 0f;
        for (int i = 0; i < edges.Length; i++)
        {
            var row = rows[i];
            string range = edges[i] == float.MaxValue ? $">{lo * 1000:F0}mm" : $"<{edges[i] * 1000:F0}mm";
            lo = edges[i];
            if (row.N == 0) continue;
            output.WriteLine($"{range,-12}{row.N,7}{row.Author / row.N * 1000,14:F2}{row.Ours / row.N * 1000,10:F2}" +
                             $"{row.Miss / row.N * 1000,10:F2}{row.Cos / row.N,7:F2}");
        }

        // ── prototype: a smooth volumetric field instead of nearest-point ──
        //
        // Every body vertex's displacement, averaged with a Gaussian weight on distance. Cloth hanging below the bust
        // then feels the bust in proportion to how close it is, instead of feeling only the (unmoving) belly it
        // happens to be nearest.
        var bodyPos = new List<Vector3>();
        var bodyDelta = new List<Vector3>();
        foreach (int v in surface.SkinVertices)
        {
            bodyPos.Add(At(source, v));
            bodyDelta.Add(built!.Field[v] ?? Vector3.Zero);
        }

        foreach (float sigma in new[] { 0.03f, 0.06f, 0.10f, 0.15f })
        {
            var proto = edges.Select(_ => (N: 0, Miss: 0.0, Cos: 0.0)).ToArray();
            seen.Clear();
            float inv = 1f / (2f * sigma * sigma);
            foreach (var part in authorM.Parts)
            {
                if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                foreach (int v in part.Triangles)
                {
                    if (!seen.Add(v)) continue;
                    var p = At(authorM, v);
                    var author = At(authorL, v) - p;

                    var sum = Vector3.Zero;
                    double wsum = 0;
                    for (int k = 0; k < bodyPos.Count; k++)
                    {
                        float d2 = Vector3.DistanceSquared(p, bodyPos[k]);
                        if (d2 > 9f * sigma * sigma) continue;
                        float w = MathF.Exp(-d2 * inv);
                        sum += bodyDelta[k] * w;
                        wsum += w;
                    }
                    var ours = wsum > 1e-12 ? sum / (float)wsum : Vector3.Zero;

                    float d = surface.Nearest(p, 1f, out var hit) ? hit.Distance : float.MaxValue;
                    int bin = Array.FindIndex(edges, e => d < e);
                    float cos = author.Length() > 1e-5f && ours.Length() > 1e-5f
                                    ? Vector3.Dot(Vector3.Normalize(author), Vector3.Normalize(ours))
                                    : 0f;
                    var row = proto[bin];
                    proto[bin] = (row.N + 1, row.Miss + Vector3.Distance(author, ours), row.Cos + cos);
                }
            }

            output.WriteLine("");
            output.WriteLine($"gaussian field, sigma {sigma * 1000:F0} mm:");
            lo = 0f;
            double total = 0;
            int count = 0;
            for (int i = 0; i < edges.Length; i++)
            {
                var row = proto[i];
                string range = edges[i] == float.MaxValue ? $">{lo * 1000:F0}mm" : $"<{edges[i] * 1000:F0}mm";
                lo = edges[i];
                if (row.N == 0) continue;
                total += row.Miss;
                count += row.N;
                output.WriteLine($"{range,-12}{row.N,7}{"",14}{"",10}{row.Miss / row.N * 1000,10:F2}{row.Cos / row.N,7:F2}");
            }
            output.WriteLine($"{"all cloth",-12}{count,7}{"",24}{total / count * 1000,10:F2}");
        }

        double baseline = rows.Sum(r => r.Miss) / rows.Sum(r => r.N);
        output.WriteLine($"{"nearest-point, all cloth",-36}{baseline * 1000,10:F2}");
    }

    /// <summary>
    /// Prototype for the detector: score candidates only over the probes where the candidates actually DIFFER.
    /// <para/>
    /// Most of a chest model is identical across all 114 options — arms, back, shoulders — so a hit rate or an RMS over
    /// every probe is dominated by points that cannot tell one size from another, and every candidate scores about the
    /// same. Keeping only probes whose distance varies across candidates leaves the region that decides it.
    /// </summary>
    [Theory]
    [InlineData("neolithe xs")]
    [InlineData("neolithe s")]
    [InlineData("neolithe m")]
    [InlineData("neolithe l")]
    public void Detect_over_the_discriminating_region(string size)
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string path = Path.Combine(ThisOldThing, size, model);
        if (!File.Exists(path) || !Directory.Exists(NeolitheRoot)) return;

        var garment = ModelPartReader.Read(File.ReadAllBytes(path))!;
        var catalog = BodySizeCatalog.Read(NeolitheRoot);
        var candidates = catalog.For("_top").Where(o => o.Group == "CHEST: SmallClothes").ToList();

        // Probes: the garment's body-mesh points, welded.
        var probes = new List<Vector3>();
        var seenKey = new HashSet<(int, int, int)>();
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                var p = At(garment, v);
                if (seenKey.Add(MeshMath.PositionKey(BodyRetarget.ToVec(p), 1e4f))) probes.Add(p);
            }
        }

        // Distance from every probe to every (geometrically distinct) candidate.
        var names = new List<string>();
        var dist = new List<float[]>();
        var seenGeometry = new HashSet<string>();
        foreach (var option in candidates)
        {
            var body = ModelPartReader.Read(File.ReadAllBytes(catalog.PathOf(option)))!;
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(body.Positions)));
            if (!seenGeometry.Add(key)) continue;
            var surface = new BodySurface(body, 0.01f);
            var d = new float[probes.Count];
            for (int i = 0; i < probes.Count; i++)
                d[i] = surface.Nearest(probes[i], 0.05f, out var hit) ? hit.Distance : 0.05f;
            names.Add(option.Label);
            dist.Add(d);
        }

        // Discriminating probes: where the candidates disagree by more than half a millimetre.
        var keep = new List<int>();
        for (int i = 0; i < probes.Count; i++)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var d in dist) { lo = MathF.Min(lo, d[i]); hi = MathF.Max(hi, d[i]); }
            if (hi - lo > 0.0005f) keep.Add(i);
        }

        var scored = names.Select((name, c) =>
        {
            double sum = 0;
            foreach (int i in keep) sum += dist[c][i] * dist[c][i];
            return (Name: name, Rms: keep.Count > 0 ? (float)Math.Sqrt(sum / keep.Count) : 0f);
        }).OrderBy(s => s.Rms).ToList();

        output.WriteLine($"{size}: {probes.Count} probes, {keep.Count} discriminating ({(float)keep.Count / probes.Count:P0})");
        foreach (var s in scored.Take(6))
            output.WriteLine($"  {s.Rms * 1000f,7:F3} mm  {s.Name}");
    }

    /// <summary>
    /// Prototype: read the hip size from the CLOTH, for a garment whose body mesh never reaches the legs. An author fits
    /// a hem to sit just outside the hips it was made for, so against too large a body the cloth goes inside it, and
    /// against too small a body it floats clear. The authored size should be the tightest body the cloth still clears.
    /// </summary>
    [Theory]
    [InlineData("neolithe xs")]
    [InlineData("neolithe s")]
    [InlineData("neolithe m")]
    [InlineData("neolithe l")]
    public void Read_the_hips_from_the_cloth(string size)
    {
        const string model = @"chara\equipment\e6255\model\c0201e6255_top.mdl";
        string path = Path.Combine(ThisOldThing, size, model);
        if (!File.Exists(path) || !Directory.Exists(NeolitheRoot)) return;

        var garment = ModelPartReader.Read(File.ReadAllBytes(path))!;
        var cloth = new List<Vector3>();
        var seen = new HashSet<int>();
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
                if (seen.Add(v)) cloth.Add(At(garment, v));
        }

        output.WriteLine($"{size}: {cloth.Count} cloth vertices");
        output.WriteLine($"  {"legs",-14}{"near",7}{"inside",8}{"deep>2mm",10}{"mean gap",10}");
        foreach (string legs in new[] { "SFW Small", "SFW Medium", "SFW Large" })
        {
            var body = ModelPartReader.Read(File.ReadAllBytes(LegsFolder + $@"\{legs}.mdl"))!;
            var surface = new BodySurface(body, 0.01f);
            int near = 0, inside = 0, deep = 0;
            double gap = 0;
            foreach (var p in cloth)
            {
                if (!surface.Nearest(p, 0.03f, out var hit)) continue;
                near++;
                float s = Vector3.Dot(p - hit.Point, hit.Normal);
                if (s < 0f) inside++;
                if (s < -0.002f) deep++;
                if (s >= 0f) gap += s;
            }
            int outside = near - inside;
            output.WriteLine($"  {legs,-14}{near,7}{inside,8}{deep,10}{(outside > 0 ? gap / outside * 1000 : 0),10:F2}");
        }
    }

    private static string Top(BodySizeMatch.Ranking ranking)
        => ranking.Best is { } b ? $"{b.Option.FullLabel} ({b.HitRate:P1})" : "nothing";

    /// <summary>Per-vertex distance from <paramref name="model"/> to <paramref name="truth"/>, over one set.</summary>
    private static List<float> Errors(ModelParts model, ModelParts truth, bool skin, bool sameNumbering)
    {
        var errors = new List<float>();
        var truthSurface = sameNumbering ? null : new AnySurface(truth, skin);
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material) != skin) continue;
            foreach (int v in part.Triangles.Distinct())
            {
                var p = At(model, v);
                errors.Add(sameNumbering ? Vector3.Distance(p, At(truth, v)) : truthSurface!.Distance(p));
            }
        }
        return errors;
    }

    private static string Stats(List<float> e)
    {
        if (e.Count == 0) return "   (none)";
        e.Sort();
        float mean = e.Average();
        float p95 = e[(int)(e.Count * 0.95f)];
        return $"{mean * 1000f,9:F2}{p95 * 1000f,9:F2}{e[^1] * 1000f,9:F2}";
    }

    private static Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>Nearest-point distance to a model's parts of one kind, brute force: only a diag, only once.</summary>
    private sealed class AnySurface(ModelParts m, bool skin)
    {
        private readonly List<(Vector3 A, Vector3 B, Vector3 C)> tris = m.Parts
            .Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material) == skin)
            .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                       .Select(t => (At(m, p.Triangles[t * 3]), At(m, p.Triangles[t * 3 + 1]),
                                                     At(m, p.Triangles[t * 3 + 2]))))
            .ToList();

        public float Distance(Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var (a, b, c) in tris)
            {
                var q = BrushTransfer.ClosestOnTriangle(p, a, b, c, out _, out _, out _);
                best = MathF.Min(best, Vector3.DistanceSquared(p, q));
            }
            return MathF.Sqrt(best);
        }
    }

    [Fact]
    public void Retarget_a_garment_between_two_Neolithe_sizes()
    {
        // Point PROTEUS_RETARGET_GARMENT at a .mdl authored for Neolithe to exercise the whole solve on real
        // geometry and drop .obj files on the Desktop to look at.
        string? garmentPath = Environment.GetEnvironmentVariable("PROTEUS_RETARGET_GARMENT");
        if (string.IsNullOrWhiteSpace(garmentPath) || !File.Exists(garmentPath)) return;
        if (!File.Exists(ChestFolder + @"\SFW M.mdl")) return;

        var garmentBytes = File.ReadAllBytes(garmentPath);
        var garment = ModelPartReader.Read(garmentBytes);
        Assert.NotNull(garment);

        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", ChestFolder + @"\SFW M.mdl", ChestFolder + @"\SFW L.mdl");
        AddPair(pairs, "_dwn", LegsFolder + @"\SFW Medium.mdl", LegsFolder + @"\SFW Large.mdl");

        var planned = BodyRetarget.Plan(garment!, garmentBytes, pairs, "_top");
        var r = planned.Report;

        output.WriteLine($"garment      {Path.GetFileName(garmentPath)}");
        output.WriteLine($"vertices     {r.Nodes:N0}");
        output.WriteLine($"snapped      {r.Snapped:N0} ({r.SnapRate:P0} of the {r.Transferred:N0} moved)");
        output.WriteLine($"missed       {r.Missed:N0}");
        output.WriteLine($"pushed out   {r.Pushed:N0}, worst {r.WorstPush * 1000f:F2} mm");
        output.WriteLine($"worst move   {r.WorstMove * 1000f:F2} mm");
        output.WriteLine($"spares       {r.UnmappedSpares:N0}   other LODs: {r.HasOtherLods}");
        output.WriteLine($"bytes        {garmentBytes.Length:N0} -> {planned.Model.Length:N0}");

        Assert.Equal(garmentBytes.Length, planned.Model.Length);

        // Clearance histogram before and after, so the push-out's effect and the Clearance constant are numbers
        // rather than a guess.
        var targetTop = Read(ChestFolder + @"\SFW L.mdl");
        var targetDwn = Read(LegsFolder + @"\SFW Large.mdl");
        var before = ModelPartReader.Read(garmentBytes)!;
        var after = ModelPartReader.Read(planned.Model)!;
        output.WriteLine("");
        output.WriteLine("distance from the TARGET body, cloth vertices only:");
        Histogram("before", before, [targetTop, targetDwn]);
        Histogram("after ", after, [targetTop, targetDwn]);

        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                      "OneDrive", "Desktop");
        if (!Directory.Exists(desktop)) return;
        File.WriteAllBytes(Path.Combine(desktop, "retarget_after.mdl"), planned.Model);
        output.WriteLine($"wrote {Path.Combine(desktop, "retarget_after.mdl")}");
    }

    private void Histogram(string label, ModelParts garment, IReadOnlyList<ModelParts> bodies)
    {
        var surfaces = bodies.Select(b => new BodySurface(b, 0.02f)).ToArray();
        int[] buckets = new int[6];   // inside, <0.5mm, <2mm, <10mm, <30mm, beyond
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles.Distinct())
            {
                var p = new Vector3(garment.Positions[v * 3], garment.Positions[v * 3 + 1],
                                    garment.Positions[v * 3 + 2]);
                float best = float.MaxValue;
                foreach (var s in surfaces)
                    if (s.Nearest(p, 0.03f, out var hit)) best = MathF.Min(best, hit.Distance);

                buckets[best == float.MaxValue ? 5
                      : best < 0.0005f ? 1
                      : best < 0.002f ? 2
                      : best < 0.01f ? 3
                      : 4]++;
            }
        }
        output.WriteLine($"  {label}  <0.5mm {buckets[1],6}  <2mm {buckets[2],6}  <10mm {buckets[3],6}  " +
                         $"<30mm {buckets[4],6}  beyond {buckets[5],6}");
    }

    private const string RueRoot = @"E:\Penumbradt\hs-Rue+-2.2.7-y0f";

    /// <summary>
    /// Reported in game: Seaside's Rue Med top refitted onto Yiggle - Small clips badly. Measures how deep the cloth
    /// sits inside each body — the author's top on the sizes it might have been made for, then each refit on Yiggle -
    /// Small, then the file the save actually wrote.
    /// </summary>
    [Fact]
    public void Seaside_rue_medium_to_yiggle_small()
    {
        string topPath = Path.Combine(Seaside, @"top size\rue med\chara\equipment\e0194\model\c0201e0194_top.mdl");
        string saved = Path.Combine(Seaside, @"Body Retarget\[HS] Rue+ — None · Yiggle - Small\chara\equipment\e0194\model\c0201e0194_top.mdl");
        if (!File.Exists(topPath) || !Directory.Exists(RueRoot)) return;

        var bytes = File.ReadAllBytes(topPath);
        var garment = ModelPartReader.Read(bytes)!;
        var catalog = BodySizeCatalog.Read(RueRoot);
        var chest = catalog.For("_top");
        output.WriteLine($"Rue chest options: {string.Join(", ", chest.Select(o => o.Label))}");

        var rank = BodySizeMatch.Rank(garment, chest, catalog.PathOf);
        var rigged = BodySizeMatch.Rank(garment, chest, catalog.PathOf,
                                        new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal));
        output.WriteLine($"with its rig, reads as: {rigged.Best?.Option.Label}");
        output.WriteLine($"reads as {rank.Confidence} (cloth {rank.FromCloth}): " +
                         string.Join(" | ", rank.Scores.Take(6).Select(s => $"{s.Option.Label} {s.Rms * 1000:F2}mm {s.HitRate:P0}")));

        BodyOption Option(string name) => chest.First(o => o.Name == name);
        ModelParts Body(string name) => Read(catalog.PathOf(Option(name)));

        // The skeleton each is rigged to: a refit keeps the garment's weights, so bones the new body uses and the
        // garment does not are motion the garment cannot follow.
        string[] Bones(string path) => ModelSkinReader.Read(File.ReadAllBytes(path), null, null)?.BoneNames ?? [];
        var garmentBones = Bones(topPath).ToHashSet();
        foreach (string name in new[] { "Medium", "Yiggle - Medium", "Yiggle - Small" })
        {
            var bones = Bones(catalog.PathOf(Option(name))).ToHashSet();
            output.WriteLine($"{name}: {bones.Count} bones; not in the garment: " +
                             string.Join(", ", bones.Except(garmentBones).OrderBy(b => b)) +
                             "; in the garment only: " + string.Join(", ", garmentBones.Except(bones).OrderBy(b => b)));
        }

        output.WriteLine("");
        output.WriteLine("the author's top, cloth inside each body:");
        foreach (string name in new[] { "Medium", "Yiggle - Medium", "Small", "Yiggle - Small" })
            Clipping(name, garment, Body(name));
        Clipping("its own skin (what is drawn)", garment, garment);
        SkinThroughCloth("author, skin through cloth", garment);

        var target = Body("Yiggle - Small");
        foreach (string from in new[] { "Medium", "Yiggle - Medium" })
        foreach (bool replace in new[] { true, false })
        {
            var pairs = new List<BodyRetarget.SlotPair>();
            AddPair(pairs, "_top", catalog.PathOf(Option(from)), catalog.PathOf(Option("Yiggle - Small")), swapSkin: true);
            var planned = BodyRetarget.Plan(garment, bytes, pairs, "_top", replaceSkin: replace);
            var r = planned.Report;
            output.WriteLine("");
            output.WriteLine($"{from} -> Yiggle - Small, replace {replace} ({pairs[0].Correspondence.GetType().Name}): " +
                             $"moved up to {r.WorstMove * 1000:F1} mm, snapped {r.Snapped:N0}, laid {r.Laid:N0}, " +
                             $"pushed {r.Pushed:N0} (worst {r.WorstPush * 1000:F2} mm), missed {r.Missed:N0}");
            if (r.Swap is { } sw)
                output.WriteLine($"  swap: garment skin out ({sw.Removed:N0} triangles), body skin in ({sw.Added:N0}), meshes kept {sw.Kept}, shapes lost {sw.LostShapes}");
            var refit = ModelPartReader.Read(planned.Model)!;
            Clipping("refit on Yiggle - Small", refit, target);
            if (replace && from == "Yiggle - Medium")
                ToeCapDiagTests.WriteObj(planned.Model, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                                                     @"OneDrive\Desktop\Seaside Rue to Yiggle\swapped top (Yiggle Small).obj"));
            Clipping("refit on its own skin", refit, refit);
            if (!replace) ClothSinking("cloth against the skin drawn", garment, refit, garment, target);
            SkinThroughCloth("refit, skin through cloth", refit);
        }

        if (File.Exists(saved))
        {
            output.WriteLine("");
            var savedModel = Read(saved);
            Clipping("the saved file on Yiggle - Small", savedModel, target);
            Clipping("the saved file on its own skin", savedModel, savedModel);
            SkinThroughCloth("the saved file, skin through cloth", savedModel);

            // For the eye: the four in one folder, all in the same space, to load together.
            string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                       @"OneDrive\Desktop\Seaside Rue to Yiggle");
            Directory.CreateDirectory(desk);
            ToeCapDiagTests.WriteObj(bytes, Path.Combine(desk, "author top (Rue Med).obj"));
            ToeCapDiagTests.WriteObj(File.ReadAllBytes(saved), Path.Combine(desk, "refit top (Yiggle Small).obj"));
            ToeCapDiagTests.WriteObj(File.ReadAllBytes(catalog.PathOf(Option("Yiggle - Medium"))),
                                     Path.Combine(desk, "body Yiggle Medium.obj"));
            ToeCapDiagTests.WriteObj(File.ReadAllBytes(catalog.PathOf(Option("Yiggle - Small"))),
                                     Path.Combine(desk, "body Yiggle Small.obj"));
            output.WriteLine($"wrote {desk}");
        }
    }

    /// <summary>
    /// Cloth vertices behind the body's surface, by depth: signed along the body normal at the nearest body point,
    /// within 30 mm. Per part, so the clip can be placed.
    /// </summary>
    private void Clipping(string label, ModelParts cloth, ModelParts body)
    {
        var surface = new BodySurface(body, 0.01f);
        var perPart = new List<string>();
        int total = 0, deep = 0;
        float worst = 0f;
        foreach (var part in cloth.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            int inside = 0;
            float partWorst = 0f;
            foreach (int v in part.Triangles.Distinct())
            {
                var p = At(cloth, v);
                if (!surface.Nearest(p, 0.03f, out var hit)) continue;
                float signed = Vector3.Dot(p - hit.Point, hit.Normal);
                if (signed > -0.0005f) continue;
                inside++;
                if (signed < -0.002f) deep++;
                partWorst = MathF.Max(partWorst, -signed);
            }
            total += inside;
            worst = MathF.Max(worst, partWorst);
            if (inside > 0) perPart.Add($"{part.Label} {inside} (to {partWorst * 1000:F1})");
        }
        output.WriteLine($"  {label,-34} inside >0.5mm {total,5}, >2mm {deep,5}, deepest {worst * 1000:F1} mm   {string.Join("; ", perPart)}");
    }

    /// <summary>
    /// Which bones the breasts are weighted to, skin and cloth separately, in each model involved: the outfit as
    /// shipped, the refit as saved, the Rue bodies, and the Neolithe body the character actually wears. Skin and cloth
    /// weighted to different bones deform differently under anything that moves those bones — a bust slider, a scaling
    /// profile — even standing still.
    /// </summary>
    [Fact]
    public void Seaside_breast_weights()
    {
        string topPath = Path.Combine(Seaside, @"top size\rue med\chara\equipment\e0194\model\c0201e0194_top.mdl");
        string saved = Path.Combine(Seaside, @"Body Retarget\[HS] Rue+ — None · Yiggle - Small\chara\equipment\e0194\model\c0201e0194_top.mdl");
        if (!File.Exists(topPath) || !Directory.Exists(RueRoot) || !Directory.Exists(NeolitheRoot)) return;

        var rue = BodySizeCatalog.Read(RueRoot);
        var neo = BodySizeCatalog.Read(NeolitheRoot);
        string RuePath(string name) => rue.PathOf(rue.For("_top").First(o => o.Name == name));
        string NeoPath(string label) => neo.PathOf(neo.For("_top").First(o => o.Label == label));

        void Report(string label, string path)
        {
            if (!File.Exists(path)) return;
            var m = ModelSkinReader.Read(File.ReadAllBytes(path), null, null);
            if (m == null) { output.WriteLine($"{label}: unreadable"); return; }

            // Per vertex, skin or cloth by the material of any triangle using it.
            var skin = new bool?[m.VertexCount];
            for (int t = 0; t < m.TriangleCount; t++)
            {
                bool isSkin = SecondSkinWriter.IsBodySkinMaterial(m.MaterialNames[m.TriangleMaterials[t]]);
                for (int c = 0; c < 3; c++) skin[m.Triangles[t * 3 + c]] ??= isSkin;
            }

            foreach (bool wantSkin in new[] { true, false })
            {
                var byBone = new Dictionary<string, float>(StringComparer.Ordinal);
                int count = 0;
                for (int v = 0; v < m.VertexCount; v++)
                {
                    if (skin[v] != wantSkin) continue;
                    var p = m.Positions[v];
                    if (p.Y < 1.12f || p.Y > 1.28f || MathF.Abs(p.X) < 0.03f || p.Z < 0.08f) continue;   // the breasts' fronts
                    count++;
                    for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                    {
                        float w = m.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                        if (w <= 0f) continue;
                        string bone = m.BoneNames[m.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]];
                        byBone[bone] = byBone.GetValueOrDefault(bone) + w;
                    }
                }
                if (count == 0) continue;
                output.WriteLine($"{label,-28} {(wantSkin ? "skin " : "cloth")} {count,5} verts: " +
                                 string.Join(", ", byBone.OrderByDescending(b => b.Value).Take(5)
                                                         .Select(b => $"{b.Key} {b.Value / count:P0}")));
            }
        }

        Report("Seaside Rue Med (shipped)", topPath);
        Report("refit as saved", saved);
        Report("Rue Medium body", RuePath("Medium"));
        Report("Rue Small body", RuePath("Small"));
        Report("Rue Yiggle - Small body", RuePath("Yiggle - Small"));
        Report("Neolithe Almond XS (worn)", NeoPath("DEFAULT ALMOND · SFW Almond XS"));
    }

    /// <summary>
    /// Per cloth vertex, how its distance off its own skin changed from the author's model to the refit — signed along
    /// the skin normal, so NEGATIVE means the cloth moved in toward (or into) the skin. The two models share vertex
    /// numbering (the refit only moves vertices), so the comparison is index for index. Bucketed, with the worst
    /// vertices placed by part and height so they can be found on the model.
    /// </summary>
    private void ClothSinking(string label, ModelParts author, ModelParts refit, ModelParts authorSkin, ModelParts refitSkin)
    {
        var before = new BodySurface(authorSkin, 0.01f);
        var after = new BodySurface(refitSkin, 0.01f);
        var sinks = new List<(float Change, float Before, float After, string Part, Vector3 At)>();
        foreach (var part in author.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles.Distinct())
            {
                var p0 = At(author, v);
                var p1 = At(refit, v);
                if (!before.Nearest(p0, 0.05f, out var h0) || !after.Nearest(p1, 0.05f, out var h1)) continue;
                float d0 = Vector3.Dot(p0 - h0.Point, h0.Normal), d1 = Vector3.Dot(p1 - h1.Point, h1.Normal);
                sinks.Add((d1 - d0, d0, d1, part.Label, p1));
            }
        }
        int[] edges = [-10, -5, -2, -1, 1, 2, 5, 10];
        var counts = new int[edges.Length + 1];
        foreach (var s in sinks)
        {
            float mm = s.Change * 1000f;
            int b = 0;
            while (b < edges.Length && mm >= edges[b]) b++;
            counts[b]++;
        }
        output.WriteLine($"  {label}: change in distance off the skin, mm (negative = moved IN), {sinks.Count} cloth vertices");
        output.WriteLine("    <-10 " + counts[0] + " | -10..-5 " + counts[1] + " | -5..-2 " + counts[2] + " | -2..-1 " + counts[3]
                       + " | within 1 " + counts[4] + " | 1..2 " + counts[5] + " | 2..5 " + counts[6] + " | 5..10 " + counts[7]
                       + " | >10 " + counts[8]);
        foreach (var group in sinks.GroupBy(s => s.Part))
            foreach (var s in group.OrderBy(s => s.Change).Take(6))
                output.WriteLine($"    part {s.Part}: {s.Before * 1000:F1} -> {s.After * 1000:F1} mm off the skin, at height {s.At.Y:F3} x {s.At.X:F3} z {s.At.Z:F3}");
        // Where the cloth ends up, for the vertices that moved in: still clear of the skin, or behind it.
        var movedIn = sinks.Where(s => s.Change < -0.001f).ToList();
        output.WriteLine($"    of the {movedIn.Count} that moved in over 1 mm: {movedIn.Count(s => s.After < 0f)} now behind the skin "
                       + $"({movedIn.Count(s => s.Before >= 0f && s.After < 0f)} were in front of it before), "
                       + $"{movedIn.Count(s => s.After >= 0f && s.After < 0.002f)} within 2 mm of it");
        // What holds a span over the cleavage up: the front of each breast. How far forward it sits before and after,
        // against how far forward the span itself sits.
        float Front(ModelParts m, bool left)
            => m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                      .SelectMany(p => p.Triangles).Distinct().Select(v => At(m, v))
                      .Where(q => q.Y > 1.10f && q.Y < 1.30f && (left ? q.X > 0.02f : q.X < -0.02f))
                      .Select(q => q.Z).DefaultIfEmpty(float.NaN).Max();
        float SpanFront(ModelParts m) => author.Parts.Where(p => p.Label == "2.2").SelectMany(p => p.Triangles).Distinct()
                                               .Select(v => At(m, v).Z).DefaultIfEmpty(float.NaN).Max();
        output.WriteLine($"    breast fronts z: left {Front(authorSkin, true) * 1000:F1} -> {Front(refitSkin, true) * 1000:F1}, "
                       + $"right {Front(authorSkin, false) * 1000:F1} -> {Front(refitSkin, false) * 1000:F1} mm; "
                       + $"part 2.2's front {SpanFront(author) * 1000:F1} -> {SpanFront(refit) * 1000:F1} mm");
        var byPart = sinks.Where(s => s.Change < -0.002f).GroupBy(s => s.Part)
                          .Select(g => $"{g.Key} {g.Count()} (worst {g.Min(s => s.Change) * 1000:F1})");
        output.WriteLine("    moved in more than 2 mm, by part: " + string.Join("; ", byPart));
    }

    /// <summary>
    /// The other way round from <see cref="Clipping"/>: the garment's own skin poking out through its cloth. A skin
    /// vertex can come through between cloth vertices, where no cloth vertex is behind anything, so this measures skin
    /// vertices against the cloth SURFACE — nearest cloth triangle within 10 mm, signed along that triangle's face
    /// normal (outward when the cloth is wound the usual way). Per cloth part, so the clip can be placed.
    /// </summary>
    private void SkinThroughCloth(string label, ModelParts m)
    {
        var clothTris = new List<(int A, int B, int C, string Part)>();
        foreach (var part in m.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                clothTris.Add((part.Triangles[t], part.Triangles[t + 1], part.Triangles[t + 2], part.Label));
        }

        // A coarse grid over cloth triangle centres; a triangle is filed in every cell its box touches.
        const float cell = 0.01f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (int i = 0; i < clothTris.Count; i++)
        {
            var (a, b, c, _) = clothTris[i];
            var lo = Vector3.Min(At(m, a), Vector3.Min(At(m, b), At(m, c)));
            var hi = Vector3.Max(At(m, a), Vector3.Max(At(m, b), At(m, c)));
            var (x0, y0, z0) = Key(lo);
            var (x1, y1, z1) = Key(hi);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = [];
                list.Add(i);
            }
        }

        var skinVerts = m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct().ToList();
        var byPart = new Dictionary<string, (int Count, float Worst)>();
        int covered = 0, through = 0;
        foreach (int v in skinVerts)
        {
            var p = At(m, v);
            var (kx, ky, kz) = Key(p);
            float best = 0.01f;
            int bestTri = -1;
            Vector3 bestAt = default;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                foreach (int i in list)
                {
                    var (a, b, c, _) = clothTris[i];
                    var q = BrushTransfer.ClosestOnTriangle(p, At(m, a), At(m, b), At(m, c), out _, out _, out _);
                    float d = Vector3.Distance(p, q);
                    if (d >= best) continue;
                    best = d;
                    bestTri = i;
                    bestAt = q;
                }
            }
            if (bestTri < 0) continue;
            covered++;

            var (ta, tb, tc, partLabel) = clothTris[bestTri];
            var n = Vector3.Cross(At(m, tb) - At(m, ta), At(m, tc) - At(m, ta));
            if (n.LengthSquared() < 1e-20f) continue;
            float signed = Vector3.Dot(p - bestAt, Vector3.Normalize(n));
            if (signed <= 0.0002f) continue;
            through++;
            var (count, worst) = byPart.GetValueOrDefault(partLabel);
            byPart[partLabel] = (count + 1, MathF.Max(worst, signed));
        }

        output.WriteLine($"  {label,-34} skin under cloth {covered,5}, in front of it >0.2mm {through,5}   " +
                         string.Join("; ", byPart.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Count} (to {k.Value.Worst * 1000:F1})")));
    }

    /// <param name="swapSkin">Hand the pair the target body's file, so a refit with replaceSkin swaps the garment's skin
    /// meshes for the body's. Off by default: the swap rebuilds the model and renumbers its vertices, and most of these
    /// diagnostics compare a refit with the author's model vertex for vertex.</param>
    private const string YabRoot = @"E:\Penumbradt\hs-Yet Another Body+-4.2.0-tmi";

    /// <summary>
    /// Reported: a YAB garment made for Small detected as "exactly Yiggle - Large". Elegy ships its bra hand-made for
    /// YAB S, M and L, so each can be ranked against YAB's chest options, the way the panel ranks it (with its rig).
    /// </summary>
    [Fact]
    public void Detect_each_Yab_size()
    {
        const string elegy = @"E:\Penumbradt\Elegy - Sylvie Bra & Panties (RueYABUranusRe)\smallclothes - bra shape";
        if (!Directory.Exists(elegy) || !Directory.Exists(YabRoot)) return;
        var yab = BodySizeCatalog.Read(YabRoot);
        var chest = yab.For("_top");

        foreach (string size in new[] { "yab - s", "yab - m", "yab - l" })
        {
            string path = Path.Combine(elegy, size, @"chara\equipment\e0000\model\c0201e0000_top.mdl");
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var garment = ModelPartReader.Read(bytes)!;
            var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
            var rank = BodySizeMatch.Rank(garment, chest, yab.PathOf, bones);
            output.WriteLine($"{size}: {rank.Confidence} (cloth {rank.FromCloth}) — " +
                             string.Join(" | ", rank.Scores.Take(6).Select(s => $"{s.Option.Label} {s.Rms * 1000:F2}mm {s.HitRate:P0}")));
        }
    }

    /// <summary>
    /// A refit from one body MOD to another, scored against the author's own hand-made version: Seaside's halter was
    /// made for Neolithe XS and, separately, for Rue (Yiggle-rigged) Med. Refit the Neolithe top onto Rue Yiggle Medium
    /// and compare with the author's Rue top; then the other way. The two versions' cloth does not share a vertex
    /// numbering, so each refit cloth vertex is paired with the author's at the same texture coordinate.
    /// </summary>
    [Fact]
    public void Between_bodies_against_the_author()
    {
        string neoTop = Path.Combine(Seaside, @"top size\neolithe xs\chara\equipment\e0194\model\c0201e0194_top.mdl");
        string rueTop = Path.Combine(Seaside, @"top size\rue med\chara\equipment\e0194\model\c0201e0194_top.mdl");
        if (!File.Exists(neoTop) || !File.Exists(rueTop) || !Directory.Exists(RueRoot) || !Directory.Exists(NeolitheRoot))
            return;

        var neo = BodySizeCatalog.Read(NeolitheRoot);
        var rue = BodySizeCatalog.Read(RueRoot);
        string neoBody = neo.PathOf(neo.For("_top").First(o => o.Label == "DEFAULT ALMOND · SFW Almond XS"));
        string rueBody = rue.PathOf(rue.For("_top").First(o => o.Name == "Yiggle - Medium"));

        void Run(string label, string garmentPath, string sourceBody, string targetBody, string truthPath)
        {
            var bytes = File.ReadAllBytes(garmentPath);
            var garment = ModelPartReader.Read(bytes)!;
            var pairs = new List<BodyRetarget.SlotPair>();
            AddPair(pairs, "_top", sourceBody, targetBody, swapSkin: true, crossBody: true);
            var planned = BodyRetarget.Plan(garment, bytes, pairs, "_top", replaceSkin: true);

            output.WriteLine("");
            output.WriteLine($"{label} ({pairs[0].Correspondence.Describe()})");
            output.WriteLine("  " + Describe(planned.Report));
            var truth = File.ReadAllBytes(truthPath);
            output.WriteLine("  nothing  " + ClothAgainst(bytes, truth));
            output.WriteLine("  refit    " + ClothAgainst(planned.Model, truth));
        }

        Run("Neolithe XS -> Rue Yiggle Medium", neoTop, neoBody, rueBody, rueTop);
        Run("Rue Yiggle Medium -> Neolithe XS", rueTop, rueBody, neoBody, neoTop);
    }

    private const string TbseRoot = @"E:\Penumbradt\The Body SE";
    private const string TbseXRoot = @"E:\Penumbradt\The Body SE-X IVCS2 V1_1_0";
    private const string Ruffles = @"E:\Penumbradt\Ruffles - by Solona\male size";

    /// <summary>
    /// Between two MEN's bodies: Ruffles' TBSE tops refitted onto TBSE-X and scored against the author's own TBSE-X
    /// versions. Both bodies draw their skin in The Body's layout, so they pair by texture coordinate with no map. The
    /// author's TBSE-X tops do not use TBSE-X's IVCS bones, so only the fit is comparable; the weight columns show the
    /// refit handing the chest to the new body's pec bones where the author did not.
    /// </summary>
    [Fact]
    public void Between_men_s_bodies_against_the_author()
    {
        if (!Directory.Exists(TbseRoot) || !Directory.Exists(TbseXRoot) || !Directory.Exists(Ruffles)) return;

        var tbse = BodySizeCatalog.Read(TbseRoot);
        var tbseX = BodySizeCatalog.Read(TbseXRoot);
        string tbseBody = tbse.PathOf(tbse.For("_top", "0101").Single());
        string tbseXBody = tbseX.PathOf(tbseX.For("_top", "0101").Single());
        const string top = @"chara\equipment\e6010\model\c0101e6010_top.mdl";

        foreach (var (from, truth) in new[] { ("tbse open", "tbse-x open"), ("tbse tied", "tbse-x tied") })
        {
            var bytes = File.ReadAllBytes(Path.Combine(Ruffles, from, top));
            var garment = ModelPartReader.Read(bytes)!;
            var pairs = new List<BodyRetarget.SlotPair>();
            AddPair(pairs, "_top", tbseBody, tbseXBody, swapSkin: true, crossBody: true);
            var planned = BodyRetarget.Plan(garment, bytes, pairs, "_top", replaceSkin: true, acrossBodies: true);

            output.WriteLine("");
            output.WriteLine($"Ruffles {from} -> TBSE-X ({pairs[0].Correspondence.Describe()})");
            output.WriteLine("  " + Describe(planned.Report));
            var author = File.ReadAllBytes(Path.Combine(Ruffles, truth, top));
            output.WriteLine("  nothing  " + ClothAgainst(bytes, author));
            output.WriteLine("  refit    " + ClothAgainst(planned.Model, author));
            output.WriteLine("  " + IvcsShare(bytes, planned.Model));
        }
    }

    /// <summary>
    /// How much of the refit's cloth weight went to TBSE-X's IVCS bones, and how far the rest moved from the garment's
    /// own weights once that is set aside — which says whether the refit mostly ADDED the new body's physics bones or
    /// also rewrote the author's weighting of the bones both bodies share. Cloth vertices pair by index: a refit keeps
    /// every cloth mesh and its vertex order.
    /// </summary>
    private static string IvcsShare(byte[] before, byte[] after)
    {
        var (_, _, was) = ClothVertices(before);
        var (_, _, now) = ClothVertices(after);
        int n = Math.Min(was.Count, now.Count);
        double ivcs = 0, total = 0, rest = 0;
        for (int i = 0; i < n; i++)
        {
            ivcs += now[i].Where(w => w.Bone.StartsWith("iv_", StringComparison.Ordinal)).Sum(w => w.W);
            total += now[i].Sum(w => w.W);

            // Both sides without IVCS, renormalised, then half the L1 distance: the share on a different bone.
            var a = Strip(was[i]);
            var b = Strip(now[i]);
            rest += a.Keys.Union(b.Keys).Sum(k => Math.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k))) / 2;
        }
        return $"on IVCS bones {ivcs / Math.Max(total, 1e-9):P1}; the other bones differ {rest / Math.Max(n, 1):P1} mean";

        static Dictionary<string, float> Strip(List<(string Bone, float W)> w)
        {
            var kept = w.Where(x => !x.Bone.StartsWith("iv_", StringComparison.Ordinal)).ToList();
            float sum = kept.Sum(x => x.W);
            var d = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (bone, weight) in kept) d[bone] = d.GetValueOrDefault(bone) + (sum > 0 ? weight / sum : 0);
            return d;
        }
    }

    private const string QueenAnne = @"E:\Penumbradt\Queen Anne - by Solona";

    /// <summary>
    /// Queen Anne's dress — skirt bones, 3.7 MB — refitted from Neolithe onto Rue, timed: the weight rewrite on its own,
    /// then the whole refit. A refit of it took minutes in game.
    /// </summary>
    [Fact]
    public void Time_a_skirted_dress_between_bodies()
    {
        string dress = Path.Combine(QueenAnne, "size", "neolithe s", @"chara\equipment\e0141\model\c0201e0141_top.mdl");
        if (!File.Exists(dress) || !Directory.Exists(RueRoot) || !Directory.Exists(NeolitheRoot)) return;

        var neo = BodySizeCatalog.Read(NeolitheRoot);
        var rue = BodySizeCatalog.Read(RueRoot);
        string neoBody = neo.PathOf(neo.For("_top").First(o => o.Label == "DEFAULT ALMOND · SFW Almond S"));
        string rueBody = rue.PathOf(rue.For("_top").First(o => o.Name == "Yiggle - Medium"));

        var bytes = File.ReadAllBytes(dress);
        var garment = ModelPartReader.Read(bytes)!;
        var pairs = new List<BodyRetarget.SlotPair>();
        AddPair(pairs, "_top", neoBody, rueBody, swapSkin: true, crossBody: true);
        output.WriteLine($"dress: {garment.Positions.Length / 3:N0} vertices, {garment.Parts.Count} parts");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var weights = BodyRetarget.PlanWeights(bytes, pairs, acrossBodies: true);
        output.WriteLine($"weights alone: {sw.ElapsedMilliseconds:N0} ms, reweighted {weights?.Reweighted:N0}");

        sw.Restart();
        var planned = BodyRetarget.Plan(garment, bytes, pairs, "_top", replaceSkin: true, acrossBodies: true);
        output.WriteLine($"whole refit: {sw.ElapsedMilliseconds:N0} ms");
        output.WriteLine("  " + Describe(planned.Report));
    }

    private static string Describe(BodyRetarget.Report r)
        => $"moved up to {r.WorstMove * 1000:F1} mm, snapped {r.Snapped:N0}, pushed {r.Pushed:N0}, missed {r.Missed:N0}"
         + (r.Swap is { } s
                ? $"; skin {s.Removed:N0} out {s.Added:N0} in, reweighted {s.Reweighted:N0} (trimmed {s.Trimmed:N0}), "
                  + $"extras out {s.ExtrasDropped:N0}, unplaced {s.Unplaced:N0}"
                : "; no rebuild");

    /// <summary>
    /// The cloth of <paramref name="model"/> against the author's <paramref name="truth"/>: each cloth vertex paired
    /// with the truth's cloth vertex at the nearest texture coordinate, then the position error and how much of the
    /// weight sits on a different bone (0 = identical skinning, 1 = nothing in common), and the share of weight on the
    /// chest, belly and hip bone families on each side.
    /// </summary>
    private static string ClothAgainst(byte[] model, byte[] truth)
    {
        var (mPos, mUv, mW) = ClothVertices(model);
        var (tPos, tUv, tW) = ClothVertices(truth);

        // Truth's cloth uvs in a grid, for the nearest-uv pairing.
        const int cells = 512;
        var grid = new Dictionary<(int, int), List<int>>();
        (int, int) Cell(Vector2 uv) => ((int)MathF.Floor(uv.X * cells), (int)MathF.Floor(uv.Y * cells));
        for (int i = 0; i < tUv.Count; i++)
        {
            if (!grid.TryGetValue(Cell(tUv[i]), out var list)) grid[Cell(tUv[i])] = list = [];
            list.Add(i);
        }

        var posErr = new List<float>();
        var wErr = new List<float>();
        float mChest = 0, tChest = 0, mBelly = 0, tBelly = 0, mHip = 0, tHip = 0;
        for (int i = 0; i < mUv.Count; i++)
        {
            var (cx, cy) = Cell(mUv[i]);
            int best = -1;
            float bestD = 0.002f;
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                if (!grid.TryGetValue((x, y), out var list)) continue;
                foreach (int j in list)
                {
                    float d = Vector2.Distance(mUv[i], tUv[j]);
                    // A uv can sit on both sides of a mirrored island: the nearer in space wins a tie.
                    if (d < bestD || (d == bestD && best >= 0
                                      && Vector3.Distance(mPos[i], tPos[j]) < Vector3.Distance(mPos[i], tPos[best])))
                    {
                        bestD = d;
                        best = j;
                    }
                }
            }
            if (best < 0) continue;
            posErr.Add(Vector3.Distance(mPos[i], tPos[best]));   // metres: Stats prints millimetres

            var a = mW[i];
            var b = tW[best];
            float shared = 0f;
            foreach (var (bone, w) in a)
                shared += MathF.Min(w, b.Where(x => x.Bone == bone).Sum(x => x.W));
            wErr.Add(1f - shared);

            float Family(List<(string Bone, float W)> ws, string[] prefixes)
                => ws.Where(x => prefixes.Any(pre => x.Bone.StartsWith(pre, StringComparison.Ordinal))).Sum(x => x.W);
            mChest += Family(a, ["j_mune", "iv_c_mune"]); tChest += Family(b, ["j_mune", "iv_c_mune"]);
            mBelly += Family(a, ["iv_fukubu", "ya_fukubu"]); tBelly += Family(b, ["iv_fukubu", "ya_fukubu"]);
            mHip += Family(a, ["j_kosi", "iv_shiri", "ya_shiri"]); tHip += Family(b, ["j_kosi", "iv_shiri", "ya_shiri"]);
        }

        int n = Math.Max(1, posErr.Count);
        wErr.Sort();
        string weights = wErr.Count == 0 ? "(none)"
                       : $"{wErr.Average():P1} mean, {wErr[(int)(wErr.Count * 0.95f)]:P0} p95";
        return $"paired {posErr.Count:N0}/{mUv.Count:N0}: position {Stats(posErr)} mm; weights differ {weights}; "
             + $"chest {mChest / n:P0} vs {tChest / n:P0}, belly {mBelly / n:P0} vs {tBelly / n:P0}, "
             + $"hips {mHip / n:P0} vs {tHip / n:P0}";
    }

    /// <summary>A model's cloth vertices: position, uv0, and influences by bone name.</summary>
    private static (List<Vector3> Pos, List<Vector2> Uv, List<List<(string Bone, float W)>> W) ClothVertices(byte[] mdl)
    {
        var parts = ModelPartReader.Read(mdl)!;
        var uv = Uv(mdl);
        var skin = ModelSkinReader.Read(mdl, null, null)!;
        var seen = new HashSet<int>();
        var pos = new List<Vector3>();
        var uvs = new List<Vector2>();
        var ws = new List<List<(string, float)>>();
        foreach (var part in parts.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)
                || BodyRetarget.IsBodyExtraMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (!seen.Add(v) || v * 2 + 1 >= uv.Length) continue;
                pos.Add(At(parts, v));
                uvs.Add(new Vector2(uv[v * 2] - MathF.Floor(uv[v * 2]), uv[v * 2 + 1] - MathF.Floor(uv[v * 2 + 1])));
                var list = new List<(string, float)>();
                for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                {
                    float w = skin.BoneWeights[v * 8 + k];
                    if (w > 0f) list.Add((skin.BoneNames[skin.BoneIndices[v * 8 + k]], w));
                }
                ws.Add(list);
            }
        }
        return (pos, uvs, ws);
    }

    /// <param name="crossBody">Hand the pair both bodies' files, so a refit between two rigs rewrites the cloth's
    /// weights (and, with replaceSkin, swaps the skin): a refit from one body mod to another.</param>
    private static void AddPair(List<BodyRetarget.SlotPair> pairs, string slot, string sourcePath, string targetPath,
                                bool swapSkin = false, bool crossBody = false)
    {
        if (!File.Exists(sourcePath) || !File.Exists(targetPath)) return;
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var targetBytes = File.ReadAllBytes(targetPath);
        var source = ModelPartReader.Read(sourceBytes)!;
        var target = ModelPartReader.Read(targetBytes)!;
        if (!BodyCorrespondence.TryBuild(source, Uv(sourceBytes), target, Uv(targetBytes), slot,
                                         out var built, out string refusal))
            throw new InvalidOperationException(refusal);
        pairs.Add(new BodyRetarget.SlotPair(slot, built!, target, swapSkin || crossBody ? targetBytes : null,
                                            crossBody ? sourceBytes : null));
    }

    private static ModelParts Read(string path)
        => ModelPartReader.Read(File.ReadAllBytes(path))
           ?? throw new InvalidOperationException($"could not read {path}");

    private static List<(string Name, ModelParts Model, float[] Uv)> Models(params string[] paths)
    {
        var list = new List<(string, ModelParts, float[])>();
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var model = ModelPartReader.Read(bytes);
            if (model == null) continue;
            list.Add((Path.GetFileNameWithoutExtension(path), model, Uv(bytes)));
        }
        return list;
    }

    /// <summary>uv0 in <see cref="ModelPartReader"/>'s vertex order, which is what the guard's proof needs.</summary>
    internal static float[] Uv(byte[] mdl)
        => SecondSkinWriter.TryReadLod0Geometry(mdl, out _, out var uv, out _, out _, out _, false, false, null)
            ? uv
            : [];
}
