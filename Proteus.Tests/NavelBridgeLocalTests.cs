using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Services.SecondSkinWriter;

namespace Proteus.Tests;

/// <summary>The navel fill on real bodies: where each body's navel is, and what the fill and the writer make of it.</summary>
public class NavelBridgeLocalTests(ITestOutputHelper o)
{
    private const string Neo = SecondSkinWriterVerbatimTests.NeolitheNeed;
    private const string Bibo = SecondSkinWriterVerbatimTests.BiboNeed;
    private const string Rue = SecondSkinWriterVerbatimTests.RueNeed;
    private const string ShellMaterial = "/mt_c0201a0053_rir_a.mtrl";

    /// <summary>
    /// The most hollow a filled navel may leave. Rue+'s leaves more (see <see cref="RueLeft"/>): its slit runs on into a
    /// shallow trough down to the top part's hem, which the fill must stay clear of.
    /// </summary>
    private const float MostLeft = 0.0005f;

    /// <summary>
    /// What Rue+'s navel may leave (0.8 mm measured, 2026-10-12): its slit sits in a shallow trough that runs 50 mm
    /// down to the top part's hem, and the fill stops clear of the hem, so the trough's edge stays. Accepted as a
    /// deviation from AC-navel-bridge-4's 0.5 mm.
    /// </summary>
    private const float RueLeft = 0.00085f;

    private static string[] PartsOf(string body) => Array.Find(SecondSkinWriterVerbatimTests.Bodies, b => b.Body == body).Parts;

    /// <summary>A body's parts read the way the build's locator reads them.</summary>
    private static List<NavelBridge.NavelMesh> Parts(string body)
    {
        var parts = new List<NavelBridge.NavelMesh>();
        foreach (var path in PartsOf(body))
        {
            Assert.True(TryReadLod0Geometry(File.ReadAllBytes(path), out var p, out _, out var t, out var w, out var nr));
            parts.Add(NavelBridge.ToNavelMesh(p, nr, t, w));
        }
        return parts;
    }

    private static SecondSkinLayer Navel(byte[]? coverage = null, int size = 0, float bust = 0f) => new()
    {
        MaterialName = ShellMaterial, Coverage = coverage, CoverageWidth = size, CoverageHeight = size,
        NavelBridgeStrength = 1f, BustBridgeStrength = bust,
    };

    private static byte[] Build(IEnumerable<byte[]> models, SecondSkinLayer[] layers, List<string>? diag = null,
                                BuildTimings? timings = null)
        => SecondSkinWriter.Build(models.Select(m => new SourceSpec(m, DropConnectors: true)).ToList(), layers, null,
                                  out _, diag == null ? null : diag.Add, timings: timings);

    [LocalDataTheory(Neo, Bibo, Rue)]
    [Trait("Plan", "AC-navel-bridge-4")]
    [Trait("Plan", "AC-navel-bridge-5")]
    [InlineData("Neolithe")]
    [InlineData("Bibo+")]
    [InlineData("Rue+")]
    public void Fills_the_navel_on_real_bodies(string body)
    {
        var parts = Parts(body);
        var site = NavelBridge.LocateNavel(parts, o.WriteLine, out var why);
        Assert.True(site != null, why);
        var part = parts[site!.Owner];
        var plan = NavelBridge.NavelFill(part.Pos, part.Nrm, Array.ConvertAll(part.Tris, t => (ushort)t), site, null, null,
                                        1f, o.WriteLine, out var report);
        o.WriteLine($"{body}: {report.DepthBefore * 1000:0.##}mm deep, {report.DepthAfter * 1000:0.##}mm left");

        Assert.NotNull(plan);
        Assert.Equal(NavelBridge.NavelOutcome.Filled, report.Outcome);
        Assert.True(report.DepthAfter <= (body == "Rue+" ? RueLeft : MostLeft), $"{report.DepthAfter * 1000:0.##}mm left");
        Assert.Equal(0, report.InwardTriangles);
        Assert.Equal(0, report.InsideNodes);
        // Nothing outside the pit and its rim moves.
        for (int i = 0; i < part.Pos.Length; i++)
            if (plan!.NodeWeight[plan.NodeOf[i]] == 0f) Assert.Equal(default, plan.Delta[i]);
    }

    [LocalDataTheory(Neo, Bibo, Rue)]
    [Trait("Plan", "AC-navel-bridge-3")]
    [Trait("Plan", "AC-navel-bridge-4")]
    [InlineData("Neolithe")]
    [InlineData("Bibo+")]
    [InlineData("Rue+")]
    public void A_full_build_with_the_navel_tick_alone_fills_it(string body)
    {
        var diag = new List<string>();
        var timings = new BuildTimings();
        var shell = Build(PartsOf(body).Select(File.ReadAllBytes), [Navel()], diag, timings);
        foreach (var l in diag.Where(l => l.StartsWith("navel fill:"))) o.WriteLine(l);
        o.WriteLine(timings.Describe());

        Assert.Single(diag, l => l.StartsWith("navel fill: filled"));
        Assert.DoesNotContain(diag, l => l.StartsWith("navel fill: not filled") || l.StartsWith("navel fill: left out"));
        Assert.True(timings.Navel.Calls >= 1);
        SecondSkinWriterVerbatimTests.Validate(shell);
    }

    /// <summary>
    /// Measured on what the writer wrote, not on the plan, built as the game builds a body — every part at once: the
    /// hollow the writer's own fill reports left, the written shell rising no further than the fill lifts, and every
    /// written triangle round the navel facing out. Not the written navel re-read as a pit: where the legs overlap the
    /// top, the legs' own copy of the navel sits under the fill, and a re-read finds it.
    /// </summary>
    [LocalDataTheory(Neo, Bibo, Rue)]
    [Trait("Plan", "AC-navel-bridge-10")]
    [InlineData("Neolithe")]
    [InlineData("Bibo+")]
    [InlineData("Rue+")]
    public void The_written_navel_is_flush(string body)
    {
        var parts = Parts(body);
        var site = NavelBridge.LocateNavel(parts, null, out _)!;
        var models = PartsOf(body).Select(File.ReadAllBytes).ToList();
        var plain = Build(models, [new SecondSkinLayer { MaterialName = ShellMaterial }]);
        var diag = new List<string>();
        var filled = Build(models, [Navel()], diag);
        foreach (var l in diag.Where(l => l.StartsWith("navel fill:"))) o.WriteLine(l);

        Assert.True(TryReadLod0Geometry(plain, out var pp, out _, out var pt, out _, out var pn, keepMaterial: _ => true));
        Assert.True(TryReadLod0Geometry(filled, out var fp, out _, out var ft, out _, out var fn, keepMaterial: _ => true));
        Assert.Equal(pt, ft);
        var before = NavelBridge.ToNavelMesh(pp, pn, pt, []);
        var after = NavelBridge.ToNavelMesh(fp, fn, ft, []);

        // "navel fill: filled N vertex(es) — Dmm deep, Lmm left, …"
        var filledLine = Assert.Single(diag, l => l.StartsWith("navel fill: filled"));
        float left = float.Parse(filledLine.Split("deep, ")[1].Split("mm")[0],
                                 System.Globalization.CultureInfo.InvariantCulture) / 1000f;
        o.WriteLine($"{body}: the writer's fill left {left * 1000:0.##}mm");
        Assert.True(left <= (body == "Rue+" ? RueLeft : MostLeft), $"{left * 1000:0.##}mm left");

        // A raised button over the navel — the extra clearance a bridge gets, a millimetre of it — rises past the
        // fill's own lift. The plan here is solved on the part whole, the writer's per mesh, so they agree closely,
        // not exactly.
        var tp = parts[site.Owner];
        var plan = NavelBridge.NavelFill(tp.Pos, tp.Nrm, Array.ConvertAll(tp.Tris, t => (ushort)t), site, null, null, 1f,
                                        null, out _)!;
        float Up(Vec3 d) => d.X * site.Axis.X + d.Y * site.Axis.Y + d.Z * site.Axis.Z;
        float lifted = plan.Delta.Max(Up);
        float rose = 0f;
        for (int i = 0; i < after.Pos.Length; i++)
            rose = MathF.Max(rose, Up(new Vec3(after.Pos[i].X - before.Pos[i].X, after.Pos[i].Y - before.Pos[i].Y,
                                               after.Pos[i].Z - before.Pos[i].Z)));
        o.WriteLine($"{body}: the fill lifts {lifted * 1000:0.##}mm, the written shell rose {rose * 1000:0.##}mm");
        Assert.True(rose <= lifted + ProudMost, $"rose {(rose - lifted) * 1000:0.##}mm past the fill's own lift");

        var nodes = Enumerable.Range(0, after.Pos.Length).ToArray();
        var near = new HashSet<int>(nodes.Where(i => Dist(after.Pos[i], site.Centre) <= site.DiscRadius));
        var tris = Array.ConvertAll(after.Tris, t => (ushort)t);
        Assert.Equal(0, NavelBridge.NavelFolded(before.Pos, after.Pos, tris, nodes,
                                               new HashSet<int>(near.Where(i => Dist(after.Pos[i], before.Pos[i]) > 1e-6f)),
                                               near.Contains, site.Axis, 1e-12f));
    }

    /// <summary>The most the written shell may rise past the fill's own lift (AC-10).</summary>
    private const float ProudMost = 0.0003f;

    /// <summary>With only the navel ticked, the waist ring the top shares with the legs is written where it always is.</summary>
    [LocalDataTheory(Neo, Bibo, Rue)]
    [Trait("Plan", "EDGE-navel-bridge-10")]
    [InlineData("Neolithe")]
    [InlineData("Bibo+")]
    [InlineData("Rue+")]
    public void The_join_does_not_move_with_the_navel_tick_alone(string body)
    {
        var models = PartsOf(body).Select(File.ReadAllBytes).ToList();
        var plain = Build(models, [new SecondSkinLayer { MaterialName = ShellMaterial }]);
        var filled = Build(models, [Navel()]);
        Assert.True(TryReadLod0Geometry(plain, out var pp, out _, out _, keepMaterial: _ => true));
        Assert.True(TryReadLod0Geometry(filled, out var fp, out _, out _, keepMaterial: _ => true));
        Assert.Equal(pp.Length, fp.Length);

        // Every point the legs part writes: a top vertex sitting on one of them is on the join.
        Assert.True(TryReadLod0Geometry(Build([models[1]], [new SecondSkinLayer { MaterialName = ShellMaterial }]),
                                        out var legs, out _, out _, keepMaterial: _ => true));
        var onLegs = new HashSet<(int, int, int)>();
        for (int i = 0; i + 2 < legs.Length; i += 3) onLegs.Add(Key(legs[i], legs[i + 1], legs[i + 2]));

        int moved = 0, joinMoved = 0;
        for (int i = 0; i + 2 < pp.Length; i += 3)
        {
            if (pp[i] == fp[i] && pp[i + 1] == fp[i + 1] && pp[i + 2] == fp[i + 2]) continue;
            moved++;
            if (onLegs.Contains(Key(pp[i], pp[i + 1], pp[i + 2]))) joinMoved++;
        }
        o.WriteLine($"{body}: {moved} vertex(es) moved, {joinMoved} on the join");
        Assert.True(moved > 0);
        Assert.Equal(0, joinMoved);

        static (int, int, int) Key(float x, float y, float z)
            => ((int)MathF.Round(x * 1e5f), (int)MathF.Round(y * 1e5f), (int)MathF.Round(z * 1e5f));
    }

    [LocalDataFact(Neo)]
    [Trait("Plan", "AC-navel-bridge-6")]
    [Trait("Plan", "EDGE-navel-bridge-15")]
    public void Stacked_layers_share_one_navel_solve()
    {
        var models = PartsOf("Neolithe").Select(File.ReadAllBytes).ToList();
        var one = new BuildTimings();
        Build(models, [Navel()], timings: one);

        // A second shell over part of the first, by its own coverage.
        const int size = 64;
        var half = new byte[size * size];
        for (int p = 0; p < half.Length; p++) half[p] = (byte)(p % size < size / 2 ? 255 : 0);
        var diag = new List<string>();
        var two = new BuildTimings();
        Build(models, [Navel(), Navel(half, size)], diag, two);

        Assert.Contains(diag, l => l.StartsWith("bust bridge: one navel solve for 2 layer(s)"));
        Assert.Single(diag, l => l.StartsWith("navel fill: filled"));
        Assert.True(one.Navel.Calls > 0);
        Assert.Equal(one.Navel.Calls, two.Navel.Calls);
    }

    [LocalDataTheory(Neo, Bibo)]
    [Trait("Plan", "EDGE-navel-bridge-17")]
    [InlineData("Neolithe")]
    [InlineData("Bibo+")]
    public void The_cleavage_and_the_navel_are_both_spanned(string body)
    {
        var diag = new List<string>();
        Build(PartsOf(body).Select(File.ReadAllBytes), [Navel(bust: 1f)], diag);
        Assert.Contains(diag, l => l.StartsWith("navel fill: filled"));
        Assert.DoesNotContain(diag, l => l.StartsWith("navel fill: left out"));
    }

    /// <summary>
    /// The game's own bodies — Midlander male and female, Roegadyn male, Lalafell — have no navel deep enough to fill
    /// (Lalafell's deepest pit is 0.42 mm), and nothing else on them is taken for one: before the men's search was
    /// capped halfway up the midline, it reached the throat (a 10 mm pit at y 1.47 on c0101, 19 mm at 1.83 on c0901),
    /// turned away only by its width.
    /// </summary>
    [LocalDataTheory(LocalData.GameData)]
    [Trait("Plan", "EDGE-navel-bridge-21")]
    [InlineData("c0101")]
    [InlineData("c0201")]
    [InlineData("c0901")]
    [InlineData("c1101")]
    public void The_game_bodies_fill_nothing_false(string race)
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var parts = new List<NavelBridge.NavelMesh>();
        foreach (var slot in new[] { "top", "dwn" })
        {
            var file = data.GetFile($"chara/equipment/e0000/model/{race}e0000_{slot}.mdl");
            if (file == null) continue;
            Assert.True(TryReadLod0Geometry(file.Data, out var p, out _, out var t, out var w, out var nr));
            parts.Add(NavelBridge.ToNavelMesh(p, nr, t, w));
        }
        Assert.NotEmpty(parts);
        var log = new List<string>();
        var site = NavelBridge.LocateNavel(parts, log.Add, out var why);
        foreach (var l in log) o.WriteLine(l);
        o.WriteLine(site?.ToString() ?? "none: " + why);
        Assert.Null(site);

        // Nor anything considered above the waist: every pit the search saw is below the breasts, or on a body with
        // none, in the lower half of the midline.
        float bustLow = parts.SelectMany(p => p.Pos.Zip(p.Bust, (q, x) => (q.Y, x)))
            .Where(v => v.x >= NavelBridge.NavelBoneFloor).Select(v => v.Y).DefaultIfEmpty(float.MaxValue).Min();
        var midline = parts.SelectMany(p => p.Pos).Where(q => MathF.Abs(q.X) < 0.01f).Select(q => q.Y).ToList();
        float cap = bustLow < float.MaxValue ? bustLow : midline.Min() + 0.5f * (midline.Max() - midline.Min());
        foreach (var l in log.Where(l => l.StartsWith("navel fill: pit")))
        {
            float y = float.Parse(l.Split('(')[1].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(y < cap, $"{l} — above the cap at {cap:0.###}");
        }
    }

    private static float Dist(Vec3 a, Vec3 b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
