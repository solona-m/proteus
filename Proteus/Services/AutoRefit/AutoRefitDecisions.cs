using System;
using System.Collections.Generic;
using System.Linq;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;

namespace Proteus.Services;

/// <summary>
/// The choices the automatic refit makes without asking — which size, which body, whether to act at all — kept apart
/// from the watcher's threading and IPC so each can be tested on plain values.
/// </summary>
internal static class AutoRefitDecisions
{
    /// <summary>The body slots the automatic refit acts on, in the order a design changing several is worked through.</summary>
    internal static readonly string[] Slots = ["_top", "_dwn", "_glv", "_sho"];

    /// <summary>The accessory slots a garment can be modded onto — a jacket on a necklace — refitted as the body part
    /// its cloth covers (see <see cref="AccessoryCover"/>). Never a body slot: every body-part loop goes over
    /// <see cref="Slots"/>.</summary>
    internal static readonly string[] AccessorySlots = ["_nek", "_ear", "_wrs", "_rir", "_ril"];

    /// <summary>Every slot a walk looks at, body slots first: a top and the jacket on its necklace put on together are
    /// worked through top first, so the jacket finds the top's reading and its outcome.</summary>
    internal static readonly string[] WornSlots = [.. Slots, .. AccessorySlots];

    internal static bool IsAccessory(string slot) => Array.IndexOf(AccessorySlots, slot) >= 0;

    /// <summary>Words in a body mod's name that name no body: shared by so many, or so generic, that an option carrying
    /// one says nothing about which body it is a size of.</summary>
    private static readonly HashSet<string> NotABody = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "bodies", "all", "one", "the", "and", "for", "nsfw", "sfw", "default", "size", "sizes", "small", "medium",
        "large", "mod", "mods", "version", "edit", "base",
    };

    private static readonly System.Text.RegularExpressions.Regex Word = new(@"[\p{L}\p{N}]{3,}",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The words of a name as <see cref="NamesABody"/> compares them: letters and digits, three or more, any case.</summary>
    internal static IEnumerable<string> Words(string name)
        => Word.Matches(name).Select(m => m.Value.ToLowerInvariant()).Where(w => !w.All(char.IsDigit));

    /// <summary>
    /// Whether an author's option names a body — Scarlet's "Neolithe XS", "Bibo+ M" — so the piece already comes in the
    /// author's sizes for bodies and is left to them. By a word of an installed body mod's name or folder, never a word
    /// any body's sizes use: "Puffy" and "Gen C" are Neolithe sizes, and a mod named "puffy …" must not make every
    /// option called "Puffy" a body's. "Small", "Medium", "Large" name no body.
    /// </summary>
    /// <param name="bodyNames">Every installed body mod's name and folder.</param>
    /// <param name="sizeWords">Every word of every installed body's size labels and sections.</param>
    internal static bool NamesABody(string option, IEnumerable<string> bodyNames, IReadOnlySet<string> sizeWords)
    {
        var bodies = bodyNames.SelectMany(Words).Where(w => !NotABody.Contains(w) && !sizeWords.Contains(w))
                              .ToHashSet(StringComparer.Ordinal);
        return Words(option).Any(bodies.Contains);
    }

    /// <summary>
    /// The part of the body a piece worn in <paramref name="slot"/> is refitted on when it keeps its own end: the legs
    /// for shoes, the chest model for gloves (it carries the arms to the wrists). Null for every other slot, which is
    /// refitted as it always was. See <see cref="KeepEnds"/>.
    /// </summary>
    internal static string? LimbOf(string slot) => slot switch
    {
        "_sho" => "_dwn",
        "_glv" => "_top",
        _ => null,
    };

    /// <summary>How the new body's skin goes into an unattended refit: cut to where the garment drew its own, whether
    /// the cloth is pushed clear of the body wherever it is buried in it, and whether a top's cut skin is carried on in
    /// under its cloth (see <see cref="BodyRetarget.TuckUnderCloth"/>), and whether a top's cloth buried in a far larger
    /// new breast is cleared out of it from deeper than usual (see <see cref="BodyRetarget.DeepClearDepth"/>), and whether a
    /// top's cloth the refit carried further off the new chest than its author put it comes back toward the author's
    /// standoff (see <see cref="BodyRetarget.KeepStandoff"/>).</summary>
    internal readonly record struct SkinRule(bool CutHidden, bool ClearBody, bool TuckUnderCloth, bool DeepClear,
                                             bool KeepStandoff);

    /// <summary>
    /// The new body's skin goes in whole: nobody is watching an unattended refit to catch the holes an author's cut can
    /// leave on another body. The game's own gear is the exception. Its cloth is buried in the game's body on purpose —
    /// the game draws no chest skin under a worn top, only the skin the top carries at its openings — so the skin goes
    /// where the game puts it, and the cloth is cleared of the body. The Neo-Ishgardian Top of Maiming on Neolithe's
    /// neobelly SFW M: 679 cm² of skin through the cloth whole, 224 cut, 193 cut and cleared.
    /// <para/>
    /// And a top's cut skin is tucked in under its cloth: the game ends its skin at the neckline, turned in under the
    /// cloth, and laid onto a fuller bust that edge lands on top of the breast while the neckline rides out with it — Dirndl's
    /// Bodice on Neolithe's Almond XS showed the inside of its back through the bust.
    /// </summary>
    /// <param name="gameGear">The piece is drawn from the game's own file, not a mod's.</param>
    internal static SkinRule SkinFor(bool gameGear)
        => gameGear ? new SkinRule(true, true, true, true, true) : new SkinRule(false, false, false, false, false);

    /// <summary>
    /// The piece is the game's own: no mod serves it, or the mod serving it is one Proteus made for a game item — it
    /// serves nothing, at any path, but refits recorded as made from the game's own garment. Refitted again onto another
    /// body it is still the game's garment underneath. The whole mod is judged, not the worn path: a modder's male-only
    /// top baked to the wearer's race serves only the refit at the wearer's path, its own model sitting at another. A
    /// mod Proteus made to hold a hand refit of a modder's garment holds nothing but refits too, but none from the game.
    /// A mod shipping the game's model (a recolour, a "— Proteus edit" copy) serves its own file, and is a mod.
    /// </summary>
    /// <param name="madeByProteus">The mod's manifest names Proteus as its author — see
    /// <see cref="RefitModService.IsMadeByProteus"/>.</param>
    internal static bool IsGameGear(string? modRoot, string gamePath, IReadOnlyList<PenumbraModMeta.Redirect> redirects,
                                    BodyRetargetWriter.Record? record, bool madeByProteus)
    {
        if (modRoot == null) return true;
        if (record == null || !madeByProteus) return false;
        var fromGame = record.Options.Where(e => e.FromGame).Select(e => RefitCore.Normal(e.File))
                             .ToHashSet(StringComparer.Ordinal);
        return redirects.Any(d => RefitCore.Normal(d.GamePath) == RefitCore.Normal(gamePath))
               && redirects.All(d => fromGame.Contains(RefitCore.Normal(d.File)));
    }

    /// <summary>
    /// Whether a mod's own sizes are looked at before refitting: only for a mod's garment refitted as the race it is
    /// drawn as. The author's sizes are of the drawn race; a garment baked or bent into another is measured against
    /// bodies of that one, which those sizes were never made between.
    /// </summary>
    internal static bool AuthorSizesApply(string? modRoot, RacialModelBake.Route route) => modRoot != null && !route.Any;

    /// <summary>
    /// Whether BOTH sides of an automatic refit have bodies of a race — what <see cref="RefitCore.Route"/> bends racial
    /// gear down by. The refit-onto mod's side is <paramref name="targetHas"/>. The made-for side is the game's own
    /// bodies for the game's own gear, which most races have none of (<paramref name="gameBodyRaces"/>, as
    /// <c>"0201"</c> codes); a mod's garment was made on some body mod, and is read on whichever it is.
    /// </summary>
    internal static Func<ushort, bool> ShapeBodies(Func<ushort, bool> targetHas, bool gameMade,
                                                   IReadOnlySet<string>? gameBodyRaces)
        => code => targetHas(code) && (!gameMade || gameBodyRaces?.Contains($"{code:D4}") == true);

    /// <summary>Whether a reading is sure enough to refit from unattended.</summary>
    internal static bool Proceed(Confidence confidence)
        => confidence is Confidence.Exact or Confidence.Likely or Confidence.Guess;

    /// <summary>The less sure of two readings: Exact, then Likely, Guess, Ambiguous, and no evidence below all of them.</summary>
    internal static Confidence Lower(Confidence a, Confidence b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(Confidence c) => c switch
    {
        Confidence.Exact => 0,
        Confidence.Likely => 1,
        Confidence.Guess => 2,
        Confidence.Ambiguous => 3,
        Confidence.TooLittle => 4,
        _ => 5,
    };

    /// <summary>
    /// The option a remembered size names, among <paramref name="options"/> (one slot, one race): the same file, or
    /// failing that the same option by group, heading and name — the same size at another race.
    /// </summary>
    internal static BodyOption? Resolve(IReadOnlyList<BodyOption> options, BodySizeRef? size)
    {
        if (size == null) return null;
        string rel = RefitCore.Normal(size.Rel);
        return options.FirstOrDefault(o => RefitCore.Normal(o.Rel) == rel)
            ?? options.FirstOrDefault(o => Same(o.Group, size.Group) && Same(o.Section, size.Section)
                                           && Same(o.Name, size.Name));
    }

    /// <summary>A size to remember, from the option picked.</summary>
    internal static BodySizeRef RefOf(BodyOption option)
        => new() { Rel = option.Rel, Group = option.Group, Section = option.Section, Name = option.Name };

    /// <summary>
    /// The size of a part nobody chose — hands or feet — to go with the size that was: the option in the same heading
    /// with the same name ("SFW M" under "--- DEFAULT ---" beside the chest's own), then by name alone, then the first
    /// of the same heading. Failing all of those, the size the character already wears there, and then the first the
    /// author lists — a body mod with one pair of hands has exactly one answer.
    /// </summary>
    internal static BodyOption? Companion(IReadOnlyList<BodyOption> options, BodyOption? anchor, BodyOption? worn)
    {
        if (options.Count == 0) return null;
        if (anchor != null)
        {
            if (options.FirstOrDefault(o => Same(o.Section, anchor.Section) && Same(o.Name, anchor.Name)) is { } both)
                return both;
            if (options.FirstOrDefault(o => Same(o.Name, anchor.Name)) is { } named) return named;
            if (anchor.Section.Length > 0 && options.FirstOrDefault(o => Same(o.Section, anchor.Section)) is { } under)
                return under;
        }
        if (worn != null && options.Contains(worn)) return worn;
        return options[0];
    }

    /// <summary>
    /// Whether a reading of ANOTHER part than the garment's own slot is good enough to refit that part too. A clear
    /// reading is; so is a guess that lands on the same size, under the same heading, as the garment's own slot — the
    /// author built the outfit on one body, and the two readings agree on which.
    /// </summary>
    internal static bool AcceptOther(BodySizeMatch.Ranking ranking, BodyOption primarySource)
        => ranking.Best is { } best
           && (ranking.Preselect
               || (ranking.Confidence == Confidence.Guess && Same(best.Option.Section, primarySource.Section)
                   && Same(best.Option.Name, primarySource.Name)));

    /// <summary>The two options are the same model of the same body mod — a refit between them has nothing to do.</summary>
    internal static bool SameFile(string sourceDir, BodyOption source, string targetDir, BodyOption target)
        => string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)
           && RefitCore.Normal(source.Rel) == RefitCore.Normal(target.Rel);

    /// <summary>The body a garment was made on, and how sure the reading is.</summary>
    internal sealed record Pick(string Dir, BodyOption Option, Confidence Confidence, bool FromCloth);

    // ── which body mod it was made on ────────────────────────────────────────
    //
    // Measured over 28 installed body mods: ranking every size of every mod (BodySizeMatch.Rank) to find a chest's body
    // took 14 s and allocated 3.4 GB — the garbage collector's pauses froze the game for the length of it. So the mod
    // is chosen first from a far cheaper reading (BodySizeMatch.Shared: the share of the garment's skin points lying
    // exactly on a body's vertices), and only the chosen mod is ranked. That reading tells body FAMILIES apart
    // cleanly — a Neolithe chest shares 41% with Neolithe's sizes and at most 1% with any other body mod — and, across
    // every size, the mod within a family: a LaRue chest shares 56% with LavaBod+'s own LaRue sizes and 41% with Rue's.
    // It cannot tell sizes of one mod apart (all of Neolithe's chests share the same 42%), which is Rank's job.

    /// <summary>How many of a mod's sizes are read to place it in a family.</summary>
    internal const int Samples = 3;

    /// <summary>
    /// How many of a family mod's sizes are read to choose between the family's mods. Not every size: Neolithe has 114
    /// chests, and reading them all for each of its forks cost a gigabyte to settle what a dozen settle as well.
    /// </summary>
    internal const int ChoiceSamples = 12;

    /// <summary>Below this share with every body, the garment's skin matches no installed body at all.</summary>
    internal const float MinShared = 0.05f;

    /// <summary>
    /// A mod within this of the best sampled share is in the garment's family. Generous: three samples can miss the
    /// sizes that share the most.
    /// </summary>
    internal const float FamilyMargin = 0.10f;

    /// <summary>Mods within this of the best share over every size are the same answer — usually one body and its fork.</summary>
    internal const float TieMargin = 0.02f;

    /// <param name="Share">The best share of any size read.</param>
    /// <param name="Preferred">The body mod refitted onto: among equals, a garment that already fits it stays on it.</param>
    /// <param name="Enabled">Switched on in the collection — what the player wears is what their gear was likely made for.</param>
    /// <param name="Sizes">How many sizes it offers: among equals, a body pack before a one-body copy of it.</param>
    internal readonly record struct ModShare(string Dir, float Share, bool Preferred, bool Enabled, int Sizes);

    /// <summary>
    /// <paramref name="count"/> of a mod's sizes spread evenly over the author's order, first and last included — with
    /// the default, the first, the middle and the last.
    /// </summary>
    internal static List<BodyOption> SamplesOf(IReadOnlyList<BodyOption> options, int count = Samples)
    {
        if (options.Count <= count) return options.ToList();
        var picked = new List<BodyOption>(count);
        for (int i = 0; i < count; i++)
            picked.Add(options[(int)Math.Round(i * (options.Count - 1) / (double)(count - 1))]);
        return picked.Distinct().ToList();
    }

    /// <summary>The mods in the garment's family, from their sampled shares, in the order given. Empty when the garment
    /// matches no installed body.</summary>
    internal static List<string> Family(IReadOnlyList<ModShare> sampled)
    {
        if (sampled.Count == 0) return [];
        float best = sampled.Max(m => m.Share);
        if (best < MinShared) return [];
        return sampled.Where(m => m.Share >= best - FamilyMargin).Select(m => m.Dir).ToList();
    }

    /// <summary>
    /// The one mod to rank the garment's sizes in, from each family mod's best share over every size: the highest, and
    /// among those within <see cref="TieMargin"/> of it, the preferred body, then one switched on, then the larger pack.
    /// </summary>
    internal static string? Choose(IReadOnlyList<ModShare> full)
    {
        if (full.Count == 0) return null;
        float best = full.Max(m => m.Share);
        if (best < MinShared) return null;
        return Prefer(full.Select((m, i) => (m.Dir, m.Preferred, m.Enabled, m.Sizes, i, m.Share >= best - TieMargin)));
    }

    /// <summary>
    /// Among equal answers: the preferred body, then one switched on, then the larger pack, then the first given. The
    /// one order both readings break ties in.
    /// </summary>
    private static string Prefer(IEnumerable<(string Dir, bool Preferred, bool Enabled, int Sizes, int Index, bool Equal)> all)
        => all.Where(x => x.Equal)
              .OrderByDescending(x => x.Preferred)
              .ThenByDescending(x => x.Enabled)
              .ThenByDescending(x => x.Sizes)
              .ThenBy(x => x.Index)
              .First().Dir;

    // ── when no body matches exactly ─────────────────────────────────────────
    //
    // A garment made on an earlier release of its body mod shares next to nothing exactly with what is installed now:
    // "Yet Another Too Tight Top+" (2023, YAB) shared 1.2% with YAB+ 4.2 — and 12.2% with Bibo+, which YAB descends
    // from. Bibo+ won the family, the top was refitted from Bibo+ Nude Small, and the bust came through it by 13 mm. Below
    // WeakShare the family is read by how close the garment's skin lies to each body instead (BodySizeMatch.Near).

    /// <summary>
    /// Below this best exact share the exact reading cannot place the garment. A garment cut from an installed body
    /// shares 40–56% with its family; the YAB top's best, an ancestor's, was 12%.
    /// </summary>
    internal const float WeakShare = 0.25f;

    /// <summary>
    /// Closer than this to the best sampled body — metres, at the 90th percentile — a mod is in the family. Generous, as
    /// <see cref="FamilyMargin"/> is: three samples can miss the size the garment was made on.
    /// </summary>
    internal const float NearFamilyMargin = 0.0015f;

    /// <summary>
    /// Further than this from even the closest body, the garment matches none of them closely enough to be refitted
    /// unattended. The YAB top lay 0.8 mm from an old YAB S and 2.8 mm from YAB+ 4.2; Bibo+, the wrong answer, 4.3.
    /// </summary>
    internal const float NearMax = 0.003f;

    /// <summary>
    /// The closest body must be this much closer than the closest OTHER body, or the reading is no clearer than two
    /// bodies' guesses: an old Bibo+ top lay 2.5 mm from Tre and 2.7 from Bibo+ (0.93). The YAB top, with no copy of its
    /// own old body installed, lay 2.8 mm from YAB+ 4.2 and 3.4 from the next (0.82). The ratio Rank calls Likely.
    /// </summary>
    internal const float NearClearRatio = 0.85f;

    /// <summary>
    /// Fewer than this share of the garment's skin points within reach of any body, and the nearness reading has nothing
    /// to say (<see cref="NearVerdict.NoReading"/>): what little it measured is not the garment.
    /// </summary>
    internal const float NearReached = 0.5f;

    /// <summary>Too few of the garment's <paramref name="points"/> lie near any body to tell by (<see cref="NearReached"/>).</summary>
    internal static bool TooFewReached(int reached, int points) => reached < points * NearReached;

    /// <summary>
    /// A centimetre off at the 90th percentile, the garment's skin lies on none of the bodies, and being nearer one of
    /// them says nothing about which it was made on (<see cref="NearVerdict.NoReading"/>). A Tre top read 20 mm from Tre
    /// and 15 from LavaBod+.
    /// </summary>
    internal const float NearNone = 0.01f;

    /// <summary>
    /// The exact answer is kept on nearness's doubt only while the nearest body lies within this: a sculpt moves a
    /// garment's skin a few millimetres off its body, not the best part of a centimetre. Further, the garment lies near
    /// none of them, and is not refitted from any.
    /// </summary>
    internal const float NearDoubtMax = 0.005f;

    /// <param name="Distance">How far the garment's skin lies from the closest size read (see BodySizeMatch.Near).</param>
    /// <param name="Body">What that closest size IS — its skin's geometry, so a fork that re-saved the same mesh with other
    /// materials is still the same body. Two mods are one answer only when their closest is the same skin. A mod that
    /// read the nearest body's skin among its sizes has that skin as its closest, so a fork sharing the file is caught;
    /// a pack whose own closest is another body nearly as near is a rival, never hidden behind a file it shares. A fork
    /// whose samples missed that size reads as another body, and the reading is refused rather than guessed.</param>
    internal readonly record struct ModNear(string Dir, float Distance, bool Preferred, bool Enabled, int Sizes,
                                            string? Body = null);

    /// <summary>What the nearness reading said.</summary>
    internal enum NearVerdict
    {
        /// <summary>One mod, clearly.</summary>
        Chosen,

        /// <summary>Nothing to measure: most of the garment's skin lies further than the cap from every body, or even the
        /// closest body is a centimetre off. The reading says nothing, and the exact reading's answer stands.</summary>
        NoReading,

        /// <summary>Measured, and no body lies within <see cref="NearMax"/>.</summary>
        NoneClose,

        /// <summary>Two different bodies nearly as close as each other.</summary>
        Unclear,
    }

    /// <summary>
    /// The one mod to rank the garment's sizes in: the closest — when it is within <see cref="NearMax"/> and clear of
    /// every other BODY by <see cref="NearClearRatio"/> — and among the mods whose closest is that same file (its
    /// forks), the order of preference <see cref="Choose"/> uses. Two different bodies are never a tie, however close: the
    /// refit target winning a tie by preference is how a wrong body would slip through.
    /// </summary>
    /// <param name="cap">The distance a point further than every body counts as (BodySizeMatch.NearCap).</param>
    /// <param name="closest">Every mod sharing the closest body, whatever the verdict — what the exact reading's own answer
    /// is checked against.</param>
    internal static NearVerdict ChooseNear(IReadOnlyList<ModNear> full, float cap, out string? chosen,
                                           out IReadOnlyList<string> closest)
    {
        chosen = null;
        closest = [];
        if (full.Count == 0) return NearVerdict.NoReading;
        var top = full.MinBy(m => m.Distance);
        float best = top.Distance;
        if (best >= MathF.Min(cap, NearNone)) return NearVerdict.NoReading;
        bool SameBody(ModNear m) => m.Dir == top.Dir || (top.Body != null && m.Body == top.Body);
        closest = full.Where(SameBody).Select(m => m.Dir).ToList();
        if (best > NearMax) return NearVerdict.NoneClose;
        float runnerUp = full.Where(m => !SameBody(m)).Select(m => m.Distance).DefaultIfEmpty(float.MaxValue).Min();
        if (best > runnerUp * NearClearRatio) return NearVerdict.Unclear;
        chosen = Prefer(full.Select((m, i) => (m.Dir, m.Preferred, m.Enabled, m.Sizes, i, SameBody(m))));
        return NearVerdict.Chosen;
    }

    /// <summary>Whether the exact reading's best share is too weak to stand alone (see <see cref="WeakShare"/>).</summary>
    internal static bool Weak(float bestShare) => bestShare < WeakShare;

    /// <summary>What becomes of a garment the exact reading could not place, once nearness has measured it.</summary>
    internal enum NearOutcome
    {
        /// <summary>Refit from the body nearness chose.</summary>
        Refit,

        /// <summary>Keep the exact reading's answer as it is: nearness had nothing to say.</summary>
        KeepExact,

        /// <summary>Keep the exact reading's answer, its size held to <see cref="Sure"/>: nearness found no body clearly,
        /// though that answer is the nearest.</summary>
        KeepDoubted,

        /// <summary>Leave the garment as it is.</summary>
        Refuse,
    }

    /// <summary>
    /// The outcome of a nearness reading. Fewer than <see cref="NearReached"/> of the points reaching any body says nothing,
    /// as does <see cref="NearVerdict.NoReading"/>, and the exact answer stands as it was; a clear body is refitted from.
    /// Otherwise nearness found no body clearly, and overrules the exact answer only where it clearly disagrees with it:
    /// that answer stands, doubted, when it is among the nearest or not clearly further than they are
    /// (<see cref="NearClearRatio"/>), and nothing is refitted when it is clearly further.
    /// </summary>
    /// <param name="closest">The mods sharing the nearest body (<see cref="ChooseNear"/>).</param>
    /// <param name="best">The nearest body's distance.</param>
    /// <param name="exactDistance">The exact answer's own distance, or null when it was not measured.</param>
    /// <example>
    /// The YAB top with no YAB installed: the exact answer, its ancestor Bibo+, lies 4.3 mm off and Lithe 3.4 — clearly
    /// further (3.4 / 4.3 = 0.79), so nothing is refitted. A garment cut from Tre and lightly sculpted: Tre 3.2 mm, Lithe
    /// 3.0 — no clearer than a guess (0.94), so Tre, the exact answer, stands.
    /// </example>
    internal static NearOutcome Settle(NearVerdict verdict, int reached, int points, IReadOnlyList<string> closest,
                                       string? exactChosen, float best = float.MaxValue, float? exactDistance = null)
    {
        if (verdict == NearVerdict.NoReading || TooFewReached(reached, points)) return NearOutcome.KeepExact;
        if (verdict == NearVerdict.Chosen) return NearOutcome.Refit;
        if (exactChosen == null || best > NearDoubtMax) return NearOutcome.Refuse;
        bool nearest = closest.Contains(exactChosen, StringComparer.OrdinalIgnoreCase);
        // Not clearly further only counts where nothing is close: two bodies about as close (Unclear) is the very case
        // where the runner-up must not be refitted from.
        bool notClearlyFurther = verdict == NearVerdict.NoneClose && exactDistance is { } d && best > d * NearClearRatio;
        return nearest || notClearlyFurther ? NearOutcome.KeepDoubted : NearOutcome.Refuse;
    }

    /// <summary>A reading that has already been doubted once: Likely or better, else Ambiguous, which is not refitted.</summary>
    internal static Confidence Sure(Confidence confidence)
        => confidence is Confidence.Exact or Confidence.Likely ? confidence : Confidence.Ambiguous;

    /// <summary>The mods in the garment's family from their sampled distances, in the order given.</summary>
    internal static List<string> NearFamily(IReadOnlyList<ModNear> sampled)
    {
        if (sampled.Count == 0) return [];
        float best = sampled.Min(m => m.Distance);
        return sampled.Where(m => m.Distance <= best + NearFamilyMargin).Select(m => m.Dir).ToList();
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── what a walk of the character asks for ──────────────────────────────

    /// <summary>
    /// Which mod draws a slot, and from which file: the identity a change of gear is noticed by.
    /// </summary>
    /// <param name="ModDir">The mod supplying the model, or null for the game's own gear.</param>
    /// <param name="Rel">The model's file relative to that mod, forward slashes; null for the game's own gear.</param>
    internal readonly record struct Worn(string GamePath, string? ModDir, string? Rel)
    {
        /// <summary>What has to change for the slot to count as newly equipped: the item, or the mod drawing it — but
        /// not which of the mod's own options does, which is the player's choice to make.</summary>
        public string Key => GamePath.ToLowerInvariant() + "|" + (ModDir ?? "").ToLowerInvariant();
    }

    /// <summary>What a queued piece's job does once the file is read again as it starts (see <see cref="Reresolved"/>).</summary>
    internal enum Reread
    {
        /// <summary>The file it was queued with: refit it.</summary>
        Proceed,

        /// <summary>Another file of the same mod — the group switched under it: refit what is drawn now.</summary>
        Retarget,

        /// <summary>A refit of ours is drawn now: nothing to do.</summary>
        OwnRefit,

        /// <summary>Another mod or the game's own: no longer this request's; the next look decides.</summary>
        Abandon,
    }

    /// <summary>
    /// A piece queued as <paramref name="queued"/>, read again as <paramref name="now"/> when its job starts. A job was
    /// queued by one look at everything worn, and the chest's job may have switched the group the legs are drawn from
    /// since — a legs job working on the file it was queued with would cut its refit from an option no longer on.
    /// </summary>
    internal static Reread Reresolved(Worn queued, Worn now)
    {
        if (!string.Equals(queued.ModDir ?? "", now.ModDir ?? "", StringComparison.OrdinalIgnoreCase)) return Reread.Abandon;
        if (RefitCore.Normal(queued.Rel ?? "") == RefitCore.Normal(now.Rel ?? "")) return Reread.Proceed;
        if (now.ModDir == null || now.Rel == null) return Reread.Abandon;
        return RefitCore.Normal(now.Rel).StartsWith(RefitCore.Normal(BodyRetargetWriter.Subfolder) + "/", StringComparison.Ordinal)
            ? Reread.OwnRefit
            : Reread.Retarget;
    }

    /// <summary>
    /// For the log, when a queued piece is <see cref="Reread.Abandon"/>ed: what draws it now, and the settings of the mod
    /// it was queued from and of the one that won it. Tells a mod that took the slot by priority from a group switch in
    /// the garment's own mod that left the piece out.
    /// </summary>
    /// <param name="garmentBefore">The garment mod's settings when the piece was queued, if read: what changed since is
    /// named.</param>
    internal static string AbandonDetail(Worn now, string? garmentDir,
                                         Interop.PenumbraBridge.ModSettingsSnapshot? garment,
                                         Interop.PenumbraBridge.ModSettingsSnapshot? winner,
                                         Interop.PenumbraBridge.ModSettingsSnapshot? garmentBefore = null)
    {
        var sb = new System.Text.StringBuilder("now drawn from ");
        sb.Append(now.ModDir == null ? "the game" : $"{now.ModDir} ({now.Rel})");
        if (garmentDir != null)
        {
            sb.Append("; ").Append(garmentDir).Append(": ").Append(Describe(garment));
            if (garmentBefore is { } before && garment is { } after)
                sb.Append("; since it was queued: ").Append(SettingsChange(before, after));
        }
        if (now.ModDir != null && !string.Equals(now.ModDir, garmentDir, StringComparison.OrdinalIgnoreCase))
            sb.Append("; ").Append(now.ModDir).Append(": ").Append(Describe(winner));
        return sb.ToString();

        static string Describe(Interop.PenumbraBridge.ModSettingsSnapshot? s)
        {
            if (s is not { } m) return "not set in this collection";
            var parts = new List<string> { m.Enabled ? "on" : "off", $"priority {m.Priority}" };
            if (m.Temporary) parts.Add("temporary");
            parts.AddRange(m.Options.OrderBy(o => o.Key, StringComparer.Ordinal)
                                    .Select(o => $"{o.Key} = {string.Join(" + ", o.Value)}"));
            return string.Join(", ", parts);
        }
    }

    /// <summary>What changed between two readings of one mod's settings, group by group; "unchanged" when nothing did.</summary>
    internal static string SettingsChange(Interop.PenumbraBridge.ModSettingsSnapshot before,
                                          Interop.PenumbraBridge.ModSettingsSnapshot after)
    {
        var parts = new List<string>();
        if (before.Enabled != after.Enabled) parts.Add(after.Enabled ? "switched on" : "switched off");
        if (before.Priority != after.Priority) parts.Add($"priority {before.Priority} → {after.Priority}");
        static string Ticked(Dictionary<string, List<string>> o, string g)
            => o.TryGetValue(g, out var t) && t.Count > 0 ? string.Join(" + ", t) : "(none)";
        foreach (string group in before.Options.Keys.Union(after.Options.Keys).OrderBy(g => g, StringComparer.Ordinal))
        {
            string was = Ticked(before.Options, group), now = Ticked(after.Options, group);
            if (was != now) parts.Add($"{group} {was} → {now}");
        }
        return parts.Count == 0 ? "unchanged" : string.Join(", ", parts);
    }

    // ── the redraw hold a batch takes (see RedrawHold) ──────────────────────

    /// <summary>The hold key of runner <paramref name="id"/>: one per runner, so an old runner can only let go its own.</summary>
    internal static string RunnerKey(int id) => $"auto-refit#{id}";

    /// <summary>
    /// The grace a runner's last word lets its key go with: none when it already let go at drain (with its grace),
    /// at once when it ended any other way — faulted, cancelled or disposed.
    /// </summary>
    internal static int? RunnerExitRelease(bool releasedAtDrain) => releasedAtDrain ? null : 0;

    /// <summary>
    /// Until when the walk leaves a piece this batch switched on alone: the redraw it waited for comes only when the
    /// batch lets go, so the quiet runs from then — never ends at it, which the walk after that redraw would miss.
    /// </summary>
    internal static long QuietUntil(long quietFromSwitch, long releaseAt, int quietMs)
        => Math.Max(quietFromSwitch, releaseAt + quietMs);

    /// <summary>Why a newly worn piece is not refitted; <see cref="Skip.None"/> when it is.</summary>
    internal enum Skip
    {
        None,

        /// <summary>Proteus's own managed mod: a composite, never gear an author made.</summary>
        Managed,

        /// <summary>A refit this feature or the Studio already made — refitting it again would compound.</summary>
        OwnRefit,

        /// <summary>The game's own gear, with its switch off.</summary>
        VanillaOff,

        /// <summary>A file served from outside the mods folder that is not the game's either.</summary>
        Unknown,

        /// <summary>The game's own accessory: jewellery, never a garment, whatever the switch for the game's gear.</summary>
        GameAccessory,
    }

    /// <summary>Whether a newly worn piece in <paramref name="slot"/> may be refitted: as any piece, but the game's own
    /// accessories never are.</summary>
    internal static Skip ShouldRefit(string slot, Worn worn, bool vanillaEnabled, bool resolvedOutsideMods)
        => IsAccessory(slot) && worn.ModDir == null && !resolvedOutsideMods
            ? Skip.GameAccessory
            : ShouldRefit(worn, vanillaEnabled, resolvedOutsideMods);

    /// <summary>Whether a newly worn piece may be refitted.</summary>
    /// <param name="resolvedOutsideMods">The file the game draws is not under Penumbra's mods folder and is not the
    /// game path itself — some other plugin's redirect.</param>
    internal static Skip ShouldRefit(Worn worn, bool vanillaEnabled, bool resolvedOutsideMods)
    {
        if (worn.ModDir == null)
            return resolvedOutsideMods ? Skip.Unknown : vanillaEnabled ? Skip.None : Skip.VanillaOff;
        if (string.Equals(worn.ModDir, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return Skip.Managed;
        if (worn.Rel != null && RefitCore.Normal(worn.Rel).StartsWith(
                RefitCore.Normal(BodyRetargetWriter.Subfolder) + "/", StringComparison.Ordinal))
            return Skip.OwnRefit;
        return Skip.None;
    }

    /// <summary>The key a mod group is remembered by in <see cref="AutoRefitPreference.SwitchedOn"/>.</summary>
    internal static string GroupKey(string modDir, string group) => modDir + "|" + group;

    /// <summary>
    /// Whether the player has chosen something other than the refit for this piece: a refit was switched on in this
    /// group before, and the group now holds none of the mod's refits. Picking the made mod's "Original" — the game's own
    /// model showing again — or one of the author's sizes reads as the piece newly put on, and switching the refit back
    /// on would undo the choice the moment Penumbra redraws. Never true for a group no refit was switched on in here: a
    /// refit made in another collection, worn here for the first time, still goes on.
    /// </summary>
    /// <param name="switchedOnBefore">The group is in <see cref="AutoRefitPreference.SwitchedOn"/>.</param>
    /// <param name="ticked">The group's selected options in this collection; null when the mod has no setting here.</param>
    /// <param name="refits">The options of the group that are refits (the mod's retarget record).</param>
    internal static bool ChoseOtherwise(bool switchedOnBefore, IEnumerable<string>? ticked, IEnumerable<string> refits)
    {
        if (!switchedOnBefore) return false;
        var ours = new HashSet<string>(refits, StringComparer.OrdinalIgnoreCase);
        return !(ticked ?? []).Any(ours.Contains);
    }

    /// <summary>
    /// The slots whose piece is newly worn since <paramref name="before"/>, with a missing slot counting as bare. Empty
    /// when <paramref name="before"/> is null — the first look after loading, enabling or a collection change is a
    /// baseline, so logging in refits nothing.
    /// </summary>
    internal static List<string> Changed(IReadOnlyDictionary<string, Worn>? before, IReadOnlyDictionary<string, Worn> now)
    {
        var changed = new List<string>();
        if (before == null) return changed;
        foreach (string slot in WornSlots)
        {
            if (!now.TryGetValue(slot, out var worn)) continue;
            if (before.TryGetValue(slot, out var was) && was.Key == worn.Key) continue;
            changed.Add(slot);
        }
        return changed;
    }
}
