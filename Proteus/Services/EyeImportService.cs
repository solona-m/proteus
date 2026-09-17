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
/// Turns a plain <c>.zip</c> of loose eye textures into a Penumbra mod whose irises can carry an ANIMATED glow:
/// the textures go in as ordinary redirects, and Proteus adds only a <c>characterscroll</c> shell cut from the
/// wearer's iris to the pack mask's shape, so nothing depends on <c>iris.shpk</c>'s samplers.
/// </summary>
public sealed partial class EyeImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly TextureLoader textureLoader;
    private readonly IrisMaterialCatalog irises;
    private readonly IPluginLog log;

    /// <summary>The Penumbra group the import adds, so the animation can be switched off without losing
    /// the eye textures themselves.</summary>
    public const string GroupName = "Eye glow";

    public EyeImportService(PenumbraBridge penumbra, CompositorService compositor,
        TextureLoader textureLoader, IrisMaterialCatalog irises, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.textureLoader = textureLoader;
        this.irises = irises;
        this.log = log;
    }

    // ── the glow's shape and tuning ──────────────────────────────────────────

    /// <summary>
    /// Which channel of the mask marks the region that glows: RED, the limbal-ring channel glowing eye mods
    /// paint into.
    /// </summary>
    private const int GlowChannel = 0;

    /// <summary>
    /// Scroll speed and tiling for an eye, which are NOT the body's <c>ScrollSettings.Default</c>: across an
    /// iris that is a shimmer, and one slow copy of the pattern is what reads as animation.
    /// </summary>
    private const float ScrollSpeed = 0.02f;
    private const float ScrollTiling = 1f;

    /// <summary>The surface under the glow: black, so the scroll map's own colour is what shows.
    /// characterscroll declares no base texture, so this row colour IS the unlit surface.</summary>
    private const string GlowSurfaceColour = "#000000";

    /// <summary>
    /// The row emissive a fresh import starts at: half of <see cref="RenderModeInference.GlowEmissive"/>,
    /// because the tonemapped result clips to white and washes out the mask's falloff.
    /// </summary>
    private const float GlowEmissive = 0.75f;

    /// <summary>The colour-table row a shell with no <c>_id</c> art samples — SecondSkinService fabricates
    /// an index of (255, 255, 0), which is row pair 16, sub-row A.</summary>
    private const int GlowRow = 16;

    /// <summary>
    /// Below this fraction of lit pixels the mask marks out nothing worth cutting a shell for.
    /// </summary>
    private const float MinGlowFraction = 0.001f;

    /// <summary>
    /// How much of the mask's glow channel the shell is cut to. A taste decision the user makes before the art
    /// is baked.
    /// </summary>
    public enum EyeCutout
    {
        /// <summary>
        /// Everything the channel marks, at an opacity proportional to it, keeping the artist's falloff.
        /// </summary>
        Falloff,

        /// <summary>
        /// Only the shape itself: the top of the channel's range, rescaled to full.
        /// </summary>
        Artwork,
    }

    /// <summary>Where each cutout starts, as a fraction of the mask's own peak.</summary>
    internal static float FloorFor(EyeCutout cutout) => cutout == EyeCutout.Artwork ? CutoutFloor : 0f;

    /// <summary>
    /// Where the <see cref="EyeCutout.Artwork"/> cutout starts, as a fraction of the mask's own peak. The glow
    /// channel is the artist's whole gradient, not a silhouette; relative so a darker-authored pack still works.
    /// </summary>
    private const float CutoutFloor = 0.7f;

    /// <summary>
    /// How much of the sheet has to reach a level before it counts as the mask's peak, so one stray bright
    /// texel does not set the range.
    /// </summary>
    private const float PeakFraction = 0.0005f;

    // ── Preview ──────────────────────────────────────────────────────────────

    /// <summary>One file in the pack and what the import will do with it.</summary>
    public sealed record FilePlan(EyePackage.PackFile File, string? SkipReason)
    {
        public bool Import => SkipReason == null;
        public EyeSlot? Slot => File.Slot;
        public string Name => File.Name;

        /// <summary>The game path it replaces, or null when it is being skipped.</summary>
        public string? GamePath => Import && Slot is { } s ? EyePackage.GamePathFor(s) : null;
    }

    /// <summary>Everything the Import tab renders after Browse, and everything <see cref="Prepare"/>
    /// needs.</summary>
    /// <param name="Fractions">How much of the sheet each cutout covers, or null when there is no mask to
    /// read. Both measured up front so <see cref="Cutout"/> can change without re-decoding.</param>
    /// <param name="FaceId">
    /// The face the glow is cut for: every race's iris material at that id. One face only, because an iris
    /// surface is keyed by face and <c>SecondSkinService.SurfaceKeyOf</c> cuts only an overlay's first surface.
    /// </param>
    public sealed record ImportPreview(
        string SourcePath,
        string Name,
        IReadOnlyList<FilePlan> Files,
        IReadOnlyList<string> IrisMaterials,
        string FaceId,
        bool FaceFromWearer,
        (float Falloff, float Artwork)? Fractions,
        IReadOnlyList<string> Warnings)
    {
        /// <summary>
        /// How much of the mask to cut the shell to; the one thing the user changes on this preview.
        /// </summary>
        public EyeCutout Cutout { get; set; } = EyeCutout.Falloff;

        public IReadOnlyList<FilePlan> Importable => [.. Files.Where(f => f.Import)];

        public bool AnyImportable => Files.Any(f => f.Import);

        /// <summary>How much of the sheet a given cutout would cover.</summary>
        public float? GlowFractionFor(EyeCutout mode)
            => Fractions is not { } f ? null : mode == EyeCutout.Artwork ? f.Artwork : f.Falloff;

        /// <summary>Whether a glow layer can be added at a given cutout — it needs a mask with something
        /// left in its glow channel after the cut, and an iris material to sit on.</summary>
        public bool CanGlowWith(EyeCutout mode)
            => GlowFractionFor(mode) >= MinGlowFraction
            && IrisMaterials.Count > 0
            && Files.Any(f => f.Import && f.Slot == EyeSlot.Mask);

        /// <summary>What the panel shows for the currently selected cutout. The WRITE must not use these; it
        /// takes the cutout as an argument.</summary>
        public float? GlowFraction => GlowFractionFor(Cutout);

        public bool CanGlow => CanGlowWith(Cutout);
    }

    /// <summary>
    /// Read the pack and work out what it carries. Throws <see cref="InvalidDataException"/> when the file
    /// isn't a readable archive. Decodes the MASK only, to measure it.
    /// </summary>
    public ImportPreview Inspect(string zipPath)
    {
        var (face, fromWearer) = WearerFace();
        var forFace = irises.All()
            .Where(p => p.Contains($"/obj/face/{face}/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return BuildPreview(EyePackage.Read(zipPath), forFace, face, fromWearer, Measure);
    }

    /// <summary>The face id the character is drawing, or the game's first face when they aren't drawn
    /// yet. See <see cref="ImportPreview.FaceId"/> for why exactly one is chosen.</summary>
    private (string Face, bool FromWearer) WearerFace()
    {
        var loaded = penumbra.GetActivePlayerMaterialPaths();
        var iris = loaded?.FirstOrDefault(p =>
            p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase)
         && p.Contains("_iri", StringComparison.OrdinalIgnoreCase));
        return FaceIdOf(iris) is { } id ? (id, true) : (DefaultFaceId, false);
    }

    /// <summary>The face folder in a material path — <c>f0001</c> — or null when it names none.</summary>
    internal static string? FaceIdOf(string? mtrlGamePath)
    {
        if (string.IsNullOrEmpty(mtrlGamePath)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            mtrlGamePath, @"/obj/face/(f\d+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>What to aim at when the character isn't drawn — the face every race has.</summary>
    internal const string DefaultFaceId = "f0001";

    /// <summary>
    /// How much of the sheet each cutout would cover, or null when the mask will not decode. The write's own
    /// computation, both from one decode.
    /// </summary>
    private (float Falloff, float Artwork)? Measure(EyePackage.PackFile file, string zipPath)
    {
        var decoded = Decode(EyePackage.ReadEntry(zipPath, file.Entry), file.Name, textureLoader, log);
        if (decoded is not { } d || d.Width <= 0 || d.Height <= 0) return null;
        return (Cutout(d.Rgba, EyeCutout.Falloff).Fraction, Cutout(d.Rgba, EyeCutout.Artwork).Fraction);
    }

    /// <summary>
    /// The shell's coverage from the mask's glow channel: everything at or below <see cref="FloorFor"/> of the
    /// mask's peak is dropped and the rest rescaled to full. Returns the per-pixel coverage and its sheet fraction.
    /// </summary>
    internal static (byte[] Coverage, float Fraction) Cutout(byte[] rgba, EyeCutout mode)
    {
        long pixels = rgba.LongLength / 4;
        if (pixels == 0) return ([], 0f);

        // The peak: the highest level at least PeakFraction of the sheet reaches, walking down from 255.
        var histogram = new long[256];
        for (long i = GlowChannel; i < rgba.LongLength; i += 4) histogram[rgba[i]]++;

        long need = Math.Max(1, (long)(pixels * PeakFraction));
        int peak = 255;
        for (long seen = 0; peak > 0; peak--)
        {
            seen += histogram[peak];
            if (seen >= need) break;
        }

        int floor = (int)(peak * FloorFor(mode));
        int span = Math.Max(1, peak - floor);

        var coverage = new byte[pixels];
        long lit = 0;
        for (long p = 0; p < pixels; p++)
        {
            int v = rgba[p * 4 + GlowChannel];
            int c = v <= floor ? 0 : Math.Min(255, (v - floor) * 255 / span);
            coverage[p] = (byte)c;
            if (c > 8) lit++;
        }
        return (coverage, (float)((double)lit / pixels));
    }

    /// <summary>
    /// Classify an already-read pack. The whole of <see cref="Inspect"/> minus the parts that need a live
    /// game — the archive read, the iris probe and the decoder — so it can be exercised offline.
    /// </summary>
    internal static ImportPreview BuildPreview(
        EyePackage.Contents pack,
        IReadOnlyList<string> irisMaterials,
        string faceId,
        bool faceFromWearer,
        Func<EyePackage.PackFile, string, (float Falloff, float Artwork)?> measure)
    {
        var warnings = new List<string>();
        var plans = new List<FilePlan>();

        bool eyes = EyePackage.LooksLikeEyes(pack);
        var claimed = new HashSet<EyeSlot>();

        foreach (var f in pack.Files)
        {
            if (f.Slot is not { } slot)
            {
                plans.Add(new FilePlan(f, Loc.Localize("Import.Eye.Skip.Unknown",
                    "its name doesn't end in a texture kind Proteus knows (base, mask or norm).")));
                continue;
            }
            if (!eyes)
            {
                plans.Add(new FilePlan(f, Loc.Localize("Import.Eye.Skip.NotEyes",
                    "this archive's names don't identify it as an eye pack, and Proteus won't point loose "
                  + "textures at your irises on a guess.")));
                continue;
            }
            if (!claimed.Add(slot))
            {
                plans.Add(new FilePlan(f, string.Format(Loc.Localize("Import.Eye.Skip.Duplicate.Fmt",
                    "another file in this archive is already the {0} texture."), slot)));
                continue;
            }
            plans.Add(new FilePlan(f, null));
        }

        var mask = plans.FirstOrDefault(p => p.Import && p.Slot == EyeSlot.Mask);
        var fractions = mask == null ? null : measure(mask.File, pack.Path);
        // Falloff is the most permissive cutout, so a pack failing these checks fails on either setting.
        float? glow = fractions?.Falloff;

        if (!plans.Any(p => p.Import))
            warnings.Add(Loc.Localize("Import.Eye.Warn.NothingImportable",
                "Nothing in this archive can be imported — see the reasons above."));
        else if (mask == null)
            warnings.Add(Loc.Localize("Import.Eye.Warn.NoMask",
                "This pack ships no mask texture, so there is no shape for the animation to fill. The eye "
              + "textures are still imported; only the glow is left out."));
        else if (glow == null)
            warnings.Add(Loc.Localize("Import.Eye.Warn.MaskUnreadable",
                "The mask texture couldn't be decoded, so Proteus can't tell what should glow. The eye "
              + "textures are still imported; only the glow is left out."));
        else if (glow < MinGlowFraction)
            warnings.Add(Loc.Localize("Import.Eye.Warn.NoGlowRegion",
                "The mask's red channel is empty, so nothing in this pack is marked as glowing. That "
              + "channel is where the game reads the limbal ring from, and where a glowing eye mod puts "
              + "its shape. The eye textures are still imported; only the glow is left out."));

        if (irisMaterials.Count == 0 && plans.Any(p => p.Import))
            warnings.Add(Loc.Localize("Import.Eye.Warn.NoIrisMaterials",
                "Proteus couldn't read the game's face list, so it has no iris material to put the glow "
              + "on. The eye textures are still imported; only the glow is left out."));

        // The face is baked in (a shell is cut per face), so a guessed one has to be said.
        if (!faceFromWearer && plans.Any(p => p.Import) && irisMaterials.Count > 0)
            warnings.Add(string.Format(Loc.Localize("Import.Eye.Warn.FaceGuessed.Fmt",
                "Your character isn't drawn, so the glow is being cut for face {0}. If you wear a "
              + "different face, import this again once you're in game."), faceId));

        return new ImportPreview(
            pack.Path, pack.Name, plans, irisMaterials, faceId, faceFromWearer, fractions, warnings);
    }

    /// <summary>
    /// RGBA8 for one of the pack's images, whatever container it arrived in. Null when it will not decode.
    /// </summary>
    internal static (byte[] Rgba, int Width, int Height)? Decode(
        byte[] bytes, string name, TextureLoader loader, IPluginLog? log)
    {
        try
        {
            if (name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                return loader.LoadTexBytesAsRgba(bytes, name) is { } t ? (t.rgba, t.width, t.height) : null;

            // DDS has no in-memory reader (LoadDdsAsRgba takes a path), so it goes through a temp file.
            if (name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "proteus-eye-" + Guid.NewGuid().ToString("N") + ".dds");
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    return loader.LoadDdsAsRgba(tmp) is { } d ? (d.rgba, d.width, d.height) : null;
                }
                finally { try { File.Delete(tmp); } catch { /* best effort */ } }
            }

            var img = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            return img?.Data == null || img.Width <= 0 || img.Height <= 0
                ? null
                : (img.Data, img.Width, img.Height);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] eye import: {0} could not be decoded", name);
            return null;
        }
    }

    // ── Import ───────────────────────────────────────────────────────────────

    /// <summary>Whether the iris catalogue answered from the game data rather than its hardcoded pair.</summary>
    public bool IrisesFromGameData => irises.FromGameData;

    /// <summary>A mod written to disk by <see cref="Prepare"/> and waiting for <see cref="Register"/>, or
    /// the reason nothing was written.</summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview, bool Glow, int Imported, int Skipped);

    /// <summary>
    /// Validate and write the mod to disk. Safe off the framework thread; nothing is left behind when it
    /// fails. The result must be handed to <see cref="Register"/> to become a live Penumbra mod.
    /// </summary>
    /// <param name="cutout">
    /// Snapshotted by the caller before the write starts: <see cref="ImportPreview.Cutout"/> is mutable and
    /// must not change mid-write on a pool thread.
    /// </param>
    public PreparedImport Prepare(ImportPreview preview, string modName, string author, EyeCutout cutout)
    {
        modName = (modName ?? "").Trim();
        author = (author ?? "").Trim();

        PreparedImport Fail(string why) => new(false, why, null, null, false, 0, 0);

        if (string.IsNullOrWhiteSpace(modName))
            return Fail(Loc.Localize("Import.NeedName", "Enter a mod name."));
        if (!preview.AnyImportable)
            return Fail(Loc.Localize("Import.Eye.Fail.NothingUsable",
                "Nothing in this archive can be imported."));
        if (!File.Exists(preview.SourcePath))
            return Fail(string.Format(Loc.Localize("Import.Eye.Fail.Gone.Fmt",
                "The archive is no longer there: {0}"), preview.SourcePath));

        var dirName = ModCreationService.Sanitize(modName);
        if (dirName == null)
            return Fail(Loc.Localize("Import.Eye.Fail.BadName",
                "That mod name has no usable characters — use letters or numbers."));
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Fail(Loc.Localize("Import.Eye.Fail.Reserved",
                "\"Proteus\" is reserved — choose a different mod name."));

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return Fail(Loc.Localize("Import.Eye.Fail.NoModDir",
                "Penumbra's mod directory isn't available."));

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return Fail(string.Format(Loc.Localize("Import.Eye.Fail.Exists.Fmt",
                "A mod folder named \"{0}\" already exists."), dirName));

        bool glow;
        int written;
        try
        {
            (written, glow) = WriteMod(root, modName, author, preview, cutout, textureLoader,
                                       (b, n) => Decode(b, n, textureLoader, log), log);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] eye import failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(string.Format(Loc.Localize("Import.Eye.Fail.Write.Fmt",
                "Failed to write the mod: {0}"), ex.Message));
        }

        if (written == 0)
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(Loc.Localize("Import.Eye.Fail.NoneWritten",
                "None of this archive's textures could be read, so nothing was written."));
        }

        return new(true, "", dirName, preview, glow, written, preview.Files.Count - written);
    }

    /// <summary>
    /// Write the mod under <paramref name="root"/>: the eye textures as Penumbra redirects and, when the mask
    /// marks a region, the Proteus sidecar with the animated-glow overlay. Filesystem only. Returns how many
    /// textures landed and whether a glow layer was written.
    /// </summary>
    /// <param name="encoder">Writes the <c>.tex</c> files. Null writes nothing and reports zero.</param>
    /// <param name="decode">Image bytes and a name → RGBA8. Passed in so the write can run offline.</param>
    internal static (int Written, bool Glow) WriteMod(
        string root, string modName, string author, ImportPreview preview, EyeCutout cutout,
        TextureLoader? encoder,
        Func<byte[], string, (byte[] Rgba, int Width, int Height)?> decode,
        IPluginLog? log = null)
    {
        return new EyeModWrite(root, modName, author, preview, cutout, encoder, decode, log).Run();
    }

    /// <summary>A copy with every pixel opaque; a scroll map's alpha means nothing to the shader.</summary>
    private static byte[] Opaque(byte[] rgba)
    {
        var copy = (byte[])rgba.Clone();
        for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
        return copy;
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

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra. Must run on the framework thread. Enabling is READ
    /// BACK and retried (see <see cref="LuminisImportService.Pump"/>); returns null while that settles, and the
    /// caller then calls <see cref="Pump"/> each frame.
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
        return quiet ? Finish(TryEnable(dirName)) : Pump();
    }

    /// <summary>A registration whose mod Penumbra is still loading.</summary>
    private sealed record Pending(PreparedImport Prepared, long Deadline, bool Quiet);

    private Pending? pending;
    private long nextAttempt;

    private const long ActivateTimeoutMs = 15_000;
    private const long AttemptIntervalMs = 250;

    /// <summary>Ask Penumbra to enable the mod, then read the state back. True only when it agrees.</summary>
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
        if (now < p.Deadline) return null;

        log.Warning("[Proteus] imported {0}: Penumbra would not report the mod as enabled within {1}ms",
            dirName, ActivateTimeoutMs);
        return Finish(false);
    }

    private ImportResult Finish(bool enabled)
    {
        var p = pending!;
        pending = null;
        var prepared = p.Prepared;
        var dirName = prepared.DirName!;
        bool quiet = p.Quiet;

        if (!quiet)
        {
            penumbra.OpenToMod(dirName);
            compositor.TriggerRecomposite("eye-imported");
        }

        log.Information("[Proteus] imported eye pack {0} -> {1} ({2} texture(s), glow={3}){4}",
            Path.GetFileName(prepared.Preview!.SourcePath), dirName, prepared.Imported, prepared.Glow,
            quiet ? " [quiet: plugin unloading]" : "");

        if (!enabled)
            return new(true, true, string.Format(Loc.Localize("Import.Eye.Result.NotEnabled.Fmt",
                "Imported \"{0}\" — textures: {1}. Penumbra hasn't switched it on yet; tick it in "
              + "Penumbra's mod list."), dirName, prepared.Imported));

        if (!prepared.Glow)
            return new(true, true, string.Format(Loc.Localize("Import.Eye.Result.NoGlow.Fmt",
                "Imported \"{0}\" — textures: {1}, but with no animated glow. See the reasons above; the "
              + "eyes themselves are in and working."), dirName, prepared.Imported));

        return new(true, false, string.Format(Loc.Localize("Import.Eye.Result.Ok.Fmt",
            "Imported \"{0}\" — textures: {1}, with an animated glow on the shape the mask marks out. "
          + "Tune it in Colors, or switch it off under \"{2}\" in Penumbra."),
            dirName, prepared.Imported, GroupName));
    }
}
