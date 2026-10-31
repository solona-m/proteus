using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Solona's "Ray", made on Neolithe XS, refitted in the Studio onto NIRÆ "Slim · Small hips" with NIRÆ's chest on
/// "Small Perky", "Use the new body's skin" and "Remove hidden skin" ticked — as reported, with a hole cut in the open
/// skin between the straps. NIRÆ ships all nine of its chests in one model and draws one by its options' <c>Atr</c>
/// switches; read whole, the author's skin was laid on whichever chest was nearest, and the drawn one cut. Needs the game
/// data and the three mods named.
/// </summary>
public class RayNiraeAtrRefitLocalTests(ITestOutputHelper output)
{
    private const string Ray = LocalData.Mods + ":Ray - by Solona";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Nirae = LocalData.Mods + ":NIRÆ body";

    // The chest is a size of its own since 1405 (BodySizeCatalog.IsShape); the legs keep NIRÆ's build and hips.
    private static string Onto(string slot) => slot == "_top" ? "Slim · Small Perky" : "Slim · Small hips";
    private static readonly Dictionary<string, List<string>> SmallPerky = new() { ["Chest"] = ["Small Perky"] };

    private sealed record Run(List<BodyRetarget.SlotPair> Pairs, BodyRetarget.Planned Plan, ModelParts Laid);

    /// <summary>The Studio's refit; with <paramref name="atr"/> off, NIRÆ read as before its switches were.</summary>
    private static Run Refit(bool atr, out BodyRetarget.BodyDraw topDraw)
    {
        var uv = new UVRemapService(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        byte[] bytes = File.ReadAllBytes(Path.Combine(LocalData.Path(Ray), "common", "3", "c0201e0504_top.mdl"));
        var garment = ModelPartReader.Read(bytes)!;
        string neoDir = LocalData.Path(Neolithe), nirDir = LocalData.Path(Nirae);
        var neo = BodySizeCatalog.Read(neoDir);
        var nir = BodySizeCatalog.Read(nirDir);

        var pairs = new List<BodyRetarget.SlotPair>();
        topDraw = null!;
        foreach (var (slot, from) in new[] { ("_top", "DEFAULT ALMOND · SFW Almond XS"), ("_dwn", "DEFAULT · Gen A Small") })
        {
            var source = neo.For(slot, "0201").First(o => o.Label == from);
            var target = nir.For(slot, "0201").First(o => o.Label == Onto(slot));
            var draw = RefitCore.MaskOf(nirDir, nir, slot, SmallPerky, RefitCore.RaceNumber("0201"))!.Onto(target);
            if (slot == "_top") topDraw = draw;
            if (!atr) draw = draw with { AtrOff = new HashSet<string>(), AtrSwitched = new HashSet<string>() };
            Assert.Null(BodyRetarget.BuildPair(slot, neo.PathOf(source), nir.PathOf(target), slot, false,
                                               RefitCore.MaskOf(neoDir, neo, slot, null, 201), draw, uv, out var pair));
            pairs.Add(pair);
        }
        var rule = RetargetInputs.StudioSkin(true, true, false, false, "_top", false);
        var plan = RetargetInputs.StudioPlan(garment, bytes, pairs, "_top", new HashSet<int>(), true, true, rule, true,
                                             RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)), false, false);
        return new Run(pairs, plan, SkinTuckSheet.Laid(bytes, plan));
    }

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static Vector3 NormalAt(ModelParts m, int v) => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);

    private static bool Skin(ModelPart p) => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material);

    /// <summary>The new chest's skin triangles the game draws under Small Perky: every part tagged with none of the
    /// names its options switch off.</summary>
    private static List<(int A, int B, int C)> GameDrawn(ModelParts body, IReadOnlySet<string> off)
    {
        uint bits = 0;
        for (int i = 0; i < body.AttributeNames.Count && i < 32; i++)
            if (off.Contains(body.AttributeNames[i])) bits |= 1u << i;
        var tris = new List<(int, int, int)>();
        foreach (var part in body.Parts.Where(p => Skin(p) && (p.AttributeMask & bits) == 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                tris.Add((part.Triangles[t], part.Triangles[t + 1], part.Triangles[t + 2]));
        return tris;
    }

    /// <summary>Drawn chest vertices the cut leaves out with no cloth over them: skin the garment's own skin does not
    /// draw, measured on the laid garment inside the pass.</summary>
    private static int VisibleCut(Run run, List<(int A, int B, int C)> drawn, out List<(int A, int B, int C)> openBand)
    {
        var body = run.Pairs[0].Target;
        var laidSkin = new BodySurface(run.Laid, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(run.Laid)));
        var edges = BodyRetarget.SkinEdges.Of(run.Laid);
        var cloth = new BodyRetarget.ClothCrossings(run.Laid, countVariants: true);
        bool Open(Vector3 p, Vector3 n) => !cloth.Over(p, n, 0.15f) && !cloth.Crowds(p, n);
        int visible = 0;
        foreach (int v in drawn.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct())
            if (!BodyRetarget.Covers(laidSkin, At(body, v), edges) && Open(At(body, v), NormalAt(body, v))) visible++;

        // The skin between the straps, in the open: what the hole was.
        openBand = drawn.Where(t =>
        {
            var c = (At(body, t.A) + At(body, t.B) + At(body, t.C)) / 3f;
            var n = NormalAt(body, t.A) + NormalAt(body, t.B) + NormalAt(body, t.C);
            return MathF.Abs(c.X) < 0.03f && c.Y is > 1.15f and < 1.27f && c.Z > 0.05f && Open(c, n);
        }).ToList();
        return visible;
    }

    /// <summary>Of <paramref name="band"/>, the triangles the written model draws no body skin within 1 mm of.</summary>
    private static int Missing(byte[] written, ModelParts body, List<(int A, int B, int C)> band)
    {
        var model = ModelPartReader.Read(written)!;
        var centroids = model.Parts.Where(Skin)
                             .SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                        .Select(t => (At(model, p.Triangles[t * 3]) + At(model, p.Triangles[t * 3 + 1])
                                                                      + At(model, p.Triangles[t * 3 + 2])) / 3f))
                             .Where(c => MathF.Abs(c.X) < 0.05f && c.Y is > 1.13f and < 1.29f)
                             .ToList();
        return band.Count(t =>
        {
            var c = (At(body, t.A) + At(body, t.B) + At(body, t.C)) / 3f;
            return !centroids.Any(o => Vector3.Distance(o, c) <= 0.001f);
        });
    }

    [LocalDataFact(LocalData.GameData, Ray, Neolithe, Nirae)]
    [Trait("Speed", "Slow")]
    [Trait("Plan", "AC-body-atr-switches-1")]
    [Trait("Plan", "AC-body-atr-switches-2")]
    [Trait("Plan", "EDGE-body-atr-switches-20")]
    public void Ray_onto_NIRAE_small_perky_keeps_the_open_skin()
    {
        var fixedRun = Refit(atr: true, out var draw);
        var control = Refit(atr: false, out _);
        var top = fixedRun.Pairs[0];
        var legs = fixedRun.Pairs[1];

        // [AC-body-atr-switches-2] The chest read as Small Perky draws it.
        string[] otherChests = ["flat", "small", "medium", "large", "saggy", "perky", "earth", "mireya"];
        foreach (string chest in otherChests) Assert.Contains("atrx_cb_chest_" + chest, top.TargetHidden!);
        Assert.Contains("atrx_cb_nip_inv", top.TargetHidden!);
        Assert.Contains("atrx_cb_pierce_navel", top.TargetHidden!);
        Assert.Contains("atrx_cb_pierce_nip", top.TargetHidden!);
        Assert.Equal(new HashSet<string> { "atrx_cb_chest_smallperky", "atrx_cb_nip_soft" }, top.TargetSwitched);
        // [EDGE-body-atr-switches-20] The legs the top reaches onto, as the defaults draw them.
        Assert.Contains("atrx_cb_vulva_closed", legs.TargetHidden!);

        // [AC-body-atr-switches-1] The same game-drawn skin judged in both runs: the hole is there without the switches,
        // and gone with them.
        var drawnFixed = GameDrawn(top.Target, draw.AtrOff);
        var drawnControl = GameDrawn(control.Pairs[0].Target, draw.AtrOff);
        Assert.Equal(drawnFixed.Count, drawnControl.Count);
        int visibleControl = VisibleCut(control, drawnControl, out var bandControl);
        int visibleFixed = VisibleCut(fixedRun, drawnFixed, out var bandFixed);
        int missingControl = Missing(control.Plan.Model, control.Pairs[0].Target, bandControl);
        int missingFixed = Missing(fixedRun.Plan.Model, top.Target, bandFixed);
        var swap = fixedRun.Plan.Report.Swap!.Value;
        output.WriteLine($"visible cut: {visibleControl} -> {visibleFixed} (of {drawnFixed.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Count()} drawn vertices); "
                         + $"open band triangles missing from the model: {missingControl}/{bandControl.Count} -> {missingFixed}/{bandFixed.Count}; "
                         + $"folded {control.Plan.Report.Folded} -> {fixedRun.Plan.Report.Folded}; worst push "
                         + $"{control.Plan.Report.WorstPush * 1000:0.0} -> {fixedRun.Plan.Report.WorstPush * 1000:0.0} mm; "
                         + $"swap added {swap.Added}, cut {swap.Cut}");
        Assert.True(visibleControl >= 300, $"the control shows {visibleControl} visible cut vertices: the harness misses the hole");
        Assert.True(missingControl > 0, "the control's model draws every open triangle between the straps");
        Assert.Equal(0, visibleFixed);
        Assert.NotEmpty(bandFixed);
        Assert.Equal(0, missingFixed);

        // Nothing switched rides on the skin brought in, nothing folded, no deeper push, nothing more than the drawn skin.
        var written = ModelPartReader.Read(fixedRun.Plan.Model)!;
        uint switchedBits = 0;
        for (int i = 0; i < written.AttributeNames.Count && i < 32; i++)
            if (written.AttributeNames[i].StartsWith("atrx_cb_chest_", StringComparison.Ordinal)
                || written.AttributeNames[i] == "atrx_cb_nip_soft") switchedBits |= 1u << i;
        Assert.DoesNotContain(written.Parts, p => (p.AttributeMask & switchedBits) != 0);
        Assert.Equal(0, fixedRun.Plan.Report.Folded);
        Assert.True(fixedRun.Plan.Report.WorstPush <= 0.012f, $"worst push {fixedRun.Plan.Report.WorstPush * 1000:0.0} mm");
        int drawnSkin = fixedRun.Pairs.Sum(p => p.Target.Parts.Where(Skin).Sum(q => q.Triangles.Length / 3));
        Assert.True(swap.Added <= drawnSkin, $"{swap.Added} triangles added of {drawnSkin} drawn");
    }

    [LocalDataFact(Nirae)]
    [Trait("Plan", "AC-body-atr-switches-2")]
    [Trait("Plan", "AC-body-atr-switches-6")]
    public void NIRAE_manifest_resolves_its_chest_choices()
    {
        string dir = LocalData.Path(Nirae);
        var nir = BodySizeCatalog.Read(dir);
        var defaults = RefitCore.MaskOf(dir, nir, "_top", null, 201)!;
        var perky = RefitCore.MaskOf(dir, nir, "_top", SmallPerky, 201)!;
        var legs = RefitCore.MaskOf(dir, nir, "_dwn", null, 201)!;
        string[] chests = ["flat", "small", "smallperky", "medium", "large", "saggy", "perky", "earth", "mireya", "champs"];

        // Under its defaults NIRÆ draws the large chest, soft nipples, no piercings.
        Assert.Equal(chests.Where(c => c != "large").Select(c => "atrx_cb_chest_" + c).Order(),
                     defaults.AtrOff.Where(a => a.StartsWith("atrx_cb_chest_", StringComparison.Ordinal)).Order());
        Assert.DoesNotContain("atrx_cb_nip_soft", defaults.AtrOff);
        Assert.Contains("atrx_cb_nip_inv", defaults.AtrOff);
        Assert.Contains("atrx_cb_pierce_navel", defaults.AtrOff);
        Assert.Contains("atrx_cb_pierce_nip", defaults.AtrOff);
        // Small Perky draws its own.
        Assert.DoesNotContain("atrx_cb_chest_smallperky", perky.AtrOff);
        Assert.Contains("atrx_cb_chest_large", perky.AtrOff);
        // The legs: the closed vulva off by default.
        Assert.Contains("atrx_cb_vulva_closed", legs.AtrOff);
        Assert.DoesNotContain("atrx_cb_vulva_open", legs.AtrOff);
        // Hips the options switch, though no model of NIRÆ's carries them.
        Assert.Contains(legs.AtrSwitched, a => a.StartsWith("atrx_cb_hips_", StringComparison.Ordinal));
    }

    [LocalDataFact(Nirae)]
    public void Of_the_installed_bodies_only_NIRAE_is_never_read_as_a_source()
    {
        var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
        var left = bodies.Where(b => BodyAtrSwitches.SwitchesShapes(b.Catalog.ModRoot)).Select(b => b.Name).ToList();
        output.WriteLine("left out: " + string.Join(", ", left));
        Assert.Contains("NIRÆ body", left);
        foreach (var body in new[] { "rue", "yab", "neolithe", "bibo", "tbse" })
            Assert.DoesNotContain(left, n => n.Contains(body, StringComparison.OrdinalIgnoreCase));
    }
}
