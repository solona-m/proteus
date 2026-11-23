extern alias RefitCli;
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
/// On demand: the layer order's skin floor measured on Slay Ride and beside it — the references the Slay Ride pin holds
/// to, what the floor reaches, and other garments and big busts with the floor on and off.
/// </summary>
[Trait("Suite", "Diagnostic")]
[Trait("Speed", "Slow")]
public sealed class SlayRideNippleClipDiagnosticTests(ITestOutputHelper o) : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "proteus-slayride-diag-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Sirius = LocalData.Mods + ":Sirius - by Solona";
    private const string OldThing = LocalData.Mods + ":This Old Thing - by Solona";
    private const string YabLarge = "files\\chest - smallclothes\\large\\chara\\equipment\\e0000\\model\\c0201e0000_top.mdl";
    private const string BiboLarge = "breasts - small clothes\\nude - large\\chara\\equipment\\e0000\\model\\c0201e0000_top.mdl";

    /// <summary>One headless refit, as the size set refits; the written model.</summary>
    private ModelParts Refit(string garment, string fromRoot, string from, string bodyRoot, string to, string tune)
    {
        string outDir = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var args = new List<string>
        {
            "--garment", garment, "--from-root", fromRoot, "--from", from, "--body-root", bodyRoot, "--to", to + "=Out",
            "--uvmaps", LocalData.Path(LocalData.UvMaps), "--whole", "--out-dir", outDir,
        };
        if (tune.Length > 0) args.AddRange(["--tune", tune]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var console = Console.Out;
        int exit;
        Console.SetOut(stdout);   // the report it prints is not read here
        try { exit = RefitCli::Proteus.Refit.Program.Dispatch([.. args], stdout, stderr); }
        finally { Console.SetOut(console); }
        Assert.True(exit == 0, stderr.ToString());
        return ModelPartReader.Read(File.ReadAllBytes(Path.Combine(outDir, "Out.mdl")))!;
    }

    private static string SlayRide => Path.Combine(LocalData.Path(LocalData.SlayRideMod), SlayRideNippleClipLocalTests.Garment);

    private static readonly (string Label, string Root, string Rel)[] Sizes =
    [
        ("RueS", LocalData.RueMod, SlayRideNippleClipLocalTests.Rue("small")),
        ("RueM", LocalData.RueMod, SlayRideNippleClipLocalTests.Rue("medium")),
        ("RueL", LocalData.RueMod, SlayRideNippleClipLocalTests.Rue("large")),
        ("NeoL", LocalData.NeolitheMod, SlayRideNippleClipLocalTests.NeolitheL),
    ];

    /// <summary>The bodice parts of a garment: its cloth laid on its own skin, no deeper than 2 mm through it.</summary>
    private static List<string> LaidOn(ModelParts original)
        => NippleClipMeasure.Measure(original).Deepest
                            .Where(kv => MathF.Max(kv.Value.Left, kv.Value.Right) <= SlayRideNippleClipLocalTests.LaidOn)
                            .Select(kv => kv.Key).ToList();

    /// <summary>The references <see cref="SlayRideNippleClipLocalTests.References"/> holds, printed to paste.</summary>
    [LocalDataFact(LocalData.SlayRideMod, LocalData.NeolitheMod, LocalData.RueMod, LocalData.UvMaps)]
    [Trait("Plan", "AC-bodice-nipple-clip-1")]
    [Trait("Plan", "AC-bodice-nipple-clip-2")]
    [Trait("Plan", "AC-bodice-nipple-clip-3")]
    [Trait("Plan", "AC-bodice-nipple-clip-4")]
    [Trait("Plan", "AC-bodice-nipple-clip-6")]
    [Trait("Plan", "AC-bodice-nipple-clip-7")]
    public void The_references_and_the_floor_on_Slay_Ride()
    {
        var original = ModelPartReader.Read(File.ReadAllBytes(SlayRide))!;
        var bodice = LaidOn(original);
        var pairs = NippleClipMeasure.CardPairs(original, bodice, SlayRideNippleClipLocalTests.Cards);
        o.WriteLine($"bodice parts {string.Join(", ", bodice)}; {pairs.Count} fur-over-bodice pairs");
        string from = SlayRideNippleClipLocalTests.MadeOn, neolithe = LocalData.Path(LocalData.NeolitheMod);
        foreach (var (label, root, rel) in Sizes)
        {
            var noLayer = Refit(SlayRide, neolithe, from, LocalData.Path(root), rel, "NoLayerOrder");
            var before = Refit(SlayRide, neolithe, from, LocalData.Path(root), rel, "NoLayerFloor");
            var floored = Refit(SlayRide, neolithe, from, LocalData.Path(root), rel, "");
            float Worst(ModelParts m) => NippleClipMeasure.Measure(m).Worst(bodice) * 1000f;
            var behind = NippleClipMeasure.CardsBehind(original, before, pairs, SlayRideNippleClipLocalTests.Cards);
            var behindNow = NippleClipMeasure.CardsBehind(original, floored, pairs, SlayRideNippleClipLocalTests.Cards);
            o.WriteLine($"[\"{label}\"] = ({Worst(noLayer):F1}f, {Worst(before):F1}f, {NippleClipMeasure.TurnedOver(original, before)}, " +
                        $"{behind.Count(b => b > 0.0005f)}, {behind.Max() * 1000f:F1}f),");
            o.WriteLine($"   floor: {Worst(floored):F1} mm, turned {NippleClipMeasure.TurnedOver(original, floored)}, " +
                        $"cards behind {behindNow.Count(b => b > 0.0005f)}, worst {behindNow.Max() * 1000f:F1} mm; " +
                        $"tangential {Tangential(noLayer, floored, bodice)}");
            string ByPart(ModelParts m) => string.Join(" ", NippleClipMeasure.TurnedOverByPart(original, m).Select(kv => $"{kv.Key}:{kv.Value}"));
            o.WriteLine($"   turned by part: before {ByPart(before)}; floor {ByPart(floored)}; no layer order {ByPart(noLayer)}");
            // The layer order off is the floor's reference: within 0.5 mm of it, or the floor is not the fix.
            Assert.True(Worst(floored) <= 3f && Worst(floored) <= Worst(noLayer) + 0.5f,
                        $"{label}: {Worst(floored):F1} mm with the floor, {Worst(noLayer):F1} with no layer order");
            // The pin's checks, on every size — the regular suite runs Rue+ Yiggle Medium and Large only.
            if (label == "RueS")
            {
                Assert.True(Worst(floored) <= 2f, $"RueS: {Worst(floored):F1} mm, no longer clear");
                Assert.True(Worst(floored) <= SlayRideNippleClipLocalTests.References["RueS"].Before + 0.5f,
                            $"RueS: {Worst(floored):F1} mm, deeper than before");
            }
            else SlayRideNippleClipLocalTests.BodiceInFront(label, original, floored, o.WriteLine);
            SlayRideNippleClipLocalTests.NoMoreFolds(label, original, floored, o.WriteLine);
            if (label != "RueS") SlayRideNippleClipLocalTests.FurInFront(label, original, floored, o.WriteLine);
        }
    }

    /// <summary>Near-tip bodice vertices' sideways move (along the skin, not off it) between two refits: worst and median.</summary>
    private static string Tangential(ModelParts a, ModelParts b, List<string> parts)
    {
        var (left, right, _) = NippleClipMeasure.Tips(b);
        var moves = new List<float>();
        foreach (var part in NippleClipMeasure.Cloth(b).Where(p => parts.Contains(p.Label)))
            foreach (int v in part.Triangles.Distinct())
            {
                var pb = new Vector3(b.Positions[v * 3], b.Positions[v * 3 + 1], b.Positions[v * 3 + 2]);
                if (Vector3.Distance(pb, left) >= NippleClipMeasure.Reach && Vector3.Distance(pb, right) >= NippleClipMeasure.Reach) continue;
                var pa = new Vector3(a.Positions[v * 3], a.Positions[v * 3 + 1], a.Positions[v * 3 + 2]);
                var n = new Vector3(b.Normals[v * 3], b.Normals[v * 3 + 1], b.Normals[v * 3 + 2]);
                var d = pb - pa;
                if (n.LengthSquared() > 1e-12f) { n = Vector3.Normalize(n); d -= n * Vector3.Dot(d, n); }
                moves.Add(d.Length());
            }
        if (moves.Count == 0) return "none";
        moves.Sort();
        return $"worst {moves[^1] * 1000f:F1} mm, median {moves[moves.Count / 2] * 1000f:F1} mm over {moves.Count}";
    }

    [LocalDataFact(LocalData.SlayRideMod, LocalData.NeolitheMod, LocalData.RueMod, LocalData.UvMaps)]
    [Trait("Plan", "AC-bodice-nipple-clip-9")]
    public void Only_the_floors_reach_changes()
    {
        // Rue+ Yiggle Large with the floor on and off: what moves is the floor's doing; most of the garment never moves.
        var off = Refit(SlayRide, LocalData.Path(LocalData.NeolitheMod), SlayRideNippleClipLocalTests.MadeOn,
                        LocalData.Path(LocalData.RueMod), SlayRideNippleClipLocalTests.Rue("large"), "NoLayerFloor");
        var on = Refit(SlayRide, LocalData.Path(LocalData.NeolitheMod), SlayRideNippleClipLocalTests.MadeOn,
                       LocalData.Path(LocalData.RueMod), SlayRideNippleClipLocalTests.Rue("large"), "");
        var cloth = NippleClipMeasure.Cloth(on).SelectMany(p => p.Triangles).Distinct().ToList();
        int changed = cloth.Count(v => on.Positions[v * 3] != off.Positions[v * 3] || on.Positions[v * 3 + 1] != off.Positions[v * 3 + 1]
                                       || on.Positions[v * 3 + 2] != off.Positions[v * 3 + 2]);
        o.WriteLine($"{changed} of {cloth.Count} cloth vertices moved by the floor");
        Assert.True(changed > 0, "the floor should have reached the bodice");
        Assert.True(changed * 2 <= cloth.Count, $"{changed} of {cloth.Count} cloth vertices moved: the floor reached too far");
    }

    /// <summary>Bodice triangles the floor may newly turn over on another body's big bust (it turned 1 on YAB+ Large).</summary>
    private const int BigBustBodiceSlack = 2;

    public static TheoryData<string, string, string, string, string, string> Others() => new()
    {
        // garment mod, garment rel, made-on body mod, its rel or auto, target body mod, target rel
        { LocalData.SlayRideMod, SlayRideNippleClipLocalTests.Garment, LocalData.NeolitheMod, SlayRideNippleClipLocalTests.MadeOn, Yab, YabLarge },
        { LocalData.SlayRideMod, SlayRideNippleClipLocalTests.Garment, LocalData.NeolitheMod, SlayRideNippleClipLocalTests.MadeOn, Bibo, BiboLarge },
        { Sirius, "size/Sirius/L/chara/equipment/e0154/model/c0201e0154_top.mdl", Yab, "auto", LocalData.RueMod, SlayRideNippleClipLocalTests.Rue("large") },
        { OldThing, "size/neolithe m/chara/equipment/e6255/model/c0201e6255_top.mdl", LocalData.NeolitheMod, "auto", LocalData.RueMod, SlayRideNippleClipLocalTests.Rue("large") },
    };

    [LocalDataTheory(LocalData.SlayRideMod, LocalData.NeolitheMod, LocalData.RueMod, Yab, Bibo, Sirius, OldThing, LocalData.UvMaps)]
    [MemberData(nameof(Others))]
    [Trait("Plan", "AC-bodice-nipple-clip-8")]
    [Trait("Plan", "EDGE-bodice-nipple-clip-9")]
    public void Layered_garments_and_big_busts_are_no_worse(string mod, string rel, string fromMod, string from, string toMod, string to)
    {
        string garment = Path.Combine(LocalData.Path(mod), rel);
        var original = ModelPartReader.Read(File.ReadAllBytes(garment))!;
        var off = Refit(garment, LocalData.Path(fromMod), from, LocalData.Path(toMod), to, "NoLayerFloor");
        var on = Refit(garment, LocalData.Path(fromMod), from, LocalData.Path(toMod), to, "");
        var onDepths = NippleClipMeasure.Measure(on).Deepest;
        // The cloth laid on the skin, where the nipple disc is read; none on a garment its author sank into the body
        // (This Old Thing's cloth sits ~4 mm inside its own), which is judged by its folds and layers alone.
        var parts = LaidOn(original).Where(onDepths.ContainsKey).ToList();   // the refit can rename what it swaps
        float depthOff = parts.Count == 0 ? 0f : NippleClipMeasure.Measure(off).Worst(parts) * 1000f;
        float depthOn = parts.Count == 0 ? 0f : NippleClipMeasure.Measure(on).Worst(parts) * 1000f;
        var turnedOff = NippleClipMeasure.TurnedOverByPart(original, off);
        var turnedOn = NippleClipMeasure.TurnedOverByPart(original, on);
        int invOff = Inversions(original, off), invOn = Inversions(original, on);
        o.WriteLine($"{Path.GetFileName(rel)} -> {to}: nipple disc {depthOff:F1} -> {depthOn:F1} mm; turned " +
                    $"{string.Join(" ", turnedOff.Select(kv => $"{kv.Key}:{kv.Value}->{turnedOn.GetValueOrDefault(kv.Key)}"))}; " +
                    $"layer inversions {invOff} -> {invOn}");
        Assert.True(depthOn <= depthOff + 0.5f, $"nipple disc {depthOn:F1} mm with the floor, {depthOff:F1} without");
        if (mod == LocalData.SlayRideMod)
        {
            // Slay Ride's only covers are its fur cards, rooted in the body: the floor keeps the bodice out of the breast
            // where the cards held it "behind" them, so those pairs count as inverted — what was fixed, not a layer broken.
            // Its folds near what the pin holds: on YAB+ Large the floor that took the disc from 18.6 to 3.1 mm turned one
            // more of the bodice's 10,304 triangles (22 to 23) — the pin's own sizes, Rue+ M and L, turn none more.
            Assert.True(turnedOn["1.1"] <= turnedOff["1.1"] + BigBustBodiceSlack,
                        $"bodice: {turnedOn["1.1"]} turned over with the floor, {turnedOff["1.1"]} without");
            Assert.True(turnedOn["1.3"] <= turnedOff["1.3"] * (1f + SlayRideNippleClipLocalTests.RollTurnSlack),
                        $"fur roll: {turnedOn["1.3"]} turned over with the floor, {turnedOff["1.3"]} without");
            return;
        }
        Assert.True(turnedOn.Values.Sum() <= turnedOff.Values.Sum(),
                    $"{turnedOn.Values.Sum()} turned over with the floor, {turnedOff.Values.Sum()} without");
        Assert.True(invOn <= invOff, $"{invOn} layer inversions with the floor, {invOff} without");
    }

    /// <summary>Cloth points the author covered with another piece that ended in front of it (beyond touching).</summary>
    private static int Inversions(ModelParts original, ModelParts refit)
    {
        var sets = BodyRetarget.Sets.From(original);
        var layered = BodyRetarget.GarmentCloth(original, sets);
        var covered = BodyRetarget.CoveredPoints(sets, layered, BodyRetarget.FacesOneWay(original, sets));
        var map = NippleClipMeasure.VertexMap(original, refit);
        var at = new Vector3[sets.NodeCount];
        var known = new bool[sets.NodeCount];
        for (int v = 0; v < original.Positions.Length / 3; v++)
        {
            int n = sets.NodeOf[v], w = map[v];
            if (known[n] || w < 0) continue;
            known[n] = true;
            at[n] = new Vector3(refit.Positions[w * 3], refit.Positions[w * 3 + 1], refit.Positions[w * 3 + 2]);
        }
        int inverted = 0;
        foreach (var c in covered)
        {
            if (!known[c.Node] || !known[c.A] || !known[c.B] || !known[c.C]) continue;
            var normal = Vector3.Cross(at[c.B] - at[c.A], at[c.C] - at[c.A]);
            if (normal.LengthSquared() < 1e-16f) continue;
            normal = Vector3.Normalize(normal) * c.Facing;
            var hit = at[c.A] * (1f - c.U - c.V) + at[c.B] * c.U + at[c.C] * c.V;
            if (Vector3.Dot(hit - at[c.Node], normal) < -0.0002f) inverted++;   // beyond touching (LayerOrderTouching)
        }
        return inverted;
    }
}
