using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// The most influences a vertex may have. The game's blend elements hold four or, since Dawntrail, eight; nothing
    /// ever writes more.
    /// </summary>
    internal const int MaxInfluences = 8;

    /// <summary>
    /// How near a body has to be under a cloth vertex for its weights to be rewritten (4 cm). Cloth sits millimetres
    /// off the skin, and a boot shaft or a stiff cuff a centimetre or two; past that there is no body under the vertex
    /// to speak of and its weights are left as the author made them.
    /// <para/>
    /// This used to be 50 cm, for "the far end of a long cape". A garment covers more of the body than the slots being
    /// refitted: a thigh-high stocking refitted on the FEET slot alone found the foot half a metre below the thigh and
    /// rigged the whole stocking to the ankle. Cloth out of reach of the bodies in play must keep its weights.
    /// </summary>
    internal const float WeightReach = 0.04f;

    /// <summary>
    /// How near both bodies must be for a cloth vertex to take the change between them in full (2 cm). From here out to
    /// <see cref="WeightReach"/> the change fades to nothing, so the author's weights take over gradually rather than at
    /// a line.
    /// <para/>
    /// A line is what two layers of cloth a millimetre apart can fall either side of, and each then follows different
    /// bones. Measured on the game's Oversized Plain Neotunic refitted onto Neolithe: the old body (the game's own) has
    /// no skin under the breasts — that is smallclothes — so its nearest skin is 4 cm off, right at the reach. The shirt
    /// found it at 39.8 mm and took +0.42 of the breast bone; the printed panel over it, 0.5 mm further out, missed it
    /// and kept the author's 0.48. Posed, the shirt came through the print as a blue patch.
    /// </summary>
    internal const float WeightFull = 0.02f;

    /// <summary>
    /// How near the garment's own skin must have been, as authored, for a cloth vertex to take the change against IT
    /// rather than against the old body mod (5 mm). Cloth lying on its skin was weighted off that skin; further out, in
    /// the crease of an armhole, the garment's skin and the new body can answer from opposite sides of the crease, and
    /// the change between two different places is noise — measured on "pop" at 2 cm: 50 armhole vertices over a tenth
    /// apart from the skin under them, against 16 measured against the old body.
    /// </summary>
    internal const float OwnSkinReachDefault = 0.005f;

    /// <summary>
    /// How near its own skin cloth must have been for "rigged like the skin under it" to be read off it (2 cm).
    /// Measured posed, on "pop" standing as the character stood in game: skin through the upper back 1,356 px with the
    /// change taken against the old body, 577 against the garment's own skin within 5 mm, 93 copying within 2 cm — the
    /// unrefitted top on its own skin draws 40. 1 cm left 123; 4 cm was no better than 2.
    /// </summary>
    internal const float CopyReachDefault = 0.02f;

    /// <summary>How alike (summed difference, 0.2 = 10% of the weight) cloth's body weights and its own skin's under
    /// it must be for the author to have copied them. 0.4 drew a little less skin through (71 px against 93) but takes
    /// cloth rigged a fifth apart from its skin for a copy; kept at the stricter.</summary>
    internal const float CopyToleranceDefault = 0.2f;

    /// <summary>
    /// New skinning for a garment's cloth, from the body it is being refitted onto.
    /// </summary>
    /// <param name="PerMesh">By the garment's mesh index: per vertex of that mesh, the influences it takes, or null to
    /// leave it. Null for a mesh with nothing to change.</param>
    /// <param name="Reweighted">Cloth vertices given new body weights.</param>
    /// <param name="Trimmed">Vertices whose body weights were cut to fit the eight-influence limit.</param>
    /// <param name="BodyBones">Every bone either body rigs — what "body bone" means for this refit.</param>
    /// <param name="Donors">The target body models, whose bones the rebuilt model must be able to name.</param>
    internal sealed record WeightPlan(
        IReadOnlyDictionary<int, (string Bone, float W)[]?[]> PerMesh, int Reweighted, int Trimmed,
        IReadOnlySet<string> BodyBones, IReadOnlyList<byte[]> Donors)
    {
        public (string Bone, float W)[]?[]? For(int mesh) => PerMesh.TryGetValue(mesh, out var w) ? w : null;
    }

    /// <summary>
    /// Rewrite the body part of the garment's cloth skinning from the target bodies, or null when there is nothing to
    /// rewrite.
    /// <para/>
    /// Between two body MODS, always: even when both rig the same bones (YAB's and Rue's plain sizes do), each body
    /// weights them to its own shape, and the cloth has to follow the body it is now on. Within one body mod, only when
    /// the two sizes are rigged differently (Rue's plain and Yiggle): two sizes on one rig share it, and there the
    /// author's weights are right as they stand — phase 1's refit keeps them untouched.
    /// <para/>
    /// A BODY bone is one either body rigs; every other bone — a skirt chain, a cape, hair — is the garment's own and its
    /// weights are left exactly as they are. Per cloth vertex the non-body influences are kept first.
    /// <para/>
    /// The body share takes the CHANGE between the two bodies, not the new body's weights outright: the old body's
    /// weights where the vertex was, the new body's where it now is, and their difference added to the author's. Where
    /// the bodies agree — TBSE and TBSE-X on every bone they share — the author's weighting stands exactly, which
    /// matters for a loose garment weighted by hand rather than copied off the skin. Where the new body moves weight to
    /// a bone of its own (TBSE-X's pecs, Rue's <c>iv_c_mune</c> in place of <c>j_mune</c>) the cloth takes the same
    /// move. A weight the change would take below zero stops at zero, and the body share is scaled back to what it was.
    /// Without the old body under the vertex, the new body's weights are taken as they are.
    /// </summary>
    /// <param name="garment">The refitted garment, positions already on the new body.</param>
    /// <param name="pairs">The slots being refitted; each must carry both its source and target body's files.</param>
    /// <param name="acrossBodies">The source and target are different body mods: rewrite whatever the rigs.</param>
    /// <param name="before">The garment as authored, in the same vertex order (the refit is in place): where each
    /// vertex sat on the OLD body. Null reads the old body's weights at the refitted positions instead.</param>
    /// <param name="held">Vertices of parts the user unticked. Held means the author's work stands: a part held in
    /// place keeps the bones it follows too, or it would sit still in the bind pose and fly apart in a posed one —
    /// which is what a rigid heel did when it was held but reweighted.</param>
    internal static WeightPlan? PlanWeights(byte[] garment, IReadOnlyList<SlotPair> pairs, bool acrossBodies = false,
                                            byte[]? before = null, IReadOnlySet<int>? held = null)
    {
        if (pairs.Count == 0 || pairs.Any(p => p.SourceModel == null || p.TargetModel == null)) return null;

        var sourceBones = new HashSet<string>(StringComparer.Ordinal);
        var targetBones = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)>();
        // Per target, in step with it: within one body mod, the slot's two sizes share a rig — see RigKept.
        var rigKept = new List<bool>();
        // The old bodies, for the change between the two. Any one unreadable and the change cannot be taken anywhere:
        // the new body's weights are then used outright, as before the change was.
        List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)>? sources = [];
        foreach (var pair in pairs)
        {
            var pairSource = SecondSkinWriter.Parse(pair.SourceModel!).BoneNames;
            var pairTarget = SecondSkinWriter.Parse(pair.TargetModel!).BoneNames;
            sourceBones.UnionWith(pairSource);
            targetBones.UnionWith(pairTarget);
            if (ModelSkinReader.Read(pair.TargetModel!, null, null) is not { } skin) return null;
            if (skin.VertexCount * 3 != pair.Target.Positions.Length) return null;   // not the part reader's order
            targets.Add((new BodySurface(pair.Target, BodySurface.CellFor(MeanEdgeOf(pair.Target))), skin));
            rigKept.Add(!acrossBodies && new HashSet<string>(pairSource, StringComparer.Ordinal).SetEquals(pairTarget));

            // The correspondence's own reading of the old body: without the variants its mod does not draw.
            if (sources != null
                && pair.Correspondence.Source is { } sourceParts
                && ModelSkinReader.Read(pair.SourceModel!, null, null) is { } sourceSkin
                && sourceSkin.VertexCount * 3 == sourceParts.Positions.Length)
                sources.Add((new BodySurface(sourceParts, BodySurface.CellFor(MeanEdgeOf(sourceParts))), sourceSkin));
            else
                sources = null;
        }
        if (!acrossBodies && sourceBones.SetEquals(targetBones)) return null;

        var bodyBones = new HashSet<string>(sourceBones, StringComparer.Ordinal);
        bodyBones.UnionWith(targetBones);

        if (ModelPartReader.Read(garment) is not { } model) return null;
        if (ModelSkinReader.Read(garment, null, null) is not { } own || own.VertexCount * 3 != model.Positions.Length)
            return null;
        // Where each vertex sat on the old body: the authored garment, when it lines up vertex for vertex.
        var authored = before != null ? ModelPartReader.Read(before) : null;
        bool linesUp = authored != null && authored.Positions.Length == model.Positions.Length;
        float[] wasAt = linesUp ? authored!.Positions : model.Positions;

        // The garment's OWN skin as authored, which is the old body its cloth was actually weighted against. An author
        // who reshapes the skin under a garment reweights it too, and the cloth follows that skin rather than the body
        // mod's: "pop" matches its own skin's weights everywhere on the back (0% apart) and YAB's only to within 12%.
        // Measured against YAB, the change handed the cloth those 12% back on top of Neolithe's weights while the skin
        // beside it took Neolithe's outright, and the two parted in a pose — skin through the back of the shirt.
        List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)>? ownSkin = null;
        var tuned = Tuned;
        if (linesUp && sources != null && !tuned.NoOwnSkinWeights && ModelSkinReader.Read(before!, null, null) is { } authoredSkin
            && authoredSkin.VertexCount * 3 == authored!.Positions.Length)
        {
            var surface = new BodySurface(authored, BodySurface.CellFor(MeanEdgeOf(authored)));
            if (!surface.IsEmpty) ownSkin = [(surface, authoredSkin)];
        }

        int vc = model.Positions.Length / 3;
        var result = new (string Bone, float W)[]?[vc];
        int reweighted = 0, trimmed = 0;
        held = RigKept(model, targets, rigKept, held);

        foreach (int v in ClothVertices(model))
        {
            if (held != null && held.Contains(v)) continue;
            var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
            var mine = Influences(own, v);

            // All the garment's own bones: untouched, and no body lookup needed.
            if (mine.Where(i => !bodyBones.Contains(i.Bone)).Sum(i => i.W) >= 0.999f) continue;
            if (Nearest(targets, p, out float far) is not { } body || body.Length == 0) continue;
            var was = new Vector3(wasAt[v * 3], wasAt[v * 3 + 1], wasAt[v * 3 + 2]);
            if (sources != null)
            {
                // Both bodies have to be under the vertex for the change between them to mean anything. With only the
                // old one missing there is nothing to compare against, and taking the new body's weights outright
                // would re-rig cloth that never sat on it.
                float ownFar = float.MaxValue;
                var fromOwn = ownSkin != null ? Nearest(ownSkin, was, out ownFar) : null;
                if (fromOwn is { Length: > 0 } && ownFar <= tuned.CopyReach
                    && Difference([.. mine.Where(i => bodyBones.Contains(i.Bone))], [.. fromOwn]) <= tuned.CopyTolerance)
                {
                    // Rigged as its own skin under it: rigged as the new skin under it, outright. Skin and cloth that
                    // moved as one on the author's body move as one on the new body; the change between two lookups
                    // can land either side of a crease and leave them apart. Faded by the FARTHER of the two, as the
                    // change is: the new body has to be under the cloth too, or a stocking refitted on the feet slot
                    // alone would take the ankle's weights half a metre up the thigh (see WeightReach).
                    far = MathF.Max(far, ownFar);
                }
                else
                {
                    // Faded by the distance to whichever old surface the weights were read from.
                    float oldFar;
                    (string Bone, float W)[]? oldBody;
                    if (fromOwn is { Length: > 0 } && ownFar <= tuned.OwnSkinReach)
                    {
                        oldBody = fromOwn;
                        oldFar = ownFar;
                    }
                    else
                    {
                        oldBody = Nearest(sources, was, out oldFar);
                    }
                    if (oldBody is not { Length: > 0 }) continue;
                    far = MathF.Max(far, oldFar);
                    if (Change(mine, oldBody, body, bodyBones) is { Count: > 0 } changed) body = [.. changed];
                }
            }

            if (Combine(mine, body, bodyBones, out bool cut) is not { } combined) continue;
            // Faded toward the author's weights as the bodies get far — see WeightFull.
            float fade = Fade(far);
            if (fade <= 0f) continue;
            if (fade < 1f) combined = MeshMath.BlendWeights([.. mine], 1f - fade, combined, fade, [], 0f, MaxInfluences);
            result[v] = combined;
            reweighted++;
            if (cut) trimmed++;
        }

        // Smoothed before the layers are paired, so partners still come out identical.
        if (acrossBodies && !tuned.NoWeightSmooth) SmoothWeights(model, own, result, bodyBones, held);

        // After the smoothing, which would otherwise take cloth lying on the skin off the skin's weights again.
        if (!tuned.NoSkinHug)
        {
            FollowBodyNearSkin(model, own, targets, result, held);
            DropOwnBonesNearBody(model, own, targets, result, bodyBones, held);
        }

        reweighted += KeepLayersTogether(model, own, wasAt, result, held);

        // The garment's own body mesh. It IS the body, so it takes the new body's weights OUTRIGHT rather than the
        // change between the two: the change exists for cloth an author weighted by hand, and a garment's body mesh
        // was copied off a body to begin with.
        //
        // Without this the refit moves the cloth to the new body's weighting and leaves the skin beside it — often
        // sharing its vertices — still rigged to the old body, and two surfaces a millimetre apart weighted to two
        // different bodies come apart as soon as a bust bone turns. Measured on a sheer corset refitted Bibo+ to
        // Neolithe with the skin kept: cloth and skin 0.044 apart on average where the author's own file is 0.019,
        // and 7% of the cup more than a tenth apart. That is the reported "the weights of the top don't match the
        // skin", and no amount of moving the geometry answers it.
        //
        // Wasted when the skin is about to be swapped for the body's own mesh, which already carries these weights —
        // but harmless, and whether the caller will swap is not something this can see.
        foreach (int v in SkinVertices(model))
        {
            if (held != null && held.Contains(v)) continue;
            var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
            if (Nearest(targets, p, out _) is not { } body || body.Length == 0) continue;
            if (Combine(Influences(own, v), body, bodyBones, out bool cut) is not { } combined) continue;
            result[v] = combined;
            reweighted++;
            if (cut) trimmed++;
        }

        // Per mesh, in each mesh's own vertex numbering, which is what the writer walks.
        var perMesh = new Dictionary<int, (string Bone, float W)[]?[]>();
        foreach (var span in model.MeshSpans)
        {
            var local = new (string Bone, float W)[]?[span.Count];
            bool any = false;
            for (int i = 0; i < span.Count; i++)
            {
                local[i] = result[span.BaseVertex + i];
                any |= local[i] != null;
            }
            if (any) perMesh[span.Mesh] = local;
        }

        return new WeightPlan(perMesh, reweighted, trimmed, bodyBones, pairs.Select(p => p.TargetModel!).ToList());
    }

    /// <summary>
    /// <paramref name="held"/>, plus every vertex whose nearest new body is a slot that kept its rig: within one body
    /// mod, the rule that two sizes on one rig leave the author's weights standing holds slot by slot, not only for the
    /// refit as a whole.
    /// <para/>
    /// One slot whose rig changes used to reweight the whole garment. "Queen Marika" refitted on Rue+ from Yiggle
    /// Medium to Large, its hands from Short Nails to Yiggle Stabbies: the Yiggle hands add eight finger bones, so every
    /// cloth vertex of the top took a fresh per-vertex reading of the bodies — and, the refit being within one body mod,
    /// none of the smoothing a refit across bodies gets. Under the bust, where the cloth bridges the crease, neighbours
    /// read either side of it: 38 edges jumping more than 0.15 of <c>iv_c_mune_l</c> where the author had none, 82 of
    /// <c>j_sebo_a</c> where the author had 10, posed as a ragged band under the breasts. The chest's two sizes share
    /// one rig, and there the author's weights were right as they stood.
    /// </summary>
    /// <returns><paramref name="held"/> itself when no slot kept its rig.</returns>
    private static IReadOnlySet<int>? RigKept(ModelParts model, List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)> targets,
                                             List<bool> rigKept, IReadOnlySet<int>? held)
    {
        if (!rigKept.Contains(true)) return held;
        var kept = held != null ? new HashSet<int>(held) : [];
        int vc = model.Positions.Length / 3;
        for (int v = 0; v < vc; v++)
        {
            var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
            float reach = WeightReach;
            int on = -1;
            for (int t = 0; t < targets.Count; t++)
            {
                if (!targets[t].Surface.Nearest(p, reach, out var hit)) continue;
                reach = hit.Distance;
                on = t;
            }
            if (on >= 0 && rigKept[on]) kept.Add(v);
        }
        return kept;
    }

    /// <summary>
    /// One vertex's new influences: its non-body ones kept first and exactly, then the body's, in as many of the eight
    /// slots as are left, scaled to the share of the vertex the body had. Null when the vertex has no body share.
    /// </summary>
    /// <param name="mine">The vertex's influences as authored.</param>
    /// <param name="body">The new body's influences at the vertex's place.</param>
    /// <param name="trimmed">Body influences had to be left out to stay within eight.</param>
    internal static (string Bone, float W)[]? Combine(IReadOnlyList<(string Bone, float W)> mine,
                                                      IReadOnlyList<(string Bone, float W)> body,
                                                      IReadOnlySet<string> bodyBones, out bool trimmed)
    {
        trimmed = false;
        var keep = mine.Where(i => !bodyBones.Contains(i.Bone)).OrderByDescending(i => i.W).ToList();
        float kept = keep.Sum(i => i.W);
        if (kept >= 0.999f || body.Count == 0) return null;

        if (keep.Count >= MaxInfluences)
        {
            // No slot left for the body: the kept influences take the whole vertex rather than leave it short.
            trimmed = true;
            return [.. Normalised(keep.Take(MaxInfluences).ToList(), 1f)];
        }

        int slots = MaxInfluences - keep.Count;
        var share = body.OrderByDescending(i => i.W).ToList();
        if (share.Count > slots)
        {
            trimmed = true;
            share = share.Take(slots).ToList();
        }
        return [.. keep, .. Normalised(share, 1f - kept)];
    }

    /// <summary>
    /// The body share of a vertex once the change between the bodies is applied: the author's body influences, plus the
    /// new body's weights, less the old body's, both scaled to the share of the vertex the body has. A bone the change
    /// takes below zero drops out. Empty when nothing is left, which the caller answers with the new body outright.
    /// </summary>
    /// <param name="mine">The vertex's influences as authored.</param>
    /// <param name="oldBody">The old body's weights where the vertex was.</param>
    /// <param name="newBody">The new body's weights where the vertex now is.</param>
    internal static List<(string Bone, float W)> Change(IReadOnlyList<(string Bone, float W)> mine,
                                                         IReadOnlyList<(string Bone, float W)> oldBody,
                                                         IReadOnlyList<(string Bone, float W)> newBody,
                                                         IReadOnlySet<string> bodyBones)
    {
        float share = mine.Where(i => bodyBones.Contains(i.Bone)).Sum(i => i.W);
        var w = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (bone, weight) in mine)
            if (bodyBones.Contains(bone)) w[bone] = w.GetValueOrDefault(bone) + weight;
        foreach (var (bone, weight) in newBody) w[bone] = w.GetValueOrDefault(bone) + share * weight;
        foreach (var (bone, weight) in oldBody) w[bone] = w.GetValueOrDefault(bone) - share * weight;
        return w.Where(p => p.Value > 1e-4f).Select(p => (p.Key, p.Value)).ToList();
    }

    /// <summary>Vertices of the garment's own body mesh — every whole submesh drawn with a skin material.</summary>
    private static List<int> SkinVertices(ModelParts model)
    {
        int vc = model.Positions.Length / 3;
        var seen = new bool[vc];
        var list = new List<int>();
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
                if (v >= 0 && v < vc && !seen[v]) { seen[v] = true; list.Add(v); }
        }
        return list;
    }

    /// <summary>Vertices of the garment's cloth — every whole submesh not drawn with a skin material.</summary>
    private static List<int> ClothVertices(ModelParts model)
    {
        int vc = model.Positions.Length / 3;
        var seen = new bool[vc];
        var list = new List<int>();
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles)
                if (v >= 0 && v < vc && !seen[v]) { seen[v] = true; list.Add(v); }
        }
        return list;
    }

    /// <summary>One vertex's influences, by bone name.</summary>
    private static List<(string Bone, float W)> Influences(XivLiveMesh.SkinnedMesh skin, int v)
    {
        var list = new List<(string, float)>(XivLiveMesh.SkinnedMesh.MaxInfluences);
        for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
        {
            float w = skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
            if (w <= 0f) continue;
            int bone = skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
            if (bone < skin.BoneNames.Length) list.Add((skin.BoneNames[bone], w));
        }
        return list;
    }

    /// <summary>
    /// The target body's weights at the point of its skin nearest <paramref name="p"/>, across every target slot — the
    /// three corners' influences blended by where the point sits in their triangle. Null when no skin is in reach.
    /// </summary>
    private static (string Bone, float W)[]? Nearest(List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)> targets,
                                                    Vector3 p, out float distance)
    {
        BodySurface.Hit? best = null;
        XivLiveMesh.SkinnedMesh? on = null;
        float reach = WeightReach;
        distance = WeightReach;
        foreach (var (surface, skin) in targets)
        {
            if (!surface.Nearest(p, reach, out var hit)) continue;
            best = hit;
            on = skin;
            reach = hit.Distance;
        }
        if (best is not { } h || on == null) return null;
        distance = h.Distance;

        return MeshMath.BlendWeights([.. Influences(on, h.A)], h.U, [.. Influences(on, h.B)], h.V,
                                     [.. Influences(on, h.C)], h.W, MaxInfluences);
    }

    /// <summary>
    /// How near two cloth vertices of different meshes must have sat, as authored, to count as one place in two layers
    /// (5 mm) — see <see cref="KeepLayersTogether"/>.
    /// </summary>
    internal const float LayerReach = 0.005f;

    /// <summary>How alike their authored weights must be (summed difference 0.02) for the author to have meant them to
    /// move as one.</summary>
    internal const float LayerSameWeights = 0.02f;

    /// <summary>
    /// Give cloth layers the author rigged to move as one the same new weights: every cloth vertex takes the average of
    /// its own new weights and those of its partners — vertices of OTHER meshes within <see cref="LayerReach"/> of it as
    /// authored, weighted the same as it (<see cref="LayerSameWeights"/>).
    /// <para/>
    /// Each vertex looks the bodies up for itself, and two layers a millimetre apart can land either side of anything
    /// sharp in that lookup: the midline between two breast bones, the edge of the old body's skin. The game's
    /// Oversized Plain Neotunic draws its print on a panel over the shirt, every panel vertex weighted exactly as the
    /// shirt beneath it; refitted onto Neolithe, a midline pair came out right breast on one layer and left on the
    /// other, and the breasts move under physics. Identical weights on layered cloth is the author saying "these move
    /// together", and a refit has to keep that.
    /// <para/>
    /// Other meshes only: within one mesh, vertices under 5 mm apart are neighbours along the same surface, and
    /// averaging them would smear the gradient the lookup gives it.
    /// </summary>
    /// <param name="result">New weights per vertex, null where the author's stand. Updated in place.</param>
    /// <returns>Vertices given weights that had none before — kept by the lookup, moved to match a partner.</returns>
    private static int KeepLayersTogether(ModelParts model, XivLiveMesh.SkinnedMesh own, float[] wasAt,
                                          (string Bone, float W)[]?[] result, IReadOnlySet<int>? held)
    {
        int vc = model.Positions.Length / 3;
        var meshOf = new int[vc];
        Array.Fill(meshOf, -1);
        foreach (var span in model.MeshSpans)
            for (int i = 0; i < span.Count && span.BaseVertex + i < vc; i++) meshOf[span.BaseVertex + i] = span.Mesh;

        var cloth = ClothVertices(model);
        Vector3 At(int v) => new(wasAt[v * 3], wasAt[v * 3 + 1], wasAt[v * 3 + 2]);
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / LayerReach), (int)MathF.Floor(p.Y / LayerReach),
                                            (int)MathF.Floor(p.Z / LayerReach));
        var grid = new Dictionary<(int, int, int), List<int>>();
        foreach (int v in cloth)
        {
            var c = Cell(At(v));
            if (!grid.TryGetValue(c, out var bucket)) grid[c] = bucket = [];
            bucket.Add(v);
        }

        // Read from a snapshot, so the order vertices are visited in cannot change the answer.
        var before = ((string Bone, float W)[]?[])result.Clone();
        var authored = new Dictionary<int, List<(string Bone, float W)>>();
        List<(string Bone, float W)> Authored(int v)
            => authored.TryGetValue(v, out var a) ? a : authored[v] = Influences(own, v);
        IEnumerable<(string Bone, float W)> Now(int v) => before[v] ?? (IEnumerable<(string Bone, float W)>)Authored(v);

        int added = 0;
        foreach (int v in cloth)
        {
            if (held != null && held.Contains(v)) continue;
            var p = At(v);
            var (cx, cy, cz) = Cell(p);
            var group = new List<int> { v };
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int z = cz - 1; z <= cz + 1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int u in bucket)
                    if (meshOf[u] != meshOf[v] && Vector3.DistanceSquared(p, At(u)) <= LayerReach * LayerReach
                        && Difference(Authored(v), Authored(u)) <= LayerSameWeights)
                        group.Add(u);
            }
            if (group.Count == 1 || group.All(u => before[u] == null)) continue;

            // Each LAYER counts once, however many of its vertices share the spot: a seam or a fan's hub puts several
            // there, and counting vertices would lean the answer toward whichever layer has more — differently seen
            // from each side, so the two would still disagree.
            var sum = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var layer in group.GroupBy(u => meshOf[u]))
            {
                float share = 1f / layer.Count();
                foreach (int u in layer)
                    foreach (var (bone, w) in Now(u))
                        sum[bone] = sum.GetValueOrDefault(bone) + w * share;
            }
            var averaged = Normalised(sum.OrderByDescending(kv => kv.Value).Take(MaxInfluences)
                                         .Select(kv => (kv.Key, kv.Value)).ToList(), 1f);
            if (before[v] == null) added++;
            result[v] = [.. averaged];
        }
        return added;
    }

    /// <summary>Cloth this close to the new body follows the body alone (3 mm) — see <see cref="FollowBodyNearSkin"/>.</summary>
    internal const float SkinHugFull = 0.003f;

    /// <summary>Cloth this far off the new body keeps all of its own bones (6 mm); between the two they fade back in.</summary>
    internal const float SkinHugFade = 0.006f;

    /// <summary>
    /// Cloth lying on the body moves exactly as the body does: within <see cref="SkinHugFull"/> of the new body a cloth
    /// vertex takes the skin's weights under it outright, fading back to its own by <see cref="SkinHugFade"/>.
    /// <para/>
    /// Rigged the same, cloth and skin bend together and keep the gap between them in any pose; rigged even a little
    /// apart, they part as soon as a joint turns, and on cloth lying on the skin that is skin through it. Two ways it
    /// came apart on "Sirius" refitted onto YAB+ Large, the hip in game: the author weights a skirt's chain into the
    /// cloth above it so the skirt hangs from the waist — 7-14% of the side and front skirt bones (<c>j_sk_s_a_l</c>,
    /// <c>j_sk_f_a_l</c>) on cloth within 3 mm of the hip, which has none, and physics swung that strip off it; and with
    /// the chain gone the cloth still bent apart from the thigh when the leg did, its weights a per-vertex reading of
    /// the bodies (and smoothed after it) rather than the skin's own. The skirt further out still swings.
    /// </summary>
    /// <param name="result">New weights per vertex, null where the author's stand. Updated in place.</param>
    private static void FollowBodyNearSkin(ModelParts model, XivLiveMesh.SkinnedMesh own,
                                           List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)> targets,
                                           (string Bone, float W)[]?[] result, IReadOnlySet<int>? held)
    {
        foreach (int v in ClothVertices(model))
        {
            if (held != null && held.Contains(v)) continue;
            var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
            if (Nearest(targets, p, out float far) is not { Length: > 0 } body || far >= SkinHugFade) continue;

            // How much of the cloth's own weighting stays: none on the body, all of it by the fade.
            float keep = far <= SkinHugFull ? 0f : (far - SkinHugFull) / (SkinHugFade - SkinHugFull);
            if (keep <= 0f)
            {
                result[v] = [.. body];
                continue;
            }
            var now = result[v] is { } w ? w : [.. Influences(own, v)];
            result[v] = MeshMath.BlendWeights(now, keep, body, 1f - keep, [], 0f, MaxInfluences);
        }
    }

    /// <summary>Cloth this close to the new body carries none of the garment's own bones (4 mm) — see
    /// <see cref="DropOwnBonesNearBody"/>.</summary>
    internal const float OwnBoneFull = 0.004f;

    /// <summary>Cloth this far off keeps all of the garment's own bones (8 mm); between, they fade back in.</summary>
    internal const float OwnBoneFade = 0.008f;

    /// <summary>
    /// Keep the garment's own bones — a skirt's chain, a cape — off the cloth near the body: within
    /// <see cref="OwnBoneFull"/> of the new body their share goes to the body bones the vertex already has, fading back to
    /// the author's split by <see cref="OwnBoneFade"/>. <see cref="FollowBodyNearSkin"/> already rigs the cloth lying ON
    /// the skin as the skin; this is the cloth a few millimetres off it, which still swings with the chain.
    /// <para/>
    /// "Sirius" on Rue+ Large still showed the hip through the cloth after the skin-following fix, where YAB+ Large —
    /// the same cloth in the same place — did not: in the fade beyond 3 mm, Rue's reading of the bodies left up to 38% of
    /// the side skirt bone (<c>j_sk_s_a_l</c>) on cloth 3-6 mm off the hip, YAB's 24%, and a third of a swinging chain on
    /// cloth that close is the hip through it. The skirt hanging free of the body still swings.
    /// </summary>
    /// <param name="result">New weights per vertex, null where the author's stand. Updated in place.</param>
    private static void DropOwnBonesNearBody(ModelParts model, XivLiveMesh.SkinnedMesh own,
                                             List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)> targets,
                                             (string Bone, float W)[]?[] result, IReadOnlySet<string> bodyBones,
                                             IReadOnlySet<int>? held)
    {
        foreach (int v in ClothVertices(model))
        {
            if (held != null && held.Contains(v)) continue;
            var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
            if (Nearest(targets, p, out float far) is not { Length: > 0 } body || far >= OwnBoneFade) continue;

            var now = result[v] is { } w ? w.ToList() : Influences(own, v);
            float ownShare = now.Where(i => !bodyBones.Contains(i.Bone)).Sum(i => i.W);
            if (ownShare <= 1e-4f) continue;

            float keep = far <= OwnBoneFull ? 0f : (far - OwnBoneFull) / (OwnBoneFade - OwnBoneFull);
            var kept = now.Where(i => !bodyBones.Contains(i.Bone)).Select(i => (i.Bone, W: i.W * keep))
                          .Where(i => i.W > 1e-4f).OrderByDescending(i => i.W).ToList();
            var bodyPart = now.Where(i => bodyBones.Contains(i.Bone)).ToList();
            if (bodyPart.Sum(i => i.W) <= 1e-4f) bodyPart = [.. body];   // nothing of its own on the body: the body's
            int slots = MaxInfluences - kept.Count;
            if (slots <= 0) continue;
            var share = Normalised(bodyPart.OrderByDescending(i => i.W).Take(slots).ToList(), 1f - kept.Sum(i => i.W));
            result[v] = [.. kept, .. share];
        }
    }

    /// <summary>Rounds of <see cref="SmoothWeights"/>.</summary>
    internal const int WeightSmoothRounds = 2;

    /// <summary>How far each round takes a vertex's body weights toward its neighbours' (half way).</summary>
    internal const float WeightSmoothRate = 0.5f;

    /// <summary>
    /// Ease the new body weights of the cloth toward their neighbours', a little: <see cref="WeightSmoothRounds"/>
    /// rounds, each <see cref="WeightSmoothRate"/> of the way.
    /// <para/>
    /// Every cloth vertex looks the bodies up on its own, and two neighbours can land either side of something sharp in
    /// that lookup — a crease under the bust, the edge of a triangle of a coarse body — and come out rigged noticeably
    /// apart. Still, nothing shows; posed, the cloth between them stretches and creases in patches. Smoothing only the
    /// BODY share keeps every bone the garment brings itself (a skirt chain, a cape) exactly as authored, and each
    /// neighbour counts by the shape of its body share, not its size, so a vertex half on a skirt chain does not drag its
    /// neighbours' body weights down. Neighbours are found across texture seams, which split one place into several
    /// vertices, by welding them on position. Cloth the lookup left alone (beyond the bodies' reach) is a neighbour as
    /// authored but never moves itself.
    /// <para/>
    /// Between two body mods only, onto plain and YAS rigs alike: within one body mod the author's weights stand and
    /// there is nothing new to smooth.
    /// </summary>
    /// <param name="result">New weights per vertex, null where the author's stand. Updated in place.</param>
    private static void SmoothWeights(ModelParts model, XivLiveMesh.SkinnedMesh own, (string Bone, float W)[]?[] result,
                                      IReadOnlySet<string> bodyBones, IReadOnlySet<int>? held)
    {
        int vc = model.Positions.Length / 3;

        // One node per place, so a seam's split vertices smooth as one and stay identical.
        var nodeOf = new int[vc];
        Array.Fill(nodeOf, -1);
        var byPlace = new Dictionary<(int, int, int), int>();
        var members = new List<List<int>>();
        foreach (int v in ClothVertices(model))
        {
            var key = ((int)MathF.Round(model.Positions[v * 3] * 1e5f), (int)MathF.Round(model.Positions[v * 3 + 1] * 1e5f),
                       (int)MathF.Round(model.Positions[v * 3 + 2] * 1e5f));
            if (!byPlace.TryGetValue(key, out int node))
            {
                byPlace[key] = node = members.Count;
                members.Add([]);
            }
            nodeOf[v] = node;
            members[node].Add(v);
        }
        int nodes = members.Count;
        if (nodes == 0) return;

        var adj = new HashSet<int>[nodes];
        for (int n = 0; n < nodes; n++) adj[n] = [];
        foreach (var part in model.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                int na = nodeOf[a], nb = nodeOf[b], nc = nodeOf[c];
                if (na < 0 || nb < 0 || nc < 0) continue;
                if (na != nb) { adj[na].Add(nb); adj[nb].Add(na); }
                if (nb != nc) { adj[nb].Add(nc); adj[nc].Add(nb); }
                if (na != nc) { adj[na].Add(nc); adj[nc].Add(na); }
            }
        }

        // Per node: the whole influence list now (new where reweighted, else as authored), and whether it may move.
        var now = new List<(string Bone, float W)>[nodes];
        var moves = new bool[nodes];
        for (int n = 0; n < nodes; n++)
        {
            int v = members[n][0];
            now[n] = result[v] is { } w ? [.. w] : Influences(own, v);
            moves[n] = members[n].Any(u => result[u] != null) && (held == null || !members[n].Any(held.Contains));
        }

        // The body share's SHAPE: its weights over their own sum.
        Dictionary<string, float>? Shape(List<(string Bone, float W)> list)
        {
            float sum = 0f;
            var d = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (bone, w) in list)
            {
                if (!bodyBones.Contains(bone)) continue;
                d[bone] = d.GetValueOrDefault(bone) + w;
                sum += w;
            }
            if (sum <= 1e-6f) return null;
            foreach (var bone in d.Keys.ToList()) d[bone] /= sum;
            return d;
        }

        for (int round = 0; round < WeightSmoothRounds; round++)
        {
            var next = new List<(string Bone, float W)>?[nodes];
            for (int n = 0; n < nodes; n++)
            {
                if (!moves[n] || adj[n].Count == 0 || Shape(now[n]) is not { } mine) continue;
                var mean = new Dictionary<string, float>(StringComparer.Ordinal);
                int counted = 0;
                foreach (int m in adj[n])
                {
                    if (Shape(now[m]) is not { } theirs) continue;
                    foreach (var (bone, w) in theirs) mean[bone] = mean.GetValueOrDefault(bone) + w;
                    counted++;
                }
                if (counted == 0) continue;

                var blended = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var (bone, w) in mine) blended[bone] = w * (1f - WeightSmoothRate);
                foreach (var (bone, w) in mean)
                    blended[bone] = blended.GetValueOrDefault(bone) + w / counted * WeightSmoothRate;

                // The body share goes back in at its own size, after the vertex's own bones, within eight.
                var keep = now[n].Where(i => !bodyBones.Contains(i.Bone)).ToList();
                float share = 1f - keep.Sum(i => i.W);
                int slots = MaxInfluences - keep.Count;
                if (slots <= 0 || share <= 0f) continue;
                var body = blended.Where(p => p.Value > 1e-4f).OrderByDescending(p => p.Value).Take(slots)
                                  .Select(p => (p.Key, p.Value)).ToList();
                next[n] = [.. keep, .. Normalised(body, share)];
            }
            for (int n = 0; n < nodes; n++)
                if (next[n] is { } w) now[n] = w;
        }

        for (int n = 0; n < nodes; n++)
        {
            if (!moves[n]) continue;
            foreach (int v in members[n])
                if (held == null || !held.Contains(v)) result[v] = [.. now[n]];
        }
    }

    /// <summary>Summed absolute difference between two vertices' influences, by bone name.</summary>
    private static float Difference(List<(string Bone, float W)> a, List<(string Bone, float W)> b)
    {
        var d = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (bone, w) in a) d[bone] = d.GetValueOrDefault(bone) + w;
        foreach (var (bone, w) in b) d[bone] = d.GetValueOrDefault(bone) - w;
        return d.Values.Sum(MathF.Abs);
    }

    /// <summary>How much of the change a cloth vertex this far from the bodies takes: whole within
    /// <see cref="WeightFull"/>, none at <see cref="WeightReach"/>, linear between.</summary>
    internal static float Fade(float distance)
        => Math.Clamp((WeightReach - distance) / (WeightReach - WeightFull), 0f, 1f);

    /// <summary><paramref name="list"/> scaled so its weights sum to <paramref name="total"/>.</summary>
    private static List<(string Bone, float W)> Normalised(List<(string Bone, float W)> list, float total)
    {
        float sum = list.Sum(i => i.W);
        if (sum <= 0f) return list;
        return list.Select(i => (i.Bone, i.W / sum * total)).ToList();
    }

    /// <summary>
    /// A body mod's extras that are not skin: piercings and pubic hair, in the body's own material family
    /// (<c>mt_c0201b0001_piercings</c>, <c>_bibopube</c>, <c>_betterpube</c>, <c>_neolithe_piercings</c>). A garment
    /// refitted across bodies drops the ones it carried from its old body — they sit on the old body's shape — and
    /// takes none from the new one.
    /// </summary>
    internal static bool IsBodyExtraMaterial(string material)
        => !SecondSkinWriter.IsBodySkinMaterial(material) && BodyExtra.IsMatch(material);

    private static readonly Regex BodyExtra = new(@"(^|/)mt_c\d{4}b\d{4}_.*(pierc|pube)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
