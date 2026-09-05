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
/// Turns an emissive-skin <c>.pmp</c> — a glowing tattoo built for one of the community skin shaders — into
/// a Penumbra mod carrying a Proteus sidecar. The Import tab's engine for the fifth pack format, and the
/// second one that ends in a glowing second skin.
/// <para/>
/// These packs are the Penumbra-native cousins of an Atramentum Luminis modpack, and they are recognised the
/// same way: their art is addressed to VIRTUAL paths under <c>chara/&lt;body&gt;/</c> that no vanilla shader
/// ever asks for. What they redirect for real is the BODY MATERIALS — one <c>.mtrl</c> per race, rewired to
/// name an extra emissive sampler — and that is the half Proteus cannot use, because those materials only
/// mean anything to a replaced <c>skin.shpk</c> the user has to have installed separately. Without it the
/// pack renders nothing; with it, it owns the body material Proteus also wants.
/// <para/>
/// So the materials are ignored and only the art is taken:
/// <list type="bullet">
/// <item>the emissive map's ALPHA is the mask — the right way up, unlike Atramentum Luminis, where 255 is
/// ordinary skin — and becomes the overlay's own alpha, which is what the shell builder reads as coverage.
/// The glowing pixels, and only those, get a second-skin shell cut for them.</item>
/// <item>its RGB scaled by that mask becomes a characterscroll scroll map at speed zero, so a coloured
/// emissive keeps its own hue per pixel. The colour-table row's emissive is only the gate that switches it
/// on.</item>
/// </list>
/// There is no author's-skin option to import, which is the visible difference from
/// <see cref="LuminisImportService"/>: these packs ship no body diffuse of their own — they point the
/// materials they rewrite at somebody else's Bibo+ or Gen3 skin — so the glow is the whole of what is here.
/// The other difference is the light: an Atramentum Luminis tattoo was dark-only and is imported that way,
/// while an emissive sampler burns at noon as well, so these rows are written as an unconditional glow.
/// Both are a dial in Colors afterwards.
/// <para/>
/// Nothing is guessed at silently. A path that is not virtual, a texture with a flat alpha, a sheet too
/// small to be body art — each is SKIPPED with a reason the tab shows.
/// </summary>
public sealed class EmissiveSkinImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly ModCreationService modCreation;
    private readonly TextureLoader textureLoader;
    private readonly BodyMaterialCatalog bodies;
    private readonly IPluginLog log;

    /// <summary>
    /// The Penumbra group an imported pack gets. One multi-select group holding every option, for the reason
    /// <see cref="LuminisImportService.GroupName"/> gives: <c>SidecarDiscoveryService.ResolveActiveOverlays</c>
    /// reads a mod's top-level <c>Overlays</c> OR its <c>OptionGroups</c> and never both.
    /// </summary>
    public const string GroupName = "Skin glow";

    public EmissiveSkinImportService(PenumbraBridge penumbra, CompositorService compositor,
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
    /// How much of a texture must carry a mask before it counts as one. A tattoo can be small — the pack
    /// this was written against paints 0.07% of an 8192² sheet — so this is a hundredth of a percent, low
    /// enough to pass a pair of hip tattoos and high enough to reject a sheet whose alpha is flat.
    /// <para/>
    /// Lower than <c>LuminisImportService</c>'s tenth of a percent, and deliberately: an Atramentum Luminis
    /// sheet is a whole body diffuse with a tattoo inside it, while this is a mask and nothing else, so
    /// there is no reason to expect the art to fill any particular share of it.
    /// </summary>
    private const float MinGlowFraction = 0.00001f;

    /// <summary>Above this the "mask" covers so much of the body that it is more likely a texture with no
    /// real alpha than a tattoo. Warned about rather than refused: it is legal, just unusual.</summary>
    private const float SuspiciousGlowFraction = 0.9f;

    /// <summary>Alpha at or above this counts as glowing. Not 1, so a lossily-compressed source does not
    /// read its own empty regions as a faint all-over haze.</summary>
    private const int LitAlpha = 8;

    /// <summary>
    /// Below this on either side, a texture is not body art.
    /// <para/>
    /// These packs ship a second virtual texture beside the emissive — an "effect" map, 32² in the pack this
    /// was written against, and blank there because the author left the effect off. It is on the same
    /// virtual path shape as the art and would be classified as art. Usually its alpha is empty and the glow
    /// test rejects it anyway; this is what stops one that ISN'T empty from being stretched across a whole
    /// body as though it were a tattoo.
    /// </summary>
    private const int MinArtSize = 64;

    /// <summary>
    /// The largest sheet the import will keep, per side.
    /// <para/>
    /// Not a limitation — it is where the pipeline ends anyway. The composite runs at
    /// <see cref="TextureLoader.BaseTargetSize"/> and writes back at that size, so an 8192² mask (which the
    /// pack this was written against ships) is resampled down the moment it is used. Doing it once, here,
    /// costs the import one bilinear pass and saves the mod 4× its size on disk and 4× the memory on every
    /// composite that reads it back.
    /// </summary>
    private const int MaxArtSize = TextureLoader.BaseTargetSize;

    /// <summary>
    /// Whether this pack is one for THIS importer rather than for <see cref="ContentImportService"/>.
    /// Answered from the manifest alone — no archive entry is decompressed and no pixel is decoded — because
    /// it runs on the frame a file was picked, purely to choose a reader.
    /// <para/>
    /// Two clauses, and the first is the one that decides the split. A pack that redirects a MODEL ships
    /// geometry, which is the content importer's whole subject and something this one cannot place at all;
    /// that a pack with geometry might also carry a virtual texture is not a reason to send it here and lose
    /// the meshes. What is left over — no geometry, but art on a path the game will never ask for — is a
    /// pack Penumbra alone can do nothing useful with.
    /// </summary>
    public static bool Claims(PenumbraPackage.Contents pack)
    {
        bool art = false;
        foreach (var (gamePath, _) in pack.AllFiles)
        {
            if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return false;
            art |= IsVirtualTexture(gamePath);
        }
        return art;
    }

    /// <summary>A texture on a path the game can never ask for — see
    /// <see cref="LuminisImportService.TokenOf"/>, which is where that shape is defined.</summary>
    private static bool IsVirtualTexture(string gamePath)
        => gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
        && LuminisImportService.TokenOf(PenumbraPackage.Normalize(gamePath)) != null;

    // ── Preview ──────────────────────────────────────────────────────────────

    /// <summary>One texture the pack ships, and what the import decided to do with it.</summary>
    /// <param name="Entry">
    /// The archive entry backing it. The identity of a file in this format: a pack may point several game
    /// paths at one entry, and importing one picture twice would write two shells over each other.
    /// </param>
    /// <param name="Paths">Every manifest path backed by that entry.</param>
    /// <param name="Stem">Filename-safe name for the written files.</param>
    /// <param name="FromWearer">
    /// The body was resolved from the character rather than from the token table. Surfaced because the two
    /// are not equally trustworthy: a known token says what the ARTIST painted, the fallback says what the
    /// wearer happens to have on.
    /// </param>
    /// <param name="SkipReason">Null when the texture will be imported; otherwise why it won't be.</param>
    public sealed record TexturePlan(
        string Entry,
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

        /// <summary>Textures that will be imported, in manifest order. Cached, because the Import panel asks
        /// for it every frame the preview is on screen.</summary>
        public IReadOnlyList<TexturePlan> Importable
            => importable ??= [.. Textures.Where(t => t.Import)];

        /// <summary>The suffix the import will target unless the user overrides it. Null only when nothing
        /// is importable.</summary>
        public string? DefaultSuffix
            => Importable.FirstOrDefault(t => !t.FromWearer)?.Suffix ?? Importable.FirstOrDefault()?.Suffix;
    }

    /// <summary>
    /// The body the character is actually wearing, for the tokens the body table has never heard of.
    /// <para/>
    /// <b>Framework thread only</b> — it asks Penumbra which materials are loaded. Split out from
    /// <see cref="Inspect"/> for exactly that reason: the inspection itself is a second of texture decoding
    /// and belongs on the pool, and this one call is the only part of it that does not.
    /// </summary>
    public string? DetectWearerBody()
        => modCreation.DetectBodyMaterial() ?? modCreation.CachedBodyMaterial();

    /// <summary>
    /// Read the pack and work out what it carries.
    /// <para/>
    /// <b>Safe on the thread pool, and belongs there.</b> This DECODES every candidate texture, and here
    /// that is not cheap: these masks are authored at the body's full resolution, and the 8192² sheet the
    /// pack this was written against ships costs a measured 1.06 seconds and a quarter of a gigabyte of
    /// RGBA to look at one channel of. On the frame that picked the file — which is where the other
    /// importers do their reading, and where this used to — that is a visible freeze rather than a hitch.
    /// <para/>
    /// The decode cannot be avoided, only moved: whether a modpack carries a glow mask at all is a question
    /// about its pixels, and it is the question the user opened the preview to have answered. What CAN be
    /// avoided is decoding a sheet that is going to be rejected on its dimensions — see the header read in
    /// <see cref="Measure"/>. Only the MEASUREMENTS are kept; the pixels are dropped, and the write pass
    /// decodes again.
    /// </summary>
    /// <param name="pack">
    /// The already-parsed manifest. The Import tab reads it to CHOOSE this reader (see
    /// <see cref="Claims"/>) and passing it back is what keeps a pick to one archive open rather than two.
    /// </param>
    /// <param name="wearerBody">
    /// What <see cref="DetectWearerBody"/> answered, resolved by the caller before it left the framework
    /// thread.
    /// </param>
    public ImportPreview Inspect(string pmpPath, PenumbraPackage.Contents pack, string? wearerBody)
        => BuildPreview(pmpPath, pack, wearerBody, Measure);

    /// <summary>
    /// How big one candidate is and how much of it glows, or null when its bytes will not decode.
    /// <para/>
    /// The size comes from the <c>.tex</c> HEADER, and a sheet too small to be body art is answered from
    /// that alone — with a glow of zero, which is never read, because <see cref="BuildPreview"/> tests the
    /// dimensions before it tests the glow. That is what stops a shader's palette map from being inflated
    /// to RGBA purely to be told it is 32 pixels across.
    /// </summary>
    private (int Width, int Height, float Glow)? Measure(byte[] tex, string what)
    {
        if (TexSize(tex) is { } s && (s.Width < MinArtSize || s.Height < MinArtSize))
            return (s.Width, s.Height, 0f);

        // Only the alpha channel is wanted, but the whole surface has to be decoded to reach it.
        var decoded = textureLoader.LoadTexBytesAsRgba(tex, what);
        if (decoded is not { } d || d.width <= 0 || d.height <= 0) return null;

        long lit = 0, total = (long)d.width * d.height;
        for (long i = 3; i < d.rgba.LongLength; i += 4)
            if (d.rgba[i] >= LitAlpha) lit++;
        return (d.width, d.height, total == 0 ? 0f : (float)((double)lit / total));
    }

    /// <summary>
    /// A <c>.tex</c>'s dimensions, read off its header without decoding a pixel. Null when the bytes are too
    /// short to hold one or the header says nothing.
    /// <para/>
    /// Width and height are two <c>ushort</c>s at bytes 8 and 10 of the 80-byte header, ahead of everything
    /// the format does that is worth being careful about. Deliberately NOT a general reader: it answers one
    /// question, and a wrong answer costs a texture the size check it would have passed anyway once the
    /// decoder gets to it.
    /// </summary>
    internal static (int Width, int Height)? TexSize(byte[] tex)
    {
        const int HeaderSize = 80;
        if (tex.Length < HeaderSize) return null;
        int width = BitConverter.ToUInt16(tex, 8);
        int height = BitConverter.ToUInt16(tex, 10);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>
    /// Classify an already-read pack. The whole of <see cref="Inspect"/> minus the two things that need a
    /// live game — the body detection and the texture decoder — so it can be exercised offline.
    /// </summary>
    /// <param name="wearerBody">The material path of the body the character is wearing, or null.</param>
    /// <param name="measure">
    /// A candidate's <c>.tex</c> bytes and a name for the log → its size and how much of it glows, or null
    /// when the bytes will not decode.
    /// </param>
    internal static ImportPreview BuildPreview(
        string pmpPath,
        PenumbraPackage.Contents pack,
        string? wearerBody,
        Func<byte[], string, (int Width, int Height, float Glow)?> measure)
    {
        var warnings = new List<string>();
        var wearerSuffix = LuminisImportService.SuffixOf(wearerBody);
        var wearerType = wearerBody == null ? null : UVRemapService.InferBodyType(wearerBody);

        // One record per archive ENTRY, carrying every game path that names it. A pack aliasing one picture
        // to several paths is describing one tattoo, and importing it once per path would stack a shell on
        // top of itself.
        var byEntry = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var (gamePath, entry) in pack.AllFiles)
        {
            if (!IsVirtualTexture(gamePath)) continue;   // a real redirect: the pack's own materials
            if (!byEntry.TryGetValue(entry, out var paths))
            {
                byEntry[entry] = paths = [];
                order.Add(entry);
            }
            if (!paths.Contains(gamePath, StringComparer.OrdinalIgnoreCase)) paths.Add(gamePath);
        }

        // Read in ONE pass over the archive rather than one open per file, the rule
        // <see cref="PenumbraPackage.ReadEntries"/> exists for.
        var payloads = PenumbraPackage.ReadEntries(pack.Path, order);

        // Stems name the written files, so two payloads may not share one — a pack shipping
        // chara/bibo/emissive.tex beside chara/gen3/emissive.tex would otherwise write both over one file.
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var plans = new List<TexturePlan>();
        foreach (var entry in order)
        {
            var paths = byEntry[entry];
            var first = paths[0];
            var token = LuminisImportService.TokenOf(PenumbraPackage.Normalize(first));
            var stem = Unique(StemFor(token, first));

            TexturePlan Skip(string why)
                => new(entry, paths, stem, token, null, null, 0, 0, 0f, false, why);

            // ── which body, and in whose UV space? ──
            var (bodyType, suffix, fromWearer) =
                LuminisImportService.ResolveBody(token!, wearerType, wearerSuffix);
            if (suffix == null)
            {
                plans.Add(Skip(string.Format(Loc.Localize("Import.Emissive.Skip.UnknownBody.Fmt",
                    "Proteus doesn't know the body \"{0}\", and can't ask your character which one they "
                  + "are wearing until they're drawn. Load in and reopen this pack."), token)));
                continue;
            }

            // ── does it actually carry a mask? ──
            if (!payloads.TryGetValue(entry, out var bytes))
            {
                plans.Add(Skip(string.Format(Loc.Localize("Import.Emissive.Skip.Missing.Fmt",
                    "the pack's manifest names it but the archive doesn't contain \"{0}\"."), entry)));
                continue;
            }

            var measured = measure(bytes, first);
            if (measured is not { } m)
            {
                plans.Add(Skip(Loc.Localize("Import.Emissive.Skip.Undecodable",
                    "its pixels couldn't be decoded.")));
                continue;
            }

            if (m.Width < MinArtSize || m.Height < MinArtSize)
            {
                plans.Add(Skip(string.Format(Loc.Localize("Import.Emissive.Skip.TooSmall.Fmt",
                    "it is only {0}×{1}, which is a shader's effect or palette map rather than art painted "
                  + "on a body."), m.Width, m.Height)));
                continue;
            }

            if (m.Glow < MinGlowFraction)
            {
                plans.Add(Skip(Loc.Localize("Import.Emissive.Skip.NoGlow",
                    "its alpha channel is empty, so it marks nothing as glowing. An emissive map puts the "
                  + "shape of the glow there, and one without a shape is not a tattoo.")));
                continue;
            }

            if (m.Glow > SuspiciousGlowFraction)
                warnings.Add(string.Format(Loc.Localize("Import.Emissive.Warn.MostlyGlow.Fmt",
                    "\"{0}\" glows across {1:P0} of the body. That is legal, but it usually means the "
                  + "texture has no real alpha channel — check the result before wearing it out."),
                    first, m.Glow));

            plans.Add(new TexturePlan(entry, paths, stem, token, bodyType, suffix,
                                      m.Width, m.Height, m.Glow, fromWearer, null));
        }

        if (plans.Count == 0 || plans.All(p => !p.Import))
            warnings.Add(Loc.Localize("Import.Emissive.Warn.NothingImportable",
                "Nothing in this pack can be imported — see the reasons above."));

        // Said once, not per texture: the fallback is a guess about which body the art was painted for, and
        // a guess repeated four times reads as four problems.
        if (plans.Any(p => p.Import && p.FromWearer))
            warnings.Add(string.Format(Loc.Localize("Import.Emissive.Warn.FromWearer.Fmt",
                "Proteus doesn't know this pack's body layout, so it will paint the art onto the body "
              + "you're wearing ({0}) exactly as it is, with no resizing. If the pack was painted for a "
              + "different body it will look wrong — change the body target below if you know better."),
                wearerSuffix ?? ""));

        return new ImportPreview(
            pmpPath,
            pack.Name.Trim(),
            pack.Author.Trim(),
            string.IsNullOrWhiteSpace(pack.Description) ? null : pack.Description!.Trim(),
            string.IsNullOrWhiteSpace(pack.Website) ? null : pack.Website!.Trim(),
            string.IsNullOrWhiteSpace(pack.Version) ? null : pack.Version!.Trim(),
            plans,
            warnings,
            wearerSuffix);

        string Unique(string stem)
        {
            if (stems.Add(stem)) return stem;
            for (int i = 2; ; i++)
                if (stems.Add(stem + "_" + i))
                    return stem + "_" + i;
        }
    }

    /// <summary>
    /// A filename-safe name for one payload: its body token in front of its own leaf.
    /// <para/>
    /// Qualified by the token FIRST, where <c>LuminisImportService</c> qualifies only on a collision. These
    /// packs name their sheets for what they are rather than for what is on them — <c>emissive.tex</c>,
    /// every time — so a pack shipping the art for two bodies collides on every file, and "emissive" and
    /// "emissive_2" name nothing anybody could tell apart afterwards.
    /// </summary>
    internal static string StemFor(string? token, string gamePath)
    {
        var leaf = LuminisImportService.StemOf(gamePath);
        return string.IsNullOrEmpty(token) ? leaf : token + "_" + leaf;
    }

    // ── Option names ─────────────────────────────────────────────────────────

    /// <summary>
    /// The option that puts the glow on. Unqualified when the pack ships one picture, because "Glow tattoo"
    /// is what it is; named for the BODY when it ships several, because a pack shipping several is shipping
    /// one tattoo per body layout and which of them fits you is the whole choice.
    /// </summary>
    internal static string GlowOptionName(TexturePlan plan, bool qualified)
        => qualified
            ? string.Format(Loc.Localize("Import.Emissive.Option.GlowFor.Fmt", "Glow tattoo — {0}"),
                            plan.Token ?? plan.Stem)
            : Loc.Localize("Import.Emissive.Option.Glow", "Glow tattoo");

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
    /// nothing was written. Split in two like every other importer here: writing means decoding and
    /// re-encoding several 4K textures, far too long for a draw call, while the Penumbra registration that
    /// follows belongs on the framework thread.
    /// </summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview,
        IReadOnlyList<string> DefaultOptions, int Imported, int Skipped);

    /// <summary>
    /// What <see cref="WriteMod"/> put on disk: every option it wrote, in group order, and the subset a
    /// fresh install wears. <see cref="Finish"/> has to assert the latter separately, because the group's
    /// <c>DefaultSettings</c> only reaches a collection that has never seen this mod.
    /// </summary>
    internal sealed record WrittenOptions(List<string> Options, List<string> DefaultOn);

    /// <summary>
    /// Validate and write the mod to disk. Safe to run off the framework thread; nothing is left behind when
    /// it fails. The result must be handed to <see cref="Register"/> to become a live Penumbra mod.
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
            return Fail(Loc.Localize("Import.Emissive.Fail.NothingUsable",
                "Nothing in this pack can be imported."));
        if (!File.Exists(preview.SourcePath))
            return Fail(string.Format(Loc.Localize("Import.Emissive.Fail.Gone.Fmt",
                "The pack is no longer there: {0}"), preview.SourcePath));

        var dirName = ModCreationService.Sanitize(modName);
        if (dirName == null)
            return Fail(Loc.Localize("Import.Emissive.Fail.BadName",
                "That mod name has no usable characters — use letters or numbers."));
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Fail(Loc.Localize("Import.Emissive.Fail.Reserved",
                "\"Proteus\" is reserved — choose a different mod name."));

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return Fail(Loc.Localize("Import.Emissive.Fail.NoModDir",
                "Penumbra's mod directory isn't available."));

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return Fail(string.Format(Loc.Localize("Import.Emissive.Fail.Exists.Fmt",
                "A mod folder named \"{0}\" already exists."), dirName));

        var materials = MaterialsFor(preview, suffixOverride);

        WrittenOptions written;
        try
        {
            written = WriteMod(root, modName, author, preview, materials, suffixOverride,
                               asTex ? textureLoader : null, textureLoader.LoadTexBytesAsRgba, log);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] emissive-skin import failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(string.Format(Loc.Localize("Import.Emissive.Fail.Write.Fmt",
                "Failed to write the mod: {0}"), ex.Message));
        }

        // Counted from what WriteMod actually wrote, not from what the preview hoped for: a payload that
        // decoded during Inspect and failed on the write pass is dropped there, and reporting the preview's
        // number would count it anyway — including in the case where EVERY one failed.
        int imported = written.Options.Count;
        if (imported == 0)
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(Loc.Localize("Import.Emissive.Fail.NoneWritten",
                "None of this pack's textures could be read on the second pass, so nothing was written."));
        }
        return new(true, "", dirName, preview, written.DefaultOn, imported,
                   preview.Textures.Count - imported);
    }

    /// <summary>
    /// Write the mod files under <paramref name="root"/>: the overlay images, the scroll maps, the Proteus
    /// sidecar, Penumbra's manifest and the option group. Pure filesystem work, no IPC, so it can be
    /// exercised offline against a temp directory.
    /// </summary>
    /// <param name="encodeTo">Non-null to write BC7 <c>.tex</c> instead of PNG.</param>
    /// <param name="decode">
    /// A candidate's <c>.tex</c> bytes and a name for the log → its pixels as RGBA8. A delegate rather than
    /// the loader itself, for the same reason <see cref="BuildPreview"/> takes one: it is the only part of
    /// this that needs a live game.
    /// </param>
    internal static WrittenOptions WriteMod(
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
        // Import tab seeded its combo with, not against each plan's own suffix. See the same guard in
        // LuminisImportService.WriteMod for what testing it per plan silently broke.
        bool retargeted = suffixOverride != null
                       && !string.Equals(suffixOverride, preview.DefaultSuffix, StringComparison.OrdinalIgnoreCase);

        // The UV space the art is being aimed AT, which is what decides the default option below: of several
        // sheets of one tattoo, the one already in the destination's space is the one that needs no
        // resampling, so it is the one to arrive switched on.
        var destinationType = UVRemapService.InferBodyType(materials.FirstOrDefault() ?? "");

        // Re-decoded rather than carried on the preview: holding an 8192² sheet as RGBA for as long as the
        // preview is on screen costs a quarter of a gigabyte, and this pass runs off the framework thread
        // where the second decode costs nobody anything.
        var payloads = PenumbraPackage.ReadEntries(
            preview.SourcePath, importable.Select(t => t.Entry));

        var options = new List<OverlayOption>();
        var wearsBodyType = new List<string?>();   // parallel to options; see the default-selection below

        foreach (var plan in importable)
        {
            if (!payloads.TryGetValue(plan.Entry, out var bytes))
            {
                log?.Warning("[Proteus] emissive import: {0} was in the archive on preview and not on write "
                           + "— skipped", plan.Label);
                continue;
            }

            if (decode(bytes, plan.Label) is not { } src)
            {
                log?.Warning("[Proteus] emissive import: {0} could not be decoded — skipped", plan.Label);
                continue;
            }

            var (rgba, w, h) = Fit(src.Rgba, src.Width, src.Height);
            string bodyType = retargeted
                ? destinationType ?? plan.BodyType ?? ""
                : plan.BodyType ?? "";

            // ── the art ──
            // Coverage is the mask AS IT STANDS. Nothing is inverted and nothing is gained: an emissive
            // map's alpha already says "there is paint here", opaque across the artwork and ramping only at
            // its outline. That is the one place this format differs from Atramentum Luminis, whose alpha is
            // an inverted INTENSITY and needs both (see LuminisImportService.CoverageGain).
            //
            // The RGB rides along untouched — it is the shell's own art, and the colour the author chose.
            var art = new byte[rgba.Length];
            Buffer.BlockCopy(rgba, 0, art, 0, rgba.Length);

            // The scroll map carries the COLOUR and the INTENSITY: a coloured emissive glows in its own hue
            // per pixel, scaled here by how strongly the mask said that pixel should emit. Black where
            // nothing glows at all, so the shell's unlit surface shows through as the row's own black.
            var scroll = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int lit = rgba[i + 3];
                scroll[i] = (byte)((rgba[i] * lit + 127) / 255);
                scroll[i + 1] = (byte)((rgba[i + 1] * lit + 127) / 255);
                scroll[i + 2] = (byte)((rgba[i + 2] * lit + 127) / 255);
                scroll[i + 3] = 255;
            }

            var artFile = Materialize(art, w, h, overlaysDir, plan.Stem, encodeTo);
            var scrollFile = Materialize(scroll, w, h, effectsDir, plan.Stem, encodeTo);

            var descriptor = new OverlayDescriptor
            {
                // Layer AND Shader, stated outright — the pair ColorTableEditor.ApplyMode writes for
                // RenderMode.Glow, which is the mode this is. Leaving them to ShouldPromoteToGear moves the
                // LAYER only: the shader then falls through to plain character.shpk, which has no scroll map
                // at all, and the effect is silently dropped.
                Layer = OverlayLayer.Gear,
                Shader = RenderModeInference.GlowShader,
                SourceBodyType = string.IsNullOrEmpty(bodyType) ? null : bodyType,
                MaterialGamePaths = [.. materials],
                Diffuse = "overlays/" + artFile,
                Scroll = scrollFile,
                // Zero, explicitly. The material constants ship at zero and an unset speed would take
                // GearMaterialWriter's own default instead, sliding a tattoo across the skin it is drawn on.
                ScrollSpeedX = 0f,
                ScrollSpeedY = 0f,
                // One-to-one: the scroll map IS the body sheet, so tiling it would repeat the tattoo.
                ScrollTilingX = 1f,
                ScrollTilingY = 1f,
            };

            // One row per plateau, so each region of the tattoo can later be given its own colour, its own
            // brightness and its own light response. Every row is written IDENTICALLY here on purpose: the
            // regions differ in what they let the user do, not in how the import looks, and the per-pixel
            // intensity that actually separates them is already baked into the scroll map above.
            var intensity = Alpha(rgba);
            var bands = GlowShell.Bands(intensity);
            int rowCount = Math.Max(1, bands.Count);
            if (bands.Count > 1)
            {
                // PNG, not the .tex path Materialize would otherwise take: an index texture is a lookup, and
                // BC7 is lossy enough to move a texel's red across a row boundary — which is why
                // SecondSkinService refuses to compress the id slot either.
                descriptor.Index = "overlays/" + Materialize(
                    GlowShell.Index(intensity, bands), w, h, overlaysDir, plan.Stem + "_id", encodeTo: null);
                log?.Information("[Proteus] emissive import: {0} — {1} glow region(s), rows 1–{1}",
                    plan.Label, bands.Count);
            }

            var rows = new List<ColorTableRowPreset>();
            for (int r = 1; r <= rowCount; r++)
                rows.Add(new ColorTableRowPreset
                {
                    // With an index the rows start at 1 and count up with the plateaus; without one the
                    // shell samples the fabricated (255,255,0), which is row 16.
                    Row = bands.Count > 1 ? r : GlowShell.Row,
                    SubRowA = new ColorTableSubRowPreset
                    {
                        Emissive = GlowShell.Emissive,
                        // Neutral: the scroll map carries its own hue, and a tinted emissive would only push
                        // everything toward that tint.
                        EmissiveColor = RenderModeInference.GlowEmissiveColour,
                        Diffuse = GlowShell.SurfaceColour,
                        // No LightResponse and no HideInLight, which is where this parts company with the
                        // Atramentum Luminis import. That mod's tattoos were dark-only by design; an
                        // emissive sampler on skin.shpk simply adds light, at noon as much as at midnight,
                        // so an unconditional glow is what parity means here. The Colors tab turns it into a
                        // dark-only one in two clicks for anyone who wants that instead.
                    },
                });

            options.Add(new OverlayOption
            {
                Name = GlowOptionName(plan, qualified),
                Overlays = [descriptor],
                // On the OPTION, never at the top level. Top-level rows are inherited by every option that
                // declares none, so these would reach the pack's other body layouts as well.
                ColorTableRows = rows,
            });
            wearsBodyType.Add(plan.BodyType);
        }

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

        PenumbraModMeta.AtomicWrite(
            Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, "metadata.json"),
            JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite));

        var imported = string.Format(Loc.Localize("Import.Emissive.Description.Fmt",
            "Imported from the emissive skin pack \"{0}\"."), Path.GetFileName(preview.SourcePath));
        var description = string.IsNullOrWhiteSpace(preview.Description)
            ? imported
            : preview.Description + "\n\n" + imported;

        PenumbraModMeta.AtomicWrite(
            Path.Combine(root, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(modName, author, description, preview.Version, preview.Website));

        // Proteus does the real texture redirection itself at composite time, so the default option would be
        // empty — which Penumbra flags as "changes nothing". Same harmless self-swap the Create tab uses.
        //
        // The pack's OWN redirects are deliberately not carried over: they are body materials rewired to
        // name an emissive sampler that only a replaced skin.shpk has, and republishing them would put this
        // mod into a fight with Proteus over the very material it is compositing into.
        PenumbraModMeta.WriteRedirects(
            root, modName,
            files: new Dictionary<string, string>(),
            swaps: new Dictionary<string, string>
                { [ModCreationService.DummySwapPath] = ModCreationService.DummySwapPath });

        // Exactly ONE option on, where the Atramentum Luminis import turns on a pair. Several options here
        // are several UV layouts of the SAME tattoo, all aimed at the one body the user picked, so wearing
        // two would stack a shell on its own copy. The one already in the destination's space is the one
        // that needs no resampling; failing that, the first.
        var defaultOn = new List<string>();
        ulong defaults = 0;
        if (options.Count > 0)
        {
            int pick = wearsBodyType.FindIndex(
                t => t != null && string.Equals(t, destinationType, StringComparison.OrdinalIgnoreCase));
            if (pick < 0) pick = 0;
            defaultOn.Add(options[pick].Name);
            defaults |= 1UL << pick;
        }

        PenumbraModMeta.WriteMultiSelectGroup(
            root, 0, GroupName, [.. options.Select(o => o.Name)], defaults);

        return new([.. options.Select(o => o.Name)], defaultOn);
    }

    /// <summary>One byte per pixel: the mask, read straight off the alpha channel. What
    /// <see cref="GlowShell"/> works in.</summary>
    private static byte[] Alpha(byte[] rgba)
    {
        var lit = new byte[rgba.Length / 4];
        for (int p = 0; p < lit.Length; p++) lit[p] = rgba[p * 4 + 3];
        return lit;
    }

    /// <summary>A sheet at no more than <see cref="MaxArtSize"/> a side, resampled if it arrived larger.
    /// Returned unchanged when it already fits, so the common case copies nothing.</summary>
    internal static (byte[] Rgba, int Width, int Height) Fit(byte[] rgba, int width, int height)
    {
        if (width <= MaxArtSize && height <= MaxArtSize) return (rgba, width, height);

        float scale = Math.Min(MaxArtSize / (float)width, MaxArtSize / (float)height);
        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));
        return (UVRemapService.ResizeBilinear(rgba, width, height, w, h), w, h);
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

    /// <summary>A registration whose mod Penumbra is still loading.</summary>
    private sealed record Pending(PreparedImport Prepared, long Deadline, bool Quiet);

    private Pending? pending;
    private long nextAttempt;

    /// <summary>
    /// How long to keep asking Penumbra to enable a mod it has not finished loading. Generous because the
    /// wait scales with the mod, and this one is big: a 4K mask and its scroll map is tens of megabytes for
    /// Penumbra's loader to walk before it will admit the mod exists. Nothing blocks while this runs.
    /// </summary>
    private const long ActivateTimeoutMs = 15_000;

    /// <summary>How often to re-ask while waiting. Penumbra IPC is milliseconds a hop and this would
    /// otherwise make two of them every frame for as long as the wait lasts.</summary>
    private const long AttemptIntervalMs = 250;

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra: add it, enable it in the player's collection,
    /// tick the default option, open Penumbra to it and recomposite. Must run on the framework thread.
    /// <para/>
    /// Returns null while Penumbra has ACCEPTED the mod but not finished loading it, in which case the
    /// caller must keep calling <see cref="Pump"/> each frame until it answers — see
    /// <see cref="LuminisImportService.Pump"/>, which documents at length why a settings write that lands
    /// mid-load reports success and is then thrown away.
    /// </summary>
    /// <param name="quiet">Register and nothing else — no Penumbra window, no recomposite, and no waiting,
    /// since no further frames are coming. Teardown path.</param>
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

        // Teardown takes its single attempt HERE. Finish does not enable anything — only Pump does — so
        // handing it straight to Finish would leave the mod added and switched off while the result said the
        // glow was on.
        return quiet ? Finish(TryEnable(dirName)) : Pump();
    }

    /// <summary>Ask Penumbra to enable the mod, then READ THE STATE BACK. True only when it agrees: the
    /// return code alone says Success for a write that a still-loading mod will discard.</summary>
    private bool TryEnable(string dirName)
    {
        var collId = penumbra.GetPlayerCollectionId();
        if (collId == null) return false;
        penumbra.SetModEnabled(collId.Value, dirName, true);
        return penumbra.GetModSettings(collId.Value, dirName) is { Enabled: true };
    }

    /// <summary>
    /// Continue a registration <see cref="Register"/> left pending, at most one attempt every quarter
    /// second. Null while Penumbra is still loading the mod; the result once it answers or the wait runs
    /// out. Harmless to call with nothing pending.
    /// </summary>
    public ImportResult? Pump()
    {
        if (pending is not { } p) return null;

        var now = Environment.TickCount64;
        if (now < nextAttempt) return null;
        nextAttempt = now + AttemptIntervalMs;

        var dirName = p.Prepared.DirName!;
        if (penumbra.GetPlayerCollectionId() == null)
        {
            // Not a reason to wait: no collection is a standing state, not a loading one.
            log.Warning("[Proteus] imported {0}: no player collection — enable it manually", dirName);
            return Finish(false);
        }

        if (TryEnable(dirName)) return Finish(true);
        if (now < p.Deadline) return null;   // still settling — ask again shortly

        log.Warning("[Proteus] imported {0}: Penumbra would not report the mod as enabled within {1}ms",
            dirName, ActivateTimeoutMs);
        return Finish(false);
    }

    /// <summary>Tick the default option, open Penumbra and report. <paramref name="enabled"/> is false when
    /// the mod could not be switched on at all, which makes the selection moot.</summary>
    private ImportResult Finish(bool enabled)
    {
        var p = pending!;
        pending = null;

        var prepared = p.Prepared;
        var dirName = prepared.DirName!;
        bool quiet = p.Quiet;
        bool selectionFailed = !enabled;

        var collId = penumbra.GetPlayerCollectionId();
        var wanted = prepared.DefaultOptions;
        if (enabled && collId.HasValue && wanted.Count > 0)
        {
            // The group's DefaultSettings only reaches a collection that has never seen this mod, and a
            // re-import into one that has is exactly the case it does not cover.
            penumbra.SetModOption(collId.Value, dirName, GroupName, wanted);

            // Read back, like the enable above. The return code is ambiguous anyway: NothingChanged is the
            // COMMON answer, since Penumbra applies the group's DefaultSettings when it loads the mod and
            // the options are already what we are asking for.
            var live = penumbra.GetModSettings(collId.Value, dirName);
            var selected = live is { } s && s.Options.TryGetValue(GroupName, out var sel)
                ? sel : (IReadOnlyList<string>)[];
            var missing = wanted
                .Where(w => !selected.Contains(w, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count > 0)
            {
                selectionFailed = true;
                log.Warning("[Proteus] imported {0}: {1}/[{2}] is still not selected after asking",
                    dirName, GroupName, string.Join(", ", missing));
            }
        }

        if (!quiet)
        {
            penumbra.OpenToMod(dirName);
            compositor.TriggerRecomposite("emissive-imported");
        }

        log.Information("[Proteus] imported emissive skin pack {0} -> {1} ({2} texture(s), {3} skipped){4}",
            Path.GetFileName(prepared.Preview!.SourcePath), dirName, prepared.Imported, prepared.Skipped,
            quiet ? " [quiet: plugin unloading]" : "");

        var tail = prepared.Skipped > 0
            ? string.Format(Loc.Localize("Import.Result.SkippedTail.Fmt", " (skipped: {0})"), prepared.Skipped)
            : "";

        if (selectionFailed)
            return new(true, true, string.Format(Loc.Localize("Import.Emissive.Result.NoSelection.Fmt",
                "Imported \"{0}\" — textures: {1}{2}, but Proteus couldn't switch it on for you. Tick it "
              + "under \"{3}\" in Penumbra, or nothing will paint."),
                dirName, prepared.Imported, tail, GroupName));

        return new(true, false, string.Format(Loc.Localize("Import.Emissive.Result.Ok.Fmt",
            "Imported \"{0}\" — textures: {1}{2}. The glow is on and needs no shader mod. Recolour or dim "
          + "it in Colors, or switch it off under \"{3}\" in Penumbra."),
            dirName, prepared.Imported, tail, GroupName));
    }
}
