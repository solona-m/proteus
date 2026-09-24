using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// One reported refit, kept as an instrument: "BiboPlus Sheer Elegance" from Bibo+ Perky Small onto Neolithe
/// DEFAULT ALMOND · NSFW Almond XS, which is the pair the mod's own record names.
/// <para/>
/// What makes this case worth keeping is the comparison it allows. The mod ships the same garment in four chest sizes
/// its author fitted by hand, so for every number here there is an answer a person arrived at: the author's own file
/// is printed beside the refit's, and the refit has to reach it. Three separate faults were found this way and each
/// measurement below is the one that found one — instruments, not assertions, in the house diag style, and they skip
/// silently when the mods are not installed.
/// <para/>
/// Measuring notes that cost time to learn, so the next reading of these numbers does not have to:
/// <list type="bullet">
/// <item><see cref="ModelParts.Parts"/> holds whole submeshes AND island subsets of the same geometry. Walking all of
/// them counts every triangle twice — which turned 95 folded triangles into a reported 190 and sent a whole
/// investigation sideways. Every walk here takes <c>Island &lt; 0</c> only.</item>
/// <item>Sign a distance by the SKIN's normal, never the cloth's. The cloth is a sheer double-sided sheet whose
/// normals face both ways, and a sign taken from it means nothing — it read "nothing is buried" over a cup that was
/// visibly buried.</item>
/// <item>Measure against the body the FILE carries, not against the body mod's file. With the skin swapped those are
/// different surfaces, and the difference between them was the bug.</item>
/// </list>
/// </summary>
public class SheerEleganceDiagTests(ITestOutputHelper output)
{
    private const string Mod = @"E:\Penumbradt\BiboPlus Sheer Elegance";
    private const string BiboRoot = @"E:\Penumbradt\Bibo+";
    private const string NeolitheRoot = @"E:\Penumbradt\Neolithe [ALL IN ONE]";
    private const string Model = @"chara\equipment\e6010\model\c0201e6010_top.mdl";

    private static readonly string[] AuthorSizes = ["Small", "Medium", "Large", "XL"];

    private static string SizePath(string size) => Path.Combine(Mod, "Chest size", size, Model);

    /// <summary>The bust, where the bust bones themselves place it: y 1.19 to 1.35, front only.</summary>
    private static bool Bust(Vector3 p) => p.Y >= 1.19f && p.Y < 1.35f && p.Z > 0f;

    // ── the case ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported refit run here, with what the solve says about itself. A reproduction first, so that anything
    /// below can be attributed to the solve rather than to the Studio, to Penumbra, or to which option was ticked.
    /// </summary>
    [Fact]
    public void Reproduce_the_reported_refit()
    {
        if (Setup() is not { } setup) { output.WriteLine("bodies not installed"); return; }
        var (garment, garmentBytes, pairs) = setup;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: true, acrossBodies: true);
        clock.Stop();

        var r = planned.Report;
        output.WriteLine($"solve {clock.ElapsedMilliseconds} ms");
        output.WriteLine($"  folded {r.Folded}, snapped {r.Snapped:N0} ({r.SnapRate:P0}), " +
                         $"pushed {r.Pushed:N0} (worst {r.WorstPush * 1000f:F2} mm), " +
                         $"worst move {r.WorstMove * 1000f:F2} mm, missed {r.Missed:N0}");
        if (r.Swap is { } sw)
            output.WriteLine($"  skin swap: removed {sw.Removed}, added {sw.Added}, kept {sw.Kept}, cut {sw.Cut}, " +
                             $"reweighted {sw.Reweighted:N0}, trimmed {sw.Trimmed:N0}, unplaced {sw.Unplaced:N0}");
    }

    /// <summary>
    /// Which Bibo+ size each of the author's four chest sizes is actually built on — the question the detector has to
    /// answer before any of the geometry means anything, since the whole displacement field is measured from it.
    /// </summary>
    [Fact]
    public void Which_Bibo_size_each_author_chest_is_built_on()
    {
        if (!Directory.Exists(Mod) || !Directory.Exists(BiboRoot)) return;

        var catalog = BodySizeCatalog.Read(BiboRoot);
        var options = catalog.For("_top", "0201");
        if (options.Count == 0) return;
        output.WriteLine($"Bibo+ offers {options.Count} chest options");

        foreach (string size in AuthorSizes)
        {
            if (!File.Exists(SizePath(size))) continue;
            var garment = ModelPartReader.Read(File.ReadAllBytes(SizePath(size)))!;
            var ranking = BodySizeMatch.Rank(garment, options, catalog.PathOf);
            output.WriteLine("");
            output.WriteLine($"=== author's \"{size}\" -> {ranking.Confidence}");
            foreach (var s in ranking.Scores.Take(3))
                output.WriteLine($"   {s.HitRate,7:P1}  rms {s.Rms * 1000f,7:F3} mm  {s.Option.FullLabel}");
        }
    }

    // ── the three faults ──────────────────────────────────────────────────────

    /// <summary>
    /// How far the cup ends up INSIDE the breast the file itself carries, with the push-out and the skin swap on and
    /// off. The measurement that found the push-out aiming at a surface Rebuild throws away: with the skin swapped the
    /// cup came out buried where the author's own file has almost nothing inside, and turning the swap off — changing
    /// nothing whatever about the solve — fixed it, which is what pointed at the swap rather than at the geometry.
    /// </summary>
    [Fact]
    public void What_clears_the_cup_out_of_the_breast()
    {
        if (Setup() is not { } setup) return;
        var (garment, garmentBytes, pairs) = setup;

        output.WriteLine($"{"push-out",10}{"skin in the file",20}{"buried",9}{"mean",9}{"max",9}{"pushed",9}");
        foreach (bool push in new[] { true, false })
            foreach (bool skin in new[] { true, false })
            {
                var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                                pushOut: push, replaceSkin: skin, acrossBodies: true);
                var (count, mean, max) = BuriedIn(planned.Model);
                output.WriteLine($"{push,10}{(skin ? "swapped" : "the author's"),20}" +
                                 $"{count,9:N0}{mean,8:F2}mm{max,8:F2}mm{planned.Report.Pushed,9:N0}");
            }

        var (a, am, ax) = BuriedIn(File.ReadAllBytes(SizePath("Small")));
        output.WriteLine($"{"the author's own file",30}{a,9:N0}{am,8:F2}mm{ax,8:F2}mm");
    }

    /// <summary>
    /// Whether the cloth and the skin beside it are weighted to the SAME body, across the whole skeleton.
    /// <para/>
    /// The measurement that found PlanWeights rewriting cloth only: the cloth was moved onto the new body's weighting
    /// and the garment's own body mesh — drawn a millimetre away, often sharing its vertices — was left on the old
    /// one's. Two surfaces weighted to two different bodies come apart as soon as a bone turns, which no measurement
    /// of the authored pose can see. Reported as total variation: 0 is identical skinning, 1 shares no bone at all.
    /// </summary>
    [Fact]
    public void Are_the_cloth_and_the_skin_weighted_to_the_same_body()
    {
        if (Setup() is not { } setup) return;
        var (garment, garmentBytes, pairs) = setup;

        foreach (bool swap in new[] { false, true })
        {
            var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                            replaceSkin: swap, acrossBodies: true);
            Skinning($"refit, new body's skin = {swap}", planned.Model);
        }

        Skinning("the author's own file", File.ReadAllBytes(SizePath("Small")));
    }

    /// <summary>
    /// Seams the refit pulls apart, across the range of gaps an author might leave.
    /// <para/>
    /// The measurement that found the boning splitting from its panels, and the one that showed a first attempt at the
    /// fix calibrated wrong: at 0.2 mm it closed everything it looked at, and the pieces of this corset meet at a
    /// third of a millimetre, so every seam that was actually opening sat in the band above it. The whole curve is
    /// printed for that reason — a lock has to close the joins that matter without welding a panel to itself, and one
    /// number cannot show both.
    /// </summary>
    [Fact]
    public void Seams_the_refit_pulls_apart()
    {
        if (Setup() is not { } setup) return;
        var (garment, garmentBytes, pairs) = setup;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: false, acrossBodies: true);
        var refit = ModelPartReader.Read(planned.Model)!;
        if (refit.Positions.Length != garment.Positions.Length) { output.WriteLine("count changed"); return; }

        int vc = garment.Positions.Length / 3;
        foreach (float tol in new[] { 0.0002f, 0.0005f, 0.001f, 0.002f, 0.005f })
        {
            var grid = new Dictionary<(int, int, int), List<int>>();
            for (int v = 0; v < vc; v++)
            {
                var p = At(garment, v);
                var key = ((int)MathF.Floor(p.X / tol), (int)MathF.Floor(p.Y / tol), (int)MathF.Floor(p.Z / tol));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add(v);
            }

            int touching = 0, opened = 0;
            float worst = 0f;
            for (int v = 0; v < vc; v++)
            {
                var p = At(garment, v);
                var key = ((int)MathF.Floor(p.X / tol), (int)MathF.Floor(p.Y / tol), (int)MathF.Floor(p.Z / tol));
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list))
                                continue;
                            foreach (int w in list)
                            {
                                if (w <= v) continue;
                                float was = Vector3.Distance(p, At(garment, w));
                                if (was > tol) continue;
                                touching++;
                                float now = Vector3.Distance(At(refit, v), At(refit, w));
                                if (now - was <= 0.0002f) continue;
                                opened++;
                                if (now > worst) worst = now;
                            }
                        }
            }
            output.WriteLine($"   within {tol * 1000f,5:F1} mm as authored: {touching,7:N0} pairs, " +
                             $"{opened,6:N0} opened, worst now {worst * 1000f,6:F2} mm");
        }
    }

    // ── still open ────────────────────────────────────────────────────────────

    /// <summary>
    /// Cloth triangles the refit turns inside out, by pass. Folds were not what this garment was suffering from —
    /// under 1% of the cloth, where the visible damage covered a good part of the cup — but they are real, they are
    /// all over the bust, and the author's own file has none there.
    /// </summary>
    [Fact]
    public void Which_pass_folds_the_bust()
    {
        if (Setup() is not { } setup) return;
        var (garment, garmentBytes, pairs) = setup;

        output.WriteLine($"{"push-out",10}{"new skin",10}{"reported",10}{"real",8}{"bust",8}{"worst move",12}");
        foreach (bool push in new[] { true, false })
            foreach (bool skin in new[] { true, false })
            {
                var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top",
                                                pushOut: push, replaceSkin: skin, acrossBodies: true);
                var (flipped, bust) = Inverted(garment, planned.Model);
                output.WriteLine($"{push,10}{skin,10}{planned.Report.Folded,10}{flipped,8:N0}{bust,8:N0}" +
                                 $"{planned.Report.WorstMove * 1000f,11:F2}mm");
            }
    }

    /// <summary>
    /// Whether the refit's cloth still lines up with the author's, vertex for vertex. <see cref="Inverted"/> rests on
    /// it and the rebuild reorders the meshes, so it is established rather than assumed: comparing triangle k of one
    /// file against triangle k of the other, before this was checked, reported 5,001 inverted triangles where there
    /// were 95.
    /// </summary>
    [Fact]
    public void Does_the_refit_cloth_still_line_up_with_the_author_s()
    {
        if (Setup() is not { } setup) return;
        var (garment, garmentBytes, pairs) = setup;
        var planned = BodyRetarget.Plan(garment, garmentBytes, pairs, "_top", replaceSkin: true, acrossBodies: true);

        byte[] authorBytes = File.ReadAllBytes(SizePath("Small"));
        var refit = ModelPartReader.Read(planned.Model)!;
        float[] authorUv = Uv(authorBytes), refitUv = Uv(planned.Model);

        var a = garment.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(garment, sp));
        var r = refit.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(refit, sp));
        output.WriteLine($"author cloth: base {a.BaseVertex}, {a.Count:N0} verts");
        output.WriteLine($"refit  cloth: base {r.BaseVertex}, {r.Count:N0} verts");
        if (a.Count == 0 || a.Count != r.Count) { output.WriteLine("cloth spans do not correspond"); return; }

        int same = 0, differ = 0;
        for (int i = 0; i < a.Count; i++)
        {
            int ai = (a.BaseVertex + i) * 2, ri = (r.BaseVertex + i) * 2;
            if (ai + 1 >= authorUv.Length || ri + 1 >= refitUv.Length) break;
            if (MathF.Abs(authorUv[ai] - refitUv[ri]) + MathF.Abs(authorUv[ai + 1] - refitUv[ri + 1]) < 1e-6f) same++;
            else differ++;
        }
        output.WriteLine($"in the same order: {same:N0} match, {differ:N0} differ");
    }

    // ── the pair, and the measures ────────────────────────────────────────────

    /// <summary>The reported pair, ready to solve; null when the bodies are not installed.</summary>
    private static (ModelParts Garment, byte[] Bytes, List<BodyRetarget.SlotPair> Pairs)? Setup()
    {
        if (!File.Exists(SizePath("Small")) || !Directory.Exists(NeolitheRoot) || !Directory.Exists(BiboRoot))
            return null;

        var bibo = BodySizeCatalog.Read(BiboRoot);
        var source = bibo.For("_top", "0201")
                         .FirstOrDefault(o => o.FullLabel?.Contains("Perky - Small",
                                                                    StringComparison.OrdinalIgnoreCase) == true);
        var neolithe = BodySizeCatalog.Read(NeolitheRoot);
        var target = neolithe.For("_top", "0201")
                             .FirstOrDefault(o => o.FullLabel?.Contains("DEFAULT ALMOND",
                                                                        StringComparison.OrdinalIgnoreCase) == true
                                               && o.FullLabel.Contains("NSFW Almond XS",
                                                                       StringComparison.OrdinalIgnoreCase));
        if (source is null || target is null) return null;

        byte[] garmentBytes = File.ReadAllBytes(SizePath("Small"));
        byte[] sourceBytes = File.ReadAllBytes(bibo.PathOf(source));
        byte[] targetBytes = File.ReadAllBytes(neolithe.PathOf(target));
        var sourceParts = ModelPartReader.Read(sourceBytes)!;
        var targetParts = ModelPartReader.Read(targetBytes)!;
        if (!BodyCorrespondence.TryBuild(sourceParts, Uv(sourceBytes), targetParts, Uv(targetBytes), "_top",
                                         out var built, out _)) return null;

        return (ModelPartReader.Read(garmentBytes)!, garmentBytes,
                [new BodyRetarget.SlotPair("_top", built!, targetParts, targetBytes, sourceBytes)]);
    }

    /// <summary>
    /// Cup vertices inside the garment's OWN skin mesh, and how deep. Its own, because that is the breast the file
    /// draws: with the skin swapped it is not the same surface as the body mod's file, and taking the body mod's read
    /// the refit as clean when it was not.
    /// </summary>
    private static (int Count, float Mean, float Max) BuriedIn(byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts) return (-1, 0f, 0f);
        var skinParts = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        var clothParts = parts.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material)).ToList();
        if (skinParts.Count == 0 || clothParts.Count == 0) return (-1, 0f, 0f);

        var skin = new BodySurface(WithParts(parts, skinParts), BodySurface.CellFor(MeanEdge(parts)));
        var depths = new List<float>();
        foreach (int v in clothParts.SelectMany(p => p.Triangles).Distinct())
        {
            var p = At(parts, v);
            if (!Bust(p) || !skin.Nearest(p, 0.03f, out var hit)) continue;
            float signed = Vector3.Dot(p - hit.Point, hit.Normal);   // the SKIN's normal: see the class remarks
            if (signed < 0f) depths.Add(-signed * 1000f);
        }
        return depths.Count == 0 ? (0, 0f, 0f) : (depths.Count, depths.Average(), depths.Max());
    }

    /// <summary>Cloth triangles the refit turned over, and how many of those are over the bust.</summary>
    private static (int All, int Bust) Inverted(ModelParts author, byte[] refitBytes)
    {
        var refit = ModelPartReader.Read(refitBytes)!;
        var a = author.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(author, sp));
        var r = refit.MeshSpans.FirstOrDefault(sp => !IsSkinSpan(refit, sp));
        if (a.Count == 0 || a.Count != r.Count) return (-1, -1);
        int shift = a.BaseVertex - r.BaseVertex;

        int flipped = 0, bust = 0;
        foreach (var part in refit.Parts)
        {
            // Whole submeshes only: an island is a subset of its own, and taking both doubles every count.
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int i0 = part.Triangles[t], i1 = part.Triangles[t + 1], i2 = part.Triangles[t + 2];
                int j0 = i0 + shift, j1 = i1 + shift, j2 = i2 + shift;
                int n = author.Positions.Length / 3;
                if (j0 < 0 || j1 < 0 || j2 < 0 || j0 >= n || j1 >= n || j2 >= n) continue;
                var n0 = Vector3.Cross(At(author, j1) - At(author, j0), At(author, j2) - At(author, j0));
                var n1 = Vector3.Cross(At(refit, i1) - At(refit, i0), At(refit, i2) - At(refit, i0));
                if (n0.Length() <= 1e-12f || n1.Length() <= 1e-12f) continue;
                if (Vector3.Dot(n0, n1) >= 0f) continue;
                flipped++;
                if (Bust((At(refit, i0) + At(refit, i1) + At(refit, i2)) / 3f)) bust++;
            }
        }
        return (flipped, bust);
    }

    /// <summary>How far apart the cloth and the skin under it are weighted, where the cup lies on the breast.</summary>
    private void Skinning(string label, byte[] mdl)
    {
        if (ModelPartReader.Read(mdl) is not { } parts || ModelSkinReader.Read(mdl, null, null) is not { } skin
            || skin.VertexCount * 3 != parts.Positions.Length) { output.WriteLine($"{label}: skipped"); return; }

        var isSkin = new bool[skin.VertexCount];
        foreach (var part in parts.Parts)
            if (part.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(part.Material))
                foreach (int v in part.Triangles) isSkin[v] = true;

        var skinVerts = new List<int>();
        for (int v = 0; v < skin.VertexCount; v++)
            if (isSkin[v] && Bust(skin.Positions[v])) skinVerts.Add(v);
        if (skinVerts.Count == 0) { output.WriteLine($"{label}: no skin over the bust"); return; }

        var apart = new List<float>();
        var worstBone = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int v = 0; v < skin.VertexCount; v++)
        {
            if (isSkin[v] || !Bust(skin.Positions[v])) continue;
            var p = skin.Positions[v];

            int best = -1;
            float bestD = float.MaxValue;
            foreach (int sv in skinVerts)
            {
                float d = Vector3.DistanceSquared(p, skin.Positions[sv]);
                if (d < bestD) { bestD = d; best = sv; }
            }
            if (best < 0 || MathF.Sqrt(bestD) > 0.005f) continue;   // only where the cup lies ON the breast

            var mine = Weights(skin, v);
            var theirs = Weights(skin, best);
            float tv = 0f;
            foreach (string bone in mine.Keys.Union(theirs.Keys))
            {
                float diff = MathF.Abs(mine.GetValueOrDefault(bone) - theirs.GetValueOrDefault(bone));
                tv += diff;
                if (diff > worstBone.GetValueOrDefault(bone)) worstBone[bone] = diff;
            }
            apart.Add(tv / 2f);
        }

        output.WriteLine("");
        output.WriteLine($"--- {label}: {apart.Count:N0} cloth verts lying on the skin over the bust");
        if (apart.Count == 0) return;
        apart.Sort();
        output.WriteLine($"    skinning apart: mean {apart.Average():F3}  p50 {apart[apart.Count / 2]:F3}  " +
                         $"p95 {apart[(int)(apart.Count * 0.95f)]:F3}  max {apart[^1]:F3}");
        output.WriteLine($"    more than a tenth apart: {apart.Count(x => x > 0.10f):N0} " +
                         $"({apart.Count(x => x > 0.10f) / (float)apart.Count:P1})");
        output.WriteLine("    the bones that differ most: " +
                         string.Join(", ", worstBone.OrderByDescending(b => b.Value).Take(3)
                                                    .Select(b => $"{b.Key} {b.Value:F3}")));
    }

    private static Dictionary<string, float> Weights(SkinnedMesh skin, int v)
    {
        var w = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int i = 0; i < SkinnedMesh.MaxInfluences; i++)
        {
            float weight = skin.BoneWeights[v * SkinnedMesh.MaxInfluences + i];
            if (weight <= 0f) continue;
            string bone = skin.BoneNames[skin.BoneIndices[v * SkinnedMesh.MaxInfluences + i]];
            w[bone] = w.GetValueOrDefault(bone) + weight;
        }
        return w;
    }

    // ── small shared things ───────────────────────────────────────────────────

    private static Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    private static bool IsSkinSpan(ModelParts m, MeshSpan span)
        => SecondSkinWriter.IsBodySkinMaterial(m.Parts.FirstOrDefault(p => p.Mesh == span.Mesh)?.Material ?? "");

    /// <summary>The model with only these parts, so one of its meshes can be used as a surface on its own.</summary>
    private static ModelParts WithParts(ModelParts m, List<ModelPart> parts)
        => new()
        {
            Positions = m.Positions,
            Normals = m.Normals,
            MeshSpans = m.MeshSpans,
            Parts = [.. parts],
            AttributeNames = m.AttributeNames,
            Min = m.Min,
            Max = m.Max,
            ShatteredSubmeshes = m.ShatteredSubmeshes,
        };

    private static float MeanEdge(ModelParts m)
    {
        double sum = 0;
        int n = 0;
        foreach (var part in m.Parts)
        {
            if (part.Island >= 0) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                sum += (At(m, part.Triangles[t + 1]) - At(m, part.Triangles[t])).Length();
                n++;
            }
        }
        return n == 0 ? 0.01f : (float)(sum / n);
    }

    private static float[] Uv(byte[] mdl) => BodyRetargetDiagTests.Uv(mdl);
}
