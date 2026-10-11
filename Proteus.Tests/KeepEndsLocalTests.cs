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
    public void Heartthrob_keeps_its_foot_exactly_when_refitted_to_another_body()
    {
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var neo = bodies[1];
        var done = RefitHeartthrob("medium", bodies, neo.Dir, NeolitheLegs);

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
    private static bool SameRig(XivLiveMesh.SkinnedMesh a, XivLiveMesh.SkinnedMesh b, int v)
    {
        Dictionary<string, float> Of(XivLiveMesh.SkinnedMesh m)
        {
            var w = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
            {
                float weight = m.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                if (weight <= 0f) continue;
                string bone = m.BoneNames[m.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k]];
                w[bone] = w.GetValueOrDefault(bone) + weight;
            }
            return w;
        }
        var x = Of(a);
        var y = Of(b);
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out float o) && MathF.Abs(o - kv.Value) <= 1.5f / 255f);
    }

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-6")]
    public void Holding_the_foot_adds_no_folds()
    {
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        // Onto another body mod, and onto another size of the shoe's own body.
        var neo = RefitHeartthrob("medium", bodies, bodies[1].Dir, NeolitheLegs);
        var bibo = RefitHeartthrob("medium", bodies, bodies[0].Dir,
                                   c => c.For("_dwn", Race).First(o => o.Label.EndsWith("Large", StringComparison.Ordinal)));
        foreach (var (name, done) in new[] { ("Neolithe", neo), ("Bibo+ Large", bibo) })
        {
            output.WriteLine($"{name}: {done.Plan.Report.Folded} folded holding the foot, {done.Unheld.Report.Folded} "
                           + $"without; moved up to {done.Plan.Report.WorstMove * 1000f:F1} mm");
            Assert.True(done.Plan.Report.Folded <= done.Unheld.Report.Folded,
                        $"{name}: holding the foot folded {done.Plan.Report.Folded} triangles, {done.Unheld.Report.Folded} without");
        }
    }

    /// <summary>The ankle edge never stretches more than this (15 mm): beyond it the seam is a hole, not a crease.</summary>
    private const float SeamLimit = 0.015f;

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "EDGE-keep-hand-foot-skin-4")]
    public void Heartthrob_seam_report()
    {
        // Rigid by choice: the foot does not follow the ankle, so where two bodies' ankles differ the seam stretches. The
        // foot and its leg are one skin mesh here, so the seam is the edges from the kept foot to the leg beside it:
        // measured inside the pass, on the solve's own move, as each edge's length now against as authored.
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        foreach (var (name, dir, target) in new (string, string, Func<BodySizeCatalog, BodyOption>)[]
                 {
                     ("Neolithe SFW M", bodies[1].Dir, NeolitheLegs),
                     ("Bibo+ Large", bodies[0].Dir,
                      c => c.For("_dwn", Race).First(o => o.Label.EndsWith("Large", StringComparison.Ordinal))),
                 })
        {
            var done = RefitHeartthrob("medium", bodies, dir, target);
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

            // The feet ride along for their shape, though the game draws the author's heeled foot there, not the new
            // body's flat one. How far that moves the free cloth right beside the kept foot, against the legs alone.
            var parts = done.Parts;
            var keptAt = done.Reading.Ends.Select(v => new System.Numerics.Vector3(parts.Positions[v * 3],
                             parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2])).ToList();
            float apart = 0f, moved = 0f;
            int beside = 0;
            foreach (int v in parts.Parts.Where(p => p.Island < 0 && !SecondSkinWriter.IsBodySkinMaterial(p.Material))
                                   .SelectMany(p => p.Triangles).Distinct())
            {
                if (done.Reading.Ends.Contains(v)) continue;
                var at = new System.Numerics.Vector3(parts.Positions[v * 3], parts.Positions[v * 3 + 1], parts.Positions[v * 3 + 2]);
                if (!keptAt.Any(k => System.Numerics.Vector3.DistanceSquared(k, at) < 0.02f * 0.02f)) continue;
                beside++;
                var a = done.Plan.Edit.DeltaAt(v);
                var b = done.LegsOnly.Edit.DeltaAt(v);
                var da = new System.Numerics.Vector3(a.X, a.Y, a.Z);
                apart = MathF.Max(apart, System.Numerics.Vector3.Distance(da, new System.Numerics.Vector3(b.X, b.Y, b.Z)));
                moved = MathF.Max(moved, da.Length());
            }
            output.WriteLine($"{name}: {beside} free cloth vertices within 20 mm of the kept foot moved up to "
                           + $"{moved * 1000f:F1} mm, at most {apart * 1000f:F1} mm away from where the legs alone put them");
        }
    }

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-6")]
    public void KeepEnds_e2e_Heartthrob_detect_plan_save()
    {
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var neo = bodies[1];
        var done = RefitHeartthrob("medium", bodies, neo.Dir, NeolitheLegs);

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
    public void The_kept_foot_is_drawn_with_the_new_bodys_skin()
    {
        // Neither moved nor swapped, the foot's skin is still drawn the way the new body draws its own — the step the
        // watcher runs on every refit of a shoe — so the body's tattoos and texture reach it.
        var bodies = Bodies(Bibo, Neolithe, Rue, Yab);
        var neo = bodies[1];
        var done = RefitHeartthrob("medium", bodies, neo.Dir, NeolitheLegs);
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

    // ── reading the legs alone ───────────────────────────────────────────────────────────────────────────

    [LocalDataFact(Heartthrob, Bibo, Neolithe, Rue, Yab)]
    [Trait("Plan", "AC-keep-hand-foot-skin-4")]
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
