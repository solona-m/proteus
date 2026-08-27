using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lumina.Data.Files;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for <see cref="TextureLoader.ParseMtrlBytes"/>, the hand-rolled .mtrl reader.
///
/// Why it exists: the compositor resolves every material's texture paths through this, and those paths
/// become the redirect keys Penumbra is asked to replace. A MISSING path degrades safely (that slot just
/// isn't composited); a WRONG one does not — Proteus would redirect a resource the game never requests and
/// the overlay silently fails with no warning, because the "no textures found" bail only fires when every
/// slot is null. So the offsets have to be pinned down by test, not by inspection.
///
/// It replaced Lumina's typed <c>MtrlFile</c>, which misreads some Dawntrail layouts — a mod-authored
/// material (older TexTools layout) read fine while the stock game file came back empty, so an overlay
/// targeting a vanilla material never composited at all.
/// </summary>
public class MtrlParseTests
{
    // Sampler CRCs, mirroring the private constants in TextureLoader.
    private const uint Diffuse   = 0x1E6FEF9Cu;
    private const uint ColorMap0 = 0x115306BEu;
    private const uint Normal    = 0x0C5EC1F1u;
    private const uint Mask      = 0x8A4E82B6u;
    private const uint Index     = 0x565F8FD8u;

    /// <summary>
    /// Build a syntactically valid .mtrl. Layout (v1.3): header, texture offset table, UV-set and
    /// colour-set tables, string table, additional data, the data set, then the shader block — its 12-byte
    /// header, the shader-key and constant tables, and finally the sampler array.
    /// <para/>
    /// <paramref name="dataSetSize"/> and <paramref name="additionalSize"/> are parameters precisely
    /// because they shift everything after the string table; a parser that ignored either would still pass
    /// a test that left them zero.
    /// </summary>
    private static byte[] BuildMtrl(
        (uint SamplerId, string Path)[] samplers,
        int dataSetSize = 0,
        int additionalSize = 0,
        int shaderKeyCount = 0,
        int constantCount = 0,
        string shaderPackage = "character.shpk",
        int colorSetCount = 0)
    {
        // String table: the shader package name first, then one entry per distinct texture path.
        var strings = new List<byte>();
        int shaderOffset = strings.Count;
        strings.AddRange(Encoding.UTF8.GetBytes(shaderPackage));
        strings.Add(0);

        var texOffsets = new List<ushort>();
        foreach (var (_, path) in samplers)
        {
            texOffsets.Add((ushort)strings.Count);
            strings.AddRange(Encoding.UTF8.GetBytes(path));
            strings.Add(0);
        }
        while (strings.Count % 4 != 0) strings.Add(0);   // real files pad the table

        int textureCount = texOffsets.Count;
        const int uvSetCount = 1;

        var b = new List<byte>();
        void U16(int v) => b.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));

        U32(0x01030000);                 // 0x00 version
        U16(0);                          // 0x04 file size (unused by the parser)
        U16(dataSetSize);                // 0x06
        U16(strings.Count);              // 0x08 string table size
        U16(shaderOffset);               // 0x0A shader package name offset
        b.Add((byte)textureCount);       // 0x0C
        b.Add(uvSetCount);               // 0x0D
        b.Add((byte)colorSetCount);      // 0x0E
        b.Add((byte)additionalSize);     // 0x0F

        foreach (var off in texOffsets) { U16(off); U16(0); }        // texture table
        for (int i = 0; i < uvSetCount; i++) { U16(0); U16(0); }     // uv-set table
        for (int i = 0; i < colorSetCount; i++) { U16(0); U16(0); }  // colour-set table

        b.AddRange(strings);
        b.AddRange(new byte[additionalSize]);
        b.AddRange(new byte[dataSetSize]);

        U16(0);                          // shader value list size
        U16(shaderKeyCount);
        U16(constantCount);
        U16(samplers.Length);
        U32(0);                          // flags — completes the 12-byte shader header
        b.AddRange(new byte[shaderKeyCount * 8]);
        b.AddRange(new byte[constantCount * 8]);

        for (int i = 0; i < samplers.Length; i++)
        {
            U32(samplers[i].SamplerId);
            U32(0);                      // sampler flags
            b.Add((byte)i);              // texture index — one texture per sampler, in order
            b.AddRange(new byte[3]);     // padding, completing the 12-byte stride
        }
        return b.ToArray();
    }

    [Fact]
    public void ParsesEverySlotFromASyntheticMaterial()
    {
        var mtrl = BuildMtrl([
            (Diffuse, "chara/x/tex/d.tex"),
            (Normal,  "chara/x/tex/n.tex"),
            (Mask,    "chara/x/tex/m.tex"),
            (Index,   "chara/x/tex/id.tex"),
        ]);

        var p = TextureLoader.ParseMtrlBytes(mtrl);

        Assert.Equal("chara/x/tex/d.tex",  p.Diffuse);
        Assert.Equal("chara/x/tex/n.tex",  p.Normal);
        Assert.Equal("chara/x/tex/m.tex",  p.Mask);
        Assert.Equal("chara/x/tex/id.tex", p.Index);
    }

    /// <summary>
    /// The tables between the string table and the samplers all shift the sampler array. Each of these
    /// would land the parser mid-garbage if its size were ignored, so vary them together and apart.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(2048, 0, 0, 0)]      // a colour-set-bearing material
    [InlineData(0, 16, 0, 0)]        // additional data present
    [InlineData(0, 0, 3, 7)]         // shader keys and constants present
    [InlineData(2048, 16, 3, 7)]     // everything at once
    public void OffsetsSurviveEveryIntermediateTable(int dataSet, int additional, int keys, int constants)
    {
        var mtrl = BuildMtrl(
            [(Normal, "chara/y/tex/n.tex"), (Mask, "chara/y/tex/m.tex"), (Index, "chara/y/tex/id.tex")],
            dataSetSize: dataSet, additionalSize: additional,
            shaderKeyCount: keys, constantCount: constants);

        var p = TextureLoader.ParseMtrlBytes(mtrl);

        Assert.Equal("chara/y/tex/n.tex",  p.Normal);
        Assert.Equal("chara/y/tex/m.tex",  p.Mask);
        Assert.Equal("chara/y/tex/id.tex", p.Index);
        Assert.Null(p.Diffuse);          // no diffuse sampler — the Dawntrail gear shape
    }

    /// <summary>Bibo+ and other custom body shaders name their base map ColorMap0, not Diffuse.</summary>
    [Fact]
    public void ColorMap0CountsAsDiffuse()
    {
        var p = TextureLoader.ParseMtrlBytes(BuildMtrl([(ColorMap0, "chara/z/tex/base.tex")]));
        Assert.Equal("chara/z/tex/base.tex", p.Diffuse);
    }

    /// <summary>
    /// Unrecognised samplers must be skipped without disturbing the ones around them — a material can
    /// carry maps Proteus has no concept of (characterscroll's _o, for one).
    /// </summary>
    [Fact]
    public void UnknownSamplersAreIgnored()
    {
        var p = TextureLoader.ParseMtrlBytes(BuildMtrl([
            (0xFEA0F3D2u, "chara/q/tex/o.tex"),     // characterscroll's colour/pattern map
            (Normal,      "chara/q/tex/n.tex"),
            (0xDEADBEEFu, "chara/q/tex/junk.tex"),
            (Mask,        "chara/q/tex/m.tex"),
        ]));

        Assert.Equal("chara/q/tex/n.tex", p.Normal);
        Assert.Equal("chara/q/tex/m.tex", p.Mask);
        Assert.Null(p.Diffuse);
        Assert.Null(p.Index);
    }

    /// <summary>
    /// Locks in a deliberate decision: the marker is stripped only when it leads the WHOLE path. Real
    /// files carry it on the file name instead (chara/.../texture/--c1401b0001_c_n.tex) and those are left
    /// verbatim, because these strings become the compositor's redirect keys — rewriting them changes
    /// which resource Penumbra is asked to replace. 48 of the installed body/face materials have the
    /// mid-path form and none have the leading form.
    /// </summary>
    [Fact]
    public void DashMarkerStrippedOnlyWhenItLeadsTheWholePath()
    {
        var p = TextureLoader.ParseMtrlBytes(BuildMtrl([
            (Normal, "--bare_n.tex"),
            (Mask,   "chara/w/texture/--c1401b0001_c_m.tex"),
        ]));

        Assert.Equal("bare_n.tex", p.Normal);                              // leading marker: stripped
        Assert.Equal("chara/w/texture/--c1401b0001_c_m.tex", p.Mask);       // mid-path: preserved
    }

    /// <summary>
    /// Malformed input must degrade to "nothing found". This runs on the ImGui draw thread and inside the
    /// composite, so a throw would take down the UI or abort a bake.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(15)]     // shorter than the 16-byte header
    [InlineData(24)]     // header present, tables truncated
    [InlineData(40)]
    public void TruncatedInputYieldsNothingAndDoesNotThrow(int keepBytes)
    {
        var full = BuildMtrl([(Normal, "chara/x/tex/n.tex"), (Mask, "chara/x/tex/m.tex")]);
        var cut = new byte[Math.Min(keepBytes, full.Length)];
        Array.Copy(full, cut, cut.Length);

        var p = TextureLoader.ParseMtrlBytes(cut);

        Assert.Null(p.Diffuse);
        Assert.Null(p.Normal);
        Assert.Null(p.Mask);
        Assert.Null(p.Index);
    }

    /// <summary>
    /// "This material names no index texture" and "Proteus could not read this material" must be separable.
    /// <para/>
    /// Both arrive as a null <c>Index</c>, because the parser is fail-open by design, and they mean opposite
    /// things to the colour editor: the first justifies pinning the grid to one colour-table row, the second
    /// justifies nothing at all. Only <see cref="MtrlTexturePaths.Parsed"/> tells them apart, and getting it
    /// wrong is expensive — the editor DISABLES every row it doesn't name, so a row claimed on no evidence
    /// puts the row that works out of reach.
    /// </summary>
    [Fact]
    public void AWalkThatBailedIsNotAMaterialWithoutAnIndex()
    {
        // Every bail reports Parsed false: too short for the header, and a texture table past the end.
        var full = BuildMtrl([(Normal, "chara/x/tex/n.tex")]);
        foreach (var keep in new[] { 0, 8, 15, 24 })
        {
            var cut = new byte[Math.Min(keep, full.Length)];
            Array.Copy(full, cut, cut.Length);
            var got = TextureLoader.ParseMtrlBytes(cut);
            Assert.False(got.Parsed);
            Assert.Null(got.Index);
        }

        // A material that walks all the way through says so, whether or not it found an index.
        Assert.True(TextureLoader.ParseMtrlBytes(full).Parsed);
        Assert.Null(TextureLoader.ParseMtrlBytes(full).Index);
        Assert.True(TextureLoader.ParseMtrlBytes(
            BuildMtrl([(Index, "chara/x/tex/id.tex")])).Parsed);
    }

    /// <summary>
    /// A material with no colour table is reported as having none, so nothing claims which of its rows is
    /// live — it has none, and <c>GearMaterialWriter.PatchColorTable</c> discards anything written to it.
    /// <para/>
    /// Both halves of the test matter independently: a declared colour set with a data set too small for the
    /// Dawntrail 32×64 rows is the legacy layout the writer refuses, and a data set big enough with no
    /// colour set declared is not a colour table either.
    /// </summary>
    [Fact]
    public void AColourTableIsOnlyReportedWhenTheRowsCouldActuallyBeThere()
    {
        (uint, string)[] one = [(Normal, "chara/x/tex/n.tex")];

        // Neither half alone is enough.
        Assert.False(TextureLoader.ParseMtrlBytes(BuildMtrl(one)).HasColorTable);
        Assert.False(TextureLoader.ParseMtrlBytes(
            BuildMtrl(one, dataSetSize: 2176)).HasColorTable);                       // no colour set declared
        Assert.False(TextureLoader.ParseMtrlBytes(
            BuildMtrl(one, dataSetSize: 544, colorSetCount: 1)).HasColorTable);      // legacy 16-row table

        // A declared colour set with room for the 32×64 rows is one, with or without the dye table.
        Assert.True(TextureLoader.ParseMtrlBytes(
            BuildMtrl(one, dataSetSize: 2048, colorSetCount: 1)).HasColorTable);
        Assert.True(TextureLoader.ParseMtrlBytes(
            BuildMtrl(one, dataSetSize: 2176, colorSetCount: 1)).HasColorTable);

        // A file that never finished parsing claims nothing either way.
        Assert.False(TextureLoader.ParseMtrlBytes(new byte[8]).HasColorTable);
    }

    /// <summary>
    /// Fuzz: no input may throw. The bounds checks are the only thing between a corrupt or truncated .mtrl
    /// and an exception on the ImGui draw thread or mid-composite, so this exercises them across many
    /// shapes rather than one sample.
    /// <para/>
    /// Half the cases get a valid version stamp and a length long enough to clear the header check, so the
    /// walk reaches the table-offset and sampler-table guards deeper in — pure noise usually bails at the
    /// first length test and never reaches them. The counts and sizes those guards act on (texture count,
    /// string-table size, shader-key/constant/sampler counts) are random bytes either way, which is exactly
    /// the hostile input wanted. Output is deliberately unasserted: garbage in, anything out is fine as
    /// long as it is not an exception.
    /// </summary>
    [Fact]
    public void NoInputCanThrow()
    {
        var rng = new Random(20260728);
        for (int i = 0; i < 1000; i++)
        {
            var junk = new byte[i % 2 == 0 ? rng.Next(16, 4096) : rng.Next(0, 64)];
            rng.NextBytes(junk);
            if (i % 2 == 0 && junk.Length >= 4)
                BitConverter.TryWriteBytes(junk.AsSpan(0), 0x01030000u);   // plausible version

            var ex = Record.Exception(() => TextureLoader.ParseMtrlBytes(junk));
            Assert.Null(ex);
        }
    }

    /// <summary>
    /// Conformance against a REAL material, so the suite isn't just the byte builder above agreeing with
    /// the parser — if both encoded the same wrong idea of the format, every other test here would still
    /// pass while production read garbage.
    /// <para/>
    /// <c>Fixtures/gear_top_a.mtrl</c> is a shipped gear material (characterlegacy.shpk) with its colour
    /// table zeroed; every count, offset and table the parser walks is byte-for-byte as the game has it.
    /// Notably it uses <c>uvSetCount=2</c>, <c>colorSetCount=1</c> and <c>additionalDataSize=4</c> — a
    /// combination <see cref="BuildMtrl"/> never produces, so a parser that mishandled any of those three
    /// would pass the synthetic tests and fail here. The expected paths were recovered independently, by a
    /// script that shares no code with <see cref="TextureLoader.ParseMtrlBytes"/>.
    /// </summary>
    [Fact]
    public void MatchesARealMaterialFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gear_top_a.mtrl");
        Assert.True(File.Exists(path), $"fixture missing: {path}");

        var p = TextureLoader.ParseMtrlBytes(File.ReadAllBytes(path));

        Assert.Equal("chara/equipment/e0051/texture/v01_c0201e0051_top_n.tex",  p.Normal);
        Assert.Equal("chara/equipment/e0051/texture/v01_c0201e0051_top_m.tex",  p.Mask);
        Assert.Equal("chara/equipment/e0051/texture/v01_c0201e0051_top_id.tex", p.Index);
        Assert.Null(p.Diffuse);   // Dawntrail gear carries no diffuse — colour comes from the colour table
    }

    /// <summary>
    /// Pins the divergence that justifies this parser existing at all, by asserting BOTH sides rather than
    /// comparing them.
    /// <para/>
    /// Measured on real files — <c>mt_c0201h0173_hir_b_c0201.mtrl</c> (hair.shpk) and
    /// <c>mt_c0201e0051_top_a.mtrl</c> (characterlegacy.shpk) — Lumina loads without throwing and reads the
    /// texture table correctly, then reports <b>zero samplers</b>. Any classifier built on it therefore has
    /// nothing to work with and yields all-null, while <see cref="TextureLoader.ParseMtrlBytes"/> recovers
    /// every slot from the same bytes. That is the bug behind "couldn't read this material" on everything
    /// except modded body and face.
    /// <para/>
    /// The check is made against Lumina's own <c>Samplers</c>/<c>TextureOffsets</c> rather than a typed
    /// parser in <c>TextureLoader</c>: keeping one there purely for this test meant production carried a
    /// method nothing called, which the next dead-code sweep would rightly delete — taking the canary with
    /// it. Nothing production-side is needed to state the fact.
    /// <para/>
    /// An earlier version compared the two parsers and skipped when Lumina came back empty — which is
    /// always, so it passed without executing a single assertion. Asserting each side's measured behaviour
    /// instead means it cannot go vacuous.
    /// <para/>
    /// <b>If the Lumina assertions here start failing, that is good news, not a defect:</b> the typed reader
    /// has been fixed, and whether the raw parser is still needed should be re-evaluated.
    /// </summary>
    [Fact]
    public void RawParserRecoversWhatTheTypedReaderCannot()
    {
        var mtrl = BuildMtrl([
            (Diffuse, "chara/x/tex/d.tex"),
            (Normal,  "chara/x/tex/n.tex"),
            (Mask,    "chara/x/tex/m.tex"),
            (Index,   "chara/x/tex/id.tex"),
        ], dataSetSize: 2048, shaderKeyCount: 2, constantCount: 5);

        // The raw parser gets all four, through a 2 KB data set and populated key/constant tables.
        var mine = TextureLoader.ParseMtrlBytes(mtrl);
        Assert.Equal("chara/x/tex/d.tex",  mine.Diffuse);
        Assert.Equal("chara/x/tex/n.tex",  mine.Normal);
        Assert.Equal("chara/x/tex/m.tex",  mine.Mask);
        Assert.Equal("chara/x/tex/id.tex", mine.Index);

        var lumina = TextureLoader.LoadLuminaFileFromBytes<MtrlFile>(mtrl);
        Assert.NotNull(lumina);
        // It reads the texture table — so this is not a "malformed file" excuse …
        Assert.Equal(4, lumina.TextureOffsets.Length);
        // … it simply finds no samplers, so there is nothing for any classifier to map to a slot.
        Assert.Empty(lumina.Samplers);
    }
}
