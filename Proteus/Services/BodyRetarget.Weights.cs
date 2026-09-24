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
        // The old bodies, for the change between the two. Any one unreadable and the change cannot be taken anywhere:
        // the new body's weights are then used outright, as before the change was.
        List<(BodySurface Surface, XivLiveMesh.SkinnedMesh Skin)>? sources = [];
        foreach (var pair in pairs)
        {
            sourceBones.UnionWith(SecondSkinWriter.Parse(pair.SourceModel!).BoneNames);
            targetBones.UnionWith(SecondSkinWriter.Parse(pair.TargetModel!).BoneNames);
            if (ModelSkinReader.Read(pair.TargetModel!, null, null) is not { } skin) return null;
            if (skin.VertexCount * 3 != pair.Target.Positions.Length) return null;   // not the part reader's order
            targets.Add((new BodySurface(pair.Target, BodySurface.CellFor(MeanEdgeOf(pair.Target))), skin));

            if (sources != null
                && ModelPartReader.Read(pair.SourceModel!) is { } sourceParts
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
        float[] wasAt = before != null && ModelPartReader.Read(before) is { } authored
                        && authored.Positions.Length == model.Positions.Length
            ? authored.Positions
            : model.Positions;

        int vc = model.Positions.Length / 3;
        var result = new (string Bone, float W)[]?[vc];
        int reweighted = 0, trimmed = 0;

        foreach (int v in ClothVertices(model))
        {
            if (held != null && held.Contains(v)) continue;
            var p = new Vector3(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
            var mine = Influences(own, v);

            // All the garment's own bones: untouched, and no body lookup needed.
            if (mine.Where(i => !bodyBones.Contains(i.Bone)).Sum(i => i.W) >= 0.999f) continue;
            if (Nearest(targets, p) is not { } body || body.Length == 0) continue;
            var was = new Vector3(wasAt[v * 3], wasAt[v * 3 + 1], wasAt[v * 3 + 2]);
            if (sources != null)
            {
                // Both bodies have to be under the vertex for the change between them to mean anything. With only the
                // old one missing there is nothing to compare against, and taking the new body's weights outright
                // would re-rig cloth that never sat on it.
                if (Nearest(sources, was) is not { Length: > 0 } oldBody) continue;
                if (Change(mine, oldBody, body, bodyBones) is { Count: > 0 } changed) body = [.. changed];
            }

            if (Combine(mine, body, bodyBones, out bool cut) is not { } combined) continue;
            result[v] = combined;
            reweighted++;
            if (cut) trimmed++;
        }

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
            if (Nearest(targets, p) is not { } body || body.Length == 0) continue;
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
                                                    Vector3 p)
    {
        BodySurface.Hit? best = null;
        XivLiveMesh.SkinnedMesh? on = null;
        float reach = WeightReach;
        foreach (var (surface, skin) in targets)
        {
            if (!surface.Nearest(p, reach, out var hit)) continue;
            best = hit;
            on = skin;
            reach = hit.Distance;
        }
        if (best is not { } h || on == null) return null;

        return MeshMath.BlendWeights([.. Influences(on, h.A)], h.U, [.. Influences(on, h.B)], h.V,
                                     [.. Influences(on, h.C)], h.W, MaxInfluences);
    }

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
