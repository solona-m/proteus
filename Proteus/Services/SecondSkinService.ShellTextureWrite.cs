using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

public sealed partial class SecondSkinService
{
    private sealed class ShellTextureWrite
    {
        private readonly SecondSkinService service;
        private readonly OverlayEntry entry;
        private readonly OverlayDescriptor d;
        private readonly string shader;
        private readonly string texPrefix;
        private readonly string texturesDir;
        private readonly Dictionary<string, string> redirects;
        private readonly char letter;
        private byte[]? alpha;
        private readonly string? srcType;
        private readonly string? dstType;
        private readonly List<ColorTableRowPreset>? rows;
        private readonly string? effectsFolder;
        private readonly int texSize;
        private readonly bool mergeMasks;
        private readonly IReadOnlyList<byte[]>? siblingReliefs;
        private readonly IReadOnlyList<string>? templateTextures;
        private readonly DeferredShellNormal? deferNormal;
        private readonly List<(string MaskPath, string? NormalPath, string? IndexPath)>? maskAssets;
        private string outputRoot = null!;
        private byte[]? diffuse;
        private byte[]? normal;
        private byte[]? mask;
        private byte[]? index;
        private byte[]? scroll;
        private int scrollW;
        private int scrollH;
        private bool[]? idAuthored;
        private byte[]? shaderIndex;
        private bool skinShell;
        private byte[] norm = null!;

        public ShellTextureWrite(SecondSkinService service, OverlayEntry entry, OverlayDescriptor d, string shader, string texPrefix, string texturesDir, Dictionary<string, string> redirects, char letter, byte[]? alpha, string? srcType, string? dstType, List<ColorTableRowPreset>? rows, string? effectsFolder, int texSize, bool mergeMasks, IReadOnlyList<byte[]>? siblingReliefs, IReadOnlyList<string>? templateTextures, DeferredShellNormal? deferNormal, List<(string MaskPath, string? NormalPath, string? IndexPath)>? maskAssets)
        {
            this.service = service;
            this.entry = entry;
            this.d = d;
            this.shader = shader;
            this.texPrefix = texPrefix;
            this.texturesDir = texturesDir;
            this.redirects = redirects;
            this.letter = letter;
            this.alpha = alpha;
            this.srcType = srcType;
            this.dstType = dstType;
            this.rows = rows;
            this.effectsFolder = effectsFolder;
            this.texSize = texSize;
            this.mergeMasks = mergeMasks;
            this.siblingReliefs = siblingReliefs;
            this.templateTextures = templateTextures;
            this.deferNormal = deferNormal;
            this.maskAssets = maskAssets;
        }

        public List<string>? Run(ref bool texturesChanged)
        {
            LoadArt();
            MergeMasks();
            RepairRowSelector();
            ApplyRowOpacity();
            BuildNormal();
            return WriteSlots(ref texturesChanged);
        }

        private void LoadArt()
        {
            var sidecarRoot = entry.SidecarRoot;
            outputRoot = Directory.GetParent(texturesDir)!.FullName;

            byte[]? Png(string? rel, ResampleFilter filter = ResampleFilter.Auto)
                => service.LoadRemapped(rel, sidecarRoot, srcType, dstType, texSize, texSize, filter);

            diffuse = Png(d.Diffuse);
            normal = Png(d.Normal);
            mask = Png(d.Mask);
            // NEAREST, always: the index's red/green are discrete colour-table row selectors. See ResampleFilter.
            index = Png(d.Index, ResampleFilter.Nearest);

            // The scroll map is NOT body-UV art: a tiling pattern sampled with uv1, so never UV-remapped, resolved from the
            // effects folder, and kept at its OWN size.
            scroll = null;
            scrollW = texSize;
            scrollH = texSize;
            if (d.Scroll != null)
            {
                var effectPath = SidecarDiscoveryService.ResolveEffectPath(entry, effectsFolder, d.Scroll);
                if (effectPath != null)
                {
                    // Probe then request that exact size, through the decode cache, so the resample is a no-op.
                    var native = TextureLoader.ProbeSize(effectPath);
                    scrollW = native?.Width  ?? texSize;
                    scrollH = native?.Height ?? texSize;
                    scroll = service.textureLoader.LoadPngAsRgba(effectPath, scrollW, scrollH);
                }
                else
                    service.log.Warning("[Proteus] second skin: effect \"{0}\" not found", d.Scroll);
            }
            if (scroll == null) { scrollW = texSize; scrollH = texSize; }   // the flat fallback below is sheet-sized
        }

        private void MergeMasks()
        {
            // ── Proteus "Masks" options ──────────────────────────────────────────
            // A mask can ship its own row assignment (_id) and relief normal (_n); the skin layer merges both, so the gear
            // layer must too. Skipped when a dedicated mask shell owns them (mergeMasks=false).
            // _id merges bottom-first, so the TOP mask wins; relief goes through CombineMaskReliefs (top-first claim).
            // `idAuthored` records which texels a mask actually wrote, so the repair below can tell them from the invented
            // seed; null when the shell brought real _id art.
            idAuthored = null;
            var mergeTopFirst = mergeMasks
                ? maskAssets ?? service.discovery.ResolveActiveMaskAssets(entry)
                : new List<(string MaskPath, string? NormalPath, string? IndexPath)>();
            foreach (var (maskPath, maskNormalPath, maskIndexPath) in Enumerable.Reverse(mergeTopFirst))
            {
                if (maskIndexPath == null) continue;
                var maskPng = service.RemapPath(maskPath, srcType, dstType, texSize, texSize);
                // A mask's _id is an index map like any other — nearest, for the same reason.
                var maskIdx = service.RemapPath(maskIndexPath, srcType, dstType, texSize, texSize, ResampleFilter.Nearest);
                if (maskPng == null || maskIdx == null) continue;
                // LoadPngAsRgba hands back a shared cached array — clone before writing into it.
                if (index != null)
                {
                    index = (byte[])index.Clone();
                }
                else
                {
                    // Row 16 sub-row A (red 255 → pair 16, green 255 → A), matching the no-index fallback below and the skin layer's
                    // flat-tint fallback.
                    index = Solid(255, 255, 0, 255, texSize);
                    idAuthored = new bool[texSize * texSize];
                }
                var idxBuf = index; var mp = maskPng; var mi = maskIdx; var claimed = idAuthored;
                // Per texel, reading and writing only its own index, so partitioning is byte-identical.
                OverlayBlend.ParallelPixels(0, idxBuf.Length, 4, (from, to) =>
                {
                    for (int i = from; i < to; i += 4)
                    {
                        if (mp[i + 3] < 128) continue;    // only where the mask is actually present
                        idxBuf[i]     = mi[i];            // red   → row pair
                        idxBuf[i + 1] = mi[i + 1];        // green → sub-row
                        if (claimed != null) claimed[i >> 2] = true;
                    }
                });
            }

            // Mask relief: the same top-first claim-combine as the skin body normal (CombineMaskReliefs), so they can't drift.
            var reliefMasks = new List<(byte[] Relief, byte[] Coverage)>();
            foreach (var (maskPath, maskNormalPath, _) in mergeTopFirst)
            {
                if (maskNormalPath == null) continue;
                var maskPng    = service.RemapPath(maskPath, srcType, dstType, texSize, texSize);
                var maskNormal = service.RemapPath(maskNormalPath, srcType, dstType, texSize, texSize);
                if (maskPng != null && maskNormal != null)
                    reliefMasks.Add((maskNormal, maskPng));
            }
            if (reliefMasks.Count > 0)
            {
                normal = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255, texSize);
                OverlayBlend.CombineMaskReliefs(normal, texSize, texSize, reliefMasks);
            }

            // Sibling relief: ADDITIVELY fold each same-mod sibling's normal into this one (CompoundNormal), gated by the
            // sibling's coverage in its alpha lane. R/G only; blue stays this shell's coverage gate.
            if (siblingReliefs is { Count: > 0 })
            {
                normal = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255, texSize);
                foreach (var sib in siblingReliefs)
                    OverlayBlend.CompoundNormal(normal, sib, texSize, texSize);
            }
        }

        private void RepairRowSelector()
        {
            // ── row-selector repair ──────────────────────────────────────────────
            // Runs after every mask _id merge. Antialiased _id edges ramp through unconfigured rows, which the shader resolves
            // against the template colorset as a fringe (see OverlayBlend.SnapIndexRowsToDefined). The repair goes to a
            // SEPARATE buffer for the shader's "id" slot; the opacity pass keeps the unrepaired index so edges don't fatten.
            shaderIndex = index;
            if (index != null && rows is { Count: > 0 })
            {
                // LoadPngAsRgba's array is shared and the merge above clones only when it merged, so clone here.
                shaderIndex = (byte[])index.Clone();
                OverlayBlend.SnapIndexRowsToDefined(shaderIndex, texSize, texSize,
                    rows.Select(p => p.Row).ToList(), authored: idAuthored);
            }
        }

        private void ApplyRowOpacity()
        {
            // ── per-row opacity ──────────────────────────────────────────────────
            // Each color table row carries an Opacity (-100..100) and the index says which row a pixel uses, so opacity is
            // per-region. Same blend as the skin layer (OverlayBlend.ApplyIndexedOpacity).
            if (alpha != null && index != null && rows is { Count: > 0 })
            {
                // The rows' two opacities by 1-based pair, resolved once up front (the loop runs per texel). A separate `present`
                // flag, not a NaN sentinel, so a genuinely NaN Opacity is still applied.
                const int PairCount = 17;                       // pairs are 1..16; index 0 is unused
                var hasPreset = new bool[PairCount];
                var opAByPair = new float[PairCount];
                var opBByPair = new float[PairCount];
                foreach (var preset in rows)
                {
                    if (preset.Row < 1 || preset.Row >= PairCount) continue;
                    // FIRST match wins, as FirstOrDefault did; a later duplicate Row must not change the output.
                    if (hasPreset[preset.Row]) continue;
                    hasPreset[preset.Row] = true;
                    // Mirrored on a mask shell exactly as BuildRows mirrors the colour, so both halves of a preset agree on "unset".
                    var subA = preset.SubRowA ?? (d.IsMaskShell ? preset.SubRowB : null);
                    var subB = preset.SubRowB ?? (d.IsMaskShell ? preset.SubRowA : null);
                    opAByPair[preset.Row] = subA?.Opacity ?? 0;
                    opBByPair[preset.Row] = subB?.Opacity ?? 0;
                }

                var src = alpha;
                var dst = (byte[])alpha.Clone();
                var idx = index;
                var opAuthored = idAuthored;
                // Per texel, no carried state, so partitioning cannot change a byte.
                OverlayBlend.ParallelPixels(0, src.Length, 1, (from, to) =>
                {
                    for (int i = from; i < to; i++)
                    {
                        float a = src[i] / 255f;
                        if (a <= 0f) continue;
                        // A texel no mask ever wrote carries the synthesized row selector, so it must not pick up that row's opacity.
                        if (opAuthored != null && !opAuthored[i]) continue;

                        int pair = idx[i * 4] / 17 + 1;                     // red → 1-based row pair
                        if (pair < 1 || pair >= PairCount) continue;
                        if (!hasPreset[pair]) continue;                     // no preset for this row pair

                        float blendA = idx[i * 4 + 1] / 255f;               // green → sub-row A weight
                        float opA = opAByPair[pair];
                        float op = opBByPair[pair] + (opA - opBByPair[pair]) * blendA;
                        if (op == 0f) continue;

                        float newA = op < 0f ? a * (100f + op) / 100f : a + (1f - a) * op / 100f;
                        dst[i] = (byte)(Math.Clamp(newA, 0f, 1f) * 255f + 0.5f);
                    }
                });
                alpha = dst;
            }
        }

        private void BuildNormal()
        {
            // norm: RG = the normal, B = TRANSPARENCY (the gear alpha gate), A = unused. The material's transparency flag must
            // also be on (see GearMaterialWriter). NOT on a skin shell, where blue is skin-colour INFLUENCE; coverage there
            // comes from the triangle trim.
            skinShell = string.Equals(shader, OverlayDescriptor.SkinShader, StringComparison.OrdinalIgnoreCase);

            // The reinforced toe is NOT applied here: it is applied after the model writer places the cap and measures its real
            // footprint (see WriteDeferredNormals).

            norm = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255, texSize);
            if (!skinShell)
            {
                var nrm = norm; var al = alpha;
                OverlayBlend.ParallelPixels(0, texSize * texSize, 1, (from, to) =>
                {
                    for (int i = from; i < to; i++)
                        nrm[i * 4 + 2] = al?[i] ?? 255;   // blue is the gate; alpha is not used
                });
            }
        }

        private List<string>? WriteSlots(ref bool texturesChanged)
        {
            var slots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["norm"] = norm,
                // A fabricated mask must be WHITE on a GEAR shader: they read occlusion/gloss from it. A SKIN shell instead names
                // the TEMPLATE's own mask path, since white there maxes skin.shpk's lanes.
                ["mask"] = mask ?? (skinShell ? null! : Solid(255, 255, 255, 255, texSize)),
                // No index texture: select Row 16 sub-row A everywhere, matching the skin layer's fallback.
                ["id"]   = shaderIndex ?? Solid(255, 255, 0, 255, texSize),

                ["base"] = diffuse ?? Solid(255, 255, 255, 255, texSize),  // tint also comes from the color table
                ["catc"] = scroll ?? Solid(0, 0, 0, 255, texSize),         // black = no glow
            };

            // Every slot is the build's square sheet EXCEPT the scroll, a uv1-tiled pattern at its own resolution; slots in one
            // material may differ in size.
            (int W, int H) SizeOf(string slot)
                => string.Equals(slot, "catc", StringComparison.OrdinalIgnoreCase) ? (scrollW, scrollH)
                                                                                  : (texSize, texSize);

            var order = GearMaterialWriter.TextureOrder(shader);
            var paths = new List<string>(order.Count);
            bool compress = service.config.EnableCompression;
            for (int slotIdx = 0; slotIdx < order.Count; slotIdx++)
            {
                var slot = order[slotIdx];
                // A slot with no art and no sensible fabrication: name the TEMPLATE's own texture and write nothing (it resolves
                // to vanilla). Falls back to the shared body skin mask if the template named nothing.
                if (slots[slot] == null)
                {
                    var inherited = templateTextures != null && slotIdx < templateTextures.Count
                                 && !string.IsNullOrWhiteSpace(templateTextures[slotIdx])
                        ? templateTextures[slotIdx]
                        : VanillaSkinMask;
                    paths.Add(inherited);
                    continue;
                }

                var gamePath = texPrefix + slot + ".tex";
                var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");
                var (sw, sh) = SizeOf(slot);

                // The reinforced toe needs the NORMAL held back: its region, the placed cap's footprint, only exists after the
                // model writer runs. The path and redirect are fixed per letter, so only the bytes wait. Not on a skin shell.
                if (deferNormal != null && !skinShell && string.Equals(slot, "norm", StringComparison.OrdinalIgnoreCase))
                {
                    deferNormal.Norm = slots[slot];
                    deferNormal.GamePath = gamePath;
                    deferNormal.TexturesDir = texturesDir;
                    deferNormal.OutputRoot = outputRoot;
                    deferNormal.Letter = letter;
                    deferNormal.Size = sw;
                    // The game path goes into the material now; the redirect waits, because the file is content-addressed.
                    paths.Add(gamePath);
                    continue;
                }

                if (!service.WriteShellSlot(slot, slots[slot], sw, sh, disk, compress, ref texturesChanged))
                    return null;

                redirects[gamePath] = Rel(outputRoot, disk);
                paths.Add(gamePath);
            }
            return paths;
        }
    }
}
