using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// How a created overlay glows, if at all. Skin cannot emit, so anything but <see cref="None"/> makes the overlay a
/// gear shell; only <c>character.shpk</c> takes a base texture, so only it shows the art in daylight.
/// </summary>
public enum GlowStyle
{
    /// <summary>An ordinary overlay painted into the skin.</summary>
    None,

    /// <summary>
    /// The art is there by day and glows day and night — <c>character.shpk</c>, where the shell's base
    /// texture is the art and the row emissive adds a flat tint on top.
    /// </summary>
    Always,

    /// <summary>
    /// Nothing in daylight, glowing in an unlit room: <c>characterscroll.shpk</c> with a generated scroll map and a
    /// full light response.
    /// </summary>
    DarkOnly,
}

/// <summary>
/// Builds a basic Proteus overlay mod from the Create tab: a Penumbra mod folder with a <c>Proteus/metadata.json</c>
/// sidecar holding one overlay, registered with Penumbra and opened in its UI. Compositing happens later.
/// </summary>
public sealed class ModCreationService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly Configuration config;
    private readonly TextureLoader textureLoader;
    private readonly IPluginLog log;

    /// <summary>Common target when nothing is detected: the Bibo+ Midlander female body skin material.</summary>
    public const string DefaultBodyMaterial =
        "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";

    /// <summary>
    /// A harmless self-swap of a vanilla monster material the player never loads, so Penumbra does not flag the
    /// redirect-free mod as "changes nothing".
    /// </summary>
    internal const string DummySwapPath =
        "chara/monster/m8030/obj/body/b0001/material/v0001/mt_m8030b0001_a.mtrl";

    public ModCreationService(PenumbraBridge penumbra, CompositorService compositor, Configuration config,
        TextureLoader textureLoader, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.config = config;
        this.textureLoader = textureLoader;
        this.log = log;
    }

    public readonly record struct CreateResult(bool Ok, string Message);

    /// <summary>
    /// The player's currently-loaded body skin material, or null when nothing is detected (player not drawn yet).
    /// </summary>
    public string? DetectBodyMaterial()
    {
        var loaded = penumbra.GetActivePlayerMaterialPaths();
        if (loaded == null) return null;

        var bodyMats = RankBodyMaterials(loaded);
        var chosen = bodyMats.FirstOrDefault();

        // Only log a resolved one: the Create tab polls this every frame until the character is drawn.
        if (chosen != null)
            log.Information("[Proteus] create: body materials [{0}] -> {1}", string.Join(", ", bodyMats), chosen);
        return chosen;
    }

    /// <summary>
    /// The body material from the last known snapshot, a placeholder while the character isn't drawn. Separate from
    /// <see cref="DetectBodyMaterial"/> because a non-null detect stops polling; the caller must keep polling.
    /// </summary>
    public string? CachedBodyMaterial()
    {
        var cached = config.CachedActiveMaterialPaths;   // reference-swapped off-thread: read once
        return cached == null ? null : RankBodyMaterials(cached).FirstOrDefault();
    }

    /// <summary>
    /// Body materials out of a set of loaded paths, best candidate first: modded bodies rank above a vanilla (gen2)
    /// skin material that gear can carry along.
    /// </summary>
    private static List<string> RankBodyMaterials(IEnumerable<string> loaded)
        => loaded
            .Where(p => p.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)
                     && p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)
                     && UVRemapService.InferBodyType(p) != null)
            .OrderBy(p => UVRemapService.InferBodyType(p) == "gen2" ? 1 : 0)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Every material the player currently has loaded, for the Create tab's picker. <paramref name="fromCache"/> is
    /// true when this fell back to the last known set. The live query costs several ms on the framework thread:
    /// call on a user action, never per frame.
    /// </summary>
    public IReadOnlyList<string> ListActiveMaterials(out bool fromCache)
    {
        var live = penumbra.GetActivePlayerMaterialPaths();
        fromCache = live == null;

        // The cached list is reference-swapped across threads without a lock, so read it into a local once.
        IEnumerable<string> src = live ?? (IEnumerable<string>?)config.CachedActiveMaterialPaths ?? [];

        // Filtering and de-duplication matter only for the cached List; the live set is already a filtered HashSet.
        return src
            .Where(p => p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Which texture slots a material declares: Penumbra-resolved disk file first, game SqPack second. An all-null
    /// result means the material could not be read, not that it has no textures; callers must fail open.
    /// Skin materials never declare <see cref="MtrlTexturePaths.Index"/>. Blocking I/O: never call per frame.
    /// </summary>
    public MtrlTexturePaths ResolveMaterialSlots(string materialGamePath)
    {
        if (string.IsNullOrWhiteSpace(materialGamePath)) return new MtrlTexturePaths(null, null, null);
        // Raw parse, not Lumina's: Lumina misreads some Dawntrail layouts.
        return textureLoader.ResolveMtrlTexturesRaw(penumbra.ResolvePlayer(materialGamePath), materialGamePath);
    }

    /// <summary>
    /// Whether the picked art is a doubled face sheet (the two sides of the head in two halves), judged by its aspect
    /// against the target face's native texture. Anything it cannot measure answers no: a wrong yes mangles the face.
    /// </summary>
    public bool LooksLikeDoubledFaceSheet(string materialTarget, string artPath)
    {
        try
        {
            if (TextureLoader.ProbeSize(artPath) is not { } art) return false;

            // The material's own texture as the reference layout; every slot on one face shares its proportions.
            var slots = ResolveMaterialSlots(materialTarget);
            var slot = slots.Diffuse ?? slots.Normal ?? slots.Mask;
            if (slot == null) return false;

            // Never measure our own output: a doubled sheet published there would read as the native shape.
            // The native layout is a property of the race, so game data is the right fallback.
            var disk = penumbra.ResolvePlayer(slot);
            if (IsOwnOutput(disk)) disk = null;
            if (textureLoader.BaseNativeSize(disk, slot) is not { } native) return false;

            return IsDoubledAspect(art.Width, art.Height, native.Width, native.Height);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] could not judge whether {0} is a doubled face sheet", artPath);
            return false;
        }
    }

    /// <summary>
    /// Whether a resolved disk path is a file Proteus itself published into the managed mod. A cheap prefix test;
    /// an unanswerable path reads as not ours.
    /// </summary>
    private bool IsOwnOutput(string? diskPath)
    {
        if (string.IsNullOrEmpty(diskPath)) return false;
        var root = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(root)) return false;

        var managed = Path.Combine(root, SidecarDiscoveryService.ManagedModDir)
                          .Replace('/', Path.DirectorySeparatorChar)
                          .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return diskPath.Replace('/', Path.DirectorySeparatorChar)
                       .StartsWith(managed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is <paramref name="artW"/>×<paramref name="artH"/> twice as wide as a <paramref name="nativeW"/>×
    /// <paramref name="nativeH"/> sheet, proportionally? Native layout scores 1, doubled 2; the 1.5–2.5 window
    /// absorbs power-of-two rounding.
    /// </summary>
    internal static bool IsDoubledAspect(int artW, int artH, int nativeW, int nativeH)
    {
        if (artW <= 0 || artH <= 0 || nativeW <= 0 || nativeH <= 0) return false;
        double ratio = ((double)artW / artH) * ((double)nativeH / nativeW);
        return ratio is >= 1.5 and <= 2.5;
    }

    /// <summary>Alpha at or above this reads as opaque. Not 255: lossy exports land a few counts short.</summary>
    private const byte OpaqueAlpha = 250;

    /// <summary>How much of the image must be opaque to count as full coverage. Not 1.0, so a stray
    /// feathered pixel at a UV island's edge can't veto the whole verdict.</summary>
    private const float FullCoverageFraction = 0.99f;

    /// <summary>
    /// Side length the probe samples at. Point-sampled, so a sparse overlay's holes stay holes.
    /// </summary>
    private const int CoverageProbeSize = 256;

    /// <summary>
    /// Mean per-channel difference, 0–255, below which the picked art is judged to be the material's skin rather
    /// than something painted onto it. Compared without mean-centring, which would make flat fabric match anything.
    /// </summary>
    /// <remarks>internal so the test asserts against this number rather than a copy of it.</remarks>
    internal const float SkinLikenessMad = 28f;

    /// <summary>
    /// Whether the picked textures look like a whole skin rather than something painted onto skin — the Create tab's
    /// default for <see cref="OverlayDescriptor.NormalMode"/>. Requires a colour map and a normal, full opacity, and
    /// resemblance to the target's current diffuse (opacity alone is not enough: garment art is often fully opaque).
    /// Decodes two images, so call it off the frame thread.
    /// </summary>
    public bool LooksLikeWholeSkin(string materialTarget, string? diffuseSrc, string? normalSrc)
    {
        if (string.IsNullOrWhiteSpace(diffuseSrc) || string.IsNullOrWhiteSpace(normalSrc)) return false;
        try
        {
            const int texels = CoverageProbeSize * CoverageProbeSize;

            var overlay = textureLoader.LoadPngAsRgba(diffuseSrc!, CoverageProbeSize, CoverageProbeSize);
            if (!IsFullCoverage(overlay, texels)) return false;

            // The material's current diffuse; an unresolvable hand-typed path answers "no".
            var slot = ResolveMaterialSlots(materialTarget).Diffuse;
            if (slot == null) return false;
            var loaded = textureLoader.LoadBaseTexture(penumbra.ResolvePlayer(slot), slot);
            if (loaded is not { } b || b.rgba.Length == 0) return false;

            var skin = b.width == CoverageProbeSize && b.height == CoverageProbeSize
                ? b.rgba
                : textureLoader.ScaleRgba(b.rgba, b.width, b.height, CoverageProbeSize, CoverageProbeSize);

            return MeanAbsDifference(overlay, skin, texels) <= SkinLikenessMad;
        }
        catch (Exception ex)
        {
            // Runs on a pool thread; the safe default is "not a whole skin", so log it.
            log.Warning(ex, "[Proteus] whole-skin probe failed for {0} — assuming it isn't one", diffuseSrc);
            return false;
        }
    }

    // ── glow ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The colour-table row a glowing overlay writes. An overlay with no <c>_id</c> art gets a fabricated index of
    /// (255, 255, 0), which selects only row pair 16 sub-row A.
    /// </summary>
    internal const int GlowRow = 16;

    /// <summary>
    /// The surface under a dark-only glow: black. <c>characterscroll.shpk</c> has no base texture, so this colour is
    /// the whole lit surface; anything above black reads as a charcoal patch.
    /// </summary>
    internal const string DarkOnlySurface = "#000000";

    /// <summary>
    /// The emissive an always-glow row carries. Very low: on <c>character.shpk</c> it is a flat additive tint that
    /// clips highlights in daylight and still reads as a glow in the dark.
    /// </summary>
    internal const float AlwaysGlowEmissive = 0.08f;

    /// <summary>
    /// The art's own alpha-weighted average colour as <c>#RRGGBB</c>, or null when nothing is covered. An always-glow
    /// row emits in this rather than white, which would desaturate the art.
    /// </summary>
    internal static string? AverageArtColour(byte[] rgba)
    {
        long r = 0, g = 0, b = 0, weight = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 0) continue;
            r += rgba[i] * a;
            g += rgba[i + 1] * a;
            b += rgba[i + 2] * a;
            weight += a;
        }
        if (weight == 0) return null;
        return $"#{r / weight:X2}{g / weight:X2}{b / weight:X2}";
    }

    /// <summary>
    /// Turn a decoded overlay into the scroll map a dark-only glow emits: the art's colour multiplied by its coverage,
    /// on black, fully opaque (a transparent scroll map renders nothing).
    /// </summary>
    internal static byte[] BuildScrollMap(byte[] rgba)
    {
        var scroll = new byte[rgba.Length];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            scroll[i]     = (byte)((rgba[i]     * a + 127) / 255);
            scroll[i + 1] = (byte)((rgba[i + 1] * a + 127) / 255);
            scroll[i + 2] = (byte)((rgba[i + 2] * a + 127) / 255);
            scroll[i + 3] = 255;
        }
        return scroll;
    }

    /// <summary>
    /// The colour-table row for a glow style, or null when the overlay doesn't glow. <see cref="GlowStyle.Always"/>
    /// carries no light response; <see cref="AlwaysGlowEmissive"/> and <see cref="AverageArtColour"/> keep it from
    /// bleaching the art.
    /// </summary>
    internal static ColorTableRowPreset? GlowRowFor(GlowStyle style, string? artColour = null) => style switch
    {
        GlowStyle.Always => new ColorTableRowPreset
        {
            Row = GlowRow,
            SubRowA = new ColorTableSubRowPreset
            {
                // White: the shell's base texture is the art, and this row multiplies it.
                Diffuse       = "#FFFFFF",
                Emissive      = AlwaysGlowEmissive,
                // The art's own average (see AverageArtColour); white only when the art could not be read.
                EmissiveColor = artColour ?? RenderModeInference.GlowEmissiveColour,
            },
        },
        GlowStyle.DarkOnly => new ColorTableRowPreset
        {
            Row = GlowRow,
            SubRowA = new ColorTableSubRowPreset
            {
                Diffuse       = DarkOnlySurface,
                Emissive      = RenderModeInference.GlowEmissive,
                // Neutral: the scroll map carries the art's hue.
                EmissiveColor = RenderModeInference.GlowEmissiveColour,
                // Dark-only, both halves: the surface fades with the glow, leaving no black silhouette.
                LightResponse = 1f,
                HideInLight   = true,
            },
        },
        _ => null,
    };

    /// <summary>The coverage verdict on a decoded RGBA buffer. Split from the load so it can be exercised
    /// offline: a null or short buffer is a failed decode, which reads as "not full coverage".</summary>
    internal static bool IsFullCoverage(byte[]? rgba, int texels)
    {
        if (rgba == null || texels <= 0 || rgba.Length < texels * 4) return false;

        int opaque = 0;
        for (int i = 3; i < texels * 4; i += 4)
            if (rgba[i] >= OpaqueAlpha) opaque++;
        return opaque >= texels * FullCoverageFraction;
    }

    /// <summary>
    /// Mean absolute per-channel RGB difference between two same-sized RGBA buffers, alpha ignored.
    /// <see cref="float.MaxValue"/> when either is missing or short — "as unlike as possible", so a failed
    /// decode can never read as a match. Split from the loads so it can be exercised offline.
    /// </summary>
    internal static float MeanAbsDifference(byte[]? a, byte[]? b, int texels)
    {
        if (a == null || b == null || texels <= 0 || a.Length < texels * 4 || b.Length < texels * 4)
            return float.MaxValue;

        long sum = 0;
        for (int i = 0; i < texels * 4; i += 4)
        {
            sum += Math.Abs(a[i]     - b[i]);
            sum += Math.Abs(a[i + 1] - b[i + 1]);
            sum += Math.Abs(a[i + 2] - b[i + 2]);
        }
        return (float)sum / (texels * 3);
    }

    /// <summary>
    /// Create the mod on disk, register it with Penumbra, and open the Penumbra UI to it. Returns a
    /// user-facing result; nothing is written when validation fails.
    /// </summary>
    /// <param name="wholeSkin">
    /// The textures are the skin: the normal replaces the material's (<see cref="NormalMode.Replace"/>) and skin-tint
    /// suppression is off, so the wearer's tone reaches the art.
    /// </param>
    /// <param name="faceSplit">
    /// The picked face texture is a doubled sheet, recorded as the overlay's source UV space so the compositor can
    /// un-mirror a face shell onto it.
    /// </param>
    /// <param name="glow">
    /// Whether the art glows, and how. <see cref="GlowStyle.DarkOnly"/>'s scroll map is derived from the diffuse here,
    /// keeping <see cref="WriteMod"/> service-free.
    /// </param>
    public CreateResult Create(
        string modName, string author, string materialTarget,
        string? diffuseSrc, string? maskSrc, string? normalSrc, string? indexSrc,
        bool wholeSkin = false, bool faceSplit = false, GlowStyle glow = GlowStyle.None)
    {
        modName = (modName ?? "").Trim();
        author = (author ?? "").Trim();
        materialTarget = (materialTarget ?? "").Trim();

        if (string.IsNullOrWhiteSpace(modName))
            return new(false, Loc.Localize("Create.Error.NoName", "Enter a mod name."));
        if (string.IsNullOrWhiteSpace(materialTarget))
            return new(false, Loc.Localize("Create.Error.NoMaterial", "Enter a material target."));

        // The label is translated, since it appears inside a translated message.
        var cs = Localization.Strings.Create;
        var sources = new (string label, string? src)[]
            { (cs.SlotDiffuse, diffuseSrc), (cs.SlotMask, maskSrc), (cs.SlotNormal, normalSrc), (cs.SlotIndex, indexSrc) };
        if (!sources.Any(s => !string.IsNullOrWhiteSpace(s.src)))
            return new(false, Loc.Localize("Create.Error.NoTexture",
                "Pick at least one texture (diffuse, mask, normal or index)."));
        foreach (var (label, src) in sources)
            if (!string.IsNullOrWhiteSpace(src) && !File.Exists(src))
                return new(false, string.Format(
                    Loc.Localize("Create.Error.MissingFile.Fmt", "The {0} file no longer exists: {1}"), label, src));

        // A glow's colour comes from the art, so there has to be art.
        if (glow != GlowStyle.None && string.IsNullOrWhiteSpace(diffuseSrc))
            return new(false, Loc.Localize("Create.Error.GlowNeedsDiffuse",
                "A glowing overlay takes its colour from the art, so it needs a diffuse texture."));

        var dirName = Sanitize(modName);
        if (dirName == null)
            return new(false, Loc.Localize("Create.Error.UnusableName",
                "That mod name has no usable characters — use letters or numbers."));
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return new(false, Loc.Localize("Create.Error.ReservedName",
                "\"Proteus\" is reserved — choose a different mod name."));

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return new(false, Loc.Localize("Service.NoPenumbraDir", "Penumbra's mod directory isn't available."));

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return new(false, string.Format(
                Loc.Localize("Create.Error.FolderExists.Fmt", "A mod folder named \"{0}\" already exists."), dirName));

        // Both glow styles decode the art here so WriteMod stays service-free. A failed decode is reported.
        byte[]? scrollRgba = null;
        int scrollW = 0, scrollH = 0;
        string? artColour = null;
        if (glow != GlowStyle.None)
        {
            if (textureLoader.LoadImageAsRgba(diffuseSrc!) is not { } src)
                return new(false, string.Format(Loc.Localize("Create.Error.GlowDecodeFailed.Fmt",
                    "Couldn't read {0} to build the glow. Try a PNG."), Path.GetFileName(diffuseSrc)));

            if (glow == GlowStyle.DarkOnly)
            {
                scrollRgba = BuildScrollMap(src.rgba);
                scrollW = src.width;
                scrollH = src.height;
            }
            else
            {
                artColour = AverageArtColour(src.rgba);
            }
        }

        try
        {
            WriteMod(root, modName, author, materialTarget, diffuseSrc, maskSrc, normalSrc, indexSrc, wholeSkin,
                     faceSplit, glow, scrollRgba, scrollW, scrollH, artColour);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] create mod failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return new(false, string.Format(
                Loc.Localize("Create.Error.WriteFailed.Fmt", "Failed to write the mod: {0}"), ex.Message));
        }

        var ec = penumbra.AddModDirectory(dirName);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] AddMod({0}) -> {1}", dirName, ec);
            // Roll the folder back so the name is free to retry.
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return new(false, string.Format(Loc.Localize("Service.RegisterFailed.Fmt",
                "Wrote the mod, but Penumbra couldn't register it ({0}). Rescan mods in Penumbra."), ec));
        }

        // Enabling is left to Pump, across frames: AddMod is asynchronous, and a settings write that lands while
        // Penumbra is still building the mod is discarded. Opened here, since Pump only runs while the tab is shown.
        penumbra.OpenToMod(dirName);

        _pending = new Pending(dirName, modName, materialTarget, Environment.TickCount64 + ActivateTimeoutMs);
        _nextAttempt = 0;
        return Pump() ?? new(true, string.Format(Loc.Localize("Create.Registering.Fmt",
            "Created \"{0}\" — waiting for Penumbra to finish loading it…"), modName));
    }

    /// <summary>A registration Penumbra has not finished loading yet.</summary>
    private sealed record Pending(string DirName, string ModName, string MaterialTarget, long Deadline);

    private Pending? _pending;
    private long _nextAttempt;

    /// <summary>How often to re-ask while waiting, so Penumbra IPC is not called every frame.</summary>
    private const long AttemptIntervalMs = 250;

    /// <summary>How long to keep asking before giving up and saying so.</summary>
    private const long ActivateTimeoutMs = 15_000;

    /// <summary>Whether a created mod is still waiting on Penumbra, so the tab keeps pumping.</summary>
    public bool IsAwaiting => _pending != null;

    /// <summary>
    /// Continue a registration <see cref="Create"/> left pending, at most one Penumbra call per frame.
    /// Null while Penumbra is still loading the mod; the final result once it answers or the wait runs out.
    /// Harmless to call with nothing pending.
    /// </summary>
    public CreateResult? Pump()
    {
        if (_pending is not { } p) return null;

        var now = Environment.TickCount64;
        if (now < _nextAttempt) return null;
        _nextAttempt = now + AttemptIntervalMs;

        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null)
        {
            // Not a reason to wait: no collection is a standing state, not a loading one.
            log.Warning("[Proteus] created {0}: no player collection — enable it manually", p.DirName);
            return Finish(false);
        }

        // Ask, then read back: a discarded write still reports Success.
        penumbra.SetModEnabled(collId.Value, p.DirName, true);
        if (penumbra.GetModSettings(collId.Value, p.DirName) is { Enabled: true })
            return Finish(true);

        if (now < p.Deadline) return null;   // still settling — ask again shortly

        log.Warning("[Proteus] created {0}: Penumbra would not report the mod as enabled within {1}ms",
            p.DirName, ActivateTimeoutMs);
        return Finish(false);
    }

    /// <summary>
    /// Recomposite and report. <paramref name="enabled"/> false means the mod is registered but switched off — a
    /// warning, not a success. The recomposite only runs when enabled.
    /// </summary>
    private CreateResult Finish(bool enabled)
    {
        var p = _pending!;
        _pending = null;

        if (enabled) compositor.TriggerRecomposite("mod-created");
        log.Information("[Proteus] created mod {0} ({1}), enabled={2}", p.DirName, p.MaterialTarget, enabled);

        return enabled
            ? new(true, string.Format(Loc.Localize("Create.Ok.Fmt",
                "Created \"{0}\", enabled it, and opened it in Penumbra."), p.ModName))
            : new(false, string.Format(Loc.Localize("Create.Ok.NotEnabled.Fmt",
                "Created \"{0}\" and opened it in Penumbra, but couldn't switch it on — enable it there."),
                p.ModName));
    }

    /// <summary>
    /// Write the mod files under <paramref name="root"/>: the texture copies, the Proteus sidecar, and Penumbra's
    /// manifests. Pure filesystem work, no IPC.
    /// </summary>
    /// <param name="faceSplit">
    /// The picked face texture is a doubled sheet, so the overlay declares that layout. Wrong on any other texture.
    /// </param>
    /// <param name="scrollRgba">
    /// The already-built scroll map for <see cref="GlowStyle.DarkOnly"/>, at <paramref name="scrollW"/> ×
    /// <paramref name="scrollH"/>. Null for every other style; a null one under DarkOnly leaves the effect unwritten.
    /// </param>
    internal static void WriteMod(
        string root, string modName, string author, string materialTarget,
        string? diffuseSrc, string? maskSrc, string? normalSrc, string? indexSrc,
        bool wholeSkin = false, bool faceSplit = false, GlowStyle glow = GlowStyle.None,
        byte[]? scrollRgba = null, int scrollW = 0, int scrollH = 0, string? artColour = null)
    {
        var overlaysDir = Path.Combine(root, "Proteus", "overlays");
        Directory.CreateDirectory(overlaysDir);

        // Copy each source into overlays/{slot}{ext}, keeping the lower-cased extension; returns the sidecar-relative path.
        string? Copy(string slot, string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;
            var ext = Path.GetExtension(src).ToLowerInvariant();
            var name = slot + ext;
            File.Copy(src, Path.Combine(overlaysDir, name), overwrite: true);
            return "overlays/" + name;
        }

        // A dark-only glow's scroll map is named in the descriptor by bare file name, which
        // SidecarDiscoveryService.ResolveEffectPath looks up in Proteus/Effects/.
        string? scrollFile = null;
        if (glow == GlowStyle.DarkOnly && scrollRgba is { Length: > 0 } && scrollW > 0 && scrollH > 0)
        {
            var effectsDir = Path.Combine(root, SidecarDiscoveryService.ManagedModDir,
                                          SidecarDiscoveryService.EffectsSubdir);
            Directory.CreateDirectory(effectsDir);
            scrollFile = "glow.png";
            using var stream = File.Create(Path.Combine(effectsDir, scrollFile));
            new StbImageWriteSharp.ImageWriter().WritePng(
                scrollRgba, scrollW, scrollH, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        }

        var descriptor = new OverlayDescriptor
        {
            // Skin cannot emit, so a glow is a gear shell. Shader is stated too: promotion alone moves only the
            // layer, and plain character.shpk has no scroll map.
            Layer = glow == GlowStyle.None ? OverlayLayer.Skin : OverlayLayer.Gear,
            Shader = glow switch
            {
                GlowStyle.Always   => OverlayDescriptor.DefaultGearShader,
                GlowStyle.DarkOnly => RenderModeInference.GlowShader,
                _                  => null,
            },
            Scroll = scrollFile,
            // Zero speed and unit tiling, explicitly: the map is the body sheet, and the default speed would slide it.
            ScrollSpeedX  = scrollFile == null ? null : 0f,
            ScrollSpeedY  = scrollFile == null ? null : 0f,
            ScrollTilingX = scrollFile == null ? null : 1f,
            ScrollTilingY = scrollFile == null ? null : 1f,
            MaterialGamePaths = [materialTarget],
            Diffuse = Copy("diffuse", diffuseSrc),
            Mask = Copy("mask", maskSrc),
            Normal = Copy("normal", normalSrc),
            Index = Copy("index", indexSrc),
            NormalMode = wholeSkin ? NormalMode.Replace : NormalMode.Compound,
            // Doubled face art declares the doubled face layout; an ordinary face texture must not.
            SourceBodyType = faceSplit ? UVRemapService.FaceSplitSpace : null,
            // The tick is also the declaration of one-sidedness: without AsymmetricArt,
            // CompositorService.NeedsUnmirroredShell gives a doubled sheet no face shell.
            AsymmetricArt = faceSplit ? true : null,
            // A whole skin wants the wearer's tone on it, so skin-tint suppression is off; null keeps the default.
            SkinToneMask = wholeSkin ? 0f : null,
        };
        var metadata = new ProteusMetadata
        {
            FormatVersion = 1,
            Name = modName,
            Author = author,
            Overlays = [descriptor],
            // Top-level rows are safe here only: they are inherited by every option, and a Create-tab mod has one
            // overlay and no option groups for an emissive to promote.
            ColorTableRows = GlowRowFor(glow, artColour) is { } row ? [row] : null,
        };

        var metaJson = JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite);
        // AtomicWrite: a zero-filled descriptor leaves a mod that does nothing in Proteus.
        PenumbraModMeta.AtomicWrite(Path.Combine(root, "Proteus", "metadata.json"), metaJson);

        // Penumbra's manifest, matching CompositorService.EnsureManagedModExists. Must precede the redirects:
        // PenumbraModMeta refuses to write into a folder without one (it reads as pre-v4).
        PenumbraModMeta.AtomicWrite(
            Path.Combine(root, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(modName, author, "Created for Proteus."));

        // A no-op self-swap so Penumbra does not flag the empty default option as "changes nothing". See DummySwapPath.
        PenumbraModMeta.WriteRedirects(
            root, modName,
            files: new Dictionary<string, string>(),
            swaps: new Dictionary<string, string> { [DummySwapPath] = DummySwapPath });
    }

    /// <summary>
    /// A Penumbra mod directory name derived from the mod name: keep letters, digits, space, dash and
    /// underscore; collapse runs of whitespace; trim. Null when nothing usable remains.
    /// </summary>
    internal static string? Sanitize(string modName)
    {
        if (string.IsNullOrWhiteSpace(modName)) return null;
        var sb = new StringBuilder(modName.Length);
        bool lastSpace = false;
        foreach (var c in modName.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (char.IsWhiteSpace(c) || c == ' ')
            {
                if (sb.Length > 0 && !lastSpace) { sb.Append(' '); lastSpace = true; }
            }
            // everything else (slashes, dots, punctuation) is dropped
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }
}
