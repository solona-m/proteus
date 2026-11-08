using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The bounded move retry of an output file into place (#91), and what counts as an output write failure. The held-file
/// tests need Windows sharing semantics (Linux CI does not block a replace on an open handle).
/// </summary>
public class TextureLoaderMoveRetryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "proteus-move-retry", Guid.NewGuid().ToString("N"));

    public TextureLoaderMoveRetryTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    private string Tmp(string path, byte[] bytes)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        return tmp;
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-4")]
    [Trait("Plan", "EDGE-issue-sweep-d-21")]
    public async Task A_briefly_held_destination_is_written_after_retries()
    {
        // [AC-issue-sweep-d-4] [EDGE-issue-sweep-d-21]
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(dir, "ss_1_norm.tex");
        File.WriteAllBytes(path, [1, 2, 3]);
        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Released on a thread of its own: a thread-pool task could start late under the parallel suite's load and miss
        // the retry budget (seen once: the release landed after five retries' ~310 ms). Eight retries give ~2.5 s.
        var released = new TaskCompletionSource();
        var release = new Thread(() => { Thread.Sleep(60); hold.Dispose(); released.SetResult(); }) { IsBackground = true };
        release.Start();
        var tmp = Tmp(path, [9, 9, 9, 9]);

        int retries = TextureLoader.MoveIntoPlace(tmp, path, 8, 10);
        await released.Task;

        Assert.True(retries >= 1);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-5")]
    [Trait("Plan", "EDGE-issue-sweep-d-21")]
    public void A_destination_held_past_the_budget_throws_OutputWriteException_and_leaves_no_temp()
    {
        // [EDGE-issue-sweep-d-5] [EDGE-issue-sweep-d-21] The old file stays intact; the temp is gone.
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(dir, "ss_1_mask.tex");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var tmp = Tmp(path, [9]);

        var ex = Assert.Throws<OutputWriteException>(() => TextureLoader.MoveIntoPlace(tmp, path, 2, 5));
        Assert.Equal(path, ex.Path);
        hold.Dispose();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-7")]
    [Trait("Plan", "EDGE-issue-sweep-d-21")]
    public void WriteIfChanged_on_a_held_destination_throws_OutputWriteException()
    {
        // [EDGE-issue-sweep-d-7] [EDGE-issue-sweep-d-21] A .mtrl/.mdl write that exhausts its retries is an output write failure.
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(dir, "ss_1.mtrl");
        File.WriteAllBytes(path, [1]);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<OutputWriteException>(() => SecondSkinService.WriteIfChanged(path, [2, 2]));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-7")]
    public void IsTransient_excludes_not_found_and_path_too_long()
    {
        // [EDGE-issue-sweep-d-7] Only a failure a retry could fix counts.
        Assert.True(OutputWriteException.IsTransient(new IOException("sharing violation")));
        Assert.True(OutputWriteException.IsTransient(new UnauthorizedAccessException()));
        Assert.False(OutputWriteException.IsTransient(new FileNotFoundException()));
        Assert.False(OutputWriteException.IsTransient(new DirectoryNotFoundException()));
        Assert.False(OutputWriteException.IsTransient(new PathTooLongException()));
        Assert.False(OutputWriteException.IsTransient(new InvalidOperationException()));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-22")]
    public void CopyPackFile_with_a_missing_source_throws_FileNotFound_not_OutputWrite()
    {
        // [EDGE-issue-sweep-d-22] A missing pack source is the source's fault: never counted, never retried.
        var ex = Record.Exception(() => SecondSkinService.CopyPackFile(new Dictionary<string, ulong>(),
            Path.Combine(dir, "missing.tex"), Path.Combine(dir, "ct_1_0.tex")));
        Assert.NotNull(ex);
        Assert.IsNotType<OutputWriteException>(ex);
        Assert.False(OutputWriteException.IsTransient(ex));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-22")]
    public void A_non_transient_move_failure_is_thrown_as_it_is_without_retrying()
    {
        // [EDGE-issue-sweep-d-22] A missing temp is not a held destination: no retry, never an OutputWriteException.
        var path = Path.Combine(dir, "ss_3_norm.tex");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<FileNotFoundException>(() => TextureLoader.MoveIntoPlace(Path.Combine(dir, "gone.tmp"), path, 5, 200));
        Assert.True(sw.ElapsedMilliseconds < 150, $"retried ({sw.ElapsedMilliseconds}ms)");
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-4")]
    public void A_free_destination_moves_at_once()
    {
        // [AC-issue-sweep-d-4]
        var path = Path.Combine(dir, "ss_2_id.tex");
        var tmp = Tmp(path, [7]);
        Assert.Equal(0, TextureLoader.MoveIntoPlace(tmp, path));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(path));
    }
}
