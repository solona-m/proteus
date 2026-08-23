using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The two rules that decide whether a content pack's mesh can be appended at all: which of a model's
/// declared materials actually carry geometry, and whether the pack binds them.
/// </summary>
public class ContentPieceTests
{
    private const string ContentPack = @"E:\ModPacks\Neolithe Piercings for Proteus.pmp";

    private static byte[]? ReadPackEntry(string entry)
    {
        if (!File.Exists(ContentPack)) return null;
        using var zip = ZipFile.OpenRead(ContentPack);
        var e = zip.GetEntry(entry);
        if (e == null) return null;
        using var st = e.Open();
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Only_materials_with_geometry_count_as_used()
    {
        // A pack starts from a stock model, empties the vanilla meshes and adds its own. Those emptied
        // meshes still DECLARE their vanilla materials, so a rule that demanded a binding for every
        // declared material would reject the pack over meshes that emit nothing.
        var model = ReadPackEntry("top/belly button heart/chara/equipment/e0000/model/c0201e0000_top.mdl");
        if (model == null) return;

        var declared = SecondSkinWriter.MaterialNames(model);
        Assert.Contains("/mt_c0201b0001_a.mtrl", declared);
        Assert.Contains("/mt_c0201e0000_top_a.mtrl", declared);

        var used = SecondSkinService.UsedMaterialNames(model, declared);
        Assert.Equal(new[] { "/mt_c0201b0001_a.mtrl" }, used);
    }

    [Fact]
    public void Material_binding_is_leaf_matched_and_case_insensitive()
    {
        // The model stores names with a leading slash, a manifest lists them without one, and neither
        // agrees on case. All three spellings must reach the same .mtrl.
        var piece = new ContentPiece
        {
            Model = "model.mdl",
            Materials = { ["mt_c0201b0001_neolithe_piercings.mtrl"] = "common/1/piercings.mtrl" },
        };

        Assert.Equal("common/1/piercings.mtrl", piece.MaterialFor("/mt_c0201b0001_neolithe_piercings.mtrl"));
        Assert.Equal("common/1/piercings.mtrl", piece.MaterialFor("mt_c0201b0001_neolithe_piercings.mtrl"));
        Assert.Equal("common/1/piercings.mtrl", piece.MaterialFor("/MT_C0201B0001_NEOLITHE_PIERCINGS.MTRL"));
        Assert.Null(piece.MaterialFor("/mt_c0201b0001_a.mtrl"));
    }

    [Fact]
    public void Surface_defaults_to_the_body()
    {
        var piece = new ContentPiece { Model = "m.mdl" };
        Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty), piece.SurfaceKey);
        Assert.False(piece.SurfaceKey.RequiresNativeHost);

        var face = new ContentPiece { Model = "m.mdl", Surface = ShellSurfaceKind.Face, SurfaceId = "f0001" };
        Assert.Equal(new ShellSurfaceKey(ShellSurfaceKind.Face, "f0001"), face.SurfaceKey);
        Assert.True(face.SurfaceKey.RequiresNativeHost);
    }
}
