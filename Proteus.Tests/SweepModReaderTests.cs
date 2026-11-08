using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// What the design-restore sweep reads from mods on disk, on a background thread: every redirect of every option
/// (Combining containers included), a material's texture paths and a model's material names — and never a failed read
/// mistaken for an empty mod, nor cached.
/// </summary>
public sealed class SweepModReaderTests : IDisposable
{
    private readonly string mods = Path.Combine(Path.GetTempPath(), "proteus-sweepreader-" + Guid.NewGuid().ToString("N"));

    public SweepModReaderTests() => Directory.CreateDirectory(mods);

    public void Dispose()
    {
        try { Directory.Delete(mods, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A minimal .mtrl naming <paramref name="textures"/>, enough for <c>ShaderUpgradePlan.TexturePaths</c>.</summary>
    internal static byte[] Mtrl(params string[] textures)
    {
        var strings = new MemoryStream();
        var offsets = new List<ushort>();
        foreach (var t in textures)
        {
            offsets.Add((ushort)strings.Position);
            strings.Write(Encoding.UTF8.GetBytes(t));
            strings.WriteByte(0);
        }
        var block = strings.ToArray();
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        w.Write(0x01030000u);              // version
        w.Write((ushort)0);                // file size (unused here)
        w.Write((ushort)0);                // data set size
        w.Write((ushort)block.Length);     // string table size
        w.Write((ushort)0);                // shader name offset
        w.Write((byte)textures.Length);    // texture count
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        foreach (var o in offsets) { w.Write(o); w.Write((ushort)0); }
        w.Write(block);
        return m.ToArray();
    }

    private string Mod(string name, string manifest, params (string Rel, byte[] Bytes)[] files)
    {
        var root = Path.Combine(mods, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), manifest);
        foreach (var (rel, bytes) in files)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        return root;
    }

    private const string Body = """
        {
          "FileVersion": 4, "Name": "Body",
          "DefaultData": { "Files": {
            "chara/equipment/e0000/model/c0201e0000_top.mdl": "top.mdl",
            "chara/human/c0201/obj/body/b0001/texture/skin_d.tex": "skin_d.tex" } },
          "Groups": [
            { "Name": "Skin Materials", "Type": "Single", "Options": [
              { "Name": "Off", "Files": {} },
              { "Name": "Matte", "Files": { "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl": "matte.mtrl" } } ] },
            { "Name": "Legs", "Type": "Combining", "Options": [ { "Name": "A" }, { "Name": "B" } ],
              "Containers": [ { "Files": {} }, { "Files": { "chara/equipment/e0000/model/c0201e0000_dwn.mdl": "dwn.mdl" } } ] }
          ]
        }
        """;

    private string BodyMod(byte[]? matte = null, byte[]? top = null)
        => Mod("Body", Body,
            ("top.mdl", top ?? SyntheticModel.PackPiece()),
            ("skin_d.tex", [1, 2, 3]),
            ("matte.mtrl", matte ?? Mtrl("--chara/bibo_mid_base.tex", "chara/bibo_mid_norm.tex")),
            ("dwn.mdl", SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201b0001_bibo.mtrl"))));

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-25")]
    [Trait("Plan", "EDGE-sweep-keeps-deps-34")]
    public void Files_EveryOptionAndContainer_WithReferences()
    {
        BodyMod();
        var files = new SweepModReader().Files(mods, "Body");
        Assert.False(files.ManifestFailed);
        Assert.Equal(4, files.Files.Count);

        var matte = files.Files.Single(f => f.GamePath.EndsWith("_bibo.mtrl"));
        Assert.False(matte.DefaultData);
        Assert.Equal(["chara/bibo_mid_base.tex", "chara/bibo_mid_norm.tex"], matte.Refs);   // the "--" marker stripped
        Assert.EndsWith("/Body/matte.mtrl", matte.FullPath);
        Assert.DoesNotContain('\\', matte.FullPath);

        var top = files.Files.Single(f => f.GamePath.EndsWith("_top.mdl"));
        Assert.True(top.DefaultData);
        Assert.Equal([SyntheticModel.PackPieceVanillaMaterial, SyntheticModel.PackPieceMaterial], top.Refs);
        Assert.Contains(files.Files, f => f.GamePath.EndsWith("_dwn.mdl") && f.Refs.SequenceEqual(["/mt_c0201b0001_bibo.mtrl"]));
        Assert.Empty(files.Files.Single(f => f.GamePath.EndsWith(".tex")).Refs);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-37")]
    public void ModelMaterialNames_PrefixEqualsFullParse()
    {
        foreach (var model in new[]
                 {
                     SyntheticModel.PackPiece(),
                     SyntheticModel.Build(["atr_a"], [new SyntheticModel.Mesh("/mt_c0201b0001_bibo.mtrl"),
                                                      new SyntheticModel.Mesh("chara/common/abs/mt_custom.mtrl")],
                                          SyntheticModel.V5, shapeNames: ["shp_a"]),
                     SyntheticModel.Build([], [new SyntheticModel.Mesh("/mt_c0201e6255_top_a.mtrl")], SyntheticModel.V6),
                 })
        {
            var prefix = SweepModReader.ModelMaterialNames(new MemoryStream(model));
            Assert.Equal(SecondSkinWriter.MaterialNames(model), prefix);
        }
        Assert.Null(SweepModReader.ModelMaterialNames(new MemoryStream([1, 2, 3])));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void SweepModReader_FailedManifest_ManifestFailed()
    {
        Mod("Broken", "{ \"FileVersion\": 4, \"DefaultData\": ");
        Assert.True(new SweepModReader().Files(mods, "Broken").ManifestFailed);
        Assert.True(new SweepModReader().Files(mods, "Not There").ManifestFailed);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void SweepModReader_UnreadableFile_MarkedAndCounted()
    {
        var root = BodyMod(matte: [0, 1, 2]);   // not a material
        File.Delete(Path.Combine(root, "top.mdl"));
        var reader = new SweepModReader();
        var files = reader.Files(mods, "Body");
        Assert.False(files.ManifestFailed);
        Assert.True(files.Files.Single(f => f.GamePath.EndsWith("_bibo.mtrl")).Unreadable);
        Assert.True(files.Files.Single(f => f.GamePath.EndsWith("_top.mdl")).Unreadable);
        Assert.False(files.Files.Single(f => f.GamePath.EndsWith(".tex")).Unreadable);
        Assert.Equal(2, reader.Unreadable);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-40")]
    public void SweepModReader_FailedReadsNotCached_RetrySucceeds()
    {
        var root = BodyMod(matte: [0, 1, 2]);
        var reader = new SweepModReader();
        Assert.True(reader.Files(mods, "Body").Files.Single(f => f.GamePath.EndsWith("_bibo.mtrl")).Unreadable);
        File.WriteAllBytes(Path.Combine(root, "matte.mtrl"), Mtrl("chara/bibo_mid_base.tex"));
        Assert.Equal(["chara/bibo_mid_base.tex"], reader.Files(mods, "Body").Files.Single(f => f.GamePath.EndsWith("_bibo.mtrl")).Refs);

        var meta = Path.Combine(root, PenumbraModMeta.MetaFile);
        File.WriteAllText(meta, "{ broken");
        Assert.True(reader.Files(mods, "Body").ManifestFailed);
        File.WriteAllText(meta, Body);
        File.SetLastWriteTimeUtc(meta, DateTime.UtcNow.AddMinutes(1));
        Assert.False(reader.Files(mods, "Body").ManifestFailed);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-40")]
    public void SweepModReader_CacheReusesUnchangedFile_RereadsChanged()
    {
        var root = BodyMod();
        var reader = new SweepModReader();
        reader.Files(mods, "Body");
        int cold = reader.ColdReads;
        reader.Files(mods, "Body");
        Assert.Equal(cold, reader.ColdReads);   // nothing re-read

        var matte = Path.Combine(root, "matte.mtrl");
        File.WriteAllBytes(matte, Mtrl("chara/other.tex", "chara/more.tex"));
        File.SetLastWriteTimeUtc(matte, DateTime.UtcNow.AddMinutes(1));
        var refs = reader.Files(mods, "Body").Files.Single(f => f.GamePath.EndsWith("_bibo.mtrl")).Refs;
        Assert.Equal(["chara/other.tex", "chara/more.tex"], refs);
        Assert.Equal(cold + 1, reader.ColdReads);
    }
}
