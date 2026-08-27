using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Imports a Penumbra <c>.pmp</c> that ships GEOMETRY as a Proteus content pack.
/// <para/>
/// The mod stays an ordinary Penumbra mod — Penumbra still owns whether it is on and which of its options
/// are selected, exactly as it does for an overlay pack. Two things change on the way in:
/// <list type="number">
/// <item>every <c>.mdl</c> redirect is removed from the manifest, and</item>
/// <item>a <c>Proteus/metadata.json</c> sidecar names those models instead.</item>
/// </list>
/// Both halves matter. Dropping the redirects is what stops two options in one multi-select group from
/// fighting over a single game path — which is the whole reason a pack like this cannot express "belly
/// button AND hip dermals" on its own — and it is also what stops a pack whose models are a stock mesh with
/// the vanilla geometry emptied out from deleting the wearer's body when Penumbra publishes one. Naming them
/// in the sidecar is what lets the compositor append their meshes into the carrier accessory instead, where
/// every selected option can be worn at once.
/// <para/>
/// Shaped like <see cref="OnionImportService"/> and for the same reason: <see cref="Inspect"/> is cheap and
/// runs on the frame that picked the file, <see cref="Prepare"/> copies tens of megabytes and must not,
/// and <see cref="Register"/> talks to Penumbra so it has to be back on the framework thread.
/// </summary>
public sealed class ContentImportService
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
    /// One model an option ships, and whether it can be appended.
    /// <para/>
    /// <paramref name="Bindings"/> maps each of the model's DRAWN material names to the pack file backing
    /// it. <paramref name="Unbound"/> lists the drawn material names the pack ships nothing for — a piece
    /// with any of those is reported rather than imported, because binding a mesh to a material its model
    /// does not name is a guess, and the wrong guess renders a metal piercing as skin.
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
        /// Material name → the attribute names of the submeshes drawn with it: the switches a mod flips by
        /// name to show and hide parts of one model.
        /// <para/>
        /// Read here because only the importer has the .mdl open. It answers two questions later: whether
        /// this model is one the pack ALREADY controls (so Proteus need not add a checkbox of its own), and
        /// which of the pack's options each material answers to (so the colour panel can show tabs for the
        /// pieces actually being worn, named after the options that turn them on).
        /// </summary>
        IReadOnlyDictionary<string, List<string>>? MaterialAttributes = null)
    {
        public bool Import => Problem == null && Bindings.Count > 0;
    }

    /// <summary>
    /// One thing the user can tick: a garment, with every race variant of it underneath.
    /// <para/>
    /// The unit rather than the model is what gets listed and gated, because a pack that ships the same shirt
    /// for five races ships five models of ONE garment. Listing those separately would put five entries in
    /// the picker, four of them wrong for whoever is wearing it.
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
        IReadOnlyList<string> Warnings)
    {
        public string Name => Pack.Name;
        public string Author => Pack.Author;
        public string? Description => string.IsNullOrWhiteSpace(Pack.Description) ? null : Pack.Description;
        public string? Website => string.IsNullOrWhiteSpace(Pack.Website) ? null : Pack.Website;

        /// <summary>Pieces that can actually be appended.</summary>
        public int ImportableUnits => Units.Count(u => u.Import);

        public bool AnyImportable => ImportableUnits > 0;

        /// <summary>Every piece the pack ships, importable or not — the count the tab reports against.</summary>
        public int TotalUnits => Units.Count;

        /// <summary>The names the synthesized group will offer, in listing order.</summary>
        public IReadOnlyList<string> GateOptions
            => [.. Units.Where(u => u.Import && u.GateOption != null)
                        .Select(u => u.GateOption!).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Read the pack and work out which of its options carry geometry Proteus can append. Throws
    /// <see cref="InvalidDataException"/> when the file isn't a readable pack.
    /// </summary>
    public static ImportPreview Inspect(
        string pmpPath, IPluginLog? log = null, Func<int, int, string?>? itemName = null)
    {
        var pack = PenumbraPackage.Read(pmpPath);
        var warnings = new List<string>();

        // Materials are matched across the WHOLE pack, not within the option that ships the model. Packs
        // routinely put their shared material in one always-on group and their meshes in another — the
        // sample piercings pack does exactly that — so an option-local search would find nothing and
        // reject every mesh in the pack.
        var materialsByLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, entry) in pack.AllFiles)
        {
            if (!gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) continue;
            materialsByLeaf.TryAdd(Path.GetFileName(gamePath), entry);
            materialsByLeaf.TryAdd(Path.GetFileName(entry), entry);
        }

        // Where every model in the pack comes from. Default-data models are in here as well as the ones
        // inside options — a pack with no option groups at all keeps ALL of its models there, and reading
        // only the grouped ones is what used to make such a pack import as "nothing usable".
        var sources = new List<(string? Group, string? Option, string GamePath, string Entry)>();
        foreach (var (gamePath, entry) in pack.DefaultFiles)
            if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                sources.Add((null, null, gamePath, entry));

        foreach (var group in pack.Groups)
        {
            if (!IsSelectable(group.Type))
            {
                // Silent for an Imc group. It selects no MODEL, so it contributes nothing to the piece list
                // — but that is not news worth a warning: its options are the pack's own show/hide toggles
                // and the composite honours them (see ContentAttributeGroup). A pack like deadrose carries
                // one per garment, so warning per group filled the preview with three amber lines saying
                // that a thing which works does not.
                if (!string.Equals(group.Type, "Imc", StringComparison.OrdinalIgnoreCase))
                    warnings.Add(string.Format(Loc.Localize("ContentImport.Warn.GroupType.Fmt",
                        "Group \"{0}\" is a {1} group, which Proteus can't place — its options are left "
                      + "alone."),
                        group.Name, group.Type));
                continue;
            }
            foreach (var option in group.Options)
                foreach (var (gamePath, entry) in option.Files)
                    if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                        sources.Add((group.Name, option.Name, gamePath, entry));
        }

        // Read in ONE pass over the archive rather than one open per file: this runs on the frame the user
        // picked the pack, and a pack with dozens of models would otherwise re-read the zip's central
        // directory dozens of times inside a single draw call.
        var models = PenumbraPackage.ReadEntries(pack.Path, sources.Select(x => x.Entry));

        // Collapse into units. The key carries the OPTION as well as the slot and set, because two options
        // of one group deliberately redirect the same path — that is what an option group is — so keying on
        // the set alone would merge two alternatives into a single entry.
        var byUnit = new Dictionary<(string?, string?, string, string), List<PiecePlan>>();
        var unitOrder = new List<(string?, string?, string, string)>();
        var slotOf = new Dictionary<(string?, string?, string, string), ContentSlot.Parsed>();

        foreach (var (group, option, gamePath, entry) in sources)
        {
            if (ContentSlot.Parse(gamePath) is not { } slot)
            {
                log?.Warning("[Proteus] content import: {0} is not a character model path — skipping", gamePath);
                continue;
            }
            var key = (group, option, slot.Label, slot.SetTag);
            if (!byUnit.TryGetValue(key, out var list))
            {
                byUnit[key] = list = new List<PiecePlan>();
                unitOrder.Add(key);
                slotOf[key] = slot;
            }
            models.TryGetValue(entry, out var bytes);
            list.Add(PlanPiece(gamePath, entry, bytes, materialsByLeaf, log) with { RaceCode = slot.RaceCode });
        }

        // How many distinct garments each of the author's options ships. One means their own option already
        // selects it and we add nothing; more means the option bundles an outfit, and its pieces are gated
        // by SLOT — the identity that stays stable as the user switches between that group's options, where
        // the set id would not.
        var unitsPerOption = unitOrder.GroupBy(k => (k.Item1, k.Item2))
            .ToDictionary(g => g.Key, g => g.Count());

        // Attributes the pack's own options switch on. A pack holding many accessories in ONE model gives
        // each a named attribute and a checkbox, redirecting no files at all — so an option that looks empty
        // is still a real selector, and a model those checkboxes already drive must not get a second one.
        var packToggles = pack.Groups
            .SelectMany(g => g.Options)
            .SelectMany(o => o.Attributes)
            .ToHashSet(StringComparer.Ordinal);

        var units = new List<PieceUnit>();
        bool selfDriven = false;   // at least one model the pack's own checkboxes already drive
        foreach (var key in unitOrder)
        {
            var (group, option, _, setTag) = key;
            var slot = slotOf[key];
            var name = itemName?.Invoke(slot.Category, ContentSlot.SetIdOf(setTag) ?? -1);

            // "Nothing else selects it" is the whole reason an unconditional model gets a switch of ours —
            // so it stops being true the moment the pack's own checkboxes reach into that model. They do
            // when its attributes are ones those options toggle, and then our switch is pure friction: a
            // box to tick before any of the pack's own boxes mean anything, which also equips everything
            // at once the moment it is ticked.
            bool packControls = byUnit[key].Any(v =>
                v.MaterialAttributes is { } byMat
                && byMat.Values.Any(names => names.Any(packToggles.Contains)));

            string? gate =
                group == null                         ? (packControls ? null : ContentSlot.Label(slot, name))
                : unitsPerOption[(group, option)] > 1 ? slot.Label   // one slot of a bundle
                : null;                                             // its own option selects it

            units.Add(new PieceUnit(group, option, slot, name, gate, byUnit[key]));

            // Only when a gate was ACTUALLY suppressed, and only for a unit that will be imported. On the
            // grouped arms packControls changes nothing — those units can still synthesize a group — so
            // setting this from packControls alone told the user Proteus was adding no checkboxes while it
            // was about to add several.
            if (group == null && packControls && units[^1].Import) selfDriven = true;
        }

        var gateGroup = units.Any(u => u.Import && u.GateOption != null)
            ? UniqueGroupName(pack)
            : null;

        if (units.Count == 0)
            warnings.Add(Loc.Localize("ContentImport.Warn.NoModels",
                "No option in this pack redirects a model, so there is no geometry for Proteus to append. "
              + "Install it in Penumbra instead."));

        // Which races the pack is actually built for, said BEFORE it is imported.
        //
        // Most gear ships one model in the shared shape the game resizes for everyone, and those carry no
        // race of their own — nothing to report. A pack built for one race only fits that race, because
        // Proteus does not resize geometry between races, and finding that out after importing means
        // wondering why an enabled mod shows nothing.
        var raceCodes = units
            .Where(u => u.Import)
            .SelectMany(u => u.Variants.Where(v => v.Import).Select(v => v.RaceCode))
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (raceCodes.Count > 0 && raceCodes.All(c => !ModelRace.IsSharedShape(c)))
            warnings.Add(string.Format(Loc.Localize("ContentImport.Warn.RaceOnly.Fmt",
                "This pack's models are built for {0}. Proteus does not resize geometry between races, so "
              + "its pieces will only appear on a character of that race."),
                ModelRace.DescribeAll(raceCodes)));

        // Said because the absence of something is otherwise invisible. Proteus normally adds a checkbox per
        // piece; for a pack that switches its own pieces on by model attribute it deliberately adds none, so
        // the pack's boxes work directly instead of needing one of ours ticked first. The consequence worth
        // knowing is the other half of that: a piece the pack does NOT gate is one its author meant to be
        // worn always, and there is now no per-piece switch to turn it off.
        if (selfDriven)
            warnings.Add(Loc.Localize("ContentImport.Warn.SelfDriven",
                "This pack switches its own pieces on and off, so Proteus adds no checkboxes of its own — "
              + "use the pack's. Any piece it does not switch is worn whenever the mod is enabled."));

        return new ImportPreview(pmpPath, pack, units, gateGroup, warnings);
    }

    /// <summary>
    /// The group the import adds so individual pieces can be picked. Suffixed until it is free: writing a
    /// group REPLACES any of the same name, so colliding with one the author happens to have called the
    /// same thing would destroy theirs.
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
        // Meshes left to the character's own skin — reported apart from `unbound` because they are a choice
        // rather than a shortfall, and only matter when they are ALL a model has.
        var skinOnly = new List<string>();

        PiecePlan Unreadable(string why)
            => new(gamePath, entry, bindings, unbound, 0, 0,
                   string.Format(Loc.Localize("ContentImport.Problem.Unreadable.Fmt",
                       "not a readable model ({0})"), why));

        // The manifest names a file the archive doesn't carry. Rare, but a pack edited by hand can say it.
        if (model == null) return Unreadable(entry);

        List<string> declared;
        Dictionary<string, List<string>> byMaterial;
        try
        {
            // Both from one walk — see MaterialsAndAttributes. These models run to six figures of vertices
            // and the parse is the expensive half of either question.
            (declared, byMaterial) = SecondSkinWriter.MaterialsAndAttributes(model);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} is not a readable model", entry);
            return Unreadable(ex.Message);
        }

        var used = SecondSkinService.UsedMaterialNames(model, declared);
        if (used.Count == 0)
            return new PiecePlan(gamePath, entry, bindings, unbound, 0, 0,
                Loc.Localize("ContentImport.Problem.NoGeometry", "no geometry — every mesh in it is empty"));

        int meshes = 0, vertices = 0;
        foreach (var name in used)
        {
            var leaf = name.TrimStart('/');

            // The pack's OWN binding is asked for first, and that ordering matters. A material can be named
            // like the body's and still be the pack's — the rebound piercings pack redirects
            // mt_c0201b0001_a.mtrl at its own piercing material, and skipping it on the name alone would
            // throw away the very meshes that redirect exists to serve.
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
            // Unbound AND named like the body's own material: a mesh Proteus deliberately leaves behind, not
            // a binding the author forgot. An outfit pack ships the body it was fitted to so the garment
            // sits right in Penumbra; the wearer already has one, and appending a second would put a
            // duplicate body on them. Counting it as unbound made the preview advise a re-export that would
            // fix nothing.
            else if (!SecondSkinWriter.IsBodySkinMaterial(leaf))
                unbound.Add(leaf);
            else
                skinOnly.Add(leaf);
        }

        string? problem = null;
        if (bindings.Count == 0)
            problem = unbound.Count > 0
                ? string.Format(Loc.Localize("ContentImport.Problem.Unbound.Fmt",
                    "its mesh names {0}, which this pack does not ship. Rebind the mesh to one of the pack's "
                  + "own materials and re-export."), string.Join(", ", unbound))
                // Nothing unbound and nothing bound: every mesh in it is the body. Real — an outfit pack
                // ships one per size — and not a fault, so it says what it is rather than asking for a fix.
                : string.Format(Loc.Localize("ContentImport.Problem.BodyOnly.Fmt",
                    "every mesh in it is the wearer's own body ({0}), so there is nothing to add."),
                    string.Join(", ", skinOnly));

        return new PiecePlan(gamePath, entry, bindings, unbound, meshes, vertices, problem,
                             MaterialAttributes: byMaterial);
    }

    // ── write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mod written to disk by <see cref="Prepare"/> and waiting for <see cref="Register"/>, or the reason
    /// nothing was written.
    /// </summary>
    public sealed record PreparedImport(
        bool Ok, string Message, string? DirName, ImportPreview? Preview, int Pieces, int Skipped);

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
            // Same sentence, same situation as the Onion import's — one key, one translation.
            return Fail(Loc.Localize("Import.NeedName", "Enter a mod name."));
        if (!preview.AnyImportable)
            return Fail(Loc.Localize("ContentImport.Fail.NothingUsable", "Nothing in this pack can be imported."));
        if (!File.Exists(preview.SourcePath))
            return Fail(string.Format(Loc.Localize("ContentImport.Fail.Gone.Fmt",
                "The pack is no longer there: {0}"), preview.SourcePath));

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
    /// Unpack the archive, strip the model redirects from the manifests and write the Proteus sidecar.
    /// Pure filesystem work, no IPC, so it can be exercised offline against a temp directory.
    /// </summary>
    internal static void WriteMod(
        string root, string modName, string author, ImportPreview preview, IPluginLog? log = null)
    {
        Directory.CreateDirectory(root);

        // The pack's own layout is preserved verbatim: its manifest names files by that layout, and the
        // sidecar written below names the models the same way, so nothing has to be rewritten to point
        // somewhere else.
        using (var zip = ZipFile.OpenRead(preview.SourcePath))
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var rel = PenumbraPackage.Normalize(e.FullName).Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }

        // The redirects the sidecar is about to name — and ONLY those. A unit this import refused keeps its
        // own, because Proteus is not taking it over and something has to publish it. Keyed on game path AND
        // file, so an option sharing a path with an imported one is judged on its own redirect.
        var taken = preview.Units
            .Where(u => u.Import)
            .SelectMany(u => u.Variants.Where(v => v.Import).Select(v => RedirectKey(v.GamePath, v.Entry)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        StripModelRedirects(root, preview.Pack, taken, log);

        // The group that makes individual pieces pickable, written with EVERY option off. An imported
        // outfit therefore contributes nothing until the user asks for a piece, which is also what keeps it
        // off the host accessory's ten-material budget until then.
        if (preview.PieceGroupName is { } gate && preview.GateOptions.Count > 0)
            PenumbraModMeta.WriteMultiSelectGroup(
                root, preview.Pack.Groups.Count, gate, preview.GateOptions, defaultSettings: 0);

        var metadata = BuildSidecar(preview, modName, author);
        var metaJson = JsonSerializer.Serialize(metadata, ProteusJson.MetadataWrite);
        var sidecarDir = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir);
        Directory.CreateDirectory(sidecarDir);
        PenumbraModMeta.AtomicWrite(Path.Combine(sidecarDir, "metadata.json"), metaJson);
    }

    /// <summary>
    /// Remove the <c>.mdl</c> redirects Proteus is taking over from the copied manifests, in whichever
    /// layout the pack uses. The files themselves stay — the sidecar names them — so this only changes who
    /// publishes them: not Penumbra (which can only ever pick ONE option per game path, and would replace
    /// the wearer's body with a mesh that has had its vanilla geometry emptied out) but Proteus, which
    /// appends them.
    /// <para/>
    /// Only the redirects in <paramref name="taken"/> — the models that actually reached the sidecar.
    /// Stripping every <c>.mdl</c> regardless was silent sabotage of the pieces this import REFUSED: a pack
    /// with one option whose mesh names a material it does not ship has that option reported as skipped and
    /// left out of the sidecar, and stripping it too meant Penumbra stopped publishing it while Proteus
    /// never picked it up. An option that worked before the import rendered nothing after it, and the
    /// preview's own "N skipped" line was the only trace.
    /// <para/>
    /// A redirect is game path AND archive entry, not the path alone. Two options claiming ONE game path
    /// from different files is not an edge case — it is the shape this whole feature exists for, and the
    /// sample pack is built that way. Matching on the path meant an imported option put that path in the
    /// set and a refused option sharing it lost its redirect on the strength of its neighbour's success,
    /// which is the same silent sabotage one level down. Two options naming the same path and the same file
    /// cannot disagree, since they plan identically, so the pair is a safe key.
    /// </summary>
    private static void StripModelRedirects(
        string root, PenumbraPackage.Contents pack, IReadOnlySet<string> taken, IPluginLog? log)
    {
        if (pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log, manifest =>
            {
                if (manifest["DefaultData"] is JsonObject dd) StripFiles(dd, taken);
                if (manifest["Groups"] is JsonArray groups)
                    foreach (var g in groups)
                        if (g is JsonObject go)
                            StripGroup(go, taken);
            });
            return;
        }

        EditJson(Path.Combine(root, PenumbraModMeta.LegacyDefaultMod), log, o => StripFiles(o, taken));
        foreach (var group in pack.Groups)
            if (group.Entry != null)
                EditJson(Path.Combine(root, group.Entry.Replace('/', Path.DirectorySeparatorChar)), log,
                    o => StripGroup(o, taken));
    }

    private static void StripGroup(JsonObject group, IReadOnlySet<string> taken)
    {
        if (group["Options"] is JsonArray opts)
            foreach (var o in opts)
                if (o is JsonObject oo) StripFiles(oo, taken);
        // A Combining group's redirects hang off Containers rather than Options. Nothing here imports one,
        // but a pack can mix kinds, and leaving a model redirect behind in one would put the two publishers
        // back in conflict.
        if (group["Containers"] is JsonArray containers)
            foreach (var c in containers)
                if (c is JsonObject co) StripFiles(co, taken);
    }

    private static void StripFiles(JsonObject owner, IReadOnlySet<string> taken)
    {
        if (owner["Files"] is not JsonObject files) return;
        var doomed = files
            .Where(p => p.Value?.GetValue<string>() is { } entry
                     && taken.Contains(RedirectKey(p.Key, entry)))
            .Select(p => p.Key)
            .ToList();
        foreach (var k in doomed) files.Remove(k);
    }

    /// <summary>
    /// One manifest redirect, as a comparable key. Both halves normalised, because a manifest writes its
    /// archive entries with backslashes while the plans carry them with forward ones, and neither side
    /// guarantees case. NUL separates them: it cannot occur in either a game path or an archive entry, so
    /// no pair of halves can be spelled two ways.
    /// </summary>
    private static string RedirectKey(string gamePath, string entry)
        => PenumbraPackage.Normalize(gamePath) + '\0' + PenumbraPackage.Normalize(entry);

    /// <summary>
    /// Edit one manifest in place, or say why it could not be.
    /// <para/>
    /// A failure here is NOT harmless and must not pass in silence: leaving a model redirect behind is the
    /// exact conflict this whole import exists to prevent, and its symptom in game — Penumbra replacing the
    /// body with a mesh whose vanilla geometry has been emptied out — points nowhere near a manifest that
    /// would not parse.
    /// </summary>
    private static void EditJson(string path, IPluginLog? log, Action<JsonObject> edit)
    {
        if (!File.Exists(path)) return;
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} could not be read, so its model redirects are "
                           + "still Penumbra's — that pack's pieces will fight over their game paths", path);
            return;
        }
        if (node is not JsonObject root)
        {
            log?.Warning("[Proteus] content import: {0} is not a JSON object, so its model redirects are "
                       + "still Penumbra's — that pack's pieces will fight over their game paths", path);
            return;
        }
        edit(root);
        PenumbraModMeta.AtomicWrite(path, root.ToJsonString(ProteusJson.MetadataWrite));
    }

    /// <summary>The Proteus sidecar mirroring the pack's groups, with one piece per importable model.</summary>
    internal static ProteusMetadata BuildSidecar(ImportPreview preview, string modName, string author)
    {
        var metadata = new ProteusMetadata
        {
            Name = modName,
            Author = author,
        };

        metadata.PieceGroupName = preview.PieceGroupName;

        // The pack's own IMC show/hide toggles. Recorded rather than left to Penumbra because Penumbra's
        // edit lands on the pack's OWN equipment set, and the composite is about to move this geometry onto
        // a host accessory — see ContentAttributeGroup. Options with no bits are skipped: an Imc group can
        // carry a plain variant switch that hides nothing.
        foreach (var g in preview.Pack.Groups.Where(g =>
                     string.Equals(g.Type, "Imc", StringComparison.OrdinalIgnoreCase)))
        {
            var opts = g.Options.Where(o => o.AttributeMask != 0)
                .ToDictionary(o => o.Name, o => (int)o.AttributeMask, StringComparer.Ordinal);
            if (opts.Count == 0 && g.DefaultAttributeMask == 0) continue;
            (metadata.ContentAttributes ??= []).Add(new ContentAttributeGroup
            {
                Group       = g.Name,
                SetId       = g.ImcSetId,
                Slot        = g.ImcSlot,
                DefaultMask = g.DefaultAttributeMask,
                Options     = opts,
            });
        }

        // The extra skeletons this pack's pieces need — see ContentSkeleton. Recorded for the same reason
        // as the IMC toggles above: the pack's own entry names the set IT replaces, and the composite is
        // about to move that geometry somewhere the entry cannot reach.
        //
        // Entries of 0 are dropped. Zero means "no extra skeleton", which is what most of these
        // manipulations are — a mod clearing the set it takes over — and writing one would not enable
        // anything, it would CLEAR the skeleton of whatever body part the composite pointed it at.
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

        // Models the pack redirects outside every option. These used to be dropped: they were looked up
        // against the grouped plans, which never contained them, so a pack with no option groups at all
        // imported as "nothing usable" while the warning claimed they had been taken as always-on pieces.
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
    /// One unit as the sidecar stores it. Its race variants become a <c>Models</c> map rather than separate
    /// pieces, and their material bindings merge: the leaf names are race-specific
    /// (<c>mt_c0101…</c> vs <c>mt_c0201…</c>) so they cannot collide, and whichever model the wearer's race
    /// selects declares only its own.
    /// </summary>
    private static ContentPiece PieceOf(PieceUnit unit, PenumbraPackage.Contents pack)
    {
        var usable = unit.Variants.Where(v => v.Import).ToList();
        var piece = new ContentPiece
        {
            Surface    = ShellSurfaceKind.Body,
            Slot       = unit.Slot.Label,
            GateOption = unit.GateOption,
        };

        if (usable.Count == 1 && string.IsNullOrEmpty(usable[0].RaceCode))
            piece.Model = usable[0].Entry;
        else
            piece.Models = usable.ToDictionary(v => v.RaceCode, v => v.Entry, StringComparer.OrdinalIgnoreCase);

        foreach (var v in usable)
            foreach (var (leaf, entry) in v.Bindings)
                piece.Materials[leaf] = entry;

        // The GAME paths each bound material is published under, so the composite can ask Penumbra which
        // file is live instead of using the one frozen above — see ContentPiece.MaterialGamePaths.
        //
        // Ordered by how many DIFFERENT files the pack puts behind each path, most first. That is the rule
        // in one sentence: a path several files compete over is a path the user chooses between, and a path
        // with one file behind it is fixed. Nothing simpler works — "options before default data" does not,
        // because both candidates can be option redirects. deadrose is the case: its dress material sits at
        // v0002 shipped once by [ dress - main files ], and at v0001 shipped eight times over by
        // [ dress - metal + dye template ]. Only the second is a choice, and only counting finds it.
        var backing = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        void Candidate(string gamePath, string entry)
        {
            if (!gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) return;
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
                    Candidate(gp, entry);
        foreach (var (gp, entry) in pack.DefaultFiles)
            Candidate(gp, entry);

        foreach (var leaf in piece.Materials.Keys)
        {
            var trimmed = leaf.TrimStart('/');
            // OrderByDescending is stable, so paths tied on count keep the order they were seen in.
            var paths = order
                .Where(gp => string.Equals(Path.GetFileName(gp.Replace('\\', '/')), trimmed,
                                           StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(gp => backing[gp].Count)
                .ToList();
            if (paths.Count > 0)
                (piece.MaterialGamePaths ??= new(StringComparer.OrdinalIgnoreCase))[leaf] = paths;
        }

        // Which of the PACK'S options reveal each material — see ContentPiece.MaterialGates. Worked out
        // here, where the model has just been read, because the panel that needs it draws every frame.
        var gates = new List<ContentMaterialGate>();
        foreach (var v in usable)
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

    /// <summary>The outcome of a registration. Warning is a success that still needs the user to act.</summary>
    public readonly record struct ImportResult(bool Ok, bool Warning, string Message);

    /// <summary>
    /// Register a <see cref="Prepare"/>d mod with Penumbra, enable it, open Penumbra to it and recomposite.
    /// Must run on the framework thread.
    /// </summary>
    /// <param name="quiet">
    /// Register and nothing else — no Penumbra window, no recomposite. For the teardown path, where the
    /// point is only that a folder already written into Penumbra's directory isn't orphaned; a recomposite
    /// there would wake into half-disposed services. Same reasoning as <see cref="OnionImportService"/>.
    /// </param>
    public ImportResult Register(PreparedImport prepared, bool quiet = false)
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

        var collId = penumbra.GetPlayerCollectionId();
        if (collId.HasValue)
            penumbra.SetModEnabled(collId.Value, dirName, true);
        else
            log.Warning("[Proteus] imported {0}: no player collection — enable it manually", dirName);

        if (!quiet)
        {
            penumbra.OpenToMod(dirName);
            compositor.TriggerRecomposite("content-imported");
        }

        log.Information("[Proteus] imported content pack {0} -> {1} ({2} piece(s), {3} skipped){4}",
            Path.GetFileName(prepared.Preview.SourcePath), dirName, prepared.Pieces, prepared.Skipped,
            quiet ? " [quiet: plugin unloading]" : "");

        var tail = prepared.Skipped > 0
            ? string.Format(Loc.Localize("ContentImport.Result.SkippedTail.Fmt", " (skipped: {0})"), prepared.Skipped)
            : "";

        // A pack whose pieces arrive switched off has, from the outside, done nothing at all. Say so in the
        // result rather than letting the user go looking for a bug: this is the one message that explains
        // why the character did not change.
        if (prepared.Preview.PieceGroupName is { } gate)
            return new(true, true, string.Format(Loc.Localize("ContentImport.Result.Pieces.Fmt",
                "Imported \"{0}\" — pieces: {1}{2}. They arrive switched OFF: tick the ones you want "
              + "under \"{3}\" in Penumbra, which is now open on this mod."),
                dirName, prepared.Pieces, tail, gate));

        return new(true, false, string.Format(Loc.Localize("ContentImport.Result.Ok.Fmt",
            "Imported \"{0}\" — pieces: {1}{2}. Enabled it and opened it in Penumbra. Its options are chosen "
          + "in Penumbra, and every one you select is worn at once."),
            dirName, prepared.Pieces, tail));
    }
}
