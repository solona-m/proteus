using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The design-restore sweep must tell "this mod's file list could not be read" from "this mod has no files": a body mod
/// whose manifest is mid-save read as empty would let the sweep hold off the skin mod its body material needs.
/// <see cref="PenumbraModMeta.TryReadAllRedirects"/> reports the failure; <see cref="PenumbraModMeta.ReadAllRedirects"/>
/// keeps answering exactly as before for its other callers.
/// </summary>
public sealed class PenumbraModMetaTryReadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-tryread-" + Guid.NewGuid().ToString("N"));

    public PenumbraModMetaTryReadTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }

    private const string V4 = """
        {
          "FileVersion": 4,
          "Name": "Body",
          "DefaultData": { "Files": { "chara/bibo_mid_base.tex": "tex\\base.tex" } },
          "Groups": [
            { "Name": "Skin Materials", "Type": "Single", "Options": [
              { "Name": "Off", "Files": {} },
              { "Name": "Matte", "Files": { "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl": "mtrl\\matte.mtrl" } } ] }
          ]
        }
        """;

    private void Write(string json) => File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), json);

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void TryReadAllRedirects_ReadableManifest_SameAsReadAllRedirects()
    {
        Write(V4);
        var strict = PenumbraModMeta.TryReadAllRedirects(root);
        Assert.NotNull(strict);
        Assert.Equal(PenumbraModMeta.ReadAllRedirects(root), strict);
        Assert.Contains(strict!, r => r.GamePath == "chara/bibo_mid_base.tex" && r.Source == "");
        Assert.Contains(strict!, r => r.Source == "Skin Materials / Matte");
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void TryReadAllRedirects_HeldOpenManifest_Null()
    {
        Write(V4);
        using (new FileStream(Path.Combine(root, PenumbraModMeta.MetaFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(PenumbraModMeta.TryReadAllRedirects(root));
            Assert.Empty(PenumbraModMeta.ReadAllRedirects(root));   // today's answer, unchanged
        }
    }

    [Theory]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    [InlineData("{ \"FileVersion\": 4, \"DefaultData\": { \"Files\": { \"a\": ")]           // truncated mid-save
    [InlineData("[1, 2, 3]")]                                                               // not an object
    [InlineData("{ \"FileVersion\": 4, \"DefaultData\": [], \"Groups\": [] }")]             // DefaultData of the wrong kind
    [InlineData("{ \"FileVersion\": 4, \"DefaultData\": { \"Files\": {} }, \"Groups\": {} }")] // Groups of the wrong kind
    public void TryReadAllRedirects_MalformedOrWrongKind_Null(string json)
    {
        Write(json);
        Assert.Null(PenumbraModMeta.TryReadAllRedirects(root));
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void TryReadAllRedirects_MissingManifest_Null()
        => Assert.Null(PenumbraModMeta.TryReadAllRedirects(root));

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void ReadAllRedirects_SameInputs_TodaysResults()
    {
        // A missing or malformed manifest still reads as empty, never throws.
        Assert.Empty(PenumbraModMeta.ReadAllRedirects(root));
        Write("{ \"FileVersion\": 4, \"DefaultData\": { \"Files\": { \"a\": ");
        Assert.Empty(PenumbraModMeta.ReadAllRedirects(root));
        Write(V4);
        Assert.Equal(2, PenumbraModMeta.ReadAllRedirects(root).Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-sweep-keeps-deps-26")]
    public void TryReadAllRedirects_LegacyLayout_ReadsGroupFiles()
    {
        Write("{ \"FileVersion\": 3, \"Name\": \"Old\" }");
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.LegacyDefaultMod), "{ \"Files\": { \"chara/x.tex\": \"x.tex\" } }");
        File.WriteAllText(Path.Combine(root, "group_001_a.json"),
            "{ \"Name\": \"A\", \"Options\": [ { \"Name\": \"On\", \"Files\": { \"chara/y.tex\": \"y.tex\" } } ] }");
        var strict = PenumbraModMeta.TryReadAllRedirects(root);
        Assert.NotNull(strict);
        Assert.Equal(["chara/x.tex", "chara/y.tex"], strict!.Select(r => r.GamePath).Order().ToArray());

        File.WriteAllText(Path.Combine(root, "group_002_b.json"), "{ broken");
        Assert.Null(PenumbraModMeta.TryReadAllRedirects(root));
    }
}
