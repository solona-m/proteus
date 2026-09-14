using System.Collections.Generic;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Saving the reinforced toe while a preset is in charge must write that ONE field. A full save would also
/// make permanent every preview edit the other footer controls had made to the same in-memory descriptors.
/// The save finds each edited descriptor's twin in a copy read back from disk; these pin that lookup.
/// </summary>
public class ToeCapDensitySaveTests
{
    private static ProteusMetadata WithGroups() => new()
    {
        OptionGroups =
        [
            new OverlayOptionGroup { Options =
            [
                new OverlayOption { Name = "Rainbow", Overlays = [new OverlayDescriptor { Diffuse = "a.png" }] },
            ]},
            new OverlayOptionGroup { Options =
            [
                new OverlayOption { Name = "Fishnet", Overlays = [new OverlayDescriptor { Diffuse = "f.png" }] },
                new OverlayOption { Name = "Sheer",   Overlays = [new OverlayDescriptor { Diffuse = "s0.png" },
                                                                  new OverlayDescriptor { Diffuse = "s1.png" }] },
            ]},
        ],
    };

    /// <summary>The disk copy is a separate object graph with the same shape, as a fresh parse would be.</summary>
    private static ProteusMetadata Reparse(ProteusMetadata m)
        => JsonSerializer.Deserialize<ProteusMetadata>(JsonSerializer.Serialize(m))!;

    [Fact]
    public void TwinOnDisk_FindsTheSamePositionInAnOptionGroup()
    {
        var mem = WithGroups();
        var disk = Reparse(mem);
        var edited = mem.OptionGroups![1].Options[1].Overlays[1];   // Sheer's second overlay

        var twin = SidecarDiscoveryService.TwinOnDisk(mem, disk, edited);

        Assert.Same(disk.OptionGroups![1].Options[1].Overlays[1], twin);
        Assert.Equal("s1.png", twin!.Diffuse);
    }

    /// <summary>
    /// The whole point: copying the density onto the twin leaves the disk copy free of the preview edits that
    /// were made to the in-memory descriptor.
    /// </summary>
    [Fact]
    public void CopyingOnlyTheDensity_LeavesPreviewEditsOutOfTheDiskCopy()
    {
        var mem = WithGroups();
        var disk = Reparse(mem);
        var edited = mem.OptionGroups![1].Options[1].Overlays[0];

        edited.Scroll = "preview-glow.tex";          // what a glow pick under a preset does to the base descriptor
        edited.ManualShaderLock = true;              // and a mode pin
        edited.ToeCapDensity = 45;

        var twin = SidecarDiscoveryService.TwinOnDisk(mem, disk, edited)!;
        twin.ToeCapDensity = edited.ToeCapDensity;

        Assert.Equal(45, twin.ToeCapDensity);
        Assert.Null(twin.Scroll);
        Assert.False(twin.ManualShaderLock);
    }

    [Fact]
    public void TwinOnDisk_FindsTopLevelOverlays()
    {
        var mem = new ProteusMetadata
        {
            Overlays = [new OverlayDescriptor { Diffuse = "x.png" }, new OverlayDescriptor { Diffuse = "y.png" }],
        };
        var disk = Reparse(mem);

        var twin = SidecarDiscoveryService.TwinOnDisk(mem, disk, mem.Overlays![1]);

        Assert.Same(disk.Overlays![1], twin);
    }

    /// <summary>A file whose option at that position is a different option has changed shape; never guess.</summary>
    [Fact]
    public void TwinOnDisk_RefusesWhenTheOptionAtThatPositionIsADifferentOne()
    {
        var mem = WithGroups();
        var disk = Reparse(mem);
        disk.OptionGroups![1].Options[1].Name = "Velvet";

        Assert.Null(SidecarDiscoveryService.TwinOnDisk(mem, disk, mem.OptionGroups![1].Options[1].Overlays[0]));
    }

    [Fact]
    public void TwinOnDisk_RefusesWhenTheDiskCopyIsMissingThatOverlay()
    {
        var mem = WithGroups();
        var disk = Reparse(mem);
        disk.OptionGroups![1].Options[1].Overlays.RemoveAt(1);

        Assert.Null(SidecarDiscoveryService.TwinOnDisk(mem, disk, mem.OptionGroups![1].Options[1].Overlays[1]));
    }

    /// <summary>A descriptor that is not part of the metadata at all has no twin.</summary>
    [Fact]
    public void TwinOnDisk_UnknownDescriptor_IsNull()
    {
        var mem = WithGroups();
        Assert.Null(SidecarDiscoveryService.TwinOnDisk(mem, Reparse(mem), new OverlayDescriptor()));
    }
}
