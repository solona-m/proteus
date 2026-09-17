using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    private sealed class BridgeSolution
    {
        private readonly Vec3[] pos;
        private readonly Vec3[] nrm;
        private readonly int[] tris;
        private readonly float[] bust;
        private readonly float strength;
        private readonly Action<string>? log;
        private readonly bool[]? covered;
        private readonly float smoothStrength;
        private readonly bool fillGap;
        private readonly Vec3? outward;
        private readonly bool envelope;
        private readonly float[]? ramp;
        private readonly float[]? relaxSeed;
        private readonly bool pinBoundary;
        private readonly float minDepthShare;
        private readonly bool[]? joinPin;
        private readonly float rampFull;
        private readonly float maxSlope;
        private readonly float openSlope;
        private int vc;
        private int[] nodeOf = null!;
        private int nodeCount;
        private bool[]? rimNode;
        private bool[]? joinNode;
        private Vec3[] start = null!;
        private Vec3[] nNorm = null!;
        private int[] members = null!;
        private bool[] seed = null!;
        private bool[] cut = null!;
        private bool[] onBust = null!;
        private List<int>[] adj = null!;
        private float[] nW = null!;
        private float[] relaxW = null!;
        private int region;
        private Vec3 lateral;
        private Vec3 ax;
        private float[] h0 = null!;
        private float[] lat = null!;
        private float[] ver = null!;
        private Vec3[] skin = null!;
        private BodyWinding? skinBody;
        private Vec3[]? sling;
        private float[] h = null!;
        private float[] scale = null!;
        private float wantedMax;
        private float rampedMax;
        private bool[]? nearHem;
        private List<int>[]? spanBands;
        private Vec3[]? nipple;
        private Vec3[] delta = null!;
        private int moved;
        private float maxMove;
        private BustBridgePlan? result;

        public BridgeSolution(Vec3[] pos, Vec3[] nrm, int[] tris, float[] bust, float strength, Action<string>? log, bool[]? covered, float smoothStrength, bool fillGap, Vec3? outward, bool envelope, float[]? ramp, float[]? relaxSeed, bool pinBoundary, float minDepthShare, bool[]? joinPin, float rampFull, float maxSlope, float openSlope)
        {
            this.pos = pos;
            this.nrm = nrm;
            this.tris = tris;
            this.bust = bust;
            this.strength = strength;
            this.log = log;
            this.covered = covered;
            this.smoothStrength = smoothStrength;
            this.fillGap = fillGap;
            this.outward = outward;
            this.envelope = envelope;
            this.ramp = ramp;
            this.relaxSeed = relaxSeed;
            this.pinBoundary = pinBoundary;
            this.minDepthShare = minDepthShare;
            this.joinPin = joinPin;
            this.rampFull = rampFull;
            this.maxSlope = maxSlope;
            this.openSlope = openSlope;
        }

        public BustBridgePlan? Run()
        {
            if (!PinBoundaries()) return result;
            if (!SeedRegion()) return result;
            if (!WeightRegion()) return result;
            if (!FitFrame()) return result;
            if (!ScaleToChord()) return result;
            ApplySlopeLimit();
            DistributeLift();
            SmoothNipple();
            if (!FinishingRelax()) return result;
            return BuildPlan();
        }

        private bool PinBoundaries()
        {
            vc = pos.Length;
            if (vc == 0 || (strength <= 0f && smoothStrength <= 0f) || bust.Length < vc) { result = null; return false; }

            nodeOf = WeldByPosition(pos, out nodeCount);

            // THE RIM OF A HOLE NEVER MOVES. An edge used by exactly one triangle is a boundary with nothing to weld to: the
            // next part (torso and legs share a waist ring, solved separately) or an authored socket. Fed in through `cut`,
            // not zeroed afterwards, so the ramp runs down to the rim instead of leaving full-weight nodes beside zeroed ones.
            // The gap fill between two lobes ignores exclusions (see BustRegionWeights); that gap is the cleavage, which no
            // body boundary crosses.
            rimNode = null;
            if (pinBoundary)
            {
                var use = new Dictionary<(int, int), int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                    int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
                    Count(a, b); Count(b, c); Count(c, a);
                }
                foreach (var (e, n) in use)
                {
                    if (n != 1) continue;
                    rimNode ??= new bool[nodeCount];
                    rimNode[e.Item1] = true;
                    rimNode[e.Item2] = true;
                }

                void Count(int a, int b)
                {
                    if (a == b) return;
                    var k = a < b ? (a, b) : (b, a);
                    use[k] = use.TryGetValue(k, out int c) ? c + 1 : 1;
                }
            }

            // A ring shared with ANOTHER PART is pinned the same way — see joinPin. Kept apart as well, because
            // the span needs its own skirt from it below, not just the relax.
            joinNode = null;
            if (joinPin != null)
                for (int i = 0; i < vc && i < joinPin.Length; i++)
                {
                    if (!joinPin[i]) continue;
                    (joinNode ??= new bool[nodeCount])[nodeOf[i]] = true;
                    (rimNode ??= new bool[nodeCount])[nodeOf[i]] = true;
                }

            start = new Vec3[nodeCount];
            nNorm = new Vec3[nodeCount];
            members = new int[nodeCount];
            return true;
        }

        private bool SeedRegion()
        {
            // The SEED — where the bust bones say a breast is, on cloth this layer actually paints. It only
            // has to find the two lobes; BustRegionWeights turns it into the region that may move.
            seed = new bool[nodeCount];
            // Uncovered cloth pins the region at the garment's own edge, so a node is seeded only if EVERY
            // welded copy of it is painted. Any copy being cut away means the boundary runs through here.
            cut = new bool[nodeCount];
            // The bust WITHOUT the coverage gate. Where a breast is, is a fact about the body; what a garment
            // paints is a fact about the garment, and the nipple smooth needs the first to locate itself.
            onBust = new bool[nodeCount];
            for (int i = 0; i < vc; i++)
            {
                int n = nodeOf[i];
                start[n] = new Vec3(start[n].X + pos[i].X, start[n].Y + pos[i].Y, start[n].Z + pos[i].Z);
                nNorm[n] = new Vec3(nNorm[n].X + nrm[i].X, nNorm[n].Y + nrm[i].Y, nNorm[n].Z + nrm[i].Z);
                if (bust[i] > 0f) { seed[n] = true; onBust[n] = true; }
                if (covered != null && i < covered.Length && !covered[i]) cut[n] = true;
                members[n]++;
            }
            int seeded = 0, pinned = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                float inv = 1f / members[n];
                start[n] = new Vec3(start[n].X * inv, start[n].Y * inv, start[n].Z * inv);
                nNorm[n] = Normalize(nNorm[n]) ?? default;
                // Counted only where it BITES — a rim node the region never reached is not a pin, and
                // reporting every boundary in the mesh would bury the ones that mattered.
                if (rimNode != null && rimNode[n]) { if (seed[n]) pinned++; cut[n] = true; }
                if (cut[n]) seed[n] = false;
                if (seed[n]) seeded++;
            }
            if (pinned > 0)
                log?.Invoke($"bust bridge: {pinned} seeded node(s) pinned for sitting on the rim of a hole");
            if (seeded < MinBustBridgeNodes)
            {
                if (seeded > 0)
                    log?.Invoke($"bust bridge: SKIPPED, {seeded} bust node(s) is under "
                              + $"MinBustBridgeNodes ({MinBustBridgeNodes})");
                { result = null; return false; }
            }

            // Edge adjacency over the welded nodes, deduped — a shared edge would otherwise pull twice and
            // bias the plane fit toward whichever neighbour happens to be used by more triangles.
            adj = new List<int>[nodeCount];
            var seen = new HashSet<long>();
            void Link(int a, int b)
            {
                if (a == b) return;
                long key = a < b ? (long)a * nodeCount + b : (long)b * nodeCount + a;
                if (!seen.Add(key)) return;
                (adj[a] ??= new List<int>()).Add(b);
                (adj[b] ??= new List<int>()).Add(a);
            }
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;   // never fault on a bad index
                int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
                Link(a, b); Link(b, c); Link(c, a);
            }
            for (int n = 0; n < nodeCount; n++) adj[n] ??= new List<int>();

            // THE LOWER POLE, where the bones stop short of it. A body that hands the breast's underside to the spine seeds
            // only the top of it, so the underside is found from its shape: grown out of each lobe across skin that faces down
            // and forward, which stops at the crease on its own. Capped at half the lobes' height below them for a torso
            // with no crease.
            if (fillGap && strength > 0f)
            {
                // Heights from percentiles, so a stray breast-weighted vertex cannot stretch the cap. Growth stays below the
                // breasts' middle: the armpit and the underside of the arm face down and forward too.
                var seedY = new List<float>();
                for (int n = 0; n < nodeCount; n++) if (seed[n]) seedY.Add(start[n].Y);
                seedY.Sort();
                float seedLo = seedY[seedY.Count / 50], seedHi = seedY[seedY.Count - 1 - seedY.Count / 50];
                float midY = seedY[seedY.Count / 2];
                float floorY = seedLo - (seedHi - seedLo) * BustLowerPoleReach;
                var grow = new Queue<int>();
                for (int n = 0; n < nodeCount; n++) if (seed[n]) grow.Enqueue(n);
                int grown = 0;
                while (grow.Count > 0)
                {
                    int q = grow.Dequeue();
                    foreach (int k in adj[q])
                    {
                        if (seed[k] || cut[k] || start[k].Y < floorY || start[k].Y > midY
                            || nNorm[k].Y > -BustLowerPoleFacing) continue;
                        // Forward as well as down, or it follows the flank round to the back; and never across the
                        // midline, which would merge the two lobes the gap fill needs to find apart.
                        if (nNorm[k].Z < BustLowerPoleFacing || start[k].X * start[q].X <= 0f) continue;
                        seed[k] = true;
                        grown++;
                        grow.Enqueue(k);
                    }
                }
                if (grown > 0)
                    log?.Invoke($"bust bridge: {grown} node(s) of the breasts' underside grown into the region below the bones' reach");
            }
            return true;
        }

        private bool WeightRegion()
        {
            // Cloth the layer does not paint is excluded from the region up front, not subtracted afterwards: subtracting
            // after the ramp is built leaves full-weight vertices beside zeroed ones, and the garment's edge tears into spikes.
            nW = BustRegionWeights(seed, cut, adj, nodeCount, log, fillGap);

            // The caller's own feather, welded to nodes the same way the geometry was, averaged over the welded copies: a
            // node is one place on the surface.
            relaxW = new float[nodeCount];
            if (ramp != null || relaxSeed != null)
            {
                var rampN = new float[nodeCount];
                for (int i = 0; i < vc && i < pos.Length; i++)
                {
                    int n = nodeOf[i];
                    if (ramp != null && i < ramp.Length) rampN[n] += ramp[i];
                    if (relaxSeed != null && i < relaxSeed.Length) relaxW[n] += relaxSeed[i];
                }
                for (int n = 0; n < nodeCount; n++)
                {
                    float inv = 1f / members[n];
                    relaxW[n] *= inv;
                    rampN[n] *= inv;
                }

                // Smoothed over the mesh before use: a caller's ramp can be ragged (bone weights are quantized per vertex), and a
                // jagged edge in the displacement draws a broken line. Jacobi over the welded graph, reading neighbours regardless
                // of their own weight so the outside pulls the boundary down to nothing.
                if (ramp != null && RampSmoothPasses > 0)
                {
                    var next = new float[nodeCount];
                    for (int pass = 0; pass < RampSmoothPasses; pass++)
                    {
                        for (int n = 0; n < nodeCount; n++)
                        {
                            if (adj[n] is not { Count: > 0 } near) { next[n] = rampN[n]; continue; }
                            float s = 0f;
                            foreach (int k in near) s += rampN[k];
                            next[n] = rampN[n] + (s / near.Count - rampN[n]) * 0.5f;
                        }
                        (rampN, next) = (next, rampN);
                    }
                }

                // Saturated at rampFull: the ramp fades the region's edges, and multiplied in raw it also scales the interior
                // by the bone weight.
                if (ramp != null)
                    for (int n = 0; n < nodeCount; n++) nW[n] *= MathF.Min(1f, rampN[n] / rampFull);
            }

            // The rim again, on the relax channel: the relax runs on every node with relaxW > 0, not on the region, so a
            // pinned seed alone does nothing. Zeroed here rather than skipped at the loop, so the rim is read by its
            // neighbours and never written. After the ramp smoothing, since a pin a later Jacobi pass could bleed into is not
            // a pin. Faded over several rings with a smoothstepped skirt, not zeroed at the rim alone, or the whole
            // displacement appears across a single edge.
            if (rimNode != null)
            {
                var ring = new int[nodeCount];
                Array.Fill(ring, -1);
                var q0 = new Queue<int>();
                for (int n = 0; n < nodeCount; n++) if (rimNode[n]) { ring[n] = 0; q0.Enqueue(n); }
                while (q0.Count > 0)
                {
                    int q = q0.Dequeue();
                    if (ring[q] >= RimPinFade || adj[q] == null) continue;
                    foreach (int k in adj[q])
                        if (ring[k] < 0) { ring[k] = ring[q] + 1; q0.Enqueue(k); }
                }
                for (int n = 0; n < nodeCount; n++)
                {
                    if (ring[n] < 0) continue;                       // beyond the skirt, untouched
                    relaxW[n] *= Smoothstep(ring[n] / (float)RimPinFade);
                }
            }

            // The span's own skirt from a part join: excluding the ring from the seed stops the region there, but the span
            // must come to rest on the ring over a distance a body can hide, since the part on the other side does not move.
            if (joinNode != null)
            {
                var ring = new int[nodeCount];
                Array.Fill(ring, -1);
                var q0 = new Queue<int>();
                for (int n = 0; n < nodeCount; n++) if (joinNode[n]) { ring[n] = 0; q0.Enqueue(n); }
                while (q0.Count > 0)
                {
                    int q = q0.Dequeue();
                    if (ring[q] >= JoinPinFade || adj[q] == null) continue;
                    foreach (int k in adj[q])
                        if (ring[k] < 0) { ring[k] = ring[q] + 1; q0.Enqueue(k); }
                }
                int faded = 0;
                for (int n = 0; n < nodeCount; n++)
                {
                    if (ring[n] < 0 || nW[n] <= 0f) continue;
                    nW[n] *= Smoothstep(ring[n] / (float)JoinPinFade);
                    faded++;
                }
                if (faded > 0)
                    log?.Invoke($"bust bridge: {faded} region node(s) within {JoinPinFade} rings of a part join faded toward it");
            }

            region = 0;
            for (int n = 0; n < nodeCount; n++) if (nW[n] > 0f) region++;
            if (region < MinBustBridgeNodes)
            {
                log?.Invoke($"bust bridge: SKIPPED, {region} region node(s) survive the coverage gate");
                { result = null; return false; }
            }
            return true;
        }

        private bool FitFrame()
        {
            // A rough outward direction: the region's weighted mean normal. Only used to find the other two axes and settle
            // the final one's sign; it is not the direction anything moves in. A caller may supply it, because inside a
            // crease the walls face each other and the mean normal is not outward at all.
            var seedOut = outward ?? Normalize(WeightedMean(nNorm, nW, nodeCount));
            if (seedOut is not { } outSeed)
            {
                log?.Invoke("bust bridge: SKIPPED, the region's normals cancel out — no outward axis");
                { result = null; return false; }
            }

            // The direction the span runs in — lobe to lobe. Everything is spanned ACROSS this and along
            // nothing else, because a cleft is a saddle and an isotropic relax settles on it unchanged.
            var across = PrincipalAcross(start, nW, nodeCount, outSeed);
            if (across is not { } lateralValue)
            {
                log?.Invoke("bust bridge: SKIPPED, the region has no principal direction across the chest");
                { result = null; return false; }
            }
            lateral = lateralValue;

            var everywhere = new float[nodeCount];
            Array.Fill(everywhere, 1f);

            // Up the body: the widest spread in the plane across the span direction, measured over the whole mesh rather
            // than the region. A bust region is a curved band whose principal direction runs diagonally; the torso it is cut
            // from is taller than it is deep and its principal direction is the body's vertical.
            var upward = PrincipalAcross(start, everywhere, nodeCount, lateral);
            if (upward is not { } vertical)
            {
                log?.Invoke("bust bridge: SKIPPED, the region has no vertical extent");
                { result = null; return false; }
            }

            // The two can come back swapped: the span direction is the region's principal direction, which is only
            // lobe-to-lobe while the region is wider than it is tall. The buttocks are not, so PCA returns the body's
            // vertical. Detected against model +Y, the one assumption here (the same fact <see cref="Facing"/> rests on for
            // +Z). Comparing the region's extents is tautological — PCA returns the longest direction by construction — and
            // the whole mesh's principal direction is not a vertical on a fixture wider than it is tall.
            if (MathF.Abs(lateral.Y) > 0.7f)
            {
                // The honest span direction is then the remaining axis: perpendicular to up and to out.
                var side = Normalize(new Vec3(1f * outSeed.Z - 0f * outSeed.Y,
                                              0f * outSeed.X - 0f * outSeed.Z,
                                              0f * outSeed.Y - 1f * outSeed.X));
                if (side is { } acrossBody)
                {
                    log?.Invoke("bust bridge: the region's principal direction is the body's own vertical, so "
                              + "it is taller than it is wide — spanning across the body instead");
                    lateral = acrossBody;
                    var reUp = PrincipalAcross(start, everywhere, nodeCount, lateral);
                    if (reUp is not { } v2)
                    {
                        log?.Invoke("bust bridge: SKIPPED, no vertical remains once the axes are swapped");
                        { result = null; return false; }
                    }
                    vertical = v2;
                }
            }

            // THE DIRECTION CLOTH MOVES: perpendicular to both, straight out from the chest. Not the mean normal, which tilts
            // wherever a garment covers the breast asymmetrically and then lifts vertices up the body as well as out, into the
            // breast. Perpendicular to the body's vertical, a chest front is single-valued, so moving out along it cannot
            // re-enter the body.
            ax = Normalize(new Vec3(lateral.Y * vertical.Z - lateral.Z * vertical.Y,
                                        lateral.Z * vertical.X - lateral.X * vertical.Z,
                                        lateral.X * vertical.Y - lateral.Y * vertical.X)) ?? outSeed;
            if (ax.X * outSeed.X + ax.Y * outSeed.Y + ax.Z * outSeed.Z < 0f)
                ax = new Vec3(-ax.X, -ax.Y, -ax.Z);   // point it out of the body, not into it

            h0 = new float[nodeCount];
            lat = new float[nodeCount];
            ver = new float[nodeCount];
            for (int n = 0; n < nodeCount; n++)
            {
                var p = start[n];
                h0[n] = p.X * ax.X + p.Y * ax.Y + p.Z * ax.Z;
                lat[n] = p.X * lateral.X + p.Y * lateral.Y + p.Z * lateral.Z;
                ver[n] = p.X * vertical.X + p.Y * vertical.Y + p.Z * vertical.Z;
            }

            // The skin as it is, before the sling below reshapes the surface the span is solved on — what every
            // inside-the-body test measures against.
            skin = (Vec3[])start.Clone();
            skinBody = null;

            // The under-bust sling, before the span (see UnderBustSling): folded into the surface the chord works on, so the
            // nodes tucked under the breast are already out on the sling when the span looks for its lowest points.
            sling = null;
            if (fillGap && strength > 0f && openSlope > maxSlope)
            {
                sling = UnderBustSling(start, nNorm, h0, lat, ver, seed, cut, adj, nodeCount, ax, lateral, vertical, SkinBody(), log,
                                       out var slingWeight, out var slingBetween);
                if (sling != null)
                    for (int n = 0; n < nodeCount; n++)
                    {
                        // The slings and the skin between them join the region the span across works on.
                        nW[n] = MathF.Max(nW[n], MathF.Max(slingWeight[n], slingBetween[n]));
                        var d = sling[n];
                        if (d.X == 0f && d.Y == 0f && d.Z == 0f) continue;
                        start[n] = new Vec3(start[n].X + d.X, start[n].Y + d.Y, start[n].Z + d.Z);
                        h0[n] += d.X * ax.X + d.Y * ax.Y + d.Z * ax.Z;
                        ver[n] += d.X * vertical.X + d.Y * vertical.Y + d.Z * vertical.Z;
                    }
            }

            h = strength <= 0f ? h0
                  : envelope ? EnvelopeTarget(h0, lat, ver, nW, nodeCount, adj, start, log)
                             : ChordTarget(h0, lat, ver, nW, nodeCount, adj, start, log);
            return true;
        }

        private bool ScaleToChord()
        {
            // How far of the way to the chord each node goes: the region ramp fades the effect at the region's edge and the
            // strength is the user's setting. Both scale the finished displacement, so the span itself stays straight.
            scale = new float[nodeCount];
            for (int n = 0; n < nodeCount; n++) scale[n] = (h[n] - h0[n]) * nW[n] * strength;

            // What the construction asked for, before the ramp and the slope limit trim it, so the report can say which of
            // the three decided the result.
            wantedMax = 0f;
            rampedMax = 0f;
            for (int n = 0; n < nodeCount; n++)
            {
                wantedMax = MathF.Max(wantedMax, h[n] - h0[n]);
                rampedMax = MathF.Max(rampedMax, MathF.Abs(scale[n]));
            }

            // A VALLEY, OR A RIDGE? See CleftMinDepthShare. Asked before anything moves, so a rejected solve
            // leaves the mesh byte-identical rather than merely scaled down.
            if (minDepthShare > 0f && strength > 0f
                && ApexGap(start, h0, nW, nodeCount, ax, lateral) is var gap and > 0f
                && wantedMax < gap * minDepthShare)
            {
                log?.Invoke($"bust bridge: SKIPPED, the chord asks {wantedMax * 1000:0.#}mm across apexes "
                          + $"{gap * 1000:0.#}mm apart (share {wantedMax / gap:0.##}, needs {minDepthShare:0.##}) — "
                          + "a shallow ridge, not a cleft between two lobes");
                { result = null; return false; }
            }
            return true;
        }

        private void ApplySlopeLimit()
        {
            // SLOPE LIMIT — the guarantee that the shell cannot tear, whatever shape the region came out. Each node is
            // pulled toward its neighbour's displacement until the difference is no more than the edge between them can
            // absorb, repeated until it settles. Two-sided, because the span only raises but the nipple smooth may lower.
            // It only ever moves a node toward zero: clamping into the neighbour's window would pull an untouched node up to
            // meet a displaced one and unpin the boundary. Near a hem the tuned limit holds; away from one the span may drop
            // more steeply, since BustMaxSlope was measured against folds along a cut edge and applied everywhere it would
            // also shape the span's own open bottom.
            nearHem = null;
            if (openSlope > maxSlope)
            {
                nearHem = new bool[nodeCount];
                var ringQ = new Queue<(int, int)>();
                for (int n = 0; n < nodeCount; n++) if (cut[n]) { nearHem[n] = true; ringQ.Enqueue((n, 0)); }
                while (ringQ.Count > 0)
                {
                    var (q, d) = ringQ.Dequeue();
                    if (d >= BustHemRings) continue;
                    foreach (int k in adj[q])
                        if (!nearHem[k]) { nearHem[k] = true; ringQ.Enqueue((k, d + 1)); }
                }
            }

            // NO RIDGE ACROSS THE SPAN. A bridge's cross-section runs flat or dips between its two sides; once the inside
            // check holds back the nodes under each breast's inner edge, the slope limit would let the midline climb proud of
            // them. Per band across the chest, over every node the chord asked to move (held-back ones included, they are
            // the dips): walking in from either side the surface may only fall, then only rise. A node is capped at the
            // higher of the lowest point between it and each end. Only ever lowers, so every guarantee above still holds.
            spanBands = null;
        }

        private void DistributeLift()
        {
            // Where the lift goes, as shares over every node the chord asked to move a real distance; the maxima in the
            // summary say only which stage capped the single deepest node.
            double askSum = 0, rampSum = 0, slopeSum = 0;
            int askNodes = 0;
            var asked = new bool[nodeCount];
            for (int n = 0; n < nodeCount; n++)
            {
                if (h[n] - h0[n] < BridgeShareFloor) continue;
                asked[n] = true; askNodes++;
                askSum += (h[n] - h0[n]) * strength;
                rampSum += scale[n];
            }
            var traceRamp = TraceSpanNode != null ? (float[])scale.Clone() : null;
            LimitSlope(scale);
            for (int n = 0; n < nodeCount; n++) if (asked[n]) slopeSum += scale[n];
            var traceSlope = TraceSpanNode != null ? (float[])scale.Clone() : null;

            // Keep every lift outside the body, alternating with the slope limit until neither changes anything: a cut
            // leaves a node low and the limit lowers its neighbours, but lowering a lift moves its end back along a path that
            // may re-enter a breast. Both only ever lower, so this settles; lifts already checked and unchanged are skipped.
            if (strength > 0f)
            {
                var body = SkinBody();
                var checkedAt = new float[nodeCount];
                Array.Fill(checkedAt, float.NaN);
                int rounds = 0, totalCut = 0;
                bool settled = false;
                while (rounds < BridgeInsideRounds)
                {
                    rounds++;
                    int cutNow = KeepLiftsOutside(scale, start, ax, body, checkedAt, nodeCount);
                    if (nearHem != null) cutNow += KeepEdgesOutside(scale, start, ax, body, adj, nodeCount, maxSlope);
                    if (nearHem != null) ShaveAcrossPeaks(scale);
                    totalCut += cutNow;
                    if (cutNow == 0) { settled = true; break; }
                    LimitSlope(scale);
                }
                // Out of rounds: the last limit may have pulled ends back in, so check once more without it. A node cut
                // here can stand below its neighbours by more than the slope limit allows, which is the lesser fault.
                if (!settled) totalCut += KeepLiftsOutside(scale, start, ax, body, checkedAt, nodeCount);
                if (totalCut > 0)
                    log?.Invoke($"bust bridge: {totalCut} lift cut(s) to keep the span outside the body, over {rounds} "
                              + $"round(s){(settled ? "" : " — did not settle")}");
            }
            if (TraceSpanNode is { } trace && traceRamp != null && traceSlope != null)
                foreach (int n in Enumerable.Range(0, nodeCount).Where(n => trace(start[n]))
                                            .OrderBy(n => MathF.Round(start[n].Y, 2)).ThenBy(n => start[n].X))
                    log?.Invoke($"TRACE ({start[n].X * 1000:0.0},{start[n].Y:0.000},{start[n].Z * 1000:0.0}) "
                              + $"n({nNorm[n].X:0.00},{nNorm[n].Y:0.00},{nNorm[n].Z:0.00}) nW {nW[n]:0.00} "
                              + $"asked {(h[n] - h0[n]) * 1000:0.0} ramp {traceRamp[n] * 1000:0.0} "
                              + $"slope {traceSlope[n] * 1000:0.0} final {scale[n] * 1000:0.0}");
            if (askNodes > 0 && askSum > 0)
            {
                double finalSum = 0;
                for (int n = 0; n < nodeCount; n++) if (asked[n]) finalSum += scale[n];
                log?.Invoke($"bust bridge: {askNodes} node(s) asked {askSum / askNodes * 1000:0.#}mm on average; the ramp "
                          + $"kept {rampSum / askSum:P0}, the slope limit {slopeSum / askSum:P0}, the inside check "
                          + $"{finalSum / askSum:P0}");
            }
        }

        private void SmoothNipple()
        {
            // The nipple relax, on the surface the span left behind. It is a 3-D displacement and cannot be folded into
            // `scale` (see NippleSmoothTarget). Slope-limited per component, which is safe because the sweep only moves a
            // value toward zero; a coverage edge cutting through the disc is exactly the ragged boundary the limit absorbs.
            nipple = null;
            if (smoothStrength > 0f)
            {
                var spanned = new Vec3[nodeCount];
                var spannedH = new float[nodeCount];
                for (int n = 0; n < nodeCount; n++)
                {
                    spanned[n] = new Vec3(start[n].X + ax.X * scale[n],
                                          start[n].Y + ax.Y * scale[n],
                                          start[n].Z + ax.Z * scale[n]);
                    spannedH[n] = h0[n] + scale[n];
                }
                // onBust, not seed: where the nipple is is a fact about the body, and the seed is gated on what this garment
                // covers (see NippleSmoothTarget).
                nipple = NippleSmoothTarget(spanned, spannedH, lat, ver, nW, onBust, nodeCount, adj, ax,
                                            smoothStrength, log, nNorm);
                if (nipple != null)
                {
                    var comp = new float[nodeCount];
                    for (int c = 0; c < 3; c++)
                    {
                        for (int n = 0; n < nodeCount; n++)
                            comp[n] = c == 0 ? nipple[n].X : c == 1 ? nipple[n].Y : nipple[n].Z;
                        LimitSlope(comp);
                        for (int n = 0; n < nodeCount; n++)
                            nipple[n] = c == 0 ? new Vec3(comp[n], nipple[n].Y, nipple[n].Z)
                                      : c == 1 ? new Vec3(nipple[n].X, comp[n], nipple[n].Z)
                                               : new Vec3(nipple[n].X, nipple[n].Y, comp[n]);
                    }
                }
            }
        }

        private bool FinishingRelax()
        {
            // ── THE FINISHING RELAX ─────────────────────────────────────────────────────────────────────
            //
            // A plain Laplacian over the region, on the surface everything above produced. The height field only moves a
            // node along one axis, so wherever the surface turns to face across that axis (under the crotch) the span has no
            // purchase and this is the only operator. Plain rather than Taubin: the negative step undoes the redistribution
            // (see NippleFinishPasses). Neighbours outside the relax region are read but never written, so they pin the
            // boundary, and relaxW fades the result on top.
            if (relaxSeed != null && strength > 0f && FoldRelaxPasses > 0)
            {
                var cur = new Vec3[nodeCount];
                for (int n = 0; n < nodeCount; n++)
                {
                    float d = scale[n];
                    cur[n] = new Vec3(start[n].X + ax.X * d, start[n].Y + ax.Y * d, start[n].Z + ax.Z * d);
                    if (nipple is { } q)
                        cur[n] = new Vec3(cur[n].X + q[n].X, cur[n].Y + q[n].Y, cur[n].Z + q[n].Z);
                }
                var basis = (Vec3[])cur.Clone();
                var next = (Vec3[])cur.Clone();

                var relaxNodes = new List<int>();
                for (int n = 0; n < nodeCount; n++) if (relaxW[n] > 0f && adj[n].Count > 0) relaxNodes.Add(n);

                for (int pass = 0; pass < FoldRelaxPasses; pass++)
                {
                    foreach (int n in relaxNodes)
                    {
                        var near = adj[n];
                        float sx = 0f, sy = 0f, sz = 0f;
                        foreach (int j in near) { sx += cur[j].X; sy += cur[j].Y; sz += cur[j].Z; }
                        float inv = 1f / near.Count;
                        next[n] = new Vec3(cur[n].X + (sx * inv - cur[n].X) * FoldRelaxLambda,
                                           cur[n].Y + (sy * inv - cur[n].Y) * FoldRelaxLambda,
                                           cur[n].Z + (sz * inv - cur[n].Z) * FoldRelaxLambda);
                    }
                    foreach (int n in relaxNodes) cur[n] = next[n];
                }

                var free = nipple ?? new Vec3[nodeCount];
                float mostRelax = 0f;
                foreach (int n in relaxNodes)
                {
                    float a = relaxW[n] * strength;
                    if (a <= 0f) continue;
                    var d = new Vec3((cur[n].X - basis[n].X) * a,
                                     (cur[n].Y - basis[n].Y) * a,
                                     (cur[n].Z - basis[n].Z) * a);
                    free[n] = new Vec3(free[n].X + d.X, free[n].Y + d.Y, free[n].Z + d.Z);
                    mostRelax = MathF.Max(mostRelax, Len(d));
                }

                // Limited as a vector, and tighter than the span's own limit. Per component is right for the span, where every
                // node moves along one axis and a slope limit can tilt a triangle but never fold it; this channel moves freely in
                // 3-D, where the same limit three times over lets neighbours pass through each other. A triangle inverts once one
                // corner's displacement exceeds its distance to the opposite edge.
                LimitSlopeVector(free, start, adj, nodeCount, FoldRelaxMaxSlope);
                nipple = free;

                log?.Invoke($"bust bridge: relax {FoldRelaxPasses} pass(es) over {relaxNodes.Count} node(s), "
                          + $"moving them by up to {mostRelax:0.#####}");
            }

            delta = new Vec3[vc];
            moved = 0;
            maxMove = 0f;
            for (int i = 0; i < vc; i++)
            {
                int n = nodeOf[i];
                float d = scale[n];
                var v = new Vec3(ax.X * d, ax.Y * d, ax.Z * d);
                if (nipple is { } np) v = new Vec3(v.X + np[n].X, v.Y + np[n].Y, v.Z + np[n].Z);
                if (sling != null) v = new Vec3(v.X + sling[n].X, v.Y + sling[n].Y, v.Z + sling[n].Z);
                float mag = Len(v);
                if (mag <= BustBridgeEpsilon) continue;
                delta[i] = v;
            }

            // THE MESH HAS TO STILL BE A MESH — but only the fold gets this. Run over every pass it wrecks the nipple:
            // flattening a point legitimately shrinks the triangles at the tip, they trip the area test, and the cascade
            // leaves a spiked mess. The tearing it exists for is at the crotch, where the span's axis lies along the surface.
            if (relaxSeed != null)
                UnfoldTriangles(pos, delta, tris, log);

            foreach (var v in delta) maxMove = MathF.Max(maxMove, Len(v));
            // Counted per NODE, not per vertex, so the number means "how much of the chest moved" rather than
            // how many UV-seam copies the mesh happens to carry.
            for (int n = 0; n < nodeCount; n++) if (NodeMove(n) > BustBridgeEpsilon) moved++;

            if (moved == 0)
            {
                log?.Invoke($"bust bridge: {region} region node(s), nothing moved — the cloth here is already "
                          + "the shape it was asked for");
                { result = null; return false; }
            }
            return true;
        }

        private BustBridgePlan? BuildPlan()
        {
            // Measured on the final heights, after the region ramp and the strength, because that is the surface the shell gets.
            var hFinal = new float[nodeCount];
            for (int n = 0; n < nodeCount; n++) hFinal[n] = h0[n] + scale[n];
            var chord = ChordReport(start, h0, hFinal, nW, nodeCount, ax, lateral);

            // The finishing relax reaches past the height field's region, so nodes it alone moved carry nW of zero and would
            // be written moved but shaded from their old positions. Admitted here, after the report has measured the span on
            // the weights it used, and by how far each node moved rather than by region: nW is the blend between the authored
            // normal and a recomputed one, and a shell is built as position + normal * BaseOffset, so rewriting the normal of
            // a node that barely moved lifts the fabric off the skin along a hem. Movement is measured against the node's own
            // edge length so it means the same on any mesh density.
            if (relaxSeed != null)
            {
                int full = 0, token = 0;
                for (int n = 0; n < nodeCount; n++)
                {
                    if (relaxW[n] <= 0f || adj[n].Count == 0) continue;
                    float edge = 0f;
                    foreach (int k in adj[n])
                    {
                        float dx = start[k].X - start[n].X, dy = start[k].Y - start[n].Y, dz = start[k].Z - start[n].Z;
                        edge += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    }
                    edge /= adj[n].Count;
                    if (edge <= 1e-9f) continue;
                    float need = Math.Clamp(NodeMove(n) / (edge * NormalReshadeSpan), 0f, 1f);
                    nW[n] = MathF.Max(nW[n], relaxW[n] * strength * need);
                    if (nW[n] >= 0.5f) full++; else if (nW[n] > 0f) token++;
                }
                log?.Invoke($"bust bridge: {full} node(s) reshaded from the new surface, {token} keeping most "
                          + "of the normal they arrived with");
            }

            // Nodes the bridge never moved report zero weight, so their normal bytes stay as they were and an untouched shell
            // stays byte-identical. The relax and the sling count as movement too; the sling moves nodes the region never reached.
            if (sling != null)
                for (int n = 0; n < nodeCount; n++)
                    if (Len(sling[n]) > BustBridgeEpsilon) nW[n] = 1f;

            for (int n = 0; n < nodeCount; n++)
                if (NodeMove(n) <= BustBridgeEpsilon) nW[n] = 0f;

            // The span's reshade follows the span's movement too: nW is the region ramp, full on nodes the chord barely
            // lifted, and a normal rebuilt there replaces the artist's without the surface having changed.
            if (relaxSeed == null)
                for (int n = 0; n < nodeCount; n++)
                {
                    if (nW[n] <= 0f || adj[n].Count == 0) continue;
                    float edge = 0f;
                    foreach (int k in adj[n]) edge += Dist(skin[k], skin[n]);   // the skin's own spacing, not the sling's
                    edge /= adj[n].Count;
                    if (edge > 1e-9f)
                        nW[n] = MathF.Min(nW[n], Smoothstep(Math.Clamp(NodeMove(n) / (edge * NormalReshadeSpan), 0f, 1f)));
                }

            log?.Invoke($"bust bridge: axis ({ax.X:0.###},{ax.Y:0.###},{ax.Z:0.###}), {region} region node(s), "
                      + $"{moved} moved, max {maxMove:0.#####} "
                      + $"(chord asked {wantedMax:0.#####}, ramp left {rampedMax:0.#####}, slope left {maxMove:0.#####})"
                      + chord);

            return new BustBridgePlan
            {
                Delta = delta, NodeOf = nodeOf, NodeWeight = nW, NodeNormal = nNorm, NodePinned = joinNode,
            };
        }

        private BodyWinding SkinBody() => skinBody ??= new BodyWinding(skin, tris, nodeOf);

        private void ShaveAcrossPeaks(float[] v)
        {
            if (spanBands == null)
            {
                float loV = float.MaxValue, hiV = float.MinValue, edgeSum = 0f;
                int edgeN = 0;
                for (int n = 0; n < nodeCount; n++)
                {
                    if (h[n] - h0[n] <= BridgeInsideMargin) continue;
                    loV = MathF.Min(loV, ver[n]); hiV = MathF.Max(hiV, ver[n]);
                    foreach (int k in adj[n]) { edgeSum += Dist(start[n], start[k]); edgeN++; }
                }
                float bandH = edgeN > 0 ? edgeSum / edgeN : 0f;
                int bandCount = bandH > 1e-6f && hiV > loV ? Math.Clamp((int)MathF.Ceiling((hiV - loV) / bandH), 1, 512) : 0;
                spanBands = new List<int>[bandCount];
                for (int b = 0; b < bandCount; b++) spanBands[b] = new List<int>();
                for (int n = 0; n < nodeCount && bandCount > 0; n++)
                    if (h[n] - h0[n] > BridgeInsideMargin)
                        spanBands[Math.Clamp((int)((ver[n] - loV) / bandH), 0, bandCount - 1)].Add(n);
                foreach (var band in spanBands) band.Sort((a, b) => lat[a].CompareTo(lat[b]));
            }
            foreach (var band in spanBands)
            {
                int m = band.Count;
                if (m < 3) continue;
                var fromLeft = new float[m];
                var fromRight = new float[m];
                for (int i = 0; i < m; i++)
                {
                    float hf = h0[band[i]] + v[band[i]];
                    fromLeft[i] = i == 0 ? hf : MathF.Min(fromLeft[i - 1], hf);
                }
                for (int i = m - 1; i >= 0; i--)
                {
                    float hf = h0[band[i]] + v[band[i]];
                    fromRight[i] = i == m - 1 ? hf : MathF.Min(fromRight[i + 1], hf);
                }
                for (int i = 1; i < m - 1; i++)
                {
                    int n = band[i];
                    float cap = MathF.Max(fromLeft[i - 1], fromRight[i + 1]) + BridgeRidgeSlack;
                    if (h0[n] + v[n] > cap) v[n] = MathF.Max(0f, cap - h0[n]);
                }
            }
        }

        private void LimitSlope(float[] v)
        {
            for (int pass = 0; pass < BustSlopePasses; pass++)
            {
                float worst = 0f;
                for (int n = 0; n < nodeCount; n++)
                {
                    if (v[n] == 0f) continue;
                    foreach (int k in adj[n])
                    {
                        float dx = start[k].X - start[n].X, dy = start[k].Y - start[n].Y, dz = start[k].Z - start[n].Z;
                        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        float slope = maxSlope;
                        if (nearHem is not null && !nearHem[n] && !nearHem[k] && len > 1e-9f)
                        {
                            // Steep only along the body, never across it: the open drop runs down from the cleavage to the crease, while
                            // across the chest a steep limit lets the midline stand proud of the held-back nodes beside it.
                            float across = MathF.Abs(dx * lateral.X + dy * lateral.Y + dz * lateral.Z) / len;
                            slope = openSlope + (maxSlope - openSlope) * Smoothstep(Math.Clamp(across / 0.7f, 0f, 1f));
                        }
                        float room = slope * len;
                        // A node that starts deeper than its neighbour may rise to meet it, however far: what tears a shell is a lift
                        // that differs between two nodes at the same height, and closing the depth flattens the surface. Only up to
                        // level, never past it.
                        float gap = h0[k] - h0[n];
                        if (v[n] > 0f)
                        {
                            float cap = MathF.Max(0f, v[k] + MathF.Max(room, gap));
                            if (cap >= v[n]) continue;
                            worst = MathF.Max(worst, v[n] - cap);
                            v[n] = cap;
                        }
                        else
                        {
                            float flo = MathF.Min(0f, v[k] - MathF.Max(room, -gap));
                            if (flo <= v[n]) continue;
                            worst = MathF.Max(worst, flo - v[n]);
                            v[n] = flo;
                        }
                    }
                }
                if (worst <= BustBridgeEpsilon) break;
            }
        }

        // Magnitude, not sign: the span only raises but the nipple relax may lower. The two compose by addition — a
        // scalar along one axis plus a free 3-D move.
        private float NodeMove(int n)
        {
            float s = MathF.Abs(scale[n]);
            if (sling != null) s += Len(sling[n]);
            return nipple is null ? s : s + Len(nipple[n]);
        }
    }
}
