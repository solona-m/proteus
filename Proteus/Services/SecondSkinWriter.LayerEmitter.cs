using System;
using System.Collections.Generic;

namespace Proteus.Services;

public static partial class SecondSkinWriter
{
    private sealed partial class ShellBuild
    {
        private sealed class LayerEmitter
        {
            private readonly ShellBuild build;
            private readonly ushort layer;
            private SecondSkinLayer def = null!;
            private float push;
            private ushort matIndex;
            private float capPush;
            private SecondSkinLayer capDef = null!;
            private byte[]? footprint;
            private int footprintSize;
            private byte[]? reinforceMap;
            private SecondSkinLayer cutDef = null!;

            public LayerEmitter(ShellBuild build, ushort layer)
            {
                this.build = build;
                this.layer = layer;
            }

            public void Run(ref List<ToeLineTri>? toeLineBody)
            {
                ComputePush();
                DropDeclinedCap();
                PlaceCapRim();
                WidenCapCoverage();
                DrawReinforcedToe(ref toeLineBody);
                PublishReinforced();
                CutToeBox();
                if (!EmitGeometry()) return;
                GraftCap();
            }

            private void ComputePush()
            {
                def = build.layers[layer];
                // Scaled per surface, so a small surface's layers stay proportionally apart.
                push = (BaseOffset + LayerSeparation * layer) * def.PushScale;
                matIndex = (ushort)(build.baseMatCount + layer);

                // THE CAP IS PUSHED TO THE SHELL'S HEIGHT, NOT BY THE SHELL'S PUSH: it is a modelled object already
                // standing off the skin by its own clearance, measured at selection. Clamped at zero: a cap authored
                // below the shell's height is left where it is rather than pulled into the skin.
                //
                // "The shell's height" means the height the shell around the cap actually reaches, which is the FOOT
                // BAND's (see FootPushAt) — a toe box is the lowest part of the body, so it is inside that band by
                // construction and y = 0 answers for it. The cap is emitted preserve:true and so never passes through
                // the per-vertex push in VerbatimCopy; without this it would keep the unscaled height and sit most of
                // a millimetre under the shell it is grafted into, opening the join.
                float pushAtToes = push * build.PushBandAt(ToeBandHeight);
                capPush = build.capStandoff > 0f ? MathF.Max(0f, pushAtToes - build.capStandoff) : pushAtToes;
            }

            private void DropDeclinedCap()
            {
                // A declined cap takes its cut with it, so the toes keep the shell they already had.
                if (build.capDeclined != null && def.ToeCap != null)
                    def = new SecondSkinLayer
                    {
                        MaterialName = def.MaterialName,
                        Coverage = def.Coverage,
                        CoverageWidth = def.CoverageWidth,
                        CoverageHeight = def.CoverageHeight,
                        ToeCap = null,
                        ToeCapWidth = 0,
                        ToeCapHeight = 0,
                        ToeCapStrength = def.ToeCapStrength,
                        BustBridgeStrength = def.BustBridgeStrength,
                        NippleSmoothStrength = def.NippleSmoothStrength,
                        CleftBridgeStrength = def.CleftBridgeStrength,
                    };
            }

            private void PlaceCapRim()
            {
                // The cap's rim at this layer's offset, needed BEFORE the shell meshes are emitted; the projection is cached.
                build.weldRim = null;
                build.weldRimVerts.Clear();
                build.weldRimPos.Clear();
                build.capAllVerts.Clear();
                build.welded = 0; build.weldWorst = 0; build.weldWorstD = 0f; build.capWelded = 0;
                build.capRimLandings.Clear();
                build.shellRim.Clear();
            }

            private void WidenCapCoverage()
            {
                // THE CAP IS TRIMMED AGAINST A SLIGHTLY WIDER COVERAGE THAN THE SHELL: the any-texel test dilates by
                // the size of the triangle asking, and the cap's faces are far smaller, so the same curve stops the cap
                // earlier and its edge lands on shell fabric with nothing to weld to.
                // Handed to EmitMesh, which is declared above this loop and cannot see its locals.
                build.capPushNow = capPush;
                build.capGrafted = false;

                capDef = def;
                if (def.Coverage != null && def.CoverageWidth > 0 && def.CoverageHeight > 0 && CapCoverDilate > 0)
                    capDef = new SecondSkinLayer
                    {
                        MaterialName = def.MaterialName,
                        Coverage = DilateMask(def.Coverage, def.CoverageWidth, def.CoverageHeight,
                                              CapCoverDilate, CoverageFloor),
                        CoverageWidth = def.CoverageWidth,
                        CoverageHeight = def.CoverageHeight,
                        ToeCap = def.ToeCap,
                        ToeCapWidth = def.ToeCapWidth,
                        ToeCapHeight = def.ToeCapHeight,
                        ToeCapStrength = def.ToeCapStrength,
                        BustBridgeStrength = def.BustBridgeStrength,
                        NippleSmoothStrength = def.NippleSmoothStrength,
                        CleftBridgeStrength = def.CleftBridgeStrength,
                    };

                footprint = null;
                footprintSize = def.ToeCapWidth > 0 && def.ToeCapWidth == def.ToeCapHeight
                    ? def.ToeCapWidth : CapFootprintSize;
            }

            private void DrawReinforcedToe(ref List<ToeLineTri>? toeLineBody)
            {
                // The reinforced-toe region, when this layer has one — drawn at the sheet's own size.
                reinforceMap = null;
                if (build.capSrc is { } cw && def.ToeCap != null)
                {
                    var segs = new List<RimSeg>();
                    footprint = new byte[footprintSize * footprintSize];
                    int cwEnd = cw.Lod0MeshIndex + cw.Lod0MeshCount;
                    for (int m = cw.Lod0MeshIndex; m < cwEnd && m < cw.MeshCount; m++)
                    {
                        if (BitConverter.ToUInt16(cw.S, cw.MeshStart + m * 36) == 0) continue;
                        if (!build.capUvCache.TryGetValue(m, out var pl))
                            build.capUvCache[m] = pl = ProjectCapUV(cw, m, build.sourceModels, build.diag,
                                build.capPlaced != null && build.capPlaced.TryGetValue(m, out var pw2) ? pw2 : null);
                        if (pl == null) continue;

                        // The rim of the cap AS THIS LAYER WILL EMIT IT, after its own coverage trims it. Counted on PRE-SPLIT
                        // indices, or the UV seam would read as a boundary.
                        var edgeUse = new Dictionary<(int A, int B), int>();
                        for (int f = 0; f * 3 + 2 < pl.Corner.Length; f++)
                        {
                            int c0 = pl.Corner[f * 3], c1 = pl.Corner[f * 3 + 1], c2 = pl.Corner[f * 3 + 2];
                            if (capDef.Coverage != null && !AnyVisible(capDef, pl.Uv[c0], pl.Uv[c1], pl.Uv[c2]))
                                continue;
                            int s0 = pl.SourceOf[c0], s1 = pl.SourceOf[c1], s2 = pl.SourceOf[c2];
                            foreach (var (x, y) in new[] { (s0, s1), (s1, s2), (s2, s0) })
                            {
                                var e = (Math.Min(x, y), Math.Max(x, y));
                                edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
                            }
                        }
                        CapFootprintMask(pl, capDef, footprint, footprintSize);

                        if (def.ToeReinforceSize > 0)
                        {
                            reinforceMap ??= new byte[def.ToeReinforceSize * def.ToeReinforceSize];
                            toeLineBody ??= ToeLineBody(build.sourceModels);
                            DrawToeLine(pl, toeLineBody, reinforceMap, def.ToeReinforceSize, build.diag);
                        }

                        // The one place a cap rim vertex's final position is worked out. See weldRimPos.
                        Vec3 CapFinal(int i)
                        {
                            var (p, n2) = (pl.SrcPos[i], pl.SrcNrm[i]);
                            return new Vec3(p.X + n2.X * capPush, p.Y + n2.Y * capPush, p.Z + n2.Z * capPush);
                        }

                        // How many emitted copies each cap vertex has: two means it sits on the atlas seam and has no single
                        // UV to share.
                        var copies = new Dictionary<int, int>();
                        var firstCopy = new Dictionary<int, int>();
                        for (int oi = 0; oi < pl.SourceOf.Length; oi++)
                        {
                            int s = pl.SourceOf[oi];
                            copies[s] = copies.GetValueOrDefault(s) + 1;
                            if (!firstCopy.ContainsKey(s)) firstCopy[s] = oi;
                        }

                        foreach (var (e, n) in edgeUse)
                        {
                            if (n != 1) continue;
                            Vec3 pa = CapFinal(e.A), pb = CapFinal(e.B);
                            // A segment touching a seam vertex carries no UV, and the lip there keeps its own.
                            bool uvOk = copies.GetValueOrDefault(e.A) == 1 && copies.GetValueOrDefault(e.B) == 1
                                     && firstCopy.TryGetValue(e.A, out int oa) && oa < pl.Uv.Length
                                     && firstCopy.TryGetValue(e.B, out int ob) && ob < pl.Uv.Length;
                            segs.Add(uvOk
                                ? new RimSeg(pa, pl.SrcNrm[e.A], pl.SrcW[e.A],
                                             pb, pl.SrcNrm[e.B], pl.SrcW[e.B],
                                             pl.Uv[firstCopy[e.A]], pl.Uv[firstCopy[e.B]], true)
                                : new RimSeg(pa, pl.SrcNrm[e.A], pl.SrcW[e.A],
                                             pb, pl.SrcNrm[e.B], pl.SrcW[e.B]));
                            build.weldRimVerts.Add(e.A); build.weldRimVerts.Add(e.B);
                            build.weldRimPos[(m, e.A)] = pa; build.weldRimPos[(m, e.B)] = pb;
                        }

                        for (int oi = 0; oi < pl.SourceOf.Length; oi++)
                        {
                            int sIdx = pl.SourceOf[oi];
                            if (build.capPlaced == null || !build.capPlaced.TryGetValue(m, out var pcap)) break;
                            if (sIdx < 0 || sIdx >= pcap.Pos.Length) continue;
                            build.capAllVerts.Add(new Vec3(pcap.Pos[sIdx].X + pcap.Nrm[sIdx].X * capPush,
                                                     pcap.Pos[sIdx].Y + pcap.Nrm[sIdx].Y * capPush,
                                                     pcap.Pos[sIdx].Z + pcap.Nrm[sIdx].Z * capPush));
                        }
                    }
                    if (segs.Count > 0) build.weldRim = segs.ToArray();
                    build.diag?.Invoke($"authored cap: layer {layer} keeps a rim of {segs.Count} edge(s) after its "
                               + "own coverage trims the cap");
                    // Only now is capDef final; the graft inside EmitMesh trims the cap with it.
                    build.capDefNow = capDef;
                }
            }

            private void PublishReinforced()
            {
                // Hand the reinforced-toe region to the caller. Unioned, since two layers of one material (a split
                // shell) can each draw part of it.
                if (reinforceMap != null)
                {
                    bool anyLit = false;
                    foreach (byte px in reinforceMap) if (px != 0) { anyLit = true; break; }
                    if (anyLit)
                    {
                        int rs = def.ToeReinforceSize;
                        if (build.toeReinforceMaps.TryGetValue(def.MaterialName, out var had) && had.Size == rs)
                        {
                            for (int i = 0; i < had.Mask.Length; i++)
                                if (reinforceMap[i] > had.Mask[i]) had.Mask[i] = reinforceMap[i];
                        }
                        else
                        {
                            build.toeReinforceMaps[def.MaterialName] = (reinforceMap, rs);
                        }
                    }
                }

                cutDef = def;
            }

            private void CutToeBox()
            {
                if (build.capSrc != null && def.ToeCap is { } paint && def.ToeCapWidth > 0 && def.ToeCapHeight > 0)
                {
                    int mwp = def.ToeCapWidth, mhp = def.ToeCapHeight;
                    var eroded = (byte[])paint.Clone();
                    for (int step = 0; step < CapCutErode; step++)
                    {
                        var next = (byte[])eroded.Clone();
                        for (int y = 0; y < mhp; y++)
                            for (int x = 0; x < mwp; x++)
                            {
                                if (eroded[y * mwp + x] < 128) continue;
                                bool edge = false;
                                for (int dy = -1; dy <= 1 && !edge; dy++)
                                    for (int dx = -1; dx <= 1 && !edge; dx++)
                                    {
                                        int nx = x + dx, ny = y + dy;
                                        if (nx < 0 || ny < 0 || nx >= mwp || ny >= mhp || eroded[ny * mwp + nx] < 128)
                                            edge = true;
                                    }
                                if (edge) next[y * mwp + x] = 0;
                            }
                        eroded = next;
                    }
                    int lit = 0, painted = 0;
                    foreach (byte px in eroded) if (px >= 128) lit++;
                    if (footprint != null) foreach (byte px in footprint) if (px >= 128) painted++;

                    // CUT TO THE CAP, not to the painted map, which only says a cap is WANTED: CapFootprintMask fills what
                    // the cap ENCLOSES and CapCutDilate supplies the overshoot the weld pulls back onto the rim.
                    var cutMask = footprint != null
                        ? DilateMask(footprint, footprintSize, footprintSize, CapCutDilate)
                        : eroded;
                    int cutSide = footprint != null ? footprintSize : mwp;
                    int cutLit = 0;
                    foreach (byte px in cutMask) if (px >= 128) cutLit++;

                    if (MaskDump is { } dump)
                    {
                        dump("painted", eroded, mwp);
                        if (footprint != null) dump("footprint", footprint, footprintSize);
                        dump("cut", cutMask, cutSide);
                    }

                    build.diag?.Invoke($"authored cap: cutting to the cap's own footprint — {painted} texels "
                               + $"covered, {cutLit} after dilating by {CapCutDilate} "
                               + $"(the painted map would have cut {lit})");

                    cutDef = new SecondSkinLayer
                    {
                        MaterialName = def.MaterialName,
                        Coverage = def.Coverage,
                        CoverageWidth = def.CoverageWidth,
                        CoverageHeight = def.CoverageHeight,
                        ToeCap = cutMask,
                        ToeCapWidth = cutSide,
                        ToeCapHeight = cutSide,
                        ToeCapStrength = def.ToeCapStrength,
                        BustBridgeStrength = def.BustBridgeStrength,
                        NippleSmoothStrength = def.NippleSmoothStrength,
                        CleftBridgeStrength = def.CleftBridgeStrength,
                    };
                }
            }

            private bool EmitGeometry()
            {
                // A content layer brings its own geometry, copied exactly as the host is: no push, no coverage trim.
                // Every geometry of the layer shares the SAME material index, so a mod's pieces share one host slot.
                if (def.Geometry.Count > 0)
                {
                    foreach (var geo in def.Geometry)
                    {
                        var gsrc = build.geomByModel[geo.Model];
                        var gs = gsrc.S;
                        int gEnd = gsrc.Lod0MeshIndex + gsrc.Lod0MeshCount;
                        for (int m = gsrc.Lod0MeshIndex; m < gEnd && m < gsrc.MeshCount; m++)
                        {
                            int gmo = gsrc.MeshStart + m * 36;
                            if (BitConverter.ToUInt16(gs, gmo) == 0) continue;   // empty placeholder mesh

                            ushort gMat = BitConverter.ToUInt16(gs, gmo + 8);
                            if (gMat >= gsrc.MatNames.Count || !geo.KeepMaterial(gsrc.MatNames[gMat]))
                                continue;

                            build.EmitMesh(gsrc, m, matIndex, 0f, preserve: true, cov: null,
                                mirrorUv1: geo.MirrorUv1,
                                hiddenAttrs: geo.HiddenAttributes, clearAttrs: geo.OwnAttributes,
                                dropVariantAttrs: geo.DropVariantAttributes);
                        }
                    }
                    return false;
                }

                foreach (var src in build.parsed)
                {
                    var s = src.S;
                    ushort U16(int o) => BitConverter.ToUInt16(s, o);

                    // LOD0 meshes only — never the lower LODs (a full game model has all three; merging them
                    // stacks overlapping low-poly copies that fling geometry across the scene).
                    int mEnd = src.Lod0MeshIndex + src.Lod0MeshCount;
                    for (int m = src.Lod0MeshIndex; m < mEnd && m < src.MeshCount; m++)
                    {
                        int mo = src.MeshStart + m * 36;
                        if (U16(mo) == 0) continue;   // empty mesh

                        // Which meshes belong in the shell — see SourceSpec.KeepMaterial: skin only for a body (undies, nails,
                        // piercings are gear-UV), the named material for a face or tail. The range guard stays outside the predicate.
                        ushort srcMat = U16(mo + 8);
                        if (srcMat >= src.MatNames.Count || !src.Keep(src.MatNames[srcMat]))
                            continue;

                        // cutDef, not def: the toe-cap cut rides the coverage argument. Redundant submeshes were settled per
                        // source (Source.DropSubmeshes).
                        build.EmitMesh(src, m, matIndex, push, preserve: false, cov: cutDef);
                    }
                }

                if (build.weldRim != null)
                    build.diag?.Invoke($"authored cap: welded {build.welded} cut-lip vertices onto the rim "
                               + $"(furthest moved {build.weldWorstD:F4}), {build.weldWorst} cut vertex/vertices "
                               + $"left beyond {WeldCutReach:F3}");
                return true;
            }

            private void GraftCap()
            {
                // Graft the authored cap for any layer that asked for one, wearing that layer's material. Skipped when
                // the cap went INTO a shell mesh (the normal path): emitting it again would draw it twice.
                if (build.capSrc is { } cs && def.ToeCap != null && !build.capGrafted)
                {
                    // The cap is authored WITHOUT UVs, so it takes the body's: each vertex is dropped onto the skin and
                    // takes the coordinate where it lands.

                    int cEnd = cs.Lod0MeshIndex + cs.Lod0MeshCount;
                    int emitted = 0;
                    for (int m = cs.Lod0MeshIndex; m < cEnd && m < cs.MeshCount; m++)
                    {
                        if (BitConverter.ToUInt16(cs.S, cs.MeshStart + m * 36) == 0) continue;   // empty mesh
                        build.EmitMesh(cs, m, matIndex, capPush, preserve: true, cov: capDef,
                                 capUv: build.capUvCache.TryGetValue(m, out var cached) ? cached
                                      : build.capUvCache[m] = ProjectCapUV(cs, m, build.sourceModels, build.diag,
                                            build.capPlaced != null && build.capPlaced.TryGetValue(m, out var pc2) ? pc2 : null));
                        emitted++;
                    }
                    build.diag?.Invoke($"authored toe cap: grafted {emitted} mesh(es) onto layer {layer}, "
                               + $"{build.capWelded} rim vertices welded back onto {build.shellRim.Count} shell segment(s)");
                }
            }
        }
    }
}
