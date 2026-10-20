using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// "Move skin too" on real garments and bodies, from <see cref="LocalData"/>: a YAB WC shoe with stockings, a game-body
/// top with a bibo skin patch, a top in its Rue and Neolithe sizes, a Neolithe chest, and a long dress for the cost.
/// Measured inside the solve — never on an exported model.
/// </summary>
public class SkinMoveLocalTests(ITestOutputHelper output)
{
    private const string ThisOldThing = LocalData.Mods + ":This Old Thing - by Solona";
    private const string RueL = ThisOldThing + "/size/rue l/chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string RueM = ThisOldThing + "/size/rue m/chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string NeolitheL = ThisOldThing + "/size/neolithe l/chara/equipment/e6255/model/c0201e6255_top.mdl";
    private const string NeolitheChest = LocalData.Mods + ":Neolithe [ALL IN ONE]/DEFAULT CHEST - SmallClothes/SFW M.mdl";
    private const string Dress = LocalData.Mods
        + ":I Just Really Like Sweater Dresses, OK - by Solona/size/medium/chara/equipment/e6057/model/c0201e6057_top.mdl";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";

    private static ModelParts Read(byte[] bytes) => ModelPartReader.Read(bytes) ?? throw new InvalidDataException("unreadable model");

    private static ModelParts ReadFile(string need) => Read(File.ReadAllBytes(LocalData.Path(need)));

    private static MeshVolumeSolve SkinSolve(ModelParts model)
    {
        var solve = new MeshVolumeSolve(model) { MoveSkin = true };
        Assert.True(solve.AttachFollow(SkinFollow.Bind(solve.FollowInputs)));
        return solve;
    }

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    private static List<int> SkinVertices(ModelParts model)
        => model.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                      .SelectMany(p => p.Triangles).Distinct().ToList();

    /// <summary>
    /// A skin vertex well inside the skin — away from its open edges — at the front of the middle third of its height:
    /// somewhere a sculpt would really be made.
    /// </summary>
    private static int Spot(ModelParts model, MeshVolumeSolve solve)
    {
        var skin = SkinVertices(model).Where(v => solve.SeamWeightAt(v) >= 1f).ToList();
        Assert.NotEmpty(skin);
        float lo = skin.Min(v => At(model.Positions, v).Y), hi = skin.Max(v => At(model.Positions, v).Y);
        var band = skin.Where(v => At(model.Positions, v).Y is var y && y > lo + (hi - lo) / 3f && y < hi - (hi - lo) / 3f).ToList();
        return (band.Count > 0 ? band : skin).MaxBy(v => At(model.Positions, v).Z);
    }

    /// <summary>The model as the solve has it now, for the measures that read a model.</summary>
    private static ModelParts Deformed(ModelParts model, MeshVolumeSolve solve)
    {
        int vc = model.Positions.Length / 3;
        var nrm = new float[vc * 3];
        for (int v = 0; v < vc; v++)
        {
            var n = solve.NormalAt(v);
            nrm[v * 3] = n.X; nrm[v * 3 + 1] = n.Y; nrm[v * 3 + 2] = n.Z;
        }
        return new ModelParts
        {
            Positions = solve.Positions().ToArray(), Normals = nrm, MeshSpans = model.MeshSpans, Parts = model.Parts,
            AttributeNames = model.AttributeNames, Min = model.Min, Max = model.Max, ShatteredSubmeshes = model.ShatteredSubmeshes,
        };
    }

    /// <summary>Triangles that turned over against the author's.</summary>
    private static int Folded(ModelParts model, float[] after)
    {
        int folded = 0;
        foreach (var part in model.Parts.Where(p => p.Island < 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = Vector3.Cross(At(model.Positions, b) - At(model.Positions, a), At(model.Positions, c) - At(model.Positions, a));
                var now = Vector3.Cross(At(after, b) - At(after, a), At(after, c) - At(after, a));
                if (was.LengthSquared() > 1e-16f && Vector3.Dot(was, now) <= 0f) folded++;
            }
        return folded;
    }

    /// <summary>
    /// Inflate the skin 3 mm at <see cref="Spot"/> and check what the game would show: the skin moved, no more skin
    /// through the cloth than the author left, and nothing turned over.
    /// </summary>
    private MeshVolumeSolve Sculpt(ModelParts model, string name, float strength = 0.003f)
    {
        var solve = SkinSolve(model);
        int spot = Spot(model, solve);
        int authoredThrough = SkinThroughCloth.Find(model).Outside.Count;

        Assert.True(solve.Paint(At(model.Positions, spot), 0.06f, strength) > 0);
        solve.EndStroke();

        var after = solve.Positions();
        float went = (At(after, spot) - At(model.Positions, spot)).Length();
        int through = SkinThroughCloth.Find(Deformed(model, solve)).Outside.Count;
        int folded = Folded(model, after);
        var which = new List<(int Vertex, float Was, float Now)>();
        int worse = SkinPoke.MadeWorse(model, after, which);
        int uncovered = SkinPoke.Uncovered(model, after, 0.001f);
        foreach (var (v, was, now) in which)
            output.WriteLine($"  v{v}: {was * 1000f:F2} mm outside its face as authored, {now * 1000f:F2} mm now; skin moved {(At(after, v) - At(model.Positions, v)).Length() * 1000f:F2} mm, seam {solve.SeamWeightAt(v):F2}");
        output.WriteLine($"{name}: skin moved {went * 1000f:F2} mm; {worse} skin points pushed further through the cloth; "
                       + $"{uncovered} covered points now over 1 mm out; SkinThroughCloth {through} (author's {authoredThrough}); "
                       + $"{folded} folded; {solve.SkinStillOutside} skin points left outside");
        Assert.True(went > strength * 0.6f, $"{name}: the skin went only {went * 1000f:F2} mm");
        Assert.Equal(0, worse);
        Assert.Equal(0, uncovered);
        Assert.Equal(0, folded);
        return solve;
    }

    [LocalDataFact(LocalData.ShiftPack)]
    [Trait("Plan", "AC-studio-move-skin-4")]
    public void ShiftStockingsRideAMovedFoot()
        => Sculpt(Read(LocalData.PackEntryEndingWith(LocalData.ShiftPack, "e6219_sho.mdl")), "Shift shoe");

    [LocalDataFact(LocalData.DeiGratiaPack)]
    [Trait("Plan", "AC-studio-move-skin-9")]
    public void DeiGratiaChestPatchSculptsAndItsOpenEdgeHolds()
    {
        var model = Read(LocalData.PackEntryEndingWith(LocalData.DeiGratiaPack, "chara/equipment/e9068/model/c0201e9068_top.mdl"));
        var solve = Sculpt(model, "Dei Gratia");
        var edge = SkinVertices(model).Where(v => solve.SeamWeightAt(v) == 0f).ToList();
        Assert.NotEmpty(edge);
        var after = solve.Positions();
        Assert.All(edge, v => Assert.Equal(At(model.Positions, v), At(after, v)));
    }

    [LocalDataTheory(ThisOldThing)]
    [InlineData(RueL)]
    [InlineData(NeolitheL)]
    [Trait("Plan", "AC-studio-move-skin-2")]
    [Trait("Plan", "AC-studio-move-skin-4")]
    [Trait("Plan", "AC-studio-move-skin-9")]
    public void ThisOldThingRueAndNeolitheTopsSculpt(string need)
        => Sculpt(ReadFile(need), need.Split("/size/")[1].Split('/')[0]);

    [LocalDataFact(ThisOldThing)]
    [Trait("Plan", "AC-studio-move-skin-8")]
    public void RueMToRueLTransferKeepsTheWaist()
    {
        var source = ReadFile(RueM);
        var target = ReadFile(RueL);
        var solve = Sculpt(source, "rue m");

        var other = BrushTransfer.Transfer(solve, source, target);
        var after = other.Positions();
        var edge = SkinVertices(target).Where(v => other.SeamWeightAt(v) == 0f).ToList();
        Assert.All(edge, v => Assert.Equal(At(target.Positions, v), At(after, v)));
        Assert.True(other.SkinEdited, "the other size's skin did not take the move");
        int worse = SkinPoke.MadeWorse(target, after);
        output.WriteLine($"rue l: {worse} skin points pushed further through the cloth");
        Assert.Equal(0, worse);
    }

    [LocalDataFact(NeolitheChest)]
    [Trait("Plan", "AC-studio-move-skin-11")]
    [Trait("Plan", "EDGE-studio-move-skin-4")]
    public void NeolitheChestIsABodyAndSculpts()
    {
        var model = ReadFile(NeolitheChest);
        Assert.True(SkinMovePolicy.IsBodyModel(model));
        var solve = Sculpt(model, "Neolithe SFW M");
        var edge = SkinVertices(model).Where(v => solve.SeamWeightAt(v) == 0f).ToList();
        Assert.NotEmpty(edge);
        Assert.All(edge, v => Assert.Equal(At(model.Positions, v), At(solve.Positions(), v)));
    }

    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";

    /// <summary>A body mod's chest models: what the exact reading compares a top's skin with.</summary>
    private static List<string> Chests(string mod)
        => Directory.EnumerateFiles(LocalData.Path(mod), "*.mdl", SearchOption.AllDirectories)
                    .Where(p => Path.GetFileName(p).Contains("top", StringComparison.OrdinalIgnoreCase)
                                || p.Contains("chest", StringComparison.OrdinalIgnoreCase))
                    .ToList();

    /// <summary>
    /// A top sculpted with "Move skin too" is read as the automatic refit reads it — each installed body mod's best exact
    /// share, then <see cref="AutoRefitDecisions.Family"/> and <see cref="AutoRefitDecisions.Choose"/> — and comes out
    /// the same as unsculpted: the same mod, still strong enough to need no nearness reading, and the same body size.
    /// </summary>
    [LocalDataFact(ThisOldThing, Rue, Bibo, Yab, Neolithe)]
    [Trait("Plan", "AC-studio-move-skin-11")]
    [Trait("Plan", "EDGE-studio-move-skin-28")]
    public void SculptedGarmentStillRanksItsBodyFirst()
    {
        var model = ReadFile(RueL);
        var solve = Sculpt(model, "rue l");
        var sculpted = Deformed(model, solve);

        var before = BodySizeMatch.ProbeKeys(model);
        var after = BodySizeMatch.ProbeKeys(sculpted);
        var mods = new[] { Rue, Bibo, Yab, Neolithe };
        var read = mods.Select(m =>
        {
            var shares = Chests(m).Select(b => (Body: b, Before: BodySizeMatch.Shared(before, b, out _),
                                                After: BodySizeMatch.Shared(after, b, out _))).ToList();
            return (Mod: m, Shares: shares);
        }).ToList();

        AutoRefitDecisions.ModShare Best(string mod, IEnumerable<float> shares) => new(mod, shares.DefaultIfEmpty(0f).Max(), false, true, 1);
        var was = read.Select(r => Best(r.Mod, r.Shares.Select(s => s.Before))).ToList();
        var now = read.Select(r => Best(r.Mod, r.Shares.Select(s => s.After))).ToList();
        foreach (var (w, n) in was.Zip(now))
            output.WriteLine($"{w.Dir}: {w.Share:P1} unsculpted, {n.Share:P1} sculpted");

        Assert.Equal(Rue, AutoRefitDecisions.Choose(was));
        Assert.Equal(AutoRefitDecisions.Family(was), AutoRefitDecisions.Family(now));
        Assert.Equal(AutoRefitDecisions.Choose(was), AutoRefitDecisions.Choose(now));
        Assert.False(AutoRefitDecisions.Weak(now.Max(m => m.Share)),
                     "sculpted, the exact reading is too weak: the refit would read the top by nearness instead");

        var rue = read.Single(r => r.Mod == Rue).Shares;
        Assert.Equal(rue.MaxBy(s => s.Before).Body, rue.MaxBy(s => s.After).Body);
    }

    /// <summary>What binding and painting with skin moving cost on a long dress and a top, for the log. Report only.</summary>
    [LocalDataTheory(ThisOldThing, Dress)]
    [InlineData(Dress)]
    [InlineData(RueL)]
    [Trait("Plan", "EDGE-studio-move-skin-13")]
    public void MeasureBindingAndDabCost(string need)
    {
        var model = ReadFile(need);
        var solve = new MeshVolumeSolve(model) { MoveSkin = true };
        var clock = Stopwatch.StartNew();
        var seams = SkinVertices(model).Count(v => solve.SeamWeightAt(v) < 1f);
        double seamMs = clock.Elapsed.TotalMilliseconds;
        var follow = SkinFollow.Bind(solve.FollowInputs);
        solve.AttachFollow(follow);

        int spot = Spot(model, solve);
        var at = At(model.Positions, spot);
        solve.Relax(at, 0.06f, 0.5f);   // warm-up
        solve.EndStroke();
        clock.Restart();
        for (int d = 0; d < 20; d++) solve.Relax(at, 0.06f, 0.5f);
        double relaxMs = clock.Elapsed.TotalMilliseconds / 20;
        solve.EndStroke();
        clock.Restart();
        for (int d = 0; d < 20; d++) solve.Paint(at, 0.06f, 0.0001f);
        double paintMs = clock.Elapsed.TotalMilliseconds / 20;
        solve.EndStroke();

        output.WriteLine($"{Path.GetFileName(LocalData.Path(need))}: {model.Positions.Length / 3:N0} verts; seams {seamMs:F0} ms "
                       + $"({seams:N0} edge-faded skin points); bind {follow.BuildMilliseconds:F0} ms, {follow.RiderCount:N0} riders; "
                       + $"relax dab {relaxMs:F1} ms; paint dab {paintMs:F1} ms");
    }
}
