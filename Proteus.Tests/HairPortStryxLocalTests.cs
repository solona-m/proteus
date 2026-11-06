using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// The mod whose ports showed a bald spot at the back of the head, and whose men's port was refused for a shared
/// material: LM Stryx Rainbow (c0201, hairstyles 133 and 168, physics hair, a Proteus hat fit on the source). Each test
/// works on a copy of the mod with every port of ours taken out first — the user's mod is never written, and whatever
/// ports it holds today do not change the answer.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class HairPortStryxLocalTests(ITestOutputHelper o) : IDisposable
{
    private const ushort From = 201;
    private readonly List<string> temps = [];

    public void Dispose()
    {
        foreach (var t in temps) try { Directory.Delete(t, true); } catch { }
    }

    /// <summary>A copy of the mod without a port of ours: record empty, nothing under Hair Ports/ named.</summary>
    private string CleanCopy()
    {
        var root = LocalData.Path(LocalData.StryxMod);
        var temp = Path.Combine(Path.GetTempPath(), "proteus_stryx_" + Path.GetRandomFileName());
        temps.Add(temp);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f);
            if (rel.StartsWith(HairPortPlan.Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(temp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        HairPortWriter.Undo(temp, _ => true, decline: false);
        var record = HairPortRecord.Read(temp);
        record.Declined.Clear();
        HairPortRecord.Write(temp, record);

        Assert.Empty(HairPortRecord.Read(temp).Ports);
        var owners = HairPortPlan.ReadOwners(temp)!;
        Assert.DoesNotContain(owners.SelectMany(w => w.Files), f => HairPortPlan.IsPortFile(f.Value) || HairMaterialPath.IsOwnName(f.Key));
        return temp;
    }

    private static string Source(int id) => HairPortLocalTests.HairPath(From, id);

    private static IEnumerable<ushort> Targets()
        => Enumerable.Range(1, 18).Select(n => (ushort)(n * 100 + 1)).Where(r => r != From);

    /// <summary>The source as published by the mod (found through its manifest), for an id.</summary>
    private static byte[] SourceBytes(string root, int id)
    {
        var owners = HairPortPlan.ReadOwners(root)!;
        var rel = owners.Select(w => HairPortPlan.FileFor(w, Source(id))).First(r => r != null)!;
        return File.ReadAllBytes(Path.Combine(root, rel));
    }

    private static bool Back(Vector3 d) => d.Z < 0f;

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] The reported bald spot: for hairstyles 168 and 133, to every playable race, the
    /// points the bend sank into the upper back of the target's skull where the source sat on its scalp are gone — at
    /// most 2% of them left (this gate is never relaxed), none deeper than the clip depth and a millimetre more.
    /// Measured on the bytes the builder would save.
    /// </summary>
    [LocalDataFact(LocalData.StryxMod, LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    public void Stryx_ports_lose_their_created_clips()
    {
        var game = HairPortLocalTests.Load();
        var root = CleanCopy();
        var sf = HairScalpPush.FrameOf(game.Head(From)!)!;
        foreach (int id in new[] { 168, 133 })
        {
            var source = SourceBytes(root, id);
            var shown = ModelPartReader.Read(source)!;
            var spares = HairScalpPush.Spares(source, shown);
            var plan = HairPortBuilder.Build(root, Source(id), Targets(), game.Pbd, game.File, withSharers: false);
            foreach (var (race, why) in plan.Refused) o.WriteLine($"h{id:D4} c{race:D4} refused: {why}");
            foreach (var w in plan.Writes.Where(w => !w.Stripped))
            {
                var to = HairPortPlan.Parse(w.TargetGamePath)!.Value.Race;
                var tf = HairScalpPush.FrameOf(game.Head(to)!)!;
                var bent = RacialModelBake.Port(source, From, to, game.Pbd, HairPortBaker.ParentOf, out _)!;
                var before = HairScalpPush.Created(shown, ModelPartReader.Read(bent)!, sf, tf, spares, Back);
                var after = HairScalpPush.Created(shown, ModelPartReader.Read(w.Model)!, sf, tf, spares, Back);
                var deep = Deepest(shown, ModelPartReader.Read(w.Model)!, sf, tf, spares);
                o.WriteLine($"h{id:D4} c{to:D4}: upper-back created clips {before.Count} -> {after.Count} of {after.Cranium}, deepest left {deep * 1000:F1} mm");
                Assert.True(after.Count <= before.Count * 0.02, $"h{id:D4} c{to:D4}: {before.Count} -> {after.Count}");
                Assert.True(deep >= -(HairScalpPush.ClipDepth + 0.001f) || after.Count == 0, $"h{id:D4} c{to:D4}: {deep * 1000:F1} mm");
            }
        }
    }

    /// <summary>The deepest a point the bend sank (where the source sat on its scalp) still is, in metres (negative).</summary>
    private static float Deepest(ModelParts shown, ModelParts port, HairScalpPush.Frame s, HairScalpPush.Frame t, IReadOnlySet<int> spares)
    {
        float deepest = 0f;
        for (int v = 0; v < port.Positions.Length / 3; v++)
        {
            if (spares.Contains(v)) continue;
            var p = new Vector3(shown.Positions[v * 3], shown.Positions[v * 3 + 1], shown.Positions[v * 3 + 2]) - s.Centre;
            var q = new Vector3(port.Positions[v * 3], port.Positions[v * 3 + 1], port.Positions[v * 3 + 2]) - t.Centre;
            if (p.Y < 0 || q.Y < 0 || p.Length() < 1e-5f || q.Length() < 1e-5f) continue;
            float gs = p.Length() - HatCompatSolve.FloorAt(s.Skull, p / p.Length());
            float gt = q.Length() - HatCompatSolve.FloorAt(t.Skull, q / q.Length());
            if (gs > -HairScalpPush.SourceSlack) deepest = MathF.Min(deepest, gt);
        }
        return deepest;
    }

    /// <summary>[AC-hair-material-scalp-fit-8] [EDGE-hair-material-scalp-fit-17] The source carries Proteus's hat fit,
    /// whose spares are pressed to the scalp on purpose: they neither seed the push nor earn a port the clip warning.</summary>
    [LocalDataFact(LocalData.StryxMod, LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-8")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-17")]
    public void Stryx_hat_fitted_source_gets_no_clip_warning_from_spares()
    {
        var game = HairPortLocalTests.Load();
        var root = CleanCopy();
        var source = SourceBytes(root, 168);
        Assert.NotEmpty(HairScalpPush.Spares(source, ModelPartReader.Read(source)!));
        var plan = HairPortBuilder.Build(root, Source(168), Targets(), game.Pbd, game.File, withSharers: false);
        foreach (var (race, warning) in plan.Warnings ?? new Dictionary<ushort, string>())
        {
            o.WriteLine($"c{race:D4}: {warning}");
            Assert.DoesNotContain("of its points sink into the head", warning);
        }
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-1] [AC-hair-material-scalp-fit-2] [AC-hair-material-scalp-fit-10] The report: only
    /// Midlander M asked for, from a mod with no men's port. It ports; its materials are the port's own names, pointing at
    /// the author's files; no game-named material of the men's shared folder is redirected; and every own name the saved
    /// model names is published.
    /// </summary>
    [LocalDataFact(LocalData.StryxMod, LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-1")]
    [Trait("Plan", "AC-hair-material-scalp-fit-2")]
    [Trait("Plan", "AC-hair-material-scalp-fit-10")]
    public void Stryx_ports_to_midlander_man_alone()
    {
        var game = HairPortLocalTests.Load();
        var root = CleanCopy();
        var plan = HairPortBuilder.Build(root, Source(168), [101], game.Pbd, game.File, withSharers: false);
        Assert.Empty(plan.Refused);
        var w = Assert.Single(plan.Writes);
        var materials = w.Materials.Where(m => m.GamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                                   .ToDictionary(m => m.GamePath, m => HairPortRecord.Rel(m.AuthorFile ?? ""), StringComparer.OrdinalIgnoreCase);
        o.WriteLine(string.Join("\n", materials.Select(m => $"{m.Key} -> {m.Value}")));
        Assert.Equal(
        [
            "chara/human/c0101/obj/hair/h0168/material/v0001/mt_c0101h0168_hir_b_port0101.mtrl -> default group/chara/human/c0201/obj/hair/h0168/material/v0001/mt_c0201h0168_hir_b.mtrl",
            "chara/human/c0101/obj/hair/h0168/material/v0001/mt_c0101h0168_hir_c_port0101.mtrl -> common/1/mt_c0201h0168_hir_c.mtrl",
        ], materials.Select(m => $"{m.Key} -> {m.Value}".ToLowerInvariant()).Order(StringComparer.Ordinal));

        var saved = HairPortWriter.Save(root, plan.Writes, auto: false);
        Assert.True(saved.Ok, saved.Message);
        var owners = HairPortPlan.ReadOwners(root)!;
        Assert.DoesNotContain(owners.SelectMany(x => x.Files.Keys), k => k.Replace('\\', '/').StartsWith(
            "chara/human/c0101/obj/hair/h0168/material/v0001/mt_c0101h0168_hir_", StringComparison.OrdinalIgnoreCase)
            && !HairMaterialPath.IsOwnName(k));
        HairPortOwnMaterialTests.AssertOwnMaterialsPublished(root);
    }

    /// <summary>[AC-hair-material-scalp-fit-7] On the reported ports, every race, the push keeps the hairstyle: at most
    /// max(2, 0.1%) folded triangles added and never into the fold warning, few pushed edges strained over 25%
    /// beyond the skull's wrap (5.3%), every bone and weight as the bend left them, and the bounding box holding every point
    /// the bend's did.</summary>
    [LocalDataFact(LocalData.StryxMod, LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-7")]
    public void Stryx_push_keeps_strands_and_physics()
    {
        var game = HairPortLocalTests.Load();
        var root = CleanCopy();
        var source = SourceBytes(root, 168);
        var shown = ModelPartReader.Read(source)!;
        var sf = HairScalpPush.FrameOf(game.Head(From)!)!;
        double worst = 0;
        foreach (var to in Targets())
        {
            var tf = HairScalpPush.FrameOf(game.Head(to)!)!;
            var bent = RacialModelBake.Port(source, From, to, game.Pbd, HairPortBaker.ParentOf, out _);
            if (bent == null) continue;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var push = HairScalpPush.Apply(bent, shown, sf, tf);
            long ms = clock.ElapsedMilliseconds;
            if (push.Pushed == 0) { o.WriteLine($"c{to:D4}: nothing pushed"); continue; }
            var b = ModelPartReader.Read(bent)!;
            var p = ModelPartReader.Read(push.Model)!;
            var (added, tipped) = HairPortScalpLocalTests.Folds(shown, b, p);
            int tris = shown.Parts.Where(x => x.Island == -1).Sum(x => x.TriangleCount);
            double share = HairPortScalpLocalTests.OverStrained(shown, b, p, sf, tf, bent);
            worst = Math.Max(worst, share);
            o.WriteLine($"c{to:D4}: pushed {push.Pushed}, worst {push.Worst * 1000:F1} mm, {ms} ms, folds +{added}, strained {share:P3}");
            Assert.True(added <= Math.Max(2, tris / 1000) && !tipped, $"c{to:D4}: folds +{added}");
            // A regression gate: the measured worst plus a quarter (2026-11-05: c1501 4.22% at a 51.9 mm push; c0701 0.079%;
            // every other race 0.000%). How the Hrothgar port looks is checked in game.
            Assert.True(share <= 0.053, $"c{to:D4}: {share:P2} of pushed edges strained over 25% beyond the skull's wrap");
            var sb = ModelSkinReader.Read(bent, null, null)!;
            var sp = ModelSkinReader.Read(push.Model, null, null)!;
            Assert.Equal(sb.BoneNames, sp.BoneNames);
            Assert.Equal(sb.BoneIndices, sp.BoneIndices);
            Assert.Equal(sb.BoneWeights, sp.BoneWeights);
            Assert.True(HairScalpPushTests.BoxHolds(push.Model, p.Positions, HairScalpPushTests.Held(bent, b.Positions)), $"c{to:D4}: box");
        }
        o.WriteLine($"worst strained share {worst:P3}");
    }

    /// <summary>
    /// [AC-hair-material-scalp-fit-6] The bald spot by the face's own skin triangles, not the push's scalp: for every race,
    /// port by port, the upper-back points that sat on the source's skin and sit inside the target's after the push pass
    /// <see cref="HairPortScalpLocalTests.CheckPort"/> — 2% of before wherever that is 16 or more (every large race: c0101,
    /// c0301, c0501, c1101, c1201, c1501), hairstyles 168 and 133; and no moved point ends more than 0.5 mm further out than
    /// it sat on the source.
    /// </summary>
    [LocalDataFact(LocalData.StryxMod, LocalData.GameData)]
    [Trait("Plan", "AC-hair-material-scalp-fit-6")]
    [Trait("Plan", "EDGE-hair-material-scalp-fit-14")]
    public void Stryx_pushed_hair_leaves_the_skin_without_overshooting()
    {
        var game = HairPortLocalTests.Load();
        var root = CleanCopy();
        var sf = HairScalpPush.FrameOf(game.Head(From)!)!;
        var fails = new List<string>();
        foreach (int id in new[] { 168, 133 })
        foreach (var to in Targets())
        {
            if (game.File(HairPortLocalTests.HairPath(to, id)) == null) continue;   // that race cannot choose it
            var source = SourceBytes(root, id);
            var shown = ModelPartReader.Read(source)!;
            var tf = HairScalpPush.FrameOf(game.Head(to)!)!;
            var bent = RacialModelBake.Port(source, From, to, game.Pbd, HairPortBaker.ParentOf, out _);
            if (bent == null) continue;
            var push = HairScalpPush.Apply(bent, shown, sf, tf);
            if (push.Pushed == 0) continue;
            var b = ModelPartReader.Read(bent)!;
            var p = ModelPartReader.Read(push.Model)!;
            var spares = HairScalpPush.Spares(bent, b);
            var r = HairPortScalpLocalTests.InsideSkin(HairPortScalpLocalTests.SkinOf(game.Head(to)!, tf),
                HairPortScalpLocalTests.SkinOf(game.Head(From)!, sf), shown, b, p, spares);
            float over = HairPortScalpLocalTests.Overshoot(shown, b, p, sf, tf, spares);
            o.WriteLine($"h{id:D4} c{to:D4}: upper-back points inside the skin {r.Before} -> {r.After} ({r.Deep} deep, {r.MissedBoth} by both), deepest unmoved " +
                        $"{r.UnmovedDepth * 1000:F1} mm ({r.Protrusion} under a protrusion, {r.ProtrusionBack} at the upper back, not counted), worst overshoot {over * 1000:F2} mm");
            HairPortScalpLocalTests.CheckPort($"h{id:D4} c{to:D4}", to, r,
                                              (ok, what) => { if (!ok) fails.Add(what); });
            if (over > 0.0005f) fails.Add($"c{to:D4}: {over * 1000:F2} mm further out than the source");
        }
        Assert.True(fails.Count == 0, string.Join("; ", fails));
    }

    private static float Dist(float[] p, int a, int b)
        => new Vector3(p[a * 3] - p[b * 3], p[a * 3 + 1] - p[b * 3 + 1], p[a * 3 + 2] - p[b * 3 + 2]).Length();
}
