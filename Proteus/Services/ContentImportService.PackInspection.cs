using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CheapLoc;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public sealed partial class ContentImportService
{
    private sealed class PackInspection
    {
        private readonly string pmpPath;
        private readonly IPluginLog? log;
        private readonly Func<int, int, string?>? itemName;
        private readonly PenumbraPackage.Contents? contents;
        private PenumbraPackage.Contents pack = null!;
        private List<string> warnings = null!;
        private Dictionary<string, string> materialsByLeaf = null!;
        private List<(string? Group, string? Option, string GamePath, string Entry)> sources = null!;
        private Dictionary<string, byte[]> models = null!;
        private Dictionary<(string?, string?, string, string), List<PiecePlan>> byUnit = null!;
        private List<(string?, string?, string, string)> unitOrder = null!;
        private Dictionary<(string?, string?, string, string), ContentSlot.Parsed> slotOf = null!;
        private HashSet<string> singleGroups = null!;
        private HashSet<string> shadowed = null!;
        private Dictionary<(string?, string?), int> unitsPerOption = null!;
        private HashSet<string> packToggles = null!;
        private Dictionary<string, string> garmentGate = null!;
        private List<PieceUnit> units = null!;
        private bool selfDriven;
        private string? gateGroup;
        private ImportPreview result = null!;

        public PackInspection(string pmpPath, IPluginLog? log, Func<int, int, string?>? itemName, PenumbraPackage.Contents? contents)
        {
            this.pmpPath = pmpPath;
            this.log = log;
            this.itemName = itemName;
            this.contents = contents;
        }

        public ImportPreview Run()
        {
            if (!ReadPack()) return result;
            IndexMaterials();
            CollectSources();
            ReadModels();
            GroupUnits();
            DropReplacedDefaults();
            FindPackToggles();
            BuildPieces();
            return DescribeRaces();
        }

        private bool ReadPack()
        {
            pack = contents ?? PenumbraPackage.Read(pmpPath);
            warnings = new List<string>();

            // A pack that is already a Proteus mod is installed, not converted: its author already chose what
            // Penumbra publishes and what the sidecar names, and converting again can override that wrongly.
            var authored = ReadAuthoredSidecar(pack, log);
            if (authored != null)
                { result = new ImportPreview(pmpPath, pack, [], null, warnings, authored); return false; }
            return true;
        }

        private void IndexMaterials()
        {
            // Materials are matched across the whole pack, not within the option that ships the model: packs
            // routinely put shared materials in one group and meshes in another.
            materialsByLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (gamePath, entry) in pack.AllFiles)
            {
                if (!gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) continue;
                materialsByLeaf.TryAdd(Path.GetFileName(gamePath), entry);
                materialsByLeaf.TryAdd(Path.GetFileName(entry), entry);
            }
        }

        private void CollectSources()
        {
            // Where every model in the pack comes from, default-data models included.
            sources = new List<(string? Group, string? Option, string GamePath, string Entry)>();
            foreach (var (gamePath, entry) in pack.DefaultFiles)
                if (gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    sources.Add((null, null, gamePath, entry));

            foreach (var group in pack.Groups)
            {
                if (!IsSelectable(group.Type))
                {
                    // Silent for an Imc group: its options are show/hide toggles the composite honours
                    // (see ContentAttributeGroup).
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
        }

        private void ReadModels()
        {
            // Read in one pass over the archive rather than one open per file: this runs on the picking frame.
            models = PenumbraPackage.ReadEntries(pack.Path, sources.Select(x => x.Entry));
        }

        private void GroupUnits()
        {
            // Collapse into units. The key carries the option too: two options of one group deliberately
            // redirect the same path, and must not merge.
            byUnit = new Dictionary<(string?, string?, string, string), List<PiecePlan>>();
            unitOrder = new List<(string?, string?, string, string)>();
            slotOf = new Dictionary<(string?, string?, string, string), ContentSlot.Parsed>();

            // Counted rather than logged one by one: a pack that gets this wrong does so in bulk.
            int notGamePaths = 0;
            string? firstNotGamePath = null;

            foreach (var (group, option, gamePath, entry) in sources)
            {
                // A redirect the game can never ask for (option folders baked into the path) is not a piece;
                // Penumbra does not resolve it either.
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
        }

        private void DropReplacedDefaults()
        {
            // ── default-data copies a SINGLE group replaces ──────────────────────────
            //
            // An option's redirect outranks the default, so a default-data model a Single group overrides never
            // loads and must not become a piece. Single only: a Multi option can be off, so its default renders.
            singleGroups = pack.Groups
                .Where(g => string.Equals(g.Type, "Single", StringComparison.OrdinalIgnoreCase))
                .Select(g => g.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Per game path: how many Single-group variants claim it, and how many of those Proteus can place.
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

            // Replaced only when every option claiming the path imports: otherwise a refused size would wear
            // nothing. A duplicate is the better failure.
            shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        // Recorded so WriteMod strips it too, since the units' `taken` set does not cover it.
                        shadowed.Add(RedirectKey(v.GamePath, v.Entry));
                        log?.Debug("[Proteus] content import: {0} is replaced by an option — "
                                 + "dropping the default copy", v.GamePath);
                        list.RemoveAt(i);
                    }
                }
            }
            // A unit whose races were all replaced would offer a checkbox that puts nothing on.
            unitOrder.RemoveAll(k => k.Item1 == null && byUnit[k].Count == 0);
        }

        private void FindPackToggles()
        {
            // How many distinct garments each of the author's options ships. One: their option selects it and we
            // add nothing. More: the option bundles an outfit, and its pieces are gated by slot. A one-garment
            // Single-group option still gets a slot switch when the pack has other garments — see the gate below.
            unitsPerOption = unitOrder.GroupBy(k => (k.Item1, k.Item2))
                .ToDictionary(g => g.Key, g => g.Count());

            // Attributes the pack's own options switch on: a model those checkboxes already drive must not get
            // a second one.
            packToggles = pack.Groups
                .SelectMany(g => g.Options)
                .SelectMany(o => o.Attributes)
                .ToHashSet(StringComparer.Ordinal);

            string GateLabel((string?, string?, string, string) key)
                => ContentSlot.Label(slotOf[key],
                    itemName?.Invoke(slotOf[key].Category, ContentSlot.SetIdOf(key.Item4) ?? -1));

            garmentGate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in unitOrder)
                if (key.Item1 == null && !PackControls(key))
                    garmentGate.TryAdd(GarmentKey(key.Item3, key.Item4), GateLabel(key));
        }

        private void BuildPieces()
        {
            // Whether the pack offers more than one garment, so wanting one of them does not mean wanting all.
            // Every unit counts, imported or not: the question is what the pack ships, keyed the way garmentGate is.
            bool severalGarments = unitOrder
                .Select(k => GarmentKey(k.Item3, k.Item4))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1).Any();

            units = new List<PieceUnit>();
            selfDriven = false;   // at least one model the pack's own checkboxes already drive
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
                    // A Single group cannot be switched off, so in a pack with other garments this one gets a slot
                    // switch shared by the group's sizes.
                    : severalGarments && singleGroups.Contains(group) ? slot.Label
                    : null;                                             // its own option selects it

                units.Add(new PieceUnit(group, option, slot, name, gate, byUnit[key]));

                // Only when a gate was actually suppressed, for a unit that will be imported.
                if (group == null && packControls && units[^1].Import) selfDriven = true;
            }

            gateGroup = units.Any(u => u.Import && u.GateOption != null)
                ? UniqueGroupName(pack)
                : null;

            if (units.Count == 0)
                warnings.Add(Loc.Localize("ContentImport.Warn.NoModels",
                    "No option in this pack redirects a model, so there is no geometry for Proteus to append. "
                  + "Install it in Penumbra instead."));
        }

        private ImportPreview DescribeRaces()
        {
            // Which races the pack is built for, said before import: Proteus does not resize geometry between races.
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

            // Said because the absence is invisible: a self-gating pack gets no checkboxes of ours, so a piece it
            // does not gate has no switch.
            if (selfDriven)
                warnings.Add(Loc.Localize("ContentImport.Warn.SelfDriven",
                    "This pack switches its own pieces on and off, so Proteus adds no checkboxes of its own — "
                  + "use the pack's. Any piece it does not switch is worn whenever the mod is enabled."));

            return new ImportPreview(pmpPath, pack, units, gateGroup, warnings, ShadowedRedirects: shadowed);
        }

        // An unconditional model gets no switch of ours when the pack's own checkboxes toggle its attributes.
        private bool PackControls((string?, string?, string, string) key) => byUnit[key].Any(v =>
            v.MaterialAttributes is { } byMat
            && byMat.Values.Any(names => names.Any(packToggles.Contains)));

        // The gate a garment already has from its unconditional copy, keyed on slot and set so one checkbox
        // governs every size copy too. NUL-joined; compared case-insensitively as a guard, since a miss is silent.
        private static string GarmentKey(string slotLabel, string setTag) => slotLabel + '\0' + setTag;
    }
}
