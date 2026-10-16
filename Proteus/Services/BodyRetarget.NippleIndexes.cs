using System;
using System.Collections.Generic;
using System.Numerics;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>Where a source skin vertex lands on the target, and whether the garment's cloth covers it there.</summary>
    internal readonly record struct Landing(Vector3 At, int Source, bool Covered);

    /// <summary>The landings, bucketed by position for the nearest one to a target point.</summary>
    internal sealed class Landings
    {
        private const float Cell = NippleLandingMatch;
        private readonly List<Landing> all = [];
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        public void Add(Landing landing)
        {
            var key = CellOf(landing.At);
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
            bucket.Add(all.Count);
            all.Add(landing);
        }

        /// <summary>The landing nearest <paramref name="p"/> within <paramref name="reach"/> (at most one cell), or null.</summary>
        public Landing? Nearest(Vector3 p, float reach)
        {
            var (cx, cy, cz) = CellOf(p);
            Landing? best = null;
            float bestD = reach * reach;
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int z = cz - 1; z <= cz + 1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int i in bucket)
                {
                    float d = Vector3.DistanceSquared(all[i].At, p);
                    if (d > bestD) continue;
                    bestD = d;
                    best = all[i];
                }
            }
            return best;
        }

        private static (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
    }

    /// <summary>A body's drawn skin vertices, bucketed by position: the haystack the prominence ruler and the coverage
    /// read. Only skin parts, so a piercing on the nipple or the smallclothes over it never count as the breast.</summary>
    internal sealed class SkinIndex
    {
        private const float Cell = 0.01f;
        private readonly ModelParts body;
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        public IReadOnlyList<int> Vertices { get; }

        public SkinIndex(ModelParts body)
        {
            this.body = body;
            var skin = new SortedSet<int>();
            int count = body.Positions.Length / 3;
            foreach (var part in body.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                foreach (int v in part.Triangles) if (v >= 0 && v < count) skin.Add(v);
            }
            Vertices = [.. skin];
            foreach (int v in Vertices)
            {
                var key = CellOf(At(body, v));
                if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
                bucket.Add(v);
            }
        }

        public Vector3 PositionOf(int v) => At(body, v);
        public Vector3 NormalOf(int v) => Normal(body, v);

        /// <summary>Skin vertices within <paramref name="radius"/> of <paramref name="p"/>.</summary>
        public IEnumerable<int> Within(Vector3 p, float radius)
        {
            var (x0, y0, z0) = CellOf(p - new Vector3(radius));
            var (x1, y1, z1) = CellOf(p + new Vector3(radius));
            float r2 = radius * radius;
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int v in bucket)
                    if (Vector3.DistanceSquared(At(body, v), p) <= r2) yield return v;
            }
        }

        private static (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
    }
}
