using System.Collections.Generic;
using System.Linq;
using Confidence = Proteus.Services.BodySizeMatch.Confidence;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// The Studio's half of keeping a shoe's foot or a glove's hand: the same reading and plan as the automatic refit, read
/// against the one body mod the user chose rather than every installed one.
/// </summary>
internal static partial class KeepEnds
{
    /// <summary>What the Studio read a shoe or glove as, against the body mod chosen as its source.</summary>
    /// <param name="Shown">The ranking shown under the dropdown — the very one <paramref name="Pick"/> came from, so the
    /// line beneath a choice always speaks for it. Null when nothing was ranked.</param>
    /// <param name="Pick">The best guess, filled in as the Studio does for every garment however sure it is: the limb's
    /// size for <see cref="Route.Skin"/> and <see cref="Route.Cloth"/>, the piece's own feet or hands for
    /// <see cref="Route.SkinOnly"/>. Null for <see cref="Route.NoSkin"/>.</param>
    internal sealed record StudioReading(Route Route, BodySizeMatch.Ranking? Shown, BodyOption? Pick);

    /// <summary>
    /// Read a shoe's leg or a glove's arm against <paramref name="source"/>, the body mod the user says it was made on.
    /// <para/>
    /// <see cref="Find"/>'s rules for which way a piece is read, without its unattended gates: the automatic refit has to
    /// find which body a piece was made on and refuses a weak reading of its foot, which for a heeled shoe is exactly the
    /// weak one. Here the user has named the body, so nothing is gated, and the best guess is always filled in.
    /// The game's own gear is read as the automatic refit reads it, off the game's own body (<see cref="VanillaPick"/>),
    /// so it routes the same way in both.
    /// </summary>
    internal static StudioReading StudioLimb(Reading reading, ModelParts garment, IReadOnlySet<string> bones, string race,
                                             string sourceDir, BodySizeCatalog source)
    {
        if (sourceDir == VanillaBodyCatalog.Key)
        {
            var found = Find(reading, garment, bones, race, (_, slot) => VanillaPick(source, slot, race), _ => source);
            return new StudioReading(found.Route, found.Pick is { } p ? Shown(p) : null, found.Pick?.Option);
        }

        // No skin anywhere: there is no body to read a piece off, as the automatic refit says too. A size picked by hand
        // still refits its leg (see RetargetInputs).
        if (BodySizeMatch.SkinPoints(garment).Count == 0) return new StudioReading(Route.NoSkin, null, null);

        var options = source.For(reading.Limb, race);
        bool limbSkin = BodySizeMatch.SkinPoints(reading.LimbView).Count > 0;
        if (limbSkin && options.Count > 0
            && BodySizeMatch.Rank(reading.LimbView, options, source.PathOf, bones) is { Best: { } bySkin } skinRanking
            && skinRanking.Confidence is not (Confidence.NoBodyMesh or Confidence.TooLittle))
            return new StudioReading(Route.Skin, skinRanking, bySkin.Option);

        // Too little leg skin: how the shaft's cloth sits. One size of legs is the answer once the piece reaches them at all.
        var cloth = options.Count > 0 ? BodySizeMatch.RankByCloth(reading.LimbView, options, source.PathOf) : null;
        if (options.Count == 1 && (limbSkin || cloth != null))
            return new StudioReading(Route.Cloth, cloth ?? Shown(new AutoRefitDecisions.Pick(sourceDir, options[0],
                                                                                              Confidence.Likely, true)),
                                     options[0]);
        if (options.Count > 1 && cloth is { Best: { } byCloth }
            && cloth.Confidence is not (Confidence.NoBodyMesh or Confidence.TooLittle))
            return new StudioReading(Route.Cloth, cloth, byCloth.Option);

        // Nothing of the leg to read: a plain shoe, its skin redrawn. Its own feet are the ones its skin was drawn for.
        var own = source.For(reading.Slot, race);
        var ownRanking = own.Count > 0 ? BodySizeMatch.Rank(garment, own, source.PathOf, bones) : null;
        return new StudioReading(Route.SkinOnly, ownRanking,
                                 ownRanking?.Best?.Option ?? (own.Count > 0 ? own[0] : null));
    }

    /// <summary>A pick as a one-line ranking, for the line under a dropdown where nothing was ranked.</summary>
    private static BodySizeMatch.Ranking Shown(AutoRefitDecisions.Pick pick)
        => new(pick.Confidence, [new BodySizeMatch.Score(pick.Option, 1f, 0f)], pick.FromCloth);

    /// <summary>
    /// The feet or hands that go with a limb's two sizes, source and target, for their shape (see <see cref="Plan"/>):
    /// what <see cref="Decide"/> pairs for the automatic refit. Both null when either body has none in the slot.
    /// </summary>
    internal static (BodyOption? From, BodyOption? To) EndPair(BodySizeCatalog source, BodySizeCatalog target,
                                                               string endSlot, string race, BodyOption limbFrom,
                                                               BodyOption limbTo)
    {
        var from = AutoRefitDecisions.Companion(source.For(endSlot, race), limbFrom, null);
        var to = from == null ? null : AutoRefitDecisions.Companion(target.For(endSlot, race), limbTo, null);
        return to == null ? (null, null) : (from, to);
    }

    /// <summary>
    /// A skin-only redraw as a plan, so it previews and saves like any refit: nothing moved, the model the redraw gave.
    /// </summary>
    internal static BodyRetarget.Planned SkinOnlyPlanned(byte[] model)
    {
        var parts = ModelPartReader.Read(model);
        int n = parts == null ? 0 : parts.Positions.Length / 3;
        var edit = new RetargetEdit(parts?.MeshSpans ?? [], new Vec3[n], new Vec3[n]);
        return new BodyRetarget.Planned(edit, model,
                                        new BodyRetarget.Report(0, 0, 0, 0, 0, 0f, 0f, 0, false));
    }
}
