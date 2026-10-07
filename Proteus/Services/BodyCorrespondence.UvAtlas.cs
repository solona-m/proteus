using System;
using System.Collections.Generic;
using System.Numerics;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// The correspondence between two bodies that share a TEXTURE LAYOUT but not a vertex numbering: each point of the
/// source body lands on the point of the target body with the same uv.
/// <para/>
/// This is what makes the refit work across meshes. Two sizes of one body are usually the same mesh and
/// <see cref="IdentityCorrespondence"/> handles them exactly; but a body mod's options are not all one mesh — Neolithe's
/// legs come in plain, Bulge, Gen A/B/C and Puffy variants with different vertex counts, its NSFW chests re-map some
/// uvs — and Rue+ is a different body altogether. What they all share is the Bibo+ uv atlas, because that is what lets
/// one skin texture fit all of them. A uv coordinate therefore names the same place on every one of these bodies —
/// the same point of the nipple, the same point of the hip — and that is exactly the question the refit asks.
/// <para/>
/// The atlas is mirrored in places (the feet, at least), so one uv can lie on both sides of the body. Where it does,
/// the landing on the source point's own side wins, and among those the nearest in space.
/// </summary>
internal sealed class UvAtlasCorrespondence : IBodyCorrespondence
{
    /// <summary>
    /// The share of the source body's skin that must find a landing before the two are accepted as sharing a layout.
    /// A few misses are expected — islands the other body cuts differently, a seam — and they are carried by their
    /// neighbours; many mean the bodies use different atlases, and landing by uv would be nonsense.
    /// </summary>
    private const float MinCoverage = 0.8f;

    /// <summary>How far outside a triangle, in barycentric terms, a uv may land and still count as inside it.</summary>
    private const float InsideSlack = 1e-3f;

    /// <summary>A uv missing every triangle (a gap between islands) may take the nearest one this close in uv.</summary>
    private const float NearUv = 0.004f;

    /// <summary>How far off in uv a point may be and still count as ON a triangle or a collapsed edge: rounding only.</summary>
    private const float OnUv = 1e-5f;

    /// <summary>Within this distance of the midline a point has no side, so a mirrored landing is not penalised.</summary>
    private const float Midline = 0.002f;

    /// <summary>
    /// No landing may be further than this from where it started (250 mm). A size change or even a change of body
    /// moves skin by centimetres; a landing half a body away is the mirrored twin or a different island, not the point.
    /// </summary>
    private const float MaxShift = 0.25f;

    /// <summary>Grid cells across the unit uv square.</summary>
    private const int GridCells = 256;

    private readonly string description;

    private UvAtlasCorrespondence(ModelParts source, IReadOnlyList<Vector3?> field, string what)
    {
        Source = source;
        Field = field;
        description = what;
    }

    public ModelParts Source { get; }

    public IReadOnlyList<Vector3?> Field { get; }

    public string Describe() => description;

    /// <param name="sourceUv">uv0 per source vertex, in <see cref="ModelParts.Positions"/> order.</param>
    /// <param name="targetUv">uv0 per target vertex, in <see cref="ModelParts.Positions"/> order.</param>
    /// <param name="convert">Carries a source uv into the target's texture layout, for two bodies in different layouts
    /// (bibo and gen3, say) — see <see cref="UVRemapService.UvConverter"/>. Null when both share one. A point the
    /// conversion cannot place finds no landing, and counts against <see cref="MinCoverage"/> like any other miss.</param>
    /// <param name="onSheet">The same conversion with next to no reach: answers only for a uv ON the source layout's
    /// sheet. With it, islands drawn off the sheet are found (<see cref="OffSheet"/>), left out of the coverage, and
    /// carried by the skin around them. Null skips that.</param>
    public static bool TryBuild(ModelParts source, float[] sourceUv, ModelParts target, float[] targetUv, string what,
                                out UvAtlasCorrespondence? correspondence, out string refusal,
                                UVRemapService.UvConversion? convert = null, UVRemapService.UvConversion? onSheet = null)
    {
        correspondence = null;
        int svc = source.Positions.Length / 3, tvc = target.Positions.Length / 3;
        if (sourceUv.Length != svc * 2 || targetUv.Length != tvc * 2)
        {
            refusal = string.Format(Loc.Localize("Parts.Retarget.Refuse.UvUnreadable.Fmt",
                          "The {0} models' texture coordinates could not be read, so there is no way to tell which " +
                          "point of one body is which point of the other."), what);
            return false;
        }

        var atlas = new Atlas(target, targetUv);
        if (atlas.IsEmpty)
        {
            refusal = string.Format(Loc.Localize("Parts.Retarget.Refuse.NoTargetSkin.Fmt",
                          "The target {0} model has no body skin to land on."), what);
            return false;
        }

        var field = new Vector3?[svc];
        var skin = SkinVertices(source);
        var offSheet = onSheet != null ? OffSheet(source, sourceUv, skin, onSheet) : [];
        if (offSheet.Count * 2 > skin.Count)
        {
            refusal = string.Format(Loc.Localize("Parts.Retarget.Refuse.OffSheet.Fmt",
                          "Most of the source {0} body is drawn outside its texture layout, so there is no way to " +
                          "tell which point of one body is which point of the other."), what);
            return false;
        }

        int landed = 0;
        foreach (int v in skin)
        {
            if (offSheet.Contains(v)) continue;
            var p = new Vector3(source.Positions[v * 3], source.Positions[v * 3 + 1], source.Positions[v * 3 + 2]);
            var uv = new Vector2(sourceUv[v * 2], sourceUv[v * 2 + 1]);
            if (convert != null)
            {
                // The maps are indexed over the unit tile, so the uv goes onto it first; the side says which half a
                // mirrored layout's point belongs to (+1 for the character's +X).
                var tile = new Vector2(MathF.Floor(uv.X), MathF.Floor(uv.Y));
                var onTile = uv - tile;
                if (convert(onTile.X, onTile.Y, p.X >= 0f ? 1 : -1) is not { } moved) continue;
                uv = new Vector2(moved.U, moved.V);
            }
            if (!atlas.Land(uv, p, out var q)) continue;
            if (Vector3.Distance(p, q) > MaxShift) continue;
            field[v] = q - p;
            landed++;
        }

        int placeable = skin.Count - offSheet.Count;
        float coverage = placeable > 0 ? (float)landed / placeable : 0f;
        if (coverage < MinCoverage)
        {
            refusal = string.Format(Loc.Localize("Parts.Retarget.Refuse.LowCoverage.Fmt",
                          "Only {1:P0} of the source {0} body finds its place on the target by texture coordinate, " +
                          "so the two do not share a texture layout. They cannot be refitted onto each other."),
                          what, coverage);
            return false;
        }

        if (offSheet.Count > 0) CarryOffSheet(source, field, offSheet);
        correspondence = new UvAtlasCorrespondence(source, field,
            $"{what}: matched by texture coordinate, {landed:N0} of {placeable:N0} skin vertices ({coverage:P1})" +
            (offSheet.Count > 0 ? $"; {offSheet.Count:N0} drawn off the texture layout follow the skin around them" : ""));
        refusal = "";
        return true;
    }

    /// <summary>
    /// The source's skin vertices on texture islands that lie OFF its layout's sheet: whole islands, connected by
    /// triangle, most of whose vertices sit where the sheet has nothing.
    /// <para/>
    /// A gen3 body's NSFW crotch detail is drawn on islands of its own in the sheet's empty space (Eve, AB Body: a
    /// fifth to a third of the legs model). No transfer map can say where that lies on another body, so every such
    /// vertex missed, and Eve's legs onto LaRue were refused at 73%. It is judged per ISLAND, and with
    /// <paramref name="onSheet"/> rather than the conversion itself, because the conversion's nearest-pixel search
    /// reaches a little way off the sheet: an island's edge, or a small island just off a big one (Eve's default legs,
    /// by the belly), gets an answer, and that answer is a point of some other island — it landed 6-16 cm away.
    /// </summary>
    internal static HashSet<int> OffSheet(ModelParts source, float[] sourceUv, List<int> skin,
                                         UVRemapService.UvConversion onSheet)
    {
        int vc = source.Positions.Length / 3;
        var parent = new int[vc];
        for (int i = 0; i < vc; i++) parent[i] = i;
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        foreach (var part in source.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                parent[Root(b)] = Root(a);
                parent[Root(c)] = Root(a);
            }
        }

        var counts = new Dictionary<int, (int All, int Missed)>();
        foreach (int v in skin)
        {
            float u = sourceUv[v * 2], w = sourceUv[v * 2 + 1];
            bool missed = onSheet(u - MathF.Floor(u), w - MathF.Floor(w), source.Positions[v * 3] >= 0f ? 1 : -1) == null;
            int r = Root(v);
            var (all, miss) = counts.GetValueOrDefault(r);
            counts[r] = (all + 1, miss + (missed ? 1 : 0));
        }

        var off = new HashSet<int>();
        foreach (int v in skin)
            if (counts[Root(v)] is var (all, missed) && missed * 2 > all)
                off.Add(v);
        return off;
    }

    /// <summary>
    /// Give the off-sheet vertices the displacement of the skin they are joined to, spread inward ring by ring from
    /// the island's seam. The seam is where the island meets the rest of the body at the same POSITION (a uv cut
    /// splits the vertices, so no triangle crosses it). An island joined to nothing takes its nearest placed point's.
    /// </summary>
    private static void CarryOffSheet(ModelParts source, Vector3?[] field, HashSet<int> offSheet)
    {
        int vc = source.Positions.Length / 3;
        Vector3 P(int v) => new(source.Positions[v * 3], source.Positions[v * 3 + 1], source.Positions[v * 3 + 2]);

        var adj = new Dictionary<int, List<int>>();
        void Link(int a, int b)
        {
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = [];
            la.Add(b);
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = [];
            lb.Add(a);
        }
        foreach (var part in source.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = part.Triangles[t + k], b = part.Triangles[t + (k + 1) % 3];
                    if (a < 0 || b < 0 || a >= vc || b >= vc) continue;
                    if (offSheet.Contains(a) || offSheet.Contains(b)) Link(a, b);
                }
        }

        // Welded across the seam: every off-sheet vertex to every skin vertex at its position.
        var at = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(int v) => MeshMath.PositionKey(new SecondSkinWriter.Vec3(P(v).X, P(v).Y, P(v).Z), WeldPerMetre);
        foreach (int v in SkinVertices(source))
        {
            if (!at.TryGetValue(Key(v), out var bucket)) at[Key(v)] = bucket = [];
            bucket.Add(v);
        }
        foreach (int v in offSheet)
            foreach (int m in at[Key(v)])
                if (m != v) Link(v, m);

        // Ring by ring from the seam, until the island is full or no ring reaches further.
        var pending = new HashSet<int>(offSheet);
        while (pending.Count > 0)
        {
            var ring = new List<(int V, Vector3 D)>();
            foreach (int v in pending)
            {
                if (!adj.TryGetValue(v, out var near)) continue;
                var sum = Vector3.Zero;
                int n = 0;
                foreach (int m in near)
                    if (field[m] is { } d) { sum += d; n++; }
                if (n > 0) ring.Add((v, sum / n));
            }
            if (ring.Count == 0) break;
            foreach (var (v, d) in ring)
            {
                field[v] = d;
                pending.Remove(v);
            }
        }

        // Islands joined to nothing: the nearest placed point in space.
        if (pending.Count == 0) return;
        var placed = new List<int>();
        for (int v = 0; v < vc; v++)
            if (field[v] != null && !offSheet.Contains(v)) placed.Add(v);
        if (placed.Count == 0) return;
        foreach (int v in pending)
        {
            var p = P(v);
            int best = placed[0];
            float bestD = float.MaxValue;
            foreach (int m in placed)
            {
                float d = Vector3.DistanceSquared(p, P(m));
                if (d < bestD) (bestD, best) = (d, m);
            }
            field[v] = field[best];
        }
    }

    /// <summary>Two vertices this close (0.01 mm) are one point of the surface, split only by a uv seam.</summary>
    private const float WeldPerMetre = 100_000f;

    private static List<int> SkinVertices(ModelParts m)
    {
        int vc = m.Positions.Length / 3;
        var seen = new bool[vc];
        var list = new List<int>();
        foreach (var part in m.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
            {
                if (v < 0 || v >= vc || seen[v]) continue;
                seen[v] = true;
                list.Add(v);
            }
        }
        return list;
    }

    /// <summary>The target body's skin triangles, bucketed by where they sit in uv.</summary>
    private sealed class Atlas
    {
        private readonly List<(int A, int B, int C)> tris = [];
        private readonly Vector3[] pos;
        private readonly Vector2[] uv;
        private readonly Dictionary<(int, int), List<int>> cells = [];

        public bool IsEmpty => tris.Count == 0;

        public Atlas(ModelParts target, float[] targetUv)
        {
            int vc = target.Positions.Length / 3;
            pos = new Vector3[vc];
            uv = new Vector2[vc];
            for (int i = 0; i < vc; i++)
            {
                pos[i] = new Vector3(target.Positions[i * 3], target.Positions[i * 3 + 1], target.Positions[i * 3 + 2]);
                uv[i] = new Vector2(targetUv[i * 2], targetUv[i * 2 + 1]);
            }

            foreach (var part in target.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                    int index = tris.Count;
                    tris.Add((a, b, c));

                    var lo = Vector2.Min(uv[a], Vector2.Min(uv[b], uv[c]));
                    var hi = Vector2.Max(uv[a], Vector2.Max(uv[b], uv[c]));
                    var (x0, y0) = Cell(lo);
                    var (x1, y1) = Cell(hi);
                    for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        if (!cells.TryGetValue((x, y), out var bucket)) cells[(x, y)] = bucket = [];
                        bucket.Add(index);
                    }
                }
            }
        }

        private static (int, int) Cell(Vector2 p)
            => ((int)MathF.Floor(p.X * GridCells), (int)MathF.Floor(p.Y * GridCells));

        /// <summary>
        /// The point of the target body at <paramref name="at"/> in uv. <paramref name="near"/> is where the point sits
        /// on the source body, which breaks ties between mirrored landings and seam-straddling triangles.
        /// </summary>
        public bool Land(Vector2 at, Vector3 near, out Vector3 landing)
        {
            landing = default;
            float bestScore = float.MaxValue;
            bool found = false;
            int side = Side(near.X);

            // Triangles containing the uv, first; the nearest one in uv only if the uv falls in a gap between islands.
            var (cx, cy) = Cell(at);
            var candidates = new List<(Vector3 Q, float UvDistance)>(3);
            for (int pass = 0; pass < 2 && !found; pass++)
            {
                int reach = pass == 0 ? 0 : (int)MathF.Ceiling(NearUv * GridCells);
                float accept = pass == 0 ? OnUv : NearUv;
                for (int x = cx - reach; x <= cx + reach; x++)
                for (int y = cy - reach; y <= cy + reach; y++)
                {
                    if (!cells.TryGetValue((x, y), out var bucket)) continue;
                    foreach (int t in bucket)
                    {
                        candidates.Clear();
                        Candidates(tris[t], at, candidates);
                        foreach (var (q, uvDistance) in candidates)
                        {
                            if (uvDistance > accept) continue;

                            // The source point's own side first, then the nearest in space.
                            float score = Vector3.Distance(q, near);
                            if (side != 0 && Side(q.X) == -side) score += 10f;
                            if (score >= bestScore) continue;

                            bestScore = score;
                            landing = q;
                            found = true;
                        }
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// The points of one triangle that sit at (or nearest to) <paramref name="at"/> in uv, each with how far off in
        /// uv it is.
        /// <para/>
        /// Normally one point, from the barycentric weights. But a triangle can be COLLAPSED in uv — two corners sharing
        /// one coordinate, so it is a line in the texture with no area — and Neolithe's neck opening is a whole ring of
        /// them. A collapsed triangle has no barycentrics; treating it as "its first corner" put a neck vertex 29 mm from
        /// where it belonged. So it is handled as the segments it really is, and where several places share one uv
        /// every one of them is offered, for the caller to choose the nearest in space.
        /// </summary>
        private void Candidates((int A, int B, int C) tri, Vector2 at, List<(Vector3, float)> into)
        {
            var (a, b, c) = tri;
            var ua = uv[a];
            var ub = uv[b];
            var uc = uv[c];
            var e0 = ub - ua;
            var e1 = uc - ua;
            float area = e0.X * e1.Y - e0.Y * e1.X;

            if (MathF.Abs(area) > 1e-12f)
            {
                Barycentric(at, ua, ub, uc, out float u, out float v, out float w);
                if (MathF.Min(u, MathF.Min(v, w)) >= -InsideSlack)
                {
                    into.Add((pos[a] * u + pos[b] * v + pos[c] * w, 0f));
                    return;
                }
                (u, v, w) = Clamp(u, v, w);
                var onTri = ua * u + ub * v + uc * w;
                into.Add((pos[a] * u + pos[b] * v + pos[c] * w, Vector2.Distance(onTri, at)));
                return;
            }

            Segment(a, b, at, into);
            Segment(b, c, at, into);
            Segment(c, a, at, into);
        }

        /// <summary>The point of edge p-q nearest <paramref name="at"/> in uv; both ends when the edge has no length in uv.</summary>
        private void Segment(int p, int q, Vector2 at, List<(Vector3, float)> into)
        {
            var d = uv[q] - uv[p];
            float len2 = d.LengthSquared();
            if (len2 < 1e-16f)
            {
                // Two places on the body with one uv: offer both.
                float dist = Vector2.Distance(uv[p], at);
                into.Add((pos[p], dist));
                into.Add((pos[q], dist));
                return;
            }
            float t = Math.Clamp(Vector2.Dot(at - uv[p], d) / len2, 0f, 1f);
            into.Add((Vector3.Lerp(pos[p], pos[q], t), Vector2.Distance(uv[p] + d * t, at)));
        }

        private static int Side(float x) => x > Midline ? 1 : x < -Midline ? -1 : 0;

        private static void Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
                                        out float u, out float v, out float w)
        {
            var v0 = b - a;
            var v1 = c - a;
            var v2 = p - a;
            float d00 = Vector2.Dot(v0, v0), d01 = Vector2.Dot(v0, v1), d11 = Vector2.Dot(v1, v1);
            float d20 = Vector2.Dot(v2, v0), d21 = Vector2.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (MathF.Abs(denom) < 1e-20f)
            {
                u = 1f; v = 0f; w = 0f;   // a degenerate uv triangle: only its first corner means anything
                return;
            }
            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = 1f - v - w;
        }

        private static (float, float, float) Clamp(float u, float v, float w)
        {
            u = MathF.Max(u, 0f);
            v = MathF.Max(v, 0f);
            w = MathF.Max(w, 0f);
            float s = u + v + w;
            return s > 1e-12f ? (u / s, v / s, w / s) : (1f, 0f, 0f);
        }
    }
}
