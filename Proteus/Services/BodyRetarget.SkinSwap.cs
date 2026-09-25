using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How close a garment skin vertex must lie to a slot's new body to count as that slot's skin (2 mm). The skin was
    /// laid onto the new body just before, so skin that belongs to the slot sits ON it — the body mesh's own worst
    /// case is 1.25 mm — while skin of another slot is centimetres away from all but the seam between them.
    /// </summary>
    internal const float SwapOnBody = 0.002f;

    /// <summary>
    /// How much of a skin mesh has to sit on the bodies for it to be the body's skin and be swapped (95%). Below it the
    /// mesh is the garment's own — a heeled shoe's foot, an author's sculpted piece — and is kept whole.
    /// </summary>
    internal const float OnBodyShare = 0.95f;

    /// <summary>
    /// How close the new body must come to the garment's own skin for that face to be one the author kept (4 mm). The
    /// garment's skin has just been laid onto the new body, so a face the author kept sits ON the body mod's own — the
    /// two are the same mesh where both draw — while a face the author deleted has centimetres of cloth over it.
    /// </summary>
    internal const float CutReach = 0.004f;

    /// <summary>
    /// How far off a garment skin triangle the body may bulge, as a share of that triangle's longest edge, and still be
    /// under it (20%). Laid onto the new body, a skin triangle's CORNERS sit on it; its middle is a flat chord across a
    /// curve, and the body rises off it by about edge²/8r. The game's own gear draws the arm in triangles 40 mm across,
    /// up to 66 — the Oversized Plain Neotunic's upper arm came out 4.8 mm under its chords, past
    /// <see cref="CutReach"/>, and the swap cut holes in both arms the author never made. Only a landing INSIDE a
    /// triangle counts: a body vertex past the edge of the garment's skin lands on that edge, and the author's cut stands.
    /// </summary>
    internal const float CutSagShare = 0.2f;

    /// <summary>The most <see cref="CutSagShare"/> may allow, however large the triangle (12 mm).</summary>
    internal const float CutSagMax = 0.012f;

    /// <param name="Removed">Triangles in the garment's skin meshes that were taken out.</param>
    /// <param name="Added">Triangles in the body skin meshes put in their place.</param>
    /// <param name="Kept">Skin meshes left alone because they belong to no slot being resized.</param>
    /// <param name="LostShapes">Shape keys the garment had, which the rebuilt model does not carry.</param>
    /// <param name="Reweighted">Cloth vertices given the new body's weights (see <see cref="PlanWeights"/>).</param>
    /// <param name="Trimmed">Of those, vertices whose body weights were cut to fit the eight-influence limit.</param>
    /// <param name="ExtrasDropped">Triangles of the old body's piercings and pubic hair taken out.</param>
    /// <param name="Unplaced">Influences the writer could not place — a bone in no model it was given, or a full table.</param>
    /// <param name="Posed">Skin meshes only partly on the bodies — a heeled shoe's own foot — which were kept.</param>
    /// <param name="Cut">Triangles of the new body's skin left out, because the garment's author deleted the body
    /// there — see <see cref="CutLike"/>.</param>
    /// <param name="Trimmed">Vertices the PLANNER could not fit in eight influences.</param>
    /// <param name="Slotted">Influences the MESH had no slot for — a four-slot cloth mesh given a vertex planned
    /// with five. The heaviest are kept and the weights renormalised, so nothing shrinks.</param>
    internal readonly record struct SwapReport(int Removed, int Added, int Kept, int LostShapes,
                                               int Reweighted = 0, int Trimmed = 0, int ExtrasDropped = 0,
                                               int Unplaced = 0, int Posed = 0, int Cut = 0, int Slotted = 0);

    /// <summary>
    /// Swap the garment's skin for the new body's, one body slot at a time: every skin mesh of the garment that belongs
    /// to a slot being resized is taken out whole, and that slot's new body skin mesh is put in whole — the body mod's
    /// own mesh for the new size, with its triangles, weights and normals as the body mod ships them.
    /// <para/>
    /// Only the slots being resized. A body mod splits the body across slots — the chest model is the torso, neck and
    /// arms to the wrists; the legs model is the waist down — and a garment can carry skin of more than one: a long top
    /// has the hips. Skin of a slot nobody is resizing stays exactly as the author left it.
    /// <para/>
    /// A skin mesh belongs to the slot whose new body most of its vertices lie on. Whole meshes, never some of their
    /// triangles: the author's mesh is replaced by the body mod's, not cut into.
    /// <para/>
    /// The one step that re-emits the model rather than editing it in place: a mesh from another file cannot be added
    /// otherwise. It goes through the second-skin writer's host path (<c>SecondSkinWriter.Build</c> with a base model),
    /// which copies the garment's remaining meshes verbatim and appends the body's; only LOD0 survives it, and no shape
    /// keys.
    /// </summary>
    /// <param name="garment">The refitted garment model, its skin already laid onto the new bodies.</param>
    /// <param name="pairs">The slots being resized. Only those carrying their target body's file can be swapped.</param>
    /// <param name="cutHidden">Leave out the new body's skin where the garment's author deleted theirs — see
    /// <see cref="CutLike"/>. Off puts the body in whole.</param>
    /// <returns>The rebuilt model, or null when no skin mesh of the garment belongs to a slot being resized.</returns>
    internal static byte[]? SwapSkin(byte[] garment, IReadOnlyList<SlotPair> pairs, out SwapReport report,
                                     bool cutHidden = true)
        => Rebuild(garment, pairs, swapSkin: true, weights: null, out report, cutHidden);

    /// <summary>
    /// Rebuild the refitted garment for its new body: swap the resized slots' skin meshes for the body's (see
    /// <see cref="SwapSkin"/>), give its cloth the new body's weights (see <see cref="PlanWeights"/>), and — whenever the
    /// weights change, which is to say across rigs — drop the piercings and pubic hair it carried from its old body
    /// (see <see cref="IsBodyExtraMaterial"/>). One re-emit for all three.
    /// </summary>
    /// <param name="cutHidden">Carry the author's cut onto the new body's skin (see <see cref="CutLike"/>).</param>
    /// <returns>The rebuilt model, or null when there was nothing to change.</returns>
    internal static byte[]? Rebuild(byte[] garment, IReadOnlyList<SlotPair> pairs, bool swapSkin, WeightPlan? weights,
                                    out SwapReport report, bool cutHidden = true)
    {
        report = default;
        var swappable = swapSkin ? pairs.Where(p => p.TargetModel != null).ToList() : [];
        if ((swappable.Count == 0 && weights == null) || ModelPartReader.Read(garment) is not { } model) return null;

        var surfaces = swappable.Select(p => new BodySurface(p.Target, BodySurface.CellFor(MeanEdgeOf(p.Target))))
                                .ToList();

        // Each skin mesh's vertices, and which slot claims it.
        var skinMeshes = model.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                    .GroupBy(p => p.Mesh)
                                    .ToList();
        var dropped = new HashSet<int>();
        var claimedBy = new HashSet<int>();   // indices into swappable
        string? skinMaterial = null;
        int removed = 0, kept = 0, posed = 0;
        foreach (var mesh in skinMeshes)
        {
            var verts = mesh.SelectMany(p => p.Triangles).Distinct().ToList();

            // Every vertex, not a majority: a mesh is replaced only when the whole of it IS the body's skin. A heeled
            // shoe draws the foot itself, turned onto the toe, in the same mesh as the lower leg. The leg sits on the
            // body and the foot does not, and a majority rule handed the whole mesh to the legs — whose skin has no
            // foot — so the foot vanished. Off the body, the author's skin is what the garment needs, and it stays.
            int best = -1;
            float bestShare = 0f;
            int onAny = verts.Count(v => surfaces.Any(s => s.Nearest(At(model, v), SwapOnBody, out _)));
            for (int s = 0; s < surfaces.Count; s++)
            {
                int on = verts.Count(v => surfaces[s].Nearest(At(model, v), SwapOnBody, out _));
                float share = verts.Count > 0 ? (float)on / verts.Count : 0f;
                if (share > bestShare) { bestShare = share; best = s; }
            }
            if (verts.Count > 0 && onAny < verts.Count * OnBodyShare)
            {
                // Some of it is the body's and some is not: the whole mesh stays, since half a mesh cannot be swapped.
                if (best >= 0) posed++;
                kept++;
                continue;
            }
            if (best < 0)
            {
                kept++;
                continue;
            }
            dropped.Add(mesh.Key);
            claimedBy.Add(best);
            skinMaterial ??= mesh.First().Material;
            removed += mesh.Sum(p => p.Triangles.Length / 3);
        }
        // The old body's extras: they sit on the shape the garment is leaving.
        int extras = 0;
        if (weights != null)
            foreach (var part in model.Parts)
                if (part.Island < 0 && IsBodyExtraMaterial(part.Material) && dropped.Add(part.Mesh))
                    extras += model.Parts.Where(q => q.Island < 0 && q.Mesh == part.Mesh).Sum(q => q.Triangles.Length / 3);

        if (dropped.Count == 0 && weights == null)
        {
            // Nothing to rebuild, but the caller still says WHY nothing was swapped: a mesh only partly on the body is
            // kept, and the user is told so rather than left wondering.
            report = new SwapReport(0, 0, kept, 0, Posed: posed);
            return null;
        }

        // The author's own cut, carried onto the new body. A garment's author hides the body under the cloth by
        // deleting its faces; the body mod ships the body whole. Put in whole, the shoulder the author deleted comes
        // back through the jacket — so the body mod's skin only draws where the garment's skin drew. Unless the user
        // turned that off, and the body goes in whole.
        var drawn = cutHidden ? new BodySurface(model, BodySurface.CellFor(MeanEdgeOf(model))) : null;
        var drawnEdges = cutHidden ? SkinEdges.Of(model) : null;
        var cuts = new Dictionary<int, Dictionary<int, HashSet<ushort>>?>();
        int cutTris = 0, keptTris = 0;
        foreach (int s in claimedBy)
        {
            int these = 0, gone = 0;
            cuts[s] = drawn == null || drawn.IsEmpty ? null : CutLike(drawn, drawnEdges!, swappable[s].TargetModel!,
                                                                      swappable[s].TargetHidden, out these, out gone);
            keptTris += cuts[s] == null ? SkinTriangles(swappable[s].Target) : these;
            cutTris += gone;
        }

        // One layer per slot whose skin came out: its body's skin meshes, under the right skin material.
        var layers = claimedBy.OrderBy(s => s).Select(s => new SecondSkinLayer
        {
            MaterialName = SkinMaterialFor(skinMaterial!, swappable[s].TargetModel!),
            // Tagged as the body tags it — atr_ude, atr_hij, atr_nek are how long gloves or a high collar hide the
            // skin under them, and the garment's own skin carried the same tags — except for variant tags, which would
            // be judged against the garment's IMC mask (a Neolithe body carries eight, atr_tv_a..h). Dropping the tag
            // draws the part always, so the variants the body mod leaves off are left out first: two alternatives of
            // one piece would otherwise both be drawn, the larger showing through the cloth fitted to the other.
            Geometry = [new ContentGeometry(swappable[s].TargetModel!, SecondSkinWriter.IsBodySkinMaterial,
                                            HiddenAttributes: swappable[s].TargetHidden,
                                            DropVariantAttributes: true, DrawOnly: cuts[s])],
        }).ToList();
        var reskinned = new SecondSkinWriter.ReskinReport();
        var rebuilt = SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(), layers, garment, out _,
                                             dropHostMesh: dropped.Contains,
                                             hostReskin: weights == null ? null : weights.For,
                                             boneDonors: weights?.Donors, reskinReport: reskinned);

        report = new SwapReport(removed, keptTris, kept, SecondSkinWriter.Parse(garment).Shapes.Count,
                                weights?.Reweighted ?? 0, weights?.Trimmed ?? 0, extras, reskinned.Dropped, posed,
                                cutTris, reskinned.Trimmed);
        return rebuilt;
    }

    /// <summary>
    /// Which of a body model's skin vertices the garment's own skin still draws, per mesh, for
    /// <see cref="ContentGeometry.DrawOnly"/>. A vertex counts when the garment's skin — already laid onto this body —
    /// passes within <see cref="CutReach"/> of it.
    /// <para/>
    /// Null when the garment's skin covers the whole body: nothing was cut, so nothing is filtered, and the swap puts
    /// the body in exactly as it always did.
    /// </summary>
    /// <param name="drawn">The garment's own skin, laid onto the new body.</param>
    /// <param name="kept">Triangles of the body's skin that survive the cut.</param>
    /// <param name="cut">Triangles left out.</param>
    /// <param name="edges">Which of <paramref name="drawn"/>'s edges two of its triangles share — see <see cref="Covers"/>.</param>
    /// <param name="hidden">The body's variant tags its mod does not draw; their parts are neither kept nor counted.</param>
    private static Dictionary<int, HashSet<ushort>>? CutLike(BodySurface drawn, SkinEdges edges, byte[] body,
                                                             IReadOnlySet<string>? hidden, out int kept, out int cut)
    {
        kept = cut = 0;
        if (ModelPartReader.Read(body) is not { } read) return null;
        var parts = Without(read, hidden);

        var baseOf = new Dictionary<int, int>();
        foreach (var span in parts.MeshSpans) baseOf[span.Mesh] = span.BaseVertex;

        var covered = new bool[parts.Positions.Length / 3];
        foreach (int v in parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                     .SelectMany(p => p.Triangles).Distinct())
            covered[v] = Covers(drawn, At(parts, v), edges);

        var sets = new Dictionary<int, HashSet<ushort>>();
        foreach (var part in parts.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            if (!baseOf.TryGetValue(part.Mesh, out int bv)) continue;

            // Every skin mesh gets its entry HERE, before a triangle is judged. A mesh missing from the dictionary
            // draws whole — that is how the writer reads one nothing cut — so a mesh the cut empties has to be in it
            // with an empty set, or the one mesh most deserving of the cut is the one that comes back in full.
            var set = sets.TryGetValue(part.Mesh, out var have) ? have : sets[part.Mesh] = [];
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (!covered[a] && !covered[b] && !covered[c]) { cut++; continue; }
                kept++;
                // The emit loop reads MESH-LOCAL indices, which is what the file stores.
                Keep(set, a, bv);
                Keep(set, b, bv);
                Keep(set, c, bv);
            }
        }
        if (cut == 0) { kept = 0; return null; }   // nothing cut: the body goes in whole, as before
        return sets;

        void Keep(HashSet<ushort> set, int v, int bv)
        {
            if (covered[v] && v - bv is >= 0 and <= ushort.MaxValue) set.Add((ushort)(v - bv));
        }
    }

    /// <summary>
    /// Whether the garment's laid skin still draws over this body point: within <see cref="CutReach"/> of it, or no
    /// further off than the skin's chord could sag there — see <see cref="CutSagShare"/>. The chord is the triangle when
    /// the point lands inside one, and the edge when it lands on an edge two triangles share: over a limb the body rises
    /// off an edge between two coarse triangles as far as off either triangle's middle, and it is the edge the nearest
    /// point falls on. An edge only one triangle uses is where the author stopped drawing skin, and there the plain
    /// reach stands.
    /// </summary>
    /// <param name="edges">The drawn skin's shared edges; null counts every edge as the author's boundary.</param>
    internal static bool Covers(BodySurface drawn, Vector3 p, SkinEdges? edges = null)
    {
        if (!drawn.Nearest(p, CutSagMax, out var hit)) return false;
        if (hit.Distance <= CutReach) return true;

        const float inside = 0.01f;   // a landing clamped onto an edge has a zero weight; this is off it
        bool offA = hit.U < inside, offB = hit.V < inside, offC = hit.W < inside;
        Vector3 a = drawn.PositionOf(hit.A), b = drawn.PositionOf(hit.B), c = drawn.PositionOf(hit.C);

        float chord;
        if (!offA && !offB && !offC)
            chord = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
        else if (offA && !offB && !offC && edges?.IsShared(hit.B, hit.C) == true)
            chord = Vector3.Distance(b, c);
        else if (offB && !offA && !offC && edges?.IsShared(hit.C, hit.A) == true)
            chord = Vector3.Distance(c, a);
        else if (offC && !offA && !offB && edges?.IsShared(hit.A, hit.B) == true)
            chord = Vector3.Distance(a, b);
        else
            return false;   // on the author's boundary, or on a corner, which sits on the body
        return hit.Distance <= MathF.Min(CutSagMax, CutSagShare * chord);
    }

    /// <summary>
    /// The edges of a model's skin that two of its triangles share, with its vertices welded by position: a body mesh
    /// splits vertices at uv seams, and an edge along a seam is still inside the skin.
    /// </summary>
    internal sealed class SkinEdges
    {
        private readonly int[] nodeOf;
        private readonly HashSet<(int, int)> shared = [];

        private SkinEdges(ModelParts m)
        {
            int vc = m.Positions.Length / 3;
            var pos = new Vec3[vc];
            for (int v = 0; v < vc; v++) pos[v] = new Vec3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
            nodeOf = MeshMath.WeldByPosition(pos, out _);

            var seen = new HashSet<(int, int)>();
            foreach (var part in m.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        var key = Key(part.Triangles[t + e], part.Triangles[t + (e + 1) % 3]);
                        if (!seen.Add(key)) shared.Add(key);
                    }
            }
        }

        internal static SkinEdges Of(ModelParts m) => new(m);

        /// <summary>Whether the edge between these two vertices of the model is used by two triangles or more.</summary>
        internal bool IsShared(int a, int b) => shared.Contains(Key(a, b));

        private (int, int) Key(int a, int b)
        {
            int na = nodeOf[a], nb = nodeOf[b];
            return na < nb ? (na, nb) : (nb, na);
        }
    }

    /// <summary>
    /// Which skin material the body's mesh is drawn with once it is in the garment.
    /// <para/>
    /// The garment's own, normally: an author who gave their skin its own material — a tattoo, a scar — meant it, and
    /// a garment made for this body already names a material in the body's own texture layout.
    /// <para/>
    /// But the mesh going in is the BODY's, with the body's texture coordinates, and a material describes a layout.
    /// Where the two disagree the garment's material is simply wrong for the mesh now under it: the game's own gear
    /// names the vanilla skin material, whose texture is laid out gen2, while the body it is being refitted onto is
    /// bibo. Same geometry, wrong half of the sheet — which reads in game as pale blocks across the back, hard-edged
    /// where the texture's islands change. The body's own material is what that mesh is drawn with everywhere else on
    /// the character, so it is what matches.
    /// </summary>
    private static string SkinMaterialFor(string garmentMaterial, byte[] body)
    {
        string? want = SecondSkinWriter.SkinMaterialBodyType(garmentMaterial);

        foreach (string name in SecondSkinWriter.Parse(body).MatNames)
        {
            if (SecondSkinWriter.SkinMaterialBodyType(name) is not { } layout) continue;
            if (want == null || !string.Equals(layout, want, StringComparison.OrdinalIgnoreCase)) return name;
            break;   // they agree: the author's material stands
        }
        return garmentMaterial;
    }

    private static Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>Triangles of a body's skin: its parts drawn with a body-skin material.</summary>
    private static int SkinTriangles(ModelParts body)
        => body.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                     .Sum(p => p.Triangles.Length / 3);
}
