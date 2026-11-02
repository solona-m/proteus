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
/// measured is what the CLI writes. Its shoes and gloves (<see cref="RunEnd"/>) serve Studio, the CLI and the size set
/// alike, through one inputs builder (<see cref="EndInputs"/>) for the first two.
/// </summary>
internal static partial class CrossSexRefitJob
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
    /// of the made-for body and they enough (<see cref="CrossSexRefit.TakesNude"/>) — or the match refused the target
    /// outright for too little coverage. The size set's legs and shoes, and Studio's and the CLI's shoes
    /// (<see cref="NudeOf"/>); null fits every target as it was picked.</param>
    /// <param name="HeldParts">A shoe's or glove's parts, by label, held where they are with the foot or hand: Studio's
    /// unticked parts. Null holds none beside them.</param>
    internal sealed record Inputs(
        byte[] Garment, string GamePath, ushort To,
        BodySizeCatalog MadeFor, Func<BodyOption, bool>? FromPick,
        BodySizeCatalog Onto, Func<BodyOption, bool>? TargetPick,
        OtherRaceWearer.Inputs Wearer, UVRemapService UvRemap, IPluginLog Log,
        bool PushOut = true, bool CutHidden = true, bool CarryPiercings = false, bool? LiftUnderbust = null,
        Func<BodyOption, BodyOption?>? Nude = null, IReadOnlySet<string>? HeldParts = null);

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

    /// <summary>A chest or legs garment made for the other sex: Studio's "Refit for", the CLI and the size set. A shoe or
    /// glove is refused here; see <see cref="RunEnd"/>.</summary>
    internal static Result Run(Inputs i) => Core(i, ends: false);

    /// <summary>
    /// A women's shoe or glove made a men's — Studio's "Refit for", the CLI and the size set (<see cref="CrossSexRefit.EndsCross"/>): its leg or arm
    /// refitted as <see cref="KeepEnds"/> refits one, through the leg or arm of the made-for and the men's body, the foot
    /// or hand held and given its new shape, after the same bend and bone moves as a chest or legs garment.
    /// <see cref="Inputs.FromPick"/> and <see cref="Inputs.TargetPick"/> pick the limb (legs for a shoe, chest for a
    /// glove). The nude smallclothes are tried for a shoe's legs as for a legs garment.
    /// </summary>
    internal static Result RunEnd(Inputs i) => Core(i, ends: true);

    private static Result Core(Inputs i, bool ends)
    {
        ushort drawn = ModelSkinReader.RaceOf(i.GamePath);
        string? slot = BodySizeCatalog.SlotOf(i.GamePath);
        var none = RacialModelBake.Route.None;
        if (slot == null || !(ends ? CrossSexRefit.EndsCross(drawn, i.To, slot) : CrossSexRefit.Offerable(drawn, i.To, slot)))
            return new Result($"c{drawn:D4} {slot} is not refitted for c{i.To:D4}", none, null, null, null, null, []);
        // The leg or arm the pairs are read through; for a chest or legs garment, its own slot.
        string limb = ends ? AutoRefitDecisions.LimbOf(slot)! : slot;

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
            ? BakedBodyCatalog.Lowered(i.MadeFor, drawn, i.To, who, ends ? [limb, slot] : [slot], out var failed,
                                       byAlias: true, made.Planner, LogBody)
            : BakedBodyCatalog.Of(i.MadeFor, $"{drawn:D4}", i.To, who, ends ? [limb, slot] : [slot], out failed,
                                  byAlias: true, made.Planner, LogBody);
        foreach (string f in failed) i.Log.Warning("[Proteus] cross-sex refit: a made-for body could not be bent: {0}", f);
        // Never a guess between sizes: with several and none picked, the refit is refused rather than made from one.
        var bentHere = bent.For(limb);
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
        // A shoe or glove: its foot or hand held, read apart from the leg or arm; the made-for body's own for its shape.
        KeepEnds.Reading? reading = null;
        string? endFromPath = null;
        HashSet<int>? held = null;
        if (ends)
        {
            // Held by label, as the panel's planner holds them: a submesh's triangles already include every island of it.
            if (i.HeldParts is { Count: > 0 } labels)
            {
                held = [];
                foreach (var part in garment.Parts)
                    if (labels.Contains(part.Label))
                        held.UnionWith(part.Triangles);
            }
            if (KeepEnds.Read(slot, garment, baked) is not { } read)
                return Refused(string.Format(Localization.Strings.SizeSet.EndsUnreadableFmt, Path.GetFileName(i.GamePath)),
                               baked, bent);
            reading = read;
            if (AutoRefitDecisions.Companion(bent.For(slot), from, null) is { } endFrom) endFromPath = bent.PathOf(endFrom);
        }

        var (sourceMale, targetMale) = (CrossSexRefit.IsMale(drawn), CrossSexRefit.IsMale(i.To));
        var onto = OtherRaceRefit.OnlyRace(i.Onto, i.To);
        var sizes = new List<Size>();
        foreach (var picked in onto.For(limb).Where(o => i.TargetPick?.Invoke(o) ?? true))
        {
            var target = picked;
            string targetPath = onto.PathOf(target);
            string? refusal = BodyRetarget.BuildPair(limb, File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath),
                                                     limb, sourceMale, null, null, i.UvRemap, out var pair, out var landed,
                                                     BodyRetarget.BodyMaterialReader(targetPath), targetMale);
            // Too little matched — accepted under the bar a men's version needs, or refused outright for coverage (only
            // that refusal says how much landed; a different layout, missing maps or an unreadable body never do, and are
            // never tried on the nude smallclothes, which they would refuse too).
            bool tooLittle = refusal == null ? !CrossSexRefit.CoverageAllowed(landed?.VertexCoverage ?? 1f) : landed != null;
            if (refusal != null && !(tooLittle && i.Nude != null))
            {
                // Landed stays null on a refusal: the refusal is said in its own words, never judged as a coverage.
                sizes.Add(new Size(target, targetPath, null, null, null, 0, null, refusal));
                continue;
            }

            // Too little matched: the same body's nude smallclothes, when they match enough (TBSE's Gen A legs, TBSE-X's
            // Flaccid M A — the latter's SFW legs match a nude-made women's body only 62%, below the refusal's 80%).
            BodyOption? asked = null;
            UvAtlasCorrespondence.Stats? askedLanded = null;
            if (tooLittle && i.Nude?.Invoke(target) is { } nude)
            {
                string nudePath = onto.PathOf(nude);
                string? nudeRefusal = BodyRetarget.BuildPair(limb, File.ReadAllBytes(sourcePath), File.ReadAllBytes(nudePath),
                                                             limb, sourceMale, null, null, i.UvRemap, out var nudePair,
                                                             out var nudeLanded, BodyRetarget.BodyMaterialReader(nudePath),
                                                             targetMale);
                bool built = nudeRefusal == null;
                if (built && CrossSexRefit.TakesNude(landed, nudeLanded) && landed is { } sfw && nudeLanded is { } bare)
                {
                    i.Log.Information("[Proteus] cross-sex refit: fitted onto {0} ({1:P1} matched); {2} matched {3:P1}",
                                      nude.Label, bare.VertexCoverage, target.Label, sfw.VertexCoverage);
                    (asked, askedLanded) = (target, landed);
                    (target, targetPath, pair, landed) = (nude, nudePath, nudePair, nudeLanded);
                    refusal = null;
                }
                else if (refusal != null)
                    i.Log.Information("[Proteus] cross-sex refit: {0} {1}; {2} refused, matched {3:P1}",
                                      nude.Label, built ? $"matched {nudeLanded?.VertexCoverage:P1}" : $"refused: {nudeRefusal}",
                                      target.Label, landed?.VertexCoverage ?? 0f);
                else
                    i.Log.Information("[Proteus] cross-sex refit: {0} {1}; {2} kept",
                                      nude.Label, built ? $"matched {nudeLanded?.VertexCoverage:P1}" : $"refused: {nudeRefusal}",
                                      target.Label);
            }
            // The nude smallclothes failed too, or are not installed: refused with the body's own reason, as before.
            if (refusal != null)
            {
                sizes.Add(new Size(target, targetPath, null, null, null, 0, null, refusal));
                continue;
            }
            BodyRetarget.Planned plan;
            if (reading != null)
            {
                // The foot or hand's shape, from the made-for body's to the men's body's; refitted without it when either
                // has none or the two do not pair.
                BodyRetarget.SlotPair? endPair = null;
                UvAtlasCorrespondence.Stats? endLanded = null;
                string? endToPath = AutoRefitDecisions.Companion(onto.For(slot), target, null) is { } endTo
                    ? onto.PathOf(endTo) : null;
                if (endFromPath != null && endToPath != null)
                {
                    if (BodyRetarget.BuildPair(slot, File.ReadAllBytes(endFromPath), File.ReadAllBytes(endToPath), slot,
                                               sourceMale, null, null, i.UvRemap, out var shape, out var endStats,
                                               BodyRetarget.BodyMaterialReader(endToPath), targetMale) is { } endWhy)
                        i.Log.Information("[Proteus] cross-sex refit: {0} without the end's shape: {1}", target.Label, endWhy);
                    else (endPair, endLanded) = (shape, endStats);
                }
                else
                    i.Log.Information("[Proteus] cross-sex refit: {0} without the end's shape: no {1} to give it",
                                      target.Label, endFromPath == null ? "made-for end" : "men's end");
                BodyRetarget.Planned Today()
                    => KeepEnds.Plan(garment, baked, reading, pair, endPair, replaceSkin: true, across: true,
                                     clearBody: i.PushOut, alsoHeld: held, extra: null,
                                     log: line => i.Log.Debug("[Proteus] cross-sex refit: {0}", line),
                                     warn: line => i.Log.Warning("[Proteus] cross-sex refit: {0}", line),
                                     carryPiercings: i.CarryPiercings);
                plan = slot == "_glv" && !BodyRetarget.Tuned.NoReshapeEnds
                    ? Reshaped(garment, baked, reading, pair, endPair, endLanded, held, target.Label, i) ?? Today()
                    : Today();
            }
            else
                plan = BodyRetarget.Plan(garment, baked, [pair], slot, replaceSkin: true, acrossBodies: true,
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

    /// <summary>
    /// A women's glove made a men's with its bare hand made the men's (cross-sex-wrist-band): the glove's skin swapped for
    /// the men's own chest-arm and hands skin, cut to what it drew, the bare hand refitted with the arm — as the men's top's
    /// arm already is, so where both draw the arm they draw the same triangles, and the hand meets it only where the men's
    /// body meets itself. Null, said once, whenever that cannot be done safely; the glove is then made as before.
    /// </summary>
    private static BodyRetarget.Planned? Reshaped(ModelParts garment, byte[] baked, KeepEnds.Reading reading,
                                                  BodyRetarget.SlotPair pair, BodyRetarget.SlotPair? endPair,
                                                  UvAtlasCorrespondence.Stats? endLanded, IReadOnlySet<int>? held,
                                                  string target, Inputs i)
    {
        void Kept(string why)
            => i.Log.Information("[Proteus] cross-sex refit: {0} hand kept as made: {1}", target, why);
        if (endPair is not { } hands)
        {
            Kept("no men's hands to refit it onto");
            return null;
        }
        if (endLanded is not { } matched || !CrossSexRefit.CoverageAllowed(matched.VertexCoverage))
        {
            Kept($"the hands bodies match only {endLanded?.VertexCoverage ?? 0f:P1}");
            return null;
        }
        if (KeepEnds.Reshape(reading, garment, held, out string why) is not { } reshaped)
        {
            Kept(why);
            return null;
        }
        // The hand is the body's own: every skin mesh with skin on a hand let go lies on the made-for bodies.
        var bodyMeshes = BodyRetarget.SwappedSkinMeshes(garment, [pair, hands with { ShapeOnly = false }]);
        var letGo = reading.Ends.Except(reshaped.Ends).ToHashSet();
        var handMeshes = garment.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)
                                                  && p.Triangles.Any(letGo.Contains))
                                      .Select(p => p.Mesh).ToHashSet();
        if (!handMeshes.All(bodyMeshes.Contains))
        {
            Kept("its skin is not the made-for body's own (posed or sculpted)");
            return null;
        }
        var plan = KeepEnds.Plan(garment, baked, reshaped, pair, hands, replaceSkin: true, across: true,
                                 clearBody: i.PushOut, alsoHeld: held, extra: null,
                                 log: line => i.Log.Debug("[Proteus] cross-sex refit: {0}", line),
                                 warn: line => i.Log.Warning("[Proteus] cross-sex refit: {0}", line),
                                 carryPiercings: i.CarryPiercings, reshapeEnd: true,
                                 swappedSkinTags: KeepEnds.SkinTagsOf(garment));
        if (plan.Report.Swap is not { Posed: 0, Kept: 0, Removed: > 0 } swap)
        {
            Kept("the swap kept the glove's own skin");
            return null;
        }
        // The arm's skin came in: every laid point of the glove's skin on the men's chest lies on the rebuilt skin. The
        // chest's skin is claimed only when enough of the mesh lies on the chest alone, and a short strip of arm beside a
        // dense hand would otherwise lose its arm.
        if (swap.AddedBySlot?.GetValueOrDefault(pair.Slot) is not > 0 || !ArmSkinCameIn(garment, plan, pair))
        {
            Kept("the arm's skin did not come in with the men's");
            return null;
        }
        i.Log.Information("[Proteus] cross-sex refit: {0} hand: made the men's ({1:P1} matched) — skin swapped for the " +
                          "men's ({2} → {3} triangles, {4} cut), {5} hand(s) kept as made", target,
                          matched.VertexCoverage, swap.Removed, swap.Added, swap.Cut,
                          reshaped.Ends.Count == 0 ? 0 : 1);
        return plan;
    }

    /// <summary>Every glove skin point laid on the men's chest (within <see cref="BodyRetarget.SwapOnBody"/>) lies within
    /// <see cref="BodyRetarget.CutReach"/> of the rebuilt glove's skin.</summary>
    private static bool ArmSkinCameIn(ModelParts garment, BodyRetarget.Planned plan, BodyRetarget.SlotPair chest)
    {
        if (ModelPartReader.Read(plan.Model) is not { } rebuilt) return false;
        var skin = new BodySurface(rebuilt, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(rebuilt)));
        var body = new BodySurface(chest.Target, BodySurface.CellFor(BodyRetarget.MeanEdgeOf(chest.Target)));
        foreach (var part in garment.Parts)
        {
            if (part.Island >= 0 || !SecondSkinWriter.IsBodySkinMaterial(part.Material)) continue;
            foreach (int v in part.Triangles.Distinct())
            {
                var d = plan.Edit.DeltaAt(v);
                var at = new System.Numerics.Vector3(garment.Positions[3 * v] + d.X, garment.Positions[3 * v + 1] + d.Y,
                                                      garment.Positions[3 * v + 2] + d.Z);
                if (!body.Nearest(at, BodyRetarget.SwapOnBody, out _)) continue;
                if (!skin.Nearest(at, BodyRetarget.CutReach, out _)) return false;
            }
        }
        return true;
    }
}
