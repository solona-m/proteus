using System;

namespace Proteus.Services;
using static Proteus.Services.BodyBridge;
using static Proteus.Services.ToeCapSolver;

public static partial class SecondSkinWriter
{
    private sealed class VerbatimCopy
    {
        private readonly byte[] s;
        private readonly int vb;
        private readonly int srcDeclOff;
        private readonly ushort vc;
        private readonly VElem[] decl;
        private readonly uint[] vbo;
        private readonly byte[] bs;
        private readonly float push;
        private readonly UVRemapService.UvConversion? uvConv;
        private readonly sbyte[]? sides;
        private readonly SecondSkinLayer? cap;
        private readonly ushort[]? capTris;
        private readonly Action<string>? capLog;
        private readonly bool buildCapGeometry;
        private readonly Func<Vec3[], Vec3[], ushort[], (float U, float V)[], BustBridgePlan?>? bridge;
        private readonly PushSweep? pushSweep;
        private ushort[]? spanTris;
        // A hand's nail beds and the fingertip UV under each — see NailBeds.
        private readonly NailBedPlan? nailBeds;
        private int uvUnmapped;
        private VElem? pos;
        private VElem? norm;
        private VElem? uv0;
        private VElem? uv1El;
        private VElem? col;
        private int streamCount;
        private Uv1Plan uv1Plan;
        private Vec3[]? basePos;
        private Vec3[]? baseNrm;

        public VerbatimCopy(byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, float push, UVRemapService.UvConversion? uvConv, sbyte[]? sides, SecondSkinLayer? cap, ushort[]? capTris, Action<string>? capLog, bool buildCapGeometry, Func<Vec3[], Vec3[], ushort[], (float U, float V)[], BustBridgePlan?>? bridge, PushSweep? pushSweep, ushort[]? spanTris, NailBedPlan? nailBeds = null)
        {
            this.nailBeds = nailBeds;
            this.s = s;
            this.vb = vb;
            this.srcDeclOff = srcDeclOff;
            this.vc = vc;
            this.decl = decl;
            this.vbo = vbo;
            this.bs = bs;
            this.push = push;
            this.uvConv = uvConv;
            this.sides = sides;
            this.cap = cap;
            this.capTris = capTris;
            this.capLog = capLog;
            this.buildCapGeometry = buildCapGeometry;
            this.bridge = bridge;
            this.pushSweep = pushSweep;
            this.spanTris = spanTris;
        }

        public int Run(out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock, out (float U, float V)[] uvs, out (float U, float V)[]? uvsPreConv, out Vec3[]? capSrcPos, out Vec3[]? capOutPos, out ToeCapPlan? capPlan)
        {
            outStreams = null!;
            outStrides = null!;
            declBlock = null!;
            uvs = null!;
            uvsPreConv = null;
            capSrcPos = null;
            capOutPos = null;
            capPlan = null;
            FindElements(ref uvsPreConv, ref capPlan);
            AllocateStreams(ref outStreams, ref outStrides, ref uvs);
            CopyVertices(ref outStreams, ref outStrides, ref uvs);
            NormalizeUv(ref outStreams, ref outStrides, ref uvs, ref uvsPreConv);
            WritePositions(ref outStreams, ref outStrides, ref uvs, ref capSrcPos, ref capOutPos, ref capPlan);
            return WriteDeclaration(ref declBlock);
        }

        private void FindElements(ref (float U, float V)[]? uvsPreConv, ref ToeCapPlan? capPlan)
        {
            uvUnmapped = 0;
            uvsPreConv = null;
            // The spans' topology: the shape-baked triangle list when the caller has one, else the mesh's own.
            spanTris ??= capTris;
            capPlan = null;
            pos = null;
            norm = null;
            uv0 = null;
            uv1El = null;
            col = null;
            foreach (var el in decl)
                switch (el.Usage)
                {
                    case UsePosition: pos ??= el; break;
                    case UseNormal:   norm ??= el; break;
                    case UseColor:    col ??= el; break;
                    case UseUV:       if (el.UsageIndex == 0) uv0 ??= el; else uv1El ??= el; break;
                }
        }

        private void AllocateStreams(ref byte[][] outStreams, ref byte[] outStrides, ref (float U, float V)[] uvs)
        {
            // Emit every stream that carries data OR is named by a decl element, so the writes below never index
            // past the arrays.
            streamCount = bs[2] > 0 ? 3 : (bs[1] > 0 ? 2 : 1);
            foreach (var el in decl) streamCount = Math.Max(streamCount, Math.Min((int)el.Stream, 2) + 1);

            // The scroll shader reads uv1, so every uv1 slot MIRRORS uv0; a Float2 uv1 is appended into uv0's own
            // stream only when there is no slot at all. See Uv1Plan.
            uv1Plan = PlanUv1(uv0, uv1El, bs);

            outStrides = new byte[streamCount];
            for (int st = 0; st < streamCount; st++) outStrides[st] = bs[st];
            if (uv1Plan.Append) outStrides[uv1Plan.Stream] = (byte)(bs[uv1Plan.Stream] + uv1Plan.ExtraBytes);
            outStreams = new byte[streamCount][];
            for (int st = 0; st < streamCount; st++) outStreams[st] = new byte[vc * outStrides[st]];

            uvs = new (float, float)[vc];
        }

        private void CopyVertices(ref byte[][] outStreams, ref byte[] outStrides, ref (float U, float V)[] uvs)
        {
            Span<float> tmp = stackalloc float[4];
            // Positions and normalized normals are decoded here but written AFTER the UV pass, because the
            // toe cap displaces them and it samples its mask with the normalized UV.
            basePos = null;
            baseNrm = null;
            if (pos is not null && norm is not null) { basePos = new Vec3[vc]; baseNrm = new Vec3[vc]; }

            for (int i = 0; i < vc; i++)
            {
                for (int st = 0; st < streamCount; st++)
                    Array.Copy(s, vb + (int)vbo[st] + i * bs[st], outStreams[st], i * outStrides[st], bs[st]);

                if (basePos is not null && baseNrm is not null && pos is { } pe && norm is { } ne)
                {
                    ReadTyped(s, SrcAddr(ne.Stream, i, ne.Offset), ne.Type, tmp);
                    float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                    if (ne.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
                    baseNrm[i] = new Vec3(nx, ny, nz);
                    ReadTyped(s, SrcAddr(pe.Stream, i, pe.Offset), pe.Type, tmp);
                    basePos[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
                }

                // Force vertex colour white so the gear shader's emissive isn't gated off.
                if (col is { } ce)
                {
                    int o = i * outStrides[ce.Stream] + ce.Offset;
                    outStreams[ce.Stream][o] = outStreams[ce.Stream][o + 1]
                        = outStreams[ce.Stream][o + 2] = outStreams[ce.Stream][o + 3] = 0xFF;
                }

                // Decode uv0 (raw) — normalized and written below, once the mesh's UV cell is known.
                if (uv0 is { } ue) { ReadTyped(s, SrcAddr(ue.Stream, i, ue.Offset), ue.Type, tmp); uvs[i] = (tmp[0], tmp[1]); }
            }
        }

        private void NormalizeUv(ref byte[][] outStreams, ref byte[] outStrides, ref (float U, float V)[] uvs, ref (float U, float V)[]? uvsPreConv)
        {
            // Normalize the mesh's UV onto the [0,1] tile by the integer floor of its minimum, per MESH so islands
            // stay together (a body UV can live in another cell). Then write uv0 and every uv1 slot with it.
            if (uv0 is { } u0e)
            {
                float minU = float.MaxValue, minV = float.MaxValue;
                for (int i = 0; i < vc; i++) { minU = MathF.Min(minU, uvs[i].U); minV = MathF.Min(minV, uvs[i].V); }
                float uOff = MathF.Floor(minU), vOff = MathF.Floor(minV);
                bool uv0Half = u0e.Type is 13 or 14;
                if (uvConv != null) uvsPreConv = new (float, float)[vc];
                for (int i = 0; i < vc; i++) uvs[i] = ShiftAndConvert(i, uvs[i], uOff, vOff, uvsPreConv);
                RescueNailBeds(uvs, uOff, vOff, uvsPreConv);
                for (int i = 0; i < vc; i++)
                {
                    var (u, v) = uvs[i];
                    WriteUV2(outStreams[u0e.Stream], i * outStrides[u0e.Stream] + u0e.Offset, uv0Half, u, v);
                    WriteUv1(uv1Plan, u0e, outStreams, outStrides, i, u, v);   // the SHIFTED value, unlike content
                }
            }
        }

        /// <summary>One raw uv0 onto the coverage map's tile, then into the SHELL's space when the part is in
        /// another body's: the maps are indexed over [0,1], so the conversion follows the tile shift. A vertex the
        /// maps can't place keeps its original UV.</summary>
        private (float U, float V) ShiftAndConvert(int i, (float U, float V) raw, float uOff, float vOff,
                                                   (float U, float V)[]? uvsPreConv)
        {
            var (uv, pre, mapped) = Converted(i, raw, uOff, vOff);
            if (uvsPreConv != null) uvsPreConv[i] = pre;
            if (!mapped) uvUnmapped++;
            return uv;
        }

        /// <summary><see cref="ShiftAndConvert"/> without recording anything: the UV, the UV before conversion, and
        /// whether the maps could place it.</summary>
        private ((float U, float V) Uv, (float U, float V) Pre, bool Mapped) Converted(int i, (float U, float V) raw,
                                                                                     float uOff, float vOff)
        {
            float u = raw.U - uOff, v = raw.V - vOff;
            var pre = (u, v);
            if (uvConv == null) return ((u, v), pre, true);
            var moved = uvConv(u, v, sides != null && i < sides.Length ? sides[i] : 0);
            return moved is { } mv ? ((mv.U, mv.V), pre, true) : ((u, v), pre, false);
        }

        /// <summary>
        /// A nail bed takes the UV of the fingertip around it wherever this layer paints that fingertip: the bed is a
        /// separate little island in the atlas, and what art puts there is the NAIL — bare, or painted as a nail —
        /// which is what shows through a glove. Coverage is only asked of the fingertip: an island the art leaves
        /// clear would be trimmed away, one it paints would be drawn as a nail, and the glove wants neither. A
        /// fingerless glove still leaves the nails bare, since the fingertip around them is unpainted too.
        /// </summary>
        private void RescueNailBeds((float U, float V)[] uvs, float uOff, float vOff, (float U, float V)[]? uvsPreConv)
        {
            if (nailBeds is not { } nb || cap is not { Coverage: not null }) return;
            int rescued = 0;
            float ownSum = 0f, tipSum = 0f;
            for (int k = 0; k < nb.Islands.Count; k++)
            {
                // Mean coverage over the island's vertices, where it sits and where it would land.
                float own = 0f, tip = 0f;
                int n = 0;
                foreach (int i in nb.VertsOf[k])
                {
                    if (nb.FingertipUv[i] is not { } t) continue;
                    own += CoverageAt(cap, uvs[i]);
                    tip += CoverageAt(cap, Converted(i, t, uOff, vOff).Uv);
                    n++;
                }
                if (n == 0) continue;
                own /= n; tip /= n;
                ownSum += own; tipSum += tip;
                if (tip < NailBedPaintedFloor) continue;

                foreach (int i in nb.VertsOf[k])
                    if (nb.FingertipUv[i] is { } t)
                        uvs[i] = ShiftAndConvert(i, t, uOff, vOff, uvsPreConv);
                rescued++;
            }
            capLog?.Invoke($"nail beds: {rescued} of {nb.Islands.Count} under painted fingertips on this layer "
                         + $"(mean coverage {ownSum / Math.Max(1, nb.Islands.Count):F0} on the nails against "
                         + $"{tipSum / Math.Max(1, nb.Islands.Count):F0} on the fingers), moved onto the fingertip's UV");
        }

        private void WritePositions(ref byte[][] outStreams, ref byte[] outStrides, ref (float U, float V)[] uvs, ref Vec3[]? capSrcPos, ref Vec3[]? capOutPos, ref ToeCapPlan? capPlan)
        {
            // Position write-back: base + cap displacement + bridge, then the push along the normal. The cap needs
            // the topology and a UV, so it can only run here.
            capSrcPos = null;
            capOutPos = null;
            if (basePos is not null && baseNrm is not null && pos is { } pw)
            {
                var plan = uv0 is not null && cap is { ToeCap: { } tc } && capTris is not null
                    ? ToeCapSolve(basePos, baseNrm, uvs, capTris, tc, cap.ToeCapWidth, cap.ToeCapHeight,
                                  cap.ToeCapStrength, capLog, buildCapGeometry)
                    : null;
                var delta = plan?.Delta;
                capPlan = plan;

                // The bust bridge, resolved through the caller's memo so every layer of a host shares one answer; it
                // can only run once uvs[] is on the tile.
                var bridgePlan = bridge is not null && spanTris is not null
                    ? bridge(basePos, baseNrm, spanTris, uvs)
                    : null;

                // Normals recomputed from the rebuilt surface: a flattened span carrying the cleavage's normals still
                // shades as a cleavage.
                var finalNrm = plan is not null
                    ? CapNormals(basePos, baseNrm, plan, CappedTopology(plan, capTris!))
                    : bridgePlan is not null
                        ? ApplyNormalOverride(bridgePlan,
                                              RelaxedNormals(basePos, baseNrm, bridgePlan.Delta, bridgePlan.NodeOf,
                                                             bridgePlan.NodeWeight, bridgePlan.NodeNormal, spanTris!),
                                              baseNrm)
                        : baseNrm;

                int stride = outStrides[pw.Stream];
                int normalsWritten = 0, uvsWritten = 0;
                bool encoderMissing = false;
                var outPos = plan is null ? null : new Vec3[vc];
                var bridgeExtra = bridgePlan is not null && spanTris is not null
                    ? BridgedClearance(bridgePlan, spanTris)
                    : null;

                for (int i = 0; i < vc; i++)
                {
                    var p = basePos[i];
                    var n = finalNrm[i];
                    if (delta is not null) p = new Vec3(p.X + delta[i].X, p.Y + delta[i].Y, p.Z + delta[i].Z);
                    if (bridgePlan is not null)
                    {
                        var bd = bridgePlan.Delta[i];
                        p = new Vec3(p.X + bd.X, p.Y + bd.Y, p.Z + bd.Z);
                    }

                    // Banded by the vertex's height BEFORE the cap or bridge moved it, so a displacement cannot
                    // carry a vertex across a band edge and step the surface somewhere the ladder did not put one.
                    // The sweep REPLACES the shipped foot band rather than compounding with it: it is a measuring
                    // instrument, and the millimetres it announces have to be the millimetres it applied.
                    float pushHere = push * (pushSweep is null ? FootPushAt(basePos[i].Y) : pushSweep.Take(basePos[i].Y));
                    // Clearance given back where the bridge moved the surface — see BridgedClearance. ADDED, not a floor,
                    // so stacked layers stay LayerSeparation apart.
                    if (bridgeExtra is not null)
                        pushHere += bridgeExtra[i];
                    // Clear of a nail mesh standing on the bed — see NailBedPlan.Lift.
                    if (nailBeds is { } nb && i < nb.Lift.Length)
                        pushHere += nb.Lift[i];
                    var final = new Vec3(p.X + n.X * pushHere, p.Y + n.Y * pushHere, p.Z + n.Z * pushHere);
                    WriteXYZ(outStreams[pw.Stream], i * stride + pw.Offset, pw.Type, final.X, final.Y, final.Z);
                    if (outPos is not null) outPos[i] = final;

                    // Only vertices the cap or the bridge actually reached get a new normal; everything else
                    // keeps the bytes it arrived with. The normal element has its own stream — not pos's.
                    bool reshade = plan is not null && plan.NodeWeight[plan.NodeOf[i]] > 0f
                                || bridgePlan is not null && bridgePlan.NodeWeight[bridgePlan.NodeOf[i]] > 0f;
                    if (reshade && norm is { } ne2)
                    {
                        if (WriteNormal(outStreams[ne2.Stream], i * outStrides[ne2.Stream] + ne2.Offset, ne2.Type,
                                n.X, n.Y, n.Z))
                            normalsWritten++;
                        else
                            encoderMissing = true;
                    }

                    // ...and the UV it was projected onto, written into every uv slot. uvs[] is updated too: the coverage
                    // test reads it.
                    if (plan is not null && plan.NodeUV is { } capUV && uv0 is { } u0w
                        && plan.NodeWeight[plan.NodeOf[i]] > 0f)
                    {
                        var (cu, cv) = capUV[plan.NodeOf[i]];
                        uvs[i] = (cu, cv);
                        int so2 = i * outStrides[u0w.Stream];
                        bool half0 = u0w.Type is 13 or 14;
                        WriteUV2(outStreams[u0w.Stream], so2 + u0w.Offset, half0, cu, cv);
                        WriteUv1(uv1Plan, u0w, outStreams, outStrides, i, cu, cv);
                        uvsWritten++;
                    }
                }

                // A vertex the bridge slid across the skin takes the skinning of the skin it now sits over — see
                // ReskinBridged. After the position loop, which never reads weights, and before anything copies
                // these streams on.
                if (bridgePlan is not null && spanTris is not null)
                {
                    int reskinned = ReskinBridged(basePos, bridgePlan, spanTris, decl, outStreams, outStrides);
                    if (reskinned > 0)
                        capLog?.Invoke($"bust bridge: reskinned {reskinned} moved vertex/vertices from the skin they now sit over");
                }

                if (plan is not null)
                {
                    capSrcPos = basePos;
                    capOutPos = outPos;
                }

                // Report what the cap did. "0 moved" on a mesh that should hold the toes means the mask
                // missed the UV; "0 normals" means it moved geometry nobody will see move.
                if (capLog != null && cap?.ToeCap != null)
                {
                    int moved = 0;
                    float max = 0f;
                    if (delta != null)
                        foreach (var d in delta)
                        {
                            float m = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                            if (m > 1e-7f) moved++;
                            max = MathF.Max(max, m);
                        }
                    capLog($"toe cap: {moved}/{vc} vertices moved, max {max:0.#####}, {normalsWritten} normals rewritten"
                         + (plan is null ? "" : $", {plan.NewTriangles.Count} triangles rebuilt")
                         + (uvsWritten == 0 ? "" : $", {uvsWritten} uvs reprojected"));
                    if (encoderMissing)
                        capLog($"toe cap: no encoder for normal type {norm?.Type} — that mesh keeps its old shading");
                }

                // A cap and a bridge on ONE mesh: the cap's normals win, the bridge moves positions only. No body seen
                // so far puts both on one mesh.
                if (plan is not null && bridgePlan is not null)
                    capLog?.Invoke("bust bridge: this mesh also carries a toe cap — the cap's normals win, the "
                                 + "bridge moves positions only");
            }
        }

        private int WriteDeclaration(ref byte[] declBlock)
        {
            // Declaration: copy the source mesh's block verbatim, splicing in a uv1 element only when we
            // appended one (the .zw / existing-uidx1 cases already declare their uv1).
            declBlock = new byte[DeclSize];
            Array.Copy(s, srcDeclOff, declBlock, 0, DeclSize);
            SpliceUv1Decl(declBlock, uv1Plan);
            return uvUnmapped;
        }

        private int SrcAddr(int st, int i, int off) => vb + (int)vbo[st] + i * bs[st] + off;
    }
}
