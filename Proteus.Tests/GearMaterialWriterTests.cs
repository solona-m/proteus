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

    /// <summary>
    /// The colour-table guard <see cref="MtrlTexturePaths.HasColorTable"/> reports agrees with the one
    /// <see cref="GearMaterialWriter.PatchColorTable"/> enforces, on a REAL material.
    /// <para/>
    /// The two are computed from different things — the flag reads the declared data-set size, the writer
    /// bounds-checks the actual buffer — and the colour panel decides whether to draw a live grid from the
    /// first while the second decides whether an edit survives. They only have to agree; the parser's own
    /// tests pin the flag, this pins that agreement where it matters.
    /// </summary>
    [Fact]
    public void The_colour_table_flag_agrees_with_what_the_writer_will_accept()
    {
        var mtrl = RealMaterial();
        if (mtrl == null) return;

        Assert.True(TextureLoader.ParseMtrlBytes(mtrl).HasColorTable);

        // And the writer does in fact write to it, rather than no-opping.
        var rows = new Dictionary<int, GearColorRow> { [0] = new() { Diffuse = (1f, 0f, 0f) } };
        Assert.NotSame(mtrl, GearMaterialWriter.PatchColorTable(mtrl, rows));
    }

    /// <summary>
    /// <see cref="GearMaterialWriter.CopyColorTable"/> — what keeps an imported pack's look when Proteus
    /// rebuilds its material onto characterscroll for an animated glow.
    /// <para/>
    /// Without it a glowing piercing takes the VANILLA template's colour table: the author's silver, its
    /// metalness and its roughness gone, and e6257's own non-zero emissives inherited in their place. So
    /// this pins both halves — that all 2048 bytes arrive, and that nothing else moves.
    /// </summary>
    [Fact]
    public void Grafting_a_colour_table_moves_the_rows_and_nothing_else()
    {
        var src = RealMaterial();
        if (src == null) return;

        // A destination whose table is deliberately different everywhere, and whose header differs too —
        // a different texture count and string table is exactly the case this has to survive, since the two
        // materials are on different shaders.
        var dst = GearMaterialWriter.PatchColorTable(src, NeutralWhiteRows());
        int at = ColorSetStart(src);
        Assert.NotEqual(0, CountDifferingBytes(src, dst, at, 32 * 64));   // the fixture is actually different

        var grafted = GearMaterialWriter.CopyColorTable(dst, src);

        Assert.NotSame(dst, grafted);                                     // the caller's bytes are untouched
        Assert.Equal(dst.Length, grafted.Length);
        for (int i = 0; i < grafted.Length; i++)
        {
            bool inTable = i >= at && i < at + 32 * 64;
            Assert.True(grafted[i] == (inTable ? src[i] : dst[i]),
                $"byte {i} wrong ({(inTable ? "in" : "outside")} the colour table at {at})");
        }

        // A material with no Dawntrail table is left alone at either end — read at these offsets a legacy
        // 16-row layout is shredded, not copied.
        var noTable = new byte[512];
        noTable[12] = 1; noTable[13] = 1; noTable[14] = 0; noTable[15] = 0;
        Assert.Same(noTable, GearMaterialWriter.CopyColorTable(noTable, src));
        Assert.Same(dst, GearMaterialWriter.CopyColorTable(dst, noTable));
    }

    /// <summary>
    /// The two steps that follow <see cref="GearMaterialWriter.Build"/> when a pack's material is rebuilt
    /// onto characterscroll for an animated glow: graft the author's colour table over the template's, then
    /// arm the effect on the rows the user gave an emissive.
    /// <para/>
    /// Build itself is not exercised here — it needs the vanilla template out of the game files, and the
    /// shell path has run it in production for as long as glow has existed. What is new is the ORDER and
    /// what survives it, and every step of that fails by rendering something plausible rather than nothing:
    /// a colour table taken from the template still draws, an unarmed row still draws.
    /// <para/>
    /// The pack's own material stands in for the built one. That is not a shortcut — both are Dawntrail
    /// 32×64 materials located from their own headers, which is the entire reason this works across shaders.
    /// </summary>
    [Fact]
    public void A_glow_rebuild_keeps_the_authors_colours_and_arms_only_the_rows_that_asked()
    {
        var pack = RealMaterial();
        if (pack == null) return;

        var packTex = TextureLoader.ParseMtrlBytes(pack);
        // What makes this pack the easy case, and worth pinning: three textures, none of them a base — so
        // nothing is lost when the material moves to a shader with no base slot.
        Assert.Null(packTex.Diffuse);
        Assert.NotNull(packTex.Normal);
        Assert.NotNull(packTex.Mask);
        Assert.NotNull(packTex.Index);

        // Stand-in for Build's output: a material whose colour table is the TEMPLATE's, not the author's.
        var built = GearMaterialWriter.PatchColorTable(pack, NeutralWhiteRows());
        var grafted = GearMaterialWriter.CopyColorTable(built, pack);

        // Row 1 sub-row B — the cell this pack's index texture actually samples — with the Glow dial up.
        // Field 23 is the master switch: without it the effect never renders however right everything else
        // is, and PatchColorTable only arms a row whose dial is above zero.
        const int subRowB = 1;
        int at = ColorSetStart(grafted);
        var rows = SecondSkinService.BuildSparseRows(
            [new ColorTableRowPreset { Row = 1, SubRowB = new ColorTableSubRowPreset { Emissive = 1f } }]);
        var final = GearMaterialWriter.PatchColorTable(grafted, rows, isScroll: true);

        int rowAt = at + subRowB * 64;
        Assert.Equal(1f, Half(final, rowAt + 23 * 2), 2);            // HEffectEnable
        Assert.True(Half(final, rowAt + 21 * 2) > 0f, "the effect's visibility must not be left at zero");

        // A row the user never touched is not a glow row and stays unarmed — arming the whole table would
        // light up parts of the piece the author never meant to glow.
        const int untouched = 4;
        Assert.Equal(0f, Half(final, at + untouched * 64 + 23 * 2), 3);

        // And the pack's own roughness and metalness on the armed row came through the graft intact — the
        // silver of a metal piercing, which the template would otherwise have replaced.
        Assert.Equal(Half(pack, at + subRowB * 64 + 16 * 2), Half(final, rowAt + 16 * 2), 3);   // roughness
        Assert.Equal(Half(pack, at + subRowB * 64 + 18 * 2), Half(final, rowAt + 18 * 2), 3);   // metalness

        // Without isScroll nothing is armed, whatever the dial says: a plain gear material has no effect to
        // switch on and field 23 means something else there.
        var plain = GearMaterialWriter.PatchColorTable(grafted, rows);
        Assert.Equal(Half(grafted, rowAt + 23 * 2), Half(plain, rowAt + 23 * 2), 3);
    }

    /// <summary>
    /// A glow's colour resolves <c>EmissiveColor → Diffuse</c>, and a row with NEITHER stays dark however
    /// high its intensity.
    /// <para/>
    /// That looks like a bug and was briefly "fixed" as one — resolving a bare intensity to white, since
    /// that is what the editor's swatch shows. It is not a bug to fix HERE. Doing so reinterprets every row
    /// already authored: a shipped bodysuit carried five sub-rows with Glow at 1.0 and no colour, inert for
    /// as long as it had existed, and all five began emitting at full strength and blew its patterns out.
    /// The mismatch belongs to the editor, which now stores white when the slider is raised on a row with
    /// no colour to fall back on.
    /// </summary>
    [Fact]
    public void A_glow_with_no_colour_anywhere_stays_dark_however_high_the_dial()
    {
        Dictionary<int, GearColorRow> Rows(ColorTableSubRowPreset b)
            => SecondSkinService.BuildSparseRows([new ColorTableRowPreset { Row = 1, SubRowB = b }])!;

        // Intensity alone, no colour: dark. Existing mods depend on this.
        var bare = Rows(new ColorTableSubRowPreset { Emissive = 1f })[1];
        Assert.Equal((0f, 0f, 0f), bare.Emissive);

        // With a colour it scales by the intensity, rather than being switched on and off by it.
        var white = Rows(new ColorTableSubRowPreset { Emissive = 1f, EmissiveColor = "#FFFFFF" })[1];
        var half  = Rows(new ColorTableSubRowPreset { Emissive = 0.5f, EmissiveColor = "#FFFFFF" })[1];
        Assert.Equal((1f, 1f, 1f), white.Emissive);
        Assert.Equal((0.5f, 0.5f, 0.5f), half.Emissive);

        // An explicit glow colour wins; the diffuse is the documented fallback when only it is set.
        Assert.Equal((0f, 0f, 1f),
            Rows(new ColorTableSubRowPreset { Emissive = 1f, Diffuse = "#0000FF" })[1].Emissive);
        Assert.Equal((1f, 0f, 0f),
            Rows(new ColorTableSubRowPreset
            { Emissive = 1f, EmissiveColor = "#FF0000", Diffuse = "#0000FF" })[1].Emissive);

        // Zero intensity is still no glow, and must still be WRITTEN — a vanilla characterscroll template
        // carries a warm emissive of its own that would otherwise be inherited as a flat white wash.
        var off = Rows(new ColorTableSubRowPreset { Emissive = 0f, EmissiveColor = "#FF0000" })[1];
        Assert.Equal((0f, 0f, 0f), off.Emissive);

        // The dial itself is carried through regardless, because characterscroll's arming needs the number
        // rather than the colour it produced — see the scroll test below.
        Assert.Equal(1f,   bare.EmissiveStrength);
        Assert.Equal(0.5f, half.EmissiveStrength);
        Assert.Equal(0f,   off.EmissiveStrength);

        // Which is why arming seeds a colour as well as an intensity — without it the piece stays dark.
        var seeded = new List<ColorTableRowPreset>();
        ContentGlowRow.Arm(seeded, 1, subRowA: false);
        Assert.NotEqual((0f, 0f, 0f), SecondSkinService.BuildSparseRows(seeded)![1].Emissive);
    }

    /// <summary>
    /// Arming a scrolling material switches the effect on and leaves the row emissive alone, because on
    /// this shader that emissive is what the effect's brightness scales with.
    /// <para/>
    /// Measured rather than assumed, and the wrong guess cost two rounds. Pinning the emissive to a small
    /// gate and sending the dial to field 21 instead left a piece barely visible while its visibility was
    /// already at 1.0 — which is exactly what proves field 21 is not the brightness. Nor does a full dial
    /// add white: the published scroll map here is a saturated orange (mean RGB ≈ 212, 110, 22), so a
    /// surface that reads white at 1.0 is blowing out, and the answer is a lower dial rather than a
    /// different field.
    /// </summary>
    [Fact]
    public void Arming_a_scrolling_material_leaves_the_emissive_as_the_effects_brightness()
    {
        var mtrl = RealMaterial();
        if (mtrl == null) return;

        int at = ColorSetStart(mtrl), row = 1, rowAt = at + row * 64;
        byte[] Scroll(ColorTableSubRowPreset b) => GearMaterialWriter.PatchColorTable(
            mtrl, SecondSkinService.BuildSparseRows([new ColorTableRowPreset { Row = 1, SubRowB = b }]),
            isScroll: true);

        // The dial reaches the emissive untouched — that is the brightness — and the effect is armed.
        // Both fields, exactly as ContentGlowRow.Arm seeds them — a colour is needed or the row is dark.
        var quarter = Scroll(new ColorTableSubRowPreset
        { Emissive = ContentGlowRow.DefaultGlow, EmissiveColor = ContentGlowRow.DefaultGlowColour });
        Assert.Equal(ContentGlowRow.DefaultGlow, Half(quarter, rowAt + 8 * 2), 2);
        Assert.Equal(1f, Half(quarter, rowAt + 23 * 2), 3);
        Assert.True(Half(quarter, rowAt + 21 * 2) > 0f, "the effect's visibility must not be left at zero");

        // It scales, rather than being pinned to a constant.
        Assert.Equal(1f, Half(Scroll(new ColorTableSubRowPreset
        { Emissive = 1f, EmissiveColor = ContentGlowRow.DefaultGlowColour }), rowAt + 8 * 2), 2);

        // The default is chosen NOT to blow out — a saturated map at full is what reads as a white blob.
        Assert.True(ContentGlowRow.DefaultGlow > 0f && ContentGlowRow.DefaultGlow < 1f);

        // A glow colour still tints, as on any other shader.
        var red = Scroll(new ColorTableSubRowPreset { Emissive = 1f, EmissiveColor = "#FF0000" });
        Assert.Equal(1f, Half(red, rowAt + 8 * 2), 2);
        Assert.Equal(0f, Half(red, rowAt + 8 * 2 + 2), 2);

        // An explicit sphere intensity wins over the default visibility — it is the same field, and an
        // author reaching for it meant that number.
        var pinned = Scroll(new ColorTableSubRowPreset
        { Emissive = 1f, EmissiveColor = ContentGlowRow.DefaultGlowColour, SphereIntensity = 0.4f });
        Assert.Equal(0.4f, Half(pinned, rowAt + 21 * 2), 3);

        // Dial at zero arms nothing at all.
        Assert.Equal(Half(mtrl, rowAt + 23 * 2),
                     Half(Scroll(new ColorTableSubRowPreset { Emissive = 0f }), rowAt + 23 * 2), 3);

        // And arming never happens on a plain gear material, where field 23 means something else.
        var cloth = GearMaterialWriter.PatchColorTable(
            mtrl, SecondSkinService.BuildSparseRows(
                [new ColorTableRowPreset { Row = 1, SubRowB = new ColorTableSubRowPreset
                { Emissive = 1f, EmissiveColor = ContentGlowRow.DefaultGlowColour } }]));
        Assert.Equal(1f, Half(cloth, rowAt + 8 * 2), 3);
        Assert.Equal(Half(mtrl, rowAt + 23 * 2), Half(cloth, rowAt + 23 * 2), 3);
    }

    /// <summary>
    /// <see cref="GearMaterialWriter.ReadPhysical"/> reports what a material's colour table really holds,
    /// so the editor can stop showing its own defaults over someone else's values.
    /// <para/>
    /// The grid falls back to 0 metalness and 0.5 roughness for anything the sidecar has not overridden.
    /// That is true of a shell, built from a neutral template — and false of an imported pack: this one
    /// arrives at metalness 1.0, so the panel read 0 while the piece rendered metallic, and the control that
    /// would have fixed it looked as though it already had.
    /// </summary>
    [Fact]
    public void The_physical_values_a_material_already_holds_can_be_read_back()
    {
        var mtrl = RealMaterial();
        if (mtrl == null) return;

        var phys = GearMaterialWriter.ReadPhysical(mtrl);
        Assert.NotNull(phys);
        Assert.Equal(32, phys!.Count);

        // Sub-row 1 is row pair 1 column B — the cell this pack's index texture selects, and where its
        // metal look comes from.
        int at = ColorSetStart(mtrl);
        Assert.Equal(Half(mtrl, at + 64 + 16 * 2), phys[1].Roughness, 3);
        Assert.Equal(Half(mtrl, at + 64 + 18 * 2), phys[1].Metalness, 3);
        Assert.Equal(1f, phys[1].Metalness, 2);      // the value the panel was hiding behind a 0

        // It follows the writer, so a change made through the editor reads back.
        var patched = GearMaterialWriter.PatchColorTable(
            mtrl, new Dictionary<int, GearColorRow> { [1] = new() { Metalness = 0f, Roughness = 0.25f } });
        var after = GearMaterialWriter.ReadPhysical(patched)!;
        Assert.Equal(0f, after[1].Metalness, 3);
        Assert.Equal(0.25f, after[1].Roughness, 3);
        Assert.Equal(phys[0], after[0]);             // and touches no other row

        // A material with no Dawntrail table has nothing to report.
        var noTable = new byte[512];
        noTable[12] = 1; noTable[13] = 1; noTable[14] = 0; noTable[15] = 0;
        Assert.Null(GearMaterialWriter.ReadPhysical(noTable));
    }

    private static Dictionary<int, GearColorRow> NeutralWhiteRows()
    {
        var rows = new Dictionary<int, GearColorRow>();
        for (int r = 0; r < 32; r++)
            rows[r] = new GearColorRow { Diffuse = (1f, 1f, 1f), Roughness = 0.5f, Metalness = 0f };
        return rows;
    }

    private static int CountDifferingBytes(byte[] a, byte[] b, int at, int len)
    {
        int n = 0;
        for (int i = at; i < at + len && i < a.Length && i < b.Length; i++)
            if (a[i] != b[i]) n++;
        return n;
    }
}
