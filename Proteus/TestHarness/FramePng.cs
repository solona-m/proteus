using System;
using System.IO;
using StbImageWriteSharp;

namespace Proteus.TestHarness;

/// <summary>A frame read back from the GPU.</summary>
internal sealed record RawFrame(int Width, int Height, int Pitch, int DxgiFormat, byte[] Data);

/// <summary>Thrown when a read-back frame cannot be encoded: the wrong format, or fewer bytes than it claims.</summary>
internal sealed class BadFrameException(string message) : Exception(message);

/// <summary>Encodes a read-back screen frame as an opaque RGB PNG.</summary>
internal static class FramePng
{
    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM, which the readback is asked to convert to.</summary>
    internal const int B8G8R8A8Unorm = 87;

    internal static byte[] Encode(RawFrame frame)
    {
        if (frame.DxgiFormat != B8G8R8A8Unorm) throw new BadFrameException($"frame format {frame.DxgiFormat}, expected {B8G8R8A8Unorm}");
        if (frame.Width <= 0 || frame.Height <= 0) throw new BadFrameException("empty frame");
        long rowBytes = (long)frame.Width * 4;
        if (frame.Pitch < rowBytes) throw new BadFrameException($"pitch {frame.Pitch} under a row of {rowBytes} bytes");
        // The last row need not be padded to the pitch; every byte read must be there.
        long needed = (long)(frame.Height - 1) * frame.Pitch + rowBytes;
        if (frame.Data.LongLength < needed) throw new BadFrameException($"frame has {frame.Data.LongLength} bytes, needs {needed}");

        var rgb = new byte[frame.Width * frame.Height * 3];
        for (int y = 0; y < frame.Height; y++)
        {
            int src = y * frame.Pitch, dst = y * frame.Width * 3;
            for (int x = 0; x < frame.Width; x++, src += 4, dst += 3)
            {
                rgb[dst] = frame.Data[src + 2];
                rgb[dst + 1] = frame.Data[src + 1];
                rgb[dst + 2] = frame.Data[src];
            }
        }
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(rgb, frame.Width, frame.Height, ColorComponents.RedGreenBlue, ms);
        return ms.ToArray();
    }
}
