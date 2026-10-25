using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NSubstitute;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// A size the detector misread is no source (<c>SizeSetRefit.Source.cs</c>, <see cref="SizeSetRefit.MisfitMm"/>): Luxe's
/// "Uranus", read as Lithe Almond Large, lay nearest Rue Medium by the bodies while its own skin lay far off the body it was
/// read as, and Rue M cut from it came out a Rue L. Run over every installed body, as the CLI's <c>--bodies all</c> does:
/// which body a size reads as depends on which are there.
/// </summary>
public class SizeSetSkinMisfitTests(ITestOutputHelper output)
{
    internal const string LuxePack = LocalData.ModPacks + ":Luxe - by Solona.pmp";
    internal const string MisaPack = LocalData.ModPacks + ":Misa (Default) v1.1.0.pmp";
    internal const string CatsuitPack = LocalData.ModPacks + ":Catsuit - by Solona.pmp";
    internal const string AbyssPack = LocalData.ModPacks + ":Abyss - by Solona.pmp";
    internal const string TwoBPack = LocalData.ModPacks + ":2B-2P - by Solona.pmp";
    internal const string SiriusPack = LocalData.Mods + ":Sirius - by Solona.pmp";
    internal const string Darling = LocalData.Mods + ":Darling - by Solona";

    /// <summary>A pack unpacked into a work folder and converted to v4 when it is v3, as the CLI sizes it.</summary>
    internal static string Unpacked(string pack)
    {
        string copy = SizeSetBatchData.NewCopy();
        ZipFile.ExtractToDirectory(LocalData.Path(pack), copy);
        PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
        return copy;
    }

    /// <summary>
    /// How far each of the author's women's chest and legs sizes lies from the body it reads as made on, read as a size set
    /// over every installed body with every key reads them (the first key's body, Neolithe, preferred on a tie).
    /// </summary>
    private static List<(string Piece, string Option, string ReadAs, float? Own)> OwnFits(
        string root, IReadOnlyList<BodyModIndex.Entry> bodies)
    {
        var mods = bodies.Select(b => (b.Dir, b.Catalog)).ToList();
        string preferred = SupportedBodies.Find(SupportedBodies.Of("neolithe", "_top", false)!, bodies)?.Dir ?? mods[0].Dir;
        var rows = new List<(string, string, string, float?)>();
        foreach (var piece in SizeSetRefit.PiecesOf(root, null, includeEnds: false)
                                          .Where(p => p.Race == "0201" && p.Slot is "_top" or "_dwn"))
            foreach (var (option, file) in piece.Options)
            {
                if (option == null) continue;
                byte[] bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(root, file));
                if (ModelPartReader.Read(bytes) is not { } garment) continue;
                var bones = new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal);
                var pick = RefitCore.DetectSource(garment, bones, piece.Slot, "0201", mods, preferred, _ => true, _ => { });
                if (pick == null || !AutoRefitDecisions.Proceed(pick.Confidence)) continue;
                string sourcePath = mods.Single(m => m.Dir == pick.Dir).Catalog.PathOf(pick.Option);
                var probes = BodySizeMatch.NearProbePoints(BodySizeMatch.SkinPoints(garment));
                rows.Add((piece.Label, option, $"{pick.Dir} / {pick.Option.Label}", SizeSetRefit.OwnSkinFit(probes, sourcePath)));
            }
        return rows;
    }

    /// <summary>
    /// The 12 mm margin against real mods, read before anything was wired to it: Luxe's Uranus (chest) and Darling's
    /// Nymph (legs) lie more than 3 mm past it, and none of the sizes made right — Misa's YAB Large, the capped skins of
    /// Catsuit, Abyss, 2B and Sirius, the six YAB chests — is past it at all.
    /// </summary>
    [LocalDataFact(LocalData.Mods, LuxePack, MisaPack, CatsuitPack, AbyssPack, TwoBPack, SiriusPack, Darling,
                   SizeSetSourceTests.StarlightBikini, SizeSetSourceTests.CallersSwimwear, SizeSetSourceTests.ButterflyCorset,
                   SizeSetSourceTests.HeartHalter, SizeSetSourceTests.Cutout, SizeSetSourceTests.Geometric)]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-15")]
    public void Own_skin_fit_separates_misread_sizes() // [EDGE-sizeset-skin-misfit-15]
    {
        var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
        var misreads = new HashSet<(string Mod, string Slot, string Option)>
        {
            ("luxe", "_top", "Uranus"),
            ("darling", "_dwn", "Nymph"),
        };
        // Printed, not judged: Darling's chest Nymph, read the same weak way as Uranus, may or may not be past it.
        var unjudged = new HashSet<(string, string, string)> { ("darling", "_top", "Nymph") };
        var mods = new (string Name, string Need, bool Pack)[]
        {
            ("luxe", LuxePack, true), ("misa", MisaPack, true), ("catsuit", CatsuitPack, true), ("abyss", AbyssPack, true),
            ("2b", TwoBPack, true), ("sirius", SiriusPack, true), ("darling", Darling, false),
            ("starlight", SizeSetSourceTests.StarlightBikini, false), ("callers", SizeSetSourceTests.CallersSwimwear, false),
            ("butterfly", SizeSetSourceTests.ButterflyCorset, false), ("heart", SizeSetSourceTests.HeartHalter, false),
            ("cutout", SizeSetSourceTests.Cutout, false), ("geometric", SizeSetSourceTests.Geometric, false),
        };
        float worstLegitimate = 0f;
        var seen = new HashSet<(string, string, string)>();
        var failures = new List<string>();
        foreach (var (name, need, pack) in mods)
        {
            string root = pack ? Unpacked(need) : LocalData.Path(need);
            try
            {
                foreach (var piece in OwnFits(root, bodies).GroupBy(r => r.Piece))
                {
                    var known = piece.Where(r => r.Own != null).ToList();
                    foreach (var r in piece)
                        output.WriteLine($"{name} {r.Piece} {r.Option} ({r.ReadAs}): own body "
                                         + (r.Own is { } o ? $"{o * 1000f:F1} mm" : "unknown"));
                    if (known.Count < 2) continue;
                    float best = known.Min(r => r.Own!.Value) * 1000f;
                    string slot = piece.Key.StartsWith("Legs", StringComparison.Ordinal) ? "_dwn" : "_top";
                    foreach (var r in known)
                    {
                        float over = r.Own!.Value * 1000f - best;
                        var key = (name, slot, r.Option);
                        if (misreads.Contains(key))
                        {
                            seen.Add(key);
                            if (over <= SizeSetRefit.MisfitMm + 3f)
                                failures.Add($"{name} {r.Piece} {r.Option}: {over:F1} mm past the best, not clear of the margin");
                        }
                        else if (!unjudged.Contains(key))
                        {
                            worstLegitimate = MathF.Max(worstLegitimate, over);
                            if (over > SizeSetRefit.MisfitMm)
                                failures.Add($"{name} {r.Piece} {r.Option}: {over:F1} mm past the best, would be passed over");
                        }
                    }
                }
            }
            finally
            {
                if (pack) SizeSetBatchData.Delete(root);
            }
        }
        output.WriteLine($"largest legitimate gap {worstLegitimate:F1} mm (margin {SizeSetRefit.MisfitMm} mm)");
        Assert.Equal(misreads, seen);
        Assert.Empty(failures);
    }

    // ── the choice, on distances alone ──────────────────────────────────────────────────────────────────────────

    private static SizeSetRefit.SourceDistance D(float bodyMm, float bodyMeanMm, float skinMm = 0, float skinMeanMm = 0,
                                                 int order = 0, float? own = null)
        => new(bodyMm / 1000f, bodyMeanMm / 1000f, skinMm / 1000f, skinMeanMm / 1000f, order, own / 1000f);

    [Fact]
    [Trait("Plan", "AC-sizeset-skin-misfit-6")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-14")]
    public void Choose_rules_out_a_misfit_with_no_skin_reading() // [AC-sizeset-skin-misfit-6] [EDGE-sizeset-skin-misfit-14]
    {
        // A men's version measures between bodies alone (skin 0): the misfit's own skin still tells.
        var (chosen, step, gap) = SizeSetRefit.Choose([("misread", D(5, 2, 0, 0, own: 30)), ("fits", D(6, 2, 0, 0, order: 1, own: 3))]);
        Assert.Equal(("fits", "skin fit"), (chosen, step));
        Assert.Equal(0.027f, gap, 4);
        // Unknown fits: as before.
        Assert.Equal(("misread", "body"), Pick(SizeSetRefit.Choose([("misread", D(5, 2)), ("fits", D(6, 2, order: 1))])));
    }

    private static (string, string) Pick((string Chosen, string Step, float Gap) r) => (r.Chosen, r.Step);

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-1")]
    public void Choose_keeps_a_lone_misfit() // [EDGE-sizeset-skin-misfit-1]
        => Assert.Equal(("only", "body", 0f), SizeSetRefit.Choose([("only", D(3, 1, own: 40))]));

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-3")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-4")]
    public void Unknown_fit_is_never_judged() // [EDGE-sizeset-skin-misfit-3] [EDGE-sizeset-skin-misfit-4]
    {
        Assert.Empty(SizeSetRefit.Misfits([("a", D(1, 1)), ("b", D(1, 1, own: 2)), ("c", D(1, 1))]));
        Assert.Empty(SizeSetRefit.Misfits([("a", D(1, 1)), ("b", D(1, 1, own: 30)), ("c", D(1, 1, own: 31))]));
        (string, SizeSetRefit.SourceDistance)[] mixed = [("S", D(44.4f, 11.8f)), ("M", D(16f, 4.7f, order: 1)), ("L", D(0.3f, 0.7f, order: 2))];
        Assert.Empty(SizeSetRefit.Misfits(mixed));
        Assert.Equal(("L", "body", 0.0157f), Round(SizeSetRefit.Choose(mixed)));
        // One known among unknowns is no best to measure by: the unknown far-off one is not passed over.
        Assert.Equal("L", SizeSetRefit.Choose([("S", D(44.4f, 11.8f, own: 2)), ("L", D(0.3f, 0.7f, order: 1))]).Chosen);
        // A misfit passed over leaving an unknown one nearest: that one is chosen, said by the key that chose it.
        Assert.Equal(("unknown", "body", 0.002f), Round(SizeSetRefit.Choose([("misread", D(5, 2, own: 30)),
                                                                             ("unknown", D(6, 2, order: 1)),
                                                                             ("fits", D(8, 2, order: 2, own: 2))])));
    }

    private static (string, string, float) Round((string Chosen, string Step, float Gap) r) => (r.Chosen, r.Step, MathF.Round(r.Gap, 4));

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-5")]
    public void Own_fit_of_an_unreadable_body_is_unknown() // [EDGE-sizeset-skin-misfit-5]
    {
        var near = new[] { new System.Numerics.Vector3(0.1f, 0.1f, 0.005f), new System.Numerics.Vector3(0.2f, 0.2f, 0.005f) };
        Assert.Null(SizeSetRefit.OwnSkinFit(near, Path.Combine(Path.GetTempPath(), "proteus-none-" + Guid.NewGuid().ToString("N") + ".mdl")));
        string path = Path.Combine(Path.GetTempPath(), "proteus-ownfit-" + Guid.NewGuid().ToString("N") + ".mdl");
        try
        {
            File.WriteAllBytes(path, SizeSetSourceTests.SkinSheet(0.4f, 20));
            Assert.Null(SizeSetRefit.OwnSkinFit([], path));
            Assert.Equal(0.005f, SizeSetRefit.OwnSkinFit(near, path)!.Value, 3);
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Null(SizeSetRefit.OwnSkinFit(near, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-6")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-16")]
    public void Misfit_margin_is_strict_and_does_not_chain() // [EDGE-sizeset-skin-misfit-6] [EDGE-sizeset-skin-misfit-16]
    {
        Assert.Empty(SizeSetRefit.Misfits([("a", D(1, 1, own: 2)), ("b", D(1, 1, own: 14))]));
        Assert.Equal([1], SizeSetRefit.Misfits([("a", D(1, 1, own: 2)), ("b", D(1, 1, own: 14.1f))]));
        Assert.Equal([2], SizeSetRefit.Misfits([("a", D(1, 1, own: 2)), ("b", D(1, 1, own: 13)), ("c", D(1, 1, own: 24))]));
    }

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-7")]
    public void Skin_below_body_never_counts_against() // [EDGE-sizeset-skin-misfit-7]
    {
        // Luxe's Rue L: Small's skin far below its body distance, every size sitting on its own body.
        var (chosen, step, _) = SizeSetRefit.Choose([
            ("Small", D(37.0f, 9.9f, 15.8f, 5.2f, 0, own: 3)), ("Medium", D(30.9f, 8.3f, 12.2f, 4.3f, 1, own: 3)),
            ("Large", D(24.3f, 6.9f, 10.1f, 3.5f, 2, own: 3)), ("XLarge", D(15.4f, 5.4f, 8.7f, 3.2f, 3, own: 3)),
            ("YAB Large", D(0.3f, 0.7f, 3.1f, 1.8f, 4, own: 3))]);
        Assert.Equal(("YAB Large", "body"), (chosen, step));
    }

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-8")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-9")]
    public void Fit_step_names_itself_only_when_it_decides() // [EDGE-sizeset-skin-misfit-8] [EDGE-sizeset-skin-misfit-9]
    {
        // Luxe's Rue M: Uranus nearest by the bodies, its skin off its own; Large is chosen as the body would have chosen
        // among the rest, and it is the misfit filter that changed the choice.
        (string, SizeSetRefit.SourceDistance)[] rueM =
        [
            ("Small", D(17.6f, 5.7f, 12.7f, 4.3f, 0, own: 3)), ("Medium", D(12.2f, 4.0f, 10.0f, 3.3f, 1, own: 4)),
            ("Large", D(8.3f, 2.8f, 6.6f, 2.5f, 2, own: 4)), ("XLarge", D(10.7f, 3.6f, 9.2f, 3.3f, 3, own: 5)),
            ("YAB Large", D(15.9f, 4.7f, 15.0f, 5.4f, 4, own: 4)), ("Uranus", D(7.4f, 3.1f, 26.0f, 5.9f, 5, own: 30)),
        ];
        Assert.Equal(("Large", "skin fit", 0.026f), Round(SizeSetRefit.Choose(rueM)));
        // A misfit that would not have been chosen anyway leaves the choice and its words as they were.
        (string, SizeSetRefit.SourceDistance)[] farMisfit = [("near", D(3, 1, own: 2)), ("far", D(20, 5, order: 1, own: 30))];
        Assert.Equal(("near", "body", 0.017f), Round(SizeSetRefit.Choose(farMisfit)));
        // Two kept after the misfit: the body decides between them, as before.
        var (chosen, step, _) = SizeSetRefit.Choose([("misread", D(5, 2, own: 30)), ("a", D(6, 2, order: 1, own: 3)),
                                                     ("b", D(9, 3, order: 2, own: 4))]);
        Assert.Equal(("a", "skin fit"), (chosen, step));
    }

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-10")]
    public void A_misfit_rigged_alone_is_still_used() // [EDGE-sizeset-skin-misfit-10]
    {
        (string Name, bool Yas, SizeSetRefit.SourceDistance D)[] sizes = [("YAB misread", true, D(5, 2, own: 30)), ("Neolithe", false, D(6, 2, order: 1, own: 3))];
        var alike = SizeSetRefit.RiggedAlike(sizes, s => s.Yas, targetYas: true);
        Assert.Equal(["YAB misread"], alike.Select(s => s.Name));
        Assert.Equal("YAB misread", SizeSetRefit.Choose(alike.Select(s => (s.Name, s.D)).ToList()).Chosen);
    }

    [Fact]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-11")]
    public void A_better_fitting_duplicate_is_kept() // [EDGE-sizeset-skin-misfit-11]
    {
        Assert.True(SizeSetRefit.KeepsBoth(0.030f, 0.003f));
        Assert.False(SizeSetRefit.KeepsBoth(0.003f, 0.030f));
        Assert.False(SizeSetRefit.KeepsBoth(null, 0.003f));
        Assert.False(SizeSetRefit.KeepsBoth(0.003f, null));
        Assert.False(SizeSetRefit.KeepsBoth(0.010f, 0.003f));
    }

    // ── whole runs on the reported mods, over every installed body ─────────────────────────────────────────────

    private readonly List<(string Piece, string Line)> logged = [];

    /// <summary>The batch job over every body installed (ModDirs null, as <c>--bodies all</c> reads them); its source
    /// lines kept.</summary>
    private SizeSetRefit.RunLog Run(string copy, IReadOnlyList<string> keys, bool men = false)
    {
        var log = Substitute.For<Dalamud.Plugin.Services.IPluginLog>();
        var runLog = new SizeSetRefit.RunLog();
        SizeSetBatch.Run(new SizeSetBatch.Inputs(copy, LocalData.Path(LocalData.Mods), keys, men, false,
                                                 men ? SizeSetBatchData.Game() : _ => null,
                                                 new UVRemapService(log, LocalData.Path(men ? LocalData.TbseUvMaps : LocalData.UvMaps)), log),
                         runLog, _ => { }, CancellationToken.None);
        logged.Clear();
        foreach (var call in log.ReceivedCalls())
        {
            var args = call.GetArguments().Skip(1).FirstOrDefault() as object[] ?? [];
            if (args.Length > 1 && args[0] is string piece && args[1] is string line) logged.Add((piece, line));
        }
        foreach (var (piece, line) in logged.Where(l => l.Line.Contains(" from ", StringComparison.Ordinal)))
            output.WriteLine($"{piece}: {line}");
        return runLog;
    }

    private string SourceOf(string piece, string option)
        => logged.Single(l => l.Piece == piece && l.Line.StartsWith(option + " from ", StringComparison.Ordinal)).Line;

    /// <summary>The candidates of a source line, by name: "Large: body 8.3 mm (mean 2.8), skin …".</summary>
    private static Dictionary<string, string> Candidates(string line)
        => Regex.Matches(line, @"(?<=; )([^;:]+): (body [^;]*?)(?=; |\)$)")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static float BodyMm(string candidate)
        => float.Parse(Regex.Match(candidate, @"^body ([\d.]+) mm").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    private const string PassedOver = ", passed over: its skin is off the body it was read as";
    private const string LuxeTop = "chara/equipment/e6045/model/c0201e6045_top.mdl";
    private static readonly string[] LuxeMisread = ["Rue M", "Neolithe M", "YAB Buff M", "Tre L"];

    [LocalDataFact(LocalData.Mods, LuxePack, LocalData.UvMaps)]
    [Trait("Plan", "AC-sizeset-skin-misfit-1")]
    [Trait("Plan", "AC-sizeset-skin-misfit-2")]
    [Trait("Plan", "AC-sizeset-skin-misfit-3")]
    [Trait("Plan", "AC-sizeset-skin-misfit-7")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-11")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-12")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-13")]
    public void Luxe_sizes_pass_over_the_misread_Uranus()
        // [AC-sizeset-skin-misfit-1] [AC-sizeset-skin-misfit-2] [AC-sizeset-skin-misfit-3] [AC-sizeset-skin-misfit-7]
        // [EDGE-sizeset-skin-misfit-11] [EDGE-sizeset-skin-misfit-12] [EDGE-sizeset-skin-misfit-13]
    {
        string copy = Unpacked(LuxePack);
        try
        {
            Run(copy, ["neolithe", "rue", "yab", "yab-buff", "tre"]);
            const string body = "Body — e6045", legs = "Legs — e6045";

            // The case is there: by the bodies alone, Uranus is still the nearest of the author's sizes to Rue M.
            var rueM = Candidates(SourceOf(body, "Rue M"));
            Assert.True(BodyMm(rueM["Uranus"]) <= rueM.Values.Min(BodyMm) + SizeSetRefit.TieMm,
                        "Uranus no longer reads nearest Rue M by the bodies: the misread this checks is not exercised");

            foreach (string size in LuxeMisread)
            {
                string line = SourceOf(body, size);
                Assert.DoesNotMatch($"^{Regex.Escape(size)} from Uranus ", line);
                var candidates = Candidates(line);
                Assert.EndsWith(PassedOver, candidates["Uranus"]);
                // [AC-7] Every candidate says how far its skin lies from its own body.
                Assert.All(candidates.Values, c => Assert.Contains(", own body ", c));
                Assert.Single(candidates.Values, c => c.EndsWith(PassedOver, StringComparison.Ordinal));

                // [AC-3] The legs follow the chest's new choice.
                string chosen = Regex.Match(line, $"^{Regex.Escape(size)} from (.+?) \\(by ").Groups[1].Value;
                Assert.Contains(logged, l => l.Piece == legs && l.Line == $"{size} from \"{chosen}\", as the outfit's lead piece in that option was");
            }
            // [EDGE-13] Legs whose lead's option gives them nothing they can read go to the nearest, measured as any size is.
            foreach (string size in new[] { "Rue L", "YAB L" })
            {
                Assert.Contains(logged, l => l.Piece == legs && l.Line.StartsWith($"{size}: not cut from ", StringComparison.Ordinal)
                                             && l.Line.EndsWith("; the nearest instead", StringComparison.Ordinal));
                var measured = Candidates(SourceOf(legs, size));
                Assert.NotEmpty(measured);
                Assert.All(measured.Values, c => Assert.Contains(", own body ", c));
            }
            // [EDGE-11] The legs' own Uranus, read as the same size as one before it that sits on it, is still left out.
            Assert.Contains(logged, l => l.Piece == legs && l.Line == "Uranus reads as the same size as another of the author's; not measured apart");

            // [AC-2] Read back, the Rue M top is a Rue Medium, as Rue S and Rue L are their own sizes.
            var bodies = SizeSetBatch.ScanBodies(LocalData.Path(LocalData.Mods));
            var mods = bodies.Select(b => (b.Dir, b.Catalog)).ToList();
            var rue = SupportedBodies.Find(SupportedBodies.Of("rue", "_top", false)!, bodies)!;
            foreach (var (option, size) in new[] { ("Rue S", "Small"), ("Rue M", "Medium"), ("Rue L", "Large") })
            {
                byte[] model = SizeSetRefitTests.ModelOfOption(copy, "Size", option, LuxeTop);
                var garment = ModelPartReader.Read(model)!;
                var bones = new HashSet<string>(SecondSkinWriter.Parse(model).BoneNames, StringComparer.Ordinal);
                var pick = RefitCore.DetectSource(garment, bones, "_top", "0201", mods, rue.Dir, _ => true, _ => { });
                output.WriteLine($"{option} reads as {pick?.Dir} / {pick?.Option.Label} ({pick?.Confidence})");
                Assert.NotNull(pick);
                Assert.True(AutoRefitDecisions.Proceed(pick.Confidence));
                Assert.Equal(rue.Dir, pick.Dir);
                Assert.EndsWith("- " + size, pick.Option.Label);
            }
        }
        finally
        {
            SizeSetBatchData.Delete(copy);
        }
    }

    [LocalDataFact(LocalData.Mods, Darling, LocalData.UvMaps)]
    [Trait("Plan", "AC-sizeset-skin-misfit-4")]
    public void Darling_legs_pass_over_Nymph() // [AC-sizeset-skin-misfit-4]
    {
        string copy = SizeSetBatchData.CopyOf(Darling);
        try
        {
            Run(copy, ["neolithe", "bibo"]);
            foreach (string size in new[] { "Neolithe S", "Neolithe M", "Bibo M" })
            {
                string line = SourceOf("Legs — e6039", size);
                Assert.DoesNotMatch($"^{Regex.Escape(size)} from Nymph ", line);
                Assert.EndsWith(PassedOver, Candidates(line)["Nymph"]);
            }
        }
        finally
        {
            SizeSetBatchData.Delete(copy);
        }
    }

    [LocalDataFact(LocalData.Mods, Darling, LocalData.GameData, LocalData.TbseUvMaps, SizeSetBatchData.Tbse)]
    [Trait("Plan", "AC-sizeset-skin-misfit-6")]
    public void Darling_mens_versions_pass_over_Nymph() // [AC-sizeset-skin-misfit-6]
    {
        string copy = SizeSetBatchData.CopyOf(Darling);
        try
        {
            Run(copy, [], men: true);
            // The legs' Nymph — a misread on the women's side — is measured there and passed over for the men's too.
            string line = SourceOf("Legs — e6039", "TBSE");
            Assert.DoesNotMatch("^TBSE from Nymph ", line);
            Assert.EndsWith(PassedOver, Candidates(line)["Nymph"]);
            Assert.All(Candidates(line).Values, c => Assert.Contains(", own body ", c));
            // So is the chest's: read as Lithe's bra-free Extra Small, 12.7 mm off it, it was the men's TBSE's source.
            string chest = SourceOf("Body — e6039", "TBSE");
            Assert.DoesNotMatch("^TBSE from Nymph ", chest);
            Assert.EndsWith(PassedOver, Candidates(chest)["Nymph"]);
        }
        finally
        {
            SizeSetBatchData.Delete(copy);
        }
    }

    [LocalDataFact(LocalData.Mods, CatsuitPack, LocalData.UvMaps)]
    [Trait("Plan", "AC-sizeset-skin-misfit-5")]
    [Trait("Plan", "EDGE-sizeset-skin-misfit-2")]
    public void A_capped_skin_garment_passes_over_none() // [AC-sizeset-skin-misfit-5] [EDGE-sizeset-skin-misfit-2]
    {
        string copy = Unpacked(CatsuitPack);
        try
        {
            Run(copy, ["neolithe", "rue"]);
            Assert.DoesNotContain(logged, l => l.Line.Contains("passed over", StringComparison.Ordinal));
            Assert.StartsWith("Neolithe M from Large (by body", SourceOf("Body — e0561", "Neolithe M"));
            Assert.StartsWith("Rue M from Large (by body", SourceOf("Body — e0561", "Rue M"));
        }
        finally
        {
            SizeSetBatchData.Delete(copy);
        }
    }
}
