using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// The scalp push (<see cref="HairScalpPush"/>) on synthetic heads: a sphere head per race and a cap of hair over it,
/// so where the bend sinks the hair — and where the push should put it back — is known exactly.
/// </summary>
public class HairScalpPushTests
{
    private static readonly Vector3 Centre = new(0f, 1.5f, 0f);

    /// <summary>A face model: a sphere of <paramref name="radius"/> on the head bone, with extra meshes.</summary>
    internal static byte[] Head(float radius, params SyntheticModel.SurfaceMesh[] extra)
        => SyntheticModel.BuildSurface([], [HairPortFixtures.Sphere("/mt_c0101f0001_fac_a.mtrl", Centre, radius, [("j_kao", 1f)],
                                                                    rings: 48, segments: 64), .. extra]);

    /// <summary>A cap of hair: a sphere of <paramref name="radius"/> down to <paramref name="minY"/> radii below the centre.</summary>
    internal static SyntheticModel.SurfaceMesh Cap(float radius, float minY = -0.2f, string material = "/mt_c0201h0130_hir_a.mtrl",
                                                   (string Bone, float W)[]? bones = null, int rings = 18)
        => HairPortFixtures.Sphere(material, Centre, radius, bones ?? [("j_kao", 1f)], rings: rings, segments: 24, minY: minY);

    internal static byte[] Hair(params SyntheticModel.SurfaceMesh[] meshes) => SyntheticModel.BuildSurface([], meshes);

    private static HairScalpPush.Frame Frame(byte[] head) => HairScalpPush.FrameOf(head)!;

    /// <summary>Every point's distance from <see cref="Centre"/>.</summary>
    private static float[] Radii(byte[] model)
    {
        var p = ModelPartReader.Read(model)!.Positions;
        return Enumerable.Range(0, p.Length / 3)
                         .Select(v => (new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]) - Centre).Length()).ToArray();
    }

    private static float[] Ys(byte[] model)
    {
        var p = ModelPartReader.Read(model)!.Positions;
        return Enumerable.Range(0, p.Length / 3).Select(v => p[v * 3 + 1]).ToArray();
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] The source's cap sits 1 mm off a 9 cm head; on an 11 cm head, unmoved (an identity
    /// bend), it is 19 mm inside. The push gives every cranium point back its source gap, to half a millimetre, and leaves
    /// no created clip.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    public void Sunk_cap_is_pushed_back_to_its_source_gap()
    {
        var source = Frame(Head(0.09f));
        var target = Frame(Head(0.11f));
        var hair = Hair(Cap(0.091f));
        var shown = ModelPartReader.Read(hair)!;
        Assert.True(HairScalpPush.Created(shown, shown, source, target).Count > 0);

        var push = HairScalpPush.Apply(hair, shown, source, target);
        Assert.True(push.Pushed > 0);
        var after = ModelPartReader.Read(push.Model)!;
        Assert.Equal(0, HairScalpPush.Created(shown, after, source, target).Count);
        var radii = Radii(push.Model);
        var ys = Ys(push.Model);
        for (int v = 0; v < radii.Length; v++)
            if (ys[v] >= Centre.Y + 0.001f)
                Assert.InRange(radii[v] - SkullAt(target, after, v), 0.001f - 0.0005f, 0.001f + 0.0005f);
    }

    private static float SkullAt(HairScalpPush.Frame f, ModelParts parts, int v)
    {
        var d = new Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]) - f.Centre;
        return HatCompatSolve.FloorAt(f.Skull, Vector3.Normalize(d));
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] Through the baker, same-sex and across the sexes alike: a target head bigger than
    /// the source's gets its port pushed, and no created clip is left in what Prepare returns.
    /// </summary>
    [Theory]
    [InlineData((ushort)201, (ushort)401)]
    [InlineData((ushort)201, (ushort)101)]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    public void Prepare_pushes_every_port_same_sex_and_cross_sex(ushort from, ushort to)
    {
        var pbd = new PbdFile(HairPortFixtures.BuildPbd(
        [
            (101, ushort.MaxValue, new() { ["j_kao"] = Matrix4x4.Identity }),
            (201, 101, new() { ["j_kao"] = Matrix4x4.Identity }),
            (401, 201, new() { ["j_kao"] = Matrix4x4.Identity }),
        ]));
        var source = Hair(Cap(0.091f, material: $"/mt_c{from:D4}h0130_hir_a.mtrl"));
        var result = HairPortBaker.Prepare(source, from, to, 130, pbd, new HashSet<string>(), Head(0.09f), Head(0.11f), out var why);
        Assert.True(result != null, why);
        Assert.True(result!.Pushed > 0);
        Assert.Equal(0, HairScalpPush.Created(ModelPartReader.Read(source)!, ModelPartReader.Read(result.Model)!,
                                              Frame(Head(0.09f)), Frame(Head(0.11f))).Count);
    }

    /// <summary>
    /// [EDGE-hair-material-scalp-fit-14] An inner layer the author tucked 5 mm inside the source's head never seeds the
    /// push, and where the outer layer is pushed it goes back to its own depth — still inside, never out past the scalp.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-14")]
    public void Hair_tucked_into_the_source_head_stays_tucked()
    {
        var source = Frame(Head(0.09f));
        var target = Frame(Head(0.11f));
        var hair = Hair(Cap(0.091f), Cap(0.085f, material: "/mt_c0201h0130_hir_b.mtrl"));
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, source, target);
        var after = ModelPartReader.Read(push.Model)!;
        var inner = after.MeshSpans[1];
        for (int k = 0; k < inner.Count; k++)
        {
            int v = inner.BaseVertex + k;
            if (after.Positions[v * 3 + 1] < Centre.Y + 0.001f) continue;
            float gap = Radii(push.Model)[v] - SkullAt(target, after, v);
            Assert.True(gap < 0f, $"inner point {k} was pushed out to {gap * 1000:F1} mm");
            Assert.True(gap >= -0.005f - 0.0005f, $"inner point {k} pushed past its authored depth: {gap * 1000:F1} mm");
        }
    }

    /// <summary>
    /// [EDGE-hair-material-scalp-fit-15] A horn on the target's face (a mesh of its own, as an Au Ra's is) is not skull:
    /// the hair beside it is pushed no further than the hair around it.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-15")]
    public void A_horn_on_the_target_head_does_not_spike_the_hair()
    {
        var horn = HairPortFixtures.Sphere("/mt_c0101f0001_fac_c.mtrl", Centre + new Vector3(0.05f, 0.06f, -0.07f), 0.04f,
                                           [("j_kao", 1f)]);
        var source = Frame(Head(0.09f));
        var withHorn = Frame(Head(0.11f, horn));
        var plain = Frame(Head(0.11f));
        var hair = Hair(Cap(0.091f));
        var shown = ModelPartReader.Read(hair)!;
        var a = ModelPartReader.Read(HairScalpPush.Apply(hair, shown, source, withHorn).Model)!.Positions;
        var b = ModelPartReader.Read(HairScalpPush.Apply(hair, shown, source, plain).Model)!.Positions;
        for (int i = 0; i < a.Length; i++) Assert.Equal(b[i], a[i], 1e-4f);
    }

    /// <summary>
    /// [EDGE-hair-material-scalp-fit-16] Strands running from the crown to 6 cm below the head's centre: the push fades
    /// out below the centre, no edge is strained over 25% beyond what the bigger skull asks, and down a strand the push
    /// only ever shrinks.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-16")]
    public void Strands_crossing_the_centre_fade_without_tearing()
    {
        // A 1 cm bigger head — the order of what the game's hairstyles need between most races.
        var source = Frame(Head(0.09f));
        var target = Frame(Head(0.10f));
        var hair = Hair(Cap(0.091f, minY: -0.75f, rings: 72));
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, source, target);
        var before = shown.Positions;
        var after = ModelPartReader.Read(push.Model)!;
        var moved = HairPortScalpLocalTests.Moved(before, after.Positions).ToHashSet();
        var excess = HairScalpPush.ExcessStrain(shown, shown, after, source, target, new HashSet<int>(), moved.Contains);
        Assert.NotEmpty(excess);
        Assert.All(excess, e => Assert.True(e <= 0.25f, $"excess strain {e:F2}"));
        // Rings run crown to chin (72 rings of 24): the push along one meridian never grows going down.
        float last = float.MaxValue;
        for (int r = 0; r <= 72; r++)
        {
            int v = r * 24;
            float shift = Dist2(before, after.Positions, v);
            if (before[v * 3 + 1] < Centre.Y) { Assert.True(shift <= last + 1e-5f, $"ring {r}: {shift} after {last}"); last = shift; }
        }
        Assert.Equal(0f, Dist2(before, after.Positions, 72 * 24), 1e-6f);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-17] A shape key's spares keep their offset from their base: they follow it.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-17")]
    public void Shape_spares_move_with_their_base()
    {
        var hair = HatFitted(out var bases);
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, Frame(Head(0.09f)), Frame(Head(0.11f)));
        Assert.True(push.Pushed > 0);
        var after = ModelPartReader.Read(push.Model)!.Positions;
        foreach (var (b, s) in bases)
        {
            var before = Sub(shown.Positions, s, b);
            var now = Sub(after, s, b);
            Assert.True((before - now).Length() < 1e-4f, $"spare {s} of base {b} lost its offset");
        }
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-8] [EDGE-hair-material-scalp-fit-17] A hat fit pressed onto the scalp — its spares
    /// sunk into a bigger head, on purpose — neither seeds the push nor counts toward the clip warning.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-8")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-17")]
    public void A_hat_fit_shape_on_the_source_never_seeds_or_warns()
    {
        var hair = HatFitted(out var bases);
        var shown = ModelPartReader.Read(hair)!;
        var spares = HairScalpPush.Spares(hair, shown);
        Assert.Equal(bases.Select(x => x.Spare).ToHashSet(), spares);
        // On an 8.5 cm source head the spares (8.6 cm) sit outside it, as created clips would: on a 9.1 cm target they are
        // 5 mm in, while the cap's base lands on the scalp. Only the spares would read as created clips — were they not
        // left out, they would seed the push and move the cap.
        var source = Frame(Head(0.085f));
        var target = Frame(Head(0.091f));
        var counted = HairScalpPush.Created(shown, shown, source, target);
        Assert.True(counted.Count > 0, "the spares must read as created clips when not left out, or this proves nothing");
        Assert.Equal(0, HairScalpPush.Created(shown, shown, source, target, spares).Count);
        var push = HairScalpPush.Apply(hair, shown, source, target);
        Assert.Equal(0, push.Before);
        Assert.Equal(0, push.Pushed);
        Assert.Same(hair, push.Model);
    }

    /// <summary>A cap at 9.1 cm with a "shp_hat" key pressing its crown ring to 8.6 cm: spares sunk 4 mm into a 9 cm head.</summary>
    internal static byte[] HatFitted(out List<(int Base, int Spare)> pairs)
    {
        var cap = Cap(0.091f);
        // The fourth and fifth rings pressed 5 mm in, as Proteus's own hat fit writes them (ModelAttributeWriter.AddShape).
        var pressed = Enumerable.Range(24 * 3, 48)
                                .ToDictionary(v => v, v => Centre + Vector3.Normalize(cap.Positions[v] - Centre) * 0.086f);
        var mesh = SecondSkinWriter.Parse(Hair(cap)).Lod0MeshIndex;
        var model = ModelAttributeWriter.AddShape(Hair(cap), "shp_hat",
            new Dictionary<int, IReadOnlyDictionary<int, Vector3>> { [mesh] = pressed });
        var parts = ModelPartReader.Read(model)!;
        pairs = HairPortScalpLocalTests.ShapePairs(model, parts).ToList();
        Assert.NotEmpty(pairs);
        return model;
    }

    /// <summary>[EDGE-hair-material-scalp-fit-18] The bounding boxes grow with the push: every point inside the box before
    /// is inside it after.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-18")]
    public void Bounding_boxes_hold_every_pushed_vertex()
    {
        var hair = Boxed(Hair(Cap(0.091f)));
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, Frame(Head(0.09f)), Frame(Head(0.11f)));
        Assert.True(push.Pushed > 0);
        var held = Held(hair, shown.Positions);
        Assert.Equal(shown.Positions.Length / 3, held.Count);
        Assert.NotEmpty(held);
        Assert.True(BoxHolds(push.Model, ModelPartReader.Read(push.Model)!.Positions, held));
    }

    /// <summary>The model with its model bounding box set tight around its points, as a real model ships (the synthetic
    /// builder leaves it zero, which the writer leaves alone).</summary>
    private static byte[] Boxed(byte[] model)
    {
        var p = ModelPartReader.Read(model)!.Positions;
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int v = 0; v < p.Length / 3; v++)
        {
            var x = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
            lo = Vector3.Min(lo, x);
            hi = Vector3.Max(hi, x);
        }
        var o = (byte[])model.Clone();
        int at = SecondSkinWriter.Parse(o).ModelBBoxAt;
        for (int c = 0; c < 3; c++)
        {
            BitConverter.GetBytes(c == 0 ? lo.X : c == 1 ? lo.Y : lo.Z).CopyTo(o, at + c * 4);
            BitConverter.GetBytes(c == 0 ? hi.X : c == 1 ? hi.Y : hi.Z).CopyTo(o, at + 16 + c * 4);
        }
        BitConverter.GetBytes(1f).CopyTo(o, at + 12);
        BitConverter.GetBytes(1f).CopyTo(o, at + 28);
        return o;
    }

    /// <summary>The points of <paramref name="positions"/> inside <paramref name="model"/>'s model bounding box.</summary>
    internal static HashSet<int> Held(byte[] model, float[] positions)
    {
        var (lo, hi) = Box(model);
        return Enumerable.Range(0, positions.Length / 3).Where(v => Inside(positions, v, lo, hi)).ToHashSet();
    }

    /// <summary>Whether <paramref name="model"/>'s model bounding box holds every point of <paramref name="which"/>.</summary>
    internal static bool BoxHolds(byte[] model, float[] positions, IEnumerable<int> which)
    {
        var (lo, hi) = Box(model);
        return which.All(v => Inside(positions, v, lo, hi));
    }

    private static (Vector3 Lo, Vector3 Hi) Box(byte[] model)
    {
        var src = SecondSkinWriter.Parse(model);
        Vector3 Read(int at) => new(BitConverter.ToSingle(model, at), BitConverter.ToSingle(model, at + 4), BitConverter.ToSingle(model, at + 8));
        return (Read(src.ModelBBoxAt), Read(src.ModelBBoxAt + 16));
    }

    private static bool Inside(float[] p, int v, Vector3 lo, Vector3 hi)
        => p[v * 3] >= lo.X - 1e-4f && p[v * 3] <= hi.X + 1e-4f && p[v * 3 + 1] >= lo.Y - 1e-4f && p[v * 3 + 1] <= hi.Y + 1e-4f
           && p[v * 3 + 2] >= lo.Z - 1e-4f && p[v * 3 + 2] <= hi.Z + 1e-4f;

    /// <summary>[EDGE-hair-material-scalp-fit-19] A target head no bigger than the source's: nothing sinks, nothing moves,
    /// and Prepare's model is byte for byte the bend with its materials renamed.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-19")]
    public void Nothing_created_leaves_the_bend_byte_identical()
    {
        var hair = Hair(Cap(0.091f));
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, Frame(Head(0.09f)), Frame(Head(0.085f)));
        Assert.Equal(0, push.Pushed);
        Assert.Same(hair, push.Model);

        var pbd = HairPortFixtures.Pbd();
        var result = HairPortBaker.Prepare(hair, 201, 401, 130, pbd, new HashSet<string>(), Head(0.09f), Head(0.085f), out var why);
        Assert.True(result != null, why);
        var bent = RacialModelBake.Port(hair, 201, 401, pbd, HairPortBaker.ParentOf, out _)!;
        bent = ModelAttributeWriter.RenameMaterial(bent, "/mt_c0201h0130_hir_a.mtrl", "/mt_c0401h0130_hir_a.mtrl");
        Assert.Equal(bent, result!.Model);
        Assert.Equal(0, result.Pushed);
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-8] [EDGE-hair-material-scalp-fit-20] [EDGE-hair-material-scalp-fit-29] Sinking the push
    /// cannot reach — deeper than <see cref="HairScalpPush.MaxPush"/> — is saved with the clip warning, after any other,
    /// joined with "; ". The limit is half a percent of the cranium, never under 20 nor over 50.
    /// </summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-8")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-20")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-29")]
    public void A_push_held_at_MaxPush_leaves_the_clip_warning()
    {
        Assert.Equal(20, HairScalpPush.ClipLimit(1000));
        Assert.Equal(30, HairScalpPush.ClipLimit(6000));
        Assert.Equal(50, HairScalpPush.ClipLimit(20000));

        // The source cap stands 1 mm off; the target head is 11 cm bigger — past the push's ceiling (MaxPush), so it stays sunk.
        var pbd = HairPortFixtures.Pbd();
        var hair = Hair(Cap(0.091f));
        var result = HairPortBaker.Prepare(hair, 201, 401, 130, pbd, new HashSet<string>(), Head(0.09f), Head(0.2f), out var why);
        Assert.True(result != null, why);
        Assert.True(result!.Clips > HairScalpPush.ClipLimit(500));
        var push = HairScalpPush.Apply(RacialModelBake.Port(hair, 201, 401, pbd, HairPortBaker.ParentOf, out _)!,
                                       ModelPartReader.Read(hair)!, Frame(Head(0.09f)), Frame(Head(0.2f)));
        Assert.True(push.Capped > 0);
        Assert.InRange(push.Worst, HairScalpPush.MaxPush - 1e-4f, HairScalpPush.MaxPush + 1e-4f);
        var clip = string.Format(ProteusLoc.Localize("HairPort.Clip.Fmt",
            "bent onto c{0:D4}, {1} of its points sink into the head where the original's do not, which may show the " +
            "scalp; check it in game"), 401, result.Clips);
        Assert.Contains(clip, result.Warning);
        Assert.Contains("; ", result.Warning);   // the fit warning first: this cap does not sit on that head at all
        Assert.EndsWith(clip, result.Warning);
    }

    /// <summary>[AC-hair-material-scalp-fit-8] The new words are in every language, each its own translation.</summary>
    [Theory]
    [InlineData("HairPort.Clip.Fmt")]
    [InlineData("HairPort.Undo.Also.Fmt")]
    [Trait("Plan", "AC-hair-material-scalp-fit-8")]
    public void New_strings_are_translated(string key)
    {
        string Message(string lang)
        {
            using var stream = typeof(Proteus.Plugin).Assembly.GetManifestResourceStream($"Proteus.Localization.{lang}.json")!;
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return doc.RootElement.GetProperty(key).GetProperty("message").GetString()!;
        }
        var en = Message("en");
        foreach (var lang in new[] { "de", "es", "fr", "ja", "ko", "ru", "zh" })
            Assert.NotEqual(en, Message(lang));
    }

    /// <summary>[AC-hair-material-scalp-fit-7] A cap pushed evenly onto a bigger head grows ring by ring by (r+d)/r — what
    /// wrapping it asks — and scores no excess strain.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-7")]
    public void Ring_growth_scores_no_excess_strain()
    {
        var source = Frame(Head(0.09f));
        var target = Frame(Head(0.11f));
        // The cranium only: below the centre the push fades out, which is not ring growth.
        var hair = Hair(Cap(0.091f, minY: 0.2f));
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, source, target);
        var after = ModelPartReader.Read(push.Model)!;
        var moved = HairPortScalpLocalTests.Moved(shown.Positions, after.Positions).ToHashSet();
        var excess = HairScalpPush.ExcessStrain(shown, shown, after, source, target, new HashSet<int>(), moved.Contains);
        Assert.NotEmpty(excess);
        Assert.True(excess.Max() <= 0.01f, $"worst excess {excess.Max():F3}, {excess.Count(e => e > 0.01f)} of {excess.Count} over 0.01");
    }

    /// <summary>[AC-hair-material-scalp-fit-7] The metric catches a kink: one point of a strand moved 2 cm out while its
    /// neighbours stay — no skull asks for that — strains its edges far over 25%.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-7")]
    public void A_kink_scores_excess_strain()
    {
        var frame = Frame(Head(0.09f));
        var cap = Cap(0.091f, minY: 0f);
        var shown = ModelPartReader.Read(Hair(cap))!;
        int v = 24 * 6 + 3;
        var kinked = (Vector3[])cap.Positions.Clone();
        kinked[v] += Vector3.Normalize(kinked[v] - Centre) * 0.02f;
        var port = ModelPartReader.Read(Hair(cap with { Positions = kinked }))!;
        var excess = HairScalpPush.ExcessStrain(shown, shown, port, frame, frame, new HashSet<int>(), i => i == v);
        Assert.Contains(excess, e => e > 0.25f);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-28] A face with no skin mesh has no scalp to measure: no frame, no push, no
    /// clip warning — the port is the bend with its materials renamed, byte for byte.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-28")]
    public void A_face_with_no_skin_mesh_gets_no_push()
    {
        var bare = SyntheticModel.BuildSurface([], [HairPortFixtures.Sphere("/mt_c0401f0001_etc_a.mtrl", Centre, 0.11f, [("j_kao", 1f)],
                                                                            rings: 48, segments: 64)]);
        Assert.Null(HairScalpPush.FrameOf(bare));
        var pbd = HairPortFixtures.Pbd();
        var hair = Hair(Cap(0.091f));
        var result = HairPortBaker.Prepare(hair, 201, 401, 130, pbd, new HashSet<string>(), Head(0.09f), bare, out var why);
        Assert.True(result != null, why);
        Assert.Equal(0, result!.Pushed);
        Assert.True(result.NoScalp);
        Assert.DoesNotContain("of its points sink into the head", result.Warning ?? "");
        var bent = RacialModelBake.Port(hair, 201, 401, pbd, HairPortBaker.ParentOf, out _)!;
        Assert.Equal(ModelAttributeWriter.RenameMaterial(bent, "/mt_c0201h0130_hir_a.mtrl", "/mt_c0401h0130_hir_a.mtrl"), result.Model);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-15] An ear or horn inside the skin mesh itself (an Elezen's, a Lalafell's, a
    /// Miqo'te's ear base): the skull clips a bin standing 20 mm proud of its neighbourhood to the neighbourhood's median plus
    /// the margin, and leaves every other bin as it was.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-15")]
    public void The_skull_clips_an_ear_in_the_skin_mesh()
    {
        const int U = HatCompatSolve.FloorBinsU;
        var floor = Enumerable.Repeat(0.09f, U * HatCompatSolve.FloorBinsV).ToArray();
        int ear = 5 * U + 9;
        floor[ear] = 0.11f;
        var skull = HairScalpPush.Skull(floor);
        Assert.Equal(0.09f + HairScalpPush.ProtrusionMargin, skull[ear], 1e-5f);
        for (int b = 0; b < floor.Length; b++)
            if (b != ear) Assert.Equal(0.09f, skull[b], 1e-6f);
    }

    /// <summary>[EDGE-hair-material-scalp-fit-15] Through the push: hair beside a 2 cm bump in the skin mesh's own
    /// triangles is pushed no further than on the same head without it plus the protrusion margin — the most of a bump the
    /// skull ever keeps.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-15")]
    public void An_ear_in_the_skin_mesh_does_not_spike_the_hair()
    {
        var bump = HairPortFixtures.Sphere("/mt_c0101f0001_fac_a.mtrl", Centre + new Vector3(0.1f, 0.03f, 0f), 0.025f, [("j_kao", 1f)]);
        var source = Frame(Head(0.09f));
        var withEar = Frame(Head(0.11f, bump));
        var plain = Frame(Head(0.11f));
        var hair = Hair(Cap(0.091f));
        var shown = ModelPartReader.Read(hair)!;
        var a = Radii(HairScalpPush.Apply(hair, shown, source, withEar).Model);
        var b = Radii(HairScalpPush.Apply(hair, shown, source, plain).Model);
        // A bump in the skin mesh counts as skull up to the protrusion margin and no further: never the bump's 2 cm.
        for (int i = 0; i < a.Length; i++)
            Assert.True(a[i] <= b[i] + HairScalpPush.ProtrusionMargin + 0.001f, $"point {i}: {a[i] * 1000:F1} mm vs {b[i] * 1000:F1} mm");
    }

    /// <summary>[AC-hair-material-scalp-fit-6] The independent skin check has teeth on a small port: a few points the bend
    /// sank 19 mm, with the push turned off (the bend compared with itself), fail its per-port rule — the floor for the two
    /// measures' disagreement does not cover points that deep.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    public void The_independent_check_catches_a_missed_push_on_a_small_port()
    {
        var sourceHead = Head(0.09f);
        var targetHead = Head(0.11f);
        var source = Frame(sourceHead);
        var target = Frame(targetHead);
        // A small patch on the cranium: a few dozen points at the upper back.
        var patch = HairPortFixtures.Sphere("/mt_c0201h0130_hir_a.mtrl", Centre, 0.091f, [("j_kao", 1f)], rings: 8, segments: 12, minY: 0.3f);
        var hair = Hair(patch);
        var shown = ModelPartReader.Read(hair)!;
        var r = HairPortScalpLocalTests.InsideSkin(HairPortScalpLocalTests.SkinOf(targetHead, target),
                                                   HairPortScalpLocalTests.SkinOf(sourceHead, source), shown, shown, shown);
        Assert.InRange(r.Before, 12, 60);
        Assert.Equal(r.Before, r.After);
        // Inside by the push's own radial gap too: a miss by both measures, not their disagreement.
        Assert.Equal(r.Deep, r.MissedBoth);
        Assert.True(r.Deep > 0);
        foreach (ushort to in new ushort[] { 401, 1501 })   // Hrothgar's looser deep bound still fails it
        {
            var fails = new List<string>();
            HairPortScalpLocalTests.CheckPort("missed", to, r, (ok, what) => { if (!ok) fails.Add(what); });
            Assert.NotEmpty(fails);
        }

        // The control: the push on, the same patch passes — the rule is not one that always fails.
        var pushed = ModelPartReader.Read(HairScalpPush.Apply(hair, shown, source, target).Model)!;
        var c = HairPortScalpLocalTests.InsideSkin(HairPortScalpLocalTests.SkinOf(targetHead, target),
                                                   HairPortScalpLocalTests.SkinOf(sourceHead, source), shown, shown, pushed);
        Assert.Equal(0, c.Deep);
        foreach (ushort to in new ushort[] { 401, 1501 })
        {
            var fails = new List<string>();
            HairPortScalpLocalTests.CheckPort("pushed", to, c, (ok, what) => { if (!ok) fails.Add(what); });
            Assert.Empty(fails);
        }
    }

    /// <summary>[EDGE-hair-material-scalp-fit-21] A Miqo'te hairstyle with ears, to a race without: the fur is hidden, the
    /// ear bones ride the head, and the push still happens.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-21")]
    public void Miqote_source_with_fur_still_pushes()
    {
        var pbd = HairPortFixtures.Pbd();
        var hair = HairPortFixtures.EaredHairModel(801, radius: 0.091f);
        var result = HairPortBaker.Prepare(hair, 801, 201, 130, pbd, new HashSet<string>(),
                                           HairPortFixtures.HeadModel(801, 0.09f), HairPortFixtures.HeadModel(201, 0.11f), out var why);
        Assert.True(result != null, why);
        Assert.True(result!.FurHidden > 0);
        Assert.True(result.Pushed > 0);
        Assert.DoesNotContain(SecondSkinWriter.Parse(result.Model).BoneNames, b => b.StartsWith("j_mimi", StringComparison.Ordinal));
    }

    /// <summary>[EDGE-hair-material-scalp-fit-23] The push moves points only: bone names, indices and weights are the
    /// bend's, so physics strands keep their bones.</summary>
    [Fact]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-23")]
    public void Push_leaves_weights_bones_and_skeleton_alone()
    {
        var hair = Hair(Cap(0.091f, bones: [("j_kao", 0.5f), ("j_ex_h0130_ke_b", 0.5f)]));
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, Frame(Head(0.09f)), Frame(Head(0.11f)));
        Assert.True(push.Pushed > 0);
        var a = ModelSkinReader.Read(hair, null, null)!;
        var b = ModelSkinReader.Read(push.Model, null, null)!;
        Assert.Equal(a.BoneNames, b.BoneNames);
        Assert.Equal(a.BoneIndices, b.BoneIndices);
        Assert.Equal(a.BoneWeights, b.BoneWeights);
        Assert.Equal(SecondSkinWriter.Parse(hair).BoneNames, SecondSkinWriter.Parse(push.Model).BoneNames);
    }

    /// <summary>[AC-hair-material-scalp-fit-7] The push turns no triangle over.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-7")]
    public void Push_adds_no_folded_triangles()
    {
        var hair = Hair(Cap(0.091f), Cap(0.085f, material: "/mt_c0201h0130_hir_b.mtrl"));
        var shown = ModelPartReader.Read(hair)!;
        var push = HairScalpPush.Apply(hair, shown, Frame(Head(0.09f)), Frame(Head(0.11f)));
        Assert.True(push.Pushed > 0);
        Assert.Equal(0, HairPortBaker.Folded(shown, ModelPartReader.Read(push.Model)!));
    }

    /// <summary>[AC-hair-material-scalp-fit-7] The hat press shares the floor helpers the push reads: its behaviour, and so
    /// its version, is unchanged.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-7")]
    public void HatCompat_behaviour_is_unchanged() => Assert.Equal(33, HatCompatSolve.Version);

    private static float Dist(float[] p, int a, int b) => Sub(p, a, b).Length();

    private static float Dist2(float[] a, float[] b, int v)
        => new Vector3(a[v * 3] - b[v * 3], a[v * 3 + 1] - b[v * 3 + 1], a[v * 3 + 2] - b[v * 3 + 2]).Length();

    private static Vector3 Sub(float[] p, int a, int b)
        => new(p[a * 3] - p[b * 3], p[a * 3 + 1] - p[b * 3 + 1], p[a * 3 + 2] - p[b * 3 + 2]);
}
