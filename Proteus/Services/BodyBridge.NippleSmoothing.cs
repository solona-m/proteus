using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    private sealed class NippleSmoothing
    {
        private readonly Vec3[] pos;
        private readonly float[] h;
        private readonly float[] lat;
        private readonly float[] ver;
        private readonly float[] w;
        private readonly bool[] onBust;
        private readonly int count;
        private readonly List<int>[] adj;
        private readonly Vec3 ax;
        private readonly float strength;
        private readonly Action<string>? log;
        private readonly Vec3[]? nrm;
        private List<int> measure = null!;
        private List<int> region = null!;
        private float midLat;
        private float ringIn;
        private float ringOut;
        private float radius;
        private int nipL;
        private int nipR;
        private float promL;
        private float promR;
        private float[] amount = null!;
        private int touched;
        private List<int> patch = null!;
        private Vec3[] cur = null!;
        private Vec3[] delta = null!;
        private int lifted;
        private int shortNodes;
        private float mostLift;
        private float shortfall;
        private float[] finishW = null!;
        private Vec3[]? result;

        public NippleSmoothing(Vec3[] pos, float[] h, float[] lat, float[] ver, float[] w, bool[] onBust, int count, List<int>[] adj, Vec3 ax, float strength, Action<string>? log, Vec3[]? nrm)
        {
            this.pos = pos;
            this.h = h;
            this.lat = lat;
            this.ver = ver;
            this.w = w;
            this.onBust = onBust;
            this.count = count;
            this.adj = adj;
            this.ax = ax;
            this.strength = strength;
            this.log = log;
            this.nrm = nrm;
        }

        public Vec3[]? Run()
        {
            if (!MeasureBust()) return result;
            if (!FindNipples()) return result;
            if (!RelaxPatch()) return result;
            ApplyStrength();
            FloorDomes();
            return LightRelax();
        }

        private bool MeasureBust()
        {
            measure = new List<int>();
            region = new List<int>();
            float loLat = float.MaxValue;
            float hiLat = float.MinValue;
            midLat = 0f;
            for (int n = 0; n < count; n++)
            {
                if (w[n] > 0f) region.Add(n);
                if (!onBust[n]) continue;
                measure.Add(n);
                loLat = MathF.Min(loLat, lat[n]);
                hiLat = MathF.Max(hiLat, lat[n]);
                midLat += lat[n];
            }
            if (measure.Count < MinBustBridgeNodes || region.Count == 0) { result = null; return false; }
            midLat /= measure.Count;

            // Distances are fractions of the bust's own width. The ring is a nipple's radius; the disc is the brush.
            float extent = hiLat - loLat;
            if (extent <= 1e-6f) { result = null; return false; }
            ringIn = extent * NippleRingInner;
            ringOut = extent * NippleRingOuter;
            radius = extent * NippleDiscRadius;
            return true;
        }

        private bool FindNipples()
        {
            // The nipple on each side: the node standing proudest of a ring around it.
            nipL = -1;
            nipR = -1;
            promL = 0f;
            promR = 0f;
            foreach (int n in measure)
            {
                float sum = 0f;
                int ring = 0, sides = 0;
                // Height above the ring along the node's OWN normal where there is one, not the chest axis, and the
                // ring by distance on the body: along the axis a steep lower slope reads as prominence.
                var nn = nrm != null && n < nrm.Length ? nrm[n] : ax;
                foreach (int k in measure)
                {
                    float r = nrm != null ? Dist(pos[n], pos[k]) : Across(n, k);
                    if (r < ringIn || r > ringOut) continue;
                    sum += (pos[n].X - pos[k].X) * nn.X + (pos[n].Y - pos[k].Y) * nn.Y + (pos[n].Z - pos[k].Z) * nn.Z;
                    ring++;
                    sides |= (lat[k] >= lat[n] ? 1 : 2) | (ver[k] >= ver[n] ? 4 : 8);
                }
                // The ring has to SURROUND the node, or the edge of the breast-weighted skin reads as proud.
                if (ring < 4 || sides != 15) continue;
                float prom = sum / ring;
                if (lat[n] < midLat) { if (prom > promL) { promL = prom; nipL = n; } }
                else                 { if (prom > promR) { promR = prom; nipR = n; } }
            }
            if (nipL < 0 && nipR < 0)
            {
                log?.Invoke("nipple smooth: nothing stands proud of its surroundings — nothing to smooth");
                { result = null; return false; }
            }
            return true;
        }

        private bool RelaxPatch()
        {
            // How much each node is relaxed: full at a nipple, nothing at the disc's edge.
            amount = new float[count];
            touched = 0;
            // A local function rather than a stackalloc in the node loop, which would grow the frame per node.
            float Reach(int n, int nip)
            {
                if (nip < 0) return 0f;
                float r = Across(n, nip) / radius;
                return r >= 1f ? 0f : 1f - Smoothstep(r);
            }
            foreach (int n in region)
            {
                float best = MathF.Max(Reach(n, nipL), Reach(n, nipR));
                if (best <= 0f) continue;
                amount[n] = best * w[n];
                touched++;
            }
            if (touched == 0) { result = null; return false; }

            // The brush: move each patch node toward its neighbours' centroid in 3-D, a fixed number of times.
            // Jacobi (order-independent), with lambda <= 0.5 because lambda 1 oscillates on the checkerboard mode.
            patch = region.Where(n => amount[n] > 0f).ToList();
            cur = (Vec3[])pos.Clone();
            var next = (Vec3[])pos.Clone();
            for (int pass = 0; pass < NipplePasses; pass++)
            {
                foreach (int n in patch)
                {
                    if (adj[n] is not { Count: > 0 } near) continue;
                    float sx = 0f, sy = 0f, sz = 0f;
                    foreach (int k in near) { sx += cur[k].X; sy += cur[k].Y; sz += cur[k].Z; }
                    float inv = 1f / near.Count;
                    next[n] = new Vec3(
                        cur[n].X + (sx * inv - cur[n].X) * NippleRelaxLambda,
                        cur[n].Y + (sy * inv - cur[n].Y) * NippleRelaxLambda,
                        cur[n].Z + (sz * inv - cur[n].Z) * NippleRelaxLambda);
                }
                (cur, next) = (next, cur);
            }
            return true;
        }

        private void ApplyStrength()
        {
            // Falloff and strength on the RESULT, so lower strength travels less far toward the same smoothing.
            delta = new Vec3[count];
            foreach (int n in patch)
            {
                float a = amount[n] * strength;
                delta[n] = new Vec3((cur[n].X - pos[n].X) * a, (cur[n].Y - pos[n].Y) * a, (cur[n].Z - pos[n].Z) * a);
            }
        }

        private void FloorDomes()
        {
            // ── the dome floor ──────────────────────────────────────────────────────────────────────
            //
            // A nipple sits in a trough the relax cannot fill (widening the relax makes the tip prouder), so this
            // second pass moves toward a quadric fitted to the breast beyond it, extrapolated inward. Skipped when
            // the fit is not a dome: extrapolating one off a crescent invents a bulge.
            float floorRadius = radius * NippleDomeReach;
            float domeCap = MathF.Max(promL, promR);
            lifted = 0;
            shortNodes = 0;
            mostLift = 0f;
            shortfall = 0f;
            // Which nodes this pass touches, and how much; the finishing relax reuses it to stay local.
            finishW = new float[count];
            foreach (int nip in stackalloc[] { nipL, nipR })
            {
                if (nip < 0) continue;
                // Sampled off the BUST, not the covered part of it; see the onBust param.
                var ring = new List<(double, double, double)>();
                foreach (int n in measure)
                {
                    float r = Across(n, nip);
                    if (r < radius * NippleDomeRingInner || r > radius * NippleDomeRingOuter) continue;
                    ring.Add((lat[n] - lat[nip], ver[n] - ver[nip], h[n]));
                }
                var q = FitQuadric(ring);
                if (q == null || q[3] >= 0 || q[5] >= 0)
                {
                    log?.Invoke($"nipple smooth: no dome to fill toward on one side ({ring.Count} ring node(s)"
                              + (q == null ? ", fit failed" : $", curvature {q[3]:0.##}/{q[5]:0.##} is not convex")
                              + ") — that side keeps whatever trough it has");
                    continue;
                }
                // Sanity check: a dome standing proud of where the nipple was is extrapolating, not fitting.
                if (q[0] > h[nip])
                {
                    log?.Invoke($"nipple smooth: the dome fit on one side extrapolates past the nipple itself "
                              + $"({q[0]:0.#####} against {h[nip]:0.#####}) — discarded, that side keeps its trough");
                    continue;
                }

                foreach (int n in region)
                {
                    float rr = Across(n, nip);
                    if (rr > floorRadius) continue;
                    // Each node belongs to its nearer nipple, so the two domes cannot fight.
                    if (nipL >= 0 && nipR >= 0 && rr > Across(n, nip == nipL ? nipR : nipL)) continue;

                    // Taper the edge: a quadric never meets the surface exactly at its inner edge.
                    float edge = rr / floorRadius;
                    float fade = edge <= NippleDomeSolid ? 1f
                               : 1f - Smoothstep((edge - NippleDomeSolid) / (1f - NippleDomeSolid));
                    finishW[n] = MathF.Max(finishW[n], w[n] * fade);

                    double u = lat[n] - lat[nip], v = ver[n] - ver[nip];
                    double dome = q[0] + q[1] * u + q[2] * v + q[3] * u * u + q[4] * u * v + q[5] * v * v;
                    // Measured against where the relax left it, so the floor does not undo the shave.
                    float here = (pos[n].X + delta[n].X) * ax.X
                               + (pos[n].Y + delta[n].Y) * ax.Y
                               + (pos[n].Z + delta[n].Z) * ax.Z;
                    // Toward the dome from either side (raise-only undoes the relax at the tip), bounded by the
                    // nipple's own prominence: the trough cannot be deeper than the nipple is tall.
                    float want = Math.Clamp((float)dome - here, -domeCap, domeCap);
                    float lift = want * w[n] * strength * fade;
                    if (want > 0f) { shortfall += want - lift; shortNodes++; }
                    if (MathF.Abs(lift) <= 1e-7f) continue;
                    delta[n] = new Vec3(delta[n].X + ax.X * lift,
                                        delta[n].Y + ax.Y * lift,
                                        delta[n].Z + ax.Z * lift);
                    lifted++;
                    mostLift = MathF.Max(mostLift, MathF.Abs(lift));
                }
            }
        }

        private Vec3[]? LightRelax()
        {
            // ── the light relax ─────────────────────────────────────────────────────────────────────
            //
            // Smooths vertex-scale bumps the passes above leave wherever their weight is short of 1. A plain
            // Laplacian, not Taubin: evening out spacing tangentially is what reads as smooth, and Taubin undoes it.
            if (NippleFinishPasses > 0)
            {
                var cur2 = new Vec3[count];
                var next2 = new Vec3[count];
                for (int n = 0; n < count; n++)
                    cur2[n] = new Vec3(pos[n].X + delta[n].X, pos[n].Y + delta[n].Y, pos[n].Z + delta[n].Z);
                Array.Copy(cur2, next2, count);

                // Kept short (NippleFinishPasses) so the Laplacian's shrinkage never becomes real.
                var finishNodes = region.Where(n => finishW[n] > 0f).ToList();
                for (int pass = 0; pass < NippleFinishPasses; pass++)
                {
                    foreach (int n in finishNodes)
                    {
                        if (adj[n] is not { Count: > 0 } near) continue;
                        float sx = 0f, sy = 0f, sz = 0f;
                        foreach (int j in near) { sx += cur2[j].X; sy += cur2[j].Y; sz += cur2[j].Z; }
                        float inv = 1f / near.Count;
                        next2[n] = new Vec3(cur2[n].X + (sx * inv - cur2[n].X) * NippleFinishLambda,
                                            cur2[n].Y + (sy * inv - cur2[n].Y) * NippleFinishLambda,
                                            cur2[n].Z + (sz * inv - cur2[n].Z) * NippleFinishLambda);
                    }
                    (cur2, next2) = (next2, cur2);
                }

                // Faded by coverage and strength like everything else, so it stops at the garment's edge.
                foreach (int n in finishNodes)
                {
                    float a = finishW[n] * strength;
                    if (a <= 0f) continue;
                    float bx = pos[n].X + delta[n].X, by = pos[n].Y + delta[n].Y, bz = pos[n].Z + delta[n].Z;
                    delta[n] = new Vec3(delta[n].X + (cur2[n].X - bx) * a,
                                        delta[n].Y + (cur2[n].Y - by) * a,
                                        delta[n].Z + (cur2[n].Z - bz) * a);
                }

                if (log != null)
                {
                    // Measured on the graph the pass smooths, never on an export, which re-welds.
                    double Rough(Func<int, Vec3> at)
                    {
                        double sum = 0; int n2 = 0;
                        foreach (int n in patch)
                        {
                            if (adj[n] is not { Count: > 2 } near) continue;
                            float sx = 0f, sy = 0f, sz = 0f;
                            foreach (int j in near) { var q2 = at(j); sx += q2.X; sy += q2.Y; sz += q2.Z; }
                            float inv = 1f / near.Count;
                            var me = at(n);
                            sum += Len(new Vec3(sx * inv - me.X, sy * inv - me.Y, sz * inv - me.Z));
                            n2++;
                        }
                        return n2 == 0 ? 0 : sum / n2;
                    }
                    float meanW = patch.Count == 0 ? 0f : patch.Average(n => w[n]);
                    log($"nipple smooth: finish {NippleFinishPasses} pass(es) over {finishNodes.Count} node(s), "
                      + $"roughness {Rough(n => pos[n]):0.######} -> "
                      + $"{Rough(n => new Vec3(pos[n].X + delta[n].X, pos[n].Y + delta[n].Y, pos[n].Z + delta[n].Z)):0.######}"
                      + $", mean coverage weight {meanW:0.###}");
                }
            }

            float most = 0f;
            foreach (int n in region) most = MathF.Max(most, Len(delta[n]));

            int tip = promL >= promR ? nipL : nipR;
            string Where(int n) => n < 0 ? "none"
                : $"({pos[n].X:0.###},{pos[n].Y:0.###},{pos[n].Z:0.###})";
            log?.Invoke($"nipple smooth: {measure.Count} bust node(s), {region.Count} of them covered; "
                      + $"nipples {Where(nipL)} prom {promL:0.#####} / {Where(nipR)} prom "
                      + $"{promR:0.#####}, ring {ringIn:0.####}-{ringOut:0.####}, disc {radius:0.####}, "
                      + $"{touched} node(s) over {NipplePasses} pass(es), {lifted} filled toward the breast's "
                      + $"own curve by up to {mostLift:0.#####} (left {(shortNodes > 0 ? shortfall / shortNodes : 0f):0.#####} "
                      + $"short on average over {shortNodes}), tip moved "
                      + $"{(tip >= 0 ? Len(delta[tip]) : 0f):0.#####}, up to {most:0.#####}");
            return delta;
        }

        private float Across(int i, int j)
        {
            float du = lat[i] - lat[j], dv = ver[i] - ver[j];
            return MathF.Sqrt(du * du + dv * dv);
        }
    }
}
