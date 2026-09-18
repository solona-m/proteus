using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Imports a Penumbra <c>.pmp</c> that ships geometry as a Proteus content pack: every <c>.mdl</c> redirect is
/// removed from the manifest and a <c>Proteus/metadata.json</c> sidecar names those models, so the compositor appends
/// them into the carrier accessory and every selected option can be worn at once.
/// <see cref="Inspect"/> is cheap, <see cref="Prepare"/> runs off-thread, <see cref="Register"/> on the framework thread.
/// </summary>
public sealed partial class ContentImportService
{
    private readonly PenumbraBridge penumbra;
    private readonly CompositorService compositor;
    private readonly IPluginLog log;

    public ContentImportService(PenumbraBridge penumbra, CompositorService compositor, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.compositor = compositor;
        this.log = log;
    }

    // ── preview ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One model an option ships, and whether it can be appended. <paramref name="Bindings"/> maps each drawn
    /// material name to the pack file backing it; <paramref name="Unbound"/> lists drawn names the pack ships nothing
    /// for, and a piece with any is reported rather than imported.
    /// </summary>
    public sealed record PiecePlan(
        string GamePath,
        string Entry,
        IReadOnlyDictionary<string, string> Bindings,
        IReadOnlyList<string> Unbound,
        int Meshes,
        int Vertices,
        string? Problem,
        string RaceCode = "",
        /// <summary>
        /// Material name → the attribute names of the submeshes drawn with it: the switches a mod flips by name.
        /// </summary>
        IReadOnlyDictionary<string, List<string>>? MaterialAttributes = null,
        /// <summary>
        /// Dropped on purpose: every mesh is the wearer's own body. An explicit flag, because an unreadable model
        /// also arrives with empty Bindings and Unbound.
        /// </summary>
        bool BodyOnly = false)
    {
        public bool Import => Problem == null && Bindings.Count > 0;

        /// <summary>Dropped, and dropped because something is WRONG — an unbound material or a model that
        /// would not read. The deliberate body drop is not one.</summary>
        public bool Faulty => !Import && !BodyOnly;
    }

    /// <summary>
    /// One thing the user can tick: a garment, with every race variant of it underneath.
    /// </summary>
    /// <param name="Group">The author's group this came from, or null when the model was unconditional.</param>
    /// <param name="GateOption">
    /// The option in the synthesized group that switches this on, or null when the author's own option
    /// already selects it and nothing needs adding.
    /// </param>
    public sealed record PieceUnit(
        string? Group,
        string? Option,
        ContentSlot.Parsed Slot,
        string? ItemName,
        string? GateOption,
        IReadOnlyList<PiecePlan> Variants)
    {
        /// <summary>What the user reads, in the Import tab and as the synthesized option's name.</summary>
        public string Label => ContentSlot.Label(Slot, ItemName);

        /// <summary>Buildable when ANY race variant is — a pack missing one race's material still works
        /// for the races it does ship.</summary>
        public bool Import => Variants.Any(v => v.Import);
    }

    /// <summary>What an import would do, shown in the Import tab before anything is written.</summary>
    /// <param name="PieceGroupName">
    /// The multi-select group the import will add so individual pieces can be picked, or null when the
    /// pack's own options already select one garment each and there is nothing to add.
    /// </param>
    public sealed record ImportPreview(
        string SourcePath,
        PenumbraPackage.Contents Pack,
        IReadOnlyList<PieceUnit> Units,
        string? PieceGroupName,
        IReadOnlyList<string> Warnings,
        /// <summary>
        /// The pack's own <c>Proteus/metadata.json</c>, or null for an ordinary mod. Non-null makes the import an
        /// install rather than a conversion — see <see cref="InstallOnly"/>.
        /// </summary>
        ProteusMetadata? AuthoredSidecar = null,
        /// <summary>
        /// Default-data model redirects an option group replaces, as <c>RedirectKey</c> pairs. Not units, but still
        /// stripped, or the shadowed default would win once the option's redirect is removed.
        /// </summary>
        IReadOnlySet<string>? ShadowedRedirects = null)
    {
        public string Name => Pack.Name;
        public string Author => Pack.Author;
        public string? Description => string.IsNullOrWhiteSpace(Pack.Description) ? null : Pack.Description;
        public string? Website => string.IsNullOrWhiteSpace(Pack.Website) ? null : Pack.Website;

        /// <summary>Pieces that can actually be appended.</summary>
        public int ImportableUnits => Units.Count(u => u.Import);

        public bool AnyImportable => ImportableUnits > 0;

        /// <summary>
        /// This pack is copied in unchanged, since it already carries a Proteus sidecar: nothing is stripped, no
        /// gate group added, no defaults cleared.
        /// </summary>
        public bool InstallOnly => AuthoredSidecar != null;

        /// <summary>Whether the Import button does anything — either geometry to take over, or a
        /// ready-made Proteus mod to install as it stands.</summary>
        public bool CanImport => InstallOnly || AnyImportable;

        /// <summary>Every piece the pack ships, importable or not — the count the tab reports against.</summary>
        public int TotalUnits => Units.Count;

        /// <summary>
        /// Pieces dropped because something is wrong with them, as opposed to on purpose (body meshes, read off
        /// <see cref="PiecePlan.BodyOnly"/>). A unit that imports for some race is not faulty.
        /// </summary>
        public int FaultyUnits => Units.Count(u => !u.Import && u.Variants.Any(v => v.Faulty));

        /// <summary>The names the synthesized group will offer, in listing order.</summary>
        public IReadOnlyList<string> GateOptions
            => [.. Units.Where(u => u.Import && u.GateOption != null)
                        .Select(u => u.GateOption!).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Read the pack and work out which of its options carry geometry Proteus can append. Throws
    /// <see cref="InvalidDataException"/> when the file isn't a readable pack.
    /// </summary>
    /// <param name="contents">
    /// The already-parsed manifest of <paramref name="pmpPath"/>, when the caller has one; null reads it here.
    /// </param>
    public static ImportPreview Inspect(
        string pmpPath, IPluginLog? log = null, Func<int, int, string?>? itemName = null,
        PenumbraPackage.Contents? contents = null)
    {
        return new PackInspection(pmpPath, log, itemName, contents).Run();
    }

    /// <summary>
    /// The group the import adds so individual pieces can be picked. Suffixed until free: writing a group replaces
    /// any of the same name.
    /// </summary>
    internal const string PieceGroup = "Pieces (Proteus)";

    private static string UniqueGroupName(PenumbraPackage.Contents pack)
    {
        bool Taken(string n) => pack.Groups.Any(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(PieceGroup)) return PieceGroup;
        for (int i = 2; ; i++)
            if (!Taken($"{PieceGroup} {i}"))
                return $"{PieceGroup} {i}";
    }

    /// <summary>Only Single and Multi have selectable options a sidecar group can mirror.</summary>
    private static bool IsSelectable(string type)
        => string.Equals(type, "Single", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "Multi", StringComparison.OrdinalIgnoreCase);

    private static PiecePlan PlanPiece(
        string gamePath, string entry, byte[]? model,
        IReadOnlyDictionary<string, string> materialsByLeaf, IPluginLog? log)
    {
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unbound = new List<string>();
        // Meshes left to the character's own skin: a choice rather than a shortfall.
        var skinOnly = new List<string>();

        PiecePlan Unreadable(string why)
            => new(gamePath, entry, bindings, unbound, 0, 0,
                   string.Format(Loc.Localize("ContentImport.Problem.Unreadable.Fmt",
                       "not a readable model ({0})"), why));

        if (model == null) return Unreadable(entry);

        List<string> declared;
        Dictionary<string, List<string>> byMaterial;
        try
        {
            // Both from one walk: the parse is the expensive half of either question.
            (declared, byMaterial) = SecondSkinWriter.MaterialsAndAttributes(model);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} is not a readable model", entry);
            return Unreadable(ex.Message);
        }

        var used = ContentPieceResolver.UsedMaterialNames(model, declared);
        if (used.Count == 0)
            return new PiecePlan(gamePath, entry, bindings, unbound, 0, 0,
                Loc.Localize("ContentImport.Problem.NoGeometry", "no geometry — every mesh in it is empty"));

        int meshes = 0, vertices = 0;
        foreach (var name in used)
        {
            var leaf = name.TrimStart('/');

            // The pack's own binding is asked first: a material named like the body's can still be the pack's.
            if (materialsByLeaf.TryGetValue(leaf, out var mtrlEntry))
            {
                bindings[leaf] = mtrlEntry;
                if (SecondSkinWriter.TryReadLod0Geometry(model, out var pos, out _, out var tri,
                        SecondSkinWriter.KeepByLeaf(
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { leaf })))
                {
                    vertices += pos.Length / 3;
                    if (tri.Length > 0) meshes++;
                }
            }
            // Unbound and named like the body's own material: a mesh Proteus deliberately leaves behind.
            else if (!SecondSkinWriter.IsBodySkinMaterial(leaf))
                unbound.Add(leaf);
            else
                skinOnly.Add(leaf);
        }

        string? problem = null;
        bool bodyOnly = false;
        if (bindings.Count == 0)
        {
            // Only a body-only model binds nothing deliberately; a model naming no drawn material is faulty.
            bodyOnly = unbound.Count == 0 && skinOnly.Count > 0;
            problem = unbound.Count == 0 && skinOnly.Count == 0
                ? Loc.Localize("ContentImport.Problem.NoMeshes",
                    "it draws no meshes, so there is nothing to append.")
                : !bodyOnly
                ? string.Format(Loc.Localize("ContentImport.Problem.Unbound.Fmt",
                    "its mesh names {0}, which this pack does not ship. Rebind the mesh to one of the pack's "
                  + "own materials and re-export."), string.Join(", ", unbound))
                // Every mesh in it is the body: not a fault, so it says what it is.
                : string.Format(Loc.Localize("ContentImport.Problem.BodyOnly.Fmt",
                    "every mesh in it is the wearer's own body ({0}), so there is nothing to add."),
                    string.Join(", ", skinOnly));
        }

        return new PiecePlan(gamePath, entry, bindings, unbound, meshes, vertices, problem,
                             MaterialAttributes: byMaterial, BodyOnly: bodyOnly);
    }

    /// <summary>
    /// The pack's own <c>Proteus/metadata.json</c>, or null when it ships none. Fail-soft: an unparseable sidecar
    /// reads as absent.
    /// </summary>
    private static ProteusMetadata? ReadAuthoredSidecar(PenumbraPackage.Contents pack, IPluginLog? log)
    {
        var entry = pack.Entries.Keys.FirstOrDefault(k =>
            string.Equals(k, SidecarDiscoveryService.SidecarSubdir + "/metadata.json",
                          StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;

        try
        {
            var bytes = PenumbraPackage.ReadEntries(pack.Path, [entry]);
            return bytes.TryGetValue(entry, out var json)
                ? JsonSerializer.Deserialize<ProteusMetadata>(json, ProteusJson.MetadataRead)
                : null;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} carries a Proteus sidecar that could not be "
                           + "read, so its overlays and colours will not be carried over", pack.Path);
            return null;
        }
    }

    // ── write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mod written to disk by <see cref="Prepare"/> and waiting for <see cref="Register"/>, or the reason
    /// nothing was written.
    /// </summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview, int Pieces, int Skipped);

    /// <summary>
    /// Why an installed mod's folder cannot be imported, or null when it can (and always for a <c>.pmp</c>).
    /// Proteus's own managed mod is rewritten every composite, and a mod that already carries a sidecar is
    /// already slot-less: copying either would only give a second copy of the same thing.
    /// </summary>
    internal static string? RefuseInstalledSource(ImportPreview preview)
    {
        if (!PenumbraPackage.IsFolder(preview.SourcePath)) return null;

        var dir = Path.GetFileName(Path.TrimEndingDirectorySeparator(preview.SourcePath));
        if (string.Equals(dir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Loc.Localize("ContentImport.Fail.ManagedMod",
                "That is Proteus's own managed mod — it cannot be imported.");
        if (preview.InstallOnly)
            return Loc.Localize("ContentImport.Fail.AlreadyProteus",
                "This mod is already a Proteus mod — there is nothing to import.");
        return null;
    }

    /// <summary>
    /// Unpack the mod and write its manifests. Safe off the framework thread; nothing is left behind when
    /// it fails. The result must be handed to <see cref="Register"/> to become a live Penumbra mod.
    /// </summary>
    public PreparedImport Prepare(ImportPreview preview, string modName, string author)
    {
        modName = (modName ?? "").Trim();
        author = (author ?? "").Trim();

        PreparedImport Fail(string why) => new(false, why, null, null, 0, 0);

        if (string.IsNullOrWhiteSpace(modName))
            return Fail(Loc.Localize("Import.NeedName", "Enter a mod name."));
        // CanImport, not AnyImportable: a ready-made Proteus mod has no units and is still importable.
        if (!preview.CanImport)
            return Fail(Loc.Localize("ContentImport.Fail.NothingUsable", "Nothing in this pack can be imported."));
        if (!PenumbraPackage.Exists(preview.SourcePath))
            return Fail(string.Format(Loc.Localize("ContentImport.Fail.Gone.Fmt",
                "The pack is no longer there: {0}"), preview.SourcePath));
        if (RefuseInstalledSource(preview) is { } refused)
            return Fail(refused);

        var dirName = ModCreationService.Sanitize(modName);
        if (dirName == null)
            return Fail(Loc.Localize("ContentImport.Fail.BadName",
                "That mod name has no usable characters — use letters or numbers."));
        if (string.Equals(dirName, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Fail(Loc.Localize("ContentImport.Fail.Reserved",
                "\"Proteus\" is reserved — choose a different mod name."));

        var modsRoot = penumbra.GetModDirectory();
        if (string.IsNullOrEmpty(modsRoot))
            return Fail(Loc.Localize("ContentImport.Fail.NoModDir", "Penumbra's mod directory isn't available."));

        var root = Path.Combine(modsRoot, dirName);
        if (Directory.Exists(root))
            return Fail(string.Format(Loc.Localize("ContentImport.Fail.Exists.Fmt",
                "A mod folder named \"{0}\" already exists."), dirName));

        try
        {
            WriteMod(root, modName, author, preview, log);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] content import failed for {0}", dirName);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort */ }
            return Fail(string.Format(Loc.Localize("ContentImport.Fail.Write.Fmt",
                "Failed to write the mod: {0}"), ex.Message));
        }

        int pieces = preview.ImportableUnits;
        return new(true, "", dirName, preview, pieces, preview.TotalUnits - pieces);
    }

    /// <summary>
    /// Unpack the archive, strip the model redirects from the manifests and write the Proteus sidecar. Pure
    /// filesystem work. A v3 pack's piece group is left for <see cref="Register"/>, after Penumbra upgrades it.
    /// </summary>
    internal static void WriteMod(
        string root, string modName, string author, ImportPreview preview, IPluginLog? log = null)
    {
        Directory.CreateDirectory(root);

        // The pack's own layout is preserved verbatim: its manifest and the sidecar both name files by it. An
        // installed mod is copied, so the original keeps its redirects.
        PenumbraPackage.ExtractTo(preview.SourcePath, root);

        // A ready-made Proteus mod stops here: the copy is the whole import. Only the name is set, since
        // Penumbra's mod list reads it from this file.
        if (preview.InstallOnly)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log,
                "the installed mod keeps the pack's own name", manifest => manifest["Name"] = modName);
            return;
        }

        // Only the redirects the sidecar is about to name; a refused unit keeps its own. Keyed on game path and
        // file, so an option sharing a path is judged on its own redirect.
        var taken = preview.Units
            .Where(u => u.Import)
            .SelectMany(u => u.Variants.Where(v => v.Import).Select(v => RedirectKey(v.GamePath, v.Entry)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // …plus the default-data copies an option replaces. See ImportPreview.ShadowedRedirects.
        if (preview.ShadowedRedirects is { } shadowed) taken.UnionWith(shadowed);
        StripModelRedirects(root, preview.Pack, taken, log);

        // The user's name goes into the copied manifest too, so it can be told apart from the original in
        // Penumbra's mod list.
        EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log,
            "the copied mod keeps the pack's own name and cannot be told from the original in Penumbra's "
          + "mod list",
            manifest => manifest["Name"] = modName);

        ClearMultiSelectDefaults(root, preview, log);

        // The piece group, written with every option off. Not here for a v3 pack: Register writes it once
        // Penumbra has migrated the folder. See WritePieceGroupAfterUpgrade.
        if (NeedsPieceGroup(preview) && preview.Pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
            PenumbraModMeta.WriteMultiSelectGroup(
                root, preview.Pack.Groups.Count, preview.PieceGroupName!, preview.GateOptions, defaultSettings: 0);

        var metadata = BuildSidecar(preview, modName, author);
        var metaJson = JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite);
        var sidecarDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(sidecarDir);
        PenumbraModMeta.AtomicWrite(Path.Combine(sidecarDir, "metadata.json"), metaJson);
    }

    /// <summary>
    /// Remove the <c>.mdl</c> redirects Proteus is taking over from the copied manifests, in either layout. The files
    /// stay: Proteus appends them. Only redirects in <paramref name="taken"/> go, keyed on game path and archive
    /// entry, so a refused option sharing a path keeps its redirect.
    /// </summary>
    private static void StripModelRedirects(
        string root, PenumbraPackage.Contents pack, IReadOnlySet<string> taken, IPluginLog? log)
    {
        const string cost = "its model redirects are still Penumbra's — that pack's pieces will fight over "
                          + "their game paths";

        if (pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log, cost, manifest =>
            {
                if (manifest["DefaultData"] is JsonObject dd) StripFiles(dd, taken);
                if (manifest["Groups"] is JsonArray groups)
                    foreach (var g in groups)
                        if (g is JsonObject go)
                            StripGroup(go, taken);
            });
            return;
        }

        EditJson(Path.Combine(root, PenumbraModMeta.LegacyDefaultMod), log, cost,
            o => StripFiles(o, taken));
        foreach (var group in pack.Groups)
            if (group.Entry != null)
                EditJson(Path.Combine(root, group.Entry.Replace('/', Path.DirectorySeparatorChar)), log,
                    cost, o => StripGroup(o, taken));
    }

    /// <summary>
    /// Every multi-select group in the copied pack comes in with nothing ticked, so an imported mod contributes only
    /// what the user asks for. Single groups (which always have a selection), Imc and Combining groups are untouched.
    /// </summary>
    private static void ClearMultiSelectDefaults(
        string root, ImportPreview preview, IPluginLog? log)
    {
        const string cost = "its multi-select groups arrive with the pack's own options already ticked, so "
                          + "the mod puts pieces on the character before anyone asks for them";

        var pack = preview.Pack;
        var keepOn = GatedGroupDefaults(preview);
        void Clear(JsonObject group) => ClearGroupDefault(group, keepOn);

        if (pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log, cost, manifest =>
            {
                if (manifest["Groups"] is JsonArray groups)
                    foreach (var g in groups)
                        if (g is JsonObject go) Clear(go);
            });
            return;
        }

        foreach (var group in pack.Groups)
            if (group.Entry != null)
                EditJson(Path.Combine(root, group.Entry.Replace('/', Path.DirectorySeparatorChar)), log,
                    cost, Clear);
    }

    /// <summary>
    /// Multi groups whose one piece-carrying option stays ticked, as group name → default bitmask. Every piece that
    /// option carries is gated in the piece group, so the gate alone decides what is worn; clearing the option too
    /// would make each piece take two switches, and drop the materials and textures it supplies.
    /// <para/>
    /// Only when nothing else in the option reaches the character (<see cref="OnlyItsOwnPieces"/>): a ticked option
    /// applies all of it whatever the piece switches say.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> GatedGroupDefaults(ImportPreview preview)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!NeedsPieceGroup(preview)) return result;

        foreach (var group in preview.Pack.Groups)
        {
            if (!string.Equals(group.Type, "Multi", StringComparison.OrdinalIgnoreCase)) continue;
            var carrying = preview.Units.Where(u => u.Import && u.Group == group.Name).ToList();
            if (carrying.Count == 0 || carrying.Any(u => u.GateOption == null)) continue;
            var options = carrying.Select(u => u.Option).Distinct().ToList();
            if (options.Count != 1) continue;

            int index = group.Options.ToList().FindIndex(o => o.Name == options[0]);
            if (index is >= 0 and < 32 && OnlyItsOwnPieces(group.Options[index], carrying))
                result[group.Name] = 1 << index;
        }
        return result;
    }

    /// <summary>
    /// Whether everything <paramref name="option"/> still does once imported belongs to the equipment sets of the
    /// pieces it carries: no model redirect left behind (a refused or body-only piece would go on the character in
    /// its gear slot), and every file, swap and manipulation inside those pieces' own sets. Those only touch the
    /// vanilla item the pieces replace; anything else — a body texture, another item, racial scaling — would apply
    /// to the character the moment the mod is imported, and stay applied with every piece switched off.
    /// </summary>
    internal static bool OnlyItsOwnPieces(PenumbraPackage.PackOption option, IReadOnlyList<PieceUnit> carrying)
    {
        var taken = carrying
            .SelectMany(u => u.Variants.Where(v => v.Import).Select(v => RedirectKey(v.GamePath, v.Entry)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only equipment and accessory sets have a folder of their own to stay inside.
        var folders = carrying.Select(u => u.Slot.SetTag.ToLowerInvariant())
            .Select(tag => tag.StartsWith('e') ? $"chara/equipment/{tag}/"
                         : tag.StartsWith('a') ? $"chara/accessory/{tag}/"
                         : null)
            .ToList();
        if (folders.Any(f => f == null)) return false;
        var sets = carrying.Select(u => ContentSlot.SetIdOf(u.Slot.SetTag)).ToHashSet();

        bool Inside(string gamePath)
        {
            var p = PenumbraPackage.Normalize(gamePath);
            return folders.Any(f => p.StartsWith(f!, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var (gamePath, entry) in option.Files)
        {
            if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
             && !taken.Contains(RedirectKey(gamePath, entry)))
                return false;
            if (!Inside(gamePath)) return false;
        }
        return option.Swaps.All(Inside)
            && option.Manipulations.All(m => m.SetId is { } id && sets.Contains(id));
    }

    /// <summary>Zero one group's default selection, if it is a multi-select, unless <paramref name="keepOn"/> names it.</summary>
    private static void ClearGroupDefault(JsonObject group, IReadOnlyDictionary<string, int> keepOn)
    {
        // Through TryGetValue: GetValue<string> throws on a non-string Type. An unreadable group is left as is.
        if (group["Type"] is not JsonValue tv
         || !tv.TryGetValue<string>(out var type)
         || !string.Equals(type, "Multi", StringComparison.OrdinalIgnoreCase))
            return;

        // Written even when absent: an explicit value says what this import meant.
        var name = group["Name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : null;
        group["DefaultSettings"] = name != null && keepOn.TryGetValue(name, out var mask) ? mask : 0;
    }

    private static void StripGroup(JsonObject group, IReadOnlySet<string> taken)
    {
        if (group["Options"] is JsonArray opts)
            foreach (var o in opts)
                if (o is JsonObject oo) StripFiles(oo, taken);
        // A Combining group's redirects hang off Containers rather than Options.
        if (group["Containers"] is JsonArray containers)
            foreach (var c in containers)
                if (c is JsonObject co) StripFiles(co, taken);
    }

    private static void StripFiles(JsonObject owner, IReadOnlySet<string> taken)
    {
        if (owner["Files"] is not JsonObject files) return;
        // Through TryGetValue: GetValue<string> throws on a non-string value, which would cost the whole file's edit.
        var doomed = files
            .Where(p => p.Value is JsonValue v
                     && v.TryGetValue<string>(out var entry)
                     && taken.Contains(RedirectKey(p.Key, entry)))
            .Select(p => p.Key)
            .ToList();
        foreach (var k in doomed) files.Remove(k);
    }

    /// <summary>
    /// One manifest redirect, as a comparable key: both halves normalised, NUL-separated (it occurs in neither).
    /// </summary>
    private static string RedirectKey(string gamePath, string entry)
        => PenumbraPackage.Normalize(gamePath) + '\0' + PenumbraPackage.Normalize(entry);

    /// <summary>
    /// Edit one manifest in place, or log why it could not be. <paramref name="cost"/> is the caller's consequence of
    /// failure and completes "…, so {cost}".
    /// </summary>
    private static void EditJson(string path, IPluginLog? log, string cost, Action<JsonObject> edit)
    {
        if (!File.Exists(path)) return;
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} could not be read, so {1}", path, cost);
            return;
        }
        if (node is not JsonObject root)
        {
            log?.Warning("[Proteus] content import: {0} is not a JSON object, so {1}", path, cost);
            return;
        }

        // The edit is guarded too, and the write skipped when it throws, so a malformed manifest cannot abandon
        // WriteMod mid-way.
        try { edit(root); }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} could not be edited, so {1}", path, cost);
            return;
        }
        PenumbraModMeta.AtomicWrite(path, root.ToJsonString(ProteusJson.MetadataWrite));
    }

    /// <summary>
    /// The pack's IMC show/hide toggles as the sidecar records them, or null when it has none. Recorded because the
    /// composite moves the geometry onto a host accessory, whose IMC mask the game reads instead. Options with no bits
    /// are skipped. Also re-run after the Parts tab writes a switch into an imported mod (see
    /// <see cref="MeshToggleService"/>), so the sidecar keeps mirroring the manifest.
    /// </summary>
    internal static List<ContentAttributeGroup>? AttributeGroups(PenumbraPackage.Contents pack)
    {
        List<ContentAttributeGroup>? result = null;
        foreach (var g in pack.Groups.Where(g =>
                     string.Equals(g.Type, "Imc", StringComparison.OrdinalIgnoreCase)))
        {
            var opts = g.Options.Where(o => o.AttributeMask != 0)
                .ToDictionary(o => o.Name, o => (int)o.AttributeMask, StringComparer.Ordinal);
            if (opts.Count == 0 && g.DefaultAttributeMask == 0) continue;
            (result ??= []).Add(new ContentAttributeGroup
            {
                Group       = g.Name,
                SetId       = g.ImcSetId,
                Slot        = g.ImcSlot,
                DefaultMask = g.DefaultAttributeMask,
                Options     = opts,
            });
        }
        return result;
    }

    /// <summary>The Proteus sidecar mirroring the pack's groups, with one piece per importable model.</summary>
    internal static ProteusMetadata BuildSidecar(ImportPreview preview, string modName, string author)
    {
        // Seeded from the pack's own sidecar as a backstop (install-only packs return before here), so a
        // freshly built sidecar never deletes authored fields. Name and Author are the user's.
        var a = preview.AuthoredSidecar;
        var metadata = new ProteusMetadata
        {
            Overlays           = a?.Overlays,
            OptionGroups       = a?.OptionGroups,
            ColorTableRows     = a?.ColorTableRows,
            MaskColorTableRows = a?.MaskColorTableRows,
            MaskDescriptor     = a?.MaskDescriptor,
            AmbientOcclusion   = a?.AmbientOcclusion,
            ContentGlow        = a?.ContentGlow,
            ContentMaterials   = a?.ContentMaterials,

            Name = modName,
            Author = author,
        };

        metadata.PieceGroupName = preview.PieceGroupName;

        metadata.ContentAttributes = AttributeGroups(preview.Pack);

        // The extra skeletons this pack's pieces need — see ContentSkeleton. Entries of 0 are dropped: writing one
        // would clear the skeleton of whatever body part the composite points it at.
        void Skeletons(IEnumerable<PenumbraPackage.PackEst> est, string? group, string? option)
        {
            foreach (var e in est)
            {
                if (e.Entry == 0) continue;
                if ((metadata.ContentSkeletons ??= []).Any(s =>
                        s.Group == group && s.Option == option
                     && string.Equals(s.Slot, e.Slot, StringComparison.OrdinalIgnoreCase)
                     && s.Entry == e.Entry)) continue;
                metadata.ContentSkeletons.Add(new ContentSkeleton
                {
                    Group = group, Option = option, Slot = e.Slot, Entry = e.Entry,
                });
            }
        }

        Skeletons(preview.Pack.DefaultEst, null, null);
        foreach (var g in preview.Pack.Groups)
            foreach (var o in g.Options)
                Skeletons(o.Est, g.Name, o.Name);

        // Models the pack redirects outside every option.
        foreach (var unit in preview.Units.Where(u => u.Import && u.Group == null))
            (metadata.Content ??= new()).Add(PieceOf(unit, preview.Pack));

        foreach (var byGroup in preview.Units.Where(u => u.Import && u.Group != null)
                     .GroupBy(u => u.Group!, StringComparer.Ordinal))
        {
            var group = new ContentOptionGroup { PenumbraGroupName = byGroup.Key };
            foreach (var byOption in byGroup.GroupBy(u => u.Option!, StringComparer.Ordinal))
                group.Options.Add(new ContentOption
                {
                    Name   = byOption.Key,
                    Pieces = byOption.Select(u => PieceOf(u, preview.Pack)).ToList(),
                });
            if (group.Options.Count > 0)
                (metadata.ContentGroups ??= new()).Add(group);
        }

        return metadata;
    }

    /// <summary>
    /// How well dressed each candidate material is: how many distinct pack files back its textures, and their total
    /// bytes. Tells the variant an author worked on from placeholder copies. A hint: an unparseable material scores zero.
    /// </summary>
    private static Dictionary<string, (int Files, long Bytes)> DressedRank(
        PenumbraPackage.Contents pack,
        Dictionary<string, HashSet<string>> backing,
        Dictionary<string, List<string>> candidatesByLeaf)
    {
        var rank = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        // Only leaves with more than one candidate need ranking.
        var contested = candidatesByLeaf.Values.Where(p => p.Count > 1).SelectMany(p => p)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var gp in contested) rank[gp] = (0, 0L);
        if (contested.Count == 0) return rank;

        // Game path → the entry backing it, for turning a material's texture references into pack files.
        var entryOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, entry) in pack.AllFiles) entryOf.TryAdd(gamePath, entry);

        Dictionary<string, byte[]> bytes;
        try { bytes = PenumbraPackage.ReadEntries(pack.Path, contested.Select(gp => backing[gp].First())); }
        catch { return rank; }   // unreadable archive — every candidate stays at zero, order unchanged

        foreach (var gp in contested)
        {
            if (!bytes.TryGetValue(backing[gp].First(), out var mtrl)) continue;
            MtrlTexturePaths slots;
            try { slots = TextureLoader.ParseMtrlBytes(mtrl); }
            catch { continue; }
            if (!slots.Parsed) continue;

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tex in new[] { slots.Diffuse, slots.Normal, slots.Mask, slots.Index })
                if (tex is { Length: > 0 } && entryOf.TryGetValue(tex, out var backedBy))
                    files.Add(backedBy);

            rank[gp] = (files.Count, files.Sum(f => pack.Entries.TryGetValue(f, out var n) ? n : 0L));
        }
        return rank;
    }

    /// <summary>
    /// The textures this piece's materials name that the pack itself ships, and which option supplies each. Every
    /// candidate material is read. Null when nothing came back, leaving textures to Penumbra.
    /// </summary>
    private static Dictionary<string, List<ContentMaterialSource>>? TextureSuppliers(
        PenumbraPackage.Contents pack,
        Dictionary<string, List<ContentMaterialSource>> suppliers,
        Dictionary<string, List<ContentMaterialSource>>? materialOptions)
    {
        if (materialOptions is not { Count: > 0 }) return null;

        var entries = materialOptions.Values.SelectMany(v => v).Select(s => s.File)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (entries.Count == 0) return null;

        Dictionary<string, byte[]> bytes;
        try { bytes = PenumbraPackage.ReadEntries(pack.Path, entries); }
        catch { return null; }

        var map = new Dictionary<string, List<ContentMaterialSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mtrl in bytes.Values)
        {
            MtrlTexturePaths slots;
            try { slots = TextureLoader.ParseMtrlBytes(mtrl); }
            catch { continue; }   // a hint, like DressedRank: one unreadable material costs its textures
            if (!slots.Parsed) continue;

            foreach (var tex in new[] { slots.Diffuse, slots.Normal, slots.Mask, slots.Index })
            {
                if (tex is not { Length: > 0 } || !suppliers.TryGetValue(tex, out var who)) continue;

                // Only a path the pack varies is worth taking over; one file behind a texture is fixed.
                if (who.Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    map[tex] = who;
            }
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Whether a redirect's game path is one the game could ever ask for: under <c>chara/</c>. A junk-prefixed path's
    /// tail still parses, so the prefix is what must be checked.
    /// </summary>
    private static bool IsGamePath(string gamePath)
        => PenumbraPackage.Normalize(gamePath).StartsWith("chara/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One unit as the sidecar stores it: race variants become a <c>Models</c> map and their material bindings merge.
    /// Built from the first variant of each race only, so model and bindings never disagree.
    /// </summary>
    private static ContentPiece PieceOf(PieceUnit unit, PenumbraPackage.Contents pack)
    {
        var piece = new ContentPiece
        {
            Surface    = ShellSurfaceKind.Body,
            Slot       = unit.Slot.Label,
            GateOption = unit.GateOption,
        };

        // First declaration of a race wins; a duplicate is dropped rather than fatal.
        var kept = new List<PiecePlan>();
        var seenRaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in unit.Variants)
            if (v.Import && seenRaces.Add(v.RaceCode))
                kept.Add(v);

        if (kept.Count == 1 && string.IsNullOrEmpty(kept[0].RaceCode))
            piece.Model = kept[0].Entry;
        else
            piece.Models = kept.ToDictionary(v => v.RaceCode, v => v.Entry, StringComparer.OrdinalIgnoreCase);

        foreach (var v in kept)
            foreach (var (leaf, entry) in v.Bindings)
                piece.Materials[leaf] = entry;

        // The game paths each bound material is published under — see ContentPiece.MaterialGamePaths. Ordered by
        // how many different files the pack puts behind each path: several files is a user choice, one is fixed.
        var backing = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        // Game path → who supplies it, in declaration order, so each file remembers the option it came from.
        var suppliers = new Dictionary<string, List<ContentMaterialSource>>(StringComparer.OrdinalIgnoreCase);
        void Candidate(string gamePath, string entry, string? group, string? option)
        {
            // Junk-prefixed paths are skipped: with no group they would become an always-on material choice.
            if (!IsGamePath(gamePath)) return;

            bool isMtrl = gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase);
            // Textures are suppliers but not candidates: a material names the one path it wants.
            // See ContentPiece.TextureOptions.
            if (!isMtrl && !gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) return;

            if (!suppliers.TryGetValue(gamePath, out var who))
                suppliers[gamePath] = who = [];
            if (!who.Any(s => s.Group == group && s.Option == option
                           && string.Equals(s.File, entry, StringComparison.OrdinalIgnoreCase)))
                who.Add(new ContentMaterialSource { Group = group, Option = option, File = entry });

            if (!isMtrl) return;
            if (!backing.TryGetValue(gamePath, out var files))
            {
                backing[gamePath] = files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                order.Add(gamePath);
            }
            files.Add(entry);
        }

        foreach (var g in pack.Groups)
            foreach (var o in g.Options)
                foreach (var (gp, entry) in o.Files)
                    Candidate(gp, entry, g.Name, o.Name);
        foreach (var (gp, entry) in pack.DefaultFiles)
            Candidate(gp, entry, null, null);

        // Everything a leaf could be published under, so the materials can be read in one pass.
        var candidatesByLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in piece.Materials.Keys)
        {
            var trimmed = leaf.TrimStart('/');
            var paths = order
                .Where(gp => string.Equals(Path.GetFileName(gp.Replace('\\', '/')), trimmed,
                                           StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (paths.Count > 0) candidatesByLeaf[leaf] = paths;
        }

        var dressed = DressedRank(pack, backing, candidatesByLeaf);

        foreach (var (leaf, paths) in candidatesByLeaf)
        {
            // Competing file count, then how well dressed (see DressedRank); OrderByDescending is stable, so a
            // full tie keeps declaration order.
            var ranked = paths
                .OrderByDescending(gp => backing[gp].Count)
                .ThenByDescending(gp => dressed[gp].Files)
                .ThenByDescending(gp => dressed[gp].Bytes)
                .ToList();

            (piece.MaterialGamePaths ??= new(StringComparer.OrdinalIgnoreCase))[leaf] = ranked;

            // The same candidates as files with their supplying options, in ranked order.
            (piece.MaterialOptions ??= new(StringComparer.OrdinalIgnoreCase))[leaf] =
                [.. ranked.SelectMany(gp => suppliers[gp])];
        }

        piece.TextureOptions = TextureSuppliers(pack, suppliers, piece.MaterialOptions);

        // Which of the pack's options reveal each material — see ContentPiece.MaterialGates.
        var gates = new List<ContentMaterialGate>();
        foreach (var v in kept)
        {
            if (v.MaterialAttributes is not { Count: > 0 } byMat) continue;
            foreach (var (matName, attrs) in byMat)
                foreach (var g in pack.Groups)
                    foreach (var o in g.Options)
                        if (o.Attributes.Any(a => attrs.Contains(a, StringComparer.Ordinal))
                            && !gates.Any(x => x.Material == matName && x.Group == g.Name && x.Option == o.Name))
                            gates.Add(new ContentMaterialGate
                            { Material = matName, Group = g.Name, Option = o.Name });
        }
        if (gates.Count > 0) piece.MaterialGates = gates;

        return piece;
    }

    // ── register ─────────────────────────────────────────────────────────────

    /// <summary>The import adds a group of its own so the pack's pieces can be picked one at a time.</summary>
    private static bool NeedsPieceGroup(ImportPreview preview)
        => !preview.InstallOnly && preview.PieceGroupName != null && preview.GateOptions.Count > 0;

    /// <summary>
    /// Write a v3 pack's piece group once Penumbra has added (and so upgraded) its folder. Null when that worked or
    /// there was nothing to write; otherwise the failure, with the registration undone. Every Penumbra call runs on
    /// the framework thread, since Penumbra raises events that read game objects on the calling thread.
    /// </summary>
    /// <param name="onPool">Running on the pool, so Penumbra calls are marshalled. False for the inline teardown
    /// path, which is already where Penumbra calls belong.</param>
    private ImportResult? WritePieceGroupAfterUpgrade(ImportPreview preview, string dirName, bool onPool)
    {
        if (!NeedsPieceGroup(preview) || preview.Pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
            return null;
        if (OnFramework(onPool, penumbra.GetModDirectory) is not { Length: > 0 } modsRoot)
            return new(false, false, Loc.Localize("ContentImport.Fail.NoModDir", "Penumbra's mod directory isn't available."));

        var root = Path.Combine(modsRoot, dirName);
        if (PenumbraModMeta.IsLegacyFolder(root))
            OnFramework(onPool, () => penumbra.ReloadModDirectory(dirName));
        if (PenumbraModMeta.IsLegacyFolder(root))
        {
            log.Warning("[Proteus] imported {0}: Penumbra added the mod but left it in the pre-v4 layout, so its "
                      + "piece group could not be written — removing it again", dirName);
            UndoRegistration(root, dirName, onPool);
            return new(false, false, string.Format(Loc.Localize("ContentImport.Fail.NotUpgraded.Fmt",
                "Penumbra did not upgrade \"{0}\" from its older format, so its pieces could not be made "
                + "switchable. The import was undone; try importing the pack again."), dirName));
        }

        try
        {
            PenumbraModMeta.WriteMultiSelectGroup(
                root, preview.Pack.Groups.Count, preview.PieceGroupName!, preview.GateOptions, defaultSettings: 0);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Proteus] imported {0}: could not write its piece group after Penumbra upgraded it — "
                        + "removing it again", dirName);
            UndoRegistration(root, dirName, onPool);
            return new(false, false, string.Format(Loc.Localize("ContentImport.Fail.PieceGroup.Fmt",
                "Could not add the piece switches to \"{0}\": {1} The import was undone; try importing the "
                + "pack again."), dirName, ex.Message));
        }

        OnFramework(onPool, () => penumbra.ReloadModDirectory(dirName));
        log.Information("[Proteus] imported {0}: Penumbra upgraded the v3 pack, piece group added", dirName);
        return null;
    }

    /// <summary>
    /// A Penumbra call on the framework thread: marshalled and waited for when <paramref name="onPool"/>, direct
    /// otherwise. Blocks with GetResult, since an await continuation would resume on the framework thread.
    /// </summary>
    private static T OnFramework<T>(bool onPool, Func<T> call)
        => onPool ? Plugin.Framework.RunOnFrameworkThread(call).GetAwaiter().GetResult() : call();

    /// <summary>
    /// Take a mod this import registered back out: Penumbra forgets it and the folder is deleted, so the name is free.
    /// </summary>
    private void UndoRegistration(string root, string dirName, bool onPool)
    {
        var ec = OnFramework(onPool, () => penumbra.DeleteModDirectory(dirName));
        if (ec != PenumbraApiEc.Success)
            log.Warning("[Proteus] DeleteMod({0}) -> {1}", dirName, ec);
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) { log.Warning(ex, "[Proteus] could not delete {0}; remove it in Penumbra", root); }
    }

    /// <summary>The outcome of a registration. Warning is a success that still needs the user to act.</summary>
    public readonly record struct ImportResult(bool Ok, bool Warning, string Message);

    /// <summary>A v3 pack's piece group being written on the pool, and the import waiting on it.</summary>
    private sealed record PendingUpgrade(PreparedImport Prepared, Task<ImportResult?> Write);

    private PendingUpgrade? pendingUpgrade;

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra, enable it, open Penumbra to it and recomposite. Framework
    /// thread. Returns null while a v3 pack's piece group is written on the pool; call <see cref="Pump"/> until it answers.
    /// </summary>
    /// <param name="quiet">
    /// Register and nothing else (no window, no recomposite), for the teardown path. The piece group is written inline.
    /// </param>
    public ImportResult? Register(PreparedImport prepared, bool quiet = false)
    {
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

        if (NeedsUpgradeWrite(prepared.Preview))
        {
            if (quiet)
            {
                if (WritePieceGroupAfterUpgrade(prepared.Preview, dirName, onPool: false) is { } failure) return failure;
            }
            else
            {
                var preview = prepared.Preview;
                pendingUpgrade = new(prepared, Task.Run(() => WritePieceGroupAfterUpgrade(preview, dirName, onPool: true)));
                return null;
            }
        }

        return Finish(prepared, quiet);
    }

    /// <summary>
    /// Continue a registration <see cref="Register"/> left pending. Null while the piece group is still being
    /// written; the import's result once it is. Harmless to call with nothing pending. Framework thread.
    /// </summary>
    public ImportResult? Pump()
    {
        if (pendingUpgrade is not { } p || !p.Write.IsCompleted) return null;
        pendingUpgrade = null;
        return Completed(p, quiet: false);
    }

    /// <summary>
    /// Teardown's answer to a pending registration: finish it quietly if its write is done, else leave it. Never
    /// waited for: the write needs the framework thread this would block.
    /// </summary>
    public void FinishPendingOnUnload()
    {
        if (pendingUpgrade is not { } p) return;
        pendingUpgrade = null;
        if (p.Write.IsCompleted) Completed(p, quiet: true);
    }

    private ImportResult Completed(PendingUpgrade p, bool quiet)
    {
        if (p.Write.IsFaulted)
        {
            var ex = p.Write.Exception!.GetBaseException();
            log.Error(ex, "[Proteus] imported {0}: writing its piece group failed", p.Prepared.DirName!);
            return new(false, false, string.Format(Loc.Localize("ContentImport.Fail.PieceGroup.Fmt",
                "Could not add the piece switches to \"{0}\": {1} The import was undone; try importing the "
                + "pack again."), p.Prepared.DirName!, ex.Message));
        }
        return p.Write.Result ?? Finish(p.Prepared, quiet);
    }

    /// <summary>A v3 pack whose piece group has to wait for Penumbra's upgrade — see WritePieceGroupAfterUpgrade.</summary>
    private static bool NeedsUpgradeWrite(ImportPreview preview)
        => NeedsPieceGroup(preview) && preview.Pack.FileVersion < PenumbraModMeta.SingleFileVersion;

    /// <summary>
    /// Every multi-select group's selection, as this import wrote its default. A collection keeps a removed mod's
    /// settings by folder name and hands them to the next mod added under it, so re-importing into a name used before
    /// would otherwise inherit the old import's ticks, not these defaults: gated options left off, so each piece
    /// needed its author option ticked as well as its switch.
    /// </summary>
    internal static Dictionary<string, List<string>> ImportSelection(ImportPreview preview)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (preview.InstallOnly) return result;   // the author's own defaults, untouched

        var keepOn = GatedGroupDefaults(preview);
        foreach (var group in preview.Pack.Groups)
        {
            if (!string.Equals(group.Type, "Multi", StringComparison.OrdinalIgnoreCase)) continue;
            result[group.Name] = keepOn.TryGetValue(group.Name, out var mask)
                ? [.. group.Options.Where((_, i) => i < 32 && (mask & (1 << i)) != 0).Select(o => o.Name)]
                : [];
        }
        if (NeedsPieceGroup(preview)) result[preview.PieceGroupName!] = [];
        return result;
    }

    private void ApplyImportSelection(Guid collectionId, string dirName, ImportPreview preview)
    {
        // One batch: each write raises ModSettingChanged, which would otherwise force a recomposite per group, each
        // cancelling the last. Finish triggers the one recomposite the import needs.
        using var batch = compositor.SuppressModSettingEvents();
        foreach (var (group, options) in ImportSelection(preview))
        {
            var ec = penumbra.SetModOption(collectionId, dirName, group, options);
            if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                log.Warning("[Proteus] imported {0}: could not set group \"{1}\" to [{2}] ({3})",
                    dirName, group, string.Join(", ", options), ec);
        }
    }

    /// <summary>Everything after the add and the piece group: enable, open Penumbra, recomposite, report.</summary>
    private ImportResult Finish(PreparedImport prepared, bool quiet)
    {
        var dirName = prepared.DirName!;
        var preview = prepared.Preview!;
        var collId = penumbra.GetPlayerCollectionId();
        if (collId.HasValue)
        {
            penumbra.SetModEnabled(collId.Value, dirName, true);
            ApplyImportSelection(collId.Value, dirName, preview);
        }
        else
            log.Warning("[Proteus] imported {0}: no player collection — enable it manually", dirName);

        if (!quiet)
        {
            penumbra.OpenToMod(dirName);
            compositor.TriggerRecomposite("content-imported");
        }

        log.Information("[Proteus] imported content pack {0} -> {1} ({2} piece(s), {3} skipped){4}",
            Path.GetFileName(preview.SourcePath), dirName, prepared.Pieces, prepared.Skipped,
            quiet ? " [quiet: plugin unloading]" : "");

        var tail = prepared.Skipped > 0
            ? string.Format(Loc.Localize("ContentImport.Result.SkippedTail.Fmt", " (skipped: {0})"), prepared.Skipped)
            : "";

        // Amber is for a problem: only a piece that came out wrong (FaultyUnits), not skipped body meshes or
        // pieces arriving switched off.
        var warn = preview.FaultyUnits > 0;

        // Installed rather than converted: no pieces to count. See ImportPreview.InstallOnly.
        if (preview.InstallOnly)
            return new(true, false, string.Format(Loc.Localize("ContentImport.Result.Installed.Fmt",
                "Installed \"{0}\". It was already a Proteus mod, so it went in exactly as its author "
              + "built it — enabled and opened in Penumbra, where its options are chosen."),
                dirName));

        if (preview.PieceGroupName is { } gate)
            return new(true, warn, string.Format(Loc.Localize("ContentImport.Result.Pieces.Fmt",
                "Imported \"{0}\" — pieces: {1}{2}. They arrive switched OFF: tick the ones you want "
              + "under \"{3}\" in Penumbra, which is now open on this mod."),
                dirName, prepared.Pieces, tail, gate));

        return new(true, warn, string.Format(Loc.Localize("ContentImport.Result.Ok.Fmt",
            "Imported \"{0}\" — pieces: {1}{2}. Enabled it and opened it in Penumbra. Its options are chosen "
          + "in Penumbra, and every one you select is worn at once."),
            dirName, prepared.Pieces, tail));
    }
}
