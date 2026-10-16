using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Tests.SyntheticChest;

namespace Proteus.Tests;

/// <summary>
/// The refit and the second-skin body smoothing on <see cref="SyntheticChest"/> fixtures, hashed before the refit learned
/// to smooth nipples. Every fixture the refit-nipple-smooth tests build is here, so "the off switch reproduces the old
/// output" is a byte comparison against code that never had the feature.
/// </summary>
public class RefitNippleSmoothPinTests(ITestOutputHelper output)
{
    internal const float Nipple = 0.004f;

    /// <summary>A refit of a chest garment, by fixture name: the fixtures RefitNippleSmoothTests runs both ways.</summary>
    internal static BodyRetarget.Planned Refit(string fixture, Action<string>? log = null)
    {
        byte[] flat = Body(0f, 0f), nippled = Body(Nipple, Nipple);
        BodyRetarget.Planned Run(byte[] garment, BodyRetarget.SlotPair pair, string slot = "_top",
                                 bool replaceSkin = true, IReadOnlySet<int>? held = null)
            => BodyRetarget.Plan(ModelPartReader.Read(garment)!, garment, [pair], slot, held: held,
                                 replaceSkin: replaceSkin, acrossBodies: true, cutHidden: false, log: log);
        return fixture switch
        {
            "flat to nippled" => Run(Garment(0f, 0f, BothCups), Pair(flat, nippled)),
            "flat to nippled, no swap" => Run(Garment(0f, 0f, BothCups), Pair(flat, nippled), replaceSkin: false),
            "equal nipples" => Run(Garment(Nipple, Nipple, BothCups), Pair(nippled, nippled)),
            "prouder source" => Run(Garment(0.006f, 0.006f, BothCups), Pair(Body(0.006f, 0.006f), nippled)),
            "partial excess" => Run(Garment(0.002f, 0.002f, BothCups), Pair(Body(0.002f, 0.002f), nippled)),
            "right cup only" => Run(Garment(0f, 0f, RightCup), Pair(flat, nippled)),
            "cloth behind" => Run(Garment(0f, 0f, BothCups, behind: true), Pair(flat, nippled)),
            "no bust bones" => Run(Garment(0f, 0f, BothCups), Pair(Body(0f, 0f, bust: false), Body(Nipple, Nipple, bust: false))),
            "legs slot" => Run(Garment(0f, 0f, BothCups), Pair(flat, nippled, "_dwn"), "_dwn"),
            "piercing" => Run(Garment(0f, 0f, BothCups), Pair(Body(0f, 0f, piercing: true), Body(Nipple, Nipple, piercing: true))),
            "variant cloth" => Run(Garment(0f, 0f, BothCups, clothAttr: "atr_tv_a"), Pair(flat, nippled)),
            "skin copy" => Run(Garment(0f, 0f, (0f, 0f, 0f, 0f), skinCopyMaterial: "/mt_c0201e9999_top_skin.mtrl",
                                       skinCopyOff: 0.0006f), Pair(flat, nippled)),
            "held cloth" => Run(Garment(0f, 0f, BothCups), Pair(flat, nippled), held: AllCloth(Garment(0f, 0f, BothCups))),
            "coarse source" => Run(Garment(0f, 0f, BothCups), Pair(Body(0f, 0f, step: 2), nippled)),
            _ => throw new ArgumentException(fixture),
        };
    }

    /// <summary>Every vertex of a garment's cloth mesh (mesh 1, after the skin).</summary>
    internal static HashSet<int> AllCloth(byte[] garment)
    {
        var parts = ModelPartReader.Read(garment)!;
        var span = parts.MeshSpans[1];
        var all = new HashSet<int>();
        for (int v = 0; v < span.Count; v++) all.Add(span.BaseVertex + v);
        return all;
    }

    internal static string Hash(byte[] model) => Convert.ToHexString(SHA256.HashData(model))[..16];

    /// <summary>Every fixture's output before the feature, by name.</summary>
    internal static readonly Dictionary<string, string> Hashes = new()
    {
        ["flat to nippled"] = "E129E7733A3454CE",
        ["flat to nippled, no swap"] = "2CC7CF11893446B7",
        ["equal nipples"] = "792417E74769D414",
        ["prouder source"] = "4D48227F8E808B0F",
        ["partial excess"] = "22026018D28BCE34",
        ["right cup only"] = "C63EA30C63FB25AD",
        ["cloth behind"] = "04F3528E293E16AE",
        ["no bust bones"] = "C514795710318539",
        ["legs slot"] = "E129E7733A3454CE",
        ["piercing"] = "E129E7733A3454CE",
        ["variant cloth"] = "15038A138508E0D1",
        ["skin copy"] = "1FF3E82CEE87F73F",
        ["held cloth"] = "844511733FED0C5A",
        ["coarse source"] = "A7379D94EF56DB33",
    };

    /// <summary>
    /// The fixtures refitted here with the smoothing off. The other six — "equal nipples", "prouder source", "cloth
    /// behind", "no bust bones", "legs slot", "skin copy" — are ones the smoothing leaves alone, and RefitNippleSmoothTests
    /// already holds each, smoothing on, to its hash above: off can only skip a pass that changed nothing there.
    /// </summary>
    public static TheoryData<string, string> Pinned
    {
        get
        {
            var rows = new TheoryData<string, string>();
            foreach (string fixture in new[] { "flat to nippled", "flat to nippled, no swap", "partial excess", "right cup only",
                                               "piercing", "variant cloth", "held cloth", "coarse source" })
                rows.Add(fixture, Hashes[fixture]);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Pinned))]
    [Trait("Plan", "AC-refit-nipple-smooth-9")]
    public void Refit_matches_the_pre_feature_output(string fixture, string pinned)
    {
        var plan = BodyRetarget.WithTuning(new BodyRetarget.Tuning(NoNippleSmooth: true), () => Refit(fixture));
        output.WriteLine($"{{ \"{fixture}\", \"{Hash(plan.Model)}\" }},");
        Assert.Equal(pinned, Hash(plan.Model));
    }

    /// <summary>The second-skin path's smoothing of the nippled chest under a mask covering its right half.</summary>
    internal static byte[]? SecondSkinSmooth()
    {
        const int size = 64;
        var mask = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = size / 2; x < size; x++) mask[y * size + x] = 255;
        var layer = new SecondSkinLayer { MaterialName = "x", Coverage = mask, CoverageWidth = size, CoverageHeight = size };
        return BodyBridge.SmoothBodyNipples(Body(Nipple, Nipple), layer, 1f);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-nipple-smooth-27")]
    public void Second_skin_smoothing_matches_the_pre_feature_output()
    {
        var smoothed = SecondSkinSmooth();
        Assert.NotNull(smoothed);
        output.WriteLine(Hash(smoothed!));
        Assert.Equal("10166FBEED8DEBF4", Hash(smoothed!));
    }
}
