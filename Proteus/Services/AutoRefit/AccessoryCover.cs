using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// Whether a piece worn on an accessory slot is a garment — a jacket on a necklace, sleeves on a bracelet — and which
/// part of the body it covers, for the automatic refit (see <see cref="AutoRefitWatcher"/>'s accessory job). Static and
/// free of IPC, as <see cref="KeepEnds"/> is.
/// <para/>
/// An accessory never hides the body, so authors ship accessory garments with no body skin at all: there is no skin to
/// read a size off, only cloth over the body. How much of the body lies under that cloth is measured from the BODY out —
/// each of its skin vertices with cloth within <see cref="BodySizeMatch.ClothReach"/> — so a choker round the neck
/// covers a sliver of the chest and a jacket most of it. Measured the other way, from the cloth in, every vertex of a
/// choker is near the neck and it would read as covering everything.
/// </summary>
internal static class AccessoryCover
{
    /// <summary>
    /// The share of a chest or legs body's skin under the cloth for a piece its author sizes with a chest or legs piece
    /// in the same option (see <see cref="SiblingPiece"/>) to count as covering it (5%). The sibling is what says it is a
    /// garment: Scarlet's piece on the bracelet slot, sized with its top in every "Top Size" option, covers 10.3% of
    /// Neolithe's SFW M chest.
    /// </summary>
    internal const float WithSibling = 0.05f;

    /// <summary>
    /// The share for a piece with no such sibling (40%): only a garment over the torso, never jewellery. Cover alone does
    /// not tell them apart lower down — Covered In Ice's necklace covers 22.9% of the same chest, more than Scarlet's
    /// garment — so a piece with nothing else to go by has to cover most of it.
    /// </summary>
    internal const float Alone = 0.40f;

    /// <summary>How much of the chosen chest and legs body the cloth covers.</summary>
    internal sealed record Cover(float Chest, float Legs)
    {
        /// <summary>Less than any piece is refitted for, sibling or not: jewellery, read so before and not read again.</summary>
        public bool CoversNothing => Chest < WithSibling && Legs < WithSibling;

        /// <summary>Every part covered enough to refit the piece as, the larger first — a long coat covers both: each by
        /// <see cref="WithSibling"/> where the author sizes the piece with that part, by <see cref="Alone"/> where not.</summary>
        /// <param name="sizedWith">Whether the piece's options ship a piece of that part beside it.</param>
        public IReadOnlyList<string> Covered(Func<string, bool> sizedWith)
            => new[] { ("_top", Chest), ("_dwn", Legs) }.Where(p => p.Item2 >= (sizedWith(p.Item1) ? WithSibling : Alone))
                                                         .OrderByDescending(p => p.Item2)
                                                         .Select(p => p.Item1).ToList();
    }

    /// <summary>The share of <paramref name="body"/>'s skin vertices with the accessory's cloth within reach.</summary>
    internal static float Share(ModelParts accessory, ModelParts body)
    {
        var cloth = BodySurface.OfCloth(accessory, BodySurface.CellFor(0.01f));
        if (cloth.IsEmpty) return 0f;
        var skin = new BodySurface(body, BodySurface.CellFor(0.01f)).SkinVertices;
        if (skin.Count == 0) return 0f;
        int covered = 0;
        foreach (int v in skin)
        {
            var p = new Vector3(body.Positions[v * 3], body.Positions[v * 3 + 1], body.Positions[v * 3 + 2]);
            if (cloth.Nearest(p, BodySizeMatch.ClothReach, out _)) covered++;
        }
        return (float)covered / skin.Count;
    }

    /// <summary>Why the piece's own part has no size to go onto: none chosen, or none at the piece's race.</summary>
    internal enum NoSize
    {
        Chosen,
        ForRace,
    }

    /// <summary>
    /// The part the piece is refitted as (the first of <paramref name="covered"/>) when it has no size to go onto — said
    /// to the player, nothing refitted — and why; null when it has one, or covers nothing.
    /// </summary>
    /// <param name="targetFor">The size chosen per part at the piece's race: the chest for "_top", the legs for "_dwn".</param>
    /// <param name="chosen">Whether the player chose a size of a part at all: chosen but not found at the piece's race
    /// is the race's lack, as a top's own refit says it.</param>
    internal static (string Slot, NoSize Why)? WithoutSize(IReadOnlyList<string> covered, Func<string, BodyOption?> targetFor,
                                                           Func<string, bool> chosen)
        => covered.Count > 0 && targetFor(covered[0]) == null
            ? (covered[0], chosen(covered[0]) ? NoSize.ForRace : NoSize.Chosen)
            : null;

    /// <summary>
    /// The option a refit of an accessory sized together with other pieces in one of the author's single-choice groups is
    /// saved as: the other pieces' automatic refit it joins (see <see cref="RefitCore.JoinOption"/>), or its own made
    /// before — or none, null, when neither is there: a new option of its own would switch theirs off the moment it went
    /// on. Outside such a group, its own.
    /// </summary>
    /// <param name="joined">What <see cref="RefitCore.JoinOption"/> answered for <paramref name="proposed"/>.</param>
    /// <param name="existing">The piece's own option is already saved for it.</param>
    internal static string? SharedOption(bool shared, string proposed, string joined, bool existing)
        => !shared ? proposed : joined != proposed || existing ? joined : null;

    /// <summary>A source's sizes: the game's own body's for a piece made on it, an installed body mod's otherwise.</summary>
    internal static BodySizeCatalog? CatalogOf(string dir, BodySizeCatalog gameBody, Func<string, BodySizeCatalog?> installed)
        => dir == VanillaBodyCatalog.Key ? gameBody : installed(dir);

    /// <summary>
    /// Whether a cached cover reading already says the piece is jewellery — covering too little of any part, given what
    /// its options size it with — so a piece put on again is let be before anything is gathered.
    /// </summary>
    internal static bool KnownJewellery(DetectionCache.Entry? hit, Func<string, bool> sizedWith)
        => hit != null && FromCache(hit) is { } cover && cover.Covered(sizedWith).Count == 0;

    /// <summary>
    /// The size of a part the cover is measured against: the one chosen, or — none chosen yet — the body's first, so a
    /// jacket over a chest with no chest size chosen still reads as covering it, and says so, rather than as jewellery.
    /// </summary>
    internal static BodyOption? MeasuredOn(BodyOption? chosen, IReadOnlyList<BodyOption> sizes)
        => chosen ?? sizes.FirstOrDefault();

    /// <summary>What the author's own sizes say about an accessory (see <see cref="AuthorsBodyOption"/>).</summary>
    internal enum AuthorGate
    {
        /// <summary>No author's size for a body is drawn: refitted as any piece.</summary>
        None,

        /// <summary>The author's size drawn is the one chosen: nothing to do, said only in the log.</summary>
        AlreadyFits,

        /// <summary>The author's size drawn is another: left to the player to pick, and said.</summary>
        LeftToAuthor,
    }

    /// <summary>
    /// What to do with an accessory whose drawn option is <paramref name="authors"/>: nothing when null; quietly nothing
    /// when the chest or legs piece shipped beside it reads as exactly the size chosen; otherwise leave it to the author's
    /// sizes and say so.
    /// </summary>
    internal static AuthorGate ForAuthor(string? authors, AutoRefitDecisions.Pick? sibling, string targetDir, BodyOption target)
        => authors == null ? AuthorGate.None
         : sibling != null && AutoRefitDecisions.SameFile(sibling.Dir, sibling.Option, targetDir, target) ? AuthorGate.AlreadyFits
         : AuthorGate.LeftToAuthor;

    /// <summary>
    /// The author's option drawn for the accessory at <paramref name="gamePath"/> in a single-choice group of theirs that
    /// sizes it, when that option names a body (see <see cref="AutoRefitDecisions.NamesABody"/>) — read from what the
    /// collection has selected NOW, after the body pieces' refits switched their groups, never from which option first
    /// serves the file: Scarlet's jacket file is served by its "Neolithe S", "Neolithe M" and Proteus's own refit option
    /// alike. Null when the option drawn is one of Proteus's refits, names no body, or no such group sizes the piece.
    /// </summary>
    /// <param name="ticked">The collection's selection in a group of the mod, or null for none.</param>
    /// <param name="bodyNames">Every installed body mod's name and folder.</param>
    /// <param name="sizeWords">Every word of every installed body's size labels and sections.</param>
    internal static string? AuthorsBodyOption(string gamePath, IReadOnlyList<PenumbraModMeta.Redirect> redirects,
                                              BodyRetargetWriter.Record? record, IReadOnlyList<string> singleGroups,
                                              Func<string, IReadOnlyList<string>?> ticked, IEnumerable<string> bodyNames,
                                              IReadOnlySet<string> sizeWords)
    {
        var bodies = bodyNames.ToList();
        foreach (string group in singleGroups)
        {
            if (record?.OwnGroups.Contains(group, StringComparer.OrdinalIgnoreCase) == true) continue;
            if (ticked(group) is not { Count: > 0 } selected) continue;
            string drawn = selected[0];
            // A group that sizes THIS piece, through the option drawn — not another accessory of the same mod.
            if (!redirects.Any(d => string.Equals(d.Source, group + " / " + drawn, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(d.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (record?.Options.Any(e => string.Equals(record.GroupOf(e), group, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(e.Name, drawn, StringComparison.OrdinalIgnoreCase)) == true)
                return null;
            return AutoRefitDecisions.NamesABody(drawn, bodies, sizeWords) ? drawn : null;
        }
        return null;
    }

    /// <summary>What each part the piece covers is refitted from, and what was left out.</summary>
    /// <param name="Sources">Per part refitted, what it was made on.</param>
    /// <param name="LeftOut">Parts covered but not refitted: no size, or no source.</param>
    /// <param name="Refused">Why the piece's own part has no source — the piece is then left as it is; null when it has one.</param>
    internal sealed record Parts(IReadOnlyDictionary<string, AutoRefitDecisions.Pick> Sources, IReadOnlyList<string> LeftOut,
                                 Refusal? Refused);

    /// <summary>
    /// What each part <paramref name="covered"/> is refitted from: the chest or legs piece shipped beside the accessory in
    /// its own options when there is one and they agree, else the one worn beneath it — and either only when the cloth
    /// clears it. The piece's own part (the first) without a source refuses the whole piece; another is left out.
    /// </summary>
    /// <param name="targetFor">The size chosen per part.</param>
    /// <param name="sibling">The siblings' agreed reading per part, or null (none, or they did not agree).</param>
    /// <param name="beneath">What the piece worn beneath says, per part — asked only without a sibling.</param>
    /// <param name="clears">Whether the cloth clears a source (see <see cref="ClearsSource"/>).</param>
    internal static Parts SourcesFor(IReadOnlyList<string> covered, Func<string, BodyOption?> targetFor,
                                     Func<string, AutoRefitDecisions.Pick?> sibling, Func<string, Beneath> beneath,
                                     Func<AutoRefitDecisions.Pick, bool> clears)
    {
        var sources = new Dictionary<string, AutoRefitDecisions.Pick>(StringComparer.Ordinal);
        var leftOut = new List<string>();
        foreach (string slot in covered)
        {
            bool own = slot == covered[0];
            if (targetFor(slot) == null)
            {
                if (own) return new Parts(sources, leftOut, Refusal.Unsure);   // said before this, by WithoutSize
                leftOut.Add(slot);
                continue;
            }
            AutoRefitDecisions.Pick? pick;
            Refusal? why = null;
            if (sibling(slot) is { } read) pick = read;
            else
            {
                var under = beneath(slot);
                (pick, why) = (under.Source, under.Why);
            }
            if (pick != null && !clears(pick)) (pick, why) = (null, Refusal.OffBody);
            if (pick != null)
            {
                sources[slot] = pick;
                continue;
            }
            if (own) return new Parts(sources, leftOut, why ?? Refusal.Unsure);
            leftOut.Add(slot);
        }
        return new Parts(sources, leftOut, null);
    }

    /// <summary>How much of the chosen chest and legs the accessory covers; a part with no body chosen covers nothing.</summary>
    internal static Cover Read(ModelParts accessory, ModelParts? chest, ModelParts? legs)
        => new(chest == null ? 0f : Share(accessory, chest), legs == null ? 0f : Share(accessory, legs));

    /// <summary>
    /// The detection cache's key for a cover reading: the accessory's content and race, and the very bodies it was
    /// measured against — the target body and its chosen chest and legs, by file. Another chest chosen measures again.
    /// </summary>
    internal static string Key(byte[] bytes, string targetDir, string? chestRel, string? legsRel, string race)
        => "cover|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
         + $"|{targetDir.ToLowerInvariant()}|{RefitCore.Normal(chestRel ?? "")}|{RefitCore.Normal(legsRel ?? "")}|{race}";

    private const string ChestKey = "_top", LegsKey = "_dwn";

    /// <summary>A cover reading as the detection cache keeps it.</summary>
    internal static DetectionCache.Entry ToCache(Cover cover)
        => new("", "", cover.CoversNothing ? BodySizeMatch.Confidence.Ambiguous : BodySizeMatch.Confidence.Exact,
               false, new Dictionary<string, string>
               {
                   [ChestKey] = cover.Chest.ToString("R", CultureInfo.InvariantCulture),
                   [LegsKey] = cover.Legs.ToString("R", CultureInfo.InvariantCulture),
               });

    /// <summary>A cached cover reading back, or null when the entry is not one.</summary>
    internal static Cover? FromCache(DetectionCache.Entry hit)
        => hit.Others.TryGetValue(ChestKey, out string? chest) && hit.Others.TryGetValue(LegsKey, out string? legs)
           && float.TryParse(chest, NumberStyles.Float, CultureInfo.InvariantCulture, out float c)
           && float.TryParse(legs, NumberStyles.Float, CultureInfo.InvariantCulture, out float l)
            ? new Cover(c, l)
            : null;

    /// <summary>
    /// Whether the accessory's cloth sits outside the body it is taken as made on, as cloth made on it would: no more of
    /// the cloth near it inside it than <see cref="KeepEnds.SiblingInsideAtMost"/> allows an author's tucks. A body the
    /// cloth sits inside is not the one it was made on, and refitting from it would pull the cloth into the new body.
    /// True when too little cloth is near to say.
    /// </summary>
    internal static bool ClearsSource(ModelParts accessory, string sourceBodyPath)
        => BodySizeMatch.ClothAgainst(accessory, sourceBodyPath) is not { } on || on.Near < BodySizeMatch.MinEvidence
           || on.Inside <= on.Near * KeepEnds.SiblingInsideAtMost;

    /// <summary>Why an accessory covering the body is left as it is, for the status line.</summary>
    internal enum Refusal
    {
        /// <summary>Its own option ships no chest or legs piece, and nothing readable is worn there.</summary>
        NothingBeneath,

        /// <summary>The piece worn beneath is the game's own: modded cloth is almost never made on the game's body.</summary>
        GameBeneath,

        /// <summary>The piece worn beneath is not, and will not be, on the body chosen.</summary>
        NotOnTarget,

        /// <summary>The piece worn beneath was refitted by an earlier version, which did not record what from.</summary>
        OldRecord,

        /// <summary>What it was made on could not be told clearly.</summary>
        Unsure,

        /// <summary>Its cloth sits inside the body it would be refitted from.</summary>
        OffBody,
    }

    /// <summary>What the piece worn beneath says, as the accessory job reads it.</summary>
    /// <param name="Source">The body and size it was made on, when it says.</param>
    internal sealed record Beneath(AutoRefitDecisions.Pick? Source, Refusal? Why);

    /// <summary>
    /// The source an accessory is refitted from, read off the chest or legs piece worn beneath it: that piece's own body
    /// and size, never a guess from the accessory's cloth (the tightest size cloth clears is how a loose jacket read a
    /// bust too small). Only a piece that is, or is about to be, on the body chosen: an accessory refitted over a top that
    /// stays on another body would no longer fit it.
    /// </summary>
    /// <param name="gameOwn">The piece beneath is the game's own.</param>
    /// <param name="record">Its refit's record entry, when it is drawn from one of Proteus's refits; null otherwise.</param>
    /// <param name="chosen">The target body's file chosen for this part now.</param>
    /// <param name="outcome">What its own refit came to in this queue, when it ran; null when it did not.</param>
    /// <param name="read">Its own reading, and whether that already is the size chosen — read only when needed.</param>
    /// <param name="fromRecord">The body option a record's source names, or null when that body no longer has it.</param>
    internal static Beneath FromBeneath(bool gameOwn, BodyRetargetWriter.Entry? record, string targetDir, string slot,
                                        string? chosen, PieceOutcome? outcome,
                                        Func<(AutoRefitDecisions.Pick? Pick, bool OnTarget)> read,
                                        Func<string, string, AutoRefitDecisions.Pick?> fromRecord)
    {
        if (gameOwn) return new Beneath(null, Refusal.GameBeneath);
        if (record != null)
        {
            if (!string.Equals(record.BodyMod, targetDir, StringComparison.OrdinalIgnoreCase))
                return new Beneath(null, Refusal.NotOnTarget);
            if (record.SourceDir == null || record.SourceRel == null || record.Targets == null)
                return new Beneath(null, Refusal.OldRecord);
            if (chosen == null || !record.Targets.TryGetValue(slot, out string? onto)
                || RefitCore.Normal(onto) != RefitCore.Normal(chosen))
                return new Beneath(null, Refusal.NotOnTarget);
            return fromRecord(record.SourceDir, record.SourceRel) is { } pick
                ? new Beneath(pick, null)
                : new Beneath(null, Refusal.Unsure);
        }
        if (outcome != null)
            return !outcome.OnTarget ? new Beneath(null, Refusal.NotOnTarget)
                 : outcome.Source != null ? new Beneath(outcome.Source, null)
                 : new Beneath(null, Refusal.Unsure);
        var (own, onTarget) = read();
        if (own == null || !AutoRefitDecisions.Proceed(own.Confidence)) return new Beneath(null, Refusal.Unsure);
        return onTarget ? new Beneath(own, null) : new Beneath(null, Refusal.NotOnTarget);
    }
}

/// <summary>
/// What a body piece's own refit came to in this queue: whether it is now on the body chosen, and what it was made on.
/// A refit saved, put back on, already fitting or switched to the author's own size is on it; one left as it is,
/// failed or cancelled is not.
/// </summary>
/// <param name="Source">What it was made on; null when that is not known (a refit put back from an old record).</param>
/// <param name="File">The file it is drawn from now, in its mod — the refit saved or put back on, or the author's own
/// file that already fits — for a garment worn over it to be kept outside (see <see cref="OutfitPartners"/>). Null when
/// that is not known (the author's other size switched on).</param>
internal sealed record PieceOutcome(bool OnTarget, AutoRefitDecisions.Pick? Source, string? File = null);
