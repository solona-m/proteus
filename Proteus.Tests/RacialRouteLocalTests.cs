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
/// Racial-only gear — Au Ra's Auri Pantalettes, which the game ships for Au Ra alone — refitted through the shared
/// Midlander shape, against the game's own files and a body mod's.
/// </summary>
public class RacialRouteLocalTests(ITestOutputHelper o)
{
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Auri = "chara/equipment/e0258/model/c1401e0258_dwn.mdl";

    private static readonly RacialModelBake.Route Down = new(1401, 201, Down: true);

    private static Lumina.GameData Data() => new(LocalData.Path(LocalData.GameData));

    private static RacialModelBake.Wearer Wearer(Lumina.GameData data)
        => new(1401, new Dictionary<string, string?>(),
               new PbdFile(data.GetFile("chara/xls/boneDeformer/human.pbd")!.Data));

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-racial-gear-upscale-1")]
    public void Auri_pantalettes_have_no_midlander_model_and_the_game_no_au_ra_body()
    {
        var data = Data();
        Assert.True(data.FileExists(Auri));
        Assert.False(data.FileExists("chara/equipment/e0258/model/c0101e0258_dwn.mdl"));
        Assert.False(data.FileExists("chara/equipment/e0258/model/c0201e0258_dwn.mdl"));

        // What "Made for" listed: nothing, for Au Ra — and the game's Midlander woman once the garment is bent down.
        Assert.Empty(VanillaBodyCatalog.Read("1401", p => data.GetFile(p)?.Data).For("_dwn", "1401"));
        Assert.NotEmpty(VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data).For("_dwn", "0201"));
        Assert.NotEmpty(Wearer(data).Pbd.Chain(201, 1401));
    }

    [LocalDataTheory(LocalData.GameData)]
    [InlineData("chara/equipment/e0258/model/c1401e0258_dwn.mdl")]
    [InlineData("chara/equipment/e0258/model/c1401e0258_sho.mdl")]
    [InlineData("chara/equipment/e0258/model/c1401e0258_top.mdl")]
    [Trait("Plan", "AC-racial-gear-upscale-5")]
    [Trait("Plan", "EDGE-racial-gear-upscale-12")]
    [Trait("Plan", "EDGE-racial-gear-upscale-16")]
    public void Shaping_down_and_back_gives_the_model_back(string path)
    {
        var data = Data();
        var who = Wearer(data);
        var model = data.GetFile(path)!.Data;
        var down = Down.Into(model, who, out string why);
        Assert.True(down != null, why);
        var back = Down.Back(down!, who, out why);
        Assert.True(back != null, why);

        var a = ModelPartReader.Read(model)!;
        var d = ModelPartReader.Read(down!)!;
        var b = ModelPartReader.Read(back!)!;
        float worst = 0f, moved = 0f;
        for (int v = 0; v < a.Positions.Length / 3; v++)
        {
            worst = MathF.Max(worst, Vector3.Distance(At(a.Positions, v), At(b.Positions, v)));
            moved = MathF.Max(moved, Vector3.Distance(At(a.Positions, v), At(d.Positions, v)));
        }
        int flipped = Flipped(a, b);
        o.WriteLine($"{path}: bent down up to {moved * 1000f:F1} mm; back within {worst * 1000f:F4} mm; {flipped} flipped");
        Assert.True(moved > 1e-3f, "bending an Au Ra model into the Midlander shape should move it");
        Assert.True(worst < 5e-4f, $"the round trip is {worst * 1000f:F4} mm off");
        Assert.Equal(0, flipped);

        var s = SecondSkinWriter.Parse(model);
        var t = SecondSkinWriter.Parse(back!);
        Assert.Equal(model.Length, back!.Length);
        Assert.Equal(s.MeshCount, t.MeshCount);
        Assert.Equal(s.Shapes.Count, t.Shapes.Count);
        Assert.Equal(s.BoneNames, t.BoneNames);
        Assert.Equal(s.MatNames, t.MatNames);
    }

    /// <summary>
    /// [AC-racial-gear-upscale-3] [AC-racial-gear-upscale-4] [AC-racial-gear-upscale-5] The report's own refit: Auri
    /// Pantalettes from the game's body onto Bibo+, as the Studio and the automatic refit both make it — measured on the
    /// plan, never an exported model.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo)]
    [Trait("Plan", "AC-racial-gear-upscale-3")]
    [Trait("Plan", "AC-racial-gear-upscale-4")]
    [Trait("Plan", "AC-racial-gear-upscale-5")]
    [Trait("Plan", "AC-racial-gear-upscale-6")]
    [Trait("Plan", "EDGE-racial-gear-upscale-10")]
    public void Pantalettes_refit_onto_bibo_land_on_her_drawn_body()
    {
        var data = Data();
        var who = Wearer(data);
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        var mod = BodySizeCatalog.Read(LocalData.Path(Bibo));
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);

        // The route as the automatic refit decides it for the game's own gear: Bibo+ has no Au Ra bodies, nor the game.
        var targetRaces = RefitCore.RacesOf(mod);
        Assert.DoesNotContain("1401", targetRaces);
        var gameRaces = new[] { "1401", "0201" }
            .Where(c => VanillaBodyCatalog.Read(c, p => data.GetFile(p)?.Data).IsBody).ToHashSet();
        var route = RefitCore.Route(Auri, 1401, c => targetRaces.Contains($"{c:D4}"),
                                    c => targetRaces.Contains($"{c:D4}") && gameRaces.Contains($"{c:D4}"), [], null);
        Assert.Equal(Down, route);
        Assert.Equal(Auri, route.SavePath(Auri));
        Assert.Null(route.Eqdp(route.SavePath(Auri)));

        var drawn = data.GetFile(Auri)!.Data;
        var bytes = route.Into(drawn, who, out string why);
        Assert.True(bytes != null, why);
        var garment = ModelPartReader.Read(bytes!)!;

        string source = vanilla.PathOf(vanilla.For("_dwn", "0201")[0]);
        var option = mod.For("_dwn", "0201").Last();
        string target = mod.PathOf(option);
        Assert.Null(BodyRetarget.BuildPair("_dwn", source, target, "_dwn", false, null, null, uv, out var pair));
        var plan = RefitCore.PlanRefit(garment, bytes!, [pair], "_dwn", across: true, gameGear: true, out _);
        plan = RefitCore.MatchSkinToBody(plan, source, target, uv, false, log);
        o.WriteLine($"onto {option.Label}: moved up to {plan.Report.WorstMove * 1000f:F1} mm, {plan.Report.Folded} folded; "
                    + $"swap {plan.Report.Swap}");
        Assert.True(plan.Report.WorstMove > 1e-3f, "a refit onto a larger body should move the cloth");
        // The garment's skin, bent down, lies on the game's body: the legs are the new body's, not the Au Ra ones.
        Assert.True(plan.Report.Swap is { Added: > 0 }, $"no skin was swapped: {plan.Report.Swap}");

        var (saved, preview, refusal) = RefitCore.Finish(plan, route, who, target, log);
        Assert.True(saved != null, refusal);
        Assert.Null(preview);   // pushed as it is: the saved model is already the drawn race's
        Assert.Equal(SecondSkinWriter.Parse(plan.Model).MatNames, SecondSkinWriter.Parse(saved!.Model).MatNames);
        // Where the game draws Bibo+'s legs on her: the body bent into Au Ra by its own weights.
        var bodyDrawn = RacialModelBake.Bake(File.ReadAllBytes(target), 201, 1401, who.Pbd, who.ParentOf, out why);
        Assert.True(bodyDrawn != null, why);
        // No face of real size turned over by the bend back. A sliver may: Bibo+'s waist edge, cut to the cloth, has one of
        // 0.2 mm² and 15.6 mm long that the bend opens backwards — as the game's own bend would for this refit had it been
        // a Midlander model worn by an Au Ra.
        var planParts = ModelPartReader.Read(plan.Model)!;
        var savedParts = ModelPartReader.Read(saved.Model)!;
        o.WriteLine($"bent back: {Flipped(planParts, savedParts)} faces turned over, "
                    + $"{Flipped(planParts, savedParts, 1e-6f)} of 1 mm² or more");
        Assert.Equal(0, Flipped(planParts, savedParts, 1e-6f));
        var body = ModelPartReader.Read(bodyDrawn!)!;
        var result = ModelPartReader.Read(saved.Model)!;
        var skin = result.Parts.Where(p => SecondSkinWriter.IsBodySkinMaterial(p.Material))
                         .SelectMany(p => p.Triangles).Distinct().ToArray();
        Assert.NotEmpty(skin);
        var bodyPoints = Enumerable.Range(0, body.Positions.Length / 3).Select(v => At(body.Positions, v)).ToArray();
        int on = skin.Count(v => bodyPoints.Min(q => Vector3.DistanceSquared(q, At(result.Positions, v))) < 1e-6f);
        o.WriteLine($"{on} of {skin.Length} skin vertices within 1 mm of Bibo+ drawn on an Au Ra");
        Assert.True(on >= skin.Length * 0.95f, $"only {on} of {skin.Length} skin vertices lie on her drawn body");

        // [EDGE-racial-gear-upscale-10] Opened again, the saved refit is bent down the same way and reads as the size
        // it was refitted onto.
        var again = route.Into(saved.Model, who, out why);
        Assert.True(again != null, why);
        var ranked = BodySizeMatch.Rank(ModelPartReader.Read(again!)!, mod.For("_dwn", "0201"), mod.PathOf);
        o.WriteLine($"read again: {ranked.Best?.Option.Label} ({ranked.Confidence})");
        foreach (var s in ranked.Scores.Take(5)) o.WriteLine($"  {s.Option.Label}: hit {s.HitRate:F4} rms {s.Rms * 1000f:F3} mm");
        // Bibo+'s Types A, B and C of a size share their legs, and tie: the size refitted onto is among the best.
        var best = ranked.Best!.Value;
        Assert.Contains(ranked.Scores, s => s.Option.Rel == option.Rel && s.HitRate == best.HitRate
                                            && MathF.Abs(s.Rms - best.Rms) < 1e-5f);
        Assert.True(best.Rms < 1e-4f, $"read again {best.Rms * 1000f:F3} mm off its size");
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-racial-gear-upscale-7")]
    public void A_model_that_cannot_be_bent_is_refused_not_saved()
    {
        var data = Data();
        var who = Wearer(data);
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();

        // No way through the deformer between two races neither of which falls to the other.
        var nowhere = new RacialModelBake.Route(1401, 1301, Down: true);
        Assert.Null(nowhere.Into(data.GetFile(Auri)!.Data, who, out string why));
        Assert.Contains("c1301", why);

        // A refit that cannot be read back is never saved in the shared shape.
        var unreadable = KeepEnds.SkinOnlyPlanned(new byte[64]);
        var (saved, _, refusal) = RefitCore.Finish(unreadable, Down, who, "", log);
        Assert.Null(saved);
        Assert.Contains(ModelRace.Describe("1401"), refusal);
    }

    /// <summary>
    /// The bake up, moved into <see cref="RefitCore.Finish"/>, as it was: the preview bent back from the model BEFORE its
    /// skin is renamed to the body's, the saved model renamed, and its save carrying the EQDP entry.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "AC-racial-gear-upscale-8")]
    public void A_man_s_boots_baked_up_finish_as_before()
    {
        var data = Data();
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var who = Wearer(data) with { Race = 201 };
        string boots = new[] { "e0100", "e0005", "e0036", "e0001" }
            .Select(set => $"chara/equipment/{set}/model/c0101{set}_sho.mdl").First(data.FileExists);
        var up = RefitCore.Route(boots, 201, c => c == 201, c => c == 201, [], null);
        Assert.Equal(new RacialModelBake.Route(101, 201, Down: false), up);

        var baked = up.Into(data.GetFile(boots)!.Data, who, out string why);
        Assert.True(baked != null, why);
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string target = vanilla.PathOf(vanilla.For("_sho", "0201")[0]);
        var plan = KeepEnds.SkinOnlyPlanned(baked!);

        var (saved, preview, refusal) = RefitCore.Finish(plan, up, who, target, log);
        Assert.True(saved != null, refusal);
        Assert.Equal(RacialModelBake.Unbake(plan.Model, 101, 201, who.Pbd, who.ParentOf, out _), preview);
        Assert.Equal(RefitCore.WithBodySkin(plan, target, log).Model, saved!.Model);
        Assert.NotNull(up.Eqdp(up.SavePath(boots)));
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-racial-gear-upscale-9")]
    public void A_racial_shoe_bent_down_keeps_its_feet_readable()
    {
        var data = Data();
        var who = Wearer(data);
        var shoe = data.GetFile("chara/equipment/e0258/model/c1401e0258_sho.mdl")!.Data;
        var down = Down.Into(shoe, who, out string why);
        Assert.True(down != null, why);
        var reading = KeepEnds.Read("_sho", ModelPartReader.Read(down!)!, down!);
        Assert.NotNull(reading);
        o.WriteLine($"{reading!.Ends.Count} of {reading.Vertices} vertices kept as the feet");
        Assert.NotEmpty(reading.Ends);
    }

    /// <summary>
    /// Triangles of <paramref name="a"/> whose face turned over in <paramref name="b"/> — a game "hole" — of at least
    /// <paramref name="minArea"/> m² in <paramref name="a"/>. Whole submeshes only: a part list names each island again.
    /// </summary>
    private static int Flipped(ModelParts a, ModelParts b, float minArea = 1e-14f)
        => a.Parts.Where(p => p.Island == -1).Sum(part => Flipped(a, b, part, minArea));

    private static int Flipped(ModelParts a, ModelParts b, ModelPart part, float minArea = 1e-14f)
    {
        int flipped = 0;
        for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
        {
            int i = part.Triangles[t], j = part.Triangles[t + 1], k = part.Triangles[t + 2];
            var na = Vector3.Cross(At(a.Positions, j) - At(a.Positions, i), At(a.Positions, k) - At(a.Positions, i));
            var nb = Vector3.Cross(At(b.Positions, j) - At(b.Positions, i), At(b.Positions, k) - At(b.Positions, i));
            if (na.Length() * 0.5f >= minArea && nb.LengthSquared() > 1e-14f && Vector3.Dot(na, nb) < 0) flipped++;
        }
        return flipped;
    }
}
