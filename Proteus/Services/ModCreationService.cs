using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string DummySwapPath =
        "chara/monster/m8030/obj/body/b0001/material/v0001/mt_m8030b0001_a.mtrl";

    public ModCreationService(PenumbraBridge penumbra, CompositorService compositor, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
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

        var bodyMats = loaded
            .Where(p => p.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)
                     && p.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)
                     && UVRemapService.InferBodyType(p) != null)
            .ToList();

        // A character usually has ONE real body, but a vanilla (gen2) skin material can ride along —
        // gear that exposes skin carries its own mt_…b….a.mtrl. If the wearer is on bibo/gen3, that
        // vanilla one is NOT the body they want the overlay on, so rank the modded bodies first.
        var chosen = bodyMats
            .OrderBy(p => UVRemapService.InferBodyType(p) == "gen2" ? 1 : 0)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        // Only when we actually resolve one — the Create tab polls this until the character is drawn, so
        // logging the empty case every frame would flood the log.
        if (chosen != null)
            log.Information("[Proteus] create: body materials [{0}] -> {1}", string.Join(", ", bodyMats), chosen);
        return chosen;
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

        if (string.IsNullOrWhiteSpace(modName)) return new(false, "Enter a mod name.");
        if (string.IsNullOrWhiteSpace(materialTarget)) return new(false, "Enter a material target.");

        var sources = new (string slot, string? src)[]
            { ("diffuse", diffuseSrc), ("mask", maskSrc), ("normal", normalSrc), ("index", indexSrc) };
        if (!sources.Any(s => !string.IsNullOrWhiteSpace(s.src)))
            return new(false, "Pick at least one texture (diffuse, mask, normal or index).");
        foreach (var (slot, src) in sources)
            if (!string.IsNullOrWhiteSpace(src) && !File.Exists(src))
                return new(false, $"The {slot} file no longer exists: {src}");

        var dirName = Sanitize(modName);
        if (dirName == null)
            return new(false, "That mod name has no usable characters — use letters or numbers.");
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return new(false, "\"Proteus\" is reserved — choose a different mod name.");

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return new(false, "Penumbra's mod directory isn't available.");

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return new(false, $"A mod folder named \"{dirName}\" already exists.");

        try
        {
            WriteMod(root, modName, author, materialTarget, diffuseSrc, maskSrc, normalSrc, indexSrc);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] create mod failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return new(false, $"Failed to write the mod: {ex.Message}");
        }

        var ec = penumbra.AddModDirectory(dirName);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] AddMod({0}) -> {1}", dirName, ec);
            // Roll the folder back so the name is free to retry — a half-registered mod on disk would
            // otherwise trip the "already exists" guard on the next attempt.
            try { Directory.Delete(root, true); } catch { /* best effort */ }
            return new(false, $"Wrote the mod, but Penumbra couldn't register it ({ec}). Rescan mods in Penumbra.");
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
        return new(true, $"Created \"{modName}\", enabled it, and opened it in Penumbra.");
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

        // Same options as SidecarDiscoveryService.SaveMetadata — skip null optional fields for clean json.
        var metaJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        File.WriteAllText(Path.Combine(root, "Proteus", "metadata.json"), metaJson);

        // Penumbra's manifest — FileVersion 4, matching CompositorService.EnsureManagedModExists. Since
        // v4 there is no separate default_mod.json; the default redirects live in DefaultData here.
        File.WriteAllText(
            Path.Combine(root, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(modName, author, "Created for Proteus."));

        // Proteus does all the real texture redirection itself (via its managed mod) at composite time, so
        // this default option would otherwise be empty — which Penumbra flags as "changes nothing". A
        // no-op self-swap of a harmless vanilla path registers it as having content. See DummySwapPath.
        PenumbraModMeta.WriteDefaultData(
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
