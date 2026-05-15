using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for SidecarDiscoveryService. Only the paths that don't invoke the
/// Penumbra IPC (i.e. no <c>penumbra.GetPlayerCollectionId()</c> call) are
/// tested here, because PenumbraBridge requires a live Dalamud plugin interface.
///
/// Covered paths:
///   1. ResolveActiveOverlays with a simple flat Overlays list (no Penumbra call).
///   2. ResolveActiveOverlays when OptionGroups is null (returns empty, no Penumbra call).
///   3. Flat overlays take precedence over OptionGroups even when both are present.
///   4. GetActiveColorRows for a simple flat overlays entry.
///   5. SaveMetadata writes valid JSON that round-trips correctly.
/// </summary>
public class SidecarDiscoveryTests
{
    // Convenience: create a service with a null bridge (safe for the code paths we test).
    private static SidecarDiscoveryService MakeService() =>
        new(null!, Substitute.For<IPluginLog>());

    // ── ResolveActiveOverlays – flat overlays list ────────────────────────────

    [Fact]
    public void ResolveActiveOverlays_FlatOverlays_ReturnsAllDescriptorsWithTopLevelRows()
    {
        var desc1 = new OverlayDescriptor { MaterialGamePaths = ["a.mtrl"], Diffuse = "d.png" };
        var desc2 = new OverlayDescriptor { MaterialGamePaths = ["b.mtrl"], Normal  = "n.png" };
        var rows  = new List<ColorTableRowPreset> { new() { Row = 16 } };
        var meta  = new ProteusMetadata
        {
            Overlays        = [desc1, desc2],
            ColorTableRows  = rows
        };

        var result = MakeService().ResolveActiveOverlays(Entry(meta));

        Assert.Equal(2, result.Count);
        Assert.Same(desc1, result[0].Descriptor);
        Assert.Same(rows,  result[0].ColorTableRows);
        Assert.Same(desc2, result[1].Descriptor);
        Assert.Same(rows,  result[1].ColorTableRows);
    }

    [Fact]
    public void ResolveActiveOverlays_FlatOverlays_NoTopLevelRows_ColorTableRowsIsNull()
    {
        var meta = new ProteusMetadata
        {
            Overlays       = [new OverlayDescriptor { MaterialGamePaths = ["x.mtrl"] }],
            ColorTableRows = null
        };

        var result = MakeService().ResolveActiveOverlays(Entry(meta));

        Assert.Single(result);
        Assert.Null(result[0].ColorTableRows);
    }

    [Fact]
    public void ResolveActiveOverlays_EmptyFlatOverlaysList_FallsThroughToOptionGroups()
    {
        // Overlays is present but empty → not { Count: > 0 } → falls through.
        // OptionGroups is null → returns [].
        var meta = new ProteusMetadata { Overlays = [], OptionGroups = null };

        var result = MakeService().ResolveActiveOverlays(Entry(meta));

        Assert.Empty(result);
    }

    // ── ResolveActiveOverlays – null OptionGroups ─────────────────────────────

    [Fact]
    public void ResolveActiveOverlays_NullOptionGroups_ReturnsEmpty()
    {
        var meta = new ProteusMetadata { OptionGroups = null };

        var result = MakeService().ResolveActiveOverlays(Entry(meta));

        Assert.Empty(result);
    }

    // ── ResolveActiveOverlays – flat vs option groups precedence ─────────────

    [Fact]
    public void ResolveActiveOverlays_BothFlatAndOptionGroups_FlatWins()
    {
        var flatDesc  = new OverlayDescriptor { MaterialGamePaths = ["flat.mtrl"] };
        var groupDesc = new OverlayDescriptor { MaterialGamePaths = ["group.mtrl"] };
        var meta = new ProteusMetadata
        {
            Overlays     = [flatDesc],
            OptionGroups =
            [
                new OverlayOptionGroup
                {
                    PenumbraGroupName = "Skin",
                    Options =
                    [
                        new OverlayOption
                        {
                            Name     = "Option1",
                            Overlays = [groupDesc]
                        }
                    ]
                }
            ]
        };

        var result = MakeService().ResolveActiveOverlays(Entry(meta));

        Assert.Single(result);
        Assert.Same(flatDesc, result[0].Descriptor); // flat overlay wins
    }

    // ── GetActiveColorRows – flat overlays ────────────────────────────────────

    [Fact]
    public void GetActiveColorRows_FlatOverlays_WithExistingRows_ReturnsThemDirectly()
    {
        var existingRows = new List<ColorTableRowPreset> { new() { Row = 1 } };
        var meta = new ProteusMetadata
        {
            Overlays       = [new OverlayDescriptor { MaterialGamePaths = ["x.mtrl"] }],
            ColorTableRows = existingRows
        };
        var entry = Entry(meta);

        var result = MakeService().GetActiveColorRows(entry);

        Assert.Same(existingRows, result);
    }

    [Fact]
    public void GetActiveColorRows_FlatOverlays_NullRows_CreatesEmptyList()
    {
        var meta = new ProteusMetadata
        {
            Overlays       = [new OverlayDescriptor { MaterialGamePaths = ["x.mtrl"] }],
            ColorTableRows = null
        };
        var entry = Entry(meta);

        var result = MakeService().GetActiveColorRows(entry);

        Assert.NotNull(result);
        Assert.Empty(result);
        // The list is now set on the metadata object itself.
        Assert.Same(result, entry.Metadata.ColorTableRows);
    }

    [Fact]
    public void GetActiveColorRows_NoOverlaysNoGroups_CreatesTopLevelEmptyList()
    {
        // Neither Overlays nor OptionGroups → falls through to the final fallback.
        var meta  = new ProteusMetadata { Overlays = null, OptionGroups = null };
        var entry = Entry(meta);

        var result = MakeService().GetActiveColorRows(entry);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ── SaveMetadata ──────────────────────────────────────────────────────────

    [Fact]
    public void SaveMetadata_WritesMetadataJsonFile()
    {
        using var tmpDir = new TempDirectory();
        var meta  = new ProteusMetadata
        {
            Name     = "TestMod",
            Author   = "Author",
            Overlays = [new OverlayDescriptor { MaterialGamePaths = ["a.mtrl"], Diffuse = "d.png" }]
        };
        var entry = Entry(meta, sidecarRoot: tmpDir.Path);

        MakeService().SaveMetadata(entry);

        var metaPath = Path.Combine(tmpDir.Path, "metadata.json");
        Assert.True(File.Exists(metaPath));
    }

    [Fact]
    public void SaveMetadata_WrittenJson_RoundTripsToEquivalentMetadata()
    {
        using var tmpDir = new TempDirectory();
        var colorRows = new List<ColorTableRowPreset>
        {
            new()
            {
                Row    = 16,
                SubRowA = new ColorTableSubRowPreset { Diffuse = "#FF0000", Emissive = 0.5f, Opacity = 10 }
            }
        };
        var meta = new ProteusMetadata
        {
            Name           = "RoundTrip Mod",
            Author         = "Tester",
            ColorTableRows = colorRows,
            Overlays =
            [
                new OverlayDescriptor
                {
                    MaterialGamePaths = ["a.mtrl", "b.mtrl"],
                    Diffuse           = "d.png",
                    Normal            = "n.png",
                    Index             = "id.png"
                }
            ]
        };
        var entry = Entry(meta, sidecarRoot: tmpDir.Path);
        MakeService().SaveMetadata(entry);

        var json   = File.ReadAllText(Path.Combine(tmpDir.Path, "metadata.json"));
        var loaded = JsonSerializer.Deserialize<ProteusMetadata>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(loaded);
        Assert.Equal("RoundTrip Mod", loaded!.Name);
        Assert.Equal("Tester",        loaded.Author);
        Assert.NotNull(loaded.Overlays);
        Assert.Single(loaded.Overlays!);
        Assert.Equal(2,        loaded.Overlays![0].MaterialGamePaths.Count);
        Assert.Equal("d.png",  loaded.Overlays[0].Diffuse);
        Assert.Equal("id.png", loaded.Overlays[0].Index);
        Assert.NotNull(loaded.ColorTableRows);
        Assert.Single(loaded.ColorTableRows!);
        Assert.Equal(16,     loaded.ColorTableRows![0].Row);
        Assert.Equal(0.5f,   loaded.ColorTableRows[0].SubRowA!.Emissive, precision: 5);
    }

    [Fact]
    public void SaveMetadata_OptionGroupsMod_WritesOptionGroups()
    {
        using var tmpDir = new TempDirectory();
        var meta = new ProteusMetadata
        {
            Name         = "Options Mod",
            OptionGroups =
            [
                new OverlayOptionGroup
                {
                    PenumbraGroupName = "Skin",
                    Options =
                    [
                        new OverlayOption
                        {
                            Name           = "Light",
                            Overlays       = [new OverlayDescriptor { MaterialGamePaths = ["x.mtrl"] }],
                            ColorTableRows = [new ColorTableRowPreset { Row = 1 }]
                        }
                    ]
                }
            ]
        };
        var entry = Entry(meta, sidecarRoot: tmpDir.Path);
        MakeService().SaveMetadata(entry);

        var json   = File.ReadAllText(Path.Combine(tmpDir.Path, "metadata.json"));
        var loaded = JsonSerializer.Deserialize<ProteusMetadata>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Overlays);
        Assert.NotNull(loaded.OptionGroups);
        Assert.Single(loaded.OptionGroups!);
        Assert.Equal("Skin",  loaded.OptionGroups![0].PenumbraGroupName);
        Assert.Equal("Light", loaded.OptionGroups[0].Options[0].Name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OverlayEntry Entry(ProteusMetadata meta, string? sidecarRoot = null) =>
        new("mod1", "Mod 1", 10, meta, sidecarRoot ?? "/tmp/mod1/Proteus");

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
