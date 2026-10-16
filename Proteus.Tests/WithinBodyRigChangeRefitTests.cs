using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A refit within one body mod whose rig changes: Rue+'s Gen A legs to Gen B (one mesh, B rigs four genital bones A does
/// not) and its plain sizes to Yiggle. Only the change between the two bodies may reach the cloth. The copy rule and the
/// skin hug, which rig cloth on the skin as the new skin outright, are for refits across body mods: on the Starlit Skirt
/// they took up to 0.21 of the thighs' weight off 13,588 skirt vertices, so the legs came through it when posed.
/// </summary>
public class WithinBodyRigChangeRefitTests(ITestOutputHelper output)
{
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Starlit = LocalData.Mods + ":[Nini] Starlit Skirt";
    private const string OldThing = LocalData.Mods + ":This Old Thing - by Solona";

    private const string StarlitWc = "rue sizes/wc/chara/equipment/e6212/model/c0201e6212_dwn.mdl";
    private const string SkirtFabric = "/mt_c0201e6212_dwn_b.mtrl";
    private const string Panties = "/mt_c0201e6212_dwn_c.mtrl";

    /// <summary>The least weight a vertex can carry once written (one step of a byte).</summary>
    private const float Written = 1f / 255f;

    private static string Legs(string option) => $"files/legs - smallclothes/{option}/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private static string Chest(string option) => $"files/chest - smallclothes/{option}/chara/equipment/e0000/model/c0201e0000_top.mdl";

    private sealed record Refit(byte[] Garment, byte[] Model, BodyRetarget.Planned Plan, byte[] Source, byte[] Target,
                                HashSet<string> SourceBones, HashSet<string> TargetBones);

    /// <summary>A refit as the Studio, the size set and the automatic refit run one: BuildPair, then Plan.</summary>
    private static Refit Run(string garmentPath, string slot, string sourcePath, string targetPath, bool pushOut = false)
    {
        byte[] bytes = File.ReadAllBytes(garmentPath);
        Assert.Null(BodyRetarget.BuildPair(slot, sourcePath, targetPath, slot, false, null, null, null, out var pair));
        var plan = BodyRetarget.Plan(ModelPartReader.Read(bytes)!, bytes, [pair], slot, pushOut: pushOut,
                                     held: new HashSet<int>(), replaceSkin: true, acrossBodies: false, cutHidden: true);
        byte[] source = File.ReadAllBytes(sourcePath), target = File.ReadAllBytes(targetPath);
        return new Refit(bytes, plan.Model, plan, source, target,
                         SecondSkinWriter.Parse(source).BoneNames.ToHashSet(StringComparer.Ordinal),
                         SecondSkinWriter.Parse(target).BoneNames.ToHashSet(StringComparer.Ordinal));
    }

    private static Refit StarlitGenAToB()
    {
        string rue = LocalData.Path(Rue);
        return Run(Path.Combine(LocalData.Path(Starlit), StarlitWc), "_dwn",
                   Path.Combine(rue, Legs("yiggle - watermelon crushers - a")),
                   Path.Combine(rue, Legs("yiggle - watermelon crushers - b")));
    }

    /// <summary>[EDGE-within-body-weights-5] The fixture is the garment as shipped, and the refit is a rig change.</summary>
    private static string[] AssertGenAToBFixture(Refit r)
    {
        var genB = r.TargetBones.Except(r.SourceBones).ToArray();
        Assert.NotEmpty(genB);                                                 // B rigs bones A does not
        Assert.Empty(SecondSkinWriter.Parse(r.Garment).BoneNames.Intersect(genB));   // not already refitted in place
        Assert.NotNull(r.Plan.Report.Swap);
        return genB;
    }

    /// <summary>
    /// [AC-within-body-weights-1] [EDGE-within-body-weights-5] Rue+ Yiggle WC Gen A to Gen B, the new body's skin, no
    /// push-out — the user's settings. The two legs weight everything but the crotch alike, so the skirt keeps its
    /// thigh weights.
    /// <para/>
    /// [AC-within-body-weights-2] The new genital bones go where Gen B's skin moves to them — the panties and the pubic
    /// hair at the crotch — and onto no skirt vertex.
    /// </summary>
    [LocalDataFact(Rue, Starlit)]
    public void Starlit_skirt_Gen_A_to_B_keeps_its_thigh_weights_and_puts_the_new_bones_only_on_the_crotch()
    {
        var r = StarlitGenAToB();
        var genB = AssertGenAToBFixture(r);

        var before = Weights(r.Garment);
        var after = Weights(r.Model);
        var pairs = Paired(r.Garment, r.Model, SkirtFabric);
        Assert.NotEmpty(pairs);

        var lost = pairs.Select(p => Sum(before[p.A], "j_asi_a_l", "j_asi_a_r") - Sum(after[p.B], "j_asi_a_l", "j_asi_a_r")).ToList();
        var moved = pairs.Select(p => L1(before[p.A], after[p.B])).ToList();
        output.WriteLine($"skirt vertices {pairs.Count}: thigh weight lost worst {lost.Max():0.000}, " +
                         $"weights changed > 0.1: {moved.Count(m => m > 0.1f)}, worst {moved.Max():0.000}, p95 {P95(moved):0.000}");

        Assert.True(lost.Max() <= 0.05f, $"a skirt vertex lost {lost.Max():0.000} of its thigh weight");
        Assert.Equal(0, moved.Count(m => m > 0.1f));

        // [AC-within-body-weights-2] Gen B's new bones: on the crotch, not the skirt.
        var parts = ModelPartReader.Read(r.Model)!.Parts.Where(p => p.Island < 0).ToList();

        int OnNewBones(Func<string, bool> material)
            => parts.Where(p => material(p.Material)).SelectMany(p => p.Triangles).Distinct()
                    .Count(v => Sum(after[v], genB) >= Written);

        int skirt = OnNewBones(m => m == SkirtFabric);
        int panties = OnNewBones(m => m == Panties);
        int pubes = OnNewBones(BodyRetarget.IsBodyExtraMaterial);
        output.WriteLine($"on Gen B's bones: skirt {skirt}, panties {panties}, pubic hair {pubes}");

        Assert.Equal(0, skirt);
        Assert.True(panties > 0, "the panties took none of Gen B's bones: the change between the bodies never reached the cloth");
        Assert.True(pubes > 0, "the pubic hair took none of Gen B's bones");
    }

    /// <summary>
    /// [AC-within-body-weights-6] [EDGE-within-body-weights-7] Rue+ Yiggle to plain on a top made on Yiggle, the rig
    /// change the within-one-mod rule was written for: the Yiggle bones come off where the body moves weight off them,
    /// the cloth sits no further from its skin's weighting than the author had it, and no new ragged edges appear.
    /// </summary>
    [LocalDataFact(Rue, OldThing)]
    public void Rue_Yiggle_to_plain_chest_takes_the_new_rig_and_keeps_the_author_s_fit()
    {
        string rue = LocalData.Path(Rue);
        string garment = Path.Combine(LocalData.Path(OldThing), "size/rue m/chara/equipment/e6255/model/c0201e6255_top.mdl");
        CheckRigChange(garment, "_top", Path.Combine(rue, Chest("medium")), Path.Combine(rue, Chest("yiggle - medium")),
                       raggedLimit: ChestRaggedLimit);
    }

    /// <summary>
    /// [AC-within-body-weights-6] [EDGE-within-body-weights-6] The Starlit Skirt's legs from Rue+'s Yiggle Gen A, which
    /// it was made on, to plain Gen A: rigged alike, the author's weights stand; rigged apart, the same three checks as
    /// the chest.
    /// </summary>
    [LocalDataFact(Rue, Starlit)]
    public void Rue_Yiggle_to_plain_legs_on_the_Starlit_skirt()
    {
        string rue = LocalData.Path(Rue);
        string garment = Path.Combine(LocalData.Path(Starlit), StarlitWc);
        string plain = Path.Combine(rue, Legs("watermelon crushers - a"));
        string yiggle = Path.Combine(rue, Legs("yiggle - watermelon crushers - a"));
        var plainBones = SecondSkinWriter.Parse(File.ReadAllBytes(plain)).BoneNames.ToHashSet(StringComparer.Ordinal);
        var yiggleBones = SecondSkinWriter.Parse(File.ReadAllBytes(yiggle)).BoneNames.ToHashSet(StringComparer.Ordinal);
        if (plainBones.SetEquals(yiggleBones))
        {
            output.WriteLine("plain and Yiggle legs rig the same bones");
            byte[] bytes = File.ReadAllBytes(garment);
            Assert.Null(BodyRetarget.BuildPair("_dwn", yiggle, plain, "_dwn", false, null, null, null, out var pair));
            Assert.Null(BodyRetarget.PlanWeights(bytes, [pair], acrossBodies: false, before: bytes));
            return;
        }
        CheckRigChange(garment, "_dwn", plain, yiggle, raggedLimit: LegsRaggedLimit);
    }

    /// <summary>Ragged edges the refit added on This Old Thing Rue M (made on Yiggle Medium, onto plain Medium) before
    /// this fix, with the copy rule and the skin hug still running: nothing may get worse.</summary>
    private const int ChestRaggedLimit = 151;

    /// <summary>The same on the Starlit Skirt, Rue+'s Yiggle Gen A legs onto plain Gen A, before this fix.</summary>
    private const int LegsRaggedLimit = 20;

    private void CheckRigChange(string garmentPath, string slot, string plainPath, string yigglePath, int raggedLimit)
    {
        // [EDGE-within-body-weights-7] From the body the garment was made on: the one whose bones it already carries.
        var garmentBones = SecondSkinWriter.Parse(File.ReadAllBytes(garmentPath)).BoneNames.ToHashSet(StringComparer.Ordinal);
        var yiggleOnly = SecondSkinWriter.Parse(File.ReadAllBytes(yigglePath)).BoneNames
                             .Except(SecondSkinWriter.Parse(File.ReadAllBytes(plainPath)).BoneNames).ToList();
        bool madeOnYiggle = yiggleOnly.Any(garmentBones.Contains);
        output.WriteLine($"made on {(madeOnYiggle ? "Yiggle" : "plain")}; refitting onto {(madeOnYiggle ? "plain" : "Yiggle")}");
        // Both fixtures are made on Yiggle: a pick the other way is a misreading, not a refit to measure.
        Assert.True(madeOnYiggle, "the garment carries none of Yiggle's own bones");
        var (sourcePath, targetPath) = (yigglePath, plainPath);

        var r = Run(garmentPath, slot, sourcePath, targetPath);
        Assert.NotNull(r.Plan.Report.Swap);
        var bodyBones = r.SourceBones.Union(r.TargetBones).ToHashSet(StringComparer.Ordinal);
        var added = r.TargetBones.Except(r.SourceBones).Where(b => !BodyRetarget.IsLegBone(b)).ToArray();
        var removed = r.SourceBones.Except(r.TargetBones).Where(b => !BodyRetarget.IsLegBone(b)).ToArray();

        var before = Weights(r.Garment);
        var after = Weights(r.Model);
        var authored = ModelPartReader.Read(r.Garment)!;
        var refitted = ModelPartReader.Read(r.Model)!;
        var oldSkin = new SkinIndex(r.Source);
        var newSkin = new SkinIndex(r.Target);
        var cloth = refitted.Parts.Where(p => p.Island < 0 && IsCloth(p.Material)).Select(p => p.Material).Distinct()
                            .SelectMany(m => Paired(r.Garment, r.Model, m)).ToList();
        Assert.NotEmpty(cloth);

        // (a) The new rig is taken: cloth on skin that moved weight onto (or off) the rig's own bones moved with it.
        int population = 0, followed = 0;
        foreach (var (a, b) in cloth)
        {
            var (skin, far) = newSkin.Nearest(At(refitted, b));
            if (far > BodyRetarget.WeightFull) continue;
            if (added.Length > 0 && Sum(skin, added) >= 0.1f)
            {
                population++;
                if (Sum(after[b], added) >= Written) followed++;
            }
            else if (added.Length == 0 && removed.Length > 0 && Sum(oldSkin.Nearest(At(authored, a)).W, removed) >= 0.1f)
            {
                population++;
                if (Sum(after[b], removed) < 0.05f) followed++;
            }
        }
        output.WriteLine($"(a) cloth over skin the rig changed: {population}, following the new rig: {followed}");

        // (b) No further from the skin's weighting than the author had it, where the cloth stands a little off the skin
        // (inside 5 mm the change against its own skin keeps the author's offset by construction).
        var authorOff = new List<float>();
        var refitOff = new List<float>();
        foreach (var (a, b) in cloth)
        {
            var (oldW, oldFar) = oldSkin.Nearest(At(authored, a));
            if (oldFar >= BodyRetarget.OwnSkinReachDefault && oldFar <= BodyRetarget.CopyReachDefault)
                authorOff.Add(L1(Body(before[a], bodyBones), oldW));
            var (newW, newFar) = newSkin.Nearest(At(refitted, b));
            if (newFar >= BodyRetarget.OwnSkinReachDefault && newFar <= BodyRetarget.CopyReachDefault)
                refitOff.Add(L1(Body(after[b], bodyBones), newW));
        }
        output.WriteLine($"(b) cloth 5-20 mm off the skin vs its weighting, p95: author {P95(authorOff):0.000} " +
                         $"({authorOff.Count}), refit {P95(refitOff):0.000} ({refitOff.Count})");

        // (c) No new ragged edges: a jump over 0.15 on a body bone across a cloth edge the author kept smooth.
        var toRefit = cloth.ToDictionary(p => p.A, p => p.B);
        var edges = new HashSet<(int, int)>();
        foreach (var part in authored.Parts.Where(p => p.Island < 0 && IsCloth(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int u = part.Triangles[t + k], w = part.Triangles[t + (k + 1) % 3];
                    edges.Add(u < w ? (u, w) : (w, u));
                }
        int ragged = edges.Count(e => toRefit.ContainsKey(e.Item1) && toRefit.ContainsKey(e.Item2)
                                      && Jump(before[e.Item1], before[e.Item2], bodyBones) <= 0.15f
                                      && Jump(after[toRefit[e.Item1]], after[toRefit[e.Item2]], bodyBones) > 0.15f);
        output.WriteLine($"(c) new ragged edges: {ragged} (limit {raggedLimit})");

        // All three measured first, so a failure still reports the others.
        Assert.True(population == 0 || followed >= 0.9f * population,
                    $"only {followed} of {population} cloth vertices took the new rig");
        Assert.True(P95(refitOff) <= P95(authorOff) + 0.05f,
                    $"the refit left cloth {P95(refitOff):0.000} off its skin's weighting, the author {P95(authorOff):0.000}");
        Assert.True(ragged <= raggedLimit, $"{ragged} new ragged edges");
    }

    private static bool IsCloth(string material)
        => !SecondSkinWriter.IsBodySkinMaterial(material) && !BodyRetarget.IsBodyExtraMaterial(material)
           && !BodyRetarget.IsBodyAdornmentMaterial(material);

    // ── weights, vertex for vertex ─────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, float>[] Weights(byte[] mdl)
    {
        var skin = ModelSkinReader.Read(mdl, null, null)!;
        var all = new Dictionary<string, float>[skin.VertexCount];
        for (int v = 0; v < skin.VertexCount; v++)
        {
            all[v] = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float w = skin.BoneWeights[v * 8 + k];
                if (w <= 0f) continue;
                string bone = skin.BoneNames[skin.BoneIndices[v * 8 + k]];
                all[v][bone] = all[v].GetValueOrDefault(bone) + w;
            }
        }
        return all;
    }

    /// <summary>The same cloth before and after: each material's parts in order, triangle for triangle.</summary>
    private static List<(int A, int B)> Paired(byte[] before, byte[] after, string material)
    {
        var a = ModelPartReader.Read(before)!.Parts.Where(p => p.Island < 0 && p.Material == material).ToList();
        var b = ModelPartReader.Read(after)!.Parts.Where(p => p.Island < 0 && p.Material == material).ToList();
        Assert.Equal(a.Count, b.Count);
        var pairs = new Dictionary<int, int>();
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Triangles.Length, b[i].Triangles.Length);
            for (int t = 0; t < a[i].Triangles.Length; t++) pairs[a[i].Triangles[t]] = b[i].Triangles[t];
        }
        return pairs.Select(p => (p.Key, p.Value)).ToList();
    }

    private static float Sum(Dictionary<string, float> w, params string[] bones) => bones.Sum(w.GetValueOrDefault);

    private static Dictionary<string, float> Body(Dictionary<string, float> w, HashSet<string> bodyBones)
        => w.Where(i => bodyBones.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

    private static float L1(Dictionary<string, float> a, Dictionary<string, float> b)
        => a.Keys.Union(b.Keys).Sum(k => MathF.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k)));

    private static float Jump(Dictionary<string, float> a, Dictionary<string, float> b, HashSet<string> bodyBones)
        => bodyBones.Max(k => MathF.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k)));

    private static float P95(List<float> values)
        => values.Count == 0 ? 0f : values.OrderBy(v => v).ElementAt((int)MathF.Floor(0.95f * (values.Count - 1)));

    private static Vector3 At(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>A body's skin vertices and their weights, for the nearest one to a point (a 1 cm grid).</summary>
    private sealed class SkinIndex
    {
        private const float Cell = 0.01f;
        private readonly Dictionary<(int, int, int), List<(Vector3 P, Dictionary<string, float> W)>> grid = [];

        public SkinIndex(byte[] body)
        {
            var parts = ModelPartReader.Read(body)!;
            var weights = Weights(body);
            foreach (int v in parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                         .SelectMany(p => p.Triangles).Distinct())
            {
                var p = At(parts, v);
                var key = Key(p);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add((p, weights[v]));
            }
        }

        private static (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        /// <summary>The nearest skin vertex within 3 cm, or an empty reading with an infinite distance.</summary>
        public (Dictionary<string, float> W, float Far) Nearest(Vector3 at)
        {
            var (x, y, z) = Key(at);
            Dictionary<string, float> best = [];
            float far = float.PositiveInfinity;
            for (int i = -3; i <= 3; i++)
                for (int j = -3; j <= 3; j++)
                    for (int k = -3; k <= 3; k++)
                        if (grid.TryGetValue((x + i, y + j, z + k), out var list))
                            foreach (var (p, w) in list)
                            {
                                float d = Vector3.Distance(p, at);
                                if (d < far) { far = d; best = w; }
                            }
            return (best, far);
        }
    }
}
