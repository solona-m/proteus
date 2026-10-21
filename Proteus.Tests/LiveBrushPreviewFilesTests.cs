using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Where the live brush writes its previews, which it keeps, and which it recognises on the character.</summary>
public class LiveBrushPreviewFilesTests : IDisposable
{
    private readonly string tmp = Path.Combine(Path.GetTempPath(), "proteus-test-" + Guid.NewGuid().ToString("N"));

    public LiveBrushPreviewFilesTests() => Directory.CreateDirectory(tmp);

    public void Dispose()
    {
        try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-8")]
    [Trait("Plan", "EDGE-brush-pair-sync-6")]
    [Trait("Plan", "EDGE-brush-pair-sync-22")]
    [Trait("Plan", "EDGE-brush-pair-sync-23")]
    public void Without_the_managed_mod_the_share_asks_for_it_and_goes_private()
    {
        Assert.Equal(LiveBrushPreview.ShareTarget.Shared,
                     LiveBrushPreview.ChooseShareTarget(@"C:\Penumbra\Proteus", managedModExists: true));
        Assert.Equal(LiveBrushPreview.ShareTarget.CreateManagedMod,
                     LiveBrushPreview.ChooseShareTarget(@"C:\Penumbra\Proteus", managedModExists: false));
        // Penumbra's root unknown: nothing can be created, nothing is owed.
        Assert.Equal(LiveBrushPreview.ShareTarget.Private,
                     LiveBrushPreview.ChooseShareTarget(null, managedModExists: false));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-7")]
    public void A_shared_copy_that_cannot_be_written_falls_back_to_a_private_one()
    {
        var source = Path.Combine(tmp, "top.mdl");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        // A file where the shared folder should be: the folder cannot be made.
        var blocked = Path.Combine(tmp, "blocked");
        File.WriteAllBytes(blocked, [0]);
        var privateDir = Path.Combine(tmp, "private");

        var (path, shared) = LiveBrushPreview.CopyPreview(source, Path.Combine(blocked, "LivePreview"), privateDir, "1-ab-000001.mdl");

        Assert.False(shared);
        Assert.StartsWith(privateDir, path);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(path));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-1")]
    public void A_shared_copy_is_byte_for_byte_the_saved_file()
    {
        var source = Path.Combine(tmp, "top.mdl");
        File.WriteAllBytes(source, [9, 8, 7]);
        var sharedDir = Path.Combine(tmp, "Proteus", "LivePreview");

        var (path, shared) = LiveBrushPreview.CopyPreview(source, sharedDir, Path.Combine(tmp, "private"), "1-ab-000002.mdl");

        Assert.True(shared);
        Assert.Equal(Path.Combine(sharedDir, "1-ab-000002.mdl"), path);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(path));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    [Trait("Plan", "EDGE-brush-pair-sync-7")]
    public void A_copy_that_fails_part_way_leaves_nothing_behind_in_either_folder()
    {
        var sharedDir = Path.Combine(tmp, "Proteus", "LivePreview");
        var privateDir = Path.Combine(tmp, "private");

        // Half a model written, then the disk gives out — both times.
        static void HalfThenFail(string from, string to)
        {
            File.WriteAllBytes(to, [1, 2]);
            throw new IOException("disk full");
        }

        Assert.Throws<IOException>(() =>
            LiveBrushPreview.CopyPreview("top.mdl", sharedDir, privateDir, "1-ab-000003.mdl", HalfThenFail));

        Assert.Empty(Directory.GetFiles(sharedDir));
        Assert.Empty(Directory.GetFiles(privateDir));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-7")]
    public void A_shared_copy_that_fails_part_way_is_removed_and_the_private_one_written()
    {
        var sharedDir = Path.Combine(tmp, "Proteus", "LivePreview");
        var privateDir = Path.Combine(tmp, "private");
        void FailInShared(string from, string to)
        {
            File.WriteAllBytes(to, [1, 2]);
            if (to.StartsWith(sharedDir, StringComparison.OrdinalIgnoreCase)) throw new IOException("share locked");
        }

        var (path, shared) = LiveBrushPreview.CopyPreview("top.mdl", sharedDir, privateDir, "1-ab-000004.mdl", FailInShared);

        Assert.False(shared);
        Assert.Empty(Directory.GetFiles(sharedDir));
        Assert.True(File.Exists(path));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    [Trait("Plan", "EDGE-brush-pair-sync-10")]
    public void A_push_landing_after_the_preview_was_taken_down_deletes_its_own_file()
    {
        // Applied or not, once End has run and nothing redirects to a preview, the late file goes.
        Assert.Equal(LiveBrushPreview.LateFile.Delete, LiveBrushPreview.LateFileFate(applied: false, takenDown: true, holdingCount: 0));
        Assert.Equal(LiveBrushPreview.LateFile.Delete, LiveBrushPreview.LateFileFate(applied: true, takenDown: true, holdingCount: 0));
    }

    [Fact]
    [Trait("Plan", "EDGE-brush-pair-sync-10")]
    public void A_late_file_a_stuck_collection_may_still_draw_is_kept()
    {
        Assert.Equal(LiveBrushPreview.LateFile.Keep, LiveBrushPreview.LateFileFate(applied: false, takenDown: true, holdingCount: 1));
        // Applied, then an End a collection would not let go of: that collection may be drawing this very file.
        Assert.Equal(LiveBrushPreview.LateFile.Keep, LiveBrushPreview.LateFileFate(applied: true, takenDown: true, holdingCount: 1));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-1")]
    public void A_preview_that_landed_is_kept_and_a_refused_one_warned_about()
    {
        Assert.Equal(LiveBrushPreview.LateFile.Keep, LiveBrushPreview.LateFileFate(applied: true, takenDown: false, holdingCount: 1));
        Assert.Equal(LiveBrushPreview.LateFile.WarnRefused, LiveBrushPreview.LateFileFate(applied: false, takenDown: false, holdingCount: 1));
        // The very first push refused, nothing held yet: still said.
        Assert.Equal(LiveBrushPreview.LateFile.WarnRefused, LiveBrushPreview.LateFileFate(applied: false, takenDown: false, holdingCount: 0));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    public void A_file_dropped_after_the_preview_was_taken_down_leaves_the_list()
    {
        var kept = new LiveBrushPreview.PreviewFiles(keep: 4);
        kept.Add("late", shared: true);
        kept.Remove("LATE");
        Assert.False(kept.Contains("late"));
        Assert.Empty(kept.TakeAll());
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-12")]
    public void The_shared_folder_is_recognised_once_the_managed_mod_is_known()
    {
        LiveBrushPreview.RememberManagedRoot(@"C:\Penumbra\Proteus");
        Assert.True(LiveBrushPreview.IsPreviewFile("c:/penumbra/proteus/livepreview/9-ff-000001.mdl"));
        LiveBrushPreview.RememberManagedRoot(null);   // unknown later: what was learnt stays
        Assert.True(LiveBrushPreview.IsPreviewFile(@"C:\Penumbra\Proteus\LivePreview\9-ff-000001.mdl"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    [Trait("Plan", "EDGE-brush-pair-sync-9")]
    public void Private_previews_never_push_a_shared_one_out()
    {
        var kept = new LiveBrushPreview.PreviewFiles(keep: 4);
        Assert.Empty(kept.Add("shared-1", shared: true));

        var evicted = Enumerable.Range(1, 6).SelectMany(i => kept.Add($"private-{i}", shared: false)).ToList();

        Assert.Equal(["private-1", "private-2"], evicted);
        Assert.True(kept.Contains("shared-1"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    public void A_reused_shared_file_moves_to_the_back_rather_than_being_kept_twice()
    {
        var kept = new LiveBrushPreview.PreviewFiles(keep: 2);
        kept.Add("s1", shared: true);
        kept.Add("s2", shared: true);
        Assert.Empty(kept.Add("s1", shared: true));   // reused: now the newest
        Assert.Equal(["s2"], kept.Add("s3", shared: true));
        Assert.True(kept.Contains("s1"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    [Trait("Plan", "EDGE-brush-pair-sync-10")]
    public void Taking_everything_returns_both_kinds_and_empties_the_list()
    {
        var kept = new LiveBrushPreview.PreviewFiles(keep: 4);
        kept.Add("p", shared: false);
        kept.Add("s", shared: true);

        Assert.Equal(["p", "s"], kept.TakeAll().OrderBy(x => x).ToList());
        Assert.Empty(kept.TakeAll());
        Assert.False(kept.Contains("s"));
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    [Trait("Plan", "EDGE-brush-pair-sync-11")]
    public void Leftovers_of_dead_sessions_and_earlier_loads_are_swept_and_nothing_else()
    {
        const int own = 100, live = 200;
        string[] files =
        [
            @"x\100-aaaa-000001.mdl",   // this load: in use
            @"x\100-bbbb-000004.mdl",   // an earlier load of the plugin in this game: stale
            @"x\200-cccc-000002.mdl",   // another running game sharing the mods folder: in use
            @"x\300-dddd-000003.mdl",   // a crashed game: stale
            @"x\notes.mdl",             // not ours to judge
        ];

        var swept = LiveBrushPreview.StaleShared(files, own, "aaaa", pid => pid == own || pid == live).ToList();

        Assert.Equal([@"x\100-bbbb-000004.mdl", @"x\300-dddd-000003.mdl"], swept);
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-4")]
    [Trait("Plan", "EDGE-brush-pair-sync-12")]
    [Trait("Plan", "EDGE-brush-pair-sync-13")]
    public void Both_kinds_of_preview_are_recognised_on_the_character_and_nothing_else()
    {
        const string managed = @"C:\Penumbra\Proteus";
        var temp = Path.Combine(Path.GetTempPath(), "proteus-live-brush", "1-ab-000001.mdl");

        Assert.True(LiveBrushPreview.IsPreviewFile(temp, managed));
        Assert.True(LiveBrushPreview.IsPreviewFile(@"C:\Penumbra\Proteus\LivePreview\1-ab-000002.mdl", managed));
        // As LiveCharacter.FilePath hands it over from the resource name.
        Assert.True(LiveBrushPreview.IsPreviewFile("c:/penumbra/proteus/livepreview/1-ab-000002.mdl", managed));
        Assert.False(LiveBrushPreview.IsPreviewFile(@"C:\Penumbra\Old Thing\chara\top.mdl", managed));
        Assert.False(LiveBrushPreview.IsPreviewFile(@"C:\Penumbra\Proteus\models\x.mdl", managed));
        Assert.False(LiveBrushPreview.IsPreviewFile(@"C:\Penumbra\Proteus\models\upstream\y.mdl", managed));
        Assert.False(LiveBrushPreview.IsPreviewFile(@"C:\Penumbra\Proteus\LivePreview\1-ab-000002.mdl", null));
    }

    [Fact]
    public void A_private_preview_is_written_as_a_temporary_file()
    {
        var path = Path.Combine(tmp, "1-ab-000005.mdl");
        File.WriteAllBytes(path, [7, 7, 7, 7, 7]);   // a longer leftover under the same name is replaced, not kept

        LiveBrushPreview.WriteTemporary(path, [1, 2, 3]);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Temporary));
    }

    [Fact]
    public void A_temporary_file_that_cannot_be_created_says_why()
    {
        var ex = Assert.ThrowsAny<IOException>(() =>
            LiveBrushPreview.WriteTemporary(Path.Combine(tmp, "missing", "1-ab-000006.mdl"), [1]));

        Assert.Equal(unchecked((int)0x80070003), ex.HResult);   // ERROR_PATH_NOT_FOUND
        Assert.Contains(Marshal.GetPInvokeErrorMessage(3), ex.Message);
    }

    [Fact]
    [Trait("Plan", "AC-brush-pair-sync-5")]
    public void Preview_names_carry_the_process_and_the_load()
        => Assert.Equal("42-abcd-000007.mdl", LiveBrushPreview.FileName(42, "abcd", 7));
}
