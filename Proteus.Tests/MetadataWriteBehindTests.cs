using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>When the colour editor's held metadata is due to be written, and what survives a write landing late.</summary>
public class MetadataWriteBehindTests
{
    // Never touched: the store does no IO. Rooted in temp so no test names a local drive.
    private static readonly string Mods = Path.Combine(Path.GetTempPath(), "proteus-mods");
    private static readonly string Root = Path.Combine(Mods, "Skin", "Proteus");

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-1")]
    public void Nothing_is_due_while_a_control_is_held_and_the_latest_once_it_is_let_go()
    {
        // [AC-colorset-save-on-release-1]
        var store = new MetadataWriteBehind();
        for (int i = 0; i < 60; i++) store.Stage("Skin", Root, $"{{\"v\":{i}}}");

        Assert.Empty(store.Due(editing: true, now: 0));
        var due = Assert.Single(store.Due(editing: false, now: 0));
        Assert.Equal("{\"v\":59}", due.Json);

        store.Complete(due);
        Assert.Empty(store.Due(editing: false, now: 0));
        Assert.Null(store.Peek("Skin"));
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-3")]
    public void A_write_landing_after_a_newer_stage_leaves_the_newer_one_held()
    {
        // [EDGE-colorset-save-on-release-3]
        var store = new MetadataWriteBehind();
        store.Stage("Skin", Root, "old");
        var writing = Assert.Single(store.Due(false, 0));
        store.Stage("Skin", Root, "new");

        store.Complete(writing);
        Assert.Equal("new", store.Peek("Skin")!.Json);

        Assert.Equal(StagedFailure.Superseded, store.Failed(writing, now: 0));
        var held = store.Peek("Skin")!;
        Assert.Equal("new", held.Json);
        Assert.Equal(0, held.Failures);
    }

    [Fact]
    [Trait("Plan", "AC-colorset-save-on-release-8")]
    public void A_failed_write_waits_a_second_and_is_given_up_on_the_fifth_failure()
    {
        // [AC-colorset-save-on-release-8]
        var store = new MetadataWriteBehind();
        store.Stage("Skin", Root, "x");
        long now = 10_000;

        for (int i = 1; i < MetadataWriteBehind.MaxFailures; i++)
        {
            var s = Assert.Single(store.Due(false, now));
            Assert.Equal(StagedFailure.Retrying, store.Failed(s, now));
            Assert.Empty(store.Due(false, now + MetadataWriteBehind.RetryDelayMs - 1));
            Assert.Equal("x", store.Peek("Skin")!.Json);   // still read by discovery while it waits
            now += MetadataWriteBehind.RetryDelayMs;
        }

        Assert.Equal(StagedFailure.GaveUp, store.Failed(Assert.Single(store.Due(false, now)), now));
        Assert.Null(store.Peek("Skin"));
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-9")]
    public void A_renamed_mod_takes_its_held_edit_to_the_new_folder()
    {
        // [EDGE-colorset-save-on-release-9]
        var store = new MetadataWriteBehind();
        store.Stage("Skin", Root, "x");

        store.Follow("Skin", "Skin v2");

        Assert.Null(store.Peek("Skin"));
        var moved = store.Peek("Skin v2")!;
        Assert.Equal("Skin v2", moved.ModDirectory);
        Assert.Equal(Path.Combine(Mods, "Skin v2", "Proteus"), moved.SidecarRoot);
        Assert.Equal("x", moved.Json);
    }

    [Fact]
    [Trait("Plan", "EDGE-colorset-save-on-release-15")]
    public void Mods_are_matched_ignoring_case_and_an_unknown_one_holds_nothing()
    {
        // [EDGE-colorset-save-on-release-15]
        var store = new MetadataWriteBehind();
        store.Stage("Skin", Root, "a");
        store.Stage("SKIN", Root, "b");

        Assert.Equal("b", store.Peek("skin")!.Json);
        Assert.Single(store.All());
        Assert.Null(store.Peek("Other"));
    }
}
