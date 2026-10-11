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
/// For each chest and legs model of the mod: the first of the author's own sizes whose body can be read is the one
/// refitted, from the body it was found to be made on, onto every size <see cref="SupportedBodies"/> lists for each body
/// ticked. The sizes are saved beside the author's in their size group, as the Studio's Body size tool saves one, named
/// "Rue S", "YAB Buff L" and so on. A size the mod already has — one of the author's that IS that size, or one a
/// previous run made — is left as it is.
/// <para/>
/// No UI and no IPC: everything here reads and writes files, on a worker. The caller tells Penumbra afterwards.
/// </summary>
internal static class SizeSetRefit
{
    /// <param name="ModRoot">The mod's folder.</param>
    /// <param name="Keys">The bodies ticked (<see cref="SupportedBodies.Choices"/>).</param>
    /// <param name="Yas">Refit onto a body's Yet Another Skeleton sizes where it has them. Neolithe has none in the table.
    /// A garment already weighted to YAS goes to them whatever this says.</param>
    /// <param name="Remake">Make again the sizes an earlier run made, rather than leaving them.</param>
    /// <param name="Bodies">The installed body mods.</param>
    /// <param name="ReadGameFile">Reads a game path from the game's own data, to tell the game's models a mod merely
    /// ships apart from the author's (see <see cref="PiecesOf"/>); null takes every model as the author's.</param>
    internal sealed record Request(string ModRoot, IReadOnlyList<string> Keys, bool Yas, bool Remake,
                                   IReadOnlyList<BodyModIndex.Entry> Bodies, Func<string, byte[]?>? ReadGameFile = null);

    /// <param name="Made">Sizes saved.</param>
    /// <param name="Notes">Every size left out, and why — for the author to read.</param>
    internal sealed record Result(int Made, IReadOnlyList<string> Notes);

    /// <summary>
    /// A chest or legs model of some outfit for a Midlander woman or man — the races the table's bodies are, and the ones
    /// every other race of the sex falls back to. The body itself (<c>e0000</c>) is not an outfit and is never one of a
    /// mod's pieces.
    /// </summary>
    private static readonly Regex PiecePath = new(@"^chara/equipment/e(?!0000)\d{4}/model/c(0101|0201)e\d{4}_(top|dwn)\.mdl$",
                                                  RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    internal sealed record Piece(string GamePath, string Slot, string? Group,
                                 IReadOnlyList<(string? Option, string File)> Options, string Label, string GroupName,
                                 string Race, bool FollowsChest = false);

    /// <summary>
    /// The chest and legs models of a mod, each with the group its sizes are switched in. A model only a refit of ours
    /// gives — under <see cref="BodyRetargetWriter.Subfolder"/> — is not one of the author's.
    /// <para/>
    /// Nor is the game's own model shipped at its own path. Mods built in TexTools carry the other races' models of an
    /// item they only changed for one — Hell for Leather, a women's outfit, gives Midlander men the game's own Anemos
    /// gambison, re-saved — and taking that for a men's piece offered TBSE sizes of a garment its author never made.
    /// </summary>
    /// <param name="readGameFile">The game's own copy of a game path; null takes every model as the author's.</param>
    internal static List<Piece> PiecesOf(string modRoot, Func<string, byte[]?>? readGameFile = null)
    {
        var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
        var record = BodyRetargetWriter.ReadRecord(modRoot);
        var ourFiles = (record?.Options ?? []).Select(e => RefitCore.Normal(e.File)).ToHashSet(StringComparer.Ordinal);
        var single = RefitCore.SingleGroups(modRoot);
        var pieces = new List<Piece>();

        foreach (var byPath in redirects.Where(r => PiecePath.IsMatch(r.GamePath))
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

        // Legs sized in the same group as a chest of their race follow the chest's sizes. Chests first, so the options
        // the legs go into are already there, named and placed by the chest.
        return pieces.OrderBy(p => p.Slot == "_dwn").Select(p => p.Slot == "_dwn" && p.Group != null
                                  && pieces.Any(c => c.Slot == "_top" && c.Race == p.Race
                                                     && string.Equals(c.Group, p.Group, StringComparison.OrdinalIgnoreCase))
                                      ? p with { FollowsChest = true }
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
        var pieces = PiecesOf(request.ModRoot, request.ReadGameFile);
        if (pieces.Count == 0)
        {
            notes.Add(Strings.SizeSet.NoPieces);
            return new Result(0, notes);
        }

        foreach (var piece in pieces)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                made += RunPiece(request, piece, uvRemap, log, progress, notes, ct);
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

    private static int RunPiece(Request request, Piece piece, UVRemapService? uvRemap, IPluginLog log,
                                Action<string> progress, List<string> notes, CancellationToken ct)
    {
        string root = request.ModRoot;
        var record = BodyRetargetWriter.ReadRecord(root);
        string race = piece.Race;
        bool male = BodySizeCatalog.IsMaleRace(race);
        // The table its sizes come from: its own slot's, or the chest's for legs that follow it.
        string sizeSlot = piece.FollowsChest ? "_top" : piece.Slot;

        // A man's piece with only women's bodies ticked, or the other way about, has nothing to go to: not read at all,
        // and nothing to say — a unisex mod asked for Rue sizes is not missing anything for its men's models.
        if (!request.Keys.Any(k => ForRace(k, sizeSlot, race))) return 0;

        // ── what it was made on: the author's first size whose body can be read, in the author's order ──
        progress(string.Format(Strings.SizeSet.ReadingFmt, piece.Label));
        var mods = request.Bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        if (mods.Count == 0)
        {
            notes.Add(Strings.SizeSet.NoBodies);
            return 0;
        }
        string preferred = request.Keys.Select(k => SupportedBodies.Of(k, sizeSlot, false))
                                       .OfType<SupportedBodies.Body>()
                                       .Where(b => b.Race == race)
                                       .Select(b => SupportedBodies.Find(b, request.Bodies)?.Dir)
                                       .FirstOrDefault(d => d != null) ?? mods[0].Dir;

        (string? Option, string File, byte[] Bytes, ModelParts Garment, HashSet<string> Bones, AutoRefitDecisions.Pick Pick)? source = null;
        // Every option until one reads, not the first few: a men's shirt lists "Type M", "TBSE-Q" and two Thicclanders
        // fits — none on an installed body — before its TBSE ones. A readable first size stops this at once.
        foreach (var (option, file) in piece.Options)
        {
            ct.ThrowIfCancellationRequested();
            string path = BodySizeCatalog.ResolveCaseInsensitive(root, file);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            if (ModelPartReader.Read(bytes) is not { } garment) continue;
            var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
            var pick = RefitCore.DetectSource(garment, bones, piece.Slot, race, mods, preferred, _ => true,
                                              note => log.Information("[Proteus] size set: {0}: {1}", piece.Label, note));
            if (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence)) continue;
            source = (option, file, bytes, garment, bones, pick);
            break;
        }
        if (source is not { } src)
        {
            notes.Add(string.Format(Strings.SizeSet.UnreadableFmt, piece.Label));
            return 0;
        }

        var sourceEntry = request.Bodies.First(b => string.Equals(b.Dir, src.Pick.Dir, StringComparison.OrdinalIgnoreCase));
        var sourceCat = sourceEntry.Catalog;
        string sourcePath = sourceCat.PathOf(src.Pick.Option);
        log.Information("[Proteus] size set: {0} from {1} made on {2} / {3} ({4})", piece.Label, src.Option ?? src.File,
                        sourceEntry.Name, src.Pick.Option.Label, src.Pick.Confidence);

        // ── the legs its hem sits on, for a chest piece ──
        BodyOption? sourceLegs = null;
        if (piece.Slot == "_top" && sourceCat.For("_dwn", race) is { Count: > 0 } legsOptions)
        {
            var ranking = BodySizeMatch.Rank(src.Garment, legsOptions, sourceCat.PathOf, src.Bones);
            if (ranking.Best is { } best && AutoRefitDecisions.Proceed(ranking.Confidence)) sourceLegs = best.Option;
            else log.Information("[Proteus] size set: {0}: the hem's legs not read ({1}), hips left as they are",
                                 piece.Label, ranking.Confidence);
        }

        // ── where the sizes go ──
        string group = piece.Group ?? piece.GroupName;
        var existing = (PenumbraModMeta.TryReadFileOptions(root, group) ?? []).Select(o => o.Name)
                                                                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inGroup = (record?.Options ?? []).Where(e => string.Equals(record!.GroupOf(e), group,
                                                                       StringComparison.OrdinalIgnoreCase)).ToList();
        var ours = inGroup.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Which of ours already carry a refit of THIS model. An option of ours without one is one the outfit's other piece
        // made — "Rue S" saved for the chest, still wearing the author's legs — and this piece's refit goes into it.
        var doneHere = inGroup.Where(e => string.Equals(e.GamePath, piece.GamePath, StringComparison.OrdinalIgnoreCase))
                              .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var authorSizes = AuthorSizes(root, piece, ours);

        // A group of our own is rewritten whole by every save, which selects the size saved last by default: a fresh
        // install, or a pack exported from here, would then draw "Bibo L" instead of the author's model. Its default is
        // put back afterwards — what it was, or "Original" for a group this run makes. The author's group is only added
        // to, and keeps its default untouched.
        bool authorGroup = BodyRetargetWriter.IsAuthorGroup(root, group);
        string defaultBefore = PenumbraModMeta.SingleDefault(root, group) ?? BodyRetargetWriter.OriginalOption;

        bool yas = request.Yas || SupportedBodies.IsYas(src.Bones);
        var pieces = RefitCore.ShapePieces(src.Garment, RefitCore.DefaultKeepShape(src.Garment));
        ushort? sourceMask = RefitCore.MaskOf(src.Pick.Dir, sourceCat, piece.Slot, null);
        ushort? sourceLegsMask = RefitCore.MaskOf(src.Pick.Dir, sourceCat, "_dwn", null);
        int saved = 0;

        foreach (string key in request.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!ForRace(key, sizeSlot, race)) continue;
            var (body, target) = BodyFor(key, sizeSlot, yas, request.Bodies, sourceEntry, notes, piece.Label);
            if (body == null || target == null) continue;
            var targetCat = target.Catalog;
            bool across = !string.Equals(target.Dir, src.Pick.Dir, StringComparison.OrdinalIgnoreCase);
            ushort? targetMask = RefitCore.MaskOf(target.Dir, targetCat, piece.Slot, null);
            ushort? targetLegsMask = RefitCore.MaskOf(target.Dir, targetCat, "_dwn", null);

            var refits = new List<BodyRetargetWriter.Refit>();
            // A family's sizes are read off the source's file name, which for legs following a chest is a legs name: those
            // take the table's chest sizes, and their legs move along the family below.
            foreach (var size in SupportedBodies.SizesFor(body, across || piece.FollowsChest ? null : src.Pick.Option.Rel))
            {
                ct.ThrowIfCancellationRequested();
                string option = SupportedBodies.OptionName(body, size);
                // The author's option of that name is theirs; one of ours is done when it carries this model, and made
                // again only when asked.
                if (existing.Contains(option) && (!ours.Contains(option) || (doneHere.Contains(option) && !request.Remake)))
                {
                    log.Information("[Proteus] size set: {0}: \"{1}\" already there", piece.Label, option);
                    continue;
                }
                // Legs following the chest only join a size the chest made. One it did not — the author already has it
                // under another name, or the pair was refused — made here would wear refitted legs under the author's
                // unrefitted chest, copied in from the option the legs were cut from.
                if (piece.FollowsChest && !ours.Contains(option))
                {
                    log.Information("[Proteus] size set: {0}: no \"{1}\" of the chest's to join", piece.Label, option);
                    continue;
                }

                // Legs following the chest go onto the legs that size's chest is worn with; a size with none (Neolithe XS
                // keeps the Small hips) keeps the author's.
                string? rel = !piece.FollowsChest ? size.Rel
                            : across ? size.LegsRel
                            : SupportedBodies.FamilyLegs(src.Pick.Option.Rel, size.Name);
                if (rel == null)
                {
                    log.Information("[Proteus] size set: {0}: {1} has no legs of its own", piece.Label, option);
                    continue;
                }
                if (OptionByFile(targetCat.For(piece.Slot), rel) is not { } to)
                {
                    notes.Add(string.Format(Strings.SizeSet.NoSuchSizeFmt, piece.Label, option, target.Name));
                    continue;
                }
                if (AutoRefitDecisions.SameFile(src.Pick.Dir, src.Pick.Option, target.Dir, to))
                {
                    log.Information("[Proteus] size set: {0}: {1} is the size it was made on", piece.Label, option);
                    continue;
                }
                if (AuthorHas(authorSizes, targetCat, to, piece.Slot, race) is { } theirs)
                {
                    log.Information("[Proteus] size set: {0}: {1} is the author's own \"{2}\"", piece.Label, option, theirs);
                    continue;
                }

                progress(string.Format(Strings.SizeSet.WorkingFmt, piece.Label, option));
                string targetPath = targetCat.PathOf(to);
                if (BodyRetarget.BuildPair(piece.Slot, sourcePath, targetPath, SlotName(piece.Slot), male, sourceMask,
                                           targetMask, uvRemap, out var pair) is { } refusal)
                {
                    notes.Add(string.Format(Strings.SizeSet.RefusedFmt, piece.Label, option, refusal));
                    continue;
                }
                var pairs = new List<BodyRetarget.SlotPair> { pair };

                // The hem follows onto the new size's hips: the table's legs across body mods, the source's legs moved
                // along their own sizes within one.
                string toLabel = to.Label;
                if (sourceLegs != null)
                {
                    string? legsRel = across ? size.LegsRel : SupportedBodies.FamilyLegs(sourceLegs.Rel, size.Name);
                    if (legsRel != null && OptionByFile(targetCat.For("_dwn"), legsRel) is { } legsTo
                        && !AutoRefitDecisions.SameFile(src.Pick.Dir, sourceLegs, target.Dir, legsTo))
                    {
                        if (BodyRetarget.BuildPair("_dwn", sourceCat.PathOf(sourceLegs), targetCat.PathOf(legsTo),
                                                   SlotName("_dwn"), male, sourceLegsMask, targetLegsMask, uvRemap,
                                                   out var legsPair) is { } legsRefusal)
                            log.Information("[Proteus] size set: {0} {1}: hips left as they are: {2}", piece.Label, option,
                                            legsRefusal);
                        else
                        {
                            pairs.Add(legsPair);
                            toLabel += " + " + legsTo.Label;
                        }
                    }
                }

                var plan = BodyRetarget.Plan(src.Garment, src.Bytes, pairs, piece.Slot, held: new HashSet<int>(),
                                             replaceSkin: true, acrossBodies: across, clearBody: false, cutHidden: true,
                                             keepShape: pieces);
                plan = RefitCore.MatchSkinToBody(plan, sourcePath, targetPath, uvRemap, male, log);
                refits.Add(new BodyRetargetWriter.Refit(option, plan.Model, toLabel));
                log.Information("[Proteus] size set: {0} onto {1} (moved up to {2:F1} mm)", piece.Label, option,
                                plan.Report.WorstMove * 1000f);
            }
            if (refits.Count == 0) continue;

            // One save per body mod: the record names the body each size was made for.
            string from = RefitCore.LabelFrom(across ? sourceEntry.Name : null, [src.Pick.Option.Label]);
            var outcome = BodyRetargetWriter.Save(root, group, piece.GamePath, target.Dir, from, refits,
                                                  piece.Group != null ? src.Option : null);
            if (!outcome.Ok)
            {
                notes.Add(string.Format(Strings.SizeSet.SaveFailedFmt, piece.Label, outcome.Message));
                continue;
            }
            saved += refits.Count;
            existing.UnionWith(refits.Select(r => r.Option));
            ours.UnionWith(refits.Select(r => r.Option));
            doneHere.UnionWith(refits.Select(r => r.Option));
        }
        if (saved > 0 && !authorGroup) PenumbraModMeta.SetSingleDefault(root, group, defaultBefore);
        return saved;
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
    private static (SupportedBodies.Body? Body, BodyModIndex.Entry? Mod) BodyFor(string key, string slot, bool yas,
        IReadOnlyList<BodyModIndex.Entry> installed, BodyModIndex.Entry source, List<string> notes, string piece)
    {
        var plain = SupportedBodies.Of(key, slot, false);
        var yasBody = SupportedBodies.Of(key, slot, true);
        if (plain == null && yasBody == null) return (null, null);   // YAB Buff has no legs of its own: nothing to say

        var order = yas ? new[] { yasBody, plain } : [plain, yasBody];
        foreach (var body in order.OfType<SupportedBodies.Body>())
        {
            if (body.Family && SupportedBodies.Find(body, [source]) != null) return (body, source);
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

    private static string SlotName(string slot) => slot == "_dwn" ? Strings.Parts.RetargetLegs : Strings.Parts.RetargetChest;
}
