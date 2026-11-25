extern alias RefitCli;
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
/// Every size of "Starlight"'s Valentione Skirt (the twelve its author made with the Studio's Body size), refit headless
/// from the author's own skirt as it is and as the build before the fix made it (see
/// <see cref="StarlightBottomsLocalTests"/>, which pins Tre Medium in the suite). Never reads the mod's own refits. On
/// demand: two refits a size.
/// </summary>
[Trait("Suite", "Diagnostic")]
[Trait("Speed", "Slow")]
public sealed class StarlightBottomsDiagnosticTests(ITestOutputHelper o)
{
    /// <summary>Each size: its body mod and the body file, as the mod's record names them.</summary>
    internal static readonly Dictionary<string, (string Mod, string Rel)> Sizes = new()
    {
        ["Neolithe S"] = (LocalData.NeolitheMod, "default legs - smallclothes\\sfw small.mdl"),
        ["Neolithe M"] = (LocalData.NeolitheMod, "default legs - smallclothes\\sfw medium.mdl"),
        ["Neolithe L"] = (LocalData.NeolitheMod, "default legs - smallclothes\\sfw large.mdl"),
        ["Bibo S"] = (LocalData.BiboPlusMod, "bottoms - small clothes\\type a - small\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Bibo M"] = (LocalData.BiboPlusMod, "bottoms - small clothes\\type a - medium\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Bibo L"] = (LocalData.BiboPlusMod, "bottoms - small clothes\\type a - large\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Rue WC"] = (LocalData.RueMod, "files\\legs - smallclothes\\yiggle - watermelon crushers - a\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["YAB WC"] = (LocalData.YabMod, "files\\legs - smallclothes\\yiggle - watermelon crushers - a\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["YAB SC"] = (LocalData.YabMod, "files\\legs - smallclothes\\yiggle - skull crushers - a\\chara\\equipment\\e0000\\model\\c0201e0000_dwn.mdl"),
        ["Tre Medium"] = (LocalData.TreMod, "files\\common\\15\\c0201e0000_dwn.mdl"),
        ["Tre Large"] = (LocalData.TreMod, "files\\common\\10\\c0201e0000_dwn.mdl"),
        ["Rosaline"] = (LocalData.RosalineMod, "files\\common\\13\\c0201e0000_dwn.mdl"),
    };

    /// <summary>
    /// Measured 2026-11-24 before the fix (both knobs on): skin points through the stockings. After it: 0 on every size.
    /// </summary>
    internal static readonly Dictionary<string, int> References = new()
    {
        ["Neolithe S"] = 178, ["Neolithe M"] = 201, ["Neolithe L"] = 181, ["Bibo S"] = 124, ["Bibo M"] = 130,
        ["Bibo L"] = 223, ["Rue WC"] = 278, ["YAB WC"] = 278, ["YAB SC"] = 479, ["Tre Medium"] = 642,
        ["Tre Large"] = 642, ["Rosaline"] = 330,
    };

    /// <summary>The thighs' band, where the author pressed the skin in under the stocking band (y 0.70-0.90 m, outside the
    /// crotch): no new-body skin left out there.</summary>
    private static bool InBand(Vector3 p) => p.Y is > 0.70f and < 0.90f && !StarlightMeasure.InCrotch(p);

    public static TheoryData<string> Names => new(Sizes.Keys);

    [LocalDataTheory(LocalData.StarlightMod, LocalData.TightFirmMod, LocalData.NeolitheMod, LocalData.BiboPlusMod,
                     LocalData.RueMod, LocalData.YabMod, LocalData.TreMod, LocalData.RosalineMod, LocalData.UvMaps)]
    [MemberData(nameof(Names))]
    [Trait("Plan", "AC-starlight-bottoms-1")]
    [Trait("Plan", "AC-starlight-bottoms-2")]
    [Trait("Plan", "AC-starlight-bottoms-3")]
    [Trait("Plan", "EDGE-starlight-bottoms-12")]
    [Trait("Plan", "EDGE-starlight-bottoms-21")]
    public void Every_bottoms_size_is_clean(string size)
    {
        var (mod, rel) = Sizes[size];
        string dir = Path.Combine(Path.GetTempPath(), "proteus-starlight-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = ModelPartReader.Read(File.ReadAllBytes(
                Path.Combine(LocalData.Path(LocalData.StarlightMod), StarlightBottomsLocalTests.Garment)))!;
            var body = ModelPartReader.Read(File.ReadAllBytes(Path.Combine(LocalData.Path(mod), rel)))!;
            var now = Refit(dir, mod, rel, "");
            var before = Refit(dir, mod, rel, StarlightBottomsLocalTests.Before);

            int stockNow = StarlightMeasure.StockingSkinOut(now), stockBefore = StarlightMeasure.StockingSkinOut(before);
            var outNow = StarlightMeasure.LeftOutBodySkin(body, now);
            var outBefore = StarlightMeasure.LeftOutBodySkin(body, before);
            int holeNow = StarlightMeasure.LeftOutInAuthorsHole(body, now), holeBefore = StarlightMeasure.LeftOutInAuthorsHole(body, before);
            int smallNow = Smallclothes(now), smallBefore = Smallclothes(before);
            var turnedNow = StarlightMeasure.TurnedOver(original, now);
            var turnedBefore = StarlightMeasure.TurnedOver(original, before);
            o.WriteLine($"{size}: stockings {stockBefore} -> {stockNow}; left out on the legs {outBefore.Count} -> {outNow.Count} "
                        + $"(band {outBefore.Count(InBand)} -> {outNow.Count(InBand)}); crotch hole {holeBefore} -> {holeNow}; "
                        + $"smallclothes {smallBefore} -> {smallNow}; turned over {turnedBefore.Values.Sum()} -> {turnedNow.Values.Sum()}");

            Assert.Equal(References[size], stockBefore);
            Assert.True(stockNow <= stockBefore / 10, $"{stockNow} skin points through the stockings ({stockBefore} before)");
            Assert.DoesNotContain(outNow, InBand);
            Assert.True(outNow.Count <= outBefore.Count, $"{outNow.Count} triangles left out on the legs ({outBefore.Count} before)");
            Assert.True(holeNow >= holeBefore * 0.9f, $"the crotch hole lost {holeBefore - holeNow} of {holeBefore}");
            Assert.Equal(smallBefore, smallNow);
            foreach (var (part, n) in turnedNow) Assert.True(n <= turnedBefore.GetValueOrDefault(part), $"part {part} folds more");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int Smallclothes(ModelParts m)
        => m.Parts.Where(p => p.Island < 0 && BodySurface.IsSmallclothes(p.Material)).Sum(p => p.Triangles.Length / 3);

    private static ModelParts Refit(string dir, string mod, string rel, string tune)
    {
        string outDir = Path.Combine(dir, tune.Length == 0 ? "as-is" : "before");
        Directory.CreateDirectory(outDir);
        var args = new List<string>
        {
            "--garment", Path.Combine(LocalData.Path(LocalData.StarlightMod), StarlightBottomsLocalTests.Garment),
            "--from-root", LocalData.Path(LocalData.TightFirmMod), "--from", StarlightBottomsLocalTests.MadeOn,
            "--body-root", LocalData.Path(mod), "--to", rel + "=Size", "--slot", "_dwn",
            "--uvmaps", LocalData.Path(LocalData.UvMaps), "--out-dir", outDir,
        };
        if (tune.Length > 0) args.AddRange(["--tune", tune]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var console = Console.Out;
        int exit;
        Console.SetOut(stdout);
        try { exit = RefitCli::Proteus.Refit.Program.Dispatch([.. args], stdout, stderr); }
        finally { Console.SetOut(console); }
        if (exit != 0) throw new InvalidOperationException($"refit failed: {stderr}");
        return ModelPartReader.Read(File.ReadAllBytes(Path.Combine(outDir, "Size.mdl")))!;
    }
}
