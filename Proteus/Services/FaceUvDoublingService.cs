using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>One face material this composite renders in the doubled layout, and the model rewritten to sample it.</summary>
/// <param name="ModelRelPath">Where the rewritten model was published, relative to the managed mod.</param>
public sealed record FaceDoubling(string MaterialGamePath, string ModelGamePath, string ModelRelPath);

/// <summary>Which face materials render doubled this composite, and the model redirects that make it true.</summary>
public sealed record FaceUvPlan(
    IReadOnlySet<string> Materials,
    IReadOnlyDictionary<string, string> ModelRedirects,
    IReadOnlyList<FaceDoubling> Entries,
    bool AnyModelChanged,
    /// <summary>
    /// The pristine bytes of every model rewritten this composite, by game path, so <see cref="SecondSkinService"/>
    /// never cuts a shell from our rewrite (which would apply the doubling affine twice).
    /// </summary>
    IReadOnlyDictionary<string, byte[]> UpstreamModels)
{
    public static readonly FaceUvPlan Empty = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        [], false,
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));

    public bool Any => Materials.Count > 0;
}

/// <summary>
/// Renders asymmetric face art by rewriting the character's own face model into the doubled sheet layout,
/// instead of a shell (a shell has no shape keys, so it cannot blink or emote).
/// The model rewrite runs first and its result is the single input <see cref="CompositorService"/> reads to
/// publish the doubled textures; the two must agree.
/// </summary>
public sealed class FaceUvDoublingService
{
    private readonly IPluginLog log;
    private readonly TextureLoader textureLoader;
    private readonly UVRemapService uvRemap;

    /// <summary>
    /// The pristine bytes of each face model we have rewritten, keyed by game path — never our own output,
    /// which would apply the affine twice once our redirect is live.
    /// </summary>
    /// <remarks>
    /// Concurrent because composites overlap and nothing here is locked; every key names the inputs its value
    /// depends on, so sharing across runs is safe.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte[]> _upstreamFaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The last rewrite of each (model, material), against the exact upstream bytes it was made from; matched
    /// by reference, so a changed face mod re-resolves to a fresh array and misses.
    /// </summary>
    /// <inheritdoc cref="_upstreamFaces" path="/remarks"/>
    private readonly ConcurrentDictionary<(string Path, string Leaf), (byte[] Src, byte[] Out)> _rewrites = new();

    public FaceUvDoublingService(IPluginLog log, TextureLoader textureLoader, UVRemapService uvRemap)
    {
        this.log = log;
        this.textureLoader = textureLoader;
        this.uvRemap = uvRemap;
    }

    /// <summary>
    /// Whether this overlay's face art can be rendered by moving the face's own UVs, rather than by a shell.
    /// Anything needing <c>character.shpk</c> is excluded, and the art must declare the doubled layout.
    /// </summary>
    public static bool IsCandidate(OverlayEntry entry, ResolvedOverlay overlay, bool aboveGear)
    {
        var d = overlay.Descriptor;
        if (d.AsymmetricArt != true || d.IsMaskShell) return false;
        if (!string.Equals(d.SourceBodyType, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase))
            return false;
        if (d.MaterialGamePaths.Count == 0) return false;
        // Every material, not any: a mixed overlay would otherwise paint through a shell and the skin at once.
        if (!d.MaterialGamePaths.All(IsFaceMaterial)) return false;
        if (d.Layer == OverlayLayer.Gear || d.Scroll != null || aboveGear) return false;
        if (RenderModeInference.HasCloth(overlay.ColorTableRows ?? [])) return false;
        if (RenderModeInference.WantsGeometry(entry.Metadata)) return false;
        return true;
    }

    /// <summary>A face material — the eyes' own <c>_iri</c> material inside the same folder is a different
    /// surface and is not one.</summary>
    public static bool IsFaceMaterial(string? mtrlGamePath)
        => ShellSurface.KeyFor(mtrlGamePath) is { Kind: ShellSurfaceKind.Face };

    /// <summary>
    /// Work out which face materials double this composite, rewrite and publish their models, and hand back
    /// the redirects. Returns <see cref="FaceUvPlan.Empty"/> whenever anything is in doubt; the caller then
    /// folds the doubled sheet.
    /// </summary>
    /// <param name="humanPartModels">The face/hair/tail models the character is actually drawing.</param>
    /// <param name="resolvePlayer">Penumbra's live answer for a game path — our own output included.</param>
    public FaceUvPlan Plan(
        IReadOnlyList<(OverlayEntry Entry, IReadOnlyList<(ResolvedOverlay Overlay, bool AboveGear)> Overlays)> resolved,
        IReadOnlyList<string>? humanPartModels,
        Func<string, string?> resolvePlayer,
        Func<string?, bool> isOwnOutput,
        string outputRoot,
        bool enabled)
    {
        if (!enabled) return FaceUvPlan.Empty;

        var wanted = new Dictionary<string, ShellSurfaceKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, overlays) in resolved)
            foreach (var (overlay, aboveGear) in overlays)
            {
                if (!IsCandidate(entry, overlay, aboveGear)) continue;
                foreach (var m in overlay.Descriptor.MaterialGamePaths)
                    if (ShellSurface.KeyFor(m) is { Kind: ShellSurfaceKind.Face } k)
                        wanted[m] = k;
            }
        if (wanted.Count == 0) return FaceUvPlan.Empty;

        if (humanPartModels == null || humanPartModels.Count == 0)
        {
            log.Warning("[Proteus] face uv: {0} material(s) want the doubled face layout, but the character "
                      + "is drawing no face model to rewrite — folding the sheet instead",
                wanted.Count);
            return FaceUvPlan.Empty;
        }

        var modelsDir = Path.Combine(outputRoot, "models");
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pristine = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<FaceDoubling>();
        bool changedAny = false;

        // One rewrite per model, with every one of its face materials in the keep filter: a rewrite converts
        // only the meshes its filter names.
        var byModel = new Dictionary<string, (List<string> Materials, HashSet<string> Leaves)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (mtrl, key) in wanted)
        {
            var leaf = mtrl[(mtrl.LastIndexOf('/') + 1)..];
            var justThis = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { leaf };
            if (PickModel(humanPartModels, key, justThis, mtrl, resolvePlayer) is not { } modelPath) continue;

            if (!byModel.TryGetValue(modelPath, out var group))
                byModel[modelPath] = group = (new List<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            group.Materials.Add(mtrl);
            group.Leaves.Add(leaf);
        }

        foreach (var (modelPath, (mats, leaves)) in byModel)
        {
            if (LoadUpstream(modelPath, resolvePlayer, isOwnOutput, modelsDir) is not { } bytes) continue;

            // The filter is part of the key: a different set of materials is a different rewrite.
            var leafKey = string.Join("|", leaves.OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
            // Rewritten already from these exact bytes; the publish still runs and settles to a no-op.
            if (_rewrites.TryGetValue((modelPath, leafKey), out var memo) && ReferenceEquals(memo.Src, bytes))
            {
                if (Publish(memo.Out, modelPath, mats, modelsDir, outputRoot, redirects, pristine, materials,
                            entries, bytes, ref changedAny, stats: null))
                    continue;
            }

            var mtrl = mats[0];   // what the decline messages below name; the rewrite covers every one
            var keep = SecondSkinWriter.KeepByLeaf(leaves);
            if (!SecondSkinWriter.TryReadLod0Geometry(bytes, out var pos, out var uv, out _, keep))
            {
                log.Warning("[Proteus] face uv: no geometry could be read from {0} for {1} — folding the "
                          + "sheet instead", modelPath, mtrl);
                continue;
            }
            if (!SurfaceMirror.LooksMirrored(pos, uv))
            {
                // Someone else's mod has already unmirrored this face. There is nothing for us to rewrite — but the
                // material is then NOT in this plan, so the composite treats it as an ordinary face and folds the
                // doubled art onto the vanilla layout (LoadRemapped → FoldFaceSplit, a crop of the +X half). Said as
                // a warning because that is a wrong picture, not a skipped step: the other half of the art is thrown
                // away, and the half that is kept lands on UVs Proteus did not lay out.
                log.Warning("[Proteus] face uv: {0}'s UV already gives each side its own texels — another mod has "
                          + "doubled this face. Proteus cannot know which half of someone else's layout is which, "
                          + "so {1}'s art is folded onto the vanilla sheet: its left half is dropped and what is "
                          + "left will not line up. Turn that mod off for this face and let Proteus double it.",
                    modelPath, mtrl);
                continue;
            }

            if (uvRemap.UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace, unmirror: true)
                is not { } convert) continue;

            byte[]? rewritten;
            FaceUvRewriter.FaceUvStats stats;
            try
            {
                rewritten = FaceUvRewriter.RewriteFaceUv0(bytes, keep, convert, out stats,
                    msg => log.Debug("[Proteus] face uv: {0}", msg));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] face uv: could not rewrite {0}", modelPath);
                continue;
            }
            if (rewritten == null)
            {
                log.Warning("[Proteus] face uv: {0} could not be rewritten into the doubled layout — "
                          + "folding the sheet instead", modelPath);
                continue;
            }

            _rewrites[(modelPath, leafKey)] = (bytes, rewritten);
            Publish(rewritten, modelPath, mats, modelsDir, outputRoot, redirects, pristine, materials,
                    entries, bytes, ref changedAny, stats);
        }

        if (materials.Count == 0) return FaceUvPlan.Empty;
        return new FaceUvPlan(materials, redirects, entries, changedAny, pristine);
    }

    /// <summary>
    /// Write the rewritten model, register its redirect, and record the material as doubled. Returns false
    /// when the write failed, leaving the material out of the plan.
    /// </summary>
    /// <param name="stats">Null when this is a memoized rewrite, whose numbers were logged when it was made.</param>
    private bool Publish(byte[] rewritten, string modelPath, IReadOnlyList<string> mats, string modelsDir,
                         string outputRoot,
                         Dictionary<string, string> redirects, Dictionary<string, byte[]> pristine,
                         HashSet<string> materials, List<FaceDoubling> entries, byte[] upstream,
                         ref bool changedAny, FaceUvRewriter.FaceUvStats? stats)
    {
        try
        {
            Directory.CreateDirectory(modelsDir);
            var disk = DoubledFacePath(modelsDir, modelPath, rewritten);
            changedAny |= SecondSkinService.WriteIfChanged(disk, rewritten);
            redirects[modelPath] = Path.GetRelativePath(outputRoot, disk).Replace('/', '\\');
            pristine[modelPath] = upstream;
            foreach (var mtrl in mats)
            {
                materials.Add(mtrl);
                entries.Add(new FaceDoubling(mtrl, modelPath, redirects[modelPath]));
            }
            if (stats is { } s)
            {
                log.Information("[Proteus] face uv: {0} -> {1} ({2} vertex(es), {3} mesh(es), {4} LOD(s), "
                              + "conflicted {5}, unsided {6}) — {7} render(s) the doubled sheet",
                    modelPath, Path.GetFileName(disk), s.VerticesWritten, s.MeshesTouched,
                    s.LodsTouched, s.Conflicted, s.Unsided, string.Join(", ", mats));
                if (s.Unsided > 0)
                    log.Warning("[Proteus] face uv: {0} vertex(es) of {1} could not be placed on either side "
                              + "and took the +X half — expect a stray triangle where one shows",
                        s.Unsided, modelPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] face uv: could not publish the rewritten {0}", modelPath);
            return false;
        }
    }

    /// <summary>
    /// The one drawn model that declares this face material; ambiguity is refused rather than guessed.
    /// </summary>
    private string? PickModel(
        IReadOnlyList<string> humanPartModels, ShellSurfaceKey key, IReadOnlySet<string> leaves, string mtrl,
        Func<string, string?> resolvePlayer)
    {
        var folder = $"/obj/face/{key.Id}/";
        string? pick = null;
        int matches = 0;
        foreach (var cand in humanPartModels)
        {
            if (!cand.Contains(folder, StringComparison.OrdinalIgnoreCase)) continue;
            // The live answer, our own rewrite included: only material names are read, which the rewrite keeps.
            var bytes = textureLoader.LoadRawFile(resolvePlayer(cand), cand);
            if (bytes == null) continue;
            List<string> mats;
            try { mats = SecondSkinWriter.MaterialNames(bytes); }
            catch { continue; }
            if (!mats.Any(m => leaves.Contains(m.TrimStart('/')))) continue;
            matches++;
            pick ??= cand;
        }

        if (matches == 0)
        {
            log.Warning("[Proteus] face uv: no drawn model under {0} declares {1} — folding the sheet instead",
                folder, mtrl);
            return null;
        }
        if (matches > 1)
        {
            log.Warning("[Proteus] face uv: {0} drawn models declare {1} — refusing to rewrite any of them, "
                      + "since half a doubled head is worse than none", matches, mtrl);
            return null;
        }
        return pick;
    }

    /// <summary>
    /// The face model as the user installed it — never our own rewrite of it. Pristine bytes are kept in
    /// memory and mirrored to <c>models/upstream/</c> (a subfolder, so <c>PruneManagedOutput</c> skips it).
    /// Refuses when the path resolves to our own output and no copy is kept: game data would publish vanilla
    /// over an installed face mod.
    /// </summary>
    private byte[]? LoadUpstream(string modelPath, Func<string, string?> resolvePlayer,
                                 Func<string?, bool> isOwnOutput, string modelsDir)
    {
        var disk = resolvePlayer(modelPath);
        bool ours = disk != null && isOwnOutput(disk);
        var upstreamDir = Path.Combine(modelsDir, "upstream");
        var mirror = Path.Combine(upstreamDir, CompositorService.SanitizeName(modelPath) + ".mdl");

        if (ours)
        {
            if (_upstreamFaces.TryGetValue(modelPath, out var remembered)) return remembered;
            if (File.Exists(mirror))
            {
                try
                {
                    var kept = File.ReadAllBytes(mirror);
                    _upstreamFaces[modelPath] = kept;
                    return kept;
                }
                catch (Exception ex)
                { log.Warning(ex, "[Proteus] face uv: could not read the kept upstream {0}", mirror); }
            }
            log.Warning("[Proteus] face uv: {0} resolves to our own output and no upstream is remembered — "
                      + "refusing to rewrite, since rewriting our own rewrite would double the fold and "
                      + "reading game data would publish vanilla over an installed face mod", modelPath);
            return null;
        }

        var bytes = textureLoader.LoadRawFile(disk, modelPath);
        if (bytes == null)
        {
            log.Warning("[Proteus] face uv: {0} could not be read — folding the sheet instead", modelPath);
            return null;
        }
        _upstreamFaces[modelPath] = bytes;
        try
        {
            Directory.CreateDirectory(upstreamDir);
            SecondSkinService.WriteIfChanged(mirror, bytes);
        }
        catch (Exception ex)
        { log.Warning(ex, "[Proteus] face uv: could not keep the upstream {0}", modelPath); }
        return bytes;
    }

    /// <summary>
    /// Where a doubled face model is published — content-addressed, because the game caches models by
    /// resolved path.
    /// </summary>
    private static string DoubledFacePath(string modelsDir, string gamePath, byte[] content)
        => Path.Combine(modelsDir,
                        $"facelr_{CompositorService.SanitizeName(gamePath)}_{SecondSkinService.Hash(content):x16}.mdl");
}
