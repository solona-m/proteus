using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The managed mod's own output versus the live brush's shared previews kept inside it.</summary>
public class ManagedModPathsTests
{
    private const string Managed = @"C:\Penumbra\Proteus";

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-1")]
    public void Shared_previews_live_under_the_managed_mod()
        => Assert.Equal(@"C:\Penumbra\Proteus\LivePreview", ManagedModPaths.LivePreviewDir(Managed));

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-6")]
    [Trait("Plan", "EDGE-brush-pair-sync-27")]
    public void Only_files_under_the_live_preview_folder_are_live_previews()
    {
        Assert.True(ManagedModPaths.IsLivePreview(@"C:\Penumbra\Proteus\LivePreview\1-ab-000001.mdl", Managed));
        // As Penumbra spells it: forward slashes, other casing, a |…| prefix.
        Assert.True(ManagedModPaths.IsLivePreview("|1_2_3|c:/penumbra/proteus/livepreview/1-ab-000001.mdl", Managed));
        Assert.False(ManagedModPaths.IsLivePreview(@"C:\Penumbra\Proteus\models\a.mdl", Managed));
        Assert.False(ManagedModPaths.IsLivePreview(@"C:\Penumbra\Proteus\LivePreviewX\a.mdl", Managed));
        Assert.False(ManagedModPaths.IsLivePreview(@"C:\Penumbra\Old Thing\LivePreview\a.mdl", Managed));
        Assert.False(ManagedModPaths.IsLivePreview(@"C:\Penumbra\Proteus\LivePreview\a.mdl", null));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-6")]
    [Trait("Plan", "EDGE-brush-pair-sync-14")]
    public void Published_output_excludes_live_previews_and_nothing_else()
    {
        Assert.True(ManagedModPaths.IsPublishedOutput(@"C:\Penumbra\Proteus\models\a.mdl", Managed));
        Assert.True(ManagedModPaths.IsPublishedOutput(@"C:\Penumbra\Proteus\textures\b.tex", Managed));
        Assert.True(ManagedModPaths.IsPublishedOutput(@"C:\Penumbra\Proteus\models\upstream\c.mdl", Managed));
        Assert.True(ManagedModPaths.IsPublishedOutput("c:/penumbra/proteus/models/a.mdl", Managed));
        Assert.False(ManagedModPaths.IsPublishedOutput(@"C:\Penumbra\Proteus\LivePreview\1-ab-000001.mdl", Managed));
        Assert.False(ManagedModPaths.IsPublishedOutput(@"C:\Penumbra\Old Thing\top.mdl", Managed));
        Assert.False(ManagedModPaths.IsPublishedOutput(@"C:\Penumbra\ProteusOther\top.mdl", Managed));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-6")]
    [Trait("Plan", "EDGE-brush-pair-sync-14")]
    public void The_shell_host_test_reads_a_live_preview_as_an_outside_file()
    {
        Assert.False(PenumbraManipulations.IsInsideOutputRoot(@"C:\Penumbra\Proteus\LivePreview\1-ab-000001.mdl", Managed));
        Assert.True(PenumbraManipulations.IsInsideOutputRoot(@"C:\Penumbra\Proteus\models\a.mdl", Managed));
        Assert.True(PenumbraManipulations.IsInsideOutputRoot(@"C:\Penumbra\Proteus\models\upstream\b.mdl", Managed));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-6")]
    [Trait("Plan", "EDGE-brush-pair-sync-28")]
    [Trait("Plan", "EDGE-brush-pair-sync-29")]
    public void The_compositor_never_takes_a_live_preview_for_its_own_output()
    {
        Assert.False(CompositorService.IsOwnOutputCore(@"C:\Penumbra\Proteus\LivePreview\1-ab-000001.mdl", Managed));
        Assert.True(CompositorService.IsOwnOutputCore(@"C:\Penumbra\Proteus\textures\a.tex", Managed));
        Assert.False(CompositorService.IsOwnOutputCore(@"C:\Penumbra\Old Thing\a.tex", Managed));
        // Undecidable stays undecided, so the caller keeps reading it as ours.
        Assert.Null(CompositorService.IsOwnOutputCore(null, Managed));
        Assert.Null(CompositorService.IsOwnOutputCore(@"C:\Penumbra\Proteus\textures\a.tex", null));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-6")]
    public void Face_sheet_measuring_reads_a_live_preview_as_an_outside_file()
    {
        Assert.False(ModCreationService.IsOwnOutputUnder(@"C:\Penumbra\Proteus\LivePreview\1-ab-000001.mdl", @"C:\Penumbra"));
        Assert.True(ModCreationService.IsOwnOutputUnder(@"C:\Penumbra\Proteus\textures\a.tex", @"C:\Penumbra"));
        Assert.False(ModCreationService.IsOwnOutputUnder(@"C:\Penumbra\Proteus\textures\a.tex", null));
    }
}
