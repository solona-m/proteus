using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// "Keep the new body's pubes": a refit brings the new body's pube meshes (any non-skin material named "pube") in with its
/// skin only when asked, on skin it draws and no cloth covers.
/// </summary>
public partial class BodyRetargetTests
{
    private const string BiboPube = "/mt_c0201b0001_bibopube.mtrl";

    /// <summary>
    /// A legs garment with its own skin, its own body-family pubes and ring, and a cloth panel off to the side; and a legs
    /// body with skin, pubes, Tre nails and a ring. Everything rigged to one bone so the across-bodies weights apply.
    /// </summary>
    private static (byte[] Garment, byte[] Body) PinnedLegs()
    {
        (string, float)[] kosi = [("j_kosi", 1f)];
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5, Weights: kosi)),
            new SyntheticModel.Mesh(BiboPube, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetZ: 0.001f,
                                                                     Weights: kosi)),
            new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 0.2f,
                                                                      OffsetZ: 0.001f, Weights: kosi)),
            new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetY: 50f,
                                                                          Weights: kosi)));
        var body = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5,
                                                                         Weights: [("j_kosi", 0.8f), ("iv_omanko", 0.2f)])),
            new SyntheticModel.Mesh(BiboPube, new SyntheticModel.Sub(0, OffsetZ: 0.001f, Weights: kosi)),
            new SyntheticModel.Mesh(TreNails, new SyntheticModel.Sub(0, OffsetX: 0.4f, OffsetZ: 0.001f, Weights: kosi)),
            new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0, OffsetX: 0.2f, OffsetZ: 0.001f, Weights: kosi)));
        return (garment, body);
    }

    /// <summary>The legs slot onto <paramref name="body"/>, from a one-bone body of the same shape (so weights are planned).</summary>
    private static BodyRetarget.SlotPair LegsPair(byte[] body)
    {
        var source = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5, Weights: [("j_kosi", 1f)])));
        return Resized("_dwn", body) with { SourceModel = source };
    }

    private static string Hash(byte[]? model) => Convert.ToHexString(SHA256.HashData(model ?? []));

    /// <summary>
    /// The outputs with the box off, hashed. The constants were captured on the code before the box existed (test at
    /// f83a0d75), so a change to what an unticked refit writes fails here.
    /// </summary>
    private static Dictionary<string, string> PinnedOffHashes(bool? carryPubes)
    {
        var (garment, body) = PinnedLegs();
        var parts = ModelPartReader.Read(garment)!;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (bool piercings in new[] { false, true })
        {
            string p = piercings ? "pierce" : "plain";
            result[$"swap/{p}"] = Hash(carryPubes is { } c
                ? BodyRetarget.SwapSkin(garment, [LegsPair(body)], out _, carryPiercings: piercings, carryPubes: c)
                : BodyRetarget.SwapSkin(garment, [LegsPair(body)], out _, carryPiercings: piercings));
            foreach (bool across in new[] { false, true })
            {
                string a = across ? "across" : "within";
                result[$"plan/{a}/{p}"] = Hash(carryPubes is { } c2
                    ? BodyRetarget.Plan(parts, garment, [LegsPair(body)], "_dwn", pushOut: false, replaceSkin: true,
                                        acrossBodies: across, carryPiercings: piercings, carryPubes: c2).Model
                    : BodyRetarget.Plan(parts, garment, [LegsPair(body)], "_dwn", pushOut: false, replaceSkin: true,
                                        acrossBodies: across, carryPiercings: piercings).Model);
            }
        }
        return result;
    }

    private static readonly Dictionary<string, string> OffHashes = new(StringComparer.Ordinal)
    {
        ["swap/plain"] = "0ACD2DCBA41E5D4CF20C9761276991F9B8210A0881412951BC9C2FF970C8D114",
        ["plan/within/plain"] = "A46E5FD2EC88D259BA0B97D321DB2C6CCB06E808E8D64D592D061DE46173EC2C",
        ["plan/across/plain"] = "BDD7A43C99905741C318CCE97E85205E074B6FE60DA6A6EE23D6A46FAEB4B1D3",
        ["swap/pierce"] = "333B539DEB19383592C2F60EF1A4D068C9806A00CB498C290F4CD4B7A13CA110",
        ["plan/within/pierce"] = "6F866A7B6D22E88CC1DABED77D5894DB3E0F0F10FA6465BBC7F3CD1C84D8C3B9",
        ["plan/across/pierce"] = "33D8BB4063DC760143BEA00DEDCF915D33B322DAB4E09E7B477BEA0B4208B905",
    };

    private const string BetterPube = "/mt_c0201b0001_betterpube.mtrl";
    private const string PubePierce = "/mt_c0201b0001_pubepierce.mtrl";

    private static byte[] BareSkin(params SyntheticModel.Mesh[] more)
        => SyntheticModel.Build([], [new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)),
                                     .. more]);

    private static byte[] OpaqueOver(params SyntheticModel.Mesh[] more)
        => BareSkin([new SyntheticModel.Mesh(ClothMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 4, OffsetZ: 0.003f)),
                     .. more]);

    private static SyntheticModel.Mesh PubesOn(string material = BiboPube, int tris = 1, float x = 0f)
        => new(material, new SyntheticModel.Sub(0, TrianglesPerIsland: tris, OffsetX: x, OffsetZ: 0.001f));

    private static int DrawnOf(byte[]? mdl, string material) => mdl == null ? 0 : Drawn(mdl).GetValueOrDefault(material.TrimStart('/'));

    [Theory]
    [Trait("Plan", "AC-refit-keep-pubes-5")]
    [Trait("Plan", "EDGE-refit-keep-pubes-9")]
    [InlineData("/mt_c0201b0001_bibopube.mtrl", true)]
    [InlineData("/mt_c0201b0001_betterpube.mtrl", true)]
    [InlineData("/mt_c0201b0001_BiboPube.mtrl", true)]
    [InlineData("/mt_c0201b0001_pubes.mtrl", true)]
    [InlineData("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibopube.mtrl", true)]
    [InlineData("/mt_c0201b0001_bibo.mtrl", false)]                     // skin
    [InlineData("/mt_c0201b0001_pubic.mtrl", false)]
    [InlineData("/mt_c0201b0001_piercings.mtrl", false)]
    [InlineData("/mt_c0201b0001_neolithe_penis.mtrl", false)]
    [InlineData("chara/pubes_mod/mt_c0201e6255_top_a.mtrl", false)]     // a folder, not the name
    public void Only_materials_named_pube_are_pubes(string material, bool pube)
        // [AC-refit-keep-pubes-5] [EDGE-refit-keep-pubes-9]
        => Assert.Equal(pube, BodyRetarget.IsBodyPubeMaterial(material));

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-1")]
    [Trait("Plan", "AC-refit-keep-pubes-6")]
    public void The_body_s_pubes_stay_out_unless_asked_for()
    {
        // [AC-refit-keep-pubes-1] [AC-refit-keep-pubes-6] Bare legs: the body's pubes come in only when asked, through
        // the swap and through Plan, as the Studio, the size set and the automatic refit call it.
        var legs = BareSkin(PubesOn());
        var bare = BareSkin();

        var off = BodyRetarget.SwapSkin(bare, [Resized("_dwn", legs)], out var offReport);
        var on = BodyRetarget.SwapSkin(bare, [Resized("_dwn", legs)], out var onReport, carryPubes: true);
        Assert.Equal(0, offReport.Adorned);
        Assert.Equal(0, DrawnOf(off, BiboPube));
        Assert.Equal(1, onReport.Adorned);
        Assert.Equal(1, DrawnOf(on, BiboPube));

        var parts = ModelPartReader.Read(bare)!;
        var planOff = BodyRetarget.Plan(parts, bare, [Resized("_dwn", legs)], "_dwn", pushOut: false, replaceSkin: true);
        var planOn = BodyRetarget.Plan(parts, bare, [Resized("_dwn", legs)], "_dwn", pushOut: false, replaceSkin: true,
                                       carryPubes: true);
        Assert.Equal(0, DrawnOf(planOff.Model, BiboPube));
        Assert.Equal(1, DrawnOf(planOn.Model, BiboPube));
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-1")]
    public void Studio_plan_carries_pubes_when_asked()
    {
        // [AC-refit-keep-pubes-1] Through the Studio's own plan call.
        var legs = BareSkin(PubesOn());
        var bare = BareSkin();
        var parts = ModelPartReader.Read(bare)!;
        var rule = RetargetInputs.StudioSkin(replaceSkin: true, cutHidden: true, clearBody: false, gameGear: false, "_dwn",
                                             crossSex: false);
        byte[] Plan(bool pubes) => RetargetInputs.StudioPlan(parts, bare, [Resized("_dwn", legs)], "_dwn", null, layOnBody: true,
                                                             acrossBodies: true, rule, cut: true, pieces: null,
                                                             carryPiercings: false, liftUnderbust: true, carryPubes: pubes).Model;
        Assert.Equal(1, DrawnOf(Plan(true), BiboPube));
        Assert.Equal(0, DrawnOf(Plan(false), BiboPube));
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-5")]
    public void Pubes_and_piercings_are_independent()
    {
        // [AC-refit-keep-pubes-5] Nails always; the ring with its box; the pubes with theirs.
        var body = BareSkin(new SyntheticModel.Mesh(TreNails, new SyntheticModel.Sub(0, OffsetZ: 0.001f)),
                            new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0, OffsetX: 0.2f, OffsetZ: 0.001f)),
                            PubesOn(x: 0.4f));
        var bare = BareSkin();
        foreach (bool piercings in new[] { false, true })
        foreach (bool pubes in new[] { false, true })
        {
            var got = BodyRetarget.SwapSkin(bare, [Resized("_dwn", body)], out var report, carryPiercings: piercings,
                                            carryPubes: pubes);
            Assert.Equal(1, DrawnOf(got, TreNails));
            Assert.Equal(piercings ? 1 : 0, DrawnOf(got, Piercings));
            Assert.Equal(pubes ? 1 : 0, DrawnOf(got, BiboPube));
            Assert.Equal(1 + (piercings ? 1 : 0) + (pubes ? 1 : 0), report.Adorned);
        }
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-6")]
    [Trait("Plan", "EDGE-refit-keep-pubes-3")]
    public void Pubes_under_cloth_stay_out()
    {
        // [AC-refit-keep-pubes-6] [EDGE-refit-keep-pubes-3] Opaque cloth 3 mm over the skin: the pubes would stand through it.
        var got = BodyRetarget.SwapSkin(OpaqueOver(), [Resized("_dwn", BareSkin(PubesOn()))], out var report, carryPubes: true);
        Assert.Equal(0, report.Adorned);
        Assert.Equal(0, DrawnOf(got, BiboPube));
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-6")]
    [Trait("Plan", "EDGE-refit-keep-pubes-20")]
    public void The_garment_s_own_pubes_under_opaque_cloth_let_none_in()
    {
        // [AC-refit-keep-pubes-6] [EDGE-refit-keep-pubes-20] The author left the body's pubes under their underwear: that
        // says nothing about where pubes show, so the body's stay out, and the author's stay as they were.
        var garment = OpaqueOver(PubesOn(tris: 2));
        var got = BodyRetarget.SwapSkin(garment, [Resized("_dwn", BareSkin(PubesOn()))], out var report, carryPubes: true);
        Assert.Equal(0, report.Adorned);
        Assert.Equal(2, DrawnOf(got, BiboPube));
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-6")]
    [Trait("Plan", "EDGE-refit-keep-pubes-20")]
    public void The_garment_s_own_uncovered_pubes_give_way_to_the_body_s()
    {
        // [AC-refit-keep-pubes-6] [EDGE-refit-keep-pubes-20] Uncovered: the body's come in and the author's go — one, not two.
        var got = BodyRetarget.SwapSkin(BareSkin(PubesOn(tris: 2)), [Resized("_dwn", BareSkin(PubesOn()))], out var report,
                                        carryPubes: true);
        Assert.Equal(1, report.Adorned);
        Assert.Equal(1, DrawnOf(got, BiboPube));
        Assert.Single(SecondSkinWriter.MaterialNames(got!), BiboPube);
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-6")]
    [Trait("Plan", "EDGE-refit-keep-pubes-20")]
    public void The_author_s_nails_under_cloth_never_let_pubes_through()
    {
        // [AC-refit-keep-pubes-6] [EDGE-refit-keep-pubes-20] The author drew nails at the very spot, under opaque cloth: a
        // nail there would come in by the author's say-so, but a pube is judged on the cloth alone and stays out.
        var garment = OpaqueOver(new SyntheticModel.Mesh(TreNails, new SyntheticModel.Sub(0, OffsetZ: 0.001f)));
        var got = BodyRetarget.SwapSkin(garment, [Resized("_dwn", BareSkin(PubesOn()))], out _, carryPubes: true);
        Assert.Equal(0, DrawnOf(got, BiboPube));

        // The same spot with the body's nail instead: in, by the author's nail — the bypass this test proves pubes skip.
        var nailed = BodyRetarget.SwapSkin(garment, [Resized("_dwn", BareSkin(new SyntheticModel.Mesh(
            TreNails, new SyntheticModel.Sub(0, OffsetZ: 0.001f))))], out var nailReport, carryPubes: true);
        Assert.Equal(1, nailReport.Adorned);
        Assert.Equal(1, DrawnOf(nailed, TreNails));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-23")]
    [Trait("Plan", "EDGE-refit-keep-pubes-5")]
    public void Another_race_s_copy_of_the_same_pubes_gives_way()
    {
        // [EDGE-refit-keep-pubes-23] [EDGE-refit-keep-pubes-5] Within one body mod: the garment's Xaela-named, odd-cased
        // copy of the body's pubes is the same pubes, so it gives way and its name goes; its crotch mesh of a material the
        // body does not bring stays.
        const string XaelaPube = "/mt_c1401b0101_BiboPube.mtrl";
        var garment = BareSkin(PubesOn(XaelaPube, tris: 2), PubesOn(BetterPube, tris: 2, x: 0.4f));
        var got = BodyRetarget.SwapSkin(garment, [Resized("_dwn", BareSkin(PubesOn()))], out var report, carryPubes: true);
        Assert.Equal(1, report.Adorned);
        Assert.Equal(1, DrawnOf(got, BiboPube));
        Assert.Equal(0, DrawnOf(got, XaelaPube));
        Assert.DoesNotContain(XaelaPube, SecondSkinWriter.MaterialNames(got!));
        Assert.Equal(2, DrawnOf(got, BetterPube));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-1")]
    public void A_body_without_pubes_is_unchanged_by_the_box()
    {
        // [EDGE-refit-keep-pubes-1]
        var body = BareSkin(new SyntheticModel.Mesh(TreNails, new SyntheticModel.Sub(0, OffsetZ: 0.001f)));
        var off = BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out _);
        var on = BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out _, carryPubes: true);
        Assert.Equal(off, on);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-18")]
    public void A_garment_s_own_pubes_onto_a_body_without_any_are_untouched()
    {
        // [EDGE-refit-keep-pubes-18] Nothing came in to replace them: the garment's own (body family and its own family)
        // stay exactly as with the box off, within one body mod and across.
        var garment = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5, Weights: [("j_kosi", 1f)])),
            new SyntheticModel.Mesh(BiboPube, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetZ: 0.001f,
                                                                     Weights: [("j_kosi", 1f)])),
            new SyntheticModel.Mesh("/mt_c0201e6255_dwn_pube.mtrl", new SyntheticModel.Sub(0, OffsetX: 0.4f, OffsetZ: 0.001f,
                                                                                         Weights: [("j_kosi", 1f)])));
        var body = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5,
                                                                         Weights: [("j_kosi", 0.8f), ("iv_omanko", 0.2f)])),
            new SyntheticModel.Mesh(TreNails, new SyntheticModel.Sub(0, OffsetX: 0.2f, OffsetZ: 0.001f, Weights: [("j_kosi", 1f)])));
        var parts = ModelPartReader.Read(garment)!;

        Assert.Equal(BodyRetarget.SwapSkin(garment, [LegsPair(body)], out _),
                     BodyRetarget.SwapSkin(garment, [LegsPair(body)], out _, carryPubes: true));
        foreach (bool across in new[] { false, true })
        {
            byte[] Model(bool pubes) => BodyRetarget.Plan(parts, garment, [LegsPair(body)], "_dwn", pushOut: false,
                                                          replaceSkin: true, acrossBodies: across, carryPubes: pubes).Model;
            Assert.Equal(Model(false), Model(true));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-2")]
    public void A_hidden_variant_s_pubes_stay_out_with_the_option_on()
    {
        // [EDGE-refit-keep-pubes-2] Pubes on a variant the body mod does not draw stay out; shown, they come in.
        var body = SyntheticModel.Build(["atr_tv_b"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)),
            new SyntheticModel.Mesh(BiboPube, new SyntheticModel.Sub(0b1, OffsetZ: 0.001f)));
        var hiddenPair = Resized("_dwn", body) with { TargetHidden = new HashSet<string> { "atr_tv_b" } };

        BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out var shown, carryPubes: true);
        var hidden = BodyRetarget.SwapSkin(BareSkin(), [hiddenPair], out var hiddenReport, carryPubes: true);
        Assert.Equal(1, shown.Adorned);                                      // reaches the case
        Assert.Equal(0, hiddenReport.Adorned);
        Assert.Equal(0, DrawnOf(hidden, BiboPube));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-4")]
    public void Pubes_on_cut_skin_stay_out_and_come_in_when_the_skin_goes_in_whole()
    {
        // [EDGE-refit-keep-pubes-4] The author deleted the crotch (the body's second patch, 50 up): its pubes go with it,
        // unless the skin goes in whole.
        var body = SyntheticModel.Build([],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5),
                                                  new SyntheticModel.Sub(0, TrianglesPerIsland: 3, OffsetY: 50f)),
            new SyntheticModel.Mesh(BiboPube, new SyntheticModel.Sub(0, OffsetY: 50f, OffsetZ: 0.001f)));

        var cut = BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out var cutReport, carryPubes: true);
        var whole = BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out var wholeReport, cutHidden: false,
                                          carryPubes: true);
        Assert.Equal(0, cutReport.Adorned);
        Assert.Equal(0, DrawnOf(cut, BiboPube));
        Assert.Equal(1, wholeReport.Adorned);
        Assert.Equal(1, DrawnOf(whole, BiboPube));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-7")]
    public void Without_room_nothing_is_swapped_and_off_still_fits_nails()
    {
        // [EDGE-refit-keep-pubes-7] Room for one more material after the body's skin: nails and pubes would take two.
        var meshes = new List<SyntheticModel.Mesh>
        {
            new(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)),
            new(TreNails, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetZ: 0.001f)),
            new(BiboPube, new SyntheticModel.Sub(0, TrianglesPerIsland: 2, OffsetX: 0.4f, OffsetZ: 0.001f)),
        };
        for (int i = 0; i < 5; i++)
            meshes.Add(new($"/mt_c0201e6255_dwn_{(char)('a' + i)}.mtrl",
                           new SyntheticModel.Sub(0, OffsetY: 50f + i, OffsetZ: 0.001f)));
        var garment = SyntheticModel.Build([], [.. meshes]);
        var body = BareSkin(new SyntheticModel.Mesh(TreNails, new SyntheticModel.Sub(0, OffsetZ: 0.001f)), PubesOn(x: 0.4f));

        var on = BodyRetarget.SwapSkin(garment, [Resized("_dwn", body)], out var onReport, carryPubes: true);
        var off = BodyRetarget.SwapSkin(garment, [Resized("_dwn", body)], out var offReport);

        Assert.Equal(0, onReport.Adorned);
        Assert.Equal(2, DrawnOf(on, TreNails));                                // the garment's own, kept
        Assert.Equal(2, DrawnOf(on, BiboPube));
        Assert.Equal(1, offReport.Adorned);
        Assert.Equal(1, DrawnOf(off, TreNails));                               // the body's
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-19")]
    public void Two_pube_materials_take_two_slots()
    {
        // [EDGE-refit-keep-pubes-19] Rue+ draws its crotch mesh and its hair in two materials: both, or (no room) neither.
        var body = BareSkin(PubesOn(BetterPube), PubesOn(BiboPube, x: 0.4f));
        var roomy = BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out var roomyReport, carryPubes: true);
        Assert.Equal(2, roomyReport.Adorned);
        Assert.Equal(1, DrawnOf(roomy, BetterPube));
        Assert.Equal(1, DrawnOf(roomy, BiboPube));
        var table = SecondSkinWriter.MaterialNames(roomy!);
        Assert.Single(table, BetterPube);
        Assert.Single(table, BiboPube);

        var meshes = new List<SyntheticModel.Mesh> { new(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)) };
        for (int i = 0; i < 7; i++)
            meshes.Add(new($"/mt_c0201e6255_dwn_{(char)('a' + i)}.mtrl",
                           new SyntheticModel.Sub(0, OffsetY: 50f + i, OffsetZ: 0.001f)));
        var crowded = SyntheticModel.Build([], [.. meshes]);
        BodyRetarget.SwapSkin(crowded, [Resized("_dwn", body)], out var crowdedReport, carryPubes: true);
        Assert.Equal(0, crowdedReport.Adorned);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-8")]
    public void A_piercing_pube_comes_in_with_either_box()
    {
        // [EDGE-refit-keep-pubes-8] Named both: either box brings it. Under cloth beside the author's own piercing, the
        // piercings box's rule lets it in; the pubes box alone judges it as a pube.
        var body = BareSkin(PubesOn(PubePierce));
        int With(byte[] garment, bool piercings, bool pubes)
            => DrawnOf(BodyRetarget.SwapSkin(garment, [Resized("_dwn", body)], out _, carryPiercings: piercings,
                                             carryPubes: pubes), PubePierce);

        Assert.Equal(1, With(BareSkin(), piercings: true, pubes: false));
        Assert.Equal(1, With(BareSkin(), piercings: false, pubes: true));
        Assert.Equal(0, With(BareSkin(), piercings: false, pubes: false));

        var shownUnderCloth = OpaqueOver(new SyntheticModel.Mesh(Piercings, new SyntheticModel.Sub(0, OffsetZ: 0.001f)));
        Assert.Equal(0, With(shownUnderCloth, piercings: false, pubes: true));
        Assert.Equal(1, With(shownUnderCloth, piercings: true, pubes: true));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-11")]
    public void A_hanging_pube_island_follows_what_it_touches()
    {
        // [EDGE-refit-keep-pubes-11] Links off the skin come in with the island they touch; one touching nothing stays out.
        var chain = new[] { 0.001f, 0.005f, 0.009f, 0.013f, 0.017f, 0.021f, 0.025f }
            .Select(z => new SyntheticModel.Sub(0, OffsetZ: z))
            .Append(new SyntheticModel.Sub(0, OffsetX: 3f, OffsetZ: 0.025f))
            .ToArray();
        var body = BareSkin(new SyntheticModel.Mesh(BiboPube, chain));
        var got = BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out var report, carryPubes: true);
        Assert.Equal(7, report.Adorned);
        Assert.Equal(7, DrawnOf(got, BiboPube));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-16")]
    public void Carried_pubes_are_the_body_s_own_triangles()
    {
        // [EDGE-refit-keep-pubes-16] Where the body put them, untouched, and nothing degenerate written.
        var body = BareSkin(PubesOn(tris: 3));
        var got = BodyRetarget.SwapSkin(BareSkin(), [Resized("_dwn", body)], out _, carryPubes: true)!;

        static List<string> Corners(byte[] mdl)
        {
            var parts = ModelPartReader.Read(mdl)!;
            return parts.Parts.Where(p => p.Island < 0 && p.Material == BiboPube)
                        .SelectMany(p => p.Triangles)
                        .Select(v => At(parts, v))
                        .Select(p => $"{p.X:F5},{p.Y:F5},{p.Z:F5}").Order().ToList();
        }
        Assert.Equal(Corners(body), Corners(got));
        var read = ModelPartReader.Read(got)!;
        foreach (var part in read.Parts.Where(p => p.Island < 0))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                var a = At(read, part.Triangles[t]);
                var b = At(read, part.Triangles[t + 1]);
                var c = At(read, part.Triangles[t + 2]);
                Assert.True(System.Numerics.Vector3.Cross(b - a, c - a).Length() > 1e-9f, $"degenerate in {part.Material}");
            }
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-21")]
    public void Within_one_body_a_pube_mesh_off_the_claimed_slot_stays()
    {
        // [EDGE-refit-keep-pubes-21] The garment's own pubes 3 m off the legs: not the slot's, so kept beside the body's,
        // as the author's nails are.
        var garment = BareSkin(new SyntheticModel.Mesh(BiboPube, new SyntheticModel.Sub(0, TrianglesPerIsland: 2,
                                                                                         OffsetX: 3f, OffsetZ: 0.001f)));
        var got = BodyRetarget.SwapSkin(garment, [Resized("_dwn", BareSkin(PubesOn()))], out var report, carryPubes: true);
        Assert.Equal(1, report.Adorned);
        Assert.Equal(3, DrawnOf(got, BiboPube));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-23")]
    public void Only_garment_pubes_of_a_carried_material_are_dropped()
    {
        // [EDGE-refit-keep-pubes-23] Rue+ onto a Rue+ size whose hair is switched off: its crotch mesh comes in and takes
        // the garment's place; the garment's own hair stays as it was.
        var garment = BareSkin(PubesOn(BetterPube, tris: 2), PubesOn(BiboPube, tris: 2, x: 0.4f));
        var body = SyntheticModel.Build(["atr_tv_b"],
            new SyntheticModel.Mesh(SkinMaterial, new SyntheticModel.Sub(0, TrianglesPerIsland: 5)),
            new SyntheticModel.Mesh(BetterPube, new SyntheticModel.Sub(0, OffsetZ: 0.001f)),
            new SyntheticModel.Mesh(BiboPube, new SyntheticModel.Sub(0b1, OffsetX: 0.4f, OffsetZ: 0.001f)));
        var pair = Resized("_dwn", body) with { TargetHidden = new HashSet<string> { "atr_tv_b" } };

        var on = BodyRetarget.SwapSkin(garment, [pair], out var report, carryPubes: true);
        var off = BodyRetarget.SwapSkin(garment, [pair], out _);
        Assert.Equal(1, report.Adorned);
        Assert.Equal(1, DrawnOf(on, BetterPube));                              // the body's, in place of the garment's
        Assert.Equal(2, DrawnOf(on, BiboPube));                                // the garment's own, kept
        Assert.Equal(DrawnOf(off, BiboPube), DrawnOf(on, BiboPube));
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-7")]
    public void Off_is_byte_identical_to_today()
    {
        // [AC-refit-keep-pubes-7] Pinned on the unchanged code: the old call and carryPubes: false both match.
        var wrong = new List<string>();
        foreach (bool? carry in new bool?[] { null, false })
        {
            var got = PinnedOffHashes(carry);
            foreach (var (key, want) in OffHashes)
                if (want != got[key]) wrong.Add($"{key} ({(carry == null ? "no argument" : "false")}): {got[key]}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
