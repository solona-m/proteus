using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.BodyBridge;
using static Proteus.Services.ToeCapSolver;

public static partial class SecondSkinWriter
{
    private sealed partial class ShellBuild
    {
        private sealed partial class MeshEmitter
        {
            private readonly ShellBuild build;
            private readonly Source src;
            private readonly int m;
            private readonly ushort materialIndex;
            private readonly float push;
            private readonly bool preserve;
            private readonly SecondSkinLayer? cov;
            private readonly int mapBase;
            private readonly bool mirrorUv1;
            private readonly IReadOnlySet<string>? hiddenAttrs;
            private readonly bool clearAttrs;
            private readonly bool dropVariantAttrs;

            /// <summary>New skinning for this (host) mesh, per vertex; null leaves it as authored.</summary>
            private readonly (string Bone, float W)[]?[]? reskin;
            private readonly CapUvPlan? capUv;
            private byte[] s = null!;
            private int mo;
            private ushort vc;
            private ushort srcSubIdx;
            private ushort srcSubCount;
            private ushort srcBoneTbl;
            private uint[] vbo = null!;
            private byte[] bs = null!;
            private VElem[] decl = null!;
            private byte[][] outStreams = null!;
            private byte[] outStrides = null!;
            private byte[] declBlock = null!;
            private ushort[]? capBoneTable;
            private (float U, float V)[] uv = null!;
            private Vec3[]? capSrcPos;
            private Vec3[]? capOutPos;
            private ToeCapPlan? capPlan;
            private (float U, float V)[]? uvPre;
            private uint meshStartIndex;
            private Dictionary<int, ushort>? shapeReplace;
            private List<ushort[]> keptPerSub = null!;
            private bool[] used = null!;
            private bool[] cutAway = null!;
            private int cutTris;
            private int cornerCursor;
            private int streamCount;
            private ushort[] remap = null!;
            private ushort nv;
            private uint[] vOff = null!;
            private uint meshStartIdx;
            private ushort keptSubs;
            private List<byte[]> subsForMesh = null!;

            public MeshEmitter(ShellBuild build, Source src, int m, ushort materialIndex, float push, bool preserve, SecondSkinLayer? cov, int mapBase, bool mirrorUv1, IReadOnlySet<string>? hiddenAttrs, bool clearAttrs, CapUvPlan? capUv, bool dropVariantAttrs = false,
                               (string Bone, float W)[]?[]? reskin = null)
            {
                this.reskin = reskin;
                this.dropVariantAttrs = dropVariantAttrs;
                this.build = build;
                this.src = src;
                this.m = m;
                this.materialIndex = materialIndex;
                this.push = push;
                this.preserve = preserve;
                this.cov = cov;
                this.mapBase = mapBase;
                this.mirrorUv1 = mirrorUv1;
                this.hiddenAttrs = hiddenAttrs;
                this.clearAttrs = clearAttrs;
                this.capUv = capUv;
            }

            public void Run(ref bool mapAppended)
            {
                if (!ReadMesh()) return;
                CopyStreams();
                ReskinHost();
                KeepTriangles();
                if (!FinishTriangles()) return;
                WeldToCapRim();
                AppendBoneMap(ref mapAppended);
                CompactStreams();
                RebuildBoneMap();
                WriteMesh();
            }

            private bool ReadMesh()
            {
                s = src.S;

                mo = src.MeshStart + m * 36;
                vc = U16(mo);
                if (vc == 0) return false;

                srcSubIdx = U16(mo + 10);
                srcSubCount = U16(mo + 12);
                srcBoneTbl = U16(mo + 14);
                // Up to three vertex streams (offset + stride each); a v6 MeshStruct carries all three.
                vbo = new uint[] { U32(mo + 20), U32(mo + 24), U32(mo + 28) };
                bs = new byte[] { s[mo + 32], s[mo + 33], s[mo + 34] };
                decl = m < src.Decls.Length ? src.Decls[m] : [];

                  
                // Set when the cap is reskinned: its blend indices then address THIS table.
                capBoneTable = null;
                
                capSrcPos = null;
                capOutPos = null;   // set only where a toe cap actually moved geometry
                capPlan = null;
                uvPre = null;
                return true;
            }

            private void CopyStreams()
            {
                if (preserve)
                    CopyPreservedStreams();
                else
                    CopyShellStreams();
            }

            /// <summary>Host and cap meshes: the source streams byte for byte, with the cap reskinned and its UVs written.</summary>
            private void CopyPreservedStreams()
            {
                new CapStreamCopy(this).Run();
            }

            /// <summary>Shell meshes: the source streams pushed off the body, with bridge weights and UV conversion applied.</summary>
            private void CopyShellStreams()
            {
                // The toe cap smooths across the mesh's own topology, so it needs the triangle list BEFORE coverage trimming.
                bool wantCap = cov is { ToeCap: not null } && cov.ToeCapStrength > 0f;
                // Bust membership is settled from the mesh's BONE TABLE before any vertex is read. Asked of the HOST's
                // shared definition, memoised per mesh. The nipple smooth is a body-side pass and must not drag the
                // chest solve in.
                bool wantBust  = build.bridgeDef != null && cov is { } cl  && cl.BustBridgeStrength  > 0f;
                bool wantCleft = build.cleftDef  != null && cov is { } cl2 && cl2.CleftBridgeStrength > 0f;
                bool wantFold  = build.foldDef   != null && cov is { } cl3 && cl3.FoldSmoothStrength  > 0f && !preserve;

                float[]? bustWeights = null, cleftWeights = null, foldWeights = null;
                // The cleft's own weights — the same bones and the same thigh veto — so the two share one cache entry.
                if (wantFold && !build.cleftWeightCache.TryGetValue((src, m), out foldWeights))
                    build.cleftWeightCache[(src, m)] = foldWeights =
                        MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone], [ThighBoneL, ThighBoneR]);
                if (wantBust)
                {
                    if (!build.bridgeWeights.TryGetValue((src, m), out bustWeights))
                        build.bridgeWeights[(src, m)] = bustWeights =
                            MeshRegionWeights(src, m, vc, decl, vbo, bs, BustBones);
                }
                // The cleft is its own seed on its own mesh (the legs part), so it never shares a call with the bust.
                if (wantCleft)
                {
                    if (!build.cleftWeightCache.TryGetValue((src, m), out cleftWeights))
                        build.cleftWeightCache[(src, m)] = cleftWeights =
                            MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone], [ThighBoneL, ThighBoneR]);
                }
                var capTris = wantCap || bustWeights != null || cleftWeights != null || foldWeights != null
                    ? MeshTriangles(src, srcSubIdx, srcSubCount)
                    : null;
                // The spans solve on the shape-baked surface, the one the game draws: in the unshaped list the morphed
                // vertices have no triangles at all. The toe cap keeps the list it was built against.
                var spanTris = capTris is not null && (bustWeights != null || cleftWeights != null || foldWeights != null) && !preserve
                    && ShapeReplacements(src, U32(mo + 16), vc) is { } spanShape
                        ? ShapedTriangles(src, srcSubIdx, srcSubCount, U32(mo + 16), spanShape)
                        : capTris;

                // The solve itself, deferred until BuildVerbatim has normalised the mesh's UVs onto the
                // tile the coverage map is indexed over — it cannot run before that and must not run twice.
                Func<Vec3[], Vec3[], ushort[], (float U, float V)[], BustBridgePlan?>? bridge = null;
                if (bustWeights != null || cleftWeights != null || foldWeights != null)
                {
                    // The definitions travel with their weights: a weight array is only non-null when its definition was.
                    var bw = bustWeights;   var bDef = build.bridgeDef;
                    var cw = cleftWeights;  var cDef = build.cleftDef;
                    var fw = foldWeights;   var fDef = build.foldDef;
                    var key = (src, m, bw != null, cw != null, fw != null);
                    bridge = (bPos, bNrm, bTris, bUv) =>
                    {
                        if (build.bridgePlans.TryGetValue(key, out var cached)) return cached;

                        // Each pass is gated by the union of the layers that asked for IT.
                        var tSolve = PhaseCounter.Begin();
                        var plan = bw == null || bDef == null ? null
                            : BustBridgeSolve(bPos, bNrm, bTris, bw, build.bridgeStrength, build.diag,
                                              CoveredVertices(bUv, bDef, bPos.Length), smoothStrength,
                                              openSlope: BustOpenSlope);
                        if (bw != null && bDef != null) build.timings?.Bust.Stop(tSolve);

                        if (cw != null && cDef != null)
                        {
                            tSolve = PhaseCounter.Begin();
                            // Gated on a COPY: the weights are memoised per mesh and shared by every layer of the host.
                            var backOnly = (float[])cw.Clone();
                            GateToBackFacing(backOnly, bNrm, bPos);
                            // The seed doubles as the RAMP: these weights already describe the waist and flank boundaries smoothly,
                            // where BustRegionWeights' one-ring fade would draw a seam.
                            plan = MergePlans(plan,
                                BustBridgeSolve(bPos, bNrm, bTris, backOnly, build.cleftStrength, build.diag,
                                                CoveredVertices(bUv, cDef, bPos.Length),
                                                smoothStrength: 0f, fillGap: false,
                                                ramp: backOnly, minDepthShare: CleftMinDepthShare,
                                                joinPin: JoinPins(bPos, src.JoinRing), rampFull: CleftRampFull,
                                                maxSlope: CleftMaxSlope));
                            build.timings?.Cleft.Stop(tSolve);
                        }

                        // FLAT ACROSS THE CROTCH, on the garment: the body's fold cannot bridge the notch, so the garment is
                        // laid across it. See CrotchFlatAcross.
                        if (fw != null && fDef != null)
                        {
                            tSolve = PhaseCounter.Begin();
                            plan = MergePlans(plan,
                                CrotchFlatAcross(bPos, bNrm, bTris, fw, CoveredVertices(bUv, fDef, bPos.Length),
                                                 build.foldStrength, build.diag));
                            build.timings?.Crotch.Stop(tSolve);
                        }

                        build.bridgePlans[key] = plan;
                        return plan;
                    };
                }
                // Which side of the body each vertex is on, read from the triangles: midline vertices sit at x ~ 0
                // and cannot answer for themselves. Only computed when a layer un-mirrors.
                sbyte[]? sides = null;
                if (src.UnmirrorSides && src.UvConv != null)
                {
                    sides = MeshSides(src, m, vc, decl, vbo, bs, out int sideConflicts, out int sideStraddling);
                    if (sideConflicts > 0 || sideStraddling > 0)
                        // Not fatal: a disputed vertex converts as if on the +X side. A mirrored layout should have neither.
                        build.diag?.Invoke($"mesh {m}: {sideConflicts} vertex(es) claimed by both sides and "
                                   + $"{sideStraddling} triangle(s) straddling the midline — those keep the +X half");
                }

                build.uvUnmapped += BuildVerbatim(s, src.Vb, 0x44 + m * DeclSize, vc, decl, vbo, bs, push,
                    out outStreams, out outStrides, out declBlock, out uv, out uvPre, src.UvConv,
                    out capSrcPos, out capOutPos, out capPlan, sides, cov, capTris, build.diag,
                    buildCapGeometry: build.capSrc == null, bridge: bridge, pushSweep: build.pushSweep, spanTris: spanTris,
                    nailBeds: cov != null ? build.NailBedsOf(src, m) : null);
                if (src.UvConv != null) build.uvMoved += vc;

                // The tile shift brings a mesh onto [0,1] only if it sits inside one integer cell. Reported, not
                // corrected: no body model has needed a per-island fix.
                if (uv.Length > 0)
                {
                    float uLo = float.MaxValue, uHi = float.MinValue, vLo = float.MaxValue, vHi = float.MinValue;
                    foreach (var (cu, cv) in uv)
                    {
                        if (cu < uLo) uLo = cu; if (cu > uHi) uHi = cu;
                        if (cv < vLo) vLo = cv; if (cv > vHi) vHi = cv;
                    }
                    if (uHi - uLo > 1f || vHi - vLo > 1f)
                        build.diag?.Invoke($"mesh {m} straddles a UV cell (u {uLo:F2}..{uHi:F2}, v {vLo:F2}..{vHi:F2}) "
                                   + "— the per-mesh tile shift cannot bring all of it onto [0,1]");
                }
            }

            private void KeepTriangles()
            {
                // Bake enabled body shape keys into the shell: a ShapeValue redirects one index entry to a morphed
                // replacement vertex already in THIS mesh's buffer. Shell layers only; a replacement >= vc is skipped.
                // BaseIndicesIndex is MESH-RELATIVE, so the lookup subtracts the mesh's StartIndex.
                meshStartIndex = U32(mo + 16);
                shapeReplace = preserve ? null : ShapeReplacements(src, meshStartIndex, vc);

                // Keep a triangle if ANY texel under its UV footprint is visible (cov null = keep all).
                keptPerSub = new List<ushort[]>();
                used = new bool[vc];
                // Vertices the TOE-CAP cut exposed: the weld may drag these a long way, since the cap fills that space.
                cutAway = new bool[vc];
                cutTris = 0;
                // Cursor into the cap's flattened corner list, advanced in the TRIANGLE loop in the same order the
                // projection walked. Every path that leaves a submesh without running that loop must go through
                // SkipSubmesh, or every later triangle of this mesh reads someone else's corners.
                cornerCursor = 0;
                // This mesh's flap past the joins; null for the host and cap passes and for a mesh joined to nothing.
                HashSet<ushort>? joinFlap = null;
                src.JoinFlaps?.TryGetValue(m, out joinFlap);
                void SkipSubmesh(uint sc)
                {
                    // The triangle loop below runs floor(sc/3) times, the same count CapTriangles walked.
                    if (capUv != null) cornerCursor += (int)(sc / 3) * 3;
                    keptPerSub.Add([]);
                }
                for (int su = 0; su < srcSubCount; su++)
                {
                    int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
                    uint so = U32(ss), sc = U32(ss + 4);
                    var keep = new List<ushort>();

                    // Already drawn by something else — see PlanConnectorDrops. Kept empty so every index and bone table
                    // keeps its shape.
                    if (src.DropSubmeshes is { } drops && drops.Contains((m, su)))
                    {
                        SkipSubmesh(sc);
                        continue;
                    }

                    // Switched off by one of the pack's own toggles — see ContentGeometry.HiddenAttributes — or,
                    // on a body, not drawn by the game — see SourceSpec.HiddenAttributes.
                    if ((hiddenAttrs is { Count: > 0 } && IsHidden(src, U32(ss + 8), hiddenAttrs))
                        || (src.HiddenAttrs is { } srcHidden && IsHidden(src, U32(ss + 8), srcHidden)))
                    {
                        build.hiddenSubs++;
                        SkipSubmesh(sc);
                        continue;
                    }
                    for (uint t = 0; t + 2 < sc; t += 3)
                    {
                        int p = src.Ib + (int)(so + t) * 2;
                        ushort a = BitConverter.ToUInt16(s, p), b = BitConverter.ToUInt16(s, p + 2), c = BitConverter.ToUInt16(s, p + 4);
                        // As stored, before the cap projection or a shape bake rewrites them — see the join cut below.
                        ushort rawA = a, rawB = b, rawC = c;

                        if (capUv is { } cplan)
                        {
                            if (cornerCursor + 2 < cplan.Corner.Length)
                            {
                                a = (ushort)cplan.Corner[cornerCursor];
                                b = (ushort)cplan.Corner[cornerCursor + 1];
                                c = (ushort)cplan.Corner[cornerCursor + 2];
                            }
                            cornerCursor += 3;
                        }
                        // Redirect index entries carrying a shape edit; so is absolute, the shape's keys are mesh-local.
                        if (shapeReplace != null)
                        {
                            int rel = (int)(so + t - meshStartIndex);
                            if (shapeReplace.TryGetValue(rel,     out var ra)) { a = ra; build.shapedTotal++; }
                            if (shapeReplace.TryGetValue(rel + 1, out var rb)) { b = rb; build.shapedTotal++; }
                            if (shapeReplace.TryGetValue(rel + 2, out var rc)) { c = rc; build.shapedTotal++; }
                        }
                        build.triIn++;

                        // Past the join, the neighbouring part draws this stretch of body. ANY corner in the flap drops the
                        // triangle (the flap set excludes the ring, so both parts end on the same vertices). RAW indices: a
                        // shape key redirects a corner to a morph vertex that is never in the flap set.
                        if (joinFlap != null
                            && (joinFlap.Contains(rawA) || joinFlap.Contains(rawB) || joinFlap.Contains(rawC)))
                        { build.trimmedOut++; continue; }

                        if (cov != null && !AnyVisible(cov, uv[a], uv[b], uv[c])) continue;
                        // The toe box was replaced wholesale, so its triangles go; the cap's own are added
                        // below. Anything the cap merely nudged is dropped only if it collapsed outright.
                        if (capPlan != null && capPlan.IsCut(a, b, c))
                        { cutAway[a] = cutAway[b] = cutAway[c] = true; cutTris++; continue; }
                        if (capPlan != null && capPlan.IsDropped(a, b, c)) continue;
                        if (capOutPos != null && CapDegenerate(capSrcPos!, capOutPos, a, b, c)) continue;
                        keep.Add(a); keep.Add(b); keep.Add(c);
                        used[a] = used[b] = used[c] = true;
                        build.triOut++;
                    }
                    keptPerSub.Add(keep.ToArray());
                }
            }

            private bool FinishTriangles()
            {
                // The two walks must agree: a desync leaves every index in range and the cap wired to the wrong vertices.
                if (capUv is { } capPlanned && cornerCursor != capPlanned.Corner.Length)
                    build.diag?.Invoke($"toe cap: mesh {m} — corner cursor ended at {cornerCursor} against "
                               + $"{capPlanned.Corner.Length} projected corner(s); the cap's UVs are misaligned");
                if (cov?.ToeCap != null)
                    build.diag?.Invoke($"toe cap: mesh {m} — plan {(capPlan == null ? "NULL" : "present")}, "
                               + $"the cut removed {cutTris} triangle(s)");

                // The rebuilt cap joins the submesh that lost the most to the cut: its vertices already skin through
                // that bone window.
                if (capPlan is { NewTriangles.Count: > 0 })
                {
                    int host = 0;
                    for (int su = 1; su < keptPerSub.Count; su++)
                        if (keptPerSub[su].Length > keptPerSub[host].Length) host = su;

                    var grown = new List<ushort>(keptPerSub[host]);
                    foreach (var (a, b, c) in capPlan.NewTriangles)
                    {
                        if (a >= vc || b >= vc || c >= vc) continue;
                        grown.Add(a); grown.Add(b); grown.Add(c);
                        used[a] = used[b] = used[c] = true;
                        build.triOut++;
                    }
                    keptPerSub[host] = grown.ToArray();
                }

                // Close the body's sockets in the garment: small holes on hip-owned skin are filled, only for a layer
                // smoothing the fold and only for holes the BODY has (see CloseHipSockets). Toenail sockets stay open.
                if (!preserve && cov is { FoldSmoothStrength: > 0f })
                {
                    var hipW = MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone]);
                    if (hipW != null)
                    {
                        var bodyTris = shapeReplace != null
                            ? ShapedTriangles(src, srcSubIdx, srcSubCount, meshStartIndex, shapeReplace)
                            : MeshTriangles(src, srcSubIdx, srcSubCount);
                        int shut = CloseHipSockets(keptPerSub, bodyTris, decl, outStreams, outStrides, vc, hipW, ref used,
                                                   out int shutTris);
                        if (shut > 0)
                        {
                            build.triOut += shutTris;
                            build.diag?.Invoke($"mesh {m}: closed {shut} socket hole(s) on hip-owned skin with {shutTris} triangle(s)");
                        }
                    }
                }

                // WATERTIGHT THE JOIN: the shell's lip landed part-way along rim EDGES, so each landing is a T-junction.
                // Splitting the cap's rim at each landing puts a vertex there without moving anything.
                if (preserve && capUv != null && build.capRimLandings.Count > 0)
                {
                    int added = SplitCapRim(build.capRimLandings, decl, ref outStreams, outStrides, ref vc,
                                            keptPerSub, ref used, out int onVert, out int offEdge);
                    build.diag?.Invoke($"authored cap: split the CAP's rim at {added} of {build.capRimLandings.Count} "
                               + $"shell landing(s) — {onVert} snapped onto, {offEdge} off the boundary");
                    JoinAudit("CAP rim", build.capRimLandings, keptPerSub, decl, outStreams, outStrides, vc, build.diag);
                }
                if (keptPerSub.All(k => k.Length == 0)) return false;   // paints nothing here

                // The UVs moved to another layout, so the tangent frame no longer describes them. Re-fit BEFORE the
                // weld, whose mirror split grows `vc` past `uv`/`uvPre`. Only runs for a UV-converted SHELL mesh.
                if (uvPre != null && uvPre.Length >= vc && uv.Length >= vc
                    && RetangentMesh(outStreams, outStrides, decl, vc, uvPre, uv, keptPerSub))
                    build.uvRetangented++;
                return true;
            }

            private void WeldToCapRim()
            {
                new CapWeld(this).Run();
            }

            private void AppendBoneMap(ref bool mapAppended)
            {
                if (!mapAppended)
                {
                    // The map's ENTRIES index THIS source's bone-name list, so they get the same by-name remap the bone
                    // tables do; only the OFFSETS are rebased (mapBase, written as boneStart).
                    foreach (var b in src.SubmeshBoneMap)
                    {
                        var bn = b < src.BoneNames.Length ? src.BoneNames[b] : null;
                        build.submeshBoneMap.Add(bn != null && build.boneIndex.TryGetValue(bn, out var bi) ? bi : (ushort)0);
                    }
                    mapAppended = true;
                }
            }

            private void CompactStreams()
            {
                // Recounted: the weld drops collapsed triangles after `used` was filled, and a vertex nothing points
                // at would still be emitted.
                Array.Clear(used);
                foreach (var sub in keptPerSub)
                    foreach (var i in sub) used[i] = true;

                // Compact each vertex stream down to the vertices the surviving triangles reference.
                streamCount = outStreams.Length;
                remap = new ushort[vc];
                nv = 0;
                var comp = new MemoryStream[streamCount];
                for (int st = 0; st < streamCount; st++) comp[st] = new MemoryStream();
                for (int i = 0; i < vc; i++)
                {
                    if (!used[i]) continue;
                    remap[i] = nv++;
                    for (int st = 0; st < streamCount; st++)
                        comp[st].Write(outStreams[st], i * outStrides[st], outStrides[st]);
                }
                build.vertOut += nv;

                vOff = new uint[streamCount];
                for (int st = 0; st < streamCount; st++)
                {
                    vOff[st] = (uint)build.vBuf.Position;
                    build.vBuf.Write(comp[st].GetBuffer(), 0, (int)comp[st].Length);
                }

                meshStartIdx = build.idxCursor;
                keptSubs = 0;
                subsForMesh = new List<byte[]>();
            }

            private void RebuildBoneMap()
            {
                // A REBUILT bone table needs a bone map of its own: publish the whole new table as this mesh's window.
                int rebuiltMapBase = -1;
                if (capBoneTable != null)
                {
                    rebuiltMapBase = build.submeshBoneMap.Count;
                    for (int i = 0; i < capBoneTable.Length; i++) build.submeshBoneMap.Add((ushort)i);
                }

                for (int su = 0; su < srcSubCount; su++)
                {
                    var keep = keptPerSub[su];
                    if (keep.Length == 0) continue;
                    int ss = src.SubmeshStart + (srcSubIdx + su) * 16;
                    uint subStart = build.idxCursor;
                    var idxBytes = new byte[keep.Length * 2];
                    for (int k = 0; k < keep.Length; k++)
                        BitConverter.TryWriteBytes(idxBytes.AsSpan(k * 2), remap[keep[k]]);
                    build.iBuf.Write(idxBytes);
                    build.idxCursor += (uint)keep.Length;

                    var ns = new byte[16];
                    W32(ns, 0, subStart);
                    W32(ns, 4, (uint)keep.Length);
                    // Cleared when Proteus owns this geometry's visibility (ContentGeometry.OwnAttributes), or the HOST's
                    // IMC mask could cull it.
                    // With dropVariantAttrs, the variant bits go and every other tag stays (ContentGeometry.DropVariantAttributes).
                    uint mask = dropVariantAttrs ? U32(ss + 8) & ~VariantBits(src) : U32(ss + 8);
                    W32(ns, 8, clearAttrs ? 0 : build.RemapAttrs(src, mask));
                    // The submesh's BONE WINDOW: the source's, rebased — unless the table was rebuilt, in which case
                    // window == table size.
                    if (capBoneTable != null)
                    {
                        W16(ns, 12, (ushort)rebuiltMapBase);
                        W16(ns, 14, (ushort)capBoneTable.Length);
                    }
                    else
                    {
                        W16(ns, 12, (ushort)(U16(ss + 12) + mapBase));        // boneStart, rebased
                        W16(ns, 14, U16(ss + 14));                            // boneCount, as authored
                    }
                    subsForMesh.Add(ns);
                    keptSubs++;
                }
            }

            private void WriteMesh()
            {
                // This mesh's OWN bone table, entries remapped onto the union list. Never merged with
                // other meshes' tables — ubyte4 vertex indices cap a table at 255 entries.
                var srcTable = srcBoneTbl < src.BoneTables.Length ? src.BoneTables[srcBoneTbl] : [];
                var table = capBoneTable ?? new ushort[srcTable.Length];
                if (capBoneTable == null)
                    for (int i = 0; i < srcTable.Length; i++)
                    {
                        var name = srcTable[i] < src.BoneNames.Length ? src.BoneNames[srcTable[i]] : null;
                        table[i] = name != null && build.boneIndex.TryGetValue(name, out var ui) ? ui : (ushort)0;
                    }

                var nm = new byte[36];
                W16(nm, 0, nv);
                W32(nm, 4, build.idxCursor - meshStartIdx);
                W16(nm, 8, materialIndex);
                W16(nm, 10, build.subCursor);
                W16(nm, 12, keptSubs);
                W16(nm, 14, (ushort)build.boneTables.Count);  // this mesh's own table
                W32(nm, 16, meshStartIdx);
                W32(nm, 20, vOff[0]);
                W32(nm, 24, streamCount > 1 ? vOff[1] : 0);
                W32(nm, 28, streamCount > 2 ? vOff[2] : 0);
                nm[32] = outStrides[0];
                nm[33] = streamCount > 1 ? outStrides[1] : (byte)0;
                nm[34] = streamCount > 2 ? outStrides[2] : (byte)0;
                nm[35] = (byte)streamCount;

                build.meshOut.Add(nm);
                build.declOut.Add(declBlock);
                build.boneTables.Add(table);
                build.subOut.AddRange(subsForMesh);
                build.subCursor += keptSubs;
            }

            private uint U32(int o) => BitConverter.ToUInt32(s, o);

            private ushort U16(int o) => BitConverter.ToUInt16(s, o);
        }
    }
}
