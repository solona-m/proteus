using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// Refit a garment authored for one body onto another — another size of the same mesh, or any body sharing its texture
/// layout (see <see cref="BodyCorrespondence"/> for which point of one body is which point of the other).
/// <para/>
/// Pure geometry: no files, no mods, no game. Everything is decided per WELDED NODE and spread to vertices at the very
/// end, because moving one copy of a uv seam and not its twin opens a crack.
/// <para/>
/// Tangents are deliberately not refitted. A tangent frame is defined by the uv parameterisation, and a retarget moves
/// positions only — never uvs, never topology, never the vertex count — so the frame's direction in uv space is still
/// exactly right and only its projection onto the slightly-turned surface is stale, which the shader's
/// re-orthogonalisation against the (recomputed) normal largely absorbs. Refitting would mean a full mesh re-emit and
/// giving up the length-neutral in-place rewrite that makes this safe to do to somebody else's mod. Revisit for Rue+,
/// where the deformation is large and anisotropic.
/// </summary>
internal static partial class BodyRetarget
{
    /// <summary>Inside this, cloth is resting on the body and follows it exactly (40 mm: past the thickest lining,
    /// jacket and padding).</summary>
    internal const float NearBand = 0.04f;

    /// <summary>Past this, cloth is hanging free and does not move at all (250 mm).</summary>
    internal const float FarBand = 0.25f;

    /// <summary>
    /// Buckets to the metre for the exact-match snap: 0.1 mm, the same tolerance <see cref="ModelPartReader"/> already
    /// welds islands at, so "coincident" means one thing across the codebase. Not the welder's 10 µm — positions may
    /// be stored as Half4 in one model and Float3 in the other, and half at body scale quantises well past that, so
    /// 10 µm would miss most of a mesh that genuinely IS a copy.
    /// </summary>
    internal const float SnapPerMetre = 1e4f;

    /// <inheritdoc cref="SnapPerMetre"/>
    internal const float SnapEps = 1e-4f;

    /// <summary>
    /// How far a garment vertex may be from the target body and still be considered for a push-out (30 mm).
    /// <para/>
    /// Scoped on purpose, and the scope is the point: this pass fixes cloth that GRAZES the body, not cloth buried
    /// five centimetres inside a torso. A vertex that deep was broken before the retarget ran, and inventing a 50 mm
    /// push for it would do more damage than the poke-through does. It also keeps the winding-number query — which
    /// walks every far cell — off the great majority of nodes.
    /// </summary>
    internal const float PushProbeRange = 0.03f;

    /// <summary>
    /// How far outside the target body a pushed cloth vertex is put (0.5 mm).
    /// <para/>
    /// This feature's own constant, measured for CLOTH OVER SKIN. Explicitly not <c>SecondSkinWriter.BaseOffset</c>,
    /// which is 0.05 mm and was measured for a shell cut from the body and sitting on its own normal — a different
    /// pass, whose number is height-banded for the foot on top of that.
    /// </summary>
    internal const float Clearance = 5e-4f;

    /// <summary>Rounds of slope-limited spreading, so the push has no step where it stops.</summary>
    internal const int PushSpreadRounds = 8;

    /// <summary>Gradient the spread allows, as a rise over the mesh's own edge length (~27 degrees).</summary>
    internal const float PushSlope = 0.5f;

    /// <summary>One slot of the body: the correspondence that says where its skin went, and the target to land on.</summary>
    /// <param name="TargetModel">The target body's file, which swapping the garment's skin copies the body's skin out
    /// of (see <see cref="SwapSkin"/>) and rewriting its weights reads the new body's weights from (see
    /// <see cref="PlanWeights"/>). Null when only the geometry is wanted.</param>
    /// <param name="SourceModel">The source body's file: which bones the OLD body rigs, so a weight rewrite knows which
    /// of the garment's bones are body bones. Null when only the geometry is wanted.</param>
    internal readonly record struct SlotPair(string Slot, IBodyCorrespondence Correspondence, ModelParts Target,
                                             byte[]? TargetModel = null, byte[]? SourceModel = null);

    /// <summary>What happened, for the status line and the saved record.</summary>
    /// <param name="Held">Welded points the user held in place, by unticking their parts.</param>
    /// <param name="Laid">Skin points laid exactly onto the new body — see <see cref="LaySkin"/>.</param>
    /// <param name="Swap">What swapping the garment's skin for the body's did; null when it did not run.</param>
    internal sealed record Report(
        int Nodes, int Snapped, int Transferred, int Missed, int Pushed,
        float WorstMove, float WorstPush, int UnmappedSpares, bool HasOtherLods, int Held = 0, int Laid = 0,
        SwapReport? Swap = null)
    {
        /// <summary>Share of moved nodes that landed on a body vertex exactly. Low means the author sculpted the
        /// garment's body mesh rather than copying it, and the seam may not come out perfect.</summary>
        public float SnapRate => Transferred > 0 ? (float)Snapped / Transferred : 0f;
    }

    /// <summary>The retarget, ready to preview or save.</summary>
    internal sealed record Planned(RetargetEdit Edit, byte[] Model, Report Report);

    /// <summary>
    /// The geometry half of a retarget, with no file anywhere near it. Separated from <see cref="Plan"/> so the solve
    /// can be tested against a hand-built <see cref="ModelParts"/> at real body scale, rather than only through a
    /// synthetic .mdl whose triangles are a metre across.
    /// </summary>
    internal sealed record Solved(RetargetEdit Edit, int Snapped, int Transferred, int Missed, int Pushed,
                                  float WorstMove, float WorstPush, int Held = 0, int Laid = 0);

    /// <summary>
    /// The garment split into the two sets the two passes act on.
    /// <para/>
    /// THE ONLY place in the retarget that asks <see cref="SecondSkinWriter.IsBodySkinMaterial"/>, because the two
    /// passes need OPPOSITE answers and a shared flag read twice is exactly how that gets inverted by accident:
    /// <list type="bullet">
    /// <item>TRANSFER moves every node, <see cref="ClothNodes"/> and embedded skin alike — the garment's own body mesh
    /// has to land on the new body exactly, or the body pokes through the cloth.</item>
    /// <item>PUSH-OUT moves <see cref="ClothNodes"/> only — shoving the embedded skin mesh off the body it is meant to
    /// coincide with would lift it clear and open a seam.</item>
    /// </list>
    /// So neither pass takes a flag and decides. Each pass is handed the node list it acts on, and neither pass body
    /// mentions skin at all.
    /// </summary>
    internal sealed class Sets
    {
        /// <summary>Which welded node each vertex belongs to, indexed like <see cref="ModelParts.Positions"/>.</summary>
        public required int[] NodeOf { get; init; }

        public required int NodeCount { get; init; }

        /// <summary>Each node's position as its author left it.</summary>
        public required Vec3[] NodeAt { get; init; }

        /// <summary>Each node's normal as its author left it.</summary>
        public required Vec3[] NodeNormal { get; init; }

        /// <summary>Node neighbours through shared triangle edges.</summary>
        public required List<int>[] Adj { get; init; }

        /// <summary>The mesh's own resolution, which the push-out's slope limit is expressed in.</summary>
        public required float MeanEdge { get; init; }

        /// <summary>Every triangle of every whole submesh, as vertex indices.</summary>
        public required int[] Tris { get; init; }

        /// <summary>The transfer's set: every node the user has not held.</summary>
        public required int[] AllNodes { get; init; }

        /// <summary>The push-out's set: every node that is neither the garment's own body mesh nor held.</summary>
        public required int[] ClothNodes { get; init; }

        /// <summary>
        /// Nodes the user has HELD — parts unticked in the Studio's list — which neither pass moves, and which are in
        /// neither set above. Held per welded node, the brush's rule: a point shared by a held part and a moving one is
        /// held, so the two cannot come apart where they meet.
        /// </summary>
        public required int HeldCount { get; init; }

        /// <summary>The garment's own body mesh, not held: what laying the skin onto the new body acts on — see
        /// <see cref="BodyRetarget.LaySkin"/>.</summary>
        public required int[] SkinNodes { get; init; }

        /// <param name="held">Vertices of the parts the user has held; null or empty for none.</param>
        public static Sets From(ModelParts garment, IReadOnlySet<int>? held = null)
        {
            int vc = garment.Positions.Length / 3;
            var vertAt = new Vec3[vc];
            for (int i = 0; i < vc; i++)
                vertAt[i] = new Vec3(garment.Positions[i * 3], garment.Positions[i * 3 + 1], garment.Positions[i * 3 + 2]);

            var nodeOf = MeshMath.WeldByPosition(vertAt, out int nodeCount);

            var tris = new List<int>();
            var isSkin = new bool[nodeCount];
            foreach (var part in garment.Parts)
            {
                // An island is a subset of its own submesh; taking both would double every triangle, and would also
                // let an island of a cloth submesh disagree with the submesh about what it is.
                if (part.Island >= 0) continue;
                tris.AddRange(part.Triangles);
                if (!SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                foreach (int v in part.Triangles)
                    if (v >= 0 && v < vc) isSkin[nodeOf[v]] = true;
            }

            var nodeAt = new Vec3[nodeCount];
            for (int i = 0; i < vc; i++) nodeAt[nodeOf[i]] = vertAt[i];

            var accum = new Vec3[nodeCount];
            for (int i = 0; i < vc && i * 3 + 2 < garment.Normals.Length; i++)
            {
                int n = nodeOf[i];
                accum[n] = new Vec3(accum[n].X + garment.Normals[i * 3],
                                    accum[n].Y + garment.Normals[i * 3 + 1],
                                    accum[n].Z + garment.Normals[i * 3 + 2]);
            }
            var nodeNormal = new Vec3[nodeCount];
            for (int n = 0; n < nodeCount; n++) nodeNormal[n] = Unit(accum[n]);

            var adj = new List<int>[nodeCount];
            for (int n = 0; n < nodeCount; n++) adj[n] = [];
            var triArray = tris.ToArray();
            for (int t = 0; t + 2 < triArray.Length; t += 3)
            {
                int a = triArray[t], b = triArray[t + 1], c = triArray[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vc || b >= vc || c >= vc) continue;
                Link(adj, nodeOf[a], nodeOf[b]);
                Link(adj, nodeOf[b], nodeOf[c]);
                Link(adj, nodeOf[c], nodeOf[a]);
            }

            var isHeld = new bool[nodeCount];
            if (held != null)
                foreach (int v in held)
                    if (v >= 0 && v < vc) isHeld[nodeOf[v]] = true;

            var every = new int[nodeCount];
            for (int n = 0; n < nodeCount; n++) every[n] = n;

            var all = new List<int>(nodeCount);
            var cloth = new List<int>(nodeCount);
            var skin = new List<int>();
            int heldCount = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                if (isHeld[n]) { heldCount++; continue; }
                all.Add(n);
                if (isSkin[n]) skin.Add(n);
                else cloth.Add(n);
            }

            return new Sets
            {
                NodeOf = nodeOf,
                NodeCount = nodeCount,
                NodeAt = nodeAt,
                NodeNormal = nodeNormal,
                Adj = adj,
                MeanEdge = MeshMath.MeanEdgeLength(nodeAt, adj, new List<int>(every)),
                Tris = triArray,
                AllNodes = all.ToArray(),
                ClothNodes = cloth.ToArray(),
                HeldCount = heldCount,
                SkinNodes = skin.ToArray(),
            };
        }

        private static void Link(List<int>[] adj, int a, int b)
        {
            if (a == b) return;
            if (!adj[a].Contains(b)) adj[a].Add(b);
            if (!adj[b].Contains(a)) adj[b].Add(a);
        }
    }

    /// <summary>
    /// Refit <paramref name="garment"/> from the source bodies to the target bodies, and write the result into
    /// <paramref name="garmentBytes"/>.
    /// </summary>
    /// <param name="garment">The garment, already read.</param>
    /// <param name="garmentBytes">The bytes it was read from — the rewrite is in place and length-neutral.</param>
    /// <param name="pairs">One per body slot the garment spans: chest, legs, and whatever else a body mod sizes.</param>
    /// <param name="garmentSlot">The slot the garment is worn in ("_top" for a top). Its body is excluded from the
    /// push-out, because the garment is drawn in its place — see <see cref="TargetBody"/>. Null keeps every body.</param>
    /// <param name="pushOut">Whether to run the push-out pass after the transfer.</param>
    /// <param name="held">Vertices of the parts the user unticked, which stay exactly where the author put them — see
    /// <see cref="Sets.HeldCount"/>. Null for none.</param>
    /// <param name="replaceSkin">Replace the garment's own body skin with the new body's: laid onto the new body first
    /// (see <see cref="LaySkin"/>) so the push-out measures against the right surface, then swapped for the body's own
    /// skin, slot by slot, for every pair that carries its body's file (see <see cref="SwapSkin"/>).</param>
    /// <param name="acrossBodies">The garment is going from one body MOD to another, rather than between sizes of one:
    /// its cloth then always takes the new body's weights, and the old body's piercings and pubic hair are left out,
    /// even when the two bodies' rigs name the same bones (YAB's and Rue's plain sizes do) — see
    /// <see cref="PlanWeights"/>.</param>
    public static Planned Plan(ModelParts garment, byte[] garmentBytes, IReadOnlyList<SlotPair> pairs,
                               string? garmentSlot = null, bool pushOut = true, IReadOnlySet<int>? held = null,
                               bool replaceSkin = false, bool acrossBodies = false)
    {
        var solved = Solve(garment, pairs, garmentSlot, pushOut, held, replaceSkin);
        var written = MeshVolumeService.Inflate(garmentBytes, solved.Edit);
        byte[] model = written.Model;

        // Across rigs the cloth takes the new body's weights; with the skin replaced, the body's skin meshes come in.
        // Both are one rebuild, and nothing is rebuilt when neither applies — a same-rig refit with the skin kept stays
        // the in-place rewrite above.
        SwapReport? swap = null;
        var weights = PlanWeights(model, pairs, acrossBodies);
        if ((replaceSkin || weights != null) && Rebuild(model, pairs, replaceSkin, weights, out var swapped) is { } rebuilt)
        {
            model = rebuilt;
            swap = swapped;
        }

        var report = new Report(garment.Positions.Length / 3, solved.Snapped, solved.Transferred, solved.Missed,
                                solved.Pushed, solved.WorstMove, solved.WorstPush,
                                written.UnmappedSpares, written.HasOtherLods, solved.Held, solved.Laid, swap);
        return new Planned(solved.Edit, model, report);
    }

    /// <inheritdoc cref="Plan"/>
    /// <remarks>The geometry, without touching the file. See <see cref="Solved"/>.</remarks>
    internal static Solved Solve(ModelParts garment, IReadOnlyList<SlotPair> pairs, string? garmentSlot = null,
                                 bool pushOut = true, IReadOnlySet<int>? held = null, bool replaceSkin = false)
    {
        var sets = Sets.From(garment, held);
        var source = SourceBody.Build(pairs);

        var nodeDelta = new Vec3[sets.NodeCount];
        var snapped = new bool[sets.NodeCount];

        Transfer(sets, sets.AllNodes, source, nodeDelta, snapped, out int transferred, out int missed);

        // Before the push-out, so the push-out measures cloth against the skin as it will actually be drawn.
        int laid = replaceSkin ? LaySkin(sets, source, pairs, nodeDelta) : 0;

        int pushed = 0;
        float worstPush = 0f;
        if (pushOut)
        {
            // The skin drawn under the cloth once it is worn, as authored and after the refit: the garment's own body
            // mesh where it has one, and the other slots' bodies. See TargetBody, and PushOut for why both are needed.
            bool hasSkin = sets.ClothNodes.Length < sets.NodeCount;
            var before = TargetBody.Build(pairs, garmentSlot, hasSkin ? garment : null, before: true);
            var after = TargetBody.Build(pairs, garmentSlot, hasSkin ? Moved(garment, sets, nodeDelta) : null,
                                         before: false);

            var pushable = new List<int>(sets.ClothNodes.Length);
            foreach (int n in sets.ClothNodes)
                if (!snapped[n]) pushable.Add(n);

            pushed = PushOut(sets, pushable, before, after, nodeDelta, out worstPush);
        }

        int vc = garment.Positions.Length / 3;
        var vertDelta = new Vec3[vc];
        for (int i = 0; i < vc; i++) vertDelta[i] = nodeDelta[sets.NodeOf[i]];

        float worstMove = 0f;
        for (int n = 0; n < sets.NodeCount; n++)
            worstMove = MathF.Max(worstMove, Len(nodeDelta[n]));

        // The author's normals, untouched: every normal zero means "leave it" to Inflate. Recomputing them from the
        // moved surface — relaxed, averaged across welded seams, blended toward the body's under laid skin — broke the
        // shading of authored hard edges and custom normals: a shirt sleeve came out blotched dark and light. A refit
        // moves the cloth along with the body; the author's normals describe the cloth, and stay.
        var vertNrm = new Vec3[vc];

        var edit = new RetargetEdit(garment.MeshSpans, vertDelta, vertNrm);
        return new Solved(edit, CountTrue(snapped), transferred, missed, pushed, worstMove, worstPush, sets.HeldCount,
                          laid);
    }

    /// <summary>
    /// Skin this close to the source body is the body's skin, and is laid onto the new body completely (10 mm) — an
    /// author's reshaping under a garment sits inside it: "This Old Thing" lifts its chest by up to 8 mm.
    /// </summary>
    internal const float LayFull = 0.01f;

    /// <summary>
    /// Skin this far off is not the body's surface at all and keeps the refit's answer; between the two the laying fades
    /// out smoothly, so there is no crease where laid skin meets skin that was left (20 mm). It was once a hard 30 mm,
    /// and that caught a piece of Seaside's body mesh sitting 29 mm in front of the belly — a separate skin piece its
    /// author never moves between sizes — and flattened it onto the belly, deforming the whole front.
    /// </summary>
    internal const float LayReach = 0.02f;

    /// <summary>
    /// Replace the garment's own body skin with the new body's: put every skin point exactly ON the target body, at the
    /// place that corresponds to where it sits on the source body.
    /// <para/>
    /// This is the difference between resizing the skin a mod came with and swapping it for the body's. The transfer
    /// carries each point along with the body, so an author's reshaping of the skin — a top that lifts or compresses the
    /// chest — survives at the new size. Laying it drops that offset: the garment's skin sits on the new body's surface.
    /// Positions only — the author's normals are never touched — so the file keeps its exact size and layout and the
    /// rewrite stays safe to do to somebody else's mod; the garment's own triangles, uvs and weights are kept.
    /// </summary>
    /// <returns>How many skin nodes were laid.</returns>
    private static int LaySkin(Sets sets, SourceBody source, IReadOnlyList<SlotPair> pairs, Vec3[] nodeDelta)
    {
        var targets = new List<BodySurface>(pairs.Count);
        foreach (var pair in pairs)
        {
            var surface = new BodySurface(pair.Target, BodySurface.CellFor(MeanEdgeOf(pair.Target)));
            if (!surface.IsEmpty) targets.Add(surface);
        }

        int laid = 0;
        foreach (int n in sets.SkinNodes)
        {
            var p = ToVector(sets.NodeAt[n]);
            if (!source.TryLand(p, LayReach, out var q, out float offBody)) continue;
            float w = 1f - MeshMath.Smoothstep((offBody - LayFull) / (LayReach - LayFull));
            if (w <= 0f) continue;

            // Onto the target surface itself. The landing is already on it for two sizes of one mesh; by texture
            // coordinate it is within a whisker, and this closes the whisker.
            BodySurface.Hit best = default;
            bool onBody = false;
            float within = 0.005f;
            foreach (var surface in targets)
            {
                if (!surface.Nearest(q, within, out var hit)) continue;
                best = hit;
                within = hit.Distance;
                onBody = true;
            }

            // Between LayFull and LayReach, part way from the refit's answer to the body.
            var at = onBody ? best.Point : q;
            var carried = p + ToVector(nodeDelta[n]);
            nodeDelta[n] = ToVec(carried + (at - carried) * w - p);
            laid++;
        }
        return laid;
    }

    private static int CountTrue(bool[] flags)
    {
        int n = 0;
        foreach (bool f in flags)
            if (f) n++;
        return n;
    }

    internal static float Len(Vec3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static Vec3 Unit(Vec3 v)
    {
        float len = Len(v);
        return len > 1e-6f ? new Vec3(v.X / len, v.Y / len, v.Z / len) : default;
    }

    internal static Vector3 ToVector(Vec3 v) => new(v.X, v.Y, v.Z);

    internal static Vec3 ToVec(Vector3 v) => new(v.X, v.Y, v.Z);
}
