using System;
using System.Collections.Generic;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// A body's SKIN, bucketed by space for nearest-point queries — the surface a body retarget reads its displacement
/// field off, and the surface it tests cloth against when pushing it back out.
/// <para/>
/// Deliberately NOT <c>BrushTransfer.Surface</c> with a flag. Three of that class's four behaviours are the opposite
/// of what is wanted here, and all three are load-bearing to the brush:
/// <list type="bullet">
/// <item>it skips <see cref="SecondSkinWriter.IsBodySkinMaterial"/>, because for a brush "skin is never a source";
/// here skin is the ONLY source.</item>
/// <item>it keeps a grid per material and prefers the nearest point on the SAME material, so a vest does not borrow
/// from the top beneath it; a body is one surface and the distinction is meaningless.</item>
/// <item>it stops at a hard <c>MaxReach</c>, which is right when "no counterpart" means "no edit"; a retarget needs
/// the distance out to a quarter of a metre so it can fade rather than step.</item>
/// </list>
/// What this class carries and the brush's does not is the interpolated NORMAL at the landing, which is the direction
/// the push-out moves along. The one thing worth sharing is the closest-point test itself, taken from
/// <see cref="BrushTransfer.ClosestOnTriangle"/>.
/// </summary>
internal sealed class BodySurface
{
    /// <summary>Where a query landed on the body.</summary>
    /// <param name="A">Corners of the triangle hit, as indices into the body's <see cref="ModelParts.Positions"/>.</param>
    /// <param name="U">Barycentric weight of <paramref name="A"/>; <paramref name="V"/> and <paramref name="W"/> the others.</param>
    /// <param name="Point">The landing itself.</param>
    /// <param name="Normal">The body's normal there, interpolated across the triangle and normalised.</param>
    /// <param name="Distance">How far the query point was from <paramref name="Point"/>.</param>
    internal readonly record struct Hit(int A, int B, int C, float U, float V, float W,
                                        Vector3 Point, Vector3 Normal, float Distance);

    private readonly Vector3[] pos;
    private readonly Vector3[] nrm;
    private readonly List<int> tris = [];
    private readonly Dictionary<(int, int, int), List<int>> cells = [];
    private readonly float cell;
    private int[] stamp = [];
    private int query;

    /// <summary>Vertices of the body's skin parts, ascending and distinct — the snap's haystack.</summary>
    public IReadOnlyList<int> SkinVertices { get; }

    /// <summary>True when the model had no body-skin geometry at all, so every query misses.</summary>
    public bool IsEmpty => tris.Count == 0;

    /// <summary>Where a vertex of the body sits — the snap builds its haystack out of these.</summary>
    public Vector3 PositionOf(int vertex) => pos[vertex];

    /// <param name="body">A body model. Only its skin parts are indexed: a body .mdl also carries undies (which have
    /// GEAR uv), nails, piercings and pubes, and none of those are the body.</param>
    /// <param name="cellSize">Grid cell, metres. <see cref="CellFor"/> picks it from the mesh's own resolution.</param>
    public BodySurface(ModelParts body, float cellSize)
    {
        int vc = body.Positions.Length / 3;
        pos = new Vector3[vc];
        nrm = new Vector3[vc];
        for (int i = 0; i < vc; i++)
        {
            pos[i] = new Vector3(body.Positions[i * 3], body.Positions[i * 3 + 1], body.Positions[i * 3 + 2]);
            nrm[i] = new Vector3(body.Normals[i * 3], body.Normals[i * 3 + 1], body.Normals[i * 3 + 2]);
        }

        cell = cellSize;
        var skin = new HashSet<int>();
        foreach (var part in body.Parts)
        {
            // An island is a subset of its own submesh; taking both would index every triangle twice.
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                Add(a, b, c);
                skin.Add(a);
                skin.Add(b);
                skin.Add(c);
            }
        }

        var list = new List<int>(skin);
        list.Sort();
        SkinVertices = list;
    }

    /// <summary>
    /// The grid cell a body of this resolution wants: the same rule <c>BrushTransfer.Surface</c> uses, which is tuned
    /// for character-scale meshes.
    /// </summary>
    public static float CellFor(float meanEdge) => Math.Clamp(meanEdge * 2f, 0.005f, 0.03f);

    private void Add(int a, int b, int c)
    {
        int index = tris.Count / 3;
        tris.Add(a);
        tris.Add(b);
        tris.Add(c);
        var lo = Vector3.Min(pos[a], Vector3.Min(pos[b], pos[c]));
        var hi = Vector3.Max(pos[a], Vector3.Max(pos[b], pos[c]));
        var (x0, y0, z0) = Cell(lo);
        var (x1, y1, z1) = Cell(hi);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            if (!cells.TryGetValue((x, y, z), out var bucket)) cells[(x, y, z)] = bucket = [];
            bucket.Add(index);
        }
    }

    private (int, int, int) Cell(Vector3 p)
        => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

    /// <summary>The nearest point on the body's skin within <paramref name="maxDistance"/>, or false.</summary>
    public bool Nearest(Vector3 p, float maxDistance, out Hit hit)
    {
        hit = default;
        if (tris.Count == 0) return false;

        int count = tris.Count / 3;
        if (stamp.Length < count) stamp = new int[Math.Max(count, stamp.Length * 2)];
        if (++query == int.MaxValue)
        {
            Array.Clear(stamp);
            query = 1;
        }

        var (cx, cy, cz) = Cell(p);
        int maxRing = (int)MathF.Ceiling(maxDistance / cell);
        float best = maxDistance * maxDistance;
        bool found = false;
        int ba = 0, bb = 0, bc = 0;
        float bu = 0f, bv = 0f, bw = 0f;
        var landing = default(Vector3);

        for (int r = 0; r <= maxRing; r++)
        {
            for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            for (int z = -r; z <= r; z++)
            {
                if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != r) continue;   // this ring's shell only
                if (!cells.TryGetValue((cx + x, cy + y, cz + z), out var bucket)) continue;
                foreach (int t in bucket)
                {
                    if (stamp[t] == query) continue;
                    stamp[t] = query;
                    int ta = tris[t * 3], tb = tris[t * 3 + 1], tc = tris[t * 3 + 2];
                    var q = BrushTransfer.ClosestOnTriangle(p, pos[ta], pos[tb], pos[tc],
                                                            out float tu, out float tv, out float tw);
                    float d2 = Vector3.DistanceSquared(p, q);
                    if (d2 >= best) continue;
                    best = d2;
                    found = true;
                    ba = ta; bb = tb; bc = tc;
                    bu = tu; bv = tv; bw = tw;
                    landing = q;
                }
            }
            // Every cell of the next ring is at least r cells away, so nothing there can beat a point this near.
            if (found && best <= (r * cell) * (r * cell)) break;
        }

        if (!found) return false;

        var n = nrm[ba] * bu + nrm[bb] * bv + nrm[bc] * bw;
        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : FaceNormal(ba, bb, bc);

        hit = new Hit(ba, bb, bc, bu, bv, bw, landing, n, MathF.Sqrt(best));
        return true;
    }

    /// <summary>The triangle's own normal, for the rare landing where the interpolated vertex normals cancel.</summary>
    private Vector3 FaceNormal(int a, int b, int c)
    {
        var f = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
        return f.LengthSquared() > 1e-20f ? Vector3.Normalize(f) : Vector3.UnitY;
    }
}
