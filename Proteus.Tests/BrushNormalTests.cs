using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// What the brush is allowed to do to a garment's normals, which is: follow the surface, and otherwise nothing.
/// <para/>
/// The brush edits somebody's finished work. A garment's shipped normals are not the geometric ones — authors smooth
/// them, or copy them off the body so the fabric shades as if it were skin — so recomputing normals from the triangles
/// throws that away. The brush used to do exactly that, through <c>RelaxedNormals</c>, which is right for a SHELL
/// (built as position plus normal times an offset, so it needs the new surface's normal) and wrong for an edit. The
/// damage reached far past where a stroke bites, because the writer emits a normal for any vertex that moved at all
/// and a stroke's falloff moves a broad ring by fractions of a millimetre: imperceptible movement, total reshading,
/// blotches across flat fabric nowhere near an edge.
/// <para/>
/// <see cref="SecondSkinWriter.TurnedNormals"/> takes the rotation the surface underwent and applies it to each
/// vertex's own authored normal instead. The two halves of that are both tested here, because either alone is
/// worthless: leave an unmoved surface alone, and follow one that moves.
/// </summary>
public class BrushNormalTests(ITestOutputHelper output)
{
    /// <summary>
    /// A garment that has not moved must come out byte-for-byte unchanged, however much of it the brush touched.
    /// <para/>
    /// The measurement that found the fault. Recomputing gave, on the same two garments with not one vertex moved:
    /// 24.8 degrees of mean change and 60% of vertices more than 10 degrees off on one, 9.1 and 27.6% on the other.
    /// </summary>
    [Theory]
    [InlineData(@"E:\Penumbradt\BiboPlus Sheer Elegance\Chest size\Small\chara\equipment\e6010\model\c0201e6010_top.mdl")]
    [InlineData(@"E:\Penumbradt\This Old Thing - by Solona\size\neolithe m\chara\equipment\e6255\model\c0201e6255_top.mdl")]
    public void A_garment_that_has_not_moved_keeps_every_normal(string file)
    {
        if (!File.Exists(file)) return;                       // the house diag rule: skip when the mod is not here
        var m = ModelPartReader.Read(File.ReadAllBytes(file));
        if (m == null) return;

        int vc = m.Positions.Length / 3;
        var pos = new Vec3[vc];
        var nrm = new Vec3[vc];
        for (int i = 0; i < vc; i++)
        {
            pos[i] = new Vec3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
            nrm[i] = i * 3 + 2 < m.Normals.Length
                ? new Vec3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2])
                : new Vec3(0f, 1f, 0f);
        }

        var tris = new List<int>();
        foreach (var part in m.Parts)
            if (part.Island < 0) tris.AddRange(part.Triangles);   // whole submeshes only: an island doubles them

        var nodeOf = MeshMath.WeldByPosition(pos, out int nodeCount);
        var weight = new float[nodeCount];
        Array.Fill(weight, 1f);                                   // the brush touched all of it...
        var outN = SecondSkinWriter.TurnedNormals(pos, nrm, new Vec3[vc], nodeOf, weight, tris.ToArray());

        // Compared as vectors, not as an angle between them: a model's stored normals are quantised and not exactly
        // unit length, so acos(n·n) is a few hundredths of a degree off zero for a normal that was never touched.
        int changed = 0;
        double worst = 0;                                         // ...and moved none of it
        for (int i = 0; i < vc; i++)
        {
            if (outN[i].X != nrm[i].X || outN[i].Y != nrm[i].Y || outN[i].Z != nrm[i].Z) changed++;
            worst = Math.Max(worst, Angle(nrm[i], outN[i]));
        }
        output.WriteLine($"{Path.GetFileName(file)}: {vc:N0} verts, {changed:N0} changed, " +
                         $"worst angle {worst:F3} degrees (quantisation, not a change)");
        Assert.Equal(0, changed);
    }

    /// <summary>
    /// And a surface that really does turn takes its normals with it — otherwise "leave them alone" would be
    /// satisfied by doing nothing at all.
    /// </summary>
    [Fact]
    public void A_surface_that_turns_takes_its_normals_with_it()
    {
        const int side = 12;
        var pos = new List<Vec3>();
        var nrm = new List<Vec3>();
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                pos.Add(new Vec3(x * 0.01f, y * 0.01f, 0f));
                nrm.Add(new Vec3(0f, 0f, 1f));
            }
        var tris = new List<int>();
        for (int y = 0; y + 1 < side; y++)
            for (int x = 0; x + 1 < side; x++)
            {
                int a = y * side + x, b = a + 1, c = a + side, d = c + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }

        // A rigid 30 degree turn about the x axis, expressed as a displacement.
        const float deg = 30f;
        float rad = deg * MathF.PI / 180f, cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        var delta = new Vec3[pos.Count];
        for (int i = 0; i < pos.Count; i++)
        {
            var p = pos[i];
            delta[i] = new Vec3(0f, p.Y * cos - p.Z * sin - p.Y, p.Y * sin + p.Z * cos - p.Z);
        }

        var arr = pos.ToArray();
        var nodeOf = MeshMath.WeldByPosition(arr, out int nodeCount);
        var weight = new float[nodeCount];
        Array.Fill(weight, 1f);

        var outN = SecondSkinWriter.TurnedNormals(arr, nrm.ToArray(), delta, nodeOf, weight, tris.ToArray());

        var turned = pos.Select((_, i) => Angle(nrm[i], outN[i])).ToList();
        output.WriteLine($"asked for {deg:F1} degrees; got mean {turned.Average():F2}, " +
                         $"min {turned.Min():F2}, max {turned.Max():F2}");
        Assert.All(turned, t => Assert.InRange(t, deg - 0.5, deg + 0.5));
    }

    /// <summary>
    /// A hard edge keeps its angle. Two quads meeting at a right angle, stored the way a model stores a crease: the
    /// shared edge duplicated, one copy per face, each carrying its own face's normal — so the copies weld into one
    /// node while describing two surfaces.
    /// <para/>
    /// Recomputing averaged across the node and then flipped each copy to face its own way, which left the two copies
    /// 180 degrees apart where the author had 90 — one of them pointing into the surface. Rotating each copy's own
    /// normal cannot do that. (On the two garments to hand this case is rare — 0 of 542 shared nodes on one, 13 of
    /// 1,703 on the other — so it was never the reported fault, but it is free to keep right.)
    /// </summary>
    [Fact]
    public void A_hard_edge_keeps_the_angle_the_author_gave_it()
    {
        var pos = new[]
        {
            new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f),        // flat quad, far edge
            new Vec3(0f, 1f, 0f), new Vec3(1f, 1f, 0f),        // shared edge, flat copy
            new Vec3(0f, 1f, 0f), new Vec3(1f, 1f, 0f),        // shared edge, upright copy (same position)
            new Vec3(0f, 1f, 1f), new Vec3(1f, 1f, 1f),        // upright quad, far edge
        };
        var nrm = new[]
        {
            new Vec3(0f, 0f, 1f), new Vec3(0f, 0f, 1f),
            new Vec3(0f, 0f, 1f), new Vec3(0f, 0f, 1f),        // flat copy faces +Z
            new Vec3(0f, 1f, 0f), new Vec3(0f, 1f, 0f),        // upright copy faces +Y
            new Vec3(0f, 1f, 0f), new Vec3(0f, 1f, 0f),
        };
        var tris = new[] { 0, 1, 3, 0, 3, 2, 4, 5, 7, 4, 7, 6 };

        var nodeOf = MeshMath.WeldByPosition(pos, out int nodeCount);
        var weight = new float[nodeCount];
        Array.Fill(weight, 1f);
        var outN = SecondSkinWriter.TurnedNormals(pos, nrm, new Vec3[pos.Length], nodeOf, weight, tris);

        double before = Angle(nrm[2], nrm[4]), after = Angle(outN[2], outN[4]);
        output.WriteLine($"the crease copies: {before:F1} degrees as authored, {after:F1} after the pass");
        Assert.InRange(after, before - 0.5, before + 0.5);
    }

    /// <summary>
    /// Copies of one point that agree — a uv seam, split for texture coordinates but describing one surface — must
    /// still come out identical, or the seam cracks into a visible line. The reason the old pass averaged per node.
    /// </summary>
    [Fact]
    public void A_uv_seam_s_copies_stay_identical()
    {
        const int side = 8;
        var pos = new List<Vec3>();
        var nrm = new List<Vec3>();
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                pos.Add(new Vec3(x * 0.01f, y * 0.01f, MathF.Sin(x * 0.4f) * 0.01f));
                nrm.Add(Unit(new Vec3(-MathF.Cos(x * 0.4f) * 0.4f, 0f, 1f)));
            }
        int n = pos.Count;
        var tris = new List<int>();
        for (int y = 0; y + 1 < side; y++)
            for (int x = 0; x + 1 < side; x++)
            {
                int a = y * side + x, b = a + 1, c = a + side, d = c + 1;
                tris.AddRange([a, b, d, a, d, c]);
            }

        // Every vertex duplicated at the same position with the same normal: the seam case.
        var dPos = pos.Concat(pos).ToArray();
        var dNrm = nrm.Concat(nrm).ToArray();
        var delta = new Vec3[dPos.Length];
        for (int i = 0; i < dPos.Length; i++) delta[i] = new Vec3(0f, 0f, dPos[i].X * 0.2f);

        var nodeOf = MeshMath.WeldByPosition(dPos, out int nodeCount);
        var weight = new float[nodeCount];
        Array.Fill(weight, 1f);
        var outN = SecondSkinWriter.TurnedNormals(dPos, dNrm, delta, nodeOf, weight, tris.ToArray());

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(outN[i].X, outN[i + n].X, 6);
            Assert.Equal(outN[i].Y, outN[i + n].Y, 6);
            Assert.Equal(outN[i].Z, outN[i + n].Z, 6);
        }
    }

    private static Vec3 Unit(Vec3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len <= 1e-12f ? v : new Vec3(v.X / len, v.Y / len, v.Z / len);
    }

    private static double Angle(Vec3 a, Vec3 b)
        => Math.Acos(Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1f, 1f)) * 180 / Math.PI;
}
