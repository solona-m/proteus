using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// How close a garment skin vertex must lie to a slot's new body to count as that slot's skin (2 mm). The skin was
    /// laid onto the new body just before, so skin that belongs to the slot sits ON it — the body mesh's own worst
    /// case is 1.25 mm — while skin of another slot is centimetres away from all but the seam between them.
    /// </summary>
    internal const float SwapOnBody = 0.002f;

    /// <param name="Removed">Triangles in the garment's skin meshes that were taken out.</param>
    /// <param name="Added">Triangles in the body skin meshes put in their place.</param>
    /// <param name="Kept">Skin meshes left alone because they belong to no slot being resized.</param>
    /// <param name="LostShapes">Shape keys the garment had, which the rebuilt model does not carry.</param>
    /// <param name="Reweighted">Cloth vertices given the new body's weights (see <see cref="PlanWeights"/>).</param>
    /// <param name="Trimmed">Of those, vertices whose body weights were cut to fit the eight-influence limit.</param>
    /// <param name="ExtrasDropped">Triangles of the old body's piercings and pubic hair taken out.</param>
    /// <param name="Unplaced">Influences the writer could not place — a bone in no model it was given, or a full table.</param>
    internal readonly record struct SwapReport(int Removed, int Added, int Kept, int LostShapes,
                                               int Reweighted = 0, int Trimmed = 0, int ExtrasDropped = 0,
                                               int Unplaced = 0);

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
    /// <returns>The rebuilt model, or null when no skin mesh of the garment belongs to a slot being resized.</returns>
    internal static byte[]? SwapSkin(byte[] garment, IReadOnlyList<SlotPair> pairs, out SwapReport report)
        => Rebuild(garment, pairs, swapSkin: true, weights: null, out report);

    /// <summary>
    /// Rebuild the refitted garment for its new body: swap the resized slots' skin meshes for the body's (see
    /// <see cref="SwapSkin"/>), give its cloth the new body's weights (see <see cref="PlanWeights"/>), and — whenever the
    /// weights change, which is to say across rigs — drop the piercings and pubic hair it carried from its old body
    /// (see <see cref="IsBodyExtraMaterial"/>). One re-emit for all three.
    /// </summary>
    /// <returns>The rebuilt model, or null when there was nothing to change.</returns>
    internal static byte[]? Rebuild(byte[] garment, IReadOnlyList<SlotPair> pairs, bool swapSkin, WeightPlan? weights,
                                    out SwapReport report)
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
        int removed = 0, kept = 0;
        foreach (var mesh in skinMeshes)
        {
            var verts = mesh.SelectMany(p => p.Triangles).Distinct().ToList();
            int best = -1;
            float bestShare = 0.5f;   // a majority, or the mesh stays
            for (int s = 0; s < surfaces.Count; s++)
            {
                int on = verts.Count(v => surfaces[s].Nearest(At(model, v), SwapOnBody, out _));
                float share = verts.Count > 0 ? (float)on / verts.Count : 0f;
                if (share > bestShare) { bestShare = share; best = s; }
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

        if (dropped.Count == 0 && weights == null) return null;

        // One layer per slot whose skin came out: its body's skin meshes, whole, under the garment's skin material.
        var layers = claimedBy.OrderBy(s => s).Select(s => new SecondSkinLayer
        {
            MaterialName = skinMaterial!,
            // Tagged as the body tags it — atr_ude, atr_hij, atr_nek are how long gloves or a high collar hide the
            // skin under them, and the garment's own skin carried the same tags — except for variant tags, which would
            // be judged against the garment's IMC mask (a Neolithe body carries eight, atr_tv_a..h).
            Geometry = [new ContentGeometry(swappable[s].TargetModel!, SecondSkinWriter.IsBodySkinMaterial,
                                            DropVariantAttributes: true)],
        }).ToList();
        var reskinned = new SecondSkinWriter.ReskinReport();
        var rebuilt = SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(), layers, garment, out _,
                                             dropHostMesh: dropped.Contains,
                                             hostReskin: weights == null ? null : weights.For,
                                             boneDonors: weights?.Donors, reskinReport: reskinned);

        int added = claimedBy.Sum(s => SkinTriangles(SecondSkinWriter.Parse(swappable[s].TargetModel!)));
        report = new SwapReport(removed, added, kept, SecondSkinWriter.Parse(garment).Shapes.Count,
                                weights?.Reweighted ?? 0, weights?.Trimmed ?? 0, extras, reskinned.Dropped);
        return rebuilt;
    }

    private static Vector3 At(ModelParts m, int v)
        => new(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);

    /// <summary>Triangles in a model's LOD0 meshes drawn with a body-skin material.</summary>
    private static int SkinTriangles(SecondSkinWriter.Source src)
    {
        int total = 0;
        int end = src.Lod0MeshIndex + src.Lod0MeshCount;
        for (int m = src.Lod0MeshIndex; m < end && m < src.MeshCount; m++)
        {
            int mo = src.MeshStart + m * 36;
            ushort mat = BitConverter.ToUInt16(src.S, mo + 8);
            if (mat < src.MatNames.Count && SecondSkinWriter.IsBodySkinMaterial(src.MatNames[mat]))
                total += (int)(BitConverter.ToUInt32(src.S, mo + 4) / 3);
        }
        return total;
    }
}
