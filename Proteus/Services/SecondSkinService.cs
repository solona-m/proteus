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
        string? effectsFolder)
    {
        if (gearOverlays.Count == 0) return null;

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
        var bodies = new List<byte[]>();
        foreach (var part in Parts)
        {
            var bodyGamePath = $"chara/equipment/e0000/model/c{charCode}e0000_{part}.mdl";
            var bodyDisk = penumbra.ResolvePlayer(bodyGamePath);
            if (bodyDisk == null || !File.Exists(bodyDisk))
            {
                log.Debug("[Proteus] second skin: {0} not drawn, skipping", bodyGamePath);
                continue;
            }
            try { bodies.Add(File.ReadAllBytes(bodyDisk)); }
            catch (Exception ex) { log.Error(ex, "[Proteus] second skin: cannot read {0}", bodyDisk); }
        }
        if (bodies.Count == 0)
        {
            log.Warning("[Proteus] second skin: no skin models resolved for c{0}", charCode);
            return null;
        }

        // The shell inherits the body model's UVs, so ask THAT MODEL which material it uses — its suffix
        // names the UV space outright. Inferring from "whichever body material happens to be loaded" is
        // ambiguous (a character can have a vanilla _a material alongside a gen3 body) and picked wrong.
        var modelType = bodies.SelectMany(SecondSkinWriter.MaterialNames)
                              .Select(SecondSkinWriter.SkinMaterialBodyType)
                              .FirstOrDefault(t => t != null);
        if (modelType != null && !string.Equals(modelType, bodyType, StringComparison.OrdinalIgnoreCase))
        {
            log.Information("[Proteus] second skin: body UV is {0} per the model's material (was {1})",
                modelType, bodyType ?? "unknown");
            bodyType = modelType;
        }

        // Only a shell whose bytes actually differ from what's on disk needs a full redraw.
        bool shellChanged = false;

        var layers = new List<SecondSkinLayer>();
        for (int i = 0; i < gearOverlays.Count; i++)
        {
            var (entry, ov) = gearOverlays[i];
            var sidecarRoot = entry.SidecarRoot;

            string shader = ov.Descriptor.ShaderPackage;
            char letter = (char)('a' + i);   // one material per layer: _a, _b, _c ...
            string matName = $"mt_c{charCode}a{EmperorSetId:D4}_{Accessory}_{letter}.mtrl";
            string matGamePath = $"chara/accessory/a{EmperorSetId:D4}/material/v0001/{matName}";

            // Textures live under the accessory's own path, so they collide with nothing.
            string texPrefix = $"chara/accessory/a{EmperorSetId:D4}/texture/ss_{letter}_";

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
            var alpha = BuildAlpha(ov.Descriptor, entry, srcType, bodyType, TexSize, TexSize);
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
        try { shell = SecondSkinWriter.Build(bodies, layers, out stats); }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] second skin: model build failed");
            return null;
        }

        var mdlGamePath = $"chara/accessory/a{EmperorSetId:D4}/model/c{charCode}a{EmperorSetId:D4}_{Accessory}.mdl";
        var mdlDisk = Path.Combine(modelsDir, "secondskin.mdl");
        shellChanged |= WriteIfChanged(mdlDisk, shell);
        redirects[mdlGamePath] = Rel(outputRoot, mdlDisk);

        // Without an EQDP entry the ring falls back to another race's model, and the shell — which is
        // shaped like THIS character's body — would not line up.
        manipulations.Add(EqdpManipulation(charCode, EqdpSlot));

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
        OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType, int w, int h)
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
                int v = alpha[i] * wArr[i] / 255 + tArr![i];
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
            ["id"]   = index ?? Solid(0, 0, 0, 255),          // row 0
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
