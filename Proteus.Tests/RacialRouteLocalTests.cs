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
[Trait("Speed", "Slow")]
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
        // Normals too: Bake and Unbake rewrite them, and a refit never touches them otherwise.
        float turned = 0f;
        for (int v = 0; v < a.Normals.Length / 3; v++)
        {
            var na = At(a.Normals, v);
            var nb = At(b.Normals, v);
            if (na == Vector3.Zero || nb == Vector3.Zero) continue;
            float cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(na), Vector3.Normalize(nb)), -1f, 1f);
            turned = MathF.Max(turned, MathF.Acos(cos) * 180f / MathF.PI);
        }
        int flipped = Flipped(a, b), collapsed = Collapsed(a, b);
        o.WriteLine($"{path}: bent down up to {moved * 1000f:F1} mm; back within {worst * 1000f:F4} mm, normals "
                    + $"{turned:F3}°; {flipped} flipped, {collapsed} collapsed");
        Assert.True(moved > 1e-3f, "bending an Au Ra model into the Midlander shape should move it");
        Assert.True(worst < 5e-4f, $"the round trip is {worst * 1000f:F4} mm off");
        Assert.True(turned < 0.5f, $"a normal came back turned {turned:F3}°");
        Assert.Equal(0, flipped);
        Assert.Equal(0, collapsed);

        var s = SecondSkinWriter.Parse(model);
        var t = SecondSkinWriter.Parse(back!);
        Assert.Equal(model.Length, back!.Length);
        Assert.Equal(s.MeshCount, t.MeshCount);
        Assert.Equal(s.Shapes.Count, t.Shapes.Count);
        Assert.Equal(s.BoneNames, t.BoneNames);
        Assert.Equal(s.MatNames, t.MatNames);
    }

    /// <summary>
    /// Shape keys go through the bend too: the Auri set carries one, and it comes back where it was.
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-racial-gear-upscale-12")]
    public void The_auri_set_s_shape_keys_go_down_and_back()
    {
        var data = Data();
        var who = Wearer(data);
        var shaped = new[] { "dwn", "sho", "top", "glv" }
            .Select(slot => $"chara/equipment/e0258/model/c1401e0258_{slot}.mdl")
            .Where(data.FileExists)
            .Select(p => (Path: p, Model: data.GetFile(p)!.Data))
            .FirstOrDefault(m => SecondSkinWriter.Parse(m.Model).Shapes.Count > 0);
        Assert.True(shaped.Model != null, "no piece of the Auri set carries a shape key");

        var down = Down.Into(shaped.Model!, who, out string why);
        Assert.True(down != null, why);
        var back = Down.Back(down!, who, out why);
        Assert.True(back != null, why);
        Assert.Equal(SecondSkinWriter.Parse(shaped.Model!).Shapes.Keys.OrderBy(k => k),
                     SecondSkinWriter.Parse(back!).Shapes.Keys.OrderBy(k => k));
        o.WriteLine($"{shaped.Path}: {string.Join(", ", SecondSkinWriter.Parse(back!).Shapes.Keys)}");
    }

    /// <summary>
    /// The premise of bending down: the game makes its racial models by bending the shared shape, so the garment's skin,
    /// bent back, lies on the game's own body of that shape — what the skin swap needs. Au Ra women's and men's both.
    /// </summary>
    [LocalDataTheory(LocalData.GameData)]
    [InlineData("e0258", 1401, 201)]   // Auri, the women's
    [InlineData("e0257", 1301, 101)]   // and the men's
    [Trait("Plan", "AC-racial-gear-upscale-1")]
    [Trait("Plan", "EDGE-racial-gear-upscale-1")]
    public void Racial_skin_bent_down_lies_on_the_game_s_shared_body(string set, int drawn, int shared)
    {
        var data = Data();
        var who = Wearer(data) with { Race = (ushort)drawn };

        // The first piece of the set that draws some skin, against the game's body of its slot.
        var piece = new[] { "_dwn", "_top", "_glv", "_sho" }
            .Select(slot => (Slot: slot, Path: $"chara/equipment/{set}/model/c{drawn:D4}{set}{slot}.mdl"))
            .Where(p => data.FileExists(p.Path))
            .Select(p => (p.Slot, p.Path, Model: data.GetFile(p.Path)!.Data))
            .FirstOrDefault(p => SkinOf(ModelPartReader.Read(p.Model)!).Length > 0);
        Assert.True(piece.Model != null, $"no piece of {set} draws skin");
        string path = piece.Path;
        Assert.False(data.FileExists(RacialModelBake.WithRace(path, (ushort)shared)), "the set has a shared model");
        var route = RefitCore.Route(path, (ushort)drawn, _ => false, _ => false, [], null);
        Assert.Equal(new RacialModelBake.Route((ushort)drawn, (ushort)shared, Down: true), route);

        var model = piece.Model!;
        var down = route.Into(model, who, out string why);
        Assert.True(down != null, why);
        string code = $"{shared:D4}";
        var vanilla = VanillaBodyCatalog.Read(code, p => data.GetFile(p)?.Data);
        var body = ModelPartReader.Read(File.ReadAllBytes(vanilla.PathOf(vanilla.For(piece.Slot, code)[0])))!;
        var bodyTriangles = body.Parts.Where(p => p.Island == -1).SelectMany(p => p.Triangles.Chunk(3))
                                .Select(t => (At(body.Positions, t[0]), At(body.Positions, t[1]), At(body.Positions, t[2])))
                                .ToArray();

        // As the skin swap judges it: the share of the skin within SwapOnBody of the body's surface.
        float Share(byte[] mdl)
        {
            var parts = ModelPartReader.Read(mdl)!;
            var skin = SkinOf(parts);
            return skin.Count(v => bodyTriangles.Min(t => DistanceToTriangle(At(parts.Positions, v), t))
                                   <= BodyRetarget.SwapOnBody)
                   / (float)skin.Length;
        }
        float drawnShare = Share(model), downShare = Share(down!);
        o.WriteLine($"{path}: skin within {BodyRetarget.SwapOnBody * 1000f} mm of the c{code} body — drawn "
                    + $"{drawnShare:P1}, bent down {downShare:P1}");
        Assert.True(downShare >= BodyRetarget.OnBodyShare,
                    $"only {downShare:P1} of the skin lies on the game's c{code} body: it would not be swapped");
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
                    + $"{Flipped(planParts, savedParts, 1e-6f)} of 1 mm² or more; {Collapsed(planParts, savedParts)} collapsed");
        Assert.Equal(0, Flipped(planParts, savedParts, 1e-6f));
        Assert.Equal(0, Collapsed(planParts, savedParts));
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

        // Nor without the character to bend it back by.
        var bent = KeepEnds.SkinOnlyPlanned(Down.Into(data.GetFile(Auri)!.Data, who, out why)!);
        (saved, _, refusal) = RefitCore.Finish(bent, Down, null, "", log);
        Assert.Null(saved);
        Assert.Contains(Proteus.Localization.Strings.Parts.RetargetBakeNoWearer, refusal);
    }

    /// <summary>
    /// The bake up, moved into <see cref="RefitCore.Finish"/>, as it was: the preview bent back from the model BEFORE its
    /// skin is renamed to the body's, the saved model renamed, and its save carrying the EQDP entry.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Bibo)]
    [Trait("Plan", "AC-racial-gear-upscale-8")]
    public void A_man_s_top_baked_up_finishes_as_before()
    {
        var data = Data();
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var who = Wearer(data) with { Race = 201 };
        // The game's own men's chest: all skin, which the rename has to draw with Bibo+'s.
        const string top = "chara/equipment/e0000/model/c0101e0000_top.mdl";
        var up = RefitCore.Route(top, 201, c => c == 201, c => c == 201, [], null);
        Assert.Equal(new RacialModelBake.Route(101, 201, Down: false), up);

        var baked = up.Into(data.GetFile(top)!.Data, who, out string why);
        Assert.True(baked != null, why);
        // Bibo+'s chest: its skin material is not the man's, so the rename has something to do and the order counts.
        var mod = BodySizeCatalog.Read(LocalData.Path(Bibo));
        string target = mod.PathOf(mod.For("_top", "0201")[0]);
        var plan = KeepEnds.SkinOnlyPlanned(baked!);

        var (saved, preview, refusal) = RefitCore.Finish(plan, up, who, target, log);
        Assert.True(saved != null, refusal);
        Assert.False(saved!.Model.AsSpan().SequenceEqual(plan.Model), "the kept skin should take the body's material");
        Assert.Equal(RacialModelBake.Unbake(plan.Model, 101, 201, who.Pbd, who.ParentOf, out _), preview);
        Assert.NotEqual(RacialModelBake.Unbake(saved.Model, 101, 201, who.Pbd, who.ParentOf, out _), preview);
        Assert.Equal(RefitCore.WithBodySkin(plan, target, log).Model, saved.Model);
        Assert.NotNull(up.Eqdp(up.SavePath(top)));
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
    internal static int Flipped(ModelParts a, ModelParts b, float minArea = 1e-14f)
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

    /// <summary>How far <paramref name="p"/> lies from triangle <paramref name="t"/> (Ericson, Real-Time Collision
    /// Detection 5.1.5: the closest point by Voronoi region).</summary>
    private static float DistanceToTriangle(Vector3 p, (Vector3 A, Vector3 B, Vector3 C) t)
    {
        var (a, b, c) = t;
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.Distance(p, a);
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.Distance(p, b);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.Distance(p, c);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6))));
        float denom = 1f / (va + vb + vc);
        return Vector3.Distance(p, a + ab * (vb * denom) + ac * (vc * denom));
    }

    /// <summary>The vertices of a model's body-skin meshes.</summary>
    private static int[] SkinOf(ModelParts parts)
        => parts.Parts.Where(p => p.Island == -1 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                .SelectMany(p => p.Triangles).Distinct().ToArray();

    /// <summary>
    /// Triangles of real size in <paramref name="a"/> (0.01 mm² or more) that <paramref name="b"/> squashes to nothing —
    /// which <see cref="Flipped"/> cannot see, having no face left to compare.
    /// </summary>
    internal static int Collapsed(ModelParts a, ModelParts b)
    {
        int collapsed = 0;
        foreach (var part in a.Parts.Where(p => p.Island == -1))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int i = part.Triangles[t], j = part.Triangles[t + 1], k = part.Triangles[t + 2];
                var na = Vector3.Cross(At(a.Positions, j) - At(a.Positions, i), At(a.Positions, k) - At(a.Positions, i));
                var nb = Vector3.Cross(At(b.Positions, j) - At(b.Positions, i), At(b.Positions, k) - At(b.Positions, i));
                if (na.Length() * 0.5f >= 1e-8f && nb.Length() * 0.5f < 1e-11f) collapsed++;
            }
        return collapsed;
    }
}
