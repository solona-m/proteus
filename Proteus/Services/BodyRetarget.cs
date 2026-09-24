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
    /// How near two bodies have to be for a point to follow both, blended (2 cm). The slots overlap where they meet —
    /// the foot's body and the leg's share the ankle — and a garment crossing that overlap tore when each point took
    /// only the nearer one's answer. Past this, the nearest body alone, exactly as before.
    /// </summary>
    internal const float SlotBlend = 0.02f;

    /// <summary>
    /// How far a hole in the displacement field is filled in from its edge, in rings of the body's own triangles. Eight
    /// covers the holes a texture-coordinate correspondence leaves — a seam, an island cut differently — without
    /// inventing a field across a region neither body shares.
    /// </summary>
    internal const int HoleRounds = 8;

    /// <summary>
    /// How far one body vertex's displacement may differ from its neighbours' average before it is taken for a mistake
    /// in the correspondence rather than the shape (4 mm). Two bodies differ smoothly across a surface; a vertex that
    /// disagrees with everything around it by more than this landed somewhere it does not belong.
    /// </summary>
    internal const float DespikeGap = 0.004f;

    /// <summary>
    /// How many times its neighbourhood's own spread a vertex has to disagree by before its displacement is taken for a
    /// mistake (3x). A field that varies quickly everywhere is the shape changing; one vertex out of step with
    /// neighbours that agree with each other is the correspondence having gone astray.
    /// </summary>
    internal const float DespikeOutlier = 3f;

    /// <summary>How many rounds of smoothing the displacement field gets, and how far each moves a vertex toward its
    /// neighbours' average. Light on purpose: enough to take the correspondence's noise off a seam, not enough to move
    /// the shape change itself.</summary>
    internal const int SmoothRounds = 2;

    /// <inheritdoc cref="SmoothRounds"/>
    internal const float SmoothRate = 0.5f;

    /// <summary>How many rounds the refit knits neighbouring points together, and how far each moves a point toward its
    /// neighbours' answer. Enough to close a tear the nearest-point search opens, little enough to leave the shape the
    /// body asked for.</summary>
    internal const int KnitRounds = 2;

    /// <inheritdoc cref="KnitRounds"/>
    internal const float KnitRate = 0.5f;

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
    /// How deep in the body cloth may be for "push the garment clear of the body" to pull it out (8 mm). Deeper than
    /// this it is the garment's own structure rather than a clip — an inner layer, a sole — and it is left alone.
    /// </summary>
    internal const float ClearDepth = 0.008f;

    /// <summary>Rounds of halving the push-out's fold sweep gets. Each quarters the worst case, so eight is a factor
    /// of 256 — past any push this pass can ask for.</summary>
    private const int PushUnfoldPasses = 8;

    /// <summary>Rounds the fold relax gets (see <see cref="Unfold"/>), and how far each one takes a corner toward
    /// what its neighbours were given.</summary>
    private const int UnfoldRounds = 24;

    private const float UnfoldRate = 0.5f;

    /// <summary>
    /// How far the push-out looks for the skin when deciding whether cloth was authored INSIDE it (15 cm). Cloth tucked
    /// under a body can sit far inside it — a heeled shoe's foot is drawn where the body's flat foot is — and at
    /// <see cref="PushProbeRange"/> such a point read as "nowhere near skin", which the pass took for outside.
    /// </summary>
    internal const float AuthoredProbeRange = 0.15f;

    /// <summary>
    /// How far outside the target body a pushed cloth vertex is put (1 mm).
    /// <para/>
    /// This feature's own constant, measured for CLOTH OVER SKIN. Explicitly not <c>SecondSkinWriter.BaseOffset</c>,
    /// which is 0.05 mm and was measured for a shell cut from the body and sitting on its own normal — a different
    /// pass, whose number is height-banded for the foot on top of that.
    /// <para/>
    /// It is a CAP on the author's own standoff, not a target: the push restores <c>min(authored, this)</c>. At 0.5 mm
    /// a corset whose author held the cup 2 mm off the skin got a quarter of that back and still read as tight against
    /// the breast. At 1 mm the same refit leaves 2 cup vertices inside the skin, which is what the author's own file
    /// has, and the ground truth against "This Old Thing"'s hand-fitted sizes does not move at all.
    /// </summary>
    internal const float Clearance = 1e-3f;

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
    /// <param name="TargetHidden">The target body's variant tags its mod does not draw (see <see cref="UndrawnVariants"/>).
    /// <paramref name="Target"/> is already without those parts; this carries the same choice to the swap, which copies
    /// the body's skin out of <paramref name="TargetModel"/>. Null when every part is drawn.</param>
    internal readonly record struct SlotPair(string Slot, IBodyCorrespondence Correspondence, ModelParts Target,
                                             byte[]? TargetModel = null, byte[]? SourceModel = null,
                                             IReadOnlySet<string>? TargetHidden = null);

    /// <summary>What happened, for the status line and the saved record.</summary>
    /// <param name="Held">Welded points the user held in place, by unticking their parts.</param>
    /// <param name="Laid">Skin points laid exactly onto the new body — see <see cref="LaySkin"/>.</param>
    /// <param name="Swap">What swapping the garment's skin for the body's did; null when it did not run.</param>
    /// <param name="Folded">Triangles the refit left facing the wrong way — see <see cref="Unfold"/>.</param>
    internal sealed record Report(
        int Nodes, int Snapped, int Transferred, int Missed, int Pushed,
        float WorstMove, float WorstPush, int UnmappedSpares, bool HasOtherLods, int Held = 0, int Laid = 0,
        SwapReport? Swap = null, int Folded = 0)
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
    /// <param name="Folded">Triangles still facing the wrong way when <see cref="Unfold"/> ran out of rounds. Any at
    /// all is worth saying: a folded triangle is a black speck on the garment.</param>
    internal sealed record Solved(RetargetEdit Edit, int Snapped, int Transferred, int Missed, int Pushed,
                                  float WorstMove, float WorstPush, int Held = 0, int Laid = 0, int Folded = 0);

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

        /// <summary>
        /// How close two points of DIFFERENT pieces have to be for the refit to move them as one (1 mm).
        /// <para/>
        /// <see cref="MeshMath.WeldByPosition"/> groups points within about 10 um, which asks them to be coincident to
        /// the last bit — and bucketed, so two points a micron apart never join if a bucket boundary runs between
        /// them. A garment author does not build to that: the boning strips of a corset merely TOUCH the panels beside
        /// them. Measured on "BiboPlus Sheer Elegance", where the pieces meet at a third of a millimetre rather than
        /// at zero: 804 pairs within half a millimetre of each other were moved two different ways, opening seams of
        /// up to 6 mm down the boning.
        /// <para/>
        /// Wide, and it can be wide because it only ever joins points of DIFFERENT pieces. A tolerance this size
        /// applied within one piece would weld a panel to itself across its own thickness and stop it deforming; a
        /// panel's interior needs no help, because it is connected geometry the knit already holds together. What
        /// cracks is the join BETWEEN pieces, and nothing else is touched.
        /// </summary>
        internal const float SeamWeld = 1e-3f;

        /// <summary>
        /// The same, within ONE piece (0.2 mm): near-exact, and there only to catch what the bucketed weld misses —
        /// two points a micron apart with a bucket boundary between them. A piece's interior is connected geometry the
        /// knit already holds, so it needs nothing wider, and giving it <see cref="SeamWeld"/> would weld a panel
        /// across its own thickness.
        /// </summary>
        internal const float SeamWeldWithinPiece = 2e-4f;

        /// <summary>
        /// The widest a locked node may end up (2 mm).
        /// <para/>
        /// Joining is transitive, so without a cap it has no scale of its own: a and b within a millimetre, b and c
        /// within a millimetre, and a and c are two apart — and a seam running between alternating boning strips and
        /// panels chains further still. Everything downstream treats a node as a POINT: the push-out probes the body
        /// at it and takes its push direction there, and the fold tests judge winding from it. A node's spread is
        /// therefore error, and it may not grow unbounded just because the joins are each small.
        /// </summary>
        internal const float MaxLockedSpan = 2e-3f;

        /// <summary>
        /// Join nodes the author left touching, so an edge shared by two pieces of the garment moves as one and the
        /// seam between them cannot open.
        /// <para/>
        /// After the exact weld rather than instead of it: this only ever merges nodes further, so a garment built to
        /// the tighter tolerance is unaffected. Skin is never merged with cloth — see the caller.
        /// </summary>
        /// <param name="pieceOf">Which piece each VERTEX belongs to; only points of different pieces are joined.</param>
        /// <param name="nodeOf">Rewritten in place to the compacted numbering.</param>
        /// <param name="isSkin">Rewritten to match; a merged node is skin only if the nodes making it up were.</param>
        /// <returns>How many nodes there now are.</returns>
        private static int LockSeams(Vec3[] vertAt, int[] pieceOf, int[] nodeOf, int nodeCount, ref bool[] isSkin)
        {
            var parent = new int[nodeCount];
            for (int n = 0; n < nodeCount; n++) parent[n] = n;
            int Find(int n)
            {
                while (parent[n] != n) n = parent[n] = parent[parent[n]];
                return n;
            }

            // Each set's extent, kept on its root, so a join that would spread a node past MaxLockedSpan is refused.
            var lo = new Vec3[nodeCount];
            var hi = new Vec3[nodeCount];
            for (int n = 0; n < nodeCount; n++)
            {
                lo[n] = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                hi[n] = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            }
            for (int i = 0; i < vertAt.Length; i++)
            {
                int n = nodeOf[i];
                lo[n] = new Vec3(MathF.Min(lo[n].X, vertAt[i].X), MathF.Min(lo[n].Y, vertAt[i].Y),
                                 MathF.Min(lo[n].Z, vertAt[i].Z));
                hi[n] = new Vec3(MathF.Max(hi[n].X, vertAt[i].X), MathF.Max(hi[n].Y, vertAt[i].Y),
                                 MathF.Max(hi[n].Z, vertAt[i].Z));
            }

            // A grid at the tolerance over VERTICES, so each one only has to look at itself and the 26 cells around it.
            // Vertices rather than nodes, because the rule is about which PIECE a point came from and a node can hold
            // points of several.
            var grid = new Dictionary<(int, int, int), List<int>>(vertAt.Length);
            (int, int, int) Cell(Vec3 p) => ((int)MathF.Floor(p.X / SeamWeld),
                                             (int)MathF.Floor(p.Y / SeamWeld),
                                             (int)MathF.Floor(p.Z / SeamWeld));
            for (int i = 0; i < vertAt.Length; i++)
            {
                var key = Cell(vertAt[i]);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add(i);
            }

            float tol2 = SeamWeld * SeamWeld;
            float withinPiece2 = SeamWeldWithinPiece * SeamWeldWithinPiece;
            for (int i = 0; i < vertAt.Length; i++)
            {
                var (cx, cy, cz) = Cell(vertAt[i]);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                            foreach (int j in list)
                            {
                                if (j <= i) continue;
                                int n = nodeOf[i], m = nodeOf[j];
                                if (n == m || isSkin[n] != isSkin[m]) continue;
                                float ddx = vertAt[i].X - vertAt[j].X;
                                float ddy = vertAt[i].Y - vertAt[j].Y;
                                float ddz = vertAt[i].Z - vertAt[j].Z;
                                float reach = pieceOf[i] == pieceOf[j] ? withinPiece2 : tol2;
                                if (ddx * ddx + ddy * ddy + ddz * ddz > reach) continue;
                                int a = Find(n), b = Find(m);
                                if (a == b) continue;

                                var newLo = new Vec3(MathF.Min(lo[a].X, lo[b].X), MathF.Min(lo[a].Y, lo[b].Y),
                                                     MathF.Min(lo[a].Z, lo[b].Z));
                                var newHi = new Vec3(MathF.Max(hi[a].X, hi[b].X), MathF.Max(hi[a].Y, hi[b].Y),
                                                     MathF.Max(hi[a].Z, hi[b].Z));
                                float sx = newHi.X - newLo.X, sy = newHi.Y - newLo.Y, sz = newHi.Z - newLo.Z;
                                if (sx * sx + sy * sy + sz * sz > MaxLockedSpan * MaxLockedSpan) continue;

                                parent[a] = b;
                                lo[b] = newLo;
                                hi[b] = newHi;
                            }
                        }
            }

            // Compact, keeping the order the nodes were first seen in so the numbering stays deterministic.
            var renumbered = new int[nodeCount];
            Array.Fill(renumbered, -1);
            int next = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                int root = Find(n);
                if (renumbered[root] < 0) renumbered[root] = next++;
                renumbered[n] = renumbered[root];
            }

            var merged = new bool[next];
            for (int n = 0; n < nodeCount; n++) merged[renumbered[n]] |= isSkin[n];
            for (int i = 0; i < nodeOf.Length; i++) nodeOf[i] = renumbered[nodeOf[i]];
            isSkin = merged;
            return next;
        }

        /// <param name="held">Vertices of the parts the user has held; null or empty for none.</param>
        public static Sets From(ModelParts garment, IReadOnlySet<int>? held = null)
        {
            int vc = garment.Positions.Length / 3;
            var vertAt = new Vec3[vc];
            for (int i = 0; i < vc; i++)
                vertAt[i] = new Vec3(garment.Positions[i * 3], garment.Positions[i * 3 + 1], garment.Positions[i * 3 + 2]);

            var nodeOf = MeshMath.WeldByPosition(vertAt, out int nodeCount);

            // Which nodes are the garment's own body mesh. Worked out BEFORE the seam lock below, because that lock
            // must never fuse skin to cloth: the two passes are handed opposite node lists, and a node that is both
            // would have to be in both.
            var isSkin = new bool[nodeCount];
            foreach (var part in garment.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                foreach (int v in part.Triangles)
                    if (v >= 0 && v < vc) isSkin[nodeOf[v]] = true;
            }

            // Which separately-moving piece each vertex belongs to. The reader has already split each submesh into
            // islands by position, so its islands ARE the pieces that can crack apart from one another; a vertex in no
            // island is given its submesh, which keeps two whole submeshes lockable to each other.
            var pieceOf = new int[vc];
            Array.Fill(pieceOf, -1);
            foreach (var part in garment.Parts)               // islands first: they are the finer split
                if (part.Island >= 0)
                    foreach (int v in part.Triangles)
                        if (v >= 0 && v < vc) pieceOf[v] = (part.Mesh << 16) | (part.Island + 1);
            foreach (var part in garment.Parts)               // then whatever the islands did not claim
                if (part.Island < 0)
                    foreach (int v in part.Triangles)
                        if (v >= 0 && v < vc && pieceOf[v] < 0) pieceOf[v] = part.Mesh << 16;

            nodeCount = LockSeams(vertAt, pieceOf, nodeOf, nodeCount, ref isSkin);

            var tris = new List<int>();
            foreach (var part in garment.Parts)
            {
                // An island is a subset of its own submesh; taking both would double every triangle, and would also
                // let an island of a cloth submesh disagree with the submesh about what it is.
                if (part.Island >= 0) continue;
                tris.AddRange(part.Triangles);
            }

            // The MIDDLE of the points a node holds, not whichever of them the loop wrote last. Before the seam lock
            // they were coincident to 10 um and any of them would do; a locked node spans up to a millimetre, which is
            // the same order as the push-out's own clearance, so an arbitrary corner of it is a millimetre of error in
            // every test that treats a node as a point.
            var nodeAt = new Vec3[nodeCount];
            var points = new int[nodeCount];
            for (int i = 0; i < vc; i++)
            {
                int n = nodeOf[i];
                nodeAt[n] = new Vec3(nodeAt[n].X + vertAt[i].X, nodeAt[n].Y + vertAt[i].Y, nodeAt[n].Z + vertAt[i].Z);
                points[n]++;
            }
            for (int n = 0; n < nodeCount; n++)
                if (points[n] > 1)
                    nodeAt[n] = new Vec3(nodeAt[n].X / points[n], nodeAt[n].Y / points[n], nodeAt[n].Z / points[n]);

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
    /// <param name="held">Vertices of the parts the user unticked, which keep the author's work exactly: both where
    /// the points sit and the bones they follow — see <see cref="Sets.HeldCount"/>. Null for none.</param>
    /// <param name="replaceSkin">Replace the garment's own body skin with the new body's: laid onto the new body first
    /// (see <see cref="LaySkin"/>) so the push-out measures against the right surface, then swapped for the body's own
    /// skin, slot by slot, for every pair that carries its body's file (see <see cref="SwapSkin"/>).</param>
    /// <param name="clearBody">Push the garment clear of the body wherever it is buried in it, instead of only undoing
    /// what the refit buried. Off by default, and deliberately: cloth an author tucks under the skin is usually meant to
    /// be hidden, and pulling it out on a garment built that way makes the fit worse rather than better (measured on
    /// "This Old Thing": every node an unconditional push moved was already inside its own body as shipped). It is for
    /// the garment this rule fails: one whose own skin is drawn over the body's, where cloth left buried shows as the
    /// body poking through the fabric.</param>
    /// <param name="acrossBodies">The garment is going from one body MOD to another, rather than between sizes of one:
    /// its cloth then always takes the change between the two bodies' weights, and the old body's piercings and pubic hair are left out,
    /// even when the two bodies' rigs name the same bones (YAB's and Rue's plain sizes do) — see
    /// <see cref="PlanWeights"/>.</param>
    /// <param name="cutHidden">With <paramref name="replaceSkin"/>, leave out the new body's skin where the garment's
    /// author deleted theirs, which is usually skin under the cloth that cannot be seen — see <see cref="CutLike"/>.
    /// On by default; off puts the body's skin in whole.</param>
    public static Planned Plan(ModelParts garment, byte[] garmentBytes, IReadOnlyList<SlotPair> pairs,
                               string? garmentSlot = null, bool pushOut = true, IReadOnlySet<int>? held = null,
                               bool replaceSkin = false, bool acrossBodies = false, bool clearBody = false,
                               bool cutHidden = true)
    {
        var solved = Solve(garment, pairs, garmentSlot, pushOut, held, replaceSkin, clearBody);
        var written = MeshVolumeService.Inflate(garmentBytes, solved.Edit);
        byte[] model = written.Model;

        // Across rigs the cloth takes the new body's weights; with the skin replaced, the body's skin meshes come in.
        // Both are one rebuild, and nothing is rebuilt when neither applies — a same-rig refit with the skin kept stays
        // the in-place rewrite above.
        SwapReport? swap = null;
        var weights = PlanWeights(model, pairs, acrossBodies, before: garmentBytes, held: held);
        if (replaceSkin || weights != null)
        {
            var rebuilt = Rebuild(model, pairs, replaceSkin, weights, out var swapped, cutHidden);
            if (rebuilt != null)
            {
                model = rebuilt;
                swap = swapped;
            }
            else if (swapped.Kept > 0)
            {
                // Nothing was rebuilt, but the skin the swap left alone is worth reporting.
                swap = swapped;
            }
        }

        var report = new Report(garment.Positions.Length / 3, solved.Snapped, solved.Transferred, solved.Missed,
                                solved.Pushed, solved.WorstMove, solved.WorstPush,
                                written.UnmappedSpares, written.HasOtherLods, solved.Held, solved.Laid, swap,
                                solved.Folded);
        return new Planned(solved.Edit, model, report);
    }

    /// <inheritdoc cref="Plan"/>
    /// <remarks>The geometry, without touching the file. See <see cref="Solved"/>.</remarks>
    internal static Solved Solve(ModelParts garment, IReadOnlyList<SlotPair> pairs, string? garmentSlot = null,
                                 bool pushOut = true, IReadOnlySet<int>? held = null, bool replaceSkin = false,
                                 bool clearBody = false)
    {
        var sets = Sets.From(garment, held);
        var source = SourceBody.Build(pairs);

        var nodeDelta = new Vec3[sets.NodeCount];
        var snapped = new bool[sets.NodeCount];

        Transfer(sets, sets.AllNodes, source, nodeDelta, snapped, out int transferred, out int missed);
        Knit(sets, nodeDelta, snapped);

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

            // With the skin replaced, the garment's OWN slot body is drawn after all — Rebuild embeds that very mesh
            // in the garment, so it is what the cloth ends up lying against. Measured on a sheer corset refitted
            // Bibo+ to Neolithe: pushed against the garment's transferred skin, 326 cup vertices came out buried up
            // to 3.4 mm inside the breast the file actually carries, against 2 in the author's own; the two surfaces
            // are not the same, and the one the solve could see is thrown away before the file is written.
            //
            // Only where the swap will really happen. TargetBody's remarks record the opposite measurement for a
            // garment that KEEPS its own skin: "This Old Thing" compresses the chest under its top, its cloth
            // legitimately sits inside the body, and pushing it out made that refit 50% worse. The swap is what
            // separates the two cases — it discards the author's compressed skin and puts the body's own full-size
            // mesh in its place, so there is no longer any compression for the cloth to be legitimately inside of.
            //
            // Asking for the swap is not enough: Rebuild only swaps a slot that carries its target body's FILE, so a
            // pair built without one keeps the author's skin and must keep the old exclusion with it. The two must
            // agree, or the cloth is pushed out of a body nobody draws.
            bool ownSlotSwapped = replaceSkin && garmentSlot != null
                               && pairs.Any(p => string.Equals(p.Slot, garmentSlot, StringComparison.Ordinal)
                                              && p.TargetModel != null);
            var after = TargetBody.Build(pairs, ownSlotSwapped ? null : garmentSlot,
                                         hasSkin ? Moved(garment, sets, nodeDelta) : null, before: false);

            var pushable = new List<int>(sets.ClothNodes.Length);
            foreach (int n in sets.ClothNodes)
                if (!snapped[n]) pushable.Add(n);

            pushed = PushOut(sets, pushable, before, after, nodeDelta, clearBody, out worstPush);
        }

        // Last, once nothing else will move: the answer is only worth having if the mesh still reads front-side out.
        int folded = Unfold(sets, nodeDelta, snapped);

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
                          laid, folded);
    }

    /// <summary>
    /// Take the fold out of the answer: wherever a triangle ends up facing the other way, each of its corners moves
    /// toward what its neighbours were given, and the test runs again. A turned-over triangle is drawn from behind,
    /// and a garment's backfaces are black — on this jacket the cleavage of the lapel came out as black specks.
    /// <para/>
    /// Relaxed toward the neighbours rather than scaled back toward zero (the bust bridge's <c>UnfoldTriangles</c>
    /// rule). A refit moves the WHOLE garment onto a body of another size, so scaling a corner's move back leaves it
    /// short of the new body and sunk into it. A fold is a disagreement between neighbours, not too much movement, and
    /// what fixes it is the neighbours' answer.
    /// <para/>
    /// Corners that landed exactly are left alone at first: that landing IS the answer, and the fold is usually in
    /// the cloth beside it. Both kinds count — a point the transfer snapped onto a body vertex, and the garment's own
    /// body mesh, which <see cref="LaySkin"/> puts ON the new body and which <see cref="Knit"/> excludes outright for
    /// the same reason: averaged with the cloth beside it, skin lifts off the body it has to coincide with.
    /// <para/>
    /// Half way through the rounds any fold still standing is between two exact landings — the correspondence carried
    /// neighbouring points across each other — and there the landing is no answer at all, so they are freed as well: a
    /// folded triangle of skin is a black speck like any other. Held nodes are not in <see cref="Sets.AllNodes"/> and
    /// never move.
    /// </summary>
    /// <returns>Triangles still folded when the rounds ran out — zero when the pass cleared them all.</returns>
    internal static int Unfold(Sets sets, Vec3[] nodeDelta, bool[] snapped)
    {
        var isSkin = new bool[sets.NodeCount];
        foreach (int n in sets.SkinNodes) isSkin[n] = true;

        var mayMove = new bool[sets.NodeCount];
        foreach (int n in sets.AllNodes) mayMove[n] = !snapped[n] && !isSkin[n];

        var flagged = new bool[sets.NodeCount];
        int folded = 0;
        int round = 0;
        for (; round < UnfoldRounds; round++)
        {
            if (round == UnfoldRounds / 2)
                foreach (int n in sets.AllNodes) mayMove[n] = true;

            folded = Folded(sets, nodeDelta, flagged);
            if (folded == 0) break;

            for (int n = 0; n < sets.NodeCount; n++)
            {
                if (!flagged[n] || !mayMove[n] || sets.Adj[n].Count == 0) continue;
                var sum = default(Vec3);
                foreach (int m in sets.Adj[n]) sum = new Vec3(sum.X + nodeDelta[m].X, sum.Y + nodeDelta[m].Y,
                                                              sum.Z + nodeDelta[m].Z);
                float inv = 1f / sets.Adj[n].Count;
                nodeDelta[n] = new Vec3(
                    nodeDelta[n].X + (sum.X * inv - nodeDelta[n].X) * UnfoldRate,
                    nodeDelta[n].Y + (sum.Y * inv - nodeDelta[n].Y) * UnfoldRate,
                    nodeDelta[n].Z + (sum.Z * inv - nodeDelta[n].Z) * UnfoldRate);
            }
        }

        // The rounds ran out with the last relax untested, and the number reported has to describe what was WRITTEN.
        return round < UnfoldRounds ? folded : Folded(sets, nodeDelta, flagged);
    }

    /// <summary>
    /// Triangles the deltas turn over, and (in <paramref name="flagged"/>, which this clears first) the nodes they
    /// hang off. Judged against the mesh as its author left it: a triangle already facing that way as authored is the
    /// author's business and not the refit's.
    /// </summary>
    private static int Folded(Sets sets, Vec3[] nodeDelta, bool[] flagged)
    {
        Array.Clear(flagged);
        int folded = 0;
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
            if (va < 0 || vb < 0 || vc < 0
                || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) continue;
            int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
            if (a == b || b == c || c == a) continue;   // welded to a line: no side to be on

            var at = ToVector(sets.NodeAt[a]);
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[b]) - at, ToVector(sets.NodeAt[c]) - at);
            if (n0.Length() <= 1e-12f) continue;        // already degenerate as authored; not this pass's doing
            var to = Placed(sets, nodeDelta, a);
            var n1 = Vector3.Cross(Placed(sets, nodeDelta, b) - to, Placed(sets, nodeDelta, c) - to);
            if (Vector3.Dot(n0, n1) >= 0f) continue;

            folded++;
            flagged[a] = flagged[b] = flagged[c] = true;
        }
        return folded;
    }



    /// <summary>
    /// Hold neighbouring points together: move each a little toward what its neighbours were given
    /// (<see cref="KnitRounds"/> rounds at <see cref="KnitRate"/>). Points that landed on the body exactly are the
    /// answer, not a guess, so they neither move nor are averaged into.
    /// <para/>
    /// Away from the body the nearest point is not a stable thing to ask for: three centimetres behind a heel, one
    /// point's nearest body triangle is the heel and its neighbour's is the calf, and the two bodies' displacements
    /// there differ by millimetres. Measured on a stocking, that parted a 0.3 mm edge to 7.1 mm — a spike through the
    /// shoe. The garment's own surface says what the answer should look like between neighbours: continuous.
    /// </summary>
    private static void Knit(Sets sets, Vec3[] nodeDelta, bool[] snapped)
    {
        // The garment's own body mesh is left out entirely: it is meant to land ON the body, exactly, and an average
        // with the cloth beside it would lift it off. Cloth welded to it still reads its answer as a neighbour, so the
        // seam between them stays closed.
        var isSkin = new bool[nodeDelta.Length];
        foreach (int n in sets.SkinNodes) isSkin[n] = true;

        // Over the transfer's own set, which is every node the user has NOT held: a held node is not the refit's to
        // average, and knitting one toward a moving neighbour let a hold slip at exactly the seam it exists to hold.
        var next = new Vec3[nodeDelta.Length];
        for (int round = 0; round < KnitRounds; round++)
        {
            Array.Copy(nodeDelta, next, nodeDelta.Length);
            foreach (int n in sets.AllNodes)
            {
                if (snapped[n] || isSkin[n] || sets.Adj[n] is not { Count: > 0 } neighbours) continue;
                float x = 0f, y = 0f, z = 0f;
                int count = 0;
                foreach (int m in neighbours)
                {
                    x += nodeDelta[m].X; y += nodeDelta[m].Y; z += nodeDelta[m].Z;
                    count++;
                }
                if (count == 0) continue;
                next[n] = new Vec3(nodeDelta[n].X + (x / count - nodeDelta[n].X) * KnitRate,
                                   nodeDelta[n].Y + (y / count - nodeDelta[n].Y) * KnitRate,
                                   nodeDelta[n].Z + (z / count - nodeDelta[n].Z) * KnitRate);
            }
            Array.Copy(next, nodeDelta, nodeDelta.Length);
        }
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
