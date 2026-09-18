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
            private readonly ToeCapSolution solution;
            private readonly int c;
            private List<int> core = null!;
            private Vec3 mid;
            private Vec3? axis;
            private Vec3 eu;
            private Vec3 ev;
            private float lo;
            private float span;
            private List<(Vec3 A, Vec3 B, Vec3 C, Vec3 Out)> skinTris = null!;
            private List<int> loop = null!;
            private int rimCount;
            private List<List<int>> chain = null!;
            private List<float> chainAt = null!;
            private HashSet<int> taken = null!;
            private float edgeLen;
            private int ringCount;
            private float ringStep;
            private float[] rimAngle = null!;
            private int[] joinAt = null!;
            private (float X, float Y)[][] ringHull = null!;
            private (float X, float Y)[] ringCentre = null!;
            private float[] ringAt = null!;
            private float[] ringClear = null!;
            private bool[][] ringSpans = null!;
            private float domeTop;
            private Dictionary<int, int[]> nearSkin = null!;

            public FootCap(ToeCapSolution solution, int c)
            {
                this.solution = solution;
                this.c = c;
            }

            public void Run()
            {
                if (!FindCore()) return;
                if (!CutToeBox()) return;
                if (!SweepRings()) return;
                RoundTip();
                RelaxRings();
                StitchRings();
                SmoothJoin();
                CloseEnd();
            }

            private bool FindCore()
            {
                var masked = solution.maskedByComp[c];
                // Report every island the mask touches: a cut that silently declines an island looks exactly like the cut working.
                if (masked is { Count: > 0 })
                    solution.capLogSink?.Invoke($"toe cap: island {c} of {solution.islandSize[c]} node(s), {masked.Count} masked"
                        + (masked.Count < MinToeCapNodes ? $" — SKIPPED, under MinToeCapNodes ({MinToeCapNodes})" : ""));
                if (masked is not { Count: >= MinToeCapNodes }) return false;

                // The CORE of the mask — where it is actually painted in, not its antialiased fringe. A soft
                // The core of the mask, not its antialiased fringe: the fringe covers a lot of ground and would tilt
                // the axis. Only the core sets up the frame; the fringe still moves, by its own small weight.
                core = new List<int>();
                foreach (int n in masked)
                    if (solution.nW[n] >= ToeCapCoreWeight) core.Add(n);
                if (core.Count < MinToeCapNodes)
                {
                    solution.capLogSink?.Invoke($"toe cap: island {c} — SKIPPED, core {core.Count} of {masked.Count} "
                        + $"masked is under MinToeCapNodes ({MinToeCapNodes}); mask weight below "
                        + $"{ToeCapCoreWeight} does not count");
                    return false;
                }

                // An island that is entirely masked has no rim to sew to, so no cap can be built for it. Only a
                // small island is dropped, or a mask painted over a whole foot would swallow the foot.
                if (core.Count > MaxCoreFraction * solution.islandSize[c])
                {
                    solution.capLogSink?.Invoke($"toe cap: island {c} — SKIPPED, core {core.Count} is over "
                        + $"{MaxCoreFraction:P0} of the island's {solution.islandSize[c]} node(s)");
                    return false;   // marked by the pre-pass above
                }
                solution.capLogSink?.Invoke($"toe cap: island {c} — CUT, core {core.Count} of {solution.islandSize[c]} node(s)");

                // An authored cap fills this region, so only the cut is wanted.
                if (!solution.buildGeometry)
                {
                    foreach (int n in core) solution.cutNode[n] = true;
                    solution.capped = true;
                    return false;
                }

                float cx = 0, cy = 0, cz = 0, wsum = 0;
                foreach (int n in core)
                {
                    cx += solution.start[n].X * solution.nW[n]; cy += solution.start[n].Y * solution.nW[n]; cz += solution.start[n].Z * solution.nW[n];
                    wsum += solution.nW[n];
                }
                if (wsum <= 0f) return false;
                mid = new Vec3(cx / wsum, cy / wsum, cz / wsum);

                float ax = 0, ay = 0, az = 0;
                int all = 0;
                for (int n = 0; n < solution.nodeCount; n++)
                    if (solution.comp[n] == c) { ax += solution.start[n].X; ay += solution.start[n].Y; az += solution.start[n].Z; all++; }
                var islandMid = new Vec3(ax / all, ay / all, az / all);

                // A mask covering its whole island puts the two centres on top of each other and leaves no
                // direction; fall back to the region's longest extent, which for a foot is still its length.
                axis = Normalize(new Vec3(mid.X - islandMid.X, mid.Y - islandMid.Y, mid.Z - islandMid.Z))
                        ?? LongestExtent(solution.start, core);
                if (axis is null) return false;
                Basis(axis.Value, out eu, out ev);

                lo = float.MaxValue;
                float hi = float.MinValue;
                foreach (int n in core) { float t = Axial(solution.start[n]); lo = MathF.Min(lo, t); hi = MathF.Max(hi, t); }
                span = hi - lo;
                if (span <= 1e-6f) return false;
                return true;
            }

            private bool CutToeBox()
            {
                // ── the cut ────────────────────────────────────────────────────────────────────────────
                // Every triangle with a core corner leaves the mesh; the edges left used by only one of them form
                // the rim the cap is sewn onto.
                var inCut = new bool[solution.nodeCount];
                foreach (int n in core) inCut[n] = true;

                // Unmasked patches stranded inside the cut are absorbed into it, so exactly one rim is left to sew.
                var reached = new bool[solution.nodeCount];
                var patches = new List<List<int>>();
                var flood = new Stack<int>();
                for (int n = 0; n < solution.nodeCount; n++)
                {
                    if (solution.comp[n] != c || inCut[n] || reached[n]) continue;
                    var patch = new List<int>();
                    flood.Push(n);
                    reached[n] = true;
                    while (flood.Count > 0)
                    {
                        int q = flood.Pop();
                        patch.Add(q);
                        if (solution.adj[q] == null) continue;
                        foreach (int k in solution.adj[q])
                            if (solution.comp[k] == c && !inCut[k] && !reached[k]) { reached[k] = true; flood.Push(k); }
                    }
                    patches.Add(patch);
                }
                int mainPatch = 0;
                for (int i = 1; i < patches.Count; i++)
                    if (patches[i].Count > patches[mainPatch].Count) mainPatch = i;
                for (int i = 0; i < patches.Count; i++)
                {
                    if (i == mainPatch) continue;
                    foreach (int n in patches[i])
                    {
                        inCut[n] = true;
                        solution.nW[n] = 1f;       // fully inside the cap, so its normal is rebuilt with the rest
                        core.Add(n);      // and it joins the pool the rings draw their vertices from
                    }
                }

                // The skin the cap has to stay off: the replaced triangles at their original positions, oriented out
                // of the body by the corners' own normals, since index winding is not dependable here.
                skinTris = new List<(Vec3 A, Vec3 B, Vec3 C, Vec3 Out)>();

                var edgeUse = new Dictionary<(int, int), int>();
                for (int t = 0; t + 2 < solution.tris.Length; t += 3)
                {
                    if (solution.tris[t] >= solution.vc || solution.tris[t + 1] >= solution.vc || solution.tris[t + 2] >= solution.vc) continue;
                    int na = solution.nodeOf[solution.tris[t]], nb = solution.nodeOf[solution.tris[t + 1]], nc2 = solution.nodeOf[solution.tris[t + 2]];
                    if (solution.comp[na] != c) continue;
                    if (!inCut[na] && !inCut[nb] && !inCut[nc2]) continue;
                    {
                        Vec3 pa = solution.start[na], pb = solution.start[nb], pc = solution.start[nc2];
                        float ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
                        float wx = pc.X - pa.X, wy = pc.Y - pa.Y, wz = pc.Z - pa.Z;
                        var face = Normalize(new Vec3(uy * wz - uz * wy, uz * wx - ux * wz, ux * wy - uy * wx));
                        if (face is { } fn)
                        {
                            float agree = fn.X * (solution.nNorm[na].X + solution.nNorm[nb].X + solution.nNorm[nc2].X)
                                        + fn.Y * (solution.nNorm[na].Y + solution.nNorm[nb].Y + solution.nNorm[nc2].Y)
                                        + fn.Z * (solution.nNorm[na].Z + solution.nNorm[nb].Z + solution.nNorm[nc2].Z);
                            if (agree < 0) fn = new Vec3(-fn.X, -fn.Y, -fn.Z);
                            skinTris.Add((pa, pb, pc, fn));
                        }
                    }
                    foreach (var (p, q) in new[] { (na, nb), (nb, nc2), (nc2, na) })
                    {
                        var key = p < q ? (p, q) : (q, p);
                        edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                    }
                }

                // The swallowed islands (toenails) are skin the cap must close over, so they join the candidates
                // before the per-vertex shortlists below are built. They take no part in the rim.
                int islandObs = 0;
                for (int t = 0; t + 2 < solution.tris.Length; t += 3)
                {
                    if (solution.tris[t] >= solution.vc || solution.tris[t + 1] >= solution.vc || solution.tris[t + 2] >= solution.vc) continue;
                    int na2 = solution.nodeOf[solution.tris[t]], nb3 = solution.nodeOf[solution.tris[t + 1]], nc4 = solution.nodeOf[solution.tris[t + 2]];
                    if (!solution.dropNode[na2] || !solution.dropNode[nb3] || !solution.dropNode[nc4]) continue;
                    Vec3 pa2 = solution.start[na2], pb2 = solution.start[nb3], pc2 = solution.start[nc4];
                    float ux3 = pb2.X - pa2.X, uy3 = pb2.Y - pa2.Y, uz3 = pb2.Z - pa2.Z;
                    float wx3 = pc2.X - pa2.X, wy3 = pc2.Y - pa2.Y, wz3 = pc2.Z - pa2.Z;
                    if (Normalize(new Vec3(uy3 * wz3 - uz3 * wy3, uz3 * wx3 - ux3 * wz3, ux3 * wy3 - uy3 * wx3))
                        is not { } fn3) continue;
                    float agree2 = fn3.X * (solution.nNorm[na2].X + solution.nNorm[nb3].X + solution.nNorm[nc4].X)
                                 + fn3.Y * (solution.nNorm[na2].Y + solution.nNorm[nb3].Y + solution.nNorm[nc4].Y)
                                 + fn3.Z * (solution.nNorm[na2].Z + solution.nNorm[nb3].Z + solution.nNorm[nc4].Z);
                    if (agree2 < 0) fn3 = new Vec3(-fn3.X, -fn3.Y, -fn3.Z);
                    skinTris.Add((pa2, pb2, pc2, fn3));
                    islandObs++;
                }

                var rim = new Dictionary<int, List<int>>();
                foreach (var (e, uses) in edgeUse)
                {
                    if (uses != 1) continue;
                    (rim.TryGetValue(e.Item1, out var l1) ? l1 : rim[e.Item1] = new List<int>()).Add(e.Item2);
                    (rim.TryGetValue(e.Item2, out var l2) ? l2 : rim[e.Item2] = new List<int>()).Add(e.Item1);
                }
                loop = LongestLoop(rim);
                if (loop.Count < MinRimNodes) return false;

                // The walk gives the rim its true cyclic order; only rotate and orient it, never re-sort —
                // sorting by angle crosses the stitch and shreds the seam.
                OrientLoop(loop, solution.start, Flatten);
                rimCount = loop.Count;
                return true;
            }

            private bool SweepRings()
            {
                // ── the sweep ──────────────────────────────────────────────────────────────────────────
                // Rings of the cross-section outline, marching from the rim to the tip; each is sampled radially
                // off the slice's convex hull, so it bridges every toe in that slice by construction.
                chain = new List<List<int>> { loop };
                chainAt = new List<float> { lo };   // each ring's own axial position, domes included
                taken = new HashSet<int>(loop);
                edgeLen = MeanEdgeLength(solution.start, solution.adj, core);

                // The donor pool is finite, so budget rings for slack and leave room for the grid that closes the
                // end; the grid spans the last dome ring, not the rim.
                float lastShrink = MathF.Cos((float)TipRings / (TipRings + 1) * MathF.PI / 2f);
                int gridSide = Math.Max(2, (int)(rimCount * lastShrink) / 4);
                int gridCost = gridSide * gridSide;
                // And a ring no longer costs the rim's worth of donors either — it carries slots for its own
                // A ring costs donors for its own perimeter, not the rim's.
                int ringCost = Math.Max(1, (int)(rimCount * RingWidthEstimate));
                int affordable = (int)(core.Count * DonorBudget - gridCost) / ringCost - TipRings - 1;
                ringCount = Math.Clamp((int)MathF.Round(span / MathF.Max(edgeLen * RingDensity, 1e-6f)),
                                           MinRings, Math.Clamp(affordable, MinRings, MaxRings));
                ringStep = span / ringCount;

                // Rings start on the rim's own uneven angles and even out as they climb; both sequences increase,
                // so no blend of them can cross.
                rimAngle = new float[rimCount];
                for (int j = 0; j < rimCount; j++)
                {
                    var f = Flatten(solution.start[loop[j]]);
                    rimAngle[j] = MathF.Atan2(f.Y, f.X);
                }
                for (int j = 1; j < rimCount; j++)
                {
                    float a = rimAngle[j], prev = rimAngle[j - 1];
                    while (a - prev > MathF.PI) a -= MathF.Tau;
                    while (a - prev < -MathF.PI) a += MathF.Tau;
                    rimAngle[j] = a;
                }

                // The rim is nowhere near flat, so each rim slot waits at the rim until the rings have passed it;
                // slots join the sweep at different rings.
                joinAt = new int[rimCount];
                for (int j = 0; j < rimCount; j++)
                {
                    float rt = Axial(solution.start[loop[j]]);
                    joinAt[j] = ringCount + 1;
                    for (int r = 1; r <= ringCount; r++)
                        if (lo + ringStep * r > rt + ringStep * 0.5f) { joinAt[j] = r; break; }
                }

                // Kept so the relax below can push a vertex back out onto the outline it belongs on.
                ringHull = new (float X, float Y)[ringCount + 1][];
                ringCentre = new (float X, float Y)[ringCount + 1];
                ringAt = new float[ringCount + 1];
                ringClear = new float[ringCount + 1];
                ringSpans = new bool[ringCount + 1][];   // per slot: is this one bridging a gap?

                // Claim the last ring first: it decides whether the toe tips are enclosed.
                var tipRing = BuildRing(ringCount);
                for (int r = 1; r < ringCount; r++)
                {
                    var ring = BuildRing(r);
                    if (ring == null) break;
                    chain.Add(ring);
                    chainAt.Add(lo + ringStep * r);
                }
                if (tipRing != null) { chain.Add(tipRing); chainAt.Add(lo + ringStep * ringCount); }
                if (chain.Count < 2) return false;

                domeTop = float.NaN;   // where the rounded end finishes, for the patch that closes it
                return true;
            }

            private void RoundTip()
            {
                // Round the end off over a few shrinking rings before closing it; fanning the last ring straight
                // to a point makes a pole that shades badly.
                {
                    int lastFull = chain.Count - 1;
                    int rr = Math.Clamp(lastFull, 1, ringCount);
                    var lastCentre = ringCentre[rr];
                    float lastT = ringAt[rr];
                    float domeReach;

                    // Each dome ring carries slots in proportion to its own perimeter; its height is a fraction of
                    // the cap's own radius, not of the ring spacing.
                    float endRadius = 0;
                    {
                        int counted = 0;
                        foreach (int v in chain[lastFull])
                        {
                            if (v < 0) continue;
                            var f = Flatten(new Vec3(
                                solution.start[v].X + solution.target[v].X, solution.start[v].Y + solution.target[v].Y, solution.start[v].Z + solution.target[v].Z));
                            endRadius += MathF.Sqrt((f.X - lastCentre.X) * (f.X - lastCentre.X)
                                                  + (f.Y - lastCentre.Y) * (f.Y - lastCentre.Y));
                            counted++;
                        }
                        if (counted > 0) endRadius /= counted;
                    }
                    domeReach = endRadius * TipRound;
                    domeTop = lastT + domeReach;      // the crown of the quarter circle the rings follow

                    // The last full ring's outline as radius against angle, so a dome ring with a different slot
                    // count is sampled from the shape rather than decimated.
                    var prof = new List<(float A, float R)>(rimCount);
                    float perimeter = 0;
                    {
                        (float X, float Y)? first = null, prev = null;
                        foreach (int v in chain[lastFull])
                        {
                            if (v < 0) continue;
                            var f = Flatten(Placed(v));
                            float ox = f.X - lastCentre.X, oy = f.Y - lastCentre.Y;
                            float rad2 = MathF.Sqrt(ox * ox + oy * oy);
                            if (rad2 > 1e-9f) prof.Add((MathF.Atan2(oy, ox), rad2));
                            if (prev is { } pv)
                                perimeter += MathF.Sqrt((f.X - pv.X) * (f.X - pv.X) + (f.Y - pv.Y) * (f.Y - pv.Y));
                            else first = f;
                            prev = f;
                        }
                        if (first is { } fs && prev is { } lv)
                            perimeter += MathF.Sqrt((fs.X - lv.X) * (fs.X - lv.X) + (fs.Y - lv.Y) * (fs.Y - lv.Y));
                    }
                    if (prof.Count < 3) prof.Clear();
                    prof.Sort((u, v) => u.A.CompareTo(v.A));

                    float OutlineAt(float ang)
                    {
                        if (prof.Count == 0) return endRadius;
                        while (ang < prof[0].A) ang += MathF.Tau;
                        while (ang > prof[0].A + MathF.Tau) ang -= MathF.Tau;
                        for (int q = 0; q < prof.Count; q++)
                        {
                            var (a0, r0) = prof[q];
                            var (a1, r1) = q + 1 < prof.Count ? prof[q + 1] : (prof[0].A + MathF.Tau, prof[0].R);
                            if (ang >= a0 && ang <= a1)
                                return a1 - a0 <= 1e-9f ? r0 : r0 + (r1 - r0) * ((ang - a0) / (a1 - a0));
                        }
                        return prof[^1].R;
                    }

                    float phase = prof.Count > 0 ? prof[0].A : 0f;
                    int prevWidth = chain[lastFull].Count;

                    for (int k = 1; k <= TipRings; k++)
                    {
                        float frac = (float)k / (TipRings + 1);
                        float shrink = MathF.Cos(frac * MathF.PI / 2f);
                        float along = lastT + domeReach * MathF.Sin(frac * MathF.PI / 2f);

                        // Slots for this ring's perimeter: even, because the closing grid needs an even loop, and never
                        // wider than the ring before it.
                        int width2 = (int)MathF.Round(perimeter * shrink / MathF.Max(edgeLen, 1e-6f));
                        width2 = Math.Min(width2, prevWidth);
                        width2 -= width2 & 1;
                        if (width2 < MinDomeSlots) break;      // too narrow to be a ring; the patch closes it

                        var dome = new List<int>(width2);
                        var claimed = new List<int>(width2);
                        for (int j = 0; j < width2; j++)
                        {
                            float ang = phase + MathF.Tau * j / width2;
                            float rad2 = OutlineAt(ang) * shrink;
                            float qx = lastCentre.X + MathF.Cos(ang) * rad2;
                            float qy = lastCentre.Y + MathF.Sin(ang) * rad2;
                            var p = new Vec3(
                                mid.X + axis!.Value.X * along + eu.X * qx + ev.X * qy,
                                mid.Y + axis.Value.Y * along + eu.Y * qx + ev.Y * qy,
                                mid.Z + axis.Value.Z * along + eu.Z * qx + ev.Z * qy);

                            int donor = NearestFree(core, solution.start, taken, p);
                            if (donor < 0) break;
                            taken.Add(donor);
                            claimed.Add(donor);
                            solution.target[donor] = new Vec3(p.X - solution.start[donor].X, p.Y - solution.start[donor].Y, p.Z - solution.start[donor].Z);
                            solution.hasTarget[donor] = true;
                            // Whether a slot bridges is a property of the DIRECTION, so take it from the slot
                            // of the full ring pointing the same way.
                            int near = chain[lastFull][Math.Clamp(
                                (int)MathF.Round((float)j * prevWidth / width2), 0, prevWidth - 1)];
                            solution.nodeFill[donor] = near >= 0 ? solution.nodeFill[near] : 1f;
                            solution.fromRing[donor] = 1000 + k; solution.fromSlot[donor] = j;
                            dome.Add(donor);
                        }

                        if (dome.Count != width2)
                        {
                            foreach (int c2 in claimed) { taken.Remove(c2); solution.hasTarget[c2] = false; solution.target[c2] = default; }
                            break;
                        }
                        prevWidth = width2;
                        chain.Add(dome);
                        chainAt.Add(along);
                    }
                }
            }

            private void RelaxRings()
            {
                // ── the relax ──────────────────────────────────────────────────────────────────────────
                // Even the cap out by hand: pull each vertex toward its ring-grid neighbours, bounded by clearance
                // over the skin rather than by the hull. The rim and the tip are pinned.
                var anchor = new Vec3[solution.nodeCount];
                for (int r = 1; r < chain.Count; r++)
                    foreach (int n in chain[r])
                    {
                        if (n < 0) continue;
                        var p = Placed(n);
                        float bestD = float.MaxValue;
                        foreach (int m in core)
                        {
                            float dx = solution.start[m].X - p.X, dy = solution.start[m].Y - p.Y, dz = solution.start[m].Z - p.Z;
                            float d = dx * dx + dy * dy + dz * dz;
                            if (d < bestD) { bestD = d; anchor[n] = solution.start[m]; }
                        }
                    }
                float minClear = edgeLen * SkinClearance;

                // A shortlist of the skin under each cap vertex, so clearance can be enforced on every relax pass.
                nearSkin = new Dictionary<int, int[]>();
                if (skinTris.Count > 0)
                    for (int r = 1; r < chain.Count; r++)
                        foreach (int n in chain[r])
                        {
                            if (n < 0 || nearSkin.ContainsKey(n)) continue;
                            var p = Placed(n);
                            var order = new (float D, int I)[skinTris.Count];
                            for (int t = 0; t < skinTris.Count; t++)
                            {
                                var (ta, tb, tc, _) = skinTris[t];
                                float cx2 = (ta.X + tb.X + tc.X) / 3f - p.X;
                                float cy2 = (ta.Y + tb.Y + tc.Y) / 3f - p.Y;
                                float cz2 = (ta.Z + tb.Z + tc.Z) / 3f - p.Z;
                                order[t] = (cx2 * cx2 + cy2 * cy2 + cz2 * cz2, t);
                            }
                            Array.Sort(order, (x, y) => x.D.CompareTo(y.D));
                            int take = Math.Min(SkinCandidates, order.Length);
                            var pick = new int[take];
                            for (int k = 0; k < take; k++) pick[k] = order[k].I;
                            nearSkin[n] = pick;
                        }

                for (int pass = 0; pass < RelaxPasses; pass++)
                {
                    var moved = new List<(int Node, Vec3 To)>();
                    // Every ring relaxes, including the last: it cannot drift backwards because the step below
                    // holds each vertex in its own ring's plane.
                    for (int r = 1; r < chain.Count; r++)
                        for (int j = 0; j < chain[r].Count; j++)
                        {
                            int n = chain[r][j];
                            if (n < 0) continue;
                            int width = chain[r].Count;

                            float sx = 0, sy = 0, sz = 0;
                            int count = 0;
                            void Gather(int m)
                            {
                                if (m < 0) return;
                                var q = Placed(m);
                                sx += q.X; sy += q.Y; sz += q.Z; count++;
                            }
                            // Neighbours along the ring always exist; the ones fore and aft only when those
                            // rings carry the same number of slots, which the dome rings deliberately do not.
                            if (r - 1 == 0 || chain[r - 1].Count == width) Gather(At(r - 1, j));
                            if (r + 1 < chain.Count && chain[r + 1].Count == width) Gather(chain[r + 1][j]);
                            Gather(chain[r][(j + 1) % width]);
                            Gather(chain[r][(j - 1 + width) % width]);
                            if (count == 0) continue;

                            var p = Placed(n);
                            float nx = p.X + (sx / count - p.X) * RelaxRate;
                            float ny = p.Y + (sy / count - p.Y) * RelaxRate;
                            float nz = p.Z + (sz / count - p.Z) * RelaxRate;

                            // Held partly in its own ring's plane, otherwise free to settle; clearance over the skin is what
                            // must hold.
                            var rel = new Vec3(nx, ny, nz);
                            int rr = Math.Clamp(r, 1, ringCount);
                            if (ringHull[rr] != null)
                            {
                                var f2 = Flatten(rel);
                                // Its own ring's plane, not the last full ring's, or the dome folds inside out.
                                float t2 = chainAt[r];
                                var flat = new Vec3(
                                    mid.X + axis!.Value.X * t2 + eu.X * f2.X + ev.X * f2.Y,
                                    mid.Y + axis.Value.Y * t2 + eu.Y * f2.X + ev.Y * f2.Y,
                                    mid.Z + axis.Value.Z * t2 + eu.Z * f2.X + ev.Z * f2.Y);
                                rel = new Vec3(rel.X + (flat.X - rel.X) * RingPlaneHold,
                                               rel.Y + (flat.Y - rel.Y) * RingPlaneHold,
                                               rel.Z + (flat.Z - rel.Z) * RingPlaneHold);
                            }

                            // Free to settle inward — down into a toe gap is fine, and reads better than a
                            // flat bridge — but never through the skin.
                            float side = Clearance(n, rel, out var onSkin, out var outward);
                            if (side < minClear && side != float.MaxValue)
                                rel = new Vec3(
                                    onSkin.X + outward.X * minClear,
                                    onSkin.Y + outward.Y * minClear,
                                    onSkin.Z + outward.Z * minClear);
                            // ...and never floating far above it: capped inside the relax so later passes even the spacing
                            // out under the constraint.
                            else if (side != float.MaxValue && solution.nodeFill[n] < BridgeExempt)
                            {
                                float allow = edgeLen * MaxStandoff / (1f - solution.nodeFill[n]);
                                if (side > allow)
                                    rel = new Vec3(
                                        rel.X - outward.X * (side - allow),
                                        rel.Y - outward.Y * (side - allow),
                                        rel.Z - outward.Z * (side - allow));
                            }
                            // ...and never on top of the slot beside it: smoothing can close a pair in a toe valley to
                            // almost nothing.
                            float keep = edgeLen * SlotMinGap;
                            for (int nbSide = 0; nbSide < 2; nbSide++)
                            {
                                int nbIdx = chain[r][(j + (nbSide == 0 ? 1 : width - 1)) % width];
                                if (nbIdx < 0 || nbIdx == n) continue;
                                var np2 = Placed(nbIdx);
                                float ddx = rel.X - np2.X, ddy = rel.Y - np2.Y, ddz = rel.Z - np2.Z;
                                float dist = MathF.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                                if (dist >= keep || dist <= 1e-9f) continue;
                                float grow = (keep - dist) / dist;
                                rel = new Vec3(rel.X + ddx * grow, rel.Y + ddy * grow, rel.Z + ddz * grow);
                            }

                            moved.Add((n, rel));
                        }

                    foreach (var (n, to) in moved)
                        solution.target[n] = new Vec3(to.X - solution.start[n].X, to.Y - solution.start[n].Y, to.Z - solution.start[n].Z);
                }

                for (int r = 1; r < chain.Count; r++)
                    foreach (int n in chain[r])
                        PushOffSkin(n);
            }

            private void StitchRings()
            {
                // ── the stitch ─────────────────────────────────────────────────────────────────────────
                for (int r = 0; r + 1 < chain.Count; r++)
                {
                    int outer = r == 0 ? rimCount : chain[r].Count;
                    int inner = chain[r + 1].Count;

                    if (outer == inner)
                    {
                        for (int j = 0; j < outer; j++)
                        {
                            int k = (j + 1) % outer;
                            EmitQuad(At(r, j), At(r, k), At(r + 1, k), At(r + 1, j));
                        }
                    }
                    else
                    {
                        // Rings of different lengths are paired by index ratio: both are laid out in the same angular
                        // order from the same phase, so the reduction is spread evenly by construction.
                        int Inner(int i) => (int)MathF.Round((float)i * inner / outer) % inner;
                        for (int i = 0; i < outer; i++)
                        {
                            int o0 = At(r, i), o1 = At(r, (i + 1) % outer);
                            int b0 = Inner(i), b1 = Inner(i + 1);
                            if (b0 == b1)
                            {
                                Emit(o0, o1, chain[r + 1][b0]);
                            }
                            else
                            {
                                // The inner ring steps on here: one triangle to carry the outer edge, then a
                                // fan across however many inner slots this outer edge spans (normally one).
                                Emit(o0, o1, chain[r + 1][b1]);
                                for (int k = b0; k != b1; k = (k + 1) % inner)
                                    Emit(o0, chain[r + 1][(k + 1) % inner], chain[r + 1][k]);
                            }
                        }
                    }
                }
            }

            private void SmoothJoin()
            {
                // ── smooth where the cap meets the foot ────────────────────────────────────────────────
                // Relax the join in place over the few rings either side of it, against the triangles actually
                // emitted. The rim never moves: it is shared with the untouched shell.
                {
                    var joinAdj = new Dictionary<int, List<int>>();
                    var joinSeen = new HashSet<(int, int)>();
                    var joinSet = new HashSet<int>();
                    var joinMove = new HashSet<int>();
                    for (int r = 1; r < chain.Count && r <= RimRelaxRings; r++)
                        foreach (int n in chain[r])
                            if (n >= 0 && solution.hasTarget[n]) { joinSet.Add(n); joinMove.Add(n); }
                    // The rim and the ring beyond the band are the fixed edges this smooths between.
                    foreach (int v in loop) joinSet.Add(v);
                    if (RimRelaxRings + 1 < chain.Count)
                        foreach (int n in chain[RimRelaxRings + 1]) if (n >= 0) joinSet.Add(n);

                    void JoinLink(int a, int b)
                    {
                        if (a < 0 || b < 0 || a == b) return;
                        if (!joinSet.Contains(a) || !joinSet.Contains(b)) return;
                        if (!joinSeen.Add(a < b ? (a, b) : (b, a))) return;
                        (joinAdj.TryGetValue(a, out var la) ? la : joinAdj[a] = new List<int>()).Add(b);
                        (joinAdj.TryGetValue(b, out var lb) ? lb : joinAdj[b] = new List<int>()).Add(a);
                    }
                    foreach (var (ta, tb, tc) in solution.newTris)
                    {
                        int na = solution.nodeOf[ta], nb2 = solution.nodeOf[tb], nc3 = solution.nodeOf[tc];
                        JoinLink(na, nb2); JoinLink(nb2, nc3); JoinLink(nc3, na);
                    }

                    for (int pass = 0; pass < RelaxPasses; pass++)
                    {
                        var moved2 = new List<(int Node, Vec3 To)>();
                        foreach (int n in joinMove)
                        {
                            if (!joinAdj.TryGetValue(n, out var nb) || nb.Count == 0) continue;
                            float sx = 0, sy = 0, sz = 0;
                            foreach (int k in nb) { var q = Placed(k); sx += q.X; sy += q.Y; sz += q.Z; }
                            var p2 = Placed(n);
                            moved2.Add((n, new Vec3(
                                p2.X + (sx / nb.Count - p2.X) * RelaxRate,
                                p2.Y + (sy / nb.Count - p2.Y) * RelaxRate,
                                p2.Z + (sz / nb.Count - p2.Z) * RelaxRate)));
                        }
                        foreach (var (n, to) in moved2)
                            solution.target[n] = new Vec3(to.X - solution.start[n].X, to.Y - solution.start[n].Y, to.Z - solution.start[n].Z);
                        foreach (int n in joinMove) PushOffSkin(n);
                    }
                }
            }

            private void CloseEnd()
            {
                new EndGrid(this).Run();
            }

            private float Axial(Vec3 p) => (p.X - mid.X) * axis!.Value.X + (p.Y - mid.Y) * axis.Value.Y + (p.Z - mid.Z) * axis.Value.Z;

            private (float X, float Y) Flatten(Vec3 p)
            {
                var d = new Vec3(p.X - mid.X, p.Y - mid.Y, p.Z - mid.Z);
                return (d.X * eu.X + d.Y * eu.Y + d.Z * eu.Z, d.X * ev.X + d.Y * ev.Y + d.Z * ev.Z);
            }

            private List<int>? BuildRing(int r)
            {
                return new RingBuild(this, r).Run();
            }

            // A slot that has not joined yet is still the rim vertex, so a strip crossing the join is a
            // triangle rather than a quad and the degenerate halves fall away below.
            private int At(int level, int j)
            {
                if (level == 0) return loop[j];
                int v = chain[level][j];
                return v >= 0 ? v : loop[j];
            }

            private Vec3 Placed(int n) => new(solution.start[n].X + solution.target[n].X, solution.start[n].Y + solution.target[n].Y, solution.start[n].Z + solution.target[n].Z);

            // Signed distance out of the body, over that vertex's shortlist.
            private float Clearance(int n, Vec3 p, out Vec3 onSkin, out Vec3 outward)
            {
                onSkin = default; outward = default;
                if (!nearSkin.TryGetValue(n, out var cand)) return float.MaxValue;
                float bestD = float.MaxValue;
                foreach (int t in cand)
                {
                    var (ta, tb, tc, to) = skinTris[t];
                    var q = ClosestOnTriangle(p, ta, tb, tc);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; onSkin = q; outward = to; }
                }
                if (bestD == float.MaxValue) return float.MaxValue;
                return (p.X - onSkin.X) * outward.X + (p.Y - onSkin.Y) * outward.Y + (p.Z - onSkin.Z) * outward.Z;
            }

            // ── keep it off the skin ───────────────────────────────────────────────────────────────
            // The relax holds this against each vertex's shortlist; this sweep checks the whole surface,
            // against triangles rather than vertices.
            private void PushOffSkin(int n)
            {
                if (skinTris.Count == 0 || n < 0 || !solution.hasTarget[n]) return;
                var p = Placed(n);

                Vec3 best = default, bestOut = default;
                float bestD = float.MaxValue;
                foreach (var (ta, tb, tc, to) in skinTris)
                {
                    var q = ClosestOnTriangle(p, ta, tb, tc);
                    float dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    float d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; best = q; bestOut = to; }
                }
                if (bestD == float.MaxValue) return;

                // Signed, against the surface's outward direction: plain distance drives a vertex that is under
                // the skin further under.
                float want = edgeLen * SkinClearance;
                float side = (p.X - best.X) * bestOut.X + (p.Y - best.Y) * bestOut.Y + (p.Z - best.Z) * bestOut.Z;
                if (side >= want) return;

                solution.target[n] = new Vec3(
                    best.X + bestOut.X * want - solution.start[n].X,
                    best.Y + bestOut.Y * want - solution.start[n].Y,
                    best.Z + bestOut.Z * want - solution.start[n].Z);
            }

            // Which way is out, from the source normals rather than a radial from the axis, which means
            // nothing at the end of the cap.
            private float FacesOut(int i0, int i1, int i2)
            {
                if (i0 == i1 || i1 == i2 || i0 == i2) return float.MaxValue;   // degenerate: dropped anyway
                Vec3 p0 = Placed(i0), p1 = Placed(i1), p2 = Placed(i2);
                float ux = p1.X - p0.X, uy = p1.Y - p0.Y, uz = p1.Z - p0.Z;
                float wx = p2.X - p0.X, wy = p2.Y - p0.Y, wz = p2.Z - p0.Z;
                var nrmF = Normalize(new Vec3(uy * wz - uz * wy, uz * wx - ux * wz, ux * wy - uy * wx));
                if (nrmF is null) return float.MaxValue;

                var outward = Normalize(new Vec3(
                    solution.nNorm[i0].X + solution.nNorm[i1].X + solution.nNorm[i2].X,
                    solution.nNorm[i0].Y + solution.nNorm[i1].Y + solution.nNorm[i2].Y,
                    solution.nNorm[i0].Z + solution.nNorm[i1].Z + solution.nNorm[i2].Z));
                if (outward is null) return float.MaxValue;
                return nrmF.Value.X * outward.Value.X + nrmF.Value.Y * outward.Value.Y + nrmF.Value.Z * outward.Value.Z;
            }

            // Winding comes from the ring order and is never flipped per triangle: the vertex normals are
            // averaged from these faces.
            private void Emit(int i0, int i1, int i2)
            {
                // Tested on the vertices after merging: two welded nodes are still different nodes.
                ushort a = solution.Rep(i0), b = solution.Rep(i1), c = solution.Rep(i2);
                if (a == b || b == c || a == c) return;
                solution.newTris.Add((a, b, c));
            }

            // Split the quad on whichever diagonal keeps both halves facing out; a quad beside a slot still
            // waiting at the rim is a bowtie.
            private void EmitQuad(int a0, int a1, int b1, int b0)
            {
                float d1 = MathF.Min(FacesOut(a0, a1, b1), FacesOut(a0, b1, b0));
                float d2 = MathF.Min(FacesOut(a0, a1, b0), FacesOut(a1, b1, b0));
                if (d1 >= d2) { Emit(a0, a1, b1); Emit(a0, b1, b0); }
                else          { Emit(a0, a1, b0); Emit(a1, b1, b0); }
            }
        }
    }
}
