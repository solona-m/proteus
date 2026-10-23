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

    /// <summary>
    /// How deep in the new breast a game top's cloth may lie and still be cleared out of it (30 mm, as far as the push
    /// probe sees), where the body grew under it by <see cref="DeepGrowLeast"/>: the game's own top on a far larger
    /// bust. <see cref="ClearDepth"/> leaves deeper cloth alone as the garment's structure, and on Dirndl's Bodice refitted
    /// onto LavaBod+'s Teardrop that left ~70 cm² of the bodice 4-24 mm inside the breasts — the top of each breast
    /// standing through it, and the neckline's skin, which cannot be drawn over buried cloth, left out. Only where the
    /// body grew: the game buries its tops' cloth 8-15 mm in its own body on purpose, and dragging that out tore the mesh
    /// (see PushOut). Only for the game's own top, in the automatic refit and the Studio — never a modder's, whose refit
    /// is written into their own mod.
    /// </summary>
    internal const float DeepClearDepth = PushProbeRange;

    /// <summary>
    /// How far the new body must stand outside the source body, where buried cloth lands on it, for that cloth to be
    /// cleared from deeper than <see cref="ClearDepth"/> (16 mm). Measured where the bodice stays buried on LavaBod+'s
    /// Teardrop, the body grew 36-60 mm and more; on Neolithe's Almond XS no cloth is buried in the breast at all. A
    /// medium bust passes too: Neolithe's neobelly M clears 8-14 points of the Top of Maiming and Scion Adventurer's (their
    /// necklines no worse — 34.0 → 32.0, 28.3 → 27.4 cm²), and 24 mm would not keep it out.
    /// </summary>
    internal const float DeepGrowLeast = 0.016f;

    /// <summary>
    /// How far inside the body a garment was made on cloth may lie and still be taken for lying ON it (4 mm), where that
    /// body decides (see <see cref="TargetBody.Fitted"/>): one mesh fitted over another stands a few millimetres either
    /// side of it. Measured on "Dei Gratia", whose blouse was fitted over the game's bra: its bust points 1.1-5.3 mm
    /// outside the bra and 0.2-3.0 mm inside. Cloth the game buries in its own body lies 6-15 mm in.
    /// </summary>
    internal const float FitSlack = 0.004f;

    /// <summary>How far the source body is looked for under a landing on the new one (60 mm); none that near counts as
    /// grown by that much.</summary>
    private const float DeepGrowReach = 0.06f;

    /// <summary>Rounds of halving the push-out's fold sweep gets. Each quarters the worst case, so eight is a factor
    /// of 256 — past any push this pass can ask for.</summary>
    private const int PushUnfoldPasses = 8;

    /// <summary>Rounds the fold relax gets (see <see cref="Unfold"/>), and how far each one takes a corner toward
    /// what its neighbours were given.</summary>
    private const int UnfoldRounds = 24;

    private const float UnfoldRate = 0.5f;

    /// <summary>
    /// How many times a triangle the relax could not turn back has its corners' movement scaled down, and by how
    /// much.
    /// <para/>
    /// Far more passes than the halving itself needs — a dozen already takes a displacement below anything that can
    /// turn a triangle over. The passes are for PROPAGATION: giving one patch back its authored shape leaves its
    /// neighbours straddling the boundary, and those fold in turn, so the count comes down a ring at a time.
    /// Measured on "Rana" refitted Bibo+ to Neolithe: 422 folds without this, 149 at sixteen passes, 52 at
    /// sixty-four, and 52 again at two hundred and fifty-six. Sixty-four is where it stops paying.
    /// <para/>
    /// It does not reach zero. What is left is held by a corner this pass may not move — the garment's own body mesh,
    /// which has to stay on the body it was laid onto.
    /// </summary>
    private const int UnfoldGiveUpPasses = 64;

    /// <inheritdoc cref="UnfoldGiveUpPasses"/>
    private const float UnfoldGiveUpRate = 0.5f;

    /// <summary>
    /// Rounds of neighbour-averaging for what the give-up takes a folded corner's movement TOWARD (128): the movement
    /// around it, smoothed until neighbouring corners no longer disagree enough to turn a triangle over.
    /// <para/>
    /// Not toward nothing, as it once was. A fold is corners disagreeing, not too much movement, and the part of their
    /// movement they share is the body-size change itself. Measured on "Picklish" (a strapless band, Neolithe Almond L
    /// to YAB Large, whose bust sits 38 mm lower): scaled toward nothing, the band under the bust was left at a
    /// sixteenth of its movement, on the old body, and 1,916 of its points came out up to 26 mm inside the new breasts
    /// to save about a hundred folded triangles. Toward this: 114, and against the authors' own sizes every refit
    /// measured came out as close or closer (Seaside L to XS cloth 1.95 -> 1.62 mm mean). Fewer rounds leave more
    /// folds (32: twelve on "Sheer Elegance", 128: two).
    /// <para/>
    /// Nor with a floor on how much of it a point keeps: the floor was there because going all the way back to
    /// nothing tore a point off the neighbours that kept the body-size move, and going all the way to what the
    /// neighbours are doing is no tear.
    /// </summary>
    internal const int UnfoldAnchorRounds = 128;

    /// <summary>How much deeper into the drawn skin unfolding may take a cloth point than the push-out left it (0.1 mm):
    /// enough for rounding, and no more.</summary>
    internal const float UnfoldBuryTolerance = 1e-4f;

    /// <summary>
    /// How far the push-out looks for the skin when deciding whether cloth was authored INSIDE it (15 cm). Cloth tucked
    /// under a body can sit far inside it — a heeled shoe's foot is drawn where the body's flat foot is — and at
    /// <see cref="PushProbeRange"/> such a point read as "nowhere near skin", which the pass took for outside. That far
    /// out the nearest skin can be a patch's open edge the point lies beyond; that reading is put to the body the garment
    /// was made on instead (see <see cref="TargetBody.Fitted"/>).
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
    /// <param name="ShapeOnly">The slot is here for its shape alone — where its body sits, and what its skin weights
    /// are — and its skin is never swapped in, though the pair carries the body's file: a shoe keeping its own foot
    /// still has the feet body, which runs up the shin, to refit its stocking's shin by (see <see cref="KeepEnds.Plan"/>).</param>
    /// <param name="TargetMaterial">Reads one of the target body mod's materials by the name its model gives it (see
    /// <see cref="BodyMaterialReader"/>): what the swap copies the body's smallclothes material from, on a
    /// <see cref="Worn"/> pair. Null when the body mod cannot be read.</param>
    /// <param name="Smallclothes">On a <see cref="Worn"/> slot <see cref="ReadySmallclothes"/> readied: each of the new
    /// body's smallclothes materials, and the name the garment draws it under. Null on every other slot.</param>
    /// <param name="SourceIsGameChest">The old body is the game's own chest, byte for byte (see
    /// <see cref="VanillaBodyCatalog.IsGameModel"/>): its bust is its bra, read with its skin (see
    /// <see cref="SmallclothesOverHoles"/>).</param>
    internal readonly record struct SlotPair(string Slot, IBodyCorrespondence Correspondence, ModelParts Target,
                                             byte[]? TargetModel = null, byte[]? SourceModel = null,
                                             IReadOnlySet<string>? TargetHidden = null, bool ShapeOnly = false,
                                             Func<string, byte[]?>? TargetMaterial = null,
                                             IReadOnlyDictionary<string, string>? Smallclothes = null,
                                             bool SourceIsGameChest = false)
    {
        /// <summary>Whether the swap puts this slot's body skin in: it carries the body's file, and is not
        /// <see cref="ShapeOnly"/>.</summary>
        public bool Swaps => TargetModel != null && !ShapeOnly;

        /// <summary>The new body draws something other than skin where the old one has it — see
        /// <see cref="IBodyCorrespondence.TargetWorn"/> — so the refit reads both bodies as they are worn.</summary>
        public bool Worn => Correspondence.TargetWorn;

        /// <summary>The new body as the refit reads it: as worn (<see cref="BodySurface.OfBodyWorn"/>) on a
        /// <see cref="Worn"/> pair, its skin alone on every other, as always.</summary>
        public BodySurface TargetSurface() => SurfaceOf(Target);

        /// <summary>The new body's open edges, read as <see cref="TargetSurface"/> reads the body.</summary>
        public SkinEdges TargetEdges() => Worn ? SkinEdges.OfWorn(Target) : SkinEdges.Of(Target);

        /// <summary>The old body as the refit reads it: as worn on a <see cref="Worn"/> pair, so cloth its author fitted
        /// over the old body's own smallclothes is judged as the new body's is.</summary>
        public BodySurface SourceSurface() => SurfaceOf(Correspondence.Source);

        /// <summary>The old body's open edges, read as <see cref="SourceSurface"/> reads the body.</summary>
        public SkinEdges SourceEdges() => Worn ? SkinEdges.OfWorn(Correspondence.Source) : SkinEdges.Of(Correspondence.Source);

        private BodySurface SurfaceOf(ModelParts body)
        {
            float cell = BodySurface.CellFor(MeanEdgeOf(body));
            return Worn ? BodySurface.OfBodyWorn(body, cell) : new BodySurface(body, cell);
        }
    }

    /// <summary>What happened, for the status line and the saved record.</summary>
    /// <param name="Held">Welded points the user held in place, by unticking their parts.</param>
    /// <param name="Laid">Skin points laid exactly onto the new body — see <see cref="LaySkin"/>.</param>
    /// <param name="Swap">What swapping the garment's skin for the body's did; null when it did not run.</param>
    /// <param name="Folded">Triangles the refit left facing the wrong way — see <see cref="Solved"/>.</param>
    /// <param name="Smoothed">Vertices of the new body whose covered nipples were smoothed — see
    /// <see cref="SmoothCoveredNipples"/>.</param>
    /// <param name="Layered">Points lifted to keep the garment outside the other piece of its outfit — see
    /// <see cref="KeepOutfitOrder"/>.</param>
    /// <param name="LayerResidual">How far, at most, the garment is still short of that piece (metres).</param>
    /// <param name="DeepCleared">Cloth points pulled out of the new breast from deeper than <see cref="ClearDepth"/> —
    /// see <see cref="DeepClearDepth"/>.</param>
    /// <param name="DeepWorst">The furthest any of them ended up from where the push-out found it (metres): the push and
    /// the settle together.</param>
    /// <param name="DeepAt">A garment vertex of each of them, for a check of how far the clear's spread reached.</param>
    /// <param name="StandoffPulled">Cloth points brought back toward the author's standoff — see <see cref="KeepStandoff"/>.</param>
    /// <param name="StandoffWorst">The furthest any of them came back (metres).</param>
    /// <param name="StandoffHeld">Of them, points a guard held short of where they would have come back to.</param>
    /// <param name="OpenEdgeFitted">Cloth points the push-out took in by the made-on body's reading, their nearest
    /// authored skin an open edge they lie behind — see <see cref="TargetBody.Fitted"/>.</param>
    /// <param name="SmallclothesFallback">Slots whose new body draws smallclothes in place of skin, which kept the
    /// garment's own skin because those smallclothes could not be brought in — see <see cref="ReadySmallclothes"/>.</param>
    /// <param name="SourceSmallclothes">Points of the old body's underwear, over a hole in its skin, given the new body's
    /// shape — see <see cref="SmallclothesOverHoles"/>.</param>
    /// <param name="ShapeFallbacks">Turns of kept pieces that the polar fit could not give and Horn's quaternion fit did —
    /// flat cards, mostly (see <see cref="TurnOf"/>). A piece turned so in both passes counts twice.</param>
    internal sealed record Report(
        int Nodes, int Snapped, int Transferred, int Missed, int Pushed,
        float WorstMove, float WorstPush, int UnmappedSpares, bool HasOtherLods, int Held = 0, int Laid = 0,
        SwapReport? Swap = null, int Folded = 0, IReadOnlyList<ShapeOutcome>? Shapes = null, int Smoothed = 0,
        int Layered = 0, float LayerResidual = 0f, int DeepCleared = 0, float DeepWorst = 0f,
        IReadOnlyList<int>? DeepAt = null, int StandoffPulled = 0, float StandoffWorst = 0f, int StandoffHeld = 0,
        int OpenEdgeFitted = 0, int SmallclothesFallback = 0, int SourceSmallclothes = 0, int ShapeFallbacks = 0)
    {
        /// <summary>Share of moved nodes that landed on a body vertex exactly. Low means the author sculpted the
        /// garment's body mesh rather than copying it, and the seam may not come out perfect.</summary>
        public float SnapRate => Transferred > 0 ? (float)Snapped / Transferred : 0f;
    }

    /// <summary>The retarget, ready to preview or save.</summary>
    /// <param name="Materials">Material files the model draws that the save must publish beside it: the new body's
    /// smallclothes, brought in with its skin (see <see cref="ReadySmallclothes"/>). Null when there are none.</param>
    internal sealed record Planned(RetargetEdit Edit, byte[] Model, Report Report,
                                   IReadOnlyList<PublishedMaterial>? Materials = null);

    /// <summary>
    /// The geometry half of a retarget, with no file anywhere near it. Separated from <see cref="Plan"/> so the solve
    /// can be tested against a hand-built <see cref="ModelParts"/> at real body scale, rather than only through a
    /// synthetic .mdl whose triangles are a metre across.
    /// </summary>
    /// <param name="Folded">Triangles still facing the wrong way when <see cref="Unfold"/> ran out of rounds — or, when
    /// <see cref="KeepOutfitOrder"/> lifted anything, on the mesh as written after it, every pass counted. Any at all is
    /// worth saying: a folded triangle is a black speck on the garment.</param>
    /// <param name="ShapeFallbacks">See <see cref="Report.ShapeFallbacks"/>: the final stage's, the rerun's when a piece
    /// was let go.</param>
    internal sealed record Solved(RetargetEdit Edit, int Snapped, int Transferred, int Missed, int Pushed,
                                  float WorstMove, float WorstPush, int Held = 0, int Laid = 0, int Folded = 0,
                                  IReadOnlyList<ShapeOutcome>? Shapes = null, int Layered = 0,
                                  float LayerResidual = 0f, int DeepCleared = 0, float DeepWorst = 0f,
                                  IReadOnlyList<int>? DeepAt = null, int StandoffPulled = 0, float StandoffWorst = 0f,
                                  int StandoffHeld = 0, int OpenEdgeFitted = 0, int SourceSmallclothes = 0,
                                  int ShapeFallbacks = 0);

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
        /// <param name="swappedSkin">The garment's skin meshes the swap will replace (see <see cref="SwappedSkinMeshes"/>):
        /// cloth welded to them is cloth, not part of them. See the body.</param>
        public static Sets From(ModelParts garment, IReadOnlySet<int>? held = null, IReadOnlySet<int>? swappedSkin = null)
        {
            int vc = garment.Positions.Length / 3;
            var vertAt = new Vec3[vc];
            for (int i = 0; i < vc; i++)
                vertAt[i] = new Vec3(garment.Positions[i * 3], garment.Positions[i * 3 + 1], garment.Positions[i * 3 + 2]);

            var nodeOf = MeshMath.WeldByPosition(vertAt, out int nodeCount);

            var skinVert = new bool[vc];
            var swappedVert = new bool[vc];
            foreach (var part in garment.Parts)
            {
                if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
                bool swapped = swappedSkin?.Contains(part.Mesh) == true;
                foreach (int v in part.Triangles)
                    if (v >= 0 && v < vc)
                    {
                        skinVert[v] = true;
                        if (swapped) swappedVert[v] = true;
                    }
            }

            // Cloth welded to the skin it meets is one node with it, and a node with skin in it is skin: laid onto the
            // new body, never pushed out. Right while that skin is drawn — the two must not part. But skin the swap
            // replaces leaves nothing for the cloth to be welded to, and it went on being treated as skin: the Pioneer's
            // Bottoms' hem shares its corners with the vanilla strip under it, so the hem was laid flat onto Neolithe's
            // rounder buttock and its edges sank 5 mm in between — a slit seen in game. There, the cloth's points get a
            // node of their own. Only there: a node that also holds skin the swap keeps — a heeled shoe's own foot, the
            // skin of a slot nobody is resizing — stays fused, or the seam to that skin would open.
            if (swappedSkin is { Count: > 0 })
            {
                var swappedNode = new bool[nodeCount];
                var keptNode = new bool[nodeCount];
                for (int v = 0; v < vc; v++)
                {
                    if (swappedVert[v]) swappedNode[nodeOf[v]] = true;
                    else if (skinVert[v]) keptNode[nodeOf[v]] = true;
                }
                var clothNodeOf = new Dictionary<int, int>();
                for (int v = 0; v < vc; v++)
                {
                    int n = nodeOf[v];
                    if (skinVert[v] || !swappedNode[n] || keptNode[n]) continue;
                    if (!clothNodeOf.TryGetValue(n, out int own)) clothNodeOf[n] = own = nodeCount++;
                    nodeOf[v] = own;
                }
            }

            // Which nodes are the garment's own body mesh. Worked out BEFORE the seam lock below, because that lock
            // must never fuse skin to cloth: the two passes are handed opposite node lists, and a node that is both
            // would have to be in both.
            var isSkin = new bool[nodeCount];
            for (int v = 0; v < vc; v++)
                if (skinVert[v]) isSkin[nodeOf[v]] = true;

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
    /// even when the two bodies' rigs name the same bones (YAB's and Rue's plain sizes do), and cloth on the skin is
    /// rigged as the new skin. Within one body mod a rig change takes the change between the two bodies alone — see
    /// <see cref="PlanWeights"/>.</param>
    /// <param name="cutHidden">With <paramref name="replaceSkin"/>, leave out the new body's skin where the garment's
    /// author deleted theirs, which is usually skin under the cloth that cannot be seen — see <see cref="CutLike"/>.
    /// On by default; off puts the body's skin in whole.</param>
    /// <param name="keepShape">Pieces moved whole rather than bent to the body — see <see cref="KeepShape"/>. One set
    /// of vertices per piece; null or empty for none.</param>
    /// <param name="swapSlot">A slot other than <paramref name="garmentSlot"/> whose skin is the one being swapped: the
    /// legs of a shoe whose own foot is held (see <see cref="SwapSlotSwapped"/>). Null for none, which is every refit but
    /// the automatic one of shoes and gloves.</param>
    /// <param name="log">Where the passes that explain themselves write: the nipple smoothing. Null for nowhere.</param>
    /// <param name="warn">Where a pass that failed — a bug, not a garment with nothing to do — says so. Null falls back to
    /// <paramref name="log"/>.</param>
    /// <param name="chestSlot">"_top" for a garment over the chest that is not worn in the chest's slot — a jacket on a
    /// necklace, its <paramref name="garmentSlot"/> null so the chest beneath it stays in the push-out — so the
    /// underside of the breast is still cleared, faces and all. Null: a top's own slot decides, as it always has.</param>
    /// <param name="trace">For a diagnostic: each stage of the solve by name, with a copy of every node's move so far
    /// (see <see cref="Solve"/>). Null, as every refit has it, costs nothing.</param>
    /// <param name="outfit">See <see cref="Solve"/>.</param>
    /// <param name="tuckSlot">The slot whose cut skin is carried on in under the cloth past the author's edge (see
    /// <see cref="TuckUnderCloth"/>), or null for none: the game's own top in the automatic refit, and a top the Body
    /// size tool refits with the hidden skin cut and the body cleared.</param>
    /// <param name="deepClear">Clear a top's cloth out of the new breast from deeper than <see cref="ClearDepth"/>, where
    /// the body grew under it (see <see cref="DeepClearDepth"/>): the game's own top, in the automatic refit and the
    /// Studio, never a modder's top. Only with <paramref name="clearBody"/>.</param>
    /// <param name="keepStandoff">Bring a top's cloth the refit carried further off the new chest than its author put it
    /// back toward the author's standoff (see <see cref="KeepStandoff"/>): the game's own top, in the automatic refit and
    /// the Studio, never a modder's top. Only with <paramref name="clearBody"/>.</param>
    /// <param name="carryPiercings">With <paramref name="replaceSkin"/>, bring the new body's piercings in with its skin
    /// (see <see cref="Rebuild"/>). Off by default: the user asks for them.</param>
    /// <param name="liftUnderbust">Lift the cloth under the new breasts off their underside (see LiftUnderbust). Off only
    /// for a men's top refitted for women (<see cref="CrossSexRefit"/>): a shirt cut for a pec has no cloth under a
    /// breast to lift, and lifting it turned over faces the face clearing does not (13 folds on the Lumberjack shirt
    /// onto Bibo+, 8 without; measured 2026-10-20). The face clearing after it still runs.</param>
    /// <param name="seeInside">With <paramref name="tuckSlot"/>, the tuck also draws open skin through which the garment's
    /// inside would show (see <see cref="TuckSeeReach"/>): the game's own top, in the automatic refit and the Studio —
    /// measured on the game's tops alone, so never a modder's top, nor a top the user refits for the other sex. A game top
    /// made only for men, which the automatic refit bakes for a woman wearing it, is still the game's own and gets it, as it
    /// gets the deep clear and the standoff.</param>
    public static Planned Plan(ModelParts garment, byte[] garmentBytes, IReadOnlyList<SlotPair> pairs,
                               string? garmentSlot = null, bool pushOut = true, IReadOnlySet<int>? held = null,
                               bool replaceSkin = false, bool acrossBodies = false, bool clearBody = false,
                               bool cutHidden = true, IReadOnlyList<IReadOnlyCollection<int>>? keepShape = null,
                               string? swapSlot = null, Action<string>? log = null, Action<string>? warn = null,
                               string? chestSlot = null, Action<string, Vec3[]>? trace = null,
                               Outfit? outfit = null, string? tuckSlot = null, bool deepClear = false,
                               bool keepStandoff = false, bool carryPiercings = false, bool liftUnderbust = true,
                               bool seeInside = false)
    {
        // First, before anything reads the new chest: its nipples smoothed where the garment covers more of them than
        // the old body had, so every pass below sees one surface — the one the swap draws.
        int smoothed = 0;
        if (!Tuned.NoNippleSmooth)
            pairs = SmoothCoveredNipples(garment, pairs, out smoothed, log, swapSlot, Uv(garmentBytes), warn);

        // A new body that draws smallclothes where it has no skin brings them in with its skin, or that slot keeps the
        // garment's own skin: decided before anything reads which skin meshes the swap replaces.
        pairs = ReadySmallclothes(garmentBytes, pairs, replaceSkin, out var smallclothes, out int smallclothesFallback);
        if (smallclothesFallback > 0)
            log?.Invoke($"smallclothes: {smallclothesFallback} slot(s) keep the garment's own skin — the new body's "
                        + "underwear could not be brought in");

        // The solver and the rebuild must agree on which skin meshes go: the solver parts the cloth welded to them, and a
        // mesh it parted that the rebuild then keeps opens the seam (see Sets.From). Without a hold the two rules agree as long
        // as the old body's skin lies on the new one once laid (see parted, below); with one, a held ankle ring stays where the old body had it and the rebuild, judging the laid model,
        // could keep a mesh the solver already parted. So the solver's set is handed to the rebuild.
        IReadOnlySet<int>? swapMeshes = SwapSlotSwapped(pairs, replaceSkin, swapSlot)
            ? SwappedSkinMeshes(garment, pairs)
            : null;
        // Without a hold the rules can still part ways: the solver judges the skin on the body it was made on, the rebuild
        // on the new one once laid there, and the old body's legs can reach further up the waist than the new one's. Metro
        // Style's waist strip from Lithe+ came out 35 mm above where Neolithe's legs stop, 89% on them, so the rebuild kept
        // the old skin, Lithe's shading and all, while the solver had already parted the cloth from it. So every mesh the
        // solver parted is swapped too (see Rebuild's parted).
        var parted = swapMeshes ?? PartedSkinMeshes(garment, pairs, garmentSlot, replaceSkin, swapSlot);
        bool deep = deepClear && clearBody && garmentSlot == "_top" && !Tuned.NoDeepClear;
        bool standoff = keepStandoff && clearBody && garmentSlot == "_top" && !Tuned.NoKeepStandoff;
        var solved = Solve(garment, pairs, garmentSlot, pushOut, held, replaceSkin, clearBody, keepShape,
                           ModelSkinReader.Read(garmentBytes, null, null), swapSlot, parted, chestSlot, trace, outfit,
                           deep, standoff, liftUnderbust);
        if (solved.ShapeFallbacks > 0)
            log?.Invoke($"keep shape: {solved.ShapeFallbacks} turns of kept pieces too flat for the usual fit, made by the "
                        + "quaternion fit");
        if (solved.SourceSmallclothes > 0)
            log?.Invoke($"source smallclothes: {solved.SourceSmallclothes} points of the old body's underwear, over its "
                        + "skin's hole, given the new body's shape");
        if (solved.OpenEdgeFitted > 0)
            log?.Invoke($"fitted: {solved.OpenEdgeFitted} cloth points below an open skin edge read against the body the "
                        + "garment was made on");
        if (solved.DeepCleared > 0)
            log?.Invoke($"cloth cleared deep: {solved.DeepCleared} points out of the new breast, up to {solved.DeepWorst * 1000f:0.0} mm");
        if (solved.StandoffPulled > 0)
            log?.Invoke($"cloth brought back toward the author's standoff: {solved.StandoffPulled} points, up to "
                        + $"{solved.StandoffWorst * 1000f:0.0} mm");
        var written = MeshVolumeService.Inflate(garmentBytes, solved.Edit);
        byte[] model = written.Model;

        // Across rigs the cloth takes the new body's weights; with the skin replaced, the body's skin meshes come in.
        // Both are one rebuild, and nothing is rebuilt when neither applies — a same-rig refit with the skin kept stays
        // the in-place rewrite above.
        SwapReport? swap = null;
        // The pieces still kept whole: one the solve let go bends with the body, and eases with it point by point.
        var whole = keepShape != null && solved.Shapes is { } judged
            ? keepShape.Where((_, k) => k >= judged.Count || !judged[k].Released).ToList()
            : keepShape;
        var weights = PlanWeights(model, pairs, acrossBodies, before: garmentBytes, held: held, keepShape: whole);
        if (weights?.CrotchNotes is { } crotchNotes)
        {
            foreach (var note in crotchNotes) log?.Invoke($"crotch: {note}");
            if (weights.CrotchEased > 0)
                log?.Invoke($"crotch: {weights.CrotchEased} cloth points between the legs eased off them");
        }
        if (replaceSkin || weights != null)
        {
            bool see = seeInside && tuckSlot != null && !Tuned.NoSeeInside;
            var rebuilt = Rebuild(model, pairs, replaceSkin, weights, out var swapped, cutHidden, acrossBodies,
                                  swapMeshes, tuckSlot, carryPiercings, parted, see);
            if (rebuilt != null)
            {
                model = rebuilt;
                swap = swapped;
                if (swapped.Tucked > 0)
                    log?.Invoke($"skin tucked: {swapped.Tucked} triangles of the new body under the cloth past the author's edge "
                                + $"({swapped.Opened} in the open, inside the author's skin"
                                + (see ? $"; {swapped.SeenInto} in the open where the garment's inside would show" : "")
                                + ")");
                if (swapped.TuckUnread > 0)
                    log?.Invoke("skin tuck: the new body's weights could not be read, so nothing was tucked under the cloth");
            }
            else if (swapped.Kept > 0)
            {
                // Nothing was rebuilt, but the skin the swap left alone is worth reporting.
                swap = swapped;
            }
        }

        // Only LOD0 was moved. The rebuild above writes LOD0 alone; an in-place refit still carries the author's other
        // levels, fitted to the old body, and the game draws them from a distance — so they are cut. What the cut
        // refuses keeps them, and the report says so, read off the model actually written.
        if (ModelLodTrimmer.LodCount(model) > 1 && ModelLodTrimmer.KeepLod0(model, out _) is { } trimmed)
            model = trimmed;

        var report = new Report(garment.Positions.Length / 3, solved.Snapped, solved.Transferred, solved.Missed,
                                solved.Pushed, solved.WorstMove, solved.WorstPush,
                                written.UnmappedSpares, ModelLodTrimmer.LodCount(model) > 1, solved.Held, solved.Laid,
                                swap, solved.Folded, solved.Shapes, smoothed, solved.Layered, solved.LayerResidual,
                                solved.DeepCleared, solved.DeepWorst, solved.DeepAt, solved.StandoffPulled,
                                solved.StandoffWorst, solved.StandoffHeld, solved.OpenEdgeFitted, smallclothesFallback,
                                solved.SourceSmallclothes, solved.ShapeFallbacks);

        // Only the copies the written model draws: a slot whose skin no mesh of the garment reached adds none.
        var drawnMaterials = new HashSet<string>(SecondSkinWriter.Parse(model).MatNames.Select(m => m.TrimStart('/')),
                                                 StringComparer.OrdinalIgnoreCase);
        var publish = smallclothes.Where(m => drawnMaterials.Contains(m.Name)).ToList();
        return new Planned(solved.Edit, model, report, publish.Count > 0 ? publish : null);
    }

    /// <summary>
    /// Whether <paramref name="swapSlot"/>'s skin really is swapped: asked for, and its pair carries its target body's
    /// file, which is the only kind <see cref="Rebuild"/> swaps.
    /// </summary>
    private static bool SwapSlotSwapped(IReadOnlyList<SlotPair> pairs, bool replaceSkin, string? swapSlot)
        => replaceSkin && swapSlot != null
        && pairs.Any(p => string.Equals(p.Slot, swapSlot, StringComparison.Ordinal) && p.Swaps);

    /// <summary>
    /// The skin meshes the solve parts the cloth welded to them from (see <see cref="Sets.From"/>): those the swap
    /// replaces, when the garment's own slot is swapped or <paramref name="swapSlot"/>'s is. Null when neither is.
    /// </summary>
    internal static IReadOnlySet<int>? PartedSkinMeshes(ModelParts garment, IReadOnlyList<SlotPair> pairs,
                                                        string? garmentSlot, bool replaceSkin, string? swapSlot)
        => SwapSlotSwapped(pairs, replaceSkin, garmentSlot) || SwapSlotSwapped(pairs, replaceSkin, swapSlot)
            ? SwappedSkinMeshes(garment, pairs)
            : null;

    /// <inheritdoc cref="Plan"/>
    /// <remarks>The geometry, without touching the file. See <see cref="Solved"/>.</remarks>
    /// <param name="garmentSkin">The garment's skinning, in the part reader's vertex order: which side of the body each
    /// point belongs to, so a pant leg never follows the other leg (see <see cref="SourceBody.TryNearest"/>). Null asks
    /// either side.</param>
    /// <param name="swapSlot">See <see cref="Plan"/>.</param>
    /// <param name="swapMeshes">The skin meshes the swap replaces (<see cref="PartedSkinMeshes"/>), as <see cref="Plan"/>
    /// works them out once for both the solve and the rebuild; null works them out here.</param>
    /// <param name="trace">Each stage by name — "transfer", "knit", "layer-knit", "lay", "follow", "push", "unfold",
    /// "relax", "face-settle", "rim-lift", "standoff", "layer-order" — with a copy of every node's move so far, indexed as
    /// <see cref="Sets.NodeOf"/> maps vertices — and "outfit-order" after them when <paramref name="outfit"/> is given.
    /// Changes nothing; null skips the copies.</param>
    /// <param name="outfit">The other piece of the garment's outfit, which it was authored over and must stay outside
    /// (see <see cref="KeepOutfitOrder"/>). Null for none, which is every refit but a jacket's or shoe's whose author
    /// option also ships the piece beneath.</param>
    internal static Solved Solve(ModelParts garment, IReadOnlyList<SlotPair> pairs, string? garmentSlot = null,
                                 bool pushOut = true, IReadOnlySet<int>? held = null, bool replaceSkin = false,
                                 bool clearBody = false, IReadOnlyList<IReadOnlyCollection<int>>? keepShape = null,
                                 XivLiveMesh.SkinnedMesh? garmentSkin = null, string? swapSlot = null,
                                 IReadOnlySet<int>? swapMeshes = null, string? chestSlot = null,
                                 Action<string, Vec3[]>? trace = null, Outfit? outfit = null, bool deepClear = false,
                                 bool keepStandoff = false, bool liftUnderbust = true)
    {
        // With the skin replaced, the garment's OWN slot body is drawn after all — Rebuild embeds that very mesh in the
        // garment, so it is what the cloth ends up lying against. Asking for the swap is not enough: Rebuild only swaps
        // a slot that carries its target body's FILE, so a pair built without one keeps the author's skin, and
        // everything that depends on the swap must agree with it.
        bool ownSlotSwapped = SwapSlotSwapped(pairs, replaceSkin, garmentSlot);
        // The cloth on swapped skin is handled the same whichever slot's skin it is: a shoe keeping its own foot swaps
        // its legs, and the stocking welded to the old legs' skin must not be laid flat into the new ones. What depends
        // on the GARMENT's own slot — which body the push-out leaves out, the underbust — still asks ownSlotSwapped.
        bool slotSwapped = ownSlotSwapped || SwapSlotSwapped(pairs, replaceSkin, swapSlot);

        var sets = Sets.From(garment, held,
                             slotSwapped ? swapMeshes ?? PartedSkinMeshes(garment, pairs, garmentSlot, replaceSkin, swapSlot)
                                         : null);
        var source = SourceBody.Build(pairs);

        var nodeDelta = new Vec3[sets.NodeCount];
        var snapped = new bool[sets.NodeCount];
        void Stage(string name) => trace?.Invoke(name, (Vec3[])nodeDelta.Clone());

        Transfer(sets, sets.AllNodes, source, nodeDelta, snapped, out int transferred, out int missed,
                 garmentSkin != null ? NodeSides(sets, garmentSkin, BodyBonesOf(pairs)) : null);
        Stage("transfer");
        Knit(sets, nodeDelta, snapped);
        Stage("knit");
        // After the knit, which evens each sheet out along itself and would take the sheets apart again.
        var layers = Tuned.NoLayerKnit ? null : LayerPartners(sets, snapped);
        if (layers != null) KnitLayers(sets, layers, nodeDelta);
        Stage("layer-knit");

        // Before the push-out, so the push-out measures cloth against the skin as it will actually be drawn.
        var carried = (Vec3[])nodeDelta.Clone();
        int laid = replaceSkin ? LaySkin(sets, source, pairs, nodeDelta) : 0;
        Stage("lay");
        // Held skin is left out of what the cloth follows only on the swapSlot path, where the hold is a kept foot: the
        // cloth beside it measured its gap from that foot, which the new legs do not draw. A Studio hold stays as it was.
        if (slotSwapped && laid > 0 && !Tuned.NoFollow)
            FollowLaidSkin(garment, sets, carried, nodeDelta, snapped, pairs, swapSlot != null ? held : null);
        Stage("follow");

        // The skin drawn under the cloth as authored: the garment's own body mesh where it has one, and the other slots'
        // bodies. See TargetBody, and PushOut for why it is needed. It reads nothing the passes below move.
        bool hasSkin = sets.ClothNodes.Length < sets.NodeCount;
        var before = pushOut ? TargetBody.Build(pairs, garmentSlot, hasSkin ? garment : null, before: true) : null;

        // Hard pieces whole, the push-out, and whole again — and any piece the whole move would sink into the new body
        // let go: the stage runs again from the same start without it, so it comes out exactly as if it had never been
        // ticked. One round; see ReleaseSink.
        var deep = deepClear && clearBody && ownSlotSwapped ? DeepClearTest(pairs, garmentSlot) : null;
        var free = keepShape is { Count: > 0 } ? (Vec3[])nodeDelta.Clone() : null;
        var stage = ShapeAndPush(garment, pairs, garmentSlot, ownSlotSwapped, sets, snapped, nodeDelta, keepShape,
                                 before, clearBody, deep);
        var kept = keepShape;
        if (free != null && stage.Shapes != null && Array.Exists(stage.Shapes, s => s.Released))
        {
            var judged = stage.Shapes;
            kept = keepShape!.Where((_, k) => !judged[k].Released).ToList();
            Array.Copy(free, nodeDelta, nodeDelta.Length);
            stage = ShapeAndPush(garment, pairs, garmentSlot, ownSlotSwapped, sets, snapped, nodeDelta, kept, before,
                                 clearBody, deep) with { Shapes = judged };
        }

        Stage("push");
        int pushed = stage.Pushed;
        float worstPush = stage.WorstPush;
        TargetBody? drawn = stage.Drawn;
        FaceCheck? faceCheck = stage.FaceCheck;
        float[]? pushedBy = stage.PushedBy;

        // Last, once nothing else will move: the answer is only worth having if the mesh still reads front-side out.
        // Past the push-out, so whatever unfolding gives up is judged against the skin it would be given up into.
        int folded = Unfold(sets, nodeDelta, snapped, drawn == null ? null : SignedOff(drawn));
        Stage("unfold");

        // Skin through the middle of a face, which the push-out's fold guard gave up on: cleared last, after the relax
        // (which only keeps VERTICES out of the skin), by a pass that turns nothing over, so the fold count above still
        // stands. Hard pieces stay as they were put. See ClearFaces.
        bool[]? stay = null;
        if (kept is { Count: > 0 })
        {
            stay = new bool[sets.NodeCount];
            foreach (var piece in kept)
                foreach (int v in piece)
                    if (v >= 0 && v < sets.NodeOf.Length) stay[sets.NodeOf[v]] = true;
        }

        // Movement out of line with its neighbours evened out, snapped cloth included — see Relax.
        if (drawn != null && !Tuned.NoRelax) Relax(sets, nodeDelta, SignedOff(drawn), stay, layers);
        Stage("relax");

        if (faceCheck != null && pushedBy != null && !Tuned.NoFaceSettle)
        {
            // The underside of the breast first, every layer together; then whatever face is still through.
            // Tops only: on legs, skin facing down and forward is the crease under the belly, not a breast. A garment
            // over the chest worn in another slot — a jacket on a necklace — is one too (chestSlot).
            if (!Tuned.NoUnderbustLift && liftUnderbust && drawn != null && (chestSlot ?? garmentSlot) == "_top")
            {
                // The breast read off the new chest's own weights: where the swap will draw that chest, or — for a garment
                // over it, whose chest is drawn beneath it as it is — wherever its pair carries the new chest.
                var chest = chestSlot != null && chestSlot != garmentSlot
                    ? pairs.FirstOrDefault(p => p.Slot == chestSlot && p.TargetModel != null)
                    : ownSlotSwapped ? pairs.FirstOrDefault(p => p.Slot == garmentSlot && p.Swaps) : default;
                var isBreast = chest.TargetModel != null ? BreastTest(chest) : null;
                // The hip across every body in play: it spans the chest's skin and the legs' beside it.
                var isHip = HipTest(pairs);
                pushed += LiftUnderbust(faceCheck, drawn, nodeDelta, pushedBy, ref worstPush, stay, isBreast, isHip);
            }
            pushed += ClearFaces(faceCheck, nodeDelta, pushedBy, ref worstPush, stay);
        }
        Stage("face-settle");

        // A hem lying on the skin whose edges the new, rounder body comes up through between its corners — see LiftRims.
        // Only with the skin swapped: then the body's own skin is drawn under the rim, where the author's sculpted skin
        // is not, and that is the case it was measured on (the Pioneer's Bottoms onto Neolithe).
        if (drawn != null && slotSwapped && !Tuned.NoRimLift) pushed += LiftRims(sets, drawn, nodeDelta, stay);
        Stage("rim-lift");

        // The cloth the passes above carried further off the new chest than its author put it, back toward the author's
        // standoff — see KeepStandoff. Last of the moves, so the tuck and the open skin judge the cloth where it ends;
        // before the layer order, which puts back anything it disturbs.
        int standoffPulled = 0, standoffHeld = 0;
        float standoffWorst = 0f;
        if (keepStandoff && clearBody && ownSlotSwapped && StandoffTest(pairs, garmentSlot) is { } standoff)
        {
            standoffPulled = KeepStandoff(garment, sets, standoff, nodeDelta, snapped, stay, out standoffWorst,
                                          out standoffHeld);
            // Counted again on what is written, as after the outfit order: Unfold counted before this moved anything.
            if (standoffPulled > 0) folded = FoldedTriangles(sets, nodeDelta);
        }
        Stage("standoff");

        // Never write a NaN into the file: one spreads through every smoothing pass it touches, and the model it lands in
        // draws nothing there and threw every frame from the Parts preview. A node with no finite answer stays put.
        for (int n = 0; n < sets.NodeCount; n++)
            if (!float.IsFinite(nodeDelta[n].X) || !float.IsFinite(nodeDelta[n].Y) || !float.IsFinite(nodeDelta[n].Z))
                nodeDelta[n] = default;

        // Last: every piece the author covered with another still behind it — see KeepLayerOrder. Past the guard above,
        // so a node with no answer is already back where it was rather than a NaN spread through a covered piece.
        bool outfitOrder = outfit != null && !Tuned.NoOutfitOrder;
        bool[]? layered = !Tuned.NoLayerOrder || outfitOrder ? GarmentCloth(garment, sets) : null;
        List<Covered>? covered = !Tuned.NoLayerOrder ? CoveredPoints(sets, layered!, FacesOneWay(garment, sets)) : null;
        if (covered != null) KeepLayerOrder(sets, covered, nodeDelta, stay, layered!, drawn);
        Stage("layer-order");

        // And outside the other piece of its outfit, which is already drawn as it is — see KeepOutfitOrder. After the
        // garment's own layers, which it puts back in order itself, so nothing moves either piece after it.
        int outfitLayered = 0;
        float layerResidual = 0f;
        if (outfitOrder)
        {
            outfitLayered = KeepOutfitOrder(sets, pairs, outfit!, nodeDelta, stay, layered!, covered ?? [],
                                            out layerResidual);
            // Counted again on what is written: the fold count reports the mesh as drawn, not as it stood before this.
            if (outfitLayered > 0) folded = FoldedTriangles(sets, nodeDelta);
            Stage("outfit-order");
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
                          laid, folded, stage.Shapes, outfitLayered, layerResidual, stage.DeepCleared, stage.DeepWorst,
                          stage.DeepAt, standoffPulled, standoffWorst, standoffHeld, stage.OpenEdgeFitted,
                          source.SmallclothesLanded, stage.Fallbacks);
    }

    /// <summary>
    /// Which buried cloth may be cleared from deeper than <see cref="ClearDepth"/>, given where it lands on the drawn
    /// skin: no deeper than <see cref="DeepClearDepth"/>, on the new chest's breast (by its own weights, read at the
    /// landing — a point 28 mm deep is too far from the skin to ask itself), and where that chest stands at least
    /// <see cref="DeepGrowLeast"/> outside the source chest. Null when the chest's breast cannot be read.
    /// </summary>
    internal static Func<int, BodySurface.Hit, float, bool>? DeepClearTest(IReadOnlyList<SlotPair> pairs, string? garmentSlot)
    {
        var chest = pairs.FirstOrDefault(p => p.Slot == garmentSlot && p.Swaps);
        if (chest.TargetModel == null || BreastTest(chest) is not { } breast) return null;
        var grownFrom = new BodySurface(chest.Correspondence.Source,
                                        BodySurface.CellFor(MeanEdgeOf(chest.Correspondence.Source)));
        return (_, hit, s) => s >= -DeepClearDepth && breast(hit.Point)
                              && (grownFrom.Nearest(hit.Point, DeepGrowReach, out var was)
                                      ? Vector3.Dot(hit.Point - was.Point, hit.Normal)
                                      : DeepGrowReach) >= DeepGrowLeast;
    }

    /// <summary>What <see cref="ShapeAndPush"/> leaves for the passes after it.</summary>
    /// <param name="Shapes">Each kept piece's outcome, by index; null when nothing was kept.</param>
    /// <param name="DeepCleared">Nodes pushed out from deeper than <see cref="ClearDepth"/>; see <see cref="DeepClearTest"/>.</param>
    /// <param name="DeepWorst">The furthest any of them was pushed in all.</param>
    /// <param name="DeepAt">A garment vertex of each of them.</param>
    /// <param name="OpenEdgeFitted">Nodes the push-out took in by the made-on body's reading; see
    /// <see cref="TargetBody.Fitted"/>.</param>
    /// <param name="Fallbacks">Turns of kept pieces made by Horn's fit, both passes; see <see cref="TurnOf"/>.</param>
    private sealed record ShapeStage(int Pushed, float WorstPush, TargetBody? Drawn, FaceCheck? FaceCheck,
                                     float[]? PushedBy, ShapeOutcome[]? Shapes, int DeepCleared = 0, float DeepWorst = 0f,
                                     IReadOnlyList<int>? DeepAt = null, int OpenEdgeFitted = 0, int Fallbacks = 0);

    /// <summary>
    /// The kept pieces moved whole, the push-out, and the pieces whole again around where the push put them — then each
    /// piece judged: how much deeper the whole move left it in the drawn skin than the push-out's own answer (see
    /// <see cref="ReleaseSink"/>). Without <paramref name="before"/> there is no push-out and nothing is judged.
    /// </summary>
    private static ShapeStage ShapeAndPush(ModelParts garment, IReadOnlyList<SlotPair> pairs, string? garmentSlot,
                                           bool ownSlotSwapped, Sets sets, bool[] snapped, Vec3[] nodeDelta,
                                           IReadOnlyList<IReadOnlyCollection<int>>? keepShape, TargetBody? before,
                                           bool clearBody, Func<int, BodySurface.Hit, float, bool>? deep = null)
    {
        bool keeping = keepShape is { Count: > 0 };

        // Hard pieces whole, before the push-out measures them against the skin. Each piece's scale is taken here, from
        // the transfer's smooth answer, and kept after the push (see KeepShape).
        var scales = new float[keepShape?.Count ?? 0];
        Array.Fill(scales, float.NaN);
        int fallbacks = 0;
        if (keeping) KeepShape(sets, keepShape!, nodeDelta, scales, out fallbacks);
        if (before == null)
            return new ShapeStage(0, 0f, null, null, null, keeping ? new ShapeOutcome[keepShape!.Count] : null,
                                  Fallbacks: fallbacks);

        // The swapped-in body is what the cloth is pushed against. Measured on a sheer corset refitted Bibo+ to
        // Neolithe: pushed against the garment's transferred skin, 326 cup vertices came out buried up to 3.4 mm
        // inside the breast the file actually carries, against 2 in the author's own; the two surfaces are not
        // the same, and the one the solve could see is thrown away before the file is written.
        //
        // Only where the swap will really happen. TargetBody's remarks record the opposite measurement for a
        // garment that KEEPS its own skin: "This Old Thing" compresses the chest under its top, its cloth
        // legitimately sits inside the body, and pushing it out made that refit 50% worse. The swap is what
        // separates the two cases — it discards the author's compressed skin and puts the body's own full-size
        // mesh in its place, so there is no longer any compression for the cloth to be legitimately inside of.
        bool hasSkin = sets.ClothNodes.Length < sets.NodeCount;
        var after = TargetBody.Build(pairs, ownSlotSwapped ? null : garmentSlot,
                                     hasSkin ? Moved(garment, sets, nodeDelta) : null, before: false);

        // Cloth the transfer snapped onto a body vertex is the surface itself, and stays out — unless the body it is
        // drawn over is tessellated otherwise (see SnappedLandsElsewhere). Not with clearBody: there the standoff is the
        // full clearance, a node on the surface really is a coin flip, and it was never measured. And only when the
        // garment's OWN slot is swapped: a shoe keeping its feet that swaps only its legs (swapSlot) was never measured
        // either, and keeps the old rule.
        bool pushSnapped = !clearBody && !Tuned.NoSnapPush && SnappedLandsElsewhere(pairs, ownSlotSwapped);
        var pushable = new List<int>(sets.ClothNodes.Length);
        foreach (int n in sets.ClothNodes)
            if (!snapped[n] || pushSnapped) pushable.Add(n);

        int pushed = PushOut(sets, pushable, before, after, nodeDelta, clearBody, out float worstPush,
                             out var faceCheck, out var pushedBy, pushSnapped ? snapped : null, deep,
                             out var deepNodes, out float deepWorst, out int openEdgeFitted);
        int[]? deepAt = null;
        if (deepNodes.Count > 0)
        {
            var vertexOf = new int[sets.NodeCount];
            Array.Fill(vertexOf, -1);
            for (int v = sets.NodeOf.Length - 1; v >= 0; v--) vertexOf[sets.NodeOf[v]] = v;
            deepAt = [.. deepNodes.Select(n => vertexOf[n]).Where(v => v >= 0)];
        }

        // The push moves points one by one, and bent the pieces straight back: whole again, around where it put them.
        ShapeOutcome[]? shapes = null;
        if (keeping)
        {
            var bent = (Vec3[])nodeDelta.Clone();
            var placed = new bool[keepShape!.Count];
            var owner = new int[sets.NodeCount];
            Array.Fill(owner, -1);
            KeepShape(sets, keepShape, nodeDelta, scales, out int again, placed, owner);
            fallbacks += again;
            shapes = JudgeShapes(sets, placed, owner, bent, nodeDelta, SignedOff(after), SignedOff(before));
        }
        return new ShapeStage(pushed, worstPush, after, faceCheck, pushedBy, shapes, deepNodes.Count, deepWorst, deepAt,
                              openEdgeFitted, fallbacks);
    }

    /// <summary>
    /// Whether cloth the transfer snapped onto a body vertex can have the new skin through it: some pair whose skin the
    /// swap really draws maps onto a body that is not the source's own mesh.
    /// <para/>
    /// A snapped point lands where its source vertex lands, and keeps the source's tessellation. Onto a size of the same
    /// body every one of those faces lies on the target's own face, and pushing them would be noise. Onto a body
    /// tessellated otherwise it lies on the new skin only at its own vertices, and the skin bulges through between them:
    /// Laurel's tights — a copy of the Neolithe legs, 0.1 mm off them — refitted onto Rue+ had no vertex inside the new
    /// legs, yet 516 skin points through the tights and stocking below the lace band, the thigh in long streaks to the
    /// knee (see SkinThroughCloth). Left out of the push, nothing could clear them; let in, 7, none deeper than 0.5 mm.
    /// <para/>
    /// Only where the swap draws the pair's body: without it the skin under the cloth is the garment's own, moved by the
    /// same field as the cloth, and it bulges nowhere.
    /// </summary>
    /// <param name="replaceSkin">Whether the garment's own skin is swapped for the bodies' — the solve's
    /// ownSlotSwapped.</param>
    internal static bool SnappedLandsElsewhere(IReadOnlyList<SlotPair> pairs, bool replaceSkin)
        => replaceSkin && pairs.Any(p => p.Swaps && !p.Correspondence.SameMesh);

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
    /// <param name="signedOff">How far a point is outside the skin drawn under the garment (negative inside). Nothing
    /// here may take a cloth point into that skin, or deeper than it was: unfolding runs after the push-out, and nothing
    /// pushes it back out. Measured on "Seaside" (Rue Medium to Yiggle Small): unjudged, the relax drew the underbust
    /// into the smaller breast, 5 mm deep. Null when there is no drawn skin to keep clear of.</param>
    /// <returns>Triangles still folded when the rounds ran out — zero when the pass cleared them all.</returns>
    internal static int Unfold(Sets sets, Vec3[] nodeDelta, bool[] snapped, Func<Vector3, float>? signedOff = null)
    {
        var isSkin = new bool[sets.NodeCount];
        foreach (int n in sets.SkinNodes) isSkin[n] = true;

        var mayMove = new bool[sets.NodeCount];
        foreach (int n in sets.AllNodes) mayMove[n] = !snapped[n] && !isSkin[n];

        // Unfolding moves cloth after the push-out has put it clear of the skin, and nothing pushes it out again: so no
        // step of it may take a cloth point into the skin, or deeper than the push-out left it. The skin's own points
        // lie ON the skin and are not judged.
        Func<int, Vec3, bool>? mayGo = null;
        if (signedOff != null)
        {
            var entry = (Vec3[])nodeDelta.Clone();
            var entryOff = new float[sets.NodeCount];
            Array.Fill(entryOff, float.NaN);
            mayGo = (n, d) =>
            {
                if (isSkin[n]) return true;
                if (float.IsNaN(entryOff[n])) entryOff[n] = signedOff(Placed(sets, entry, n));
                return signedOff(ToVector(sets.NodeAt[n]) + ToVector(d)) >= MathF.Min(entryOff[n], 0f) - UnfoldBuryTolerance;
            };
        }

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
                var relaxed = new Vec3(
                    nodeDelta[n].X + (sum.X * inv - nodeDelta[n].X) * UnfoldRate,
                    nodeDelta[n].Y + (sum.Y * inv - nodeDelta[n].Y) * UnfoldRate,
                    nodeDelta[n].Z + (sum.Z * inv - nodeDelta[n].Z) * UnfoldRate);
                if (mayGo != null && !mayGo(n, relaxed)) continue;
                nodeDelta[n] = relaxed;
            }
        }

        // The rounds running out leaves the LAST relax untested: `folded` and `flagged` are from before it, so they
        // describe a mesh that no longer exists. Counted again — the give-up would otherwise scale corners that last
        // pass already straightened, surrendering their movement for nothing, and would measure its own work against
        // a number too high to beat. Breaking out is the other case, and there the reading is current and free.
        if (round == UnfoldRounds) folded = Folded(sets, nodeDelta, flagged);

        return folded == 0 || Tuned.NoGiveUp
            ? folded
            : GiveUpFolds(sets, nodeDelta, GiveUpFixed(sets, isSkin), flagged, folded, mayGo);
    }

    /// <summary>
    /// The nodes the give-up may not move: the skin, and every node the user holds. The give-up takes a corner toward
    /// the movement around it, so a held node — no movement of its own — would be dragged after its neighbours.
    /// </summary>
    internal static bool[] GiveUpFixed(Sets sets, bool[] isSkin)
    {
        var fixedNode = new bool[sets.NodeCount];
        Array.Fill(fixedNode, true);
        foreach (int n in sets.AllNodes) fixedNode[n] = isSkin[n];
        return fixedNode;
    }

    /// <summary>How far outside <paramref name="drawn"/> a point is, along the normal of the skin it is deepest in;
    /// past <see cref="PushProbeRange"/> it counts as clear.</summary>
    private static Func<Vector3, float> SignedOff(TargetBody drawn)
        => p => drawn.Deepest(p, PushProbeRange, out var hit) ? Vector3.Dot(p - hit.Point, hit.Normal) : float.PositiveInfinity;

    /// <summary>
    /// Whatever the relax could not turn back, give up its disagreement for: a folded triangle's corners are taken,
    /// half the way at a time, from their own movement toward the movement around them smoothed (see
    /// <see cref="UnfoldAnchorRounds"/>), and the test runs again.
    /// <para/>
    /// Averaging a folded node against its neighbours only works when the neighbours are right; where a whole
    /// patch is folded together they are all wrong the same way, and it converges to nothing. Measured on "Rana"
    /// refitted Bibo+ to Neolithe, a loose off-shoulder jacket whose own body mesh the author sculpted rather than
    /// copied (11% of its points snap): 424 triangles turned over, and 200 rounds of relaxing took that to 396.
    /// <para/>
    /// A smooth movement turns no triangle over, so this clears a fold whose corners are all ours while keeping the
    /// body-size change they share. Where the corner holding it is the skin's, nothing here reaches it — hence keeping
    /// the BEST reading rather than the last: this may not hand back a mesh worse than the one the relax gave it.
    /// </summary>
    /// <param name="isSkin">Nodes this may not move: the skin, which has to stay on the new body, and whatever the
    /// caller holds.</param>
    /// <param name="flagged">The corners of <paramref name="folded"/>, as <see cref="Folded"/> left them; written
    /// through.</param>
    /// <param name="folded">The relax's own last reading, so a garment it cleared is never re-counted.</param>
    /// <param name="mayGo">Whether a node may take a delta — see <see cref="Unfold"/>: a corner is never given up into
    /// the skin. Where the next step would bury it, it keeps the step it has and its fold stays. Null gives up without
    /// looking.</param>
    /// <returns>Triangles still folded — never more than <paramref name="folded"/>.</returns>
    internal static int GiveUpFolds(Sets sets, Vec3[] nodeDelta, bool[] isSkin, bool[] flagged, int folded,
                                    Func<int, Vec3, bool>? mayGo = null)
    {
        var full = (Vec3[])nodeDelta.Clone();
        var anchor = Smoothed(sets, full, UnfoldAnchorRounds);
        var give = new float[sets.NodeCount];
        Array.Fill(give, 1f);

        // Nodes stopped from going any further because the next step would bury them.
        var stuck = new bool[sets.NodeCount];

        int best = folded;
        float[]? bestGive = null;

        for (int pass = 0; pass < UnfoldGiveUpPasses; pass++)
        {
            bool moved = false;
            for (int n = 0; n < sets.NodeCount; n++)
            {
                // The skin has to stay ON the body, and a held point where the user left it; neither is ours to undo.
                if (!flagged[n] || isSkin[n] || stuck[n]) continue;
                float g = give[n] * UnfoldGiveUpRate;
                var step = Toward(anchor[n], full[n], g);
                if (mayGo != null && !mayGo(n, step))
                {
                    stuck[n] = true;
                    continue;
                }
                give[n] = g;
                nodeDelta[n] = step;
                moved = true;
            }
            if (!moved) break;   // every folded point this may move would be buried by the next step

            folded = Folded(sets, nodeDelta, flagged);
            if (folded == 0) return 0;
            if (folded < best) { best = folded; bestGive = (float[])give.Clone(); }
        }

        // Scaling one patch folds its neighbours, so the last pass is not always the best one — and a run that only
        // ever made things worse gives every point its movement back.
        if (folded > best)
        {
            for (int n = 0; n < sets.NodeCount; n++)
            {
                float g = bestGive?[n] ?? 1f;
                nodeDelta[n] = g >= 1f ? full[n] : Toward(anchor[n], full[n], g);
            }
            folded = Folded(sets, nodeDelta, flagged);
        }
        return folded;

        static Vec3 Toward(Vec3 anchor, Vec3 full, float give)
            => new(anchor.X + (full.X - anchor.X) * give, anchor.Y + (full.Y - anchor.Y) * give,
                   anchor.Z + (full.Z - anchor.Z) * give);
    }

    /// <summary>
    /// The displacement averaged over the garment's own surface, <paramref name="rounds"/> times: each node takes its
    /// neighbours' mean. What <see cref="GiveUpFolds"/> takes a folded corner's movement toward.
    /// </summary>
    private static Vec3[] Smoothed(Sets sets, Vec3[] delta, int rounds)
    {
        var cur = (Vec3[])delta.Clone();
        var next = new Vec3[cur.Length];
        for (int round = 0; round < rounds; round++)
        {
            for (int n = 0; n < cur.Length; n++)
            {
                var adj = sets.Adj[n];
                if (adj.Count == 0) { next[n] = cur[n]; continue; }
                float x = cur[n].X, y = cur[n].Y, z = cur[n].Z;
                foreach (int m in adj) { x += cur[m].X; y += cur[m].Y; z += cur[m].Z; }
                float inv = 1f / (adj.Count + 1);
                next[n] = new Vec3(x * inv, y * inv, z * inv);
            }
            (cur, next) = (next, cur);
        }
        return cur;
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

    /// <summary>How near two cloth points of different sheets have to sit, as authored, for <see cref="KnitLayers"/> to
    /// pair them (6 mm); within half of it a partner counts in full, fading to nothing at the edge.</summary>
    internal const float LayerKnitReach = 0.006f;

    /// <summary>How much longer than the straight line the way along the cloth has to be for two points to be on
    /// different sheets (3x). Along one sheet the two are about the same.</summary>
    internal const float LayerKnitDetour = 3f;

    /// <summary>Rounds of <see cref="KnitLayers"/>: each takes partners most of the way to one move.</summary>
    internal const int LayerKnitRounds = 4;

    /// <summary>
    /// Keep the space between two pieces of cloth what the author left it where the body stretches under both: every
    /// cloth point with partners — points within <see cref="LayerKnitReach"/> of it as authored that the cloth itself
    /// only reaches the long way round (<see cref="LayerKnitDetour"/>): another piece beside or over it, the far side
    /// of a fold — is given the average of its move and theirs, and the correction is spread along each piece over
    /// <see cref="LayerKnitSpread"/>, fading out, so the stretch goes into the cloth rather than the space between.
    /// <para/>
    /// The transfer gives every point the body's own change under it, and where the body grows a lot over a short
    /// distance that change stretches everything on it — including the gap between two pieces. Measured on "Sirius" (a
    /// corset over a blouse, both one mesh) from Neolithe Pushup XS to YAB+ Large: the skin across the top of the breast
    /// stretches about three times over, and the 2-4 mm between the corset's top edge and the blouse opened to 16 mm;
    /// the skin showed through the slit in game. <see cref="Knit"/> and <see cref="Relax"/> even each piece out along
    /// itself and cannot see the other one; run before them, this was undone by them. So it runs after the knit, the
    /// relax counts the same partners among its neighbours, and the change is spread rather than put on the edge alone
    /// — an edge moved by itself is a step the relax smooths straight back out.
    /// <para/>
    /// The garment's own body mesh neither moves nor counts — it is laid onto the new body exactly (see
    /// <see cref="LaySkin"/>). Against "This Old Thing"'s hand-made sizes the cloth error does not change (mean within
    /// 0.005 mm on every pair measured).
    /// </summary>
    private static void KnitLayers(Sets sets, List<(int Node, float W)>?[] partners, Vec3[] nodeDelta)
    {
        var isSkin = new bool[sets.NodeCount];
        foreach (int n in sets.SkinNodes) isSkin[n] = true;
        var moving = new bool[sets.NodeCount];
        foreach (int n in sets.AllNodes) moving[n] = !isSkin[n];

        // The band the correction spreads through: cloth within LayerKnitSpread of a partnered point, along the cloth.
        var way = new float[sets.NodeCount];
        Array.Fill(way, float.MaxValue);
        var queue = new PriorityQueue<int, float>();
        for (int n = 0; n < partners.Length; n++)
            if (partners[n] != null && moving[n]) { way[n] = 0f; queue.Enqueue(n, 0f); }
        if (queue.Count == 0) return;
        while (queue.TryDequeue(out int a, out float at))
        {
            if (at > way[a]) continue;
            foreach (int b in sets.Adj[a])
            {
                if (!moving[b]) continue;
                float to = at + Vector3.Distance(ToVector(sets.NodeAt[a]), ToVector(sets.NodeAt[b]));
                if (to > LayerKnitSpread || to >= way[b]) continue;
                way[b] = to;
                queue.Enqueue(b, to);
            }
        }
        var band = new List<int>();
        for (int n = 0; n < sets.NodeCount; n++)
            if (way[n] <= LayerKnitSpread && partners[n] == null) band.Add(n);

        var fix = new Vector3[sets.NodeCount];
        var next = new Vector3[sets.NodeCount];
        for (int round = 0; round < LayerKnitRounds; round++)
        {
            // What each partnered point should move by: its own move and its partners', weighted by nearness.
            Array.Clear(fix);
            for (int n = 0; n < partners.Length; n++)
            {
                if (partners[n] is not { } list || !moving[n]) continue;
                var sum = ToVector(nodeDelta[n]);
                float total = 1f;
                foreach (var (m, w) in list) { sum += ToVector(nodeDelta[m]) * w; total += w; }
                fix[n] = sum / total - ToVector(nodeDelta[n]);
            }

            // Spread smoothly into the band, to nothing at its far edge, so neither sheet gets a step where it stops.
            for (int it = 0; it < LayerKnitSmoothRounds; it++)
            {
                Array.Copy(fix, next, fix.Length);
                foreach (int n in band)
                {
                    var sum = Vector3.Zero;
                    int count = 0;
                    foreach (int m in sets.Adj[n])
                    {
                        if (!moving[m]) continue;
                        sum += way[m] <= LayerKnitSpread ? fix[m] : Vector3.Zero;
                        count++;
                    }
                    if (count > 0) next[n] = sum / count;
                }
                Array.Copy(next, fix, fix.Length);
            }

            for (int n = 0; n < sets.NodeCount; n++)
                if (moving[n] && way[n] <= LayerKnitSpread)
                    nodeDelta[n] = new Vec3(nodeDelta[n].X + fix[n].X, nodeDelta[n].Y + fix[n].Y, nodeDelta[n].Z + fix[n].Z);
        }
    }

    /// <summary>How far along the cloth <see cref="KnitLayers"/> spreads its correction from where two sheets meet
    /// (15 mm), so the stretch the body asks for goes into the cloth rather than the space between the sheets.</summary>
    internal const float LayerKnitSpread = 0.015f;

    /// <summary>Rounds of neighbour-averaging that spread the correction across <see cref="LayerKnitSpread"/>.</summary>
    internal const int LayerKnitSmoothRounds = 40;

    /// <summary>
    /// Each cloth node's partners for <see cref="KnitLayers"/>, weighted by nearness: the points within
    /// <see cref="LayerKnitReach"/> of it as authored that the cloth only reaches the long way round. Null for a node
    /// with none, and for a node that landed on the body exactly, which is a partner to others but takes no average of
    /// its own (the spread still reaches it). The garment's own body mesh and held nodes are neither.
    /// </summary>
    private static List<(int Node, float W)>?[] LayerPartners(Sets sets, bool[] snapped)
    {
        var isSkin = new bool[sets.NodeCount];
        foreach (int n in sets.SkinNodes) isSkin[n] = true;
        var cloth = new List<int>(sets.AllNodes.Length);
        foreach (int n in sets.AllNodes)
            if (!isSkin[n]) cloth.Add(n);

        (int, int, int) Cell(Vec3 p) => ((int)MathF.Floor(p.X / LayerKnitReach), (int)MathF.Floor(p.Y / LayerKnitReach),
                                         (int)MathF.Floor(p.Z / LayerKnitReach));
        var grid = new Dictionary<(int, int, int), List<int>>();
        foreach (int n in cloth)
        {
            var c = Cell(sets.NodeAt[n]);
            if (!grid.TryGetValue(c, out var bucket)) grid[c] = bucket = [];
            bucket.Add(n);
        }

        var partners = new List<(int Node, float W)>?[sets.NodeCount];
        var near = new List<(int Node, float D)>();
        var way = new Dictionary<int, float>();
        var queue = new PriorityQueue<int, float>();
        foreach (int n in cloth)
        {
            if (snapped[n]) continue;
            var p = ToVector(sets.NodeAt[n]);
            var (cx, cy, cz) = Cell(sets.NodeAt[n]);
            near.Clear();
            float farthest = 0f;
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int z = cz - 1; z <= cz + 1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int m in bucket)
                {
                    if (m == n) continue;
                    float d = Vector3.Distance(p, ToVector(sets.NodeAt[m]));
                    if (d >= LayerKnitReach) continue;
                    near.Add((m, d));
                    farthest = MathF.Max(farthest, d);
                }
            }
            if (near.Count == 0) continue;

            // The way along the cloth, as far as it could matter.
            float limit = farthest * LayerKnitDetour;
            way.Clear();
            queue.Clear();
            way[n] = 0f;
            queue.Enqueue(n, 0f);
            while (queue.TryDequeue(out int a, out float at))
            {
                if (at > way[a]) continue;
                foreach (int b in sets.Adj[a])
                {
                    float to = at + Vector3.Distance(ToVector(sets.NodeAt[a]), ToVector(sets.NodeAt[b]));
                    if (to > limit || (way.TryGetValue(b, out float had) && had <= to)) continue;
                    way[b] = to;
                    queue.Enqueue(b, to);
                }
            }

            List<(int, float)>? list = null;
            foreach (var (m, d) in near)
            {
                if (isSkin[m]) continue;
                if (way.TryGetValue(m, out float along) && along <= d * LayerKnitDetour) continue;
                (list ??= []).Add((m, MathF.Min(1f, 2f * (1f - d / LayerKnitReach))));
            }
            partners[n] = list;
        }
        return partners;
    }

    /// <summary>How far out of line with its neighbours a cloth node's movement must be for <see cref="Relax"/> to take
    /// it in (0.5 mm).</summary>
    internal const float RelaxThreshold = 5e-4f;

    /// <summary>Rounds of <see cref="Relax"/>.</summary>
    internal const int RelaxRounds = 6;

    /// <summary>How far each <see cref="Relax"/> round takes a node toward its neighbours' movement (half way).</summary>
    internal const float RelaxRate = 0.5f;

    /// <summary>
    /// Even out the cloth's movement where it is out of line with its neighbours: each cloth node moving more than
    /// <see cref="RelaxThreshold"/> differently from its neighbours' average is taken part way toward it, a few rounds.
    /// The movement, not the position, is what is smoothed, so the author's own folds and edges ride along.
    /// <para/>
    /// <see cref="Knit"/> already does this right after the transfer, but leaves out every node the transfer landed on
    /// a body vertex exactly — and a garment authored hugging the skin is a mix of those and nodes between them. On a
    /// corset the top of the cups came out jagged in game ("Sirius", every size): the snapped nodes jumped to the new
    /// body's vertices, the ones between followed the field, and nothing evened the two out. So here snapped cloth is
    /// relaxed too; the garment's own body mesh, which has to stay on the body, and held nodes are not.
    /// <para/>
    /// A node never comes closer to the drawn skin than <see cref="Clearance"/> (or than it already is, where that is
    /// less), and a round that turns a triangle over is taken back for that triangle's corners. Runs after the fold relax and before the underbust lift and <see cref="ClearFaces"/>,
    /// which have the last word on clearance.
    /// </summary>
    /// <param name="signedOff">How far a point is outside the drawn skin (negative inside); null when there is none.</param>
    /// <param name="stay">Nodes it may not move — the hard pieces.</param>
    /// <returns>How many nodes it moved.</returns>
    /// <param name="layers">Each node's partners on other sheets (see <see cref="LayerPartners"/>), which count among its
    /// neighbours by their weight: relaxed toward its own sheet alone, a sheet laid over another is evened out along
    /// itself and drifts from the one under it. Null for none.</param>
    internal static int Relax(Sets sets, Vec3[] nodeDelta, Func<Vector3, float>? signedOff, bool[]? stay = null,
                              List<(int Node, float W)>?[]? layers = null)
    {
        var isSkin = new bool[sets.NodeCount];
        foreach (int n in sets.SkinNodes) isSkin[n] = true;
        var moved = new bool[sets.NodeCount];

        for (int round = 0; round < RelaxRounds; round++)
        {
            var was = new Dictionary<int, Vec3>();
            var next = new Dictionary<int, Vec3>();
            foreach (int n in sets.ClothNodes)
            {
                if (isSkin[n] || (stay != null && stay[n]) || sets.Adj[n] is not { Count: > 0 } neighbours) continue;
                float x = 0f, y = 0f, z = 0f, count = neighbours.Count;
                foreach (int m in neighbours) { x += nodeDelta[m].X; y += nodeDelta[m].Y; z += nodeDelta[m].Z; }
                if (layers?[n] is { } partners)
                    foreach (var (m, w) in partners)
                    {
                        x += nodeDelta[m].X * w; y += nodeDelta[m].Y * w; z += nodeDelta[m].Z * w;
                        count += w;
                    }
                float inv = 1f / count;
                var off = new Vector3(x * inv - nodeDelta[n].X, y * inv - nodeDelta[n].Y, z * inv - nodeDelta[n].Z);
                if (off.Length() <= RelaxThreshold) continue;
                var d = nodeDelta[n];
                var to = new Vec3(d.X + off.X * RelaxRate, d.Y + off.Y * RelaxRate, d.Z + off.Z * RelaxRate);
                // Never closer to the skin than the clearance, or than it already is where that is less. Only keeping
                // it out of the skin let the relax lay cloth down on it, clear at rest and through it as soon as a pose
                // pressed the two together: on "Sirius" refitted to Rue+ Large the visible cloth within 0.3 mm of the
                // skin went from 4 to 23, and skin showed through the top of the breasts and the hips in game.
                if (signedOff != null)
                {
                    var p0 = Placed(sets, nodeDelta, n);
                    var p1 = new Vector3(sets.NodeAt[n].X + to.X, sets.NodeAt[n].Y + to.Y, sets.NodeAt[n].Z + to.Z);
                    float s0 = signedOff(p0), s1 = signedOff(p1);
                    if (s1 < MathF.Min(s0, Clearance)) continue;
                }
                next[n] = to;
            }
            if (next.Count == 0) break;

            // Folds the round would make before it is applied, so its triangles can be taken back.
            var before = new bool[sets.Tris.Length / 3];
            for (int t = 0; t < before.Length; t++) before[t] = TriTurnedOver(sets, nodeDelta, t);
            foreach (var (n, d) in next)
            {
                was[n] = nodeDelta[n];
                nodeDelta[n] = d;
            }
            for (int t = 0; t < before.Length; t++)
            {
                if (before[t] || !TriTurnedOver(sets, nodeDelta, t)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = sets.Tris[t * 3 + k];
                    if (v < 0 || v >= sets.NodeOf.Length) continue;
                    int n = sets.NodeOf[v];
                    if (was.TryGetValue(n, out var back)) nodeDelta[n] = back;
                }
            }
            foreach (var n in next.Keys)
                if (!was.TryGetValue(n, out var back) || !back.Equals(nodeDelta[n])) moved[n] = true;
        }
        return moved.Count(m => m);

        // Facing the other way from how the author drew it.
        static bool TriTurnedOver(Sets sets, Vec3[] nodeDelta, int t)
        {
            int va = sets.Tris[t * 3], vb = sets.Tris[t * 3 + 1], vc = sets.Tris[t * 3 + 2];
            if (va < 0 || vb < 0 || vc < 0
                || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) return false;
            int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
            if (a == b || b == c || c == a) return false;
            var at = ToVector(sets.NodeAt[a]);
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[b]) - at, ToVector(sets.NodeAt[c]) - at);
            if (n0.Length() <= 1e-12f) return false;
            var to = Placed(sets, nodeDelta, a);
            var n1 = Vector3.Cross(Placed(sets, nodeDelta, b) - to, Placed(sets, nodeDelta, c) - to);
            return Vector3.Dot(n0, n1) <= 0f;
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
            var surface = pair.TargetSurface();
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
