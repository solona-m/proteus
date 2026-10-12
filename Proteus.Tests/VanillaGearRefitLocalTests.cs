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
[Collection(GameBodyCollection.Name)]
public class VanillaGearRefitLocalTests(ITestOutputHelper output)
{
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";

    private const string Maiming = "Neo-Ishgardian Top of Maiming";
    private const string Scion = "Scion Adventurer's";

    public static TheoryData<string, string> NeolitheCases => new()
    {
        { Maiming, "neobelly chest - smallclothes/sfw m.mdl" },
        { Maiming, "default chest - smallclothes/sfw xs.mdl" },
        { Scion, "neobelly chest - smallclothes/sfw m.mdl" },
        { Scion, "default chest - smallclothes/sfw xs.mdl" },
    };

    [LocalDataTheory(LocalData.GameData, Neolithe)]
    [MemberData(nameof(NeolitheCases))]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-1")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-2")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-3")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-4")]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-6")]
    public void A_game_top_on_Neolithe_shows_no_skin_through_and_no_holes(string garment, string rel)
        => Check(garment, Neolithe, rel);

    [LocalDataTheory(LocalData.GameData, Bibo)]
    [InlineData(Maiming)]
    [InlineData(Scion)]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-6")]
    public void A_game_top_on_Bibo_shows_no_skin_through_and_no_holes(string garment) => Check(garment, Bibo, null);

    [LocalDataTheory(LocalData.GameData, Rue)]
    [InlineData(Maiming)]
    [InlineData(Scion)]
    [Trait("Plan", "AC-vanilla-refit-cut-skin-6")]
    public void A_game_top_on_Rue_shows_no_skin_through_and_no_holes(string garment) => Check(garment, Rue, null);

    /// <param name="rel">The chest option of the body mod, or null for its first.</param>
    private void Check(string garmentName, string modNeed, string? rel)
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

        byte[] Drawn(BodyRetarget.Planned plan) => RefitCore.MatchSkinToBody(plan.Model, source, target, uv, false, log);
        var game = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: true, out _);
        var whole = RefitCore.PlanRefit(garment, bytes, [pair], "_top", across: true, gameGear: false, out _);
        var cutOnly = BodyRetarget.Plan(garment, bytes, [pair], "_top", held: new HashSet<int>(), replaceSkin: true,
                                        acrossBodies: true, clearBody: false, cutHidden: true,
                                        keepShape: RefitCore.ShapePieces(garment, RefitCore.DefaultKeepShape(garment)));

        var gameModel = ModelPartReader.Read(Drawn(game))!;
        var body = ModelPartReader.Read(File.ReadAllBytes(target))!;
        double throughGame = SkinThrough(gameModel);
        double throughWhole = SkinThrough(ModelPartReader.Read(Drawn(whole))!);
        double throughCut = SkinThrough(ModelPartReader.Read(Drawn(cutOnly))!);
        var holeBands = new SortedDictionary<(float, string), double>();
        var baseBands = new SortedDictionary<(float, string), double>();
        double holes = UncoveredUndrawn(gameModel, body, holeBands);
        double baseline = UncoveredUndrawn(garment, ModelPartReader.Read(File.ReadAllBytes(source))!, baseBands);

        output.WriteLine($"RAN {garmentName} on {Path.GetFileName(LocalData.Path(modNeed))} / {option.Rel}");
        output.WriteLine($"  skin through cloth: whole {throughWhole:0.0} cm², cut {throughCut:0.0}, cut + cleared {throughGame:0.0}");
        output.WriteLine($"  pushed: cut {cutOnly.Report.Pushed} (worst move {cutOnly.Report.WorstMove * 1000:0.0} mm), "
                         + $"cut + cleared {game.Report.Pushed} (worst move {game.Report.WorstMove * 1000:0.0} mm)");
        output.WriteLine($"  uncovered torso skin left undrawn:{holes:0.0} cm² (game's own on its body {baseline:0.0})");
        foreach (var key in holeBands.Keys.Union(baseBands.Keys).OrderByDescending(k => k))
            output.WriteLine($"    y {key.Item1:0.0} {key.Item2}: {holeBands.GetValueOrDefault(key):0.0} cm² "
                             + $"(game's own {baseBands.GetValueOrDefault(key):0.0})");
        output.WriteLine($"  folded: game {game.Report.Folded}, whole {whole.Report.Folded}; swap {game.Report.Swap}");

        // AC-1: on the reported top and body the cut takes most of what showed through (679 → 193 cm²); on any other at
        // least half — a smaller body shows less to start with (Rue+ 529 → 220, Neolithe XS 487 → 198).
        bool reported = garmentName == Maiming && modNeed == Neolithe && rel == "neobelly chest - smallclothes/sfw m.mdl";
        double share = reported ? 0.4 : 0.5;
        Assert.True(throughGame <= share * throughWhole, $"{throughGame:0.0} cm² of {throughWhole:0.0} still shows");
        // AC-2: clearing the body on top of the cut shows no more skin — rounding aside — and moves the cloth not much
        // further.
        Assert.True(throughGame <= throughCut + 0.5, $"cleared {throughGame:0.0} cm² against cut {throughCut:0.0}");
        Assert.True(game.Report.Pushed <= 2 * cutOnly.Report.Pushed + 50,
                    $"pushed {game.Report.Pushed} against {cutOnly.Report.Pushed}");
        Assert.True(game.Report.WorstMove <= cutOnly.Report.WorstMove + 0.008f,
                    $"moved {game.Report.WorstMove * 1000:0.0} mm against {cutOnly.Report.WorstMove * 1000:0.0}");
        // AC-3: the cut opens no holes the game's own top does not have. The margin is what was checked by eye: the Scion
        // top's jacket hangs further off neobelly SFW M's bust than any ray reaches, 34 cm² under it, closed over by the
        // cloth (49 cm² in all against the game's 14). Every Top of Maiming case comes in far below its own body's 436.
        Assert.True(holes <= Math.Max(1.5 * baseline, baseline + 40.0), $"{holes:0.0} cm² undrawn against {baseline:0.0}");
        // AC-4
        Assert.True(game.Report.Folded <= whole.Report.Folded, $"folded {game.Report.Folded} against {whole.Report.Folded}");
    }

    /// <summary>The c0201 body model of the first game item named <paramref name="name"/> worn on the body.</summary>
    private static byte[] TopOf(Lumina.GameData data, string name)
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
    private static double SkinThrough(ModelParts m)
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
                    if (s < -0.0005f && s > -0.015f) inside++;
                }
                area += Vector3.Cross(b - a, c - a).Length() / 2 * inside / n;
            }
        return area * 1e4;
    }

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

    /// <summary>How far off the skin cloth still covers it, m.</summary>
    private const float Reach = 0.06f;

    /// <summary>Skin further than this from the midline, m, is the arms.</summary>
    private const float ArmFrom = 0.12f;
}
