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

    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string FixPubes = LocalData.Mods + ":fix pubes";
    private const string SmallGlove = "chest size/small/chara/equipment/e0068/model/c0201e0068_glv.mdl";

    /// <summary>
    /// The size set's reading of Geometric's jacket onto each of Rue's chests, as "Every size for other bodies" reads it:
    /// its "Chest Size / Small" option, the top in that option read over the body mods with Rue preferred. Per size: the
    /// cloth inside Rue's chest sized by the top and by the old cloth reading, and the triangles each leaves turned over
    /// and where — measured inside the pass.
    /// </summary>
    [LocalDataFact(Geometric, Bibo, IvcsBibo, FixPubes, Rue)]
    [Trait("Plan", "AC-jacket-refit-12")]
    public void Geometrics_jacket_onto_rue_sized_by_its_top_against_its_cloth()
    {
        string root = LocalData.Path(Geometric);
        var bodies = Bodies(Bibo, IvcsBibo, FixPubes, Rue);
        var rue = bodies[3];
        BodySizeCatalog? CatalogOf(string dir)
            => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;

        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var top = Assert.Single(SiblingPiece.Find(redirects, BodyRetargetWriter.ReadRecord(root), SmallGlove, "_top", Race));
        var topBytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, top.File));
        var sibling = RefitCore.DetectSource(ModelPartReader.Read(topBytes)!, BonesOf(topBytes), "_top", Race, bodies,
                                             rue.Dir, _ => true)!;
        output.WriteLine($"Small's top reads {sibling.Dir} / {sibling.Option.Label} ({sibling.Confidence})");

        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, SmallGlove));
        var parts = ModelPartReader.Read(bytes)!;
        var bones = BonesOf(bytes);
        var reading = KeepEnds.Read("_glv", parts, bytes)!;
        Func<ModelParts, string, AutoRefitDecisions.Pick?> detect
            = (view, slot) => RefitCore.DetectSource(view, bones, slot, Race, bodies, rue.Dir, _ => true);
        var byCloth = KeepEnds.Find(reading, parts, bones, Race, detect, CatalogOf);
        var byTop = KeepEnds.Find(reading, parts, bones, Race, detect, CatalogOf, sibling: sibling);
        Assert.Equal(KeepEnds.Route.Sibling, byTop.Route);
        Assert.Equal(sibling.Option, byTop.Pick!.Option);
        Assert.NotEqual(KeepEnds.Route.Sibling, byCloth.Route);
        output.WriteLine($"by its top: {byTop.Pick.Dir} / {byTop.Pick.Option.Label} (from cloth: {byTop.Pick.FromCloth}); "
                       + $"by its cloth: route {KeepEnds.Letter(byCloth.Route)}, {byCloth.Pick?.Dir} / {byCloth.Pick?.Option.Label}");

        foreach (string size in new[] { "Small", "Medium", "Large" })
        {
            var chest = rue.Catalog.For("_top", Race).First(o => o.Label == "None · " + size);
            BodyOption? TargetFor(string slot) => slot == "_top" ? chest
                : AutoRefitDecisions.Companion(rue.Catalog.For(slot, Race), chest, null);
            string chestPath = rue.Catalog.PathOf(chest);
            (int Inside, int Near, int Folded) RefitFrom(KeepEnds.Found from)
            {
                var refit = Assert.IsType<KeepEnds.Refit>(KeepEnds.Decide(from, reading, bones, Race, rue.Dir, CatalogOf, TargetFor));
                var sourceCat = CatalogOf(refit.Source.Dir)!;
                Assert.Null(KeepEnds.BuildPairs(reading, sourceCat.PathOf(refit.Source.Option), chestPath,
                                                refit.EndFrom == null ? null : sourceCat.PathOf(refit.EndFrom),
                                                refit.EndTo == null ? null : rue.Catalog.PathOf(refit.EndTo),
                                                false, _ => (null, null), s => s, null, null, out var limb, out var end));
                var plan = KeepEnds.Plan(parts, bytes, reading, limb, end, across: true);
                var on = BodySizeMatch.ClothAgainst(ModelPartReader.Read(plan.Model)!, chestPath)!.Value;
                return (on.Inside, on.Near, plan.Report.Folded);
            }
            var a = RefitFrom(byTop);
            var b = RefitFrom(byCloth);
            output.WriteLine($"Rue {size}: by its top {a.Inside}/{a.Near} inside, {a.Folded} folded; "
                           + $"by its cloth {b.Inside}/{b.Near} inside, {b.Folded} folded");
            // Measured 32/33/34 against 89/99/100, folded 0/2/3 against 0/0/2 (amendment 12).
            Assert.True(a.Inside < b.Inside, $"Rue {size}: {a.Inside} inside sized by its top, {b.Inside} by its cloth");
            Assert.True(a.Folded <= 3, $"Rue {size}: {a.Folded} triangles left folded");
        }
    }
}
