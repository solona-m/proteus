using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Penumbra.Api.Enums;

namespace Proteus.Services;
using static Proteus.Services.IslandBlur;
using static Proteus.Services.OverlayBlend;

public partial class CompositorService
{
    private sealed partial class CompositeRun
    {
        private sealed partial class MaterialComposite
        {
            private sealed class OcclusionPass
            {
                private readonly MaterialComposite material;
                private float aoSoftness;
                private List<string> aoModsList = null!;
                private bool bodyUvMaterial;
                private Dictionary<string, bool> aoQualified = null!;
                private byte[]? insidePlane;
                private int[]? islandLabels;
                private int[]? islandOwner;
                private int islandCount;
                private IslandBlurCache islandBlurCache = null!;
                private HalfResIslandPlanes halfPlanes = null!;
                private List<UvSeamMapService.SeamModel>? bodyMdls;
                private bool aoLoadedNormal;
                private bool aoIndentedNormal;
                private long tAo;

                public OcclusionPass(MaterialComposite material)
                {
                    this.material = material;
                }

                public void Run()
                {
                    // ── Ambient occlusion: soft contact-shadow on skin around strap / garment edges ──
                    // Each mod spreads its silhouette (its mask, else its garment coverage) into the surrounding skin and darkens the
                    // diffuse just outside the edge. Multiplies into the shared baseD, so overlapping mods each add a shadow.
                    float aoStrength = material.run.compositor.config.AmbientOcclusionStrength;
                    float aoNormal   = material.run.compositor.config.AmbientOcclusionNormalDepth;
                    if (aoStrength > 0f || aoNormal > 0f)
                    {
                        QualifyMods(aoStrength, aoNormal);
                        LabelIslands();
                        LoadSeams();
                        CastShadows(aoStrength, aoNormal);
                        Finish();
                    }
                }

                private void QualifyMods(float aoStrength, float aoNormal)
                {
                    aoSoftness = material.run.compositor.config.AmbientOcclusionSoftness;
                    // Every mod contributing a mask, gear garment or skin-painted garment to this body, in composite order.
                    aoModsList = material.pairs.Select(p => p.Entry.ModDirectory)
                        .Concat(material.run.gearOverlays.Select(g => g.Entry.ModDirectory))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    // What each pack declares about AO; absent means no (ProteusMetadata.AmbientOcclusion).
                    var aoDeclaredBy = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in material.pairs.Select(p => p.Entry).Concat(material.run.gearOverlays.Select(g => g.Entry)))
                        aoDeclaredBy.TryAdd(e.ModDirectory, e.Metadata?.AmbientOcclusion);

                    // Gear shells and Masks coverage are in body UV, so they only describe a garment on a body-UV material.
                    bodyUvMaterial = IsBodyUvMaterial(material.mtrlGamePath);

                    // Qualify every mod once. Before the island precompute below, which needs baseD loaded off the back of this.
                    aoQualified = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var modDir in aoModsList)
                    {
                        // Opt-in: the user's explicit choice, else what the pack declared, else off.
                        aoDeclaredBy.TryGetValue(modDir, out bool? declared);
                        if (!material.run.compositor.config.AmbientOcclusionEnabledFor(modDir, declared)) continue;
                        var (m, g, s) = AoSources(modDir);
                        if (m || g || s) aoQualified[modDir] = m;
                    }

                    material.run.compositor.log.Debug("[Proteus] AO on {0}: strength={1:F3} normal={2:F4} softness={3:F4} — {4}/{5} mod(s) qualified [{6}]",
                              material.mtrlGamePath, aoStrength, aoNormal, aoSoftness,
                              aoQualified.Count, aoModsList.Count, string.Join(", ", aoQualified.Keys));

                    // The base diffuse is needed for the shadow, the island precompute and the "covered above" dimensions, but only load
                    // it when some mod qualifies.
                    if (aoQualified.Count > 0) material.EnsureBaseDiffuse();
                }

                private void LabelIslands()
                {
                    // UV islands at the diffuse resolution: a 0/255 inside plane plus the labelling BlurCoverageWithinIslands needs.
                    // Null with no transfer map (gen2), in which case silhouettes are blurred plainly.
                    insidePlane = null;
                    islandLabels = null;
                    islandOwner = null;
                    islandCount = 0;
                    // Reused by every mod's AO pass on this material; per material because materials composite in parallel.
                    islandBlurCache = new IslandBlurCache();
                    // Same lifetime and reason as islandBlurCache — see HalfResIslandPlanes.
                    halfPlanes = new HalfResIslandPlanes();
                    var tIslands = PhaseCounter.Begin();
                    if (material.dstBodyType != null && material.baseD is { Length: > 0 })
                    {
                        var isl = material.run.compositor.uvRemap.IslandMask(material.dstBodyType, out int islW, out int islH);
                        if (isl != null && islW > 0 && islH > 0)
                        {
                            insidePlane = new byte[material.wD * material.hD];
                            var ip = insidePlane;
                            Parallel.For(0, material.hD, y =>
                            {
                                int srow = (int)((long)y * islH / material.hD) * islW;
                                int row = y * material.wD;
                                for (int x = 0; x < material.wD; x++)
                                {
                                    int si = srow + (int)((long)x * islW / material.wD);
                                    ip[row + x] = (byte)(si < isl.Length && isl[si] ? 255 : 0);
                                }
                            });
                            (islandLabels, islandOwner, islandCount) = material.run.compositor.IslandLabelsFor(material.dstBodyType, insidePlane, material.wD, material.hD);
                        }
                    }
                    material.run.compositor.blendIslandStats.Stop(tIslands);
                }

                private void LoadSeams()
                {
                    // The mesh that owns this UV layout: its seams are the only statement of which island edges continue into which.
                    // Identity only here, no file opened; the seam map is cached by it.
                    bodyMdls = null;
                    if (islandLabels != null)
                    {
                        if (BodyModelPathsFor(material.mtrlGamePath) is { } bodyMdlPaths)
                        {
                            bodyMdls = new List<UvSeamMapService.SeamModel>(bodyMdlPaths.Length);
                            foreach (var mp in bodyMdlPaths)
                            {
                                var gamePath = mp;
                                var disk = material.run.compositor.penumbra.ResolvePlayer(gamePath);
                                // A modded part is a real file (size+mtime identity); a vanilla one can only change with a patch (game path identity).
                                string id = gamePath;
                                try
                                {
                                    if (!string.IsNullOrEmpty(disk))
                                    {
                                        var fi = new FileInfo(disk);
                                        if (fi.Exists) id = $"{disk}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                                    }
                                }
                                catch { /* unreadable — fall back to the game path */ }
                                bodyMdls.Add(new UvSeamMapService.SeamModel(
                                    id, () => material.run.compositor.textureLoader.LoadRawFile(disk, gamePath)));
                            }
                            // Which files the seam map will be built from; on a cache hit nothing else is logged.
                            material.run.compositor.log.Debug("[Proteus] seam map: {0} body part(s) for {1} — {2}",
                                      bodyMdls.Count, material.mtrlGamePath,
                                      string.Join(", ", bodyMdls.Select(m =>
                                          m.Id.Contains('|') ? Path.GetFileName(m.Id[..m.Id.IndexOf('|')]) : "(game)")));
                        }
                        else
                        {
                            material.run.compositor.log.Debug("[Proteus] seam map: {0} is not a human body material — no seam data",
                                      material.mtrlGamePath);
                        }
                    }
                }

                private void CastShadows(float aoStrength, float aoNormal)
                {
                    // Union of opaque coverage of every garment above the one being processed, so a lower garment's shadow/indent is
                    // suppressed under it. Processed top→bottom, using full garment coverage (GarmentSilhouette).
                    byte[]? coveredAbove = material.baseD is { Length: > 0 } ? new byte[material.wD * material.hD] : null;

                    // Tracked across the whole sweep: load the 4K skin normal at most once and hand it back at most once.
                    aoLoadedNormal = false;
                    aoIndentedNormal = false;

                    tAo = PhaseCounter.Begin();
                    for (int mi = aoModsList.Count - 1; mi >= 0; mi--)
                    {
                        var modDir = aoModsList[mi];
                        if (!aoQualified.TryGetValue(modDir, out bool hasMasks)) continue;
                        material.lastSrcBodyTypeByMod.TryGetValue(modDir, out var aoSrcBodyType);

                        // This garment's coverage at diffuse res, carved by its own masks so a cutout does not occlude lower garments:
                        // fabric·(mask keep) + trim.
                        byte[]? garmentCov = material.baseD is { Length: > 0 } ? material.GarmentSilhouette(modDir, material.wD, material.hD) : null;
                        if (hasMasks && material.baseD is { Length: > 0 } && material.CombinedMaskAt(modDir, material.wD, material.hD, aoSrcBodyType) is { } gm)
                        {
                            var mW = gm.W; var mT = gm.T;
                            if (garmentCov == null) garmentCov = (byte[])mT.Clone();   // mask-only: its trim is the coverage
                            else
                            {
                                var gc = garmentCov;
                                ParallelPixels(0, material.wD * material.hD, 1, (from, to) =>
                                {
                                    for (int p = from; p < to; p++)
                                    {
                                        int v = gc[p] * mW[p] / 255 + mT[p];
                                        gc[p] = (byte)(v > 255 ? 255 : v);
                                    }
                                });
                            }
                        }

                        // The AO silhouette (mask, else garment coverage) at diffuse res, reused for the indent when sizes match. radiusD is
                        // the radius the blur was actually built with.
                        byte[]? strapD = null, blurredD = null;
                        int radiusD = 0;

                        // ── Diffuse: soft contact shadow on the skin just outside the edge ──
                        if (aoStrength > 0f && material.baseD is { Length: > 0 })
                        {
                            strapD = hasMasks ? (material.CombinedMaskAt(modDir, material.wD, material.hD, aoSrcBodyType)?.T ?? garmentCov) : garmentCov;
                            if (strapD != null)
                            {
                                radiusD = Math.Max(1, (int)(material.wD * aoSoftness));
                                // Keep every blur window on one UV island, so the halo comes from the mask, not padding or the island across the
                                // gutter. The silhouette itself stays as authored (see BlurCoverageWithinIslands).
                                blurredD = material.run.compositor.BlurSilhouette(strapD, material.wD, material.hD, radiusD, material.dstBodyType, insidePlane,
                                    islandLabels, islandOwner, islandCount, bodyMdls, islandBlurCache, halfPlanes);
                                material.SnapshotBaseDiffuse();
                                ApplyAmbientOcclusion(material.baseD, strapD, blurredD, material.wD, material.hD, aoStrength, coveredAbove,
                                    material.run.compositor.BustStandoff(modDir, bodyMdls, strapD, material.wD, material.hD, radiusD));
                                // AO is a real edit to the skin diffuse; a gear-only mod legitimately owns the buffer through it.
                                material.diffuseBlended = true;
                            }
                        }

                        // ── Normal: indent the skin at the edge so the strap looks pressed in ──
                        if (aoNormal > 0f && material.texPaths.Normal != null)
                        {
                            // The load reports the normal's dimensions, so it cannot be deferred; note that we loaded it (aoLoadedNormal) and
                            // hand it back after the sweep if nothing indented it, or an untouched normal is republished.
                            if (material.baseN == null)
                            {
                                aoLoadedNormal = true;
                                material.baseN = material.LoadBaseNormalHere(material.texPaths.Normal, ref material.wN, ref material.hN);
                            }
                            if (material.baseN.Length > 0)
                            {
                                byte[]? strapN, blurredN;
                                // The radius the indent's gradient is normalised against (it sets the coverage ramp width).
                                int radiusN;
                                if (strapD != null && blurredD != null && material.wN == material.wD && material.hN == material.hD)
                                {
                                    strapN = strapD; blurredN = blurredD;   // reuse the diffuse-resolution buffers
                                    radiusN = radiusD;                      // the radius that blur was built with
                                }
                                else
                                {
                                    strapN = hasMasks ? (material.CombinedMaskAt(modDir, material.wN, material.hN, aoSrcBodyType)?.T ?? material.GarmentSilhouette(modDir, material.wN, material.hN))
                                                      : material.GarmentSilhouette(modDir, material.wN, material.hN);
                                    radiusN = Math.Max(1, (int)(material.wN * aoSoftness));
                                    // Island-restricted only when the normal shares the diffuse's size (planes are built at wD/hD).
                                    bool sameSize = material.wN == material.wD && material.hN == material.hD;
                                    blurredN = strapN == null ? null
                                        : material.run.compositor.BlurSilhouette(strapN, material.wN, material.hN, radiusN, material.dstBodyType,
                                            sameSize ? insidePlane : null, sameSize ? islandLabels : null,
                                            sameSize ? islandOwner : null, islandCount, bodyMdls, islandBlurCache, halfPlanes);
                                }
                                // Gate by covered-above only when the normal shares the diffuse res; otherwise ungated.
                                if (strapN != null && blurredN != null)
                                {
                                    ApplyNormalIndent(material.baseN, blurredN, strapN, material.wN, material.hN, aoNormal,
                                        material.wN == material.wD && material.hN == material.hD ? coveredAbove : null, radiusN,
                                        material.wN == material.wD && material.hN == material.hD ? insidePlane : null,
                                        material.run.compositor.BustStandoff(modDir, bodyMdls, strapN, material.wN, material.hN, radiusN));
                                    aoIndentedNormal = true;
                                }
                            }
                        }

                        // Add this garment's coverage so LOWER garments' effects are suppressed where it covers.
                        if (garmentCov != null && coveredAbove != null)
                        {
                            var acc = coveredAbove; var src = garmentCov;
                            ParallelPixels(0, material.wD * material.hD, 1, (from, to) =>
                            { for (int p = from; p < to; p++) if (src[p] > acc[p]) acc[p] = src[p]; });
                        }
                    }
                }

                private void Finish()
                {
                    // Includes seam-map and content-tag time charged separately below; the phases line subtracts those.
                    material.run.compositor.blendAoStats.Stop(tAo);

                    // Loaded here and not indented: hand it back so the writer doesn't republish it. A failed load's Array.Empty stays as the memo.
                    if (aoLoadedNormal && !aoIndentedNormal && material.baseN is { Length: > 0 }) material.baseN = null;
                }

                // What a mod casts onto this material: one definition for both the load decision and the loop. Every source is scoped
                // to this material: masks and gear by UV space, skin by the material it was painted for.
                private (bool Masks, bool Gear, bool Skin) AoSources(string modDir) => (
                    bodyUvMaterial && material.run.maskPathsByMod.ContainsKey(modDir),
                    bodyUvMaterial && material.run.gearOverlays.Any(g => string.Equals(g.Entry.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)),
                    // Skin-layer overlays with a diffuse also cast AO/indent; flat full coverage is self-gating. AO is opt-in per mod.
                    material.run.allOverlays.Any(o => string.Equals(o.Entry.ModDirectory, modDir, StringComparison.OrdinalIgnoreCase)
                        && o.Overlay.Descriptor.Layer == OverlayLayer.Skin
                        && o.Overlay.Descriptor.Diffuse != null
                        // A print would otherwise drag its mod into AO. Cheap preset test first.
                        && !(AnyBlendRow(o.Overlay.ColorTableRows)
                             && AllRowsPrint(BuildRowDict(o.Overlay.ColorTableRows),
                                             o.Overlay.Descriptor.Index != null))
                        && o.Overlay.Descriptor.MaterialGamePaths.Contains(material.mtrlGamePath, StringComparer.OrdinalIgnoreCase)));
            }
        }
    }
}
