using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Whole cross-body refits — the swap, the weights and the push-out, with real body files — pinned byte for byte. The
/// skinless-target change switches every target surface of the refit on one gate; these pairs are on its far side (the
/// target has skin wherever the source has), so they must come out exactly as they did before it. Recorded 2026-10-20
/// on test cf0fa3d (build 1324), before any of that change was made.
/// </summary>
[Trait("Speed", "Slow")]
public class GoldenCrossBodyLocalTests(ITestOutputHelper output)
{
    private const string Laurel = LocalData.Mods + ":[Jaque] Laurel Attire [July 2025]";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Bottoms = "valentione emissary/bottom/chara/equipment/e6144/model/c0201e6144_dwn.mdl";
    private const string BiboLegs = "bottoms - small clothes/type a - medium/chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string MuseGenA = "Smallclothes Legs/Defined - Gen A/chara/equipment/e0000/model/c0201e0000_dwn.mdl";

    /// <summary>The model each refit wrote, by SHA-256, and how many cloth nodes its push moved.</summary>
    private static readonly Dictionary<string, (string Sha, int Pushed)> Recorded = new()
    {
        ["muse gen a / studio"] = ("D2928EDB51CCAD93191D377D94A4E47985AF647961390935523F474DA27D71CF", 3080),
        ["muse gen a / auto"] = ("72D2D55F5453593727A4A21A9383AD2068328EBD3EFA8DDCEECFA35A0AE40D6A", 3080),
        ["bibo+ / studio"] = ("C3E7ABBC64ACCCC936E6466397C7FCD26C46438D6B0EED779170877417E36200", 3266),
        ["bibo+ / auto"] = ("66FC03EBC889E2DAA8AB8672C9830A00ABA9380748EDD471F127F3BBC832E0B3", 3266),
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var target in new[] { "muse gen a", "bibo+" })
            foreach (var path in new[] { "studio", "auto" })
                yield return [target, path];
    }

    [LocalDataTheory(Laurel, Neolithe, Bibo, LocalData.MusePack)]
    [MemberData(nameof(Cases))]
    [Trait("Plan", "AC-skinless-target-5")]
    [Trait("Plan", "EDGE-seam-weights-15")]
    public void A_cross_body_refit_onto_a_body_with_skin_is_as_it_was(string target, string path)
    {
        var (sha, pushed, model) = Refit(target, path);
        output.WriteLine($"[\"{target} / {path}\"] = (\"{sha}\", {pushed}),");
        var (wantSha, wantPushed) = Recorded[$"{target} / {path}"];
        Assert.Equal(wantSha, sha);
        Assert.Equal(wantPushed, pushed);
        // [EDGE-seam-weights-15] With 1382's seam weights: the same refit, weights apart.
        SeamWeightsTests.SameGeometry(model, Refit(target, path, seams: true).Model);
    }

    private static (string Sha, int Pushed, byte[] Model) Refit(string target, string path, bool seams = false)
    {
        var bodies = new[] { Neolithe }
            .Select(b => (Dir: Path.GetFileName(LocalData.Path(b)), Catalog: BodySizeCatalog.Read(LocalData.Path(b)))).ToList();
        var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Laurel), Bottoms));
        var garment = ModelPartReader.Read(bytes)!;
        var from = RefitCore.DetectSource(garment, new HashSet<string>(SecondSkinWriter.Parse(bytes).BoneNames, StringComparer.Ordinal),
                                          "_dwn", "0201", bodies, null, _ => true)!;
        string source = bodies[0].Catalog.PathOf(from.Option);

        string temp = Path.Combine(Path.GetTempPath(), "proteus-golden-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string targetPath;
            if (target == "bibo+")
                targetPath = BodySizeCatalog.ResolveCaseInsensitive(LocalData.Path(Bibo), BiboLegs);
            else
            {
                targetPath = Path.Combine(temp, "c0201e0000_dwn.mdl");
                File.WriteAllBytes(targetPath, LocalData.PackEntry(LocalData.MusePack, MuseGenA));
            }
            Assert.Null(BodyRetarget.BuildPair("_dwn", source, targetPath, "_dwn", false, null, null, null, out var pair));

            // As it was recorded: a seam's split vertices weighted each on its own, as builds before 1382 did. Measured on
            // these refits at 1382: 136-137 vertices on 55-59 seams took one weighting, the most any moved 0.150, positions
            // untouched (the seam rule writes weights alone); judged in SeamWeightsTests.
            var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoSeamWeights: !seams), () => path == "studio"
                ? BodyRetarget.Plan(garment, bytes, [pair], "_dwn", held: new HashSet<int>(), replaceSkin: true,
                                    acrossBodies: true, cutHidden: true)
                : RefitCore.PlanRefit(garment, bytes, [pair], "_dwn", across: true, gameGear: false, out _));
            return (Convert.ToHexString(SHA256.HashData(plan.Model)), plan.Report.Pushed, plan.Model);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
