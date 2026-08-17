using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;
using StbImageSharp;

namespace Proteus.Services;

/// <summary>
/// Turns an Onion <c>.omp</c> overlay pack into a Penumbra mod carrying a Proteus sidecar — the Import
/// tab's engine. Reads the pack with <see cref="OnionPackage"/>, classifies each layer against what
/// Proteus can actually render, and writes the same mod shape <see cref="ModCreationService"/> does, only
/// multi-layer and (for packs shipping several UV layouts) option-gated.
/// <para/>
/// Nothing is guessed at silently. A layer Proteus has no equivalent for — a blend mode other than
/// Normal, an unknown UV layout, a texture slot outside diffuse/normal/mask/index — is SKIPPED with a
/// reason the tab shows, rather than imported as something that would render wrong.
/// </summary>
public sealed class OnionImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly ModCreationService modCreation;
    private readonly TextureLoader textureLoader;
    private readonly BodyMaterialCatalog bodies;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>
    /// The Penumbra group an imported multi-layout pack gets. One option per UV layout, single-select, so
    /// only one layout's art ever composites — Proteus matches overlays by exact material path, and
    /// importing every layout unconditionally would paint the same artwork two or three times over.
    /// </summary>
    public const string LayoutGroupName = "Body UV";

    public OnionImportService(PenumbraBridge penumbra, CompositorService compositor,
        ModCreationService modCreation, TextureLoader textureLoader, BodyMaterialCatalog bodies,
        Configuration config, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.modCreation = modCreation;
        this.textureLoader = textureLoader;
        this.bodies = bodies;
        this.config = config;
        this.log = log;
    }

    // ── Format mapping ───────────────────────────────────────────────────────

    /// <summary>
    /// Onion's <c>Layout</c> token → the Proteus UV body type it means and the material suffix a body of
    /// that type carries. <c>vanilla</c> is Proteus's <c>gen2</c>; <c>eve</c> shares gen3's UV space but
    /// lives at its own <c>_eve</c> material, so it needs a row of its own.
    /// </summary>
    private static readonly Dictionary<string, (string BodyType, string Suffix)> Layouts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bibo"]    = ("bibo", "_bibo.mtrl"),
            ["gen3"]    = ("gen3", "_b.mtrl"),
            ["vanilla"] = ("gen2", "_a.mtrl"),
            ["gen2"]    = ("gen2", "_a.mtrl"),
            ["eve"]     = ("gen3", "_eve.mtrl"),
        };

    /// <summary>Which layout to select when the character's own body doesn't appear in the pack.</summary>
    private static readonly string[] LayoutPreference = ["bibo", "gen3", "eve", "vanilla", "gen2"];

    /// <summary>Onion's <c>Map</c> token → the Proteus overlay slot it fills.</summary>
    private static readonly Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["base"] = "Diffuse", ["diffuse"] = "Diffuse", ["d"] = "Diffuse", ["basecolor"] = "Diffuse",
        ["normal"] = "Normal", ["norm"] = "Normal", ["n"] = "Normal",
        ["mask"] = "Mask", ["multi"] = "Mask", ["m"] = "Mask", ["s"] = "Mask", ["specular"] = "Mask",
        ["id"] = "Index", ["index"] = "Index",
    };

    // ── Preview ──────────────────────────────────────────────────────────────

    /// <summary>One pack layer and what the import decided to do with it.</summary>
    /// <param name="SkipReason">Null when the layer will be imported; otherwise why it won't be.</param>
    public sealed record LayerPlan(
        int Index,
        string File,
        string LayoutToken,
        string MapToken,
        string ModeToken,
        float Opacity,
        long Bytes,
        string? Entry,
        string? BodyType,
        string? Suffix,
        string? Slot,
        string? GeneratedFrom,
        string? SkipReason)
    {
        public bool Import => SkipReason == null;
    }

    /// <summary>
    /// Everything the Import tab renders after Browse, and everything <see cref="Prepare"/> needs.
    /// </summary>
    /// <param name="WearerBodyType">
    /// The UV body type the character is actually wearing ("bibo"/"gen3"/"gen2"), or null when they aren't
    /// drawn yet. Kept because what happens to a layout the pack DOESN'T carry depends on it: bibo↔gen3 is
    /// remapped automatically, but a vanilla body needs the mod's sibling mode raised to AllBodies.
    /// </param>
    /// <param name="DefaultLayoutMatchedBody">
    /// Whether <paramref name="DefaultLayout"/> was chosen because it matches the body the character is
    /// actually wearing, as opposed to falling back to a house preference. The two are indistinguishable
    /// from the string alone, and telling a gen3 wearer that a bibo-only pack "matches the body you're
    /// wearing" sends them looking for the problem everywhere except where it is.
    /// </param>
    public sealed record ImportPreview(
        string SourcePath,
        string Name,
        string Author,
        string? Description,
        string? Website,
        string? Version,
        IReadOnlyList<LayerPlan> Layers,
        IReadOnlyList<string> Warnings,
        string? DefaultLayout,
        string? WearerBodyType,
        bool DefaultLayoutMatchedBody)
    {
        /// <summary>
        /// The import will land on a body the pack wasn't painted for, and Proteus only crosses into
        /// VANILLA UV space when the mod's sibling mode is AllBodies (the default is bibo+gen3 only, see
        /// <c>Configuration.SiblingModeFor</c>). Without that raised, such an import paints nothing at all.
        /// </summary>
        public bool NeedsAllBodies
            => !DefaultLayoutMatchedBody
            && string.Equals(WearerBodyType, "gen2", StringComparison.OrdinalIgnoreCase);

        /// <summary>The distinct UV layouts that will be imported, in paint order.</summary>
        public IReadOnlyList<string> Layouts => Layers.Where(l => l.Import)
            .Select(l => l.LayoutToken).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public bool AnyImportable => Layers.Any(l => l.Import);
    }

    /// <summary>
    /// Parse and classify a pack without writing anything. Throws when the file isn't a readable
    /// <c>.omp</c> — the caller turns that into a message.
    /// </summary>
    public ImportPreview Inspect(string ompPath)
    {
        // The body the character is actually wearing decides which layout option is pre-selected. Resolved
        // here rather than inside the classifier so the classification itself stays pure.
        var body = modCreation.DetectBodyMaterial() ?? modCreation.CachedBodyMaterial();
        return BuildPreview(ompPath, OnionPackage.Read(ompPath),
            body == null ? null : UVRemapService.InferBodyType(body));
    }

    /// <summary>
    /// Classify an already-read pack. The whole of <see cref="Inspect"/> minus the two things that need a
    /// live game — the file read and the body detection — so it can be exercised offline.
    /// </summary>
    /// <param name="preferredBodyType">The wearer's UV body type ("bibo"/"gen3"/"gen2"), or null.</param>
    internal static ImportPreview BuildPreview(string ompPath, OnionPackage.Contents pack, string? preferredBodyType)
    {
        var m = pack.Manifest;
        var warnings = new List<string>();

        // Produced at PARSE time and held on the preview, so a language change while a pack is open leaves
        // these in the old language until it is reopened. Same trade as UVMapDownloadService.StatusMessage.
        if (m.FormatVersion > OnionPackage.KnownFormatVersion)
            warnings.Add(string.Format(Loc.Localize("Import.Warn.FormatVersion.Fmt",
                "The pack declares format version {0}; Proteus has only been checked against {1}. " +
                "Import it and check the result."), m.FormatVersion, OnionPackage.KnownFormatVersion));

        if (m.Groups is { ValueKind: JsonValueKind.Array } g && g.GetArrayLength() > 0)
            warnings.Add(string.Format(Loc.Localize("Import.Warn.OptionGroups.Fmt",
                "The pack has Onion option groups ({0}). Those aren't imported — every layer is brought " +
                "in unconditionally."), g.GetArrayLength()));

        var layers = new List<LayerPlan>();
        // Paint order, not manifest order: Onion states it in Order, and Proteus composites descriptors in
        // the order the metadata lists them.
        int i = 0;
        foreach (var layer in (m.Layers ?? []).OrderBy(l => l.Order))
            layers.Add(Classify(pack, layer, i++));

        if (layers.Any(l => l.GeneratedFrom != null))
            warnings.Add(Loc.Localize("Import.Warn.Generated",
                "Some layouts were generated by Onion from another layout rather than painted. " +
                "They're imported as-is; prefer the authored layout if the result looks soft."));

        if (m.Layers != null && m.Layers.Any(l => l.Races is { ValueKind: JsonValueKind.Array } r && r.GetArrayLength() > 0))
            warnings.Add(Loc.Localize("Import.Warn.RaceFilters",
                "Some layers are restricted to specific races. Proteus doesn't carry race filters, " +
                "so they're imported for every race."));

        if (!layers.Any(l => l.Import))
            warnings.Add(Loc.Localize("Import.Warn.NothingImportable",
                "No layer in this pack can be imported — see the reasons above."));

        var (defaultLayout, matched) = PickDefaultLayout(layers, preferredBodyType);

        return new ImportPreview(
            ompPath,
            string.IsNullOrWhiteSpace(m.Name) ? Path.GetFileNameWithoutExtension(ompPath) : m.Name!.Trim(),
            (m.Author ?? "").Trim(),
            string.IsNullOrWhiteSpace(m.Description) ? null : m.Description!.Trim(),
            string.IsNullOrWhiteSpace(m.Website) ? null : m.Website!.Trim(),
            string.IsNullOrWhiteSpace(m.Version) ? null : m.Version!.Trim(),
            layers,
            warnings,
            defaultLayout,
            preferredBodyType,
            matched);
    }

    private static LayerPlan Classify(OnionPackage.Contents pack, OnionLayer layer, int index)
    {
        var file = layer.File ?? "";
        var layout = (layer.Layout ?? "").Trim();
        var map = (layer.Map ?? "").Trim();
        var mode = string.IsNullOrWhiteSpace(layer.Mode) ? "Normal" : layer.Mode!.Trim();
        var entry = pack.ResolveEntry(file);
        var bytes = entry != null && pack.Entries.TryGetValue(entry, out var n) ? n : 0;
        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);

        string? skip = null;
        string? bodyType = null, suffix = null, slot = null;

        if (entry == null)
            skip = string.IsNullOrWhiteSpace(file)
                ? "the layer names no image file"
                : $"the pack doesn't contain \"{file}\"";
        else if (opacity <= 0f)
            // Onion's way of hiding a layer. Baking that into the alpha would write a full-size image that
            // costs a decode on every composite and paints nothing — the same "would render wrong, so say
            // so" rule the blend modes get.
            skip = "the layer is fully transparent (opacity 0)";
        else if (!Layouts.TryGetValue(layout, out var lay))
            skip = $"unsupported UV layout \"{layout}\"";
        else if (!Maps.TryGetValue(map, out var s))
            skip = $"unsupported texture map \"{map}\"";
        else if (!string.Equals(mode, "Normal", StringComparison.OrdinalIgnoreCase))
            // Proteus composites alpha-over only. Importing a Multiply/Screen layer as alpha-over would
            // render visibly wrong, which is worse than saying so.
            skip = $"blend mode \"{mode}\" has no Proteus equivalent";
        else
            (bodyType, suffix, slot) = (lay.BodyType, lay.Suffix, s);

        return new LayerPlan(
            index, file, layout, map, mode,
            opacity, bytes, entry,
            bodyType, suffix, slot,
            string.IsNullOrWhiteSpace(layer.GeneratedFrom) ? null : layer.GeneratedFrom,
            skip);
    }

    /// <summary>
    /// Which layout option to pre-select: the one matching the body the character is actually wearing,
    /// else the first of <see cref="LayoutPreference"/> the pack carries. Null when nothing is importable.
    /// <para/>
    /// <c>Matched</c> says which of those two it was, because the caller shows the choice to the user and
    /// the fallback is a guess, not a match.
    /// </summary>
    private static (string? Layout, bool Matched) PickDefaultLayout(IReadOnlyList<LayerPlan> layers, string? wanted)
    {
        var present = layers.Where(l => l.Import).ToList();
        if (present.Count == 0) return (null, false);

        // Always answer with the FIRST importable layer's spelling of the layout — that is the spelling
        // WriteMod names the Penumbra option with, and Register feeds this string straight to
        // SetModOption. A pack that writes "bibo" on one layer and "BIBO" on another would otherwise pick
        // an option name Penumbra has never heard of, and nothing would be selected.
        string Canonical(LayerPlan hit)
            => present.First(l => string.Equals(l.LayoutToken, hit.LayoutToken, StringComparison.OrdinalIgnoreCase))
                      .LayoutToken;

        if (wanted != null)
        {
            var hit = present.FirstOrDefault(l => string.Equals(l.BodyType, wanted, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return (Canonical(hit), true);
        }

        foreach (var pref in LayoutPreference)
        {
            var hit = present.FirstOrDefault(l => string.Equals(l.LayoutToken, pref, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return (Canonical(hit), false);
        }
        return (present[0].LayoutToken, false);
    }

    // ── Import ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The material game paths each of the pack's layouts will target, keyed by layout token. Every human
    /// body the game defines, with that layout's material suffix — see <see cref="BodyMaterialCatalog"/>.
    /// The Import tab shows this so the author can see what the mod will claim before writing it.
    /// </summary>
    /// <summary>
    /// Whether the last <see cref="MaterialsFor"/> answered from the game data rather than the catalogue's
    /// hardcoded female-only fallback. Surfaced because a fallback list looks entirely legitimate in the
    /// preview while naming no male body at all.
    /// </summary>
    public bool BodiesFromGameData => bodies.FromGameData;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> MaterialsFor(ImportPreview preview)
        => preview.Layers.Where(l => l.Import)
            .GroupBy(l => l.LayoutToken, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => bodies.ForSuffix(g.First().Suffix!), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A mod written to disk by <see cref="Prepare"/> and waiting for <see cref="Register"/>, or the reason
    /// nothing was written. Split in two because writing a pack means copying tens of megabytes (and, with
    /// BC7 on, encoding several 4K textures) — far too long to spend in a draw call — while the Penumbra
    /// registration that follows belongs on the framework thread.
    /// </summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview, int Imported, int Skipped);

    /// <summary>
    /// Validate and write the mod to disk. Safe to run off the framework thread; nothing is left behind
    /// when it fails. The result must be handed to <see cref="Register"/> to become a live Penumbra mod.
    /// </summary>
    public PreparedImport Prepare(ImportPreview preview, string modName, string author, bool asTex)
    {
        modName = (modName ?? "").Trim();
        author = (author ?? "").Trim();

        PreparedImport Fail(string why) => new(false, why, null, null, 0, 0);

        if (string.IsNullOrWhiteSpace(modName)) return Fail("Enter a mod name.");
        if (!preview.AnyImportable) return Fail("Nothing in this pack can be imported.");
        if (!File.Exists(preview.SourcePath))
            return Fail($"The pack is no longer there: {preview.SourcePath}");

        var dirName = ModCreationService.Sanitize(modName);
        if (dirName == null)
            return Fail("That mod name has no usable characters — use letters or numbers.");
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Fail("\"Proteus\" is reserved — choose a different mod name.");

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return Fail("Penumbra's mod directory isn't available.");

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return Fail($"A mod folder named \"{dirName}\" already exists.");

        var materials = MaterialsFor(preview);

        try
        {
            WriteMod(root, modName, author, preview, materials, asTex ? textureLoader : null);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] onion import failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail($"Failed to write the mod: {ex.Message}");
        }

        var imported = preview.Layers.Count(l => l.Import);
        return new(true, "", dirName, preview, imported, preview.Layers.Count - imported);
    }

    /// <summary>
    /// The outcome of a registration. Three states, not two: an import can succeed and still need the user
    /// to do something — a message that ends "or nothing will paint" must not render in success-green.
    /// </summary>
    public readonly record struct ImportResult(bool Ok, bool Warning, string Message);

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra: add it, enable it in the player's collection,
    /// select the layout matching their body, open Penumbra to it and recomposite. Must run on the
    /// framework thread. A failed preparation passes straight through as its own message.
    /// </summary>
    /// <param name="quiet">
    /// Register and nothing else — no Penumbra window, no recomposite. For the teardown path: the point
    /// there is only that the folder already written into Penumbra's directory doesn't end up orphaned.
    /// Opening a window as the plugin unloads is merely odd, but a recomposite is genuinely unsafe — it
    /// passes CompositorService's own disposal guard (that service is torn down later in Plugin.Dispose)
    /// and schedules a delayed task that would wake into half-disposed services.
    /// </param>
    public ImportResult Register(PreparedImport prepared, bool quiet = false)
    {
        if (!prepared.Ok || prepared.DirName == null || prepared.Preview == null)
            return new(false, false, prepared.Message);

        var dirName = prepared.DirName;
        var preview = prepared.Preview;

        var ec = penumbra.AddModDirectory(dirName);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] AddMod({0}) -> {1}", dirName, ec);
            // Roll the folder back so the name is free to retry, exactly as ModCreationService does.
            var modsRoot = penumbra.GetModDirectory();
            if (!string.IsNullOrEmpty(modsRoot))
                try { Directory.Delete(Path.Combine(modsRoot, dirName), true); } catch { /* best effort */ }
            return new(false, false, string.Format(Loc.Localize("Service.RegisterFailed.Fmt",
                "Wrote the mod, but Penumbra couldn't register it ({0}). Rescan mods in Penumbra."), ec));
        }

        var layouts = preview.Layouts;
        // For a multi-layout pack the group SELECTION is the only thing that makes the mod composite
        // anything — with none, ResolveActiveOverlays returns an empty list — so a failure here has to
        // reach the user rather than hide behind a green "imported".
        var selectionFailed = false;

        // The pack has nothing for a VANILLA wearer, so the overlay only reaches them through a cross-UV
        // bake into gen2 — and that is opt-in per mod (Configuration.SiblingModeFor defaults to bibo+gen3,
        // deliberately, because baking every mod onto every vanilla body loaded nearby is expensive).
        // Leaving it at the default here would import a mod that is inert for the person importing it, so
        // raise it for THIS mod only and say so in the result rather than doing it silently.
        if (preview.NeedsAllBodies)
        {
            config.SiblingSynthesis[dirName] = SiblingSynthesisMode.AllBodies;
            config.Save();
            log.Information("[Proteus] imported {0}: wearer is on a vanilla body and the pack has no vanilla " +
                "layout — set this mod's sibling mode to AllBodies so the bake reaches gen2", dirName);
        }

        var collId = penumbra.GetPlayerCollectionId();
        if (collId.HasValue)
        {
            penumbra.SetModEnabled(collId.Value, dirName, true);
            // The group file's DefaultSettings only applies to collections that have never seen the mod;
            // assert the selection so the right layout is live in THIS collection either way.
            if (layouts.Count > 1 && preview.DefaultLayout != null)
            {
                var sel = penumbra.SetModOption(collId.Value, dirName, LayoutGroupName, [preview.DefaultLayout]);
                if (sel != PenumbraApiEc.Success)
                {
                    selectionFailed = true;
                    log.Warning("[Proteus] imported {0}: selecting {1}/{2} -> {3}",
                        dirName, LayoutGroupName, preview.DefaultLayout, sel);
                }
            }
        }
        else
        {
            log.Warning("[Proteus] imported {0}: no player collection — enable it manually", dirName);
        }

        if (!quiet)
        {
            penumbra.OpenToMod(dirName);
            compositor.TriggerRecomposite("onion-imported");
        }

        log.Information("[Proteus] imported Onion pack {0} -> {1} ({2} layer(s), {3} skipped, layouts: {4}){5}",
            Path.GetFileName(preview.SourcePath), dirName, prepared.Imported, prepared.Skipped,
            string.Join(", ", layouts), quiet ? " [quiet: plugin unloading]" : "");

        // Counts are labelled rather than inflected — see ModExportService for why.
        var tail = prepared.Skipped > 0
            ? string.Format(Loc.Localize("Import.Result.SkippedTail.Fmt", " (skipped: {0})"), prepared.Skipped)
            : "";
        if (selectionFailed)
            return new(true, true, string.Format(Loc.Localize("Import.Result.NoLayout.Fmt",
                "Imported \"{0}\" — layers: {1}{2}, but Proteus couldn't pick a body layout for you. " +
                "Choose one under \"{3}\" in Penumbra, or nothing will paint."),
                dirName, prepared.Imported, tail, LayoutGroupName));
        if (preview.NeedsAllBodies)
            return new(true, true, string.Format(Loc.Localize("Import.Result.AllBodies.Fmt",
                "Imported \"{0}\" — layers: {1}{2}. The pack has nothing painted for a vanilla body, so " +
                "Proteus set this mod's \"Bodies\" to \"All bodies\" (Colors → Advanced) to bake it across " +
                "— turn that off and it will stop painting."),
                dirName, prepared.Imported, tail));
        return new(true, false, string.Format(Loc.Localize("Import.Result.Ok.Fmt",
            "Imported \"{0}\" — layers: {1}{2}. Enabled it and opened it in Penumbra."),
            dirName, prepared.Imported, tail));
    }

    /// <summary>
    /// Write the mod files under <paramref name="root"/>: the layer images, the Proteus sidecar, Penumbra's
    /// manifest and default option, and — for a multi-layout pack — the layout option group. Pure
    /// filesystem work, no IPC, so it can be exercised offline against a temp directory.
    /// </summary>
    /// <param name="materials">Layout token → the material game paths that layout's overlays target.</param>
    /// <param name="texLoader">
    /// Non-null to write BC7 <c>.tex</c> instead of PNG. Null keeps the pack's own images, which is the
    /// default and the only path that can copy a full-opacity layer byte-for-byte.
    /// </param>
    internal static void WriteMod(
        string root, string modName, string author,
        ImportPreview preview,
        IReadOnlyDictionary<string, IReadOnlyList<string>> materials,
        TextureLoader? texLoader)
    {
        var overlaysDir = Path.Combine(root, "Proteus", "overlays");
        Directory.CreateDirectory(overlaysDir);

        var importable = preview.Layers.Where(l => l.Import).ToList();

        // Layout token → the descriptors painted on that body, in the pack's own paint order.
        var byLayout = new List<(string Layout, List<OverlayDescriptor> Overlays)>();
        foreach (var layer in importable)
        {
            var rel = Materialize(preview.SourcePath, layer, overlaysDir, texLoader);

            var d = new OverlayDescriptor
            {
                Layer = OverlayLayer.Skin,
                SourceBodyType = layer.BodyType,
                MaterialGamePaths = materials.TryGetValue(layer.LayoutToken, out var mats)
                    ? [.. mats]
                    : [],
            };
            switch (layer.Slot)
            {
                case "Diffuse": d.Diffuse = rel; break;
                case "Normal":  d.Normal  = rel; break;
                case "Mask":    d.Mask    = rel; break;
                case "Index":   d.Index   = rel; break;
            }
            // A layer that isn't a diffuse must not invent one: GenerateDiffuse would synthesize a tint
            // from the normal's coverage and row 16's colour, which is not what the pack painted.
            if (layer.Slot != "Diffuse") d.GenerateDiffuse = false;

            var bucket = byLayout.FirstOrDefault(b =>
                string.Equals(b.Layout, layer.LayoutToken, StringComparison.OrdinalIgnoreCase));
            if (bucket.Overlays == null)
                byLayout.Add((layer.LayoutToken, [d]));
            else
                bucket.Overlays.Add(d);
        }

        var metadata = new ProteusMetadata
        {
            FormatVersion = 1,
            Name = modName,
            Author = author,
        };

        if (byLayout.Count > 1)
        {
            // Several UV layouts of the same artwork — gate them behind one single-select Penumbra group
            // so exactly one composites. Option names are the pack's own layout tokens, and the sidecar's
            // group must name them identically: that string pair is how a selection reaches the compositor.
            metadata.OptionGroups =
            [
                new OverlayOptionGroup
                {
                    PenumbraGroupName = LayoutGroupName,
                    Options = byLayout
                        .Select(b => new OverlayOption { Name = b.Layout, Overlays = b.Overlays })
                        .ToList(),
                },
            ];
        }
        else if (byLayout.Count == 1)
        {
            metadata.Overlays = byLayout[0].Overlays;
        }

        var metaJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        // AtomicWrite, not File.WriteAllText: this descriptor is the one file here nothing can rebuild —
        // material paths, body type, shader, colour rows — and the import goes straight on to unpacking
        // textures, so the window where a truncated copy could be left behind is a busy one.
        PenumbraModMeta.AtomicWrite(Path.Combine(root, "Proteus", "metadata.json"), metaJson);

        // Penumbra's manifest, in the older layout every Penumbra can read (see PenumbraModMeta remarks).
        // The pack's own description/version/website ride along so the origin isn't lost.
        var description = string.IsNullOrWhiteSpace(preview.Description)
            ? $"Imported from the Onion pack \"{Path.GetFileName(preview.SourcePath)}\"."
            : preview.Description + $"\n\nImported from the Onion pack \"{Path.GetFileName(preview.SourcePath)}\".";
        PenumbraModMeta.AtomicWrite(
            Path.Combine(root, PenumbraModMeta.MetaFile),
            PenumbraModMeta.NewMetaJson(modName, author, description, preview.Version, preview.Website));

        // Proteus does the real texture redirection itself at composite time, so the default option would
        // be empty — which Penumbra flags as "changes nothing". Same harmless self-swap the Create tab uses.
        PenumbraModMeta.WriteRedirects(
            root, modName,
            files: new Dictionary<string, string>(),
            swaps: new Dictionary<string, string> { [ModCreationService.DummySwapPath] = ModCreationService.DummySwapPath });

        if (byLayout.Count > 1)
        {
            var names = byLayout.Select(b => b.Layout).ToList();
            var def = preview.DefaultLayout == null
                ? 0
                : Math.Max(0, names.FindIndex(n => string.Equals(n, preview.DefaultLayout, StringComparison.OrdinalIgnoreCase)));
            PenumbraModMeta.WriteSingleSelectGroup(root, 0, LayoutGroupName, names, def);
        }
    }

    // ── Layer images ─────────────────────────────────────────────────────────

    /// <summary>
    /// Extract one layer into the sidecar and return its sidecar-relative path.
    /// <para/>
    /// A fully-opaque layer going out as-is is copied BYTE-FOR-BYTE — no decode, no re-encode, no quality
    /// or size change. Only a layer that needs its opacity baked in, or one being converted to BC7, is
    /// decoded. An image the PNG/JPG/BMP/TGA decoder can't read falls back to a verbatim copy rather than
    /// failing the whole import.
    /// </summary>
    private static string Materialize(string ompPath, LayerPlan layer, string overlaysDir, TextureLoader? texLoader)
    {
        var bytes = OnionPackage.ReadEntry(ompPath, layer.Entry!);
        // layout_slot_index: unique per layer (index is the pack-wide paint order) and legible on disk.
        var stem = Sanitize($"{layer.LayoutToken}_{layer.Slot}_{layer.Index}".ToLowerInvariant());
        var srcExt = Path.GetExtension(layer.Entry!).ToLowerInvariant();
        if (string.IsNullOrEmpty(srcExt)) srcExt = ".png";

        bool needsAlpha = layer.Opacity < 0.999f;
        if (!needsAlpha && texLoader == null)
        {
            File.WriteAllBytes(Path.Combine(overlaysDir, stem + srcExt), bytes);
            return "overlays/" + stem + srcExt;
        }

        var decoded = TryDecode(bytes);
        if (decoded == null)
        {
            // Can't touch it — keep the original rather than dropping the layer. The opacity is then not
            // applied, which the caller's report notes via the layer's own Opacity value.
            File.WriteAllBytes(Path.Combine(overlaysDir, stem + srcExt), bytes);
            return "overlays/" + stem + srcExt;
        }

        var (rgba, w, h) = decoded.Value;
        if (needsAlpha)
        {
            var scale = layer.Opacity;
            for (int i = 3; i < rgba.Length; i += 4)
                rgba[i] = (byte)MathF.Round(rgba[i] * scale);
        }

        if (texLoader != null && texLoader.WriteTex(rgba, w, h, Path.Combine(overlaysDir, stem + ".tex"),
                TexEncoding.Bc7))
            return "overlays/" + stem + ".tex";

        WritePng(rgba, w, h, Path.Combine(overlaysDir, stem + ".png"));
        return "overlays/" + stem + ".png";
    }

    /// <summary>RGBA8 decode of a PNG/JPG/BMP/TGA in memory, or null when the bytes aren't one.</summary>
    private static (byte[] Rgba, int Width, int Height)? TryDecode(byte[] bytes)
    {
        try
        {
            var img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            return img?.Data == null || img.Width <= 0 || img.Height <= 0
                ? null
                : (img.Data, img.Width, img.Height);
        }
        catch { return null; }
    }

    private static void WritePng(byte[] rgba, int width, int height, string path)
    {
        using var stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }

    /// <summary>Filename-safe form of a generated stem. Only ever fed generated tokens, never pack input.</summary>
    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "layer" : cleaned;
    }
}
