using Proteus.Services;
using StbImageSharp;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The preload blob is only useful if it actually decodes: TextureLoader's constructor runs it inside a
/// try/catch that logs at Debug, so a malformed PNG would leave StbImageSharp unloaded — reintroducing the
/// "AssemblyLoadContext is unloading" crash on plugin reload — while looking installed. The first version of
/// these bytes was hand-written and had a bad IDAT CRC, so this is not a hypothetical.
/// </summary>
public class ImageCodecPreloadTests
{
    [Fact]
    public void OnePixelPng_Decodes()
    {
        var img = ImageResult.FromMemory(TextureLoader.OnePixelPng, ColorComponents.RedGreenBlueAlpha);

        Assert.NotNull(img);
        Assert.Equal(1, img.Width);
        Assert.Equal(1, img.Height);
        Assert.Equal(4, img.Data.Length);   // one RGBA pixel
    }
}
