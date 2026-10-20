using System;
using System.IO;
using BitMiracle.LibTiff.Classic;

namespace Proteus.Services;

/// <summary>
/// Writes a uv transfer map in the layout <see cref="UVRemapService"/> reads: a square 16-bit RGBA TIFF whose red and
/// green are the source coordinate scaled to 0..65535 and whose alpha says whether the pixel has one at all.
/// <para/>
/// Deflate with a horizontal predictor: a map is smooth across each island, so neighbouring values differ by little and
/// the file shrinks to a fraction of the 128 MB the uncompressed v1 maps are. Strips are read row by row, which is how
/// the loader reads them.
/// </summary>
internal static class UvTransferMapFile
{
    private const int RowsPerStrip = 16;

    /// <summary>The TIFF's bytes.</summary>
    internal static byte[] Encode(ushort[] r, ushort[] g, bool[] valid, int size)
    {
        if (r.Length != size * size || g.Length != r.Length || valid.Length != r.Length)
            throw new ArgumentException("map arrays do not match the size");

        var stream = new MemoryStream();
        using (var tiff = Tiff.ClientOpen("map", "w", stream, new TiffStream()))
        {
            if (tiff == null) throw new IOException("the TIFF writer could not be opened");
            tiff.SetField(TiffTag.IMAGEWIDTH, size);
            tiff.SetField(TiffTag.IMAGELENGTH, size);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 16);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 4);
            tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
            tiff.SetField(TiffTag.COMPRESSION, Compression.ADOBE_DEFLATE);
            tiff.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL);
            tiff.SetField(TiffTag.ROWSPERSTRIP, RowsPerStrip);

            var line = new byte[size * 8];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x, o = x * 8;
                    bool v = valid[i];
                    ushort rr = v ? r[i] : (ushort)0, gg = v ? g[i] : (ushort)0, aa = v ? ushort.MaxValue : (ushort)0;
                    line[o] = (byte)rr; line[o + 1] = (byte)(rr >> 8);
                    line[o + 2] = (byte)gg; line[o + 3] = (byte)(gg >> 8);
                    line[o + 4] = 0; line[o + 5] = 0;
                    line[o + 6] = (byte)aa; line[o + 7] = (byte)(aa >> 8);
                }
                if (!tiff.WriteScanline(line, y)) throw new IOException($"row {y} of the map could not be written");
            }
            tiff.Flush();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// <paramref name="bytes"/> written to <paramref name="path"/> through a temporary file beside it, so a reader never
    /// sees half a map and a failure leaves nothing behind.
    /// </summary>
    internal static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
