using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;
using Xunit;

namespace Proteus.Tests;

/// <summary>Synthetic glass and template materials for the glass row cut (shaped like the real <c>_b</c> and a0170 <c>_ear_a</c>).</summary>
internal static class GlassKit
{
    public const uint NormalFlags = 0xF8000, OtherFlags = 0xF8340;

    /// <summary>A characterglass material bound to N/M/ID on three textures, Dawntrail table, 4-byte additional data.</summary>
    public static byte[] Glass(float? threshold = null, int tableBytes = 2048, int dyeBytes = 0, uint additional = 0x1C53D,
                               int additionalSize = 4, uint flags = 0x11, string normal = RowFixture.NormalPath,
                               string index = RowFixture.IndexPath, string? layout = null, byte tint = 0,
                               string mask = "chara/x/glass_m.tex")
    {
        var textures = new List<string> { normal, mask, index };
        var samplers = new List<(uint, int)> { (MatcapMaterial.SamplerNormal, 0), (MatcapMaterial.SamplerMask, 1), (ShaderUpgradePlan.SamplerIndex, 2) };
        var sflags = new List<uint> { NormalFlags, OtherFlags, OtherFlags };
        switch (layout)
        {
            case "no-index": samplers.RemoveAt(2); sflags.RemoveAt(2); textures.RemoveAt(2); break;
            case "no-mask": samplers[1] = (ShaderUpgradePlan.SamplerIndex, 2); samplers.RemoveAt(2); sflags.RemoveAt(2); break;
            case "extra-diffuse": textures.Add("chara/x/glass_d.tex"); samplers.Add((ShaderUpgradePlan.SamplerDiffuse, 3)); sflags.Add(OtherFlags); break;
            case "shared-texture": samplers[2] = (ShaderUpgradePlan.SamplerIndex, 1); break;
        }
        var consts = new List<(uint, float[])> { (0x59BDA0B1, [tint]), (0x2D9C3AA6, [0.3f]), (0x36080AD0, [1f]) };
        if (threshold is { } t) consts.Add((MatcapMaterial.ConstAlphaThreshold, [t]));
        return SyntheticMatcapMod.Material(MatcapGlassCut.GlassShader, [.. textures], [.. samplers], [.. consts],
            colorTableBytes: tableBytes, additional: additional, shaderFlags: flags, keys: [(0xE8DA5B62, 0x7B804D6E)],
            samplerFlags: [.. sflags], dyeBytes: dyeBytes, additionalSize: additionalSize);
    }

    /// <summary>A character.shpk N/M/ID template shaped like a0170 <c>_ear_a</c>.</summary>
    public static byte[] Template(string shader = "character.shpk", bool threshold = true, bool diffuse = false,
                                  uint additional = 0x534, int additionalSize = 4, uint flags = 0x1)
    {
        var consts = new List<(uint, float[])>
        {
            (0xB7FA33E2, [1f]), (0xAD94E254, [0f]), (0x39551220, [0f]), (0x5351646E, [0f]), (0x6421DD30, [0f]),
            (0xB5545FBB, [1f]), (MeshVolumeService.VertexMovementScaleId, [0f]), (MeshVolumeService.VertexMovementMaxLengthId, [0f]),
            (0xD62BF368, [2f]), (0xD07A6A65, [0f]), (0xB61D7498, [0f]), (0xD925FF32, [0.5f]),
        };
        if (threshold) consts.Insert(0, (MatcapMaterial.ConstAlphaThreshold, [0.5f]));
        var textures = new List<string> { "chara/t/n.tex", "chara/t/m.tex", "chara/t/id.tex" };
        var samplers = new List<(uint, int)> { (MatcapMaterial.SamplerNormal, 0), (MatcapMaterial.SamplerMask, 1), (ShaderUpgradePlan.SamplerIndex, 2) };
        if (diffuse) { textures.Add("chara/t/d.tex"); samplers.Add((ShaderUpgradePlan.SamplerDiffuse, 3)); }
        return SyntheticMatcapMod.Material(shader, [.. textures], [.. samplers], [.. consts], colorTableBytes: 2048,
            additional: additional, shaderFlags: flags, additionalSize: additionalSize,
            keys: [(0xB616DC5A, 0x5CC605B5), (0x40D1481E, 0x337C6BC4), (0xD2777173, 0x4242B842), (0xF52CCF05, 0xA7D2FF60)],
            samplerFlags: [0xF8355, 0xF8340, 0xF8340]);
    }

    /// <summary>A row-matcap mod whose part is glass (RowFixture's model and maps), the template served by the game.</summary>
    public static RowFixture Fixture(byte[]? glass = null, byte[]? template = null, bool withTemplate = true)
    {
        var f = new RowFixture();
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"), glass ?? Glass());
        if (withTemplate) f.Game[MatcapGlassCut.TemplatePath] = template ?? Template();
        return f;
    }
}

/// <summary>
/// Row matcaps on glass parts (Sensitive writers: MatcapWriter and the glass graft — quick suite): the cut copy is moved
/// to character.shpk, the author's glass is untouched, and the matcap's Undo puts the mod back.
/// </summary>
public class MatcapRowGlassTests
{
    private static List<PenumbraModMeta.ContainerRedirect> Redirects(string mod) => PenumbraModMeta.ReadContainerRedirects(mod);

    [Fact]
    [Trait("Plan", "AC-row-matcap-glass-1")]
    public void Glass_row_applies_and_cuts()
    {
        using var f = GlassKit.Fixture();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok);
        var row = MatcapRecord.Read(f.ModRoot).Materials.Single().Row!;
        var cut = File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, row.Files.Single().File));
        Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(cut));
        var h = MatcapMaterial.ReadHeader(cut)!;
        Assert.Equal(row.Normals.Single().GamePath, h.TextureOf(MatcapMaterial.SamplerNormal));
        Assert.Equal(0.5f, h.AlphaThreshold);
        var n = f.Loader.LoadTexBytesAsRgba(File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, row.Normals.Single().File)), "cut")!.Value;
        for (int y = 0; y < n.height; y += 7)
            for (int x = 0; x < n.width; x += 5)
                Assert.Equal(x < n.width / 2 ? 0 : 255, n.rgba[(y * n.width + x) * 4 + 2]);
        Assert.Contains(ModelPartReader.Read(f.Current())!.Parts, x => x.Material == row.Cut);
    }

    [Fact]
    [Trait("Plan", "AC-row-matcap-glass-2")]
    public void Author_glass_files_untouched_rest_stays_glass()
    {
        using var f = GlassKit.Fixture();
        var before = new[] { "mat/top_a.mtrl", "tex/n.tex", "tex/id.tex" }.ToDictionary(r => r, r => File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, r)));
        Assert.True(f.Apply().Ok);
        foreach (var (rel, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(MatcapWriter.Full(f.ModRoot, rel)));
        Assert.Equal(MatcapGlassCut.GlassShader, CharacterShaderUpgrade.ShaderOf(before["mat/top_a.mtrl"]));
        Assert.Contains(ModelPartReader.Read(f.Current())!.Parts, x => x.Material == RowFixture.Mat && x.Island < 0);
    }

    [Fact]
    [Trait("Plan", "AC-row-matcap-glass-3")]
    [Trait("Plan", "AC-row-matcap-glass-5")]
    public void Prepared_and_preview_flag_glass_with_count_and_preview_writes_nothing()
    {
        using var f = GlassKit.Fixture();
        var before = f.Snapshot();
        var p = f.Prepare();
        Assert.True(p.RowGlass);
        Assert.Equal(2, p.RowGlassTriangles);   // the two left (row 15 B) triangles
        var built = MatcapPreview.Build(new MatcapPreview.Input(f.ModRoot, RowFixture.ModelRel, RowFixture.ModelPath, [RowFixture.ModelPath],
            f.Current(), f.Pool(), MatcapChoice.OfImage(f.Image()), f.Env() with { TempDir = Path.Combine(f.Temp, "preview") }, f.Mods));
        Assert.True(built.Ok, built.Code + ": " + built.Message);
        Assert.True(built.RowGlass);
        Assert.Equal(2, built.RowGlassTriangles);
        // The preview pushes the converted cut at the cut's own path, and nothing at the author's material path.
        var cutName = built.Files.SelectMany(x => x.GamePaths).First(gp => gp.Contains("_mcut") && gp.EndsWith(".mtrl"));
        var cutFile = built.Files.First(x => x.GamePaths.Contains(cutName));
        Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(cutFile.Bytes));
        Assert.DoesNotContain(built.Files.SelectMany(x => x.GamePaths), gp => PenumbraModMeta.SameFile(gp, RowFixture.MatPath()));
        Assert.Equal(before, f.Snapshot());       // neither Prepare nor the preview writes into the mod

        using var plain = new RowFixture();
        var q = plain.Prepare();
        Assert.False(q.RowGlass);
        Assert.Equal(0, q.RowGlassTriangles);
    }

    [Fact]
    [Trait("Plan", "AC-row-matcap-glass-3")]
    public void Report_adds_glass_line()
    {
        var with = MatcapPanelModel.Report(new MatcapPanelModel.JobReport("x", true, 1, false, [], false, false, [], RowGlass: true, RowGlassTriangles: 2));
        Assert.Single(with.Chat, l => l == string.Format(Strings.Matcap.ChatGlassFmt, 2));
        var without = MatcapPanelModel.Report(new MatcapPanelModel.JobReport("x", true, 1, false, [], false, false, []));
        Assert.DoesNotContain(without.Chat, l => l == string.Format(Strings.Matcap.ChatGlassFmt, 2));
        Assert.Contains("2", string.Format(Strings.Matcap.RowGlassWarnFmt, Strings.Matcap.UndoLabel, 2));
    }

    [Fact]
    [Trait("Plan", "AC-row-matcap-glass-4")]
    public void Undo_restores_mod_byte_identical()
    {
        using var f = GlassKit.Fixture();
        var before = f.Snapshot();
        Assert.True(f.Apply().Ok);
        Assert.True(MatcapWriter.Undo(f.ModRoot, RowFixture.ModelRel).Ok);
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [Trait("Plan", "AC-row-matcap-glass-6")]
    [Trait("Plan", "EDGE-row-matcap-glass-4")]
    [InlineData("no-index")]
    [InlineData("extra-diffuse")]
    [InlineData("no-mask")]
    [InlineData("table-512")]
    [InlineData("no-table")]
    [InlineData("shared-texture")]
    [InlineData("add-0")]
    [InlineData("add-8")]
    public void Unconvertible_glass_layouts_refuse(string layout)
    {
        var glass = layout switch
        {
            "table-512" => GlassKit.Glass(tableBytes: 512, additional: 0x4),
            "no-table" => GlassKit.Glass(tableBytes: 0, additional: 0),
            "add-0" => GlassKit.Glass(additionalSize: 0),
            "add-8" => GlassKit.Glass(additionalSize: 8),
            _ => GlassKit.Glass(layout: layout),
        };
        using var f = GlassKit.Fixture(glass);
        var before = f.Snapshot();
        var p = f.Prepare();
        Assert.False(p.Ok);
        Assert.Equal(MatcapRowCut.CodeGlassLayout, p.Code);
        Assert.Contains(MatcapGlassCut.GlassShader, p.Message);
        Assert.True(char.IsUpper(p.Message[0]), p.Message);
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [Trait("Plan", "AC-row-matcap-glass-6")]
    [Trait("Plan", "EDGE-row-matcap-glass-5")]
    [InlineData("none")]
    [InlineData("glass")]
    [InlineData("diffuse")]
    [InlineData("no-threshold")]
    [InlineData("add-8")]
    [InlineData("constant-past-block")]
    public void Missing_or_wrong_template_refuses(string kind)
    {
        byte[]? template = kind switch
        {
            "glass" => GlassKit.Template(shader: MatcapGlassCut.GlassShader),
            "diffuse" => GlassKit.Template(diffuse: true),
            "no-threshold" => GlassKit.Template(threshold: false),
            "add-8" => GlassKit.Template(additionalSize: 8),
            "constant-past-block" => PastBlock(GlassKit.Template()),
            _ => null,
        };
        static byte[] PastBlock(byte[] t)
        {
            var l = MatcapGlassCut.Walk(t)!;
            BitConverter.TryWriteBytes(t.AsSpan(l.ConstsAt + 6), (ushort)400);
            return t;
        }
        using var f = GlassKit.Fixture(template: template, withTemplate: template != null);
        var before = f.Snapshot();
        var p = f.Prepare();
        Assert.Equal(MatcapRowCut.CodeGlassTemplate, p.Code);
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [Trait("Plan", "AC-row-matcap-glass-7")]
    [Trait("Plan", "EDGE-row-matcap-glass-14")]
    [InlineData("hair.shpk")]
    [InlineData("characterstockings.shpk")]
    public void Other_shaders_still_refuse(string shader)
    {
        using var f = new RowFixture();
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"), RowFixture.Material(shader: shader));
        var p = f.Prepare();
        Assert.Equal(MatcapRowCut.CodeShader, p.Code);
        Assert.Equal(string.Format(ProteusLoc.Localize("Matcap.Refused.RowShader.Fmt", "A row matcap needs the part on character.shpk, and {0} uses {1}."),
                                   MatcapRowCut.Describe(new PenumbraModMeta.ContainerRef("")), shader), p.Message);
    }

    [Fact]
    [Trait("Plan", "AC-row-matcap-glass-8")]
    public void Glass_texts_in_every_language()
    {
        foreach (var lang in new[] { "en", "de", "es", "fr", "ja", "ko", "ru", "zh" })
        {
            using var s = typeof(Proteus.Plugin).Assembly.GetManifestResourceStream($"Proteus.Localization.{lang}.json")!;
            var json = JsonNode.Parse(s)!.AsObject();
            string M(string key) => json[key]?["message"]?.GetValue<string>() ?? throw new Xunit.Sdk.XunitException($"{lang}: {key} missing");
            Assert.Contains("{0}", M("Matcap.Refused.RowGlassLayout.Fmt"));
            Assert.Contains("{1}", M("Matcap.Refused.RowGlassLayout.Fmt"));
            Assert.Contains("character.shpk", M("Matcap.Refused.RowGlassLayout.Fmt"));
            Assert.False(string.IsNullOrWhiteSpace(M("Matcap.Refused.RowGlassTemplate")));
            Assert.Contains("{0}", M("Parts.Matcap.RowGlassWarn.Fmt"));
            Assert.Contains("{1}", M("Parts.Matcap.RowGlassWarn.Fmt"));
            Assert.Contains("{0}", M("Parts.Matcap.Chat.Glass.Fmt"));
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-1")]
    public void Identical_glass_options_share_one_cut()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        var glass = GlassKit.Glass();
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/red.mtrl"), glass);
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/blue.mtrl"), glass);
        f.Game[MatcapGlassCut.TemplatePath] = GlassKit.Template();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        var file = Assert.Single(p.Request!.Materials.Single().Row!.Cut.Files);
        Assert.Equal(["Blue", "Red"], file.Redirects.Select(r => r.Container.Option!).Order());
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-2")]
    public void Mixed_glass_and_character_options()
    {
        using var f = new RowFixture(materialInOption: true);
        f.ColourOptions();
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/red.mtrl"), GlassKit.Glass());
        f.Game[MatcapGlassCut.TemplatePath] = GlassKit.Template();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        var files = p.Request!.Materials.Single().Row!.Cut.Files;
        Assert.Equal(2, files.Count);
        Assert.All(files, x => Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(x.Bytes)));
        Assert.All(files, x => Assert.True(x.ThresholdZero));
        Assert.True(MatcapWriter.Apply(f.ModRoot, p.Request!).Ok);
    }

    [Theory]
    [Trait("Plan", "EDGE-row-matcap-glass-3")]
    [InlineData(null)]
    [InlineData(0.3f)]
    public void Glass_threshold_class_is_zero_before_reread(float? threshold)
    {
        using var f = new RowFixture(normal: null);
        // Off the row the authored blue is 40 (glass opacity, far under 128): the cut must still write 255 there.
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "tex/n.tex"), f.Tex(RowFixture.NormalRgba(blue: 40)));
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"), GlassKit.Glass(threshold: threshold));
        f.Game[MatcapGlassCut.TemplatePath] = GlassKit.Template();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        Assert.False(p.RowHoles);
        var cut = p.Request!.Materials.Single().Row!.Cut;
        Assert.True(cut.Files.Single().ThresholdZero);
        var bake = cut.Bakes.Single();
        var n = f.Loader.LoadTexBytesAsRgba(File.ReadAllBytes(bake.Temp), "bake")!.Value;
        Assert.Equal(255, n.rgba[(10 * n.width + n.width - 3) * 4 + 2]);
        Assert.Equal(0.5f, MatcapMaterial.ReadHeader(cut.Files.Single().Bytes)!.AlphaThreshold);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-6")]
    public void Unsound_conversion_refuses_as_row_bake()
    {
        // A valid glass so large that the converted copy would pass the format's 16-bit file size: the conversion is
        // unsound (never written), and the refusal is the plain "couldn't be written" one.
        int baseLength = GlassKit.Glass().Length;
        var glass = GlassKit.Glass(mask: "chara/x/" + new string('m', 65_500 - baseLength) + ".tex");
        Assert.InRange(glass.Length, 65_400, ushort.MaxValue);
        Assert.Equal(MatcapGlassCut.Failure.Unsound, MatcapGlassCut.Convert(glass, GlassKit.Template()).Why);
        using var f = GlassKit.Fixture(glass);
        var before = f.Snapshot();
        var p = f.Prepare();
        Assert.Equal(MatcapRowCut.CodeBake, p.Code);
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-7")]
    public void Low_vertex_alpha_glass_raises_cut_alpha()
    {
        // The fixture's model has no colour element (alpha reads 1): this one carries vertex alpha 0.
        using var f = new RowFixture(model: MatcapRowWriterTests.ColourModel());
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"), GlassKit.Glass());
        f.Game[MatcapGlassCut.TemplatePath] = GlassKit.Template();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        Assert.True(p.CutAlphaSet);
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-11")]
    public void Edited_glass_source_reads_stale()
    {
        using var f = GlassKit.Fixture();
        Assert.True(f.Apply().Ok);
        File.WriteAllBytes(MatcapWriter.Full(f.ModRoot, "mat/top_a.mtrl"), GlassKit.Glass(tint: 9));
        var entry = MatcapRecord.Read(f.ModRoot).Materials.Single();
        Assert.Equal("container", MatcapRowCut.Stale(f.ModRoot, entry, MatcapPlan.TargetOf(RowFixture.ModelPath, out _)!, f.Game.GetValueOrDefault));
    }

    [Fact]
    [Trait("Plan", "EDGE-row-matcap-glass-12")]
    public void Game_served_glass_cut_in_default_data()
    {
        using var f = new RowFixture(materialInOption: true);   // the mod doesn't serve the material: the game does
        f.Game[RowFixture.MatPath()] = GlassKit.Glass();
        f.Game[MatcapGlassCut.TemplatePath] = GlassKit.Template();
        var p = f.Prepare();
        Assert.True(p.Ok, p.Code + ": " + p.Message);
        var file = p.Request!.Materials.Single().Row!.Cut.Files.Single();
        Assert.True(file.Source.Game);
        Assert.Equal(CharacterShaderUpgrade.Character, CharacterShaderUpgrade.ShaderOf(file.Bytes));
        Assert.All(file.Redirects, r => Assert.True(r.Container.IsDefault));
    }
}
