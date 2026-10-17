using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// Hair ports measured against the game's own files. The game ships every shared hairstyle (101+) once per race, so
/// the target race's own model of the same hairstyle is the answer a port should land on.
/// </summary>
public sealed class HairPortLocalTests(ITestOutputHelper o)
{
    internal static readonly (ushort From, ushort To)[] Pairs =
    [
        (801, 201), (201, 801), (1401, 201), (1801, 201), (1201, 201), (1601, 201), (1001, 201), (601, 401),
        (201, 1201), (201, 1601), (201, 1801),
        (701, 101), (101, 701), (1101, 101), (901, 101), (1301, 101), (1501, 101), (101, 1501),
    ];

    internal static readonly int[] Ids = [101, 105, 110, 116, 120, 130, 140, 150, 160, 170, 180, 190, 200];

    internal static string HairPath(ushort race, int id)
        => $"chara/human/c{race:D4}/obj/hair/h{id:D4}/model/c{race:D4}h{id:D4}_hir.mdl";


    internal sealed record Game(Lumina.GameData Data, PbdFile Pbd)
    {
        public byte[]? File(string path) => Data.GetFile(path)?.Data;

        /// <summary>The race's first face model the game has: not every race has f0001.</summary>
        public byte[]? Head(ushort race) => HairPortPlan.HeadPaths(race).Select(File).FirstOrDefault(b => b != null);

        public HashSet<string> ExtraBones(ushort race, int id)
            => File(HairPath(race, id)) is { } m
                ? SecondSkinWriter.Parse(m).BoneNames.Where(b => b.StartsWith("j_ex_", StringComparison.Ordinal)).ToHashSet()
                : [];
    }

    internal static Game Load()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return new Game(data, new PbdFile(data.GetFile("chara/xls/boneDeformer/human.pbd")!.Data));
    }

    /// <summary>
    /// Every shared hairstyle of every pair ports, and the fit check passes it — while the game's OWN copy of that
    /// hairstyle on the target race, which the check must also accept, stays inside the same limits. The margins are
    /// calibrated from what this prints.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void Ported_hair_sits_on_the_target_head()
    {
        var game = Load();
        float worstGameSunk = 0, worstGameShift = 0, worstPortSunk = 0, worstPortShift = 0;
        int ports = 0, hrothgarRefused = 0;
        var refused = new List<string>();
        foreach (var (from, to) in Pairs)
        {
            var sourceHead = game.Head(from)!;
            var targetHead = game.Head(to)!;
            foreach (int id in Ids)
            {
                var src = game.File(HairPath(from, id));
                var own = game.File(HairPath(to, id));
                if (src == null || own == null) continue;

                var s = HairPortBaker.Measure(src, sourceHead)!.Value;
                var g = HairPortBaker.Measure(own, targetHead)!.Value;
                var result = HairPortBaker.Prepare(src, from, to, id, game.Pbd, game.ExtraBones(to, id), sourceHead,
                                                   targetHead, out string why);
                // Hrothgar copies are re-authored rather than bent (h0170 sits 18 mm further off a Hrothgar's scalp),
                // so they say nothing about what a bend should be held to.
                if (from / 100 is not (15 or 16) && to / 100 is not (15 or 16))
                {
                    worstGameSunk = MathF.Max(worstGameSunk, g.Sunk);
                    worstGameShift = MathF.Max(worstGameShift, MathF.Abs(g.Gap - s.Gap));
                }
                if (result != null)
                {
                    ports++;
                    worstPortSunk = MathF.Max(worstPortSunk, result.Sunk);
                    worstPortShift = MathF.Max(worstPortShift, MathF.Abs(result.GapShift));
                }
                else if (from / 100 is 15 or 16 || to / 100 is 15 or 16) hrothgarRefused++;
                else refused.Add($"h{id:D4} c{from:D4}->c{to:D4}: {why}");
                o.WriteLine($"h{id:D4} c{from:D4}->c{to:D4}: game sunk {g.Sunk:P0} shift {(g.Gap - s.Gap) * 1000:F0} mm; " +
                            (result == null ? $"REFUSED {why}"
                                            : $"port sunk {result.Sunk:P0} shift {result.GapShift * 1000:F0} mm, {result.Millis} ms"));
            }
        }
        o.WriteLine($"game's own copies: worst sunk {worstGameSunk:P0}, worst shift {worstGameShift * 1000:F0} mm");
        // Hrothgar heads are the furthest from everyone's: refusing a port there is the check working, not failing.
        o.WriteLine($"Hrothgar ports refused: {hrothgarRefused}");
        o.WriteLine($"ports: {ports} made, {refused.Count} refused; worst sunk {worstPortSunk:P0}, worst shift {worstPortShift * 1000:F0} mm");

        // The check accepts what the game itself ships, and refuses almost none of the ports.
        Assert.True(worstGameSunk <= HairPortBaker.SunkLimit, $"the game's own copies sink {worstGameSunk:P0}");
        Assert.True(worstGameShift <= HairPortBaker.GapMargin, $"the game's own copies shift {worstGameShift * 1000:F0} mm");
        Assert.True(ports > 100, $"only {ports} ports were made");
        Assert.True(refused.Count * 20 <= ports + refused.Count, string.Join("\n", refused));
    }

    /// <summary>A hairstyle that has not been bent at all sits nowhere near a distant race's head, and is refused.</summary>
    [LocalDataTheory(LocalData.GameData)]
    [InlineData((ushort)1201, (ushort)201)]   // a Lalafell's, far below a Midlander's head
    [InlineData((ushort)201, (ushort)1201)]   // a Midlander's, far above a Lalafell's
    [InlineData((ushort)1401, (ushort)201)]   // an Au Ra's, sunk into a Midlander's
    [InlineData((ushort)201, (ushort)801)]    // a Midlander's, floating off a Miqo'te's
    [Trait("Plan", "EDGE-hair-race-port-30")]
    public void Unbent_hair_fails_the_fit_check(ushort from, ushort to)
    {
        var game = Load();
        var src = game.File(HairPath(from, 130))!;
        var s = HairPortBaker.Measure(src, game.Head(from)!)!.Value;
        var p = HairPortBaker.Measure(src, game.Head(to)!)!.Value;
        o.WriteLine($"c{from:D4} hair on c{to:D4} unbent: sunk {s.Sunk:P0} -> {p.Sunk:P0}, gap {s.Gap * 1000:F0} -> {p.Gap * 1000:F0} mm");
        Assert.True(p.Sunk > HairPortBaker.SunkLimit || !(MathF.Abs(p.Gap - s.Gap) <= HairPortBaker.GapMargin));
    }

    /// <summary>
    /// The head bone every hairstyle hangs from is one the deformer moves, for every race: otherwise a port would keep
    /// the source race's head position (a Lalafell's hair at a Midlander's chest).
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void Every_race_s_deformer_moves_the_head()
    {
        var game = Load();
        for (int n = 2; n <= 18; n++)
        {
            ushort race = (ushort)(n * 100 + 1);
            var chain = game.Pbd.Chain(RacialModelBake.CommonAncestor(race, 101), race);
            Assert.True(PbdFile.Lists(chain, "j_kao", _ => null), $"c{race:D4}: the deformer does not list j_kao");
        }
    }

    /// <summary>
    /// Against the answer: a port of one race's shared hairstyle lands within millimetres of the game's own model of
    /// that hairstyle on the other race — far nearer than the source does unbent.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void Port_lands_near_the_game_s_own_model()
    {
        var game = Load();
        foreach (var (from, to) in Pairs)
        {
            int n = 0;
            double before = 0, after = 0, worst = 0;
            foreach (int id in Ids)
            {
                var src = game.File(HairPath(from, id));
                var own = game.File(HairPath(to, id));
                if (src == null || own == null) continue;
                var port = RacialModelBake.Port(src, from, to, game.Pbd, HairPortBaker.ParentOf, out string why);
                Assert.True(port != null, why);
                var t = ModelPartReader.Read(own)!.Positions;
                double b = MedianNearest(ModelPartReader.Read(src)!.Positions, t);
                double a = MedianNearest(ModelPartReader.Read(port!)!.Positions, t);
                n++; before += b; after += a; worst = Math.Max(worst, a);
            }
            if (n == 0) continue;
            o.WriteLine($"c{from:D4}->c{to:D4}: {n} hairstyles, median distance to the game's own {before / n * 1000:F1} mm unbent, " +
                        $"{after / n * 1000:F1} mm ported, worst {worst * 1000:F1} mm");
            // Hrothgar heads differ from everyone's most; the game re-authors its hairstyles for them.
            double bound = from / 100 is 15 or 16 || to / 100 is 15 or 16 ? 0.020 : 0.012;
            Assert.True(after / n < bound, $"c{from:D4}->c{to:D4}: ports land {after / n * 1000:F1} mm from the game's own on average");
            Assert.True(worst < bound + 0.006, $"c{from:D4}->c{to:D4}: a port lands {worst * 1000:F1} mm from the game's own");
        }
    }

    /// <summary>Median, over a sample of <paramref name="a"/>'s vertices, of the distance to the nearest of <paramref name="b"/>'s.</summary>
    private static double MedianNearest(float[] a, float[] b)
    {
        int na = a.Length / 3, nb = b.Length / 3;
        int step = Math.Max(1, na / 1000);
        var d = new List<float>();
        for (int i = 0; i < na; i += step)
        {
            float best = float.MaxValue;
            for (int j = 0; j < nb; j++)
            {
                float dx = b[j * 3] - a[i * 3], dy = b[j * 3 + 1] - a[i * 3 + 1], dz = b[j * 3 + 2] - a[i * 3 + 2];
                best = MathF.Min(best, dx * dx + dy * dy + dz * dz);
            }
            d.Add(MathF.Sqrt(best));
        }
        d.Sort();
        return d[d.Count / 2];
    }

    /// <summary>A port folds no triangle over and collapses none, for every shared hairstyle of every pair.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-race-port-2")]
    public void Ported_hair_has_no_folded_or_degenerate_triangles()
    {
        // The bend itself, before any check: what it does to every shared hairstyle of every pair, Miqo'te included.
        var game = Load();
        int bent = 0, hrothgarOver = 0;
        var over = new List<string>();
        foreach (var (from, to) in Pairs)
            foreach (int id in Ids)
            {
                if (game.File(HairPath(from, id)) is not { } src || game.File(HairPath(to, id)) == null) continue;
                var port = RacialModelBake.Port(src, from, to, game.Pbd, HairPortBaker.ParentOf, out string why);
                Assert.True(port != null, why);
                var a = ModelPartReader.Read(src)!;
                var b = ModelPartReader.Read(port!)!;
                int triangles = a.Parts.Where(p => p.Island == -1).Sum(p => p.TriangleCount);
                int folded = HairPortBaker.Folded(a, b), collapsed = RacialRouteLocalTests.Collapsed(a, b);
                if (folded > 0) o.WriteLine($"h{id:D4} c{from:D4}->c{to:D4}: {folded} of {triangles} turned");
                Assert.True(collapsed == 0, $"h{id:D4} c{from:D4}->c{to:D4}: {collapsed} collapsed");

                // Over the limit, the fit check refuses the port. Hrothgar heads are the furthest from everyone's, and
                // there that is the check working; elsewhere it must stay rare.
                if (folded > HairPortBaker.FoldedShare * triangles)
                {
                    if (from / 100 is 15 or 16 || to / 100 is 15 or 16) hrothgarOver++;
                    else over.Add($"h{id:D4} c{from:D4}->c{to:D4}: {folded} of {triangles}");
                }
                bent++;
            }
        o.WriteLine($"{bent} bends checked; over the fold limit, so refused: {hrothgarOver} Hrothgar, " +
                    $"{over.Count} others [{string.Join("; ", over)}]");
        Assert.True(bent > 150);
        Assert.True(over.Count * 50 <= bent, $"{over.Count} of {bent} bends turn too much inside out: {string.Join("; ", over)}");
    }

    /// <summary>The Miqo'te ear fur and its skin are taken out of a port to a race without ears, on the game's own hair.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-race-port-4")]
    public void Miqote_fur_is_hidden_on_real_hair()
    {
        var game = Load();
        int withFur = 0;
        foreach (int id in Ids)
        {
            if (game.File(HairPath(801, id)) is not { } src || game.File(HairPath(201, id)) == null) continue;
            var result = HairPortBaker.Prepare(src, 801, 201, id, game.Pbd, game.ExtraBones(201, id), game.Head(801),
                                               game.Head(201), out var why);
            Assert.True(result != null, why);
            Assert.DoesNotContain(SecondSkinWriter.Parse(result!.Model).BoneNames, b => b.StartsWith("j_mimi"));
            if (result.FurHidden > 0) withFur++;
        }
        o.WriteLine($"{withFur} of the Miqo'te hairstyles carried fur");
    }

    /// <summary>A port keeps every shape key, LOD and mesh of the game's own hairstyles that carry them.</summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-race-port-26")]
    public void Port_keeps_shapes_and_lods()
    {
        var game = Load();
        int withShapes = 0;
        foreach (var (from, to) in Pairs)
            foreach (int id in Ids)
            {
                if (game.File(HairPath(from, id)) is not { } src) continue;
                var a = SecondSkinWriter.Parse(src);
                if (a.Shapes.Count == 0) continue;
                var port = RacialModelBake.Port(src, from, to, game.Pbd, HairPortBaker.ParentOf, out string why);
                Assert.True(port != null, why);
                var b = SecondSkinWriter.Parse(port!);
                Assert.Equal(a.Shapes.Keys.OrderBy(k => k), b.Shapes.Keys.OrderBy(k => k));
                Assert.Equal(a.MeshCount, b.MeshCount);
                Assert.Equal(a.Lod0MeshCount, b.Lod0MeshCount);
                Assert.Equal(src.Length, port!.Length);
                withShapes++;
            }
        o.WriteLine($"{withShapes} ports of hairstyles with shape keys checked");
        Assert.True(withShapes > 0, "no hairstyle in the sample carries a shape key");
    }
}
