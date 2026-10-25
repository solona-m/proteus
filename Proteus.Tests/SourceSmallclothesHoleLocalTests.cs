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
/// The game's No.2 Type B Dress refitted in the Studio from the game's body onto [HS] Muse+ (YAS) "Defined - Bare", a flat
/// chest: reported as "muse has a flat chest and the port still has breasts". The game's chest has no skin under its bra
/// — the bust is the bra — and the refit read the old body as its skin alone, so the cloth over the bust followed the
/// hole's rim. Needs the game's data and the body mods installed.
/// </summary>
public class SourceSmallclothesHoleLocalTests(ITestOutputHelper output)
{
    private const string MuseRoot = LocalData.Mods + ":hs-Muse+-3.2.2-LbM";
    private const string MuseBare = MuseRoot + "/files/smallclothes chest/defined - bare/chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string LavaBod = LocalData.Mods + ":LavaBod+ 2.2";
    private const string Teardrop = "common/11/c0201e0000_top.mdl";

    private const string Dress = "chara/equipment/e0649/model/c0201e0649_top.mdl";

    private static readonly BodyRetarget.Tuning Old = new() { NoSourceWorn = true };

    private sealed record Studio(BodyRetarget.Planned Planned, byte[] Drawn, int Folded, SecondSkinWriter.Vec3[] Laid,
                                 List<string> Log, BodyRetarget.SlotPair Pair);

    /// <summary>The Studio's whole-garment refit, as <c>BodyRetargetPanel</c> runs it with the screenshot's settings — the
    /// new body's skin, hidden skin removed, cloth pushed out, no piercings — and no other slots or masks, as there were
    /// none: the game's body for the dress's own slot onto <paramref name="target"/>.</summary>
    /// <param name="gameGear">The Studio as it runs the game's own top since build 1355: the automatic refit's skin rule —
    /// the body cleared, the deep clear and the standoff pass (see <see cref="RetargetInputs.StudioSkin"/>). False runs
    /// it as the reported build did, the boxes as the screenshot shows them.</param>
    private static Studio Refit(Lumina.GameData data, string target, BodyRetarget.Tuning? tuning = null, string garmentPath = Dress,
                                bool gameGear = false)
    {
        var rule = RetargetInputs.StudioSkin(replaceSkin: true, cutHidden: true, clearBody: true, gameGear, "_top", crossSex: false);
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_top", "0201")[0]);
        byte[] bytes = data.GetFile(garmentPath)!.Data;
        var garment = ModelPartReader.Read(bytes)!;
        Assert.Null(BodyRetarget.BuildPair("_top", File.ReadAllBytes(source), File.ReadAllBytes(target), "_top", false,
                                           null, null, uv, out var pair, out _, BodyRetarget.BodyMaterialReader(target)));
        var lines = new List<string>();
        SecondSkinWriter.Vec3[]? laid = null;
        var planned = BodyRetarget.WithTuning(tuning ?? new BodyRetarget.Tuning(), () => BodyRetarget.Plan(
            garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true, acrossBodies: true,
            clearBody: true, cutHidden: true, keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)),
            log: lines.Add, tuckSlot: rule.TuckSlot, deepClear: rule.DeepClear, keepStandoff: rule.KeepStandoff,
            carryPiercings: false, liftUnderbust: true, relaxSinkGuard: rule.RelaxSinkGuard,
            trace: (stage, delta) =>
            {
                if (stage == "lay") laid = delta;
            }));
        var drawn = RefitCore.MatchSkinToBody(planned, source, target, uv, false, log).Model;
        return new Studio(planned, drawn, Folded(garment, bytes, planned), laid!, lines, pair);
    }

    private static Lumina.GameData Game() => new(LocalData.Path(LocalData.GameData));

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>The front-most point of every LOD0 mesh along a ray toward -z at (x, y), m; NaN where nothing is hit.</summary>
    private static float Front(byte[] model, float x, float y, bool skinOnly = false)
    {
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(model, out var pos, out _, out var tris, out _, out _, skinOnly: skinOnly));
        float best = float.NaN;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t] * 3, b = tris[t + 1] * 3, c = tris[t + 2] * 3;
            float x0 = pos[a], y0 = pos[a + 1], x1 = pos[b], y1 = pos[b + 1], x2 = pos[c], y2 = pos[c + 1];
            float d = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (MathF.Abs(d) < 1e-12f) continue;
            float l0 = ((y1 - y2) * (x - x2) + (x2 - x1) * (y - y2)) / d;
            float l1 = ((y2 - y0) * (x - x2) + (x0 - x2) * (y - y2)) / d;
            float l2 = 1 - l0 - l1;
            if (l0 < 0 || l1 < 0 || l2 < 0) continue;
            float z = l0 * pos[a + 2] + l1 * pos[b + 2] + l2 * pos[c + 2];
            if (float.IsNaN(best) || z > best) best = z;
        }
        return best;
    }

    /// <summary>The garment's triangles, its own skin with its cloth, facing the other way from how its author drew them
    /// once every pass has moved them: the folds on what is written.</summary>
    private static int Folded(ModelParts garment, byte[] bytes, BodyRetarget.Planned plan)
        => SkinTuckSheet.FoldedAgainstAuthored(garment, ModelPartReader.Read(MeshVolumeService.Inflate(bytes, plan.Edit).Model)!,
                                               withSkin: true);

    private static float Median(List<float> g) => g[g.Count / 2];

    private static float P90(List<float> g) => g[(int)(g.Count * 0.9f)];

    /// <summary>
    /// For each cloth point the dress's author put over the game's bra (outside it, its nearest point of the game's body
    /// as worn a bra triangle over the skin's hole), how far it stands off the new body as worn once the skin is laid,
    /// less how far it stood off the bra: 0 where the cloth keeps its author's gap. Sorted.
    /// </summary>
    private static List<float> BustGaps(Lumina.GameData data, string target, Studio refit)
    {
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        var vanillaBody = ModelPartReader.Read(File.ReadAllBytes(vanilla.PathOf(vanilla.For("_top", "0201")[0])))!;
        var over = BodyRetarget.SmallclothesOverHoles(vanillaBody, new BodySurface(vanillaBody, 0.01f))!;
        var worn = BodySurface.OfSkinAnd(vanillaBody, 0.01f, over);
        var newBody = BodySurface.OfBodyWorn(ModelPartReader.Read(File.ReadAllBytes(target))!, 0.01f);
        var garment = ModelPartReader.Read(data.GetFile(Dress)!.Data)!;
        var sets = BodyRetarget.Sets.From(garment, new HashSet<int>(),
                                          BodyRetarget.PartedSkinMeshes(garment, [refit.Pair], "_top", true, null));
        Assert.Equal(sets.NodeCount, refit.Laid.Length);
        var gaps = new List<float>();
        foreach (int n in sets.ClothNodes)
        {
            var p0 = new Vector3(sets.NodeAt[n].X, sets.NodeAt[n].Y, sets.NodeAt[n].Z);
            if (!worn.Nearest(p0, 0.04f, out var h0) || !over[h0.A] || !over[h0.B] || !over[h0.C]) continue;
            float s0 = Vector3.Dot(p0 - h0.Point, h0.Normal);
            if (s0 < 0f) continue;
            var p1 = p0 + new Vector3(refit.Laid[n].X, refit.Laid[n].Y, refit.Laid[n].Z);
            if (!newBody.Nearest(p1, 0.1f, out var h1)) continue;
            gaps.Add(Vector3.Dot(p1 - h1.Point, h1.Normal) - s0);
        }
        gaps.Sort();
        return gaps;
    }

    [LocalDataFact(LocalData.GameData, MuseBare)]
    public void The_Type_B_dress_on_Muse_bare_follows_the_flat_chest()
    {
        // [AC-bra-hole-transfer-1][EDGE-bra-hole-transfer-15] With nipple smoothing on, as it ships.
        var data = Game();
        string muse = LocalData.Path(MuseBare);
        var old = Refit(data, muse, Old);

        // The harness is the Studio's: the old reading reproduces the in-game log of the reported refit exactly.
        var r = old.Planned.Report;
        var swap = r.Swap.GetValueOrDefault();
        Assert.Equal((29, 88, 1095, 1956, 1, 1, 2), (r.Snapped, r.Missed, r.Pushed, swap.Reweighted, swap.Kept,
                                                     swap.Posed, swap.LostShapes));
        Assert.InRange(r.WorstMove, 0.03035f, 0.03045f);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var now = Refit(data, muse);
        output.WriteLine($"refit {clock.ElapsedMilliseconds} ms; {now.Planned.Report.SourceSmallclothes} underwear points landed");
        Assert.True(now.Planned.Report.SourceSmallclothes > 0);
        Assert.Contains(now.Log, l => l.StartsWith("source smallclothes: ", StringComparison.Ordinal));
        Assert.DoesNotContain(old.Log, l => l.StartsWith("source smallclothes: ", StringComparison.Ordinal));

        // Inside the pass, once the skin is laid: cloth the author fitted over the game's bra keeps that gap over Muse.
        var fixedGaps = BustGaps(data, muse, now);
        var oldGaps = BustGaps(data, muse, old);
        output.WriteLine($"bust band: {fixedGaps.Count} cloth points; gap over Muse less the author's over the bra: "
                         + $"median {Median(fixedGaps) * 1000:0.0} mm, p90 {P90(fixedGaps) * 1000:0.0} (old reading "
                         + $"median {Median(oldGaps) * 1000:0.0}, p90 {P90(oldGaps) * 1000:0.0})");
        Assert.True(fixedGaps.Count >= 50, $"only {fixedGaps.Count} bust points");
        Assert.True(Median(oldGaps) >= 0.008f, "the old reading no longer shows the fault");
        Assert.True(MathF.Abs(Median(fixedGaps)) <= 0.003f, $"median {Median(fixedGaps) * 1000:0.0} mm");
        Assert.True(MathF.Abs(P90(fixedGaps)) <= 0.006f, $"p90 {P90(fixedGaps) * 1000:0.0} mm");

        // As written: the bust well in from where the old reading left it, at the fullest, both sides.
        foreach (float x in new[] { -0.06f, 0.06f })
            foreach (float y in new[] { 1.24f, 1.26f, 1.28f })
            {
                float was = Front(old.Drawn, x, y), is_ = Front(now.Drawn, x, y);
                output.WriteLine($"  x {x:+0.00;-0.00} y {y:0.00}: {was * 1000:0} -> {is_ * 1000:0} mm");
                Assert.True(is_ <= was - 0.008f, $"at x {x}, y {y}: {is_ * 1000:0} mm against {was * 1000:0} before");
            }

        // Nothing turned over that was not, and no more skin through the cloth.
        int foldedNow = now.Folded, foldedOld = old.Folded;
        double throughNow = VanillaGearRefitLocalTests.SkinThrough(ModelPartReader.Read(now.Drawn)!);
        double throughOld = VanillaGearRefitLocalTests.SkinThrough(ModelPartReader.Read(old.Drawn)!);
        output.WriteLine($"folded {foldedNow} (old {foldedOld}); skin through {throughNow:0.0} cm² (old {throughOld:0.0})");
        Assert.True(foldedNow <= foldedOld, $"folded {foldedNow} against {foldedOld}");
        Assert.True(throughNow <= throughOld + 0.5, $"skin through {throughNow:0.0} cm² against {throughOld:0.0}");

        // And the Studio as it now runs the game's own top, the automatic refit's way: the same, against the same way read
        // without the bra.
        var gameOld = Refit(data, muse, Old, gameGear: true);
        var gameNow = Refit(data, muse, gameGear: true);
        foreach (float x in new[] { -0.06f, 0.06f })
            foreach (float y in new[] { 1.24f, 1.26f, 1.28f })
            {
                float was = Front(gameOld.Drawn, x, y), is_ = Front(gameNow.Drawn, x, y);
                output.WriteLine($"  game-gear Studio x {x:+0.00;-0.00} y {y:0.00}: {was * 1000:0} -> {is_ * 1000:0} mm");
                Assert.True(is_ <= was - 0.008f, $"game-gear Studio at x {x}, y {y}: {is_ * 1000:0} mm against {was * 1000:0}");
            }
        double gameThroughNow = VanillaGearRefitLocalTests.SkinThrough(ModelPartReader.Read(gameNow.Drawn)!);
        double gameThroughOld = VanillaGearRefitLocalTests.SkinThrough(ModelPartReader.Read(gameOld.Drawn)!);
        output.WriteLine($"game-gear Studio: folded {gameNow.Folded} (old {gameOld.Folded}); skin through {gameThroughNow:0.0} cm² "
                         + $"(old {gameThroughOld:0.0})");
        Assert.True(gameNow.Folded <= gameOld.Folded, $"folded {gameNow.Folded} against {gameOld.Folded}");
        Assert.True(gameThroughNow <= gameThroughOld + 0.5, $"skin through {gameThroughNow:0.0} cm² against {gameThroughOld:0.0}");
    }

    public static TheoryData<string, string?> FullerBusts => new()
    {
        { Rue, null },
        { LavaBod, Teardrop },
        { Bibo, null },
    };

    [LocalDataTheory(LocalData.GameData, Rue, LavaBod, Bibo)]
    [MemberData(nameof(FullerBusts))]
    public void The_Type_B_dress_on_fuller_busts_follows_them_out(string modNeed, string? rel)
    {
        // [AC-bra-hole-transfer-2] Other chests: the bra only ever brings the cloth in, so over a fuller chest it stays
        // about where it was; nothing is worse, and the cloth over the bra keeps the author's gap no worse than before.
        var data = Game();
        var mod = BodySizeCatalog.Read(LocalData.Path(modNeed));
        var options = mod.For("_top", "0201");
        string target = mod.PathOf(rel == null ? options[0] : options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel)));
        var old = Refit(data, target, Old);
        var now = Refit(data, target);

        output.WriteLine($"RAN onto {target}");
        foreach (float x in new[] { -0.06f, 0.06f })
            foreach (float y in new[] { 1.24f, 1.26f })
                output.WriteLine($"  x {x:+0.00;-0.00} y {y:0.00}: {Front(old.Drawn, x, y) * 1000:0} -> "
                                 + $"{Front(now.Drawn, x, y) * 1000:0} mm (body {Front(File.ReadAllBytes(target), x, y) * 1000:0})");
        // Inside the pass, once the skin is laid: the author's gap over the bra, kept over the new chest.
        var gaps = BustGaps(data, target, now);
        var oldGaps = BustGaps(data, target, old);
        output.WriteLine($"bust band: {gaps.Count} cloth points; median {Median(gaps) * 1000:0.0} mm, p90 {P90(gaps) * 1000:0.0} "
                         + $"(old reading median {Median(oldGaps) * 1000:0.0}, p90 {P90(oldGaps) * 1000:0.0})");
        Assert.True(MathF.Abs(Median(gaps)) <= MathF.Max(0.004f, MathF.Abs(Median(oldGaps))),
                    $"median {Median(gaps) * 1000:0.0} mm against {Median(oldGaps) * 1000:0.0}");
        int foldedNow = now.Folded, foldedOld = old.Folded;
        var nowModel = ModelPartReader.Read(now.Drawn)!;
        var oldModel = ModelPartReader.Read(old.Drawn)!;
        double throughNow = VanillaGearRefitLocalTests.SkinThrough(nowModel), throughOld = VanillaGearRefitLocalTests.SkinThrough(oldModel);
        double underNow = VanillaGearRefitLocalTests.SkinThrough(nowModel, VanillaGearRefitLocalTests.Under);
        double underOld = VanillaGearRefitLocalTests.SkinThrough(oldModel, VanillaGearRefitLocalTests.Under);
        output.WriteLine($"folded {foldedNow} (old {foldedOld}); skin through {throughNow:0.0} cm² (old {throughOld:0.0}); "
                         + $"under {underNow:0.0} (old {underOld:0.0})");
        Assert.True(foldedNow <= foldedOld, $"folded {foldedNow} against {foldedOld}");
        Assert.True(throughNow <= throughOld + 0.5, $"skin through {throughNow:0.0} cm² against {throughOld:0.0}");
        Assert.True(underNow <= underOld + 0.5, $"under {underNow:0.0} cm² against {underOld:0.0}");
    }

    /// <summary>Chest and legs options of the body mods surveyed, by file: whether any has its own underwear over a
    /// hole in its skin — and so reads differently now.</summary>
    private static IEnumerable<(string Name, string Path)> Surveyed()
    {
        foreach (var need in new[] { Neolithe, Bibo, MuseRoot })
        {
            if (!Directory.Exists(LocalData.Path(need))) continue;
            var catalog = BodySizeCatalog.Read(LocalData.Path(need));
            foreach (string slot in new[] { "_top", "_dwn" })
                foreach (var option in catalog.For(slot, "0201"))
                    yield return ($"{Path.GetFileName(LocalData.Path(need))} {slot} {option.Rel}", catalog.PathOf(option));
        }
    }

    [LocalDataFact(LocalData.GameData, Neolithe, Bibo, MuseRoot)]
    public void Which_bodies_have_no_skin_under_their_underwear()
    {
        // [EDGE-bra-hole-transfer-4] Every race's own game body, and every chest and legs option of Neolithe, Bibo+ and
        // Muse+, as measured: of the game's chests the Midlander woman's (c0201) and the Lalafell woman's (c1201); of its
        // legs five races'; the SFW and binder options of Bibo+ and Muse+; never Neolithe's. Only a game chest is read so
        // (see SlotPair.SourceIsGameChest) — the Lalafell woman's too, never measured on a garment — and the rest are pinned
        // here so a change in them is seen.
        var data = Game();
        var holed = new List<string>();
        foreach (string race in new[] { "0101", "0201", "0301", "0401", "0501", "0601", "0701", "0801", "0901", "1001",
                                        "1101", "1201", "1301", "1401", "1501", "1601", "1701", "1801" })
        {
            var vanilla = VanillaBodyCatalog.Read(race, p => data.GetFile(p)?.Data);
            foreach (string slot in new[] { "_top", "_dwn" })
                foreach (var option in vanilla.For(slot, race))
                    if (Holed(vanilla.PathOf(option))) holed.Add($"vanilla c{race} {slot}");
        }
        int count = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (name, path) in Surveyed())
        {
            count++;
            if (Holed(path)) holed.Add(name);
        }
        output.WriteLine($"{count} body-mod options read in {clock.ElapsedMilliseconds} ms; holed: {string.Join("; ", holed)}");
        Assert.Equal(["vanilla c0101 _dwn", "vanilla c0201 _top", "vanilla c0201 _dwn", "vanilla c0901 _dwn",
                      "vanilla c1101 _dwn", "vanilla c1201 _top", "vanilla c1201 _dwn"],
                     holed.Where(h => h.StartsWith("vanilla")));
        var mods = holed.Where(h => !h.StartsWith("vanilla")).ToList();
        Assert.All(mods, h => Assert.True(h.Contains("sfw") || h.Contains("binder"), h));
        Assert.Equal(7, mods.Count(h => h.StartsWith("Bibo+")));
        Assert.Equal(6, mods.Count(h => h.StartsWith("hs-Muse+")));
        Assert.DoesNotContain(mods, h => h.StartsWith("Neolithe"));

        static bool Holed(string path)
        {
            var body = ModelPartReader.Read(File.ReadAllBytes(path));
            return body != null && BodyRetarget.SmallclothesOverHoles(body, new BodySurface(body, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(body)))) != null;
        }
    }

    [LocalDataFact(LocalData.GameData, Bibo, MuseBare)]
    public void A_body_mods_chest_is_read_as_before_whatever_its_underwear()
    {
        // [AC-bra-hole-transfer-3][EDGE-bra-hole-transfer-1] Bibo+'s first chest, its SFW bra over a hole of its own (see the
        // survey), as the dress's made-on body onto Muse: not the game's chest, so byte for byte the refit it was.
        var data = Game();
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        string source = bibo.PathOf(bibo.For("_top", "0201")[0]);
        string target = LocalData.Path(MuseBare);
        byte[] bytes = data.GetFile(Dress)!.Data;
        var garment = ModelPartReader.Read(bytes)!;
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", false, null, null, uv, out var pair));
        BodyRetarget.Planned Plan() => BodyRetarget.Plan(garment, bytes, [pair], "_top", replaceSkin: true,
                                                         acrossBodies: true, clearBody: true, cutHidden: true);
        var now = Plan();
        var old = BodyRetarget.WithTuning(Old, Plan);
        Assert.Equal(0, now.Report.SourceSmallclothes);
        Assert.Equal(old.Model, now.Model);
    }

    public static TheoryData<string, string, string?> GameTops => new()
    {
        { "Neo-Ishgardian Top of Maiming", Neolithe, "neobelly chest - smallclothes/sfw m.mdl" },
        { "Scion Adventurer's", Neolithe, "neobelly chest - smallclothes/sfw m.mdl" },
        { "Dirndl's Bodice", Neolithe, "neobelly chest - smallclothes/sfw m.mdl" },
        { "Neo-Ishgardian Top of Maiming", Bibo, null },
        { "Dirndl's Bodice", Bibo, null },
        { "Scion Adventurer's", Rue, null },
        { "Dirndl's Bodice", Rue, null },
        { "Neo-Ishgardian Top of Maiming", LavaBod, Teardrop },
        { "Dirndl's Bodice", LavaBod, Teardrop },
    };

    /// <summary>
    /// What reading the game chest's bra costs a game top's automatic refit, by measure, as accepted (build 1359). Rue+'s
    /// small chest is flatter than the game's bra and the cloth follows it in, 3-5 mm under the bust: the skin "uncovered"
    /// is skin the refit cuts away under that cloth — the nipples' tips (Scion), the midline under the bust (Dirndl) — the
    /// cloth now at or just inside where it would be, drawn over nothing. On Bibo+ one triangle of the Top of Maiming at the
    /// bust's side has the skin through it.
    /// </summary>
    private static readonly Dictionary<(string, string, string), double> Accepted = new()
    {
        [("Dirndl's Bodice", Rue, "undrawn")] = 3.5,
        [("Scion Adventurer's", Rue, "undrawn")] = 1.2,
        [("Neo-Ishgardian Top of Maiming", Bibo, "through")] = 1.4,
    };

    private static double Allowed(string garment, string mod, string measure)
        => Accepted.TryGetValue((garment, mod, measure), out double cm2) ? cm2 : 0.5;

    [LocalDataTheory(LocalData.GameData, Neolithe, Bibo, Rue, LavaBod)]
    [MemberData(nameof(GameTops))]
    public void A_game_top_auto_refit_with_the_bra_followed_is_no_worse(string garmentName, string modNeed, string? rel)
    {
        // [AC-bra-hole-transfer-4] The automatic refit of game tops: no more skin through or under the cloth, no more
        // skin left undrawn and uncovered, nothing more turned over.
        var data = Game();
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = VanillaGearRefitLocalTests.TopOf(data, garmentName);
        var garment = ModelPartReader.Read(bytes)!;
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_top", "0201")[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(modNeed));
        var options = mod.For("_top", "0201");
        string target = mod.PathOf(rel == null ? options[0] : options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel)));
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", false, null, null, uv, out var pair));

        (ModelParts Model, BodyRetarget.Report Report, int Folded) Run(BodyRetarget.Tuning tuning)
        {
            var plan = BodyRetarget.WithTuning(tuning, () => RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true,
                                                                                gameGear: true, out _));
            var drawn = ModelPartReader.Read(RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log))!;
            return (drawn, plan.Report, Folded(garment, bytes, plan));
        }
        var now = Run(new BodyRetarget.Tuning());
        var old = Run(Old);
        var body = ModelPartReader.Read(File.ReadAllBytes(target))!;

        double through = VanillaGearRefitLocalTests.SkinThrough(now.Model), throughOld = VanillaGearRefitLocalTests.SkinThrough(old.Model);
        double under = VanillaGearRefitLocalTests.SkinThrough(now.Model, VanillaGearRefitLocalTests.Under);
        double underOld = VanillaGearRefitLocalTests.SkinThrough(old.Model, VanillaGearRefitLocalTests.Under);
        double holes = VanillaGearRefitLocalTests.UncoveredUndrawn(now.Model, body, new SortedDictionary<(float, string), double>());
        double holesOld = VanillaGearRefitLocalTests.UncoveredUndrawn(old.Model, body, new SortedDictionary<(float, string), double>());
        output.WriteLine($"RAN {garmentName} on {Path.GetFileName(LocalData.Path(modNeed))} / {rel}: "
                         + $"{now.Report.SourceSmallclothes} underwear points landed");
        output.WriteLine($"  through {through:0.0} cm² (old {throughOld:0.0}); under {under:0.0} (old {underOld:0.0}); "
                         + $"undrawn {holes:0.0} (old {holesOld:0.0}); folded {now.Folded} (old {old.Folded}); "
                         + $"standoff pulled {now.Report.StandoffPulled} (old {old.Report.StandoffPulled}); deep cleared "
                         + $"{now.Report.DeepCleared} (old {old.Report.DeepCleared})");
        Assert.True(through <= throughOld + Allowed(garmentName, modNeed, "through"), $"through {through:0.0} cm² against {throughOld:0.0}");
        Assert.True(under <= underOld + Allowed(garmentName, modNeed, "under"), $"under {under:0.0} cm² against {underOld:0.0}");
        Assert.True(holes <= holesOld + Allowed(garmentName, modNeed, "undrawn"), $"undrawn {holes:0.0} cm² against {holesOld:0.0}");
        Assert.True(now.Folded <= old.Folded, $"folded {now.Folded} against {old.Folded}");
    }

    [LocalDataFact(LocalData.GameData, MuseRoot)]
    public void The_game_legs_panties_follow_the_rule()
    {
        // [EDGE-bra-hole-transfer-3] The game's legs stand their panties over a hole too (see the survey), but only its
        // chest is read with its underwear: Pioneer's Bottoms onto Muse+'s legs refit byte for byte as before.
        var data = Game();
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_dwn", "0201")[0]);
        var muse = BodySizeCatalog.Read(LocalData.Path(MuseRoot));
        string target = muse.PathOf(muse.For("_dwn", "0201")[0]);
        const string bottoms = "chara/equipment/e0689/model/c0201e0689_dwn.mdl";
        byte[] bytes = data.GetFile(bottoms)!.Data;
        var garment = ModelPartReader.Read(bytes)!;
        Assert.Null(BodyRetarget.BuildPair("_dwn", source, target, "_dwn", false, null, null, uv, out var pair));
        BodyRetarget.Planned Plan() => BodyRetarget.Plan(garment, bytes, [pair], "_dwn", replaceSkin: true,
                                                         acrossBodies: true, clearBody: true, cutHidden: true);
        var now = Plan();
        Assert.Equal(0, now.Report.SourceSmallclothes);
        Assert.Equal(BodyRetarget.WithTuning(Old, Plan).Model, now.Model);
    }
}
