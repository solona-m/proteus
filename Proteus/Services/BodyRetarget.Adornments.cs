using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// A body mod's nails and piercings: meshes in the body's own material family that are not skin, and go with the
    /// skin they sit on — <c>_piercings</c>, <c>_neolithe_piercings</c>, <c>_nipplepierce</c>, <c>_neolithe_nails</c>,
    /// <c>_yafinger</c>, <c>_yatoe</c>, Tre's <c>_trenails</c> and <c>_treaccent</c>. A list, not "every body material
    /// that is not skin": the same family names the genitals (<c>_neolithe_penis</c>, <c>_tre_trans</c>) and undies
    /// (<c>_ckbra</c>, <c>_ckundies</c>), which a garment must never be handed. Pubic hair stays out too: it sits under
    /// whatever the legs wear.
    /// </summary>
    internal static bool IsBodyAdornmentMaterial(string material)
        => !SecondSkinWriter.IsBodySkinMaterial(material) && BodyAdornment.IsMatch(material);

    private static readonly Regex BodyAdornment = new(@"(^|/)mt_c\d{4}b\d{4}_.*(pierc|nail|finger|toe|accent)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// How far off the new body an adornment may stand and still be on it (20 mm): a stiletto nail runs well past the
    /// fingertip, a hanging ring well off the nipple. Beyond it a vertex says nothing about the skin it belongs to.
    /// </summary>
    internal const float AdornReach = 0.02f;

    /// <summary>
    /// How far in front of an adornment cloth still covers it (5 mm): a glove's fingertip wraps the nail a millimetre
    /// or two off it, a pasty lies on the ring through it.
    /// </summary>
    internal const float AdornCover = 0.005f;

    /// <summary>
    /// How close the garment's own nail or piercing must have been for the new body's to take its place whatever covers
    /// it (5 mm): the author drew one there, through sheer cloth or not.
    /// </summary>
    internal const float AdornSame = 0.005f;

    /// <summary>The new body's nails and piercings a swap carries in, as layers and the cut that goes with them.</summary>
    /// <param name="Materials">One per material the kept islands are drawn with, in the body's own order.</param>
    /// <param name="DrawOnly">Per adornment mesh, the vertices of its kept islands (mesh-local) — merged into the
    /// body's skin cut, since a cut belongs to the model.</param>
    /// <param name="Triangles">Triangles carried in.</param>
    private readonly record struct Adornments(List<string> Materials, Dictionary<int, HashSet<ushort>> DrawOnly,
                                              int Triangles);

    /// <summary>
    /// Which of a body's nails and piercings (<see cref="IsBodyAdornmentMaterial"/>) go into the garment with its skin,
    /// one island at a time. A garment refitted onto Tre's hands took Tre's skin and nothing else, so the nails Tre
    /// draws in their own material (<c>_trenails</c>, switched on by a nail mod's <c>atrx_nt_mani</c>) never came with
    /// it and the hands went bare.
    /// <para/>
    /// An island comes in when the skin under it is skin the swap draws — a glove whose author deleted the fingertips
    /// hides the nails with them — and no cloth lies over it: a top that keeps the skin under an opaque cup would
    /// otherwise have the ring stand through it, and a glove that paints its own nail caps would have the body's nail
    /// round them. Where the garment carried its own nail or piercing, the author showed
    /// one there, and the body's takes its place whatever lies over it.
    /// </summary>
    /// <param name="body">The new body, as it goes in (pulled back to the author's edge, if it was).</param>
    /// <param name="hidden">The body's variant tags its mod does not draw; their parts are left out.</param>
    /// <param name="cut">The skin cut (see <see cref="CutLike"/>), or null when the skin goes in whole.</param>
    /// <param name="cloth">The garment's cloth.</param>
    /// <param name="authored">Where the garment's own nails and piercings were.</param>
    private static Adornments AdornmentsOf(byte[] body, IReadOnlySet<string>? hidden,
                                           Dictionary<int, HashSet<ushort>>? cut, ClothCrossings cloth,
                                           PointGrid authored)
    {
        var none = new Adornments([], [], 0);
        if (ModelPartReader.Read(body) is not { } read) return none;
        var parts = Without(read, hidden);
        var adorn = parts.Parts.Where(p => p.Island < 0 && IsBodyAdornmentMaterial(p.Material)).ToList();
        if (adorn.Count == 0) return none;

        var spanOf = parts.MeshSpans.ToDictionary(s => s.Mesh);
        var skin = new BodySurface(parts, BodySurface.CellFor(MeanEdgeOf(parts)));
        if (skin.IsEmpty) return none;

        // Every island, the submesh's own; a submesh the reader could not split stands as one.
        var islands = new List<(ModelPart Sub, int[] Tris, bool? Verdict)>();
        foreach (var sub in adorn)
        {
            var own = parts.Parts.Where(p => p.Island >= 0 && p.Mesh == sub.Mesh && p.Submesh == sub.Submesh)
                                 .Select(p => p.Triangles).ToList();
            if (own.Count == 0) own.Add(sub.Triangles);
            islands.AddRange(own.Select(tris => (sub, tris, Carried(tris))));
        }

        // An island too far off the skin to land anywhere — a chain strung between two nipple rings, 20-30 mm in front of
        // the sternum on YAB — goes with whatever it hangs from: in when it touches an island that came in, and so on
        // link by link. Hung from nothing that came in, it stays out.
        var kept = new PointGrid();
        foreach (var island in islands.Where(i => i.Verdict == true))
            foreach (int v in island.Tris) kept.Add(At(parts, v));
        for (bool grew = true; grew;)
        {
            grew = false;
            for (int i = 0; i < islands.Count; i++)
            {
                if (islands[i].Verdict != null || !islands[i].Tris.Any(v => kept.Any(At(parts, v), AdornSame))) continue;
                islands[i] = islands[i] with { Verdict = true };
                foreach (int v in islands[i].Tris) kept.Add(At(parts, v));
                grew = true;
            }
        }

        var draw = new Dictionary<int, HashSet<ushort>>();
        var materials = new List<string>();
        int triangles = 0;
        foreach (var (sub, tris, verdict) in islands)
        {
            if (!spanOf.TryGetValue(sub.Mesh, out var span)) continue;
            var set = draw.TryGetValue(sub.Mesh, out var have) ? have : draw[sub.Mesh] = [];
            if (verdict != true) continue;
            foreach (int v in tris)
                if (v - span.BaseVertex is >= 0 and <= ushort.MaxValue) set.Add((ushort)(v - span.BaseVertex));
            triangles += tris.Length / 3;
            if (!materials.Contains(sub.Material, StringComparer.Ordinal)) materials.Add(sub.Material);
        }
        return new Adornments(materials, draw, triangles);

        // In, out, or null when no part of it is near enough the skin to say.
        bool? Carried(int[] tris)
        {
            var verts = tris.Distinct().ToList();
            if (verts.Any(v => authored.Any(At(parts, v), AdornSame))) return true;
            int landed = 0, onDrawn = 0, covered = 0;
            foreach (int v in verts)
            {
                var p = At(parts, v);
                if (!skin.Nearest(p, AdornReach, out var hit)) continue;
                landed++;
                if (Drawn(hit)) onDrawn++;
                if (cloth.Over(hit.Point, hit.Normal, hit.Distance + AdornCover)) covered++;
            }
            // A quarter under cloth is under it: a garment's own nail caps, made for a shorter nail, cover 36-70% of Tre's
            // (Meru's armlets, Bibo+ -> Tre), and at half the fingers were split, a Tre nail standing out round some caps.
            if (landed == 0) return null;
            return onDrawn * 2 >= landed && covered * 4 < landed;
        }

        // As the writer reads a cut: a triangle draws when any corner is in its mesh's set, and a mesh with no entry
        // draws whole.
        bool Drawn(BodySurface.Hit hit)
        {
            if (cut == null) return true;
            foreach (int v in (ReadOnlySpan<int>)[hit.A, hit.B, hit.C])
            {
                var span = parts.MeshSpans.FirstOrDefault(s => v >= s.BaseVertex && v < s.BaseVertex + s.Count);
                if (!cut.TryGetValue(span.Mesh, out var set)) return true;
                if (v - span.BaseVertex is >= 0 and <= ushort.MaxValue && set.Contains((ushort)(v - span.BaseVertex)))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Points bucketed by space, for "was there one within so far of here".</summary>
    private sealed class PointGrid
    {
        private const float Cell = 0.01f;
        private readonly Dictionary<(int, int, int), List<Vector3>> cells = [];

        public bool IsEmpty => cells.Count == 0;

        public void Add(Vector3 p)
        {
            var key = CellOf(p);
            if (!cells.TryGetValue(key, out var bucket)) cells[key] = bucket = [];
            bucket.Add(p);
        }

        /// <summary>Whether any point lies within <paramref name="reach"/> (at most one cell) of <paramref name="p"/>.</summary>
        public bool Any(Vector3 p, float reach)
        {
            if (cells.Count == 0) return false;
            var (x0, y0, z0) = CellOf(p);
            float r2 = reach * reach;
            for (int x = x0 - 1; x <= x0 + 1; x++)
            for (int y = y0 - 1; y <= y0 + 1; y++)
            for (int z = z0 - 1; z <= z0 + 1; z++)
            {
                if (!cells.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (var q in bucket)
                    if (Vector3.DistanceSquared(p, q) <= r2) return true;
            }
            return false;
        }

        private static (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
    }
}
