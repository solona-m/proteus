using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Dalamud.Plugin.Services;
using Proteus.Localization;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// Give a mod every size of the bodies its author ticks — the mod packer's <c>--refit-to</c>, run in game on a mod that
/// is already installed.
/// <para/>
/// For each chest and legs model of the mod, every size <see cref="SupportedBodies"/> lists for each body ticked is
/// refitted from the author's own size whose body lies nearest it (see <c>SizeSetRefit.Source.cs</c>), and the outfit's
/// other pieces in that option from the same size of theirs. The first of the author's sizes whose body can be read
/// still picks the bodies' YAS or plain sizes and a body family's size list. The sizes are saved beside the author's in
/// their size group, as the Studio's Body size tool saves one, named
/// "Rue S", "YAB Buff L" and so on. A size the mod already has — one of the author's that IS that size, or one a
/// previous run made — is left as it is.
/// <para/>
/// No UI and no IPC: everything here reads and writes files, on a worker. The caller tells Penumbra afterwards.
/// <para/>
/// Shoes and gloves are pieces too, when asked for (<see cref="Request.Ends"/>): sized by their legs or arms with the foot
/// or hand kept exactly, as the automatic refit does them (see <c>SizeSetRefit.Ends.cs</c>).
/// </summary>
internal static partial class SizeSetRefit
{
    /// <param name="ModRoot">The mod's folder.</param>
    /// <param name="Keys">The bodies ticked (<see cref="SupportedBodies.Choices"/>).</param>
    /// <param name="Yas">Refit onto a body's Yet Another Skeleton sizes where it has them. Neolithe has none in the table.
    /// A garment already weighted to YAS goes to them whatever this says.</param>
    /// <param name="Remake">Make again the sizes an earlier run made, rather than leaving them.</param>
    /// <param name="Bodies">The installed body mods.</param>
    /// <param name="ReadGameFile">Reads a game path from the game's own data, to tell the game's models a mod merely
    /// ships apart from the author's (see <see cref="PiecesOf"/>); null takes every model as the author's.</param>
    /// <param name="Ends">Make sizes of the mod's shoes and gloves too. Off, the mod's chest and legs alone, as before.</param>
    /// <param name="GameBody">The game's own body for a race (<see cref="VanillaBodyCatalog"/>), read on the framework
    /// thread before the run: a shoe or glove may have been made on it, and is searched for there last, after the
    /// installed body mods, as the automatic refit searches. Null, or a race without one, offers no game body. Chest and
    /// legs are never read off it.</param>
    internal sealed record Request(string ModRoot, IReadOnlyList<string> Keys, bool Yas, bool Remake,
                                   IReadOnlyList<BodyModIndex.Entry> Bodies, Func<string, byte[]?>? ReadGameFile = null,
                                   bool Ends = true, Func<string, BodySizeCatalog?>? GameBody = null);

    /// <param name="Made">Sizes saved.</param>
    /// <param name="Notes">Every size left out, and why — for the author to read.</param>
    internal sealed record Result(int Made, IReadOnlyList<string> Notes);

    /// <summary>
    /// A chest or legs model of some outfit for a Midlander woman or man — the races the table's bodies are, and the ones
    /// every other race of the sex falls back to. The body itself (<c>e0000</c>) is not an outfit and is never one of a
    /// mod's pieces.
    /// </summary>
    private static readonly Regex PiecePath = new(@"^chara/equipment/e(?!0000)\d{4}/model/c(0101|0201)e\d{4}_(top|dwn|sho|glv)\.mdl$",
                                                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A shoe or glove: a piece that keeps its own foot or hand and is sized by its legs or arms.</summary>
    private static bool IsEnd(string slot) => AutoRefitDecisions.LimbOf(slot) != null;

    /// <summary>One model of the mod to make sizes of.</summary>
    /// <param name="GamePath">Where it is drawn.</param>
    /// <param name="Slot">"_top" or "_dwn".</param>
    /// <param name="Group">The author's size group switching it, or null when there is none.</param>
    /// <param name="Options">The author's options giving it a model, as (option, file) in the author's order; the
    /// option is null for a model the mod's default files or another group give.</param>
    /// <param name="Label">What notes call it: "Body — e6255".</param>
    /// <param name="GroupName">The group made for its sizes when the author has none.</param>
    /// <param name="Race">"0201" or "0101": which of the table's bodies it can go to.</param>
    /// <param name="FollowsChest">
    /// A legs model whose size group also switches a chest model of the same race — an outfit sized as one. Its sizes are
    /// then the CHEST's ("Rue S", each onto the legs that size's chest goes with), saved into the same options as the
    /// chest's, so one choice in the group dresses both. Sized by the legs table instead ("Rue WC"), the chest and the
    /// legs became separate options and either one wore the other half unrefitted.
    /// </param>
    /// <param name="FollowsLegs">
    /// A shoe whose size group switches a legs model of the same race but no chest: it joins the options the legs made
    /// ("Rue WC"), so one choice dresses both. A shoe or glove in a group with a chest follows the chest instead.
    /// </param>
    /// <param name="Unpaired">
    /// A shoe or glove sized in one group with a piece of its race, of another slot, it cannot follow — gloves beside legs, or a shoe
    /// and gloves with no chest or legs. Each would add sizes of its own to the group, every one wearing the other piece
    /// unrefitted, so it gets no sizes, and a note.
    /// </param>
    internal sealed record Piece(string GamePath, string Slot, string? Group,
                                 IReadOnlyList<(string? Option, string File)> Options, string Label, string GroupName,
                                 string Race, bool FollowsChest = false, bool FollowsLegs = false, bool Unpaired = false);

    /// <summary>
    /// The chest and legs models of a mod, each with the group its sizes are switched in. A model only a refit of ours
    /// gives — under <see cref="BodyRetargetWriter.Subfolder"/> — is not one of the author's.
    /// <para/>
    /// Nor is the game's own model shipped at its own path. Mods built in TexTools carry the other races' models of an
    /// item they only changed for one — Hell for Leather, a women's outfit, gives Midlander men the game's own Anemos
    /// gambison, re-saved — and taking that for a men's piece offered TBSE sizes of a garment its author never made.
    /// </summary>
    /// <param name="readGameFile">The game's own copy of a game path; null takes every model as the author's.</param>
    /// <param name="includeEnds">Shoes and gloves too (see <see cref="Request.Ends"/>).</param>
    internal static List<Piece> PiecesOf(string modRoot, Func<string, byte[]?>? readGameFile = null, bool includeEnds = true)
    {
        var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
        var record = BodyRetargetWriter.ReadRecord(modRoot);
        var ourFiles = (record?.Options ?? []).Select(e => RefitCore.Normal(e.File)).ToHashSet(StringComparer.Ordinal);
        var single = RefitCore.SingleGroups(modRoot);
        var pieces = new List<Piece>();

        foreach (var byPath in redirects.Where(r => PiecePath.IsMatch(r.GamePath)
                                                    && (includeEnds || !IsEnd(BodySizeCatalog.SlotOf(r.GamePath)!)))
                                        .GroupBy(r => r.GamePath.ToLowerInvariant()))
        {
            var authors = byPath.Where(r => !ourFiles.Contains(RefitCore.Normal(r.File))
                                            && !RefitCore.Normal(r.File).StartsWith(
                                                   BodyRetargetWriter.Subfolder.ToLowerInvariant() + "/",
                                                   StringComparison.Ordinal))
                                .ToList();
            if (authors.Count == 0) continue;
            string gamePath = authors[0].GamePath;
            string slot = BodySizeCatalog.SlotOf(gamePath)!;
            string? group = RefitCore.SwitchingGroup(redirects, gamePath, record?.OwnGroups, single);

            var options = new List<(string? Option, string File)>();
            foreach (var r in authors)
            {
                int split = r.Source.IndexOf(" / ", StringComparison.Ordinal);
                string? option = group != null && split > 0
                                 && string.Equals(r.Source[..split], group, StringComparison.OrdinalIgnoreCase)
                    ? r.Source[(split + 3)..]
                    : null;
                // In a size group, its own options are the sizes; a model given elsewhere is not one of them.
                if (group != null && option == null) continue;
                if (options.Any(o => RefitCore.Normal(o.File) == RefitCore.Normal(r.File))) continue;
                options.Add((option, r.File));
            }
            if (options.Count == 0) continue;
            if (readGameFile != null && AllTheGamesOwn(modRoot, gamePath, options, readGameFile)) continue;
            pieces.Add(new Piece(gamePath, slot, group, options, Gui.PartsPanel.SlotOf(authors[0]),
                                 string.Format(Strings.Parts.RetargetGroupFmt, Gui.PartsPanel.ModelLabel(authors[0])),
                                 BodySizeCatalog.RaceOf(gamePath)!));
        }

        // Legs sized in the same group as a chest of their race follow the chest's sizes, and a shoe or glove follows the
        // chest — or, a shoe, the legs — it is sized with. Chests first, then legs, then shoes and gloves, so the options a
        // follower goes into are already there, named and placed by what it follows.
        bool SharesGroup(Piece p, string slot)
            => p.Group != null && pieces.Any(c => c.Slot == slot && c.Race == p.Race
                                                  && string.Equals(c.Group, p.Group, StringComparison.OrdinalIgnoreCase));
        // A piece of another slot: two shoes (or two gloves) in one group are sized alike and share their options.
        bool SharesWithAnother(Piece p)
            => p.Group != null && pieces.Any(c => c.Slot != p.Slot && c.Race == p.Race
                                                  && string.Equals(c.Group, p.Group, StringComparison.OrdinalIgnoreCase));
        return pieces.OrderBy(p => p.Slot switch { "_top" => 0, "_dwn" => 1, _ => 2 })
                     .Select(p => (p.Slot == "_dwn" || IsEnd(p.Slot)) && SharesGroup(p, "_top") ? p with { FollowsChest = true }
                                : p.Slot == "_sho" && SharesGroup(p, "_dwn") ? p with { FollowsLegs = true }
                                : IsEnd(p.Slot) && SharesWithAnother(p) ? p with { Unpaired = true }
                                : p)
                     .ToList();
    }

    /// <summary>
    /// Whether every model a mod gives <paramref name="gamePath"/> is the game's own one there: the same vertices in the
    /// same places. Not the same bytes — TexTools re-saves what it ships, 68 bytes longer — and stopping at the first
    /// model that differs, so an author's piece costs one extra read.
    /// </summary>
    private static bool AllTheGamesOwn(string modRoot, string gamePath, IReadOnlyList<(string? Option, string File)> options,
                                       Func<string, byte[]?> readGameFile)
    {
        float[]? game;
        try { game = readGameFile(gamePath) is { } bytes ? ModelPartReader.Read(bytes)?.Positions : null; }
        catch (Exception) { return false; }
        if (game == null) return false;   // a new item: nothing of the game's to be

        foreach (var (_, file) in options)
        {
            string path = BodySizeCatalog.ResolveCaseInsensitive(modRoot, file);
            if (!File.Exists(path)) return false;
            var mine = ModelPartReader.Read(File.ReadAllBytes(path))?.Positions;
            if (mine == null || mine.Length != game.Length) return false;
            for (int i = 0; i < mine.Length; i++)
                if (MathF.Abs(mine[i] - game[i]) > 1e-5f) return false;
        }
        return true;
    }

    /// <summary>Make every size. Worker thread; minutes for a mod of several pieces.</summary>
    /// <param name="progress">Told what is being made, for the panel's status line.</param>
    internal static Result Run(Request request, UVRemapService? uvRemap, IPluginLog log, Action<string> progress,
                               CancellationToken ct)
    {
        var notes = new List<string>();
        int made = 0;
        var pieces = PiecesOf(request.ModRoot, request.ReadGameFile, request.Ends);
        if (pieces.Count == 0)
        {
            notes.Add(Strings.SizeSet.NoPieces);
            return new Result(0, notes);
        }

        // What each author's file was read as this run, so a glove sized by the top beside it in its option reads that
        // top once — the reading the top's own sizes were cut from.
        var readings = new Dictionary<string, AutoRefitDecisions.Pick?>(StringComparer.Ordinal);
        // Which of the author's sizes each option of a group was cut from, by the piece that made it first — the chest,
        // pieces running chests first — so the legs, shoes and gloves joining it are cut from that same size.
        var leadSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in pieces)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                made += IsEnd(piece.Slot) ? RunEndsPiece(request, piece, uvRemap, log, progress, notes, readings, leadSources, ct)
                                          : RunPiece(request, piece, uvRemap, log, progress, notes, readings, leadSources, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                log.Error(e, "[Proteus] size set: {0} failed", piece.GamePath);
                notes.Add(string.Format(Strings.SizeSet.PieceFailedFmt, piece.Label, e.Message));
            }
        }
        return new Result(made, notes);
    }

    /// <summary>
    /// How a reading of an author's file is kept for the run: by the file, the slot and race it is read in, and the body
    /// preferred on a tie — everything <see cref="RefitCore.DetectSource"/> answers by.
    /// </summary>
    internal static string ReadingKey(string file, string slot, string race, string preferred)
        => RefitCore.Normal(file) + "|" + slot + "|" + race + "|" + preferred.ToLowerInvariant();

    /// <summary>The body mod a size set prefers on a tie, for a piece sized from <paramref name="sizeSlot"/>'s table: the
    /// first ticked key's body at the piece's race, else the first installed.</summary>
    private static string Preferred(Request request, string sizeSlot, string race, string fallback)
        => request.Keys.Select(k => SupportedBodies.Of(k, sizeSlot, false))
                       .OfType<SupportedBodies.Body>()
                       .Where(b => b.Race == race)
                       .Select(b => SupportedBodies.Find(b, request.Bodies)?.Dir)
                       .FirstOrDefault(d => d != null) ?? fallback;

    private static int RunPiece(Request request, Piece piece, UVRemapService? uvRemap, IPluginLog log,
                                Action<string> progress, List<string> notes,
                                Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                Dictionary<string, string> leadSources, CancellationToken ct)
    {
        string root = request.ModRoot;
        string race = piece.Race;
        bool male = BodySizeCatalog.IsMaleRace(race);
        // The table its sizes come from: its own slot's, or the chest's for legs that follow it.
        string sizeSlot = piece.FollowsChest ? "_top" : piece.Slot;

        // A man's piece with only women's bodies ticked, or the other way about, has nothing to go to: not read at all,
        // and nothing to say — a unisex mod asked for Rue sizes is not missing anything for its men's models.
        if (!request.Keys.Any(k => ForRace(k, sizeSlot, race))) return 0;

        // ── what it was made on: every one of the author's sizes whose body can be read ──
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0)
        {
            notes.Add(Strings.SizeSet.NoBodies);
            return 0;
        }
        string preferred = Preferred(request, sizeSlot, race, mods[0].Dir);
        void Note(string note) => log.Information("[Proteus] size set: {0}: {1}", piece.Label, note);
        // Per piece, not per run: a reading open for the whole run would keep every body it touched for minutes.
        using var reading = BodySizeMatch.BeginReading();

        var sources = new Sources(request, piece, preferred, mods, readings, Note, ct);
        if (sources.Primary is not { } primary)
        {
            notes.Add(string.Format(Strings.SizeSet.UnreadableFmt, piece.Label));
            return 0;
        }
        var sourceEntry = primary.Entry;
        log.Information("[Proteus] size set: {0} from {1} made on {2} / {3} ({4})", piece.Label, primary.Name,
                        sourceEntry.Name, primary.Pick!.Option.Label, primary.Pick.Confidence);
        BodySizeMatch.NeedNear(sources.Choices.Count + 2 * SupportedBodies.Choices.Count);

        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        BodySizeCatalog? CatalogOf(string dir)
            => request.Bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase))?.Catalog;
        // What a size cut from a candidate takes along, read once per candidate.
        BodyOption? SourceLegsOf(Candidate c)
        {
            if (c.LegsRead) return c.SourceLegs;
            c.LegsRead = true;
            // The legs its hem sits on, for a chest piece.
            if (piece.Slot != "_top" || c.Entry.Catalog.For("_dwn", race) is not { Count: > 0 } legsOptions) return null;
            var ranking = BodySizeMatch.Rank(c.Garment, legsOptions, c.Entry.Catalog.PathOf, c.Bones);
            if (ranking.Best is { } best && AutoRefitDecisions.Proceed(ranking.Confidence)) c.SourceLegs = best.Option;
            else Note($"{c.Name}: the hem's legs not read ({ranking.Confidence}), hips left as they are");
            return c.SourceLegs;
        }
        RefitCore.ChestBeneath? BeneathOf(Candidate c)
        {
            if (c.BeneathRead) return c.Beneath;
            c.BeneathRead = true;
            // The chest beneath a legs piece's waist, when it lies over the belly (see RefitCore.ChestBeneath).
            return c.Beneath = piece.Slot == "_dwn" ? RefitCore.ChestBeneathOf(c.Garment, c.Pick!, race, mods, CatalogOf, Note) : null;
        }
        // Bodies' probe points and the distances between them, read once per piece.
        var probes = new Dictionary<string, System.Numerics.Vector3[]>(StringComparer.OrdinalIgnoreCase);
        System.Numerics.Vector3[] ProbesOf(string path) => probes.TryGetValue(path, out var p) ? p : probes[path] = BodyProbes(path);
        var bodyDistances = new Dictionary<(string, string), (float P90, float Mean)>();
        SourceDistance Measure(Candidate c, string targetPath)
        {
            if (!bodyDistances.TryGetValue((c.SourcePath, targetPath), out var body))
                bodyDistances[(c.SourcePath, targetPath)] = body = BodyDistance(c.SourcePath, targetPath, ProbesOf);
            var skin = SkinDistance(c.SkinProbes, targetPath);
            return new SourceDistance(body.P90, body.Mean, skin.P90, skin.Mean, c.Order);
        }

        // ── where the sizes go ──
        using var group = new SizeGroup(root, piece);
        var authorSizes = AuthorSizes(root, piece, group.Ours);

        bool yas = request.Yas || primary.Yas;

        foreach (string key in request.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!ForRace(key, sizeSlot, race)) continue;
            var (body, target) = BodyFor(key, sizeSlot, yas, request.Bodies, sourceEntry, notes, piece.Label);
            if (body == null || target == null) continue;
            var targetCat = target.Catalog;
            bool Across(Candidate c) => !string.Equals(target.Dir, c.Pick!.Dir, StringComparison.OrdinalIgnoreCase);
            ushort? targetMask = RefitCore.MaskOf(target.Dir, targetCat, piece.Slot, null);
            ushort? targetLegsMask = RefitCore.MaskOf(target.Dir, targetCat, "_dwn", null);

            // A family's sizes are read off the primary's file name, which for legs following a chest is a legs name:
            // those take the table's chest sizes, and their legs move along the family below.
            foreach (var size in SupportedBodies.SizesFor(body, Across(primary) || piece.FollowsChest ? null : primary.Pick.Option.Rel))
            {
                ct.ThrowIfCancellationRequested();
                string option = SupportedBodies.OptionName(body, size);
                string leadKey = LeadKey(group.Name, race, option);
                // Where a candidate goes for this size. Legs following the chest go onto the legs that size's chest is
                // worn with — across body mods the table's, within one the candidate's own family's; a size with none
                // (Neolithe XS keeps the Small hips) has nowhere to go from it.
                BodyOption? ToOf(Candidate c)
                {
                    string? rel = !piece.FollowsChest ? size.Rel
                                : Across(c) ? size.LegsRel
                                : SupportedBodies.FamilyLegs(c.Pick!.Option.Rel, size.Name);
                    return rel == null ? null : OptionByFile(targetCat.For(piece.Slot), rel);
                }
                string? WhyNot(Candidate c) => Ineligible(c.Pick!.Dir, c.Pick.Option, body.Family, target.Dir, ToOf(c));
                // The nearest of the author's sizes that may go onto this one, and the log line saying why.
                (Candidate Chosen, string Why)? Nearest()
                {
                    var eligible = new List<(Candidate, SourceDistance)>();
                    var said = new List<string>();
                    var may = new List<Candidate>();
                    foreach (var c in sources.Choices)
                        if (WhyNot(c) is { } why) said.Add($"{c.Name}: {why}");
                        else may.Add(c);
                    var alike = RiggedAlike(may, c => c.Yas, body.Yas);
                    foreach (var c in may.Except(alike)) said.Add($"{c.Name}: {(c.Yas ? "rigged for YAS" : "not rigged for YAS")}");
                    foreach (var c in alike)
                    {
                        var d = Measure(c, targetCat.PathOf(ToOf(c)!));
                        eligible.Add((c, d));
                        said.Add($"{c.Name}: {Describe(d)}");
                    }
                    if (eligible.Count == 0)
                    {
                        Note($"{option}: none of the author's sizes goes onto it ({string.Join("; ", said)})");
                        return null;
                    }
                    var (chosen, step, gap) = Choose(eligible);
                    return (chosen, $"{option} from {chosen.Name} (by {step}, {gap * 1000f:F1} mm ahead; {string.Join("; ", said)})");
                }

                if (group.AlreadyThere(option, request.Remake))
                {
                    log.Information("[Proteus] size set: {0}: \"{1}\" already there", piece.Label, option);
                    // An option an earlier run cut from the size this one would choose still leads its outfit: the shoes
                    // and gloves joining it now follow that size. One cut otherwise — from the author's first size, by an
                    // earlier version — leads nothing, and they are left out until it is made again.
                    if (!piece.FollowsChest && !leadSources.ContainsKey(leadKey) && group.FromOf(option) is { } was
                        && Nearest() is { } seed && seed.Chosen.Option != null
                        && RefitCore.LabelFrom(Across(seed.Chosen) ? seed.Chosen.Entry.Name : null,
                                               [seed.Chosen.Pick!.Option.Label]) == was)
                        leadSources[leadKey] = seed.Chosen.Option;
                    continue;
                }
                // Legs following the chest only join a size the chest made. One it did not — the author already has it
                // under another name, or the pair was refused — made here would wear refitted legs under the author's
                // unrefitted chest, copied in from the option the legs were cut from.
                if (piece.FollowsChest && !group.Ours.Contains(option))
                {
                    log.Information("[Proteus] size set: {0}: no \"{1}\" of the chest's to join", piece.Label, option);
                    continue;
                }
                // A size the author ships is skipped before any source is measured for it — where it is one size for
                // every source; legs following a chest go onto the legs of the source's family.
                if (!piece.FollowsChest)
                {
                    if (OptionByFile(targetCat.For(piece.Slot), size.Rel) is not { } fixedTo)
                    {
                        notes.Add(string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name));
                        continue;
                    }
                    if (AuthorHas(authorSizes, targetCat, fixedTo, piece.Slot, race) is { } own)
                    {
                        log.Information("[Proteus] size set: {0}: {1} is the author's own \"{2}\"", piece.Label, option, own);
                        continue;
                    }
                }

                // ── which of the author's sizes it is cut from: the one the outfit's lead was, else the nearest ──
                Candidate? chosen = null;
                string? sourceOption = null;
                if (leadSources.TryGetValue(leadKey, out var leadOption))
                {
                    var c = ResolveOptionFile(redirects, piece.GamePath, group.Name, leadOption) is { } file
                        ? sources.ByFile(leadOption, file)
                        : null;
                    string? why = c == null ? "its model there could not be read" : WhyNot(c);
                    if (why == MadeOnIt)
                    {
                        // The author's own model there already fits this size, and the chest's option carries it.
                        log.Information("[Proteus] size set: {0}: {1} is the size it was made on", piece.Label, option);
                        continue;
                    }
                    if (why == null)
                    {
                        (chosen, sourceOption) = (c, leadOption);
                        Note($"{option} from \"{leadOption}\", as the outfit's lead piece in that option was");
                    }
                    else
                        Note($"{option}: not cut from \"{leadOption}\" as its lead piece was: {why}; the nearest instead");
                }
                if (chosen == null)
                {
                    if (sources.Choices.All(c => WhyNot(c) == MadeOnIt))
                    {
                        log.Information("[Proteus] size set: {0}: {1} is the size it was made on", piece.Label, option);
                        continue;
                    }
                    if (Nearest() is not { } nearest) continue;
                    (chosen, sourceOption) = (nearest.Chosen, nearest.Chosen.Option);
                    Note(nearest.Why);
                }
                var to = ToOf(chosen)!;
                if (piece.FollowsChest && AuthorHas(authorSizes, targetCat, to, piece.Slot, race) is { } theirs)
                {
                    log.Information("[Proteus] size set: {0}: {1} is the author's own \"{2}\"", piece.Label, option, theirs);
                    continue;
                }

                progress(string.Format(Strings.SizeSet.WorkingFmt, piece.Label, option));
                bool across = Across(chosen);
                var sourceCat = chosen.Entry.Catalog;
                string targetPath = targetCat.PathOf(to);
                if (BodyRetarget.BuildPair(piece.Slot, chosen.SourcePath, targetPath, SlotName(piece.Slot), male,
                                           RefitCore.MaskOf(chosen.Pick!.Dir, sourceCat, piece.Slot, null), targetMask,
                                           uvRemap, out var pair) is { } refusal)
                {
                    notes.Add(string.Format(Strings.SizeSet.RefusedFmt, piece.Label, option, refusal));
                    continue;
                }
                var pairs = new List<BodyRetarget.SlotPair> { pair };

                // The hem follows onto the new size's hips: the table's legs across body mods, the source's legs moved
                // along their own sizes within one.
                string toLabel = to.Label;
                if (SourceLegsOf(chosen) is { } sourceLegs)
                {
                    string? legsRel = across ? size.LegsRel : SupportedBodies.FamilyLegs(sourceLegs.Rel, size.Name);
                    if (legsRel != null && OptionByFile(targetCat.For("_dwn"), legsRel) is { } legsTo
                        && !AutoRefitDecisions.SameFile(chosen.Pick.Dir, sourceLegs, target.Dir, legsTo))
                    {
                        if (BodyRetarget.BuildPair("_dwn", sourceCat.PathOf(sourceLegs), targetCat.PathOf(legsTo),
                                                   SlotName("_dwn"), male, RefitCore.MaskOf(chosen.Pick.Dir, sourceCat, "_dwn", null),
                                                   targetLegsMask, uvRemap, out var legsPair) is { } legsRefusal)
                            log.Information("[Proteus] size set: {0} {1}: hips left as they are: {2}", piece.Label, option,
                                            legsRefusal);
                        else
                        {
                            pairs.Add(legsPair);
                            toLabel += " + " + legsTo.Label;
                        }
                    }
                }

                // The chest beneath goes along for its shape: the size's own chest for legs following one, else the body's
                // middle chest — its chests share their belly.
                if (across && BeneathOf(chosen) is { } beneath
                    && ChestBeneathTo(key, size, piece.FollowsChest, yas, targetCat, race) is { } chestTo
                    && RefitCore.ChestBeneathPair(beneath, target.Dir, targetCat, chestTo, male,
                                                  RefitCore.MaskOf(beneath.Dir, beneath.Catalog, "_top", null),
                                                  RefitCore.MaskOf(target.Dir, targetCat, "_top", null), uvRemap,
                                                  note => log.Information("[Proteus] size set: {0} {1}: {2}", piece.Label,
                                                                          option, note),
                                                  out var chestPair))
                {
                    pairs.Add(chestPair);
                    log.Information("[Proteus] size set: {0} {1}: the chest beneath refitted along onto {2}",
                                    piece.Label, option, chestTo.Label);
                }

                var plan = BodyRetarget.Plan(chosen.Garment, chosen.Bytes, pairs, piece.Slot, held: new HashSet<int>(),
                                             replaceSkin: true, acrossBodies: across, clearBody: false, cutHidden: true,
                                             keepShape: chosen.Shape, log: line => log.Debug("[Proteus] size set: {0}", line),
                                             warn: line => log.Warning("[Proteus] size set: {0}", line));
                plan = RefitCore.MatchSkinToBody(plan, chosen.SourcePath, targetPath, uvRemap, male, log);
                log.Information("[Proteus] size set: {0} onto {1} (moved up to {2:F1} mm)", piece.Label, option,
                                plan.Report.WorstMove * 1000f);
                if (RefitCore.ReleasedLog(plan.Report, chosen.ShapeNames) is { } released)
                    log.Information("[Proteus] size set: {0} {1}: let go of kept piece(s) that would sink moved whole: {2}",
                                    piece.Label, option, released);

                // One save per size: each names the size it was cut from. The first piece to make an option leads it.
                // Stopped while this size was made: it is not saved — Stop keeps what was saved before it.
                ct.ThrowIfCancellationRequested();
                bool lead = !leadSources.ContainsKey(leadKey);
                if (group.Save(target.Dir, RefitCore.LabelFrom(across ? chosen.Entry.Name : null, [chosen.Pick.Option.Label]),
                               [new BodyRetargetWriter.Refit(option, plan.Model, toLabel)], sourceOption, notes,
                               sourceFilesWin: request.Remake && lead)
                    && lead && sourceOption != null)
                    leadSources[leadKey] = sourceOption;
            }
        }
        group.RestoreDefault();
        var (indexed, bytes, _, _, _) = BodySizeMatch.NearCacheSize();
        Note($"nearness cache: {indexed} bodies, {bytes / 1024} KB");
        return group.Saved;
    }

    /// <summary>
    /// The target chest beneath a legs size: the size's own chest, for legs following a chest; else the body's middle
    /// chest ("M") — a body's chests share their belly — or its first. Null when the body has none here.
    /// </summary>
    internal static BodyOption? ChestBeneathTo(string key, SupportedBodies.Size size, bool followsChest, bool yas,
                                              BodySizeCatalog targetCat, string race)
    {
        string? rel = followsChest
            ? size.Rel
            : (SupportedBodies.Of(key, "_top", yas) ?? SupportedBodies.Of(key, "_top", !yas)) is { } chests
                ? (chests.Sizes.FirstOrDefault(s => s.Name == "M") ?? chests.Sizes.FirstOrDefault())?.Rel
                : null;
        return rel == null ? null : OptionByFile(targetCat.For("_top", race), rel);
    }

    /// <summary>
    /// The group a piece's sizes go into: which options are there, which are ours, which already carry this piece, and
    /// the default to put back. One for every piece, chest, legs, shoe or glove alike. Disposed, it puts the default of a
    /// group of ours back — however the piece ends, cancelled or failed partway included.
    /// </summary>
    private sealed class SizeGroup : IDisposable
    {
        private readonly string root;
        private readonly Piece piece;
        private readonly HashSet<string> existing;
        private readonly HashSet<string> ours;
        private readonly HashSet<string> doneHere;
        private readonly Dictionary<string, string> fromHere;
        private readonly bool authorGroup;
        private readonly string defaultBefore;

        internal string Name { get; }
        /// <summary>Options of ours in the group: the sizes this piece, or the outfit's other pieces, were saved into.</summary>
        internal IReadOnlySet<string> Ours => ours;
        internal int Saved { get; private set; }

        internal SizeGroup(string root, Piece piece)
        {
            this.root = root;
            this.piece = piece;
            var record = BodyRetargetWriter.ReadRecord(root);
            Name = piece.Group ?? piece.GroupName;
            existing = (PenumbraModMeta.TryReadFileOptions(root, Name) ?? []).Select(o => o.Name)
                                                                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inGroup = (record?.Options ?? []).Where(e => string.Equals(record!.GroupOf(e), Name,
                                                                           StringComparison.OrdinalIgnoreCase)).ToList();
            ours = inGroup.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Which of ours already carry a refit of THIS model. An option of ours without one is one the outfit's other
            // piece made — "Rue S" saved for the chest, still wearing the author's legs — and this piece's refit goes into it.
            doneHere = inGroup.Where(e => string.Equals(e.GamePath, piece.GamePath, StringComparison.OrdinalIgnoreCase))
                              .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            fromHere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in inGroup.Where(e => string.Equals(e.GamePath, piece.GamePath, StringComparison.OrdinalIgnoreCase)))
                fromHere[e.Name] = e.From;
            // A group of our own is rewritten whole by every save, which selects the size saved last by default: a fresh
            // install, or a pack exported from here, would then draw "Bibo L" instead of the author's model. Its default
            // is put back afterwards — what it was, or "Original" for a group this run makes. The author's group is only
            // added to, and keeps its default untouched.
            authorGroup = BodyRetargetWriter.IsAuthorGroup(root, Name);
            defaultBefore = PenumbraModMeta.SingleDefault(root, Name) ?? BodyRetargetWriter.OriginalOption;
        }

        /// <summary>
        /// Whether <paramref name="option"/> is already there: the author's option of that name is theirs; one of ours is
        /// done when it carries this model, and made again only when asked.
        /// </summary>
        internal bool AlreadyThere(string option, bool remake)
            => existing.Contains(option) && (!ours.Contains(option) || (doneHere.Contains(option) && !remake));

        /// <summary>What an option of ours that carries this piece names it was cut from ("Yet Another Body+ — Large"),
        /// or null.</summary>
        internal string? FromOf(string option) => fromHere.GetValueOrDefault(option);

        /// <summary>
        /// Save sizes — chests and legs one per size, each naming the size it was cut from. False, noted, on failure.
        /// </summary>
        /// <param name="sourceFilesWin">Made again from another of the author's options: that option's files replace
        /// the ones carried from the old (see <see cref="BodyRetargetWriter.Save"/>).</param>
        internal bool Save(string targetDir, string from, List<BodyRetargetWriter.Refit> refits, string? sourceOption,
                           List<string> notes, bool sourceFilesWin = false)
        {
            var outcome = BodyRetargetWriter.Save(root, Name, piece.GamePath, targetDir, from, refits,
                                                  piece.Group != null ? sourceOption : null,
                                                  fromGame: RefitCore.IsGameGear(root, piece.GamePath),
                                                  sourceFilesWin: sourceFilesWin);
            if (!outcome.Ok)
            {
                notes.Add(string.Format(Strings.SizeSet.SaveFailedFmt, piece.Label, outcome.Message));
                return false;
            }
            Saved += refits.Count;
            existing.UnionWith(refits.Select(r => r.Option));
            ours.UnionWith(refits.Select(r => r.Option));
            doneHere.UnionWith(refits.Select(r => r.Option));
            foreach (var refit in refits) fromHere[refit.Option] = from;
            return true;
        }

        private bool restored;

        /// <summary>
        /// Put a group of our own's default back once the run saved into it — at the end of a piece that finished, where a
        /// failure to is the piece's failure and reported as one.
        /// </summary>
        internal void RestoreDefault()
        {
            restored = true;
            if (Saved > 0 && !authorGroup) PenumbraModMeta.SetSingleDefault(root, Name, defaultBefore);
        }

        /// <summary>The same for a piece that did not finish: cancelled, or failed partway.</summary>
        public void Dispose()
        {
            if (restored) return;
            try
            {
                RestoreDefault();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not over what ended the piece — a cancel, or the failure the run reports. The sizes saved stand.
            }
        }
    }

    /// <summary>Whether a ticked body has sizes for a piece of this slot and race.</summary>
    private static bool ForRace(string key, string slot, string race)
        => SupportedBodies.Of(key, slot, false) is { } plain ? plain.Race == race
         : SupportedBodies.Of(key, slot, true) is { } yas && yas.Race == race;

    /// <summary>
    /// The body to refit onto for one ticked key: its YAS sizes when wanted and installed, else its plain ones, else
    /// whichever is installed. Null, with a note, when the slot has none in the table or the body is not installed.
    /// <para/>
    /// Neolithe's sizes are YAS-free whatever is asked (the table has none), and a garment made on a Neolithe install
    /// takes its sizes from that same install — a second copy of the pack must not be refitted onto from the first.
    /// </summary>
    /// <param name="source">The body mod the piece was made on; null for the game's own body, which is no family's.</param>
    private static (SupportedBodies.Body? Body, BodyModIndex.Entry? Mod) BodyFor(string key, string slot, bool yas,
        IReadOnlyList<BodyModIndex.Entry> installed, BodyModIndex.Entry? source, List<string> notes, string piece)
    {
        var plain = SupportedBodies.Of(key, slot, false);
        var yasBody = SupportedBodies.Of(key, slot, true);
        if (plain == null && yasBody == null) return (null, null);   // YAB Buff has no legs of its own: nothing to say

        var order = yas ? new[] { yasBody, plain } : [plain, yasBody];
        foreach (var body in order.OfType<SupportedBodies.Body>())
        {
            if (body.Family && source != null && SupportedBodies.Find(body, [source]) != null) return (body, source);
            if (SupportedBodies.Find(body, installed) is { } mod) return (body, mod);
        }

        string label = (plain ?? yasBody)!.Label;
        notes.Add(string.Format(Strings.SizeSet.NotInstalledFmt, piece, label));
        return (null, null);
    }

    /// <summary>The author's sizes of a piece with the probe points their body skin is read by, read once.</summary>
    private sealed record AuthorSize(string Option, ModelParts Model, long[] Probes, HashSet<string> Bones);

    private static List<AuthorSize> AuthorSizes(string root, Piece piece, IReadOnlySet<string> ours)
    {
        var sizes = new List<AuthorSize>();
        foreach (var (option, file) in piece.Options)
        {
            if (option == null || ours.Contains(option)) continue;
            string path = BodySizeCatalog.ResolveCaseInsensitive(root, file);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            if (ModelPartReader.Read(bytes) is not { } model) continue;
            sizes.Add(new AuthorSize(option, model, BodySizeMatch.ProbeKeys(model),
                                     new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal)));
        }
        return sizes;
    }

    /// <summary>
    /// The author's option that already IS <paramref name="target"/> — its body skin a copy of that very size — or null.
    /// The same test the automatic refit makes before refitting a piece whose author shipped the size (see
    /// <see cref="AutoRefitWatcher.AuthorSize"/>): cheap shared-point count first, then a ranking that must come out an
    /// exact copy of that size.
    /// </summary>
    private static string? AuthorHas(List<AuthorSize> sizes, BodySizeCatalog targetCat, BodyOption target, string slot,
                                     string race)
    {
        string targetPath = targetCat.PathOf(target);
        AuthorSize? best = null;
        float bestShare = 0f;
        foreach (var size in sizes)
        {
            if (size.Probes.Length == 0) continue;
            float share = BodySizeMatch.Shared(size.Probes, targetPath, out _);
            if (share > bestShare) (bestShare, best) = (share, size);
        }
        if (best == null || bestShare < BodySizeMatch.Exact) return null;
        var ranking = BodySizeMatch.Rank(best.Model, targetCat.For(slot, race), targetCat.PathOf, best.Bones);
        return ranking.Confidence == Confidence.Exact && ranking.Best is { } top
               && RefitCore.Normal(top.Option.Rel) == RefitCore.Normal(target.Rel)
            ? best.Option
            : null;
    }

    private static BodyOption? OptionByFile(IReadOnlyList<BodyOption> options, string rel)
    {
        string want = RefitCore.Normal(rel);
        return options.FirstOrDefault(o => RefitCore.Normal(o.Rel) == want);
    }

    private static string SlotName(string slot) => slot switch
    {
        "_dwn" => Strings.Parts.RetargetLegs,
        "_sho" => Strings.Parts.RetargetFeet,
        "_glv" => Strings.Parts.RetargetHands,
        _ => Strings.Parts.RetargetChest,
    };
}
