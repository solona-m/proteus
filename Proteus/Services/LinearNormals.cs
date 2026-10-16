using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// What a linear transform does to normals, and the face sums the edit's normal pass measures turns from. Pure
/// geometry for <see cref="MeshVolumeSolve"/>'s exact normals: a part stretched along one axis keeps the shading of
/// its flat faces only if its normals take the transform's inverse-transpose, which no per-node turn can give —
/// a box corner's three split normals share one node, and one rotation tilts all three.
/// <para/>
/// Matrices follow System.Numerics' row-vector convention: a normal is mapped as
/// <c>Vector3.TransformNormal(n, map)</c>, and "first A, then B" is <c>A * B</c>.
/// </summary>
internal static class LinearNormals
{
    /// <summary>
    /// The map a transform's normals take: the inverse-transpose of its 3×3 linear part, translation ignored.
    /// Null when that part is singular (a squash to nothing has no normals to give).
    /// </summary>
    internal static Matrix4x4? NormalMap(Matrix4x4 m)
    {
        var linear = Linear(m);
        float det = linear.GetDeterminant();
        if (!float.IsFinite(det) || MathF.Abs(det) < 1e-12f) return null;
        if (!Matrix4x4.Invert(linear, out var inverse)) return null;
        return Matrix4x4.Transpose(inverse);
    }

    /// <summary>
    /// The transform's 3×3 linear part is a positive multiple of the identity — an even scale or no change at all —
    /// so it turns no normal. Translation is ignored.
    /// </summary>
    internal static bool IsUniform(Matrix4x4 m, float eps = 1e-6f)
    {
        float s = m.M11;
        if (s <= eps) return false;
        return MathF.Abs(m.M22 - s) <= eps * s && MathF.Abs(m.M33 - s) <= eps * s
            && MathF.Abs(m.M12) <= eps * s && MathF.Abs(m.M13) <= eps * s
            && MathF.Abs(m.M21) <= eps * s && MathF.Abs(m.M23) <= eps * s
            && MathF.Abs(m.M31) <= eps * s && MathF.Abs(m.M32) <= eps * s;
    }

    /// <summary>The 3×3 linear part of <paramref name="m"/> as a 4×4 with no translation.</summary>
    internal static Matrix4x4 Linear(Matrix4x4 m)
        => new(m.M11, m.M12, m.M13, 0f,
               m.M21, m.M22, m.M23, 0f,
               m.M31, m.M32, m.M33, 0f,
               0f, 0f, 0f, 1f);

    /// <summary>
    /// <paramref name="v"/> turned by the rotation that takes the direction of <paramref name="from"/> to that of
    /// <paramref name="to"/> — the same Rodrigues turn, in the same arithmetic, as
    /// <see cref="SecondSkinWriter.TurnedNormals"/>. Unchanged when either has no direction, or when the two are
    /// parallel or turned clean over; so equal faces give back <paramref name="v"/> bit for bit.
    /// </summary>
    internal static Vec3 Turn(Vec3 from, Vec3 to, Vec3 v)
    {
        if (MeshMath.Normalize(from) is not { } a || MeshMath.Normalize(to) is not { } b) return v;
        float cos = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        var axis = new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        float sin = MathF.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
        if (sin <= 1e-7f) return v;

        var k = new Vec3(axis.X / sin, axis.Y / sin, axis.Z / sin);
        float kv = k.X * v.X + k.Y * v.Y + k.Z * v.Z;
        var kxv = new Vec3(k.Y * v.Z - k.Z * v.Y, k.Z * v.X - k.X * v.Z, k.X * v.Y - k.Y * v.X);
        return new Vec3(
            v.X * cos + kxv.X * sin + k.X * kv * (1f - cos),
            v.Y * cos + kxv.Y * sin + k.Y * kv * (1f - cos),
            v.Z * cos + kxv.Z * sin + k.Z * kv * (1f - cos));
    }

    /// <summary><see cref="Turn"/> as a matrix: each row is where the turn takes that basis vector.</summary>
    internal static Matrix4x4 TurnMatrix(Vec3 from, Vec3 to)
    {
        var x = Turn(from, to, new Vec3(1, 0, 0));
        var y = Turn(from, to, new Vec3(0, 1, 0));
        var z = Turn(from, to, new Vec3(0, 0, 1));
        return new Matrix4x4(x.X, x.Y, x.Z, 0f,
                             y.X, y.Y, y.Z, 0f,
                             z.X, z.Y, z.Z, 0f,
                             0f, 0f, 0f, 1f);
    }

    /// <summary><paramref name="v"/> through a normal map.</summary>
    internal static Vec3 Apply(Matrix4x4 map, Vec3 v)
    {
        var r = Vector3.TransformNormal(new Vector3(v.X, v.Y, v.Z), map);
        return new Vec3(r.X, r.Y, r.Z);
    }

    /// <summary>
    /// The triangles <see cref="SecondSkinWriter.TurnedNormals"/> sums, in its order: as vertex triples, degenerate
    /// node triples and repeats of a node triple dropped — the first copy kept.
    /// </summary>
    internal static int[] UniqueFaces(int[] tris, int[] nodeOf)
    {
        int vc = nodeOf.Length;
        var kept = new List<int>(tris.Length);
        var seen = new HashSet<(int, int, int)>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            if (ia < 0 || ib < 0 || ic < 0 || ia >= vc || ib >= vc || ic >= vc) continue;
            int na = nodeOf[ia], nb = nodeOf[ib], nc = nodeOf[ic];
            if (na == nb || nb == nc || na == nc) continue;
            int s0 = Math.Min(na, Math.Min(nb, nc)), s2 = Math.Max(na, Math.Max(nb, nc));
            if (!seen.Add((s0, na + nb + nc - s0 - s2, s2))) continue;
            kept.Add(ia); kept.Add(ib); kept.Add(ic);
        }
        return kept.ToArray();
    }

    /// <summary>Each node's faces in <paramref name="faces"/> (from <see cref="UniqueFaces"/>), as offsets into it, in order.</summary>
    internal static int[][] FacesOfNode(int[] faces, int[] nodeOf, int nodeCount)
    {
        var lists = new List<int>[nodeCount];
        for (int f = 0; f + 2 < faces.Length; f += 3)
            for (int k = 0; k < 3; k++)
                (lists[nodeOf[faces[f + k]]] ??= []).Add(f);
        var result = new int[nodeCount][];
        for (int n = 0; n < nodeCount; n++) result[n] = lists[n]?.ToArray() ?? [];
        return result;
    }

    /// <summary>
    /// The summed, area-weighted face normal around one node: its faces' cross products at
    /// <c>pos + delta</c> (no delta: the author's positions), added in face order — the same sum
    /// <see cref="SecondSkinWriter.TurnedNormals"/> turns from.
    /// </summary>
    internal static Vec3 FaceSum(int[] faces, int[] around, Vec3[] pos, Vec3[]? delta)
    {
        float sx = 0f, sy = 0f, sz = 0f;
        foreach (int f in around)
        {
            int ia = faces[f], ib = faces[f + 1], ic = faces[f + 2];
            float ax = pos[ia].X, ay = pos[ia].Y, az = pos[ia].Z;
            float bx = pos[ib].X, by = pos[ib].Y, bz = pos[ib].Z;
            float cx = pos[ic].X, cy = pos[ic].Y, cz = pos[ic].Z;
            if (delta is not null)
            {
                ax += delta[ia].X; ay += delta[ia].Y; az += delta[ia].Z;
                bx += delta[ib].X; by += delta[ib].Y; bz += delta[ib].Z;
                cx += delta[ic].X; cy += delta[ic].Y; cz += delta[ic].Z;
            }
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float wx = cx - ax, wy = cy - ay, wz = cz - az;
            sx += uy * wz - uz * wy;
            sy += uz * wx - ux * wz;
            sz += ux * wy - uy * wx;
        }
        return new Vec3(sx, sy, sz);
    }
}
