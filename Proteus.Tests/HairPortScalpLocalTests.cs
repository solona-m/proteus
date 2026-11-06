using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The scalp push (<see cref="HairScalpPush"/>) measured on the game's own shared hairstyles: what the bend sinks into
/// the target's skull before and after it, how far it moves a port from the game's own copy for that race, and what it
/// does to the triangles. Prints the calibration table its constants were set from.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class HairPortScalpLocalTests(ITestOutputHelper o)
{
    private static readonly ushort[] Sources = [201, 101];

    private static IEnumerable<ushort> Playable() => Enumerable.Range(1, 18).Select(n => (ushort)(n * 100 + 1));

    private static bool Hrothgar(ushort race) => race / 100 is 15 or 16;

    /// <summary>One port of a game hairstyle: the source as drawn, the bend alone, and the bend pushed.</summary>
    internal sealed record Case(ushort From, ushort To, int Id, ModelParts Shown, byte[] Bent, HairScalpPush.Result Push,
                                HairScalpPush.Frame Source, HairScalpPush.Frame Target, byte[]? Own);

    internal static IEnumerable<Case> Cases(HairPortLocalTests.Game game, bool crossSex)
    {
        foreach (var from in Sources)
        {
            var sourceFrame = HairScalpPush.FrameOf(game.Head(from)!)!;
            foreach (var to in Playable().Where(t => t != from && CrossSexRefit.CrossesSex(from, t) == crossSex))
            {
                var targetFrame = HairScalpPush.FrameOf(game.Head(to)!)!;
                foreach (int id in HairPortLocalTests.Ids)
                {
                    var src = game.File(HairPortLocalTests.HairPath(from, id));
                    if (src == null || game.File(HairPortLocalTests.HairPath(to, id)) is not { } own) continue;
                    var bent = RacialModelBake.Port(src, from, to, game.Pbd, HairPortBaker.ParentOf, out _);
                    if (bent == null) continue;
                    var shown = ModelPartReader.Read(src)!;
                    var push = HairScalpPush.Apply(bent, shown, sourceFrame, targetFrame);
                    yield return new Case(from, to, id, shown, bent, push, sourceFrame, targetFrame, crossSex ? null : own);
                }
            }
        }
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] What the bend sank into the target's skull where the source sat on its scalp is
    /// pushed back out on every pair: after ≤ before for each, and over all but Hrothgar after ≤ 5% of before (the
    /// game's-own-copy gate below wins over this one). Ports still over <see cref="HairScalpPush.ClipLimit"/> are listed:
    /// they are warned, and the limit is never raised for them.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-15")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-20")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-22")]
    public void Created_clips_on_game_hairstyles_before_and_after_the_push()
    {
        var game = HairPortLocalTests.Load();
        long before = 0, after = 0;
        var warned = new List<string>();
        foreach (bool cross in new[] { false, true })
            foreach (var group in Cases(game, cross).GroupBy(c => (c.From, c.To)))
            {
                int b = 0, a = 0, n = 0;
                float worst = 0;
                foreach (var c in group)
                {
                    Assert.True(c.Push.After <= c.Push.Before, $"c{c.From:D4}->c{c.To:D4} h{c.Id:D4}: {c.Push.Before} -> {c.Push.After}");
                    Assert.True(c.Push.Capped == 0, $"c{c.From:D4}->c{c.To:D4} h{c.Id:D4}: {c.Push.Capped} points held at MaxPush");
                    b += c.Push.Before; a += c.Push.After; n++;
                    worst = MathF.Max(worst, c.Push.Worst);
                    if (c.Push.After > HairScalpPush.ClipLimit(c.Push.Cranium))
                        warned.Add($"c{c.From:D4}->c{c.To:D4} h{c.Id:D4} {c.Push.After}/{c.Push.Cranium}");
                }
                o.WriteLine($"c{group.Key.From:D4}->c{group.Key.To:D4}{(cross ? " (other sex)" : "")}: {n} hairstyles, " +
                            $"created clips {b} -> {a}, worst push {worst * 1000:F1} mm");
                if (!Hrothgar(group.Key.From) && !Hrothgar(group.Key.To)) { before += b; after += a; }
            }
        o.WriteLine($"all but Hrothgar: {before} -> {after}; warned: {warned.Count}");
        foreach (var w in warned) o.WriteLine($"  warned {w}");
        Assert.True(after <= before * 0.05, $"created clips {before} -> {after}");
        Assert.Equal(50, HairScalpPush.ClipLimit(1_000_000));
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-7] The push never takes a port away from the game's own copy of that hairstyle for the
    /// target race, where that copy is a bend of the same style. Over all points, the median distance to the nearest of
    /// the copy grows by at most 0.5 mm (1.0 mm for a Hrothgar, whose hairstyles the game re-authors). Over the points it
    /// moved, their mean distance grows by at most 1.0 mm (Hrothgar 2.0 mm) — where the bend alone lands within 8 mm of the
    /// copy on them; beyond that the game reshaped the style there, the bound is 3 mm, and those cases are listed and
    /// counted (at most <see cref="MaxBeyondReference"/>).
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-7")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-15")]
    public void Push_never_moves_a_port_away_from_the_games_own_copy()
    {
        var game = HairPortLocalTests.Load();
        var beyond = new List<string>();
        foreach (var group in Cases(game, crossSex: false).GroupBy(c => (c.From, c.To)))
        {
            bool hroth = Hrothgar(group.Key.From) || Hrothgar(group.Key.To);
            double worstMoved = 0, worstMedian = 0;
            foreach (var c in group.Where(c => c.Push.Pushed > 0))
            {
                var own = ModelPartReader.Read(c.Own!)!.Positions;
                var bent = ModelPartReader.Read(c.Bent)!.Positions;
                var pushed = ModelPartReader.Read(c.Push.Model)!.Positions;
                var moved = Moved(bent, pushed);
                double mb = MeanNearest(bent, own, moved), ma = MeanNearest(pushed, own, moved);
                double db = MedianNearest(bent, own), da = MedianNearest(pushed, own);
                worstMoved = Math.Max(worstMoved, ma - mb);
                worstMedian = Math.Max(worstMedian, da - db);
                string at = $"c{c.From:D4}->c{c.To:D4} h{c.Id:D4}";
                Check(da <= db + (hroth ? 0.001 : 0.0005), $"{at}: median {db * 1000:F1} -> {da * 1000:F1} mm from the game's own");
                if (mb <= 0.008)
                    Check(ma <= mb + (hroth ? 0.002 : 0.001), $"{at}: moved points {mb * 1000:F1} -> {ma * 1000:F1} mm from the game's own");
                else
                {
                    beyond.Add($"{at}: moved points {mb * 1000:F1} -> {ma * 1000:F1} mm");
                    Check(ma <= mb + 0.003, $"{at}: moved points {mb * 1000:F1} -> {ma * 1000:F1} mm (bend already off the copy)");
                }
            }
            o.WriteLine($"c{group.Key.From:D4}->c{group.Key.To:D4}: moved points at most {worstMoved * 1000:F2} mm further, " +
                        $"median at most {worstMedian * 1000:F2} mm");
        }
        o.WriteLine($"{beyond.Count} case(s) where the bend alone is over 8 mm from the game's copy on the moved points:");
        foreach (var b in beyond) o.WriteLine("  " + b);
        Check(beyond.Count <= ExcludedPairsMax, $"{beyond.Count} cases beyond the 8 mm reference, at most {ExcludedPairsMax}");
        var names = beyond.Select(b => b[..b.IndexOf(':')]).Order(StringComparer.Ordinal).ToList();
        Check(names.SequenceEqual(Excluded.Order(StringComparer.Ordinal)),
              $"the cases beyond the 8 mm reference changed: {string.Join(", ", names)}");
        Done();
    }

    /// <summary>
    /// The game ports whose moved points lie over 8 mm from the game's own copy on the bend alone — the game reshaped the
    /// style there — measured 2026-11-05 (bend alone, then pushed, mean distance on the moved points): c0201→c1201 h0110
    /// 11.0→6.7, h0120 11.0→9.4, h0140 8.5→5.1; c0101→c0901 h0101 14.2→14.8, h0105 12.3→12.9, h0170 11.7→13.7;
    /// c0101→c1101 h0120 13.0→10.7; c0101→c1501 h0101 34.4→16.2, h0116 29.2→16.6, h0120 25.9→14.5, h0140 30.1→19.0, h0170
    /// 33.1→23.2, h0180 22.0→10.7, h0190 16.7→11.4. A new or different one fails until it is looked at.
    /// </summary>
    private const int ExcludedPairsMax = 14;

    private static readonly string[] Excluded =
    [
        "c0201->c1201 h0110", "c0201->c1201 h0120", "c0201->c1201 h0140",
        "c0101->c0901 h0101", "c0101->c0901 h0105", "c0101->c0901 h0170", "c0101->c1101 h0120",
        "c0101->c1501 h0101", "c0101->c1501 h0116", "c0101->c1501 h0120", "c0101->c1501 h0140",
        "c0101->c1501 h0170", "c0101->c1501 h0180", "c0101->c1501 h0190",
    ];

    /// <summary>
    /// [AC-hair-material-scalp-fit-7] The push keeps the hairstyle intact. It adds at most max(2, 0.1%) folded triangles
    /// and never tips a port into the fold warning; it makes no triangle degenerate; and few edges — at most
    /// <see cref="PlainStrain"/> (Hrothgar <see cref="HrothgarStrain"/>), regression gates — are distorted by more than 25%
    /// beyond what wrapping them onto the target's skull asks for
    /// (<see cref="HairScalpPush.ExcessStrain"/>).
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-7")]
    public void Push_adds_no_folds_or_excess_strain_on_game_hairstyles()
    {
        var game = HairPortLocalTests.Load();
        double worstPlain = 0, worstHroth = 0;
        int worstFolds = 0;
        foreach (bool cross in new[] { false, true })
            foreach (var c in Cases(game, cross).Where(c => c.Push.Pushed > 0))
            {
                bool hroth = Hrothgar(c.From) || Hrothgar(c.To);
                string at = $"c{c.From:D4}->c{c.To:D4} h{c.Id:D4}";
                var bent = ModelPartReader.Read(c.Bent)!;
                var pushed = ModelPartReader.Read(c.Push.Model)!;
                var (added, tipped) = Folds(c.Shown, bent, pushed);
                worstFolds = Math.Max(worstFolds, added);
                int tris = c.Shown.Parts.Where(p => p.Island == -1).Sum(p => p.TriangleCount);
                Check(added <= Math.Max(2, tris / 1000), $"{at}: {added} folded triangles added of {tris}");
                Check(!tipped, $"{at}: the push tipped it into the fold warning");
                Check(Degenerate(bent, pushed) == 0, $"{at}: degenerate triangles");
                double share = OverStrained(c.Shown, bent, pushed, c.Source, c.Target, c.Bent);
                if (hroth) worstHroth = Math.Max(worstHroth, share); else worstPlain = Math.Max(worstPlain, share);
                Check(share <= (hroth ? HrothgarStrain : PlainStrain), $"{at}: {share:P2} of pushed edges strained over 25% beyond the skull's wrap");
            }
        o.WriteLine($"worst excess-strain share: non-Hrothgar {worstPlain:P3}, Hrothgar {worstHroth:P3}; worst folds added {worstFolds}");
        Done();
    }

    /// <summary>
    /// The most of a game port's pushed edges that may be strained over 25% beyond the skull's wrap: regression gates, the
    /// measured worst plus a quarter (2026-11-05) — non-Hrothgar 0.46% (c0101→c0701 h0140; then 0.30% c0201→c0701 h0140,
    /// 0.21% c0201→c0701 h0170, 0.15% c0201→c1701 h0140, every other port at most 0.1%); Hrothgar 10.0% (c0101→c1501
    /// h0101; then 5.2% h0180, 4.8% h0116; c0201→c1501 1.2–3.6%; c0101→c1601 h0180 1.24%). The strain is what wrapping
    /// coarse cards onto a much bigger skull costs; how it looks is checked in game.
    /// </summary>
    private const double PlainStrain = 0.006, HrothgarStrain = 0.125;

    /// <summary>Folded triangles the push adds over the bend, and whether it takes the port over the fold warning's share
    /// when the bend alone was under it.</summary>
    internal static (int Added, bool Tipped) Folds(ModelParts shown, ModelParts bent, ModelParts pushed)
    {
        int tris = shown.Parts.Where(p => p.Island == -1).Sum(p => p.TriangleCount);
        int b = HairPortBaker.Folded(shown, bent), f = HairPortBaker.Folded(shown, pushed);
        float limit = HairPortBaker.FoldedShare * tris;
        return (f - b, b <= limit && f > limit);
    }

    /// <summary>The share of eligible edges whose excess strain is over 25%.</summary>
    internal static double OverStrained(ModelParts shown, ModelParts bent, ModelParts pushed, HairScalpPush.Frame s,
                                        HairScalpPush.Frame t, byte[] bentModel)
    {
        var moved = Moved(bent.Positions, pushed.Positions).ToHashSet();
        var excess = HairScalpPush.ExcessStrain(shown, bent, pushed, s, t, HairScalpPush.Spares(bentModel, bent), moved.Contains);
        return excess.Count == 0 ? 0 : excess.Count(e => e > 0.25f) / (double)excess.Count;
    }

    private static int Degenerate(ModelParts bent, ModelParts pushed)
    {
        int n = 0;
        foreach (var part in bent.Parts.Where(p => p.Island == -1))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int i = part.Triangles[t], j = part.Triangles[t + 1], k = part.Triangles[t + 2];
                if (Area(bent.Positions, i, j, k) > 1e-10f && Area(pushed.Positions, i, j, k) <= 1e-12f) n++;
            }
        return n;
    }

    internal static List<int> Moved(float[] bent, float[] pushed)
        => Enumerable.Range(0, bent.Length / 3)
                     .Where(v => Math.Abs(bent[v * 3] - pushed[v * 3]) + Math.Abs(bent[v * 3 + 1] - pushed[v * 3 + 1])
                                 + Math.Abs(bent[v * 3 + 2] - pushed[v * 3 + 2]) > 1e-6f).ToList();

    /// <summary>The plan's fixed sample for the independent skin check (revision 4.1).</summary>
    private static readonly (ushort From, ushort To)[] SkinPairs =
    [
        (201, 101), (201, 301), (201, 501), (201, 701), (201, 1101), (201, 1401), (201, 1501), (201, 1601), (201, 1701),
        (101, 201), (101, 801), (101, 1201), (101, 1801),
    ];

    private static readonly int[] SkinIds = [120, 140, 168, 180];

    /// <summary>The sample's ports: each pair's game hairstyles of <see cref="SkinIds"/>, bent and pushed.</summary>
    private static IEnumerable<Case> SkinCases(HairPortLocalTests.Game game)
    {
        foreach (var (from, to) in SkinPairs)
        {
            var sf = HairScalpPush.FrameOf(game.Head(from)!)!;
            var tf = HairScalpPush.FrameOf(game.Head(to)!)!;
            foreach (int id in SkinIds)
            {
                if (game.File(HairPortLocalTests.HairPath(from, id)) is not { } src) continue;
                var bent = RacialModelBake.Port(src, from, to, game.Pbd, HairPortBaker.ParentOf, out _);
                if (bent == null) continue;
                var shown = ModelPartReader.Read(src)!;
                yield return new Case(from, to, id, shown, bent, HairScalpPush.Apply(bent, shown, sf, tf), sf, tf, null);
            }
        }
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] A check that does not use the push's own scalp: each pushed upper-back point that sat
    /// on the source's skin tested against the target face's skin triangles directly — inside when it is more than the
    /// clip depth behind the nearest one, by its outward normal — every such point, not only those the push moved. Port by
    /// port over the plan's fixed sample (<see cref="CheckPort"/>).
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    public void Pushed_hair_is_outside_the_skin_by_an_independent_test()
    {
        var game = HairPortLocalTests.Load();
        int skipped = 0;
        foreach (var pair in SkinCases(game).GroupBy(c => (c.From, c.To)))
        {
            long before = 0, after = 0;
            foreach (var c in pair)
            {
                var bentParts = ModelPartReader.Read(c.Bent)!;
                var r = InsideSkin(SkinOf(game.Head(c.To)!, c.Target), SkinOf(game.Head(c.From)!, c.Source), c.Shown,
                                   bentParts, ModelPartReader.Read(c.Push.Model)!, HairScalpPush.Spares(c.Bent, bentParts));
                string at = $"c{c.From:D4}->c{c.To:D4} h{c.Id:D4}";
                o.WriteLine($"{at}: upper-back points inside the skin {r.Before} -> {r.After} ({r.Deep} deep, {r.MissedBoth} by both), deepest unmoved " +
                            $"{r.UnmovedDepth * 1000:F1} mm ({r.Protrusion} under a protrusion, {r.ProtrusionBack} at the upper back, not counted)");
                CheckPort(at, c.To, r, Check);
                before += r.Before; after += r.After;
            }
            o.WriteLine($"c{pair.Key.From:D4}->c{pair.Key.To:D4}: {before} -> {after}");
            if (before == 0) skipped++;
        }
        o.WriteLine($"{skipped} pair(s) with nothing inside before");
        Done();
    }

    /// <summary>
    /// The independent skin check's rule for one hairstyle port: never more points inside after the push than before, and
    /// after at most max(2% of before, 16) of points no deeper than <see cref="DisagreementDepth"/>, and at most 2% of before
    /// of points deeper (5% on a Hrothgar, whose curved skull and nape part the two measures further) — no floor, so a miss
    /// fails on a port of any size; and at most 2% of before inside by both measures, on every port. The floor is twice the worst disagreement measured
    /// (8 points a port, 2026-11-05) between the push's radial gap and this check's distance to the nearest skin triangle,
    /// which part by a few millimetres on steep skin — mostly points the push never moved. Points under a protrusion are
    /// left out; at least half of before must be
    /// otherwise where before totals 100 or more, and at the upper back (where the bald patch was) at most
    /// <see cref="UpperBackProtrusionMax"/> per race family.
    /// </summary>
    /// <summary>
    /// The one port allowed more points inside by both measures than 2% of before, pinned by name and count: c0201→c1501
    /// h0180, 8 — two positions and their duplicates at the head's centre line, which the bend sinks 53–55 mm and only
    /// neighbouring seeds lift (47 of 55 mm), since a point seeds only above the centre on both heads (the calibrated rule;
    /// seeding them broke three gates). A deferred known issue (2026-11-05). Any other port over 2%, or this one over 8,
    /// fails.
    /// </summary>
    internal static readonly Dictionary<string, int> MissedByBothExceptions = new() { ["c0201->c1501 h0180"] = 8 };

    internal static void CheckPort(string at, ushort to, Inside r, Action<bool, string> check)
    {
        check(r.After <= r.Before, $"{at}: {r.Before} -> {r.After}, more inside after the push");
        check(r.After - r.Deep <= Math.Max(r.Before * 0.02, 16), $"{at}: {r.Before} -> {r.After - r.Deep} within {DisagreementDepth * 1000:F1} mm");
        bool hrothgar = to / 100 is 15 or 16;
        check(r.Deep <= (int)(r.Before * (hrothgar ? 0.05 : 0.02)), $"{at}: {r.Deep} points left deeper than {DisagreementDepth * 1000:F1} mm, of {r.Before}");
        int missedMax = MissedByBothExceptions.TryGetValue(at, out var pinned) ? pinned : (int)(r.Before * 0.02);
        check(r.MissedBoth <= missedMax, $"{at}: {r.MissedBoth} points inside by both measures, of {r.Before}");
        if (r.Before + r.Protrusion >= 100) check(r.Before >= 0.5 * (r.Before + r.Protrusion), $"{at}: protrusions ({r.Protrusion}) are most of before");
        int family = to / 100 - (to / 100 + 1) % 2;
        check(r.ProtrusionBack <= UpperBackProtrusionMax.GetValueOrDefault(family), $"{at}: {r.ProtrusionBack} protrusion points at the upper back");
    }

    /// <summary>The most protrusion points at the upper back one port may have, by target race family (the odd race number:
    /// 1 Midlander, 3 Highlander, 5 Elezen, 7 Miqo'te, 9 Roegadyn, 11 Lalafell, 13 Au Ra, 15 Hrothgar, 17 Viera) — pinned to
    /// what was measured 2026-11-05 over the game sample and Stryx; any family not listed, 0.</summary>
    internal static readonly Dictionary<int, int> UpperBackProtrusionMax = new() { [7] = 3 };

    /// <summary>The face's skin triangles (<c>_fac_a</c>), each with its outward normal (away from the head's centre).</summary>
    internal static List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)> SkinTriangles(byte[] head, Vector3 centre)
    {
        var face = ModelPartReader.Read(head)!;
        var tris = new List<(Vector3, Vector3, Vector3, Vector3)>();
        foreach (var part in face.Parts.Where(p => p.Island == -1 && HairScalpPush.IsSkin(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var a = V(face.Positions, part.Triangles[t]);
                var b = V(face.Positions, part.Triangles[t + 1]);
                var cc = V(face.Positions, part.Triangles[t + 2]);
                var n = Vector3.Cross(b - a, cc - a);
                if (n.LengthSquared() < 1e-14f) continue;
                n = Vector3.Normalize(n);
                if (Vector3.Dot(n, (a + b + cc) / 3f - centre) < 0) n = -n;
                tris.Add((a, b, cc, n));
            }
        return tris;
    }

    /// <summary>
    /// Of the points above the target head's centre and behind it that sat on or outside the SOURCE's
    /// skin on its own head (by its own triangles: hair the author tucked inside stays tucked, and is not counted), how
    /// many sit inside the target's skin before the push and after it.
    /// </summary>
    /// <remarks>Points under a protrusion — their nearest target skin triangle standing more than
    /// <see cref="HairScalpPush.ProtrusionMargin"/> beyond the clipped skull in that direction: a Miqo'te's ear base, an
    /// Elezen's ear — are left out of both counts and counted on their own: the push does not push hair out of ears
    /// (EDGE-hair-material-scalp-fit-15).</remarks>
    /// <returns>Non-protrusion points inside before and after; protrusion points inside before or after, and of those, the ones
    /// at the upper back (behind the ear line: |x| under half the distance from the centre) — the bald patch's region,
    /// where the exclusion must not reach.</returns>
    /// <remarks>Also: of the points inside after, how many lie deeper than <see cref="DisagreementDepth"/>, and the deepest
    /// of those the push never moved (metres).</remarks>
    internal static Inside InsideSkin(Skin target, Skin source, ModelParts shown, ModelParts bent, ModelParts pushed,
                                      IReadOnlySet<int>? spares = null)
    {
        int b = 0, a = 0, protrusion = 0, back = 0, deep = 0, both = 0;
        float unmovedDepth = 0f;
        var moved = Moved(bent.Positions, pushed.Positions).ToHashSet();
        // Every point, not only those the push moved: one its own scalp wrongly called "not sunk" is what this catches.
        for (int v = 0; v < bent.Positions.Length / 3; v++)
        {
            if (spares?.Contains(v) == true) continue;
            var q = V(bent.Positions, v);
            if (q.Y < target.Centre.Y || q.Z > target.Centre.Z) continue;
            if (Side(source.Triangles, V(shown.Positions, v)).Side < -HairScalpPush.SourceSlack) continue;
            var (before, at) = Side(target.Triangles, q);
            var (after, atAfter) = Side(target.Triangles, V(pushed.Positions, v));
            // Under a protrusion before the push or after it: pushed out, a point beside an ear base can end up under it.
            if (IsProtrusion(at) || IsProtrusion(atAfter))
            {
                if (before < -HairScalpPush.ClipDepth || after < -HairScalpPush.ClipDepth)
                {
                    protrusion++;
                    var r = q - target.Centre;
                    if (MathF.Abs(r.X) < 0.5f * r.Length()) back++;
                }
                continue;
            }
            if (before < -HairScalpPush.ClipDepth) b++;
            if (after < -HairScalpPush.ClipDepth)
            {
                a++;
                if (-after > DisagreementDepth)
                {
                    deep++;
                    // Inside by the push's own radial gap too: missed by both measures, never their disagreement.
                    if (Gap(V(pushed.Positions, v), target.Frame) < -HairScalpPush.ClipDepth) both++;
                }
                if (!moved.Contains(v)) unmovedDepth = MathF.Max(unmovedDepth, -after);
            }
        }
        return new Inside(b, a, protrusion, back, deep, unmovedDepth, both);

        bool IsProtrusion(Vector3 skinPoint)
        {
            var d = skinPoint - target.Centre;
            return d.Length() > HatCompatSolve.FloorAt(target.Frame.Skull, Vector3.Normalize(d)) + HairScalpPush.ProtrusionMargin;
        }
    }

    /// <param name="Before">Counted points inside the target's skin on the bend alone.</param>
    /// <param name="After">The same after the push.</param>
    /// <param name="Protrusion">Points under a protrusion, inside before or after, left out.</param>
    /// <param name="ProtrusionBack">Of those, the ones at the upper back.</param>
    /// <param name="Deep">Of <paramref name="After"/>, those deeper than <see cref="DisagreementDepth"/>.</param>
    /// <param name="UnmovedDepth">The deepest point inside after that the push never moved, in metres.</param>
    /// <param name="MissedBoth">Of <paramref name="Deep"/>, those the push's own radial gap reads as inside too.</param>
    internal readonly record struct Inside(int Before, int After, int Protrusion, int ProtrusionBack, int Deep, float UnmovedDepth,
                                           int MissedBoth = 0);

    /// <summary>
    /// How deep the push's radial gap and this check's distance to the nearest skin triangle can part on steep skin, in
    /// metres: the deepest point left inside that the push never moved, over the game sample and Stryx (measured
    /// 2026-11-05: 8.8 mm), plus a margin. Points left inside no deeper than this get the per-port floor;
    /// deeper ones are a miss, and get none.
    /// </summary>
    internal const float DisagreementDepth = 0.010f;

    /// <summary>A face's skin triangles with outward normals, and its head's frame.</summary>
    internal sealed record Skin(List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)> Triangles, HairScalpPush.Frame Frame)
    {
        public Vector3 Centre => Frame.Centre;
    }

    internal static Skin SkinOf(byte[] head, HairScalpPush.Frame frame) => new(SkinTriangles(head, frame.Centre), frame);

    /// <summary>How far outside the nearest skin triangle a point is, by its outward normal (negative inside), and where
    /// on the skin that nearest point is.</summary>
    internal static (float Side, Vector3 At) Side(List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)> skin, Vector3 q)
    {
        float best = float.MaxValue;
        float side = 0;
        Vector3 at = default;
        foreach (var (a, b, c, n) in skin)
        {
            var p = Closest(q, a, b, c);
            float d = (q - p).LengthSquared();
            if (d < best) { best = d; side = Vector3.Dot(q - p, n); at = p; }
        }
        return (side, at);
    }

    /// <summary>The closest point of triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] [EDGE-hair-material-scalp-fit-14] The push never overshoots: no point it moved ends
    /// more than 0.5 mm further from the scalp than it sat on the source, over every game port.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-14")]
    public void No_pushed_vertex_ends_further_from_the_scalp_than_on_the_source()
    {
        var game = HairPortLocalTests.Load();
        float worst = float.MinValue;
        foreach (bool cross in new[] { false, true })
            foreach (var c in Cases(game, cross).Where(c => c.Push.Pushed > 0))
            {
                var bentParts = ModelPartReader.Read(c.Bent)!;
                float over = Overshoot(c.Shown, bentParts, ModelPartReader.Read(c.Push.Model)!, c.Source, c.Target,
                                       HairScalpPush.Spares(c.Bent, bentParts));
                worst = MathF.Max(worst, over);
                Check(over <= 0.0005f, $"c{c.From:D4}->c{c.To:D4} h{c.Id:D4}: a point {over * 1000:F2} mm further out than on the source");
            }
        o.WriteLine($"worst overshoot {worst * 1000:F2} mm");
        Done();
    }

    /// <summary>The most any moved point stands further from the target's scalp than it stood from the source's, in metres.</summary>
    internal static float Overshoot(ModelParts shown, ModelParts bent, ModelParts pushed, HairScalpPush.Frame s, HairScalpPush.Frame t,
                                    IReadOnlySet<int>? spares = null)
    {
        float worst = float.MinValue;
        foreach (var v in Moved(bent.Positions, pushed.Positions))
        {
            if (spares?.Contains(v) == true) continue;
            float gs = Gap(V(shown.Positions, v), s), gt = Gap(V(pushed.Positions, v), t);
            worst = MathF.Max(worst, gt - gs);
        }
        return worst;
    }

    /// <summary>A point's distance outside a frame's scalp, read in the upper half's directions as the push reads it.</summary>
    internal static float Gap(Vector3 p, HairScalpPush.Frame f)
    {
        var d = p - f.Centre;
        float len = d.Length();
        var dir = d / len;
        if (dir.Y < 0) { var flat = new Vector3(dir.X, 0, dir.Z); dir = flat.LengthSquared() > 1e-12f ? Vector3.Normalize(flat) : Vector3.UnitX; }
        return len - HatCompatSolve.FloorAt(f.Skull, dir);
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] The scalp is read from the face's skin meshes: on an Au Ra (horns and scales are
    /// meshes of their own) and a Miqo'te (whose cranium is a few large triangles), the game's own copies of the shared
    /// hairstyles sit no deeper inside it than inside the hat press's furthest-vertex floor.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    public void Skull_surface_reads_the_skin_not_the_horns()
    {
        var game = HairPortLocalTests.Load();
        foreach (ushort race in new ushort[] { 1401, 801 })
        {
            var head = game.Head(race)!;
            var frame = HairScalpPush.FrameOf(head)!;
            var f = HatCompatSolve.HeadFrameFrom(head)!.Value;
            var floor = HatCompatSolve.HeadFloor(HatCompatSolve.ReadLod0Meshes(head), f.Centre, f.Radius, f.Centre.Y);
            int cranium = 0, sunkNew = 0, sunkOld = 0;
            foreach (int id in HairPortLocalTests.Ids)
            {
                if (game.File(HairPortLocalTests.HairPath(race, id)) is not { } own) continue;
                var p = ModelPartReader.Read(own)!.Positions;
                for (int v = 0; v < p.Length / 3; v++)
                {
                    var q = V(p, v);
                    var d = q - f.Centre;
                    if (d.Y < 0 || d.Z > 0 || d.Length() < 1e-5f) continue;
                    cranium++;
                    if (Gap(q, frame) < -HairScalpPush.ClipDepth) sunkNew++;
                    if (d.Length() - HatCompatSolve.FloorAt(floor, d / d.Length()) < -HairScalpPush.ClipDepth) sunkOld++;
                }
            }
            o.WriteLine($"c{race:D4}: of {cranium} upper-back points of the game's own copies, sunk {sunkNew} on the skin surface, {sunkOld} on the furthest-vertex floor");
            Assert.True(sunkNew <= sunkOld, $"c{race:D4}: {sunkNew} > {sunkOld}");
        }
    }

    private static Vector3 V(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    /// <summary>[EDGE-hair-material-scalp-fit-17] A game hairstyle with shape keys keeps each spare's offset from its base.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-17")]
    public void Push_keeps_game_hair_shapes()
    {
        var game = HairPortLocalTests.Load();
        int checkedShapes = 0;
        foreach (var c in Cases(game, crossSex: false).Where(c => c.Push.Pushed > 0))
        {
            var src = SecondSkinWriter.Parse(c.Bent);
            if (src.Shapes.Count == 0) continue;
            var bent = ModelPartReader.Read(c.Bent)!;
            var pushed = ModelPartReader.Read(c.Push.Model)!;
            foreach (var (baseVertex, spare) in ShapePairs(c.Bent, bent))
            {
                var ob = Sub(bent.Positions, spare, baseVertex);
                var op = Sub(pushed.Positions, spare, baseVertex);
                Assert.True((ob - op).Length() < 1e-4f, $"c{c.From:D4}->c{c.To:D4} h{c.Id:D4}: spare {spare} offset moved");
                checkedShapes++;
            }
            if (checkedShapes > 2000) break;
        }
        o.WriteLine($"{checkedShapes} spare offsets checked");
    }

    /// <summary>(base, spare) pairs in <paramref name="parts"/>' numbering, read from the model's shape values.</summary>
    internal static IEnumerable<(int Base, int Spare)> ShapePairs(byte[] mdl, ModelParts parts)
    {
        var src = SecondSkinWriter.Parse(mdl);
        foreach (var (_, entries) in src.Shapes)
            foreach (var entry in entries)
                foreach (var span in parts.MeshSpans)
                {
                    int mo = src.MeshStart + span.Mesh * 36;
                    uint start = BitConverter.ToUInt32(mdl, mo + 16), count = BitConverter.ToUInt32(mdl, mo + 4);
                    if (entry.MeshIndexOffset < start || entry.MeshIndexOffset >= start + count) continue;
                    foreach (var (b, replace) in entry.Values)
                    {
                        int baseVertex = BitConverter.ToUInt16(mdl, src.Ib + (int)(entry.MeshIndexOffset + b) * 2);
                        if (baseVertex >= span.Count || replace >= span.Count) continue;
                        yield return (span.BaseVertex + baseVertex, span.BaseVertex + (int)replace);
                    }
                    break;
                }
    }

    private readonly List<string> fails = [];

    /// <summary>Note a failed case and go on, so a run prints every one; <see cref="Done"/> fails the test on any.</summary>
    private void Check(bool ok, string what)
    {
        if (ok) return;
        fails.Add(what);
        o.WriteLine("FAIL " + what);
    }

    private void Done() => Assert.True(fails.Count == 0, $"{fails.Count} failed: {string.Join("; ", fails.Take(10))}");

    private static Vector3 Sub(float[] p, int a, int b)
        => new(p[a * 3] - p[b * 3], p[a * 3 + 1] - p[b * 3 + 1], p[a * 3 + 2] - p[b * 3 + 2]);

    private static float Length(float[] p, int a, int b) => Sub(p, a, b).Length();

    private static float Area(float[] p, int i, int j, int k) => Vector3.Cross(Sub(p, j, i), Sub(p, k, i)).Length() * 0.5f;

    private static double MeanNearest(float[] a, float[] b, IReadOnlyList<int> which)
    {
        if (which.Count == 0) return 0;
        int step = Math.Max(1, which.Count / 500);
        double sum = 0;
        int n = 0;
        for (int w = 0; w < which.Count; w += step) { sum += Nearest(a, which[w], b); n++; }
        return sum / n;
    }

    private static double MedianNearest(float[] a, float[] b)
    {
        int na = a.Length / 3;
        int step = Math.Max(1, na / 800);
        var d = new List<float>();
        for (int i = 0; i < na; i += step) d.Add(Nearest(a, i, b));
        d.Sort();
        return d[d.Count / 2];
    }

    private static float Nearest(float[] a, int i, float[] b)
    {
        float best = float.MaxValue;
        for (int j = 0; j < b.Length / 3; j++)
        {
            float dx = b[j * 3] - a[i * 3], dy = b[j * 3 + 1] - a[i * 3 + 1], dz = b[j * 3 + 2] - a[i * 3 + 2];
            best = MathF.Min(best, dx * dx + dy * dy + dz * dz);
        }
        return MathF.Sqrt(best);
    }
}
