using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Builds the "second skin": every <see cref="OverlayLayer.Gear"/> overlay becomes a shell — a copy of
/// the character's skin mesh, pushed out along its normals and drawn as gear so it can run a full gear
/// shader (color table, sphere maps, metalness, scrolling emissive), none of which skin.shpk offers.
///
/// The shells ride on The Emperor's New accessories (set a0053), which are invisible, so they survive
/// any outfit and cost no visible equipment slot. Chest rides the right ring, legs the left.
/// </summary>
public sealed class SecondSkinService
{
    private readonly PenumbraBridge penumbra;
    private readonly TextureLoader textureLoader;
    private readonly SidecarDiscoveryService discovery;
    private readonly UVRemapService uvRemap;
    private readonly IPluginLog log;

    /// <summary>Textures are authored in BODY UV (the shell inherits the body's UVs).</summary>
    private const int TexSize = 2048;

    /// <summary>Coverage only decides whether a whole triangle survives, so it can be coarse.</summary>
    private const int CoverageSize = 256;

    /// <summary>The Emperor's New Ring — invisible, so a shell on it shows only our material.</summary>
    private const int EmperorSetId = 53;
    private const string Accessory = "rir";
    private const string EqdpSlot = "RFinger";

    /// <summary>
    /// Every skin part is MERGED into the one ring model, each part contributing its own mesh groups.
    /// A part × layer group carries that layer's material, so different regions can run different
    /// shaders. Parts the character isn't drawing are simply skipped.
    /// </summary>
    private static readonly string[] Parts = ["top", "dwn", "glv", "sho"];

    public SecondSkinService(
        PenumbraBridge penumbra, TextureLoader textureLoader, SidecarDiscoveryService discovery,
        UVRemapService uvRemap, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.textureLoader = textureLoader;
        this.discovery = discovery;
        this.uvRemap = uvRemap;
        this.log = log;
    }

    /// <summary>
    /// Files to redirect, plus the metadata edits that make the shells load.
    /// <paramref name="ShellChanged"/> is true only when the model or a material actually differs from
    /// what was already on disk. Glamourer's in-place reload can't see .mdl/.mtrl changes, so those runs
    /// need a full redraw — but a run that rewrites identical bytes must not force one.
    /// </summary>
    public sealed record Result(
        Dictionary<string, string> Redirects, List<object> Manipulations, bool ShellChanged);

    /// <summary>Write only if the content differs; reports whether it did.</summary>
    private static bool WriteIfChanged(string path, byte[] data)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
                return false;
        }
        catch { /* unreadable — fall through and rewrite */ }

        File.WriteAllBytes(path, data);
        return true;
    }

    /// <summary>
    /// Content hash of each shell texture we last wrote, so we can tell a real change from a rewrite of
    /// identical bytes. The shell's TEXTURES matter as much as its model: an opacity or mask edit only
    /// moves coverage, which lands in the normal map — and the game won't pick that up on an in-place
    /// reload either, because the texture belongs to an accessory rather than the body.
    /// </summary>
    private readonly Dictionary<string, ulong> _texHashes = new(StringComparer.OrdinalIgnoreCase);

    private static ulong Hash(byte[] data)
    {
        ulong h = 14695981039346656037;   // FNV-1a
        foreach (var b in data) { h ^= b; h *= 1099511628211; }
        return h;
    }

    /// <summary>
    /// Build every gear shell for the character. <paramref name="charCode"/> is the human model code
    /// ("0201" = Midlander female). <paramref name="outputRoot"/> is the managed mod directory.
    /// Returns null when there is nothing to build.
    /// </summary>
    /// <summary>
    /// The UV space an overlay's art is painted in, inferred from the body materials it targets — a mod
    /// listing only <c>*_bibo.mtrl</c> is bibo art. Returns null when the materials disagree or name no
    /// body type, in which case the art is assumed to already be in the body's space.
    /// </summary>
    /// <summary>The UV space of a body model, read from its own skin material's suffix, or null.</summary>
    private static string? SkinBodyType(byte[] model)
    {
        try
        {
            return SecondSkinWriter.MaterialNames(model)
                .Select(SecondSkinWriter.SkinMaterialBodyType)
                .FirstOrDefault(t => t != null);
        }
        catch { return null; }
    }

    private static string? InferOverlayBodyType(OverlayDescriptor d)
    {
        string? found = null;
        foreach (var p in d.MaterialGamePaths)
        {
            var t = UVRemapService.InferBodyType(p);
            if (t == null) continue;
            if (found == null) found = t;
            else if (!string.Equals(found, t, StringComparison.OrdinalIgnoreCase)) return null;   // mixed
        }
        return found;
    }

    /// <summary>
    /// Load an overlay image and, if it was painted for a different body's UV layout, remap it into the
    /// body's UV space. The shell INHERITS the body's UVs, so the destination is the character's body UV
    /// type — not the accessory material's. Mirrors CompositorService.RemapIfNeeded; keep them in step.
    /// </summary>
    private byte[]? LoadRemapped(string? rel, string sidecarRoot, string? srcType, string? dstType, int w, int h)
    {
        if (rel == null) return null;
        var path = Path.Combine(sidecarRoot, rel);
        return RemapPath(path, srcType, dstType, w, h);
    }

    private byte[]? RemapPath(string path, string? srcType, string? dstType, int w, int h)
    {
        var png = textureLoader.LoadPngAsRgba(path, w, h);
        if (png == null || srcType == null || dstType == null) return png;
        if (string.Equals(srcType, dstType, StringComparison.OrdinalIgnoreCase)) return png;

        // Any source -> gen2 (vanilla): vanilla UV is the RIGHT HALF of bibo UV space, so convert to
        // bibo first (via transfer map when needed), crop, then resize.
        if (string.Equals(dstType, "gen2", StringComparison.OrdinalIgnoreCase))
        {
            var native = textureLoader.LoadPngAsRgba(path, 4096, 4096);
            if (native == null) return png;
            byte[] biboSpace;
            if (string.Equals(srcType, "bibo", StringComparison.OrdinalIgnoreCase))
            {
                biboSpace = native;
            }
            else
            {
                var converted = uvRemap.Remap(native, 4096, 4096, srcType, "bibo");
                if (ReferenceEquals(converted, native)) return png;   // no transfer map — leave it alone
                biboSpace = converted;
            }
            var rightHalf = UVRemapService.CropRightHalf(biboSpace, 4096, 4096);
            return UVRemapService.ResizeBilinear(rightHalf, 2048, 4096, w, h);
        }

        // Transfer maps operate at 4096x4096; our textures are smaller, so remap at full res then resize.
        if (w != 4096 || h != 4096)
        {
            var native4k = textureLoader.LoadPngAsRgba(path, 4096, 4096);
            if (native4k == null) return png;
            var remapped = uvRemap.Remap(native4k, 4096, 4096, srcType, dstType);
            if (ReferenceEquals(remapped, native4k)) return png;
            return UVRemapService.ResizeBilinear(remapped, 4096, 4096, w, h);
        }
        return uvRemap.Remap(png, w, h, srcType, dstType);
    }

    public Result? Build(
        string charCode,
        IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        string outputRoot,
        string? bodyType,
        string? effectsFolder,
        IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)>? allOverlays = null,
        IReadOnlyDictionary<string, string>? equippedPartModels = null,
        IReadOnlyDictionary<string, string>? equippedAccessories = null,
        Func<string, bool>? gen2Allowed = null)
    {
        if (gearOverlays.Count == 0) return null;

        // A mask's forced-opacity term belongs to the mod's HIGHEST-priority group alone (see
        // CompositorService.ApplyCoverageMask — same rule, and the two must agree). A gear overlay in a
        // lower group must not be handed coverage inside a mask's territory: it becomes a SHELL, so it
        // would render its art, its relief and its color rows straight over whatever the mask is meant to
        // be. The ranking spans BOTH layers — a skin-layer group can outrank a gear one — so it has to be
        // taken over every active overlay of the mod, not just the gear ones.
        var topGroupByMod = (allOverlays ?? gearOverlays)
            .GroupBy(p => p.Entry.ModDirectory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(p => p.Overlay.GroupOrder), StringComparer.OrdinalIgnoreCase);

        bool MaskAdds(OverlayEntry e, ResolvedOverlay o)
            => !topGroupByMod.TryGetValue(e.ModDirectory, out var top) || o.GroupOrder <= top;

        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manipulations = new List<object>();

        var modelsDir = Path.Combine(outputRoot, "models");
        var materialsDir = Path.Combine(outputRoot, "materials");
        var texturesDir = Path.Combine(outputRoot, "textures");
        Directory.CreateDirectory(modelsDir);
        Directory.CreateDirectory(materialsDir);
        Directory.CreateDirectory(texturesDir);

        // ── every skin part the character is drawing, MERGED into the one ring model ──
        // The shell is a COPY of the body geometry, so it must be cut from the models the character is
        // actually drawing. A shell built from any other body/size is a different shape and the body
        // pokes through it at any push distance. Resolve them live, every time.
        // gen2 (vanilla) is opt-in per the gear mode, exactly like the skin layer's gen2 sibling — but the
        // gate is per-PART, not per-character: a bibo torso plus a vanilla skirt's exposed legs is ONE
        // shell, and only the vanilla legs must be withheld unless a gear overlay opted into "All bodies".
        bool anyGen2Allowed = gen2Allowed == null || gearOverlays.Any(g => gen2Allowed(g.Entry.ModDirectory));

        var bodies = new List<byte[]>();
        string? modelType = null;   // UV space of the first kept part, from its own skin material
        foreach (var part in Parts)
        {
            // When gear is equipped in a slot, the bare-body part for that slot ISN'T drawn — the gear
            // model is, and it carries the skin it exposes posed to fit (a high heel tiptoes the foot,
            // a bikini bottom reshapes the hip, etc.), as an mt_c….b….skin mesh beside its cloth meshes.
            // Cut the shell from that equipped model so it deforms WITH the gear AND covers only the skin
            // the gear actually exposes (the hidden skin under cloth isn't in the model, so nothing pokes
            // through it); the flat bare-body e0000 would shell the whole body and float off the posed
            // skin. The skin-material filter in SecondSkinWriter keeps only the skin mesh. Slots with no
            // gear (or gear that exposes no skin) fall back to the bare body e0000.
            var bodyGamePath = equippedPartModels != null && equippedPartModels.TryGetValue(part, out var eq)
                ? eq
                : $"chara/equipment/e0000/model/c{charCode}e0000_{part}.mdl";
            // ResolvePlayer only yields a real file for MODDED models; a vanilla piece resolves to the
            // game path unchanged, so read from the game data in that case. The transcoder reads each
            // model's own vertex declaration, so vanilla and modded models both skin correctly.
            var bodyDisk = penumbra.ResolvePlayer(bodyGamePath);
            var bytes = textureLoader.LoadRawFile(bodyDisk, bodyGamePath);
            if (bytes == null)
            {
                log.Debug("[Proteus] second skin: {0} not loadable, skipping", bodyGamePath);
                continue;
            }

            // The part's UV space names itself in its skin material's suffix. A vanilla (gen2) part gets
            // no shell unless a gear overlay is set to All bodies — otherwise the overlay would wear on
            // vanilla whether or not the author opted in. Ambiguity (a vanilla _a material alongside a
            // gen3 body) is avoided by reading THIS part's own model rather than the loaded-material soup.
            var partType = SkinBodyType(bytes);
            if (string.Equals(partType, "gen2", StringComparison.OrdinalIgnoreCase) && !anyGen2Allowed)
            {
                log.Information("[Proteus] second skin: {0} is vanilla (gen2) — no gear overlay opted into All bodies, skipping part", bodyGamePath);
                continue;
            }
            bodies.Add(bytes);
            modelType ??= partType;
        }
        if (bodies.Count == 0)
        {
            log.Warning("[Proteus] second skin: no skin models resolved for c{0} (or all parts gated out)", charCode);
            return null;
        }

        if (modelType != null && !string.Equals(modelType, bodyType, StringComparison.OrdinalIgnoreCase))
        {
            log.Information("[Proteus] second skin: body UV is {0} per the model's material (was {1})",
                modelType, bodyType ?? "unknown");
            bodyType = modelType;
        }

        // Choose the accessory the shell rides on: a ring the player already wears (fewest materials, right
        // wins a tie), else a bracelet, else the invisible Emperor's New Ring. For an already-equipped host
        // the shell's meshes are APPENDED to its model; only the Emperor fallback replaces + forces a model.
        var host = ChooseHost(charCode, equippedAccessories, gearOverlays.Count);

        // Only a shell whose bytes actually differ from what's on disk needs a full redraw.
        bool shellChanged = false;

        var layers = new List<SecondSkinLayer>();
        for (int i = 0; i < gearOverlays.Count; i++)
        {
            var (entry, ov) = gearOverlays[i];
            var sidecarRoot = entry.SidecarRoot;

            string shader = ov.Descriptor.ShaderPackage;
            // Appended materials start AFTER the host's own (indices 0..BaseMatCount-1): _a for the Emperor
            // fallback, but _b/_c… when the host already carries materials, so nothing collides.
            char letter = (char)('a' + host.BaseMatCount + i);
            string matName = $"mt_c{charCode}a{host.SetId:D4}_{host.Slot}_{letter}.mtrl";
            string matGamePath = $"chara/accessory/a{host.SetId:D4}/material/v0001/{matName}";

            // Textures live under the accessory's own path, so they collide with nothing.
            string texPrefix = $"chara/accessory/a{host.SetId:D4}/texture/ss_{letter}_";

            // Coverage is authored the same way as on the skin layer: the overlay's own alpha, SHAPED BY
            // the mod's selected "Masks" options. For a lot of mods the diffuse is opaque everywhere and
            // the mask IS the shape, so skipping masks would paint the whole body.
            // Which UV space is this art painted in? An overlay usually doesn't say — it just lists the
            // body materials it targets, and THAT is the declaration: a mod listing only *_bibo.mtrl is
            // bibo art. The skin layer gets this gating for free (a bibo overlay simply doesn't match a
            // gen3 body's material, so it never composites). The gear layer has no such gate — it paints
            // the shell regardless — so without this it would spray bibo art across a gen3 body.
            var srcType = ov.Descriptor.SourceBodyType ?? InferOverlayBodyType(ov.Descriptor);
            log.Information(
                "[Proteus] gear layer {0}: shader={1} UV {2} -> {3}{4}",
                letter, shader, srcType ?? "(unknown)", bodyType ?? "(unknown)",
                srcType != null && bodyType != null && !string.Equals(srcType, bodyType, StringComparison.OrdinalIgnoreCase)
                    ? " [REMAP]" : " [no remap]");
            var alpha = BuildAlpha(ov.Descriptor, entry, srcType, bodyType, TexSize, TexSize, MaskAdds(entry, ov));
            var coverage = Downsample(alpha, TexSize, TexSize, CoverageSize);

            var texPaths = WriteTextures(
                entry, ov.Descriptor, shader, texPrefix, texturesDir, redirects, letter, alpha,
                srcType, bodyType, ov.ColorTableRows, effectsFolder, ref shellChanged);
            if (texPaths == null) continue;

            // Both templates are vanilla game materials, so no mod needs to be installed.
            var template = textureLoader.LoadRawMtrl(null, GearMaterialWriter.TemplateFor(shader));
            if (template == null)
            {
                log.Error("[Proteus] second skin: missing template material for {0}", shader);
                continue;
            }

            var scroll = new ScrollSettings(
                ov.Descriptor.ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                ov.Descriptor.ScrollSpeedY ?? ScrollSettings.Default.SpeedY,
                ov.Descriptor.ScrollTilingX ?? ScrollSettings.Default.TilingX,
                ov.Descriptor.ScrollTilingY ?? ScrollSettings.Default.TilingY);

            byte[] mtrl;
            try { mtrl = GearMaterialWriter.Build(template, texPaths, BuildRows(ov.ColorTableRows), scroll); }
            catch (Exception ex) { log.Error(ex, "[Proteus] second skin: material build failed for {0}", shader); continue; }

            var matDisk = Path.Combine(materialsDir, $"ss_{letter}.mtrl");
            shellChanged |= WriteIfChanged(matDisk, mtrl);
            redirects[matGamePath] = Rel(outputRoot, matDisk);

            layers.Add(new SecondSkinLayer
            {
                MaterialName = "/" + matName,   // the model stores material names with a leading slash
                Coverage = coverage,
                CoverageWidth = coverage == null ? 0 : CoverageSize,
                CoverageHeight = coverage == null ? 0 : CoverageSize,
            });
        }
        if (layers.Count == 0) return null;

        byte[] shell;
        SecondSkinWriter.Stats stats;
        try { shell = SecondSkinWriter.Build(bodies, layers, host.BaseModel, out stats); }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] second skin: model build failed");
            return null;
        }

        var mdlGamePath = $"chara/accessory/a{host.SetId:D4}/model/c{charCode}a{host.SetId:D4}_{host.Slot}.mdl";
        var mdlDisk = Path.Combine(modelsDir, "secondskin.mdl");
        shellChanged |= WriteIfChanged(mdlDisk, shell);
        redirects[mdlGamePath] = Rel(outputRoot, mdlDisk);
        log.Information("[Proteus] second skin: redirect {0} -> {1} (host a{2:D4} {3}, append={4})",
            mdlGamePath, Rel(outputRoot, mdlDisk), host.SetId, host.Slot, host.BaseModel != null);

        // The Emperor's New Ring is invisible and not really equipped, so it needs an EQDP entry to load a
        // model at all (and for THIS race). A real equipped host is already loading its own model — we just
        // redirect it — so it must NOT get an EQDP edit.
        if (host.BaseModel == null)
        {
            manipulations.Add(EqdpManipulation(charCode, host.EqdpSlot));
            log.Information("[Proteus] second skin: EQDP added for {0} slot (Emperor fallback)", host.EqdpSlot);
        }
        else
        {
            log.Information("[Proteus] second skin: no EQDP (real equipped host a{0:D4} {1} already loads its model)",
                host.SetId, host.Slot);
        }

        log.Information(
            "[Proteus] second skin: {0} part(s) x {1} layer(s) -> {2} meshes, {3} submeshes, {4} bones, " +
            "{5}/{6} triangles kept ({7:F0}% trimmed), {8} KB",
            bodies.Count, layers.Count, stats.Meshes, stats.Submeshes, stats.Bones,
            stats.TrianglesOut, stats.TrianglesIn,
            stats.TrianglesIn == 0 ? 0 : (stats.TrianglesIn - stats.TrianglesOut) * 100.0 / stats.TrianglesIn,
            shell.Length / 1024);

        return new Result(redirects, manipulations, shellChanged);
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('/', '\\');

    /// <summary>
    /// The overlay's coverage, exactly as the skin layer computes it: the art's own alpha, then shaped
    /// by the mod's selected "Masks" options. Returns one byte per texel, or null when nothing bounds it
    /// (in which case the shell covers the whole body).
    ///
    /// Each mask contributes W *= (1-a) and T += gray*a, and the result is baseAlpha*W/255 + T — so a
    /// mask can both carve coverage away and force it on. Mirrors CompositorService.CombinedMaskAt /
    /// ApplyCoverageMask; keep the two in step.
    /// </summary>
    private byte[]? BuildAlpha(
        OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType, int w, int h,
        bool maskAdds = true)
    {
        var artPath = d.Diffuse ?? d.Normal ?? d.Mask;
        var masks = discovery.ResolveActiveMasks(entry);
        if (artPath == null && masks.Count == 0) return null;

        int n = w * h;
        var alpha = new byte[n];

        if (artPath != null)
        {
            var art = LoadRemapped(artPath, entry.SidecarRoot, srcType, dstType, w, h);
            if (art == null) return null;
            for (int i = 0; i < n; i++) alpha[i] = art[i * 4 + 3];
        }
        else
        {
            Array.Fill(alpha, (byte)255);
        }

        // combine the selected masks into weight/target, then apply
        byte[]? wArr = null, tArr = null;
        for (int p = masks.Count - 1; p >= 0; p--)
        {
            var m = RemapPath(masks[p], srcType, dstType, w, h);   // masks share the overlay's UV space
            if (m == null) continue;
            if (wArr == null)
            {
                wArr = new byte[n];
                tArr = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    int o = i * 4, a = m[o + 3];
                    int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;   // luminance
                    wArr[i] = (byte)(255 - a);
                    tArr[i] = (byte)(g * a / 255);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    int o = i * 4, a = m[o + 3];
                    int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;
                    int inv = 255 - a;
                    tArr![i] = (byte)(tArr[i] * inv / 255 + g * a / 255);
                    wArr[i] = (byte)(wArr[i] * inv / 255);
                }
            }
        }

        if (wArr != null)
            for (int i = 0; i < n; i++)
            {
                if (alpha[i] == 0) continue;                       // no base coverage -> mask has no say
                int v = alpha[i] * wArr[i] / 255 + (maskAdds ? tArr![i] : 0);
                alpha[i] = (byte)(v > 255 ? 255 : v);
            }

        long opaque = 0, clear = 0;
        foreach (var a in alpha) { if (a == 0) clear++; else if (a == 255) opaque++; }
        log.Information(
            "[Proteus] gear coverage: art={0} masks={1} [{2}] -> {3:F1}% clear, {4:F1}% opaque, {5:F1}% partial",
            artPath ?? "none", masks.Count,
            string.Join(", ", masks.Select(Path.GetFileNameWithoutExtension)),
            clear * 100.0 / n, opaque * 100.0 / n, (n - clear - opaque) * 100.0 / n);

        return alpha;
    }

    /// <summary>Box-downsample the coverage for triangle trimming; it only decides keep/drop.</summary>
    private static byte[]? Downsample(byte[]? src, int w, int h, int size)
    {
        if (src == null) return null;
        var dst = new byte[size * size];
        int sx = w / size, sy = h / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int max = 0;   // keep a texel if ANY source texel under it is visible
                for (int j = 0; j < sy; j++)
                    for (int i = 0; i < sx; i++)
                        max = Math.Max(max, src[(y * sy + j) * w + x * sx + i]);
                dst[y * size + x] = (byte)max;
            }
        return dst;
    }

    /// <summary>
    /// Author the shell's textures. Returns the game paths in the shader's slot order, or null on
    /// failure. The overlay's alpha is written into the NORMAL's BLUE channel — that is what gates
    /// transparency for gear, and therefore what lets stacked shells composite instead of occlude.
    /// </summary>
    private List<string>? WriteTextures(
        OverlayEntry entry, OverlayDescriptor d, string shader, string texPrefix,
        string texturesDir, Dictionary<string, string> redirects, char letter, byte[]? alpha,
        string? srcType, string? dstType, List<ColorTableRowPreset>? rows, string? effectsFolder,
        ref bool texturesChanged)
    {
        var sidecarRoot = entry.SidecarRoot;
        var outputRoot = Directory.GetParent(texturesDir)!.FullName;

        byte[]? Png(string? rel) => LoadRemapped(rel, sidecarRoot, srcType, dstType, TexSize, TexSize);

        var diffuse = Png(d.Diffuse);
        var normal = Png(d.Normal);
        var mask = Png(d.Mask);
        var index = Png(d.Index);

        // The scroll map is NOT body-UV art — it's a tiling pattern the shader samples with uv1, so it
        // must NOT be UV-remapped (that would tear the pattern apart). It also lives in an effects
        // folder, not the sidecar tree, so resolve it separately.
        byte[]? scroll = null;
        if (d.Scroll != null)
        {
            var effectPath = SidecarDiscoveryService.ResolveEffectPath(entry, effectsFolder, d.Scroll);
            if (effectPath != null)
                scroll = textureLoader.LoadPngAsRgba(effectPath, TexSize, TexSize);
            else
                log.Warning("[Proteus] second skin: effect \"{0}\" not found", d.Scroll);
        }

        byte[] Solid(byte r, byte g, byte b, byte a)
        {
            var t = new byte[TexSize * TexSize * 4];
            for (int i = 0; i < t.Length; i += 4) { t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a; }
            return t;
        }

        // ── Proteus "Masks" options ──────────────────────────────────────────
        // A mask isn't only coverage: its export can also ship its OWN row assignment (Masks/<x>_id.png)
        // and relief normal (Masks/<x>_n.dds). The skin layer merges both — LoadIndexMerged and the
        // masks-driven relief pass in CompositorService — so the gear layer must too, or a mask silently
        // loses its rows and its bump. (Coverage itself is already folded in by BuildAlpha.)
        foreach (var (maskPath, maskNormalPath, maskIndexPath) in discovery.ResolveActiveMaskAssets(entry))
        {
            var maskPng = RemapPath(maskPath, srcType, dstType, TexSize, TexSize);
            if (maskPng == null) continue;

            if (maskIndexPath != null)
            {
                var maskIdx = RemapPath(maskIndexPath, srcType, dstType, TexSize, TexSize);
                if (maskIdx != null)
                {
                    // LoadPngAsRgba hands back a shared cached array — clone before writing into it.
                    index = index != null ? (byte[])index.Clone() : Solid(0, 0, 0, 255);
                    for (int i = 0; i < index.Length; i += 4)
                    {
                        if (maskPng[i + 3] < 128) continue;   // only where the mask is actually present
                        index[i]     = maskIdx[i];            // red   → row pair
                        index[i + 1] = maskIdx[i + 1];        // green → sub-row
                    }
                }
            }

            if (maskNormalPath != null)
            {
                var maskNormal = RemapPath(maskNormalPath, srcType, dstType, TexSize, TexSize);
                if (maskNormal != null)
                {
                    // The mask's relief IS the surface there — replace the base normal rather than
                    // piling a second bump on top of it (same rule as the skin layer).
                    normal = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
                    CompositorService.ReplaceNormal(normal, maskNormal, TexSize, TexSize, maskPng);
                }
            }
        }

        // ── per-row opacity ──────────────────────────────────────────────────
        // Each color table row carries an Opacity (-100..100), and the index texture says which row a
        // pixel uses — so opacity is per-region, not global. Same blend the skin layer applies
        // (CompositorService.ApplyIndexedOpacity): negative fades toward transparent, positive pushes
        // toward opaque, interpolated between sub-rows A and B by the index's green channel.
        if (alpha != null && index != null && rows is { Count: > 0 })
        {
            alpha = (byte[])alpha.Clone();
            for (int i = 0; i < alpha.Length; i++)
            {
                float a = alpha[i] / 255f;
                if (a <= 0f) continue;

                int pair = index[i * 4] / 17 + 1;                       // red → 1-based row pair
                var preset = rows.FirstOrDefault(p => p.Row == pair);
                if (preset == null) continue;

                float blendA = index[i * 4 + 1] / 255f;                 // green → sub-row A weight
                float opA = preset.SubRowA?.Opacity ?? 0;
                float opB = preset.SubRowB?.Opacity ?? 0;
                float op = opB + (opA - opB) * blendA;
                if (op == 0f) continue;

                float newA = op < 0f ? a * (100f + op) / 100f : a + (1f - a) * op / 100f;
                alpha[i] = (byte)(Math.Clamp(newA, 0f, 1f) * 255f + 0.5f);
            }
        }

        // norm: RG = the normal itself, B = TRANSPARENCY (the gear alpha gate), A = unused.
        // This is the whole trick: a Proteus overlay is gated by opacity, and on the gear layer that
        // opacity has to be translated into the normal map's BLUE channel or the shell renders solid.
        // (It also needs the material's transparency flag on — see GearMaterialWriter.)
        var norm = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
        for (int i = 0; i < TexSize * TexSize; i++)
            norm[i * 4 + 2] = alpha?[i] ?? 255;   // blue is the gate; alpha is not used

        var slots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["norm"] = norm,
            // A fabricated mask must be WHITE, not mid-grey. The gear shaders read occlusion/gloss out
            // of it, so a 50% grey mask halves the lighting everywhere and a white surface renders grey.
            ["mask"] = mask ?? Solid(255, 255, 255, 255),
            // No index texture → select Row 16 sub-row A everywhere, matching the SKIN layer's fallback
            // (it applies row16A as a flat tint when desc.Index == null). red 255 → row pair 16, green 255
            // → sub-row A. Defaulting to black (row 1) instead picked up the template's default row — which
            // renders the shell a flat red — and ignored the Row 16 tint the overlay actually carries.
            ["id"]   = index ?? Solid(255, 255, 0, 255),

            ["base"] = diffuse ?? Solid(255, 255, 255, 255),  // tint also comes from the color table
            ["catc"] = scroll ?? Solid(0, 0, 0, 255),         // black = no glow
        };

        var order = GearMaterialWriter.TextureOrder(shader);
        var paths = new List<string>(order.Count);
        foreach (var slot in order)
        {
            var gamePath = texPrefix + slot + ".tex";
            var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");

            // Skip the write when the content is byte-identical to what we last wrote — otherwise every
            // recomposite would look like a change and force a redraw.
            var hash = Hash(slots[slot]);
            bool same = _texHashes.TryGetValue(disk, out var prev) && prev == hash && File.Exists(disk);
            if (!same)
            {
                if (!textureLoader.WriteTex(slots[slot], TexSize, TexSize, disk))
                {
                    log.Error("[Proteus] second skin: failed to write {0}", disk);
                    return null;
                }
                _texHashes[disk] = hash;
                texturesChanged = true;
            }

            redirects[gamePath] = Rel(outputRoot, disk);
            paths.Add(gamePath);
        }
        return paths;
    }

    /// <summary>Map the metadata's 1-based row/sub-row presets onto 0-based color table rows.</summary>
    private static Dictionary<int, GearColorRow>? BuildRows(List<ColorTableRowPreset>? presets)
    {
        if (presets == null || presets.Count == 0) return null;
        var rows = new Dictionary<int, GearColorRow>();

        foreach (var p in presets)
        {
            if (p.Row is < 1 or > 16) continue;
            Add((p.Row - 1) * 2, p.SubRowA);
            Add((p.Row - 1) * 2 + 1, p.SubRowB);
        }
        return rows.Count == 0 ? null : rows;

        void Add(int rowIndex, ColorTableSubRowPreset? sub)
        {
            if (sub == null) return;
            var rgb = ParseHex(sub.Diffuse);
            // Glow colour is INDEPENDENT of the diffuse (a scrolling material wants a near-black diffuse
            // with a white emissive), falling back to the diffuse when not given.
            var emis = ParseHex(sub.EmissiveColor) ?? rgb;
            rows[rowIndex] = new GearColorRow
            {
                Diffuse = rgb,
                Specular = ParseHex(sub.Specular),
                // Always write emissive — a template's own emissive must be CLEARED, not inherited.
                // Vanilla characterscroll rows carry a warm non-zero emissive that renders as a flat
                // white glow and drowns out the scroll map entirely.
                Emissive = sub.Emissive > 0f && emis is { } c
                    ? (c.R * sub.Emissive, c.G * sub.Emissive, c.B * sub.Emissive)
                    : (0f, 0f, 0f),
                SphereMapIndex = sub.SphereMap,
                SphereMapMask = sub.SphereIntensity,
                Roughness = sub.Roughness,
                Metalness = sub.Metalness,
            };
        }
    }

    private static (float R, float G, float B)? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    // ── EQDP ─────────────────────────────────────────────────────────────────

    private static readonly string[] RaceNames =
    [
        "Midlander", "Highlander", "Elezen", "Miqote", "Roegadyn",
        "Lalafell", "AuRa", "Hrothgar", "Viera",
    ];

    /// <summary>
    /// The accessory a second skin rides on. For an already-equipped ring/bracelet <see cref="BaseModel"/>
    /// holds its model bytes (the shell is appended into it) and <see cref="BaseMatCount"/> its material
    /// count; for the Emperor's New Ring fallback both are 0/null and an EQDP edit forces the model.
    /// </summary>
    private readonly record struct HostAccessory(int SetId, string Slot, string EqdpSlot, byte[]? BaseModel, int BaseMatCount);

    /// <summary>
    /// Pick the accessory to host the shell. Prefer a ring the player already wears (the one with the
    /// FEWEST materials, right ring winning a tie), else a bracelet, else the invisible Emperor's New Ring.
    /// A candidate is skipped — with a warning to chat AND log — when it has no room to append this many
    /// overlays: a model carries at most <see cref="SecondSkinWriter.MaxMaterials"/> materials, so the
    /// host's own count plus the layer count must not exceed it.
    /// </summary>
    private HostAccessory ChooseHost(string charCode, IReadOnlyDictionary<string, string>? equipped, int layerCount)
    {
        int budget = SecondSkinWriter.MaxMaterials - layerCount;   // base materials must fit under this

        log.Information("[Proteus] host: choosing from equipped accessories [{0}] (budget {1} = {2} - {3} layers)",
            equipped == null ? "(null)" : string.Join(", ", equipped.Select(kv => $"{kv.Key}={kv.Value}")),
            budget, SecondSkinWriter.MaxMaterials, layerCount);

        // Load an equipped accessory as a host candidate, or null if absent, unloadable, or over budget
        // (in which case it warns to chat + log and is skipped).
        HostAccessory? Consider(string slot, string eqdpSlot)
        {
            if (equipped == null || !equipped.TryGetValue(slot, out var gamePath))
            {
                log.Information("[Proteus] host: {0} — none equipped", slot);
                return null;
            }

            int setId = ParseSetId(gamePath);

            // The Emperor's New Ring is invisible and only loads a model via our own EQDP edit — it is the
            // FALLBACK, never an append host. Appending to it skips that EQDP, so its model never loads and
            // nothing renders. Skip it here so it drops through to the replace+EQDP path below.
            if (setId == EmperorSetId)
            {
                log.Information("[Proteus] host: {0} is the Emperor's ring (a{1:D4}) — reserved for fallback, skipping", slot, setId);
                return null;
            }

            var disk = penumbra.ResolvePlayer(gamePath);
            var bytes = textureLoader.LoadRawFile(disk, gamePath);
            if (bytes == null)
            {
                log.Warning("[Proteus] host: {0} (a{1:D4}) model {2} not loadable (disk={3}) — skipping", slot, setId, gamePath, disk ?? "(null)");
                return null;
            }

            int mats;
            try { mats = SecondSkinWriter.MaterialNames(bytes).Count; }
            catch (Exception ex) { log.Warning(ex, "[Proteus] host: {0} (a{1:D4}) material parse failed — skipping", slot, setId); return null; }

            if (mats > budget)
            {
                var msg = $"[Proteus] equipped {slot} accessory (a{setId:D4}) has {mats} material(s) — no room to append "
                        + $"{layerCount} overlay(s) (max {SecondSkinWriter.MaxMaterials}); skipping it as a host.";
                Plugin.ChatGui.Print(msg);
                log.Warning(msg);
                return null;
            }
            log.Information("[Proteus] host: {0} (a{1:D4}) is a candidate — {2} material(s), {3} bytes", slot, setId, mats, bytes.Length);
            return new HostAccessory(setId, slot, eqdpSlot, bytes, mats);
        }

        // Rings first: fewest materials, right (rir) wins a tie.
        var right = Consider("rir", "RFinger");
        var left = Consider("ril", "LFinger");
        HostAccessory? ring =
            right != null && left != null
                ? (left.Value.BaseMatCount < right.Value.BaseMatCount ? left : right)
                : right ?? left;
        if (ring != null)
        {
            log.Information("[Proteus] host: CHOSEN ring a{0:D4} ({1}), {2} base material(s) — appending",
                ring.Value.SetId, ring.Value.Slot, ring.Value.BaseMatCount);
            return ring.Value;
        }

        // Then a bracelet.
        var wrist = Consider("wrs", "Wrists");
        if (wrist != null)
        {
            log.Information("[Proteus] host: CHOSEN bracelet a{0:D4} (wrs), {1} base material(s) — appending",
                wrist.Value.SetId, wrist.Value.BaseMatCount);
            return wrist.Value;
        }

        // Fallback: the invisible Emperor's New Ring (replace + EQDP). Base 0 always fits when the caller's
        // layer count is within the material cap.
        log.Information("[Proteus] host: FALLBACK to Emperor's New Ring a{0:D4} ({1}) — replace + EQDP", EmperorSetId, Accessory);
        return new HostAccessory(EmperorSetId, Accessory, EqdpSlot, null, 0);
    }

    /// <summary>The accessory set id from a model path, e.g. "…/a0114/model/c0201a0114_rir.mdl" → 114.</summary>
    private static int ParseSetId(string gamePath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, @"/a(\d+)/");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : EmperorSetId;
    }

    /// <summary>
    /// Tell the game this accessory has a model for the character's own race/gender. Char codes run
    /// c0101 = Midlander male, c0201 = Midlander female, c0301 = Highlander male, and so on.
    /// </summary>
    private static object EqdpManipulation(string charCode, string slot)
    {
        int n = int.TryParse(charCode.AsSpan(0, 2), out var parsed) ? parsed : 2;
        string race = RaceNames[Math.Clamp((n - 1) / 2, 0, RaceNames.Length - 1)];
        string gender = n % 2 == 1 ? "Male" : "Female";

        return new
        {
            Type = "Eqdp",
            Manipulation = new
            {
                Entry = 192,
                Gender = gender,
                Race = race,
                SetId = EmperorSetId,
                Slot = slot,
                ShiftedEntry = 3,
            },
        };
    }
}
