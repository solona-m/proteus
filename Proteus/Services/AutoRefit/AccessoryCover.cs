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

    /// <summary>The part the piece is refitted as (the first of <paramref name="covered"/>) when no size is chosen for it —
    /// said to the player, nothing refitted — or null when it has one, or covers nothing.</summary>
    /// <param name="targetFor">The size chosen per part: the chest for "_top", the legs for "_dwn".</param>
    internal static string? WithoutSize(IReadOnlyList<string> covered, Func<string, BodyOption?> targetFor)
        => covered.Count > 0 && targetFor(covered[0]) == null ? covered[0] : null;

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
internal sealed record PieceOutcome(bool OnTarget, AutoRefitDecisions.Pick? Source);
