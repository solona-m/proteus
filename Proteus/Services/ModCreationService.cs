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
/// How a created overlay glows, if at all — the Create tab's "Make this art glow" checkbox and the choice
/// under it.
/// <para/>
/// Skin cannot emit, so anything but <see cref="None"/> makes the overlay a gear shell. The two lit styles
/// differ in which shader that shell runs, and that difference decides everything else: only
/// <c>character.shpk</c> takes a base texture, so only it can show the art itself in daylight.
/// </summary>
public enum GlowStyle
{
    /// <summary>An ordinary overlay painted into the skin. What the Create tab has always made.</summary>
    None,

    /// <summary>
    /// The art is there by day and glows day and night — <c>character.shpk</c>, where the shell's base
    /// texture is the art and the row emissive adds a flat tint on top.
    /// </summary>
    Always,

    /// <summary>
    /// Nothing in daylight, glowing in an unlit room — the Atramentum Luminis behaviour, on
    /// <c>characterscroll.shpk</c> with a generated scroll map and a full light response.
    /// </summary>
    DarkOnly,
}

/// <summary>
/// Builds a basic Proteus overlay mod from the Create tab: a Penumbra mod folder carrying a
/// <c>Proteus/metadata.json</c> sidecar with one Skin-layer overlay, registered with Penumbra and opened
/// in its UI. The heavy lifting (compositing) is done later by <see cref="CompositorService"/>; this only
/// writes the source mod so the user can enable and tweak it.
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
    /// A harmless self-swap so Penumbra registers the mod as having content (it otherwise flags a
    /// redirect-free mod as "changes nothing"). A vanilla MONSTER body material the player never loads —
    /// swapping it to itself is a guaranteed no-op that can't touch the character. Matches the community
    /// "Panties for Proteus" template rather than self-swapping the target body material (which for bibo/
    /// gen3 is a modded, non-vanilla path).
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
    /// The player's currently-loaded body skin material, or null when nothing is detected (player not
    /// drawn yet). Used to pre-fill the Create tab's material target so a basic mod paints on the right
    /// body without the user knowing the path.
    /// </summary>
    public string? DetectBodyMaterial()
    {
        var loaded = penumbra.GetActivePlayerMaterialPaths();
        if (loaded == null) return null;

        var bodyMats = RankBodyMaterials(loaded);
        var chosen = bodyMats.FirstOrDefault();

        // Only when we actually resolve one — the Create tab polls this until the character is drawn, so
        // logging the empty case every frame would flood the log.
        if (chosen != null)
            log.Information("[Proteus] create: body materials [{0}] -> {1}", string.Join(", ", bodyMats), chosen);
        return chosen;
    }

    /// <summary>
    /// The body material from the LAST KNOWN snapshot — a placeholder for the Create tab while the
    /// character isn't drawn, in place of the hardcoded <see cref="DefaultBodyMaterial"/> (a Bibo+
    /// Midlander path that may be nothing like the user's body).
    /// <para/>
    /// Deliberately separate from <see cref="DetectBodyMaterial"/> rather than folded in as a fallback:
    /// the Create tab treats a non-null detect as authoritative and stops polling, so answering from the
    /// cache there would lock in a possibly-stale body and never pick up the real one. The caller must
    /// keep polling after using this.
    /// </summary>
    public string? CachedBodyMaterial()
    {
        var cached = config.CachedActiveMaterialPaths;   // reference-swapped off-thread: read once
        return cached == null ? null : RankBodyMaterials(cached).FirstOrDefault();
    }

    /// <summary>
    /// Body materials out of a set of loaded paths, best candidate first.
    /// <para/>
    /// A character usually has ONE real body, but a vanilla (gen2) skin material can ride along — gear that
    /// exposes skin carries its own mt_…b….a.mtrl. If the wearer is on bibo/gen3, that vanilla one is NOT
    /// the body they want the overlay on, so rank the modded bodies first.
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
    /// Every material the player currently has loaded — the Create tab's picker lists these so the author
    /// can select a target instead of typing a 60–100 character game path. Unfiltered on purpose: the
    /// picker groups skin first but still offers gear, accessories, hair and weapon.
    /// <para/>
    /// <paramref name="fromCache"/> is true when the character wasn't drawable and this fell back to the
    /// last known set, so the caller can say so rather than presenting stale data as current. Returns an
    /// empty list when neither source has anything (fresh config at the title screen).
    /// <para/>
    /// The live query costs several ms and must run on the framework thread — call it on a user action
    /// (opening the picker), never per frame. <see cref="DetectBodyMaterial"/> has the same constraints.
    /// </summary>
    public IReadOnlyList<string> ListActiveMaterials(out bool fromCache)
    {
        var live = penumbra.GetActivePlayerMaterialPaths();
        fromCache = live == null;

        // The cached list is reference-swapped from both the framework thread and background pool threads
        // without a lock, so read it into a local ONCE and enumerate that.
        IEnumerable<string> src = live ?? (IEnumerable<string>?)config.CachedActiveMaterialPaths ?? [];

        // .mtrl filtering and de-duplication are redundant for the live set (already a filtered HashSet)
        // but not for the cached List, which carries no set semantics.
        return src
            .Where(p => p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Which texture slots a material actually declares, so the Create tab can offer only the rows that
    /// will be consumed. Penumbra-resolved disk file first, game SqPack second — the same two-step the
    /// compositor uses, because a modded body's material only exists on disk.
    /// <para/>
    /// An all-null result means the material could NOT be read (path not installed, mod disabled, Penumbra
    /// down) — NOT that it has no textures. Callers must fail open on that and offer everything; the
    /// Create tab deliberately accepts hand-typed paths for bodies the player isn't wearing, and those
    /// routinely don't resolve.
    /// <para/>
    /// <see cref="MtrlTexturePaths.Index"/> is the material's OWN <c>_id</c> sampler — gear and accessories
    /// declare one almost universally. Body and face skin materials never do, so a caller wanting to offer
    /// a Proteus index on skin has to decide that from the material's kind; the material won't say.
    /// <para/>
    /// Re-reads and re-parses the file on every call — cheap, but blocking I/O. Call it when the material
    /// changes, never per frame.
    /// </summary>
    public MtrlTexturePaths ResolveMaterialSlots(string materialGamePath)
    {
        if (string.IsNullOrWhiteSpace(materialGamePath)) return new MtrlTexturePaths(null, null, null);
        // RAW parse, not the Lumina-typed one. Most materials the picker offers are VANILLA, and Lumina
        // misreads some Dawntrail layouts — which surfaced as every non-skin material reporting "couldn't
        // read", because a modded body/face (older TexTools layout) parsed while stock gear did not.
        return textureLoader.ResolveMtrlTexturesRaw(penumbra.ResolvePlayer(materialGamePath), materialGamePath);
    }

    /// <summary>Alpha at or above this reads as opaque. Not 255: a whole-skin base exported through a lossy
    /// step lands a few counts short — the mod this was measured against bottoms out at 251 — and demanding
    /// the maximum would call it sheer.</summary>
    private const byte OpaqueAlpha = 250;

    /// <summary>How much of the image must be opaque to count as full coverage. Not 1.0, so a stray
    /// feathered pixel at a UV island's edge can't veto the whole verdict.</summary>
    private const float FullCoverageFraction = 0.99f;

    /// <summary>
    /// Side length the probe samples at. The loader point-samples down to this, so a sparse overlay's holes
    /// stay holes rather than averaging away, and the decode is cached separately from the full-resolution
    /// one the compositor will want later.
    /// </summary>
    private const int CoverageProbeSize = 256;

    /// <summary>
    /// Mean per-channel difference, 0–255, below which the picked art is judged to BE the material's skin
    /// rather than something painted onto it.
    /// <para/>
    /// Calibrated by running the rule over 238 skin overlays: six real bibo skins scored 0.98–21.34 against
    /// a seventh, and the nearest thing on the other side — a full-coverage tartan bodysuit — scored 34.42.
    /// This sits between them with about six counts of room either way. Everything else was further off
    /// (patterned stockings 36–69, a flat velvet mitt 110), and exactly one overlay in that library trips
    /// the whole rule: the converted skin this was written for, at 4.58.
    /// <para/>
    /// Deliberately compared WITHOUT removing each image's mean. Mean-centring is the obvious way to make
    /// this tolerant of a skin tone far from the base's, and it backfires: it drops a solid-colour fabric
    /// to 6.65 — better than most real skins — because a flat field resembles anything once its level is
    /// taken away. Raw difference keeps colour in the comparison, which is what tells fabric from flesh.
    /// </summary>
    /// <remarks>internal so the test asserts against THIS number rather than a copy of it — a duplicated
    /// literal would keep passing while the shipped threshold moved out from under it.</remarks>
    internal const float SkinLikenessMad = 28f;

    /// <summary>
    /// Whether the picked textures look like a whole skin rather than something painted onto skin — the
    /// Create tab's default for <see cref="OverlayDescriptor.NormalMode"/>. Decodes two images, so call it
    /// off the frame thread.
    /// <para/>
    /// Three conditions, and the third is the one that does the work:
    /// <list type="number">
    /// <item>Both a colour map and a normal. The flag only decides how a normal blends, and a converted
    /// skin always brings its base with it.</item>
    /// <item>The colour map is opaque throughout. A skin has no holes; a tattoo or a decal is mostly
    /// hole.</item>
    /// <item>It resembles the diffuse already on the target material.</item>
    /// </list>
    /// Coverage alone is not enough, which is worth stating because it is the obvious test and it fails
    /// quietly: a garment overlay's art is routinely opaque across the WHOLE map, its shape coming from the
    /// mod's Masks group rather than from its own alpha. Measured on real mods, a pair of mitts and a set
    /// of stockings are both alpha 255 end to end. Only the comparison against the material's own skin
    /// separates them.
    /// </summary>
    public bool LooksLikeWholeSkin(string materialTarget, string? diffuseSrc, string? normalSrc)
    {
        if (string.IsNullOrWhiteSpace(diffuseSrc) || string.IsNullOrWhiteSpace(normalSrc)) return false;
        try
        {
            const int texels = CoverageProbeSize * CoverageProbeSize;

            var overlay = textureLoader.LoadPngAsRgba(diffuseSrc!, CoverageProbeSize, CoverageProbeSize);
            if (!IsFullCoverage(overlay, texels)) return false;

            // The material's CURRENT diffuse — through the same resolve the slot rows use, so a hand-typed
            // path for a body the player isn't wearing simply doesn't resolve and the answer stays "no".
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
            // Runs on a thread-pool thread, where an escaping exception is a silent wrong answer — and the
            // wrong answer here is "not a whole skin", which is also the safe default. Say so in the log.
            log.Warning(ex, "[Proteus] whole-skin probe failed for {0} — assuming it isn't one", diffuseSrc);
            return false;
        }
    }

    // ── glow ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The colour-table row a glowing overlay writes. An overlay with no <c>_id</c> art gets a fabricated
    /// index of (255, 255, 0) from the shell builder, and row pair 16 sub-row A is the only cell that
    /// selects — a row written anywhere else would be invisible.
    /// </summary>
    internal const int GlowRow = 16;

    /// <summary>
    /// The surface under a dark-only glow: black. <c>characterscroll.shpk</c> declares no base texture, so
    /// this row colour IS the whole unlit surface and it is lit by the scene like any other. Anything above
    /// black reads as a lifted charcoal patch wherever light falls.
    /// </summary>
    internal const string DarkOnlySurface = "#000000";

    /// <summary>
    /// The emissive an ALWAYS-glow row carries.
    /// <para/>
    /// Very low, and measured against the failure rather than reasoned about. On <c>character.shpk</c> the
    /// emissive is a flat ADDITIVE tint across the whole region, not a map — and the reasoning that put
    /// this at a quarter was backwards. "A lit scene already swamps a small additive glow" is exactly
    /// wrong: in daylight the add lands on an already-bright surface and clips into the highlights, so a
    /// quarter of white bleached a pale watercolour to near-white. It is in the DARK that a small value
    /// reads as a glow, because there is nothing else lit to compete with it.
    /// <para/>
    /// Anyone who wants more at night without the daytime wash wants the per-row "Fades in light" dial in
    /// Colors, which is the control that can tell the two apart.
    /// </summary>
    internal const float AlwaysGlowEmissive = 0.08f;

    /// <summary>
    /// The art's own average colour, weighted by coverage, as <c>#RRGGBB</c> — or null when nothing is
    /// covered.
    /// <para/>
    /// An always-glow row emits in this rather than in white. The emissive on <c>character.shpk</c> is one
    /// flat colour for the whole region, so it cannot follow the picture per pixel the way a scroll map
    /// does; what it CAN do is stop fighting it. White adds grey to every hue at once and desaturates
    /// everything it touches, while the art's own average reinforces the colours already there.
    /// <para/>
    /// Weighted by alpha because the art sits on a transparent background: an unweighted mean is mostly the
    /// background and comes out near-black, which would emit nothing at all.
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
    /// Turn a decoded overlay into the scroll map a dark-only glow emits: the art's own colour multiplied
    /// by its own coverage, laid on black and made fully opaque.
    /// <para/>
    /// The colour and the shape both come from the art, which is why the Create tab asks for no colour: on
    /// <c>characterscroll.shpk</c> this map IS the light, per pixel. Black where the art is transparent, so
    /// nothing outside it emits; opaque throughout, because this texture's alpha is not a coverage channel
    /// and a transparent scroll map simply renders nothing.
    /// <para/>
    /// The same transform <see cref="LuminisImportService"/> performs on an Atramentum Luminis sheet — the
    /// difference is only where the intensity comes from. There it is an inverted mask in the alpha; here
    /// the art's alpha is ordinary coverage.
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
    /// The colour-table row for a glow style, or null when the overlay doesn't glow.
    /// <para/>
    /// <see cref="GlowStyle.Always"/> carries NO light response, which is what makes its name true: it
    /// emits the same in a lit street as in a cellar, and it goes on working with the light feature
    /// switched off entirely. What keeps that from bleaching the art in daylight is not the scene — see
    /// <see cref="AlwaysGlowEmissive"/>, where assuming the scene would handle it was the mistake — but the
    /// pairing of a very small value with <see cref="AverageArtColour"/>. Anyone who wants a brighter night
    /// glow without a daytime wash wants the per-row "Fades in light" dial in Colors, which is the only
    /// control that can tell the two apart.
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
                // The art's own average, not white — see AverageArtColour. White falls back only when the
                // art could not be read, where a colourless glow beats none.
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
                // Neutral: the scroll map carries the art's own hue, and a tinted emissive would only push
                // everything toward that tint.
                EmissiveColor = RenderModeInference.GlowEmissiveColour,
                // Dark-only, both halves: the glow fades as the light rises and the surface goes with it,
                // so the art vanishes into the skin instead of leaving a black silhouette.
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
    /// The textures ARE the skin, not something painted onto it. Three consequences: the normal replaces the
    /// one already on the material instead of stacking onto it (see <see cref="NormalMode.Replace"/>);
    /// skin-tint suppression is off, so the wearer's tone reaches the art the way it reaches their face; and
    /// the mod's sibling mode is raised to <see cref="SiblingSynthesisMode.AllBodies"/> so the bake reaches
    /// a vanilla body too. The first two are sidecar fields written by <see cref="WriteMod"/>; the third is
    /// plugin config, keyed by mod directory, so it is set here once the directory name is settled.
    /// </param>
    /// <param name="faceSplit">
    /// The picked face texture is a DOUBLED sheet, the two sides of the head in the two halves of the image.
    /// Recorded as the overlay's source UV space, which is what lets the compositor un-mirror a face shell
    /// onto it — the vanilla face layout has no room for side-specific art at all.
    /// </param>
    /// <param name="glow">
    /// Whether the art glows, and how. Anything but <see cref="GlowStyle.None"/> makes the overlay a gear
    /// shell — skin cannot emit — and adds the colour-table row that lights it.
    /// <see cref="GlowStyle.DarkOnly"/> also needs a scroll map, which is derived from the diffuse HERE
    /// rather than in <see cref="WriteMod"/>: that one is deliberately service-free so the tests can drive
    /// it against a temp directory, and decoding an image is not.
    /// </param>
    public CreateResult Create(
        string modName, string author, string materialTarget,
        string? diffuseSrc, string? maskSrc, string? normalSrc, string? indexSrc,
        bool wholeSkin = false, bool faceSplit = false, GlowStyle glow = GlowStyle.None)
    {
        modName = (modName ?? "").Trim();
        author = (author ?? "").Trim();
        materialTarget = (materialTarget ?? "").Trim();

        // These run once per click on Create, not once per frame, so they read the string table directly
        // rather than going through a cached holder — the message stays next to the branch that decides it.
        if (string.IsNullOrWhiteSpace(modName))
            return new(false, Loc.Localize("Create.Error.NoName", "Enter a mod name."));
        if (string.IsNullOrWhiteSpace(materialTarget))
            return new(false, Loc.Localize("Create.Error.NoMaterial", "Enter a material target."));

        // Each slot carries BOTH names: the label is what the error message shows the user, and it is
        // translated; there is no invariant token needed here because nothing downstream switches on it.
        // Interpolating the English identifier instead would put a bare "diffuse" inside an otherwise
        // fully translated Russian sentence.
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

        // A glow's colour comes from the art, per pixel, so there has to be art. The tab dims the checkbox
        // without a diffuse; this is the same rule where it can be enforced rather than merely shown.
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

        // Measure the art's left/right asymmetry from the SOURCE files, before they are copied in — the
        // descriptor's own space comes from the material it targets, since the Create tab records no
        // SourceBodyType of its own. Same slot priority the compositor uses: a tattoo lives in the diffuse,
        // and a normal/mask-only overlay carries its shape in whichever it has.
        // Both styles read the art HERE, so WriteMod stays service-free and testable: dark-only needs the
        // scroll map built from it, and always-glow needs its average colour to emit in. A decode that
        // fails is reported rather than swallowed — the alternative is a mod that writes fine, renders
        // wrong, and says nothing about why.
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
            // Roll the folder back so the name is free to retry — a half-registered mod on disk would
            // otherwise trip the "already exists" guard on the next attempt.
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return new(false, string.Format(Loc.Localize("Service.RegisterFailed.Fmt",
                "Wrote the mod, but Penumbra couldn't register it ({0}). Rescan mods in Penumbra."), ec));
        }

        // A whole skin has to reach every body the wearer might be on, and sibling synthesis defaults to
        // bibo+gen3 deliberately — baking every mod onto every vanilla body loaded nearby is expensive, so
        // vanilla (gen2) is opt-in per mod. That default is right for a tattoo and wrong for a skin, which
        // would otherwise be silently inert on a vanilla body. Raise it for THIS mod only, and log it: the
        // control lives in another panel (Advanced → Bodies), so a silent write there is a setting the user
        // finds already moved with nothing to say why. Same reasoning and shape as OnionImportService.
        if (wholeSkin)
        {
            config.SiblingSynthesis[dirName] = SiblingSynthesisMode.AllBodies;
            config.Save();
            log.Information("[Proteus] created {0} as a whole skin — set its sibling mode to AllBodies so "
                          + "the bake reaches vanilla (gen2) as well as bibo and gen3", dirName);
        }

        // Enabling is left to Pump, across frames. AddMod is ASYNCHRONOUS: a settings write that lands
        // while Penumbra is still building the mod returns Success and is then discarded, because the
        // finished mod replaces the placeholder and comes up on its own defaults — disabled. Enabling on
        // the next line therefore worked only because Create-tab mods were small, and this is exactly the
        // failure LuminisImportService documents ("the bigger the mod the more reliably it happened, which
        // is why the Create tab never showed it"). A dark-only glow writes a second full-size texture, so
        // it is the case that surfaces it.
        // Opened HERE rather than in Finish, and unconditionally, because Pump only runs while the Create
        // tab is on screen. Someone who clicks Create and closes the window immediately would otherwise
        // never have the mod opened at all — the one thing this did for certain before enabling moved
        // across frames. Opening it also puts an un-enabled mod in front of the user on the page where
        // switching it on is a single click, which is the best available outcome for an abandoned wait.
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

    /// <summary>How often to re-ask while waiting — Penumbra IPC is milliseconds a hop, and this would
    /// otherwise make two of them every frame for as long as the wait lasts.</summary>
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

        // Ask, then READ BACK — the return code is not enough on its own. A write discarded by the mod
        // finishing its load still reports Success, so only the state Penumbra reports afterwards settles it.
        penumbra.SetModEnabled(collId.Value, p.DirName, true);
        if (penumbra.GetModSettings(collId.Value, p.DirName) is { Enabled: true })
            return Finish(true);

        if (now < p.Deadline) return null;   // still settling — ask again shortly

        log.Warning("[Proteus] created {0}: Penumbra would not report the mod as enabled within {1}ms",
            p.DirName, ActivateTimeoutMs);
        return Finish(false);
    }

    /// <summary>
    /// Recomposite and report. <paramref name="enabled"/> false means the mod is on disk and registered but
    /// switched off — a warning, not a success, because a mod that renders nothing while the message says
    /// it worked is the failure this whole path exists to avoid.
    /// <para/>
    /// The recomposite is gated on that flag while the OPEN is not (it happens in <see cref="Create"/>):
    /// a mod Penumbra never switched on contributes nothing, so compositing for it would be work that
    /// cannot change a pixel. When the enable does land later, this is what paints it.
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
    /// Write the mod files under <paramref name="root"/>: the texture copies, the Proteus sidecar
    /// (metadata.json), and Penumbra's meta.json/default_mod.json. Pure filesystem work, no IPC — split
    /// out so it can be exercised offline against a temp directory.
    /// </summary>
    /// <param name="faceSplit">
    /// The picked face texture is a DOUBLED sheet — the two sides of the head in the two halves of the image
    /// — so the overlay declares that layout and gets un-mirrored onto a face shell. Meaningless on any
    /// non-face target, and wrong on an ordinary face texture.
    /// </param>
    /// <param name="scrollRgba">
    /// The already-built scroll map for <see cref="GlowStyle.DarkOnly"/>, at <paramref name="scrollW"/> ×
    /// <paramref name="scrollH"/>. Built by the caller because this method decodes nothing — it is
    /// service-free on purpose so the tests can drive it against a temp directory. Null for every other
    /// style, and a null one under DarkOnly simply leaves the effect unwritten rather than throwing.
    /// </param>
    internal static void WriteMod(
        string root, string modName, string author, string materialTarget,
        string? diffuseSrc, string? maskSrc, string? normalSrc, string? indexSrc,
        bool wholeSkin = false, bool faceSplit = false, GlowStyle glow = GlowStyle.None,
        byte[]? scrollRgba = null, int scrollW = 0, int scrollH = 0, string? artColour = null)
    {
        var overlaysDir = Path.Combine(root, "Proteus", "overlays");
        Directory.CreateDirectory(overlaysDir);

        // Copy each provided source into overlays/{slot}{ext}, keeping the original (lower-cased) extension
        // so .png/.tex/.dds all load; record the sidecar-relative path for the descriptor.
        string? Copy(string slot, string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;
            var ext = Path.GetExtension(src).ToLowerInvariant();
            var name = slot + ext;
            File.Copy(src, Path.Combine(overlaysDir, name), overwrite: true);
            return "overlays/" + name;
        }

        // A dark-only glow's light lives in a scroll map beside the art, named in the descriptor by its
        // BARE file name — SidecarDiscoveryService.ResolveEffectPath looks it up in Proteus/Effects/ and in
        // the shared library, not as a sidecar-relative path like the overlay slots above.
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
            // Skin cannot emit, so a glow is a gear shell. Layer AND Shader are both stated: promotion
            // alone only moves the LAYER, and the shader would then fall through to plain character.shpk —
            // which has no scroll map at all, so a dark-only overlay would render its art as an unlit
            // surface with a flat tint on top and no amount of tuning would fix it.
            Layer = glow == GlowStyle.None ? OverlayLayer.Skin : OverlayLayer.Gear,
            Shader = glow switch
            {
                GlowStyle.Always   => OverlayDescriptor.DefaultGearShader,
                GlowStyle.DarkOnly => RenderModeInference.GlowShader,
                _                  => null,
            },
            Scroll = scrollFile,
            // Zero and one, explicitly. The material constants ship the speeds at zero and an unset speed
            // takes GearMaterialWriter's own default instead, sliding the tattoo across the skin it is
            // drawn on; the map IS the body sheet, so tiling it would repeat the art.
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
            // A face texture painted as two halves declares the doubled face layout, which is the only way
            // side-specific face art can exist: the vanilla one gives both cheeks the same texels. Left null
            // otherwise — an ordinary face texture IS in the vanilla layout, and saying so would send its two
            // halves to the two sides of the head.
            SourceBodyType = faceSplit ? UVRemapService.FaceSplitSpace : null,
            // Skin-tint suppression exists to keep FABRIC at its authored colour on any wearer. Art that is
            // itself the skin wants the opposite — the wearer's tone multiplied onto it, the way the face's
            // own material already does — so a whole skin ships with it off. Left null otherwise, which is
            // the documented "full masking" default and keeps the line out of an ordinary sidecar.
            SkinToneMask = wholeSkin ? 0f : null,
        };
        var metadata = new ProteusMetadata
        {
            FormatVersion = 1,
            Name = modName,
            Author = author,
            Overlays = [descriptor],
            // Top level is safe HERE and nowhere else: top-level rows are inherited by every option that
            // declares none, and any emissive makes RenderModeInference.HasCloth true — which is why the
            // importers put theirs on the option instead, so an emissive can't reach a plain skin option
            // and promote it to a shell. A Create-tab mod has exactly one overlay and no option groups,
            // so there is nothing else for it to reach.
            ColorTableRows = GlowRowFor(glow, artColour) is { } row ? [row] : null,
        };

        var metaJson = JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite);
        // AtomicWrite for the same reason as the manifest below, and with more at stake: meta.json is
        // regenerable boilerplate, whereas this descriptor is the authored overlay itself. A zero-filled
        // one leaves a mod that loads in Penumbra and does nothing in Proteus.
        PenumbraModMeta.AtomicWrite(Path.Combine(root, "Proteus", "metadata.json"), metaJson);

        // Penumbra's manifest, matching CompositorService.EnsureManagedModExists. Written in the older
        // layout on purpose: every Penumbra can read it, and a new one migrates it into meta.json on load.
        // Via AtomicWrite for durability — a manifest left truncated or zero-filled by a crash makes
        // Penumbra drop the whole mod, with only a parse error in its Messages tab to say why.
        PenumbraModMeta.AtomicWrite(
            Path.Combine(root, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(modName, author, "Created for Proteus."));

        // Proteus does all the real texture redirection itself (via its managed mod) at composite time, so
        // this default option would otherwise be empty — which Penumbra flags as "changes nothing". A
        // no-op self-swap of a harmless vanilla path registers it as having content. See DummySwapPath.
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
