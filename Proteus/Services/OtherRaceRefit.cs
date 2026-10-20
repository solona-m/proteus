using System;
using System.Collections.Generic;
using System.Linq;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// Body size for another race of the garment's sex than the one it is drawn for: a Midlander man's shirt made into a
/// Highlander man's, refitted onto the Highlander body a body mod ships, and saved where Highlander men load it.
/// <para/>
/// The game draws a race that has no model of its own with the model of the race it falls through to, bent at draw
/// time (see <see cref="RacialModelBake"/>). Picking a race here bakes that bend into the garment first — the same bake
/// the wearer's own race gets automatically — so the refit happens between that race's bodies, and the save goes to
/// that race's path with the EQDP entry that makes the game load it. Every other race keeps falling through to the
/// drawn model as before.
/// <para/>
/// Decided by the creator, not by who is logged in: a mod author makes the Highlander version while playing anyone.
/// </summary>
internal static partial class OtherRaceRefit
{
    /// <summary>Why a race the body mod has bodies of is not baked for.</summary>
    internal enum Block
    {
        None,

        /// <summary>The mod already has its own model for that race (or, above the drawn race, for one between the two):
        /// that one is the model to refit, and baking over it would replace the author's work.</summary>
        OwnModel,
    }

    /// <summary>
    /// The race picked in "Refit for", and the model it was picked for: another model opened starts from none. Never
    /// remembered past that — the next garment's races are its own question.
    /// </summary>
    internal readonly record struct Choice(string? Model, ushort Race)
    {
        public ushort For(string model) => Model == model ? Race : (ushort)0;
    }

    /// <summary>One race offered beside the drawn one.</summary>
    /// <param name="Race">Its code, as the game numbers it (301 for c0301).</param>
    /// <param name="OwnModelPath">With <see cref="Block.OwnModel"/>, the game path of the author's model.</param>
    /// <param name="Below">A race the drawn one falls through to (Midlander for a Highlander model), not one that falls
    /// through to it: the garment is bent down into it, and kept there (see <see cref="RacialModelBake.Route.Lowered"/>).</param>
    internal sealed record Candidate(ushort Race, Block Blocked, string? OwnModelPath = null, bool Below = false);

    /// <summary>
    /// The races to offer for a garment drawn at <paramref name="gamePath"/>: each of its sex that the refit-onto body
    /// mod has a body of in the garment's own slot, and that is on one fall-through line with the drawn race.
    /// <para/>
    /// Above it — a race that falls through to the drawn one — a model baked for it replaces exactly what that race
    /// draws now, and nothing else. Below it — a race the drawn one falls through to, Midlander for a Highlander-only
    /// mod — the model made for it is what that race draws, and so is every race of its sex without a model of its own.
    /// <para/>
    /// Never the other sex, never a race on another line (Elezen from Highlander), and never the game's own gear, whose
    /// racial models work differently.
    /// </summary>
    /// <param name="redirects">The garment mod's redirects, to find a model the author already made for a race.</param>
    /// <param name="ownRecord">The garment mod's refit record: a model this tool saved is not the author's.</param>
    internal static IReadOnlyList<Candidate> Offered(string gamePath, BodySizeCatalog? target,
                                                     IEnumerable<PenumbraModMeta.Redirect> redirects,
                                                     BodyRetargetWriter.Record? ownRecord, bool isVanilla)
    {
        ushort drawn = ModelSkinReader.RaceOf(gamePath);
        if (isVanilla || drawn == 0 || target == null || BodySizeCatalog.SlotOf(gamePath) is not { } slot) return [];

        var authored = redirects as IReadOnlyCollection<PenumbraModMeta.Redirect> ?? redirects.ToList();
        var result = new List<Candidate>();
        foreach (string code in RefitCore.RacesOf(target).Order(StringComparer.Ordinal))
        {
            if (!ushort.TryParse(code, out ushort race) || race % 100 != 1 || race == drawn) continue;
            if (BodySizeCatalog.IsMaleRace(code) != BodySizeCatalog.IsMaleRace($"{drawn:D4}")) continue;
            var line = RacialModelBake.SameSexLine(race).ToList();
            bool below = !line.Contains(drawn);
            if (below && !RacialModelBake.SameSexLine(drawn).Contains(race)) continue;
            if (!target.For(slot).Any(o => BodySizeCatalog.RaceOf(o.GamePath) == code)) continue;

            // Above: the race itself, then every race it falls through to before the drawn one — a model the author made
            // for any of them is what that race draws now. Below: the race's own alone. A race between keeps drawing
            // the author's model of it whatever is saved further down.
            IEnumerable<ushort> asked = below ? [race] : line.TakeWhile(r => r != drawn).Prepend(race);
            string? own = asked.Select(r => RacialModelBake.WithRace(gamePath, r))
                               .FirstOrDefault(p => BodyRetargetWriter.AuthorProvides(authored, ownRecord, p));
            result.Add(own != null ? new Candidate(race, Block.OwnModel, own, below) : new Candidate(race, Block.None, null, below));
        }
        return result;
    }

    /// <summary>
    /// The bodies a garment was made on, OF THE DRAWN RACE — what a refit for a picked race bends alongside it: the game's
    /// own body for that race when the made-for side is the game's, else the made-for mod (or the refit-onto one, when
    /// they are the same). The panel's own copy of the game's follows the race its lists are filtered by, which is the
    /// picked one by then, so it is read again here for the drawn race.
    /// </summary>
    /// <param name="gameBody">The game's own body for a race, as the "made for" lists read it
    /// (<see cref="VanillaBodyCatalog.Read"/>, drawn); the framework thread's to call.</param>
    internal static BodySizeCatalog? MadeFor(string? fromDir, BodySizeCatalog? fromCatalog, BodySizeCatalog? onto,
                                             string drawn, Func<string, BodySizeCatalog> gameBody)
        => fromDir == VanillaBodyCatalog.Key ? gameBody(drawn) : fromCatalog ?? onto;

    /// <summary>
    /// The file a mod publishes at <paramref name="gamePath"/> where a refit cut from <paramref name="cutFrom"/> ("Group /
    /// Option", or null for none) will load it: the mod's default files, or that one option, whose files the saved
    /// option carries — or null. A file of some other option would not load with the refit, and an extra-skeleton entry
    /// pointing at it would point at nothing.
    /// </summary>
    /// <remarks>Where both have it, the option's wins: Penumbra lays an option's files over the default ones.</remarks>
    internal static string? CarriedFile(IEnumerable<PenumbraModMeta.Redirect> redirects, string? cutFrom, string gamePath)
    {
        string? fallback = null;
        foreach (var r in redirects)
        {
            if (!string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (cutFrom != null && r.Source == cutFrom) return r.File;
            if (r.Source.Length == 0) fallback ??= r.File;
        }
        return fallback;
    }

    /// <summary>
    /// The option a refit of the model at <paramref name="modelRel"/> saved into <paramref name="group"/> is cut from, as
    /// the mod spells it ("Group / Option") — the one whose files the save carries — or null when that group publishes no
    /// such option. The group as typed may differ in case from the mod's; the mod's spelling is what every later
    /// comparison is made against.
    /// </summary>
    /// <remarks>The writer's own rule (<see cref="BodyRetargetWriter.SourceOfFile"/>), so the option the bake reads is
    /// the one the save carries.</remarks>
    internal static string? CutFrom(IEnumerable<PenumbraModMeta.Redirect> redirects, string modelRel, string? group)
        => group == null ? null : BodyRetargetWriter.SourceOfFile(redirects, modelRel, group);

    /// <summary>
    /// What the option a refit is cut from gives a bake for a picked race to read, as text: the extra skeletons it ships
    /// and the extra-skeleton entries it sets for this item — "" for an option that gives neither, as most do. Two
    /// destinations with the same answer make the same model, so a change of where to save only makes it again when this
    /// changes.
    /// </summary>
    /// <param name="optionEst">The option's own Est entries (<see cref="EstEntriesFor"/> with no default data).</param>
    internal static string EstFingerprint(IEnumerable<PenumbraModMeta.Redirect> redirects, string? cutFrom,
                                          IEnumerable<EstTable.ModEntry> optionEst, string slot, int set)
    {
        if (cutFrom == null) return "";
        var files = redirects.Where(r => r.Source == cutFrom && ExtraSkeletonPath().IsMatch(r.GamePath))
                             .Select(r => r.GamePath.ToLowerInvariant() + "=" + r.File)
                             .Order(StringComparer.Ordinal);
        string? estSlot = EstTable.EstSlot(slot);
        var entries = optionEst.Where(e => e.Set == set && string.Equals(e.Slot, estSlot, StringComparison.OrdinalIgnoreCase))
                               .Select(e => $"{e.Race}:{e.Entry}").Order(StringComparer.Ordinal);
        string print = string.Join(";", files) + "|" + string.Join(";", entries);
        return print == "|" ? "" : print;
    }

    /// <summary>
    /// Whether a save of a model made for a picked race (<paramref name="picked"/>) must be refused: the option it goes
    /// into now gives the bake something other than what it was made with — it is being made again.
    /// </summary>
    internal static bool RefusesSave(bool picked, string bakedFingerprint, string currentFingerprint)
        => picked && !string.Equals(bakedFingerprint, currentFingerprint, StringComparison.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"^chara/human/c\d{4}/skeleton/(top|met)/",
                                                   System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ExtraSkeletonPath();

    /// <summary>
    /// The Est entries that apply with a refit cut from <paramref name="cutFrom"/>: that option's first — Penumbra lays an
    /// option over the default data, so where both set one, the option's is the one the game reads, and
    /// <see cref="PlanEst"/> takes the first — then the default data's.
    /// </summary>
    /// <param name="groups">The mod's groups, as <see cref="PenumbraModMeta.TryReadGroups"/> reads them.</param>
    /// <param name="defaultManipulations">The default data's manipulations.</param>
    internal static List<EstTable.ModEntry> EstEntriesFor(IEnumerable<(string Name, System.Text.Json.JsonElement Group)>? groups,
                                                          IEnumerable<object>? defaultManipulations, string? cutFrom)
    {
        var found = new List<EstTable.ModEntry>();
        if (cutFrom != null)
            foreach (var (group, json) in groups ?? [])
                foreach (var option in PenumbraModMeta.FileOptionsOf(json) ?? [])
                    if ($"{group} / {option.Name}" == cutFrom)
                        found.AddRange(EstTable.FromManipulations(option.Manipulations));
        found.AddRange(EstTable.FromManipulations(defaultManipulations));
        return found;
    }

    /// <summary>
    /// A body mod's bodies of exactly <paramref name="race"/> and nothing else: what a refit for a picked race goes onto.
    /// Its lists never fall back to another race's bodies, so a part the mod has none of the race for is not offered.
    /// </summary>
    internal static BodySizeCatalog OnlyRace(BodySizeCatalog catalog, ushort race)
    {
        string code = $"{race:D4}";
        return catalog with { Options = catalog.Options.Where(o => BodySizeCatalog.RaceOf(o.GamePath) == code).ToList() };
    }

    /// <summary>
    /// The route the garment takes: the one the wearer's race decides (<paramref name="automatic"/>) until a race is
    /// picked, a bake up into the picked race — or down into it, for a race below — once one is; or none, refitted as
    /// drawn, when the drawn race itself is picked. A pick no longer offered (another body mod chosen since) or blocked
    /// falls back to the automatic route.
    /// <para/>
    /// The drawn race picked keeps a down route the character's race decides: that is how the model is refitted for her
    /// "as it is" when the body mod has no bodies of her race, and refitting it as drawn would find none to refit onto.
    /// </summary>
    /// <param name="chosen">The picked race, or 0 for none.</param>
    internal static RacialModelBake.Route Resolve(RacialModelBake.Route automatic, ushort chosen, ushort drawn,
                                                  IReadOnlyList<Candidate> offered)
    {
        if (chosen == 0) return automatic;
        if (chosen == drawn) return automatic.Down ? automatic : RacialModelBake.Route.None;
        return offered.FirstOrDefault(c => c.Race == chosen) is { Blocked: Block.None } c
            ? new RacialModelBake.Route(drawn, chosen, Down: false, Lowered: c.Below)
            : automatic;
    }

    /// <summary>
    /// Whether the route <see cref="Resolve"/> gives is the picked race's own bake — made with a wearer read from files,
    /// against bent bodies — rather than the automatic route, even where the two reach the same race.
    /// </summary>
    internal static bool InForce(ushort chosen, ushort drawn, IReadOnlyList<Candidate> offered)
        => chosen != 0 && chosen != drawn && offered.Any(c => c.Race == chosen && c.Blocked == Block.None);

    /// <summary>The picked race is one the mod has its own model of: nothing may be refitted or saved under the pick.</summary>
    internal static bool Blocked(ushort chosen, IReadOnlyList<Candidate> offered)
        => chosen != 0 && offered.Any(c => c.Race == chosen && c.Blocked != Block.None);

    /// <summary>One entry of the "Refit for" list.</summary>
    /// <param name="Race">What picking it chooses; 0 for the automatic route.</param>
    /// <param name="Kind">How it is labelled.</param>
    internal sealed record Entry(ushort Race, EntryKind Kind, Candidate? Candidate = null);

    internal enum EntryKind
    {
        /// <summary>The drawn race: the garment refitted as it is drawn.</summary>
        ThisModel,

        /// <summary>The automatic route, where it goes somewhere none of the others do — a man's garment baked for the
        /// woman playing.</summary>
        AsWorn,

        /// <summary>Another race of the garment's sex.</summary>
        Other,
    }

    /// <summary>
    /// The "Refit for" list: the drawn race, then each offered race. When the automatic route goes somewhere neither
    /// of those is — across the sexes, for the character playing — it leads the list, standing for "as before".
    /// </summary>
    internal static IReadOnlyList<Entry> PickerEntries(RacialModelBake.Route automatic, ushort drawn,
                                                       IReadOnlyList<Candidate> offered)
    {
        var entries = new List<Entry>();
        if (automatic.Any && automatic.To != drawn && offered.All(c => c.Race != automatic.To))
            entries.Add(new Entry(0, EntryKind.AsWorn));
        entries.Add(new Entry(drawn, EntryKind.ThisModel));
        entries.AddRange(offered.Select(c => new Entry(c.Race, EntryKind.Other, c)));
        return entries;
    }

    /// <summary>
    /// Which entry of <paramref name="entries"/> stands for the route now in force: the picked race's, or with nothing
    /// picked, the one the automatic route equals.
    /// </summary>
    internal static Entry Current(IReadOnlyList<Entry> entries, ushort chosen, RacialModelBake.Route automatic,
                                  ushort drawn)
    {
        if (chosen != 0 && entries.FirstOrDefault(e => e.Race == chosen && e.Kind != EntryKind.AsWorn) is { } picked)
            return picked;
        if (entries.FirstOrDefault(e => e.Kind == EntryKind.AsWorn) is { } asWorn) return asWorn;
        ushort autoRace = automatic.Any && !automatic.Down ? automatic.To : drawn;
        return entries.FirstOrDefault(e => e.Race == autoRace) ?? entries[0];
    }

    /// <summary>
    /// A saved option's name with the race it is for, so a Highlander refit never shares a name with the Midlander
    /// one beside it — unless the size label already names the race (TBSE-X's "M Highlander").
    /// </summary>
    internal static string OptionName(string name, ushort race)
    {
        string code = $"{race:D4}";
        if (ModelRace.Index(code) is not { } n) return name;
        string english = ModelRace.Names[(n - 1) / 2];
        string local = Localization.Strings.Common.RaceNames[(n - 1) / 2];
        if (name.Contains(english, StringComparison.OrdinalIgnoreCase)
            || (local.Length > 0 && name.Contains(local, StringComparison.OrdinalIgnoreCase)))
            return name;
        return $"{name} ({ModelRace.Describe(code)})";
    }

    /// <summary>What the panel says about the live preview of a refit for a picked race.</summary>
    internal enum PreviewNote
    {
        /// <summary>The character draws the model and is the race picked: the preview is what that race will see.</summary>
        None,

        /// <summary>The character draws the model as another race: the game bends the preview onto them, not onto the
        /// race picked.</summary>
        OtherRace,

        /// <summary>The character does not draw this model at all, so the preview never shows.</summary>
        NotDrawn,
    }

    /// <summary>
    /// What to say about the preview: it goes onto the drawn model's path, bent back into the drawn race's shape, and the
    /// game bends it forward onto whoever draws that path — so it is only the picked race's view when the character
    /// draws the model AND is that race.
    /// </summary>
    /// <param name="drawsModel">Whether the character's live models include the drawn path.</param>
    /// <param name="characterRace">The live character's race, 0 when there is none.</param>
    internal static PreviewNote NoteFor(bool drawsModel, ushort characterRace, ushort chosen)
        => !drawsModel || characterRace == 0 ? PreviewNote.NotDrawn
         : characterRace != chosen ? PreviewNote.OtherRace
         : PreviewNote.None;

    /// <summary>
    /// The bones a garment leans on that a bake from <paramref name="from"/> to <paramref name="to"/> could not move:
    /// neither the deformer nor any ancestor the skeleton gives the bone lists them, so their vertices would stay where
    /// the drawn race has them while the rest of the garment moved — torn off it. A bake with any is refused.
    /// <para/>
    /// Either way along the line: bent down (Highlander to Midlander), the bones are the ones the deformer chain from
    /// <paramref name="to"/> up to <paramref name="from"/> lists, which the bend inverts.
    /// </summary>
    internal static List<string> Unbendable(SkinnedMesh skin, PbdFile pbd, Func<string, string?> parentOf, ushort from,
                                            ushort to)
    {
        var chain = pbd.Chain(from, to);
        if (chain.Count == 0) chain = pbd.Chain(to, from);
        if (chain.Count == 0) return [];   // no bend at all: refused on its own, with its own reason
        return Weighted(skin).Where(b => !PbdFile.Lists(chain, b, parentOf)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The bones any vertex actually leans on.</summary>
    internal static IEnumerable<string> Weighted(SkinnedMesh skin)
    {
        var used = new HashSet<int>();
        for (int i = 0; i < skin.BoneWeights.Length; i++)
            if (skin.BoneWeights[i] > 0f) used.Add(skin.BoneIndices[i]);
        return used.Where(i => i < skin.BoneNames.Length).Select(i => skin.BoneNames[i]).Distinct(StringComparer.Ordinal);
    }
}
