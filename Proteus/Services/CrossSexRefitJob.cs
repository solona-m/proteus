using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// A refit for the other sex, headless (see <see cref="CrossSexRefit"/>): the order the Body size panel takes it in —
/// the wearer read from files, the garment bent, the bodies it was made on bent the same way, each size matched through
/// the bibo↔TBSE maps, refitted and finished as the save would. Shared by the CLI and the real-data gates, so what is
/// measured is what the CLI writes.
/// </summary>
internal static class CrossSexRefitJob
{
    /// <param name="Garment">The model as drawn.</param>
    /// <param name="GamePath">Where it is drawn from (its race and slot are read off it).</param>
    /// <param name="To">The other sex's race to refit it for (101 or 201).</param>
    /// <param name="MadeFor">The body mod it was made for (bodies of the drawn race).</param>
    /// <param name="FromPick">Which of its bodies it was made on; null only when it has one there.</param>
    /// <param name="Onto">The body mod to refit onto (bodies of <paramref name="To"/>).</param>
    /// <param name="TargetPick">Which of its sizes to make; null makes all.</param>
    /// <param name="Wearer">What the wearer of <paramref name="To"/> is built from (<see cref="OtherRaceWearer.Gather"/>).</param>
    /// <param name="LiftUnderbust">Overrides <see cref="CrossSexRefit.LiftsUnderbust"/>, for the measurement that decides it.</param>
    /// <param name="Nude">For a target, the same body's nude smallclothes, fitted instead when the target matches too little
    /// of the made-for body and they enough (<see cref="CrossSexRefit.TakesNude"/>). The size set's only: null fits every
    /// target as it was picked.</param>
    internal sealed record Inputs(
        byte[] Garment, string GamePath, ushort To,
        BodySizeCatalog MadeFor, Func<BodyOption, bool>? FromPick,
        BodySizeCatalog Onto, Func<BodyOption, bool>? TargetPick,
        OtherRaceWearer.Inputs Wearer, UVRemapService UvRemap, IPluginLog Log,
        bool PushOut = true, bool CutHidden = true, bool CarryPiercings = false, bool? LiftUnderbust = null,
        Func<BodyOption, BodyOption?>? Nude = null);

    /// <summary>One size made, or refused.</summary>
    /// <param name="Target">The body it was fitted to: the one picked, or its nude smallclothes (<see cref="Asked"/>).</param>
    /// <param name="Folds">Cloth faces the refit turned over (<see cref="CrossSexRefit.ClothFolded(byte[], BodyRetarget.Planned, Action{string}?)"/>).</param>
    /// <param name="Landed">How the two bodies' skin landed on each other through the maps.</param>
    /// <param name="Asked">The body picked, when it was fitted to that body's nude smallclothes instead; null otherwise.</param>
    /// <param name="AskedLanded">How the skin landed on the body picked, then.</param>
    internal sealed record Size(BodyOption Target, string TargetPath, BodyRetarget.Planned? Plan,
                                BodyRetarget.Planned? Saved, byte[]? Preview, int Folds,
                                UvAtlasCorrespondence.Stats? Landed, string Refusal, BodyOption? Asked = null,
                                UvAtlasCorrespondence.Stats? AskedLanded = null)
    {
        /// <summary>Whether it may be saved: made, and enough of the body matched (<see cref="CrossSexRefit.CoverageAllowed(double)"/>).
        /// Never refused for its folds — those are said (<see cref="Folds"/>).</summary>
        public bool SaveAllowed => Saved != null && CrossSexRefit.CoverageAllowed(Landed?.VertexCoverage ?? 1f);
    }

    /// <param name="Moved">The garment's bones the race lacks, moved onto ones it has (<see cref="SkeletonRemap"/>).</param>
    /// <param name="Est">What the race needs of the extra-skeleton table.</param>
    /// <param name="NeedsSkeleton">Bones only the custom skeleton the wearer was read from gives the race.</param>
    /// <param name="Earrings">The garment's earring nodes moved onto the hip (<see cref="SkeletonRemap.DropsEarrings"/>).</param>
    internal sealed record Result(string Refusal, RacialModelBake.Route Route, RacialModelBake.Wearer? Wearer,
                                  byte[]? Baked, BodySizeCatalog? Bent, string? SourcePath, IReadOnlyList<Size> Sizes,
                                  IReadOnlyList<SkeletonRemap.Move>? Moved = null, EstPlan? Est = null,
                                  IReadOnlyList<string>? NeedsSkeleton = null,
                                  IReadOnlyList<SkeletonRemap.Move>? Earrings = null);

    internal static Result Run(Inputs i)
    {
        ushort drawn = ModelSkinReader.RaceOf(i.GamePath);
        string? slot = BodySizeCatalog.SlotOf(i.GamePath);
        var none = RacialModelBake.Route.None;
        if (slot == null || !CrossSexRefit.Offerable(drawn, i.To, slot))
            return new Result($"c{drawn:D4} {slot} is not refitted for c{i.To:D4}", none, null, null, null, null, []);

        var route = new RacialModelBake.Route(drawn, i.To, Down: false, Lowered: CrossSexRefit.Below(drawn, i.To))
            { ByAlias = true };
        if (ModelSkinReader.Read(i.Garment, null, null) is not { } skin)
            return new Result("the garment could not be read", route, null, null, null, null, []);
        var made = OtherRaceWearer.Build(i.Wearer, skin);
        if (made.Wearer is not { } who) return new Result(made.Refusal, route, null, null, null, null, []);
        var moves = made.Moves ?? [];
        var earrings = made.Earrings ?? [];
        Result Refused(string why, byte[]? baked = null, BodySizeCatalog? bent = null)
            => new(why, route, who, baked, bent, null, [], moves, made.Est, made.NeedsSkeleton, earrings);

        // The garment's earring nodes and the bones the race lacks, moved onto ones it has before anything bends it.
        byte[] drawnModel = i.Garment;
        if (made.Renames is { Count: > 0 } renames)
        {
            if (SkeletonRemap.Rename(i.Garment, renames, out string refusal) is not { } renamed)
                return Refused(SkeletonRemap.RenameRefusal(refusal));
            drawnModel = renamed;
            if (earrings.Count > 0)
                i.Log.Information("[Proteus] retarget: earring bones moved for c{0:D4}: {1}", i.To, SkeletonRemap.DescribeHow(earrings));
            if (moves.Count > 0)
                i.Log.Information("[Proteus] retarget: bones moved for c{0:D4}: {1}", i.To, SkeletonRemap.DescribeHow(moves));
        }

        var baked = route.Into(drawnModel, who, out string why);
        if (baked == null || ModelPartReader.Read(baked) is not { } garment)
            return Refused(why.Length > 0 ? why : "the bent garment could not be read");

        void LogBody(string line) => i.Log.Information("[Proteus] retarget: {0}", line);
        var bent = route.Lowered
            ? BakedBodyCatalog.Lowered(i.MadeFor, drawn, i.To, who, [slot], out var failed, byAlias: true, made.Planner, LogBody)
            : BakedBodyCatalog.Of(i.MadeFor, $"{drawn:D4}", i.To, who, [slot], out failed, byAlias: true, made.Planner, LogBody);
        foreach (string f in failed) i.Log.Warning("[Proteus] cross-sex refit: a made-for body could not be bent: {0}", f);
        // Never a guess between sizes: with several and none picked, the refit is refused rather than made from one.
        var bentHere = bent.For(slot);
        var from = i.FromPick != null ? bentHere.FirstOrDefault(i.FromPick) : bentHere.Count == 1 ? bentHere[0] : null;
        if (from == null && i.FromPick == null && bentHere.Count > 1)
            return Refused($"pick which of {bentHere.Count} made-for sizes the garment was made on", baked, bent);
        // The body picked could not be bent: say why, not that another body mod is wanted.
        if (from == null && failed.Count > 0)
            return Refused(string.Join("; ", failed), baked, bent);
        if (from == null)
            return Refused(string.Format(Localization.Strings.Parts.RetargetRefuseCrossSexMadeForFmt,
                                         ModelRace.Describe($"{drawn:D4}")), baked, bent);
        string sourcePath = bent.PathOf(from);

        var (sourceMale, targetMale) = (CrossSexRefit.IsMale(drawn), CrossSexRefit.IsMale(i.To));
        var onto = OtherRaceRefit.OnlyRace(i.Onto, i.To);
        var sizes = new List<Size>();
        foreach (var picked in onto.For(slot).Where(o => i.TargetPick?.Invoke(o) ?? true))
        {
            var target = picked;
            string targetPath = onto.PathOf(target);
            if (BodyRetarget.BuildPair(slot, File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath), slot, sourceMale,
                                       null, null, i.UvRemap, out var pair, out var landed,
                                       BodyRetarget.BodyMaterialReader(targetPath), targetMale) is { } refusal)
            {
                sizes.Add(new Size(target, targetPath, null, null, null, 0, landed, refusal));
                continue;
            }

            // Too little matched: the same body's nude smallclothes, when they match enough. A pair the match refused
            // outright never gets here — a different layout would refuse them too.
            BodyOption? asked = null;
            UvAtlasCorrespondence.Stats? askedLanded = null;
            if (!CrossSexRefit.CoverageAllowed(landed?.VertexCoverage ?? 1f) && i.Nude?.Invoke(target) is { } nude)
            {
                string nudePath = onto.PathOf(nude);
                bool built = BodyRetarget.BuildPair(slot, File.ReadAllBytes(sourcePath), File.ReadAllBytes(nudePath), slot,
                                                    sourceMale, null, null, i.UvRemap, out var nudePair, out var nudeLanded,
                                                    BodyRetarget.BodyMaterialReader(nudePath), targetMale) == null;
                if (built && CrossSexRefit.TakesNude(landed, nudeLanded))
                {
                    i.Log.Information("[Proteus] cross-sex refit: fitted onto {0} ({1:P1} matched); {2} matched {3:P1}",
                                      nude.Label, nudeLanded!.Value.VertexCoverage, target.Label, landed!.Value.VertexCoverage);
                    (asked, askedLanded) = (target, landed);
                    (target, targetPath, pair, landed) = (nude, nudePath, nudePair, nudeLanded);
                }
                else
                    i.Log.Information("[Proteus] cross-sex refit: {0} matched {1}; {2} kept",
                                      nude.Label, built ? $"{nudeLanded?.VertexCoverage:P1}" : "nothing", target.Label);
            }
            var plan = BodyRetarget.Plan(garment, baked, [pair], slot, replaceSkin: true, acrossBodies: true,
                                         clearBody: i.PushOut, cutHidden: i.CutHidden,
                                         tuckSlot: RefitCore.TuckSlot(i.CutHidden && i.PushOut, slot),
                                         carryPiercings: i.CarryPiercings,
                                         liftUnderbust: i.LiftUnderbust ?? CrossSexRefit.LiftsUnderbust(drawn, i.To),
                                         log: line => i.Log.Debug("[Proteus] cross-sex refit: {0}", line),
                                         warn: line => i.Log.Warning("[Proteus] cross-sex refit: {0}", line));
            plan = RefitCore.MatchSkinToBody(plan, sourcePath, targetPath, i.UvRemap, sourceMale, i.Log, targetMale);
            int folds = CrossSexRefit.ClothFolded(baked, plan);
            var (saved, preview, finishRefusal) = RefitCore.Finish(plan, route, who, targetPath, i.Log);
            sizes.Add(new Size(target, targetPath, plan, saved, preview, folds, landed, finishRefusal, asked, askedLanded));
        }
        return new Result("", route, who, baked, bent, sourcePath, sizes, moves, made.Est, made.NeedsSkeleton, earrings);
    }
}
