using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    private sealed class EnvelopeSolution
    {
        private readonly float[] h0;
        private readonly float[] lat;
        private readonly float[] ver;
        private readonly float[] w;
        private readonly int count;
        private readonly List<int>[] adj;
        private readonly Vec3[] pos;
        private readonly Action<string>? log;
        private float[] target = null!;
        private float loV;
        private int bands;
        private float bandH;
        private float[,] binMax = null!;
        private float[,] binLat = null!;
        private float loL;
        private float latW;
        private float midL;
        private float[,] prof = null!;
        private bool[] fitOk = null!;
        private float[] bandRelief = null!;
        private float[] result = null!;

        public EnvelopeSolution(float[] h0, float[] lat, float[] ver, float[] w, int count, List<int>[] adj, Vec3[] pos, Action<string>? log)
        {
            this.h0 = h0;
            this.lat = lat;
            this.ver = ver;
            this.w = w;
            this.count = count;
            this.adj = adj;
            this.pos = pos;
            this.log = log;
        }

        public float[] Run()
        {
            if (!MeasureBands()) return result;
            if (!FindEnvelope()) return result;
            SmoothProfile();
            MeasureRelief();
            return SmoothBands();
        }

        private bool MeasureBands()
        {
            target = (float[])h0.Clone();

            loV = float.MaxValue;
            float hiV = float.MinValue;
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;
                loV = MathF.Min(loV, ver[n]); hiV = MathF.Max(hiV, ver[n]);
            }
            if (hiV <= loV) { result = target; return false; }

            // One band per edge of mesh, as the chord does — a band as fine as the geometry can express.
            float edge = 0f; int edges = 0;
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f || adj[n] == null) continue;
                foreach (int k in adj[n])
                {
                    float dx = pos[k].X - pos[n].X, dy = pos[k].Y - pos[n].Y, dz = pos[k].Z - pos[n].Z;
                    edge += MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    edges++;
                }
            }
            edge = edges > 0 ? edge / edges : 0f;
            bands = edge > 1e-6f ? (int)MathF.Round((hiV - loV) / edge) : 0;
            bands = Math.Clamp(bands, 3, 512);
            bandH = (hiV - loV) / bands;
            if (bandH <= 1e-9f) { result = target; return false; }
            return true;
        }

        private bool FindEnvelope()
        {
            // The envelope, per band: the frontmost node in each lateral bin.
            binMax = new float[bands, EnvelopeBins];
            binLat = new float[bands, EnvelopeBins];
            for (int b = 0; b < bands; b++)
                for (int q = 0; q < EnvelopeBins; q++) binMax[b, q] = float.MinValue;

            loL = float.MaxValue;
            float hiL = float.MinValue;
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;
                loL = MathF.Min(loL, lat[n]); hiL = MathF.Max(hiL, lat[n]);
            }
            if (hiL <= loL) { result = target; return false; }
            latW = hiL - loL;
            midL = (loL + hiL) * 0.5f;

            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;
                int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
                int q = Bin(lat[n]);
                if (h0[n] > binMax[b, q]) { binMax[b, q] = h0[n]; binLat[b, q] = lat[n]; }
            }
            return true;
        }

        private void SmoothProfile()
        {
            // A low-pass along the band, not a polynomial: the fold's cross-section is a W, which a parabola cannot describe.
            // What separates body from fold is scale, not shape — the body's curvature runs the width of the corridor, the
            // fold wiggles several times across it. Two-sided, so the ridge comes down as the grooves come up. Sized in
            // millimetres, not bins: the bins span the feathered corridor, which varies with the body.
            // k passes of [1 2 1] is a Gaussian of sigma = sqrt(k/2) bins, so k = 2*(sigma/binWidth)^2.
            float binW = latW / EnvelopeBins;
            int passes = Math.Clamp((int)MathF.Round(2f * (EnvelopeSmoothSigma / binW) * (EnvelopeSmoothSigma / binW)),
                                    1, 40);

            prof = new float[bands, EnvelopeBins];
            fitOk = new bool[bands];
            var row = new float[EnvelopeBins];
            var next = new float[EnvelopeBins];
            for (int b = 0; b < bands; b++)
            {
                int have = 0;
                for (int q = 0; q < EnvelopeBins; q++) if (binMax[b, q] != float.MinValue) have++;
                if (have < EnvelopeMinBins) continue;

                // Gaps filled from the nearest sample either side before smoothing, so an empty bin does not drag its neighbours
                // toward zero and the kernel stays uniform.
                for (int q = 0; q < EnvelopeBins; q++)
                {
                    if (binMax[b, q] != float.MinValue) { row[q] = binMax[b, q]; continue; }
                    float lo2 = float.MinValue, hi2 = float.MinValue;
                    for (int k = q - 1; k >= 0; k--) if (binMax[b, k] != float.MinValue) { lo2 = binMax[b, k]; break; }
                    for (int k = q + 1; k < EnvelopeBins; k++) if (binMax[b, k] != float.MinValue) { hi2 = binMax[b, k]; break; }
                    row[q] = lo2 == float.MinValue ? hi2 : hi2 == float.MinValue ? lo2 : (lo2 + hi2) * 0.5f;
                }

                // [1 2 1], repeated. Clamped at the ends rather than wrapped or reflected: the corridor's edge
                // is the inner thigh, not the other side of the same feature.
                for (int pass = 0; pass < passes; pass++)
                {
                    for (int q = 0; q < EnvelopeBins; q++)
                    {
                        float l = row[Math.Max(0, q - 1)], r = row[Math.Min(EnvelopeBins - 1, q + 1)];
                        next[q] = (l + 2f * row[q] + r) * 0.25f;
                    }
                    (row, next) = (next, row);
                }

                for (int q = 0; q < EnvelopeBins; q++) prof[b, q] = row[q];
                fitOk[b] = true;
            }
        }

        private void MeasureRelief()
        {
            // How much the envelope rises and falls across each band: the size of whatever feature that row contains.
            bandRelief = new float[bands];
            for (int b = 0; b < bands; b++)
            {
                float lo2 = float.MaxValue, hi2 = float.MinValue;
                for (int q = 0; q < EnvelopeBins; q++)
                {
                    if (binMax[b, q] == float.MinValue) continue;
                    lo2 = MathF.Min(lo2, binMax[b, q]); hi2 = MathF.Max(hi2, binMax[b, q]);
                }
                bandRelief[b] = hi2 > lo2 ? hi2 - lo2 : 0f;
            }
        }

        private float[] SmoothBands()
        {
            // Smoothed down the bands too, as the chord smooths its apex line: each band's samples wobble by a fraction of an
            // edge from row to row.
            var col = new float[bands];
            for (int q = 0; q < EnvelopeBins; q++)
            {
                for (int b = 0; b < bands; b++) col[b] = prof[b, q];
                for (int pass = 0; pass < BustBandSmoothing; pass++) Smooth1D(col, bands);
                for (int b = 0; b < bands; b++) prof[b, q] = col[b];
            }
            for (int pass = 0; pass < BustBandSmoothing; pass++) Smooth1D(bandRelief, bands);

            int flattened = 0;
            double askSum = 0, gotSum = 0, fadeSum = 0; int deep = 0;
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;
                int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
                if (!fitOk[b]) continue;

                // Only the outer surface: a node well behind its bin's frontmost is inside the fold, and moving it onto the
                // silhouette drags the invagination's walls out through the skin. Faded rather than cut, so no hard threshold
                // runs through the middle of the feature.
                int q = Bin(lat[n]);
                if (binMax[b, q] == float.MinValue) continue;

                // Both thresholds are measured against the band's own relief, not the region's width: width says nothing about
                // how deep the feature is, and a node may never travel further than the feature it is part of is tall.
                float relief = bandRelief[b];
                if (relief <= 1e-6f) continue;
                float behind = binMax[b, q] - h0[n];
                float skin = relief * EnvelopeSkin;
                if (behind >= skin) { deep++; continue; }
                float onSurface = 1f - Smoothstep(behind / skin);

                float want = Envelope(b, lat[n]);

                // Tapered across the corridor: the fold sits on the midline, and everything past its shoulders is inner thigh,
                // where a garment's trim runs. The region's own ramp (nW) fades from the outer boundary and is still saturated
                // here; this taper is the fold's own lateral scale.
                float taper = 1f - Smoothstep(Math.Clamp(MathF.Abs(lat[n] - midL) / (latW * 0.5f), 0f, 1f));
                float cap = relief * EnvelopeMaxMove;
                float move = Math.Clamp((want - h0[n]) * onSurface * taper, -cap, cap);
                target[n] = h0[n] + move;
                askSum += Math.Abs(want - h0[n]);
                gotSum += Math.Abs(target[n] - h0[n]);
                fadeSum += onSurface;
                flattened++;
            }

            double resid = 0; int rn = 0;
            for (int b = 0; b < bands; b++)
            {
                if (!fitOk[b]) continue;
                for (int q = 0; q < EnvelopeBins; q++)
                {
                    if (binMax[b, q] == float.MinValue) continue;
                    double d = binMax[b, q] - Envelope(b, binLat[b, q]);
                    resid += d * d; rn++;
                }
            }

            log?.Invoke($"crotch fold: {bands} band(s) of {bandH:0.#####}, {fitOk.Count(x => x)} fitted, "
                      + $"{flattened} node(s) flattened onto the envelope");
            if (flattened > 0)
                log?.Invoke($"crotch fold: ask {askSum / flattened * 1000:0.###}mm, applied "
                          + $"{gotSum / flattened * 1000:0.###}mm, mean fade {fadeSum / flattened:0.###}, "
                          + $"{deep} node(s) judged interior; fit leaves "
                          + $"{(rn > 0 ? Math.Sqrt(resid / rn) * 1000 : 0):0.###}mm rms on the envelope");
            return target;
        }

        private int Bin(float u) => Math.Clamp((int)((u - loL) / latW * EnvelopeBins), 0, EnvelopeBins - 1);

        // The smoothed profile read back at an arbitrary lateral position, linearly between bin centres.
        private float Envelope(int b, float u)
        {
            float t = (u - loL) / latW * EnvelopeBins - 0.5f;
            int q0 = (int)MathF.Floor(t);
            float f = t - q0;
            int qa = Math.Clamp(q0, 0, EnvelopeBins - 1);
            int qb = Math.Clamp(q0 + 1, 0, EnvelopeBins - 1);
            return prof[b, qa] + (prof[b, qb] - prof[b, qa]) * Math.Clamp(f, 0f, 1f);
        }
    }
}
