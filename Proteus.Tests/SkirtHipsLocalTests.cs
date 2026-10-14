using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// Geometric's high-waisted skirt onto Rue "Watermelon Crushers - A", as the automatic refit makes it (the chest beneath
/// included): Rue's buttock through the middle of the skirt's long faces, and the skirt's own stocking through it at the
/// waistband. The skirt is a thick shell, its walls 2 mm apart — see <see cref="BodyRetarget.FaceCheck.WallPartner"/>.
/// Measured inside the pass, on the solve's own nodes; the buttock also against the skin the file draws.
/// </summary>
public class SkirtHipsLocalTests(ITestOutputHelper output)
{
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string FixPubes = LocalData.Mods + ":fix pubes";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Heartthrob = LocalData.Mods + ":Heartthrob - by Solona";
    private const string Scarlet = LocalData.Mods + ":Scarlet - by Solona";
    private const string Race = "0201";
    private const string Skirt = "leg size/small/chara/equipment/e6010/model/c0201e6010_dwn.mdl";

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));
    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <param name="ShellFolds">Folded triangles with a corner on the skirt's shell, the part that is seen.</param>
    /// <param name="RegionFolds">Folded triangles over the buttock or at the waistband.</param>
    /// <param name="DrawnButtock">Shell faces over the buttock with the skin the written file draws through their middle.</param>
    private sealed record Measured(int FacesThrough, int ButtockFaces, int StockingCells, int Folds, int ShellFolds,
                                   int RegionFolds, int DrawnButtock, byte[] Model);

    /// <summary>The skirt refitted onto Rue WC A, with Rue's <paramref name="chest"/> beneath or (null) the legs alone,
    /// and what came through what.</summary>
    /// <param name="sizeSet">Refitted as the size set refits it (<see cref="BodyRetarget.Plan"/> with the skin taken and
    /// the default Keep shape pieces), not as the automatic refit does.</param>
    private static Measured Refit(BodyRetarget.Tuning tuning, string? chest, bool sizeSet = false)
    {
        var bodies = new[] { Body(FixPubes), Body(Bibo), Body(Rue) }.ToList();
        var rue = bodies[2];
        BodySizeCatalog? CatalogOf(string dir)
            => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Geometric), Skirt));
        var skirt = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var pick = RefitCore.DetectSource(skirt, bones, "_dwn", Race, bodies, rue.Dir, _ => true)!;
        var legsTo = rue.Catalog.For("_dwn", Race).First(o => o.Label == "None · Watermelon Crushers - A");
        Assert.Null(BodyRetarget.BuildPair("_dwn", CatalogOf(pick.Dir)!.PathOf(pick.Option), rue.Catalog.PathOf(legsTo), "_dwn",
                                           false, null, null, null, out var legsPair));
        List<BodyRetarget.SlotPair> pairs = [legsPair];
        if (chest != null)
        {
            var beneath = RefitCore.ChestBeneathOf(skirt, pick, Race, bodies, CatalogOf)!;
            var chestTo = rue.Catalog.For("_top", Race).First(o => o.Label == "None · " + chest);
            Assert.True(RefitCore.ChestBeneathPair(beneath, rue.Dir, rue.Catalog, chestTo, false, null, null, null, _ => { },
                                                   out var chestPair));
            pairs.Add(chestPair);
        }

        Vec3[]? final = null;
        void Final(string s, Vec3[] d) { if (s == "layer-order") final = d; }
        var plan = sizeSet
            ? BodyRetarget.WithTuning(tuning, () => BodyRetarget.Plan(skirt, bytes, pairs, "_dwn", held: new HashSet<int>(), replaceSkin: true,
                                                                      acrossBodies: true, clearBody: false, cutHidden: true,
                                                                      keepShape: RefitCore.ShapePieces(skirt, RefitCore.DefaultKeepShape(skirt), out _),
                                                                      trace: Final))
            : BodyRetarget.WithTuning(tuning, () => RefitCore.PlanRefit(skirt, bytes, pairs, "_dwn", true, false, out _, trace: Final));
        Assert.NotNull(final);

        var sets = BodyRetarget.Sets.From(skirt, new HashSet<int>(), BodyRetarget.PartedSkinMeshes(skirt, pairs, "_dwn", true, null));
        var stocking = new bool[sets.NodeCount];
        var shell = new bool[sets.NodeCount];
        foreach (var part in skirt.Parts.Where(p => p.Island < 0))
            foreach (int v in part.Triangles)
            {
                if (part.Mesh == 1 && part.Submesh <= 2) stocking[sets.NodeOf[v]] = true;
                if (part.Mesh == 2) shell[sets.NodeOf[v]] = true;
            }
        var drawn = BodyRetarget.TargetBody.Build(pairs, null, null, before: false);
        var legs = new BodySurface(legsPair.Target, BodySurface.CellFor(0.01f));
        Vector3 P(int n) => V(sets.NodeAt[n]) + V(final![n]);
        // The buttock: the back of the hips below the waist; the waistband: front or back above it.
        bool Buttock(Vector3 p) => legs.Nearest(p, 0.05f, out var l) && l.Normal.Z < -0.3f && l.Point.Y is >= 0.81f and < 0.98f;
        bool Region(Vector3 p) => legs.Nearest(p, 0.05f, out var l)
                                  && ((l.Normal.Z < -0.3f && l.Point.Y is >= 0.81f and < 1.09f)
                                      || (l.Normal.Z > 0.3f && l.Point.Y is >= 0.98f and < 1.09f));

        // The skin the written file draws (cut and pulled as the swap leaves it), for the buttock check below.
        var rebuilt = ModelPartReader.Read(plan.Model)!;
        var drawnSkin = new BodySurface(rebuilt, BodySurface.CellFor(0.01f));

        int faces = 0, buttock = 0, folds = 0, shellFolds = 0, regionFolds = 0, drawnButtock = 0;
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int a = sets.NodeOf[sets.Tris[t]], b = sets.NodeOf[sets.Tris[t + 1]], c = sets.NodeOf[sets.Tris[t + 2]];
            if (a == b || b == c || a == c) continue;
            var centre = (P(a) + P(b) + P(c)) / 3f;
            var n0 = Vector3.Cross(V(sets.NodeAt[b]) - V(sets.NodeAt[a]), V(sets.NodeAt[c]) - V(sets.NodeAt[a]));
            if (n0.LengthSquared() > 1e-24f && Vector3.Dot(n0, Vector3.Cross(P(b) - P(a), P(c) - P(a))) < 0f)
            {
                folds++;
                if (shell[a] || shell[b] || shell[c]) shellFolds++;
                // Over the buttock or at the waistband on the pieces that are seen: the shell and the stocking. The skirt's
                // inner pieces lie inside the skin as its author made them (accepted, STOP 2).
                if (Region(centre) && (shell[a] || shell[b] || shell[c] || stocking[a] || stocking[b] || stocking[c])) regionFolds++;
            }
            if (!shell[a]) continue;
            if (Buttock(centre) && drawnSkin.Nearest(centre, 0.03f, out var ds) && Vector3.Dot(centre - ds.Point, ds.Normal) < -0.0002f)
                drawnButtock++;
            if (!drawn.Deepest(centre, 0.03f, out var h) || Vector3.Dot(centre - h.Point, h.Normal) >= -0.0002f) continue;
            faces++;
            if (Buttock(centre)) buttock++;
        }

        // 5 mm cells of Rue's legs skin where the stocking stands more than 0.5 mm in front of the shell.
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / 0.005f), (int)MathF.Floor(p.Y / 0.005f), (int)MathF.Floor(p.Z / 0.005f));
        var high = new Dictionary<(int, int, int), float>();
        var low = new Dictionary<(int, int, int), float>();
        for (int n = 0; n < sets.NodeCount; n++)
        {
            if (!stocking[n] && !shell[n]) continue;
            if (!legs.Nearest(P(n), 0.04f, out var h)) continue;
            float g = Vector3.Dot(P(n) - h.Point, h.Normal);
            var k = Cell(h.Point);
            if (stocking[n] && !shell[n]) high[k] = high.TryGetValue(k, out float o) ? MathF.Max(o, g) : g;
            if (shell[n]) low[k] = low.TryGetValue(k, out float o) ? MathF.Min(o, g) : g;
        }
        int cells = high.Count(kv => low.TryGetValue(kv.Key, out float s) && kv.Value > s + 0.0005f);
        return new Measured(faces, buttock, cells, folds, shellFolds, regionFolds, drawnButtock, plan.Model);
    }

    public static TheoryData<string> Chests => new() { "Small", "Medium", "Large", "legs only" };

    [LocalDataTheory(Geometric, Bibo, FixPubes, Rue)]
    [MemberData(nameof(Chests))]
    [Trait("Plan", "AC-skirt-hips-1")]
    [Trait("Plan", "AC-skirt-hips-2")]
    [Trait("Plan", "AC-skirt-hips-3")]
    [Trait("Plan", "AC-skirt-hips-7")]
    [Trait("Plan", "EDGE-skirt-hips-1")]
    [Trait("Plan", "EDGE-skirt-hips-2")]
    [Trait("Plan", "EDGE-skirt-hips-11")]
    public void Rues_buttock_and_the_stocking_stay_under_Geometrics_skirt(string chest)
    {
        string? beneath = chest == "legs only" ? null : chest;
        var now = Refit(new BodyRetarget.Tuning(), beneath);
        var before = Refit(new BodyRetarget.Tuning(NoWallPair: true, NoWallCover: true, NoRimTurnGuard: true), beneath);
        output.WriteLine($"{chest}: shell faces with skin through {before.FacesThrough} -> {now.FacesThrough} (buttock {before.ButtockFaces} -> "
                       + $"{now.ButtockFaces}, against the drawn skin {before.DrawnButtock} -> {now.DrawnButtock}); stocking through the "
                       + $"shell {before.StockingCells} -> {now.StockingCells} cells; folds {before.Folds} -> {now.Folds} (shell "
                       + $"{before.ShellFolds} -> {now.ShellFolds}, buttock/waistband {before.RegionFolds} -> {now.RegionFolds})");
        // Printed beside it, not held to it: the size set refits the same pairs its own way (EDGE-11).
        var set = Refit(new BodyRetarget.Tuning(), beneath, sizeSet: true);
        output.WriteLine($"{chest}, as the size set refits it: shell faces with skin through {set.FacesThrough} (buttock {set.ButtockFaces}, "
                       + $"against the drawn skin {set.DrawnButtock}); stocking through the shell {set.StockingCells} cells; folds {set.Folds} "
                       + $"(shell {set.ShellFolds}, buttock/waistband {set.RegionFolds})");

        // What showed in game (build 1256): 28 faces and 14 cells with the chest beneath.
        Assert.True(before.ButtockFaces >= 10, "the harness should reproduce what showed in game");
        Assert.Equal(0, now.ButtockFaces);
        Assert.Equal(0, now.DrawnButtock);
        // Legs only, nothing is beneath the waist to clear it from Rue's belly (see ChestBeneath): there the bar is fewer
        // than before.
        Assert.True(beneath != null ? now.FacesThrough <= 2 : now.FacesThrough < before.FacesThrough,
                    $"{now.FacesThrough} shell faces with skin through, {before.FacesThrough} before");
        // Legs only (accepted, build 1261): 5 -> 3 cells.
        Assert.True(beneath != null ? now.StockingCells <= 2 : now.StockingCells <= 3,
                    $"{now.StockingCells} cells of stocking through the shell, {before.StockingCells} before");
        // No more folds anywhere: none on the shell, none more over the buttock or at the waistband on the pieces that are
        // seen, and in all no more than before (with the rim lift's turn guard, 258 -> 248).
        Assert.True(now.ShellFolds <= before.ShellFolds, $"{now.ShellFolds} shell triangles folded, {before.ShellFolds} before");
        Assert.True(now.RegionFolds <= before.RegionFolds,
                    $"{now.RegionFolds} folded over the buttock or waistband, {before.RegionFolds} before");
        Assert.True(now.Folds <= before.Folds, $"{now.Folds} folded, {before.Folds} before");

        // Positions only: the same model as the refit without the rules, but for where its points stand.
        var written = ModelPartReader.Read(now.Model)!;
        var old = ModelPartReader.Read(before.Model)!;
        Assert.Equal(old.MeshSpans.Count, written.MeshSpans.Count);
        Assert.Equal(old.Positions.Length, written.Positions.Length);
        Assert.Equal(old.Normals, written.Normals);
        Assert.Equal(old.Parts.Select(p => p.Material), written.Parts.Select(p => p.Material));
    }

    [LocalDataFact(Heartthrob, Scarlet, Bibo, Rue, Neolithe)]
    [Trait("Plan", "AC-skirt-hips-5")]
    [Trait("Plan", "EDGE-skirt-hips-12")]
    public void Hip_level_pants_are_no_worse()
    {
        var bodies = new[] { Body(Bibo), Body(Rue), Body(Neolithe) }.ToList();
        var rueLegs = bodies[1].Catalog.For("_dwn", Race).First(o => o.Label == "None · Watermelon Crushers - A");
        var neoLegs = bodies[2].Catalog.For("_dwn", Race).First(o => o.Label.EndsWith("SFW M", StringComparison.Ordinal));
        foreach (var (mod, rel) in new[]
                 {
                     (Heartthrob, "bottom size/medium/chara/equipment/e6071/model/c0201e6071_dwn.mdl"),
                     (Scarlet, "pant size/neolithe m/chara/equipment/e0041/model/c0201e0041_dwn.mdl"),
                 })
            foreach (var (target, legsTo) in new[] { (bodies[1], rueLegs), (bodies[2], neoLegs) })
            {
                var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), rel));
                var pants = ModelPartReader.Read(bytes)!;
                var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
                var pick = RefitCore.DetectSource(pants, bones, "_dwn", Race, bodies, target.Dir, _ => true)!;
                var from = bodies.First(b => string.Equals(b.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase)).Catalog;
                if (AutoRefitDecisions.SameFile(pick.Dir, pick.Option, target.Dir, legsTo)) continue;
                Assert.Null(BodyRetarget.BuildPair("_dwn", from.PathOf(pick.Option), target.Catalog.PathOf(legsTo), "_dwn", false,
                                                   null, null, null, out var pair));
                bool across = !string.Equals(pick.Dir, target.Dir, StringComparison.OrdinalIgnoreCase);
                var sets = BodyRetarget.Sets.From(pants, new HashSet<int>(), BodyRetarget.PartedSkinMeshes(pants, [pair], "_dwn", across, null));
                (BodyRetarget.Planned Plan, int Folds) Run(BodyRetarget.Tuning t)
                {
                    Vec3[]? final = null;
                    var plan = BodyRetarget.WithTuning(t, () => RefitCore.PlanRefit(pants, bytes, [pair], "_dwn", across, false, out _,
                                                                                   trace: (s, d) => { if (s == "layer-order") final = d; }));
                    return (plan, BodyRetarget.FoldedTriangles(sets, final!));
                }
                var now = Run(new BodyRetarget.Tuning());
                var before = Run(new BodyRetarget.Tuning(NoWallPair: true, NoWallCover: true, NoRimTurnGuard: true));
                var inNow = BodySizeMatch.ClothAgainst(ModelPartReader.Read(now.Plan.Model)!, target.Catalog.PathOf(legsTo))!.Value;
                var inBefore = BodySizeMatch.ClothAgainst(ModelPartReader.Read(before.Plan.Model)!, target.Catalog.PathOf(legsTo))!.Value;
                output.WriteLine($"{Path.GetFileName(LocalData.Path(mod))} onto {target.Dir} / {legsTo.Label}: inside {inBefore.Inside} -> "
                               + $"{inNow.Inside}; folds {before.Folds} -> {now.Folds}; worst move {before.Plan.Report.WorstMove * 1000f:F1} -> "
                               + $"{now.Plan.Report.WorstMove * 1000f:F1} mm");
                // Accepted when the rules were made (build 1261): Scarlet 3 points more inside on Rue WC A (of 2,711), its
                // worst move 1.7 mm more, its folds 77 -> 61; Heartthrob fewer inside.
                Assert.True(inNow.Inside <= inBefore.Inside + 3, $"{inNow.Inside} inside, {inBefore.Inside} before");
                Assert.True(now.Folds <= before.Folds, $"{now.Folds} folded, {before.Folds} before");
                Assert.True(now.Plan.Report.WorstMove <= before.Plan.Report.WorstMove + 0.002f,
                            $"worst move {now.Plan.Report.WorstMove * 1000f:F1} mm, {before.Plan.Report.WorstMove * 1000f:F1} before");
            }
    }
}
