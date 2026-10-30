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
    /// <param name="Piercings">Bring each new body's piercings in with its skin (see <see cref="BodyRetarget.Rebuild"/>):
    /// <see cref="Configuration.SizeSetPiercings"/>. Off by default; nails come in either way.</param>
    /// <param name="Across">What a women's piece's men's versions read from the player's collection (see
    /// <c>SizeSetRefit.CrossSex.cs</c>), asked for on the framework thread; null makes none, whatever is ticked.</param>
    /// <param name="MadeOn">The body mod's folder the author says the chest and legs were made on, for the sizes the
    /// automatic reading cannot place (see <c>SizeSetRefit.Source.cs</c>); null reads every size automatically. A shoe's
    /// or glove's own sizes never read it; a skinless women's one made a men's is read there first, when its cloth sits
    /// on it (see <c>SizeSetRefit.CrossSexEnds.cs</c>).</param>
    internal sealed record Request(string ModRoot, IReadOnlyList<string> Keys, bool Yas, bool Remake,
                                   IReadOnlyList<BodyModIndex.Entry> Bodies, Func<string, byte[]?>? ReadGameFile = null,
                                   bool Ends = true, Func<string, BodySizeCatalog?>? GameBody = null,
                                   bool Piercings = false, AcrossInputs? Across = null, string? MadeOn = null);

    /// <param name="ResolveCollection">The file the player's collection loads for a game path (a skeleton): a path on
    /// disk when a mod replaces it, anything else for the game's own (see <see cref="OtherRaceWearer.Gather"/>).</param>
    internal sealed record AcrossInputs(Func<string, string?> ResolveCollection);

    /// <param name="Made">Sizes saved.</param>
    /// <param name="Notes">Every size left out, and why — for the author to read.</param>
    /// <param name="Warnings">Sizes saved that want a look in game: a men's version's folds and moved bones.</param>
    /// <param name="Summary">What the result line says (<see cref="ResultLine"/>).</param>
    internal sealed record Result(int Made, IReadOnlyList<string> Notes, IReadOnlyList<string>? Warnings = null,
                                  Summary Summary = Summary.Added);

    /// <summary>What came of a run, as its result line says it.</summary>
    internal enum Summary
    {
        /// <summary>Sizes were added.</summary>
        Added,
        /// <summary>None, and none was wanted: every size is already in the mod, or nothing was there to make.</summary>
        HadEverySize,
        /// <summary>None, and "Left out" says why.</summary>
        SeeLeftOut,
        /// <summary>None, and only the log says why.</summary>
        SeeLog,
    }

    /// <summary>The outcomes that leave nothing to do: the size is in the mod already, or there is none to make.</summary>
    private static readonly HashSet<string> NothingToDo = new(StringComparer.Ordinal)
    {
        Codes.AlreadyThere, Codes.AuthorsOwn, Codes.MadeOnIt, Codes.NoOwnLegs, Codes.NoBodyForRace,
        Codes.SameSkin, Codes.NoEnd,
    };

    /// <summary>
    /// What a run came to. Nothing made is "the mod already has every size" only when every outcome left nothing to do —
    /// never because nothing was noted: a size with no source is an outcome with no note. Legs with no chest option to
    /// join count as nothing to do when every chest outcome for that option is the author's own or already there.
    /// </summary>
    internal static Summary Summarize(RunLog log, int made)
    {
        if (made > 0) return Summary.Added;
        bool Nothing(SizeOutcome o)
        {
            if (o.ReasonCode is { } code && NothingToDo.Contains(code)) return true;
            if (o.ReasonCode != Codes.NoChestOption) return false;
            var chests = log.Sizes.Where(c => c.Slot == "_top" && string.Equals(c.Group, o.Group, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(c.Option, o.Option, StringComparison.OrdinalIgnoreCase)).ToList();
            return chests.Count > 0 && chests.All(c => c.ReasonCode is Codes.AuthorsOwn or Codes.AlreadyThere);
        }
        if (log.Sizes.Count > 0 && log.Sizes.All(Nothing)) return Summary.HadEverySize;
        return log.Notes.Count > 0 ? Summary.SeeLeftOut : Summary.SeeLog;
    }

    /// <summary>The panel's result line for a run.</summary>
    internal static string ResultLine(Result result) => result.Summary switch
    {
        Summary.Added => string.Format(Strings.SizeSet.DoneFmt, result.Made),
        Summary.HadEverySize => Strings.SizeSet.NothingAdded,
        Summary.SeeLeftOut => Strings.SizeSet.NothingAddedSeeLeftOut,
        _ => Strings.SizeSet.NothingAddedSeeLog,
    };

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
    /// <para/>
    /// Nor a copy of it, beside the author's design for the other sex: the copies TexTools packed before Dawntrail are of
    /// the game's older models, and re-saved with more vertices, so they are not the game's own vertex for vertex — BOObies
    /// gave Midlander men the game's halter that way, and its women's design was never made a men's (see
    /// <see cref="WithoutRacialCopies"/>).
    /// </summary>
    /// <param name="readGameFile">The game's own copy of a game path; null takes every model as the author's.</param>
    /// <param name="includeEnds">Shoes and gloves too (see <see cref="Request.Ends"/>).</param>
    /// <param name="droppedCopy">Told each game path left out as the game's own model or a copy of it, for the log.</param>
    internal static List<Piece> PiecesOf(string modRoot, Func<string, byte[]?>? readGameFile = null, bool includeEnds = true,
                                         Action<string>? droppedCopy = null)
    {
        var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
        var record = BodyRetargetWriter.ReadRecord(modRoot);
        var ourFiles = (record?.Options ?? []).Select(e => RefitCore.Normal(e.File)).ToHashSet(StringComparer.Ordinal);
        var single = RefitCore.SingleGroups(modRoot);
        var pieces = new List<Piece>();
        var models = readGameFile != null ? new Models(modRoot, readGameFile) : null;

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
            if (models != null && AllTheGamesOwn(gamePath, options, models))
            {
                droppedCopy?.Invoke(gamePath);
                continue;
            }
            pieces.Add(new Piece(gamePath, slot, group, options, Gui.PartsPanel.SlotOf(authors[0]),
                                 string.Format(Strings.Parts.RetargetGroupFmt, Gui.PartsPanel.ModelLabel(authors[0])),
                                 BodySizeCatalog.RaceOf(gamePath)!));
        }
        if (models != null) pieces = WithoutRacialCopies(pieces, redirects, ourFiles, models, droppedCopy);

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
    /// model that differs, so an author's piece costs one extra read. A model that cannot be read is not the game's.
    /// </summary>
    private static bool AllTheGamesOwn(string gamePath, IReadOnlyList<(string? Option, string File)> options, Models models)
    {
        var game = models.Games(gamePath);
        if (game == null) return false;   // a new item: nothing of the game's to be

        foreach (var (_, file) in options)
        {
            var mine = models.Mine(file);
            if (mine == null || mine.Length != game.Length) return false;
            for (int i = 0; i < mine.Length; i++)
                if (MathF.Abs(mine[i] - game[i]) > 1e-5f) return false;
        }
        return true;
    }

    /// <summary>The same item and slot for any race: the item, the race and the slot.</summary>
    private static readonly Regex RacialPath = new(@"^chara/equipment/e(\d{4})/model/c(\d{4})e\1_(top|dwn|sho|glv)\.mdl$",
                                                   RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="pieces"/> without the copies TexTools packed of the game's own model. A Midlander piece P goes when:
    /// <list type="number">
    /// <item>every model its options give is a copy of the game's model at P (<see cref="GameModelCopy.IsCopy"/>: 95% of
    /// the vertices, both ways);</item>
    /// <item>the other sex's model of the item is a piece too, and the author's own (<see cref="GameModelCopy.IsAuthors"/>,
    /// or no game model there). One that cannot be read counts as the author's, on purpose: P must still be a copy, beside
    /// a racial copy, and one model;</item>
    /// <item>the mod gives the item and slot to another race too, from a file of the author's, and one of those is a copy
    /// of the game's model for its race — the racial copies TexTools packs for every race the author did not change;</item>
    /// <item>P's options all give one model: a copy is one, an author's fits come one per body.</item>
    /// </list>
    /// Judged against the pieces as given, so the order does not matter. A men's fit edited from the game's model is kept:
    /// such a mod ships no racial copies (Twill Trousers), or its other races are the author's own fits (a body mod's
    /// hands, 58.6% the game's).
    /// </summary>
    private static List<Piece> WithoutRacialCopies(List<Piece> pieces, List<PenumbraModMeta.Redirect> redirects,
                                                   HashSet<string> ourFiles, Models models, Action<string>? droppedCopy)
    {
        bool Ours(string file)
            => ourFiles.Contains(RefitCore.Normal(file))
               || RefitCore.Normal(file).StartsWith(BodyRetargetWriter.Subfolder.ToLowerInvariant() + "/", StringComparison.Ordinal);

        bool IsRacialCopy(Piece piece)
        {
            string other = RacialModelBake.WithRace(piece.GamePath, piece.Race == "0101" ? CrossSexRefit.Women : CrossSexRefit.Men);
            var sibling = pieces.FirstOrDefault(s => string.Equals(s.GamePath, other, StringComparison.OrdinalIgnoreCase));
            if (sibling == null) return false;

            // One model: the options' files are one, or the same bytes.
            var files = piece.Options.Select(o => o.File).DistinctBy(RefitCore.Normal).ToList();
            if (files.Count > 1 && files.Skip(1).Any(f => !models.SameBytes(files[0], f))) return false;

            if (models.Games(piece.GamePath) is not { } game) return false;
            if (files.Any(f => models.Mine(f) is not { } mine || !GameModelCopy.IsCopy(mine, game))) return false;

            var p = RacialPath.Match(piece.GamePath);
            if (!p.Success) return false;
            bool racialCopy = redirects.Any(r => RacialPath.Match(r.GamePath) is { Success: true } m
                                                 && m.Groups[1].Value == p.Groups[1].Value
                                                 && string.Equals(m.Groups[3].Value, p.Groups[3].Value, StringComparison.OrdinalIgnoreCase)
                                                 && m.Groups[2].Value is not ("0101" or "0201")
                                                 && !Ours(r.File)
                                                 && models.Games(r.GamePath.ToLowerInvariant()) is { } theirs
                                                 && models.Mine(r.File) is { } copy
                                                 && GameModelCopy.IsCopy(copy, theirs));
            if (!racialCopy) return false;

            var theirGame = models.Games(sibling.GamePath);
            return theirGame == null
                   || sibling.Options.Any(o => models.Mine(o.File) is not { } mine || GameModelCopy.IsAuthors(mine, theirGame));
        }

        var copies = pieces.Where(IsRacialCopy).ToList();
        foreach (var copy in copies) droppedCopy?.Invoke(copy.GamePath);
        return copies.Count == 0 ? pieces : pieces.Where(p => !copies.Contains(p)).ToList();
    }

    /// <summary>
    /// The models <see cref="PiecesOf"/> compares, each read once: the mod's by file, the game's by game path. Any model
    /// that cannot be read — missing, malformed, or its read throwing — is null.
    /// </summary>
    private sealed class Models(string modRoot, Func<string, byte[]?> readGameFile)
    {
        private readonly Dictionary<string, float[]?> mods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float[]?> games = new(StringComparer.Ordinal);

        internal float[]? Mine(string file)
        {
            string key = RefitCore.Normal(file);
            if (!mods.TryGetValue(key, out var positions))
                mods[key] = positions = Positions(() => Bytes(file));
            return positions;
        }

        internal float[]? Games(string gamePath)
        {
            string key = gamePath.ToLowerInvariant();
            if (!games.TryGetValue(key, out var positions))
                games[key] = positions = Positions(() => readGameFile(gamePath));
            return positions;
        }

        /// <summary>Whether two of the mod's files hold the same bytes; unreadable is not.</summary>
        internal bool SameBytes(string a, string b)
            => Length(a) is { } la && la == Length(b) && Bytes(a) is { } x && Bytes(b) is { } y && x.AsSpan().SequenceEqual(y);

        private long? Length(string file)
        {
            try
            {
                var info = new FileInfo(BodySizeCatalog.ResolveCaseInsensitive(modRoot, file));
                return info.Exists ? info.Length : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private byte[]? Bytes(string file)
        {
            try
            {
                string path = BodySizeCatalog.ResolveCaseInsensitive(modRoot, file);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static float[]? Positions(Func<byte[]?> read)
        {
            try
            {
                return read() is { } bytes ? ModelPartReader.Read(bytes)?.Positions : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Make every size. Worker thread; minutes for a mod of several pieces.</summary>
    /// <param name="progress">Told what is being made, for the panel's status line.</param>
    internal static Result Run(Request request, UVRemapService? uvRemap, IPluginLog log, Action<string> progress,
                               CancellationToken ct)
    {
        var runLog = new RunLog();
        int made = Run(request, runLog, uvRemap, log, progress, ct);
        return new Result(made, runLog.Notes.Distinct(StringComparer.Ordinal).ToList(),
                          runLog.Warnings.Distinct(StringComparer.Ordinal).ToList(), Summarize(runLog, made));
    }

    /// <inheritdoc cref="Run(Request, UVRemapService?, IPluginLog, Action{string}, CancellationToken)"/>
    /// <param name="runLog">Filled as the run goes: what it says, and every size's outcome — kept when it is stopped.</param>
    /// <returns>Models saved, as <see cref="Result.Made"/> counts them.</returns>
    internal static int Run(Request request, RunLog runLog, UVRemapService? uvRemap, IPluginLog log, Action<string> progress,
                            CancellationToken ct)
    {
        var notes = runLog.Notes;
        int made = 0;
        var pieces = PiecesOf(request.ModRoot, request.ReadGameFile, request.Ends,
                              path => log.Debug("[Proteus] size set: {0} is the game's own model or a copy of it, not a piece", path));
        if (pieces.Count == 0)
        {
            NoPieces(runLog);
            return 0;
        }

        // What each author's file was read as this run, so a glove sized by the top beside it in its option reads that
        // top once — the reading the top's own sizes were cut from.
        var readings = new Dictionary<string, AutoRefitDecisions.Pick?>(StringComparer.Ordinal);
        // And what the ones it could not place were read as on the "Made on" body.
        var madeOnReadings = new Dictionary<string, SourceReading>(StringComparer.Ordinal);
        // Which of the author's sizes each option of a group was cut from, by the piece that made it first — the chest,
        // pieces running chests first — so the legs, shoes and gloves joining it are cut from that same size.
        var leadSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in pieces)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                made += IsEnd(piece.Slot) ? RunEndsPiece(request, piece, uvRemap, log, progress, runLog, readings, leadSources, ct)
                                          : RunPiece(request, piece, uvRemap, log, progress, runLog, readings, madeOnReadings,
                                                     leadSources, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                log.Error(e, "[Proteus] size set: {0} failed", piece.GamePath);
                runLog.Note(string.Format(Strings.SizeSet.PieceFailedFmt, piece.Label, e.Message), piece, SizeStatus.Failed,
                            Codes.PieceFailed, piece.Group ?? piece.GroupName);
            }
        }
        // The women's chest and legs pieces' men's versions, after every piece's own sizes.
        made += RunAcross(request, pieces, uvRemap, log, progress, runLog, ct);
        return made;
    }

    /// <summary>
    /// The game's own body for each race of the ticked bodies, which a shoe or glove may have been made on (see
    /// <see cref="Request.GameBody"/>); none when shoes and gloves are left out. Reads the game's data: the panel calls it
    /// on the framework thread.
    /// </summary>
    internal static Dictionary<string, BodySizeCatalog> GameBodies(IEnumerable<string> keys, Func<string, byte[]?> readGameFile,
                                                                  bool ends)
    {
        var gameBodies = new Dictionary<string, BodySizeCatalog>(StringComparer.Ordinal);
        if (ends)
        {
            foreach (string race in keys.Select(k => SupportedBodies.Of(k, "_top", false)?.Race).OfType<string>().Distinct())
                gameBodies[race] = VanillaBodyCatalog.Read(race, readGameFile);
            // A women's shoe or glove made a men's may have been made on the game's women's body.
            if (MensKeys(keys.ToList()).Count > 0 && !gameBodies.ContainsKey("0201"))
                gameBodies["0201"] = VanillaBodyCatalog.Read("0201", readGameFile);
        }
        return gameBodies;
    }

    /// <summary>
    /// How a reading of an author's file is kept for the run: by the file, the slot and race it is read in, and the body
    /// preferred on a tie — everything <see cref="RefitCore.DetectSource"/> answers by — and, for a reading on the body
    /// chosen under "Made on", that body.
    /// </summary>
    internal static string ReadingKey(string file, string slot, string race, string preferred, string? madeOn = null)
        => RefitCore.Normal(file) + "|" + slot + "|" + race + "|" + preferred.ToLowerInvariant()
           + (madeOn != null ? "|made on " + madeOn.ToLowerInvariant() : "");

    /// <summary>The body mod a size set prefers on a tie, for a piece sized from <paramref name="sizeSlot"/>'s table: the
    /// first ticked key's body at the piece's race, else the first installed.</summary>
    private static string Preferred(Request request, string sizeSlot, string race, string fallback)
        => request.Keys.Select(k => SupportedBodies.Of(k, sizeSlot, false))
                       .OfType<SupportedBodies.Body>()
                       .Where(b => b.Race == race)
                       .Select(b => SupportedBodies.Find(b, request.Bodies)?.Dir)
                       .FirstOrDefault(d => d != null) ?? fallback;

    private static int RunPiece(Request request, Piece piece, UVRemapService? uvRemap, IPluginLog log,
                                Action<string> progress, RunLog runLog,
                                Dictionary<string, AutoRefitDecisions.Pick?> readings,
                                Dictionary<string, SourceReading> madeOnReadings,
                                Dictionary<string, string> leadSources, CancellationToken ct)
    {
        var notes = runLog.Notes;
        string root = request.ModRoot;
        string race = piece.Race;
        bool male = BodySizeCatalog.IsMaleRace(race);
        // The table its sizes come from: its own slot's, or the chest's for legs that follow it.
        string sizeSlot = piece.FollowsChest ? "_top" : piece.Slot;

        // A man's piece with only women's bodies ticked, or the other way about, has nothing to go to: not read at all,
        // and nothing to say — a unisex mod asked for Rue sizes is not missing anything for its men's models. A women's
        // piece's men's versions are ticked apart ("tbse@women") and made by RunAcross.
        if (!request.Keys.Any(k => ForRace(k, sizeSlot, race)))
        {
            runLog.Add(piece, SizeStatus.Skipped, Codes.NoBodyForRace, "none of the bodies asked for is of its race");
            return 0;
        }

        // ── what it was made on: every one of the author's sizes whose body can be read ──
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0)
        {
            runLog.Note(Strings.SizeSet.NoBodies, piece, SizeStatus.Skipped, Codes.NoBodies);
            return 0;
        }
        string preferred = Preferred(request, sizeSlot, race, mods[0].Dir);
        void Note(string note) => log.Information("[Proteus] size set: {0}: {1}", piece.Label, note);
        // Per piece, not per run: a reading open for the whole run would keep every body it touched for minutes.
        using var reading = BodySizeMatch.BeginReading();

        var sources = new Sources(request, piece, preferred, mods, readings, madeOnReadings, Note, ct);
        if (sources.Primary is not { } primary)
        {
            var (unread, code) = UnreadableNote(piece.Label, request.MadeOn, sources.MadeOnName, sources.MadeOnMissing,
                                                sources.Fits);
            runLog.Note(unread, piece, SizeStatus.Skipped, code, piece.Group ?? piece.GroupName);
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
        System.Numerics.Vector3[] ProbesOf(string path) => probes.TryGetValue(path, out var p) ? p : probes[path] = BodyProbes(path, Note);
        var bodyDistances = new Dictionary<(string, string), (float P90, float Mean)>();
        SourceDistance Measure(Candidate c, string targetPath)
        {
            if (!bodyDistances.TryGetValue((c.SourcePath, targetPath), out var body))
                bodyDistances[(c.SourcePath, targetPath)] = body = BodyDistance(c.SourcePath, targetPath, ProbesOf);
            var skin = SkinDistance(c.SkinProbes, targetPath);
            return c.Distance(body.P90, body.Mean, skin.P90, skin.Mean);
        }
        // A size cut from one read on another body than "Made on" says, said once per option under "Check in game".
        var warnedOtherBody = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void WarnOtherBody(Candidate c)
        {
            if (c.OtherBody && warnedOtherBody.Add(c.Name))
                runLog.Warnings.Add(string.Format(Strings.SizeSet.MadeOnOtherBodyFmt, piece.Label, c.Name, c.Entry.Name,
                                                  sources.MadeOnName ?? request.MadeOn));
        }

        // ── where the sizes go ──
        using var group = new SizeGroup(root, piece);
        var authorSizes = AuthorSizes(root, piece, group.Ours);

        bool yas = request.Yas || primary.Yas;

        foreach (string key in request.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!ForRace(key, sizeSlot, race)) continue;
            var (body, target, missing) = BodyFor(key, sizeSlot, yas, request.Bodies, sourceEntry, notes, piece.Label);
            if (missing != null)
                runLog.Add(piece, SizeStatus.Skipped, Codes.NotInstalled, missing, group.Name, body: key, yas: yas);
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
                var clock = System.Diagnostics.Stopwatch.StartNew();
                void Out(SizeStatus status, string? code, string? reason, string? from = null, Measures? measures = null,
                         string? cutFrom = null)
                    => runLog.Add(piece, status, code, reason, group.Name, option, key, target!.Dir, body!.Yas, from: from, cutFrom: cutFrom,
                                  measures: measures, seconds: status == SizeStatus.Made ? clock.Elapsed.TotalSeconds : null);
                void NoteOut(string note, string code)
                {
                    notes.Add(note);
                    Out(SizeStatus.Skipped, code, note);
                }
                // Where a candidate goes for this size. Legs following the chest go onto the legs that size's chest is
                // worn with — across body mods the table's, within one the candidate's own family's; a size with none
                // (Neolithe XS keeps the Small hips) has nowhere to go from it.
                BodyOption? ToOf(Candidate c)
                    => TargetRel(piece.FollowsChest, Across(c), size, c.Pick!.Option.Rel) is { } rel
                        ? OptionByFile(targetCat.For(piece.Slot), rel)
                        : null;
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
                    foreach (var c in alike) eligible.Add((c, Measure(c, targetCat.PathOf(ToOf(c)!))));
                    if (eligible.Count == 0)
                    {
                        Note($"{option}: none of the author's sizes goes onto it ({string.Join("; ", said)})");
                        return null;
                    }
                    var (chosen, step, gap, misfits) = ChooseWithMisfits(eligible);
                    for (int i = 0; i < eligible.Count; i++)
                        said.Add($"{eligible[i].Item1.Name}: {Describe(eligible[i].Item2, misfits.Contains(i))}");
                    if (LoneMadeOn(eligible, chosen) is { } lone) said.Add(lone);
                    return (chosen, $"{option} from {chosen.Name} (by {step}, {gap * 1000f:F1} mm ahead; {string.Join("; ", said)})");
                }

                if (group.AlreadyThere(option, request.Remake))
                {
                    log.Information("[Proteus] size set: {0}: \"{1}\" already there", piece.Label, option);
                    AlreadyOut(group, option, (code, why) => Out(SizeStatus.Skipped, code, why));
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
                    Out(SizeStatus.Skipped, Codes.NoChestOption, $"no \"{option}\" of the chest's to join");
                    continue;
                }
                // A size the author ships is skipped before any source is measured for it — where it is one size for
                // every source; legs following a chest go onto the legs of the source's family.
                if (!piece.FollowsChest)
                {
                    if (OptionByFile(targetCat.For(piece.Slot), size.Rel) is not { } fixedTo)
                    {
                        NoteOut(string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name), Codes.NoSuchSize);
                        continue;
                    }
                    if (AuthorHas(authorSizes, targetCat, fixedTo, piece.Slot, race) is { } own)
                    {
                        log.Information("[Proteus] size set: {0}: {1} is the author's own \"{2}\"", piece.Label, option, own);
                        Out(SizeStatus.Skipped, Codes.AuthorsOwn, $"the author's own \"{own}\"");
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
                        Out(SizeStatus.Skipped, Codes.MadeOnIt, "the size it was made on");
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
                        Out(SizeStatus.Skipped, Codes.MadeOnIt, "the size it was made on");
                        continue;
                    }
                    // Legs following the chest with nowhere to go from any size: a size with no legs of its own keeps
                    // the author's, as before; one whose legs the body mod lacks is said so.
                    if (piece.FollowsChest && sources.Choices.All(c => ToOf(c) == null))
                    {
                        if (sources.Choices.Any(c => TargetRel(true, Across(c), size, c.Pick!.Option.Rel) != null))
                            NoteOut(string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name), Codes.NoSuchSize);
                        else
                        {
                            log.Information("[Proteus] size set: {0}: {1} has no legs of its own", piece.Label, option);
                            Out(SizeStatus.Skipped, Codes.NoOwnLegs, "no legs of its own: keeps the author's");
                        }
                        continue;
                    }
                    if (Nearest() is not { } nearest)
                    {
                        Out(SizeStatus.Skipped, Codes.NoSource, "none of the author's sizes goes onto it");
                        continue;
                    }
                    (chosen, sourceOption) = (nearest.Chosen, nearest.Chosen.Option);
                    Note(nearest.Why);
                }
                var to = ToOf(chosen)!;
                if (piece.FollowsChest && AuthorHas(authorSizes, targetCat, to, piece.Slot, race) is { } theirs)
                {
                    log.Information("[Proteus] size set: {0}: {1} is the author's own \"{2}\"", piece.Label, option, theirs);
                    Out(SizeStatus.Skipped, Codes.AuthorsOwn, $"the author's own \"{theirs}\"");
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
                    Out(SizeStatus.Refused, Codes.RefusedPair, refusal);
                    log.Information("[Proteus] size set: {0} {1}: refused: {2}", piece.Label, option, refusal);
                    continue;
                }
                log.Information("[Proteus] size set: {0} {1}: {2}", piece.Label, option, pair.Correspondence.Describe());
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
                            log.Information("[Proteus] size set: {0} {1}: hips: {2}", piece.Label, option,
                                            legsPair.Correspondence.Describe());
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
                                             warn: line => log.Warning("[Proteus] size set: {0}", line),
                                             carryPiercings: request.Piercings);
                plan = RefitCore.MatchSkinToBody(plan, chosen.SourcePath, targetPath, uvRemap, male, log);
                // Folds are said, never refused (cross-sex-tops-yas).
                log.Information("[Proteus] size set: {0} onto {1} (moved up to {2:F1} mm, {3} folded)", piece.Label, option,
                                plan.Report.WorstMove * 1000f, plan.Report.Folded);
                if (RefitCore.ReleasedLog(plan.Report, chosen.ShapeNames) is { } released)
                    log.Information("[Proteus] size set: {0} {1}: let go of kept piece(s) that would sink moved whole: {2}",
                                    piece.Label, option, released);

                // One save per size: each names the size it was cut from. The first piece to make an option leads it —
                // never legs following the chest, which follow it or, when they cannot, fall back to their own nearest:
                // the shoes and gloves beside them are left out rather than laid over a chest cut from another size.
                // Stopped while this size was made: it is not saved — Stop keeps what was saved before it.
                ct.ThrowIfCancellationRequested();
                bool lead = !piece.FollowsChest && !leadSources.ContainsKey(leadKey);
                string fromLabel = RefitCore.LabelFrom(across ? chosen.Entry.Name : null, [chosen.Pick.Option.Label]);
                // The one fold count every size reports: cloth faces turned over on the plan saved.
                int folded = CrossSexRefit.ClothFolded(chosen.Bytes, plan);
                if (!group.Save(target.Dir, fromLabel,
                                [new BodyRetargetWriter.Refit(option, plan.Model, toLabel, Materials: plan.Materials)], sourceOption,
                                notes, sourceFilesWin: request.Remake && lead))
                {
                    Out(SizeStatus.Failed, Codes.SaveFailed, notes[^1]);
                    continue;
                }
                Out(SizeStatus.Made, null, null, fromLabel, Measures.Of(plan, folded), sourceOption);
                WarnOtherBody(chosen);
                if (lead && sourceOption != null)
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
        private readonly string gamePath;
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

        /// <param name="name">The group, when not the piece's own: the men's group a women's piece's men's versions go
        /// into (see <c>SizeSetRefit.CrossSex.cs</c>).</param>
        /// <param name="gamePath">Where its sizes are saved, when not where the piece is drawn: c0101 for those.</param>
        internal SizeGroup(string root, Piece piece, string? name = null, string? gamePath = null)
        {
            this.root = root;
            this.piece = piece;
            this.gamePath = gamePath ?? piece.GamePath;
            var record = BodyRetargetWriter.ReadRecord(root);
            Name = name ?? piece.Group ?? piece.GroupName;
            existing = (PenumbraModMeta.TryReadFileOptions(root, Name) ?? []).Select(o => o.Name)
                                                                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inGroup = (record?.Options ?? []).Where(e => string.Equals(record!.GroupOf(e), Name,
                                                                           StringComparison.OrdinalIgnoreCase)).ToList();
            ours = inGroup.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Which of ours already carry a refit of THIS model. An option of ours without one is one the outfit's other
            // piece made — "Rue S" saved for the chest, still wearing the author's legs — and this piece's refit goes into it.
            doneHere = inGroup.Where(e => string.Equals(e.GamePath, this.gamePath, StringComparison.OrdinalIgnoreCase))
                              .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            fromHere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in inGroup.Where(e => string.Equals(e.GamePath, this.gamePath, StringComparison.OrdinalIgnoreCase)))
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

        /// <summary>Whether an option of ours already carries a refit of this piece.</summary>
        internal bool Carries(string option) => doneHere.Contains(option);

        /// <summary>What an option of ours that carries this piece names it was cut from ("Yet Another Body+ — Large"),
        /// or null.</summary>
        internal string? FromOf(string option) => fromHere.GetValueOrDefault(option);

        /// <summary>
        /// Save sizes — chests and legs one per size, each naming the size it was cut from. False, noted, on failure:
        /// the note is always the last of <paramref name="notes"/>, which is what the caller records the failure by.
        /// </summary>
        /// <param name="sourceFilesWin">Made again from another of the author's options: that option's files replace
        /// the ones carried from the old (see <see cref="BodyRetargetWriter.Save"/>).</param>
        /// <param name="manipulations">Metadata the sizes carry: the EQDP (and Est) entries a men's version needs.</param>
        internal bool Save(string targetDir, string from, List<BodyRetargetWriter.Refit> refits, string? sourceOption,
                           List<string> notes, bool sourceFilesWin = false, IReadOnlyList<object>? manipulations = null)
        {
            var outcome = BodyRetargetWriter.Save(root, Name, gamePath, targetDir, from, refits,
                                                  piece.Group != null ? sourceOption : null, manipulations,
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

    /// <summary>
    /// Says why a size that is already in the mod was left: an option of the author's by that name is their own size;
    /// one of ours was made by an earlier run.
    /// </summary>
    private static void AlreadyOut(SizeGroup group, string option, Action<string, string> outcome)
    {
        if (group.Ours.Contains(option)) outcome(Codes.AlreadyThere, $"\"{option}\" is already in the mod, made by an earlier run");
        else outcome(Codes.AuthorsOwn, $"the author's own \"{option}\"");
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
    /// <returns>The body and its mod; or, with neither, the note saying it is not installed (null when the table has
    /// nothing for the slot).</returns>
    private static (SupportedBodies.Body? Body, BodyModIndex.Entry? Mod, string? Missing) BodyFor(string key, string slot,
        bool yas, IReadOnlyList<BodyModIndex.Entry> installed, BodyModIndex.Entry? source, List<string> notes, string piece)
    {
        var plain = SupportedBodies.Of(key, slot, false);
        var yasBody = SupportedBodies.Of(key, slot, true);
        if (plain == null && yasBody == null) return (null, null, null);   // YAB Buff has no legs of its own: nothing to say

        var order = yas ? new[] { yasBody, plain } : [plain, yasBody];
        foreach (var body in order.OfType<SupportedBodies.Body>())
        {
            if (body.Family && source != null && SupportedBodies.Find(body, [source]) != null) return (body, source, null);
            if (SupportedBodies.Find(body, installed) is { } mod) return (body, mod, null);
        }

        string label = (plain ?? yasBody)!.Label;
        string note = string.Format(Strings.SizeSet.NotInstalledFmt, piece, label);
        notes.Add(note);
        return (null, null, note);
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
