using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Plugin.Services;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// A women's shoe or glove made a men's (<see cref="RunEnd"/>), its inputs built one way for Studio's "Refit for" and the
/// CLI's --for-race, and the size set's own narrowing shared with them: what each surface hands the job differs only in how
/// it picked the made-for size and the targets.
/// </summary>
internal static partial class CrossSexRefitJob
{
    /// <summary>
    /// The made-for body narrowed to one leg or arm size and its own foot or hand: the job bends what it is given, so a
    /// catalog of one size is bent once, and that size is the one it is made from (no pick needed).
    /// </summary>
    internal static BodySizeCatalog NarrowMadeFor(BodySizeCatalog catalog, BodyOption limb, string endSlot, string race)
        => catalog with
        {
            Options = AutoRefitDecisions.Companion(catalog.For(endSlot, race), limb, null) is { } end ? [limb, end] : [limb],
        };

    /// <summary>
    /// For a men's legs target, the same body's nude smallclothes among <paramref name="menLegs"/>, as the size set offers
    /// them (<see cref="SupportedBodies.Size.NudeRel"/>, TBSE's SFW legs → its Gen A); null for every other target, and
    /// null altogether when none of <paramref name="menLegs"/> has any.
    /// </summary>
    internal static Func<BodyOption, BodyOption?>? NudeOf(IReadOnlyList<BodyOption> menLegs)
    {
        var nudes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, _, male) in SupportedBodies.Choices)
            if (male && SupportedBodies.Of(key, "_dwn", false) is { } body)
                foreach (var size in body.Sizes)
                    if (size.NudeRel is { } nude) nudes[RefitCore.Normal(size.Rel)] = nude;
        if (nudes.Count == 0) return null;
        return target => nudes.TryGetValue(RefitCore.Normal(target.Rel), out string? rel)
            ? menLegs.FirstOrDefault(o => RefitCore.Normal(o.Rel) == RefitCore.Normal(rel))
            : null;
    }

    /// <summary>
    /// The job's inputs for one shoe or glove made a men's, as Studio and the CLI hand it over.
    /// </summary>
    /// <param name="garment">The model AS DRAWN at <paramref name="gamePath"/>: the job bends it itself.</param>
    /// <param name="madeFor">The made-for body, UNBENT (bodies of the drawn race).</param>
    /// <param name="limbFrom">The leg or arm size it was made on: an option of <paramref name="madeFor"/>, or of a bent
    /// copy of it (Studio's lists are bent) — matched by its group, section, name and slot, as the CLI matches.</param>
    /// <param name="targetRels">The sizes of <paramref name="onto"/> to make, by file.</param>
    /// <param name="heldLabels">Parts held where they are with the foot or hand (Studio's unticked parts).</param>
    /// <param name="bakedModel">Studio's bake of the garment, when it has one: handing it in as the garment would bend it
    /// twice, so that throws. The CLI passes null.</param>
    /// <param name="refusal">Why it can't be made, said to the user; null when the inputs were built.</param>
    internal static Inputs? EndInputs(byte[] garment, string gamePath, ushort to, BodySizeCatalog madeFor,
                                      BodyOption limbFrom, BodySizeCatalog onto, IReadOnlyCollection<string> targetRels,
                                      OtherRaceWearer.Inputs wearer, UVRemapService uv, IPluginLog log, bool pushOut,
                                      bool carryPiercings, IReadOnlyCollection<string>? heldLabels, byte[]? bakedModel,
                                      out string? refusal)
    {
        if (bakedModel != null && (ReferenceEquals(garment, bakedModel) || garment.AsSpan().SequenceEqual(bakedModel)))
            throw new InvalidOperationException(
                "a shoe or glove refitted for the other sex is handed its baked model, not the one drawn");
        refusal = null;
        ushort drawn = ModelSkinReader.RaceOf(gamePath);
        string slot = BodySizeCatalog.SlotOf(gamePath) ?? "";
        string race = $"{drawn:D4}";
        var unbent = madeFor.For(limbFrom.Slot, race)
                            .FirstOrDefault(o => o.Group == limbFrom.Group && o.Section == limbFrom.Section
                                                 && o.Name == limbFrom.Name && o.Slot == limbFrom.Slot);
        if (unbent == null)
        {
            refusal = string.Format(Strings.Parts.RetargetRefuseCrossSexMadeForFmt, ModelRace.Describe(race));
            return null;
        }
        var wanted = targetRels.Select(RefitCore.Normal).ToHashSet(StringComparer.Ordinal);
        string? limb = AutoRefitDecisions.LimbOf(slot);
        return new Inputs(garment, gamePath, to, NarrowMadeFor(madeFor, unbent, slot, race), null, onto,
                          o => wanted.Contains(RefitCore.Normal(o.Rel)), wearer, uv, log,
                          PushOut: pushOut, CarryPiercings: carryPiercings,
                          Nude: slot == "_sho" && limb != null ? NudeOf(OtherRaceRefit.OnlyRace(onto, to).For(limb)) : null,
                          HeldParts: heldLabels is { Count: > 0 } ? heldLabels.ToHashSet(StringComparer.Ordinal) : null);
    }

    /// <summary>What Studio's planner gets back for a shoe or glove made a men's: the sizes to preview and save, or why not.</summary>
    /// <param name="Planned">Each size, by the option the user PICKED, with its saved model and preview; null when refused.</param>
    /// <param name="Folds">Cloth faces each size turned over.</param>
    /// <param name="Quality">One quality line per size.</param>
    internal sealed record StudioEnd(IReadOnlyList<(BodyOption To, BodyRetarget.Planned Saved, byte[]? Preview)>? Planned,
                                     string Error, IReadOnlyList<int> Folds, string Quality);

    /// <summary>Runs the job for Studio (worker thread) and words its outcome (<see cref="ToStudio"/>).</summary>
    internal static StudioEnd ForStudio(Inputs inputs, int asked, IPluginLog log)
    {
        var clock = Stopwatch.StartNew();
        var result = RunEnd(inputs);
        log.Information("[Proteus] retarget: shoe or glove for c{0:D4} in {1} ms, {2} size(s){3}", inputs.To,
                        clock.ElapsedMilliseconds, result.Sizes.Count,
                        result.Refusal.Length > 0 ? ": " + result.Refusal : "");
        return ToStudio(result, asked);
    }

    /// <summary>
    /// The job's outcome as Studio's planner takes it: refused whole when the job refused, when any size may not be saved
    /// (its label said first when several were asked), or when it made no size at all; otherwise every size, under the
    /// option the user picked, with its quality line — a size fitted to the nude smallclothes instead saying so.
    /// </summary>
    internal static StudioEnd ToStudio(Result r, int asked)
    {
        if (r.Refusal.Length > 0) return new StudioEnd(null, r.Refusal, [], "");
        if (r.Sizes.Count == 0) return new StudioEnd(null, Strings.Parts.RetargetCrossSexEndNoSizes, [], "");
        bool several = asked > 1 || r.Sizes.Count > 1;
        var planned = new List<(BodyOption, BodyRetarget.Planned, byte[]?)>();
        var quality = new List<string>();
        var folds = new List<int>();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        foreach (var size in r.Sizes)
        {
            var picked = size.Asked ?? size.Target;
            string Labelled(string text) => several ? $"{picked.Label}: {text}" : text;
            double coverage = size.Landed?.VertexCoverage ?? 1.0;
            var (low, line) = CrossSexRefit.Judged(coverage, size.Folds);
            if (size.Saved == null || !size.SaveAllowed)
            {
                string why = low ?? (size.Refusal.Length > 0 ? size.Refusal : Strings.SizeSet.NotMade);
                return new StudioEnd(null, Labelled(why), [], "");
            }
            if (size.Asked != null)
                line += " " + string.Format(Strings.Parts.RetargetRaceCrossSexNudeFmt, size.Asked.Label,
                                            (size.AskedLanded?.VertexCoverage ?? 0.0).ToString("P0", culture),
                                            size.Target.Label, coverage.ToString("P0", culture));
            planned.Add((picked, size.Saved, size.Preview));
            quality.Add(Labelled(line));
            folds.Add(size.Folds);
        }
        return new StudioEnd(planned, "", folds, string.Join("\n", quality));
    }
}
