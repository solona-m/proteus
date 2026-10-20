using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using XivLiveMesh;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Step 0 of the bibo↔TBSE bake: the facts about the real Bibo+ and TBSE meshes the bake rests on, measured before the
/// maps are trusted. Each prints what it measured.
/// </summary>
public class UvBakeStep0LocalTests(ITestOutputHelper output)
{
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Tbse = LocalData.Mods + ":The Body SE";

    private static (PbdFile Pbd, Func<string, string?> ParentOf, HashSet<string> Bones) Game()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var pbd = new PbdFile(data.GetFile(OtherRaceWearer.PbdPath)!.Data);
        var skel = HavokSkeleton.Read(data.GetFile(OtherRaceWearer.BaseSkeletonPath(101))!.Data)!;
        var parents = skel.ParentMap();
        return (pbd, b => parents.GetValueOrDefault(b), skel.Names.ToHashSet(StringComparer.Ordinal));
    }

    private static Dictionary<string, byte[]> BiboFiles() => UvBakeInputs.Read(LocalData.Path(Bibo), UvBakeInputs.BiboFiles());
    private static Dictionary<string, byte[]> TbseFiles() => UvBakeInputs.Read(LocalData.Path(Tbse), UvBakeInputs.TbseFiles());

    /// <summary>Of the triangles off the midline, how many sit on the sheet half of their own side (+X → right half).</summary>
    private static (double PlusRight, double PlusLeft, int Counted) SideHalf(BakeBody body)
    {
        int right = 0, left = 0, counted = 0;
        for (int t = 0; t < body.TriangleCount; t++)
        {
            var c = (body.P[body.Tri[t * 3]] + body.P[body.Tri[t * 3 + 1]] + body.P[body.Tri[t * 3 + 2]]) / 3f;
            if (MathF.Abs(c.X) < 0.01f) continue;
            float u = (body.Uv[t * 3].X + body.Uv[t * 3 + 1].X + body.Uv[t * 3 + 2].X) / 3f;
            counted++;
            bool plus = c.X > 0, rightHalf = u >= 0.5f;
            if (plus == rightHalf) right++; else left++;
        }
        return ((double)right / counted, (double)left / counted, counted);
    }

    private static int Covered(BakeBody body, int size)
        => UvTransferBake.Rasterize(body, size).Owner.Count(o => o >= 0);

    [LocalDataFact(LocalData.GameData, Bibo, Tbse)]
    [Trait("Plan", "AC-tbse-uvmap-2")]
    public void Measure_bodies() // [AC-tbse-uvmap-2]
    {
        var (pbd, parentOf, bones) = Game();
        var bibo = BiboFiles();
        var tbse = TbseFiles();

        foreach (var (name, files) in new[] { ("Bibo+", bibo), ("TBSE", tbse) })
            foreach (var (rel, bytes) in files)
            {
                var part = UvBakeInputs.Part(bytes);
                if (part == null) { output.WriteLine($"{name} {rel}: NO SKIN"); continue; }
                var body = new BakeBody([part]);
                var lo = body.P.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var hi = body.P.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                var uvLo = body.Uv.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
                var uvHi = body.Uv.Aggregate(new Vector2(float.MinValue), Vector2.Max);
                var (pr, pl, n) = SideHalf(body);
                output.WriteLine($"{name} {rel}: {body.P.Length} v, {body.TriangleCount} t, pos {lo}..{hi}, uv {uvLo}..{uvHi}, "
                                 + $"+X→right {pr:P1} +X→left {pl:P1} of {n}, rim {body.Rim.SegmentCount}, cover@1024 {Covered(body, 1024)}");
                foreach (var g in body.Rim.Segments.GroupBy(s => (MathF.Round((s.A.Y + s.B.Y) * 50f) / 100f,
                                                                  MathF.Sign((s.A.X + s.B.X) * 0.5f))))
                    output.WriteLine($"    rim y≈{g.Key.Item1:F2} side {g.Key.Item2}: {g.Count()} edges, "
                                     + $"x {g.Min(s => s.A.X):F3}..{g.Max(s => s.A.X):F3} z {g.Min(s => s.A.Z):F3}..{g.Max(s => s.A.Z):F3}");
                foreach (var (bone, count, how) in UvBakeBones.Report(part.Weights, bones.Contains))
                    if (how != UvBakeBones.Resolution.Own)
                        output.WriteLine($"    bone {bone}: {count} v, {how}");
            }
    }
}
