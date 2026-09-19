using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Penumbra.Api.Enums;

namespace Proteus.Services;
using static Proteus.Services.OverlayBlend;

public partial class CompositorService
{
    private sealed partial class CompositeRun
    {
        private sealed partial class MaterialComposite
        {
            private sealed class OverlayBlendPass
            {
                private readonly MaterialComposite material;
                private readonly OverlayEntry entry;
                private readonly ResolvedOverlay resolved;
                private int stackIdx;
                private OverlayDescriptor desc = null!;
                private string? srcBodyType;
                private Dictionary<int, ColorTableRowOverride> rows = null!;
                private ColorTableSubRow row16A = null!;
                private bool hasPrintRows;
                private bool purePrint;
                private string optLabel = null!;
                private byte[]? diffuseOv;
                private byte[]? normalOv;
                private byte[]? covSrc;
                private int covW;
                private int covH;
                private byte[]? ovAfterArt;
                private byte[]? ovAfterMask;
                private byte[]? ovAfterOpacity;
                private bool result;

                public OverlayBlendPass(MaterialComposite material, OverlayEntry entry, ResolvedOverlay resolved)
                {
                    this.material = material;
                    this.entry = entry;
                    this.resolved = resolved;
                }

                public bool Run(ref int pairIndex)
                {
                    if (!Describe(ref pairIndex)) return result;
                    LoadDiffuseArt();
                    LoadNormalArt();
                    if (!LoadMaskArt()) return result;
                    ApplyMasksAndOpacity();
                    ReportMissingArt();
                    RemoveSeamBleed();
                    CompositeDiffuse();
                    CompositeNormal();
                    SuppressSkinColor();
                    return CompositeMask();
                }

                private bool Describe(ref int pairIndex)
                {
                    if (material.run.ct.IsCancellationRequested) { result = false; return false; }

                    material.PrefetchAhead(++pairIndex + 1);

                    // Snapshotted per iteration so local functions close over a value, not the shared counter.
                    stackIdx = pairIndex;

                    desc = resolved.Descriptor;
                    srcBodyType = desc.SourceBodyType;
                    // Infer the source UV space from the overlay's material paths when SourceBodyType is unset (older overlays, sibling
                    // entries). gen3 covers _b and _eve (same UV).
                    if (srcBodyType == null)
                    {
                        if (desc.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)))
                            srcBodyType = "bibo";
                        else if (desc.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3"))
                            srcBodyType = "gen3";
                    }
                    rows = BuildRowDict(resolved.ColorTableRows);
                    rows.TryGetValue(15, out var row16);
                    row16A = row16?.A ?? new ColorTableSubRow();

                    // Does any row print? Every print-specific behaviour below is gated on this, so other overlays keep their old path.
                    hasPrintRows = AnyBlendRow(rows);

                    // Every cell prints: colour only, no relief, shadow or skin-tone suppression. A strict early-out; AnyCoverage decides
                    // for indexed prints.
                    purePrint = AllRowsPrint(rows, desc.Index != null);

                    material.lastSrcBodyTypeByMod[entry.ModDirectory] = srcBodyType;

                    // Diagnostics label; both halves are null for a mod's default data.
                    optLabel = $"{resolved.OptionGroup ?? "(default)"}/{resolved.Option ?? "(default)"}";

                    diffuseOv = null;
                    normalOv = null;

                    // Coverage mask: the diffuse overlay's alpha defines where this overlay applies; with no diffuse it is synthesized
                    // from the normal's blue channel. Every channel is gated by this same mask.
                    covSrc = null;  // coverage source at (covW × covH)
                    covW = 0;
                    covH = 0;
                    return true;
                }

                private void LoadDiffuseArt()
                {
                    // ── Step 1: load diffuse overlay (establishes coverage) ───
                    // A material with no diffuse sampler skips this block and composites only the normal: warn about it too.
                    if (desc.Diffuse != null) material.diffuseWanted = true;

                    if (desc.Diffuse != null && material.texPaths.Diffuse == null && !material.warnedNoDiffuseSampler)
                    {
                        material.warnedNoDiffuseSampler = true;
                        material.run.compositor.log.Warning("[Proteus] Overlay declares a diffuse but the material has no diffuse "
                                  + "sampler, so it cannot be painted onto the skin: {0} (first seen on mod "
                                  + "{1}, {2}) — set the overlay to Cloth to render it on a gear shell instead",
                            material.mtrlGamePath, entry.ModDirectory, optLabel);
                    }

                    if (desc.Diffuse != null && material.texPaths.Diffuse != null)
                    {
                        if (material.EnsureBaseDiffuse() != null)
                        {
                            var diffPath = Path.Combine(entry.SidecarRoot, desc.Diffuse);
                            diffuseOv = material.LoadRemapped(diffPath, material.wD, material.hD, srcBodyType);
                            if (diffuseOv != null)
                            {
                                // All opacity is applied after the Masks-group mask (in CovAt), so the slider scales the mask result.
                                covSrc = diffuseOv; covW = material.wD; covH = material.hD;
                            }
                            else
                            {
                                // RemapIfNeeded returns null only for a null input, so this is a LoadPng failure (TextureLoader logs it once, unprefixed).
                                material.run.compositor.log.Warning("[Proteus] Overlay diffuse failed to load: {0} (mod {1}, {2}) — "
                                          + "the skin diffuse is left untouched",
                                    diffPath, entry.ModDirectory, optLabel);
                            }
                        }
                    }
                }

                private void LoadNormalArt()
                {
                    // ── Step 2: load normal overlay; synthesize coverage if needed ──
                    if (desc.Normal != null && material.texPaths.Normal != null)
                    {
                        material.baseN ??= material.LoadBaseNormalHere(material.texPaths.Normal, ref material.wN, ref material.hN);
                        if (material.baseN.Length > 0)
                        {
                            var normPath = Path.Combine(entry.SidecarRoot, desc.Normal);
                            normalOv = material.LoadRemapped(normPath, material.wN, material.hN, srcBodyType);
                        }

                        if (normalOv != null && covSrc == null)
                        {
                            // No diffuse overlay — synthesize coverage from normal blue channel.
                            var synth = new byte[normalOv.Length];
                            var nOv = normalOv;
                            ParallelPixels(0, nOv.Length, 4, (fromSi, toSi) =>
                            {
                                for (int si = fromSi; si < toSi; si += 4)
                                {
                                    synth[si] = synth[si + 1] = synth[si + 2] = 255;
                                    synth[si + 3] = nOv[si + 2]; // blue → opacity
                                }
                            });
                            // Opacity (indexed and flat) deferred — CovAt applies it after the Masks-group mask.
                            diffuseOv = synth;
                            covSrc = synth; covW = material.wN; covH = material.hN;
                        }
                    }
                }

                private bool LoadMaskArt()
                {
                    // ── Step 3: mask-only overlay — coverage from the mask's own alpha ──
                    // No diffuse or normal defines the silhouette, so the mask PNG's alpha is the coverage.
                    if (covSrc == null && desc.Mask != null && material.texPaths.Mask != null)
                    {
                        // Into the doubled layout too (inside EnsureBaseMask): the mask is sampled with the rewritten model's uv0.
                        material.EnsureBaseMask();
                        if (material.baseM!.Length > 0)
                        {
                            var maskPath3 = Path.Combine(entry.SidecarRoot, desc.Mask);
                            var maskOv = material.LoadRemapped(maskPath3, material.wM, material.hM, srcBodyType);
                            if (maskOv != null)
                            {
                                // Flat opacity deferred — CovAt applies it after the Masks-group mask.
                                covSrc = maskOv; covW = material.wM; covH = material.hM;
                            }
                        }
                    }
                    else if (covSrc == null && desc.Mask != null && material.texPaths.Mask == null)
                    {
                        material.run.compositor.log.Warning("[Proteus] Mask-only overlay but material has no mask texture: {0}", material.mtrlGamePath);
                    }

                    if (covSrc == null)
                    {
                        // Nothing to composite: log that this overlay contributed nothing (any specific reason was warned above).
                        material.run.compositor.log.Debug("[Proteus] No coverage for {0} on {1} ({2}) — overlay contributes nothing",
                            entry.ModDirectory, material.mtrlGamePath, optLabel);
                        { result = true; return false; }
                    }
                    return true;
                }

                private void ApplyMasksAndOpacity()
                {
                    // ── Per-mod transparency masks + opacity ─────────────────
                    // diffuseOv is consumed directly by Phase A, so apply mask then opacity here; covSrc stays raw for CovAt.
                    // Each stage returns a new buffer, so the erasure report can say which one emptied the overlay.
                    ovAfterArt = diffuseOv;
                    ovAfterMask = diffuseOv;
                    ovAfterOpacity = diffuseOv;
                    if (desc.Diffuse != null && diffuseOv != null)
                    {
                        var msk = material.CombinedMaskAt(entry.ModDirectory, covW, covH, srcBodyType);
                        if (msk != null)
                            diffuseOv = ApplyCoverageMask(diffuseOv, msk.Value.W, msk.Value.T, MaskAdds(entry, resolved));
                        ovAfterMask = diffuseOv;
                        if (desc.Index != null && rows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                        {
                            var idxPath = Path.Combine(entry.SidecarRoot, desc.Index);
                            var idD = material.LoadIndexMerged(idxPath, covW, covH, srcBodyType, entry.ModDirectory);
                            if (idD != null) diffuseOv = ApplyIndexedOpacity(diffuseOv, idD, rows);
                        }
                        else if (desc.Index == null && row16A.Opacity != 0)
                            diffuseOv = ScaleOverlayAlpha(diffuseOv, row16A.Opacity);
                        ovAfterOpacity = diffuseOv;
                    }

                    // Phase A reads diffuseOv directly, so it needs the same fade from above; Suppress() clones, leaving covSrc raw.
                    diffuseOv = material.Suppress(diffuseOv, entry, resolved, stackIdx, covW, covH);
                }

                private void ReportMissingArt()
                {
                    // ── "Where did my overlay go?" ────────────────────────────
                    // Art that loaded but came out with no covered texel is a silent failure: name the stage that emptied it.
                    // Only the failing overlay pays; the healthy path is one AnyCoverage.
                    if (desc.Diffuse != null && ovAfterArt != null && !AnyCoverage(diffuseOv))
                    {
                        string why;
                        if (!AnyCoverage(ovAfterArt))
                            why = "its own art has no opaque texel";
                        else if (!AnyCoverage(ovAfterMask))
                            why = "the Masks group leaves nothing of it";
                        else if (!AnyCoverage(ovAfterOpacity))
                        {
                            var neg = rows.Where(kv => kv.Value.A.Opacity < 0 || kv.Value.B.Opacity < 0)
                                .Select(kv => $"row {kv.Key + 1}"
                                    + (kv.Value.A.Opacity < 0 ? $" A={kv.Value.A.Opacity}" : "")
                                    + (kv.Value.B.Opacity < 0 ? $" B={kv.Value.B.Opacity}" : ""))
                                .ToList();
                            why = neg.Count > 0
                                ? $"a negative colour-row opacity fades it away ({string.Join(", ", neg)})"
                                : "its colour-row opacity leaves nothing";
                        }
                        else
                        {
                            // Only overlays that actually contributed to the claim (not pure prints or failed loads). Recomputed, since this
                            // runs only for an overlay already empty.
                            var above = new List<string>();
                            for (int j = stackIdx + 1; j < material.pairs.Count; j++)
                            {
                                var (e2, o2) = material.pairs[j];
                                if (!string.Equals(e2.ModDirectory, entry.ModDirectory,
                                                   StringComparison.OrdinalIgnoreCase)) continue;
                                if (!AnyCoverage(material.CoverageOf(e2, o2, covW, covH))) continue;
                                above.Add(o2.OptionGroup != null && o2.Option != null
                                    ? $"{o2.OptionGroup}/{o2.Option}"
                                    : o2.Option ?? o2.OptionGroup ?? "an overlay with no option group");
                            }
                            why = above.Count > 0
                                ? $"it is fully covered by {string.Join(", ", above)} above it in the stack"
                                : "it was suppressed by another overlay in the same mod";
                        }

                        // Once per (overlay, material, reason) per session; a new cause is still reported.
                        if (material.run.compositor._erasureReported.TryAdd($"{entry.ModDirectory} {optLabel} {material.mtrlGamePath} {why}", 0))
                            material.run.compositor.log.Information("[Proteus] {0} ({1}) paints nothing on {2}: {3}",
                                entry.ModDirectory, optLabel, material.mtrlGamePath, why);
                    }
                }

                private void RemoveSeamBleed()
                {
                    // ── UV-seam bleed removal ─────────────────────────────────
                    // Only for coverage that was cross-UV converted; native overlays are verbatim. Decided on final coverage and dropped
                    // from covSrc and diffuseOv so channels stay consistent. Skip gen2: a right-half crop has no transfer-map seams.
                    if (srcBodyType != null && material.dstBodyType != null
                        && !string.Equals(srcBodyType, material.dstBodyType, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(material.dstBodyType, "gen2", StringComparison.OrdinalIgnoreCase))
                    {
                        var decision = CovAt(covW, covH);
                        // Timed separately: ComputeSeamDropMask's summed-area table is not in uvRemap.RemapStats.
                        var tSeamDrop = PhaseCounter.Begin();
                        var dropMask = decision != null
                            ? material.run.compositor.uvRemap.ComputeSeamDropMask(decision, covW, covH, srcBodyType, material.dstBodyType)
                            : null;
                        material.run.compositor.blendSeamDropStats.Stop(tSeamDrop);
                        if (dropMask != null)
                        {
                            var cov = covSrc!;
                            var dif = diffuseOv != null && diffuseOv.Length == cov.Length ? diffuseOv : null;
                            var drop = dropMask;
                            ParallelPixels(0, drop.Length, 1, (fromDi, toDi) =>
                            {
                                for (int di = fromDi; di < toDi; di++)
                                {
                                    if (!drop[di]) continue;
                                    int o = di * 4;
                                    cov[o] = cov[o + 1] = cov[o + 2] = cov[o + 3] = 0;
                                    if (dif != null)
                                        dif[o] = dif[o + 1] = dif[o + 2] = dif[o + 3] = 0;
                                }
                            });
                        }
                    }
                }

                private void CompositeDiffuse()
                {
                    // ── Phase A: diffuse composite ────────────────────────────
                    if (desc.Diffuse != null && diffuseOv != null && material.baseD is { Length: > 0 })
                    {
                        material.SnapshotBaseDiffuse();

                        // The clip a print is multiplied through: everything this mod already painted on this material; null ⇒ print shows nothing.
                        material.paintedByMod.TryGetValue(entry.ModDirectory, out var clip);
                        if (hasPrintRows && clip == null
                            && rows.Values.Any(r => (r.A.Blend != RowBlend.Paint && !r.A.OntoBeneath)
                                                 || (r.B.Blend != RowBlend.Paint && !r.B.OntoBeneath)))
                            material.run.compositor.log.Debug("[Proteus] {0} ({1}) prints onto {2}, but this mod has painted nothing "
                                    + "here yet — a print colours its own mod's fabric, and on bare skin "
                                    + "there is nothing to print on", entry.ModDirectory, optLabel, material.mtrlGamePath);

                        byte[]? idD = null;
                        if (desc.Index != null)
                            idD = material.LoadIndexMerged(Path.Combine(entry.SidecarRoot, desc.Index), material.wD, material.hD,
                                                  srcBodyType, entry.ModDirectory);

                        // An indexed print whose _id did not load falls back to all-paint; warn once.
                        if (hasPrintRows && desc.Index != null && idD == null)
                            material.run.compositor.log.Warning("[Proteus] {0} ({1}) declares blend rows and an index, but the index "
                                      + "did not load for {2} — the rows fall back to painting, so nothing "
                                      + "prints. Check the _id texture.",
                                        entry.ModDirectory, optLabel, material.mtrlGamePath);

                        // What this overlay paints, as opposed to prints; the same buffer when nothing prints.
                        var paintCov = PaintCoverage(diffuseOv, idD, rows, material.wD, material.hD, desc.Index != null);

                        if (idD != null)
                        {
                            ApplyIndexedOverlay(material.baseD, diffuseOv, idD, rows, false, material.wD, material.hD, clip);

                            // Glow recipe: which pixels resolve to each row (red/17 = pair, green≥128 = sub-row A), gated by painted coverage,
                            // downsampled. One byte/pixel: 0 = no glow, else 0x80 | (A?0x40) | pairIdx.
                            int gw = Math.Min(material.wD, glowMapCap), gh = Math.Min(material.hD, glowMapCap);
                            var gmap = new byte[gw * gh];
                            for (int my = 0; my < gh; my++)
                            {
                                int sy = gh == material.hD ? my : (int)((long)my * material.hD / gh);
                                for (int mx = 0; mx < gw; mx++)
                                {
                                    int sx = gw == material.wD ? mx : (int)((long)mx * material.wD / gw);
                                    int si = (sy * material.wD + sx) * 4;
                                    if (paintCov[si + 3] == 0) continue;   // outside what this overlay painted
                                    gmap[my * gw + mx] = (byte)(0x80 | (idD[si + 1] >= 128 ? 0x40 : 0) | ((idD[si] / 17) & 0x0F));
                                }
                            }
                            material.glowMaps.Add((entry.ModDirectory, resolved.OptionGroup, resolved.Option, gmap, gw, gh));
                        }
                        else ApplyFlatOverlay(material.baseD, diffuseOv, row16A, material.wD, material.hD, clip);
                        material.diffuseBlended = true; material.diffuseContributors++;

                        // Hand what this overlay painted to the layers above. A print contributes nothing, so prints never print onto prints.
                        if (!material.paintedByMod.TryGetValue(entry.ModDirectory, out var acc))
                            material.paintedByMod[entry.ModDirectory] = acc = new byte[material.wD * material.hD];
                        UnionAlphaInto(acc, paintCov);
                    }
                    else if (desc.Diffuse == null && normalOv != null)
                    {
                        // Deliberate, but indistinguishable in the log from a failed diffuse: say which.
                        material.run.compositor.log.Debug("[Proteus] Normal-only overlay {0} ({1}) on {2} — skin diffuse left "
                                + "untouched by design",
                            entry.ModDirectory, optLabel, material.mtrlGamePath);
                    }
                }

                private void CompositeNormal()
                {
                    // Normal-only (and mask-only) overlays no longer synthesize a diffuse tint. The synthesis below is disabled but kept
                    // current (EnsureBaseDiffuse, diffuseBlended) so it still works if revived.
                    /*
                    else if (desc.Diffuse == null && normalOv != null && texPaths.Diffuse != null && desc.GenerateDiffuse)
                    {
                        // Normal-only overlay: apply synthesized tint (Row 16 color) to the diffuse
                        // channel. Skipped when GenerateDiffuse is false — the author wants the normal
                        // (and any mask) applied without altering the skin diffuse.
                        if (EnsureBaseDiffuse() is { } tintBaseD)
                        {
                            var tint = CovAt(wD, hD);
                            if (tint != null)
                            {
                                SnapshotBaseDiffuse();
                                ApplyFlatOverlay(tintBaseD, tint, row16A, wD, hD);
                                diffuseBlended = true; diffuseContributors++;
                            }
                        }
                    }
                    */

                    // ── Phase B: normal composite ─────────────────────────────
                    // PaintCovAt, not CovAt: a print has no relief of its own, and Suppress does not fade prints.
                    if (normalOv != null && material.baseN is { Length: > 0 })
                    {
                        var nCov = PaintCovAt(material.wN, material.hN);
                        if (!hasPrintRows || AnyCoverage(nCov))
                        {
                            // Replace mode is a plain alpha-over: at full coverage the base is gone (no doubled slopes), and RGB includes blue,
                            // so the author's skin-colour influence survives.
                            if (desc.NormalMode == NormalMode.Replace)
                                AlphaComposite(material.baseN, normalOv, material.wN, material.hN, nCov);
                            else
                                CompoundNormal(material.baseN, normalOv, material.wN, material.hN, nCov);
                            material.normalBlended = true; material.normalContributors++;
                        }
                    }
                }

                private void SuppressSkinColor()
                {
                    // ── Phase B2: suppress skin-color influence under the overlay ──
                    // skin.shpk reads the normal's blue channel as skin colour influence, which re-tints overlay pixels by skin tone.
                    // Fade it by diffuse coverage so opaque fabric keeps its authored colour. Diffuse overlays only; strength = global
                    // setting × SkinToneMask (null = full); 0 disables it. Pure prints are excluded, or they would strip skin tone body-wide.
                    float skinMask = material.run.compositor.config.SkinColorSuppression * (desc.SkinToneMask ?? 1f);
                    if (desc.Diffuse != null && material.texPaths.Normal != null && skinMask > 0f && !purePrint)
                    {
                        material.baseN ??= material.LoadBaseNormalHere(material.texPaths.Normal, ref material.wN, ref material.hN);
                        if (material.baseN.Length > 0)
                        {
                            // AnyCoverage, not a null check: most prints arrive here as a non-null all-zero mask.
                            var scMask = PaintCovAt(material.wN, material.hN);
                            if (scMask != null && (!hasPrintRows || AnyCoverage(scMask)))
                            {
                                // Weight by the composited overlay colour, so dark dyes keep skin tone and bright dyes are fully de-tinted.
                                byte[]? diffAtN = material.baseD is { Length: > 0 }
                                    ? (material.wD == material.wN && material.hD == material.hN ? material.baseD : material.run.compositor.textureLoader.ScaleRgba(material.baseD, material.wD, material.hD, material.wN, material.hN))
                                    : null;
                                SuppressSkinColorInfluence(material.baseN, scMask, diffAtN, material.wN, material.hN, skinMask);
                                // A real rewrite of the normal buffer, so it is ours to publish; not a contributor bump.
                                material.normalBlended = true;
                            }
                        }
                    }
                }

                private bool CompositeMask()
                {
                    // ── Phase D: mask texture composite ───────────────────────
                    if (desc.Mask != null && material.texPaths.Mask != null)
                    {
                        material.EnsureBaseMask();
                        if (material.baseM!.Length > 0)
                        {
                            var maskPathD = Path.Combine(entry.SidecarRoot, desc.Mask);
                            var ov = material.LoadRemapped(maskPathD, material.wM, material.hM, srcBodyType);
                            // PaintCovAt as in Phase B: gloss and specular describe a surface, which a print lacks.
                            var mCov = ov != null ? PaintCovAt(material.wM, material.hM) : null;
                            if (ov != null && (!hasPrintRows || AnyCoverage(mCov)))
                            {
                                AlphaComposite(material.baseM, ov, material.wM, material.hM, mCov);
                                material.maskBlended = true; material.maskContributors++;
                            }
                        }
                    }
                    return true;
                }

                // Returns coverage at (tw × th): mask first, then opacity (indexed or flat). covSrc is raw.
                private byte[]? CovAt(int tw, int th)
                {
                    byte[]? cov;
                    if (tw == covW && th == covH)
                    {
                        cov = covSrc; // raw seed
                    }
                    else if (desc.Diffuse != null)
                    {
                        // Reload at the requested size and remap into the destination UV space; without the remap coverage lands in the
                        // source space and fringes at UV-island boundaries.
                        var diffPath = Path.Combine(entry.SidecarRoot, desc.Diffuse);
                        cov = material.LoadRemapped(diffPath, tw, th, srcBodyType);
                    }
                    else
                    {
                        cov = material.run.compositor.textureLoader.ScaleRgba(covSrc!, covW, covH, tw, th);
                    }
                    // Mask first.
                    if (cov != null)
                    {
                        var mask = material.CombinedMaskAt(entry.ModDirectory, tw, th, srcBodyType);
                        if (mask != null)
                            cov = ApplyCoverageMask(cov, mask.Value.W, mask.Value.T, MaskAdds(entry, resolved));
                    }
                    // Opacity after mask (TextureLoader cache deduplicates the index-texture load).
                    if (cov != null && desc.Index != null && rows.Values.Any(r => r.A.Opacity != 0 || r.B.Opacity != 0))
                    {
                        var idxPath = Path.Combine(entry.SidecarRoot, desc.Index);
                        var idxCov = material.LoadIndexMerged(idxPath, tw, th, srcBodyType, entry.ModDirectory);
                        if (idxCov != null) cov = ApplyIndexedOpacity(cov, idxCov, rows);
                    }
                    else if (cov != null && desc.Index == null && row16A.Opacity != 0)
                        cov = ScaleOverlayAlpha(cov, row16A.Opacity);

                    // Finally, fade by what this mod already claims above it in the stack.
                    cov = material.Suppress(cov, entry, resolved, stackIdx, tw, th);
                    return cov;
                }

                // CovAt with print rows removed: the coverage that laid down a surface. Phase B2 and the AO silhouette read this,
                // since a print must neither bleach skin tone nor cast a shadow.
                private byte[]? PaintCovAt(int tw, int th)
                {
                    var cov = CovAt(tw, th);
                    if (cov == null || !AnyBlendRow(rows)) return cov;
                    byte[]? pIdx = desc.Index != null
                        ? material.LoadIndexMerged(Path.Combine(entry.SidecarRoot, desc.Index), tw, th,
                                          srcBodyType, entry.ModDirectory)
                        : null;
                    return PaintCoverage(cov, pIdx, rows, tw, th, desc.Index != null);
                }
            }
        }
    }
}
