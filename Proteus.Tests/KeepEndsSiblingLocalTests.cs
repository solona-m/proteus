using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Geometric's cropped jacket — its hands model, sized with its top in each "Chest Size" option — sized by that top
/// (<see cref="SiblingPiece"/>) instead of by how its loose cloth sits, against the installed mods. Skipped where they
/// are not installed. Measured on the refit's own model, inside the pass.
/// </summary>
public class KeepEndsSiblingLocalTests(ITestOutputHelper output)
{
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string IvcsBibo = LocalData.Mods + ":[IVCS] Bibo+";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Race = "0201";
    private const string GloveRel = "chest size/medium/chara/equipment/e0068/model/c0201e0068_glv.mdl";

    private static List<(string Dir, BodySizeCatalog Catalog)> Bodies(params string[] needs)
        => needs.Select(n => (Dir: Path.GetFileName(LocalData.Path(n)), Catalog: BodySizeCatalog.Read(LocalData.Path(n))))
                .ToList();

    private static IReadOnlySet<string> BonesOf(byte[] bytes)
        => new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);

    [LocalDataFact(Geometric, Bibo, IvcsBibo, Neolithe)]
    [Trait("Plan", "AC-jacket-refit-1")]
    [Trait("Plan", "EDGE-jacket-refit-4")]
    [Trait("Plan", "EDGE-jacket-refit-7")]
    public void Geometrics_jacket_is_sized_by_its_medium_top_and_clears_the_new_bust_better()
    {
        string root = LocalData.Path(Geometric);
        var bodies = Bodies(Neolithe, Bibo, IvcsBibo);
        BodySizeCatalog? CatalogOf(string dir)
            => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;
        var neo = bodies[0];

        // The top in the same option, never Proteus's own refit option beside it.
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var record = BodyRetargetWriter.ReadRecord(root);
        var candidates = SiblingPiece.Find(redirects, record, GloveRel, "_top", Race, output.WriteLine);
        var top = Assert.Single(candidates);
        output.WriteLine($"sibling: {top.File} in {top.Source}");
        Assert.Contains("medium", top.File, StringComparison.OrdinalIgnoreCase);

        // Read off the top's skin, as its own refit reads it.
        var topBytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, top.File));
        var sibling = RefitCore.DetectSource(ModelPartReader.Read(topBytes)!, BonesOf(topBytes), "_top", Race, bodies,
                                             neo.Dir, _ => true, output.WriteLine);
        Assert.NotNull(sibling);
        output.WriteLine($"top reads {sibling!.Dir} / {sibling.Option.Label} ({sibling.Confidence})");
        Assert.True(AutoRefitDecisions.Proceed(sibling.Confidence));
        Assert.Contains("Medium", sibling.Option.Label, StringComparison.OrdinalIgnoreCase);

        // The jacket.
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, GloveRel));
        var parts = ModelPartReader.Read(bytes)!;
        var bones = BonesOf(bytes);
        var reading = KeepEnds.Read("_glv", parts, bytes)!;
        Func<ModelParts, string, AutoRefitDecisions.Pick?> detect
            = (view, slot) => RefitCore.DetectSource(view, bones, slot, Race, bodies, neo.Dir, _ => true);
        var before = KeepEnds.Find(reading, parts, bones, Race, detect, CatalogOf);
        var found = KeepEnds.Find(reading, parts, bones, Race, detect, CatalogOf, sibling: sibling);
        output.WriteLine($"without its top: route {KeepEnds.Letter(before.Route)}, {before.Pick?.Dir} / "
                       + $"{before.Pick?.Option.Label}; with it: route {KeepEnds.Letter(found.Route)}, "
                       + $"{found.Pick?.Dir} / {found.Pick?.Option.Label}");
        Assert.Equal(KeepEnds.Route.Sibling, found.Route);
        Assert.Equal(sibling.Option, found.Pick!.Option);

        // Refitted onto Neolithe's chosen chest: less of the jacket inside the new bust than from the old reading.
        var chest = neo.Catalog.For("_top", Race).FirstOrDefault(o => o.Label == "DEFAULT ALMOND · NSFW Almond XS")
                    ?? neo.Catalog.For("_top", Race).First(o => o.Label.Contains("Almond XS", StringComparison.Ordinal));
        BodyOption? TargetFor(string slot) => slot == "_top" ? chest
            : AutoRefitDecisions.Companion(neo.Catalog.For(slot, Race), chest, null);
        string chestPath = neo.Catalog.PathOf(chest);

        (int Inside, int Near, int Folded) RefitFrom(KeepEnds.Found from)
        {
            var refit = Assert.IsType<KeepEnds.Refit>(KeepEnds.Decide(from, reading, bones, Race, neo.Dir, CatalogOf, TargetFor));
            var sourceCat = CatalogOf(refit.Source.Dir)!;
            Assert.Null(KeepEnds.BuildPairs(reading, sourceCat.PathOf(refit.Source.Option), chestPath,
                                            refit.EndFrom == null ? null : sourceCat.PathOf(refit.EndFrom),
                                            refit.EndTo == null ? null : neo.Catalog.PathOf(refit.EndTo),
                                            false, _ => (null, null), s => s, null, null, out var limb, out var end));
            var plan = KeepEnds.Plan(parts, bytes, reading, limb, end, across: true);
            var on = BodySizeMatch.ClothAgainst(ModelPartReader.Read(plan.Model)!, chestPath)!.Value;
            return (on.Inside, on.Near, plan.Report.Folded);
        }

        var bySibling = RefitFrom(found);
        output.WriteLine($"from {found.Pick.Option.Label}: {bySibling.Inside} of {bySibling.Near} cloth points inside "
                       + $"the new chest, {bySibling.Folded} folded");
        Assert.Equal(0, bySibling.Folded);
        if (before.Pick is { } old && before.Route != KeepEnds.Route.Sibling)
        {
            var byCloth = RefitFrom(before);
            output.WriteLine($"from {old.Option.Label} (route {KeepEnds.Letter(before.Route)}): {byCloth.Inside} of "
                           + $"{byCloth.Near} inside, {byCloth.Folded} folded");
            Assert.True(bySibling.Inside < byCloth.Inside,
                        $"sized by its top, {bySibling.Inside} cloth points sit inside the new chest; by its cloth, "
                      + $"{byCloth.Inside}");
        }
    }
}
