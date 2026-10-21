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
/// The bone moves measured against the real skeletons (cross-sex-tops-yas): Rue+'s YAS for women, IVCS's and the game's
/// for men, and the Rue+ Yiggle legs, which rig a YAS bone and its IVCS twin both.
/// </summary>
public class SkeletonRemapLocalTests(ITestOutputHelper o)
{
    private const string IvcsWomen = LocalData.Mods
        + ":[IVCS] Illusio Vitae Custom Skeleton Base V1.1 (2)/chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb";

    private static HavokSkeleton Read(string need) => HavokSkeleton.Read(File.ReadAllBytes(LocalData.Path(need)))!;

    /// <summary>
    /// Every counterpart is a YAS bone and an IVCS bone both skeletons place alike, and the vertices each of a pair carries
    /// most of on Rue's Yiggle legs lie on the same side, close together. Every Starlit bone a men's skeleton lacks is
    /// logged with its YAS parents, and every stand-in is checked against the first standard bone above the YAS bone.
    /// </summary>
    [LocalDataFact(CrossSexRefitLocalTests.YasSkeleton, CrossSexRefitLocalTests.IvcsMen, IvcsWomen,
                   CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Starlit, LocalData.GameData)]
    [Trait("Plan", "AC-cross-sex-tops-yas-4")]
    [Trait("Plan", "EDGE-cross-sex-tops-yas-5")]
    public void Yas_parents_counterparts_and_stand_ins_are_measured() // [AC-cross-sex-tops-yas-4] [EDGE-cross-sex-tops-yas-5]
    {
        var yas = Read(CrossSexRefitLocalTests.YasSkeleton);
        var yasParents = yas.ParentMap();
        var ivcsMen = Read(CrossSexRefitLocalTests.IvcsMen).Names.ToHashSet(StringComparer.Ordinal);
        var ivcsWomen = Read(IvcsWomen).Names.ToHashSet(StringComparer.Ordinal);
        using var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var gameMen = HavokSkeleton.Read(data.GetFile(OtherRaceWearer.BaseSkeletonPath(101))!.Data)!.Names
                                   .ToHashSet(StringComparer.Ordinal);

        string Chain(string bone)
        {
            var chain = new List<string> { bone };
            for (string? at = yasParents.GetValueOrDefault(bone); at != null && chain.Count < 32; at = yasParents.GetValueOrDefault(at))
                chain.Add(at);
            return string.Join(" → ", chain);
        }

        var starlit = ModelSkinReader.Read(File.ReadAllBytes(LocalData.Path(CrossSexRefitLocalTests.Starlit)), null, null)!;
        foreach (string bone in OtherRaceRefit.Weighted(starlit).Order(StringComparer.Ordinal))
            if (!gameMen.Contains(bone) || !ivcsMen.Contains(bone))
                o.WriteLine($"{bone}: game c0101 {(gameMen.Contains(bone) ? "has" : "lacks")}, IVCS c0101 "
                          + $"{(ivcsMen.Contains(bone) ? "has" : "lacks")}; YAS {Chain(bone)}");

        foreach (var (from, to) in SkeletonRemap.Counterparts)
        {
            Assert.Contains(from, yas.Names);
            Assert.Contains(to, yas.Names);
            Assert.Contains(to, ivcsWomen);
            Assert.Contains(to, ivcsMen);
        }

        // Centroids of the vertices each bone carries most of, on the legs that rig both.
        string legs = Path.Combine(LocalData.Path(CrossSexRefitLocalTests.Rue), "files", "legs - smallclothes",
                                   CrossSexRefitLocalTests.RueYiggleWc.TrimEnd('/'), "chara", "equipment", "e0000", "model",
                                   "c0201e0000_dwn.mdl");
        var body = ModelSkinReader.Read(File.ReadAllBytes(legs), null, null)!;
        // Weighted by how much of each vertex the bone carries: neither buttock bone is the heaviest anywhere on these legs.
        Vector3? Centroid(string bone)
        {
            var sum = Vector3.Zero;
            float total = 0f;
            for (int v = 0; v < body.VertexCount; v++)
                for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
                {
                    float w = body.BoneWeights[v * 8 + k];
                    if (w <= 0f || body.BoneNames[body.BoneIndices[v * 8 + k]] != bone) continue;
                    sum += body.Positions[v] * w;
                    total += w;
                }
            return total <= 0f ? null : sum / total;
        }
        foreach (var (from, to) in SkeletonRemap.Counterparts)
        {
            var a = Centroid(from);
            var b = Centroid(to);
            o.WriteLine($"{from} centroid {a}, {to} centroid {b}");
            Assert.True(a != null && b != null, $"{from} or {to} carries no vertex on the Yiggle legs");
            Assert.Equal(MathF.Sign(a!.Value.X), MathF.Sign(b!.Value.X));
            // 6 cm, the user's decision: YAS's lower-buttock bone sits 5.5 cm under IVCS's on the same buttock (2026-10-21).
            Assert.True(Vector3.Distance(a.Value, b.Value) <= 0.06f, $"{from} and {to} are {Vector3.Distance(a.Value, b.Value) * 100f:F1} cm apart");
        }

        // A stand-in for a breast or thigh bone keeps the part of the body its first standard ancestor in YAS is in.
        int checkedStandIns = 0;
        foreach (string bone in yas.Names.Where(b => !b.StartsWith("j_", StringComparison.Ordinal) && !b.StartsWith("n_", StringComparison.Ordinal)))
        {
            if (SkeletonRemap.StandIn(bone) is not { } stand) continue;
            string? standard = yasParents.GetValueOrDefault(bone);
            while (standard != null && !(standard.StartsWith("j_", StringComparison.Ordinal) || standard.StartsWith("n_", StringComparison.Ordinal)))
                standard = yasParents.GetValueOrDefault(standard);
            o.WriteLine($"{bone}: stand-in {stand}, first standard ancestor {standard}");
            if (standard == null) continue;
            checkedStandIns++;
            // The stand-in is in the part of the body YAS hangs the bone in, or the part beside it; a breast or thigh
            // bone's in that very part.
            var want = UvBakeBones.RegionOf(standard);
            var got = UvBakeBones.RegionOf(stand);
            if (bone.Contains("mune") || bone.Contains("kyokin") || bone.Contains("daitai"))
                Assert.True(want == got, $"{bone}'s stand-in {stand} ({got}) is not where YAS hangs it, under {standard} ({want})");
            else
                Assert.True(UvBakeBones.Adjacent(want, got),
                            $"{bone}'s stand-in {stand} ({got}) is far from where YAS hangs it, under {standard} ({want})");
        }
        Assert.True(checkedStandIns >= 20, $"only {checkedStandIns} stand-ins checked: the YAS skeleton read too few parents");
    }
}
