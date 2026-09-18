using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    private sealed class CrotchFlattening
    {
        private readonly Vec3[] pos;
        private readonly Vec3[] nrm;
        private readonly ushort[] tris;
        private readonly float[] hip;
        private readonly bool[]? covered;
        private readonly float strength;
        private readonly Action<string>? log;
        private int vc;
        private int[] nodeOf = null!;
        private int nodeCount;
        private Vec3[] at = null!;
        private Vec3[] nn = null!;
        private float[] hipN = null!;
        private bool[] cut = null!;
        private float midX;
        private float lowY;
        private List<int> region = null!;
        private int bands;
        private Dictionary<int, List<int>> bandOf = null!;
        private List<int>[] adj = null!;
        private float[] fromLeft = null!;
        private float[] fromRight = null!;
        private int[] hemRing = null!;
        private float[] drop = null!;
        private float[] ceiling = null!;
        private float[] shift = null!;
        private Vec3[] overN = null!;
        private float[] overW = null!;
        private bool[] slitNode = null!;
        private int moved;
        private float most;
        private int[] leftOf = null!;
        private int[] rightOf = null!;
        private float[] yL = null!;
        private float[] yR = null!;
        private Vec3[] nL = null!;
        private Vec3[] nR = null!;
        private BustBridgePlan? result;

        public CrotchFlattening(Vec3[] pos, Vec3[] nrm, ushort[] tris, float[] hip, bool[]? covered, float strength, Action<string>? log)
        {
            this.pos = pos;
            this.nrm = nrm;
            this.tris = tris;
            this.hip = hip;
            this.covered = covered;
            this.strength = strength;
            this.log = log;
        }

        public BustBridgePlan? Run()
        {
            if (!FindUnderside()) return result;
            if (!BoundRegion()) return result;
            MarkRegion();
            FadeAtHem();
            FindLine();
            SmoothLine();
            if (!DropToLine()) return result;
            SmoothPatch();
            return KeepOutOfBody();
        }

        private bool FindUnderside()
        {
            vc = pos.Length;
            if (vc == 0 || strength <= 0f) { result = null; return false; }
            nodeOf = WeldByPosition(pos, out nodeCount);
            at = new Vec3[nodeCount];
            nn = new Vec3[nodeCount];
            hipN = new float[nodeCount];
            cut = new bool[nodeCount];
            var members = new int[nodeCount];
            for (int i = 0; i < vc; i++)
            {
                int n = nodeOf[i];
                at[n] = new Vec3(at[n].X + pos[i].X, at[n].Y + pos[i].Y, at[n].Z + pos[i].Z);
                nn[n] = new Vec3(nn[n].X + nrm[i].X, nn[n].Y + nrm[i].Y, nn[n].Z + nrm[i].Z);
                if (i < hip.Length) hipN[n] = MathF.Max(hipN[n], hip[i]);
                if (covered != null && i < covered.Length && !covered[i]) cut[n] = true;
                members[n]++;
            }
            for (int n = 0; n < nodeCount; n++)
            {
                at[n] = new Vec3(at[n].X / members[n], at[n].Y / members[n], at[n].Z / members[n]);
                nn[n] = Normalize(nn[n]) ?? default;
            }

            // The underside: hip-owned, facing down, near the midline. The midline is where the down-facing hip skin is
            // centred; its lowest point is the crotch.
            midX = 0f;
            lowY = float.MaxValue;
            int under = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                if (hipN[n] <= 0f || nn[n].Y > -CrotchDownFacing) continue;
                midX += at[n].X; under++;
            }
            if (under < MinBustBridgeNodes) { result = null; return false; }
            midX /= under;
            // The crotch's own lowest point, at the midline; over the whole legs part it is the underside of the buttocks.
            for (int n = 0; n < nodeCount; n++)
            {
                if (hipN[n] <= 0f || nn[n].Y > -CrotchDownFacing || MathF.Abs(at[n].X - midX) > CrotchFlatHalf) continue;
                if (at[n].Z < 0f) continue;   // the front half: the crotch, not the cleft behind
                lowY = MathF.Min(lowY, at[n].Y);
            }
            if (lowY == float.MaxValue) { result = null; return false; }
            return true;
        }

        private bool BoundRegion()
        {
            // The region takes the notch's walls as well as the underside (they face each other, sideways); anything facing
            // up, or straight forward or back, is left out. Front to back only as far as the underside itself reaches.
            float uzLo = float.MaxValue, uzHi = float.MinValue;
            for (int n = 0; n < nodeCount; n++)
            {
                if (hipN[n] <= 0f || nn[n].Y > -CrotchDownFacing) continue;
                if (MathF.Abs(at[n].X - midX) > CrotchFlatHalf || at[n].Y > lowY + CrotchFlatRise) continue;
                uzLo = MathF.Min(uzLo, at[n].Z); uzHi = MathF.Max(uzHi, at[n].Z);
            }
            // Not behind the vulva: the perineum and the cleft have their own span.
            uzLo = MathF.Max(uzLo, -CrotchFlatBehind);
            if (uzHi <= uzLo) { result = null; return false; }
            if (TraceSpanNode is { } crotchTrace)
                for (int n = 0; n < nodeCount; n++)
                    if (crotchTrace(at[n]))
                        log?.Invoke($"TRACE-CROTCH ({at[n].X * 1000:0.0},{at[n].Y:0.000},{at[n].Z * 1000:0.0}) hip {hipN[n]:0.00} "
                                  + $"cut {cut[n]} n({nn[n].X:0.00},{nn[n].Y:0.00},{nn[n].Z:0.00}) mid {midX * 1000:0.0} lowY {lowY:0.000} "
                                  + $"z {uzLo * 1000:0.0}..{uzHi * 1000:0.0}");
            region = new List<int>();
            for (int n = 0; n < nodeCount; n++)
            {
                if (hipN[n] <= 0f || cut[n]) continue;
                if (at[n].Z < uzLo || at[n].Z > uzHi) continue;
                if (MathF.Abs(at[n].X - midX) > CrotchFlatHalf + CrotchFlatFeather) continue;
                if (at[n].Y > lowY + CrotchFlatRise) continue;
                region.Add(n);
            }
            if (region.Count < MinBustBridgeNodes) { result = null; return false; }

            float zLo = region.Min(n => at[n].Z), zHi = region.Max(n => at[n].Z);
            bands = Math.Max(1, (int)MathF.Ceiling((zHi - zLo) / CrotchFlatBand));
            bandOf = new Dictionary<int, List<int>>();
            foreach (int n in region)
            {
                int b = Math.Clamp((int)((at[n].Z - zLo) / CrotchFlatBand), 0, bands - 1);
                if (!bandOf.TryGetValue(b, out var list)) bandOf[b] = list = new List<int>();
                list.Add(n);
            }

            adj = new List<int>[nodeCount];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                if (tris[t] >= vc || tris[t + 1] >= vc || tris[t + 2] >= vc) continue;
                int a = nodeOf[tris[t]], b = nodeOf[tris[t + 1]], c = nodeOf[tris[t + 2]];
                (adj[a] ??= new()).Add(b); (adj[b] ??= new()).Add(c); (adj[c] ??= new()).Add(a);
                (adj[b] ??= new()).Add(a); (adj[c] ??= new()).Add(b); (adj[a] ??= new()).Add(c);
            }
            return true;
        }

        private void MarkRegion()
        {
            // Inside the slit, where a node lands across the flat line comes from how far it is over the surface from the
            // skin outside the slit on either side: the walls overhang, so its own x and height say nothing about its order.
            var inRegion = new bool[nodeCount];
            foreach (int n in region) inRegion[n] = true;
            float[] Reach(bool leftSide)
            {
                var d = new float[nodeCount];
                Array.Fill(d, float.MaxValue);
                var queue = new PriorityQueue<int, float>();
                foreach (int n in region)
                {
                    float dx = at[n].X - midX;
                    if (leftSide ? dx <= -CrotchSlitHalf : dx >= CrotchSlitHalf) { d[n] = 0f; queue.Enqueue(n, 0f); }
                }
                while (queue.TryDequeue(out int n, out float dn))
                {
                    if (dn > d[n] || adj[n] is not { } near) continue;
                    foreach (int k in near)
                    {
                        if (!inRegion[k]) continue;
                        float ex = at[k].X - at[n].X, ey = at[k].Y - at[n].Y, ez = at[k].Z - at[n].Z;
                        float dk = dn + MathF.Sqrt(ex * ex + ey * ey + ez * ez);
                        if (dk < d[k]) { d[k] = dk; queue.Enqueue(k, dk); }
                    }
                }
                return d;
            }
            fromLeft = Reach(true);
            fromRight = Reach(false);
        }

        private void FadeAtHem()
        {
            // Faded to nothing at the garment's cut edge: uncovered nodes do not move but their triangles are still drawn, and
            // a covered node beside one laid down its full way stands a spike along the leg opening.
            hemRing = new int[nodeCount];
            Array.Fill(hemRing, int.MaxValue);
            var hemQ = new Queue<int>();
            for (int n = 0; n < nodeCount; n++) if (cut[n]) { hemRing[n] = 0; hemQ.Enqueue(n); }
            while (hemQ.Count > 0)
            {
                int c = hemQ.Dequeue();
                if (hemRing[c] >= CrotchFlatHemRings || adj[c] is not { } near) continue;
                foreach (int k in near) if (hemRing[k] > hemRing[c] + 1) { hemRing[k] = hemRing[c] + 1; hemQ.Enqueue(k); }
            }

            drop = new float[nodeCount];
            ceiling = new float[nodeCount];
            shift = new float[nodeCount];
            overN = new Vec3[nodeCount];
            overW = new float[nodeCount];
            slitNode = new bool[nodeCount];
            moved = 0;
            most = 0f;
        }

        private void FindLine()
        {
            // The lowest point either side of the midline in each slice, within the flat core and outside the slit (taken
            // from anywhere it is often the slit's own floor).
            leftOf = new int[bands];
            rightOf = new int[bands];
            Array.Fill(leftOf, -1);
            Array.Fill(rightOf, -1);
            foreach (var (b, list) in bandOf)
                foreach (int n in list)
                {
                    float dx = at[n].X - midX;
                    if (MathF.Abs(dx) > CrotchFlatHalf || MathF.Abs(dx) < CrotchSlitHalf) continue;
                    if (dx < 0f) { if (leftOf[b] < 0 || at[n].Y < at[leftOf[b]].Y) leftOf[b] = n; }
                    else if (rightOf[b] < 0 || at[n].Y < at[rightOf[b]].Y) rightOf[b] = n;
                }
        }

        private void SmoothLine()
        {
            // The line's ends smoothed front to back, heights and normals, so the slices do not step against each other.
            yL = new float[bands]; yR = new float[bands];
            nL = new Vec3[bands]; nR = new Vec3[bands];
            for (int b = 0; b < bands; b++)
            {
                if (leftOf[b] < 0 || rightOf[b] < 0) continue;
                yL[b] = at[leftOf[b]].Y; yR[b] = at[rightOf[b]].Y;
                nL[b] = nn[leftOf[b]]; nR[b] = nn[rightOf[b]];
            }
            for (int pass = 0; pass < CrotchFlatLineSmooth; pass++)
            {
                var yL2 = (float[])yL.Clone(); var yR2 = (float[])yR.Clone();
                var nL2 = (Vec3[])nL.Clone(); var nR2 = (Vec3[])nR.Clone();
                for (int b = 0; b < bands; b++)
                {
                    if (leftOf[b] < 0 || rightOf[b] < 0) continue;
                    float sl = yL[b], sr = yR[b];
                    Vec3 ml = nL[b], mr = nR[b];
                    int count = 1;
                    foreach (int k in new[] { b - 1, b + 1 })
                    {
                        if (k < 0 || k >= bands || leftOf[k] < 0 || rightOf[k] < 0) continue;
                        sl += yL[k]; sr += yR[k];
                        ml = new Vec3(ml.X + nL[k].X, ml.Y + nL[k].Y, ml.Z + nL[k].Z);
                        mr = new Vec3(mr.X + nR[k].X, mr.Y + nR[k].Y, mr.Z + nR[k].Z);
                        count++;
                    }
                    yL2[b] = sl / count; yR2[b] = sr / count;
                    nL2[b] = Normalize(ml) ?? nL[b]; nR2[b] = Normalize(mr) ?? nR[b];
                }
                (yL, yR, nL, nR) = (yL2, yR2, nL2, nR2);
            }
        }

        private bool DropToLine()
        {
            foreach (var (b, list) in bandOf)
            {
                int left = leftOf[b], right = rightOf[b];
                if (left < 0 || right < 0) continue;
                float xl = at[left].X, xr = at[right].X;
                if (xr - xl < 1e-5f) continue;
                // Ends of the crotch front and back fade in over a couple of bands, so the flat patch does not step.
                float bw = Smoothstep(Math.Clamp(MathF.Min(b + 0.5f, bands - b - 0.5f) / CrotchFlatEndBands, 0f, 1f));
                foreach (int n in list)
                {
                    float x = at[n].X;
                    if (x <= xl || x >= xr) continue;
                    float xTo = x;
                    bool inSlit = slitNode[n] = MathF.Abs(x - midX) < CrotchSlitHalf;
                    if (inSlit)
                    {
                        float dl = fromLeft[n], dr = fromRight[n];
                        if (dl == float.MaxValue || dr == float.MaxValue) continue;
                        float s = dl + dr > 1e-7f ? dl / (dl + dr) : 0.5f;
                        // Packed into a narrow seam rather than spread across the slit's width, or its faces run stretched and crooked.
                        xTo = midX - CrotchSeamHalf + 2f * CrotchSeamHalf * s;
                    }
                    float t = (xTo - xl) / (xr - xl);
                    float line = yL[b] + (yR[b] - yL[b]) * t;
                    float up = at[n].Y - line;
                    if (up <= 0f && !inSlit) continue;
                    float side = 1f - Smoothstep(Math.Clamp((MathF.Abs(x - midX) - CrotchFlatHalf) / CrotchFlatFeather, 0f, 1f));
                    float hem = Hem(n);
                    if (hem <= 0f) continue;
                    // Outside the slit the labia only come down, keeping a sliver of their height so they keep their order.
                    // Inside it everything lies ON the line: its order across it now comes from xTo, not from height.
                    // The hem fade is in the ceiling, so the smoothing below cannot carry the drop back up to the edge.
                    ceiling[n] = MathF.Max(0f, MathF.Min(up * (inSlit ? 1f : 1f - CrotchFlatKeep), CrotchFlatMaxDrop)) * strength
                               * hem;
                    drop[n] = ceiling[n] * bw * side;
                    side *= hem;
                    shift[n] = (xTo - x) * bw * side * strength;
                    // Shaded as the flat line it now lies on. Recomputed from its faces, a slit laid flat still has some
                    // turned over, and their normals came out either way up.
                    var lineN = Normalize(new Vec3(nL[b].X + (nR[b].X - nL[b].X) * t, nL[b].Y + (nR[b].Y - nL[b].Y) * t,
                                                   nL[b].Z + (nR[b].Z - nL[b].Z) * t));
                    if (lineN is { } ln)
                    {
                        overN[n] = ln;
                        overW[n] = bw * side * strength * (inSlit ? 1f : Smoothstep(Math.Clamp(up / CrotchSeamHalf, 0f, 1f)));
                    }
                    most = MathF.Max(most, drop[n]);
                    moved++;
                }
            }
            if (moved == 0) { result = null; return false; }
            return true;
        }

        private void SmoothPatch()
        {
            // Smoothed among the dropped nodes and their neighbours, so the flat patch eases into the surface around it.
            for (int pass = 0; pass < CrotchFlatSmooth; pass++)
            {
                var next = (float[])drop.Clone();
                for (int n = 0; n < nodeCount; n++)
                {
                    if (adj[n] is not { Count: > 0 } near || cut[n]) continue;
                    float s = 0f;
                    foreach (int k in near) s += drop[k];
                    float avg = s / near.Count;
                    // Never below its own drop: the notch must still come down all the way. Never past its own line either:
                    // a rim node dragged down by the walls beside it bulged below the flat patch.
                    next[n] = MathF.Min(ceiling[n], MathF.Max(drop[n], avg * 0.5f + drop[n] * 0.5f));
                }
                drop = next;
            }
        }

        private BustBridgePlan? KeepOutOfBody()
        {
            // NEVER INTO THE BODY. Toward the region's front the slices cross forward-facing surface, and moved straight
            // down that slides under whatever bulges out below it. Tested against the surface as it was: each moved node
            // backed off until outside, then each edge with a moved end drawn back until it clears. Not the slit's own
            // edges: closing the slit folds its walls in behind the flat surface by construction.
            var body = new BodyWinding(at, Array.ConvertAll(tris, t => (int)t), nodeOf);
            var keep = new float[nodeCount];
            Array.Fill(keep, 1f);
            Vec3 MovedTo(int n) => new(at[n].X + shift[n] * keep[n], at[n].Y - drop[n] * keep[n], at[n].Z);
            bool Moves(int n) => drop[n] > BustBridgeEpsilon || MathF.Abs(shift[n]) > BustBridgeEpsilon;
            int heldBack = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                if (!Moves(n)) continue;
                bool held = false;
                while (keep[n] > 0f && body.Inside(MovedTo(n))) { keep[n] = MathF.Max(0f, keep[n] - 0.25f); held = true; }
                if (held) heldBack++;
            }
            for (int round = 0; round < CrotchFlatEdgeRounds; round++)
            {
                int backed = 0;
                for (int n = 0; n < nodeCount; n++)
                {
                    if (!Moves(n) || keep[n] <= 0f || slitNode[n] || adj[n] is not { } near) continue;
                    var pn = MovedTo(n);
                    float moveN = drop[n] * keep[n] + MathF.Abs(shift[n]) * keep[n];
                    bool bad = false;
                    foreach (int k in near)
                    {
                        // The further-moved end answers for the edge.
                        if (slitNode[k] || drop[k] * keep[k] + MathF.Abs(shift[k]) * keep[k] > moveN) continue;
                        var pk = MovedTo(k);
                        for (int s = 1; s < EdgeInsideSamples && !bad; s++)
                        {
                            float f = s / (float)EdgeInsideSamples;
                            bad = body.Inside(new Vec3(pn.X + (pk.X - pn.X) * f, pn.Y + (pk.Y - pn.Y) * f, pn.Z + (pk.Z - pn.Z) * f));
                        }
                        if (bad) break;
                    }
                    if (!bad) continue;
                    keep[n] *= 0.7f;
                    backed++;
                }
                if (backed == 0) break;
                heldBack += backed;
            }
            most = 0f;
            for (int n = 0; n < nodeCount; n++)
            {
                drop[n] *= keep[n];
                shift[n] *= keep[n];
                overW[n] *= keep[n];
                most = MathF.Max(most, drop[n]);
            }

            var delta = new Vec3[vc];
            var weight = new float[nodeCount];
            for (int i = 0; i < vc; i++)
            {
                float d = drop[nodeOf[i]], sx = shift[nodeOf[i]];
                if (d <= BustBridgeEpsilon && MathF.Abs(sx) <= BustBridgeEpsilon) continue;
                delta[i] = new Vec3(sx, -d, 0f);
                weight[nodeOf[i]] = 1f;
            }
            log?.Invoke($"crotch flat: {moved} node(s) in {bandOf.Count} slice(s) laid down flat across the crotch, by up to "
                      + $"{most * 1000:0.#}mm{(heldBack > 0 ? $" ({heldBack} held back to stay outside the body)" : "")}");
            return new BustBridgePlan
            {
                Delta = delta, NodeOf = nodeOf, NodeWeight = weight, NodeNormal = nn,
                NormalOverride = overN, NormalOverrideWeight = overW,
            };
        }

        private float Hem(int n) => hemRing[n] >= CrotchFlatHemRings ? 1f : Smoothstep(hemRing[n] / (float)CrotchFlatHemRings);
    }
}
