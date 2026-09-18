using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class ToeCapSolver
{
    private sealed partial class ToeCapSolution
    {
        private sealed partial class FootCap
        {
            private sealed class RingBuild
            {
                private readonly FootCap cap;
                private readonly int r;
                private float t;
                private List<(float X, float Y)> slicePts = null!;
                private (float X, float Y)[] hull = null!;
                private (float X, float Y) centre;
                private float clear;
                private int bins;
                private float[] binR = null!;
                private int width;
                private bool evenSlots;
                private (float X, float Y)[] dirs = null!;
                private float[] hullRad = null!;
                private float[] reach = null!;
                private float[] fill = null!;
                private List<int>? result;

                public RingBuild(FootCap cap, int r)
                {
                    this.cap = cap;
                    this.r = r;
                }

                public List<int>? Run()
                {
                    if (!SampleSlice()) return result;
                    if (!TraceOutline()) return result;
                    CountSlots();
                    PlaceSlots();
                    return ClaimDonors();
                }

                private bool SampleSlice()
                {
                    t = cap.lo + cap.ringStep * r;

                    // Read the cross-section from the narrowest band that still holds enough points, widening only
                    // where the geometry thins out.
                    slicePts = new List<(float X, float Y)>();
                    float band = MathF.Min(cap.ringStep, cap.edgeLen * SliceWindow);
                    for (int widen = 0; widen < BandWidenSteps; widen++)
                    {
                        slicePts.Clear();
                        for (int n = 0; n < cap.solution.nodeCount; n++)
                            if (cap.solution.comp[n] == cap.c && MathF.Abs(cap.Axial(cap.solution.start[n]) - t) <= band)
                                slicePts.Add(cap.Flatten(cap.solution.start[n]));
                        if (slicePts.Count >= MinSliceNodes) break;
                        band *= 1.8f;
                    }
                    if (slicePts.Count < MinSliceNodes) { result = null; return false; }

                    hull = ConvexHull(slicePts.ToArray());
                    if (hull.Length < 3) { result = null; return false; }

                    float hx = 0, hy = 0;
                    foreach (var h in hull) { hx += h.X; hy += h.Y; }
                    centre = (X: hx / hull.Length, Y: hy / hull.Length);

                    // Stand the cap off the hull a little, eased in from the rim so the join stays flush.
                    clear = 1f + CapClearance * MathF.Min(1f, r / 2f);
                    cap.ringHull[r] = hull;
                    cap.ringCentre[r] = centre;
                    cap.ringAt[r] = t;
                    cap.ringClear[r] = clear;
                    return true;
                }

                private bool TraceOutline()
                {
                    // The outline the slice points trace: bucket by angle about the centre, keep the farthest in each
                    // bucket, fill empty buckets by interpolation. Read before the slots are chosen, since the slot
                    // count depends on the ring's size.
                    bins = Math.Max(cap.rimCount, MinOutlineBins);
                    binR = new float[bins];
                    for (int b = 0; b < bins; b++) binR[b] = -1f;
                    int filled = 0;
                    foreach (var q in slicePts)
                    {
                        float ox = q.X - centre.X, oy = q.Y - centre.Y;
                        float len = MathF.Sqrt(ox * ox + oy * oy);
                        if (len <= 1e-9f) continue;
                        float ang0 = MathF.Atan2(oy, ox);
                        int b = (int)MathF.Floor((ang0 + MathF.PI) / MathF.Tau * bins);
                        b = Math.Clamp(b, 0, bins - 1);
                        if (binR[b] < 0f) filled++;
                        binR[b] = MathF.Max(binR[b], len);
                    }
                    if (filled < 3) { result = null; return false; }

                    // Circular gap fill: an empty bucket blends the nearest filled one either way.
                    if (filled < bins)
                    {
                        var back = new int[bins];
                        var fwd = new int[bins];
                        int last = -1;
                        for (int k = 0; k < bins * 2; k++) { int b = k % bins; if (binR[b] >= 0f) last = b; if (k >= bins) back[b] = last; }
                        last = -1;
                        for (int k = bins * 2 - 1; k >= 0; k--) { int b = k % bins; if (binR[b] >= 0f) last = b; if (k < bins) fwd[b] = last; }
                        var solid = (float[])binR.Clone();
                        for (int b = 0; b < bins; b++)
                        {
                            if (binR[b] >= 0f) continue;
                            int lb = back[b], rb = fwd[b];
                            int dl = (b - lb + bins) % bins, dr = (rb - b + bins) % bins;
                            solid[b] = dl + dr == 0 ? binR[lb] : binR[lb] + (binR[rb] - binR[lb]) * ((float)dl / (dl + dr));
                        }
                        binR = solid;
                    }
                    return true;
                }

                private void CountSlots()
                {
                    // How many slots this ring carries: its own perimeter at the mesh's edge length. The first ring
                    // keeps the rim's count, because joinAt only means anything while ring slot j is rim slot j.
                    float perim = 0f;
                    {
                        var prevP = (X: 0f, Y: 0f);
                        bool first = true;
                        var firstP = (X: 0f, Y: 0f);
                        for (int b = 0; b <= bins; b++)
                        {
                            float ang0 = -MathF.PI + MathF.Tau * (b % bins) / bins;
                            float rr0 = binR[b % bins];
                            var pt = (X: MathF.Cos(ang0) * rr0, Y: MathF.Sin(ang0) * rr0);
                            if (first) { firstP = pt; first = false; }
                            else perim += MathF.Sqrt((pt.X - prevP.X) * (pt.X - prevP.X) + (pt.Y - prevP.Y) * (pt.Y - prevP.Y));
                            prevP = pt;
                        }
                        perim += MathF.Sqrt((firstP.X - prevP.X) * (firstP.X - prevP.X) + (firstP.Y - prevP.Y) * (firstP.Y - prevP.Y));
                    }

                    width = cap.rimCount;
                    if (r > 1)
                    {
                        width = (int)MathF.Round(perim / MathF.Max(cap.edgeLen, 1e-6f));
                        width = Math.Clamp(width, Math.Min(MinRingSlots, cap.rimCount), cap.rimCount);
                    }
                    evenSlots = width != cap.rimCount;
                }

                private void PlaceSlots()
                {
                    // Which slots are bridging a gap and which lie on a toe; the relax lets a slot on a toe settle
                    // inward but never a bridging one.
                    dirs = new (float X, float Y)[width];
                    hullRad = new float[width];
                    reach = new float[width];
                    var ang = new float[width];
                    for (int j = 0; j < width; j++)
                        ang[j] = evenSlots
                            ? cap.rimAngle[0] + MathF.Tau * j / width          // its own even spacing
                            : cap.rimAngle[j] + (cap.rimAngle[0] + MathF.Tau * j / cap.rimCount - cap.rimAngle[j]) * ((float)r / cap.ringCount);

                    for (int j = 0; j < width; j++)
                    {
                        dirs[j] = (MathF.Cos(ang[j]), MathF.Sin(ang[j]));
                        hullRad[j] = HullRadius(hull, centre, dirs[j].X, dirs[j].Y);
                        reach[j] = OutlineRadius(ang[j]);
                    }

                    // Over the crown of a toe the hull is a chord to the next toe, so blend from where the skin is
                    // toward the hull by how much space the ray crosses.
                    float bridgeAt = cap.edgeLen * BridgeSpan;
                    fill = new float[width];
                    var spans = new bool[width];
                    for (int j = 0; j < width; j++)
                    {
                        // Nothing under this slot: it bridges. Without the guard the slot collapses to the centre.
                        if (reach[j] <= 0f) { fill[j] = 1f; spans[j] = true; continue; }
                        float u = Math.Clamp((hullRad[j] - reach[j]) / bridgeAt, 0f, 1f);
                        fill[j] = u * u * (3f - 2f * u);   // smoothstep: a hard cut steps where a toe ends
                        spans[j] = fill[j] > 0.5f;
                    }
                    cap.ringSpans[r] = spans;
                }

                private List<int>? ClaimDonors()
                {
                    // Donors claimed here are released if the ring turns out unusable, or nothing relaxes them or
                    // checks them against the skin.
                    var claimed = new List<int>(width);
                    void Abandon()
                    {
                        foreach (int taken2 in claimed)
                        {
                            cap.taken.Remove(taken2);
                            cap.solution.hasTarget[taken2] = false;
                            cap.solution.target[taken2] = default;
                        }
                    }

                    var ring = new List<int>(width);
                    for (int j = 0; j < width; j++)
                    {
                        // Only while this ring is still slot-for-slot with the rim does waiting mean anything.
                        if (!evenSlots && r < cap.joinAt[j]) { ring.Add(-1); continue; }

                        float dx = dirs[j].X, dy = dirs[j].Y;
                        float rad = (reach[j] + (hullRad[j] - reach[j]) * fill[j]) * clear;
                        float qx = centre.X + dx * rad, qy = centre.Y + dy * rad;
                        var p = new Vec3(
                            cap.mid.X + cap.axis!.Value.X * t + cap.eu.X * qx + cap.ev.X * qy,
                            cap.mid.Y + cap.axis.Value.Y * t + cap.eu.Y * qx + cap.ev.Y * qy,
                            cap.mid.Z + cap.axis.Value.Z * t + cap.eu.Z * qx + cap.ev.Z * qy);

                        // Reuse a vertex from the region: it keeps its own blend weights and bone window.
                        int donor = NearestFree(cap.core, cap.solution.start, cap.taken, p);
                        if (donor < 0) { Abandon(); return null; }   // pool exhausted; the caller stops here
                        cap.taken.Add(donor);
                        claimed.Add(donor);
                        cap.solution.target[donor] = new Vec3(p.X - cap.solution.start[donor].X, p.Y - cap.solution.start[donor].Y, p.Z - cap.solution.start[donor].Z);
                        cap.solution.hasTarget[donor] = true;
                        cap.solution.nodeFill[donor] = fill[j];
                        cap.solution.fromRing[donor] = r; cap.solution.fromSlot[donor] = j;
                        ring.Add(donor);
                    }
                    if (ring.Count == width) return ring;
                    Abandon();
                    return null;
                }

                private float OutlineRadius(float ang)
                {
                    float f2 = (ang + MathF.PI) / MathF.Tau * bins - 0.5f;
                    int b0 = (int)MathF.Floor(f2);
                    float w2 = f2 - b0;
                    return binR[((b0 % bins) + bins) % bins] * (1f - w2)
                         + binR[(((b0 + 1) % bins) + bins) % bins] * w2;
                }
            }
        }
    }
}
