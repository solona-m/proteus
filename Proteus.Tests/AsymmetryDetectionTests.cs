using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="UVRemapService.IsArtAsymmetric"/> — the measurement that decides whether an overlay is worth a
/// whole extra un-mirrored shell.
/// <para/>
/// Both errors cost something real. A false negative folds asymmetric art in half and silently loses a side,
/// which is the bug the feature exists to fix. A false positive builds a second mesh set for art that never
/// needed one. So the cases here are the two ends plus the noise floor in between.
/// <para/>
/// No transfer map is loaded (the detector asks for the island mask with <c>loadIfMissing: false</c>), so
/// every texel counts and these run on any machine.
/// </summary>
public class AsymmetryDetectionTests
{
    private static UVRemapService Service() => new(Substitute.For<IPluginLog>(), ".");

    private const int W = 256, H = 256;

    /// <summary>An opaque mid-grey sheet — the "no marks anywhere" baseline.</summary>
    private static byte[] Sheet(byte v = 128, byte a = 255)
    {
        var p = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            p[i * 4] = p[i * 4 + 1] = p[i * 4 + 2] = v;
            p[i * 4 + 3] = a;
        }
        return p;
    }

    /// <summary>Paints a filled rectangle in (r,g,b,a).</summary>
    private static void Rect(byte[] p, int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a)
    {
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int o = (y * W + x) * 4;
                p[o] = r; p[o + 1] = g; p[o + 2] = b; p[o + 3] = a;
            }
    }

    /// <summary>The mirror partner of a column, about the sheet's centre — the pairing the detector uses.</summary>
    private static int Mirror(int x) => W - 1 - x;

    [Fact]
    public void A_blank_sheet_is_symmetric()
    {
        Assert.False(Service().IsArtAsymmetric(Sheet(), W, H, "bibo"));
    }

    [Fact]
    public void A_mark_mirrored_onto_both_sides_is_symmetric()
    {
        var p = Sheet();
        Rect(p, 160, 60, 200, 100, 200, 30, 30, 255);
        // The same mark reflected into the left half, exactly as an artist mirroring a design would leave it.
        Rect(p, Mirror(200), 60, Mirror(160), 100, 200, 30, 30, 255);
        Assert.False(Service().IsArtAsymmetric(p, W, H, "bibo"));
    }

    [Fact]
    public void A_mark_on_one_side_only_is_asymmetric()
    {
        var p = Sheet();
        Rect(p, 160, 60, 200, 100, 200, 30, 30, 255);
        Assert.True(Service().IsArtAsymmetric(p, W, H, "bibo"));
    }

    /// <summary>
    /// The case the whole feature is for: a tattoo sheet that is transparent everywhere except one shoulder.
    /// It differs in ALPHA first, which is why alpha is compared unweighted while colour is weighted by it.
    /// </summary>
    [Fact]
    public void A_one_sided_mark_on_a_transparent_sheet_is_asymmetric()
    {
        var p = Sheet(0, 0);
        Rect(p, 150, 40, 210, 110, 10, 10, 200, 255);
        Assert.True(Service().IsArtAsymmetric(p, W, H, "bibo"));
    }

    /// <summary>
    /// Leftover colour under FULLY TRANSPARENT texels must not vote. Art tools leave whatever was painted
    /// there before the eraser, and it differs side to side constantly — counting it would send most
    /// symmetric sheets down the expensive path for pixels the game never samples.
    /// </summary>
    [Fact]
    public void Colour_hidden_under_zero_alpha_does_not_count()
    {
        var p = Sheet(0, 0);
        Rect(p, 160, 60, 200, 100, 255, 0, 255, 0);   // vivid, but alpha 0 on both this and its partner
        Assert.False(Service().IsArtAsymmetric(p, W, H, "bibo"));
    }

    /// <summary>
    /// Compression noise is not asymmetry. Symmetric art is never bit-identical across the seam after a
    /// round trip through a lossy format, so a difference below the channel threshold has to be ignored.
    /// </summary>
    [Fact]
    public void Noise_below_the_channel_threshold_is_symmetric()
    {
        var p = Sheet();
        // Every left-half texel off by a few levels — far more noise than a real codec leaves, and still
        // under the per-channel delta that counts as a difference.
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W / 2; x++)
            {
                int o = (y * W + x) * 4;
                p[o] = p[o + 1] = p[o + 2] = (byte)(128 + (x + y) % 8);
            }
        Assert.False(Service().IsArtAsymmetric(p, W, H, "bibo"));
    }

    /// <summary>
    /// A handful of differing texels is not worth a shell — the detector needs both a fraction AND a floor,
    /// so a stray speck (a dust brush, a stamp that landed one pixel off) can't promote a whole mod.
    /// </summary>
    [Fact]
    public void A_speck_too_small_to_notice_is_symmetric()
    {
        var p = Sheet();
        Rect(p, 180, 80, 182, 82, 255, 0, 0, 255);
        Assert.False(Service().IsArtAsymmetric(p, W, H, "bibo"));
    }

    /// <summary>
    /// gen2 IS the mirrored layout — one sheet describing both sides — so the question has no meaning there
    /// and must not come back true, or vanilla-native art would ask for a shell to un-mirror itself onto.
    /// An unnamed space gets the same conservative answer.
    /// </summary>
    [Fact]
    public void The_mirrored_space_itself_is_never_asymmetric()
    {
        var p = Sheet();
        Rect(p, 160, 60, 200, 100, 200, 30, 30, 255);

        var uv = Service();
        Assert.True(uv.IsArtAsymmetric(p, W, H, "bibo"));    // the same sheet, in an asymmetric space
        Assert.False(uv.IsArtAsymmetric(p, W, H, "gen2"));
        Assert.False(uv.IsArtAsymmetric(p, W, H, "GEN2"));   // the comparison is case-insensitive
        Assert.False(uv.IsArtAsymmetric(p, W, H, null));
    }

    /// <summary>A sheet too small, or a buffer too short for its declared size, answers false rather than
    /// reading off the end of it.</summary>
    [Fact]
    public void A_degenerate_sheet_is_refused()
    {
        var uv = Service();
        Assert.False(uv.IsArtAsymmetric(new byte[4 * 4 * 4], 4, 4, "bibo"));
        Assert.False(uv.IsArtAsymmetric(new byte[16], W, H, "bibo"));
    }
}
