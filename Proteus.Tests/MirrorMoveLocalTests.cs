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
using Kind = Proteus.Services.MeshVolumeSolve.MoveKind;
using Mode = Proteus.Services.MeshVolumeSolve.MirrorMode;
using MoveMirror = Proteus.Services.MeshVolumeSolve.MoveMirror;

namespace Proteus.Tests;

/// <summary>
/// The mirror for Move, Rotate and Scale on real garments — game tops and body mods' smallclothes — which are only
/// roughly symmetric: authored asymmetry, sides triangulated differently, skin a millimetre under tight cloth. Measured
/// inside the pass, from <see cref="MeshVolumeSolve.Positions"/>, never from a re-exported model.
/// </summary>
public class MirrorMoveLocalTests(ITestOutputHelper output)
{
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
    private static Vector3 Reflect(Vector3 v) => new(-v.X, v.Y, v.Z);

    /// <summary>The largest cloth submesh: not skin, not a piercing.</summary>
    private static ModelPart Cloth(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)
                              && !p.Material.Contains("piercing", StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(p => p.Triangles.Length).First();

    private static ModelParts GameTop(string name)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        return ModelPartReader.Read(VanillaGearRefitLocalTests.TopOf(data, name))!;
    }

    /// <summary>The chest model at <paramref name="rel"/>, or with none named, the first chest option that has cloth on it.</summary>
    private static ModelParts ModChest(string need, string? rel)
    {
        string dir = LocalData.Path(need);
        var options = BodySizeCatalog.Read(dir).For("_top", "0201");
        if (rel != null)
        {
            var option = options.First(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel));
            return ModelPartReader.Read(File.ReadAllBytes(Path.Combine(dir, option.Rel)))!;
        }
        foreach (var option in options)
            if (ModelPartReader.Read(File.ReadAllBytes(Path.Combine(dir, option.Rel))) is { } m && HasCloth(m)) return m;
        throw new InvalidOperationException($"{need}: no chest option has cloth");
    }

    private static bool HasCloth(ModelParts m)
        => m.Parts.Any(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)
                            && !p.Material.Contains("piercing", StringComparison.OrdinalIgnoreCase));

    private static int Folded(ModelParts model, float[] before, float[] after)
    {
        int folded = 0;
        foreach (var part in model.Parts.Where(p => p.Island < 0))
        {
            var t = part.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var n0 = Vector3.Cross(At(before, t[i + 1]) - At(before, t[i]), At(before, t[i + 2]) - At(before, t[i]));
                var n1 = Vector3.Cross(At(after, t[i + 1]) - At(after, t[i]), At(after, t[i + 2]) - At(after, t[i]));
                if (n0.LengthSquared() > 1e-16f && Vector3.Dot(n0, n1) <= 0f) folded++;
            }
        }
        return folded;
    }

    /// <summary>For each vertex of <paramref name="verts"/>, the vertex of <paramref name="among"/> nearest its mirror
    /// image, within 2 mm; −1 where there is none.</summary>
    private static Dictionary<int, int> MirrorVertices(float[] pos, IEnumerable<int> verts, IEnumerable<int> among)
    {
        const float cell = 0.002f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        foreach (int v in among)
        {
            var k = Key(At(pos, v));
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = [];
            list.Add(v);
        }
        var found = new Dictionary<int, int>();
        foreach (int v in verts)
        {
            var q = Reflect(At(pos, v));
            var (x, y, z) = Key(q);
            int best = -1;
            float bestD = cell;
            for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            for (int k = -1; k <= 1; k++)
            {
                if (!grid.TryGetValue((x + i, y + j, z + k), out var list)) continue;
                foreach (int u in list)
                {
                    float d = Vector3.Distance(At(pos, u), q);
                    if (d < bestD) { best = u; bestD = d; }
                }
            }
            found[v] = best;
        }
        return found;
    }

    private void RealGarmentMirrorsSymmetrically(ModelParts model, string what)
    {
        var cloth = Cloth(model);
        var clothVerts = cloth.Triangles.Distinct().ToArray();
        var seeds = clothVerts.Where(v => model.Positions[v * 3] > 0.02f).ToArray();
        Assert.True(seeds.Length > 50, $"{what}: only {seeds.Length} cloth vertices right of 20 mm");
        var twins = MirrorVertices(model.Positions, seeds, clothVerts);
        var pivot = seeds.Aggregate(Vector3.Zero, (s, v) => s + At(model.Positions, v)) / seeds.Length;

        // Warm once, then time the preview and the drag's start.
        new MeshVolumeSolve(model).MoveFalloff(seeds, 0.03f, false, mirror: true);
        foreach (var (kind, drag) in new (Kind, Action<MeshVolumeSolve>)[]
                 {
                     (Kind.Translate, s => s.MoveTo(new Vector3(0.006f, 0.004f, 0f))),
                     (Kind.Rotate, s => s.TransformTo(RotateGizmo.About(pivot, Vector3.UnitY, 0.15f))),
                     (Kind.Scale, s => s.TransformTo(PartScaleDrag.Build(pivot, new Vector3(1.15f, 1f, 1f)))),
                 })
        {
            var solve = new MeshVolumeSolve(model);
            long t0 = PhaseCounter.Begin();
            solve.MoveFalloff(seeds, 0.03f, false, mirror: true);
            double previewMs = PhaseCounter.MsSince(t0);
            t0 = PhaseCounter.Begin();
            Assert.True(solve.BeginMove(seeds, true, 0.03f, mirror: new MoveMirror(kind, pivot)) > 0);
            double beginMs = PhaseCounter.MsSince(t0);
            var report = solve.DragMirror!.Value;
            drag(solve);
            var after = solve.Positions();

            // How much of the far side's mirror image moved as the mirror of its twin.
            int twinned = 0, mirrored = 0;
            foreach (int v in seeds)
            {
                if (twins[v] < 0) continue;
                twinned++;
                var dv = At(after, v) - At(model.Positions, v);
                var du = At(after, twins[v]) - At(model.Positions, twins[v]);
                if (Vector3.Distance(Reflect(dv), du) <= 0.0005f) mirrored++;
            }

            // The same by hand with Mirror off: this side's drag, and the far side's mirror image dragged on its own.
            // The far side of a game mesh is not quite the mirror of this one, so it may fold where this side does not.
            var plain = new MeshVolumeSolve(model);
            plain.BeginMove(seeds, true, 0.03f);
            if (kind == Kind.Translate) plain.MoveTo(new Vector3(0.006f, 0.004f, 0f));
            else drag(plain);
            var farSeeds = clothVerts.Where(v => model.Positions[v * 3] < -0.02f).ToArray();
            var byHand = new MeshVolumeSolve(model);
            byHand.BeginMove(farSeeds, true, 0.03f);
            if (kind == Kind.Translate) byHand.MoveTo(MirrorMotion.Reflect(new Vector3(0.006f, 0.004f, 0f)));
            else byHand.TransformTo(MirrorMotion.Reflect(kind == Kind.Rotate
                ? RotateGizmo.About(pivot, Vector3.UnitY, 0.15f)
                : PartScaleDrag.Build(pivot, new Vector3(1.15f, 1f, 1f))));
            int plainFolded = Folded(model, model.Positions, plain.Positions())
                              + Folded(model, model.Positions, byHand.Positions());
            int folded = Folded(model, model.Positions, after);

            output.WriteLine($"{what} {kind}: {report.Mode}, paired {report.Paired}/{report.Selected} " +
                             $"({100f * report.Paired / report.Selected:F0} %), held {report.Held}; " +
                             $"mirror twins found {twinned}, moved as the mirror {mirrored} " +
                             $"({(twinned > 0 ? 100f * mirrored / twinned : 0f):F0} %); folded {folded} (both sides by hand {plainFolded}); " +
                             $"preview {previewMs:F1} ms, begin {beginMs:F1} ms");

            Assert.Equal(Mode.Sides, report.Mode);
            Assert.True(report.Paired >= 0.9f * report.Selected, $"{what} {kind}: paired {report.Paired} of {report.Selected}");
            Assert.True(twinned == 0 || mirrored >= 0.9f * twinned, $"{what} {kind}: {mirrored} of {twinned} twins moved as the mirror");
            Assert.True(folded <= plainFolded, $"{what} {kind}: {folded} folded, both sides by hand fold {plainFolded}");

            Assert.True(solve.EndMove());
            solve.Undo();
            var back = solve.Positions();
            for (int v = 0; v < model.Positions.Length / 3; v++)
                Assert.True(Vector3.Distance(At(model.Positions, v), At(back, v)) < 1e-6f, $"{what} {kind}: vertex {v} not undone");
        }
    }

    private void RealBraIsCentredAndStaysCentred(ModelParts model, string what)
    {
        var bra = Cloth(model).Triangles.Distinct().ToArray();
        var pivot = bra.Aggregate(Vector3.Zero, (s, v) => s + At(model.Positions, v)) / bra.Length;

        var solve = new MeshVolumeSolve(model);
        solve.BeginMove(bra, false, 0f, mirror: new MoveMirror(Kind.Translate, pivot));
        output.WriteLine($"{what}: whole bra {solve.DragMirror}");
        Assert.Equal(Mode.Centred, solve.DragMirror?.Mode);
        solve.MoveTo(new Vector3(0.01f, 0f, 0f));
        Assert.False(solve.EndMove(), $"{what}: a sideways move of a centred bra changed something");

        solve.BeginMove(bra, false, 0f, mirror: new MoveMirror(Kind.Translate, pivot));
        solve.MoveTo(new Vector3(0.01f, 0.004f, 0f));
        var after = solve.Positions();
        foreach (int v in bra) Assert.Equal(model.Positions[v * 3], after[v * 3], 1e-6f);

        // Cloth welded to skin holds with it, so the same lift with Mirror off stretches (and may fold) the same faces.
        var plain = new MeshVolumeSolve(model);
        plain.BeginMove(bra, false, 0f);
        plain.MoveTo(new Vector3(0f, 0.004f, 0f));
        Assert.Equal(Folded(model, model.Positions, plain.Positions()), Folded(model, model.Positions, after));
    }

    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string ThisOldThing = LocalData.Mods + ":This Old Thing - by Solona";
    private const string ThisOldThingRueL = ThisOldThing + "/size/rue l/chara/equipment/e6255/model/c0201e6255_top.mdl";

    private static MeshVolumeSolve SkinSolve(ModelParts model)
    {
        var solve = new MeshVolumeSolve(model) { MoveSkin = true };
        Assert.True(solve.AttachFollow(SkinFollow.Bind(solve.FollowInputs)));
        return solve;
    }

    private static List<int> SkinVertices(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                  .SelectMany(p => p.Triangles).Distinct().ToList();

    /// <summary>The first chest option of a body mod that has skin.</summary>
    private static ModelParts ModChestWithSkin(string need)
    {
        string dir = LocalData.Path(need);
        foreach (var option in BodySizeCatalog.Read(dir).For("_top", "0201"))
            if (ModelPartReader.Read(File.ReadAllBytes(Path.Combine(dir, option.Rel))) is { } m && SkinVertices(m).Count > 0) return m;
        throw new InvalidOperationException($"{need}: no chest option has skin");
    }

    /// <summary>Moved skin vertices whose skin neighbours all stayed: a spike the mirror left behind.</summary>
    private static int Spikes(ModelParts model, float[] after, HashSet<int> skin)
    {
        var adj = new Dictionary<int, HashSet<int>>();
        foreach (var part in model.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = part.Triangles[t + k], b = part.Triangles[t + (k + 1) % 3];
                    (adj.TryGetValue(a, out var la) ? la : adj[a] = []).Add(b);
                    (adj.TryGetValue(b, out var lb) ? lb : adj[b] = []).Add(a);
                }
        bool Moved(int v) => Vector3.Distance(At(after, v), At(model.Positions, v)) > 1e-5f;
        return skin.Count(v => Moved(v) && adj.TryGetValue(v, out var n) && n.Count > 0 && n.All(u => !Moved(u)));
    }

    /// <summary>
    /// "Move skin too" with Mirror on a real model: skin right of 20 mm in the middle third of its height is moved,
    /// turned and stretched, with the falloff on and off. The far side's skin twins must move as the mirror, and the
    /// result may fold and poke no more than doing both sides by hand; with the falloff off, no spikes.
    /// </summary>
    /// <param name="centreSlivers">Folds allowed beyond the by-hand count: sliver cloth straddling the centre line, which
    /// Sides pulls apart when a drag goes sideways — each point takes its own side's motion, a known limit of the mirror.</param>
    private void RealSkinMirrorsSymmetrically(ModelParts model, string what, int centreSlivers = 0)
    {
        var skinList = SkinVertices(model);
        var skin = skinList.ToHashSet();
        float lo = skinList.Min(v => At(model.Positions, v).Y), hi = skinList.Max(v => At(model.Positions, v).Y);
        bool Band(int v) => At(model.Positions, v).Y is var y && y > lo + (hi - lo) / 3f && y < hi - (hi - lo) / 3f;
        var seeds = skinList.Where(v => model.Positions[v * 3] > 0.02f && Band(v)).ToArray();
        var farSeeds = skinList.Where(v => model.Positions[v * 3] < -0.02f && Band(v)).ToArray();
        Assert.True(seeds.Length > 50, $"{what}: only {seeds.Length} skin vertices right of 20 mm");
        var twins = MirrorVertices(model.Positions, seeds, skinList);
        var pivot = seeds.Aggregate(Vector3.Zero, (s, v) => s + At(model.Positions, v)) / seeds.Length;
        var offset = new Vector3(0.004f, 0.003f, 0.002f);
        Matrix4x4 TurnOf(Kind kind) => kind == Kind.Rotate
            ? RotateGizmo.About(pivot, Vector3.UnitY, 0.1f)
            : PartScaleDrag.Build(pivot, new Vector3(1.1f, 1f, 1.05f));
        void Drag(MeshVolumeSolve s, Kind kind, bool far)
        {
            if (kind == Kind.Translate) s.MoveTo(far ? MirrorMotion.Reflect(offset) : offset);
            else s.TransformTo(far ? MirrorMotion.Reflect(TurnOf(kind)) : TurnOf(kind));
        }

        SkinSolve(model).MoveFalloff(seeds, 0.03f, false, mirror: true);   // warm
        foreach (bool adjacent in new[] { true, false })
        foreach (var kind in new[] { Kind.Translate, Kind.Rotate, Kind.Scale })
        {
            float falloff = adjacent ? 0.03f : 0f;
            var solve = SkinSolve(model);
            long t0 = PhaseCounter.Begin();
            solve.MoveFalloff(seeds, falloff, false, mirror: true);
            double previewMs = PhaseCounter.MsSince(t0);
            t0 = PhaseCounter.Begin();
            Assert.True(solve.BeginMove(seeds, adjacent, falloff, mirror: new MoveMirror(kind, pivot)) > 0);
            double beginMs = PhaseCounter.MsSince(t0);
            var report = solve.DragMirror!.Value;
            Drag(solve, kind, far: false);
            var during = solve.Positions();

            int twinned = 0, mirrored = 0;
            foreach (int v in seeds)
            {
                if (twins[v] < 0) continue;
                twinned++;
                var dv = At(during, v) - At(model.Positions, v);
                var du = At(during, twins[v]) - At(model.Positions, twins[v]);
                if (Vector3.Distance(Reflect(dv), du) <= 0.0005f) mirrored++;
            }
            int spikes = adjacent ? 0 : Spikes(model, during, skin);
            t0 = PhaseCounter.Begin();
            Assert.True(solve.EndMove());
            double endMs = PhaseCounter.MsSince(t0);
            var after = solve.Positions();

            // Both sides by hand, Mirror off: this side's drag, and the far side's mirror image dragged on its own.
            var near = SkinSolve(model);
            near.BeginMove(seeds, adjacent, falloff);
            Drag(near, kind, far: false);
            t0 = PhaseCounter.Begin();
            near.EndMove();
            double oneSideEndMs = PhaseCounter.MsSince(t0);
            var byHand = SkinSolve(model);
            byHand.BeginMove(farSeeds, adjacent, falloff);
            Drag(byHand, kind, far: true);
            byHand.EndMove();
            int handFolded = Folded(model, model.Positions, near.Positions()) + Folded(model, model.Positions, byHand.Positions());
            int handWorse = SkinPoke.MadeWorse(model, near.Positions()) + SkinPoke.MadeWorse(model, byHand.Positions());
            int folded = Folded(model, model.Positions, after);
            int worse = SkinPoke.MadeWorse(model, after);

            output.WriteLine($"{what} {kind} falloff {falloff * 1000f:F0} mm: {report.Mode}, paired {report.Paired}/{report.Selected} " +
                             $"({100f * report.Paired / report.Selected:F0} %), held {report.Held}; twins {twinned}, moved as the mirror " +
                             $"{mirrored} ({(twinned > 0 ? 100f * mirrored / twinned : 0f):F0} %); spikes {spikes}; folded {folded} " +
                             $"(by hand {handFolded}); poked {worse} (by hand {handWorse}); preview {previewMs:F1} ms, " +
                             $"begin {beginMs:F1} ms, release {endMs:F1} ms (one side {oneSideEndMs:F1} ms)");

            Assert.Equal(Mode.Sides, report.Mode);
            Assert.True(report.Paired >= 0.9f * report.Selected, $"{what} {kind}: paired {report.Paired} of {report.Selected}");
            Assert.True(twinned > 0 && mirrored >= 0.9f * twinned, $"{what} {kind}: {mirrored} of {twinned} twins moved as the mirror");
            Assert.Equal(0, spikes);
            Assert.True(folded <= handFolded + centreSlivers,
                        $"{what} {kind}: {folded} folded, both sides by hand fold {handFolded}");
            Assert.True(worse <= handWorse, $"{what} {kind}: {worse} skin points poked through, both sides by hand {handWorse}");

            solve.Undo();
            var back = solve.Positions();
            for (int v = 0; v < model.Positions.Length / 3; v++)
                Assert.True(Vector3.Distance(At(model.Positions, v), At(back, v)) < 1e-6f, $"{what} {kind}: vertex {v} not undone");
        }
    }

    [LocalDataTheory(Neolithe)]
    [InlineData("default chest - smallclothes/sfw xs.mdl")]
    [Trait("Plan", "AC-mirror-skin-move-9")]
    [Trait("Plan", "EDGE-mirror-skin-move-14")]
    public void Neolithe_body_skin_mirrors(string rel)
    {
        var model = ModChest(Neolithe, rel);
        Assert.True(SkinMovePolicy.IsBodyModel(model));
        RealSkinMirrorsSymmetrically(model, "Neolithe " + rel);
    }

    [LocalDataTheory(Bibo)]
    [InlineData(0)]
    [Trait("Plan", "AC-mirror-skin-move-9")]
    public void Bibo_body_skin_mirrors(int _) => RealSkinMirrorsSymmetrically(ModChestWithSkin(Bibo), "Bibo+");

    [LocalDataTheory(Rue)]
    [InlineData(0)]
    [Trait("Plan", "AC-mirror-skin-move-9")]
    public void Rue_body_skin_mirrors(int _) => RealSkinMirrorsSymmetrically(ModChestWithSkin(Rue), "Rue+");

    /// <summary>
    /// A garment's own skin patch — the top of This Old Thing in its Rue L size — not a body's. A sideways move with the
    /// falloff folds one 2 mm sliver of cloth straddling the cleavage's centre line that both sides by hand do not:
    /// accepted as the mirror's known limit at the centre (measured 2026-10-21).
    /// </summary>
    [LocalDataTheory(ThisOldThing)]
    [InlineData(ThisOldThingRueL)]
    [Trait("Plan", "AC-mirror-skin-move-9")]
    public void Garment_skin_patch_mirrors(string need)
        => RealSkinMirrorsSymmetrically(ModelPartReader.Read(File.ReadAllBytes(LocalData.Path(need)))!, "This Old Thing rue l",
                                        centreSlivers: 1);

    public static TheoryData<string> GameTops => new() { "Neo-Ishgardian Top of Maiming", "Scion Adventurer's", "Dirndl's Bodice" };

    [LocalDataTheory(LocalData.GameData)]
    [MemberData(nameof(GameTops))]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    [Trait("Plan", "AC-mirror-move-tools-4")]
    [Trait("Plan", "AC-mirror-move-tools-10")]
    public void A_game_top_mirrors_symmetrically(string name) => RealGarmentMirrorsSymmetrically(GameTop(name), name);

    [LocalDataTheory(Neolithe)]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    [Trait("Plan", "AC-mirror-move-tools-10")]
    [InlineData("default chest - smallclothes/sfw xs.mdl")]
    public void Neolithe_smallclothes_mirror(string rel)
    {
        var model = ModChest(Neolithe, rel);
        RealGarmentMirrorsSymmetrically(model, "Neolithe " + rel);
        RealBraIsCentredAndStaysCentred(model, "Neolithe " + rel);
    }

    [LocalDataTheory(Bibo)]
    [InlineData(0)]
    [Trait("Plan", "AC-mirror-move-tools-1")]
    [Trait("Plan", "AC-mirror-move-tools-3")]
    [Trait("Plan", "AC-mirror-move-tools-10")]
    public void Bibo_smallclothes_mirror(int _)
    {
        var model = ModChest(Bibo, null);
        RealGarmentMirrorsSymmetrically(model, "Bibo+");
        RealBraIsCentredAndStaysCentred(model, "Bibo+");
    }
}
