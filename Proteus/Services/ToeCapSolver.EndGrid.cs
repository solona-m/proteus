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
            private sealed class EndGrid
            {
                private readonly FootCap cap;
                private int sideA;
                private int sideB;
                private int[,] gridV = null!;
                private bool ok;

                public EndGrid(FootCap cap)
                {
                    this.cap = cap;
                }

                public void Run()
                {
                    // ── close the end with a grid ──────────────────────────────────────────────────────────
                    // Not a fan to a single apex, which makes a pole that shades badly: an even quad grid spanning
                    // the opening (a Coons patch), then domed.
                    int last = cap.chain.Count - 1;
                    var rim2 = new List<int>();
                    for (int j = 0; j < cap.chain[last].Count; j++)
                    {
                        int v = cap.At(last, j);
                        if (rim2.Count == 0 || v != rim2[^1]) rim2.Add(v);
                    }
                    if (rim2.Count >= 8 && rim2.Count % 2 == 0)
                    {
                        PlaceGrid(last, rim2);
                        EmitGrid();
                    }

                    foreach (int n in cap.core) cap.solution.cutNode[n] = true;
                    cap.solution.capped = true;
                }

                private void PlaceGrid(int last, List<int> rim2)
                {
                    int n2 = rim2.Count;
                    sideA = n2 / 4;
                    sideB = n2 / 2 - sideA;      // the loop as four sides: a, b, a, b

                    (float U, float V) Flat2(int v) => cap.Flatten(cap.Placed(v));
                    int Ring(int t) => rim2[((t % n2) + n2) % n2];

                    // Corner-to-corner walk: grid[i,j], i across side A, j across side B.
                    gridV = new int[sideA + 1, sideB + 1];
                    for (int i = 0; i <= sideA; i++) gridV[i, 0] = Ring(i);
                    for (int j = 0; j <= sideB; j++) gridV[sideA, j] = Ring(sideA + j);
                    for (int i = 0; i <= sideA; i++) gridV[sideA - i, sideB] = Ring(sideA + sideB + i);
                    for (int j = 0; j <= sideB; j++) gridV[0, sideB - j] = Ring(2 * sideA + sideB + j);

                    var p00 = Flat2(gridV[0, 0]); var p10 = Flat2(gridV[sideA, 0]);
                    var p01 = Flat2(gridV[0, sideB]); var p11 = Flat2(gridV[sideA, sideB]);
                    // The patch sits on the last ring the cap actually has, a dome ring; clamping to the full rings
                    // caves the end in.
                    float domeAt = cap.chainAt[last];
                    float domeUp = float.IsNaN(cap.domeTop)
                        ? MathF.Max(cap.ringStep, cap.edgeLen) * TipRound
                        : MathF.Max(cap.domeTop - domeAt, 0f);

                    ok = true;
                    for (int i = 1; i < sideA && ok; i++)
                        for (int j = 1; j < sideB && ok; j++)
                        {
                            float u = (float)i / sideA, v2 = (float)j / sideB;
                            var a0 = Flat2(gridV[i, 0]); var a1 = Flat2(gridV[i, sideB]);
                            var b0 = Flat2(gridV[0, j]); var b1 = Flat2(gridV[sideA, j]);

                            // Coons: the two rulings, less the bilinear corner sheet they share.
                            float qx = (1 - v2) * a0.U + v2 * a1.U + (1 - u) * b0.U + u * b1.U
                                     - ((1 - u) * (1 - v2) * p00.U + u * (1 - v2) * p10.U
                                      + (1 - u) * v2 * p01.U + u * v2 * p11.U);
                            float qy = (1 - v2) * a0.V + v2 * a1.V + (1 - u) * b0.V + u * b1.V
                                     - ((1 - u) * (1 - v2) * p00.V + u * (1 - v2) * p10.V
                                      + (1 - u) * v2 * p01.V + u * v2 * p11.V);

                            // Lift it into a dome: zero at the edges, most in the middle.
                            float lift = domeUp * MathF.Sin(u * MathF.PI) * MathF.Sin(v2 * MathF.PI);
                            float t4 = domeAt + lift;
                            var p = new Vec3(
                                cap.mid.X + cap.axis!.Value.X * t4 + cap.eu.X * qx + cap.ev.X * qy,
                                cap.mid.Y + cap.axis.Value.Y * t4 + cap.eu.Y * qx + cap.ev.Y * qy,
                                cap.mid.Z + cap.axis.Value.Z * t4 + cap.eu.Z * qx + cap.ev.Z * qy);

                            int donor = NearestFree(cap.core, cap.solution.start, cap.taken, p);
                            if (donor < 0) { ok = false; break; }
                            cap.taken.Add(donor);
                            cap.solution.target[donor] = new Vec3(p.X - cap.solution.start[donor].X, p.Y - cap.solution.start[donor].Y, p.Z - cap.solution.start[donor].Z);
                            cap.solution.hasTarget[donor] = true;
                            cap.solution.fromRing[donor] = 2000; cap.solution.fromSlot[donor] = i * 1000 + j;
                            gridV[i, j] = donor;

                            // Made after the relax has run, so checked against the skin here.
                            cap.PushOffSkin(donor);
                        }
                }

                private void EmitGrid()
                {
                    if (ok)
                    {
                        // The patch's winding is settled by the faces it joins, not by the source normals: two faces
                        // sharing an edge must run it in opposite directions.
                        var sewn = new HashSet<(ushort, ushort)>();
                        foreach (var (ta, tb, tc) in cap.solution.newTris)
                        {
                            sewn.Add((ta, tb)); sewn.Add((tb, tc)); sewn.Add((tc, ta));
                        }
                        int agrees = 0;
                        for (int i = 0; i < sideA; i++)
                            for (int j = 0; j < sideB; j++)
                                foreach (var (u, v) in new[]
                                         {
                                             (gridV[i, j], gridV[i + 1, j]),
                                             (gridV[i + 1, j], gridV[i + 1, j + 1]),
                                             (gridV[i + 1, j + 1], gridV[i, j + 1]),
                                             (gridV[i, j + 1], gridV[i, j]),
                                         })
                                {
                                    if (sewn.Contains((cap.solution.Rep(u), cap.solution.Rep(v)))) agrees--;   // same way round: wrong
                                    if (sewn.Contains((cap.solution.Rep(v), cap.solution.Rep(u)))) agrees++;   // opposite: right
                                }
                        bool flip = agrees < 0;

                        for (int i = 0; i < sideA; i++)
                            for (int j = 0; j < sideB; j++)
                            {
                                if (flip)
                                {
                                    cap.Emit(gridV[i, j], gridV[i + 1, j + 1], gridV[i + 1, j]);
                                    cap.Emit(gridV[i, j], gridV[i, j + 1], gridV[i + 1, j + 1]);
                                }
                                else
                                {
                                    cap.Emit(gridV[i, j], gridV[i + 1, j], gridV[i + 1, j + 1]);
                                    cap.Emit(gridV[i, j], gridV[i + 1, j + 1], gridV[i, j + 1]);
                                }
                            }

                        // ── smooth the end ─────────────────────────────────────────────────────────
                        // The patch was built after the relax, so nothing has smoothed it yet; same rate and clearance
                        // floor as the main relax, over the end only, with the ring below pinned.
                        int endFrom = Math.Max(1, cap.chain.Count - TipRings - 1 - TipRelaxSpan);
                        var endSet = new HashSet<int>();
                        var movable = new HashSet<int>();
                        for (int r = endFrom; r < cap.chain.Count; r++)
                            foreach (int n in cap.chain[r])
                            {
                                if (n < 0) continue;
                                endSet.Add(n);
                                if (r > endFrom) movable.Add(n);   // the ring below is the pinned boundary
                            }
                        for (int i = 0; i <= sideA; i++)
                            for (int j = 0; j <= sideB; j++)
                            {
                                endSet.Add(gridV[i, j]);
                                if (i > 0 && i < sideA && j > 0 && j < sideB) movable.Add(gridV[i, j]);
                            }
                        endSet.Remove(-1);
                        movable.Remove(-1);

                        // Neighbours read off the triangles actually emitted, not the grid and ring structure: zipping
                        // unequal rings makes edges the structure knows nothing about.
                        var endAdj = new Dictionary<int, List<int>>();
                        var seenEdge = new HashSet<(int, int)>();
                        void Join(int a, int b)
                        {
                            if (a < 0 || b < 0 || a == b) return;
                            if (!endSet.Contains(a) || !endSet.Contains(b)) return;
                            if (!seenEdge.Add(a < b ? (a, b) : (b, a))) return;
                            (endAdj.TryGetValue(a, out var la) ? la : endAdj[a] = new List<int>()).Add(b);
                            (endAdj.TryGetValue(b, out var lb) ? lb : endAdj[b] = new List<int>()).Add(a);
                        }
                        foreach (var (ta, tb, tc) in cap.solution.newTris)
                        {
                            int na = cap.solution.nodeOf[ta], nb2 = cap.solution.nodeOf[tb], nc3 = cap.solution.nodeOf[tc];
                            Join(na, nb2); Join(nb2, nc3); Join(nc3, na);
                        }

                        for (int pass = 0; pass < TipRelaxPasses; pass++)
                        {
                            var endMoved = new List<(int Node, Vec3 To)>();
                            foreach (int n in movable)
                            {
                                if (!cap.solution.hasTarget[n] || !endAdj.TryGetValue(n, out var nb) || nb.Count == 0) continue;
                                float sx = 0, sy = 0, sz = 0;
                                foreach (int k in nb) { var q = cap.Placed(k); sx += q.X; sy += q.Y; sz += q.Z; }
                                var p = cap.Placed(n);
                                endMoved.Add((n, new Vec3(
                                    p.X + (sx / nb.Count - p.X) * RelaxRate,
                                    p.Y + (sy / nb.Count - p.Y) * RelaxRate,
                                    p.Z + (sz / nb.Count - p.Z) * RelaxRate)));
                            }
                            foreach (var (n, to) in endMoved)
                                cap.solution.target[n] = new Vec3(to.X - cap.solution.start[n].X, to.Y - cap.solution.start[n].Y, to.Z - cap.solution.start[n].Z);
                            foreach (int n in movable) cap.PushOffSkin(n);
                        }
                    }
                }
            }
        }
    }
}
