using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The bust bridge against a REAL torso, not the synthetic strip <see cref="BustBridgeTests"/> uses.
/// <para/>
/// Those prove the solver; this proves the two things about the world that it rests on and that no
/// synthetic mesh can vouch for: that a shipped body actually names <c>j_mune_l</c> / <c>j_mune_r</c> in
/// the bone table of the mesh that holds the chest, and that a real mesh's density converges inside the
/// pass ceiling. Both are assumptions about somebody else's art, so they are measured rather than argued.
/// <para/>
/// Does nothing unless the model exists — point <c>PROTEUS_TORSO</c> at any body's chest model to run it
/// against that one instead.
/// </summary>
public class BustBridgeDiagTests
{
    private static readonly string Torso =
        Environment.GetEnvironmentVariable("PROTEUS_TORSO")
        ?? @"E:\Penumbradt\Neolithe [ALL IN ONE]\DEFAULT CHEST - SmallClothes\NSFW L.mdl";

    private readonly ITestOutputHelper o;
    public BustBridgeDiagTests(ITestOutputHelper o) => this.o = o;

    [Fact]
    public void RealTorsoCarriesTheBustBonesAndConverges()
    {
        if (!File.Exists(Torso)) { o.WriteLine($"skipped — no model at {Torso}"); return; }

        var mdl = File.ReadAllBytes(Torso);
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(
            mdl, out var pos, out _, out var tri, out var weights, out var nrm));
        o.WriteLine($"{Path.GetFileName(Torso)}: {pos.Length / 3} skin vertices, {tri.Length / 3} triangles");

        // 1. THE ASSUMPTION THE WHOLE FEATURE RESTS ON. If a body ever ships without these, the region
        //    cannot be found and MeshBustWeights returns null — the pass declines rather than guessing,
        //    but the feature is then dead on that body and this is where that would show up.
        var bust = new float[pos.Length / 3];
        int rigged = 0;
        for (int i = 0; i < bust.Length; i++)
        {
            float w = 0f;
            foreach (var (bone, bw) in weights[i])
                if (bone.Equals("j_mune_l", StringComparison.OrdinalIgnoreCase)
                 || bone.Equals("j_mune_r", StringComparison.OrdinalIgnoreCase))
                    w += bw;
            bust[i] = MathF.Min(1f, w);
            if (w > 0f) rigged++;
        }
        o.WriteLine($"bust-weighted vertices: {rigged} of {bust.Length} "
                  + $"({100.0 * rigged / bust.Length:0.#}%)");
        Assert.True(rigged > 0, "this body names neither j_mune_l nor j_mune_r — the region cannot be found");

        // A sanity band, not a tight assertion: the bust is a real but small part of a whole-body mesh.
        // Zero would mean the bones are absent; a majority would mean they are being read wrong and the
        // relax would be handed most of the body.
        Assert.InRange(100.0 * rigged / bust.Length, 0.5, 40.0);

        var p3 = new SecondSkinWriter.Vec3[bust.Length];
        var n3 = new SecondSkinWriter.Vec3[bust.Length];
        for (int i = 0; i < bust.Length; i++)
        {
            p3[i] = new SecondSkinWriter.Vec3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            n3[i] = new SecondSkinWriter.Vec3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
        }
        var tris = tri.Select(t => (ushort)t).ToArray();

        var log = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = SecondSkinWriter.BustBridgeSolve(p3, n3, tris, bust, 1f, log.Add);
        sw.Stop();
        foreach (var line in log) o.WriteLine(line);
        o.WriteLine($"solve took {sw.ElapsedMilliseconds} ms");
        Assert.NotNull(plan);

        // 2. THE SECOND ASSUMPTION. The bust bones do not meet across the sternum — measured here as two
        //    components of 1139 nodes each — so the cleavage is not in the seed and has to be added. This
        //    is the check that the gap fill still finds it; without it the pass moves the breasts' inner
        //    slopes and leaves the one place a bridge exists for exactly as deep as it found it.
        Assert.Contains(log, l => l.Contains("added across the gap") && !l.Contains(" 0 node(s) added"));

        // 3. It actually flattened the cleavage. Parsed out of the report rather than recomputed, so the
        //    number the log shows in game is the number this test stands behind.
        var dish = log.FirstOrDefault(l => l.Contains("dish mean"));
        Assert.NotNull(dish);
        // The MEAN, not the worst. The report's chord runs between the region's two overall forward-most
        // points, while the construction spans between each horizontal band's OWN pair — so a band above
        // or below the nipple line is legitimately behind the global chord and the worst case is measured
        // against a promise the pass never made. The mean is the honest summary of a per-band span.
        var m = System.Text.RegularExpressions.Regex.Match(
            dish!, @"dish mean ([\d.]+) -> ([\d.]+).*worst ([\d.]+) -> ([\d.]+)");
        Assert.True(m.Success, dish);
        float before = float.Parse(m.Groups[1].Value), after = float.Parse(m.Groups[2].Value);
        o.WriteLine($"cleavage, mean over the chord: {before:0.#####} -> {after:0.#####} "
                  + $"({100 * (1 - after / before):0.#}% spanned); worst "
                  + $"{m.Groups[3].Value} -> {m.Groups[4].Value}");
        // Half is well under the ~63% measured when this was written and well over anything a broken solve
        // produced — every failed approach along the way sat between 20% and 36%.
        Assert.True(after < before * 0.5f,
            $"the cleavage still averages {after:0.#####} deep against {before:0.#####} before");

        // 4. Untouched vertices are byte-identical — nothing outside the region moved at all, which is what
        //    keeps a shell with the bridge off and one with it on the same everywhere else.
        int movedOutsideRegion = 0;
        for (int i = 0; i < bust.Length; i++)
        {
            if (bust[i] > 0f) continue;
            var d = plan!.Delta[i];
            if (d.X != 0f || d.Y != 0f || d.Z != 0f) movedOutsideRegion++;
        }
        // Not zero-tolerance: a vertex outside the BONE weighting is legitimately inside the region — the
        // gap between the lobes is exactly that — and a welded copy of one moves with its twin.
        o.WriteLine($"vertices with no bone weight that moved (gap fill, or welded to a twin): {movedOutsideRegion}");

        // 5. Something to LOOK at. Every measurement above is a scalar, and the failure this feature is
        //    most likely to have left is one no scalar catches: a span that is the right depth and the
        //    wrong shape. Two OBJs of the same mesh, before and after, open on top of each other.
        var dir = Environment.GetEnvironmentVariable("PROTEUS_BUST_OBJ_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        WriteObj(Path.Combine(dir, "bust_before.obj"), p3, tris);
        var moved = new SecondSkinWriter.Vec3[bust.Length];
        for (int i = 0; i < bust.Length; i++)
            moved[i] = new SecondSkinWriter.Vec3(p3[i].X + plan!.Delta[i].X,
                                                 p3[i].Y + plan.Delta[i].Y,
                                                 p3[i].Z + plan.Delta[i].Z);
        WriteObj(Path.Combine(dir, "bust_after.obj"), moved, tris);
        o.WriteLine($"wrote bust_before.obj and bust_after.obj to {dir}");
    }

    /// <summary>
    /// The standoff map the skin bake gates skindenting on. Runs the same solve over the whole body and
    /// rasterises the lift into body UV, so this checks the two things that make it usable: it lands on
    /// the CHEST rather than smeared over the atlas, and the lift there is far enough past the contact
    /// threshold to actually suppress rather than merely dim.
    /// </summary>
    [Fact]
    public void StandoffMapCoversTheChestAndNothingElse()
    {
        if (!File.Exists(Torso)) { o.WriteLine($"skipped — no model at {Torso}"); return; }

        const int size = 512;
        const float fullAt = 0.0015f;
        var map = SecondSkinWriter.BustStandoffMap(
            new[] { File.ReadAllBytes(Torso) }, null, 0, 0, 1f, size, fullAt);
        Assert.NotNull(map);

        int lit = 0, full = 0, minX = size, maxX = -1, minY = size, maxY = -1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte v = map![y * size + x];
                if (v == 0) continue;
                lit++;
                if (v == 255) full++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        float u0 = minX / (float)size, u1 = maxX / (float)size;
        float v0 = minY / (float)size, v1 = maxY / (float)size;
        o.WriteLine($"{lit} texels lifted ({100.0 * lit / map!.Length:0.##}%), {full} fully suppressed; "
                  + $"u {u0:0.###}..{u1:0.###}, v {v0:0.###}..{v1:0.###}");

        Assert.True(lit > 0, "nothing lifted — the map would suppress no skindent at all");
        // A few percent of the atlas. Much more means it is marking something other than the cleavage,
        // and a map that suppresses broadly would quietly take skindenting off whole garments.
        Assert.InRange(100.0 * lit / map.Length, 0.1, 8.0);
        // Straddling the midline and in the upper half of the body: the chest, not the hips or the arms.
        Assert.True(u0 < 0.5f && u1 > 0.5f, $"the lifted region does not straddle the midline (u {u0}..{u1})");
        Assert.True(v1 < 0.5f, $"the lifted region reaches below the torso (v {v0}..{v1})");
        // Nearly all of it saturated: the span clears the contact threshold many times over, so this is a
        // gate rather than a fade, and a change that quietly halved the lift would show up here.
        Assert.True(full > lit * 0.75, $"only {full} of {lit} lifted texels are past {fullAt}");
    }

    /// <summary>
    /// The map's edge must be a GRADIENT, not a step.
    /// <para/>
    /// The lift runs far past the contact threshold and falls off steeply, so the map is very nearly
    /// binary — and taking the maximum over each triangle made its boundary follow the body's triangle
    /// edges. Gating the skindent on that drew a hard faceted line of suppression across the ribs, plainly
    /// visible in game. This measures the thing that was wrong: how many texels change by a large step
    /// against their neighbour. A contour that follows the interpolated lift has almost none.
    /// </summary>
    [Fact]
    public void StandoffMapEdgeIsAGradientNotAStep()
    {
        if (!File.Exists(Torso)) { o.WriteLine($"skipped — no model at {Torso}"); return; }

        const int size = 1024;
        var map = SecondSkinWriter.BustStandoffMap(
            new[] { File.ReadAllBytes(Torso) }, null, 0, 0, 1f, size, 0.0015f);
        Assert.NotNull(map);

        int edges = 0, harsh = 0, worst = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                int p = y * size + x;
                int d = Math.Abs(map![p] - map[p + 1]);
                if (map[p] == 0 && map[p + 1] == 0) continue;
                edges++;
                if (d > worst) worst = d;
                if (d > 64) harsh++;
            }
        o.WriteLine($"{edges} texel pairs inside the map, {harsh} jump by more than 64/255, worst {worst}");
        Assert.True(edges > 0);
        // Some jump is unavoidable at the outermost texel, where the map meets nothing at all — and the
        // compositor feathers the whole thing by the indent's own radius afterwards. What must not happen
        // is a large fraction of the boundary stepping at once, which is what facets look like.
        Assert.True(harsh < edges * 0.06,
            $"{harsh} of {edges} texel pairs step by more than a quarter — the edge is faceted, not smooth");

        // ...and after the feather the compositor applies, which is the map that actually gates the
        // indent, nothing should step at all. This is the end-to-end version of the same measurement.
        int radius = Math.Max(1, (int)(size * 0.003f));   // the default AO softness
        var soft = CompositorService.BlurCoverage(map!, size, size, radius);
        int softHarsh = 0, softWorst = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                int p = y * size + x;
                if (soft[p] == 0 && soft[p + 1] == 0) continue;
                int d = Math.Abs(soft[p] - soft[p + 1]);
                if (d > softWorst) softWorst = d;
                if (d > 64) softHarsh++;
            }
        o.WriteLine($"after feathering by {radius}: {softHarsh} harsh pairs, worst step {softWorst}");
        Assert.True(softHarsh == 0,
            $"{softHarsh} texel pairs still step after feathering (worst {softWorst})");
    }

    /// <summary>
    /// No fold in the span may be steeper than <c>BustMaxSlope</c>, on real body topology.
    /// <para/>
    /// The synthetic version of this only ever sees a tidy grid. On a body the demand for steepness comes
    /// from the region's own boundary — the garment's edge, the reach of the bust bones — and the limit is
    /// genuinely binding there. Left at 1.5 that produced 146 edges folding at 56°, which is the jagged
    /// notch reported along a bralette strap. The measurement is what the limit was tuned against, so it
    /// belongs in the suite rather than in a scratch file.
    /// </summary>
    [Fact]
    public void NoFoldInTheSpanIsSteeperThanTheLimit()
    {
        if (!File.Exists(Torso)) { o.WriteLine($"skipped — no model at {Torso}"); return; }

        Assert.True(SecondSkinWriter.TryReadLod0Geometry(
            File.ReadAllBytes(Torso), out var pos, out _, out var tri, out var weights, out var nrm));
        int vc = pos.Length / 3;

        var bust = new float[vc];
        for (int i = 0; i < vc; i++)
        {
            float acc = 0f;
            foreach (var (bone, bw) in weights[i])
                if (bone.Equals("j_mune_l", StringComparison.OrdinalIgnoreCase)
                 || bone.Equals("j_mune_r", StringComparison.OrdinalIgnoreCase)) acc += bw;
            bust[i] = MathF.Min(1f, acc);
        }
        var p3 = new SecondSkinWriter.Vec3[vc];
        var n3 = new SecondSkinWriter.Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            p3[i] = new SecondSkinWriter.Vec3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            n3[i] = new SecondSkinWriter.Vec3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
        }

        var plan = SecondSkinWriter.BustBridgeSolve(p3, n3, tri, bust, 1f);
        Assert.NotNull(plan);

        static float Mag(SecondSkinWriter.Vec3 d) => MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        var slopes = new List<float>();
        for (int t = 0; t + 2 < tri.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = tri[t + e], b = tri[t + (e + 1) % 3];
                float ma = Mag(plan!.Delta[a]), mb = Mag(plan.Delta[b]);
                if (ma == 0f && mb == 0f) continue;
                float dx = pos[a * 3] - pos[b * 3], dy = pos[a * 3 + 1] - pos[b * 3 + 1],
                      dz = pos[a * 3 + 2] - pos[b * 3 + 2];
                float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len > 1e-9f) slopes.Add(MathF.Abs(ma - mb) / len);
            }
        slopes.Sort();
        float P(double q) => slopes[Math.Min(slopes.Count - 1, (int)(slopes.Count * q))];
        o.WriteLine($"{slopes.Count} edges in the span: p50 {P(.5):0.###}, p90 {P(.9):0.###}, "
                  + $"p99 {P(.99):0.###}, max {slopes[^1]:0.###}");

        // A little over the limit for welded copies and float noise. The build this was written against
        // measured a max of 0.8 exactly, and the one before it 1.5.
        Assert.True(slopes[^1] < 1.0f,
            $"a fold in the span reaches {slopes[^1]:0.###} per unit length — steep enough to read as a notch");
    }

    private static void WriteObj(string path, SecondSkinWriter.Vec3[] pos, ushort[] tris)
    {
        using var w = new StreamWriter(path);
        foreach (var p in pos)
            w.WriteLine($"v {p.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} "
                      + $"{p.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} "
                      + $"{p.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        for (int t = 0; t + 2 < tris.Length; t += 3)
            w.WriteLine($"f {tris[t] + 1} {tris[t + 1] + 1} {tris[t + 2] + 1}");
    }
}
