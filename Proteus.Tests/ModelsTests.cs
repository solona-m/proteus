using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Proteus.Tests;

public class ModelsTests
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    // ── StringOrStringArrayConverter ─────────────────────────────────────────

    [Fact]
    public void Converter_ReadsSingleString_AsOneElementList()
    {
        var json = """{"MaterialGamePath":"chara/body/test.mtrl"}""";
        var result = JsonSerializer.Deserialize<OverlayDescriptor>(json, CaseInsensitive);
        Assert.NotNull(result);
        Assert.Single(result!.MaterialGamePaths);
        Assert.Equal("chara/body/test.mtrl", result.MaterialGamePaths[0]);
    }

    [Fact]
    public void Converter_ReadsJsonArray_AsList()
    {
        var json = """{"MaterialGamePath":["a.mtrl","b.mtrl","c.mtrl"]}""";
        var result = JsonSerializer.Deserialize<OverlayDescriptor>(json, CaseInsensitive);
        Assert.NotNull(result);
        Assert.Equal(3, result!.MaterialGamePaths.Count);
        Assert.Equal("a.mtrl", result.MaterialGamePaths[0]);
        Assert.Equal("b.mtrl", result.MaterialGamePaths[1]);
        Assert.Equal("c.mtrl", result.MaterialGamePaths[2]);
    }

    [Fact]
    public void Converter_ReadsEmptyArray_AsEmptyList()
    {
        var json = """{"MaterialGamePath":[]}""";
        var result = JsonSerializer.Deserialize<OverlayDescriptor>(json, CaseInsensitive);
        Assert.NotNull(result);
        Assert.Empty(result!.MaterialGamePaths);
    }

    [Fact]
    public void Converter_InvalidToken_ThrowsJsonException()
    {
        var json = """{"MaterialGamePath":42}""";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<OverlayDescriptor>(json, CaseInsensitive));
    }

    [Fact]
    public void Converter_WritesSingleElement_AsPlainString()
    {
        var desc = new OverlayDescriptor { MaterialGamePaths = ["only.mtrl"] };
        var json = JsonSerializer.Serialize(desc);
        // Single path serialises as a plain string, not an array
        Assert.Contains("\"only.mtrl\"", json);
        Assert.DoesNotContain("[", json);
    }

    [Fact]
    public void Converter_WritesMultipleElements_AsArray()
    {
        var desc = new OverlayDescriptor { MaterialGamePaths = ["a.mtrl", "b.mtrl"] };
        var json = JsonSerializer.Serialize(desc);
        Assert.Contains("[", json);
        Assert.Contains("\"a.mtrl\"", json);
        Assert.Contains("\"b.mtrl\"", json);
    }

    [Fact]
    public void Converter_RoundTrip_SinglePath()
    {
        var original = new OverlayDescriptor { MaterialGamePaths = ["chara/body/v0001/mt_c0101b0001_b.mtrl"] };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<OverlayDescriptor>(json, CaseInsensitive);
        Assert.NotNull(loaded);
        Assert.Equal(original.MaterialGamePaths, loaded!.MaterialGamePaths);
    }

    [Fact]
    public void Converter_RoundTrip_MultiplePaths()
    {
        var original = new OverlayDescriptor { MaterialGamePaths = ["a.mtrl", "b.mtrl"] };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<OverlayDescriptor>(json, CaseInsensitive);
        Assert.NotNull(loaded);
        Assert.Equal(original.MaterialGamePaths, loaded!.MaterialGamePaths);
    }

    // ── ProteusMetadata ──────────────────────────────────────────────────────

    [Fact]
    public void ProteusMetadata_DeserializesSimpleOverlays()
    {
        var json = """
        {
            "FormatVersion": 1,
            "Name": "My Skin Mod",
            "Author": "Modder",
            "Overlays": [
                {
                    "MaterialGamePath": "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_b.mtrl",
                    "Diffuse": "overlay_d.png",
                    "Normal": "overlay_n.png",
                    "Mask": "overlay_m.png"
                }
            ]
        }
        """;

        var meta = JsonSerializer.Deserialize<ProteusMetadata>(json, CaseInsensitive);
        Assert.NotNull(meta);
        Assert.Equal(1, meta!.FormatVersion);
        Assert.Equal("My Skin Mod", meta.Name);
        Assert.Equal("Modder", meta.Author);
        Assert.NotNull(meta.Overlays);
        Assert.Single(meta.Overlays!);
        Assert.Equal("overlay_d.png", meta.Overlays![0].Diffuse);
        Assert.Equal("overlay_n.png", meta.Overlays[0].Normal);
        Assert.Equal("overlay_m.png", meta.Overlays[0].Mask);
        Assert.Null(meta.OptionGroups);
    }

    [Fact]
    public void ProteusMetadata_DeserializesIndexOverlay()
    {
        var json = """
        {
            "FormatVersion": 1,
            "Name": "Index Mod",
            "Overlays": [
                {
                    "MaterialGamePath": "chara/test.mtrl",
                    "Index": "overlay_id.png"
                }
            ]
        }
        """;

        var meta = JsonSerializer.Deserialize<ProteusMetadata>(json, CaseInsensitive);
        Assert.NotNull(meta);
        Assert.Equal("overlay_id.png", meta!.Overlays![0].Index);
    }

    [Fact]
    public void ProteusMetadata_DeserializesOptionGroups()
    {
        var json = """
        {
            "FormatVersion": 1,
            "Name": "Multi-Variant Mod",
            "OptionGroups": [
                {
                    "PenumbraGroupName": "Skin Tone",
                    "Options": [
                        {
                            "Name": "Light",
                            "Overlays": [
                                { "MaterialGamePath": "chara/test.mtrl", "Diffuse": "light_d.png" }
                            ],
                            "ColorTableRows": [{ "Row": 16, "SubRowA": { "Diffuse": "#FFCCAA" } }]
                        },
                        {
                            "Name": "Dark",
                            "Overlays": [
                                { "MaterialGamePath": "chara/test.mtrl", "Diffuse": "dark_d.png" }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        var meta = JsonSerializer.Deserialize<ProteusMetadata>(json, CaseInsensitive);
        Assert.NotNull(meta);
        Assert.Null(meta!.Overlays);
        Assert.NotNull(meta.OptionGroups);
        Assert.Single(meta.OptionGroups!);

        var group = meta.OptionGroups![0];
        Assert.Equal("Skin Tone", group.PenumbraGroupName);
        Assert.Equal(2, group.Options.Count);
        Assert.Equal("Light", group.Options[0].Name);
        Assert.Equal("Dark",  group.Options[1].Name);
        Assert.NotNull(group.Options[0].ColorTableRows);
        Assert.Null(group.Options[1].ColorTableRows);
    }

    [Fact]
    public void ProteusMetadata_DeserializesTopLevelColorTableRows()
    {
        var json = """
        {
            "FormatVersion": 1,
            "Name": "Color Mod",
            "ColorTableRows": [
                {
                    "Row": 16,
                    "SubRowA": { "Diffuse": "#FF0000", "Emissive": 0.75, "Opacity": 20 },
                    "SubRowB": { "Diffuse": "#00FF00", "Emissive": 0.0,  "Opacity": -10 }
                }
            ]
        }
        """;

        var meta = JsonSerializer.Deserialize<ProteusMetadata>(json, CaseInsensitive);
        Assert.NotNull(meta);
        Assert.NotNull(meta!.ColorTableRows);
        Assert.Single(meta.ColorTableRows!);

        var row = meta.ColorTableRows![0];
        Assert.Equal(16, row.Row);
        Assert.NotNull(row.SubRowA);
        Assert.Equal("#FF0000", row.SubRowA!.Diffuse);
        Assert.Equal(0.75f, row.SubRowA.Emissive, precision: 5);
        Assert.Equal(20, row.SubRowA.Opacity);
        Assert.NotNull(row.SubRowB);
        Assert.Equal("#00FF00", row.SubRowB!.Diffuse);
        Assert.Equal(-10, row.SubRowB.Opacity);
    }

    [Fact]
    public void ProteusMetadata_DefaultColorTableSubRow_HasZeroEmissiveAndOpacity()
    {
        var json = """{"Row":1,"SubRowA":{"Diffuse":"#FFFFFF"}}""";
        var preset = JsonSerializer.Deserialize<ColorTableRowPreset>(json, CaseInsensitive);
        Assert.NotNull(preset);
        Assert.Equal(0f, preset!.SubRowA!.Emissive);
        Assert.Equal(0,  preset.SubRowA.Opacity);
        Assert.Null(preset.SubRowB);
    }

    [Fact]
    public void ProteusMetadata_MissingOptionalFields_AreNull()
    {
        var json = """{"FormatVersion":1,"Name":"Minimal"}""";
        var meta = JsonSerializer.Deserialize<ProteusMetadata>(json, CaseInsensitive);
        Assert.NotNull(meta);
        Assert.Null(meta!.Overlays);
        Assert.Null(meta.OptionGroups);
        Assert.Null(meta.ColorTableRows);
        Assert.Equal(string.Empty, meta.Author);
    }

    [Fact]
    public void OverlayDescriptor_ArrayOfMaterials_RoundTrips()
    {
        var paths = new List<string> { "a.mtrl", "b.mtrl", "c.mtrl" };
        var desc = new OverlayDescriptor { MaterialGamePaths = paths, Diffuse = "d.png", Normal = "n.png" };
        var json = JsonSerializer.Serialize(desc);
        var loaded = JsonSerializer.Deserialize<OverlayDescriptor>(json, CaseInsensitive);
        Assert.NotNull(loaded);
        Assert.Equal(paths, loaded!.MaterialGamePaths);
        Assert.Equal("d.png", loaded.Diffuse);
        Assert.Equal("n.png", loaded.Normal);
    }

    [Fact]
    public void ColorTableSubRow_DefaultValues_AreWhiteWithNoGlow()
    {
        var sub = new ColorTableSubRow();
        Assert.Equal(1f, sub.DiffuseR);
        Assert.Equal(1f, sub.DiffuseG);
        Assert.Equal(1f, sub.DiffuseB);
        Assert.Equal(0f, sub.Emissive);
        Assert.Equal(0,  sub.Opacity);
    }
}
