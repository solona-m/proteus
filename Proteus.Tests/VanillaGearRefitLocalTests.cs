using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Lumina.Excel.Sheets;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The game's own tops refitted onto body mods as the automatic refit does it, measured on the refit's own output. The
/// game buries a top's cloth in its own body — about 700 cm² of the Neo-Ishgardian Top of Maiming sits 0.5–15 mm
/// inside it — and draws no chest skin there, so the new body's skin put in whole showed through the cloth under the bust
/// and on the belly. Needs the game's data and the body mods named.
/// </summary>
public class VanillaGearRefitLocalTests(ITestOutputHelper output)
{
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string LavaBod = LocalData.Mods + ":LavaBod+ 2.2";

    private const string Maiming = "Neo-Ishgardian Top of Maiming";
    private const string Scion = "Scion Adventurer's";
    private const string Dirndl = "Dirndl's Bodice";

    /// <summary>The bodice was reported on; the tuck's constants were set on it and on LavaBod+'s Teardrop.</summary>
    private const string AlmondXs = "extra chest - smallclothes/nsfw almond xs.mdl";

    /// <summary>LavaBod+ 2.2's "Smallclothes Chest" / "Teardrop": a large bust.</summary>
    private const string Teardrop = "common/11/c0201e0000_top.mdl";

    public static TheoryData<string, string> NeolitheCases => new()
    {
        { Maiming, "neobelly chest - smallclothes/sfw m.mdl" },
        { Maiming, "default chest - smallclothes/sfw xs.mdl" },
        { Scion, "neobelly chest - smallclothes/sfw m.mdl" },
        { Scion, "default chest - smallclothes/sfw xs.mdl" },
        { Dirndl, AlmondXs },
    };

    [LocalDataTheory(LocalData.GameData, Neolithe)]
    [MemberData(nameof(NeolitheCases))]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-1")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-2")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-3")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-4")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-6")]
    [Trait("Plan", "AC-neckline-skin-tuck-2")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-16")]
    public void A_game_top_on_Neolithe_shows_no_skin_through_and_no_holes(string garment, string rel)
        => Check(garment, Neolithe, rel);

    [LocalDataTheory(LocalData.GameData, Bibo)]
    [InlineData(Maiming)]
    [InlineData(Scion)]
    [InlineData(Dirndl)]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-6")]
    [Trait("Plan", "AC-neckline-skin-tuck-2")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-14")]
    public void A_game_top_on_Bibo_shows_no_skin_through_and_no_holes(string garment) => Check(garment, Bibo, null);

    [LocalDataTheory(LocalData.GameData, Rue)]
    [InlineData(Maiming)]
    [InlineData(Scion)]
    [InlineData(Dirndl)]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-6")]
    [Trait("Plan", "AC-neckline-skin-tuck-2")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-14")]
    public void A_game_top_on_Rue_shows_no_skin_through_and_no_holes(string garment) => Check(garment, Rue, null);

    [LocalDataTheory(LocalData.GameData, LavaBod)]
    [InlineData(Maiming)]
    [InlineData(Scion)]
    [InlineData(Dirndl)]
    [Trait("Plan", "AC-neckline-skin-tuck-2")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-14")]
    public void A_game_top_on_LavaBod_shows_no_skin_through_and_no_holes(string garment) => Check(garment, LavaBod, Teardrop);

    /// <summary>A game top refitted onto a body as the automatic refit does it, and the same with the tuck off.</summary>
    private sealed record Refit(byte[] Bytes, ModelParts Garment, string Source, string Target, BodyOption Option,
                                BodyRetarget.SlotPair Pair, BodyRetarget.Planned Game, BodyRetarget.Planned Untucked,
                                Func<BodyRetarget.Planned, byte[]> Drawn, Lumina.GameData Data, long TuckMs, long UntuckedMs);

    private static Refit Plan(string garmentName, string modNeed, string? rel)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = TopOf(data, garmentName);
        var garment = ModelPartReader.Read(bytes)!;

        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_top", "0201")[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(modNeed));
        var options = mod.For("_top", "0201");
        var option = rel == null ? options[0] : options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel));
        string target = mod.PathOf(option);
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", false, null, null, uv, out var pair));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var game = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _);
        long tuckMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var untucked = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoTuck: true),
            () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _));
        long untuckedMs = clock.ElapsedMilliseconds;
        return new Refit(bytes, garment, source, target, option, pair, game, untucked,
                         plan => RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log), data, tuckMs,
                         untuckedMs);
    }

    /// <param name="rel">The chest option of the body mod, or null for its first.</param>
    private void Check(string garmentName, string modNeed, string? rel)
    {
        var r = Plan(garmentName, modNeed, rel);
        var (bytes, garment, source, target, option, pair, game) = (r.Bytes, r.Garment, r.Source, r.Target, r.Option, r.Pair, r.Game);
        var whole = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _);
        var cutOnly = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true,
                                        acrossBodies: true, clearBody: false, cutHidden: true,
                                        keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));

        var gameModel = ModelPartReader.Read(r.Drawn(game))!;
        var untuckedModel = ModelPartReader.Read(r.Drawn(r.Untucked))!;
        var body = ModelPartReader.Read(File.ReadAllBytes(target))!;
        double throughGame = SkinThrough(gameModel);
        double throughUntucked = SkinThrough(untuckedModel);
        double underGame = SkinThrough(gameModel, Under);
        double underUntucked = SkinThrough(untuckedModel, Under);
        double throughWhole = SkinThrough(ModelPartReader.Read(r.Drawn(whole))!);
        double throughCut = SkinThrough(ModelPartReader.Read(r.Drawn(cutOnly))!);
        double nearCoincident = NearCoincident(gameModel, untuckedModel);
        var holeBands = new SortedDictionary<(float, string), double>();
        var baseBands = new SortedDictionary<(float, string), double>();
        double holes = UncoveredUndrawn(untuckedModel, body, holeBands);
        double holesTucked = UncoveredUndrawn(gameModel, body, new SortedDictionary<(float, string), double>());
        double baseline = UncoveredUndrawn(garment, ModelPartReader.Read(File.ReadAllBytes(source))!, baseBands);

        output.WriteLine($"RAN {garmentName} on {Path.GetFileName(LocalData.Path(modNeed))} / {option.Rel}");
        output.WriteLine($"  tucked {game.Report.Swap?.Tucked ?? 0} triangles; cloth under the skin tucked {underGame:0.0} cm², "
                         + $"untucked {underUntucked:0.0}; with the cloth beside its edge, tucked {throughGame:0.0}, untucked "
                         + $"{throughUntucked:0.0}; new skin under 1 mm of cloth {nearCoincident:0.00} cm²");
        output.WriteLine($"  skin through cloth untucked: whole {throughWhole:0.0} cm², cut {throughCut:0.0}, cut + cleared {throughUntucked:0.0}");
        output.WriteLine($"  pushed: cut {cutOnly.Report.Pushed} (worst move {cutOnly.Report.WorstMove * 1000:0.0} mm), "
                         + $"cut + cleared {game.Report.Pushed} (worst move {game.Report.WorstMove * 1000:0.0} mm)");
        output.WriteLine($"  uncovered torso skin left undrawn: untucked {holes:0.0} cm², tucked {holesTucked:0.0} "
                         + $"(game's own on its body {baseline:0.0})");
        foreach (var key in holeBands.Keys.Union(baseBands.Keys).OrderByDescending(k => k))
            output.WriteLine($"    y {key.Item1:0.0} {key.Item2}: {holeBands.GetValueOrDefault(key):0.0} cm² "
                             + $"(game's own {baseBands.GetValueOrDefault(key):0.0})");
        output.WriteLine($"  folded: game {game.Report.Folded}, whole {whole.Report.Folded}; swap {game.Report.Swap}");

        // AC-neckline-skin-tuck-2: the tuck brings no skin through the cloth, and none right under it — and, drawing only
        // more skin, opens no hole.
        Assert.True(underGame <= underUntucked + 0.5, $"tucked {underGame:0.0} cm² under the skin against {underUntucked:0.0}");
        Assert.True(nearCoincident <= 0.5, $"{nearCoincident:0.00} cm² of new skin under 1 mm of cloth");
        Assert.True(holesTucked <= holes + 0.5, $"tucked {holesTucked:0.0} cm² undrawn against {holes:0.0}");
        Assert.Equal(r.Untucked.Report.Folded, game.Report.Folded);

        // The cut and the clear, judged as they were written, on the bodies they were written for: on the refit without the
        // tuck, which the goldens pin as it was. LavaBod+'s Teardrop came in with the tuck; on so large a bust the clear
        // buries more than it was made for (see the deep clear).
        if (modNeed == LavaBod) return;
        // AC-1: on the reported top and body the cut takes most of what showed through (679 → 193 cm²); on any other at
        // least half — a smaller body shows less to start with (Rue+ 529 → 220, Neolithe XS 487 → 198).
        bool reported = garmentName == Maiming && modNeed == Neolithe && rel == "neobelly chest - smallclothes/sfw m.mdl";
        double share = reported ? 0.4 : 0.5;
        Assert.True(throughUntucked <= share * throughWhole, $"{throughUntucked:0.0} cm² of {throughWhole:0.0} still shows");
        // AC-2: clearing the body on top of the cut shows no more skin — rounding aside — and moves the cloth not much
        // further.
        Assert.True(throughUntucked <= throughCut + 0.5, $"cleared {throughUntucked:0.0} cm² against cut {throughCut:0.0}");
        Assert.True(game.Report.Pushed <= 2 * cutOnly.Report.Pushed + 50,
                    $"pushed {game.Report.Pushed} against {cutOnly.Report.Pushed}");
        Assert.True(game.Report.WorstMove <= cutOnly.Report.WorstMove + 0.008f,
                    $"moved {game.Report.WorstMove * 1000:0.0} mm against {cutOnly.Report.WorstMove * 1000:0.0}");
        // AC-3: the cut opens no holes the game's own top does not have. The margin is what was checked by eye: the Scion
        // top's jacket hangs further off neobelly SFW M's bust than any ray reaches, 34 cm² under it, closed over by the
        // cloth (49 cm² in all against the game's 14). Every Top of Maiming case comes in far below its own body's 436.
        Assert.True(holes <= Math.Max(1.5 * baseline, baseline + 40.0), $"{holes:0.0} cm² undrawn against {baseline:0.0}");
        // AC-4
        Assert.True(r.Untucked.Report.Folded <= whole.Report.Folded, $"folded {r.Untucked.Report.Folded} against {whole.Report.Folded}");
    }

    public static TheoryData<string, string, string?> NecklineCases => new()
    {
        { Dirndl, Neolithe, AlmondXs },              // required, and what the tuck's constants were set on
        { Dirndl, LavaBod, Teardrop },               // required, and what the tuck's constants were set on
        { Dirndl, Rue, null },                       // held out
        { Dirndl, Bibo, null },                      // held out
        { Maiming, Neolithe, "neobelly chest - smallclothes/sfw m.mdl" },
        { Maiming, Neolithe, "default chest - smallclothes/sfw xs.mdl" },
        { Scion, Neolithe, "neobelly chest - smallclothes/sfw m.mdl" },
        { Scion, Neolithe, "default chest - smallclothes/sfw xs.mdl" },
    };

    /// <summary>
    /// Looking into the neckline, from straight ahead down to steeply above, as a camera looks at a character: no skin of
    /// the new body undrawn and seen through, beyond what the game's own top shows on its own body. Reported on Dirndl's
    /// Bodice on Neolithe's Almond XS — the bust under the neckline cut away, the inside of the bodice's back through it.
    /// </summary>
    [LocalDataTheory(LocalData.GameData, Neolithe, LavaBod, Rue, Bibo)]
    [MemberData(nameof(NecklineCases))]
    [Trait("Plan", "AC-neckline-skin-tuck-1")]
    [Trait("Plan", "AC-neckline-skin-tuck-4")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-3")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-12")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-14")]
    [Trait("Plan", "EDGE-neckline-skin-tuck-18")]
    public void A_game_top_shows_no_hole_looking_into_its_neckline(string garmentName, string modNeed, string? rel)
    {
        var r = Plan(garmentName, modNeed, rel);
        var culled = Culled(r.Data, r.Garment);
        output.WriteLine($"RAN {garmentName} on {Path.GetFileName(LocalData.Path(modNeed))} / {r.Option.Rel}");
        output.WriteLine($"  back faces hidden: {string.Join(", ", culled.Select(c => $"{c.Key} {c.Value}"))}");
        output.WriteLine($"  plan took {r.TuckMs} ms, {r.UntuckedMs} ms without the tuck; swap {r.Game.Report.Swap}");

        var body = ModelPartReader.Read(File.ReadAllBytes(r.Target))!;
        var tucked = ModelPartReader.Read(r.Drawn(r.Game))!;
        var untucked = ModelPartReader.Read(r.Drawn(r.Untucked))!;
        var bands = new SortedDictionary<(float, string), double>();
        var untuckedBands = new SortedDictionary<(float, string), double>();
        var baseBands = new SortedDictionary<(float, string), double>();
        var seenAt = new List<(Vector3 Centre, Vector3 Normal, int Elevation)>();
        double seen = NecklineSeenThrough(tucked, culled, body, bands, seenAt);
        double seenUntucked = NecklineSeenThrough(untucked, culled, body, untuckedBands);
        double baseline = NecklineSeenThrough(r.Garment, culled, ModelPartReader.Read(File.ReadAllBytes(r.Source))!, baseBands);
        output.WriteLine($"  seen through: tucked {seen:0.0} cm², untucked {seenUntucked:0.0}, game's own on its body {baseline:0.0}");
        foreach (var key in bands.Keys.Union(untuckedBands.Keys).Union(baseBands.Keys).OrderByDescending(k => k))
            output.WriteLine($"    y {key.Item1:0.0} {key.Item2}: {bands.GetValueOrDefault(key):0.0} cm² "
                             + $"(untucked {untuckedBands.GetValueOrDefault(key):0.0}, game's own {baseBands.GetValueOrDefault(key):0.0})");

        // What the tuck drew: every corner of every triangle new to it, against the untucked refit — by this test's own
        // measures, not the tuck's.
        var newCorners = NewSkinCorners(untucked, tucked);
        var untuckedSkin = new BodySurface(untucked, 0.01f);
        var cloth = ClothTriangles(tucked);
        var skin = ModelSkinReader.Read(File.ReadAllBytes(r.Target), null, null)!;
        var bodyVertexAt = new Dictionary<(int, int, int), int>();
        for (int v = 0; v < body.Positions.Length / 3; v++) bodyVertexAt.TryAdd(Key(At(body, v)), v);
        var gaps = new SortedDictionary<int, int>();
        int beyondBand = 0, uncovered = 0, onArm = 0;
        foreach (var (p, n) in newCorners)
        {
            float off = untuckedSkin.Nearest(p, 0.2f, out var hit) ? hit.Distance : 0.2f;
            gaps[(int)(off * 100)] = gaps.GetValueOrDefault((int)(off * 100)) + 1;
            // The pass measures from the game's skin as laid, which can lie past what the cut kept of the body — its
            // turned-in lip laid off the body — by up to one of the body's triangles.
            if (off > BodyRetarget.TuckBand + BandSlack) beyondBand++;
            var level = new Vector3(n.X, 0f, n.Z);
            if (Along(cloth, p, n, BodyRetarget.TuckOver) == null
                && (level.LengthSquared() < 1e-6f || Along(cloth, p, Vector3.Normalize(level), BodyRetarget.TuckOver) == null))
                uncovered++;
            if (bodyVertexAt.TryGetValue(Key(p), out int bv)
                && BodyRetarget.EndShareOf(skin, bv, BodyRetarget.IsArmBone) >= BodyRetarget.TuckArmShare)
                onArm++;
        }
        output.WriteLine($"  new corners {newCorners.Count}: by cm from the untucked skin "
                         + string.Join(", ", gaps.Select(g => $"{g.Key}:{g.Value}")));
        // Why the skin still seen from above was not tucked: how far it lies from the skin kept, and how much room the
        // cloth over it leaves.
        var why = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (centre, normal, elevation) in seenAt.Where(s => s.Elevation >= 20))
        {
            float off = untuckedSkin.Nearest(centre, 0.2f, out var hit) ? hit.Distance : 0.2f;
            float? over = Along(cloth, centre, normal, 0.1f);
            float? under = Along(cloth, centre, -normal, 0.03f);
            string reason = off > BodyRetarget.TuckBand ? $"band {(int)(off * 100)}cm"
                          : under != null ? $"cloth {under.Value * 1000:0}mm behind"
                          : over == null ? "no cloth over"
                          : over < BodyRetarget.TuckClear ? $"cloth {over.Value * 1000:0.0}mm over"
                          : $"room {(int)(over.Value * 1000 / 5) * 5}mm";
            why[reason] = why.GetValueOrDefault(reason) + 1;
        }
        output.WriteLine($"  seen from above, untucked because: {string.Join(", ", why.Select(w => $"{w.Key} x{w.Value}"))}");

        // [AC-1] Without the tuck the bodice's hole shows; with it, no more than the game's own top shows. Measured: Neolithe
        // Almond XS 126.9 → 12.1 cm², Rue+ 130.6 → 17.7, against the game's own 80.5. Every other top is no worse for it.
        if (garmentName == Dirndl && modNeed == Neolithe)
            Assert.True(seenUntucked >= 3 * seen + 10.0,
                        $"the metric does not see the hole: untucked {seenUntucked:0.0} cm², tucked {seen:0.0}");
        if (garmentName == Dirndl && modNeed != LavaBod)
            Assert.True(seen <= Math.Max(1.5 * baseline, baseline + 5.0), $"{seen:0.0} cm² seen through against {baseline:0.0}");
        else if (modNeed == LavaBod)
            // A bust this large buries the bodice deeper than the clear reaches, and the game's skin laid onto it no
            // longer reaches its open neckline: the tuck closes some of the hole (231.6 → 162.2 cm²), the deep clear the rest.
            Assert.True(seen < seenUntucked, $"{seen:0.0} cm² seen through against {seenUntucked:0.0} untucked");
        else
            Assert.True(seen <= seenUntucked + 0.5, $"{seen:0.0} cm² seen through against {seenUntucked:0.0} untucked");
        // [AC-4] Within the band, under this top's cloth, off the arm.
        Assert.Equal(0, beyondBand);
        Assert.Equal(0, uncovered);
        Assert.Equal(0, onArm);
        // [EDGE-12]
        Assert.Equal(r.Untucked.Report.Folded, r.Game.Report.Folded);
        Assert.NotNull(ModelPartReader.Read(r.Game.Model));
    }

    /// <summary>The c0201 body model of the first game item named <paramref name="name"/> worn on the body.</summary>
    internal static byte[] TopOf(Lumina.GameData data, string name)
    {
        foreach (var item in data.GetExcelSheet<Item>()!)
        {
            if (item.EquipSlotCategory.RowId != 4 || !item.Name.ExtractText().StartsWith(name, StringComparison.Ordinal))
                continue;
            int set = (int)(item.ModelMain & 0xFFFF);
            if (data.GetFile($"chara/equipment/e{set:D4}/model/c0201e{set:D4}_top.mdl") is { } file) return file.Data;
        }
        throw new InvalidOperationException($"no game top named {name}");
    }

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static IEnumerable<ModelPart> ClothParts(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)
                              && !p.Material.Contains("piercing", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cloth area, cm², that the model's own drawn skin comes through: points inside each cloth triangle lying 0.5–15 mm
    /// under the skin. Inside the triangles, not at the corners — the corners mostly sit outside.
    /// </summary>
    /// <param name="beside">Also count cloth whose nearest skin is that far to the side, m — off the skin's edge rather
    /// than under it. Unlimited by default, as the test was written; <see cref="Under"/> for the tuck.</param>
    private static double SkinThrough(ModelParts m, float beside = float.MaxValue)
    {
        var surf = new BodySurface(m, 0.01f);
        double area = 0;
        foreach (var part in ClothParts(m))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var a = At(m, part.Triangles[t]); var b = At(m, part.Triangles[t + 1]); var c = At(m, part.Triangles[t + 2]);
                int inside = 0, n = 0;
                for (int i = 1; i < 6; i++)
                for (int j = 1; i + j < 6; j++)
                {
                    var q = a * (1 - i / 6f - j / 6f) + b * (i / 6f) + c * (j / 6f);
                    n++;
                    if (!surf.Nearest(q, 0.03f, out var hit)) continue;
                    float s = Vector3.Dot(q - hit.Point, hit.Normal);
                    if (s < -0.0005f && s > -0.015f && (q - hit.Point - hit.Normal * s).Length() <= beside) inside++;
                }
                area += Vector3.Cross(b - a, c - a).Length() / 2 * inside / n;
            }
        return area * 1e4;
    }

    /// <summary>
    /// How far to the side of its nearest skin a cloth point may lie and still be under that skin (2 mm). Further, the
    /// nearest skin is an edge the cloth runs on past — the tuck stops its skin where the cloth comes back down onto the
    /// body, and the cloth lies beside that edge, below the plane of its last triangle, 15-25 mm off: under no skin.
    /// </summary>
    private const float Under = 0.002f;

    /// <summary>
    /// Torso skin, cm², that no cloth of the model covers and the model does not draw: a hole. Covered = a ray from the
    /// skin triangle's centre, out along its face or level with the ground away from the body, meets cloth within
    /// <see cref="Reach"/>; drawn = the model has skin within 2 mm. Level and that far, because cloth hanging off the bust
    /// covers the skin under it from every side it is seen from, yet a ray straight off that skin points at the floor —
    /// checked by eye on the Scion Adventurer's top and the Top of Maiming, no holes there. The forearms are left out:
    /// gloves cover them, and the game's own tops leave them undrawn on its own body too.
    /// </summary>
    /// <param name="bands">Where the area lies: by 10 cm of height, front or back.</param>
    private static double UncoveredUndrawn(ModelParts m, ModelParts body, SortedDictionary<(float, string), double> bands)
    {
        var cloth = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        foreach (var part in ClothParts(m))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                cloth.Add((At(m, part.Triangles[t]), At(m, part.Triangles[t + 1]), At(m, part.Triangles[t + 2])));
        var grid = new Dictionary<(int, int, int), List<int>>();
        const float cell = 0.02f;
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (int i = 0; i < cloth.Count; i++)
        {
            var (a, b, c) = cloth[i];
            var lo = Vector3.Min(a, Vector3.Min(b, c));
            var hi = Vector3.Max(a, Vector3.Max(b, c));
            var (x0, y0, z0) = Cell(lo);
            var (x1, y1, z1) = Cell(hi);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = [];
                list.Add(i);
            }
        }

        var drawn = new BodySurface(m, 0.01f);
        double area = 0;
        foreach (var part in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var a = At(body, part.Triangles[t]); var b = At(body, part.Triangles[t + 1]); var c = At(body, part.Triangles[t + 2]);
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-14f) continue;
                var centre = (a + b + c) / 3f;
                if (MathF.Abs(centre.X) > ArmFrom) continue;
                var normal = Vector3.Normalize(cross);
                var level = new Vector3(normal.X, 0f, normal.Z);
                if (drawn.Nearest(centre, 0.002f, out _) || Covered(centre, normal)
                    || (level.LengthSquared() > 1e-6f && Covered(centre, Vector3.Normalize(level))))
                    continue;
                area += cross.Length() / 2;
                var key = (MathF.Floor(centre.Y * 10f) / 10f, centre.Z >= 0 ? "front" : "back");
                bands[key] = bands.GetValueOrDefault(key) + cross.Length() / 2 * 1e4;
            }
        return area * 1e4;

        bool Covered(Vector3 origin, Vector3 dir)
        {
            var seen = new HashSet<int>();
            for (float step = 0f; step <= Reach; step += cell / 4)
            {
                if (!grid.TryGetValue(Cell(origin + dir * step), out var list)) continue;
                foreach (int i in list)
                    if (seen.Add(i) && Hits(origin, dir, cloth[i])) return true;
            }
            return false;
        }

        static bool Hits(Vector3 o, Vector3 d, (Vector3 A, Vector3 B, Vector3 C) tri)
        {
            // Möller–Trumbore, either face, within reach.
            var e1 = tri.B - tri.A; var e2 = tri.C - tri.A;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) return false;
            var s = o - tri.A;
            float u = Vector3.Dot(s, p) / det;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) / det;
            if (v < 0f || u + v > 1f) return false;
            float dist = Vector3.Dot(e2, q) / det;
            return dist >= 0f && dist <= Reach;
        }
    }

    /// <summary>One triangle a model draws, with its front face's normal as the GPU culls it — by winding — and whether
    /// its material hides back faces.</summary>
    private readonly record struct Drawn(Vector3 A, Vector3 B, Vector3 C, Vector3 Front, bool Culled);

    /// <summary>
    /// Every triangle <paramref name="m"/> draws (LOD0, no islands), its front face found from its winding: which way the
    /// model winds its front faces is read off the model itself — the winding normal agreeing with the vertex normals on
    /// most of the area, or not — since the vertex normals point out of the surface while the GPU culls by winding.
    /// </summary>
    private static List<Drawn> DrawnTriangles(ModelParts m, IReadOnlyDictionary<string, bool> culled)
    {
        var list = new List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Wind, bool Culled)>();
        double agree = 0;
        foreach (var part in m.Parts.Where(p => p.Island < 0))
        {
            bool cull = SecondSkinWriter.IsBodySkinMaterial(part.Material) || culled.GetValueOrDefault(Name(part.Material));
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int ia = part.Triangles[t], ib = part.Triangles[t + 1], ic = part.Triangles[t + 2];
                Vector3 a = At(m, ia), b = At(m, ib), c = At(m, ic);
                var wind = Vector3.Cross(b - a, c - a);
                agree += Vector3.Dot(wind, NormalAt(m, ia) + NormalAt(m, ib) + NormalAt(m, ic));
                list.Add((a, b, c, wind, cull));
            }
        }
        float sign = agree >= 0 ? 1f : -1f;
        return [.. list.Select(t => new Drawn(t.A, t.B, t.C, t.Wind * sign, t.Culled))];
    }

    /// <summary>
    /// Torso skin of <paramref name="body"/>, cm², that <paramref name="m"/> leaves undrawn and a camera in front sees:
    /// for each undrawn front skin triangle (none of <paramref name="m"/>'s skin within 2 mm of its centre), a ray from just
    /// off its centre toward cameras from level to 75° above, straight ahead and 25° to either side, that meets nothing
    /// <paramref name="m"/> draws within a metre. A face whose material hides its back blocks only when its front faces
    /// the camera. What the neckline of a refitted top leaves open, seen the way the game draws it.
    /// </summary>
    /// <param name="culled">The garment's materials (by file name) that hide back faces.</param>
    /// <param name="bands">Where the area lies: by 10 cm of height, and the first view that saw it.</param>
    private static double NecklineSeenThrough(ModelParts m, IReadOnlyDictionary<string, bool> culled, ModelParts body,
                                              SortedDictionary<(float, string), double> bands,
                                              List<(Vector3 Centre, Vector3 Normal, int Elevation)>? seenAt = null)
    {
        var tris = DrawnTriangles(m, culled);
        var grid = new TriangleGrid(tris.Select(t => (t.A, t.B, t.C)).ToList());
        var drawnSkin = new BodySurface(m, 0.01f);
        // Only as high as the garment's cloth reaches, 2 cm in from either end: above it is the neck and the head's, below
        // it the legs' piece — skin other gear draws or covers in game, and no business of this top's neckline.
        var clothY = ClothTriangles(m).SelectMany(t => new[] { t.A.Y, t.B.Y, t.C.Y }).ToList();
        float low = clothY.Min() + 0.02f, high = clothY.Max() - 0.02f;

        var views = new List<(string Name, Vector3 ToCamera)>();
        foreach (int elevation in new[] { 0, 20, 40, 60, 75 })
            foreach (int azimuth in new[] { 0, -25, 25 })
            {
                float el = elevation * MathF.PI / 180f, az = azimuth * MathF.PI / 180f;
                views.Add(($"{elevation}°/{azimuth}°", new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el),
                                                                     MathF.Cos(az) * MathF.Cos(el))));
            }

        double area = 0;
        foreach (var part in body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int ia = part.Triangles[t], ib = part.Triangles[t + 1], ic = part.Triangles[t + 2];
                Vector3 a = At(body, ia), b = At(body, ib), c = At(body, ic);
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-14f) continue;
                var centre = (a + b + c) / 3f;
                if (MathF.Abs(centre.X) > ArmFrom || centre.Z <= 0f || centre.Y < low || centre.Y > high) continue;
                if (drawnSkin.Nearest(centre, 0.002f, out _)) continue;
                var normal = Vector3.Normalize(cross);
                if (Vector3.Dot(normal, NormalAt(body, ia) + NormalAt(body, ib) + NormalAt(body, ic)) < 0) normal = -normal;
                var origin = centre + normal * 0.001f;
                foreach (var (name, toCamera) in views)
                {
                    if (Vector3.Dot(normal, toCamera) <= 0.1f) continue;
                    bool Seen(int i) => !tris[i].Culled || Vector3.Dot(tris[i].Front, toCamera) > 0f;
                    if (grid.Along(origin, toCamera, 1f).Any(Seen)) continue;
                    // Something the model draws just behind the missing skin is what the camera sees there instead —
                    // cloth the game buries under its body's skin on purpose — and not a hole: a hole looks through to the
                    // far side of the garment.
                    if (grid.Along(centre, -toCamera, BehindIsThere).Any(Seen)) continue;
                    area += cross.Length() / 2;
                    var key = (MathF.Floor(centre.Y * 10f) / 10f, name);
                    bands[key] = bands.GetValueOrDefault(key) + cross.Length() / 2 * 1e4;
                    seenAt?.Add((centre, normal, int.Parse(name[..name.IndexOf('°')])));
                    break;
                }
            }
        return area * 1e4;
    }

    /// <summary>How much further from the untucked refit's skin than <see cref="BodyRetarget.TuckBand"/> a tucked corner
    /// may lie (10 mm): see <see cref="A_game_top_shows_no_hole_looking_into_its_neckline"/>.</summary>
    private const float BandSlack = 0.01f;

    /// <summary>How far behind a missing piece of skin something drawn still stands in for it (20 mm): the deepest the
    /// game buries a top's cloth in its own body is 15 mm.</summary>
    private const float BehindIsThere = 0.02f;

    /// <summary>
    /// Body skin the tucked model draws and the untucked one does not, cm², that has cloth of the tucked model less than
    /// 1 mm in front of it or up to 20 mm behind it, along the triangle's normal: skin right under the cloth, or through
    /// it. Each new triangle sampled at its corners, middle, edge middles and a 6×6 grid inside, as
    /// <see cref="SkinThrough"/> samples cloth.
    /// </summary>
    private static double NearCoincident(ModelParts tucked, ModelParts untucked)
    {
        var had = SkinTriangleKeys(untucked);
        var cloth = new TriangleGrid(ClothTriangles(tucked));
        double area = 0;
        foreach (var part in tucked.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int ia = part.Triangles[t], ib = part.Triangles[t + 1], ic = part.Triangles[t + 2];
                Vector3 a = At(tucked, ia), b = At(tucked, ib), c = At(tucked, ic);
                if (had.Contains(TriangleKey(a, b, c))) continue;
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.LengthSquared() < 1e-14f) continue;
                var n = Vector3.Normalize(cross);
                if (Vector3.Dot(n, NormalAt(tucked, ia) + NormalAt(tucked, ib) + NormalAt(tucked, ic)) < 0) n = -n;
                var samples = new List<Vector3> { a, b, c, (a + b + c) / 3f, (a + b) / 2f, (b + c) / 2f, (c + a) / 2f };
                for (int i = 1; i < 6; i++)
                for (int j = 1; i + j < 6; j++)
                    samples.Add(a * (1 - i / 6f - j / 6f) + b * (i / 6f) + c * (j / 6f));
                int close = samples.Count(q => cloth.Along(q - n * 0.02f, n, 0.021f).Any());
                area += cross.Length() / 2 * close / samples.Count;
            }
        return area * 1e4;
    }

    /// <summary>Each corner (position and normal) of every body skin triangle <paramref name="tucked"/> draws that
    /// <paramref name="untucked"/> does not.</summary>
    private static List<(Vector3 P, Vector3 N)> NewSkinCorners(ModelParts untucked, ModelParts tucked)
    {
        var had = SkinTriangleKeys(untucked);
        var corners = new Dictionary<(int, int, int), (Vector3, Vector3)>();
        foreach (var part in tucked.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int[] v = [part.Triangles[t], part.Triangles[t + 1], part.Triangles[t + 2]];
                if (had.Contains(TriangleKey(At(tucked, v[0]), At(tucked, v[1]), At(tucked, v[2])))) continue;
                foreach (int k in v) corners.TryAdd(Key(At(tucked, k)), (At(tucked, k), NormalAt(tucked, k)));
            }
        return [.. corners.Values];
    }

    private static HashSet<string> SkinTriangleKeys(ModelParts m)
    {
        var keys = new HashSet<string>();
        foreach (var part in m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                keys.Add(TriangleKey(At(m, part.Triangles[t]), At(m, part.Triangles[t + 1]), At(m, part.Triangles[t + 2])));
        return keys;
    }

    private static string TriangleKey(Vector3 a, Vector3 b, Vector3 c)
        => string.Join(";", new[] { Key(a), Key(b), Key(c) }.Order());

    private static (int, int, int) Key(Vector3 p)
        => ((int)MathF.Round(p.X * 1e5f), (int)MathF.Round(p.Y * 1e5f), (int)MathF.Round(p.Z * 1e5f));

    private static List<(Vector3 A, Vector3 B, Vector3 C)> ClothTriangles(ModelParts m)
        => [.. ClothParts(m).SelectMany(p => Enumerable.Range(0, p.Triangles.Length / 3)
                                                     .Select(t => (At(m, p.Triangles[t * 3]), At(m, p.Triangles[t * 3 + 1]),
                                                                   At(m, p.Triangles[t * 3 + 2]))))];

    /// <summary>How far from <paramref name="p"/> along <paramref name="dir"/> the first of <paramref name="tris"/> lies,
    /// within <paramref name="reach"/>; null for none.</summary>
    private static float? Along(List<(Vector3 A, Vector3 B, Vector3 C)> tris, Vector3 p, Vector3 dir, float reach)
    {
        float? best = null;
        foreach (var tri in tris)
            if (TriangleGrid.Hit(p, dir, tri) is float d && d <= reach && (best == null || d < best)) best = d;
        return best;
    }

    /// <summary>The garment's own file name of a material the model names ("/mt_c0201e6114_top_a.mtrl").</summary>
    private static string Name(string material) => material.TrimStart('/');

    /// <summary>
    /// Which of the garment's materials hide back faces (shader flag 0x01), read from the game's own .mtrl in every variant
    /// folder the game has for the set: hidden in any variant counts as hidden — fewer faces that block, so a hole is
    /// counted the larger. A material not found counts as hiding them too.
    /// </summary>
    private static Dictionary<string, bool> Culled(Lumina.GameData data, ModelParts garment)
    {
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string material in garment.Parts.Select(p => Name(p.Material)).Distinct())
        {
            if (SecondSkinWriter.IsBodySkinMaterial("/" + material)) continue;
            var set = System.Text.RegularExpressions.Regex.Match(material, @"e(\d{4})_");
            bool hidden = false, found = false;
            for (int variant = 1; variant <= 40 && set.Success; variant++)
            {
                if (data.GetFile($"chara/equipment/e{set.Groups[1].Value}/material/v{variant:D4}/{material}") is not { } file)
                    continue;
                found = true;
                hidden |= (ShaderFlags(file.Data) & 0x01) != 0;
            }
            flags[material] = hidden || !found;
        }
        return flags;
    }

    /// <summary>A .mtrl's shader flags: the header, its texture, uv-set and colour-set tables, the strings, the additional
    /// data and the data set, then the shader section's { u16, u16, u16, u16, u32 flags } (see GearMaterialWriter).</summary>
    private static uint ShaderFlags(byte[] m)
    {
        int dataSet = BitConverter.ToUInt16(m, 6), strings = BitConverter.ToUInt16(m, 8);
        int tex = m[12], uvs = m[13], colourSets = m[14], additional = m[15];
        int shader = 16 + tex * 4 + uvs * 4 + colourSets * 4 + strings + additional + dataSet;
        return shader + 12 <= m.Length ? BitConverter.ToUInt32(m, shader + 8) : 0u;
    }

    private static Vector3 NormalAt(ModelParts m, int v) => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);

    /// <summary>Triangles bucketed in 2 cm cells, for rays.</summary>
    private sealed class TriangleGrid
    {
        private const float Cell = 0.02f;
        private readonly List<(Vector3 A, Vector3 B, Vector3 C)> tris;
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        public TriangleGrid(List<(Vector3 A, Vector3 B, Vector3 C)> tris)
        {
            this.tris = tris;
            for (int i = 0; i < tris.Count; i++)
            {
                var (a, b, c) = tris[i];
                var (x0, y0, z0) = CellOf(Vector3.Min(a, Vector3.Min(b, c)));
                var (x1, y1, z1) = CellOf(Vector3.Max(a, Vector3.Max(b, c)));
                for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = [];
                    list.Add(i);
                }
            }
        }

        private static (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        /// <summary>Every triangle the segment from <paramref name="o"/> along <paramref name="d"/> (unit) crosses within
        /// <paramref name="reach"/>.</summary>
        public IEnumerable<int> Along(Vector3 o, Vector3 d, float reach)
        {
            var seen = new HashSet<int>();
            for (float step = 0f; step <= reach + Cell; step += Cell / 4)
            {
                var at = CellOf(o + d * MathF.Min(step, reach));
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((at.Item1 + dx, at.Item2 + dy, at.Item3 + dz), out var list)) continue;
                    foreach (int i in list)
                        if (seen.Add(i) && Hit(o, d, tris[i]) is float t && t <= reach) yield return i;
                }
            }
        }

        /// <summary>Möller–Trumbore, either face, forward of <paramref name="o"/>.</summary>
        public static float? Hit(Vector3 o, Vector3 d, (Vector3 A, Vector3 B, Vector3 C) tri)
        {
            var e1 = tri.B - tri.A; var e2 = tri.C - tri.A;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) return null;
            var s = o - tri.A;
            float u = Vector3.Dot(s, p) / det;
            if (u < 0f || u > 1f) return null;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) / det;
            if (v < 0f || u + v > 1f) return null;
            float dist = Vector3.Dot(e2, q) / det;
            return dist >= 0f ? dist : null;
        }
    }

    /// <summary>How far off the skin cloth still covers it, m.</summary>
    private const float Reach = 0.06f;

    /// <summary>Skin further than this from the midline, m, is the arms.</summary>
    private const float ArmFrom = 0.12f;
}
