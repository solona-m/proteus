using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// voidblood's "Dei Gratia", a top made on the game's body whose chest skin is cut away under the blouse, refitted onto
/// body mods as the automatic refit and the Studio's Body size tool do it. Reported on Bibo+ Nude Medium: the breasts
/// through the blouse. The bust cloth's nearest authored skin was the upper chest's open edge, 6-9 cm above it, whose
/// normal read it 3-45 mm inside — "tucked in on purpose" — so the push-out left it 5-16 mm inside the new breast. Read
/// against the game's body it was made on, as worn — its bra with its skin — it lies on it (see
/// <see cref="BodyRetarget.TargetBody.Fitted"/>). Needs the game's data, the pack and the body mods named.
/// </summary>
public class DeiGratiaRefitLocalTests(ITestOutputHelper output)
{
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";

    private const string NudeM = "breasts - small clothes/nude - medium/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string SmallclothesM = "files/chest - smallclothes/medium/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string NeolitheSfwM = "default chest - smallclothes/sfw m.mdl";

    private const string Blouse = "mt_c0201e9068_top_a.mtrl";

    /// <summary>The blouse point (with its mirror) the game's body as worn reads furthest inside — 3.0 mm under its bra,
    /// within <see cref="BodyRetarget.FitSlack"/> — and the last of the bust to stay through the new breast before it was
    /// taken in with them.</summary>
    private static readonly Vector3 Inside = new(0.06616934f, 1.243426f, 0.12172698f);

    private static readonly BodyRetarget.Tuning Before = new(NoOpenEdgeFit: true);

    /// <summary>Edges stretched against the authored garment beyond the game-gear refit's, accepted at their measured count
    /// (build 1323): onto Rue+'s fuller bust the blouse now covers the breast it used to pass through, and its long edges
    /// across it stretch (26 → 40 mm).</summary>
    private static readonly Dictionary<string, int> AcceptedStretch = new() { [Rue] = 38 };

    /// <summary>Folds on the solve's last stage beyond the refit without the rule, accepted at their measured count (build
    /// 1323): onto Neolithe, 4 at the neckline patch's bottom edge, where the blouse meets the kept skin — as many as the
    /// game-gear refit of the same top turns over against the authored garment.</summary>
    private static readonly Dictionary<string, int> AcceptedFolds = new() { [Neolithe] = 4 };

    /// <summary>How much further off the new body cloth near the points taken in, but not taken in itself, may end — accepted
    /// at its measured 6.4 mm (build 1323): the blouse over the upper chest, under the neckline patch, lifts as the bust under
    /// it comes out. 6.7 since build 1349, which reads the game chest's bra (see BodyRetarget.SourceWorn): with it the bust
    /// lies inside the blouse from the start (deepest 16.0 mm → -0.8 written, read without the open-edge fit).</summary>
    private const float NearFurtherOff = 0.0068f;

    /// <summary>A refit as one path makes it, with the solve's own nodes and its last stage.</summary>
    private sealed record Refit(BodyRetarget.Planned Plan, ModelParts Laid, BodyRetarget.Sets Sets, Vec3[] Final,
                                bool[] Fitted, List<string> Log);

    private sealed record Inputs(byte[] Bytes, ModelParts Garment, BodyRetarget.SlotPair Pair, string Source, string Target,
                                 UVRemapService Uv, Dalamud.Plugin.Services.IPluginLog PluginLog, Lumina.GameData Data,
                                 BodySizeCatalog Mod, BodySizeCatalog Vanilla);

    private static Inputs Read(string modNeed, string rel)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var pluginLog = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(pluginLog, ".");
        byte[] bytes = LocalData.PackEntryEndingWith(LocalData.DeiGratiaPack, "chara/equipment/e9068/model/c0201e9068_top.mdl");
        var garment = ModelPartReader.Read(bytes)!;
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string dir = LocalData.Path(modNeed);
        var mod = BodySizeCatalog.Read(dir);

        // The source the app would refit from: the automatic refit's own reading over this body mod and the game's body.
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var pick = RefitCore.DetectSource(garment, bones, "_top", "0201", RefitCore.SearchOrder(dir, mod, [], vanilla), dir,
                                          _ => true);
        Assert.NotNull(pick);
        Assert.Equal(VanillaBodyCatalog.Key, pick!.Dir);

        var options = mod.For("_top", "0201");
        var option = options.FirstOrDefault(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel))
                  ?? throw new InvalidOperationException($"{rel} is not one of {string.Join(", ", options.Select(o => o.Rel))}");
        string source = vanilla.PathOf(pick.Option), target = mod.PathOf(option);
        // As the automatic refit builds it: the game's body takes no mask, the body mod's its own (AutoRefitWatcher).
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", false, RefitCore.MaskOf(pick.Dir, vanilla, "_top", null),
                                           RefitCore.MaskOf(dir, mod, "_top", null), uv, out var pair));
        return new Inputs(bytes, garment, pair, source, target, uv, pluginLog, data, mod, vanilla);
    }

    private static Refit Plan(Inputs i, string path, BodyRetarget.Tuning? tuning = null)
    {
        Vec3[]? final = null;
        float[]? fitted = null;
        var log = new List<string>();
        void Trace(string stage, Vec3[] d) => final = d;
        BodyRetarget.Planned Run()
        {
            BodyRetarget.PushTrace.Value = (key, values) => { if (key == "fitted") fitted = values; };
            try
            {
                if (path == "auto")
                {
                    // The automatic refit: a garment that reaches no other body part (asserted by the caller), the skin
                    // whole, as for any mod's garment; then the kept skin matched to the body, and the race route.
                    var plan = RefitCore.PlanRefit(i.Garment, i.Bytes, [i.Pair], "_top", across: true, gameGear: false,
                                                   out _, log.Add, log.Add, Trace);
                    plan = RefitCore.MatchSkinToBody(plan, i.Source, [i.Target], i.Uv, false, i.PluginLog);
                    var route = RefitCore.Route("chara/equipment/e9068/model/c0201e9068_top.mdl", 201,
                                                race => i.Mod.For("_top", $"{race:D4}").Count > 0,
                                                race => race == 201, [], null);
                    var (finished, _, why) = RefitCore.Finish(plan, route, null, [i.Target], i.PluginLog);
                    return finished ?? throw new InvalidOperationException(why);
                }
                // The Studio's Body size tool as it opens: its default pieces, the skin laid on and cut, not cleared.
                var studio = BodyRetarget.Plan(i.Garment, i.Bytes, [i.Pair], "_top", held: new HashSet<int>(),
                                               replaceSkin: true, acrossBodies: true, clearBody: false, cutHidden: true,
                                               keepShape: RefitCore.ShapePieces(i.Garment, RefitCore.DefaultKeepShape(i.Garment)),
                                               log: log.Add, warn: log.Add, trace: Trace);
                return RefitCore.MatchSkinToBody(studio, i.Source, i.Target, i.Uv, false, i.PluginLog);
            }
            finally { BodyRetarget.PushTrace.Value = null; }
        }
        var planned = tuning != null ? BodyRetarget.WithTuning(tuning, Run) : Run();
        // The solve's own nodes: its welds part the cloth from the skin the swap replaces.
        var sets = BodyRetarget.Sets.From(i.Garment, new HashSet<int>(),
                                          BodyRetarget.PartedSkinMeshes(i.Garment, [i.Pair], "_top", true, null));
        Assert.NotNull(final);
        Assert.Equal(sets.NodeCount, final!.Length);
        // The last push-out's: a kept piece let go runs it a second time.
        var taken = fitted is { } f ? Array.ConvertAll(f, x => x > 0f) : new bool[sets.NodeCount];
        Assert.Equal(sets.NodeCount, taken.Length);
        return new Refit(planned, SkinTuckSheet.Laid(i.Bytes, planned), sets, final, taken, log);
    }

    /// <summary>The game-gear refit of the same top onto the same body, laid: the yardstick for what refitting the game's
    /// own garments already turns over and stretches.</summary>
    private static ModelParts GamePath(Inputs i)
        => SkinTuckSheet.Laid(i.Bytes, RefitCore.PlanRefit(i.Garment, i.Bytes, [i.Pair], "_top", across: true, gameGear: true, out _));

    private static Vector3 At(BodyRetarget.Sets sets, Vec3[] delta, int n)
        => new(sets.NodeAt[n].X + delta[n].X, sets.NodeAt[n].Y + delta[n].Y, sets.NodeAt[n].Z + delta[n].Z);

    private static Vector3 Authored(BodyRetarget.Sets sets, int n) => new(sets.NodeAt[n].X, sets.NodeAt[n].Y, sets.NodeAt[n].Z);

    private static bool InBustBand(Vector3 p) => p.Y is > 1.12f and < 1.32f && p.Z > 0.03f;

    /// <summary>The blouse's cloth nodes over the front of the bust.</summary>
    private static List<int> BustFront(ModelParts garment, BodyRetarget.Sets sets)
    {
        var blouse = new HashSet<int>();
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && p.Material.EndsWith(Blouse, StringComparison.Ordinal)))
            foreach (int v in part.Triangles) blouse.Add(sets.NodeOf[v]);
        return sets.ClothNodes.Where(n => blouse.Contains(n) && InBustBand(Authored(sets, n))).ToList();
    }

    /// <summary>How deep each of <paramref name="nodes"/> ends inside the new body (positive: inside); NaN with no reading.</summary>
    private static Dictionary<int, float> Depths(Refit r, IEnumerable<int> nodes, BodySurface body)
        => nodes.ToDictionary(n => n, n => -SkinTuckSheet.Standoff(At(r.Sets, r.Final, n), body));

    /// <summary>
    /// Amendment 4: that "nothing inside" is read off the new body and not off missing readings. Its skin's open-edge
    /// vertices over the bust, printed, and how many of the given nodes read on it away from any such edge.
    /// </summary>
    private int Readable(Refit r, IEnumerable<int> nodes, string target)
    {
        var model = ModelPartReader.Read(File.ReadAllBytes(target))!;
        var edges = BodyRetarget.SkinEdges.Of(model);
        var body = new BodySurface(model, 0.01f);
        int open = body.SkinVertices.Count(v => InBustBand(body.PositionOf(v)) && edges.OnBoundary(v));
        int readable = nodes.Count(n => body.Nearest(At(r.Sets, r.Final, n), 0.03f, out var h) && !BodyRetarget.PastEdge(h, edges));
        output.WriteLine($"  the new body's open-edge vertices over the bust {open}; bust nodes read on it {readable}");
        return readable;
    }

    /// <summary>The written blouse's points over the bust more than 5 mm inside the new body.</summary>
    private static int WrittenInside(byte[] model, BodySurface body, out float deepest)
    {
        var read = ModelPartReader.Read(model)!;
        var verts = read.Parts.Where(p => p.Island < 0 && p.Material.EndsWith(Blouse, StringComparison.Ordinal))
                        .SelectMany(p => p.Triangles).Distinct();
        int inside = 0;
        deepest = float.NegativeInfinity;
        foreach (int v in verts)
        {
            var p = new Vector3(read.Positions[v * 3], read.Positions[v * 3 + 1], read.Positions[v * 3 + 2]);
            if (!InBustBand(p)) continue;
            float depth = -SkinTuckSheet.Standoff(p, body);
            if (float.IsNaN(depth)) continue;
            deepest = MathF.Max(deepest, depth);
            if (depth > 0.005f) inside++;
        }
        return inside;
    }

    public static TheoryData<string> Paths => new() { "auto", "studio" };

    [LocalDataTheory(LocalData.GameData, Bibo, LocalData.DeiGratiaPack)]
    [MemberData(nameof(Paths))]
    [Trait("Plan", "AC-open-patch-authored-1")]
    [Trait("Plan", "AC-open-patch-authored-2")]
    [Trait("Plan", "AC-open-patch-authored-3")]
    [Trait("Plan", "AC-open-patch-authored-8")]
    [Trait("Plan", "EDGE-open-patch-authored-10")]
    [Trait("Plan", "EDGE-open-patch-authored-12")]
    [Trait("Plan", "EDGE-open-patch-authored-16")]
    [Trait("Plan", "EDGE-open-patch-authored-19")]
    public void Dei_Gratia_onto_Bibo_Nude_M_keeps_the_bust_inside_the_blouse(string path)
    {
        var i = Read(Bibo, NudeM);
        // The automatic refit sends a top that reaches no other body part through its chest alone.
        var bones = new HashSet<string>(SecondSkinWriter.Parse(i.Bytes).BoneNames, StringComparer.Ordinal);
        var pick = new AutoRefitDecisions.Pick(VanillaBodyCatalog.Key, i.Vanilla.For("_top", "0201")[0],
                                               BodySizeMatch.Confidence.Guess, false);
        Assert.Empty(AutoRefitWatcher.OtherParts(i.Garment, "_top", "0201", pick, i.Vanilla,
                                                 (s, options) => BodySizeMatch.Rank(i.Garment, options, i.Vanilla.PathOf, bones)));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var off = Plan(i, path, Before);
        long offMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var on = Plan(i, path);
        long onMs = clock.ElapsedMilliseconds;
        var body = new BodySurface(ModelPartReader.Read(File.ReadAllBytes(i.Target))!, 0.01f);
        var bust = BustFront(i.Garment, on.Sets);
        var inside = bust.Where(n => Vector3.Distance(Authored(on.Sets, n), Inside) < 1e-4f
                                     || Vector3.Distance(Authored(on.Sets, n), Inside with { X = -Inside.X }) < 1e-4f).ToList();
        Assert.Equal(2, inside.Count);
        var depthOff = Depths(off, bust, body);
        var depthOn = Depths(on, bust, body);
        var deepBefore = depthOff.Where(kv => kv.Value > 0.005f).Select(kv => kv.Key).ToList();
        int fitted = on.Fitted.Count(f => f);
        output.WriteLine($"{path}: fitted {fitted}; pushed {off.Plan.Report.Pushed} -> {on.Plan.Report.Pushed}; "
                         + $"bust >5 mm inside {deepBefore.Count} -> {depthOn.Values.Count(d => d > 0.005f)}, "
                         + $"deepest {depthOff.Values.Max() * 1000:0.0} -> {depthOn.Values.Max() * 1000:0.0} mm; "
                         + $"plan {offMs} -> {onMs} ms");

        // The trap as reported.
        Assert.True(deepBefore.Count >= 10, "the fixture no longer shows the bust through the blouse");
        // [EDGE-open-patch-authored-19] Those points read on the game's body as worn — its bra, which fills the hole its
        // skin has there — never on the hole's rim, and some on the bra itself.
        var source = ModelPartReader.Read(File.ReadAllBytes(i.Source))!;
        var worn = BodySurface.OfBodyWorn(source, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(source)));
        var wornEdges = BodyRetarget.SkinEdges.OfWorn(source);
        var bra = source.Parts.Where(p => p.Island < 0 && BodySurface.IsSmallclothes(p.Material)).SelectMany(p => p.Triangles).ToHashSet();
        int onBra = 0;
        foreach (int n in deepBefore)
        {
            Assert.True(worn.Nearest(Authored(on.Sets, n), BodyRetarget.AuthoredProbeRange, out var hit), $"node {n} reads nothing");
            Assert.False(BodyRetarget.PastEdge(hit, wornEdges), $"node {n} lands past the worn body's edge");
            if (bra.Contains(hit.A) && bra.Contains(hit.B) && bra.Contains(hit.C)) onBra++;
        }
        Assert.True(onBra > 0, "no deep point reads on the game's bra");

        // [AC-open-patch-authored-1] None of it through, inside the pass and on the written model; every point that was
        // through came in by the rule.
        Assert.True(Readable(on, bust, i.Target) >= bust.Count / 2, "too few bust points read on the new body to judge");
        Assert.All(depthOn, kv => Assert.True(!(kv.Value > 0.005f), $"node {kv.Key} {kv.Value * 1000:0.0} mm inside"));
        int written = WrittenInside(on.Plan.Model, body, out float writtenDeepest);
        output.WriteLine($"  written blouse >5 mm inside over the bust: {written}, deepest {writtenDeepest * 1000:0.0} mm");
        Assert.Equal(0, written);
        foreach (int n in deepBefore) Assert.True(on.Fitted[n], $"node {n}, {depthOff[n] * 1000:0.0} mm in before, was not taken in");
        // [EDGE-open-patch-authored-12] The point the game's body reads 3.0 mm inside came in too, within the slack.
        foreach (int n in inside)
        {
            Assert.True(on.Fitted[n]);
            output.WriteLine($"  3.0 mm under the game's bra: node {n} {depthOff[n] * 1000:0.0} -> {depthOn[n] * 1000:0.0} mm inside");
            Assert.True(depthOn[n] <= 0.005f);
        }

        // [AC-open-patch-authored-2] Nothing folded, and nothing turned over or stretched against the authored garment
        // beyond what the game-gear refit of the same top already does.
        var game = GamePath(i);
        Assert.Equal(0, on.Plan.Report.Folded);
        Assert.Equal(0, BodyRetarget.FoldedTriangles(on.Sets, on.Final));
        Assert.True(SkinTuckSheet.FoldedAgainstAuthored(i.Garment, on.Laid) <= SkinTuckSheet.FoldedAgainstAuthored(i.Garment, game));
        Assert.True(SkinTuckSheet.ClothStretch(i.Garment, on.Laid, s => output.WriteLine("  stretched " + s))
                    <= SkinTuckSheet.ClothStretch(i.Garment, game));

        // [AC-open-patch-authored-3] Local: far cloth where it was; near cloth the rule did not take in no further off
        // than measured and no deeper.
        var taken = Enumerable.Range(0, on.Sets.NodeCount).Where(n => on.Fitted[n]).Select(n => Authored(on.Sets, n)).ToList();
        float far = 0f, further = 0f, deeper = 0f;
        int nearChanged = 0;
        foreach (int n in on.Sets.ClothNodes)
        {
            if (on.Fitted[n]) continue;
            var a = Authored(on.Sets, n);
            float moved = Vector3.Distance(At(on.Sets, on.Final, n), At(off.Sets, off.Final, n));
            if (taken.All(t => Vector3.Distance(t, a) > 0.10f)) { far = MathF.Max(far, moved); continue; }
            if (moved > 1e-4f) nearChanged++;
            float sOn = SkinTuckSheet.Standoff(At(on.Sets, on.Final, n), body);
            float sOff = SkinTuckSheet.Standoff(At(off.Sets, off.Final, n), body);
            if (float.IsNaN(sOn) || float.IsNaN(sOff)) continue;
            further = MathF.Max(further, sOn - sOff);
            deeper = MathF.Max(deeper, sOff - sOn);
        }
        output.WriteLine($"  far cloth moved at most {far * 1000:0.0} mm; near cloth changed {nearChanged}, further off "
                         + $"{further * 1000:0.0} mm, deeper {deeper * 1000:0.0} mm");
        Assert.True(far <= 0.002f, $"far cloth moved {far * 1000:0.0} mm");
        Assert.True(further <= NearFurtherOff, $"near cloth {further * 1000:0.0} mm further off");
        Assert.True(deeper <= 0.001f, $"near cloth {deeper * 1000:0.0} mm deeper");

        // [AC-open-patch-authored-8] Said once, with the count the report gives; never without the rule.
        var lines = on.Log.Where(l => l.StartsWith("fitted: ", StringComparison.Ordinal)).ToList();
        Assert.Single(lines);
        Assert.StartsWith($"fitted: {on.Plan.Report.OpenEdgeFitted} ", lines[0]);
        Assert.Equal(fitted, on.Plan.Report.OpenEdgeFitted);
        Assert.True(fitted >= 1);
        Assert.DoesNotContain(off.Log, l => l.StartsWith("fitted: ", StringComparison.Ordinal));

        // [EDGE-open-patch-authored-16] The new body's own open edges under the points taken in, for the record.
        var targetModel = ModelPartReader.Read(File.ReadAllBytes(i.Target))!;
        var targetEdges = BodyRetarget.SkinEdges.Of(targetModel);
        int onEdge = Enumerable.Range(0, on.Sets.NodeCount).Count(n => on.Fitted[n]
            && body.Nearest(At(on.Sets, on.Final, n), BodyRetarget.PushProbeRange, out var h)
            && Vector3.Dot(At(on.Sets, on.Final, n) - h.Point, h.Normal) < 0f && BodyRetarget.PastEdge(h, targetEdges));
        output.WriteLine($"  points taken in landing inside past the new body's own open edge: {onEdge}");

        // [EDGE-open-patch-authored-10] The same refit twice is the same file.
        Assert.Equal(SkinTuckSheet.Sha(on.Plan.Model), SkinTuckSheet.Sha(Plan(i, path).Plan.Model));
    }

    public static TheoryData<string, string, string> OtherBodies => new()
    {
        { Rue, SmallclothesM, "auto" }, { Rue, SmallclothesM, "studio" },
        { Yab, SmallclothesM, "auto" }, { Yab, SmallclothesM, "studio" },
        { Neolithe, NeolitheSfwM, "auto" }, { Neolithe, NeolitheSfwM, "studio" },
    };

    [LocalDataTheory(LocalData.GameData, Bibo, Rue, Yab, Neolithe, LocalData.DeiGratiaPack)]
    [MemberData(nameof(OtherBodies))]
    [Trait("Plan", "AC-open-patch-authored-7")]
    [Trait("Plan", "EDGE-open-patch-authored-24")]
    public void Dei_Gratia_onto_other_bodies_is_no_worse(string modNeed, string rel, string path)
    {
        var i = Read(modNeed, rel);
        var off = Plan(i, path, Before);
        var on = Plan(i, path);
        var body = new BodySurface(ModelPartReader.Read(File.ReadAllBytes(i.Target))!, 0.01f);
        var bust = BustFront(i.Garment, on.Sets);
        Assert.True(Readable(on, bust, i.Target) >= bust.Count / 2, "too few bust points read on the new body to judge");
        var depthOff = Depths(off, bust, body).Values.Where(d => !float.IsNaN(d)).ToList();
        var depthOn = Depths(on, bust, body).Values.Where(d => !float.IsNaN(d)).ToList();
        int overOff = depthOff.Count(d => d > 0.005f), overOn = depthOn.Count(d => d > 0.005f);
        var game = GamePath(i);
        int turnedOn = SkinTuckSheet.FoldedAgainstAuthored(i.Garment, on.Laid), turnedOff = SkinTuckSheet.FoldedAgainstAuthored(i.Garment, off.Laid);
        int stretchedOn = SkinTuckSheet.ClothStretch(i.Garment, on.Laid), stretchedOff = SkinTuckSheet.ClothStretch(i.Garment, off.Laid);
        int turnedGame = SkinTuckSheet.FoldedAgainstAuthored(i.Garment, game), stretchedGame = SkinTuckSheet.ClothStretch(i.Garment, game);
        output.WriteLine($"{Path.GetFileName(LocalData.Path(modNeed))} {path}: fitted {on.Fitted.Count(f => f)}; pushed "
                         + $"{off.Plan.Report.Pushed} -> {on.Plan.Report.Pushed}; bust >5 mm inside {overOff} -> {overOn}, deepest "
                         + $"{depthOff.Max() * 1000:0.0} -> {depthOn.Max() * 1000:0.0} mm; folded {off.Plan.Report.Folded} -> "
                         + $"{on.Plan.Report.Folded}; turned {turnedOff} -> {turnedOn} (game path {turnedGame}); stretched "
                         + $"{stretchedOff} -> {stretchedOn} (game path {stretchedGame})");

        // [AC-open-patch-authored-7] No more through, nothing deeper, no more folds; turned over and stretched against the
        // authored garment no more than the refit without the rule or the game-gear refit, or as accepted.
        Assert.True(overOn <= overOff, $"{overOn} bust points >5 mm inside against {overOff}");
        Assert.True(depthOn.Max() <= depthOff.Max() + 1e-4f, $"deepest {depthOn.Max() * 1000:0.0} mm against {depthOff.Max() * 1000:0.0}");
        Assert.True(on.Plan.Report.Folded <= off.Plan.Report.Folded);
        Assert.True(BodyRetarget.FoldedTriangles(on.Sets, on.Final)
                    <= BodyRetarget.FoldedTriangles(off.Sets, off.Final) + AcceptedFolds.GetValueOrDefault(modNeed));
        Assert.True(turnedOn <= Math.Max(turnedOff, turnedGame), $"{turnedOn} turned over");
        Assert.True(stretchedOn <= Math.Max(Math.Max(stretchedOff, stretchedGame), AcceptedStretch.GetValueOrDefault(modNeed)),
                    $"{stretchedOn} stretched");

        // [EDGE-open-patch-authored-24] The points taken in are the ones taken in onto Bibo+: read off the body the garment
        // was made on, never the new one.
        var bibo = Plan(Read(Bibo, NudeM), path);
        Assert.Equal(bibo.Fitted, on.Fitted);
    }

    [LocalDataFact(LocalData.GameData, Bibo, LocalData.DeiGratiaPack)]
    [Trait("Plan", "AC-open-patch-authored-5")]
    [Trait("Plan", "EDGE-open-patch-authored-9")]
    public void Dei_Gratia_game_gear_and_clear_body_refits_are_unchanged()
    {
        var i = Read(Bibo, NudeM);
        BodyRetarget.Planned Game() => RefitCore.PlanRefit(i.Garment, i.Bytes, [i.Pair], "_top", across: true, gameGear: true, out _);
        BodyRetarget.Planned Clear() => BodyRetarget.Plan(i.Garment, i.Bytes, [i.Pair], "_top", held: new HashSet<int>(),
                                                          replaceSkin: true, acrossBodies: true, clearBody: true, cutHidden: true,
                                                          keepShape: RefitCore.ShapePieces(i.Garment, RefitCore.DefaultKeepShape(i.Garment)),
                                                          tuckSlot: "_top");
        Assert.Equal(SkinTuckSheet.Sha(BodyRetarget.WithTuning(Before, Game).Model), SkinTuckSheet.Sha(Game().Model));
        Assert.Equal(SkinTuckSheet.Sha(BodyRetarget.WithTuning(Before, Clear).Model), SkinTuckSheet.Sha(Clear().Model));
    }
}
