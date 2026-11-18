using System;
using System.Linq;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>The glass → character.shpk graft and its gate, on synthetic bytes (Sensitive binary writer: quick suite).</summary>
public class MatcapGlassCutTests
{
    private static (byte[] Glass, byte[] Template, byte[] Converted) Make(byte[]? glass = null, byte[]? template = null)
    {
        glass ??= GlassKit.Glass();
        template ??= GlassKit.Template();
        var (bytes, why) = MatcapGlassCut.Convert(glass, template);
        Assert.Equal(MatcapGlassCut.Failure.None, why);
        return (glass, template, bytes!);
    }

    private static uint Add(byte[] b) { var l = MatcapGlassCut.Walk(b)!; return BitConverter.ToUInt32(b, l.AddAt); }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-1")]
    public void Convert_is_deterministic()
    {
        var (g, t, a) = Make();
        var (b, _) = MatcapGlassCut.Convert(g, t);
        Assert.Equal(a, b);
    }

    public static TheoryData<string> Tampers() =>
        new() { "file-size", "trailing", "constant-past-block", "sampler-count", "texture-index", "value-size-not-4",
                "texture-string", "sampler-flag", "table-byte", "template-constant", "movement", "threshold", "extra-key" };

    [Theory]
    [Trait("Plan", "EDGE-row-matcap-glass-6")]
    [Trait("Plan", "EDGE-row-matcap-glass-15")]
    [MemberData(nameof(Tampers))]
    public void Gate_rejects_tampered_copies(string tamper)
    {
        var (g, t, c) = Make();
        Assert.True(MatcapGlassCut.IsSound(g, c, t));
        var b = (byte[])c.Clone();
        var l = MatcapGlassCut.Walk(b)!;
        int ConstAt(uint id) => Enumerable.Range(0, l.ConstCount).Select(i => l.ConstsAt + 8 * i).First(o => BitConverter.ToUInt32(b, o) == id);
        int ValueAt(uint id) => l.ValuesAt + BitConverter.ToUInt16(b, ConstAt(id) + 4);
        switch (tamper)
        {
            case "file-size": BitConverter.TryWriteBytes(b.AsSpan(4), (ushort)(b.Length - 4)); break;
            case "trailing": b = [.. b, 0, 0, 0, 0]; BitConverter.TryWriteBytes(b.AsSpan(4), (ushort)b.Length); break;
            case "constant-past-block": BitConverter.TryWriteBytes(b.AsSpan(l.ConstsAt + 4), (ushort)l.ValueSize); break;
            case "sampler-count": BitConverter.TryWriteBytes(b.AsSpan(l.ShaderAt + 6), (ushort)(l.SampCount - 1)); break;
            case "texture-index": b[l.SampsAt + 8] = (byte)l.Tex; break;
            case "value-size-not-4": BitConverter.TryWriteBytes(b.AsSpan(l.ShaderAt), (ushort)(l.ValueSize - 2)); break;
            case "texture-string": b[l.StrAt] ^= 0x20; break;
            case "sampler-flag": b[l.SampsAt + 4] ^= 0x01; break;
            case "table-byte": b[l.AddAt + 4 + 10] ^= 0x01; break;
            case "template-constant": BitConverter.TryWriteBytes(b.AsSpan(ValueAt(0xD62BF368)), 3f); break;
            case "movement": BitConverter.TryWriteBytes(b.AsSpan(ValueAt(MeshVolumeService.VertexMovementScaleId)), 1f); break;
            case "threshold": BitConverter.TryWriteBytes(b.AsSpan(ValueAt(MatcapMaterial.ConstAlphaThreshold)), 0.3f); break;
            case "extra-key": b = TextureLoader.EnsureShaderKey(b, 0xB616DC5A, 0x5CC605B5); break;
        }
        Assert.False(MatcapGlassCut.IsSound(g, b, t));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-6")]
    public void Second_gate_checks_the_retargeted_normal()
    {
        var (g, t, c) = Make();
        var h = MatcapMaterial.ReadHeader(c)!;
        int ni = h.Textures.ToList().FindIndex(x => x == h.TextureOf(MatcapMaterial.SamplerNormal));
        var cut = MatcapMaterial.RetargetByIndex(c, new System.Collections.Generic.Dictionary<int, string> { [ni] = "chara/x/cut1.tex" })!;
        Assert.True(MatcapGlassCut.IsSound(g, cut, t, "chara/x/cut1.tex"));
        Assert.False(MatcapGlassCut.IsSound(g, cut, t, "chara/x/wrong.tex"));   // not the bake it names
        Assert.False(MatcapGlassCut.IsSound(g, c, t, "chara/x/cut1.tex"));      // the normal was never retargeted
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-8")]
    public void Keeps_source_sampler_flags_and_uv_sets()
    {
        var (g, _, c) = Make();
        var gl = MatcapGlassCut.Walk(g)!;
        var cl = MatcapGlassCut.Walk(c)!;
        Assert.Equal(g.AsSpan(gl.SampsAt, 12 * gl.SampCount).ToArray(), c.AsSpan(cl.SampsAt, 12 * cl.SampCount).ToArray());
        Assert.Equal(GlassKit.NormalFlags, BitConverter.ToUInt32(c, cl.SampsAt + 4));   // not the template's 0xF8355
        Assert.Equal(gl.Uv, cl.Uv);
    }

    [Theory]
    [Trait("Plan", "EDGE-row-matcap-glass-9")]
    [InlineData(0x534u, 0x1C53Du, 0x53Cu)]                     // the real pair → the working _a's value
    [InlineData(0x1C535u, 0x53Cu, 0x1C53Du)]                   // non-table bits (0x1, 0x4000, 0x10000, 0x8000) follow the template
    [InlineData(0x534u, 0x53Cu, 0x53Cu)]                       // the dye bit follows the source when the template lacks it
    [InlineData(0x53Cu, 0x534u, 0x534u)]                       // …and when the template has it
    public void Additional_data_rule(uint template, uint source, uint expected)
    {
        var (_, _, c) = Make(GlassKit.Glass(additional: source), GlassKit.Template(additional: template));
        Assert.Equal(expected, Add(c));
    }

    [Theory]
    [Trait("Plan", "EDGE-row-matcap-glass-9")]
    [InlineData(0)]
    [InlineData(128)]
    public void Dye_table_follows_source(int dye)
    {
        var (g, _, c) = Make(GlassKit.Glass(dyeBytes: dye));
        var gl = MatcapGlassCut.Walk(g)!;
        var cl = MatcapGlassCut.Walk(c)!;
        Assert.Equal(2048 + dye, cl.DataSet);
        Assert.Equal(g.AsSpan(gl.AddAt + 4, gl.DataSet).ToArray(), c.AsSpan(cl.AddAt + 4, cl.DataSet).ToArray());
    }

    [Theory]
    [Trait("Plan", "EDGE-row-matcap-glass-10")]
    [InlineData(0x11u, 0x1u)]
    [InlineData(0x10u, 0x0u)]
    public void Wind_neutral_and_parameters(uint sourceFlags, uint expectedFlags)
    {
        var (_, t, c) = Make(GlassKit.Glass(flags: sourceFlags));
        var cl = MatcapGlassCut.Walk(c)!;
        Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(c));
        Assert.Equal(expectedFlags, cl.Flags);
        Assert.Equal(2, cl.KeyCount);
        Assert.Equal(MatcapGlassCut.VertexColourKey, BitConverter.ToUInt32(c, cl.KeysAt));
        Assert.Equal(MatcapGlassCut.VertexColourMask, BitConverter.ToUInt32(c, cl.KeysAt + 4));
        Assert.Equal(CharacterShaderUpgrade.FlowMapKey, BitConverter.ToUInt32(c, cl.KeysAt + 8));
        Assert.Equal(0f, TextureLoader.ConstantValue(c, MeshVolumeService.VertexMovementScaleId));
        Assert.Equal(0f, TextureLoader.ConstantValue(c, MeshVolumeService.VertexMovementMaxLengthId));
        Assert.Equal(0.5f, TextureLoader.ConstantValue(c, MatcapMaterial.ConstAlphaThreshold));
        Assert.Equal(TextureLoader.GetMtrlInfo(t).constIds.Order(), TextureLoader.GetMtrlInfo(c).constIds.Order());
    }
}
