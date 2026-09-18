using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    private sealed class ChordSolution
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
        private float midLat;
        private int bands;
        private float bandH;
        private float[] latL = null!;
        private float[] hL = null!;
        private float[] latR = null!;
        private float[] hR = null!;
        private bool[] have = null!;
        private int usable;
        private bool[] hole = null!;
        private float[] bandFade = null!;
        private float[] result = null!;

        public ChordSolution(float[] h0, float[] lat, float[] ver, float[] w, int count, List<int>[] adj, Vec3[] pos, Action<string>? log)
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
            FindApexes();
            if (!FindHoles()) return result;
            FadeBands();
            return SmoothApexLine();
        }

        private bool MeasureBands()
        {
            target = (float[])h0.Clone();

            loV = float.MaxValue;
            float hiV = float.MinValue;
            midLat = 0f;
            int n0 = 0;
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;
                loV = MathF.Min(loV, ver[n]); hiV = MathF.Max(hiV, ver[n]);
                midLat += lat[n];
                n0++;
            }
            if (n0 < 2) { result = target; return false; }
            midLat /= n0;

            // One band per edge of mesh, so a band is as fine as the geometry can express and no finer.
            float edge = 0f;
            int edges = 0;
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

        private void FindApexes()
        {
            // Each band's two apexes: the furthest-forward node either side of the chest's midline.
            latL = new float[bands]; hL = new float[bands];
            latR = new float[bands]; hR = new float[bands];
            have = new bool[bands];
            for (int b = 0; b < bands; b++) { hL[b] = float.MinValue; hR[b] = float.MinValue; }
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;
                int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
                if (lat[n] < midLat) { if (h0[n] > hL[b]) { hL[b] = h0[n]; latL[b] = lat[n]; } }
                else                 { if (h0[n] > hR[b]) { hR[b] = h0[n]; latR[b] = lat[n]; } }
            }
        }

        private bool FindHoles()
        {
            // Is there any surface between the two apexes? A cleft has a floor; a gap does not. Below the buttocks the body
            // becomes two legs, and a band there still has a furthest-back point per thigh; spanned, that is a skirt.
            var midHole = new float[bands];
            for (int b = 0; b < bands; b++) midHole[b] = float.MaxValue;
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;
                int b = Math.Clamp((int)((ver[n] - loV) / bandH), 0, bands - 1);
                if (hL[b] <= float.MinValue || hR[b] <= float.MinValue) continue;
                float mid = (latL[b] + latR[b]) * 0.5f;
                midHole[b] = MathF.Min(midHole[b], MathF.Abs(lat[n] - mid));
            }

            usable = 0;
            int holed = 0;
            hole = new bool[bands];
            for (int b = 0; b < bands; b++)
            {
                have[b] = hL[b] > float.MinValue && hR[b] > float.MinValue && latR[b] - latL[b] > 1e-6f;
                // Measured as a fraction of THIS band's own apex separation, so it means the same thing on a
                // cleavage and a cleft and needs no length in model units.
                if (have[b] && midHole[b] > (latR[b] - latL[b]) * ChordMidlineGap)
                { have[b] = false; hole[b] = true; holed++; }
                if (have[b]) usable++;
            }
            if (holed > 0)
                log?.Invoke($"bust bridge: {holed} band(s) have no surface between their two sides — a gap, "
                          + "not a cleft, and nothing to span there");
            if (usable == 0)
            {
                log?.Invoke("bust bridge: no band has a forward-most point on BOTH sides — nothing to span");
                { result = target; return false; }
            }
            return true;
        }

        private void FadeBands()
        {
            // A band with a lobe on only one side (the top and bottom of the region, where the cleft has run out) borrows
            // its neighbour's chord so the span tapers instead of ending in a step. A band with a hole does not: that is
            // open space, and borrowing there carries the span into the gap between the legs. A borrowed chord is faded by
            // how far it reached, to nothing past ChordBorrowBands: borrowing alone does not taper, since the same chord
            // sits further in front of a surface that has fallen away and the lift grows until the region weight cuts it off.
            bandFade = new float[bands];
            for (int b = 0; b < bands; b++)
            {
                if (have[b]) { bandFade[b] = 1f; continue; }
                if (hole[b]) continue;
                int near = -1, reach = 0;
                for (int d = 1; d < bands && near < 0; d++)
                {
                    if (b - d >= 0 && have[b - d]) { near = b - d; reach = d; }
                    else if (b + d < bands && have[b + d]) { near = b + d; reach = d; }
                }
                if (near < 0) continue;
                latL[b] = latL[near]; hL[b] = hL[near];
                latR[b] = latR[near]; hR[b] = hR[near];
                bandFade[b] = 1f - Smoothstep(Math.Clamp((reach - 1f) / ChordBorrowBands, 0f, 1f));
            }
        }

        private float[] SmoothApexLine()
        {
            // Smooth the apex line down each breast before spanning between them: each band's apex wobbles by a fraction
            // of an edge from band to band, which the chord amplifies across the cleavage. Safe to smooth downward too: a
            // node is only ever lifted, so a chord pulled under a real apex leaves that apex where it is.
            for (int pass = 0; pass < BustBandSmoothing; pass++)
            {
                Smooth1D(hL, bands); Smooth1D(latL, bands);
                Smooth1D(hR, bands); Smooth1D(latR, bands);
            }

            // The chord at one band, evaluated at a lateral position — or null outside the two apexes, which
            // is what keeps this a bridge BETWEEN the breasts rather than a flattening of the whole chest.
            float? Chord(int b, float u)
            {
                if (u <= latL[b] || u >= latR[b]) return null;
                float t = (u - latL[b]) / (latR[b] - latL[b]);
                return hL[b] + (hR[b] - hL[b]) * t;
            }

            int lifted = 0;
            for (int n = 0; n < count; n++)
            {
                if (w[n] <= 0f) continue;

                // Between the two nearest band CENTRES, so the surface is ruled between rows rather than
                // stepped at their boundaries.
                float f = (ver[n] - loV) / bandH - 0.5f;
                int b0 = Math.Clamp((int)MathF.Floor(f), 0, bands - 1);
                int b1 = Math.Clamp(b0 + 1, 0, bands - 1);
                float mix = Math.Clamp(f - b0, 0f, 1f);

                var c0 = Chord(b0, lat[n]);
                var c1 = Chord(b1, lat[n]);
                // A band with no chord at this lateral position (past its own apexes) contributes the node's own height, and the
                // blend fades toward that. Handing the blend to whichever band still has a chord steps at band granularity.
                float want = (c0, c1) switch
                {
                    ({ } a, { } b) => a + (b - a) * mix,
                    ({ } a, null)  => a + (h0[n] - a) * mix,
                    (null, { } b)  => h0[n] + (b - h0[n]) * mix,
                    _              => float.MinValue,
                };
                // No chord in either band: guarded on its own, because the sentinel is float.MinValue and the clamp below
                // would swallow it only by accident.
                if (c0 == null && c1 == null) continue;

                // Faded by how far each band had to reach for its chord, blended between the two the same way
                // the heights are. This is what makes the span die out at the top and bottom of the cleft
                // instead of holding a borrowed lift right up to the region's edge.
                float fade = bandFade[b0] + (bandFade[b1] - bandFade[b0]) * mix;
                if (fade <= 0f) continue;
                want = h0[n] + (want - h0[n]) * fade;

                // Raise-only, and that clamp is the no-clip guarantee: a node only ever leaves the skin. A pass that needs to
                // lower wants EnvelopeTarget instead.
                if (want <= h0[n]) continue;
                target[n] = want;
                lifted++;
            }

            log?.Invoke($"bust bridge: {bands} band(s) of {bandH:0.#####} ({usable} with a lobe either side), "
                      + $"{lifted} node(s) lifted to the chord");
            return target;
        }
    }
}
