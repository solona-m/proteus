using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// [HS] Code, made on Tight&amp;Firm Gen2: its legs were refused for every size the size set made (only 44% of T&amp;F's
/// legs find their place on Bibo+'s by texture coordinate — its NSFW crotch is 60% of their vertices and lands nowhere),
/// though 93% of their surface does. Judged by surface, they refit, the crotch following the skin around it.
/// </summary>
public class Gen2LayoutRefitLocalTests(ITestOutputHelper output)
{
    private const string Code = LocalData.Mods + ":hs-Code-1.0.1-0uk";
    private const string TandF = LocalData.Mods + ":tight&firmgen2body-nsfw-installer";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string TreYas = LocalData.Mods + ":hs-[YAS] Tre-5.5.3-8xB";
    private const string Rosaline = LocalData.Mods + ":hs-Rosaline-1.1.2-hM5N";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string AbBody = LocalData.Mods + ":[Cry] AB Body [Body][DT] v2.0.9";

    private const string TandFLegs = "hip size/medium/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string TandFChest = "chest size/medium/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string CodeLegs = "files/common/6/c0201e0147_dwn.mdl";
    private const string HsLegs = "files/legs - smallclothes/{0}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string BiboLegs = "bottoms - small clothes/type a - {0}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    private static readonly Dalamud.Plugin.Services.IPluginLog Log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();

    /// <summary>One service for every test here: a gen3 map is two 134 MB TIFFs read and checked on first use.</summary>
    private static readonly Lazy<UVRemapService> Maps = new(() => new UVRemapService(Log, LocalData.Path(LocalData.UvMaps)));

    /// <summary>The legs of each women's body the size set offers, and whether a chest size of that body hems onto them.</summary>
    private static readonly (string Body, string Rel, bool ChestLegs)[] Targets =
    [
        (Bibo, string.Format(BiboLegs, "small"), true),
        (Bibo, string.Format(BiboLegs, "medium"), true),
        (Bibo, string.Format(BiboLegs, "large"), true),
        (Rue, string.Format(HsLegs, "watermelon crushers - a"), true),
        (Yab, string.Format(HsLegs, "watermelon crushers - a"), true),
        (Yab, string.Format(HsLegs, "skull crushers - a"), false),
        (TreYas, "files/common/15/c0201e0000_dwn.mdl", true),
        (TreYas, "files/common/10/c0201e0000_dwn.mdl", true),
        (Rosaline, "files/common/13/c0201e0000_dwn.mdl", true),
        (Neolithe, "default legs - smallclothes/sfw small.mdl", true),
        (Neolithe, "default legs - smallclothes/sfw medium.mdl", true),
        (Neolithe, "default legs - smallclothes/sfw large.mdl", true),
    ];

    /// <summary>Every T&amp;F hip size against every target: what the automatic refit and the Body size tool may pair.</summary>
    public static TheoryData<string, string, string, bool> WomensLegs()
    {
        var rows = new TheoryData<string, string, string, bool>();
        foreach (string size in new[] { "small", "medium", "large" })
            foreach (var (body, rel, chestLegs) in Targets)
                rows.Add(size, body, rel, chestLegs);
        return rows;
    }

    [LocalDataTheory(Code, TandF, Bibo, Rue, Yab, TreYas, Rosaline, Neolithe)]
    [MemberData(nameof(WomensLegs))]
    [Trait("Plan", "AC-gen2-legs-refit-2")]
    [Trait("Plan", "AC-gen2-legs-refit-4")]
    [Trait("Plan", "AC-gen2-legs-refit-8")]
    [Trait("Plan", "AC-gen2-legs-refit-10")]
    [Trait("Plan", "EDGE-gen2-legs-refit-21")]
    public void TandF_legs_pair_with_every_womens_legs(string size, string body, string rel, bool chestLegs)
    {
        // The size set refits Code's one legs file from the one T&F hip size it reads as, onto the legs a chest size
        // hems onto: those rows must fit. Every other row is a pair the automatic refit could still make, so its
        // correspondence is held to the same standard; its fit is printed.
        bool paired = chestLegs && size == CodeLegsSize.Value;
        var row = PairAndFit(size, body, rel, new UVRemapService(Log, "."), fit: paired);
        var m = row.Stats;

        if (body == Bibo)
        {
            // The crotch midline lands 17–20 cm off on Bibo+: set aside, and nothing left near that far.
            Assert.True(m.Disagreed > 0, "Bibo+'s wrong midline landing was not set aside");
            Assert.True(row.FinalMax <= CleanWorst + 0.015f, $"a point moved {row.FinalMax * 1000f:F1} mm");
        }
        else
        {
            Assert.True(m.Disagreed <= m.Landed / 100, $"{m.Disagreed} of {m.Landed} landings disagreed");
            Assert.True(m.AreaCoverage >= m.RawAreaCoverage - 0.02f, $"the guard cost {m.RawAreaCoverage - m.AreaCoverage:P1}");
        }
    }

    /// <summary>
    /// gen2 onto gen3 stays refused, as before: judged by surface through the gen3 map (measured, build 1279), AB Body's
    /// sparse shin turned over where the map's nearest-pixel search snapped it — 7 triangles below 0.75 m, 2–3 edges torn
    /// — so only a mirrored layout's own doubled one is judged by surface.
    /// </summary>
    [LocalDataTheory(TandF, AbBody, LocalData.UvMaps)]
    [InlineData("small")]
    [InlineData("medium")]
    [InlineData("large")]
    [Trait("Plan", "EDGE-gen2-legs-refit-12")]
    public void TandF_legs_onto_AB_Body_stay_refused(string size)
    {
        var (source, sourceCat) = Mod(TandF);
        var (target, targetCat) = Mod(AbBody);
        string refusal = BodyRetarget.BuildPair("_dwn",
            BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(TandF), $"hip size/{size}/chara/equipment/e0000/model/c0201e0000_dwn.mdl"),
            BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(AbBody), "Smallclothes Legs/AB - Innie 1/chara/equipment/e0000/model/c0201e0000_dwn.mdl"),
            "_dwn", false, RefitCore.MaskOf(source, sourceCat, "_dwn", null), RefitCore.MaskOf(target, targetCat, "_dwn", null),
            Maps.Value, out _)!;
        output.WriteLine(refusal);
        Assert.Contains("texture layout", refusal);
    }

    /// <summary>The worst a point moved on the targets where nothing landed wrong (Rosaline, measured: 44.1 mm).</summary>
    private const float CleanWorst = 0.045f;

    /// <summary>The T&amp;F hip size Code's legs read as, as the size set reads them.</summary>
    private static readonly Lazy<string> CodeLegsSize = new(() =>
    {
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Code), CodeLegs));
        var legs = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var tandf = Mod(TandF);
        var pick = RefitCore.DetectSource(legs, bones, "_dwn", "0201", [tandf], tandf.Dir, _ => true)!;
        return RefitCore.Normal(pick.Option.Rel).Split('/')[1];
    });

    private sealed record Row(UvAtlasCorrespondence.Stats Stats, float FinalMax);

    /// <summary>
    /// T&amp;F's <paramref name="size"/> legs onto <paramref name="rel"/> of <paramref name="body"/>: paired by surface, every
    /// skin vertex placed, no edge torn or triangle turned, nothing moved further than the two bodies lie apart allows.
    /// With <paramref name="fit"/>, Code's legs refitted onto them as the size set refits them, measured inside the pass:
    /// no triangle folded at its end, and their cloth no deeper in the new body than the author's in T&amp;F.
    /// </summary>
    private Row PairAndFit(string size, string body, string rel, UVRemapService uvRemap, bool fit)
    {
        var (source, sourceCat) = Mod(TandF);
        var (target, targetCat) = Mod(body);
        string sourcePath = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(TandF),
                                                                   $"hip size/{size}/chara/equipment/e0000/model/c0201e0000_dwn.mdl");
        string targetPath = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(body), rel);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(BodyRetarget.BuildPair("_dwn", sourcePath, targetPath, "_dwn", false,
                                           RefitCore.MaskOf(source, sourceCat, "_dwn", null),
                                           RefitCore.MaskOf(target, targetCat, "_dwn", null), uvRemap, out var pair));
        watch.Stop();
        var atlas = Assert.IsType<UvAtlasCorrespondence>(pair.Correspondence);
        var m = atlas.Measured;
        var skin = Skin(atlas.Source);
        float finalMax = skin.Max(v => atlas.Field[v]?.Length() ?? float.PositiveInfinity);
        float bound = MathF.Max(0.06f, 2f * SizeSetRefit.BodyDistance(sourcePath, targetPath, p => SizeSetRefit.BodyProbes(p)).P90);
        var (torn, turned, turnedOutside) = TornOrTurned(atlas.Source, atlas.Field);
        output.WriteLine($"{size} -> {target} / {rel}: by count {m.VertexCoverage:P1} ({m.Landed} of {m.Placeable}), by area " +
                         $"{m.RawAreaCoverage:P1} -> {m.AreaCoverage:P1}, {m.Carried} carried, disagreed {m.Disagreed}, " +
                         $"unvouched {m.Unvouched}, unjudged {m.Unjudged}, q p99 {m.P99Disagreement:F2} max " +
                         $"{m.MaxDisagreement:F2}, trusted shift max {m.MaxShift * 1000f:F1} p99 {m.P99Shift * 1000f:F1} mm, " +
                         $"final max {finalMax * 1000f:F1} mm (bound {bound * 1000f:F0}), {torn} torn, {turned} turned " +
                         $"({turnedOutside} outside the crotch), " +
                         $"paired in {watch.ElapsedMilliseconds} ms");
        output.WriteLine("  " + atlas.Describe());

        Assert.True(m.VertexCoverage < 0.8f && m.AreaCoverage >= 0.8f, $"by count {m.VertexCoverage:P1}, by area {m.AreaCoverage:P1}");
        Assert.All(skin, v => Assert.True(atlas.Field[v].HasValue, $"skin vertex {v} has no displacement"));
        Assert.Equal(0, m.Unjudged);
        Assert.Equal((0, 0), (torn, turnedOutside));
        Assert.True(turned <= CrotchTurnedCap, $"{turned} of the crotch's triangles turned over");
        Assert.True(finalMax <= bound, $"a point moved {finalMax * 1000f:F1} mm, more than {bound * 1000f:F0}");

        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Code), CodeLegs));
        var legs = ModelPartReader.Read(bytes)!;
        Vec3[]? final = null;
        var plan = RefitCore.PlanRefit(legs, bytes, [pair], "_dwn", true, false, out _,
                                       trace: (s, d) => { if (s == "layer-order") final = d; });
        Assert.NotNull(final);
        var sets = BodyRetarget.Sets.From(legs, new HashSet<int>(), BodyRetarget.PartedSkinMeshes(legs, [pair], "_dwn", true, null));
        int folds = BodyRetarget.FoldedTriangles(sets, final!);
        var now = BodySizeMatch.ClothAgainst(ModelPartReader.Read(plan.Model)!, targetPath)!.Value;
        var author = BodySizeMatch.ClothAgainst(legs, sourcePath)!.Value;
        float allowed = MathF.Max(BodySizeMatch.ClearsBelow, author.Near > 0 ? (float)author.Inside / author.Near : 0f);
        output.WriteLine($"  Code's legs: {folds} folded at the end of the pass, {now.Inside} of {now.Near} cloth points inside " +
                         $"(the author's on T&F {author.Inside} of {author.Near}){(fit ? "" : " — not a size the size set makes")}");
        if (fit)
        {
            Assert.Equal(0, folds);
            if (now.Near >= BodySizeMatch.MinEvidence)
                Assert.True(now.Inside <= now.Near * allowed, $"{now.Inside} of {now.Near} cloth points inside the new body");
            else output.WriteLine($"  only {now.Near} cloth points near the body: too few to judge the fit");
        }
        return new Row(m, finalMax);
    }

    /// <summary>
    /// How many of the crotch's triangles may turn over. Two crotches cut differently do not land triangle for triangle
    /// at their 2 mm scale even by texture coordinate: refits accepted before this change turn over 162 (Bibo+ onto Rue)
    /// to 738 (Neolithe onto Rue). T&amp;F's, settled, turned 0–4 on every bibo target.
    /// </summary>
    private const int CrotchTurnedCap = 10;

    /// <summary>The crotch band, where two bodies' 2 mm triangles are cut differently (0.8–0.95 m).</summary>
    private static bool InCrotch(float y) => y is >= 0.8f and <= 0.95f;

    /// <summary>
    /// The source's skin edges the field stretches past three times their length and 10 mm, or crushes to a third and
    /// 10 mm shorter; its skin triangles it turns over or shrinks to a tenth of their area; and of those, the ones outside
    /// the crotch. Per edge and triangle, judged on its own: nothing here asks the neighbours, as the guard does.
    /// </summary>
    private static (int Torn, int Turned, int TurnedOutside) TornOrTurned(ModelParts source,
                                                                         IReadOnlyList<System.Numerics.Vector3?> field)
    {
        int outside = 0;
        System.Numerics.Vector3 P(int v) => new(source.Positions[v * 3], source.Positions[v * 3 + 1], source.Positions[v * 3 + 2]);
        System.Numerics.Vector3 Q(int v) => P(v) + (field[v] ?? default);
        var edges = new HashSet<(int, int)>();
        int turned = 0;
        foreach (var part in source.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var before = System.Numerics.Vector3.Cross(P(b) - P(a), P(c) - P(a));
                var after = System.Numerics.Vector3.Cross(Q(b) - Q(a), Q(c) - Q(a));
                if (before.LengthSquared() > 0f
                    && (System.Numerics.Vector3.Dot(before, after) <= 0f || after.Length() < before.Length() / 10f))
                {
                    turned++;
                    if (!InCrotch((P(a).Y + P(b).Y + P(c).Y) / 3f)) outside++;
                }
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) }) edges.Add(x < y ? (x, y) : (y, x));
            }
        int torn = 0;
        foreach (var (x, y) in edges)
        {
            float l = System.Numerics.Vector3.Distance(P(x), P(y)), l2 = System.Numerics.Vector3.Distance(Q(x), Q(y));
            if (l2 > MathF.Max(3f * l, l + 0.01f) || (l > 0.015f && l2 < MathF.Min(l / 3f, l - 0.01f))) torn++;
        }
        return (torn, turned, outside);
    }

    /// <summary>
    /// The headless refit, given <c>--uvmaps</c>, as it refits Code's legs onto Bibo+ Medium: the pair it builds, the plan
    /// it makes, and the model it writes.
    /// </summary>
    [LocalDataFact(Code, TandF, Bibo)]
    [Trait("Plan", "AC-gen2-legs-refit-2")]
    [Trait("Plan", "AC-gen2-legs-refit-4")]
    [Trait("Plan", "AC-gen2-legs-refit-10")]
    public void The_CLI_s_Code_legs_onto_Bibo_M_refit_with_no_folds()
    {
        var (source, sourceCat) = Mod(TandF);
        var (target, targetCat) = Mod(Bibo);
        string sourcePath = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(TandF), TandFLegs);
        string targetPath = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Bibo), string.Format(BiboLegs, "medium"));
        Assert.Null(BodyRetarget.BuildPair("_dwn", sourcePath, targetPath, "_dwn", false,
                                           RefitCore.MaskOf(source, sourceCat, "_dwn", null),
                                           RefitCore.MaskOf(target, targetCat, "_dwn", null), new UVRemapService(Log, "."),
                                           out var pair));

        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Code), CodeLegs));
        var legs = ModelPartReader.Read(bytes)!;
        Vec3[]? final = null;
        var plan = BodyRetarget.Plan(legs, bytes, [pair], "_dwn", pushOut: true, replaceSkin: true, acrossBodies: true,
                                     cutHidden: true, trace: (s, d) => { if (s == "layer-order") final = d; });
        Assert.NotNull(final);
        var sets = BodyRetarget.Sets.From(legs, new HashSet<int>(), BodyRetarget.PartedSkinMeshes(legs, [pair], "_dwn", true, null));
        Assert.Equal(0, BodyRetarget.FoldedTriangles(sets, final!));
        var written = ModelPartReader.Read(plan.Model);
        Assert.NotNull(written);
        var now = BodySizeMatch.ClothAgainst(written!, targetPath)!.Value;
        var author = BodySizeMatch.ClothAgainst(legs, sourcePath)!.Value;
        output.WriteLine($"{now.Inside} of {now.Near} cloth points inside Bibo+ M; the author's on T&F {author.Inside} of {author.Near}");
        Assert.True(now.Near >= BodySizeMatch.MinEvidence);
        Assert.True(now.Inside <= now.Near * MathF.Max(BodySizeMatch.ClearsBelow, (float)author.Inside / author.Near));
    }

    /// <summary>
    /// The game's own body pieces — a gen2 body, the one every piece of game gear is made on — onto body mods: each is
    /// refused, paired by count as before with nothing carried or set aside, or paired by surface with a field that
    /// tears no edge and turns no triangle outside the crotch.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo, Rue, AbBody, LocalData.UvMaps)]
    [Trait("Plan", "EDGE-gen2-legs-refit-20")]
    public void The_game_s_body_pieces_onto_body_mods_census()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        foreach (string slot in new[] { "_top", "_dwn", "_glv", "_sho" })
        {
            if (vanilla.For(slot, "0201").FirstOrDefault() is not { } piece) continue;
            string sourcePath = vanilla.PathOf(piece);
            foreach (string need in new[] { Bibo, Rue, AbBody })
            {
                var (target, targetCat) = Mod(need);
                if (targetCat.For(slot, "0201").FirstOrDefault() is not { } to) continue;
                string? refusal = BodyRetarget.BuildPair(slot, sourcePath, targetCat.PathOf(to), slot, false, null,
                                                         RefitCore.MaskOf(target, targetCat, slot, null), Maps.Value, out var pair);
                if (refusal != null)
                {
                    output.WriteLine($"{slot} -> {target} / {to.Label}: refused: {refusal}");
                    continue;
                }
                output.WriteLine($"{slot} -> {target} / {to.Label}: {pair.Correspondence.Describe()}");
                if (pair.Correspondence is not UvAtlasCorrespondence atlas) continue;
                var m = atlas.Measured;
                if (m.Carried == 0)
                {
                    Assert.Equal(0, m.Distrusted);
                    continue;
                }
                var (torn, _, turnedOutside) = TornOrTurned(atlas.Source, atlas.Field);
                Assert.Equal((0, 0), (torn, turnedOutside));
            }
        }
    }

    [LocalDataFact(TandF, Bibo)]
    [Trait("Plan", "AC-gen2-legs-refit-6")]
    public void TandF_top_onto_Bibo_is_matched_as_before()
    {
        // Accepted by count before this change: no surface judgement, nothing carried, and what missed still has no
        // displacement of its own — left for the refit's fill, as it always was.
        var (source, sourceCat) = Mod(TandF);
        var (target, targetCat) = Mod(Bibo);
        Assert.Null(BodyRetarget.BuildPair("_top", BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(TandF), TandFChest),
                                           BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Bibo),
                                               "breasts - small clothes/nude - medium/chara/equipment/e0000/model/c0201e0000_top.mdl"),
                                           "_top", false, RefitCore.MaskOf(source, sourceCat, "_top", null),
                                           RefitCore.MaskOf(target, targetCat, "_top", null), new UVRemapService(Log, "."),
                                           out var pair));
        var atlas = Assert.IsType<UvAtlasCorrespondence>(pair.Correspondence);
        output.WriteLine(atlas.Describe());
        Assert.True(atlas.Measured.VertexCoverage >= 0.8f);
        Assert.Equal(0, atlas.Measured.Carried);
        Assert.Equal((0, 0, 0), (atlas.Measured.Disagreed, atlas.Measured.Unvouched, atlas.Measured.Unjudged));
        Assert.Empty(atlas.CarriedVertices);
        Assert.Equal(atlas.Measured.RawAreaCoverage, atlas.Measured.AreaCoverage);
        Assert.DoesNotContain("no place on the target's layout", atlas.Describe());
        Assert.Equal(atlas.Measured.Placeable - atlas.Measured.Landed, atlas.Field.Count(f => f == null)
                     - (atlas.Source.Positions.Length / 3 - Skin(atlas.Source).Count));
    }

    [LocalDataFact(Code, TandF, Bibo, Rue)]
    [Trait("Plan", "AC-gen2-legs-refit-1")]
    [Trait("Plan", "AC-gen2-legs-refit-7")]
    [Trait("Plan", "AC-gen2-legs-refit-2")]
    [Trait("Plan", "EDGE-gen2-legs-refit-21")]
    public void Code_gains_legs_for_every_size_its_top_gets()
    {
        string copy = CodeAsShipped();
        try
        {
            var (result, log, request) = Run(copy, ["bibo", "rue"], remake: false);
            Assert.True(result.Made > 0);
            Assert.DoesNotContain(result.Notes, n => n.Contains("Legs", StringComparison.Ordinal));
            AssertEveryOptionHasRefittedLegs(copy);
            Assert.Contains(log, l => l.Contains("size set: Legs", StringComparison.Ordinal)
                                      && l.Contains("no place on the target's layout", StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.Contains("Only 44%", StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.Contains("size set: Legs", StringComparison.Ordinal)
                                            && l.Contains("refused:", StringComparison.Ordinal));
            // Bibo+'s wrong midline landing set aside, and said so.
            Assert.Contains(log, l => l.Contains("size set: Legs", StringComparison.Ordinal) && l.Contains("Bibo", StringComparison.Ordinal)
                                      && l.Contains("whose landing disagreed with the skin around them", StringComparison.Ordinal));
            // [EDGE-gen2-legs-refit-21] Cut from the one T&F hip size the legs read as — the size the fit is gated on.
            Assert.Contains(log, l => l.Contains("size set: Legs", StringComparison.Ordinal)
                                      && l.Contains("/ " + char.ToUpperInvariant(CodeLegsSize.Value[0]) + CodeLegsSize.Value[1..],
                                                    StringComparison.Ordinal)
                                      && l.Contains("made on", StringComparison.Ordinal));

            var (again, _, _) = Run(copy, ["bibo", "rue"], remake: false);
            Assert.Equal(0, again.Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    [LocalDataFact(Code, TandF, Bibo, Rue)]
    [Trait("Plan", "EDGE-gen2-legs-refit-10")]
    public void A_mod_sized_before_gains_legs_on_a_plain_re_run()
    {
        // As the user's Code was left by the run before this change: each size we added carries the author's legs.
        string copy = CodeAsShipped();
        try
        {
            Assert.True(Run(copy, ["bibo", "rue"], remake: false).Result.Made > 0);
            var ours = BodyRetargetWriter.ReadRecord(copy)!.Options.Where(e => e.GamePath.EndsWith("_dwn.mdl", StringComparison.Ordinal))
                                         .Select(e => e.Name).ToHashSet();
            Assert.NotEmpty(ours);
            RestoreAuthorsLegs(copy, ours);

            var (result, _, _) = Run(copy, ["bibo", "rue"], remake: false);
            Assert.True(result.Made > 0);
            AssertEveryOptionHasRefittedLegs(copy);
            Assert.Equal(0, Run(copy, ["bibo", "rue"], remake: false).Result.Made);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    [LocalDataFact(Code, TandF, Bibo, Rue)]
    [Trait("Plan", "EDGE-gen2-legs-refit-11")]
    public void Remaking_moves_each_top_s_hem_onto_the_new_hips()
    {
        string copy = CodeAsShipped();
        try
        {
            Assert.True(Run(copy, ["bibo", "rue"], remake: false).Result.Made > 0);
            var (remade, log, _) = Run(copy, ["bibo", "rue"], remake: true);
            Assert.True(remade.Made > 0);
            Assert.DoesNotContain(log, l => l.Contains("hips left as they are", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("size set: Body", StringComparison.Ordinal)
                                      && l.Contains(": hips: ", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private static (string Dir, BodySizeCatalog Catalog) Mod(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    private static List<int> Skin(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                  .SelectMany(p => p.Triangles).Distinct().ToList();

    /// <summary>Code as its author ships it: its manifests and models, without the sizes Proteus added to it.</summary>
    private static string CodeAsShipped()
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-" + Guid.NewGuid().ToString("N"));
        SizeSetRefitTests.CopyModels(LocalData.Path(Code), copy, withRecord: false);
        foreach (var option in PenumbraModMeta.TryReadFileOptions(copy, "Size") ?? [])
            if (option.Files != null
                && option.Files.Values.Any(f => RefitCore.Normal(f).StartsWith("body retarget/", StringComparison.Ordinal)))
                Assert.NotNull(PenumbraModMeta.RemoveOption(copy, "Size", option.Name));
        Assert.Equal(["Large", "Medium"], (PenumbraModMeta.TryReadFileOptions(copy, "Size") ?? []).Select(o => o.Name).Order());
        return copy;
    }

    private (SizeSetRefit.Result Result, List<string> Log, SizeSetRefit.Request Request) Run(string copy,
        IReadOnlyList<string> keys, bool remake)
    {
        string modsRoot = LocalData.Path(LocalData.Mods);
        var mods = new[] { TandF, Bibo, Rue }.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var request = new SizeSetRefit.Request(copy, keys, Yas: false, Remake: remake, bodies);
        var result = SizeSetRefit.Run(request, new UVRemapService(log, "."), log, p => output.WriteLine(p), CancellationToken.None);
        foreach (string note in result.Notes) output.WriteLine("note: " + note);
        var lines = new List<string>();
        foreach (var call in log.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.FirstOrDefault() is not string template) continue;
            var values = args.Skip(1).FirstOrDefault() as object[] ?? [];
            string line;
            try { line = string.Format(template.Replace("{0:F1}", "{0}"), values); }
            catch (FormatException) { line = template + " | " + string.Join(", ", values); }
            lines.Add(line);
            output.WriteLine("log: " + line);
        }
        return (result, lines, request);
    }

    /// <summary>Every size the run added to Code's "Size" group draws refitted legs, not the author's.</summary>
    private static void AssertEveryOptionHasRefittedLegs(string copy)
    {
        var added = (PenumbraModMeta.TryReadFileOptions(copy, "Size") ?? [])
                    .Where(o => o.Name is not ("Large" or "Medium")).ToList();
        Assert.NotEmpty(added);
        foreach (var option in added)
        {
            Assert.True(option.Files!.TryGetValue("chara/equipment/e0147/model/c0201e0147_dwn.mdl", out var legs),
                        $"{option.Name} has no legs");
            Assert.StartsWith("body retarget/", RefitCore.Normal(legs!));
        }
    }

    /// <summary>
    /// Points the legs of the named options back at the author's file and forgets they were refitted, as a run before
    /// this change left them: the record lists only their tops.
    /// </summary>
    private static void RestoreAuthorsLegs(string copy, IReadOnlySet<string> names)
    {
        var indented = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        string meta = Path.Combine(copy, PenumbraModMeta.MetaFile);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!;
        foreach (var group in json["Groups"]!.AsArray())
        {
            if ((string?)group!["Name"] != "Size") continue;
            foreach (var option in group["Options"]!.AsArray())
                if (names.Contains((string)option!["Name"]!) && option["Files"] is System.Text.Json.Nodes.JsonObject files)
                    files["chara/equipment/e0147/model/c0201e0147_dwn.mdl"] = "files\\common\\6\\c0201e0147_dwn.mdl";
        }
        File.WriteAllText(meta, json.ToJsonString(indented));

        string recordPath = Path.Combine(copy, SidecarDiscoveryService.SidecarSubdir, BodyRetargetWriter.RecordFile);
        var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(recordPath))!;
        var options = record["Options"]!.AsArray();
        foreach (var entry in options.Where(e => ((string?)e!["GamePath"])?.EndsWith("_dwn.mdl", StringComparison.Ordinal) == true)
                                     .ToList())
            options.Remove(entry);
        File.WriteAllText(recordPath, record.ToJsonString(indented));
        Assert.DoesNotContain(BodyRetargetWriter.ReadRecord(copy)!.Options, e => e.GamePath.EndsWith("_dwn.mdl", StringComparison.Ordinal));
    }
}
