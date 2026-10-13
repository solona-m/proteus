using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The chest beneath a legs garment's waist (see <see cref="RefitCore.ChestBeneath"/>), against the installed mods:
/// Geometric's high-waisted skirt, made on "fix pubes" — Bibo+'s "Type A - Small" legs alone — onto Rue; Heartthrob's
/// and Scarlet's pants, which stop at the hips.
/// </summary>
public class ChestBeneathLocalTests(ITestOutputHelper output)
{
    private const string Geometric = LocalData.Mods + ":Geometric - by Solona";
    private const string Heartthrob = LocalData.Mods + ":Heartthrob - by Solona";
    private const string Scarlet = LocalData.Mods + ":Scarlet - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string IvcsBibo = LocalData.Mods + ":[IVCS] Bibo+";
    private const string FixPubes = LocalData.Mods + ":fix pubes";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Race = "0201";
    private const string Skirt = "leg size/small/chara/equipment/e6010/model/c0201e6010_dwn.mdl";

    private static (string Dir, BodySizeCatalog Catalog) Body(string need)
        => (Path.GetFileName(LocalData.Path(need)), BodySizeCatalog.Read(LocalData.Path(need)));

    private static (byte[] Bytes, ModelParts Parts, HashSet<string> Bones) Load(string mod, string rel)
    {
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(mod), rel));
        return (bytes, ModelPartReader.Read(bytes)!, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal));
    }

    private static Func<string, BodySizeCatalog?> CatalogOf(List<(string Dir, BodySizeCatalog Catalog)> bodies)
        => dir => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;

    [LocalDataFact(Geometric, Bibo, IvcsBibo, FixPubes, Rue)]
    [Trait("Plan", "AC-outfit-fit-6")]
    [Trait("Plan", "EDGE-outfit-fit-24")]
    public void A_skirt_made_on_legs_with_no_chest_takes_the_chests_of_the_body_with_the_same_legs()
    {
        var bodies = new[] { Body(FixPubes), Body(IvcsBibo), Body(Bibo), Body(Rue) }.ToList();
        var (_, skirt, bones) = Load(Geometric, Skirt);
        var pick = RefitCore.DetectSource(skirt, bones, "_dwn", Race, bodies, bodies[3].Dir, _ => true)!;
        Assert.Equal("fix pubes", pick.Dir);
        var notes = new List<string>();

        var beneath = RefitCore.ChestBeneathOf(skirt, pick, Race, bodies, CatalogOf(bodies), notes.Add);

        notes.ForEach(output.WriteLine);
        Assert.NotNull(beneath);
        // [IVCS] Bibo+ comes first in the search and has chests, but not the same legs: Bibo+'s are, point for point.
        Assert.Equal("Bibo+", beneath!.Dir);
        Assert.True(beneath.Cells >= RefitCore.OverBellyGate, $"{beneath.Cells} cells");
        Assert.Contains(notes, n => n.Contains("Type A - Small", StringComparison.Ordinal));

        // [EDGE-outfit-fit-24] Asked again — the twin from what was found before, the gate from the geometry: the same.
        var again = RefitCore.ChestBeneathOf(skirt, pick, Race, bodies, CatalogOf(bodies));
        Assert.Equal(beneath, again);
    }

    [LocalDataFact(Geometric, IvcsBibo, FixPubes, Rue)]
    [Trait("Plan", "EDGE-outfit-fit-19")]
    public void Legs_with_no_chest_and_no_twin_among_the_bodies_take_no_chest()
    {
        // Bibo+ — the twin — is not among the bodies: [IVCS] Bibo+ and Rue have chests, but not the same legs.
        var bodies = new[] { Body(FixPubes), Body(IvcsBibo), Body(Rue) }.ToList();
        var (_, skirt, bones) = Load(Geometric, Skirt);
        var pick = RefitCore.DetectSource(skirt, bones, "_dwn", Race, bodies, bodies[2].Dir, _ => true)!;
        Assert.Equal("fix pubes", pick.Dir);
        var notes = new List<string>();

        Assert.Null(RefitCore.ChestBeneathOf(skirt, pick, Race, bodies, CatalogOf(bodies), notes.Add));
        Assert.Contains(notes, n => n.Contains("no other body has the same legs", StringComparison.Ordinal));
    }

    [LocalDataFact(Rue)]
    [Trait("Plan", "EDGE-outfit-fit-16")]
    public void The_same_chest_or_a_refused_pair_leaves_the_legs_alone()
    {
        var rue = Body(Rue);
        var medium = rue.Catalog.For("_top", Race).First(o => o.Label == "None · Medium");
        var beneath = new RefitCore.ChestBeneath(rue.Dir, rue.Catalog, medium, 100);
        var notes = new List<string>();

        Assert.False(RefitCore.ChestBeneathPair(beneath, rue.Dir, rue.Catalog, medium, false, null, null, null, notes.Add,
                                                out _));
        // A legs model where a chest should be: the pair is refused.
        var legs = rue.Catalog.For("_dwn", Race)[0];
        Assert.False(RefitCore.ChestBeneathPair(beneath, rue.Dir, rue.Catalog, legs with { Slot = "_top" }, false, null,
                                                null, null, notes.Add, out _));
        notes.ForEach(output.WriteLine);
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Contains("left out", StringComparison.Ordinal));

        var small = rue.Catalog.For("_top", Race).First(o => o.Label == "None · Small");
        Assert.True(RefitCore.ChestBeneathPair(beneath, rue.Dir, rue.Catalog, small, false, null, null, null, notes.Add,
                                               out var pair));
        Assert.True(pair.ShapeOnly);
    }

    [LocalDataFact(Rue)]
    [Trait("Plan", "EDGE-outfit-fit-23")]
    public void A_legs_size_takes_its_own_chest_when_it_follows_one_and_the_middle_chest_otherwise()
    {
        var rue = Body(Rue);
        var chests = SupportedBodies.Of("rue", "_top", false)!;
        var first = chests.Sizes[0];
        Assert.Equal(RefitCore.Normal(first.Rel),
                     RefitCore.Normal(SizeSetRefit.ChestBeneathTo("rue", first, true, false, rue.Catalog, Race)!.Rel));
        var middle = chests.Sizes.First(s => s.Name == "M");
        var legsSize = SupportedBodies.Of("rue", "_dwn", false)!.Sizes[0];
        Assert.Equal(RefitCore.Normal(middle.Rel),
                     RefitCore.Normal(SizeSetRefit.ChestBeneathTo("rue", legsSize, false, false, rue.Catalog, Race)!.Rel));
    }

    [LocalDataFact(Heartthrob, Scarlet, Bibo, Neolithe)]
    [Trait("Plan", "AC-outfit-fit-7")]
    [Trait("Plan", "EDGE-outfit-fit-14")]
    public void Pants_that_stop_at_the_hips_take_no_chest()
    {
        var bodies = new[] { Body(Bibo), Body(Neolithe) }.ToList();
        foreach (var (mod, rel) in new[]
                 {
                     (Heartthrob, "bottom size/medium/chara/equipment/e6071/model/c0201e6071_dwn.mdl"),
                     (Scarlet, "pant size/neolithe m/chara/equipment/e0041/model/c0201e0041_dwn.mdl"),
                 })
        {
            var (_, pants, bones) = Load(mod, rel);
            var pick = RefitCore.DetectSource(pants, bones, "_dwn", Race, bodies, bodies[0].Dir, _ => true)!;
            var legs = CatalogOf(bodies)(pick.Dir)!;
            var chest = RefitCore.Beneath(legs.For("_top", Race), pick.Option) ?? legs.For("_top", Race)[0];
            int cells = RefitCore.WaistOverChest(pants, ModelPartReader.Read(File.ReadAllBytes(legs.PathOf(pick.Option)))!,
                                                 ModelPartReader.Read(File.ReadAllBytes(legs.PathOf(chest)))!);
            output.WriteLine($"{mod}: {cells} chest cells");
            Assert.True(cells < RefitCore.OverBellyGate);
            Assert.Null(RefitCore.ChestBeneathOf(pants, pick, Race, bodies, CatalogOf(bodies)));
        }
    }

    [LocalDataFact(Geometric, Bibo, FixPubes, Rue)]
    [Trait("Plan", "AC-outfit-fit-5")]
    [Trait("Plan", "EDGE-outfit-fit-17")]
    [Trait("Plan", "EDGE-outfit-fit-25")]
    public void Geometrics_skirt_onto_Rue_keeps_the_belly_inside_its_waist()
    {
        var bodies = new[] { Body(FixPubes), Body(Bibo), Body(Rue) }.ToList();
        var rue = bodies[2];
        var (bytes, skirt, bones) = Load(Geometric, Skirt);
        var pick = RefitCore.DetectSource(skirt, bones, "_dwn", Race, bodies, rue.Dir, _ => true)!;
        var beneath = RefitCore.ChestBeneathOf(skirt, pick, Race, bodies, CatalogOf(bodies))!;
        var legsTo = rue.Catalog.For("_dwn", Race).First(o => o.Label == "None · Watermelon Crushers - A");
        var chestTo = rue.Catalog.For("_top", Race).First(o => o.Label == "None · Medium");
        string legsPath = rue.Catalog.PathOf(legsTo), chestPath = rue.Catalog.PathOf(chestTo);
        Assert.Null(BodyRetarget.BuildPair("_dwn", CatalogOf(bodies)(pick.Dir)!.PathOf(pick.Option), legsPath, "_dwn", false,
                                           null, null, null, out var legsPair));
        Assert.Null(BodyRetarget.BuildPair("_top", beneath.Catalog.PathOf(beneath.Chest), chestPath, "_top", false, null,
                                           null, null, out var chestPair));

        var alone = RefitCore.PlanRefit(skirt, bytes, [legsPair], "_dwn", true, false, out _);
        var with = RefitCore.PlanRefit(skirt, bytes, [legsPair, chestPair with { ShapeOnly = true }], "_dwn", true, false, out _);

        var aloneParts = ModelPartReader.Read(alone.Model)!;
        var withParts = ModelPartReader.Read(with.Model)!;
        var before = BodySizeMatch.ClothAgainst(aloneParts, chestPath)!.Value;
        var after = BodySizeMatch.ClothAgainst(withParts, chestPath)!.Value;
        output.WriteLine($"inside Rue's chest: {before.Inside}/{before.Near} legs-only, {after.Inside}/{after.Near} with the chest "
                       + $"beneath; folds {alone.Report.Folded} -> {with.Report.Folded}");
        Assert.True(after.Inside * 2 <= before.Inside, $"{after.Inside} inside, {before.Inside} with the legs alone");
        // One folded triangle more at most, as measured and accepted (256 → 257).
        Assert.True(with.Report.Folded <= alone.Report.Folded + 1, $"{with.Report.Folded} folded, {alone.Report.Folded} legs-only");
        // Its shape only: no chest skin drawn, the author's normals, and the waist rigged by the legs.
        Assert.Equal(aloneParts.MeshSpans.Count, withParts.MeshSpans.Count);
        Assert.Equal(0, with.Report.Smoothed);
        Assert.Equal(aloneParts.Normals, withParts.Normals);
        var legsBones = new HashSet<string>(SecondSkinWriter.Parse(File.ReadAllBytes(legsPath)).BoneNames, StringComparer.Ordinal);
        var skin = ModelSkinReader.Read(with.Model, null, null)!;
        int offLegs = 0;
        for (int v = 0; v < skin.VertexCount; v++)
        {
            float w = 0f;
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                if (skin.BoneWeights[v * 8 + k] > 0f && !legsBones.Contains(skin.BoneNames[skin.BoneIndices[v * 8 + k]]))
                    w += skin.BoneWeights[v * 8 + k];
            if (w > 0.2f) offLegs++;
        }
        Assert.Equal(0, offLegs);
    }
}
