using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Proteus.Services;
using static Proteus.Services.PenumbraManipulations;
using static Proteus.Services.ShellColorRows;

public sealed partial class SecondSkinService
{
    private sealed partial class ShellSetBuild
    {
        private sealed class ShellTextureBake
        {
            private readonly ShellSetBuild build;
            private Dictionary<int, byte[]?> maskAlphaByLayer = null!;
            private Dictionary<int, (char Disk, string Prefix, Dictionary<string, string> Redirects, bool Changed, List<string>? Paths)> specTextures = null!;

            public ShellTextureBake(ShellSetBuild build)
            {
                this.build = build;
            }

            public void Run()
            {
                BakeInParallel();
                AssignSlots();
            }

            private void BakeInParallel()
            {
                // ── Shell textures, built in parallel ahead of the slot loop ──────────────────────────────────────
                // No layer's pixels depend on another's, but the disk letter advances only on success: each layer is built under
                // the letter it will get if all before it succeed, and the slot loop uses the result only when the letters agree
                // (else it rebuilds serially). A shell whose normal is held for a reinforced toe is left to the serial loop.
                // Penumbra calls and per-layer logging run serially first; only the pixel work fans out.
                maskAlphaByLayer = new Dictionary<int, byte[]?>();
                specTextures = new Dictionary<int, (char Disk, string Prefix, Dictionary<string, string> Redirects,
                                                        bool Changed, List<string>? Paths)>();
                {
                    var specJobs = new List<(int I, char Disk, string Prefix, byte[]? Alpha, byte[] Template, bool MergeMasks,
                                             List<byte[]>? Siblings,
                                             List<(string MaskPath, string? NormalPath, string? IndexPath)>? Masks,
                                             string? Src, string? Dst)>();
                    var masksByMod = new Dictionary<string, List<(string MaskPath, string? NormalPath, string? IndexPath)>>(
                        StringComparer.OrdinalIgnoreCase);
                    int specLetter = build.diskLetter;
                    foreach (var (i, hIdx) in build.work)
                    {
                        var (entry, ov) = build.gearOverlays[i];
                        var d = ov.Descriptor;
                        var (srcType, dstType) = build.UvFor(i, d);
                        byte[]? alpha;
                        if (d.IsMaskShell)
                        {
                            var tMask = PhaseCounter.Begin();
                            alpha = maskAlphaByLayer[i] = build.service.BuildMaskCoverage(entry, srcType, dstType, build.texSize, build.texSize);
                            build.service.statsCoverage.Stop(tMask);
                        }
                        else alpha = build.alphaByLayer[i];
                        if (alpha == null) continue;   // the loop drops it without spending a letter

                        // In the slot loop's own order, agreeing with it about whether each drop spends a letter.
                        var host = build.hosts[hIdx];
                        var layerSurf = build.surfaces[build.layerSurface[i] >= 0 ? build.layerSurface[i] : 0];
                        var template = build.LoadTemplate(layerSurf, d.ShaderPackage, report: false);
                        if (template == null) continue;

                        char disk = DiskId(specLetter++);
                        if (!d.IsMaskShell && d.ToeCapDensity > 0) continue;   // reinforced toe: serial, see above

                        bool mergeMasks = d.IsMaskShell || !(build.maskShellMods?.Contains(entry.ModDirectory) ?? false);
                        List<(string MaskPath, string? NormalPath, string? IndexPath)>? masks = null;
                        if (mergeMasks && !masksByMod.TryGetValue(entry.ModDirectory, out masks))
                            masksByMod[entry.ModDirectory] = masks = build.service.discovery.ResolveActiveMaskAssets(entry);

                        var siblings = d.IsMaskShell
                            ? null
                            : build.reliefContribs.Where(c => c.LayerIdx != i
                                    && build.layerSurface[c.LayerIdx] == build.layerSurface[i]
                                    && string.Equals(c.ModDir, entry.ModDirectory, StringComparison.OrdinalIgnoreCase))
                                .Select(c => c.Normal).ToList();
                        specJobs.Add((i, disk, $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/texture/ss_{disk}_",
                                      alpha, template, mergeMasks, siblings, masks, srcType, dstType));
                    }

                    if (specJobs.Count > 1)
                    {
                        var tSpec = PhaseCounter.Begin();
                        var results = new (Dictionary<string, string> Redirects, bool Changed, List<string>? Paths)[specJobs.Count];
                        // Bounded: each layer holds several 4K buffers, and the pixel kernels are already parallel.
                        Parallel.For(0, specJobs.Count,
                            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 4) },
                            j =>
                            {
                                var job = specJobs[j];
                                var (entry, ov) = build.gearOverlays[job.I];
                                var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                bool changed = false;
                                List<string>? paths = null;
                                try
                                {
                                    paths = build.service.WriteTextures(entry, ov.Descriptor, ov.Descriptor.ShaderPackage, job.Prefix,
                                        build.texturesDir, local, job.Disk, job.Alpha, job.Src, job.Dst, ov.ColorTableRows,
                                        build.effectsFolder, build.texSize, ref changed, job.MergeMasks, job.Siblings,
                                        GearMaterialWriter.TextureNames(job.Template), deferNormal: null,
                                        maskAssets: job.Masks);
                                }
                                catch (Exception ex)
                                {
                                    // Not fatal: no result means the slot loop builds this layer itself.
                                    build.service.log.Debug("[Proteus] second skin: parallel texture build for layer {0} failed ({1}) — "
                                            + "the slot loop will build it", job.I, ex.Message);
                                    return;
                                }
                                results[j] = (local, changed, paths);
                            });
                        for (int j = 0; j < specJobs.Count; j++)
                            if (results[j].Redirects != null)
                                specTextures[specJobs[j].I] = (specJobs[j].Disk, specJobs[j].Prefix, results[j].Redirects,
                                                               results[j].Changed, results[j].Paths);
                        build.service.statsLayerTextures.Stop(tSpec);
                    }
                }

                build.inHost = new int[build.hosts.Count];
            }

            private void AssignSlots()
            {
                foreach (var (i, hIdx) in build.work)
                {
                    var (entry, ov) = build.gearOverlays[i];
                    bool isMaskShell = ov.Descriptor.IsMaskShell;
                    var host = build.hosts[hIdx];

                    string shader = ov.Descriptor.ShaderPackage;
                    char matLetter = (char)('a' + host.BaseMatCount + build.inHost[hIdx]);   // in-model material index (per-host, <= 'j')
                    char diskChar  = DiskId(build.diskLetter);                               // globally-unique disk id (base-36, 0-9a-z)
                    // Materials live inside the host's model, so name them with the code that model loads under: the host's resolved
                    // path, or plan[hIdx].PublishCode for a carrier (not cutCode, which differs on the native-publish path).
                    var hostCode = host.ModelPath != null
                        ? PathCharCode(host.ModelPath) ?? build.plan[hIdx].PublishCode
                        : build.plan[hIdx].PublishCode;
                    string matName = $"mt_c{hostCode}{host.Prefix}{host.SetId:D4}_{host.Slot}_{matLetter}.mtrl";
                    string matVariant = build.VariantFolderFor(host);
                    string matGamePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/material/{matVariant}/{matName}";
                    string texPrefix   = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/texture/ss_{diskChar}_";

                    // Which UV space the art is painted in and must end up in; the gear layer remaps explicitly. Human parts: see UvFor.
                    var layerSurf = build.surfaces[build.layerSurface[i] >= 0 ? build.layerSurface[i] : 0];
                    var (srcType, dstType) = build.UvFor(i, ov.Descriptor);
                    build.service.log.Information("[Proteus] gear layer mat={0}/{10}/disk={1} -> host {2}{3:D4}/{4}: shader={5} UV {6}->{7}{8}{9} [{11}]",
                        matLetter, diskChar, host.Prefix, host.SetId, host.Slot, shader, srcType ?? "(unknown)", dstType ?? "(native)",
                        srcType != null && dstType != null && !string.Equals(srcType, dstType, StringComparison.OrdinalIgnoreCase) ? " [REMAP]" : "",
                        isMaskShell ? " [MASK SHELL]" : "", matVariant, layerSurf.Key);

                    // The mask shell's coverage IS the mask; other shells' coverage is the overlay's art shaped by masks.
                    bool mergeMasks = isMaskShell || !(build.maskShellMods?.Contains(entry.ModDirectory) ?? false);
                    var alpha = isMaskShell
                        ? maskAlphaByLayer.TryGetValue(i, out var builtMask) ? builtMask   // the parallel pre-pass built it
                            : build.service.BuildMaskCoverage(entry, srcType, dstType, build.texSize, build.texSize)
                        : build.alphaByLayer[i];   // computed once in the sibling-relief pre-pass above

                    // Error-drops don't consume a host slot; inHost/diskLetter only advance on success. Null coverage (art failed or
                    // empty; BuildAlpha logged why) drops the shell rather than rendering it fully opaque over the whole body.
                    if (alpha == null) continue;
                    var coverage = Downsample(alpha, build.texSize, build.texSize, CoverageSize);

                    // Same-mod, same-SURFACE siblings' relief compounds into this fabric shell (never a mask shell), excluding itself.
                    // A sibling's normal is at its own UV coordinates, so relief must stay inside one atlas.
                    var siblingReliefs = isMaskShell
                        ? null
                        : build.reliefContribs.Where(c => c.LayerIdx != i
                                && build.layerSurface[c.LayerIdx] == build.layerSurface[i]
                                && string.Equals(c.ModDir, entry.ModDirectory, StringComparison.OrdinalIgnoreCase))
                            .Select(c => c.Normal).ToList();

                    // Loaded BEFORE the textures: a slot the overlay doesn't supply inherits the template's path (see WriteTextures).
                    // Falls back to the Midlander body for anything that ships no matching material.
                    var template = build.LoadTemplate(layerSurf, shader, report: true);
                    if (template == null) { build.service.log.Error("[Proteus] second skin: missing template material for {0}", shader); continue; }

                    // A shell follows every contour, so hosiery sleeves each toe unless the toe area is marked; the writer then
                    // rebuilds it as one rounded cap. BODY surfaces only (the map is body UV). Resolved before the textures, because
                    // a cap decides whether the normal is held back for the reinforced toe.
                    var toeCap = layerSurf.Key.IsBody
                        ? build.service.ToeCapFor(ov.Descriptor, entry, srcType, dstType, build.sharedToeCap, alpha, build.texSize)
                        : null;

                    // A reinforced toe needs a cap, and not a mask shell (that would apply it twice).
                    DeferredShellNormal? deferredNormal =
                        toeCap != null && !isMaskShell && ov.Descriptor.ToeCapDensity > 0 ? new DeferredShellNormal() : null;

                    List<string>? texPaths;
                    if (deferredNormal == null && specTextures.TryGetValue(i, out var spec)
                        && spec.Disk == diskChar && string.Equals(spec.Prefix, texPrefix, StringComparison.Ordinal))
                    {
                        // Built by the parallel pre-pass under the letter this layer did get.
                        foreach (var (gp, rel) in spec.Redirects) build.redirects[gp] = rel;
                        build.shellChanged |= spec.Changed;
                        texPaths = spec.Paths;
                    }
                    else
                    {
                        var tTex = PhaseCounter.Begin();
                        texPaths = build.service.WriteTextures(entry, ov.Descriptor, shader, texPrefix, build.texturesDir, build.redirects, diskChar,
                            alpha, srcType, dstType, ov.ColorTableRows, build.effectsFolder, build.texSize, ref build.shellChanged, mergeMasks,
                            siblingReliefs, GearMaterialWriter.TextureNames(template), deferredNormal);
                        build.service.statsLayerTextures.Stop(tTex);
                    }
                    // Registered even if the textures then failed: the normal's redirect is already published. The "/" is how the model
                    // stores material names.
                    if (deferredNormal?.Norm != null)
                        build.pendingNormals.Add(("/" + matName, ov.Descriptor.ToeCapDensity, deferredNormal));
                    if (texPaths == null) continue;

                    var scroll = new ScrollSettings(
                        ov.Descriptor.ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                        ov.Descriptor.ScrollSpeedY ?? ScrollSettings.Default.SpeedY,
                        ov.Descriptor.ScrollTilingX ?? ScrollSettings.Default.TilingX,
                        ov.Descriptor.ScrollTilingY ?? ScrollSettings.Default.TilingY);

                    byte[] mtrl;
                    // A mask shell's colour lives in the colorset over a WHITE base, so its colorset diffuse is linearised; fabric
                    // shells carry colour in their texture with a white colorset.
                    // NEUTRAL whenever the author set no rows: the template's own table (vanilla e0041) is unrelated to the look and
                    // darkens the art. Only the baseline, not the mask shell's half-pair mirroring (see BuildRows).
                    // A spanning shell needs its backfaces drawn (the span's inside is visible), for every shell of the outermost MOD
                    // only (see topSpanningModOnHost).
                    bool spanning = build.topSpanningModOnHost.TryGetValue(hIdx, out var topMod)
                                 && string.Equals(topMod, entry.ModDirectory, StringComparison.OrdinalIgnoreCase);
                    var tMat = PhaseCounter.Begin();
                    try { mtrl = GearMaterialWriter.Build(template, texPaths, BuildRows(ov.ColorTableRows, isMaskShell: isMaskShell, neutralWhenEmpty: true), scroll, build.service.config.GearCutoutAlpha, linearizeDiffuse: isMaskShell, showBackfaces: spanning); }
                    catch (Exception ex) { build.service.log.Error(ex, "[Proteus] second skin: material build failed for {0}", shader); continue; }

                    var matDisk = Path.Combine(build.materialsDir, $"ss_{diskChar}.mtrl");
                    build.shellChanged |= WriteIfChanged(matDisk, mtrl);
                    build.service.statsLayerMaterial.Stop(tMat);
                    build.redirects[matGamePath] = Rel(build.outputRoot, matDisk);
                    var shellKey = (entry.ModDirectory, ov.OptionGroup, ov.Option);
                    if (!build.shellMaterials.TryGetValue(shellKey, out var shellList))
                        build.shellMaterials[shellKey] = shellList = new List<string>();
                    shellList.Add($"ss_{diskChar}.mtrl");

                    if (BuildLightProfile(ov.ColorTableRows, isMaskShell, layerSurf.Key.Kind,
                            isScroll: string.Equals(shader, RenderModeInference.GlowShader,
                                                    StringComparison.OrdinalIgnoreCase)) is { } lightProfile)
                        build.shellLight[$"ss_{diskChar}.mtrl"] = lightProfile;

                    build.perHostLayers[hIdx].Add(new SecondSkinLayer
                    {
                        MaterialName = "/" + matName,   // the model stores material names with a leading slash
                        Coverage = coverage,
                        CoverageWidth = coverage == null ? 0 : CoverageSize,
                        CoverageHeight = coverage == null ? 0 : CoverageSize,
                        PushScale = build.PushFor(i),
                        ToeCap = toeCap,
                        ToeCapWidth = toeCap == null ? 0 : ToeCapSize,
                        ToeCapHeight = toeCap == null ? 0 : ToeCapSize,
                        ToeCapStrength = Math.Clamp(ov.Descriptor.ToeCapStrength ?? 1f, 0f, 1f),
                        // Only for a shell whose normal was held for a reinforced toe, at the sheet's size so it matches texel for texel.
                        ToeReinforceSize = deferredNormal?.Norm != null ? build.texSize : 0,
                        // BODY SURFACES ONLY: a face or tail has no bust bones.
                        BustBridgeStrength = layerSurf.Key.IsBody
                                          && build.bridgeByMod.TryGetValue(entry.ModDirectory, out var bridgeS)
                            ? bridgeS
                            : 0f,
                        NippleSmoothStrength = layerSurf.Key.IsBody
                                            && build.smoothByMod.TryGetValue(entry.ModDirectory, out var smoothS)
                            ? smoothS
                            : 0f,
                        CleftBridgeStrength = layerSurf.Key.IsBody
                                           && build.cleftByMod.TryGetValue(entry.ModDirectory, out var cleftS)
                            ? cleftS
                            : 0f,
                        FoldSmoothStrength = layerSurf.Key.IsBody
                                          && build.foldByMod.TryGetValue(entry.ModDirectory, out var foldS)
                            ? foldS
                            : 0f,
                    });
                    build.inHost[hIdx]++; build.diskLetter++;       // slot consumed
                    if (isMaskShell) build.maskLayers++; else build.clothLayers++;
                }
            }
        }
    }
}
