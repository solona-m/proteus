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
    /// How much of a skin mesh has to sit on one slot's body alone for that slot's skin to go in with it (5%): enough
    /// that it is a part of the body the garment draws — a dress's legs — not the few vertices along a waist seam.
    /// </summary>
    internal const float SpanShare = 0.05f;

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
        var mostOf = new HashSet<int>();      // slots some mesh lies MOSTLY on
        var reachedOnto = new HashSet<int>(); // slots a mesh only reaches onto (a dress's legs, a long top's hips)
        string? skinMaterial = null;
        int removed = 0, kept = 0, posed = 0;
        foreach (var mesh in skinMeshes)
        {
            var verts = mesh.SelectMany(p => p.Triangles).Distinct().ToList();

            // Every vertex, not a majority: a mesh is replaced only when the whole of it IS the body's skin. A heeled
            // shoe draws the foot itself, turned onto the toe, in the same mesh as the lower leg. The leg sits on the
            // body and the foot does not, and a majority rule handed the whole mesh to the legs — whose skin has no
            // foot — so the foot vanished. Off the body, the author's skin is what the garment needs, and it stays.
            // "On" here allows the millimetres a laid point can miss the body by — see OnBody. The Pioneer's Bottoms'
            // waist strip is 38 vertices, and two of its corners landed 6 mm off Rue+'s hip: 94.7% within 2 mm, so the
            // vanilla strip was kept over the new body. The posed foot this guards against stands centimetres off.
            int best = -1;
            float bestShare = 0f;
            int onAny = verts.Count(v => surfaces.Any(s => OnBody(s, At(model, v))));
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
            // Every slot the mesh really spans, not only the one most of it lies on: a full-body dress draws torso and
            // legs in ONE skin mesh, and handing it to the chest alone put back the chest's skin and nothing below the
            // hips — every size but the hand-made one came out legless. A slot counts when enough of the mesh sits on its
            // body and on no other; the vertices along the waist seam sit on both and decide nothing.
            claimedBy.Add(best);
            mostOf.Add(best);
            for (int s = 0; s < surfaces.Count && surfaces.Count > 1; s++)
            {
                if (s == best) continue;
                int only = verts.Count(v => surfaces[s].Nearest(At(model, v), SwapOnBody, out _)
                                         && !surfaces.Where((_, o) => o != s)
                                                     .Any(o => o.Nearest(At(model, v), SwapOnBody, out _)));
                if (only < verts.Count * SpanShare) continue;
                claimedBy.Add(s);
                reachedOnto.Add(s);
            }
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
        // A slot the garment only REACHES onto is cut whatever the setting: whole, a long top's hips would bring the
        // entire legs body down to the ankles into the top's model, drawing through whatever pants are worn.
        var alwaysCut = new HashSet<int>(reachedOnto.Except(mostOf));
        bool anyCut = cutHidden || alwaysCut.Count > 0;
        var drawn = anyCut ? new BodySurface(model, BodySurface.CellFor(MeanEdgeOf(model))) : null;
        var drawnEdges = anyCut ? SkinEdges.Of(model) : null;
        var cloth = anyCut ? new ClothCrossings(model) : null;
        var cuts = new Dictionary<int, Dictionary<int, HashSet<ushort>>?>();
        // Each slot's body as it goes in: its own model, or one whose skin was pulled back to the author's edge.
        var bodyOf = new Dictionary<int, byte[]>();
        int cutTris = 0, keptTris = 0;
        foreach (int s in claimedBy)
        {
            int these = 0, gone = 0;
            byte[]? pulled = null;
            bool cutThis = cutHidden || alwaysCut.Contains(s);
            cuts[s] = !cutThis || drawn == null || drawn.IsEmpty ? null : CutLike(drawn, drawnEdges!, cloth!,
                                                                      swappable[s].TargetModel!, swappable[s].TargetHidden,
                                                                      out these, out gone, out pulled);
            bodyOf[s] = pulled ?? swappable[s].TargetModel!;
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
            Geometry = [new ContentGeometry(bodyOf[s], SecondSkinWriter.IsBodySkinMaterial,
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
    /// <param name="pulled">The body with its kept skin pulled back to the author's edge (see <see cref="PullToEdge"/>),
    /// or null when nothing needed pulling.</param>
    /// <param name="cloth">The garment's cloth, which decides which of the kept skin needs pulling back.</param>
    private static Dictionary<int, HashSet<ushort>>? CutLike(BodySurface drawn, SkinEdges edges, ClothCrossings cloth,
                                                             byte[] body, IReadOnlySet<string>? hidden, out int kept,
                                                             out int cut, out byte[]? pulled)
    {
        kept = cut = 0;
        pulled = null;
        if (ModelPartReader.Read(body) is not { } read) return null;
        var parts = Without(read, hidden);

        var baseOf = new Dictionary<int, int>();
        foreach (var span in parts.MeshSpans) baseOf[span.Mesh] = span.BaseVertex;

        var covered = new bool[parts.Positions.Length / 3];
        foreach (int v in parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                     .SelectMany(p => p.Triangles).Distinct())
            covered[v] = Covers(drawn, At(parts, v), edges);

        var sets = new Dictionary<int, HashSet<ushort>>();
        var drawnCorners = new HashSet<int>();
        var keptTris = new List<(int A, int B, int C)>();
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
                drawnCorners.Add(a);
                drawnCorners.Add(b);
                drawnCorners.Add(c);
                keptTris.Add((a, b, c));
            }
        }
        if (cut == 0) { kept = 0; return null; }   // nothing cut: the body goes in whole, as before
        pulled = PullToEdge(drawn, edges, cloth, body, parts, drawnCorners, keptTris);
        return sets;

        void Keep(HashSet<ushort> set, int v, int bv)
        {
            if (covered[v] && v - bv is >= 0 and <= ushort.MaxValue) set.Add((ushort)(v - bv));
        }
    }

    /// <summary>
    /// The garment's skin meshes the swap will replace, read before anything moves: <see cref="Rebuild"/>'s own rule —
    /// a mesh goes when <see cref="OnBodyShare"/> of it lies on a body being swapped in (<see cref="OnBody"/>) — asked of
    /// the bodies the skin sits on as authored, the pairs' sources, instead of their targets once it is laid there.
    /// A mesh partly off them (a heeled shoe's foot), or on a slot no pair resizes, is kept.
    /// </summary>
    internal static HashSet<int> SwappedSkinMeshes(ModelParts garment, IReadOnlyList<SlotPair> pairs)
    {
        var swapped = new HashSet<int>();
        var sources = pairs.Where(p => p.TargetModel != null)
                           .Select(p => new BodySurface(p.Correspondence.Source,
                                                        BodySurface.CellFor(MeanEdgeOf(p.Correspondence.Source))))
                           .Where(s => !s.IsEmpty)
                           .ToList();
        if (sources.Count == 0) return swapped;
        foreach (var mesh in garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                          .GroupBy(p => p.Mesh))
        {
            var verts = mesh.SelectMany(p => p.Triangles).Distinct().ToList();
            int on = verts.Count(v => sources.Any(s => OnBody(s, At(garment, v))));
            if (verts.Count > 0 && on >= verts.Count * OnBodyShare) swapped.Add(mesh.Key);
        }
        return swapped;
    }

    /// <summary>
    /// How far past the author's edge a drawn corner is pulled back from (30 mm): the cut keeps a triangle with ANY corner
    /// the author drew, so its other corners reach one body triangle past the edge.
    /// </summary>
    internal const float PullReach = 0.03f;

    /// <summary>A corner this close to the author's skin (0.5 mm) is on it, and stays.</summary>
    internal const float PullSlack = 0.0005f;

    /// <summary>
    /// The body with each corner of its kept skin that lies PAST the author's open edge, under cloth that leaves it no
    /// room, moved back toward that edge.
    /// <para/>
    /// The cut keeps a body triangle when any corner of it is one the author drew, so that no gap opens along the
    /// author's edge; its other corners then reach a whole body triangle further, plus <see cref="CutReach"/>. Under
    /// the cloth the author tucked over that edge, the extra skin bulges through: the Pioneer's Bottoms' coarse hem
    /// and stocking tops, faces 60-75 mm across tucked a few millimetres over a strip of skin, had Neolithe's buttock
    /// through the hem and the stocking corners standing out of the thigh as black spikes.
    /// <para/>
    /// Each such corner is walked out from the author's edge toward where it was, and stops short of the first step the
    /// cloth crowds (<see cref="ClothCrossings"/>); one the walk reaches stays. The walk, not the corner alone: a
    /// corner 3 mm under the cloth can still span a triangle the face sags through — Neolithe's hip at the front of
    /// the shorts. And no further under the rim than that: a fixed tuck closed the slit the buried hem leaves at the
    /// back (271 mm² to 11 at 3 mm) but put the skin in front of the rim between its corners, which stood through it
    /// as dark notches (739 / 1,964 mm² of skin over cloth, back / front, against 82 / 346 untucked). Landings inside
    /// the author's skin, or on an edge two of its triangles share, are under it and never move.
    /// <para/>
    /// The body's own open edge stays where it is: that is its seam to another slot's skin, which pulling would open.
    /// </summary>
    /// <para/>
    /// A corner pulled a whole body triangle back can cross the kept triangles beside it, which do not move: a pull that
    /// would turn one over is halved, then given up, keyed by position so a vertex split at a texture seam moves as one.
    /// </summary>
    /// <param name="corners">Every corner of the triangles the cut keeps.</param>
    /// <param name="keptTris">The triangles the cut keeps.</param>
    /// <returns>The body's bytes with those corners moved, or null when none needed it or the model cannot be written.</returns>
    private static byte[]? PullToEdge(BodySurface drawn, SkinEdges edges, ClothCrossings cloth, byte[] body,
                                      ModelParts parts, IReadOnlyCollection<int> corners,
                                      IReadOnlyList<(int A, int B, int C)> keptTris)
    {
        var bodyEdges = SkinEdges.Of(parts);
        var surface = new BodySurface(parts, BodySurface.CellFor(MeanEdgeOf(parts)));
        var edit = new PullEdit(parts.MeshSpans, parts.Positions.Length / 3);
        foreach (int v in corners)
        {
            if (bodyEdges.OnBoundary(v)) continue;
            var p = At(parts, v);
            if (!drawn.Nearest(p, PullReach, out var hit) || hit.Distance <= PullSlack || !PastEdge(hit, edges)) continue;
            // From the author's edge out toward where the corner was, as far as the cloth leaves the skin room: the
            // author's edge was laid, the cloth refitted, and the two no longer meet exactly — pulled right onto the
            // edge, a corner opened a slit under a hem that now ends above it. Each step lands on the body, which the
            // laid edge only approximately sits on.
            float reached = 1f;
            for (int step = 1; step <= PullSteps; step++)
            {
                var q = OnBodyAt(surface, Vector3.Lerp(hit.Point, p, (float)step / PullSteps), out var qn);
                if (!cloth.Crowds(q, qn)) continue;
                reached = (step - 1f) / PullSteps;
                break;
            }
            if (reached >= 1f) continue;
            var to = OnBodyAt(surface, Vector3.Lerp(hit.Point, p, reached), out _);
            var d = to - p;
            if (d.Length() <= PullSlack) continue;
            edit.Delta[v] = new Vec3(d.X, d.Y, d.Z);
        }

        // Back off pulls that turn a kept triangle over: halved on its moving corners, then given up.
        var scale = new Dictionary<Vector3, float>();
        foreach (int v in corners)
            if (edit.Delta[v].X != 0f || edit.Delta[v].Y != 0f || edit.Delta[v].Z != 0f) scale[At(parts, v)] = 1f;
        if (scale.Count == 0) return null;
        Vector3 Trial(int v)
        {
            var p = At(parts, v);
            var d = edit.Delta[v];
            return scale.TryGetValue(p, out float s) ? p + new Vector3(d.X, d.Y, d.Z) * s : p;
        }
        for (int pass = 0; ; pass++)
        {
            bool giveUp = pass >= PushUnfoldPasses;
            int turned = 0;
            foreach (var (a, b, c) in keptTris)
            {
                Vector3 pa = At(parts, a), pb = At(parts, b), pc = At(parts, c);
                bool any = scale.TryGetValue(pa, out float sa) && sa > 0f;
                any |= scale.TryGetValue(pb, out float sb) && sb > 0f;
                any |= scale.TryGetValue(pc, out float sc) && sc > 0f;
                if (!any) continue;
                var was = Vector3.Cross(pb - pa, pc - pa);
                if (was.LengthSquared() <= 1e-24f) continue;
                if (Vector3.Dot(was, Vector3.Cross(Trial(b) - Trial(a), Trial(c) - Trial(a))) > 0f) continue;
                float keep = giveUp ? 0f : 0.5f;
                foreach (var corner in new[] { pa, pb, pc })
                    if (scale.ContainsKey(corner)) scale[corner] *= keep;
                turned++;
            }
            if (turned == 0 || giveUp) break;
        }

        int moved = 0;
        foreach (int v in corners)
        {
            var p = At(parts, v);
            if (!scale.TryGetValue(p, out float s) || s <= 0f) { edit.Delta[v] = default; continue; }
            var d = edit.Delta[v];
            if (d.X == 0f && d.Y == 0f && d.Z == 0f) continue;
            edit.Delta[v] = new Vec3(d.X * s, d.Y * s, d.Z * s);
            edit.Worst = MathF.Max(edit.Worst, Len(edit.Delta[v]));
            moved++;
        }
        if (moved == 0) return null;
        try
        {
            return MeshVolumeService.Inflate(body, edit).Model;
        }
        catch (ModelAttributeWriter.ModelEditException)
        {
            return null;   // a position format the writer cannot store: the body goes in as cut, overhang and all
        }
    }

    /// <summary>How many steps the walk from the author's edge out to a corner takes (32: about half a millimetre each on
    /// the Pioneer's Bottoms' 14 mm overhang).</summary>
    private const int PullSteps = 32;

    /// <summary>The body point nearest <paramref name="p"/>, and its normal; <paramref name="p"/> itself when the body
    /// is out of reach.</summary>
    private static Vector3 OnBodyAt(BodySurface body, Vector3 p, out Vector3 normal)
    {
        if (body.Nearest(p, LayFull, out var hit))
        {
            normal = hit.Normal;
            return hit.Point;
        }
        normal = default;
        return p;
    }

    /// <summary>Whether a landing on the author's skin is on its open edge — an edge only one of its triangles uses,
    /// or a corner on one — rather than inside it.</summary>
    private static bool PastEdge(BodySurface.Hit hit, SkinEdges edges)
    {
        const float inside = 0.01f;   // a landing clamped onto an edge has a zero weight; this is off it
        bool offA = hit.U < inside, offB = hit.V < inside, offC = hit.W < inside;
        return (offA, offB, offC) switch
        {
            (false, false, false) => false,
            (true, false, false) => !edges.IsShared(hit.B, hit.C),
            (false, true, false) => !edges.IsShared(hit.C, hit.A),
            (false, false, true) => !edges.IsShared(hit.A, hit.B),
            (false, true, true) => edges.OnBoundary(hit.A),
            (true, false, true) => edges.OnBoundary(hit.B),
            (true, true, false) => edges.OnBoundary(hit.C),
            _ => false,
        };
    }

    /// <summary>
    /// How far over the skin the garment's cloth must stand for the skin under it to stay (2 mm). Closer than that, the
    /// skin past the author's edge is pulled back: it shows through the cloth's flat faces between their corners.
    /// </summary>
    internal const float PullClear = 0.002f;

    /// <summary>
    /// <see cref="PullClear"/> under a rim's faces (0.25 mm): under <see cref="RimClear"/>, so a rim
    /// <see cref="LiftRims"/> has just lifted clear still has skin under it. At 2 mm the skin stopped short of every
    /// lifted hem and left the slit the lift was there to close.
    /// </summary>
    internal const float PullClearRim = 0.00025f;

    /// <summary>How far BEHIND the skin cloth still counts as crowding it (10 mm): the skin is already through it.</summary>
    internal const float PullBehind = 0.01f;

    /// <summary>How far in front of the skin the cloth is looked for (15 mm).</summary>
    private const float PullAhead = 0.015f;

    /// <summary>
    /// The garment's cloth, for asking whether it crosses the line through a skin point along the skin's normal — and
    /// where. Skin past the author's edge with no cloth over it is in the open, filling the gap between the author's edge
    /// and the cloth, and stays; skin with cloth well over it is hidden and stays; skin the cloth sits right on, or is
    /// already behind, is what shows through, and is pulled back.
    /// </summary>
    private sealed class ClothCrossings
    {
        private const float Cell = 0.02f;
        private readonly List<(Vector3 A, Vector3 B, Vector3 C)> tris = [];
        private readonly List<float> clear = [];
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        public ClothCrossings(ModelParts garment)
        {
            int vc = garment.Positions.Length / 3;
            var vertAt = new Vec3[vc];
            for (int i = 0; i < vc; i++)
                vertAt[i] = new Vec3(garment.Positions[i * 3], garment.Positions[i * 3 + 1], garment.Positions[i * 3 + 2]);
            var nodeOf = MeshMath.WeldByPosition(vertAt, out _);

            // A part switched by an IMC variant tag may not be drawn — which of them is, is the wearer's setting, and
            // nothing here knows it. Counted, a hidden long skirt would pull the thigh's skin back from under nothing.
            uint variantBits = 0;
            for (int i = 0; i < garment.AttributeNames.Count && i < 32; i++)
                if (SecondSkinWriter.IsVariantAttribute(garment.AttributeNames[i])) variantBits |= 1u << i;

            var faces = new List<(int A, int B, int C)>();
            foreach (var part in garment.Parts)
            {
                if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)
                    || IsBodyExtraMaterial(part.Material) || (part.AttributeMask & variantBits) != 0) continue;
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    tris.Add((At(garment, a), At(garment, b), At(garment, c)));
                    faces.Add((nodeOf[a], nodeOf[b], nodeOf[c]));
                }
            }

            // A rim's faces have just been lifted off the skin by RimClear (see LiftRims), and the skin is meant to run
            // on under them: there the cloth crowds only within PullClearRim. Everywhere else, PullClear.
            var rims = RimFaces(faces, n => ToVector(vertAt[n]));
            for (int index = 0; index < tris.Count; index++)
            {
                clear.Add(rims.Contains(index) ? PullClearRim : PullClear);
                var tri = tris[index];
                var (x0, y0, z0) = CellOf(Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C)));
                var (x1, y1, z1) = CellOf(Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C)));
                for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!grid.TryGetValue((x, y, z), out var bucket)) grid[(x, y, z)] = bucket = [];
                    bucket.Add(index);
                }
            }
        }

        private static (int, int, int) CellOf(Vector3 p)
            => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        /// <summary>Whether cloth crosses the line through <paramref name="p"/> along <paramref name="normal"/> between
        /// <see cref="PullBehind"/> behind it and <see cref="PullClear"/> in front (<see cref="PullClearRim"/> for a rim's
        /// faces).</summary>
        public bool Crowds(Vector3 p, Vector3 normal) => Deepest(p, normal) != null;

        /// <summary>
        /// Of the cloth crowding <paramref name="p"/> (see <see cref="Crowds"/>), how far in front of it the deepest lies
        /// — negative behind it; null when none does.
        /// </summary>
        public float? Deepest(Vector3 p, Vector3 normal)
        {
            if (normal.LengthSquared() < 1e-12f) return null;
            var d = Vector3.Normalize(normal);
            var from = p - d * PullBehind;
            var to = p + d * PullAhead;
            var (x0, y0, z0) = CellOf(Vector3.Min(from, to));
            var (x1, y1, z1) = CellOf(Vector3.Max(from, to));
            var seen = new HashSet<int>();
            float? deepest = null;
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int i in bucket)
                {
                    if (!seen.Add(i)) continue;
                    var (a, b, c) = tris[i];
                    if (!LineHits(from, d, a, b, c, out float t)) continue;
                    float ahead = t - PullBehind;
                    if (ahead <= clear[i] && (deepest == null || ahead < deepest)) deepest = ahead;
                }
            }
            return deepest;
        }

        /// <summary>Möller–Trumbore, both faces, forward of <paramref name="o"/> only.</summary>
        private static bool LineHits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t >= 0f;
        }
    }

    /// <summary>The pull as an edit <see cref="MeshVolumeService.Inflate"/> can write. Normals are left as the body's.</summary>
    private sealed class PullEdit(IReadOnlyList<MeshSpan> spans, int count) : IMeshEdit
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
    /// How squarely off the body's surface a point must sit, beyond <see cref="SwapOnBody"/>, to count as on it: the
    /// cosine between its offset and the body's normal at the landing (0.8, about 37°).
    /// </summary>
    internal const float OverBody = 0.8f;

    /// <summary>
    /// Whether a garment skin point counts as lying on this body, for <see cref="Rebuild"/>'s test that a whole skin
    /// mesh is the body's: within <see cref="SwapOnBody"/>, or within <see cref="LayFull"/> straight OVER the body —
    /// its offset along the body's normal (see <see cref="OverBody"/>). Laying can leave a point millimetres off: the
    /// Pioneer's Bottoms' corners missed Rue+'s hip by 6 mm, above it. A point PAST the body's edge is off along the
    /// surface instead: a long top's narrow hip strip, a few millimetres below where the chest body stops, is the legs'
    /// skin, not the chest's, and counted on the chest it was swapped for skin that has nothing below the waist.
    /// </summary>
    internal static bool OnBody(BodySurface body, Vector3 p)
    {
        if (!body.Nearest(p, LayFull, out var hit)) return false;
        if (hit.Distance <= SwapOnBody) return true;
        return MathF.Abs(Vector3.Dot((p - hit.Point) / hit.Distance, hit.Normal)) >= OverBody;
    }

    /// <summary>
    /// The edges of a model's skin that two of its triangles share, with its vertices welded by position: a body mesh
    /// splits vertices at uv seams, and an edge along a seam is still inside the skin.
    /// </summary>
    internal sealed class SkinEdges
    {
        private readonly int[] nodeOf;
        private readonly HashSet<(int, int)> shared = [];
        private readonly HashSet<int> boundary = [];

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
            foreach (var (a, b) in seen)
                if (!shared.Contains((a, b)))
                {
                    boundary.Add(a);
                    boundary.Add(b);
                }
        }

        internal static SkinEdges Of(ModelParts m) => new(m);

        /// <summary>Whether the edge between these two vertices of the model is used by two triangles or more.</summary>
        internal bool IsShared(int a, int b) => shared.Contains(Key(a, b));

        /// <summary>Whether this vertex is on the skin's open edge — an end of an edge only one triangle uses.</summary>
        internal bool OnBoundary(int v) => v >= 0 && v < nodeOf.Length && boundary.Contains(nodeOf[v]);

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
