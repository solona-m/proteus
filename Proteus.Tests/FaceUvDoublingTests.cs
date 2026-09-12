using System.Collections.Generic;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which face art is rendered by rewriting the face's own UVs (<see cref="FaceUvDoublingService"/>) rather
/// than by cutting a second-skin shell for it.
/// <para/>
/// The in-place path wins on every axis for art that belongs on SKIN — it keeps the face's own material and
/// so the wearer's tone, adds no geometry, and leaves the shape keys that drive every blink and viseme
/// alone, all of which a shell loses. So the question this answers is not "is it worth it" but "does this
/// art need <c>character.shpk</c>", because that is the only thing the skin layer cannot give it.
/// </summary>
public class FaceUvDoublingTests
{
    private const string FaceMtrl = "chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl";
    private const string IrisMtrl = "chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_iri_a.mtrl";
    private const string BodyMtrl = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";

    private static OverlayEntry Entry(ProteusMetadata? md = null)
        => new("asymface", "Asym Face", 0, true, md ?? new ProteusMetadata(), "/tmp/asymface/Proteus");

    private static ResolvedOverlay Art(string material = FaceMtrl, bool asymmetric = true,
                                       string? space = UVRemapService.FaceSplitSpace,
                                       OverlayLayer layer = OverlayLayer.Skin,
                                       List<ColorTableRowPreset>? rows = null,
                                       string? scroll = null)
        => new(new OverlayDescriptor
        {
            Layer = layer,
            AsymmetricArt = asymmetric ? true : null,
            SourceBodyType = space,
            Scroll = scroll,
            MaterialGamePaths = [material],
            Diffuse = "overlays/diffuse.png",
        }, rows, null, null);

    /// <summary>The case the feature exists for: a whole face skin, painted on a doubled sheet.</summary>
    [Fact]
    public void Asymmetric_face_art_on_the_skin_layer_is_handled_in_place()
        => Assert.True(FaceUvDoublingService.IsCandidate(Entry(), Art(), aboveGear: false));

    /// <summary>
    /// The layout has to be DECLARED. Nothing can infer it — the two halves of an ordinary face texture are
    /// two regions of one face, not two sides of a head — which is why the Create tab's tick is the only
    /// thing that says so.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("gen2")]
    [InlineData(UVRemapService.FaceSpace)]
    public void Art_that_does_not_declare_the_doubled_layout_is_not_a_candidate(string? space)
        => Assert.False(FaceUvDoublingService.IsCandidate(Entry(), Art(space: space), aboveGear: false));

    /// <summary>Symmetric art has no second side to keep, so it stays on the cheap path.</summary>
    [Fact]
    public void Symmetric_art_is_not_a_candidate()
        => Assert.False(FaceUvDoublingService.IsCandidate(Entry(), Art(asymmetric: false), aboveGear: false));

    /// <summary>
    /// Only a FACE material. The body's doubled sheet is bibo and has its own un-mirroring path; the eyes
    /// are their own surface inside the same folder and their own material.
    /// </summary>
    [Theory]
    [InlineData(BodyMtrl)]
    [InlineData(IrisMtrl)]
    public void Only_face_materials_are_handled_in_place(string material)
        => Assert.False(FaceUvDoublingService.IsCandidate(Entry(), Art(material), aboveGear: false));

    /// <summary>
    /// Everything that genuinely needs character.shpk keeps the shell: a colour table, a scrolling glow, or
    /// a place above a garment where no skin shows through. The face's own material is skin.shpk and has
    /// none of those — no per-texel row selector at all.
    /// </summary>
    [Fact]
    public void Art_that_needs_the_gear_shader_keeps_its_shell()
    {
        var glow = new List<ColorTableRowPreset>
            { new() { Row = 16, SubRowA = new ColorTableSubRowPreset { Emissive = 0.5f } } };
        Assert.True(RenderModeInference.HasCloth(glow));   // the premise this test rests on

        Assert.False(FaceUvDoublingService.IsCandidate(Entry(), Art(rows: glow), aboveGear: false));
        Assert.False(FaceUvDoublingService.IsCandidate(Entry(), Art(scroll: "glow.png"), aboveGear: false));
        Assert.False(FaceUvDoublingService.IsCandidate(Entry(), Art(layer: OverlayLayer.Gear), aboveGear: false));
        Assert.False(FaceUvDoublingService.IsCandidate(Entry(), Art(), aboveGear: true));
    }

    /// <summary>A pack that wants geometry is asking for a shell in as many words.</summary>
    [Fact]
    public void A_pack_that_wants_geometry_keeps_its_shell()
        => Assert.False(FaceUvDoublingService.IsCandidate(
            Entry(new ProteusMetadata { SmoothNipples = true }), Art(), aboveGear: false));

    // ── the shape test the Create tab ticks itself by ────────────────────────
    // A doubled sheet is exactly twice as wide as the sheet it doubles — but what that LOOKS like depends
    // on the race, which is why the tab compares against the face the art targets instead of testing a
    // fixed aspect. Both native sizes below are measured off real game files.

    [Theory]
    // Au Ra: the face sheet is square, because the horns take its right half. Doubled it is 2:1.
    [InlineData(4096, 2048, 2048, 2048, true)]
    [InlineData(2048, 1024, 2048, 2048, true)]     // the same sheet at half the resolution
    [InlineData(2048, 2048, 2048, 2048, false)]    // the face's own layout, at its own size
    [InlineData(4096, 4096, 2048, 2048, false)]    // and at 4K — still not doubled
    // Every other race: 1024x2048, so a doubled sheet is SQUARE. The two rules are opposites, which is
    // exactly why one fixed aspect cannot serve both.
    [InlineData(2048, 2048, 1024, 2048, true)]
    [InlineData(4096, 4096, 1024, 2048, true)]
    [InlineData(1024, 2048, 1024, 2048, false)]
    [InlineData(4096, 8192, 1024, 2048, false)]
    public void A_doubled_sheet_is_twice_as_wide_as_the_face_it_targets(
        int artW, int artH, int nativeW, int nativeH, bool doubled)
        => Assert.Equal(doubled, ModCreationService.IsDoubledAspect(artW, artH, nativeW, nativeH));

    /// <summary>Anything unmeasurable answers NO. A wrong "yes" sends the two halves of an ordinary face
    /// texture to the two sides of the head; a wrong "no" is one tick the author can see and set.</summary>
    [Theory]
    [InlineData(0, 2048, 1024, 2048)]
    [InlineData(2048, 0, 1024, 2048)]
    [InlineData(2048, 2048, 0, 2048)]
    [InlineData(2048, 2048, 1024, -1)]
    public void An_unmeasurable_shape_is_not_doubled(int artW, int artH, int nativeW, int nativeH)
        => Assert.False(ModCreationService.IsDoubledAspect(artW, artH, nativeW, nativeH));

    /// <summary>The face/iris split the rest of this leans on, stated once.</summary>
    [Fact]
    public void IsFaceMaterial_knows_the_eyes_are_not_the_face()
    {
        Assert.True(FaceUvDoublingService.IsFaceMaterial(FaceMtrl));
        Assert.False(FaceUvDoublingService.IsFaceMaterial(IrisMtrl));
        Assert.False(FaceUvDoublingService.IsFaceMaterial(BodyMtrl));
        Assert.False(FaceUvDoublingService.IsFaceMaterial(null));
    }
}
