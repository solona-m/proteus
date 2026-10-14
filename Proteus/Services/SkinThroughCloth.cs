using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// A garment's own skin standing OUTSIDE the cloth over it: what shows through in game.
/// <para/>
/// The other way round from asking whether cloth vertices are inside the skin, and the way that catches a sheet of cloth
/// tessellated unlike the skin under it. Laurel's tights, a copy of the Neolithe legs refitted onto Rue+, had no vertex
/// inside the new skin and weights matching it, yet Rue's skin bulged through between the tights' vertices down the
/// whole leg: 656 skin points outside the cloth, against the author's 123, all in the lace band.
/// </summary>
internal static class SkinThroughCloth
{
    /// <summary>A skin point this near a cloth face is under it (10 mm).</summary>
    internal const float Reach = 0.01f;

    /// <summary>Outside the cloth by more than this counts (0.2 mm).</summary>
    internal const float Tolerance = 0.0002f;

    /// <summary>Within this barycentric share of an edge, the nearest cloth point is on that edge.</summary>
    private const float OnEdge = 0.02f;

    /// <summary>A skin vertex standing outside the cloth that covers it.</summary>
    /// <param name="Over">The label of the cloth part it shows through.</param>
    /// <param name="By">How far outside, along the cloth face's way out (metres).</param>
    internal sealed record Point(int Vertex, Vector3 At, string Over, float By);

    /// <summary>
    /// Every skin vertex of <paramref name="garment"/> (its body-skin parts) within <paramref name="reach"/> of a cloth
    /// face, and those of them standing outside the nearest one by more than <paramref name="tolerance"/>. The way out is
    /// the face's own normal, turned to agree with its corners' normals.
    /// <para/>
    /// A skin point whose nearest cloth point lies on an edge only one cloth face uses is past a hem — the thigh above a
    /// stocking — and is not through anything. Nothing else is left out: a skin point nearest a cloth CORNER counts, so the
    /// measure has no blind spot where the cloth's own vertices are.
    /// </summary>
    internal static (int Covered, IReadOnlyList<Point> Outside) Find(ModelParts garment, float reach = Reach,
                                                                     float tolerance = Tolerance)
    {
        Vector3 P(int v) => new(garment.Positions[v * 3], garment.Positions[v * 3 + 1], garment.Positions[v * 3 + 2]);
        Vector3 N(int v) => v * 3 + 2 < garment.Normals.Length
            ? new(garment.Normals[v * 3], garment.Normals[v * 3 + 1], garment.Normals[v * 3 + 2])
            : Vector3.Zero;

        var whole = garment.Parts.Where(p => p.Island < 0).ToList();
        var tris = new List<(int A, int B, int C, string Label)>();
        foreach (var part in whole.Where(p => !SecondSkinWriter.IsBodySkinMaterial(p.Material)))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a == b || b == c || c == a) continue;
                tris.Add((a, b, c, part.Label));
            }
        var skin = whole.Where(p => SecondSkinWriter.IsBodySkinMaterial(p.Material))
                        .SelectMany(p => p.Triangles).Distinct().ToList();
        if (tris.Count == 0 || skin.Count == 0) return (0, []);

        // Edges two DIFFERENT cloth faces share, counted once per set of corners (a double-sided sheet draws each twice):
        // an edge only one face uses is a hem. Corners are taken by POSITION: a model splits its vertices along every uv
        // or normal seam, and counted by index a seam down the inside of a leg would read as two hems.
        int vc = garment.Positions.Length / 3;
        var at = new SecondSkinWriter.Vec3[vc];
        for (int i = 0; i < vc; i++)
            at[i] = new SecondSkinWriter.Vec3(garment.Positions[i * 3], garment.Positions[i * 3 + 1], garment.Positions[i * 3 + 2]);
        var welded = MeshMath.WeldByPosition(at, out _);
        var edgeUses = new Dictionary<(int, int), int>();
        var faces = new HashSet<(int, int, int)>();
        foreach (var (a, b, c, _) in tris)
        {
            int x = welded[a], y = welded[b], z = welded[c];
            if (x == y || y == z || z == x || !faces.Add(FaceKey(x, y, z))) continue;
            foreach (var key in new[] { EdgeKey(x, y), EdgeKey(y, z), EdgeKey(z, x) })
                edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
        }
        bool Shared(int a, int b) => edgeUses.GetValueOrDefault(EdgeKey(welded[a], welded[b])) >= 2;

        // A corner on a hem: every face around it reaches the same nearest point there, and whichever came first must
        // not decide whether a point past the hem counts — so a hem corner is judged as the hem, whatever face it is read off.
        var hemCorner = new HashSet<int>();
        foreach (var ((x, y), uses) in edgeUses)
            if (uses < 2) { hemCorner.Add(x); hemCorner.Add(y); }

        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / reach), (int)MathF.Floor(p.Y / reach), (int)MathF.Floor(p.Z / reach));
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c, _) = tris[i];
            var lo = Vector3.Min(P(a), Vector3.Min(P(b), P(c))) - new Vector3(reach);
            var hi = Vector3.Max(P(a), Vector3.Max(P(b), P(c))) + new Vector3(reach);
            var (x0, y0, z0) = Cell(lo);
            var (x1, y1, z1) = Cell(hi);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = [];
                list.Add(i);
            }
        }

        int covered = 0;
        var outside = new List<Point>();
        foreach (int v in skin)
        {
            var s = P(v);
            if (!grid.TryGetValue(Cell(s), out var near)) continue;
            float best = float.MaxValue;
            int bestTri = -1;
            Vector3 bestQ = default;
            float bu = 0, bv = 0, bw = 0;
            foreach (int i in near)
            {
                var (a, b, c, _) = tris[i];
                var q = BrushTransfer.ClosestOnTriangle(s, P(a), P(b), P(c), out float u, out float w1, out float w2);
                float d = Vector3.Distance(s, q);
                if (d >= best) continue;
                best = d;
                bestTri = i;
                bestQ = q;
                (bu, bv, bw) = (u, w1, w2);
            }
            if (bestTri < 0 || best > reach) continue;

            var (ta, tb, tc, label) = tris[bestTri];
            // On a corner (two weights near nothing): past the hem if that corner is on one.
            int offCount = (bu < OnEdge ? 1 : 0) + (bv < OnEdge ? 1 : 0) + (bw < OnEdge ? 1 : 0);
            if (offCount >= 2)
            {
                int corner = bu >= OnEdge ? ta : bv >= OnEdge ? tb : tc;
                if (hemCorner.Contains(welded[corner])) continue;
            }
            // On an edge only one face uses: past the hem. (u weighs corner a, so a small u is the edge b–c.)
            else if ((bu < OnEdge && !Shared(tb, tc)) || (bv < OnEdge && !Shared(tc, ta)) || (bw < OnEdge && !Shared(ta, tb)))
                continue;

            var face = Vector3.Cross(P(tb) - P(ta), P(tc) - P(ta));
            if (face.LengthSquared() < 1e-14f) continue;
            face = Vector3.Normalize(face);
            if (Vector3.Dot(face, N(ta) + N(tb) + N(tc)) < 0f) face = -face;
            covered++;
            float by = Vector3.Dot(s - bestQ, face);
            if (by > tolerance) outside.Add(new Point(v, s, label, by));
        }
        return (covered, outside);
    }

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static (int, int, int) FaceKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
