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
        PenumbraBridge penumbra, TextureLoader textureLoader, SidecarDiscoveryService discovery, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.textureLoader = textureLoader;
        this.discovery = discovery;
        this.log = log;
    }

    /// <summary>Files to redirect, plus the metadata edits that make the shells load.</summary>
    public sealed record Result(Dictionary<string, string> Redirects, List<object> Manipulations);

    /// <summary>
    /// Build every gear shell for the character. <paramref name="charCode"/> is the human model code
    /// ("0201" = Midlander female). <paramref name="outputRoot"/> is the managed mod directory.
    /// Returns null when there is nothing to build.
    /// </summary>
    public Result? Build(
        string charCode,
        IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        string outputRoot)
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
            var alpha = BuildAlpha(ov.Descriptor, entry, TexSize, TexSize);
            var coverage = Downsample(alpha, TexSize, TexSize, CoverageSize);

            var texPaths = WriteTextures(ov.Descriptor, sidecarRoot, shader, texPrefix, texturesDir, redirects, letter, alpha);
            if (texPaths == null) continue;

            var template = textureLoader.LoadRawMtrl(null, GearMaterialWriter.TemplateFor(shader));
            if (template == null)
            {
                log.Error("[Proteus] second skin: missing template material for {0}", shader);
                continue;
            }

            byte[] mtrl;
            try { mtrl = GearMaterialWriter.Build(template, texPaths, BuildRows(ov.ColorTableRows)); }
            catch (Exception ex) { log.Error(ex, "[Proteus] second skin: material build failed for {0}", shader); continue; }

            var matDisk = Path.Combine(materialsDir, $"ss_{letter}.mtrl");
            File.WriteAllBytes(matDisk, mtrl);
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
        File.WriteAllBytes(mdlDisk, shell);
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

        return new Result(redirects, manipulations);
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
    private byte[]? BuildAlpha(OverlayDescriptor d, OverlayEntry entry, int w, int h)
    {
        var artPath = d.Diffuse ?? d.Normal ?? d.Mask;
        var masks = discovery.ResolveActiveMasks(entry);
        if (artPath == null && masks.Count == 0) return null;

        int n = w * h;
        var alpha = new byte[n];

        if (artPath != null)
        {
            var art = textureLoader.LoadPngAsRgba(Path.Combine(entry.SidecarRoot, artPath), w, h);
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
            var m = textureLoader.LoadPngAsRgba(masks[p], w, h);
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
        OverlayDescriptor d, string sidecarRoot, string shader, string texPrefix,
        string texturesDir, Dictionary<string, string> redirects, char letter, byte[]? alpha)
    {
        var outputRoot = Directory.GetParent(texturesDir)!.FullName;

        byte[]? Png(string? rel) =>
            rel == null ? null : textureLoader.LoadPngAsRgba(Path.Combine(sidecarRoot, rel), TexSize, TexSize);

        var diffuse = Png(d.Diffuse);
        var normal = Png(d.Normal);
        var mask = Png(d.Mask);
        var index = Png(d.Index);
        var scroll = Png(d.Scroll);

        byte[] Solid(byte r, byte g, byte b, byte a)
        {
            var t = new byte[TexSize * TexSize * 4];
            for (int i = 0; i < t.Length; i += 4) { t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a; }
            return t;
        }

        // norm: RG = the normal itself, B = TRANSPARENCY (the gear alpha gate), A = unused.
        // This is the whole trick: a Proteus overlay is gated by opacity, and on the gear layer that
        // opacity has to be translated into the normal map's BLUE channel or the shell renders solid.
        var norm = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
        for (int i = 0; i < TexSize * TexSize; i++)
            norm[i * 4 + 2] = alpha?[i] ?? 255;

        var slots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["norm"] = norm,
            ["mask"] = mask ?? Solid(128, 128, 128, 255),
            ["id"]   = index ?? Solid(0, 0, 0, 255),          // row 0
            ["base"] = diffuse ?? Solid(255, 255, 255, 255),  // colour tint comes from the color table
            ["catc"] = scroll ?? Solid(0, 0, 0, 255),         // black = no glow
        };

        var order = GearMaterialWriter.TextureOrder(shader);
        var paths = new List<string>(order.Count);
        foreach (var slot in order)
        {
            var gamePath = texPrefix + slot + ".tex";
            var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");
            if (!textureLoader.WriteTex(slots[slot], TexSize, TexSize, disk))
            {
                log.Error("[Proteus] second skin: failed to write {0}", disk);
                return null;
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
            rows[rowIndex] = new GearColorRow
            {
                Diffuse = rgb,
                Emissive = sub.Emissive > 0f && rgb is { } c
                    ? (c.R * sub.Emissive, c.G * sub.Emissive, c.B * sub.Emissive)
                    : null,
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
