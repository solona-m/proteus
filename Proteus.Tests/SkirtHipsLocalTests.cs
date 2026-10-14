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
/// Measured inside the pass, on the solve's own nodes.
/// </summary>
[Collection(GameBodyCollection.Name)]
public class SkirtHipsLocalTests(ITestOutputHelper output)
{
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string FixPubes = LocalData.Mods + ":fix pubes";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Heartthrob = LocalData.Mods + ":Heartthrob - by Solona";
    private const string Scarlet = LocalData.Mods + ":Scarlet - by Solona";
    private const string Race = "0201";
    private const string Skirt = "leg size/small/chara/equipment/e6010/model/c0201e6010_dwn.mdl";

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));
    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    private sealed record Measured(int FacesThrough, int ButtockFaces, int StockingCells, int Folds, byte[] Model);

    /// <summary>The skirt refitted onto Rue WC A with Rue's Medium chest beneath, and what came through what.</summary>
    private static Measured Refit(BodyRetarget.Tuning tuning)
    {
        var bodies = new[] { Body(FixPubes), Body(Bibo), Body(Rue) }.ToList();
        var rue = bodies[2];
        BodySizeCatalog? CatalogOf(string dir)
            => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Geometric), Skirt));
        var skirt = ModelPartReader.Read(bytes)!;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
        var pick = RefitCore.DetectSource(skirt, bones, "_dwn", Race, bodies, rue.Dir, _ => true)!;
        var beneath = RefitCore.ChestBeneathOf(skirt, pick, Race, bodies, CatalogOf)!;
        var legsTo = rue.Catalog.For("_dwn", Race).First(o => o.Label == "None · Watermelon Crushers - A");
        var chestTo = rue.Catalog.For("_top", Race).First(o => o.Label == "None · Medium");
        Assert.Null(BodyRetarget.BuildPair("_dwn", CatalogOf(pick.Dir)!.PathOf(pick.Option), rue.Catalog.PathOf(legsTo), "_dwn",
                                           false, null, null, null, out var legsPair));
        Assert.True(RefitCore.ChestBeneathPair(beneath, rue.Dir, rue.Catalog, chestTo, false, null, null, null, _ => { },
                                               out var chestPair));
        List<BodyRetarget.SlotPair> pairs = [legsPair, chestPair];

        Vec3[]? final = null;
        var plan = BodyRetarget.WithTuning(tuning, () => RefitCore.PlanRefit(skirt, bytes, pairs, "_dwn", true, false, out _,
                                                                            trace: (s, d) => { if (s == "layer-order") final = d; }));
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

        // Faces of the shell with the drawn skin through their middle; the buttock: the back of the hips, below the waist.
        int faces = 0, buttock = 0;
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int a = sets.NodeOf[sets.Tris[t]], b = sets.NodeOf[sets.Tris[t + 1]], c = sets.NodeOf[sets.Tris[t + 2]];
            if (a == b || b == c || a == c || !shell[a]) continue;
            var centre = (P(a) + P(b) + P(c)) / 3f;
            if (!drawn.Deepest(centre, 0.03f, out var h) || Vector3.Dot(centre - h.Point, h.Normal) >= -0.0002f) continue;
            faces++;
            if (legs.Nearest(centre, 0.05f, out var l) && l.Normal.Z < -0.3f && l.Point.Y is >= 0.81f and < 0.98f) buttock++;
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
        return new Measured(faces, buttock, cells, BodyRetarget.FoldedTriangles(sets, final!), plan.Model);
    }

    [LocalDataFact(Geometric, Bibo, FixPubes, Rue)]
    [Trait("Plan", "AC-skirt-hips-1")]
    [Trait("Plan", "AC-skirt-hips-2")]
    [Trait("Plan", "AC-skirt-hips-3")]
    [Trait("Plan", "AC-skirt-hips-7")]
    [Trait("Plan", "EDGE-skirt-hips-13")]
    public void Rues_buttock_and_the_stocking_stay_under_Geometrics_skirt()
    {
        var now = Refit(new BodyRetarget.Tuning());
        var before = Refit(new BodyRetarget.Tuning(NoWallPair: true, NoWallCover: true));
        output.WriteLine($"shell faces with skin through: {before.FacesThrough} -> {now.FacesThrough} (buttock {before.ButtockFaces} -> {now.ButtockFaces}); "
                       + $"stocking through the shell: {before.StockingCells} -> {now.StockingCells} cells; folds {before.Folds} -> {now.Folds}");

        // As measured when the rules were made (build 1258): 28 faces and 14 cells before.
        Assert.True(before.FacesThrough >= 20 && before.StockingCells >= 10, "the harness should reproduce what showed in game");
        Assert.Equal(0, now.ButtockFaces);
        Assert.True(now.FacesThrough <= 1, $"{now.FacesThrough} shell faces with skin through");
        Assert.True(now.StockingCells <= 2, $"{now.StockingCells} cells of stocking through the shell");
        // Accepted: 21 more folds, all in the skirt's inner pieces, which lie inside the skin as its author made them.
        Assert.True(now.Folds <= before.Folds + 21, $"{now.Folds} folded, {before.Folds} before");

        // Positions only: the same model as the refit without the rules, but for where its points stand.
        var written = ModelPartReader.Read(now.Model)!;
        var old = ModelPartReader.Read(before.Model)!;
        Assert.Equal(old.MeshSpans.Count, written.MeshSpans.Count);
        Assert.Equal(old.Positions.Length, written.Positions.Length);
        Assert.Equal(old.Normals, written.Normals);
        Assert.Equal(old.Parts.Select(p => p.Material), written.Parts.Select(p => p.Material));
    }

    [LocalDataFact(Heartthrob, Scarlet, Bibo, Rue)]
    [Trait("Plan", "AC-skirt-hips-5")]
    [Trait("Plan", "EDGE-skirt-hips-12")]
    public void Hip_level_pants_are_no_worse()
    {
        var bodies = new[] { Body(Bibo), Body(Rue), Body(LocalData.Mods + ":Neolithe [ALL IN ONE]") }.ToList();
        var rue = bodies[1];
        var legsTo = rue.Catalog.For("_dwn", Race).First(o => o.Label == "None · Watermelon Crushers - A");
        foreach (var (mod, rel) in new[]
                 {
                     (Heartthrob, "bottom size/medium/chara/equipment/e6071/model/c0201e6071_dwn.mdl"),
                     (Scarlet, "pant size/neolithe m/chara/equipment/e0041/model/c0201e0041_dwn.mdl"),
                 })
        {
            var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), rel));
            var pants = ModelPartReader.Read(bytes)!;
            var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
            var pick = RefitCore.DetectSource(pants, bones, "_dwn", Race, bodies, rue.Dir, _ => true)!;
            var from = bodies.First(b => string.Equals(b.Dir, pick.Dir, StringComparison.OrdinalIgnoreCase)).Catalog;
            Assert.Null(BodyRetarget.BuildPair("_dwn", from.PathOf(pick.Option), rue.Catalog.PathOf(legsTo), "_dwn", false,
                                               null, null, null, out var pair));
            BodyRetarget.Planned Plan(BodyRetarget.Tuning t)
                => BodyRetarget.WithTuning(t, () => RefitCore.PlanRefit(pants, bytes, [pair], "_dwn", true, false, out _));
            var now = Plan(new BodyRetarget.Tuning());
            var before = Plan(new BodyRetarget.Tuning(NoWallPair: true, NoWallCover: true));
            var inNow = BodySizeMatch.ClothAgainst(ModelPartReader.Read(now.Model)!, rue.Catalog.PathOf(legsTo))!.Value;
            var inBefore = BodySizeMatch.ClothAgainst(ModelPartReader.Read(before.Model)!, rue.Catalog.PathOf(legsTo))!.Value;
            output.WriteLine($"{mod}: inside {inBefore.Inside} -> {inNow.Inside}; folds {before.Report.Folded} -> {now.Report.Folded}; "
                           + $"worst move {before.Report.WorstMove * 1000f:F1} -> {now.Report.WorstMove * 1000f:F1} mm; same model {now.Model.AsSpan().SequenceEqual(before.Model)}");
            // Accepted when the rules were made (build 1258): Heartthrob 161 → 159 inside; Scarlet 207 → 209 (of 2,711, its
            // double-walled pieces nudged by a hair), no new folds on either.
            Assert.True(inNow.Inside <= inBefore.Inside + 2, $"{inNow.Inside} inside, {inBefore.Inside} before");
            Assert.True(now.Report.Folded <= before.Report.Folded);
        }
    }
}
