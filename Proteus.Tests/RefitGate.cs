using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The quality gates a refit across the sexes must pass on real data (the plan's G1–G6 and G5b), measured on the model
/// the save writes and inside the pass.
/// </summary>
internal static class RefitGate
{
    /// <summary>G2: cloth folds over the same garment's same-sex refit.</summary>
    internal const int FoldsOverBaseline = 5, FoldsFloor = 10;

    /// <summary>G4: how the maps matched the two bodies.</summary>
    internal const float MinCoverage = 0.95f, MaxP99Shift = 0.12f;

    /// <summary>G6: p95 cloth standoff over the made-for one, metres.</summary>
    internal const float StandoffSlack = 0.008f;

    /// <summary>G3: share of near cloth more than 2 mm inside the body, over the made-for share.</summary>
    internal const float InsideSlack = 0.01f, InsideDepth = 0.002f;

    /// <summary>
    /// G5b: near cloth whose dominant bone's region is the body's there, against the same garment on the bodies it was
    /// made for, less this. Relative, like the other fit gates: a garment's own rig disagrees with its made-for body
    /// where regions meet (97% on the TBSE trousers), and an absolute bar would measure its author, not the refit.
    /// </summary>
    internal const float RegionSlack = 0.01f;

    internal sealed record Measured(int Folds, int Baseline, float Coverage, int Astray, float P99Shift,
                                    float PreviewOff, int Collapsed, float RoundTrip, IReadOnlyList<string> ForeignSkins,
                                    IReadOnlyList<string> MissingBones, float StandoffP95, float StandoffP50,
                                    float MadeForP95, float Inside, float MadeForInside, float RegionAgreement,
                                    float MadeForRegionAgreement);

    /// <summary>Which gates a case asserts; the rest are only logged. The shipped cells assert all of them.</summary>
    [Flags]
    internal enum Gates
    {
        None = 0, Folds = 1, Inside = 2, Regions = 4, Standoff = 8,
        All = Folds | Inside | Regions | Standoff,
    }

    /// <summary>Measure every gate and assert it, logging all of them first so a failure shows the rest.</summary>
    /// <param name="gates">Which of G2, G3, G5b and G6 are asserted (G1, G4 and G5 always are): a refit across the sexes
    /// is never refused for its folds (cross-sex-tops-yas), so the new cases log G2.</param>
    internal static Measured Assert(ITestOutputHelper o, CrossSexRefitJob.Result r, CrossSexRefitJob.Size s, int baseline,
                                    Gates gates = Gates.All, float minCoverage = MinCoverage)
    {
        var m = Measure(r, s, baseline);
        o.WriteLine($"G1 preview {m.PreviewOff * 1000f:F3} mm off, {m.Collapsed} collapsed, round trip {m.RoundTrip * 1000f:F3} mm; "
                  + $"G2 folds {m.Folds} (baseline {m.Baseline}); G3 inside {m.Inside:P2} (made for {m.MadeForInside:P2}); "
                  + $"G4 coverage {m.Coverage:P1}, astray {m.Astray}, p99 {m.P99Shift * 1000f:F1} mm; "
                  + $"G5 foreign skins [{string.Join(", ", m.ForeignSkins)}], missing bones [{string.Join(", ", m.MissingBones)}]; "
                  + $"G5b regions {m.RegionAgreement:P1} (made for {m.MadeForRegionAgreement:P1}); G6 standoff p50 {m.StandoffP50 * 1000f:F1} / p95 {m.StandoffP95 * 1000f:F1} mm "
                  + $"(made for p95 {m.MadeForP95 * 1000f:F1} mm)");

        Xunit.Assert.True(m.PreviewOff < 5e-4f, $"G1: the preview shows {m.PreviewOff * 1000f:F3} mm off the saved model");
        Xunit.Assert.Equal(0, m.Collapsed);
        Xunit.Assert.True(m.RoundTrip < 5e-4f, $"G1: the bend there and back moves {m.RoundTrip * 1000f:F3} mm");
        int maxFolds = Math.Max(baseline + FoldsOverBaseline, FoldsFloor);
        if (gates.HasFlag(Gates.Folds))
            Xunit.Assert.True(m.Folds <= maxFolds, $"G2: {m.Folds} cloth faces turned over, more than {maxFolds}");
        if (gates.HasFlag(Gates.Inside))
            Xunit.Assert.True(m.Inside <= m.MadeForInside + InsideSlack,
                              $"G3: {m.Inside:P2} of near cloth inside the body, made for {m.MadeForInside:P2}");
        Xunit.Assert.True(m.Coverage >= minCoverage, $"G4: coverage {m.Coverage:P1}");
        Xunit.Assert.Equal(0, m.Astray);
        Xunit.Assert.True(m.P99Shift <= MaxP99Shift, $"G4: p99 shift {m.P99Shift * 1000f:F1} mm");
        Xunit.Assert.Empty(m.ForeignSkins);
        Xunit.Assert.Empty(m.MissingBones);
        if (gates.HasFlag(Gates.Regions))
            Xunit.Assert.True(m.RegionAgreement >= m.MadeForRegionAgreement - RegionSlack,
                              $"G5b: regions agree for {m.RegionAgreement:P1}, made for {m.MadeForRegionAgreement:P1}");
        if (gates.HasFlag(Gates.Standoff))
            Xunit.Assert.True(m.StandoffP95 <= m.MadeForP95 + StandoffSlack,
                              $"G6: standoff p95 {m.StandoffP95 * 1000f:F1} mm, made for {m.MadeForP95 * 1000f:F1} mm");
        return m;
    }

    /// <summary>G5c: mean share of a cloth vertex's weight on bones the new body does not rig, near the body.</summary>
    internal const float MaxStrangerMean = 0.02f, MaxStrangerOnSkin = 0.05f;

    /// <summary>
    /// G5c, for a garment whose bones were moved (cross-sex-tops-yas): over its cloth within <see cref="BodyRetarget.NearBand"/>
    /// of the new body, the weight on bones the bodies it was made on name but neither the new body nor the garment itself
    /// rigs — the old body's share left behind where cloth and skin would part when posed. The mean, and the most on any
    /// vertex within
    /// <see cref="BodyRetarget.SkinHugFull"/> of the skin.
    /// </summary>
    internal static (float Mean, float MostOnSkin, IReadOnlySet<string> Bones) Strangers(CrossSexRefitJob.Result r, CrossSexRefitJob.Size s)
    {
        var cloth = ModelSkinReader.Read(s.Saved!.Model, null, null)!;
        var targetBytes = File.ReadAllBytes(s.TargetPath);
        var targetBones = OtherRaceRefit.Weighted(ModelSkinReader.Read(targetBytes, null, null)!).ToHashSet(StringComparer.Ordinal);
        // Not the garment's own bones: a skirt its author rigged to IVCS's buttocks keeps them on a man with IVCS, on
        // purpose (its physics), as it would on any body that does not rig them.
        var own = OtherRaceRefit.Weighted(ModelSkinReader.Read(r.Baked!, null, null)!).ToHashSet(StringComparer.Ordinal);
        var stranger = OtherRaceRefit.Weighted(ModelSkinReader.Read(File.ReadAllBytes(r.SourcePath!), null, null)!)
                                     .Where(b => !targetBones.Contains(b) && !own.Contains(b)).ToHashSet(StringComparer.Ordinal);
        var surface = new BodySurface(ModelPartReader.Read(targetBytes)!, 0.01f);
        float sum = 0f, most = 0f;
        int near = 0;
        var seen = new HashSet<int>();
        for (int t = 0; t < cloth.TriangleCount; t++)
        {
            if (SecondSkinWriter.IsBodySkinMaterial(cloth.MaterialNames[cloth.TriangleMaterials[t]])) continue;
            for (int k = 0; k < 3; k++)
            {
                int v = cloth.Triangles[t * 3 + k];
                if (!seen.Add(v) || !surface.Nearest(cloth.Positions[v], BodyRetarget.NearBand, out var hit)) continue;
                float w = 0f;
                for (int i = 0; i < SkinnedMesh.MaxInfluences; i++)
                {
                    int at = v * SkinnedMesh.MaxInfluences + i;
                    if (cloth.BoneWeights[at] > 0f && stranger.Contains(cloth.BoneNames[cloth.BoneIndices[at]]))
                        w += cloth.BoneWeights[at];
                }
                sum += w;
                near++;
                if (hit.Distance <= BodyRetarget.SkinHugFull) most = MathF.Max(most, w);
            }
        }
        return (near == 0 ? 0f : sum / near, most, stranger);
    }

    internal static Measured Measure(CrossSexRefitJob.Result r, CrossSexRefitJob.Size s, int baseline)
    {
        var who = r.Wearer!;
        var saved = s.Saved!.Model;
        var savedParts = ModelPartReader.Read(saved)!;
        var savedSkin = ModelSkinReader.Read(saved, null, null)!;
        Xunit.Assert.Equal(ModelPartReader.Read(s.Plan!.Model)!.Positions.Length, savedParts.Positions.Length);

        // G1: an up route's preview, bent forward by the game's bones as named, lands on the saved model; a lowered
        // route's preview is the saved model bent back (by alias, as its garment was). Either way no face collapses.
        float previewOff = 0f;
        int collapsed = 0;
        if (s.Preview is { } preview)
        {
            var previewParts = ModelPartReader.Read(preview)!;
            if (r.Route.Lowered)
            {
                // Previewed on the drawn path, where the drawn race sees it unbent: the saved model bent back by alias.
                var bentBack = RacialModelBake.Bake(saved, r.Route.To, r.Route.Drawn, who.Pbd, who.ParentOf, out _, UvBakeBones.Alias);
                previewOff = bentBack == null ? float.MaxValue : MaxOff(ModelPartReader.Read(bentBack)!.Positions, previewParts.Positions);
            }
            else
            {
                var shown = RacialModelBake.Bake(preview, r.Route.Drawn, r.Route.To, who.Pbd, who.ParentOf, out _);
                previewOff = shown == null ? float.MaxValue : MaxOff(savedParts.Positions, ModelPartReader.Read(shown)!.Positions);
            }
            collapsed = RacialRouteLocalTests.Collapsed(savedParts, previewParts);
        }
        // The bend's maths: the saved model (in the picked race's shape) to the drawn race's and back, by alias.
        ushort to = r.Route.To, drawn = r.Route.Drawn;
        var there = r.Route.Lowered
            ? RacialModelBake.Bake(saved, to, drawn, who.Pbd, who.ParentOf, out _, UvBakeBones.Alias)
            : RacialModelBake.Unbake(saved, drawn, to, who.Pbd, who.ParentOf, out _, UvBakeBones.Alias);
        var back = there == null ? null
                 : r.Route.Lowered
                     ? RacialModelBake.Unbake(there, to, drawn, who.Pbd, who.ParentOf, out _, UvBakeBones.Alias)
                     : RacialModelBake.Bake(there, drawn, to, who.Pbd, who.ParentOf, out _, UvBakeBones.Alias);
        float roundTrip = back == null ? float.MaxValue : MaxOff(savedParts.Positions, ModelPartReader.Read(back)!.Positions);

        // G5: skin drawn with the new body's materials; every bone leaned on is the wearer's, or the new body's own — a body
        // that rigs its skin to physics bones (TBSE-X's IVCS iv_*_phys) comes with the skeleton that has them, whatever
        // base skeleton file the wearer was read from.
        var targetBytes = File.ReadAllBytes(s.TargetPath);
        var targetSkins = SecondSkinWriter.Parse(targetBytes).MatNames.Where(SecondSkinWriter.IsBodySkinMaterial).ToHashSet();
        var foreign = SecondSkinWriter.Parse(saved).MatNames
                                      .Where(m => SecondSkinWriter.IsBodySkinMaterial(m) && !targetSkins.Contains(m)).ToList();
        var bodyOwn = OtherRaceRefit.Weighted(ModelSkinReader.Read(targetBytes, null, null)!).ToHashSet(StringComparer.Ordinal);
        var missing = OtherRaceRefit.Weighted(savedSkin).Where(b => !who.Parents.ContainsKey(b) && !bodyOwn.Contains(b)).ToList();

        // G3, G6, G5b on the cloth near the body, against the new body; the baseline against the made-for body bent.
        var target = ModelPartReader.Read(targetBytes)!;
        var targetSkin = ModelSkinReader.Read(targetBytes, null, null)!;
        var (p95, p50, inside, agree) = Fit(savedSkin, target, targetSkin);
        var madeForBytes = File.ReadAllBytes(r.SourcePath!);
        var (mp95, _, mInside, mAgree) = Fit(ModelSkinReader.Read(r.Baked!, null, null)!, ModelPartReader.Read(madeForBytes)!,
                                        ModelSkinReader.Read(madeForBytes, null, null)!);

        var l = s.Landed ?? default;
        return new Measured(s.Folds, baseline, l.VertexCoverage, l.Astray, l.P99Shift, previewOff, collapsed, roundTrip,
                            foreign, missing, p95, p50, mp95, inside, mInside, agree, mAgree);
    }

    /// <summary>
    /// Over the cloth vertices within <see cref="BodyRetarget.NearBand"/> of the body: the standoff's p95 and p50, the
    /// share more than <see cref="InsideDepth"/> inside, and how often the cloth's dominant bone votes for the region of
    /// the body vertex nearest it (where both vote).
    /// </summary>
    internal static (float P95, float P50, float Inside, float Agreement) Fit(SkinnedMesh cloth, ModelParts body, SkinnedMesh bodySkin)
    {
        var surface = new BodySurface(body, 0.01f);
        var clothVerts = new HashSet<int>();
        for (int t = 0; t < cloth.TriangleCount; t++)
        {
            if (SecondSkinWriter.IsBodySkinMaterial(cloth.MaterialNames[cloth.TriangleMaterials[t]])) continue;
            for (int k = 0; k < 3; k++) clothVerts.Add(cloth.Triangles[t * 3 + k]);
        }
        var standoffs = new List<float>();
        int inside = 0, voted = 0, agreed = 0;
        foreach (int v in clothVerts)
        {
            var p = cloth.Positions[v];
            if (!surface.Nearest(p, BodyRetarget.NearBand, out var hit)) continue;
            float d = Vector3.Dot(p - hit.Point, hit.Normal);
            standoffs.Add(MathF.Abs(d));
            if (d < -InsideDepth) inside++;
            int nearest = hit.U >= hit.V && hit.U >= hit.W ? hit.A : hit.V >= hit.W ? hit.B : hit.C;
            var a = UvBakeBones.RegionOf(Dominant(cloth, v));
            var b = nearest < bodySkin.VertexCount ? UvBakeBones.RegionOf(Dominant(bodySkin, nearest)) : UvBakeBones.Region.None;
            if (a == UvBakeBones.Region.None || b == UvBakeBones.Region.None) continue;
            voted++;
            if (a == b) agreed++;
        }
        if (standoffs.Count == 0) return (0f, 0f, 0f, 1f);
        standoffs.Sort();
        return (standoffs[(int)(standoffs.Count * 0.95f)], standoffs[standoffs.Count / 2],
                inside / (float)standoffs.Count, voted == 0 ? 1f : agreed / (float)voted);
    }

    private static string Dominant(SkinnedMesh skin, int v)
    {
        int best = 0;
        for (int k = 1; k < SkinnedMesh.MaxInfluences; k++)
            if (skin.BoneWeights[v * SkinnedMesh.MaxInfluences + k] > skin.BoneWeights[v * SkinnedMesh.MaxInfluences + best])
                best = k;
        int bone = skin.BoneIndices[v * SkinnedMesh.MaxInfluences + best];
        return bone < skin.BoneNames.Length ? skin.BoneNames[bone] : "";
    }

    private static float MaxOff(float[] a, float[] b)
    {
        float off = 0f;
        for (int v = 0; v < Math.Min(a.Length, b.Length) / 3; v++)
            off = MathF.Max(off, Vector3.Distance(new(a[v * 3], a[v * 3 + 1], a[v * 3 + 2]),
                                                  new(b[v * 3], b[v * 3 + 1], b[v * 3 + 2])));
        return off;
    }
}
