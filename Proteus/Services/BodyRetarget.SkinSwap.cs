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
    /// <param name="ExtrasDropped">Triangles of the old body's piercings, nails and pubic hair taken out.</param>
    /// <param name="Unplaced">Influences the writer could not place — a bone in no model it was given, or a full table.</param>
    /// <param name="Posed">Skin meshes only partly on the bodies — a heeled shoe's own foot — which were kept.</param>
    /// <param name="Cut">Triangles of the new body's skin left out, because the garment's author deleted the body
    /// there — see <see cref="CutLike"/>.</param>
    /// <param name="Trimmed">Vertices the PLANNER could not fit in eight influences.</param>
    /// <param name="Slotted">Influences the MESH had no slot for — a four-slot cloth mesh given a vertex planned
    /// with five. The heaviest are kept and the weights renormalised, so nothing shrinks.</param>
    /// <param name="Adorned">Triangles of the new body's nails and piercings carried in (see <see cref="AdornmentsOf"/>).</param>
    /// <param name="Tucked">Of <paramref name="Added"/>, triangles drawn past the author's edge, in under the cloth (see
    /// <see cref="TuckUnderCloth"/>).</param>
    /// <param name="TuckUnread">Slots the tuck was asked for whose body's weights could not be read: nothing tucked there.</param>
    /// <param name="Opened">Of <paramref name="Tucked"/>, triangles drawn in the open, inside the author's own skin
    /// (see <see cref="TuckSpanShare"/>).</param>
    /// <param name="Smallclothes">Triangles of the new body's smallclothes brought in with its skin, where it draws them
    /// in place of skin (see <see cref="ReadySmallclothes"/>), before the cut.</param>
    /// <param name="SeenInto">Of <paramref name="Tucked"/>, triangles drawn in the open where the garment's inside would
    /// otherwise show (see <see cref="TuckSeeReach"/>); none of them is counted in <paramref name="Opened"/>.</param>
    /// <param name="Spanned">Of <paramref name="Added"/>, triangles a slot the garment only reaches onto drew in the open
    /// inside the author's own skin, which the cut had left out (see <see cref="GrowSpannedOpenSkin"/>).</param>
    internal readonly record struct SwapReport(int Removed, int Added, int Kept, int LostShapes,
                                               int Reweighted = 0, int Trimmed = 0, int ExtrasDropped = 0,
                                               int Unplaced = 0, int Posed = 0, int Cut = 0, int Slotted = 0,
                                               int Adorned = 0, int Tucked = 0, int TuckUnread = 0, int Opened = 0,
                                               int Smallclothes = 0, int SeenInto = 0, int Spanned = 0);

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
    /// <param name="tuckSlot">See <see cref="Rebuild"/>.</param>
    /// <param name="carryPiercings">See <see cref="Rebuild"/>.</param>
    /// <param name="seeInside">See <see cref="Rebuild"/>.</param>
    /// <returns>The rebuilt model, or null when no skin mesh of the garment belongs to a slot being resized.</returns>
    internal static byte[]? SwapSkin(byte[] garment, IReadOnlyList<SlotPair> pairs, out SwapReport report,
                                     bool cutHidden = true, string? tuckSlot = null, bool carryPiercings = false,
                                     bool seeInside = false)
        => Rebuild(garment, pairs, swapSkin: true, weights: null, out report, cutHidden, tuckSlot: tuckSlot,
                   carryPiercings: carryPiercings, seeInside: seeInside);

    /// <summary>
    /// Rebuild the refitted garment for its new body: swap the resized slots' skin meshes for the body's (see
    /// <see cref="SwapSkin"/>), give its cloth the new body's weights (see <see cref="PlanWeights"/>), and — when the
    /// garment goes to another body mod — drop the piercings and pubic hair it carried from its old body
    /// (see <see cref="IsBodyExtraMaterial"/>). One re-emit for all three.
    /// </summary>
    /// <param name="cutHidden">Carry the author's cut onto the new body's skin (see <see cref="CutLike"/>).</param>
    /// <param name="acrossBodies">The garment is going to another body mod, not between sizes of one.</param>
    /// <param name="swapMeshes">The skin meshes the solver parted the cloth from (<see cref="SwappedSkinMeshes"/>), when
    /// it was told which: then exactly those go and every other stays, rather than judging again on the laid model —
    /// see <see cref="Plan"/>. Null judges here, as always.</param>
    /// <param name="tuckSlot">The slot whose cut skin is carried on in under the cloth past the author's edge (see
    /// <see cref="TuckUnderCloth"/>): the game's own top's chest. Null tucks nothing, as every other refit has it.</param>
    /// <param name="carryPiercings">Bring the new body's piercings in with its skin, as its nails always are (see
    /// <see cref="AdornmentsOf"/>). Off, the default, carries none, and the garment's own piercings are left as they were
    /// before build 1147: dropped with the old body's extras across body mods, kept within one.</param>
    /// <param name="parted">With <paramref name="swapMeshes"/> null, the skin meshes the solver parted the cloth from
    /// anyway, judged on the bodies it was made on: each is swapped wherever any of it lies on a new body, even when, laid,
    /// some of it reaches past that body's edge — skin of the old body that the new one draws in another slot. Every
    /// other mesh is judged here as before. Null for none.</param>
    /// <param name="seeInside">With <paramref name="tuckSlot"/>, the tuck also draws open skin through which the garment's
    /// inside would show (see <see cref="TuckSeeReach"/>): the game's own top only. Off, as every other refit has it.</param>
    /// <returns>The rebuilt model, or null when there was nothing to change.</returns>
    internal static byte[]? Rebuild(byte[] garment, IReadOnlyList<SlotPair> pairs, bool swapSkin, WeightPlan? weights,
                                    out SwapReport report, bool cutHidden = true, bool acrossBodies = false,
                                    IReadOnlySet<int>? swapMeshes = null, string? tuckSlot = null,
                                    bool carryPiercings = false, IReadOnlySet<int>? parted = null,
                                    bool seeInside = false, bool openSkin = false)
    {
        report = default;
        var swappable = swapSkin ? pairs.Where(p => p.Swaps).ToList() : [];
        if ((swappable.Count == 0 && weights == null) || ModelPartReader.Read(garment) is not { } model) return null;

        // As worn on a slot that brings its smallclothes in: the garment's skin laid on Muse+'s boxers is that slot's.
        var surfaces = swappable.Select(p => p.TargetSurface()).ToList();

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
            // Except a mesh the solver parted (parted), already judged whole on the bodies it was made on: the old body's
            // legs can reach higher up the waist than the new one's, and laid there its top is off the new legs.
            int best = -1;
            float bestShare = 0f;
            int onAny = verts.Count(v => surfaces.Any(s => OnBody(s, At(model, v))));
            for (int s = 0; s < surfaces.Count; s++)
            {
                int on = verts.Count(v => surfaces[s].Nearest(At(model, v), SwapOnBody, out _));
                float share = verts.Count > 0 ? (float)on / verts.Count : 0f;
                if (share > bestShare) { bestShare = share; best = s; }
            }
            bool partlyOff = swapMeshes != null ? !swapMeshes.Contains(mesh.Key)
                           : parted?.Contains(mesh.Key) != true && verts.Count > 0 && onAny < verts.Count * OnBodyShare;
            if (partlyOff)
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
        // The old body's extras: they sit on the shape the garment is leaving. Only across body mods — within one, a rig
        // change (Rue+'s Gen A legs to Gen B) carries them with the skin they sit on, and nothing would replace them.
        // Across body mods with no weights (a body unreadable) they stay, as before.
        int extras = 0;
        if (acrossBodies && weights != null)
            foreach (var part in model.Parts)
                if (part.Island < 0 && IsBodyExtraMaterial(part.Material) && dropped.Add(part.Mesh))
                    extras += model.Parts.Where(q => q.Island < 0 && q.Mesh == part.Mesh).Sum(q => q.Triangles.Length / 3);
        // The old body's nails and piercings on a slot whose skin is swapped go with that skin — the new body brings its
        // own (see AdornmentsOf) — even within one body mod, where they would otherwise be doubled. Where they were is
        // kept: the author showed one there. Taken out only once the new body's are known to fit (below). Piercings only
        // when the new body's come in: otherwise the garment's own are left to the rule above.
        var authored = new PointGrid();
        var oldAdornments = new List<(int Mesh, int Triangles)>();
        if (claimedBy.Count > 0)
            foreach (var mesh in model.Parts.Where(p => p.Island < 0 && IsBodyAdornmentMaterial(p.Material)
                                                        && (carryPiercings || !IsBodyPiercingMaterial(p.Material)))
                                            .GroupBy(p => p.Mesh))
            {
                var verts = mesh.SelectMany(p => p.Triangles).Distinct().ToList();
                int on = verts.Count(v => claimedBy.Any(s => surfaces[s].Nearest(At(model, v), AdornReach, out _)));
                if (on * 2 < verts.Count) continue;
                foreach (int v in verts) authored.Add(At(model, v));
                oldAdornments.Add((mesh.Key, mesh.Sum(p => p.Triangles.Length / 3)));
            }

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
        // entire legs body down to the ankles into the top's model, drawing through whatever pants are worn. But in the
        // automatic refit (openSkin) not where the author's own skin spans the body (see GrowSpannedOpenSkin).
        var alwaysCut = new HashSet<int>(reachedOnto.Except(mostOf));
        bool anyCut = cutHidden || alwaysCut.Count > 0;
        var drawn = anyCut ? new BodySurface(model, BodySurface.CellFor(MeanEdgeOf(model))) : null;
        var drawnEdges = anyCut ? SkinEdges.Of(model) : null;
        var cloth = new ClothCrossings(model);
        var cuts = new Dictionary<int, Dictionary<int, HashSet<ushort>>?>();
        // The author's own cut, where the tuck added to it: what the new body's nails and piercings are judged on.
        var authorCuts = new Dictionary<int, Dictionary<int, HashSet<ushort>>?>();
        // Each slot's body as it goes in: its own model, or one whose skin was pulled back to the author's edge.
        var bodyOf = new Dictionary<int, byte[]>();
        int cutTris = 0, keptTris = 0, tuckedTris = 0, tuckUnreadSlots = 0, openedTris = 0, seenIntoTris = 0, spannedTris = 0;
        foreach (int s in claimedBy)
        {
            int these = 0, gone = 0, tucked = 0, opened = 0, seenInto = 0, spanned = 0;
            byte[]? pulled = null;
            Dictionary<int, HashSet<ushort>>? authorCut = null;
            bool tuckUnread = false;
            bool cutThis = cutHidden || alwaysCut.Contains(s);
            bool tuck = cutThis && tuckSlot != null && swappable[s].Slot == tuckSlot && !Tuned.NoTuck;
            // Cut only because the garment reaches onto it, the hidden-skin cut off: no author's cut is being carried
            // here, so skin the cut leaves out in the open inside the author's skin is drawn (see GrowSpannedOpenSkin).
            bool open = openSkin && alwaysCut.Contains(s) && !cutHidden && !tuck;
            cuts[s] = !cutThis || drawn == null || drawn.IsEmpty ? null : CutLike(drawn, drawnEdges!, cloth,
                                                                      swappable[s].TargetModel!, swappable[s].TargetHidden,
                                                                      tuck, out these, out gone, out pulled, out tucked,
                                                                      out authorCut, out tuckUnread, out opened,
                                                                      out seenInto, out spanned, seeInside,
                                                                      smallclothes: swappable[s].Smallclothes != null,
                                                                      openSkin: open);
            authorCuts[s] = authorCut ?? cuts[s];
            bodyOf[s] = pulled ?? swappable[s].TargetModel!;
            keptTris += cuts[s] == null ? SkinTriangles(swappable[s].Target) : these;
            cutTris += gone;
            tuckedTris += tucked;
            openedTris += opened;
            seenIntoTris += seenInto;
            spannedTris += spanned;
            if (tuckUnread) tuckUnreadSlots++;
        }

        // The new body's nails and piercings, each material a layer of its own and drawn as the body draws it, tags and
        // all: a nail mod picks the nail's shape and material by atrx_ tag. They share the skin's cut, which belongs to
        // the model — so a body whose skin went in whole still gets one, holding only the adornments, and a mesh missing
        // from it draws whole as before.
        // Materials are capped and skin comes first. The swap is all or nothing: the garment's own are taken out only when
        // every one of the new body's fits, so a garment with no room keeps the nails it had rather than going bare.
        var adornments = claimedBy.OrderBy(s => s)
                                  .Select(s => (Slot: s, Adorn: AdornmentsOf(bodyOf[s], swappable[s].TargetHidden,
                                                                             authorCuts[s], cloth, authored,
                                                                             carryPiercings)))
                                  .Where(a => a.Adorn.Materials.Count > 0)
                                  .ToList();
        // The smallclothes a slot brings in come before them: without its boxers, Muse+'s hips would be a hole.
        int smallclothesLayers = claimedBy.Sum(s => swappable[s].Smallclothes?.Count ?? 0);
        int room = SecondSkinWriter.MaxMaterials - SecondSkinWriter.Parse(garment).MatNames.Count - claimedBy.Count
                 - smallclothesLayers;
        bool fits = adornments.Sum(a => a.Adorn.Materials.Count) <= room;
        int adorned = 0;
        var adornMaterials = new Dictionary<int, List<string>>();
        if (fits)
        {
            foreach (var (s, adorn) in adornments)
            {
                var cut = cuts[s] ?? [];
                foreach (var (mesh, set) in adorn.DrawOnly) cut[mesh] = set;
                cuts[s] = cut;
                adornMaterials[s] = adorn.Materials;
                adorned += adorn.Triangles;
            }
            foreach (var (mesh, tris) in oldAdornments)
                if (dropped.Add(mesh)) extras += tris;
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
        // The new body's smallclothes, where it draws them in place of skin: each a layer of its own, under the garment's
        // own name for the copy of its material the save publishes (see ReadySmallclothes), cut as its skin is.
        int smallclothesTris = 0;
        foreach (int s in claimedBy.OrderBy(s => s))
            foreach (var (bodyMaterial, garmentMaterial) in swappable[s].Smallclothes ?? new Dictionary<string, string>())
            {
                layers.Add(new SecondSkinLayer
                {
                    MaterialName = garmentMaterial,
                    Geometry = [new ContentGeometry(bodyOf[s], m => string.Equals(m, bodyMaterial, StringComparison.Ordinal),
                                                    HiddenAttributes: swappable[s].TargetHidden,
                                                    DropVariantAttributes: true, DrawOnly: cuts[s])],
                });
                smallclothesTris += swappable[s].Target.Parts
                    .Where(p => p.Island < 0 && string.Equals(p.Material, bodyMaterial, StringComparison.Ordinal))
                    .Sum(p => p.Triangles.Length / 3);
            }
        foreach (var (s, materials) in adornMaterials.OrderBy(a => a.Key))
            foreach (string material in materials)
                layers.Add(new SecondSkinLayer
                {
                    MaterialName = material,
                    Geometry = [new ContentGeometry(bodyOf[s], m => string.Equals(m, material, StringComparison.Ordinal),
                                                    HiddenAttributes: swappable[s].TargetHidden,
                                                    DropVariantAttributes: true, DrawOnly: cuts[s])],
                });
        var reskinned = new SecondSkinWriter.ReskinReport();
        var rebuilt = SecondSkinWriter.Build(Array.Empty<SecondSkinWriter.SourceSpec>(), layers, garment, out _,
                                             dropHostMesh: dropped.Contains,
                                             hostReskin: weights == null ? null : weights.For,
                                             boneDonors: weights?.Donors, reskinReport: reskinned);

        report = new SwapReport(removed, keptTris, kept, SecondSkinWriter.Parse(garment).Shapes.Count,
                                weights?.Reweighted ?? 0, weights?.Trimmed ?? 0, extras, reskinned.Dropped, posed,
                                cutTris, reskinned.Trimmed, adorned, tuckedTris, tuckUnreadSlots, openedTris,
                                smallclothesTris, seenIntoTris, spannedTris);
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
    /// <param name="tuck">Carry the kept skin on past the author's edge in under the cloth (see <see cref="TuckUnderCloth"/>).</param>
    /// <param name="tucked">Triangles the tuck drew, counted in <paramref name="kept"/>.</param>
    /// <param name="authorCut">The cut before the tuck, when the tuck added to it; null otherwise.</param>
    /// <param name="tuckUnread">The tuck was asked for and the body's weights could not be read, so it grew nothing.</param>
    /// <param name="opened">Of <paramref name="tucked"/>, triangles drawn in the open, inside the author's own skin.</param>
    /// <param name="seenInto">Of <paramref name="tucked"/>, triangles drawn in the open where the garment's inside would
    /// show (see <see cref="TuckSeeReach"/>).</param>
    /// <param name="seeInside">Let the tuck draw those (see <see cref="Rebuild"/>).</param>
    /// <param name="openSkin">Draw the skin the cut left out in the open inside the author's own skin (see
    /// <see cref="GrowSpannedOpenSkin"/>): a slot the garment only reaches onto. Never with <paramref name="tuck"/>.</param>
    /// <param name="spanned">Triangles <paramref name="openSkin"/> drew, counted in <paramref name="kept"/>; the cut before
    /// it is then <paramref name="authorCut"/>.</param>
    private static Dictionary<int, HashSet<ushort>>? CutLike(BodySurface drawn, SkinEdges edges, ClothCrossings cloth,
                                                             byte[] body, IReadOnlySet<string>? hidden, bool tuck,
                                                             out int kept, out int cut, out byte[]? pulled,
                                                             out int tucked,
                                                             out Dictionary<int, HashSet<ushort>>? authorCut,
                                                             out bool tuckUnread, out int opened, out int seenInto,
                                                             out int spanned, bool seeInside, bool smallclothes = false,
                                                             bool openSkin = false)
    {
        kept = cut = tucked = opened = seenInto = spanned = 0;
        pulled = null;
        authorCut = null;
        tuckUnread = false;
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
        if (smallclothes) CutSmallclothes(drawn, edges, parts, baseOf, sets);
        pulled = PullToEdge(drawn, edges, cloth, body, parts, drawnCorners, keptTris, out var movedAt);
        if (openSkin && !Tuned.NoCutOpenSkin)
        {
            // Never on a slot the tuck runs on (see Rebuild): the tuck draws open skin by its own rule.
            var (spannedSets, spannedTris) = GrowSpannedOpenSkin(drawn, edges, cloth, parts, covered, keptTris, movedAt,
                                                                 baseOf);
            if (spannedTris > 0)
            {
                authorCut = sets.ToDictionary(e => e.Key, e => new HashSet<ushort>(e.Value));
                foreach (var (mesh, local) in spannedSets)
                {
                    if (!sets.TryGetValue(mesh, out var set)) sets[mesh] = set = [];
                    set.UnionWith(local);
                }
                spanned = spannedTris;
                kept += spannedTris;
                cut -= spannedTris;
            }
        }
        if (!tuck) return sets;

        var (grown, added, open, seen, unread) = TuckUnderCloth(drawn, cloth, body, parts, covered, keptTris, movedAt,
                                                                baseOf, edges, seeInside);
        tuckUnread = unread;
        if (added == 0) return sets;
        opened = open;
        seenInto = seen;
        authorCut = sets.ToDictionary(e => e.Key, e => new HashSet<ushort>(e.Value));
        foreach (var (mesh, local) in grown)
        {
            if (!sets.TryGetValue(mesh, out var set)) sets[mesh] = set = [];
            set.UnionWith(local);
        }
        tucked = added;
        kept += added;
        cut -= added;
        return sets;

        void Keep(HashSet<ushort> set, int v, int bv)
        {
            if (covered[v] && v - bv is >= 0 and <= ushort.MaxValue) set.Add((ushort)(v - bv));
        }
    }

    /// <summary>
    /// The author's cut, carried onto the new body's smallclothes as onto its skin: they draw where the garment's skin
    /// drew — laid onto them, where the new body has no skin of its own — and not where the author deleted it. Every
    /// smallclothes mesh gets its entry, empty or not, as the skin's do (a mesh missing from the cut draws whole). Never
    /// pulled to the author's edge or tucked: those passes read the body's skin.
    /// </summary>
    private static void CutSmallclothes(BodySurface drawn, SkinEdges edges, ModelParts parts,
                                        Dictionary<int, int> baseOf, Dictionary<int, HashSet<ushort>> sets)
    {
        foreach (var part in parts.Parts)
        {
            if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)
                || !BodySurface.IsSmallclothes(part.Material) || !baseOf.TryGetValue(part.Mesh, out int bv)) continue;
            var set = sets.TryGetValue(part.Mesh, out var have) ? have : sets[part.Mesh] = [];
            foreach (int v in part.Triangles.Distinct())
                if (Covers(drawn, At(parts, v), edges) && v - bv is >= 0 and <= ushort.MaxValue)
                    set.Add((ushort)(v - bv));
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
        var sources = pairs.Where(p => p.Swaps)
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
    /// <param name="movedAt">Where each corner the pull moved stood before it, so a vertex split at a texture seam is
    /// matched with its twin — what <see cref="TuckUnderCloth"/> leaves alone. Empty when nothing moved.</param>
    /// <returns>The body's bytes with those corners moved, or null when none needed it or the model cannot be written.</returns>
    private static byte[]? PullToEdge(BodySurface drawn, SkinEdges edges, ClothCrossings cloth, byte[] body,
                                      ModelParts parts, IReadOnlyCollection<int> corners,
                                      IReadOnlyList<(int A, int B, int C)> keptTris, out HashSet<Vector3> movedAt)
    {
        movedAt = [];
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
            movedAt.Add(p);
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
    internal static bool PastEdge(BodySurface.Hit hit, SkinEdges edges)
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
    internal sealed class ClothCrossings
    {
        private const float Cell = 0.02f;
        private readonly List<(Vector3 A, Vector3 B, Vector3 C)> tris = [];
        private readonly List<float> clear = [];
        // Each face's outer side, by its winding turned the way the garment winds its outer faces (see SeesInside).
        private readonly List<Vector3> outward = [];
        private readonly Dictionary<(int, int, int), List<int>> grid = [];

        /// <param name="countVariants">Count parts switched by an IMC variant tag as cloth too: for asking what the
        /// garment covers whichever of its options is drawn (see <see cref="CoveredTargetSkin"/>).</param>
        public ClothCrossings(ModelParts garment, bool countVariants = false)
        {
            int vc = garment.Positions.Length / 3;
            var vertAt = new Vec3[vc];
            for (int i = 0; i < vc; i++)
                vertAt[i] = new Vec3(garment.Positions[i * 3], garment.Positions[i * 3 + 1], garment.Positions[i * 3 + 2]);
            var nodeOf = MeshMath.WeldByPosition(vertAt, out _);

            // A part switched by an IMC variant tag may not be drawn — which of them is, is the wearer's setting, and
            // nothing here knows it. Counted, a hidden long skirt would pull the thigh's skin back from under nothing.
            uint variantBits = 0;
            for (int i = 0; i < garment.AttributeNames.Count && i < 32 && !countVariants; i++)
                if (SecondSkinWriter.IsVariantAttribute(garment.AttributeNames[i])) variantBits |= 1u << i;

            var faces = new List<(int A, int B, int C)>();
            // Which way the garment winds its outer faces: the side most of its area's vertex normals agree with. A
            // face's own vertex normals are the old body's — the refit moves positions, never normals — and disagree with
            // its winding on up to 1.2% of the faces it pulled hardest (the Witch's Coatee on LavaBod+'s Teardrop); the
            // game culls by winding.
            double agree = 0;
            foreach (var part in garment.Parts)
            {
                // Nails and piercings are the old body's, not cloth: they go when the skin does.
                if (part.Island >= 0 || SecondSkinWriter.IsBodySkinMaterial(part.Material)
                    || IsBodyExtraMaterial(part.Material) || IsBodyAdornmentMaterial(part.Material)
                    || (part.AttributeMask & variantBits) != 0) continue;
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                    var tri = (At(garment, a), At(garment, b), At(garment, c));
                    tris.Add(tri);
                    faces.Add((nodeOf[a], nodeOf[b], nodeOf[c]));
                    var wind = Vector3.Cross(tri.Item2 - tri.Item1, tri.Item3 - tri.Item1);
                    outward.Add(wind);
                    agree += Vector3.Dot(wind, NormalOf(garment, a) + NormalOf(garment, b) + NormalOf(garment, c));
                }
            }
            if (agree < 0)
                for (int i = 0; i < outward.Count; i++) outward[i] = -outward[i];

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

        private static Vector3 NormalOf(ModelParts m, int v)
            => v * 3 + 2 < m.Normals.Length ? new Vector3(m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2])
                                            : Vector3.Zero;

        /// <summary>
        /// Whether, looking from <paramref name="p"/> along <paramref name="into"/>, the first of this garment's cloth
        /// within <paramref name="reach"/> is seen from inside: its outer side turned away. Skin missing there would show
        /// the garment's inside, which the game does not draw — a slit. Nothing within reach, or a face with no area, is
        /// not inside.
        /// </summary>
        public bool SeesInside(Vector3 p, Vector3 into, float reach)
            => FirstHit(p, into, reach) is { } hit && Vector3.Dot(outward[hit.Index], Vector3.Normalize(into)) > 0f;

        /// <summary>
        /// The first cloth face the line from <paramref name="p"/> along <paramref name="into"/> crosses within
        /// <paramref name="reach"/>, and how far along: walked cell by cell through the grid (Amanatides and Woo), and done
        /// once a crossing lies before the far side of the cell being read — a face crossed there sits in a cell already
        /// read. Null when none does.
        /// </summary>
        internal (int Index, float T)? FirstHit(Vector3 p, Vector3 into, float reach)
        {
            if (into.LengthSquared() < 1e-12f) return null;
            var d = Vector3.Normalize(into);
            var (x, y, z) = CellOf(p);
            int stepX = d.X > 0 ? 1 : -1, stepY = d.Y > 0 ? 1 : -1, stepZ = d.Z > 0 ? 1 : -1;
            float Next(float pos, float dir, int cell)
                => dir == 0f ? float.PositiveInfinity : ((dir > 0 ? cell + 1 : cell) * Cell - pos) / dir;
            float tX = Next(p.X, d.X, x), tY = Next(p.Y, d.Y, y), tZ = Next(p.Z, d.Z, z);
            float dX = d.X == 0f ? float.PositiveInfinity : Cell / MathF.Abs(d.X);
            float dY = d.Y == 0f ? float.PositiveInfinity : Cell / MathF.Abs(d.Y);
            float dZ = d.Z == 0f ? float.PositiveInfinity : Cell / MathF.Abs(d.Z);
            var tried = new HashSet<int>();
            int best = -1;
            float bestT = float.MaxValue, entered = 0f;
            while (entered <= reach)
            {
                if (grid.TryGetValue((x, y, z), out var bucket))
                    foreach (int i in bucket)
                    {
                        if (!tried.Add(i)) continue;
                        var (a, b, c) = tris[i];
                        if (LineHits(p, d, a, b, c, out float t) && t <= reach && t < bestT
                            && outward[i].LengthSquared() > 0f)
                        {
                            bestT = t;
                            best = i;
                        }
                    }
                float exit = MathF.Min(tX, MathF.Min(tY, tZ));
                if (best >= 0 && bestT <= exit) break;
                entered = exit;
                if (tX <= tY && tX <= tZ) { x += stepX; tX += dX; }
                else if (tY <= tZ) { y += stepY; tY += dY; }
                else { z += stepZ; tZ += dZ; }
            }
            return best >= 0 ? (best, bestT) : null;
        }

        /// <summary>Whether cloth crosses the line through <paramref name="p"/> along <paramref name="normal"/> between
        /// <see cref="PullBehind"/> behind it and <see cref="PullClear"/> in front (<see cref="PullClearRim"/> for a rim's
        /// faces).</summary>
        public bool Crowds(Vector3 p, Vector3 normal) => Deepest(p, normal) != null;

        /// <summary>Whether cloth crosses the line through <paramref name="p"/> along <paramref name="normal"/> between
        /// <paramref name="behind"/> behind it and <paramref name="ahead"/> in front — the same for every face, a rim's
        /// included (see <see cref="TuckClear"/>).</summary>
        public bool Crowds(Vector3 p, Vector3 normal, float ahead, float behind)
        {
            if (normal.LengthSquared() < 1e-12f) return false;
            var d = Vector3.Normalize(normal);
            var from = p - d * behind;
            var to = p + d * ahead;
            var (x0, y0, z0) = CellOf(Vector3.Min(from, to));
            var (x1, y1, z1) = CellOf(Vector3.Max(from, to));
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int i in bucket)
                {
                    var (a, b, c) = tris[i];
                    if (LineHits(from, d, a, b, c, out float t) && t <= behind + ahead) return true;
                }
            }
            return false;
        }

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

        /// <summary>Whether cloth crosses the line from <paramref name="p"/> out along <paramref name="normal"/> within
        /// <paramref name="reach"/> — whether something lying there is under it (see <see cref="AdornmentsOf"/>).</summary>
        public bool Over(Vector3 p, Vector3 normal, float reach)
        {
            if (normal.LengthSquared() < 1e-12f) return false;
            var d = Vector3.Normalize(normal);
            var to = p + d * reach;
            var (x0, y0, z0) = CellOf(Vector3.Min(p, to));
            var (x1, y1, z1) = CellOf(Vector3.Max(p, to));
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int i in bucket)
                {
                    var (a, b, c) = tris[i];
                    if (LineHits(p, d, a, b, c, out float t) && t <= reach) return true;
                }
            }
            return false;
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
        return ChordUnder(drawn, hit, edges) is float chord && hit.Distance <= MathF.Min(CutSagMax, CutSagShare * chord);
    }

    /// <summary>
    /// The chord of the garment's laid skin a point lands under: the triangle when it lands inside one, the edge when it
    /// lands on an edge two triangles share — see <see cref="Covers"/>. Null on the author's boundary (an edge one
    /// triangle uses) or on a corner, which sits on the body.
    /// </summary>
    internal static float? ChordUnder(BodySurface drawn, BodySurface.Hit hit, SkinEdges? edges)
    {
        const float inside = 0.01f;   // a landing clamped onto an edge has a zero weight; this is off it
        bool offA = hit.U < inside, offB = hit.V < inside, offC = hit.W < inside;
        Vector3 a = drawn.PositionOf(hit.A), b = drawn.PositionOf(hit.B), c = drawn.PositionOf(hit.C);

        if (!offA && !offB && !offC)
            return MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
        if (offA && !offB && !offC && edges?.IsShared(hit.B, hit.C) == true)
            return Vector3.Distance(b, c);
        if (offB && !offA && !offC && edges?.IsShared(hit.C, hit.A) == true)
            return Vector3.Distance(c, a);
        if (offC && !offA && !offB && edges?.IsShared(hit.A, hit.B) == true)
            return Vector3.Distance(a, b);
        return null;
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

        private SkinEdges(ModelParts m, bool smallclothes)
        {
            int vc = m.Positions.Length / 3;
            var pos = new Vec3[vc];
            for (int v = 0; v < vc; v++) pos[v] = new Vec3(m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
            nodeOf = MeshMath.WeldByPosition(pos, out _);

            var seen = new HashSet<(int, int)>();
            foreach (var part in m.Parts)
            {
                if (part.Island >= 0) continue;
                if (!SecondSkinWriter.IsBodySkinMaterial(part.Material)
                    && !(smallclothes && BodySurface.IsSmallclothes(part.Material))) continue;
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

        internal static SkinEdges Of(ModelParts m) => new(m, smallclothes: false);

        /// <summary>The edges of a body as it is worn bare: its skin and its own smallclothes together (see
        /// <see cref="BodySurface.OfBodyWorn"/>).</summary>
        internal static SkinEdges OfWorn(ModelParts m) => new(m, smallclothes: true);

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
