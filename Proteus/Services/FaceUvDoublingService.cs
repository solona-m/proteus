using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// One face material this composite renders in the DOUBLED layout, and the model rewritten to sample it.
/// </summary>
/// <param name="MaterialGamePath">The <c>_fac</c> material whose textures must be published doubled.</param>
/// <param name="ModelGamePath">The face model whose uv0 was rewritten.</param>
/// <param name="ModelRelPath">Where the rewritten model was published, relative to the managed mod.</param>
public sealed record FaceDoubling(string MaterialGamePath, string ModelGamePath, string ModelRelPath);

/// <summary>
/// Which face materials render doubled this composite, and the model redirects that make it true. Empty
/// when nothing qualified — which is the normal case for almost every character.
/// </summary>
public sealed record FaceUvPlan(
    IReadOnlySet<string> Materials,
    IReadOnlyDictionary<string, string> ModelRedirects,
    IReadOnlyList<FaceDoubling> Entries,
    bool AnyModelChanged,
    /// <summary>
    /// The PRISTINE bytes of every model rewritten this composite, by game path. Handed to
    /// <see cref="SecondSkinService"/> so a shell cut from the same face reads the model the user
    /// installed: resolving the path now answers with our rewrite, and cutting a shell from that would
    /// send its vertices through the doubling affine a second time.
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
/// Renders asymmetric FACE art by rewriting the character's own face model into the doubled sheet layout,
/// instead of cutting a second-skin shell for it.
/// <para/>
/// The shell cannot carry a face. It is emitted with its shape block zeroed, so it is a frozen duplicate of
/// the head riding a millimetre proud of a face that is still blinking, talking and emoting underneath —
/// and an opaque whole-face texture keeps every triangle of it, so the result is a second head rather than
/// a decal. Moving the UVs of the face the game is ALREADY drawing costs no geometry, keeps the face's own
/// <c>skin.shpk</c> material and therefore the wearer's skin tone, and leaves every expression intact.
/// <para/>
/// This service decides WHICH materials double and publishes the rewritten models.
/// <see cref="SecondSkinWriter.RewriteFaceUv0"/> does the byte walk, and
/// <see cref="CompositorService"/> publishes that material's textures in the doubled layout. All three have
/// to agree or the face samples a doubled sheet with vanilla coordinates, so the model rewrite runs FIRST
/// and its result is the single input the other two read.
/// </summary>
public sealed class FaceUvDoublingService
{
    private readonly IPluginLog log;
    private readonly TextureLoader textureLoader;
    private readonly UVRemapService uvRemap;

    /// <summary>
    /// The PRISTINE bytes of each face model we have rewritten, keyed by game path — the upstream, never
    /// our own output. Held for the same reason <c>SecondSkinService</c> holds the upstream bodies: once
    /// our redirect is live, resolving the path answers with the model we published, and rewriting that
    /// again would apply the affine twice (u -> 0.5 + u/2 twice is 0.75 + u/4 — a face wearing a quarter
    /// of its own texture).
    /// </summary>
    /// <remarks>
    /// CONCURRENT, like SecondSkinService's remap cache and for the same reason: composites genuinely
    /// overlap (see CompositorService's _compositesInFlight) and nothing here is locked. A plain Dictionary
    /// resizing under a concurrent read does not merely lose an entry — it can spin forever in a bucket
    /// chain and hang the thread. Sharing across runs is harmless by construction: every key names the
    /// inputs its value depends on.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte[]> _upstreamFaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The last rewrite of each (model, material), against the exact upstream bytes it was made from.
    /// <para/>
    /// A composite runs on every equipment change, every settings tweak and every ambient trigger, and the
    /// rewrite is a full parse and vertex walk of the largest model a character draws. Nothing about it
    /// changes between runs unless the face model itself does — and the pristine bytes are held by
    /// reference, so "the same array" is exactly the question worth asking. A changed face mod re-resolves
    /// to a fresh array and misses, which is the behaviour that matters.
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
    /// <para/>
    /// Everything excluded here is excluded because it needs <c>character.shpk</c> — a colour table, a
    /// scrolling glow, a mask shell, or a place above a garment where no skin shows through. Those keep the
    /// shell path they have always had. Note the art must DECLARE the doubled layout: there is no inferring
    /// it, exactly as the Create tab's tick is the only thing that can say so.
    /// </summary>
    public static bool IsCandidate(OverlayEntry entry, ResolvedOverlay overlay, bool aboveGear)
    {
        var d = overlay.Descriptor;
        if (d.AsymmetricArt != true || d.IsMaskShell) return false;
        if (!string.Equals(d.SourceBodyType, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase))
            return false;
        if (d.MaterialGamePaths.Count == 0) return false;
        // EVERY material, not any: an overlay spanning a face and something else would otherwise paint its
        // remaining surface through a shell and this one through the skin at the same time.
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
    /// the redirects. Returns <see cref="FaceUvPlan.Empty"/> whenever anything at all is in doubt: the
    /// caller then folds the doubled sheet as before, which loses a side but lands in the right place.
    /// </summary>
    /// <param name="resolved">Every enabled mod's active overlays, with each one's above-gear rank.</param>
    /// <param name="humanPartModels">The face/hair/tail models the character is actually drawing.</param>
    /// <param name="resolvePlayer">Penumbra's live answer for a game path — our own output included.</param>
    /// <param name="isOwnOutput">Whether a resolved disk path is a file this plugin published.</param>
    /// <param name="outputRoot">The managed mod directory.</param>
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

        // Grouped by MODEL, because that is what gets rewritten. A model can declare more than one face
        // material — the lashes and brows sit under /obj/face/ and classify as Face just as the skin does,
        // and a merged or custom face model can carry both — and a rewrite converts only the meshes its
        // keep filter names. Handling the second material by reusing the first one's rewrite would mark it
        // doubled while its meshes still held vanilla UVs, so those meshes would sample the wrong half of
        // their own sheet. One rewrite per model, with every one of its face materials in the filter.
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

            // The filter is part of the key: the same model rewritten for a different SET of materials is a
            // different rewrite, and reusing one for the other is the bug this grouping exists to prevent.
            var leafKey = string.Join("|", leaves.OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
            // Rewritten already, from these exact bytes — skip the parse and the vertex walk. The publish
            // below still runs: it is content-addressed and WriteIfChanged, so it settles to a no-op.
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
                // Not a fault: a face whose UV already gives each side its own texels needs no doubling at
                // all, and rewriting it would tear it in half.
                log.Information("[Proteus] face uv: {0}'s UV already gives each side its own texels — "
                              + "leaving the art as authored", modelPath);
                continue;
            }

            if (uvRemap.UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace, unmirror: true)
                is not { } convert) continue;

            byte[]? rewritten;
            SecondSkinWriter.FaceUvStats stats;
            try
            {
                rewritten = SecondSkinWriter.RewriteFaceUv0(bytes, keep, convert, out stats,
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
    /// when the write failed — the caller then leaves the material out of the plan, so its textures are
    /// published in the vanilla layout the model it still has expects.
    /// </summary>
    /// <param name="mats">Every face material on this model — all of them were in the rewrite's filter.</param>
    /// <param name="stats">Null when this is a memoized rewrite, whose numbers were logged when it was made.</param>
    private bool Publish(byte[] rewritten, string modelPath, IReadOnlyList<string> mats, string modelsDir,
                         string outputRoot,
                         Dictionary<string, string> redirects, Dictionary<string, byte[]> pristine,
                         HashSet<string> materials, List<FaceDoubling> entries, byte[] upstream,
                         ref bool changedAny, SecondSkinWriter.FaceUvStats? stats)
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
    /// The one drawn model that declares this face material. Ambiguity is refused rather than guessed: a
    /// part draws several models (a face ships eyes and brows beside the face itself), and rewriting one of
    /// two that both claim the material would leave the character with half a doubled head.
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
            // The LIVE answer, our own rewrite included: this only reads MATERIAL NAMES, which the rewrite
            // does not touch, and reading what the character actually draws is what makes the match true.
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
    /// The face model as the user installed it — never our own rewrite of it.
    /// <para/>
    /// Once the redirect from a previous composite is live, Penumbra answers this path with the model we
    /// published. Rewriting THAT applies the affine a second time, so the pristine bytes are kept in memory
    /// and mirrored to <c>models/upstream/</c> (a SUBfolder: <c>PruneManagedOutput</c> deletes unreferenced
    /// files directly under <c>models/</c>, and these are never published). When the path resolves to our
    /// own output and neither copy is available, this REFUSES rather than falling back to game data —
    /// falling back would rewrite vanilla and publish it over the user's own face mod.
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
    /// Where a doubled face model is published — CONTENT-ADDRESSED, for the reason every model Proteus
    /// writes is: the game caches models by RESOLVED PATH, so publishing each revision to one fixed name
    /// means the path never changes, the cache is never invalidated, and the character keeps whichever
    /// version it happened to load first.
    /// </summary>
    private static string DoubledFacePath(string modelsDir, string gamePath, byte[] content)
        => Path.Combine(modelsDir,
                        $"facelr_{CompositorService.SanitizeName(gamePath)}_{SecondSkinService.Hash(content):x16}.mdl");
}
