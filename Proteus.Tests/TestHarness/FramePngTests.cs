using Proteus.TestHarness;
using StbImageSharp;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class FramePngTests
{
    [Fact]
    public void Bgra_PaddedPitch_EncodesExpectedPixels()
    {
        // [AC-qa-harness-7] [EDGE-qa-harness-30] BGRA to an opaque RGB PNG; a padded pitch is skipped over, and the
        // last row need not be padded.
        // 2x2, pitch 12 (8 bytes of pixels + 4 padding), last row unpadded: 12 + 8 = 20 bytes.
        byte[] data =
        [
            0, 0, 255, 0,   0, 255, 0, 0,   9, 9, 9, 9,   // red, green, padding
            255, 0, 0, 0,   10, 20, 30, 0,                // blue, (30,20,10)
        ];
        var png = FramePng.Encode(new RawFrame(2, 2, 12, FramePng.B8G8R8A8Unorm, data));
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        var img = ImageResult.FromMemory(png, ColorComponents.RedGreenBlue);
        Assert.Equal(2, img.Width);
        Assert.Equal([255, 0, 0, 0, 255, 0, 0, 0, 255, 30, 20, 10], img.Data);
    }

    [Fact]
    public void ShortBuffer_Fails()
    {
        // [EDGE-qa-harness-30] Fewer bytes than it claims: refused, never read past the end.
        Assert.Throws<BadFrameException>(() => FramePng.Encode(new RawFrame(2, 2, 12, FramePng.B8G8R8A8Unorm, new byte[19])));
        Assert.Throws<BadFrameException>(() => FramePng.Encode(new RawFrame(2, 2, 4, FramePng.B8G8R8A8Unorm, new byte[100])));
    }

    [Fact]
    public void WrongFormat_Fails()
    {
        // [EDGE-qa-harness-30]
        Assert.Throws<BadFrameException>(() => FramePng.Encode(new RawFrame(1, 1, 4, 28, new byte[4])));
        Assert.Throws<BadFrameException>(() => FramePng.Encode(new RawFrame(0, 1, 4, FramePng.B8G8R8A8Unorm, new byte[4])));
    }
}
