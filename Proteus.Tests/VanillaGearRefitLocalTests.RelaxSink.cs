using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// The relax guard (see <see cref="BodyRetarget.RelaxSinkTolerance"/>) on the game's own gear. Reported: the game's Ruby
/// Cotton Gilet of Striking refitted automatically onto The Body SE showed the pec through the vest — the relax pulled the
/// coarse vest's corners in by up to 8.8 mm, every one still 3 mm clear of the skin, and the rounder chest came up through
/// the middle of the faces between them. Needs the game's data and the body mods named.
/// </summary>
public partial class VanillaGearRefitLocalTests
{
    private const string Gilet = "Ruby Cotton Gilet";

    /// <summary>The Body SE's one chest for c0101, and its legs.</summary>
    private const string TbseChest = "common/1/c0101e0000_top.mdl";

    /// <summary>The game's Pioneer's Bottoms, and the body they were reported on (see PioneerBottomsRefitTests).</summary>
    private const string PioneerBottoms = "chara/equipment/e0689/model/c0201e0689_dwn.mdl";
    private const string GenCSmall = "default legs - smallclothes/gen c small.mdl";

    /// <summary>The gilet's refit as build 1373 made it, piercings left out and carried (The Body SE brings none for c0101,
    /// so the two are one): the guard off reproduces it.</summary>
    private const string Gilet1373 = "62F2A9AD0D8402948DC804292BADCC61F3A2357005B4AD2F40613C02EEFA81DE";
    private const string Gilet1373Pierced = Gilet1373;

    /// <summary>The gilet through the paths that never take the guard, as build 1373 made them: a mod garment's refit, and
    /// an accessory's.</summary>
    private const string GiletMod1373 = "37DCC7435A4E618152A81FDED9550822F1589947ABEA79716EDA8EA1D955FC4B";
    private const string GiletAccessory1373 = "EF23B875186CBD671F690FDBE3B7F4514686403255EC35CAD8BB47AA2A024A3F";

    /// <summary>The patch the user's screenshot shows, on the character's left pec under the strap, and the same on the
    /// right (the spot by the pendant lies in it).</summary>
    private static readonly (Vector3 Lo, Vector3 Hi) LeftPec = (new(0.06f, 1.33f, 0.03f), new(0.11f, 1.39f, 1f));
    private static readonly (Vector3 Lo, Vector3 Hi) RightPec = (new(-0.11f, 1.33f, 0.03f), new(-0.06f, 1.39f, 1f));
    private static readonly (Vector3 Lo, Vector3 Hi) ChestWindow = (new(-0.15f, 1.25f, 0.03f), new(0.15f, 1.45f, 1f));

    private static readonly List<(string Name, Vector3 ToCamera)> GiletViews =
        [("front", ViewAt(0, 0)), ("30° left", ViewAt(0, 30)), ("30° right", ViewAt(0, -30))];

    /// <summary>The gilet set up as the automatic refit sets it up for the user: the game's c0101 chest and legs onto The
    /// Body SE's.</summary>
    private sealed record GiletCase(Lumina.GameData Data, Dalamud.Plugin.Services.IPluginLog Log, UVRemapService Uv,
                                    byte[] Bytes, ModelParts Garment, BodyRetarget.SlotPair[] Pairs, string Source,
                                    string Target)
    {
        public BodyRetarget.Planned Auto(bool piercings = false, BodyRetarget.Tuning? tuning = null,
                                         Action<string, Vec3[]>? trace = null)
        {
            BodyRetarget.Planned Run() => RefitCore.PlanRefit(Garment, Bytes, Pairs, "_top", across: true, gameGear: true,
                                                              out _, trace: trace, carryPiercings: piercings);
            return tuning == null ? Run() : BodyRetarget.WithTuning(tuning, Run);
        }

        public ModelParts Drawn(BodyRetarget.Planned plan)
            => ModelPartReader.Read(RefitCore.MatchSkinToBody(plan.Model, Source, Target, Uv, true, Log))!;
    }

    private static GiletCase GiletOnTbse()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = TopOf(data, Gilet, "0101");
        var vanilla = VanillaBodyCatalog.Read("0101", p => data.GetFile(p)?.Data);
        var mod = BodySizeCatalog.Read(LocalData.Path(TheBodySe));
        var chests = mod.For("_top", "0101");
        // The user's chest: The Body SE ships one for c0101.
        Assert.Equal(TbseChest, Assert.Single(chests).Rel.Replace('\\', '/'));
        string source = vanilla.PathOf(vanilla.For("_top", "0101")[0]), target = mod.PathOf(chests[0]);
        Assert.Null(BodyRetarget.BuildPair("_top", source, target, "_top", true, null, null, uv, out var chest));
        Assert.Null(BodyRetarget.BuildPair("_dwn", vanilla.PathOf(vanilla.For("_dwn", "0101")[0]),
                                           mod.PathOf(mod.For("_dwn", "0101")[0]), "_dwn", true, null, null, uv, out var legs));
        return new GiletCase(data, log, uv, bytes, ModelPartReader.Read(bytes)!, [chest, legs], source, target);
    }

    /// <summary>
    /// cm² of body skin a camera sees inside <paramref name="box"/> from each of <paramref name="views"/>, summed: pixels
    /// whose front-most fragment of everything <paramref name="m"/> draws — culled as the GPU culls — is body skin, the
    /// fragment's point in the box. Piercings and cloth hide what is behind them and are never counted.
    /// </summary>
    private static double SkinOverCloth(ModelParts m, IReadOnlyDictionary<string, bool> culled,
                                        List<(string Name, Vector3 ToCamera)> views, (Vector3 Lo, Vector3 Hi) box,
                                        float centreY = 1.2f)
    {
        var drawn = DrawnTriangles(m, culled);
        var skin = SkinTriangleSet(m);
        long pixels = 0;
        foreach (var (_, toCam) in views)
        {
            var view = new Raster(toCam, centreY);
            var depth = view.Depth();
            var isSkin = new bool[Raster.W * Raster.H];
            foreach (var t in drawn)
            {
                if (t.Culled && Vector3.Dot(t.Front, toCam) <= 0f) continue;
                bool s = skin.Contains((t.A, t.B, t.C));
                view.Fill(t.A, t.B, t.C, (i, z) => { if (z > depth[i]) { depth[i] = z; isSkin[i] = s; } });
            }
            for (int i = 0; i < isSkin.Length; i++)
            {
                if (!isSkin[i]) continue;
                var p = view.At(i, depth[i]);
                if (p.X >= box.Lo.X && p.Y >= box.Lo.Y && p.Z >= box.Lo.Z && p.X <= box.Hi.X && p.Y <= box.Hi.Y && p.Z <= box.Hi.Z)
                    pixels++;
            }
        }
        return pixels / 100.0;
    }

    /// <summary>
    /// Per cloth triangle of the garment (by its corners' nodes) whose centre lies in <paramref name="box"/> where
    /// <paramref name="at"/> puts it: how deep the body comes through its interior, m — the deepest of the same barycentric
    /// samples <see cref="SkinThrough"/> takes, each measured against <paramref name="body"/>'s surface. Independent of the
    /// solve's own face test: this reads the surface, not the skin's vertices.
    /// </summary>
    private static Dictionary<(int, int, int), float> FaceInteriorDepth(ModelParts garment, BodyRetarget.Sets sets,
                                                                         Vec3[] delta, BodySurface body,
                                                                         (Vector3 Lo, Vector3 Hi) box,
                                                                         IReadOnlyCollection<(int, int, int)>? only = null)
    {
        Vector3 Placed(int n) => new(sets.NodeAt[n].X + delta[n].X, sets.NodeAt[n].Y + delta[n].Y, sets.NodeAt[n].Z + delta[n].Z);
        var result = new Dictionary<(int, int, int), float>();
        foreach (var part in ClothParts(garment))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var key = (sets.NodeOf[part.Triangles[t]], sets.NodeOf[part.Triangles[t + 1]], sets.NodeOf[part.Triangles[t + 2]]);
                if (key.Item1 == key.Item2 || key.Item2 == key.Item3 || key.Item1 == key.Item3) continue;
                if (only != null ? !only.Contains(key) : result.ContainsKey(key)) continue;
                Vector3 a = Placed(key.Item1), b = Placed(key.Item2), c = Placed(key.Item3);
                var mid = (a + b + c) / 3f;
                if (only == null && (mid.X < box.Lo.X || mid.Y < box.Lo.Y || mid.Z < box.Lo.Z
                                     || mid.X > box.Hi.X || mid.Y > box.Hi.Y || mid.Z > box.Hi.Z)) continue;
                float deepest = 0f;
                for (int i = 1; i < 6; i++)
                for (int j = 1; i + j < 6; j++)
                {
                    var q = a * (1 - i / 6f - j / 6f) + b * (i / 6f) + c * (j / 6f);
                    if (!body.Nearest(q, 0.03f, out var hit)) continue;
                    float s = Vector3.Dot(q - hit.Point, hit.Normal);
                    if (s < 0f && s > -0.015f) deepest = MathF.Max(deepest, -s);
                }
                result[key] = deepest;
            }
        return result;
    }

    /// <summary>
    /// [AC-relax-sink-guard-1] The user's case: the gilet refitted automatically onto The Body SE shows no skin through the
    /// vest over either pec — from the front or 30° to either side, piercings left out or carried — and the chest shows no
    /// more skin than the game's own gilet on its own body. [EDGE-relax-sink-guard-7] With the guard off, build 1373's bytes,
    /// and the patch back. Measured inside the pass too, against the surface: the relax takes no face deeper into the chest.
    /// [EDGE-relax-sink-guard-11] And the relax still relaxes: most of what it moved without the guard it moves the same.
    /// </summary>
    [LocalDataTheory(LocalData.GameData, TheBodySe)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Plan", "AC-relax-sink-guard-1")]
    [Trait("Plan", "EDGE-relax-sink-guard-7")]
    [Trait("Plan", "EDGE-relax-sink-guard-8")]
    [Trait("Plan", "EDGE-relax-sink-guard-11")]
    public void The_game_gilet_on_TBSE_shows_no_skin_through_the_chest(bool piercings)
    {
        var g = GiletOnTbse();
        var culled = Culled(g.Data, g.Garment);
        var clock = Stopwatch.StartNew();
        var on = g.Auto(piercings);
        long onMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var off = g.Auto(piercings, new BodyRetarget.Tuning(NoRelaxSinkGuard: true));
        long offMs = clock.ElapsedMilliseconds;
        var noRelax = g.Auto(piercings, new BodyRetarget.Tuning(NoRelax: true));
        var drawnOn = g.Drawn(on);
        var drawnOff = g.Drawn(off);
        var drawnNoRelax = g.Drawn(noRelax);
        double Seen(ModelParts m, (Vector3, Vector3) box) => SkinOverCloth(m, culled, GiletViews, box);
        double shipped = Seen(g.Garment, ChestWindow);
        output.WriteLine($"gilet, piercings {piercings}: {onMs} ms guarded, {offMs} ms without; sha on {SkinTuckSheet.Sha(on.Model)}, "
                         + $"off {SkinTuckSheet.Sha(off.Model)}");
        foreach (var (name, box) in new[] { ("left pec", LeftPec), ("right pec", RightPec), ("chest", ChestWindow) })
            output.WriteLine($"  {name}: guarded {Seen(drawnOn, box):0.00} cm², without {Seen(drawnOff, box):0.00}, "
                             + $"no relax {Seen(drawnNoRelax, box):0.00}, the game's own on its body {Seen(g.Garment, box):0.00}");

        // [EDGE-relax-sink-guard-7] The guard off is build 1373.
        Assert.Equal(piercings ? Gilet1373Pierced : Gilet1373, SkinTuckSheet.Sha(off.Model));
        // The measure sees the patch: build 1373 shows the chest well over the game's own, no relax at all in between.
        double chestOff = Seen(drawnOff, ChestWindow), chestNoRelax = Seen(drawnNoRelax, ChestWindow);
        Assert.True(chestOff > chestNoRelax && chestNoRelax > shipped,
                    $"chest {chestOff:0.00} / {chestNoRelax:0.00} / {shipped:0.00} cm²: the harness does not see the patch");
        Assert.True(Seen(drawnOff, LeftPec) >= PatchFloor, $"{Seen(drawnOff, LeftPec):0.00} cm² of the patch without the guard");
        // [AC-relax-sink-guard-1] Guarded: nothing over either pec, and the chest no more than with no relax at all — which
        // has no patch. Not the game's own on its own body: The Body SE's neck and collarbones fill more of the neckline
        // (86.5 cm² the game's own, 127.2 the refit unrelaxed, 124.8 guarded, as measured).
        Assert.True(Seen(drawnOn, LeftPec) <= PecCeiling, $"{Seen(drawnOn, LeftPec):0.00} cm² over the left pec");
        Assert.True(Seen(drawnOn, RightPec) <= PecCeiling, $"{Seen(drawnOn, RightPec):0.00} cm² over the right pec");
        Assert.True(Seen(drawnOn, ChestWindow) <= chestNoRelax, $"{Seen(drawnOn, ChestWindow):0.00} cm² of chest against {chestNoRelax:0.00}");

        // Inside the pass: from the fold relax to the relax, no face of the patch goes deeper into the chest, by its surface.
        var sets = BodyRetarget.Sets.From(g.Garment, new HashSet<int>(),
                                          BodyRetarget.PartedSkinMeshes(g.Garment, g.Pairs, "_top", true, null));
        Dictionary<string, Vec3[]> Stages(BodyRetarget.Tuning? tuning)
        {
            var stages = new Dictionary<string, Vec3[]>();
            g.Auto(piercings, tuning, (s, d) => stages[s] = d);
            Assert.Equal(sets.NodeCount, stages["relax"].Length);
            return stages;
        }
        var guarded = Stages(null);
        var unguarded = Stages(new BodyRetarget.Tuning(NoRelaxSinkGuard: true));
        var chest = new BodySurface(ModelPartReader.Read(File.ReadAllBytes(g.Target))!, 0.01f);
        var region = (LeftPec.Lo - new Vector3(0.02f, 0.02f, 0f), LeftPec.Hi + new Vector3(0.02f, 0.02f, 0f));
        // Faces within the push-out's reach of the chest: cloth already buried deeper as the relax starts — strap ends 9–10 mm
        // inside the body, under the skin drawn over them — is past what any pass reads, and hidden.
        var all = FaceInteriorDepth(g.Garment, sets, guarded["unfold"], chest, region);
        var before = all.Where(f => f.Value < BodyRetarget.ClearDepth).ToDictionary(f => f.Key, f => f.Value);
        // To the face clearing's end: the guard leaves it the shallow sinks, which it lifts.
        float Worst(Vec3[] settled) => FaceInteriorDepth(g.Garment, sets, settled, chest, region, before.Keys)
                                       .Max(f => f.Value - before[f.Key]);
        float worstOn = Worst(guarded["face-settle"]), worstOff = Worst(unguarded["face-settle"]);
        output.WriteLine($"  patch faces {before.Count} ({all.Count - before.Count} buried past reach): deepest further by "
                         + $"the relax {worstOn * 1000:0.0} mm guarded, {worstOff * 1000:0.0} without");

        // [AC-relax-sink-guard-1] The guard is what made the difference: the two runs agree up to the relax, and in the
        // region it took a face back.
        Assert.Equal(unguarded["unfold"], guarded["unfold"]);
        var both = (LeftPec.Lo - new Vector3(0.02f, 0.02f, 0f), LeftPec.Hi + new Vector3(0.02f, 0.02f, 0f));
        var mirror = (RightPec.Lo - new Vector3(0.02f, 0.02f, 0f), RightPec.Hi + new Vector3(0.02f, 0.02f, 0f));
        var regionFaces = FaceInteriorDepth(g.Garment, sets, guarded["unfold"], chest, both).Keys
                          .Concat(FaceInteriorDepth(g.Garment, sets, guarded["unfold"], chest, mirror).Keys).Distinct().ToList();
        bool Differs(int n) => new Vector3(guarded["relax"][n].X - unguarded["relax"][n].X, guarded["relax"][n].Y - unguarded["relax"][n].Y,
                                           guarded["relax"][n].Z - unguarded["relax"][n].Z).Length() > 1e-6f;
        var takenBack = regionFaces.Where(f => Differs(f.Item1) || Differs(f.Item2) || Differs(f.Item3)).ToList();
        Vector3 At(int n) => new(sets.NodeAt[n].X + guarded["relax"][n].X, sets.NodeAt[n].Y + guarded["relax"][n].Y,
                                 sets.NodeAt[n].Z + guarded["relax"][n].Z);
        float Longest((int A, int B, int C) f) => MathF.Max(Vector3.Distance(At(f.A), At(f.B)),
                                                            MathF.Max(Vector3.Distance(At(f.B), At(f.C)), Vector3.Distance(At(f.C), At(f.A))));
        output.WriteLine($"  region faces the guard changed: {takenBack.Count} of {regionFaces.Count}; longest edges "
                         + string.Join(", ", takenBack.Select(f => $"{Longest(f) * 1000:0}").Order()) + " mm");
        Assert.NotEmpty(takenBack);
        Assert.True(worstOff > 0.0005f, $"without the guard the relax takes the patch only {worstOff * 1000:0.0} mm deeper");
        Assert.True(worstOn <= 0.0005f, $"the guarded relax takes a face {worstOn * 1000:0.0} mm deeper");

        // [EDGE-relax-sink-guard-11] Of what the relax moved without the guard, nearly all it still relaxes, and a share
        // exactly as before: a corner held back changes its neighbours' mean in the rounds after, so most of the rest
        // lands a little apart (0.5 mm at the median, as measured), not unrelaxed.
        int movedOff = 0, same = 0, still = 0;
        for (int n = 0; n < sets.NodeCount; n++)
        {
            float Len(Vec3 a, Vec3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z).Length();
            if (Len(unguarded["relax"][n], unguarded["unfold"][n]) <= 1e-4f) continue;
            movedOff++;
            if (Len(guarded["relax"][n], guarded["unfold"][n]) > 1e-4f) still++;
            if (Len(guarded["relax"][n], unguarded["relax"][n]) <= 1e-4f) same++;
        }
        output.WriteLine($"  relaxed without the guard {movedOff}: still relaxed with it {still} ({(double)still / movedOff:P0}), "
                         + $"exactly the same {same} ({(double)same / movedOff:P0})");
        Assert.True(still >= StillRelaxedShare * movedOff, $"{still} of {movedOff} still relaxed");
        Assert.True(same >= RelaxedShare * movedOff, $"{same} of {movedOff} relaxed the same");
    }

    /// <summary>The patch over the left pec without the guard: 21.85 cm² over the three views as build 1373 makes it; held
    /// at half, so the measure is seen to find it.</summary>
    private const double PatchFloor = 10.0;

    /// <summary>Skin over either pec with the guard: 0.00 cm² measured, and the margin of a few stray pixels.</summary>
    private const double PecCeiling = 0.5;

    /// <summary>Of the nodes the relax moves without the guard, the share it still relaxes with it (92% measured, 497 of
    /// 543) and the share it relaxes to within 0.1 mm of the same place (29%, 158): each held at 80% of what was measured.</summary>
    private const double StillRelaxedShare = 0.73;
    private const double RelaxedShare = 0.23;

    /// <summary>
    /// [AC-relax-sink-guard-2][EDGE-relax-sink-guard-9] The Body size tool's refit of the game's gilet, through the very
    /// call the panel makes, is the automatic refit's — on the case the guard acts on, so the flag is proved to reach it.
    /// </summary>
    [LocalDataFact(LocalData.GameData, TheBodySe)]
    [Trait("Plan", "AC-relax-sink-guard-2")]
    [Trait("Plan", "EDGE-relax-sink-guard-9")]
    public void The_Studios_game_top_refit_is_the_automatic_one_on_the_gilet()
    {
        var g = GiletOnTbse();
        var rule = RetargetInputs.StudioSkin(replaceSkin: true, cutHidden: true, clearBody: false, gameGear: true, "_top", crossSex: false);
        BodyRetarget.Planned Studio() => RetargetInputs.StudioPlan(g.Garment, g.Bytes, g.Pairs, "_top", new HashSet<int>(),
                                                                   layOnBody: true, acrossBodies: true, rule, cut: true,
                                                                   RefitCore.ShapePieces(g.Garment, RefitCore.DefaultKeepShape(g.Garment)),
                                                                   carryPiercings: false, liftUnderbust: true);
        var off = new BodyRetarget.Tuning(NoRelaxSinkGuard: true);
        Assert.Equal(g.Auto().Model, Studio().Model);
        Assert.Equal(g.Auto(tuning: off).Model, BodyRetarget.WithTuning(off, Studio).Model);
        Assert.NotEqual(Studio().Model, BodyRetarget.WithTuning(off, Studio).Model);
        // A top refitted for the other sex keeps the boxes, and no guard.
        Assert.False(RetargetInputs.StudioSkin(true, true, true, gameGear: true, "_top", crossSex: true).RelaxSinkGuard);
    }

    /// <summary>The game's Pioneer's Bottoms and the body they were reported on, onto Neolithe's Gen C Small.</summary>
    private static (Lumina.GameData Data, byte[] Bytes, ModelParts Garment, BodyRetarget.SlotPair Pair, string Source, string Target)
        BottomsOnGenC()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var uv = new UVRemapService(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        byte[] bytes = data.GetFile(PioneerBottoms)!.Data;
        var vanilla = VanillaBodyCatalog.Read("0201", p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For("_dwn", "0201")[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(Neolithe));
        string target = mod.PathOf(mod.For("_dwn", "0201").First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(GenCSmall)));
        Assert.Null(BodyRetarget.BuildPair("_dwn", source, target, "_dwn", false, null, null, uv, out var pair));
        return (data, bytes, ModelPartReader.Read(bytes)!, pair, source, target);
    }

    /// <summary>
    /// [AC-relax-sink-guard-2][EDGE-relax-sink-guard-14][EDGE-relax-sink-guard-13] The Body size tool's refit of the game's
    /// legs with "Push cloth out" ticked is the automatic refit's, as it was before the guard: the guard goes to both.
    /// </summary>
    [LocalDataFact(LocalData.GameData, Neolithe)]
    [Trait("Plan", "AC-relax-sink-guard-2")]
    [Trait("Plan", "EDGE-relax-sink-guard-13")]
    [Trait("Plan", "EDGE-relax-sink-guard-14")]
    public void The_Studios_game_legs_refit_is_the_automatic_one()
    {
        var b = BottomsOnGenC();
        var rule = RetargetInputs.StudioSkin(true, true, clearBody: true, gameGear: true, "_dwn", false);
        BodyRetarget.Planned Studio() => RetargetInputs.StudioPlan(b.Garment, b.Bytes, [b.Pair], "_dwn", new HashSet<int>(),
                                                                   layOnBody: true, acrossBodies: true, rule, cut: true,
                                                                   RefitCore.ShapePieces(b.Garment, RefitCore.DefaultKeepShape(b.Garment)),
                                                                   carryPiercings: false, liftUnderbust: true);
        BodyRetarget.Planned Auto() => RefitCore.PlanRefit(b.Garment, b.Bytes, [b.Pair], "_dwn", across: true, gameGear: true, out _);
        var off = new BodyRetarget.Tuning(NoRelaxSinkGuard: true);
        Assert.Equal(Auto().Model, Studio().Model);
        Assert.Equal(BodyRetarget.WithTuning(off, Auto).Model, BodyRetarget.WithTuning(off, Studio).Model);
        bool fires = !Auto().Model.AsSpan().SequenceEqual(BodyRetarget.WithTuning(off, Auto).Model);
        output.WriteLine($"the guard {(fires ? "acts" : "does not act")} on the Pioneer's Bottoms; the gilet proves the Studio's wiring");
    }

    /// <summary>The rows, and whether the guard runs on them: the game's tops on The Body SE's men's chest and the game's
    /// legs refitted alone; never over a breast. "_dwn+chest" is the legs with a woman's chest in play beside them.</summary>
    public static TheoryData<string, string, string?, string, string> GameGearCases
    {
        get
        {
            var cases = new TheoryData<string, string, string?, string, string>();
            foreach (var row in StrapTipCases)
                cases.Add((string)row[0], (string)row[1], (string?)row[2], (string)row[3], "_top");
            cases.Add(Gilet, TheBodySe, TbseChest, "0101", "_top");
            cases.Add(PioneerBottoms, Neolithe, GenCSmall, "0201", "_dwn");
            cases.Add(PioneerBottoms, Neolithe, GenCSmall, "0201", "_dwn+chest");
            return cases;
        }
    }

    /// <summary>
    /// [AC-relax-sink-guard-2][EDGE-relax-sink-guard-13][EDGE-relax-sink-guard-24] Where the guard runs, it makes no game
    /// garment worse against itself off: no more skin seen — from the front and, for the tops, from every side — no more
    /// seen into the garment, no more folds, turned faces or cloth in the body. [AC-relax-sink-guard-3]
    /// [EDGE-relax-sink-guard-22][EDGE-relax-sink-guard-23] Over a breast it does not run at all: every woman's chest, and
    /// the legs with one in play, byte for byte as build 1373 made them. Skin through the cloth by <see cref="SkinThrough"/>
    /// is printed, not held: it counts cloth buried under drawn skin that nothing shows (272.8 cm² on the game's own gilet
    /// as shipped).
    /// </summary>
    [LocalDataTheory(LocalData.GameData, Neolithe, Rosaline, LavaBod, Bibo, Rue, TheBodySe)]
    [MemberData(nameof(GameGearCases))]
    [Trait("Plan", "AC-relax-sink-guard-2")]
    [Trait("Plan", "AC-relax-sink-guard-3")]
    [Trait("Plan", "EDGE-relax-sink-guard-13")]
    [Trait("Plan", "EDGE-relax-sink-guard-22")]
    [Trait("Plan", "EDGE-relax-sink-guard-23")]
    [Trait("Plan", "EDGE-relax-sink-guard-24")]
    public void A_relax_guard_makes_no_game_gear_worse(string garmentName, string modNeed, string? rel, string race, string row)
    {
        string slot = row == "_dwn+chest" ? "_dwn" : row;
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        byte[] bytes = slot == "_dwn" ? data.GetFile(garmentName)!.Data : TopOf(data, garmentName, race);
        var garment = ModelPartReader.Read(bytes)!;
        var vanilla = VanillaBodyCatalog.Read(race, p => data.GetFile(p)?.Data);
        string source = vanilla.PathOf(vanilla.For(slot, race)[0]);
        var mod = BodySizeCatalog.Read(LocalData.Path(modNeed));
        var options = mod.For(slot, race);
        string target = mod.PathOf(rel == null ? options[0] : options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel)));
        bool male = race == "0101";
        Assert.Null(BodyRetarget.BuildPair(slot, source, target, slot, male, null, null, uv, out var pair));
        List<BodyRetarget.SlotPair> pairs = [pair];
        if (row == "_dwn+chest")
        {
            // A woman's chest in play beside the legs, as a chest beneath the waist goes along.
            string chestFrom = vanilla.PathOf(vanilla.For("_top", race)[0]);
            string chestTo = mod.PathOf(mod.For("_top", race).First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(AlmondXs)));
            Assert.Null(BodyRetarget.BuildPair("_top", chestFrom, chestTo, "_top", male, null, null, uv, out var chest));
            pairs.Add(chest);
        }
        bool guarded = !BodyRetarget.BreastInPlay(pairs);
        BodyRetarget.Planned Auto(BodyRetarget.Tuning? tuning)
        {
            BodyRetarget.Planned Run() => RefitCore.PlanRefit(garment, bytes, pairs, slot, across: true, gameGear: true, out _);
            return tuning == null ? Run() : BodyRetarget.WithTuning(tuning, Run);
        }
        var knob = new BodyRetarget.Tuning(NoRelaxSinkGuard: true);
        // The cost, warm: one untimed run of each, then the median of three, the two settings taking turns.
        var on = Auto(null);
        var off = Auto(knob);
        var times = (On: new List<long>(), Off: new List<long>());
        for (int i = 0; i < 3; i++)
        {
            var clock = Stopwatch.StartNew();
            Auto(null);
            times.On.Add(clock.ElapsedMilliseconds);
            clock.Restart();
            Auto(knob);
            times.Off.Add(clock.ElapsedMilliseconds);
        }
        long onMs = times.On.Order().ElementAt(1), offMs = times.Off.Order().ElementAt(1);
        ModelParts Drawn(BodyRetarget.Planned p) => ModelPartReader.Read(RefitCore.MatchSkinToBody(p.Model, source, target, uv, male, log))!;
        var (drawnOn, drawnOff) = (Drawn(on), Drawn(off));
        var culled = Culled(data, garment);
        bool legs = slot == "_dwn";
        var views = legs ? WideViews : FrontViews;
        var box = legs ? (new Vector3(-1f, 0.4f, -1f), new Vector3(1f, 1.1f, 1f))
                       : (new Vector3(-ArmFrom, 0.95f, -1f), new Vector3(ArmFrom, 1.45f, 1f));
        float centreY = legs ? 0.8f : 1.2f;
        double seenOn = SkinOverCloth(drawnOn, culled, views, box, centreY), seenOff = SkinOverCloth(drawnOff, culled, views, box, centreY);
        int Inside(BodyRetarget.Planned p) => BodySizeMatch.ClothAgainst(ModelPartReader.Read(p.Model)!, target)!.Value.Inside;
        int Turned(BodyRetarget.Planned p) => SkinTuckSheet.FoldedAgainstAuthored(garment, SkinTuckSheet.Laid(bytes, p), withSkin: true);
        double throughOn = SkinThrough(drawnOn), throughOff = SkinThrough(drawnOff);
        output.WriteLine($"RAN {garmentName} on {Path.GetFileName(LocalData.Path(modNeed))} / {rel}: {garment.Parts.Where(p => p.Island < 0).Sum(p => p.Triangles.Length / 3)} "
                         + $"triangles, {onMs} ms guarded, {offMs} ms without ({(offMs > 0 ? (double)onMs / offMs - 1 : 0):P0}); "
                         + $"{(on.Model.AsSpan().SequenceEqual(off.Model) ? "unchanged" : "changed")}; skin seen {seenOff:0.00} -> {seenOn:0.00} cm², "
                         + $"folded {off.Report.Folded} -> {on.Report.Folded}, turned {Turned(off)} -> {Turned(on)}, inside {Inside(off)} -> "
                         + $"{Inside(on)}, through {throughOff:0.0} -> {throughOn:0.0} cm²");
        Assert.NotNull(ModelPartReader.Read(on.Model));
        // [AC-relax-sink-guard-3][EDGE-relax-sink-guard-22][EDGE-relax-sink-guard-23] Over a breast: build 1373's bytes.
        Assert.Equal(garmentName == Gilet || modNeed == TheBodySe || row == "_dwn", guarded);
        if (!guarded)
        {
            Assert.Equal(off.Model, on.Model);
            return;
        }
        Assert.True(seenOn <= seenOff + 0.2, $"{seenOn:0.00} cm² of skin seen against {seenOff:0.00}");
        if (!legs)
        {
            // From every side too: the cloth the guard holds may lie anywhere round the body.
            var all = (new Vector3(-ArmFrom, 0.95f, -1f), new Vector3(ArmFrom, 1.45f, 1f));
            double wideOn = SkinOverCloth(drawnOn, culled, WideViews, all), wideOff = SkinOverCloth(drawnOff, culled, WideViews, all);
            var torso = TorsoTriangles(ModelPartReader.Read(File.ReadAllBytes(target))!);
            double intoOn = InsideHoleArea(drawnOn, culled, torso, FrontViews, null), intoOff = InsideHoleArea(drawnOff, culled, torso, FrontViews, null);
            output.WriteLine($"  seen from every side {wideOff:0.00} -> {wideOn:0.00} cm²; seen into the garment {intoOff:0.00} -> {intoOn:0.00} cm²");
            Assert.True(wideOn <= wideOff + 0.2, $"{wideOn:0.00} cm² of skin seen from every side against {wideOff:0.00}");
            Assert.True(intoOn <= intoOff + 0.5, $"{intoOn:0.00} cm² seen into the garment against {intoOff:0.00}");
        }
        Assert.True(on.Report.Folded <= off.Report.Folded, $"folded {on.Report.Folded} against {off.Report.Folded}");
        Assert.True(Turned(on) <= Turned(off), $"turned {Turned(on)} against {Turned(off)}");
        Assert.True(Inside(on) <= Inside(off), $"inside {Inside(on)} against {Inside(off)}");
    }

    /// <summary>
    /// [EDGE-relax-sink-guard-22][EDGE-relax-sink-guard-23][EDGE-relax-sink-guard-24] The breast read off the chests in
    /// play: none on The Body SE's men's chest, nor with the legs alone; on every woman's chest measured, Rue+'s
    /// <c>iv_c_mune</c> included; and a chest file that reads as another mesh than the pair's counts as one.
    /// </summary>
    [LocalDataFact(LocalData.GameData, TheBodySe, Neolithe, Rue, Bibo)]
    [Trait("Plan", "EDGE-relax-sink-guard-22")]
    [Trait("Plan", "EDGE-relax-sink-guard-23")]
    [Trait("Plan", "EDGE-relax-sink-guard-24")]
    public void Relax_guard_reads_the_breast_off_the_chests_in_play()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var uv = new UVRemapService(Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        BodyRetarget.SlotPair Pair(string slot, string race, string modNeed, string? rel)
        {
            var vanilla = VanillaBodyCatalog.Read(race, p => data.GetFile(p)?.Data);
            var mod = BodySizeCatalog.Read(LocalData.Path(modNeed));
            var options = mod.For(slot, race);
            string target = mod.PathOf(rel == null ? options[0] : options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel)));
            Assert.Null(BodyRetarget.BuildPair(slot, vanilla.PathOf(vanilla.For(slot, race)[0]), target, slot, race == "0101",
                                               null, null, uv, out var pair));
            return pair;
        }
        var tbse = Pair("_top", "0101", TheBodySe, TbseChest);
        Assert.False(BodyRetarget.BreastInPlay([tbse]));
        Assert.False(BodyRetarget.BreastInPlay([Pair("_dwn", "0201", Neolithe, GenCSmall)]));
        Assert.True(BodyRetarget.BreastInPlay([Pair("_top", "0201", Neolithe, AlmondXs)]));
        Assert.True(BodyRetarget.BreastInPlay([Pair("_top", "0201", Rue, null)]));
        Assert.True(BodyRetarget.BreastInPlay([Pair("_top", "0201", Bibo, null)]));
        // The men's chest's file under a mesh it does not match: no guard.
        var cut = tbse with { Target = ModelPartReader.Read(File.ReadAllBytes(
            BodySizeCatalog.Read(LocalData.Path(Neolithe)).PathOf(BodySizeCatalog.Read(LocalData.Path(Neolithe)).For("_top", "0201")[0])))! };
        Assert.True(BodyRetarget.BreastInPlay([cut]));
    }

    /// <summary>
    /// [AC-relax-sink-guard-3] The paths a modder's garment takes never take the guard: the gilet refitted as a mod
    /// garment, and as an accessory over the chest, come out byte for byte as build 1373 made them, the knob or not.
    /// </summary>
    [LocalDataFact(LocalData.GameData, TheBodySe)]
    [Trait("Plan", "AC-relax-sink-guard-3")]
    public void A_mod_garment_refit_ignores_the_relax_guard()
    {
        var g = GiletOnTbse();
        var off = new BodyRetarget.Tuning(NoRelaxSinkGuard: true);
        BodyRetarget.Planned Mod() => RefitCore.PlanRefit(g.Garment, g.Bytes, g.Pairs, "_top", across: true, gameGear: false, out _);
        BodyRetarget.Planned Accessory() => RefitCore.PlanAccessoryRefit(g.Garment, g.Bytes, g.Pairs, "_top", across: true, out _);
        string mod = SkinTuckSheet.Sha(Mod().Model), accessory = SkinTuckSheet.Sha(Accessory().Model);
        output.WriteLine($"mod garment {mod}, accessory {accessory}");
        Assert.Equal(GiletMod1373, mod);
        Assert.Equal(GiletAccessory1373, accessory);
        Assert.Equal(mod, SkinTuckSheet.Sha(BodyRetarget.WithTuning(off, Mod).Model));
        Assert.Equal(accessory, SkinTuckSheet.Sha(BodyRetarget.WithTuning(off, Accessory).Model));
    }
}
