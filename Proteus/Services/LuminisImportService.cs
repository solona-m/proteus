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
/// Turns an Atramentum Luminis <c>.ttmp2</c> glow-tattoo pack into a Penumbra mod carrying a Proteus sidecar,
/// with no shader replacement. AL reads glow from a diffuse's ALPHA (255 skin, 0 full glow) at virtual paths:
/// <c>255 − alpha</c> becomes the overlay's coverage and the RGB a characterscroll scroll map at speed zero.
/// The author's skin is imported as a separate option. Anything not AL-shaped is SKIPPED with a reason.
/// </summary>
public sealed partial class LuminisImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly ModCreationService modCreation;
    private readonly TextureLoader textureLoader;
    private readonly BodyMaterialCatalog bodies;
    private readonly IPluginLog log;

    /// <summary>
    /// The Penumbra group an imported pack gets: one multi-select group for every option, because discovery
    /// reads a mod's <c>Overlays</c> OR its <c>OptionGroups</c>, never both.
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
    /// Atramentum Luminis body token → the Proteus UV body type its art is painted in, and that body's material
    /// suffix. An unknown token falls back to the wearer's body; see <see cref="ResolveBody"/>.
    /// </summary>
    private static readonly Dictionary<string, (string BodyType, string Suffix)> Bodies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bibo"] = ("bibo", "_bibo.mtrl"),
            ["gen3"] = ("gen3", "_b.mtrl"),
            // Tight & Firm's Gen3: Gen3's UV space and suffix, so its sheets are remapped rather than assumed bibo.
            ["tfgen3"] = ("gen3", "_b.mtrl"),
        };

    /// <summary>
    /// The material suffixes the body-target override offers: every UV space
    /// <see cref="UVRemapService.InferBodyType"/> can name.
    /// </summary>
    public static readonly string[] BodySuffixes = ["_bibo.mtrl", "_b.mtrl", "_a.mtrl", "_eve.mtrl"];

    /// <summary>
    /// Second path segments that belong to the GAME. A path under one is a real redirect, so the pack is an
    /// ordinary TexTools mod, which is reported by name.
    /// </summary>
    private static readonly HashSet<string> VanillaRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "human", "equipment", "accessory", "weapon", "monster", "demihuman",
        "common", "xls", "action", "base_material", "npc",
    };

    /// <summary>
    /// Fraction of a texture that must carry a glow mask before it counts as one (a tattoo can be small).
    /// </summary>
    private const float MinGlowFraction = 0.0001f;

    /// <summary>Above this, the "mask" covers so much of the body that it is more likely a texture with no
    /// alpha at all than a tattoo. Warned about rather than refused: it is legal AL, just unusual.</summary>
    private const float SuspiciousGlowFraction = 0.9f;

    /// <summary>Alpha at or above this is "no glow". Not 255, so a lossily-compressed source still reads
    /// its own flat regions as flat.</summary>
    private const int OpaqueAlpha = 250;

    /// <summary>
    /// How hard to push the inverted mask toward opaque for coverage: saturates the flat interior plateaus
    /// without hardening the ramped outline.
    /// </summary>
    private const int CoverageGain = 8;

    /// <summary>
    /// What an imported glow row asks of the scene light: everything, since AL tattoos were dark-only; glow and
    /// surface both fade. Unlike <see cref="EmissiveSkinImportService"/>, whose shader burned in daylight too.
    /// </summary>
    private const float GlowLightResponse = 1f;

    // ── Preview ──────────────────────────────────────────────────────────────

    /// <summary>One texture the pack ships, and what the import decided to do with it.</summary>
    /// <param name="Paths">Every manifest path backed by this payload; AL aliases one texture to several.</param>
    /// <param name="Stem">Filename-safe name for the written files and the option labels.</param>
    /// <param name="FromWearer">
    /// The body was resolved from the character rather than from <see cref="Bodies"/>, which is less
    /// trustworthy: it says what the wearer has on, not what the artist painted for.
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
        /// Textures that will be imported, in manifest order. Cached: the panel asks every frame.
        /// </summary>
        public IReadOnlyList<TexturePlan> Importable
            => importable ??= [.. Textures.Where(t => t.Import)];

        /// <summary>The suffix the import will target unless the user overrides it. Null only when
        /// nothing is importable.</summary>
        public string? DefaultSuffix => Importable.FirstOrDefault()?.Suffix;
    }

    /// <summary>
    /// Read the pack and work out what it carries. Throws <see cref="InvalidDataException"/> when the file
    /// isn't a readable modpack. Decodes every candidate texture (costly, deliberately) but keeps only the
    /// measurements.
    /// </summary>
    public ImportPreview Inspect(string ttmpPath)
    {
        // The worn body, for tokens the table does not know; resolved here so the classifier stays pure.
        var body = modCreation.DetectBodyMaterial() ?? modCreation.CachedBodyMaterial();
        return BuildPreview(ttmpPath, TexToolsPackage.Read(ttmpPath), body, DecodeAlpha);

        // Only alpha is wanted, but a BC-compressed texture must be decoded whole to reach it.
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
        return new LuminisPreview(ttmpPath, pack, wearerBody, measure).Run();
    }

    /// <summary>
    /// Which material this token's art belongs on, and which UV space to declare it in. A known token answers
    /// from the table (remapped for a different wearer body); an unknown one declares the wearer's own space,
    /// so the art paints one-to-one without a wrong transfer map.
    /// </summary>
    internal static (string? BodyType, string? Suffix, bool FromWearer) ResolveBody(
        string token, string? wearerType, string? wearerSuffix)
    {
        if (Bodies.TryGetValue(token, out var known)) return (known.BodyType, known.Suffix, false);
        if (wearerSuffix == null) return (null, null, false);
        return (wearerType, wearerSuffix, true);
    }

    /// <summary>
    /// The Atramentum Luminis body token in a virtual path, or null when the path is a real game one. AL uses
    /// <c>chara/&lt;token&gt;/&lt;name&gt;.tex</c> and <c>chara/&lt;token&gt;_&lt;tag&gt;.tex</c>; the second
    /// form's token is the longest KNOWN prefix, else everything before the first underscore.
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
    /// A filename-safe name for one payload, from the first path that names it, minus AL's trailing <c>_d</c>.
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
    /// The option that puts the glow on; qualified by the texture's name when the pack has several.
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
    /// hardcoded female-only fallback.</summary>
    public bool BodiesFromGameData => bodies.FromGameData;

    /// <summary>The material paths an import will claim, for the preview to show before anything is
    /// written.</summary>
    public IReadOnlyList<string> MaterialsFor(ImportPreview preview, string? suffixOverride)
        => bodies.ForSuffix(suffixOverride ?? preview.DefaultSuffix ?? "_bibo.mtrl");

    /// <summary>
    /// A mod written to disk by <see cref="Prepare"/> and waiting for <see cref="Register"/>, or the reason
    /// nothing was written. Writing is too slow for a draw call; registration belongs on the framework thread.
    /// </summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview,
        IReadOnlyList<string> DefaultOptions, int Imported, int Skipped);

    /// <summary>
    /// What <see cref="WriteMod"/> put on disk. <paramref name="Glow"/> is every glow option written, in group
    /// order (the imported count). <paramref name="DefaultOn"/> is what a fresh install wears, which
    /// <see cref="Finish"/> asserts because DefaultSettings only reach a collection new to the mod.
    /// </summary>
    internal sealed record WrittenOptions(List<string> Glow, List<string> DefaultOn);

    /// <summary>
    /// Validate and write the mod to disk. Safe to run off the framework thread; nothing is left behind
    /// when it fails. The result must be handed to <see cref="Register"/> to become a live Penumbra mod.
    /// </summary>
    /// <param name="suffixOverride">
    /// Aim every overlay at this material suffix instead of the resolved one. Null takes the default.
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

        WrittenOptions written;
        try
        {
            written = WriteMod(root, modName, author, preview, materials, suffixOverride,
                               asTex ? textureLoader : null, textureLoader.LoadTexBytesAsRgba, log);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] luminis import failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(string.Format(Loc.Localize("Import.Luminis.Fail.Write.Fmt",
                "Failed to write the mod: {0}"), ex.Message));
        }

        // Counted from what WriteMod actually wrote: a payload can fail on the write pass after decoding in Inspect.
        int imported = written.Glow.Count;
        if (imported == 0)
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(Loc.Localize("Import.Luminis.Fail.NoneWritten",
                "None of this pack's textures could be read on the second pass, so nothing was written."));
        }
        return new(true, "", dirName, preview, written.DefaultOn, imported,
                   preview.Textures.Count - imported);
    }

    /// <summary>
    /// Write the mod files under <paramref name="root"/>: overlay images, scroll maps, the Proteus sidecar,
    /// Penumbra's manifest and the option group. Filesystem only, so it can run offline.
    /// </summary>
    /// <param name="encodeTo">Non-null to write BC7 <c>.tex</c> instead of PNG.</param>
    /// <param name="decode">
    /// Reassembled <c>.tex</c> bytes and a name for the log → its pixels as RGBA8; a delegate so the write
    /// needs no live game.
    /// </param>
    internal static WrittenOptions WriteMod(
        string root, string modName, string author,
        ImportPreview preview, IReadOnlyList<string> materials, string? suffixOverride,
        TextureLoader? encodeTo,
        Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode,
        IPluginLog? log = null)
    {
        return new LuminisModWrite(root, modName, author, preview, materials, suffixOverride, encodeTo, decode, log).Run();
    }

    /// <summary>
    /// Atramentum Luminis's alpha read as ordinary intensity (0 dark, 255 lit), for <see cref="GlowShell"/>.
    /// The inversion is this format's alone and belongs here, not in a shared helper.
    /// </summary>
    private static byte[] Intensity(byte[] rgba)
    {
        var lit = new byte[rgba.Length / 4];
        for (int p = 0; p < lit.Length; p++) lit[p] = (byte)(255 - rgba[p * 4 + 3]);
        return lit;
    }

    /// <summary>The distinct glow plateaus in an Atramentum Luminis mask, brightest first — see
    /// <see cref="GlowShell.Bands"/>.</summary>
    internal static List<int> GlowBands(byte[] rgba, int maxBands = GlowShell.MaxRegions,
                                        float minFraction = GlowShell.MinRegionFraction)
        => GlowShell.Bands(Intensity(rgba), maxBands, minFraction);

    /// <summary>An index texture sending each glowing pixel to its plateau's row — see
    /// <see cref="GlowShell.Index"/>.</summary>
    internal static byte[] BuildGlowIndex(byte[] rgba, IReadOnlyList<int> bands)
        => GlowShell.Index(Intensity(rgba), bands);

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

    /// <summary>The outcome of a registration. An import can succeed and still need the user to act.</summary>
    public readonly record struct ImportResult(bool Ok, bool Warning, string Message);

    /// <summary>
    /// A registration whose mod Penumbra is still loading. Retried from <see cref="Pump"/> on later frames
    /// until Penumbra answers, or the deadline passes.
    /// </summary>
    private sealed record Pending(PreparedImport Prepared, long Deadline, bool Quiet);

    private Pending? pending;

    /// <summary>When the next activation attempt may run, so the wait does not make IPC calls every frame.</summary>
    private long nextAttempt;

    /// <summary>How often to re-ask while waiting.</summary>
    private const long AttemptIntervalMs = 250;

    /// <summary>
    /// How long to keep asking Penumbra to enable a mod it has not finished loading; large mods take a while.
    /// </summary>
    private const long ActivateTimeoutMs = 15_000;

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra: add it, enable it in the player's collection, tick
    /// the glow options, open Penumbra to it and recomposite. Must run on the framework thread. Returns null
    /// while Penumbra has accepted but not finished loading the mod (enabling returns <c>ModMissing</c>); the
    /// caller then calls <see cref="Pump"/> each frame.
    /// </summary>
    /// <param name="quiet">Register only: no Penumbra window, no recomposite, no waiting. For the teardown
    /// path; see <see cref="OnionImportService.Register"/>.</param>
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

        // Teardown: no more frames to retry on, so take the single enable attempt here (Finish enables nothing).
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

        // Ask, then READ BACK: a settings write landing while Penumbra is still building the mod is lost, yet
        // still reports Success.
        penumbra.SetModEnabled(collId.Value, dirName, true);
        if (penumbra.GetModSettings(collId.Value, dirName) is { Enabled: true })
            return Finish(true);

        if (now < p.Deadline) return null;   // still settling — ask again shortly

        log.Warning("[Proteus] imported {0}: Penumbra would not report the mod as enabled within {1}ms",
            dirName, ActivateTimeoutMs);
        return Finish(false);
    }

    /// <summary>
    /// Tick the default options, open Penumbra and report. <paramref name="reachedPenumbra"/> is false
    /// when the mod could not be enabled at all, which makes the selection moot and the result a warning.
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
        var wanted = prepared.DefaultOptions;
        if (reachedPenumbra && collId.HasValue && wanted.Count > 0)
        {
            // DefaultSettings only reach a collection new to this mod, so assert the selection. Both names in one
            // call: this is a multi-select group.
            penumbra.SetModOption(collId.Value, dirName, GroupName, wanted);

            // Read back rather than trusting the return code: NothingChanged is the common, successful answer.
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
            compositor.TriggerRecomposite("luminis-imported");
        }

        log.Information("[Proteus] imported Atramentum Luminis pack {0} -> {1} ({2} texture(s), {3} skipped)",
            Path.GetFileName(prepared.Preview!.SourcePath), dirName, prepared.Imported, prepared.Skipped);

        var tail = prepared.Skipped > 0
            ? string.Format(Loc.Localize("Import.Result.SkippedTail.Fmt", " (skipped: {0})"), prepared.Skipped)
            : "";

        if (selectionFailed)
            return new(true, true, string.Format(Loc.Localize("Import.Luminis.Result.NoSelection.Fmt",
                "Imported \"{0}\" — textures: {1}{2}, but Proteus couldn't switch it on for you. "
              + "Tick it under \"{3}\" in Penumbra, or nothing will paint."),
                dirName, prepared.Imported, tail, GroupName));

        return new(true, false, string.Format(Loc.Localize("Import.Luminis.Result.Ok.Fmt",
            "Imported \"{0}\" — textures: {1}{2}. The glow and the author's own body texture are both on; "
          + "untick the body under \"{3}\" in Penumbra if you want the glow by itself."),
            dirName, prepared.Imported, tail, GroupName));
    }
}
