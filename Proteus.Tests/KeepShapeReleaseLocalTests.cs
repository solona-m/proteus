using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Keep shape's release (<see cref="BodyRetarget.ReleaseSink"/>) against the mods it was measured on: "Ash by Succubi"
/// (e0460), whose left cup cover is a closed 11.3 cm piece of cloth that sank into the breast moved whole, and "pop"
/// (e0367), whose arm bands, underwires and chain links are the metal Keep shape is for. Skipped where they are not
/// installed. Each refit is the one <c>Proteus.Refit --keep-default</c> runs, with the Studio's default ticks.
/// </summary>
public class KeepShapeReleaseLocalTests
{
    private const string AshPack = LocalData.ModPacks + ":Ash by Succubi RueYab ML WC (Model Only).pmp";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Yab = LocalData.Mods + ":hs-Yet Another Body+-4.2.0-tmi";
    private const string Uranus = LocalData.Mods + ":hs-Uranus Redux-4.4.0-VKT";
    private const string Neolithe = LocalData.Mods + ":Neolithe [ALL IN ONE]";
    private const string Bibo = LocalData.Mods + ":Bibo+";
    private const string Pop = LocalData.Mods + ":pop_v1.2/p1/pop/top & accessories/chara/equipment/e0367/model/c0201e0367_top.mdl";

    private const string YiggleLarge = @"files\chest - smallclothes\yiggle - large\chara\equipment\e0000\model\c0201e0000_top.mdl";
    private const string YabMedium = @"files\chest - smallclothes\medium\chara\equipment\e0000\model\c0201e0000_top.mdl";
    private const string UranusRue = @"files\common\7\c0201e0000_top.mdl";

    private const string Cover = "2.1.7";
    private const string Choker = "2.1.3";

    /// <summary>
    /// The written models of these refits on <c>test</c> before this rule (build 1229, 7e76b72), SHA-256: with release
    /// off, the refit must still write exactly these — the stage the release re-runs was pulled out of the solve, and
    /// nothing else on the Keep shape path may have moved.
    /// </summary>
    private static readonly Dictionary<string, string> Before = new()
    {
        ["ash Rue L > Uranus"] = "8C21BA073FF3E10B5DCCC3A0D20BA411F12115546E08A61F0AE2AAAD66236334",
        ["ash Yab L > Uranus"] = "3B4769B40112952A9A094AA1BE9A6CD94579F120B4CDB91CE5475AF5B179045C",
        ["pop > Uranus"] = "DD799F3CC28B721C315420C1510FF459C0A2E7E1A67CA67C8C1F98825E3747B2",
        ["pop > Neolithe XS"] = "96A5D5E72F62C2ECECBC75CF13D55DEC11A3369E202AB33956B282036D0C1345",
        ["pop > Bibo S"] = "4860C45C6F7ADF88F0F85EB51DBDA8F5490C9F8E0EA19FEB8563AF61171E5B84",
    };

    private sealed record Run(BodyRetarget.Planned Plan, List<string> Names)
    {
        public BodyRetarget.ShapeOutcome Of(string piece) => Plan.Report.Shapes![Names.IndexOf(piece)];
        public string Hash => Convert.ToHexString(SHA256.HashData(Plan.Model));
    }

    /// <summary>One refit as <c>Proteus.Refit --keep-default</c> runs it, the default ticks less <paramref name="untick"/>.</summary>
    private static Run Refit(byte[] bytes, string fromNeed, string fromRel, string toNeed, string toRel,
                             bool noRelease = false, string? untick = null, bool noNippleSmooth = false)
    {
        var garment = ModelPartReader.Read(bytes)!;
        string fromRoot = LocalData.Path(fromNeed), toRoot = LocalData.Path(toNeed);
        static ushort? Mask(string root) => ImcEntrySource.MaskFor(root, 0, BodyRetarget.ImcSlotName("_top")!, null);
        Assert.Null(BodyRetarget.BuildPair("_top", BodySizeCatalog.ResolveCaseInsensitive(fromRoot, fromRel),
                                           BodySizeCatalog.ResolveCaseInsensitive(toRoot, toRel), "_top", false,
                                           Mask(fromRoot), Mask(toRoot), null, out var pair));

        var labels = RefitCore.DefaultKeepShape(garment);
        if (untick != null) Assert.True(labels.Remove(untick), $"{untick} is not ticked by default");
        var pieces = RefitCore.ShapePieces(garment, labels, out var names);
        bool across = !string.Equals(fromRoot, toRoot, StringComparison.OrdinalIgnoreCase);
        var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoRelease: noRelease, NoNippleSmooth: noNippleSmooth), () => BodyRetarget.Plan(
            garment, bytes, [pair], "_top", replaceSkin: true, acrossBodies: across, keepShape: pieces));
        return new Run(plan, names);
    }

    private static byte[] Ash(string size) => LocalData.PackEntry(AshPack, $"default group/Top Sizes/{size}.mdl");

    [LocalDataFact(AshPack, Rue, Uranus)]
    [Trait("Plan", "AC-keep-shape-release-1")]
    [Trait("Plan", "AC-keep-shape-release-9")]
    public void Ash_cover_bends_with_Uranus()
    {
        var bytes = Ash("Rue L");

        // The harness reproduces the report first: kept whole, the cover sinks into the bigger breast — and the refit
        // with release off is byte for byte the one this rule replaced.
        var whole = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, noRelease: true);
        var wholeCover = whole.Of(Cover);
        Assert.True(wholeCover.Sink > 0.005f, $"moved whole the cover should sink past 5 mm, not {wholeCover.Sink * 1000:F2}");
        Assert.True(wholeCover.Share * 893 > 100, $"only {wholeCover.Share * 893:F0} of the cover's points sank");
        Assert.Equal(Before["ash Rue L > Uranus"], whole.Hash);

        var released = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue);
        var unticked = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, untick: Cover);

        Assert.True(released.Of(Cover).Released);
        // Let go, it comes out exactly as if the user had unticked it.
        Assert.Equal(unticked.Hash, released.Hash);
        Assert.Equal(unticked.Plan.Report.Folded, released.Plan.Report.Folded);
    }

    [LocalDataFact(AshPack, Rue, Uranus)]
    [Trait("Plan", "AC-keep-shape-release-2")]
    public void Ash_choker_stays_kept()
    {
        var run = Refit(Ash("Rue L"), Rue, YiggleLarge, Uranus, UranusRue);

        var choker = run.Of(Choker);
        Assert.True(choker.Judged);
        Assert.False(choker.Released, $"the choker sinks {choker.Sink * 1000:F2} mm");
        // Only the cover is let go.
        Assert.Equal([Cover], RefitCore.KeptPieces(run.Plan.Report, run.Names).Where(p => p.Released).Select(p => p.Name));
    }

    [LocalDataFact(AshPack, Yab, Uranus)]
    [Trait("Plan", "AC-keep-shape-release-2")]
    [Trait("Plan", "AC-keep-shape-release-9")]
    public void Ash_other_source_keeps_its_choker()
    {
        var bytes = Ash("Yab L");

        Assert.Equal(Before["ash Yab L > Uranus"], Refit(bytes, Yab, YiggleLarge, Uranus, UranusRue, noRelease: true).Hash);

        var run = Refit(bytes, Yab, YiggleLarge, Uranus, UranusRue);
        Assert.Equal([Cover], RefitCore.KeptPieces(run.Plan.Report, run.Names).Where(p => p.Released).Select(p => p.Name));
        Assert.Equal(Refit(bytes, Yab, YiggleLarge, Uranus, UranusRue, untick: Cover).Hash, run.Hash);
    }

    [LocalDataFact(AshPack, Rue, Uranus)]
    [Trait("Plan", "AC-keep-shape-release-3")]
    public void Ash_no_longer_carries_the_left_nipple_piercing()
    {
        // The sunk cover left the nipple reading bare, so the new body's nipple piercing and its chain came in with the
        // skin. Let go, the cover covers it again, as unticked.
        var bytes = Ash("Rue L");
        var whole = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, noRelease: true);
        var released = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue);
        var unticked = Refit(bytes, Rue, YiggleLarge, Uranus, UranusRue, untick: Cover);

        int Adorned(Run r) => r.Plan.Report.Swap?.Adorned ?? 0;
        Assert.Equal(Adorned(unticked), Adorned(released));
        Assert.True(Adorned(released) < Adorned(whole),
                    $"released {Adorned(released)} adornment triangles carried, kept whole {Adorned(whole)}");
    }

    private const string LaVieEnRose =
        LocalData.Mods + ":La Vie en Rose - by Solona/size/large/chara/equipment/e6098/model/c0201e6098_top.mdl";
    private const string BiboPerkyLarge = @"breasts - small clothes\perky - large\chara\equipment\e0000\model\c0201e0000_top.mdl";

    [LocalDataFact(LaVieEnRose, Bibo, Rue)]
    [Trait("Plan", "AC-keep-shape-release-4")]
    public void Small_ornaments_stay_whole_however_deep_they_sink()
    {
        // La Vie en Rose's little roses and beads, onto Rue+ Yiggle Large: moved whole some sink far past ReleaseSink —
        // 16.5 mm for a 1.7 cm rose when measured — but under ReleaseMinSize every one stays whole.
        var bytes = File.ReadAllBytes(LocalData.Path(LaVieEnRose));
        var garment = ModelPartReader.Read(bytes)!;
        var run = Refit(bytes, Bibo, BiboPerkyLarge, Rue, YiggleLarge);

        float SizeOf(string label)
        {
            var part = garment.Parts.First(p => p.Label == label);
            var d = part.Max - part.Min;
            return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
        }
        var small = RefitCore.KeptPieces(run.Plan.Report, run.Names)
                             .Where(p => SizeOf(p.Name) < BodyRetarget.ReleaseMinSize).ToList();

        Assert.Contains(small, p => p.Sink > BodyRetarget.ReleaseSink);   // or this proves nothing
        Assert.DoesNotContain(small, p => p.Released);
    }

    public static TheoryData<string> PopTargets => new() { "pop > Uranus", "pop > Neolithe XS", "pop > Bibo S" };

    [LocalDataTheory(Pop, Yab, Uranus, Neolithe, Bibo)]
    [MemberData(nameof(PopTargets))]
    [Trait("Plan", "AC-keep-shape-release-2")]
    [Trait("Plan", "AC-keep-shape-release-9")]
    public void Pop_hard_pieces_stay_kept(string which)
    {
        var (toNeed, toRel) = which switch
        {
            "pop > Uranus" => (Uranus, UranusRue),
            "pop > Neolithe XS" => (Neolithe, @"extra chest - smallclothes\nsfw almond xs.mdl"),
            _ => (Bibo, @"breasts - small clothes\nude - small\chara\equipment\e0000\model\c0201e0000_top.mdl"),
        };

        // Without the nipple smoothing, which smooths YAB+ Medium's excess off Uranus and Bibo+ Nude: the hashes are
        // this rule's, taken before it — the smoothing came later and has its own (RefitNippleSmoothTests).
        var run = Refit(File.ReadAllBytes(LocalData.Path(Pop)), Yab, YabMedium, toNeed, toRel, noNippleSmooth: true);

        // Arm bands, underwires and 173 chain links: every one judged stays whole, and the refit is the one before.
        var kept = RefitCore.KeptPieces(run.Plan.Report, run.Names);
        Assert.NotEmpty(kept);
        Assert.DoesNotContain(kept, p => p.Released);
        Assert.Equal(Before[which], run.Hash);
    }
}
