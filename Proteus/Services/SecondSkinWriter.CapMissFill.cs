using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    private sealed partial class CapPlacementFromBind
    {
        private sealed partial class CapMeshPlacement
        {
            private sealed class CapMissFill
            {
                private readonly CapMeshPlacement meshPlacement;
                private Vec3[] asAuthored = null!;
                private Vec3[] authoredNrm = null!;
                private List<int>[] near2 = null!;
                private int fitted;
                private int patches;
                private int clipped;
                private float medianSocketR;
                private float worstMove;
                private int filled;

                public CapMissFill(CapMeshPlacement meshPlacement)
                {
                    this.meshPlacement = meshPlacement;
                }

                public void Run()
                {
                    if (meshPlacement.missed > 0 && meshPlacement.placement.capMdl != null)
                    {
                        try
                        {
                            ReadAuthoredCap();
                            FitSocketPatches();
                            RelaxPatches();
                            FillRemaining();
                        }
                        catch (Exception ex)
                        {
                            meshPlacement.placement.diag?.Invoke($"cap bind: could not fill unplaced vertices ({ex.Message})");
                        }
                    }
                }

                private void ReadAuthoredCap()
                {
                    var capSrc2 = ParseCached(meshPlacement.placement.capMdl!);
                    ReadCapVertices(capSrc2, meshPlacement.mesh, out asAuthored, out authoredNrm);
                    var tri2 = CapTriangles(capSrc2, meshPlacement.mesh, (ushort)meshPlacement.vc);
                    near2 = new List<int>[meshPlacement.vc];
                    for (int i = 0; i < meshPlacement.vc; i++) near2[i] = [];
                    for (int t = 0; t + 2 < tri2.Count; t += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            int a = tri2[t + k], b = tri2[t + (k + 1) % 3];
                            if (a < meshPlacement.vc && b < meshPlacement.vc) { near2[a].Add(b); near2[b].Add(a); }
                        }
                }

                private void FitSocketPatches()
                {
                    // A socket patch moves as one piece: the cap is very nearly rigid over a nail, so take the
                    // rotation and translation that carry the authored cap onto where it landed around the patch
                    // and move the whole patch by it.
                    fitted = 0;
                    patches = 0;
                    clipped = 0;
                    medianSocketR = 0f;
                    worstMove = 0f;
                    {
                        var bySocket = new Dictionary<int, List<int>>();
                        for (int i = 0; i < meshPlacement.vc; i++)
                        {
                            if (meshPlacement.socketOf[i] < 0) continue;
                            (bySocket.TryGetValue(meshPlacement.socketOf[i], out var l) ? l
                                : bySocket[meshPlacement.socketOf[i]] = []).Add(i);
                        }
                        // Sized against the sockets the cap actually sits over, not every hole in the body.
                        if (bySocket.Count > 0)
                        {
                            var radii = bySocket.Keys.Select(k => meshPlacement.discs2[k].R).OrderBy(x => x).ToArray();
                            medianSocketR = radii[radii.Length / 2];
                        }
                        foreach (var members in bySocket.Values)
                        {
                            patches++;

                            // Anchors: the landed vertices nearest the patch, found by walking outwards in rings, so a
                            // small toe and the big toe are answered at their own scale.
                            var anchors = new List<int>();
                            var seenA = new HashSet<int>(members);
                            var frontier = new List<int>(members);
                            for (int ring = 0; ring < CapFitAnchorRings && anchors.Count < CapFitMinAnchors; ring++)
                            {
                                var nextF = new List<int>();
                                foreach (int x in frontier)
                                    foreach (int y in near2[x])
                                    {
                                        if (!seenA.Add(y)) continue;
                                        nextF.Add(y);
                                        if (meshPlacement.found[y] && meshPlacement.socketOf[y] < 0 && y < asAuthored.Length) anchors.Add(y);
                                    }
                                if (nextF.Count == 0) break;
                                frontier = nextF;
                            }
                            if (anchors.Count < CapFitMinAnchors) continue;

                            Vec3 ca = default, cp = default;
                            foreach (int j in anchors)
                            {
                                ca = new Vec3(ca.X + asAuthored[j].X, ca.Y + asAuthored[j].Y,
                                              ca.Z + asAuthored[j].Z);
                                cp = new Vec3(cp.X + meshPlacement.pos[j].X, cp.Y + meshPlacement.pos[j].Y, cp.Z + meshPlacement.pos[j].Z);
                            }
                            float invA = 1f / anchors.Count;
                            ca = new Vec3(ca.X * invA, ca.Y * invA, ca.Z * invA);
                            cp = new Vec3(cp.X * invA, cp.Y * invA, cp.Z * invA);

                            var fromP = anchors.Select(j => asAuthored[j]).ToList();
                            var toP = anchors.Select(j => meshPlacement.pos[j]).ToList();
                            var rot = BestRotation(fromP, toP, ca, cp);
                            Vec3 Apply(Vec3 q)
                            {
                                float ax = q.X - ca.X, ay = q.Y - ca.Y, az = q.Z - ca.Z;
                                return new Vec3(cp.X + rot[0] * ax + rot[1] * ay + rot[2] * az,
                                                cp.Y + rot[3] * ax + rot[4] * ay + rot[5] * az,
                                                cp.Z + rot[6] * ax + rot[7] * ay + rot[8] * az);
                            }
                            Vec3 Turn(Vec3 q) =>
                                new(rot[0] * q.X + rot[1] * q.Y + rot[2] * q.Z,
                                    rot[3] * q.X + rot[4] * q.Y + rot[5] * q.Z,
                                    rot[6] * q.X + rot[7] * q.Y + rot[8] * q.Z);

                            // Faded out at the edge of the patch: full weight over the middle of the nail, none at the rim,
                            // so the fit and the resolved landing agree where they meet.
                            var (cu, cv, cr) = meshPlacement.discs2[meshPlacement.socketOf[members[0]]];

                            // The bound scales with the socket, measured against the median so only the larger get more.
                            float bound = CapFitMaxMove;
                            if (medianSocketR > 1e-9f)
                                bound = Math.Clamp(CapFitMaxMove * (cr / medianSocketR),
                                                   CapFitMaxMove, CapFitMaxMove * CapFitMoveScaleMax);
                            foreach (int x in members)
                            {
                                if (x >= asAuthored.Length) continue;
                                var was = meshPlacement.pos[x];
                                var want = Apply(asAuthored[x]);

                                float du2 = meshPlacement.uvs[x].U - cu, dv2 = meshPlacement.uvs[x].V - cv;
                                float rel = cr > 1e-9f ? MathF.Sqrt(du2 * du2 + dv2 * dv2) / cr : 1f;
                                // Full weight over the nail itself; the fade is kept to the outer band.
                                float t2 = (1f - rel) / MathF.Max(1e-6f, 1f - CapFitFeatherStart);
                                float wgt = Math.Clamp(t2, 0f, 1f);
                                wgt *= wgt * (3f - 2f * wgt);          // smoothstep: flat at both ends

                                float mx = want.X - was.X, my = want.Y - was.Y, mz = want.Z - was.Z;
                                mx *= wgt; my *= wgt; mz *= wgt;
                                float len = MathF.Sqrt(mx * mx + my * my + mz * mz);
                                if (len > bound)
                                {
                                    float k2 = bound / len;
                                    mx *= k2; my *= k2; mz *= k2;
                                    clipped++;
                                }
                                meshPlacement.pos[x] = new Vec3(was.X + mx, was.Y + my, was.Z + mz);
                                if (x < authoredNrm.Length && wgt > 0.5f)
                                    meshPlacement.nrm[x] = NormalizeOr(Turn(authoredNrm[x]), meshPlacement.nrm[x]);
                                meshPlacement.found[x] = true;      // settled; the ring fill below is for atlas gaps
                                meshPlacement.missed--;
                                fitted++;
                                worstMove = MathF.Max(worstMove, MathF.Sqrt(mx * mx + my * my + mz * mz));
                            }
                        }
                    }
                }

                private void RelaxPatches()
                {
                    // Relax the patches: Laplacian smoothing over each patch's interior, the vertices around it held
                    // fixed, bounded against the fitted position so it cannot flatten the nail back into the dish.
                    if (fitted > 0 && CapRelaxPasses > 0)
                    {
                        var anchorPos = new Vec3[meshPlacement.vc];
                        for (int i = 0; i < meshPlacement.vc; i++) anchorPos[i] = meshPlacement.pos[i];
                        var next = new Vec3[meshPlacement.vc];
                        for (int pass = 0; pass < CapRelaxPasses; pass++)
                        {
                            for (int i = 0; i < meshPlacement.vc; i++) next[i] = meshPlacement.pos[i];
                            for (int i = 0; i < meshPlacement.vc; i++)
                            {
                                if (meshPlacement.socketOf[i] < 0 || near2[i].Count == 0) continue;
                                Vec3 sum = default;
                                int c2 = 0;
                                foreach (int j in near2[i])
                                {
                                    sum = new Vec3(sum.X + meshPlacement.pos[j].X, sum.Y + meshPlacement.pos[j].Y, sum.Z + meshPlacement.pos[j].Z);
                                    c2++;
                                }
                                if (c2 == 0) continue;
                                float invC = 1f / c2;
                                var avg = new Vec3(sum.X * invC, sum.Y * invC, sum.Z * invC);
                                var to = new Vec3(meshPlacement.pos[i].X + (avg.X - meshPlacement.pos[i].X) * CapRelaxWeight,
                                                  meshPlacement.pos[i].Y + (avg.Y - meshPlacement.pos[i].Y) * CapRelaxWeight,
                                                  meshPlacement.pos[i].Z + (avg.Z - meshPlacement.pos[i].Z) * CapRelaxWeight);
                                // Never further from where the fit put it than this.
                                float dx2 = to.X - anchorPos[i].X, dy2 = to.Y - anchorPos[i].Y,
                                      dz2 = to.Z - anchorPos[i].Z;
                                float dl = MathF.Sqrt(dx2 * dx2 + dy2 * dy2 + dz2 * dz2);
                                // Sized per socket, as the height bound is; every socket at or under the median keeps the drift it has.
                                float drift = CapRelaxMaxDrift;
                                if (medianSocketR > 1e-9f)
                                {
                                    // Squared, not linear: a crease runs the length of a nail while the drift bound is a distance,
                                    // so the room a patch needs grows faster than its radius.
                                    float ratio = meshPlacement.discs2[meshPlacement.socketOf[i]].R / medianSocketR;
                                    drift = Math.Clamp(CapRelaxMaxDrift * ratio * ratio,
                                                       CapRelaxMaxDrift,
                                                       CapRelaxMaxDrift * CapRelaxDriftScaleMax);
                                }
                                if (dl > drift)
                                {
                                    float k3 = drift / dl;
                                    to = new Vec3(anchorPos[i].X + dx2 * k3, anchorPos[i].Y + dy2 * k3,
                                                  anchorPos[i].Z + dz2 * k3);
                                }
                                next[i] = to;
                            }
                            (meshPlacement.pos, next) = (next, meshPlacement.pos);
                        }
                        float worstDrift = 0f;
                        for (int i = 0; i < meshPlacement.vc; i++)
                            if (meshPlacement.socketOf[i] >= 0) worstDrift = MathF.Max(worstDrift, Dist(meshPlacement.pos[i], anchorPos[i]));
                        meshPlacement.placement.diag?.Invoke($"cap bind: relaxed the socket patches over {CapRelaxPasses} pass(es), "
                                   + $"furthest a vertex settled {worstDrift:F5}");
                    }

                    if (fitted > 0)
                        meshPlacement.placement.diag?.Invoke($"cap bind: {fitted} vertex/vertices over {patches} toenail socket(s) "
                                   + "placed by fitting the authored cap onto where it landed around "
                                   + $"them, furthest moved {worstMove:F5}"
                                   + (clipped > 0 ? $", {clipped} held back at the bound" : ""));

                    filled = 0;
                }

                private void FillRemaining()
                {
                    for (int pass = 0; pass < CapBindFillPasses; pass++)
                    {
                        int did = 0;
                        for (int i = 0; i < meshPlacement.vc; i++)
                        {
                            if (meshPlacement.found[i]) continue;
                            // Each neighbour votes by carrying its own move and keeping the authored gap; averaging positions
                            // outright collapses two adjacent stragglers to the same point.
                            Vec3 sp = default, sn = default;
                            int c = 0;
                            foreach (int j in near2[i])
                            {
                                if (!meshPlacement.found[j]) continue;
                                var keep = i < asAuthored.Length && j < asAuthored.Length
                                    ? new Vec3(asAuthored[i].X - asAuthored[j].X,
                                               asAuthored[i].Y - asAuthored[j].Y,
                                               asAuthored[i].Z - asAuthored[j].Z)
                                    : default;
                                sp = new Vec3(sp.X + meshPlacement.pos[j].X + keep.X, sp.Y + meshPlacement.pos[j].Y + keep.Y,
                                              sp.Z + meshPlacement.pos[j].Z + keep.Z);
                                sn = new Vec3(sn.X + meshPlacement.nrm[j].X, sn.Y + meshPlacement.nrm[j].Y, sn.Z + meshPlacement.nrm[j].Z);
                                c++;
                            }
                            if (c == 0) continue;
                            meshPlacement.pos[i] = new Vec3(sp.X / c, sp.Y / c, sp.Z / c);
                            meshPlacement.nrm[i] = NormalizeOr(sn, meshPlacement.nrm[i]);
                            did++;
                        }
                        if (did == 0) break;
                        for (int i = 0; i < meshPlacement.vc; i++) if (!meshPlacement.found[i] && near2[i].Any(j => meshPlacement.found[j])) meshPlacement.found[i] = true;
                        filled += did;
                    }
                    meshPlacement.placement.diag?.Invoke($"cap bind: {filled} of {meshPlacement.missed} unplaced vertices filled from their neighbours");
                }
            }
        }
    }
}
