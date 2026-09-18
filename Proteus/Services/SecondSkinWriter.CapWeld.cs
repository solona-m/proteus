using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    private sealed partial class ShellBuild
    {
        private sealed partial class MeshEmitter
        {
            private sealed partial class CapWeld
            {
                private readonly MeshEmitter emitter;
                private int stride;
                private bool[] movedV = null!;
                private List<(int From, int To)> joinAdded = null!;
                private Vec3[] weldPos = null!;
                private Vec3[] weldNrm = null!;
                private (string Bone, float W)[][] weldWgt = null!;
                private List<ushort> shellTbl = null!;
                private Dictionary<string, int> shellSlot = null!;
                private int reweighted;
                private int uvFixed;
                private int fromCapRim;

                public CapWeld(MeshEmitter emitter)
                {
                    this.emitter = emitter;
                }

                public void Run()
                {
                    // ── weld the cut lip onto the cap's rim ───────────────────────────────────────────────────
                    // The cut is decided in texture space, so its edge lands wherever the texels fall. Each lip vertex
                    // slides onto the nearest point of a rim SEGMENT, not the nearest rim vertex: the loops have different
                    // vertex counts and vertex-to-vertex pairing would tear the triangles between them.
                    if (emitter.build.weldRim is { Length: > 0 } wr && !emitter.preserve)
                    {
                        VElem? pEl2 = null, nEl2 = null, wEl3 = null, iEl3 = null, uEl0 = null, uEl1 = null;
                        foreach (var el in emitter.decl)
                        {
                            if (el.Usage == UsePosition) pEl2 ??= el;
                            if (el.Usage == UseNormal) nEl2 ??= el;
                            if (el.Usage == UseBlendWeight) wEl3 ??= el;
                            if (el.Usage == UseBlendIndices) iEl3 ??= el;
                            if (el.Usage == UseUV) { if (el.UsageIndex == 0) uEl0 ??= el; else uEl1 ??= el; }
                        }
                        if (pEl2 is { } pw2)
                        {
                            PrepareWeld(pw2);
                            WeldLipToRim(wr, nEl2, wEl3, iEl3, uEl0, uEl1, pw2);
                            RampToCap(nEl2, pw2);
                            MatchCapRim(wr, pw2);
                            UnifyJoinNormals(nEl2, uEl0, uEl1, pw2);
                            GraftCap(pw2);
                            RefitCappedLayers(pw2);
                            WeldCracks(pEl2, uEl0);
                            RaisePokeThrough(pEl2);
                            CollectBodySolid();
                            ClearBodySolid(nEl2, pw2);
                        }
                    }
                }

                private void PrepareWeld(VElem pw2)
                {
                    stride = emitter.outStrides[pw2.Stream];
                    movedV = new bool[emitter.vc];
                    // Index ranges inserted ON the join after movedV was sized (lip split, rim stitch); the graft's own
                    // vertices are not the join.
                    joinAdded = new List<(int From, int To)>();
                    weldPos = new Vec3[emitter.vc];
                    weldNrm = new Vec3[emitter.vc];
                    weldWgt = new (string Bone, float W)[emitter.vc][];
                    for (int i = 0; i < emitter.vc; i++) weldWgt[i] = [];

                    // The shell's bone table, grown on demand so a welded vertex can be given the cap's
                    // skinning even when that names a bone this mesh never used.
                    shellTbl = new List<ushort>();
                    shellSlot = new Dictionary<string, int>();
                    {
                        var st0 = emitter.srcBoneTbl < emitter.src.BoneTables.Length ? emitter.src.BoneTables[emitter.srcBoneTbl] : [];
                        foreach (var bi in st0)
                        {
                            var nmB = bi < emitter.src.BoneNames.Length ? emitter.src.BoneNames[bi] : null;
                            if (nmB != null && emitter.build.boneIndex.TryGetValue(nmB, out var ui4))
                            { shellSlot.TryAdd(nmB, shellTbl.Count); shellTbl.Add(ui4); }
                            else shellTbl.Add(0);
                        }
                    }
                    reweighted = 0;
                    uvFixed = 0;
                    fromCapRim = 0;
                }

                private void WeldLipToRim(RimSeg[] wr, VElem? nEl2, VElem? wEl3, VElem? iEl3, VElem? uEl0, VElem? uEl1, VElem pw2)
                {
                    // Outside both loops below: cleared at each use.
                    Span<float> tmpW = stackalloc float[4];
                    Span<byte> wb3 = stackalloc byte[8], ib3 = stackalloc byte[8];

                    // More than one round: dropping a collapsed triangle EXPOSES vertices that were interior when the
                    // boundary was found.
                    for (int round = 0; round < WeldRounds; round++)
                    {
                        // The lip is the shell's open boundary, whatever made it — not only what the toe-cap cut removed.
                        var edgeUses = new Dictionary<(ushort, ushort), int>();
                        foreach (var sub in emitter.keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    var e = (Math.Min(x, y), Math.Max(x, y));
                                    edgeUses[e] = edgeUses.GetValueOrDefault(e) + 1;
                                }
                        var onBoundary = new bool[emitter.vc];
                        foreach (var (e, n) in edgeUses)
                            if (n == 1) { onBoundary[e.Item1] = true; onBoundary[e.Item2] = true; }

                        int movedThisRound = 0;
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            if (!emitter.used[i] || !onBoundary[i] || movedV[i]) continue;
                            int po = i * stride + pw2.Offset;
                            ReadTyped(emitter.outStreams[pw2.Stream], po, pw2.Type, tmpW);
                            var p = new Vec3(tmpW[0], tmpW[1], tmpW[2]);

                            // How far a vertex may be dragged depends on WHY it is on a boundary: the toe-cap cut deliberately
                            // overshoots the cap and the weld closes the difference, so a cut vertex gets the long reach. Every
                            // other boundary keeps the short leash. Misses are counted on the last round only.
                            float reach = emitter.cutAway[i] ? WeldCutReach : WeldRadius;
                            if (!NearestOnRim(p, wr, reach, out var best, out var capN, out var capW,
                                              out float dist))
                            { if (round == WeldRounds - 1) emitter.build.weldWorst++; continue; }
                            WriteXYZ(emitter.outStreams[pw2.Stream], po, pw2.Type, best.X, best.Y, best.Z);
                            movedV[i] = true;
                            weldPos[i] = best;
                            emitter.build.welded++;
                            movedThisRound++;
                            emitter.build.weldWorstD = MathF.Max(emitter.build.weldWorstD, dist);

                            // THE SKINNING MOVES WITH THE VERTEX, and it takes the cap's: `capW` is the cap's skinning interpolated
                            // along the segment at the same parameter as the position. Anything else welds in bind pose only.
                            emitter.build.bodySkin ??= CollectSkinTriangles(emitter.build.sourceModels);
                            if (capW.Length > 0) fromCapRim++;

                            // ...and its UV, or two coincident vertices read different texels and draw a line along the join.
                            // Applied as a DELTA between two body lookups, because the mesh's [0,1] tile shift is not known here.
                            if (uEl0 is { } ue4 && i < emitter.uv.Length)
                            {
                                var uvWas = NearestUv(p, emitter.build.bodySkin, WeldRadius);
                                var uvNow = NearestUv(best, emitter.build.bodySkin, WeldRadius);
                                if (uvWas is { } w0 && uvNow is { } w1)
                                {
                                    var moved = (U: emitter.uv[i].U + (w1.U - w0.U), V: emitter.uv[i].V + (w1.V - w0.V));
                                    emitter.uv[i] = moved;
                                    bool half4 = ue4.Type is 13 or 14;
                                    int so4 = i * emitter.outStrides[ue4.Stream];
                                    WriteUV2(emitter.outStreams[ue4.Stream], so4 + ue4.Offset, half4, moved.U, moved.V);
                                    if (ue4.Type is 3 or 14)
                                        WriteUV2(emitter.outStreams[ue4.Stream],
                                                 so4 + ue4.Offset + (ue4.Type == 3 ? 8 : 4),
                                                 ue4.Type == 14, moved.U, moved.V);
                                    if (uEl1 is { } ue5)
                                        WriteUV2(emitter.outStreams[ue5.Stream],
                                                 i * emitter.outStrides[ue5.Stream] + ue5.Offset,
                                                 ue5.Type is 13 or 14, moved.U, moved.V);
                                    uvFixed++;
                                }
                            }

                            weldWgt[i] = capW;
                            if (wEl3 is { } we6 && iEl3 is { } ie6 && capW.Length > 0)
                            {
                                // Write every influence the element declares; leaving the rest was what over-weighted the join.
                                int nInf3 = BlendCount(we6.Type);
                                wb3.Clear(); ib3.Clear();
                                int used3 = 0, total3 = 0;
                                foreach (var (bone, f) in capW)
                                {
                                    if (used3 == nInf3) break;
                                    if (!shellSlot.TryGetValue(bone, out int at3))
                                    {
                                        if (!emitter.build.boneIndex.TryGetValue(bone, out var ui5)) continue;
                                        if (shellTbl.Count >= 255) continue;
                                        shellSlot[bone] = at3 = shellTbl.Count;
                                        shellTbl.Add(ui5);
                                    }
                                    byte q3 = (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
                                    if (q3 == 0) continue;
                                    ib3[used3] = (byte)at3; wb3[used3] = q3; total3 += q3;
                                    used3++;
                                }
                                if (used3 > 0)
                                {
                                    wb3[0] = (byte)Math.Clamp(wb3[0] + (255 - total3), 0, 255);
                                    int wo3 = i * emitter.outStrides[we6.Stream] + we6.Offset;
                                    int io3 = i * emitter.outStrides[ie6.Stream] + ie6.Offset;
                                    for (int q4 = 0; q4 < nInf3; q4++)
                                    {
                                        emitter.outStreams[we6.Stream][wo3 + q4] = wb3[q4];
                                        emitter.outStreams[ie6.Stream][io3 + q4] = ib3[q4];
                                    }
                                    reweighted++;
                                }
                            }

                            if (nEl2 is not { } ne4) continue;
                            int no = i * emitter.outStrides[ne4.Stream] + ne4.Offset;
                            ReadTyped(emitter.outStreams[ne4.Stream], no, ne4.Type, tmpW);
                            float sx = tmpW[0], sy = tmpW[1], sz = tmpW[2];
                            if (ne4.Type == 8) { sx = sx * 2 - 1; sy = sy * 2 - 1; sz = sz * 2 - 1; }
                            var own = NormalizeOr(new Vec3(sx, sy, sz), capN);
                            // Kept BEFORE averaging: the cap averages against the shell's own normal, not one with the cap folded in.
                            weldNrm[i] = own;
                            var avg = NormalizeOr(new Vec3(own.X + capN.X, own.Y + capN.Y, own.Z + capN.Z), own);
                            WriteNormal(emitter.outStreams[ne4.Stream], no, ne4.Type, avg.X, avg.Y, avg.Z);
                        }
                        if (movedThisRound == 0) break;

                        var wp = new Vec3[emitter.vc];
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            ReadTyped(emitter.outStreams[pw2.Stream], i * stride + pw2.Offset, pw2.Type, tmpW);
                            wp[i] = new Vec3(tmpW[0], tmpW[1], tmpW[2]);
                        }

                        // Sliding the lip flattens a few triangles behind it; the cap covers where they were, so they go.
                        var lens = new List<float>();
                        foreach (var sub in emitter.keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                    lens.Add(Dist(wp[sub[t + k]], wp[sub[t + (k + 1) % 3]]));
                        if (lens.Count > 0)
                        {
                            lens.Sort();
                            // BY AREA, not by shortest edge: a long thin triangle still covers the sliver it stands on.
                            float med = lens[lens.Count / 2];
                            float floorArea = med * med * WeldCollapse * WeldCollapse;
                            int dropped = 0;
                            for (int su = 0; su < emitter.keptPerSub.Count; su++)
                            {
                                var sub = emitter.keptPerSub[su];
                                var trimmed = new List<ushort>(sub.Length);
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    ushort a3 = sub[t], b3 = sub[t + 1], c3 = sub[t + 2];
                                    bool touched = movedV[a3] || movedV[b3] || movedV[c3];
                                    if (touched && TriArea(wp[a3], wp[b3], wp[c3]) < floorArea)
                                    { emitter.build.triOut--; dropped++; continue; }
                                    trimmed.Add(a3); trimmed.Add(b3); trimmed.Add(c3);
                                }
                                emitter.keptPerSub[su] = trimmed.ToArray();
                            }
                            if (dropped > 0)
                                emitter.build.diag?.Invoke($"authored cap: {dropped} collapsed triangle(s) dropped at the join");
                        }
                    }
                }

                private void RampToCap(VElem? nEl2, VElem pw2)
                {
                    // ── RAMP THE SHELL UP TO THE CAP ─────────────────────────────────────────────────
                    // The cap stands further off the skin than the shell; the weld leaves that whole step to the ONE row of
                    // triangles behind the lip, which shades as a hard line. The cap is not lowered (its toe clearance is
                    // small); the step is spread over a band behind the lip instead.
                    if (nEl2 is { } ne6 && emitter.build.weldRim is { Length: > 0 } wr3 && emitter.build.welded > 0 && CapFeather > 0f)
                    {
                        Span<float> tmpF = stackalloc float[4];
                        int ramped = 0;
                        float worstLift = 0f;
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            if (!emitter.used[i] || movedV[i]) continue;   // the lip itself is already on the rim
                            int po3 = i * emitter.outStrides[pw2.Stream] + pw2.Offset;
                            ReadTyped(emitter.outStreams[pw2.Stream], po3, pw2.Type, tmpF);
                            var p4 = new Vec3(tmpF[0], tmpF[1], tmpF[2]);
                            if (!NearestOnRim(p4, wr3, CapFeather, out var onRim, out _, out _, out float dRim))
                                continue;

                            ReadTyped(emitter.outStreams[ne6.Stream], i * emitter.outStrides[ne6.Stream] + ne6.Offset,
                                      ne6.Type, tmpF);
                            float nx2 = tmpF[0], ny2 = tmpF[1], nz2 = tmpF[2];
                            if (ne6.Type == 8) { nx2 = nx2 * 2 - 1; ny2 = ny2 * 2 - 1; nz2 = nz2 * 2 - 1; }
                            var n4 = NormalizeOr(new Vec3(nx2, ny2, nz2), default);
                            if (n4 is { X: 0, Y: 0, Z: 0 }) continue;

                            // How much higher the rim sits along this vertex's normal, taken fully at the lip and not at all at
                            // the band's edge. Only ever lifts.
                            float gap = (onRim.X - p4.X) * n4.X + (onRim.Y - p4.Y) * n4.Y + (onRim.Z - p4.Z) * n4.Z;
                            if (gap <= 0f) continue;
                            float lift = gap * (1f - dRim / CapFeather);
                            if (lift <= 1e-6f) continue;
                            WriteXYZ(emitter.outStreams[pw2.Stream], po3, pw2.Type,
                                     p4.X + n4.X * lift, p4.Y + n4.Y * lift, p4.Z + n4.Z * lift);
                            worstLift = MathF.Max(worstLift, lift);
                            ramped++;
                        }
                        if (ramped > 0)
                            emitter.build.diag?.Invoke($"authored cap: ramped {ramped} vertices behind the lip up to the "
                                       + $"cap's standoff (furthest lift {worstLift:F4} over {CapFeather:F3})");
                    }

                    if (emitter.build.welded > 0)
                    {
                        // The welded lip as segments, taken from the triangles that SURVIVED so a chord of a dropped one
                        // cannot pull the cap sideways.
                        var seenEdge = new HashSet<(ushort, ushort)>();
                        var stillOnLip = new bool[emitter.vc];
                        foreach (var sub in emitter.keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    if (!movedV[x] || !movedV[y]) continue;
                                    stillOnLip[x] = stillOnLip[y] = true;
                                    if (!seenEdge.Add((Math.Min(x, y), Math.Max(x, y)))) continue;
                                    emitter.build.shellRim.Add(new RimSeg(weldPos[x], weldNrm[x], weldWgt[x],
                                                            weldPos[y], weldNrm[y], weldWgt[y]));
                                }

                        _ = stillOnLip;
                    }
                }

                private void MatchCapRim(RimSeg[] wr, VElem pw2)
                {
                    // THE OTHER HALF OF WATERTIGHT: the cap's rim has more vertices than the lip, so the lip is split at
                    // each of them. AFTER shellRim is built, which is indexed by the pre-split numbering.
                    if (emitter.build.welded > 0 && emitter.build.weldRimPos.Count > 0)
                    {
                        // EVERY cap mesh's rim: they all belong to the one cap and all meet this shell.
                        var rimPts = emitter.build.weldRimPos.Values.ToList();
                        int preSplit = emitter.vc;
                        int mirrored = SplitCapRim(rimPts, emitter.decl, ref emitter.outStreams, emitter.outStrides,
                                                   ref emitter.vc, emitter.keptPerSub, ref emitter.used,
                                                   out int onVert2, out int offEdge2);
                        joinAdded.Add((preSplit, emitter.vc));
                        emitter.build.diag?.Invoke($"authored cap: split the SHELL's lip at {mirrored} of "
                                   + $"{rimPts.Count} cap rim vertex/vertices — {onVert2} snapped onto, "
                                   + $"{offEdge2} off the boundary");
                        JoinAudit("SHELL lip", rimPts, emitter.keptPerSub, emitter.decl, emitter.outStreams, emitter.outStrides, emitter.vc, emitter.build.diag);

                        // EVERY vertex of the FINAL lip is a landing the cap must have a vertex at, read AFTER the split and
                        // off the surviving triangles.
                        var lipUses = new Dictionary<(ushort, ushort), int>();
                        foreach (var sub in emitter.keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    var e = (Math.Min(x, y), Math.Max(x, y));
                                    lipUses[e] = lipUses.GetValueOrDefault(e) + 1;
                                }
                        var lipVerts = new HashSet<ushort>();
                        foreach (var (e, n) in lipUses)
                            if (n == 1) { lipVerts.Add(e.Item1); lipVerts.Add(e.Item2); }

                        Span<float> tmpL = stackalloc float[4];
                        int landed = 0;
                        foreach (var v in lipVerts)
                        {
                            if (v >= emitter.vc || !emitter.used[v]) continue;
                            ReadTyped(emitter.outStreams[pw2.Stream], v * emitter.outStrides[pw2.Stream] + pw2.Offset,
                                      pw2.Type, tmpL);
                            var lp = new Vec3(tmpL[0], tmpL[1], tmpL[2]);
                            // Only the stretch that meets the cap. A coverage edge or the ankle cut is
                            // boundary too and has no business being split into the cap.
                            if (!NearestOnRim(lp, wr, CapRimSplitReach, out _, out _, out _, out _)) continue;
                            emitter.build.capRimLandings.Add(lp);
                            landed++;
                        }
                        emitter.build.diag?.Invoke($"authored cap: {landed} lip vertices offered to the cap as landings "
                                   + "so every one of them gets a partner");
                    }
                }

                private void UnifyJoinNormals(VElem? nEl2, VElem? uEl0, VElem? uEl1, VElem pw2)
                {
                    // ── ONE NORMAL PER POSITION ALONG THE JOIN ───────────────────────────────────────
                    // Both splits insert vertices whose normals are interpolated from their own mesh's edge. The normal is
                    // made a function of POSITION: every lip vertex takes the cap rim's normal at the point it sits on, and
                    // the cap keeps its own, so both sides land on one answer.
                    if (nEl2 is { } ne5 && emitter.build.weldRim is { Length: > 0 } wr2)
                    {
                        var lipNow = new bool[emitter.vc];
                        var uses = new Dictionary<(ushort, ushort), int>();
                        foreach (var sub in emitter.keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                                for (int k = 0; k < 3; k++)
                                {
                                    ushort x = sub[t + k], y = sub[t + (k + 1) % 3];
                                    var e = (Math.Min(x, y), Math.Max(x, y));
                                    uses[e] = uses.GetValueOrDefault(e) + 1;
                                }
                        foreach (var (e, n) in uses)
                            if (n == 1) { lipNow[e.Item1] = true; lipNow[e.Item2] = true; }

                        Span<float> tmpN = stackalloc float[4];
                        int reshaded = 0, reuved = 0;
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            if (!lipNow[i] || !emitter.used[i]) continue;
                            int po2 = i * emitter.outStrides[pw2.Stream] + pw2.Offset;
                            ReadTyped(emitter.outStreams[pw2.Stream], po2, pw2.Type, tmpN);
                            var here = new Vec3(tmpN[0], tmpN[1], tmpN[2]);
                            // Only where the cap actually is: coverage cuts and the ankle are boundary too.
                            if (!NearestOnRim(here, wr2, CapWeldRadius, out _, out var capN2, out _, out _,
                                              out var capUvAt))
                                continue;
                            WriteNormal(emitter.outStreams[ne5.Stream], i * emitter.outStrides[ne5.Stream] + ne5.Offset,
                                        ne5.Type, capN2.X, capN2.Y, capN2.Z);
                            reshaded++;

                            // ── AND ONE UV PER POSITION, for the same reason ──────────────────────────
                            // Only where the cap's coordinate is unambiguous: at the body's atlas seam a rim vertex carries one
                            // per chart.
                            if (uEl0 is not { } ue6 || i >= emitter.uv.Length || capUvAt is not { } capUv2) continue;

                            // Keep the shell's own TILE: the two meshes may differ by whole tiles, so only the fractional part is
                            // taken from the cap.
                            float cu = capUv2.U, cvv = capUv2.V;
                            cu += MathF.Round(emitter.uv[i].U - cu);
                            cvv += MathF.Round(emitter.uv[i].V - cvv);
                            emitter.uv[i] = (cu, cvv);
                            bool half6 = ue6.Type is 13 or 14;
                            int so6 = i * emitter.outStrides[ue6.Stream];
                            WriteUV2(emitter.outStreams[ue6.Stream], so6 + ue6.Offset, half6, cu, cvv);
                            if (ue6.Type is 3 or 14)
                                WriteUV2(emitter.outStreams[ue6.Stream], so6 + ue6.Offset + (ue6.Type == 3 ? 8 : 4),
                                         ue6.Type == 14, cu, cvv);
                            if (uEl1 is { } ue7)
                                WriteUV2(emitter.outStreams[ue7.Stream], i * emitter.outStrides[ue7.Stream] + ue7.Offset,
                                         ue7.Type is 13 or 14, cu, cvv);
                            reuved++;
                        }
                        if (reshaded > 0)
                            emitter.build.diag?.Invoke($"authored cap: {reshaded} lip vertices took the cap rim's normal "
                                       + $"and {reuved} took its uv, so both sides of the join shade and "
                                       + "sample as one surface");
                    }
                    if (reweighted > 0)
                    {
                        emitter.capBoneTable = shellTbl.ToArray();
                        emitter.build.diag?.Invoke($"authored cap: {reweighted} welded shell vertices took the rim's "
                                   + $"skinning, bone table -> {shellTbl.Count}");
                    }
                    if (uvFixed > 0)
                        emitter.build.diag?.Invoke($"authored cap: {uvFixed} welded shell vertices re-read their uv from "
                                   + "the body at where they landed");
                    if (fromCapRim > 0)
                        emitter.build.diag?.Invoke($"authored cap: {fromCapRim} lip vertices took the cap rim's skinning, "
                                   + "so the pair cannot separate when posed");
                }

                private void GraftCap(VElem pw2)
                {
                    new CapGraft(this, pw2).Run();
                }

                private void RefitCappedLayers(VElem pw2)
                {
                    // EVERY capped layer, not just the one the cap was grafted into.
                    if (emitter.build.capAllVerts.Count > 0 && emitter.build.capDefNow != null)
                        // ── DROP THE TOENAIL PATCHES ─────────────────────────────────────────────
                        // The nails are their own UV islands, so the cut leaves each as a small closed patch floating under the
                        // cap. Identified by what they ARE: a component that is small AND lies entirely within a whisker of the
                        // cap's own vertices.
                        {
                            var nodeOfPos = new Dictionary<(int, int, int), int>();
                            var parent = new List<int>();
                            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                            void Union(int a2, int b2) { int ra = Find(a2), rb = Find(b2); if (ra != rb) parent[ra] = rb; }
                            Span<float> tmpC = stackalloc float[4];
                            var posOf = new Vec3[emitter.vc];
                            var node = new int[emitter.vc];
                            for (int i = 0; i < emitter.vc; i++) node[i] = -1;
                            for (int i = 0; i < emitter.vc; i++)
                            {
                                if (!emitter.used[i]) continue;
                                ReadTyped(emitter.outStreams[pw2.Stream], i * emitter.outStrides[pw2.Stream] + pw2.Offset,
                                          pw2.Type, tmpC);
                                posOf[i] = new Vec3(tmpC[0], tmpC[1], tmpC[2]);
                                var k = QuantPos(tmpC[0], tmpC[1], tmpC[2]);
                                if (!nodeOfPos.TryGetValue(k, out int n)) { nodeOfPos[k] = n = parent.Count; parent.Add(n); }
                                node[i] = n;
                            }
                            foreach (var sub in emitter.keptPerSub)
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    if (node[sub[t]] < 0 || node[sub[t + 1]] < 0 || node[sub[t + 2]] < 0) continue;
                                    Union(node[sub[t]], node[sub[t + 1]]);
                                    Union(node[sub[t + 1]], node[sub[t + 2]]);
                                }
                            var count = new Dictionary<int, int>();
                            foreach (var sub in emitter.keptPerSub)
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    if (node[sub[t]] < 0) continue;
                                    int r = Find(node[sub[t]]);
                                    count[r] = count.GetValueOrDefault(r) + 1;
                                }
                            int biggest = 0;
                            foreach (int n in count.Values) biggest = Math.Max(biggest, n);
                            emitter.build.diag?.Invoke($"NAILPROBE comps={count.Count} biggest={biggest} capVerts={emitter.build.capAllVerts.Count}");

                            var capAt = new HashSet<(int, int, int)>();
                            foreach (var cp in emitter.build.capAllVerts) capAt.Add(QuantPos(cp.X, cp.Y, cp.Z));
                            bool NearCap(Vec3 q)
                            {
                                foreach (var cp in emitter.build.capAllVerts)
                                    if (Dist(q, cp) <= NailUnderCap) return true;
                                return false;
                            }
                            var drop = new HashSet<int>();
                            foreach (var (root, n) in count)
                            {
                                    // An ABSOLUTE ceiling: an inner shell may be nothing but nail patches, all one size. The near-cap test
                                    // identifies them.
                                    if (n > NailIslandMaxTris) continue;
                                drop.Add(root);
                            }
                            if (drop.Count > 0)
                            {
                                // Confirm each candidate really is under the cap before taking it.
                                var byRoot = new Dictionary<int, List<ushort>>();
                                for (ushort i = 0; i < emitter.vc; i++)
                                {
                                    if (node[i] < 0) continue;
                                    int r = Find(node[i]);
                                    if (!drop.Contains(r)) continue;
                                    (byRoot.TryGetValue(r, out var l) ? l : byRoot[r] = new List<ushort>()).Add(i);
                                }
                                foreach (var (r, vs) in byRoot)
                                    foreach (var i in vs)
                                        if (!NearCap(posOf[i])) { drop.Remove(r); break; }
                            }
                            if (drop.Count > 0)
                            {
                                int gone = 0;
                                for (int su = 0; su < emitter.keptPerSub.Count; su++)
                                {
                                    var sub = emitter.keptPerSub[su];
                                    var keepT = new List<ushort>(sub.Length);
                                    for (int t = 0; t + 2 < sub.Length; t += 3)
                                    {
                                        if (node[sub[t]] >= 0 && drop.Contains(Find(node[sub[t]]))) { gone++; continue; }
                                        keepT.Add(sub[t]); keepT.Add(sub[t + 1]); keepT.Add(sub[t + 2]);
                                    }
                                    emitter.keptPerSub[su] = keepT.ToArray();
                                }
                                emitter.build.triOut -= gone;
                                emitter.build.diag?.Invoke($"authored cap: dropped {drop.Count} toenail patch(es) under the "
                                           + $"cap, {gone} triangle(s) - they drew nothing but their own rim");
                            }
                        }
                }

                private void WeldCracks(VElem? pEl2, VElem? uEl0)
                {
                    // WELD THE CRACKS ALONG THE TRIMS: the cuts leave boundary vertices on top of one another without being
                    // the same vertex. Only boundary vertices, only where both sit on the same spot.
                    {
                        var onEdge = new HashSet<ushort>();
                        {
                            var cnt = new Dictionary<(ushort, ushort), int>();
                            foreach (var sub in emitter.keptPerSub)
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                    for (int k = 0; k < 3; k++)
                                    {
                                        ushort a3 = sub[t + k], b3 = sub[t + (k + 1) % 3];
                                        var key = a3 < b3 ? (a3, b3) : (b3, a3);
                                        cnt[key] = cnt.GetValueOrDefault(key) + 1;
                                    }
                            foreach (var (k, n) in cnt)
                                if (n == 1) { onEdge.Add(k.Item1); onEdge.Add(k.Item2); }
                        }

                        var at = new Dictionary<(int, int, int), ushort>();
                        var into = new ushort[emitter.vc];
                        for (ushort i = 0; i < emitter.vc; i++) into[i] = i;
                        int welds = 0, snapped = 0;
                        if (pEl2 is { } pw3)
                        {
                            Span<float> tw = stackalloc float[4];
                            // A plain array, not a stackalloc: the local function below reads it
                            // and a ref local cannot be captured.
                            var tuv = new float[4];
                            // Quantised at the weld tolerance, so two points inside it share a bucket.
                            (int, int, int) Bucket(float x, float y, float z)
                                => ((int)MathF.Round(x / CrackWeldTolerance),
                                    (int)MathF.Round(y / CrackWeldTolerance),
                                    (int)MathF.Round(z / CrackWeldTolerance));
                            // ...AND ONLY IF THE TWO SAMPLE THE SAME PLACE: welding across a UV chart boundary drags every face on
                            // the losing side across the atlas.
                            var uvAt = new Dictionary<ushort, (float U, float V)>();
                            (float, float) UvOf(ushort i)
                            {
                                if (uvAt.TryGetValue(i, out var got)) return got;
                                var r = (0f, 0f);
                                if (uEl0 is { } ue7)
                                {
                                    ReadTyped(emitter.outStreams[ue7.Stream], i * emitter.outStrides[ue7.Stream] + ue7.Offset,
                                              ue7.Type, tuv);
                                    r = (tuv[0], tuv[1]);
                                }
                                uvAt[i] = r;
                                return r;
                            }
                            foreach (var i in onEdge)
                            {
                                if (!emitter.used[i]) continue;
                                ReadTyped(emitter.outStreams[pw3.Stream], i * emitter.outStrides[pw3.Stream] + pw3.Offset,
                                          pw3.Type, tw);
                                var b4 = Bucket(tw[0], tw[1], tw[2]);
                                // The 27 buckets around it: two points inside the tolerance can land either side of a bucket boundary.
                                ushort keep = i;
                                bool found2 = false;
                                for (int dx = -1; dx <= 1 && !found2; dx++)
                                for (int dy = -1; dy <= 1 && !found2; dy++)
                                for (int dz = -1; dz <= 1 && !found2; dz++)
                                    if (at.TryGetValue((b4.Item1 + dx, b4.Item2 + dy, b4.Item3 + dz),
                                                       out var cand2) && cand2 != i)
                                    { keep = cand2; found2 = true; }
                                if (found2)
                                {
                                    var (u1, v1) = UvOf(i);
                                    var (u2, v2) = UvOf(keep);
                                    float du = u1 - u2, dv = v1 - v2;
                                    if (du * du + dv * dv <= CrackWeldUvTolerance * CrackWeldUvTolerance)
                                    { into[i] = keep; welds++; continue; }

                                    // DIFFERENT CHARTS: SNAP, DO NOT MERGE. Two vertices at the same position leave no gap, and each keeps
                                    // the coordinate it samples.
                                    ReadTyped(emitter.outStreams[pw3.Stream],
                                              keep * emitter.outStrides[pw3.Stream] + pw3.Offset, pw3.Type, tw);
                                    WriteXYZ(emitter.outStreams[pw3.Stream],
                                             i * emitter.outStrides[pw3.Stream] + pw3.Offset, pw3.Type,
                                             tw[0], tw[1], tw[2]);
                                    snapped++;
                                }
                                else at[b4] = i;
                            }
                        }

                        if (snapped > 0)
                            emitter.build.diag?.Invoke($"authored cap: closed {snapped} crack(s) by moving one side onto "
                                       + "the other without merging them, so each keeps the part of the "
                                       + "atlas it samples");
                        if (welds > 0)
                        {
                            int lost = 0;
                            for (int su = 0; su < emitter.keptPerSub.Count; su++)
                            {
                                var sub = emitter.keptPerSub[su];
                                var keepT = new List<ushort>(sub.Length);
                                for (int t = 0; t + 2 < sub.Length; t += 3)
                                {
                                    ushort a4 = into[sub[t]], b5 = into[sub[t + 1]], c4 = into[sub[t + 2]];
                                    // A triangle whose corners weld together has no area left.
                                    if (a4 == b5 || b5 == c4 || c4 == a4) { lost++; continue; }
                                    keepT.Add(a4); keepT.Add(b5); keepT.Add(c4);
                                }
                                emitter.keptPerSub[su] = keepT.ToArray();
                            }
                            emitter.build.triOut -= lost;
                            for (int i = 0; i < emitter.vc; i++) if (into[i] != i) emitter.used[i] = false;
                            emitter.build.diag?.Invoke($"authored cap: welded {welds} crack(s) along the trims at "
                                       + $"{CrackWeldTolerance:F4}"
                                       + (lost > 0 ? $", dropping {lost} triangle(s) left with no area" : "")
                                       );
                        }
                    }

                    int holesShut = FillSmallHoles(emitter.keptPerSub, emitter.vc, ref emitter.used, SmallHoleEdges);
                    if (holesShut > 0)
                        emitter.build.diag?.Invoke($"authored cap: closed {holesShut} small hole(s) left along the join");
                }

                private void RaisePokeThrough(VElem? pEl2)
                {
                    // ── RAISE ANYTHING THE SKIN POKES THROUGH ────────────────────────────────────
                    // The weld and the binding can leave the surface BETWEEN two clear vertices under the body's curve.
                    // Driven from the BODY: the shell's vertices are clear, the body bulges past the flat triangle between
                    // them. MinSkinClearanceOfPush keeps the floor under the push; MaxSkinLift bounds the repair.
                    // RELAX THE SHELL AROUND THE TOES, OUTWARD ONLY: welded by position so UV-split copies move as one,
                    // unique edges, Taubin, and the inward component of every step dropped so the clearance pass has
                    // nothing to undo.
                    if (emitter.build.capAllVerts.Count > 0 && pEl2 is { } pr0 && ShellRelaxPasses > 0)
                    {
                        Span<float> tmpS = stackalloc float[4];
                        var cur0 = new Vec3[emitter.vc];
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            if (!emitter.used[i]) continue;
                            ReadTyped(emitter.outStreams[pr0.Stream], i * emitter.outStrides[pr0.Stream] + pr0.Offset,
                                      pr0.Type, tmpS);
                            cur0[i] = new Vec3(tmpS[0], tmpS[1], tmpS[2]);
                        }

                        var nodeOf = new Dictionary<(int, int, int), int>();
                        var owner = new int[emitter.vc];
                        Array.Fill(owner, -1);
                        var nodePos = new List<Vec3>();
                        var nodeVerts = new List<List<ushort>>();
                        for (ushort i = 0; i < emitter.vc; i++)
                        {
                            if (!emitter.used[i]) continue;
                            var key = QuantPos(cur0[i].X, cur0[i].Y, cur0[i].Z);
                            if (!nodeOf.TryGetValue(key, out int nd))
                            {
                                nd = nodePos.Count;
                                nodeOf[key] = nd;
                                nodePos.Add(cur0[i]);
                                nodeVerts.Add([]);
                            }
                            owner[i] = nd;
                            nodeVerts[nd].Add(i);
                        }

                        int nn = nodePos.Count;
                        var nb = new HashSet<int>[nn];
                        var acc = new Vec3[nn];
                        foreach (var sub in emitter.keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                            {
                                int a2 = owner[sub[t]], b2 = owner[sub[t + 1]], c2 = owner[sub[t + 2]];
                                if (a2 < 0 || b2 < 0 || c2 < 0) continue;
                                foreach (var (x2, y2) in new[] { (a2, b2), (b2, c2), (c2, a2) })
                                {
                                    if (x2 == y2) continue;
                                    (nb[x2] ??= []).Add(y2);
                                    (nb[y2] ??= []).Add(x2);
                                }
                                var e1 = new Vec3(nodePos[b2].X - nodePos[a2].X, nodePos[b2].Y - nodePos[a2].Y,
                                                  nodePos[b2].Z - nodePos[a2].Z);
                                var e2 = new Vec3(nodePos[c2].X - nodePos[a2].X, nodePos[c2].Y - nodePos[a2].Y,
                                                  nodePos[c2].Z - nodePos[a2].Z);
                                var fn = new Vec3(e1.Y * e2.Z - e1.Z * e2.Y, e1.Z * e2.X - e1.X * e2.Z,
                                                  e1.X * e2.Y - e1.Y * e2.X);
                                foreach (int q in new[] { a2, b2, c2 })
                                    acc[q] = new Vec3(acc[q].X + fn.X, acc[q].Y + fn.Y, acc[q].Z + fn.Z);
                            }

                        var outN = new Vec3[nn];
                        for (int n = 0; n < nn; n++) outN[n] = NormalizeOr(acc[n], default);

                        var move = new bool[nn];
                        int inBand = 0;
                        for (int n = 0; n < nn; n++)
                        {
                            if (nb[n] is not { Count: > 2 }) continue;
                            if (outN[n] is { X: 0, Y: 0, Z: 0 }) continue;
                            foreach (var q0 in emitter.build.capAllVerts)
                                if (Dist(nodePos[n], q0) <= ShellRelaxReach) { move[n] = true; inBand++; break; }
                        }

                        var start = nodePos.ToArray();
                        var cur = nodePos.ToArray();
                        var nxt = new Vec3[nn];
                        for (int pass = 0; pass < ShellRelaxPasses; pass++)
                            foreach (float lam in new[] { ShellRelaxLambda, ShellRelaxMu })
                            {
                                Array.Copy(cur, nxt, nn);
                                for (int n = 0; n < nn; n++)
                                {
                                    if (!move[n]) continue;
                                    Vec3 sum = default;
                                    int c3 = 0;
                                    foreach (int mn in nb[n]!)
                                    { sum = new Vec3(sum.X + cur[mn].X, sum.Y + cur[mn].Y, sum.Z + cur[mn].Z); c3++; }
                                    if (c3 == 0) continue;
                                    float ic = 1f / c3;
                                    var to = new Vec3(cur[n].X + (sum.X * ic - cur[n].X) * lam,
                                                      cur[n].Y + (sum.Y * ic - cur[n].Y) * lam,
                                                      cur[n].Z + (sum.Z * ic - cur[n].Z) * lam);

                                    // Measured from where it STARTED, so the constraint is on the result
                                    // rather than on one step of it.
                                    float dx7 = to.X - start[n].X, dy7 = to.Y - start[n].Y, dz7 = to.Z - start[n].Z;
                                    var un = outN[n];
                                    float along = dx7 * un.X + dy7 * un.Y + dz7 * un.Z;
                                    if (along < 0f)
                                    {   // drop the inward part; keep the sideways part entire
                                        dx7 -= un.X * along; dy7 -= un.Y * along; dz7 -= un.Z * along;
                                    }
                                    float dl = MathF.Sqrt(dx7 * dx7 + dy7 * dy7 + dz7 * dz7);
                                    if (dl > ShellRelaxMaxDrift)
                                    {
                                        float k4 = ShellRelaxMaxDrift / dl;
                                        dx7 *= k4; dy7 *= k4; dz7 *= k4;
                                    }
                                    nxt[n] = new Vec3(start[n].X + dx7, start[n].Y + dy7, start[n].Z + dz7);
                                }
                                (cur, nxt) = (nxt, cur);
                            }

                        int smoothed = 0;
                        float worstS = 0f;
                        for (int n = 0; n < nn; n++)
                        {
                            if (!move[n]) continue;
                            float d5 = Dist(cur[n], start[n]);
                            if (d5 <= 1e-6f) continue;
                            foreach (var i in nodeVerts[n])
                                WriteXYZ(emitter.outStreams[pr0.Stream], i * emitter.outStrides[pr0.Stream] + pr0.Offset,
                                         pr0.Type, cur[n].X, cur[n].Y, cur[n].Z);
                            worstS = MathF.Max(worstS, d5);
                            smoothed++;
                        }
                        if (smoothed > 0)
                            emitter.build.diag?.Invoke($"authored cap: relaxed {smoothed} of {inBand} welded shell points "
                                       + $"around the toes, outward only, furthest {worstS:F5}");
                    }
                }

                private void CollectBodySolid()
                {
                    if (emitter.build.bodySolid == null)
                    {
                        emitter.build.bodySolid = CollectSkinTriangles(emitter.build.sourceModels, dropIslands: false);

                        // ONLY THE NON-SKIN THAT HUGS THE SKIN: a nail lies on the flesh, a strap stands off it, and a sandal
                        // strap is supposed to sit outside the stocking. No material names needed.
                        var skinNear = new HashSet<(int, int, int)>();
                        foreach (var t in emitter.build.bodySolid)
                            foreach (var q in new[] { t.A, t.B, t.C })
                                skinNear.Add(((int)MathF.Floor(q.X / NailHugsSkin),
                                              (int)MathF.Floor(q.Y / NailHugsSkin),
                                              (int)MathF.Floor(q.Z / NailHugsSkin)));
                        bool HugsSkin(Vec3 q)
                        {
                            int cx = (int)MathF.Floor(q.X / NailHugsSkin);
                            int cy = (int)MathF.Floor(q.Y / NailHugsSkin);
                            int cz = (int)MathF.Floor(q.Z / NailHugsSkin);
                            for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                            for (int dz = -1; dz <= 1; dz++)
                                if (skinNear.Contains((cx + dx, cy + dy, cz + dz))) return true;
                            return false;
                        }

                        // ...AND ONLY IF IT IS NAIL-SIZED: a shoe's inner surface also hugs the foot, but it is one island of
                        // tens of thousands of triangles. Grouped by shared position.
                        var nonSkin = CollectSkinTriangles(emitter.build.sourceModels, skinOnly: false,
                                                           dropIslands: false, nonSkin: true);
                        var owner = new Dictionary<(int, int, int), int>();
                        var parent = new List<int>();
                        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                        void Union(int a, int b)
                        { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
                        int NodeAt(Vec3 q)
                        {
                            var k = QuantPos(q.X, q.Y, q.Z);
                            if (owner.TryGetValue(k, out int got)) return got;
                            owner[k] = parent.Count;
                            parent.Add(parent.Count);
                            return parent.Count - 1;
                        }
                        var triNode = new int[nonSkin.Count];
                        for (int i = 0; i < nonSkin.Count; i++)
                        {
                            var t = nonSkin[i];
                            int na = NodeAt(t.A), nb = NodeAt(t.B), nc = NodeAt(t.C);
                            Union(na, nb); Union(nb, nc);
                            triNode[i] = na;
                        }
                        var islandSize = new Dictionary<int, int>();
                        for (int i = 0; i < nonSkin.Count; i++)
                        {
                            int r = Find(triNode[i]);
                            islandSize[r] = islandSize.GetValueOrDefault(r) + 1;
                        }

                        int keptNonSkin = 0, dropped = 0;
                        for (int i = 0; i < nonSkin.Count; i++)
                        {
                            var t = nonSkin[i];
                            var ctr = new Vec3((t.A.X + t.B.X + t.C.X) / 3f,
                                               (t.A.Y + t.B.Y + t.C.Y) / 3f,
                                               (t.A.Z + t.B.Z + t.C.Z) / 3f);
                            if (islandSize[Find(triNode[i])] <= NailIslandMaxTris && HugsSkin(ctr))
                            { emitter.build.bodySolid.Add(t); keptNonSkin++; }
                            else dropped++;
                        }
                        if (dropped > 0)
                            emitter.build.diag?.Invoke($"authored cap: clearance sees {keptNonSkin} non-skin triangle(s) "
                                       + $"small enough and close enough to be a toenail, and ignores "
                                       + $"{dropped} (a shoe is meant to be outside the shell)");
                    }
                }

                private void ClearBodySolid(VElem? nEl2, VElem pw2)
                {
                    if (emitter.build.bodySolid!.Count > 0 && emitter.build.capAllVerts.Count > 0 && nEl2 is { } ne10)
                    {
                        float lx = float.MaxValue, ly = float.MaxValue, lz = float.MaxValue;
                        float hx = float.MinValue, hy = float.MinValue, hz = float.MinValue;
                        foreach (var q0 in emitter.build.capAllVerts)
                        {
                            lx = MathF.Min(lx, q0.X); hx = MathF.Max(hx, q0.X);
                            ly = MathF.Min(ly, q0.Y); hy = MathF.Max(hy, q0.Y);
                            lz = MathF.Min(lz, q0.Z); hz = MathF.Max(hz, q0.Z);
                        }
                        const float pad = 0.01f;
                        float minClearance = MinSkinClearanceOfPush * emitter.push;
                        // Tested against the shell's TRIANGLES, not its nearest vertex: what shows through is the body's curve
                        // rising past the flat triangle.
                        const float cell = 0.004f;
                        (int, int, int) Cell(Vec3 q) => ((int)MathF.Floor(q.X / cell),
                                                         (int)MathF.Floor(q.Y / cell),
                                                         (int)MathF.Floor(q.Z / cell));
                        Span<float> tmpR = stackalloc float[4];
                        var shellPos = new Vec3[emitter.vc];
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            if (!emitter.used[i]) continue;
                            ReadTyped(emitter.outStreams[pw2.Stream], i * emitter.outStrides[pw2.Stream] + pw2.Offset,
                                      pw2.Type, tmpR);
                            shellPos[i] = new Vec3(tmpR[0], tmpR[1], tmpR[2]);
                        }

                        // Every surviving triangle near the cap, registered in each cell its bounding box
                        // touches so a body vertex can find the ones it might be standing through.
                        var triHash = new Dictionary<(int, int, int), List<(ushort, ushort, ushort)>>();
                        foreach (var sub in emitter.keptPerSub)
                            for (int t = 0; t + 2 < sub.Length; t += 3)
                            {
                                ushort ia = sub[t], ib = sub[t + 1], ic = sub[t + 2];
                                if (!emitter.used[ia] || !emitter.used[ib] || !emitter.used[ic]) continue;
                                Vec3 a = shellPos[ia], b = shellPos[ib], c = shellPos[ic];
                                float tlx = MathF.Min(a.X, MathF.Min(b.X, c.X));
                                float thx = MathF.Max(a.X, MathF.Max(b.X, c.X));
                                float tly = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                                float thy = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                                float tlz = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
                                float thz = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                                if (thx < lx - pad || tlx > hx + pad || thy < ly - pad || tly > hy + pad
                                    || thz < lz - pad || tlz > hz + pad) continue;
                                var k0 = Cell(new Vec3(tlx, tly, tlz));
                                var k1 = Cell(new Vec3(thx, thy, thz));
                                for (int cx = k0.Item1; cx <= k1.Item1; cx++)
                                for (int cy = k0.Item2; cy <= k1.Item2; cy++)
                                for (int cz = k0.Item3; cz <= k1.Item3; cz++)
                                {
                                    var k = (cx, cy, cz);
                                    (triHash.TryGetValue(k, out var l)
                                        ? l : triHash[k] = new List<(ushort, ushort, ushort)>()).Add((ia, ib, ic));
                                }
                            }

                        // ON THE JOIN: a vertex the weld moved, or one the lip split or rim stitch inserted
                        // along it. The cap's own interior is not on it, whatever its index.
                        var onJoin = new bool[emitter.vc];
                        Array.Copy(movedV, onJoin, Math.Min(movedV.Length, emitter.vc));
                        foreach (var (from, to) in joinAdded)
                            for (int i = from; i < to && i < emitter.vc; i++) onJoin[i] = true;

                        var lift = new float[emitter.vc];
                        foreach (var t in emitter.build.bodySolid)
                            foreach (var (bp, bn) in new[] { (t.A, t.Na), (t.B, t.Nb), (t.C, t.Nc) })
                            {
                                if (bp.X < lx - pad || bp.X > hx + pad || bp.Y < ly - pad || bp.Y > hy + pad
                                    || bp.Z < lz - pad || bp.Z > hz + pad) continue;
                                var n2 = NormalizeOr(bn, default);
                                if (n2 is { X: 0, Y: 0, Z: 0 }) continue;
                                if (!triHash.TryGetValue(Cell(bp), out var near)) continue;

                                foreach (var (ia, ib, ic) in near)
                                {
                                    Vec3 a = shellPos[ia], b = shellPos[ib], c = shellPos[ic];
                                    var e1 = new Vec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                                    var e2 = new Vec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                                    var fn = NormalizeOr(new Vec3(e1.Y * e2.Z - e1.Z * e2.Y,
                                                                  e1.Z * e2.X - e1.X * e2.Z,
                                                                  e1.X * e2.Y - e1.Y * e2.X), default);
                                    if (fn is { X: 0, Y: 0, Z: 0 }) continue;
                                    // Orient outward, by the body's own normal: winding alone cannot be
                                    // trusted across a mesh assembled from several sources.
                                    if (fn.X * n2.X + fn.Y * n2.Y + fn.Z * n2.Z < 0)
                                        fn = new Vec3(-fn.X, -fn.Y, -fn.Z);

                                    // How far the triangle's plane stands above this body vertex. A face
                                    // on the join keeps the absolute floor — see WeldedSkinClearance.
                                    float h = (a.X - bp.X) * fn.X + (a.Y - bp.Y) * fn.Y + (a.Z - bp.Z) * fn.Z;
                                    float floor = onJoin[ia] || onJoin[ib] || onJoin[ic]
                                        ? MathF.Max(minClearance, WeldedSkinClearance)
                                        : minClearance;
                                    if (h >= floor || h < -MaxSkinLift) continue;

                                    // Only if the body vertex is actually UNDER this triangle: the plane
                                    // of a triangle elsewhere on the foot says nothing about this spot.
                                    var q = new Vec3(bp.X + fn.X * h, bp.Y + fn.Y * h, bp.Z + fn.Z * h);
                                    bool inside = true;
                                    foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                                    {
                                        var ev = new Vec3(v.X - u.X, v.Y - u.Y, v.Z - u.Z);
                                        var qv = new Vec3(q.X - u.X, q.Y - u.Y, q.Z - u.Z);
                                        float side = (ev.Y * qv.Z - ev.Z * qv.Y) * fn.X
                                                   + (ev.Z * qv.X - ev.X * qv.Z) * fn.Y
                                                   + (ev.X * qv.Y - ev.Y * qv.X) * fn.Z;
                                        if (side < -1e-9f) { inside = false; break; }
                                    }
                                    if (!inside) continue;

                                    // Lift the whole face — one corner is not what the skin came through.
                                    float need = MathF.Min(floor - h, MaxSkinLift);
                                    lift[ia] = MathF.Max(lift[ia], need);
                                    lift[ib] = MathF.Max(lift[ib], need);
                                    lift[ic] = MathF.Max(lift[ic], need);
                                }
                            }

                        // ONE LIFT PER POSITION, along one direction: the join is full of coincident unmerged vertices, and
                        // lifting each on its own reopens the crack.
                        var group = new Dictionary<(int, int, int), (float Lift, Vec3 Dir)>();
                        var rnOf = new Vec3[emitter.vc];
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            if (!emitter.used[i]) continue;
                            ReadTyped(emitter.outStreams[ne10.Stream], i * emitter.outStrides[ne10.Stream] + ne10.Offset,
                                      ne10.Type, tmpR);
                            float rx = tmpR[0], ry = tmpR[1], rz = tmpR[2];
                            if (ne10.Type == 8) { rx = rx * 2 - 1; ry = ry * 2 - 1; rz = rz * 2 - 1; }
                            rnOf[i] = NormalizeOr(new Vec3(rx, ry, rz), default);
                            var key = QuantPos(shellPos[i].X, shellPos[i].Y, shellPos[i].Z);
                            var g = group.GetValueOrDefault(key);
                            group[key] = (MathF.Max(g.Lift, lift[i]),
                                          new Vec3(g.Dir.X + rnOf[i].X, g.Dir.Y + rnOf[i].Y, g.Dir.Z + rnOf[i].Z));
                        }

                        int raised = 0;
                        float worstLift2 = 0f;
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            if (!emitter.used[i]) continue;
                            var sp = shellPos[i];
                            var (gl, gd) = group[QuantPos(sp.X, sp.Y, sp.Z)];
                            if (gl <= 1e-6f) continue;
                            var rn = NormalizeOr(gd, rnOf[i]);
                            if (rn is { X: 0, Y: 0, Z: 0 }) continue;
                            WriteXYZ(emitter.outStreams[pw2.Stream], i * emitter.outStrides[pw2.Stream] + pw2.Offset, pw2.Type,
                                     sp.X + rn.X * gl, sp.Y + rn.Y * gl, sp.Z + rn.Z * gl);
                            worstLift2 = MathF.Max(worstLift2, gl);
                            raised++;
                        }
                        if (raised > 0)
                            emitter.build.diag?.Invoke($"authored cap: raised {raised} vertex/vertices clear of the skin "
                                       + $"(furthest {worstLift2:F5}) so it cannot show through");
                    }
                }
            }
        }
    }
}
