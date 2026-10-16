using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Garments and jewellery worn on accessory slots, against the installed mods: Scarlet's jacket on a bracelet slot, sized
/// with its top in each "Top Size" option, and real jewellery. Skipped where they are not installed. Everything measured
/// on the refit's own model, inside the pass — never on an export.
/// </summary>
public class AccessoryRefitLocalTests(ITestOutputHelper output)
{
    private const string Scarlet = LocalData.Mods + ":Scarlet - by Solona";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Daystar = LocalData.Mods + ":Daystar DT - by Solona";
    private const string Flutter = LocalData.Mods + ":Flutter My Heart";
    private const string CoveredInIce = LocalData.Mods + ":Covered In Ice - by Solona";
    private const string Race = "0201";
    private const string Jacket = "chara/accessory/a0095/model/c0201a0095_wrs.mdl";
    private const string XsJacket = "top size/neolithe xs/chara/accessory/a0095/model/c0201a0095_wrs.mdl";

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    private static (byte[] Bytes, ModelParts Parts) Load(string root, string rel)
    {
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, rel));
        return (bytes, ModelPartReader.Read(bytes)!);
    }

    private static IReadOnlySet<string> BonesOf(byte[] bytes)
        => new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);

    /// <summary>Neolithe's chest and legs a player would choose: plain medium, as the refit tests do.</summary>
    private static (BodyOption Chest, BodyOption Legs) Chosen(BodySizeCatalog neo)
        => (neo.For("_top", Race).FirstOrDefault(o => o.Label.EndsWith("SFW M", StringComparison.Ordinal)) ?? neo.For("_top", Race)[0],
            neo.For("_dwn", Race).FirstOrDefault(o => o.Label.EndsWith("SFW M", StringComparison.Ordinal)) ?? neo.For("_dwn", Race)[0]);

    [LocalDataFact(Scarlet, Neolithe)]
    [Trait("Plan", "AC-jacket-refit-5")]
    [Trait("Plan", "EDGE-jacket-refit-26")]
    [Trait("Plan", "EDGE-jacket-refit-31")]
    public void Scarlets_bracelet_jacket_is_sized_by_its_top_and_refits_clean()
    {
        string root = LocalData.Path(Scarlet);
        var neo = Body(Neolithe);
        var (chest, legs) = Chosen(neo.Catalog);
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var record = BodyRetargetWriter.ReadRecord(root);

        // The jacket of the XS option, and the top beside it in that option.
        var top = Assert.Single(SiblingPiece.Find(redirects, record, XsJacket, "_top", Race, output.WriteLine));
        output.WriteLine($"sibling: {top.File} in {top.Source}");
        var (topBytes, topParts) = Load(root, top.File);
        var source = RefitCore.DetectSource(topParts, BonesOf(topBytes), "_top", Race, [neo], neo.Dir, _ => true,
                                            output.WriteLine);
        Assert.NotNull(source);
        Assert.True(AutoRefitDecisions.Proceed(source!.Confidence));
        output.WriteLine($"top reads {source.Option.Label} ({source.Confidence})");

        // A garment over the chest.
        var (bytes, jacket) = Load(root, XsJacket);
        var cover = AccessoryCover.Read(jacket, ModelPartReader.Read(File.ReadAllBytes(neo.Catalog.PathOf(chest))),
                                        ModelPartReader.Read(File.ReadAllBytes(neo.Catalog.PathOf(legs))));
        output.WriteLine($"covers {cover.Chest:P1} of {chest.Label}, {cover.Legs:P1} of {legs.Label}");
        // Sized with its top: a garment over the chest, though it covers little of it.
        Assert.Equal(["_top"], cover.Covered(slot => slot == "_top"));
        Assert.Empty(cover.Covered(_ => false));
        Assert.True(AccessoryCover.ClearsSource(jacket, neo.Catalog.PathOf(source.Option)),
                    "the jacket's cloth sits inside the size its top was made on");

        // Refitted onto the chosen chest, the chest beneath it kept in the push-out and the breast's underside cleared.
        Assert.Null(BodyRetarget.BuildPair("_top", neo.Catalog.PathOf(source.Option), neo.Catalog.PathOf(chest), "_top",
                                           false, null, null, null, out var pair));
        var plan = RefitCore.PlanAccessoryRefit(jacket, bytes, [pair], "_top", across: false, out _, output.WriteLine);
        var refitted = ModelPartReader.Read(plan.Model)!;
        output.WriteLine($"moved up to {plan.Report.WorstMove * 1000f:F1} mm, {plan.Report.Pushed} pushed, "
                       + $"{plan.Report.Folded} folded");
        Assert.Equal(0, plan.Report.Folded);
        Assert.True(Degenerate(refitted) <= Degenerate(jacket),
                    $"{Degenerate(refitted)} zero-area triangles after, {Degenerate(jacket)} before");
        var on = BodySizeMatch.ClothAgainst(refitted, neo.Catalog.PathOf(chest))!.Value;
        output.WriteLine($"{on.Inside} of {on.Near} cloth points inside {chest.Label}");
        Assert.True(on.Inside <= on.Near * KeepEnds.SiblingInsideAtMost, $"{on.Inside} of {on.Near} inside the new chest");
        Assert.True(AccessoryCover.ClearsSource(refitted, neo.Catalog.PathOf(chest)));

        // The refit moves points, never their normals; and any skin the jacket carries stays (it is not swapped).
        Assert.Equal(jacket.Normals, refitted.Normals);
        Assert.Equal(SkinTriangles(jacket), SkinTriangles(refitted));

        // The chest's underside is cleared because the chest is named. This jacket does not reach under the breast, so
        // its top — cloth that does — is planned as an accessory would be: with the chest named the underside is cleared,
        // without it, it is not.
        var named = RefitCore.PlanAccessoryRefit(topParts, topBytes, [pair], "_top", across: false, out _);
        var unnamed = RefitCore.PlanAccessoryRefit(topParts, topBytes, [pair], null, across: false, out _);
        output.WriteLine($"top as an accessory: {named.Report.Pushed} pushed with the chest named, "
                       + $"{unnamed.Report.Pushed} without");
        Assert.NotEqual(unnamed.Model, named.Model);
        Assert.True(named.Report.Pushed > unnamed.Report.Pushed,
                    $"{named.Report.Pushed} pushed with the chest named, {unnamed.Report.Pushed} without");

        // A top's own refit is untouched by the chest slot: the same plan, byte for byte.
        var asTop = BodyRetarget.Plan(topParts, topBytes, [pair], "_top", held: new HashSet<int>());
        var asTopToo = BodyRetarget.Plan(topParts, topBytes, [pair], "_top", held: new HashSet<int>(), chestSlot: "_top");
        Assert.Equal(asTop.Model, asTopToo.Model);
    }

    [LocalDataFact(Scarlet, Neolithe)]
    [Trait("Plan", "AC-jacket-refit-6")]
    [Trait("Plan", "EDGE-jacket-refit-5")]
    [Trait("Plan", "EDGE-jacket-refit-16")]
    public void Scarlets_shared_jacket_file_has_two_tops_that_do_not_agree()
    {
        // As on disk: Neolithe S and M serve one jacket file, and Proteus's own refit option (made in the Studio)
        // carries it too — that one is not the author's.
        string root = LocalData.Path(Scarlet);
        var neo = Body(Neolithe);
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        var record = BodyRetargetWriter.ReadRecord(root);
        var candidates = SiblingPiece.Find(redirects, record, "common/7/c0201a0095_wrs.mdl", "_top", Race, output.WriteLine);
        output.WriteLine(string.Join(", ", candidates.Select(c => c.Source)));
        Assert.Equal(["Top Size / Neolithe S", "Top Size / Neolithe M"], candidates.Select(c => c.Source));

        var notes = new List<string>();
        var agreed = SiblingPiece.Agree(candidates, c =>
        {
            var (b, p) = Load(root, c.File);
            return RefitCore.DetectSource(p, BonesOf(b), "_top", Race, [neo], neo.Dir, _ => true);
        }, notes.Add);
        Assert.Null(agreed);
        Assert.Contains("disagree", Assert.Single(notes));

        // Its options name a body, read against every body installed, as the refit reads them: left to the author's
        // sizes when one of theirs is selected; refitted when Proteus's own refit option is.
        string modsRoot = Path.GetDirectoryName(root)!;
        var mods = Directory.GetDirectories(modsRoot).Select(Path.GetFileName).ToDictionary(d => d!, d => d!);
        var bodies = new BodyModIndex().RefreshState(mods, modsRoot).List;
        output.WriteLine($"{bodies.Count} body mods installed");
        var bodyNames = bodies.SelectMany(b => new[] { b.Name, b.Dir }).ToList();
        var sizeWords = bodies.SelectMany(b => AutoRefitDecisions.Slots.SelectMany(s => b.Catalog.For(s, Race)))
                              .SelectMany(o => AutoRefitDecisions.Words(o.Group + " " + o.Section + " " + o.Name))
                              .ToHashSet();
        foreach (string option in new[] { "Neolithe XS", "Neolithe S", "Neolithe M", "Neolithe L" })
            Assert.True(AutoRefitDecisions.NamesABody(option, bodyNames, sizeWords), option);
        foreach (string option in new[] { "Small", "Medium", "Large", "Puffy" })
            Assert.False(AutoRefitDecisions.NamesABody(option, bodyNames, sizeWords), option);

        string studio = record!.Options.First(e => e.GamePath == "chara/equipment/e0041/model/c0201e0041_top.mdl"
                                                   && record.GroupOf(e) == "Top Size").Name;
        string? Drawn(string selected)
            => AccessoryCover.AuthorsBodyOption(Jacket, redirects, record, RefitCore.SingleGroups(root),
                                                g => g == "Top Size" ? [selected] : null, bodyNames, sizeWords);
        Assert.Equal("Neolithe XS", Drawn("Neolithe XS"));
        Assert.Equal("Neolithe M", Drawn("Neolithe M"));
        Assert.Null(Drawn(studio));
    }

    public static TheoryData<string, string> Jewellery => new()
    {
        { Daystar, "chara/accessory/a0003/model/c0201a0003_nek.mdl" },
        { Flutter, "chara/accessory/a0031/model/c0201a0031_ril.mdl" },
        { CoveredInIce, "chara/accessory/a0095/model/c0201a0095_nek.mdl" },
        { CoveredInIce, "chara/accessory/a0095/model/c0201a0095_rir.mdl" },
        { CoveredInIce, "chara/accessory/a0095/model/c0201a0095_ril.mdl" },
        { CoveredInIce, "chara/accessory/a0095/model/c0201a0095_ear.mdl" },
    };

    [LocalDataTheory(Daystar, Flutter, CoveredInIce, Neolithe)]
    [MemberData(nameof(Jewellery))]
    [Trait("Plan", "AC-jacket-refit-7")]
    public void Real_jewellery_covers_too_little_of_the_body_to_be_a_garment(string mod, string gamePath)
    {
        string root = LocalData.Path(mod);
        var file = PenumbraModMeta.ReadAllRedirects(root)
                                  .FirstOrDefault(d => string.Equals(d.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
        Assert.True(file.File != null, $"{mod} does not serve {gamePath}");
        var neo = Body(Neolithe);
        var (chest, legs) = Chosen(neo.Catalog);
        var (_, piece) = Load(root, file.File!);

        var cover = AccessoryCover.Read(piece, ModelPartReader.Read(File.ReadAllBytes(neo.Catalog.PathOf(chest))),
                                        ModelPartReader.Read(File.ReadAllBytes(neo.Catalog.PathOf(legs))));
        // Sized with nothing in its options: it would have to cover most of the chest or legs to be a garment.
        var redirects = PenumbraModMeta.ReadAllRedirects(root);
        bool SizedWith(string slot) => SiblingPiece.Find(redirects, null, file.File!, slot, Race).Count > 0;
        output.WriteLine($"{gamePath}: {cover.Chest:P1} of the chest, {cover.Legs:P1} of the legs; sized with the chest "
                       + $"{SizedWith("_top")}, the legs {SizedWith("_dwn")}");
        Assert.Empty(cover.Covered(SizedWith));
    }

    /// <summary>Triangles with no area: three corners in a line or on one point.</summary>
    private static int Degenerate(ModelParts model)
    {
        int count = 0;
        foreach (var part in model.Parts.Where(p => p.Island < 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                System.Numerics.Vector3 P(int v) => new(model.Positions[v * 3], model.Positions[v * 3 + 1], model.Positions[v * 3 + 2]);
                var a = P(part.Triangles[t]);
                var cross = System.Numerics.Vector3.Cross(P(part.Triangles[t + 1]) - a, P(part.Triangles[t + 2]) - a);
                if (cross.LengthSquared() < 1e-18f) count++;
            }
        return count;
    }

    private static int SkinTriangles(ModelParts model)
        => model.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material)).Sum(p => p.TriangleCount);
}
