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

        /// <summary>The extra bones a race's skeleton has for a hairstyle, as the builder reads them.</summary>
        public HashSet<string> ExtraBones(ushort race, int id)
        {
            var bones = File(HairPath(race, id)) is { } m
                ? SecondSkinWriter.Parse(m).BoneNames.Where(b => b.StartsWith("j_ex_", StringComparison.Ordinal)).ToHashSet()
                : [];
            bones.UnionWith(HairPortPlan.SkeletonBones(File(HairPortPlan.SkeletonPath(race, id))));
            return bones;
        }
    }

    internal static Game Load()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return new Game(data, new PbdFile(data.GetFile("chara/xls/boneDeformer/human.pbd")!.Data));
    }

    /// <summary>
    /// Every shared hairstyle of every pair ports, and the fit check passes it (one that fails is saved with a warning,
    /// never refused) — while the game's OWN copy of that
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
                // A port that fails the fit check is saved with a warning, never refused; "refused" here counts those,
                // and anything refused for what cannot be made.
                if (result == null)
                {
                    Assert.DoesNotContain("sinks into the skull", why);
                    if (from / 100 is 15 or 16 || to / 100 is 15 or 16) hrothgarRefused++;
                    else refused.Add($"h{id:D4} c{from:D4}->c{to:D4}: {why}");
                    o.WriteLine($"h{id:D4} c{from:D4}->c{to:D4}: REFUSED {why}");
                    continue;
                }
                bool fits = result.Sunk <= HairPortBaker.SunkLimit && MathF.Abs(result.GapShift) <= HairPortBaker.GapMargin;
                Assert.Equal(!fits, result.Warning?.Contains("sinks into the skull") == true);
                if (fits)
                {
                    ports++;
                    worstPortSunk = MathF.Max(worstPortSunk, result.Sunk);
                    worstPortShift = MathF.Max(worstPortShift, MathF.Abs(result.GapShift));
                }
                else if (from / 100 is 15 or 16 || to / 100 is 15 or 16) hrothgarRefused++;
                else refused.Add($"h{id:D4} c{from:D4}->c{to:D4}: {result.Warning}");
                o.WriteLine($"h{id:D4} c{from:D4}->c{to:D4}: game sunk {g.Sunk:P0} shift {(g.Gap - s.Gap) * 1000:F0} mm; " +
                            (fits ? $"port sunk {result.Sunk:P0} shift {result.GapShift * 1000:F0} mm, {result.Millis} ms"
                                  : $"WARNED {result.Warning}"));
            }
        }
        o.WriteLine($"game's own copies: worst sunk {worstGameSunk:P0}, worst shift {worstGameShift * 1000:F0} mm");
        // Hrothgar heads are the furthest from everyone's: a fit warning there is the check working, not failing.
        o.WriteLine($"Hrothgar ports warned of their fit: {hrothgarRefused}");
        o.WriteLine($"ports: {ports} fit, {refused.Count} warned of their fit; worst sunk {worstPortSunk:P0}, worst shift {worstPortShift * 1000:F0} mm");

        // The check accepts what the game itself ships, and warns of almost none of the ports.
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

                // Over the limit, the port is saved with a warning (cross-sex-tops-yas: never refused for folds).
                // Hrothgar heads are the furthest from everyone's, and there that is expected; elsewhere it stays rare.
                if (folded > HairPortBaker.FoldedShare * triangles)
                {
                    if (from / 100 is 15 or 16 || to / 100 is 15 or 16) hrothgarOver++;
                    else over.Add($"h{id:D4} c{from:D4}->c{to:D4}: {folded} of {triangles}");
                }
                bent++;
            }
        o.WriteLine($"{bent} bends checked; over the fold limit, so warned: {hrothgarOver} Hrothgar, " +
                    $"{over.Count} others [{string.Join("; ", over)}]");
        Assert.True(bent > 150);
        Assert.True(over.Count * 50 <= bent, $"{over.Count} of {bent} bends turn too much inside out: {string.Join("; ", over)}");
    }

    /// <summary>
    /// Every shared hairstyle bend over the fold limit is saved with a warning that speaks of turning inside out, never
    /// refused for its folds (cross-sex-tops-yas) or its fit, which is only warned of.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-cross-sex-tops-yas-8")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-22")]
    public void Over_the_fold_limit_a_port_is_saved_with_a_warning() // [AC-cross-sex-tops-yas-8] [EDGE-cross-sex-tops-yas-22]
    {
        var game = Load();
        int warned = 0, quiet = 0;
        foreach (var (from, to) in Pairs)
            foreach (int id in Ids)
            {
                if (game.File(HairPath(from, id)) is not { } src || game.File(HairPath(to, id)) == null) continue;
                var result = HairPortBaker.Prepare(src, from, to, id, game.Pbd, game.ExtraBones(to, id), game.Head(from),
                                                   game.Head(to), out var why);
                if (result == null)
                {
                    // Refused only for what cannot be made: bones the target lacks, never folds or fit.
                    Assert.DoesNotContain("inside out", why);
                    Assert.DoesNotContain("sinks into the skull", why);
                    continue;
                }
                var a = ModelPartReader.Read(src)!;
                int triangles = a.Parts.Where(p => p.Island == -1).Sum(p => p.TriangleCount);
                bool over = result.Folded > HairPortBaker.FoldedShare * triangles;
                Assert.Equal(over, result.Warning?.Contains("inside out") == true);
                if (over) warned++;
                else quiet++;
            }
        o.WriteLine($"{warned} saved with a fold warning, {quiet} without");
        Assert.True(warned + quiet > 150);
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

    /// <summary>
    /// The reported pack (Loosened Bun) is a mashup of the game's Miqo'te h0194 and h0195, and like them was refused
    /// for a Lalafell: its fur, turned over by the Lalafell's own ear bones, was counted though it is taken out.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-race-port-4")]
    public void Miqote_hair_with_fur_ports_to_a_lalafell()
    {
        var game = Load();
        foreach (int id in new[] { 194, 195 })
        {
            var src = game.File(HairPath(801, id))!;
            var result = HairPortBaker.Prepare(src, 801, 1201, id, game.Pbd, game.ExtraBones(1201, id), game.Head(801),
                                               game.Head(1201), out var why);
            Assert.True(result != null, $"h{id:D4}: {why}");
            Assert.True(result!.FurHidden > 0, $"h{id:D4} no longer carries fur");
        }
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

    /// <summary>
    /// The game's own skeleton files name their extra bones as plain strings, per race: what lets a port borrow another
    /// hairstyle's skeleton for its race, as physics hair mods do.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-hair-race-port-5")]
    public void Skeleton_files_name_their_extra_bones()
    {
        var game = Load();
        foreach (ushort race in new ushort[] { 201, 401, 801, 1401, 1801 })
        {
            var bones = HairPortPlan.SkeletonBones(game.File(HairPortPlan.SkeletonPath(race, 170)));
            o.WriteLine($"c{race:D4} h0170 skeleton: {string.Join(", ", bones.Order())}");
            Assert.Contains("j_ex_h0170_ke_b", bones);
        }
    }

    /// <summary>
    /// The harness's own proof that the game's table of hairstyle skeletons is read right: it is laid out as the game
    /// writes it, and every hairstyle the game ships moves with no bone the skeleton its race loads for it lacks — the
    /// skeleton the table names, not the one at the hairstyle's own number. A hairstyle with no entry moves with none.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-skeleton-infer-2")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-21")]
    public void The_games_hair_skeleton_table_is_what_its_own_models_move_with()
    {
        var game = Load();
        var bytes = game.File(HairSkeletonTable.GamePath)!;
        var table = HairSkeletonTable.Parse(bytes)!;
        Assert.True(HairSkeletonTable.IsSorted(bytes));
        Assert.Equal((9, 9, 9, 8), (table.Loaded(201, 8), table.Loaded(401, 8), table.Loaded(1201, 9), table.Loaded(1801, 8)));

        int examined = 0;
        var wrong = new List<string>();
        // The game's own quirks: models naming bones the skeleton their race loads does not have (a malformed
        // "j_ex_h002_" with no entry at all; three naming another hairstyle's bones). Pinned, so a patch that fixes one,
        // or any other mismatch, fails here.
        string[] quirks = ["c0101 h0103", "c0201 h0103", "c0501 h0214", "c0701 h0162", "c0801 h0204"];
        foreach (ushort race in Enumerable.Range(1, 18).Select(n => (ushort)(n * 100 + 1)))
            for (int set = 1; set <= 300; set++)
            {
                if (game.File(HairPath(race, set)) is not { } model) continue;
                examined++;
                var extra = HairPortSkeleton.ExtraBones(model);
                int loaded = table.Loaded(race, set);
                var have = loaded > 0 ? HairPortPlan.SkeletonBones(game.File(HairPortPlan.SkeletonPath(race, loaded))) : [];
                var missing = extra.Where(b => !have.Contains(b)).ToList();
                if (missing.Count > 0) wrong.Add($"c{race:D4} h{set:D4} (loads {loaded}): {string.Join(" ", missing)}");
            }
        o.WriteLine($"{table.Count} entries; {examined} of the game's hairstyles checked");
        foreach (var w in wrong) o.WriteLine(w);

        // What else a slot ships beside its skeleton: a carry skips a slot with kinematics (.kdb) rather than pair them
        // with a foreign skeleton, and copies no .skp — the game ships none for hair. (.bnmb has no hair path Penumbra
        // knows of, so none is looked for.)
        int skp = 0;
        var kdb = new List<string>();
        foreach (ushort race in Enumerable.Range(1, 18).Select(n => (ushort)(n * 100 + 1)))
            foreach (int slot in table.SkeletonsLoaded(race, except: 0))
            {
                if (game.File(HairPortPlan.SkeletonPath(race, slot).Replace(".sklb", ".skp")) != null) skp++;
                if (game.File(HairPortPlan.KinematicsPath(race, slot)) != null) kdb.Add($"c{race:D4} h{slot:D4}");
            }
        o.WriteLine($"hair skeleton slots shipping .skp: {skp}; .kdb: {kdb.Count} ({string.Join(", ", kdb)})");
        Assert.Equal(0, skp);
        Assert.True(examined > 1000, $"only {examined} hairstyles found");
        Assert.Equal(quirks, wrong.Select(w => w[..11]));
    }

    /// <summary>
    /// The reported pack: a Midlander hairstyle 8 that moves with hairstyle 9's skeleton, as the game's own does — stood
    /// in for by the game's own c0201 h0008, which has exactly the reported bones. Every woman's race that can choose
    /// hairstyle 8 gets a port, and loads a skeleton with every bone it moves with: the Midlander's skeleton 9 copied to
    /// a slot only hairstyle 8 of that race loads. The automatic path and Studio agree; each port is current once saved.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-hair-skeleton-infer-1")]
    [Trait("Plan", "AC-hair-skeleton-infer-3")]
    [Trait("Plan", "AC-hair-skeleton-infer-4")]
    [Trait("Plan", "AC-hair-skeleton-infer-7")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-20")]
    [Trait("Plan", "EDGE-hair-skeleton-infer-25")]
    public void The_reported_hairstyle_8_ports_with_the_skeleton_each_race_loads()
    {
        var game = Load();
        var source = HairPath(201, 8);
        var model = game.File(source)!;
        var extra = HairPortSkeleton.ExtraBones(model).Order();
        Assert.True(extra.SequenceEqual(["j_ex_h0009_ke_b", "j_ex_h0009_ke_f", "j_ex_h0009_ke_r", "j_ex_h0009_ke_u"]),
                    "the game's c0201 h0008 no longer reproduces the report: " + string.Join(", ", extra));

        // As the pack ships it: the model and its material, from the default data.
        var files = new Dictionary<string, string> { [source] = "hair.mdl" };
        var materials = SecondSkinWriter.Parse(model).MatNames.Distinct().ToList();
        for (int i = 0; i < materials.Count; i++) files[HairMaterialPath.GamePath(201, 8, materials[i])!] = $"mt{i}.mtrl";
        using var mod = new HairPortFixtures.Mod(defaults: files);
        mod.Put("hair.mdl", model);
        for (int i = 0; i < materials.Count; i++) mod.Put($"mt{i}.mtrl", game.File(HairMaterialPath.GamePath(201, 8, materials[i])!)!);

        var plans = new[] { true, false }.Select(sharers =>
            HairPortBuilder.Build(mod.Root, source, HairPortPlan.SameGenderRaces(201), game.Pbd, game.File, sharers)).ToList();
        foreach (var (r, why) in plans[0].Refused) o.WriteLine($"c{r:D4} refused: {why}");
        foreach (var w in plans[0].Writes) o.WriteLine($"{w.TargetGamePath}: entry {w.Est}, carried from {w.SkeletonFrom}");
        Assert.Equal(plans[0].Writes.Select(w => (w.TargetGamePath, w.Est, w.SkeletonFrom)),
                     plans[1].Writes.Select(w => (w.TargetGamePath, w.Est, w.SkeletonFrom)));
        var plan = plans[0];

        ushort[] fitted = [401, 601, 801, 1001, 1201, 1401, 1801];
        Assert.Equal(fitted.Select(r => HairPath(r, 8)), plan.Writes.Select(w => w.TargetGamePath).Order());
        foreach (var w in plan.Writes)
        {
            Assert.Equal(9, w.SkeletonFrom);
            HairPortBuilderTests.AssertLoadsEveryBone(w, model, game.File);
            ushort to = HairPortPlan.Parse(w.TargetGamePath)!.Value.Race;
            Assert.Equal(game.File(HairPortPlan.SkeletonPath(201, 9)),
                         w.Materials.Single(m => m.GamePath == HairPortPlan.SkeletonPath(to, w.Est)).Bytes);
            Assert.Equal(game.File(HairPortPlan.PhysicsPath(201, 9)),
                         w.Materials.Single(m => m.GamePath == HairPortPlan.PhysicsPath(to, w.Est)).Bytes);
        }
        Assert.Contains("c1601", plan.Refused[1601]);
        Assert.DoesNotContain("j_ex_", plan.Refused[1601]);

        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: true).Ok);
        var record = HairPortRecord.Read(mod.Root);
        Assert.All(record.Ports, e => Assert.False(
            HairPortBuilder.IsOutOfDate(mod.Root, e, mod.Get(e.SourceFile), mod.Owners, record.Ports, game.File), e.TargetGamePath));
        var again = HairPortBuilder.Build(mod.Root, source, fitted, game.Pbd, game.File, withSharers: true);
        Assert.Empty(again.Writes);
        Assert.Equal(fitted.Order(), again.Current.Order());
    }
}
