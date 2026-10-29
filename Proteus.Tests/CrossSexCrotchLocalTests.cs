using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// The torn crotch on women's legs made men's (mens-ends-crotch). Stormblessed's Neo-Ishgardian bottoms, made on nude
/// LavaBod+ 2.2, fitted to TBSE's Gen A legs: the user saw a hole in the thong over the bulge, and folded cloth faces are
/// back-face culled in game. Measured per stage of the solve, inside and outside the crotch field (see <see cref="Field"/>).
/// </summary>
[Trait("Speed", "Slow")]
public class CrossSexCrotchLocalTests(ITestOutputHelper o)
{
    private static string TbsePath(int i)
        => Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Tbse), UvBakeInputs.TbseLegs[i].Rel);

    /// <summary>The crotch field about TBSE's own crotch: what the drape may move.</summary>
    internal readonly record struct Field(float MidX, float Y, float Scale)
    {
        internal bool Holds(Vector3 p)
            => MathF.Abs(p.X - MidX) <= 0.06f * Scale && p.Y >= Y - 0.05f * Scale && p.Y <= Y + 0.14f * Scale;
    }

    internal static Field CrotchOf(string bodyPath)
    {
        var bytes = File.ReadAllBytes(bodyPath);
        var model = ModelPartReader.Read(bytes)!;
        var skin = ModelSkinReader.Read(bytes, null, null)!;
        var a = BodyRetarget.FindCrotch([(model, skin)]);
        Assert.NotNull(a);
        return new Field(a!.Value.MidX, a.Value.Y, a.Value.Scale);
    }

    /// <summary>The cloth nodes: the garment's own skin is swapped out and never counted.</summary>
    internal static bool[] ClothOf(BodyRetarget.Sets sets)
    {
        var cloth = new bool[sets.NodeCount];
        foreach (int n in sets.ClothNodes) cloth[n] = true;
        return cloth;
    }

    private static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 At(BodyRetarget.Sets sets, Vec3[] delta, int n) => V(sets.NodeAt[n]) + V(delta[n]);

    /// <summary>Folded cloth triangles of one stage, in the field and out of it, by where they now lie.</summary>
    internal static (int In, int Out) Folds(BodyRetarget.Sets sets, Vec3[] delta, Field f)
    {
        var cloth = ClothOf(sets);
        int inside = 0, outside = 0;
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
            if (va < 0 || vb < 0 || vc < 0 || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length)
                continue;
            int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
            if (a == b || b == c || c == a || !cloth[a] || !cloth[b] || !cloth[c]) continue;
            var n0 = Vector3.Cross(V(sets.NodeAt[b]) - V(sets.NodeAt[a]), V(sets.NodeAt[c]) - V(sets.NodeAt[a]));
            if (n0.Length() <= 1e-12f) continue;
            var pa = At(sets, delta, a);
            var n1 = Vector3.Cross(At(sets, delta, b) - pa, At(sets, delta, c) - pa);
            if (Vector3.Dot(n0, n1) >= 0f) continue;
            if (f.Holds((pa + At(sets, delta, b) + At(sets, delta, c)) / 3f)) inside++;
            else outside++;
        }
        return (inside, outside);
    }

    /// <summary>The worst stretch of a cloth edge in the field, against its authored length.</summary>
    internal static float Stretch(BodyRetarget.Sets sets, Vec3[] delta, Field f)
    {
        var cloth = ClothOf(sets);
        float worst = 1f;
        for (int n = 0; n < sets.NodeCount; n++)
        {
            if (!cloth[n] || !f.Holds(At(sets, delta, n))) continue;
            foreach (int m in sets.Adj[n])
            {
                if (!cloth[m]) continue;
                float was = Vector3.Distance(V(sets.NodeAt[n]), V(sets.NodeAt[m]));
                if (was < 1e-5f) continue;
                worst = MathF.Max(worst, Vector3.Distance(At(sets, delta, n), At(sets, delta, m)) / was);
            }
        }
        return worst;
    }

    /// <summary>Field nodes within 2 cm of the drawn skin and more than 2 mm inside it.</summary>
    internal static int Inside(BodyRetarget.Sets sets, Vec3[] delta, Field f, BodySurface skin)
    {
        var cloth = ClothOf(sets);
        int count = 0;
        for (int n = 0; n < sets.NodeCount; n++)
        {
            if (!cloth[n]) continue;
            var p = At(sets, delta, n);
            if (!f.Holds(p) || !skin.Nearest(p, 0.02f, out var hit)) continue;
            if (Vector3.Dot(p - hit.Point, hit.Normal) < -0.002f) count++;
        }
        return count;
    }

    /// <summary>
    /// Phase 0, measured before the drape: per stage of the solve, the cloth folded in and out of the field, the field's
    /// worst edge stretch and cloth inside the skin; the folds the racial bend itself made; the summed against the net
    /// worst push. Rules the drape answers to are read from these (see the plan, mens-ends-crotch Phase 0).
    /// </summary>
    [LocalDataFact(LocalData.GameData, CrossSexNudeLegsLocalTests.StormblessedLegs, CrossSexNudeLegsLocalTests.LavaBod,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-mens-ends-crotch-7")]
    [Trait("Plan", "EDGE-mens-ends-crotch-29")]
    [Trait("Plan", "EDGE-mens-ends-crotch-35")]
    public void Stormblessed_crotch_by_stage() // [AC-mens-ends-crotch-7] [EDGE-mens-ends-crotch-29] [EDGE-mens-ends-crotch-35]
    {
        var off = Measure(drape: false);
        var on = Measure(drape: true);
        Assert.Equal(0, off.BendIn);
        Assert.Equal(0, on.FieldFolds);
        Assert.True(on.OutFolds <= off.OutFolds, $"{on.OutFolds} folds outside the field, {off.OutFolds} without the drape");
        Assert.True(on.Inside <= off.Inside, $"{on.Inside} field points inside the body, {off.Inside} without the drape");
    }

    private sealed record Measured(int FieldFolds, int OutFolds, int Inside, float Stretch, int BendIn, int Folds);

    private Measured Measure(bool drape)
    {
        o.WriteLine(drape ? "── drape on ──" : "── drape off ──");
        var field = CrotchOf(TbsePath(1));
        o.WriteLine($"crotch field: mid x {field.MidX:F3}, y {field.Y:F3}, scale {field.Scale:F2}");
        var genA = ModelPartReader.Read(File.ReadAllBytes(TbsePath(1)))!;
        var skin = new BodySurface(genA, BodySurface.CellFor(0.01f));

        var stages = new List<(string Name, BodyRetarget.Sets Sets, Vec3[] Delta)>();
        var log = Substitute.For<IPluginLog>();
        CrossSexRefitJob.Result r;
        BodyRetarget.PushTrace.Value = (k, v) =>
        {
            if (k == "drape-round") o.WriteLine($"  drape round {v[0]}: {v[1]} folds (of {v[4]} before, patch {v[5]}), counts {v[2]}, worst {v[3] * 1000f:F1} mm");
        };
        BodyRetarget.StageTrace.Value = (name, sets, delta) =>
        {
            if (name == "transfer") stages.Clear();
            stages.Add((name, sets, delta));
        };
        try
        {
            r = CrossSexRefitJob.Run(CrossSexNudeLegsLocalTests.JobInputs(CrossSexNudeLegsLocalTests.StormblessedLegs,
                                                                         CrossSexNudeLegsLocalTests.LavaBod, 1, log)
                                     with { DrapeCrotch = drape });
        }
        finally { BodyRetarget.StageTrace.Value = null; BodyRetarget.PushTrace.Value = null; }

        var s = Assert.Single(r.Sizes);
        Assert.NotEmpty(stages);
        foreach (var (name, sets, delta) in stages)
        {
            var (inF, outF) = Folds(sets, delta, field);
            o.WriteLine($"{name,-12} folds in field {inF,4}, out {outF,4}; stretch {Stretch(sets, delta, field):F2}x; " +
                        $"inside {Inside(sets, delta, field, skin)}");
        }

        // The bend's own folds: the garment as authored against the garment bent for a man, before any refit.
        var raw = ModelPartReader.Read(File.ReadAllBytes(LocalData.Path(CrossSexNudeLegsLocalTests.StormblessedLegs)))!;
        var baked = ModelPartReader.Read(r.Baked!)!;
        int bendIn = 0, bendOut = 0;
        foreach (var part in raw.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                Vector3 P(float[] pos, int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                var was = Vector3.Cross(P(raw.Positions, b) - P(raw.Positions, a), P(raw.Positions, c) - P(raw.Positions, a));
                if (was.Length() * 0.5f < 1e-6f) continue;
                var now = Vector3.Cross(P(baked.Positions, b) - P(baked.Positions, a), P(baked.Positions, c) - P(baked.Positions, a));
                if (Vector3.Dot(was, now) > 0f) continue;
                if (field.Holds((P(baked.Positions, a) + P(baked.Positions, b) + P(baked.Positions, c)) / 3f)) bendIn++;
                else bendOut++;
            }
        o.WriteLine($"the bend's own folds: in field {bendIn}, out {bendOut}");

        // The worst push as reported, against how far any node actually moved across the push stage and after it.
        var follow = stages.Last(st => st.Name == "follow").Delta;
        var final = stages[^1].Delta;
        float net = 0f;
        for (int n = 0; n < final.Length; n++) net = MathF.Max(net, Vector3.Distance(V(final[n]), V(follow[n])));
        o.WriteLine($"reported worst push {s.Plan!.Report.WorstPush * 1000f:F1} mm, worst move {s.Plan.Report.WorstMove * 1000f:F1} mm, " +
                    $"largest move from before the push to the end {net * 1000f:F1} mm; job folds {s.Folds}, draped {s.Plan.Report.Draped}");
        var (fin, fout) = Folds(stages[^1].Sets, final, field);
        return new Measured(fin, fout, Inside(stages[^1].Sets, final, field, skin), Stretch(stages[^1].Sets, final, field),
                            bendIn, s.Folds);
    }
}
