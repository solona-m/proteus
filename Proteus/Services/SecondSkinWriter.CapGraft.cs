using System;
using System.Collections.Generic;

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
                private sealed class CapGraft
                {
                    private readonly CapWeld weld;
                    private readonly VElem pw2;
                    private int reused;
                    private int added;
                    private int capTriCount;
                    private List<Vec3> capVerts = null!;
                    private List<ushort> capInterior = null!;
                    private Dictionary<(int, int, int), ushort> atPos = null!;
                    private VElem? gN;
                    private VElem? gU0;
                    private VElem? gU1;
                    private VElem? gW;
                    private VElem? gI;
                    private List<ushort> newTris = null!;
                    private int gEnd;

                    public CapGraft(CapWeld weld, VElem pw2)
                    {
                        this.weld = weld;
                        this.pw2 = pw2;
                    }

                    public void Run()
                    {
                        // ── GRAFT THE CAP INTO THIS MESH ─────────────────────────────────────────────────
                        // At the end of the weld, the last moment the shell's vertices are still addressable by their own indices.
                        if (!weld.emitter.build.capGrafted && weld.emitter.build.capSrc is { } gcap && weld.emitter.build.capDefNow is { } gdef && weld.emitter.build.welded > 0)
                        {
                            IndexShellVertices(gcap);
                            AppendCapVertices(gcap, gdef);
                            AppendCapTriangles();
                        }
                    }

                    private void IndexShellVertices(Source gcap)
                    {
                        reused = 0;
                        added = 0;
                        capTriCount = 0;
                        capVerts = new List<Vec3>();
                        // The cap's INTERIOR: vertices shared with the shell keep the weld's normal.
                        capInterior = new List<ushort>();
                        // Where the shell already has a vertex, so the cap reuses it rather than emitting a copy.
                        atPos = new Dictionary<(int, int, int), ushort>();
                        Span<float> tmpG = stackalloc float[4];
                        for (int i = 0; i < weld.emitter.vc; i++)
                        {
                            if (!weld.emitter.used[i]) continue;
                            ReadTyped(weld.emitter.outStreams[pw2.Stream], i * weld.emitter.outStrides[pw2.Stream] + pw2.Offset,
                                      pw2.Type, tmpG);
                            atPos[QuantPos(tmpG[0], tmpG[1], tmpG[2])] = (ushort)i;
                        }

                        gN = null;
                        gU0 = null;
                        gU1 = null;
                        gW = null;
                        gI = null;
                        foreach (var el in weld.emitter.decl)
                        {
                            if (el.Usage == UseNormal) gN ??= el;
                            if (el.Usage == UseBlendWeight) gW ??= el;
                            if (el.Usage == UseBlendIndices) gI ??= el;
                            if (el.Usage == UseUV) { if (el.UsageIndex == 0) gU0 ??= el; else gU1 ??= el; }
                        }

                        newTris = new List<ushort>();
                        gEnd = gcap.Lod0MeshIndex + gcap.Lod0MeshCount;
                    }

                    private void AppendCapVertices(Source gcap, SecondSkinLayer gdef)
                    {
                        for (int cm = gcap.Lod0MeshIndex; cm < gEnd && cm < gcap.MeshCount; cm++)
                        {
                            if (BitConverter.ToUInt16(gcap.S, gcap.MeshStart + cm * 36) == 0) continue;
                            if (!weld.emitter.build.capUvCache.TryGetValue(cm, out var gpl) || gpl == null) continue;
                            if (weld.emitter.build.capPlaced == null || !weld.emitter.build.capPlaced.TryGetValue(cm, out var gplace)) continue;

                            int cmLocal = cm;
                            Vec3 Final(int s) => weld.emitter.build.weldRimPos.TryGetValue((cmLocal, s), out var atRim)
                                ? atRim
                                : new Vec3(gplace.Pos[s].X + gplace.Nrm[s].X * weld.emitter.build.capPushNow,
                                           gplace.Pos[s].Y + gplace.Nrm[s].Y * weld.emitter.build.capPushNow,
                                           gplace.Pos[s].Z + gplace.Nrm[s].Z * weld.emitter.build.capPushNow);

                            // The shell's UVs are on its own [0,1] tile; take the whole-tile difference off a vertex the two share.
                            // The template seeds every grafted vertex so colour and tangent arrive valid.
                            ushort template = 0;
                            bool haveTemplate = false;
                            float shU = 0f, shV = 0f;
                            for (int oi = 0; oi < gpl.SourceOf.Length; oi++)
                            {
                                int sv0 = gpl.SourceOf[oi];
                                if (sv0 < 0 || sv0 >= gplace.Pos.Length || oi >= gpl.Uv.Length) continue;
                                var fp0 = Final(sv0);
                                if (!atPos.TryGetValue(QuantPos(fp0.X, fp0.Y, fp0.Z), out ushort sv)) continue;
                                if (sv >= weld.emitter.uv.Length) continue;
                                shU = MathF.Round(weld.emitter.uv[sv].U - gpl.Uv[oi].U);
                                shV = MathF.Round(weld.emitter.uv[sv].V - gpl.Uv[oi].V);
                                template = sv; haveTemplate = true;
                                break;
                            }

                            if (!haveTemplate)
                            {
                                weld.emitter.build.diag?.Invoke($"authored cap: mesh {cm} shares no vertex with this shell - "
                                           + "not grafting it here");
                                continue;
                            }

                            var map = new ushort[gpl.SourceOf.Length];
                            var have = new bool[gpl.SourceOf.Length];
                            for (int f = 0; f * 3 + 2 < gpl.Corner.Length; f++)
                            {
                                int c0 = gpl.Corner[f * 3], c1 = gpl.Corner[f * 3 + 1], c2 = gpl.Corner[f * 3 + 2];
                                if (c0 >= gpl.Uv.Length || c1 >= gpl.Uv.Length || c2 >= gpl.Uv.Length) continue;
                                if (gdef.Coverage != null
                                    && !AnyVisible(gdef, gpl.Uv[c0], gpl.Uv[c1], gpl.Uv[c2])) continue;

                                bool ok = true;
                                foreach (int c in new[] { c0, c1, c2 })
                                {
                                    if (have[c]) continue;
                                    int cs2 = gpl.SourceOf[c];
                                    if (cs2 < 0 || cs2 >= gplace.Pos.Length) { ok = false; break; }
                                    var fp = Final(cs2);
                                    if (atPos.TryGetValue(QuantPos(fp.X, fp.Y, fp.Z), out ushort sv))
                                    { map[c] = sv; have[c] = true; reused++; capVerts.Add(fp); continue; }
                                    if (weld.emitter.vc >= ushort.MaxValue - 4) { ok = false; break; }

                                    ushort nv2 = GrowOne(ref weld.emitter.outStreams, weld.emitter.outStrides, ref weld.emitter.vc, ref weld.emitter.used, template);
                                    WriteXYZ(weld.emitter.outStreams[pw2.Stream], nv2 * weld.emitter.outStrides[pw2.Stream] + pw2.Offset,
                                             pw2.Type, fp.X, fp.Y, fp.Z);
                                    if (gN is { } ne9)
                                        WriteNormal(weld.emitter.outStreams[ne9.Stream],
                                                    nv2 * weld.emitter.outStrides[ne9.Stream] + ne9.Offset, ne9.Type,
                                                    gplace.Nrm[cs2].X, gplace.Nrm[cs2].Y, gplace.Nrm[cs2].Z);
                                    if (gU0 is { } ue8)
                                    {
                                        float cu2 = gpl.Uv[c].U + shU, cv2 = gpl.Uv[c].V + shV;
                                        bool h8 = ue8.Type is 13 or 14;
                                        int so8 = nv2 * weld.emitter.outStrides[ue8.Stream];
                                        WriteUV2(weld.emitter.outStreams[ue8.Stream], so8 + ue8.Offset, h8, cu2, cv2);
                                        if (ue8.Type is 3 or 14)
                                            WriteUV2(weld.emitter.outStreams[ue8.Stream],
                                                     so8 + ue8.Offset + (ue8.Type == 3 ? 8 : 4),
                                                     ue8.Type == 14, cu2, cv2);
                                        if (gU1 is { } ue9)
                                            WriteUV2(weld.emitter.outStreams[ue9.Stream],
                                                     nv2 * weld.emitter.outStrides[ue9.Stream] + ue9.Offset,
                                                     ue9.Type is 13 or 14, cu2, cv2);
                                    }
                                    // Skinning by NAME into this mesh's own table, the same route the
                                    // welded lip takes, so one table serves the merged mesh.
                                    if (gW is { } we9 && gI is { } ie9 && cs2 < gpl.SrcW.Length)
                                        WriteSkinNamed(weld.emitter.outStreams, weld.emitter.outStrides, we9, ie9, nv2, gpl.SrcW[cs2],
                                                       weld.shellSlot, weld.shellTbl, weld.emitter.build.boneIndex);
                                    map[c] = nv2; have[c] = true; added++; capVerts.Add(fp);
                                    capInterior.Add(nv2);
                                }
                                if (!ok) continue;
                                newTris.Add(map[c0]); newTris.Add(map[c1]); newTris.Add(map[c2]);
                                capTriCount++;
                            }
                        }
                    }

                    private void AppendCapTriangles()
                    {
                        if (newTris.Count > 0)
                        {
                            // Into the submesh that lost the most to the cut; its bone window already covers this region.
                            int host = 0;
                            for (int su = 1; su < weld.emitter.keptPerSub.Count; su++)
                                if (weld.emitter.keptPerSub[su].Length > weld.emitter.keptPerSub[host].Length) host = su;
                            var grown = new List<ushort>(weld.emitter.keptPerSub[host]);
                            grown.AddRange(newTris);
                            weld.emitter.keptPerSub[host] = grown.ToArray();
                            foreach (var t in newTris) weld.emitter.used[t] = true;
                            weld.emitter.build.triOut += capTriCount;
                            weld.emitter.capBoneTable = weld.shellTbl.ToArray();
                            weld.emitter.build.capGrafted = true;
                            weld.emitter.build.diag?.Invoke($"authored cap: grafted INTO the shell mesh — {capTriCount} triangle(s), "
                                       + $"{reused} vertex references shared with the shell, {added} added; "
                                       + "the join is interior edges now, not two boundaries");

                            // Merging alone leaves T-junctions: the two runs must share EDGES, not just vertices. Fed both runs'
                            // vertices so each is split at the other's — see StitchBoundaryAt.
                            var rimPts2 = new List<Vec3>(weld.emitter.build.capRimLandings);
                            rimPts2.AddRange(weld.emitter.build.weldRimPos.Values);
                            int preStitch = weld.emitter.vc;
                            int stitched = StitchBoundaryAt(rimPts2, weld.emitter.decl, ref weld.emitter.outStreams, weld.emitter.outStrides,
                                                            ref weld.emitter.vc, weld.emitter.keptPerSub, ref weld.emitter.used);
                            weld.joinAdded.Add((preStitch, weld.emitter.vc));
                            weld.emitter.build.diag?.Invoke($"authored cap: stitched the merged rim - {stitched} vertex/vertices "
                                       + $"inserted and {StitchShared} split point(s) reused a vertex the mesh "
                                       + $"already had, so the two runs share edges ({rimPts2.Count} positions)");

                            // THE CAP KEEPS ITS OWN SHADING: recomputed from the merged mesh, since the binding bends the cap on a
                            // body it was not modelled for. Interior only; a vertex shared with the shell keeps the weld's normal.
                            if (gN is { } ne11 && capInterior.Count > 0)
                            {
                                var acc = new Dictionary<ushort, Vec3>();
                                // A plain array: the local function below reads it, and a ref local
                                // cannot be captured.
                                var tp = new float[4];
                                Vec3 PosOf(ushort v)
                                {
                                    ReadTyped(weld.emitter.outStreams[pw2.Stream], v * weld.emitter.outStrides[pw2.Stream] + pw2.Offset,
                                              pw2.Type, tp);
                                    return new Vec3(tp[0], tp[1], tp[2]);
                                }
                                var want = new HashSet<ushort>(capInterior);
                                foreach (var sub in weld.emitter.keptPerSub)
                                    for (int t = 0; t + 2 < sub.Length; t += 3)
                                    {
                                        ushort a5 = sub[t], b5 = sub[t + 1], c5 = sub[t + 2];
                                        if (!want.Contains(a5) && !want.Contains(b5) && !want.Contains(c5))
                                            continue;
                                        Vec3 pa = PosOf(a5), pb = PosOf(b5), pc = PosOf(c5);
                                        // Unnormalised, so a big triangle counts for more than a sliver.
                                        var fn = new Vec3(
                                            (pb.Y - pa.Y) * (pc.Z - pa.Z) - (pb.Z - pa.Z) * (pc.Y - pa.Y),
                                            (pb.Z - pa.Z) * (pc.X - pa.X) - (pb.X - pa.X) * (pc.Z - pa.Z),
                                            (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X));
                                        foreach (var v in new[] { a5, b5, c5 })
                                        {
                                            if (!want.Contains(v)) continue;
                                            var had = acc.GetValueOrDefault(v);
                                            acc[v] = new Vec3(had.X + fn.X, had.Y + fn.Y, had.Z + fn.Z);
                                        }
                                    }

                                int reshaded = 0;
                                float worstTurn = 0f;
                                Span<float> tn = stackalloc float[4];
                                foreach (var (v, sum) in acc)
                                {
                                    var nn = NormalizeOr(sum, default);
                                    if (nn is { X: 0, Y: 0, Z: 0 }) continue;
                                    ReadTyped(weld.emitter.outStreams[ne11.Stream], v * weld.emitter.outStrides[ne11.Stream] + ne11.Offset,
                                              ne11.Type, tn);
                                    float ox = tn[0], oy = tn[1], oz = tn[2];
                                    if (ne11.Type == 8) { ox = ox * 2 - 1; oy = oy * 2 - 1; oz = oz * 2 - 1; }
                                    var was = NormalizeOr(new Vec3(ox, oy, oz), nn);
                                    // Keep the emitted winding's sense: the merged mesh is assembled from
                                    // several sources and a flipped face here would invert the shading.
                                    if (nn.X * was.X + nn.Y * was.Y + nn.Z * was.Z < 0)
                                        nn = new Vec3(-nn.X, -nn.Y, -nn.Z);
                                    WriteNormal(weld.emitter.outStreams[ne11.Stream],
                                                v * weld.emitter.outStrides[ne11.Stream] + ne11.Offset, ne11.Type,
                                                nn.X, nn.Y, nn.Z);
                                    worstTurn = MathF.Max(worstTurn,
                                        MathF.Acos(Math.Clamp(nn.X * was.X + nn.Y * was.Y + nn.Z * was.Z,
                                                              -1f, 1f)) * 180f / MathF.PI);
                                    reshaded++;
                                }
                                if (reshaded > 0)
                                    weld.emitter.build.diag?.Invoke($"authored cap: re-shaded {reshaded} interior vertices from "
                                               + $"the cap's own surface instead of the body it was projected "
                                               + $"onto (furthest turn {worstTurn:F1} degrees); the rim keeps "
                                               + "the weld's normals so the join still shades as one surface");
                            }

                        }
                    }
                }
            }
        }
    }
}
