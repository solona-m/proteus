using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    private sealed class SlingSolution
    {
        private readonly Vec3[] start;
        private readonly Vec3[] nNorm;
        private readonly float[] h0;
        private readonly float[] lat;
        private readonly float[] ver;
        private readonly bool[] seed;
        private readonly bool[] cut;
        private readonly List<int>[] adj;
        private readonly int nodeCount;
        private readonly Vec3 ax;
        private readonly Vec3 lateral;
        private readonly Vec3 vertical;
        private readonly BodyWinding body;
        private readonly Action<string>? log;
        private float up;
        private float latLo;
        private float latHi;
        private float meanH;
        private float seedLo;
        private float seedHi;
        private float bottom;
        private float edge;
        private float binW;
        private float cLat;
        private float cH;
        private float seedR;
        private int bins;
        private int[] ring = null!;
        private Vec3[] moveD = null!;
        private int moved;
        private int heldInside;
        private float most;
        private Vec3[]? result;

        public SlingSolution(Vec3[] start, Vec3[] nNorm, float[] h0, float[] lat, float[] ver, bool[] seed, bool[] cut, List<int>[] adj, int nodeCount, Vec3 ax, Vec3 lateral, Vec3 vertical, BodyWinding body, Action<string>? log)
        {
            this.start = start;
            this.nNorm = nNorm;
            this.h0 = h0;
            this.lat = lat;
            this.ver = ver;
            this.seed = seed;
            this.cut = cut;
            this.adj = adj;
            this.nodeCount = nodeCount;
            this.ax = ax;
            this.lateral = lateral;
            this.vertical = vertical;
            this.body = body;
            this.log = log;
        }

        public Vec3[]? Run(out float[] weight, out float[] between)
        {
            weight = null!;
            between = null!;
            if (!FindSeeds(ref weight, ref between)) return result;
            if (!HangSlices(ref weight)) return result;
            SmoothSling(ref weight);
            KeepEdgesOut(ref weight);
            return SpanSternum(ref weight, ref between);
        }

        private bool FindSeeds(ref float[] weight, ref float[] between)
        {
            weight = new float[nodeCount];
            between = new float[nodeCount];
            // Up the body along `vertical`, whichever way its PCA sign came out.
            up = vertical.Y >= 0f ? 1f : -1f;

            var seedV = new List<float>();
            latLo = float.MaxValue;
            latHi = float.MinValue;
            float edgeSum = 0f;
            meanH = 0f;
            int edgeN = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                meanH += h0[n];
                if (!seed[n]) continue;
                seedV.Add(ver[n] * up);
                latLo = MathF.Min(latLo, lat[n]); latHi = MathF.Max(latHi, lat[n]);
                foreach (int k in adj[n]) { edgeSum += Dist(start[n], start[k]); edgeN++; }
            }
            meanH /= Math.Max(1, nodeCount);
            if (seedV.Count < MinBustBridgeNodes || edgeN == 0 || latHi <= latLo) { result = null; return false; }
            seedV.Sort();
            seedLo = seedV[seedV.Count / 50];
            seedHi = seedV[seedV.Count - 1 - seedV.Count / 50];
            float reach = (seedHi - seedLo) * SlingReachDown;
            bottom = seedLo - reach;
            edge = edgeSum / edgeN;
            binW = edge;

            // The slices reach a little past the breasts' width, and each slice past the middle of a breast turns its "out"
            // toward the flank, so the outer lower corner of each breast is not left sucked into the skin.
            cLat = 0f;
            cH = 0f;
            seedR = 0f;
            int cN = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                float v = ver[n] * up;
                if (v < bottom || v > seedHi) continue;
                cLat += lat[n]; cH += h0[n]; cN++;
            }
            if (cN == 0) { result = null; return false; }
            cLat /= cN; cH /= cN;
            for (int n = 0; n < nodeCount; n++)
                if (seed[n]) seedR = MathF.Max(seedR, MathF.Abs(lat[n] - cLat));
            if (seedR <= 1e-6f) { result = null; return false; }
            float latExt = (latHi - latLo) * SlingSideExtend;
            latLo -= latExt; latHi += latExt;
            bins = Math.Clamp((int)MathF.Ceiling((latHi - latLo) / binW), 1, 1024);

            // Hem fade: nothing moves on uncovered cloth, and it comes back over a few rings.
            ring = new int[nodeCount];
            Array.Fill(ring, int.MaxValue);
            var q = new Queue<int>();
            for (int n = 0; n < nodeCount; n++) if (cut[n]) { ring[n] = 0; q.Enqueue(n); }
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                if (ring[c] >= BustHemRings) continue;
                foreach (int k in adj[c]) if (ring[k] > ring[c] + 1) { ring[k] = ring[c] + 1; q.Enqueue(k); }
            }
            return true;
        }

        private bool HangSlices(ref float[] weight)
        {
            // The front profile of each slice: nodes on the front half of the body (not the back, whose points would
            // sit inside every hull and be dragged to the front), facing anything but backwards.
            var inBin = new List<int>[bins];
            for (int b = 0; b < bins; b++) inBin[b] = new List<int>();
            for (int n = 0; n < nodeCount; n++)
            {
                float v = ver[n] * up;
                if (lat[n] < latLo || lat[n] > latHi || v < bottom || v > seedHi) continue;
                if (h0[n] < meanH || Dot(nNorm[n], ax) < -0.5f) continue;
                inBin[Math.Clamp((int)((lat[n] - latLo) / binW), 0, bins - 1)].Add(n);
            }

            moveD = new Vec3[nodeCount];
            moved = 0;
            heldInside = 0;
            most = 0f;
            var pts = new List<(float V, float U, int N)>();
            var hull = new List<(float V, float U, int N)>();
            for (int b = 0; b < bins; b++)
            {
                if (inBin[b].Count == 0) continue;
                // This slice's "out": forward across the front, turning toward the flank past the middle of the breast —
                // the angle of the slice round a torso as wide as the breasts reach.
                float latB = latLo + (b + 0.5f) * binW;
                float angB = FrameAngle(MathF.Asin(Math.Clamp((latB - cLat) / (seedR * SlingTorsoWidth), -1f, 1f)));
                float cosB = MathF.Cos(angB), sinB = MathF.Sin(angB);
                float U(int n) => (h0[n] - cH) * cosB + (lat[n] - cLat) * sinB;
                // Nothing further out than the breast itself: past the flank an arm hanging beside the torso stands out
                // further than anything, and a hull taking it would run the sling from the breast to the arm.
                float maxSeedU = float.MinValue;
                for (int w = Math.Max(0, b - 1); w <= Math.Min(bins - 1, b + 1); w++)
                    foreach (int n in inBin[w]) if (seed[n]) maxSeedU = MathF.Max(maxSeedU, U(n));
                pts.Clear();
                for (int w = Math.Max(0, b - 1); w <= Math.Min(bins - 1, b + 1); w++)
                    foreach (int n in inBin[w])
                        if (seed[n] || maxSeedU == float.MinValue || U(n) <= maxSeedU + SlingArmMargin)
                            pts.Add((ver[n] * up, U(n), n));
                if (pts.Count < 3) continue;
                pts.Sort((a, c) => a.V != c.V ? a.V.CompareTo(c.V) : a.U.CompareTo(c.U));

                // The forward side of the hull, bottom to top (monotone chain; a right turn keeps the chain convex
                // toward +U).
                hull.Clear();
                foreach (var p in pts)
                {
                    while (hull.Count >= 2)
                    {
                        var o = hull[^2]; var a = hull[^1];
                        float cross = (a.V - o.V) * (p.U - o.U) - (a.U - o.U) * (p.V - o.V);
                        if (cross < 0f) break;
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(p);
                }
                int apex = 0;
                for (int i = 1; i < hull.Count; i++) if (hull[i].U > hull[apex].U) apex = i;

                // The one segment that is the sling: from the lowest hull point still on the breast down to the next hull point.
                // Other hull edges trace the breast front or bridge ribs and waist, which is not this feature.
                int top = -1;
                for (int i = 1; i <= apex; i++)
                    if (seed[hull[i].N] && !seed[hull[i - 1].N]) { top = i; break; }
                if (top < 1) continue;
                var sc = hull[top];

                // The line ends on the skin: the hull is taken again over only the profile from a fixed distance under the breast
                // upward, so the segment below the breast runs straight to the skin at that depth (or to the ribs if they come
                // forward first). One landing height for every slice, so the sling's lower edge is one even line across the body.
                float vCut = seedLo - SlingLength * (seedHi - seedLo);
                pts.RemoveAll(p => p.V < vCut);
                hull.Clear();
                foreach (var p in pts)
                {
                    while (hull.Count >= 2)
                    {
                        var o = hull[^2]; var a = hull[^1];
                        float cross = (a.V - o.V) * (p.U - o.U) - (a.U - o.U) * (p.V - o.V);
                        if (cross < 0f) break;
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(p);
                }
                // The segment is the longest hull edge from the bottom up to the slice's most-forward point — the jump over the
                // crease. The breast bones may stop partway down the breast, so the edge under the first weighted point can span nothing.
                int apexCut = 0;
                for (int i = 1; i < hull.Count; i++) if (hull[i].U > hull[apexCut].U) apexCut = i;
                int topCut = -1;
                float longest = 0f;
                for (int i = 1; i <= apexCut; i++)
                {
                    float lv = hull[i].V - hull[i - 1].V, lu = hull[i].U - hull[i - 1].U, l2 = lv * lv + lu * lu;
                    if (l2 > longest) { longest = l2; topCut = i; }
                }
                if (topCut < 1) continue;
                var sa = hull[topCut - 1];
                sc = hull[topCut];
                float segV = sc.V - sa.V, segU = sc.U - sa.U, segLen2 = segV * segV + segU * segU;
                if (segLen2 < (SlingMinSpan * edge) * (SlingMinSpan * edge)) continue;

                // Lateral fade over the stretch the slices reach past the bust — not over the bust itself any more.
                float t = ((b + 0.5f) * binW) / (latHi - latLo);
                float latW = Smoothstep(Math.Clamp(MathF.Min(t, 1f - t) / SlingSideFade, 0f, 1f));
                if (latW <= 0f) continue;
                var outB = new Vec3(ax.X * cosB + lateral.X * sinB, ax.Y * cosB + lateral.Y * sinB, ax.Z * cosB + lateral.Z * sinB);

                foreach (int n in inBin[b])
                {
                    float pv = ver[n] * up, pu = U(n);
                    if (maxSeedU != float.MinValue && !seed[n] && pu > maxSeedU + SlingArmMargin) continue;
                    if (pv >= sc.V || pv <= sa.V) continue;
                    float s = Math.Clamp(((pv - sa.V) * segV + (pu - sa.U) * segU) / segLen2, 0f, 1f);
                    float qv = sa.V + segV * s, qu = sa.U + segU * s;
                    // A light concave curve rather than the straight line: cloth stretched from a breast down to the ribs dips a
                    // little between them.
                    float segLen = MathF.Sqrt(segLen2);
                    float sag = SlingSag * segLen * 4f * s * (1f - s);
                    qv += segU / segLen * sag;
                    qu -= segV / segLen * sag;
                    float dist = MathF.Sqrt((qv - pv) * (qv - pv) + (qu - pu) * (qu - pu));
                    if (dist < SlingMinMove || dist > SlingMaxMove || qu < pu) continue;

                    float wt = latW;
                    if (cut[n]) wt = 0f;
                    else if (ring[n] < BustHemRings) wt *= Smoothstep(ring[n] / (float)BustHemRings);
                    if (wt <= 0f) continue;

                    float dv = (qv - pv) * up * wt, du = (qu - pu) * wt;
                    var d = new Vec3(outB.X * du + vertical.X * dv, outB.Y * du + vertical.Y * dv, outB.Z * du + vertical.Z * dv);
                    // The line is in front of the skin in this slice, but a neighbouring slice's breast can still be in
                    // the way; back off toward the start until the end is clear.
                    float keep = 1f;
                    while (keep > 0f && body.Inside(new Vec3(start[n].X + d.X * keep, start[n].Y + d.Y * keep, start[n].Z + d.Z * keep)))
                        keep -= 0.25f;
                    if (keep < 1f) heldInside++;
                    if (keep <= 0f) continue;
                    moveD[n] = new Vec3(d.X * keep, d.Y * keep, d.Z * keep);
                    weight[n] = wt * keep;
                    most = MathF.Max(most, Len(moveD[n]));
                    moved++;
                }
            }
            if (moved == 0) { result = null; return false; }
            return true;
        }

        private void SmoothSling(ref float[] weight)
        {
            // Smoothed among the nodes it moved, reading unmoved neighbours as zero, so no node stands far out past the ones
            // around it: each slice solves alone.
            var next = new Vec3[nodeCount];
            for (int pass = 0; pass < SlingSmoothPasses; pass++)
            {
                for (int n = 0; n < nodeCount; n++)
                {
                    if (weight[n] <= 0f || adj[n].Count == 0) { next[n] = moveD[n]; continue; }
                    float sx = 0f, sy = 0f, sz = 0f;
                    foreach (int k in adj[n]) { sx += moveD[k].X; sy += moveD[k].Y; sz += moveD[k].Z; }
                    float inv = 1f / adj[n].Count;
                    next[n] = new Vec3((moveD[n].X + sx * inv) * 0.5f, (moveD[n].Y + sy * inv) * 0.5f, (moveD[n].Z + sz * inv) * 0.5f);
                }
                (moveD, next) = (next, moveD);
            }
        }

        private void KeepEdgesOut(ref float[] weight)
        {
            // Then the faces, not just the nodes: every edge with a moved end is sampled against the body, and the
            // further-moved end is drawn back until the edge clears.
            for (int round = 0; round < SlingEdgeRounds; round++)
            {
                int backed = 0;
                for (int n = 0; n < nodeCount; n++)
                {
                    if (weight[n] <= 0f) continue;
                    var pn = new Vec3(start[n].X + moveD[n].X, start[n].Y + moveD[n].Y, start[n].Z + moveD[n].Z);
                    bool bad = body.Inside(pn);
                    if (!bad)
                        foreach (int k in adj[n])
                        {
                            if (Len(moveD[k]) > Len(moveD[n])) continue;   // the further-moved end answers for the edge
                            var pk = new Vec3(start[k].X + moveD[k].X, start[k].Y + moveD[k].Y, start[k].Z + moveD[k].Z);
                            for (int s = 1; s < EdgeInsideSamples && !bad; s++)
                            {
                                float f = s / (float)EdgeInsideSamples;
                                bad = body.Inside(new Vec3(pn.X + (pk.X - pn.X) * f, pn.Y + (pk.Y - pn.Y) * f, pn.Z + (pk.Z - pn.Z) * f));
                            }
                            if (bad) break;
                        }
                    if (!bad) continue;
                    moveD[n] = new Vec3(moveD[n].X * 0.7f, moveD[n].Y * 0.7f, moveD[n].Z * 0.7f);
                    backed++;
                }
                if (backed == 0) break;
                heldInside += backed;
            }
            most = 0f;
            for (int n = 0; n < nodeCount; n++)
            {
                if (Len(moveD[n]) < SlingMinMove * 0.2f) { moveD[n] = default; weight[n] = 0f; }
                most = MathF.Max(most, Len(moveD[n]));
            }
        }

        private Vec3[]? SpanSternum(ref float[] weight, ref float[] between)
        {
            // Between the two slings the sternum has no breast to hang one from, so the pair alone leaves a groove down the
            // midline. Every front node between the innermost slung node on either side, in the same band, is handed to the
            // span across, which then runs between the slings as it runs between the breasts.
            float midLat = (latLo + latHi) * 0.5f;
            float vLo = float.MaxValue, vHi = float.MinValue;
            for (int n = 0; n < nodeCount; n++)
                if (weight[n] > 0f) { vLo = MathF.Min(vLo, ver[n] * up); vHi = MathF.Max(vHi, ver[n] * up); }
            int vBands = Math.Clamp((int)MathF.Ceiling((vHi - vLo) / edge) + 1, 1, 1024);
            var innerL = new float[vBands]; var innerR = new float[vBands];
            var wL = new float[vBands]; var wR = new float[vBands];
            var wBand = new float[vBands];
            Array.Fill(innerL, float.MinValue); Array.Fill(innerR, float.MaxValue);
            for (int n = 0; n < nodeCount; n++)
            {
                if (weight[n] > 0f)
                {
                    int wb = Math.Clamp((int)((ver[n] * up - vLo) / edge), 0, vBands - 1);
                    wBand[wb] = MathF.Max(wBand[wb], weight[n]);
                }
                // Only a node the sling holds firmly marks where a sling is; the faint nodes toward the midline are the groove
                // this exists to fill, not its edges.
                if (weight[n] < SlingSolid) continue;
                int vb = Math.Clamp((int)((ver[n] * up - vLo) / edge), 0, vBands - 1);
                if (lat[n] < midLat) { if (lat[n] > innerL[vb]) { innerL[vb] = lat[n]; wL[vb] = weight[n]; } }
                else if (lat[n] < innerR[vb]) { innerR[vb] = lat[n]; wR[vb] = weight[n]; }
            }
            int bridged = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                if (weight[n] >= SlingSolid || cut[n] || h0[n] < meanH) continue;
                float v = ver[n] * up;
                if (v < vLo || v > vHi) continue;
                // A band either way: the slung nodes each side sit on their own rows, which need not share the midline's.
                int vb = Math.Clamp((int)((v - vLo) / edge), 0, vBands - 1);
                float il = float.MinValue, ir = float.MaxValue, wb = 0f;
                for (int k = Math.Max(0, vb - 1); k <= Math.Min(vBands - 1, vb + 1); k++)
                {
                    il = MathF.Max(il, innerL[k]);
                    ir = MathF.Min(ir, innerR[k]);
                    wb = MathF.Max(wb, wBand[k]);
                }
                if (il == float.MinValue || ir == float.MaxValue) continue;
                if (lat[n] <= il || lat[n] >= ir) continue;
                // Full weight wherever the slings are, fading with them where they fade. Not their weight itself: the chord rides
                // on their already-faded surface, and scaling by that weight again fades it twice.
                between[n] = MathF.Min(1f, 2f * wb);
                bridged++;
            }
            log?.Invoke($"under-bust sling: {bridged} node(s) between the two slings handed to the span across");
            log?.Invoke($"under-bust sling: {moved} node(s) laid onto the line from each breast down to the ribs, "
                      + $"up to {most * 1000:0.#}mm{(heldInside > 0 ? $" ({heldInside} shortened to stay outside the body)" : "")}");
            return moveD;
        }

        private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        // Forward across the front, turning to the true angle only past the middle of each breast; turned all the way in,
        // the inner underside slides sideways away from the midline.
        private static float FrameAngle(float a)
        {
            float m = MathF.Abs(a);
            float f = Smoothstep(Math.Clamp((m - SlingFrontAngle) / (SlingRadialAngle - SlingFrontAngle), 0f, 1f));
            return MathF.Sign(a) * m * f;
        }
    }
}
