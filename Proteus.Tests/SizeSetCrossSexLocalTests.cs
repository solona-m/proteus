using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A women's mod's men's versions in the size set, against the real files (sizeset-cross-sex): Sheer Elegance's top on
/// Bibo+, a Rue top, and Starlit's YAS-rigged skirt made men's onto TBSE and TBSE-X. Every run works on a copy.
/// </summary>
public class SizeSetCrossSexLocalTests(ITestOutputHelper o)
{
    internal const string Sheer = LocalData.Mods + ":BiboPlus Sheer Elegance";
    internal const string Starlit = LocalData.Mods + ":[Nini] Starlit Skirt";
    internal const string RueTop = LocalData.Mods + ":[alee] Thursday - Rue+";
    internal const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";

    private static readonly Lazy<Lumina.GameData> GameData = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    private static byte[]? Game(string path)
    {
        try { return GameData.Value.GetFile(path)?.Data; }
        catch (Exception) { return null; }
    }

    private static UVRemapService Maps() => new(Substitute.For<IPluginLog>(), LocalData.Path(LocalData.TbseUvMaps));

    internal static IReadOnlyList<BodyModIndex.Entry> Installed(params string[] bodyMods)
    {
        var dirs = bodyMods.Select(n => Path.GetFileName(LocalData.Path(n))).ToDictionary(d => d, d => d);
        return new BodyModIndex().RefreshState(dirs, LocalData.Path(LocalData.Mods)).List;
    }

    /// <summary>The mod copied to a temp folder (its models, manifest and record).</summary>
    internal static string Copy(string need)
    {
        string copy = Path.Combine(Path.GetTempPath(), "proteus-sizeset-men-" + Guid.NewGuid().ToString("N"));
        SizeSetRefitTests.CopyModels(LocalData.Path(need), copy);
        return copy;
    }

    /// <summary>One of the author's sizes measured as a men's version's source: its made-for body bent for a man, how
    /// far that lies from the men's body, and what the job makes from it.</summary>
    internal sealed record Measured(string Option, string MadeFor, string? Layout, bool Yas, float BodyP90, float BodyMean,
                                    int Folds, double Coverage, long Ms, CrossSexRefitJob.Size? Size);

    /// <summary>
    /// Every one of the author's sizes of <paramref name="gamePath"/> in <paramref name="modNeed"/>, measured onto the
    /// men's body at <paramref name="targetRel"/> in <paramref name="targetNeed"/> — the steps the size set takes, each
    /// size with its own wearer (the game's skeleton).
    /// </summary>
    internal List<Measured> Measure(string modNeed, string gamePath, string targetNeed, string targetRel,
                                    IReadOnlyList<BodyModIndex.Entry> women)
    {
        var log = Substitute.For<IPluginLog>();
        string root = LocalData.Path(modNeed);
        var piece = SizeSetRefit.PiecesOf(root, Game, includeEnds: false)
                                .First(p => string.Equals(p.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
        string slot = piece.Slot;
        var mods = women.Select(b => (b.Dir, b.Catalog)).ToList();
        var men = OtherRaceRefit.OnlyRace(BodySizeCatalog.Read(LocalData.Path(targetNeed)), CrossSexRefit.Men);
        var onto = men.For(slot).First(b => RefitCore.Normal(b.Rel) == RefitCore.Normal(targetRel));
        string targetPath = men.PathOf(onto);
        var maps = Maps();
        var measured = new List<Measured>();
        foreach (var (option, file) in piece.Options)
        {
            byte[] bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, file));
            var garment = ModelPartReader.Read(bytes)!;
            var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
            var pick = RefitCore.DetectSource(garment, bones, slot, piece.Race, mods, mods[0].Dir, _ => true);
            if (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence))
            {
                o.WriteLine($"{option ?? file}: made-for body not read");
                continue;
            }
            var entry = women.First(b => b.Dir == pick.Dir);
            string madeForPath = entry.Catalog.PathOf(pick.Option);
            string? layout = BodyCorrespondence.LayoutOf(ModelPartReader.Read(File.ReadAllBytes(madeForPath))!);
            bool yas = SupportedBodies.IsYas(bones);
            if (!SizeSetRefit.AcrossEligible(layout))
            {
                measured.Add(new Measured(option ?? file, $"{entry.Name} / {pick.Option.Label}", layout, yas, float.NaN,
                                          float.NaN, -1, 0, 0, null));
                continue;
            }
            string? cutFrom = piece.Group != null && option != null ? $"{piece.Group} / {option}" : null;
            var redirects = PenumbraModMeta.ReadAllRedirects(root);
            var wearerIn = OtherRaceWearer.Gather(CrossSexRefit.Men, gamePath, Game, _ => null,
                path => OtherRaceRefit.CarriedFile(redirects, cutFrom, path) is { } rel
                    ? File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, rel)) : null,
                OtherRaceRefit.EstEntriesFor(PenumbraModMeta.TryReadGroups(root),
                                             PenumbraModMeta.TryReadDefaultData(root)?.Manipulations, cutFrom));
            var made = OtherRaceWearer.Build(wearerIn, ModelSkinReader.Read(bytes, null, null)!);
            Assert.True(made.Wearer != null, made.Refusal);
            var madeFor = entry.Catalog with { Options = [pick.Option] };
            var bent = BakedBodyCatalog.Lowered(madeFor, CrossSexRefit.Women, CrossSexRefit.Men, made.Wearer!, [slot],
                                                out var failed, byAlias: true, made.Planner);
            Assert.True(bent.For(slot).Count == 1, string.Join("; ", failed));
            var (p90, mean) = SizeSetRefit.BodyDistance(bent.PathOf(bent.For(slot)[0]), targetPath,
                                                        p => SizeSetRefit.BodyProbes(p));
            var clock = Stopwatch.StartNew();
            var job = CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs(bytes, gamePath, CrossSexRefit.Men, madeFor, null, men,
                b => RefitCore.Normal(b.Rel) == RefitCore.Normal(targetRel), wearerIn, maps, log));
            var size = job.Sizes.FirstOrDefault();
            measured.Add(new Measured(option ?? file, $"{entry.Name} / {pick.Option.Label}", layout, yas, p90, mean,
                                      size?.Saved != null ? size.Folds : -1, size?.Landed?.VertexCoverage ?? 0,
                                      clock.ElapsedMilliseconds, size));
        }
        foreach (var m in measured)
            o.WriteLine($"{m.Option}: made on {m.MadeFor} ({m.Layout}{(m.Yas ? ", YAS" : "")}); bent body "
                        + $"{m.BodyP90 * 1000f:F1} mm (mean {m.BodyMean * 1000f:F1}); {m.Folds} folds, {m.Coverage:P1} "
                        + $"matched, {m.Ms} ms");
        return measured;
    }

    /// <summary>The one the size set chooses among <paramref name="measured"/>: rigged alike (not YAS) first, then the
    /// nearest bent body, ties to the author's order.</summary>
    internal static Measured Chosen(List<Measured> measured)
    {
        var may = measured.Where(m => m.Size != null).ToList();
        var alike = SizeSetRefit.RiggedAlike(may, m => m.Yas, targetYas: false);
        return SizeSetRefit.Choose(alike.Select(m => (m, new SizeSetRefit.SourceDistance(m.BodyP90, m.BodyMean, 0f, 0f,
                                                                                          measured.IndexOf(m))))
                                        .ToList()).Chosen;
    }

    /// <summary>The plan's step 1 rule: the size chosen folds no more than the fewest of the rest plus max(5, 10%).</summary>
    private void AssertChosenFoldsNearTheFewest(List<Measured> measured, string what)
    {
        var made = measured.Where(m => m.Folds >= 0).ToList();
        Assert.NotEmpty(made);
        var chosen = Chosen(measured);
        int fewest = made.Min(m => m.Folds);
        int allowed = fewest + Math.Max(5, (int)Math.Ceiling(fewest * 0.1));
        o.WriteLine($"{what}: chosen {chosen.Option} ({chosen.Folds} folds); fewest {fewest}; allowed {allowed}");
        Assert.True(chosen.Folds <= allowed,
                    $"{what}: the nearest bent body ({chosen.Option}) folds {chosen.Folds}, the fewest {fewest}");
    }

    [LocalDataFact(LocalData.GameData, Sheer, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse,
                   CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-sizeset-cross-sex-3")]
    public void Sources_for_men_are_measured_sheer() // [AC-sizeset-cross-sex-3]
    {
        var women = Installed(CrossSexRefitLocalTests.Bibo);
        const string top = "chara/equipment/e6010/model/c0201e6010_top.mdl";
        AssertChosenFoldsNearTheFewest(Measure(Sheer, top, CrossSexRefitLocalTests.Tbse, TbseChestRel, women), "Sheer → TBSE");
        AssertChosenFoldsNearTheFewest(Measure(Sheer, top, CrossSexRefitLocalTests.TbseX, TbseXChestRel, women),
                                       "Sheer → TBSE-X");
    }

    [LocalDataFact(LocalData.GameData, RueTop, Yab, CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-sizeset-cross-sex-3")]
    public void Sources_for_men_are_measured_rue_top() // [AC-sizeset-cross-sex-3]
    {
        var women = Installed(Yab, CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Bibo);
        var piece = SizeSetRefit.PiecesOf(LocalData.Path(RueTop), Game, includeEnds: false).First(p => p.Slot == "_top");
        o.WriteLine($"{piece.GamePath} in {piece.Group}: {piece.Options.Count} sizes");
        var measured = Measure(RueTop, piece.GamePath, CrossSexRefitLocalTests.Tbse, TbseChestRel, women);
        if (measured.All(m => m.Size == null))
        {
            o.WriteLine("none of its sizes is on a layout the maps join: it gets no men's version");
            return;
        }
        AssertChosenFoldsNearTheFewest(measured, "Thursday top → TBSE");
    }

    [LocalDataFact(LocalData.GameData, Starlit, Yab, CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Bibo,
                   CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-sizeset-cross-sex-3")]
    public void Sources_for_men_are_measured_starlit_legs() // [AC-sizeset-cross-sex-3]
    {
        var women = Installed(Yab, CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Bibo);
        var piece = SizeSetRefit.PiecesOf(LocalData.Path(Starlit), Game, includeEnds: false).First(p => p.Slot == "_dwn");
        o.WriteLine($"{piece.GamePath} in {piece.Group}: {string.Join(", ", piece.Options.Select(x => x.Option))}");
        AssertChosenFoldsNearTheFewest(Measure(Starlit, piece.GamePath, CrossSexRefitLocalTests.Tbse, TbseLegsRel, women),
                                       "Starlit → TBSE");
    }

    /// <summary>A size set over <paramref name="bodies"/> with men's versions on, the men's skeleton from
    /// <paramref name="menSkeleton"/> (the game's when null); its notes and warnings printed.</summary>
    private SizeSetRefit.Result RunMen(string root, IReadOnlyList<string> keys, IReadOnlyList<BodyModIndex.Entry> bodies,
                                       bool remake = false, string? menSkeleton = null, Action<string>? progress = null,
                                       System.Threading.CancellationToken ct = default)
    {
        var log = Substitute.For<IPluginLog>();
        var across = new SizeSetRefit.AcrossInputs(
            path => menSkeleton != null && path == OtherRaceWearer.BaseSkeletonPath(CrossSexRefit.Men) ? menSkeleton : null);
        var result = SizeSetRefit.Run(new SizeSetRefit.Request(root, keys, false, remake, bodies, Game, Ends: false,
                                                               Across: across),
                                      Maps(), log, progress ?? (_ => { }), ct);
        o.WriteLine($"made {result.Made}");
        foreach (string note in result.Notes) o.WriteLine("note: " + note);
        foreach (string warning in result.Warnings ?? []) o.WriteLine("check: " + warning);
        return result;
    }

    private static List<(string Name, string Json)> Groups(string root)
        => (PenumbraModMeta.TryReadGroups(root) ?? []).Select(g => (g.Name, g.Group.GetRawText())).ToList();

    private static string SheerTop => "chara/equipment/e6010/model/c0201e6010_top.mdl";
    private static string SheerMenTop => RacialModelBake.WithRace(SheerTop, CrossSexRefit.Men);

    private static float[] PositionsAt(string root, string group, string option, string gamePath)
    {
        var files = PenumbraModMeta.TryReadFileOptions(root, group)!.First(x => x.Name == option).Files;
        string rel = files.First(f => string.Equals(f.Key, gamePath, StringComparison.OrdinalIgnoreCase)).Value;
        return ModelPartReader.Read(File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, rel)))!.Positions;
    }

    [LocalDataFact(LocalData.GameData, Sheer, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse,
                   CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-sizeset-cross-sex-2")]
    [Trait("Plan", "AC-sizeset-cross-sex-4")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-19")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-24")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-3")]
    public void Sheer_elegance_gains_mens_versions() // [AC-sizeset-cross-sex-2] [AC-sizeset-cross-sex-4] [EDGE-sizeset-cross-sex-19] [EDGE-sizeset-cross-sex-24] [EDGE-sizeset-cross-sex-3]
    {
        string root = Copy(Sheer);
        var across = SizeSetRefit.AcrossPieces(SizeSetRefit.PiecesOf(root, Game, includeEnds: false));
        o.WriteLine("crossed: " + string.Join(", ", across.Select(p => p.GamePath)));
        Assert.Contains(across, p => p.GamePath == SheerTop);
        // The mod ships the game's own men's trousers at c0101e6107_dwn: not a piece of the author's, so the women's legs
        // are crossed too.
        var legs = Assert.Single(across, p => p.Slot == "_dwn");
        var before = Groups(root);

        var result = RunMen(root, ["tbse@women", "tbse-x@women"],
                            Installed(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX));
        // The top on both. The legs match too little of TBSE's legs (88% of the body, measured 2026-10-21) and share no
        // layout with TBSE-X's: left out, saying why, never written.
        Assert.Equal(2, result.Made);
        Assert.Contains(result.Notes, n => n.StartsWith(legs.Label + ": \"TBSE\"", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith(legs.Label + ": \"TBSE-X\"", StringComparison.Ordinal));
        across = [across.First(p => p.GamePath == SheerTop)];

        // One group appended after every other, the author's groups byte for byte as they were.
        var after = Groups(root);
        Assert.Equal(before.Count + 1, after.Count);
        for (int i = 0; i < before.Count; i++) Assert.Equal(before[i], after[i]);
        string men = Localization.Strings.SizeSet.MensGroup;
        Assert.Equal(men, after[^1].Name);
        Assert.Equal(BodyRetargetWriter.OriginalOption, PenumbraModMeta.SingleDefault(root, men));
        var options = PenumbraModMeta.TryReadFileOptions(root, men)!;
        Assert.Equal([BodyRetargetWriter.OriginalOption, "TBSE", "TBSE-X"], options.Select(x => x.Name));
        Assert.Empty(options[0].Files);
        foreach (var option in options.Skip(1))
        {
            foreach (var piece in across)
                Assert.Contains(option.Files.Keys, k => string.Equals(k, RacialModelBake.WithRace(piece.GamePath, CrossSexRefit.Men),
                                                                      StringComparison.OrdinalIgnoreCase));
            // Only the men's paths and the materials it publishes: nothing at a women's path.
            Assert.DoesNotContain(option.Files.Keys, k => k.Contains("/c0201", StringComparison.OrdinalIgnoreCase)
                                                          && k.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(option.Files.Keys, k => string.Equals(k, RacialModelBake.WithRace(legs.GamePath, CrossSexRefit.Men),
                                                                        StringComparison.OrdinalIgnoreCase));
            string meta = System.Text.Json.JsonSerializer.Serialize(option.Manipulations);
            Assert.Contains("Eqdp", meta);
        }

        // Its folds are said under "Check in game", never a refusal; and that men get one of the two variants.
        Assert.Contains(result.Warnings!, w => w.Contains("\"TBSE\"") && w.Contains("cloth face"));
        Assert.Contains(result.Warnings!, w => w.Contains("\"TBSE-X\"") && w.Contains("cloth face"));
        Assert.Contains(result.Warnings!, w => w == string.Format(Localization.Strings.SizeSet.OneVariantFmt,
                                                                  across.First(p => p.GamePath == SheerTop).Label, "SFW- Opaque"));
    }

    [LocalDataFact(LocalData.GameData, Sheer, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-sizeset-cross-sex-4")]
    public void The_size_sets_mens_model_is_the_jobs() // [AC-sizeset-cross-sex-4]
    {
        string root = Copy(Sheer);
        var women = Installed(CrossSexRefitLocalTests.Bibo);
        var result = RunMen(root, ["tbse@women"], Installed(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse));
        Assert.True(result.Made >= 1);
        var job = Chosen(Measure(Sheer, SheerTop, CrossSexRefitLocalTests.Tbse, TbseChestRel, women));
        var want = ModelPartReader.Read(job.Size!.Saved!.Model)!.Positions;
        var saved = PositionsAt(root, Localization.Strings.SizeSet.MensGroup, "TBSE", SheerMenTop);
        Assert.Equal(want.Length, saved.Length);
        for (int i = 0; i < want.Length; i++) Assert.True(MathF.Abs(want[i] - saved[i]) < 1e-5f, $"position {i} differs");
    }

    [LocalDataFact(LocalData.GameData, Sheer, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse,
                   CrossSexRefitLocalTests.TbseX, LocalData.TbseUvMaps)]
    [Trait("Plan", "AC-sizeset-cross-sex-7")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-12")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-13")]
    public void Mens_versions_rerun_remake_cancel_and_undo() // [AC-sizeset-cross-sex-7] [EDGE-sizeset-cross-sex-12] [EDGE-sizeset-cross-sex-13]
    {
        string root = Copy(Sheer);
        var bodies = Installed(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.TbseX);
        string men = Localization.Strings.SizeSet.MensGroup;
        int made = RunMen(root, ["tbse@women"], bodies).Made;
        Assert.True(made >= 1);
        int at = Groups(root).FindIndex(g => g.Name == men);

        // Again: nothing to make.
        Assert.Equal(0, RunMen(root, ["tbse@women"], bodies).Made);
        // Made again: replaced in place.
        Assert.Equal(made, RunMen(root, ["tbse@women"], bodies, remake: true).Made);
        Assert.Equal(at, Groups(root).FindIndex(g => g.Name == men));
        Assert.Equal(["Original", "TBSE"], PenumbraModMeta.TryReadFileOptions(root, men)!.Select(x => x.Name));

        // Stopped while TBSE-X is made: TBSE stays, TBSE-X is not saved, the default is "Original".
        using var cancel = new System.Threading.CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => RunMen(root, ["tbse@women", "tbse-x@women"], bodies, remake: true,
            progress: p => { if (p.Contains("TBSE-X", StringComparison.Ordinal)) cancel.Cancel(); }, ct: cancel.Token));
        Assert.Equal(["Original", "TBSE"], PenumbraModMeta.TryReadFileOptions(root, men)!.Select(x => x.Name));
        Assert.Equal(BodyRetargetWriter.OriginalOption, PenumbraModMeta.SingleDefault(root, men));

        // Undone: the group goes once only "Original" is left; the author's groups as they were.
        Assert.True(BodyRetargetWriter.Undo(root, men, "TBSE").Ok);
        Assert.DoesNotContain(Groups(root), g => g.Name == men);
    }

    public static TheoryData<string?> MenSkeletons => new() { null, CrossSexRefitLocalTests.IvcsMen };

    [LocalDataTheory(LocalData.GameData, Starlit, Yab, CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Bibo,
                     CrossSexRefitLocalTests.Tbse, CrossSexRefitLocalTests.IvcsMen, LocalData.TbseUvMaps)]
    [MemberData(nameof(MenSkeletons))]
    [Trait("Plan", "AC-sizeset-cross-sex-8")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-8")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-9")]
    public void Starlit_gains_mens_versions_with_its_bones_moved(string? skeleton) // [AC-sizeset-cross-sex-8] [EDGE-sizeset-cross-sex-8] [EDGE-sizeset-cross-sex-9]
    {
        string root = Copy(Starlit);
        var result = RunMen(root, ["tbse@women"],
                            Installed(Yab, CrossSexRefitLocalTests.Rue, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse),
                            menSkeleton: skeleton == null ? null : LocalData.Path(skeleton));
        Assert.Equal(1, result.Made);
        // Every size of Starlit is rigged to YAS's own bones: moved onto the men's, and said.
        Assert.Contains(result.Warnings!, w => w.Contains("\"TBSE\"") && w.Contains("→"));
        var piece = SizeSetRefit.PiecesOf(root, Game, includeEnds: false).First(p => p.Slot == "_dwn");
        var options = PenumbraModMeta.TryReadFileOptions(root, Localization.Strings.SizeSet.MensGroup)!;
        Assert.Contains(options.First(x => x.Name == "TBSE").Files.Keys,
                        k => string.Equals(k, RacialModelBake.WithRace(piece.GamePath, CrossSexRefit.Men), StringComparison.OrdinalIgnoreCase));
    }

    [LocalDataFact(LocalData.GameData, Sheer, CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse, LocalData.TbseUvMaps)]
    [Trait("Plan", "EDGE-sizeset-cross-sex-22")]
    [Trait("Plan", "EDGE-sizeset-cross-sex-23")]
    public void Unreadable_and_missing_bodies_are_said_once() // [EDGE-sizeset-cross-sex-22] [EDGE-sizeset-cross-sex-23]
    {
        // TBSE-X not installed: said once per piece, and TBSE still made.
        string root = Copy(Sheer);
        var result = RunMen(root, ["tbse@women", "tbse-x@women"], Installed(CrossSexRefitLocalTests.Bibo, CrossSexRefitLocalTests.Tbse));
        var top = SizeSetRefit.PiecesOf(root, Game, includeEnds: false).First(p => p.GamePath == SheerTop);
        Assert.Single(result.Notes, n => n == string.Format(Localization.Strings.SizeSet.NotInstalledFmt, top.Label, "TBSE-X"));
        Assert.True(result.Made >= 1);

        // No women's body its sizes read as made on: said once, nothing written.
        string other = Copy(Sheer);
        var before = Groups(other);
        result = RunMen(other, ["tbse@women"], Installed(CrossSexRefitLocalTests.Tbse));
        Assert.Single(result.Notes, n => n == string.Format(Localization.Strings.SizeSet.UnreadableFmt, top.Label));
        Assert.Equal(before, Groups(other));
    }

    internal static string TbseChestRel =>SupportedBodies.Of("tbse", "_top", false)!.Sizes[0].Rel;
    internal static string TbseXChestRel => SupportedBodies.Of("tbse-x", "_top", false)!.Sizes[0].Rel;
    internal static string TbseLegsRel => SupportedBodies.Of("tbse", "_dwn", false)!.Sizes[0].Rel;
}
