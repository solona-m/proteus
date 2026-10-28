using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Tests.RefitNippleSmoothPinTests;
using static Proteus.Tests.SyntheticChest;

namespace Proteus.Tests;

/// <summary>
/// The refit's nipple smoothing on <see cref="SyntheticChest"/>: a chest garment made on one body, refitted to a body
/// whose nipples stand prouder. Only the excess under covering cloth goes; everything else is left exactly as it was,
/// which is checked against the output hashed before the feature (<see cref="RefitNippleSmoothPinTests"/>).
/// </summary>
public class RefitNippleSmoothTests(ITestOutputHelper output)
{
    /// <summary>The ring the prominence is read over: the locator's own on this chest (4.5% and 9% of its bust's 0.36 m
    /// width, as the pass logs it).</summary>
    private const float RingIn = 0.0162f, RingOut = 0.0324f;

    private static BodyRetarget.Tuning Off => new(NoNippleSmooth: true);

    private BodyRetarget.Planned On(string fixture, List<string>? lines = null)
        => Refit(fixture, line => { lines?.Add(line); output.WriteLine(line); });

    private static string PinnedHash(string fixture) => RefitNippleSmoothPinTests.Hashes[fixture];

    /// <summary>How proud the skin a model draws stands at one nipple of the chest.</summary>
    private static float Prominence(byte[] model, bool left, Func<string, bool>? material = null)
    {
        var parts = ModelPartReader.Read(model)!;
        if (material != null)
            parts = new ModelParts
            {
                Positions = parts.Positions, Normals = parts.Normals, MeshSpans = parts.MeshSpans,
                Parts = parts.Parts.Where(p => material(p.Material)).ToList(), AttributeNames = parts.AttributeNames,
                Min = parts.Min, Max = parts.Max, ShatteredSubmeshes = parts.ShatteredSubmeshes,
            };
        var tip = Tip(left);
        return BodyRetarget.ProminenceAt(new BodyRetarget.SkinIndex(parts),
                                         new Vector3(tip.X, tip.Y, Z(tip.X, tip.Y, 0f, 0f)), RingIn, RingOut);
    }

    /// <summary>The grid vertex at a nipple's tip, in every full-resolution chest built here.</summary>
    private static int TipVertex(bool left) => (Rows / 2) * Columns + (left ? Columns / 4 : Columns * 3 / 4);

    /// <summary>The excess the pass measured on one side, from its log line.</summary>
    private static float Excess(IEnumerable<string> lines, bool left)
    {
        var line = lines.First(l => l.StartsWith($"refit nipple smooth: side {(left ? "L" : "R")} tip"));
        int at = line.IndexOf("excess ", StringComparison.Ordinal) + "excess ".Length;
        return float.Parse(line[at..line.IndexOf(" mm", at, StringComparison.Ordinal)],
                           System.Globalization.CultureInfo.InvariantCulture) / 1000f;
    }

    /// <summary>How far a tip came down, as a share of the excess the pass asked to take off it.</summary>
    private float TipShare(ModelParts before, ModelParts after, IEnumerable<string> lines, bool left)
    {
        int tip = TipVertex(left);
        float drop = P(before, tip).Z - P(after, tip).Z, excess = Excess(lines, left);
        output.WriteLine($"{(left ? "L" : "R")}: excess {excess * 1000:0.##} mm, tip down {drop * 1000:0.##} mm");
        return drop / excess;
    }

    private static Vector3 P(ModelParts m, int v) => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
    private static Vector3 N(ModelParts m, int v) => new(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);

    private static string Hash(byte[] b) => RefitNippleSmoothPinTests.Hash(b);

    // ── what it smooths ────────────────────────────────────────────────────────────────────────────

    /// <summary>[AC-refit-nipple-smooth-2] [EDGE-refit-nipple-smooth-19] A garment made on a flat chest, refitted to a
    /// nippled one: each tip comes down by the excess the pass measured (85-115% of it), and the swapped-in skin is the
    /// smoothed one.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-2")]
    [Trait("Plan", "EDGE-refit-nipple-smooth-19")]
    [Trait("Speed", "Slow")]
    public void Flat_source_nippled_target_is_smoothed_to_flat()
    {
        var lines = new List<string>();
        var plan = On("flat to nippled", lines);
        float source = Prominence(Body(0f, 0f), true), target = Prominence(Body(Nipple, Nipple), true);
        Assert.True(target - source > 0.002f, "fixture: the target's nipple must stand proud of the source's");
        Assert.True(plan.Report.Smoothed > 0);

        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var smoothed = BodyRetarget.SmoothCoveredNipples(ModelPartReader.Read(Garment(0f, 0f, BothCups))!, [pair], out _)[0];
        foreach (bool left in new[] { true, false })
            Assert.InRange(TipShare(pair.Target, smoothed.Target, lines, left), 0.85f, 1.15f);

        // The output draws that skin: its tip is where the smoothed body's is.
        var drawn = ModelPartReader.Read(plan.Model)!;
        var tip = P(smoothed.Target, TipVertex(true));
        Assert.Contains(new BodyRetarget.SkinIndex(drawn).Vertices, v => Vector3.Distance(P(drawn, v), tip) < 1e-5f);
    }

    /// <summary>[AC-refit-nipple-smooth-3] [EDGE-refit-nipple-smooth-20] A source as proud as the target, or prouder,
    /// leaves the refit exactly as it was; a flat source, the control, does not.</summary>
    [Theory]
    [InlineData("prouder source")]
    [InlineData("equal nipples")]
    [Trait("Plan", "AC-refit-nipple-smooth-3")]
    [Trait("Plan", "EDGE-refit-nipple-smooth-20")]
    [Trait("Speed", "Slow")]
    public void Equal_or_prouder_source_changes_nothing(string fixture)
    {
        Assert.Equal(PinnedHash(fixture), Hash(On(fixture).Model));
        Assert.NotEqual(PinnedHash("flat to nippled"), Hash(On("flat to nippled").Model));
    }

    /// <summary>[AC-refit-nipple-smooth-3] Two files of one body — the same skin, one carrying a piercing — have nothing
    /// to smooth between them.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-3")]
    public void Same_skin_in_another_file_changes_nothing()
    {
        var garment = Garment(Nipple, Nipple, BothCups);
        var pair = Pair(Body(Nipple, Nipple, piercing: true), Body(Nipple, Nipple));
        var lines = new List<string>();
        var smoothed = BodyRetarget.SmoothCoveredNipples(ModelPartReader.Read(garment)!, [pair], out int moved, lines.Add);
        foreach (var l in lines) output.WriteLine(l);
        Assert.Equal(0, moved);
        Assert.Equal(pair, smoothed[0]);
    }

    /// <summary>[AC-refit-nipple-smooth-3] [EDGE-refit-nipple-smooth-26] The same body file on both sides — a
    /// <c>--finish</c> — is never looked at.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-3")]
    [Trait("Plan", "EDGE-refit-nipple-smooth-26")]
    [Trait("Speed", "Slow")]
    public void Finish_with_the_same_body_is_a_no_op()
    {
        var lines = new List<string>();
        var plan = On("equal nipples", lines);
        Assert.Equal(PinnedHash("equal nipples"), Hash(plan.Model));
        Assert.Equal(0, plan.Report.Smoothed);
        Assert.Contains(lines, l => l.Contains("same body on both sides"));
    }

    /// <summary>[EDGE-refit-nipple-smooth-21] A source with a smaller nipple keeps it: the tip comes down by the excess,
    /// and stays above the source's own tip.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-21")]
    public void Partial_excess_keeps_the_source_s_nipple()
    {
        var lines = new List<string>();
        var pair = Pair(Body(0.002f, 0.002f), Body(Nipple, Nipple));
        var smoothed = BodyRetarget.SmoothCoveredNipples(ModelPartReader.Read(Garment(0.002f, 0.002f, BothCups))!, [pair],
                                                         out int moved, lines.Add)[0];
        Assert.True(moved > 0);
        var source = ModelPartReader.Read(Body(0.002f, 0.002f))!;
        foreach (bool left in new[] { true, false })
        {
            Assert.InRange(TipShare(pair.Target, smoothed.Target, lines, left), 0.85f, 1.15f);
            Assert.True(P(smoothed.Target, TipVertex(left)).Z > P(source, TipVertex(left)).Z, "flattened below the source");
        }
    }

    /// <summary>[AC-refit-nipple-smooth-4] [EDGE-refit-nipple-smooth-4] Cloth over the right breast only: the left keeps
    /// the new body's exact shape, the right is smoothed, and both are located and framed as with both covered.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-4")]
    [Trait("Plan", "EDGE-refit-nipple-smooth-4")]
    [Trait("Speed", "Slow")]
    public void Uncovered_nipple_keeps_the_body_s_exact_shape()
    {
        var one = new List<string>();
        var both = new List<string>();
        var plan = On("right cup only", one);
        On("flat to nippled", both);

        var target = ModelPartReader.Read(Body(Nipple, Nipple))!;
        var drawn = ModelPartReader.Read(plan.Model)!;
        var at = new HashSet<Vector3>(new BodyRetarget.SkinIndex(drawn).Vertices.Select(v => P(drawn, v)));
        int left = 0;
        foreach (int v in new BodyRetarget.SkinIndex(target).Vertices)
        {
            if (P(target, v).X > -0.03f) continue;
            left++;
            Assert.Contains(P(target, v), at);
        }
        Assert.True(left > 100);
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var smoothed = BodyRetarget.SmoothCoveredNipples(ModelPartReader.Read(Garment(0f, 0f, RightCup))!, [pair], out _)[0];
        Assert.InRange(TipShare(pair.Target, smoothed.Target, one, false), 0.85f, 1.15f);
        Assert.Equal(P(pair.Target, TipVertex(true)), P(smoothed.Target, TipVertex(true)));

        string Line(List<string> lines, string start) => lines.First(l => l.StartsWith(start));
        Assert.Equal(Line(both, "refit nipple smooth: side R tip")[..58], Line(one, "refit nipple smooth: side R tip")[..58]);
        Assert.Equal(Axis(Line(both, "bust bridge: axis")), Axis(Line(one, "bust bridge: axis")));

        static string Axis(string line) => line[..line.IndexOf(')')];
    }

    // ── what it never touches ──────────────────────────────────────────────────────────────────────

    /// <summary>[AC-refit-nipple-smooth-5] The body files handed to the refit come out of it byte for byte.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-5")]
    [Trait("Speed", "Slow")]
    public void Plan_never_writes_into_the_body_s_bytes()
    {
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        string target = Hash(pair.TargetModel!), source = Hash(pair.SourceModel!);
        var garment = Garment(0f, 0f, BothCups);
        var plan = BodyRetarget.Plan(ModelPartReader.Read(garment)!, garment, [pair], "_top", replaceSkin: true,
                                     acrossBodies: true, cutHidden: false);
        Assert.True(plan.Report.Smoothed > 0);
        Assert.Equal(target, Hash(pair.TargetModel!));
        Assert.Equal(source, Hash(pair.SourceModel!));
    }

    /// <summary>[AC-refit-nipple-smooth-6] Only the skin the smoothing moved is reshaded, along its new surface; every
    /// other normal — the rest of the skin, all of the cloth — is as it was, nothing turns over, and the output reads back
    /// as one level of detail.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-6")]
    [Trait("Speed", "Slow")]
    public void Only_moved_skin_is_reshaded_and_nothing_folds()
    {
        var garmentBytes = Garment(0f, 0f, BothCups);
        var garment = ModelPartReader.Read(garmentBytes)!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var smoothed = BodyRetarget.SmoothCoveredNipples(garment, [pair], out int moved)[0];
        Assert.True(moved > 0);

        var before = ModelPartReader.Read(pair.TargetModel!)!;
        var after = ModelPartReader.Read(smoothed.TargetModel!)!;
        var g = Surface(Nipple, Nipple);
        var faceNormal = Normals(Enumerable.Range(0, g.Pos.Length).Select(v => P(after, v)).ToArray(), g.Tris);
        var reshaded = new HashSet<int>();
        for (int v = 0; v < g.Pos.Length; v++)
        {
            bool shifted = Vector3.Distance(P(before, v), P(after, v)) > 1e-6f;
            if (!shifted)
            {
                Assert.Equal(N(before, v), N(after, v));
                continue;
            }
            reshaded.Add(v);
            // Never turned against its surface. Not tighter: the reshade averages the new normals over the mesh, which is
            // what keeps the shading smooth, so on a curved breast it sits a little off the raw faces.
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(N(after, v)), faceNormal[v]), -1f, 1f));
            Assert.True(angle <= MathF.PI / 4f, $"vertex {v}: normal {angle * 180 / MathF.PI:0.#}° off its surface");
        }
        Assert.NotEmpty(reshaded);
        // Shading roughness over the reshaded area — the angle between the normals at either end of each edge — no worse
        // than the body's own: the measure the reshade was tuned on.
        double Rough(ModelParts m)
        {
            double sum = 0;
            int n = 0;
            for (int t = 0; t + 2 < g.Tris.Length; t += 3)
                foreach (var (a, b) in new[] { (g.Tris[t], g.Tris[t + 1]), (g.Tris[t + 1], g.Tris[t + 2]), (g.Tris[t + 2], g.Tris[t]) })
                {
                    if (!reshaded.Contains(a) && !reshaded.Contains(b)) continue;
                    sum += MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(N(m, a)), Vector3.Normalize(N(m, b))), -1f, 1f));
                    n++;
                }
            return sum / n;
        }
        Assert.True(Rough(after) <= Rough(before), $"shading roughness {Rough(before) * 180 / Math.PI:0.##}° -> {Rough(after) * 180 / Math.PI:0.##}°");
        for (int t = 0; t + 2 < g.Tris.Length; t += 3)
        {
            var was = Vector3.Cross(P(before, g.Tris[t + 1]) - P(before, g.Tris[t]), P(before, g.Tris[t + 2]) - P(before, g.Tris[t]));
            var now = Vector3.Cross(P(after, g.Tris[t + 1]) - P(after, g.Tris[t]), P(after, g.Tris[t + 2]) - P(after, g.Tris[t]));
            Assert.True(Vector3.Dot(was, now) > 0f, $"triangle {t / 3} turned over");
        }

        var on = BodyRetarget.Plan(garment, garmentBytes, [pair], "_top", replaceSkin: true, acrossBodies: true, cutHidden: false);
        var off = BodyRetarget.WithTuning(Off, () => BodyRetarget.Plan(garment, garmentBytes, [pair], "_top",
                                                                        replaceSkin: true, acrossBodies: true, cutHidden: false));
        Assert.True(on.Report.Folded <= off.Report.Folded);
        var drawn = ModelPartReader.Read(on.Model)!;
        Assert.NotNull(ModelSkinReader.Read(on.Model, null, null));
        Assert.Equal(1, ModelLodTrimmer.LodCount(on.Model));
        var cloth = drawn.Parts.Where(p => p.Island < 0 && p.Material == Cloth).SelectMany(p => p.Triangles).Distinct().ToList();
        var authored = garment.Parts.Where(p => p.Island < 0 && p.Material == Cloth).SelectMany(p => p.Triangles).Distinct().ToList();
        Assert.Equal(authored.Count, cloth.Count);
        Assert.Equal(authored.Select(v => N(garment, v)), cloth.Select(v => N(drawn, v)));
    }

    /// <summary>[AC-refit-nipple-smooth-9] [EDGE-refit-nipple-smooth-16] A legs pair, even over a nippled chest, comes
    /// out exactly as before; the same chest as the <c>_top</c> pair, the control, does not.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-9")]
    [Trait("Plan", "EDGE-refit-nipple-smooth-16")]
    [Trait("Speed", "Slow")]
    public void Only_the_chest_pair_is_smoothed()
    {
        Assert.Equal(PinnedHash("legs slot"), Hash(On("legs slot").Model));
        Assert.NotEqual(PinnedHash("flat to nippled"), Hash(On("flat to nippled").Model));

        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var chest = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var legs = Pair(Body(0f, 0f), Body(Nipple, Nipple), "_dwn");
        IReadOnlyList<BodyRetarget.SlotPair> pairs = [chest with { ShapeOnly = true }, legs];
        Assert.Same(pairs, BodyRetarget.SmoothCoveredNipples(garment, pairs, out int moved));
        Assert.Equal(0, moved);

        // A glove's or shoe's limb refit (KeepEnds passes the limb as swapSlot, "_top" for a glove's arm).
        IReadOnlyList<BodyRetarget.SlotPair> limb = [chest];
        Assert.Same(limb, BodyRetarget.SmoothCoveredNipples(garment, limb, out moved, swapSlot: "_top"));
        Assert.Equal(0, moved);
        // The control: the same chest pair alone, with no swap slot, is smoothed.
        Assert.NotSame(limb, BodyRetarget.SmoothCoveredNipples(garment, limb, out moved));
    }

    /// <summary>[AC-refit-nipple-smooth-9] [EDGE-refit-nipple-smooth-2] A body the bust bones never reach is left as it
    /// was.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-9")]
    [Trait("Plan", "EDGE-refit-nipple-smooth-2")]
    [Trait("Speed", "Slow")]
    public void Body_without_bust_bones_is_unchanged()
    {
        Assert.Equal(PinnedHash("no bust bones"), Hash(On("no bust bones").Model));
        Assert.NotEqual(PinnedHash("flat to nippled"), Hash(On("flat to nippled").Model));
    }

    /// <summary>[AC-refit-nipple-smooth-9] [EDGE-refit-nipple-smooth-1] A pair without its body's file is passed through;
    /// the same pair with it, the control, is smoothed.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-9")]
    [Trait("Plan", "EDGE-refit-nipple-smooth-1")]
    [Trait("Speed", "Slow")]
    public void Pair_without_a_body_file_is_left_alone()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        IReadOnlyList<BodyRetarget.SlotPair> bare = [pair with { TargetModel = null }];
        Assert.Same(bare, BodyRetarget.SmoothCoveredNipples(garment, bare, out int none));
        Assert.Equal(0, none);
        BodyRetarget.SmoothCoveredNipples(garment, [pair], out int some);
        Assert.True(some > 0);
    }

    /// <summary>[EDGE-refit-nipple-smooth-3] A back piece covers nothing of the bust: the refit is as it was. Cloth over
    /// the front, the control, is not.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-3")]
    [Trait("Speed", "Slow")]
    public void Cloth_off_the_bust_changes_nothing()
    {
        Assert.Equal(PinnedHash("cloth behind"), Hash(On("cloth behind").Model));
        Assert.NotEqual(PinnedHash("flat to nippled"), Hash(On("flat to nippled").Model));
    }

    /// <summary>[EDGE-refit-nipple-smooth-22] Cloth too small to seed the region smooths nothing, and says why.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-22")]
    [Trait("Speed", "Slow")]
    public void Too_little_coverage_smooths_nothing()
    {
        var tip = Tip(false);
        var bytes = Garment(0f, 0f, (tip.X - 0.004f, tip.Y - 0.004f, tip.X + 0.004f, tip.Y + 0.004f));
        var garment = ModelPartReader.Read(bytes)!;
        var lines = new List<string>();
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var on = BodyRetarget.Plan(garment, bytes, [pair], "_top", replaceSkin: true, acrossBodies: true, cutHidden: false,
                                   log: lines.Add);
        var off = BodyRetarget.WithTuning(Off, () => BodyRetarget.Plan(garment, bytes, [pair], "_top", replaceSkin: true,
                                                                        acrossBodies: true, cutHidden: false));
        foreach (var l in lines) output.WriteLine(l);
        Assert.Equal(Hash(off.Model), Hash(on.Model));
        Assert.Contains(lines, l => l.Contains("SKIPPED"));
    }

    // ── the field ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A correspondence over another, with holes where <paramref name="hole"/> says.</summary>
    private sealed class Holed(IBodyCorrespondence inner, Func<int, bool> hole, Func<int, Vector3?>? replace = null)
        : IBodyCorrespondence
    {
        private readonly Vector3?[] entries = Enumerable.Range(0, inner.Field.Count)
            .Select(s => hole(s) ? null : replace?.Invoke(s) ?? inner.Field[s]).ToArray();

        public ModelParts Source => inner.Source;
        public IReadOnlyList<Vector3?> Field => entries;
        public string Describe() => inner.Describe();
    }

    /// <summary>[EDGE-refit-nipple-smooth-5] A hole in the atlas stays a hole, and a landing too far off the target to
    /// say where it is keeps its field.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-5")]
    public void Atlas_holes_stay_holes()
    {
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var tipVertex = new BodyRetarget.SkinIndex(pair.Target).Within(
            new Vector3(Tip(false).X, Tip(false).Y, Z(Tip(false).X, Tip(false).Y, Nipple, Nipple)), 0.004f).First();
        var holed = new Holed(pair.Correspondence, s => s % 7 == 0,
                              s => s == tipVertex ? pair.Correspondence.Field[s]!.Value + new Vector3(0, 0, 0.006f) : null);
        var delta = Enumerable.Repeat(new Vector3(0, 0, -0.001f), pair.Target.Positions.Length / 3).ToArray();
        var shifted = new SmoothedCorrespondence(holed, pair.Target, delta);
        for (int s = 0; s < holed.Field.Count; s++)
        {
            if (holed.Field[s] == null) Assert.Null(shifted.Field[s]);
            else if (s == tipVertex) Assert.Equal(holed.Field[s], shifted.Field[s]);
        }
    }

    /// <summary>[EDGE-refit-nipple-smooth-6] Through the identity, each landing is its own target vertex, and moves
    /// exactly as that vertex did.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-6")]
    public void Identity_landing_takes_its_own_vertex_s_shift()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var smoothed = BodyRetarget.SmoothCoveredNipples(garment, [pair], out _)[0];
        int moved = 0;
        foreach (int v in new BodyRetarget.SkinIndex(pair.Target).Vertices)
        {
            var delta = P(smoothed.Target, v) - P(pair.Target, v);
            var shift = smoothed.Correspondence.Field[v]!.Value - pair.Correspondence.Field[v]!.Value;
            Assert.True(Vector3.Distance(delta, shift) < 1e-6f, $"vertex {v}: shift {shift} against {delta}");
            if (delta.Length() > 1e-5f) moved++;
        }
        Assert.True(moved > 0);
    }

    /// <summary>[EDGE-refit-nipple-smooth-7] Through the uv atlas (a coarser source), each landing moves with the
    /// smoothed surface under it: it lies on the smoothed target as it lay on the unsmoothed one.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-7")]
    public void UvAtlas_shift_is_the_barycentric_blend()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var pair = Pair(Body(0f, 0f, step: 2), Body(Nipple, Nipple));
        Assert.IsNotType<IdentityCorrespondence>(pair.Correspondence);
        var smoothed = BodyRetarget.SmoothCoveredNipples(garment, [pair], out int moved)[0];
        Assert.True(moved > 0);
        var was = new BodySurface(pair.Target, 0.01f);
        var now = new BodySurface(smoothed.Target, 0.01f);
        var source = pair.Correspondence.Source;
        int checkedShifted = 0;
        for (int s = 0; s < source.Positions.Length / 3; s++)
        {
            if (pair.Correspondence.Field[s] is not { } d) continue;
            if (!was.Nearest(P(source, s) + d, 1e-4f, out _)) continue;
            var landing = P(source, s) + smoothed.Correspondence.Field[s]!.Value;
            Assert.True(now.Nearest(landing, 1e-4f, out _), $"source {s} landed off the smoothed body");
            if (smoothed.Correspondence.Field[s] != d) checkedShifted++;
        }
        Assert.True(checkedShifted > 0);
    }

    // ── what covers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>[EDGE-refit-nipple-smooth-19] Where the correspondence has a hole at the source's nipple, nothing says
    /// what the source had there: it counts as flat, and that side is smoothed at full strength — here, though the source's
    /// nipple was as proud as the target's. The other side, its landing intact, is left alone.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-19")]
    public void A_hole_at_the_source_nipple_counts_as_flat()
    {
        var garment = ModelPartReader.Read(Garment(Nipple, Nipple, BothCups))!;
        var pair = Pair(Body(Nipple, Nipple, piercing: true), Body(Nipple, Nipple));
        var source = pair.Correspondence.Source;
        var left = Tip(true);
        var holed = pair with
        {
            Correspondence = new Holed(pair.Correspondence,
                s => Vector2.Distance(new Vector2(P(source, s).X, P(source, s).Y), left) < 0.012f),
        };
        var lines = new List<string>();
        BodyRetarget.SmoothCoveredNipples(garment, [holed], out _, lines.Add);
        foreach (var l in lines) output.WriteLine(l);
        string L = lines.First(l => l.StartsWith("refit nipple smooth: side L tip"));
        string R = lines.First(l => l.StartsWith("refit nipple smooth: side R tip"));
        Assert.Contains("source prom 0 mm", L);
        Assert.True(Excess(lines, true) > BodyRetarget.ExcessFloor);
        Assert.Equal(0f, float.Parse(R[(R.LastIndexOf("scale ", StringComparison.Ordinal) + "scale ".Length)..],
                                     System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>[EDGE-refit-nipple-smooth-8] New-body skin with no source landing near it is not covered.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-8")]
    public void Target_skin_far_from_any_landing_is_uncovered()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var source = pair.Correspondence.Source;
        var holed = pair with { Correspondence = new Holed(pair.Correspondence, s => P(source, s).X > 0f) };
        var covered = BodyRetarget.CoveredTargetSkin(garment, holed, out _);
        var skin = new BodyRetarget.SkinIndex(pair.Target).Vertices;
        Assert.DoesNotContain(skin, v => P(pair.Target, v).X > NippleLandingMatchPlus && covered[v]);
        Assert.Contains(skin, v => P(pair.Target, v).X < -0.05f && covered[v]);
    }

    private const float NippleLandingMatchPlus = BodyRetarget.NippleLandingMatch + 0.001f;

    /// <summary>[EDGE-refit-nipple-smooth-25] The refit's gate never answers "everything": a mesh it cannot place gets
    /// nothing covered, and is drawn in full.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-25")]
    public void Refit_gate_never_returns_null()
    {
        var parts = ModelPartReader.Read(Body(Nipple, Nipple))!;
        var all = Enumerable.Repeat(true, parts.Positions.Length / 3).ToArray();
        var span = parts.MeshSpans[0];
        foreach (var (mesh, count) in new[] { (99, 10), (span.Mesh, span.Count + 1) })
        {
            var covered = BodyRetarget.SliceOf(all, parts, mesh, count, false);
            Assert.Equal(count, covered.Length);
            Assert.DoesNotContain(true, covered);
            Assert.DoesNotContain(false, BodyRetarget.SliceOf(new bool[all.Length], parts, mesh, count, true));
        }
        Assert.DoesNotContain(false, BodyRetarget.SliceOf(all, parts, span.Mesh, span.Count, false));
    }

    /// <summary>[EDGE-refit-nipple-smooth-11] Cloth the source's own nipple already stands through still covers it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-11")]
    public void Cloth_the_source_nipple_pokes_through_still_covers()
    {
        var garment = ModelPartReader.Read(Garment(Nipple, Nipple, BothCups, off: -0.001f))!;
        var pair = Pair(Body(Nipple, Nipple, piercing: true), Body(Nipple, Nipple));
        var covered = BodyRetarget.CoveredTargetSkin(garment, pair, out _);
        var tip = Tip(true);
        var near = new BodyRetarget.SkinIndex(pair.Target).Within(new Vector3(tip.X, tip.Y, Z(tip.X, tip.Y, Nipple, Nipple)), 0.01f).ToList();
        Assert.NotEmpty(near);
        Assert.All(near, v => Assert.True(covered[v]));
    }

    /// <summary>[EDGE-refit-nipple-smooth-12] A copy of the skin under another material — an author's sculpted skin —
    /// is not cloth: with nothing else over the chest, the refit is as it was.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-12")]
    [Trait("Speed", "Slow")]
    public void Skin_copy_under_a_custom_material_is_not_cover()
        => Assert.Equal(PinnedHash("skin copy"), Hash(On("skin copy").Model));

    /// <summary>[EDGE-refit-nipple-smooth-12] An exact copy of the skin, and one sculpted 0.9 mm off it, are skin too.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.0009f)]
    [Trait("Plan", "EDGE-refit-nipple-smooth-12")]
    public void Exact_and_sculpted_skin_copies_are_not_cover(float off)
    {
        var bytes = Garment(0f, 0f, (0f, 0f, 0f, 0f), skinCopyMaterial: "/mt_c0201e9999_top_skin.mtrl", skinCopyOff: off);
        var garment = ModelPartReader.Read(bytes)!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var on = BodyRetarget.Plan(garment, bytes, [pair], "_top", replaceSkin: true, acrossBodies: true, cutHidden: false);
        var off2 = BodyRetarget.WithTuning(Off, () => BodyRetarget.Plan(garment, bytes, [pair], "_top", replaceSkin: true,
                                                                         acrossBodies: true, cutHidden: false));
        Assert.Equal(0, on.Report.Smoothed);
        Assert.Equal(Hash(off2.Model), Hash(on.Model));
    }

    /// <summary>[EDGE-refit-nipple-smooth-12] Cloth painted on within a millimetre, with its own texture layout, is cloth:
    /// it covers the nipple. Judged by distance alone (no uvs) it would read as a copy of the skin, and cover nothing.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-12")]
    [Trait("Speed", "Slow")]
    public void Latex_painted_on_with_its_own_layout_is_cover()
    {
        var bytes = Garment(0f, 0f, BothCups, off: 0.0005f);
        var garment = ModelPartReader.Read(bytes)!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var tip = Tip(true);
        var near = new BodyRetarget.SkinIndex(pair.Target).Within(new Vector3(tip.X, tip.Y, Z(tip.X, tip.Y, Nipple, Nipple)), 0.01f).ToList();
        Assert.NotEmpty(near);

        var byUv = BodyRetarget.CoveredTargetSkin(garment, pair, out _, Uv(bytes));
        var byDistance = BodyRetarget.CoveredTargetSkin(garment, pair, out _);
        Assert.All(near, v => Assert.True(byUv[v]));
        Assert.All(near, v => Assert.False(byDistance[v]));

        var plan = BodyRetarget.Plan(garment, bytes, [pair], "_top", replaceSkin: true, acrossBodies: true, cutHidden: false);
        Assert.True(plan.Report.Smoothed > 0);
    }

    // ── the excess floor and the correction ────────────────────────────────────────────────────────

    /// <summary>[AC-refit-nipple-smooth-3] A source nipple 3.2 mm against the target's 4 reads an excess just under the
    /// floor (0.49 mm): nothing is smoothed. 3.0 mm reads just over it (0.69 mm): the tip comes down by it.</summary>
    [Theory]
    [InlineData(0.0032f, false)]
    [InlineData(0.003f, true)]
    [Trait("Plan", "AC-refit-nipple-smooth-3")]
    [Trait("Speed", "Slow")]
    public void Excess_either_side_of_the_floor(float source, bool smooths)
    {
        var bytes = Garment(source, source, BothCups);
        var garment = ModelPartReader.Read(bytes)!;
        var pair = Pair(Body(source, source), Body(Nipple, Nipple));
        var lines = new List<string>();
        var smoothed = BodyRetarget.SmoothCoveredNipples(garment, [pair], out int moved, lines.Add)[0];
        foreach (var l in lines) output.WriteLine(l);
        float excess = Excess(lines, true);
        if (!smooths)
        {
            Assert.InRange(excess, BodyRetarget.ExcessFloor - 0.0002f, BodyRetarget.ExcessFloor);
            Assert.Equal(0, moved);
            BodyRetarget.Planned Refit() => BodyRetarget.Plan(garment, bytes, [pair], "_top", replaceSkin: true,
                                                              acrossBodies: true, cutHidden: false);
            Assert.Equal(Hash(BodyRetarget.WithTuning(Off, Refit).Model), Hash(Refit().Model));
            return;
        }
        Assert.InRange(excess, BodyRetarget.ExcessFloor, BodyRetarget.ExcessFloor + 0.0003f);
        Assert.True(moved > 0);
        Assert.InRange(TipShare(pair.Target, smoothed.Target, lines, true), 0.85f, 1.15f);
    }

    /// <summary>[AC-refit-nipple-smooth-2] A wider nipple (16 mm) moves less than its share asks on the first pass: the
    /// pass corrects it, and the tip comes down by the excess.</summary>
    [Fact]
    [Trait("Plan", "AC-refit-nipple-smooth-2")]
    public void A_tip_that_misses_its_excess_is_corrected()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var pair = Pair(Body(0f, 0f, spread: 0.016f), Body(Nipple, Nipple, spread: 0.016f));
        var lines = new List<string>();
        var smoothed = BodyRetarget.SmoothCoveredNipples(garment, [pair], out _, lines.Add)[0];
        Assert.Contains(lines, l => l.Contains("tip came down"));
        foreach (bool left in new[] { true, false })
            Assert.InRange(TipShare(pair.Target, smoothed.Target, lines, left), 0.9f, 1.1f);
    }

    /// <summary>[EDGE-refit-nipple-smooth-13] Cloth behind a variant tag covers: which option is drawn is the wearer's.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-13")]
    [Trait("Speed", "Slow")]
    public void Variant_tagged_panels_follow_the_cover_rule()
    {
        var plan = On("variant cloth");
        Assert.NotEqual(PinnedHash("variant cloth"), Hash(plan.Model));
        Assert.True(plan.Report.Smoothed > 0);
    }

    /// <summary>[EDGE-refit-nipple-smooth-17] Cloth the user holds in place is still drawn, and still covers.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-17")]
    public void Held_cloth_still_counts_as_cover()
    {
        var plan = On("held cloth");
        Assert.NotEqual(PinnedHash("held cloth"), Hash(plan.Model));
        Assert.True(plan.Report.Smoothed > 0);
    }

    // ── the new body's own parts ───────────────────────────────────────────────────────────────────

    /// <summary>[EDGE-refit-nipple-smooth-9] An alternative nipple the body mod does not draw, welded to the skin along
    /// its rim, is neither measured nor moved: the drawn skin smooths exactly as on a body without it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-9")]
    public void Hidden_alternative_nipple_sharing_a_seam_is_never_touched()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var hidden = new HashSet<string> { "atr_tv_b" };
        var withAlt = Pair(Body(0f, 0f), Body(Nipple, Nipple, altNipple: 0.003f), hidden: hidden);
        Assert.IsType<UvAtlasCorrespondence>(withAlt.Correspondence);
        var plain = Pair(Body(0f, 0f), Body(Nipple, Nipple), atlas: true);
        var a = BodyRetarget.SmoothCoveredNipples(garment, [withAlt], out _)[0];
        var b = BodyRetarget.SmoothCoveredNipples(garment, [plain], out _)[0];

        var before = ModelPartReader.Read(withAlt.TargetModel!)!;
        var after = ModelPartReader.Read(a.TargetModel!)!;
        int grid = Surface(0f, 0f).Pos.Length;
        for (int v = grid; v < before.Positions.Length / 3; v++) Assert.Equal(P(before, v), P(after, v));
        var plainAfter = ModelPartReader.Read(b.TargetModel!)!;
        Assert.Contains(Enumerable.Range(0, grid), v => Vector3.Distance(P(after, v), P(before, v)) > 1e-4f);
        for (int v = 0; v < grid; v++)
            Assert.True(Vector3.Distance(P(after, v), P(plainAfter, v)) < 1e-6f, $"vertex {v} smoothed differently");
        Assert.DoesNotContain(a.Target.Parts, p => (p.AttributeMask & 1) != 0);
    }

    /// <summary>[EDGE-refit-nipple-smooth-10] A piercing on the nipple moves with the skin, keeps its normals, is never
    /// buried, and is never taken for the nipple.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-10")]
    public void A_nipple_piercing_moves_with_the_skin_and_does_not_raise_the_ruler()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var pierced = Pair(Body(0f, 0f), Body(Nipple, Nipple, piercing: true));
        var bare = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var withRing = new List<string>();
        var without = new List<string>();
        var smoothed = BodyRetarget.SmoothCoveredNipples(garment, [pierced], out _, withRing.Add)[0];
        BodyRetarget.SmoothCoveredNipples(garment, [bare], out _, without.Add);
        Assert.Equal(without.Where(l => l.Contains("target prom")), withRing.Where(l => l.Contains("target prom")));

        var before = ModelPartReader.Read(pierced.TargetModel!)!;
        var after = ModelPartReader.Read(smoothed.TargetModel!)!;
        var skin = new BodySurface(after, 0.01f);
        var ring = before.Parts.Where(p => p.Material == Piercing).SelectMany(p => p.Triangles).Distinct().ToList();
        Assert.NotEmpty(ring);
        foreach (int v in ring)
        {
            Assert.True(P(after, v).Z < P(before, v).Z - 0.0005f, $"ring vertex {v} did not follow the skin down");
            Assert.Equal(N(before, v), N(after, v));
            Assert.True(skin.Nearest(P(after, v), 0.02f, out var hit));
            Assert.True(Vector3.Dot(P(after, v) - hit.Point, hit.Normal) >= -0.0001f, $"ring vertex {v} inside the skin");
        }
    }

    /// <summary>[EDGE-refit-nipple-smooth-14] A cloth edge running through the nipple leaves no step in the skin and
    /// turns nothing over.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-14")]
    public void Cloth_edge_across_the_nipple_leaves_no_step_or_fold()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, (Centre, Base + 0.06f, 0.17f, Base + 0.24f)))!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var smoothed = BodyRetarget.SmoothCoveredNipples(garment, [pair], out int moved)[0];
        Assert.True(moved > 0);
        var g = Surface(Nipple, Nipple);
        float worst = 0f;
        for (int t = 0; t + 2 < g.Tris.Length; t += 3)
        {
            int a = g.Tris[t], b = g.Tris[t + 1], c = g.Tris[t + 2];
            Vector3 D(int v) => P(smoothed.Target, v) - P(pair.Target, v);
            worst = MathF.Max(worst, MathF.Max(Vector3.Distance(D(a), D(b)), MathF.Max(Vector3.Distance(D(b), D(c)), Vector3.Distance(D(c), D(a)))));
            var was = Vector3.Cross(P(pair.Target, b) - P(pair.Target, a), P(pair.Target, c) - P(pair.Target, a));
            var now = Vector3.Cross(P(smoothed.Target, b) - P(smoothed.Target, a), P(smoothed.Target, c) - P(smoothed.Target, a));
            Assert.True(Vector3.Dot(was, now) > 0f, $"triangle {t / 3} turned over");
        }
        output.WriteLine($"largest movement step across an edge: {worst * 1000:0.###} mm");
        Assert.True(worst <= 0.0005f);
    }

    /// <summary>[EDGE-refit-nipple-smooth-15] A body that cannot be smoothed, or a pass that throws, leaves the refit as
    /// it would have been, and says so.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-15")]
    public void A_body_that_cannot_be_smoothed_refits_as_before()
    {
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var pair = Pair(Body(0f, 0f), Body(Nipple, Nipple));
        var lines = new List<string>();

        IReadOnlyList<BodyRetarget.SlotPair> garbage = [pair with { TargetModel = new byte[64] }];
        Assert.Same(garbage, BodyRetarget.SmoothCoveredNipples(garment, garbage, out int none, lines.Add));
        Assert.Equal(0, none);
        Assert.Contains(lines, l => l.Contains("left as it was"));

        lines.Clear();
        IReadOnlyList<BodyRetarget.SlotPair> throwing = [pair with { Correspondence = new Throwing(pair.Correspondence) }];
        Assert.Same(throwing, BodyRetarget.SmoothCoveredNipples(garment, throwing, out _, lines.Add));
        Assert.Contains(lines, l => l.Contains("InvalidOperationException"));
    }

    private sealed class Throwing(IBodyCorrespondence inner) : IBodyCorrespondence
    {
        public ModelParts Source => inner.Source;
        public IReadOnlyList<Vector3?> Field => throw new InvalidOperationException("no field");
        public string Describe() => inner.Describe();
    }

    /// <summary>[EDGE-refit-nipple-smooth-18] Without the swap, the garment's own skin follows the smoothed body in
    /// position, and keeps its author's normals.</summary>
    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-18")]
    [Trait("Speed", "Slow")]
    public void Garment_skin_follows_positions_but_keeps_its_normals_without_a_swap()
    {
        var lines = new List<string>();
        var plan = On("flat to nippled, no swap", lines);
        Assert.True(plan.Report.Smoothed > 0);
        var off = ModelPartReader.Read(BodyRetarget.WithTuning(Off, () => Refit("flat to nippled, no swap")).Model)!;
        var garment = ModelPartReader.Read(Garment(0f, 0f, BothCups))!;
        var drawn = ModelPartReader.Read(plan.Model)!;
        // The garment's own skin is mesh 0 in both, vertex for vertex the source grid: its tip came down with the body.
        Assert.InRange(TipShare(off, drawn, lines, true), 0.85f, 1.15f);
        var skin = garment.MeshSpans[0];
        for (int v = 0; v < skin.Count; v++)
            Assert.Equal(N(garment, skin.BaseVertex + v), N(drawn, drawn.MeshSpans[0].BaseVertex + v));
    }
}
