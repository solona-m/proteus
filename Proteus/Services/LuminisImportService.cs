using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Turns an Atramentum Luminis <c>.ttmp2</c> glow-tattoo pack into a Penumbra mod carrying a Proteus
/// sidecar — the Import tab's engine for the third pack format, shaped like
/// <see cref="OnionImportService"/> and for the same reasons.
/// <para/>
/// Atramentum Luminis is a shader mod: it replaces skin.shpk and reads glow out of a diffuse's ALPHA
/// channel, where 255 is ordinary skin, 0 glows at full intensity and the values between set how brightly.
/// Its art therefore ships as a complete body diffuse addressed to VIRTUAL paths (<c>chara/bibo/…</c>) that
/// no vanilla shader ever asks for, and without AL installed the pack renders nothing at all.
/// <para/>
/// Proteus renders the same effect with no shader replacement, out of parts it already has:
/// <list type="bullet">
/// <item><c>255 − alpha</c> becomes the overlay's own alpha, which is what the shell builder reads as
/// coverage — so the glowing pixels, and only those, get a second-skin shell cut for them.</item>
/// <item>the RGB becomes a characterscroll scroll map at speed zero. A COLOURED scroll map carries its own
/// colour per pixel, which is exactly AL's multicoloured glow; the colour-table row's emissive is only the
/// small gate that switches it on.</item>
/// </list>
/// The author's underlying skin is imported too, as a separate option that arrives switched OFF — it is a
/// whole body texture at their skin tone, not an overlay, so wearing it is a choice and never a side
/// effect of wanting the tattoo.
/// <para/>
/// Nothing is guessed at silently. A path that is not AL-shaped, a texture with no glow mask in it, a
/// payload that is not a texture — each is SKIPPED with a reason the tab shows.
/// </summary>
public sealed class LuminisImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly ModCreationService modCreation;
    private readonly TextureLoader textureLoader;
    private readonly BodyMaterialCatalog bodies;
    private readonly IPluginLog log;

    /// <summary>
    /// The Penumbra group an imported pack gets. One multi-select group holding every option, rather than
    /// a group per concern: <c>SidecarDiscoveryService.ResolveActiveOverlays</c> reads a mod's top-level
    /// <c>Overlays</c> OR its <c>OptionGroups</c> and never both, so an unconditional glow plus a gated
    /// skin would silently drop the gated half.
    /// </summary>
    public const string GroupName = "Atramentum Luminis";

    public LuminisImportService(PenumbraBridge penumbra, CompositorService compositor,
        ModCreationService modCreation, TextureLoader textureLoader, BodyMaterialCatalog bodies,
        IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.modCreation = modCreation;
        this.textureLoader = textureLoader;
        this.bodies = bodies;
        this.log = log;
    }

    // ── Format mapping ───────────────────────────────────────────────────────

    /// <summary>
    /// Atramentum Luminis body token → the Proteus UV body type its art is painted in, and the material
    /// suffix a body of that type carries. Same shape as <see cref="OnionImportService"/>'s layout table
    /// and for the same reason: these are the two facts an overlay needs to land on the right material in
    /// the right UV space.
    /// <para/>
    /// Deliberately short. A token that is NOT here is not refused — see <see cref="ResolveBody"/>, which
    /// falls back to the body the character is actually wearing. That is what lets a male TBSE or HRBody
    /// pack import without this table having heard of it.
    /// </summary>
    private static readonly Dictionary<string, (string BodyType, string Suffix)> Bodies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bibo"] = ("bibo", "_bibo.mtrl"),
            ["gen3"] = ("gen3", "_b.mtrl"),
        };

    /// <summary>
    /// The material suffixes the body-target override offers. Every UV space
    /// <see cref="UVRemapService.InferBodyType"/> can name, so a body Proteus has never heard of can still
    /// be aimed at the right material without a code change.
    /// </summary>
    public static readonly string[] BodySuffixes = ["_bibo.mtrl", "_b.mtrl", "_a.mtrl", "_eve.mtrl"];

    /// <summary>
    /// Second path segments that belong to the GAME. A path under one of these is a real redirect, which
    /// means the pack is an ordinary TexTools mod rather than an Atramentum Luminis one — a distinction
    /// worth making by name, because "nothing importable" would send the user looking for a fault that
    /// isn't there.
    /// </summary>
    private static readonly HashSet<string> VanillaRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "human", "equipment", "accessory", "weapon", "monster", "demihuman",
        "common", "xls", "action", "base_material", "npc",
    };

    /// <summary>
    /// How much of a texture must carry a glow mask before it counts as one. A tattoo can be small — this
    /// is about a tenth of one percent, ~4,000 pixels of a 2048² sheet — so it rejects a texture whose
    /// alpha is flat while passing anything actually painted.
    /// </summary>
    private const float MinGlowFraction = 0.0001f;

    /// <summary>Above this, the "mask" covers so much of the body that it is more likely a texture with no
    /// alpha at all than a tattoo. Warned about rather than refused: it is legal AL, just unusual.</summary>
    private const float SuspiciousGlowFraction = 0.9f;

    /// <summary>Alpha at or above this is "no glow". Not 255, so a lossily-compressed source still reads
    /// its own flat regions as flat.</summary>
    private const int OpaqueAlpha = 250;

    /// <summary>
    /// How hard to push the inverted mask toward opaque when turning it into coverage. See the coverage
    /// comment in <see cref="WriteMod"/>: the mask's interior is flat plateaus and only its outline ramps,
    /// so this saturates the former without hardening the latter.
    /// </summary>
    private const int CoverageGain = 8;

    /// <summary>
    /// The surface UNDER the glow: black.
    /// <para/>
    /// characterscroll declares no base texture at all (see GearMaterialWriter's sampler table), so this
    /// row colour IS the whole unlit surface, and it is lit by the scene like any other. An Atramentum
    /// Luminis panel is artwork drawn on black and has to read as black; the near-black grey this started
    /// at came out as a visibly lifted charcoal wherever the light fell on it, against the true black of
    /// the areas facing away.
    /// </summary>
    private const string GlowSurfaceColour = "#000000";

    /// <summary>The colour-table row a shell with no <c>_id</c> art samples: SecondSkinService fabricates
    /// an index of (255, 255, 0), which is row pair 16, sub-row A.</summary>
    private const int GlowRow = 16;

    /// <summary>
    /// The row emissive: 300%. Measured in game against the artwork, not derived.
    /// <para/>
    /// On characterscroll this is the multiplier the scroll map's brightness scales with. Much higher than
    /// anything else in Proteus writes, and the reason is what the map holds: an Atramentum Luminis sheet
    /// is mostly BLACK with thin neon on it, so the average pixel contributes nothing and only the lines
    /// have anything to scale. The values tuned for a piece whose scroll map is saturated edge to edge —
    /// <see cref="ContentGlowRow.DefaultGlow"/> at 25%, <see cref="RenderModeInference.GlowEmissive"/> at
    /// 150% — both leave this art dim.
    /// <para/>
    /// It only reads correctly once <see cref="GlowSurfaceColour"/> is true black. Against the near-black
    /// grey this started at, the same dial lifted the whole panel instead of the lines, which is what made
    /// 150% look blown out earlier: the surface was rising with the glow.
    /// <para/>
    /// Worth being precise about, because the same field means something else one shader over: on plain
    /// character.shpk it is a flat additive tint, and a quarter of WHITE there is a wash that turns black
    /// artwork into white slabs. Both were observed here, on the way to getting the shader right.
    /// </summary>
    private const float GlowGate = 3.0f;

    // ── Preview ──────────────────────────────────────────────────────────────

    /// <summary>One texture the pack ships, and what the import decided to do with it.</summary>
    /// <param name="Paths">Every manifest path backed by this payload. An AL pack aliases one texture to
    /// several — six, in the pack this was written against — and they are all the same picture.</param>
    /// <param name="Stem">Filename-safe name for the written files and the option labels.</param>
    /// <param name="FromWearer">
    /// The body was resolved from the character rather than from <see cref="Bodies"/>. Surfaced because
    /// the two are not equally trustworthy: a known token says what the ARTIST painted, the fallback says
    /// what the wearer happens to have on.
    /// </param>
    /// <param name="SkipReason">Null when the texture will be imported; otherwise why it won't be.</param>
    public sealed record TexturePlan(
        long Offset,
        long Size,
        IReadOnlyList<string> Paths,
        string Stem,
        string? Token,
        string? BodyType,
        string? Suffix,
        int Width,
        int Height,
        float GlowFraction,
        bool FromWearer,
        string? SkipReason)
    {
        public bool Import => SkipReason == null;

        /// <summary>What the tab lists it as.</summary>
        public string Label => Paths.Count > 0 ? Paths[0] : Stem;
    }

    /// <summary>Everything the Import tab renders after Browse, and everything <see cref="Prepare"/>
    /// needs.</summary>
    /// <param name="WearerSuffix">
    /// The material suffix of the body the character is wearing, or null when they aren't drawn. Seeds the
    /// body-target override, and is what an unknown token resolves against.
    /// </param>
    public sealed record ImportPreview(
        string SourcePath,
        string Name,
        string Author,
        string? Description,
        string? Website,
        string? Version,
        IReadOnlyList<TexturePlan> Textures,
        IReadOnlyList<string> Warnings,
        string? WearerSuffix)
    {
        public bool AnyImportable => Textures.Any(t => t.Import);

        private IReadOnlyList<TexturePlan>? importable;

        /// <summary>
        /// Textures that will be imported, in manifest order. Cached, because the Import panel asks for it
        /// every frame the preview is on screen and a fresh filtered list per frame is exactly the kind of
        /// per-draw allocation the rest of this window goes out of its way to avoid.
        /// </summary>
        public IReadOnlyList<TexturePlan> Importable
            => importable ??= [.. Textures.Where(t => t.Import)];

        /// <summary>The suffix the import will target unless the user overrides it. Null only when
        /// nothing is importable.</summary>
        public string? DefaultSuffix => Importable.FirstOrDefault()?.Suffix;
    }

    /// <summary>
    /// Read the pack and work out what it carries. Throws <see cref="InvalidDataException"/> when the file
    /// isn't a readable modpack — the caller turns that into a message.
    /// <para/>
    /// This DECODES every candidate texture, which is not free: about 90 ms for a 2048² sheet, on the frame
    /// that picked the file. Paid deliberately, and the same trade <see cref="ContentImportService.Inspect"/>
    /// already makes by parsing every model in a pack there. The alternative is a preview that classifies
    /// on the path alone and cannot tell an AL pack from an ordinary TexTools one until after it has
    /// written a mod — which is the question the user opened the preview to have answered. Only the
    /// MEASUREMENTS are kept; the pixels are dropped, so a pack of eight variants does not hold 176 MB
    /// for as long as the preview is on screen.
    /// </summary>
    public ImportPreview Inspect(string ttmpPath)
    {
        // The body the character is actually wearing, for the tokens the table has never heard of.
        // Resolved here rather than inside the classifier so the classification itself stays pure.
        var body = modCreation.DetectBodyMaterial() ?? modCreation.CachedBodyMaterial();
        return BuildPreview(ttmpPath, TexToolsPackage.Read(ttmpPath), body, DecodeAlpha);

        // Only the alpha channel is wanted, but the whole surface has to be decoded to reach it — the
        // loader's job is pixels, and a BC-compressed AL pack has no shortcut to one channel.
        (int Width, int Height, float Glow)? DecodeAlpha(byte[] tex, string what)
        {
            var decoded = textureLoader.LoadTexBytesAsRgba(tex, what);
            if (decoded is not { } d || d.width <= 0 || d.height <= 0) return null;

            long glow = 0, total = (long)d.width * d.height;
            for (long i = 3; i < d.rgba.LongLength; i += 4)
                if (d.rgba[i] < OpaqueAlpha) glow++;
            return (d.width, d.height, total == 0 ? 0f : (float)((double)glow / total));
        }
    }

    /// <summary>
    /// Classify an already-read pack. The whole of <see cref="Inspect"/> minus the two things that need a
    /// live game — the body detection and the texture decoder — so it can be exercised offline.
    /// </summary>
    /// <param name="wearerBody">The material path of the body the character is wearing, or null.</param>
    /// <param name="measure">
    /// Reassembled <c>.tex</c> bytes and a name for the log → its size and how much of it glows, or null
    /// when the bytes will not decode.
    /// </param>
    internal static ImportPreview BuildPreview(
        string ttmpPath,
        TexToolsPackage.Contents pack,
        string? wearerBody,
        Func<byte[], string, (int Width, int Height, float Glow)?> measure)
    {
        var warnings = new List<string>();
        var wearerSuffix = SuffixOf(wearerBody);
        var wearerType = wearerBody == null ? null : UVRemapService.InferBodyType(wearerBody);

        // One entry per distinct payload, carrying every path that aliases it. Keyed on the offset because
        // that IS the identity of a file in this format — an AL pack points six paths at byte 0, and
        // importing six copies of one picture would write six shells over each other.
        var byOffset = new Dictionary<long, List<TexToolsPackage.PackFile>>();
        var order = new List<long>();
        foreach (var f in pack.Files)
        {
            if (!byOffset.TryGetValue(f.Offset, out var list))
            {
                byOffset[f.Offset] = list = [];
                order.Add(f.Offset);
            }
            list.Add(f);
        }

        var slices = byOffset.ToDictionary(kv => kv.Key, kv => kv.Value.Max(f => f.Size));

        // Decode ONLY the payloads that survive the path check, and decide that from the manifest alone.
        //
        // Everything below needs pixels, but nothing about "is this even an Atramentum Luminis path" does,
        // and the difference is unbounded: the Import tab accepts any .ttmp2 a user picks, so an ordinary
        // TexTools mod arriving here would otherwise be inflated in full — every model, material and 4K
        // sheet in it, all resident at once, on the frame that picked the file — purely to be told none of
        // its paths are AL-shaped. A pack that is not one now costs a manifest read.
        var wanted = slices
            .Where(kv => IsCandidate(byOffset[kv.Key][0].GamePath))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var payloads = TexToolsPackage.ReadPayloads(ttmpPath, wanted);

        // Stems name the written files AND the Penumbra options, so two payloads may not share one. A pack
        // shipping chara/bibo/midlander_d.tex beside chara/gen3/midlander_d.tex would otherwise write both
        // over the same overlays/midlander_glow.png and offer two identically-named options.
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var plans = new List<TexturePlan>();
        foreach (var offset in order)
        {
            var paths = byOffset[offset].Select(f => f.GamePath).ToList();
            var stem = Unique(StemOf(paths[0]), TokenOf(paths[0]));
            var size = slices[offset];

            TexturePlan Skip(string why)
                => new(offset, size, paths, stem, null, null, null, 0, 0, 0f, false, why);

            // ── is it AL-shaped? ──
            var first = paths[0];
            if (!first.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.NotATexture",
                    "it isn't a texture — Atramentum Luminis only carries those.")));
                continue;
            }

            var token = TokenOf(first);
            if (token == null)
            {
                plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.GamePath.Fmt",
                    "\"{0}\" is a real game path, so this is an ordinary TexTools mod rather than an "
                  + "Atramentum Luminis pack. Install it in Penumbra instead."), first)));
                continue;
            }

            // ── which body, and in whose UV space? ──
            var (bodyType, suffix, fromWearer) = ResolveBody(token, wearerType, wearerSuffix);
            if (suffix == null)
            {
                plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.UnknownBody.Fmt",
                    "Proteus doesn't know the body \"{0}\", and can't ask your character which one they "
                  + "are wearing until they're drawn. Load in and reopen this pack."), token)));
                continue;
            }

            // ── does it actually carry a glow mask? ──
            if (!payloads.TryGetValue(offset, out var payload))
            {
                plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.Missing",
                    "the modpack's data blob doesn't contain it.")));
                continue;
            }
            if (payload.Error != null)
            {
                plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.Unreadable.Fmt",
                    "it couldn't be read: {0}"), payload.Error)));
                continue;
            }
            if (!payload.IsTexture)
            {
                plans.Add(Skip(string.Format(Loc.Localize("Import.Luminis.Skip.NotTextureData.Fmt",
                    "it is a model or material (SqPack type {0}), not a texture."), payload.Type)));
                continue;
            }

            var measured = measure(payload.Tex!, first);
            if (measured is not { } m)
            {
                plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.Undecodable",
                    "its pixels couldn't be decoded.")));
                continue;
            }

            if (m.Glow < MinGlowFraction)
            {
                plans.Add(Skip(Loc.Localize("Import.Luminis.Skip.NoGlow",
                    "its alpha channel is flat, so it carries no glow. Atramentum Luminis puts the glow "
                  + "there, and a texture without one is an ordinary skin rather than a glowing tattoo.")));
                continue;
            }

            if (m.Glow > SuspiciousGlowFraction)
                warnings.Add(string.Format(Loc.Localize("Import.Luminis.Warn.MostlyGlow.Fmt",
                    "\"{0}\" glows across {1:P0} of the body. That is legal, but it usually means the "
                  + "texture has no real alpha channel — check the result before wearing it out."),
                    first, m.Glow));

            plans.Add(new TexturePlan(offset, size, paths, stem, token, bodyType, suffix,
                                      m.Width, m.Height, m.Glow, fromWearer, null));
        }

        if (plans.Count == 0 || plans.All(p => !p.Import))
            warnings.Add(Loc.Localize("Import.Luminis.Warn.NothingImportable",
                "Nothing in this pack can be imported — see the reasons above."));

        // Said once, not per texture: the fallback is a guess about which body the art was painted for,
        // and a guess repeated eight times reads as eight problems.
        if (plans.Any(p => p.Import && p.FromWearer))
            warnings.Add(string.Format(Loc.Localize("Import.Luminis.Warn.FromWearer.Fmt",
                "Proteus doesn't know this pack's body layout, so it will paint the art onto the body "
              + "you're wearing ({0}) exactly as it is, with no resizing. If the pack was painted for a "
              + "different body it will look wrong — change the body target below if you know better."),
                wearerSuffix ?? ""));

        // Deliberately NOT warnings, and the two that are here say why by contrast. Proteus carries no race
        // or sex filter and never has — that is a standing property of every overlay it composites, not
        // something this pack did — and colouring it amber on every single import is the cried-wolf problem
        // ContentImportService.FaultyUnits exists to avoid. The panel states it in plain text instead. The
        // aliasing is the same: several paths over one picture is the NORMAL shape of these packs, and the
        // texture table already says "{n} paths" on the row it applies to.
        return new ImportPreview(
            ttmpPath,
            pack.Name.Trim(),
            pack.Author.Trim(),
            string.IsNullOrWhiteSpace(pack.Description) ? null : pack.Description!.Trim(),
            string.IsNullOrWhiteSpace(pack.Website) ? null : pack.Website!.Trim(),
            string.IsNullOrWhiteSpace(pack.Version) ? null : pack.Version!.Trim(),
            plans,
            warnings,
            wearerSuffix);

        // Worth decoding: a texture on an Atramentum Luminis virtual path. The two cheap checks the loop
        // below makes before it touches a pixel, hoisted so the decode can be limited to what passes them.
        static bool IsCandidate(string gamePath)
            => gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && TokenOf(gamePath) != null;

        // Qualified by the body token first, since that is what actually differs between two payloads
        // spelled the same, and only then by a number.
        string Unique(string stem, string? token)
        {
            if (stems.Add(stem)) return stem;
            if (token != null && stems.Add(stem + "_" + token)) return stem + "_" + token;
            for (int i = 2; ; i++)
                if (stems.Add(stem + "_" + i))
                    return stem + "_" + i;
        }
    }

    /// <summary>
    /// Which material this token's art belongs on, and which UV space to declare it in.
    /// <para/>
    /// A known token answers both from the table, and a wearer on a DIFFERENT body then gets the art
    /// remapped into theirs. An unknown one answers from the wearer's own body instead, declaring the art
    /// to already be in that space — so <c>UVRemapService.UvConverter</c> sees source and destination
    /// agree, returns null, and the art paints one-to-one. That is the right answer for a body Proteus
    /// cannot remap: its art was painted in its own UV space, and the only thing that would ruin it is
    /// putting it through a transfer map meant for another body.
    /// </summary>
    private static (string? BodyType, string? Suffix, bool FromWearer) ResolveBody(
        string token, string? wearerType, string? wearerSuffix)
    {
        if (Bodies.TryGetValue(token, out var known)) return (known.BodyType, known.Suffix, false);
        if (wearerSuffix == null) return (null, null, false);
        return (wearerType, wearerSuffix, true);
    }

    /// <summary>
    /// The Atramentum Luminis body token in a virtual path, or null when the path is a real game one.
    /// <para/>
    /// AL addresses <c>chara/&lt;token&gt;/&lt;name&gt;.tex</c> and <c>chara/&lt;token&gt;_&lt;tag&gt;.tex</c>
    /// — two or three segments directly under <c>chara/</c>, which is a shape no vanilla path has. The
    /// second form's token is read as the longest KNOWN token the filename starts with, falling back to
    /// everything before the first underscore, so <c>bibo_high_base</c> is bibo rather than bibo_high.
    /// </summary>
    internal static string? TokenOf(string gamePath)
    {
        var parts = gamePath.Split('/');
        if (parts.Length is < 2 or > 3) return null;
        if (!string.Equals(parts[0], "chara", StringComparison.OrdinalIgnoreCase)) return null;

        if (parts.Length == 3)
            return VanillaRoots.Contains(parts[1]) ? null : parts[1];

        var leaf = Path.GetFileNameWithoutExtension(parts[1]);
        foreach (var known in Bodies.Keys)
            if (leaf.StartsWith(known + "_", StringComparison.OrdinalIgnoreCase))
                return known;

        int underscore = leaf.IndexOf('_');
        return underscore > 0 ? leaf[..underscore] : null;
    }

    /// <summary>
    /// A filename-safe name for one payload, from the first path that names it. The trailing <c>_d</c> AL
    /// puts on its diffuses is dropped — every texture here is one, so carrying it into the option labels
    /// would say nothing and read as noise.
    /// </summary>
    internal static string StemOf(string gamePath)
    {
        var leaf = Path.GetFileNameWithoutExtension(gamePath);
        if (leaf.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^2];
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(leaf.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "tattoo" : cleaned;
    }

    /// <summary>The material suffix of a body path — everything from the last underscore on. Null when the
    /// path names no body material.</summary>
    internal static string? SuffixOf(string? bodyMaterialPath)
    {
        if (string.IsNullOrWhiteSpace(bodyMaterialPath)) return null;
        var leaf = Path.GetFileName(bodyMaterialPath);
        int at = leaf.LastIndexOf('_');
        return at < 0 ? null : leaf[at..];
    }

    // ── Option names ─────────────────────────────────────────────────────────

    /// <summary>
    /// The option that puts the glow on. Unqualified when the pack has one picture, because "Glow tattoo"
    /// is what it is; qualified by the texture's own name when it has several, because then the choice
    /// between them is the point.
    /// </summary>
    internal static string GlowOptionName(TexturePlan plan, bool qualified)
        => qualified
            ? string.Format(Loc.Localize("Import.Luminis.Option.GlowOf.Fmt", "{0} — glow"), plan.Stem)
            : Loc.Localize("Import.Luminis.Option.Glow", "Glow tattoo");

    /// <summary>The option that puts the author's own body texture on underneath.</summary>
    internal static string SkinOptionName(TexturePlan plan, bool qualified)
        => qualified
            ? string.Format(Loc.Localize("Import.Luminis.Option.SkinOf.Fmt", "{0} — author's skin"), plan.Stem)
            : Loc.Localize("Import.Luminis.Option.Skin", "Author's skin");

    // ── Import ───────────────────────────────────────────────────────────────

    /// <summary>Whether <see cref="BodyMaterialCatalog"/> answered from the game data rather than its
    /// hardcoded female-only fallback. Surfaced because a fallback list looks entirely legitimate in the
    /// preview while naming no male body at all.</summary>
    public bool BodiesFromGameData => bodies.FromGameData;

    /// <summary>The material paths an import will claim, for the preview to show before anything is
    /// written.</summary>
    public IReadOnlyList<string> MaterialsFor(ImportPreview preview, string? suffixOverride)
        => bodies.ForSuffix(suffixOverride ?? preview.DefaultSuffix ?? "_bibo.mtrl");

    /// <summary>
    /// A mod written to disk by <see cref="Prepare"/> and waiting for <see cref="Register"/>, or the reason
    /// nothing was written. Split in two for the same reason as the other importers: writing means
    /// decoding and re-encoding several 4K textures, far too long for a draw call, while the Penumbra
    /// registration that follows belongs on the framework thread.
    /// </summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview,
        IReadOnlyList<string> GlowOptions, int Imported, int Skipped);

    /// <summary>
    /// Validate and write the mod to disk. Safe to run off the framework thread; nothing is left behind
    /// when it fails. The result must be handed to <see cref="Register"/> to become a live Penumbra mod.
    /// </summary>
    /// <param name="suffixOverride">
    /// Aim every overlay at this material suffix instead of the one the pack's token implies — the Import
    /// tab's body-target combo. Null takes the resolved default.
    /// </param>
    public PreparedImport Prepare(
        ImportPreview preview, string modName, string author, bool asTex, string? suffixOverride = null)
    {
        modName = (modName ?? "").Trim();
        author = (author ?? "").Trim();

        PreparedImport Fail(string why) => new(false, why, null, null, [], 0, 0);

        if (string.IsNullOrWhiteSpace(modName))
            return Fail(Loc.Localize("Import.NeedName", "Enter a mod name."));
        if (!preview.AnyImportable)
            return Fail(Loc.Localize("Import.Luminis.Fail.NothingUsable",
                "Nothing in this pack can be imported."));
        if (!File.Exists(preview.SourcePath))
            return Fail(string.Format(Loc.Localize("Import.Luminis.Fail.Gone.Fmt",
                "The pack is no longer there: {0}"), preview.SourcePath));

        var dirName = ModCreationService.Sanitize(modName);
        if (dirName == null)
            return Fail(Loc.Localize("Import.Luminis.Fail.BadName",
                "That mod name has no usable characters — use letters or numbers."));
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Fail(Loc.Localize("Import.Luminis.Fail.Reserved",
                "\"Proteus\" is reserved — choose a different mod name."));

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return Fail(Loc.Localize("Import.Luminis.Fail.NoModDir",
                "Penumbra's mod directory isn't available."));

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return Fail(string.Format(Loc.Localize("Import.Luminis.Fail.Exists.Fmt",
                "A mod folder named \"{0}\" already exists."), dirName));

        var materials = MaterialsFor(preview, suffixOverride);

        List<string> glowOptions;
        try
        {
            glowOptions = WriteMod(root, modName, author, preview, materials, suffixOverride,
                                   asTex ? textureLoader : null, textureLoader.LoadTexBytesAsRgba, log);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] luminis import failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(string.Format(Loc.Localize("Import.Luminis.Fail.Write.Fmt",
                "Failed to write the mod: {0}"), ex.Message));
        }

        // Counted from what WriteMod actually wrote, not from what the preview hoped for. A payload that
        // decoded during Inspect and failed on the write pass is dropped there, and reporting the preview's
        // number would have counted it anyway — including in the case where EVERY one failed, which
        // reported a clean success over an empty option group.
        int imported = glowOptions.Count;
        if (imported == 0)
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(Loc.Localize("Import.Luminis.Fail.NoneWritten",
                "None of this pack's textures could be read on the second pass, so nothing was written."));
        }
        return new(true, "", dirName, preview, glowOptions, imported, preview.Textures.Count - imported);
    }

    /// <summary>
    /// Write the mod files under <paramref name="root"/>: the overlay images, the scroll maps, the Proteus
    /// sidecar, Penumbra's manifest and the option group. Pure filesystem work, no IPC, so it can be
    /// exercised offline against a temp directory. Returns the names of the glow options it wrote, in
    /// group order.
    /// </summary>
    /// <param name="encodeTo">Non-null to write BC7 <c>.tex</c> instead of PNG.</param>
    /// <param name="decode">
    /// Reassembled <c>.tex</c> bytes and a name for the log → its pixels as RGBA8. A delegate rather than
    /// the loader itself, for the same reason <see cref="BuildPreview"/> takes one: it is the only part of
    /// this that needs a live game, and passing it in is what lets the whole write be exercised offline.
    /// </param>
    internal static List<string> WriteMod(
        string root, string modName, string author,
        ImportPreview preview, IReadOnlyList<string> materials, string? suffixOverride,
        TextureLoader? encodeTo,
        Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode,
        IPluginLog? log = null)
    {
        var overlaysDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "overlays");
        var effectsDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir,
                                      SidecarDiscoveryService.EffectsSubdir);
        Directory.CreateDirectory(overlaysDir);
        Directory.CreateDirectory(effectsDir);

        var importable = preview.Importable;
        bool qualified = importable.Count > 1;

        // An override only overrides when the user actually RETARGETED — measured against the value the
        // Import tab seeded its combo with, not against each plan's own suffix.
        //
        // The tab hands a non-null suffix back on every import, so "non-null means retarget" was never the
        // test it looked like. Comparing per-plan was worse than useless on the case it was meant to fix:
        // in a pack mixing chara/bibo with chara/gen3 the gen3 plan differs from the seeded bibo default,
        // so it read as retargeted and had its SourceBodyType overwritten with bibo — declaring gen3 art
        // to already be in bibo space and skipping the very remap that would have made it fit.
        //
        // One decision for the whole write, because that is what it is: either the user left the combo
        // where Proteus put it (every plan keeps the space its own token names, and mixed packs remap
        // correctly) or they aimed it somewhere by hand (everything is declared already in the
        // destination's space, so the remap is a no-op rather than a transfer map for a body they have
        // just said it is not for).
        bool retargeted = suffixOverride != null
                       && !string.Equals(suffixOverride, preview.DefaultSuffix, StringComparison.OrdinalIgnoreCase);

        // Re-decoded rather than carried on the preview: holding every 2048² sheet as RGBA for as long as
        // the preview is on screen costs ~16 MB apiece, and this pass runs off the framework thread where
        // the second decode costs nobody anything.
        var payloads = TexToolsPackage.ReadPayloads(
            preview.SourcePath, importable.ToDictionary(t => t.Offset, t => t.Size));

        // Skin options first, so the author's body paints UNDER the glow. Only ordering within the group
        // decides that, and ResolveActiveOverlays walks the options in the order they are declared.
        var skinOptions = new List<OverlayOption>();
        var glowOptions = new List<OverlayOption>();

        foreach (var plan in importable)
        {
            if (!payloads.TryGetValue(plan.Offset, out var payload) || !payload.IsTexture)
            {
                log?.Warning("[Proteus] luminis import: {0} decoded on preview and not on write — skipped",
                    plan.Label);
                continue;
            }

            if (decode(payload.Tex!, plan.Label) is not { } src)
            {
                log?.Warning("[Proteus] luminis import: {0} could not be decoded — skipped", plan.Label);
                continue;
            }

            var (rgba, w, h) = src;
            string bodyType = retargeted
                ? UVRemapService.InferBodyType(materials.FirstOrDefault() ?? "") ?? plan.BodyType ?? ""
                : plan.BodyType ?? "";

            OverlayDescriptor Base(string diffuse) => new()
            {
                Layer = OverlayLayer.Skin,
                SourceBodyType = string.IsNullOrEmpty(bodyType) ? null : bodyType,
                MaterialGamePaths = [.. materials],
                Diffuse = diffuse,
            };

            // ── the glow ──
            // Coverage is PRESENCE, not intensity. Atramentum Luminis's alpha says how brightly a pixel
            // glows, never how solid it is: the panel is opaque skin either way, and only its emission
            // varies. Mapping alpha straight onto the overlay's own alpha therefore made the sheet's
            // half-lit regions into HALF-TRANSPARENT shells that let real skin through, so they read as a
            // paler black against the fully-lit panels beside them.
            //
            // Multiplied up rather than thresholded so the mask keeps its antialiased outline. The fill
            // values are flat plateaus well below 255 (0 for full, ~128 for half); only the outer edge
            // ramps through the top of the range, so ×8 saturates every interior region to opaque while
            // leaving that edge soft.
            var glow = new byte[rgba.Length];
            Buffer.BlockCopy(rgba, 0, glow, 0, rgba.Length);
            for (int i = 3; i < glow.Length; i += 4)
                glow[i] = (byte)Math.Min(255, (255 - rgba[i]) * CoverageGain);

            // The scroll map carries the COLOUR and, now, the INTENSITY: a coloured _o glows in its own hue
            // per pixel, scaled here by how strongly AL said that pixel should emit. That is the half of
            // the alpha channel coverage no longer carries, and it is what keeps a half-lit panel solid
            // black with a dimmer glow instead of a translucent one. Black where nothing glows at all.
            var scroll = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int lit = 255 - rgba[i + 3];
                scroll[i] = (byte)((rgba[i] * lit + 127) / 255);
                scroll[i + 1] = (byte)((rgba[i + 1] * lit + 127) / 255);
                scroll[i + 2] = (byte)((rgba[i + 2] * lit + 127) / 255);
                scroll[i + 3] = 255;
            }

            var glowFile = Materialize(glow, w, h, overlaysDir, plan.Stem + "_glow", encodeTo);
            var scrollFile = Materialize(scroll, w, h, effectsDir, plan.Stem + "_glow", encodeTo);

            var glowDescriptor = Base("overlays/" + glowFile);

            // Layer AND Shader, stated outright — the pair ColorTableEditor.ApplyMode writes for
            // RenderMode.Glow, which is the mode this is.
            //
            // Leaving them at Skin/null and trusting ShouldPromoteToGear to notice the emissive does move
            // it onto a shell, but promotion only changes the LAYER: the shader then falls through to
            // Shader ?? DefaultGearShader, i.e. plain character.shpk, which has no scroll map at all. The
            // shell rendered as the tattoo's raw art lit as an ordinary surface, with the scroll map
            // silently unused and the row emissive behaving like a flat tint on top — washed-out neon on
            // washed-out black, and no amount of tuning that emissive could have fixed it.
            glowDescriptor.Layer = OverlayLayer.Gear;
            glowDescriptor.Shader = RenderModeInference.GlowShader;
            glowDescriptor.Scroll = scrollFile;
            // Zero, explicitly. The material constants ship at zero and an unset speed would take
            // GearMaterialWriter's own default instead, sliding a tattoo across the skin it is drawn on.
            glowDescriptor.ScrollSpeedX = 0f;
            glowDescriptor.ScrollSpeedY = 0f;
            // One-to-one: the scroll map IS the body sheet, so tiling it would repeat the tattoo.
            glowDescriptor.ScrollTilingX = 1f;
            glowDescriptor.ScrollTilingY = 1f;

            glowOptions.Add(new OverlayOption
            {
                Name = GlowOptionName(plan, qualified),
                Overlays = [glowDescriptor],
                // On the OPTION, never at the top level. Top-level rows are inherited by every option that
                // declares none, so an emissive there would reach the skin option too — and any emissive
                // makes RenderModeInference.HasCloth true, which would promote the author's plain body
                // texture to a gear shell as well.
                ColorTableRows =
                [
                    new ColorTableRowPreset
                    {
                        Row = GlowRow,
                        SubRowA = new ColorTableSubRowPreset
                        {
                            Emissive = GlowGate,
                            // Neutral: the scroll map is COLOURED, so it carries its own hue and a tinted
                            // emissive would only push everything toward that tint.
                            EmissiveColor = RenderModeInference.GlowEmissiveColour,
                            Diffuse = GlowSurfaceColour,
                        },
                    },
                ],
            });

            // ── the author's own skin ──
            var skin = new byte[rgba.Length];
            Buffer.BlockCopy(rgba, 0, skin, 0, rgba.Length);
            for (int i = 3; i < skin.Length; i += 4) skin[i] = 255;

            var skinDescriptor = Base("overlays/" + Materialize(skin, w, h, overlaysDir, plan.Stem + "_skin", encodeTo));
            skinOptions.Add(new OverlayOption
            {
                Name = SkinOptionName(plan, qualified),
                Overlays = [skinDescriptor],
            });
        }

        var options = new List<OverlayOption>();
        options.AddRange(skinOptions);
        options.AddRange(glowOptions);

        var metadata = new ProteusMetadata
        {
            FormatVersion = 1,
            Name = modName,
            Author = author,
            OptionGroups =
            [
                new OverlayOptionGroup { PenumbraGroupName = GroupName, Options = options },
            ],
        };

        var metaJson = JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite);
        PenumbraModMeta.AtomicWrite(
            Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json"), metaJson);

        var description = string.IsNullOrWhiteSpace(preview.Description)
            ? string.Format(Loc.Localize("Import.Luminis.Description.Fmt",
                "Imported from the Atramentum Luminis pack \"{0}\"."),
                Path.GetFileName(preview.SourcePath))
            : preview.Description + "\n\n" + string.Format(Loc.Localize("Import.Luminis.Description.Fmt",
                "Imported from the Atramentum Luminis pack \"{0}\"."),
                Path.GetFileName(preview.SourcePath));

        PenumbraModMeta.AtomicWrite(
            Path.Combine(root, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(modName, author, description, preview.Version, preview.Website));

        // Proteus does the real texture redirection itself at composite time, so the default option would
        // be empty — which Penumbra flags as "changes nothing". Same harmless self-swap the Create tab uses.
        PenumbraModMeta.WriteRedirects(
            root, modName,
            files: new Dictionary<string, string>(),
            swaps: new Dictionary<string, string>
                { [ModCreationService.DummySwapPath] = ModCreationService.DummySwapPath });

        // Every skin option off, the FIRST glow on. The skin options come first, so the glow's bit is its
        // index past them.
        ulong defaults = glowOptions.Count > 0 ? 1UL << skinOptions.Count : 0UL;
        PenumbraModMeta.WriteMultiSelectGroup(
            root, 0, GroupName, [.. options.Select(o => o.Name)], defaults);

        return [.. glowOptions.Select(o => o.Name)];
    }

    /// <summary>Write one RGBA buffer into the sidecar and return its file name.</summary>
    private static string Materialize(
        byte[] rgba, int width, int height, string dir, string stem, TextureLoader? encodeTo)
    {
        if (encodeTo != null
         && encodeTo.WriteTex(rgba, width, height, Path.Combine(dir, stem + ".tex"), TexEncoding.Bc7))
            return stem + ".tex";

        using var stream = File.Create(Path.Combine(dir, stem + ".png"));
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        return stem + ".png";
    }

    // ── Register ─────────────────────────────────────────────────────────────

    /// <summary>The outcome of a registration. Three states, not two: an import can succeed and still need
    /// the user to do something.</summary>
    public readonly record struct ImportResult(bool Ok, bool Warning, string Message);

    /// <summary>
    /// A registration whose mod Penumbra is still loading. Retried from <see cref="Pump"/> on later frames
    /// until Penumbra answers, or the deadline passes.
    /// </summary>
    private sealed record Pending(PreparedImport Prepared, long Deadline, bool Quiet);

    private Pending? pending;

    /// <summary>When the next activation attempt may run. Penumbra IPC is milliseconds a hop and this
    /// would otherwise make two of them on every frame for as long as the wait lasts.</summary>
    private long nextAttempt;

    /// <summary>How often to re-ask while waiting.</summary>
    private const long AttemptIntervalMs = 250;

    /// <summary>
    /// How long to keep asking Penumbra to enable a mod it has not finished loading.
    /// <para/>
    /// Generous because the wait scales with the mod, and this one is big: two 2048² sheets and a scroll
    /// map is ~20 MB of PNG for Penumbra's loader to walk before it will admit the mod exists. Nothing
    /// blocks while this runs — each attempt is one cheap IPC call on a frame that was happening anyway.
    /// </summary>
    private const long ActivateTimeoutMs = 15_000;

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra: add it, enable it in the player's collection,
    /// tick the glow options, open Penumbra to it and recomposite. Must run on the framework thread.
    /// <para/>
    /// Returns null when Penumbra has ACCEPTED the mod but not yet finished loading it, in which case the
    /// caller must keep calling <see cref="Pump"/> each frame until it answers. <c>AddMod</c> is explicitly
    /// documented as "a successful call, not a successful mod load", and enabling a mod Penumbra is still
    /// reading returns <c>ModMissing</c> — which this used to ignore. The mod then sat in the collection
    /// disabled, with its group defaults applied, rendering nothing, while the import reported success.
    /// The bigger the mod the more reliably it happened, which is why the Create tab never showed it.
    /// </summary>
    /// <param name="quiet">Register and nothing else — no Penumbra window, no recomposite, and no waiting,
    /// since no further frames are coming. For the teardown path; see
    /// <see cref="OnionImportService.Register"/> for why that matters.</param>
    public ImportResult? Register(PreparedImport prepared, bool quiet = false)
    {
        pending = null;

        if (!prepared.Ok || prepared.DirName == null || prepared.Preview == null)
            return new(false, false, prepared.Message);

        var dirName = prepared.DirName;

        var ec = penumbra.AddModDirectory(dirName);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] AddMod({0}) -> {1}", dirName, ec);
            var modsRoot = penumbra.GetModDirectory();
            if (!string.IsNullOrEmpty(modsRoot))
                try { Directory.Delete(Path.Combine(modsRoot, dirName), true); } catch { /* best effort */ }
            return new(false, false, string.Format(Loc.Localize("Service.RegisterFailed.Fmt",
                "Wrote the mod, but Penumbra couldn't register it ({0}). Rescan mods in Penumbra."), ec));
        }

        pending = new Pending(prepared, Environment.TickCount64 + ActivateTimeoutMs, quiet);
        nextAttempt = 0;
        if (!quiet) return Pump();

        // Teardown: no more frames to retry on, so take the single attempt HERE. Finish does not enable
        // anything — only Pump does — so handing it straight to Finish left the mod added and switched
        // off while the result said the glow was on. It may well fail anyway, since Penumbra is probably
        // still loading, but an added-and-disabled mod is the worst case rather than the default one.
        var quietColl = penumbra.GetPlayerCollectionId();
        bool quietOn = quietColl.HasValue
                    && penumbra.SetModEnabled(quietColl.Value, dirName, true)
                           is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged;
        return Finish(quietOn);
    }

    /// <summary>
    /// Continue a registration <see cref="Register"/> left pending, at most one Penumbra call per frame.
    /// Null while Penumbra is still loading the mod; the result once it answers or the wait runs out.
    /// Harmless to call with nothing pending.
    /// </summary>
    public ImportResult? Pump()
    {
        if (pending is not { } p) return null;

        var now = Environment.TickCount64;
        if (now < nextAttempt) return null;
        nextAttempt = now + AttemptIntervalMs;

        var dirName = p.Prepared.DirName!;
        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null)
        {
            // Not a reason to wait: no collection is a standing state, not a loading one.
            log.Warning("[Proteus] imported {0}: no player collection — enable it manually", dirName);
            return Finish(false);
        }

        // Ask, then READ BACK — the return code is not enough on its own.
        //
        // A settings write that lands while Penumbra is still building the mod does not survive: the
        // finished mod replaces what was there and comes up on its own defaults, which for a freshly added
        // one means DISABLED with its group defaults applied. The call that made the write reports Success
        // regardless, so trusting it left the mod switched off with its glow option correctly ticked —
        // exactly the state that rendered nothing while the import claimed to have worked. Only the state
        // Penumbra reports afterwards actually settles it, so that is what this waits on.
        penumbra.SetModEnabled(collId.Value, dirName, true);
        if (penumbra.GetModSettings(collId.Value, dirName) is { Enabled: true })
            return Finish(true);

        if (now < p.Deadline) return null;   // still settling — ask again shortly

        log.Warning("[Proteus] imported {0}: Penumbra would not report the mod as enabled within {1}ms",
            dirName, ActivateTimeoutMs);
        return Finish(false);
    }

    /// <summary>
    /// Tick the glow, open Penumbra and report. <paramref name="reachedPenumbra"/> is false when the mod
    /// could not be enabled at all, which makes the selection moot and the result a warning.
    /// </summary>
    private ImportResult Finish(bool reachedPenumbra)
    {
        var p = pending!;
        pending = null;

        var prepared = p.Prepared;
        var dirName = prepared.DirName!;
        bool quiet = p.Quiet;
        bool selectionFailed = !reachedPenumbra;

        var collId = penumbra.GetPlayerCollectionId();
        if (reachedPenumbra && collId.HasValue && prepared.GlowOptions.Count > 0)
        {
            // The group's DefaultSettings only reaches a collection that has never seen this mod, and a
            // re-import into one that has is exactly the case it does not cover. Asserting the selection is
            // what makes the glow live there too.
            var first = prepared.GlowOptions[0];
            penumbra.SetModOption(collId.Value, dirName, GroupName, [first]);

            // Read back, like the enable above, and for the same reason — and because the return code is
            // ambiguous anyway: NothingChanged is the COMMON answer here, since Penumbra applies the
            // group's DefaultSettings when it loads the mod and the option is already what we are asking
            // for. Treating that as a failure told the user to go and tick a box that was ticked in front
            // of them. What the collection actually holds settles it either way.
            var live = penumbra.GetModSettings(collId.Value, dirName);
            bool ticked = live is { } s
                       && s.Options.TryGetValue(GroupName, out var selected)
                       && selected.Contains(first, StringComparer.OrdinalIgnoreCase);
            if (!ticked)
            {
                selectionFailed = true;
                log.Warning("[Proteus] imported {0}: {1}/{2} is still not selected after asking",
                    dirName, GroupName, first);
            }
        }

        if (!quiet)
        {
            penumbra.OpenToMod(dirName);
            compositor.TriggerRecomposite("luminis-imported");
        }

        log.Information("[Proteus] imported Atramentum Luminis pack {0} -> {1} ({2} texture(s), {3} skipped)",
            Path.GetFileName(prepared.Preview!.SourcePath), dirName, prepared.Imported, prepared.Skipped);

        var tail = prepared.Skipped > 0
            ? string.Format(Loc.Localize("Import.Result.SkippedTail.Fmt", " (skipped: {0})"), prepared.Skipped)
            : "";

        if (selectionFailed)
            return new(true, true, string.Format(Loc.Localize("Import.Luminis.Result.NoSelection.Fmt",
                "Imported \"{0}\" — textures: {1}{2}, but Proteus couldn't switch the glow on for you. "
              + "Tick it under \"{3}\" in Penumbra, or nothing will paint."),
                dirName, prepared.Imported, tail, GroupName));

        return new(true, false, string.Format(Loc.Localize("Import.Luminis.Result.Ok.Fmt",
            "Imported \"{0}\" — textures: {1}{2}. The glow is on; the author's own skin is available "
          + "under \"{3}\" in Penumbra and starts off."),
            dirName, prepared.Imported, tail, GroupName));
    }
}
