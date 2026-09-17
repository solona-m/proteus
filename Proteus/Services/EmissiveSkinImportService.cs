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
/// Turns an emissive-skin <c>.pmp</c> (a glowing tattoo for a community skin shader) into a Penumbra mod carrying
/// a Proteus sidecar. Its art sits on VIRTUAL paths; its rewritten body materials need a replaced skin.shpk and
/// are ignored. The emissive's ALPHA (right way up, unlike Luminis) becomes coverage and its mask-scaled RGB a
/// characterscroll scroll map at speed zero. Rows glow unconditionally. Unusable textures are SKIPPED with a reason.
/// </summary>
public sealed partial class EmissiveSkinImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly ModCreationService modCreation;
    private readonly TextureLoader textureLoader;
    private readonly BodyMaterialCatalog bodies;
    private readonly IPluginLog log;

    /// <summary>
    /// The Penumbra group an imported pack gets: one multi-select group for every option, for the reason
    /// <see cref="LuminisImportService.GroupName"/> gives.
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
    /// Fraction of a texture that must carry a mask before it counts as one. Lower than Luminis's: this sheet is
    /// a mask alone, not a body diffuse with a tattoo in it.
    /// </summary>
    private const float MinGlowFraction = 0.00001f;

    /// <summary>Above this the "mask" covers so much of the body that it is more likely a texture with no
    /// real alpha than a tattoo. Warned about rather than refused: it is legal, just unusual.</summary>
    private const float SuspiciousGlowFraction = 0.9f;

    /// <summary>Alpha at or above this counts as glowing. Not 1, so a lossily-compressed source does not
    /// read its own empty regions as a faint all-over haze.</summary>
    private const int LitAlpha = 8;

    /// <summary>
    /// Below this on either side, a texture is not body art: it keeps a pack's small "effect" map, on the same
    /// virtual path shape, from being stretched across the body.
    /// </summary>
    private const int MinArtSize = 64;

    /// <summary>
    /// The largest sheet the import will keep, per side: the composite runs at
    /// <see cref="TextureLoader.BaseTargetSize"/> anyway, so larger masks are resampled once, here.
    /// </summary>
    private const int MaxArtSize = TextureLoader.BaseTargetSize;

    /// <summary>
    /// Whether this pack is one for THIS importer rather than <see cref="ContentImportService"/>, from the
    /// manifest alone. Any MODEL redirect sends it to the content importer; otherwise it needs a virtual texture.
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

    /// <summary>A texture on a path the game can never ask for; the shape is defined by
    /// <see cref="LuminisImportService.TokenOf"/>.</summary>
    private static bool IsVirtualTexture(string gamePath)
        => gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
        && LuminisImportService.TokenOf(PenumbraPackage.Normalize(gamePath)) != null;

    // ── Preview ──────────────────────────────────────────────────────────────

    /// <summary>One texture the pack ships, and what the import decided to do with it.</summary>
    /// <param name="Entry">
    /// The archive entry backing it: the identity of a file here, since several game paths may name one entry.
    /// </param>
    /// <param name="Paths">Every manifest path backed by that entry.</param>
    /// <param name="Stem">Filename-safe name for the written files.</param>
    /// <param name="FromWearer">
    /// The body was resolved from the character rather than from the token table, which is less trustworthy.
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

        /// <summary>Textures that will be imported, in manifest order. Cached: the panel asks every frame.</summary>
        public IReadOnlyList<TexturePlan> Importable
            => importable ??= [.. Textures.Where(t => t.Import)];

        /// <summary>The suffix the import will target unless the user overrides it. Null only when nothing
        /// is importable.</summary>
        public string? DefaultSuffix
            => Importable.FirstOrDefault(t => !t.FromWearer)?.Suffix ?? Importable.FirstOrDefault()?.Suffix;
    }

    /// <summary>
    /// The body the character is actually wearing, for tokens the body table does not know.
    /// <b>Framework thread only</b> (it asks Penumbra), which is why it is split from <see cref="Inspect"/>.
    /// </summary>
    public string? DetectWearerBody()
        => modCreation.DetectBodyMaterial() ?? modCreation.CachedBodyMaterial();

    /// <summary>
    /// Read the pack and work out what it carries. <b>Safe on the thread pool, and belongs there:</b> it decodes
    /// every candidate texture at full resolution. Sheets rejected on their header size are not decoded; only
    /// the measurements are kept.
    /// </summary>
    /// <param name="pack">
    /// The already-parsed manifest the Import tab used to choose this reader (see <see cref="Claims"/>).
    /// </param>
    /// <param name="wearerBody">
    /// What <see cref="DetectWearerBody"/> answered, resolved on the framework thread.
    /// </param>
    public ImportPreview Inspect(string pmpPath, PenumbraPackage.Contents pack, string? wearerBody)
        => BuildPreview(pmpPath, pack, wearerBody, Measure);

    /// <summary>
    /// How big one candidate is and how much of it glows, or null when its bytes will not decode. A sheet too
    /// small to be body art is answered from the <c>.tex</c> header with glow zero, which
    /// <see cref="BuildPreview"/> never reads because it tests dimensions first.
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
    /// A <c>.tex</c>'s dimensions from its header (two <c>ushort</c>s at bytes 8 and 10), without decoding.
    /// Null when the bytes are too short or the header says nothing. Not a general reader.
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

        // One record per archive ENTRY with every game path naming it, so an aliased picture is imported once.
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

        // Stems name the written files, so two payloads may not share one.
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

        // Said once, not per texture.
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
    /// A filename-safe name for one payload: its body token in front of its own leaf. Always qualified, since
    /// these packs name every sheet alike (<c>emissive.tex</c>).
    /// </summary>
    internal static string StemFor(string? token, string gamePath)
    {
        var leaf = LuminisImportService.StemOf(gamePath);
        return string.IsNullOrEmpty(token) ? leaf : token + "_" + leaf;
    }

    // ── Option names ─────────────────────────────────────────────────────────

    /// <summary>
    /// The option that puts the glow on; named for the BODY when the pack ships one tattoo per body layout.
    /// </summary>
    internal static string GlowOptionName(TexturePlan plan, bool qualified)
        => qualified
            ? string.Format(Loc.Localize("Import.Emissive.Option.GlowFor.Fmt", "Glow tattoo — {0}"),
                            plan.Token ?? plan.Stem)
            : Loc.Localize("Import.Emissive.Option.Glow", "Glow tattoo");

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

        // Counted from what WriteMod actually wrote: a payload can fail on the write pass after decoding in Inspect.
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
    /// Write the mod files under <paramref name="root"/>: overlay images, scroll maps, the Proteus sidecar,
    /// Penumbra's manifest and the option group. Filesystem only, so it can run offline.
    /// </summary>
    /// <param name="encodeTo">Non-null to write BC7 <c>.tex</c> instead of PNG.</param>
    /// <param name="decode">
    /// A candidate's <c>.tex</c> bytes and a name for the log → its pixels as RGBA8; a delegate so the write
    /// needs no live game.
    /// </param>
    internal static WrittenOptions WriteMod(
        string root, string modName, string author,
        ImportPreview preview, IReadOnlyList<string> materials, string? suffixOverride,
        TextureLoader? encodeTo,
        Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode,
        IPluginLog? log = null)
    {
        return new EmissiveModWrite(root, modName, author, preview, materials, suffixOverride, encodeTo, decode, log).Run();
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

    /// <summary>The outcome of a registration. An import can succeed and still need the user to act.</summary>
    public readonly record struct ImportResult(bool Ok, bool Warning, string Message);

    /// <summary>A registration whose mod Penumbra is still loading.</summary>
    private sealed record Pending(PreparedImport Prepared, long Deadline, bool Quiet);

    private Pending? pending;
    private long nextAttempt;

    /// <summary>
    /// How long to keep asking Penumbra to enable a mod it has not finished loading; large mods take a while.
    /// </summary>
    private const long ActivateTimeoutMs = 15_000;

    /// <summary>How often to re-ask while waiting, so the wait does not make IPC calls every frame.</summary>
    private const long AttemptIntervalMs = 250;

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra: add it, enable it in the player's collection, tick
    /// the default option, open Penumbra to it and recomposite. Must run on the framework thread. Returns null
    /// while Penumbra is still loading the mod; the caller then calls <see cref="Pump"/> each frame.
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

        // Teardown: no more frames to retry on, so take the single enable attempt here (Finish enables nothing).
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
            // DefaultSettings only reach a collection new to this mod, so assert the selection.
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
