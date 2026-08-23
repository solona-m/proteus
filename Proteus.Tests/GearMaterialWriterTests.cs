using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <see cref="GearMaterialWriter.PatchColorTable"/> — the row writer an imported content pack's OWN
/// material goes through.
/// <para/>
/// It matters more than a shell's colour write does. A shell material is rebuilt from a vanilla template
/// every composite, so a mistake there is overwritten next run; this one edits a file the author shipped,
/// in place, and everything it must NOT touch — the texture table, the shader section, the rows the user
/// never opened — has no second source to be restored from.
/// </summary>
public class GearMaterialWriterTests
{
    private const string SamplePack = @"E:\ModPacks\Neolithe Piercings for Proteus.pmp";
    private const string SampleMtrl = "common/1/mt_c0201b0001_neolithe_piercings.mtrl";

    /// <summary>A real Dawntrail material (character.shpk, 3 samplers, one 32×64 colour set + dye table).</summary>
    private static byte[]? RealMaterial()
    {
        if (!File.Exists(SamplePack)) return null;
        using var zip = ZipFile.OpenRead(SamplePack);
        var e = zip.GetEntry(SampleMtrl);
        if (e == null) return null;
        using var st = e.Open();
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    private static int ColorSetStart(byte[] m)
        => 16 + m[12] * 4 + m[13] * 4 + m[14] * 4 + BitConverter.ToUInt16(m, 8) + m[15];

    private static float Half(byte[] m, int at)
        => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(m, at));

    [Fact]
    public void Patching_one_row_leaves_every_other_byte_alone()
    {
        var mtrl = RealMaterial();
        if (mtrl == null) return;

        // The layout this is written against. Asserted rather than assumed: if a future pack has a legacy
        // 16-row table these offsets are wrong, and the no-op guard below is what has to catch it.
        Assert.Equal(3, mtrl[12]);                                  // texture count
        Assert.Equal(1, mtrl[14]);                                  // colour set count
        Assert.Equal(2176, BitConverter.ToUInt16(mtrl, 6));         // 32 x 64 rows + a 128-byte dye table

        const int row = 5;
        var patched = GearMaterialWriter.PatchColorTable(
            mtrl, new Dictionary<int, GearColorRow> { [row] = new() { Diffuse = (0.25f, 0.5f, 0.75f) } });

        Assert.NotSame(mtrl, patched);                              // the caller's bytes are never mutated
        Assert.Equal(mtrl.Length, patched.Length);

        int cs = ColorSetStart(mtrl);
        Assert.Equal(212, cs);                                      // this pack's, pinned so a drift is visible

        int rowAt = cs + row * 64;
        Assert.Equal(0.25f, Half(patched, rowAt),     3);
        Assert.Equal(0.50f, Half(patched, rowAt + 2), 3);
        Assert.Equal(0.75f, Half(patched, rowAt + 4), 3);

        // Everything outside that row's first three halves is byte-identical — header, string table,
        // texture offsets, the other 31 rows, the dye table and the shader section alike.
        for (int i = 0; i < mtrl.Length; i++)
        {
            if (i >= rowAt && i < rowAt + 6) continue;
            Assert.True(mtrl[i] == patched[i], $"byte {i} changed (colour set starts at {cs})");
        }
    }

    [Fact]
    public void Untouched_fields_of_a_patched_row_keep_the_authors_values()
    {
        var mtrl = RealMaterial();
        if (mtrl == null) return;

        const int row = 0;
        int rowAt = ColorSetStart(mtrl) + row * 64;
        float authorSpecular = Half(mtrl, rowAt + 4 * 2);   // HSpecular
        float authorEmissive = Half(mtrl, rowAt + 8 * 2);   // HEmissive

        // A row preset with only a diffuse set must not clear the rest — a null field means "leave it".
        var patched = GearMaterialWriter.PatchColorTable(
            mtrl, new Dictionary<int, GearColorRow> { [row] = new() { Diffuse = (1f, 0f, 0f) } });

        Assert.Equal(authorSpecular, Half(patched, rowAt + 4 * 2), 3);
        Assert.Equal(authorEmissive, Half(patched, rowAt + 8 * 2), 3);
    }

    [Fact]
    public void Nothing_to_write_returns_the_input_unchanged()
    {
        var mtrl = RealMaterial() ?? new byte[512];
        var rows = new Dictionary<int, GearColorRow> { [0] = new() { Diffuse = (1f, 1f, 1f) } };

        Assert.Same(mtrl, GearMaterialWriter.PatchColorTable(mtrl, null));
        Assert.Same(mtrl, GearMaterialWriter.PatchColorTable(mtrl, new Dictionary<int, GearColorRow>()));

        // A material with no colour set has nothing to patch…
        var noColorSet = new byte[512];
        noColorSet[12] = 1; noColorSet[13] = 1; noColorSet[14] = 0; noColorSet[15] = 0;
        Assert.Same(noColorSet, GearMaterialWriter.PatchColorTable(noColorSet, rows));

        // …and one whose data set is too small for a Dawntrail 32×64 table must be left alone rather than
        // written past its end or, worse, shredded at offsets that mean something else in a legacy layout.
        var tooSmall = new byte[512];
        tooSmall[12] = 1; tooSmall[13] = 1; tooSmall[14] = 1; tooSmall[15] = 0;
        Assert.Same(tooSmall, GearMaterialWriter.PatchColorTable(tooSmall, rows));

        Assert.Same(Array.Empty<byte>(), GearMaterialWriter.PatchColorTable(Array.Empty<byte>(), rows));
    }
}
