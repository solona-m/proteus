using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Which of the author's sizes a size set cuts each new size from (<c>SizeSetRefit.Source.cs</c>): the nearest, by the
/// distance between the bodies, measured against the refit's own move.
/// </summary>
[Collection(GameBodyCollection.Name)]
public class SizeSetSourceTests(ITestOutputHelper output)
{
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string IvcsBibo = LocalData.Mods + ":[IVCS] Bibo+";
    internal const string StarlightBikini = LocalData.Mods + ":Starlight Bikini - by Solona";
    internal const string CallersSwimwear = LocalData.Mods + ":Caller's Swimwear - by Solona";
    internal const string ButterflyCorset = LocalData.Mods + ":Butterfly Corset - by Solona";
    internal const string HeartHalter = LocalData.Mods + ":Heart Halter - by Solona";
    internal const string Cutout = LocalData.Mods + ":Cutout - by Solona";
    internal const string Abyss = LocalData.Mods + ":Abyss - by Solona";
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    internal const string BeMine = LocalData.Mods + ":Be Mine - by Solona";
    internal const string FishetStarlight = LocalData.Mods + ":Fishet Starlight - by Solona";

    private static SizeSetRefit.SourceDistance D(float bodyMm, float bodyMeanMm, float skinMm = 0, float skinMeanMm = 0, int order = 0)
        => new(bodyMm / 1000f, bodyMeanMm / 1000f, skinMm / 1000f, skinMeanMm / 1000f, order);

    [Fact]
    [Trait("Plan", "AC-bulk-nearest-source-2")]
    [Trait("Plan", "EDGE-bulk-nearest-source-14")]
    [Trait("Plan", "EDGE-bulk-nearest-source-26")]
    [Trait("Plan", "EDGE-bulk-nearest-source-27")]
    [Trait("Plan", "EDGE-bulk-nearest-source-34")]
    [Trait("Plan", "EDGE-bulk-nearest-source-1")]
    public void Choose_keeps_the_nearest_at_each_key_in_turn()
    {
        // The body decides when it is clear.
        var (chosen, step, gap) = SizeSetRefit.Choose([("S", D(44.4f, 11.8f, order: 0)), ("M", D(16f, 4.7f, order: 1)),
                                                        ("L", D(0.3f, 0.7f, order: 2))]);
        Assert.Equal(("L", "body"), (chosen, step));
        Assert.Equal(0.0157f, gap, 4);

        // Within half a millimetre at the 90th percentile, the mean decides — exactly, however small the difference.
        Assert.Equal(("B", "body mean"), (SizeSetRefit.Choose([("A", D(10f, 5.0f)), ("B", D(10.4f, 4.9f, order: 1))]).Chosen,
                                          SizeSetRefit.Choose([("A", D(10f, 5.0f)), ("B", D(10.4f, 4.9f, order: 1))]).Step));
        // Bodies alike (a covered bust: the skin tells nothing either, or a body read the same way): the skin, then the
        // author's order.
        Assert.Equal("Y", SizeSetRefit.Choose([("X", D(5, 2, 9, 3)), ("Y", D(5, 2, 4, 3, 1))]).Chosen);
        var tie = SizeSetRefit.Choose([("first", D(5, 2, 4, 3, 0)), ("second", D(5, 2, 4, 3, 1))]);
        Assert.Equal(("first", "author order", 0f), tie);
        // Capped alike at the percentile, the mean still tells the further from the nearer.
        Assert.Equal("near", SizeSetRefit.Choose([("far", D(80, 30)), ("near", D(80, 12, order: 1))]).Chosen);

        // a ≈ b and b ≈ c but not a ≈ c: a filter, not a comparer, so no inconsistency — the nearest is kept with what
        // lies within reach of IT, and the mean picks among those.
        var chain = SizeSetRefit.Choose([("c", D(10.8f, 1, order: 2)), ("b", D(10.4f, 3, order: 1)), ("a", D(10.0f, 5))]);
        Assert.Equal("b", chain.Chosen);
        Assert.Equal("body mean", chain.Step);

        // One alone is chosen by the body, with nothing to be ahead of.
        Assert.Equal(("only", "body", 0f), SizeSetRefit.Choose([("only", D(3, 1))]));
    }

    [Fact]
    [Trait("Plan", "EDGE-bulk-nearest-source-13")]
    [Trait("Plan", "EDGE-bulk-nearest-source-35")]
    public void Nothing_to_read_counts_as_the_cap_not_the_family_reading_s()
    {
        Assert.Equal(SizeSetRefit.SourceCap, SizeSetRefit.P90(null));
        Assert.Equal(SizeSetRefit.SourceCap, SizeSetRefit.P90([]));
        Assert.Equal(SizeSetRefit.SourceCap, SizeSetRefit.Mean(null));
        Assert.Equal(9f, SizeSetRefit.P90([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
        Assert.Equal(5f, SizeSetRefit.P90([5]));
        Assert.NotEqual(BodySizeMatch.NearCap, SizeSetRefit.SourceCap);
    }

    [Fact]
    [Trait("Plan", "AC-bulk-nearest-source-3")]
    [Trait("Plan", "EDGE-bulk-nearest-source-11")]
    public void A_lead_s_choice_is_kept_per_race()
    {
        Assert.NotEqual(SizeSetRefit.LeadKey("Size", "0201", "Rue L"), SizeSetRefit.LeadKey("Size", "0101", "Rue L"));
        Assert.NotEqual(SizeSetRefit.LeadKey("Size", "0201", "Rue L"), SizeSetRefit.LeadKey("Size", "0201", "Rue M"));
    }

    [Fact]
    [Trait("Plan", "AC-bulk-nearest-source-3")]
    [Trait("Plan", "EDGE-bulk-nearest-source-8")]
    public void A_follower_s_file_comes_from_the_manifest_not_the_deduplicated_options()
    {
        const string legs = "chara/equipment/e6000/model/c0201e6000_dwn.mdl";
        var redirects = new List<PenumbraModMeta.Redirect>
        {
            new(legs, "shared\\dwn.mdl", "Size / Small"),
            new(legs, "shared\\dwn.mdl", "Size / Large"),
            new("chara/equipment/e6000/model/c0201e6000_top.mdl", "large\\top.mdl", "Size / Large"),
        };
        Assert.Equal("shared\\dwn.mdl", SizeSetRefit.ResolveOptionFile(redirects, legs.ToUpperInvariant(), "size", "LARGE"));
        Assert.Null(SizeSetRefit.ResolveOptionFile(redirects, legs, "Size", "Bare"));
        Assert.Null(SizeSetRefit.ResolveOptionFile(redirects, legs, "Other", "Large"));
    }

    [Fact]
    [Trait("Plan", "AC-bulk-nearest-source-4")]
    [Trait("Plan", "EDGE-bulk-nearest-source-4")]
    [Trait("Plan", "EDGE-bulk-nearest-source-19")]
    [Trait("Plan", "EDGE-bulk-nearest-source-30")]
    [Trait("Plan", "EDGE-bulk-nearest-source-31")]
    public void A_size_is_cut_only_from_what_may_go_onto_it()
    {
        static BodyOption O(string rel) => new("", "", rel, rel, "", "_top");
        var almondS = O("extra chest - smallclothes/sfw almond s.mdl");
        var almondL = O("extra chest - smallclothes/sfw almond l.mdl");
        var neobellyM = O("neobelly chest/sfw neobelly m.mdl");

        // Across body mods: anything that has a size to go onto.
        Assert.Null(SizeSetRefit.Ineligible("YAB", almondS, true, "Rue", almondL));
        Assert.Equal("no size of its own to go onto", SizeSetRefit.Ineligible("YAB", almondS, false, "Rue", null));
        // Within one: never the size itself, and never from another family of a body that has them.
        Assert.Equal(SizeSetRefit.MadeOnIt, SizeSetRefit.Ineligible("Neo", almondL, true, "neo", almondL));
        Assert.Null(SizeSetRefit.Ineligible("Neo", almondS, true, "Neo", almondL));
        Assert.Equal("another family of its body", SizeSetRefit.Ineligible("Neo", neobellyM, true, "Neo", almondL));
        Assert.Null(SizeSetRefit.Ineligible("Neo", neobellyM, false, "Neo", almondL));

        // Rigged as the target is, when any are; else all of them.
        (string Name, bool Yas)[] mixed = [("Neolithe L", false), ("YAB L", true)];
        Assert.Equal(["YAB L"], SizeSetRefit.RiggedAlike(mixed, c => c.Yas, targetYas: true).Select(c => c.Name));
        Assert.Equal(["Neolithe L"], SizeSetRefit.RiggedAlike(mixed, c => c.Yas, targetYas: false).Select(c => c.Name));
        Assert.Equal(["Neolithe L"], SizeSetRefit.RiggedAlike(mixed[..1], c => c.Yas, targetYas: true).Select(c => c.Name));
    }

    /// <summary>
    /// Where a size goes from each of the author's sizes: legs following a chest go onto the legs the table pairs with
    /// that chest size across body mods, and within one, onto the legs of the CANDIDATE's own family — chosen before the
    /// legs' target is read, never the first size's.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-bulk-nearest-source-3")]
    [Trait("Plan", "EDGE-bulk-nearest-source-20")]
    [Trait("Plan", "EDGE-bulk-nearest-source-28")]
    public void A_size_s_target_follows_the_source_chosen_for_it()
    {
        var neolithe = SupportedBodies.Of("neolithe", "_top", false)!;
        var l = neolithe.Sizes.Single(s => s.Name == "L");
        const string smallLegs = "default legs - smallclothes/sfw small.mdl";
        Assert.Equal(l.Rel, SizeSetRefit.TargetRel(followsChest: false, across: true, l, smallLegs));
        Assert.Equal(l.LegsRel, SizeSetRefit.TargetRel(followsChest: true, across: true, l, smallLegs));
        Assert.Equal("default legs - smallclothes/sfw large.mdl", SizeSetRefit.TargetRel(followsChest: true, across: false, l, smallLegs));
        // A size whose family has no legs of its own: nowhere to go from that source.
        var xs = neolithe.Sizes.Single(s => s.Name == "XS");
        Assert.Null(SizeSetRefit.TargetRel(followsChest: true, across: false, xs, smallLegs));
    }

    [Fact]
    [Trait("Plan", "AC-bulk-nearest-source-2")]
    public void Near_honours_its_cap()
    {
        // A body that is one flat sheet of skin, and points 50 mm off it.
        string path = Path.Combine(Path.GetTempPath(), "proteus-nearcap-" + Guid.NewGuid().ToString("N") + ".mdl");
        try
        {
            File.WriteAllBytes(path, SkinSheet(0.4f, 20));
            var off = new[] { new System.Numerics.Vector3(0.1f, 0.1f, 0.05f), new System.Numerics.Vector3(0.1f, 0.1f, 0.005f) };
            var capped = BodySizeMatch.Near(off, path, out _, out _)!;
            var far = BodySizeMatch.Near(off, path, out _, out _, SizeSetRefit.SourceCap)!;
            Assert.Equal(BodySizeMatch.NearCap, capped[0], 4);
            Assert.Equal(0.05f, far[0], 3);
            Assert.Equal(capped[1], far[1], 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── whole runs on the reported mods, measured inside the pass ───────────────────────────────────────────────

    /// <summary>A copy of a mod's manifests and models, with the named options of its size group taken out.</summary>
    private static string CopyWithout(string mod, string group, params string[] remove)
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        SizeSetRefitTests.CopyModels(LocalData.Path(mod), copy, withRecord: false);
        foreach (string option in remove) Assert.NotNull(PenumbraModMeta.RemoveOption(copy, group, option));
        return copy;
    }

    private readonly List<(string Template, object[] Args)> logged = [];

    private SizeSetRefit.Result Run(string copy, IReadOnlyList<string> keys, bool remake, params string[] bodyMods)
        => Run(copy, keys, remake, yas: false, bodyMods);

    private SizeSetRefit.Result Run(string copy, IReadOnlyList<string> keys, bool remake, bool yas, params string[] bodyMods)
    {
        var mods = bodyMods.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(mods, LocalData.Path(LocalData.Mods)).List;
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var result = SizeSetRefit.Run(new SizeSetRefit.Request(copy, keys, yas, remake, bodies),
                                      new UVRemapService(log, "."), log, _ => { }, System.Threading.CancellationToken.None);
        foreach (string note in result.Notes) output.WriteLine("note: " + note);
        logged.Clear();
        foreach (var call in log.ReceivedCalls())
        {
            var args = call.GetArguments();
            logged.Add((args.FirstOrDefault() as string ?? "", args.Skip(1).FirstOrDefault() as object[] ?? []));
        }
        foreach (var (template, args) in logged.Where(l => l.Args.Length > 1 && l.Args[1] is string s
                                                           && (s.Contains(" from ", StringComparison.Ordinal) || s.Contains("sources", StringComparison.Ordinal))))
            output.WriteLine($"log: {args[0]}: {args[1]}");
        return result;
    }

    /// <summary>The source line the run logged for one size of one piece: "Rue L from YAB Large (by body, …)".</summary>
    private string SourceOf(string option)
        => logged.Select(l => l.Args.Length > 1 ? l.Args[1] as string : null)
                 .Single(s => s != null && s.StartsWith(option + " from ", StringComparison.Ordinal))!;

    /// <summary>How far, at most, the run moved one size of one piece (its own log line).</summary>
    private float MovedMm(string piece, string option)
        => Convert.ToSingle(logged.Single(l => l.Template.Contains("onto {1} (moved up to", StringComparison.Ordinal)
                                               && (string)l.Args[0] == piece && (string)l.Args[1] == option).Args[2]);

    /// <summary>
    /// The target body's skin a refit left out that no cloth of the refit lies over — a hole — in cm²: the largest
    /// edge-connected patch, and all of it. Left out: none of a triangle's corners is among the refit's skin. Uncovered:
    /// a ray from its middle along its outward normal, from 3 mm inside to 25 mm out, meets no cloth.
    /// </summary>
    internal static (double Largest, double Total) UncoveredCutSkin(byte[] model, string targetBodyPath)
    {
        var refit = ModelPartReader.Read(model)!;
        var body = ModelPartReader.Read(File.ReadAllBytes(targetBodyPath))!;
        System.Numerics.Vector3 At(ModelParts m, int v) => new(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]);
        var kept = refit.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                              .SelectMany(p => p.Triangles)
                              .Select(v => MeshMath.PositionKey(BodyRetarget.ToVec(At(refit, v)), BodyRetarget.SnapPerMetre))
                              .ToHashSet();
        var cloth = refit.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                          .Select(t => (At(refit, p.Triangles[3 * t]), At(refit, p.Triangles[3 * t + 1]),
                                                                        At(refit, p.Triangles[3 * t + 2]))))
                               .ToList();
        const float cell = 0.02f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < cloth.Count; i++)
        {
            var (a, b, c) = cloth[i];
            var lo = System.Numerics.Vector3.Min(a, System.Numerics.Vector3.Min(b, c)) / cell;
            var hi = System.Numerics.Vector3.Max(a, System.Numerics.Vector3.Max(b, c)) / cell;
            for (int x = (int)MathF.Floor(lo.X); x <= (int)MathF.Floor(hi.X); x++)
                for (int y = (int)MathF.Floor(lo.Y); y <= (int)MathF.Floor(hi.Y); y++)
                    for (int z = (int)MathF.Floor(lo.Z); z <= (int)MathF.Floor(hi.Z); z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = [];
                        list.Add(i);
                    }
        }
        bool Covered(System.Numerics.Vector3 o, System.Numerics.Vector3 d)
        {
            var seen = new HashSet<int>();
            for (float t = -0.003f; t <= 0.025f + cell; t += cell / 2)
            {
                var p = (o + d * MathF.Min(t, 0.025f)) / cell;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue(((int)MathF.Floor(p.X) + dx, (int)MathF.Floor(p.Y) + dy, (int)MathF.Floor(p.Z) + dz),
                                                  out var list)) continue;
                            foreach (int i in list)
                            {
                                if (!seen.Add(i)) continue;
                                var (a, b, c) = cloth[i];
                                var e1 = b - a;
                                var e2 = c - a;
                                var h = System.Numerics.Vector3.Cross(d, e2);
                                float det = System.Numerics.Vector3.Dot(e1, h);
                                if (MathF.Abs(det) < 1e-12f) continue;
                                var s = o - a;
                                float u = System.Numerics.Vector3.Dot(s, h) / det;
                                if (u < 0 || u > 1) continue;
                                var q = System.Numerics.Vector3.Cross(s, e1);
                                float w = System.Numerics.Vector3.Dot(d, q) / det;
                                if (w < 0 || u + w > 1) continue;
                                float hit = System.Numerics.Vector3.Dot(e2, q) / det;
                                if (hit >= -0.003f && hit <= 0.025f) return true;
                            }
                        }
            }
            return false;
        }

        // The holes, triangle by triangle, joined into patches where two share an edge.
        var holes = new List<(int A, int B, int C, double Area)>();
        foreach (var part in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int ia = part.Triangles[t], ib = part.Triangles[t + 1], ic = part.Triangles[t + 2];
                var (a, b, c) = (At(body, ia), At(body, ib), At(body, ic));
                if (new[] { a, b, c }.Any(v => kept.Contains(MeshMath.PositionKey(BodyRetarget.ToVec(v), BodyRetarget.SnapPerMetre))))
                    continue;
                var cross = System.Numerics.Vector3.Cross(b - a, c - a);
                if (cross.Length() <= 0f) continue;
                var n = System.Numerics.Vector3.Normalize(cross);
                var vn = new System.Numerics.Vector3(body.Normals[3 * ia], body.Normals[3 * ia + 1], body.Normals[3 * ia + 2]);
                if (System.Numerics.Vector3.Dot(n, vn) < 0) n = -n;
                if (!Covered((a + b + c) / 3, n)) holes.Add((ia, ib, ic, cross.Length() / 2 * 1e4));
            }
        var parent = Enumerable.Range(0, holes.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var byEdge = new Dictionary<(int, int), int>();
        for (int i = 0; i < holes.Count; i++)
        {
            var (a, b, c, _) = holes[i];
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var edge = x < y ? (x, y) : (y, x);
                if (byEdge.TryGetValue(edge, out int j)) parent[Find(i)] = Find(j);
                else byEdge[edge] = i;
            }
        }
        var patches = holes.Select((h, i) => (Root: Find(i), h.Area)).GroupBy(h => h.Root).Select(g => g.Sum(h => h.Area)).ToList();
        return (patches.DefaultIfEmpty(0).Max(), patches.Sum());
    }

    /// <summary>
    /// A refit of one author's file onto one Rue chest made by hand the size set's way: its arguments (the Studio's
    /// defaults), the hem's legs moved onto the table's legs, and its skin drawn the way the new body draws it.
    /// </summary>
    private static (BodyRetarget.Planned Plan, ModelParts Garment) DirectRefit(string garmentPath, BodyModIndex.Entry source,
        BodyOption from, BodyModIndex.Entry target, BodyOption to, SupportedBodies.Size size, UVRemapService uv)
    {
        byte[] bytes = File.ReadAllBytes(garmentPath);
        var garment = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        string sourcePath = source.Catalog.PathOf(from), targetPath = target.Catalog.PathOf(to);
        Assert.Null(BodyRetarget.BuildPair("_top", sourcePath, targetPath, "Chest", false,
                                           RefitCore.MaskOf(source.Dir, source.Catalog, "_top", null),
                                           RefitCore.MaskOf(target.Dir, target.Catalog, "_top", null), uv, out var pair));
        var pairs = new List<BodyRetarget.SlotPair> { pair };
        var hem = BodySizeMatch.Rank(garment, source.Catalog.For("_dwn", "0201"), source.Catalog.PathOf, bones);
        if (hem.Best is { } legs && AutoRefitDecisions.Proceed(hem.Confidence)
            && target.Catalog.For("_dwn").FirstOrDefault(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(size.LegsRel!)) is { } legsTo
            && BodyRetarget.BuildPair("_dwn", source.Catalog.PathOf(legs.Option), target.Catalog.PathOf(legsTo), "Legs", false,
                                      RefitCore.MaskOf(source.Dir, source.Catalog, "_dwn", null),
                                      RefitCore.MaskOf(target.Dir, target.Catalog, "_dwn", null), uv, out var legsPair) == null)
            pairs.Add(legsPair);
        var plan = BodyRetarget.Plan(garment, bytes, pairs, "_top", held: new HashSet<int>(), replaceSkin: true,
                                     acrossBodies: true, clearBody: false, cutHidden: true,
                                     keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        return (RefitCore.MatchSkinToBody(plan, sourcePath, targetPath, uv, false, log), garment);
    }

    /// <summary>Triangles a refit leaves folded, counted on its FINAL move (the report counts before the last passes).</summary>
    private static int FoldedAfter(ModelParts garment, RetargetEdit edit)
    {
        var sets = BodyRetarget.Sets.From(garment);
        var nodeDelta = new SecondSkinWriter.Vec3[sets.NodeCount];
        var seen = new bool[sets.NodeCount];
        for (int v = 0; v < sets.NodeOf.Length; v++)
            if (!seen[sets.NodeOf[v]])
            {
                seen[sets.NodeOf[v]] = true;
                nodeDelta[sets.NodeOf[v]] = edit.DeltaAt(v);
            }
        return BodyRetarget.FoldedTriangles(sets, nodeDelta);
    }

    /// <summary>
    /// The reporter's case: a top whose sizes are YAB Small, Medium and Large, given Rue's. Rue Large is cut from YAB
    /// Large, not Small, and is the refit of YAB Large made by hand — where cut from Small, carried 57 mm, the move folded
    /// 374 triangles over, the black specks seen at the cups' rims.
    /// </summary>
    [LocalDataFact(Yab, Rue, FishetStarlight)]
    [Trait("Plan", "AC-bulk-nearest-source-1")]
    [Trait("Plan", "AC-bulk-nearest-source-5")]
    [Trait("Plan", "EDGE-bulk-nearest-source-6")]
    public void Yab_sizes_each_cut_their_own_Rue_size()
    {
        const string top = "chara/equipment/e6030/model/c0201e6030_top.mdl";
        string copy = CopyWithout(FishetStarlight, "Size", "XSmall", "Small", "Medium", "Large", "XLarge",
                                  "Rue Small", "Rue Medium", "Rue Large", "Buff Small", "Buff Medium", "Buff Large");
        try
        {
            var before = SizeSetRefitTests.Options(copy, "Size");
            Run(copy, ["rue"], remake: false, Yab, Rue);
            var added = SizeSetRefitTests.Options(copy, "Size").Except(before).ToList();
            Assert.Equal(["Rue S", "Rue M", "Rue L"], added);

            // Each from its own YAB size, the body deciding by far more than the tie.
            foreach (var (size, from) in new[] { ("Rue S", "YAB Small"), ("Rue M", "YAB Medium"), ("Rue L", "YAB Large") })
                Assert.StartsWith($"{size} from {from} (by body", SourceOf(size));
            Assert.True(MovedMm("Body — e6030", "Rue L") < 25f, $"moved {MovedMm("Body — e6030", "Rue L")} mm");
            var record = BodyRetargetWriter.ReadRecord(copy)!;
            Assert.Contains("Large", record.Options.Single(e => e.Name == "Rue L" && e.GamePath == top).From);

            // The hole itself: the bulk Rue L is the refit of YAB Large made by hand the same way, and the hole a refit of
            // YAB Small leaves is gone.
            var dirs = new[] { Yab, Rue }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
            var bodies = new BodyModIndex().RefreshState(dirs, LocalData.Path(LocalData.Mods)).List;
            var yab = bodies.Single(b => b.Dir == Path.GetFileName(LocalData.Path(Yab)));
            var rue = bodies.Single(b => b.Dir == Path.GetFileName(LocalData.Path(Rue)));
            BodyOption Chest(BodyModIndex.Entry e, string key, string size)
                => e.Catalog.For("_top").First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(
                       SupportedBodies.Of(key, "_top", true)!.Sizes.Single(s => s.Name == size).Rel));
            var rueL = SupportedBodies.Of("rue", "_top", true)!.Sizes.Single(s => s.Name == "L");
            string target = rue.Catalog.PathOf(Chest(rue, "rue", "L"));
            var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
            var uv = new UVRemapService(log, ".");
            string Garment(string option) => Path.Combine(LocalData.Path(FishetStarlight), "size", option, "chara", "equipment",
                                                          "e6030", "model", "c0201e6030_top.mdl");
            var (byHand, garmentL) = DirectRefit(Garment("yab large"), yab, Chest(yab, "yab", "L"), rue, Chest(rue, "rue", "L"), rueL, uv);
            var (fromSmall, garmentS) = DirectRefit(Garment("yab small"), yab, Chest(yab, "yab", "S"), rue, Chest(rue, "rue", "L"), rueL, uv);
            byte[] bulkModel = SizeSetRefitTests.ModelOfOption(copy, "Size", "Rue L", top);
            var large = UncoveredCutSkin(byHand.Model, target);
            var small = UncoveredCutSkin(fromSmall.Model, target);
            var bulk = UncoveredCutSkin(bulkModel, target);
            int foldedL = FoldedAfter(garmentL, byHand.Edit), foldedS = FoldedAfter(garmentS, fromSmall.Edit);
            output.WriteLine($"uncovered cut skin, largest patch / all: by hand from Large {large.Largest:F2}/{large.Total:F2} cm² "
                           + $"(moved {byHand.Report.WorstMove * 1000:F1} mm, {foldedL} folded), from Small "
                           + $"{small.Largest:F2}/{small.Total:F2} cm² (moved {fromSmall.Report.WorstMove * 1000:F1} mm, "
                           + $"{foldedS} folded), bulk {bulk.Largest:F2}/{bulk.Total:F2} cm²");
            // The holes are triangles the move folded over — black specks in game — and no skin left uncovered: from
            // Small 374 folded, from Large 17, no skin uncovered either way.
            Assert.True(foldedS > 5 * foldedL, $"from Small {foldedS} folded, from Large {foldedL}");
            Assert.True(large.Largest < 1.0, $"a refit of YAB Large by hand reads {large.Largest:F2} cm² of hole");
            // And the bulk Rue L IS that refit of Large: byte for byte, or (failing determinism) moved alike and holed alike.
            if (bulkModel.AsSpan().SequenceEqual(byHand.Model))
                output.WriteLine("the bulk Rue L is byte for byte the refit of YAB Large made by hand");
            else
            {
                Assert.Equal(byHand.Report.WorstMove * 1000f, MovedMm("Body — e6030", "Rue L"), 0.1f);
                Assert.InRange(bulk.Largest, large.Largest - 0.3, large.Largest + 0.3);
            }

            // Logs name the mod's files by their place in it, never the folder on this machine.
            Assert.DoesNotContain(logged, l => l.Args.Any(a => a is string s && s.Contains(copy, StringComparison.OrdinalIgnoreCase)));

            // A second run finds nothing to make and nothing to say.
            var again = Run(copy, ["rue"], remake: false, Yab, Rue);
            Assert.Equal(0, again.Made);
            Assert.Empty(again.Notes);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// Sizes of several bodies in one group: Abyss lists Neolithe XS–XL before its YAB ones. Rue Large is cut from YAB
    /// Large, not the first option.
    /// </summary>
    [LocalDataFact(Yab, Rue, Neolithe, Abyss)]
    [Trait("Plan", "AC-bulk-nearest-source-2")]
    [Trait("Plan", "EDGE-bulk-nearest-source-29")]
    public void Mixed_body_sizes_pick_the_nearest_per_Rue_size()
    {
        string copy = CopyWithout(Abyss, "Size", "Rue Small", "Rue Medium", "Rue Large");
        try
        {
            Run(copy, ["rue"], remake: false, Neolithe, Yab, Rue);
            string rueL = SourceOf("Rue L");
            Assert.StartsWith("Rue L from YAB Large", rueL);
            // Every candidate measured and named.
            foreach (string option in new[] { "YAB Small", "YAB Medium", "Buff Small", "Buff Medium", "Buff Large" })
                Assert.Contains(option + ": body", rueL);
            Assert.True(MovedMm("Body — e0328", "Rue L") < 25f);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// A corset over the bust, listing Neolithe's sizes (no YAS) before its YAB ones (YAS): asked for YAS sizes, as the
    /// panel asks by default, Rue's YAS Large is cut from YAB Large, rigged alike, rather than from Neolithe. Its skin
    /// tells the sizes apart no better than its body does.
    /// </summary>
    [LocalDataFact(Yab, Rue, Neolithe, ButterflyCorset)]
    [Trait("Plan", "EDGE-bulk-nearest-source-27")]
    public void A_covered_bust_garment_is_chosen_by_its_body()
    {
        string copy = CopyWithout(ButterflyCorset, "Size", "Rue S", "Rue M", "Rue L");
        try
        {
            Run(copy, ["rue"], remake: false, yas: true, Neolithe, Yab, Rue);
            string rueL = SourceOf("Rue L");
            Assert.StartsWith("Rue L from Yab L", rueL);
            Assert.True(MovedMm("Body — e0014", "Rue L") < 30f);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// Every one of the author's YAB sizes is the same model, read as the small chest Rue+ and YAB+ share: each Rue size
    /// is cut from the first, and Rue Small is that very size.
    /// </summary>
    [LocalDataFact(Yab, Rue, BeMine)]
    [Trait("Plan", "EDGE-bulk-nearest-source-1")]
    public void Identical_author_sizes_tie_to_author_order()
    {
        string copy = CopyWithout(BeMine, "Size", "XSmall", "Small", "Medium", "Large", "XLarge",
                                  "Rue Small", "Rue Medium", "Rue Large", "Buff Small", "Buff Medium", "Buff Large");
        try
        {
            var result = Run(copy, ["rue"], remake: false, Yab, Rue);
            // Rue Small is the size it reads as made on (Rue+ files its small chest as YAB+ does): not made.
            Assert.Equal(2, result.Made);
            Assert.Contains(logged, l => l.Args.Length > 1 && l.Args[1] as string == "Rue S"
                                         && (l.Template.Contains("is the size it was made on", StringComparison.Ordinal)
                                             || l.Template.Contains("is the author's own", StringComparison.Ordinal)));
            foreach (string size in new[] { "Rue M", "Rue L" })
                Assert.StartsWith(size + " from Yab Small", SourceOf(size));
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>A flat square of skin in the XY plane, <paramref name="size"/> metres across, cut into a grid.</summary>
    private static byte[] SkinSheet(float size, int cells)
    {
        var pos = new List<System.Numerics.Vector3>();
        var uv = new List<System.Numerics.Vector2>();
        for (int y = 0; y <= cells; y++)
            for (int x = 0; x <= cells; x++)
            {
                pos.Add(new System.Numerics.Vector3(x * size / cells, y * size / cells, 0f));
                uv.Add(new System.Numerics.Vector2((float)x / cells, (float)y / cells));
            }
        var tris = new List<int>();
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                int a = y * (cells + 1) + x, b = a + 1, c = a + cells + 1, d = c + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }
        var normals = pos.Select(_ => System.Numerics.Vector3.UnitZ).ToArray();
        return SyntheticModel.BuildSurface([],
            new SyntheticModel.SurfaceMesh(SyntheticChest.Skin, pos.ToArray(), normals, uv.ToArray(),
                                           new (string, float)[]?[pos.Count], [(0u, tris.ToArray())]));
    }

    /// <summary>
    /// For every chest the reported mods ship — six made on YAB, Abyss's Neolithe sizes, Geometric's Bibo+ ones — the
    /// author's size the body distance chooses for Rue Large is the one the refit moves least, or within 2 mm of it. Run
    /// before anything was wired to it: had it failed, the measure was wrong, not its threshold.
    /// </summary>
    [LocalDataFact(Yab, Rue, Neolithe, Bibo, IvcsBibo, StarlightBikini, CallersSwimwear, ButterflyCorset, HeartHalter,
                   Cutout, Abyss, Geometric)]
    [Trait("Plan", "AC-bulk-nearest-source-2")]
    public void Body_key_chooses_the_size_the_refit_moves_least()
    {
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        var dirs = new[] { Yab, Rue, Neolithe, Bibo, IvcsBibo }.Select(n => Path.GetFileName(LocalData.Path(n)))
                                                                  .ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(dirs, LocalData.Path(LocalData.Mods)).List;
        var rue = bodies.Single(b => b.Dir == Path.GetFileName(LocalData.Path(Rue)));
        var mods = bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        var probes = new Dictionary<string, System.Numerics.Vector3[]>(StringComparer.OrdinalIgnoreCase);
        System.Numerics.Vector3[] ProbesOf(string path)
            => probes.TryGetValue(path, out var p) ? p : probes[path] = SizeSetRefit.BodyProbes(path);

        foreach (string mod in new[] { StarlightBikini, CallersSwimwear, ButterflyCorset, HeartHalter, Cutout, Abyss, Geometric })
        {
            string root = LocalData.Path(mod);
            foreach (var piece in SizeSetRefit.PiecesOf(root, null, includeEnds: false).Where(p => p.Slot == "_top" && p.Race == "0201"))
            {
                var rows = new List<(string Option, float Body, float Mean, float Move)>();
                bool? yas = null;
                foreach (var (option, file) in piece.Options)
                {
                    if (option == null) continue;
                    byte[] bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, file));
                    if (ModelPartReader.Read(bytes) is not { } garment) continue;
                    var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
                    var pick = RefitCore.DetectSource(garment, bones, "_top", "0201", mods, mods[0].Dir, _ => true, _ => { });
                    // The author's own Rue sizes are the target's, not sources of it.
                    if (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence) || pick.Dir == rue.Dir) continue;
                    yas ??= SupportedBodies.IsYas(bones);
                    if (SupportedBodies.IsYas(bones) != yas) continue;
                    var size = SupportedBodies.Of("rue", "_top", yas.Value)!.Sizes.Single(s => s.Name == "L");
                    var to = rue.Catalog.For("_top").First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(size.Rel));
                    string sourcePath = mods.Single(m => m.Dir == pick.Dir).Catalog.PathOf(pick.Option);
                    string targetPath = rue.Catalog.PathOf(to);
                    if (BodyRetarget.BuildPair("_top", sourcePath, targetPath, "Chest", false, null, null, uv, out var pair) != null)
                        continue;
                    var plan = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true,
                                                 acrossBodies: true, clearBody: false, cutHidden: true,
                                                 keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment), out _));
                    var (body, mean) = SizeSetRefit.BodyDistance(sourcePath, targetPath, ProbesOf);
                    rows.Add((option, body, mean, plan.Report.WorstMove));
                    output.WriteLine($"{Path.GetFileName(root)} {option} ({pick.Option.Label}) -> {to.Label}: body {body * 1000:F1} mm "
                                     + $"(mean {mean * 1000:F1}), moved {plan.Report.WorstMove * 1000:F1} mm");
                }
                if (rows.Count < 2) continue;
                var chosen = SizeSetRefit.Choose(rows.Select((r, i) => (r, new SizeSetRefit.SourceDistance(r.Body, r.Mean, 0, 0, i))).ToList());
                float least = rows.Min(r => r.Move);
                output.WriteLine($"  chose {chosen.Chosen.Option} by {chosen.Step} ({chosen.Gap * 1000:F1} mm); least move {least * 1000:F1} mm");
                Assert.True(chosen.Chosen.Move <= least + 0.002f,
                            $"{Path.GetFileName(root)}: chose {chosen.Chosen.Option} moving {chosen.Chosen.Move * 1000:F1} mm, least {least * 1000:F1} mm");
            }
        }
    }
}
