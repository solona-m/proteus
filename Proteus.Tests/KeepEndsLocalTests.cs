using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Shoes and gloves keeping their own feet and hands (<see cref="KeepEnds"/>), against the installed mods: Heartthrob's
/// heeled shoes with stockings in three leg sizes, and Hell for Leather's boots and long gloves. Skipped where they are
/// not installed. Everything is measured on the refit's own model, inside the pass — never on an export.
/// </summary>
public class KeepEndsLocalTests(ITestOutputHelper output)
{
    private const string Heartthrob = LocalData.Mods + ":Heartthrob - by Solona";
    private const string HellForLeather = LocalData.Mods + ":Hell for Leather - by Solona";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Tre = LocalData.Mods + ":hs-[YAS] Tre-5.5.3-8xB";
    private const string TheBodySe = LocalData.Mods + ":The Body SE";

    private const string Race = "0201";

    private static string HeartthrobShoe(string size)
        => LocalData.Path(Heartthrob + $"/bottom size/{size}/chara/equipment/e6070/model/c0201e6070_sho.mdl");

    private static string HellForLeatherModel(string rel) => LocalData.Path(HellForLeather + "/" + rel);

    private static (ModelParts Parts, byte[] Bytes) Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return (ModelPartReader.Read(bytes)!, bytes);
    }

    private static IReadOnlySet<string> BonesOf(byte[] bytes)
        => new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);

    /// <summary>The installed bodies the automatic refit would search, Bibo+ preferred.</summary>
    private static List<(string Dir, BodySizeCatalog Catalog)> Bodies(params string[] needs)
        => needs.Select(n => (Dir: Path.GetFileName(LocalData.Path(n)), Catalog: BodySizeCatalog.Read(LocalData.Path(n))))
                .ToList();

    private static Func<ModelParts, string, AutoRefitDecisions.Pick?> Detector(
        List<(string Dir, BodySizeCatalog Catalog)> bodies, IReadOnlySet<string> bones)
        => (view, slot) => RefitCore.DetectSource(view, bones, slot, Race, bodies, bodies[0].Dir, _ => true);

    private static Func<string, BodySizeCatalog?> CatalogOf(List<(string Dir, BodySizeCatalog Catalog)> bodies)
        => dir => bodies.FirstOrDefault(b => string.Equals(b.Dir, dir, StringComparison.OrdinalIgnoreCase)).Catalog;

    // ── which vertices are the hand or foot ──────────────────────────────────────────────────────────────

    public static TheoryData<string> InstalledEnds => new()
    {
        "heartthrob:small", "heartthrob:medium", "heartthrob:large",
        "hfl:common/7/c0201e0380_sho.mdl", "hfl:common/8/c0201e0380_sho.mdl",
        "hfl:bottom size/large/chara/equipment/e0380/model/c0201e0380_sho.mdl",
        "hfl:common/4/c0201e0380_glv.mdl", "hfl:common/5/c0201e0380_glv.mdl", "hfl:common/6/c0201e0380_glv.mdl",
    };

    private static (string Path, string Slot) Resolve(string which)
    {
        string path = which.StartsWith("heartthrob:") ? HeartthrobShoe(which["heartthrob:".Length..])
                                                      : HellForLeatherModel(which["hfl:".Length..]);
        return (path, path.EndsWith("_glv.mdl") ? "_glv" : "_sho");
    }

    [LocalDataTheory(Heartthrob, HellForLeather)]
    [MemberData(nameof(InstalledEnds))]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-19")]
    public void Installed_shoes_and_gloves_classify_cleanly(string which)
    {
        var (path, slot) = Resolve(which);
        var (parts, bytes) = Load(path);
        var reading = KeepEnds.Read(slot, parts, bytes);

        Assert.NotNull(reading);
        output.WriteLine($"{which}: {reading!.Ends.Count} of {reading.Vertices} vertices are the end, {reading.Band} in "
                       + $"the 40-60% band; limb view has {BodySizeMatch.SkinPoints(reading.LimbView).Count} skin points");
        Assert.NotEmpty(reading.Ends);
        Assert.True(reading.Band <= reading.Vertices * 0.05f,
                    $"{reading.Band} of {reading.Vertices} vertices are 40-60% end: the split is not clean");
    }

    public static TheoryData<string> BodiesWithEnds => new() { Bibo, Neolithe, Rue, Yab, Tre, TheBodySe };

    [LocalDataTheory(Bibo, Neolithe, Rue, Yab, Tre, TheBodySe)]
    [MemberData(nameof(BodiesWithEnds))]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-19")]
    public void Every_bodys_own_feet_and_hands_are_ends(string body)
    {
        // A body's feet model is not all foot: it runs up the shin, rigged to the calf (j_asi_c), and that part is the
        // leg's to refit. What must hold is that every vertex the body itself rigs mostly to the foot or hand is the end,
        // and that next to none of the legs or chest model is.
        var catalog = BodySizeCatalog.Read(LocalData.Path(body));
        string race = catalog.For("_sho", Race).Count > 0 ? Race : "0101";
        int checkedModels = 0;
        foreach (var (end, limb) in new[] { ("_sho", "_dwn"), ("_glv", "_top") })
        {
            if (catalog.For(end, race).FirstOrDefault() is { } endOption)
            {
                var (rigged, asEnd) = EndCount(catalog.PathOf(endOption), end);
                output.WriteLine($"{body} {end} {endOption.Label}: {asEnd} of {rigged} {end}-rigged vertices are the end");
                Assert.True(rigged > 0, $"{endOption.Label}: no vertex rigged to its {end} bones");
                Assert.True(asEnd >= rigged * 0.99f, $"{endOption.Label}: only {asEnd} of {rigged} read as the end");
                checkedModels++;
            }
            if (catalog.For(limb, race).FirstOrDefault() is { } limbOption)
            {
                var (_, asEnd) = EndCount(catalog.PathOf(limbOption), end, out int all);
                output.WriteLine($"{body} {limb} {limbOption.Label}: {asEnd} of {all} vertices read as a {end} end");
                Assert.True(asEnd <= all * 0.02f, $"{limbOption.Label}: {asEnd} of {all} read as a {end} end");
                checkedModels++;
            }
        }
        Assert.True(checkedModels > 0, $"{body} has no feet, hands, legs or chest at c{race}");
    }

    /// <summary>Of a body model's skin vertices, how many it rigs mostly to <paramref name="slot"/>'s end bones, and
    /// how many <see cref="BodyRetarget.EndVertices"/> calls the end.</summary>
    private static (int Rigged, int AsEnd) EndCount(string path, string slot) => EndCount(path, slot, out _);

    private static (int Rigged, int AsEnd) EndCount(string path, string slot, out int all)
    {
        var (parts, bytes) = Load(path);
        var skin = ModelSkinReader.Read(bytes, null, null)!;
        var ends = BodyRetarget.EndVertices(parts, skin, slot, [], out _);
        var isEnd = BodyRetarget.EndBonesOf(slot)!;
        var skinVerts = parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                             .SelectMany(p => p.Triangles).Distinct().ToList();
        all = skinVerts.Count;
        int rigged = 0, asEnd = 0;
        foreach (int v in skinVerts)
        {
            int best = 0;
            for (int k = 1; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                if (skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]
                    > skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + best])
                    best = k;
            if (isEnd(skin.BoneNames[skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + best]])) rigged++;
            if (ends.Contains(v)) asEnd++;
        }
        return (rigged, asEnd);
    }

    // ── the refit itself ─────────────────────────────────────────────────────────────────────────────────

    private const string ShoePath = "chara/equipment/e6070/model/c0201e6070_sho.mdl";

    /// <summary>Neolithe's plain medium legs: another body mod from the Heartthrob's Bibo+.</summary>
    private static BodyOption NeolitheLegs(BodySizeCatalog neo)
        => neo.For("_dwn", Race).FirstOrDefault(o => o.Label.EndsWith("SFW M", StringComparison.Ordinal))
           ?? neo.For("_dwn", Race)[0];

    private sealed record Refitted(KeepEnds.Reading Reading, ModelParts Parts, byte[] Bytes,
                                   KeepEnds.Refit Decision, BodyRetarget.Planned Plan, BodyRetarget.Planned Unheld,
                                   BodyRetarget.Planned LegsOnly);

    /// <summary>A Heartthrob shoe read and refitted onto <paramref name="targetOf"/>'s choice, exactly as the watcher
    /// does it after gathering — and, for comparison, the same refit with nothing held.</summary>
    private static Refitted RefitHeartthrob(string size, List<(string Dir, BodySizeCatalog Catalog)> bodies,
                                            string targetDir, Func<BodySizeCatalog, BodyOption> targetOf)
    {
        var (parts, bytes) = Load(HeartthrobShoe(size));
        var bones = BonesOf(bytes);
        var reading = KeepEnds.Read("_sho", parts, bytes)!;
        var catalogOf = CatalogOf(bodies);
        var found = KeepEnds.Find(reading, parts, bones, Race, Detector(bodies, bones), catalogOf);
        var targetCat = catalogOf(targetDir)!;
        var legs = targetOf(targetCat);
        var decision = KeepEnds.Decide(found, reading, bones, Race, targetDir, catalogOf,
                                       slot => slot == "_dwn" ? legs
                                             : AutoRefitDecisions.Companion(targetCat.For(slot, Race), legs, null));
        var refit = Assert.IsType<KeepEnds.Refit>(decision);

        var sourceCat = catalogOf(refit.Source.Dir)!;
        Assert.Null(BodyRetarget.BuildPair("_dwn", sourceCat.PathOf(refit.Source.Option), targetCat.PathOf(refit.Target),
                                           "_dwn", false, null, null, null, out var pair));
        Assert.NotNull(refit.EndFrom);
        Assert.NotNull(refit.EndTo);
        Assert.Null(BodyRetarget.BuildPair("_sho", sourceCat.PathOf(refit.EndFrom!), targetCat.PathOf(refit.EndTo!),
                                           "_sho", false, null, null, null, out var feet));
        bool across = !string.Equals(refit.Source.Dir, targetDir, StringComparison.OrdinalIgnoreCase);
        var plan = KeepEnds.Plan(parts, bytes, reading, pair, feet, across);
        var unheld = BodyRetarget.Plan(parts, bytes, [pair, feet with { ShapeOnly = true }], "_sho",
                                       held: new HashSet<int>(), replaceSkin: across, acrossBodies: across,
                                       clearBody: false, cutHidden: true,
                                       keepShape: RefitCore.ShapePieces(parts, RefitCore.DefaultKeepShape(parts)),
                                       swapSlot: "_dwn");
        var legsOnly = KeepEnds.Plan(parts, bytes, reading, pair, null, across);
        return new Refitted(reading, parts, bytes, refit, plan, unheld, legsOnly);
    }

    /// <summary>
    /// The medium shoe refitted onto Neolithe's SFW M legs (<see cref="NeolitheMedium"/>) and onto Bibo+ Large, its own
    /// body's (<see cref="BiboLarge"/>). Each is three plans and several tests read it, so it is made once a run.
    /// <para/>
    /// Shared, so read-only: a test that would change a model or a set it holds copies it first. A refit that throws keeps
    /// its exception, and every test that reads it reports the same failure. Only ever read inside a
    /// <see cref="LocalDataFactAttribute"/> test body, so without the mods it is never made.
    /// </summary>
    private static readonly Lazy<Refitted> NeolitheMedium = new(() =>
    {
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        return RefitHeartthrob("medium", bodies, bodies[1].Dir, NeolitheLegs);
    });

    /// <inheritdoc cref="NeolitheMedium"/>
    private static readonly Lazy<Refitted> BiboLarge = new(() =>
    {
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        return RefitHeartthrob("medium", bodies, bodies[0].Dir,
                               c => c.For("_dwn", Race).First(o => o.Label.EndsWith("Large", StringComparison.Ordinal)));
    });

    /// <summary>The model's skin positions, exactly, as a set — what a kept foot has to be found in.</summary>
    private static HashSet<(float, float, float)> SkinPositions(byte[] model, Func<int, bool>? which = null)
    {
        var parts = ModelPartReader.Read(model)!;
        var set = new HashSet<(float, float, float)>();
        foreach (int v in parts.Parts.Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                               .SelectMany(p => p.Triangles).Distinct())
            if (which == null || which(v))
                set.Add((parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]));
        return set;
    }

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-1")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-1")]
    [Trait("Speed", "Slow")]
    public void Heartthrob_keeps_its_foot_exactly_when_refitted_to_another_body()
    {
        var done = NeolitheMedium.Value;

        // Not a point of the foot moved, skin or shoe.
        foreach (int v in done.Reading.Ends)
            Assert.Equal(default, done.Plan.Edit.DeltaAt(v));

        // And every point of the kept foot's skin is in the model written, where it was.
        var footSkin = SkinPositions(done.Bytes, done.Reading.Ends.Contains);
        var written = SkinPositions(done.Plan.Model);
        int missing = footSkin.Count(p => !written.Contains(p));
        output.WriteLine($"swap {done.Plan.Report.Swap}; {footSkin.Count} foot skin points, {missing} missing");
        Assert.Equal(0, missing);
        Assert.True(done.Plan.Report.Swap?.Posed > 0 || done.Plan.Report.Swap?.Kept > 0,
                    "the foot's skin mesh should have been kept, not swapped");

        // Kept in its rig too: across body mods the cloth takes the new body's weights, but the foot follows the bones
        // its author gave it, the new feet's shape riding along notwithstanding.
        var before = ModelSkinReader.Read(done.Bytes, null, null)!;
        var after = ModelSkinReader.Read(done.Plan.Model, null, null)!;
        Assert.Equal(before.VertexCount, after.VertexCount);   // nothing dropped: the kept meshes are numbered as before
        int changed = 0;
        foreach (int v in done.Reading.Ends)
            if (!SameRig(before, after, v)) changed++;
        output.WriteLine($"{changed} of {done.Reading.Ends.Count} foot vertices changed bones or weights");
        Assert.Equal(0, changed);
    }

    /// <summary>Vertex <paramref name="v"/> follows the same bones by the same weights (to a byte) in both rigs.</summary>
    private static bool SameRig(XivLiveMesh.SkinnedMesh a, XivLiveMesh.SkinnedMesh b, int v) => SameRigAt(a, v, b, v);

    /// <summary>Vertex <paramref name="v"/> of one rig and <paramref name="w"/> of another follow the same bones by the
    /// same weights (to a byte).</summary>
    private static bool SameRigAt(XivLiveMesh.SkinnedMesh a, int v, XivLiveMesh.SkinnedMesh b, int w)
    {
        static Dictionary<string, float> Of(XivLiveMesh.SkinnedMesh m, int vertex)
        {
            var weights = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float weight = m.BoneWeights[vertex * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                if (weight <= 0f) continue;
                string bone = m.BoneNames[m.BoneIndices[vertex * XivLiveMesh.SkinnedMesh.MaxInfluences + k]];
                weights[bone] = weights.GetValueOrDefault(bone) + weight;
            }
            return weights;
        }
        var x = Of(a, v);
        var y = Of(b, w);
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out float o) && MathF.Abs(o - kv.Value) <= 1.5f / 255f);
    }

    /// <summary>The ankle edge never stretches more than this (15 mm): beyond it the seam is a hole, not a crease.</summary>
    private const float SeamLimit = 0.015f;

    /// <summary>
    /// Onto another body mod, and onto another size of the shoe's own body: holding the foot folds no more than refitting
    /// with nothing held, and the seam it leaves at the ankle stays a crease.
    /// <para/>
    /// Rigid by choice: the foot does not follow the ankle, so where two bodies' ankles differ the seam stretches. The
    /// foot and its leg are one skin mesh here, so the seam is the edges from the kept foot to the leg beside it: measured
    /// inside the pass, on the solve's own move, as each edge's length now against as authored.
    /// </summary>
    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-6")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-4")]
    [Trait("Speed", "Slow")]
    public void Holding_the_foot_adds_no_folds_and_the_ankle_seam_holds()
    {
        foreach (var (name, done) in new[] { ("Neolithe SFW M", NeolitheMedium.Value), ("Bibo+ Large", BiboLarge.Value) })
        {
            output.WriteLine($"{name}: {done.Plan.Report.Folded} folded holding the foot, {done.Unheld.Report.Folded} "
                           + $"without; moved up to {done.Plan.Report.WorstMove * 1000f:F1} mm");
            Assert.True(done.Plan.Report.Folded <= done.Unheld.Report.Folded,
                        $"{name}: holding the foot folded {done.Plan.Report.Folded} triangles, {done.Unheld.Report.Folded} without");

            foreach (int v in done.Reading.Ends) Assert.Equal(default, done.Plan.Edit.DeltaAt(v));
            var (edges, worst) = KeepEnds.SeamStretch(done.Parts, done.Reading.Ends, done.Plan);
            var (_, legsOnly) = KeepEnds.SeamStretch(done.Parts, done.Reading.Ends, done.LegsOnly);
            output.WriteLine($"{name}: {edges} ankle edges from the kept foot to the leg; the most stretched by "
                           + $"{worst * 1000f:F1} mm (refitted on the legs alone, {legsOnly * 1000f:F1} mm)"
                           + (worst > KeepEnds.SeamWarn ? " — LOOK AT THIS SEAM IN GAME" : ""));
            Assert.True(edges > 0, "no skin edge runs from the kept foot to the leg");
            Assert.True(worst <= SeamLimit, $"{name}: the ankle stretched by {worst * 1000f:F1} mm");
            Assert.True(worst <= legsOnly + 1e-4f,
                        $"{name}: the feet's shape stretched the ankle more ({worst * 1000f:F1} mm) than the legs alone");
        }
    }

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-6")]
    [Trait("Speed", "Slow")]
    public void KeepEnds_e2e_Heartthrob_detect_plan_save()
    {
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var neo = bodies[1];
        var done = NeolitheMedium.Value;

        // Into a copy of the mod: its manifest and the shoe sizes. Never the installed mod.
        string root = Path.Combine(Path.GetTempPath(), "proteus-keepends-" + Guid.NewGuid().ToString("N"));
        try
        {
            string mod = LocalData.Path(Heartthrob);
            Directory.CreateDirectory(root);
            File.Copy(Path.Combine(mod, "meta.json"), Path.Combine(root, "meta.json"));
            foreach (string size in new[] { "small", "medium", "large" })
            {
                string rel = Path.Combine("bottom size", size, "chara", "equipment", "e6070", "model", "c0201e6070_sho.mdl");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!);
                File.Copy(Path.Combine(mod, rel), Path.Combine(root, rel));
            }
            var groupsBefore = (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name).ToList();
            var redirects = PenumbraModMeta.ReadAllRedirects(root);
            string worn = "bottom size/medium/chara/equipment/e6070/model/c0201e6070_sho.mdl";

            string? group = RefitCore.SwitchingGroup(redirects, ShoePath, null, RefitCore.SingleGroups(root), worn,
                                                     RefitCore.MultiGroups(root));
            Assert.Equal("Bottom Size", group);
            string option = RefitCore.OptionName("Neolithe", [done.Decision.Target.Label]);
            string? cutFrom = BodyRetargetWriter.OptionOfFile(redirects, worn, group!);
            var outcome = BodyRetargetWriter.Save(root, group!, ShoePath, neo.Dir, "Bibo+ — " + done.Decision.Source.Option.Label,
                                                  [new BodyRetargetWriter.Refit(option, done.Plan.Model, done.Decision.Target.Label)],
                                                  cutFrom, null, auto: true);
            Assert.True(outcome.Ok, outcome.Message);

            // Appended last in the author's size group; no group moved.
            var options = PenumbraModMeta.TryReadFileOptions(root, group!)!;
            Assert.Equal(option, options[^1].Name);
            Assert.Equal(groupsBefore, (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => g.Name).ToList());

            // The model saved keeps the foot exactly.
            string savedRel = options[^1].Files.First(f => string.Equals(f.Key, ShoePath, StringComparison.OrdinalIgnoreCase)).Value;
            var saved = File.ReadAllBytes(Path.Combine(root, savedRel.Replace('/', Path.DirectorySeparatorChar)));
            var footSkin = SkinPositions(done.Bytes, done.Reading.Ends.Contains);
            var written = SkinPositions(saved);
            Assert.Equal(0, footSkin.Count(p => !written.Contains(p)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [LocalDataFact(Heartthrob, Bibo)]
    [Trait("Plan", "AC-keep-hand-foot-skin-7")]
    [Trait("Speed", "Slow")]
    public void Heartthrob_authors_own_size_found_by_its_legs()
    {
        // Choosing Bibo+ Medium legs, the shoe's author already ships that size: compared by its legs, "Medium" is found.
        // Compared whole, the heeled foot — on no body — holds the share below exact and nothing is.
        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        var medium = bibo.For("_dwn", Race).First(o => o.Label.EndsWith("Medium", StringComparison.Ordinal));
        string mod = LocalData.Path(Heartthrob);
        var sizes = PenumbraModMeta.ReadAllRedirects(mod)
                                   .Where(r => r.GamePath == ShoePath && r.Source.StartsWith("Bottom Size / "))
                                   .Select(r => (r.Source["Bottom Size / ".Length..], r.File))
                                   .ToList();

        Assert.Equal("Medium", AutoRefitWatcher.AuthorSize(mod, sizes, bibo, medium, "_dwn", Race,
                                                           (b, m) => KeepEnds.Read("_sho", m, b)?.LimbView));
        Assert.Null(AutoRefitWatcher.AuthorSize(mod, sizes, bibo, medium, "_dwn", Race));
    }

    [LocalDataFact(HellForLeather, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-11")]
    public void HellForLeather_boots_are_read_as_before()
    {
        // Leather from toe to thigh, no skin anywhere: there is no body to read, now as before ("carries no body skin"),
        // and nothing is written. A boot that showed skin at the foot would have its shaft read by its cloth instead —
        // see KeepEndsTests.A_cloth_only_boot_shaft_is_read_by_its_cloth.
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        foreach (string rel in new[] { "common/7/c0201e0380_sho.mdl", "common/8/c0201e0380_sho.mdl" })
        {
            var (parts, bytes) = Load(HellForLeatherModel(rel));
            var bones = BonesOf(bytes);
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            var found = KeepEnds.Find(reading, parts, bones, Race, Detector(bodies, bones), CatalogOf(bodies));
            output.WriteLine($"{rel}: {BodySizeMatch.SkinPoints(parts).Count} skin points; route {found.Route}");
            if (BodySizeMatch.SkinPoints(parts).Count == 0)
                Assert.Equal(KeepEnds.Route.NoSkin, found.Route);
            else
                Assert.Contains(found.Route, new[] { KeepEnds.Route.Skin, KeepEnds.Route.Cloth, KeepEnds.Route.SkinOnly });
        }
    }

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-8")]
    [Trait("Speed", "Slow")]
    public void The_kept_foot_is_drawn_with_the_new_bodys_skin()
    {
        // Neither moved nor swapped, the foot's skin is still drawn the way the new body draws its own — the step the
        // watcher runs on every refit of a shoe — so the body's tattoos and texture reach it.
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var neo = bodies[1];
        var done = NeolitheMedium.Value;
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var uv = new UVRemapService(log, ".");
        var sourceCat = CatalogOf(bodies)(done.Decision.Source.Dir)!;
        string targetPath = neo.Catalog.PathOf(done.Decision.Target);

        var model = RefitCore.MatchSkinToBody(done.Plan.Model, sourceCat.PathOf(done.Decision.Source.Option), targetPath,
                                              uv, false, log);

        var bodySkins = SecondSkinWriter.Parse(File.ReadAllBytes(targetPath)).MatNames
                                        .Where(SecondSkinWriter.IsBodySkinMaterial).ToHashSet();
        var drawn = ModelPartReader.Read(model)!.Parts
                                   .Where(p => p.Island < 0 && SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                   .Select(p => p.Material).Distinct().ToList();
        output.WriteLine($"body draws {string.Join(", ", bodySkins)}; the shoe now draws {string.Join(", ", drawn)}");
        Assert.NotEmpty(drawn);
        Assert.All(drawn, m => Assert.Contains(m, bodySkins));
        // And the foot is where it was.
        var footSkin = SkinPositions(done.Bytes, done.Reading.Ends.Contains);
        var written = SkinPositions(model);
        Assert.Equal(0, footSkin.Count(p => !written.Contains(p)));
    }

    [LocalDataFact(LocalData.GameData, Bibo)]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-13")]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-14")]
    [Trait("Speed", "Slow")]
    public void Game_boots_baked_for_a_woman_keep_their_feet_and_read_on_the_game_legs()
    {
        // A man's boots from the game, baked onto a woman as the watcher bakes male-only gear: the ends are found on the
        // baked model, the game's own gear is read on the game's own legs, and the refit holds the baked foot.
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var pbd = new XivLiveMesh.PbdFile(data.GetFile("chara/xls/boneDeformer/human.pbd")!.Data);
        string[] candidates =
        [
            "chara/equipment/e0100/model/c0101e0100_sho.mdl", "chara/equipment/e0005/model/c0101e0005_sho.mdl",
            "chara/equipment/e0036/model/c0101e0036_sho.mdl", "chara/equipment/e0001/model/c0101e0001_sho.mdl",
        ];
        string path = candidates.First(p => data.GetFile(p) != null);
        var baked = RacialModelBake.Bake(data.GetFile(path)!.Data, 101, 201, pbd, _ => null, out string why);
        Assert.True(baked != null, why);
        var parts = ModelPartReader.Read(baked!)!;
        var reading = KeepEnds.Read("_sho", parts, baked!);
        Assert.NotNull(reading);
        Assert.NotEmpty(reading!.Ends);

        var vanilla = VanillaBodyCatalog.Read(Race, p => data.GetFile(p)?.Data);
        AutoRefitDecisions.Pick? GameBody(ModelParts _, string slot)
            => vanilla.For(slot, Race) is { Count: > 0 } options
                ? new AutoRefitDecisions.Pick(VanillaBodyCatalog.Key, options[0], BodySizeMatch.Confidence.Exact, false)
                : null;
        var bones = BonesOf(baked!);
        var found = KeepEnds.Find(reading, parts, bones, Race, GameBody,
                                  dir => dir == VanillaBodyCatalog.Key ? vanilla : null);
        output.WriteLine($"{path}: {reading.Ends.Count} of {reading.Vertices} vertices the foot; route {found.Route}, "
                       + $"{found.Pick?.Option.Slot}");
        if (found.Route is KeepEnds.Route.Skin or KeepEnds.Route.Cloth)
        {
            Assert.Equal(VanillaBodyCatalog.Key, found.Pick!.Dir);
            Assert.Equal("_dwn", found.Pick.Option.Slot);

            var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
            var legs = bibo.For("_dwn", Race).First(o => o.Label.EndsWith("Medium", StringComparison.Ordinal));
            var uv = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
            Assert.Null(BodyRetarget.BuildPair("_dwn", vanilla.PathOf(found.Pick.Option), bibo.PathOf(legs), "_dwn", false,
                                               null, null, uv, out var pair));
            var plan = KeepEnds.Plan(parts, baked!, reading, pair, null, across: true);
            foreach (int v in reading.Ends) Assert.Equal(default, plan.Edit.DeltaAt(v));
        }
        else Assert.Contains(found.Route, new[] { KeepEnds.Route.SkinOnly, KeepEnds.Route.NoSkin });
    }

    [LocalDataFact(Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-3")]
    [Trait("Speed", "Slow")]
    public void Installed_long_gloves_are_read_on_their_arms()
    {
        // Every chest size of a body has the same arms, so a glove's forearm might tie between them and read as unsure.
        // Read every installed woman's glove that carries forearm skin, as the watcher would.
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var bodyDirs = bodies.Select(b => b.Dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routes = new Dictionary<KeepEnds.Route, int>();
        int read = 0;
        foreach (string path in Directory.EnumerateFiles(LocalData.Path(LocalData.Mods), "c0201e*_glv.mdl",
                                                         SearchOption.AllDirectories))
        {
            string mod = Path.GetRelativePath(LocalData.Path(LocalData.Mods), path).Split(Path.DirectorySeparatorChar)[0];
            if (bodyDirs.Contains(mod)) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); } catch (IOException) { continue; }
            if (ModelPartReader.Read(bytes) is not { } parts || KeepEnds.Read("_glv", parts, bytes) is not { } reading)
                continue;
            if (BodySizeMatch.SkinPoints(reading.LimbView).Count < 100) continue;
            var bones = BonesOf(bytes);
            var found = KeepEnds.Find(reading, parts, bones, Race, Detector(bodies, bones), CatalogOf(bodies));
            routes[found.Route] = routes.GetValueOrDefault(found.Route) + 1;
            output.WriteLine($"{Path.GetRelativePath(LocalData.Path(LocalData.Mods), path)}: route {KeepEnds.Letter(found.Route)}"
                           + $" {found.Pick?.Dir} / {found.Pick?.Option.Label} ({found.Pick?.Confidence})");
            if (++read == 25) break;
        }
        output.WriteLine($"{read} gloves with forearm skin: "
                       + string.Join(", ", routes.Select(r => $"{KeepEnds.Letter(r.Key)} {r.Value}")));
        Assert.True(read > 0, "no installed glove carries forearm skin to read");
        Assert.True(routes.GetValueOrDefault(KeepEnds.Route.Unsure) < read,
                    "every glove with forearm skin read as unsure: the arms cannot be told apart");
    }

    private const string SheerElegance =
        LocalData.Mods + ":BiboPlus Sheer Elegance/Chest size/Small/chara/equipment/e6010/model/c0201e6010_top.mdl";

    [LocalDataFact(SheerElegance, Bibo, Neolithe)]
    [Trait("Plan", "AC-keep-hand-foot-skin-3")]
    [Trait("Speed", "Slow")]
    public void A_glove_is_never_lifted_like_a_breast()
    {
        // The underbust lift is for tops. A Bibo+ corset refitted onto Neolithe's chest is where it acts: worn as a top,
        // the lift changes the refit. Put through the same refit as gloves are — worn in the hands' slot, the chest
        // model's skin the one swapped — it changes nothing, as no glove may be lifted like a breast.
        var (parts, bytes) = Load(LocalData.Path(SheerElegance));
        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        var neo = BodySizeCatalog.Read(LocalData.Path(Neolithe));
        var from = bibo.For("_top", Race).First(o => o.Label.Contains("Small", StringComparison.Ordinal));
        var to = neo.For("_top", Race).FirstOrDefault(o => o.Label.EndsWith("SFW M", StringComparison.Ordinal))
                 ?? neo.For("_top", Race)[0];
        var uv = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        Assert.Null(BodyRetarget.BuildPair("_top", bibo.PathOf(from), neo.PathOf(to), "_top", false, null, null, uv,
                                           out var chest));
        var skin = ModelSkinReader.Read(bytes, null, null);

        BodyRetarget.Solved Solve(string worn, bool noLift) => BodyRetarget.WithTuning(
            new BodyRetarget.Tuning(NoUnderbustLift: noLift),
            () => BodyRetarget.Solve(parts, [chest], worn, replaceSkin: true, garmentSkin: skin,
                                     swapSlot: worn == "_glv" ? "_top" : null));

        int Differ(BodyRetarget.Solved a, BodyRetarget.Solved b)
            => Enumerable.Range(0, parts.Positions.Length / 3).Count(v => !a.Edit.DeltaAt(v).Equals(b.Edit.DeltaAt(v)));

        int asTop = Differ(Solve("_top", false), Solve("_top", true));
        int asGloves = Differ(Solve("_glv", false), Solve("_glv", true));
        output.WriteLine($"the lift moved {asTop} vertices worn as a top, {asGloves} worn as gloves");
        Assert.True(asTop > 0, "the lift did nothing to this top either, so the glove case proves nothing");
        Assert.Equal(0, asGloves);
    }

    // ── the Studio's Body size tool, keeping the end (keep-ends-studio) ──────────────────────────────────

    private const string LavaBod = LocalData.Mods + ":LavaBod+ 2.2";
    private const string CleoGloves = LocalData.Mods + ":Cleo - by Solona/common/4/c0201e0361_glv.mdl";
    private const string ShiftShoe = "e6219_sho.mdl";

    private sealed record Studied(ModelParts Parts, byte[] Bytes, KeepEnds.Reading Reading, KeepEnds.StudioReading Read,
                                  BodyOption From, BodyOption Target, BodyRetarget.Planned Plan,
                                  BodyRetarget.Planned Unheld);

    /// <summary>
    /// A shoe or glove through the Body size tool with its end kept: the panel's worker's own planning
    /// (<see cref="KeepEnds.StudioPlan"/>) with the panel's defaults (new body's skin on, cut, nothing cleared), the limb
    /// read against the chosen source as the panel reads it — and, for comparison, the same pairs with nothing held.
    /// </summary>
    private static Studied StudioRefit(byte[] bytes, string slot, string sourceNeed, string targetNeed,
                                       Func<BodySizeCatalog, BodyOption, BodyOption>? targetOf = null,
                                       IReadOnlySet<int>? alsoHeld = null)
    {
        var parts = ModelPartReader.Read(bytes)!;
        var bones = BonesOf(bytes);
        var source = BodySizeCatalog.Read(LocalData.Path(sourceNeed));
        var target = BodySizeCatalog.Read(LocalData.Path(targetNeed));
        bool across = !string.Equals(sourceNeed, targetNeed, StringComparison.OrdinalIgnoreCase);
        var pieces = RefitCore.ShapePieces(parts, new HashSet<string>());
        var reading = KeepEnds.Read(slot, parts, bytes, pieces)!;
        var read = KeepEnds.StudioLimb(reading, parts, bones, Race, Path.GetFileName(LocalData.Path(sourceNeed)), source);
        Assert.True(read.Pick != null, $"route {read.Route} picked nothing");
        var from = read.Pick!;
        var to = targetOf?.Invoke(target, from)
                 ?? AutoRefitDecisions.Companion(target.For(reading.Limb, Race), from, null)!;
        var uv = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var input = new KeepEnds.StudioPlanInput(parts, bytes, slot, pieces, source, target, Race, false, from, [to],
                                                 SameBodyMod: !across, FromCloth: read.Shown?.FromCloth == true,
                                                 ReplaceSkin: true, Across: across, ClearBody: false, Held: alsoHeld,
                                                 Shared: null, _ => (null, null), s => s, "test");
        var studied = KeepEnds.StudioPlan(input, (p, _) => p, uv, log);
        Assert.True(studied.Planned is { Count: 1 }, $"planned nothing: {studied.Error}{studied.Notice}");
        var plan = studied.Planned![0].Planned;

        var (endFrom, endTo) = KeepEnds.EndPair(source, target, slot, Race, from, to);
        Assert.Null(KeepEnds.BuildPairs(reading, source.PathOf(from), target.PathOf(to),
                                        endFrom == null ? null : source.PathOf(endFrom),
                                        endTo == null ? null : target.PathOf(endTo), false, _ => (null, null), s => s, uv,
                                        null, out var limb, out var end));
        List<BodyRetarget.SlotPair> pairs = [limb];
        if (end is { } e) pairs.Add(e with { ShapeOnly = true });
        var unheld = BodyRetarget.Plan(parts, bytes, pairs, slot, held: new HashSet<int>(), replaceSkin: true,
                                       acrossBodies: across, clearBody: false, cutHidden: true,
                                       keepShape: reading.FreePieces, swapSlot: reading.Limb);
        return new Studied(parts, bytes, reading, read, from, to, plan, unheld);
    }

    /// <summary>How far each of <paramref name="vertices"/> sits from the nearest vertex of a body model, in metres.</summary>
    private static float[] GapTo(string bodyPath, IEnumerable<System.Numerics.Vector3> points)
    {
        var body = ModelPartReader.Read(File.ReadAllBytes(bodyPath))!;
        var verts = Enumerable.Range(0, body.Positions.Length / 3)
                              .Select(i => new System.Numerics.Vector3(body.Positions[i * 3], body.Positions[i * 3 + 1],
                                                                       body.Positions[i * 3 + 2]))
                              .ToArray();
        return points.Select(p => verts.Min(v => System.Numerics.Vector3.Distance(v, p))).ToArray();
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0f : sorted[sorted.Length / 2];
    }

    [LocalDataFact(LocalData.ShiftPack, Yab, LavaBod)]
    [Trait("Plan", "AC-keep-ends-studio-1")]
    [Trait("Plan", "EDGE-keep-ends-studio-20")]
    [Trait("Speed", "Slow")]
    public void Studio_Shift_YAB_WC_onto_LavaBod_keeps_the_heeled_foot_and_refits_the_stocking()   // [AC-keep-ends-studio-1] [EDGE-keep-ends-studio-20]
    {
        var bytes = LocalData.PackEntryEndingWith(LocalData.ShiftPack, ShiftShoe);
        var done = StudioRefit(bytes, "_sho", Yab, LavaBod);
        output.WriteLine($"route {KeepEnds.Letter(done.Read.Route)}, made for {done.From.Label}, onto {done.Target.Label}; "
                       + $"{done.Reading.Ends.Count} of {done.Reading.Vertices} vertices kept; swap {done.Plan.Report.Swap}");
        Assert.Contains(done.Read.Route, new[] { KeepEnds.Route.Skin, KeepEnds.Route.Cloth });
        Assert.Equal("_dwn", done.From.Slot);

        // Not a point of the heeled foot moved, skin, shoe or stocking: the bug moved them 10.8, 27 and 28 mm.
        foreach (int v in done.Reading.Ends) Assert.Equal(default, done.Plan.Edit.DeltaAt(v));
        var footSkin = SkinPositions(bytes, done.Reading.Ends.Contains);
        var written = SkinPositions(done.Plan.Model);
        Assert.Equal(0, footSkin.Count(p => !written.Contains(p)));
        Assert.True(done.Plan.Report.Swap?.Posed > 0 || done.Plan.Report.Swap?.Kept > 0,
                    "the foot's skin mesh should have been kept, not swapped");
        // In its own rig too. The swap renumbers the model (the limb's skin is cut and the new legs' laid in), so each kept
        // vertex is found again where it stands — it did not move — and its bones compared there.
        var before = ModelSkinReader.Read(bytes, null, null)!;
        var after = ModelSkinReader.Read(done.Plan.Model, null, null)!;
        var afterParts = ModelPartReader.Read(done.Plan.Model)!;
        var at = new Dictionary<(float, float, float), int>();
        for (int v = afterParts.Positions.Length / 3 - 1; v >= 0; v--)
            at[(afterParts.Positions[v * 3], afterParts.Positions[v * 3 + 1], afterParts.Positions[v * 3 + 2])] = v;
        int rigChanged = 0, found = 0;
        foreach (int v in done.Reading.Ends)
        {
            if (!at.TryGetValue((done.Parts.Positions[v * 3], done.Parts.Positions[v * 3 + 1], done.Parts.Positions[v * 3 + 2]),
                                out int w)) continue;
            found++;
            if (!SameRigAt(before, v, after, w)) rigChanged++;
        }
        output.WriteLine($"{found} of {done.Reading.Ends.Count} kept vertices found again; {rigChanged} changed bones");
        Assert.Equal(done.Reading.Ends.Count, found);
        Assert.Equal(0, rigChanged);

        // The stocking follows the new legs up the thigh: its gap to LavaBod's legs after is its gap to YAB's before.
        var yab = BodySizeCatalog.Read(LocalData.Path(Yab));
        var lava = BodySizeCatalog.Read(LocalData.Path(LavaBod));
        var parts = done.Parts;
        var thigh = parts.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                         .SelectMany(p => p.Triangles).Distinct()
                         .Where(v => !done.Reading.Ends.Contains(v) && parts.Positions[v * 3 + 1] > 0.35f)
                         .ToList();
        Assert.NotEmpty(thigh);
        System.Numerics.Vector3 At(int v, BodyRetarget.Planned? plan = null)
        {
            var p = new System.Numerics.Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]);
            if (plan == null) return p;
            var d = plan.Edit.DeltaAt(v);
            return p + new System.Numerics.Vector3(d.X, d.Y, d.Z);
        }
        var gapBefore = GapTo(yab.PathOf(done.From), thigh.Select(v => At(v)));
        var gapAfter = GapTo(lava.PathOf(done.Target), thigh.Select(v => At(v, done.Plan)));
        float drift = Median(gapBefore.Zip(gapAfter, (a, b) => MathF.Abs(a - b)));
        output.WriteLine($"{thigh.Count} thigh stocking vertices: median gap change {drift * 1000f:F2} mm; folded "
                       + $"{done.Plan.Report.Folded} (unheld {done.Unheld.Report.Folded}); moved up to "
                       + $"{done.Plan.Report.WorstMove * 1000f:F1} mm");
        Assert.True(drift <= 0.001f, $"the stocking's gap to the legs changed by {drift * 1000f:F2} mm");
        Assert.True(done.Plan.Report.Folded <= done.Unheld.Report.Folded);

        // [EDGE-keep-ends-studio-20] The seam at the heeled ankle stays a crease.
        var (edges, worst) = KeepEnds.SeamStretch(done.Parts, done.Reading.Ends, done.Plan);
        output.WriteLine($"{edges} ankle edges; the most stretched by {worst * 1000f:F1} mm"
                       + (worst > KeepEnds.SeamWarn ? " — LOOK AT THIS SEAM IN GAME" : ""));
        Assert.True(edges > 0, "no skin edge runs from the kept foot to the leg");
        Assert.True(worst <= SeamLimit, $"the ankle stretched by {worst * 1000f:F1} mm");
    }

    [LocalDataFact(LocalData.ShiftPack, Yab, LavaBod)]
    [Trait("Plan", "EDGE-keep-ends-studio-7")]
    [Trait("Speed", "Slow")]
    public void Studio_unticked_foot_skin_keeps_the_stocking_beside_it_unfolded()   // [EDGE-keep-ends-studio-7]
    {
        // ND-7 (a): the parts unticked join the hold. Beside held skin there is no gap correction, so the stocking there
        // keeps only its transfer move. Onto LavaBod+ legs other than YAB WC's twin, so the legs really differ; what the
        // code guarantees is checked (the skin held, no new folds), the gap change only printed. (YAB to Bibo+ legs is
        // refused outright: the two bodies' legs do not correspond.)
        var bytes = LocalData.PackEntryEndingWith(LocalData.ShiftPack, ShiftShoe);
        var parts = ModelPartReader.Read(bytes)!;
        var skin = parts.Parts.Where(p => p.Mesh == 0).SelectMany(p => p.Triangles).ToHashSet();
        BodyOption Other(BodySizeCatalog c, BodyOption from)
            => c.For("_dwn", Race).Last(o => !string.Equals(o.Name, from.Name, StringComparison.OrdinalIgnoreCase));

        var free = StudioRefit(bytes, "_sho", Yab, LavaBod, Other);
        var held = StudioRefit(bytes, "_sho", Yab, LavaBod, Other, alsoHeld: skin);

        foreach (int v in skin) Assert.Equal(default, held.Plan.Edit.DeltaAt(v));
        float most = Enumerable.Range(0, parts.Positions.Length / 3).Where(v => !skin.Contains(v))
                               .Max(v =>
                               {
                                   var a = held.Plan.Edit.DeltaAt(v);
                                   var b = free.Plan.Edit.DeltaAt(v);
                                   return new System.Numerics.Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z).Length();
                               });
        output.WriteLine($"onto {held.Target.Label}: folded {held.Plan.Report.Folded} with the skin unticked, "
                       + $"{free.Plan.Report.Folded} without; the rest moved at most {most * 1000f:F1} mm differently");
        Assert.True(held.Plan.Report.Folded <= free.Plan.Report.Folded,
                    $"unticking the skin folded {held.Plan.Report.Folded} triangles, {free.Plan.Report.Folded} without");
    }

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-ends-studio-1")]
    [Trait("Speed", "Slow")]
    public void Studio_path_equals_the_automatic_refit_when_options_match()   // [AC-keep-ends-studio-1]
    {
        // The Studio's plan with the automatic refit's settings — new skin only across body mods, nothing cleared, no
        // hold of the user's and no other parts — is the automatic refit's plan, byte for byte. Its pairs are the Studio's
        // own (BuildPairs), the refit's the watcher's: what this holds on real meshes is that the two build the same.
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var done = NeolitheMedium.Value;
        var sourceCat = CatalogOf(bodies)(done.Decision.Source.Dir)!;
        var targetCat = bodies[1].Catalog;
        Assert.Null(KeepEnds.BuildPairs(done.Reading, sourceCat.PathOf(done.Decision.Source.Option),
                                        targetCat.PathOf(done.Decision.Target), sourceCat.PathOf(done.Decision.EndFrom!),
                                        targetCat.PathOf(done.Decision.EndTo!), false, _ => (null, null), s => s, null,
                                        null, out var limb, out var end));
        var studio = KeepEnds.Plan(done.Parts, done.Bytes, done.Reading, limb, end, replaceSkin: true, across: true,
                                   clearBody: false, alsoHeld: null, extra: null);
        Assert.Equal(done.Plan.Model, studio.Model);
    }

    private const string SeenGloves = LocalData.Mods + ":Seen - by Solona/common/8/c0201e0670_glv.mdl";

    [LocalDataFact(SeenGloves, Rue, Yab)]
    [Trait("Plan", "AC-keep-ends-studio-2")]
    [Trait("Speed", "Slow")]
    public void Studio_gloves_keep_their_hands_onto_another_body()   // [AC-keep-ends-studio-2]
    {
        // Rue+ to YAB+: one family's two bodies, whose arms correspond. (Neolithe to Bibo+ arms are refused outright.)
        var bytes = File.ReadAllBytes(LocalData.Path(SeenGloves));
        var done = StudioRefit(bytes, "_glv", Rue, Yab);
        output.WriteLine($"route {KeepEnds.Letter(done.Read.Route)}, made for {done.From.Label}, onto {done.Target.Label}; "
                       + $"{done.Reading.Ends.Count} hand vertices; folded {done.Plan.Report.Folded} (unheld "
                       + $"{done.Unheld.Report.Folded}); moved up to {done.Plan.Report.WorstMove * 1000f:F1} mm");
        Assert.Contains(done.Read.Route, new[] { KeepEnds.Route.Skin, KeepEnds.Route.Cloth });
        Assert.Equal("_top", done.From.Slot);
        Assert.NotEmpty(done.Reading.Ends);
        foreach (int v in done.Reading.Ends) Assert.Equal(default, done.Plan.Edit.DeltaAt(v));
        Assert.True(done.Plan.Report.Folded <= done.Unheld.Report.Folded);
    }

    [LocalDataFact(CleoGloves, Neolithe)]
    [Trait("Plan", "EDGE-keep-ends-studio-5")]
    [Trait("Speed", "Slow")]
    public void Studio_gloves_across_one_bodys_chests_already_fit()   // [EDGE-keep-ends-studio-5]
    {
        // Every chest size of a body has the same arms: a glove read as one and aimed at another fits already.
        var bytes = File.ReadAllBytes(LocalData.Path(CleoGloves));
        var parts = ModelPartReader.Read(bytes)!;
        var bones = BonesOf(bytes);
        var neo = BodySizeCatalog.Read(LocalData.Path(Neolithe));
        var reading = KeepEnds.Read("_glv", parts, bytes)!;
        var read = KeepEnds.StudioLimb(reading, parts, bones, Race, Path.GetFileName(LocalData.Path(Neolithe)), neo);
        var other = neo.For("_top", Race).First(o => !string.Equals(o.Rel, read.Pick!.Rel, StringComparison.OrdinalIgnoreCase)
                                                     && o.Section == read.Pick.Section);
        output.WriteLine($"read as {read.Pick!.Label}; compared with {other.Label}");
        Assert.True(KeepEnds.SameShape(reading.LimbView, read.Pick!, other, neo, bones, read.Route == KeepEnds.Route.Cloth));

        // And the tool says so: nothing planned, and "already fits" named for the size.
        var input = new KeepEnds.StudioPlanInput(parts, bytes, "_glv", RefitCore.ShapePieces(parts, new HashSet<string>()),
                                                 neo, neo, Race, false, read.Pick!, [other], SameBodyMod: true,
                                                 FromCloth: read.Shown?.FromCloth == true, ReplaceSkin: true, Across: false,
                                                 ClearBody: false, Held: null, Shared: null, _ => (null, null), s => s, "test");
        var planned = KeepEnds.StudioPlan(input, (p, _) => p, null,
                                          NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
        Assert.Null(planned.Planned);
        Assert.Equal(string.Format(Proteus.Localization.Strings.Parts.RetargetAlreadyFitsFmt, other.Label), planned.Notice);
    }

    [LocalDataFact(HellForLeather, Bibo, Neolithe)]
    [Trait("Plan", "EDGE-keep-ends-studio-27")]
    [Trait("Speed", "Slow")]
    public void Studio_skinless_boot_with_legs_picked_by_hand_keeps_its_foot()   // [EDGE-keep-ends-studio-27]
    {
        // Leather from toe to thigh: nothing to read a body off (NoSkin), so the user picks the legs by hand. The refit
        // then runs by them: the foot held, and no body skin brought into a boot that had none.
        var bytes = File.ReadAllBytes(HellForLeatherModel("common/7/c0201e0380_sho.mdl"));
        var parts = ModelPartReader.Read(bytes)!;
        var bones = BonesOf(bytes);
        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        var neo = BodySizeCatalog.Read(LocalData.Path(Neolithe));
        var reading = KeepEnds.Read("_sho", parts, bytes, RefitCore.ShapePieces(parts, new HashSet<string>()))!;
        Assert.Equal(KeepEnds.Route.NoSkin,
                     KeepEnds.StudioLimb(reading, parts, bones, Race, Path.GetFileName(LocalData.Path(Bibo)), bibo).Route);

        var from = bibo.For("_dwn", Race).First(o => o.Label.EndsWith("Medium", StringComparison.Ordinal));
        var to = NeolitheLegs(neo);
        var (endFrom, endTo) = KeepEnds.EndPair(bibo, neo, "_sho", Race, from, to);
        Assert.Null(KeepEnds.BuildPairs(reading, bibo.PathOf(from), neo.PathOf(to),
                                        endFrom == null ? null : bibo.PathOf(endFrom),
                                        endTo == null ? null : neo.PathOf(endTo), false, _ => (null, null), s => s, null,
                                        null, out var limb, out var end));
        var plan = KeepEnds.Plan(parts, bytes, reading, limb, end, true, true, false, null, null);

        output.WriteLine($"{reading.Ends.Count} foot vertices; swap {plan.Report.Swap}; moved up to "
                       + $"{plan.Report.WorstMove * 1000f:F1} mm");
        foreach (int v in reading.Ends) Assert.Equal(default, plan.Edit.DeltaAt(v));
        Assert.Empty(SkinPositions(plan.Model));
    }

    [LocalDataFact(LocalData.GameData)]
    [Trait("Plan", "EDGE-keep-ends-studio-10")]
    [Trait("Speed", "Slow")]
    public void Studio_game_boots_route_as_the_automatic_refit_does()   // [EDGE-keep-ends-studio-10]
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        string path = new[]
        {
            "chara/equipment/e0100/model/c0201e0100_sho.mdl", "chara/equipment/e0005/model/c0201e0005_sho.mdl",
            "chara/equipment/e0036/model/c0201e0036_sho.mdl", "chara/equipment/e0001/model/c0201e0001_sho.mdl",
        }.First(p => data.GetFile(p) != null);
        var bytes = data.GetFile(path)!.Data;
        var parts = ModelPartReader.Read(bytes)!;
        var bones = BonesOf(bytes);
        var vanilla = VanillaBodyCatalog.Read(Race, p => data.GetFile(p)?.Data);
        var reading = KeepEnds.Read("_sho", parts, bytes)!;

        var studio = KeepEnds.StudioLimb(reading, parts, bones, Race, VanillaBodyCatalog.Key, vanilla);
        var auto = KeepEnds.Find(reading, parts, bones, Race, (_, s) => KeepEnds.VanillaPick(vanilla, s, Race),
                                 _ => vanilla);
        output.WriteLine($"{path}: Studio route {studio.Route}, automatic {auto.Route}");
        Assert.Equal(auto.Route, studio.Route);
        Assert.Equal(auto.Pick?.Option, studio.Pick);
    }

    [LocalDataFact(LocalData.GameData, Bibo)]
    [Trait("Plan", "EDGE-keep-ends-studio-11")]
    [Trait("Speed", "Slow")]
    public void Studio_baked_boots_keep_their_feet()   // [EDGE-keep-ends-studio-11]
    {
        // A man's game boots baked onto a woman, as the panel bakes them, then refitted with the foot kept and drawn with
        // the new body's skin.
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var pbd = new XivLiveMesh.PbdFile(data.GetFile("chara/xls/boneDeformer/human.pbd")!.Data);
        string path = new[]
        {
            "chara/equipment/e0100/model/c0101e0100_sho.mdl", "chara/equipment/e0005/model/c0101e0005_sho.mdl",
            "chara/equipment/e0036/model/c0101e0036_sho.mdl", "chara/equipment/e0001/model/c0101e0001_sho.mdl",
        }.First(p => data.GetFile(p) != null);
        var baked = RacialModelBake.Bake(data.GetFile(path)!.Data, 101, 201, pbd, _ => null, out string why);
        Assert.True(baked != null, why);
        var parts = ModelPartReader.Read(baked!)!;
        var bones = BonesOf(baked!);
        var vanilla = VanillaBodyCatalog.Read(Race, p => data.GetFile(p)?.Data);
        var reading = KeepEnds.Read("_sho", parts, baked!)!;
        var read = KeepEnds.StudioLimb(reading, parts, bones, Race, VanillaBodyCatalog.Key, vanilla);
        output.WriteLine($"{path}: route {read.Route}, {read.Pick?.Label}");
        Assert.Contains(read.Route, new[] { KeepEnds.Route.Skin, KeepEnds.Route.Cloth });

        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        var legs = bibo.For("_dwn", Race).First(o => o.Label.EndsWith("Medium", StringComparison.Ordinal));
        var uv = new UVRemapService(NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>(), ".");
        Assert.Null(KeepEnds.BuildPairs(reading, vanilla.PathOf(read.Pick!), bibo.PathOf(legs), null, null, false,
                                        _ => (null, null), s => s, uv, null, out var limb, out _));
        var plan = KeepEnds.Plan(parts, baked!, reading, limb, null, true, true, false, null, null);
        plan = RefitCore.WithBodySkin(plan, bibo.PathOf(legs), NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>());
        foreach (int v in reading.Ends) Assert.Equal(default, plan.Edit.DeltaAt(v));
    }

    [LocalDataFact(Bibo, Neolithe)]
    [Trait("Plan", "AC-keep-ends-studio-4")]
    public void Studio_shoe_reaching_no_leg_redraws_only_its_skin()   // [AC-keep-ends-studio-4]
    {
        // Bibo+'s own feet as a "shoe": all skin, nothing of the leg. Pointed at the men's Bibo skin (as Shift is), it
        // is redrawn with Neolithe's; as Bibo+ ships them, there is nothing to change — unless it is being baked.
        string feetPath = Path.Combine(LocalData.Path(Bibo), "feet", "small clothes", "chara", "equipment", "e0000",
                                       "model", "c0201e0000_sho.mdl");
        var plain = File.ReadAllBytes(feetPath);
        var mens = ModelAttributeWriter.RenameMaterial(plain, "/mt_c0201b0001_bibo.mtrl", "/mt_c0101b0001_bibo.mtrl");
        var bibo = BodySizeCatalog.Read(LocalData.Path(Bibo));
        var neo = BodySizeCatalog.Read(LocalData.Path(Neolithe));
        var parts = ModelPartReader.Read(mens)!;
        var reading = KeepEnds.Read("_sho", parts, mens)!;
        var read = KeepEnds.StudioLimb(reading, parts, BonesOf(mens), Race, Path.GetFileName(LocalData.Path(Bibo)), bibo);
        Assert.Equal(KeepEnds.Route.SkinOnly, read.Route);
        Assert.NotNull(read.Pick);
        var end = AutoRefitDecisions.Companion(neo.For("_sho", Race), null, null)!;
        var log = NSubstitute.Substitute.For<Dalamud.Plugin.Services.IPluginLog>();

        var redrawn = KeepEnds.StudioSkinOnly(mens, bibo.PathOf(read.Pick!), end, neo, false, baking: false, (p, _) => p,
                                              null, log);
        Assert.True(redrawn.Planned is { Count: 1 }, redrawn.Error + redrawn.Notice);
        var model = redrawn.Planned![0].Planned.Model;
        Assert.Equal(parts.Positions, ModelPartReader.Read(model)!.Positions);   // its shape, untouched
        Assert.DoesNotContain("/mt_c0101b0001_bibo.mtrl", SecondSkinWriter.Parse(model).MatNames);
        Assert.Contains("/mt_c0201b0001_bibo.mtrl", SecondSkinWriter.Parse(model).MatNames);

        var same = KeepEnds.StudioSkinOnly(plain, bibo.PathOf(read.Pick!), end, neo, false, baking: false, (p, _) => p,
                                           null, log);
        Assert.Null(same.Planned);
        Assert.Equal(Proteus.Localization.Strings.Parts.RetargetSkinAlreadyMatches, same.Notice);
        var baked = KeepEnds.StudioSkinOnly(plain, bibo.PathOf(read.Pick!), end, neo, false, baking: true, (p, _) => p,
                                            null, log);
        Assert.True(baked.Planned is { Count: 1 });
    }

    // ── reading the legs alone ───────────────────────────────────────────────────────────────────────────

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-4")]
    [Trait("Speed", "Slow")]
    public void Heartthrob_shoe_sizes_read_three_different_legs()
    {
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var read = new List<string>();
        foreach (string size in new[] { "small", "medium", "large" })
        {
            var (parts, bytes) = Load(HeartthrobShoe(size));
            var bones = BonesOf(bytes);
            var reading = KeepEnds.Read("_sho", parts, bytes)!;
            var found = KeepEnds.Find(reading, parts, bones, Race, Detector(bodies, bones), CatalogOf(bodies));
            output.WriteLine($"{size}: route {found.Route}, {found.Pick?.Dir} / {found.Pick?.Option.Label} "
                           + $"({found.Pick?.Confidence})");
            Assert.Equal(KeepEnds.Route.Skin, found.Route);
            Assert.True(AutoRefitDecisions.Proceed(found.Pick!.Confidence));
            read.Add(found.Pick.Option.Rel);
        }
        Assert.Equal(3, read.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
