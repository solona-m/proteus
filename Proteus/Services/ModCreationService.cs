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

    /// <summary>
    /// Create the mod on disk, register it with Penumbra, and open the Penumbra UI to it. Returns a
    /// user-facing result; nothing is written when validation fails.
    /// </summary>
    public CreateResult Create(
        string modName, string author, string materialTarget,
        string? diffuseSrc, string? maskSrc, string? normalSrc, string? indexSrc)
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

        try
        {
            WriteMod(root, modName, author, materialTarget, diffuseSrc, maskSrc, normalSrc, indexSrc);
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

        // Enable it in the player's collection so it takes effect immediately, then recomposite so Proteus
        // picks up the new sidecar and paints it. Enabling is best-effort — the mod is still usable if the
        // collection lookup fails, the user just enables it manually.
        var collId = penumbra.GetPlayerCollectionId();
        if (collId.HasValue)
            penumbra.SetModEnabled(collId.Value, dirName, true);
        else
            log.Warning("[Proteus] created {0}: no player collection — enable it manually", dirName);

        penumbra.OpenToMod(dirName);
        compositor.TriggerRecomposite("mod-created");
        log.Information("[Proteus] created mod {0} ({1})", dirName, materialTarget);
        return new(true, string.Format(Loc.Localize("Create.Ok.Fmt",
            "Created \"{0}\", enabled it, and opened it in Penumbra."), modName));
    }

    /// <summary>
    /// Write the mod files under <paramref name="root"/>: the texture copies, the Proteus sidecar
    /// (metadata.json), and Penumbra's meta.json/default_mod.json. Pure filesystem work, no IPC — split
    /// out so it can be exercised offline against a temp directory.
    /// </summary>
    internal static void WriteMod(
        string root, string modName, string author, string materialTarget,
        string? diffuseSrc, string? maskSrc, string? normalSrc, string? indexSrc)
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

        var descriptor = new OverlayDescriptor
        {
            Layer = OverlayLayer.Skin,
            MaterialGamePaths = [materialTarget],
            Diffuse = Copy("diffuse", diffuseSrc),
            Mask = Copy("mask", maskSrc),
            Normal = Copy("normal", normalSrc),
            Index = Copy("index", indexSrc),
        };
        var metadata = new ProteusMetadata
        {
            FormatVersion = 1,
            Name = modName,
            Author = author,
            Overlays = [descriptor],
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
