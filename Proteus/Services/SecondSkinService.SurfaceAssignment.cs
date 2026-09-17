using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;
using static Proteus.Services.ContentPieceResolver;
using static Proteus.Services.PenumbraManipulations;

public sealed partial class SecondSkinService
{
    private sealed partial class ShellSetBuild
    {
        private sealed class SurfaceAssignment
        {
            private readonly ShellSetBuild build;
            private Dictionary<ShellSurfaceKey, string> keySurfaceArt = null!;

            public SurfaceAssignment(ShellSetBuild build)
            {
                this.build = build;
            }

            public void Run()
            {
                CollectSurfaceArt();
                AssignLayers();
            }

            private void CollectSurfaceArt()
            {
                // The UV space each surface's asymmetric art declares; the only way a human-part surface can tell mirrored
                // vanilla art from a doubled sheet. First declaration wins.
                keySurfaceArt = new Dictionary<ShellSurfaceKey, string>();
                foreach (var (_, ovArt) in build.gearOverlays)
                {
                    if (ovArt.Descriptor.AsymmetricArt != true) continue;
                    var declared = ovArt.Descriptor.SourceBodyType ?? InferOverlayBodyType(ovArt.Descriptor);
                    if (declared == null) continue;
                    var artKey = SurfaceKeyOf(ovArt);
                    if (!keySurfaceArt.ContainsKey(artKey)) keySurfaceArt[artKey] = declared;
                }
            }

            private void AssignLayers()
            {
                // Group the layers by surface, resolving each non-body surface once; an unresolvable layer is dropped before it
                // consumes a host slot or disk letter. layerSurfaceName keeps each surface as text so nothing re-derives it.
                build.layerSurface = new int[build.gearOverlays.Count];
                build.layerSurfaceName = new string[build.gearOverlays.Count];
                build.resolvedByKey = new Dictionary<ShellSurfaceKey, int> { [build.bodySurface.Key] = 0 };
                build.droppedLayers = new HashSet<int>();
                for (int i = 0; i < build.gearOverlays.Count; i++)
                {
                    var key = SurfaceKeyOf(build.gearOverlays[i].Overlay);
                    build.layerSurfaceName[i] = key.ToString();
                    if (build.resolvedByKey.TryGetValue(key, out var known)) { build.layerSurface[i] = known; continue; }

                    var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, ov) in build.gearOverlays)
                        if (SurfaceKeyOf(ov).Equals(key))
                            foreach (var mp in ov.Descriptor.MaterialGamePaths)
                                if (!string.IsNullOrEmpty(mp)) leaves.Add(Path.GetFileName(mp));

                    var resolved = ResolveHumanSurface(key, leaves);
                    if (resolved == null) { build.resolvedByKey[key] = -1; build.layerSurface[i] = -1; continue; }
                    build.surfaces.Add(resolved);
                    build.resolvedByKey[key] = build.surfaces.Count - 1;
                    build.layerSurface[i] = build.surfaces.Count - 1;
                }
                for (int i = 0; i < build.gearOverlays.Count; i++)
                    if (build.layerSurface[i] < 0) build.droppedLayers.Add(i);
            }

            // ── which surface each layer paints ───────────────────────────────────────────────────
            // From the overlay's declared material. A synthesized MASK shell and an overlay naming no material fall back to the body.
            private ShellSurfaceKey SurfaceKeyOf(ResolvedOverlay ov)
            {
                if (ov.Descriptor.IsMaskShell) return build.bodySurface.Key;
                var keys = ShellSurface.KeysFor(ov.Descriptor.MaterialGamePaths);
                if (keys.Count == 0) return build.bodySurface.Key;
                if (keys.Count > 1)
                    // One overlay painting two surfaces would need one layer per surface, which is not built: take the first and say so.
                    build.service.log.Warning("[Proteus] second skin: overlay \"{0}/{1}\" names {2} surfaces [{3}] — only {4} is "
                              + "cut; split it into one overlay per surface to get the rest",
                        ov.OptionGroup ?? "", ov.Option ?? "", keys.Count, string.Join(", ", keys), keys[0]);
                return keys[0];
            }

            // The .mdl folder a human part's models live under, or null for a kind that names none (Body is cut from
            // equipment, Native is a pack's own geometry). Null rather than a throw: the kind comes from a hand-editable sidecar.
            private static string? PartFolder(ShellSurfaceKind kind) => kind switch
            {
                ShellSurfaceKind.Face => "face",
                // The eyes are cut from a face model; only the surface is separate (see ShellSurfaceKind.Iris).
                ShellSurfaceKind.Iris => "face",
                ShellSurfaceKind.Hair => "hair",
                ShellSurfaceKind.Tail => "tail",
                ShellSurfaceKind.Ear  => "zear",
                _                     => null,
            };

            // Resolve one human-part surface: the model the character is drawing, cut down to meshes bound to the named material.
            // No fallbacks: a human part loads from its literal path, so if the live walk did not report it, it is not worn.
            private ResolvedSurface? ResolveHumanSurface(ShellSurfaceKey key, IReadOnlySet<string> targetLeaves)
            {
                if (PartFolder(key.Kind) is not { } part)
                {
                    build.service.log.Warning("[Proteus] second skin: {0} overlay(s) skipped — {1} names no human part to cut "
                              + "from. Check the Surface in this mod's sidecar", key, key.Kind);
                    return null;
                }

                var folder = $"/obj/{part}/{key.Id}/";
                var candidates = (build.humanPartModels ?? [])
                    .Where(p => p.Contains(folder, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 0)
                {
                    build.service.log.Warning("[Proteus] second skin: {0} overlay(s) skipped — the character is not drawing a "
                              + "model for {1} (live walk saw [{2}])",
                        key, key, string.Join(", ", build.humanPartModels ?? []));
                    return null;
                }

                // A part can draw several models; take the one that DECLARES the targeted material.
                string? pick = null;
                byte[]? pickBytes = null;
                foreach (var cand in candidates)
                {
                    // Ours-this-composite first: a face we rewrote into the doubled layout must not have its UVs doubled again.
                    var bytes = build.pristineHumanModels != null && build.pristineHumanModels.TryGetValue(cand, out var kept)
                        ? kept
                        : build.service.textureLoader.LoadRawFile(build.service.penumbra.ResolvePlayer(cand), cand);
                    if (bytes == null) continue;
                    pickBytes ??= bytes; pick ??= cand;      // first loadable, as the fallback
                    List<string> mats;
                    try { mats = SecondSkinWriter.MaterialNames(bytes); }
                    catch { continue; }
                    if (!mats.Any(m => targetLeaves.Contains(m.TrimStart('/')))) continue;
                    pick = cand; pickBytes = bytes;
                    break;
                }
                if (pick == null || pickBytes == null)
                {
                    build.service.log.Warning("[Proteus] second skin: {0} — none of the {1} drawn model(s) could be read, skipping",
                        key, candidates.Count);
                    return null;
                }

                var keep = SecondSkinWriter.KeepByLeaf(targetLeaves);
                // Decoded once; the log line and the mirror test both use it.
                bool haveGeom = SecondSkinWriter.TryReadLod0Geometry(pickBytes, out var hPos, out var hUv, out var hTri, keep);
                var shape = "(no matching geometry)";
                if (haveGeom && hPos.Length >= 3)
                    shape = $"{hPos.Length / 3}v/{hTri.Length / 3}t";

                var partShapes = LiveModelState(build.enabledBodyShapes, pick, build.service.penumbra.ResolvePlayer(pick));

                // Its own path's race code, with no vote: authored at the character's race, so hosted with no deform.
                var hCut = PathCharCode(pick) ?? build.charCode;
                build.service.log.Information("[Proteus] second skin part {0}: {1} ({2} KB) geometry={3} materials=[{4}] cut in c{5}",
                    key, pick, pickBytes.Length / 1024, shape, string.Join(", ", targetLeaves), hCut);

                if (shape == "(no matching geometry)")
                {
                    build.service.log.Warning("[Proteus] second skin: {0} — no mesh in {1} uses [{2}], so there is nothing to "
                              + "cut. The overlay names a material this model does not carry",
                        key, pick, string.Join(", ", targetLeaves));
                    return null;
                }

                // ── un-mirroring a face ───────────────────────────────────────────────────────────
                // The vanilla face layout is MIRRORED, so a one-sided mark cannot be expressed in it. Art declaring the doubled
                // face sheet gets the shell's two halves sent to that sheet's two halves (the gen2 -> bibo affine).
                // Asked of the geometry: an un-mirrored face must not be split.
                var faceSplit = keySurfaceArt.TryGetValue(key, out var artSpace)
                             && string.Equals(artSpace, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase);
                UVRemapService.UvConversion? faceConv = null;
                if (faceSplit)
                {
                    // The two ways this can decline are reported separately: an unmirrored UV needs nothing, an unreadable model is a fault.
                    if (!haveGeom)
                    {
                        faceSplit = false;
                        build.service.log.Warning("[Proteus] second skin: {0} art declares {1}, but no geometry could be read "
                                  + "from {2} to check it against — leaving it as authored",
                            key, UVRemapService.FaceSplitSpace, pick);
                    }
                    else if (!SurfaceMirror.LooksMirrored(hPos, hUv))
                    {
                        faceSplit = false;
                        build.service.log.Information("[Proteus] second skin: {0} art declares {1}, but this model's UV already "
                                      + "gives each side its own texels — leaving it as authored",
                            key, UVRemapService.FaceSplitSpace);
                    }
                    else
                    {
                        faceConv = build.service.uvRemap.UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace,
                                                       unmirror: true);
                        build.service.log.Information("[Proteus] second skin: asymmetric {0} art on a mirrored {1} — un-mirroring "
                                      + "its vertices into the two halves of the sheet",
                            UVRemapService.FaceSplitSpace, key);
                    }
                }

                return new ResolvedSurface(
                    key,
                    [new SecondSkinWriter.SourceSpec(
                        pickBytes,
                        KeepMaterial: keep,
                        EnabledShapes: Interop.BodyShapeReader.Split(partShapes).Shapes,
                        HiddenAttributes: Interop.BodyShapeReader.Split(partShapes).HiddenAttributes,
                        // Null for ordinary face art; non-null only for a doubled sheet, where the geometry moves.
                        UvConv: faceConv,
                        DelegateKey: "keep:" + string.Join(",", targetLeaves.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                                   + "|uv:" + (faceConv != null ? "face>facesplit:unmirror" : "-"),
                        DropConnectors: false,    // the connector heuristic is body-tuned; it eats real geometry here
                        UnmirrorSides: faceConv != null)],
                    [pick],
                    hCut,
                    // The art's own space for a doubled sheet, so RemapPathCore leaves it alone; otherwise none (no face transfer maps).
                    faceSplit ? UVRemapService.FaceSplitSpace : null);
            }
        }
    }
}
