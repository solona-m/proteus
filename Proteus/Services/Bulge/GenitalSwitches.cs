using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services.Bulge;

/// <summary>
/// The garment's own penis switches, given to the new body's genitals (plan genital-switches, part 1). A garment can carry
/// its body's penis behind its own options — Beach Briefs carries Gen A–D's, each tagged <c>atr_dv_e…f</c> with
/// <c>atr_dv_j</c>, and ticking "Briefs" turns <c>j</c> off and hides them all. A refit that swaps the skin drops those
/// pieces and puts in the new body's, whose penis is untagged and so always drawn: through the briefs. Here the garment's
/// switch sets are read off its own pieces, and the new body's genital triangles are emitted once per set, each copy tagged
/// so (<see cref="ContentGeometry.Switches"/>) — a submesh draws only when every tag it carries is on, so one copy per set
/// draws exactly when the garment drew a penis.
/// </summary>
internal static class GenitalSwitches
{
    /// <summary>
    /// The cheap gate: a tagged skin submesh of the garment whose bounds reach the crotch box of the body it was made on —
    /// |x − mid| ≤ 12 cm·s, reaching above half the body's height (a legs body's crotch stands at about 80% of it). Every
    /// garment without one (pants whose tagged skin is the shins) passes by at the cost of a bounds test.
    /// </summary>
    internal static bool MayHaveSwitches(ModelParts garment, ModelParts sourceBody)
    {
        float height = sourceBody.Max.Y - sourceBody.Min.Y;
        if (height <= 0f) return false;
        float s = Math.Clamp(height / 1.05f, 0.4f, 1.5f);
        float midX = (sourceBody.Min.X + sourceBody.Max.X) / 2f;
        float lowY = sourceBody.Min.Y + 0.5f * height;
        return garment.Parts.Any(p => p.Island < 0 && p.AttributeMask != 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)
                                      && p.Max.Y >= lowY && p.Min.X <= midX + 0.12f * s && p.Max.X >= midX - 0.12f * s);
    }

    /// <summary>
    /// The garment's switch sets: the distinct tag sets of its switched penis pieces, in submesh order. A switched penis
    /// piece is a skin submesh of the garment that carries a tag, lies at least <see cref="GenitalParts.WhollyInWindow"/> in
    /// the made-on body's crotch window, and stands more than <see cref="GenitalParts.PenisProtrusion"/> off that body's
    /// skin without its genitals or hangs at least <see cref="GenitalParts.PenisWeight"/> on penis bones. Null, with the
    /// reason in <paramref name="why"/> (null when there is nothing to say), when there is none, when the garment draws a
    /// penis untagged anywhere (it is always drawn, so the new body's must be too), when a tagged penis reaches past the
    /// window (its set is not the whole picture), or when the made-on body has no crotch to read.
    /// </summary>
    internal static IReadOnlyList<IReadOnlySet<string>>? Of(ModelParts garment, XivLiveMesh.SkinnedMesh? garmentSkin,
                                                            ModelParts sourceBody, GenitalParts.Reading? source, out string? why)
    {
        why = null;
        if (source == null) { why = "the garment's body has no crotch to read"; return null; }
        var plain = GenitalParts.PlainSkin(sourceBody, source.Genitals);
        var sets = new List<IReadOnlySet<string>>();
        foreach (var part in garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                          .OrderBy(p => p.Mesh).ThenBy(p => p.Submesh))
        {
            var verts = part.Triangles.Distinct().ToArray();
            if (verts.Length == 0) continue;
            float window = verts.Count(v => GenitalParts.InWindow(GenitalParts.At(garment, v), source.Anchor)) / (float)verts.Length;
            float protrusion = GenitalParts.Protrusion(garment, verts, plain, source.Anchor);
            float onPenis = garmentSkin == null ? 0f : verts.Average(v => GenitalParts.OnPenis(garmentSkin, v));
            bool penis = protrusion > GenitalParts.PenisProtrusion || onPenis >= GenitalParts.PenisWeight;
            bool wholly = window >= GenitalParts.WhollyInWindow;
            if (part.AttributeMask == 0)
            {
                if (penis && wholly) { why = "the garment draws an untagged penis"; return null; }
                // A penis merged into the garment's untagged skin: triangles wholly in the window with a corner standing
                // off the body as a penis does (not the hole's rim, which lies on it).
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    if (!new[] { a, b, c }.All(v => GenitalParts.InWindow(GenitalParts.At(garment, v), source.Anchor))) continue;
                    if (GenitalParts.Protrusion(garment, [a, b, c], plain, source.Anchor) > GenitalParts.PenisProtrusion)
                    { why = "the garment draws an untagged penis"; return null; }
                }
                continue;
            }
            if (!penis) continue;   // the cap over the hole, knee and shin skin: tagged, but no penis
            if (!wholly) { why = "a switched piece reaches past the crotch window"; return null; }
            var names = Names(garment, part.AttributeMask);
            if (!sets.Any(s => s.SetEquals(names))) sets.Add(names);
        }
        return sets.Count > 0 ? sets : null;
    }

    /// <summary>The attribute names a mask carries, against the model's own table.</summary>
    internal static IReadOnlySet<string> Names(ModelParts model, uint mask)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int bit = 0; bit < 32 && bit < model.AttributeNames.Count; bit++)
            if ((mask & (1u << bit)) != 0) names.Add(model.AttributeNames[bit]);
        return names;
    }

    /// <summary>
    /// The new body's switches for one layer's geometry (<paramref name="keep"/> chooses its material): each genital part
    /// of <paramref name="target"/> on that material, whole or as its merged triangles (rule b), copied once per set; on a
    /// piercing material, the triangles lying on the genitals (every corner within <see cref="BodyRetarget.AdornReach"/> of
    /// a genital vertex) or hanging on penis bones; on a pube material, a pube part only when the reading already holds it
    /// as genital (rigged to the penis). Nails never. <paramref name="fringe"/> counts the triangles of a rule-b part's
    /// submesh with some but not all corners on the genitals, left untagged.
    /// </summary>
    internal static List<SubmeshSwitch> For(ModelParts target, XivLiveMesh.SkinnedMesh? targetSkin, GenitalParts.Reading reading,
                                            IReadOnlyList<IReadOnlySet<string>> sets, Func<string, bool> keep, out int fringe,
                                            out int triangles)
    {
        if (BodyRetarget.Tuned.GenitalSwitchFault == BodyRetarget.SwitchFault.InFor)
            throw new InvalidOperationException("genital switch fault (test)");
        fringe = 0;
        triangles = 0;
        var specs = new List<SubmeshSwitch>();
        var genitalVerts = reading.Genitals.SelectMany(g => g.Triangles.SelectMany(t => new[] { g.Part.Triangles[t * 3],
                                                       g.Part.Triangles[t * 3 + 1], g.Part.Triangles[t * 3 + 2] })).ToHashSet();
        foreach (var g in reading.Genitals.Where(g => keep(g.Part.Material)))
        {
            if (!SecondSkinWriter.IsBodySkinMaterial(g.Part.Material) && BodyRetarget.IsBodyPubeMaterial(g.Part.Material)
                && g.Rule != "a") continue;
            int all = g.Part.Triangles.Length / 3;
            bool whole = g.Triangles.Count == all;
            specs.Add(new SubmeshSwitch(g.Part.Mesh, g.Part.Submesh,
                                        whole ? null : g.Triangles.Select(t => g.Part.Ordinals[t]).ToHashSet(), sets));
            triangles += g.Triangles.Count * sets.Count;
            if (whole) continue;
            var inside = g.Triangles.ToHashSet();
            for (int t = 0; t < all; t++)
                if (!inside.Contains(t) && Enumerable.Range(0, 3).Any(k => genitalVerts.Contains(g.Part.Triangles[t * 3 + k])))
                    fringe++;
        }
        // Piercings on the genitals: they would float where the hidden penis was.
        if (genitalVerts.Count > 0)
        {
            var grid = new GenitalParts.PointGrid(genitalVerts.Select(v => GenitalParts.At(target, v)).ToList(), 0.01f);
            foreach (var part in target.Parts.Where(p => p.Island < 0 && keep(p.Material) && BodyRetarget.IsBodyPiercingMaterial(p.Material)
                                                         && reading.Genitals.All(g => g.Part != p)))
            {
                var on = new HashSet<int>();
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    bool near = Enumerable.Range(0, 3).All(k => grid.Nearest(GenitalParts.At(target, part.Triangles[t + k]),
                                                                             BodyRetarget.AdornReach * 2) <= BodyRetarget.AdornReach);
                    bool rigged = targetSkin != null && Enumerable.Range(0, 3).Average(k => GenitalParts.OnPenis(targetSkin, part.Triangles[t + k]))
                                  >= GenitalParts.PenisWeight;
                    if (near || rigged) on.Add(t / 3);
                }
                if (on.Count == 0) continue;
                bool whole = on.Count == part.Triangles.Length / 3;
                specs.Add(new SubmeshSwitch(part.Mesh, part.Submesh, whole ? null : on.Select(t => part.Ordinals[t]).ToHashSet(), sets));
                triangles += on.Count * sets.Count;
            }
        }
        return specs;
    }

    /// <summary>
    /// The submeshes of <paramref name="model"/> (distinct mesh and submesh, never an island) whose tags hold a whole switch
    /// set, in <paramref name="meshes"/> when given: the one count both the garment's own and the rebuilt model are read by.
    /// </summary>
    internal static int CountSetParts(ModelParts model, IReadOnlySet<int>? meshes, IReadOnlyList<IReadOnlySet<string>> sets)
        => model.Parts.Where(p => p.Island < 0 && (meshes == null || meshes.Contains(p.Mesh)))
                      .Select(p => (p.Mesh, p.Submesh, Names: Names(model, p.AttributeMask)))
                      .Where(p => sets.Any(s => s.IsSubsetOf(p.Names)))
                      .Select(p => (p.Mesh, p.Submesh)).Distinct().Count();
}
