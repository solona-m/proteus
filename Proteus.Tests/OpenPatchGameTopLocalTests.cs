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
/// The game's own tops refitted through the MOD paths — the automatic refit's mod-garment rule and the Studio's cut — with
/// the made-on body's reading of cloth past an open skin edge (<see cref="BodyRetarget.TargetBody.Fitted"/>) on, against the
/// same refits without it. A game top's own skin is a neckline patch whose cloth below reads that way, and up to 80 of its
/// points are taken in. The game-gear refit never takes the rule (it clears the body), so it is the yardstick for what
/// refitting these tops already turns over and stretches. Needs the game's data and the body mods named.
/// </summary>
public class OpenPatchGameTopLocalTests(ITestOutputHelper output)
{
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string LavaBod = LocalData.Mods + ":LavaBod+ 2.2";

    private const string Maiming = "Neo-Ishgardian Top of Maiming";
    private const string Scion = "Scion Adventurer's";
    private const string Dirndl = "Dirndl's Bodice";

    private const string NeobellyM = "neobelly chest - smallclothes/sfw m.mdl";
    private const string Teardrop = "common/11/c0201e0000_top.mdl";

    private static readonly BodyRetarget.Tuning RuleOff = new(NoOpenEdgeFit: true);

    /// <summary>
    /// Edges stretched against the game's own top, accepted at their measured count (build 1323): over a fuller bust the
    /// top now covers the breast it used to pass through, and its long edges across it stretch. Every one is above the
    /// game-gear refit's count for the same top and body (0, 5, 10).
    /// </summary>
    private static readonly Dictionary<(string, string, string?), int> AcceptedStretch = new()
    {
        [(Maiming, Neolithe, NeobellyM)] = 10,
        [(Maiming, LavaBod, Teardrop)] = 31,
        [(Dirndl, LavaBod, Teardrop)] = 34,
    };

    public static TheoryData<string, string, string?, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string, string?, string>();
            void Both(string garment, string mod, string? rel)
            {
                cases.Add(garment, mod, rel, "whole");
                cases.Add(garment, mod, rel, "cutOnly");
            }
            foreach (string garment in new[] { Maiming, Scion })
            {
                Both(garment, Neolithe, NeobellyM);
                Both(garment, Neolithe, "default chest - smallclothes/sfw xs.mdl");
            }
            Both(Dirndl, Neolithe, "extra chest - smallclothes/nsfw almond xs.mdl");
            foreach (string mod in new[] { Bibo, Rue })
                foreach (string garment in new[] { Maiming, Scion, Dirndl }) Both(garment, mod, null);
            foreach (string garment in new[] { Maiming, Scion, Dirndl }) Both(garment, LavaBod, Teardrop);
            return cases;
        }
    }

    [LocalDataTheory(LocalData.GameData, Neolithe, Bibo, Rue, LavaBod)]
    [MemberData(nameof(Cases))]
    [Trait("Plan", "AC-open-patch-authored-9")]
    [Trait("Plan", "AC-open-patch-authored-4")]
    public void A_game_top_on_a_mod_path_is_no_worse(string garmentName, string modNeed, string? rel, string path)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = VanillaGearRefitLocalTests.TopOf(data, garmentName);
        var garment = ModelPartReader.Read(bytes)!;
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_top", "0201")[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(modNeed));
        var options = mod.For("_top", "0201");
        var option = rel == null ? options[0] : options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel));
        string target = mod.PathOf(option);
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", false, null, null, uv, out var pair));
        var sets = BodyRetarget.Sets.From(garment, new HashSet<int>(), BodyRetarget.PartedSkinMeshes(garment, [pair], "_top", true, null));

        (BodyRetarget.Planned Plan, Vec3[] Final, int Fitted) Run(BodyRetarget.Tuning? tuning)
        {
            Vec3[]? final = null;
            float[]? fitted = null;
            void Trace(string stage, Vec3[] d) => final = d;
            BodyRetarget.Planned Refit() => path == "whole"
                ? RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _, trace: Trace)
                : BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
                                    clearBody: false, cutHidden: true,
                                    keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)), trace: Trace);
            BodyRetarget.PushTrace.Value = (k, v) => { if (k == "fitted") fitted = v; };
            try
            {
                var plan = tuning != null ? BodyRetarget.WithTuning(tuning, Refit) : Refit();
                Assert.Equal(sets.NodeCount, final!.Length);
                return (plan, final, fitted?.Count(f => f > 0f) ?? 0);
            }
            finally { BodyRetarget.PushTrace.Value = null; }
        }
        var off = Run(RuleOff);
        var on = Run(null);
        var game = SkinTuckSheet.Laid(bytes, RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _));

        // The written models, the kept skin in the new body's layout, as the Studio and the automatic refit draw them.
        byte[] Drawn(BodyRetarget.Planned plan) => RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log);
        int Inside(BodyRetarget.Planned plan) => BodySizeMatch.ClothAgainst(ModelPartReader.Read(plan.Model)!, target)!.Value.Inside;
        var body = new BodySurface(ModelPartReader.Read(File.ReadAllBytes(target))!, 0.01f);
        var bust = sets.ClothNodes.Where(n => sets.NodeAt[n].Y is > 1.12f and < 1.32f && sets.NodeAt[n].Z > 0.03f).ToList();
        List<float> Depths(Vec3[] d) => bust.Select(n => -SkinTuckSheet.Standoff(
                                               new Vector3(sets.NodeAt[n].X + d[n].X, sets.NodeAt[n].Y + d[n].Y, sets.NodeAt[n].Z + d[n].Z), body))
                                           .Where(x => !float.IsNaN(x)).ToList();
        var depthOff = Depths(off.Final);
        var depthOn = Depths(on.Final);
        // That "nothing inside" is read off the new body, not off missing readings: most of the bust front reads on it, away
        // from any open edge of its skin.
        var edges = BodyRetarget.SkinEdges.Of(ModelPartReader.Read(File.ReadAllBytes(target))!);
        int readable = bust.Count(n => body.Nearest(new Vector3(sets.NodeAt[n].X + on.Final[n].X, sets.NodeAt[n].Y + on.Final[n].Y,
                                                                sets.NodeAt[n].Z + on.Final[n].Z), 0.03f, out var h)
                                       && !BodyRetarget.PastEdge(h, edges));
        Assert.True(readable >= bust.Count / 2, $"only {readable} of {bust.Count} bust points read on the new body");
        var laidOff = SkinTuckSheet.Laid(bytes, off.Plan);
        var laidOn = SkinTuckSheet.Laid(bytes, on.Plan);
        int turnedOn = SkinTuckSheet.FoldedAgainstAuthored(garment, laidOn), turnedOff = SkinTuckSheet.FoldedAgainstAuthored(garment, laidOff);
        int stretchedOn = SkinTuckSheet.ClothStretch(garment, laidOn), stretchedOff = SkinTuckSheet.ClothStretch(garment, laidOff);
        int turnedGame = SkinTuckSheet.FoldedAgainstAuthored(garment, game), stretchedGame = SkinTuckSheet.ClothStretch(garment, game);
        double throughOn = VanillaGearRefitLocalTests.SkinThrough(ModelPartReader.Read(Drawn(on.Plan))!);
        double throughOff = VanillaGearRefitLocalTests.SkinThrough(ModelPartReader.Read(Drawn(off.Plan))!);
        int foldsOn = BodyRetarget.FoldedTriangles(sets, on.Final), foldsOff = BodyRetarget.FoldedTriangles(sets, off.Final);
        output.WriteLine($"{garmentName} on {Path.GetFileName(LocalData.Path(modNeed))} / {option.Rel} ({path}): fitted {on.Fitted}; "
                         + $"inside {Inside(off.Plan)} -> {Inside(on.Plan)}; bust >5 mm inside {depthOff.Count(x => x > 0.005f)} -> "
                         + $"{depthOn.Count(x => x > 0.005f)}; skin through {throughOff:0.0} -> {throughOn:0.0} cm²; folds {foldsOff} -> "
                         + $"{foldsOn}; turned {turnedOff} -> {turnedOn} (game path {turnedGame}); stretched {stretchedOff} -> "
                         + $"{stretchedOn} (game path {stretchedGame})");

        if (on.Fitted == 0)
        {
            // [AC-open-patch-authored-4] Taking nothing in, the rule changes nothing.
            Assert.Equal(SkinTuckSheet.Sha(off.Plan.Model), SkinTuckSheet.Sha(on.Plan.Model));
            return;
        }
        // [AC-open-patch-authored-9] No more cloth inside the new body, through it at the bust, or skin through it; no
        // deeper; no more folds; turned over and stretched against the game's own top no more than without the rule or the
        // game-gear refit, or as accepted.
        Assert.True(Inside(on.Plan) <= Inside(off.Plan), $"{Inside(on.Plan)} inside against {Inside(off.Plan)}");
        Assert.True(depthOn.Count(x => x > 0.005f) <= depthOff.Count(x => x > 0.005f));
        Assert.True(depthOn.Max() <= depthOff.Max() + 1e-4f, $"deepest {depthOn.Max() * 1000:0.0} mm against {depthOff.Max() * 1000:0.0}");
        Assert.True(throughOn <= throughOff + 0.5, $"{throughOn:0.0} cm² of skin through against {throughOff:0.0}");
        Assert.True(foldsOn <= foldsOff, $"{foldsOn} folded against {foldsOff}");
        Assert.True(on.Plan.Report.Folded <= off.Plan.Report.Folded);
        Assert.True(turnedOn <= Math.Max(turnedOff, turnedGame), $"{turnedOn} turned over");
        int allowed = Math.Max(Math.Max(stretchedOff, stretchedGame), AcceptedStretch.GetValueOrDefault((garmentName, modNeed, rel)));
        Assert.True(stretchedOn <= allowed, $"{stretchedOn} stretched, {allowed} allowed");
    }
}
