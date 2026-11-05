using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// Gives a bent hairstyle back the scalp distance its author gave it, where the bend sank it into the target's skull.
/// <para/>
/// The bend (<see cref="RacialModelBake.Port"/>) moves hair by the head bone's racial matrices, which scale a head but
/// do not know its shape: on a bigger or longer skull — most men's races, Au Ra, Hrothgar — an author's tight scalp
/// layers end up inside it at the upper back, and the skin shows through as a bald patch (LM Stryx Rainbow, c0201 h0168:
/// 500–2,400 points per race, 12–42 mm deep, 2026-11-05). The fit check (<see cref="HairPortBaker.Measure"/>) does not
/// see it: it is a few percent of the cranium. The game's own copies have sunk inner layers too, so only what the BEND
/// created is undone: a point sunk into the target's skull that sat outside the source's on its own head.
/// <para/>
/// Done as a smooth field over directions from the head's centre, seeded by those points, so every layer of a region
/// moves together (their order kept), copies of one point at a seam move alike, and strands bend without tearing. A
/// point is never pushed further from the scalp than it sat on the source, so hair the author tucked inside stays
/// tucked. Shape keys' spares neither seed nor count: they follow their base (an author's or Proteus's hat fit presses
/// them to the scalp on purpose).
/// <para/>
/// Calibrated 2026-11-05 on the game's shared hairstyles (c0201 and c0101 to every playable race) and LM Stryx Rainbow
/// (see HairPortScalpLocalTests, HairPortStryxLocalTests): created clips on game ports 726 → 0 (none held at
/// <see cref="MaxPush"/>; the deepest needs 55 mm, c0201→c1501); Stryx upper-back created clips → 0 for every race, and by
/// the face's own skin triangles c0101 610 → 0, c0301 848 → 0, c0501 876 → 0, c0701 416 → 2 (Miqo'te ear bases and
/// Elezen ears are protrusions, left alone); worst overshoot past the source's gap 0.00 mm; folds added at most 2.
/// Strain beyond the skull's wrap (<see cref="ExcessStrain"/>), share of pushed edges over 25%: non-Hrothgar game ports
/// at most 0.46% (c0101→c0701 h0140), Hrothgar up to 10.0% (c0101→c1501 h0101), Stryx c1501 4.22% at a 51.9 mm push —
/// coarse cards wrapped onto a much bigger skull; how it looks is checked in game.
/// </summary>
internal static class HairScalpPush
{
    /// <summary>How deep inside the target's skull a point must be to count as sunk, in metres. Constants calibrated
    /// against the game's shared hairstyles and LM Stryx Rainbow (see HairPortLocalTests, HairPortStryxLocalTests).</summary>
    public const float ClipDepth = 0.003f;

    /// <summary>How far inside the source's own skull a point may already sit and still count as on its scalp, in metres:
    /// strands laid on a scalp dip under its furthest-vertex floor by a millimetre.</summary>
    public const float SourceSlack = 0.001f;

    /// <summary>How far above its neighbourhood's median a face model's furthest point may stand before it is an ear or a
    /// horn rather than skull, in metres.</summary>
    public const float ProtrusionMargin = 0.008f;

    /// <summary>How far below the head's centre the push fades out, in metres: hair hanging past the jaw is not on the
    /// scalp.</summary>
    public const float FadeBelow = 0.03f;

    /// <summary>The furthest one point is ever pushed, in metres. The game's own shared hairstyles need up to 55 mm (a
    /// woman's onto a Hrothgar man); deeper than this is no skull shape a bend misses but a head the hair was never
    /// near, and what is left is said (<see cref="ClipLimit"/>) rather than dragged across the room.</summary>
    public const float MaxPush = 0.06f;

    /// <summary>The most points a port may still have sunk into the head where the source does not before it is saved
    /// with a warning: half a percent of its cranium points, never under 20 nor over 50.</summary>
    public static int ClipLimit(int cranium) => Math.Clamp((int)(0.005f * cranium), 20, 50);

    /// <summary>A head: its centre and its skull per direction, ears and horns clipped off.</summary>
    public sealed record Frame(Vector3 Centre, float[] Skull);

    /// <summary>The frame of a face model, or null when it cannot be read.</summary>
    public static Frame? FrameOf(byte[] head)
    {
        if (HatCompatSolve.HeadFrameFrom(head) is not { } f || ModelPartReader.Read(head) is not { } parts) return null;
        // A face with no skin mesh has no scalp to measure against: no push, never a wrong one.
        if (!parts.Parts.Any(p => p.Island == -1 && IsSkin(p.Material))) return null;
        return new Frame(f.Centre, Skull(Surface(parts, f.Centre, f.Radius)));
    }

    /// <summary>
    /// The face model's surface per direction from <paramref name="centre"/>: where a ray through each bin's centre
    /// last leaves its triangles. Not its furthest VERTEX per bin, as the hat press reads it: a face's cranium is a few
    /// large triangles (a Miqo'te's has 243 points above the centre), so most bins at the back of the head hold no point
    /// at all and would take a median. A bin no ray finds a triangle in takes the mean of the bins around it that did.
    /// </summary>
    internal static float[] Surface(ModelParts face, Vector3 centre, float radius)
    {
        const int U = HatCompatSolve.FloorBinsU, V = HatCompatSolve.FloorBinsV;
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        // The skin only (<c>_fac_a</c>): an Au Ra's horns and scales, the lashes and the eyes are meshes of their own.
        foreach (var part in face.Parts.Where(p => p.Island == -1 && IsSkin(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                tris.Add((At(face.Positions, part.Triangles[t]) - centre, At(face.Positions, part.Triangles[t + 1]) - centre,
                          At(face.Positions, part.Triangles[t + 2]) - centre));

        var floor = new float[U * V];
        for (int iv = 0; iv < V; iv++)
            for (int iu = 0; iu < U; iu++)
            {
                float u = (iu + 0.5f) / U * 2 * MathF.PI - MathF.PI, v = (iv + 0.5f) / V * MathF.PI;
                var dir = new Vector3(MathF.Sin(v) * MathF.Cos(u), MathF.Cos(v), MathF.Sin(v) * MathF.Sin(u));
                float far = 0f;
                foreach (var (a, b, c) in tris)
                    if (Hit(dir, a, b, c) is { } t && t > far) far = t;
                floor[iv * U + iu] = far;
            }

        // Holes: the mean of what was found around them, widening until something is.
        for (int pass = 0; pass < V && floor.Any(f => f <= 0f); pass++)
        {
            var next = (float[])floor.Clone();
            for (int iv = 0; iv < V; iv++)
                for (int iu = 0; iu < U; iu++)
                {
                    if (floor[iv * U + iu] > 0f) continue;
                    float sum = 0f;
                    int n = 0;
                    for (int dv = -1; dv <= 1; dv++)
                    {
                        int jv = iv + dv;
                        if (jv < 0 || jv >= V) continue;
                        for (int du = -1; du <= 1; du++)
                            if (floor[jv * U + ((iu + du + U) % U)] is > 0f and var f) { sum += f; n++; }
                    }
                    if (n > 0) next[iv * U + iu] = sum / n;
                }
            floor = next;
        }
        for (int i = 0; i < floor.Length; i++) if (floor[i] <= 0f) floor[i] = radius;
        return floor;
    }

    /// <summary>A face model's skin material: <c>/mt_c1401f0001_fac_a.mtrl</c>.</summary>
    internal static bool IsSkin(string material) => material.EndsWith("_fac_a.mtrl", StringComparison.OrdinalIgnoreCase);

    /// <summary>How far along <paramref name="dir"/> from the origin the ray crosses triangle abc, or null (Möller–Trumbore).</summary>
    private static float? Hit(Vector3 dir, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return null;
        float inv = 1f / det;
        var s = -a;
        float bu = Vector3.Dot(s, p) * inv;
        if (bu < 0f || bu > 1f) return null;
        var q = Vector3.Cross(s, e1);
        float bv = Vector3.Dot(dir, q) * inv;
        if (bv < 0f || bu + bv > 1f) return null;
        float t = Vector3.Dot(e2, q) * inv;
        return t > 1e-5f ? t : null;
    }

    /// <summary>
    /// A head floor with what stands out of the skull clipped to it: each bin no further than the median of the 5×5
    /// bins around it plus <see cref="ProtrusionMargin"/>. An Elezen's or Lalafell's ear, an Au Ra's horn, is otherwise
    /// the "scalp" in its bins, and hair beside it would read as sunk and be pushed out into a spike.
    /// </summary>
    internal static float[] Skull(float[] floor)
    {
        const int U = HatCompatSolve.FloorBinsU, V = HatCompatSolve.FloorBinsV;
        var skull = new float[floor.Length];
        var around = new List<float>(25);
        for (int iv = 0; iv < V; iv++)
            for (int iu = 0; iu < U; iu++)
            {
                around.Clear();
                for (int dv = -2; dv <= 2; dv++)
                {
                    int jv = iv + dv;
                    if (jv < 0 || jv >= V) continue;
                    for (int du = -2; du <= 2; du++) around.Add(floor[jv * U + ((iu + du + U) % U)]);
                }
                around.Sort();
                skull[iv * U + iu] = MathF.Min(floor[iv * U + iu], around[around.Count / 2] + ProtrusionMargin);
            }
        return skull;
    }

    /// <param name="Model">The model pushed, or the same bytes when nothing was.</param>
    /// <param name="Pushed">Points moved.</param>
    /// <param name="Worst">The furthest any moved, in metres.</param>
    /// <param name="Before">Points the bend sank (see <see cref="Created"/>) before the push.</param>
    /// <param name="After">Those left after it.</param>
    /// <param name="Cranium">Points at or above the head's centre, spares aside: what <see cref="ClipLimit"/> is taken of.</param>
    /// <param name="Capped">Points held at <see cref="MaxPush"/>, short of their source gap.</param>
    public sealed record Result(byte[] Model, int Pushed, float Worst, int Before, int After, int Cranium, int Capped = 0);

    /// <summary>
    /// Push <paramref name="bent"/> — <paramref name="shown"/> bent onto the target, the same points in the same order —
    /// back out where the bend sank it into <paramref name="target"/>'s skull.
    /// </summary>
    /// <param name="shown">The source as it is drawn on its own head, before the bend.</param>
    public static Result Apply(byte[] bent, ModelParts shown, Frame source, Frame target)
    {
        if (ModelPartReader.Read(bent) is not { } parts || parts.Positions.Length != shown.Positions.Length)
            return new Result(bent, 0, 0f, 0, 0, 0);
        var spares = Spares(bent, parts);
        int count = parts.Positions.Length / 3;
        var gap = Gaps(shown, parts, source, target, spares);

        // The field: per direction, how far the bend sank what it sank there, spread two bins and smoothed.
        const int U = HatCompatSolve.FloorBinsU, V = HatCompatSolve.FloorBinsV;
        var seed = new float[U * V];
        int before = 0, cranium = 0;
        for (int v = 0; v < count; v++)
        {
            if (spares.Contains(v) || !gap[v].Cranium) continue;
            cranium++;
            if (!IsCreated(gap[v])) continue;
            before++;
            int b = HatCompatSolve.BinFor(Upper(Dir(parts.Positions, v, target.Centre)));
            seed[b] = MathF.Max(seed[b], gap[v].Source - gap[v].Target);
        }
        if (before == 0) return new Result(bent, 0, 0f, 0, 0, cranium);
        var field = Smooth(Dilate(seed));

        var edit = new Edit(parts.MeshSpans, count);
        int pushed = 0, capped = 0;
        for (int v = 0; v < count; v++)
        {
            if (spares.Contains(v)) continue;
            var q = At(parts.Positions, v);
            var dir = Dir(parts.Positions, v, target.Centre);
            float fade = Math.Clamp(1f - (target.Centre.Y - q.Y) / FadeBelow, 0f, 1f);
            if (fade <= 0f) continue;
            float reach = HatCompatSolve.FloorAt(field, Upper(dir));
            float want = MathF.Min(reach, MathF.Max(gap[v].Source - gap[v].Target, 0f));
            if (want > MaxPush) capped++;
            float push = MathF.Min(want, MaxPush) * fade;
            if (push <= 1e-5f) continue;
            var d = dir * push;
            edit.Delta[v] = new Vec3(d.X, d.Y, d.Z);
            edit.Worst = MathF.Max(edit.Worst, push);
            pushed++;
        }
        if (pushed == 0) return new Result(bent, 0, 0f, before, before, cranium);

        var model = MeshVolumeService.Inflate(bent, edit, carrySpares: true).Model;
        int after = ModelPartReader.Read(model) is { } done ? Created(shown, done, source, target, spares).Count : before;
        return new Result(model, pushed, edit.Worst, before, after, cranium, capped);
    }

    /// <summary>
    /// How many of <paramref name="port"/>'s points the bend sank into <paramref name="target"/>'s skull where
    /// <paramref name="shown"/> sat on <paramref name="source"/>'s scalp, spares aside; and of how many cranium points.
    /// </summary>
    /// <param name="where">Count only points whose place relative to the target head's centre passes this (the back
    /// of the head is −Z); null counts all.</param>
    public static (int Count, int Cranium) Created(ModelParts shown, ModelParts port, Frame source, Frame target,
                                                   IReadOnlySet<int>? spares = null, Func<Vector3, bool>? where = null)
    {
        if (port.Positions.Length != shown.Positions.Length) return (0, 0);
        spares ??= new HashSet<int>();
        var gap = Gaps(shown, port, source, target, spares);
        int created = 0, cranium = 0;
        for (int v = 0; v < gap.Length; v++)
        {
            if (spares.Contains(v) || !gap[v].Cranium) continue;
            if (where != null && !where(At(port.Positions, v) - target.Centre)) continue;
            cranium++;
            if (IsCreated(gap[v])) created++;
        }
        return (created, cranium);
    }

    /// <summary>
    /// How much more <paramref name="port"/> distorts each strand than wrapping the hairstyle onto the target's skull asks
    /// for, edge by edge: an edge's length on the port against its length on the source, scaled by how much further from
    /// the head's centre its two ends now sit (<c>g</c>, the radial growth the bigger skull requires — a ring pushed out by
    /// d at radius r grows by (r+d)/r and scores no strain). Only edges with an end <paramref name="pushed"/> and neither a
    /// spare. The strain of the bend alone is subtracted, so only what the push adds counts. Note the fit warning
    /// (<see cref="HairPortBaker.Measure"/>) still judges the scalp by the hat press's furthest-vertex floor, not this
    /// skin surface: the two can disagree on a port.
    /// </summary>
    /// <returns>Each eligible edge's excess: |strain on the port| − |strain of the bend alone|.</returns>
    internal static List<float> ExcessStrain(ModelParts shown, ModelParts bent, ModelParts port, Frame source, Frame target,
                                             IReadOnlySet<int> spares, Func<int, bool> pushed)
    {
        var excess = new List<float>();
        foreach (var part in shown.Parts.Where(x => x.Island == -1))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int i = part.Triangles[t + e], j = part.Triangles[t + (e + 1) % 3];
                    if (spares.Contains(i) || spares.Contains(j) || !(pushed(i) || pushed(j))) continue;
                    float src = (At(shown.Positions, i) - At(shown.Positions, j)).Length();
                    float rs = (At(shown.Positions, i) - source.Centre).Length() + (At(shown.Positions, j) - source.Centre).Length();
                    if (src < 1e-5f || rs < 1e-5f) continue;
                    excess.Add(MathF.Abs(Strain(port)) - MathF.Abs(Strain(bent)));

                    float Strain(ModelParts m)
                    {
                        var a = At(m.Positions, i);
                        var b = At(m.Positions, j);
                        float g = ((a - target.Centre).Length() + (b - target.Centre).Length()) / rs;
                        return (a - b).Length() / (src * g) - 1f;
                    }
                }
        return excess;
    }

    /// <summary>The spares of <paramref name="model"/>'s shape keys, by their place in <paramref name="parts"/>.</summary>
    internal static HashSet<int> Spares(byte[] model, ModelParts parts)
    {
        var set = new HashSet<int>();
        var keyed = MeshVolumeService.SpareVertices(model, SecondSkinWriter.Parse(model));
        if (keyed.Count == 0) return set;
        foreach (var span in parts.MeshSpans)
            for (int k = 0; k < span.Count; k++)
                if (keyed.Contains(((long)span.Mesh << 32) | (uint)k)) set.Add(span.BaseVertex + k);
        return set;
    }

    private readonly record struct Gap(float Source, float Target, bool Cranium);

    private static bool IsCreated(Gap g) => g.Target < -ClipDepth && g.Source > -SourceSlack;

    /// <summary>Each point's distance outside the skull (negative inside), on the source's head and on the target's —
    /// read in the upper half's directions, where the face model is skull, for a point below the centre too.</summary>
    private static Gap[] Gaps(ModelParts shown, ModelParts port, Frame source, Frame target, IReadOnlySet<int> spares)
    {
        int count = port.Positions.Length / 3;
        var gaps = new Gap[count];
        for (int v = 0; v < count; v++)
        {
            if (spares.Contains(v)) continue;
            var p = At(shown.Positions, v) - source.Centre;
            var q = At(port.Positions, v) - target.Centre;
            float lp = p.Length(), lq = q.Length();
            if (lp < 1e-5f || lq < 1e-5f) continue;
            gaps[v] = new Gap(lp - HatCompatSolve.FloorAt(source.Skull, Upper(p / lp)),
                              lq - HatCompatSolve.FloorAt(target.Skull, Upper(q / lq)),
                              q.Y >= 0f && p.Y >= 0f);
        }
        return gaps;
    }

    /// <summary>A direction raised to the head's equator if it points below it.</summary>
    private static Vector3 Upper(Vector3 dir)
    {
        if (dir.Y >= 0f) return dir;
        var flat = new Vector3(dir.X, 0f, dir.Z);
        return flat.LengthSquared() > 1e-12f ? Vector3.Normalize(flat) : Vector3.UnitX;
    }

    private static Vector3 At(float[] p, int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

    private static Vector3 Dir(float[] p, int v, Vector3 centre)
    {
        var d = At(p, v) - centre;
        return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitY;
    }

    /// <summary>Each bin the most of the bins within two of it: a seed reaches its neighbours' points too.</summary>
    private static float[] Dilate(float[] seed)
    {
        const int U = HatCompatSolve.FloorBinsU, V = HatCompatSolve.FloorBinsV;
        var o = new float[seed.Length];
        for (int iv = 0; iv < V; iv++)
            for (int iu = 0; iu < U; iu++)
            {
                float m = 0f;
                for (int dv = -2; dv <= 2; dv++)
                {
                    int jv = iv + dv;
                    if (jv < 0 || jv >= V) continue;
                    for (int du = -2; du <= 2; du++) m = MathF.Max(m, seed[jv * U + ((iu + du + U) % U)]);
                }
                o[iv * U + iu] = m;
            }
        return o;
    }

    /// <summary>Each bin the mean of the 3×3 around it, so the field has no edge a strand could kink on.</summary>
    private static float[] Smooth(float[] field)
    {
        const int U = HatCompatSolve.FloorBinsU, V = HatCompatSolve.FloorBinsV;
        var o = new float[field.Length];
        for (int iv = 0; iv < V; iv++)
            for (int iu = 0; iu < U; iu++)
            {
                float sum = 0f;
                int n = 0;
                for (int dv = -1; dv <= 1; dv++)
                {
                    int jv = iv + dv;
                    if (jv < 0 || jv >= V) continue;
                    for (int du = -1; du <= 1; du++) { sum += field[jv * U + ((iu + du + U) % U)]; n++; }
                }
                o[iv * U + iu] = sum / n;
            }
        return o;
    }

    /// <summary>The push as an edit <see cref="MeshVolumeService.Inflate"/> writes: positions only, normals kept.</summary>
    private sealed class Edit(IReadOnlyList<MeshSpan> spans, int count) : IMeshEdit
    {
        public readonly Vec3[] Delta = new Vec3[count];

        public IReadOnlyList<MeshSpan> Spans => spans;
        public float Worst { get; set; }
        public bool Dirty => true;
        public bool WindEdited => false;
        public Vec3 DeltaAt(int vertex) => Delta[vertex];
        public Vec3 NormalAt(int vertex) => default;
        public float WindAt(int vertex) => 0f;
    }
}
