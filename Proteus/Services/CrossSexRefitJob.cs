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
    /// <param name="FromPick">Which of its bodies it was made on; null takes the first.</param>
    /// <param name="Onto">The body mod to refit onto (bodies of <paramref name="To"/>).</param>
    /// <param name="TargetPick">Which of its sizes to make; null makes all.</param>
    /// <param name="Wearer">What the wearer of <paramref name="To"/> is built from (<see cref="OtherRaceWearer.Gather"/>).</param>
    internal sealed record Inputs(
        byte[] Garment, string GamePath, ushort To,
        BodySizeCatalog MadeFor, Func<BodyOption, bool>? FromPick,
        BodySizeCatalog Onto, Func<BodyOption, bool>? TargetPick,
        OtherRaceWearer.Inputs Wearer, UVRemapService UvRemap, IPluginLog Log,
        bool PushOut = true, bool CutHidden = true, bool CarryPiercings = false);

    /// <summary>One size made, or refused.</summary>
    /// <param name="Folds">Cloth faces the refit turned over (<see cref="CrossSexRefit.ClothFolded(byte[], BodyRetarget.Planned, Action{string}?)"/>).</param>
    /// <param name="Landed">How the two bodies' skin landed on each other through the maps.</param>
    internal sealed record Size(BodyOption Target, string TargetPath, BodyRetarget.Planned? Plan,
                                BodyRetarget.Planned? Saved, byte[]? Preview, int Folds,
                                UvAtlasCorrespondence.Stats? Landed, string Refusal)
    {
        /// <summary>Whether the run-time check lets it be saved (<see cref="CrossSexRefit.SaveAllowed"/>).</summary>
        public bool SaveAllowed => Saved != null && CrossSexRefit.SaveAllowed(Folds, Landed?.VertexCoverage ?? 1f);
    }

    internal sealed record Result(string Refusal, RacialModelBake.Route Route, RacialModelBake.Wearer? Wearer,
                                  byte[]? Baked, BodySizeCatalog? Bent, string? SourcePath, IReadOnlyList<Size> Sizes);

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

        var baked = route.Into(i.Garment, who, out string why);
        if (baked == null || ModelPartReader.Read(baked) is not { } garment)
            return new Result(why.Length > 0 ? why : "the bent garment could not be read", route, who, null, null, null, []);

        var bent = route.Lowered
            ? BakedBodyCatalog.Lowered(i.MadeFor, drawn, i.To, who, [slot], out var failed, byAlias: true)
            : BakedBodyCatalog.Of(i.MadeFor, $"{drawn:D4}", i.To, who, [slot], out failed, byAlias: true);
        foreach (string f in failed) i.Log.Warning("[Proteus] cross-sex refit: a made-for body could not be bent: {0}", f);
        var from = bent.For(slot).FirstOrDefault(o => i.FromPick?.Invoke(o) ?? true);
        if (from == null)
            return new Result(string.Format(Localization.Strings.Parts.RetargetRefuseCrossSexMadeForFmt,
                                            ModelRace.Describe($"{drawn:D4}")), route, who, baked, bent, null, []);
        string sourcePath = bent.PathOf(from);

        var (sourceMale, targetMale) = (CrossSexRefit.IsMale(drawn), CrossSexRefit.IsMale(i.To));
        var onto = OtherRaceRefit.OnlyRace(i.Onto, i.To);
        var sizes = new List<Size>();
        foreach (var target in onto.For(slot).Where(o => i.TargetPick?.Invoke(o) ?? true))
        {
            string targetPath = onto.PathOf(target);
            if (BodyRetarget.BuildPair(slot, File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath), slot, sourceMale,
                                       null, null, i.UvRemap, out var pair, out var landed,
                                       BodyRetarget.BodyMaterialReader(targetPath), targetMale) is { } refusal)
            {
                sizes.Add(new Size(target, targetPath, null, null, null, 0, landed, refusal));
                continue;
            }
            var plan = BodyRetarget.Plan(garment, baked, [pair], slot, replaceSkin: true, acrossBodies: true,
                                         clearBody: i.PushOut, cutHidden: i.CutHidden,
                                         tuckSlot: RefitCore.TuckSlot(i.CutHidden && i.PushOut, slot),
                                         carryPiercings: i.CarryPiercings,
                                         liftUnderbust: !CrossSexRefit.IsMale(drawn) || CrossSexRefit.IsMale(i.To),
                                         log: line => i.Log.Debug("[Proteus] cross-sex refit: {0}", line),
                                         warn: line => i.Log.Warning("[Proteus] cross-sex refit: {0}", line));
            plan = RefitCore.MatchSkinToBody(plan, sourcePath, targetPath, i.UvRemap, sourceMale, i.Log, targetMale);
            int folds = CrossSexRefit.ClothFolded(baked, plan);
            var (saved, preview, finishRefusal) = RefitCore.Finish(plan, route, who, targetPath, i.Log);
            sizes.Add(new Size(target, targetPath, plan, saved, preview, folds, landed, finishRefusal));
        }
        return new Result("", route, who, baked, bent, sourcePath, sizes);
    }
}
