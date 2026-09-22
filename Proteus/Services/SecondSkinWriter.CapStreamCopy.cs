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
            private sealed class CapStreamCopy
            {
                private readonly MeshEmitter emitter;
                private int nvNew;
                private VElem? u0;
                private VElem? u1;

                public CapStreamCopy(MeshEmitter emitter)
                {
                    this.emitter = emitter;
                }

                public void Run()
                {
                    CopyVerbatim(emitter.s, emitter.src.Vb, 0x44 + emitter.m * DeclSize, emitter.vc, emitter.decl, emitter.vbo, emitter.bs, emitter.mirrorUv1,
                        out emitter.outStreams, out emitter.outStrides, out emitter.declBlock);
                    // The grafted cap is trimmed the same way the shell is; the host accessory has no coordinate to trim by.
                    emitter.uv = emitter.capUv is { } uvPlan ? uvPlan.Uv : [];

                    // This path copies bytes, so a supplied UV has to be written explicitly; the authored cap arrives with
                    // every vertex at (0,1).
                    if (emitter.capUv is { } tooBig && tooBig.SourceOf.Length > ushort.MaxValue)
                        emitter.build.diag?.Invoke($"authored cap: {tooBig.SourceOf.Length} vertices after the seam split "
                                   + "exceeds a 16-bit index — UVs NOT applied, the cap will render blank");
                    if (emitter.capUv is { } plan && plan.SourceOf.Length <= ushort.MaxValue)
                    {
                        ExpandStreams(plan);
                        PlaceFromBinding(plan);
                        ReskinToBody(plan);
                        PushCap(plan);
                        WriteCapUv(plan);
                    }
                }

                private void ExpandStreams(CapUvPlan plan)
                {
                    // The projection cuts the body's UV seams into the cap, so a seam vertex exists once per chart as a
                    // byte-for-byte copy.
                    nvNew = plan.SourceOf.Length;
                    var grownStreams = new byte[emitter.outStreams.Length][];
                    for (int st = 0; st < emitter.outStreams.Length; st++)
                    {
                        int stride = emitter.outStrides[st];
                        var g = new byte[nvNew * stride];
                        for (int i = 0; i < nvNew; i++)
                        {
                            int from = plan.SourceOf[i];
                            if (from >= 0 && from < emitter.vc)
                                Buffer.BlockCopy(emitter.outStreams[st], from * stride, g, i * stride, stride);
                        }
                        grownStreams[st] = g;
                    }
                    emitter.outStreams = grownStreams;
                    emitter.vc = (ushort)nvNew;
                }

                private void PlaceFromBinding(CapUvPlan plan)
                {
                    // The binding's placement, written before the push and the weld. Indexed through SourceOf because
                    // the binding is per authored vertex.
                    if (emitter.build.capPlaced != null && emitter.build.capPlaced.TryGetValue(emitter.m, out var place))
                    {
                        VElem? pP = null, pN = null;
                        foreach (var el in emitter.decl)
                        {
                            if (el.Usage == UsePosition) pP ??= el;
                            if (el.Usage == UseNormal) pN ??= el;
                        }
                        if (pP is { } pe4)
                        {
                            int moved = 0;
                            for (int i = 0; i < nvNew; i++)
                            {
                                int from = plan.SourceOf[i];
                                if (from < 0 || from >= place.Pos.Length) continue;
                                WriteXYZ(emitter.outStreams[pe4.Stream], i * emitter.outStrides[pe4.Stream] + pe4.Offset,
                                         pe4.Type, place.Pos[from].X, place.Pos[from].Y, place.Pos[from].Z);
                                if (pN is { } ne8)
                                    WriteNormal(emitter.outStreams[ne8.Stream],
                                                i * emitter.outStrides[ne8.Stream] + ne8.Offset, ne8.Type,
                                                place.Nrm[from].X, place.Nrm[from].Y, place.Nrm[from].Z);
                                moved++;
                            }
                            emitter.build.diag?.Invoke($"authored cap: mesh {emitter.m} placed onto the equipped body, "
                                       + $"{moved} vertices moved, {place.Missed} left as authored");
                        }
                    }
                }

                private void ReskinToBody(CapUvPlan plan)
                {
                    // RESKIN THE CAP TO THE BODY: coincident positions with different weights separate the moment a toe
                    // bends. The bone table grows to fit, since an index is only meaningful against its own table.
                    if (plan.Weights.Length == nvNew)
                    {
                        VElem? wEl2 = null, iEl2 = null;
                        foreach (var el in emitter.decl)
                        {
                            if (el.Usage == UseBlendWeight) wEl2 ??= el;
                            if (el.Usage == UseBlendIndices) iEl2 ??= el;
                        }
                        // Widen a four-influence cap to eight: both sides of a weld must deform the same way. Only when
                        // stream 0 holds exactly position, weights and indices; otherwise skipped rather than guessed.
                        if (wEl2 is { } wUp && BlendCount(wUp.Type) == 4 && emitter.WidenToEight(nvNew))
                        {
                            wEl2 = emitter.decl.First(e => e.Usage == UseBlendWeight);
                            iEl2 = emitter.decl.First(e => e.Usage == UseBlendIndices);
                            emitter.build.diag?.Invoke("authored cap: widened to 8 bone influences to match the shell");
                        }

                        if (wEl2 is { } we5 && iEl2 is { } ie5)
                        {
                            var srcTbl = emitter.srcBoneTbl < emitter.src.BoneTables.Length ? emitter.src.BoneTables[emitter.srcBoneTbl] : [];
                            var tbl = new List<ushort>();
                            var slot = new Dictionary<string, int>();
                            foreach (var bi in srcTbl)
                            {
                                var nmB = bi < emitter.src.BoneNames.Length ? emitter.src.BoneNames[bi] : null;
                                if (nmB != null && emitter.build.boneIndex.TryGetValue(nmB, out var ui2))
                                { slot.TryAdd(nmB, tbl.Count); tbl.Add(ui2); }
                                else tbl.Add(0);
                            }
                            int reskinned = 0, dropped = 0;
                            int SlotOf(string bone)
                            {
                                if (slot.TryGetValue(bone, out int at)) return at;
                                if (!emitter.build.boneIndex.TryGetValue(bone, out var union) || tbl.Count >= 255) return -1;
                                slot[bone] = tbl.Count;
                                tbl.Add(union);
                                return tbl.Count - 1;
                            }
                            int nInfNow = BlendCount(we5.Type);
                            // Hoisted: a stackalloc per iteration grows the frame by the iteration count.
                            Span<byte> wb2 = stackalloc byte[8], ib2 = stackalloc byte[8];
                            for (int i = 0; i < nvNew; i++)
                            {
                                // KEEP THE AUTHORED SKINNING (it weights to the nails too); only a band at the back seam is blended
                                // toward the body so the cap and the shell agree where they meet.
                                int srcV = plan.SourceOf[i];
                                int ring = srcV >= 0 && srcV < plan.RimRing.Length ? plan.RimRing[srcV] : int.MaxValue;
                                if (ring >= CapSeamBlendRings) continue;
                                float toBody = 1f - ring / (float)CapSeamBlendRings;

                                var body = plan.Weights[i];
                                if (body.Length == 0) continue;

                                // What the author put here, resolved to names through the cap's own table.
                                var mine = new List<(string Bone, float W)>(nInfNow);
                                {
                                    int wa0 = i * emitter.outStrides[we5.Stream] + we5.Offset;
                                    int ia0 = i * emitter.outStrides[ie5.Stream] + ie5.Offset;
                                    for (int q = 0; q < nInfNow; q++)
                                    {
                                        float fw = emitter.outStreams[we5.Stream][wa0 + q] / 255f;
                                        if (fw <= 0f) continue;
                                        int local = emitter.outStreams[ie5.Stream][ia0 + q];
                                        if (local >= srcTbl.Length) continue;
                                        var nm2 = srcTbl[local] < emitter.src.BoneNames.Length
                                            ? emitter.src.BoneNames[srcTbl[local]] : null;
                                        if (nm2 != null) mine.Add((nm2, fw));
                                    }
                                }
                                var w = mine.Count > 0
                                    ? BlendWeights(mine.ToArray(), 1f - toBody, body, toBody, [], 0f)
                                    : body;
                                if (w.Length == 0) continue;
                                // As many influences as THIS element declares; anything not written is zeroed.
                                int nInf = BlendCount(we5.Type);
                                int used2 = EncodeBlend(w, nInf, SlotOf, wb2, ib2, ref dropped);
                                if (used2 == 0) continue;
                                int wo = i * emitter.outStrides[we5.Stream] + we5.Offset;
                                int io = i * emitter.outStrides[ie5.Stream] + ie5.Offset;
                                for (int q2 = 0; q2 < nInf; q2++)
                                {
                                    emitter.outStreams[we5.Stream][wo + q2] = wb2[q2];
                                    emitter.outStreams[ie5.Stream][io + q2] = ib2[q2];
                                }
                                reskinned++;
                            }
                            emitter.capBoneTable = tbl.ToArray();
                            emitter.build.diag?.Invoke($"authored cap: reskinned {reskinned} vertices from the body, "
                                       + $"bone table {srcTbl.Length} -> {tbl.Count}"
                                       + (dropped > 0 ? $", {dropped} influence(s) dropped" : ""));
                        }
                    }
                }

                private void PushCap(CapUvPlan plan)
                {
                    // PUSH. The cap is authored ON the skin; the shell it lands in is pushed off it.
                    VElem? pEl = null, nEl = null;
                    foreach (var el in emitter.decl)
                    {
                        if (el.Usage == UsePosition) pEl ??= el;
                        if (el.Usage == UseNormal) nEl ??= el;
                    }
                    if (pEl is { } pe3 && nEl is { } ne3 && (emitter.push != 0f || emitter.build.shellRim.Count > 0))
                    {
                        Span<float> tmp3 = stackalloc float[4];
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            ReadTyped(emitter.outStreams[ne3.Stream], i * emitter.outStrides[ne3.Stream] + ne3.Offset,
                                      ne3.Type, tmp3);
                            float nx = tmp3[0], ny = tmp3[1], nz = tmp3[2];
                            if (ne3.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                            var n3 = NormalizeOr(new Vec3(nx, ny, nz), default);
                            if (n3 is { X: 0, Y: 0, Z: 0 }) continue;

                            int po = i * emitter.outStrides[pe3.Stream] + pe3.Offset;
                            ReadTyped(emitter.outStreams[pe3.Stream], po, pe3.Type, tmp3);
                            int from2 = plan.SourceOf[i];
                            // A rim vertex takes the coordinate the shell was welded and split against,
                            // verbatim. Everything else is pushed the ordinary way. See weldRimPos.
                            var p3 = emitter.build.weldRimPos.TryGetValue((emitter.m, from2), out var atRim)
                                ? atRim
                                : new Vec3(tmp3[0] + n3.X * emitter.push, tmp3[1] + n3.Y * emitter.push,
                                           tmp3[2] + n3.Z * emitter.push);

                            // The cap keeps its own normal; the shell adopts the cap rim's normal in the weld block instead.
                            if (emitter.build.shellRim.Count > 0 && emitter.build.weldRimVerts.Contains(from2)) emitter.build.capWelded++;
                            WriteXYZ(emitter.outStreams[pe3.Stream], po, pe3.Type, p3.X, p3.Y, p3.Z);
                        }
                    }

                    u0 = null;
                    u1 = null;
                    foreach (var el in emitter.decl)
                        if (el.Usage == UseUV) { if (el.UsageIndex == 0) u0 ??= el; else u1 ??= el; }
                }

                private void WriteCapUv(CapUvPlan plan)
                {
                    if (u0 is { } ue2)
                    {
                        // Shifted onto the [0,1] tile the same way every other mesh is.
                        float minU = float.MaxValue, minV = float.MaxValue;
                        for (int i = 0; i < emitter.vc; i++)
                        { minU = MathF.Min(minU, plan.Uv[i].U); minV = MathF.Min(minV, plan.Uv[i].V); }
                        float capUOff = MathF.Floor(minU), capVOff = MathF.Floor(minV);
                        bool half0 = ue2.Type is 13 or 14;
                        bool zwOk = ue2.Type is 3 or 14;
                        int zwOff2 = ue2.Offset + (ue2.Type == 3 ? 8 : 4);
                        for (int i = 0; i < emitter.vc; i++)
                        {
                            float u = plan.Uv[i].U - capUOff, v = plan.Uv[i].V - capVOff;
                            int so = i * emitter.outStrides[ue2.Stream];
                            WriteUV2(emitter.outStreams[ue2.Stream], so + ue2.Offset, half0, u, v);
                            if (zwOk) WriteUV2(emitter.outStreams[ue2.Stream], so + zwOff2, ue2.Type == 14, u, v);
                            if (u1 is { } ue3)
                                WriteUV2(emitter.outStreams[ue3.Stream], i * emitter.outStrides[ue3.Stream] + ue3.Offset,
                                         ue3.Type is 13 or 14, u, v);
                        }
                    }
                }
            }
        }
    }
}
