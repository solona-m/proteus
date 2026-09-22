using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Penumbra.Api.Enums;

namespace Proteus.Services;
using static Proteus.Services.InertModDiagnosis;
using static Proteus.Services.OverlayBlend;

public partial class CompositorService
{
    private sealed partial class CompositeRun
    {
        private sealed partial class MaterialComposite
        {
            private readonly CompositeRun run;
            private readonly KeyValuePair<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> kvp;
            private readonly bool compress;
            private readonly int encSalt;
            private string mtrlGamePath = null!;
            private List<(OverlayEntry Entry, ResolvedOverlay Overlay)> pairs = null!;
            private string? dstBodyType;
            private bool faceDoubled;
            private string? dstFaceSpace;
            private string? mtrlDisk;
            private MtrlTexturePaths texPaths = null!;
            private byte[]? baseD;
            private byte[]? baseN;
            private byte[]? baseM;
            private int wD;
            private int hD;
            private int wN;
            private int hN;
            private int wM;
            private int hM;
            private bool baseDTried;
            private string? baseDiffuseTag;
            private bool diffuseBlended;
            private bool normalBlended;
            private bool maskBlended;
            private int diffuseContributors;
            private int normalContributors;
            private int maskContributors;
            private bool diffuseWanted;
            private bool normalWanted;
            private bool warnedNoDiffuseSampler;
            private bool warnedNoNormalSampler;
            private bool warnedNoBaseNormal;
            private Dictionary<string, string?> lastSrcBodyTypeByMod = null!;
            private Dictionary<string, byte[]> paintedByMod = null!;
            private Dictionary<(string Mod, int Stack, int W, int H, OverlayChannel Ch), byte[]?> claimCache = null!;
            private const int glowMapCap = 1024;
            private List<(string ModDir, string? Group, string? Option, byte[] Map, int W, int H)> glowMaps = null!;
            private long tMaskRelief;
            private long tMaskDiffuse;
            private string baseName = null!;
            private System.Text.StringBuilder channels = null!;
            private List<string> published = null!;
            private Dictionary<string, string> retarget = null!;

            public MaterialComposite(CompositeRun run, KeyValuePair<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> kvp, bool compress, int encSalt)
            {
                this.run = run;
                this.kvp = kvp;
                this.compress = compress;
                this.encSalt = encSalt;
            }

            public void Run()
            {
                Begin();
                if (!LoadBases()) return;
                PrepareClaims();
                if (!BlendOverlays()) return;
                BlendMaskRelief();
                BlendMaskDiffuse();
                ApplyOcclusion();
                PublishChannels();
                PublishRetargets();
                ReportFaults();
            }

            private void Begin()
            {
                (mtrlGamePath, pairs) = kvp;

                dstBodyType = UVRemapService.InferBodyType(mtrlGamePath);

                // This material renders the doubled face sheet (its model was rewritten, see FaceUvDoublingService): bases are
                // expanded into that layout and art is loaded to match.
                faceDoubled = run.facePlan.Materials.Contains(mtrlGamePath);
                // The face layout this face material's textures are in this composite; null for non-face materials.
                dstFaceSpace = !FaceUvDoublingService.IsFaceMaterial(mtrlGamePath) ? null
                                     : faceDoubled ? UVRemapService.FaceSplitSpace : UVRemapService.FaceSpace;
            }

            private bool LoadBases()
            {
                if (run.ct.IsCancellationRequested) return false;

                mtrlDisk = run.compositor.ResolveUpstream(mtrlGamePath);
                // Raw parse: Lumina's typed MtrlFile misreads some Dawntrail layouts, which would bail on vanilla materials.
                texPaths = run.compositor.textureLoader.ResolveMtrlTexturesRaw(mtrlDisk, mtrlGamePath);

                if (texPaths.Diffuse == null && texPaths.Normal == null && texPaths.Mask == null)
                {
                    run.compositor.log.Warning("[Proteus] No textures found for material: {0}", mtrlGamePath);
                    // Record it before bailing, so the most broken materials still appear in the panel.
                    run.contributions[mtrlGamePath] = new ChannelContribution(mtrlGamePath, 0, 0, 0,
                        DiffuseWanted: pairs.Any(p => p.Overlay.Descriptor.Diffuse != null), Touched: false,
                        NormalWanted: pairs.Any(p => p.Overlay.Descriptor.Normal != null));
                    return false;
                }

                baseD = null;
                baseN = null;
                baseM = null;
                wD = 0;
                hD = 0;
                wN = 0;
                hN = 0;
                wM = 0;
                hM = 0;

                // Whether the base diffuse has been asked for yet (a failed load leaves non-null Array.Empty). Every site loads
                // through EnsureBaseDiffuse, so a failure is reported once per material.
                baseDTried = false;

                // The base diffuse's content tag before anything blended into it, with the output filename's salts; tells a
                // blend that changed the skin from one that was a no-op. Taken by SnapshotBaseDiffuse at the first edit.
                baseDiffuseTag = null;

                // Whether a blend pass ran on each buffer, as opposed to the buffer merely existing. "Ran", not "bytes changed":
                // a fully transparent overlay still sets it. Locals on one material's task.
                diffuseBlended = false;
                normalBlended = false;
                maskBlended = false;
                diffuseContributors = 0;
                normalContributors = 0;
                maskContributors = 0;

                // At least one overlay on this material asked for a diffuse (or a normal); with zero contributors that is the
                // fault the UI shows red.
                diffuseWanted = false;
                normalWanted = false;

                // These warnings are about the material: log each once, not per overlay.
                warnedNoDiffuseSampler = false;
                warnedNoNormalSampler = false;
                warnedNoBaseNormal = false;
                return true;
            }

            private void PrepareClaims()
            {
                // Captured per mod for the Masks-driven relief pass afterwards (masks are mod-level).
                lastSrcBodyTypeByMod = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                // ── What each mod has PAINTED on this material so far ──────────
                // The clip a print is multiplied through: per mod, diffuse-only and post-seam-drop (unlike ClaimAt). One per
                // material task, so unlocked.
                paintedByMod = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                // ── Claims from higher in the stack ───────────────────────────
                // An overlay is faded by whatever composites above it in this material's order (`pairs`, bottom→top), the same
                // ranking as the tab strip and Rank(). Fading coverage also stops a lower normal compounding through an opaque
                // higher one. Same-group options stack against each other; the claim is a per-texel alpha union (UnionAlphaInto).
                // Keyed by channel as well: an overlay only claims a channel it supplies art in, so a diffuse-only group cannot
                // erase a normal-only group beneath it in the same mod.
                claimCache = new Dictionary<(string Mod, int Stack, int W, int H, OverlayChannel Ch), byte[]?>();

                // Per-overlay glow row-maps for the live "glow" button, from the diffuse phase, downsampled to bound memory
                // (the highlighter nearest-samples back up).
                
                glowMaps = new List<(string ModDir, string? Group, string? Option, byte[] Map, int W, int H)>();
            }

            private bool BlendOverlays()
            {
                // Measured as one block: per-overlay kernels, coverage rebuilds and clones. Not try/finally; a cancelled run's numbers are discarded.
                var tOverlays = PhaseCounter.Begin();
                int pairIndex = -1;
                foreach (var (entry, resolved) in pairs)
                {
                    if (!BlendOverlay(ref pairIndex, entry, resolved)) return false;
                }
                run.compositor.blendOverlayStats.Stop(tOverlays);

                tMaskRelief = PhaseCounter.Begin();
                return true;
            }

            /// <summary>Blends one overlay into this material's diffuse, normal and mask; false when the run was cancelled.</summary>
            private bool BlendOverlay(ref int pairIndex, OverlayEntry entry, ResolvedOverlay resolved)
            {
                return new OverlayBlendPass(this, entry, resolved).Run(ref pairIndex);
            }

            private void BlendMaskRelief()
            {
                // ── Masks-driven relief ────────────────────────────────────────
                // Runs once per mod after its whole stack, for active masks with a companion relief normal. Base normal plus every
                // mask's relief first, then the combined Masks-group coverage as a final show/hide pass, so each mask's shape is honoured.
                foreach (var modDir in pairs.Select(p => p.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (run.maskShellMods.Contains(modDir)) continue;   // relief lives on the mask shell instead
                    if (!run.maskAssetsByMod.TryGetValue(modDir, out var assets)) continue;
                    if (texPaths.Normal == null || !assets.Any(a => a.NormalPath != null)) continue;
                    lastSrcBodyTypeByMod.TryGetValue(modDir, out var maskSrcBodyType);

                    baseN ??= LoadBaseNormalHere(texPaths.Normal, ref wN, ref hN);
                    if (baseN.Length > 0)
                    {
                        // Snapshot before any mask relief; the combined coverage below blends back toward it per pixel.
                        var preRelief = (byte[])baseN.Clone();

                        // Fold each mask's trim relief in top-first with a claim, so a higher mask's trim wins (see CombineMaskReliefs).
                        var reliefMasks = new List<(byte[] Relief, byte[] Coverage)>();
                        foreach (var (maskPath, normalPath, _) in assets)   // top-first (highest priority first)
                        {
                            if (normalPath == null) continue;
                            var maskPng  = LoadRemapped(maskPath, wN, hN, maskSrcBodyType);
                            var normalOv = LoadRemapped(normalPath, wN, hN, maskSrcBodyType);
                            if (maskPng != null && normalOv != null)
                                reliefMasks.Add((normalOv, maskPng));
                        }
                        CombineMaskReliefs(baseN, wN, hN, reliefMasks);
                        // A writer of the normal buffer, so the untouched-normal hand-back in PublishChannels must not fire.
                        if (reliefMasks.Count > 0) normalBlended = true;

                        var msk = CombinedMaskAt(modDir, wN, hN, maskSrcBodyType);
                        if (msk != null)
                        {
                            var full = new byte[wN * hN * 4];
                            ParallelPixels(3, full.Length, 4, (fromFi, toFi) =>
                            {
                                for (int fi = fromFi; fi < toFi; fi += 4) full[fi] = 255;
                            });
                            var weight = ApplyCoverageMask(full, msk.Value.W, msk.Value.T);
                            var bn = baseN;
                            var pre = preRelief;
                            ParallelPixels(0, bn.Length, 4, (fromI, toI) =>
                            {
                                for (int i = fromI; i < toI; i += 4)
                                {
                                    float t = weight[i + 3] / 255f;
                                    bn[i]     = (byte)(pre[i]     * (1f - t) + bn[i]     * t);
                                    bn[i + 1] = (byte)(pre[i + 1] * (1f - t) + bn[i + 1] * t);
                                }
                            });
                        }
                    }
                }
                run.compositor.blendMaskReliefStats.Stop(tMaskRelief);

                tMaskDiffuse = PhaseCounter.Begin();
            }

            private void BlendMaskDiffuse()
            {
                // ── Masks diffuse ──────────────────────────────────────────────
                // A mod's "Masks" tab colours its active masks from one shared table, composited over the overlay diffuse; the mask
                // _id selects the row. The table is the mod's Masks colorset, else the topmost fabric overlay's rows (a flat row
                // colour, not art). Masks combine top-territory-wins: the topmost mask with alpha decides coverage and row, so black
                // in its territory makes a hole.
                foreach (var modDir in pairs.Select(p => p.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (run.maskShellMods.Contains(modDir)) continue;   // mask lives on the shell, not the skin diffuse
                    if (!run.maskAssetsByMod.TryGetValue(modDir, out var assets) || texPaths.Diffuse == null) continue;
                    if (!assets.Any(a => a.IndexPath != null)) continue;
                    // Own Masks colorset, else the inherited one (precomputed above). Neither ⇒ nothing to colour with.
                    if (!run.maskRowsByMod.TryGetValue(modDir, out var maskRows)
                     && !run.maskFallbackRows.TryGetValue(MaskFallbackKey(mtrlGamePath, modDir), out maskRows))
                        continue;
                    lastSrcBodyTypeByMod.TryGetValue(modDir, out var maskSrcBodyType);

                    if (EnsureBaseDiffuse() is not { } maskBaseD) continue;

                    // Combine into one coverage + one _id, top-territory-wins: bottom masks first so the top one lands last.
                    int n = wD * hD;
                    var cov = new byte[n];        // paint alpha = winning mask's grayscale in its territory
                    var cid = new byte[n * 4];    // winning mask's _id (red = row pair, green = A/B blend)
                    bool anyMask = false;
                    for (int mi = assets.Count - 1; mi >= 0; mi--)
                    {
                        var (maskPath, _, maskIndexPath) = assets[mi];
                        if (maskIndexPath == null) continue;
                        var maskPng = LoadRemapped(maskPath, wD, hD, maskSrcBodyType);
                        // Nearest, like every _id read: red is a row id (red / 17 + 1).
                        var maskIdx = LoadRemapped(maskIndexPath, wD, hD, maskSrcBodyType, ResampleFilter.Nearest);
                        if (maskPng == null || maskIdx == null) continue;
                        anyMask = true;
                        ParallelPixels(0, n, 1, (from, to) =>
                        {
                            for (int p = from; p < to; p++)
                            {
                                int o = p * 4;
                                int a = maskPng[o + 3];
                                if (a == 0) continue;                                       // outside this mask
                                int g = (maskPng[o] * 77 + maskPng[o + 1] * 150 + maskPng[o + 2] * 29) >> 8;
                                // Territory alpha-over: a=255,g=0 (black) drives cov to 0, erasing a lower mask's white.
                                cov[p] = (byte)(cov[p] * (255 - a) / 255 + g * a / 255);
                                if (a >= 128) { cid[o] = maskIdx[o]; cid[o + 1] = maskIdx[o + 1]; }
                            }
                        });
                    }
                    if (!anyMask) continue;

                    // Paint once from the combined coverage + _id (white "art", alpha = coverage).
                    var art = new byte[n * 4];
                    ParallelPixels(0, art.Length, 4, (from, to) =>
                    {
                        for (int i = from; i < to; i += 4)
                        {
                            art[i] = art[i + 1] = art[i + 2] = 255;
                            art[i + 3] = cov[i >> 2];
                        }
                    });

                    // Per-row Opacity from the Masks colorset, applied after coverage (ApplyIndexedOverlay never reads opacity).
                    if (maskRows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                        art = ApplyIndexedOpacity(art, cid, maskRows);

                    SnapshotBaseDiffuse();
                    ApplyIndexedOverlay(maskBaseD, art, cid, maskRows, false, wD, hD);
                    diffuseBlended = true; diffuseContributors++;

                    // Glow row-map from the painted (post-opacity) alpha + _id: 0 = no glow, else 0x80 | (A?0x40) | pairIdx.
                    int gw = Math.Min(wD, glowMapCap), gh = Math.Min(hD, glowMapCap);
                    var gmap = new byte[gw * gh];
                    bool anyGlow = false;
                    for (int my = 0; my < gh; my++)
                    {
                        int sy = gh == hD ? my : (int)((long)my * hD / gh);
                        for (int mx = 0; mx < gw; mx++)
                        {
                            int sx = gw == wD ? mx : (int)((long)mx * wD / gw);
                            int sp = sy * wD + sx;
                            int so = sp * 4;
                            if (art[so + 3] == 0) continue;   // hole/outside/faded out = no glow
                            gmap[my * gw + mx] = (byte)(0x80 | (cid[so + 1] >= 128 ? 0x40 : 0) | ((cid[so] / 17) & 0x0F));
                            anyGlow = true;
                        }
                    }
                    if (anyGlow)
                        glowMaps.Add((modDir, SidecarDiscoveryService.MaskGroupName, "Masks", gmap, gw, gh));
                }
                run.compositor.blendMaskDiffuseStats.Stop(tMaskDiffuse);
            }

            private void ApplyOcclusion()
            {
                new OcclusionPass(this).Run();
            }

            private void PublishChannels()
            {
                // A doubled material publishes every slot its material declares, blended or not: its model samples the doubled
                // sheet. Each load no-ops when already done.
                if (faceDoubled)
                {
                    EnsureBaseDiffuse();
                    if (texPaths.Normal != null) baseN ??= LoadBaseNormalHere(texPaths.Normal, ref wN, ref hN);
                    EnsureBaseMask();
                }

                baseName = SanitizeName(mtrlGamePath);
                channels = new System.Text.StringBuilder();
                // Which game path each channel was published to (read from the .mtrl): the link that decides whether the game reads our output.
                published = new List<string>(3);

                // Nothing edited the diffuse: hand the buffer back rather than publishing a lossy re-encode of the skin mod's own
                // pixels. Not for a doubled material, where the relayout is the change.
                if (!diffuseBlended && !faceDoubled && baseD is { Length: > 0 })
                {
                    run.compositor.log.Debug("[Proteus] Nothing composited into the diffuse of {0} — leaving the base texture "
                            + "in place rather than republishing it", mtrlGamePath);
                    baseD = null;
                }

                // Same for the normal. normalBlended covers every writer — the overlay blend, skin-tint suppression, the Masks
                // relief pass and the AO indent — so this only fires when the buffer was loaded and left alone, which is what a
                // normal declared on a material nothing could reach looks like. The AO pass has its own narrower hand-back for
                // the case where it was the one that loaded it. faceDoubled is exempt for the same reason the diffuse is: on a
                // doubled face material the relayout IS the change, and LoadBaseNormalHere is the only thing that applies it.
                if (!normalBlended && !faceDoubled && baseN is { Length: > 0 })
                {
                    run.compositor.log.Debug("[Proteus] Nothing composited into the normal of {0} — leaving the base texture "
                            + "in place rather than republishing it", mtrlGamePath);
                    baseN = null;
                }

                // Edited to no effect: a fault only if an overlay blended (it looks like the overlay not applying). AO changing
                // nothing is healthy (gear covering the whole body) and only happens with zero contributors.
                string? diffuseTag = null;
                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    diffuseTag = run.compositor.TimedContentTag(baseD, wD, hD, encSalt, OutputFormatVersion);

                    // faceDoubled is exempt: this gate must never unpublish a relayout.
                    if (!faceDoubled && baseDiffuseTag != null && diffuseTag == baseDiffuseTag)
                    {
                        if (diffuseContributors > 0)
                            run.compositor.log.Warning("[Proteus] Diffuse of {0} is byte-identical to the base skin after {1} "
                                      + "overlay(s) blended into it — the blend was a no-op, so the body will "
                                      + "render as if nothing applied (check coverage masks and opacity)",
                                mtrlGamePath, diffuseContributors);
                        else
                        {
                            // No contributors means no glow maps either, so hand the buffer back like the untouched case.
                            run.compositor.log.Debug("[Proteus] Diffuse of {0} unchanged — only ambient occlusion reached it "
                                    + "and it had nothing to darken (gear covers the skin, or strength is 0). "
                                    + "Leaving the base texture in place.", mtrlGamePath);
                            baseD = null;
                        }
                    }
                }

                // The game path each slot is published at. A slot naming a texture every character shares (e.g.
                // chara/common/texture/skin_mask.tex) publishes at a private path, and the material is rewritten to name it.
                // Recorded only once written, so the material never names a path nothing serves.
                retarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string PublishKey(string texPath, string slot)
                    => IsSharedTexturePath(texPath) ? OwnedTexturePath(mtrlGamePath, slot) : texPath;
                void Published(string texPath, string key)
                {
                    if (!string.Equals(texPath, key, StringComparison.OrdinalIgnoreCase)) retarget[texPath] = key;
                }

                if (baseD is { Length: > 0 } && texPaths.Diffuse != null)
                {
                    var tag = diffuseTag!;
                    var name = baseName + "_" + tag + "_d.tex";
                    var outPath = Path.Combine(run.texturesDir, name);
                    var relPath = "textures/" + name;
                    if (run.compositor.AlreadyWritten(outPath)
                     || run.compositor.textureLoader.WriteTex(baseD, wD, hD, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    {
                        var key = PublishKey(texPaths.Diffuse, "d");
                        run.redirects[key] = relPath; Interlocked.Increment(ref run.texturesPatched);
                        Published(texPaths.Diffuse, key);
                        channels.Append(" diffuse(").Append(diffuseContributors).Append(')');
                        published.Add($"{key} -> {relPath}");

                        // Publish the glow recipes captured during the diffuse phase, now that the on-disk path is known.
                        foreach (var gm in glowMaps)
                        {
                            var list = run.skinGlow.GetOrAdd((gm.ModDir, gm.Group, gm.Option),
                                _ => new List<Proteus.Interop.SkinGlowTarget>());
                            lock (list) list.Add(new Proteus.Interop.SkinGlowTarget(outPath, gm.Map, gm.W, gm.H));
                        }
                    }
                }
                if (baseN is { Length: > 0 } && texPaths.Normal != null)
                {
                    var name = baseName + "_" + run.compositor.TimedContentTag(baseN, wN, hN, encSalt, OutputFormatVersion) + "_n.tex";
                    var outPath = Path.Combine(run.texturesDir, name);
                    var relPath = "textures/" + name;
                    if (run.compositor.AlreadyWritten(outPath)
                     || run.compositor.textureLoader.WriteTex(baseN, wN, hN, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    {
                        var key = PublishKey(texPaths.Normal, "n");
                        run.redirects[key] = relPath; Interlocked.Increment(ref run.texturesPatched);
                        Published(texPaths.Normal, key);
                        channels.Append(" normal(").Append(normalContributors).Append(')');
                        published.Add($"{key} -> {relPath}");
                    }
                }
                if (baseM is { Length: > 0 } && texPaths.Mask != null)
                {
                    var name = baseName + "_" + run.compositor.TimedContentTag(baseM, wM, hM, encSalt, OutputFormatVersion) + "_m.tex";
                    var outPath = Path.Combine(run.texturesDir, name);
                    var relPath = "textures/" + name;
                    if (run.compositor.AlreadyWritten(outPath)
                     || run.compositor.textureLoader.WriteTex(baseM, wM, hM, outPath, compress ? TexEncoding.Bc7 : TexEncoding.Uncompressed))
                    {
                        var key = PublishKey(texPaths.Mask, "m");
                        run.redirects[key] = relPath; Interlocked.Increment(ref run.texturesPatched);
                        Published(texPaths.Mask, key);
                        channels.Append(" mask(").Append(maskContributors).Append(')');
                        published.Add($"{key} -> {relPath}");
                    }
                }
            }

            private void PublishRetargets()
            {
                // A retargeted slot reaches the character only through a rewritten material copy; if the copy fails, withdraw the
                // slot rather than redirect the shared file.
                if (retarget.Count > 0)
                {
                    string? materialRel = null;
                    // Our manifest masks the material, so its base must come from the remembered upstream; a null resolve would read
                    // vanilla and overwrite the body mod's material. A material no mod provides has the game file as its upstream,
                    // known from _upstreamIsGameData or Penumbra echoing the game path back.
                    var liveMaterial = mtrlDisk == null ? run.compositor.penumbra.ResolvePlayer(mtrlGamePath) : null;
                    bool upstreamIsGame = run.compositor._upstreamIsGameData.ContainsKey(mtrlGamePath)
                        || (run.compositor._upstreamByGamePath.TryGetValue(mtrlGamePath, out var remembered)
                            && string.Equals(remembered, mtrlGamePath, StringComparison.OrdinalIgnoreCase));
                    bool upstreamUnknown = liveMaterial != null && run.compositor.IsOwnOutput(liveMaterial) && !upstreamIsGame;
                    if (upstreamUnknown)
                    {
                        run.compositor.log.Warning("[Proteus] {0} resolves to our own copy and its real upstream is not known yet "
                                  + "— not rewriting it from the game's vanilla file; retried next composite",
                            mtrlGamePath);
                    }
                    else
                    {
                        try
                        {
                            var raw = run.compositor.textureLoader.LoadRawFile(mtrlDisk, mtrlGamePath);
                            if (raw != null && TextureLoader.RetargetTexturePaths(raw, retarget) is { } rewritten)
                            {
                                var name = baseName + "_" + run.compositor.TimedContentTag(rewritten, OutputFormatVersion) + ".mtrl";
                                var outPath = Path.Combine(run.skinMaterialsDir, name);
                                if (!File.Exists(outPath))
                                {
                                    Directory.CreateDirectory(run.skinMaterialsDir);
                                    PenumbraModMeta.AtomicWrite(outPath, rewritten);
                                }
                                materialRel = "materials/" + name;
                            }
                        }
                        catch (Exception ex)
                        {
                            run.compositor.log.Warning(ex, "[Proteus] Failed to write a private copy of {0}", mtrlGamePath);
                        }
                    }

                    if (materialRel != null)
                    {
                        run.redirects[mtrlGamePath] = materialRel;
                        published.Add($"{mtrlGamePath} -> {materialRel} (names {string.Join(", ", retarget.Values)})");
                    }
                    else
                    {
                        foreach (var (shared, owned) in retarget)
                        {
                            run.redirects.TryRemove(owned, out _);
                            published.RemoveAll(p => p.StartsWith(owned + " ", StringComparison.OrdinalIgnoreCase));
                            run.compositor.log.Warning("[Proteus] {0} names the shared texture {1}, and it could not be rewritten "
                                      + "to name a private copy — leaving that slot un-composited rather than "
                                      + "redirecting a file every character samples", mtrlGamePath, shared);
                        }
                    }
                }

                run.contributions[mtrlGamePath] = new ChannelContribution(mtrlGamePath,
                    diffuseContributors, normalContributors, maskContributors,
                    diffuseWanted, diffuseBlended || normalBlended || maskBlended, normalWanted);

                if (channels.Length > 0)
                {
                    run.compositor.log.Debug("[Proteus] Composited {0}:{1}", mtrlGamePath, channels);
                    run.compositor.log.Debug("[Proteus]   redirects: {0}", string.Join(" | ", published));
                }
            }

            private void ReportFaults()
            {
                // The headline fault in one line: an overlay asked to paint the skin and nothing reached the diffuse.
                if (diffuseWanted && !diffuseBlended)
                    run.compositor.log.Warning("[Proteus] Nothing reached the diffuse of {0} although an overlay declared one "
                              + "— the body will render its base skin colour (normal: {1}, mask: {2})",
                        mtrlGamePath, normalBlended ? "applied" : "not applied", maskBlended ? "applied" : "not applied");

                // The normal's half of the same fault: relief was declared and none of it landed, so the body keeps whatever
                // normal it already had. Its cause is named per overlay by ReportEmptyNormal; this is the headline.
                if (normalWanted && normalContributors == 0)
                    run.compositor.log.Warning("[Proteus] Nothing reached the normal of {0} although an overlay declared one "
                              + "— the body will render its base skin relief (diffuse: {1}, mask: {2})",
                        mtrlGamePath, diffuseBlended ? "applied" : "not applied", maskBlended ? "applied" : "not applied");
            }

            // TextureLoader caches decoded PNGs across runs (path + mtime) and dedups concurrent requests. Timed even on a hit.
            private byte[]? LoadPng(string path, int w, int h, ResampleFilter filter = ResampleFilter.Auto)
            {
                var t0 = PhaseCounter.Begin();
                try { return run.compositor.textureLoader.LoadPngAsRgba(path, w, h, filter); }
                finally { run.compositor.blendLoadStats.Stop(t0); }
            }

            /// <summary>
            /// Put a freshly loaded base into the doubled layout, and say what size it now is. The output size comes from the
            /// texture's native aspect, not the loaded (squared-off) buffer, so each half gets the native sheet width.
            /// </summary>
            private (byte[] Rgba, int W, int H) ToDoubled(byte[] rgba, int w, int h, string? texGamePath)
            {
                int outW = TextureLoader.BaseTargetSize, outH = outW;
                if (texGamePath != null
                    && run.compositor.textureLoader.BaseNativeSize(run.compositor.ResolveUpstream(texGamePath), texGamePath) is { } native
                    && native.Width > 0 && native.Height > 0)
                {
                    long want = (long)outW * native.Height / (2L * native.Width);
                    int pow = 64;
                    while (pow < want) pow <<= 1;
                    outH = Math.Clamp(pow, 64, outW);
                }
                return (UVRemapService.ExpandMirrored(rgba, w, h, outW, outH), outW, outH);
            }

            /// <summary>
            /// <see cref="LoadBaseNormal"/>, then into the doubled layout when this material is in it. The normal usually has no
            /// overlay, so nothing else would put it in the layout its model now samples.
            /// </summary>
            private byte[] LoadBaseNormalHere(string gamePath, ref int w, ref int h)
            {
                var n = run.compositor.LoadBaseNormal(gamePath, ref w, ref h);
                if (!faceDoubled || n is not { Length: > 0 }) return n;
                var d = ToDoubled(n, w, h, gamePath);
                w = d.W; h = d.H;
                return d.Rgba;
            }

            private byte[]? RemapIfNeeded(byte[]? png, int w, int h, string? srcType, string? overlayPath = null,
                                  ResampleFilter filter = ResampleFilter.Auto)
            {
                // Same counter as LoadPng; they cannot nest because LoadPng is evaluated as this method's argument.
                var tRemap = PhaseCounter.Begin();
                try { return RemapIfNeededCore(png, w, h, srcType, overlayPath, filter); }
                finally { run.compositor.blendLoadStats.Stop(tRemap); }
            }

            /// <summary>
            /// Load an overlay image and bring it into this material's UV space: the path-based form of
            /// <see cref="RemapIfNeeded"/> for callers starting from a file. It decides the decode size from the source's
            /// space, avoiding a wrong-aspect decode that is thrown away.
            /// </summary>
            private byte[]? LoadRemapped(string path, int w, int h, string? srcType,
                                 ResampleFilter filter = ResampleFilter.Auto)
            {
                bool srcIsDoubledFace = srcType != null
                    && string.Equals(srcType, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase);

                // A doubled sheet on a doubled destination is already in its layout: load whole, fold nothing.
                if (srcIsDoubledFace && faceDoubled) return LoadPng(path, w, h, filter);

                if (srcIsDoubledFace)
                {
                    // Timed as a load, on the same counter LoadPng feeds.
                    var t0 = PhaseCounter.Begin();
                    try { return FoldFaceSplit(path, w, h, filter); }
                    finally { run.compositor.blendLoadStats.Stop(t0); }
                }

                // Vanilla-face-layout art (declared, or undeclared on a face material) on a doubled material describes both sides,
                // so it is expanded into both halves.
                if (faceDoubled
                    && (srcType == null
                        || string.Equals(srcType, UVRemapService.FaceSpace, StringComparison.OrdinalIgnoreCase)))
                {
                    var t0 = PhaseCounter.Begin();
                    try
                    {
                        // At half the output width (the vanilla sheet); ExpandMirrored writes both halves at full size.
                        var png = LoadPng(path, Math.Max(1, w / 2), h, filter);
                        return png == null ? null
                             : UVRemapService.ExpandMirrored(png, Math.Max(1, w / 2), h, w, h);
                    }
                    finally { run.compositor.blendLoadStats.Stop(t0); }
                }

                return RemapIfNeeded(LoadPng(path, w, h, filter), w, h, srcType, path, filter);
            }

            /// <summary>
            /// Fold a doubled face sheet into the vanilla face layout, at (w × h). Loaded at twice the destination width so the
            /// crop lands at the base's own size.
            /// </summary>
            private byte[]? FoldFaceSplit(string path, int w, int h, ResampleFilter filter)
            {
                var doubled = run.compositor.textureLoader.LoadPngAsRgba(path, w * 2, h, filter);
                return doubled == null ? null : UVRemapService.CropRightHalf(doubled, w * 2, h);
            }

            private byte[]? RemapIfNeededCore(byte[]? png, int w, int h, string? srcType, string? overlayPath = null,
                                      ResampleFilter filter = ResampleFilter.Auto)
            {
                if (png == null || srcType == null) return png;
                // A doubled face sheet that reached the skin path must be folded by a crop, not resampled: its right half is the
                // vanilla layout ("keep the +X side and mirror it"). No live caller takes this (LoadRemapped folds first); kept
                // for buffer callers.
                if (string.Equals(srcType, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase))
                    return overlayPath == null ? png : FoldFaceSplit(overlayPath, w, h, filter) ?? png;
                if (dstBodyType == null) return png;
                if (string.Equals(srcType, dstBodyType, StringComparison.OrdinalIgnoreCase)) return png;
                // Any source → gen2 (vanilla): vanilla UV is the right half of bibo UV space.
                // Convert to bibo first (via transfer map if needed), crop right half, resize.
                if (string.Equals(dstBodyType, "gen2", StringComparison.OrdinalIgnoreCase))
                {
                    if (overlayPath == null) return png;
                    // Cached: a vanilla sibling sends every overlay, mask and index through this costly crop and resample. Keyed on the
                    // overlay file plus everything the result depends on, including the source space.
                    var tGen2 = PhaseCounter.Begin();
                    try
                    {
                        var srcSpace = srcType;
                        return run.compositor.textureLoader.GetOrDerive(overlayPath,
                            $"gen2<{srcSpace}|{(filter == ResampleFilter.Nearest ? "n" : "a")}", w, h, () =>
                            {
                                var native = run.compositor.textureLoader.LoadPngAsRgba(overlayPath, 4096, 4096, filter);
                                if (native == null) return null;
                                byte[] biboSpace;
                                if (string.Equals(srcSpace, "bibo", StringComparison.OrdinalIgnoreCase))
                                {
                                    biboSpace = native;
                                }
                                else
                                {
                                    var converted = run.compositor.uvRemap.Remap(native, 4096, 4096, srcSpace, "bibo");
                                    if (ReferenceEquals(converted, native)) return null; // map not found — skip
                                    biboSpace = converted;
                                }
                                var rightHalf = UVRemapService.CropRightHalf(biboSpace, 4096, 4096);
                                // Honour the caller's filter: bilinear would interpolate the row selectors of an index map.
                                return TextureLoader.Resample(rightHalf, 2048, 4096, w, h, filter);
                            }) ?? png;
                    }
                    finally { run.compositor.blendGen2Stats.Stop(tGen2); }
                }
                // Transfer-map paths operate at 4096×4096. If the overlay was loaded at a
                // smaller size (e.g. base texture is 2048), reload at full res, remap, resize.
                if (w != 4096 || h != 4096)
                {
                    if (overlayPath == null) return png;
                    var native4k = run.compositor.textureLoader.LoadPngAsRgba(overlayPath, 4096, 4096, filter);
                    if (native4k == null) return png;
                    var remapped4k = run.compositor.uvRemap.Remap(native4k, 4096, 4096, srcType, dstBodyType);
                    if (ReferenceEquals(remapped4k, native4k)) return png;
                    return TextureLoader.Resample(remapped4k, 4096, 4096, w, h, filter);
                }
                return run.compositor.uvRemap.Remap(png, w, h, srcType, dstBodyType);
            }

            // Loads an overlay's Index texture, then replaces its row selection (R = row, G = subrow blend) with any active
            // mask's own Index companion wherever that mask has ≥ 50% coverage. A hard swap, not a blend: R is a row id.
            // `idxmerge` times only the clone and merge loops (exclusive of loads charged to `load`), so the counters never nest.
            private byte[]? LoadIndexMerged(string idxPath, int w, int h, string? srcType, string modDir)
            {
                // Nearest end to end: R is a row id, so the resize must not interpolate it either (see ResampleFilter).
                var idx = LoadRemapped(idxPath, w, h, srcType, ResampleFilter.Nearest);
                if (idx == null || !run.maskAssetsByMod.TryGetValue(modDir, out var assets)) return idx;

                // Masks handled elsewhere are not merged here (that would double them): a mask colorset ⇒ the top diffuse pass; a
                // mask shell ⇒ the shell. A mod with no mask colorset still merges; the mask's W term keeps it to the soft edges.
                if (run.maskRowsByMod.ContainsKey(modDir) || run.maskShellMods.Contains(modDir)) return idx;

                // The cached array is shared with read-only callers (TextureLoader's mutation contract): clone before writing.
                var tClone = PhaseCounter.Begin();
                idx = (byte[])idx.Clone();
                run.compositor.blendIdxMergeStats.Stop(tClone);
                foreach (var (maskPath, _, maskIndexPath) in assets)
                {
                    if (maskIndexPath == null) continue;
                    var maskPng = LoadRemapped(maskPath, w, h, srcType);
                    var maskIdx = LoadRemapped(maskIndexPath, w, h, srcType, ResampleFilter.Nearest);
                    if (maskPng == null || maskIdx == null) continue;
                    // Timed per mask, excluding the loads above.
                    var tMerge = PhaseCounter.Begin();
                    for (int i = 0; i < idx.Length; i += 4)
                    {
                        if (maskPng[i + 3] < 128) continue;
                        idx[i]     = maskIdx[i];
                        idx[i + 1] = maskIdx[i + 1];
                    }
                    run.compositor.blendIdxMergeStats.Stop(tMerge);
                }
                return idx;
            }

            // Combined coverage mask for a mod's active masks at a given size, cached per run. A mask sets coverage within its
            // alpha: cov' = lerp(cov, gray, a). Stored as W = Π(1-aᵢ) and T (accumulated gray*a); apply cov' = cov*W + T,
            // gated by base alpha > 0. `paths` is highest-priority-first, applied in reverse so the top mask wins.
            // Returns null when none active. W/T are bytes (0–255).
            private (byte[] W, byte[] T)? CombinedMaskAt(string modDir, int w, int h, string? srcBodyType = null)
            {
                if (!run.maskPathsByMod.TryGetValue(modDir, out var paths) || paths.Count == 0) return null;
                // dstFaceSpace is in the key because dstBodyType is null for every face material.
                var bodyKey = $"{srcBodyType ?? ""}→{dstBodyType ?? dstFaceSpace ?? ""}";
                return run.combinedMaskCache.GetOrAdd((modDir, w, h, bodyKey), _ =>
                {
                    int n = w * h;
                    byte[]? wArr = null, tArr = null;
                    for (int pidx = paths.Count - 1; pidx >= 0; pidx--)
                    {
                        var m = LoadRemapped(paths[pidx], w, h, srcBodyType);
                        if (m == null) continue;
                        // Masks accumulate in order, but within one mask every pixel is independent, so the inner pass parallelises.
                        var src = m;
                        if (wArr == null)
                        {
                            var wNew = new byte[n];
                            var tNew = new byte[n];
                            ParallelPixels(0, n, 1, (fromPi, toPi) =>
                            {
                                for (int pi = fromPi; pi < toPi; pi++)
                                {
                                    int o = pi * 4;
                                    int a = src[o + 3];
                                    int g = (src[o] * 77 + src[o + 1] * 150 + src[o + 2] * 29) >> 8; // luminance
                                    wNew[pi] = (byte)(255 - a);       // (1-a)
                                    tNew[pi] = (byte)(g * a / 255);   // gray*a
                                }
                            });
                            wArr = wNew;
                            tArr = tNew;
                        }
                        else
                        {
                            var wCur = wArr;
                            var tCur = tArr!;
                            ParallelPixels(0, n, 1, (fromPi, toPi) =>
                            {
                                for (int pi = fromPi; pi < toPi; pi++)
                                {
                                    int o = pi * 4;
                                    int a = src[o + 3];
                                    int g = (src[o] * 77 + src[o + 1] * 150 + src[o + 2] * 29) >> 8;
                                    int inv = 255 - a;
                                    // T' = T*(1-a) + gray*a ;  W' = W*(1-a)
                                    tCur[pi] = (byte)(tCur[pi] * inv / 255 + g * a / 255);
                                    wCur[pi] = (byte)(wCur[pi] * inv / 255);
                                }
                            });
                        }
                    }
                    return wArr == null ? ((byte[] W, byte[] T)?)null : (wArr, tArr!);
                });
            }

            // Garment silhouette for a mod at (w,h) in body UV: the union of its overlays' diffuse alpha (gear shells and
            // skin-painted garments), so an unmasked garment casts AO / indent like a masked strap. A garment opaque everywhere
            // casts no halo. Returns null if the mod has no overlay with a diffuse. Mask shells are not in allOverlays.
            private byte[]? GarmentSilhouette(string modDir, int w, int h)
            {
                var tSil = PhaseCounter.Begin();
                try { return GarmentSilhouetteCore(modDir, w, h); }
                finally { run.compositor.blendSilhouetteStats.Stop(tSil); }
            }

            private byte[]? GarmentSilhouetteCore(string modDir, int w, int h)
            {
                byte[]? sil = null;
                foreach (var (gEntry, gOverlay) in run.allOverlays)
                {
                    if (!string.Equals(gEntry.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)) continue;
                    var gd = gOverlay.Descriptor;
                    if (gd.IsMaskShell || gd.Diffuse == null) continue;   // mask shells trace their mask, not this
                    // A skin overlay is baked into one material, so only trace it for the material being composited.
                    if (gd.Layer == OverlayLayer.Skin
                        && !gd.MaterialGamePaths.Contains(mtrlGamePath, StringComparer.OrdinalIgnoreCase))
                        continue;
                    // A gear shell's art lives in body UV, so it describes a garment only on a body-UV material.
                    if (gd.Layer == OverlayLayer.Gear)
                    {
                        if (!IsBodyUvMaterial(mtrlGamePath)) continue;
                        // And only a shell cut from a body surface leaves its art in body UV; a face shell's makeup is in face UV. Keyed on
                        // the surface: an overlay naming no surface is left in.
                        var gKeys = ShellSurface.KeysFor(gd.MaterialGamePaths);
                        if (gKeys.Count > 0 && !gKeys.Any(k => k.IsBody)) continue;
                    }

                    var gSrc = gd.SourceBodyType;
                    if (gSrc == null)
                    {
                        if (gd.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)))
                            gSrc = "bibo";
                        else if (gd.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3"))
                            gSrc = "gen3";
                    }
                    var dp = Path.Combine(gEntry.SidecarRoot, gd.Diffuse);
                    var img = LoadRemapped(dp, w, h, gSrc);
                    if (img == null) continue;

                    // A print has no silhouette of its own (it borrows the fabric's), so its rows are masked out rather than traced.
                    // Cheap preset test first: almost no overlays print.
                    if (AnyBlendRow(gOverlay.ColorTableRows))
                    {
                        byte[]? gIdx = gd.Index != null
                            ? LoadIndexMerged(Path.Combine(gEntry.SidecarRoot, gd.Index), w, h,
                                              gSrc, gEntry.ModDirectory)
                            : null;
                        img = PaintCoverage(img, gIdx, BuildRowDict(gOverlay.ColorTableRows), w, h,
                                            gd.Index != null);
                    }

                    sil ??= new byte[w * h];
                    var s = sil; var src = img;
                    ParallelPixels(0, w * h, 1, (from, to) =>
                    {
                        for (int p = from; p < to; p++)
                            if (src[p * 4 + 3] > s[p]) s[p] = src[p * 4 + 3];   // union of diffuse alpha
                    });
                }
                return sil;
            }

            // Load the material's base diffuse at most once, memoising failure. Returns the buffer or null when there is nothing
            // to composite onto (a buffer, not a bool, for nullability flow).
            private byte[]? EnsureBaseDiffuse()
            {
                if (baseDTried) return baseD is { Length: > 0 } ? baseD : null;
                baseDTried = true;
                if (texPaths.Diffuse == null) { baseD = Array.Empty<byte>(); return null; }

                var diffDisk = run.compositor.ResolveUpstream(texPaths.Diffuse);
                var loaded = run.compositor.TimedLoadBaseTexture(diffDisk, texPaths.Diffuse);
                if (loaded.HasValue) { baseD = loaded.Value.rgba; wD = loaded.Value.width; hD = loaded.Value.height; }
                // Into the doubled layout before anything else sees it, including SnapshotBaseDiffuse.
                if (faceDoubled && baseD is { Length: > 0 })
                    (baseD, wD, hD) = ToDoubled(baseD, wD, hD, texPaths.Diffuse);
                baseD ??= Array.Empty<byte>();

                // Skin mods invent paths with no SqPack fallback, so a failed resolve loses the whole diffuse: say so.
                if (baseD.Length == 0)
                    run.compositor.log.Warning("[Proteus] Base diffuse failed to load for {0}: {1} resolved to {2} — no "
                              + "diffuse can be composited onto this material",
                        mtrlGamePath, texPaths.Diffuse, diffDisk ?? "(nothing)");

                return baseD.Length > 0 ? baseD : null;
            }

            // Fingerprint the base immediately before the first edit (called from each mutating site), not at load.
            private void SnapshotBaseDiffuse()
            {
                if (baseDiffuseTag == null && baseD is { Length: > 0 })
                    baseDiffuseTag = run.compositor.TimedContentTag(baseD, wD, hD, encSalt, OutputFormatVersion);
            }

            // Load the base mask at most once (Array.Empty on failure); its size is the bake's resolution. A shared flat-colour
            // placeholder is first brought up to the diffuse's size.
            private void EnsureBaseMask()
            {
                if (baseM != null || texPaths.Mask == null) return;
                var loaded = run.compositor.TimedLoadBaseTexture(run.compositor.ResolveUpstream(texPaths.Mask), texPaths.Mask);
                if (loaded.HasValue) { baseM = loaded.Value.rgba; wM = loaded.Value.width; hM = loaded.Value.height; }
                if (faceDoubled && baseM is { Length: > 0 })
                    (baseM, wM, hM) = ToDoubled(baseM, wM, hM, texPaths.Mask);

                if (baseM is { Length: > 0 } && IsSharedTexturePath(texPaths.Mask)
                 && EnsureBaseDiffuse() != null && (long)wD * hD > (long)wM * hM)
                {
                    run.compositor.log.Debug("[Proteus] Base mask of {0} is the shared placeholder {1} at {2}x{3} — baking at "
                            + "the diffuse's {4}x{5} instead", mtrlGamePath, texPaths.Mask, wM, hM, wD, hD);
                    baseM = run.compositor.textureLoader.ScaleRgba(baseM, wM, hM, wD, hD);
                    wM = wD; hM = hD;
                }
                baseM ??= Array.Empty<byte>();
            }

            private string? SrcTypeOf(OverlayDescriptor d)
            {
                if (d.SourceBodyType != null) return d.SourceBodyType;
                if (d.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)))
                    return "bibo";
                if (d.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3"))
                    return "gen3";
                return null;
            }

            // One overlay's effective coverage: art alpha, then the Masks group, then opacity (same rules as CovAt).
            private byte[]? CoverageOf(OverlayEntry e, ResolvedOverlay o, int tw, int th)
            {
                var d = o.Descriptor;
                var srcT = SrcTypeOf(d);
                byte[]? cov = null;

                if (d.Diffuse != null)
                {
                    var p = Path.Combine(e.SidecarRoot, d.Diffuse);
                    cov = LoadRemapped(p, tw, th, srcT);
                }
                else if (d.Normal != null)
                {
                    // Alpha, not blue: on skin.shpk blue is skin-colour influence, never where the overlay is.
                    var p = Path.Combine(e.SidecarRoot, d.Normal);
                    var n = LoadRemapped(p, tw, th, srcT);
                    if (n != null)
                    {
                        cov = new byte[n.Length];
                        for (int i = 0; i < n.Length; i += 4) cov[i + 3] = n[i + 3];
                    }
                }
                else if (d.Mask != null)
                {
                    var p = Path.Combine(e.SidecarRoot, d.Mask);
                    cov = LoadRemapped(p, tw, th, srcT);
                }
                if (cov == null) return null;

                var msk = CombinedMaskAt(e.ModDirectory, tw, th, srcT);
                if (msk != null) cov = ApplyCoverageMask(cov, msk.Value.W, msk.Value.T, MaskAdds(e, o));

                var r = BuildRowDict(o.ColorTableRows);
                if (d.Index != null && r.Values.Any(x => x.A.Opacity != 0 || x.B.Opacity != 0))
                {
                    var ip = Path.Combine(e.SidecarRoot, d.Index);
                    var idx = LoadIndexMerged(ip, tw, th, srcT, e.ModDirectory);
                    if (idx != null) cov = ApplyIndexedOpacity(cov, idx, r);
                }
                else if (d.Index == null)
                {
                    r.TryGetValue(15, out var r16);
                    int op = r16?.A.Opacity ?? 0;
                    if (op != 0) cov = ScaleOverlayAlpha(cov, op);
                }

                // Take the print rows out, so a print (usually opaque sheet-wide) cannot claim the group beneath it.
                if (AnyBlendRow(r))
                {
                    byte[]? pIdx = d.Index != null
                        ? LoadIndexMerged(Path.Combine(e.SidecarRoot, d.Index), tw, th, srcT, e.ModDirectory)
                        : null;
                    cov = PaintCoverage(cov, pIdx, r, tw, th, d.Index != null);
                }
                return cov;
            }

            // What one overlay claims in `ch` from the overlays beneath it: its coverage, except that a Compound normal-only
            // overlay claims the normal channel only where it has relief (OverlayBlend.ReliefPresence). Replace mode overwrites
            // by design, and an overlay with a diffuse is a surface whose silhouette hides the relief under it.
            private byte[]? ClaimCoverageOf(OverlayEntry e, ResolvedOverlay o, int tw, int th, OverlayChannel ch)
            {
                var cov = CoverageOf(e, o, tw, th);
                var d = o.Descriptor;
                if (cov == null || ch != OverlayChannel.Normal || d.Diffuse != null || d.Normal == null
                    || d.NormalMode != NormalMode.Compound)
                    return cov;

                var n = LoadRemapped(Path.Combine(e.SidecarRoot, d.Normal), tw, th, SrcTypeOf(d));
                return n == null ? cov : ScaleAlphaByPlane(cov, ReliefPresence(n, tw, th));
            }

            // Union alpha of every same-mod overlay composited above this one THAT SUPPLIES `ch`, built as a suffix union
            // (above i = above i+1 plus the overlay between). The buffer is shared when that overlay adds nothing.
            // The channel scoping is what lets one mod ship a whole-body diffuse group and a whole-body normal group:
            // neither can claim the other's channel, so neither erases the other. See OverlayBlend.Supplies.
            private byte[]? ClaimAt(string modDir, int stackIdx, int tw, int th, OverlayChannel ch)
            {
                var key = (modDir, stackIdx, tw, th, ch);
                if (claimCache.TryGetValue(key, out var hit)) return hit;

                byte[]? acc = null;
                if (stackIdx + 1 < pairs.Count)
                {
                    var above = ClaimAt(modDir, stackIdx + 1, tw, th, ch);
                    acc = above;

                    var (e, o) = pairs[stackIdx + 1];
                    if (string.Equals(e.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)
                     && Supplies(o.Descriptor, ch))
                    {
                        var cov = ClaimCoverageOf(e, o, tw, th, ch);
                        if (cov != null)
                        {
                            // Clone before mutating: `above` is cached and shared by every deeper level.
                            acc = above != null ? (byte[])above.Clone() : new byte[tw * th];
                            UnionAlphaInto(acc, cov);   // alpha-over union
                        }
                    }
                }
                claimCache[key] = acc;
                return acc;
            }

            // Fade a coverage buffer by what the overlays above it already claim IN `ch`. `suppress` times only the clone and
            // serial pass, since ClaimAt's work is already charged to `cov`.
            private byte[]? Suppress(byte[]? cov, OverlayEntry e, ResolvedOverlay o, int stackIdx, int tw, int th,
                                     OverlayChannel ch)
            {
                if (cov == null) return null;

                // A print is clipped by what its mod painted, the opposite of suppression, so it is exempt. Whole-overlay, erring
                // toward showing art.
                if (AnyBlendRow(o.ColorTableRows)) return cov;
                var claim = ClaimAt(e.ModDirectory, stackIdx, tw, th, ch);
                if (claim == null) return cov;

                // Timed from here, after ClaimAt.
                var tSup = PhaseCounter.Begin();
                var dst = (byte[])cov.Clone();
                for (int i = 0, a = 3; i < claim.Length && a < dst.Length; i++, a += 4)
                    dst[a] = (byte)(dst[a] * (255 - claim[i]) / 255);
                run.compositor.blendSuppressStats.Stop(tSup);
                return dst;
            }

            // ── Decode prefetch ───────────────────────────────────────────
            // Overlay decode dominates a recomposite and the loop consumes art strictly in turn, so warm the next few overlays in
            // the background. Depth is bounded by DecodeCacheBudgetBytes.

            // Overlay sizes follow the base textures, so this no-ops until pair 0 establishes wD/hD and wN/hN.
            private void PrefetchAhead(int fromIndex)
            {
                for (int k = fromIndex; k < Math.Min(fromIndex + PrefetchDepth, pairs.Count); k++)
                {
                    var pd = pairs[k].Overlay.Descriptor;
                    var root = pairs[k].Entry.SidecarRoot;
                    if (pd.Diffuse != null && wD > 0) run.WarmBg(Path.Combine(root, pd.Diffuse), wD, hD);
                    if (pd.Normal  != null && wN > 0) run.WarmBg(Path.Combine(root, pd.Normal),  wN, hN);
                    // The colour-row index map, decoded once per overlay.
                    if (pd.Index   != null && wD > 0) run.WarmBg(Path.Combine(root, pd.Index),   wD, hD,
                                                            ResampleFilter.Nearest);

                    // The Masks group is read per mod by CombinedMaskAt at the diffuse size; the cache dedups shared masks.
                    var mod = pairs[k].Entry.ModDirectory;
                    if (wD > 0 && run.maskPathsByMod.TryGetValue(mod, out var mPaths))
                        foreach (var mp in mPaths) run.WarmBg(mp, wD, hD);

                    // ...and each mask's companion relief normal / colour index, read in later passes of the same overlay.
                    if (wD > 0 && run.maskAssetsByMod.TryGetValue(mod, out var mAssets))
                        foreach (var a in mAssets)
                        {
                            if (a.NormalPath != null) run.WarmBg(a.NormalPath, wD, hD);
                            if (a.IndexPath  != null) run.WarmBg(a.IndexPath,  wD, hD, ResampleFilter.Nearest);
                        }
                }
            }
        }
    }
}
