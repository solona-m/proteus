using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// Carry one model's brush edit onto another model of the same garment — the mod's other sizes.
/// <para/>
/// Sizes do not share vertices: each is its own mesh, with its own counts and order. So the edit is carried by
/// PLACE. Every vertex of the other size takes the displacement, the normal-blend weight and (when wind was
/// painted) the wind of the nearest point on the brushed model's surface, interpolated across the triangle that
/// point lies on. Nearest on the SAME MATERIAL where the brushed model has it, so a size's vest does not borrow
/// from the top underneath it where the two lie close; any cloth where it does not.
/// <para/>
/// A size is a garment scaled or reshaped around the same body, so the nearest point on the brushed size is the
/// matching spot on it. Skin is never a source: the brush never moves it, and a garment's lining lying on the body
/// would otherwise pick up the body's zero.
/// </summary>
internal static class BrushTransfer
{
    /// <summary>Beyond this, a vertex has no counterpart on the brushed model and takes no edit.</summary>
    public const float MaxReach = 0.1f;

    /// <summary>
    /// One model's edit, copied out of its solve: what a transfer reads. A copy so the transfer can run on a worker
    /// thread while the user keeps painting the live solve on the framework thread.
    /// </summary>
    /// <param name="Delta">Per vertex, indexed like <see cref="ModelParts.Positions"/>.</param>
    /// <param name="WindPainted">Wind was edited, so it is carried; otherwise each size keeps its author's.</param>
    public sealed record Edit(Vector3[] Delta, float[] Weight, float[] Wind, bool WindPainted, float MeanEdge)
    {
        /// <summary>Snapshot <paramref name="solve"/>, built over a model of <paramref name="vertexCount"/> vertices.</summary>
        public static Edit From(MeshVolumeSolve solve, int vertexCount)
        {
            var delta = new Vector3[vertexCount];
            var weight = new float[vertexCount];
            var wind = new float[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                var d = solve.DeltaAt(v);
                delta[v] = new Vector3(d.X, d.Y, d.Z);
                weight[v] = solve.WeightAt(v);
                wind[v] = solve.WindAt(v);
            }
            return new Edit(delta, weight, wind, solve.WindEdited, solve.MeanEdge);
        }
    }

    /// <summary>
    /// A new solve for <paramref name="target"/> carrying <paramref name="source"/>'s edit, settled like a stroke that
    /// just ended — ready for <see cref="MeshVolumeService.Apply"/>.
    /// </summary>
    /// <param name="sourceModel">The model <paramref name="source"/> was built from.</param>
    public static MeshVolumeSolve Transfer(MeshVolumeSolve source, ModelParts sourceModel, ModelParts target)
        => Transfer(Edit.From(source, sourceModel.Positions.Length / 3), sourceModel, target);

    /// <inheritdoc cref="Transfer(MeshVolumeSolve, ModelParts, ModelParts)"/>
    /// <param name="edit">The edit, snapshotted — see <see cref="Edit"/>.</param>
    public static MeshVolumeSolve Transfer(Edit edit, ModelParts sourceModel, ModelParts target)
    {
        var src = new Surface(sourceModel, edit.MeanEdge);
        var solve = new MeshVolumeSolve(target);

        int vc = target.Positions.Length / 3;
        var materialOf = new string?[vc];
        foreach (var part in target.Parts)
        {
            if (part.Island >= 0) continue;
            foreach (int v in part.Triangles)
                if (v >= 0 && v < vc) materialOf[v] = part.Material;
        }

        var samples = new (Vector3 Delta, float Weight, float Wind)?[vc];
        for (int v = 0; v < vc; v++)
        {
            var p = new Vector3(target.Positions[v * 3], target.Positions[v * 3 + 1], target.Positions[v * 3 + 2]);
            if (!src.Nearest(p, materialOf[v], out int a, out int b, out int c, out float u, out float w1, out float w2))
                continue;

            var delta = edit.Delta[a] * u + edit.Delta[b] * w1 + edit.Delta[c] * w2;
            float weight = edit.Weight[a] * u + edit.Weight[b] * w1 + edit.Weight[c] * w2;
            float windAt = edit.Wind[a] * u + edit.Wind[b] * w1 + edit.Wind[c] * w2;
            samples[v] = (delta, weight, windAt);
        }

        solve.ImportEdit(samples, edit.WindPainted);
        return solve;
    }

    /// <summary>The brushed model's cloth triangles, bucketed by material and by space for nearest-point queries.</summary>
    private sealed class Surface
    {
        private readonly Vector3[] pos;
        private readonly float cell;
        private readonly int maxRing;
        private readonly Dictionary<string, Grid> byMaterial = new(StringComparer.OrdinalIgnoreCase);
        private readonly Grid any = new();
        private int[] stamp = [];
        private int query;

        private sealed class Grid
        {
            public readonly List<int> Tris = [];   // corner triples
            public readonly Dictionary<(int, int, int), List<int>> Cells = [];
        }

        public Surface(ModelParts model, float meanEdge)
        {
            int vc = model.Positions.Length / 3;
            pos = new Vector3[vc];
            for (int i = 0; i < vc; i++)
                pos[i] = new Vector3(model.Positions[i * 3], model.Positions[i * 3 + 1], model.Positions[i * 3 + 2]);

            cell = Math.Clamp(meanEdge * 2f, 0.005f, 0.03f);
            maxRing = (int)MathF.Ceiling(MaxReach / cell);

            foreach (var part in model.Parts)
            {
                if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                if (!byMaterial.TryGetValue(part.Material, out var grid)) byMaterial[part.Material] = grid = new Grid();
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                    Add(grid, a, b, c);
                    Add(any, a, b, c);
                }
            }
        }

        private void Add(Grid grid, int a, int b, int c)
        {
            int index = grid.Tris.Count / 3;
            grid.Tris.Add(a); grid.Tris.Add(b); grid.Tris.Add(c);
            var lo = Vector3.Min(pos[a], Vector3.Min(pos[b], pos[c]));
            var hi = Vector3.Max(pos[a], Vector3.Max(pos[b], pos[c]));
            var (x0, y0, z0) = Cell(lo);
            var (x1, y1, z1) = Cell(hi);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.Cells.TryGetValue((x, y, z), out var list)) grid.Cells[(x, y, z)] = list = [];
                list.Add(index);
            }
        }

        private (int, int, int) Cell(Vector3 p)
            => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

        /// <summary>The nearest cloth point within <see cref="MaxReach"/>, as a triangle and its barycentrics.</summary>
        public bool Nearest(Vector3 p, string? material, out int a, out int b, out int c,
                            out float u, out float v, out float w)
        {
            var grid = material != null && byMaterial.TryGetValue(material, out var own) ? own : any;
            if (Search(grid, p, out a, out b, out c, out u, out v, out w)) return true;
            return !ReferenceEquals(grid, any) && Search(any, p, out a, out b, out c, out u, out v, out w);
        }

        private bool Search(Grid grid, Vector3 p, out int a, out int b, out int c,
                            out float u, out float v, out float w)
        {
            a = b = c = -1;
            u = v = w = 0f;
            if (grid.Tris.Count == 0) return false;

            int count = grid.Tris.Count / 3;
            if (stamp.Length < count) stamp = new int[Math.Max(count, stamp.Length * 2)];
            if (++query == int.MaxValue) { Array.Clear(stamp); query = 1; }

            var (cx, cy, cz) = Cell(p);
            float best = MaxReach * MaxReach;
            bool found = false;
            for (int r = 0; r <= maxRing; r++)
            {
                for (int x = -r; x <= r; x++)
                for (int y = -r; y <= r; y++)
                for (int z = -r; z <= r; z++)
                {
                    if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != r) continue;   // this ring's shell only
                    if (!grid.Cells.TryGetValue((cx + x, cy + y, cz + z), out var list)) continue;
                    foreach (int t in list)
                    {
                        if (stamp[t] == query) continue;
                        stamp[t] = query;
                        int ta = grid.Tris[t * 3], tb = grid.Tris[t * 3 + 1], tc = grid.Tris[t * 3 + 2];
                        var q = ClosestOnTriangle(p, pos[ta], pos[tb], pos[tc], out float tu, out float tv, out float tw);
                        float d2 = Vector3.DistanceSquared(p, q);
                        if (d2 >= best) continue;
                        best = d2; found = true;
                        a = ta; b = tb; c = tc; u = tu; v = tv; w = tw;
                    }
                }
                // Every cell of the next ring is at least r cells away, so nothing there can beat a point this near.
                if (found && best <= (r * cell) * (r * cell)) break;
            }
            return found;
        }
    }

    /// <summary>
    /// The point of triangle abc nearest <paramref name="p"/>, with its barycentrics (u for a, v for b, w for c).
    /// Ericson, Real-Time Collision Detection, 5.1.5.
    /// </summary>
    internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c,
                                              out float u, out float v, out float w)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) { u = 1f; v = 0f; w = 0f; return a; }

        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) { u = 0f; v = 1f; w = 0f; return b; }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            u = 1f - t; v = t; w = 0f;
            return a + ab * t;
        }

        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) { u = 0f; v = 0f; w = 1f; return c; }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            u = 1f - t; v = 0f; w = t;
            return a + ac * t;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            u = 0f; v = 1f - t; w = t;
            return b + (c - b) * t;
        }

        float denom = 1f / (va + vb + vc);
        v = vb * denom;
        w = vc * denom;
        u = 1f - v - w;
        return a + ab * v + ac * w;
    }
}
