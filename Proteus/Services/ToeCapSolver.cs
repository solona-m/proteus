using System;
using System.Collections.Generic;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;

internal static partial class ToeCapSolver
{
    /// <summary>Mask value at which a vertex counts as part of the cap's core rather than its soft edge; only the core sets the axis, band and slicing.</summary>
    private const float ToeCapCoreWeight = 0.5f;

    /// <summary>Bounds on the ring count swept from the rim to the tip; the actual number follows edge length.</summary>
    private const int MinRings = 4, MaxRings = 64;

    /// <summary>Ring spacing as a fraction of the mesh's own edge length; below 1 the cap is finer than the body it replaces.</summary>
    private const float RingDensity = 0.5f;

    /// <summary>Share of the cut region's vertices the rings may consume, leaving the rest as slack.</summary>
    private const float DonorBudget = 0.75f;

    /// <summary>Rim vertices needed before a cut boundary is a usable loop to sew onto.</summary>
    private const int MinRimNodes = 8;

    /// <summary>How much of an island the cap may claim; past this there is no rim left to sew to and the island is left alone.</summary>
    private const float MaxCoreFraction = 0.8f;

    /// <summary>How far past the last ring the end of the cap reaches, in ring spacings.</summary>
    private const float TipReach = 0.5f;

    /// <summary>Shrinking rings that round the end off before it closes; the grid patch closes whatever is left.</summary>
    private const int TipRings = 3;

    /// <summary>Fewest slots a dome ring is worth building with; below this the closing patch takes over.</summary>
    private const int MinDomeSlots = 8;

    /// <summary>How far the end domes over, as a fraction of the cap's own radius there.</summary>
    private const float TipRound = 0.3f;

    /// <summary>Closest the relaxed cap may come to the skin, in mesh edge lengths (the fabric's thickness).</summary>
    private const float SkinClearance = 0.2f;

    /// <summary>
    /// Furthest the finished cap may float above the skin under it, in mesh edge lengths; a slot bridging
    /// a gap has its allowance opened out in proportion.
    /// </summary>
    private const float MaxStandoff = 0.5f;

    /// <summary>How bridged a slot must be before nothing may pull it down toward the skin at all.</summary>
    private const float BridgeExempt = 0.999f;

    /// <summary>Passes lifting the cap off skin that comes through the middle of a face; stops early once a pass finds nothing.</summary>
    private const int PokePasses = 4;

    /// <summary>Ceiling on how far that lift may move any one cap vertex, in mesh edge lengths, so a bad measurement cannot send a vertex off the foot.</summary>
    private const float MaxPokeLift = 1.5f;

    /// <summary>
    /// How far to the side the cap may be and still count as covering a skin vertex, in edge lengths;
    /// keeps a lift on a toe's inner flank from pushing the bridge into the neighbouring toe.
    /// </summary>
    private const float PokeReach = 0.25f;

    /// <summary>Aspect ratio above which a cap face is badly enough shaped to be worth evening out.</summary>
    private const float TangentTrigger = 4f;

    /// <summary>Passes of that evening-out. It converges; more than this buys nothing.</summary>
    private const int TangentPasses = 30;

    /// <summary>How far the evening-out may slide any vertex from where the rings placed it, in edge lengths.</summary>
    private const float TangentClamp = 0.75f;

    /// <summary>Skin triangles shortlisted per cap vertex, so clearance can be enforced on every pass.</summary>
    private const int SkinCandidates = 16;

    /// <summary>How wide a gap must be, in edge lengths, before the cap spans it rather than following the surface down into it.</summary>
    private const float BridgeSpan = 1.5f;

    /// <summary>Angular buckets a cross-section's outline is read into, when it has more slots than this.</summary>
    private const int MinOutlineBins = 32;

    /// <summary>Smoothing passes over the finished cap — the equivalent of relaxing it by hand.</summary>
    private const int RelaxPasses = 24;

    /// <summary>Smoothing passes over the end once the closing patch exists; the patch has never been smoothed when this runs.</summary>
    private const int TipRelaxPasses = 24;

    /// <summary>Extra rings behind the dome the end relax may move, beyond the dome itself.</summary>
    private const int TipRelaxSpan = 3;

    /// <summary>How many rings back from the rim the join is smoothed over; the first rings inherit the cut boundary's uneven spacing.</summary>
    private const int RimRelaxRings = 4;

    /// <summary>How far each pass moves a vertex toward its neighbours' average.</summary>
    private const float RelaxRate = 0.5f;

    /// <summary>How firmly the relax holds each vertex in its own ring's plane, 1 being rigidly; rigid converges lumpy.</summary>
    private const float RingPlaneHold = 0.5f;

    /// <summary>How far the cap stands off the outline it is built from, as a fraction of the cross-section's radius (the fabric's thickness).</summary>
    private const float CapClearance = 0.02f;

    /// <summary>How far beyond its own slice a cross-section reads points for its hull, in slice thicknesses.</summary>
    private const float SliceWindow = 0.7f;

    /// <summary>Largest share of a mesh an island may be and still be dropped when the cap swallows it whole; a whole masked foot must never be removed.</summary>
    private const float SmallIslandFraction = 0.25f;

    /// <summary>Smallest masked island worth capping; guards against a stray scrap being treated as its own toe box.</summary>
    private const int MinToeCapNodes = 24;

    /// <summary>Fewest slots a ring is built with, however small its cross-section gets.</summary>
    private const int MinRingSlots = 12;

    /// <summary>Closest two neighbouring slots in a ring may be smoothed, in edge lengths.</summary>
    private const float SlotMinGap = 0.35f;

    /// <summary>What fraction of the rim's slot count an average ring is expected to carry, for budgeting donors.</summary>
    private const float RingWidthEstimate = 0.7f;

    /// <summary>Points a cross-section needs before its hull is a meaningful outline.</summary>
    private const int MinSliceNodes = 8;

    /// <summary>How many times a cross-section may widen its band looking for enough points.</summary>
    private const int BandWidenSteps = 5;

    /// <summary>
    /// Toe cap: per-vertex displacement that inflates the masked region onto a smooth envelope, so a
    /// stocking shell webs the gaps between the toes instead of sleeving each toe individually.
    /// Vertices are welded by source position first and each weld group moves by one shared delta.
    /// Returns null when nothing is masked.
    /// </summary>
    internal static Vec3[]? ToeCapDelta(
        Vec3[] pos, Vec3[] nrm, (float U, float V)[] uv, ushort[] tris,
        byte[] mask, int mw, int mh, float strength)
        => ToeCapSolve(pos, nrm, uv, tris, mask, mw, mh, strength)?.Delta;

    /// <summary>What the toe cap decided: the displacement, plus the welding and per-node data the normal pass needs.</summary>
    internal sealed class ToeCapPlan
    {
        /// <summary>Per-vertex displacement, indexed like the mesh's vertices.</summary>
        public required Vec3[] Delta { get; init; }

        /// <summary>Vertex index -> welded node index.</summary>
        public required int[] NodeOf { get; init; }

        /// <summary>Per-node mask weight (max over the node's members), 0 where the cap left it alone.</summary>
        public required float[] NodeWeight { get; init; }

        /// <summary>Per-node normalized average of the members' SOURCE normals.</summary>
        public required Vec3[] NodeNormal { get; init; }

        /// <summary>Nodes inside the cap: every triangle touching one is cut out and replaced.</summary>
        public required bool[] CutNode { get; init; }

        /// <summary>Per-node UV for the nodes the cap moved, projected back onto the surface it replaced; null when nothing moved.</summary>
        public (float U, float V)[]? NodeUV { get; init; }

        /// <summary>The rebuilt cap, as mesh-local vertex indices.</summary>
        public required List<(ushort A, ushort B, ushort C)> NewTriangles { get; init; }

        /// <summary>Nodes on an island the cap swallowed whole: their triangles are simply removed.</summary>
        public required bool[] DropNode { get; init; }


        /// <summary>Does this triangle belong to the region the cap replaced?</summary>
        public bool IsCut(ushort a, ushort b, ushort c)
            => CutNode[NodeOf[a]] || CutNode[NodeOf[b]] || CutNode[NodeOf[c]];

        /// <summary>Is this triangle entirely on a swallowed island, and so nothing the shell should draw?</summary>
        public bool IsDropped(ushort a, ushort b, ushort c)
            => DropNode[NodeOf[a]] && DropNode[NodeOf[b]] && DropNode[NodeOf[c]];
    }

    internal static ToeCapPlan? ToeCapSolve(
        Vec3[] pos, Vec3[] nrm, (float U, float V)[] uv, ushort[] tris,
        byte[] mask, int mw, int mh, float strength, Action<string>? capLogSink = null,
        bool buildGeometry = true)
    {
        return new ToeCapSolution(pos, nrm, uv, tris, mask, mw, mh, strength, capLogSink, buildGeometry).Run();
    }
}
