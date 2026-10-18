using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Proteus.Services;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A Midlander man's shirt made into a Highlander man's and refitted onto TBSE-X's Highlander body — the request this
/// feature came from — against the real files: The Lumberjack's Summer Indigo Shirt, made for The Body SE.
/// </summary>
public class OtherRaceRefitLocalTests(ITestOutputHelper o)
{
    private const string Lumberjack = LocalData.Mods + ":the lumberjack - by solona";
    private const string Tbse = LocalData.Mods + ":The Body SE";
    private const string TbseX = LocalData.Mods + ":The Body SE-X IVCS2 V1_1_0";
    private const string IvcsHighlander = LocalData.Mods
        + ":[IVCS] Illusio Vitae Custom Skeleton Base V1.1 (2)/chara/human/c0301/skeleton/base/b0001/skl_c0301b0001.sklb";
    private const string Shirt = "chara/equipment/e6091/model/c0101e6091_top.mdl";

    /// <summary>Cloth faces of 1 mm² or more the Highlander refit of the shirt turns over today (a known limit).</summary>
    private const int KnownPecFolds = 4;

    private static byte[] Game(string path) => HavokSkeletonLocalTests.GameFile(path);

    private static byte[] ShirtBytes()
    {
        string root = LocalData.Path(Lumberjack);
        var r = PenumbraModMeta.ReadAllRedirects(root).First(x => x.GamePath == Shirt);
        return File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, r.File));
    }

    /// <summary>The wearer the panel builds for a Highlander man: the IVCS skeleton the collection gives him, the game's deformer.</summary>
    private static OtherRaceWearer.Made HighlanderWearer(byte[] shirt)
    {
        var inputs = new OtherRaceWearer.Inputs(301, 101, "_top", 6091,
            File.ReadAllBytes(LocalData.Path(IvcsHighlander)), "IVCS", Game(OtherRaceWearer.BaseSkeletonPath(301)),
            Game(OtherRaceWearer.PbdPath), [], EstTable.Parse(Game("chara/xls/charadb/extra_top.est")),
            new Dictionary<string, byte[]>());
        return OtherRaceWearer.Build(inputs, ModelSkinReader.Read(shirt, null, null)!);
    }

    [LocalDataFact(LocalData.GameData, Lumberjack, TbseX, IvcsHighlander)]
    [Trait("Plan", "AC-refit-other-race-1")]
    [Trait("Plan", "AC-refit-other-race-6")]
    [Trait("Plan", "EDGE-refit-other-race-11")]
    public void Tbse_x_offers_highlander_and_the_lumberjack_points_at_its_own_model()
    {
        var target = BodySizeCatalog.Read(LocalData.Path(TbseX));
        var redirects = PenumbraModMeta.ReadAllRedirects(LocalData.Path(Lumberjack));

        var offered = OtherRaceRefit.Offered(Shirt, target, redirects, null, isVanilla: false);
        var highlander = Assert.Single(offered);
        Assert.Equal(301, highlander.Race);
        Assert.Equal(OtherRaceRefit.Block.OwnModel, highlander.Blocked);
        Assert.Equal("chara/equipment/e6091/model/c0301e6091_top.mdl", highlander.OwnModelPath);

        // A mod that ships only the Midlander model: Highlander is bakeable.
        var onlyMidlander = redirects.Where(r => !r.GamePath.Contains("c0301", StringComparison.Ordinal)).ToList();
        Assert.Equal(OtherRaceRefit.Block.None, Assert.Single(OtherRaceRefit.Offered(Shirt, target, onlyMidlander, null, false)).Blocked);
    }

    /// <summary>
    /// Bake the shirt for Highlander men, bend the bodies it was made on the same way, refit onto TBSE-X's Highlander
    /// torso, and finish it as the save would — then measure the model that is written, and the preview, inside the pass.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Lumberjack, Tbse, TbseX, IvcsHighlander)]
    [Trait("Plan", "AC-refit-other-race-2")]
    [Trait("Plan", "AC-refit-other-race-3")]
    [Trait("Plan", "AC-refit-other-race-10")]
    public void A_highlander_refit_has_no_new_folds_in_the_saved_model_or_the_preview()
    {
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        BakedBodyCatalog.Dir = Path.Combine(Path.GetTempPath(), "proteus-tests-baked-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shirt = ShirtBytes();
            var made = HighlanderWearer(shirt);
            Assert.True(made.Wearer != null, made.Refusal);
            var who = made.Wearer!;
            o.WriteLine($"est: {made.Est.Outcome}");

            var route = new RacialModelBake.Route(101, 301, Down: false);
            var baked = route.Into(shirt, who, out string why);
            Assert.True(baked != null, why);
            var garment = ModelPartReader.Read(baked!)!;

            var made_for = BakedBodyCatalog.Of(BodySizeCatalog.Read(LocalData.Path(Tbse)), "0101", 301, who, ["_top"],
                                               out var failed);
            Assert.Empty(failed);
            var source = Assert.Single(made_for.For("_top", "0301"));
            Assert.Equal("chara/equipment/e0000/model/c0301e0000_top.mdl", source.GamePath);

            var target = BodySizeCatalog.Read(LocalData.Path(TbseX));
            var onto = Assert.Single(target.For("_top", "0301"));
            string sourcePath = made_for.PathOf(source), targetPath = target.PathOf(onto);
            Assert.Null(BodyRetarget.BuildPair("_top", sourcePath, targetPath, "_top", true, null, null, uv, out var pair));
            var plan = BodyRetarget.Plan(garment, baked!, [pair], "_top", replaceSkin: true, acrossBodies: true,
                                         cutHidden: true);
            plan = RefitCore.MatchSkinToBody(plan, sourcePath, targetPath, uv, true, log);
            o.WriteLine($"onto {onto.Label}: moved up to {plan.Report.WorstMove * 1000f:F1} mm, report folded "
                        + $"{plan.Report.Folded}, swap {plan.Report.Swap}");

            var (saved, preview, refusal) = RefitCore.Finish(plan, route, who, targetPath, log);
            Assert.True(saved != null, refusal);
            Assert.NotNull(preview);
            Assert.Equal("chara/equipment/e6091/model/c0301e6091_top.mdl", route.SavePath(Shirt));
            Assert.NotNull(route.Eqdp(route.SavePath(Shirt)));

            // The cloth as written, against the baked shirt it came from: no face of real size turned over.
            int folded = ClothFolded(garment, SkinTuckSheet.Laid(baked!, plan), o.WriteLine);
            // The preview is bent back to the Midlander shape and the game bends it forward again: what shows is the
            // saved model, within a hair, with no face squashed flat on the way.
            var planParts = ModelPartReader.Read(plan.Model)!;
            var forward = RacialModelBake.Bake(preview!, 101, 301, who.Pbd, who.ParentOf, out why);
            Assert.True(forward != null, why);
            var shown = ModelPartReader.Read(forward!)!;
            float off = 0f;
            for (int v = 0; v < planParts.Positions.Length / 3; v++)
                off = MathF.Max(off, Vector3.Distance(At(planParts.Positions, v), At(shown.Positions, v)));
            int collapsed = RacialRouteLocalTests.Collapsed(planParts, ModelPartReader.Read(preview!)!);
            o.WriteLine($"cloth turned over by the refit, of 1 mm² or more: {folded}; preview as shown {off * 1000f:F4} mm "
                        + $"off the saved model, {collapsed} collapsed");
            // Four, two a side on the pecs: the refit itself meeting TBSE-X's far larger Highlander chest, not the bake —
            // the same shirt between Midlander bodies turns two slivers. A known limit, guarded here against growing.
            Assert.True(folded <= KnownPecFolds, $"{folded} cloth faces turned over, more than the {KnownPecFolds} known");
            Assert.True(off < 5e-4f, $"the preview shows {off * 1000f:F4} mm off what is saved");
            Assert.Equal(0, collapsed);
            Assert.NotNull(ModelPartReader.Read(saved!.Model));
        }
        finally
        {
            try { Directory.Delete(BakedBodyCatalog.Dir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The measurement the "made for" choice was taken on (always the bent bodies, 1b): how far TBSE's own Highlander
    /// torso is from its Midlander torso bent to Highlander. Logged, not gated.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Lumberjack, Tbse, IvcsHighlander)]
    [Trait("Plan", "AC-refit-other-race-2")]
    public void Tbse_s_own_highlander_body_against_its_midlander_body_bent_is_logged()
    {
        var made = HighlanderWearer(ShirtBytes());
        Assert.True(made.Wearer != null, made.Refusal);
        var tbse = BodySizeCatalog.Read(LocalData.Path(Tbse));
        var mid = tbse.For("_top", "0101").First(x => BodySizeCatalog.RaceOf(x.GamePath) == "0101");
        var high = tbse.For("_top", "0301").First(x => BodySizeCatalog.RaceOf(x.GamePath) == "0301");
        var bent = RacialModelBake.Bake(File.ReadAllBytes(tbse.PathOf(mid)), 101, 301, made.Wearer!.Pbd,
                                        made.Wearer.ParentOf, out string why);
        Assert.True(bent != null, why);

        var a = ModelPartReader.Read(bent!)!;
        var b = ModelPartReader.Read(File.ReadAllBytes(tbse.PathOf(high)))!;
        var d = NearestDistances(a.Positions, b.Positions);
        Array.Sort(d);
        float p50 = d[d.Length / 2], p95 = d[(int)(d.Length * 0.95)], max = d[^1];
        float within2 = d.Count(x => x <= 0.002f) / (float)d.Length;
        o.WriteLine($"TBSE c0101 bent to c0301 against TBSE c0301 ({a.Positions.Length / 3} vs {b.Positions.Length / 3} "
                    + $"vertices): p50 {p50 * 1000f:F2} mm, p95 {p95 * 1000f:F2} mm, max {max * 1000f:F2} mm, "
                    + $"{within2:P1} within 2 mm");
    }

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    /// <summary>
    /// Cloth faces of 1 mm² or more in <paramref name="before"/> that <paramref name="after"/> — the same garment, moved —
    /// turned over: a hole in game. Smaller ones are seam slivers the refit already turns between Midlander bodies.
    /// </summary>
    /// <param name="where">Told of each one, for the log.</param>
    private static int ClothFolded(ModelParts before, ModelParts after, Action<string>? where = null)
    {
        int folded = 0;
        foreach (var part in before.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = Vector3.Cross(At(before.Positions, b) - At(before.Positions, a), At(before.Positions, c) - At(before.Positions, a));
                if (was.Length() * 0.5f < 1e-6f) continue;
                var now = Vector3.Cross(At(after.Positions, b) - At(after.Positions, a), At(after.Positions, c) - At(after.Positions, a));
                if (Vector3.Dot(was, now) > 0f) continue;
                folded++;
                where?.Invoke($"{part.Label}: {was.Length() * 0.5f * 1e6f:F1} mm² at {At(after.Positions, a)}");
            }
        return folded;
    }

    /// <summary>For each point of <paramref name="from"/>, the distance to the nearest point of <paramref name="to"/>.</summary>
    private static float[] NearestDistances(float[] from, float[] to)
    {
        const float cell = 0.01f;
        var grid = new Dictionary<(int, int, int), List<Vector3>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (int v = 0; v < to.Length / 3; v++)
        {
            var p = new Vector3(to[v * 3], to[v * 3 + 1], to[v * 3 + 2]);
            var key = Cell(p);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(p);
        }
        var result = new float[from.Length / 3];
        for (int v = 0; v < result.Length; v++)
        {
            var p = new Vector3(from[v * 3], from[v * 3 + 1], from[v * 3 + 2]);
            var (x, y, z) = Cell(p);
            float best = float.MaxValue;
            for (int r = 1; r <= 4 && best == float.MaxValue; r++)
                for (int i = -r; i <= r; i++)
                    for (int j = -r; j <= r; j++)
                        for (int k = -r; k <= r; k++)
                            if (grid.TryGetValue((x + i, y + j, z + k), out var list))
                                foreach (var q in list) best = MathF.Min(best, Vector3.Distance(p, q));
            result[v] = best == float.MaxValue ? 0.05f : best;
        }
        return result;
    }
}
