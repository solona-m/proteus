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
        IReadOnlyDictionary<string, List<string>>? MaterialAttributes = null,
        /// <summary>
        /// This model was dropped ON PURPOSE: every mesh in it is the wearer's own body, which Proteus
        /// leaves to the character's own skin.
        /// <para/>
        /// A flag rather than something inferred from the other fields, because the two "nothing bound"
        /// endings are otherwise identical — a body-only model and an UNREADABLE one both arrive with empty
        /// Bindings and empty Unbound. Reading emptiness as "deliberate" made a corrupt .mdl import as a
        /// clean success with the piece silently missing.
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
        IReadOnlyList<string> Warnings,
        /// <summary>
        /// The pack's OWN <c>Proteus/metadata.json</c>, when it ships one, or null for an ordinary mod.
        /// <para/>
        /// Non-null is what makes the import an INSTALL rather than a conversion — see
        /// <see cref="InstallOnly"/>, which is simply this being present. Such a pack arrives with no units,
        /// because none of its geometry is being taken over, and is still importable: it is copied in as it
        /// stands. The top of <see cref="Inspect"/> has the reason converting it a second time can be
        /// actively wrong.
        /// </summary>
        ProteusMetadata? AuthoredSidecar = null,
        /// <summary>
        /// Default-data model redirects an option group replaces, as <c>RedirectKey</c> pairs.
        /// <para/>
        /// These never became units — Penumbra would never have loaded them, since an option outranks the
        /// default for one game path — so the <c>taken</c> set <see cref="WriteMod"/> derives from the units
        /// does not cover them. They still have to be stripped: the option's own redirect IS stripped
        /// (Proteus took that model over), which would otherwise promote the shadowed default to winner and
        /// republish the very geometry Proteus is now appending itself.
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
        /// This pack is copied in UNCHANGED — it already carries a Proteus sidecar, so its author has
        /// already chosen what Proteus handles and what Penumbra publishes.
        /// <para/>
        /// Nothing is stripped, no gate group is added, no defaults are cleared and the sidecar is left
        /// exactly as written. The outcome is what dropping the file on Penumbra would have produced;
        /// doing it here only saves the trip.
        /// </summary>
        public bool InstallOnly => AuthoredSidecar != null;

        /// <summary>Whether the Import button does anything — either geometry to take over, or a
        /// ready-made Proteus mod to install as it stands.</summary>
        public bool CanImport => InstallOnly || AnyImportable;

        /// <summary>Every piece the pack ships, importable or not — the count the tab reports against.</summary>
        public int TotalUnits => Units.Count;

        /// <summary>
        /// Pieces that were dropped because something is WRONG with them, as opposed to dropped on purpose.
        /// <para/>
        /// Not the same as <c>TotalUnits - ImportableUnits</c>, and the difference is the whole point of the
        /// property. An outfit pack ships the body it was fitted to; Proteus deliberately leaves those
        /// meshes to the character's own skin, which drops a unit and is the WANTED outcome. Counting that
        /// as a shortfall would put a warning colour on nearly every outfit import — the same
        /// cried-wolf problem as colouring the "pieces arrive switched off" line, which is exactly what this
        /// exists to avoid.
        /// <para/>
        /// A real fault is a mesh naming a material the pack does not ship, a model that will not read, or
        /// one that draws nothing — the import can only report those, and the fix is the author's.
        /// <para/>
        /// Read off <see cref="PiecePlan.BodyOnly"/> rather than inferred from an empty <c>Unbound</c> list:
        /// a body-only model and an UNREADABLE one both arrive with nothing bound and nothing unbound, so
        /// the inference called a corrupt .mdl a clean success.
        /// <para/>
        /// A unit that imports for SOME race is not faulty — a pack missing one race's material still works
        /// for the races it ships, which is the rule <see cref="PieceUnit.Import"/> already states. Of the
        /// rest, one variant failing for a bad reason is enough, so a piece that is body-only for one race
        /// and unbound for another still reports.
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
    public static ImportPreview Inspect(
        string pmpPath, IPluginLog? log = null, Func<int, int, string?>? itemName = null)
    {
        var pack = PenumbraPackage.Read(pmpPath);
        var warnings = new List<string>();

        // A pack that is ALREADY a Proteus mod is INSTALLED, not converted — copied in exactly as it is,
        // which is what dropping it on Penumbra would have done.
        //
        // Its author already decided which of its files Penumbra publishes and which its sidecar names. The
        // import exists to make that decision for a mod that has never had it made; making it a second time
        // does not refine the first, it overrides it, and the override can be flatly wrong. "Picklish - by
        // Solona" is the case: nine of its models sit in SINGLE groups — Top Size XS/S/M/L, Skirt Size
        // Small/Medium/Large — mutually exclusive by construction, where appending is the mechanism for
        // wearing several options AT ONCE. Taking those over would strip the redirects that make Penumbra
        // pick exactly one and hand the choice to a composite built to honour all of them.
        //
        // No units, because none of its geometry is being taken over; read before the models are planned,
        // because that work is wanted for neither the preview nor the write.
        var authored = ReadAuthoredSidecar(pack, log);
        if (authored != null)
            return new ImportPreview(pmpPath, pack, [], null, warnings, authored);

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

        // Counted rather than logged one by one. A pack that gets this wrong gets it wrong in BULK — STREGA
        // declares all 109 of its default redirects that way — and 29 identical warning lines say nothing the
        // first one and a number do not.
        int notGamePaths = 0;
        string? firstNotGamePath = null;

        foreach (var (group, option, gamePath, entry) in sources)
        {
            // A redirect the game can never ASK for is not a piece. Packs exported with their option folders
            // baked into the game path — "Gen 3/Bra, Choker & Gloves/chara/equipment/e6046/model/
            // c0101e6046_top.mdl" — still end in a name ContentSlot reads perfectly, so they parse as real
            // garments and arrive as second copies of models the pack ALSO ships correctly inside its groups.
            // Two copies of one race under one unit is what made STREGA fail to import outright.
            //
            // Nothing is lost by dropping them: Penumbra does not resolve them either, so they are dead
            // entries in the manifest rather than geometry anyone was wearing.
            if (!IsGamePath(gamePath))
            {
                notGamePaths++;
                firstNotGamePath ??= gamePath;
                continue;
            }
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

        if (notGamePaths > 0)
            log?.Warning("[Proteus] content import: {0} model redirect(s) are not paths the game can "
                       + "request — skipping, first is \"{1}\"", notGamePaths, firstNotGamePath!);

        // ── default-data copies a SINGLE group replaces ──────────────────────────
        //
        // Penumbra resolves one file per game path and an option's redirect outranks the default, so a
        // default-data model a Single group overrides is one the game never loaded. Taking it as a piece of
        // its own put the same garment on the character twice — the default copy AND the size the user
        // picked — which read as nothing worse than faint z-fighting, and left the piece checkbox governing
        // the default copy with no visible effect.
        //
        // SINGLE only. A Multi group's option can be off — and after an import always IS, since
        // ClearMultiSelectDefaults empties every one of them — so for those the default really is the copy
        // that renders, and dropping it would take the base garment away until the user found the pack's own
        // box. That is the opposite failure and a worse one.
        var singleGroups = pack.Groups
            .Where(g => string.Equals(g.Type, "Single", StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Per game path: how many Single-group variants claim it, and how many of those Proteus can actually
        // place. Both halves matter — see the all-or-nothing rule below.
        var claims = new Dictionary<string, (int Total, int Importing)>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in unitOrder)
        {
            if (key.Item1 is not { } g || !singleGroups.Contains(g)) continue;
            foreach (var v in byUnit[key])
            {
                var path = PenumbraPackage.Normalize(v.GamePath);
                var c = claims.TryGetValue(path, out var prev) ? prev : default;
                claims[path] = (c.Total + 1, c.Importing + (v.Import ? 1 : 0));
            }
        }

        // Replaced only when EVERY option claiming the path imports. One refused option means the user can
        // select a size Proteus cannot place, and with the default already dropped that selection would wear
        // nothing at all. Keeping the default there costs the duplicate this exists to remove — but showing a
        // garment twice is a far better failure than showing it not at all, so the doubt resolves that way.
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in unitOrder)
        {
            if (key.Item1 != null) continue;                 // unconditional units only
            var list = byUnit[key];
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var v = list[i];
                if (claims.TryGetValue(PenumbraPackage.Normalize(v.GamePath), out var c)
                    && c.Total > 0 && c.Importing == c.Total)
                {
                    // Recorded so WriteMod can strip it too. It is no longer a variant of any unit, so the
                    // `taken` set built from the units would not cover it — and an unstripped default
                    // redirect would republish the very model the option handed to Proteus.
                    shadowed.Add(RedirectKey(v.GamePath, v.Entry));
                    log?.Debug("[Proteus] content import: {0} is replaced by an option — "
                             + "dropping the default copy", v.GamePath);
                    list.RemoveAt(i);
                }
            }
        }
        // A unit every one of whose races was replaced is not a garment of its own any more. Left in, it
        // would offer a checkbox that can never put anything on anyone.
        unitOrder.RemoveAll(k => k.Item1 == null && byUnit[k].Count == 0);

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

        // "Nothing else selects it" is the whole reason an unconditional model gets a switch of ours —
        // so it stops being true the moment the pack's own checkboxes reach into that model. They do
        // when its attributes are ones those options toggle, and then our switch is pure friction: a
        // box to tick before any of the pack's own boxes mean anything, which also equips everything
        // at once the moment it is ticked.
        bool PackControls((string?, string?, string, string) key) => byUnit[key].Any(v =>
            v.MaterialAttributes is { } byMat
            && byMat.Values.Any(names => names.Any(packToggles.Contains)));

        string GateLabel((string?, string?, string, string) key)
            => ContentSlot.Label(slotOf[key],
                itemName?.Invoke(slotOf[key].Category, ContentSlot.SetIdOf(key.Item4) ?? -1));

        // The gate a garment already has from its UNCONDITIONAL copy, keyed on the garment — slot and set,
        // the identity a size group's options all share.
        //
        // A pack that ships a garment in default data AND overrides it per size ships ONE garment, and one
        // checkbox has to govern it however the size is chosen. Gating only the unconditional copy (which is
        // all this used to do) left the size copy ungated, and a Single group always has an option selected —
        // so the garment was worn whatever the checkbox said, and the checkbox did nothing. The shadowing
        // above removes the duplicate geometry; this is what makes the switch mean something.
        //
        // Slot and set joined by NUL and compared case-insensitively, the way RedirectKey pairs its own two
        // halves — NUL because it cannot occur in either, so no pair can be spelled two ways.
        //
        // The case-insensitivity is belt and braces, NOT a live fix: ContentSlot.Parse already lowercases
        // both halves it produces (the set tag is a lowercased kind letter plus four digits, and a label is
        // either a constant from its own table or a lowercased suffix), so today the two spellings cannot
        // arise. It is written this way because a MISS here is silent and its symptom is the bug this whole
        // block exists to fix — the size copy quietly ungated — which is too costly an outcome to rest on a
        // normalisation another file happens to perform. The set beside it is keyed the same way.
        static string GarmentKey(string slotLabel, string setTag) => slotLabel + '\0' + setTag;

        var garmentGate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in unitOrder)
            if (key.Item1 == null && !PackControls(key))
                garmentGate.TryAdd(GarmentKey(key.Item3, key.Item4), GateLabel(key));

        var units = new List<PieceUnit>();
        bool selfDriven = false;   // at least one model the pack's own checkboxes already drive
        foreach (var key in unitOrder)
        {
            var (group, option, _, setTag) = key;
            var slot = slotOf[key];
            var name = itemName?.Invoke(slot.Category, ContentSlot.SetIdOf(setTag) ?? -1);

            bool packControls = PackControls(key);

            string? gate =
                group == null                         ? (packControls ? null : ContentSlot.Label(slot, name))
                // Shares the garment with an unconditional copy: one checkbox, already named, governs both.
                : garmentGate.TryGetValue(GarmentKey(key.Item3, setTag), out var shared) ? shared
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

        return new ImportPreview(pmpPath, pack, units, gateGroup, warnings, ShadowedRedirects: shadowed);
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
        bool bodyOnly = false;
        if (bindings.Count == 0)
        {
            // Three ways to bind nothing, and only the middle one is deliberate. A model that names no
            // drawn material at all is the third: not the body, not a missing material, and NOT something
            // to wave through as a clean import — it lands in the same "faulty" bucket as an unreadable one.
            bodyOnly = unbound.Count == 0 && skinOnly.Count > 0;
            problem = unbound.Count == 0 && skinOnly.Count == 0
                ? Loc.Localize("ContentImport.Problem.NoMeshes",
                    "it draws no meshes, so there is nothing to append.")
                : !bodyOnly
                ? string.Format(Loc.Localize("ContentImport.Problem.Unbound.Fmt",
                    "its mesh names {0}, which this pack does not ship. Rebind the mesh to one of the pack's "
                  + "own materials and re-export."), string.Join(", ", unbound))
                // Nothing unbound and nothing bound: every mesh in it is the body. Real — an outfit pack
                // ships one per size — and not a fault, so it says what it is rather than asking for a fix.
                : string.Format(Loc.Localize("ContentImport.Problem.BodyOnly.Fmt",
                    "every mesh in it is the wearer's own body ({0}), so there is nothing to add."),
                    string.Join(", ", skinOnly));
        }

        return new PiecePlan(gamePath, entry, bindings, unbound, meshes, vertices, problem,
                             MaterialAttributes: byMaterial, BodyOnly: bodyOnly);
    }

    /// <summary>
    /// The pack's own <c>Proteus/metadata.json</c>, or null when it ships none — which is every ordinary
    /// mod, and the common case.
    /// <para/>
    /// Fail-soft on a sidecar that will not parse: a hand-edited one is the author's problem, and refusing
    /// the whole import over it would be worse than importing without carrying its fields. Absent reads the
    /// same as unreadable, so the caller has one case to handle.
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
        // CanImport, not AnyImportable: a ready-made Proteus mod has no units to take over and is still
        // perfectly importable — it is copied in as it stands. See ImportPreview.InstallOnly.
        if (!preview.CanImport)
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

        // A ready-made Proteus mod stops here: the copy above IS the whole import. Everything below edits
        // the pack into something it already is — stripping redirects its author meant Penumbra to publish,
        // adding a gate group over options that already gate themselves, clearing defaults the author set,
        // and writing a derived sidecar over the authored one. See ImportPreview.InstallOnly.
        //
        // The name is still the user's, because the dialog asked for it and this is the file Penumbra's mod
        // list reads.
        if (preview.InstallOnly)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log,
                "the installed mod keeps the pack's own name", manifest => manifest["Name"] = modName);
            return;
        }

        // The redirects the sidecar is about to name — and ONLY those. A unit this import refused keeps its
        // own, because Proteus is not taking it over and something has to publish it. Keyed on game path AND
        // file, so an option sharing a path with an imported one is judged on its own redirect.
        var taken = preview.Units
            .Where(u => u.Import)
            .SelectMany(u => u.Variants.Where(v => v.Import).Select(v => RedirectKey(v.GamePath, v.Entry)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // …plus the default-data copies an option replaces, which are not units at all. See
        // ImportPreview.ShadowedRedirects for why leaving one behind resurrects the geometry.
        if (preview.ShadowedRedirects is { } shadowed) taken.UnionWith(shadowed);
        StripModelRedirects(root, preview.Pack, taken, log);

        // The name the user chose goes into the copied manifest too, not just the sidecar and the folder.
        // An import leaves the original pack installable on its own terms, so the two sit together in
        // Penumbra's mod list — and that list reads THIS file. Without it both rows carry the pack's own
        // name and the only way to tell the Proteus copy apart is to open it.
        EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log,
            "the copied mod keeps the pack's own name and cannot be told from the original in Penumbra's "
          + "mod list",
            manifest => manifest["Name"] = modName);

        ClearMultiSelectDefaults(root, preview.Pack, log);

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
    /// Every multi-select group in the copied pack comes in with NOTHING ticked.
    /// <para/>
    /// The same rule the synthesized piece group is written under, extended to the pack's own: an imported
    /// mod contributes only what the user has asked for. That is worth more here than tidiness, because a
    /// multi-select group is the one place where the pack's selection can be genuinely ambiguous — two
    /// ticked options may redirect the SAME file path, and Penumbra settles that by option priority while
    /// <see cref="SecondSkinService.SelectedMaterialFile"/> takes the first one declared. Starting empty
    /// means the ambiguity only ever arises if the user builds it themselves, one tick at a time.
    /// <para/>
    /// SINGLE groups are left alone. There is no "off" for one — Penumbra always has an option selected —
    /// so clearing the field would just re-elect the first option and silently move a pack's chosen default
    /// print or dye. Imc and Combining groups are likewise untouched: they are not selections of files.
    /// <para/>
    /// This edits the pack's DEFAULT, which Penumbra reads only for a collection that has never seen the
    /// mod. That is exactly the case an import creates, and re-importing over an existing folder leaves a
    /// collection's established choices alone — which is the wanted behaviour either way.
    /// </summary>
    private static void ClearMultiSelectDefaults(
        string root, PenumbraPackage.Contents pack, IPluginLog? log)
    {
        const string cost = "its multi-select groups arrive with the pack's own options already ticked, so "
                          + "the mod puts pieces on the character before anyone asks for them";

        if (pack.FileVersion >= PenumbraModMeta.SingleFileVersion)
        {
            EditJson(Path.Combine(root, PenumbraModMeta.MetaFile), log, cost, manifest =>
            {
                if (manifest["Groups"] is JsonArray groups)
                    foreach (var g in groups)
                        if (g is JsonObject go) ClearGroupDefault(go);
            });
            return;
        }

        foreach (var group in pack.Groups)
            if (group.Entry != null)
                EditJson(Path.Combine(root, group.Entry.Replace('/', Path.DirectorySeparatorChar)), log,
                    cost, ClearGroupDefault);
    }

    /// <summary>Zero one group's default selection, if it is a multi-select. See
    /// <see cref="ClearMultiSelectDefaults"/> for why only that kind.</summary>
    private static void ClearGroupDefault(JsonObject group)
    {
        // Through TryGetValue, like PenumbraPackage reads it: GetValue<string> THROWS on a Type that is a
        // number or an object, and a hand-edited pack can carry one. A group whose kind cannot be read is
        // left exactly as it is, which is the safe direction — the worst case is the pack's own default.
        if (group["Type"] is not JsonValue tv
         || !tv.TryGetValue<string>(out var type)
         || !string.Equals(type, "Multi", StringComparison.OrdinalIgnoreCase))
            return;

        // Written even when the field is absent: Penumbra's own default for a missing DefaultSettings is not
        // something to rely on, and an explicit zero says what this import meant.
        group["DefaultSettings"] = 0;
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
        // Through TryGetValue: GetValue<string> THROWS on a value that is a number or an object, and a
        // hand-edited manifest can carry one. EditJson now catches that, but a throw here would still cost
        // the whole file's edit — every OTHER redirect in it included — over one malformed entry.
        var doomed = files
            .Where(p => p.Value is JsonValue v
                     && v.TryGetValue<string>(out var entry)
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
    /// A failure here is NOT harmless and must not pass in silence. But the harm differs per caller, which
    /// is why <paramref name="cost"/> is theirs to supply rather than baked in here: this helper started
    /// with one caller and its message named that caller's consequence, so once a second and third arrived
    /// a failed name-write reported a model-redirect conflict that was not happening and said nothing about
    /// what had. A wrong diagnosis in this log is worse than none — it is read to work out why a pack
    /// misbehaved, and it sends the reader at the wrong subsystem.
    /// <para/>
    /// <paramref name="cost"/> completes "…, so {cost}".
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

        // The edit itself is guarded too, and the write is skipped when it throws. A manifest can carry a
        // shape the readers do not expect — a Files value that is a number rather than a string is enough,
        // since StripFiles asks it for a string — and an exception escaping here would abandon WriteMod with
        // the archive already extracted: a mod folder Penumbra loads happily, with none of the import's
        // edits applied and no sidecar to mark it as one of ours. Leaving the file untouched is the same
        // outcome as a manifest that would not parse, which the two arms above already report.
        try { edit(root); }
        catch (Exception ex)
        {
            log?.Warning(ex, "[Proteus] content import: {0} could not be edited, so {1}", path, cost);
            return;
        }
        PenumbraModMeta.AtomicWrite(path, root.ToJsonString(ProteusJson.MetadataWrite));
    }

    /// <summary>The Proteus sidecar mirroring the pack's groups, with one piece per importable model.</summary>
    internal static ProteusMetadata BuildSidecar(ImportPreview preview, string modName, string author)
    {
        // Seeded from the pack's OWN sidecar when it has one. A BACKSTOP, not the mechanism: a pack that
        // carries one is INSTALLED rather than converted (see ImportPreview.InstallOnly), and WriteMod
        // returns before it reaches here — so nothing driven by the Import tab arrives with a non-null
        // AuthoredSidecar.
        //
        // It stays because this method and WriteMod are internal and callable on their own, and the failure
        // it prevents is silent data loss: BuildSidecar derives the CONTENT half and nothing else, so a
        // freshly-built object written over an authored file deletes the overlays, colour rows, mask
        // settings and per-material edits that are the author's.
        //
        // Name and Author are the user's to set — the import dialog asks for both — so they are assigned
        // after, over whatever the authored file said.
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
    /// How well DRESSED each candidate material is: how many distinct files back the textures it names, and
    /// how many bytes those come to.
    /// <para/>
    /// This is what tells the variant an author actually worked on from the copies sitting beside it. A pack
    /// ships its material under one folder per IMC variant, and when only one of them is real the rest are
    /// stubs pointing at a shared placeholder set. [LOONY] Light the Way ships nine, of which v0007
    /// references four files totalling 33 MB while the other eight reference two totalling 2,208 bytes —
    /// and its nine colour tables come to only two distinct values. Counting files finds that; where two
    /// candidates name the same NUMBER of textures, only the byte total separates real art from a
    /// placeholder.
    /// <para/>
    /// Sizes are free: <see cref="PenumbraPackage.Contents.Entries"/> already carries each entry's
    /// uncompressed length from the archive's central directory, so this reads the materials but never the
    /// textures.
    /// <para/>
    /// A hint, not a gate. A material that will not parse scores zero and sorts last rather than throwing —
    /// the pack still publishes something, and a wrong guess here costs a look rather than a piece.
    /// </summary>
    private static Dictionary<string, (int Files, long Bytes)> DressedRank(
        PenumbraPackage.Contents pack,
        Dictionary<string, HashSet<string>> backing,
        Dictionary<string, List<string>> candidatesByLeaf)
    {
        var rank = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        // Only leaves with a real choice to make are worth reading. One candidate needs no ranking.
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
    /// The textures this piece's materials name that the PACK itself ships, and which option supplies each.
    /// <para/>
    /// Read from the materials rather than taken from the pack wholesale, so a piece records only the
    /// textures it can actually reach. A pack ships textures for every garment in it; a bracelet has no use
    /// for the dress's normal map, and carrying all of them in every piece would bloat the sidecar and
    /// invite the composite to republish files nothing draws.
    /// <para/>
    /// Every candidate material is read, not just the best-ranked one: which of them gets published is a
    /// runtime decision, and the four prints of one leaf can name different textures from each other.
    /// <para/>
    /// Null when nothing came back — an unreadable archive or materials that will not parse leave the
    /// textures to Penumbra, which is exactly the behaviour this replaces and still works for most packs.
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

                // Only a path the pack VARIES is worth taking over. One file behind a texture is not a
                // choice, and republishing it would copy every 4K map the pack ships on every composite for
                // no decision at all — Cerise alone would move tens of megabytes to change nothing.
                //
                // The same shape as the material rule one level up: several files competing over one path is
                // the user choosing, one file is fixed.
                if (who.Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    map[tex] = who;
            }
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Whether a redirect's game path is one the game could ever ask for.
    /// <para/>
    /// A prefix test rather than another parse, because the whole trouble is that a junk path's TAIL parses
    /// perfectly: an option folder glued onto the front leaves the file name — the only part
    /// <see cref="ContentSlot.Parse"/> reads — completely intact. Every character model the game loads is
    /// under <c>chara/</c>, so what is in front of that is the question worth asking.
    /// </summary>
    private static bool IsGamePath(string gamePath)
        => PenumbraPackage.Normalize(gamePath).StartsWith("chara/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One unit as the sidecar stores it. Its race variants become a <c>Models</c> map rather than separate
    /// pieces, and their material bindings merge: one race's leaf names (<c>mt_c0101…</c> vs
    /// <c>mt_c0201…</c>) do not collide with another's, and whichever model the wearer's race selects
    /// declares only its own.
    /// <para/>
    /// That holds ACROSS races and not within one. A malformed pack can declare two models for a single race
    /// — two game paths differing only by a folder, or two groups sharing a name — and then both the model
    /// map and the bindings have two answers to one question. Everything below is built from
    /// <c>kept</c>, the first variant of each race, so the two never disagree: a piece that renders one
    /// variant's geometry against the other's material is a metal ring drawn as skin.
    /// </summary>
    private static ContentPiece PieceOf(PieceUnit unit, PenumbraPackage.Contents pack)
    {
        var piece = new ContentPiece
        {
            Surface    = ShellSurfaceKind.Body,
            Slot       = unit.Slot.Label,
            GateOption = unit.GateOption,
        };

        // First declaration of a race wins, and a second is DROPPED rather than fatal. This used to be a
        // ToDictionary, which turned a pack declaring one model twice into an import that failed outright
        // with nothing written — a far worse ending than wearing whichever copy came first, since the two are
        // the same garment. A malformed manifest is the pack's problem to have, not the user's to be stopped
        // by.
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
        // Game path → who supplies it, in declaration order. This is the half that makes a print or dye
        // group work: the pack's own selection decides, so each file has to remember the option it came from.
        var suppliers = new Dictionary<string, List<ContentMaterialSource>>(StringComparer.OrdinalIgnoreCase);
        void Candidate(string gamePath, string entry, string? group, string? option)
        {
            // Same rule as the models, and it matters MORE here. A prefixed path supplies its leaf with no
            // group at all, and a source with no group is unconditional — the fallback for a leaf whose
            // option groups are all unselected, which after an import every Multi group is. So STREGA's 80
            // junk .mtrl entries would have invented an always-on material choice out of dead redirects, and
            // SelectedMaterialFile scans backwards, so the one it settled on was the WORST-ranked of them.
            // The pack ships no default data at all; nothing should behave as though it does.
            if (!IsGamePath(gamePath)) return;

            bool isMtrl = gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase);
            // Textures are recorded as suppliers but NOT as candidates: they are never ranked, because a
            // material names the one path it wants and there is no variant folder to choose between. What
            // they are here for is the option each file came from — see ContentPiece.TextureOptions.
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

        // Everything a leaf could be published under, so the ranking below can weigh them together and the
        // materials behind them can be read in ONE pass over the archive.
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
            // Three keys, then declaration order — OrderByDescending is stable, so a full tie keeps the
            // order the paths were seen in.
            //
            // Count of competing files first: a path several files compete over is a path the user chooses
            // between, and a path with one file behind it is fixed. Then how well DRESSED the material is,
            // which is what tells one variant folder from eight copies of it — see DressedRank.
            var ranked = paths
                .OrderByDescending(gp => backing[gp].Count)
                .ThenByDescending(gp => dressed[gp].Files)
                .ThenByDescending(gp => dressed[gp].Bytes)
                .ToList();

            (piece.MaterialGamePaths ??= new(StringComparer.OrdinalIgnoreCase))[leaf] = ranked;

            // The same candidates as FILES, carrying the option that supplies each — in the same ranked
            // order, so the composite walks best path first and only then asks which of its options is on.
            (piece.MaterialOptions ??= new(StringComparer.OrdinalIgnoreCase))[leaf] =
                [.. ranked.SelectMany(gp => suppliers[gp])];
        }

        piece.TextureOptions = TextureSuppliers(pack, suppliers, piece.MaterialOptions);

        // Which of the PACK'S options reveal each material — see ContentPiece.MaterialGates. Worked out
        // here, where the model has just been read, because the panel that needs it draws every frame.
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

        // Amber is for a PROBLEM, and an import that took everything it was given does not have one.
        //
        // Pieces arriving switched off used to colour this line, on the reasoning that a character which did
        // not change looks like a failure. But that is how every import ends — it is the design, not a
        // fault — so the warning colour fired on every success and taught the user to read amber as "fine".
        // Then a genuinely amber import, one that dropped pieces it could not bind, reads the same as all
        // the others. The sentence explaining the switches is worth keeping; the colour is not.
        //
        // What is left to warn about is a piece that came out WRONG — see ImportPreview.FaultyUnits. Not the
        // skipped count: that includes the body meshes Proteus drops on purpose, so warning on it would just
        // move the cried-wolf amber from every gated pack to every outfit pack.
        var warn = prepared.Preview.FaultyUnits > 0;

        // Installed rather than converted, so none of the sentences below apply: there are no pieces to
        // count and nothing arrives switched off. See ImportPreview.InstallOnly.
        if (prepared.Preview.InstallOnly)
            return new(true, false, string.Format(Loc.Localize("ContentImport.Result.Installed.Fmt",
                "Installed \"{0}\". It was already a Proteus mod, so it went in exactly as its author "
              + "built it — enabled and opened in Penumbra, where its options are chosen."),
                dirName));

        if (prepared.Preview.PieceGroupName is { } gate)
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
