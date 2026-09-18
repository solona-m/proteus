using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    private sealed partial class CapPlacementFromBind
    {
        private sealed partial class CapMeshPlacement
        {
            private readonly CapPlacementFromBind placement;
            private int mesh;
            private int vc;
            private Vec3[] pos = null!;
            private Vec3[] nrm = null!;
            private (float U, float V)[] uvs = null!;
            private (string Bone, float W)[][] wts = null!;
            private bool[] found = null!;
            private int missed;
            private int sampled;
            private int[] socketOf = null!;
            private List<(float U, float V, float R)> discs2 = null!;

            public CapMeshPlacement(CapPlacementFromBind placement)
            {
                this.placement = placement;
            }

            public void Run()
            {
                if (!ProjectVertices()) return;
                FindSockets();
                FillMisses();
                KeepCapShape();
            }

            private bool ProjectVertices()
            {
                mesh = placement.r.ReadInt32();
                vc = placement.r.ReadInt32();
                pos = new Vec3[vc];
                nrm = new Vec3[vc];
                uvs = new (float U, float V)[vc];
                wts = new (string Bone, float W)[vc][];
                found = new bool[vc];
                missed = 0;
                sampled = 0;

                for (int i = 0; i < vc; i++)
                {
                    float u = placement.r.ReadSingle(), v = placement.r.ReadSingle(), off = placement.r.ReadSingle();
                    int side = placement.r.ReadInt32();
                    var face = new Vec3(placement.r.ReadSingle(), placement.r.ReadSingle(), placement.r.ReadSingle());
                    // Version 2 carries the reconstruction residual in the landing's tangent frame; without it the
                    // round trip is out most over the toenails, where the cap stands furthest off the skin.
                    float rt = 0f, rb = 0f, rn = 0f;
                    if (placement.version >= 2) { rt = placement.r.ReadSingle(); rb = placement.r.ReadSingle(); rn = placement.r.ReadSingle(); }
                    uvs[i] = (u, v);
                    wts[i] = [];
                    if (placement.stride > 1 && i % placement.stride != 0) continue;
                    sampled++;

                    float best = ResolveBindLanding(placement.tris, u, v, side, face,
                                                    out var at, out var n2, out var w2, out var tan, out var bit);
                    if (best < float.MaxValue)
                    {
                        pos[i] = new Vec3(at.X + n2.X * off + tan.X * rt + bit.X * rb + n2.X * rn,
                                          at.Y + n2.Y * off + tan.Y * rt + bit.Y * rb + n2.Y * rn,
                                          at.Z + n2.Z * off + tan.Z * rt + bit.Z * rb + n2.Z * rn);
                        nrm[i] = n2;
                        wts[i] = w2;
                    }
                    found[i] = best <= CapBindMissTolerance;
                    if (!found[i]) missed++;
                }

                // Scoring only wants the hit rate; reported against what was actually looked at.
                if (placement.stride > 1)
                {
                    placement.outp.Add(new CapPlacement
                    {
                        Mesh = mesh, Pos = pos, Nrm = nrm, Uv = uvs, W = wts,
                        Missed = missed, Considered = sampled,
                    });
                    return false;
                }
                return true;
            }

            private void FindSockets()
            {
                // Over a nail socket, keep the authored shape. Only on the full placement: inflating the scoring
                // probe's miss count would make a cap look unplaceable.
                socketOf = new int[vc];
                Array.Fill(socketOf, -1);
                discs2 = NailSocketDiscs(placement.allTris);
                {
                    var discs = discs2;
                    int over = 0;
                    for (int i = 0; i < vc && discs.Count > 0; i++)
                    {
                        if (!found[i]) continue;
                        var (u3, v3) = uvs[i];
                        // Which socket, and the nearest one when discs overlap: one socket, one transform.
                        float bestD = float.MaxValue;
                        for (int k = 0; k < discs.Count; k++)
                        {
                            var (du, dv, rr) = discs[k];
                            float ddu = u3 - du, ddv = v3 - dv;
                            float dd = ddu * ddu + ddv * ddv;
                            if (dd > rr * rr || dd >= bestD) continue;
                            bestD = dd; socketOf[i] = k;
                        }
                        if (socketOf[i] < 0) continue;
                        found[i] = false; missed++; over++;
                    }
                    if (over > 0)
                        placement.diag?.Invoke($"cap bind: {over} vertex/vertices sit over a hole in the skin where a "
                                   + "toenail is carried on its own mesh - their landings are measured off "
                                   + "the rim of the hole and cannot be trusted");
                }
            }

            private void FillMisses()
            {
                new CapMissFill(this).Run();
            }

            private void KeepCapShape()
            {
                // Keep the cap's own shape, follow the body only in the large. Each vertex above is reconstructed
                // on its own, and on a foot the binding was not measured against neighbours disagree by a little.
                // Laplacian surface editing (Sorkine et al., 2004): hold the cap's authored differential
                // coordinates and treat the landings as a soft constraint,
                //
                //     x_i  <-  ( sum_j x_j  +  deg_i * delta_i  +  w * p_i ) / (deg_i + w)
                //
                // solved by relaxation. It moves the reference body's cap slightly too, so the round trip is not an
                // exact zero. The cap's boundary is pinned hard to meet the shell, and a vertex that never landed
                // is left to its neighbours (w = 0).
                if (placement.capMdl != null && CapShapePasses > 0)
                {
                    try
                    {
                        var shapeSrc = ParseCached(placement.capMdl);
                        ReadCapVertices(shapeSrc, mesh, out var authored, out _);
                        var shapeTri = CapTriangles(shapeSrc, mesh, (ushort)vc);
                        if (authored.Length >= vc && shapeTri.Count >= 3)
                        {
                            var nb = new List<int>[vc];
                            var edge = new Dictionary<(int, int), int>();
                            for (int t = 0; t + 2 < shapeTri.Count; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    int a = shapeTri[t + k], b = shapeTri[t + (k + 1) % 3];
                                    if (a >= vc || b >= vc) continue;
                                    (nb[a] ??= []).Add(b);
                                    (nb[b] ??= []).Add(a);
                                    var e = (Math.Min(a, b), Math.Max(a, b));
                                    edge[e] = edge.GetValueOrDefault(e) + 1;
                                }
                            // The cap's open boundary: the rim that welds to the shell.
                            var onRim = new bool[vc];
                            foreach (var (e, n) in edge)
                                if (n == 1) { onRim[e.Item1] = true; onRim[e.Item2] = true; }

                            // The authored differential coordinate - what "round" actually means here.
                            var delta = new Vec3[vc];
                            for (int i = 0; i < vc; i++)
                            {
                                if (nb[i] is not { Count: > 0 }) continue;
                                Vec3 sum = default;
                                foreach (int j in nb[i]) sum = new Vec3(sum.X + authored[j].X,
                                                                        sum.Y + authored[j].Y,
                                                                        sum.Z + authored[j].Z);
                                float inv = 1f / nb[i].Count;
                                delta[i] = new Vec3(authored[i].X - sum.X * inv,
                                                    authored[i].Y - sum.Y * inv,
                                                    authored[i].Z - sum.Z * inv);
                            }

                            var cur = (Vec3[])pos.Clone();
                            var nxt = new Vec3[vc];
                            for (int pass = 0; pass < CapShapePasses; pass++)
                            {
                                Array.Copy(cur, nxt, vc);
                                for (int i = 0; i < vc; i++)
                                {
                                    if (nb[i] is not { Count: > 0 }) continue;
                                    if (onRim[i]) continue;                  // pinned to meet the shell
                                    Vec3 sum = default;
                                    foreach (int j in nb[i])
                                        sum = new Vec3(sum.X + cur[j].X, sum.Y + cur[j].Y, sum.Z + cur[j].Z);
                                    int deg = nb[i].Count;
                                    float w = found[i] ? CapShapeFollow : 0f;
                                    nxt[i] = new Vec3(
                                        (sum.X + deg * delta[i].X + w * pos[i].X) / (deg + w),
                                        (sum.Y + deg * delta[i].Y + w * pos[i].Y) / (deg + w),
                                        (sum.Z + deg * delta[i].Z + w * pos[i].Z) / (deg + w));
                                }
                                (cur, nxt) = (nxt, cur);
                            }

                            int shaped = 0;
                            float worst = 0f, tot = 0f;
                            for (int i = 0; i < vc; i++)
                            {
                                if (nb[i] is not { Count: > 0 } || onRim[i]) continue;
                                float d = Dist(cur[i], pos[i]);
                                if (d <= 1e-7f) continue;
                                // It may smooth, but it may not lift off the foot: bounding the distance moved does not fix that,
                                // so the move is walked back until the vertex sits no further from the skin than its landing did.
                                if (NearestOnSkin(pos[i], placement.tris, CapStandoffReach, out var hadAt))
                                {
                                    float had = Dist(pos[i], hadAt);
                                    for (int back = 0; back < CapShapeBackoffSteps; back++)
                                    {
                                        if (!NearestOnSkin(cur[i], placement.tris, CapStandoffReach, out var nowAt)
                                            || Dist(cur[i], nowAt) <= had + CapShapeLiftAllow) break;
                                        cur[i] = new Vec3((cur[i].X + pos[i].X) * 0.5f,
                                                          (cur[i].Y + pos[i].Y) * 0.5f,
                                                          (cur[i].Z + pos[i].Z) * 0.5f);
                                    }
                                    d = Dist(cur[i], pos[i]);
                                    if (d <= 1e-7f) continue;
                                }
                                pos[i] = cur[i];
                                tot += d; worst = MathF.Max(worst, d); shaped++;
                            }
                            if (shaped > 0)
                            {
                                placement.diag?.Invoke($"cap bind: reshaped {shaped} vertices to hold the cap's own "
                                           + $"curvature while following the body (mean {tot / shaped:F5}, "
                                           + $"furthest {worst:F5}, {CapShapePasses} passes at w={CapShapeFollow})");
                                // Everything now has a position, from its neighbours if not from a landing.
                                for (int i = 0; i < vc; i++)
                                    if (!found[i] && nb[i] is { Count: > 0 }) { found[i] = true; missed--; }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        placement.diag?.Invoke($"cap bind: could not hold the cap's shape ({ex.Message})");
                    }
                }

                placement.outp.Add(new CapPlacement
                {
                    Mesh = mesh, Pos = pos, Nrm = nrm, Uv = uvs, W = wts,
                    Missed = missed, Considered = sampled,
                });
                {
                    float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                    foreach (var (u2, v2) in uvs)
                    { u0 = MathF.Min(u0, u2); u1 = MathF.Max(u1, u2); v0 = MathF.Min(v0, v2); v1 = MathF.Max(v1, v2); }
                    placement.diag?.Invoke($"cap bind: mesh {mesh} placed {vc} vertices on the equipped body"
                               + (missed > 0 ? $", {missed} outside its atlas" : "")
                               + $"; the cap wants u {u0:F3}..{u1:F3} v {v0:F3}..{v1:F3}");
                }
            }
        }
    }
}
