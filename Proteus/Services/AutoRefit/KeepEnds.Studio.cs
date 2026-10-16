using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Proteus.Localization;
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

    /// <summary>What the Body size tool plans for a shoe or glove with its end kept: each "made for" size of the limb.</summary>
    /// <param name="Pieces">The hard pieces kept in shape now (<see cref="RefitCore.ShapePieces"/>): the end is read
    /// again from them, since they decide which pieces the end may not split.</param>
    /// <param name="SameBodyMod">Source and target are one body mod, where a limb of the same shape fits already.</param>
    /// <param name="FromCloth">The limb was read from how its cloth sits, not its skin; sizes are compared the same way.</param>
    /// <param name="Held">The parts the user unticked, as vertices: held with the end (ND-7).</param>
    /// <param name="Shared">The other chosen slots' pairs, refitted along.</param>
    /// <param name="Masks">Each slot's IMC masks, source and target, read on the framework thread.</param>
    /// <param name="SlotName">A slot's name as a refusal words it.</param>
    /// <param name="PieceNames">Each of <paramref name="Pieces"/>' labels, in the same order: how a piece the refit let go
    /// is named.</param>
    /// <param name="CarryPiercings">Bring the new body's piercings in with the limb's skin: the panel's box, with the skin
    /// replaced (see <see cref="RetargetInputs.CarryPiercings"/>).</param>
    internal sealed record StudioPlanInput(
        ModelParts Garment, byte[] Bytes, string Slot, IReadOnlyList<IReadOnlyCollection<int>> Pieces,
        BodySizeCatalog Source, BodySizeCatalog Target, string Race, bool Male,
        BodyOption LimbFrom, IReadOnlyList<BodyOption> LimbTargets,
        bool SameBodyMod, bool FromCloth, bool ReplaceSkin, bool Across, bool ClearBody,
        IReadOnlySet<int>? Held, IReadOnlyList<BodyRetarget.SlotPair>? Shared,
        Func<string, (ushort? Source, ushort? Target)> Masks, Func<string, string> SlotName, string ModelRel,
        IReadOnlyList<string>? PieceNames = null, bool CarryPiercings = false);

    /// <summary>
    /// The plans, one per target; or none, with the refusal to show (<paramref name="Error"/>) or a notice that there is
    /// nothing to make (<paramref name="Notice"/>).
    /// </summary>
    /// <param name="ShapeNames">The labels of the pieces the plans kept in shape, by the plans' piece index — the end's
    /// own left out (see <see cref="Reading.FreeNames"/>).</param>
    internal sealed record StudioPlanned(List<(BodyOption To, BodyRetarget.Planned Planned)>? Planned, string Error,
                                         string Notice = "", IReadOnlyList<string>? ShapeNames = null);

    /// <summary>
    /// Plan a shoe's leg or a glove's arm refitted onto each of <see cref="StudioPlanInput.LimbTargets"/>, the foot or hand
    /// held where its author put it and following along for its shape, its skin then drawn like the new body's. Worker
    /// thread; the panel's Preview and its tests both come through here.
    /// </summary>
    /// <param name="finish">What the panel does to each plan before it is shown — a bake's preview and skin; given the
    /// plan and its target body's path.</param>
    /// <param name="done">Told as each plan is made, for the progress bar.</param>
    internal static StudioPlanned StudioPlan(StudioPlanInput input,
                                             Func<BodyRetarget.Planned, string, BodyRetarget.Planned> finish,
                                             UVRemapService? uvRemap, IPluginLog log, Action? done = null)
    {
        var ps = Strings.Parts;
        if (Read(input.Slot, input.Garment, input.Bytes, input.Pieces, input.PieceNames) is not { } reading)
            return new StudioPlanned(null, ps.RetargetEndsUnreadable);
        var source = input.Source;
        var target = input.Target;
        var bones = new HashSet<string>(SecondSkinWriter.Parse(input.Bytes).BoneNames, StringComparer.Ordinal);
        string limbSource = source.PathOf(input.LimbFrom);
        var results = new List<(BodyOption, BodyRetarget.Planned)>();
        BodyOption? fits = null;

        foreach (var option in input.LimbTargets)
        {
            string targetPath = target.PathOf(option);
            // Arms, and often the lower legs, are one shape across a body mod's sizes: "refitted" onto another size of
            // the same, the piece would be exactly where it is, and saved for nothing.
            if (input.SameBodyMod && SameShape(reading.LimbView, input.LimbFrom, option, source, bones, input.FromCloth))
            {
                fits ??= option;
                continue;
            }
            var (endFrom, endTo) = EndPair(source, target, reading.Slot, input.Race, input.LimbFrom, option);
            if (BuildPairs(reading, limbSource, targetPath, endFrom == null ? null : source.PathOf(endFrom),
                           endTo == null ? null : target.PathOf(endTo), input.Male, input.Masks, input.SlotName,
                           uvRemap,
                           why => log.Information("[Proteus] retarget: {0} refitted without the {1}' shape: {2}",
                                                  input.ModelRel, reading.Slot, why),
                           out var limbPair, out var endPair) is { } refusal)
                return new StudioPlanned(null, input.LimbTargets.Count > 1 ? $"{option.Label}: {refusal}" : refusal);

            var plan = Plan(input.Garment, input.Bytes, reading, limbPair, endPair, input.ReplaceSkin, input.Across,
                            input.ClearBody, input.Held, input.Shared,
                            line => log.Debug("[Proteus] body size: {0}", line),
                            line => log.Warning("[Proteus] body size: {0}", line),
                            carryPiercings: input.CarryPiercings);
            plan = RefitCore.MatchSkinToBody(plan, limbSource, targetPath, uvRemap, input.Male, log);
            var (seamEdges, stretched) = SeamStretch(input.Garment, reading.Ends, plan);
            log.Information("[Proteus] retarget: {0} kept its {1}: moved up to {2:F1} mm, the seam stretched up to {3:F1} "
                          + "mm over {4} edges", input.ModelRel, reading.Slot, plan.Report.WorstMove * 1000f,
                            stretched * 1000f, seamEdges);
            if (stretched > SeamWarn)
                log.Warning("[Proteus] retarget: {0}'s seam stretched by {1:F1} mm — the kept end and the new body differ "
                          + "there; look at it in game", input.ModelRel, stretched * 1000f);
            results.Add((option, finish(plan, targetPath)));
            done?.Invoke();
        }
        if (results.Count == 0 && fits != null)
            return new StudioPlanned(null, "", string.Format(ps.RetargetAlreadyFitsFmt, fits.Label));
        return new StudioPlanned(results, "", ShapeNames: reading.FreeNames);
    }

    /// <summary>
    /// A shoe or glove reaching no leg or arm, in the Body size tool: its shape kept, its skin drawn like the new body's
    /// <paramref name="end"/>. Nothing to make when that changes nothing and it is not being baked — a bake is itself a
    /// change worth saving. Worker thread.
    /// </summary>
    /// <param name="ownEndPath">The feet or hands its skin was drawn for, in the source body mod.</param>
    internal static StudioPlanned StudioSkinOnly(byte[] bytes, string ownEndPath, BodyOption end, BodySizeCatalog target,
                                                 bool male, bool baking,
                                                 Func<BodyRetarget.Planned, string, BodyRetarget.Planned> finish,
                                                 UVRemapService? uvRemap, IPluginLog log)
    {
        string targetPath = target.PathOf(end);
        var model = RefitCore.MatchSkinToBody(bytes, ownEndPath, targetPath, uvRemap, male, log);
        if (!RetargetInputs.SkinOnlyChanges(baking, bytes, model))
            return new StudioPlanned(null, "", Strings.Parts.RetargetSkinAlreadyMatches);
        return new StudioPlanned([(end, finish(SkinOnlyPlanned(model), targetPath))], "");
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
