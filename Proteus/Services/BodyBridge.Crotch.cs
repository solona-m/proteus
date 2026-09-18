using System;
using System.Collections.Generic;

namespace Proteus.Services;

using static Proteus.Services.SecondSkinWriter;

using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    /// <summary>How many [1,2,1] passes the per-band apex line is smoothed with before the chords are drawn between them.</summary>
    private const int BustBandSmoothing = 3;

    /// <summary>
    /// The crotch fold, flattened to the surface either side of it. A body pass: it lowers as well as raises, so the
    /// shell has to be cut from the result. A plain 3-D relax cannot close an invagination without destroying the
    /// surroundings; the chord over the whole hip front finds the two thighs as its lobes and webs the legs together,
    /// hence the corridor either side of the midline; and inside a crease the walls face each other, so the outward
    /// direction is supplied. Two-sided, because the fold's cross-section is a W with a ridge on the midline.
    /// </summary>
    /// <param name="hip">
    /// The hip bone's influence with the thigh rivalry applied: the span's region, where the veto must stay, since
    /// spanning between two legs webs them together.
    /// </param>
    /// <param name="near">
    /// The same influence without that veto: the relax's region. Safe here because a Laplacian moves a node along
    /// mesh edges, and below the crotch the two legs share none; geometry (the box below) bounds it instead.
    /// </param>
    private static BustBridgePlan? FoldPlan(Vec3[] pos, Vec3[] nrm, ushort[] tris, float[] hip, float[] near,
                                            bool[]? covered, float strength, Action<string>? log)
    {
        int vc = pos.Length;

        // The front of the body, by position rather than by facing (see the summary). Model +Z is forward on every body.
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < vc; i++)
        {
            if (hip[i] <= 0f || pos[i].Z <= 0f) continue;
            lo = MathF.Min(lo, pos[i].X); hi = MathF.Max(hi, pos[i].X);
        }
        if (hi <= lo) { log?.Invoke("crotch fold: no hip-owned surface on the front"); return null; }

        // The corridor's width comes from the crotch, not the whole region (the hip owns the waist too). The crotch is
        // the lowest height at which the body still has surface on the midline; below it the legs are separate.
        float mid = (lo + hi) * 0.5f;
        float half = (hi - lo) * 0.5f;

        float lowest = float.MaxValue;
        for (int i = 0; i < vc; i++)
        {
            if (hip[i] <= 0f || pos[i].Z <= 0f) continue;
            if (MathF.Abs(pos[i].X - mid) > half * FoldMidlineProbe) continue;
            lowest = MathF.Min(lowest, pos[i].Y);
        }
        if (lowest == float.MaxValue)
        {
            log?.Invoke("crotch fold: the body has no surface on the front midline — nothing to flatten");
            return null;
        }

        float wLo = float.MaxValue, wHi = float.MinValue;
        for (int i = 0; i < vc; i++)
        {
            if (hip[i] <= 0f || pos[i].Z <= 0f) continue;
            if (pos[i].Y > lowest + half * FoldCrotchBand) continue;
            wLo = MathF.Min(wLo, pos[i].X); wHi = MathF.Max(wHi, pos[i].X);
        }
        float crotchHalf = wHi > wLo ? (wHi - wLo) * 0.5f : half;

        // IS THAT ACTUALLY A CROTCH? Everything below is scaled by it. A skin mesh that stops at the hips has its lowest
        // midline surface at its own bottom edge, which is a waist, and the pass would reshape the belly. Two independent
        // tests: a crotch is narrow (a small fraction of the pelvis around it), and a crotch has legs below it. Measured on
        // `near`, not `hip`: the thigh veto zeroes the hip's weight exactly where the legs begin.
        float bottom = float.MaxValue;
        for (int i = 0; i < vc; i++)
            if (near[i] > 0f && pos[i].Z > 0f) bottom = MathF.Min(bottom, pos[i].Y);

        if (crotchHalf > half * FoldCrotchMaxWidth)
        {
            log?.Invoke($"crotch fold: SKIPPED, what the midline found at y={lowest:0.###} is "
                      + $"{crotchHalf * 2000:0.#}mm across against a hip region {half * 2000:0.#}mm wide — "
                      + "that is a waist, not a crotch, and this mesh most likely stops at the hips");
            return null;
        }
        if (bottom == float.MaxValue || lowest - bottom < crotchHalf * FoldLegsBelow)
        {
            log?.Invoke($"crotch fold: SKIPPED, the body reaches only {(lowest - bottom) * 1000:0.#}mm "
                      + $"below y={lowest:0.###} — there are no legs under it, so that is the edge of the "
                      + "mesh rather than the place they meet");
            return null;
        }

        float corridor = crotchHalf * FoldCorridor;

        // Bounded above as well: the hip owns the belly and the corridor is a vertical strip. The fold lives just above
        // where the legs meet, so the crotch's width is the ruler.
        float ceiling = lowest + crotchHalf * 2f * FoldHeight;

        // Every edge of this region is a line through open skin, not a garment's hem, so each is feathered by hand in the
        // crotch's own width: sideways toward the inner thigh, upward past the ceiling into the belly, and forward from
        // the mid-plane (where the height field's axis becomes tangent to the surface), which fades in rather than out.
        float feather = corridor * (FoldFeather - 1f);
        float roof = (ceiling - lowest) * (FoldFeather - 1f);
        float frontFade = crotchHalf * FoldFrontFade;
        float back = crotchHalf * FoldBack;

        float Fade(float x, float width) => width <= 1e-9f ? 1f : 1f - Smoothstep(Math.Clamp(x / width, 0f, 1f));

        var w = new float[vc];
        var ramp = new float[vc];
        var relax = new float[vc];
        // A floor as well: without the thigh veto the relax region would run down the inner thighs for as long as the
        // corridor holds them, and a strip of smoothed skin down each leg is a seam.
        float floorY = lowest - crotchHalf * FoldDrop;
        float drop = crotchHalf * FoldDropFeather;

        int seeded = 0, relaxed = 0, under = 0;
        for (int i = 0; i < vc; i++)
        {
            float side = MathF.Abs(pos[i].X - mid);
            if (side > corridor + feather) continue;
            float above = pos[i].Y - ceiling;
            if (above > roof) continue;
            float below = floorY - pos[i].Y;
            if (below > drop) continue;

            float box = Fade(side - corridor, feather) * Fade(above, roof) * Fade(below, drop);
            if (box <= 0f) continue;

            // The height field: front-facing only, faded in from the mid-plane, still subject to the thigh veto, and faded
            // by facing. Where the surface lies along the axis a push slides the skin sideways, and adjacent vertices sliding
            // across a millimetre edge pass through each other; position is only a proxy for orientation, the normal says it.
            if (hip[i] > 0f && pos[i].Z > 0f)
            {
                float square = Smoothstep(Math.Clamp(nrm[i].Z / FoldFacing, 0f, 1f));
                float f = box * Smoothstep(Math.Clamp(pos[i].Z / frontFade, 0f, 1f)) * square;
                if (f > 0f) { w[i] = hip[i]; ramp[i] = f; seeded++; }
            }

            // The relax: the same box, carried on round underneath and taking the thighs in with it (see the relaxSeed
            // parameter on BustBridgeSolve, and `near` above for why the veto is lifted).
            if (near[i] > 0f)
            {
                float b = Fade(-pos[i].Z, back);

                // Confined to the fold's own width, narrower than the corridor: `box` holds full strength inside the corridor,
                // and the inner thigh is where a garment's leg trim runs. Full strength to half the corridor covers the shoulders;
                // tapering from the midline would weaken them.
                float side2 = MathF.Abs(pos[i].X - mid);
                float core = corridor * FoldRelaxCore;
                float confine = side2 <= core ? 1f
                              : 1f - Smoothstep(Math.Clamp((side2 - core) / (corridor - core), 0f, 1f));

                if (b > 0f && confine > 0f)
                { relax[i] = box * b * confine; relaxed++; if (pos[i].Z <= 0f) under++; }
            }
        }
        if (seeded < MinBustBridgeNodes)
        {
            log?.Invoke($"crotch fold: {seeded} vertex(es) in the corridor — too few to describe a fold");
            return null;
        }
        log?.Invoke($"crotch fold: {seeded} vertex(es) in a corridor {corridor * 2000:0.#}mm wide about "
                  + $"x={mid:0.####} (feathered out to {(corridor + feather) * 2000:0.#}mm), {relaxed} "
                  + $"in the relax region ({under} of them behind the mid-plane), reaching "
                  + $"{back * 1000:0.#}mm behind it and down to y={floorY:0.###}, from a crotch "
                  + $"{crotchHalf * 2000:0.#}mm across at y={lowest:0.###} (the hip region itself is "
                  + $"{half * 2000:0.#}mm across)");

        // pinBoundary: this runs on the body, and the crotch is where a body is most likely to have an open edge.
        return BustBridgeSolve(pos, nrm, tris, w, strength, log, covered,
                               smoothStrength: 0f, fillGap: false,
                               outward: new Vec3(0, 0, 1), envelope: true,
                               ramp: ramp, relaxSeed: relax, pinBoundary: true);
    }

    /// <summary>
    /// The garment flat across the underside of the crotch: in each front-to-back slice, the underside between the
    /// lowest point either side of the midline is laid down onto the straight line between them. Null when nothing
    /// moves. The forward-axis spans run along this surface; down is the way off the body here.
    /// </summary>
    internal static BustBridgePlan? CrotchFlatAcross(Vec3[] pos, Vec3[] nrm, ushort[] tris, float[] hip, bool[]? covered,
                                                    float strength, Action<string>? log)
    {
        return new CrotchFlattening(pos, nrm, tris, hip, covered, strength, log).Run();
    }

    /// <summary>Passes smoothing each slice's flat line front to back.</summary>
    private const int CrotchFlatLineSmooth = 3;

    /// <summary>How far down a normal must face to count as the crotch's underside.</summary>
    private const float CrotchDownFacing = 0.5f;

    /// <summary>Share of its height above the flat line a node in the notch keeps, so the notch's faces keep their order.</summary>
    private const float CrotchFlatKeep = 0.05f;

    /// <summary>Half-width of the seam the slit's inside is packed into, in its order over the surface.</summary>
    private const float CrotchSeamHalf = 0.001f;

    /// <summary>How far behind the body's mid-plane the flattening reaches — the slit's back end, short of the perineum.</summary>
    private const float CrotchFlatBehind = 0.008f;

    /// <summary>Half-width of the slit between the labia: the flat line's ends are taken outside it.</summary>
    private const float CrotchSlitHalf = 0.003f;

    /// <summary>Rings in from the garment's cut edge over which the crotch flattening fades in.</summary>
    private const int CrotchFlatHemRings = 6;

    /// <summary>Most rounds of drawing back crotch-flattening edges that pass through the body.</summary>
    private const int CrotchFlatEdgeRounds = 12;

    /// <summary>Half-width either side of the midline laid flat at full strength — the labia and the notch between.</summary>
    private const float CrotchFlatHalf = 0.012f;

    /// <summary>Further width over which the flattening fades out toward the inner thighs.</summary>
    private const float CrotchFlatFeather = 0.006f;

    /// <summary>How far above the crotch's lowest point the underside may reach and still be flattened.</summary>
    private const float CrotchFlatRise = 0.03f;

    /// <summary>Front-to-back slice depth.</summary>
    private const float CrotchFlatBand = 0.003f;

    /// <summary>Slices at each end over which the flattening fades in.</summary>
    private const float CrotchFlatEndBands = 2f;

    /// <summary>The most any node is laid down — past the flattening's own reach, so no node in the seam is left standing.</summary>
    private const float CrotchFlatMaxDrop = 0.035f;

    /// <summary>Smoothing passes over the drop.</summary>
    private const int CrotchFlatSmooth = 3;

    /// <summary>
    /// How wide the fold's corridor is, as a fraction of the crotch's width where the legs meet: wide enough to hold
    /// the fold's shoulders and no wider, because past them is the inner thigh.
    /// </summary>
    private const float FoldCorridor = 0.25f;

    /// <summary>
    /// How much of the corridor the relax holds at full strength before fading, as a fraction of the corridor's
    /// half-width. Not the corridor's feather (which fades past the edge); this decides how much of the inside the
    /// relax may work on, so it lets go of the inner thigh where a leg trim lies.
    /// </summary>
    private const float FoldRelaxCore = 0.5f;

    /// <summary>
    /// Flatten each row of the region onto a line fitted across its own outer surface: the target for the crotch
    /// fold, where the chord cannot work. <see cref="ChordTarget"/> spans a valley between two peaks; the fold is a
    /// peak between two valleys, so the chord hops the ridge and leaves the grooves. This moves the surface onto the
    /// line in both directions. Fitted to the envelope, not the region's nodes: the fold's interior walls sit far
    /// behind the visible surface, and least squares over them drags the line into the body.
    /// </summary>
    private static float[] EnvelopeTarget(float[] h0, float[] lat, float[] ver, float[] w, int count,
                                          List<int>[] adj, Vec3[] pos, Action<string>? log)
    {
        return new EnvelopeSolution(h0, lat, ver, w, count, adj, pos, log).Run();
    }

    /// <summary>
    /// How wide the envelope's lateral low-pass is, as a distance on the body: where the body's shape ends and the
    /// fold begins. In metres, not bins or passes, because the bins span the region's own extent. Above the fold's
    /// wavelengths and well below the corridor's width; too wide converges on a straight line across the corridor.
    /// </summary>
    private const float EnvelopeSmoothSigma = 0.005f;

    /// <summary>
    /// The furthest a node may be moved onto the envelope, as a multiple of its own band's relief: a bound that
    /// scales with the body. Three gives the ask room to land while refusing to move a node several times the
    /// height of anything near it.
    /// </summary>
    private const float EnvelopeMaxMove = 3.0f;

    /// <summary>Lateral bins the envelope is sampled in, across the region's width.</summary>
    private const int EnvelopeBins = 16;

    /// <summary>Fitted samples a band needs before its line is trusted.</summary>
    private const int EnvelopeMinBins = 4;

    /// <summary>
    /// How far behind its bin's frontmost node a node may sit and still count as the outer surface, as a multiple of
    /// its band's relief; past this it is inside the fold. Above 1 so the threshold sits clear of the feature: a
    /// threshold inside the relief runs through the fold and snaps neighbours differently.
    /// </summary>
    private const float EnvelopeSkin = 1.5f;

    /// <summary>How close to the midline a vertex counts as being ON it, when looking for the lowest place
    /// the body still spans between the legs. A fraction of the hip region's half-width.</summary>
    private const float FoldMidlineProbe = 0.06f;

    /// <summary>
    /// How far above the crotch the fold's region reaches, as a multiple of the crotch's own width. The
    /// fold sits directly above where the legs meet and is done well before the belly starts; the hip bone
    /// is not, so something has to say where to stop.
    /// </summary>
    private const float FoldHeight = 0.3f;

    /// <summary>How tall a slice above that lowest point the crotch's width is measured over, as a
    /// fraction of the hip region's half-width. Enough rows to average out one ragged one.</summary>
    private const float FoldCrotchBand = 0.10f;

    /// <summary>The widest a crotch may be as a fraction of the hip region around it; a mesh that stops at the hips
    /// measures far wider, and getting this wrong is a hole in the stomach.</summary>
    private const float FoldCrotchMaxWidth = 0.45f;

    /// <summary>How far the body must continue below the crotch before it counts as having legs, as a
    /// multiple of the crotch's own half-width. A limb goes on for many; a mesh boundary for none.</summary>
    private const float FoldLegsBelow = 1.0f;

    /// <summary>
    /// How far past the corridor and the ceiling the region fades to nothing, as a multiple of each; enough that the
    /// worst slope at those edges is below what a highlight can pick out.
    /// </summary>
    private const float FoldFeather = 1.8f;

    /// <summary>
    /// How far forward of the body's mid-plane the height field reaches full strength, as a fraction of
    /// the crotch's half-width. Its axis is +Z, so at z=0 that axis lies IN the surface and moving a node
    /// along it slides the skin sideways instead of raising it. Fading in over this leaves the mid-plane
    /// to the relax, which does not care which way a surface points.
    /// </summary>
    private const float FoldFrontFade = 0.25f;

    /// <summary>
    /// How far below the crotch the region reaches at full strength, as a fraction of the crotch's half-width.
    /// Load-bearing only for the relax, which has no thigh veto. Small: below the crotch there is no fold, only the
    /// fillet where the thighs part, and smoothing a concave fillet pushes it out and bows the thigh.
    /// </summary>
    private const float FoldDrop = 0.1f;

    /// <summary>How far past <see cref="FoldDrop"/> the region fades to nothing, as a fraction of the crotch's
    /// half-width; only enough room to land softly.</summary>
    private const float FoldDropFeather = 0.3f;

    /// <summary>
    /// How far behind the mid-plane the finishing relax reaches, as a fraction of the crotch's half-width: far enough
    /// to take in the surface under the crotch, which faces down and which the span cannot touch, and to clear the
    /// rough part of the underside; not so far as to meet the seat, which is the cleft's business.
    /// </summary>
    private const float FoldBack = 1.0f;

    /// <summary>
    /// How square to the span's axis a surface has to be before the span acts on it at full strength — the
    /// forward component of its normal, faded in from zero. Half is about sixty degrees off axis, which
    /// keeps the mons and the front of the fold and drops the surface where it turns under.
    /// </summary>
    private const float FoldFacing = 0.5f;

    /// <summary>
    /// Back off the displacement wherever it would turn a triangle inside out or collapse it flat, until none does:
    /// the guarantee that this pass cannot punch a hole through the character. A halving sweep, valid because the
    /// test is monotone: scaling a displacement toward zero moves its triangles back toward their start. Deliberately
    /// the last thing that touches the displacement; every bound above is tuned for how the result looks.
    /// </summary>
    /// <param name="allowCollapse">Only a triangle that has turned over AND kept enough area to be seen counts
    /// as folded; one flattened to a sliver is left alone. For a caller that flattens surfaces on purpose.</param>
    internal static void UnfoldTriangles(Vec3[] pos, Vec3[] delta, int[] tris, Action<string>? log,
                                         bool allowCollapse = false)
    {
        int vc = pos.Length;
        var keep = new float[vc];
        Array.Fill(keep, 1f);

        Vec3 At(int i) => new(pos[i].X + delta[i].X * keep[i],
                              pos[i].Y + delta[i].Y * keep[i],
                              pos[i].Z + delta[i].Z * keep[i]);

        int touched = 0;
        for (int pass = 0; pass < UnfoldPasses; pass++)
        {
            int bad = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a >= vc || b >= vc || c >= vc) continue;
                if (keep[a] == 0f && keep[b] == 0f && keep[c] == 0f) continue;

                var n0 = TriNormal(pos[a], pos[b], pos[c]);
                float area0 = Len(n0);
                if (area0 <= 1e-12f) continue;      // already degenerate; not this pass's doing

                var n1 = TriNormal(At(a), At(b), At(c));
                bool turned = n0.X * n1.X + n0.Y * n1.Y + n0.Z * n1.Z <= 0f;
                bool collapsed = Len(n1) < area0 * UnfoldMinArea;
                bool folded = allowCollapse ? turned && !collapsed : turned || collapsed;
                if (!folded) continue;

                keep[a] *= 0.5f; keep[b] *= 0.5f; keep[c] *= 0.5f;
                bad++;
            }
            if (bad == 0) break;
            touched = Math.Max(touched, bad);
        }

        if (touched == 0) return;
        int held = 0;
        for (int i = 0; i < vc; i++)
        {
            if (keep[i] >= 1f) continue;
            delta[i] = new Vec3(delta[i].X * keep[i], delta[i].Y * keep[i], delta[i].Z * keep[i]);
            held++;
        }
        log?.Invoke($"bust bridge: held {held} vertex(es) back to keep {touched} triangle(s) from folding");
    }

    /// <summary>Rounds of halving <see cref="UnfoldTriangles"/> gets. Each one quarters the worst case, so
    /// eight is a factor of 256 — past any displacement this file can produce.</summary>
    private const int UnfoldPasses = 8;

    /// <summary>How much of its original area a triangle may lose before it counts as collapsed. A sliver
    /// this thin is already invisible; the point is that it must not go through zero and come out the
    /// other side.</summary>
    private const float UnfoldMinArea = 0.02f;

    /// <summary>
    /// <see cref="BustMaxSlope"/>'s sweep for a displacement that is a vector rather than a height: pull each node's
    /// displacement toward its neighbours' until no edge carries more difference than its length allows. Only ever
    /// toward zero: clamping into the neighbour's window drags an untouched node along and unpins the boundary.
    /// </summary>
    internal static void LimitSlopeVector(Vec3[] v, Vec3[] pos, List<int>[] adj, int count, float maxSlope)
    {
        for (int pass = 0; pass < BustSlopePasses; pass++)
        {
            float worst = 0f;
            for (int n = 0; n < count; n++)
            {
                float here = Len(v[n]);
                if (here <= 0f) continue;
                foreach (int k in adj[n])
                {
                    float dx = pos[k].X - pos[n].X, dy = pos[k].Y - pos[n].Y, dz = pos[k].Z - pos[n].Z;
                    float room = maxSlope * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    var diff = new Vec3(v[n].X - v[k].X, v[n].Y - v[k].Y, v[n].Z - v[k].Z);
                    float gap = Len(diff);
                    if (gap <= room || gap <= 1e-12f) continue;

                    float back = (gap - room) / gap;
                    var cand = new Vec3(v[n].X - diff.X * back,
                                        v[n].Y - diff.Y * back,
                                        v[n].Z - diff.Z * back);
                    float after = Len(cand);
                    if (after >= here) continue;          // never grow a displacement to satisfy a slope
                    worst = MathF.Max(worst, gap - room);
                    v[n] = cand;
                    here = after;
                }
            }
            if (worst <= BustBridgeEpsilon) break;
        }
    }
}
