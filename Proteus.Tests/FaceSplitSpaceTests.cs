using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The doubled FACE layout, and the pairing that makes it work.
/// <para/>
/// The vanilla face UV is mirrored in exactly the sense gen2 is — both cheeks sample the same texels
/// (measured on c0201f0001_fac: 89.4% of mirror-partner vertices share a UV, 1.3% reflect) — so a one-sided
/// mark cannot be expressed in it: paint a texel and it IS both sides. Bodies solve this with bibo, which
/// gives each side its own half; faces had no equivalent, so <see cref="UVRemapService.FaceSplitSpace"/> is
/// one. Both are the SAME transform, which is why they share a code path rather than two.
/// </summary>
public class FaceSplitSpaceTests
{
    private static UVRemapService Service() => new(Substitute.For<IPluginLog>(), ".");

    /// <summary>The pairing itself: each mirrored space names the doubled one it is a half of.</summary>
    [Fact]
    public void Mirrored_spaces_name_their_doubled_counterpart()
    {
        Assert.Equal("bibo", UVRemapService.DoubledSpaceOf("gen2"));
        Assert.Equal(UVRemapService.FaceSplitSpace, UVRemapService.DoubledSpaceOf(UVRemapService.FaceSpace));
        Assert.Equal(UVRemapService.FaceSplitSpace, UVRemapService.DoubledSpaceOf("FACE"));   // case-insensitive
    }

    /// <summary>A doubled space is not itself mirrored — it is the destination, not the source.</summary>
    [Fact]
    public void Doubled_spaces_are_not_mirrored()
    {
        Assert.Null(UVRemapService.DoubledSpaceOf("bibo"));
        Assert.Null(UVRemapService.DoubledSpaceOf(UVRemapService.FaceSplitSpace));
        Assert.Null(UVRemapService.DoubledSpaceOf("gen3"));
        Assert.Null(UVRemapService.DoubledSpaceOf(null));
    }

    /// <summary>
    /// The face conversion is the same affine gen2 → bibo uses: the +X side into the right half, the -X side
    /// into its reflection. Asserted against the body pair directly, so the two can never drift apart.
    /// </summary>
    [Fact]
    public void The_face_conversion_matches_the_body_one()
    {
        var uv = Service();
        var face = uv.UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace, unmirror: true)!;
        var body = uv.UvConverter("gen2", "bibo", unmirror: true)!;

        Assert.NotNull(face);
        foreach (var u in new[] { 0f, 0.2f, 0.5f, 0.9f, 1f })
        {
            Assert.Equal(body(u, 0.4f, 1), face(u, 0.4f, 1));
            Assert.Equal(body(u, 0.4f, -1), face(u, 0.4f, -1));
        }
    }

    /// <summary>
    /// The two sides land in opposite halves and reflect about the middle of the sheet — which is what gives
    /// a one-sided mark somewhere to live.
    /// </summary>
    [Fact]
    public void The_two_sides_of_a_face_take_opposite_halves()
    {
        var conv = Service().UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace,
                                         unmirror: true)!;
        foreach (var u in new[] { 0f, 0.3f, 0.75f, 1f })
        {
            var right = conv(u, 0.6f, 1)!.Value;
            var left = conv(u, 0.6f, -1)!.Value;
            Assert.Equal(1f, right.U + left.U, 5);
            Assert.True(right.U >= 0.5f && left.U <= 0.5f);
            Assert.Equal(0.6f, right.V, 5);
        }
    }

    /// <summary>Without the flag the two sides collapse onto the same texels — today's fold, unchanged.</summary>
    [Fact]
    public void Without_unmirroring_a_face_still_folds()
    {
        var conv = Service().UvConverter(UVRemapService.FaceSpace, UVRemapService.FaceSplitSpace)!;
        Assert.Equal(conv(0.3f, 0.7f, 1), conv(0.3f, 0.7f, -1));
    }

    /// <summary>
    /// Asymmetry is meaningless in a MIRRORED space — its two sides are one sheet — so the detector refuses
    /// the vanilla face layout exactly as it refuses gen2.
    /// </summary>
    [Fact]
    public void Asymmetry_is_not_asked_of_a_mirrored_space()
    {
        var uv = Service();
        var art = new byte[64 * 64 * 4];
        for (int i = 0; i < art.Length; i += 4) { art[i] = 200; art[i + 3] = 255; }

        Assert.False(uv.IsArtAsymmetric(art, 64, 64, UVRemapService.FaceSpace));
        Assert.False(uv.IsArtAsymmetric(art, 64, 64, "gen2"));
    }

    // ── promotion ────────────────────────────────────────────────────────────

    private static OverlayDescriptor FaceArt(bool? asymmetric, string? source) => new()
    {
        Layer = OverlayLayer.Skin,
        AsymmetricArt = asymmetric,
        SourceBodyType = source,
        MaterialGamePaths = ["chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl"],
    };

    /// <summary>
    /// A doubled face sheet needs the shell whatever body is worn. The vanilla face layout is mirrored on
    /// every character — there is no second face to be wearing — so the body-type question never applies,
    /// and activeBodyTypes never names a face anyway.
    /// </summary>
    [Fact]
    public void A_split_face_sheet_needs_a_shell_on_any_body()
    {
        Assert.True(CompositorService.NeedsUnmirroredShell(
            FaceArt(true, UVRemapService.FaceSplitSpace), wearingMirroredBody: false));
        Assert.True(CompositorService.NeedsUnmirroredShell(
            FaceArt(true, UVRemapService.FaceSplitSpace), wearingMirroredBody: true));
    }

    /// <summary>
    /// Art in the vanilla face layout has only one side to begin with, so there is nothing to un-mirror —
    /// the same reason gen2-authored body art is refused.
    /// </summary>
    [Fact]
    public void Ordinary_face_art_is_left_alone()
    {
        Assert.False(CompositorService.NeedsUnmirroredShell(
            FaceArt(true, UVRemapService.FaceSpace), wearingMirroredBody: true));
    }

    /// <summary>Symmetric or unmeasured face art keeps the cheap path, like everywhere else.</summary>
    [Fact]
    public void Symmetric_or_unmeasured_face_art_is_left_alone()
    {
        Assert.False(CompositorService.NeedsUnmirroredShell(
            FaceArt(false, UVRemapService.FaceSplitSpace), wearingMirroredBody: true));
        Assert.False(CompositorService.NeedsUnmirroredShell(
            FaceArt(null, UVRemapService.FaceSplitSpace), wearingMirroredBody: true));
    }
}
